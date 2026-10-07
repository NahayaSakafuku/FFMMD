// FFMMD 离线测试台：
//   dotnet run --project FFMMD.Test -- <某个.vmd 路径>   → 解析真实文件并输出报告
//   dotnet run --project FFMMD.Test                       → 合成 VMD 往返 + 贝塞尔曲线自测
using System.Numerics;
using System.Text;
using FFMMD.Vmd;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

if (args.Length > 0 && args[0] != "--verify")
{
    ReportRealFile(args[0]);
    return 0;
}

var failures = 0;

// ───────────────────────── 合成 VMD 往返测试 ─────────────────────────
var file = new VmdFile
{
    Header = "Vocaloid Motion Data 0002",
    ModelName = "テストモデル",
    BoneFrames =
    [
        Kf("センター", 0, new Vector3(0f, 10f, 0f), Quaternion.Identity, Linear()),
        Kf("センター", 30, new Vector3(2f, 10f, 4f), Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2), Linear()),
        Kf("左腕", 0, Vector3.Zero, Quaternion.Identity, Linear()),
        Kf("左腕", 15, Vector3.Zero, new Quaternion(0, 0, 0.38268f, 0.92388f), EaseIn()),
    ],
    MorphFrames =
    [
        new VmdMorphKeyFrame { Name = "ウィンク", Frame = 0, Weight = 0f },
        new VmdMorphKeyFrame { Name = "ウィンク", Frame = 15, Weight = 1f },
    ],
};

var parsed = VmdFile.Parse(WriteVmd(file));

Check(parsed.Header.Contains("0002"), "header");
Check(parsed.ModelName == "テストモデル", $"模型名 Shift-JIS 解码: '{parsed.ModelName}'");
Check(parsed.BoneFrames.Count == 4, "骨骼关键帧数量");
Check(parsed.MorphFrames.Count == 2, "morph 关键帧数量");

var anim = VmdAnimation.Build(parsed);
Check(anim.Tracks.Count == 2, $"轨道数量 = {anim.Tracks.Count}");
Check(anim.MaxFrame == 30, "最大帧号");
Check(MathF.Abs(anim.DurationSec - 1f) < 1e-5, "时长 = 1 秒");

// 线性段：贝塞尔默认值应退化为 lerp
var (midPos, _) = anim.Tracks["センター"].Sample(15);
Check(Vector3.Distance(midPos, new Vector3(1f, 10f, 2f)) < 1e-3f, $"センター 中点线性插值 = {midPos}");
var (endPos, endRot) = anim.Tracks["センター"].Sample(30);
Check(Vector3.Distance(endPos, new Vector3(2f, 10f, 4f)) < 1e-4f, "センター 终点");
Check(MathF.Abs(endRot.Y - MathF.Sin(MathF.PI / 4)) < 1e-4f && MathF.Abs(endRot.W - MathF.Cos(MathF.PI / 4)) < 1e-4f, "センター 终点旋转 = 90° yaw");

// The destination key owns the interval curve: ease-in must differ from linear slerp.
var (_, armMid) = anim.Tracks["左腕"].Sample(7.5f);
var armCurve = new VmdBezierCurve(new VmdBezier { P1x = 107, P2x = 107, P1y = 20, P2y = 20 });
Check(MathF.Abs(armMid.W - MathF.Cos(MathF.PI / 8 * armCurve.Evaluate(0.5f))) < 1e-3f, $"左腕 终点曲线 slerp w = {armMid.W:0.00000}");

// 非线性贝塞尔：控制点拉向右侧（慢启动），中点应明显落后于线性
var ease = new VmdBezierCurve(new VmdBezier { P1x = 107, P2x = 107, P1y = 20, P2y = 20 });
var y = ease.Evaluate(0.5f);
Check(y < 0.42f, $"ease-in 曲线 Evaluate(0.5) = {y:0.000}（应 < 0.42）");
Check(MathF.Abs(ease.Evaluate(0f)) < 1e-4 && MathF.Abs(ease.Evaluate(1f) - 1f) < 1e-4, "曲线端点 0/1");
var lin = new VmdBezierCurve(new VmdBezier { P1x = 20, P2x = 107, P1y = 20, P2y = 107 });
Check(MathF.Abs(lin.Evaluate(0.3f) - 0.3f) < 1e-3f, "默认 20/107 曲线 ≈ 线性");

failures += RegressionSuite.Run(args.Length > 1 && args[0] == "--verify" ? args[1] : null);
failures += MusicRegression.Run();
Console.WriteLine(failures == 0 ? "\n全部通过 ✔" : $"\n{failures} 项失败 ✘");
return failures == 0 ? 0 : 1;

static VmdBoneKeyFrame Kf(string bone, uint frame, Vector3 pos, Quaternion rot, byte[] interp) => new()
{
    Bone = bone,
    Frame = frame,
    Position = pos,
    Rotation = Quaternion.Normalize(rot),
    Interp = interp,
};

// Independent canonical layout: rows x1, y1, x2, y2 for X/Y/Z/R.
static byte[] Linear()
{
    var b = new byte[64];
    for (var c = 0; c < 4; c++)
    {
        b[c] = 20; b[4 + c] = 20;
        b[8 + c] = 107; b[12 + c] = 107;
    }
    return b;
}

static byte[] EaseIn()
{
    var b = Linear();
    b[3] = 107; b[7] = 20; b[11] = 107; b[15] = 20;
    return b;
}

static byte[] WriteVmd(VmdFile f)
{
    using var ms = new MemoryStream();
    using var w = new BinaryWriter(ms);
    void Str(string s, int len)
    {
        var buf = new byte[len];
        Encoding.GetEncoding(932).GetBytes(s).CopyTo(buf, 0);
        w.Write(buf);
    }
    Str(f.Header, 30);
    Str(f.ModelName, 20);
    w.Write((uint)f.BoneFrames.Count);
    foreach (var k in f.BoneFrames)
    {
        Str(k.Bone, 15);
        w.Write(k.Frame);
        w.Write(k.Position.X);
        w.Write(k.Position.Y);
        w.Write(k.Position.Z);
        w.Write(k.Rotation.X);
        w.Write(k.Rotation.Y);
        w.Write(k.Rotation.Z);
        w.Write(k.Rotation.W);
        w.Write(k.Interp);
    }
    w.Write((uint)f.MorphFrames.Count);
    foreach (var k in f.MorphFrames)
    {
        Str(k.Name, 15);
        w.Write(k.Frame);
        w.Write(k.Weight);
    }
    w.Write(0u); // camera
    w.Write(0u); // light
    w.Write(0u); // shadow
    w.Write(0u); // showIK
    return ms.ToArray();
}

void Check(bool ok, string label)
{
    Console.WriteLine($"{(ok ? "  ✔" : "  ✘")} {label}");
    if (!ok) failures++;
}

void ReportRealFile(string p)
{
    var f = VmdFile.Parse(File.ReadAllBytes(p));
    var anim = VmdAnimation.Build(f);
    Console.WriteLine($"文件: {p}");
    Console.WriteLine($"头: {f.Header.Trim()}  模型: {f.ModelName}");
    Console.WriteLine($"骨骼关键帧: {f.BoneFrames.Count}  morph: {f.MorphFrames.Count}  相机: {f.CameraFrames.Count}");
    Console.WriteLine($"轨道数: {anim.Tracks.Count}  最大帧: {anim.MaxFrame}（{anim.DurationSec:0.00}s @30fps）");

    // 腿部驱动分析：谁有关键帧、谁在动。
    Console.WriteLine("\n腿部/躯干关键轨道(位置范围 + 旋转幅度):");
    string[] watch =
    [
        "センター", "グルーブ", "腰", "全ての親", "下半身", "上半身",
        "左足", "右足", "左足D", "右足D", "左ひざ", "右ひざ", "左ひざD", "右ひざD",
        "左足首", "右足首", "左足首D", "右足首D", "左足ＩＫ", "右足ＩＫ",
        "左つま先ＩＫ", "右つま先ＩＫ", "左足先EX", "右足先EX",
        "左腕", "右腕", "左ひじ", "右ひじ",
    ];
    foreach (var name in watch)
    {
        if (!anim.TryGetTrack(name, out var t)) continue;
        var (lo, hi, med) = TrackRange(t);
        var maxAngle = TrackMaxRotation(t);
        Console.WriteLine($"  {name,-8} {t.Keys.Count,6} KF  位置 X[{lo.X:+0.00;-0.00},{hi.X:+0.00;-0.00}] Y[{lo.Y:+0.00;-0.00},{hi.Y:+0.00;-0.00}] Z[{lo.Z:+0.00;-0.00},{hi.Z:+0.00;-0.00}]  最大旋转 {maxAngle,5:0.0}°");
    }

    // 表示IK：MMD 是否真的启用腿部 IK。
    if (f.ShowIkFrames.Count > 0)
    {
        var first = f.ShowIkFrames[0];
        Console.WriteLine($"\n表示IK帧: {f.ShowIkFrames.Count} 帧，首帧 Show={first.Show}");
        foreach (var (name, en) in first.Ik)
            Console.WriteLine($"  {name}: {(en ? "启用" : "关闭")}");
    }
    else Console.WriteLine("\n无表示IK帧段（默认全部启用）");

    // 旋转随时间分布：看前 30 秒腿部是否有动作、动作在哪些时段。
    Console.WriteLine("\n旋转角度随时间(每秒采样,进度秒: 左足/右足/左足首/下半身/左腕):");
    string[] angleWatch = ["左足", "右足", "左足首", "下半身", "左腕"];
    for (var sec = 0; sec <= 30; sec++)
    {
        var frame = sec * 30f;
        var parts = angleWatch.Select(n => anim.TryGetTrack(n, out var t) ? TrackMaxRotation(t, frame - 15f, frame + 15f).ToString("0") : "-");
        Console.WriteLine($"  {sec,3}s: {string.Join(" / ", parts)}");
    }

    Console.WriteLine("\n轨道（按关键帧数排序，前 20）:");
    foreach (var t in anim.Tracks.Values.OrderByDescending(t => t.Keys.Count).Take(20))
        Console.WriteLine($"  {t.Bone,-12} {t.Keys.Count,6} KF  [{t.StartFrame}..{t.EndFrame}]");
}

static float TrackMaxRotation(BoneTrack t, float from = -1f, float to = -1f)
{
    var max = 0f;
    foreach (var k in t.Keys)
    {
        if (to >= 0 && (k.Frame < Math.Max(from, 0) || k.Frame > to)) continue;
        var angle = 2f * MathF.Acos(Math.Clamp(MathF.Abs(k.Rotation.W), 0f, 1f)) * 180f / MathF.PI;
        if (angle > max) max = angle;
    }
    return max;
}

static (Vector3 Lo, Vector3 Hi, Vector3 Median) TrackRange(BoneTrack t)
{
    var lo = new Vector3(float.MaxValue);
    var hi = new Vector3(float.MinValue);
    var xs = new List<float>();
    var ys = new List<float>();
    var zs = new List<float>();
    for (var f = t.StartFrame; f <= t.EndFrame; f += 10f)
    {
        var (p, _) = t.Sample(f);
        lo = Vector3.Min(lo, p);
        hi = Vector3.Max(hi, p);
        xs.Add(p.X);
        ys.Add(p.Y);
        zs.Add(p.Z);
    }
    xs.Sort();
    ys.Sort();
    zs.Sort();
    var m = xs.Count / 2;
    return (lo, hi, new Vector3(xs[m], ys[m], zs[m]));
}
