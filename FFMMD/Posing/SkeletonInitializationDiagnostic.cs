using System.Globalization;
using System.Numerics;

namespace FFMMD.Posing;

public sealed class SkeletonInitializationBone
{
    public int Index, ParentIndex;
    public string Name = "", ParentName = "";
    public string[] InvalidFields = [];
    public string[] ReferenceLocalPosition = [], ReferenceLocalScale = [], ReferenceLocalRotation = [];
    public string[] CurrentLocalPosition = [], CurrentLocalScale = [], CurrentLocalRotation = [];
    public string[] EffectiveLocalPosition = [], EffectiveLocalScale = [];
}

public sealed class SkeletonInitializationDiagnostic
{
    public const int MaximumRecordedBones = 16;
    public int SchemaVersion = 1;
    public string Kind = "SkeletonInitializationFailure";
    public string PluginVersion = typeof(SkeletonInitializationDiagnostic).Assembly.GetName().Version?.ToString() ?? "unknown";
    public string CapturedUtc = DateTime.UtcNow.ToString("O"), Stage = "EffectiveTargetProfile";
    public int Slot, TargetObjectIndex, BoneCount, InvalidBoneCount;
    public string TargetName = "", TargetId = "", MotionPath = "";
    public double Frame;
    public bool AnimationScaleWritesAllowed = false;
    public string Note = "Captured after SyncLocalSpace, before target profile construction or pose writes. Effective rotation is the reference rotation. Root and n_hara positions retain reference values. ManagedReferenceAdjustments lists any suppressed breast leaf reference substitutions; other effective scales retain current values. Float components are invariant strings, preserving NaN/Infinity. No native transform or scale write was applied.";
    public SkeletonInitializationBone[] Bones = [];
    public Retarget.SuppressedBreastBoneAdjustment[] ManagedReferenceAdjustments = [];

    public static SkeletonInitializationDiagnostic? Inspect(SkeletonTree reference, Vector3[] currentPositions,
        Vector3[] currentScales, Quaternion[] currentRotations, Vector3[] effectivePositions, Vector3[]? effectiveScales = null)
    {
        var count = reference.Names.Length;
        if (reference.Parent.Length != count || reference.RefLocalPos.Length != count ||
            reference.RefLocalScale.Length != count || reference.RefLocalRot.Length != count ||
            currentPositions.Length != count || currentScales.Length != count || currentRotations.Length != count ||
            effectivePositions.Length != count || effectiveScales != null && effectiveScales.Length != count)
            throw new ArgumentException("Skeleton preflight array lengths do not match.");
        var invalidCount = 0;
        List<SkeletonInitializationBone>? bones = null;
        for (var i = 0; i < count; i++)
        {
            var position = effectivePositions[i];
            var scale = (effectiveScales ?? currentScales)[i];
            var rotation = reference.RefLocalRot[i];
            if (SkeletonTree.Finite(position) && SkeletonTree.Finite(scale) &&
                scale.X > 1e-6f && scale.Y > 1e-6f && scale.Z > 1e-6f &&
                SkeletonTree.Finite(rotation) && rotation.LengthSquared() >= 1e-12f) continue;
            invalidCount++;
            bones ??= [];
            if (bones.Count == MaximumRecordedBones) continue;
            var fields = new List<string>();
            CheckPosition("X", position.X); CheckPosition("Y", position.Y); CheckPosition("Z", position.Z);
            CheckScale("X", scale.X); CheckScale("Y", scale.Y); CheckScale("Z", scale.Z);
            if (!SkeletonTree.Finite(rotation)) fields.Add("ReferenceLocalRotation: non-finite component");
            else if (rotation.LengthSquared() < 1e-12f) fields.Add("ReferenceLocalRotation: length squared < 1e-12");
            var parent = reference.Parent[i];
            bones.Add(new()
            {
                Index = i, Name = reference.Names[i], ParentIndex = parent,
                ParentName = parent >= 0 && parent < count ? reference.Names[parent] : "",
                InvalidFields = fields.ToArray(),
                ReferenceLocalPosition = Components(reference.RefLocalPos[i]),
                ReferenceLocalScale = Components(reference.RefLocalScale[i]),
                ReferenceLocalRotation = Components(rotation),
                CurrentLocalPosition = Components(currentPositions[i]),
                CurrentLocalScale = Components(currentScales[i]),
                EffectiveLocalScale = Components(scale),
                CurrentLocalRotation = Components(currentRotations[i]),
                EffectiveLocalPosition = Components(position),
            });
            void CheckPosition(string axis, float value)
            {
                if (!float.IsFinite(value)) fields.Add($"EffectiveLocalPosition.{axis}: non-finite");
            }
            void CheckScale(string axis, float value)
            {
                var field = scale.Equals(currentScales[i]) ? "CurrentLocalScale" : "EffectiveLocalScale";
                if (!float.IsFinite(value)) fields.Add($"{field}.{axis}: non-finite");
                else if (value <= 1e-6f) fields.Add($"{field}.{axis}: <= 1e-6");
            }
        }
        return bones == null ? null : new() { BoneCount = count, InvalidBoneCount = invalidCount, Bones = bones.ToArray() };
    }

    internal static string[] Components(Vector3 value) => [Number(value.X), Number(value.Y), Number(value.Z)];
    internal static string[] Components(Quaternion value) => [Number(value.X), Number(value.Y), Number(value.Z), Number(value.W)];
    private static string Number(float value) => value.ToString("R", CultureInfo.InvariantCulture);
}

public sealed class SkeletonInitializationException(SkeletonInitializationDiagnostic diagnostic)
    : Exception($"Skeleton initialization rejected {diagnostic.InvalidBoneCount} bone(s); first: " +
        $"[{diagnostic.Bones[0].Index}] {diagnostic.Bones[0].Name}, {string.Join("; ", diagnostic.Bones[0].InvalidFields)}, " +
        $"reference scale=({string.Join(", ", diagnostic.Bones[0].ReferenceLocalScale)}), " +
        $"current scale=({string.Join(", ", diagnostic.Bones[0].CurrentLocalScale)}), " +
        $"current position=({string.Join(", ", diagnostic.Bones[0].CurrentLocalPosition)}), " +
        $"effective position=({string.Join(", ", diagnostic.Bones[0].EffectiveLocalPosition)}).")
{
    public SkeletonInitializationDiagnostic Diagnostic { get; } = diagnostic;
}
