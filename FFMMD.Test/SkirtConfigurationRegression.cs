using System.Text.Json;
using FFMMD;

/// <summary>自动裙骨物理配置、旧实验字段清理与缓存关联保存回归。</summary>
internal static class SkirtConfigurationRegression
{
    private static readonly JsonSerializerOptions JsonOptions = new() { IncludeFields = true };

    public static int Run()
    {
        var failed = 0;
        var count = 0;
        void Test(string name, Action body)
        {
            count++;
            try { body(); Console.WriteLine($"  ✔ {name}"); }
            catch (Exception e) { failed++; Console.WriteLine($"  ✘ {name}: {e.Message}"); }
        }

        Test("新配置与旧配置默认开启自动裙骨物理且无需外部工具", () =>
        {
            var fresh = new Config();
            fresh.Normalize();
            var legacy = Load("{\"RigPipelineVersion\":2,\"Cal\":{\"SkirtSimEnabled\":true,\"SkirtSwing\":1.7}}");
            legacy.Normalize();
            Require(fresh.AutoSkirtPhysics && legacy.AutoSkirtPhysics,
                "New and legacy configurations should enable automatic skirt physics by default.");
            Require(fresh.SkirtPhysicsPmxPath == null && legacy.SkirtPhysicsPmxPath == null,
                "The built-in physics reference must work without a configured PMX path.");
            var saved = JsonSerializer.Serialize(legacy, JsonOptions);
            Require(!saved.Contains("SkirtSimEnabled", StringComparison.Ordinal) && !saved.Contains("SkirtSwing", StringComparison.Ordinal) &&
                !saved.Contains("SkirtBlenderPath", StringComparison.Ordinal), "Removed experimental and external-tool fields must not be saved again.");
        });

        foreach (var enabled in new[] { true, false })
        {
            Test($"配置迁移与保存往返保留自动物理选择 {enabled}", () =>
            {
                var json = enabled
                    ? "{\"RigPipelineVersion\":0,\"AutoSkirtPhysics\":true}"
                    : "{\"RigPipelineVersion\":0,\"AutoSkirtPhysics\":false}";
                var config = Load(json);
                config.Normalize();
                var reloaded = Load(JsonSerializer.Serialize(config, JsonOptions));
                reloaded.Normalize();
                Require(config.AutoSkirtPhysics == enabled && reloaded.AutoSkirtPhysics == enabled,
                    "Normalization and serialization must preserve the explicit setting.");
            });
        }

        Test("空校准配置安全补齐且自动物理保持默认开启", () =>
        {
            var config = Load("{\"RigPipelineVersion\":5,\"Cal\":null}");
            config.Normalize();
            Require(config.Cal != null && config.AutoSkirtPhysics,
                "A recovered calibration must preserve default automatic physics.");
        });

        Test("高级物理参考路径与源 PMX 关联分开保存且空白恢复内置参考", () =>
        {
            var config = new Config
            {
                SkirtPhysicsPmxPath = "  E:\\Physics\\Skirt.pmx  ",
                SourcePmxByMotion = new(StringComparer.OrdinalIgnoreCase) { ["motion.vmd"] = "source.pmx" },
                Cal = new() { MotionScale = .75f, YawDegrees = 17, HeightOffset = .03f },
            };
            config.Normalize();
            var reloaded = Load(JsonSerializer.Serialize(config, JsonOptions));
            reloaded.Normalize();
            Require(reloaded.SkirtPhysicsPmxPath == @"E:\Physics\Skirt.pmx" && reloaded.SourcePmxByMotion["motion.vmd"] == "source.pmx",
                "The advanced physics reference must not replace a motion's source PMX association.");
            Require(reloaded.Cal.MotionScale == .75f && reloaded.Cal.YawDegrees == 17 && reloaded.Cal.HeightOffset == .03f,
                "Normalizing physics settings must not modify body calibration.");
            reloaded.SkirtPhysicsPmxPath = "  ";
            reloaded.Normalize();
            Require(reloaded.SkirtPhysicsPmxPath == null, "An empty advanced reference must select the built-in default.");
        });

        Test("离线裙骨关联保存往返后仍按 Windows 路径大小写恢复和清除", () =>
        {
            const string motion = @"D:\Dance\Bibbidiba.vmd";
            const string cache = @"E:\Bakes\Bibbidiba.ffskirt.json";
            var config = new Config();
            config.SkirtBakeByMotion[motion] = cache;
            var reloaded = Load(JsonSerializer.Serialize(config, JsonOptions));
            reloaded.Normalize();
            Require(reloaded.SkirtBakeByMotion.TryGetValue(motion.ToLowerInvariant(), out var restored) && restored == cache,
                "A saved cache association must restore for a differently cased motion path.");
            Require(reloaded.SkirtBakeByMotion.Remove(motion.ToUpperInvariant()) && reloaded.SkirtBakeByMotion.Count == 0,
                "Clearing an association must use the same case-insensitive path identity.");
            var cleared = Load(JsonSerializer.Serialize(reloaded, JsonOptions));
            cleared.Normalize();
            Require(cleared.SkirtBakeByMotion.Count == 0, "A cleared association must stay cleared after saving.");
        });

        Test("离线裙骨关联补齐 null 并幂等合并旧配置中的大小写重复", () =>
        {
            var empty = Load("{\"SkirtBakeByMotion\":null}");
            empty.Normalize();
            Require(empty.SkirtBakeByMotion.Count == 0, "A null association map must recover as empty.");
            var config = new Config
            {
                SkirtBakeByMotion = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [@"D:\Dance\motion.vmd"] = "first.ffskirt.json",
                    [@"d:\dance\MOTION.vmd"] = "last.ffskirt.json",
                },
            };
            var reloaded = Load(JsonSerializer.Serialize(config, JsonOptions));
            reloaded.Normalize();
            reloaded.Normalize();
            Require(reloaded.SkirtBakeByMotion.Count == 1 && reloaded.SkirtBakeByMotion[@"D:\DANCE\motion.vmd"] == "last.ffskirt.json",
                "Normalization must merge duplicate path identities without throwing or dropping the latest association.");
        });

        Console.WriteLine($"\n裙摆配置回归：{count - failed}/{count} 通过");
        return failed;
    }

    private static Config Load(string json)
        => JsonSerializer.Deserialize<Config>(json, JsonOptions)
           ?? throw new InvalidOperationException("Configuration deserialization returned null.");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
