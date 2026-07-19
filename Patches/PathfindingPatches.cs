using HarmonyLib;
using HearthBelow.VoxelMagic;
using UnityEngine;
using UnityEngine.AI;

namespace HearthBelow.Patches;

//SnapToNavMesh clamps path endpoints up to "ground height" (monsters pace on the surface above you), and
// IsUnderTerrain throws away paths > 1m below it. Both prefixes bail unless the point is in carved cave air.
[HarmonyPatch(typeof(Pathfinding), nameof(Pathfinding.SnapToNavMesh))]
public static class Pathfinding_SnapToNavMesh_Patch
{
    private static readonly float[] ExtendedRanges = [1.5f, 3f, 6f, 12f];
    private static readonly float[] Ranges = [1f];

    private static bool Prefix(ref Vector3 point, bool extendedSearchArea, Pathfinding.AgentSettings settings, ref bool __result)
    {
        if (!VoxelWorld.IsUndergroundAt(point))
            return true;

        // vanilla's method minus the ground clamp; keep the swim clamp, water still exists
        if (settings.m_canSwim)
            point.y = Mathf.Max(30f - settings.m_swimDepth, point.y);
        NavMeshQueryFilter filter = new()
        {
            agentTypeID = settings.m_build.agentTypeID,
            areaMask = settings.m_areaMask
        };
        foreach (float range in extendedSearchArea ? ExtendedRanges : Ranges)
        {
            if (!NavMesh.SamplePosition(point, out NavMeshHit hit, range, filter)) continue;
            point = hit.position;
            __result = true;
            return false;
        }

        __result = false;
        return false;
    }
}

[HarmonyPatch(typeof(Pathfinding), nameof(Pathfinding.IsUnderTerrain))]
public static class Pathfinding_IsUnderTerrain_Patch
{
    private static bool Prefix(Vector3 p, ref bool __result)
    {
        VoxelZone? zone = VoxelWorld.GetActiveZoneAt(p);
        if (zone == null)
            return true;
        
        __result = zone.SampleSolid(p + Vector3.up);
        return false;
    }
}