using System.Security.Cryptography;
using FFMMD.Skirt;

internal static class SkirtPreprocessorRegression
{
    public static int Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task<int> RunAsync()
    {
        var failed = 0; var count = 0;
        async Task Test(string name, Func<Task> action)
        {
            count++;
            try { await action(); Console.WriteLine($"  ✔ {name}"); }
            catch (Exception e) { failed++; Console.WriteLine($"  ✘ {name}: {e.Message}"); }
        }
        await Test("相同动作去重且单个lease取消不影响其他订阅", async () =>
        {
            using var f = new Fixture(); var worker = new FakeWorker(); using var service = new SkirtPreprocessor(f.Output, worker);
            using var a = service.Request(f.Request()); using var b = service.Request(f.Request("copy.vmd"));
            await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); await WaitStatus(b, "Baking"); a.Cancel(); worker.Release.TrySetResult();
            var ready = await b.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Require(ready.Status == "Ready" && worker.Calls == 1 && a.Snapshot.Status == "Cancelled", "dedup/cancel failed");
        });
        await Test("磁盘缓存命中且recipe变化生成新内容key", async () =>
        {
            using var f = new Fixture(); var firstWorker = new FakeWorker(true); string key;
            using (var service = new SkirtPreprocessor(f.Output, firstWorker)) using (var lease = service.Request(f.Request()))
            { var result = await lease.Completion.WaitAsync(TimeSpan.FromSeconds(5)); Require(result.Status == "Ready", result.Message ?? "bake failed"); key = result.ContentKey!; }
            var secondWorker = new FakeWorker(true); using var second = new SkirtPreprocessor(f.Output, secondWorker);
            using var hit = second.Request(f.Request()); var cached = await hit.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Require(cached.Status == "Ready" && secondWorker.Calls == 0 && cached.ContentKey == key, "cache miss");
            using var changed = second.Request(f.Request() with { Recipe = SkirtBakeRecipe.Default with { Substeps = 121 } });
            var baked = await changed.Completion.WaitAsync(TimeSpan.FromSeconds(5)); Require(baked.Status == "Ready" && secondWorker.Calls == 1 && baked.ContentKey != key, "recipe key miss");
        });
        await Test("碰撞距离比例默认1.5、接受边界并参与缓存身份", async () =>
        {
            Require(SkirtBakeRecipe.Default.NonCollisionDistanceScale == 1.5f, "distance default changed");
            using var f = new Fixture(); var worker = new FakeWorker(true); using var service = new SkirtPreprocessor(f.Output, worker);
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var distance in new[] { 1.5f, 0f, 100f })
            {
                using var lease = service.Request(f.Request() with { Recipe = SkirtBakeRecipe.Default with { NonCollisionDistanceScale = distance } });
                var result = await lease.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                Require(result.Status == "Ready" && result.Cache?.Document.Solver.NonCollisionDistanceScale == distance,
                    result.Message ?? "distance recipe did not reach the worker cache");
                Require(keys.Add(result.ContentKey!), "different distance reused the same identity");
            }
            Require(worker.Calls == 3, "distance recipes were incorrectly deduplicated");
        });
        await Test("失败任务允许相同内容重试", async () =>
        {
            using var f = new Fixture(); var worker = new FakeWorker(true, failFirst: true); using var service = new SkirtPreprocessor(f.Output, worker);
            using var first = service.Request(f.Request()); Require((await first.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status == "Failed", "first did not fail");
            using var retry = service.Request(f.Request()); Require((await retry.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status == "Ready" && worker.Calls == 2, "retry was deduplicated away");
        });
        await Test("动作字节快照与hash校验", async () =>
        {
            using var f = new Fixture(); var worker = new FakeWorker(true); using var service = new SkirtPreprocessor(f.Output, worker);
            var bytes = f.MotionBytes; var hash = Convert.ToHexString(SHA256.HashData(bytes));
            using var lease = service.Request(f.Request() with { MotionBytes = bytes, ExpectedMotionSha256 = hash });
            bytes[0] ^= 1; var result = await lease.Completion.WaitAsync(TimeSpan.FromSeconds(5)); Require(result.Status == "Ready", result.Message ?? "snapshot failed");
        });
        await Test("最后订阅取消不会发布迟到缓存，Dispose不阻塞", async () =>
        {
            using var f = new Fixture(); var worker = new FakeWorker(ignoreCancellation: true); var service = new SkirtPreprocessor(f.Output, worker); using var lease = service.Request(f.Request());
            await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); var timer = System.Diagnostics.Stopwatch.StartNew(); service.Dispose(); timer.Stop();
            Require(timer.ElapsedMilliseconds < 500 && lease.Snapshot.Status == "Cancelled", "dispose blocked"); worker.Release.TrySetResult();
            await worker.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5)); await WaitUntil(() => !Directory.GetFiles(f.Output, "*.pending").Any());
            Require(lease.Snapshot.Status == "Cancelled" && lease.Snapshot.Cache == null && !Directory.GetFiles(f.Output, "*.ffskirt.json").Any(), "late cache committed");
        });
        await Test("不同动作严格串行排队且最后订阅取消后推进队列", async () =>
        {
            using var f = new Fixture(); var worker = new FakeWorker(); using var service = new SkirtPreprocessor(f.Output, worker);
            using var first = service.Request(f.Request()); await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var second = service.Request(f.OtherRequest()); await WaitStatus(second, "Queued");
            Require(worker.Calls == 1, "concurrent jobs started"); first.Cancel();
            await worker.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5)); worker.Release.TrySetResult();
            var result = await second.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Require(result.Status == "Ready" && worker.Calls == 2 && worker.MaximumConcurrent == 1, "queue did not advance serially");
        });
        await Test("动态库内容改变生成新key", async () =>
        {
            using var f = new Fixture(); var worker = new FakeWorker(true); using var service = new SkirtPreprocessor(f.Output, worker);
            using var first = service.Request(f.Request()); var before = await first.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            File.AppendAllText(f.EnginePath, "changed");
            using var second = service.Request(f.Request()); var after = await second.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Require(before.Status == "Ready" && after.Status == "Ready" && before.ContentKey != after.ContentKey && worker.Calls == 2, "engine identity omitted");
        });
        await Test("烘焙期间动态库变化拒绝提交缓存", async () =>
        {
            using var f = new Fixture(); var worker = new FakeWorker(); using var service = new SkirtPreprocessor(f.Output, worker);
            using var lease = service.Request(f.Request()); await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            File.AppendAllText(f.EnginePath, "changed while baking"); worker.Release.TrySetResult();
            var result = await lease.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Require(result.Status == "Failed" && result.Cache == null && !Directory.GetFiles(f.Output, "*.ffskirt.json").Any(), "changed engine committed cache");
        });
        await Test("错误动作hash和frame范围先于worker拒绝", async () =>
        {
            using var f = new Fixture(); var worker = new FakeWorker(true); using var service = new SkirtPreprocessor(f.Output, worker);
            foreach (var request in new[] { f.Request() with { ExpectedMotionSha256 = new string('A', 64) },
                f.Request() with { ExpectedMotionSha256 = "invalid" }, f.Request() with { FrameCount = 4 }, f.Request() with { MotionBytes = [1, 2, 3], ExpectedMotionSha256 = null } })
            {
                using var lease = service.Request(request); var result = await lease.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                Require(result.Status == "Failed" && result.Cache == null, "bad request was accepted");
            }
            Require(worker.Calls == 0, "invalid requests reached worker");
        });
        await Test("错误输出动作身份、frame、engine和旧recipe拒绝发布", async () =>
        {
            foreach (var corrupt in new Action<SkirtBakeCache.Document>[]
            {
                d => d.MotionSha256 = new string('A', 64), d => d.FrameCount = 4,
                d => d.Solver.Engine = "Blender Bullet", d => d.Solver.RecipeVersion = 2, d => d.Solver.NonCollisionDistanceScale = 0,
                d => d.Solver.KinematicDrivingAlgorithm = null,
            })
            {
                using var f = new Fixture(); var worker = new FakeWorker(true, corrupt: corrupt); using var service = new SkirtPreprocessor(f.Output, worker);
                using var lease = service.Request(f.Request()); var result = await lease.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                Require(result.Status == "Failed" && result.Cache == null && result.LogTail == "fake worker tail", "wrong worker output became ready");
                Require(!Directory.GetFiles(f.Output, "*.ffskirt.json").Any(), "invalid cache committed");
            }
        });
        await Test("缺少动作、参考模型或内置库明确报告", async () =>
        {
            using var f = new Fixture(); var worker = new FakeWorker(true); using var service = new SkirtPreprocessor(f.Output, worker);
            foreach (var (request, expected) in new[]
            {
                (f.Request() with { MotionBytes = null, MotionPath = Path.Combine(f.Root, "missing.vmd") }, "MissingMotion"),
                (f.Request() with { ReferencePmxPath = Path.Combine(f.Root, "missing.pmx") }, "MissingModel"),
                (f.Request() with { LibraryPath = Path.Combine(f.Root, "missing.dll") }, "MissingTool"),
            })
            {
                using var lease = service.Request(request); Require((await lease.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status == expected, "missing input status incorrect");
            }
            Require(worker.Calls == 0, "missing input reached worker");
        });
        await Test("切换VMD目录后预处理hash和worker仍使用安装DLL", async () =>
        {
            using var f = new Fixture();
            var engine = SkirtLibraryLocator.FromPluginAssembly(Path.Combine(f.Root, "FFMMD.dll"));
            File.Copy(f.EnginePath, engine);
            var motionDirectory = Path.Combine(f.Root, "vmd-directory"); Directory.CreateDirectory(motionDirectory);
            File.WriteAllBytes(Path.Combine(motionDirectory, SkirtLibraryLocator.FileName), [9, 8, 7]);
            var expectedHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(engine)));
            var worker = new FakeWorker(true); using var service = new SkirtPreprocessor(f.Output, worker);
            var previousDirectory = Directory.GetCurrentDirectory();
            try
            {
                Directory.SetCurrentDirectory(motionDirectory);
                using var lease = service.Request(f.Request() with { LibraryPath = engine });
                var result = await lease.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                Require(result.Status == "Ready", result.Message ?? "installed engine request failed");
                Require(worker.LastWork?.Request.LibraryPath == engine && worker.LastWork.EngineSha256 == expectedHash,
                    "preprocessor hashed or forwarded the VMD-directory decoy");
            }
            finally { Directory.SetCurrentDirectory(previousDirectory); }
        });
        await Test("预处理拒绝相对DLL地址且不会调用worker", async () =>
        {
            using var f = new Fixture(); var worker = new FakeWorker(true); using var service = new SkirtPreprocessor(f.Output, worker);
            using var lease = service.Request(f.Request() with { LibraryPath = SkirtLibraryLocator.FileName });
            var result = await lease.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Require(result.Status == "Failed" && result.Message?.Contains("absolute", StringComparison.Ordinal) == true && worker.Calls == 0,
                "relative engine path was not rejected before worker entry");
        });
        await Test("非有限及不支持recipe拒绝且不会进入worker", async () =>
        {
            using var f = new Fixture(); var worker = new FakeWorker(true); using var service = new SkirtPreprocessor(f.Output, worker);
            foreach (var recipe in new[]
            {
                SkirtBakeRecipe.Default with { Version = 2 }, SkirtBakeRecipe.Default with { Scale = float.NaN },
                SkirtBakeRecipe.Default with { SmoothingWindowFrames = 9 }, SkirtBakeRecipe.Default with { SmoothingMaximumCorrectionDegrees = 16 },
                SkirtBakeRecipe.Default with { NonCollisionDistanceScale = -1 }, SkirtBakeRecipe.Default with { NonCollisionDistanceScale = 101 },
                SkirtBakeRecipe.Default with { NonCollisionDistanceScale = float.NaN }, SkirtBakeRecipe.Default with { NonCollisionDistanceScale = float.PositiveInfinity },
                SkirtBakeRecipe.Default with { Iterations = 0 },
            })
            {
                using var lease = service.Request(f.Request() with { Recipe = recipe });
                Require((await lease.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Status == "Failed", "unsupported recipe accepted");
            }
            Require(worker.Calls == 0, "bad recipe reached worker");
        });
        Console.WriteLine($"\n裙摆自动预处理回归：{count - failed}/{count} 通过"); return failed;
    }

    private static async Task WaitStatus(SkirtBakeLease lease, string status)
    {
        await WaitUntil(() =>
        {
            if (lease.Snapshot.Status == status) return true;
            if (lease.Snapshot.IsCompleted) throw new InvalidOperationException($"Expected {status}, got {lease.Snapshot.Status}: {lease.Snapshot.Message}");
            return false;
        });
    }
    private static async Task WaitUntil(Func<bool> done)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!done()) await Task.Delay(10, timeout.Token);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root = Path.Combine(Path.GetTempPath(), "ffmmd-pre-" + Guid.NewGuid().ToString("N"));
        public string Output => Path.Combine(Root, "out");
        public string EnginePath => Path.Combine(Root, "engine.dll");
        public byte[] MotionBytes { get; }
        public Fixture()
        {
            Directory.CreateDirectory(Output); MotionBytes = MakeVmd();
            File.WriteAllBytes(Path.Combine(Root, "motion.vmd"), MotionBytes); File.WriteAllBytes(Path.Combine(Root, "copy.vmd"), MotionBytes);
            File.WriteAllBytes(EnginePath, [1, 2, 3, 4]);
        }
        public SkirtBakeRequest Request(string name = "motion.vmd") => new()
        {
            MotionPath = Path.Combine(Root, name), MotionBytes = MotionBytes.ToArray(), FrameCount = 3,
            LibraryPath = EnginePath, ExpectedMotionSha256 = Convert.ToHexString(SHA256.HashData(MotionBytes)),
        };
        public SkirtBakeRequest OtherRequest()
        {
            var bytes = MakeVmd(.5f);
            return Request() with { MotionBytes = bytes, ExpectedMotionSha256 = Convert.ToHexString(SHA256.HashData(bytes)) };
        }
        public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
        private static byte[] MakeVmd(float translation = 0)
        {
            using var ms = new MemoryStream(); using var w = new BinaryWriter(ms);
            WriteFixed(w, "Vocaloid Motion Data 0002", 30); WriteFixed(w, "test", 20); w.Write(3u);
            for (uint frame = 0; frame < 3; frame++) { WriteFixed(w, "center", 15); w.Write(frame); w.Write(translation); w.Write(0f); w.Write(0f); w.Write(0f); w.Write(0f); w.Write(0f); w.Write(1f); w.Write(new byte[64]); }
            return ms.ToArray();
        }
        private static void WriteFixed(BinaryWriter w, string value, int bytes) { var data = System.Text.Encoding.ASCII.GetBytes(value); w.Write(data.Take(bytes).Concat(new byte[Math.Max(0, bytes - data.Length)]).ToArray()); }
    }

    private sealed class FakeWorker(bool autoRelease = false, bool failFirst = false, bool ignoreCancellation = false,
        Action<SkirtBakeCache.Document>? corrupt = null) : ISkirtBakeWorker
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public int MaximumConcurrent;
        public SkirtBakeWork? LastWork;
        private int _concurrent;
        public async Task<SkirtBakeWorkerResult> RunAsync(SkirtBakeWork work, Action<SkirtBakeProgress> progress, CancellationToken token)
        {
            LastWork = work;
            var call = Interlocked.Increment(ref Calls); MaximumConcurrent = Math.Max(MaximumConcurrent, Interlocked.Increment(ref _concurrent));
            Started.TrySetResult(); progress(new("fake", .4f));
            try
            {
                if (!autoRelease) await Release.Task.WaitAsync(ignoreCancellation ? CancellationToken.None : token);
                if (failFirst && call == 1) return new(1, "fake failure");
                var document = Cache(work); corrupt?.Invoke(document);
                File.WriteAllBytes(work.OutputPath, SkirtBakeCache.Serialize(document));
                return new(0, "fake worker tail");
            }
            catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
            finally { Interlocked.Decrement(ref _concurrent); Finished.TrySetResult(); }
        }
        private static SkirtBakeCache.Document Cache(SkirtBakeWork work)
        {
            var r = work.Request.Recipe; var d = new SkirtBakeCache.Document { MotionSha256 = work.MotionSha256, ReferencePmxSha256 = work.ReferencePmxSha256, FrameCount = work.Request.FrameCount, Solver = new()
            {
                Engine = BuiltinSkirtBaker.Engine, EngineVersion = "fake", RecipeVersion = r.Version, Substeps = r.Substeps, Iterations = r.Iterations, WarmupFrames = r.WarmupFrames,
                Scale = r.Scale, PointCacheBaked = true, CollisionMargin = r.CollisionMargin, NonCollisionDistanceScale = r.NonCollisionDistanceScale,
                KinematicDrivingAlgorithm = KinematicColliderDriver.Algorithm,
                Smoothing = new() { Algorithm = "BoundedBilateralQuaternion", WindowFrames = r.SmoothingWindowFrames, MaximumCorrectionDegrees = r.SmoothingMaximumCorrectionDegrees, Strength = r.SmoothingStrength }
            }};
            foreach (var target in SkirtBakeCache.RequiredTargetNames)
            {
                var p = target.Split('_'); var root = p[3] == "a"; p[3] = p[3] == "b" ? "a" : "b";
                d.Bones.Add(new() { SourceName = "src_" + target, TargetName = target, ParentSourceName = root ? "root" : "src_" + string.Join('_', p), RelativeRotations = Enumerable.Range(0, d.FrameCount).Select(_ => new[] { 0f, 0f, 0f, 1f }).ToArray() });
            }
            return d;
        }
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
