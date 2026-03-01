using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using StandaloneExtractor.Models;

namespace StandaloneExtractor.Extractors
{
    public sealed class ItemDataExtractor : IExtractorPhase<ItemRow>
    {
        private const string DefaultTerrariaDirectory = @"C:\Program Files (x86)\Steam\steamapps\common\Terraria";

        public string PhaseName
        {
            get { return "item-pricing"; }
        }

        public IEnumerable<ItemRow> Extract(ExtractionContext context)
        {
            string terrariaExePath = ResolveTerrariaExecutablePath(context);
            if (!File.Exists(terrariaExePath))
            {
                throw new FileNotFoundException("Terraria executable was not found for item extraction.", terrariaExePath);
            }

            string decompiledDirectory = FindRepositoryDecompiledDirectory();
            string loadableTerrariaExePath = EnsureTerrariaRuntimeDependencies(terrariaExePath, decompiledDirectory);
            TerrariaDependencyResolver.EnsureRegistered(
                loadableTerrariaExePath,
                Path.GetDirectoryName(loadableTerrariaExePath),
                decompiledDirectory);
            Assembly terrariaAssembly = Assembly.LoadFrom(loadableTerrariaExePath);
            Type itemType = terrariaAssembly.GetType("Terraria.Item", throwOnError: true);
            Type itemIdType = terrariaAssembly.GetType("Terraria.ID.ItemID", throwOnError: true);

            PrepareTerrariaProgramState(terrariaAssembly, context);

            int itemCount = GetItemCount(itemIdType);
            var rows = new List<ItemRow>(Math.Max(0, itemCount - 1));

            object item = Activator.CreateInstance(itemType);
            MethodInfo setDefaultsMethod = itemType.GetMethod("SetDefaults", new[] { typeof(int) });
            FieldInfo valueField = itemType.GetField("value", BindingFlags.Public | BindingFlags.Instance);
            PropertyInfo nameProperty = itemType.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
            Func<int, string> getInternalName = BuildInternalNameResolver(itemIdType);

            if (setDefaultsMethod == null || valueField == null || nameProperty == null)
            {
                throw new InvalidOperationException("Failed to resolve required Terraria item members for extraction.");
            }

            for (int id = 1; id < itemCount; id++)
            {
                try
                {
                    setDefaultsMethod.Invoke(item, new object[] { id });
                }
                catch (TargetInvocationException ex)
                {
                    throw new InvalidOperationException("Item.SetDefaults failed for item id " + id.ToString(CultureInfo.InvariantCulture) + ".", ex.InnerException ?? ex);
                }

                int value = Convert.ToInt32(valueField.GetValue(item), CultureInfo.InvariantCulture);
                string internalName = getInternalName(id);
                string displayName = Convert.ToString(nameProperty.GetValue(item, null), CultureInfo.InvariantCulture);

                if (string.IsNullOrWhiteSpace(displayName) || displayName.StartsWith("ItemName.", StringComparison.Ordinal))
                {
                    displayName = internalName;
                }

                rows.Add(new ItemRow
                {
                    Id = id,
                    Name = displayName ?? string.Empty,
                    InternalName = internalName ?? string.Empty,
                    Value = value,
                    SellPrice = value / 5
                });
            }

            Console.WriteLine("[ItemDataExtractor] Extracted " + rows.Count + " items (id range: 1-" + (itemCount - 1).ToString(CultureInfo.InvariantCulture) + ").");
            return rows;
        }

        private static string EnsureTerrariaRuntimeDependencies(string terrariaExePath, string decompiledDirectory)
        {
            string terrariaDirectory = Path.GetDirectoryName(terrariaExePath);
            string appBaseDirectory = AppDomain.CurrentDomain.BaseDirectory;

            foreach (string sourceDllPath in Directory.GetFiles(terrariaDirectory, "*.dll"))
            {
                string destinationDllPath = Path.Combine(appBaseDirectory, Path.GetFileName(sourceDllPath));
                if (!File.Exists(destinationDllPath))
                {
                    File.Copy(sourceDllPath, destinationDllPath);
                }
            }

            string destinationTerrariaExePath = Path.Combine(appBaseDirectory, "Terraria.exe");
            if (!File.Exists(destinationTerrariaExePath))
            {
                File.Copy(terrariaExePath, destinationTerrariaExePath);
            }

            if (!string.IsNullOrWhiteSpace(decompiledDirectory) && Directory.Exists(decompiledDirectory))
            {
                foreach (string sourceLibraryPath in Directory.GetFiles(decompiledDirectory, "Terraria.Libraries.*.dll"))
                {
                    try
                    {
                        AssemblyName assemblyName = AssemblyName.GetAssemblyName(sourceLibraryPath);
                        if (string.IsNullOrWhiteSpace(assemblyName.Name))
                        {
                            continue;
                        }

                        string destinationLibraryPath = Path.Combine(appBaseDirectory, assemblyName.Name + ".dll");
                        if (!File.Exists(destinationLibraryPath))
                        {
                            File.Copy(sourceLibraryPath, destinationLibraryPath);
                        }
                    }
                    catch
                    {
                    }
                }
            }

            return destinationTerrariaExePath;
        }

        private static string FindRepositoryDecompiledDirectory()
        {
            var current = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (current != null)
            {
                string candidatePath = Path.Combine(current.FullName, "decompiled");
                string relogicLibraryPath = Path.Combine(candidatePath, "Terraria.Libraries.ReLogic.ReLogic.dll");
                if (File.Exists(relogicLibraryPath))
                {
                    return candidatePath;
                }

                current = current.Parent;
            }

            return null;
        }

        private static string ResolveTerrariaExecutablePath(ExtractionContext context)
        {
            string[] args = context == null ? new string[0] : context.CommandLineArgs;
            for (int i = 0; i < args.Length; i++)
            {
                if (!string.Equals(args[i], "--terraria-dir", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (i + 1 < args.Length && !string.IsNullOrWhiteSpace(args[i + 1]))
                {
                    return Path.Combine(args[i + 1], "Terraria.exe");
                }
            }

            return Path.Combine(DefaultTerrariaDirectory, "Terraria.exe");
        }

        private static int GetItemCount(Type itemIdType)
        {
            FieldInfo countField = itemIdType.GetField("Count", BindingFlags.Public | BindingFlags.Static);
            if (countField == null)
            {
                throw new InvalidOperationException("Terraria.ID.ItemID.Count was not found.");
            }

            return Convert.ToInt32(countField.GetValue(null), CultureInfo.InvariantCulture);
        }

        private static Func<int, string> BuildInternalNameResolver(Type itemIdType)
        {
            FieldInfo searchField = itemIdType.GetField("Search", BindingFlags.Public | BindingFlags.Static);
            object search = searchField == null ? null : searchField.GetValue(null);
            if (search == null)
            {
                return id => string.Empty;
            }

            MethodInfo getNameMethod = search.GetType()
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(method => method.Name == "GetName" && method.GetParameters().Length == 1);

            if (getNameMethod == null)
            {
                return id => string.Empty;
            }

            ParameterInfo parameter = getNameMethod.GetParameters()[0];
            return id =>
            {
                object typedId = Convert.ChangeType(id, parameter.ParameterType, CultureInfo.InvariantCulture);
                object value = getNameMethod.Invoke(search, new[] { typedId });
                return Convert.ToString(value, CultureInfo.InvariantCulture);
            };
        }

        private static void PrepareTerrariaProgramState(Assembly terrariaAssembly, ExtractionContext context)
        {
            Type programType = terrariaAssembly.GetType("Terraria.Program", throwOnError: true);
            FieldInfo savePathField = programType.GetField("SavePath", BindingFlags.Public | BindingFlags.Static);
            FieldInfo launchParametersField = programType.GetField("LaunchParameters", BindingFlags.Public | BindingFlags.Static);

            if (savePathField != null)
            {
                string savePath = Path.Combine(context.OutputDirectory, "_runtime", "TerrariaSave");
                Directory.CreateDirectory(savePath);
                savePathField.SetValue(null, savePath);
            }

            if (launchParametersField != null)
            {
                launchParametersField.SetValue(null, new Dictionary<string, string>());
            }

            PrepareTerrariaMainState(terrariaAssembly);
        }

        private static void PrepareTerrariaMainState(Assembly terrariaAssembly)
        {
            Type mainType = terrariaAssembly.GetType("Terraria.Main", throwOnError: true);
            Type playerType = terrariaAssembly.GetType("Terraria.Player", throwOnError: true);

            FieldInfo myPlayerField = mainType.GetField("myPlayer", BindingFlags.Public | BindingFlags.Static);
            if (myPlayerField != null)
            {
                myPlayerField.SetValue(null, 0);
            }

            FieldInfo playerField = mainType.GetField("player", BindingFlags.Public | BindingFlags.Static);
            if (playerField != null)
            {
                Array players = playerField.GetValue(null) as Array;
                if (players == null || players.Length < 255)
                {
                    players = Array.CreateInstance(playerType, 255);
                    for (int i = 0; i < players.Length; i++)
                    {
                        players.SetValue(Activator.CreateInstance(playerType), i);
                    }

                    playerField.SetValue(null, players);
                }
                else if (players.GetValue(0) == null)
                {
                    players.SetValue(Activator.CreateInstance(playerType), 0);
                }
            }
        }

    }
}
