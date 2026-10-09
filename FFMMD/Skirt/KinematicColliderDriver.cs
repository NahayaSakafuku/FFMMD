using System.Numerics;
using FFMMD.Retarget;

namespace FFMMD.Skirt;

/// <summary>
/// Drives the PMX bodies without injecting source IK axial flips into jointless
/// capsule contacts. The source pose itself, joint frames and dynamic bodies are
/// never changed. Instances consume frames in bake order; reset before replay.
/// </summary>
public sealed class KinematicColliderDriver
{
    public const string Algorithm = "JointlessCapsuleMinimalSwing";
    private readonly PmxRigidBody[] _bodies;
    private readonly Vector3[] _restPositions, _axes;
    private readonly Quaternion[] _bindRotations;
    private readonly bool[] _protectedBodies, _transportBones;
    private Quaternion[] _previousBones, _sphereRotations;
    private bool _initialized;

    public KinematicColliderDriver(SkirtPhysicsReference reference)
        : this((reference ?? throw new ArgumentNullException(nameof(reference))).Rig, reference.Physics) { }

    public KinematicColliderDriver(SourceRigDefinition rig, PmxPhysicsProfile physics)
    {
        ArgumentNullException.ThrowIfNull(rig);
        ArgumentNullException.ThrowIfNull(physics);
        _bodies = physics.RigidBodies.ToArray();
        _restPositions = rig.Bones.Select(b => b.RestPosition).ToArray();
        _axes = new Vector3[rig.Bones.Length];
        _bindRotations = new Quaternion[_bodies.Length];
        _protectedBodies = new bool[_bodies.Length];
        _transportBones = new bool[rig.Bones.Length];
        _previousBones = new Quaternion[rig.Bones.Length];
        _sphereRotations = new Quaternion[_bodies.Length];
        if (_restPositions.Any(p => !Finite(p)))
            throw new ArgumentException("Non-finite source rest position.", nameof(rig));
        for (var i = 0; i < _bodies.Length; i++)
        {
            var body = _bodies[i];
            if (body.Index != i || body.BoneIndex < -1 || body.BoneIndex >= rig.Bones.Length ||
                !Finite(body.Position) || !Finite(body.RotationEuler))
                throw new ArgumentException("Invalid rigid body transform or index.", nameof(physics));
            _bindRotations[i] = PmxRotation(body.RotationEuler);
        }
        var protectedBones = new bool[rig.Bones.Length];
        // This list contains real PMX joints only. Native non-collision helpers
        // are created later by the baker and do not restrict transport.
        foreach (var joint in physics.Joints)
        {
            Protect(joint.RigidBodyA);
            Protect(joint.RigidBodyB);
        }
        void Protect(int body)
        {
            if (body < -1 || body >= _bodies.Length)
                throw new ArgumentException("Invalid joint body index.", nameof(physics));
            if (body < 0) return;
            _protectedBodies[body] = true;
            if (_bodies[body].BoneIndex >= 0) protectedBones[_bodies[body].BoneIndex] = true;
        }
        for (var i = 0; i < _bodies.Length; i++)
            if (_bodies[i].BoneIndex >= 0 && protectedBones[_bodies[i].BoneIndex])
                _protectedBodies[i] = true;

        var incompatibleAxes = new bool[rig.Bones.Length];
        for (var i = 0; i < _bodies.Length; i++)
        {
            var body = _bodies[i]; var bone = body.BoneIndex;
            if (_protectedBodies[i] || bone < 0 || body.Mode != PmxRigidBodyMode.Bone || body.Shape != PmxRigidBodyShape.Capsule)
                continue;
            var axis = Vector3.Normalize(Vector3.Transform(Vector3.UnitY, _bindRotations[i]));
            if (!_transportBones[bone]) { _axes[bone] = axis; _transportBones[bone] = true; }
            // A single transported rotation can preserve every collinear axis.
            // Keep raw rotation if a model attaches incompatible capsule axes.
            // Dot products round to one for small but meaningful axis offsets.
            // Cross squared detects either signed collinear direction while
            // permitting only float noise, rather than a visible angle budget.
            else if (Vector3.Cross(_axes[bone], axis).LengthSquared() > 1e-12f) incompatibleAxes[bone] = true;
        }
        for (var i = 0; i < _transportBones.Length; i++)
            if (incompatibleAxes[i]) _transportBones[i] = false;
    }

    public void Reset()
    {
        _initialized = false;
        Array.Clear(_previousBones);
        Array.Clear(_sphereRotations);
    }

    public NativeBodyTransform[] GetFrame(SolvedSourcePose pose, float scale)
    {
        ArgumentNullException.ThrowIfNull(pose);
        if (!float.IsFinite(scale) || scale <= 0) throw new ArgumentOutOfRangeException(nameof(scale));
        if (pose.Positions.Length != _restPositions.Length || pose.Rotations.Length != _restPositions.Length)
            throw new ArgumentException("Source pose bone count differs from the collider rig.", nameof(pose));
        // Validate and build into new arrays before committing temporal state.
        // A rejected frame must not change the next valid frame's transport.
        var rawBones = new Quaternion[_restPositions.Length];
        for (var bone = 0; bone < rawBones.Length; bone++)
        {
            if (!Finite(pose.Positions[bone]) || !Valid(pose.Rotations[bone]))
                throw new ArgumentException("Non-finite or degenerate source pose.", nameof(pose));
            rawBones[bone] = Quaternion.Normalize(pose.Rotations[bone]);
        }
        var nextBones = (Quaternion[])rawBones.Clone();
        for (var bone = 0; bone < rawBones.Length; bone++)
        {
            if (!_transportBones[bone] || !_initialized) continue;
            var previous = _previousBones[bone];
            var from = Vector3.Normalize(Vector3.Transform(_axes[bone], previous));
            var to = Vector3.Normalize(Vector3.Transform(_axes[bone], rawBones[bone]));
            nextBones[bone] = Quaternion.Normalize(MinimalSwing(from, to, previous) * previous);
        }
        var nextSpheres = (Quaternion[])_sphereRotations.Clone();
        var result = new NativeBodyTransform[_bodies.Length];
        for (var i = 0; i < _bodies.Length; i++)
        {
            var body = _bodies[i]; var bone = body.BoneIndex;
            var jointlessKinematic = !_protectedBodies[i] && body.Mode == PmxRigidBodyMode.Bone;
            var useTransport = jointlessKinematic && bone >= 0 && _transportBones[bone] &&
                (body.Shape == PmxRigidBodyShape.Capsule || body.Shape == PmxRigidBodyShape.Sphere);
            var boneRotation = bone < 0 ? Quaternion.Identity : useTransport ? nextBones[bone] : rawBones[bone];
            // Capsule orientation can omit axial twist without changing its
            // geometry. Its center must still use the original source rotation,
            // especially when the PMX center is offset from the bone axis.
            // Companion spheres deliberately transport their offset instead.
            var centerRotation = bone >= 0 && body.Shape == PmxRigidBodyShape.Capsule ? rawBones[bone] : boneRotation;
            var position = bone < 0 ? body.Position * scale :
                (pose.Positions[bone] + Vector3.Transform(body.Position - _restPositions[bone], centerRotation)) * scale;
            var rotation = Quaternion.Normalize(boneRotation * _bindRotations[i]);
            if (jointlessKinematic && body.Shape == PmxRigidBodyShape.Sphere)
            {
                if (!_initialized) nextSpheres[i] = rotation;
                rotation = nextSpheres[i];
            }
            if (!Finite(position) || !Valid(rotation))
                throw new ArgumentException("Collider transform exceeds the finite range.", nameof(pose));
            result[i] = new(position, rotation);
        }
        _previousBones = nextBones;
        _sphereRotations = nextSpheres;
        _initialized = true;
        return result;
    }

    private static Quaternion MinimalSwing(Vector3 from, Vector3 to, Quaternion previous)
    {
        // Double intermediates matter near 180 degrees: cancellation in a
        // float cross product can produce a non-perpendicular rotation axis.
        var x = (double)from.Y * to.Z - (double)from.Z * to.Y;
        var y = (double)from.Z * to.X - (double)from.X * to.Z;
        var z = (double)from.X * to.Y - (double)from.Y * to.X;
        var dot = (double)from.X * to.X + (double)from.Y * to.Y + (double)from.Z * to.Z;
        var length = Math.Sqrt(x * x + y * y + z * z);
        if (length > 1e-10)
        {
            var halfAngle = .5 * Math.Atan2(length, dot);
            var factor = Math.Sin(halfAngle) / length;
            return Quaternion.Normalize(new((float)(x * factor), (float)(y * factor), (float)(z * factor), (float)Math.Cos(halfAngle)));
        }
        if (dot >= 0) return Quaternion.Identity;
        // A directed 180-degree axis reversal must be preserved. The minimal
        // rotation is ambiguous, so choose a deterministic transported tangent.
        var tangent = Vector3.Transform(Vector3.UnitX, previous);
        tangent -= from * Vector3.Dot(tangent, from);
        if (tangent.LengthSquared() < 1e-6f)
        {
            tangent = Vector3.Transform(Vector3.UnitZ, previous);
            tangent -= from * Vector3.Dot(tangent, from);
        }
        return Quaternion.CreateFromAxisAngle(Vector3.Normalize(tangent), MathF.PI);
    }

    private static Quaternion PmxRotation(Vector3 euler) => Quaternion.Normalize(
        Quaternion.CreateFromAxisAngle(Vector3.UnitY, euler.Y) *
        Quaternion.CreateFromAxisAngle(Vector3.UnitX, euler.X) *
        Quaternion.CreateFromAxisAngle(Vector3.UnitZ, euler.Z));
    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static bool Valid(Quaternion value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) &&
        float.IsFinite(value.W) && float.IsFinite(value.LengthSquared()) && value.LengthSquared() > 1e-12f;
}
