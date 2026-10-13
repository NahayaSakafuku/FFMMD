using System.Globalization;
using System.Numerics;
using System.Text.Json;
using FFMMD.Posing;
using FFMMD.Retarget;

internal static class SkeletonInitializationRegression
{
    private static readonly JsonSerializerOptions Json = new() { IncludeFields = true, WriteIndented = true };

    public static int Run()
    {
        var count = 0;
        var failed = 0;
        void Test(string name, Action action)
        {
            count++;
            try { action(); Console.WriteLine($"  ✔ {name}"); }
            catch (Exception e) { failed++; Console.WriteLine($"  ✘ {name}: {e.Message}"); }
        }

        Test("骨架预检：真实 fixture 合法非单位尺寸与原 profile 完全一致", () =>
        {
            var reference = JsonSerializer.Deserialize<SkeletonTree>(File.ReadAllText(
                Path.Combine(AppContext.BaseDirectory, "Fixtures", "Current-character-172.json")), Json)!;
            reference.RebuildReference();
            var positions = (Vector3[])reference.RefLocalPos.Clone();
            var scales = (Vector3[])reference.RefLocalScale.Clone();
            scales[0] = new(.7f, 1.2f, 1.5f);
            var before = new TargetRigProfile(reference, positions, scales);
            Assert(SkeletonInitializationDiagnostic.Inspect(reference, positions, scales, reference.RefLocalRot, positions) == null);
            var after = new TargetRigProfile(reference, positions, scales);
            Assert(before.Left == after.Left && before.Up == after.Up && before.Back == after.Back);
            Assert(before.Skeleton.RefModelPos.SequenceEqual(after.Skeleton.RefModelPos));
            Assert(before.Skeleton.RefModelRot.SequenceEqual(after.Skeleton.RefModelRot));
            Assert(before.Skeleton.RefModelScale.SequenceEqual(after.Skeleton.RefModelScale));
        });

        Test("骨架预检：无关叶骨零 scale 记录精确名称、索引、父骨与 reference", () =>
        {
            var reference = Reference();
            var scales = (Vector3[])reference.RefLocalScale.Clone(); scales[1].X = 0;
            var report = Inspect(reference, scales: scales)!;
            Assert(report.InvalidBoneCount == 1 && report.Bones.Length == 1);
            var bone = report.Bones[0];
            Assert(bone.Index == 1 && bone.Name == "fixture_auxiliary" && bone.ParentIndex == 0 && bone.ParentName == "n_root");
            Assert(bone.InvalidFields.SequenceEqual(new[] { "CurrentLocalScale.X: <= 1e-6" }));
            Assert(bone.CurrentLocalScale.SequenceEqual(new[] { "0", "1", "1" }));
            Assert(bone.ReferenceLocalScale.SequenceEqual(new[] { "1", "1", "1" }));
            Reject(() => new TargetRigProfile(reference, reference.RefLocalPos, scales));
        });

        Test("骨架预检：负 scale、阈值和阈值以上与生产校验一致", () =>
        {
            var reference = Reference();
            foreach (var value in new[] { -1f, 1e-6f, 2e-6f })
            {
                var scales = (Vector3[])reference.RefLocalScale.Clone(); scales[1].Y = value;
                var report = Inspect(reference, scales: scales);
                Assert((report != null) == (value <= 1e-6f));
                if (report != null) Reject(() => new TargetRigProfile(reference, reference.RefLocalPos, scales));
                else _ = new TargetRigProfile(reference, reference.RefLocalPos, scales);
            }
        });

        Test("骨架预检：NaN 与正负 Infinity scale 可序列化且不丢原值", () =>
        {
            var reference = Reference();
            var scales = (Vector3[])reference.RefLocalScale.Clone(); scales[1] = new(float.NaN, float.PositiveInfinity, float.NegativeInfinity);
            var report = Inspect(reference, scales: scales)!;
            Assert(report.Bones[0].InvalidFields.Length == 3);
            var copy = JsonSerializer.Deserialize<SkeletonInitializationDiagnostic>(JsonSerializer.Serialize(report, Json), Json)!;
            Assert(copy.Bones[0].CurrentLocalScale.SequenceEqual(new[] { "NaN", "Infinity", "-Infinity" }));
            Assert(!copy.AnimationScaleWritesAllowed);
        });

        Test("骨架预检：非法当前 position 保留 reference/current/effective 三组证据", () =>
        {
            var reference = Reference();
            var positions = (Vector3[])reference.RefLocalPos.Clone(); positions[1].Z = float.NaN;
            var report = Inspect(reference, positions: positions)!;
            var bone = report.Bones[0];
            Assert(bone.InvalidFields.SequenceEqual(new[] { "EffectiveLocalPosition.Z: non-finite" }));
            Assert(bone.CurrentLocalPosition[2] == "NaN" && bone.EffectiveLocalPosition[2] == "NaN");
            Assert(bone.ReferenceLocalPosition[2] == "3");
            Reject(() => new TargetRigProfile(reference, positions, reference.RefLocalScale));
        });

        Test("骨架预检：root 当前 position 不用于几何时不误报", () =>
        {
            var reference = Reference();
            var current = (Vector3[])reference.RefLocalPos.Clone(); current[0] = new(float.NaN, float.PositiveInfinity, 3);
            var effective = (Vector3[])current.Clone(); effective[0] = reference.RefLocalPos[0];
            Assert(SkeletonInitializationDiagnostic.Inspect(reference, current, reference.RefLocalScale, reference.RefLocalRot, effective) == null);
            _ = new TargetRigProfile(reference, effective, reference.RefLocalScale);
        });

        Test("骨架预检：使用 reference rotation 且失败时保留 current rotation", () =>
        {
            var reference = Reference();
            var rotations = (Quaternion[])reference.RefLocalRot.Clone(); rotations[1] = new(float.NaN, 0, 0, 0);
            Assert(SkeletonInitializationDiagnostic.Inspect(reference, reference.RefLocalPos, reference.RefLocalScale, rotations, reference.RefLocalPos) == null);
            var scales = (Vector3[])reference.RefLocalScale.Clone(); scales[1].Z = 0;
            var report = SkeletonInitializationDiagnostic.Inspect(reference, reference.RefLocalPos, scales, rotations, reference.RefLocalPos)!;
            Assert(report.Bones[0].CurrentLocalRotation[0] == "NaN");
            Assert(report.Bones[0].ReferenceLocalRotation.SequenceEqual(new[] { "0", "0", "0", "1" }));
            Assert(!report.Bones[0].InvalidFields.Any(field => field.Contains("Rotation")));
        });

        Test("骨架预检：非法 reference quaternion 使用相同生产阈值", () =>
        {
            var reference = Reference(); reference.RefLocalRot[1] = new(0, 0, 0, 0);
            var report = Inspect(reference)!;
            Assert(report.Bones[0].InvalidFields.Single() == "ReferenceLocalRotation: length squared < 1e-12");
            Reject(reference.RebuildReference);
        });

        Test("骨架预检：最多记录16骨但总异常数完整", () =>
        {
            var reference = Reference(40);
            var scales = Enumerable.Repeat(Vector3.Zero, reference.BoneCount).ToArray();
            var report = Inspect(reference, scales: scales)!;
            Assert(report.InvalidBoneCount == 40 && report.Bones.Length == SkeletonInitializationDiagnostic.MaximumRecordedBones);
            Assert(report.Bones[0].Index == 0 && report.Bones[^1].Index == 15);
        });

        Test("骨架预检：输入数组与原参考模型缓存逐位不变", () =>
        {
            var reference = Reference();
            var positions = (Vector3[])reference.RefLocalPos.Clone();
            var scales = (Vector3[])reference.RefLocalScale.Clone(); scales[1].X = 0;
            var rotations = (Quaternion[])reference.RefLocalRot.Clone();
            string ReferenceState() => JsonSerializer.Serialize(new
            {
                reference.Names, reference.Parent, reference.BoneCount, reference.Index,
                reference.RefLocalPos, reference.RefLocalScale, reference.RefLocalRot,
                reference.RefModelPos, reference.RefModelScale, reference.RefModelRot,
            }, Json);
            var before = ReferenceState();
            var beforePositions = (Vector3[])positions.Clone(); var beforeScales = (Vector3[])scales.Clone();
            var beforeRotations = (Quaternion[])rotations.Clone();
            _ = SkeletonInitializationDiagnostic.Inspect(reference, positions, scales, rotations, positions);
            Assert(before == ReferenceState());
            Assert(positions.SequenceEqual(beforePositions) && scales.SequenceEqual(beforeScales) && rotations.SequenceEqual(beforeRotations));
        });

        Test("骨架预检：float round-trip 在逗号小数文化下仍可读取", () =>
        {
            var previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                var reference = Reference();
                var scales = (Vector3[])reference.RefLocalScale.Clone(); scales[1] = new(0, .12345679f, -2.5f);
                var report = Inspect(reference, scales: scales)!;
                var copy = JsonSerializer.Deserialize<SkeletonInitializationDiagnostic>(JsonSerializer.Serialize(report, Json), Json)!;
                for (var axis = 0; axis < 3; axis++)
                    Assert(float.Parse(copy.Bones[0].CurrentLocalScale[axis], CultureInfo.InvariantCulture) == scales[1][axis]);
                Assert(copy.Bones[0].CurrentLocalScale[2] == "-2.5");
            }
            finally { CultureInfo.CurrentCulture = previous; }
        });

        Test("骨架预检：数组长度不匹配拒绝且异常摘要带首骨和值", () =>
        {
            var reference = Reference();
            try { _ = SkeletonInitializationDiagnostic.Inspect(reference, [], [], [], []); throw new Exception("short arrays accepted"); }
            catch (ArgumentException) { }
            var scales = (Vector3[])reference.RefLocalScale.Clone(); scales[1].X = 0;
            var error = new SkeletonInitializationException(Inspect(reference, scales: scales)!);
            Assert(error.Message.Contains("[1] fixture_auxiliary") && error.Message.Contains("current scale=(0, 1, 1)"));
            Assert(error.Message.Contains("reference scale=(1, 1, 1)"));
        });

        Console.WriteLine($"  骨架初始化预检回归 {count - failed}/{count} 通过");
        return failed;
    }

    private static SkeletonTree Reference(int count = 2)
    {
        var tree = new SkeletonTree
        {
            Names = Enumerable.Range(0, count).Select(i => i == 0 ? "n_root" : i == 1 ? "fixture_auxiliary" : $"fixture_auxiliary_{i}").ToArray(),
            Parent = Enumerable.Range(0, count).Select(i => (short)(i == 0 ? -1 : 0)).ToArray(),
            RefLocalPos = Enumerable.Range(0, count).Select(i => i == 0 ? Vector3.Zero : new Vector3(1, 2, 3)).ToArray(),
            RefLocalScale = Enumerable.Repeat(Vector3.One, count).ToArray(),
            RefLocalRot = Enumerable.Repeat(Quaternion.Identity, count).ToArray(),
        };
        tree.RebuildReference();
        return tree;
    }

    private static SkeletonInitializationDiagnostic? Inspect(SkeletonTree reference, Vector3[]? positions = null, Vector3[]? scales = null) =>
        SkeletonInitializationDiagnostic.Inspect(reference, positions ?? reference.RefLocalPos, scales ?? reference.RefLocalScale,
            reference.RefLocalRot, positions ?? reference.RefLocalPos);
    private static void Assert(bool condition) { if (!condition) throw new Exception("assertion failed"); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new Exception("invalid effective target profile accepted");
    }
}
