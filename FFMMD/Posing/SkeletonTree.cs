using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.Havok.Animation.Rig;
using FFXIVClientStructs.Havok.Common.Base.Math.QsTransform;

namespace FFMMD.Posing;

/// <summary>
/// 包装一个 hkaPose 的骨架：骨名、父子关系、参考姿势（局部/模型空间）。
/// FF14 的 Render::Skeleton + Havok hkaSkeleton 是权威数据，全部经 FFXIVClientStructs 结构访问。
/// </summary>
public sealed unsafe partial class SkeletonTree
{
    public static SkeletonTree? Build(hkaPose* pose)
    {
        if (pose == null) return null;
        var skel = pose->Skeleton;
        if (skel == null) return null;

        var boneCount = skel->Bones.Length;
        var parentCount = skel->ParentIndices.Length;
        var refCount = skel->ReferencePose.Length;
        if (boneCount <= 0 || parentCount != boneCount || refCount != boneCount ||
            pose->LocalPose.Length != boneCount || pose->ModelPose.Length != boneCount ||
            skel->Bones.Data == null || skel->ParentIndices.Data == null || skel->ReferencePose.Data == null ||
            pose->LocalPose.Data == null || pose->ModelPose.Data == null) return null;

        var tree = new SkeletonTree
        {
            SkeletonPtr = (nint)skel,
            BoneCount = boneCount,
            Parent = new short[boneCount],
            Names = new string[boneCount],
            RefLocalRot = new Quaternion[boneCount],
            RefLocalPos = new Vector3[boneCount],
            RefLocalScale = new Vector3[boneCount],
            RefModelRot = new Quaternion[boneCount],
            RefModelPos = new Vector3[boneCount],
            RefModelScale = new Vector3[boneCount],
        };

        var parents = skel->ParentIndices.Data;
        var bones = skel->Bones.Data;
        var refs = skel->ReferencePose.Data;
        for (var i = 0; i < boneCount; i++)
        {
            tree.Parent[i] = parents[i];
            var name = bones[i].Name.String;
            tree.Names[i] = name ?? $"bone_{i}";
            tree.Index[tree.Names[i]] = i;

            var r = &refs[i];
            tree.RefLocalRot[i] = new Quaternion(r->Rotation.X, r->Rotation.Y, r->Rotation.Z, r->Rotation.W);
            tree.RefLocalPos[i] = new Vector3(r->Translation.X, r->Translation.Y, r->Translation.Z);
            tree.RefLocalScale[i] = new Vector3(r->Scale.X, r->Scale.Y, r->Scale.Z);
        }

        tree.RebuildReference();
        return tree;
    }
}
