using System.Numerics;
using FFMMD.Retarget;
using FFMMD.Skirt;

internal static class KinematicColliderDriverRegression
{
    public static int Run()
    {
        var failures = 0; var count = 0;
        void Check(string name, Action body)
        {
            count++;
            try { body(); Console.WriteLine($"  ✔ {name}"); }
            catch (Exception e) { failures++; Console.WriteLine($"  ✘ {name}: {e.Message}"); }
        }
        Check("碰撞驱动首帧使用原始完整姿态", () =>
        {
            var (rig, physics) = Fixture(); var pose = Pose(rig); var driver = new KinematicColliderDriver(rig, physics);
            var actual = driver.GetFrame(pose, .08f);
            for (var i = 0; i < actual.Length; i++) Same(actual[i], Raw(rig, physics.RigidBodies[i], pose, .08f));
        });
        Check("碰撞驱动胶囊消除180度轴向扭转且精确保留原中心与有向长轴", () =>
        {
            var (rig, physics) = Fixture(); var pose = Pose(rig); var driver = new KinematicColliderDriver(rig, physics);
            var first = driver.GetFrame(pose, .08f);
            var axis = Vector3.Transform(Vector3.UnitY, Bind(physics.RigidBodies[0]));
            pose.Rotations[0] = Quaternion.Normalize(pose.Rotations[0] * Quaternion.CreateFromAxisAngle(axis, MathF.PI));
            var second = driver.GetFrame(pose, .08f);
            var raw = Raw(rig, physics.RigidBodies[0], pose, .08f);
            Near(second[0].Position, raw.Position);
            Near(Vector3.Transform(Vector3.UnitY, second[0].Rotation), Vector3.Transform(Vector3.UnitY, raw.Rotation));
            Require(Angle(first[0].Rotation, second[0].Rotation) < .001, "Axial flip injected a capsule angular step.");
            Require(Vector3.Distance(first[0].Position, second[0].Position) > .01f, "Offset capsule center was incorrectly frozen.");
            Near(second[1].Position, first[1].Position);
            SameRotation(second[1].Rotation, first[1].Rotation);
            Same(second[2], Raw(rig, physics.RigidBodies[2], pose, .08f));
            Same(second[3], Raw(rig, physics.RigidBodies[3], pose, .08f));
        });
        Check("碰撞驱动保留180度有向长轴反转", () =>
        {
            var (rig, physics) = Fixture(); var pose = Pose(rig); var driver = new KinematicColliderDriver(rig, physics);
            var first = driver.GetFrame(pose, 1);
            var oldAxis = Vector3.Transform(Vector3.UnitY, first[0].Rotation);
            var tangent = Vector3.Normalize(Vector3.Cross(oldAxis, Vector3.UnitX));
            pose.Rotations[0] = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(tangent, MathF.PI) * pose.Rotations[0]);
            var second = driver.GetFrame(pose, 1);
            var raw = Raw(rig, physics.RigidBodies[0], pose, 1);
            var newAxis = Vector3.Transform(Vector3.UnitY, second[0].Rotation);
            Near(newAxis, Vector3.Transform(Vector3.UnitY, raw.Rotation));
            Require(Vector3.Dot(oldAxis, newAxis) < -.99999f, "Driver chose the unoriented nearest axis.");
        });
        Check("碰撞驱动连续摆动仅产生最小旋转且不累计长轴误差", () =>
        {
            var (rig, physics) = Fixture(); var pose = Pose(rig); var driver = new KinematicColliderDriver(rig, physics);
            var bindAxis = Vector3.Transform(Vector3.UnitY, Bind(physics.RigidBodies[0]));
            NativeBodyTransform? previous = null;
            for (var f = 0; f < 512; f++)
            {
                var swing = Quaternion.CreateFromYawPitchRoll(.4f * MathF.Sin(f * .08f), .3f * MathF.Cos(f * .11f), .2f * MathF.Sin(f * .07f));
                pose.Rotations[0] = Quaternion.Normalize(swing * Quaternion.CreateFromAxisAngle(bindAxis, f % 2 == 0 ? .2f : MathF.PI + .2f));
                var frame = driver.GetFrame(pose, .08f); var raw = Raw(rig, physics.RigidBodies[0], pose, .08f);
                Near(frame[0].Position, raw.Position);
                var currentAxis = Vector3.Transform(Vector3.UnitY, frame[0].Rotation);
                Near(currentAxis, Vector3.Transform(Vector3.UnitY, raw.Rotation), 4e-6f);
                if (previous is { } p)
                {
                    var oldAxis = Vector3.Normalize(Vector3.Transform(Vector3.UnitY, p.Rotation));
                    var axisAngle = Math.Atan2(Vector3.Cross(oldAxis, currentAxis).Length(), Vector3.Dot(oldAxis, currentAxis)) * 180 / Math.PI;
                    Require(Math.Abs(Angle(p.Rotation, frame[0].Rotation) - axisAngle) < .003, "Transport includes an axial rotation.");
                }
                previous = frame[0];
            }
        });
        Check("碰撞驱动精确反向及bind长轴为X时fallback保持有向轴", () =>
        {
            foreach (var euler in new[] { Vector3.Zero, new Vector3(0, 0, -MathF.PI / 2) })
            {
                var rig = new SourceRigDefinition { Bones = [new SourceBone { Name = "Root" }] };
                var body = new PmxRigidBody { Index = 0, BoneIndex = 0, Mode = PmxRigidBodyMode.Bone,
                    Shape = PmxRigidBodyShape.Capsule, RotationEuler = euler, Position = new(.3f, 1, .7f) };
                var driver = new KinematicColliderDriver(rig, new PmxPhysicsProfile { RigidBodies = [body] });
                var pose = new SolvedSourcePose(1, 0); pose.Rotations[0] = Quaternion.Identity;
                var first = driver.GetFrame(pose, 1);
                // This exact quaternion avoids sin(pi/2) rounding. The raw
                // target axis is exactly the previous axis negated.
                pose.Rotations[0] = new(0, 0, 1, 0);
                var second = driver.GetFrame(pose, 1); var raw = Raw(rig, body, pose, 1);
                Near(second[0].Position, raw.Position);
                Near(Vector3.Transform(Vector3.UnitY, second[0].Rotation), Vector3.Transform(Vector3.UnitY, raw.Rotation));
                Require(Vector3.Dot(Vector3.Transform(Vector3.UnitY, first[0].Rotation), Vector3.Transform(Vector3.UnitY, second[0].Rotation)) < -.99999f,
                    "Exact antipodal fallback did not reverse its directed axis.");
            }
        });
        Check("碰撞驱动真实关节及同骨刚体始终保留完整原旋转", () =>
        {
            var (rig, physics) = Fixture(); var pose = Pose(rig); var driver = new KinematicColliderDriver(rig, physics);
            driver.GetFrame(pose, .08f);
            pose.Rotations[1] = Quaternion.CreateFromYawPitchRoll(2, -.8f, .5f);
            pose.Rotations[2] = Quaternion.CreateFromYawPitchRoll(-2, .7f, -.6f);
            var frame = driver.GetFrame(pose, .08f);
            foreach (var i in new[] { 4, 5, 6, 7, 8 }) Same(frame[i], Raw(rig, physics.RigidBodies[i], pose, .08f));
        });
        Check("碰撞驱动球体固定首帧旋转同时允许真实中心平移", () =>
        {
            var (rig, physics) = Fixture(); var pose = Pose(rig); var driver = new KinematicColliderDriver(rig, physics);
            var first = driver.GetFrame(pose, .08f);
            pose.Rotations[4] = Quaternion.CreateFromYawPitchRoll(1, 2, 3);
            pose.Positions[4] += new Vector3(3, 1, -2);
            pose.Positions[0] += new Vector3(-1, 2, 3);
            var frame = driver.GetFrame(pose, .08f);
            Near(frame[12].Position, Raw(rig, physics.RigidBodies[12], pose, .08f).Position);
            SameRotation(frame[12].Rotation, first[12].Rotation);
            Near(frame[1].Position, first[1].Position + new Vector3(-1, 2, 3) * .08f);
            SameRotation(frame[1].Rotation, first[1].Rotation);
            Same(frame[13], first[13]); Same(frame[14], first[14]);
        });
        Check("碰撞驱动不兼容胶囊长轴采用完整原姿态", () =>
        {
            var (rig, physics) = Fixture(); var pose = Pose(rig); var driver = new KinematicColliderDriver(rig, physics);
            driver.GetFrame(pose, .08f);
            pose.Rotations[3] = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI);
            var frame = driver.GetFrame(pose, .08f);
            Same(frame[9], Raw(rig, physics.RigidBodies[9], pose, .08f));
            Same(frame[10], Raw(rig, physics.RigidBodies[10], pose, .08f));
            Near(frame[11].Position, Raw(rig, physics.RigidBodies[11], pose, .08f).Position);
        });
        Check("碰撞驱动同骨小角偏轴双胶囊保守采用原姿态", () =>
        {
            foreach (var angle in new[] { .001f, MathF.PI - .001f })
            {
                var rig = new SourceRigDefinition { Bones = [new() { Name = "Bone", RestPosition = Vector3.Zero }] };
                var physics = new PmxPhysicsProfile
                {
                    RigidBodies = [
                        new() { Index = 0, BoneIndex = 0, Shape = PmxRigidBodyShape.Capsule, Position = new(.4f, 1, .5f) },
                        new() { Index = 1, BoneIndex = 0, Shape = PmxRigidBodyShape.Capsule, Position = new(-.3f, 1, .2f), RotationEuler = new(0, 0, angle) },
                    ],
                };
                var pose = new SolvedSourcePose(1, 0); pose.Rotations[0] = Quaternion.Identity;
                var driver = new KinematicColliderDriver(rig, physics); driver.GetFrame(pose, 1);
                pose.Rotations[0] = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI);
                var frame = driver.GetFrame(pose, 1);
                for (var i = 0; i < frame.Length; i++)
                {
                    var raw = Raw(rig, physics.RigidBodies[i], pose, 1);
                    Same(frame[i], raw);
                    Near(Vector3.Transform(Vector3.UnitY, frame[i].Rotation), Vector3.Transform(Vector3.UnitY, raw.Rotation));
                }
            }
        });
        Check("碰撞驱动Reset与新实例逐帧可重复且不修改输入或旧输出", () =>
        {
            var (rig, physics) = Fixture(); var driver = new KinematicColliderDriver(rig, physics);
            NativeBodyTransform[][] Sequence(KinematicColliderDriver value)
            {
                var result = new NativeBodyTransform[4][];
                for (var i = 0; i < result.Length; i++)
                {
                    var pose = Pose(rig); pose.Rotations[0] = Quaternion.CreateFromYawPitchRoll(i * 1.7f, i * .2f, i * .1f);
                    var positions = (Vector3[])pose.Positions.Clone(); var rotations = (Quaternion[])pose.Rotations.Clone();
                    result[i] = value.GetFrame(pose, .08f);
                    Require(positions.SequenceEqual(pose.Positions) && rotations.SequenceEqual(pose.Rotations), "Input pose was modified.");
                }
                return result;
            }
            var first = Sequence(driver); var saved = first.Select(f => f.ToArray()).ToArray();
            driver.Reset(); var second = Sequence(driver); var third = Sequence(new(rig, physics));
            for (var f = 0; f < first.Length; f++)
                for (var i = 0; i < first[f].Length; i++)
                {
                    Require(first[f][i].Equals(saved[f][i]), "Old output mutated.");
                    Require(first[f][i].Equals(second[f][i]) && first[f][i].Equals(third[f][i]), "Reset sequence is not bitwise repeatable.");
                }
        });
        Check("碰撞驱动拒绝无效输入且拒绝帧不会污染后续transport", () =>
        {
            var (rig, physics) = Fixture(); var driver = new KinematicColliderDriver(rig, physics); var control = new KinematicColliderDriver(rig, physics);
            var pose = Pose(rig); driver.GetFrame(pose, .08f); control.GetFrame(pose, .08f);
            static void Reject(Action action)
            {
                try { action(); } catch (ArgumentException) { return; }
                throw new InvalidDataException("Invalid input was accepted.");
            }
            foreach (var scale in new[] { 0f, -1f, float.NaN, float.PositiveInfinity }) Reject(() => driver.GetFrame(pose, scale));
            var bad = Pose(rig); bad.Positions[4] = new(float.NaN, 0, 0); Reject(() => driver.GetFrame(bad, .08f));
            bad = Pose(rig); bad.Rotations[4] = default; Reject(() => driver.GetFrame(bad, .08f));
            bad = Pose(rig); bad.Rotations[0] = new(float.MaxValue, 0, 0, 1); Reject(() => driver.GetFrame(bad, .08f));
            bad = Pose(rig); bad.Positions[0] = new(float.MaxValue, 0, 0); Reject(() => driver.GetFrame(bad, 100));
            Reject(() => driver.GetFrame(new SolvedSourcePose(1, 0), .08f));
            pose.Rotations[0] = Quaternion.CreateFromYawPitchRoll(1, .3f, -.6f);
            var actual = driver.GetFrame(pose, .08f); var expected = control.GetFrame(pose, .08f);
            for (var i = 0; i < actual.Length; i++) Require(actual[i].Equals(expected[i]), "Rejected input changed temporal state.");
        });
        Console.WriteLine($"裙骨运动学碰撞驱动: {count - failures}/{count}");
        return failures;
    }

    private static (SourceRigDefinition Rig, PmxPhysicsProfile Physics) Fixture()
    {
        var rig = new SourceRigDefinition { Bones = Enumerable.Range(0, 5).Select(i => new SourceBone { Name = $"Bone{i}", RestPosition = new(i, 2 * i, -i) }).ToArray() };
        var bodies = new List<PmxRigidBody>();
        void Add(int bone, PmxRigidBodyShape shape, PmxRigidBodyMode mode = PmxRigidBodyMode.Bone, Vector3? euler = null)
        {
            bodies.Add(new() { Index = bodies.Count, BoneIndex = bone, Shape = shape, Mode = mode,
                Position = (bone < 0 ? Vector3.Zero : rig.Bones[bone].RestPosition) + new Vector3(.4f, 1, .5f), RotationEuler = euler ?? new(.3f, .2f, .1f) });
        }
        Add(0, PmxRigidBodyShape.Capsule); Add(0, PmxRigidBodyShape.Sphere, euler: new(.4f, -.2f, .6f));
        Add(0, PmxRigidBodyShape.Box); Add(0, PmxRigidBodyShape.Capsule, PmxRigidBodyMode.Physics);
        Add(1, PmxRigidBodyShape.Capsule); Add(1, PmxRigidBodyShape.Sphere);
        Add(2, PmxRigidBodyShape.Sphere); Add(2, PmxRigidBodyShape.Capsule); Add(2, PmxRigidBodyShape.Box, PmxRigidBodyMode.PhysicsAndBone);
        Add(3, PmxRigidBodyShape.Capsule, euler: Vector3.Zero); Add(3, PmxRigidBodyShape.Capsule, euler: new(0, 0, MathF.PI / 2));
        Add(3, PmxRigidBodyShape.Sphere); Add(4, PmxRigidBodyShape.Sphere);
        Add(-1, PmxRigidBodyShape.Capsule); Add(-1, PmxRigidBodyShape.Sphere);
        return (rig, new() { RigidBodies = bodies.ToArray(), Joints = [new() { RigidBodyA = 4, RigidBodyB = 6 }] });
    }
    private static SolvedSourcePose Pose(SourceRigDefinition rig)
    {
        var pose = new SolvedSourcePose(rig.Bones.Length, 0);
        for (var i = 0; i < rig.Bones.Length; i++) { pose.Positions[i] = rig.Bones[i].RestPosition + new Vector3(.1f, .2f, .3f); pose.Rotations[i] = Quaternion.CreateFromYawPitchRoll(.4f, -.2f, .3f); }
        return pose;
    }
    private static NativeBodyTransform Raw(SourceRigDefinition rig, PmxRigidBody body, SolvedSourcePose pose, float scale)
    {
        var rotation = body.BoneIndex < 0 ? Quaternion.Identity : Quaternion.Normalize(pose.Rotations[body.BoneIndex]);
        return new(body.BoneIndex < 0 ? body.Position * scale : (pose.Positions[body.BoneIndex] + Vector3.Transform(body.Position - rig.Bones[body.BoneIndex].RestPosition, rotation)) * scale,
            Quaternion.Normalize(rotation * Bind(body)));
    }
    private static Quaternion Bind(PmxRigidBody body) => Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.UnitY, body.RotationEuler.Y) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, body.RotationEuler.X) * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, body.RotationEuler.Z));
    private static void Same(NativeBodyTransform actual, NativeBodyTransform expected) { Near(actual.Position, expected.Position); SameRotation(actual.Rotation, expected.Rotation); }
    private static void Near(Vector3 actual, Vector3 expected, float tolerance = 2e-6f) => Require(Vector3.Distance(actual, expected) <= tolerance, $"Vector mismatch: {actual} vs {expected}.");
    private static void SameRotation(Quaternion actual, Quaternion expected) => Require(Angle(actual, expected) < .001, $"Quaternion mismatch: {Angle(actual, expected)} degrees.");
    private static double Angle(Quaternion a, Quaternion b)
    {
        var dot = (double)a.X * b.X + (double)a.Y * b.Y + (double)a.Z * b.Z + (double)a.W * b.W;
        var an = (double)a.X * a.X + (double)a.Y * a.Y + (double)a.Z * a.Z + (double)a.W * a.W;
        var bn = (double)b.X * b.X + (double)b.Y * b.Y + (double)b.Z * b.Z + (double)b.W * b.W;
        return 2 * Math.Acos(Math.Min(1, Math.Abs(dot) / Math.Sqrt(an * bn))) * 180 / Math.PI;
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
