using System.Numerics;
using FFMMD.Posing;
using FFMMD.Vmd;

namespace FFMMD.Retarget;

public sealed class MappedBone
{
    public required string SourceJp,FfName;
    public int FfIndex,SourceIndex,OrientationSourceIndex;
    public BoneTrack? Track;
}
public sealed partial class Retargeter
{
    public SkeletonTree? Tree;
    public TargetRigProfile? Profile;
    public SourceRigDefinition? SourceRig;
    public SourceRigSolver? SourceSolver;
    public List<MappedBone> Mapped=[];
    public List<string> UnmappedMmd=[];
    public int CenterBoneIndex=-1;
    public int HeightRootIndex=-1;
    public float AutoPosScale=-1;
    public readonly BoneTrack?[] IkTrack=[null,null];
    public ReadOnlySpan<Quaternion> ModelRotations=>_rot;
    public ReadOnlySpan<Quaternion> LocalRotations=>_rotLocPose;
    public ReadOnlySpan<Vector3> ModelPositions=>_pos;
    public ReadOnlySpan<Vector3> LocalPositions=>_posLocPose;
    public ReadOnlySpan<bool> RotationWrites=>_writes;
    public readonly Vector3[] LegGoals=new Vector3[2],LegPoles=new Vector3[2];
    private readonly int[] _hip=[-1,-1],_knee=[-1,-1],_ankle=[-1,-1],_srcHip=[-1,-1],_srcKnee=[-1,-1],_srcAnkle=[-1,-1];
    private int _sourcePelvis=-1,_sourcePreset=-1;
    private SourceRigDefinition? _explicitRig;
    private VmdAnimation? _anim;
    private Quaternion[] _rot=[],_rotLocPose=[],_aligned=[];
    private Vector3[] _pos=[],_posLocPose=[];
    private bool[] _writes=[];
    private int[] _targetToSource=[];
    private Vector3 _startTravel;
    private float _alignmentYaw=float.NaN;
    private RigCoordinateMap _baseMap;
    private RigCoordinateMap _groundMap;
    public Matrix4x4 GroundPlacementMatrix=>_groundMap.Matrix;
    public string SourceStatus=>SourceRig==null?"未建立源骨架":SourceRig.Approximate?$"{SourceRig.Name}（近似适配，可选源 PMX）":$"PMX：{SourceRig.Name}";
    public void SetSourceRig(SourceRigDefinition? source){_explicitRig=source;ResetCache();}
    public void ResetCache()
    {
        Tree=null;Profile=null;SourceSolver=null;SourceRig=null;_anim=null;_alignmentYaw=float.NaN;
        Mapped.Clear();UnmappedMmd.Clear();CenterBoneIndex=HeightRootIndex=-1;AutoPosScale=-1;Array.Clear(IkTrack);_sourcePreset=-1;
    }
    internal void TestSetup(SkeletonTree tree,VmdAnimation anim,Calibration cal,SourceRigDefinition? rig=null)
    {_explicitRig=rig;Initialize(new TargetRigProfile(tree),anim,cal);}
    private void Initialize(TargetRigProfile profile,VmdAnimation? anim,Calibration cal)
    {
        Profile=profile;Tree=profile.Skeleton;_anim=anim;_sourcePreset=cal.SourceRestPose;
        SourceRig=_explicitRig??SourceRigDefinition.Standard(cal.SourceRestPose==1);SourceSolver=new SourceRigSolver(SourceRig,anim);
        var tree=Tree;var n=tree.BoneCount;_rot=new Quaternion[n];_rotLocPose=new Quaternion[n];_pos=new Vector3[n];_posLocPose=new Vector3[n];_aligned=new Quaternion[n];_writes=new bool[n];_targetToSource=Enumerable.Repeat(-1,n).ToArray();
        Mapped.Clear();UnmappedMmd.Clear();
        foreach(var binding in BoneMap.Bindings)
        {
            var target=tree.Find(binding.Ff);var src=binding.Mmd.Select(SourceRig.Find).FirstOrDefault(i=>i>=0,-1);
            if(src>=0)
            {
                var name=SourceRig.Bones[src].Name;
                var deformName=name is "左足" or "右足" or "左ひざ" or "右ひざ" or "左足首" or "右足首"?name+"D":
                    name=="左つま先"?"左足先EX":name=="右つま先"?"右足先EX":null;
                if(deformName!=null&&SourceRig.Find(deformName) is var deform&&deform>=0)src=deform;
            }
            if(target<0||src<0)continue;
            _targetToSource[target]=src;_writes[target]=true;
            Mapped.Add(new MappedBone{FfIndex=target,SourceIndex=src,OrientationSourceIndex=src,FfName=tree.Names[target],SourceJp=SourceRig.Bones[src].Name,Track=SourceSolver.Tracks[src]});
        }
        CenterBoneIndex=tree.Find("n_hara","j_kosi");_sourcePelvis=SourceRig.Find("下半身");
        HeightRootIndex=Array.FindIndex(tree.Parent,p=>p<0);
        if(HeightRootIndex>=0)_writes[HeightRootIndex]=true;
        var sourceCenter=SourceRig.Find("腰");if(sourceCenter<0)sourceCenter=SourceRig.Find("グルーブ");if(sourceCenter<0)sourceCenter=SourceRig.Find("センター");
        if(CenterBoneIndex>=0&&sourceCenter>=0){_targetToSource[CenterBoneIndex]=sourceCenter;_writes[CenterBoneIndex]=true;}
        foreach(var m in Mapped)
            for(var parent=tree.Parent[m.FfIndex];parent>=0&&_targetToSource[parent]<0;parent=tree.Parent[parent])_writes[parent]=true;
        var used=SourceSolver.Tracks.Where(t=>t!=null).Select(t=>t!.Bone).ToHashSet(StringComparer.Ordinal);
        if(anim!=null)foreach(var name in anim.Tracks.Keys)if(!used.Contains(name))UnmappedMmd.Add(name);
        UnmappedMmd.Sort(StringComparer.Ordinal);
        for(var s=0;s<2;s++)
        {
            var suffix=s==0?"l":"r";var prefix=s==0?"左":"右";
            _hip[s]=tree.Find("j_asi_a_"+suffix);_knee[s]=tree.Find("j_asi_c_"+suffix);_ankle[s]=tree.Find("j_asi_d_"+suffix);
            _srcHip[s]=_hip[s]>=0?_targetToSource[_hip[s]]:-1;_srcKnee[s]=_knee[s]>=0?_targetToSource[_knee[s]]:-1;_srcAnkle[s]=_ankle[s]>=0?_targetToSource[_ankle[s]]:-1;
            var controller=SourceRig.Find(prefix+"足IK");IkTrack[s]=controller>=0?SourceSolver.Tracks[controller]:null;
            // Intermediate joints participate in the solved leg chain. Freeze
            // their bind rotations only; their live translations/scales are kept.
            if(_hip[s]>=0&&_ankle[s]>=0)
                for(var i=tree.Parent[_ankle[s]];i>=0&&i!=_hip[s];i=tree.Parent[i])_writes[i]=true;
        }
        // One travel anchor removes only the initial horizontal displacement.
        // Initial stance, vertical crouch and jump remain authored.
        SourceSolver.Evaluate(0,cal);
        var travel=_sourcePelvis<0?Vector3.Zero:SourceSolver.Pose.Positions[_sourcePelvis]-SourceRig.Bones[_sourcePelvis].RestPosition;
        _startTravel=new Vector3(travel.X,0,travel.Z);AutoPosScale=CalculateScale();_alignmentYaw=float.NaN;
        _baseMap=profile.CoordinateMap(SourceRig,0);
        _groundMap=profile.GroundCoordinateMap(SourceRig);
        InitializeUpperBody();
    }
    private float CalculateScale()
    {
        var sum=0f;var count=0;
        for(var s=0;s<2;s++)if(_hip[s]>=0&&_knee[s]>=0&&_ankle[s]>=0&&_srcHip[s]>=0&&_srcKnee[s]>=0&&_srcAnkle[s]>=0)
        {
            var sourceLength=Vector3.Distance(SourceRig!.Bones[_srcHip[s]].RestPosition,SourceRig.Bones[_srcKnee[s]].RestPosition)+Vector3.Distance(SourceRig.Bones[_srcKnee[s]].RestPosition,SourceRig.Bones[_srcAnkle[s]].RestPosition);
            var targetLength=Vector3.Distance(Tree!.RefModelPos[_hip[s]],Tree.RefModelPos[_knee[s]])+Vector3.Distance(Tree.RefModelPos[_knee[s]],Tree.RefModelPos[_ankle[s]]);
            if(sourceLength>1e-5f){sum+=targetLength/sourceLength;count++;}
        }
        return count>0?sum/count:.09f;
    }
    public bool IsIkActive(int side,Calibration cal,float frame=0)
    {
        if(side is <0 or >1||SourceRig==null||SourceSolver==null)return false;
        var controller=SourceRig.Find(side==0?"左足IK":"右足IK");
        for(var c=0;c<SourceRig.IkChains.Length;c++)if(SourceRig.IkChains[c].Controller==controller)return SourceSolver.Enabled(c,cal,frame);
        return false;
    }
    private void Align(RigCoordinateMap map)
    {
        Array.Copy(Tree!.RefModelRot,_aligned,_aligned.Length);
        foreach(var m in Mapped)
        {
            // Cervical/spinal curves and finger opposition are target anatomy.
            // A neutral source must not straighten these native reference joints.
            if(m.FfIndex==HeightRootIndex||m.FfName.StartsWith("j_sebo_",StringComparison.Ordinal)||m.FfName is "j_kubi" or "j_kao"||_fingerHand[m.FfIndex]>=0)continue;
            // The nearest mapped SOURCE descendant also collapses unmapped
            // phalanges and passes through target auxiliary joints.
            MappedBone? next=null;var best=int.MaxValue;
            foreach(var candidate in Mapped)
            {
                var depth=0;var i=candidate.SourceIndex;
                while(i>=0&&i!=m.SourceIndex){i=SourceRig!.Bones[i].Parent;depth++;}
                if(i==m.SourceIndex&&depth>0&&depth<best&&DescendsFrom(candidate.FfIndex,m.FfIndex)){next=candidate;best=depth;}
            }
            if(next==null||m.FfName.StartsWith("j_asi_d_",StringComparison.Ordinal))continue;
            var source=SourceRig!.Bones[next.SourceIndex].RestPosition-SourceRig.Bones[m.SourceIndex].RestPosition;
            var target=Tree.RefModelPos[next.FfIndex]-Tree.RefModelPos[m.FfIndex];
            _aligned[m.FfIndex]=Quaternion.Normalize(RigMath.FromTo(target,map.Vector(source))*Tree.RefModelRot[m.FfIndex]);
        }
        var pelvis=Tree.Find("j_kosi");if(pelvis>=0)_aligned[pelvis]=Tree.RefModelRot[pelvis];
        if(CenterBoneIndex>=0)_aligned[CenterBoneIndex]=Tree.RefModelRot[CenterBoneIndex];
    }
    public bool Evaluate(float frame,Calibration cal)
    {
        if(Tree==null||SourceSolver==null||SourceRig==null||Profile==null||!float.IsFinite(frame)||!float.IsFinite(cal.MotionScale)||!float.IsFinite(cal.YawDegrees)||!float.IsFinite(cal.ManualPositionScale)||!float.IsFinite(cal.HeightOffset))return false;
        if(_explicitRig==null&&_sourcePreset!=cal.SourceRestPose)Initialize(Profile,_anim,cal);
        var amp=Math.Clamp(cal.MotionScale,0,1);var map=_baseMap.Yaw(Profile!.Up,cal.YawDegrees);
        if(_alignmentYaw!=cal.YawDegrees){Align(_baseMap);_alignmentYaw=cal.YawDegrees;}
        if(!SourceSolver!.Evaluate(frame,cal))return false;
        var pose=SourceSolver.Pose;var yaw=Quaternion.CreateFromAxisAngle(Profile.Up,cal.YawDegrees*MathF.PI/180);
        var placement=_groundMap.Yaw(Vector3.UnitY,cal.YawDegrees);
        var groundYaw=Quaternion.CreateFromAxisAngle(Vector3.UnitY,cal.YawDegrees*MathF.PI/180);
        for(var i=0;i<Tree!.BoneCount;i++)
        {
            var src=_orientationSource[i];var p=Tree.Parent[i];
            if(src>=0&&_fingerHand[i]<0)
            {
                var desired=Quaternion.Normalize(map.Rotation(pose.Rotations[src])*yaw*_aligned[i]);
                if(i==HeightRootIndex)desired=Quaternion.Normalize(placement.Rotation(pose.Rotations[src])*groundYaw*Tree.RefModelRot[i]);
                desired=Quaternion.Normalize(RigMath.Power(desired*Quaternion.Conjugate(Tree.RefModelRot[i]),amp)*Tree.RefModelRot[i]);
                _rotLocPose[i]=Quaternion.Normalize((p>=0?Quaternion.Conjugate(_rot[p]):Quaternion.Identity)*desired);
            }
            else _rotLocPose[i]=Tree.RefLocalRot[i];
            _posLocPose[i]=Tree.RefLocalPos[i];_rot[i]=p>=0?Quaternion.Normalize(_rot[p]*_rotLocPose[i]):_rotLocPose[i];
            _pos[i]=p>=0?_pos[p]+Vector3.Transform(_posLocPose[i]*Tree.RefModelScale[p],_rot[p]):_posLocPose[i];
        }
        var scale=cal.AutoPositionScale?AutoPosScale:Math.Clamp(cal.ManualPositionScale,0,.5f);
        var travel=_sourcePelvis>=0?pose.Positions[_sourcePelvis]-SourceRig!.Bones[_sourcePelvis].RestPosition-_startTravel:Vector3.Zero;
        var authoredMotion=placement.Vector(travel)*(scale*amp);
        // Place the moving pelvis relative to the native floor root. Subtract the
        // current parent-rotated baseline so root rotation is not counted twice.
        // Authored vertical motion is kept, including crouches and jumps.
        var offset=CenterBoneIndex>=0?Tree.RefModelPos[CenterBoneIndex]+authoredMotion-_pos[CenterBoneIndex]:Vector3.Zero;
        if(CenterBoneIndex>=0){var p=Tree.Parent[CenterBoneIndex];_posLocPose[CenterBoneIndex]+=p>=0?Vector3.Transform(offset,Quaternion.Conjugate(_rot[p]))/Tree.RefModelScale[p]:offset;}
        if(HeightRootIndex>=0)_posLocPose[HeightRootIndex]+=Vector3.UnitY*Math.Clamp(cal.HeightOffset,-3,3);
        Cascade();
        for(var s=0;s<2;s++)
        {
            LegGoals[s]=_ankle[s]>=0?_pos[_ankle[s]]:Vector3.Zero;LegPoles[s]=-Profile.Back;
            if(amp>0)RetargetLeg(s,map,amp);
        }
        UpdateArmCorrections(amp);
        UpdateFingers(amp);
        for(var i=0;i<Tree.BoneCount;i++)if(!SkeletonTree.Finite(_rot[i])||!SkeletonTree.Finite(_pos[i]))return false;
        return true;
    }
    private void Cascade()
    {
        for(var i=0;i<Tree!.BoneCount;i++){var p=Tree.Parent[i];_rot[i]=p<0?_rotLocPose[i]:Quaternion.Normalize(_rot[p]*_rotLocPose[i]);_pos[i]=p<0?_posLocPose[i]:_pos[p]+Vector3.Transform(_posLocPose[i]*Tree.RefModelScale[p],_rot[p]);}
    }
    private void SetModelRotation(int bone,Quaternion q)
    {var p=Tree!.Parent[bone];_rotLocPose[bone]=Quaternion.Normalize((p<0?Quaternion.Identity:Quaternion.Conjugate(_rot[p]))*q);Cascade();}
    private void RetargetLeg(int s,RigCoordinateMap map,float amp)
    {
        var h=_hip[s];var k=_knee[s];var a=_ankle[s];var sh=_srcHip[s];var sk=_srcKnee[s];var sa=_srcAnkle[s];
        if(h<0||k<0||a<0||sh<0||sk<0||sa<0||!DescendsFrom(k,h)||!DescendsFrom(a,k))return;
        var pose=SourceSolver!.Pose;var upper=pose.Positions[sk]-pose.Positions[sh];var lower=pose.Positions[sa]-pose.Positions[sk];var reach=upper+lower;
        if(upper.LengthSquared()<1e-10f||lower.LengthSquared()<1e-10f)return;
        var l1=Vector3.Distance(_pos[h],_pos[k]);var l2=Vector3.Distance(_pos[k],_pos[a]);
        var bendCos=Math.Clamp(Vector3.Dot(Vector3.Normalize(upper),Vector3.Normalize(lower)),-1,1);
        var length=MathF.Sqrt(MathF.Max(0,l1*l1+l2*l2+2*l1*l2*bendCos));
        var reference=SourceRig!.Bones[sa].RestPosition-SourceRig.Bones[sh].RestPosition;
        var sourceParent=SourceRig.Bones[sh].Parent;
        var fallback=RigMath.Direction(Vector3.Transform(reference,sourceParent<0?Quaternion.Identity:pose.Rotations[sourceParent]),-Vector3.UnitY);
        var goal=_pos[h]+map.Vector(RigMath.Direction(reach,fallback))*length;var bend=map.Vector(upper);var foot=_rot[a];
        if(amp!=1){goal=Vector3.Lerp(_pos[a],goal,Math.Min(amp,1));bend=Vector3.Lerp(_pos[k]-_pos[h],bend,Math.Min(amp,1));}
        RigMath.TwoBone(_pos[h],_pos[k],_pos[a],goal,bend,out var knee,out var ankle);
        var unit=RigMath.Direction(ankle-_pos[h],-Profile!.Up);LegGoals[s]=ankle;LegPoles[s]=RigMath.Direction(knee-_pos[h]-unit*Vector3.Dot(knee-_pos[h],unit),map.Vector(-Vector3.UnitZ));
        SetModelRotation(h,RigMath.FromTo(_pos[k]-_pos[h],knee-_pos[h])*_rot[h]);
        SetModelRotation(k,RigMath.FromTo(_pos[a]-_pos[k],ankle-_pos[k])*_rot[k]);SetModelRotation(a,foot);
    }
    private bool DescendsFrom(int child,int ancestor){for(var i=child;i>=0;i=Tree!.Parent[i])if(i==ancestor)return true;return false;}
    public List<string> Diagnose(float frame,Calibration cal)
    {
        if(!Evaluate(frame,cal))return["姿态计算失败或骨架尚未就绪"];
        var result=new List<string>{SourceStatus,$"实际腿长位移比例：{AutoPosScale:0.0000}"};
        for(var s=0;s<2;s++)if(_hip[s]>=0&&_knee[s]>=0&&_ankle[s]>=0)result.Add($"{(s==0?"左":"右")}腿：源已求解；目标误差 {Vector3.Distance(_pos[_ankle[s]],LegGoals[s]):0.00000}");
        return result;
    }
    public RotationTranslationWrite GetBoneWrite(int bone)=>new(_rotLocPose[bone],_posLocPose[bone],_writes[bone],
        _writes[bone]&&(bone==CenterBoneIndex||bone==HeightRootIndex));
}
