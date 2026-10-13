using System.Numerics;
using FFMMD.Posing;

namespace FFMMD.Retarget;

public sealed class SuppressedBreastBoneAdjustment
{
    public int Index;
    public string Name = "", Algorithm = "SuppressedBreastLeafReference";
    public Vector3 CurrentLocalPosition, CurrentLocalScale, ReferenceLocalPosition, ReferenceLocalScale;
    public bool NativeTransformPreserved = true;
}

public sealed class TargetRigDimensions
{
    public readonly Vector3[] LocalPositions, LocalScales;
    public readonly SuppressedBreastBoneAdjustment[] SuppressedBreastBones;
    public readonly int[] BreastLeafIndices;

    private TargetRigDimensions(Vector3[] positions, Vector3[] scales, SuppressedBreastBoneAdjustment[] adjustments, int[] breastLeaves)
    {
        LocalPositions = positions; LocalScales = scales; SuppressedBreastBones = adjustments; BreastLeafIndices = breastLeaves;
    }

    public static TargetRigDimensions Create(SkeletonTree binding, Vector3[]? positions = null, Vector3[]? scales = null)
    {
        var count = binding.Names.Length;
        if (binding.Parent.Length != count || binding.RefLocalPos.Length != count || binding.RefLocalScale.Length != count ||
            binding.RefLocalRot.Length != count || positions != null && positions.Length != count || scales != null && scales.Length != count)
            throw new InvalidDataException("骨架数组长度不一致。");
        var effectivePositions = (Vector3[])(positions ?? binding.RefLocalPos).Clone();
        var effectiveScales = (Vector3[])(scales ?? binding.RefLocalScale).Clone();
        List<SuppressedBreastBoneAdjustment>? adjustments = null;
        List<int>? breastLeaves = null;
        for (var i = 0; i < count; i++)
        {
            if (binding.Names[i] is not ("j_mune_l" or "j_mune_r")) continue;
            var parent = binding.Parent[i];
            if (parent < 0 || parent >= count || binding.Names[parent] != "j_sebo_b" ||
                Array.IndexOf(binding.Parent, (short)i) >= 0 ||
                BoneMap.Bindings.Any(map => map.Ff.Contains(binding.Names[i]))) continue;
            breastLeaves ??= []; breastLeaves.Add(i);
            var scale = effectiveScales[i];
            if (!Suppressed(scale) || !SkeletonTree.Finite(effectivePositions[i])) continue;
            var referenceScale = binding.RefLocalScale[i]; var referenceRotation = binding.RefLocalRot[i];
            if (!SkeletonTree.Finite(binding.RefLocalPos[i]) || !SkeletonTree.Finite(referenceScale) ||
                referenceScale.X <= 1e-6f || referenceScale.Y <= 1e-6f || referenceScale.Z <= 1e-6f ||
                !SkeletonTree.Finite(referenceRotation) || referenceRotation.LengthSquared() < 1e-12f) continue;
            adjustments ??= [];
            adjustments.Add(new()
            {
                Index = i, Name = binding.Names[i], CurrentLocalPosition = effectivePositions[i], CurrentLocalScale = scale,
                ReferenceLocalPosition = binding.RefLocalPos[i], ReferenceLocalScale = referenceScale,
            });
            // These unmapped leaves have no body descendants. Only the managed
            // reference geometry changes; native suppression remains untouched.
            effectivePositions[i] = binding.RefLocalPos[i]; effectiveScales[i] = referenceScale;
        }
        return new(effectivePositions, effectiveScales, adjustments?.ToArray() ?? [], breastLeaves?.ToArray() ?? []);
    }

    internal static bool MatchesBreastLeafState(Vector3 position, Vector3 scale, bool wasSuppressed) =>
        SkeletonTree.Finite(position) && (wasSuppressed ? Suppressed(scale) :
            SkeletonTree.Finite(scale) && scale.X > 1e-6f && scale.Y > 1e-6f && scale.Z > 1e-6f);

    private static bool Suppressed(Vector3 scale) => SkeletonTree.Finite(scale) &&
        scale.X >= 0 && scale.X <= 1e-6f && scale.Y >= 0 && scale.Y <= 1e-6f && scale.Z >= 0 && scale.Z <= 1e-6f;
}
