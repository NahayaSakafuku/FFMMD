namespace FFMMD.Skirt;

/// <summary>File declarations only; parsing a profile does not establish runtime bone ownership.</summary>
public sealed class PhybProfile
{
    public string Status = "NotRead";
    public string? Error;
    public string SourcePath = "";
    public string Sha256 = "";
    public int FileSize;
    public uint Version, DataType, CollisionOffset, SimulationOffset;
    public bool HasUnparsedTrailingData;
    public string RuntimeOwnership = "Unknown: file declarations must be correlated with runtime resource slots and simulator observations.";
    public string[] Warnings = [];
    public PhybCapsuleProfile[] Capsules = [];
    public PhybSphereProfile[] Spheres = [];
    public PhybSimulatorProfile[] Simulators = [];
    public bool Parsed => Status is "Parsed" or "ParsedBaseProfileOnly";
}

/// <summary>
/// A bounded copy-and-parse result for one already-loaded native resource.  The
/// profile describes file declarations only; it does not claim runtime ownership.
/// </summary>
public sealed class PhybProfileObservation
{
    public int ResourceIndex;
    public string CopyStatus = "NotAttempted";
    public bool PayloadCopied;
    public string? SourceFileName;
    public PhybProfile? Profile;
    public string[] DeclaredClothingSkirtBones = [];
    public string[] PresentOnTargetSkeleton = [];
    public string[] MissingFromTargetSkeleton = [];
    public string RuntimeOwnership = "Unknown: file declarations are correlated with the native resource slot, not a controlled simulator bone list.";
}

public sealed class PhybCapsuleProfile
{
    public string Name = "", StartBone = "", EndBone = "";
    public float[] StartOffset = [], EndOffset = [];
    public float Radius;
}

public sealed class PhybSphereProfile
{
    public string Name = "", BoneName = "";
    public float[] Offset = [];
    public float Thickness;
    public float Radius;
}

public sealed class PhybSimulatorProfile
{
    public int Index;
    public PhybSimulatorParamsProfile Params = new();
    public PhybCollisionReferenceProfile[] CollisionObjects = [], CollisionConnections = [];
    public PhybChainProfile[] Chains = [];
    public int ConnectorCount, AttractCount, PinCount, SpringCount, PostAlignmentCount;
    public int DeclaredConstraintCount => ConnectorCount + AttractCount + PinCount + SpringCount + PostAlignmentCount;
}

public sealed class PhybSimulatorParamsProfile
{
    public float[] Gravity = [], Wind = [];
    public ushort ConstraintLoop, CollisionLoop;
    public byte Flags, Group;
    public bool IsClothing => Group == 2;
}

public sealed class PhybChainProfile
{
    public int Index;
    public float Dampening, MaxSpeed, Friction, CollisionDampening, RepulsionStrength;
    public float[] LastBoneOffset = [];
    public uint Type;
    public PhybCollisionReferenceProfile[] Collisions = [];
    public PhybNodeProfile[] Nodes = [];
}

public sealed class PhybCollisionReferenceProfile
{
    public string CollisionName = "";
    public uint Type;
}

public sealed class PhybNodeProfile
{
    public int Index;
    public string BoneName = "";
    public float Radius, AttractByAnimation, WindScale, GravityScale, ConeMaxAngle;
    public float[] ConeAxisOffset = [], ConstraintPlaneNormal = [];
    public uint CollisionFlag, ContinuousCollisionFlag;
}
