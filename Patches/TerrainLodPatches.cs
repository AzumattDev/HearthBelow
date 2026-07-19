using HarmonyLib;
using HearthBelow.VoxelMagic;

namespace HearthBelow.Patches;

// Sink the finalized LOD heights here so both collision and render meshes build correctly once.
[HarmonyPatch(typeof(Heightmap), nameof(Heightmap.RebuildCollisionMesh))]
public static class Heightmap_RebuildCollisionMesh_Patch
{
    private static void Prefix(Heightmap __instance)
    {
        if (!__instance.IsDistantLod)
            return;
        DistantLod.Register(__instance);
        DistantLod.SinkUnderActiveZones(__instance);
    }
}
