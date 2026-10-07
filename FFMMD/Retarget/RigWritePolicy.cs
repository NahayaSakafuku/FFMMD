using System.Numerics;

namespace FFMMD.Retarget;

/// <summary>The animation writer can carry only rotation and explicitly allowed translation.</summary>
public readonly record struct RotationTranslationWrite(Quaternion Rotation,Vector3 Translation,bool WriteRotation,bool WriteTranslation)
{
    public void Apply(ref Vector3 translation,ref Quaternion rotation)
    {
        if(WriteTranslation)translation=Translation;
        if(WriteRotation)rotation=Rotation;
    }
}
public sealed class ScaleChangeAudit
{
    public float MaximumLocalScaleDelta;
    public string[] ChangedBones=[];
}
public static class RigWritePolicy
{
    public static ScaleChangeAudit CompareScales(string[] names,PoseSnapshot before,PoseSnapshot after,
        PartialPoseSnapshot[]? beforePartials=null,PartialPoseSnapshot[]? afterPartials=null)
    {
        var maximum=0f;var changed=new List<string>();
        void Compare(string prefix,string[] boneNames,PoseSnapshot a,PoseSnapshot b)
        {
            if(a.LocalScales.Length!=b.LocalScales.Length||a.LocalScales.Length!=boneNames.Length)
                throw new InvalidDataException("缩放审计的姿态长度不一致。");
            for(var i=0;i<boneNames.Length;i++)
            {
                var delta=Vector3.Distance(a.LocalScales[i],b.LocalScales[i]);maximum=MathF.Max(maximum,delta);
                if(delta>1e-5f)changed.Add(prefix+boneNames[i]);
            }
        }
        Compare("body/",names,before,after);
        if(beforePartials!=null&&afterPartials!=null)
            foreach(var first in beforePartials)
            {
                var next=afterPartials.FirstOrDefault(p=>p.PartialIndex==first.PartialIndex);
                if(first.Pose!=null&&next?.Pose!=null)Compare($"partial{first.PartialIndex}/",first.Names,first.Pose,next.Pose);
            }
        return new(){MaximumLocalScaleDelta=maximum,ChangedBones=changed.ToArray()};
    }
}
