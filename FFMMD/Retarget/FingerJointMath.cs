using System.Numerics;

namespace FFMMD.Retarget;

/// <summary>Anatomical two-joint approximation for FF14's four non-thumb fingers.</summary>
public static class FingerJointMath
{
    public const float ProximalFlexionLimit=95*MathF.PI/180;
    public const float DistalFlexionLimit=110*MathF.PI/180;
    public const float SpreadLimit=20*MathF.PI/180;
    public static float SignedAxisAngle(Quaternion rotation,Vector3 axis)
    {
        // Equivalent q/-q and keyframe sign changes produce the same angle.
        rotation=Quaternion.Normalize(rotation);
        var angle=2*MathF.Atan2(Vector3.Dot(new(rotation.X,rotation.Y,rotation.Z),axis),rotation.W);
        return MathF.Atan2(MathF.Sin(angle),MathF.Cos(angle));
    }
    public static float EquivalentDistalCurl(float middle,float tip,float middleLength,float tipLength)
    {
        // Direction of the two source phalanges' combined chord. Unlike simply
        // multiplying their rotations, this does not assign both full bends to
        // one target joint. Keep winding relative to the middle joint.
        if(tipLength<=1e-8f)return middle;
        var x=middleLength+tipLength*MathF.Cos(tip);var y=tipLength*MathF.Sin(tip);
        var offset=x*x+y*y<1e-12f?tip*.5f:MathF.Atan2(y,x);
        return middle+offset;
    }
    public static float LimitCurl(float sourceAngle,float sourceClosingSign,bool proximal)
    {
        var extension=(proximal?15:10)*MathF.PI/180;
        return Math.Clamp(sourceAngle*sourceClosingSign,-extension,proximal?ProximalFlexionLimit:DistalFlexionLimit)*sourceClosingSign;
    }
}
