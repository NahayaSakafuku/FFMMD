using System.Numerics;
using System.Text;
using FFMMD.Skirt;

internal static class PmxPhysicsProfileRegression
{
    public static int Run()
    {
        var failures=0;var count=0;
        void Check(string name,Action body)
        {
            count++;
            try{body();Console.WriteLine($"  ✔ {name}");}
            catch(Exception e){failures++;Console.WriteLine($"  ✘ {name}: {e.Message}");}
        }
        Check("PMX 物理原始坐标、允许碰撞 mask、mode 2 和六轴约束完整保留",()=>
        {
            var p=PmxPhysicsProfileReader.Parse(Fixture());
            Require(p.BoneNames.SequenceEqual(new[]{"bone"})&&p.RigidBodies.Length==2&&p.Joints.Length==1,"section counts");
            var b=p.RigidBodies[1];
            Require(b.Mode==PmxRigidBodyMode.PhysicsAndBone&&b.CollisionMask==0x8080&&b.CollisionGroup==12,"mode/mask");
            Require(b.Position==new Vector3(1,2,3)&&b.RotationEuler==new Vector3(.1f,.2f,.3f)&&b.Size==new Vector3(1,2,.3f),"source units");
            Require(p.Joints[0].RigidBodyA==0&&p.Joints[0].RigidBodyB==1&&!p.HasUnsupportedPhysics,"joint identities");
        });
        foreach(var entry in new (string Name,byte[] Bytes)[]
        {
            ("非有限质量",Fixture(mass:float.NaN)),("负形状尺寸",Fixture(radius:-1)),
            ("零形状尺寸",Fixture(radius:0)),("无效动态模式",Fixture(mode:3)),
            ("无效碰撞形状",Fixture(shape:3)),("无效碰撞组",Fixture(group:16)),
            ("越界阻尼",Fixture(damping:1.1f)),("动态刚体零质量",Fixture(mass:0)),
            ("负弹簧刚度",Fixture(spring:-1)),
        })Check($"PMX 物理拒绝{entry.Name}",()=>Reject(entry.Bytes));
        Check("PMX 物理每个截断点都拒绝",()=>
        {
            var bytes=Fixture();for(var i=0;i<bytes.Length;i++)Reject(bytes[..i]);
        });
        Check("PMX 2.1 非六轴 spring 关节显式标为未支持",()=>
        {
            var p=PmxPhysicsProfileReader.Parse(Fixture(version:2.1f,jointType:1));
            Require(p.HasUnsupportedPhysics&&p.Joints[0].Type==1,"unsupported joint was hidden");
        });
        Check("PMX 物理大计数在分配前拒绝",()=>
        {
            var bytes=Fixture();const int vertexCountOffset=17+4+7+3*4;
            BitConverter.GetBytes(int.MaxValue).CopyTo(bytes,vertexCountOffset);Reject(bytes);
        });
        Check("PMX 物理输入不被解析器修改",()=>
        {
            var bytes=Fixture();var copy=(byte[])bytes.Clone();PmxPhysicsProfileReader.Parse(bytes);
            Require(bytes.SequenceEqual(copy),"input bytes were changed");
        });
        var path=Environment.GetEnvironmentVariable("FFMMD_TEST_PMX");
        if(!string.IsNullOrWhiteSpace(path))Check("真实 PMX 裙骨物理与骨索引关联完整",()=>
        {
            var p=PmxPhysicsProfileReader.Parse(path);
            Require(p.RigidBodies.All(b=>b.BoneIndex<0||b.BoneIndex<p.BoneCount),"bone references");
            Require(p.Joints.All(j=>j.RigidBodyA<0||j.RigidBodyA<p.RigidBodies.Length),"joint A references");
            Require(p.Joints.All(j=>j.RigidBodyB<0||j.RigidBodyB<p.RigidBodies.Length),"joint B references");
        });
        Console.WriteLine($"PMX 物理 profile: {count-failures}/{count}");
        return failures;
    }

    private static void Require(bool value,string message){if(!value)throw new InvalidDataException(message);}
    private static void Reject(byte[] bytes)
    {
        try{PmxPhysicsProfileReader.Parse(bytes);}
        catch(InvalidDataException){return;}
        throw new InvalidDataException("Expected the PMX reader to reject this sample.");
    }
    private static byte[] Fixture(float version=2,float mass=.3f,float radius=1,float damping=.5f,
        byte mode=2,byte jointType=0,byte group=12,byte shape=1,float spring=0)
    {
        using var stream=new MemoryStream();using var w=new BinaryWriter(stream,Encoding.UTF8,true);
        void Text(string s){var b=Encoding.UTF8.GetBytes(s);w.Write(b.Length);w.Write(b);}
        void Vec(float a=0,float b=0,float c=0){w.Write(a);w.Write(b);w.Write(c);}
        w.Write(Encoding.ASCII.GetBytes("PMX "));w.Write(version);w.Write((byte)8);
        w.Write(new byte[]{1,0,1,1,1,1,1,1});Text("fixture");Text("");Text("");Text("");
        for(var i=0;i<4;i++)w.Write(0);
        w.Write(1);Text("bone");Text("");Vec();w.Write((sbyte)-1);w.Write(0);w.Write((ushort)0);Vec();
        w.Write(0);w.Write(0);w.Write(2);
        for(var i=0;i<2;i++)
        {
            Text("body"+i);Text("");w.Write((sbyte)0);w.Write(group);w.Write((ushort)0x8080);w.Write(shape);
            Vec(radius,2,.3f);Vec(1,2,3);Vec(.1f,.2f,.3f);w.Write(i==0?0:mass);w.Write(damping);
            w.Write(.5f);w.Write(0f);w.Write(.1f);w.Write(i==0?(byte)0:mode);
        }
        w.Write(1);Text("joint");Text("");w.Write(jointType);w.Write((sbyte)0);w.Write((sbyte)1);
        for(var i=0;i<6;i++)Vec();Vec(spring);Vec();
        if(version>2.05f)w.Write(0);
        w.Flush();return stream.ToArray();
    }
}
