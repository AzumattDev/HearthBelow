using System;
using System.Collections.Generic;
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

internal static class CaveShelter
{
    private const float RecheckInterval = 0.25f;

    private static float _nextCheck;
    private static bool _sheltered;

    internal static bool Enabled => HearthBelowPlugin.VoxelDigging.Value == HearthBelowPlugin.Toggle.On;

    internal static bool PlayerSheltered()
    {
        if (!Enabled)
            return false;

        Player player = Player.m_localPlayer;
        if (player == null)
            return false;

        if (Time.time >= _nextCheck)
        {
            _nextCheck = Time.time + RecheckInterval;
            _sheltered = Evaluate(player);
        }

        return _sheltered;
    }

    private static bool Evaluate(Player player)
    {
        Vector3 pos = player.GetCenterPoint();
        if (!VoxelWorld.IsInCaveAt(pos))
            return false;

        Cover.GetCoverForPoint(pos, out float coverage, out bool underRoof);
        return underRoof && coverage >= HearthBelowPlugin.CaveWeatherCover.Value;
    }

    internal static void Report(System.Collections.Generic.List<string> lines)
    {
        lines.Add($"shelter enabled={Enabled} (digging={HearthBelowPlugin.VoxelDigging.Value})");

        Player player = Player.m_localPlayer;
        if (player == null)
        {
            lines.Add("no local player");
            return;
        }

        Vector3 pos = player.GetCenterPoint();
        bool carved = VoxelWorld.IsCarvedZoneAt(pos);
        bool haveSurface = Heightmap.GetHeight(pos, out float surface);
        bool ceiling = Physics.Raycast(pos + Vector3.up * 0.5f, Vector3.up, out RaycastHit hit, 200f, LayerMask.GetMask("terrain"));
        Cover.GetCoverForPoint(pos, out float coverage, out bool underRoof);

        lines.Add($"pos={pos} carvedZone={carved} surface={(haveSurface ? surface.ToString("0.0") : "?")} depth={(haveSurface ? (surface - pos.y).ToString("0.0") : "?")}m");
        lines.Add($"ceiling={(ceiling ? $"{hit.distance:0.0}m '{hit.collider.name}' layer={LayerMask.LayerToName(hit.collider.gameObject.layer)}" : "NONE (open sky above)")}");
        lines.Add($"cover={coverage:0.00} (need {HearthBelowPlugin.CaveWeatherCover.Value:0.00}) underRoof={underRoof}");
        lines.Add($"=> IsInCaveAt={VoxelWorld.IsInCaveAt(pos)} sheltered={Evaluate(player)} InShelter()={player.InShelter()}");

        EnvMan env = EnvMan.instance;
        if (env == null)
        {
            lines.Add("no EnvMan");
            return;
        }

        EnvSetup cur = env.m_currentEnv;
        GameObject[]? ps = cur?.m_psystems;
        lines.Add($"env='{cur?.m_name}' forced='{env.m_forceEnv}' psystems={(ps == null ? "null" : ps.Length.ToString())} outsideOnly={cur?.m_psystemsOutsideOnly} currentPSystems={(env.m_currentPSystems == null ? "null" : env.m_currentPSystems.Length.ToString())} weBlocked={EnvMan_SetEnv_Patch.BlockedCount}");
        lines.Add($"names='{HearthBelowPlugin.CaveWeatherParticles.Value}' matched={EnvMan_SetEnv_Patch.DescribeMatches(ps)}");

        if (ps == null)
            return;
        foreach (GameObject go in ps)
        {
            if (go == null)
                continue;
            foreach (ParticleSystem p in go.GetComponentsInChildren<ParticleSystem>())
                lines.Add($"  ps '{go.name}/{p.name}' emitting={p.emission.enabled} alive={p.particleCount} playing={p.isPlaying} active={p.gameObject.activeInHierarchy}");
        }
    }
}

[HarmonyPatch(typeof(Player), nameof(Player.InShelter))]
public static class Player_InShelter_Patch
{
    private static void Postfix(Player __instance, ref bool __result)
    {
        if (__result || __instance != Player.m_localPlayer)
            return;
        if (CaveShelter.PlayerSheltered())
            __result = true;
    }
}

[HarmonyPatch(typeof(EnvMan), nameof(EnvMan.SetEnv))]
public static class EnvMan_SetEnv_Patch
{
    private static readonly int WetShaderId = Shader.PropertyToID("_Wet");
    private static readonly List<GameObject> Matched = [];

    private static GameObject[]? _held;
    private static bool _wetHeld;
    private static float _wetBefore;

    private static string? _rawNames;
    private static string[] _names = [];

    internal static string BlockedCount => _held == null ? "no" : Matched.Count.ToString();

    private static void Prefix()
    {
        _wetHeld = CaveShelter.PlayerSheltered();
        if (_wetHeld)
            _wetBefore = Shader.GetGlobalFloat(WetShaderId);
    }

    private static void Postfix(EnvMan __instance, EnvSetup env, float dt)
    {
        if (_wetHeld)
        {
            _wetHeld = false;
            float wet = Mathf.MoveTowards(_wetBefore, 0f, dt / Mathf.Max(0.01f, __instance.m_wetTransitionDuration));
            Shader.SetGlobalFloat(WetShaderId, wet);
        }

        bool sheltered = CaveShelter.PlayerSheltered();
        GameObject[]? want = sheltered ? env.m_psystems : null;
        if (ReferenceEquals(_held, want))
            return;

        if (_held != null)
        {
            if (!sheltered && ReferenceEquals(__instance.m_currentPSystems, _held))
                SetMatchedEnabled(_held, true);
            _held = null;
        }

        if (want == null || want.Length == 0)
            return;

        SetMatchedEnabled(want, false);
        _held = want;
    }

    private static void SetMatchedEnabled(GameObject[] systems, bool enabled)
    {
        Collect(systems);
        foreach (GameObject go in Matched)
        foreach (ParticleSystem ps in go.GetComponentsInChildren<ParticleSystem>())
        {
            ParticleSystem.EmissionModule emission = ps.emission;
            emission.enabled = enabled;
            if (!enabled)
                ps.Clear(true);
        }
    }

    internal static string DescribeMatches(GameObject[]? systems)
    {
        if (systems == null)
            return "none";
        Collect(systems);
        if (Matched.Count == 0)
            return "NONE - nothing in this environment matches the name list";
        string joined = "";
        foreach (GameObject go in Matched)
            joined += (joined.Length > 0 ? "+" : "") + go.name;
        return joined;
    }

    private static void Collect(GameObject[] systems)
    {
        Matched.Clear();
        ParseNames();
        foreach (GameObject go in systems)
        {
            if (go == null)
                continue;
            string name = go.name;
            foreach (string needle in _names)
            {
                if (name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                Matched.Add(go);
                break;
            }
        }
    }

    private static void ParseNames()
    {
        string raw = HearthBelowPlugin.CaveWeatherParticles.Value;
        if (ReferenceEquals(raw, _rawNames))
            return;
        _rawNames = raw;

        List<string> parsed = [];
        foreach (string part in raw.Split(','))
        {
            string trimmed = part.Trim();
            if (trimmed.Length > 0)
                parsed.Add(trimmed);
        }

        _names = parsed.ToArray();
    }
}