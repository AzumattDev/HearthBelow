using HarmonyLib;
using HearthBelow.VoxelMagic;
using UnityEngine;

namespace HearthBelow.Patches;

[HarmonyPatch(typeof(Character), nameof(Character.UnderWorldCheck))]
public static class Character_UnderWorldCheck_Patch
{
    private static bool Prefix(Character __instance)
    {
        if (!VoxelWorld.SuppressTerrainCheck(__instance.transform.position))
            return true;
        __instance.m_underWorldCheckTimer = 0f; // Prevents the player from teleporting to land above.
        return false;
    }
}

// A character that loads in before its cave finishes meshing has no floor and would free-fall
// out of the grid. I copied this shit from how teleporting works.
[HarmonyPatch(typeof(Character), nameof(Character.CustomFixedUpdate))]
public static class Character_CustomFixedUpdate_Patch
{
    private static void Prefix(Character __instance)
    {
        if (__instance.m_nview == null || !__instance.m_nview.IsValid() || !__instance.m_nview.IsOwner())
            return;
        Vector3 pos = __instance.transform.position;
        if (!VoxelWorld.IsMeshPendingAt(pos))
            return;
        __instance.m_body.linearVelocity = Vector3.zero;
        __instance.m_maxAirAltitude = pos.y;
    }
}