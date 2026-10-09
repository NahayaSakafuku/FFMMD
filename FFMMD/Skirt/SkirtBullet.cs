using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FFMMD.Skirt;

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct NativeBodyDescription
{
    public int Shape, Mode;
    /// <summary>Zero-based PMX collision group index, 0..15. Native code forms its bit.</summary>
    public uint CollisionGroup;
    /// <summary>Raw PMX allowed-group mask; each set bit enables collision with that group.</summary>
    public uint CollisionMask;
    public Vector3 Size, Position;
    public Quaternion Rotation;
    public float Mass, LinearDamping, AngularDamping, Restitution, Friction, CollisionMargin;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct NativeJointDescription
{
    public int BodyA, BodyB;
    public Vector3 Position;
    public Quaternion Rotation;
    /// <summary>Raw PMX angular limits. Spring2 RO_XZY maps to Blender's reflected RO_XYZ, with .5 damping.</summary>
    public Vector3 LinearLower, LinearUpper, AngularLower, AngularUpper, LinearSpring, AngularSpring;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct NativeBodyTransform
{
    public Vector3 Position;
    public Quaternion Rotation;
    public NativeBodyTransform(Vector3 position, Quaternion rotation) { Position = position; Rotation = rotation; }
}

/// <summary>
/// Independent Bullet world for background fixed-step bakes. This DLL never
/// accesses game memory and its worlds must not be called from native pose hooks.
/// </summary>
public sealed class SkirtBullet : IDisposable
{
    private static readonly object LoadGate = new();
    private static Api? _api;
    private readonly WorldHandle _world;
    public static string? EngineVersion => _api?.EngineVersion;

    public static bool TryInitialize(string libraryPath, out string? error)
    {
        lock (LoadGate)
        {
            if (_api != null) { error = null; return true; }
            nint library = 0;
            try
            {
                if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess) throw new PlatformNotSupportedException("The bundled Bullet worker requires Windows x64.");
                libraryPath = SkirtLibraryLocator.Resolve(libraryPath);
                if (!File.Exists(libraryPath)) throw new FileNotFoundException("Bundled FFMMD.Bullet.dll is missing.", libraryPath);
                if (Marshal.SizeOf<NativeBodyDescription>() != 80 || Marshal.SizeOf<NativeJointDescription>() != 108 || Marshal.SizeOf<NativeBodyTransform>() != 28)
                    throw new InvalidOperationException("Bullet managed ABI layout does not match.");
                library = NativeLibrary.Load(libraryPath);
                var api = new Api(library);
                if (api.AbiVersion() != 1) throw new InvalidDataException("Bundled Bullet ABI is not version 1.");
                _api = api; error = null; return true;
            }
            catch (Exception e) when (e is IOException or BadImageFormatException or DllNotFoundException or EntryPointNotFoundException or InvalidOperationException or PlatformNotSupportedException or InvalidDataException)
            {
                if (library != 0) NativeLibrary.Free(library);
                error = e.Message; return false;
            }
        }
    }

    public SkirtBullet(Vector3 gravity, int solverIterations = 40, string? libraryPath = null)
    {
        if (_api == null)
        {
            if (!TryInitialize(SkirtLibraryLocator.Resolve(libraryPath), out var error)) throw new InvalidOperationException(error);
        }
        var api = _api!; var handle = api.Create(gravity, solverIterations);
        if (handle == 0) throw new InvalidOperationException(api.Error());
        _world = new WorldHandle(handle, api);
    }

    public int AddBody(in NativeBodyDescription description)
    {
        var added = false; _world.DangerousAddRef(ref added);
        try { Check(_world.Api.AddBody(_world.DangerousGetHandle(), description, out var id)); return id; }
        finally { if (added) _world.DangerousRelease(); }
    }
    public int AddJoint(in NativeJointDescription description, bool disableCollision = false)
    {
        var added = false; _world.DangerousAddRef(ref added);
        try
        {
            int id;
            if (disableCollision)
            {
                var call = _world.Api.AddJointCollisionOption ?? throw new InvalidOperationException("Bullet worker is missing joint collision filtering.");
                Check(call(_world.DangerousGetHandle(), description, 1, out id));
            }
            else Check(_world.Api.AddJoint(_world.DangerousGetHandle(), description, out id));
            return id;
        }
        finally { if (added) _world.DangerousRelease(); }
    }
    /// <summary>Spring-free six-axis-free helper matching a Blender noncollision constraint.</summary>
    public void DisableCollisionPair(int bodyA, int bodyB)
    {
        var added = false; _world.DangerousAddRef(ref added);
        try
        {
            var call = _world.Api.DisableCollisionPair ?? throw new InvalidOperationException("Bullet worker is missing pair collision filtering.");
            Check(call(_world.DangerousGetHandle(), bodyA, bodyB));
        }
        finally { if (added) _world.DangerousRelease(); }
    }
    public void SetTransform(int body, in NativeBodyTransform transform, bool resetVelocity = false)
    {
        var added = false; _world.DangerousAddRef(ref added);
        try { Check(_world.Api.SetTransform(_world.DangerousGetHandle(), body, transform, resetVelocity ? 1 : 0)); }
        finally { if (added) _world.DangerousRelease(); }
    }
    public void SetPosition(int body, Vector3 position)
    {
        var added = false; _world.DangerousAddRef(ref added);
        try { Check(_world.Api.SetPosition(_world.DangerousGetHandle(), body, position)); }
        finally { if (added) _world.DangerousRelease(); }
    }
    public NativeBodyTransform GetTransform(int body)
    {
        var added = false; _world.DangerousAddRef(ref added);
        try { Check(_world.Api.GetTransform(_world.DangerousGetHandle(), body, out var transform)); return transform; }
        finally { if (added) _world.DangerousRelease(); }
    }
    /// <summary>A single exact fixed step. The baker controls substeps and cancellation.</summary>
    public void Step(float seconds)
    {
        var added = false; _world.DangerousAddRef(ref added);
        try { Check(_world.Api.Step(_world.DangerousGetHandle(), seconds)); }
        finally { if (added) _world.DangerousRelease(); }
    }
    private void Check(int code) { if (code != 0) throw new InvalidOperationException(_world.Api.Error()); }
    public void Dispose() => _world.Dispose();

    private sealed class WorldHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public Api Api { get; }
        public WorldHandle(nint world, Api api) : base(true) { Api = api; SetHandle(world); }
        protected override bool ReleaseHandle() { Api.Destroy(handle); return true; }
    }

    private sealed class Api
    {
        // The library remains loaded for the plugin process lifetime. Each world
        // has a SafeHandle; freeing DLL code while a finalizer owns a world would
        // make deterministic cleanup unsafe.
        private readonly nint _library;
        public readonly VersionCall AbiVersion;
        private readonly TextCall _lastError;
        public readonly CreateCall Create;
        public readonly DestroyCall Destroy;
        public readonly AddBodyCall AddBody;
        public readonly AddJointCall AddJoint;
        public readonly AddJointCollisionCall? AddJointCollisionOption;
        public readonly DisableCollisionPairCall? DisableCollisionPair;
        public readonly SetTransformCall SetTransform;
        public readonly GetTransformCall GetTransform;
        public readonly SetPositionCall SetPosition;
        public readonly StepCall Step;
        public readonly string EngineVersion;
        public Api(nint library)
        {
            _library = library;
            T Export<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
            T? OptionalExport<T>(string name) where T : Delegate => NativeLibrary.TryGetExport(library, name, out var address)
                ? Marshal.GetDelegateForFunctionPointer<T>(address) : null;
            AbiVersion = Export<VersionCall>("ffsk_abi_version"); _lastError = Export<TextCall>("ffsk_last_error");
            EngineVersion = Marshal.PtrToStringUTF8(Export<TextCall>("ffsk_engine_version")()) ?? "unknown";
            Create = Export<CreateCall>("ffsk_world_create"); Destroy = Export<DestroyCall>("ffsk_world_destroy");
            AddBody = Export<AddBodyCall>("ffsk_body_add"); AddJoint = Export<AddJointCall>("ffsk_joint_add");
            AddJointCollisionOption = OptionalExport<AddJointCollisionCall>("ffsk_joint_add_collision_option");
            DisableCollisionPair = OptionalExport<DisableCollisionPairCall>("ffsk_collision_disable_pair");
            SetTransform = Export<SetTransformCall>("ffsk_body_set_transform"); GetTransform = Export<GetTransformCall>("ffsk_body_get_transform");
            SetPosition = Export<SetPositionCall>("ffsk_body_set_position"); Step = Export<StepCall>("ffsk_world_step");
        }
        public string Error() => Marshal.PtrToStringUTF8(_lastError()) ?? "Bullet worker failed.";
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int VersionCall();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate nint TextCall();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate nint CreateCall(Vector3 gravity, int iterations);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void DestroyCall(nint world);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int AddBodyCall(nint world, in NativeBodyDescription description, out int id);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int AddJointCall(nint world, in NativeJointDescription description, out int id);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int AddJointCollisionCall(nint world, in NativeJointDescription description, int disableCollision, out int id);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int DisableCollisionPairCall(nint world, int bodyA, int bodyB);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int SetTransformCall(nint world, int id, in NativeBodyTransform transform, int resetVelocity);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int GetTransformCall(nint world, int id, out NativeBodyTransform transform);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int SetPositionCall(nint world, int id, Vector3 position);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int StepCall(nint world, float seconds);
    }
}
