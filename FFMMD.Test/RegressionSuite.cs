using System.Numerics;
using System.Text;
using FFMMD.Vmd;

internal static class RegressionSuite
{
    public static int Run(string? realPath)
    {
        var failed = 0;
        var count = 0;
        void Test(string name, Action body)
        {
            count++;
            try { body(); Console.WriteLine($"  ✔ {name}"); }
            catch (Exception e) { failed++; Console.WriteLine($"  ✘ {name}: {e.Message}"); }
        }
        Test("独立标准字节：四通道插值布局", () => {
            byte[] data = new byte[64];
            byte[] rows = [10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 120, 115, 116, 117, 118];
            rows.CopyTo(data, 0);
            for (var i = 0; i < 4; i++) {
                var curve = VmdBoneKeyFrame.ParseInterp(data, i);
                Assert(curve.P1x == rows[i] && curve.P1y == rows[4+i] && curve.P2x == rows[8+i] && curve.P2y == rows[12+i]);
            }
        });
        Test("终点慢启动曲线独立数值基准", () => {
            var a = Key("センター", 0, Vector3.Zero);
            var b = Key("センター", 30, new Vector3(10));
            b.Interp[0] = b.Interp[8] = 107; b.Interp[4] = b.Interp[12] = 20;
            var animation = VmdAnimation.Build(new VmdFile { BoneFrames = [a,b] });
            var p = animation.Tracks["センター"].Sample(15).Pos;
            Assert(MathF.Abs(p.X - 1.073033f) < 1e-4f && MathF.Abs(p.Y - 5) < 1e-4f);
        });
        Test("相机、灯光、自阴影、IK 同文件无错位", () => {
            var f = VmdFile.Parse(StandardFile());
            var c = f.CameraFrames.Single();
            Assert(c.Frame == 12 && c.Distance == -45 && c.ViewAngle == 45 && c.Perspective);
            Near(c.Rotation, new Vector3(.1f,.2f,.3f));
            Assert(c.Interp.Length == 24 && f.ShowIkFrames.Count == 2 && f.ShowIkFrames[1].Frame == 30);
            Assert(f.ShowIkFrames[0].Show && f.ShowIkFrames[0].Ik.Single() == ("右足ＩＫ", true));
            var anim = VmdAnimation.Build(f);
            Assert(anim.IsIkEnabled("右足IK", 29.5f) && !anim.IsIkEnabled("右足ＩＫ", 30));
        });
        Test("相机透视标志 1 表示关闭", () => Assert(!VmdFile.Parse(StandardFile(perspectiveFlag: 1)).CameraFrames[0].Perspective));
        Test("VMD 旧版模型名 10 字节", () => Assert(VmdFile.Parse(StandardFile(oldHeader: true)).ModelName == "Audit"));
        Test("允许在完整段边界结束的旧动作", () => {
            var bytes = StandardFile();
            Assert(VmdFile.Parse(bytes[..54]).BoneFrames.Count == 0);
            Throws<InvalidDataException>(() => VmdFile.Parse(bytes[..55]));
        });
        Test("拒绝截断的已声明 IK 记录", () => Throws<InvalidDataException>(() => VmdFile.Parse(StandardFile()[..^1])));
        Test("拒绝非法头标识", () => { var bytes = StandardFile(); bytes[0] = (byte)'X'; Throws<InvalidDataException>(() => VmdFile.Parse(bytes)); });
        Test("计数越界拒绝，避免溢出或长循环", () => {
            var bytes = StandardFile(); BitConverter.GetBytes(uint.MaxValue).CopyTo(bytes, 50);
            Throws<InvalidDataException>(() => VmdFile.Parse(bytes));
        });
        Test("零四元数回退单位旋转", () => {
            var f = VmdFile.Parse(StandardFile(bone: Key("センター",0,Vector3.Zero,default(Quaternion))));
            SameRotation(f.BoneFrames[0].Rotation, Quaternion.Identity);
        });
        Test("非有限输入不进入姿态写入", () => Throws<InvalidDataException>(() => VmdFile.Parse(StandardFile(bone: Key("センター",0,new Vector3(float.NaN,0,0))))));
        Test("重复帧采用最后记录且采样有限", () => {
            var a = VmdAnimation.Build(new VmdFile { BoneFrames = [Key("骨",0,Vector3.One),Key("骨",0,new Vector3(2)),Key("骨",1,new Vector3(3))] });
            Near(a.Tracks["骨"].Sample(0).Pos, new Vector3(2));
            Near(a.Tracks["骨"].Sample(.5f).Pos, new Vector3(2.5f));
        });
        Test("未到首个 IK 开关帧默认启用", () => {
            var a = VmdAnimation.Build(new VmdFile { ShowIkFrames = [new() { Frame = 30, Ik = [("左足ＩＫ",false)] }] });
            Assert(a.IsIkEnabled("左足IK", 0) && !a.IsIkEnabled("左足IK",30));
        });
        Console.WriteLine($"回归测试：{count-failed}/{count} 通过");
        return failed + PipelineRegression.Run(realPath) + AcceptanceRegression.Run(realPath) + FingerHeightRegression.Run(realPath) + RightHandGroundRegression.Run(realPath);
    }

    private static void Assert(bool condition,string message="assertion failed") { if(!condition) throw new Exception(message); }
    private static void Near(Vector3 a,Vector3 b,float epsilon=1e-4f) => Assert(Vector3.Distance(a,b)<epsilon,$"{a} != {b}, error={Vector3.Distance(a,b)}");
    private static void SameRotation(Quaternion a,Quaternion b) => Assert(MathF.Abs(Quaternion.Dot(a,b))>1-1e-5f,$"rotation mismatch: {a} vs {b}");
    private static void Throws<T>(Action action) where T:Exception { try { action(); } catch(T) { return; } throw new Exception($"Expected {typeof(T).Name}"); }
    private static VmdBoneKeyFrame Key(string name,uint frame,Vector3 pos,Quaternion? rotation=null) => new() { Bone=name,Frame=frame,Position=pos,Rotation=rotation??Quaternion.Identity,Interp=Linear() };
    private static byte[] Linear() { var bytes=new byte[64]; byte[] first=[20,20,20,20,20,20,20,20,107,107,107,107,107,107,107,107]; first.CopyTo(bytes,0); return bytes; }
    private static byte[] StandardFile(bool oldHeader=false,byte perspectiveFlag=0,VmdBoneKeyFrame? bone=null)
    {
        using var ms=new MemoryStream(); using var w=new BinaryWriter(ms);
        void Str(string value,int length) { var bytes=new byte[length]; Encoding.GetEncoding(932).GetBytes(value).CopyTo(bytes,0); w.Write(bytes); }
        Str(oldHeader?"Vocaloid Motion Data file":"Vocaloid Motion Data 0002",30); Str("Audit",oldHeader?10:20);
        w.Write(bone==null?0u:1u);
        if(bone!=null) { Str(bone.Bone,15); w.Write(bone.Frame); w.Write(bone.Position.X); w.Write(bone.Position.Y); w.Write(bone.Position.Z);
            w.Write(bone.Rotation.X); w.Write(bone.Rotation.Y); w.Write(bone.Rotation.Z); w.Write(bone.Rotation.W); w.Write(bone.Interp); }
        w.Write(0u); w.Write(1u); w.Write(12u); w.Write(-45f);
        w.Write(1f); w.Write(2f); w.Write(3f); w.Write(.1f); w.Write(.2f); w.Write(.3f); w.Write(new byte[24]); w.Write(45u); w.Write(perspectiveFlag);
        w.Write(1u); w.Write(5u); for(var i=0;i<6;i++) w.Write((float)i);
        w.Write(1u); w.Write(9u); w.Write((byte)1); w.Write(.05f);
        w.Write(2u); foreach(var frame in new uint[]{0,30}) { w.Write(frame); w.Write((byte)1); w.Write(1u); Str("右足ＩＫ",20); w.Write((byte)(frame==0?1:0)); }
        return ms.ToArray();
    }
}
