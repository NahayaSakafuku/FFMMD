using System;
using System.Linq;
using System.Numerics;

namespace FFMMD.Skirt;

/// <summary>
/// Symmetric, bounded quaternion filtering for a complete fixed-step bake.
/// It has no playback state and never reads or writes a native pose.
/// </summary>
public static class SkirtTrackSmoothing
{
    private const double RadiansPerDegree = Math.PI / 180;
    private const double ChangeEpsilon = 1e-8;

    public sealed record Settings
    {
        public int Window { get; init; } = 2;
        public double Strength { get; init; } = .65;
        public double MaximumCorrectionDegrees { get; init; } = 3;
        public double AngularBandwidthDegrees { get; init; } = 12;
        public int MaximumBackoffs { get; init; } = 8;
    }

    public sealed record Result(Quaternion[][] Frames, float[] AcceptedAmounts, Metrics Metrics);

    public sealed record TrackMetrics(double RootMeanSquareStepDegrees,
        double RootMeanSquareAngularChangeDegrees, double MaximumStepDegrees);

    public sealed record Metrics
    {
        public int FrameCount { get; init; }
        public int BoneCount { get; init; }
        public Settings Settings { get; init; } = new();
        public bool CollisionGuardUsed { get; init; }
        public int GuardCalls { get; init; }
        public int RejectedCandidates { get; init; }
        public int ReducedFrames { get; init; }
        public int OriginalFallbackFrames { get; init; }
        public int CandidateChangedFrames { get; init; }
        public int ChangedFrames { get; init; }
        public int ChangedSamples { get; init; }
        public double MaximumCandidateCorrectionDegrees { get; init; }
        public double MaximumAcceptedCorrectionDegrees { get; init; }
        public TrackMetrics Original { get; init; } = new(0, 0, 0);
        public TrackMetrics Smoothed { get; init; } = new(0, 0, 0);
    }

    /// <summary>
    /// Input and output are frame-major. The collision guard receives all bones
    /// for one candidate frame and must treat that array as read-only. It returns
    /// true only when the candidate does not worsen the original frame's contacts.
    /// Rejected candidates use one common blend amount across all bones; after
    /// eight default halvings the original frame is retained exactly.
    /// </summary>
    public static Result Smooth(Quaternion[][] frames,
        Func<int, Quaternion[], bool>? collisionGuard = null, Settings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(frames);
        settings ??= new();
        ValidateSettings(settings);
        var raw = NormalizeFrames(frames);
        var boneCount = raw.Length == 0 ? 0 : raw[0].Length;
        var filtered = Filter(raw, settings);
        var output = new Quaternion[raw.Length][];
        var amounts = new float[raw.Length];
        var guardCalls = 0;
        var rejected = 0;
        var reducedFrames = 0;
        var originalFallbackFrames = 0;
        var candidateChangedFrames = 0;
        var changedFrames = 0;
        var changedSamples = 0;
        var maximumCandidate = 0d;
        var maximumAccepted = 0d;

        for (var frame = 0; frame < raw.Length; frame++)
        {
            var candidateChanged = false;
            for (var bone = 0; bone < boneCount; bone++)
            {
                var correction = AngularDistance(raw[frame][bone], filtered[frame][bone]);
                candidateChanged |= correction > ChangeEpsilon;
                maximumCandidate = Math.Max(maximumCandidate, correction);
            }
            if (candidateChanged) candidateChangedFrames++;
            output[frame] = filtered[frame];
            amounts[frame] = 1;
            if (candidateChanged && collisionGuard != null)
            {
                var accepted = false;
                var mixed = new Quaternion[boneCount];
                for (var step = 0; step <= settings.MaximumBackoffs; step++)
                {
                    var amount = Math.Pow(.5, step);
                    for (var bone = 0; bone < boneCount; bone++)
                        mixed[bone] = step == 0 ? filtered[frame][bone]
                            : Slerp(raw[frame][bone], filtered[frame][bone], amount);
                    guardCalls++;
                    if (!collisionGuard(frame, mixed))
                    {
                        rejected++;
                        continue;
                    }
                    output[frame] = mixed;
                    amounts[frame] = (float)amount;
                    if (step > 0) reducedFrames++;
                    accepted = true;
                    break;
                }
                if (!accepted)
                {
                    output[frame] = (Quaternion[])raw[frame].Clone();
                    amounts[frame] = 0;
                    originalFallbackFrames++;
                }
            }
            var changed = false;
            for (var bone = 0; bone < boneCount; bone++)
            {
                var correction = AngularDistance(raw[frame][bone], output[frame][bone]);
                maximumAccepted = Math.Max(maximumAccepted, correction);
                if (correction <= ChangeEpsilon) continue;
                changed = true;
                changedSamples++;
            }
            if (changed) changedFrames++;
        }
        return new(output, amounts, new()
        {
            FrameCount = raw.Length, BoneCount = boneCount, Settings = settings,
            CollisionGuardUsed = collisionGuard != null, GuardCalls = guardCalls,
            RejectedCandidates = rejected, ReducedFrames = reducedFrames,
            OriginalFallbackFrames = originalFallbackFrames,
            CandidateChangedFrames = candidateChangedFrames, ChangedFrames = changedFrames,
            ChangedSamples = changedSamples,
            MaximumCandidateCorrectionDegrees = maximumCandidate / RadiansPerDegree,
            MaximumAcceptedCorrectionDegrees = maximumAccepted / RadiansPerDegree,
            Original = Measure(raw), Smoothed = Measure(output),
        });
    }

    private static void ValidateSettings(Settings settings)
    {
        if (settings.Window is < 0 or > 8 || !double.IsFinite(settings.Strength) || settings.Strength is < 0 or > 1 ||
            !double.IsFinite(settings.MaximumCorrectionDegrees) || settings.MaximumCorrectionDegrees is < 0 or > 15 ||
            !double.IsFinite(settings.AngularBandwidthDegrees) || settings.AngularBandwidthDegrees is <= 0 or > 180 ||
            settings.MaximumBackoffs is < 0 or > 16)
            throw new ArgumentOutOfRangeException(nameof(settings), "Invalid skirt smoothing settings.");
    }

    private static Quaternion[][] NormalizeFrames(Quaternion[][] frames)
    {
        var raw = new Quaternion[frames.Length][];
        var boneCount = frames.Length == 0 ? 0 : frames[0]?.Length ??
            throw new ArgumentException("A frame is null.", nameof(frames));
        for (var frame = 0; frame < frames.Length; frame++)
        {
            if (frames[frame] == null || frames[frame].Length != boneCount)
                throw new ArgumentException("Every frame must contain the same bone count.", nameof(frames));
            raw[frame] = new Quaternion[boneCount];
            for (var bone = 0; bone < boneCount; bone++)
            {
                var q = Normalize(frames[frame][bone]);
                if (frame > 0 && Dot(raw[frame - 1][bone], q) < 0) q = Negate(q);
                raw[frame][bone] = q;
            }
        }
        return raw;
    }

    private static Quaternion[][] Filter(Quaternion[][] raw, Settings settings)
    {
        var filtered = new Quaternion[raw.Length][];
        var cap = settings.MaximumCorrectionDegrees * RadiansPerDegree;
        var bandwidth = settings.AngularBandwidthDegrees * RadiansPerDegree;
        var timeSigma = Math.Max(.5, settings.Window * .65);
        for (var frame = 0; frame < raw.Length; frame++)
        {
            filtered[frame] = (Quaternion[])raw[frame].Clone();
            if (frame == 0 || frame == raw.Length - 1 || settings.Window == 0 ||
                settings.Strength == 0 || cap == 0) continue;
            for (var bone = 0; bone < raw[frame].Length; bone++)
            {
                var center = raw[frame][bone];
                var mean = new RotationVector(0, 0, 0);
                var total = 0d;
                var last = (int)Math.Min(raw.Length - 1L, (long)frame + settings.Window);
                for (var neighbor = Math.Max(0, frame - settings.Window); neighbor <= last; neighbor++)
                {
                    var delta = Log(Quaternion.Conjugate(center) * raw[neighbor][bone]);
                    var time = (neighbor - frame) / timeSigma;
                    var angle = delta.Length / bandwidth;
                    var weight = Math.Exp(-.5 * time * time - .5 * angle * angle);
                    mean += delta * weight;
                    total += weight;
                }
                var correction = mean * (settings.Strength / total);
                var length = correction.Length;
                if (length < 1e-10) continue;
                if (length > cap) correction *= cap / length;
                filtered[frame][bone] = Normalize(center * Exp(correction));
            }
        }
        return filtered;
    }

    // Angular-change values describe local temporal roughness, not physical
    // acceleration expressed in one common world coordinate frame.
    private static TrackMetrics Measure(Quaternion[][] frames)
    {
        if (frames.Length == 0) return new(0, 0, 0);
        long count = 0, accelerationCount = 0;
        var speedSquared = 0d;
        var accelerationSquared = 0d;
        var maximumStep = 0d;
        for (var bone = 0; bone < frames[0].Length; bone++)
        {
            var previousVelocity = new RotationVector(0, 0, 0);
            for (var frame = 1; frame < frames.Length; frame++)
            {
                var velocity = Log(Quaternion.Conjugate(frames[frame - 1][bone]) * frames[frame][bone]);
                speedSquared += velocity.LengthSquared;
                maximumStep = Math.Max(maximumStep, velocity.Length);
                count++;
                if (frame > 1)
                {
                    accelerationSquared += (velocity - previousVelocity).LengthSquared;
                    accelerationCount++;
                }
                previousVelocity = velocity;
            }
        }
        return new(Math.Sqrt(speedSquared / Math.Max(1, count)) / RadiansPerDegree,
            Math.Sqrt(accelerationSquared / Math.Max(1, accelerationCount)) / RadiansPerDegree,
            maximumStep / RadiansPerDegree);
    }

    private static Quaternion Slerp(Quaternion original, Quaternion candidate, double amount)
        => Normalize(original * Exp(Log(Quaternion.Conjugate(original) * candidate) * amount));

    private static double AngularDistance(Quaternion a, Quaternion b)
        => Log(Quaternion.Conjugate(a) * b).Length;

    private static Quaternion Normalize(Quaternion q)
    {
        if (!float.IsFinite(q.X) || !float.IsFinite(q.Y) || !float.IsFinite(q.Z) || !float.IsFinite(q.W))
            throw new ArgumentException("Quaternion values must be finite.");
        var length = Math.Sqrt(Dot(q, q));
        if (length < 1e-12) throw new ArgumentException("Quaternion has zero length.");
        return new((float)(q.X / length), (float)(q.Y / length), (float)(q.Z / length), (float)(q.W / length));
    }

    private static double Dot(Quaternion a, Quaternion b)
        => (double)a.X * b.X + (double)a.Y * b.Y + (double)a.Z * b.Z + (double)a.W * b.W;

    private static Quaternion Negate(Quaternion q) => new(-q.X, -q.Y, -q.Z, -q.W);

    private static RotationVector Log(Quaternion q)
    {
        q = Normalize(q);
        if (q.W < 0) q = Negate(q);
        var vector = new RotationVector(q.X, q.Y, q.Z);
        var length = vector.Length;
        return vector * (length < 1e-10 ? 2 : 2 * Math.Atan2(length, Math.Max(0, q.W)) / length);
    }

    private static Quaternion Exp(RotationVector vector)
    {
        var angle = vector.Length;
        if (angle < 1e-10)
            return Normalize(new((float)(vector.X * .5), (float)(vector.Y * .5), (float)(vector.Z * .5), 1));
        var ratio = Math.Sin(angle * .5) / angle;
        return new((float)(vector.X * ratio), (float)(vector.Y * ratio), (float)(vector.Z * ratio), (float)Math.Cos(angle * .5));
    }

    private readonly record struct RotationVector(double X, double Y, double Z)
    {
        public double LengthSquared => X * X + Y * Y + Z * Z;
        public double Length => Math.Sqrt(LengthSquared);
        public static RotationVector operator +(RotationVector a, RotationVector b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static RotationVector operator -(RotationVector a, RotationVector b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static RotationVector operator *(RotationVector value, double scale) => new(value.X * scale, value.Y * scale, value.Z * scale);
    }
}
