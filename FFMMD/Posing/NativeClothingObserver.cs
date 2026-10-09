using System.Diagnostics;
using System.Threading;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Graphics.Physics;

namespace FFMMD.Posing;

/// <summary>
/// Optional, read-only observation hooks for the two public BoneSimulator update methods.
/// It never calls a simulator method itself and remains fully disabled outside a diagnostic
/// window. Hook callbacks carry only identity/timing; the owner performs bounded reads.
/// </summary>
public readonly record struct NativeSimulationCallData(
    long Token,
    nint Simulator,
    nint Module,
    string Method,
    bool Before,
    int ManagedThreadId,
    long TimestampTicks)
{
    /// <summary>False at entry; true at exit only after the game original returned normally.</summary>
    public bool OriginalCompleted { get; init; }
}

public sealed unsafe class NativeClothingObserver : IDisposable
{
    private delegate void SimulationDelegate(BoneSimulator* simulator, BonePhysicsModule* module);

    private Hook<SimulationDelegate>? _updateHook;
    private Hook<SimulationDelegate>? _withoutIntegrationHook;
    private SimulationDelegate? _updateOriginal;
    private SimulationDelegate? _withoutIntegrationOriginal;
    private readonly object _lifecycleLock = new();
    private long _sequence;
    private int _enabled;
    private int _disposed;
    private int _available;
    private int _activeDetours;
    private int _loggedCallbackError;
    private string? _error;
    private string _status = "Unavailable";
    private Action<NativeSimulationCallData>? _onSimulationCall;

    public bool Available => Volatile.Read(ref _available) != 0 && Volatile.Read(ref _disposed) == 0;
    public string? Error => Volatile.Read(ref _error);
    public bool Enabled => Volatile.Read(ref _enabled) != 0;
    public string Status => Volatile.Read(ref _status);
    public Action<NativeSimulationCallData>? OnSimulationCall
    {
        get => Volatile.Read(ref _onSimulationCall);
        set => Volatile.Write(ref _onSimulationCall, value);
    }

    public NativeClothingObserver()
    {
        try
        {
            // The installed ClientStructs resolver supplies the public member-function addresses.
            // Both generated methods have the ABI void(BoneSimulator*, BonePhysicsModule*).
            var updateAddress = BoneSimulator.Addresses.Update.Value;
            var noIntegrationAddress = BoneSimulator.Addresses.UpdateWithoutIntegration.Value;
            if (updateAddress == 0 || noIntegrationAddress == 0 || updateAddress == noIntegrationAddress)
            {
                _error = "BoneSimulator update addresses are unresolved or ambiguous.";
                return;
            }

            _updateHook = Svc.Hook.HookFromAddress<SimulationDelegate>(updateAddress, UpdateDetour);
            _updateOriginal = _updateHook.Original;
            _withoutIntegrationHook = Svc.Hook.HookFromAddress<SimulationDelegate>(noIntegrationAddress, WithoutIntegrationDetour);
            _withoutIntegrationOriginal = _withoutIntegrationHook.Original;
            // Constructing hooks must not alter game execution. They are enabled only by SetEnabled.
            _available = 1;
            _status = "Ready";
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            DisposeHook(_updateHook);
            DisposeHook(_withoutIntegrationHook);
        }
    }

    /// <summary>Enables or disables both hooks. Disabled means no observer callback or read.</summary>
    public void SetEnabled(bool enabled)
    {
        // Lifecycle changes occur on Tick/disposal. A native detour never acquires this lock.
        lock (_lifecycleLock)
        {
            if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _available) == 0 || Enabled == enabled) return;
            if (enabled)
            {
                try
                {
                    _updateHook!.Enable();
                    _withoutIntegrationHook!.Enable();
                    Volatile.Write(ref _enabled, 1);
                    Volatile.Write(ref _status, "Observing");
                }
                catch (Exception ex)
                {
                    Fault("enable", ex);
                }
            }
            else
            {
                Volatile.Write(ref _enabled, 0);
                var updateError = DisableHook(_updateHook);
                var noIntegrationError = DisableHook(_withoutIntegrationHook);
                if (updateError != null || noIntegrationError != null) Fault("disable", updateError ?? noIntegrationError!);
                else Volatile.Write(ref _status, "Ready");
            }
        }
    }

    private void UpdateDetour(BoneSimulator* simulator, BonePhysicsModule* module) =>
        Detour(simulator, module, "Update", _updateOriginal!);

    private void WithoutIntegrationDetour(BoneSimulator* simulator, BonePhysicsModule* module) =>
        Detour(simulator, module, "UpdateWithoutIntegration", _withoutIntegrationOriginal!);

    private void Detour(BoneSimulator* simulator, BonePhysicsModule* module, string method,
        SimulationDelegate original)
    {
        Interlocked.Increment(ref _activeDetours);
        try
        {
            var observe = Enabled && Volatile.Read(ref _disposed) == 0;
            var token = observe ? Interlocked.Increment(ref _sequence) : 0;
            var callback = observe ? Volatile.Read(ref _onSimulationCall) : null;
            if (callback != null) Notify(callback, new(token, (nint)simulator, (nint)module, method, true,
                Environment.CurrentManagedThreadId, Stopwatch.GetTimestamp()));
            // This is the only game call in the detour. The original is called even if observation
            // callback code fails, and any after callback remains diagnostic-only.
            var completed = false;
            try
            {
                original(simulator, module);
                completed = true;
            }
            finally
            {
                // Keep the entry/exit pair even when SetEnabled(false) races the original call.
                // The owner can then discard the pair by token/window generation; suppressing the
                // exit here would make an in-flight call look like a call that never completed.
                // Disposal still suppresses callbacks because the owner may already have torn down
                // its diagnostic state.
                if (callback != null && Volatile.Read(ref _disposed) == 0)
                    Notify(callback, new(token, (nint)simulator, (nint)module, method, false,
                        Environment.CurrentManagedThreadId, Stopwatch.GetTimestamp()) { OriginalCompleted = completed });
            }
        }
        finally
        {
            Interlocked.Decrement(ref _activeDetours);
        }
    }

    private void Notify(Action<NativeSimulationCallData> callback, NativeSimulationCallData data)
    {
        try { callback(data); }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref _loggedCallbackError, 1) == 0)
                LogError($"[FFMMD] Native clothing observer callback failed: {ex.Message}");
        }
    }

    private void Fault(string operation, Exception error)
    {
        Volatile.Write(ref _enabled, 0);
        Volatile.Write(ref _available, 0);
        Volatile.Write(ref _error, $"{operation}: {error.Message}");
        Volatile.Write(ref _status, "Faulted");
        DisableHook(_updateHook);
        DisableHook(_withoutIntegrationHook);
        LogError($"[FFMMD] Native clothing observer {operation} failed: {error.Message}");
    }

    private static Exception? DisableHook(Hook<SimulationDelegate>? hook)
    {
        try { hook?.Disable(); return null; }
        catch (Exception error) { return error; }
    }

    private static void DisposeHook(Hook<SimulationDelegate>? hook)
    {
        try { hook?.Dispose(); }
        catch (Exception error) { LogError($"[FFMMD] Native clothing observer disposal failed: {error.Message}"); }
    }

    private static void LogError(string message)
    {
        // Diagnostic/logging failures must never escape into a game original invocation.
        try { PluginLog.Error(message); } catch { }
    }

    public void Dispose()
    {
        lock (_lifecycleLock)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Volatile.Write(ref _enabled, 0);
            Volatile.Write(ref _available, 0);
            Volatile.Write(ref _onSimulationCall, null);
            Volatile.Write(ref _status, "Disposed");
            DisableHook(_updateHook);
            DisableHook(_withoutIntegrationHook);
            // Disable prevents new entries. Drain already-entered detours before freeing their
            // trampolines; never wait from a detour itself. If a game native call exceeds the
            // bound, keep the disabled hooks alive and let the host's hook manager finish cleanup.
            var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 10;
            while (Volatile.Read(ref _activeDetours) != 0 && Stopwatch.GetTimestamp() < deadline)
                Thread.Sleep(1);
            if (Volatile.Read(ref _activeDetours) == 0)
            {
                DisposeHook(_updateHook);
                DisposeHook(_withoutIntegrationHook);
            }
            else
            {
                LogError("[FFMMD] Native clothing observer retained disabled hooks because a simulator call remained in flight after 100 ms.");
            }
        }
    }
}
