using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace FFMMD.Skirt;

/// <summary>
/// Bounded, read-only PMX 2.0/2.1 physics parser. It skips meshes/materials/morphs,
/// preserves raw MMD units, and never loads a texture, native engine, or game object.
/// </summary>
public static class PmxPhysicsProfileReader
{
    public const long MaximumFileBytes = 512L * 1024 * 1024;
    public const int MaximumBones = 8192;
    public const int MaximumRigidBodies = 8192;
    public const int MaximumJoints = 32768;

    public static PmxPhysicsProfile Parse(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > MaximumFileBytes) throw new InvalidDataException("PMX file exceeds 512 MB.");
        var fingerprint = Convert.ToHexString(SHA256.HashData(stream));
        stream.Position = 0;
        return Parse(stream, fingerprint);
    }

    public static PmxPhysicsProfile Parse(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.LongLength > MaximumFileBytes) throw new InvalidDataException("PMX file exceeds 512 MB.");
        using var stream = new MemoryStream(bytes, false);
        return Parse(stream, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    private static PmxPhysicsProfile Parse(Stream stream, string fingerprint)
    {
        using var r = new BinaryReader(stream, Encoding.UTF8, true);
        long Remaining() => stream.Length - stream.Position;
        void Skip(long n)
        {
            if (n < 0 || n > Remaining()) throw new InvalidDataException("Truncated PMX payload.");
            stream.Position += n;
        }
        int Count(int minimum, int maximum = 1_000_000)
        {
            var n = r.ReadInt32();
            if (n < 0 || n > maximum || (long)n * minimum > Remaining())
                throw new InvalidDataException("PMX count is outside the supported bounds.");
            return n;
        }
        float Float()
        {
            var v = r.ReadSingle();
            if (!float.IsFinite(v)) throw new InvalidDataException("PMX contains a non-finite value.");
            return v;
        }
        Vector3 Vec() => new(Float(), Float(), Float());
        void CheckIndex(int index, int count, string kind)
        {
            if (index < -1 || index >= count) throw new InvalidDataException($"Invalid PMX {kind} index: {index}.");
        }
        void NonNegative(Vector3 v, string kind)
        {
            if (v.X < 0 || v.Y < 0 || v.Z < 0) throw new InvalidDataException($"Negative PMX {kind} value.");
        }
        try
        {
            if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "PMX ") throw new InvalidDataException("Not a PMX file.");
            var version = Float();
            if (MathF.Abs(version - 2) > 1e-4f && MathF.Abs(version - 2.1f) > 1e-4f)
                throw new InvalidDataException("Only PMX 2.0 and 2.1 are supported.");
            var globals = r.ReadByte();
            if (globals < 8) throw new InvalidDataException("PMX global header is incomplete.");
            var flags = r.ReadBytes(globals);
            if (flags.Length != globals || flags[0] > 1 || flags[1] > 4)
                throw new InvalidDataException("Invalid PMX text encoding or extra UV count.");
            for (var i = 2; i < 8; i++)
                if (flags[i] is not (1 or 2 or 4)) throw new InvalidDataException("Invalid PMX index width.");
            Encoding encoding = flags[0] == 0 ? new UnicodeEncoding(false, false, true) : new UTF8Encoding(false, true);
            string Text()
            {
                var n = Count(1, 1_048_576);
                if (flags[0] == 0 && (n & 1) != 0) throw new InvalidDataException("Odd PMX UTF-16 text length.");
                var data = r.ReadBytes(n);
                if (data.Length != n) throw new InvalidDataException("Truncated PMX text.");
                return encoding.GetString(data);
            }
            int Index(int size) => size switch
            {
                1 => r.ReadSByte(), 2 => r.ReadInt16(), 4 => r.ReadInt32(),
                _ => throw new InvalidDataException("Invalid PMX index width."),
            };
            var name = Text(); Text(); Text(); Text();
            var vertices = Count(38, 10_000_000);
            for (var i = 0; i < vertices; i++)
            {
                Skip(32 + 16 * flags[1]);
                var skin = r.ReadByte();
                Skip(skin switch
                {
                    0 => flags[5], 1 => 2 * flags[5] + 4,
                    2 => 4 * flags[5] + 16, 3 => 2 * flags[5] + 40,
                    4 when version > 2.05f => 4 * flags[5] + 16,
                    _ => throw new InvalidDataException("Unsupported PMX skinning kind."),
                });
                Skip(4);
            }
            Skip((long)Count(flags[2], 30_000_000) * flags[2]);
            var textures = Count(4);
            for (var i = 0; i < textures; i++) Text();
            var materials = Count(70);
            for (var i = 0; i < materials; i++)
            {
                Text(); Text(); Skip(65 + 2 * flags[3]); Skip(1);
                var shared = r.ReadByte();
                if (shared == 0) Skip(flags[3]); else if (shared == 1) Skip(1);
                else throw new InvalidDataException("Invalid PMX toon flag.");
                Text(); Count(0, 30_000_000);
            }
            var boneCount = Count(22, MaximumBones);
            var boneNames = new string[boneCount];
            for (var i = 0; i < boneCount; i++)
            {
                boneNames[i] = Text(); Text(); Vec();
                CheckIndex(Index(flags[5]), boneCount, "bone parent");
                r.ReadInt32(); var boneFlags = r.ReadUInt16();
                if ((boneFlags & 1) != 0) CheckIndex(Index(flags[5]), boneCount, "bone tail"); else Vec();
                if ((boneFlags & 0x300) != 0) { CheckIndex(Index(flags[5]), boneCount, "append bone"); Float(); }
                if ((boneFlags & 0x400) != 0) Vec();
                if ((boneFlags & 0x800) != 0) { Vec(); Vec(); }
                if ((boneFlags & 0x2000) != 0) r.ReadInt32();
                if ((boneFlags & 0x20) == 0) continue;
                CheckIndex(Index(flags[5]), boneCount, "IK effector"); Count(0, 4096); Float();
                var links = Count(flags[5] + 1, 256);
                for (var j = 0; j < links; j++)
                {
                    CheckIndex(Index(flags[5]), boneCount, "IK link"); var limited = r.ReadByte();
                    if (limited > 1) throw new InvalidDataException("Invalid PMX IK limit flag.");
                    if (limited == 1) { Vec(); Vec(); }
                }
            }
            var morphs = Count(14);
            for (var i = 0; i < morphs; i++)
            {
                Text(); Text(); r.ReadByte(); var kind = r.ReadByte();
                var recordSize = kind switch
                {
                    0 => flags[6] + 4, 1 => flags[2] + 12, 2 => flags[5] + 28,
                    >= 3 and <= 7 => flags[2] + 16, 8 => flags[4] + 113,
                    9 when version > 2.05f => flags[6] + 4,
                    10 when version > 2.05f => flags[7] + 25,
                    _ => throw new InvalidDataException("Unsupported PMX morph kind."),
                };
                Skip((long)Count(recordSize, 10_000_000) * recordSize);
            }
            var displayFrames = Count(13);
            for (var i = 0; i < displayFrames; i++)
            {
                Text(); Text(); var special = r.ReadByte();
                if (special > 1) throw new InvalidDataException("Invalid PMX display-frame flag.");
                var elements = Count(1 + Math.Min(flags[5], flags[6]));
                for (var j = 0; j < elements; j++)
                {
                    var kind = r.ReadByte();
                    if (kind == 0) CheckIndex(Index(flags[5]), boneCount, "display bone");
                    else if (kind == 1) CheckIndex(Index(flags[6]), morphs, "display morph");
                    else throw new InvalidDataException("Invalid PMX display element kind.");
                }
            }
            var bodies = new PmxRigidBody[Count(69 + flags[5], MaximumRigidBodies)];
            for (var i = 0; i < bodies.Length; i++)
            {
                var bodyName = Text(); var englishName = Text(); var bone = Index(flags[5]);
                CheckIndex(bone, boneCount, "rigid-body bone");
                var group = r.ReadByte(); var mask = r.ReadUInt16(); var shape = r.ReadByte();
                if (group > 15 || shape > 2) throw new InvalidDataException("Invalid PMX rigid-body group or shape.");
                var size = Vec(); NonNegative(size, "rigid-body size");
                if (size.X <= 0 || (shape == 1 && (size.Y <= 0 || size.Z <= 0)))
                    throw new InvalidDataException("PMX rigid-body shape has a zero dimension.");
                var position = Vec(); var rotation = Vec(); var mass = Float();
                var linear = Float(); var angular = Float(); var restitution = Float(); var friction = Float();
                var mode = r.ReadByte();
                if (mode > 2 || mass < 0 || friction < 0 || linear is < 0 or > 1 || angular is < 0 or > 1 || restitution is < 0 or > 1)
                    throw new InvalidDataException("Invalid PMX rigid-body physical parameters.");
                if (mode != 0 && mass <= 0) throw new InvalidDataException("A dynamic PMX rigid body must have positive mass.");
                bodies[i] = new PmxRigidBody { Index = i, Name = bodyName, EnglishName = englishName,
                    BoneIndex = bone, CollisionGroup = group, CollisionMask = mask,
                    Shape = (PmxRigidBodyShape)shape, Size = size, Position = position, RotationEuler = rotation,
                    Mass = mass, LinearDamping = linear, AngularDamping = angular,
                    Restitution = restitution, Friction = friction, Mode = (PmxRigidBodyMode)mode };
            }
            var joints = new PmxPhysicsJoint[Count(105 + 2 * flags[7], MaximumJoints)];
            for (var i = 0; i < joints.Length; i++)
            {
                var jointName = Text(); var englishName = Text(); var type = r.ReadByte();
                if (type > 5 || (version < 2.05f && type != 0)) throw new InvalidDataException("Invalid PMX joint kind for this version.");
                var a = Index(flags[7]); var b = Index(flags[7]);
                CheckIndex(a, bodies.Length, "joint body A"); CheckIndex(b, bodies.Length, "joint body B");
                var position = Vec(); var rotation = Vec(); var translationMin = Vec(); var translationMax = Vec();
                var rotationMin = Vec(); var rotationMax = Vec(); var translationSpring = Vec(); var rotationSpring = Vec();
                NonNegative(translationSpring, "translation spring"); NonNegative(rotationSpring, "rotation spring");
                joints[i] = new PmxPhysicsJoint { Index = i, Name = jointName, EnglishName = englishName, Type = type,
                    RigidBodyA = a, RigidBodyB = b, Position = position, RotationEuler = rotation,
                    TranslationMinimum = translationMin, TranslationMaximum = translationMax,
                    RotationMinimum = rotationMin, RotationMaximum = rotationMax,
                    TranslationSpring = translationSpring, RotationSpring = rotationSpring };
            }
            var softBodies = 0; var warnings = new List<string>();
            if (version > 2.05f && Remaining() != 0) softBodies = Count(0, MaximumRigidBodies);
            if (softBodies != 0) warnings.Add("PMX soft bodies are unsupported by the rigid-body baker.");
            if (joints.Any(j => j.Type != 0)) warnings.Add("PMX joint kinds other than spring 6DOF are unsupported by the baker.");
            if (Remaining() != 0) warnings.Add("PMX has trailing data after the parsed rigid physics section.");
            return new PmxPhysicsProfile { Name = name, Fingerprint = fingerprint, Version = version,
                BoneCount = boneCount, BoneNames = boneNames, RigidBodies = bodies, Joints = joints,
                SoftBodyCount = softBodies, TrailingBytes = Remaining(), Warnings = warnings.ToArray() };
        }
        catch (EndOfStreamException e) { throw new InvalidDataException("Truncated PMX payload.", e); }
        catch (DecoderFallbackException e) { throw new InvalidDataException("Invalid PMX text encoding.", e); }
    }
}
