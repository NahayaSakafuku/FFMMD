using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FFMMD.Vmd;

namespace FFMMD.Skirt;

public sealed record SkirtBakeRecipe
{
    public static SkirtBakeRecipe Default { get; } = new();
    public int Version { get; init; } = 3;
    public int Substeps { get; init; } = 120;
    public int Iterations { get; init; } = 40;
    public int WarmupFrames { get; init; } = 60;
    public float Scale { get; init; } = .08f;
    public float CollisionMargin { get; init; } = 1e-6f;
    // Distance multiplier used when building the reference collision graph.
    public float NonCollisionDistanceScale { get; init; } = 1.5f;
    public int SmoothingWindowFrames { get; init; } = 2;
    public float SmoothingMaximumCorrectionDegrees { get; init; } = 3;
    public float SmoothingStrength { get; init; } = .65f;
}

public sealed record SkirtBakeRequest
{
    public string MotionPath { get; init; } = "";
    public byte[]? MotionBytes { get; init; }
    public string? ExpectedMotionSha256 { get; init; }
    public int FrameCount { get; init; }
    public string? ReferencePmxPath { get; init; }
    public string? LibraryPath { get; init; }
    public SkirtBakeRecipe Recipe { get; init; } = SkirtBakeRecipe.Default;
}

public sealed record SkirtBakeProgress(string Phase, float Fraction, string? Message = null);

public sealed record SkirtBakeStatus(string Status, float Progress = 0, string? Message = null,
    string? LogTail = null, string? CachePath = null, SkirtBakeCache.Loaded? Cache = null, string? ContentKey = null)
{
    public bool IsCompleted => Status is "Ready" or "Failed" or "Cancelled" or "MissingTool" or "MissingModel" or "MissingMotion";
}

/// <summary>A slot subscription; releasing it cannot cancel another slot's shared work.</summary>
public sealed class SkirtBakeLease : IDisposable
{
    private readonly SkirtPreprocessor _owner;
    private SkirtBakeStatus _snapshot = new("Resolving", Message: "Reading motion and physics identities.");
    private readonly TaskCompletionSource<SkirtBakeStatus> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal SkirtBakeLease(SkirtPreprocessor owner) { _owner = owner; }
    internal bool Cancelled;
    internal SkirtPreprocessor.Job? Job;
    internal readonly CancellationTokenSource ResolutionCancellation = new();
    public SkirtBakeStatus Snapshot => Volatile.Read(ref _snapshot);
    public Task<SkirtBakeStatus> Completion => _completion.Task;
    internal void Publish(SkirtBakeStatus value)
    {
        Volatile.Write(ref _snapshot, value);
        if (value.IsCompleted) _completion.TrySetResult(value);
    }
    public void Cancel() => _owner.Cancel(this);
    public void Dispose() => Cancel();
}

public sealed record SkirtBakeWork(SkirtBakeRequest Request, SkirtPhysicsReference Reference, VmdAnimation Animation,
    string MotionSha256, string ReferencePmxSha256, string ReferenceContentSha256, string EngineSha256,
    string ContentKey, string OutputPath, string LogPath);

public sealed record SkirtBakeWorkerResult(int ExitCode, string LogTail);

public interface ISkirtBakeWorker
{
    Task<SkirtBakeWorkerResult> RunAsync(SkirtBakeWork work, Action<SkirtBakeProgress> progress,
        CancellationToken cancellationToken);
}

/// <summary>
/// One background worker shared by all slots. Game pose hooks only read lease
/// snapshots; parsing, native physics, and cache IO happen on background tasks.
/// </summary>
public sealed class SkirtPreprocessor : IDisposable
{
    private const int MaxInputBytes = 512 * 1024 * 1024;
    private readonly string _outputDirectory;
    private readonly ISkirtBakeWorker _worker;
    private readonly object _gate = new();
    private readonly Dictionary<string, Job> _jobs = new(StringComparer.Ordinal);
    private readonly HashSet<SkirtBakeLease> _resolving = [];
    private readonly Queue<Job> _queue = new();
    private readonly SemaphoreSlim _queued = new(0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _pump;
    private bool _disposed;

    internal sealed class Job
    {
        public required SkirtBakeWork Work;
        public readonly HashSet<SkirtBakeLease> Subscribers = [];
        public readonly CancellationTokenSource Cancellation = new();
        public SkirtBakeStatus Status = new("Queued");
        public bool Completed;
    }

    public SkirtPreprocessor(string outputDirectory, ISkirtBakeWorker? worker = null)
    {
        _outputDirectory = outputDirectory;
        _worker = worker ?? new BuiltinSkirtBakeWorker();
        _pump = Task.Run(ProcessQueueAsync);
    }

    public SkirtBakeLease Request(SkirtBakeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var lease = new SkirtBakeLease(this);
        // Preserve the exact bytes used to load this animation even if the
        // caller replaces the VMD file while background work is queued.
        var snapshot = request with { MotionBytes = request.MotionBytes?.ToArray() };
        lock (_gate)
        {
            if (_disposed)
            {
                lease.Cancelled = true;
                lease.Publish(new("Cancelled", Message: "Preprocessor disposed."));
                return lease;
            }
            _resolving.Add(lease);
        }
        _ = Task.Run(() => ResolveAsync(lease, snapshot));
        return lease;
    }

    private async Task ResolveAsync(SkirtBakeLease lease, SkirtBakeRequest request)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, lease.ResolutionCancellation.Token);
        var token = linked.Token;
        try
        {
            var invalid = ValidateRequest(request);
            if (invalid != null) { ResolveFailure(lease, invalid); return; }
            var motionBytes = request.MotionBytes ?? await ReadBytesAsync(request.MotionPath, token).ConfigureAwait(false);
            var motionHash = Convert.ToHexString(SHA256.HashData(motionBytes));
            if (!string.IsNullOrWhiteSpace(request.ExpectedMotionSha256) && !HashEquals(motionHash, request.ExpectedMotionSha256))
                throw new InvalidDataException("Motion SHA-256 does not match the requested animation.");
            var animation = VmdAnimation.Build(VmdFile.Parse(motionBytes));
            if (animation.Tracks.Count == 0) throw new InvalidDataException("Motion contains no bone tracks.");
            var actualFrameCount = checked((int)animation.MaxFrame + 1);
            if (request.FrameCount != actualFrameCount)
                throw new InvalidDataException($"Motion frame count is {actualFrameCount}, but request says {request.FrameCount}.");
            token.ThrowIfCancellationRequested();
            var reference = string.IsNullOrWhiteSpace(request.ReferencePmxPath) ? SkirtPhysicsReference.Builtin()
                : SkirtPhysicsReference.FromPmx(await ReadBytesAsync(request.ReferencePmxPath!, token).ConfigureAwait(false));
            var referenceHash = reference.ContentHash;
            var enginePath = SkirtLibraryLocator.Resolve(request.LibraryPath);
            var engineHash = await HashFileAsync(enginePath, token).ConfigureAwait(false);
            var keyMaterial = JsonSerializer.Serialize(new
            {
                implementation = typeof(BuiltinSkirtBaker).Assembly.ManifestModule.ModuleVersionId,
                motion = motionHash, reference = referenceHash, physics = reference.Physics.Fingerprint,
                engine = engineHash, request.FrameCount, request.Recipe,
            });
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(keyMaterial))).ToLowerInvariant();
            var work = new SkirtBakeWork(request with { MotionBytes = motionBytes, LibraryPath = enginePath }, reference,
                animation, motionHash, reference.Physics.Fingerprint, referenceHash, engineHash, key,
                Path.Combine(_outputDirectory, key + ".ffskirt.json"), Path.Combine(_outputDirectory, key + ".log"));
            lock (_gate)
            {
                _resolving.Remove(lease);
                if (_disposed || lease.Cancelled) return;
                if (!_jobs.TryGetValue(key, out var job))
                {
                    job = new Job { Work = work, Status = new("Queued", ContentKey: key, Message: "Waiting for the skirt physics worker.") };
                    _jobs[key] = job; _queue.Enqueue(job); _queued.Release();
                }
                lease.Job = job; job.Subscribers.Add(lease); lease.Publish(job.Status);
            }
        }
        catch (OperationCanceledException) { ResolveFailure(lease, new("Cancelled", Message: "Preprocessor cancelled.")); }
        catch (FileNotFoundException e) { ResolveFailure(lease, new("MissingTool", Message: e.Message)); }
        catch (DirectoryNotFoundException e) { ResolveFailure(lease, new("MissingTool", Message: e.Message)); }
        catch (Exception e) { ResolveFailure(lease, new("Failed", Message: e.Message)); }
    }

    private static SkirtBakeStatus? ValidateRequest(SkirtBakeRequest request)
    {
        if (request.MotionBytes is null && !File.Exists(request.MotionPath)) return new("MissingMotion", Message: "VMD file does not exist.");
        if (!string.IsNullOrWhiteSpace(request.ReferencePmxPath) && !File.Exists(request.ReferencePmxPath))
            return new("MissingModel", Message: "Skirt physics reference PMX does not exist.");
        if (request.MotionBytes is { Length: > MaxInputBytes }) return new("Failed", Message: "Motion input is too large.");
        if (request.FrameCount is <= 0 or > SkirtBakeCache.MaxFrames ||
            (long)request.FrameCount * SkirtBakeCache.RequiredBoneCount > SkirtBakeCache.MaxRotationSamples)
            return new("Failed", Message: "Motion frame count exceeds the supported cache range.");
        if (request.ExpectedMotionSha256 is { Length: > 0 } && !IsHash(request.ExpectedMotionSha256))
            return new("Failed", Message: "Expected motion SHA-256 is invalid.");
        var p = request.Recipe;
        if (p == null || p.Version != 3 || p.Substeps is < 1 or > 1000 || p.Iterations is < 1 or > 1000 ||
            p.WarmupFrames is < 0 or > 300 || !float.IsFinite(p.Scale) || p.Scale is < .001f or > 100 ||
            !float.IsFinite(p.CollisionMargin) || p.CollisionMargin is < 0 or > 1 ||
            !float.IsFinite(p.NonCollisionDistanceScale) || p.NonCollisionDistanceScale is < 0 or > 100 ||
            p.SmoothingWindowFrames is < 0 or > 8 || !float.IsFinite(p.SmoothingMaximumCorrectionDegrees) ||
            p.SmoothingMaximumCorrectionDegrees is < 0 or > 15 || !float.IsFinite(p.SmoothingStrength) || p.SmoothingStrength is < 0 or > 1)
            return new("Failed", Message: "Unsupported skirt physics recipe.");
        return null;
    }

    private async Task ProcessQueueAsync()
    {
        try
        {
            while (true)
            {
                await _queued.WaitAsync(_shutdown.Token).ConfigureAwait(false);
                Job? job;
                lock (_gate) job = _queue.Count == 0 ? null : _queue.Dequeue();
                if (job == null || job.Cancellation.IsCancellationRequested) continue;
                await RunJobAsync(job).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task RunJobAsync(Job job)
    {
        var work = job.Work; string? pending = null;
        try
        {
            Directory.CreateDirectory(_outputDirectory);
            Publish(job, new("CheckingCache", .02f, "Checking the shared skirt physics cache.", ContentKey: work.ContentKey));
            var cached = SkirtBakeCache.ReadFile(work.OutputPath, work.MotionSha256, work.ReferencePmxSha256);
            if (Matches(cached.Cache, work))
            {
                Complete(job, new("Ready", 1, "Shared skirt physics cache ready.", CachePath: work.OutputPath, Cache: cached.Cache, ContentKey: work.ContentKey));
                return;
            }
            job.Cancellation.Token.ThrowIfCancellationRequested();
            pending = work.OutputPath + "." + Guid.NewGuid().ToString("N") + ".pending";
            Publish(job, new("Baking", .03f, "Baking skirt physics in the built-in Bullet worker.", ContentKey: work.ContentKey));
            var result = await _worker.RunAsync(work with { OutputPath = pending }, progress =>
            {
                var value = float.IsFinite(progress.Fraction) ? Math.Clamp(progress.Fraction, 0, .99f) : 0;
                Publish(job, new("Baking", value, progress.Message ?? progress.Phase, ContentKey: work.ContentKey));
            }, job.Cancellation.Token).ConfigureAwait(false);
            job.Cancellation.Token.ThrowIfCancellationRequested();
            if (result.ExitCode != 0)
            {
                Complete(job, new("Failed", Message: $"Built-in skirt worker exited with code {result.ExitCode}.", LogTail: result.LogTail, ContentKey: work.ContentKey));
                return;
            }
            var loaded = SkirtBakeCache.ReadFile(pending, work.MotionSha256, work.ReferencePmxSha256);
            if (!Matches(loaded.Cache, work))
            {
                Complete(job, new("Failed", Message: loaded.Error ?? "Worker cache metadata does not match the request.", LogTail: result.LogTail, ContentKey: work.ContentKey));
                return;
            }
            var currentEngineHash = await HashFileAsync(work.Request.LibraryPath!, job.Cancellation.Token).ConfigureAwait(false);
            if (!HashEquals(currentEngineHash, work.EngineSha256))
            {
                Complete(job, new("Failed", Message: "Bullet library changed while baking.", LogTail: result.LogTail, ContentKey: work.ContentKey));
                return;
            }
            job.Cancellation.Token.ThrowIfCancellationRequested();
            // The rename and publication share Cancel's lock. Last-lease
            // cancellation cannot race a ready-cache commit.
            lock (_gate)
            {
                if (_disposed || job.Completed || job.Cancellation.IsCancellationRequested || job.Subscribers.Count == 0)
                    throw new OperationCanceledException(job.Cancellation.Token);
                File.Move(pending, work.OutputPath, true); pending = null;
                CompleteLocked(job, new("Ready", 1, "Skirt physics preprocessing completed.", result.LogTail,
                    work.OutputPath, loaded.Cache, work.ContentKey));
            }
        }
        catch (OperationCanceledException) { Complete(job, new("Cancelled", Message: "No active subscribers remain.", ContentKey: work.ContentKey)); }
        catch (FileNotFoundException e) { Complete(job, new("MissingTool", Message: e.Message, ContentKey: work.ContentKey)); }
        catch (Exception e) { Complete(job, new("Failed", Message: e.Message, LogTail: ReadLogTail(work.LogPath), ContentKey: work.ContentKey)); }
        finally
        {
            if (pending != null) { try { File.Delete(pending); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
    }

    private static bool Matches(SkirtBakeCache.Loaded? cache, SkirtBakeWork work)
    {
        if (cache == null || cache.FrameCount != work.Request.FrameCount) return false;
        var s = cache.Document.Solver; var r = work.Request.Recipe; var m = s.Smoothing;
        return s.Engine == BuiltinSkirtBaker.Engine && !string.IsNullOrWhiteSpace(s.EngineVersion) && s.RecipeVersion == r.Version &&
            s.KinematicDrivingAlgorithm == KinematicColliderDriver.Algorithm &&
            s.Substeps == r.Substeps && s.Iterations == r.Iterations && s.WarmupFrames == r.WarmupFrames &&
            s.Scale == r.Scale && s.CollisionMargin == r.CollisionMargin && s.NonCollisionDistanceScale == r.NonCollisionDistanceScale &&
            m != null && m.Algorithm == "BoundedBilateralQuaternion" && m.WindowFrames == r.SmoothingWindowFrames &&
            m.MaximumCorrectionDegrees == r.SmoothingMaximumCorrectionDegrees && m.Strength == r.SmoothingStrength;
    }

    private static async Task<byte[]> ReadBytesAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaxInputBytes) throw new InvalidDataException("Input file is too large.");
        var bytes = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return bytes;
    }
    private static async Task<string> HashFileAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
    }
    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static bool HashEquals(string? a, string? b) => IsHash(a) && IsHash(b) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static string? ReadLogTail(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            stream.Seek(Math.Max(0, stream.Length - 32 * 1024), SeekOrigin.Begin);
            using var reader = new StreamReader(stream); return reader.ReadToEnd();
        }
        catch (IOException) { return null; } catch (UnauthorizedAccessException) { return null; }
    }

    private void ResolveFailure(SkirtBakeLease lease, SkirtBakeStatus status)
    {
        lock (_gate) { _resolving.Remove(lease); if (!_disposed && !lease.Cancelled) lease.Publish(status); }
    }
    private void Publish(Job job, SkirtBakeStatus status)
    {
        lock (_gate)
        {
            if (_disposed || job.Completed || job.Cancellation.IsCancellationRequested) return;
            job.Status = status;
            foreach (var lease in job.Subscribers) if (!lease.Cancelled) lease.Publish(status);
        }
    }
    private void Complete(Job job, SkirtBakeStatus status)
    {
        lock (_gate) CompleteLocked(job, status);
    }
    private void CompleteLocked(Job job, SkirtBakeStatus status)
    {
        if (job.Completed) return;
        job.Completed = true; job.Status = status;
        foreach (var lease in job.Subscribers) if (!lease.Cancelled) lease.Publish(status);
        if (status.Status != "Ready") RemoveJobLocked(job);
        TrimCompletedLocked();
    }
    private void RemoveJobLocked(Job job)
    {
        if (_jobs.TryGetValue(job.Work.ContentKey, out var existing) && ReferenceEquals(job, existing))
            _jobs.Remove(job.Work.ContentKey);
    }
    private void TrimCompletedLocked()
    {
        var stale = _jobs.Where(p => p.Value.Completed && p.Value.Subscribers.Count == 0).Select(p => p.Key).ToArray();
        foreach (var key in stale.SkipLast(4)) _jobs.Remove(key);
    }
    internal void Cancel(SkirtBakeLease lease)
    {
        lock (_gate)
        {
            if (lease.Cancelled) return;
            lease.Cancelled = true; _resolving.Remove(lease); lease.ResolutionCancellation.Cancel();
            lease.Publish(new("Cancelled", Message: "Slot released its physics bake subscription.", ContentKey: lease.Job?.Work.ContentKey));
            if (lease.Job is { } job)
            {
                job.Subscribers.Remove(lease);
                if (job.Subscribers.Count == 0 && !job.Completed) { RemoveJobLocked(job); job.Cancellation.Cancel(); }
                TrimCompletedLocked();
            }
        }
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var lease in _resolving) { lease.Cancelled = true; lease.ResolutionCancellation.Cancel(); lease.Publish(new("Cancelled", Message: "Plugin is unloading.")); }
            _resolving.Clear();
            foreach (var job in _jobs.Values)
            {
                foreach (var lease in job.Subscribers) { lease.Cancelled = true; lease.Publish(new("Cancelled", Message: "Plugin is unloading.", ContentKey: job.Work.ContentKey)); }
                job.Subscribers.Clear(); job.Cancellation.Cancel();
            }
            _jobs.Clear(); _shutdown.Cancel();
        }
        // Never block the framework disposal thread on native work or file IO.
    }
}

public sealed class BuiltinSkirtBakeWorker : ISkirtBakeWorker
{
    public async Task<SkirtBakeWorkerResult> RunAsync(SkirtBakeWork work, Action<SkirtBakeProgress> progress,
        CancellationToken cancellationToken)
    {
        try
        {
            var document = new BuiltinSkirtBaker().Bake(work.Animation, work.Reference, work.MotionSha256,
                work.Request.Recipe, progress, cancellationToken, work.Request.LibraryPath);
            cancellationToken.ThrowIfCancellationRequested();
            await File.WriteAllBytesAsync(work.OutputPath, SkirtBakeCache.Serialize(document), cancellationToken).ConfigureAwait(false);
            return new(0, "Built-in Bullet skirt bake completed.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { return new(1, e.ToString()); }
    }
}
