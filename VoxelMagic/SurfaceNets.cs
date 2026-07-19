using System.Collections.Generic;
using UnityEngine;

namespace HearthBelow.VoxelMagic;

// Surface nets mesh with one vertex per intersected cell, using density gradients for seamless normals.
public static class SurfaceNets
{
    // border verts against a non-voxelized neighbor get tucked under the heightmap so no gap can open
    private const float PristineBorderSink = 0.03f;
    private const float CarvedBorderSink = 0.05f;

    private const float CeilingNormalY = -0.55f;
    private static readonly float CeilingNormalXZ = Mathf.Sqrt(1f - CeilingNormalY * CeilingNormalY);

    private static readonly List<Vector3> Verts = [];
    private static readonly List<Vector3> Normals = [];
    private static readonly List<Color32> Colors = [];
    private static readonly List<Vector2> UVs = [];
    private static readonly List<int> Tris = [];

    // vertex index per cell of the current build window, -1 = no vertex
    private static int[] _cellVerts = [];
    private static Vector3Int _cellMin, _cellMax;
    private static int _sizeX, _sizeZ;

    private static readonly float[] CornerD = new float[8];

    private static readonly int[,] CornerOffset =
    {
        { 0, 0, 0 }, { 1, 0, 0 }, { 0, 1, 0 }, { 1, 1, 0 },
        { 0, 0, 1 }, { 1, 0, 1 }, { 0, 1, 1 }, { 1, 1, 1 }
    };

    private static readonly int[,] CellEdges =
    {
        { 0, 1 }, { 2, 3 }, { 4, 5 }, { 6, 7 },
        { 0, 2 }, { 1, 3 }, { 4, 6 }, { 5, 7 },
        { 0, 4 }, { 1, 5 }, { 2, 6 }, { 3, 7 }
    };

    public static bool Build(VoxelZone zone, Vector3Int sMin, Vector3Int sMax, out List<Vector3> verts, out List<Vector3> normals, out List<Color32> colors, out List<Vector2> uvs, out List<int> tris)
    {
        verts = Verts;
        normals = Normals;
        colors = Colors;
        uvs = UVs;
        tris = Tris;
        Verts.Clear();
        Normals.Clear();
        Colors.Clear();
        UVs.Clear();
        Tris.Clear();
        if (sMin.x >= sMax.x || sMin.y >= sMax.y || sMin.z >= sMax.z)
            return false;

        // cells that quads in this range can reference: [sMin-1, sMax-1], clamped to the grid
        _cellMin = new Vector3Int(Mathf.Max(0, sMin.x - 1), Mathf.Max(0, sMin.y - 1), Mathf.Max(0, sMin.z - 1));
        _cellMax = new Vector3Int(Mathf.Min(zone.NX - 2, sMax.x - 1), Mathf.Min(zone.NY - 2, sMax.y - 1), Mathf.Min(zone.NZ - 2, sMax.z - 1));
        _sizeX = _cellMax.x - _cellMin.x + 1;
        _sizeZ = _cellMax.z - _cellMin.z + 1;
        int cellCount = _sizeX * (_cellMax.y - _cellMin.y + 1) * _sizeZ;
        if (_cellVerts.Length < cellCount)
            _cellVerts = new int[cellCount];
        for (int i = 0; i < cellCount; ++i)
            _cellVerts[i] = -1;

        PlaceVertices(zone);
        if (Verts.Count == 0)
            return false;
        EmitQuads(zone, sMin, sMax);
        return Tris.Count > 0;
    }

    // one vertex per cell whose corners straddle the surface
    private static void PlaceVertices(VoxelZone zone)
    {
        for (int cy = _cellMin.y; cy <= _cellMax.y; ++cy)
        {
            for (int cz = _cellMin.z; cz <= _cellMax.z; ++cz)
            {
                for (int cx = _cellMin.x; cx <= _cellMax.x; ++cx)
                {
                    float[] d = CornerD;
                    int mask = 0;
                    for (int i = 0; i < 8; ++i)
                    {
                        d[i] = zone.D(cx + CornerOffset[i, 0], cy + CornerOffset[i, 1], cz + CornerOffset[i, 2]);
                        if (d[i] > 0f)
                            mask |= 1 << i;
                    }

                    if (mask == 0 || mask == 255)
                        continue; // all solid or all air, no surface in here

                    Vector3 p = AverageEdgeCrossing(d) + new Vector3(cx, cy, cz);
                    Vector3 local = new(p.x * zone.HorizontalSpacing, p.y * VoxelZone.VerticalSpacing, p.z * zone.HorizontalSpacing);

                    // border verts drift off the heightmap on slopes and gaps kept opening on
                    // hillsides - snap untouched cells onto the real surface instead
                    if ((cx == 0 && !zone.NeighborVoxNX) || (cx == zone.NX - 2 && !zone.NeighborVoxPX) ||
                        (cz == 0 && !zone.NeighborVoxNZ) || (cz == zone.NZ - 2 && !zone.NeighborVoxPZ))
                    {
                        if (zone.IsPristineCell(cx, cy, cz) && Heightmap.GetHeight(zone.Origin + local, out float wh))
                            local.y = wh - zone.Origin.y - PristineBorderSink;
                        else
                            local.y -= CarvedBorderSink;
                    }

                    zone.GetSurfaceAttributes(zone.Origin + local, out Vector2 uv, out Color32 color);
                    _cellVerts[CellIndex(cx, cy, cz)] = Verts.Count;
                    Verts.Add(local);
                    Normals.Add(CellNormal(zone, d));
                    Colors.Add(color);
                    UVs.Add(uv);
                }
            }
        }
    }

    // where the surface passes through the unit cell: average of all edge crossings
    private static Vector3 AverageEdgeCrossing(float[] d)
    {
        Vector3 sum = Vector3.zero;
        int crossings = 0;
        for (int e = 0; e < 12; ++e)
        {
            int a = CellEdges[e, 0];
            int b = CellEdges[e, 1];
            if (d[a] > 0f == d[b] > 0f)
                continue;
            float t = d[a] / (d[a] - d[b]);
            sum += new Vector3(
                CornerOffset[a, 0] + t * (CornerOffset[b, 0] - CornerOffset[a, 0]),
                CornerOffset[a, 1] + t * (CornerOffset[b, 1] - CornerOffset[a, 1]),
                CornerOffset[a, 2] + t * (CornerOffset[b, 2] - CornerOffset[a, 2]));
            ++crossings;
        }

        return sum / crossings;
    }

    private static Vector3 CellNormal(VoxelZone zone, float[] d)
    {
        // central-difference gradient of the density, negated (density grows downward into rock)
        float gx = (d[1] + d[3] + d[5] + d[7] - d[0] - d[2] - d[4] - d[6]) * 0.25f;
        float gy = (d[2] + d[3] + d[6] + d[7] - d[0] - d[1] - d[4] - d[5]) * 0.25f;
        float gz = (d[4] + d[5] + d[6] + d[7] - d[0] - d[1] - d[2] - d[3]) * 0.25f;
        Vector3 grad = new(gx / zone.HorizontalSpacing, gy / VoxelZone.VerticalSpacing, gz / zone.HorizontalSpacing);
        Vector3 normal = grad.sqrMagnitude > 1e-8f ? (-grad).normalized : Vector3.up;

        // Valheim's trilight hands straight-down normals the near-black ground color - flat
        // ceilings render as black slabs at night. Tilt them to shade like steep cave walls.
        if (normal.y < CeilingNormalY)
        {
            Vector3 flat = new(normal.x, 0f, normal.z);
            if (flat.sqrMagnitude < 1e-6f)
                flat = new Vector3(0.7f, 0f, 0.7f);
            flat.Normalize();
            normal = new Vector3(flat.x * CeilingNormalXZ, CeilingNormalY, flat.z * CeilingNormalXZ);
        }

        return normal;
    }

    // one quad per grid edge the surface crosses, wound to face the air side
    private static void EmitQuads(VoxelZone zone, Vector3Int sMin, Vector3Int sMax)
    {
        // the zone owns edges at samples [1, NX-3]; edges on the max border line belong to the
        // neighbor and only get rendered here when that neighbor isn't voxelized
        int maxOwned = zone.NX - 3;
        for (int sy = sMin.y; sy < sMax.y; ++sy)
        {
            for (int sz = sMin.z; sz < sMax.z; ++sz)
            {
                bool perpOkZ = sz <= maxOwned || (sz == maxOwned + 1 && !zone.NeighborVoxPZ);
                bool alongOkZ = sz <= maxOwned;
                for (int sx = sMin.x; sx < sMax.x; ++sx)
                {
                    bool perpOkX = sx <= maxOwned || (sx == maxOwned + 1 && !zone.NeighborVoxPX);
                    if (!perpOkX && !perpOkZ)
                        continue;
                    bool alongOkX = sx <= maxOwned;

                    bool solid = zone.D(sx, sy, sz) > 0f;

                    if (alongOkX && perpOkZ && zone.D(sx + 1, sy, sz) > 0f != solid)
                        EmitQuad(solid,
                            new Vector3Int(sx, sy - 1, sz - 1), new Vector3Int(sx, sy, sz - 1),
                            new Vector3Int(sx, sy, sz), new Vector3Int(sx, sy - 1, sz));

                    if (perpOkX && perpOkZ && zone.D(sx, sy + 1, sz) > 0f != solid)
                        EmitQuad(solid,
                            new Vector3Int(sx - 1, sy, sz - 1), new Vector3Int(sx - 1, sy, sz),
                            new Vector3Int(sx, sy, sz), new Vector3Int(sx, sy, sz - 1));

                    if (alongOkZ && perpOkX && zone.D(sx, sy, sz + 1) > 0f != solid)
                        EmitQuad(solid,
                            new Vector3Int(sx - 1, sy - 1, sz), new Vector3Int(sx, sy - 1, sz),
                            new Vector3Int(sx, sy, sz), new Vector3Int(sx - 1, sy, sz));
                }
            }
        }
    }

    private static void EmitQuad(bool solidLower, Vector3Int a, Vector3Int b, Vector3Int c, Vector3Int e)
    {
        int va = CellVert(a);
        int vb = CellVert(b);
        int vc = CellVert(c);
        int ve = CellVert(e);
        if (va < 0 || vb < 0 || vc < 0 || ve < 0)
            return;
        Tris.Add(va);
        if (solidLower)
        {
            Tris.Add(vb);
            Tris.Add(vc);
            Tris.Add(va);
            Tris.Add(vc);
            Tris.Add(ve);
        }
        else
        {
            Tris.Add(vc);
            Tris.Add(vb);
            Tris.Add(va);
            Tris.Add(ve);
            Tris.Add(vc);
        }
    }

    private static int CellVert(Vector3Int c)
    {
        if (c.x < _cellMin.x || c.y < _cellMin.y || c.z < _cellMin.z || c.x > _cellMax.x || c.y > _cellMax.y || c.z > _cellMax.z)
            return -1;
        return _cellVerts[CellIndex(c.x, c.y, c.z)];
    }

    private static int CellIndex(int cx, int cy, int cz)
    {
        return ((cy - _cellMin.y) * _sizeZ + (cz - _cellMin.z)) * _sizeX + (cx - _cellMin.x);
    }
}