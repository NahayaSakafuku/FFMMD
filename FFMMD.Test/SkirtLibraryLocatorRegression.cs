using System.Reflection;
using FFMMD.Skirt;

internal static class SkirtLibraryLocatorRegression
{
    public static int Run()
    {
        var failed = 0;
        var count = 0;
        void Test(string name, Action action)
        {
            count++;
            try { action(); Console.WriteLine($"  ✔ {name}"); }
            catch (Exception e) { failed++; Console.WriteLine($"  ✘ {name}: {e.Message}"); }
        }

        using var fixture = new Fixture();
        Test("切换到含诱饵DLL的VMD目录后仍定位宿主安装目录", () =>
        {
            fixture.InMotionDirectory(() =>
            {
                Require(File.Exists(SkirtLibraryLocator.FileName), "诱饵 DLL 未处于当前目录");
                RequireSamePath(SkirtLibraryLocator.FromPluginAssembly(fixture.PluginAssemblyPath), fixture.EnginePath);
                RequireSamePath(SkirtLibraryLocator.Resolve(fixture.EnginePath), fixture.EnginePath);
            });
        });
        Test("宿主程序集和显式DLL绝对路径规范化且不要求文件存在", () =>
        {
            var nonexistentDirectory = Path.Combine(fixture.Root, "not-installed", "child", "..");
            Require(!Directory.Exists(nonexistentDirectory), "无 I/O 场景意外存在目录");
            var assemblyPath = Path.Combine(nonexistentDirectory, "FFMMD.dll");
            var enginePath = Path.Combine(nonexistentDirectory, SkirtLibraryLocator.FileName);
            RequireSamePath(SkirtLibraryLocator.FromPluginAssembly(assemblyPath), Path.GetFullPath(enginePath));
            RequireSamePath(SkirtLibraryLocator.Resolve(enginePath), Path.GetFullPath(enginePath));
        });
        Test("正常文件加载的默认路径不受VMD当前目录影响", () =>
        {
            var expected = Path.Combine(Path.GetDirectoryName(typeof(SkirtBullet).Assembly.Location)!, SkirtLibraryLocator.FileName);
            fixture.InMotionDirectory(() => RequireSamePath(SkirtLibraryLocator.Resolve(), expected));
        });
        Test("拒绝空及相对宿主程序集地址而不借用当前目录", () =>
        {
            fixture.InMotionDirectory(() =>
            {
                foreach (var location in InvalidLocations())
                    Throws<InvalidOperationException>(() => SkirtLibraryLocator.FromPluginAssembly(location));
            });
        });
        Test("拒绝相对及驱动器相对DLL地址而不命中VMD诱饵", () =>
        {
            fixture.InMotionDirectory(() =>
            {
                foreach (var location in new[] { SkirtLibraryLocator.FileName, Path.Combine("subdirectory", SkirtLibraryLocator.FileName),
                    @"C:FFMMD.Bullet.dll", Path.DirectorySeparatorChar + SkirtLibraryLocator.FileName })
                    Throws<InvalidOperationException>(() => SkirtLibraryLocator.Resolve(location));
            });
        });
        Test("内存加载程序集Location为空时宿主位置仍可定位DLL", () =>
        {
            var memoryAssembly = LoadMemoryAssembly();
            Require(memoryAssembly.Location.Length == 0, "字节加载程序集意外拥有文件 Location");
            var fromHost = GetLocatorMethod(memoryAssembly, nameof(SkirtLibraryLocator.FromPluginAssembly));
            fixture.InMotionDirectory(() => RequireSamePath(
                (string)Invoke(fromHost, fixture.PluginAssemblyPath)!, fixture.EnginePath));
        });
        Test("内存加载程序集默认地址明确失败而不回退VMD目录", () =>
        {
            var memoryAssembly = LoadMemoryAssembly();
            Require(memoryAssembly.Location.Length == 0, "字节加载程序集意外拥有文件 Location");
            var resolve = GetLocatorMethod(memoryAssembly, nameof(SkirtLibraryLocator.Resolve));
            fixture.InMotionDirectory(() => Throws<InvalidOperationException>(() => Invoke(resolve, null)));
        });
        if (OperatingSystem.IsWindows())
        {
            Test("宿主UNC安装路径保持为绝对网络路径", () =>
            {
                const string assembly = @"\\server\plugins\FFMMD\FFMMD.dll";
                const string engine = @"\\server\plugins\FFMMD\FFMMD.Bullet.dll";
                fixture.InMotionDirectory(() => RequireSamePath(SkirtLibraryLocator.FromPluginAssembly(assembly), engine));
            });
        }
        Console.WriteLine($"\n裙骨物理引擎路径回归：{count - failed}/{count} 通过");
        return failed;
    }

    private static IEnumerable<string> InvalidLocations()
    {
        yield return "";
        yield return " ";
        yield return "FFMMD.dll";
        yield return Path.Combine("installedPlugins", "FFMMD.dll");
        yield return @"C:FFMMD.dll";
        yield return Path.DirectorySeparatorChar + "FFMMD.dll";
    }

    private static Assembly LoadMemoryAssembly() =>
        Assembly.Load(File.ReadAllBytes(typeof(SkirtLibraryLocator).Assembly.Location));

    private static MethodInfo GetLocatorMethod(Assembly assembly, string name) =>
        assembly.GetType(typeof(SkirtLibraryLocator).FullName!, throwOnError: true)!
            .GetMethod(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException($"内存程序集缺少路径定位方法 {name}");

    private static object? Invoke(MethodInfo method, string? argument)
    {
        try { return method.Invoke(null, [argument]); }
        catch (TargetInvocationException e) when (e.InnerException != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }
    }

    private static void RequireSamePath(string actual, string expected)
    {
        Require(Path.IsPathFullyQualified(actual), $"定位结果不是绝对路径：{actual}");
        Require(string.Equals(actual, Path.GetFullPath(expected), OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal), $"路径错误：{actual}，期望 {expected}");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"期望 {typeof(T).Name}，但地址被接受");
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "FFMMD-locator-" + Guid.NewGuid().ToString("N"));
        public string PluginAssemblyPath { get; }
        public string EnginePath { get; }
        private string MotionDirectory { get; }

        public Fixture()
        {
            var installationDirectory = Path.Combine(Root, "installedPlugins", "FFMMD", "1.0.1.0");
            Directory.CreateDirectory(installationDirectory);
            MotionDirectory = Path.Combine(Root, "bibbidiba_FullMotion");
            Directory.CreateDirectory(MotionDirectory);
            PluginAssemblyPath = Path.Combine(installationDirectory, "FFMMD.dll");
            EnginePath = Path.Combine(installationDirectory, SkirtLibraryLocator.FileName);
            File.WriteAllText(EnginePath, "installed-engine");
            File.WriteAllText(Path.Combine(MotionDirectory, SkirtLibraryLocator.FileName), "vmd-directory-decoy");
        }

        public void InMotionDirectory(Action action)
        {
            var previous = Directory.GetCurrentDirectory();
            try { Directory.SetCurrentDirectory(MotionDirectory); action(); }
            finally { Directory.SetCurrentDirectory(previous); }
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
