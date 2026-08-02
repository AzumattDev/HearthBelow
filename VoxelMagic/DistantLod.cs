using System.Collections.Generic;
using UnityEngine;

namespace HearthBelow.VoxelMagic;

// TerrainLod ignores every edit, so inside a dug-out mountain it shows as a white walk-through
public static class DistantLod
{
    private const float StripRange = 64f;

    private static readonly List<Heightmap> LodMaps = [];

    private static readonly HashSet<Heightmap> Pending = [];
    private static int _lastTickFrame = -1;

    private static readonly List<Vector3> StripVerts = [];
    private static readonly List<int> StripTris = [];
    private static readonly List<int> StripKept = [];
    private static readonly HashSet<Vector2Int> ActiveZones = [];
    private static readonly HashSet<Vector2Int> Stripped = [];
    private static readonly HashSet<Vector2Int> Scratch = [];
    private static float _lastZoneSize;

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
        QueueOverlapping(zoneHmap.transform.position, zoneHmap.m_width * zoneHmap.m_scale * 0.5f);
    }

    private static void QueueOverlapping(Vector3 centre, float half)
    {
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
            if (Mathf.Abs(centre.x - lp.x) > lodHalf + half || Mathf.Abs(centre.z - lp.z) > lodHalf + half)
                continue;
            Pending.Add(lod);
        }
    }

    public static void Tick()
    {
        if (_lastTickFrame == Time.frameCount)
            return;
        _lastTickFrame = Time.frameCount;
        RefreshEligibility();
        FlushPending();
    }

    private static void RefreshEligibility()
    {
        if (Player.m_localPlayer == null)
        {
            Stripped.Clear();
            return;
        }

        float zoneSize = CollectStrippable(Scratch);
        if (zoneSize <= 0f)
            zoneSize = _lastZoneSize;
        if (zoneSize <= 0f || Scratch.SetEquals(Stripped))
            return;

        foreach (Vector2Int key in Scratch)
        {
            if (!Stripped.Contains(key))
                QueueOverlapping(new Vector3(key.x * zoneSize, 0f, key.y * zoneSize), zoneSize * 0.5f);
        }

        foreach (Vector2Int key in Stripped)
        {
            if (!Scratch.Contains(key))
                QueueOverlapping(new Vector3(key.x * zoneSize, 0f, key.y * zoneSize), zoneSize * 0.5f);
        }

        Stripped.Clear();
        Stripped.UnionWith(Scratch);
    }

    private static float CollectStrippable(HashSet<Vector2Int> into)
    {
        into.Clear();
        Player? player = Player.m_localPlayer;
        if (player == null)
            return 0f;
        Vector3 eye = player.transform.position;
        float zoneSize = 0f;
        foreach (KeyValuePair<Heightmap, VoxelZone> pair in VoxelWorld.Zones)
        {
            Heightmap zh = pair.Key;
            if (zh == null || pair.Value is not { IsActive: true, HasCarvedGeometry: true })
                continue;
            float size = zh.m_width * zh.m_scale;
            if (size <= 0f)
                continue;
            zoneSize = _lastZoneSize = size;
            Vector3 c = zh.transform.position;
            float half = size * 0.5f;
            if (Mathf.Abs(eye.x - c.x) > half + StripRange || Mathf.Abs(eye.z - c.z) > half + StripRange)
                continue;
            into.Add(ZoneKey(c.x, c.z, size));
        }

        return zoneSize;
    }

    private static void FlushPending()
    {
        if (Pending.Count == 0)
            return;
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

        float zoneSize = CollectStrippable(ActiveZones);
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