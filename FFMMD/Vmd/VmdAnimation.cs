// VMD 时间轴采样。纯 .NET，无 Dalamud 依赖。
using System.Numerics;

namespace FFMMD.Vmd;

/// <summary> 单条贝塞尔插值曲线（0..1 参数域）。MMD 默认值 20/20/107/107 即线性。 </summary>
public sealed class VmdBezierCurve
{
    private const float Segments = 127f;

    /// <summary> 共享的线性曲线实例（曲线未预解析时的兜底）。 </summary>
    public static readonly VmdBezierCurve Linear = new(new VmdBezier { P1x = 20, P2x = 107, P1y = 20, P2y = 107 });

    private readonly float _x1, _y1, _x2, _y2;
    private readonly bool _linear;

    public VmdBezierCurve(VmdBezier b)
    {
        _x1 = Math.Clamp((int)b.P1x, 0, 127) / Segments;
        _x2 = Math.Clamp((int)b.P2x, 0, 127) / Segments;
        _y1 = Math.Clamp((int)b.P1y, 0, 127) / Segments;
        _y2 = Math.Clamp((int)b.P2y, 0, 127) / Segments;
        // P0/P3 固定在对角线端点上，两个控制点也都在对角线上时整条曲线就是 y=x，直接走 lerp。
        _linear = MathF.Abs(_x1 - _y1) < 1e-4f && MathF.Abs(_x2 - _y2) < 1e-4f;
    }

    /// <summary> 给定时间比例 u ∈ [0,1]，返回曲线的值比例 y ∈ [0,1]（解 x(t)=u 后代入 y(t)）。 </summary>
    public float Evaluate(float u)
    {
        u = Math.Clamp(u, 0f, 1f);
        if (_linear || u <= 0 || u >= 1) return u;
        return BezierY(SolveT(u));
    }

    private float SolveT(float u)
    {
        // 牛顿迭代，失败退回二分；VMD 控制点都在 [0,1] 单调域内，收敛很快。
        var t = u;
        for (var i = 0; i < 10; i++)
        {
            var x = BezierX(t) - u;
            if (MathF.Abs(x) < 1e-5f) return t;
            var d = BezierDerivX(t);
            if (MathF.Abs(d) < 1e-6f) break;
            t -= x / d;
            t = Math.Clamp(t, 0f, 1f);
        }
        var lo = 0f;
        var hi = 1f;
        t = u;
        for (var i = 0; i < 24; i++)
        {
            var x = BezierX(t);
            if (MathF.Abs(x - u) < 1e-5f) return t;
            if (x < u) lo = t;
            else hi = t;
            t = (lo + hi) * 0.5f;
        }
        return t;
    }

    private float BezierX(float t) => 3f * t * (1 - t) * (1 - t) * _x1 + 3f * t * t * (1 - t) * _x2 + t * t * t;
    private float BezierY(float t) => 3f * t * (1 - t) * (1 - t) * _y1 + 3f * t * t * (1 - t) * _y2 + t * t * t;
    private float BezierDerivX(float t) => 3f * (1 - t) * (1 - t) * _x1 + 6f * t * (1 - t) * (_x2 - _x1) + 3f * t * t * (1 - _x2);
}

public sealed class BoneTrack
{
    public string Bone = "";
    public List<VmdBoneKeyFrame> Keys = [];

    public float StartFrame => Keys.Count == 0 ? 0 : Keys[0].Frame;
    public float EndFrame => Keys.Count == 0 ? 0 : Keys[^1].Frame;

    public void Sort()
    {
        // Stable last-record-wins handling for duplicate frame numbers.
        Keys = Keys.OrderBy(k => k.Frame).GroupBy(k => k.Frame).Select(g => g.Last()).ToList();
        // 预解析贝塞尔曲线：播放热路径每帧要采样几十条轨道，不能每次 new。
        foreach (var k in Keys)
        {
            k.CX = new VmdBezierCurve(k.CurveX);
            k.CY = new VmdBezierCurve(k.CurveY);
            k.CZ = new VmdBezierCurve(k.CurveZ);
            k.CR = new VmdBezierCurve(k.CurveR);
        }
    }

    /// <summary> 采样某帧：位置按 X/Y/Z 各自曲线插值，旋转按 R 曲线做 slerp。零分配。 </summary>
    public (Vector3 Pos, Quaternion Rot) Sample(float frame)
    {
        if (!float.IsFinite(frame)) throw new ArgumentOutOfRangeException(nameof(frame));
        if (Keys.Count == 0) return (Vector3.Zero, Quaternion.Identity);
        if (Keys.Count == 1 || frame <= Keys[0].Frame) return (Keys[0].Position, Keys[0].Rotation);
        var last = Keys[^1];
        if (frame >= last.Frame) return (last.Position, last.Rotation);

        // 二分定位区间 [k0, k1]。
        var lo = 0;
        var hi = Keys.Count - 1;
        while (hi - lo > 1)
        {
            var mid = (lo + hi) / 2;
            if (Keys[mid].Frame <= frame) lo = mid;
            else hi = mid;
        }
        var a = Keys[lo];
        var b = Keys[hi];
        var span = b.Frame - a.Frame;
        var u = span <= 0 ? 0f : (frame - a.Frame) / span;
        var uX = (b.CX ?? VmdBezierCurve.Linear).Evaluate(u);
        var uY = (b.CY ?? VmdBezierCurve.Linear).Evaluate(u);
        var uZ = (b.CZ ?? VmdBezierCurve.Linear).Evaluate(u);
        var uR = (b.CR ?? VmdBezierCurve.Linear).Evaluate(u);
        return (
            new Vector3(
                Lerp(a.Position.X, b.Position.X, uX),
                Lerp(a.Position.Y, b.Position.Y, uY),
                Lerp(a.Position.Z, b.Position.Z, uZ)),
            Quaternion.Slerp(a.Rotation, b.Rotation, uR));
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}

public sealed class VmdAnimation
{
    public const float FramesPerSecond = 30f;

    public Dictionary<string, BoneTrack> Tracks = [];
    public Dictionary<string, List<VmdMorphKeyFrame>> MorphTracks = [];
    public List<BoneTrack> CameraOrCenterTracks => Tracks.Values.ToList();
    public uint MaxFrame;
    public string ModelName = "";
    private readonly Dictionary<string, List<(uint Frame, bool Enabled)>> _ikStates = new(StringComparer.Ordinal);

    public static string NormalizeBoneName(string name) => name.Normalize(System.Text.NormalizationForm.FormKC).Replace("人指", "人差指");

    public bool IsIkEnabled(string name, float frame)
        => IsIkEnabledNormalized(NormalizeBoneName(name), frame);

    internal bool IsIkEnabledNormalized(string name, float frame)
    {
        if (!_ikStates.TryGetValue(name, out var keys)) return true;
        var lo = 0;
        var hi = keys.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (keys[mid].Frame <= frame) lo = mid + 1;
            else hi = mid;
        }
        return lo == 0 || keys[lo - 1].Enabled;
    }

    public float DurationSec => MaxFrame / FramesPerSecond;

    public static VmdAnimation Build(VmdFile file)
    {
        var anim = new VmdAnimation { ModelName = file.ModelName };
        foreach (var kf in file.BoneFrames)
        {
            if (!anim.Tracks.TryGetValue(kf.Bone, out var track))
                anim.Tracks[kf.Bone] = track = new BoneTrack { Bone = kf.Bone };
            track.Keys.Add(kf);
            if (kf.Frame > anim.MaxFrame) anim.MaxFrame = kf.Frame;
        }
        foreach (var track in anim.Tracks.Values) track.Sort();

        foreach (var kf in file.MorphFrames)
        {
            if (!anim.MorphTracks.TryGetValue(kf.Name, out var list))
                anim.MorphTracks[kf.Name] = list = [];
            list.Add(kf);
            if (kf.Frame > anim.MaxFrame) anim.MaxFrame = kf.Frame;
        }
        foreach (var list in anim.MorphTracks.Values) list.Sort((a, b) => a.Frame.CompareTo(b.Frame));
        foreach (var key in file.ShowIkFrames.OrderBy(k => k.Frame))
        {
            foreach (var (name, enabled) in key.Ik)
            {
                var normalized = NormalizeBoneName(name);
                if (!anim._ikStates.TryGetValue(normalized, out var keys)) anim._ikStates[normalized] = keys = [];
                keys.Add((key.Frame, enabled));
            }
            if (key.Frame > anim.MaxFrame) anim.MaxFrame = key.Frame;
        }
        return anim;
    }

    public bool TryGetTrack(string bone, out BoneTrack track) => Tracks.TryGetValue(bone, out track!);
}
