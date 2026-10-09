using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.Graphics.Physics;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using FFXIVClientStructs.Havok.Animation.Rig;
using FFXIVClientStructs.Havok.Common.Base.Math.QsTransform;

namespace FFMMD.Skirt;

/// <summary>
/// Diagnostic-only reader, independent of Retargeter. Configure and serialization belong on Tick;
/// Capture copies bounded public native fields into preallocated storage without calling Havok,
/// simulator, collision, resource-loading or other game functions. The owning slot serializes access.
/// </summary>
public sealed unsafe class NativeBonePhysicsInspector
{
    private const int ResourceCount = 5;
    private const int MaximumVectorCount = 128;
    private const int MaximumConstraintCount = 4096;
    private const int MaximumBones = 2048;
    private const int MaximumPartials = 16;
    private const int MaximumSkirtBones = 256;
    private const int MaximumFileName = 512;
    private const int MaximumBoneName = 128;
    private readonly StageBuffer[] _stages = { new(), new(), new(), new(), new(), new() };
    private readonly ResourceBuffer _payloadBefore = new();
    private readonly ResourceBuffer _payloadAfter = new();
    private PosePlan[] _poses = Array.Empty<PosePlan>();
    private BonePlan[] _bones = Array.Empty<BonePlan>();
    private readonly nint[] _resourceAddresses = new nint[ResourceCount];
    private readonly ResourceBuffer[] _planResources = { new(), new(), new(), new(), new() };
    private readonly VectorHeader[] _simulatorVectors = new VectorHeader[ResourceCount];
    private readonly VectorHeader[] _collisionVectors = new VectorHeader[ResourceCount];
    private nint _characterBase, _skeleton, _mainPose, _module, _moduleSkeleton, _partials;
    private ushort _partialCount;
    private bool _enabled;
    private string _status = "NotConfigured";
    private string? _invalidationReason;
    private string[] _warnings = Array.Empty<string>();
    private readonly SimulationObservation _simulation = new();

    public bool HasPairedClothingObservation => _simulation.Status == SimulationStatus.Paired;
    public bool HasPendingClothingObservation => _enabled &&
        (_simulation.Status is SimulationStatus.AwaitingMatchingAfter or SimulationStatus.AwaitingMatchingAfter_MismatchedCallbackSeen);

    /// <summary>Builds the name/pose plan on Tick. Never call this from a native hook.</summary>
    public void ConfigureTarget(CharacterBase* characterBase, Skeleton* expectedSkeleton, hkaPose* expectedPose)
    {
        _enabled = false;
        _characterBase = (nint)characterBase;
        _skeleton = (nint)expectedSkeleton;
        _mainPose = (nint)expectedPose;
        _module = _moduleSkeleton = _partials = 0;
        _partialCount = 0;
        _poses = Array.Empty<PosePlan>();
        _bones = Array.Empty<BonePlan>();
        _invalidationReason = null;
        _warnings = Array.Empty<string>();
        _simulation.Reset();
        Array.Clear(_resourceAddresses);
        foreach (var resource in _planResources) resource.Reset();
        Array.Clear(_simulatorVectors);
        Array.Clear(_collisionVectors);
        foreach (var stage in _stages) stage.Reset();
        if (!OperatingSystem.IsWindows()) { _status = "UnsupportedPlatform"; return; }
        if (!LayoutSupported()) { _status = "UnsupportedNativeLayout"; return; }
        if (_characterBase == 0 || _skeleton == 0 || _mainPose == 0 ||
            !NativePhysicsRead.TryRead(_characterBase, out CharacterBase character) ||
            (nint)character.Skeleton != _skeleton ||
            !NativePhysicsRead.TryRead(_skeleton, out Skeleton skeleton))
        { _status = "TargetUnavailable"; return; }
        _module = (nint)character.BonePhysicsModule;
        _partialCount = skeleton.PartialSkeletonCount;
        _partials = (nint)skeleton.PartialSkeletons;
        if (_partialCount is 0 or > MaximumPartials || _partials == 0)
        { _status = "InvalidPartialSkeletonRange"; return; }

        var warnings = new List<string>();
        var poses = new List<PosePlan>();
        var bones = new List<BonePlan>();
        for (var partialIndex = 0; partialIndex < _partialCount; partialIndex++)
        {
            var partialAddress = _partials + partialIndex * sizeof(PartialSkeleton);
            if (!NativePhysicsRead.TryRead(partialAddress, out PartialSkeleton partial))
            { warnings.Add($"Partial skeleton {partialIndex} is unreadable."); continue; }
            var poseAddress = (nint)partial.GetHavokPose(0);
            if (poseAddress == 0) continue;
            if (!TryBuildPosePlan(poseAddress, partialIndex, out var plan, out var planBones))
            { warnings.Add($"Partial skeleton {partialIndex} has an invalid or unreadable pose/bone range."); continue; }
            if (bones.Count + planBones.Count > MaximumSkirtBones)
            { warnings.Add($"Observed skirt/body bone limit ({MaximumSkirtBones}) reached; additional partials are unavailable."); break; }
            plan.BoneStart = bones.Count;
            plan.BoneCount = planBones.Count;
            if (plan.UnreadableBoneNameCount != 0)
                warnings.Add($"Partial skeleton {partialIndex}: {plan.UnreadableBoneNameCount} bone names were unreadable; absence of skirt names is inconclusive.");
            bones.AddRange(planBones);
            poses.Add(plan);
        }
        if (!poses.Any(p => p.PoseAddress == _mainPose))
        { _status = "MainPoseNotInTargetSkeleton"; _warnings = warnings.ToArray(); return; }
        _poses = poses.ToArray();
        _bones = bones.ToArray();
        _warnings = warnings.ToArray();
        _status = _module == 0 ? "NoPhysicsModule" : "Configured";
        if (_module != 0 && NativePhysicsRead.TryRead(_module, out BonePhysicsModule module))
        {
            _moduleSkeleton = (nint)module.Skeleton;
            for (var i = 0; i < ResourceCount; i++)
            {
                _resourceAddresses[i] = (nint)module.BonePhysicsResourceHandles[i].Value;
                CaptureResource(_resourceAddresses[i], _planResources[i]);
                _simulatorVectors[i] = SimulatorVector(in module, i);
                _collisionVectors[i] = CollisionVector(in module, i);
            }
        }
        else if (_module != 0) _status = "PhysicsModuleUnreadable";
        // Pose observations remain useful even when the physics module is absent or unreadable.
        _enabled = true;
    }

    /// <summary>Stops new native reads while preserving observations for export.</summary>
    public void Invalidate(string reason)
    {
        _enabled = false;
        _status = "Invalidated";
        _invalidationReason = reason;
    }

    /// <summary>
    /// Called by the specific simulator observer while the owning slot holds its pose lock.
    /// Records the first verified Clothing invocation and accepts only its exact matching return.
    /// The string is supplied by the preexisting observer; this method allocates no capture data.
    /// </summary>
    public void ObserveSimulationCall(long token, nint simulator, nint module, string method,
        bool before, int threadId, long timestamp, bool originalCompleted = true)
    {
        if (!_enabled || _simulation.Status is SimulationStatus.Paired or SimulationStatus.PairCaptureFailed or SimulationStatus.PairIdentityChanged) return;
        if (before)
        {
            if (_simulation.BeforeCaptured) return;
            if (!TryVerifyClothingSimulator(simulator, module, out var resourceIndex)) return;
            _simulation.Token = token;
            _simulation.Simulator = simulator;
            _simulation.Module = module;
            _simulation.Method = method;
            _simulation.ThreadId = threadId;
            _simulation.TimestampTicks = timestamp;
            _simulation.ResourceIndex = resourceIndex;
            _simulation.BeforeCaptured = true;
            _simulation.BeforeValid = Capture(NativeBonePhysicsStage.BeforeClothingSimulation);
            _simulation.Status = _simulation.BeforeValid ? SimulationStatus.AwaitingMatchingAfter : SimulationStatus.PairCaptureFailed;
            return;
        }
        if (!_simulation.BeforeCaptured) return;
        if (_simulation.Token != token || _simulation.Simulator != simulator || _simulation.Module != module ||
            !string.Equals(_simulation.Method, method, StringComparison.Ordinal) || _simulation.ThreadId != threadId)
        {
            _simulation.Status = SimulationStatus.AwaitingMatchingAfter_MismatchedCallbackSeen;
            return;
        }
        if (_simulation.AfterCaptured) return;
        _simulation.OriginalCompleted = originalCompleted;
        _simulation.AfterTimestampTicks = timestamp;
        if (!originalCompleted)
        {
            _simulation.Status = SimulationStatus.PairCaptureFailed;
            return;
        }
        _simulation.AfterCaptured = true;
        if (!TryVerifyClothingSimulator(simulator, module, out var afterResourceIndex) ||
            afterResourceIndex != _simulation.ResourceIndex)
        { _simulation.Status = SimulationStatus.PairIdentityChanged; return; }
        _simulation.AfterValid = Capture(NativeBonePhysicsStage.AfterClothingSimulation);
        _simulation.Status = _simulation.BeforeValid && _simulation.AfterValid
            ? SimulationStatus.Paired : SimulationStatus.PairCaptureFailed;
    }

    /// <summary>
    /// Tick-only copy of an already loaded .phyb payload. Revalidates all public target/resource
    /// identities before and after the bounded copy. No resource-loading or native game call occurs.
    /// </summary>
    public bool TryCopyPlannedResourcePayload(int resourceIndex, int maximumBytes, out byte[] payload, out string status)
    {
        payload = Array.Empty<byte>();
        status = "NotConfigured";
        if (!_enabled || (uint)resourceIndex >= ResourceCount || maximumBytes <= 0) return false;
        if (!TryReadLiveModule(out var module)) { status = "TargetOrModuleChanged"; return false; }
        var address = (nint)module.BonePhysicsResourceHandles[resourceIndex].Value;
        if (address == 0) { status = "Absent"; return false; }
        if (address != _resourceAddresses[resourceIndex]) { status = "ResourceChangedSincePlan"; return false; }
        _payloadBefore.Reset();
        CaptureResource(address, _payloadBefore);
        if (_payloadBefore.Status != ReadStatus.Observed || !SameResourceIdentity(_payloadBefore, _planResources[resourceIndex]))
        { status = "ResourceChangedOrUnreadable"; return false; }
        if (_payloadBefore.Header.FileType != 0x70687962 || _payloadBefore.FileNameStatus != ReadStatus.Observed ||
            !IsPhybName(_payloadBefore.FileName.AsSpan(0, _payloadBefore.FileNameLength)))
        { status = "ResourceTypeUnconfirmed"; return false; }
        if (_payloadBefore.PayloadStatus != ReadStatus.Observed || _payloadBefore.DataLength == 0)
        { status = "InvalidOrEmptyPayload"; return false; }
        if (_payloadBefore.DataLength > (ulong)maximumBytes || _payloadBefore.DataLength > int.MaxValue)
        { status = "PayloadLimitExceeded"; return false; }
        var copy = new byte[(int)_payloadBefore.DataLength];
        if (!NativePhysicsRead.TryReadBytes(_payloadBefore.DataAddress, copy))
        { status = "PayloadUnreadable"; return false; }
        _payloadAfter.Reset();
        CaptureResource(address, _payloadAfter);
        if (!TryReadLiveModule(out var afterModule) || !SameModuleIdentity(in module, in afterModule) ||
            (nint)afterModule.BonePhysicsResourceHandles[resourceIndex].Value != address ||
            !SameResourceIdentity(_payloadBefore, _payloadAfter) ||
            !SameResourceIdentity(_payloadAfter, _planResources[resourceIndex]))
        { status = "ResourceChangedDuringCopy"; return false; }
        payload = copy;
        status = "CopiedLoadedPayload";
        return true;
    }

    private bool TryReadLiveModule(out BonePhysicsModule module)
    {
        module = default;
        return _module != 0 && NativePhysicsRead.TryRead(_characterBase, out CharacterBase character) &&
               (nint)character.Skeleton == _skeleton && (nint)character.BonePhysicsModule == _module &&
               NativePhysicsRead.TryRead(_skeleton, out Skeleton skeleton) && skeleton.PartialSkeletonCount == _partialCount &&
               (nint)skeleton.PartialSkeletons == _partials && NativePhysicsRead.TryRead(_module, out module) &&
               (nint)module.Skeleton == _skeleton && (nint)module.Skeleton == _moduleSkeleton;
    }

    private bool TryVerifyClothingSimulator(nint simulator, nint moduleAddress, out int resourceIndex)
    {
        resourceIndex = -1;
        if (moduleAddress != _module || !TryReadLiveModule(out var module) ||
            !NativePhysicsRead.TryRead(simulator, out BoneSimulator sim) ||
            sim.Group != BoneSimulator.PhysicsGroup.Clothing || (nint)sim.Skeleton != _skeleton) return false;
        for (var i = 0; i < ResourceCount; i++)
        {
            var vector = SimulatorVector(in module, i);
            if (!NativePhysicsRead.TryGetVectorCount(vector.First, vector.Last, vector.End,
                sizeof(nint), MaximumVectorCount, out var count)) continue;
            for (var j = 0; j < count; j++)
            {
                if (!NativePhysicsRead.TryRead(vector.First + j * sizeof(nint), out nint pointer) || pointer != simulator) continue;
                if (!TryReadLiveModule(out var afterModule) || !SameModuleIdentity(in module, in afterModule) ||
                    !NativePhysicsRead.TryRead(vector.First + j * sizeof(nint), out nint afterPointer) || afterPointer != simulator ||
                    !NativePhysicsRead.TryRead(simulator, out BoneSimulator afterSim) || afterSim.Group != sim.Group ||
                    afterSim.Skeleton != sim.Skeleton) return false;
                resourceIndex = i;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Captures at most once per stage. Native reads are bounded, allocation-free and read-only.
    /// The stage denotes an observed hook boundary, not proof of completion of asynchronous jobs.
    /// </summary>
    public bool Capture(NativeBonePhysicsStage stage)
    {
        var index = (int)stage;
        if (!_enabled || (uint)index >= (uint)_stages.Length) return false;
        var buffer = _stages[index];
        if (buffer.Captured) return buffer.Status is ReadStatus.Observed or ReadStatus.ObservedWithUnavailableFields or ReadStatus.NoPhysicsModule;
        buffer.Captured = true;
        buffer.TimestampTicks = Stopwatch.GetTimestamp();
        buffer.Status = ReadStatus.TargetChanged;
        if (!NativePhysicsRead.TryRead(_characterBase, out CharacterBase character) ||
            (nint)character.Skeleton != _skeleton ||
            !NativePhysicsRead.TryRead(_skeleton, out Skeleton skeleton) ||
            skeleton.PartialSkeletonCount != _partialCount || (nint)skeleton.PartialSkeletons != _partials)
            return false;
        if (!CapturePoses(buffer)) { buffer.Status = ReadStatus.PoseChangedOrUnreadable; return false; }
        buffer.ModuleAddress = (nint)character.BonePhysicsModule;
        if (buffer.ModuleAddress != _module) { buffer.Status = ReadStatus.ModuleChangedSincePlan; return false; }
        if (_module == 0) { buffer.Status = ReadStatus.NoPhysicsModule; return true; }
        if (!NativePhysicsRead.TryRead(_module, out BonePhysicsModule module))
        { buffer.Status = ReadStatus.Unreadable; return false; }
        buffer.ModuleSkeleton = (nint)module.Skeleton;
        buffer.ModuleFieldsAvailable = true;
        buffer.FrameDeltaTime = module.FrameDeltaTime;
        buffer.OverrideSimulationTime = module.OverrideSimulationTime;
        buffer.UseOverrideSimulationTime = module.UseOverrideSimulationTime;
        if ((nint)module.Skeleton != _skeleton || (nint)module.Skeleton != _moduleSkeleton)
        { buffer.Status = ReadStatus.ModuleSkeletonMismatch; return false; }
        buffer.Status = ReadStatus.Observed;
        for (var i = 0; i < ResourceCount; i++) CaptureResourceSlot(in module, buffer.Groups[i], i, buffer);
        if (buffer.Status == ReadStatus.Observed && buffer.PoseFieldsUnavailable)
            buffer.Status = ReadStatus.ObservedWithUnavailableFields;
        // A changing vector or resource handle invalidates the observation, without invalidating
        // the body-animation slot or attempting to repair/reload native physics.
        if (!NativePhysicsRead.TryRead(_module, out BonePhysicsModule after) || !SameModuleIdentity(in module, in after))
            buffer.Status = ReadStatus.ModuleChangedDuringRead;
        if (!NativePhysicsRead.TryRead(_characterBase, out CharacterBase afterCharacter) ||
            (nint)afterCharacter.Skeleton != _skeleton || (nint)afterCharacter.BonePhysicsModule != _module)
            buffer.Status = ReadStatus.TargetChanged;
        return buffer.Status is ReadStatus.Observed or ReadStatus.ObservedWithUnavailableFields;
    }

    /// <summary>Creates managed JSON data on Tick; never call this from a native hook.</summary>
    public SkirtPhysicsSnapshot CreateSnapshot()
    {
        var assembly = typeof(BonePhysicsModule).Assembly;
        var result = new SkirtPhysicsSnapshot
        {
            Status = _status,
            InvalidationReason = _invalidationReason,
            ClientStructsVersion = assembly.GetName().Version?.ToString() ?? "Unknown",
            ClientStructsModuleId = assembly.ManifestModule.ModuleVersionId.ToString(),
            CharacterBaseAddress = Address(_characterBase),
            SkeletonAddress = Address(_skeleton),
            MainPoseAddress = Address(_mainPose),
            PlanWarnings = _warnings,
            Stages = new NativeBonePhysicsStageSnapshot[_stages.Length],
            SimulationObservation = new NativeSimulationObservationSnapshot
            {
                Available = HasPairedClothingObservation, Status = _simulation.Status.ToString(),
                OriginalCompleted = _simulation.OriginalCompleted,
                Token = _simulation.Token, SimulatorAddress = Address(_simulation.Simulator),
                ModuleAddress = Address(_simulation.Module), Method = _simulation.Method,
                ThreadId = _simulation.ThreadId, TimestampTicks = _simulation.TimestampTicks,
                AfterTimestampTicks = _simulation.AfterTimestampTicks,
                ResourceIndex = _simulation.BeforeCaptured ? _simulation.ResourceIndex : null,
                BeforeCaptured = _simulation.BeforeCaptured, AfterCaptured = _simulation.AfterCaptured,
            },
        };
        for (var i = 0; i < _stages.Length; i++) result.Stages[i] = SerializeStage(_stages[i], (NativeBonePhysicsStage)i);
        return result;
    }

    private bool TryBuildPosePlan(nint address, int partialIndex, out PosePlan plan, out List<BonePlan> bones)
    {
        plan = new PosePlan();
        bones = new List<BonePlan>();
        if (!NativePhysicsRead.TryRead(address, out hkaPose pose) ||
            !NativePhysicsRead.TryRead((nint)pose.Skeleton, out hkaSkeleton skeleton)) return false;
        var count = skeleton.Bones.Length;
        if (count is <= 0 or > MaximumBones || skeleton.ParentIndices.Length != count ||
            skeleton.ReferencePose.Length != count || pose.LocalPose.Length != count || pose.ModelPose.Length != count ||
            pose.BoneFlags.Length != count || skeleton.Bones.Data == null || pose.LocalPose.Data == null ||
            pose.ModelPose.Data == null || pose.BoneFlags.Data == null ||
            !NativePhysicsRead.IsRangeValid((nint)skeleton.Bones.Data, count * sizeof(hkaBone)) ||
            !NativePhysicsRead.IsRangeValid((nint)pose.LocalPose.Data, count * sizeof(hkQsTransformf)) ||
            !NativePhysicsRead.IsRangeValid((nint)pose.ModelPose.Data, count * sizeof(hkQsTransformf)) ||
            !NativePhysicsRead.IsRangeValid((nint)pose.BoneFlags.Data, count * sizeof(uint))) return false;
        plan.PoseAddress = address;
        plan.SkeletonAddress = (nint)pose.Skeleton;
        plan.PartialIndex = partialIndex;
        plan.NativeBoneCount = count;
        plan.BoneNames = (nint)skeleton.Bones.Data;
        plan.Local = (nint)pose.LocalPose.Data;
        plan.Model = (nint)pose.ModelPose.Data;
        plan.Flags = (nint)pose.BoneFlags.Data;
        Span<byte> text = stackalloc byte[MaximumBoneName];
        for (var i = 0; i < count; i++)
        {
            if (!NativePhysicsRead.TryRead(plan.BoneNames + i * sizeof(hkaBone), out hkaBone bone)) return false;
            var namePointer = (nint)((ulong)bone.Name.StringAndFlag & ~1UL);
            if (!TryReadNullTerminated(namePointer, text, out var length)) { plan.UnreadableBoneNameCount++; continue; }
            var skirt = length >= 5 && text[0] == (byte)'j' && text[1] == (byte)'_' && text[2] == (byte)'s' &&
                text[3] == (byte)'k' && text[4] == (byte)'_';
            var name = Encoding.UTF8.GetString(text[..length]);
            if (!skirt && !IsBodyProbeBone(name)) continue;
            bones.Add(new BonePlan { Index = i, Name = name, PartialIndex = partialIndex, IsSkirt = skirt });
        }
        return true;
    }

    private bool CapturePoses(StageBuffer buffer)
    {
        foreach (var plan in _poses)
        {
            if (!NativePhysicsRead.TryRead(_partials + plan.PartialIndex * sizeof(PartialSkeleton), out PartialSkeleton partial) ||
                (nint)partial.GetHavokPose(0) != plan.PoseAddress ||
                !NativePhysicsRead.TryRead(plan.PoseAddress, out hkaPose pose) ||
                (nint)pose.Skeleton != plan.SkeletonAddress ||
                !NativePhysicsRead.TryRead(plan.SkeletonAddress, out hkaSkeleton skeleton) ||
                skeleton.Bones.Length != plan.NativeBoneCount || (nint)skeleton.Bones.Data != plan.BoneNames ||
                pose.LocalPose.Length != plan.NativeBoneCount || pose.ModelPose.Length != plan.NativeBoneCount ||
                pose.BoneFlags.Length != plan.NativeBoneCount || (nint)pose.LocalPose.Data != plan.Local ||
                (nint)pose.ModelPose.Data != plan.Model || (nint)pose.BoneFlags.Data != plan.Flags)
                return false;
            for (var i = plan.BoneStart; i < plan.BoneStart + plan.BoneCount; i++)
            {
                ref var capture = ref buffer.Bones[i];
                var bone = _bones[i];
                capture.PoseAddress = plan.PoseAddress;
                capture.SkeletonAddress = plan.SkeletonAddress;
                capture.LocalInSync = pose.LocalInSync;
                capture.ModelInSync = pose.ModelInSync;
                capture.Status = ReadStatus.Unreadable;
                if (!NativePhysicsRead.TryRead(plan.Local + bone.Index * sizeof(hkQsTransformf), out capture.Local) ||
                    !NativePhysicsRead.TryRead(plan.Model + bone.Index * sizeof(hkQsTransformf), out capture.Model) ||
                    !NativePhysicsRead.TryRead(plan.Flags + bone.Index * sizeof(uint), out capture.Flags))
                { buffer.PoseFieldsUnavailable = true; continue; }
                capture.Status = TransformFinite(in capture.Local) && TransformFinite(in capture.Model)
                    ? ReadStatus.Observed : ReadStatus.NonFiniteValues;
                if (capture.Status != ReadStatus.Observed) buffer.PoseFieldsUnavailable = true;
            }
            // Confirm array identities and flags have not changed while reading selected bones.
            if (!NativePhysicsRead.TryRead(plan.PoseAddress, out hkaPose after) ||
                (nint)after.Skeleton != plan.SkeletonAddress || (nint)after.LocalPose.Data != plan.Local ||
                (nint)after.ModelPose.Data != plan.Model || (nint)after.BoneFlags.Data != plan.Flags ||
                after.LocalPose.Length != plan.NativeBoneCount || after.ModelPose.Length != plan.NativeBoneCount ||
                after.BoneFlags.Length != plan.NativeBoneCount || after.LocalInSync != pose.LocalInSync ||
                after.ModelInSync != pose.ModelInSync) return false;
        }
        return true;
    }

    private void CaptureResourceSlot(in BonePhysicsModule module, GroupBuffer group, int index, StageBuffer stage)
    {
        var simulators = SimulatorVector(in module, index);
        var collisions = CollisionVector(in module, index);
        var resourceAddress = (nint)module.BonePhysicsResourceHandles[index].Value;
        if (!simulators.Equals(_simulatorVectors[index]) || !collisions.Equals(_collisionVectors[index]) ||
            resourceAddress != _resourceAddresses[index]) stage.Status = ReadStatus.ResourcesChangedSincePlan;
        CaptureResource(resourceAddress, group.Resource);
        if (group.Resource.Status == ReadStatus.ResourceChangedDuringRead) stage.Status = ReadStatus.ResourceChangedDuringRead;
        else if (group.Resource.Status == ReadStatus.Observed && _planResources[index].Status == ReadStatus.Observed &&
                 !SameResourceIdentity(group.Resource, _planResources[index])) stage.Status = ReadStatus.ResourcesChangedSincePlan;
        group.SimulatorStatus = ReadStatus.InvalidVector;
        if (NativePhysicsRead.TryGetVectorCount(simulators.First, simulators.Last, simulators.End,
                sizeof(nint), MaximumVectorCount, out var simulatorCount))
        {
            group.SimulatorCount = simulatorCount;
            group.SimulatorStatus = ReadStatus.Observed;
            for (var i = 0; i < simulatorCount; i++)
            {
                ref var capture = ref group.Simulators[i];
                capture.Status = ReadStatus.Unreadable;
                if (!NativePhysicsRead.TryRead(simulators.First + i * sizeof(nint), out capture.Address) ||
                    !NativePhysicsRead.TryRead(capture.Address, out capture.Simulator)) continue;
                capture.Status = ReadStatus.Observed;
                var constraints = new VectorHeader((nint)capture.Simulator.Constraints.First,
                    (nint)capture.Simulator.Constraints.Last, (nint)capture.Simulator.Constraints.End);
                capture.ConstraintStatus = NativePhysicsRead.TryGetVectorCount(constraints.First, constraints.Last,
                    constraints.End, sizeof(ConstraintBase), MaximumConstraintCount, out capture.ConstraintCount)
                    ? ReadStatus.Observed : ReadStatus.InvalidVector;
                if (!NativePhysicsRead.TryRead(capture.Address, out BoneSimulator afterSimulator) ||
                    afterSimulator.Skeleton != capture.Simulator.Skeleton || afterSimulator.Group != capture.Simulator.Group)
                    capture.Status = ReadStatus.ObjectChangedDuringRead;
            }
        }
        group.CollisionStatus = ReadStatus.InvalidVector;
        if (NativePhysicsRead.TryGetVectorCount(collisions.First, collisions.Last, collisions.End,
                sizeof(nint), MaximumVectorCount, out var collisionCount))
        {
            group.CollisionCount = collisionCount;
            group.CollisionStatus = ReadStatus.Observed;
            for (var i = 0; i < collisionCount; i++)
            {
                ref var capture = ref group.Collisions[i];
                capture.Status = ReadStatus.Unreadable;
                if (!NativePhysicsRead.TryRead(collisions.First + i * sizeof(nint), out capture.Address) ||
                    !NativePhysicsRead.TryRead(capture.Address, out CollisionBase collision)) continue;
                capture.Shape = (byte)collision.Shape;
                capture.Status = capture.Shape <= (byte)CollisionShape.Sphere ? ReadStatus.Observed : ReadStatus.UnknownShape;
            }
        }
        // The vector storage can keep the same header while individual pointer entries change.
        for (var i = 0; i < group.SimulatorCount; i++)
            if (!NativePhysicsRead.TryRead(simulators.First + i * sizeof(nint), out nint afterPointer) ||
                afterPointer != group.Simulators[i].Address) stage.Status = ReadStatus.ModuleChangedDuringRead;
        for (var i = 0; i < group.CollisionCount; i++)
            if (!NativePhysicsRead.TryRead(collisions.First + i * sizeof(nint), out nint afterPointer) ||
                afterPointer != group.Collisions[i].Address) stage.Status = ReadStatus.ModuleChangedDuringRead;
        if (stage.Status == ReadStatus.Observed &&
            (group.SimulatorStatus != ReadStatus.Observed || group.CollisionStatus != ReadStatus.Observed ||
             group.Resource.Status is ReadStatus.Unreadable ||
             group.Resource.Status == ReadStatus.Observed && _planResources[index].Status != ReadStatus.Observed ||
             group.Resource.FileNameStatus is ReadStatus.InvalidString or ReadStatus.Unreadable ||
             group.Resource.PayloadStatus is ReadStatus.Unreadable or ReadStatus.InvalidDataRange ||
             HasUnavailableEntries(group)))
            stage.Status = ReadStatus.ObservedWithUnavailableFields;
    }

    private static void CaptureResource(nint address, ResourceBuffer buffer)
    {
        buffer.Address = address;
        buffer.Status = address == 0 ? ReadStatus.Absent : ReadStatus.Unreadable;
        if (address == 0 || !NativePhysicsRead.TryRead(address, out buffer.Header)) return;
        buffer.Status = ReadStatus.Observed;
        var name = buffer.Header.FileName;
        if (name.Length > MaximumFileName || name.Length > name.Capacity || name.Capacity > 0x100000 ||
            (name.Capacity < 16 && name.Length >= 16)) buffer.FileNameStatus = ReadStatus.InvalidString;
        else
        {
            buffer.FileNameLength = (int)name.Length;
            if (name.Capacity < 16)
            {
                new ReadOnlySpan<byte>(name.Buffer, buffer.FileNameLength).CopyTo(buffer.FileName);
                buffer.FileNameStatus = ReadStatus.Observed;
            }
            else buffer.FileNameStatus = NativePhysicsRead.TryReadBytes((nint)name.BufferPtr,
                buffer.FileName.AsSpan(0, buffer.FileNameLength)) ? ReadStatus.Observed : ReadStatus.Unreadable;
        }
        // The base module stores ResourceHandle*. Only read the typed extension after a .phyb
        // filename establishes the expected resource kind; never call GetData/GetLength virtuals.
        buffer.PayloadStatus = ReadStatus.ResourceTypeUnconfirmed;
        if (buffer.FileNameStatus == ReadStatus.Observed && IsPhybName(buffer.FileName.AsSpan(0, buffer.FileNameLength)))
        {
            if (NativePhysicsRead.TryRead(address, out BonePhysicsResourceHandle typed))
            {
                buffer.DataAddress = (nint)typed.Data;
                buffer.DataLength = typed.Length;
                buffer.PayloadStatus = typed.Length != 0 && (typed.Length > int.MaxValue ||
                    !NativePhysicsRead.IsRangeValid((nint)typed.Data, (int)typed.Length))
                    ? ReadStatus.InvalidDataRange : ReadStatus.Observed;
                // Read metadata only: neither Data nor the transient Blob is dereferenced/copied.
            }
            else buffer.PayloadStatus = ReadStatus.Unreadable;
        }
        if (!NativePhysicsRead.TryRead(address, out ResourceHandle after) || after.Id != buffer.Header.Id ||
            after.FileType != buffer.Header.FileType || after.Blob != buffer.Header.Blob ||
            after.FileName.Length != buffer.Header.FileName.Length || after.FileName.Capacity != buffer.Header.FileName.Capacity ||
            after.FileName.BufferPtr != buffer.Header.FileName.BufferPtr ||
            after.ReadState != buffer.Header.ReadState || after.LoadState != buffer.Header.LoadState)
            buffer.Status = ReadStatus.ResourceChangedDuringRead;
        if (buffer.FileNameStatus == ReadStatus.Observed)
        {
            if (name.Capacity < 16)
            {
                if (!new ReadOnlySpan<byte>(after.FileName.Buffer, buffer.FileNameLength).SequenceEqual(
                        buffer.FileName.AsSpan(0, buffer.FileNameLength))) buffer.Status = ReadStatus.ResourceChangedDuringRead;
            }
            else if (!NativePhysicsRead.TryReadBytes((nint)name.BufferPtr,
                         buffer.ConfirmFileName.AsSpan(0, buffer.FileNameLength)) ||
                     !buffer.ConfirmFileName.AsSpan(0, buffer.FileNameLength).SequenceEqual(
                         buffer.FileName.AsSpan(0, buffer.FileNameLength)))
                buffer.Status = ReadStatus.ResourceChangedDuringRead;
        }
        if ((buffer.PayloadStatus is ReadStatus.Observed or ReadStatus.InvalidDataRange) &&
            (!NativePhysicsRead.TryRead(address, out BonePhysicsResourceHandle afterTyped) ||
             (nint)afterTyped.Data != buffer.DataAddress || afterTyped.Length != buffer.DataLength))
            buffer.Status = ReadStatus.ResourceChangedDuringRead;
    }

    private static bool SameResourceIdentity(ResourceBuffer a, ResourceBuffer b)
    {
        if (a.Address != b.Address || a.Status != b.Status) return false;
        if (a.Status == ReadStatus.Absent) return true;
        if (a.Status != ReadStatus.Observed) return false;
        return a.Header.Id == b.Header.Id && a.Header.Type.Value == b.Header.Type.Value &&
               a.Header.FileType == b.Header.FileType && a.Header.Blob == b.Header.Blob &&
               a.Header.FileSize == b.Header.FileSize && a.Header.FileSize2 == b.Header.FileSize2 &&
               a.Header.FileSize3 == b.Header.FileSize3 &&
               a.Header.ReadState == b.Header.ReadState && a.Header.LoadState == b.Header.LoadState &&
               a.FileNameStatus == b.FileNameStatus && a.FileNameLength == b.FileNameLength &&
               a.FileName.AsSpan(0, a.FileNameLength).SequenceEqual(b.FileName.AsSpan(0, b.FileNameLength)) &&
               a.PayloadStatus == b.PayloadStatus && a.DataAddress == b.DataAddress && a.DataLength == b.DataLength;
    }

    private static bool HasUnavailableEntries(GroupBuffer group)
    {
        for (var i = 0; i < group.SimulatorCount; i++)
            if (group.Simulators[i].Status != ReadStatus.Observed ||
                group.Simulators[i].ConstraintStatus != ReadStatus.Observed) return true;
        for (var i = 0; i < group.CollisionCount; i++)
            if (group.Collisions[i].Status != ReadStatus.Observed) return true;
        return false;
    }

    private NativeBonePhysicsStageSnapshot SerializeStage(StageBuffer buffer, NativeBonePhysicsStage stage)
    {
        var result = new NativeBonePhysicsStageSnapshot
        {
            Stage = stage.ToString(), Captured = buffer.Captured, Status = buffer.Status.ToString(),
            TimestampTicks = buffer.TimestampTicks, ModuleAddress = Address(buffer.ModuleAddress),
            ModuleSkeletonAddress = Address(buffer.ModuleSkeleton),
            ModuleFieldsAvailable = buffer.ModuleFieldsAvailable,
            FrameDeltaTime = buffer.ModuleFieldsAvailable ? Finite(buffer.FrameDeltaTime) : null,
            OverrideSimulationTime = buffer.ModuleFieldsAvailable ? Finite(buffer.OverrideSimulationTime) : null,
            UseOverrideSimulationTime = buffer.UseOverrideSimulationTime,
            ResourceSlots = buffer.ModuleAddress == 0 ? Array.Empty<NativeBonePhysicsResourceSlotSnapshot>() :
                new NativeBonePhysicsResourceSlotSnapshot[ResourceCount],
            SkirtBones = buffer.Captured ? new NativeSkirtPoseSnapshot[_bones.Count(b => b.IsSkirt)] : Array.Empty<NativeSkirtPoseSnapshot>(),
            BodyBones = buffer.Captured ? new NativeBodyPoseSnapshot[_bones.Count(b => !b.IsSkirt)] : Array.Empty<NativeBodyPoseSnapshot>(),
        };
        for (var i = 0; i < result.ResourceSlots.Length; i++) result.ResourceSlots[i] = SerializeGroup(buffer.Groups[i], i);
        var skirtIndex = 0;
        var bodyIndex = 0;
        for (var i = 0; buffer.Captured && i < _bones.Length; i++)
        {
            ref var capture = ref buffer.Bones[i];
            var bone = _bones[i];
            NativeSkirtPoseSnapshot item = bone.IsSkirt ? new NativeSkirtPoseSnapshot() : new NativeBodyPoseSnapshot();
            item.PartialSkeletonIndex = bone.PartialIndex; item.BoneIndex = bone.Index; item.BoneName = bone.Name;
            item.PoseAddress = Address(capture.PoseAddress); item.HavokSkeletonAddress = Address(capture.SkeletonAddress);
            item.Status = capture.Status.ToString();
            if (capture.Status is ReadStatus.Observed or ReadStatus.NonFiniteValues)
            {
                item.LocalInSync = capture.LocalInSync; item.ModelInSync = capture.ModelInSync; item.BoneFlags = capture.Flags;
                item.RawLocalRotation = Rotation(in capture.Local); item.RawLocalPosition = Position(in capture.Local);
                item.RawLocalScale = Scale(in capture.Local); item.RawModelRotation = Rotation(in capture.Model);
                item.RawModelPosition = Position(in capture.Model); item.RawModelScale = Scale(in capture.Model);
            }
            if (bone.IsSkirt) result.SkirtBones[skirtIndex++] = item;
            else result.BodyBones[bodyIndex++] = (NativeBodyPoseSnapshot)item;
        }
        return result;
    }

    private NativeBonePhysicsResourceSlotSnapshot SerializeGroup(GroupBuffer group, int index)
    {
        var resource = group.Resource;
        var headerReadable = resource.Status is ReadStatus.Observed or ReadStatus.ResourceChangedDuringRead;
        var item = new NativeBonePhysicsResourceSlotSnapshot
        {
            ResourceIndex = index, SimulatorVectorStatus = group.SimulatorStatus.ToString(),
            CollisionVectorStatus = group.CollisionStatus.ToString(),
            SimulatorCount = group.SimulatorStatus == ReadStatus.Observed ? group.SimulatorCount : null,
            CollisionCount = group.CollisionStatus == ReadStatus.Observed ? group.CollisionCount : null,
            Simulators = new NativeBoneSimulatorSnapshot[group.SimulatorCount],
            Collisions = new NativeBoneCollisionSnapshot[group.CollisionCount],
            Resource = new NativeBonePhysicsResourceSnapshot
            {
                Address = Address(resource.Address), Status = resource.Status.ToString(),
                Id = headerReadable ? resource.Header.Id : null,
                ResourceType = headerReadable ? resource.Header.Type.Value : null,
                FileType = headerReadable ? resource.Header.FileType : null,
                FileNameReadable = resource.FileNameStatus == ReadStatus.Observed,
                FileName = resource.FileNameStatus == ReadStatus.Observed ?
                    Encoding.UTF8.GetString(resource.FileName, 0, resource.FileNameLength) : null,
                FileNameIsPhyb = resource.FileNameStatus == ReadStatus.Observed ?
                    IsPhybName(resource.FileName.AsSpan(0, resource.FileNameLength)) : null,
                FileSize = headerReadable ? resource.Header.FileSize : null,
                FileSize2 = headerReadable ? resource.Header.FileSize2 : null,
                FileSize3 = headerReadable ? resource.Header.FileSize3 : null,
                DataAddress = Address(resource.DataAddress),
                DataLength = resource.PayloadStatus == ReadStatus.Observed ? resource.DataLength : null,
                PayloadMetadataStatus = resource.PayloadStatus.ToString(),
                TransientBlobAddress = headerReadable ? Address((nint)resource.Header.Blob) : "0x0",
                ReadState = headerReadable ? resource.Header.ReadState : null,
                LoadState = headerReadable ? resource.Header.LoadState : null,
            },
        };
        var candidateNames = _bones.Where(b => b.IsSkirt).Select(b => b.Name).Distinct(StringComparer.Ordinal).ToArray();
        for (var i = 0; i < item.Simulators.Length; i++)
        {
            ref var capture = ref group.Simulators[i];
            var sim = capture.Simulator;
            var available = capture.Status == ReadStatus.Observed;
            var sharesSkeleton = available && (nint)sim.Skeleton == _skeleton;
            var clothing = available && sim.Group == BoneSimulator.PhysicsGroup.Clothing;
            if (clothing) item.ClothingSimulatorCount++;
            item.Simulators[i] = new NativeBoneSimulatorSnapshot
            {
                VectorIndex = i, Address = Address(capture.Address), Status = capture.Status.ToString(),
                Group = available ? (uint)sim.Group : null,
                GroupName = available ? sim.Group.ToString() : "Unknown", IsClothing = clothing,
                SkeletonAddress = available ? Address((nint)sim.Skeleton) : "0x0", SharesTargetSkeleton = sharesSkeleton,
                SkirtSkeletonAssociation = sharesSkeleton ?
                    candidateNames.Length > 0 ? "SkirtBonesPresentOnSharedSkeleton_ControlUnknown" :
                    _warnings.Length > 0 ? "Unknown_NameOrPartialReadIncomplete" : "NoSkirtBonesFoundOnObservedPartials" : "Unknown",
                CandidateSkirtBonesOnSharedSkeleton = sharesSkeleton ? candidateNames : Array.Empty<string>(),
                ConstraintCount = capture.ConstraintStatus == ReadStatus.Observed ? capture.ConstraintCount : null,
                ConstraintVectorStatus = capture.ConstraintStatus.ToString(),
                ConstraintLoop = sim.ConstraintLoop, CollisionLoop = sim.CollisionLoop,
                IsSimulating = sim.IsSimulating, IsTimeIntegrating = sim.IsTimeIntegrating,
                IsCollidable = sim.IsCollidable, ContinuousCollisions = sim.ContinuousCollisions,
                UsingGroundPlane = sim.UsingGroundPlane, FixedLength = sim.FixedLength,
                IsStarted = sim.IsStarted, IsStopped = sim.IsStopped, IsReset = sim.IsReset,
                SimulationTime = available ? Finite(sim.SimulationTime) : null,
                SimulationTimeInv = available ? Finite(sim.SimulationTimeInv) : null,
                CharacterPosition = available ? Vector(sim.CharacterPosition.X, sim.CharacterPosition.Y, sim.CharacterPosition.Z) : null,
                Gravity = available ? Vector(sim.Gravity.X, sim.Gravity.Y, sim.Gravity.Z) : null,
                Wind = available ? Vector(sim.Wind.X, sim.Wind.Y, sim.Wind.Z) : null,
            };
        }
        for (var i = 0; i < item.Collisions.Length; i++)
        {
            ref var capture = ref group.Collisions[i];
            var available = capture.Status is ReadStatus.Observed or ReadStatus.UnknownShape;
            item.Collisions[i] = new NativeBoneCollisionSnapshot
            {
                VectorIndex = i, Address = Address(capture.Address), Status = capture.Status.ToString(),
                Shape = available ? capture.Shape : null,
                ShapeName = available ? ((CollisionShape)capture.Shape).ToString() : "Unknown",
            };
        }
        return item;
    }

    private static bool TryReadNullTerminated(nint address, Span<byte> destination, out int length)
    {
        length = 0;
        if (!NativePhysicsRead.IsRangeValid(address, destination.Length)) return false;
        for (var i = 0; i < destination.Length; i++)
        {
            if (!NativePhysicsRead.TryRead(address + i, out byte value)) return false;
            if (value == 0) { length = i; return true; }
            destination[i] = value;
        }
        return false;
    }

    private static bool LayoutSupported() => IntPtr.Size == 8 && sizeof(BonePhysicsModule) == 0x590 &&
        sizeof(BoneSimulator) == 0x450 && sizeof(CollisionBase) == 0x90 && sizeof(PartialSkeleton) == 0x230 &&
        sizeof(hkaPose) == 0x50 && sizeof(hkaSkeleton) == 0x88 && sizeof(ResourceHandle) == 0xB0 &&
        sizeof(BonePhysicsResourceHandle) == 0x120 && sizeof(hkQsTransformf) == 0x30 &&
        Marshal.OffsetOf<CharacterBase>(nameof(CharacterBase.BonePhysicsModule)).ToInt64() == 0x158 &&
        Marshal.OffsetOf<BoneSimulator>(nameof(BoneSimulator.Skeleton)).ToInt64() == 0x18;

    private static VectorHeader SimulatorVector(in BonePhysicsModule module, int index)
    {
        var v = index switch
        {
            0 => module.BoneSimulators.BoneSimulator_1, 1 => module.BoneSimulators.BoneSimulator_2,
            2 => module.BoneSimulators.BoneSimulator_3, 3 => module.BoneSimulators.BoneSimulator_4,
            _ => module.BoneSimulators.BoneSimulator_5,
        };
        return new VectorHeader((nint)v.First, (nint)v.Last, (nint)v.End);
    }

    private static VectorHeader CollisionVector(in BonePhysicsModule module, int index)
    {
        var v = index switch
        {
            0 => module.BoneCollisions.BoneCollision_1, 1 => module.BoneCollisions.BoneCollision_2,
            2 => module.BoneCollisions.BoneCollision_3, 3 => module.BoneCollisions.BoneCollision_4,
            _ => module.BoneCollisions.BoneCollision_5,
        };
        return new VectorHeader((nint)v.First, (nint)v.Last, (nint)v.End);
    }

    private static bool SameModuleIdentity(in BonePhysicsModule a, in BonePhysicsModule b)
    {
        if (a.Skeleton != b.Skeleton) return false;
        for (var i = 0; i < ResourceCount; i++)
            if (!SimulatorVector(in a, i).Equals(SimulatorVector(in b, i)) ||
                !CollisionVector(in a, i).Equals(CollisionVector(in b, i)) ||
                a.BonePhysicsResourceHandles[i].Value != b.BonePhysicsResourceHandles[i].Value) return false;
        return true;
    }

    private static bool IsPhybName(ReadOnlySpan<byte> value) => value.Length >= 5 && value[^5] == (byte)'.' &&
        (value[^4] | 0x20) == 'p' && (value[^3] | 0x20) == 'h' &&
        (value[^2] | 0x20) == 'y' && (value[^1] | 0x20) == 'b';
    private static bool IsBodyProbeBone(string name) => name is "n_root" or "n_hara" or "j_kosi" or
        "j_sebo_a" or "j_sebo_b" or "j_sebo_c" or "j_asi_a_l" or "j_asi_b_l" or "j_asi_c_l" or
        "j_asi_d_l" or "j_asi_a_r" or "j_asi_b_r" or "j_asi_c_r" or "j_asi_d_r";
    private static string Address(nint value) => $"0x{(ulong)value:X}";
    private static float? Finite(float value) => float.IsFinite(value) ? value : null;
    private static float[]? Vector(float x, float y, float z) =>
        float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z) ? new[] { x, y, z } : null;
    private static float[]? Rotation(in hkQsTransformf t) => float.IsFinite(t.Rotation.X) &&
        float.IsFinite(t.Rotation.Y) && float.IsFinite(t.Rotation.Z) && float.IsFinite(t.Rotation.W)
        ? new[] { t.Rotation.X, t.Rotation.Y, t.Rotation.Z, t.Rotation.W } : null;
    private static float[]? Position(in hkQsTransformf t) => Vector(t.Translation.X, t.Translation.Y, t.Translation.Z);
    private static float[]? Scale(in hkQsTransformf t) => Vector(t.Scale.X, t.Scale.Y, t.Scale.Z);
    private static bool TransformFinite(in hkQsTransformf t) =>
        float.IsFinite(t.Rotation.X) && float.IsFinite(t.Rotation.Y) && float.IsFinite(t.Rotation.Z) &&
        float.IsFinite(t.Rotation.W) && float.IsFinite(t.Translation.X) && float.IsFinite(t.Translation.Y) &&
        float.IsFinite(t.Translation.Z) && float.IsFinite(t.Scale.X) && float.IsFinite(t.Scale.Y) && float.IsFinite(t.Scale.Z);

    private enum ReadStatus
    {
        NotObserved, Observed, ObservedWithUnavailableFields, Absent, NoPhysicsModule, Unreadable, TargetChanged, PoseChangedOrUnreadable,
        ModuleChangedSincePlan, ResourcesChangedSincePlan, ModuleChangedDuringRead, ResourceChangedDuringRead,
        ModuleSkeletonMismatch, ObjectChangedDuringRead, InvalidVector, InvalidString, ResourceTypeUnconfirmed,
        InvalidDataRange, UnknownShape, NonFiniteValues,
    }

    private readonly record struct VectorHeader(nint First, nint Last, nint End);
    private sealed class PosePlan
    {
        public nint PoseAddress, SkeletonAddress, BoneNames, Local, Model, Flags;
        public int PartialIndex, NativeBoneCount, BoneStart, BoneCount, UnreadableBoneNameCount;
    }
    private sealed class BonePlan { public int Index, PartialIndex; public string Name = ""; public bool IsSkirt; }
    private enum SimulationStatus
    {
        NotObserved, AwaitingMatchingAfter, AwaitingMatchingAfter_MismatchedCallbackSeen,
        PairIdentityChanged, PairCaptureFailed, Paired,
    }
    private sealed class SimulationObservation
    {
        public SimulationStatus Status;
        public long Token, TimestampTicks, AfterTimestampTicks;
        public nint Simulator, Module;
        public string Method = "";
        public int ThreadId, ResourceIndex;
        public bool BeforeCaptured, AfterCaptured, BeforeValid, AfterValid;
        public bool? OriginalCompleted;
        public void Reset()
        {
            Status = SimulationStatus.NotObserved; Token = TimestampTicks = AfterTimestampTicks = 0; Simulator = Module = 0;
            Method = ""; ThreadId = 0; ResourceIndex = -1;
            BeforeCaptured = AfterCaptured = BeforeValid = AfterValid = false;
            OriginalCompleted = null;
        }
    }
    private struct BoneCapture
    {
        public ReadStatus Status;
        public nint PoseAddress, SkeletonAddress;
        public byte LocalInSync, ModelInSync;
        public uint Flags;
        public hkQsTransformf Local, Model;
    }
    private struct SimulatorCapture
    {
        public ReadStatus Status, ConstraintStatus;
        public nint Address;
        public int ConstraintCount;
        public BoneSimulator Simulator;
    }
    private struct CollisionCapture { public ReadStatus Status; public nint Address; public byte Shape; }
    private sealed class ResourceBuffer
    {
        public ReadStatus Status, FileNameStatus, PayloadStatus;
        public nint Address, DataAddress;
        public ResourceHandle Header;
        public ulong DataLength;
        public readonly byte[] FileName = new byte[MaximumFileName];
        public readonly byte[] ConfirmFileName = new byte[MaximumFileName];
        public int FileNameLength;
        public void Reset()
        {
            Status = FileNameStatus = PayloadStatus = ReadStatus.NotObserved;
            Address = DataAddress = 0; Header = default; DataLength = 0; FileNameLength = 0;
        }
    }
    private sealed class GroupBuffer
    {
        public ReadStatus SimulatorStatus, CollisionStatus;
        public int SimulatorCount, CollisionCount;
        public readonly SimulatorCapture[] Simulators = new SimulatorCapture[MaximumVectorCount];
        public readonly CollisionCapture[] Collisions = new CollisionCapture[MaximumVectorCount];
        public readonly ResourceBuffer Resource = new();
        public void Reset()
        {
            SimulatorStatus = CollisionStatus = ReadStatus.NotObserved; SimulatorCount = CollisionCount = 0;
            Array.Clear(Simulators); Array.Clear(Collisions); Resource.Reset();
        }
    }
    private sealed class StageBuffer
    {
        public bool Captured, UseOverrideSimulationTime, ModuleFieldsAvailable, PoseFieldsUnavailable;
        public ReadStatus Status;
        public long TimestampTicks;
        public nint ModuleAddress, ModuleSkeleton;
        public float FrameDeltaTime, OverrideSimulationTime;
        public readonly BoneCapture[] Bones = new BoneCapture[MaximumSkirtBones];
        public readonly GroupBuffer[] Groups = { new(), new(), new(), new(), new() };
        public void Reset()
        {
            Captured = UseOverrideSimulationTime = ModuleFieldsAvailable = PoseFieldsUnavailable = false; Status = ReadStatus.NotObserved;
            TimestampTicks = 0; ModuleAddress = ModuleSkeleton = 0; FrameDeltaTime = OverrideSimulationTime = 0;
            Array.Clear(Bones); foreach (var group in Groups) group.Reset();
        }
    }
}
