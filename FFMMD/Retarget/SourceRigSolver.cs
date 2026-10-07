using System.Numerics;
using FFMMD.Posing;
using FFMMD.Vmd;

namespace FFMMD.Retarget;

public sealed class SolvedSourcePose
{
    public readonly Vector3[] Positions, LocalPositions;
    public readonly Quaternion[] Rotations, LocalRotations;
    public readonly bool[] IkActive;
    public readonly Vector3[] IkTargets, KneePlanes;
    public SolvedSourcePose(int bones,int chains)
    { Positions=new Vector3[bones];LocalPositions=new Vector3[bones];Rotations=new Quaternion[bones];LocalRotations=new Quaternion[bones];IkActive=new bool[chains];IkTargets=new Vector3[chains];KneePlanes=new Vector3[chains]; }
}

/// <summary>Samples FK and solves source-model constraints before any target sizing is applied.</summary>
public sealed class SourceRigSolver
{
    public readonly SourceRigDefinition Rig;
    public readonly SolvedSourcePose Pose;
    public readonly BoneTrack?[] Tracks;
    private readonly VmdAnimation? _animation;
    private readonly Quaternion[] _sampleRot, _ikRot, _baseRot, _appendRot;
    private readonly Vector3[] _samplePos, _appendPos;
    private readonly Vector3[] _fallbackBend;
    public SourceRigSolver(SourceRigDefinition rig,VmdAnimation? animation)
    {
        Rig=rig;_animation=animation;var n=rig.Bones.Length;Pose=new(n,rig.IkChains.Length);Tracks=new BoneTrack?[n];
        _sampleRot=new Quaternion[n];_ikRot=new Quaternion[n];_baseRot=new Quaternion[n];_appendRot=new Quaternion[n];_samplePos=new Vector3[n];_appendPos=new Vector3[n];
        var trackMap=new Dictionary<string,BoneTrack>(StringComparer.Ordinal);
        if(animation!=null)foreach(var pair in animation.Tracks)trackMap.TryAdd(VmdAnimation.NormalizeBoneName(pair.Key),pair.Value);
        for(var i=0;i<n;i++)Tracks[i]=trackMap.GetValueOrDefault(rig.Bones[i].Name);
        _fallbackBend=new Vector3[rig.IkChains.Length];
        for(var c=0;c<rig.IkChains.Length;c++)
        {
            var chain=rig.IkChains[c];var bend=-Vector3.UnitZ;
            if(chain.Links.Length==2)
            {
                var h=rig.Bones[chain.Links[1].Bone].RestPosition;var k=rig.Bones[chain.Links[0].Bone].RestPosition;var a=rig.Bones[chain.Effector].RestPosition;
                var direction=RigMath.Direction(a-h,-Vector3.UnitY);var projected=k-h-direction*Vector3.Dot(k-h,direction);
                bend=RigMath.Direction(projected,-Vector3.UnitZ);
            }
            _fallbackBend[c]=bend;
        }
    }
    public bool Enabled(int chain,Calibration cal,float frame)
    {
        var c=Rig.IkChains[chain];return cal.LegIkMode!=0&&c.Iterations>0&&
            (cal.LegIkMode==1||(_animation?.IsIkEnabledNormalized(Rig.Bones[c.Controller].Name,frame)??true));
    }
    public bool Evaluate(float frame,Calibration cal)
    {
        if(!float.IsFinite(frame))return false;
        for(var i=0;i<Rig.Bones.Length;i++)
        {
            var sample=Tracks[i]?.Sample(frame)??(Vector3.Zero,Quaternion.Identity);
            _samplePos[i]=sample.Item1;_sampleRot[i]=sample.Item2;_ikRot[i]=Quaternion.Identity;
        }
        World();
        for(var c=0;c<Rig.IkChains.Length;c++)
        {
            var chain=Rig.IkChains[c];Pose.IkActive[c]=Enabled(c,cal,frame);Pose.IkTargets[c]=Pose.Positions[chain.Controller];Pose.KneePlanes[c]=Vector3.Zero;
            if(!Pose.IkActive[c])continue;
            // The controller's own rotation moves its toe child. Ankle orientation
            // is solved by the toe chain, never copied from the controller key.
            if(chain.Links.Length==2 && chain.Iterations*chain.AngleLimit>=MathF.PI && IsAncestor(chain.Links[1].Bone,chain.Links[0].Bone)) SolveTwo(c);
            else SolveCcd(chain);
        }
        for(var i=0;i<Rig.Bones.Length;i++)if(!SkeletonTree.Finite(Pose.Positions[i])||!SkeletonTree.Finite(Pose.Rotations[i]))return false;
        return true;
    }
    private void World()
    {
        foreach(var i in Rig.Order)
        {
            var bone=Rig.Bones[i];var position=_samplePos[i];var rotation=_sampleRot[i];_appendRot[i]=Quaternion.Identity;_appendPos[i]=Vector3.Zero;
            if(bone.AppendParent>=0)
            {
                var donor=bone.AppendParent;
                var local=bone.AppendLocal||Rig.Bones[donor].AppendParent<0;
                if((bone.Flags&0x100)!=0)
                {
                    _appendRot[i]=RigMath.Power(_ikRot[donor]*(local?_sampleRot[donor]:_appendRot[donor]),bone.AppendRatio);
                    rotation=Quaternion.Normalize(rotation*_appendRot[i]);
                }
                if((bone.Flags&0x200)!=0){_appendPos[i]=(local?_samplePos[donor]:_appendPos[donor])*bone.AppendRatio;position+=_appendPos[i];}
            }
            _baseRot[i]=rotation;rotation=Quaternion.Normalize(_ikRot[i]*rotation);
            if((bone.Flags&0x400)!=0 && bone.FixedAxis.LengthSquared()>1e-12f)
            {
                var axis=Vector3.Normalize(bone.FixedAxis);var v=new Vector3(rotation.X,rotation.Y,rotation.Z);var projected=axis*Vector3.Dot(v,axis);
                var fixedRot=new Quaternion(projected,rotation.W);rotation=fixedRot.LengthSquared()<1e-12f?Quaternion.Identity:Quaternion.Normalize(fixedRot);
            }
            var p=bone.Parent;Pose.LocalRotations[i]=rotation;
            Pose.LocalPositions[i]=bone.RestPosition-(p>=0?Rig.Bones[p].RestPosition:Vector3.Zero)+position;
            Pose.Positions[i]=p<0?Pose.LocalPositions[i]:Pose.Positions[p]+Vector3.Transform(Pose.LocalPositions[i],Pose.Rotations[p]);
            Pose.Rotations[i]=p<0?rotation:Quaternion.Normalize(Pose.Rotations[p]*rotation);
        }
    }
    private bool IsAncestor(int parent,int child)
    {for(var i=Rig.Bones[child].Parent;i>=0;i=Rig.Bones[i].Parent)if(i==parent)return true;return false;}
    private void SetWorld(SourceIkLink link,Quaternion model)
    {
        var parent=Rig.Bones[link.Bone].Parent;
        var local=Quaternion.Normalize((parent<0?Quaternion.Identity:Quaternion.Conjugate(Pose.Rotations[parent]))*model);
        if(link.Limited)local=RigMath.FromEuler(Vector3.Clamp(RigMath.Euler(local),link.Minimum,link.Maximum));
        _ikRot[link.Bone]=Quaternion.Normalize(local*Quaternion.Conjugate(_baseRot[link.Bone]));World();
    }
    private void SolveTwo(int index)
    {
        var chain=Rig.IkChains[index];var knee=chain.Links[0];var hip=chain.Links[1];var h=Pose.Positions[hip.Bone];var k=Pose.Positions[knee.Bone];var a=Pose.Positions[chain.Effector];
        var direction=RigMath.Direction(a-h,-Vector3.UnitY);var bend=k-h-direction*Vector3.Dot(k-h,direction);
        var parent=Rig.Bones[hip.Bone].Parent;
        if(bend.LengthSquared()<1e-8f)bend=Vector3.Transform(_fallbackBend[index],parent<0?Quaternion.Identity:Pose.Rotations[parent]);
        RigMath.TwoBone(h,k,a,Pose.IkTargets[index],bend,out var desiredKnee,out var desiredAnkle);
        SetWorld(hip,RigMath.FromTo(k-h,desiredKnee-h)*Pose.Rotations[hip.Bone]);
        // A hinge needs a plane as well as an aimed thigh. Aim alone leaves an
        // axial degree of freedom which local-X clipping otherwise fights in CCD.
        if(knee.Limited&&knee.Minimum.Y==0&&knee.Maximum.Y==0&&knee.Minimum.Z==0&&knee.Maximum.Z==0)
        {
            var axis=Vector3.Cross(desiredKnee-h,desiredAnkle-desiredKnee);
            if(axis.LengthSquared()>1e-10f)
            {
                axis=Vector3.Normalize(axis);if(knee.Maximum.X<=0)axis=-axis;
                var kneeParent=Rig.Bones[knee.Bone].Parent;
                var current=Vector3.Transform(Vector3.UnitX,kneeParent<0?Quaternion.Identity:Pose.Rotations[kneeParent]);
                var upperAim=RigMath.Direction(desiredKnee-h,-Vector3.UnitY);
                current-=upperAim*Vector3.Dot(current,upperAim);axis-=upperAim*Vector3.Dot(axis,upperAim);
                current=RigMath.Direction(current,axis);axis=RigMath.Direction(axis,current);
                var twist=MathF.Atan2(Vector3.Dot(upperAim,Vector3.Cross(current,axis)),Vector3.Dot(current,axis));
                SetWorld(hip,Quaternion.CreateFromAxisAngle(upperAim,twist)*Pose.Rotations[hip.Bone]);
            }
        }
        SetWorld(knee,RigMath.FromTo(Pose.Positions[chain.Effector]-Pose.Positions[knee.Bone],desiredAnkle-Pose.Positions[knee.Bone])*Pose.Rotations[knee.Bone]);
        // Generic constraints can differ from a hinge; finish with bounded CCD if
        // local clipping prevented the analytical solution from reaching its goal.
        if(Vector3.DistanceSquared(Pose.Positions[chain.Effector],desiredAnkle)>1e-6f)SolveCcd(chain);
        var upper=Pose.Positions[knee.Bone]-Pose.Positions[hip.Bone];var reach=Pose.Positions[chain.Effector]-Pose.Positions[hip.Bone];
        var unit=RigMath.Direction(reach,-Vector3.UnitY);Pose.KneePlanes[index]=RigMath.Direction(upper-unit*Vector3.Dot(upper,unit),RigMath.Direction(bend,-Vector3.UnitZ));
    }
    private void SolveCcd(SourceIkChain chain)
    {
        var goal=Pose.Positions[chain.Controller];
        for(var iteration=0;iteration<chain.Iterations;iteration++)
        {
            if(Vector3.DistanceSquared(goal,Pose.Positions[chain.Effector])<1e-8f)break;
            foreach(var link in chain.Links)
            {
                var at=Pose.Positions[link.Bone];var delta=RigMath.FromTo(Pose.Positions[chain.Effector]-at,goal-at);
                // A perfectly straight hinge has zero CCD gradient for a shorter,
                // collinear goal. Seed its permitted bend deterministically.
                if(iteration==0&&chain.AngleLimit>0&&link.Limited&&
                    link.Minimum.Y==0&&link.Maximum.Y==0&&link.Minimum.Z==0&&link.Maximum.Z==0&&
                    new Vector3(delta.X,delta.Y,delta.Z).LengthSquared()<1e-12f&&
                    Vector3.DistanceSquared(goal,at)<Vector3.DistanceSquared(Pose.Positions[chain.Effector],at)-1e-6f)
                {
                    var euler=RigMath.Euler(Pose.LocalRotations[link.Bone]);
                    var step=MathF.Min(chain.AngleLimit,.1f);
                    euler.X=Math.Clamp(euler.X+(link.Minimum.X<0?-step:step),link.Minimum.X,link.Maximum.X);
                    var parent=Rig.Bones[link.Bone].Parent;
                    SetWorld(link,(parent<0?Quaternion.Identity:Pose.Rotations[parent])*RigMath.FromEuler(euler));
                    continue;
                }
                var w=Math.Clamp(MathF.Abs(delta.W),0,1);var angle=2*MathF.Acos(w);
                if(angle>chain.AngleLimit&&angle>1e-7f)delta=RigMath.Power(delta,chain.AngleLimit/angle);
                SetWorld(link,delta*Pose.Rotations[link.Bone]);
            }
        }
    }
}
