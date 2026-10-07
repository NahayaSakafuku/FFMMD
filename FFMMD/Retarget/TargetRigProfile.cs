using System.Numerics;
using FFMMD.Posing;

namespace FFMMD.Retarget;

/// <summary>Bind orientation with effective character dimensions; idle rotation is not calibration.</summary>
public sealed class TargetRigProfile
{
    public readonly SkeletonTree Skeleton;
    public readonly Vector3 Left,Up,Back;
    public TargetRigProfile(SkeletonTree binding,Vector3[]? effectivePositions=null,Vector3[]? effectiveScales=null)
    {
        Skeleton=new SkeletonTree{Names=(string[])binding.Names.Clone(),Parent=(short[])binding.Parent.Clone(),RefLocalRot=(Quaternion[])binding.RefLocalRot.Clone(),
            RefLocalPos=(Vector3[])(effectivePositions??binding.RefLocalPos).Clone(),RefLocalScale=(Vector3[])(effectiveScales??binding.RefLocalScale).Clone(),SkeletonPtr=binding.SkeletonPtr};
        Skeleton.RebuildReference();
        var l=Skeleton.Find("j_asi_a_l");var r=Skeleton.Find("j_asi_a_r");var pelvis=Skeleton.Find("j_kosi","n_hara");var head=Skeleton.Find("j_kao","j_kubi","j_sebo_a");
        Left=l>=0&&r>=0?RigMath.Direction(Skeleton.RefModelPos[l]-Skeleton.RefModelPos[r],Vector3.UnitX):Vector3.UnitX;
        var rawUp=head>=0&&pelvis>=0?Skeleton.RefModelPos[head]-Skeleton.RefModelPos[pelvis]:Vector3.UnitY;
        Up=RigMath.Direction(rawUp-Left*Vector3.Dot(rawUp,Left),Vector3.UnitY);
        var forward=Vector3.Zero;
        foreach(var side in new[]{"l","r"}){var a=Skeleton.Find("j_asi_d_"+side);var toe=Skeleton.Find("j_asi_e_"+side);if(a>=0&&toe>=0)forward+=Skeleton.RefModelPos[toe]-Skeleton.RefModelPos[a];}
        var back=Vector3.Normalize(Vector3.Cross(Left,Up));Back=Vector3.Dot(-forward,back)<0?-back:back;
    }
    public RigCoordinateMap CoordinateMap(SourceRigDefinition source,float yaw)
    {
        var l=source.Find("左足");var r=source.Find("右足");var p=source.Find("下半身");var h=source.Find("頭");
        var left=l>=0&&r>=0?RigMath.Direction(source.Bones[l].RestPosition-source.Bones[r].RestPosition,Vector3.UnitX):Vector3.UnitX;
        var rawUp=p>=0&&h>=0?source.Bones[h].RestPosition-source.Bones[p].RestPosition:Vector3.UnitY;
        var up=RigMath.Direction(rawUp-left*Vector3.Dot(rawUp,left),Vector3.UnitY);var back=Vector3.Normalize(Vector3.Cross(left,up));var forward=Vector3.Zero;
        foreach(var side in new[]{"左","右"}){var a=source.Find(side+"足首");var t=source.Find(side+"つま先");if(a>=0&&t>=0)forward+=source.Bones[t].RestPosition-source.Bones[a].RestPosition;}
        if(Vector3.Dot(-forward,back)<0)back=-back;
        var sourceMatrix=new RigCoordinateMap(left,up,back).Matrix;
        var targetMatrix=new RigCoordinateMap(Left,Up,Back).Matrix*Matrix4x4.CreateFromAxisAngle(Up,yaw*MathF.PI/180);
        var c=Matrix4x4.Transpose(sourceMatrix)*targetMatrix;
        return new RigCoordinateMap(new(c.M11,c.M12,c.M13),new(c.M21,c.M22,c.M23),new(c.M31,c.M32,c.M33));
    }
    public RigCoordinateMap GroundCoordinateMap(SourceRigDefinition source)
    {
        // Placement uses the explicit Y-up model axes. The head/pelvis line is
        // anatomy (and may be inclined), not the terrain's vertical direction.
        var l=source.Find("左足");var r=source.Find("右足");
        var sourceLeft=l>=0&&r>=0?source.Bones[l].RestPosition-source.Bones[r].RestPosition:Vector3.UnitX;
        sourceLeft.Y=0;sourceLeft=RigMath.Direction(sourceLeft,Vector3.UnitX);
        var targetLeft=Left;targetLeft.Y=0;targetLeft=RigMath.Direction(targetLeft,Vector3.UnitX);
        var sourceBack=Vector3.Cross(sourceLeft,Vector3.UnitY);var sourceForward=Vector3.Zero;
        foreach(var side in new[]{"左","右"})
        {
            var ankle=source.Find(side+"足首");var toe=source.Find(side+"つま先");
            if(ankle>=0&&toe>=0)sourceForward+=source.Bones[toe].RestPosition-source.Bones[ankle].RestPosition;
        }
        if(Vector3.Dot(-sourceForward,sourceBack)<0)sourceBack=-sourceBack;
        var targetBack=Vector3.Cross(targetLeft,Vector3.UnitY);
        if(Vector3.Dot(targetBack,Back)<0)targetBack=-targetBack;
        var s=new RigCoordinateMap(sourceLeft,Vector3.UnitY,sourceBack).Matrix;
        var t=new RigCoordinateMap(targetLeft,Vector3.UnitY,targetBack).Matrix;
        var map=Matrix4x4.Transpose(s)*t;
        return new(new(map.M11,map.M12,map.M13),new(map.M21,map.M22,map.M23),new(map.M31,map.M32,map.M33));
    }
}
