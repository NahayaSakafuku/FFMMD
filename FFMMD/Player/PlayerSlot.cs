using System.Diagnostics;
using System.Threading;
using Dalamud.Game;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using GameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using BoneApplier = FFMMD.Posing.BoneApplier;

namespace FFMMD.Player;

/// <summary>
/// 单角色播放槽:一个目标、一条时间轴、独立的骨架缓存(Retargeter)与诊断状态。
/// 时间只在协调器 Tick 推进(dt 由协调器计算);骨骼写入只发生在 BoneApplier 的
/// UpdateBonePhysics post-hook 里(读当前时钟,幂等)。
/// 槽 0(主槽)读写 Config 中的目标设置,其余槽为会话内内存态。
/// </summary>
public sealed unsafe class PlayerSlot
{
    public int Index;

    public Vmd.VmdAnimation? Anim;
    public string? LoadedPath;
    public string? LoadError;

    public double TimeSec;
    public bool Playing;
    public bool Paused;

    /// <summary> transport 状态变化通知;由协调器转发(主槽事件送给音乐服务)。 </summary>
    public event Action<TransportEvent>? TransportChanged;

    private void Fire(TransportAction action, string? path = null, bool flag = false)
        => TransportChanged?.Invoke(new TransportEvent(action, TimeSec, path, flag, P.Config.Speed));

    public Retarget.Retargeter Retargeter { get; } = new();

    /// <summary> 文件对话框回调线程 → Tick 线程 的交接点。 </summary>
    internal string? PendingLoadPath;
    internal string? PendingPmxPath;
    public string? SkirtBakePath, SkirtBakeStatus;
    private string? _motionSha256;
    private byte[]? _motionBytes;
    private readonly Skirt.SkirtPreprocessor _skirtPreprocessor;
    private readonly string _skirtLibraryPath;
    private Skirt.SkirtBakeLease? _skirtLease;
    private long _motionGeneration, _skirtRequestedMotionGeneration;
    private string? _skirtRequestedReferencePath;
    private bool _skirtRequestedAutomatic, _skirtSettingsObserved, _skirtLeaseSettled;
    private bool _skirtPlayRequested, _skirtBodyOnly, _detached;
    public bool SkirtPreprocessing => _skirtLease is { } lease && !lease.Snapshot.IsCompleted;
    public float SkirtBakeProgress => _skirtLease?.Snapshot.Progress ?? 0;
    internal bool HasPendingPlay => _skirtPlayRequested;
    private Skirt.SkirtBakeCache.Loaded? _offlineSkirtCache;
    private Skirt.SkirtBakeBinding? _offlineSkirtBinding;
    private Posing.SkeletonTree? _offlineSkirtAttemptedTree;
    private bool _offlineSkirtPrepared;
    public string? SourcePmxPath, SourceError, DiagnosticStatus;
    public string? ScaleAuditStatus;
    private Retarget.RigDiagnosticReport? _diagnostic;
    private bool _diagnosticRequested;
    private int _diagnosticReady;
    private long _diagnosticDeadline;
    private ulong _diagnosticTargetId;
    private nint _diagnosticPoseAddress, _diagnosticCharacterAddress, _diagnosticHookArgument;
    private bool _diagnosticBeforeCaptured, _diagnosticBodyCaptured;
    private long _poseGeneration, _diagnosticPoseGeneration;
    private Posing.SkeletonTree? _diagnosticTree;
    private Vmd.VmdAnimation? _diagnosticAnimation;
    private readonly Skirt.NativeBonePhysicsInspector _nativePhysics = new();
    private readonly object _poseLock = new();
    private float _preparedFrame;
    private readonly Posing.PartialPoseBridge _partials = new();

    private readonly BoneApplier? _applier;
    private readonly Posing.NativeClothingObserver? _clothingObserver;
    internal bool NeedsClothingObservation => Volatile.Read(ref _diagnostic) != null &&
        Volatile.Read(ref _diagnosticReady) == 0;
    private volatile bool _poseActive;
    private volatile bool _prepared;
    private int _targetIndex = -1;
    private ulong _targetId;
    // GPose actor range used by Brio/Game/Actor/ActorTableHelpers.cs.
    private const int GposeStart = 201, GposeEnd = 439;
    private bool _wasGposing;
    private bool _wasEnvironment;
    private long _lastDiagMs = -1;
    private string? _lastDiag;

    /// <summary> 目标游戏对象的地址(每帧从它重新解析 CharacterBase,避免缓存指针失效)。 </summary>
    public nint TargetGameObjectAddress;
    public nint TargetCharaBase;
    public string TargetName = "";
    public bool TargetValid;

    /// <summary> 播放目标模式:0 = 自己,1 = 当前目标,2 = 附近角色。槽 0 读写全局配置,其余槽为内存态。 </summary>
    public int TargetMode
    {
        get => Index == 0 ? P.Config.TargetMode : _targetMode;
        set { if (Index == 0) { P.Config.TargetMode = value; P.ConfigDirty = true; } else _targetMode = value; }
    }
    private int _targetMode;

    public ulong NearbyObjectId
    {
        get => Index == 0 ? P.Config.NearbyObjectId : _nearbyObjectId;
        set { if (Index == 0) { P.Config.NearbyObjectId = value; P.ConfigDirty = true; } else _nearbyObjectId = value; }
    }
    private ulong _nearbyObjectId;

    public PlayerSlot(BoneApplier? applier, int index, Skirt.SkirtPreprocessor skirtPreprocessor,
        string skirtLibraryPath, Posing.NativeClothingObserver? clothingObserver = null)
    {
        _applier = applier;
        _skirtPreprocessor = skirtPreprocessor;
        _skirtLibraryPath = skirtLibraryPath;
        _clothingObserver = clothingObserver;
        Index = index;
    }

    public void Load(string path)
    {
        lock (_poseLock) LoadLocked(path);
    }

    private void LoadLocked(string path)
    {
        if (_detached) return;
        try
        {
            var motionBytes = File.ReadAllBytes(path);
            var file = Vmd.VmdFile.Parse(motionBytes);
            var anim = Vmd.VmdAnimation.Build(file);
            if (anim.Tracks.Count == 0) throw new InvalidDataException(Loc.S.LoadNoBoneTracks);
            var motionSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(motionBytes));
            ReleaseSkirtLease();
            _motionGeneration++;
            Interlocked.Increment(ref _poseGeneration);
            Anim = anim;
            LoadedPath = path;
            _motionSha256 = motionSha256;
            _motionBytes = motionBytes;
            ResetSkirtCache();
            _skirtPlayRequested = false;
            _skirtBodyOnly = false;
            _skirtSettingsObserved = false;
            LoadError = null;
            TimeSec = 0;
            Playing = false;
            Paused = false;
            _poseActive = _prepared = false;
            // 目标骨架可能已缓存,重算映射与基线。
            Retargeter.ResetCache();
            Retargeter.SetSourceRig(null);
            SourcePmxPath = null; SourceError = null;
            // 按动作恢复源 PMX 关联(多槽下每个动作各自记忆)。
            if (P.Config.SourcePmxByMotion.TryGetValue(path, out var pmx) && File.Exists(pmx))
                LoadSourcePmx(pmx);
            P.Config.LastVmdPath = path;
            P.ConfigDirty = true;
            PluginLog.Information($"[FFMMD] 槽{Index + 1} 已加载 {Path.GetFileName(path)}:{anim.Tracks.Count} 条骨骼轨道 / {anim.MaxFrame} 帧 / 模型 {anim.ModelName}");
            Fire(TransportAction.Loaded, path: path);
            RequestSkirtPreprocessing();
        }
        catch (Exception e)
        {
            LoadError = e.Message;
            PluginLog.Error($"[FFMMD] VMD 加载失败: {e}");
        }
    }

    public void Play()
    {
        lock (_poseLock)
        {
            if (_detached || Anim == null) return;
            if (!Svc.ClientState.IsGPosing) { LoadError = Loc.S.LoadEnterGpose; return; }
            UpdateSkirtPreprocessing();
            LoadError = null;
            if (SkirtPreprocessing || !CanStartSkirtPlayback)
            {
                _skirtPlayRequested = true;
                Paused = false;
                return;
            }
            if (!Playing || Paused) StartPlayback();
        }
    }

    private void StartPlayback()
    {
        if (_detached || Anim == null) return;
        _skirtPlayRequested = false;
        LoadError = null;
        Playing = true; Paused = false; _poseActive = true;
        if (TimeSec >= Anim.DurationSec) TimeSec = 0;
        Fire(TransportAction.Play, flag: !Svc.ClientState.IsGPosing || !TargetValid);
    }

    private bool CanStartSkirtPlayback => !P.Config.AutoSkirtPhysics || _skirtBodyOnly || _offlineSkirtCache != null;

    public void Pause() => SetPaused(!Paused);

    /// <summary> UI/诊断统一走这里切换暂停,保证 transport 通知不漏发。 </summary>
    public void SetPaused(bool paused)
    {
        lock (_poseLock)
        {
            if (Paused == paused) return;
            Paused = paused;
            if (paused) _skirtPlayRequested = false;
            // Waiting for preparation never starts music through PauseChanged.
            if (Playing) Fire(TransportAction.PauseChanged, flag: paused);
        }
    }

    public void Stop()
    {
        lock (_poseLock)
        {
            _skirtPlayRequested = false;
            Interlocked.Increment(ref _poseGeneration);
            Playing = false; Paused = false; TimeSec = 0;
            _poseActive = _prepared = _offlineSkirtPrepared = false;
            Fire(TransportAction.Stop);
        }
    }

    public void Seek(double time)
    {
        lock (_poseLock)
        {
            if (_detached || Anim == null || !double.IsFinite(time)) return;
            Interlocked.Increment(ref _poseGeneration);
            TimeSec = Math.Clamp(time, 0, Anim.DurationSec);
            _poseActive = true; _prepared = _offlineSkirtPrepared = false;
            Fire(TransportAction.Seek);
        }
    }

    public void LoadSourcePmx(string path)
    {
        Stop();
        try
        {
            var rig = Retarget.PmxRigReader.Parse(path);
            foreach (var name in new[] { "下半身", "左足", "左ひざ", "左足首", "右足", "右ひざ", "右足首" })
                if (rig.Find(name) < 0) throw new InvalidDataException(string.Format(Loc.S.SourcePmxMissing, name));
            Retargeter.SetSourceRig(rig); SourcePmxPath = path; SourceError = null;
            if (LoadedPath is { } motion) P.Config.SourcePmxByMotion[motion] = path;
            P.Config.SourcePmxPath = path; P.Config.SourcePmxMotionPath = LoadedPath; P.ConfigDirty = true;
        }
        catch (Exception e) { SourceError = e.Message; PluginLog.Error($"[FFMMD] 源 PMX 导入失败:{e.Message}"); }
    }

    public void UseStandardSource()
    {
        Stop();
        lock (_poseLock)
        {
            Retargeter.SetSourceRig(null); SourcePmxPath = null; SourceError = null;
            if (LoadedPath is { } motion) P.Config.SourcePmxByMotion.Remove(motion);
            P.Config.SourcePmxPath = P.Config.SourcePmxMotionPath = null; P.ConfigDirty = true;
        }
    }

    public void RetrySkirtBake()
    {
        lock (_poseLock)
        {
            if (_detached || !P.Config.AutoSkirtPhysics || SkirtPreprocessing) return;
            RequestSkirtPreprocessing();
        }
    }

    public void CancelSkirtBake()
    {
        lock (_poseLock)
        {
            var resume = _skirtPlayRequested;
            _skirtPlayRequested = false;
            ReleaseSkirtLease(); ResetSkirtCache();
            _skirtBodyOnly = true;
            _skirtSettingsObserved = true; _skirtRequestedAutomatic = P.Config.AutoSkirtPhysics;
            _skirtRequestedReferencePath = ConfiguredSkirtReference();
            SkirtBakeStatus = Loc.IsChinese ? "裙骨物理已取消；仅播放身体。" : "Skirt physics cancelled; body only.";
            if (resume && Svc.ClientState.IsGPosing) StartPlayback();
        }
    }

    private void ReleaseSkirtLease()
    {
        var lease = _skirtLease; _skirtLease = null;
        lease?.Dispose();
        _skirtLeaseSettled = false;
    }

    private void ResetSkirtCache()
    {
        Interlocked.Increment(ref _poseGeneration);
        _offlineSkirtCache = null; _offlineSkirtBinding = null; _offlineSkirtAttemptedTree = null;
        _offlineSkirtPrepared = false; SkirtBakePath = null; SkirtBakeStatus = null; _prepared = false;
    }

    private static string? ConfiguredSkirtReference() =>
        string.IsNullOrWhiteSpace(P.Config.SkirtPhysicsPmxPath) ? null : P.Config.SkirtPhysicsPmxPath;

    private void RequestSkirtPreprocessing()
    {
        var resume = _skirtPlayRequested || Playing && !Paused;
        ReleaseSkirtLease(); ResetSkirtCache();
        _skirtBodyOnly = false;
        _skirtRequestedAutomatic = P.Config.AutoSkirtPhysics;
        _skirtRequestedReferencePath = ConfiguredSkirtReference();
        _skirtRequestedMotionGeneration = _motionGeneration;
        _skirtSettingsObserved = true;
        if (_detached || Anim == null || LoadedPath == null || _motionBytes == null || _motionSha256 == null) return;
        if (!_skirtRequestedAutomatic)
        {
            SkirtBakeStatus = Loc.IsChinese ? "自动裙骨物理已关闭；仅播放身体。" : "Automatic skirt physics disabled; body only.";
            if (resume && Svc.ClientState.IsGPosing) StartPlayback();
            return;
        }
        _skirtLease = _skirtPreprocessor.Request(new Skirt.SkirtBakeRequest
        {
            MotionPath = LoadedPath, MotionBytes = _motionBytes, ExpectedMotionSha256 = _motionSha256,
            ReferencePmxPath = _skirtRequestedReferencePath, LibraryPath = _skirtLibraryPath,
            FrameCount = Anim.MaxFrame >= int.MaxValue ? int.MaxValue : (int)Anim.MaxFrame + 1,
        });
        _skirtPlayRequested = resume;
        if (Playing)
        {
            Playing = false; Paused = false; _poseActive = false;
            Fire(TransportAction.TargetSuspendChanged, flag: true);
            _wasEnvironment = false;
        }
        SkirtBakeStatus = Loc.IsChinese ? "正在准备裙骨物理。" : "Preparing skirt physics.";
    }

    // Tick/UI inspect immutable background results. This never reads a file or
    // simulates physics and has no continuation that can write a native pose.
    private void UpdateSkirtPreprocessing()
    {
        if (_detached || Anim == null) return;
        if (!_skirtSettingsObserved || _skirtRequestedAutomatic != P.Config.AutoSkirtPhysics ||
            !string.Equals(_skirtRequestedReferencePath, ConfiguredSkirtReference(), StringComparison.OrdinalIgnoreCase))
            RequestSkirtPreprocessing();
        var lease = _skirtLease;
        if (lease != null && !_skirtLeaseSettled)
        {
            var snapshot = lease.Snapshot;
            SkirtBakeStatus = snapshot.Message ?? snapshot.Status;
            if (snapshot.IsCompleted)
            {
                _skirtLeaseSettled = true;
                if (snapshot.Status == "Ready" && snapshot.Cache is { } cache)
                {
                    if (_skirtRequestedMotionGeneration != _motionGeneration || !P.Config.AutoSkirtPhysics ||
                        cache.FrameCount != Anim.MaxFrame + 1 ||
                        !string.Equals(cache.MotionSha256, _motionSha256, StringComparison.OrdinalIgnoreCase) ||
                        cache.Document.Solver.RecipeVersion != Skirt.SkirtBakeRecipe.Default.Version)
                    {
                        SkirtBakeStatus = Loc.IsChinese ? "缓存与当前动作或设置不一致，未应用。" : "Cache does not match the current motion or settings; not applied.";
                    }
                    else
                    {
                        Interlocked.Increment(ref _poseGeneration);
                        _offlineSkirtCache = cache; SkirtBakePath = snapshot.CachePath;
                        _offlineSkirtBinding = null; _offlineSkirtAttemptedTree = null; _offlineSkirtPrepared = false; _prepared = false;
                        if (LoadedPath != null && snapshot.CachePath is { } cachePath)
                        { P.Config.SkirtBakeByMotion[LoadedPath] = cachePath; P.ConfigDirty = true; }
                    }
                }
            }
        }
        // Failure leaves the pending intent visible. Retry can satisfy it;
        // CancelSkirtBake is the explicit user choice to play the body only.
        if (_skirtPlayRequested && !SkirtPreprocessing && CanStartSkirtPlayback && Svc.ClientState.IsGPosing) StartPlayback();
    }

    private void PrepareOfflineSkirt()
    {
        _offlineSkirtPrepared = false;
        if (_offlineSkirtCache == null || Retargeter.Tree == null || Retargeter.Profile == null) return;
        if (P.Config.Cal.MotionScale != 1 || !P.Config.Cal.AutoPositionScale || P.Config.Cal.LegIkMode != 2)
        {
            SkirtBakeStatus = "Cache requires full motion amplitude, automatic position scale and VMD IK mode.";
            return;
        }
        if (!ReferenceEquals(_offlineSkirtAttemptedTree, Retargeter.Tree))
        {
            _offlineSkirtAttemptedTree = Retargeter.Tree; _offlineSkirtBinding = null;
            try { _offlineSkirtBinding = Skirt.SkirtBakeBinding.Build(_offlineSkirtCache, Retargeter.Profile); }
            catch (InvalidDataException e) { SkirtBakeStatus = e.Message; }
        }
        if (_offlineSkirtBinding != null)
        {
            _offlineSkirtPrepared = _offlineSkirtBinding.Prepare(_preparedFrame, P.Config.Cal.MotionScale);
            SkirtBakeStatus = $"{_offlineSkirtCache.FrameCount} frames / 18 bones / {_offlineSkirtCache.Document.Solver.Engine}";
        }
    }

    public void RequestDiagnostics()
    {
        lock (_poseLock) RequestDiagnosticsLocked();
    }

    private void RequestDiagnosticsLocked()
    {
        if (Anim == null || !Svc.ClientState.IsGPosing || !TargetValid || _applier?.Available != true)
        { DiagnosticStatus = Loc.S.DiagNeedGpose; return; }
        SetPaused(true); Seek(TimeSec); _diagnosticRequested = true; _diagnostic = null;
        _nativePhysics.Invalidate("New diagnostic request");
        _diagnosticBeforeCaptured = _diagnosticBodyCaptured = false;
        ScaleAuditStatus = null;
        Volatile.Write(ref _diagnosticReady, 0); _diagnosticDeadline = Environment.TickCount64 + 3000;
        DiagnosticStatus = Loc.S.DiagWaiting;
    }

    /// <summary> 协调器每帧调用;dt 为协调器计算的 Stopwatch 秒,避免逐槽各自计时。 </summary>
    internal void Tick(double dt, bool gposing)
    {
        lock (_poseLock) TickLocked(dt, gposing);
    }

    private void TickLocked(double dt, bool gposing)
    {
        _prepared = false;
        _offlineSkirtPrepared = false;

        if (!gposing && _wasGposing) Stop();
        _wasGposing = gposing;

        var pending = Interlocked.Exchange(ref PendingLoadPath, null);
        if (pending != null)
        {
            Load(pending);
        }
        var pmxPending = Interlocked.Exchange(ref PendingPmxPath, null);
        if (pmxPending != null) LoadSourcePmx(pmxPending);

        ResolveTarget();

        UpdateSkirtPreprocessing();

        if (Playing && !Paused && Anim != null && gposing && TargetValid)
        {
            TimeSec += dt * P.Config.Speed;
            var dur = Anim.DurationSec;
            if (dur > 0 && TimeSec >= dur)
            {
                if (P.Config.Loop)
                {
                    TimeSec %= dur;
                    Fire(TransportAction.LoopWrap);
                }
                else
                {
                    TimeSec = dur;
                    Playing = false;
                    Fire(TransportAction.EndReached);
                }
            }
        }

        // 时钟冻结环境(有动画 + GPose + 目标有效)的边沿:失效/恢复只通知一次,
        // 用户暂停/恢复走 PauseChanged,两者语义分开。
        var environment = Anim != null && gposing && TargetValid && !(_skirtPlayRequested && !CanStartSkirtPlayback);
        if (environment != _wasEnvironment)
        {
            _wasEnvironment = environment;
            Fire(TransportAction.TargetSuspendChanged, flag: !environment);
        }

        // Build caches and solve outside the native hook; the hook only copies prepared buffers.
        if (_poseActive && gposing && TargetValid && Anim != null && TryGetLivePose(out var pose))
        {
            try
            {
                if (Retargeter.EnsureSkeleton(pose, Anim, P.Config.Cal))
                {
                    _partials.Prepare(((CharacterBase*)TargetCharaBase)->Skeleton, Retargeter.Tree!);
                    _preparedFrame = (float)(TimeSec * Vmd.VmdAnimation.FramesPerSecond);
                    _prepared = Retargeter.Evaluate(_preparedFrame, P.Config.Cal);
                    if (_prepared) PrepareOfflineSkirt();
                    if (_prepared && _diagnosticRequested) PrepareDiagnostics(pose);
                }
            }
            catch (Exception e) { Diag($"槽{Index + 1} 骨架准备失败: {e.Message}"); }
        }
        if (_diagnostic != null &&
            (!_poseActive || !_prepared || !TryGetLivePose(out var diagnosticPose) || !DiagnosticMatches(diagnosticPose)))
        {
            _diagnostic.PhysicsObservationNote = "Target, pose, motion, frame or calibration changed during observation; stages must not be combined as one physics result.";
            _diagnostic.FinalStageNote = _diagnostic.PhysicsObservationNote;
            _nativePhysics.Invalidate("Diagnostic target or preparation changed");
            Volatile.Write(ref _diagnosticReady, 1);
        }
        FlushDiagnostics();
    }

    // Allocate report/inspector buffers on Tick, never in the native pre-hook.
    private void PrepareDiagnostics(FFXIVClientStructs.Havok.Animation.Rig.hkaPose* pose)
    {
        _diagnostic = Retarget.RigDiagnostics.Create(Retargeter, P.Config.Cal, _preparedFrame);
        _diagnostic.MotionPath = LoadedPath ?? "";
        _diagnostic.TargetName = TargetName;
        _diagnostic.FinalStageAvailable = _applier?.FinalStageAvailable == true;
        _diagnosticTargetId = _targetId;
        _diagnosticPoseGeneration = _poseGeneration;
        _diagnostic.PhysicsPreparedGeneration = _poseGeneration;
        _diagnosticTree = Retargeter.Tree;
        _diagnosticAnimation = Anim;
        _diagnostic.OfflineSkirtCachePath = SkirtBakePath;
        _diagnostic.OfflineSkirtCacheApplied = _offlineSkirtPrepared;
        _diagnostic.OfflineSkirtCacheStatus = SkirtBakeStatus;
        _diagnostic.OfflineSkirtReferenceSha256 = _offlineSkirtCache?.ReferencePmxSha256;
        _diagnostic.OfflineSkirtSolver = _offlineSkirtCache?.Document.Solver;
        if (_offlineSkirtPrepared && _offlineSkirtBinding != null)
        {
            _diagnostic.OfflineSkirtTargetBones = _offlineSkirtCache!.Bones.Select(b => b.TargetName).ToArray();
            _diagnostic.OfflineSkirtPreparedLocalRotations = (Quaternion[])_offlineSkirtBinding.LocalRotations.Clone();
        }
        _diagnosticPoseAddress = (nint)pose;
        _diagnosticCharacterAddress = TargetCharaBase;
        _diagnosticHookArgument = 0;
        _diagnosticBeforeCaptured = _diagnosticBodyCaptured = false;
        var cb = (CharacterBase*)TargetCharaBase;
        _nativePhysics.ConfigureTarget(cb, cb->Skeleton, pose);
        _diagnostic.NativeClothingObserverStatus = _clothingObserver?.Status ?? "Unavailable";
        _diagnostic.NativeClothingObserverError = _clothingObserver?.Error;
        _diagnostic.PhybProfiles = ReadLoadedPhybProfiles();
        _diagnosticRequested = false;
    }

    // Loaded resource bytes are copied and parsed only on Tick under the slot lock.
    private Skirt.PhybProfileObservation[] ReadLoadedPhybProfiles()
    {
        var observations = new Skirt.PhybProfileObservation[5];
        for (var i = 0; i < observations.Length; i++)
        {
            var copied = _nativePhysics.TryCopyPlannedResourcePayload(i, Skirt.PhybProfileReader.MaxFileBytes,
                out var payload, out var status);
            var profile = copied ? Skirt.PhybProfileReader.Read(payload, $"Loaded native resource slot {i}") : null;
            var declaredBones = profile?.Parsed == true ? profile.Simulators.Where(sim => sim.Params.IsClothing)
                .SelectMany(sim => sim.Chains).SelectMany(chain => chain.Nodes).Select(node => node.BoneName)
                .Where(name => name.StartsWith("j_sk_", StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).ToArray() : [];
            var targetNames = Retargeter.Tree?.Names ?? [];
            observations[i] = new() { ResourceIndex = i, CopyStatus = status, PayloadCopied = copied, Profile = profile,
                DeclaredClothingSkirtBones = declaredBones,
                PresentOnTargetSkeleton = declaredBones.Where(name => Array.IndexOf(targetNames, name) >= 0).ToArray(),
                MissingFromTargetSkeleton = declaredBones.Where(name => Array.IndexOf(targetNames, name) < 0).ToArray() };
        }
        return observations;
    }

    internal void ObserveClothingSimulation(Posing.NativeSimulationCallData call)
    {
        if (Volatile.Read(ref _diagnostic) == null || !Monitor.TryEnter(_poseLock)) return;
        try
        {
            // Worker callbacks do not traverse the object table or dereference live pose
            // pointers. The inspector revalidates target/module/pose identities by RPM.
            if (_diagnostic == null || Environment.TickCount64 > _diagnosticDeadline || !_diagnosticBeforeCaptured ||
                call.Before && (_diagnosticBodyCaptured || Volatile.Read(ref _diagnosticReady) != 0) ||
                !call.Before && !_nativePhysics.HasPendingClothingObservation ||
                !_poseActive || !_prepared || !_wasGposing ||
                _diagnosticPoseGeneration != _poseGeneration || _diagnosticTargetId != _targetId ||
                _diagnosticCharacterAddress != TargetCharaBase || _diagnostic.Frame != _preparedFrame ||
                !ReferenceEquals(_diagnosticTree, Retargeter.Tree) || !ReferenceEquals(_diagnosticAnimation, Anim) ||
                !SameDiagnosticCalibration(_diagnostic.Calibration, P.Config.Cal)) return;
            _nativePhysics.ObserveSimulationCall(call.Token, call.Simulator, call.Module, call.Method,
                call.Before, call.ManagedThreadId, call.TimestampTicks, call.OriginalCompleted);
        }
        finally { Monitor.Exit(_poseLock); }
    }

    private bool DiagnosticMatches(FFXIVClientStructs.Havok.Animation.Rig.hkaPose* pose) =>
        _diagnostic != null && _poseActive && _prepared && Svc.ClientState.IsGPosing &&
        ReferenceEquals(_diagnosticTree, Retargeter.Tree) && ReferenceEquals(_diagnosticAnimation, Anim) &&
        _diagnosticPoseGeneration == _poseGeneration && _diagnosticTargetId == _targetId &&
        _diagnosticCharacterAddress == TargetCharaBase && _diagnosticPoseAddress == (nint)pose &&
        _diagnostic.Frame == _preparedFrame && _diagnostic.MotionPath == (LoadedPath ?? "") &&
        _diagnostic.SourceFingerprint == Retargeter.SourceRig?.Fingerprint &&
        SameDiagnosticCalibration(_diagnostic.Calibration, P.Config.Cal) && Retargeter.Matches(pose);

    private static bool SameDiagnosticCalibration(Calibration captured, Calibration current) =>
        captured.SourceRestPose == current.SourceRestPose && captured.MotionScale == current.MotionScale &&
        captured.YawDegrees == current.YawDegrees && captured.AutoPositionScale == current.AutoPositionScale &&
        captured.ManualPositionScale == current.ManualPositionScale && captured.HeightOffset == current.HeightOffset &&
        captured.LegIkMode == current.LegIkMode;

    private void CaptureNativePhysics(Skirt.NativeBonePhysicsStage stage)
    {
        // Optional observation must never prevent the prepared body pose write.
        try { _nativePhysics.Capture(stage); }
        catch (Exception e)
        {
            if (_diagnostic != null) _diagnostic.PhysicsObservationNote = $"Native observation failed: {e.Message}";
        }
    }

    internal void CaptureBeforeGamePhysics(long invocation, nint argument)
    {
        if (_diagnostic == null || _diagnosticBeforeCaptured || Volatile.Read(ref _diagnosticReady) != 0) return;
        if (!Monitor.TryEnter(_poseLock)) return;
        try
        {
            if (_diagnostic == null || _diagnosticBeforeCaptured || !_poseActive || !_prepared ||
                !Svc.ClientState.IsGPosing || !TryGetLivePose(out var pose) || !DiagnosticMatches(pose)) return;
            _diagnostic.PhysicsHookInvocation = invocation;
            _diagnosticHookArgument = argument;
            CaptureNativePhysics(Skirt.NativeBonePhysicsStage.BeforeGamePhysics);
            _diagnosticBeforeCaptured = true;
        }
        finally { Monitor.Exit(_poseLock); }
    }

    public void ResolveTarget()
    {
        IGameObject? obj = null;
        switch (TargetMode)
        {
            case 0:
                obj = Svc.Objects.LocalPlayer;
                if (Svc.ClientState.IsGPosing)
                {
                    var selfName = obj?.Name.TextValue;
                    obj = null;
                    if (selfName != null)
                    {
                        var selected = Svc.Targets.GPoseTarget;
                        if (IsGposeActor(selected) && selected!.Name.TextValue == selfName) obj = selected;
                        else
                        {
                            for (var i = GposeStart; i <= GposeEnd && i < Svc.Objects.Length; i++)
                            {
                                var candidate = Svc.Objects[i];
                                if (IsGposeActor(candidate) && candidate!.Name.TextValue == selfName) { obj = candidate; break; }
                            }
                        }
                    }
                }
                break;
            case 1:
                obj = Svc.ClientState.IsGPosing ? Svc.Targets.GPoseTarget : Svc.Targets.Target;
                break;
            case 2:
                if (NearbyObjectId != 0)
                    foreach (var o in Svc.Objects)
                        if (o.GameObjectId == NearbyObjectId || o.EntityId == NearbyObjectId)
                        {
                            obj = o;
                            break;
                        }
                break;
        }

        if (obj is not ICharacter || !obj.IsValid() || obj.Address == nint.Zero ||
            Svc.ClientState.IsGPosing && !IsGposeActor(obj))
        {
            ClearTarget();
            return;
        }

        var go = (GameObject*)obj.Address;
        var cb = go->GetCharacterBase();
        if (cb == null || cb->GetModelType() != CharacterBase.ModelType.Human)
        {
            ClearTarget();
            return;
        }
        if (TargetGameObjectAddress != obj.Address || TargetCharaBase != (nint)cb || _targetId != obj.GameObjectId)
            Retargeter.ResetCache();
        TargetGameObjectAddress = obj.Address;
        TargetCharaBase = (nint)cb;
        _targetIndex = obj.ObjectIndex;
        _targetId = obj.GameObjectId;
        TargetName = obj.Name.TextValue;
        TargetValid = cb != null;
    }

    private void ClearTarget()
    {
        _partials.Clear();
        if (TargetValid || TargetGameObjectAddress != 0) Retargeter.ResetCache();
        TargetValid = _prepared = false;
        TargetGameObjectAddress = TargetCharaBase = 0;
        _targetIndex = -1; _targetId = 0; TargetName = "";
    }

    internal static bool IsGposeActor(IGameObject? obj) => obj is ICharacter &&
        obj.ObjectIndex is >= GposeStart and <= GposeEnd && obj.IsValid();

    private bool TryGetLivePose(out FFXIVClientStructs.Havok.Animation.Rig.hkaPose* pose)
    {
        pose = null;
        if (!TargetValid || _targetIndex < 0 || _targetIndex >= Svc.Objects.Length) return false;
        if (Svc.ClientState.IsGPosing && _targetIndex is < GposeStart or > GposeEnd) return false;
        var live = Svc.Objects[_targetIndex];
        if (live is not ICharacter || !live.IsValid() || live.Address != TargetGameObjectAddress || live.GameObjectId != _targetId)
        { ClearTarget(); return false; }
        var cb = ((GameObject*)live.Address)->GetCharacterBase();
        if (cb == null || (nint)cb != TargetCharaBase || cb->GetModelType() != CharacterBase.ModelType.Human)
        { ClearTarget(); return false; }
        var skel = cb->Skeleton;
        if (skel == null || skel->PartialSkeletonCount < 1 || skel->PartialSkeletons == null) return false;
        pose = skel->PartialSkeletons[0].GetHavokPose(0);
        return pose != null && pose->Skeleton != null;
    }

    /// <summary>
    /// BoneApplier post-hook 调用点(协调器对每个活跃槽各调用一次);忽略参数,
    /// 每次调用都复制本帧准备好的姿态。不在 hook 中分配映射或重新求解。
    /// </summary>
    internal void ApplyTo(long invocation, nint argument)
    {
        // Loading/parsing happens on Tick; a stopped player must not stall the
        // native animation thread waiting for file work under the pose lock.
        if (!_poseActive || !_prepared) return;
        if (!Monitor.TryEnter(_poseLock)) return;
        try { ApplyToLocked(invocation, argument); }
        finally { Monitor.Exit(_poseLock); }
    }

    private void ApplyToLocked(long invocation, nint argument)
    {
        if (!_poseActive || !_prepared || !Svc.ClientState.IsGPosing || !TryGetLivePose(out var pose)) return;
        if (!Retargeter.Matches(pose)) { _prepared = false; return; }
        var observing = _diagnostic != null && !_diagnosticBodyCaptured && _diagnosticBeforeCaptured &&
            _diagnostic.PhysicsHookInvocation == invocation && _diagnosticHookArgument == argument && DiagnosticMatches(pose);
        if (observing)
        {
            // Raw inspector reads precede the existing synchronized pose diagnostics.
            CaptureNativePhysics(Skirt.NativeBonePhysicsStage.AfterGamePhysics);
            _diagnostic!.BeforeWrite = Capture(pose);
            _diagnostic.BeforePlacement = CapturePlacement(_diagnostic.BeforeWrite);
            _diagnostic.BeforePartials = _partials.Capture(((CharacterBase*)TargetCharaBase)->Skeleton, Capture);
        }
        Retargeter.CopyToPose(pose);
        if (observing) CaptureNativePhysics(Skirt.NativeBonePhysicsStage.AfterFFMMDBodyWrite);
        if (_offlineSkirtPrepared && _offlineSkirtBinding != null) Skirt.SkirtBakeWriter.Apply(pose, _offlineSkirtBinding);
        _partials.Apply(((CharacterBase*)TargetCharaBase)->Skeleton, pose);
        if (observing)
        {
            _diagnostic!.AfterWrite = Capture(pose);
            _diagnosticBodyCaptured = true;
            _diagnostic.AfterPlacement = CapturePlacement(_diagnostic.AfterWrite);
            if (Retargeter.HeightRootIndex >= 0) _diagnostic.AfterWriteRootY = _diagnostic.AfterWrite.ModelPositions[Retargeter.HeightRootIndex].Y;
            var center = Array.IndexOf(_diagnostic.Target.Names, "n_hara");
            if (center < 0) center = Array.IndexOf(_diagnostic.Target.Names, "j_kosi");
            if (center >= 0) _diagnostic.AfterWriteCenterY = _diagnostic.AfterWrite.ModelPositions[center].Y;
            _diagnostic.AfterPartials = _partials.Capture(((CharacterBase*)TargetCharaBase)->Skeleton, Capture);
            _diagnostic.DuringWriteScaleAudit = Retarget.RigWritePolicy.CompareScales(_diagnostic.Target.Names, _diagnostic.BeforeWrite!, _diagnostic.AfterWrite,
                _diagnostic.BeforePartials, _diagnostic.AfterPartials);
            if (!_diagnostic.FinalStageAvailable) { _diagnostic.FinalStageNote = _applier?.FinalStageError ?? "最终观察不可用"; Volatile.Write(ref _diagnosticReady, 1); }
        }
    }

    private static Retarget.PoseSnapshot Capture(FFXIVClientStructs.Havok.Animation.Rig.hkaPose* pose)
    {
        pose->SyncModelSpace(); pose->SyncLocalSpace(); var n = pose->Skeleton->Bones.Length;
        var snapshot = new Retarget.PoseSnapshot { LocalPositions = new Vector3[n], LocalScales = new Vector3[n], LocalRotations = new Quaternion[n], ModelPositions = new Vector3[n], ModelScales = new Vector3[n], ModelRotations = new Quaternion[n] };
        for (var i = 0; i < n; i++)
        {
            var local = pose->LocalPose.Data[i]; var model = pose->ModelPose.Data[i];
            snapshot.LocalPositions[i] = new(local.Translation.X, local.Translation.Y, local.Translation.Z); snapshot.LocalScales[i] = new(local.Scale.X, local.Scale.Y, local.Scale.Z); snapshot.LocalRotations[i] = new(local.Rotation.X, local.Rotation.Y, local.Rotation.Z, local.Rotation.W);
            snapshot.ModelPositions[i] = new(model.Translation.X, model.Translation.Y, model.Translation.Z); snapshot.ModelScales[i] = new(model.Scale.X, model.Scale.Y, model.Scale.Z); snapshot.ModelRotations[i] = new(model.Rotation.X, model.Rotation.Y, model.Rotation.Z, model.Rotation.W);
        }
        return snapshot;
    }

    internal void CaptureFinalPose()
    {
        if (!Monitor.TryEnter(_poseLock)) return;
        try
        {
            if (_diagnostic == null || _diagnostic.AfterWrite == null || Volatile.Read(ref _diagnosticReady) != 0) return;
            if (!TryGetLivePose(out var pose) || !DiagnosticMatches(pose)) return;
            CaptureNativePhysics(Skirt.NativeBonePhysicsStage.FinalRender);
            _diagnostic.FinalRender = Capture(pose);
            _diagnostic.FinalPlacement = CapturePlacement(_diagnostic.FinalRender);
            if (Retargeter.HeightRootIndex >= 0) _diagnostic.FinalRootY = _diagnostic.FinalRender.ModelPositions[Retargeter.HeightRootIndex].Y;
            var center = Array.IndexOf(_diagnostic.Target.Names, "n_hara");
            if (center < 0) center = Array.IndexOf(_diagnostic.Target.Names, "j_kosi");
            if (center >= 0) _diagnostic.FinalCenterY = _diagnostic.FinalRender.ModelPositions[center].Y;
            _diagnostic.FinalPartials = _partials.Capture(((CharacterBase*)TargetCharaBase)->Skeleton, Capture);
            _diagnostic.FinalStageScaleAudit = Retarget.RigWritePolicy.CompareScales(_diagnostic.Target.Names, _diagnostic.AfterWrite, _diagnostic.FinalRender,
                _diagnostic.AfterPartials, _diagnostic.FinalPartials);
            Volatile.Write(ref _diagnosticReady, 1);
        }
        finally { Monitor.Exit(_poseLock); }
    }

    private void FlushDiagnostics()
    {
        if ((_diagnosticRequested || _diagnostic != null) && Environment.TickCount64 > _diagnosticDeadline && Volatile.Read(ref _diagnosticReady) == 0)
        {
            _diagnosticRequested = false;
            if (_diagnostic == null) { DiagnosticStatus = Loc.S.DiagNoWrite; return; }
            _diagnostic.FinalStageNote = Loc.S.DiagTimeout; Volatile.Write(ref _diagnosticReady, 1);
        }
        if (_diagnostic == null || Volatile.Read(ref _diagnosticReady) == 0) return;
        // The first body/final observation is frozen. An invocation whose Clothing
        // entry was inside that global boundary may finish later on a worker; wait
        // only for its matching exit, never capture a new later invocation.
        if (_nativePhysics.HasPendingClothingObservation && Environment.TickCount64 <= _diagnosticDeadline) return;
        if (_nativePhysics.HasPendingClothingObservation)
            _diagnostic.PhysicsObservationNote += " Clothing entry was observed, but its matching exit was not captured before the diagnostic deadline.";
        try
        {
            _diagnostic.SkirtPhysics = _nativePhysics.CreateSnapshot();
            _diagnostic.NativeClothingObserverStatus = _clothingObserver?.Status ?? "Unavailable";
            _diagnostic.NativeClothingObserverError = _clothingObserver?.Error;
            foreach (var profile in _diagnostic.PhybProfiles)
                profile.SourceFileName = _diagnostic.SkirtPhysics.Stages
                    .SelectMany(stage => stage.ResourceSlots)
                    .FirstOrDefault(slot => slot.ResourceIndex == profile.ResourceIndex && slot.Resource.FileNameReadable)?.Resource.FileName;
            _diagnostic.PhysicsHookArgument = $"0x{(ulong)_diagnosticHookArgument:X}";
            if (string.IsNullOrEmpty(_diagnostic.PhysicsObservationNote) && (!_diagnosticBeforeCaptured || !_diagnosticBodyCaptured))
                _diagnostic.PhysicsObservationNote = "Matching pre/post observation was not completed; missing stages are not native physics evidence.";
            var directory = Path.Combine(Svc.PluginInterface.GetPluginConfigDirectory(), "diagnostics"); Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"FFMMD-slot{Index + 1}-{DateTime.Now:yyyyMMdd-HHmmss-fff}-frame{_diagnostic.Frame:0.00}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(_diagnostic, new JsonSerializerOptions { IncludeFields = true, WriteIndented = true }));
            ScaleAuditStatus = string.Format(Loc.S.ScaleAudit, _diagnostic.Frame / 30, _diagnostic.DuringWriteScaleAudit?.ChangedBones.Length.ToString() ?? Loc.S.ScaleNotCaptured, _diagnostic.FinalStageScaleAudit?.ChangedBones.Length.ToString() ?? Loc.S.ScaleNotCaptured);
            DiagnosticStatus = string.Format(Loc.S.DiagExported, path); PluginLog.Information($"[FFMMD] {DiagnosticStatus}");
        }
        catch (Exception e) { DiagnosticStatus = string.Format(Loc.S.DiagSaveFailed, e.Message); }
        _diagnostic = null; Volatile.Write(ref _diagnosticReady, 0);
        _diagnosticTree = null; _diagnosticAnimation = null;
        _nativePhysics.Invalidate("Diagnostic completed");
    }

    private Retarget.PlacementSnapshot CapturePlacement(Retarget.PoseSnapshot body)
    {
        var cb = (CharacterBase*)TargetCharaBase; var go = (GameObject*)TargetGameObjectAddress;
        var result = new Retarget.PlacementSnapshot { ActorPosition = new(go->Position.X, go->Position.Y, go->Position.Z),
            DrawPosition = new(cb->Position.X, cb->Position.Y, cb->Position.Z), DrawScale = new(cb->Scale.X, cb->Scale.Y, cb->Scale.Z),
            DrawRotation = new(cb->Rotation.X, cb->Rotation.Y, cb->Rotation.Z, cb->Rotation.W), HasGraphicsParent = cb->ParentObject != null };
        if (!result.HasGraphicsParent && result.DrawRotation.LengthSquared() > 1e-12f)
        {
            var rotation = Quaternion.Normalize(result.DrawRotation);
            float WorldY(int bone) => result.DrawPosition.Y + Vector3.Transform(body.ModelPositions[bone] * result.DrawScale, rotation).Y;
            if (Retargeter.HeightRootIndex >= 0) { result.RootWorldY = WorldY(Retargeter.HeightRootIndex); result.RootRelativeToActorY = result.RootWorldY - result.ActorPosition.Y; }
            if (Retargeter.CenterBoneIndex >= 0) result.CenterWorldY = WorldY(Retargeter.CenterBoneIndex);
        }
        return result;
    }

    /// <summary> 失败原因节流日志:同一原因 5 秒内只报一次,避免刷屏。 </summary>
    private void Diag(string msg)
    {
        var now = Environment.TickCount64;
        if (msg == _lastDiag && now - _lastDiagMs < 5000) return;
        _lastDiag = msg;
        _lastDiagMs = now;
        PluginLog.Information($"[FFMMD] {msg}");
    }

    /// <summary> 供 DebugTab 使用:不播放也能构建骨架缓存(仅建立映射/dump,不写骨骼)。 </summary>
    public bool TryBuildDebugTree()
    {
        lock (_poseLock) return TryBuildDebugTreeLocked();
    }

    private bool TryBuildDebugTreeLocked()
    {
        ResolveTarget();
        if (!TryGetLivePose(out var pose)) return false;
        return Retargeter.EnsureSkeleton(pose, Anim, P.Config.Cal);
    }

    public List<string> DiagnoseCurrentFrame()
    {
        lock (_poseLock)
        {
            _prepared = false;
            if (!TryBuildDebugTreeLocked()) return [Loc.S.NoSkelReady];
            return Retargeter.Diagnose((float)(TimeSec * Vmd.VmdAnimation.FramesPerSecond), P.Config.Cal);
        }
    }

    /// <summary> 附近角色(GPose 生成的也算),给目标下拉列表用。 </summary>
    public List<(ulong Id, string Name, float Distance)> GetNearbyPlayers()
    {
        var result = new List<(ulong, string, float)>();
        var self = Svc.Objects.LocalPlayer;
        if (self == null) return result;
        foreach (var o in Svc.Objects)
        {
            if (o is not ICharacter || !o.IsValid()) continue;
            if (Svc.ClientState.IsGPosing && !IsGposeActor(o)) continue;
            var d = Vector3.Distance(o.Position, self.Position);
            if (d < 50f) result.Add((o.GameObjectId, o.Name.TextValue, d));
        }
        result.Sort((a, b) => a.Item3.CompareTo(b.Item3));
        return result;
    }

    /// <summary> 移除槽时由协调器调用:停止并清空目标,不触碰其他槽。 </summary>
    internal void Detach()
    {
        lock (_poseLock)
        {
            _detached = true; _motionGeneration++;
            Stop();
            ReleaseSkirtLease(); ResetSkirtCache(); _motionBytes = null;
            Interlocked.Exchange(ref PendingLoadPath, null); Interlocked.Exchange(ref PendingPmxPath, null);
            ClearTarget();
        }
    }
}
