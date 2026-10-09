using System.Security.Cryptography;
using System.Text.Json;
using FFMMD.Skirt;
using FFMMD.Vmd;

internal static class BuiltinSkirtBakeHarness
{
    public static int Run(string motionPath, string outputPath, string? libraryPath, int? maximumFrame = null, int? warmupFrames = null, int? iterations = null, int? substeps = null)
    {
        try
        {
            libraryPath ??= Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../native/SkirtBullet/artifacts/x64/FFMMD.Bullet.dll"));
            var bytes = File.ReadAllBytes(motionPath);
            var animation = VmdAnimation.Build(VmdFile.Parse(bytes));
            if (maximumFrame is { } lastFrame && lastFrame >= 0) animation.MaxFrame = Math.Min(animation.MaxFrame, (uint)lastFrame);
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            var last = Environment.TickCount64;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var document = new BuiltinSkirtBaker().Bake(animation, SkirtPhysicsReference.Builtin(), hash,
                SkirtBakeRecipe.Default with { WarmupFrames = warmupFrames ?? 60, Iterations = iterations ?? SkirtBakeRecipe.Default.Iterations,
                    Substeps = substeps ?? SkirtBakeRecipe.Default.Substeps }, progress =>
                {
                    if (Environment.TickCount64 - last < 5000 && progress.Phase != "Saving") return;
                    last = Environment.TickCount64; Console.WriteLine($"{progress.Fraction:P0} {progress.Phase}: {progress.Message}");
                }, CancellationToken.None, libraryPath);
            var output = SkirtBakeCache.Serialize(document);
            var checkedCache = SkirtBakeCache.Read(output, hash, document.ReferencePmxSha256);
            if (!checkedCache.Success) throw new InvalidDataException(checkedCache.Error);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            File.WriteAllBytes(outputPath, output);
            Console.WriteLine(JsonSerializer.Serialize(new { Motion = motionPath, Output = outputPath,
                Seconds = timer.Elapsed.TotalSeconds, Sha256 = Convert.ToHexString(SHA256.HashData(output)),
                document.FrameCount, Bones = document.Bones.Count, document.Solver }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
