using System.Collections.Generic;
using UnityEngine;

namespace HearthBelow.VoxelMagic;

public static class MeshRectClipper
{
    private const int MaxPolygonVertices = 12;

    private struct ClipVertex
    {
        public Vector3 Pos;
        public Vector3 Normal;
        public Vector2 Uv;
        public Color32 Color;
    }

    private static readonly ClipVertex[] PolyA = new ClipVertex[MaxPolygonVertices];
    private static readonly ClipVertex[] PolyB = new ClipVertex[MaxPolygonVertices];

    private static readonly List<Vector3> OutVerts = [];
    private static readonly List<Vector3> OutNormals = [];
    private static readonly List<Vector2> OutUvs = [];
    private static readonly List<Color32> OutColors = [];
    private static readonly List<int> OutTris = [];
    private readonly struct VertexKey : System.IEquatable<VertexKey>
    {
        private readonly Vector3 _pos;
        private readonly Vector3 _normal;
        private readonly Vector2 _uv;

        public VertexKey(ClipVertex v)
        {
            _pos = v.Pos;
            _normal = v.Normal;
            _uv = v.Uv;
        }

        public bool Equals(VertexKey o) => _pos.Equals(o._pos) && _normal.Equals(o._normal) && _uv.Equals(o._uv);
        public override bool Equals(object? o) => o is VertexKey k && Equals(k);

        public override int GetHashCode()
        {
            int h = _pos.GetHashCode();
            h = (h * 397) ^ _normal.GetHashCode();
            return (h * 397) ^ _uv.GetHashCode();
        }
    }

    private static readonly Dictionary<VertexKey, int> Dedup = new();

    public static void Clip(List<Vector3> verts, List<Vector3> normals, List<Vector2> uvs, List<Color32> colors,
        List<int> tris, float minX, float maxX, float minZ, float maxZ)
    {
        OutVerts.Clear();
        OutNormals.Clear();
        OutUvs.Clear();
        OutColors.Clear();
        OutTris.Clear();
        Dedup.Clear();

        for (int i = 0; i + 2 < tris.Count; i += 3)
        {
            ClipVertex[] poly = PolyA;
            ClipVertex[] scratch = PolyB;
            int count = 3;
            for (int j = 0; j < 3; ++j)
            {
                int idx = tris[i + j];
                poly[j] = new ClipVertex { Pos = verts[idx], Normal = normals[idx], Uv = uvs[idx], Color = colors[idx] };
            }

            count = ClipPlane(poly, count, scratch, axisX: true, minX, keepGreater: true);
            Swap(ref poly, ref scratch);
            count = ClipPlane(poly, count, scratch, axisX: true, maxX, keepGreater: false);
            Swap(ref poly, ref scratch);
            count = ClipPlane(poly, count, scratch, axisX: false, minZ, keepGreater: true);
            Swap(ref poly, ref scratch);
            count = ClipPlane(poly, count, scratch, axisX: false, maxZ, keepGreater: false);
            Swap(ref poly, ref scratch);

            if (count < 3 || CoplanarWithMin(poly, count, minX, minZ))
                continue;
            for (int k = 1; k + 1 < count; ++k)
                Emit(poly[0], poly[k], poly[k + 1]);
        }

        verts.Clear();
        verts.AddRange(OutVerts);
        normals.Clear();
        normals.AddRange(OutNormals);
        uvs.Clear();
        uvs.AddRange(OutUvs);
        colors.Clear();
        colors.AddRange(OutColors);
        tris.Clear();
        tris.AddRange(OutTris);
    }

    private static int ClipPlane(ClipVertex[] input, int count, ClipVertex[] output, bool axisX, float bound, bool keepGreater)
    {
        if (count == 0)
            return 0;
        int outCount = 0;
        ClipVertex prev = input[count - 1];
        bool prevIn = Inside(prev, axisX, bound, keepGreater);
        for (int i = 0; i < count; ++i)
        {
            ClipVertex cur = input[i];
            bool curIn = Inside(cur, axisX, bound, keepGreater);
            if (curIn != prevIn && outCount < MaxPolygonVertices)
                output[outCount++] = Intersect(prev, cur, axisX, bound);
            if (curIn && outCount < MaxPolygonVertices)
                output[outCount++] = cur;
            prev = cur;
            prevIn = curIn;
        }

        return outCount;
    }

    private static bool Inside(ClipVertex v, bool axisX, float bound, bool keepGreater)
    {
        float c = axisX ? v.Pos.x : v.Pos.z;
        return keepGreater ? c >= bound : c <= bound;
    }

    private static ClipVertex Intersect(ClipVertex a, ClipVertex b, bool axisX, float bound)
    {
        float ca = axisX ? a.Pos.x : a.Pos.z;
        float cb = axisX ? b.Pos.x : b.Pos.z;
        ClipVertex lo = ca <= cb ? a : b;
        ClipVertex hi = ca <= cb ? b : a;
        float loC = Mathf.Min(ca, cb);
        float hiC = Mathf.Max(ca, cb);
        float t = hiC - loC > 1e-9f ? (bound - loC) / (hiC - loC) : 0f;

        Vector3 pos = Vector3.Lerp(lo.Pos, hi.Pos, t);
        if (axisX) pos.x = bound;
        else pos.z = bound;

        return new ClipVertex
        {
            Pos = pos,
            Normal = Vector3.Slerp(lo.Normal, hi.Normal, t),
            Uv = Vector2.Lerp(lo.Uv, hi.Uv, t),
            Color = Color32.Lerp(lo.Color, hi.Color, t)
        };
    }

    private static bool CoplanarWithMin(ClipVertex[] poly, int count, float minX, float minZ)
    {
        bool allX = true, allZ = true;
        for (int i = 0; i < count; ++i)
        {
            allX &= poly[i].Pos.x == minX;
            allZ &= poly[i].Pos.z == minZ;
        }

        return allX || allZ;
    }

    private static void Emit(ClipVertex a, ClipVertex b, ClipVertex c)
    {
        Vector3 cross = Vector3.Cross(b.Pos - a.Pos, c.Pos - a.Pos);
        if (cross.sqrMagnitude < 1e-10f)
            return;
        OutTris.Add(Add(a));
        OutTris.Add(Add(b));
        OutTris.Add(Add(c));
    }

    private static int Add(ClipVertex v)
    {
        VertexKey key = new(v);
        if (Dedup.TryGetValue(key, out int existing))
            return existing;
        int idx = OutVerts.Count;
        OutVerts.Add(v.Pos);
        OutNormals.Add(v.Normal);
        OutUvs.Add(v.Uv);
        OutColors.Add(v.Color);
        Dedup.Add(key, idx);
        return idx;
    }

    private static void Swap(ref ClipVertex[] a, ref ClipVertex[] b)
    {
        (a, b) = (b, a);
    }
}
