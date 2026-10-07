using System.Numerics;
using System.Text.Json;
using FFMMD;
using FFMMD.Posing;
using FFMMD.Retarget;
using FFMMD.Vmd;

static class FingerHeightRegression
{
    static readonly JsonSerializerOptions Json=new(){IncludeFields=true};
    public sealed class FrameData
    {
        public float Frame {get;set;}
        public SkeletonTree Target=new();public SourceRigDefinition Source=new();public Calibration Calibration=new();
        public Quaternion[] RecordedLocalRotations=[];public PoseSnapshot After=new();
    }
    static FrameData[] Frames()
    {
        var result=JsonSerializer.Deserialize<FrameData[]>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures","Acceptance-v1.1.4.json")),Json)!;
        foreach(var f in result){f.Target.RebuildReference();f.Source.Validate();}return result;
    }
    static void Assert(bool v,string why="assertion failed"){if(!v)throw new Exception(why);}
    static void Near(float a,float b,float eps=1e-5f)=>Assert(MathF.Abs(a-b)<eps,$"{a} != {b}");
    static void Near(Vector3 a,Vector3 b,float eps=1e-4f)=>Assert(Vector3.Distance(a,b)<eps,$"{a} != {b}");
    static void Same(Quaternion a,Quaternion b)=>Assert(MathF.Abs(Quaternion.Dot(a,b))>1-1e-5f);
    static float Rad(float degrees)=>degrees*MathF.PI/180;
    static VmdBoneKeyFrame Key(string bone,Quaternion rotation,uint frame=0,Vector3 position=default)=>new(){Bone=bone,Frame=frame,Rotation=rotation,Position=position,Interp=[20,20,20,20,20,20,20,20,107,107,107,107,107,107,107,107,..new byte[48]]};
    static VmdAnimation Motion(params VmdBoneKeyFrame[] keys)=>VmdAnimation.Build(new(){BoneFrames=keys.ToList()});
    static void CheckFourFingers(Retargeter r)
    {
        foreach(var m in r.Mapped)
        {
            var name=m.FfName;
            if(!name.StartsWith("j_hito_")&&!name.StartsWith("j_naka_")&&!name.StartsWith("j_kusu_")&&!name.StartsWith("j_ko_"))continue;
            var t=r.Tree!;var q=Quaternion.Conjugate(t.RefLocalRot[m.FfIndex])*r.LocalRotations[m.FfIndex];var euler=RigMath.Euler(q);
            Near(euler.X,0,2e-4f);var proximal=t.Names[t.Parent[m.FfIndex]].StartsWith("j_te_",StringComparison.Ordinal);
            Assert(MathF.Abs(euler.Z)<(proximal?FingerJointMath.ProximalFlexionLimit:FingerJointMath.DistalFlexionLimit)+2e-4f,$"flexion {name}: {euler}");
            Assert(MathF.Abs(euler.Y)<FingerJointMath.SpreadLimit+2e-4f,$"spread {name}: {euler}");
            if(!proximal)Near(euler.Y,0,2e-4f);
        }
    }
    public static int Run(string? vmdPath)
    {
        var count=0;var failed=0;
        void Test(string name,Action action){count++;try{action();Console.WriteLine("  ✔ "+name);}catch(Exception e){failed++;Console.WriteLine("  ✘ "+name+": "+e.Message);}}
        Test("独立合并：90°+90° 等长两节的弦方向为 135°",()=>Near(FingerJointMath.EquivalentDistalCurl(Rad(90),Rad(90),1,1),Rad(135)));
        Test("独立合并：长 2/1 两节弦方向为 116.565°",()=>Near(FingerJointMath.EquivalentDistalCurl(Rad(90),Rad(90),2,1),Rad(116.56505f)));
        Test("退化弦、无末节和高弯曲角保持有限及同向",()=>{Near(FingerJointMath.EquivalentDistalCurl(Rad(-60),Rad(-180),1,1),Rad(-150));Near(FingerJointMath.EquivalentDistalCurl(.4f,1,1,0),.4f);Near(FingerJointMath.LimitCurl(Rad(-220),-1,false),Rad(-110));});
        Test("四指角提取不受四元数符号影响",()=>{var q=Quaternion.CreateFromAxisAngle(Vector3.UnitZ,Rad(-100));var negative=new Quaternion(-q.X,-q.Y,-q.Z,-q.W);Near(FingerJointMath.SignedAxisAngle(q,Vector3.UnitZ),FingerJointMath.SignedAxisAngle(negative,Vector3.UnitZ));});
        Test("四指伸展、弯曲及摊开界限明确",()=>{Near(FingerJointMath.LimitCurl(Rad(-150),-1,true),Rad(-95));Near(FingerJointMath.LimitCurl(Rad(60),-1,true),Rad(15));Near(FingerJointMath.LimitCurl(Rad(150),1,false),Rad(110));Near(FingerJointMath.LimitCurl(Rad(-60),1,false),Rad(-10));});
        Test("双手四指 90° 抓握：按镜像掌面区分左右侧",()=>{
            var f=Frames()[2];var keys=new List<VmdBoneKeyFrame>();
            foreach(var side in new[]{"左","右"})foreach(var finger in new[]{"人差指","中指","薬指","小指"})
                for(var joint=1;joint<=3;joint++)keys.Add(Key(side+finger+joint,Quaternion.CreateFromAxisAngle(Vector3.UnitZ,Rad(side=="左"?-90:90))));
            var c=new Calibration();var r=new Retargeter();r.TestSetup(f.Target,Motion(keys.ToArray()),c);Assert(r.Evaluate(0,c));
            foreach(var side in new[]{"l","r"})
            {
                var palm=Vector3.Transform(side=="l"?-Vector3.UnitZ:Vector3.UnitZ,r.ModelRotations[f.Target.Find("j_te_"+side)]);
                foreach(var digit in new[]{"hito","naka","kusu","ko"})
                {
                    var a=f.Target.Find("j_"+digit+"_a_"+side);var b=f.Target.Find("j_"+digit+"_b_"+side);
                    var direction=Vector3.Normalize(r.ModelPositions[b]-r.ModelPositions[a]);Assert(Vector3.Dot(direction,palm)>.995f,digit+" points away from palm");
                }
            }
            CheckFourFingers(r);
        });
        Test("源 PMX 骨架及现场 106/172 骨样例可以复建",()=>{var f=Frames();Assert(f.Length==3&&f[0].Target.BoneCount==106&&f[2].Target.BoneCount==172);Assert(f.All(x=>x.Source.Bones.Length==132));});
        Test("倾斜源根及运动不能改变目标根的放置高度",()=>{
            var f=Frames()[0];var motion=Motion(Key("全ての親",Quaternion.CreateFromAxisAngle(Vector3.UnitX,Rad(25))),Key("センター",Quaternion.Identity),Key("センター",Quaternion.Identity,100,new(0,0,-100)));
            var c=new Calibration{LockHeight=true,HeightOffset=.12f};var r=new Retargeter();r.TestSetup(f.Target,motion,c,f.Source);
            foreach(var frame in new[]{0f,15,30,80,100,20,0}){Assert(r.Evaluate(frame,c));Near(r.ModelPositions[r.HeightRootIndex].Y,r.Tree!.RefModelPos[r.HeightRootIndex].Y+.12f);}
        });
        Test("旧质心锁定字段不再生效，蹲起与根高度独立",()=>{
            var f=Frames()[0];var motion=Motion(Key("センター",Quaternion.Identity,0,new(0,-2,0)));var c=new Calibration{LockHeight=true,HeightOffset=.2f};var r=new Retargeter();r.TestSetup(f.Target,motion,c,f.Source);r.Evaluate(0,c);var y=r.ModelPositions[r.CenterBoneIndex].Y;c.LockHeight=false;r.Evaluate(0,c);Near(r.ModelPositions[r.CenterBoneIndex].Y,y);Assert(y<r.Tree!.RefModelPos[r.CenterBoneIndex].Y+.19f);Near(r.ModelPositions[r.HeightRootIndex].Y,r.Tree.RefModelPos[r.HeightRootIndex].Y+.2f);c.MotionScale=0;r.Evaluate(0,c);Near(r.ModelPositions[r.CenterBoneIndex].Y,r.Tree.RefModelPos[r.CenterBoneIndex].Y+.2f);
        });
        Test("配置迁移解除质心锁定并保留手动根高度和 PMX",()=>{var c=new Config{RigPipelineVersion=3,SourcePmxPath="model.pmx",Cal=new(){HeightOffset=.01f,YawDegrees=-6,LockHeight=false}};c.Normalize();Assert(c.RigPipelineVersion==5&&!c.Cal.LockHeight&&c.Cal.HeightOffset==.01f&&c.Cal.YawDegrees==-6&&c.SourcePmxPath=="model.pmx");c.Cal.LockHeight=true;c.Normalize();Assert(!c.Cal.LockHeight);});
        var pmx=Environment.GetEnvironmentVariable("FFMMD_TEST_PMX");
        if(!string.IsNullOrWhiteSpace(pmx))Test("实际 PMX 保留末端参考信息且尺寸有限",()=>{var rig=PmxRigReader.Parse(pmx);Assert(rig.Bones.Any(b=>b.TailOffset.LengthSquared()>0||b.TailBone>=0));foreach(var b in rig.Bones)Assert(float.IsFinite(b.TailOffset.LengthSquared()));});
        if(vmdPath!=null)
        {
            var animation=VmdAnimation.Build(VmdFile.Parse(vmdPath));
            Test("复现现场源骨架和动作关键帧，作为修复输入",()=>{foreach(var f in Frames()){var solver=new SourceRigSolver(f.Source,animation);Assert(solver.Evaluate(f.Frame,f.Calibration));foreach(var name in new[]{"左中指1","左中指2","左中指3","右小指2"}){var i=f.Source.Find(name);Same(solver.Pose.LocalRotations[i],f.RecordedLocalRotations[i]);}}});
            Test("现场抓握三帧：四指无轴向扭转／远节不反折",()=>{foreach(var f in Frames()){var r=new Retargeter();r.TestSetup(f.Target,animation,f.Calibration,f.Source);Assert(r.Evaluate(f.Frame,f.Calibration));CheckFourFingers(r);var i=f.Target.Find("j_naka_b_l");var delta=Quaternion.Conjugate(r.Tree!.RefLocalRot[i])*r.LocalRotations[i];Console.WriteLine($"    {f.Frame:0.00}: middle distal flexion {RigMath.Euler(delta).Z*180/MathF.PI:0.00} degrees");}});
            Test("已验收颈／大臂／拇指旋转与 1.1.4 现场结果一致",()=>{foreach(var f in Frames()){var r=new Retargeter();r.TestSetup(f.Target,animation,f.Calibration,f.Source);Assert(r.Evaluate(f.Frame,f.Calibration));foreach(var name in new[]{"j_kubi","j_kao","j_ude_a_l","j_ude_b_l","j_ude_a_r","j_ude_b_r","j_oya_a_l","j_oya_b_l","j_oya_a_r","j_oya_b_r"}){var i=f.Target.Find(name);Same(r.LocalRotations[i],f.After.LocalRotations[i]);}}});
            Test("旧锁高字段不再改变动作结果",()=>{foreach(var f in Frames()){var r=new Retargeter();r.TestSetup(f.Target,animation,f.Calibration,f.Source);f.Calibration.LockHeight=false;r.Evaluate(f.Frame,f.Calibration);var before=r.ModelPositions.ToArray();var rotations=r.LocalRotations.ToArray();f.Calibration.LockHeight=true;r.Evaluate(f.Frame,f.Calibration);for(var i=0;i<f.Target.BoneCount;i++){Same(rotations[i],r.LocalRotations[i]);Near(before[i].X,r.ModelPositions[i].X);Near(before[i].Z,r.ModelPositions[i].Z);}Near(r.ModelPositions[r.HeightRootIndex].Y,r.Tree!.RefModelPos[r.HeightRootIndex].Y+f.Calibration.HeightOffset);}});
            Test("全段 PMX + VMD：根高度固定，质心升降保留，四指和骨长受约束",()=>{
                foreach(var f in new[]{Frames()[0],Frames()[2]})
                {
                    var c=f.Calibration;c.LockHeight=true;var r=new Retargeter();
                    var candidate=!string.IsNullOrWhiteSpace(pmx)?PmxRigReader.Parse(pmx):f.Source;
                    var source=candidate.Fingerprint==f.Source.Fingerprint?candidate:f.Source;
                    r.TestSetup(f.Target,animation,c,source);
                    for(var frame=0f;frame<=animation.MaxFrame;frame+=15)
                    {
                        Assert(r.Evaluate(frame,c));CheckFourFingers(r);Near(r.ModelPositions[r.HeightRootIndex].Y,r.Tree!.RefModelPos[r.HeightRootIndex].Y+c.HeightOffset);var pelvis=r.SourceRig!.Find("下半身");var authoredY=(r.SourceSolver!.Pose.Positions[pelvis].Y-r.SourceRig.Bones[pelvis].RestPosition.Y)*r.AutoPosScale;Near(r.ModelPositions[r.CenterBoneIndex].Y,r.Tree.RefModelPos[r.CenterBoneIndex].Y+c.HeightOffset+authoredY);
                        for(var i=0;i<r.Tree.BoneCount;i++)if(i!=r.CenterBoneIndex&&i!=r.HeightRootIndex)Near(r.LocalPositions[i],r.Tree.RefLocalPos[i]);
                    }
                }
            });
            Test("抓握／锁高随机跳帧及循环起点没有状态积累",()=>{var f=Frames()[2];f.Calibration.LockHeight=true;var r=new Retargeter();var fresh=new Retargeter();r.TestSetup(f.Target,animation,f.Calibration,f.Source);fresh.TestSetup(f.Target,animation,f.Calibration,f.Source);foreach(var frame in new[]{4278.4243f,2596.7998f,0,1768.8053f,4891,0,58.3f}){r.Evaluate(frame,f.Calibration);fresh.Evaluate(frame,f.Calibration);for(var i=0;i<f.Target.BoneCount;i++){Same(r.LocalRotations[i],fresh.LocalRotations[i]);Near(r.ModelPositions[i],fresh.ModelPositions[i]);}}});
            Test("PMX 抓握新热路径仍为零分配",()=>{var f=Frames()[0];var r=new Retargeter();r.TestSetup(f.Target,animation,f.Calibration,f.Source);for(var j=0;j<10;j++)r.Evaluate(2596.7998f,f.Calibration);var before=GC.GetAllocatedBytesForCurrentThread();for(var j=0;j<50;j++)r.Evaluate(2596.7998f+j,f.Calibration);Assert(GC.GetAllocatedBytesForCurrentThread()==before);});
        }
        Console.WriteLine($"四指／锁高回归：{count-failed}/{count} 通过");return failed;
    }
}
