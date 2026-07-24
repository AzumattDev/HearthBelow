using HarmonyLib;
using HearthBelow.VoxelMagic;
using UnityEngine;

namespace HearthBelow.Patches;

[HarmonyPatch(typeof(Player), nameof(Player.PieceRayTest))]
public static class Player_PieceRayTest_Patch
{
    private const float FallbackSphereRadius = 0.075f;

    private static readonly AccessTools.FieldRef<Player, int> PlaceRayMaskRef = AccessTools.FieldRefAccess<Player, int>("m_placeRayMask");

    private static readonly AccessTools.FieldRef<Player, int> PlaceWaterRayMaskRef = AccessTools.FieldRefAccess<Player, int>("m_placeWaterRayMask");

    private static readonly AccessTools.FieldRef<Player, GameObject> PlacementGhostRef = AccessTools.FieldRefAccess<Player, GameObject>("m_placementGhost");

    private static void Postfix(Player __instance, bool water, ref bool __result, ref Vector3 point, ref Vector3 normal, ref Piece piece, ref Heightmap heightmap, ref Collider waterSurface)
    {
        if (__result)
            return;

        int layerMask = water ? PlaceWaterRayMaskRef(__instance) : PlaceRayMaskRef(__instance);
        Transform cam = GameCamera.instance.transform;
        if (!Physics.SphereCast(cam.position, FallbackSphereRadius, cam.forward, out RaycastHit hitInfo, 50f, layerMask))
            return;

        // gate on where the cursor actually landed, not the player's feet - leveling/paving is
        // usually aimed several meters down the tunnel, well past the player's own zone boundary
        if (!VoxelWorld.IsVoxelizedAt(hitInfo.point))
            return;

        // same maxPlaceDistance + extra-placement-distance logic vanilla applies after its raycast hits
        float maxPlaceDistance = __instance.m_maxPlaceDistance;
        GameObject ghost = PlacementGhostRef(__instance);
        if (ghost != null)
        {
            Piece ghostPiece = ghost.GetComponent<Piece>();
            if (ghostPiece != null)
                maxPlaceDistance += ghostPiece.m_extraPlacementDistance;
        }

        if (hitInfo.collider == null || hitInfo.collider.attachedRigidbody != null
                                     || Vector3.Distance(__instance.m_eye.position, hitInfo.point) >= maxPlaceDistance)
            return;

        point = hitInfo.point;
        normal = hitInfo.normal;
        piece = hitInfo.collider.GetComponentInParent<Piece>();
        heightmap = hitInfo.collider.GetComponent<Heightmap>();
        waterSurface = hitInfo.collider.gameObject.layer != LayerMask.NameToLayer("Water") ? null : hitInfo.collider;
        __result = true;
    }
}