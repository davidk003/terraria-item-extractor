using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace StandaloneExtractor
{
    internal static class TerrariaDependencyResolver
    {
        private static readonly object SyncRoot = new object();
        private static bool _registered;
        private static string _terrariaExePath;
        private static string _terrariaDirectory;
        private static string _decompiledDirectory;
        private static string[] _runtimeResourceNames;
        private static readonly Dictionary<string, string[]> ReflectionResourceNamesByExePath = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        public static void EnsureRegistered(string terrariaExePath, string terrariaDirectory, string decompiledDirectory)
        {
            lock (SyncRoot)
            {
                _terrariaExePath = NormalizePath(terrariaExePath);
                _terrariaDirectory = terrariaDirectory;
                _decompiledDirectory = decompiledDirectory;
                _runtimeResourceNames = null;

                if (_registered)
                {
                    return;
                }

                AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
                _registered = true;
            }
        }

        public static bool HasEmbeddedDependency(string terrariaExePath, string assemblySimpleName)
        {
            if (string.IsNullOrWhiteSpace(assemblySimpleName))
            {
                return false;
            }

            string[] resourceNames = GetReflectionResourceNames(terrariaExePath);
            if (resourceNames.Length == 0)
            {
                return false;
            }

            string suffix = "." + assemblySimpleName + ".dll";
            return resourceNames.Any(name => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        }

        private static Assembly OnAssemblyResolve(object sender, ResolveEventArgs args)
        {
            string assemblySimpleName = new AssemblyName(args.Name).Name;
            if (string.IsNullOrWhiteSpace(assemblySimpleName))
            {
                return null;
            }

            Assembly alreadyLoaded = AppDomain.CurrentDomain
                .GetAssemblies()
                .FirstOrDefault(assembly => string.Equals(assembly.GetName().Name, assemblySimpleName, StringComparison.OrdinalIgnoreCase));
            if (alreadyLoaded != null)
            {
                return alreadyLoaded;
            }

            string terrariaDirectory;
            string decompiledDirectory;
            string terrariaExePath;
            lock (SyncRoot)
            {
                terrariaDirectory = _terrariaDirectory;
                decompiledDirectory = _decompiledDirectory;
                terrariaExePath = _terrariaExePath;
            }

            Assembly fromDirectory = TryLoadFromDirectory(assemblySimpleName, terrariaDirectory);
            if (fromDirectory != null)
            {
                return fromDirectory;
            }

            Assembly fromDecompiler = TryLoadFromDecompiledLibraries(assemblySimpleName, decompiledDirectory);
            if (fromDecompiler != null)
            {
                return fromDecompiler;
            }

            return TryLoadFromTerrariaEmbeddedLibraries(assemblySimpleName, terrariaExePath);
        }

        private static Assembly TryLoadFromDirectory(string assemblySimpleName, string directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return null;
            }

            string dllPath = Path.Combine(directory, assemblySimpleName + ".dll");
            if (File.Exists(dllPath))
            {
                return Assembly.LoadFrom(dllPath);
            }

            string exePath = Path.Combine(directory, assemblySimpleName + ".exe");
            if (File.Exists(exePath))
            {
                return Assembly.LoadFrom(exePath);
            }

            return null;
        }

        private static Assembly TryLoadFromDecompiledLibraries(string assemblySimpleName, string decompiledDirectory)
        {
            if (string.IsNullOrWhiteSpace(decompiledDirectory) || !Directory.Exists(decompiledDirectory))
            {
                return null;
            }

            foreach (string libraryPath in Directory.GetFiles(decompiledDirectory, "Terraria.Libraries.*." + assemblySimpleName + ".dll"))
            {
                return Assembly.LoadFrom(libraryPath);
            }

            return null;
        }

        private static Assembly TryLoadFromTerrariaEmbeddedLibraries(string assemblySimpleName, string terrariaExePath)
        {
            if (string.IsNullOrWhiteSpace(terrariaExePath) || !File.Exists(terrariaExePath))
            {
                return null;
            }

            Assembly terrariaAssembly = AppDomain.CurrentDomain
                .GetAssemblies()
                .FirstOrDefault(assembly => string.Equals(TryGetAssemblyLocation(assembly), terrariaExePath, StringComparison.OrdinalIgnoreCase));
            if (terrariaAssembly == null)
            {
                return null;
            }

            string[] resourceNames;
            lock (SyncRoot)
            {
                if (_runtimeResourceNames == null)
                {
                    _runtimeResourceNames = terrariaAssembly.GetManifestResourceNames();
                }

                resourceNames = _runtimeResourceNames;
            }

            string suffix = "." + assemblySimpleName + ".dll";
            string resourceName = resourceNames.FirstOrDefault(name => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(resourceName))
            {
                return null;
            }

            using (Stream resourceStream = terrariaAssembly.GetManifestResourceStream(resourceName))
            {
                if (resourceStream == null)
                {
                    return null;
                }

                using (var memory = new MemoryStream())
                {
                    resourceStream.CopyTo(memory);
                    return Assembly.Load(memory.ToArray());
                }
            }
        }

        private static string[] GetReflectionResourceNames(string terrariaExePath)
        {
            string normalizedPath = NormalizePath(terrariaExePath);
            if (string.IsNullOrWhiteSpace(normalizedPath) || !File.Exists(normalizedPath))
            {
                return Array.Empty<string>();
            }

            lock (SyncRoot)
            {
                if (ReflectionResourceNamesByExePath.TryGetValue(normalizedPath, out string[] cachedNames))
                {
                    return cachedNames;
                }
            }

            string[] names;
            try
            {
                Assembly reflectionAssembly = Assembly.ReflectionOnlyLoadFrom(normalizedPath);
                names = reflectionAssembly.GetManifestResourceNames();
            }
            catch
            {
                names = Array.Empty<string>();
            }

            lock (SyncRoot)
            {
                ReflectionResourceNamesByExePath[normalizedPath] = names;
            }

            return names;
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            try
            {
                return Path.GetFullPath(path);
            }
            catch
            {
                return path;
            }
        }

        private static string TryGetAssemblyLocation(Assembly assembly)
        {
            try
            {
                return NormalizePath(assembly.Location);
            }
            catch
            {
                return null;
            }
        }
    }
}
