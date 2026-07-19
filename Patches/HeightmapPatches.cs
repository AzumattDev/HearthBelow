using HarmonyLib;
using HearthBelow.VoxelMagic;

namespace HearthBelow.Patches;

[HarmonyPatch(typeof(Heightmap), nameof(Heightmap.Regenerate))]
public static class Heightmap_Regenerate_Patch
{
    private static void Postfix(Heightmap __instance)
    {
        VoxelWorld.OnHeightmapRegenerated(__instance);
    }
}

[HarmonyPatch(typeof(Heightmap), nameof(Heightmap.OnDestroy))]
public static class Heightmap_OnDestroy_Patch
{
    private static void Postfix(Heightmap __instance)
    {
        if (__instance.IsDistantLod)
            DistantLod.Unregister(__instance);
        else
            VoxelWorld.OnHeightmapDestroyed(__instance);
    }
}