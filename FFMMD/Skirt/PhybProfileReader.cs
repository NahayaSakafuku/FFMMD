using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace FFMMD.Skirt;

/// <summary>
/// Bounded, read-only parser for the legacy .phyb binary profile. This is an original
/// format reader based on public field descriptions; it never calls native physics.
/// </summary>
public static class PhybProfileReader
{
    private const int MaxRecords = 4096;
    public const int MaxFileBytes = 16 * 1024 * 1024;
    private const int CapsuleSize = 124;
    private const int SphereSize = 80;
    private const int SimHeaderSize = 72;
    private const int ChainSize = 48;
    private const int NodeSize = 84;
    private const int CollisionReferenceSize = 36;
    private const int ConnectorSize = 32;
    private const int AttractSize = 52;
    private const int PinSize = 48;
    private const int SpringSize = 16;
    private const int PostAlignmentSize = 36;

    public static PhybProfile ReadFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return Reject("Path is empty.");
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length > MaxFileBytes) return Reject($"PH YB file exceeds {MaxFileBytes} bytes.", path);
            var bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            return Read(bytes, path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        { return Reject($"Could not read PH YB file: {e.Message}", path); }
    }

    public static PhybProfile Read(ReadOnlySpan<byte> data, string sourcePath = "")
    {
        var profile = new PhybProfile { SourcePath = sourcePath, FileSize = data.Length };
        if (data.Length > MaxFileBytes) return Reject($"PH YB payload exceeds {MaxFileBytes} bytes.", sourcePath, profile);
        try
        {
            profile.Sha256 = Convert.ToHexString(SHA256.HashData(data));
            var reader = new Reader(data);
            profile.Version = reader.U32(0);
            profile.DataType = reader.U32(4);
            profile.CollisionOffset = reader.U32(8);
            profile.SimulationOffset = reader.U32(12);
            // Version is the packed four-byte format marker (for example 01 00 00 01),
            // so it is deliberately not treated as a small integer.
            if (profile.Version is not 0x01000001 and not 1 || profile.DataType > 3)
                return Reject("Unsupported PH YB version or data type.", sourcePath, profile);
            if (profile.CollisionOffset > int.MaxValue || profile.SimulationOffset > int.MaxValue ||
                profile.CollisionOffset < 16 || profile.SimulationOffset < profile.CollisionOffset ||
                profile.SimulationOffset > data.Length)
                return Reject("Header offsets are outside the file or not ordered.", sourcePath, profile);
            if (profile.CollisionOffset != profile.SimulationOffset) ParseCollision(ref reader, profile);
            if (profile.SimulationOffset != data.Length) ParseSimulation(ref reader, profile);
            // Optional EP(H)B/extended data may follow the classic sections.
            profile.HasUnparsedTrailingData = reader.Consumed < data.Length;
            if (profile.HasUnparsedTrailingData) profile.Warnings = profile.Warnings.Append("Trailing data is not parsed; extended physics declarations may be unavailable.").ToArray();
            profile.Status = profile.Warnings.Length == 0 ? "Parsed" : "ParsedBaseProfileOnly";
            return profile;
        }
        catch (FormatException e) { return Reject(e.Message, sourcePath, profile); }
        catch (ArgumentOutOfRangeException e) { return Reject($"Out-of-range PH YB field: {e.Message}", sourcePath, profile); }
        catch (OverflowException) { return Reject("PH YB offset arithmetic overflow.", sourcePath, profile); }
    }

    private static void ParseCollision(ref Reader r, PhybProfile p)
    {
        var o = CheckedOffset(p.CollisionOffset, r.Length);
        var capsules = r.Byte(o); var ellipsoids = r.Byte(o + 1); var planes = r.Byte(o + 2);
        var threePlanes = r.Byte(o + 3); var spheres = r.Byte(o + 4);
        if (ellipsoids != 0 || planes != 0 || threePlanes != 0)
            throw new FormatException("Unsupported PH YB collision shape: ellipsoid or plane declarations are not parsed.");
        CheckCount(capsules, "capsules"); CheckCount(spheres, "spheres");
        r.ClaimRecords(capsules + spheres);
        var pos = checked(o + 8);
        var end = checked(pos + capsules * CapsuleSize + spheres * SphereSize);
        if (end > p.SimulationOffset) throw new FormatException("Collision records overlap the simulation section.");
        r.Need(pos, end - pos, "collision records");
        p.Capsules = new PhybCapsuleProfile[capsules];
        for (var i = 0; i < capsules; i++)
        {
            r.Need(pos, CapsuleSize, "capsule");
            p.Capsules[i] = new PhybCapsuleProfile {
                Name = r.Padded(pos, 32), StartBone = r.Padded(pos + 32, 32), EndBone = r.Padded(pos + 64, 32),
                StartOffset = r.Vec3(pos + 96), EndOffset = r.Vec3(pos + 108), Radius = r.Radius(pos + 120, "capsule radius")
            }; pos += CapsuleSize;
        }
        p.Spheres = new PhybSphereProfile[spheres];
        for (var i = 0; i < spheres; i++)
        {
            r.Need(pos, SphereSize, "sphere");
            var thickness = r.Radius(pos + 76, "sphere thickness");
            p.Spheres[i] = new PhybSphereProfile {
                Name = r.Padded(pos, 32), BoneName = r.Padded(pos + 32, 32), Offset = r.Vec3(pos + 64), Thickness = thickness, Radius = thickness * .5f
            }; pos += SphereSize;
        }
    }

    private static void ParseSimulation(ref Reader r, PhybProfile p)
    {
        var sim = CheckedOffset(p.SimulationOffset, r.Length);
        r.Need(sim, 4, "simulation count");
        var count = r.U32(sim); CheckCount(count, "simulators");
        r.ClaimRecords(checked((int)count));
        var start = checked(sim + 4);
        var headerEnd = checked(start + (int)count * SimHeaderSize);
        r.Need(start, headerEnd - start, "simulator headers");
        p.Simulators = new PhybSimulatorProfile[count];
        for (var i = 0; i < count; i++)
        {
            r.Need(start, SimHeaderSize, "simulator header");
            var simulator = new PhybSimulatorProfile { Index = i };
            var nCollision = r.Byte(start); var nConnector = r.Byte(start + 1); var nChain = r.Byte(start + 2);
            simulator.ConnectorCount = r.Byte(start + 3); simulator.AttractCount = r.Byte(start + 4);
            simulator.PinCount = r.Byte(start + 5); simulator.SpringCount = r.Byte(start + 6); simulator.PostAlignmentCount = r.Byte(start + 7);
            simulator.Params = new PhybSimulatorParamsProfile {
                Gravity = r.Vec3(start + 8), Wind = r.Vec3(start + 20),
                ConstraintLoop = r.U16(start + 32), CollisionLoop = r.U16(start + 34),
                Flags = r.Byte(start + 36), Group = r.Byte(start + 37)
            };
            if (simulator.Params.ConstraintLoop > short.MaxValue || simulator.Params.CollisionLoop > short.MaxValue)
                throw new FormatException("Negative simulator loop count is not supported.");
            // Public PH YB records place the eight section offsets immediately after params.
            var offsets = new uint[8]; for (var x = 0; x < offsets.Length; x++) offsets[x] = r.U32(start + 40 + x * 4);
            // Offsets are relative to the simulation section's u32 count;
            // their payload begins another four bytes after the relative value.
            var sectionBase = sim;
            simulator.CollisionObjects = ParseCollisionReferences(ref r, sectionBase, offsets[0], nCollision, headerEnd);
            simulator.CollisionConnections = ParseCollisionReferences(ref r, sectionBase, offsets[1], nConnector, headerEnd);
            simulator.Chains = ParseChains(ref r, sectionBase, offsets[2], nChain, headerEnd);
            ValidateSkippedSection(ref r, sectionBase, offsets[3], simulator.ConnectorCount, ConnectorSize, "connectors", headerEnd, p);
            ValidateSkippedSection(ref r, sectionBase, offsets[4], simulator.AttractCount, AttractSize, "attracts", headerEnd, p);
            ValidateSkippedSection(ref r, sectionBase, offsets[5], simulator.PinCount, PinSize, "pins", headerEnd, p);
            ValidateSkippedSection(ref r, sectionBase, offsets[6], simulator.SpringCount, SpringSize, "springs", headerEnd, p);
            ValidateSkippedSection(ref r, sectionBase, offsets[7], simulator.PostAlignmentCount, PostAlignmentSize, "post-alignments", headerEnd, p);
            p.Simulators[i] = simulator;
            start += SimHeaderSize;
        }
    }

    private static PhybCollisionReferenceProfile[] ParseCollisionReferences(ref Reader r, int baseOffset, uint offset, int count, int headerEnd)
    {
        if (count == 0) return [];
        if (offset == 0 || offset == 0xCCCCCCCC) throw new FormatException("Nonempty collision references have no data offset.");
        CheckCount(count, "collision references");
        var pos = CheckedOffset(checked((uint)baseOffset + offset + 4), r.Length);
        if (pos < headerEnd) throw new FormatException("Collision data overlaps simulator headers.");
        r.ClaimRecords(count);
        r.Need(pos, checked(count * CollisionReferenceSize), "collision reference array");
        var output = new PhybCollisionReferenceProfile[count];
        for (var i = 0; i < count; i++) { r.Need(pos, CollisionReferenceSize, "collision reference"); output[i] = new() { CollisionName = r.Padded(pos, 32), Type = r.U32(pos + 32) }; pos += CollisionReferenceSize; }
        return output;
    }

    private static PhybChainProfile[] ParseChains(ref Reader r, int baseOffset, uint offset, int count, int headerEnd)
    {
        if (count == 0) return [];
        if (offset == 0 || offset == 0xCCCCCCCC) throw new FormatException("Nonempty chains have no data offset.");
        CheckCount(count, "chains"); var pos = CheckedOffset(checked((uint)baseOffset + offset + 4), r.Length); var output = new PhybChainProfile[count];
        if (pos < headerEnd) throw new FormatException("Chain data overlaps simulator headers.");
        r.ClaimRecords(count);
        r.Need(pos, checked(count * ChainSize), "chain header array");
        for (var i = 0; i < count; i++)
        {
            r.Need(pos, ChainSize, "chain header");
            var collisions = r.U16(pos); var nodes = r.U16(pos + 2);
            var chain = new PhybChainProfile { Index = i, Dampening = r.FiniteFloat(pos + 4, "dampening"), MaxSpeed = r.FiniteFloat(pos + 8, "max speed"), Friction = r.FiniteFloat(pos + 12, "friction"), CollisionDampening = r.FiniteFloat(pos + 16, "collision dampening"), RepulsionStrength = r.FiniteFloat(pos + 20, "repulsion"), LastBoneOffset = r.Vec3(pos + 24), Type = r.U32(pos + 36) };
            var collisionOffset = r.U32(pos + 40); var nodeOffset = r.U32(pos + 44);
            chain.Collisions = ParseCollisionReferences(ref r, baseOffset, collisionOffset, collisions, headerEnd);
            CheckCount(nodes, "chain nodes");
            if (nodes > 0)
            {
                if (nodeOffset == 0 || nodeOffset == 0xCCCCCCCC) throw new FormatException("Nonempty chain nodes have no data offset.");
                var npos = CheckedOffset(checked((uint)baseOffset + nodeOffset + 4), r.Length); var arr = new PhybNodeProfile[nodes];
                if (npos < headerEnd) throw new FormatException("Node data overlaps simulator headers.");
                r.ClaimRecords(nodes);
                r.Need(npos, checked(nodes * NodeSize), "chain node array");
                for (var n = 0; n < nodes; n++) { r.Need(npos, NodeSize, "chain node"); arr[n] = ReadNode(ref r, npos, n); npos += NodeSize; }
                chain.Nodes = arr;
            }
            output[i] = chain; pos += ChainSize;
        }
        return output;
    }

    private static void ValidateSkippedSection(ref Reader r, int baseOffset, uint offset, int count, int size, string name, int headerEnd, PhybProfile profile)
    {
        if (count == 0) return;
        if (offset == 0 || offset == 0xCCCCCCCC) throw new FormatException($"Nonempty {name} have no data offset.");
        CheckCount(count, name);
        var pos = CheckedOffset(checked((uint)baseOffset + offset + 4), r.Length);
        if (pos < headerEnd) throw new FormatException($"{name} overlap simulator headers.");
        r.ClaimRecords(count); r.Need(pos, checked(count * size), $"{name} records");
        profile.Warnings = profile.Warnings.Append($"{name} records are bounded and counted but their payload is not parsed.").ToArray();
    }

    private static PhybNodeProfile ReadNode(ref Reader r, int o, int index) => new() {
        Index = index, BoneName = r.Padded(o, 32), Radius = r.Radius(o + 32, "node radius"), AttractByAnimation = r.FiniteFloat(o + 36, "node attract"), WindScale = r.FiniteFloat(o + 40, "node wind"), GravityScale = r.FiniteFloat(o + 44, "node gravity"), ConeMaxAngle = r.FiniteFloat(o + 48, "node cone"), ConeAxisOffset = r.Vec3(o + 52), ConstraintPlaneNormal = r.Vec3(o + 64), CollisionFlag = r.U32(o + 76), ContinuousCollisionFlag = r.U32(o + 80)
    };

    private static void CheckCount(uint count, string name) { if (count > MaxRecords) throw new FormatException($"Too many {name}: {count}."); }
    private static void CheckCount(int count, string name) { if (count < 0 || count > MaxRecords) throw new FormatException($"Too many {name}: {count}."); }
    private static int CheckedOffset(uint o, int length) { if (o > int.MaxValue || o >= length) throw new FormatException($"Offset {o} is outside file."); return (int)o; }
    private static PhybProfile Reject(string error, string path = "", PhybProfile? p = null) { p ??= new PhybProfile(); p.Status = "Rejected"; p.Error = error; p.SourcePath = path; return p; }

    private ref struct Reader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _recordCount;
        public int Length => _data.Length; public int Consumed { get; private set; }
        public Reader(ReadOnlySpan<byte> data) { _data = data; }
        public bool Range(int o, int n) => o >= 0 && n >= 0 && o <= _data.Length - n;
        public void Need(int o, int n, string what) { if (!Range(o, n)) throw new FormatException($"{what} exceeds file bounds."); Consumed = Math.Max(Consumed, o + n); }
        public void ClaimRecords(int count) { _recordCount = checked(_recordCount + count); if (_recordCount > MaxRecords) throw new FormatException("PH YB record budget exceeded."); }
        public byte Byte(int o) { Need(o, 1, "byte"); return _data[o]; }
        public uint U32(int o) { Need(o, 4, "u32"); return BinaryPrimitives.ReadUInt32LittleEndian(_data[o..]); }
        public ushort U16(int o) { Need(o, 2, "u16"); return BinaryPrimitives.ReadUInt16LittleEndian(_data[o..]); }
        public float FiniteFloat(int o, string what) { Need(o, 4, what); var f = BitConverter.Int32BitsToSingle((int)U32(o)); if (!float.IsFinite(f)) throw new FormatException($"Non-finite {what}."); return f; }
        public float Radius(int o, string what) { var value = FiniteFloat(o, what); if (value < 0) throw new FormatException($"Negative {what}."); return value; }
        public float[] Vec3(int o) => [FiniteFloat(o, "vector"), FiniteFloat(o + 4, "vector"), FiniteFloat(o + 8, "vector")];
        public string Padded(int o, int n) { Need(o, n, "padded string"); var span = _data.Slice(o, n); var len = span.IndexOf((byte)0); if (len < 0) len = span.Length; while (len > 0 && span[len - 1] == 0xFE) len--; return Encoding.UTF8.GetString(span[..len]); }
    }
}
