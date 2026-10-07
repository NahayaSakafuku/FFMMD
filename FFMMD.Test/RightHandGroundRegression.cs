using System.Numerics;
using System.Text.Json;
using FFMMD;
using FFMMD.Posing;
using FFMMD.Retarget;
using FFMMD.Vmd;

static class RightHandGroundRegression
{
    static readonly JsonSerializerOptions Json=new(){IncludeFields=true};
    public sealed class FrameData
    {
        public float Frame {get;set;}
        public SkeletonTree Target=new();public SourceRigDefinition Source=new();public Calibration Calibration=new();
        public PoseSnapshot Before=new(),After=new();
    }
    static FrameData[] Frames()
    {
        var values=JsonSerializer.Deserialize<FrameData[]>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures","Acceptance-v1.1.5.json")),Json)!;
        foreach(var f in values){f.Target.RebuildReference();f.Source.Validate();}return values;
    }
    static void Assert(bool v,string why="assertion failed"){if(!v)throw new Exception(why);}
    static void Near(float a,float b,float eps=1e-5f)=>Assert(MathF.Abs(a-b)<eps,$"{a} != {b}");
    static void Near(Vector3 a,Vector3 b,float eps=1e-4f)=>Assert(Vector3.Distance(a,b)<eps,$"{a} != {b}");
    static void Same(Quaternion a,Quaternion b)=>Assert(MathF.Abs(Quaternion.Dot(a,b))>1-1e-5f);
    static VmdBoneKeyFrame Key(string bone,uint frame,Vector3 position,Quaternion? rotation=null)=>new(){Bone=bone,Frame=frame,Position=position,Rotation=rotation??Quaternion.Identity,Interp=[20,20,20,20,20,20,20,20,107,107,107,107,107,107,107,107,..new byte[48]]};
    static VmdAnimation Motion(params VmdBoneKeyFrame[] keys)=>VmdAnimation.Build(new(){BoneFrames=keys.ToList()});
    static Vector3 Delta(Quaternion reference,Quaternion pose)=>RigMath.Euler(Quaternion.Normalize(Quaternion.Conjugate(reference)*pose));
    public static int Run(string? path)
    {
        var count=0;var failed=0;
        void Test(string name,Action body){count++;try{body();Console.WriteLine("  ✔ "+name);}catch(Exception e){failed++;Console.WriteLine("  ✘ "+name+": "+e.Message);}}
        Test("实际原生握拳数据：左右远节都是负 Z 闭合",()=>{
            foreach(var f in Frames())foreach(var side in new[]{"l","r"})foreach(var digit in new[]{"hito","naka","kusu","ko"})
            {
                var i=f.Target.Find($"j_{digit}_b_{side}");var angle=Delta(f.Target.RefLocalRot[i],f.Before.LocalRotations[i]).Z;
                Assert(angle<-MathF.PI/3&&angle>-MathF.PI*.8f,$"native curl {side}/{digit}: {angle}");
            }
        });
        Test("现场证据：旧版 root 已固定，但 center 随动作升降",()=>{var f=Frames();foreach(var x in f){var root=x.Target.Find("n_root");Near(x.After.ModelPositions[root].Y,-.03f);}var center=f[0].Target.Find("n_hara");Assert(MathF.Abs(f[0].After.ModelPositions[center].Y-f[1].After.ModelPositions[center].Y)>.01f);});
        Test("地面放置矩阵：水平输入不含 Y，Y 轴保持竖直",()=>{foreach(var f in Frames()){var profile=new TargetRigProfile(f.Target);var map=profile.GroundCoordinateMap(f.Source).Yaw(Vector3.UnitY,-6);Near(map.Vector(Vector3.UnitX).Y,0);Near(map.Vector(Vector3.UnitZ).Y,0);Near(map.Vector(Vector3.UnitY),Vector3.UnitY);}});
        Test("百单位水平移动不下沉：质心和 root 均无伪竖直分量",()=>{
            var f=Frames()[0];var motion=Motion(Key("センター",0,Vector3.Zero),Key("センター",100,new(0,0,-100)));var c=new Calibration{YawDegrees=-6,HeightOffset=.13f};var r=new Retargeter();r.TestSetup(f.Target,motion,c,f.Source);
            foreach(var frame in new[]{0f,20,50,100,0}){r.Evaluate(frame,c);Near(r.ModelPositions[r.HeightRootIndex].Y,.13f);Near(r.ModelPositions[r.CenterBoneIndex].Y,r.Tree!.RefModelPos[r.CenterBoneIndex].Y+.13f);}
        });
        Test("源质心下蹲保留：根不动，质心按真实 Y 位移变化",()=>{
            var f=Frames()[0];var motion=Motion(Key("センター",0,Vector3.Zero),Key("センター",100,new(0,-2,-100)));var c=new Calibration{HeightOffset=.13f};var r=new Retargeter();r.TestSetup(f.Target,motion,c,f.Source);
            foreach(var frame in new[]{0f,50,100}){r.Evaluate(frame,c);Near(r.ModelPositions[r.HeightRootIndex].Y,.13f);Near(r.ModelPositions[r.CenterBoneIndex].Y,r.Tree!.RefModelPos[r.CenterBoneIndex].Y+.13f-2*(frame/100)*r.AutoPosScale);}
        });
        Test("源质心跳跃保留，不触发质心锁定",()=>{var f=Frames()[0];var motion=Motion(Key("センター",0,Vector3.Zero),Key("センター",100,new(0,3,0)));var c=new Calibration{LockHeight=true};var r=new Retargeter();r.TestSetup(f.Target,motion,c,f.Source);r.Evaluate(100,c);Near(r.ModelPositions[r.HeightRootIndex].Y,0);Near(r.ModelPositions[r.CenterBoneIndex].Y,r.Tree!.RefModelPos[r.CenterBoneIndex].Y+3*r.AutoPosScale);});
        Test("根参考无需假倾斜：空动作仅保留竖直偏航",()=>{var f=Frames()[0];var c=new Calibration{YawDegrees=-6};var r=new Retargeter();r.TestSetup(f.Target,Motion(),c,f.Source);r.Evaluate(0,c);Same(r.ModelRotations[r.HeightRootIndex],Quaternion.CreateFromAxisAngle(Vector3.UnitY,-6*MathF.PI/180)*r.Tree!.RefModelRot[r.HeightRootIndex]);});
        Test("根转动的升降只计一次：由源 FK 世界位置计算",()=>{var f=Frames()[0];var c=new Calibration();var motion=Motion(Key("全ての親",0,Vector3.Zero,Quaternion.CreateFromAxisAngle(Vector3.UnitX,.4f)));var r=new Retargeter();r.TestSetup(f.Target,motion,c,f.Source);r.Evaluate(0,c);var source=r.SourceRig!;var pelvis=source.Find("下半身");var dy=r.SourceSolver!.Pose.Positions[pelvis].Y-source.Bones[pelvis].RestPosition.Y;Near(r.ModelPositions[r.CenterBoneIndex].Y,r.Tree!.RefModelPos[r.CenterBoneIndex].Y+dy*r.AutoPosScale);});
        Test("配置迁移解除旧质心锁定并保留根偏移",()=>{var c=new Config{RigPipelineVersion=4,Cal=new(){LockHeight=true,HeightOffset=-.03f,YawDegrees=-6}};c.Normalize();Assert(c.RigPipelineVersion==5&&!c.Cal.LockHeight&&c.Cal.HeightOffset==-.03f&&c.Cal.YawDegrees==-6);});
        Test("高度诊断锚定 root，center 单独记录",()=>{var f=Frames()[0];var c=new Calibration{HeightOffset=.12f};var r=new Retargeter();r.TestSetup(f.Target,Motion(Key("センター",0,new(0,-2,0))),c,f.Source);r.Evaluate(0,c);var report=RigDiagnostics.Create(r,c,0);Near(report.HeightAnchorY,.12f);Near(report.PreparedRootY,.12f);Assert(report.PreparedCenterY>.3f);});
        if(path!=null)
        {
            var motion=VmdAnimation.Build(VmdFile.Parse(path));
            Test("左手保持新现场结果；右手只反转闭合，张开保持",()=>{
                foreach(var f in Frames())
                {
                    var r=new Retargeter();r.TestSetup(f.Target,motion,f.Calibration,f.Source);r.Evaluate(f.Frame,f.Calibration);
                    foreach(var digit in new[]{"hito","naka","kusu","ko"})foreach(var part in new[]{"a","b"})
                    {
                        var left=f.Target.Find($"j_{digit}_{part}_l");Same(r.LocalRotations[left],f.After.LocalRotations[left]);
                        var right=f.Target.Find($"j_{digit}_{part}_r");var before=Delta(f.Target.RefLocalRot[right],f.After.LocalRotations[right]);var after=Delta(r.Tree!.RefLocalRot[right],r.LocalRotations[right]);
                        Near(after.Z,-before.Z,2e-4f);Near(after.Y,before.Y,2e-4f);Near(after.X,0,2e-4f);
                    }
                }
            });
            Test("拇指／颈／大臂角度保持现场结果",()=>{foreach(var f in Frames()){var r=new Retargeter();r.TestSetup(f.Target,motion,f.Calibration,f.Source);r.Evaluate(f.Frame,f.Calibration);foreach(var name in new[]{"j_kubi","j_kao","j_ude_a_l","j_ude_b_l","j_ude_a_r","j_ude_b_r","j_oya_a_l","j_oya_b_l","j_oya_a_r","j_oya_b_r"}){var i=f.Target.Find(name);Same(r.LocalRotations[i],f.After.LocalRotations[i]);}}});
            Test("全段 root 高度固定且质心保留源升降，骨长不变",()=>{
                var f=Frames()[0];var r=new Retargeter();r.TestSetup(f.Target,motion,f.Calibration,f.Source);var min=float.MaxValue;var max=float.MinValue;
                for(var frame=0f;frame<=motion.MaxFrame;frame+=15)
                {
                    r.Evaluate(frame,f.Calibration);Near(r.ModelPositions[r.HeightRootIndex].Y,-.03f);var p=f.Source.Find("下半身");var dy=r.SourceSolver!.Pose.Positions[p].Y-f.Source.Bones[p].RestPosition.Y;
                    Near(r.ModelPositions[r.CenterBoneIndex].Y,r.Tree!.RefModelPos[r.CenterBoneIndex].Y-.03f+dy*r.AutoPosScale);min=MathF.Min(min,r.ModelPositions[r.CenterBoneIndex].Y);max=MathF.Max(max,r.ModelPositions[r.CenterBoneIndex].Y);
                    for(var i=0;i<r.Tree.BoneCount;i++)if(i!=r.CenterBoneIndex&&i!=r.HeightRootIndex)Near(r.LocalPositions[i],r.Tree.RefLocalPos[i]);
                }
                Assert(max-min>.05f,"center accidentally locked");Console.WriteLine($"    Root stays -0.03; authored center range {min:0.000}..{max:0.000}");
            });
        }
        Console.WriteLine($"右手／根基准回归：{count-failed}/{count} 通过");return failed;
    }
}
