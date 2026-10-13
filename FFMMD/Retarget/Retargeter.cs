using FFMMD.Posing;
using FFXIVClientStructs.Havok.Animation.Rig;

namespace FFMMD.Retarget;

public sealed unsafe partial class Retargeter
{
    private nint _skeleton;
    public bool EnsureSkeleton(hkaPose* pose, Vmd.VmdAnimation? anim, Calibration cal)
    {
        if (pose == null || pose->Skeleton == null) return false;
        var ptr = (nint)pose->Skeleton;
        if (Tree != null && ptr == _skeleton && ReferenceEquals(_anim, anim) && pose->Skeleton->Bones.Length == Tree.BoneCount && Matches(pose) && DimensionsMatch(pose)) return true;
        var tree = SkeletonTree.Build(pose);
        if (tree == null) return false;
        pose->SyncLocalSpace();
        var positions=new System.Numerics.Vector3[tree.BoneCount];
        var currentPositions=new System.Numerics.Vector3[tree.BoneCount];
        var scales=new System.Numerics.Vector3[tree.BoneCount];
        var rotations=new System.Numerics.Quaternion[tree.BoneCount];
        for(var i=0;i<tree.BoneCount;i++)
        {
            var local=pose->LocalPose.Data[i];
            currentPositions[i]=positions[i]=new(local.Translation.X,local.Translation.Y,local.Translation.Z);
            scales[i]=new(local.Scale.X,local.Scale.Y,local.Scale.Z);
            rotations[i]=new(local.Rotation.X,local.Rotation.Y,local.Rotation.Z,local.Rotation.W);
            // Animated root translation is not a character-proportion calibration.
            if(tree.Parent[i]<0||tree.Names[i]=="n_hara")positions[i]=tree.RefLocalPos[i];
        }
        var dimensions=TargetRigDimensions.Create(tree,positions,scales);
        if(SkeletonInitializationDiagnostic.Inspect(tree,currentPositions,scales,rotations,dimensions.LocalPositions,dimensions.LocalScales) is { } failure)
        {
            failure.ManagedReferenceAdjustments=dimensions.SuppressedBreastBones;
            throw new SkeletonInitializationException(failure);
        }
        Initialize(new TargetRigProfile(tree,dimensions), anim, cal);
        _skeleton = ptr;
        return true;
    }

    public bool Matches(hkaPose* pose) => Tree != null && pose != null && (nint)pose->Skeleton == _skeleton &&
        pose->ModelPose.Length == Tree.BoneCount && pose->LocalPose.Length == Tree.BoneCount &&
        pose->ModelPose.Data != null && pose->LocalPose.Data != null;
    private bool DimensionsMatch(hkaPose* pose)
    {
        pose->SyncLocalSpace();
        var tree=Tree!;
        foreach(var i in Profile!.BreastLeafIndices)
        {
            var l=pose->LocalPose.Data[i];
            var position=new Vector3(l.Translation.X,l.Translation.Y,l.Translation.Z);
            var scale=new Vector3(l.Scale.X,l.Scale.Y,l.Scale.Z);
            var suppressed=false;
            foreach(var bone in Profile.SuppressedBreastBones)if(bone.Index==i){suppressed=true;break;}
            if(!TargetRigDimensions.MatchesBreastLeafState(position,scale,suppressed))return false;
        }
        for(var i=0;i<tree.BoneCount;i++)
        {
            if(!_writes[i]||IsArmHelper(i))continue;
            var l=pose->LocalPose.Data[i];
            var scale=new Vector3(l.Scale.X,l.Scale.Y,l.Scale.Z);
            if(Vector3.DistanceSquared(scale,tree.RefLocalScale[i])>1e-10f)return false;
            if(tree.Parent[i]<0||i==CenterBoneIndex)continue;
            var position=new Vector3(l.Translation.X,l.Translation.Y,l.Translation.Z);
            if(Vector3.DistanceSquared(position,tree.RefLocalPos[i])>1e-10f)return false;
        }
        return true;
    }
    public void CopyToPose(hkaPose* pose)
    {
        if (!Matches(pose)) return;
        var tree = Tree!;
        pose->SyncLocalSpace();
        for (var i = 0; i < tree.BoneCount; i++)
        {
            if(!_writes[i])continue;
            NativePoseWriter.Apply(pose->AccessBoneLocalSpace(i),GetBoneWrite(i));
        }
        // Havok owns dirty flags and the model-space cascade. Unmapped bones,
        // native translations and character scale never receive blanket writes.
        pose->SyncModelSpace();
    }
}
