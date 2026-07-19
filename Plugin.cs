using System;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using JetBrains.Annotations;
using LocalizationManager;
using ServerSync;
using UnityEngine;

namespace HearthBelow;

[BepInPlugin(ModGUID, ModName, ModVersion)]
public class HearthBelowPlugin : BaseUnityPlugin
{
    internal const string ModName = "HearthBelow";
    internal const string ModVersion = "0.2.0";
    internal const string Author = "Azumatt";
    private const string ModGUID = Author + "." + ModName;
    private static string ConfigFileName = ModGUID + ".cfg";
    private static string ConfigFileFullPath = Paths.ConfigPath + Path.DirectorySeparatorChar + ConfigFileName;
    internal static string ConnectionError = "";
    private readonly Harmony _harmony = new(ModGUID);
    public static readonly ManualLogSource HearthBelowLogger = BepInEx.Logging.Logger.CreateLogSource(ModName);
    private static readonly ConfigSync ConfigSync = new(ModGUID) { DisplayName = ModName, CurrentVersion = ModVersion, MinimumRequiredVersion = ModVersion, ModRequired = true};
    private FileSystemWatcher? _watcher;
    private readonly object _reloadLock = new();
    private DateTime _lastConfigReloadTime;
    private const long RELOAD_DELAY = 10000000; // One second

    public enum Toggle
    {
        On = 1,
        Off = 0
    }

    public enum DigShape
    {
        Sphere = 0,
        Cube = 1
    }

    public enum DigStyle
    {
        Gradual = 0,
        Blast = 1
    }

    public void Awake()
    {
        Localizer.Load();
        bool saveOnSet = Config.SaveOnConfigSet;
        Config.SaveOnConfigSet = false;

        _serverConfigLocked = config("1 - General", "Lock Configuration", Toggle.On, new ConfigDescription("If on, the configuration is locked and can be changed by server admins only.", null, new ConfigurationManagerAttributes { Order = 100 }));
        _ = ConfigSync.AddLockingConfigEntry(_serverConfigLocked);

        VoxelDigging = config("2 - Digging", "Voxel Digging", Toggle.On, new ConfigDescription("Master switch. If on, pickaxes carve real holes and tunnels into the terrain - dig sideways into a hillside, hollow out a mountain. If off, digging works like vanilla.", null, new ConfigurationManagerAttributes { Order = 90 }));
        DigMode = config("2 - Digging", "Dig Mode", DigStyle.Gradual, new ConfigDescription("Gradual takes a shallow bite in the direction you swing, so repeated hits dig in predictably like vanilla terrain edits do. Blast removes the full Carve Radius in a single hit.", null, new ConfigurationManagerAttributes { Order = 89 }));
        DigDepthPerHit = config("2 - Digging", "Dig Depth Per Hit", 0.75f, new ConfigDescription("Gradual mode only. How many meters a single pickaxe hit bites into the surface.", new AcceptableValueRange<float>(0.25f, 2f), new ConfigurationManagerAttributes { Order = 88 }));
        CarveRadius = config("2 - Digging", "Carve Radius", 1.6f, new ConfigDescription("How wide the hole from one pickaxe hit is, in meters.", new AcceptableValueRange<float>(0.5f, 3f), new ConfigurationManagerAttributes { Order = 87 }));
        CarveShape = config("2 - Digging", "Dig Shape", DigShape.Cube, new ConfigDescription("Cube digs flat floors, walls and ceilings, which is great for building. Sphere digs round, organic looking caves.", null, new ConfigurationManagerAttributes { Order = 86 }));
        CaveDepth = config("2 - Digging", "Max Cave Depth", 128f, new ConfigDescription("How many meters below the original terrain surface you can dig, no matter the tool. Keep this at or above the deepest Tool Tier Depth Limit (default 128 = blackmetal) or the top tiers get silently cut short. Higher values use more memory per dug-out zone. Avoid changing this on a world that already has deep caves.", new AcceptableValueRange<float>(4f, 256f), new ConfigurationManagerAttributes { Order = 85 }));
        ToolDepthLimits = config("2 - Digging", "Tool Depth Limits", Toggle.On, new ConfigDescription("If on, better pickaxes dig deeper (see Tool Tier Depth Limits). Digging at a tool's limit leaves a flat, even floor - swapping to a lower tier pickaxe on purpose is a handy way to line up floors for building. If off, every pickaxe digs to Max Cave Depth.", null, new ConfigurationManagerAttributes { Order = 84 }));
        // Still debating on this a bit. If I want to reduce. People in testing kept saying they wanted to go a lot deeper than 64 max. I doubled it...see how this goes
        ToolTierDepthList = config("2 - Digging", "Tool Tier Depth Limits", "0:8,1:16,2:48,3:128", new ConfigDescription("Tier:meters pairs, comma separated. Tier is the pickaxe's tool tier - the same stat that decides which ores it can mine - so modded pickaxes fit in automatically. Vanilla: 0 = antler/stone, 1 = bronze, 2 = iron, 3 = blackmetal. The defaults are tuned around two anchors: 8m is vanilla's own dig limit (so antler/stone feel vanilla), and 16m is where the cave ambience kicks in - bronze digs to the edge of the dark, iron is the first pickaxe that takes you properly into it, and blackmetal reaches the digging floor. Tiers not listed here dig with no tool limit.", null, new ConfigurationManagerAttributes { Order = 83 }));
        MaxOpsPerZone = config("2 - Digging", "Max Carves Per Zone", 5000, new ConfigDescription("Safety cap on how many dig operations a single 64x64m zone can store. Normal play won't get anywhere near this.", new AcceptableValueRange<int>(100, 20000), new ConfigurationManagerAttributes { Order = 82 }));

        // ambience is client-side presentation, deliberately NOT synced
        UndergroundEnvironment = config("3 - Underground", "Underground Environment", "Darklands_dark", new ConfigDescription("Ambience/weather while you are deep inside a cave, applied the same way vanilla dungeons do it (fades in and out like a weather change). Vanilla options: Crypt (dark - bring a torch), SunkenCrypt, Caves (frost caves), InfectedMine. Not case sensitive. Leave empty to keep the outside weather underground.", null, new ConfigurationManagerAttributes { Order = 79 }), false);
        UndergroundEnvironmentDepth = config("3 - Underground", "Underground Environment Depth", 16f, new ConfigDescription("How many meters below the original surface you must be before the cave ambience starts. Keeps shallow dugouts and walk-in basements feeling like the outdoors. The default 16 lines up with the bronze pickaxe's depth limit, so darkness arrives with iron-tier digging.", new AcceptableValueRange<float>(2f, 32f), new ConfigurationManagerAttributes { Order = 78 }), false);
        UndergroundPainting = config("3 - Underground", "Underground Painting", Toggle.On, new ConfigDescription("Let hoe paths/paving paint cave floors. Heads up: the game stores terrain paint top-down with no depth, so a painted cave floor also shows on the ground directly above the cave. Turn off if clean surfaces matter more to you than paintable cave floors.", null, new ConfigurationManagerAttributes { Order = 77 }));

        Assembly assembly = Assembly.GetExecutingAssembly();
        _harmony.PatchAll(assembly);
        SetupWatcher();

        Config.Save();
        if (saveOnSet)
        {
            Config.SaveOnConfigSet = saveOnSet;
        }
    }

    private void OnDestroy()
    {
        SaveWithRespectToConfigSet();
        _watcher?.Dispose();
    }

    private void SetupWatcher()
    {
        FileSystemWatcher watcher = new(Paths.ConfigPath, ConfigFileName);
        watcher.Changed += ReadConfigValues;
        watcher.Created += ReadConfigValues;
        watcher.Renamed += ReadConfigValues;
        watcher.IncludeSubdirectories = true;
        watcher.SynchronizingObject = ThreadingHelper.SynchronizingObject;
        watcher.EnableRaisingEvents = true;
        _watcher = watcher;
    }

    private void ReadConfigValues(object sender, FileSystemEventArgs e)
    {
        DateTime now = DateTime.Now;
        long time = now.Ticks - _lastConfigReloadTime.Ticks;
        if (time < RELOAD_DELAY)
        {
            return;
        }

        lock (_reloadLock)
        {
            if (!File.Exists(ConfigFileFullPath))
            {
                HearthBelowLogger.LogWarning("Config file does not exist. Skipping reload.");
                return;
            }

            try
            {
                HearthBelowLogger.LogDebug("Reloading configuration...");
                SaveWithRespectToConfigSet(true);
                HearthBelowLogger.LogInfo("Configuration reload complete.");
            }
            catch (Exception ex)
            {
                HearthBelowLogger.LogError($"Error reloading configuration: {ex.Message}");
            }
        }

        _lastConfigReloadTime = now;
    }

    private void SaveWithRespectToConfigSet(bool reload = false)
    {
        bool originalSaveOnSet = Config.SaveOnConfigSet;
        Config.SaveOnConfigSet = false;
        if (reload)
            Config.Reload();
        Config.Save();
        if (originalSaveOnSet)
        {
            Config.SaveOnConfigSet = originalSaveOnSet;
        }
    }

    #region ConfigOptions

    private static ConfigEntry<Toggle> _serverConfigLocked = null!;
    internal static ConfigEntry<Toggle> VoxelDigging = null!;
    internal static ConfigEntry<DigStyle> DigMode = null!;
    internal static ConfigEntry<float> DigDepthPerHit = null!;
    internal static ConfigEntry<float> CarveRadius = null!;
    internal static ConfigEntry<DigShape> CarveShape = null!;
    internal static ConfigEntry<float> CaveDepth = null!;
    internal static ConfigEntry<Toggle> ToolDepthLimits = null!;
    internal static ConfigEntry<string> ToolTierDepthList = null!;
    internal static ConfigEntry<int> MaxOpsPerZone = null!;
    internal static ConfigEntry<string> UndergroundEnvironment = null!;
    internal static ConfigEntry<float> UndergroundEnvironmentDepth = null!;
    internal static ConfigEntry<Toggle> UndergroundPainting = null!;

    private ConfigEntry<T> config<T>(string group, string name, T value, ConfigDescription description, bool synchronizedSetting = true)
    {
        ConfigDescription extendedDescription = new(description.Description + (synchronizedSetting ? " [Synced with Server]" : " [Not Synced with Server]"), description.AcceptableValues, description.Tags);
        ConfigEntry<T> configEntry = Config.Bind(group, name, value, extendedDescription);

        SyncedConfigEntry<T> syncedConfigEntry = ConfigSync.AddConfigEntry(configEntry);
        syncedConfigEntry.SynchronizedConfig = synchronizedSetting;

        return configEntry;
    }

    private ConfigEntry<T> config<T>(string group, string name, T value, string description, bool synchronizedSetting = true)
    {
        return config(group, name, value, new ConfigDescription(description), synchronizedSetting);
    }

    private class ConfigurationManagerAttributes
    {
        [UsedImplicitly] public int? Order = null!;
        [UsedImplicitly] public bool? Browsable = null!;
        [UsedImplicitly] public string? Category = null!;
        [UsedImplicitly] public Action<ConfigEntryBase>? CustomDrawer = null!;
    }

    #endregion
}