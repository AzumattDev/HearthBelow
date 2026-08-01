using System.Collections.Generic;
using UnityEngine;

namespace HearthBelow.VoxelMagic;

public static class SeamDiagnostics
{
    public static float WeldTolerance = 1e-3f;

    public readonly struct Hole
    {
        public readonly Vector3 A, B;
        public readonly int Uses;

        public readonly int ZoneMask;

        public Hole(Vector3 a, Vector3 b, int uses, int zoneMask)
        {
            A = a;
            B = b;
            Uses = uses;
            ZoneMask = zoneMask;
        }

        public Vector3 Midpoint => (A + B) * 0.5f;
    }

    public static readonly List<Vector3> Zones = [];

    private sealed class Welder
    {
        private readonly Dictionary<Vector3Int, List<int>> _cells = new();
        private readonly List<Vector3> _points = [];

        public int Weld(Vector3 p)
        {
            Vector3Int key = Cell(p);
            for (int dz = -1; dz <= 1; ++dz)
            {
                for (int dy = -1; dy <= 1; ++dy)
                {
                    for (int dx = -1; dx <= 1; ++dx)
                    {
                        if (!_cells.TryGetValue(new Vector3Int(key.x + dx, key.y + dy, key.z + dz), out List<int>? bucket))
                            continue;
                        foreach (int id in bucket)
                        {
                            if ((_points[id] - p).sqrMagnitude <= WeldTolerance * WeldTolerance)
                                return id;
                        }
                    }
                }
            }

            int newId = _points.Count;
            _points.Add(p);
            if (!_cells.TryGetValue(key, out List<int>? own))
            {
                own = [];
                _cells[key] = own;
            }

            own.Add(newId);
            return newId;
        }

        public Vector3 Position(int id) => _points[id];

        private static Vector3Int Cell(Vector3 p) => new(
            Mathf.FloorToInt(p.x / WeldTolerance),
            Mathf.FloorToInt(p.y / WeldTolerance),
            Mathf.FloorToInt(p.z / WeldTolerance));
    }

    private const float IncludeMargin = 8f;

    public static List<Hole> Analyze(Vector3 center, float radius, out int zoneCount, out int edgeCount)
    {
        zoneCount = 0;
        edgeCount = 0;
        List<Hole> holes = [];

        Welder welder = new();
        Dictionary<long, int> edgeUses = new();
        Dictionary<long, int> edgeZones = new();
        List<Vector3> verts = [];
        List<int> tris = [];
        Dictionary<int, int> vertIds = new();
        Zones.Clear();
        float include = radius + IncludeMargin;
        float include2 = include * include;
        Bounds box = new(center, new Vector3(include * 2f, 20000f, include * 2f));
        float gather = include + 64f;

        foreach (KeyValuePair<Heightmap, VoxelZone> kv in VoxelWorld.Zones)
        {
            Heightmap hmap = kv.Key;
            if (hmap == null || Utils.DistanceXZ(hmap.transform.position, center) > gather)
                continue;
            ++zoneCount;
            int zoneBit = Zones.Count < 31 ? 1 << Zones.Count : 0;
            Zones.Add(hmap.transform.position);
            Transform tf = hmap.transform;
            foreach (Mesh mesh in kv.Value.DebugChunkMeshes())
            {
                Bounds worldBounds = mesh.bounds;
                worldBounds.center += tf.position;
                if (!worldBounds.Intersects(box))
                    continue;
                verts.Clear();
                tris.Clear();
                vertIds.Clear();
                mesh.GetVertices(verts);
                mesh.GetTriangles(tris, 0);
                for (int i = 0; i + 2 < tris.Count; i += 3)
                {
                    int ta = tris[i], tb = tris[i + 1], tc = tris[i + 2];
                    Vector3 pa = tf.TransformPoint(verts[ta]);
                    Vector3 pb = tf.TransformPoint(verts[tb]);
                    Vector3 pc = tf.TransformPoint(verts[tc]);
                    Vector3 mid = (pa + pb + pc) * (1f / 3f);
                    float dx = mid.x - center.x, dz = mid.z - center.z;
                    if (dx * dx + dz * dz > include2)
                        continue;
                    int ia = WeldCached(welder, vertIds, ta, pa);
                    int ib = WeldCached(welder, vertIds, tb, pb);
                    int ic = WeldCached(welder, vertIds, tc, pc);
                    Bump(edgeUses, edgeZones, ia, ib, zoneBit);
                    Bump(edgeUses, edgeZones, ib, ic, zoneBit);
                    Bump(edgeUses, edgeZones, ic, ia, zoneBit);
                }
            }
        }

        edgeCount = edgeUses.Count;
        float r2 = radius * radius;
        foreach (KeyValuePair<long, int> e in edgeUses)
        {
            if (e.Value == 2)
                continue;
            int a = (int)(e.Key >> 32);
            int b = (int)(e.Key & 0xFFFFFFFF);
            Vector3 pa = welder.Position(a), pb = welder.Position(b);
            if (((pa + pb) * 0.5f - center).sqrMagnitude > r2)
                continue;
            holes.Add(new Hole(pa, pb, e.Value, edgeZones.TryGetValue(e.Key, out int m) ? m : 0));
        }

        holes.Sort((x, y) => (x.Midpoint - center).sqrMagnitude.CompareTo((y.Midpoint - center).sqrMagnitude));
        return holes;
    }

    private static int WeldCached(Welder welder, Dictionary<int, int> cache, int localIndex, Vector3 world)
    {
        if (cache.TryGetValue(localIndex, out int id))
            return id;
        id = welder.Weld(world);
        cache[localIndex] = id;
        return id;
    }

    private static void Bump(Dictionary<long, int> edges, Dictionary<long, int> zones, int a, int b, int zoneBit)
    {
        long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        edges[key] = edges.TryGetValue(key, out int n) ? n + 1 : 1;
        zones[key] = zones.TryGetValue(key, out int m) ? m | zoneBit : zoneBit;
    }
}
