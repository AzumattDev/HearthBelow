using HarmonyLib;
using HearthBelow.VoxelMagic;
using UnityEngine;

namespace HearthBelow.Patches;

[HarmonyPatch(typeof(TerrainOp), nameof(TerrainOp.Awake))]
public static class TerrainOp_Awake_Patch
{
    private static void Prefix(TerrainOp __instance)
    {
        if (TerrainOp.m_forceDisableTerrainOps)
            return;
        Vector3 pos = __instance.transform.position;

        if (Attack_SpawnOnHitTerrain_Patch.InTerrainAttack)
        {
            if (HearthBelowPlugin.VoxelDigging.Value != HearthBelowPlugin.Toggle.On)
                return;
            bool carved = VoxelWorld.CarveAt(pos, HearthBelowPlugin.CarveRadius.Value, Attack_SpawnOnHitTerrain_Patch.DigDir,
                quiet: !Attack_SpawnOnHitTerrain_Patch.LocalPlayerSwing,
                toolDepthCap: Attack_SpawnOnHitTerrain_Patch.ToolDepthCap);
            TerrainOp.Settings s = __instance.m_settings;
            s.m_level = false;
            s.m_raise = false;
            s.m_smooth = false; // paint stays on - dig marks around the hole look right
            if (!carved)
                __instance.m_spawnOnPlaced = null; // prevent farming glitch at depth limits. Thank you James for finding this one in testing.
            return;
        }

        if (!VoxelWorld.IsCarvedGroundAt(pos))
            return;
        TerrainOp.Settings settings = __instance.m_settings;
        if (settings.m_raise)
        {
            VoxelWorld.RaiseAt(pos, settings.m_raiseRadius, settings.m_raiseDelta, settings.m_raisePower);
        }
        else if (settings.m_level)
        {
            VoxelWorld.FlattenAt(pos + Vector3.up * settings.m_levelOffset, Mathf.Max(1.5f, settings.m_levelRadius));
            if (settings.m_smooth)
                VoxelWorld.SmoothAt(pos + Vector3.up * settings.m_levelOffset, Mathf.Max(2f, settings.m_smoothRadius));
        }
        else if (settings.m_smooth)
        {
            VoxelWorld.SmoothAt(pos + Vector3.up * settings.m_levelOffset, Mathf.Max(2f, settings.m_smoothRadius));
        }
    }
}

[HarmonyPatch(typeof(TerrainComp), nameof(TerrainComp.InternalDoOperation))]
public static class TerrainComp_InternalDoOperation_Patch
{
    private static void Prefix(Vector3 pos, TerrainOp.Settings modifier)
    {
        if (!VoxelWorld.IsCarvedGroundAt(pos))
            return;
        modifier.m_level = false;
        modifier.m_raise = false;
        modifier.m_smooth = false;
        modifier.m_paintHeightCheck = false;
        // the paint mask is 2D and shared with the surface above - painting down here WILL
        // show up top, and honestly, it pisses me off. Not sure how to get around this yet, so config for now.
        if (HearthBelowPlugin.UndergroundPainting.Value != HearthBelowPlugin.Toggle.On)
            modifier.m_paintCleared = false;
    }
}