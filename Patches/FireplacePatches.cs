using HarmonyLib;
using HearthBelow.VoxelMagic;
using UnityEngine;

namespace HearthBelow.Patches;

[HarmonyPatch(typeof(Fireplace), nameof(Fireplace.CheckUnderTerrain))]
public static class Fireplace_CheckUnderTerrain_Patch
{
    private static bool Prefix(Fireplace __instance)
    {
        Vector3 pos = __instance.transform.position;
        VoxelZone? zone = VoxelWorld.GetZone(Heightmap.FindHeightmap(pos));
        if (zone is not { IsActive: true })
            return true;
        if (!(Heightmap.GetHeight(pos, out float height) && height > pos.y + __instance.m_checkTerrainOffset))
            return true;

        __instance.m_blocked = false;
        if (__instance.m_disableCoverCheck)
            return false;
        if (zone.SampleSolid(pos + Vector3.up * 0.5f) || Physics.Raycast(pos + Vector3.up * __instance.m_coverCheckOffset, Vector3.up, out RaycastHit _, 0.5f, Fireplace.m_solidRayMask) || __instance.m_smokeSpawner != null && __instance.m_smokeSpawner.IsBlocked())
            __instance.m_blocked = true;
        return false;
    }
}