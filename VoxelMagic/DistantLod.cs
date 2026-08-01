using System.Collections.Generic;
using UnityEngine;

namespace HearthBelow.VoxelMagic;

// TerrainLod ignores every edit, so inside a dug-out mountain it shows as a white walk-through
public static class DistantLod
{
    private static readonly List<Heightmap> LodMaps = [];

    private static readonly HashSet<Heightmap> Pending = [];
    private static int _lastFlushFrame = -1;

    private static readonly List<Vector3> StripVerts = [];
    private static readonly List<int> StripTris = [];
    private static readonly List<int> StripKept = [];
    private static readonly HashSet<Vector2Int> ActiveZones = [];

    public static void Register(Heightmap lod)
    {
        if (!LodMaps.Contains(lod))
            LodMaps.Add(lod);
    }

    public static void Unregister(Heightmap lod)
    {
        LodMaps.Remove(lod);
        Pending.Remove(lod);
    }

    public static void SetVisible(bool visible)
    {
        for (int i = LodMaps.Count - 1; i >= 0; --i)
        {
            Heightmap lod = LodMaps[i];
            if (lod == null)
            {
                LodMaps.RemoveAt(i);
                continue;
            }

            if (lod.m_meshRenderer != null)
                lod.m_meshRenderer.enabled = visible;
        }
    }

    public static void RefreshAt(Heightmap zoneHmap)
    {
        if (zoneHmap == null)
            return;
        Vector3 c = zoneHmap.transform.position;
        float half = zoneHmap.m_width * zoneHmap.m_scale * 0.5f;
        for (int i = LodMaps.Count - 1; i >= 0; --i)
        {
            Heightmap lod = LodMaps[i];
            if (lod == null)
            {
                LodMaps.RemoveAt(i);
                continue;
            }

            float lodHalf = lod.m_width * lod.m_scale * 0.5f;
            Vector3 lp = lod.transform.position;
            if (Mathf.Abs(c.x - lp.x) > lodHalf + half || Mathf.Abs(c.z - lp.z) > lodHalf + half)
                continue;
            Pending.Add(lod);
        }
    }

    public static void FlushPending()
    {
        if (Pending.Count == 0 || _lastFlushFrame == Time.frameCount)
            return;
        _lastFlushFrame = Time.frameCount;
        foreach (Heightmap lod in Pending)
        {
            if (lod != null)
                lod.Regenerate();
        }

        Pending.Clear();
    }

    public static bool StripUnderActiveZones(Heightmap lod, Mesh? mesh)
    {
        if (lod == null || mesh == null)
            return false;

        ActiveZones.Clear();
        float zoneSize = 0f;
        foreach (KeyValuePair<Heightmap, VoxelZone> pair in VoxelWorld.Zones)
        {
            Heightmap zh = pair.Key;
            if (zh == null || pair.Value is not { IsActive: true, HasCarvedGeometry: true })
                continue;
            zoneSize = zh.m_width * zh.m_scale;
            if (zoneSize <= 0f)
                continue;
            Vector3 c = zh.transform.position;
            ActiveZones.Add(ZoneKey(c.x, c.z, zoneSize));
        }

        if (ActiveZones.Count == 0 || zoneSize <= 0f)
            return false;

        StripVerts.Clear();
        StripTris.Clear();
        mesh.GetVertices(StripVerts);
        mesh.GetTriangles(StripTris, 0);
        if (StripTris.Count == 0)
            return false;

        Transform t = lod.transform;
        StripKept.Clear();
        for (int i = 0; i + 2 < StripTris.Count; i += 3)
        {
            Vector3 local = (StripVerts[StripTris[i]] + StripVerts[StripTris[i + 1]] + StripVerts[StripTris[i + 2]]) / 3f;
            Vector3 world = t.TransformPoint(local);
            if (ActiveZones.Contains(ZoneKey(world.x, world.z, zoneSize)))
                continue;
            StripKept.Add(StripTris[i]);
            StripKept.Add(StripTris[i + 1]);
            StripKept.Add(StripTris[i + 2]);
        }

        if (StripKept.Count == StripTris.Count)
            return false;
        mesh.SetTriangles(StripKept, 0);
        return true;
    }

    private static Vector2Int ZoneKey(float worldX, float worldZ, float zoneSize)
    {
        return new Vector2Int(
            Mathf.FloorToInt(worldX / zoneSize + 0.5f),
            Mathf.FloorToInt(worldZ / zoneSize + 0.5f));
    }
}
