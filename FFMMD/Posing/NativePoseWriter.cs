using FFMMD.Retarget;
using FFXIVClientStructs.Havok.Common.Base.Math.QsTransform;
using FFXIVClientStructs.Havok.Common.Base.Math.Quaternion;

namespace FFMMD.Posing;

/// <summary>Shared body/partial writer. No scale component is exposed or assigned.</summary>
public static unsafe class NativePoseWriter
{
    public static void Apply(hkQsTransformf* bone,in RotationTranslationWrite write)
    {
        var translation=new Vector3(bone->Translation.X,bone->Translation.Y,bone->Translation.Z);
        var rotation=new Quaternion(bone->Rotation.X,bone->Rotation.Y,bone->Rotation.Z,bone->Rotation.W);
        write.Apply(ref translation,ref rotation);
        if(write.WriteTranslation)
        {
            var value=bone->Translation;value.X=translation.X;value.Y=translation.Y;value.Z=translation.Z;bone->Translation=value;
        }
        if(write.WriteRotation)bone->Rotation=new hkQuaternionf{X=rotation.X,Y=rotation.Y,Z=rotation.Z,W=rotation.W};
    }
}
