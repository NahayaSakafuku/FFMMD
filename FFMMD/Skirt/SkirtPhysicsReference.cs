using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using FFMMD.Retarget;

namespace FFMMD.Skirt;

/// <summary>Skeleton and physics numbers only; the default reference contains no mesh or textures.</summary>
public sealed class SkirtPhysicsReference
{
    public SourceRigDefinition Rig { get; set; } = new();
    public PmxPhysicsProfile Physics { get; set; } = new();
    private static readonly JsonSerializerOptions Options = new() { IncludeFields = true, WriteIndented = true };

    public static SkirtPhysicsReference FromPmx(byte[] bytes)
    {
        var reference = new SkirtPhysicsReference { Rig = PmxRigReader.Parse(bytes), Physics = PmxPhysicsProfileReader.Parse(bytes) };
        reference.Validate(); return reference;
    }
    public static SkirtPhysicsReference Builtin()
    {
        using var stream = typeof(SkirtPhysicsReference).Assembly.GetManifestResourceStream("FFMMD.Skirt.DefaultReference.json")
            ?? throw new InvalidDataException("Bundled skirt physics reference is missing.");
        var value = JsonSerializer.Deserialize<SkirtPhysicsReference>(stream, Options) ?? throw new InvalidDataException("Invalid bundled physics reference.");
        value.Validate(); return value;
    }
    public byte[] Serialize() => JsonSerializer.SerializeToUtf8Bytes(this, Options);
    [JsonIgnore] public string ContentHash => Convert.ToHexString(SHA256.HashData(Serialize()));
    public void Validate()
    {
        Rig.Validate();
        if (Rig.Bones.Length != Physics.BoneCount || Rig.Fingerprint != Physics.Fingerprint || Physics.HasUnsupportedPhysics ||
            Physics.RigidBodies.Length is < 1 or > 4096 || Physics.Joints.Length > 8192)
            throw new InvalidDataException("Unsupported skirt physics reference.");
        for (var layer = 0; layer < 3; layer++)
            for (var column = 0; column < 6; column++)
                if (Rig.Find($"Skirt_{layer}_{column}") < 0) throw new InvalidDataException("Reference needs the six three-bone skirt chains.");
    }
}
