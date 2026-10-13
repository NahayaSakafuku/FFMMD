using System.Numerics;
using System.Text.Json;
using FFMMD;
using FFMMD.Posing;
using FFMMD.Retarget;
using FFMMD.Vmd;

internal static class SuppressedBreastRegression
{
    private static readonly JsonSerializerOptions Json = new() { IncludeFields = true };
    private sealed class Capture { public SkeletonTree Target = new(); }
    private sealed class ChestSample
    {
        public string Name { get; set; } = "";
        public Vector3 ReferenceLocalPosition { get; set; }
        public Vector3 ReferenceLocalScale { get; set; }
        public Vector3 CurrentLocalPosition { get; set; }
        public Vector3 CurrentLocalScale { get; set; }
        public Quaternion ReferenceLocalRotation { get; set; }
        public Quaternion CurrentLocalRotation { get; set; }
    }
    private sealed record Input(SkeletonTree Reference, Vector3[] Positions, Vector3[] Scales, Quaternion[] Rotations);

    public static int Run(string? vmdPath)
    {
        var count = 0; var failed = 0;
        void Test(string name, Action action)
        {
            count++;
            try { action(); Console.WriteLine($"  ✔ {name}"); }
            catch (Exception e) { failed++; Console.WriteLine($"  ✘ {name}: {e.Message}"); }
        }
        Test("胸部骨特化：实机两骨数据覆盖106骨后 profile 与预检通过", () =>
        {
            var input = RecordedInput();
            var dimensions = TargetRigDimensions.Create(input.Reference, input.Positions, input.Scales);
            Assert(SkeletonInitializationDiagnostic.Inspect(input.Reference, input.Positions, input.Scales,
                input.Rotations, dimensions.LocalPositions, dimensions.LocalScales) == null);
            var profile = new TargetRigProfile(input.Reference, dimensions);
            Assert(profile.Skeleton.BoneCount == 106 && profile.SuppressedBreastBones.Length == 2);
            foreach (var bone in profile.SuppressedBreastBones)
            {
                Assert(bone.Algorithm == "SuppressedBreastLeafReference" && bone.NativeTransformPreserved);
                Assert(profile.Skeleton.RefLocalScale[bone.Index] == bone.ReferenceLocalScale);
                Assert(profile.Skeleton.RefLocalPos[bone.Index] == bone.ReferenceLocalPosition);
                Assert(bone.CurrentLocalScale == input.Scales[bone.Index]);
            }
        });
        Test("胸部骨特化：零与阈值三轴接受且只改变托管副本", () =>
        {
            foreach (var scale in new[] { Vector3.Zero, new Vector3(1e-6f) })
            {
                var input = RecordedInput(); var index = input.Reference.Find("j_mune_l"); input.Scales[index] = scale;
                var beforePosition = (Vector3[])input.Positions.Clone(); var beforeScale = (Vector3[])input.Scales.Clone();
                var beforeReference = (Vector3[])input.Reference.RefLocalScale.Clone();
                var profile = new TargetRigProfile(input.Reference, input.Positions, input.Scales);
                Assert(profile.SuppressedBreastBones.Length == 2);
                Assert(input.Positions.SequenceEqual(beforePosition) && input.Scales.SequenceEqual(beforeScale));
                Assert(input.Reference.RefLocalScale.SequenceEqual(beforeReference));
                Assert(!ReferenceEquals(profile.Skeleton.RefLocalScale, input.Scales));
            }
        });
        Test("胸部骨特化：正常及合法非单位胸部 scale 不替换", () =>
        {
            var input = RecordedInput();
            foreach (var name in new[] { "j_mune_l", "j_mune_r" }) input.Scales[input.Reference.Find(name)] = new(.5f, .7f, 1.3f);
            var profile = new TargetRigProfile(input.Reference, input.Positions, input.Scales);
            Assert(profile.SuppressedBreastBones.Length == 0);
            Assert(profile.Skeleton.RefLocalScale.SequenceEqual(input.Scales));
            Assert(profile.Skeleton.RefLocalPos.SequenceEqual(input.Positions));
        });
        Test("胸部骨特化：部分轴抑制与阈值以上混合数据仍拒绝", () =>
        {
            foreach (var scale in new[] { new Vector3(1e-6f, 1, 1), new Vector3(0, 2e-6f, 0) })
            {
                var input = RecordedInput(); input.Scales[input.Reference.Find("j_mune_l")] = scale;
                Reject(() => new TargetRigProfile(input.Reference, input.Positions, input.Scales));
            }
        });
        Test("胸部骨特化：负值、NaN、Infinity 不被回退掩盖", () =>
        {
            foreach (var scale in new[] { new Vector3(-1e-7f, 0, 0), new Vector3(float.NaN, 0, 0), new Vector3(0, float.PositiveInfinity, 0) })
            {
                var input = RecordedInput(); input.Scales[input.Reference.Find("j_mune_l")] = scale;
                Reject(() => new TargetRigProfile(input.Reference, input.Positions, input.Scales));
            }
        });
        Test("胸部骨特化：缓存状态恢复或出现混合非法scale要求重建", () =>
        {
            Assert(TargetRigDimensions.MatchesBreastLeafState(Vector3.Zero, new(1e-6f), true));
            Assert(!TargetRigDimensions.MatchesBreastLeafState(Vector3.Zero, new(0, 1, 1), true));
            Assert(!TargetRigDimensions.MatchesBreastLeafState(Vector3.Zero, Vector3.One, true));
            Assert(TargetRigDimensions.MatchesBreastLeafState(Vector3.Zero, Vector3.One, false));
            Assert(!TargetRigDimensions.MatchesBreastLeafState(Vector3.Zero, new(1e-6f), false));
            Assert(!TargetRigDimensions.MatchesBreastLeafState(new(float.NaN, 0, 0), new(1e-6f), true));
            Assert(!TargetRigDimensions.MatchesBreastLeafState(Vector3.Zero, new(float.NaN), true));
        });
        Test("胸部骨特化：稳态缓存检查无托管分配", () =>
        {
            var position = new Vector3(1, 2, 3); var scale = new Vector3(1e-6f);
            for (var i = 0; i < 100; i++) Assert(TargetRigDimensions.MatchesBreastLeafState(position, scale, true));
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1000; i++) Assert(TargetRigDimensions.MatchesBreastLeafState(position, scale, true));
            Assert(GC.GetAllocatedBytesForCurrentThread() == before);
        });
        Test("胸部骨特化：非胸部同类极小 scale 不放行", () =>
        {
            var input = RecordedInput(); input.Reference.Names[input.Reference.Find("j_mune_l")] = "fixture_auxiliary";
            Reject(() => new TargetRigProfile(input.Reference, input.Positions, input.Scales));
        });
        Test("胸部骨特化：父骨不是j_sebo_b时拒绝", () =>
        {
            var input = RecordedInput(); input.Reference.Parent[input.Reference.Find("j_mune_l")] = (short)input.Reference.Find("n_hara");
            Reject(() => new TargetRigProfile(input.Reference, input.Positions, input.Scales));
        });
        Test("胸部骨特化：有后代包括未映射辅助骨时拒绝", () =>
        {
            var input = RecordedInput();
            var breast = input.Reference.Find("j_mune_l"); var child = input.Reference.Find("n_sippo_e");
            Assert(child > breast); input.Reference.Parent[child] = (short)breast;
            Reject(() => new TargetRigProfile(input.Reference, input.Positions, input.Scales));
        });
        Test("胸部骨特化：若未来加入身体映射就拒绝回退", () =>
        {
            var old = BoneMap.Bindings[0];
            try
            {
                BoneMap.Bindings[0] = new(["下半身"], ["j_mune_l"]);
                var input = RecordedInput();
                Reject(() => new TargetRigProfile(input.Reference, input.Positions, input.Scales));
            }
            finally { BoneMap.Bindings[0] = old; }
        });
        Test("胸部骨特化：非法reference与非有限当前position保留拒绝", () =>
        {
            for (var scenario = 0; scenario < 4; scenario++)
            {
                var input = RecordedInput(); var index = input.Reference.Find("j_mune_l");
                switch (scenario)
                {
                    case 0: input.Reference.RefLocalScale[index] = Vector3.Zero; break;
                    case 1: input.Reference.RefLocalPos[index].X = float.NaN; break;
                    case 2: input.Reference.RefLocalRot[index] = new(0, 0, 0, 0); break;
                    case 3: input.Positions[index].X = float.NaN; break;
                }
                Reject(() => new TargetRigProfile(input.Reference, input.Positions, input.Scales));
            }
        });
        Test("胸部骨特化：混合故障记录仍保留真实原值与两项已执行调整", () =>
        {
            var input = RecordedInput(); var hip = input.Reference.Find("j_asi_a_l"); input.Scales[hip] = new(0, 1, 1);
            var dimensions = TargetRigDimensions.Create(input.Reference, input.Positions, input.Scales);
            var failure = SkeletonInitializationDiagnostic.Inspect(input.Reference, input.Positions, input.Scales,
                input.Rotations, dimensions.LocalPositions, dimensions.LocalScales)!;
            failure.ManagedReferenceAdjustments = dimensions.SuppressedBreastBones;
            Assert(failure.InvalidBoneCount == 1 && failure.Bones[0].Name == "j_asi_a_l");
            var copy = JsonSerializer.Deserialize<SkeletonInitializationDiagnostic>(JsonSerializer.Serialize(failure, Json), Json)!;
            Assert(copy.ManagedReferenceAdjustments.Length == 2 && copy.Bones[0].CurrentLocalScale[0] == "0");
            Assert(copy.Bones[0].EffectiveLocalScale[0] == "0");
            Reject(() => new TargetRigProfile(input.Reference, dimensions));
        });
        Test("胸部骨特化：动画写入掩码排除两骨且不改变当前rotation/position", () =>
        {
            var input = RecordedInput(); var profile = new TargetRigProfile(input.Reference, input.Positions, input.Scales);
            var retargeter = new Retargeter(); var cal = new Calibration(); retargeter.TestSetup(profile, Motion(), cal);
            Assert(retargeter.Evaluate(15, cal));
            foreach (var bone in profile.SuppressedBreastBones)
            {
                var write = retargeter.GetBoneWrite(bone.Index);
                Assert(!write.WriteRotation && !write.WriteTranslation && !retargeter.RotationWrites[bone.Index]);
                var position = input.Positions[bone.Index]; var rotation = input.Rotations[bone.Index]; var scale = input.Scales[bone.Index];
                write.Apply(ref position, ref rotation);
                Assert(position == input.Positions[bone.Index] && rotation == input.Rotations[bone.Index] && scale == input.Scales[bone.Index]);
            }
        });
        Test("胸部骨特化：普通schema9诊断可区分托管参考与原生抑制", () =>
        {
            var input = RecordedInput(); var profile = new TargetRigProfile(input.Reference, input.Positions, input.Scales);
            var retargeter = new Retargeter(); var cal = new Calibration(); retargeter.TestSetup(profile, Motion(), cal); Assert(retargeter.Evaluate(0, cal));
            var report = RigDiagnostics.Create(retargeter, cal, 0);
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(report, Json));
            Assert(document.RootElement.GetProperty("SchemaVersion").GetInt32() == 9);
            var target = document.RootElement.GetProperty("Target").Deserialize<TargetSkeletonSnapshot>(Json)!;
            Assert(target.ManagedReferenceAdjustments.Length == 2);
            foreach (var bone in target.ManagedReferenceAdjustments)
            {
                Assert(!target.RotationWrites[bone.Index] && bone.NativeTransformPreserved);
                Assert(bone.CurrentLocalScale == input.Scales[bone.Index]);
                Assert(target.EffectiveLocalScales[bone.Index] == input.Reference.RefLocalScale[bone.Index]);
            }
        });
        Test("胸部骨特化：既有172/186骨正常数据全部保留", () =>
        {
            foreach (var file in new[] { "Current-character-172.json", "AnimationKit-target.json" })
            {
                var tree = JsonSerializer.Deserialize<SkeletonTree>(File.ReadAllText(Fixture(file)), Json)!; tree.RebuildReference();
                var dimensions = TargetRigDimensions.Create(tree); var profile = new TargetRigProfile(tree, dimensions);
                Assert(profile.SuppressedBreastBones.Length == 0);
                Assert(dimensions.LocalScales.SequenceEqual(tree.RefLocalScale) && dimensions.LocalPositions.SequenceEqual(tree.RefLocalPos));
                var baseline = new SkeletonTree
                {
                    Names = (string[])tree.Names.Clone(), Parent = (short[])tree.Parent.Clone(),
                    RefLocalPos = (Vector3[])tree.RefLocalPos.Clone(), RefLocalScale = (Vector3[])tree.RefLocalScale.Clone(),
                    RefLocalRot = (Quaternion[])tree.RefLocalRot.Clone(),
                };
                baseline.RebuildReference();
                Assert(profile.Skeleton.RefModelRot.SequenceEqual(baseline.RefModelRot));
            }
        });
        if (vmdPath != null && File.Exists(vmdPath)) Test("胸部骨特化：真实VMD身体输出逐位不变且随机Seek无累积", () =>
        {
            var input = RecordedInput(); var profile = new TargetRigProfile(input.Reference, input.Positions, input.Scales);
            var baseline = new TargetRigProfile(input.Reference);
            var motion = VmdAnimation.Build(VmdFile.Parse(vmdPath)); var cal = new Calibration();
            var adjusted = new Retargeter(); var original = new Retargeter();
            adjusted.TestSetup(profile, motion, cal); original.TestSetup(baseline, motion, cal);
            foreach (var frame in new[] { 0f, 2473.8f, 2926.5f, 4365.6f, 4891f, 0f, 1773.31f })
            {
                Assert(adjusted.Evaluate(frame, cal) && original.Evaluate(frame, cal));
                Assert(adjusted.LocalRotations.SequenceEqual(original.LocalRotations));
                Assert(adjusted.ModelPositions.SequenceEqual(original.ModelPositions));
                Assert(adjusted.Profile!.Left == original.Profile!.Left && adjusted.Profile.Up == original.Profile.Up && adjusted.Profile.Back == original.Profile.Back);
                foreach (var bone in profile.SuppressedBreastBones) Assert(!adjusted.RotationWrites[bone.Index]);
            }
        });
        Console.WriteLine($"胸部骨特化回归：{count - failed}/{count} 通过");
        return failed;
    }

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
    private static Input RecordedInput()
    {
        var tree = JsonSerializer.Deserialize<Capture[]>(File.ReadAllText(Fixture("Acceptance-v1.1.4.json")), Json)![0].Target;
        Assert(tree.Names.Length == 106);
        var samples = JsonSerializer.Deserialize<ChestSample[]>(File.ReadAllText(Fixture("Suppressed-breast-leaves.json")), Json)!;
        foreach (var sample in samples)
        {
            var index = Array.IndexOf(tree.Names, sample.Name); Assert(index >= 0);
            tree.RefLocalPos[index] = sample.ReferenceLocalPosition; tree.RefLocalScale[index] = sample.ReferenceLocalScale;
            tree.RefLocalRot[index] = sample.ReferenceLocalRotation;
        }
        tree.RebuildReference();
        var positions = (Vector3[])tree.RefLocalPos.Clone(); var scales = (Vector3[])tree.RefLocalScale.Clone(); var rotations = (Quaternion[])tree.RefLocalRot.Clone();
        foreach (var sample in samples)
        {
            var index = tree.Find(sample.Name); positions[index] = sample.CurrentLocalPosition; scales[index] = sample.CurrentLocalScale; rotations[index] = sample.CurrentLocalRotation;
        }
        return new(tree, positions, scales, rotations);
    }
    private static VmdAnimation Motion() => VmdAnimation.Build(new()
    {
        BoneFrames = [new() { Bone = "上半身", Frame = 0, Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, .3f),
            Interp = [20, 20, 20, 20, 20, 20, 20, 20, 107, 107, 107, 107, 107, 107, 107, 107, ..new byte[48]] }],
    });
    private static void Assert(bool condition) { if (!condition) throw new Exception("assertion failed"); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new Exception("unsafe suppressed chest input accepted");
    }
}
