using System.Numerics;

namespace FFMMD.Retarget;

public sealed class PoseSnapshot
{
    public Vector3[] LocalPositions=[],LocalScales=[],ModelPositions=[],ModelScales=[];
    public Quaternion[] LocalRotations=[],ModelRotations=[];
}
public sealed class FingerJointDiagnostic
{
    public string TargetBone="",SourceBone="";
    public bool Proximal;
    public float FlexionDegrees,SpreadDegrees,AxialDegrees,MiddleLength,TipLength;
}
public sealed class PlacementSnapshot
{
    public Vector3 ActorPosition,DrawPosition,DrawScale;
    public Quaternion DrawRotation;
    public bool HasGraphicsParent;
    public float? RootWorldY,CenterWorldY,RootRelativeToActorY;
    public string GroundReference="actor placement Y; terrain not sampled";
}
public sealed class PartialRootConnection
{
    public int ChildBone,BodyBone;
}
public sealed class PartialPoseSnapshot
{
    public int PartialIndex;
    public string[] Names=[];
    public short[] Parent=[];
    public PartialRootConnection[] Connections=[];
    public PoseSnapshot? Pose;
}
public sealed class RigDiagnosticReport
{
    public int SchemaVersion=5;
    public string PluginVersion="1.1.6",CapturedUtc=DateTime.UtcNow.ToString("O"),TargetName="",MotionPath="";
    public float Frame;
    public bool ApproximateSource,FinalStageAvailable;
    public string SourceName="",SourceFingerprint="",FinalStageNote="";
    public Calibration Calibration=new();
    public SourceBone[] SourceBones=[];
    public SourceIkChain[] SourceIkChains=[];
    public SolvedSourcePose? SourcePose;
    public TargetSkeletonSnapshot Target=new();
    public Vector3[] TargetLegGoals=[],TargetLegPoles=[];
    public PoseSnapshot? Prepared,BeforeWrite,AfterWrite,FinalRender;
    public PartialPoseSnapshot[] BeforePartials=[],AfterPartials=[],FinalPartials=[];
    public bool AnimationScaleWritesAllowed=false;
    public ScaleChangeAudit? DuringWriteScaleAudit,FinalStageScaleAudit;
    public float HeightAnchorY,PreparedCenterY;
    public float? AfterWriteCenterY,FinalCenterY;
    public float PreparedRootY;
    public float? AfterWriteRootY,FinalRootY;
    public Matrix4x4 GroundPlacementMatrix;
    public PlacementSnapshot? BeforePlacement,AfterPlacement,FinalPlacement;
    public FingerJointDiagnostic[] FourFingerJoints=[];
    public string FourFingerAdaptation="bounded flexion/spread; length-weighted middle/tip chord";
}
public sealed class TargetSkeletonSnapshot
{
    public string[] Names=[];
    public short[] Parent=[];
    public Quaternion[] BindLocalRotations=[];
    public Vector3[] EffectiveLocalPositions=[],EffectiveLocalScales=[];
    public bool[] RotationWrites=[];
    public Dictionary<string,string> SourceBindings=[];
    public Dictionary<string,string> OrientationSourceBindings=[];
}
public static class RigDiagnostics
{
    public static RigDiagnosticReport Create(Retargeter r,Calibration cal,float frame)
    {
        var tree=r.Tree??throw new InvalidOperationException("目标骨架未就绪。");
        var rig=r.SourceRig??throw new InvalidOperationException("源骨架未就绪。");
        var live=r.SourceSolver!.Pose;var sourcePose=new SolvedSourcePose(live.Positions.Length,live.IkActive.Length);
        live.Positions.CopyTo(sourcePose.Positions,0);live.LocalPositions.CopyTo(sourcePose.LocalPositions,0);
        live.Rotations.CopyTo(sourcePose.Rotations,0);live.LocalRotations.CopyTo(sourcePose.LocalRotations,0);
        live.IkActive.CopyTo(sourcePose.IkActive,0);live.IkTargets.CopyTo(sourcePose.IkTargets,0);live.KneePlanes.CopyTo(sourcePose.KneePlanes,0);
        var report=new RigDiagnosticReport{Frame=frame,SourceName=rig.Name,SourceFingerprint=rig.Fingerprint,ApproximateSource=rig.Approximate,
            Calibration=new(){SourceRestPose=cal.SourceRestPose,MotionScale=cal.MotionScale,YawDegrees=cal.YawDegrees,AutoPositionScale=cal.AutoPositionScale,ManualPositionScale=cal.ManualPositionScale,HeightOffset=cal.HeightOffset,LockHeight=cal.LockHeight,LegIkMode=cal.LegIkMode},SourceBones=rig.Bones,SourceIkChains=rig.IkChains,SourcePose=sourcePose,
            Target=new(){Names=tree.Names,Parent=tree.Parent,BindLocalRotations=tree.RefLocalRot,EffectiveLocalPositions=tree.RefLocalPos,EffectiveLocalScales=tree.RefLocalScale,RotationWrites=r.RotationWrites.ToArray(),SourceBindings=r.Mapped.ToDictionary(m=>m.FfName,m=>m.SourceJp),OrientationSourceBindings=r.Mapped.ToDictionary(m=>m.FfName,m=>rig.Bones[m.OrientationSourceIndex].Name)},
            TargetLegGoals=r.LegGoals.ToArray(),TargetLegPoles=r.LegPoles.ToArray(),Prepared=new(){LocalPositions=r.LocalPositions.ToArray(),LocalRotations=r.LocalRotations.ToArray(),ModelPositions=r.ModelPositions.ToArray(),ModelRotations=r.ModelRotations.ToArray(),LocalScales=tree.RefLocalScale,ModelScales=tree.RefModelScale}};
        if(r.CenterBoneIndex>=0)report.PreparedCenterY=r.ModelPositions[r.CenterBoneIndex].Y;
        if(r.HeightRootIndex>=0){report.HeightAnchorY=tree.RefModelPos[r.HeightRootIndex].Y+cal.HeightOffset;report.PreparedRootY=r.ModelPositions[r.HeightRootIndex].Y;}
        report.GroundPlacementMatrix=r.GroundPlacementMatrix;
        report.FourFingerJoints=r.GetFourFingerDiagnostics();
        return report;
    }
}
