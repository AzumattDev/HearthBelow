using System.Collections.Generic;
using UnityEngine;

namespace HearthBelow.VoxelMagic;

// TerrainLod ignores every edit, so inside a dug-out mountain it shows as a white walk-through
// sheet. Sink its verts under active voxel zones - the real mesh renders there anyway.
public static class DistantLod
{
    // sunk this far below the zone's grid floor, no carve can ever reach the sheet
    private const float SinkMargin = 4f;
    
    private static readonly List<Heightmap> LodMaps = [];

    public static void Register(Heightmap lod)
    {
        if (!LodMaps.Contains(lod)) 
            LodMaps.Add(lod);
    }

    public static void Unregister(Heightmap lod) => LodMaps.Remove(lod);

    // a zone gained or lost its mesh - regenerate the LOD tiles over it so they re-sink or un-sink
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
            lod.Regenerate();
        }
    }

    // called right before the LOD tile builds its meshes, heights freshly copied from build data
    public static void SinkUnderActiveZones(Heightmap lod)
    {
        int num = lod.m_width + 1;
        float scale = lod.m_scale;
        Vector3 lp = lod.transform.position;
        float lodMinX = lp.x - lod.m_width * scale * 0.5f;
        float lodMinZ = lp.z - lod.m_width * scale * 0.5f;
        foreach (KeyValuePair<Heightmap, VoxelZone> pair in VoxelWorld.Zones)
        {
            Heightmap zh = pair.Key;
            VoxelZone zone = pair.Value;
            if (zh == null || zone is not { IsActive: true })
                continue;
            Vector3 c = zh.transform.position;
            float half = zh.m_width * zh.m_scale * 0.5f;
            int x0 = Mathf.Max(0, Mathf.CeilToInt((c.x - half - lodMinX) / scale));
            int x1 = Mathf.Min(lod.m_width, Mathf.FloorToInt((c.x + half - lodMinX) / scale));
            int z0 = Mathf.Max(0, Mathf.CeilToInt((c.z - half - lodMinZ) / scale));
            int z1 = Mathf.Min(lod.m_width, Mathf.FloorToInt((c.z + half - lodMinZ) / scale));
            if (x1 < x0 || z1 < z0)
                continue;
            float sunk = zone.Origin.y - SinkMargin - lp.y;
            for (int z = z0; z <= z1; ++z)
            {
                int row = z * num;
                for (int x = x0; x <= x1; ++x)
                {
                    if (lod.m_heights[row + x] > sunk)
                        lod.m_heights[row + x] = sunk;
                }
            }
        }
    }
}
