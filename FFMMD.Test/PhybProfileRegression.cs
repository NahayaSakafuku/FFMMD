using System.Buffers.Binary;
using System.Text;
using FFMMD.Skirt;

/// <summary>Independent fixed-offset binary samples; no production serializer is used.</summary>
internal static class PhybProfileRegression
{
    private const int Simulation = 228, HeaderA = 232, HeaderB = 304;
    private const int References = 376, ChainA = 412, NodeA = 460, ChainB = 544, NodeB = 592;

    public static int Run()
    {
        var failures = 0; var count = 0;
        void Check(string name, Action body)
        {
            count++;
            try { body(); Console.WriteLine($"  ✔ {name}"); }
            catch (Exception e) { failures++; Console.WriteLine($"  ✘ {name}: {e.Message}"); }
        }

        Check("独立两模拟器 fixture 保持公共偏移基准及节点参数", () =>
        {
            var p = PhybProfileReader.Read(Fixture(), "fixed-offset-sample");
            Require(p.Status == "Parsed", p.Error ?? p.Status);
            Require(p.Simulators.Length == 2 && p.Simulators[0].Params.IsClothing && p.Simulators[1].Params.Group == 7, "simulator declarations wrong");
            var a = p.Simulators[0]; var b = p.Simulators[1];
            Require(a.Params.Gravity.SequenceEqual(new[] { 0f, -9.8f, 0f }) && a.Params.Wind.SequenceEqual(new[] { .1f, .2f, .3f }), "params vectors wrong");
            Require(a.Params.ConstraintLoop == 2 && a.Params.CollisionLoop == 1 && a.Params.Flags == 0x13, "params flags/loops wrong");
            Require(a.CollisionObjects.Single().CollisionName == "leg" && a.CollisionObjects.Single().Type == 1, "collision reference wrong");
            Require(a.Chains.Single().LastBoneOffset.SequenceEqual(new[] { .4f, .1f, -.2f }), "last offset wrong");
            Require(a.Chains.Single().Dampening == .9f && a.Chains.Single().MaxSpeed == 3f && a.Chains.Single().Type == 1, "chain parameters wrong");
            var n = a.Chains.Single().Nodes.Single();
            Require(n.BoneName == "j_sk_f_a_l" && n.Radius == .04f && n.AttractByAnimation == .075f && n.WindScale == .001f && n.GravityScale == 1f && n.ConeMaxAngle == .6f, "node scalar parameters wrong");
            Require(n.ConeAxisOffset.SequenceEqual(new[] { 1f, 0f, 0f }) && n.ConstraintPlaneNormal.SequenceEqual(new[] { 0f, 1f, 0f }) && n.CollisionFlag == 3 && n.ContinuousCollisionFlag == 5, "node vector/flag parameters wrong");
            Require(b.Chains.Single().Nodes.Single().BoneName == "j_mune_l", "second simulator used its own header as pointer base");
            Require(p.RuntimeOwnership.StartsWith("Unknown", StringComparison.Ordinal), "file declarations must not assert runtime ownership");
        });

        Check("胶囊几何与 sphere thickness/2 半径分别解析", () =>
        {
            var p = PhybProfileReader.Read(Fixture()); var c = p.Capsules.Single(); var s = p.Spheres.Single();
            Require(c.Name == "leg" && c.StartBone == "j_asi_a_l" && c.EndBone == "j_asi_b_l" && c.Radius == .12f, "capsule binding or radius wrong");
            Require(c.StartOffset.SequenceEqual(new[] { .1f, .2f, .3f }) && c.EndOffset.SequenceEqual(new[] { .4f, .5f, .6f }), "capsule offsets wrong");
            Require(s.Name == "hip" && s.BoneName == "j_kosi" && s.Thickness == .4f && s.Radius == .2f, "sphere thickness was interpreted as radius");
        });

        Check("有界未解析约束保留 counts 并标记部分 profile", () =>
        {
            var p = PhybProfileReader.Read(Fixture(true));
            Require(p.Parsed && p.Status == "ParsedBaseProfileOnly" && p.Warnings.Length == 5, "unparsed constraints must be explicit");
            var s = p.Simulators[0];
            Require(s.ConnectorCount == 1 && s.AttractCount == 1 && s.PinCount == 1 && s.SpringCount == 1 && s.PostAlignmentCount == 1, "constraint counts wrong");
        });

        Check("五类约束数组拒绝越界和缺失偏移", () =>
        {
            for (var section = 3; section < 8; section++)
            {
                var data = Fixture(true); U32(data, HeaderA + 40 + section * 4, uint.MaxValue); Rejected(data, "offset");
                data = Fixture(true); U32(data, HeaderA + 40 + section * 4, 0); Rejected(data, "no data offset");
                data = Fixture(true); U32(data, HeaderA + 40 + section * 4, (uint)(data.Length - Simulation - 8)); Rejected(data, "bounds");
            }
        });

        Check("链/节点/碰撞/约束 payload 不得指向模拟器头", () =>
        {
            foreach (var field in new[] { HeaderA + 40, HeaderA + 48, ChainA + 44, HeaderA + 52 })
            { var data = Fixture(true); U32(data, field, 4); Rejected(data, "header"); }
        });

        Check("空偏移不能掩盖非空链与节点声明", () =>
        {
            foreach (var field in new[] { HeaderB + 48, ChainB + 44 })
            { var data = Fixture(); U32(data, field, 0); Rejected(data, "no data offset"); }
        });

        Check("恶意模拟器和节点计数先于分配拒绝", () =>
        {
            var data = Fixture(); U32(data, Simulation, uint.MaxValue); Rejected(data, "Too many");
            data = Fixture(); U16(data, ChainA + 2, ushort.MaxValue); Rejected(data, "Too many");
        });

        Check("非有限参数与负半径拒绝", () =>
        {
            var data = Fixture(); F32(data, NodeB + 36, float.NaN); Rejected(data, "Non-finite");
            data = Fixture(); F32(data, NodeA + 32, -.01f); Rejected(data, "Negative");
            data = Fixture(); F32(data, HeaderA + 12, float.PositiveInfinity); Rejected(data, "Non-finite");
        });

        Check("不支持碰撞形状与负 loop 明确拒绝", () =>
        {
            var data = Fixture(); data[17] = 1; Rejected(data, "Unsupported");
            data = Fixture(); U16(data, HeaderA + 32, ushort.MaxValue); Rejected(data, "Negative simulator loop");
        });

        Check("截断与 collision/simulation 重叠拒绝", () =>
        {
            Rejected(Fixture()[..640], "bounds");
            var data = Fixture(); U32(data, 12, 100); Rejected(data, "overlap");
        });

        Check("内容 hash 与尾部未解析状态明确", () =>
        {
            var data = Fixture(); var p = PhybProfileReader.Read(data);
            Require(p.Sha256.Length == 64 && p.FileSize == data.Length, "content identity missing");
            var changed = Fixture(); changed[HeaderA + 37] = 3;
            Require(PhybProfileReader.Read(changed).Sha256 != p.Sha256, "hash does not track content");
            var trailing = new byte[data.Length + 16]; data.CopyTo(trailing, 0); var t = PhybProfileReader.Read(trailing);
            Require(t.Status == "ParsedBaseProfileOnly" && t.HasUnparsedTrailingData, "unparsed trailing extension not reported");
        });

        var path = Environment.GetEnvironmentVariable("FFMMD_PHYB_SAMPLE");
        if (!string.IsNullOrWhiteSpace(path)) Check("真实本地 profile 可解析（样本不进仓库）", () =>
        {
            var p = PhybProfileReader.ReadFile(path); Require(p.Parsed, p.Error ?? "profile rejected");
            Require(p.Capsules.Length + p.Spheres.Length > 0 && p.Simulators.Any(s => s.Params.IsClothing && s.Chains.SelectMany(c => c.Nodes).Any(n => n.BoneName.StartsWith("j_sk_", StringComparison.Ordinal))), "real profile lacks declared clothing/skirt data");
        });
        Console.WriteLine($"\n.phyb profile 回归：{count - failures}/{count} 通过"); return failures;
    }

    private static byte[] Fixture(bool constraints = false)
    {
        // Literal positions are an independent sample definition. The second
        // simulator deliberately points past the first simulator's node payload.
        var data = new byte[constraints ? 860 : 676];
        U32(data, 0, 0x01000001); U32(data, 4, 3); U32(data, 8, 16); U32(data, 12, Simulation);
        data[16] = 1; data[20] = 1;
        Text(data, 24, "leg"); Text(data, 56, "j_asi_a_l"); Text(data, 88, "j_asi_b_l");
        Vector(data, 120, .1f, .2f, .3f); Vector(data, 132, .4f, .5f, .6f); F32(data, 144, .12f);
        Text(data, 148, "hip"); Text(data, 180, "j_kosi"); Vector(data, 212, 0, .2f, 0); F32(data, 224, .4f);
        U32(data, Simulation, 2); data[HeaderA] = 1; data[HeaderA + 2] = 1; data[HeaderB + 2] = 1;
        Vector(data, HeaderA + 8, 0, -9.8f, 0); Vector(data, HeaderA + 20, .1f, .2f, .3f);
        U16(data, HeaderA + 32, 2); U16(data, HeaderA + 34, 1); data[HeaderA + 36] = 0x13; data[HeaderA + 37] = 2;
        Vector(data, HeaderB + 8, 0, -9.8f, 0); U16(data, HeaderB + 32, 2); data[HeaderB + 37] = 7;
        U32(data, HeaderA + 40, 144); U32(data, HeaderA + 48, 180); U32(data, HeaderB + 48, 312);
        Text(data, References, "leg"); U32(data, References + 32, 1);
        U16(data, ChainA + 2, 1); U16(data, ChainB + 2, 1);
        F32(data, ChainA + 4, .9f); F32(data, ChainA + 8, 3f); F32(data, ChainA + 12, .1f); F32(data, ChainA + 16, .2f); F32(data, ChainA + 20, .3f);
        Vector(data, ChainA + 24, .4f, .1f, -.2f); U32(data, ChainA + 36, 1); U32(data, ChainA + 44, 228); U32(data, ChainB + 44, 360);
        Text(data, NodeA, "j_sk_f_a_l"); Text(data, NodeB, "j_mune_l");
        foreach (var node in new[] { NodeA, NodeB })
        {
            F32(data, node + 32, .04f); F32(data, node + 36, .075f); F32(data, node + 40, .001f); F32(data, node + 44, 1); F32(data, node + 48, .6f);
            Vector(data, node + 52, 1, 0, 0); Vector(data, node + 64, 0, 1, 0); U32(data, node + 76, 3); U32(data, node + 80, 5);
        }
        if (constraints)
        {
            var positions = new[] { 676, 708, 760, 808, 824 };
            for (var i = 0; i < positions.Length; i++) { data[HeaderA + 3 + i] = 1; U32(data, HeaderA + 52 + i * 4, (uint)(positions[i] - Simulation - 4)); }
        }
        return data;
    }
    private static void Text(byte[] data, int offset, string value) => Encoding.ASCII.GetBytes(value).CopyTo(data, offset);
    private static void U32(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset, 4), value);
    private static void U16(byte[] data, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset, 2), value);
    private static void F32(byte[] data, int offset, float value) => U32(data, offset, (uint)BitConverter.SingleToInt32Bits(value));
    private static void Vector(byte[] data, int offset, float x, float y, float z) { F32(data, offset, x); F32(data, offset + 4, y); F32(data, offset + 8, z); }
    private static void Rejected(byte[] data, string message)
    {
        var p = PhybProfileReader.Read(data);
        Require(!p.Parsed && p.Status == "Rejected" && p.Error?.Contains(message, StringComparison.OrdinalIgnoreCase) == true, $"expected rejection containing {message}, got {p.Status}: {p.Error}");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
