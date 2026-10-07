using System.Numerics;

namespace FFMMD.Retarget;

public static class RigMath
{
    public static Vector3 Direction(Vector3 v, Vector3 fallback) => v.LengthSquared() < 1e-12f ? fallback : Vector3.Normalize(v);
    public static Vector3 Orthogonal(Vector3 v) => Direction(Vector3.Cross(v, MathF.Abs(v.Y) < .9f ? Vector3.UnitY : Vector3.UnitX), Vector3.UnitZ);
    public static Quaternion FromTo(Vector3 from, Vector3 to)
    {
        if (from.LengthSquared() < 1e-12f || to.LengthSquared() < 1e-12f) return Quaternion.Identity;
        from = Vector3.Normalize(from); to = Vector3.Normalize(to);
        var d = Math.Clamp(Vector3.Dot(from, to), -1, 1);
        if (d > 1 - 1e-7f) return Quaternion.Identity;
        if (d < -1 + 1e-7f) return Quaternion.CreateFromAxisAngle(Orthogonal(from), MathF.PI);
        return Quaternion.Normalize(new Quaternion(Vector3.Cross(from, to), 1 + d));
    }
    public static Quaternion Power(Quaternion q, float amount)
    {
        q = Quaternion.Normalize(q);
        if (q.W < 0) q = new Quaternion(-q.X, -q.Y, -q.Z, -q.W);
        var v = new Vector3(q.X, q.Y, q.Z);
        return v.LengthSquared() < 1e-12f ? Quaternion.Identity : Quaternion.CreateFromAxisAngle(Vector3.Normalize(v), 2 * MathF.Atan2(v.Length(), q.W) * amount);
    }
    public static void TwoBone(Vector3 hip, Vector3 knee, Vector3 ankle, Vector3 goal, Vector3 bend, out Vector3 newKnee, out Vector3 newAnkle)
    {
        var a = Vector3.Distance(hip, knee); var b = Vector3.Distance(knee, ankle);
        if (a < 1e-6f || b < 1e-6f) { newKnee = knee; newAnkle = ankle; return; }
        var direction = Direction(goal - hip, Direction(ankle - hip, -Vector3.UnitY));
        var distance = Math.Clamp(Vector3.Distance(goal, hip), MathF.Abs(a - b) + MathF.Min(a, b) * 1e-6f, a + b);
        bend -= direction * Vector3.Dot(bend, direction);
        bend = Direction(bend, Orthogonal(direction));
        var along = (a * a + distance * distance - b * b) / (2 * distance);
        var height = MathF.Sqrt(MathF.Max(0, a * a - along * along));
        newKnee = hip + direction * along + bend * height;
        newAnkle = hip + direction * distance;
    }
    // XYZ Euler limits are evaluated in PMX link-local space. Local-axis metadata
    // describes editing axes, not a non-identity bind rotation.
    public static Vector3 Euler(Quaternion q)
    {
        q = Quaternion.Normalize(q);
        var y = MathF.Asin(Math.Clamp(2 * (q.W * q.Y - q.Z * q.X), -1, 1));
        if (MathF.Abs(MathF.Cos(y)) < 1e-5f)
            return new Vector3(2 * MathF.Atan2(q.X, q.W), y, 0);
        return new Vector3(MathF.Atan2(2 * (q.W * q.X + q.Y * q.Z), 1 - 2 * (q.X * q.X + q.Y * q.Y)), y,
            MathF.Atan2(2 * (q.W * q.Z + q.X * q.Y), 1 - 2 * (q.Y * q.Y + q.Z * q.Z)));
    }
    public static Quaternion FromEuler(Vector3 v) => Quaternion.Normalize(
        Quaternion.CreateFromAxisAngle(Vector3.UnitZ, v.Z) * Quaternion.CreateFromAxisAngle(Vector3.UnitY, v.Y) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, v.X));
}

/// <summary>One orthogonal basis transports vectors AND rotations, including reflections.</summary>
public readonly struct RigCoordinateMap
{
    public readonly Matrix4x4 Matrix;
    public readonly float Parity;
    public RigCoordinateMap(Vector3 left, Vector3 up, Vector3 back)
    {
        Matrix = new Matrix4x4(left.X,left.Y,left.Z,0, up.X,up.Y,up.Z,0, back.X,back.Y,back.Z,0, 0,0,0,1);
        Parity = MathF.Sign(Matrix.GetDeterminant());
        if (MathF.Abs(Matrix.GetDeterminant()) < .99f || MathF.Abs(Vector3.Dot(left, up)) > 1e-4f ||
            MathF.Abs(Vector3.Dot(left, back)) > 1e-4f || MathF.Abs(Vector3.Dot(up, back)) > 1e-4f ||
            MathF.Abs(left.LengthSquared()-1)>1e-4f || MathF.Abs(up.LengthSquared()-1)>1e-4f || MathF.Abs(back.LengthSquared()-1)>1e-4f)
            throw new InvalidDataException("坐标基不是正交单位基。");
    }
    public Vector3 Vector(Vector3 value) => Vector3.TransformNormal(value, Matrix);
    public RigCoordinateMap Yaw(Vector3 up,float degrees)
    {
        var q=Quaternion.CreateFromAxisAngle(up,degrees*MathF.PI/180);
        return new RigCoordinateMap(Vector3.Transform(new Vector3(Matrix.M11,Matrix.M12,Matrix.M13),q),
            Vector3.Transform(new Vector3(Matrix.M21,Matrix.M22,Matrix.M23),q),Vector3.Transform(new Vector3(Matrix.M31,Matrix.M32,Matrix.M33),q));
    }
    public Quaternion Rotation(Quaternion value)
    {
        var xyz = Vector(new Vector3(value.X, value.Y, value.Z)) * Parity;
        return Quaternion.Normalize(new Quaternion(xyz, value.W));
    }
}
