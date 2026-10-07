// VMD 二进制格式解析。本文件保持纯 .NET，不引用 Dalamud/ECommons，
// 以便 FFMMD.Test 控制台工程直接编译同源文件做离线验证。
using System.Numerics;
using System.Text;

namespace FFMMD.Vmd;

/// <summary> VMD 贝塞尔插值控制点（MMD 把 0..127 的控制值存成字节）。 </summary>
public sealed class VmdBezier
{
    public byte P1x, P2x, P1y, P2y;

    public static VmdBezier From(byte p1x, byte p2x, byte p1y, byte p2y) => new() { P1x = p1x, P2x = p2x, P1y = p1y, P2y = p2y };
}

public sealed class VmdBoneKeyFrame
{
    public string Bone = "";
    public uint Frame;
    public Vector3 Position;
    public Quaternion Rotation = Quaternion.Identity;

    /// <summary> 64 字节原始插值数据（X/Y/Z/R 四条贝塞尔曲线的规范布局见 VmdParser.ParseInterp）。 </summary>
    public byte[] Interp = new byte[64];

    /// <summary> 预解析的四条插值曲线（BoneTrack.Sort 时填充），播放热路径零分配。 </summary>
    public VmdBezierCurve? CX, CY, CZ, CR;

    public VmdBezier CurveX => ParseInterp(Interp, 0);
    public VmdBezier CurveY => ParseInterp(Interp, 1);
    public VmdBezier CurveZ => ParseInterp(Interp, 2);
    public VmdBezier CurveR => ParseInterp(Interp, 3);

    /// <summary> 四行依次为 X/Y/Z/R 的 x1、y1、x2、y2；后 48 字节是冗余布局。 </summary>
    public static VmdBezier ParseInterp(byte[] data, int curve)
    {
        if (data.Length < 16) throw new InvalidDataException("骨骼插值数据不足 16 字节。");
        curve = Math.Clamp(curve, 0, 3);
        return new VmdBezier
        {
            P1x = data[curve],
            P1y = data[curve + 4],
            P2x = data[curve + 8],
            P2y = data[curve + 12],
        };
    }
}

public sealed class VmdMorphKeyFrame
{
    public string Name = "";
    public uint Frame;
    public float Weight;
}

public sealed class VmdCameraKeyFrame
{
    public uint Frame;
    public float Distance;
    public Vector3 Position;
    public Vector3 Rotation; // XYZ Euler angles in radians; camera VMD does not store a quaternion.
    public byte[] Interp = new byte[24];
    public uint ViewAngle;
    public bool Perspective;
}

public sealed class VmdShowIkFrame
{
    public uint Frame;
    public bool Show;
    public List<(string Name, bool Enabled)> Ik = [];
}

public sealed class VmdFile
{
    public string Header = "";
    public string ModelName = "";
    public List<VmdBoneKeyFrame> BoneFrames = [];
    public List<VmdMorphKeyFrame> MorphFrames = [];
    public List<VmdCameraKeyFrame> CameraFrames = [];
    public List<VmdShowIkFrame> ShowIkFrames = [];

    public static VmdFile Parse(string path) => Parse(File.ReadAllBytes(path));

    public static VmdFile Parse(byte[] bytes)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var vmd = new VmdFile();
        using var ms = new MemoryStream(bytes);
        using var r = new BinaryReader(ms);

        vmd.Header = ReadFixedString(r, 30);
        // "Vocaloid Motion Data 0002"（旧格式 "Vocaloid Motion Data file" 的模型名只有 10 字节）。
        var modelNameLen = vmd.Header switch
        {
            "Vocaloid Motion Data 0002" => 20,
            "Vocaloid Motion Data file" => 10,
            _ => throw new InvalidDataException("不是支持的 VMD 文件：头标识错误。"),
        };
        vmd.ModelName = ReadFixedString(r, modelNameLen);

        // 骨骼关键帧：count × 111 字节（名15 + 帧4 + 位置12 + 四元数16 + 插值64）。
        var boneCount = ReadCount(r, 111, "骨骼");
        for (var i = 0; i < boneCount; i++)
        {
            var kf = new VmdBoneKeyFrame
            {
                Bone = ReadFixedString(r, 15),
                Frame = r.ReadUInt32(),
                Position = ReadVector(r),
                Rotation = ReadRotation(r),
            };
            r.BaseStream.ReadExactly(kf.Interp);
            vmd.BoneFrames.Add(kf);
        }

        // 表情（morph）关键帧：count × 23 字节。
        if (!HasSection(r)) return vmd;
        var morphCount = ReadCount(r, 23, "表情");
        for (var i = 0; i < morphCount; i++)
        {
            vmd.MorphFrames.Add(new VmdMorphKeyFrame
            {
                Name = ReadFixedString(r, 15),
                Frame = r.ReadUInt32(),
                Weight = ReadFloat(r),
            });
        }

        // 相机关键帧：count × 61 字节。
        if (!HasSection(r)) return vmd;
        var cameraCount = ReadCount(r, 61, "相机");
        for (var i = 0; i < cameraCount; i++)
        {
            var kf = new VmdCameraKeyFrame
            {
                Frame = r.ReadUInt32(),
                Distance = ReadFloat(r),
                Position = ReadVector(r),
                Rotation = ReadVector(r),
            };
            r.BaseStream.ReadExactly(kf.Interp);
            kf.ViewAngle = r.ReadUInt32();
            kf.Perspective = r.ReadByte() == 0;
            vmd.CameraFrames.Add(kf);
        }

        // 照明：count × 28 字节（帧4 + RGB12 + 位置12）——跳过。
        if (!HasSection(r)) return vmd;
        var lightCount = ReadCount(r, 28, "照明");
        r.BaseStream.Position += (long)lightCount * 28;

        // 自阴影：帧4 + 模式1 + 距离4 = 9 字节。
        if (!HasSection(r)) return vmd;
        var shadowCount = ReadCount(r, 9, "自阴影");
        r.BaseStream.Position += (long)shadowCount * 9;

        // 显示/IK：帧4 + 可见性1 + 数量4 + 每项(名称20 + 开关1)。
        if (HasSection(r))
        {
            var ikFrameCount = ReadCount(r, 9, "显示 IK");
            for (var i = 0; i < ikFrameCount; i++)
            {
                Require(r, 9);
                var frame = new VmdShowIkFrame { Frame = r.ReadUInt32(), Show = r.ReadByte() != 0 };
                var n = ReadCount(r, 21, "IK 开关");
                for (var j = 0; j < n; j++)
                {
                    var name = ReadFixedString(r, 20);
                    frame.Ik.Add((name, r.ReadByte() != 0));
                }
                vmd.ShowIkFrames.Add(frame);
            }
        }

        if (r.BaseStream.Position != r.BaseStream.Length)
            throw new InvalidDataException("VMD 尾段含未识别数据，文件可能损坏。");

        return vmd;
    }

    private static bool HasSection(BinaryReader r)
    {
        if (r.BaseStream.Position == r.BaseStream.Length) return false;
        Require(r, 4);
        return true;
    }

    private static void Require(BinaryReader r, long bytes)
    {
        if (bytes < 0 || r.BaseStream.Length - r.BaseStream.Position < bytes)
            throw new InvalidDataException($"VMD 数据截断（偏移 {r.BaseStream.Position}）。");
    }

    private static uint ReadCount(BinaryReader r, int recordSize, string section)
    {
        Require(r, 4);
        var count = r.ReadUInt32();
        if (count > int.MaxValue || (long)count * recordSize > r.BaseStream.Length - r.BaseStream.Position)
            throw new InvalidDataException($"VMD {section}计数超出剩余数据范围。");
        return count;
    }

    private static float ReadFloat(BinaryReader r)
    {
        var value = r.ReadSingle();
        if (!float.IsFinite(value)) throw new InvalidDataException("VMD 包含非有限数值。");
        return value;
    }

    private static Vector3 ReadVector(BinaryReader r) => new(ReadFloat(r), ReadFloat(r), ReadFloat(r));

    private static Quaternion ReadRotation(BinaryReader r)
    {
        var q = new Quaternion(ReadFloat(r), ReadFloat(r), ReadFloat(r), ReadFloat(r));
        var max = MathF.Max(MathF.Max(MathF.Abs(q.X), MathF.Abs(q.Y)), MathF.Max(MathF.Abs(q.Z), MathF.Abs(q.W)));
        return max == 0 ? Quaternion.Identity : Quaternion.Normalize(new Quaternion(q.X / max, q.Y / max, q.Z / max, q.W / max));
    }

    private static string ReadFixedString(BinaryReader r, int len)
    {
        Require(r, len);
        var raw = r.ReadBytes(len);
        var end = Array.IndexOf(raw, (byte)0);
        if (end < 0) end = raw.Length;
        return end == 0 ? "" : Encoding.GetEncoding(932).GetString(raw, 0, end);
    }
}
