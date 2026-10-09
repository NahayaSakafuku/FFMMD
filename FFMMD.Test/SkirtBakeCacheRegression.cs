using System.Numerics;
using FFMMD.Posing;
using FFMMD.Retarget;
using FFMMD.Skirt;

internal static class SkirtBakeCacheRegression
{
    private const string Motion = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Reference = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    public static int Run()
    {
        var failed = 0; var count = 0;
        void Test(string name, Action body)
        {
            count++;
            try { body(); Console.WriteLine($"  ✔ {name}"); }
            catch (Exception e) { failed++; Console.WriteLine($"  ✘ {name}: {e.Message}"); }
        }

        Test("schema1 cache parses exact 18 mapping and validates hashes", () =>
        {
            var result = SkirtBakeCache.Read(SkirtBakeCache.Serialize(Fixture()), Motion, Reference);
            Require(result.Success, result.Error ?? result.Status);
            Require(result.Cache!.FrameCount == 3 && result.Cache.Bones.Count == 18, "frame/mapping count");
            Require(result.Cache.Bones.Select(b => b.TargetName).SequenceEqual(SkirtBakeCache.RequiredTargetNames), "canonical target order");
            Require(result.Cache.SourceBasis == Matrix4x4.Identity, "source basis");
            var floatingFps = System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(SkirtBakeCache.Serialize(Fixture())).Replace("\"Fps\":30,", "\"Fps\":30.0,", StringComparison.Ordinal));
            Require(SkirtBakeCache.Read(floatingFps, Motion, Reference).Success, "30.0 JSON FPS should parse");
        });

        Test("motion and optional PMX identity are enforced", () =>
        {
            var bytes = SkirtBakeCache.Serialize(Fixture());
            Reject(bytes, "motion", "different" + Motion[1..], Reference);
            Reject(bytes, "reference", Motion, "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc");
            var optional = SkirtBakeCache.Read(bytes, Motion);
            Require(optional.Success, optional.Error ?? "optional reference hash rejected");
        });

        Test("builtin physics metadata requires engine version without external tools", () =>
        {
            var document = Fixture(); document.Solver.RecipeVersion = 3;
            document.Solver.Engine = BuiltinSkirtBaker.Engine; document.Solver.EngineVersion = "Bullet 3.25";
            document.Solver.BlenderVersion = document.Solver.AddonVersion = null;
            Require(SkirtBakeCache.Read(SkirtBakeCache.Serialize(document), Motion, Reference).Success, "Builtin engine metadata rejected");
            document.Solver.EngineVersion = null; Reject(document, "engine version");
        });

        Test("bundled reference matches the measured 206 pair exclusions and 168 helpers", () =>
        {
            var reference = SkirtPhysicsReference.Builtin();
            var pairs = BuiltinSkirtBaker.BuildCollisionFilter(reference.Physics, 1.5f);
            var jointPairs = reference.Physics.Joints.Select(j => j.RigidBodyA < j.RigidBodyB ?
                (j.RigidBodyA, j.RigidBodyB) : (j.RigidBodyB, j.RigidBodyA)).ToHashSet();
            Require(pairs.Count == 206 && pairs.Count(jointPairs.Contains) == 38 && pairs.Count(p => !jointPairs.Contains(p)) == 168,
                "Collision graph differs from the independent Blender reference dump.");
            var skirtIds = reference.Physics.RigidBodies.Where(b => b.Name.StartsWith("Skirt_", StringComparison.Ordinal)).Select(b => b.Index).ToHashSet();
            Require(pairs.Count(p => skirtIds.Contains(p.A) && skirtIds.Contains(p.B)) == 153, "Skirt self-collision filtering changed.");
        });

        Test("random seek, loop and NaN sampling are deterministic", () =>
        {
            var loaded = Load(Fixture()); var a = new Quaternion[18]; var b = new Quaternion[18];
            loaded.Sample(1.25f, a); loaded.Sample(1.25f, b); Require(a.SequenceEqual(b), "random sample changed");
            loaded.Sample(-100, a); loaded.Sample(0, b); Require(a.SequenceEqual(b), "clamped lower frame differs");
            loaded.Sample(float.NaN, a); Require(a.SequenceEqual(b), "NaN frame is not deterministic");
            loaded.SampleLoop(3, a); loaded.SampleLoop(0, b); Require(a.SequenceEqual(b), "loop boundary differs");
            loaded.SampleLoop(-1, a); loaded.SampleLoop(2, b); Require(a.SequenceEqual(b), "negative loop differs");
            loaded.SampleLoop(float.PositiveInfinity, a); loaded.Sample(0, b); Require(a.SequenceEqual(b), "nonfinite loop is not deterministic");
        });

        Test("fractional rotation uses shortest quaternion path", () =>
        {
            var d = Fixture(); d.Bones[0].RelativeRotations![0] = [0, 0, 0, 1]; d.Bones[0].RelativeRotations![1] = [0, 0, 0, -1];
            var loaded = Load(d); var q = new Quaternion[18]; loaded.Sample(.5f, q);
            Require(MathF.Abs(MathF.Abs(q[0].W) - 1) < 1e-5f && MathF.Abs(q[0].X) < 1e-5f, $"antipodal interpolation drifted: {q[0]}");
        });

        Test("basis, fps, frame and sample budgets reject malformed cache", () =>
        {
            var d = Fixture(); d.SourceBasis.Left = [.70710677f, .70710677f, 0]; Reject(d, "orthogonal");
            d = Fixture(); d.Fps = 60; Reject(d, "FPS");
            d = Fixture(); d.FrameCount = 0; Reject(d, "FrameCount");
            d = Fixture(); d.Bones[0].RelativeRotations = [[0, 0, 0, 1]]; Reject(d, "one rotation");
            d = Fixture(); d.Bones.RemoveAt(0); Reject(d, "exactly 18");
        });

        Test("duplicate and missing targets are rejected", () =>
        {
            var d = Fixture(); d.Bones[1].TargetName = d.Bones[0].TargetName; Reject(d, "Duplicate target");
            d = Fixture(); d.Bones[0].TargetName = "unknown"; Reject(d, "Required target");
            d = Fixture(); d.Bones[1].SourceName = d.Bones[0].SourceName; Reject(d, "Duplicate source");
            d = Fixture(); d.Bones[6].ParentSourceName = "missing-source"; Reject(d, "parent topology");
        });

        Test("quaternion zero, nonfinite and solver limits reject", () =>
        {
            var d = Fixture(); d.Bones[0].RelativeRotations![0] = [0, 0, 0, 0]; Reject(d, "zero quaternion");
            d = Fixture(); var malformed = System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(SkirtBakeCache.Serialize(d)).Replace("[0,0,0,1]", "[NaN,0,0,1]", StringComparison.Ordinal));
            Reject(malformed, "JSON");
            d = Fixture(); d.Solver.Substeps = 0; Reject(d, "Solver parameters");
            d = Fixture(); d.Solver.Scale = -1; Reject(d, "Solver parameters");
            d = Fixture(); d.Solver.PointCacheBaked = false; Reject(d, "PointCacheBaked");
        });

        Test("unknown JSON and oversized payload are rejected", () =>
        {
            var malformed = System.Text.Encoding.UTF8.GetBytes("{\"SchemaVersion\":1,");
            Reject(malformed, "JSON");
            var huge = new byte[SkirtBakeCache.MaxFileBytes + 1];
            var result = SkirtBakeCache.Read(huge, Motion, Reference);
            Require(!result.Success && result.Error!.Contains("exceeds", StringComparison.OrdinalIgnoreCase), "oversized payload accepted");
            var missing = System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(SkirtBakeCache.Serialize(Fixture())).Replace("\"SourceBasis\":{\"Left\":[1,0,0],\"Up\":[0,1,0],\"Back\":[0,0,1]},", "", StringComparison.Ordinal));
            Reject(missing, "required properties");
        });

        Test("identity bake preserves nonidentity bind and all body transforms", () =>
        {
            var d = Fixture(); SetRotations(d, Quaternion.Identity);
            var tree = Skeleton(nonidentity: true); var beforeRot = (Quaternion[])tree.RefLocalRot.Clone();
            var beforePos = (Vector3[])tree.RefLocalPos.Clone(); var beforeScale = (Vector3[])tree.RefLocalScale.Clone();
            var binding = SkirtBakeBinding.Build(Load(d), new TargetRigProfile(tree));
            Require(binding.Prepare(1, 1), "prepare failed");
            for (var i = 0; i < binding.BoneIndices.Length; i++)
                Require(Angle(binding.LocalRotations[i], beforeRot[binding.BoneIndices[i]]) < 1e-3f, "identity changed native bind");
            Require(tree.RefLocalRot.SequenceEqual(beforeRot) && tree.RefLocalPos.SequenceEqual(beforePos) && tree.RefLocalScale.SequenceEqual(beforeScale), "body data mutated");
        });

        Test("relative rotation transports correctly through reflection basis", () =>
        {
            var d = Fixture(); d.SourceBasis.Back = [0, 0, -1];
            var q = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 3); SetRotations(d, q);
            var binding = SkirtBakeBinding.Build(Load(d), new TargetRigProfile(Skeleton()));
            Require(binding.Prepare(0, 1), "reflection prepare failed");
            // For diag(1,1,-1), an axial vector transforms with determinant -1.
            var expected = Quaternion.CreateFromAxisAngle(-Vector3.UnitX, MathF.PI / 3);
            Require(Angle(binding.LocalRotations[0], expected) < 1e-3f, "reflection rotation sign wrong");
        });

        Test("row basis convention transports a rotated source coordinate system", () =>
        {
            var d = Fixture(); d.SourceBasis.Left = [0, 0, -1]; d.SourceBasis.Up = [0, 1, 0]; d.SourceBasis.Back = [1, 0, 0];
            var q = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 3); SetRotations(d, q);
            var binding = SkirtBakeBinding.Build(Load(d), new TargetRigProfile(Skeleton()));
            Require(binding.Prepare(0, 1), "rotated basis prepare failed");
            var expected = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 3);
            Require(Angle(binding.LocalRotations[0], expected) < 1e-3f, "rows/columns convention inverted");
        });

        Test("binding random seek idempotence and zero amplitude restore bind", () =>
        {
            var tree = Skeleton(nonidentity: true); var binding = SkirtBakeBinding.Build(Load(Fixture()), new TargetRigProfile(tree));
            Require(binding.Prepare(1.25f, 1), "prepare failed"); var expected = (Quaternion[])binding.LocalRotations.Clone();
            binding.Prepare(2, .5f); binding.Prepare(0, 0); binding.Prepare(1.25f, 1);
            Require(expected.SequenceEqual(binding.LocalRotations), "random seek accumulated state");
            binding.Prepare(2, 0);
            for (var i = 0; i < binding.BoneIndices.Length; i++) Require(Angle(binding.LocalRotations[i], tree.RefLocalRot[binding.BoneIndices[i]]) < 1e-3f, "zero amplitude lost bind");
            Require(!binding.Prepare(float.NaN, 1) && !binding.Prepare(1, float.PositiveInfinity), "nonfinite sampling accepted");
        });

        Console.WriteLine($"\n裙摆 bake cache 回归：{count - failed}/{count} 通过");
        return failed;
    }

    private static SkirtBakeCache.Loaded Load(SkirtBakeCache.Document document)
    {
        var result = SkirtBakeCache.Read(SkirtBakeCache.Serialize(document), Motion, Reference);
        Require(result.Success, result.Error ?? result.Status); return result.Cache!;
    }

    private static void Reject(SkirtBakeCache.Document document, string expected)
        => Reject(SkirtBakeCache.Serialize(document), expected, Motion, Reference);

    private static void Reject(byte[] bytes, string expected)
        => Reject(bytes, expected, Motion, Reference);

    private static void Reject(byte[] bytes, string expected, string motion, string? reference)
    {
        var result = SkirtBakeCache.Read(bytes, motion, reference);
        Require(!result.Success && result.Status == "Rejected" && result.Error?.Contains(expected, StringComparison.OrdinalIgnoreCase) == true,
            $"expected rejection {expected}, got {result.Status}: {result.Error}");
    }

    private static SkirtBakeCache.Document Fixture()
    {
        var document = new SkirtBakeCache.Document
        {
            SchemaVersion = 1, MotionSha256 = Motion, ReferencePmxSha256 = Reference,
            Fps = 30, StartFrame = 0, FrameCount = 3,
            SourceBasis = new() { Left = [1, 0, 0], Up = [0, 1, 0], Back = [0, 0, 1] },
            Solver = new() { Engine = "mmdtools", BlenderVersion = "4.2", AddonVersion = "2.0", Substeps = 4, Iterations = 8, WarmupFrames = 10, Scale = 1, PointCacheBaked = true },
        };
        foreach (var target in SkirtBakeCache.RequiredTargetNames)
        {
            var rotations = new float[3][];
            for (var frame = 0; frame < rotations.Length; frame++)
            {
                var angle = frame * .2f;
                rotations[frame] = [0, MathF.Sin(angle / 2), 0, MathF.Cos(angle / 2)];
            }
            var parts = target.Split('_'); var layer = parts[3];
            parts[3] = layer == "b" ? "a" : "b";
            var parentName = layer == "a" ? "下半身" : "Skirt_" + string.Join('_', parts);
            document.Bones.Add(new() { SourceName = "Skirt_" + target, TargetName = target, ParentSourceName = parentName, RelativeRotations = rotations });
        }
        return document;
    }

    private static void SetRotations(SkirtBakeCache.Document document, Quaternion q)
    {
        foreach (var bone in document.Bones)
            for (var frame = 0; frame < document.FrameCount; frame++) bone.RelativeRotations![frame] = [q.X, q.Y, q.Z, q.W];
    }

    private static SkeletonTree Skeleton(bool nonidentity = false)
    {
        var names = new List<string> { "n_root", "j_kosi", "j_asi_a_l", "j_asi_a_r", "j_kao", "j_asi_d_l", "j_asi_e_l" };
        var parents = new List<short> { -1, 0, 1, 1, 1, 2, 5 };
        var positions = new List<Vector3> { Vector3.Zero, new(0, 1, 0), new(.1f, 0, 0), new(-.1f, 0, 0), new(0, 1, 0), new(0, -1, 0), new(0, 0, -.2f) };
        var rotations = Enumerable.Repeat(Quaternion.Identity, names.Count).ToList();
        foreach (var target in SkirtBakeCache.RequiredTargetNames)
        {
            // Target names are j_sk_<direction>_<layer>_<side>.
            var parts = target.Split('_'); var layer = parts[3]; parts[3] = layer == "b" ? "a" : "b";
            var p = layer == "a" ? (short)1 : (short)names.IndexOf(string.Join('_', parts));
            names.Add(target); parents.Add(p); positions.Add(new Vector3(.1f, -.2f, .1f));
            rotations.Add(nonidentity ? Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .1f) : Quaternion.Identity);
        }
        var tree = new SkeletonTree { Names = names.ToArray(), Parent = parents.ToArray(), RefLocalPos = positions.ToArray(), RefLocalRot = rotations.ToArray(), RefLocalScale = Enumerable.Repeat(Vector3.One, names.Count).ToArray() };
        tree.RebuildReference(); return tree;
    }

    private static float Angle(Quaternion a, Quaternion b) => 2 * MathF.Acos(Math.Clamp(MathF.Abs(Quaternion.Dot(a, b)), 0, 1));

    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
}
