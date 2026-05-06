using System.Reflection;

namespace LinuxMadeSane.Core.Versioning;

public static class LinuxMadeSaneBuildVersion
{
    public static string GetCurrent(Assembly? preferredAssembly = null, Assembly? fallbackAssembly = null) =>
        Resolve(preferredAssembly) ??
        Resolve(fallbackAssembly) ??
        "dev";

    private static string? Resolve(Assembly? assembly)
    {
        if (assembly is null)
        {
            return null;
        }

        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            return informationalVersion.Split('+', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
        }

        var fileVersion = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
        if (!string.IsNullOrWhiteSpace(fileVersion))
        {
            return fileVersion.Trim();
        }

        return assembly.GetName().Version?.ToString();
    }
}
