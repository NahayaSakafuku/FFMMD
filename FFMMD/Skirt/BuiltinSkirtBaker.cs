using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using FFMMD.Retarget;
using FFMMD.Vmd;

namespace FFMMD.Skirt;

/// <summary>Standalone fixed-step bake. Never invoked from a game pose hook.</summary>
public sealed class BuiltinSkirtBaker
{
    public const string Engine = "FFMMD Builtin Bullet";
    public SkirtBakeCache.Document Bake(VmdAnimation animation, SkirtPhysicsReference reference,
        string motionSha256, SkirtBakeRecipe recipe, Action<SkirtBakeProgress> progress,
        CancellationToken cancellationToken, string? libraryPath = null)
    {
        ArgumentNullException.ThrowIfNull(animation);
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(recipe);
        if (recipe.Version != 3 || recipe.Substeps is < 1 or > 1000 || recipe.Iterations is < 1 or > 1000 ||
            recipe.WarmupFrames is < 0 or > 300 || !float.IsFinite(recipe.Scale) || recipe.Scale is < .001f or > 100 ||
            !float.IsFinite(recipe.CollisionMargin) || recipe.CollisionMargin is < 0 or > 1 ||
            !float.IsFinite(recipe.NonCollisionDistanceScale) || recipe.NonCollisionDistanceScale is < 0 or > 100 ||
            recipe.SmoothingWindowFrames is < 0 or > 8 || !float.IsFinite(recipe.SmoothingMaximumCorrectionDegrees) ||
            recipe.SmoothingMaximumCorrectionDegrees is < 0 or > 15 || !float.IsFinite(recipe.SmoothingStrength) || recipe.SmoothingStrength is < 0 or > 1)
            throw new InvalidDataException("Unsupported builtin physics recipe.");
        reference.Validate();
        var rig = reference.Rig; var physics = reference.Physics;
        var count = checked((int)animation.MaxFrame + 1);
        if (count > SkirtBakeCache.MaxFrames || (long)count * 18 > SkirtBakeCache.MaxRotationSamples)
            throw new InvalidDataException("Motion exceeds the supported physics cache range.");
        var cal = new Calibration { LegIkMode = 2 };
        var solver = new SourceRigSolver(rig, animation);
        var bodies = physics.RigidBodies;
        var bindRotations = bodies.Select(b => PmxRotation(b.RotationEuler)).ToArray();
        var mapping = SkirtBakeCache.RequiredTargetNames.Select(TargetToSource).Select(rig.Find).ToArray();
        var bodyForBone = Enumerable.Repeat(-1, rig.Bones.Length).ToArray();
        foreach (var body in bodies)
            if (body.BoneIndex >= 0 && body.Mode != PmxRigidBodyMode.Bone) bodyForBone[body.BoneIndex] = body.Index;
        if (mapping.Any(b => b < 0 || bodyForBone[b] < 0))
            throw new InvalidDataException("Each skirt bone needs a dynamic rigid body.");
        if (mapping.Any(b => bodies[bodyForBone[b]].Shape != PmxRigidBodyShape.Box))
            throw new InvalidDataException("Skirt physics requires box rigid bodies for its collision guard.");
        var ignoredPairs = BuildCollisionFilter(physics, recipe.NonCollisionDistanceScale);
        var jointPairs = physics.Joints.Where(j => j.RigidBodyA >= 0 && j.RigidBodyB >= 0)
            .Select(j => Pair(j.RigidBodyA, j.RigidBodyB)).ToHashSet();
        using var world = new SkirtBullet(new(0, -9.81f, 0), recipe.Iterations, libraryPath);
        foreach (var body in bodies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var description = new NativeBodyDescription
            {
                Shape = (int)body.Shape, Mode = (int)body.Mode,
                // Bullet's default groups exclude kinematic/kinematic contacts.
                // Rest-geometry pair exclusions are installed separately below.
                CollisionGroup = body.Mode == PmxRigidBodyMode.Bone ? 1u : 0u,
                CollisionMask = body.Mode == PmxRigidBodyMode.Bone ? 0xfffdu : 0xffffu,
                Size = body.Size * recipe.Scale, Position = body.Position * recipe.Scale,
                Rotation = bindRotations[body.Index], Mass = body.Mass,
                LinearDamping = body.LinearDamping, AngularDamping = body.AngularDamping,
                Restitution = body.Restitution, Friction = body.Friction, CollisionMargin = recipe.CollisionMargin,
            };
            if (world.AddBody(description) != body.Index) throw new InvalidDataException("Rigid body order changed.");
        }
        foreach (var joint in physics.Joints)
        {
            if (joint.RigidBodyA < 0 || joint.RigidBodyB < 0) continue;
            world.AddJoint(new NativeJointDescription
            {
                BodyA = joint.RigidBodyA, BodyB = joint.RigidBodyB,
                Position = joint.Position * recipe.Scale, Rotation = PmxRotation(joint.RotationEuler),
                LinearLower = joint.TranslationMinimum * recipe.Scale, LinearUpper = joint.TranslationMaximum * recipe.Scale,
                AngularLower = joint.RotationMinimum, AngularUpper = joint.RotationMaximum,
                LinearSpring = joint.TranslationSpring, AngularSpring = joint.RotationSpring,
            }, ignoredPairs.Contains(Pair(joint.RigidBodyA, joint.RigidBodyB)));
        }
        var helperCount = 0;
        foreach (var pair in ignoredPairs.OrderBy(p => p.A).ThenBy(p => p.B))
            if (!jointPairs.Contains(pair)) { world.DisableCollisionPair(pair.A, pair.B); helperCount++; }
        if (!solver.Evaluate(0, cal)) throw new InvalidDataException("Invalid first motion pose.");
        var driver = new KinematicColliderDriver(reference);
        var previous = driver.GetFrame(solver.Pose, recipe.Scale);
        var colliderIndices = bodies.Where(b => b.Mode == PmxRigidBodyMode.Bone && b.Shape != PmxRigidBodyShape.Box)
            .Select(b => b.Index).ToArray();
        if ((long)count * colliderIndices.Length > SkirtBakeCache.MaxRotationSamples)
            throw new InvalidDataException("Reference collider tracks exceed the supported preprocessing range.");
        var colliderFrames = new NativeBodyTransform[count][];
        for (var b = 0; b < bodies.Length; b++)
        {
            world.SetTransform(b, previous[b], true);
        }
        var dt = 1f / (30 * recipe.Substeps);
        progress(new("Warmup", 0, "Settling the initial skirt pose."));
        for (var warm = 0; warm < recipe.WarmupFrames; warm++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var sub = 0; sub < recipe.Substeps; sub++) world.Step(dt);
            if (warm % 10 == 0) progress(new("Warmup", .04f * (warm + 1) / Math.Max(1, recipe.WarmupFrames)));
        }
        var raw = new Quaternion[count][];
        var model = new Quaternion[rig.Bones.Length];
        for (var frame = 0; frame < count; frame++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!solver.Evaluate(frame, cal)) throw new InvalidDataException($"Invalid source pose at frame {frame}.");
            var current = driver.GetFrame(solver.Pose, recipe.Scale);
            colliderFrames[frame] = colliderIndices.Select(i => current[i]).ToArray();
            if (frame > 0)
            {
                for (var sub = 1; sub <= recipe.Substeps; sub++)
                {
                    if ((sub & 15) == 0) cancellationToken.ThrowIfCancellationRequested();
                    var fraction = (float)sub / recipe.Substeps;
                    for (var b = 0; b < bodies.Length; b++)
                        if (bodies[b].Mode == PmxRigidBodyMode.Bone)
                            world.SetTransform(b, new NativeBodyTransform(Vector3.Lerp(previous[b].Position, current[b].Position, fraction),
                                Quaternion.Slerp(previous[b].Rotation, current[b].Rotation, fraction)));
                    world.Step(dt);
                }
            }
            Array.Copy(solver.Pose.Rotations, model, model.Length);
            foreach (var b in mapping)
            {
                var body = bodyForBone[b]; var transform = world.GetTransform(body);
                model[b] = Quaternion.Normalize(transform.Rotation * Quaternion.Conjugate(bindRotations[body]));
                if (!Finite(model[b])) throw new InvalidDataException($"Non-finite physics at frame {frame}.");
            }
            raw[frame] = new Quaternion[18];
            for (var i = 0; i < mapping.Length; i++)
            {
                var b = mapping[i]; var parent = rig.Bones[b].Parent;
                raw[frame][i] = Quaternion.Normalize((parent < 0 ? Quaternion.Identity : Quaternion.Conjugate(model[parent])) * model[b]);
            }
            previous = current;
            if (frame % 30 == 0 || frame == count - 1) progress(new("Baking", .04f + .87f * (frame + 1) / count,
                $"Skirt physics: {frame + 1} / {count} frames."));
        }

        var guard = new CollisionGuard(solver, cal, reference, recipe.Scale, mapping, bodyForBone, bindRotations, raw,
            colliderIndices, colliderFrames, ignoredPairs, cancellationToken);
        progress(new("Smoothing", .92f, "Smoothing skirt rotations while preserving leg clearance."));
        var smoothed = SkirtTrackSmoothing.Smooth(raw, guard.Accept, new()
        {
            Window = recipe.SmoothingWindowFrames, Strength = recipe.SmoothingStrength,
            MaximumCorrectionDegrees = recipe.SmoothingMaximumCorrectionDegrees,
        });
        cancellationToken.ThrowIfCancellationRequested();
        var document = new SkirtBakeCache.Document
        {
            MotionSha256 = motionSha256, ReferencePmxSha256 = physics.Fingerprint,
            FrameCount = count, SourceBasis = SourceBasis(rig),
            Solver = new()
            {
                Engine = Engine, EngineVersion = SkirtBullet.EngineVersion, RecipeVersion = recipe.Version,
                Substeps = recipe.Substeps, Iterations = recipe.Iterations, WarmupFrames = recipe.WarmupFrames,
                Scale = recipe.Scale, CollisionMargin = recipe.CollisionMargin, NonCollisionDistanceScale = recipe.NonCollisionDistanceScale, PointCacheBaked = true,
                RigidBodyCount = bodies.Length, JointCount = physics.Joints.Length, MotionMaxFrame = count - 1,
                NonCollisionConstraintCount = helperCount,
                KinematicDrivingAlgorithm = KinematicColliderDriver.Algorithm,
                Smoothing = new()
                {
                    Algorithm = "BoundedBilateralQuaternion", WindowFrames = recipe.SmoothingWindowFrames,
                    MaximumCorrectionDegrees = recipe.SmoothingMaximumCorrectionDegrees, Strength = recipe.SmoothingStrength,
                    CollisionGuard = "SignedCapsuleBoxFK",
                    Statistics = new() { ["Metrics"] = JsonSerializer.SerializeToElement(smoothed.Metrics) },
                },
            },
        };
        for (var i = 0; i < mapping.Length; i++)
        {
            var bone = rig.Bones[mapping[i]];
            document.Bones.Add(new()
            {
                SourceName = bone.Name, TargetName = SkirtBakeCache.RequiredTargetNames[i],
                ParentSourceName = rig.Bones[bone.Parent].Name,
                RelativeRotations = smoothed.Frames.Select(f => new[] { f[i].X, f[i].Y, f[i].Z, f[i].W }).ToArray(),
            });
        }
        progress(new("Saving", .98f, "Saving the reusable skirt physics cache."));
        return document;
    }

    public static Quaternion PmxRotation(Vector3 euler) => Quaternion.Normalize(
        Quaternion.CreateFromAxisAngle(Vector3.UnitY, euler.Y) *
        Quaternion.CreateFromAxisAngle(Vector3.UnitX, euler.X) *
        Quaternion.CreateFromAxisAngle(Vector3.UnitZ, euler.Z));

    private static bool Finite(Quaternion q) => float.IsFinite(q.X) && float.IsFinite(q.Y) && float.IsFinite(q.Z) && float.IsFinite(q.W);
    private static (int A, int B) Pair(int a, int b) => a < b ? (a, b) : (b, a);
    public static HashSet<(int A, int B)> BuildCollisionFilter(PmxPhysicsProfile physics, float distanceScale)
    {
        var jointPairs = physics.Joints.Where(j => j.RigidBodyA >= 0 && j.RigidBodyB >= 0)
            .Select(j => Pair(j.RigidBodyA, j.RigidBodyB)).ToHashSet();
        var ignored = new HashSet<(int, int)>(); var bodies = physics.RigidBodies;
        for (var a = 0; a < bodies.Length; a++)
            for (var b = a + 1; b < bodies.Length; b++)
            {
                var first = bodies[a]; var second = bodies[b];
                var maskIgnores = (first.CollisionMask & 1 << second.CollisionGroup) == 0 ||
                    (second.CollisionMask & 1 << first.CollisionGroup) == 0;
                if (!maskIgnores) continue;
                if (jointPairs.Contains((a, b)) || Vector3.Distance(first.Position, second.Position) <
                    distanceScale * (RigidRange(first) + RigidRange(second)) * .5f) ignored.Add((a, b));
            }
        return ignored;
    }
    private static float RigidRange(PmxRigidBody body) => body.Shape switch
    {
        PmxRigidBodyShape.Box => 2 * body.Size.Length(),
        // The reference's sphere proxy has five latitude segments. Its two
        // horizontal bounds are 2r*cos(pi/10); its vertical bound is 2r.
        PmxRigidBodyShape.Sphere => 2 * body.Size.X * MathF.Sqrt(1 + 2 * MathF.Pow(MathF.Cos(MathF.PI / 10), 2)),
        PmxRigidBodyShape.Capsule => MathF.Sqrt(8 * body.Size.X * body.Size.X + MathF.Pow(body.Size.Y + 2 * body.Size.X, 2)),
        _ => throw new InvalidDataException("Unsupported collision shape."),
    };
    private static string TargetToSource(string target)
    {
        var p = target.Split('_');
        var column = (p[2], p[4]) switch { ("f", "r") => 0, ("f", "l") => 1, ("s", "l") => 2,
            ("b", "l") => 3, ("b", "r") => 4, ("s", "r") => 5, _ => throw new InvalidDataException("Unknown skirt target.") };
        return $"Skirt_{p[3][0] - 'a'}_{column}";
    }
    private static SkirtBakeCache.SourceBasisDocument SourceBasis(SourceRigDefinition rig)
    {
        Vector3 Position(string name) { var i = rig.Find(name); if (i < 0) throw new InvalidDataException($"Reference basis needs {name}."); return rig.Bones[i].RestPosition; }
        var up = Vector3.Normalize(Position("頭") - Position("下半身"));
        var left = Position("左足") - Position("右足"); left = Vector3.Normalize(left - up * Vector3.Dot(left, up));
        var back = Position("左足首") + Position("右足首") - Position("左足先EX") - Position("右足先EX");
        back = Vector3.Normalize(back - up * Vector3.Dot(back, up) - left * Vector3.Dot(back, left));
        return new() { Left = [left.X, left.Y, left.Z], Up = [up.X, up.Y, up.Z], Back = [back.X, back.Y, back.Z] };
    }

    private sealed class CollisionGuard
    {
        private readonly SourceRigSolver _solver;
        private readonly Calibration _cal;
        private readonly SkirtPhysicsReference _reference;
        private readonly float _scale;
        private readonly int[] _mapping, _bodyForBone, _mappedIndex;
        private readonly Quaternion[] _bind, _model;
        private readonly Vector3[] _positions;
        private readonly Quaternion[][] _raw;
        private readonly NativeBodyTransform[][] _colliderFrames;
        private readonly int[] _colliderFrameIndex;
        private readonly CancellationToken _token;
        private readonly (int Skirt, int Collider)[] _pairs;
        private readonly double[] _baseline, _candidate;
        private int _frame = -1;
        public CollisionGuard(SourceRigSolver solver, Calibration cal, SkirtPhysicsReference reference, float scale,
            int[] mapping, int[] bodyForBone, Quaternion[] bind, Quaternion[][] raw, int[] colliderIndices, NativeBodyTransform[][] colliderFrames,
            HashSet<(int A, int B)> ignoredPairs, CancellationToken token)
        {
            _solver = solver; _cal = cal; _reference = reference; _scale = scale;
            _mapping = mapping; _bodyForBone = bodyForBone; _bind = bind; _raw = raw; _colliderFrames = colliderFrames; _token = token;
            _model = new Quaternion[reference.Rig.Bones.Length]; _positions = new Vector3[_model.Length];
            _mappedIndex = Enumerable.Repeat(-1, _model.Length).ToArray();
            for (var i = 0; i < mapping.Length; i++) _mappedIndex[mapping[i]] = i;
            var bodies = reference.Physics.RigidBodies;
            _colliderFrameIndex = Enumerable.Repeat(-1, bodies.Length).ToArray();
            for (var i = 0; i < colliderIndices.Length; i++) _colliderFrameIndex[colliderIndices[i]] = i;
            _pairs = (from i in Enumerable.Range(0, 18) let skirt = bodies[bodyForBone[mapping[i]]]
                from collider in bodies where collider.Mode == PmxRigidBodyMode.Bone && collider.Shape != PmxRigidBodyShape.Box &&
                !ignoredPairs.Contains(Pair(skirt.Index, collider.Index))
                select (i, collider.Index)).ToArray();
            _baseline = new double[_pairs.Length]; _candidate = new double[_pairs.Length];
        }
        public bool Accept(int frame, Quaternion[] rotations)
        {
            _token.ThrowIfCancellationRequested();
            if (_frame != frame)
            {
                if (!_solver.Evaluate(frame, _cal)) return false;
                _frame = frame; Distances(_raw[frame], _baseline);
            }
            Distances(rotations, _candidate);
            return SkirtProxyCollisionGuard.DoesNotWorsen(_baseline, _candidate);
        }
        private void Distances(Quaternion[] rotations, double[] distances)
        {
            var rig = _reference.Rig; var pose = _solver.Pose;
            foreach (var b in rig.Order)
            {
                var parent = rig.Bones[b].Parent; var i = _mappedIndex[b];
                var local = i < 0 ? pose.LocalRotations[b] : rotations[i];
                _model[b] = parent < 0 ? local : Quaternion.Normalize(_model[parent] * local);
                _positions[b] = parent < 0 ? pose.LocalPositions[b] : _positions[parent] + Vector3.Transform(pose.LocalPositions[b], _model[parent]);
            }
            var bodies = _reference.Physics.RigidBodies;
            for (var pair = 0; pair < _pairs.Length; pair++)
            {
                var (skirtIndex, colliderIndex) = _pairs[pair];
                var bodyIndex = _bodyForBone[_mapping[skirtIndex]]; var box = bodies[bodyIndex];
                var center = (_positions[box.BoneIndex] + Vector3.Transform(box.Position - rig.Bones[box.BoneIndex].RestPosition, _model[box.BoneIndex])) * _scale;
                var rotation = Quaternion.Normalize(_model[box.BoneIndex] * _bind[bodyIndex]);
                var collider = bodies[colliderIndex]; var transform = _colliderFrames[_frame][_colliderFrameIndex[colliderIndex]];
                var half = collider.Shape == PmxRigidBodyShape.Sphere ? Vector3.Zero : Vector3.Transform(Vector3.UnitY * collider.Size.Y * _scale * .5f, transform.Rotation);
                distances[pair] = SkirtProxyCollisionGuard.CapsuleBoxPenetration(transform.Position - half, transform.Position + half,
                    collider.Size.X * _scale, center, rotation, box.Size * _scale);
            }
        }
    }
}
