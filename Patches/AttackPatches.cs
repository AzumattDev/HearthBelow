using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using UnityEngine;

namespace HearthBelow.Patches;

[HarmonyPatch(typeof(Attack), nameof(Attack.SpawnOnHitTerrain))]
public static class Attack_SpawnOnHitTerrain_Patch
{
    internal static bool InTerrainAttack;
    internal static bool LocalPlayerSwing;
    internal static Vector3? DigDir;
    internal static float ToolDepthCap = float.PositiveInfinity;

    private static void Prefix(Vector3 hitPoint, Character character, ItemDrop.ItemData? weapon)
    {
        LocalPlayerSwing = character != null && (Character)Player.m_localPlayer == character;
        Vector3 origin = character != null ? (character.m_eye != null ? character.m_eye.position : character.transform.position + Vector3.up * 1.5f) : hitPoint + Vector3.up;
        Vector3 dir = hitPoint - origin;
        DigDir = dir.sqrMagnitude > 0.01f ? dir.normalized : (Vector3?)null;
        ToolDepthCap = ResolveToolDepthCap(weapon);
        InTerrainAttack = true;
    }

    private static void Finalizer()
    {
        InTerrainAttack = false;
        LocalPlayerSwing = false;
        DigDir = null;
        ToolDepthCap = float.PositiveInfinity;
    }

    private static string? _parsedList;
    private static readonly Dictionary<int, float> DepthCaps = new();
    
    private static float ResolveToolDepthCap(ItemDrop.ItemData? weapon)
    {
        if (HearthBelowPlugin.ToolDepthLimits.Value != HearthBelowPlugin.Toggle.On || weapon?.m_shared == null)
            return float.PositiveInfinity;

        string list = HearthBelowPlugin.ToolTierDepthList.Value;
        if (!ReferenceEquals(list, _parsedList))
        {
            DepthCaps.Clear();
            foreach (string entry in list.Split(','))
            {
                int sep = entry.IndexOf(':');
                if (sep <= 0)
                    continue;
                if (int.TryParse(entry.Substring(0, sep).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int tierKey)
                    && float.TryParse(entry.Substring(sep + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out float meters))
                    DepthCaps[tierKey] = meters;
            }

            _parsedList = list;
        }

        return DepthCaps.TryGetValue(weapon.m_shared.m_toolTier, out float cap) ? cap : float.PositiveInfinity;
    }
}