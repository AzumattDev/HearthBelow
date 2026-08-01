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

        _ = new Terminal.ConsoleCommand("hearthbelow", "hearthbelow [carve|fill|raise|flatten|smooth|restore|remesh|holes|show|weather|info] - voxel cave digging commands", args =>
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
                case "raise":
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
                            // same delta/power ballpark as the hoe's raise piece
                            "raise" => VoxelWorld.RaiseAt(hit.point, radius, 1f, 3f),
                            "flatten" => VoxelWorld.FlattenAt(hit.point, radius),
                            "smooth" => VoxelWorld.SmoothAt(hit.point, radius),
                            // no dig direction: the console command always blasts the full radius
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
                case "meshinfo":
                {
                    Heightmap mhmap = Heightmap.FindHeightmap(player.transform.position);
                    if (mhmap == null)
                    {
                        ctx?.AddString("No heightmap here.");
                        return;
                    }

                    void Line(string s)
                    {
                        ctx?.AddString(s);
                        HearthBelowPlugin.HearthBelowLogger.LogInfo(s);
                    }

                    static string Describe(string label, Mesh? m)
                    {
                        if (m == null)
                            return $"  {label}: null";
                        return $"  {label}: verts={m.vertexCount} tris={m.triangles.Length / 3}"
                               + $" normals={m.normals.Length} colors={m.colors32.Length} tangents={m.tangents.Length}"
                               + $" uv={m.uv.Length} uv2={m.uv2.Length}";
                    }

                    static string DescribeRenderer(string label, MeshRenderer? r)
                    {
                        if (r == null)
                            return $"  {label}: null";
                        return $"  {label}: shadows={r.shadowCastingMode} receive={r.receiveShadows}"
                               + $" lightProbe={r.lightProbeUsage} reflProbe={r.reflectionProbeUsage} enabled={r.enabled}";
                    }

                    Line($"Heightmap at {mhmap.transform.position} width={mhmap.m_width} scale={mhmap.m_scale}");
                    Line(Describe("vanilla render   ", mhmap.m_renderMesh));
                    Line(DescribeRenderer("vanilla renderer ", mhmap.m_meshRenderer));
                    Material? mat = mhmap.m_materialInstance;
                    Line($"  material: {(mat == null ? "null" : mat.name)} shader: {(mat == null || mat.shader == null ? "?" : mat.shader.name)}");

                    if (mat != null && mat.shader != null)
                    {
                        int props = mat.shader.GetPropertyCount();
                        int shownTex = 0;
                        for (int pi = 0; pi < props && shownTex < 6; ++pi)
                        {
                            if (mat.shader.GetPropertyType(pi) != UnityEngine.Rendering.ShaderPropertyType.Texture)
                                continue;
                            string pname = mat.shader.GetPropertyName(pi);
                            Texture? tex = mat.GetTexture(pname);
                            if (tex == null)
                                continue;
                            Line($"  tex {pname}: '{tex.name}' wrapU={tex.wrapModeU} wrapV={tex.wrapModeV}");
                            ++shownTex;
                        }
                    }

                    VoxelZone? mz = VoxelWorld.GetZone(mhmap);
                    if (mz == null)
                    {
                        Line("  no voxel zone here (this ground is pure vanilla)");
                        break;
                    }

                    int shown = 0;
                    foreach (Mesh cm in mz.DebugChunkMeshes())
                    {
                        Line(Describe($"our chunk #{shown}   ", cm));
                        if (++shown >= 2)
                            break;
                    }

                    Line(DescribeRenderer("our renderer     ", mz.DebugFirstChunkRenderer()));
                    Line(mz.DebugSurfaceInfo());
                    Line($"  our chunk meshes: {shown} shown");
                    break;
                }
                case "show":
                {
                    string mode = args.Args.Length > 2 ? args.Args[2].ToLowerInvariant() : "all";
                    if (mode is not ("all" or "voxel" or "vanilla" or "lod"))
                    {
                        ctx?.AddString("usage: hearthbelow show [all|voxel|vanilla|lod]");
                        return;
                    }

                    VoxelWorld.SetDebugView(mode, player.transform.position, 300f);
                    ctx?.AddString(mode == "all"
                        ? "Restored normal rendering."
                        : $"Showing ONLY the {mode} mesh within 300m. Run 'hearthbelow show all' to restore.");
                    break;
                }
                case "holes":
                {
                    Vector3 p = player.transform.position;
                    SeamDiagnostics.WeldTolerance = args.Args.Length > 2
                        && float.TryParse(args.Args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float tol)
                        && tol > 0f
                            ? tol
                            : 1e-3f;
                    List<SeamDiagnostics.Hole> holes = SeamDiagnostics.Analyze(p, 24f, out int zones, out int edges);
                    string head = $"Cross-zone hole check (weld {SeamDiagnostics.WeldTolerance:G}): {zones} zone(s), {edges} welded edge(s), {holes.Count} unclosed edge(s) within 24m.";
                    ctx?.AddString(head);
                    HearthBelowPlugin.HearthBelowLogger.LogInfo(head);
                    for (int z = 0; z < SeamDiagnostics.Zones.Count; ++z)
                    {
                        string zl = $"  zone {z} @ {SeamDiagnostics.Zones[z]}";
                        HearthBelowPlugin.HearthBelowLogger.LogInfo(zl);
                        ctx?.AddString(zl);
                    }

                    for (int i = 0; i < holes.Count; ++i)
                    {
                        SeamDiagnostics.Hole h = holes[i];
                        string zoneList = "";
                        for (int z = 0; z < SeamDiagnostics.Zones.Count; ++z)
                            if ((h.ZoneMask & (1 << z)) != 0)
                                zoneList += (zoneList.Length > 0 ? "+" : "") + z;
                        string line = $"  [{h.Uses}x zones={zoneList}] {h.A} -> {h.B}";
                        HearthBelowPlugin.HearthBelowLogger.LogInfo(line);
                        if (i < 8)
                            ctx?.AddString(line);
                    }

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
                case "weather":
                {
                    List<string> lines = [];
                    CaveShelter.Report(lines);
                    foreach (string l in lines)
                    {
                        ctx?.AddString(l);
                        HearthBelowPlugin.HearthBelowLogger.LogInfo(l);
                    }

                    break;
                }
                default:
                {
                    ctx?.AddString(VoxelWorld.GetInfo(player.transform.position));
                    break;
                }
            }
        }, optionsFetcher: () => ["carve", "fill", "raise", "flatten", "smooth", "restore", "remesh", "holes", "show", "meshinfo", "weather", "info"]);
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