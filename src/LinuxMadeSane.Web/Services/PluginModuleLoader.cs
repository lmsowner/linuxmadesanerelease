// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE.md for details.

using System.Reflection;
using System.Runtime.Loader;
using LinuxMadeSane.Application;

namespace LinuxMadeSane.Web.Services;

public static class PluginModuleLoader
{
    private const string PluginFolderName = "plugins";
    private static readonly object SyncRoot = new();
    private static IReadOnlyList<Assembly> loadedAssemblies = [];

    public static IReadOnlyList<Assembly> LoadedAssemblies
    {
        get
        {
            lock (SyncRoot)
            {
                return loadedAssemblies;
            }
        }
    }

    public static void ConfigureServices(WebApplicationBuilder builder)
    {
        var assemblies = LoadPluginAssemblies(builder.Environment.ContentRootPath);
        lock (SyncRoot)
        {
            loadedAssemblies = assemblies;
        }

        foreach (var module in DiscoverModules(assemblies))
        {
            module.ConfigureServices(
                builder.Services,
                builder.Configuration,
                builder.Environment.ContentRootPath);
        }
    }

    internal static IReadOnlyList<ILinuxMadeSanePluginModule> DiscoverModules(IEnumerable<Assembly> assemblies) =>
        assemblies
            .SelectMany(assembly => assembly
                .GetTypes()
                .Where(type =>
                    typeof(ILinuxMadeSanePluginModule).IsAssignableFrom(type) &&
                    type is { IsAbstract: false, IsInterface: false } &&
                    type.GetConstructor(Type.EmptyTypes) is not null)
                .Select(type => (ILinuxMadeSanePluginModule)Activator.CreateInstance(type)!))
            .ToArray();

    private static IReadOnlyList<Assembly> LoadPluginAssemblies(string contentRootPath)
    {
        var assemblies = new List<Assembly>();
        var loadedAssemblyNames = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetName().Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var path in GetPluginAssemblyPaths(contentRootPath))
        {
            var assemblyName = AssemblyName.GetAssemblyName(path);
            if (string.IsNullOrWhiteSpace(assemblyName.Name) ||
                loadedAssemblyNames.Contains(assemblyName.Name))
            {
                continue;
            }

            var loadContext = new PluginLoadContext(path);
            assemblies.Add(loadContext.LoadFromAssemblyPath(path));
            loadedAssemblyNames.Add(assemblyName.Name);
        }

        return assemblies;
    }

    private static IReadOnlyList<string> GetPluginAssemblyPaths(string contentRootPath)
    {
        var paths = new List<string>();

        foreach (var directory in GetPluginDirectories(contentRootPath))
        {
            foreach (var pluginDirectory in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly))
            {
                var pluginName = Path.GetFileName(pluginDirectory);
                var primaryAssemblyPath = Path.Combine(pluginDirectory, $"{pluginName}.dll");
                if (File.Exists(primaryAssemblyPath))
                {
                    paths.Add(primaryAssemblyPath);
                }
            }

            foreach (var legacyPath in Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly))
            {
                paths.Add(legacyPath);
            }
        }

        return paths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> GetPluginDirectories(string contentRootPath) =>
        new[]
        {
            Path.Combine(AppContext.BaseDirectory, PluginFolderName),
            Path.Combine(contentRootPath, PluginFolderName)
        }
        .Where(Directory.Exists)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private sealed class PluginLoadContext(string pluginAssemblyPath) : AssemblyLoadContext(isCollectible: false)
    {
        private readonly AssemblyDependencyResolver _resolver = new(pluginAssemblyPath);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var alreadyLoaded = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(assembly =>
                    string.Equals(assembly.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase));
            if (alreadyLoaded is not null)
            {
                return alreadyLoaded;
            }

            var assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
            return assemblyPath is not null
                ? LoadFromAssemblyPath(assemblyPath)
                : null;
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            var libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return libraryPath is not null
                ? LoadUnmanagedDllFromPath(libraryPath)
                : IntPtr.Zero;
        }
    }
}
