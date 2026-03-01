using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using StandaloneExtractor.Models;

namespace StandaloneExtractor.Extractors
{
    public sealed class RecipeDataExtractor : IExtractorPhase<RecipeRow>
    {
        private const string DefaultTerrariaExePath = @"C:\Program Files (x86)\Steam\steamapps\common\Terraria\Terraria.exe";

        public string PhaseName
        {
            get { return "recipes"; }
        }

        public IEnumerable<RecipeRow> Extract(ExtractionContext context)
        {
            var rows = new List<RecipeRow>();
            string terrariaExePath = ResolveTerrariaPath(context.CommandLineArgs);

            if (!File.Exists(terrariaExePath))
            {
                Console.WriteLine("[recipes] Terraria.exe not found at " + terrariaExePath);
                Console.WriteLine("[recipes] pass --terraria <path> to override the Terraria executable path.");
                return rows;
            }

            try
            {
                EnsureTerrariaAssemblyResolve(terrariaExePath);
                Assembly terrariaAssembly = LoadTerrariaAssembly(terrariaExePath);
                Type programType = GetRequiredType(terrariaAssembly, "Terraria.Program");
                Type recipeType = GetRequiredType(terrariaAssembly, "Terraria.Recipe");
                Type recipeGroupType = GetRequiredType(terrariaAssembly, "Terraria.RecipeGroup");
                Type mainType = GetRequiredType(terrariaAssembly, "Terraria.Main");
                Type langType = GetRequiredType(terrariaAssembly, "Terraria.Lang");
                Type tileIdType = GetRequiredType(terrariaAssembly, "Terraria.ID.TileID");

                PrepareTerrariaProgramState(terrariaAssembly, programType, mainType, context.OutputDirectory);
                EnsureMainDataInitialized(mainType);
                TryInvokeStaticIgnoringErrors(langType, "InitializeLegacyLocalization");
                EnsureRecipesInitialized(recipeType, recipeGroupType, mainType);
                int recipeCount = GetStaticFieldValue<int>(recipeType, "numRecipes");
                Array recipes = GetStaticFieldValue<Array>(mainType, "recipe");

                if (recipeCount <= 0 || recipes == null)
                {
                    Console.WriteLine("[recipes] recipe initialization yielded no rows.");
                    return rows;
                }

                Func<int, string> stationNameResolver = CreateStationNameResolver(tileIdType);

                for (int i = 0; i < recipeCount; i++)
                {
                    object recipe = recipes.GetValue(i);
                    if (recipe == null)
                    {
                        continue;
                    }

                    rows.Add(ProjectRecipe(recipe, i, stationNameResolver));
                }

                Console.WriteLine("[recipes] extracted " + rows.Count + " recipes from Main.recipe");
                return rows;
            }
            catch (Exception ex)
            {
                Console.WriteLine("[recipes] extraction failed: " + ex);
                if (ex.InnerException != null)
                {
                    Console.WriteLine("[recipes] extraction inner exception: " + ex.InnerException);
                }

                return rows;
            }
        }

        private static RecipeRow ProjectRecipe(object recipe, int index, Func<int, string> stationNameResolver)
        {
            var row = new RecipeRow();
            row.RecipeIndex = index;

            object resultItem = GetInstanceFieldValue<object>(recipe, "createItem");
            if (resultItem != null)
            {
                row.ResultItemId = GetInstanceFieldValue<int>(resultItem, "type");
                row.ResultItemName = GetItemName(resultItem);
                row.ResultAmount = GetInstanceFieldValue<int>(resultItem, "stack");
            }

            Array requiredItems = GetInstanceFieldValue<Array>(recipe, "requiredItem");
            if (requiredItems != null)
            {
                foreach (object ingredient in requiredItems)
                {
                    if (ingredient == null)
                    {
                        continue;
                    }

                    int ingredientItemId = GetInstanceFieldValue<int>(ingredient, "type");
                    if (ingredientItemId <= 0)
                    {
                        break;
                    }

                    row.Ingredients.Add(new RecipeIngredientRow
                    {
                        ItemId = ingredientItemId,
                        Name = GetItemName(ingredient),
                        Count = GetInstanceFieldValue<int>(ingredient, "stack")
                    });
                }
            }

            Array requiredTiles = GetInstanceFieldValue<Array>(recipe, "requiredTile");
            if (requiredTiles != null)
            {
                foreach (object tileObj in requiredTiles)
                {
                    int tileId = Convert.ToInt32(tileObj);
                    if (tileId < 0)
                    {
                        break;
                    }

                    row.CraftingStations.Add(stationNameResolver(tileId));
                }
            }

            AppendConditions(recipe, row.Conditions);
            return row;
        }

        private static void EnsureRecipesInitialized(Type recipeType, Type recipeGroupType, Type mainType)
        {
            FieldInfo numRecipesField = GetRequiredStaticField(recipeType, "numRecipes");
            FieldInfo maxRecipesField = GetRequiredStaticField(recipeType, "maxRecipes");
            FieldInfo mainRecipeField = GetRequiredStaticField(mainType, "recipe");
            MethodInfo setupRecipesMethod = recipeType.GetMethod("SetupRecipes", BindingFlags.Public | BindingFlags.Static);
            if (setupRecipesMethod == null)
            {
                throw new MissingMethodException(recipeType.FullName, "SetupRecipes");
            }

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

            TryInvokeStatic(recipeType, "UpdateWhichItemsAreCrafted");
        }

        private static void PrepareTerrariaProgramState(Assembly terrariaAssembly, Type programType, Type mainType, string outputDirectory)
        {
            EnsureProgramSavePath(programType, outputDirectory);

            FieldInfo launchParametersField = programType.GetField("LaunchParameters", BindingFlags.Public | BindingFlags.Static);
            if (launchParametersField != null)
            {
                launchParametersField.SetValue(null, new Dictionary<string, string>());
            }

            Type playerType = GetRequiredType(terrariaAssembly, "Terraria.Player");

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

        private static void EnsureMainDataInitialized(Type mainType)
        {
            InvokePrivateStaticIfPresent(mainType, "Initialize_TileAndNPCData1");
        }

        private static void InvokePrivateStaticIfPresent(Type type, string methodName)
        {
            MethodInfo method = type.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
            if (method != null)
            {
                method.Invoke(null, null);
            }
        }

        private static void EnsureProgramSavePath(Type programType, string outputDirectory)
        {
            string savePath = Path.Combine(
                string.IsNullOrWhiteSpace(outputDirectory) ? AppDomain.CurrentDomain.BaseDirectory : outputDirectory,
                "_runtime",
                "TerrariaSave");

            Directory.CreateDirectory(savePath);

            FieldInfo savePathField = programType.GetField("SavePath", BindingFlags.Public | BindingFlags.Static);
            if (savePathField != null)
            {
                savePathField.SetValue(null, savePath);
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

        private static void TryInvokeStatic(Type type, string methodName)
        {
            MethodInfo method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (method != null)
            {
                method.Invoke(null, null);
            }
        }

        private static void TryInvokeStaticIgnoringErrors(Type type, string methodName)
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

        private static void EnsureTerrariaAssemblyResolve(string terrariaExePath)
        {
            TerrariaDependencyResolver.EnsureRegistered(
                terrariaExePath,
                Path.GetDirectoryName(terrariaExePath),
                FindRepositoryDecompiledDirectory());
        }

        private static string FindRepositoryDecompiledDirectory()
        {
            string assemblyLocation = typeof(RecipeDataExtractor).Assembly.Location;
            var current = new DirectoryInfo(Path.GetDirectoryName(assemblyLocation));
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

        private static Func<int, string> CreateStationNameResolver(Type tileIdType)
        {
            FieldInfo searchField = tileIdType.GetField("Search", BindingFlags.Public | BindingFlags.Static);
            object search = searchField == null ? null : searchField.GetValue(null);

            MethodInfo getNameMethod = null;
            if (search != null)
            {
                Type searchType = search.GetType();
                getNameMethod = searchType.GetMethod("GetName", new[] { typeof(int) })
                    ?? searchType.GetMethod("GetName", new[] { typeof(ushort) });
            }

            return delegate(int tileId)
            {
                if (getNameMethod != null && search != null)
                {
                    object[] args = getNameMethod.GetParameters()[0].ParameterType == typeof(ushort)
                        ? new object[] { unchecked((ushort)tileId) }
                        : new object[] { tileId };

                    string name = Convert.ToString(getNameMethod.Invoke(search, args));
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        return name;
                    }
                }

                return "TileID." + tileId;
            };
        }

        private static string GetItemName(object item)
        {
            PropertyInfo nameProperty = item.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
            string name = nameProperty == null ? null : Convert.ToString(nameProperty.GetValue(item, null));
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }

            return "ItemID." + GetInstanceFieldValue<int>(item, "type");
        }

        private static void AppendConditions(object recipe, List<string> conditions)
        {
            AddConditionIfTrue(recipe, "needWater", "Water");
            AddConditionIfTrue(recipe, "needLava", "Lava");
            AddConditionIfTrue(recipe, "needHoney", "Honey");
            AddConditionIfTrue(recipe, "needSnowBiome", "SnowBiome");
            AddConditionIfTrue(recipe, "needGraveyardBiome", "GraveyardBiome");
            AddConditionIfTrue(recipe, "needEverythingSeed", "EverythingSeed");
            AddConditionIfTrue(recipe, "alchemy", "AlchemyTable");
            AddConditionIfTrue(recipe, "anyWood", "AnyWood");
            AddConditionIfTrue(recipe, "anyIronBar", "AnyIronBar");
            AddConditionIfTrue(recipe, "anyPressurePlate", "AnyPressurePlate");
            AddConditionIfTrue(recipe, "anySand", "AnySand");
            AddConditionIfTrue(recipe, "anyFragment", "AnyFragment");
            AddConditionIfTrue(recipe, "crimson", "CrimsonWorld");
            AddConditionIfTrue(recipe, "corruption", "CorruptionWorld");
            AddConditionIfTrue(recipe, "notDecraftable", "NotDecraftable");

            void AddConditionIfTrue(object source, string fieldName, string label)
            {
                if (GetInstanceFieldValue<bool>(source, fieldName))
                {
                    conditions.Add(label);
                }
            }
        }

        private static string ResolveTerrariaPath(IList<string> args)
        {
            if (args == null)
            {
                return DefaultTerrariaExePath;
            }

            for (int i = 0; i < args.Count; i++)
            {
                if (string.Equals(args[i], "--terraria", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(args[i], "-t", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Count && !string.IsNullOrWhiteSpace(args[i + 1]))
                    {
                        return Path.GetFullPath(args[i + 1]);
                    }
                }
            }

            return DefaultTerrariaExePath;
        }

        private static Assembly LoadTerrariaAssembly(string terrariaExePath)
        {
            string fullPath = Path.GetFullPath(terrariaExePath);
            Assembly loaded = AppDomain.CurrentDomain
                .GetAssemblies()
                .FirstOrDefault(delegate(Assembly assembly)
                {
                    string location = TryGetAssemblyLocation(assembly);
                    return !string.IsNullOrWhiteSpace(location)
                        && string.Equals(Path.GetFullPath(location), fullPath, StringComparison.OrdinalIgnoreCase);
                });

            return loaded ?? Assembly.LoadFrom(fullPath);
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

        private static Type GetRequiredType(Assembly assembly, string fullName)
        {
            Type type = assembly.GetType(fullName, throwOnError: false);
            if (type == null)
            {
                throw new InvalidOperationException("Missing type in Terraria assembly: " + fullName);
            }

            return type;
        }

        private static T GetStaticFieldValue<T>(Type type, string fieldName)
        {
            FieldInfo field = GetRequiredStaticField(type, fieldName);
            return (T)field.GetValue(null);
        }

        private static FieldInfo GetRequiredStaticField(Type type, string fieldName)
        {
            FieldInfo field = type.GetField(fieldName, BindingFlags.Public | BindingFlags.Static);
            if (field == null)
            {
                throw new MissingFieldException(type.FullName, fieldName);
            }

            return field;
        }

        private static T GetInstanceFieldValue<T>(object instance, string fieldName)
        {
            FieldInfo field = instance.GetType().GetField(fieldName, BindingFlags.Public | BindingFlags.Instance);
            if (field == null)
            {
                throw new MissingFieldException(instance.GetType().FullName, fieldName);
            }

            return (T)field.GetValue(instance);
        }
    }
}
