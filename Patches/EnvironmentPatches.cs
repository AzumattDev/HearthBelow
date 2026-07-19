using System;
using HarmonyLib;
using HearthBelow.VoxelMagic;
using UnityEngine;

namespace HearthBelow.Patches;

[HarmonyPatch(typeof(EnvMan), nameof(EnvMan.GetEnvironmentOverride))]
public static class EnvMan_GetEnvironmentOverride_Patch
{
    private static float _nextCaveCheck;
    private static bool _inCave;
    private static string? _rawConfigValue;
    private static string? _resolvedEnv;

    private static void Postfix(EnvMan __instance, ref string? __result)
    {
        if (!string.IsNullOrEmpty(__result))
            return;
        if (HearthBelowPlugin.VoxelDigging.Value != HearthBelowPlugin.Toggle.On)
            return;

        string raw = HearthBelowPlugin.UndergroundEnvironment.Value;
        if (!ReferenceEquals(raw, _rawConfigValue))
            ResolveEnvName(__instance, raw);
        if (_resolvedEnv == null)
            return;

        Player player = Player.m_localPlayer;
        if (player == null)
            return;

        if (Time.time >= _nextCaveCheck)
        {
            _nextCaveCheck = Time.time + 1f;
            _inCave = VoxelWorld.IsInCaveAt(player.transform.position, HearthBelowPlugin.UndergroundEnvironmentDepth.Value);
        }

        if (_inCave)
            __result = _resolvedEnv;
    }

    private static void ResolveEnvName(EnvMan env, string raw)
    {
        _rawConfigValue = raw;
        _resolvedEnv = null;
        string wanted = raw.Trim();
        if (wanted.Length == 0)
            return;

        for (int i = 0; i < env.m_environments.Count; ++i)
        {
            EnvSetup setup = env.m_environments[i];
            if (!string.Equals(setup.m_name, wanted, StringComparison.OrdinalIgnoreCase)) continue;
            _resolvedEnv = setup.m_name;
            return;
        }

        HearthBelowPlugin.HearthBelowLogger.LogWarning($"Underground Environment '{wanted}' does not exist, no cave environment will be applied. Try Crypt, SunkenCrypt, Caves or InfectedMine.");
    }
}