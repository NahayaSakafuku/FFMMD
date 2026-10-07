using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;

namespace FFMMD.Posing;

/// <summary>
/// 挂 UpdateBonePhysics post-hook（参照 Brio 的帧末骨架覆盖入口）。
/// 不假定 a1 是单个 CharacterBase；播放器自行核验目标并复制准备好的姿态。
/// 具体签名及时序仍需与游戏版本相符。
/// </summary>
public sealed unsafe class BoneApplier : IDisposable
{
    // CharacterBase::UpdateBonePhysics（来源：Brio GPL-3.0，社区长期验证）。
    public const string UpdateBonePhysicsSig =
        "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 41 54 41 56 48 83 EC ?? 48 8B 59 ?? 45 33 E4";

    private delegate nint UpdateBonePhysicsDelegate(nint a1);

    private Hook<UpdateBonePhysicsDelegate>? _hook;
    private delegate void FinalizeSkeletonsDelegate(nint a1);
    private Hook<FinalizeSkeletonsDelegate>? _finalHook;
    private bool _errorLogged;

    public bool Available;
    public string? Error;

    /// <summary> 由播放器注册：每次 hook 触发后复制准备好的姿态。 </summary>
    public Action? OnUpdateBonePhysics;
    public Action? OnFinalized;
    public bool FinalStageAvailable=>_finalHook!=null;
    public string? FinalStageError;

    public BoneApplier()
    {
        try
        {
            if (Svc.SigScanner.TryScanText(UpdateBonePhysicsSig, out var addr))
            {
                _hook = Svc.Hook.HookFromAddress<UpdateBonePhysicsDelegate>(addr, Detour);
                _hook.Enable();
                Available = true;
            }
            else
            {
                Error = $"签名未命中: {UpdateBonePhysicsSig}";
            }
        }
        catch (Exception e)
        {
            Error = e.Message;
        }
        try
        {
            if(Svc.SigScanner.TryScanText("40 53 57 41 54 41 55 48 83 EC ?? ?? 48 ?? ?? ?? ?? ?? ?? ?? 4C",out var finalAddress))
            {
                _finalHook=Svc.Hook.HookFromAddress<FinalizeSkeletonsDelegate>(finalAddress,FinalDetour);
                _finalHook.Enable();
            }
            else FinalStageError="最终渲染观察签名未命中";
        }
        catch(Exception e){FinalStageError=e.Message;_finalHook?.Dispose();_finalHook=null;}
    }

    private nint Detour(nint a1)
    {
        var ret = _hook!.Original(a1);
        try
        {
            OnUpdateBonePhysics?.Invoke();
        }
        catch (Exception e)
        {
            if (!_errorLogged)
            {
                _errorLogged = true;
                PluginLog.Error($"[FFMMD] 应用姿态异常（仅报告一次）: {e}");
            }
        }
        return ret;
    }

    private void FinalDetour(nint a1)
    {
        _finalHook!.Original(a1);
        try{OnFinalized?.Invoke();}catch(Exception e){PluginLog.Error($"[FFMMD] 最终姿态诊断失败：{e.Message}");}
    }
    public void Dispose(){_hook?.Dispose();_finalHook?.Dispose();}
}
