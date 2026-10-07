using System.Numerics;
using System.Text.Json;
using FFMMD;
using FFMMD.Posing;
using FFMMD.Retarget;
using FFMMD.Vmd;

static class AcceptanceRegression
{
    static readonly JsonSerializerOptions Json=new(){IncludeFields=true};
    static void Assert(bool value,string why="assertion failed"){if(!value)throw new Exception(why);}
    static void Near(Vector3 a,Vector3 b,float tolerance=1e-4f)=>Assert(Vector3.Distance(a,b)<tolerance,$"{a} != {b}");
    static void Same(Quaternion a,Quaternion b)=>Assert(MathF.Abs(Quaternion.Dot(a,b))>1-1e-5f,$"{a} != {b}");
    static SkeletonTree Character()
    {
        var path=Path.Combine(AppContext.BaseDirectory,"Fixtures","Current-character-172.json");
        var t=JsonSerializer.Deserialize<SkeletonTree>(File.ReadAllText(path),Json)!;t.RebuildReference();return t;
    }
    static VmdBoneKeyFrame Key(string bone,Quaternion rotation)=>new(){Bone=bone,Frame=0,Rotation=rotation,Interp=[20,20,20,20,20,20,20,20,107,107,107,107,107,107,107,107,..new byte[48]]};
    static VmdAnimation Motion(params VmdBoneKeyFrame[] keys)=>VmdAnimation.Build(new(){BoneFrames=keys.ToList()});
    static Retargeter Solve(SkeletonTree t,VmdAnimation motion,Calibration? c=null)
    {c??=new();var r=new Retargeter();r.TestSetup(t,motion,c);Assert(r.Evaluate(0,c));return r;}
    public sealed class AcceptanceFrame
    {
        public float Frame {get;set;} public string[] Names=[];public short[] Parent=[];
        public PoseSnapshot Before=new(),After=new(),Final=new();
    }
    static AcceptanceFrame[] Recorded()=>JsonSerializer.Deserialize<AcceptanceFrame[]>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures","Acceptance-v1.1.3.json")),Json)!;
    static SkeletonTree ArmTarget()
    {
        var t=new SkeletonTree{Names=["n_root","n_hara","j_kosi","j_sebo_a","j_sako_l","j_ude_a_l","j_ude_b_l","n_hhiji_l","n_hkata_l","j_te_l"],
            Parent=[-1,0,1,1,3,4,5,6,5,6],RefLocalPos=[Vector3.Zero,Vector3.Zero,Vector3.Zero,Vector3.UnitY,Vector3.Zero,Vector3.Zero,Vector3.UnitX,Vector3.Zero,Vector3.Zero,Vector3.UnitX],
            RefLocalRot=Enumerable.Repeat(Quaternion.Identity,10).ToArray(),RefLocalScale=Enumerable.Repeat(Vector3.One,10).ToArray()};
        t.RebuildReference();return t;
    }
    public static int Run(string? vmdPath)
    {
        var count=0;var failed=0;
        void Test(string name,Action body){count++;try{body();Console.WriteLine("  ✔ "+name);}catch(Exception e){failed++;Console.WriteLine("  ✘ "+name+": "+e.Message);}}
        Test("现场角色：172 骨、主臂直链与形变辅助叶骨",()=>{var t=Character();Assert(t.BoneCount==172);Assert(t.Parent[t.Find("j_ude_b_l")]==t.Find("j_ude_a_l"));Assert(!t.Parent.Contains((short)t.Find("n_hkata_l")));Assert(!t.Parent.Contains((short)t.Find("n_hhiji_l")));});
        Test("中立源：脊柱、脖子和头保留原生参考曲线",()=>{var t=Character();var r=Solve(t,Motion());foreach(var name in new[]{"j_sebo_a","j_sebo_b","j_sebo_c","j_kubi","j_kao"})Same(r.LocalRotations[t.Find(name)],t.RefLocalRot[t.Find(name)]);});
        Test("中立源：两只手的全部手指保留原生局部参考旋转",()=>{var t=Character();var r=Solve(t,Motion());foreach(var m in r.Mapped.Where(m=>m.FfName.StartsWith("j_oya_")||m.FfName.StartsWith("j_hito_")||m.FfName.StartsWith("j_naka_")||m.FfName.StartsWith("j_kusu_")||m.FfName.StartsWith("j_ko_")))Same(r.LocalRotations[m.FfIndex],t.RefLocalRot[m.FfIndex]);});
        Test("手腕单独旋转不产生额外拇指弯曲",()=>{var t=Character();var r=Solve(t,Motion(Key("左手首",Quaternion.CreateFromAxisAngle(Vector3.UnitY,.7f))));foreach(var name in new[]{"j_oya_a_l","j_oya_b_l"})Same(r.LocalRotations[t.Find(name)],t.RefLocalRot[t.Find(name)]);});
        Test("拇指中关节实际参与近节，未丢失源旋转",()=>{var t=Character();var neutral=Solve(t,Motion());var r=Solve(t,Motion(Key("左親指1",Quaternion.CreateFromAxisAngle(Vector3.UnitZ,.4f))));var i=t.Find("j_oya_a_l");Assert(MathF.Abs(Quaternion.Dot(r.LocalRotations[i],neutral.LocalRotations[i]))<.999f);Assert(r.Mapped.Single(m=>m.FfIndex==i).SourceJp=="左親指1");});
        Test("拇指运动在整体偏航后局部结果相同",()=>{var t=Character();var motion=Motion(Key("左親指0",Quaternion.CreateFromAxisAngle(Vector3.UnitX,.2f)),Key("左親指1",Quaternion.CreateFromAxisAngle(Vector3.UnitZ,.3f)));var a=Solve(t,motion);var b=Solve(t,motion,new(){YawDegrees=80});foreach(var name in new[]{"j_oya_a_l","j_oya_b_l"})Same(a.LocalRotations[t.Find(name)],b.LocalRotations[t.Find(name)]);});
        Test("MMD 扭转骨不再映射到 FF14 形变辅助骨",()=>{var r=Solve(Character(),Motion());Assert(!r.Mapped.Any(m=>m.FfName.StartsWith("n_hkata_")||m.FfName.StartsWith("n_hhiji_")));Assert(r.Mapped.Single(m=>m.FfName=="j_ude_a_l").OrientationSourceIndex==r.SourceRig!.Find("左腕捩"));});
        Test("独立直臂扭转 90°：主臂 90°，辅助反向 45°",()=>{var t=ArmTarget();var c=new Calibration{SourceRestPose=1};var r=Solve(t,Motion(Key("左腕捩",Quaternion.CreateFromAxisAngle(Vector3.UnitX,MathF.PI/2))),c);Same(r.ModelRotations[t.Find("j_ude_a_l")],Quaternion.CreateFromAxisAngle(Vector3.UnitX,MathF.PI/2));Same(r.LocalRotations[t.Find("n_hkata_l")],Quaternion.CreateFromAxisAngle(Vector3.UnitX,-MathF.PI/4));Near(r.ModelPositions[t.Find("j_ude_b_l")]-r.ModelPositions[t.Find("j_ude_a_l")],Vector3.UnitX);});
        Test("独立肘弯曲 90°：肘辅助反向 45°，长度不变",()=>{var t=ArmTarget();var c=new Calibration{SourceRestPose=1};var r=Solve(t,Motion(Key("左ひじ",Quaternion.CreateFromAxisAngle(Vector3.UnitZ,MathF.PI/2))),c);Same(r.LocalRotations[t.Find("n_hhiji_l")],Quaternion.CreateFromAxisAngle(Vector3.UnitZ,-MathF.PI/4));Near(r.ModelPositions[t.Find("j_te_l")]-r.ModelPositions[t.Find("j_ude_b_l")],Vector3.UnitY);});
        Test("高度偏移只移动整体：所有局部骨长和缩放保留",()=>{var t=Character();var a=Solve(t,Motion());var b=Solve(t,Motion(),new(){HeightOffset=.37f});for(var i=0;i<t.BoneCount;i++){Near(b.ModelPositions[i]-a.ModelPositions[i],new(0,.37f,0));Same(b.LocalRotations[i],a.LocalRotations[i]);Near(b.Tree!.RefLocalScale[i],t.RefLocalScale[i]);if(i!=b.HeightRootIndex)Near(b.LocalPositions[i],a.LocalPositions[i]);}});
        Test("高度归零、随机跳帧及幅度零不积累位移",()=>{var t=Character();var c=new Calibration{HeightOffset=.3f};var r=Solve(t,Motion(),c);r.Evaluate(20,c);c.HeightOffset=0;c.MotionScale=0;r.Evaluate(0,c);for(var i=0;i<t.BoneCount;i++)Near(r.ModelPositions[i],t.RefModelPos[i]);});
        Test("写入能力仅有旋转及根平移，没有缩放参数",()=>{var r=Solve(Character(),Motion(),new(){HeightOffset=.2f});foreach(var member in typeof(RotationTranslationWrite).GetProperties())Assert(!member.Name.Contains("Scale"));for(var i=0;i<r.Tree!.BoneCount;i++){var p=r.Tree.RefLocalPos[i];var q=r.Tree.RefLocalRot[i];var scale=new Vector3(1.23f,.81f,1.07f);var before=scale;var write=r.GetBoneWrite(i);write.Apply(ref p,ref q);Near(scale,before);if(i!=r.HeightRootIndex&&i!=r.CenterBoneIndex){Assert(!write.WriteTranslation);Near(p,r.Tree.RefLocalPos[i]);}}});
        Test("现场四帧：插件写入没有改变任何身体缩放",()=>{foreach(var f in Recorded()){var audit=RigWritePolicy.CompareScales(f.Names,f.Before,f.After);Assert(audit.ChangedBones.Length==0);Assert(audit.MaximumLocalScaleDelta<1e-5f);}});
        Test("现场四帧：最终阶段胸和尾巴调整单独识别",()=>{foreach(var f in Recorded()){var audit=RigWritePolicy.CompareScales(f.Names,f.After,f.Final);Assert(audit.ChangedBones.Length==3);Assert(audit.ChangedBones.Contains("body/j_mune_l")&&audit.ChangedBones.Contains("body/j_mune_r")&&audit.ChangedBones.Contains("body/n_sippo_b"));}});
        Test("缩放审计能独立检出非单位缩放及分段根变化",()=>{var a=new PoseSnapshot{LocalScales=[Vector3.One]};var b=new PoseSnapshot{LocalScales=[new(1,2,1)]};var audit=RigWritePolicy.CompareScales(["body"],a,a,[new(){PartialIndex=2,Names=["root"],Pose=a}],[new(){PartialIndex=2,Names=["root"],Pose=b}]);Assert(audit.MaximumLocalScaleDelta==1&&audit.ChangedBones.Single()=="partial2/root");});
        Test("1.1.3 配置迁移保留 PMX、朝向与播放设置",()=>{var c=new Config{RigPipelineVersion=2,SourcePmxPath="source.pmx",Loop=false,Speed=1.5f,Cal=new(){YawDegrees=-6,ManualPositionScale=.089f}};c.Normalize();Assert(c.RigPipelineVersion==5&&c.SourcePmxPath=="source.pmx"&&!c.Loop&&c.Speed==1.5f&&c.Cal.YawDegrees==-6&&c.Cal.HeightOffset==0);});
        Test("非法高度恢复为零并限制范围",()=>{var c=new Config{Cal=new(){HeightOffset=float.NaN}};c.Normalize();Assert(c.Cal.HeightOffset==0);c.Cal.HeightOffset=100;c.Normalize();Assert(c.Cal.HeightOffset==3);});
        if(vmdPath!=null)Test("当前角色 + bibbidiba：四个现场帧及全段骨长固定",()=>{
            var t=Character();var motion=VmdAnimation.Build(VmdFile.Parse(vmdPath));var c=new Calibration{YawDegrees=-6,HeightOffset=.18f};var r=new Retargeter();r.TestSetup(t,motion,c);
            var frames=Enumerable.Range(0,(int)motion.MaxFrame/15+1).Select(i=>i*15f).Concat(new[]{784.8f,935.1f,1235.1f,1385.1f});
            foreach(var frame in frames){Assert(r.Evaluate(frame,c));for(var i=0;i<t.BoneCount;i++){Near(r.Tree!.RefLocalScale[i],t.RefLocalScale[i]);if(i!=r.CenterBoneIndex&&i!=r.HeightRootIndex)Near(r.LocalPositions[i],t.RefLocalPos[i]);var p=t.Parent[i];if(p>=0&&i!=r.CenterBoneIndex){var length=Vector3.Distance(r.ModelPositions[i],r.ModelPositions[p]);var expected=(t.RefLocalPos[i]*t.RefModelScale[p]).Length();Assert(MathF.Abs(length-expected)<2e-4f,$"changed length: {t.Names[i]}");}}}
        });
        if(vmdPath!=null)Test("当前角色 + bibbidiba：上下臂方向适配误差 <1°",()=>{
            var t=Character();var motion=VmdAnimation.Build(VmdFile.Parse(vmdPath));var c=new Calibration{YawDegrees=-6};var r=new Retargeter();r.TestSetup(t,motion,c);
            var map=r.Profile!.CoordinateMap(r.SourceRig!,-6);
            foreach(var frame in new[]{784.8f,935.1f,1235.1f,1385.1f})
            {
                Assert(r.Evaluate(frame,c));
                foreach(var side in new[]{"l","r"})
                {
                    var jp=side=="l"?"左":"右";var rig=r.SourceRig!;var positions=r.SourceSolver!.Pose.Positions;
                    var arm=positions[rig.Find(jp+"腕")];var elbow=positions[rig.Find(jp+"ひじ")];var hand=positions[rig.Find(jp+"手首")];
                    var targetArm=r.ModelPositions[t.Find("j_ude_a_"+side)];var targetElbow=r.ModelPositions[t.Find("j_ude_b_"+side)];var targetHand=r.ModelPositions[t.Find("j_te_"+side)];
                    foreach(var pair in new[]{(map.Vector(elbow-arm),targetElbow-targetArm),(map.Vector(hand-elbow),targetHand-targetElbow)})
                        Assert(Vector3.Dot(Vector3.Normalize(pair.Item1),Vector3.Normalize(pair.Item2))>MathF.Cos(MathF.PI/180),$"arm direction: {side} at {frame}");
                }
            }
        });
        Console.WriteLine($"现场验收回归：{count-failed}/{count} 通过");return failed;
    }
}
