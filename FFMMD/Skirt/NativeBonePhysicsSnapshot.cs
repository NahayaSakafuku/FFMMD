using System;

namespace FFMMD.Skirt;

/// <summary>Read-only observations around one game physics invocation.</summary>
public enum NativeBonePhysicsStage
{
    BeforeGamePhysics,
    AfterGamePhysics,
    AfterFFMMDBodyWrite,
    FinalRender,
    BeforeClothingSimulation,
    AfterClothingSimulation,
}

/// <summary>
/// Managed diagnostic schema. A simulator sharing a skeleton is not evidence that it controls
/// particular bones. Unsupported native fields remain explicitly unavailable.
/// </summary>
public sealed class SkirtPhysicsSnapshot
{
    public int SchemaVersion = 2;
    public string Mode = "ReadOnlyNativeInspection";
    public string Status = "NotConfigured";
    public string? InvalidationReason;
    public string ClientStructsVersion = "";
    public string ClientStructsModuleId = "";
    public string CharacterBaseAddress = "0x0";
    public string SkeletonAddress = "0x0";
    public string MainPoseAddress = "0x0";
    public string SkirtControlAssessment = "Unknown";
    public string SkirtControlEvidence = "The installed native schema exposes simulator skeletons, but no controlled bone or chain indices.";
    public bool ControlledSkirtBoneIndicesAvailable;
    public bool CollisionGeometryAvailable;
    public string CollisionGeometryNote = "Only the public collision shape field is exposed; centers, radii, bone bindings and simulator ownership are unavailable.";
    public string ResourceGroupingNote = "Each simulator vector, collision vector and resource handle uses the same module resource index. This establishes resource-slot grouping, not individual simulator/collider ownership.";
    public string PoseCacheNote = "Raw pose arrays and their sync/dirty flags are observed without synchronizing Havok. Dirty caches are not authoritative pose output.";
    public string PoseConcurrencyNote = "Reads are bounded observations rather than an atomic memory snapshot. Pointer identities and cache sync flags are rechecked; concurrent native jobs can still change values between reads.";
    public string StageTimingNote = "The first four stages describe UpdateBonePhysics and body/final observation boundaries. The two Clothing stages describe one separately verified simulator method invocation, identified by SimulationObservation. Completion of other native jobs is not established.";
    public string[] PlanWarnings = Array.Empty<string>();
    public NativeBonePhysicsStageSnapshot[] Stages = Array.Empty<NativeBonePhysicsStageSnapshot>();
    public NativeSimulationObservationSnapshot SimulationObservation = new();
}

public sealed class NativeBonePhysicsStageSnapshot
{
    public string Stage = "";
    public bool Captured;
    public string Status = "NotObserved";
    public long TimestampTicks;
    public string ModuleAddress = "0x0";
    public string ModuleSkeletonAddress = "0x0";
    public bool ModuleFieldsAvailable;
    public float? FrameDeltaTime;
    public float? OverrideSimulationTime;
    public bool UseOverrideSimulationTime;
    public NativeBonePhysicsResourceSlotSnapshot[] ResourceSlots = Array.Empty<NativeBonePhysicsResourceSlotSnapshot>();
    public NativeSkirtPoseSnapshot[] SkirtBones = Array.Empty<NativeSkirtPoseSnapshot>();
    public NativeBodyPoseSnapshot[] BodyBones = Array.Empty<NativeBodyPoseSnapshot>();
}

public sealed class NativeSimulationObservationSnapshot
{
    public bool Available;
    public string Status = "NotObserved";
    public long Token;
    public string SimulatorAddress = "0x0";
    public string ModuleAddress = "0x0";
    public string Method = "";
    public int ThreadId;
    public long TimestampTicks;
    public long AfterTimestampTicks;
    public int? ResourceIndex;
    public bool BeforeCaptured;
    public bool AfterCaptured;
    public bool? OriginalCompleted;
    public string BoundaryNote = "A pair requires the same token, simulator, module, method and thread and a normal original return. Entry is selected between the diagnostic global pre boundary and its body write; the matching exit may occur later before the deadline. Timestamps describe its relation to the frozen global stages. Missing callbacks do not establish absence of native simulation.";
}

public sealed class NativeBonePhysicsResourceSlotSnapshot
{
    public int ResourceIndex;
    public string SimulatorVectorStatus = "NotObserved";
    public string CollisionVectorStatus = "NotObserved";
    public int? SimulatorCount;
    public int? CollisionCount;
    public int ClothingSimulatorCount;
    public NativeBonePhysicsResourceSnapshot Resource = new();
    public NativeBoneSimulatorSnapshot[] Simulators = Array.Empty<NativeBoneSimulatorSnapshot>();
    public NativeBoneCollisionSnapshot[] Collisions = Array.Empty<NativeBoneCollisionSnapshot>();
}

public sealed class NativeBonePhysicsResourceSnapshot
{
    public string Address = "0x0";
    public string Status = "NotObserved";
    public uint? Id;
    public uint? ResourceType;
    public uint? FileType;
    public string? FileName;
    public bool FileNameReadable;
    public bool? FileNameIsPhyb;
    public uint? FileSize;
    public uint? FileSize2;
    public uint? FileSize3;
    public string DataAddress = "0x0";
    public ulong? DataLength;
    public string PayloadMetadataStatus = "NotObserved";
    public string TransientBlobAddress = "0x0";
    public ulong? TransientBlobLength;
    public string BlobLengthNote = "ResourceHandle.Blob is a temporary loading buffer; its length is not exposed. DataLength is the separate DefaultResourceHandle payload length, not Blob length. Tick-only payload copy results are recorded separately in PhybProfiles.";
    public byte? ReadState;
    public byte? LoadState;
}

public sealed class NativeBoneSimulatorSnapshot
{
    public int VectorIndex;
    public string Address = "0x0";
    public string Status = "NotObserved";
    public uint? Group;
    public string GroupName = "Unknown";
    public bool IsClothing;
    public string SkeletonAddress = "0x0";
    public bool SharesTargetSkeleton;
    public string SkirtSkeletonAssociation = "Unknown";
    public string[] CandidateSkirtBonesOnSharedSkeleton = Array.Empty<string>();
    public bool ControlledBoneIndicesAvailable;
    public string ControlledSkirtBones = "Unknown: simulator bone/chain indices are not exposed by this native schema.";
    public int? ConstraintCount;
    public string ConstraintVectorStatus = "NotObserved";
    public ushort ConstraintLoop;
    public ushort CollisionLoop;
    public bool IsSimulating;
    public bool IsTimeIntegrating;
    public bool IsCollidable;
    public bool ContinuousCollisions;
    public bool UsingGroundPlane;
    public bool FixedLength;
    public bool IsStarted;
    public bool IsStopped;
    public bool IsReset;
    public float? SimulationTime;
    public float? SimulationTimeInv;
    public float[]? CharacterPosition;
    public float[]? Gravity;
    public float[]? Wind;
}

public sealed class NativeBoneCollisionSnapshot
{
    public int VectorIndex;
    public string Address = "0x0";
    public string Status = "NotObserved";
    public byte? Shape;
    public string ShapeName = "Unknown";
    public bool GeometryAvailable;
    public string SimulatorOwnership = "Unavailable; only the shared resource-slot index is known.";
}

public class NativeSkirtPoseSnapshot
{
    public int PartialSkeletonIndex;
    public int BoneIndex;
    public string BoneName = "";
    public string PoseAddress = "0x0";
    public string HavokSkeletonAddress = "0x0";
    public string Status = "NotObserved";
    public byte? LocalInSync;
    public byte? ModelInSync;
    public uint? BoneFlags;
    public float[]? RawLocalRotation;
    public float[]? RawLocalPosition;
    public float[]? RawLocalScale;
    public float[]? RawModelRotation;
    public float[]? RawModelPosition;
    public float[]? RawModelScale;
}

public sealed class NativeBodyPoseSnapshot : NativeSkirtPoseSnapshot
{
}
