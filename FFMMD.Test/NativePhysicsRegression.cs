using System.Numerics;
using System.Runtime.InteropServices;
using FFMMD.Skirt;

/// <summary>Exercises the production read-only helper against real process memory, without a game.</summary>
internal static unsafe class NativePhysicsRegression
{
    public static int Run()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("  跳过原生内存读取回归：需要 Windows");
            return 0;
        }

        var failed = 0;
        var count = 0;
        void Test(string name, Action body)
        {
            count++;
            try { body(); Console.WriteLine($"  ✔ {name}"); }
            catch (Exception e) { failed++; Console.WriteLine($"  ✘ {name}: {e.Message}"); }
        }

        Test("原生只读 helper 精确读取当前进程结构和字节", () =>
        {
            var source = new RawTransform
            {
                Translation = new(1, 2, 3, 4),
                Rotation = new(0, 0.5f, 0, 0.8660254f),
                Scale = new(0.75f, 1.25f, 1.5f, 1),
            };
            var address = Marshal.AllocHGlobal(sizeof(RawTransform));
            try
            {
                Marshal.StructureToPtr(source, address, false);
                Assert(NativePhysicsRead.TryRead<RawTransform>(address, out var copy), "结构读取失败");
                Assert(copy.Translation == source.Translation && copy.Rotation == source.Rotation && copy.Scale == source.Scale,
                    "读取值与分配的源结构不一致");
                var bytes = new byte[sizeof(RawTransform)];
                Assert(NativePhysicsRead.TryReadBytes(address, bytes), "字节读取失败");
                Assert(bytes.SequenceEqual(ReadKnownBytes(address, bytes.Length)), "字节副本与源不一致");
            }
            finally { Marshal.FreeHGlobal(address); }
        });

        Test("重复原始姿态读取保留 local/model 缓存、dirty flags 和非单位 scale", () =>
        {
            var size = sizeof(RawPose) + 2 * sizeof(RawTransform) + sizeof(uint);
            var address = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.Copy(Enumerable.Range(0, size).Select(i => (byte)(i * 17)).ToArray(), 0, address, size);
                var local = address + sizeof(RawPose);
                var model = local + sizeof(RawTransform);
                var flags = model + sizeof(RawTransform);
                var localValue = new RawTransform { Translation = new(2, 3, 4, 0), Rotation = new(0, 0, 0, 1), Scale = new(0.6f, 1.4f, 2, 1) };
                var modelValue = new RawTransform { Translation = new(-1, 4, 8, 0), Rotation = new(0, 1, 0, 0), Scale = new(1.2f, 0.8f, 3, 1) };
                Marshal.StructureToPtr(localValue, local, false);
                Marshal.StructureToPtr(modelValue, model, false);
                Marshal.WriteInt32(flags, 3);
                var pose = new RawPose
                {
                    Skeleton = address,
                    Local = new() { Data = local, Length = 1, CapacityAndFlags = unchecked((int)0x80000001) },
                    Model = new() { Data = model, Length = 1, CapacityAndFlags = unchecked((int)0x80000001) },
                    Flags = new() { Data = flags, Length = 1, CapacityAndFlags = unchecked((int)0x80000001) },
                    ModelInSync = 0,
                    LocalInSync = 0,
                };
                Marshal.StructureToPtr(pose, address, false);
                var before = ReadKnownBytes(address, size);
                for (var i = 0; i < 32; i++)
                {
                    Assert(NativePhysicsRead.TryRead<RawPose>(address, out var observed), "pose header 读取失败");
                    Assert(observed.LocalInSync == 0 && observed.ModelInSync == 0, "dirty 缓存被同步");
                    Assert(NativePhysicsRead.TryRead<RawTransform>(observed.Local.Data, out var rawLocal), "local 读取失败");
                    Assert(NativePhysicsRead.TryRead<RawTransform>(observed.Model.Data, out var rawModel), "model 读取失败");
                    Assert(NativePhysicsRead.TryRead<uint>(observed.Flags.Data, out var rawFlags) && rawFlags == 3,
                        "bone dirty flags 被改变");
                    Assert(rawLocal.Scale == localValue.Scale && rawModel.Scale == modelValue.Scale, "非单位 scale 被改变");
                    Assert(rawLocal.Translation != rawModel.Translation, "独立缓存被重算或合并");
                }
                Assert(before.SequenceEqual(ReadKnownBytes(address, size)), "只读采样修改了原生姿态内存");
            }
            finally { Marshal.FreeHGlobal(address); }
        });

        Test("原生读取拒绝空地址和未映射地址", () =>
        {
            Assert(!NativePhysicsRead.TryRead<ulong>(0, out _), "接受空地址");
            Assert(!NativePhysicsRead.TryRead<ulong>((nint)1, out _), "接受未映射地址");
            Assert(!NativePhysicsRead.TryReadBytes((nint)1, new byte[16]), "接受未映射字节范围");
        });

        Test("原生读取遇到不可读页面和跨页范围时安全失败", () =>
        {
            var pageSize = Environment.SystemPageSize;
            var address = VirtualAlloc(0, (nuint)(2 * pageSize), 0x3000, 0x04);
            Assert(address != 0, "测试页面分配失败");
            try
            {
                Marshal.WriteInt64(address + pageSize - 8, 0x0102030405060708);
                Assert(VirtualProtect(address + pageSize, (nuint)pageSize, 0x01, out _), "不可读测试页面保护失败");
                Assert(NativePhysicsRead.TryRead<ulong>(address + pageSize - 8, out var readable) && readable == 0x0102030405060708,
                    "邻近不可读页面的有效结构无法读取");
                Assert(!NativePhysicsRead.TryRead<ulong>(address + pageSize, out _), "不可读页面被当成有效结构");
                Assert(!NativePhysicsRead.TryReadBytes(address + pageSize - 8, new byte[16]), "跨不可读页的部分读取被当成完整读取");
            }
            finally { Assert(VirtualFree(address, 0, 0x8000), "测试页面释放失败"); }
        });

        Test("内存范围拒绝负长度、地址溢出和非用户地址", () =>
        {
            Assert(!NativePhysicsRead.IsRangeValid((nint)0x10000, -1), "接受负长度");
            Assert(!NativePhysicsRead.IsRangeValid((nint)0x10000, 0), "接受空结构范围");
            Assert(!NativePhysicsRead.IsRangeValid(nint.MaxValue - 3, 16), "接受地址加法溢出");
            Assert(!NativePhysicsRead.IsRangeValid((nint)(-1), 16), "接受非用户地址");
            Assert(NativePhysicsRead.TryReadBytes((nint)1, Span<byte>.Empty), "空 byte span 应无需访问地址");
        });

        Test("native vector 接受合法空向量和有界指针数组", () =>
        {
            Assert(NativePhysicsRead.TryGetVectorCount(0, 0, 0, 8, 16, out var empty) && empty == 0, "全空 vector 被拒绝");
            var address = Marshal.AllocHGlobal(4 * sizeof(nint));
            try
            {
                Assert(NativePhysicsRead.TryGetVectorCount(address, address + 3 * sizeof(nint), address + 4 * sizeof(nint), sizeof(nint), 16, out var countValue)
                    && countValue == 3, "正常 vector 计数错误");
                Assert(NativePhysicsRead.TryGetVectorCount(address, address, address + 4 * sizeof(nint), sizeof(nint), 16, out empty)
                    && empty == 0, "有容量的空 vector 被拒绝");
            }
            finally { Marshal.FreeHGlobal(address); }
        });

        Test("native vector 拒绝混合空指针、倒序和非整元素范围", () =>
        {
            const int size = 8;
            var first = (nint)0x10000;
            Assert(!NativePhysicsRead.TryGetVectorCount(0, first, first, size, 16, out _), "接受不一致的空指针");
            Assert(!NativePhysicsRead.TryGetVectorCount(first, first - size, first + size, size, 16, out _), "接受倒序 last");
            Assert(!NativePhysicsRead.TryGetVectorCount(first, first + 2 * size, first + size, size, 16, out _), "接受 end 小于 last");
            Assert(!NativePhysicsRead.TryGetVectorCount(first, first + size + 1, first + 2 * size, size, 16, out _), "接受半个元素");
            Assert(!NativePhysicsRead.TryGetVectorCount(first, first + size, first + 2 * size + 1, size, 16, out _), "接受非整元素容量");
        });

        Test("native vector 拒绝超大长度、容量和无效元素尺寸", () =>
        {
            var first = (nint)0x10000;
            Assert(!NativePhysicsRead.TryGetVectorCount(first, first + 17 * 8, first + 17 * 8, 8, 16, out _), "接受超过计数上限的 vector");
            Assert(!NativePhysicsRead.TryGetVectorCount(first, first, first + 17 * 8, 8, 16, out _), "接受超大空 vector 容量");
            Assert(!NativePhysicsRead.TryGetVectorCount(first, first + 8, first + 8, -1, 16, out _), "接受负元素尺寸");
            Assert(!NativePhysicsRead.TryGetVectorCount(first, first + 8, first + 8, 0, 16, out _), "接受零元素尺寸");
            Assert(!NativePhysicsRead.TryGetVectorCount(first, first + 8, first + 8, 8, -1, out _), "接受负计数上限");
            Assert(!NativePhysicsRead.TryGetVectorCount(first, nint.MaxValue, nint.MaxValue, 8, 16, out _), "接受极大指针范围");
        });

        Test("资源元数据每次读取反映当前 ID、data pointer 和长度", () =>
        {
            var address = Marshal.AllocHGlobal(sizeof(RawResource));
            try
            {
                Marshal.StructureToPtr(new RawResource { Id = 7, Data = (nint)0x10000, Length = 120 }, address, false);
                Assert(NativePhysicsRead.TryRead<RawResource>(address, out var before), "首次资源读取失败");
                Marshal.StructureToPtr(new RawResource { Id = 8, Data = (nint)0x20000, Length = 240 }, address, false);
                Assert(NativePhysicsRead.TryRead<RawResource>(address, out var after), "变化后资源读取失败");
                Assert(before.Id == 7 && after.Id == 8 && before.Data != after.Data && before.Length != after.Length,
                    "资源变化后读取了旧元数据");
                Assert(NativePhysicsRead.TryRead<RawResource>(address, out var stable) && stable.Id == after.Id && stable.Data == after.Data && stable.Length == after.Length,
                    "资源只读采样改变了元数据");
            }
            finally { Marshal.FreeHGlobal(address); }
        });

        Console.WriteLine($"\n原生物理只读回归：{count - failed}/{count} 通过");
        return failed;
    }

    private static byte[] ReadKnownBytes(nint address, int size)
    {
        var bytes = new byte[size];
        Marshal.Copy(address, bytes, 0, size);
        return bytes;
    }

    private static void Assert(bool condition, string? message = null)
    {
        if (!condition) throw new InvalidOperationException(message ?? "断言失败");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawTransform
    {
        public Vector4 Translation, Rotation, Scale;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawArray
    {
        public nint Data;
        public int Length, CapacityAndFlags;
    }

    [StructLayout(LayoutKind.Explicit, Size = 0x50)]
    private struct RawPose
    {
        [FieldOffset(0x00)] public nint Skeleton;
        [FieldOffset(0x08)] public RawArray Local;
        [FieldOffset(0x18)] public RawArray Model;
        [FieldOffset(0x28)] public RawArray Flags;
        [FieldOffset(0x38)] public byte ModelInSync;
        [FieldOffset(0x39)] public byte LocalInSync;
    }

    [StructLayout(LayoutKind.Explicit, Size = 0xC0)]
    private struct RawResource
    {
        [FieldOffset(0x10)] public uint Id;
        [FieldOffset(0xB0)] public nint Data;
        [FieldOffset(0xB8)] public ulong Length;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint VirtualAlloc(nint address, nuint size, uint allocationType, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualProtect(nint address, nuint size, uint protect, out uint oldProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualFree(nint address, nuint size, uint freeType);
}
