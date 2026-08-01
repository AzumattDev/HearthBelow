using HarmonyLib;
using HearthBelow.VoxelMagic;

namespace HearthBelow.Patches;

[HarmonyPatch(typeof(Heightmap), nameof(Heightmap.RebuildRenderMesh))]
public static class Heightmap_RebuildRenderMesh_LodPatch
{
    private static void Postfix(Heightmap __instance)
    {
        if (!__instance.IsDistantLod)
        {
            VoxelWorld.GetZone(__instance)?.OnVanillaRenderMeshRebuilt();
            return;
        }

        DistantLod.Register(__instance);
        DistantLod.StripUnderActiveZones(__instance, __instance.m_renderMesh);
    }
}

[HarmonyPatch(typeof(Heightmap), nameof(Heightmap.RebuildCollisionMesh))]
public static class Heightmap_RebuildCollisionMesh_Patch
{
    private static void Postfix(Heightmap __instance)
    {
        if (__instance.IsDistantLod)
            DistantLod.Register(__instance);
    }
}
