using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace HearthBelow.VoxelMagic;

// Tracks voxel zones and syncs carve operations through TerrainComp ZDOs and RPCs.
public static class VoxelWorld
{
    // clamps for player-driven ops - digs stay tight, flatten/smooth reach wider
    private const float MinDigRadius = 0.25f, MaxDigRadius = 4f;
    private const float MinPlaneRadius = 0.5f, MaxPlaneRadius = 6f;

    // gap under the heightmap that counts as carved ground; clears the mesher's worst-case
    // drift on uncarved slopes (~10cm + 5cm border sink)
    private const float CarvedGroundGap = 0.35f;

    // PollComp retries per comp before giving up on a heightmap that never gets ready (~10s)
    private const int MaxApplyAttempts = 600;

    internal static readonly Dictionary<Heightmap, VoxelZone> Zones = new();
    private static readonly Dictionary<TerrainComp, uint> LastRev = new();
    private static readonly Dictionary<TerrainComp, int> FailedAttempts = new();
    private static readonly List<Heightmap> TmpHmaps = [];

    public static VoxelZone? GetZone(Heightmap? hmap)
    {
        return hmap != null && Zones.TryGetValue(hmap, out VoxelZone? zone) ? zone : null;
    }

    public static VoxelZone? EnsureZone(Heightmap hmap)
    {
        if (Zones.TryGetValue(hmap, out VoxelZone? existing))
            return existing;
        VoxelZone zone = new();
        long start = Stopwatch.GetTimestamp();
        if (!zone.Build(hmap))
            return null;
        Zones[hmap] = zone;
        HearthBelowPlugin.HearthBelowLogger.LogDebug($"Voxelized zone at {hmap.transform.position}: grid {zone.NX}x{zone.NY}x{zone.NZ}, data built in {ElapsedMs(start):F1} ms");
        return zone;
    }

    private static double ElapsedMs(long fromTimestamp) => (Stopwatch.GetTimestamp() - fromTimestamp) * 1000.0 / Stopwatch.Frequency;

    public static void RemoveZone(Heightmap hmap, bool restoreHeightmap)
    {
        if (!Zones.TryGetValue(hmap, out VoxelZone? zone))
            return;
        zone.Dispose(restoreHeightmap);
        Zones.Remove(hmap);
        DistantLod.RefreshAt(hmap); // un-sink the LOD sheet over this zone
        if (restoreHeightmap)
            NotifyNeighbors(hmap); // neighbors must extend their border strips over this zone again
    }

    public static VoxelZone? GetActiveZoneAt(Vector3 pos)
    {
        VoxelZone? zone = GetZone(Heightmap.FindHeightmap(pos));
        return zone != null && zone.IsActive ? zone : null;
    }

    public static bool IsVoxelizedAt(Vector3 pos) => GetActiveZoneAt(pos) != null;

    public static void NotifyNeighbors(Heightmap hmap)
    {
        float size = hmap.m_width * hmap.m_scale;
        Vector3 c = hmap.transform.position;
        GetZone(Heightmap.FindHeightmap(c + new Vector3(size, 0f, 0f)))?.OnNeighborChanged();
        GetZone(Heightmap.FindHeightmap(c - new Vector3(size, 0f, 0f)))?.OnNeighborChanged();
        GetZone(Heightmap.FindHeightmap(c + new Vector3(0f, 0f, size)))?.OnNeighborChanged();
        GetZone(Heightmap.FindHeightmap(c - new Vector3(0f, 0f, size)))?.OnNeighborChanged();
    }

    public static void OnHeightmapDestroyed(Heightmap hmap) => RemoveZone(hmap, false);

    public static void OnHeightmapRegenerated(Heightmap hmap) => GetZone(hmap)?.Rebuild();

    public static void ForgetComp(TerrainComp comp)
    {
        LastRev.Remove(comp);
        FailedAttempts.Remove(comp);
    }

    public static bool SuppressGroundClamp(Vector3 pos)
    {
        VoxelZone? zone = GetZone(Heightmap.FindHeightmap(pos));
        return zone != null && pos.y > zone.Origin.y + 1f;
    }

    public static bool HasSavedOpsAt(Vector3 pos)
    {
        TerrainComp? comp = TerrainComp.FindTerrainCompiler(pos);
        if (comp == null || comp.m_nview == null || !comp.m_nview.IsValid())
            return false;
        byte[]? bytes = comp.m_nview.GetZDO().GetByteArray(CarveData.ZdoKey);
        return bytes != null && bytes.Length > 8;
    }

    public static bool IsMeshPendingAt(Vector3 pos)
    {
        if (GetActiveZoneAt(pos) != null)
            return false;
        return Heightmap.GetHeight(pos, out float surface) && pos.y < surface && pos.y > surface - HearthBelowPlugin.CaveDepth.Value - 1f && HasSavedOpsAt(pos);
    }

    public static bool SuppressTerrainCheck(Vector3 pos)
    {
        return SuppressGroundClamp(pos) || IsMeshPendingAt(pos);
    }

    // watches the ZDO DataRevision for unseen carve ops - both the zone-load path and live sync
    public static void PollComp(TerrainComp comp)
    {
        ZNetView nview = comp.m_nview;
        if (nview == null || !nview.IsValid())
            return;
        GetZone(comp.m_hmap)?.Pump();
        uint rev = nview.GetZDO().DataRevision;
        if (LastRev.TryGetValue(comp, out uint last) && last == rev)
            return;
        if (TryApplyData(comp))
        {
            LastRev[comp] = rev;
            FailedAttempts.Remove(comp);
        }
        else
        {
            int attempts = FailedAttempts.TryGetValue(comp, out int c) ? c + 1 : 1;
            FailedAttempts[comp] = attempts;
            if (attempts <= MaxApplyAttempts) return;
            HearthBelowPlugin.HearthBelowLogger.LogWarning($"Giving up applying voxel data for zone at {comp.transform.position} (heightmap never became ready)");
            LastRev[comp] = rev;
            FailedAttempts.Remove(comp);
        }
    }

    private static bool TryApplyData(TerrainComp comp)
    {
        Heightmap? hmap = comp.m_hmap;
        if (hmap == null)
            return false;
        byte[]? bytes = comp.m_nview.GetZDO().GetByteArray(CarveData.ZdoKey);
        List<CarveOp>? ops = CarveData.Deserialize(bytes);
        VoxelZone? zone = GetZone(hmap);
        if (ops == null || ops.Count == 0)
        {
            if (zone != null)
                RemoveZone(hmap, true);
            return true;
        }

        if (zone != null)
        {
            // a locally applied op vanished remotely? start over from a clean zone
            HashSet<int> incoming = [];
            foreach (CarveOp op in ops)
                incoming.Add(op.Id);
            bool removed = false;
            foreach (int id in zone.AppliedIds)
            {
                if (incoming.Contains(id)) continue;
                removed = true;
                break;
            }

            if (removed)
            {
                RemoveZone(hmap, true);
                zone = null;
            }
        }

        zone ??= EnsureZone(hmap);
        if (zone == null)
            return false;

        long replayStart = Stopwatch.GetTimestamp();
        int applied = 0;
        foreach (CarveOp op in ops)
        {
            if (!zone.ApplyOp(op)) continue;
            ++applied;
            ClutterSystem.instance?.ResetGrass(op.Point, op.Radius + 2f);
            EjectCharacters(op);
        }

        if (applied > 0)
            zone.RemeshDirty();
        if (applied > 1)
            HearthBelowPlugin.HearthBelowLogger.LogDebug($"Replayed {applied}/{ops.Count} saved dig op(s) for zone at {comp.transform.position} in {ElapsedMs(replayStart):F1} ms");
        return true;
    }

    public static bool CarveAt(Vector3 point, float radius) => CarveAt(point, radius, null);

    // Gradual mode + digDir = shallow oriented scoop, otherwise full radius in one go. A finite
    // toolDepthCap protects everything below surface-cap meters, leaving a flat floor.
    public static bool CarveAt(Vector3 point, float radius, Vector3? digDir, bool quiet = false, float toolDepthCap = float.PositiveInfinity)
    {
        radius = Mathf.Clamp(radius, MinDigRadius, MaxDigRadius);
        float floorY = float.NegativeInfinity;
        if (!float.IsPositiveInfinity(toolDepthCap) && Heightmap.GetHeight(point, out float surface))
            floorY = surface - Mathf.Max(1f, toolDepthCap);

        CarveOp op;
        if (digDir.HasValue && HearthBelowPlugin.DigMode.Value == HearthBelowPlugin.DigStyle.Gradual)
        {
            op = new CarveOp
            {
                Id = NewOpId(),
                Type = (byte)VoxelOpType.Scoop,
                Shape = ConfiguredShape(),
                Point = point,
                Radius = radius,
                Dir = digDir.Value.normalized,
                Depth = Mathf.Clamp(HearthBelowPlugin.DigDepthPerHit.Value, 0.25f, 2f),
                FloorY = floorY
            };
        }
        else
        {
            op = new CarveOp
            {
                Id = NewOpId(),
                Type = (byte)VoxelOpType.Carve,
                Shape = ConfiguredShape(),
                Point = point,
                Radius = radius,
                FloorY = floorY
            };
        }

        string? message = null;
        if (quiet) return ApplyPlayerOp(op, message);

        bool capBinding = !float.IsNegativeInfinity(floorY) && point.y - radius < floorY + 0.5f;
        message = capBinding ? "$hearthbelow_pickaxe_depth_limit" : "$hearthbelow_ground_too_hard";

        return ApplyPlayerOp(op, message);
    }

    public static bool FillAt(Vector3 point, float radius)
    {
        return ApplyPlayerOp(new CarveOp
        {
            Id = NewOpId(),
            Type = (byte)VoxelOpType.Fill,
            Shape = ConfiguredShape(),
            Point = point,
            Radius = Mathf.Clamp(radius, MinDigRadius, MaxDigRadius),
            FloorY = float.NegativeInfinity
        }, null);
    }

    public static bool FlattenAt(Vector3 point, float radius)
    {
        return ApplyPlayerOp(new CarveOp
        {
            Id = NewOpId(),
            Type = (byte)VoxelOpType.Flatten,
            Point = point,
            Radius = Mathf.Clamp(radius, MinPlaneRadius, MaxPlaneRadius),
            FloorY = float.NegativeInfinity
        }, null);
    }

    public static bool SmoothAt(Vector3 point, float radius)
    {
        return ApplyPlayerOp(new CarveOp
        {
            Id = NewOpId(),
            Type = (byte)VoxelOpType.Smooth,
            Point = point,
            Radius = Mathf.Clamp(radius, MinPlaneRadius, MaxPlaneRadius),
            FloorY = float.NegativeInfinity
        }, null);
    }

    private static int NewOpId() => Random.Range(int.MinValue, int.MaxValue);

    private static byte ConfiguredShape()
    {
        return (byte)(HearthBelowPlugin.CarveShape.Value == HearthBelowPlugin.DigShape.Cube ? VoxelOpShape.Cube : VoxelOpShape.Sphere);
    }

    public static bool IsUndergroundAt(Vector3 pos)
    {
        if (GetActiveZoneAt(pos) == null)
            return false;
        return Heightmap.GetHeight(pos, out float height) && pos.y < height - CarvedGroundGap;
    }

    private static int _terrainMask;
    private static int TerrainMask => _terrainMask != 0 ? _terrainMask : _terrainMask = LayerMask.GetMask("terrain");

    public static bool IsInCaveAt(Vector3 pos, float minDepth = 1f)
    {
        if (GetActiveZoneAt(pos) == null)
            return false;
        if (!Heightmap.GetHeight(pos, out float surface) || pos.y > surface - Mathf.Max(1f, minDepth))
            return false;
        // cave ceilings face down, so an upward ray from chest height finds them
        return Physics.Raycast(pos + Vector3.up * 0.5f, Vector3.up, 200f, TerrainMask);
    }

    private static bool ApplyPlayerOp(CarveOp op, string? noEffectMessage)
    {
        if (!PrivateArea.CheckAccess(op.Point, op.Radius))
            return false;
        if (Location.IsInsideNoBuildLocation(op.Point))
            return false;
        TmpHmaps.Clear();
        Heightmap.FindHeightmap(op.Point, op.Radius + 1f, TmpHmaps);
        if (TmpHmaps.Count == 0)
            return false;
        bool anyEffect = false;
        foreach (Heightmap hmap in TmpHmaps)
        {
            VoxelZone? zone = EnsureZone(hmap);
            if (zone != null && !zone.WouldChange(op))
                continue;

            anyEffect = true;
            TerrainComp comp = hmap.GetAndCreateTerrainCompiler();
            if (comp == null)
                continue;
            bool persistedOrSent = false;
            ZNetView nview = comp.m_nview;
            if (nview != null && nview.IsValid())
            {
                if (nview.IsOwner())
                {
                    persistedOrSent = AppendAndSave(comp, op);
                }
                else
                {
                    ZPackage pkg = new();
                    op.Write(pkg);
                    nview.InvokeRPC("HearthBelow_Carve", pkg);
                    persistedOrSent = true;
                }
            }

            // never show locally what didn't make it into the persistent data
            if (persistedOrSent)
                ApplyLocal(hmap, op);
        }

        if (!anyEffect && noEffectMessage != null)
            Player.m_localPlayer?.Message(MessageHud.MessageType.Center, noEffectMessage);
        return anyEffect;
    }

    private static void ApplyLocal(Heightmap hmap, CarveOp op)
    {
        VoxelZone? zone = EnsureZone(hmap);
        if (zone == null)
            return; // heightmap not ready; the op will still arrive via the ZDO data sync
        if (zone.ApplyOp(op))
        {
            zone.RemeshDirty();
            ClutterSystem.instance?.ResetGrass(op.Point, op.Radius + 2f);
            EjectCharacters(op);
        }
    }

    // A fill materializes rock around the capsule and PhysX won't depenetrate a non-convex
    // MeshCollider - congrats, you're entombed. Pop buried owned characters up to the surface.
    private static void EjectCharacters(CarveOp op)
    {
        if (op.Type == (byte)VoxelOpType.Carve || op.Type == (byte)VoxelOpType.Scoop)
            return; // removing material never buries anyone
        float range = op.Radius + 1f;
        foreach (Character ch in Character.GetAllCharacters())
        {
            if (ch == null || ch.m_nview == null || !ch.m_nview.IsValid() || !ch.m_nview.IsOwner())
                continue;
            Vector3 pos = ch.transform.position;
            if (Utils.DistanceXZ(pos, op.Point) > range)
                continue;
            VoxelZone? zone = GetActiveZoneAt(pos);
            if (zone == null || !zone.SampleSolid(pos + Vector3.up * 0.3f))
                continue;
            float top = Mathf.Max(pos.y, op.Point.y) + range + 2f;
            float y = pos.y;
            while (y < top && zone.SampleSolid(new Vector3(pos.x, y + 0.3f, pos.z)))
                y += 0.25f;
            if (y >= top)
                continue; // no air above, let the vanilla systems sort it out
            Vector3 lifted = new(pos.x, y + 0.05f, pos.z);
            ch.transform.position = lifted;
            if (ch.m_body != null)
            {
                ch.m_body.position = lifted;
                ch.m_body.linearVelocity = Vector3.zero;
            }

            ch.m_maxAirAltitude = lifted.y; // the lift isn't a fall
        }
    }

    // TODO: reserializes the whole op list per carve, delta-sync if it ever matters
    private static bool AppendAndSave(TerrainComp comp, CarveOp op)
    {
        ZDO zdo = comp.m_nview.GetZDO();
        List<CarveOp> ops = CarveData.Deserialize(zdo.GetByteArray(CarveData.ZdoKey)) ?? [];
        foreach (CarveOp existing in ops)
            if (existing.Id == op.Id)
                return true;
        if (ops.Count >= HearthBelowPlugin.MaxOpsPerZone.Value)
        {
            HearthBelowPlugin.HearthBelowLogger.LogWarning("Carve limit reached for this zone");
            return false;
        }

        ops.Add(op);
        zdo.Set(CarveData.ZdoKey, CarveData.Serialize(ops));
        return true;
    }

    public static void RPC_Carve(TerrainComp comp, long sender, ZPackage pkg)
    {
        if (comp == null)
            return;
        ZNetView nview = comp.m_nview;
        if (nview == null || !nview.IsValid() || !nview.IsOwner())
            return;
        CarveOp op = CarveOp.Read(pkg);
        op.Radius = Mathf.Clamp(op.Radius, MinDigRadius, op.Type == (byte)VoxelOpType.Flatten || op.Type == (byte)VoxelOpType.Smooth ? MaxPlaneRadius : MaxDigRadius);
        if (!AppendAndSave(comp, op))
            return;
        if (comp.m_hmap != null)
            ApplyLocal(comp.m_hmap, op);
    }

    public static void RPC_Clear(TerrainComp comp, long sender)
    {
        if (comp == null)
            return;
        ZNetView nview = comp.m_nview;
        if (nview == null || !nview.IsValid() || !nview.IsOwner())
            return;
        nview.GetZDO().Set(CarveData.ZdoKey, []);
    }

    // Clears this zone AND the same ops from neighbors - border-straddling ops live on both
    // sides, and leaving half behind makes the seam look like a landslide.
    public static void RequestClear(Heightmap hmap)
    {
        TerrainComp comp = TerrainComp.FindTerrainCompiler(hmap.transform.position);
        if (comp == null)
            return;
        ZNetView nview = comp.m_nview;
        if (nview == null || !nview.IsValid())
            return;
        HashSet<int> ids = [];
        List<CarveOp>? ops = CarveData.Deserialize(nview.GetZDO().GetByteArray(CarveData.ZdoKey));
        if (ops != null)
            foreach (CarveOp op in ops)
                ids.Add(op.Id);
        if (nview.IsOwner())
            nview.GetZDO().Set(CarveData.ZdoKey, []);
        else
            nview.InvokeRPC("HearthBelow_Clear");

        if (ids.Count == 0)
            return;
        float size = hmap.m_width * hmap.m_scale;
        Vector3 c = hmap.transform.position;
        for (int dx = -1; dx <= 1; ++dx)
        {
            for (int dz = -1; dz <= 1; ++dz)
            {
                if (dx == 0 && dz == 0)
                    continue;
                TerrainComp neighbor = TerrainComp.FindTerrainCompiler(c + new Vector3(dx * size, 0f, dz * size));
                if (neighbor != null)
                    RequestRemoveOps(neighbor, ids);
            }
        }
    }

    private static void RequestRemoveOps(TerrainComp comp, HashSet<int> ids)
    {
        ZNetView nview = comp.m_nview;
        if (nview == null || !nview.IsValid())
            return;
        if (nview.IsOwner())
        {
            RemoveOps(comp, ids);
            return;
        }

        ZPackage pkg = new();
        pkg.Write(ids.Count);
        foreach (int id in ids)
            pkg.Write(id);
        nview.InvokeRPC("HearthBelow_RemoveOps", pkg);
    }

    private static void RemoveOps(TerrainComp comp, HashSet<int> ids)
    {
        ZDO zdo = comp.m_nview.GetZDO();
        List<CarveOp>? ops = CarveData.Deserialize(zdo.GetByteArray(CarveData.ZdoKey));
        if (ops == null)
            return;
        if (ops.RemoveAll(op => ids.Contains(op.Id)) == 0)
            return;
        zdo.Set(CarveData.ZdoKey, ops.Count == 0 ? [] : CarveData.Serialize(ops));
    }

    public static void RPC_RemoveOps(TerrainComp comp, long sender, ZPackage pkg)
    {
        if (comp == null)
            return;
        ZNetView nview = comp.m_nview;
        if (nview == null || !nview.IsValid() || !nview.IsOwner())
            return;
        int count = pkg.ReadInt();
        if (count <= 0 || count > HearthBelowPlugin.MaxOpsPerZone.Value)
            return;
        HashSet<int> ids = [];
        for (int i = 0; i < count; ++i)
            ids.Add(pkg.ReadInt());
        RemoveOps(comp, ids);
    }

    public static string GetInfo(Vector3 pos)
    {
        Heightmap hmap = Heightmap.FindHeightmap(pos);
        if (hmap == null)
            return "No heightmap at this position.";
        VoxelZone? zone = GetZone(hmap);
        return zone == null ? $"Zone not voxelized. {Zones.Count} zone(s) voxelized in total." : $"Zone voxelized: {zone.Ops.Count} carve op(s), grid {zone.NX}x{zone.NY}x{zone.NZ}. {Zones.Count} zone(s) voxelized in total.";
    }
}