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

// configs must still replay identical geometry.
public struct CarveOp
{
    public readonly struct Key : System.IEquatable<Key>
    {
        private readonly int _id;
        private readonly float _x, _y, _z;

        public Key(int id, Vector3 point)
        {
            _id = id;
            _x = point.x;
            _y = point.y;
            _z = point.z;
        }

        public bool Equals(Key other) => _id == other._id && _x == other._x && _y == other._y && _z == other._z;

        public override bool Equals(object? obj) => obj is Key other && Equals(other);

        public override int GetHashCode()
        {
            int h = _id;
            h = (h * 397) ^ _x.GetHashCode();
            h = (h * 397) ^ _y.GetHashCode();
            h = (h * 397) ^ _z.GetHashCode();
            return h;
        }
    }

    public Key DedupKey => new(Id, Point);

    public int Id;

    public long Seq;
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
        pkg.Write(Seq);
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

    public static CarveOp Read(ZPackage pkg) => Read(pkg, CarveData.Version);

    public static CarveOp Read(ZPackage pkg, int version)
    {
        CarveOp op = new()
        {
            Id = pkg.ReadInt(),
            Seq = version >= 7 ? pkg.ReadLong() : 0L,
            Type = version >= 2 ? pkg.ReadByte() : (byte)VoxelOpType.Carve,
            Shape = version >= 3 ? pkg.ReadByte() : (byte)VoxelOpShape.Sphere,
            Point = pkg.ReadVector3(),
            Radius = pkg.ReadSingle(),
            FloorY = version >= 5 ? pkg.ReadSingle() : float.NegativeInfinity
        };
        if (op.Type == (byte)VoxelOpType.Scoop && version >= 4)
        {
            op.Dir = pkg.ReadVector3();
            op.Depth = pkg.ReadSingle();
        }
        else if (op.Type == (byte)VoxelOpType.Raise && version >= 6)
        {
            op.Depth = pkg.ReadSingle();
            op.Power = pkg.ReadSingle();
        }

        return op;
    }
}

public enum CarveDataState
{
    Empty,
    Ok,

    Unreadable
}

// Per-zone op list compressed onto the TerrainComp ZDO - save persistence and client sync for free.
public static class CarveData
{
    public const int Version = 7;
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

    private static readonly HashSet<int> WarnedPayloads = [];

    public static CarveDataState Read(byte[]? bytes, out List<CarveOp> ops)
    {
        ops = [];
        if (bytes == null || bytes.Length == 0)
            return CarveDataState.Empty;
        try
        {
            ZPackage pkg = new(Utils.Decompress(bytes));
            int version = pkg.ReadInt();
            if (version > Version)
            {
                WarnOnce(bytes, $"Voxel data version {version} is newer than this build supports. "
                                + "Leaving this zone's data untouched - digging here is disabled until you run a build that understands it.");
                return CarveDataState.Unreadable;
            }

            int count = pkg.ReadInt();
            List<CarveOp> read = new(count);
            for (int i = 0; i < count; ++i)
                read.Add(CarveOp.Read(pkg, version));
            ops = read;
            return CarveDataState.Ok;
        }
        catch (System.Exception e)
        {
            WarnOnce(bytes, $"Failed to parse voxel data ({e.Message}). Leaving this zone's data untouched.");
            return CarveDataState.Unreadable;
        }
    }

    private static void WarnOnce(byte[] payload, string message)
    {
        int key = payload.Length;
        for (int i = 0; i < payload.Length; i += 64)
            key = key * 31 + payload[i];
        if (WarnedPayloads.Add(key))
            HearthBelowPlugin.HearthBelowLogger.LogWarning(message);
    }
}