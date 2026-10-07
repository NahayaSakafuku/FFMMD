using System.Numerics;

namespace FFMMD.Retarget;

public sealed partial class Retargeter
{
    private int[] _orientationSource=[],_fingerHand=[],_fingerSourceHand=[];
    private Quaternion[] _fingerCalibration=[];
    private readonly int[] _upperArm=[-1,-1],_forearm=[-1,-1],_upperHelper=[-1,-1],_elbowHelper=[-1,-1];

    private void InitializeUpperBody()
    {
        var tree=Tree!;var rig=SourceRig!;var n=tree.BoneCount;
        _orientationSource=(int[])_targetToSource.Clone();
        _fingerHand=Enumerable.Repeat(-1,n).ToArray();_fingerSourceHand=Enumerable.Repeat(-1,n).ToArray();
        _fingerCalibration=Enumerable.Repeat(Quaternion.Identity,n).ToArray();
        bool SourceAncestor(int ancestor,int child)
        {for(var i=child;i>=0;i=rig.Bones[i].Parent)if(i==ancestor)return true;return false;}
        bool IsLeaf(int bone)=>bone>=0&&!tree.Parent.Contains((short)bone);
        foreach(var side in new[]{"l","r"})
        {
            var s=side=="l"?0:1;var jp=s==0?"左":"右";
            _upperArm[s]=tree.Find("j_ude_a_"+side);_forearm[s]=tree.Find("j_ude_b_"+side);
            var hand=tree.Find("j_te_"+side);var sourceHand=rig.Find(jp+"手首");
            var upperTwist=rig.Find(jp+"腕捩");var lowerTwist=rig.Find(jp+"手捩");
            void UseOrientation(int target,int twist,int end)
            {
                if(target<0||twist<0||end<0||_targetToSource[target]<0)return;
                if(!SourceAncestor(_targetToSource[target],twist)||!SourceAncestor(twist,end))return;
                _orientationSource[target]=twist;
                var binding=Mapped.First(m=>m.FfIndex==target);binding.OrientationSourceIndex=twist;
            }
            var sourceElbow=_forearm[s]>=0?_targetToSource[_forearm[s]]:-1;
            UseOrientation(_upperArm[s],upperTwist,sourceElbow);UseOrientation(_forearm[s],lowerTwist,sourceHand);
            _upperHelper[s]=tree.Find("n_hkata_"+side);_elbowHelper[s]=tree.Find("n_hhiji_"+side);
            // These FF14 bones are shape corrections, not MMD twist parents.
            if(!IsLeaf(_upperHelper[s])||tree.Parent[_upperHelper[s]]!=_upperArm[s])_upperHelper[s]=-1;
            if(!IsLeaf(_elbowHelper[s])||tree.Parent[_elbowHelper[s]]!=_forearm[s])_elbowHelper[s]=-1;
            if(_upperHelper[s]>=0)_writes[_upperHelper[s]]=true;
            if(_elbowHelper[s]>=0)_writes[_elbowHelper[s]]=true;
            if(hand<0||sourceHand<0)continue;
            var sourceMiddle=rig.Find(jp+"中指1");var sourceTip=rig.Find(jp+"中指3");
            var sourceIndex=rig.Find(jp+"人差指1");var sourceLittle=rig.Find(jp+"小指1");
            var targetMiddle=tree.Find("j_naka_a_"+side);var targetTip=tree.Find("j_naka_b_"+side);
            var targetIndex=tree.Find("j_hito_a_"+side);var targetLittle=tree.Find("j_ko_a_"+side);
            var sourceNormal=Vector3.UnitZ;var targetNormal=_baseMap.Vector(sourceNormal)*_baseMap.Parity;
            if(sourceMiddle>=0&&sourceTip>=0&&sourceIndex>=0&&sourceLittle>=0)
                sourceNormal=RigMath.Direction(Vector3.Cross(rig.Bones[sourceTip].RestPosition-rig.Bones[sourceMiddle].RestPosition,
                    rig.Bones[sourceIndex].RestPosition-rig.Bones[sourceLittle].RestPosition),sourceNormal);
            if(targetMiddle>=0&&targetTip>=0&&targetIndex>=0&&targetLittle>=0)
                targetNormal=RigMath.Direction(Vector3.Cross(tree.RefModelPos[targetTip]-tree.RefModelPos[targetMiddle],
                    tree.RefModelPos[targetIndex]-tree.RefModelPos[targetLittle]),targetNormal);
            var mappedNormal=_baseMap.Vector(sourceNormal)*_baseMap.Parity;
            if(Vector3.Dot(targetNormal,mappedNormal)<0)targetNormal=-targetNormal;
            foreach(var m in Mapped)
            {
                if(m.FfIndex==hand||!DescendsFrom(m.FfIndex,hand)||!SourceAncestor(sourceHand,m.SourceIndex))continue;
                if(!m.FfName.StartsWith("j_oya_",StringComparison.Ordinal)&&!m.FfName.StartsWith("j_hito_",StringComparison.Ordinal)&&
                    !m.FfName.StartsWith("j_naka_",StringComparison.Ordinal)&&!m.FfName.StartsWith("j_kusu_",StringComparison.Ordinal)&&!m.FfName.StartsWith("j_ko_",StringComparison.Ordinal))continue;
                var sourceChild=Array.FindIndex(rig.Bones,b=>b.Parent==m.SourceIndex);
                var sourceParent=rig.Bones[m.SourceIndex].Parent;
                var sd=sourceChild>=0?rig.Bones[sourceChild].RestPosition-rig.Bones[m.SourceIndex].RestPosition:
                    sourceParent>=0?rig.Bones[m.SourceIndex].RestPosition-rig.Bones[sourceParent].RestPosition:Vector3.UnitX;
                var targetChild=Array.FindIndex(tree.Parent,p=>p==m.FfIndex);var targetParent=tree.Parent[m.FfIndex];
                var td=targetChild>=0?tree.RefModelPos[targetChild]-tree.RefModelPos[m.FfIndex]:tree.RefModelPos[m.FfIndex]-tree.RefModelPos[targetParent];
                var axis=RigMath.Direction(td,Vector3.UnitX);var correction=RigMath.FromTo(_baseMap.Vector(sd),axis);
                var from=Vector3.Transform(mappedNormal,correction);from-=axis*Vector3.Dot(from,axis);
                var to=targetNormal-axis*Vector3.Dot(targetNormal,axis);
                from=RigMath.Direction(from,RigMath.Orthogonal(axis));to=RigMath.Direction(to,from);
                var angle=MathF.Atan2(Vector3.Dot(axis,Vector3.Cross(from,to)),Vector3.Dot(from,to));
                _fingerCalibration[m.FfIndex]=Quaternion.Normalize(Quaternion.CreateFromAxisAngle(axis,angle)*correction);
                _fingerHand[m.FfIndex]=hand;_fingerSourceHand[m.FfIndex]=sourceHand;
            }
        }
        InitializeFourFingers();
    }

    private void UpdateArmCorrections(float amplitude)
    {
        if(amplitude<=0)return;
        var tree=Tree!;
        for(var side=0;side<2;side++)
        {
            var upper=_upperArm[side];var lower=_forearm[side];var helper=_upperHelper[side];
            if(upper<0||lower<0)continue;
            if(helper>=0)
            {
                // Counter only axial upper-arm twist; never borrow a control bone
                // orientation or scale for a skin-volume correction.
                var delta=Quaternion.Normalize(_rotLocPose[upper]*Quaternion.Conjugate(tree.RefLocalRot[upper]));
                var axis=Vector3.Transform(RigMath.Direction(tree.RefLocalPos[lower],Vector3.UnitX),tree.RefLocalRot[upper]);
                var v=new Vector3(delta.X,delta.Y,delta.Z);var q=new Quaternion(axis*Vector3.Dot(v,axis),delta.W);
                var twist=q.LengthSquared()<1e-12f?Quaternion.Identity:Quaternion.Normalize(q);
                var localTwist=Quaternion.Conjugate(_rotLocPose[upper])*twist*_rotLocPose[upper];
                _rotLocPose[helper]=Quaternion.Normalize(RigMath.Power(localTwist,-.5f)*tree.RefLocalRot[helper]);
            }
            helper=_elbowHelper[side];
            if(helper>=0&&tree.Parent[lower]==upper)
            {
                var delta=Quaternion.Normalize(_rotLocPose[lower]*Quaternion.Conjugate(tree.RefLocalRot[lower]));
                var counter=Quaternion.Conjugate(_rotLocPose[lower])*RigMath.Power(delta,-.5f)*_rotLocPose[lower];
                _rotLocPose[helper]=Quaternion.Normalize(counter*tree.RefLocalRot[helper]);
            }
        }
    }
    private bool IsArmHelper(int bone)=>bone==_upperHelper[0]||bone==_upperHelper[1]||bone==_elbowHelper[0]||bone==_elbowHelper[1];

    private void UpdateFingers(float amplitude)
    {
        // One topological pass preserves the native thumb rest orientation and
        // collapses source joints in hand space without twisting the whole arm.
        var pose=SourceSolver!.Pose;
        for(var i=0;i<Tree!.BoneCount;i++)
        {
            var parent=Tree.Parent[i];var hand=_fingerHand[i];
            if(hand>=0)
            {
                if(_fourFinger[i])
                {
                    _rotLocPose[i]=FourFingerLocalRotation(i,amplitude);
                }
                else
                {
                    var relative=Quaternion.Normalize(Quaternion.Conjugate(pose.Rotations[_fingerSourceHand[i]])*pose.Rotations[_targetToSource[i]]);
                    var calibration=_fingerCalibration[i];
                    var delta=Quaternion.Normalize(calibration*_baseMap.Rotation(relative)*Quaternion.Conjugate(calibration));
                    var model=Quaternion.Normalize(_rot[hand]*Quaternion.Conjugate(Tree.RefModelRot[hand])*RigMath.Power(delta,amplitude)*Tree.RefModelRot[i]);
                    _rotLocPose[i]=Quaternion.Normalize((parent<0?Quaternion.Identity:Quaternion.Conjugate(_rot[parent]))*model);
                }
            }
            _rot[i]=parent<0?_rotLocPose[i]:Quaternion.Normalize(_rot[parent]*_rotLocPose[i]);
            _pos[i]=parent<0?_posLocPose[i]:_pos[parent]+Vector3.Transform(_posLocPose[i]*Tree.RefModelScale[parent],_rot[parent]);
        }
    }
}
