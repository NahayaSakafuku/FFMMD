using System.Diagnostics;
using System.Threading;
using Dalamud.Game;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Plugin.Services;
using BoneApplier = FFMMD.Posing.BoneApplier;

namespace FFMMD.Player;

/// <summary>
/// 播放协调器:管理多个 PlayerSlot(每槽一个角色/一条时间轴),统一推进时钟、
/// 分发 native hook 写入,主槽(槽 0)的 transport 事件转发给音乐服务。
/// 全局参数:速度/循环(所有槽共享)、快捷键(作用于主槽)、窗口自动弹出。
/// </summary>
public sealed unsafe class VmdPlayerService : IDisposable
{
    private readonly List<PlayerSlot> _slots = new();
    private PlayerSlot[] _hookSlots = [];
    private readonly BoneApplier? _applier;
    private readonly Posing.NativeClothingObserver _clothingObserver;
    private readonly Skirt.SkirtPreprocessor _skirtPreprocessor;
    private readonly string _skirtLibraryPath;
    private long _lastTickStamp = -1; // Stopwatch 时间戳(Tick_COUNT 分辨率只有 10-16ms,会让动画时钟抖动)
    private long _lastSaveStamp = Stopwatch.GetTimestamp();
    private bool _wasGposing;
    private readonly HashSet<int> _hotkeyDown = new();

    public IReadOnlyList<PlayerSlot> Slots => _slots;
    /// <summary> 主槽(槽 0):音乐跟随与全局快捷键的作用对象;不可删除。 </summary>
    public PlayerSlot Primary => _slots[0];

    /// <summary>
    /// transport 通知:主槽事件的转发 + 协调器自身事件(SpeedChanged/Disposing)。
    /// 控制点全部在 Framework/UI 线程;消费者不得在此长时间阻塞。
    /// </summary>
    public event Action<TransportEvent>? TransportChanged;

    public VmdPlayerService(BoneApplier applier)
    {
        _applier = applier;
        _skirtLibraryPath = Skirt.SkirtLibraryLocator.FromPluginAssembly(Svc.PluginInterface.AssemblyLocation.FullName);
        var skirtCachePath = Path.Combine(Svc.PluginInterface.GetPluginConfigDirectory(), "skirt-cache");
        _skirtPreprocessor = new Skirt.SkirtPreprocessor(skirtCachePath);
        PluginLog.Information($"[FFMMD] 裙骨物理引擎: {_skirtLibraryPath}; 缓存目录: {skirtCachePath}");
        _clothingObserver = new Posing.NativeClothingObserver();
        _clothingObserver.OnSimulationCall = ObserveSimulationAll;
        if (_applier != null)
        {
            _applier.OnBeforeUpdateBonePhysics = CaptureBeforePhysicsAll;
            _applier.OnUpdateBonePhysics = ApplyToAll;
            _applier.OnFinalized = CaptureFinalAll;
        }
        var primary = new PlayerSlot(applier, 0, _skirtPreprocessor, _skirtLibraryPath, _clothingObserver);
        primary.TransportChanged += e => TransportChanged?.Invoke(e);
        _slots.Add(primary);
        Volatile.Write(ref _hookSlots, _slots.ToArray());
        Svc.Framework.Update += Tick;
    }

    public PlayerSlot AddSlot()
    {
        var slot = new PlayerSlot(_applier, _slots.Count, _skirtPreprocessor, _skirtLibraryPath, _clothingObserver);
        _slots.Add(slot);
        Volatile.Write(ref _hookSlots, _slots.ToArray());
        return slot;
    }

    /// <summary> 移除槽(主槽 0 不可移除);停止其姿态覆盖并清空目标。 </summary>
    public void RemoveSlot(int index)
    {
        if (index <= 0 || index >= _slots.Count) return;
        _slots[index].Detach();
        _slots.RemoveAt(index);
        for (var i = index; i < _slots.Count; i++) _slots[i].Index = i;
        Volatile.Write(ref _hookSlots, _slots.ToArray());
    }

    /// <summary> 速度收口:全局共享,钳制范围与 Normalize 一致并通知音频。 </summary>
    public void SetSpeed(float speed)
    {
        speed = float.IsFinite(speed) ? Math.Clamp(speed, 0.25f, 4f) : 1f;
        if (Math.Abs(P.Config.Speed - speed) < 1e-5f) return;
        P.Config.Speed = speed;
        P.ConfigDirty = true;
        TransportChanged?.Invoke(new TransportEvent(TransportAction.SpeedChanged, Primary.TimeSec, Speed: speed));
    }

    private void Tick(IFramework framework)
    {
        var now = Stopwatch.GetTimestamp();
        var dt = _lastTickStamp < 0 ? 0 : (now - _lastTickStamp) / (double)Stopwatch.Frequency;
        _lastTickStamp = now;

        // GPose 下游戏隐藏聊天框,/ffmmd 没法敲,进入时自动弹出窗口(Brio/Ktisis 同款行为)。
        var gposing = Svc.ClientState.IsGPosing;
        if (gposing && !_wasGposing && P.Config.AutoOpenInGPose && EzConfigGui.Window is { } w)
            w.IsOpen = true;
        _wasGposing = gposing;

        foreach (var slot in _slots.ToArray()) slot.Tick(dt, gposing);
        _clothingObserver.SetEnabled(gposing && _slots.Any(slot => slot.NeedsClothingObservation));
        HandleHotkeys();

        // 校准滑条每次编辑都 Save 会刷盘太频繁,脏标记 + 3 秒节流。
        if (P.ConfigDirty && (now - _lastSaveStamp) / (double)Stopwatch.Frequency >= 3)
        {
            _lastSaveStamp = now;
            P.Save();
        }
    }

    /// <summary> Hook 分发:每次调用对每个槽各执行一次幂等写入(槽内自行验证目标)。 </summary>
    private void CaptureBeforePhysicsAll(long invocation, nint argument)
    {
        foreach (var slot in Volatile.Read(ref _hookSlots)) slot.CaptureBeforeGamePhysics(invocation, argument);
    }

    private void ApplyToAll(long invocation, nint argument)
    {
        foreach (var slot in Volatile.Read(ref _hookSlots)) slot.ApplyTo(invocation, argument);
    }

    private void CaptureFinalAll()
    {
        foreach (var slot in Volatile.Read(ref _hookSlots)) slot.CaptureFinalPose();
    }

    private void ObserveSimulationAll(Posing.NativeSimulationCallData call)
    {
        foreach (var slot in Volatile.Read(ref _hookSlots)) slot.ObserveClothingSimulation(call);
    }

    // —— 全局快捷键(窗口关闭时仍有效;作用于主槽)——

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
        var p = Primary;
        if (p.Anim == null || !Svc.ClientState.IsGPosing) return;
        if (p.Playing && p.Paused) p.SetPaused(false);
        else if (!p.Playing) p.Play();
        // 播放中再按播放键:不从头重播,避免误触。
    }

    private void HotkeyPauseToggle()
    {
        if (Primary.Playing) Primary.SetPaused(!Primary.Paused);
    }

    private void HotkeyStop()
    {
        if (Primary.Playing || Primary.Paused || Primary.HasPendingPlay) Primary.Stop();
    }

    public void Dispose()
    {
        _clothingObserver.SetEnabled(false);
        _clothingObserver.OnSimulationCall = null;
        Volatile.Write(ref _hookSlots, []);
        foreach (var slot in _slots) slot.Detach();
        _skirtPreprocessor.Dispose();
        TransportChanged?.Invoke(new TransportEvent(TransportAction.Disposing, 0));
        if (_applier != null) _applier.OnUpdateBonePhysics = null;
        if (_applier != null) _applier.OnBeforeUpdateBonePhysics = null;
        if (_applier != null) _applier.OnFinalized = null;
        Svc.Framework.Update -= Tick;
        _clothingObserver.Dispose();
    }
}
