using System.Numerics;
using FFMMD.Skirt;

internal static class SkirtTrackSmoothingRegression
{
    public static int Run()
    {
        var failed = 0;
        var count = 0;
        void Test(string name, Action body)
        {
            count++;
            try { body(); Console.WriteLine($"  ✔ {name}"); }
            catch (Exception e) { failed++; Console.WriteLine($"  ✘ {name}: {e.Message}"); }
        }

        Test("constant and antipodal quaternion tracks retain their rotations", () =>
        {
            var q = AxisAngle(27);
            var frames = Make(15, 3, (f, b) => (f + b) % 2 == 0 ? q : Negate(q));
            var before = Clone(frames);
            var result = SkirtTrackSmoothing.Smooth(frames);
            Require(result.Frames.All(frame => frame.All(value => Angle(value, q) < 1e-4)), "constant rotation drifted");
            Require(result.Metrics.ChangedFrames == 0, "antipodal signs created smoothing changes");
            Require(Same(frames, before), "input was modified");
        });

        Test("single-frame jitter decreases temporal roughness without endpoint drift", () =>
        {
            var frames = Make(15, 1, (f, _) => AxisAngle(f == 7 ? 9 : 0));
            var result = SkirtTrackSmoothing.Smooth(frames);
            Require(Angle(result.Frames[7][0], Quaternion.Identity) < 7, "central jitter was not reduced");
            Require(result.Metrics.Smoothed.RootMeanSquareAngularChangeDegrees < result.Metrics.Original.RootMeanSquareAngularChangeDegrees,
                "angular roughness did not decrease");
            Require(result.Frames[0][0] == frames[0][0] && result.Frames[^1][0] == frames[^1][0], "endpoints changed");
            Require(result.Metrics.MaximumAcceptedCorrectionDegrees <= 3.0001, "default correction cap exceeded");
        });

        Test("authored slow rotation is preserved in the symmetric window interior", () =>
        {
            var frames = Make(61, 2, (f, b) => AxisAngle(f * .6f + b * 18));
            var result = SkirtTrackSmoothing.Smooth(frames);
            for (var frame = 2; frame < frames.Length - 2; frame++)
                for (var bone = 0; bone < 2; bone++)
                    Require(Angle(result.Frames[frame][bone], frames[frame][bone]) < 2e-4, "constant angular velocity changed");
            Require(result.Frames[0][0] == frames[0][0] && Angle(result.Frames[^1][1], frames[^1][1]) < 1e-5,
                "authored endpoints moved");
        });

        Test("maximum correction remains bounded for a strong displaced sample", () =>
        {
            var frames = Make(9, 1, (f, _) => AxisAngle(f == 4 ? 30 : 0));
            var result = SkirtTrackSmoothing.Smooth(frames, settings: new()
            {
                Strength = 1, AngularBandwidthDegrees = 180,
            });
            Require(Math.Abs(result.Metrics.MaximumCandidateCorrectionDegrees - 3) < 1e-4, "cap was not exercised");
            for (var frame = 0; frame < frames.Length; frame++)
                Require(Angle(result.Frames[frame][0], frames[frame][0]) <= 3.0001, "sample exceeded cap");
        });

        Test("a rejecting collision guard retains every original rotation", () =>
        {
            var frames = Make(9, 3, (f, b) => AxisAngle(f == 4 ? 12 + b * 2 : 0));
            var calls = 0;
            var result = SkirtTrackSmoothing.Smooth(frames, (frame, candidate) =>
            {
                calls++;
                Require(candidate.Length == 3 && frame is > 0 and < 8, "guard frame layout is incorrect");
                return false;
            });
            for (var frame = 0; frame < frames.Length; frame++)
                for (var bone = 0; bone < 3; bone++)
                    Require(Angle(result.Frames[frame][bone], frames[frame][bone]) < 1e-5, "rejected candidate changed the raw rotation");
            Require(result.Metrics.OriginalFallbackFrames == result.Metrics.CandidateChangedFrames && result.Metrics.ChangedFrames == 0,
                "fallback metrics are wrong");
            Require(calls == result.Metrics.OriginalFallbackFrames * 9 && calls == result.Metrics.GuardCalls,
                "guard backoff count is wrong");
        });

        Test("contact backoff uses one common frame blend across all panels", () =>
        {
            var frames = Make(11, 2, (f, b) => AxisAngle(f == 5 ? 10 + b * 4 : 0));
            var unguarded = SkirtTrackSmoothing.Smooth(frames);
            var result = SkirtTrackSmoothing.Smooth(frames, (frame, candidate) =>
                frame != 5 || Angle(frames[frame][0], candidate[0]) <= .76);
            Require(result.AcceptedAmounts[5] == .25f && result.Metrics.ReducedFrames == 1, "expected quarter backoff was not selected");
            for (var bone = 0; bone < 2; bone++)
            {
                var full = Angle(frames[5][bone], unguarded.Frames[5][bone]);
                var accepted = Angle(frames[5][bone], result.Frames[5][bone]);
                Require(Math.Abs(accepted / full - .25) < 1e-4, "panels used different blend fractions");
            }
        });

        Test("empty, one-frame and disabled filters keep finite normalized samples", () =>
        {
            Require(SkirtTrackSmoothing.Smooth([]).Frames.Length == 0, "empty input failed");
            var q = AxisAngle(15);
            var single = SkirtTrackSmoothing.Smooth([[new(q.X * 2, q.Y * 2, q.Z * 2, q.W * 2)]]);
            Require(Angle(single.Frames[0][0], q) < 1e-5, "single quaternion failed normalization");
            var frames = Make(7, 1, (f, _) => AxisAngle(f == 3 ? 11 : 0));
            foreach (var settings in new[]
                { new SkirtTrackSmoothing.Settings { Window = 0 }, new() { Strength = 0 }, new() { MaximumCorrectionDegrees = 0 } })
            {
                var result = SkirtTrackSmoothing.Smooth(frames, settings: settings);
                Require(result.Metrics.ChangedFrames == 0, "disabled smoothing changed a sample");
            }
        });

        Test("random frame and bone counts are finite, bounded and deterministic", () =>
        {
            var random = new Random(81397);
            foreach (var (frameCount, boneCount) in new[] { (2, 1), (13, 6), (81, 18), (103, 4) })
            {
                var frames = Make(frameCount, boneCount, (_, _) =>
                {
                    var axis = new Vector3((float)random.NextDouble() - .5f, (float)random.NextDouble() - .5f, (float)random.NextDouble() - .5f);
                    return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), (float)random.NextDouble() * MathF.PI);
                });
                var a = SkirtTrackSmoothing.Smooth(frames);
                var b = SkirtTrackSmoothing.Smooth(frames);
                Require(Same(a.Frames, b.Frames) && a.Metrics == b.Metrics, "repeated calculation changed the output");
                Require(a.Metrics.MaximumAcceptedCorrectionDegrees <= 3.0001, "random correction exceeded cap");
                foreach (var frame in a.Frames)
                    foreach (var q in frame)
                        Require(float.IsFinite(q.X) && float.IsFinite(q.Y) && float.IsFinite(q.Z) && float.IsFinite(q.W) && MathF.Abs(q.Length() - 1) < 1e-6f,
                            "random result is not a finite unit quaternion");
            }
        });

        Test("invalid layouts, quaternions and settings are rejected", () =>
        {
            Reject(() => SkirtTrackSmoothing.Smooth([[Quaternion.Identity], []]));
            Reject(() => SkirtTrackSmoothing.Smooth([null!]));
            Reject(() => SkirtTrackSmoothing.Smooth([[default(Quaternion)]]));
            Reject(() => SkirtTrackSmoothing.Smooth([[new(float.NaN, 0, 0, 1)]]));
            foreach (var settings in new[] { new SkirtTrackSmoothing.Settings { Window = -1 }, new() { Strength = double.NaN },
                new() { MaximumCorrectionDegrees = 16 }, new() { AngularBandwidthDegrees = 0 }, new() { MaximumBackoffs = 17 } })
                Reject(() => SkirtTrackSmoothing.Smooth([[Quaternion.Identity]], settings: settings));
        });

        Console.WriteLine($"\n裙骨轨道平滑回归：{count - failed}/{count} 通过");
        return failed;
    }

    private static Quaternion[][] Make(int frames, int bones, Func<int, int, Quaternion> create)
        => Enumerable.Range(0, frames).Select(frame => Enumerable.Range(0, bones).Select(bone => create(frame, bone)).ToArray()).ToArray();

    private static Quaternion[][] Clone(Quaternion[][] frames) => frames.Select(frame => (Quaternion[])frame.Clone()).ToArray();
    private static bool Same(Quaternion[][] a, Quaternion[][] b) => a.Length == b.Length && a.Zip(b).All(pair => pair.First.SequenceEqual(pair.Second));
    private static Quaternion AxisAngle(float degrees) => Quaternion.CreateFromAxisAngle(Vector3.UnitY, degrees * MathF.PI / 180);
    private static Quaternion Negate(Quaternion q) => new(-q.X, -q.Y, -q.Z, -q.W);

    private static double Angle(Quaternion a, Quaternion b)
    {
        var q = Quaternion.Conjugate(Quaternion.Normalize(a)) * Quaternion.Normalize(b);
        var vectorLength = Math.Sqrt((double)q.X * q.X + (double)q.Y * q.Y + (double)q.Z * q.Z);
        return 2 * Math.Atan2(vectorLength, Math.Abs(q.W)) * 180 / Math.PI;
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("Invalid input was accepted.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
