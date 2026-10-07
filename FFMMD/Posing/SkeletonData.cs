using System.Numerics;

namespace FFMMD.Posing;

/// <summary> Pure managed skeleton data shared by playback and offline regression tests. </summary>
public sealed partial class SkeletonTree
{
    public nint SkeletonPtr;
    public int BoneCount;
    public short[] Parent = [];
    public string[] Names = [];
    public Quaternion[] RefLocalRot = [], RefModelRot = [];
    public Vector3[] RefLocalPos = [], RefModelPos = [];
    public Vector3[] RefLocalScale = [], RefModelScale = [];
    public Dictionary<string, int> Index = new(StringComparer.Ordinal);

    public void RebuildReference()
    {
        BoneCount = Names.Length;
        if (BoneCount == 0 || Parent.Length != BoneCount || RefLocalRot.Length != BoneCount ||
            RefLocalPos.Length != BoneCount || RefLocalScale.Length != BoneCount)
            throw new InvalidDataException("骨架数组长度不一致。");
        RefModelRot = new Quaternion[BoneCount];
        RefModelPos = new Vector3[BoneCount];
        RefModelScale = new Vector3[BoneCount];
        Index.Clear();
        for (var i = 0; i < BoneCount; i++)
        {
            var p = Parent[i];
            if (p < -1 || p >= i) throw new InvalidDataException("骨架父索引不符合拓扑顺序。");
            if (!Finite(RefLocalPos[i]) || !Finite(RefLocalScale[i]) ||
                RefLocalScale[i].X <= 1e-6f || RefLocalScale[i].Y <= 1e-6f || RefLocalScale[i].Z <= 1e-6f ||
                !Finite(RefLocalRot[i]) || RefLocalRot[i].LengthSquared() < 1e-12f)
                throw new InvalidDataException("骨架参考变换无效。");
            RefLocalRot[i] = Quaternion.Normalize(RefLocalRot[i]);
            Index[Names[i]] = i;
            if (p < 0)
            {
                RefModelRot[i] = RefLocalRot[i];
                RefModelPos[i] = RefLocalPos[i];
                RefModelScale[i] = RefLocalScale[i];
            }
            else
            {
                RefModelRot[i] = Quaternion.Normalize(RefModelRot[p] * RefLocalRot[i]);
                RefModelPos[i] = RefModelPos[p] + Vector3.Transform(RefLocalPos[i] * RefModelScale[p], RefModelRot[p]);
                RefModelScale[i] = RefModelScale[p] * RefLocalScale[i];
            }
        }
    }

    public int Find(params string[] names)
    {
        foreach (var name in names) if (Index.TryGetValue(name, out var i)) return i;
        return -1;
    }

    internal static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    internal static bool Finite(Quaternion q) => float.IsFinite(q.X) && float.IsFinite(q.Y) && float.IsFinite(q.Z) && float.IsFinite(q.W);
}
