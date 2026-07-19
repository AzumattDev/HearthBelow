using System.Collections.Generic;
using HarmonyLib;
using HearthBelow.VoxelMagic;
using UnityEngine;

namespace HearthBelow.Patches;

// Honestly, the amount of places the game checks objects under the terrain in the code was a little dumb. If I didn't have a decompiled project of their shit
// this would have been much harder to find. Big thanks to JamesJones for testing this.

[HarmonyPatch(typeof(ItemDrop), nameof(ItemDrop.TerrainCheck))]
public static class ItemDrop_TerrainCheck_Patch
{
    private static bool Prefix(ItemDrop __instance) => !VoxelWorld.SuppressTerrainCheck(__instance.transform.position);
}

[HarmonyPatch(typeof(Floating), nameof(Floating.TerrainCheck))]
public static class Floating_TerrainCheck_Patch
{
    private static bool Prefix(Floating __instance) => !VoxelWorld.SuppressTerrainCheck(__instance.transform.position);
}

[HarmonyPatch(typeof(TombStone), nameof(TombStone.PositionCheck))]
public static class TombStone_PositionCheck_Patch
{
    private static bool Prefix(TombStone __instance) => !VoxelWorld.SuppressTerrainCheck(__instance.transform.position);
}

[HarmonyPatch(typeof(StaticPhysics), nameof(StaticPhysics.PushUp))]
public static class StaticPhysics_PushUp_Patch
{
    private static bool Prefix(StaticPhysics __instance) => !VoxelWorld.SuppressTerrainCheck(__instance.transform.position);
}

[HarmonyPatch(typeof(DropOnDestroyed), nameof(DropOnDestroyed.OnDestroyed))]
public static class DropOnDestroyed_OnDestroyed_Patch
{
    private static bool Prefix(DropOnDestroyed __instance)
    {
        Vector3 basePos = __instance.transform.position;
        if (!VoxelWorld.SuppressTerrainCheck(basePos))
            return true;
        List<GameObject> dropList = __instance.m_dropWhenDestroyed.GetDropList();
        for (int i = 0; i < dropList.Count; ++i)
        {
            Vector2 c = Random.insideUnitCircle * 0.5f;
            Vector3 pos = basePos + Vector3.up * __instance.m_spawnYOffset + new Vector3(c.x, __instance.m_spawnYStep * i, c.y);
            Quaternion rot = Quaternion.Euler(0f, Random.Range(0, 360), 0f);
            ItemDrop.OnCreateNew(Object.Instantiate(dropList[i], pos, rot));
        }

        return false;
    }
}

[HarmonyPatch(typeof(Game), nameof(Game.FindSpawnPoint))]
public static class Game_FindSpawnPoint_Patch
{
    // bail-out so a broken zone can't hold the loading screen hostage forever
    private const float MeshWaitTimeout = 30f;
    
    private static Vector3? _protectSpawn;
    private static float _gateStart = -1f;

    private static bool Prefix(Game __instance, ref Vector3 point, ref bool usedLogoutPoint, float dt, ref bool __result)
    {
        _protectSpawn = null;
        bool logout = !__instance.m_respawnAfterDeath && __instance.m_playerProfile.HaveLogoutPoint();
        Vector3 target;
        if (logout)
            target = __instance.m_playerProfile.GetLogoutPoint();
        else if (__instance.m_playerProfile.HaveCustomSpawnPoint())
            target = __instance.m_playerProfile.GetCustomSpawnPoint();
        else
        {
            _gateStart = -1f;
            return true;
        }

        // not underground (or the heightmap isn't loaded yet similar to IsAreaReady from vanilla)
        if (!Heightmap.GetHeight(target, out float surface) || target.y >= surface - 1f)
        {
            _gateStart = -1f;
            return true;
        }

        if (VoxelWorld.HasSavedOpsAt(target) && VoxelWorld.GetActiveZoneAt(target) == null)
        {
            if (_gateStart < 0f)
                _gateStart = Time.time;
            if (Time.time - _gateStart < MeshWaitTimeout)
            {
                __instance.m_respawnWait += dt;
                ZNet.instance.SetReferencePosition(target);
                point = Vector3.zero;
                usedLogoutPoint = false;
                __result = false;
                return false;
            }

            HearthBelowPlugin.HearthBelowLogger.LogWarning($"Voxel mesh at spawn point {target} never became ready, letting vanilla place the player");
        }

        _gateStart = -1f;
        if (logout)
            _protectSpawn = target;
        return true;
    }

    private static void Postfix(ref Vector3 point, ref bool usedLogoutPoint, ref bool __result)
    {
        if (_protectSpawn is not Vector3 saved)
            return;
        _protectSpawn = null;
        if (!__result || !usedLogoutPoint)
            return;

        if (Utils.DistanceXZ(point, saved) < 0.5f && point.y > saved.y + 0.5f)
            point.y = saved.y + 0.25f;
    }
}