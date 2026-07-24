using HarmonyLib;
using HearthBelow.VoxelMagic;
using UnityEngine;

namespace HearthBelow.Patches;

[HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.GetGroundHeight), typeof(Vector3))]
public static class ZoneSystem_GetGroundHeight_Patch
{
    // clears the player's head, stays under any tunnel carved with normal tool radii
    private const float LocalCastStartOffset = 3f;
    private const float LocalCastDistance = 50f;

    private static readonly int TerrainMask = LayerMask.GetMask("terrain");

    private static bool Prefix(Vector3 p, ref float __result)
    {
        if (!VoxelWorld.IsVoxelizedAt(p))
            return true;

        Vector3 origin = p with { y = p.y + LocalCastStartOffset };
        if (!Physics.Raycast(origin, Vector3.down, out RaycastHit hitInfo, LocalCastDistance, TerrainMask))
            return true;

        __result = hitInfo.point.y;
        return false;
    }
}