using System.Numerics;

namespace FFMMD.Retarget;

public sealed partial class Retargeter
{
    private bool[] _fourFinger=[],_fingerProximal=[];
    private int[] _fingerMiddle=[],_fingerTip=[];
    private float[] _fingerCurlSign=[],_fingerSpreadSign=[],_sourceClosingSign=[],_middleLength=[],_tipLength=[];

    private void InitializeFourFingers()
    {
        var tree=Tree!;var rig=SourceRig!;var n=tree.BoneCount;
        _fourFinger=new bool[n];_fingerProximal=new bool[n];_fingerMiddle=Enumerable.Repeat(-1,n).ToArray();_fingerTip=Enumerable.Repeat(-1,n).ToArray();
        _fingerCurlSign=new float[n];_fingerSpreadSign=new float[n];_sourceClosingSign=new float[n];_middleLength=new float[n];_tipLength=new float[n];
        foreach(var m in Mapped)
        {
            var i=m.FfIndex;
            if(_fingerHand[i]<0||m.FfName.StartsWith("j_oya_",StringComparison.Ordinal))continue;
            _fourFinger[i]=true;_fingerProximal[i]=tree.Parent[i]==_fingerHand[i];
            _sourceClosingSign[i]=m.SourceJp.StartsWith("左",StringComparison.Ordinal)?-1:1;
            var suffix=m.FfName.EndsWith("_l",StringComparison.Ordinal)?"l":"r";
            var middleBase=tree.Find("j_naka_a_"+suffix);var middleEnd=tree.Find("j_naka_b_"+suffix);
            var index=tree.Find("j_hito_a_"+suffix);var little=tree.Find("j_ko_a_"+suffix);
            var palm=Vector3.Transform(-Vector3.UnitZ,tree.RefModelRot[_fingerHand[i]]);
            if(middleBase>=0&&middleEnd>=0&&index>=0&&little>=0)
                palm=RigMath.Direction(Vector3.Cross(tree.RefModelPos[middleEnd]-tree.RefModelPos[middleBase],
                    tree.RefModelPos[index]-tree.RefModelPos[little]),palm);
            // A cross product gives an oriented plane, not the palm side.
            // The mirrored right-hand bind changes its handedness.
            if(suffix=="r")palm=-palm;
            var baseBone=_fingerProximal[i]?i:tree.Parent[i];
            var positiveCurlDirection=Vector3.Transform(Vector3.UnitY,tree.RefModelRot[baseBone]);
            var nativeClosingSign=Vector3.Dot(positiveCurlDirection,palm)<0?-1f:1f;
            _fingerCurlSign[i]=nativeClosingSign/_sourceClosingSign[i];
            _fingerSpreadSign[i]=-1; // Spread and flexion have independent mirror signs.
            if(_fingerProximal[i])continue;
            var source=m.SourceIndex;var parent=rig.Bones[source].Parent;var proximal=tree.Parent[i]>=0?_targetToSource[tree.Parent[i]]:-1;
            if(parent>=0&&parent!=proximal&&rig.Bones[parent].Parent==proximal)
            {
                _fingerMiddle[i]=parent;_fingerTip[i]=source;
                _middleLength[i]=Vector3.Distance(rig.Bones[parent].RestPosition,rig.Bones[source].RestPosition);
                var b=rig.Bones[source];var tail=b.TailBone>=0?rig.Bones[b.TailBone].RestPosition-b.RestPosition:b.TailOffset;
                _tipLength[i]=tail.Length();
                if(_tipLength[i]<1e-6f)_tipLength[i]=_middleLength[i]*.65f;
            }
            else _fingerMiddle[i]=source; // A genuine two-joint source needs no merging.
        }
    }
    private Quaternion FourFingerLocalRotation(int bone,float amplitude)
    {
        var pose=SourceSolver!.Pose;var proximal=_fingerProximal[bone];float curl,spread=0;
        if(proximal)
        {
            var q=pose.LocalRotations[_targetToSource[bone]];
            curl=FingerJointMath.SignedAxisAngle(q,Vector3.UnitZ);
            spread=Math.Clamp(FingerJointMath.SignedAxisAngle(q,Vector3.UnitY),-FingerJointMath.SpreadLimit,FingerJointMath.SpreadLimit)*_fingerSpreadSign[bone];
        }
        else
        {
            curl=FingerJointMath.SignedAxisAngle(pose.LocalRotations[_fingerMiddle[bone]],Vector3.UnitZ);
            if(_fingerTip[bone]>=0)
                curl=FingerJointMath.EquivalentDistalCurl(curl,FingerJointMath.SignedAxisAngle(pose.LocalRotations[_fingerTip[bone]],Vector3.UnitZ),_middleLength[bone],_tipLength[bone]);
        }
        curl=FingerJointMath.LimitCurl(curl,_sourceClosingSign[bone],proximal)*_fingerCurlSign[bone];
        // Native finger long axis is X: give the base flexion/spread, the end
        // flexion only. No source long-axis roll is written onto these joints.
        var delta=RigMath.FromEuler(new(0,spread*amplitude,curl*amplitude));
        return Quaternion.Normalize(Tree!.RefLocalRot[bone]*delta);
    }
    public FingerJointDiagnostic[] GetFourFingerDiagnostics()
    {
        var result=new List<FingerJointDiagnostic>();
        foreach(var m in Mapped)
        {
            var i=m.FfIndex;if(!_fourFinger[i])continue;
            var delta=Quaternion.Normalize(Quaternion.Conjugate(Tree!.RefLocalRot[i])*_rotLocPose[i]);
            var angles=RigMath.Euler(delta)*180/MathF.PI;
            result.Add(new(){TargetBone=m.FfName,SourceBone=m.SourceJp,Proximal=_fingerProximal[i],
                AxialDegrees=angles.X,SpreadDegrees=angles.Y,FlexionDegrees=angles.Z,MiddleLength=_middleLength[i],TipLength=_tipLength[i]});
        }
        return result.ToArray();
    }
}
