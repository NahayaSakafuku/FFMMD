using System.Diagnostics;
using System.Threading;
using Dalamud.Game;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using GameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using BoneApplier = FFMMD.Posing.BoneApplier;

namespace FFMMD.Player;

/// <summary>
/// 播放状态机 + 目标管理。时间只在 Framework.Tick 推进；骨骼写入只发生在
/// BoneApplier 的 UpdateBonePhysics post-hook 里（读当前时钟，幂等）。
/// </summary>
public sealed unsafe class VmdPlayerService : IDisposable
{
    public Vmd.VmdAnimation? Anim;
    public string? LoadedPath;
    public string? LoadError;

    public double TimeSec;
    public bool Playing;
    public bool Paused;

    /// <summary>
    /// transport 状态变化通知(音频等消费者用)。控制点全部在 Framework/UI 线程;
    /// 消费者不得在此长时间阻塞(文件解码在各自的加载路径上)。
    /// </summary>
    public event Action<Player.TransportEvent>? TransportChanged;

    private void Fire(TransportAction action, string? path = null, bool flag = false)
        => TransportChanged?.Invoke(new TransportEvent(action, TimeSec, path, flag, P.Config.Speed));

    public Retarget.Retargeter Retargeter { get; } = new();

    /// <summary> 文件对话框回调线程 → Tick 线程 的交接点。 </summary>
    internal string? PendingLoadPath;
    internal string? PendingPmxPath;
    public string? SourcePmxPath,SourceError,DiagnosticStatus;
    public string? ScaleAuditStatus;
    private Retarget.RigDiagnosticReport? _diagnostic;
    private bool _diagnosticRequested;
    private int _diagnosticReady;
    private long _diagnosticDeadline;
    private ulong _diagnosticTargetId;
    private readonly object _poseLock=new();
    private float _preparedFrame;
    private readonly Posing.PartialPoseBridge _partials=new();

    private readonly BoneApplier? _applier;
    private long _lastTickStamp = -1; // Stopwatch 时间戳（Tick_COUNT 分辨率只有 10-16ms，会让动画时钟抖动）
    private long _lastSaveStamp = Stopwatch.GetTimestamp();
    private volatile bool _poseActive;
    private volatile bool _prepared;
    private int _targetIndex = -1;
    private ulong _targetId;
    // GPose actor range used by Brio/Game/Actor/ActorTableHelpers.cs.
    private const int GposeStart = 201, GposeEnd = 439;
    private bool _wasGposing;
    private bool _wasEnvironment;
    private readonly HashSet<int> _hotkeyDown = new();
    private long _lastDiagMs = -1;
    private string? _lastDiag;

    /// <summary> 目标游戏对象的地址（每帧从它重新解析 CharacterBase，避免缓存指针失效）。 </summary>
    public nint TargetGameObjectAddress;
    public nint TargetCharaBase;
    public string TargetName = "";
    public bool TargetValid;

    public VmdPlayerService(Posing.BoneApplier applier)
    {
        _applier = applier;
        if (_applier != null) _applier.OnUpdateBonePhysics = ApplyTo;
        if (_applier != null) _applier.OnFinalized = CaptureFinalPose;
        Svc.Framework.Update += Tick;
    }

    public void Load(string path)
    {
        try
        {
            var file = Vmd.VmdFile.Parse(path);
            var anim = Vmd.VmdAnimation.Build(file);
            if (anim.Tracks.Count == 0) throw new InvalidDataException(Loc.S.LoadNoBoneTracks);
            Anim = anim;
            LoadedPath = path;
            LoadError = null;
            TimeSec = 0;
            Playing = false;
            Paused = false;
            _poseActive = _prepared = false;
            // 目标骨架可能已缓存，重算映射与基线。
            Retargeter.ResetCache();
            Retargeter.SetSourceRig(null);
            SourcePmxPath = null; SourceError = null;
            if (P.Config.SourcePmxPath is { } pmx && string.Equals(P.Config.SourcePmxMotionPath,path,StringComparison.OrdinalIgnoreCase))
                LoadSourcePmx(pmx);
            P.Config.LastVmdPath = path;
            P.ConfigDirty = true;
            PluginLog.Information($"[FFMMD] 已加载 {Path.GetFileName(path)}：{anim.Tracks.Count} 条骨骼轨道 / {anim.MaxFrame} 帧 / 模型 {anim.ModelName}");
            Fire(TransportAction.Loaded, path: path);
        }
        catch (Exception e)
        {
            LoadError = e.Message;
            PluginLog.Error($"[FFMMD] VMD 加载失败: {e}");
        }
    }

    public void Play()
    {
        if (Anim == null) return;
        if (!Svc.ClientState.IsGPosing) { LoadError = Loc.S.LoadEnterGpose; return; }
        LoadError = null;
        Playing = true;
        Paused = false;
        _poseActive = true;
        if (TimeSec >= Anim.DurationSec) TimeSec = 0;
        Fire(TransportAction.Play);
    }

    public void Pause() => SetPaused(!Paused);

    /// <summary> UI/诊断统一走这里切换暂停,保证 transport 通知不漏发。 </summary>
    public void SetPaused(bool paused)
    {
        if (Paused == paused) return;
        Paused = paused;
        Fire(TransportAction.PauseChanged, flag: paused);
    }

    public void Stop()
    {
        Playing = false;
        Paused = false;
        TimeSec = 0;
        _poseActive = _prepared = false;
        Fire(TransportAction.Stop);
    }

    public void Seek(double time)
    {
        if (Anim == null || !double.IsFinite(time)) return;
        TimeSec = Math.Clamp(time, 0, Anim.DurationSec);
        _poseActive = true;
        _prepared = false;
        Fire(TransportAction.Seek);
    }

    /// <summary> 速度收口:与 UI 滑条共用,钳制范围与 Normalize 一致并通知音频。 </summary>
    public void SetSpeed(float speed)
    {
        speed = float.IsFinite(speed) ? Math.Clamp(speed, 0.25f, 4f) : 1f;
        if (Math.Abs(P.Config.Speed - speed) < 1e-5f) return;
        P.Config.Speed = speed;
        P.ConfigDirty = true;
        Fire(TransportAction.SpeedChanged);
    }

    // —— 全局快捷键(窗口关闭时仍有效)——

    private void HandleHotkeys()
    {
        // 插件窗口的输入框等文本输入激活时不触发,避免键入数值/聊天时误按。
        // (此 Dalamud 版本没有聊天输入检测 API;默认键位选小键盘把冲突概率降到最低。)
        if (ImGui.GetIO().WantTextInput) { _hotkeyDown.Clear(); return; }
        HandleHotkey(P.Config.PlayHotkey, HotkeyPlay);
        HandleHotkey(P.Config.PauseHotkey, HotkeyPauseToggle);
        HandleHotkey(P.Config.StopHotkey, HotkeyStop);
    }

    private void HandleHotkey(int keyInt, Action action)
    {
        if (keyInt <= 0) return;
        var key = (VirtualKey)keyInt;
        bool down;
        try { down = Svc.KeyState[key]; }
        catch { return; } // 无效虚拟键值,视为未绑定
        var wasDown = _hotkeyDown.Contains(keyInt);
        if (down && !wasDown) action();
        if (down) _hotkeyDown.Add(keyInt);
        else _hotkeyDown.Remove(keyInt);
    }

    private void HotkeyPlay()
    {
        if (Anim == null || !Svc.ClientState.IsGPosing) return;
        if (Playing && Paused) SetPaused(false);
        else if (!Playing) Play();
        // 播放中再按播放键:不从头重播,避免误触。
    }

    private void HotkeyPauseToggle()
    {
        if (Playing) SetPaused(!Paused);
    }

    private void HotkeyStop()
    {
        if (Playing || Paused) Stop();
    }
    public void LoadSourcePmx(string path)
    {
        Stop();
        try
        {
            var rig=Retarget.PmxRigReader.Parse(path);
            foreach(var name in new[]{"下半身","左足","左ひざ","左足首","右足","右ひざ","右足首"})
                if(rig.Find(name)<0)throw new InvalidDataException(string.Format(Loc.S.SourcePmxMissing, name));
            Retargeter.SetSourceRig(rig);SourcePmxPath=path;SourceError=null;
            P.Config.SourcePmxPath=path;P.Config.SourcePmxMotionPath=LoadedPath;P.ConfigDirty=true;
        }
        catch(Exception e){SourceError=e.Message;PluginLog.Error($"[FFMMD] 源 PMX 导入失败：{e.Message}");}
    }
    public void UseStandardSource()
    {
        Stop();
        lock(_poseLock)
        {
            Retargeter.SetSourceRig(null);SourcePmxPath=null;SourceError=null;
            P.Config.SourcePmxPath=P.Config.SourcePmxMotionPath=null;P.ConfigDirty=true;
        }
    }
    public void RequestDiagnostics()
    {
        lock(_poseLock)RequestDiagnosticsLocked();
    }
    private void RequestDiagnosticsLocked()
    {
        if(Anim==null||!Svc.ClientState.IsGPosing||!TargetValid||_applier?.Available!=true)
        {DiagnosticStatus=Loc.S.DiagNeedGpose;return;}
        SetPaused(true);Seek(TimeSec);_diagnosticRequested=true;_diagnostic=null;
        ScaleAuditStatus=null;
        Volatile.Write(ref _diagnosticReady,0);_diagnosticDeadline=Environment.TickCount64+3000;
        DiagnosticStatus=Loc.S.DiagWaiting;
    }

    private void Tick(IFramework framework)
    {
        lock(_poseLock)TickLocked(framework);
    }
    private void TickLocked(IFramework framework)
    {
        var now = Stopwatch.GetTimestamp();
        var dt = _lastTickStamp < 0 ? 0 : (now - _lastTickStamp) / (double)Stopwatch.Frequency;
        _lastTickStamp = now;
        _prepared = false;

        // GPose 下游戏隐藏聊天框，/ffmmd 没法敲，进入时自动弹出窗口（Brio/Ktisis 同款行为）。
        var gposing = Svc.ClientState.IsGPosing;
        if (!gposing && _wasGposing) Stop();
        if (gposing && !_wasGposing && P.Config.AutoOpenInGPose && EzConfigGui.Window is { } w)
            w.IsOpen = true;
        _wasGposing = gposing;

        HandleHotkeys();

        var pending = Interlocked.Exchange(ref PendingLoadPath, null);
        if (pending != null)
        {
            Load(pending);
        }
        var pmxPending=Interlocked.Exchange(ref PendingPmxPath,null);
        if(pmxPending!=null)LoadSourcePmx(pmxPending);

        ResolveTarget();

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
        var environment = Anim != null && gposing && TargetValid;
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
                    _partials.Prepare(((CharacterBase*)TargetCharaBase)->Skeleton,Retargeter.Tree!);
                    _preparedFrame=(float)(TimeSec*Vmd.VmdAnimation.FramesPerSecond);
                    _prepared=Retargeter.Evaluate(_preparedFrame,P.Config.Cal);
                }
            }
            catch (Exception e) { Diag($"骨架准备失败: {e.Message}"); }
        }
        FlushDiagnostics();

        // 校准滑条每次编辑都 Save 会刷盘太频繁，脏标记 + 3 秒节流。
        if (P.ConfigDirty && (now - _lastSaveStamp) / (double)Stopwatch.Frequency >= 3)
        {
            _lastSaveStamp = now;
            P.Save();
        }
    }

    public void ResolveTarget()
    {
        IGameObject? obj = null;
        switch (P.Config.TargetMode)
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
                if (P.Config.NearbyObjectId != 0)
                    foreach (var o in Svc.Objects)
                        if (o.GameObjectId == P.Config.NearbyObjectId || o.EntityId == P.Config.NearbyObjectId)
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

    private static bool IsGposeActor(IGameObject? obj) => obj is ICharacter &&
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
    /// BoneApplier post-hook 调用点；忽略参数，每次调用都复制本帧准备好的姿态。
    /// 不在 hook 中分配映射或重新求解，也不让早先的一次调用阻止后续覆盖。
    /// </summary>
    internal void ApplyTo()
    {
        // Loading/parsing happens on Tick; a stopped player must not stall the
        // native animation thread waiting for file work under the pose lock.
        if(!_poseActive||!_prepared)return;
        lock(_poseLock)ApplyToLocked();
    }
    private void ApplyToLocked()
    {
        if (!_poseActive || !_prepared || !Svc.ClientState.IsGPosing || !TryGetLivePose(out var pose)) return;
        if (!Retargeter.Matches(pose)) { _prepared = false; return; }
        if(_diagnosticRequested)
        {
            _diagnostic=Retarget.RigDiagnostics.Create(Retargeter,P.Config.Cal,_preparedFrame);
            _diagnostic.MotionPath=LoadedPath??"";_diagnostic.TargetName=TargetName;
            _diagnostic.BeforeWrite=Capture(pose);_diagnostic.FinalStageAvailable=_applier?.FinalStageAvailable==true;
            _diagnostic.BeforePlacement=CapturePlacement(_diagnostic.BeforeWrite);
            _diagnostic.BeforePartials=_partials.Capture(((CharacterBase*)TargetCharaBase)->Skeleton,Capture);
            _diagnosticTargetId=_targetId;_diagnosticRequested=false;
        }
        Retargeter.CopyToPose(pose);
        _partials.Apply(((CharacterBase*)TargetCharaBase)->Skeleton,pose);
        if(_diagnostic!=null&&_diagnostic.AfterWrite==null)
        {
            _diagnostic.AfterWrite=Capture(pose);
            _diagnostic.AfterPlacement=CapturePlacement(_diagnostic.AfterWrite);
            if(Retargeter.HeightRootIndex>=0)_diagnostic.AfterWriteRootY=_diagnostic.AfterWrite.ModelPositions[Retargeter.HeightRootIndex].Y;
            var center=Array.IndexOf(_diagnostic.Target.Names,"n_hara");
            if(center<0)center=Array.IndexOf(_diagnostic.Target.Names,"j_kosi");
            if(center>=0)_diagnostic.AfterWriteCenterY=_diagnostic.AfterWrite.ModelPositions[center].Y;
            _diagnostic.AfterPartials=_partials.Capture(((CharacterBase*)TargetCharaBase)->Skeleton,Capture);
            _diagnostic.DuringWriteScaleAudit=Retarget.RigWritePolicy.CompareScales(_diagnostic.Target.Names,_diagnostic.BeforeWrite!,_diagnostic.AfterWrite,
                _diagnostic.BeforePartials,_diagnostic.AfterPartials);
            if(!_diagnostic.FinalStageAvailable){_diagnostic.FinalStageNote=_applier?.FinalStageError??"最终观察不可用";Volatile.Write(ref _diagnosticReady,1);}
        }
    }
    private static Retarget.PoseSnapshot Capture(FFXIVClientStructs.Havok.Animation.Rig.hkaPose* pose)
    {
        pose->SyncModelSpace();pose->SyncLocalSpace();var n=pose->Skeleton->Bones.Length;
        var snapshot=new Retarget.PoseSnapshot{LocalPositions=new Vector3[n],LocalScales=new Vector3[n],LocalRotations=new Quaternion[n],ModelPositions=new Vector3[n],ModelScales=new Vector3[n],ModelRotations=new Quaternion[n]};
        for(var i=0;i<n;i++)
        {
            var local=pose->LocalPose.Data[i];var model=pose->ModelPose.Data[i];
            snapshot.LocalPositions[i]=new(local.Translation.X,local.Translation.Y,local.Translation.Z);snapshot.LocalScales[i]=new(local.Scale.X,local.Scale.Y,local.Scale.Z);snapshot.LocalRotations[i]=new(local.Rotation.X,local.Rotation.Y,local.Rotation.Z,local.Rotation.W);
            snapshot.ModelPositions[i]=new(model.Translation.X,model.Translation.Y,model.Translation.Z);snapshot.ModelScales[i]=new(model.Scale.X,model.Scale.Y,model.Scale.Z);snapshot.ModelRotations[i]=new(model.Rotation.X,model.Rotation.Y,model.Rotation.Z,model.Rotation.W);
        }
        return snapshot;
    }
    private void CaptureFinalPose()
    {
        lock(_poseLock)
        {
            if(_diagnostic==null||_diagnostic.AfterWrite==null||Volatile.Read(ref _diagnosticReady)!=0)return;
            if(_diagnosticTargetId!=_targetId||!TryGetLivePose(out var pose)||!Retargeter.Matches(pose))return;
            _diagnostic.FinalRender=Capture(pose);
            _diagnostic.FinalPlacement=CapturePlacement(_diagnostic.FinalRender);
            if(Retargeter.HeightRootIndex>=0)_diagnostic.FinalRootY=_diagnostic.FinalRender.ModelPositions[Retargeter.HeightRootIndex].Y;
            var center=Array.IndexOf(_diagnostic.Target.Names,"n_hara");
            if(center<0)center=Array.IndexOf(_diagnostic.Target.Names,"j_kosi");
            if(center>=0)_diagnostic.FinalCenterY=_diagnostic.FinalRender.ModelPositions[center].Y;
            _diagnostic.FinalPartials=_partials.Capture(((CharacterBase*)TargetCharaBase)->Skeleton,Capture);
            _diagnostic.FinalStageScaleAudit=Retarget.RigWritePolicy.CompareScales(_diagnostic.Target.Names,_diagnostic.AfterWrite,_diagnostic.FinalRender,
                _diagnostic.AfterPartials,_diagnostic.FinalPartials);
            Volatile.Write(ref _diagnosticReady,1);
        }
    }
    private void FlushDiagnostics()
    {
        if((_diagnosticRequested||_diagnostic!=null)&&Environment.TickCount64>_diagnosticDeadline&&Volatile.Read(ref _diagnosticReady)==0)
        {
            _diagnosticRequested=false;
            if(_diagnostic==null){DiagnosticStatus=Loc.S.DiagNoWrite;return;}
            _diagnostic.FinalStageNote=Loc.S.DiagTimeout;Volatile.Write(ref _diagnosticReady,1);
        }
        if(_diagnostic==null||Volatile.Read(ref _diagnosticReady)==0)return;
        try
        {
            var directory=Path.Combine(Svc.PluginInterface.GetPluginConfigDirectory(),"diagnostics");Directory.CreateDirectory(directory);
            var path=Path.Combine(directory,$"FFMMD-{DateTime.Now:yyyyMMdd-HHmmss-fff}-frame{_diagnostic.Frame:0.00}.json");
            File.WriteAllText(path,JsonSerializer.Serialize(_diagnostic,new JsonSerializerOptions{IncludeFields=true,WriteIndented=true}));
            ScaleAuditStatus=string.Format(Loc.S.ScaleAudit, _diagnostic.Frame/30, _diagnostic.DuringWriteScaleAudit?.ChangedBones.Length.ToString()??Loc.S.ScaleNotCaptured, _diagnostic.FinalStageScaleAudit?.ChangedBones.Length.ToString()??Loc.S.ScaleNotCaptured);
            DiagnosticStatus=string.Format(Loc.S.DiagExported, path);PluginLog.Information($"[FFMMD] {DiagnosticStatus}");
        }
        catch(Exception e){DiagnosticStatus=string.Format(Loc.S.DiagSaveFailed, e.Message);}
        _diagnostic=null;Volatile.Write(ref _diagnosticReady,0);
    }
    private Retarget.PlacementSnapshot CapturePlacement(Retarget.PoseSnapshot body)
    {
        var cb=(CharacterBase*)TargetCharaBase;var go=(GameObject*)TargetGameObjectAddress;
        var result=new Retarget.PlacementSnapshot{ActorPosition=new(go->Position.X,go->Position.Y,go->Position.Z),
            DrawPosition=new(cb->Position.X,cb->Position.Y,cb->Position.Z),DrawScale=new(cb->Scale.X,cb->Scale.Y,cb->Scale.Z),
            DrawRotation=new(cb->Rotation.X,cb->Rotation.Y,cb->Rotation.Z,cb->Rotation.W),HasGraphicsParent=cb->ParentObject!=null};
        if(!result.HasGraphicsParent&&result.DrawRotation.LengthSquared()>1e-12f)
        {
            var rotation=Quaternion.Normalize(result.DrawRotation);
            float WorldY(int bone)=>result.DrawPosition.Y+Vector3.Transform(body.ModelPositions[bone]*result.DrawScale,rotation).Y;
            if(Retargeter.HeightRootIndex>=0){result.RootWorldY=WorldY(Retargeter.HeightRootIndex);result.RootRelativeToActorY=result.RootWorldY-result.ActorPosition.Y;}
            if(Retargeter.CenterBoneIndex>=0)result.CenterWorldY=WorldY(Retargeter.CenterBoneIndex);
        }
        return result;
    }

    /// <summary> 失败原因节流日志：同一原因 5 秒内只报一次，避免刷屏。 </summary>
    private void Diag(string msg)
    {
        var now = Environment.TickCount64;
        if (msg == _lastDiag && now - _lastDiagMs < 5000) return;
        _lastDiag = msg;
        _lastDiagMs = now;
        PluginLog.Information($"[FFMMD] {msg}");
    }

    /// <summary> 供 DebugTab 使用：不播放也能构建骨架缓存（仅建立映射/dump，不写骨骼）。 </summary>
    public bool TryBuildDebugTree()
    {
        lock(_poseLock)return TryBuildDebugTreeLocked();
    }
    private bool TryBuildDebugTreeLocked()
    {
        ResolveTarget();
        if (!TryGetLivePose(out var pose)) return false;
        return Retargeter.EnsureSkeleton(pose, Anim, P.Config.Cal);
    }
    public List<string> DiagnoseCurrentFrame()
    {
        lock(_poseLock)
        {
            _prepared=false;
            if(!TryBuildDebugTreeLocked())return ["目标骨架尚未就绪"];
            return Retargeter.Diagnose((float)(TimeSec*Vmd.VmdAnimation.FramesPerSecond),P.Config.Cal);
        }
    }

    /// <summary> 附近角色（GPose 生成的也算），给目标下拉列表用。 </summary>
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

    public void Dispose()
    {
        Stop();
        TransportChanged?.Invoke(new TransportEvent(TransportAction.Disposing, TimeSec));
        if (_applier != null) _applier.OnUpdateBonePhysics = null;
        if (_applier != null) _applier.OnFinalized = null;
        Svc.Framework.Update -= Tick;
        ClearTarget();
    }
}
