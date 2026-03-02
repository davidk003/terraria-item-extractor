using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace StandaloneExtractor
{
    // === Reflection Helpers ===

    public static class ReflectionHelpers
    {
        public static Type GetRequiredType(Assembly assembly, string typeName)
        {
            Type type = assembly.GetType(typeName, throwOnError: false);
            if (type == null)
            {
                throw new InvalidOperationException("Missing type in Terraria assembly: " + typeName);
            }

            return type;
        }

        public static MethodInfo GetRequiredMethod(Type type, string name, BindingFlags flags, Type[] paramTypes = null)
        {
            MethodInfo method = paramTypes != null
                ? type.GetMethod(name, flags, null, paramTypes, null)
                : type.GetMethod(name, flags);

            if (method == null)
            {
                throw new InvalidOperationException("Missing method: " + type.FullName + "." + name);
            }

            return method;
        }

        public static FieldInfo GetRequiredField(Type type, string name, BindingFlags flags)
        {
            FieldInfo field = type.GetField(name, flags);
            if (field == null)
            {
                throw new InvalidOperationException("Missing field: " + type.FullName + "." + name);
            }

            return field;
        }

        public static object TryInvokeStatic(Type type, string methodName)
        {
            MethodInfo method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (method != null)
            {
                return method.Invoke(null, null);
            }

            return null;
        }

        public static void TryInvokeStaticIgnoringErrors(Type type, string methodName)
        {
            MethodInfo method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (method == null)
            {
                return;
            }

            try
            {
                method.Invoke(null, null);
            }
            catch
            {
            }
        }

        public static T GetStaticFieldValue<T>(Type type, string fieldName)
        {
            FieldInfo field = type.GetField(fieldName, BindingFlags.Public | BindingFlags.Static);
            if (field == null)
            {
                throw new MissingFieldException(type.FullName, fieldName);
            }

            return (T)field.GetValue(null);
        }

        public static T GetInstanceFieldValue<T>(object instance, string fieldName)
        {
            FieldInfo field = instance.GetType().GetField(fieldName, BindingFlags.Public | BindingFlags.Instance);
            if (field == null)
            {
                throw new MissingFieldException(instance.GetType().FullName, fieldName);
            }

            return (T)field.GetValue(instance);
        }
    }

    // === Terraria Path Resolver ===

    public static class TerrariaPathResolver
    {
        public const string DefaultTerrariaExePath =
            @"C:\Program Files (x86)\Steam\steamapps\common\Terraria\Terraria.exe";

        public static string ResolveTerrariaExePath(IList<string> args)
        {
            if (args != null)
            {
                for (int i = 0; i < args.Count; i++)
                {
                    if ((string.Equals(args[i], "--terraria", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(args[i], "-t", StringComparison.OrdinalIgnoreCase))
                        && i + 1 < args.Count
                        && !string.IsNullOrWhiteSpace(args[i + 1]))
                    {
                        return Path.GetFullPath(args[i + 1]);
                    }

                    if (string.Equals(args[i], "--terraria-dir", StringComparison.OrdinalIgnoreCase)
                        && i + 1 < args.Count
                        && !string.IsNullOrWhiteSpace(args[i + 1]))
                    {
                        return Path.Combine(Path.GetFullPath(args[i + 1]), "Terraria.exe");
                    }
                }
            }

            return DefaultTerrariaExePath;
        }

        public static string FindRepositoryDecompiledDirectory()
        {
            var roots = new List<string>();

            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            if (!string.IsNullOrWhiteSpace(baseDirectory))
            {
                roots.Add(baseDirectory);
            }

            string assemblyDirectory = TryGetExecutingAssemblyDirectory();
            if (!string.IsNullOrWhiteSpace(assemblyDirectory)
                && !roots.Exists(p => string.Equals(p, assemblyDirectory, StringComparison.OrdinalIgnoreCase)))
            {
                roots.Add(assemblyDirectory);
            }

            string currentDirectory = Environment.CurrentDirectory;
            if (!string.IsNullOrWhiteSpace(currentDirectory)
                && !roots.Exists(p => string.Equals(p, currentDirectory, StringComparison.OrdinalIgnoreCase)))
            {
                roots.Add(currentDirectory);
            }

            foreach (string root in roots)
            {
                var current = new DirectoryInfo(root);
                while (current != null)
                {
                    string directPath = Path.Combine(current.FullName, "decompiled");
                    string directReLogicPath = Path.Combine(directPath, "Terraria.Libraries.ReLogic.ReLogic.dll");
                    if (File.Exists(directReLogicPath))
                    {
                        return directPath;
                    }

                    string nestedPath = Path.Combine(current.FullName, "extract-mod", "decompiled");
                    string nestedReLogicPath = Path.Combine(nestedPath, "Terraria.Libraries.ReLogic.ReLogic.dll");
                    if (File.Exists(nestedReLogicPath))
                    {
                        return nestedPath;
                    }

                    current = current.Parent;
                }
            }

            return null;
        }

        private static string TryGetExecutingAssemblyDirectory()
        {
            try
            {
                string location = Assembly.GetExecutingAssembly().Location;
                return string.IsNullOrWhiteSpace(location) ? null : Path.GetDirectoryName(location);
            }
            catch
            {
                return null;
            }
        }
    }

    // === Item Name Resolver ===

    public static class ItemNameResolver
    {
        public static Func<int, string> Build(Assembly terrariaAssembly)
        {
            Type langType = terrariaAssembly.GetType("Terraria.Lang", throwOnError: false);
            Type itemIdType = terrariaAssembly.GetType("Terraria.ID.ItemID", throwOnError: false);
            Type itemType = terrariaAssembly.GetType("Terraria.Item", throwOnError: false);

            MethodInfo getItemNameValueMethod = langType == null
                ? null
                : langType.GetMethod(
                    "GetItemNameValue",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(int) },
                    null);

            object search = null;
            MethodInfo searchGetNameMethod = null;
            if (itemIdType != null)
            {
                FieldInfo searchField = itemIdType.GetField("Search", BindingFlags.Public | BindingFlags.Static);
                search = searchField == null ? null : searchField.GetValue(null);
                if (search != null)
                {
                    searchGetNameMethod = search.GetType().GetMethod("GetName", new[] { typeof(short) })
                        ?? search.GetType().GetMethod("GetName", new[] { typeof(int) });
                }
            }

            MethodInfo setDefaultsMethod = itemType == null
                ? null
                : itemType.GetMethod("SetDefaults", new[] { typeof(int) });
            PropertyInfo nameProperty = itemType == null
                ? null
                : itemType.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);

            object sharedItem = null;

            return delegate(int itemId)
            {
                if (getItemNameValueMethod != null)
                {
                    try
                    {
                        string name = Convert.ToString(getItemNameValueMethod.Invoke(null, new object[] { itemId }));
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            return name;
                        }
                    }
                    catch
                    {
                    }
                }

                if (search != null && searchGetNameMethod != null)
                {
                    try
                    {
                        Type parameterType = searchGetNameMethod.GetParameters()[0].ParameterType;
                        object typedId = parameterType == typeof(short)
                            ? (object)unchecked((short)itemId)
                            : (object)itemId;
                        string name = Convert.ToString(searchGetNameMethod.Invoke(search, new[] { typedId }));
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            return name;
                        }
                    }
                    catch
                    {
                    }
                }

                if (itemType != null && setDefaultsMethod != null && nameProperty != null)
                {
                    try
                    {
                        if (sharedItem == null)
                        {
                            sharedItem = Activator.CreateInstance(itemType);
                        }

                        setDefaultsMethod.Invoke(sharedItem, new object[] { itemId });
                        string name = Convert.ToString(nameProperty.GetValue(sharedItem, null));
                        if (!string.IsNullOrWhiteSpace(name)
                            && !name.StartsWith("ItemName.", StringComparison.Ordinal))
                        {
                            return name;
                        }
                    }
                    catch
                    {
                    }
                }

                return "Unknown_" + itemId;
            };
        }
    }

    // === Terraria Bootstrap ===

    public static class TerrariaBootstrap
    {
        public static Assembly LoadTerrariaAssembly(string terrariaExePath, string decompiledDir)
        {
            string fullPath = Path.GetFullPath(terrariaExePath);
            string terrariaDirectory = Path.GetDirectoryName(fullPath);

            TerrariaDependencyResolver.EnsureRegistered(fullPath, terrariaDirectory, decompiledDir);

            Assembly loaded = AppDomain.CurrentDomain
                .GetAssemblies()
                .FirstOrDefault(assembly =>
                {
                    string location = TryGetAssemblyLocation(assembly);
                    return !string.IsNullOrWhiteSpace(location)
                        && string.Equals(Path.GetFullPath(location), fullPath, StringComparison.OrdinalIgnoreCase);
                });

            return loaded ?? Assembly.LoadFrom(fullPath);
        }

        public static void InitializeProgramState(Assembly terrariaAssembly)
        {
            Type programType = ReflectionHelpers.GetRequiredType(terrariaAssembly, "Terraria.Program");
            Type mainType = ReflectionHelpers.GetRequiredType(terrariaAssembly, "Terraria.Main");
            Type playerType = ReflectionHelpers.GetRequiredType(terrariaAssembly, "Terraria.Player");

            FieldInfo savePathField = programType.GetField("SavePath", BindingFlags.Public | BindingFlags.Static);
            if (savePathField != null)
            {
                string savePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "_runtime", "TerrariaSave");
                Directory.CreateDirectory(savePath);
                savePathField.SetValue(null, savePath);
            }

            FieldInfo launchParametersField = programType.GetField("LaunchParameters", BindingFlags.Public | BindingFlags.Static);
            if (launchParametersField != null)
            {
                launchParametersField.SetValue(null, new Dictionary<string, string>());
            }

            FieldInfo myPlayerField = mainType.GetField("myPlayer", BindingFlags.Public | BindingFlags.Static);
            if (myPlayerField != null)
            {
                myPlayerField.SetValue(null, 0);
            }

            FieldInfo dedServField = mainType.GetField("dedServ", BindingFlags.Public | BindingFlags.Static);
            if (dedServField != null)
            {
                dedServField.SetValue(null, true);
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
                else
                {
                    for (int i = 0; i < players.Length; i++)
                    {
                        if (players.GetValue(i) == null)
                        {
                            players.SetValue(Activator.CreateInstance(playerType), i);
                        }
                    }
                }
            }
        }

        public static void EnsureRecipesInitialized(Assembly terrariaAssembly)
        {
            Type recipeType = ReflectionHelpers.GetRequiredType(terrariaAssembly, "Terraria.Recipe");
            Type recipeGroupType = ReflectionHelpers.GetRequiredType(terrariaAssembly, "Terraria.RecipeGroup");
            Type mainType = ReflectionHelpers.GetRequiredType(terrariaAssembly, "Terraria.Main");

            FieldInfo numRecipesField = ReflectionHelpers.GetRequiredField(
                recipeType, "numRecipes", BindingFlags.Public | BindingFlags.Static);
            FieldInfo maxRecipesField = ReflectionHelpers.GetRequiredField(
                recipeType, "maxRecipes", BindingFlags.Public | BindingFlags.Static);
            FieldInfo mainRecipeField = ReflectionHelpers.GetRequiredField(
                mainType, "recipe", BindingFlags.Public | BindingFlags.Static);
            MethodInfo setupRecipesMethod = ReflectionHelpers.GetRequiredMethod(
                recipeType, "SetupRecipes", BindingFlags.Public | BindingFlags.Static, Type.EmptyTypes);

            int numRecipes = Convert.ToInt32(numRecipesField.GetValue(null));
            int maxRecipes = Convert.ToInt32(maxRecipesField.GetValue(null));
            Array recipeArray = mainRecipeField.GetValue(null) as Array;

            bool recipesNeedInitialization = numRecipes <= 0
                || recipeArray == null
                || numRecipes > recipeArray.Length
                || HasNullRecipeEntries(recipeArray, numRecipes);

            if (recipesNeedInitialization)
            {
                recipeArray = CreateInitializedRecipeArray(recipeType, maxRecipes);
                mainRecipeField.SetValue(null, recipeArray);
                numRecipesField.SetValue(null, 0);
                ResetRecipeGroups(recipeGroupType);

                try
                {
                    setupRecipesMethod.Invoke(null, null);
                }
                catch (TargetInvocationException)
                {
                    recipeArray = CreateInitializedRecipeArray(recipeType, maxRecipes);
                    mainRecipeField.SetValue(null, recipeArray);
                    numRecipesField.SetValue(null, 0);
                    ResetRecipeGroups(recipeGroupType);
                    setupRecipesMethod.Invoke(null, null);
                }
            }

            ReflectionHelpers.TryInvokeStatic(recipeType, "UpdateWhichItemsAreCrafted");
        }

        private static string TryGetAssemblyLocation(Assembly assembly)
        {
            try
            {
                return assembly.Location;
            }
            catch (NotSupportedException)
            {
                return null;
            }
        }

        private static bool HasNullRecipeEntries(Array recipeArray, int numRecipes)
        {
            int upperBound = Math.Min(numRecipes, recipeArray.Length);
            for (int i = 0; i < upperBound; i++)
            {
                if (recipeArray.GetValue(i) == null)
                {
                    return true;
                }
            }

            return false;
        }

        private static Array CreateInitializedRecipeArray(Type recipeType, int length)
        {
            Array recipeArray = Array.CreateInstance(recipeType, length);
            for (int i = 0; i < length; i++)
            {
                recipeArray.SetValue(Activator.CreateInstance(recipeType), i);
            }

            return recipeArray;
        }

        private static void ResetRecipeGroups(Type recipeGroupType)
        {
            FieldInfo recipeGroupsField = recipeGroupType.GetField("recipeGroups", BindingFlags.Public | BindingFlags.Static);
            IDictionary recipeGroups = recipeGroupsField == null ? null : recipeGroupsField.GetValue(null) as IDictionary;
            recipeGroups?.Clear();

            FieldInfo recipeGroupIdsField = recipeGroupType.GetField("recipeGroupIDs", BindingFlags.Public | BindingFlags.Static);
            IDictionary recipeGroupIds = recipeGroupIdsField == null ? null : recipeGroupIdsField.GetValue(null) as IDictionary;
            recipeGroupIds?.Clear();

            FieldInfo nextRecipeGroupIndexField = recipeGroupType.GetField("nextRecipeGroupIndex", BindingFlags.Public | BindingFlags.Static);
            if (nextRecipeGroupIndexField != null)
            {
                nextRecipeGroupIndexField.SetValue(null, 0);
            }
        }
    }
}
