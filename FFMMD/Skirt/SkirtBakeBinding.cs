using System.Numerics;
using FFMMD.Posing;
using FFMMD.Retarget;

namespace FFMMD.Skirt;

/// <summary>Transports baked parent-relative model deltas into native bind axes on Tick.</summary>
public sealed class SkirtBakeBinding
{
    public SkeletonTree Tree { get; }
    public SkirtBakeCache.Loaded Cache { get; }
    public int[] BoneIndices { get; }
    public Quaternion[] LocalRotations { get; }
    private readonly Quaternion[] _sample;
    private readonly RigCoordinateMap _map;

    private SkirtBakeBinding(SkirtBakeCache.Loaded cache, TargetRigProfile profile)
    {
        Cache = cache; Tree = profile.Skeleton;
        BoneIndices = cache.Bones.Select(b => Tree.Find(b.TargetName)).ToArray();
        if (BoneIndices.Any(i => i < 0)) throw new InvalidDataException("Target skeleton has missing baked skirt bones.");
        for (var i = 0; i < BoneIndices.Length; i++)
        {
            var bone = cache.Bones[i]; var parent = Tree.Parent[BoneIndices[i]];
            var parentSource = cache.Bones.FirstOrDefault(b => b.SourceName == bone.ParentSourceName);
            if (parentSource != null && (parent < 0 || Tree.Names[parent] != parentSource.TargetName))
                throw new InvalidDataException("Target skirt chain does not match the cache topology.");
        }
        LocalRotations = new Quaternion[BoneIndices.Length]; _sample = new Quaternion[BoneIndices.Length];
        var targetBasis = new RigCoordinateMap(profile.Left, profile.Up, profile.Back).Matrix;
        var matrix = Matrix4x4.Transpose(cache.SourceBasis) * targetBasis;
        _map = new RigCoordinateMap(new(matrix.M11, matrix.M12, matrix.M13),
            new(matrix.M21, matrix.M22, matrix.M23), new(matrix.M31, matrix.M32, matrix.M33));
    }

    public static SkirtBakeBinding Build(SkirtBakeCache.Loaded cache, TargetRigProfile profile) => new(cache, profile);

    public bool Prepare(float frame, float amplitude)
    {
        if (!float.IsFinite(frame) || !float.IsFinite(amplitude)) return false;
        Cache.Sample(frame, _sample);
        for (var i = 0; i < BoneIndices.Length; i++)
        {
            var bone = BoneIndices[i]; var parent = Tree.Parent[bone];
            var parentBind = parent < 0 ? Quaternion.Identity : Tree.RefModelRot[parent];
            var delta = RigMath.Power(_map.Rotation(_sample[i]), Math.Clamp(amplitude, 0, 1));
            LocalRotations[i] = Quaternion.Normalize(Quaternion.Conjugate(parentBind) * delta * Tree.RefModelRot[bone]);
        }
        return true;
    }
}
