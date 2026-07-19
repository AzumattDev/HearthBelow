using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using HearthBelow.VoxelMagic;
using UnityEngine;

namespace HearthBelow.Patches;

[HarmonyPatch(typeof(Terminal), nameof(Terminal.InitTerminal))]
public static class Terminal_InitTerminal_Patch
{
    private const float MaxAimDistance = 100f;
    private static bool _registered;

    private static void Postfix()
    {
        if (_registered)
            return;
        _registered = true;

        _ = new Terminal.ConsoleCommand("hearthbelow", "hearthbelow [carve|fill|flatten|smooth|restore|remesh|info] - voxel cave digging commands", args =>
        {
            Terminal ctx = args.Context;
            Player player = Player.m_localPlayer;
            if (player == null)
            {
                ctx?.AddString("Requires a local player.");
                return;
            }

            string sub = args.Args.Length > 1 ? args.Args[1].ToLowerInvariant() : "info";
            
            bool editsTerrain = sub is "carve" or "fill" or "raise" or "flatten" or "smooth" or "restore";
            if (editsTerrain && !IsLocalPlayerAdmin())
            {
                ctx?.AddString($"'hearthbelow {sub}' can only be used by server admins (adminlist.txt).");
                return;
            }
            
            switch (sub)
            {
                case "carve":
                case "fill":
                case "flatten":
                case "smooth":
                {
                    float radius = HearthBelowPlugin.CarveRadius.Value;
                    if (args.Args.Length > 2 && float.TryParse(args.Args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float r))
                        radius = Mathf.Clamp(r, 0.25f, 6f);
                    GameCamera cam = GameCamera.instance;
                    if (cam == null)
                    {
                        ctx?.AddString("No camera.");
                        return;
                    }

                    if (Physics.Raycast(cam.transform.position, cam.transform.forward, out RaycastHit hit, MaxAimDistance, LayerMask.GetMask("terrain")))
                    {
                        bool ok = sub switch
                        {
                            "fill" => VoxelWorld.FillAt(hit.point, radius),
                            "flatten" => VoxelWorld.FlattenAt(hit.point, radius),
                            "smooth" => VoxelWorld.SmoothAt(hit.point, radius),
                            // no dig direction: the console command always blasts the full
                            // radius instead of taking pickaxe-style bites
                            _ => VoxelWorld.CarveAt(hit.point, radius)
                        };
                        ctx?.AddString(ok ? $"{sub} r={radius:0.0} at {hit.point}" : $"{sub} had no effect (ward, no-build, or depth limit?)");
                    }
                    else
                    {
                        ctx?.AddString($"No terrain under the crosshair within {MaxAimDistance:0}m.");
                    }

                    break;
                }
                case "remesh":
                {
                    VoxelZone? zone = VoxelWorld.GetZone(Heightmap.FindHeightmap(player.transform.position));
                    if (zone == null)
                    {
                        ctx?.AddString("Zone not voxelized.");
                        return;
                    }

                    zone.ForceRemeshAll();
                    ctx?.AddString("Rebuilt all chunk meshes for this zone.");
                    break;
                }
                case "restore":
                {
                    Heightmap hmap = Heightmap.FindHeightmap(player.transform.position);
                    if (hmap == null)
                    {
                        ctx?.AddString("No heightmap here.");
                        return;
                    }

                    VoxelWorld.RequestClear(hmap);
                    ctx?.AddString("Requested restore of this zone (removes all carves).");
                    break;
                }
                default:
                {
                    ctx?.AddString(VoxelWorld.GetInfo(player.transform.position));
                    break;
                }
            }
        }, optionsFetcher: () => ["carve", "fill", "flatten", "smooth", "restore", "remesh", "info"]);
    }
    
    private static bool IsLocalPlayerAdmin()
    {
        ZNet net = ZNet.instance;
        if (net == null)
            return true;
        if (net.LocalPlayerIsAdminOrHost())
            return true;
        List<string> admins = net.GetAdminList();
        return admins != null && admins.Contains(UserInfo.GetLocalUser().UserId.m_userID);
    }
}