using System.Numerics;

namespace FFMMD.Skirt;

/// <summary>
/// Continuous reference-rig collision proxy for smoothing. These signed distances
/// do not measure a clothing mesh or Bullet's exact contact-manifold depth.
/// All positions, radii and half extents must use the same coordinate system and units.
/// </summary>
public static class SkirtProxyCollisionGuard
{
    /// <summary>
    /// Minimum signed point-to-box distance along the capsule axis, minus its radius.
    /// Positive values mean clearance; negative values indicate proxy overlap.
    /// Sphere queries use equal axis endpoints. Box extents are half sizes.
    /// </summary>
    public static double SignedCapsuleBoxDistance(Vector3 endpointA, Vector3 endpointB,
        float radius, Vector3 boxCenter, Quaternion boxRotation, Vector3 halfExtents)
    {
        Validate(endpointA, nameof(endpointA)); Validate(endpointB, nameof(endpointB));
        Validate(boxCenter, nameof(boxCenter)); ValidateHalf(halfExtents);
        if (!float.IsFinite(radius) || radius < 0) throw new ArgumentOutOfRangeException(nameof(radius));
        var qLength = Math.Sqrt((double)boxRotation.X * boxRotation.X +
            (double)boxRotation.Y * boxRotation.Y + (double)boxRotation.Z * boxRotation.Z +
            (double)boxRotation.W * boxRotation.W);
        if (!double.IsFinite(qLength) || qLength < 1e-12)
            throw new ArgumentException("Box rotation must be a finite nonzero quaternion.", nameof(boxRotation));
        var a = InverseRotate(new Point(endpointA) - new Point(boxCenter), boxRotation, qLength);
        var b = InverseRotate(new Point(endpointB) - new Point(boxCenter), boxRotation, qLength);
        return SegmentAabbDistance(a, b, new Point(halfExtents)) - radius;
    }

    public static double SignedSphereBoxDistance(Vector3 sphereCenter, float radius,
        Vector3 boxCenter, Quaternion boxRotation, Vector3 halfExtents) =>
        SignedCapsuleBoxDistance(sphereCenter, sphereCenter, radius, boxCenter, boxRotation, halfExtents);

    public static double CapsuleBoxPenetration(Vector3 endpointA, Vector3 endpointB,
        float radius, Vector3 boxCenter, Quaternion boxRotation, Vector3 halfExtents) =>
        Math.Max(0, -SignedCapsuleBoxDistance(endpointA, endpointB, radius, boxCenter, boxRotation, halfExtents));

    public static double SphereBoxPenetration(Vector3 sphereCenter, float radius,
        Vector3 boxCenter, Quaternion boxRotation, Vector3 halfExtents) =>
        CapsuleBoxPenetration(sphereCenter, sphereCenter, radius, boxCenter, boxRotation, halfExtents);

    /// <summary>Reject a candidate if any pair's proxy penetration gets worse.</summary>
    public static bool DoesNotWorsen(ReadOnlySpan<double> originalPenetrations,
        ReadOnlySpan<double> candidatePenetrations, double tolerance = 1e-7)
    {
        if (!double.IsFinite(tolerance) || tolerance < 0)
            throw new ArgumentOutOfRangeException(nameof(tolerance));
        if (originalPenetrations.Length != candidatePenetrations.Length) return false;
        for (var i = 0; i < originalPenetrations.Length; i++)
        {
            var before = originalPenetrations[i]; var after = candidatePenetrations[i];
            if (!double.IsFinite(before) || before < 0 || !double.IsFinite(after) || after < 0 ||
                after > before + tolerance) return false;
        }
        return true;
    }

    /// <summary>Minimum signed point-to-axis-aligned-box distance along a closed segment.</summary>
    public static double SignedSegmentAabbDistance(Vector3 endpointA, Vector3 endpointB, Vector3 halfExtents)
    {
        Validate(endpointA, nameof(endpointA)); Validate(endpointB, nameof(endpointB)); ValidateHalf(halfExtents);
        return SegmentAabbDistance(new Point(endpointA), new Point(endpointB), new Point(halfExtents));
    }

    private static double SegmentAabbDistance(Point a, Point b, Point half)
    {
        var direction = b - a;
        Span<double> breaks = stackalloc double[8];
        breaks[0] = 0; breaks[1] = 1; var count = 2;
        for (var axis = 0; axis < 3; axis++)
        {
            if (Math.Abs(direction[axis]) <= 1e-14) continue;
            for (var sign = -1; sign <= 1; sign += 2)
            {
                var t = (sign * half[axis] - a[axis]) / direction[axis];
                if (t > 0 && t < 1) breaks[count++] = t;
            }
        }
        breaks = breaks[..count]; breaks.Sort();
        var bestSquared = double.PositiveInfinity; var inside = false;
        for (var interval = 1; interval < count; interval++)
        {
            var low = breaks[interval - 1]; var high = breaks[interval];
            if (high <= low) continue;
            var midpoint = a + direction * ((low + high) * .5);
            double denominator = 0, numerator = 0; var activeCount = 0;
            for (var axis = 0; axis < 3; axis++)
            {
                if (Math.Abs(midpoint[axis]) <= half[axis]) continue;
                activeCount++;
                var face = midpoint[axis] > half[axis] ? half[axis] : -half[axis];
                denominator += direction[axis] * direction[axis];
                numerator += direction[axis] * (a[axis] - face);
            }
            if (activeCount == 0) { inside = true; continue; }
            var t = denominator < 1e-28 ? low : Math.Clamp(-numerator / denominator, low, high);
            bestSquared = Math.Min(bestSquared, OutsideDistanceSquared(a + direction * t, half));
        }
        if (inside || IsInside(a, half) || IsInside(b, half))
        {
            // Inside the box, depth is the minimum of six affine face gaps.
            // This concave piecewise-linear function reaches its maximum at an
            // endpoint or where two affine gaps meet; solve those points exactly.
            Span<double> intercepts = stackalloc double[6];
            Span<double> slopes = stackalloc double[6];
            for (var axis = 0; axis < 3; axis++)
            {
                intercepts[axis] = half[axis] - a[axis]; slopes[axis] = -direction[axis];
                intercepts[axis + 3] = half[axis] + a[axis]; slopes[axis + 3] = direction[axis];
            }
            var deepest = Math.Max(MinimumGap(intercepts, slopes, 0), MinimumGap(intercepts, slopes, 1));
            for (var i = 0; i < 6; i++)
            for (var j = i + 1; j < 6; j++)
            {
                var slopeDifference = slopes[i] - slopes[j];
                if (Math.Abs(slopeDifference) <= 1e-14) continue;
                var t = (intercepts[j] - intercepts[i]) / slopeDifference;
                if (t >= 0 && t <= 1) deepest = Math.Max(deepest, MinimumGap(intercepts, slopes, t));
            }
            if (deepest >= 0) return -deepest;
        }
        bestSquared = Math.Min(bestSquared, Math.Min(OutsideDistanceSquared(a, half), OutsideDistanceSquared(b, half)));
        return Math.Sqrt(bestSquared);
    }

    private static double MinimumGap(ReadOnlySpan<double> intercepts, ReadOnlySpan<double> slopes, double t)
    {
        var minimum = double.PositiveInfinity;
        for (var i = 0; i < intercepts.Length; i++) minimum = Math.Min(minimum, intercepts[i] + slopes[i] * t);
        return minimum;
    }

    private static double OutsideDistanceSquared(Point point, Point half)
    {
        double distance = 0;
        for (var axis = 0; axis < 3; axis++)
        {
            var excess = Math.Max(Math.Abs(point[axis]) - half[axis], 0);
            distance += excess * excess;
        }
        return distance;
    }

    private static bool IsInside(Point point, Point half) =>
        Math.Abs(point.X) <= half.X && Math.Abs(point.Y) <= half.Y && Math.Abs(point.Z) <= half.Z;

    private static Point InverseRotate(Point point, Quaternion q, double qLength)
    {
        var vector = new Point(-q.X / qLength, -q.Y / qLength, -q.Z / qLength);
        var uv = Point.Cross(vector, point);
        return point + uv * (2 * q.W / qLength) + Point.Cross(vector, uv) * 2;
    }

    private static void Validate(Vector3 value, string name)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z))
            throw new ArgumentException("Geometry must contain finite coordinates.", name);
    }

    private static void ValidateHalf(Vector3 value)
    {
        Validate(value, nameof(value));
        if (value.X < 0 || value.Y < 0 || value.Z < 0)
            throw new ArgumentOutOfRangeException(nameof(value), "Half extents cannot be negative.");
    }

    private readonly record struct Point(double X, double Y, double Z)
    {
        public Point(Vector3 value) : this(value.X, value.Y, value.Z) { }
        public double this[int axis] => axis switch { 0 => X, 1 => Y, _ => Z };
        public static Point operator +(Point a, Point b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Point operator -(Point a, Point b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Point operator *(Point a, double scale) => new(a.X * scale, a.Y * scale, a.Z * scale);
        public static Point Cross(Point a, Point b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    }
}
