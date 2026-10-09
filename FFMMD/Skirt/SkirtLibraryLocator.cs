namespace FFMMD.Skirt;

/// <summary>Resolves the bundled engine without depending on the process working directory.</summary>
internal static class SkirtLibraryLocator
{
    internal const string FileName = "FFMMD.Bullet.dll";

    internal static string FromPluginAssembly(string assemblyLocation)
    {
        // Dalamud can load managed assemblies from bytes, leaving Assembly.Location
        // empty. The plugin must supply IDalamudPluginInterface.AssemblyLocation.
        if (string.IsNullOrWhiteSpace(assemblyLocation) || !Path.IsPathFullyQualified(assemblyLocation))
            throw new InvalidOperationException("The bundled Bullet engine requires an absolute plugin assembly location from the host.");
        return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(assemblyLocation))!, FileName);
    }

    internal static string Resolve(string? libraryPath = null)
    {
        if (string.IsNullOrWhiteSpace(libraryPath))
            return FromPluginAssembly(typeof(SkirtBullet).Assembly.Location);
        if (!Path.IsPathFullyQualified(libraryPath))
            throw new InvalidOperationException("The bundled Bullet library path must be absolute.");
        return Path.GetFullPath(libraryPath);
    }
}
