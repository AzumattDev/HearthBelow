using HarmonyLib;
using HearthBelow.VoxelMagic;

namespace HearthBelow.Patches;

[HarmonyPatch(typeof(TerrainComp), nameof(TerrainComp.Awake))]
public static class TerrainComp_Awake_Patch
{
    private static void Postfix(TerrainComp __instance)
    {
        ZNetView nview = __instance.m_nview;
        if (nview == null || !nview.IsValid())
            return;
        TerrainComp comp = __instance;
        nview.Register<ZPackage>("HearthBelow_Carve", (sender, pkg) => VoxelWorld.RPC_Carve(comp, sender, pkg));
        nview.Register("HearthBelow_Clear", (long sender) => VoxelWorld.RPC_Clear(comp, sender));
        nview.Register<ZPackage>("HearthBelow_RemoveOps", (sender, pkg) => VoxelWorld.RPC_RemoveOps(comp, sender, pkg));
    }
}

[HarmonyPatch(typeof(TerrainComp), nameof(TerrainComp.Update))]
public static class TerrainComp_Update_Patch
{
    private static void Postfix(TerrainComp __instance)
    {
        VoxelWorld.PollComp(__instance);
    }
}

[HarmonyPatch(typeof(TerrainComp), nameof(TerrainComp.OnDestroy))]
public static class TerrainComp_OnDestroy_Patch
{
    private static void Postfix(TerrainComp __instance)
    {
        VoxelWorld.ForgetComp(__instance);
    }
}