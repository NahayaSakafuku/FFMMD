using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FFMMD.Skirt;

/// <summary>
/// Bounded, pure-managed reader for a fixed-step skirt bake produced by the
/// offline fixed-step physics pipeline. The cache contains relative model-space
/// rotations only; it never invokes native physics or writes a game pose.
/// </summary>
public static class SkirtBakeCache
{
    public const int SchemaVersion = 1;
    public const int RequiredBoneCount = 18;
    public const int RequiredFps = 30;
    public const int MaxFileBytes = 64 * 1024 * 1024;
    public const int MaxFrames = 100_000;
    public const long MaxRotationSamples = 2_000_000;

    /// <summary>Canonical output order used by Loaded.Sample.</summary>
    public static readonly string[] RequiredTargetNames =
    [
        "j_sk_b_a_l", "j_sk_b_a_r", "j_sk_f_a_l", "j_sk_f_a_r", "j_sk_s_a_l", "j_sk_s_a_r",
        "j_sk_b_b_l", "j_sk_b_b_r", "j_sk_f_b_l", "j_sk_f_b_r", "j_sk_s_b_l", "j_sk_s_b_r",
        "j_sk_b_c_l", "j_sk_b_c_r", "j_sk_f_c_l", "j_sk_f_c_r", "j_sk_s_c_l", "j_sk_s_c_r",
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.Strict,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
    };

    public static byte[] Serialize(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
    }

    public static LoadResult ReadFile(string path, string expectedMotionSha256, string? expectedReferencePmxSha256 = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return Reject("Path is empty.");
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length > MaxFileBytes) return Reject($"Cache file exceeds {MaxFileBytes} bytes.");
            var bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() >= 0) return Reject("Cache file changed length while being read.");
            return Read(bytes, expectedMotionSha256, expectedReferencePmxSha256);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Reject($"Could not read cache file: {e.Message}");
        }
    }

    public static LoadResult Read(ReadOnlySpan<byte> data, string expectedMotionSha256, string? expectedReferencePmxSha256 = null)
    {
        if (data.Length == 0) return Reject("Cache is empty.");
        if (data.Length > MaxFileBytes) return Reject($"Cache payload exceeds {MaxFileBytes} bytes.");
        if (!IsHash(expectedMotionSha256) || (!string.IsNullOrWhiteSpace(expectedReferencePmxSha256) && !IsHash(expectedReferencePmxSha256)))
            return Reject("Expected motion SHA-256 and optional reference SHA-256 must be 64 hexadecimal characters.");

        try
        {
            // Bound structural allocations before asking the serializer to build
            // jagged rotation arrays, including when metadata appears last.
            var preflight = new Utf8JsonReader(data, new JsonReaderOptions { MaxDepth = 16 });
            long arrays = 0, numbers = 0;
            while (preflight.Read())
            {
                if (preflight.TokenType == JsonTokenType.StartArray && ++arrays > MaxRotationSamples + 128)
                    return Reject("Rotation array budget exceeded.");
                if (preflight.TokenType == JsonTokenType.Number && ++numbers > MaxRotationSamples * 4 + 128)
                    return Reject("Rotation component budget exceeded.");
            }
            var document = JsonSerializer.Deserialize<Document>(data, JsonOptions);
            if (document is null) return Reject("Cache JSON is null.");
            return Validate(document, expectedMotionSha256, expectedReferencePmxSha256);
        }
        catch (JsonException e) { return Reject($"Invalid cache JSON: {e.Message}"); }
        catch (NotSupportedException e) { return Reject($"Unsupported cache JSON: {e.Message}"); }
        catch (InvalidDataException e) { return Reject(e.Message); }
        catch (OverflowException) { return Reject("Cache number is outside the supported range."); }
    }

    private static LoadResult Validate(Document d, string expectedMotion, string? expectedReference)
    {
        if (d.SchemaVersion != SchemaVersion) return Reject($"Unsupported cache schema: {d.SchemaVersion}.");
        if (!HashEquals(d.MotionSha256, expectedMotion)) return Reject("MotionSha256 does not match the requested motion.");
        if (!IsHash(d.ReferencePmxSha256)) return Reject("ReferencePmxSha256 must be a SHA-256 value.");
        if (!string.IsNullOrWhiteSpace(expectedReference) && !HashEquals(d.ReferencePmxSha256, expectedReference)) return Reject("ReferencePmxSha256 does not match the requested PMX.");
        if (d.Fps != RequiredFps) return Reject($"Only {RequiredFps} FPS caches are supported.");
        if (d.StartFrame != 0) return Reject("StartFrame must be zero.");
        if (d.FrameCount <= 0 || d.FrameCount > MaxFrames) return Reject($"FrameCount must be in 1..{MaxFrames}.");
        var basisError = "";
        if (d.SourceBasis is null || !TryReadBasis(d.SourceBasis, out var basis, out basisError)) return Reject(basisError);
        if (d.Bones is null || d.Bones.Count != RequiredBoneCount) return Reject($"Cache must contain exactly {RequiredBoneCount} skirt mappings.");
        if ((long)d.FrameCount * d.Bones.Count > MaxRotationSamples) return Reject("Rotation sample budget exceeded.");
        var solverError = "";
        if (d.Solver is null || !ValidateSolver(d.Solver, out solverError)) return Reject(solverError);

        var byTarget = new Dictionary<string, BoneDocument>(StringComparer.Ordinal);
        var bySource = new HashSet<string>(StringComparer.Ordinal);
        foreach (var bone in d.Bones)
        {
            if (bone is null || string.IsNullOrWhiteSpace(bone.SourceName) || string.IsNullOrWhiteSpace(bone.TargetName) ||
                string.IsNullOrWhiteSpace(bone.ParentSourceName))
                return Reject("Every skirt mapping requires source, target, and parent names.");
            if (bone.SourceName.Length > 128 || bone.TargetName.Length > 128 || bone.ParentSourceName.Length > 128)
                return Reject("Skirt mapping names exceed 128 characters.");
            if (!byTarget.TryAdd(bone.TargetName, bone)) return Reject($"Duplicate target mapping: {bone.TargetName}.");
            if (!bySource.Add(bone.SourceName)) return Reject($"Duplicate source mapping: {bone.SourceName}.");
            if (bone.RelativeRotations is null || bone.RelativeRotations.Length != d.FrameCount)
                return Reject($"Mapping {bone.TargetName} must contain one rotation per frame.");
        }
        foreach (var target in RequiredTargetNames)
            if (!byTarget.ContainsKey(target)) return Reject($"Required target mapping is missing: {target}.");
        foreach (var target in RequiredTargetNames)
        {
            var bone = byTarget[target]; var parts = target.Split('_');
            if (parts[3] == "a")
            {
                if (bySource.Contains(bone.ParentSourceName!)) return Reject($"Root skirt mapping has a mapped skirt parent: {target}.");
            }
            else
            {
                parts[3] = parts[3] == "b" ? "a" : "b";
                var expectedParent = byTarget[string.Join('_', parts)].SourceName;
                if (!string.Equals(bone.ParentSourceName, expectedParent, StringComparison.Ordinal))
                    return Reject($"Skirt mapping parent topology is invalid: {target}.");
            }
        }

        var loaded = new Loaded(d, basis, RequiredTargetNames.Select(target => ToLoadedBone(byTarget[target], d.FrameCount)).ToArray());
        return new LoadResult(loaded, "Parsed", null);
    }

    private static LoadedBone ToLoadedBone(BoneDocument bone, int frameCount)
    {
        var rotations = new Quaternion[frameCount];
        for (var i = 0; i < frameCount; i++)
        {
            var values = bone.RelativeRotations![i];
            if (values is null || values.Length != 4 || values.Any(value => !float.IsFinite(value)))
                throw new InvalidDataException($"Mapping {bone.TargetName} contains an invalid quaternion.");
            var q = new Quaternion(values[0], values[1], values[2], values[3]);
            var length = q.Length();
            if (!float.IsFinite(length) || length < 1e-6f) throw new InvalidDataException($"Mapping {bone.TargetName} contains a zero quaternion.");
            rotations[i] = Quaternion.Normalize(q);
        }
        return new LoadedBone(bone.SourceName!, bone.TargetName!, bone.ParentSourceName!, rotations);
    }

    private static bool TryReadBasis(SourceBasisDocument basis, out Matrix4x4 matrix, out string error)
    {
        matrix = default; error = "";
        if (!TryVector(basis.Left, out var left) || !TryVector(basis.Up, out var up) || !TryVector(basis.Back, out var back))
        { error = "SourceBasis vectors must be finite three-component arrays."; return false; }
        const float tolerance = 1e-4f;
        if (MathF.Abs(left.LengthSquared() - 1f) > tolerance || MathF.Abs(up.LengthSquared() - 1f) > tolerance || MathF.Abs(back.LengthSquared() - 1f) > tolerance)
        { error = "SourceBasis vectors must be unit length."; return false; }
        if (MathF.Abs(Vector3.Dot(left, up)) > tolerance || MathF.Abs(Vector3.Dot(left, back)) > tolerance || MathF.Abs(Vector3.Dot(up, back)) > tolerance)
        { error = "SourceBasis vectors must be mutually orthogonal."; return false; }
        matrix = new Matrix4x4(left.X, left.Y, left.Z, 0, up.X, up.Y, up.Z, 0, back.X, back.Y, back.Z, 0, 0, 0, 0, 1);
        return true;
    }

    private static bool ValidateSolver(SolverDocument s, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(s.Engine)) { error = "Solver.Engine is required."; return false; }
        if (s.RecipeVersion >= 3 ? string.IsNullOrWhiteSpace(s.EngineVersion) :
            string.IsNullOrWhiteSpace(s.BlenderVersion) || string.IsNullOrWhiteSpace(s.AddonVersion))
        { error = "Solver engine version is required."; return false; }
        if (!s.PointCacheBaked) { error = "Solver.PointCacheBaked must be true for a physics cache."; return false; }
        if (s.Engine.Length > 128 || s.BlenderVersion?.Length > 128 || s.AddonVersion?.Length > 128 || s.EngineVersion?.Length > 128)
        { error = "Solver metadata exceeds 128 characters."; return false; }
        if (s.Substeps is < 1 or > 4096 || s.Iterations is < 1 or > 4096 || s.WarmupFrames is < 0 or > MaxFrames || !float.IsFinite(s.Scale) || s.Scale <= 0)
        { error = "Solver parameters are outside supported bounds."; return false; }
        if (s.RigidBodyCount is < 0 or > 100_000 || s.JointCount is < 0 or > 100_000 || s.NonCollisionConstraintCount is < 0 or > 100_000 || s.MotionMaxFrame is < 0 or >= MaxFrames ||
            s.CollisionMargin is { } margin && (!float.IsFinite(margin) || margin < 0) ||
            s.NonCollisionDistanceScale is { } distanceScale && (!float.IsFinite(distanceScale) || distanceScale < 0))
        { error = "Optional solver metadata is outside supported bounds."; return false; }
        return true;
    }

    private static bool TryVector(float[]? values, out Vector3 vector)
    {
        vector = default;
        if (values is null || values.Length != 3 || values.Any(value => !float.IsFinite(value))) return false;
        vector = new Vector3(values[0], values[1], values[2]);
        return vector.LengthSquared() > 1e-8f;
    }

    private static bool IsHash(string? hash) => hash is { Length: 64 } && hash.All(Uri.IsHexDigit);
    private static bool HashEquals(string? actual, string expected) => IsHash(actual) && string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    private static LoadResult Reject(string error) => new(null, "Rejected", error);

    public sealed class Loaded
    {
        private readonly LoadedBone[] _bones;
        internal Loaded(Document document, Matrix4x4 sourceBasis, LoadedBone[] bones)
        {
            Document = document; SourceBasis = sourceBasis; _bones = bones;
            FrameCount = document.FrameCount; MotionSha256 = document.MotionSha256!;
            ReferencePmxSha256 = document.ReferencePmxSha256!;
        }
        public Document Document { get; }
        public Matrix4x4 SourceBasis { get; }
        public IReadOnlyList<LoadedBone> Bones => _bones;
        public int FrameCount { get; }
        public string MotionSha256 { get; }
        public string ReferencePmxSha256 { get; }

        public void Sample(int frame, Span<Quaternion> output) => Sample((float)frame, output);
        public void Sample(float frame, Span<Quaternion> output)
        {
            if (output.Length < _bones.Length) throw new ArgumentException("Output quaternion span is too small.", nameof(output));
            var value = float.IsFinite(frame) ? Math.Clamp(frame, 0, FrameCount - 1) : 0;
            var first = Math.Min((int)MathF.Floor(value), FrameCount - 1);
            var second = Math.Min(first + 1, FrameCount - 1);
            var t = second == first ? 0 : value - first;
            for (var i = 0; i < _bones.Length; i++) output[i] = SlerpShortest(_bones[i].RelativeRotations[first], _bones[i].RelativeRotations[second], t);
        }

        public void SampleLoop(float frame, Span<Quaternion> output)
        {
            if (output.Length < _bones.Length) throw new ArgumentException("Output quaternion span is too small.", nameof(output));
            if (FrameCount == 1) { Sample(0, output); return; }
            if (!float.IsFinite(frame)) { Sample(0, output); return; }
            var period = FrameCount;
            var wrapped = frame % period;
            if (wrapped < 0) wrapped += period;
            var first = (int)MathF.Floor(wrapped);
            var second = (first + 1) % period;
            var t = wrapped - first;
            for (var i = 0; i < _bones.Length; i++) output[i] = SlerpShortest(_bones[i].RelativeRotations[first], _bones[i].RelativeRotations[second], t);
        }

        private static Quaternion SlerpShortest(Quaternion a, Quaternion b, float t)
        {
            if (Quaternion.Dot(a, b) < 0) b = new Quaternion(-b.X, -b.Y, -b.Z, -b.W);
            var result = Quaternion.Slerp(a, b, Math.Clamp(t, 0, 1));
            return result.LengthSquared() > 1e-12f ? Quaternion.Normalize(result) : Quaternion.Identity;
        }
    }

    public sealed class LoadedBone
    {
        internal LoadedBone(string sourceName, string targetName, string parentSourceName, Quaternion[] rotations)
        { SourceName = sourceName; TargetName = targetName; ParentSourceName = parentSourceName; RelativeRotations = rotations; }
        public string SourceName { get; }
        public string TargetName { get; }
        public string ParentSourceName { get; }
        public IReadOnlyList<Quaternion> RelativeRotations { get; }
    }

    public sealed record LoadResult(Loaded? Cache, string Status, string? Error)
    {
        public bool Success => Cache is not null && Status == "Parsed";
    }

    public sealed class Document
    {
        [JsonRequired]
        public int SchemaVersion { get; set; } = SchemaVersionConst;
        [JsonRequired]
        public string? MotionSha256 { get; set; }
        [JsonRequired]
        public string? ReferencePmxSha256 { get; set; }
        [JsonRequired]
        public float Fps { get; set; } = RequiredFps;
        [JsonRequired]
        public int StartFrame { get; set; }
        [JsonRequired]
        public int FrameCount { get; set; }
        [JsonRequired]
        public SourceBasisDocument SourceBasis { get; set; } = new();
        [JsonRequired]
        public List<BoneDocument> Bones { get; set; } = [];
        [JsonRequired]
        public SolverDocument Solver { get; set; } = new();
    }

    private const int SchemaVersionConst = 1;

    public sealed class SourceBasisDocument
    {
        [JsonRequired]
        public float[] Left { get; set; } = [1, 0, 0];
        [JsonRequired]
        public float[] Up { get; set; } = [0, 1, 0];
        [JsonRequired]
        public float[] Back { get; set; } = [0, 0, 1];
    }

    public sealed class BoneDocument
    {
        [JsonRequired]
        public string? SourceName { get; set; }
        [JsonRequired]
        public string? TargetName { get; set; }
        [JsonRequired]
        public string? ParentSourceName { get; set; }
        [JsonRequired]
        public float[][]? RelativeRotations { get; set; }
    }

    public sealed class SolverDocument
    {
        [JsonRequired]
        public string? Engine { get; set; }
        public string? BlenderVersion { get; set; }
        public string? AddonVersion { get; set; }
        public string? EngineVersion { get; set; }
        [JsonRequired]
        public int Substeps { get; set; }
        [JsonRequired]
        public int Iterations { get; set; }
        [JsonRequired]
        public int WarmupFrames { get; set; }
        [JsonRequired]
        public float Scale { get; set; }
        [JsonRequired]
        public bool PointCacheBaked { get; set; }
        public int? RigidBodyCount { get; set; }
        public int? JointCount { get; set; }
        public int? NonCollisionConstraintCount { get; set; }
        public string? KinematicDrivingAlgorithm { get; set; }
        public float? CollisionMargin { get; set; }
        public float? NonCollisionDistanceScale { get; set; }
        public int? MotionMaxFrame { get; set; }
        public int RecipeVersion { get; set; }
        public SmoothingDocument? Smoothing { get; set; }
    }

    public sealed class SmoothingDocument
    {
        public string? Algorithm { get; set; }
        public int WindowFrames { get; set; }
        public float MaximumCorrectionDegrees { get; set; }
        public float Strength { get; set; }
        public string? CollisionGuard { get; set; }
        [JsonExtensionData]
        public Dictionary<string, JsonElement>? Statistics { get; set; }
    }
}
