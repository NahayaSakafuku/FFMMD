using System.Numerics;
using FFMMD.Skirt;

internal static class SkirtProxyCollisionGuardRegression
{
    public static int Run()
    {
        var failures = 0; var count = 0;
        void Check(string name, Action body)
        {
            count++;
            try { body(); Console.WriteLine($"  ✔ {name}"); }
            catch (Exception e) { failures++; Console.WriteLine($"  ✘ {name}: {e.Message}"); }
        }
        static void Near(double actual, double expected, double tolerance = 1e-6)
        {
            if (Math.Abs(actual - expected) > tolerance)
                throw new InvalidDataException($"Expected {expected}, got {actual}.");
        }
        static void Require(bool value) { if (!value) throw new InvalidDataException("Assertion failed."); }
        var half = Vector3.One;
        Check("碰撞代理连续平行轴距离与端点最小值", () =>
        {
            Near(SkirtProxyCollisionGuard.SignedSegmentAabbDistance(new(-2, 2, 0), new(2, 2, 0), half), 1);
            Near(SkirtProxyCollisionGuard.SignedSegmentAabbDistance(new(2, 2, 0), new(3, 3, 0), half), Math.Sqrt(2));
        });
        Check("碰撞代理连续线段最小值不依赖采样", () =>
        {
            // The diagonal reaches the box's top-right corner at t=1/3.
            Near(SkirtProxyCollisionGuard.SignedSegmentAabbDistance(new(2, 0, 0), new(-1, 3, 0), half), 0);
            // A segment parallel to the diagonal stops short; closest t=1/2 is inside the interval.
            Near(SkirtProxyCollisionGuard.SignedSegmentAabbDistance(new(3, 0, 0), new(0, 3, 0), half), Math.Sqrt(.5));
        });
        Check("碰撞代理内部距离保留深度和零长线段", () =>
        {
            Near(SkirtProxyCollisionGuard.SignedSegmentAabbDistance(new(-2, 0, 0), new(2, 0, 0), half), -1);
            Near(SkirtProxyCollisionGuard.SignedSegmentAabbDistance(new(0, .5f, 0), new(0, .5f, 0), half), -.5);
            Near(SkirtProxyCollisionGuard.SignedSegmentAabbDistance(new(2, 0, 0), new(2, 0, 0), half), 1);
            Near(SkirtProxyCollisionGuard.SignedSegmentAabbDistance(new(-2, .7f, 0), new(2, .7f, 0), half), -.3);
        });
        Check("碰撞代理内部深穿透不会被无符号距离掩盖", () =>
        {
            Near(SkirtProxyCollisionGuard.SphereBoxPenetration(new(.8f, 0, 0), .1f, Vector3.Zero, Quaternion.Identity, half), .3);
            Near(SkirtProxyCollisionGuard.SphereBoxPenetration(Vector3.Zero, .1f, Vector3.Zero, Quaternion.Identity, half), 1.1);
        });
        Check("碰撞代理球体等同零长胶囊且远距离保持有符号间隙", () =>
        {
            Near(SkirtProxyCollisionGuard.SignedSphereBoxDistance(new(3, 0, 0), .2f, Vector3.Zero, Quaternion.Identity, half), 1.8);
            Near(SkirtProxyCollisionGuard.CapsuleBoxPenetration(new(3, 0, 0), new(3, 2, 0), .2f, Vector3.Zero, Quaternion.Identity, half), 0);
        });
        Check("碰撞代理旋转平移与四元数符号不改变几何", () =>
        {
            var q = Quaternion.CreateFromAxisAngle(Vector3.Normalize(new Vector3(1, 2, 3)), 1.2f);
            var center = new Vector3(3, 4, 5); var extents = new Vector3(1, .2f, .3f);
            var a = new Vector3(0, .5f, 0); var b = new Vector3(1, .5f, 0);
            var expected = SkirtProxyCollisionGuard.SignedCapsuleBoxDistance(a, b, .4f, Vector3.Zero, Quaternion.Identity, extents);
            var actual = SkirtProxyCollisionGuard.SignedCapsuleBoxDistance(Vector3.Transform(a, q) + center, Vector3.Transform(b, q) + center, .4f, center, q, extents);
            Near(actual, expected);
            var opposite = new Quaternion(-q.X, -q.Y, -q.Z, -q.W);
            Near(SkirtProxyCollisionGuard.SignedCapsuleBoxDistance(Vector3.Transform(a, q) + center, Vector3.Transform(b, q) + center, .4f, center, opposite, extents), expected);
        });
        Check("碰撞代理逐对拒绝恶化，即使总穿透降低", () =>
        {
            Require(!SkirtProxyCollisionGuard.DoesNotWorsen([.5, .1], [.1, .2], 0));
            Require(SkirtProxyCollisionGuard.DoesNotWorsen([.5, .1], [.4, .1], 0));
            Require(SkirtProxyCollisionGuard.DoesNotWorsen([.5, .1], [.5 + 5e-8, .1]));
            Require(!SkirtProxyCollisionGuard.DoesNotWorsen([.5, .1], [.5 + 2e-7, .1]));
            Require(!SkirtProxyCollisionGuard.DoesNotWorsen([.5], [double.NaN]));
            Require(!SkirtProxyCollisionGuard.DoesNotWorsen([.5], [.5, .1]));
        });
        Check("碰撞代理线段反向及统一缩放不改变距离关系", () =>
        {
            var random = new Random(3292);
            for (var i = 0; i < 500; i++)
            {
                Vector3 Point() => new((float)(random.NextDouble() * 6 - 3), (float)(random.NextDouble() * 6 - 3), (float)(random.NextDouble() * 6 - 3));
                var a = Point(); var b = Point(); var extents = new Vector3(.6f, .4f, .2f);
                var distance = SkirtProxyCollisionGuard.SignedSegmentAabbDistance(a, b, extents);
                Near(SkirtProxyCollisionGuard.SignedSegmentAabbDistance(b, a, extents), distance, 1e-10);
                Near(SkirtProxyCollisionGuard.SignedSegmentAabbDistance(a * 2, b * 2, extents * 2), distance * 2, 1e-10);
            }
        });
        Check("碰撞代理内部极值与端点外部距离均满足连续采样上界", () =>
        {
            var random = new Random(392);
            for (var i = 0; i < 120; i++)
            {
                Vector3 Point() => new((float)(random.NextDouble() * 4 - 2), (float)(random.NextDouble() * 4 - 2), (float)(random.NextDouble() * 4 - 2));
                var a = Point(); var b = Point(); var extents = new Vector3(.9f, .7f, .5f);
                var exact = SkirtProxyCollisionGuard.SignedSegmentAabbDistance(a, b, extents);
                for (var j = 0; j <= 200; j++)
                {
                    var p = Vector3.Lerp(a, b, j / 200f);
                    var delta = Vector3.Abs(p) - extents;
                    var sampled = delta.X <= 0 && delta.Y <= 0 && delta.Z <= 0
                        ? Math.Max(delta.X, Math.Max(delta.Y, delta.Z))
                        : Vector3.Max(delta, Vector3.Zero).Length();
                    Require(exact <= sampled + 1e-6);
                }
            }
        });
        Check("碰撞代理拒绝无效半尺寸、半径与旋转", () =>
        {
            static void Reject(Action action)
            {
                try { action(); } catch (ArgumentException) { return; }
                throw new InvalidDataException("Expected invalid geometry to be rejected.");
            }
            Reject(() => SkirtProxyCollisionGuard.SignedSphereBoxDistance(Vector3.Zero, -1, Vector3.Zero, Quaternion.Identity, half));
            Reject(() => SkirtProxyCollisionGuard.SignedSphereBoxDistance(Vector3.Zero, 1, Vector3.Zero, default, half));
            Reject(() => SkirtProxyCollisionGuard.SignedSegmentAabbDistance(Vector3.Zero, Vector3.Zero, -half));
            Reject(() => SkirtProxyCollisionGuard.SignedSegmentAabbDistance(new(float.NaN, 0, 0), Vector3.Zero, half));
        });
        Console.WriteLine($"裙骨碰撞代理: {count - failures}/{count}");
        return failures;
    }
}
