using FFMMD.Posing;
using FFMMD.Retarget;
using FFXIVClientStructs.Havok.Animation.Rig;

namespace FFMMD.Skirt;

/// <summary>Native hook copies only already prepared skirt rotations.</summary>
public static unsafe class SkirtBakeWriter
{
    public static void Apply(hkaPose* pose, SkirtBakeBinding binding)
    {
        if (pose == null || pose->Skeleton == null || (nint)pose->Skeleton != binding.Tree.SkeletonPtr ||
            pose->LocalPose.Length != binding.Tree.BoneCount || pose->LocalPose.Data == null) return;
        pose->SyncLocalSpace();
        for (var i = 0; i < binding.BoneIndices.Length; i++)
            NativePoseWriter.Apply(pose->AccessBoneLocalSpace(binding.BoneIndices[i]),
                new RotationTranslationWrite(binding.LocalRotations[i], default, true, false));
        pose->SyncModelSpace();
    }
}
