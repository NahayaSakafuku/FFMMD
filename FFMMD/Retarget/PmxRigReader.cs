using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace FFMMD.Retarget;

/// <summary>Reads only PMX skeletal metadata. Mesh, textures and physics are never loaded into the game.</summary>
public static class PmxRigReader
{
    public static SourceRigDefinition Parse(string path)
    {
        using var stream=File.OpenRead(path);
        if(stream.Length>512L*1024*1024)throw new InvalidDataException("PMX 文件超过 512 MB。");
        var fingerprint=Convert.ToHexString(SHA256.HashData(stream));stream.Position=0;
        return Parse(stream,fingerprint);
    }
    public static SourceRigDefinition Parse(byte[] bytes)
    {
        using var stream=new MemoryStream(bytes,false);return Parse(stream,Convert.ToHexString(SHA256.HashData(bytes)));
    }
    private static SourceRigDefinition Parse(Stream stream,string fingerprint)
    {
        using var r=new BinaryReader(stream,Encoding.UTF8,true);
        long Remaining()=>stream.Length-stream.Position;
        void Skip(long n){if(n<0||n>Remaining())throw new InvalidDataException("PMX 数据截断。");stream.Position+=n;}
        int Count(int minimum,int maximum=int.MaxValue)
        {var n=r.ReadInt32();if(n<0||n>maximum||(long)n*minimum>Remaining())throw new InvalidDataException("PMX 计数超出范围。");return n;}
        float Float(){var v=r.ReadSingle();if(!float.IsFinite(v))throw new InvalidDataException("PMX 包含非有限数值。");return v;}
        Vector3 Vec()=>new(Float(),Float(),Float());
        try
        {
            if(Encoding.ASCII.GetString(r.ReadBytes(4))!="PMX ")throw new InvalidDataException("不是 PMX 文件。");
            var version=Float();if(MathF.Abs(version-2)>1e-4f&&MathF.Abs(version-2.1f)>1e-4f)throw new InvalidDataException("仅支持 PMX 2.0 / 2.1。");
            var globals=r.ReadByte();if(globals<8)throw new InvalidDataException("PMX 全局参数不足。");
            var flags=r.ReadBytes(globals);if(flags.Length!=globals||flags[0]>1||flags[1]>4)throw new InvalidDataException("PMX 编码或 UV 参数无效。");
            for(var i=2;i<8;i++)if(flags[i] is not (1 or 2 or 4))throw new InvalidDataException("PMX 索引宽度无效。");
            var encoding=flags[0]==0?Encoding.Unicode:Encoding.UTF8;
            string Text(){var n=Count(1,1_048_576);var data=r.ReadBytes(n);if(data.Length!=n)throw new InvalidDataException("PMX 文本截断。");return encoding.GetString(data);}
            int Index(int size)=>size switch{1=>r.ReadSByte(),2=>r.ReadInt16(),4=>r.ReadInt32(),_=>throw new InvalidDataException("PMX 索引宽度无效。")};
            var name=Text();Text();Text();Text();
            var vertices=Count(38,10_000_000);
            for(var i=0;i<vertices;i++)
            {
                Skip(32+16*flags[1]);var skin=r.ReadByte();
                Skip(skin switch{0=>flags[5],1=>2*flags[5]+4,2 or 4=>4*flags[5]+16,3=>2*flags[5]+40,_=>throw new InvalidDataException("未知 PMX 蒙皮类型。")});Skip(4);
            }
            Skip((long)Count(flags[2])*flags[2]);
            var textures=Count(4,1_000_000);for(var i=0;i<textures;i++)Text();
            var materials=Count(70,1_000_000);
            for(var i=0;i<materials;i++)
            {
                Text();Text();Skip(65+2*flags[3]);Skip(1);var shared=r.ReadByte();
                if(shared==0)Skip(flags[3]);else if(shared==1)Skip(1);else throw new InvalidDataException("PMX toon 标志无效。");
                Text();Count(0);
            }
            var bones=new SourceBone[Count(22,8192)];var iks=new List<SourceIkChain>();var warnings=new List<string>();
            for(var i=0;i<bones.Length;i++)
            {
                var b=new SourceBone{Name=Text()};Text();b.RestPosition=Vec();b.Parent=Index(flags[5]);b.Layer=r.ReadInt32();b.Flags=r.ReadUInt16();
                if((b.Flags&1)!=0)b.TailBone=Index(flags[5]);else b.TailOffset=Vec();
                if((b.Flags&0x300)!=0){b.AppendParent=Index(flags[5]);b.AppendRatio=Float();}
                if((b.Flags&0x400)!=0)b.FixedAxis=Vec();
                if((b.Flags&0x800)!=0){b.LocalAxisX=Vec();b.LocalAxisZ=Vec();}
                if((b.Flags&0x2000)!=0){r.ReadInt32();warnings.Add($"{b.Name} 使用外部亲；没有对应外部变换时按原骨架父链计算。");}
                if((b.Flags&0x1000)!=0)warnings.Add($"{b.Name} 标记物理后变形；当前仅计算骨骼约束，不运行物理。");
                if((b.Flags&0x20)!=0)
                {
                    var ik=new SourceIkChain{Controller=i,Effector=Index(flags[5]),Iterations=Count(0,4096),AngleLimit=Float()};
                    ik.Links=new SourceIkLink[Count(flags[5]+1,256)];
                    for(var j=0;j<ik.Links.Length;j++)
                    {
                        var index=Index(flags[5]);var limited=r.ReadByte();if(limited>1)throw new InvalidDataException("PMX IK 限位标志无效。");
                        var min=limited==1?Vec():Vector3.Zero;var max=limited==1?Vec():Vector3.Zero;ik.Links[j]=new(index,limited==1,min,max);
                    }
                    iks.Add(ik);
                }
                bones[i]=b;
            }
            var rig=new SourceRigDefinition{Name=name,Fingerprint=fingerprint,Bones=bones,IkChains=iks.ToArray()};rig.Warnings.AddRange(warnings.Distinct());rig.Validate();return rig;
        }
        catch(EndOfStreamException e){throw new InvalidDataException("PMX 文件截断。",e);}
    }
}
