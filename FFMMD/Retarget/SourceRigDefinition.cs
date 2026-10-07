using System.Numerics;
using FFMMD.Posing;
using FFMMD.Vmd;

namespace FFMMD.Retarget;

public sealed class SourceBone
{
    public required string Name;
    public int Parent = -1, Layer, AppendParent = -1;
    public int TailBone=-1;
    public Vector3 TailOffset;
    public Vector3 RestPosition, FixedAxis, LocalAxisX, LocalAxisZ;
    public ushort Flags;
    public float AppendRatio;
    public bool AppendLocal => (Flags & 0x80) != 0;
}
public sealed record SourceIkLink(int Bone, bool Limited, Vector3 Minimum, Vector3 Maximum);
public sealed class SourceIkChain
{
    public int Controller, Effector, Iterations = 64;
    public float AngleLimit = .5f;
    public SourceIkLink[] Links = [];
}
public sealed class SourceRigDefinition
{
    public string Name = "", Fingerprint = "";
    public bool Approximate;
    public SourceBone[] Bones = [];
    public SourceIkChain[] IkChains = [];
    public int[] Order = [];
    public readonly Dictionary<string,int> Index = new(StringComparer.Ordinal);
    public readonly List<string> Warnings = [];
    public int Find(string name) => Index.GetValueOrDefault(VmdAnimation.NormalizeBoneName(name), -1);
    public void Validate()
    {
        if (Bones.Length is < 1 or > 8192) throw new InvalidDataException("源骨架骨骼数量超出范围。");
        Index.Clear();
        for (var i=0;i<Bones.Length;i++)
        {
            var b= Bones[i]; b.Name=VmdAnimation.NormalizeBoneName(b.Name);
            if (!Index.TryAdd(b.Name,i)) throw new InvalidDataException($"源骨架骨名重复：{b.Name}");
            if (b.Parent < -1 || b.Parent >= Bones.Length || b.AppendParent < -1 || b.AppendParent >= Bones.Length ||b.TailBone < -1||b.TailBone>=Bones.Length||!SkeletonTree.Finite(b.TailOffset)||
                !SkeletonTree.Finite(b.RestPosition) || !SkeletonTree.Finite(b.FixedAxis) || !SkeletonTree.Finite(b.LocalAxisX) || !SkeletonTree.Finite(b.LocalAxisZ) || !float.IsFinite(b.AppendRatio)) throw new InvalidDataException("源骨架索引或参考位置无效。");
        }
        var state=new byte[Bones.Length]; var order=new List<int>();
        void Visit(int i,int depth)
        {
            if(i<0 || state[i]==2)return;
            if(state[i]==1 || depth>256)throw new InvalidDataException("源骨架父链或追加变换存在环或过深依赖。");
            state[i]=1;Visit(Bones[i].Parent,depth+1);Visit(Bones[i].AppendParent,depth+1);state[i]=2;order.Add(i);
        }
        foreach(var i in Enumerable.Range(0,Bones.Length).OrderBy(i=>(Bones[i].Flags&0x1000)!=0).ThenBy(i=>Bones[i].Layer).ThenBy(i=>i)) Visit(i,0);
        Order=order.ToArray();
        foreach(var ik in IkChains)
        {
            if(ik.Controller<0||ik.Controller>=Bones.Length||ik.Effector<0||ik.Effector>=Bones.Length||ik.Iterations is <0 or >4096||
                !float.IsFinite(ik.AngleLimit)||ik.AngleLimit<0||ik.Links.Length is <1 or >256)throw new InvalidDataException("源 IK 定义无效。");
            foreach(var l in ik.Links)
            {
                if(l.Bone<0||l.Bone>=Bones.Length||!SkeletonTree.Finite(l.Minimum)||!SkeletonTree.Finite(l.Maximum)||
                    l.Minimum.X>l.Maximum.X||l.Minimum.Y>l.Maximum.Y||l.Minimum.Z>l.Maximum.Z)throw new InvalidDataException("源 IK 链或限位无效。");
                var parent=Bones[ik.Effector].Parent;var found=false;
                while(parent>=0){if(parent==l.Bone){found=true;break;}parent=Bones[parent].Parent;}
                if(!found)throw new InvalidDataException("源 IK 链骨不是末端的祖先。");
            }
        }
        if(IkChains.Sum(c=>(long)c.Iterations*c.Links.Length*Bones.Length)>8_000_000)throw new InvalidDataException("源 IK 约束计算量过大，拒绝影响游戏帧率的骨架。");
        IkChains=IkChains.OrderBy(c=>(Bones[c.Controller].Flags&0x1000)!=0).ThenBy(c=>Bones[c.Controller].Layer).ThenBy(c=>c.Controller).ToArray();
    }
    public static SourceRigDefinition Standard(bool tPose=false)
    {
        var bones=new List<SourceBone>();var chains=new List<SourceIkChain>();var names=new Dictionary<string,int>();
        int Add(string name,string? parent,Vector3 rest)
        {
            var i=bones.Count;names[name]=i;bones.Add(new SourceBone{Name=name,Parent=parent==null?-1:names[parent],RestPosition=rest});return i;
        }
        Add("全ての親",null,Vector3.Zero);Add("センター","全ての親",new(0,8,0));Add("グルーブ","センター",new(0,10,0));
        Add("腰","グルーブ",new(0,10,0));Add("下半身","腰",new(0,10,0));Add("上半身","腰",new(0,10,0));
        Add("上半身2","上半身",new(0,12,0));Add("上半身3","上半身2",new(0,13,0));Add("首","上半身3",new(0,14,0));Add("頭","首",new(0,15,0));
        foreach(var side in new[]{"左","右"})
        {
            var s=side=="左"?1f:-1f;
            Add(side+"足","下半身",new(s,9.6f,0));Add(side+"ひざ",side+"足",new(s,5.2f,-.1f));Add(side+"足首",side+"ひざ",new(s,.8f,0));
            Add(side+"つま先",side+"足首",new(s,.2f,-1.2f));
            Add(side+"足IK親","全ての親",new(s,0,0));var foot=Add(side+"足IK",side+"足IK親",new(s,.8f,0));
            var toe=Add(side+"つま先IK",side+"足IK",new(s,.2f,-1.2f));
            chains.Add(new SourceIkChain{Controller=foot,Effector=names[side+"足首"],Links=[new(names[side+"ひざ"],true,new(-MathF.PI,0,0),Vector3.Zero),new(names[side+"足"],false,Vector3.Zero,Vector3.Zero)]});
            chains.Add(new SourceIkChain{Controller=toe,Effector=names[side+"つま先"],Iterations=8,Links=[new(names[side+"足首"],false,Vector3.Zero,Vector3.Zero)]});
            // D deformation chains inherit solved FK/IK plus their own keys.
            foreach(var part in new[]{"足","ひざ","足首"})
            {
                var src=names[side+part];var id=Add(side+part+"D",part=="足"?"下半身":side+(part=="ひざ"?"足D":"ひざD"),bones[src].RestPosition);
                bones[id].AppendParent=src;bones[id].AppendRatio=1;bones[id].Flags=0x100;
            }
            var ex=Add(side+"足先EX",side+"足首D",bones[names[side+"つま先"]].RestPosition);
            bones[ex].AppendParent=names[side+"つま先"];bones[ex].AppendRatio=1;bones[ex].Flags=0x100;
            var shoulder=Add(side+"肩P","上半身3",new(s*.7f,13.5f,0));
            Add(side+"肩",side+"肩P",new(s*.8f,13.5f,0));Add(side+"腕",side+"肩",new(s*1.5f,13.5f,0));
            var elbow=new Vector3(s*4.3f,tPose?13.5f:11.5f,0);var wrist=new Vector3(s*6.8f,tPose?13.5f:9.7f,0);
            var twist=Add(side+"腕捩",side+"腕",Vector3.Lerp(bones[names[side+"腕"]].RestPosition,elbow,.5f));
            bones[twist].Flags=0x400;bones[twist].FixedAxis=Vector3.Normalize(elbow-bones[names[side+"腕"]].RestPosition);
            Add(side+"ひじ",side+"腕捩",elbow);
            var handTwist=Add(side+"手捩",side+"ひじ",Vector3.Lerp(elbow,wrist,.5f));bones[handTwist].Flags=0x400;bones[handTwist].FixedAxis=Vector3.Normalize(wrist-elbow);
            Add(side+"手首",side+"手捩",wrist);
            var axis=Vector3.Normalize(wrist-elbow);
            var width=Vector3.Normalize(new Vector3(-axis.Y*s,MathF.Abs(axis.X),0));
            foreach(var finger in new[]{"親指","人差指","中指","薬指","小指"})
            {
                var first=finger=="親指"?0:1;var parent=side+"手首";
                for(var j=first;j<= (finger=="親指"?2:3);j++)
                {
                    var name=side+finger+j;
                    var rest=finger=="親指"?wrist+axis*(j==0?.05f:j==1?.2f:.5f)+width*(.2f+.25f*j):
                        wrist+axis*(.35f+.35f*(j-first))+width*((2-Array.IndexOf(new[]{"親指","人差指","中指","薬指","小指"},finger))*.15f);
                    Add(name,parent,rest);parent=name;
                }
            }
        }
        var rig=new SourceRigDefinition{Name=tPose?"标准 MMD T 姿态":"标准 MMD A 姿态",Approximate=true,Fingerprint=tPose?"standard-t-v2":"standard-a-v2",Bones=bones.ToArray(),IkChains=chains.ToArray()};rig.Validate();return rig;
    }
}
