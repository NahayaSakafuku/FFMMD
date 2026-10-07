using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.Havok.Animation.Rig;
using FFMMD.Retarget;

namespace FFMMD.Posing;

/// <summary>Connects secondary Havok roots to the already synchronized body pose.</summary>
public sealed unsafe class PartialPoseBridge
{
    private sealed class Part
    {
        public int Index;
        public nint Skeleton;
        public int BoneCount;
        public short ConnectedParent, ConnectedBone;
        public PartialRootConnection[] Roots=[];
    }
    private nint _renderSkeleton, _bodySkeleton;
    private int _count;
    private Part[] _parts=[];
    public delegate PoseSnapshot CapturePose(hkaPose* pose);

    private static bool Valid(hkaPose* pose) => pose!=null&&pose->Skeleton!=null&&
        pose->Skeleton->Bones.Length>0&&pose->Skeleton->ParentIndices.Length==pose->Skeleton->Bones.Length&&
        pose->Skeleton->Bones.Data!=null&&pose->Skeleton->ParentIndices.Data!=null&&
        pose->LocalPose.Length==pose->Skeleton->Bones.Length&&pose->ModelPose.Length==pose->Skeleton->Bones.Length&&
        pose->LocalPose.Data!=null&&pose->ModelPose.Data!=null;

    public void Prepare(Skeleton* skeleton, SkeletonTree body)
    {
        if(skeleton==null||skeleton->PartialSkeletons==null||skeleton->PartialSkeletonCount<1){Clear();return;}
        if(Matches(skeleton))return;
        var parts=new List<Part>();
        for(var i=1;i<skeleton->PartialSkeletonCount;i++)
        {
            var partial=&skeleton->PartialSkeletons[i];var pose=partial->GetHavokPose(0);
            if(!Valid(pose))continue;
            var roots=new List<int>();
            for(var b=0;b<pose->Skeleton->Bones.Length;b++)if(pose->Skeleton->ParentIndices.Data[b]<0)roots.Add(b);
            var connections=new List<PartialRootConnection>();
            if(roots.Count==1)
            {
                var parent=partial->ConnectedParentBoneIndex;var child=partial->ConnectedBoneIndex;
                if(parent>=0&&parent<body.BoneCount&&child==roots[0])connections.Add(new(){ChildBone=child,BodyBone=parent});
            }
            else foreach(var root in roots)
            {
                var name=pose->Skeleton->Bones.Data[root].Name.String;
                if(name!=null&&body.Index.TryGetValue(name,out var parent))connections.Add(new(){ChildBone=root,BodyBone=parent});
            }
            parts.Add(new(){Index=i,Skeleton=(nint)pose->Skeleton,BoneCount=pose->Skeleton->Bones.Length,
                ConnectedParent=partial->ConnectedParentBoneIndex,ConnectedBone=partial->ConnectedBoneIndex,Roots=connections.ToArray()});
        }
        _parts=parts.ToArray();_renderSkeleton=(nint)skeleton;_count=skeleton->PartialSkeletonCount;
        _bodySkeleton=body.SkeletonPtr;
    }

    private bool Matches(Skeleton* skeleton)
    {
        if(skeleton==null||(nint)skeleton!=_renderSkeleton||skeleton->PartialSkeletons==null||skeleton->PartialSkeletonCount!=_count)return false;
        var body=skeleton->PartialSkeletons[0].GetHavokPose(0);
        if(!Valid(body)||(nint)body->Skeleton!=_bodySkeleton)return false;
        // Also detect a previously absent secondary pose becoming available.
        var available=0;
        for(var i=1;i<_count;i++)if(Valid(skeleton->PartialSkeletons[i].GetHavokPose(0)))available++;
        if(available!=_parts.Length)return false;
        foreach(var part in _parts)
        {
            var partial=&skeleton->PartialSkeletons[part.Index];var pose=partial->GetHavokPose(0);
            if(!Valid(pose)||(nint)pose->Skeleton!=part.Skeleton||pose->Skeleton->Bones.Length!=part.BoneCount||
                partial->ConnectedParentBoneIndex!=part.ConnectedParent||partial->ConnectedBoneIndex!=part.ConnectedBone)return false;
        }
        return true;
    }

    public void Apply(Skeleton* skeleton, hkaPose* body)
    {
        if(!Matches(skeleton)||!Valid(body)||(nint)body->Skeleton!=_bodySkeleton)return;
        body->SyncModelSpace();
        foreach(var part in _parts)
        {
            var pose=skeleton->PartialSkeletons[part.Index].GetHavokPose(0);
            pose->SyncModelSpace();
            foreach(var root in part.Roots)
            {
                var transform=pose->AccessBoneModelSpace(root.ChildBone,hkaPose.PropagateOrNot.Propagate);
                // Never copy a complete transform: native scale belongs to the
                // character, including each secondary root's independent scale.
                var source=body->ModelPose.Data[root.BodyBone];
                NativePoseWriter.Apply(transform,new(new(source.Rotation.X,source.Rotation.Y,source.Rotation.Z,source.Rotation.W),
                    new(source.Translation.X,source.Translation.Y,source.Translation.Z),true,true));
            }
            pose->SyncLocalSpace();pose->SyncModelSpace();
        }
    }

    public PartialPoseSnapshot[] Capture(Skeleton* skeleton, CapturePose capture)
    {
        if(!Matches(skeleton))return [];
        var result=new PartialPoseSnapshot[_parts.Length];
        for(var i=0;i<_parts.Length;i++)
        {
            var part=_parts[i];var pose=skeleton->PartialSkeletons[part.Index].GetHavokPose(0);
            var names=new string[part.BoneCount];
            var parents=new short[part.BoneCount];
            for(var b=0;b<names.Length;b++)names[b]=pose->Skeleton->Bones.Data[b].Name.String??$"bone_{b}";
            for(var b=0;b<parents.Length;b++)parents[b]=pose->Skeleton->ParentIndices.Data[b];
            result[i]=new(){PartialIndex=part.Index,Names=names,Parent=parents,Connections=part.Roots,Pose=capture(pose)};
        }
        return result;
    }
    public void Clear(){_parts=[];_renderSkeleton=_bodySkeleton=0;_count=0;}
}
