using System.Numerics;
using System.Text;
using System.Text.Json;
using FFMMD;
using FFMMD.Posing;
using FFMMD.Retarget;
using FFMMD.Vmd;

static class PipelineRegression
{
    static readonly JsonSerializerOptions Json=new(){IncludeFields=true,WriteIndented=true};
    static void Assert(bool value,string message="assertion failed"){if(!value)throw new Exception(message);}
    static void Near(Vector3 actual,Vector3 expected,float tolerance=1e-4f)=>Assert(Vector3.Distance(actual,expected)<tolerance,$"{actual} vs independent expected {expected}");
    static float Angle(Vector3 a,Vector3 b)=>MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(a),Vector3.Normalize(b)),-1,1))*180/MathF.PI;
    static Calibration Cal(int mode=2)=>new(){LegIkMode=mode,MotionScale=1};
    static VmdBoneKeyFrame Key(string bone,Vector3 pos,Quaternion? q=null,uint frame=0)=>new(){Bone=bone,Frame=frame,Position=pos,Rotation=q??Quaternion.Identity,Interp=[20,20,20,20,20,20,20,20,107,107,107,107,107,107,107,107,..new byte[48]]};
    static VmdAnimation Motion(params VmdBoneKeyFrame[] keys)=>VmdAnimation.Build(new(){BoneFrames=keys.ToList()});
    static SourceRigDefinition SimpleRig()
    {
        var bones=new List<SourceBone>{new(){Name="全ての親",RestPosition=Vector3.Zero},new(){Name="センター",Parent=0,RestPosition=Vector3.Zero},new(){Name="下半身",Parent=1,RestPosition=new(0,2,0)},new(){Name="頭",Parent=1,RestPosition=new(0,3,0)}};
        var chains=new List<SourceIkChain>();
        foreach(var side in new[]{"左","右"})
        {
            var x=side=="左"?.2f:-.2f;var h=bones.Count;
            bones.Add(new(){Name=side+"足",Parent=2,RestPosition=new(x,2,0)});bones.Add(new(){Name=side+"ひざ",Parent=h,RestPosition=new(x,1,0)});
            bones.Add(new(){Name=side+"足首",Parent=h+1,RestPosition=new(x,0,0)});bones.Add(new(){Name=side+"つま先",Parent=h+2,RestPosition=new(x,0,-.2f)});
            bones.Add(new(){Name=side+"足IK親",Parent=0,RestPosition=Vector3.Zero});bones.Add(new(){Name=side+"足IK",Parent=h+4,RestPosition=new(x,0,0)});bones.Add(new(){Name=side+"つま先IK",Parent=h+5,RestPosition=new(x,0,-.2f)});
            chains.Add(new(){Controller=h+5,Effector=h+2,AngleLimit=.5f,Links=[new(h+1,true,new(-MathF.PI,0,0),Vector3.Zero),new(h,false,Vector3.Zero,Vector3.Zero)]});
            chains.Add(new(){Controller=h+6,Effector=h+3,Iterations=16,AngleLimit=.5f,Links=[new(h+2,false,Vector3.Zero,Vector3.Zero)]});
        }
        var rig=new SourceRigDefinition{Name="Independent two-unit legs",Bones=bones.ToArray(),IkChains=chains.ToArray()};rig.Validate();return rig;
    }
    static SkeletonTree Target()
    {
        var tree=JsonSerializer.Deserialize<SkeletonTree>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures","AnimationKit-target.json")),Json)!;tree.RebuildReference();return tree;
    }
    static byte[] PmxBytes(SourceRigDefinition rig,float version,bool utf8)
    {
        using var stream=new MemoryStream();using var w=new BinaryWriter(stream);var encoding=utf8?Encoding.UTF8:Encoding.Unicode;
        void Text(string text){var bytes=encoding.GetBytes(text);w.Write(bytes.Length);w.Write(bytes);}
        void Vector(Vector3 value){w.Write(value.X);w.Write(value.Y);w.Write(value.Z);}
        w.Write(Encoding.ASCII.GetBytes("PMX "));w.Write(version);w.Write((byte)8);w.Write(new byte[]{(byte)(utf8?1:0),0,4,4,4,4,4,4});
        Text("独立骨架样例");Text("");Text("");Text("");for(var i=0;i<4;i++)w.Write(0);w.Write(rig.Bones.Length);
        for(var i=0;i<rig.Bones.Length;i++)
        {
            var bone=rig.Bones[i];var ik=rig.IkChains.FirstOrDefault(c=>c.Controller==i);
            var flags=(ushort)(bone.Flags|(ik==null?0:0x20));
            Text(bone.Name);Text("");Vector(bone.RestPosition);w.Write(bone.Parent);w.Write(bone.Layer);w.Write(flags);if((flags&1)!=0)w.Write(bone.TailBone);else Vector(bone.TailOffset);
            if((flags&0x300)!=0){w.Write(bone.AppendParent);w.Write(bone.AppendRatio);}
            if((flags&0x400)!=0)Vector(bone.FixedAxis);
            if((flags&0x800)!=0){Vector(bone.LocalAxisX);Vector(bone.LocalAxisZ);}
            if((flags&0x2000)!=0)w.Write(0);
            if(ik!=null){w.Write(ik.Effector);w.Write(ik.Iterations);w.Write(ik.AngleLimit);w.Write(ik.Links.Length);foreach(var link in ik.Links){w.Write(link.Bone);w.Write((byte)(link.Limited?1:0));if(link.Limited){Vector(link.Minimum);Vector(link.Maximum);}}}
        }
        return stream.ToArray();
    }
    static void Reject(Action action){try{action();}catch(InvalidDataException){return;}throw new Exception("invalid input accepted");}
    static void CompareLeg(Retargeter r,int side)
    {
        var source=r.SourceRig!;var pose=r.SourceSolver!.Pose;var p=side==0?"左":"右";var s=side==0?"l":"r";var target=r.Tree!;
        var h=target.Find("j_asi_a_"+s);var k=target.Find("j_asi_c_"+s);var a=target.Find("j_asi_d_"+s);
        var sh=r.Mapped.Single(m=>m.FfIndex==h).SourceIndex;var sk=r.Mapped.Single(m=>m.FfIndex==k).SourceIndex;var sa=r.Mapped.Single(m=>m.FfIndex==a).SourceIndex;
        var upper=pose.Positions[sk]-pose.Positions[sh];var lower=pose.Positions[sa]-pose.Positions[sk];
        var tu=r.ModelPositions[k]-r.ModelPositions[h];var tl=r.ModelPositions[a]-r.ModelPositions[k];
        var map=r.Profile!.CoordinateMap(source,0);
        Assert(MathF.Abs(Angle(upper,lower)-Angle(tu,tl))<1,$"{p} knee angle: source={Angle(upper,lower)} target={Angle(tu,tl)}");
        Assert(Angle(map.Vector(upper+lower),tu+tl)<1,$"{p} reach direction");
        var normal=Vector3.Cross(upper,lower);var targetNormal=Vector3.Cross(tu,tl);
        if(normal.LengthSquared()>1e-5f&&targetNormal.LengthSquared()>1e-8f)Assert(Angle(map.Vector(normal)*map.Parity,targetNormal)<1,$"{p} bend plane");
        Assert(Vector3.Distance(r.ModelPositions[a],r.LegGoals[side])<1e-3f,"target auxiliary-chain endpoint");
    }
    public static int Run(string? realPath)
    {
        var count=0;var failed=0;
        void Test(string name,Action action){count++;try{action();Console.WriteLine("  ✔ "+name);}catch(Exception e){failed++;Console.WriteLine("  ✘ "+name+": "+e.Message);}}
        Test("反射及旋转：坐标转换满足 C(Rv)=R'(Cv)",()=>{
            var maps=new[]{new RigCoordinateMap(Vector3.UnitX,Vector3.UnitY,-Vector3.UnitZ),new RigCoordinateMap(-Vector3.UnitZ,Vector3.UnitY,Vector3.UnitX)};
            foreach(var map in maps)foreach(var axis in new[]{Vector3.UnitX,Vector3.UnitY,Vector3.UnitZ}){var q=Quaternion.CreateFromAxisAngle(axis,.7f);var v=new Vector3(.3f,.7f,-.5f);Near(map.Vector(Vector3.Transform(v,q)),Vector3.Transform(map.Vector(v),map.Rotation(q)));}
        });
        Test("独立源骨架：中立关节位置",()=>{var rig=SimpleRig();var solver=new SourceRigSolver(rig,Motion());Assert(solver.Evaluate(0,Cal()));Near(solver.Pose.Positions[rig.Find("左足首")],new(.2f,0,0));Near(solver.Pose.Positions[rig.Find("左ひざ")],new(.2f,1,0));});
        Test("独立 FK：绕 Y 90° 的髋位置",()=>{var rig=SimpleRig();var solver=new SourceRigSolver(rig,Motion(Key("下半身",Vector3.Zero,Quaternion.CreateFromAxisAngle(Vector3.UnitY,MathF.PI/2))));solver.Evaluate(0,Cal(0));Near(solver.Pose.Positions[rig.Find("左足")],new(0,2,-.2f));});
        Test("独立 IK：单位腿下蹲，膝盖 z=-sqrt(3)/2",()=>{var rig=SimpleRig();var solver=new SourceRigSolver(rig,Motion(Key("センター",new(0,-1,0))));solver.Evaluate(0,Cal());Near(solver.Pose.Positions[rig.Find("左足首")],new(.2f,0,0));Near(solver.Pose.Positions[rig.Find("左ひざ")],new(.2f,.5f,-MathF.Sqrt(3)/2),3e-4f);});
        Test("独立 IK：抬脚到 y=1，膝盖 y=1.5",()=>{var rig=SimpleRig();var solver=new SourceRigSolver(rig,Motion(Key("左足IK",new(0,1,0))));solver.Evaluate(0,Cal());Near(solver.Pose.Positions[rig.Find("左足首")],new(.2f,1,0));Near(solver.Pose.Positions[rig.Find("左ひざ")],new(.2f,1.5f,-MathF.Sqrt(3)/2),3e-4f);});
        Test("固定脚转身：源脚目标保持原站位",()=>{var rig=SimpleRig();var solver=new SourceRigSolver(rig,Motion(Key("下半身",Vector3.Zero,Quaternion.CreateFromAxisAngle(Vector3.UnitY,MathF.PI/2)),Key("センター",new(0,-.4f,0))));Assert(solver.Evaluate(0,Cal()));Near(solver.Pose.Positions[rig.Find("左足首")],new(.2f,0,0),3e-4f);Near(solver.Pose.Positions[rig.Find("右足首")],new(-.2f,0,0),3e-4f);});
        Test("限步 CCD：完全直腿也向允许方向弯曲",()=>{var rig=SimpleRig();rig.IkChains[0].Iterations=16;rig.IkChains[0].AngleLimit=.1f;var solver=new SourceRigSolver(rig,Motion(Key("左足IK",new(0,.5f,0))));Assert(solver.Evaluate(0,Cal()));Assert(solver.Pose.Positions[rig.Find("左ひざ")].Z<-.01f);Assert(solver.Pose.LocalRotations[rig.Find("左ひざ")].X<=0);});
        Test("足 IK 亲位移在控制骨父链生效",()=>{var rig=SimpleRig();var solver=new SourceRigSolver(rig,Motion(Key("左足IK親",new(.3f,0,.1f)),Key("左足IK",new(0,.5f,0))));solver.Evaluate(0,Cal());Near(solver.Pose.Positions[rig.Find("左足首")],new(.5f,.5f,.1f),3e-4f);});
        Test("足尖 IK：控制骨 90° 转向经足尖解算传到脚",()=>{var rig=SimpleRig();var solver=new SourceRigSolver(rig,Motion(Key("左足IK",Vector3.Zero,Quaternion.CreateFromAxisAngle(Vector3.UnitY,MathF.PI/2))));solver.Evaluate(0,Cal());Near(solver.Pose.Positions[rig.Find("左つま先")],new(0,0,0),3e-4f);});
        Test("源 IK 开关与强制模式",()=>{var rig=SimpleRig();var motion=Motion(Key("センター",new(0,-1,0)));var file=new VmdFile{BoneFrames=[Key("センター",new(0,-1,0))],ShowIkFrames=[new(){Frame=0,Ik=[("左足IK",false),("左つま先IK",false)]}]};motion=VmdAnimation.Build(file);var solver=new SourceRigSolver(rig,motion);solver.Evaluate(0,Cal());Near(solver.Pose.Positions[rig.Find("左足首")],new(.2f,-1,0));solver.Evaluate(0,Cal(1));Near(solver.Pose.Positions[rig.Find("左足首")],new(.2f,0,0));});
        Test("追加位移与负旋转权重",()=>{
            var rig=new SourceRigDefinition{Bones=[new(){Name="donor"},new(){Name="receiver",AppendParent=0,AppendRatio=-1,Flags=0x380}]};rig.Validate();var solver=new SourceRigSolver(rig,Motion(Key("donor",new(1,2,3),Quaternion.CreateFromAxisAngle(Vector3.UnitY,.7f))));solver.Evaluate(0,Cal());Near(solver.Pose.Positions[1],new(-1,-2,-3));Near(Vector3.Transform(Vector3.UnitX,solver.Pose.Rotations[1]),new(MathF.Cos(.7f),0,MathF.Sin(.7f)));
        });
        Test("真实目标：186 骨及 j_asi_b 父链",()=>{var t=Target();Assert(t.BoneCount==186);Assert(t.Parent[t.Find("j_asi_c_l")]==t.Find("j_asi_b_l"));Assert(t.Parent[t.Find("j_asi_b_l")]==t.Find("j_asi_a_l"));});
        Test("源腿姿转移到真实目标：方向/角度/平面 <1°",()=>{var cal=Cal();var r=new Retargeter();r.TestSetup(Target(),Motion(Key("センター",new(0,-1,0)),Key("左足IK",new(.3f,.4f,.1f))),cal,SimpleRig());Assert(r.Evaluate(0,cal));CompareLeg(r,0);CompareLeg(r,1);});
        Test("增加后半段关键帧不改变首帧姿态",()=>{
            var cal=Cal();var a=Motion(Key("センター",new(1,-.25f,0)),Key("左足IK",new(.1f,.3f,-.2f)));var b=Motion(Key("センター",new(1,-.25f,0)),Key("左足IK",new(.1f,.3f,-.2f)),Key("センター",new(100,-4,100),frame:5000),Key("左足IK",new(-50,20,-100),frame:5000));
            var r=new Retargeter();var other=new Retargeter();r.TestSetup(Target(),a,cal);other.TestSetup(Target(),b,cal);r.Evaluate(0,cal);other.Evaluate(0,cal);for(var i=0;i<r.Tree!.BoneCount;i++)Near(r.ModelPositions[i],other.ModelPositions[i]);
        });
        Test("幅度 0 回到目标绑定姿态",()=>{var cal=Cal();cal.MotionScale=0;var r=new Retargeter();r.TestSetup(Target(),Motion(Key("センター",new(2,-3,4))),cal);r.Evaluate(0,cal);for(var i=0;i<r.Tree!.BoneCount;i++)Near(r.ModelPositions[i],r.Tree.RefModelPos[i]);});
        Test("标准骨架 A/T 切换与新实例一致",()=>{var cal=Cal();var r=new Retargeter();var fresh=new Retargeter();var motion=Motion();r.TestSetup(Target(),motion,cal);r.Evaluate(0,cal);cal.SourceRestPose=1;r.Evaluate(0,cal);fresh.TestSetup(Target(),motion,cal);fresh.Evaluate(0,cal);for(var i=0;i<r.Tree!.BoneCount;i++)Near(r.ModelPositions[i],fresh.ModelPositions[i]);Assert(r.SourceRig!.Name.Contains("T"));});
        Test("退化折腿和幅度零的诊断不依赖上一帧",()=>{var cal=Cal(0);var motion=Motion(Key("左ひざ",Vector3.Zero,Quaternion.CreateFromAxisAngle(Vector3.UnitX,-MathF.PI)),Key("左ひざ",Vector3.Zero,Quaternion.CreateFromAxisAngle(Vector3.UnitX,-.4f),frame:30));var rig=SimpleRig();var r=new Retargeter();var fresh=new Retargeter();r.TestSetup(Target(),motion,cal,rig);fresh.TestSetup(Target(),motion,cal,rig);Assert(r.Evaluate(30,cal)&&r.Evaluate(0,cal)&&fresh.Evaluate(0,cal));for(var i=0;i<r.Tree!.BoneCount;i++)Near(r.ModelPositions[i],fresh.ModelPositions[i]);for(var s=0;s<2;s++)Near(r.LegGoals[s],fresh.LegGoals[s]);cal.MotionScale=0;r.Evaluate(0,cal);for(var s=0;s<2;s++)Near(r.LegGoals[s],r.ModelPositions[r.Tree.Find("j_asi_d_"+(s==0?"l":"r"))]);});
        Test("D 形变腿单独关键帧参与最终腿姿",()=>{var cal=Cal(0);var r=new Retargeter();r.TestSetup(Target(),Motion(Key("左ひざD",Vector3.Zero,Quaternion.CreateFromAxisAngle(Vector3.UnitX,-.6f))),cal);Assert(r.Evaluate(0,cal));Assert(r.Mapped.Single(m=>m.FfName=="j_asi_c_l").SourceJp=="左ひざD");CompareLeg(r,0);CompareLeg(r,1);});
        Test("随机跳帧、重复、IK/FK 切换没有状态积累",()=>{
            var cal=Cal();var a=Motion(Key("センター",Vector3.Zero),Key("センター",new(2,-3,4),frame:100));var r=new Retargeter();var fresh=new Retargeter();r.TestSetup(Target(),a,cal);fresh.TestSetup(Target(),a,cal);
            foreach(var f in new[]{80f,10,0,90,15,22}){r.Evaluate(f,cal);cal.LegIkMode=0;r.Evaluate(f,cal);cal.LegIkMode=2;r.Evaluate(f,cal);fresh.Evaluate(f,cal);for(var i=0;i<r.Tree!.BoneCount;i++)Near(r.ModelPositions[i],fresh.ModelPositions[i]);}
        });
        Test("非法骨架依赖拒绝",()=>{var rig=SimpleRig();rig.Bones[0].Parent=2;try{rig.Validate();throw new Exception("accepted cycle");}catch(InvalidDataException){}});
        Test("迁移旧翻轴与中位数，不丢播放设置",()=>{var c=new Config{Loop=false,Speed=2,Cal=new(){RotateZ180=true,MedianBaseline=true,AutoPositionScale=false}};c.Normalize();Assert(c.RigPipelineVersion==5&&!c.Cal.RotateZ180&&!c.Cal.MedianBaseline&&c.Cal.AutoPositionScale&&c.Speed==2&&!c.Loop);});
        Test("PMX 2.0 UTF16 与 2.1 UTF8 骨架和 IK 限位",()=>{
            foreach(var version in new[]{2f,2.1f})foreach(var utf8 in new[]{false,true})
            {
                var rig=PmxRigReader.Parse(PmxBytes(SimpleRig(),version,utf8));Assert(rig.Find("左足IK")>=0&&rig.IkChains.Length==4&&rig.IkChains[0].Links[0].Limited);
                var solver=new SourceRigSolver(rig,Motion(Key("センター",new(0,-1,0))));solver.Evaluate(0,Cal());Near(solver.Pose.Positions[rig.Find("左ひざ")],new(.2f,.5f,-MathF.Sqrt(3)/2),3e-4f);
            }
        });
        Test("PMX 追加、局部轴、变形层保留",()=>{
            var original=SimpleRig();var b=original.Bones[3];b.Flags=0xB80;b.AppendParent=1;b.AppendRatio=-.5f;b.LocalAxisX=Vector3.UnitX;b.LocalAxisZ=Vector3.UnitZ;b.Layer=9;
            var rig=PmxRigReader.Parse(PmxBytes(original,2.1f,true));var bone=rig.Bones[3];Assert(bone.AppendLocal&&bone.AppendParent==1&&bone.AppendRatio==-.5f&&bone.Layer==9&&bone.LocalAxisX==Vector3.UnitX);
        });
        Test("PMX 截断、异常计数、编码与索引宽度拒绝",()=>{
            var bytes=PmxBytes(SimpleRig(),2.1f,true);Reject(()=>PmxRigReader.Parse(bytes[..^4]));
            var bad=(byte[])bytes.Clone();BitConverter.GetBytes(int.MaxValue).CopyTo(bad,17);Reject(()=>PmxRigReader.Parse(bad));
            bad=(byte[])bytes.Clone();bad[9]=3;Reject(()=>PmxRigReader.Parse(bad));bad=(byte[])bytes.Clone();bad[14]=3;Reject(()=>PmxRigReader.Parse(bad));
        });
        Test("PMX 非有限参考位置拒绝",()=>{var rig=SimpleRig();rig.Bones[0].RestPosition=new(float.NaN,0,0);Reject(()=>PmxRigReader.Parse(PmxBytes(rig,2,true)));});
        Test("PMX FK 固定轴只保留允许分量",()=>{var rig=new SourceRigDefinition{Bones=[new(){Name="axis",Flags=0x400,FixedAxis=Vector3.UnitY}]};rig.Validate();var s=new SourceRigSolver(rig,Motion(Key("axis",Vector3.Zero,Quaternion.CreateFromAxisAngle(Vector3.UnitX,.7f))));s.Evaluate(0,Cal());Near(Vector3.Transform(Vector3.UnitZ,s.Pose.Rotations[0]),Vector3.UnitZ);});
        Test("整体偏航同时旋转源朝向、腿方向和移动",()=>{
            var cal=Cal();cal.YawDegrees=90;var r=new Retargeter();r.TestSetup(Target(),Motion(Key("センター",Vector3.Zero),Key("センター",new(1,-1,2),frame:30)),cal,SimpleRig());Assert(r.Evaluate(30,cal));
            var pose=r.SourceSolver!.Pose;var source=r.SourceRig!;var direction=pose.Positions[source.Find("左足首")]-pose.Positions[source.Find("左足")];var map=r.Profile!.CoordinateMap(source,90);var t=r.Tree!;
            Assert(Angle(map.Vector(direction),r.ModelPositions[t.Find("j_asi_d_l")]-r.ModelPositions[t.Find("j_asi_a_l")])<1);
        });
        Test("诊断源姿态是快照，后续求解不改已捕获帧",()=>{var r=new Retargeter();var cal=Cal();r.TestSetup(Target(),Motion(Key("センター",Vector3.Zero),Key("センター",new(0,-2,0),frame:30)),cal);r.Evaluate(0,cal);var report=RigDiagnostics.Create(r,cal,0);var before=report.SourcePose!.Positions.ToArray();r.Evaluate(30,cal);for(var i=0;i<before.Length;i++)Near(report.SourcePose.Positions[i],before[i]);var json=JsonSerializer.Serialize(report,Json);Assert(json.Contains("SourceIkChains")&&json.Contains("Prepared"));});
        Test("目标有效尺寸和未覆盖骨骼基线保留",()=>{
            var t=Target();var scales=t.RefLocalScale.ToArray();scales[t.Find("j_asi_a_l")]=new(1.1f);var profile=new TargetRigProfile(t,null,scales);
            Assert(profile.Skeleton.RefLocalScale[t.Find("j_asi_a_l")].X==1.1f);var r=new Retargeter();r.TestSetup(profile.Skeleton,Motion(),Cal());Assert(!r.RotationWrites[t.Find("j_ago")]);
        });
        Test("求解热路径无分配",()=>{var r=new Retargeter();var cal=Cal();r.TestSetup(Target(),Motion(),cal);for(var i=0;i<10;i++)r.Evaluate(i,cal);var before=GC.GetAllocatedBytesForCurrentThread();for(var i=0;i<100;i++)r.Evaluate(i,cal);Assert(GC.GetAllocatedBytesForCurrentThread()==before,"allocation in evaluator");});
        var pmx=Environment.GetEnvironmentVariable("FFMMD_TEST_PMX");
        if(!string.IsNullOrWhiteSpace(pmx))Test("实际 Kaito PMX 读取及约束求解",()=>{var rig=PmxRigReader.Parse(pmx);Assert(rig.Bones.Length>50&&rig.IkChains.Length>=4);Assert(rig.Find("左足")>=0);var solver=new SourceRigSolver(rig,Motion());Assert(solver.Evaluate(0,Cal()));Console.WriteLine($"    PMX: {rig.Bones.Length} bones / {rig.IkChains.Length} IK chains");});
        if(realPath!=null)Test("真实 bibbidiba：全段抽样腿姿转移 <1°",()=>{
            var a=VmdAnimation.Build(VmdFile.Parse(realPath));var r=new Retargeter();var cal=Cal();r.TestSetup(Target(),a,cal);
            for(var f=0f;f<=a.MaxFrame;f+=7.5f){Assert(r.Evaluate(f,cal));CompareLeg(r,0);CompareLeg(r,1);}
            foreach(var sec in new[]{26.16f,31.17f,41.17f,46.17f}){Assert(r.Evaluate(sec*30,cal));CompareLeg(r,0);CompareLeg(r,1);}
            Assert(r.Evaluate(a.MaxFrame,cal));CompareLeg(r,0);CompareLeg(r,1);
            Console.WriteLine("    Checked actual 186-bone target, full motion range and video timestamps; source is standard approximate rig.");
        });
        if(realPath!=null&&!string.IsNullOrWhiteSpace(pmx))Test("实际 PMX 与 VMD 联合求解：有限姿态与腿姿转移",()=>{
            var rig=PmxRigReader.Parse(pmx);var animation=VmdAnimation.Build(VmdFile.Parse(realPath));var cal=Cal();var r=new Retargeter();r.TestSetup(Target(),animation,cal,rig);
            for(var frame=0f;frame<=animation.MaxFrame;frame+=30){Assert(r.Evaluate(frame,cal));CompareLeg(r,0);CompareLeg(r,1);}
            Console.WriteLine("    Kaito is a validation sample, not the motion's identified original model.");
        });
        Console.WriteLine($"骨架管线测试：{count-failed}/{count} 通过");return failed;
    }
}
