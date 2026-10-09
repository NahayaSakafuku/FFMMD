using System.Numerics;
using FFMMD.Skirt;

namespace FFMMD.Test;

internal static class SkirtBulletRegression
{
    public static int Run(string? libraryPath = null, string? referencePmxPath = null)
    {
        libraryPath ??= Path.Combine(AppContext.BaseDirectory, "FFMMD.Bullet.dll");
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess || !File.Exists(libraryPath))
        {
            Console.WriteLine("SKIP SkirtBullet: bundled Windows x64 worker is not present.");
            return 0;
        }
        var failed = 0; var passed = 0;
        void Test(string name, Action action)
        {
            try { action(); passed++; Console.WriteLine($"PASS SkirtBullet/{name}"); }
            catch (Exception e) { failed++; Console.WriteLine($"FAIL SkirtBullet/{name}: {e}"); }
        }
        if (!SkirtBullet.TryInitialize(libraryPath, out var error))
        {
            Console.WriteLine($"FAIL SkirtBullet/load: {error}"); return 1;
        }
        Test("ABI-and-engine", () => Require(SkirtBullet.EngineVersion?.Contains("Bullet 3.25") == true, "Wrong engine version."));
        Test("gravity", () =>
        {
            using var world = new SkirtBullet(new Vector3(0, -9.81f, 0));
            var body = world.AddBody(Body(0, 1, new Vector3(.25f), new Vector3(0, 3, 0)));
            Advance(world, 120);
            Require(world.GetTransform(body).Position.Y < 2, "Dynamic body did not fall.");
        });
        Test("zero-stiffness-Spring2-retains-damping", () =>
        {
            using var world = new SkirtBullet(new Vector3(0, -9.81f, 0));
            var anchor = Body(0, 0, new Vector3(.1f), Vector3.Zero); anchor.CollisionMask = 0;
            var falling = Body(0, 2, new Vector3(.1f), Vector3.Zero); falling.CollisionMask = 0;
            var a = world.AddBody(anchor); var b = world.AddBody(falling);
            world.AddJoint(new NativeJointDescription
            {
                BodyA = a, BodyB = b, Rotation = Quaternion.Identity,
                // Lower > upper releases a degree of freedom. Zero spring
                // stiffness still supplies mmdtools' default .5 damping.
                LinearLower = Vector3.One, LinearUpper = -Vector3.One,
                AngularLower = Vector3.One, AngularUpper = -Vector3.One,
            });
            Advance(world, 240);
            var y = world.GetTransform(b).Position.Y;
            Require(y is < -1 and > -4.6f, $"Zero-stiffness joint lost velocity damping (Y={y}).");
        });
        Test("raw-PMX-collision-mask", () =>
        {
            var enabled = FloorResult(0, new Vector3(.25f), true);
            var disabled = FloorResult(0, new Vector3(.25f), false);
            Require(MathF.Abs(enabled - .75f) < .035f, $"Sphere/floor contact is {enabled}.");
            Require(disabled < -5, "A cleared mask bit did not disable collision.");
        });
        Test("box-half-extents", () =>
        {
            var y = FloorResult(1, new Vector3(.3f, .4f, .2f), true);
            Require(MathF.Abs(y - .9f) < .035f, $"Box/floor contact is {y}.");
        });
        Test("capsule-cylinder-height", () =>
        {
            var y = FloorResult(2, new Vector3(.4f, 2, 0), true);
            Require(MathF.Abs(y - 1.9f) < .04f, $"Capsule/floor contact is {y}.");
        });
        Test("animated-capsule-pushes-box", () =>
        {
            using var world = new SkirtBullet(Vector3.Zero);
            var leg = world.AddBody(Body(2, 0, new Vector3(.5f, 2, 0), Vector3.Zero));
            var skirt = world.AddBody(Body(1, 1, new Vector3(.2f), new Vector3(1.1f, 0, 0)));
            for (var i = 1; i <= 240; i++)
            {
                world.SetTransform(leg, new NativeBodyTransform(new Vector3(i / 240f, 0, 0), Quaternion.Identity));
                world.Step(1f / 240);
            }
            Require(world.GetTransform(skirt).Position.X > 1.65f, "Animated capsule failed to push the box.");
        });
        Test("6DoF-anchor-and-movement", () =>
        {
            using var world = new SkirtBullet(new Vector3(0, -9.81f, 0));
            var anchor = Body(1, 0, new Vector3(.2f), Vector3.Zero); anchor.CollisionMask = 0;
            var pendant = Body(0, 2, new Vector3(.2f), new Vector3(0, -2, 0)); pendant.CollisionMask = 0;
            var a = world.AddBody(anchor); var b = world.AddBody(pendant);
            world.AddJoint(new NativeJointDescription { BodyA = a, BodyB = b, Rotation = Quaternion.Identity });
            Advance(world, 120);
            Require(Vector3.Distance(world.GetTransform(b).Position, new Vector3(0, -2, 0)) < .02f, "Locked joint did not retain the initial frame.");
            for (var i = 1; i <= 120; i++)
            {
                world.SetTransform(a, new NativeBodyTransform(new Vector3(i / 120f, 0, 0), Quaternion.Identity));
                world.Step(1f / 240);
            }
            Advance(world, 120);
            Require(Vector3.Distance(world.GetTransform(b).Position, new Vector3(1, -2, 0)) < .035f, "Joint did not follow the kinematic anchor.");
        });
        Test("Spring2-raw-PMX-multiaxis-order", () =>
        {
            using var world = new SkirtBullet(Vector3.Zero);
            var anchor = Body(0, 0, new Vector3(.1f), Vector3.Zero); anchor.CollisionMask = 0;
            var dynamic = Body(0, 2, new Vector3(.1f), Vector3.Zero); dynamic.CollisionMask = 0;
            var a = world.AddBody(anchor); var b = world.AddBody(dynamic);
            // Fixed, unequal angles distinguish raw XZY from XYZ even though
            // single-axis joints and zero-angle anchors cannot distinguish it.
            var angles = new Vector3(.25f, .5f, -.2f);
            world.AddJoint(new NativeJointDescription
            {
                BodyA = a, BodyB = b, Rotation = Quaternion.Identity,
                AngularLower = angles, AngularUpper = angles,
            });
            Advance(world, 480);
            // Bullet's constraint angles decompose the inverse relative matrix.
            var expected = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.UnitY, -angles.Y) *
                Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -angles.Z) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, -angles.X));
            var actual = world.GetTransform(b).Rotation;
            var angle = 2 * MathF.Acos(Math.Clamp(MathF.Abs(Quaternion.Dot(expected, actual)), 0, 1)) * 180 / MathF.PI;
            Require(angle < .2f, $"Raw PMX multiaxis lock differs by {angle} degrees.");
        });
        Test("repeated-world-determinism", () =>
        {
            var a = Sequence(); var b = Sequence();
            Require(a.SequenceEqual(b), "Fresh identical worlds produced different float results.");
        });
        Test("rotated-body-reset-matches-construction", () =>
        {
            // Resetting an anisotropic box before the first step must refresh
            // world inertia exactly as constructing it in that orientation does.
            var constructed = RotatedBodyFirstStep(false);
            var reset = RotatedBodyFirstStep(true);
            Require(constructed.Equals(reset), "Reset body retained inertia from its old orientation.");
        });
        Test("noncollision-helper-is-pair-specific-and-force-free", () =>
        {
            using var world = new SkirtBullet(new Vector3(0, -9.81f, 0));
            var floor = world.AddBody(Body(1, 0, new Vector3(10, .5f, 10), Vector3.Zero));
            var ignored = world.AddBody(Body(0, 1, new Vector3(.25f), new Vector3(0, 4, 0)));
            var allowed = world.AddBody(Body(0, 1, new Vector3(.25f), new Vector3(2, 4, 0)));
            world.DisableCollisionPair(floor, ignored);
            world.DisableCollisionPair(ignored, floor); // Duplicate inverse pair is harmless.
            using var free = new SkirtBullet(new Vector3(0, -9.81f, 0));
            var falling = free.AddBody(Body(0, 1, new Vector3(.25f), new Vector3(0, 4, 0)));
            Advance(world, 720); Advance(free, 720);
            Require(world.GetTransform(ignored).Position.Y < -5, "Ignored floor still generated contact.");
            Require(MathF.Abs(world.GetTransform(allowed).Position.Y - .75f) < .035f, "Helper suppressed unrelated floor contact.");
            Require(world.GetTransform(ignored).Equals(free.GetTransform(falling)), "Free-axis Generic helper applied a force.");
            world.DisableCollisionPair(floor, allowed);
            Advance(world, 240);
            Require(world.GetTransform(allowed).Position.Y < -.5f, "Disabling an existing pair retained cached contact forces.");
            Throws<InvalidOperationException>(() => world.DisableCollisionPair(floor, floor));
            Throws<InvalidOperationException>(() => world.DisableCollisionPair(floor, 100));
        });
        Test("real-joint-collision-option", () =>
        {
            float Height(bool ignored)
            {
                using var world = new SkirtBullet(new Vector3(0, -9.81f, 0));
                var floor = world.AddBody(Body(1, 0, new Vector3(10, .5f, 10), Vector3.Zero));
                var falling = world.AddBody(Body(0, 1, new Vector3(.25f), new Vector3(0, 4, 0)));
                world.AddJoint(new NativeJointDescription
                {
                    BodyA = floor, BodyB = falling, Rotation = Quaternion.Identity,
                    LinearLower = Vector3.One, LinearUpper = -Vector3.One,
                    AngularLower = Vector3.One, AngularUpper = -Vector3.One,
                }, ignored);
                Advance(world, 720);
                return world.GetTransform(falling).Position.Y;
            }
            Require(Height(true) < -5, "Joint collision option failed to suppress contact.");
            Require(MathF.Abs(Height(false) - .75f) < .035f, "Ordinary joint suppressed contact.");
        });
        Test("invalid-arguments-and-disposal", () =>
        {
            using var world = new SkirtBullet(Vector3.Zero);
            var invalid = Body(0, 1, new Vector3(.2f), Vector3.Zero); invalid.Position.X = float.NaN;
            Throws<InvalidOperationException>(() => world.AddBody(invalid));
            Throws<InvalidOperationException>(() => world.GetTransform(0));
            Throws<InvalidOperationException>(() => world.Step(0));
            Throws<InvalidOperationException>(() => new SkirtBullet(new Vector3(float.NaN, 0, 0)));
            var valid = world.AddBody(Body(0, 1, new Vector3(.2f), Vector3.Zero));
            Require(valid == 0, "Rejected input changed the body index.");
            world.Dispose(); world.Dispose();
            Throws<ObjectDisposedException>(() => world.Step(1f / 240));
        });
        if (referencePmxPath != null)
            Test("actual-PMX-30-bodies-39-joints", () => RunReference(referencePmxPath));
        Console.WriteLine($"SkirtBullet: {passed} passed, {failed} failed; {SkirtBullet.EngineVersion}");
        return failed;
    }

    private static NativeBodyDescription Body(int shape, int mode, Vector3 size, Vector3 position) => new()
    {
        Shape = shape, Mode = mode, Size = size, Position = position, Rotation = Quaternion.Identity,
        CollisionGroup = 0, CollisionMask = ushort.MaxValue, Mass = 1, Friction = .5f, CollisionMargin = .04f,
    };

    private static float FloorResult(int shape, Vector3 size, bool collision)
    {
        using var world = new SkirtBullet(new Vector3(0, -9.81f, 0));
        var floor = Body(1, 0, new Vector3(10, .5f, 10), Vector3.Zero); floor.CollisionMask = 2;
        world.AddBody(floor);
        var body = Body(shape, 1, size, new Vector3(0, 4, 0)); body.CollisionGroup = 1; body.CollisionMask = collision ? 1u : 0u;
        var id = world.AddBody(body); Advance(world, 720);
        return world.GetTransform(id).Position.Y;
    }

    private static NativeBodyTransform[] Sequence()
    {
        using var world = new SkirtBullet(new Vector3(0, -9.81f, 0));
        world.AddBody(Body(1, 0, new Vector3(4, .5f, 4), Vector3.Zero));
        var body = world.AddBody(Body(1, 1, new Vector3(.2f, .4f, .3f), new Vector3(.2f, 3, 0)));
        var result = new NativeBodyTransform[300];
        for (var i = 0; i < result.Length; i++) { world.Step(1f / 240); result[i] = world.GetTransform(body); }
        return result;
    }

    private static NativeBodyTransform RotatedBodyFirstStep(bool reset)
    {
        using var world = new SkirtBullet(new Vector3(0, -9.81f, 0), 40);
        var rotation = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.UnitY, .5f) *
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .7f));
        var anchor = Body(0, 0, new Vector3(.1f), Vector3.Zero); anchor.CollisionMask = 0;
        var dynamic = Body(1, 1, new Vector3(.7f, .1f, .2f), new Vector3(.7f, 0, 0)); dynamic.CollisionMask = 0;
        dynamic.Rotation = reset ? Quaternion.Identity : rotation;
        var a = world.AddBody(anchor); var b = world.AddBody(dynamic);
        if (reset) world.SetTransform(b, new NativeBodyTransform(dynamic.Position, rotation), true);
        world.AddJoint(new NativeJointDescription
        {
            BodyA = a, BodyB = b, Rotation = Quaternion.Identity,
            AngularLower = Vector3.One, AngularUpper = -Vector3.One,
        });
        world.Step(1f / 30);
        return world.GetTransform(b);
    }

    private static void RunReference(string path)
    {
        var profile = PmxPhysicsProfileReader.Parse(path);
        Require(profile.RigidBodies.Length == 30 && profile.Joints.Length == 39 && !profile.HasUnsupportedPhysics, "Unexpected reference PMX physics.");
        using var world = new SkirtBullet(new Vector3(0, -98.1f, 0), 40);
        foreach (var body in profile.RigidBodies)
            world.AddBody(new NativeBodyDescription
            {
                Shape = (int)body.Shape, Mode = (int)body.Mode, CollisionGroup = body.CollisionGroup, CollisionMask = body.CollisionMask,
                Size = body.Size, Position = body.Position, Rotation = Euler(body.RotationEuler), Mass = body.Mass,
                LinearDamping = body.LinearDamping, AngularDamping = body.AngularDamping, Restitution = body.Restitution, Friction = body.Friction,
                CollisionMargin = .04f,
            });
        foreach (var joint in profile.Joints)
            world.AddJoint(new NativeJointDescription
            {
                BodyA = joint.RigidBodyA, BodyB = joint.RigidBodyB, Position = joint.Position, Rotation = Euler(joint.RotationEuler),
                LinearLower = joint.TranslationMinimum, LinearUpper = joint.TranslationMaximum,
                AngularLower = joint.RotationMinimum, AngularUpper = joint.RotationMaximum,
                LinearSpring = joint.TranslationSpring, AngularSpring = joint.RotationSpring,
            });
        for (var frame = 0; frame < 240; frame++)
        {
            for (var substep = 0; substep < 8; substep++) world.Step(1f / 240);
            foreach (var body in profile.RigidBodies)
            {
                var pose = world.GetTransform(body.Index);
                Require(float.IsFinite(pose.Position.LengthSquared()) && float.IsFinite(pose.Rotation.LengthSquared()), "Nonfinite PMX pose.");
                Require(MathF.Abs(pose.Rotation.LengthSquared() - 1) < 1e-3f, "PMX quaternion is not normalized.");
            }
        }
    }

    private static Quaternion Euler(Vector3 value) => Quaternion.Normalize(
        Quaternion.CreateFromAxisAngle(Vector3.UnitY, value.Y) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, value.X) * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, value.Z));
    private static void Advance(SkirtBullet world, int steps) { for (var i = 0; i < steps; i++) world.Step(1f / 240); }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }
}
