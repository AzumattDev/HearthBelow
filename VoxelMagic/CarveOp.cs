using System.Collections.Generic;
using UnityEngine;

namespace HearthBelow.VoxelMagic;

public enum VoxelOpType : byte
{
    Carve = 0, // remove material ("blast" digging)
    Fill = 1, // hoe raise underground
    Flatten = 2, // hoe level: fill below the op height, carve headroom above
    Scoop = 3, // gradual dig: shallow bite, Radius wide, Depth deep along Dir
    Smooth = 4, // hoe smooth: blend the floor toward the op plane
    Raise = 5 // hoe raise underground: per-column vanilla RaiseTerrain math (Depth = raiseDelta, Power = raisePower)
}

public enum VoxelOpShape : byte
{
    Sphere = 0,
    Cube = 1
}

// Idempotent (dedup by id), applied in ZDO order. Shape is baked in - clients with different
// configs must still replay identical geometry.
public struct CarveOp
{
    public int Id;
    public byte Type;
    public byte Shape;
    public Vector3 Point;
    public float Radius;
    public Vector3 Dir; // scoop only: approach direction of the dig
    public float Depth; // scoop: bite depth along Dir; raise: per-swing height delta (vanilla m_raiseDelta)
    public float Power; // raise only: falloff exponent (vanilla m_raisePower)
    public float FloorY; // carve/scoop: protected floor (world y) from tool depth caps; -Infinity = uncapped

    public void Write(ZPackage pkg)
    {
        pkg.Write(Id);
        pkg.Write(Type);
        pkg.Write(Shape);
        pkg.Write(Point);
        pkg.Write(Radius);
        pkg.Write(FloorY);
        if (Type == (byte)VoxelOpType.Scoop)
        {
            pkg.Write(Dir);
            pkg.Write(Depth);
        }
        else if (Type == (byte)VoxelOpType.Raise)
        {
            pkg.Write(Depth);
            pkg.Write(Power);
        }
    }

    public static CarveOp Read(ZPackage pkg)
    {
        CarveOp op = new()
        {
            Id = pkg.ReadInt(),
            Type = pkg.ReadByte(),
            Shape = pkg.ReadByte(),
            Point = pkg.ReadVector3(),
            Radius = pkg.ReadSingle(),
            FloorY = pkg.ReadSingle()
        };
        if (op.Type == (byte)VoxelOpType.Scoop)
        {
            op.Dir = pkg.ReadVector3();
            op.Depth = pkg.ReadSingle();
        }
        else if (op.Type == (byte)VoxelOpType.Raise)
        {
            op.Depth = pkg.ReadSingle();
            op.Power = pkg.ReadSingle();
        }

        return op;
    }

    private static CarveOp ReadV1(ZPackage pkg)
    {
        return new CarveOp
        {
            Id = pkg.ReadInt(),
            Type = (byte)VoxelOpType.Carve,
            Shape = (byte)VoxelOpShape.Sphere,
            Point = pkg.ReadVector3(),
            Radius = pkg.ReadSingle(),
            FloorY = float.NegativeInfinity
        };
    }

    private static CarveOp ReadV2(ZPackage pkg)
    {
        return new CarveOp
        {
            Id = pkg.ReadInt(),
            Type = pkg.ReadByte(),
            Shape = (byte)VoxelOpShape.Sphere,
            Point = pkg.ReadVector3(),
            Radius = pkg.ReadSingle(),
            FloorY = float.NegativeInfinity
        };
    }

    private static CarveOp ReadV3(ZPackage pkg)
    {
        // v3 predates the Scoop type, so no op carries Dir/Depth
        return new CarveOp
        {
            Id = pkg.ReadInt(),
            Type = pkg.ReadByte(),
            Shape = pkg.ReadByte(),
            Point = pkg.ReadVector3(),
            Radius = pkg.ReadSingle(),
            FloorY = float.NegativeInfinity
        };
    }

    private static CarveOp ReadV4(ZPackage pkg)
    {
        // v4 predates tool depth caps, so no op carries a protected floor
        CarveOp op = new()
        {
            Id = pkg.ReadInt(),
            Type = pkg.ReadByte(),
            Shape = pkg.ReadByte(),
            Point = pkg.ReadVector3(),
            Radius = pkg.ReadSingle(),
            FloorY = float.NegativeInfinity
        };
        if (op.Type != (byte)VoxelOpType.Scoop) return op;
        op.Dir = pkg.ReadVector3();
        op.Depth = pkg.ReadSingle();

        return op;
    }
    
    private static CarveOp ReadV5(ZPackage pkg)
    {
        // v5 predates the Raise type, so scoop is the only op with extra fields
        CarveOp op = new()
        {
            Id = pkg.ReadInt(),
            Type = pkg.ReadByte(),
            Shape = pkg.ReadByte(),
            Point = pkg.ReadVector3(),
            Radius = pkg.ReadSingle(),
            FloorY = pkg.ReadSingle()
        };
        if (op.Type == (byte)VoxelOpType.Scoop)
        {
            op.Dir = pkg.ReadVector3();
            op.Depth = pkg.ReadSingle();
        }

        return op;
    }

    public static CarveOp Read(ZPackage pkg, int version)
    {
        if (version >= 6)
            return Read(pkg);
        if (version == 5)
            return ReadV5(pkg);
        if (version == 4)
            return ReadV4(pkg);
        if (version == 3)
            return ReadV3(pkg);
        return version == 2 ? ReadV2(pkg) : ReadV1(pkg);
    }
}

// Per-zone op list compressed onto the TerrainComp ZDO - save persistence and client sync for free.
public static class CarveData
{
    public const int Version = 6;
    public static readonly int ZdoKey = "HearthBelow_VoxelOps".GetStableHashCode();

    public static byte[] Serialize(List<CarveOp> ops)
    {
        ZPackage pkg = new();
        pkg.Write(Version);
        pkg.Write(ops.Count);
        foreach (CarveOp op in ops)
            op.Write(pkg);
        return Utils.Compress(pkg.GetArray());
    }

    public static List<CarveOp>? Deserialize(byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0)
            return null;
        try
        {
            ZPackage pkg = new(Utils.Decompress(bytes));
            int version = pkg.ReadInt();
            if (version > Version)
            {
                HearthBelowPlugin.HearthBelowLogger.LogWarning($"Voxel data version {version} is newer than this mod supports, ignoring");
                return null;
            }

            int count = pkg.ReadInt();
            List<CarveOp> ops = new(count);
            for (int i = 0; i < count; ++i)
                ops.Add(CarveOp.Read(pkg, version));
            return ops;
        }
        catch (System.Exception e)
        {
            HearthBelowPlugin.HearthBelowLogger.LogWarning($"Failed to parse voxel data: {e.Message}");
            return null;
        }
    }
}