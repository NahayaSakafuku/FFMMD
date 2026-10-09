using System.Numerics;

namespace FFMMD.Skirt;

/// <summary>PMX source physics data in its original MMD model coordinates and units.</summary>
public sealed class PmxPhysicsProfile
{
    public string Name { get; init; } = "";
    public string Fingerprint { get; init; } = "";
    public float Version { get; init; }
    public int BoneCount { get; init; }
    public string[] BoneNames { get; init; } = [];
    public PmxRigidBody[] RigidBodies { get; init; } = [];
    public PmxPhysicsJoint[] Joints { get; init; } = [];
    public int SoftBodyCount { get; init; }
    public long TrailingBytes { get; init; }
    public string[] Warnings { get; init; } = [];

    /// <summary>The current rigid-body baker supports PMX six-axis spring joints only.</summary>
    public bool HasUnsupportedPhysics => SoftBodyCount != 0 || Joints.Any(j => j.Type != 0);
}

public enum PmxRigidBodyShape : byte { Sphere = 0, Box = 1, Capsule = 2 }
public enum PmxRigidBodyMode : byte { Bone = 0, Physics = 1, PhysicsAndBone = 2 }

public sealed class PmxRigidBody
{
    public int Index { get; init; }
    public string Name { get; init; } = "";
    public string EnglishName { get; init; } = "";
    public int BoneIndex { get; init; }
    public byte CollisionGroup { get; init; }
    /// <summary>A set bit allows collisions with that group, matching the raw PMX and Bullet group mask.</summary>
    public ushort CollisionMask { get; init; }
    public PmxRigidBodyShape Shape { get; init; }
    /// <summary>Sphere: X radius; box: XYZ half extents; capsule: X radius and Y cylindrical length.</summary>
    public Vector3 Size { get; init; }
    public Vector3 Position { get; init; }
    /// <summary>PMX model-space Euler angles, radians; quaternion composition is qY * qX * qZ. No handedness/unit conversion is applied here.</summary>
    public Vector3 RotationEuler { get; init; }
    public float Mass { get; init; }
    public float LinearDamping { get; init; }
    public float AngularDamping { get; init; }
    public float Restitution { get; init; }
    public float Friction { get; init; }
    public PmxRigidBodyMode Mode { get; init; }
}

public sealed class PmxPhysicsJoint
{
    public int Index { get; init; }
    public string Name { get; init; } = "";
    public string EnglishName { get; init; } = "";
    /// <summary>0=PMX spring six degrees of freedom. Other PMX 2.1 kinds remain unsupported.</summary>
    public byte Type { get; init; }
    public int RigidBodyA { get; init; }
    public int RigidBodyB { get; init; }
    public Vector3 Position { get; init; }
    public Vector3 RotationEuler { get; init; }
    public Vector3 TranslationMinimum { get; init; }
    public Vector3 TranslationMaximum { get; init; }
    public Vector3 RotationMinimum { get; init; }
    public Vector3 RotationMaximum { get; init; }
    public Vector3 TranslationSpring { get; init; }
    public Vector3 RotationSpring { get; init; }
}
