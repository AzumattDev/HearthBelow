using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace HearthBelow.VoxelMagic;

// Voxelized heightmap zone meshed with surface nets and shared borders for seamless joins.
public class VoxelZone
{
    public const int ChunkSize = 16;

    // each density sample is meters of rock above/below the surface (positive = solid), capped at +/- this
    public const float DensityRange = 8f;

    // fills cap out here so a later carve can still chew back through
    private const float FillDensityCap = 4f;

    private const float GridHeadroom = 6f;

    // vertical span flatten/smooth bother to touch around the aim point
    private const float PlaneOpBand = 4.5f;

    public Heightmap Hmap = null!;
    public GameObject? Root;
    public float[] Density = null!;
    public int NX, NY, NZ; // sample counts; NX == NZ == heightmap width + 3 (1 shared border sample on every side)
    public Vector3 Origin; // world position of sample (0,0,0)
    public float HorizontalSpacing = 1f;
    public const float VerticalSpacing = 1f;

    public bool NeighborVoxPX, NeighborVoxNX, NeighborVoxPZ, NeighborVoxNZ;

    public readonly List<CarveOp> Ops = [];
    public readonly HashSet<int> AppliedIds = [];

    // colliders live on the Heightmap's own GameObject, NOT the chunk child: half the damn
    // game does hitInfo.collider.GetComponent<Heightmap>() and dereferences without checking
    private class Chunk
    {
        public GameObject Go = null!;
        public MeshCollider Collider = null!;
    }

    private readonly HashSet<Vector3Int> _dirty = [];
    private readonly Dictionary<Vector3Int, Chunk> _chunks = new();
    private readonly HashSet<Vector3Int> _carvedChunks = [];
    private readonly List<Vector3Int> _pumpList = [];
    private static readonly Stopwatch PumpWatch = new();
    private const long PumpBudgetMs = 5;
    private Material? _material;
    private int _layer;
    private float _zoneMinX, _zoneMinZ, _zoneSize;
    private int _chunksX, _chunksY, _chunksZ;
    private float[] _colMinH = null!, _colMaxH = null!;
    private float[] _heights = null!;
    private int _heightsChecksum;
    private bool _swapPending;
    private readonly Stopwatch _swapWatch = new();
    private long _meshTicks;
    private int _meshFrames;
    private readonly List<Rigidbody> _frozenBodies = [];

    public float D(int gx, int gy, int gz) => Density[(gy * NZ + gz) * NX + gx];

    public bool Build(Heightmap hmap)
    {
        if (hmap == null || hmap.m_heights == null || hmap.m_heights.Count == 0)
            return false;
        Hmap = hmap;
        if (!BuildData())
            return false;
        Root = new GameObject("HearthBelow_Voxels");
        Root.transform.SetParent(hmap.transform, false);
        Root.layer = _layer;
        RefreshNeighborFlags();
        // Pump() meshes over several frames; the vanilla heightmap stays active until every
        // chunk is ready, then everything swaps at once
        MarkAllDirty();
        _swapPending = true;
        _swapWatch.Restart();
        _meshTicks = 0;
        _meshFrames = 0;
        FreezeLooseBodies();
        return true;
    }

    public bool IsActive => Root != null && !_swapPending;

    public void RefreshNeighborFlags()
    {
        Vector3 c = Hmap.transform.position;
        NeighborVoxPX = VoxelWorld.IsVoxelizedAt(c + new Vector3(_zoneSize, 0f, 0f));
        NeighborVoxNX = VoxelWorld.IsVoxelizedAt(c - new Vector3(_zoneSize, 0f, 0f));
        NeighborVoxPZ = VoxelWorld.IsVoxelizedAt(c + new Vector3(0f, 0f, _zoneSize));
        NeighborVoxNZ = VoxelWorld.IsVoxelizedAt(c - new Vector3(0f, 0f, _zoneSize));
    }

    public void OnNeighborChanged()
    {
        bool px = NeighborVoxPX, nx = NeighborVoxNX, pz = NeighborVoxPZ, nz = NeighborVoxNZ;
        RefreshNeighborFlags();
        for (int ky = 0; ky < _chunksY; ++ky)
        {
            for (int k = 0; k < _chunksX; ++k)
            {
                if (px != NeighborVoxPX) _dirty.Add(new Vector3Int(_chunksX - 1, ky, k));
                if (nx != NeighborVoxNX) _dirty.Add(new Vector3Int(0, ky, k));
                if (pz != NeighborVoxPZ) _dirty.Add(new Vector3Int(k, ky, _chunksZ - 1));
                if (nz != NeighborVoxNZ) _dirty.Add(new Vector3Int(k, ky, 0));
            }
        }

        RemeshDirty();
    }

    private bool BuildData()
    {
        int n = Hmap.m_width + 1;
        HorizontalSpacing = Hmap.m_scale;
        NX = NZ = n + 2;
        _zoneSize = Hmap.m_width * HorizontalSpacing;
        Vector3 hp = Hmap.transform.position;
        _zoneMinX = hp.x - _zoneSize * 0.5f;
        _zoneMinZ = hp.z - _zoneSize * 0.5f;

        _heights = new float[NX * NZ];
        float min = float.MaxValue, max = float.MinValue;
        for (int gz = 0; gz < NZ; ++gz)
        {
            for (int gx = 0; gx < NX; ++gx)
            {
                float h = SampleWorldHeight(gx, gz, n, hp);
                _heights[gz * NX + gx] = h;
                if (h < min) min = h;
                if (h > max) max = h;
            }
        }

        float minH = Mathf.Floor(min) - HearthBelowPlugin.CaveDepth.Value;
        float maxH = Mathf.Ceil(max) + GridHeadroom;
        NY = Mathf.CeilToInt((maxH - minH) / VerticalSpacing) + 1;
        Origin = new Vector3(_zoneMinX - HorizontalSpacing, minH, _zoneMinZ - HorizontalSpacing);
        Density = new float[NX * NY * NZ];
        for (int gy = 0; gy < NY; ++gy)
        {
            float y = minH + gy * VerticalSpacing;
            for (int gz = 0; gz < NZ; ++gz)
            {
                int row = gz * NX;
                int drow = (gy * NZ + gz) * NX;
                for (int gx = 0; gx < NX; ++gx)
                    Density[drow + gx] = Mathf.Clamp(_heights[row + gx] - y, -DensityRange, DensityRange);
            }
        }

        _material = Hmap.m_materialInstance;
        _layer = Hmap.gameObject.layer;
        _chunksX = Mathf.CeilToInt((NX - 1) / (float)ChunkSize);
        _chunksY = Mathf.CeilToInt((NY - 1) / (float)ChunkSize);
        _chunksZ = Mathf.CeilToInt((NZ - 1) / (float)ChunkSize);
        _heightsChecksum = ComputeHeightsChecksum();

        _colMinH = new float[_chunksX * _chunksZ];
        _colMaxH = new float[_chunksX * _chunksZ];
        UpdateColumnRanges(0, _chunksX - 1, 0, _chunksZ - 1);
        return true;
    }

    private float SampleWorldHeight(int gx, int gz, int n, Vector3 hp)
    {
        int wx = gx - 1, wz = gz - 1;
        if (wx >= 0 && wx < n && wz >= 0 && wz < n)
            return Hmap.m_heights[wz * n + wx] + hp.y;
        // border sample that lives inside a neighbor zone; fall back to this zone's own edge if it isn't loaded
        Vector3 wp = new(_zoneMinX + wx * HorizontalSpacing, 0f, _zoneMinZ + wz * HorizontalSpacing);
        if (Heightmap.GetHeight(wp, out float h))
            return h;
        return Hmap.m_heights[Mathf.Clamp(wz, 0, n - 1) * n + Mathf.Clamp(wx, 0, n - 1)] + hp.y;
    }

    // no carve touched this cell - at zone borders the mesher can snap its verts onto the
    // real heightmap surface, carved cells it can't
    public bool IsPristineCell(int cx, int cy, int cz)
    {
        for (int dz = 0; dz <= 1; ++dz)
        {
            for (int dx = 0; dx <= 1; ++dx)
            {
                float h = _heights[(cz + dz) * NX + cx + dx];
                for (int dy = 0; dy <= 1; ++dy)
                {
                    float expected = Mathf.Clamp(h - (Origin.y + (cy + dy) * VerticalSpacing), -DensityRange, DensityRange);
                    if (D(cx + dx, cy + dy, cz + dz) != expected)
                        return false;
                }
            }
        }

        return true;
    }

    private void UpdateColumnRanges(int kMinX, int kMaxX, int kMinZ, int kMaxZ)
    {
        for (int kx = kMinX; kx <= kMaxX; ++kx)
        {
            for (int kz = kMinZ; kz <= kMaxZ; ++kz)
            {
                int x0 = Mathf.Max(0, kx * ChunkSize - 1), x1 = Mathf.Min(NX - 1, (kx + 1) * ChunkSize + 1);
                int z0 = Mathf.Max(0, kz * ChunkSize - 1), z1 = Mathf.Min(NZ - 1, (kz + 1) * ChunkSize + 1);
                float lo = float.MaxValue, hi = float.MinValue;
                for (int gz = z0; gz <= z1; ++gz)
                {
                    for (int gx = x0; gx <= x1; ++gx)
                    {
                        float h = _heights[gz * NX + gx];
                        if (h < lo) lo = h;
                        if (h > hi) hi = h;
                    }
                }

                _colMinH[kx * _chunksZ + kz] = lo;
                _colMaxH[kx * _chunksZ + kz] = hi;
            }
        }
    }

    private int ComputeHeightsChecksum()
    {
        int hash = 17;
        List<float> heights = Hmap.m_heights;
        for (int i = 0; i < heights.Count; ++i)
            hash = hash * 31 + heights[i].GetHashCode();
        return hash;
    }

    public void Rebuild()
    {
        if (Hmap == null || Hmap.m_heights == null || Hmap.m_heights.Count == 0)
            return;
        // every ZDO write (my own carve saves included) pokes the heightmap and lands here,
        // so only rebuild when the heights actually changed
        int checksum = ComputeHeightsChecksum();
        if (checksum == _heightsChecksum)
            return;

        if (!_swapPending && Root != null && TryIncrementalHeightsUpdate())
        {
            _heightsChecksum = checksum;
            return;
        }

        foreach (Chunk chunk in _chunks.Values)
            DestroyChunk(chunk);
        _chunks.Clear();
        _dirty.Clear();
        _carvedChunks.Clear();
        if (!BuildData())
            return;
        foreach (CarveOp op in Ops)
            CarveDensity(op, false);
        MarkAllDirty();
        if (!_swapPending)
        {
            RemeshNow();
            // a raise that outgrew the grid lands here - pop out anyone the rebuilt surface swallowed
            VoxelWorld.EjectBuried(Hmap.transform.position, _zoneSize);
        }
    }

    // patch only the columns whose heights changed; false = outside the grid, full rebuild
    private bool TryIncrementalHeightsUpdate()
    {
        int n = Hmap.m_width + 1;
        Vector3 hp = Hmap.transform.position;
        int minGX = int.MaxValue, maxGX = int.MinValue, minGZ = int.MaxValue, maxGZ = int.MinValue;
        float gridTop = Origin.y + (NY - 1) * VerticalSpacing;
        float gridBottomSafe = Origin.y + 3f;
        float maxChangedH = float.MinValue;
        for (int gz = 0; gz < NZ; ++gz)
        {
            for (int gx = 0; gx < NX; ++gx)
            {
                float h = SampleWorldHeight(gx, gz, n, hp);
                int i = gz * NX + gx;
                if (Mathf.Approximately(h, _heights[i]))
                    continue;
                if (h + GridHeadroom > gridTop || h < gridBottomSafe)
                    return false;
                _heights[i] = h;
                if (h > maxChangedH) maxChangedH = h;
                if (gx < minGX) minGX = gx;
                if (gx > maxGX) maxGX = gx;
                if (gz < minGZ) minGZ = gz;
                if (gz > maxGZ) maxGZ = gz;
            }
        }

        if (maxGX < minGX)
            return true; // paint only - shared material updates by itself

        for (int gy = 0; gy < NY; ++gy)
        {
            float y = Origin.y + gy * VerticalSpacing;
            for (int gz = minGZ; gz <= maxGZ; ++gz)
            {
                int row = (gy * NZ + gz) * NX;
                int hrow = gz * NX;
                for (int gx = minGX; gx <= maxGX; ++gx)
                    Density[row + gx] = Mathf.Clamp(_heights[hrow + gx] - y, -DensityRange, DensityRange);
            }
        }

        foreach (CarveOp op in Ops)
            ApplyOpDensity(op, minGX, maxGX, minGZ, maxGZ, false, out _, out _, out _, out _, out _, out _);

        UpdateColumnRanges(Mathf.Max(0, (minGX - 2) / ChunkSize), Mathf.Min(_chunksX - 1, (maxGX + 2) / ChunkSize), Mathf.Max(0, (minGZ - 2) / ChunkSize), Mathf.Min(_chunksZ - 1, (maxGZ + 2) / ChunkSize));
        MarkSampleRange(_dirty, minGX, 0, minGZ, maxGX, NY - 1, maxGZ);
        RemeshDirty();
        // vanilla raise lets the player ride the heightmap collider up; here the voxel mesh
        // just rebuilds around them, so pop out anyone the new surface swallowed
        Vector3 center = new(Origin.x + (minGX + maxGX) * 0.5f * HorizontalSpacing, maxChangedH, Origin.z + (minGZ + maxGZ) * 0.5f * HorizontalSpacing);
        float ex = (maxGX - minGX) * HorizontalSpacing, ez = (maxGZ - minGZ) * HorizontalSpacing;
        VoxelWorld.EjectBuried(center, 0.5f * Mathf.Sqrt(ex * ex + ez * ez) + 1f);
        return true;
    }

    public bool ApplyOp(CarveOp op)
    {
        if (!AppliedIds.Add(op.Id))
            return false;
        Ops.Add(op);
        CarveDensity(op, true);
        return true;
    }

    public bool WouldChange(CarveOp op)
    {
        return ApplyOpDensity(op, 0, NX - 1, 0, NZ - 1, true, out _, out _, out _, out _, out _, out _);
    }

    private void CarveDensity(CarveOp op, bool markDirty)
    {
        ApplyOpDensity(op, 0, NX - 1, 0, NZ - 1, false,
            out int minX, out int minY, out int minZ, out int maxX, out int maxY, out int maxZ);
        if (minX > maxX)
            return;
        // modified chunks can never be skipped as uniform, even on rebuild replay
        MarkSampleRange(_carvedChunks, minX, minY, minZ, maxX, maxY, maxZ);
        if (markDirty)
            MarkSampleRange(_dirty, minX, minY, minZ, maxX, maxY, maxZ);
    }

    // Writes are restricted to columns [colMin..colMax] (incremental rebuilds use that).
    // Returns true when the op visibly changes the surface.
    private bool ApplyOpDensity(CarveOp op, int colMinX, int colMaxX, int colMinZ, int colMaxZ, bool dryRun,
        out int minX, out int minY, out int minZ, out int maxX, out int maxY, out int maxZ)
    {
        float r = op.Radius;
        float extentXZ = r;
        // plane ops need PrepareFloorColumns for their per-column targets - Raise included,
        // or it reads the previous op's stale targets and silently no-ops
        bool planeOp = op.Type is (byte)VoxelOpType.Flatten or (byte)VoxelOpType.Smooth or (byte)VoxelOpType.Raise;
        float extentY = planeOp ? PlaneOpBand : r;
        if (op.Type == (byte)VoxelOpType.Raise)
            extentY = Mathf.Max(extentY, op.Depth + 1.5f); // modded pieces can raise more per swing than the band
        Vector3 scoopDir = Vector3.down, scoopU = Vector3.right, scoopV = Vector3.forward;
        if (op.Type == (byte)VoxelOpType.Scoop)
        {
            scoopDir = op.Dir.sqrMagnitude > 0.01f ? op.Dir.normalized : Vector3.down;
            scoopU = Vector3.Cross(scoopDir, Vector3.up);
            if (scoopU.sqrMagnitude < 1e-4f)
                scoopU = Vector3.Cross(scoopDir, Vector3.forward);
            scoopU.Normalize();
            scoopV = Vector3.Cross(scoopDir, scoopU);
            extentXZ = extentY = Mathf.Max(r, op.Depth);
        }

        Vector3 local = op.Point - Origin;
        minX = Mathf.Max(Mathf.Max(colMinX, 0), Mathf.FloorToInt((local.x - extentXZ) / HorizontalSpacing) - 1);
        maxX = Mathf.Min(Mathf.Min(colMaxX, NX - 1), Mathf.CeilToInt((local.x + extentXZ) / HorizontalSpacing) + 1);
        minY = Mathf.Max(op.Type == (byte)VoxelOpType.Fill ? 0 : 2, Mathf.FloorToInt((local.y - extentY) / VerticalSpacing) - 1);
        maxY = Mathf.Min(NY - 1, Mathf.CeilToInt((local.y + extentY) / VerticalSpacing) + 1);
        minZ = Mathf.Max(Mathf.Max(colMinZ, 0), Mathf.FloorToInt((local.z - extentXZ) / HorizontalSpacing) - 1);
        maxZ = Mathf.Min(Mathf.Min(colMaxZ, NZ - 1), Mathf.CeilToInt((local.z + extentXZ) / HorizontalSpacing) + 1);
        if (minX > maxX || minY > maxY || minZ > maxZ)
        {
            maxX = minX - 1;
            return false;
        }

        int smoothW = maxX - minX + 1;
        if (planeOp)
            PrepareFloorColumns(op, minX, maxX, minZ, maxZ, minY, maxY);

        bool isCarve = op.Type is (byte)VoxelOpType.Carve or (byte)VoxelOpType.Scoop;
        bool flipped = false;
        bool anyWrite = false;
        for (int gy = minY; gy <= maxY; ++gy)
        {
            float y = Origin.y + gy * VerticalSpacing;
            for (int gz = minZ; gz <= maxZ; ++gz)
            {
                float z = Origin.z + gz * HorizontalSpacing;
                int row = (gy * NZ + gz) * NX;
                for (int gx = minX; gx <= maxX; ++gx)
                {
                    float x = Origin.x + gx * HorizontalSpacing;
                    int idx = row + gx;
                    float d = Density[idx];
                    float newD = d;
                    switch ((VoxelOpType)op.Type)
                    {
                        case VoxelOpType.Carve:
                        {
                            if (y < op.FloorY)
                                break;
                            float sd = ShapeDistance(op, x, y, z) - r;
                            if (sd < d) newD = sd;
                            break;
                        }
                        case VoxelOpType.Fill:
                        {
                            float fill = r - ShapeDistance(op, x, y, z);
                            if (fill > d) newD = fill;
                            break;
                        }
                        case VoxelOpType.Flatten:
                        {
                            int col = (gz - minZ) * smoothW + (gx - minX);
                            if (float.IsNaN(_smoothTargets[col]))
                                break; // outside the radius
                            float t = op.Point.y - y; // positive below the target plane
                            if (y <= op.Point.y)
                            {
                                float v = Mathf.Min(t, FillDensityCap);
                                if (v > d) newD = v;
                            }
                            else if (y <= _smoothFloors[col] + 1f && t < d)
                            {
                                // only cut between the plane and the old floor (NaN floor
                                // compares false) - leveling must not eat low ceilings
                                newD = t;
                            }

                            break;
                        }
                        case VoxelOpType.Raise:
                        {
                            // vanilla-style raise: targets computed per column, fill only
                            float target = _smoothTargets[(gz - minZ) * smoothW + (gx - minX)];
                            if (float.IsNaN(target) || y > target)
                                break;
                            float v = Mathf.Min(target - y, FillDensityCap);
                            if (v > d) newD = v;
                            break;
                        }
                        case VoxelOpType.Scoop:
                        {
                            if (y < op.FloorY)
                                break;
                            Vector3 rel = new Vector3(x, y, z) - op.Point;
                            float t = Vector3.Dot(rel, scoopDir);
                            Vector3 perp = rel - t * scoopDir;
                            float sd;
                            // scale by the dominant axis, NOT min(depth, r) - that bound thinned
                            // the rock in a wide shell and spam-dug shafts eventually tore open
                            if (op.Shape == (byte)VoxelOpShape.Cube)
                            {
                                float u = Mathf.Abs(Vector3.Dot(perp, scoopU));
                                float v = Mathf.Abs(Vector3.Dot(perp, scoopV));
                                float along = Mathf.Abs(t) / op.Depth;
                                float across = Mathf.Max(u, v) / r;
                                sd = along >= across ? (along - 1f) * op.Depth : (across - 1f) * r;
                            }
                            else
                            {
                                float along = t / op.Depth;
                                float across = perp.magnitude / r;
                                float k = Mathf.Sqrt(along * along + across * across);
                                sd = k > 1e-4f ? (k - 1f) * rel.magnitude / k : -Mathf.Min(op.Depth, r);
                            }

                            if (sd < d) newD = sd;
                            break;
                        }
                        case VoxelOpType.Smooth:
                        {
                            int col = (gz - minZ) * smoothW + (gx - minX);
                            float target = _smoothTargets[col];
                            if (float.IsNaN(target))
                                break;
                            if (y <= target)
                            {
                                float v = Mathf.Min(target - y, FillDensityCap);
                                if (v > d) newD = v;
                            }
                            else if (y <= _smoothFloors[col] + 1f && target - y < d)
                            {
                                // only clear between old and new floor - never carve into
                                // ceilings the way flatten does
                                newD = target - y;
                            }

                            break;
                        }
                    }

                    if (newD == d)
                        continue;
                    anyWrite = true;
                    if (d > 0f != newD > 0f)
                        flipped = true;
                    if (dryRun)
                    {
                        // a carve only "changed" something if a sample flipped to air; fills
                        // count any write
                        if (!isCarve || flipped)
                            return true;
                        continue;
                    }

                    Density[idx] = newD;
                }
            }
        }

        return isCarve ? flipped : anyWrite;
    }

    private static float[] _smoothTargets = [];
    private static float[] _smoothFloors = [];

    // Per column: floor crossing nearest the op plane, blended toward it with smoothstep
    // falloff. NaN = outside radius or no floor - keeps smooth the hell off tunnel walls.
    private void PrepareFloorColumns(CarveOp op, int minX, int maxX, int minZ, int maxZ, int minY, int maxY)
    {
        int w = maxX - minX + 1;
        int needed = w * (maxZ - minZ + 1);
        if (_smoothTargets.Length < needed)
        {
            _smoothTargets = new float[needed];
            _smoothFloors = new float[needed];
        }

        float r = op.Radius;
        bool square = op.Shape == (byte)VoxelOpShape.Cube;
        int centerX = Mathf.FloorToInt((op.Point.x - Origin.x) / HorizontalSpacing + 0.5f);
        int centerZ = Mathf.FloorToInt((op.Point.z - Origin.z) / HorizontalSpacing + 0.5f);
        float f = r / HorizontalSpacing;
        int reach = Mathf.CeilToInt(f);
        for (int gz = minZ; gz <= maxZ; ++gz)
        {
            for (int gx = minX; gx <= maxX; ++gx)
            {
                int col = (gz - minZ) * w + (gx - minX);
                int di = gx - centerX, dj = gz - centerZ;
                float n;
                if (square)
                {
                    if (Mathf.Abs(di) > reach || Mathf.Abs(dj) > reach)
                    {
                        _smoothTargets[col] = float.NaN;
                        _smoothFloors[col] = float.NaN;
                        continue;
                    }

                    n = 0f;
                }
                else
                {
                    float dist = Mathf.Sqrt(di * di + dj * dj);
                    if (dist > f)
                    {
                        _smoothTargets[col] = float.NaN;
                        _smoothFloors[col] = float.NaN;
                        continue;
                    }

                    n = dist / f;
                }

                float floor = float.NaN, bestDist = float.MaxValue;
                for (int gy = minY; gy < maxY; ++gy)
                {
                    float d0 = D(gx, gy, gz), d1 = D(gx, gy + 1, gz);
                    if (!(d0 > 0f) || !(d1 <= 0f)) continue;
                    float cross = Origin.y + (gy + d0 / (d0 - d1)) * VerticalSpacing;
                    float dist = Mathf.Abs(cross - op.Point.y);
                    if (!(dist < bestDist)) continue;
                    bestDist = dist;
                    floor = cross;
                }

                _smoothFloors[col] = floor;
                switch ((VoxelOpType)op.Type)
                {
                    case VoxelOpType.Flatten:
                        _smoothTargets[col] = op.Point.y;
                        break;
                    case VoxelOpType.Raise:
                    {
                        if (float.IsNaN(floor))
                        {
                            _smoothTargets[col] = float.NaN;
                            break;
                        }

                        float step = op.Depth * (square || op.Power <= 0f ? 1f : Mathf.Pow(1f - n, op.Power));
                        float target = op.Point.y + step;
                        _smoothTargets[col] = target < floor ? float.NaN : Mathf.Min(target, floor + step);
                        break;
                    }
                    default:
                    {
                        if (float.IsNaN(floor))
                        {
                            _smoothTargets[col] = float.NaN;
                            break;
                        }

                        float target = Mathf.Lerp(floor, op.Point.y, 1f - n * n * n);
                        _smoothTargets[col] = floor + Mathf.Clamp(target - floor, -1f, 1f);
                        break;
                    }
                }
            }
        }
    }

    private static float ShapeDistance(CarveOp op, float x, float y, float z)
    {
        float dx = x - op.Point.x, dy = y - op.Point.y, dz = z - op.Point.z;
        return op.Shape == (byte)VoxelOpShape.Cube ? Mathf.Max(Mathf.Abs(dx), Mathf.Max(Mathf.Abs(dy), Mathf.Abs(dz))) : Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private void MarkSampleRange(HashSet<Vector3Int> target, int minX, int minY, int minZ, int maxX, int maxY, int maxZ)
    {
        int kMinX = Mathf.Max(0, Mathf.CeilToInt((minX - ChunkSize) / (float)ChunkSize));
        int kMaxX = Mathf.Min(_chunksX - 1, Mathf.FloorToInt((maxX + 1) / (float)ChunkSize));
        int kMinY = Mathf.Max(0, Mathf.CeilToInt((minY - ChunkSize) / (float)ChunkSize));
        int kMaxY = Mathf.Min(_chunksY - 1, Mathf.FloorToInt((maxY + 1) / (float)ChunkSize));
        int kMinZ = Mathf.Max(0, Mathf.CeilToInt((minZ - ChunkSize) / (float)ChunkSize));
        int kMaxZ = Mathf.Min(_chunksZ - 1, Mathf.FloorToInt((maxZ + 1) / (float)ChunkSize));
        for (int ky = kMinY; ky <= kMaxY; ++ky)
        for (int kz = kMinZ; kz <= kMaxZ; ++kz)
        for (int kx = kMinX; kx <= kMaxX; ++kx)
            target.Add(new Vector3Int(kx, ky, kz));
    }

    private void MarkAllDirty()
    {
        for (int ky = 0; ky < _chunksY; ++ky)
        for (int kz = 0; kz < _chunksZ; ++kz)
        for (int kx = 0; kx < _chunksX; ++kx)
            _dirty.Add(new Vector3Int(kx, ky, kz));
    }

    public void RemeshDirty()
    {
        if (_swapPending)
            return; // initial build in progress, the pump will pick these up
        RemeshNow();
    }

    public void ForceRemeshAll()
    {
        MarkAllDirty();
        RemeshDirty();
    }

    private void RemeshNow()
    {
        if (Root == null)
            return;
        foreach (Vector3Int key in _dirty)
            BuildChunkMesh(key);
        _dirty.Clear();
    }

    public void Pump()
    {
        if (Root == null)
            return;
        if (_dirty.Count > 0)
        {
            PumpWatch.Restart();
            _pumpList.Clear();
            _pumpList.AddRange(_dirty);
            foreach (Vector3Int key in _pumpList)
            {
                BuildChunkMesh(key);
                _dirty.Remove(key);
                if (PumpWatch.ElapsedMilliseconds >= PumpBudgetMs)
                    break;
            }

            if (_swapPending)
            {
                _meshTicks += PumpWatch.ElapsedTicks;
                ++_meshFrames;
            }
        }

        if (!_swapPending || _dirty.Count != 0) return;
        _swapPending = false;
        if (Hmap.m_meshRenderer != null) Hmap.m_meshRenderer.enabled = false;
        if (Hmap.m_collider != null) Hmap.m_collider.enabled = false;
        UnfreezeLooseBodies();
        VoxelWorld.NotifyNeighbors(Hmap);
        DistantLod.RefreshAt(Hmap);
        HearthBelowPlugin.HearthBelowLogger.LogDebug($"Zone at {Hmap.transform.position} swapped to voxel: {_chunks.Count} chunk mesh(es), {_meshTicks * 1000 / Stopwatch.Frequency} ms meshing spread over {_meshFrames} frame(s), {_swapWatch.ElapsedMilliseconds} ms wall time");
    }

    private void BuildChunkMesh(Vector3Int key)
    {
        // owned edges: samples [1, N-2] horizontally (0 and N-1 belong to neighbors)
        Vector3Int sMin = new(Mathf.Max(key.x * ChunkSize, 1), key.y * ChunkSize, Mathf.Max(key.z * ChunkSize, 1));
        Vector3Int sMax = new(Mathf.Min((key.x + 1) * ChunkSize, NX - 1), Mathf.Min((key.y + 1) * ChunkSize, NY - 1), Mathf.Min((key.z + 1) * ChunkSize, NZ - 1));

        // uncarved chunks entirely below or above the base surface have no geometry
        if (!_carvedChunks.Contains(key))
        {
            float yLow = Origin.y + (key.y * ChunkSize - 2) * VerticalSpacing;
            float yHigh = Origin.y + ((key.y + 1) * ChunkSize + 2) * VerticalSpacing;
            int col = key.x * _chunksZ + key.z;
            if (yHigh < _colMinH[col] || yLow > _colMaxH[col])
            {
                if (!_chunks.TryGetValue(key, out Chunk? uniform)) return;
                DestroyChunk(uniform);
                _chunks.Remove(key);

                return;
            }
        }

        bool has = SurfaceNets.Build(this, sMin, sMax,
            out List<Vector3> verts, out List<Vector3> normals, out List<Color32> colors, out List<Vector2> uvs, out List<int> tris);

        _chunks.TryGetValue(key, out Chunk? existing);
        if (!has)
        {
            if (existing == null) return;
            DestroyChunk(existing);
            _chunks.Remove(key);

            return;
        }

        if (existing == null)
        {
            GameObject go = new($"chunk_{key.x}_{key.y}_{key.z}");
            go.layer = _layer;
            go.transform.SetParent(Root!.transform, false);
            go.AddComponent<MeshFilter>();
            MeshRenderer mr = go.AddComponent<MeshRenderer>();
            if (_material != null)
                mr.sharedMaterial = _material;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.TwoSided;
            existing = new Chunk { Go = go, Collider = Hmap.gameObject.AddComponent<MeshCollider>() };
            _chunks[key] = existing;
        }

        // heightmap-local space so the same mesh drives the renderer child AND the collider
        // on the heightmap's GameObject
        Vector3 offset = Origin - Hmap.transform.position;
        for (int i = 0; i < verts.Count; ++i)
            verts[i] += offset;

        MeshFilter mf = existing.Go.GetComponent<MeshFilter>();
        Mesh? old = mf.sharedMesh;
        Mesh mesh = new() { name = "hearthbelow_voxel_chunk" };
        mesh.SetVertices(verts);
        mesh.SetNormals(normals);
        mesh.SetColors(colors);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateBounds();
        mf.sharedMesh = mesh;
        existing.Collider.sharedMesh = mesh;
        if (old != null)
            Object.Destroy(old);
    }

    public bool SampleSolid(Vector3 world)
    {
        Vector3 l = world - Origin;
        float fx = Mathf.Clamp(l.x / HorizontalSpacing, 0f, NX - 1.0001f);
        float fy = Mathf.Clamp(l.y / VerticalSpacing, 0f, NY - 1.0001f);
        float fz = Mathf.Clamp(l.z / HorizontalSpacing, 0f, NZ - 1.0001f);
        int x0 = (int)fx, y0 = (int)fy, z0 = (int)fz;
        float tx = fx - x0, ty = fy - y0, tz = fz - z0;
        float d00 = Mathf.Lerp(D(x0, y0, z0), D(x0 + 1, y0, z0), tx);
        float d10 = Mathf.Lerp(D(x0, y0 + 1, z0), D(x0 + 1, y0 + 1, z0), tx);
        float d01 = Mathf.Lerp(D(x0, y0, z0 + 1), D(x0 + 1, y0, z0 + 1), tx);
        float d11 = Mathf.Lerp(D(x0, y0 + 1, z0 + 1), D(x0 + 1, y0 + 1, z0 + 1), tx);
        return Mathf.Lerp(Mathf.Lerp(d00, d10, ty), Mathf.Lerp(d01, d11, ty), tz) > 0f;
    }

    public void GetSurfaceAttributes(Vector3 world, out Vector2 uv, out Color32 color)
    {
        float u = Mathf.Clamp01((world.x - _zoneMinX) / _zoneSize);
        float v = Mathf.Clamp01((world.z - _zoneMinZ) / _zoneSize);
        uv = new Vector2(u, v);
        color = (Color32)Hmap.GetBiomeColor(DUtils.SmoothStep(0f, 1f, u), DUtils.SmoothStep(0f, 1f, v));
    }

    // No floor until the initial mesh is in - pin loose bodies kinematic so they don't fall out
    // the bottom of the grid. Characters excluded: Character_CustomFixedUpdate_Patch pins those.
    private void FreezeLooseBodies()
    {
        float gridH = (NY - 1) * VerticalSpacing;
        Vector3 center = new(_zoneMinX + _zoneSize * 0.5f, Origin.y + gridH * 0.5f, _zoneMinZ + _zoneSize * 0.5f);
        Vector3 half = new(_zoneSize * 0.5f, gridH * 0.5f, _zoneSize * 0.5f);
        foreach (Collider col in Physics.OverlapBox(center, half, Quaternion.identity, -1, QueryTriggerInteraction.Ignore))
        {
            Rigidbody body = col.attachedRigidbody;
            if (body == null || body.isKinematic || _frozenBodies.Contains(body))
                continue;
            if (body.GetComponent<Character>() != null)
                continue;
            // on or above the surface the heightmap collider still does its job
            if (!Heightmap.GetHeight(body.position, out float surface) || body.position.y > surface - 0.5f)
                continue;
            body.isKinematic = true;
            _frozenBodies.Add(body);
        }
    }

    private void UnfreezeLooseBodies()
    {
        foreach (Rigidbody body in _frozenBodies)
        {
            if (body == null)
                continue;
            ZNetView? nview = body.GetComponentInParent<ZNetView>();
            if (nview != null && nview.IsValid() && !nview.IsOwner())
                continue; // remote-owned bodies are kinematic on purpose (ZSyncTransform)
            body.isKinematic = false;
            body.WakeUp();
        }

        _frozenBodies.Clear();
    }

    public void Dispose(bool restoreHeightmap)
    {
        UnfreezeLooseBodies();
        foreach (Chunk chunk in _chunks.Values)
            DestroyChunk(chunk);
        _chunks.Clear();
        _dirty.Clear();
        if (Root != null)
            Object.Destroy(Root);
        Root = null;
        if (!restoreHeightmap || Hmap == null) return;
        if (Hmap.m_meshRenderer != null) Hmap.m_meshRenderer.enabled = true;
        if (Hmap.m_collider != null) Hmap.m_collider.enabled = true;
    }

    private static void DestroyChunk(Chunk chunk)
    {
        if (chunk.Go != null)
        {
            MeshFilter mf = chunk.Go.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null)
                Object.Destroy(mf.sharedMesh);
            Object.Destroy(chunk.Go);
        }

        if (chunk.Collider != null)
            Object.Destroy(chunk.Collider);
    }
}