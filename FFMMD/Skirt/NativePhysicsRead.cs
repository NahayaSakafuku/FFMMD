using System;
using System.Runtime.InteropServices;

namespace FFMMD.Skirt;

/// <summary>
/// Bounded copies from this process. No native pointer is dereferenced by managed code.
/// ReadProcessMemory fails on inaccessible pages instead of relying on catching access violations.
/// Readability still does not prove object ownership; callers must revalidate native identities.
/// </summary>
internal static unsafe class NativePhysicsRead
{
    private const ulong MaximumUserAddress = 0x00007FFFFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(nint process, void* source, void* destination,
        nuint size, out nuint bytesRead);

    public static bool IsRangeValid(nint address, int size)
    {
        var start = (ulong)address;
        return size > 0 && start >= 0x10000 && start <= MaximumUserAddress &&
               (ulong)(size - 1) <= MaximumUserAddress - start;
    }

    public static bool TryRead<T>(nint address, out T value) where T : unmanaged
    {
        value = default;
        if (!OperatingSystem.IsWindows() || !IsRangeValid(address, sizeof(T))) return false;
        T copy = default;
        if (!ReadProcessMemory(-1, (void*)address, &copy, (nuint)sizeof(T), out var read) ||
            read != (nuint)sizeof(T)) return false;
        value = copy;
        return true;
    }

    public static bool TryReadBytes(nint address, Span<byte> destination)
    {
        if (destination.IsEmpty) return true;
        if (!OperatingSystem.IsWindows() || !IsRangeValid(address, destination.Length)) return false;
        fixed (byte* output = destination)
            return ReadProcessMemory(-1, (void*)address, output, (nuint)destination.Length, out var read) &&
                   read == (nuint)destination.Length;
    }

    public static bool TryGetVectorCount(nint first, nint last, nint end, int elementSize,
        int maxCount, out int count)
    {
        count = 0;
        if (elementSize <= 0 || maxCount < 0) return false;
        if (first == 0 && last == 0 && end == 0) return true;
        if (!IsRangeValid(first, 1) || !IsRangeValid(last, 1) || !IsRangeValid(end, 1)) return false;
        var begin = (ulong)first;
        var finish = (ulong)last;
        var capacity = (ulong)end;
        if (finish < begin || capacity < finish || begin % (ulong)Math.Min(elementSize, 8) != 0 ||
            (finish - begin) % (ulong)elementSize != 0 || (capacity - begin) % (ulong)elementSize != 0)
            return false;
        var length = (finish - begin) / (ulong)elementSize;
        var allocation = (capacity - begin) / (ulong)elementSize;
        if (length > (ulong)maxCount || allocation > (ulong)maxCount) return false;
        count = (int)length;
        return true;
    }
}
