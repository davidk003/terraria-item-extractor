using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using StandaloneExtractor.Models;

namespace StandaloneExtractor.Extractors
{
    public sealed class ShimmerDataExtractor : IExtractorPhase<ShimmerRow>
    {
        private const string DefaultTerrariaExePath = @"C:\Program Files (x86)\Steam\steamapps\common\Terraria\Terraria.exe";
        private const string ItemTransformType = "item_transform";
        private const string DeconstructType = "deconstruct";

        public string PhaseName
        {
            get { return "shimmer"; }
        }

        public IEnumerable<ShimmerRow> Extract(ExtractionContext context)
        {
            string terrariaPath = ResolveTerrariaPath(context);
            if (!File.Exists(terrariaPath))
            {
                throw new FileNotFoundException("Could not locate Terraria.exe for shimmer extraction.", terrariaPath);
            }

            TerrariaDependencyResolver.EnsureRegistered(
                terrariaPath,
                Path.GetDirectoryName(terrariaPath),
                FindRepositoryDecompiledDirectory());

            Assembly terrariaAssembly = LoadTerrariaAssembly(terrariaPath);

            Type itemType = GetRequiredType(terrariaAssembly, "Terraria.Item");
            Type programType = GetRequiredType(terrariaAssembly, "Terraria.Program");
            Type mainType = GetRequiredType(terrariaAssembly, "Terraria.Main");
            Type recipeType = GetRequiredType(terrariaAssembly, "Terraria.Recipe");
            Type recipeGroupType = GetRequiredType(terrariaAssembly, "Terraria.RecipeGroup");
            Type itemIdType = GetRequiredType(terrariaAssembly, "Terraria.ID.ItemID");
            Type itemIdSetsType = GetRequiredType(terrariaAssembly, "Terraria.ID.ItemID+Sets");
            Type langType = GetRequiredType(terrariaAssembly, "Terraria.Lang");
            Type shimmerTransformsType = GetRequiredType(terrariaAssembly, "Terraria.GameContent.ShimmerTransforms");

            PrepareTerrariaProgramState(terrariaAssembly, programType, mainType, context);
            TryInvokeStaticIgnoringErrors(langType, "InitializeLegacyLocalization");
            EnsureRecipesInitialized(recipeType, recipeGroupType, mainType);

            int itemCount = Convert.ToInt32(GetRequiredField(itemIdType, "Count", BindingFlags.Public | BindingFlags.Static).GetValue(null));
            int[] shimmerTransformToItem = (int[])GetRequiredField(itemIdSetsType, "ShimmerTransformToItem", BindingFlags.Public | BindingFlags.Static).GetValue(null);
            int[] shimmerCountsAsItem = (int[])GetRequiredField(itemIdSetsType, "ShimmerCountsAsItem", BindingFlags.Public | BindingFlags.Static).GetValue(null);

            MethodInfo getDecraftingRecipeIndexMethod = GetRequiredMethod(
                shimmerTransformsType,
                "GetDecraftingRecipeIndex",
                BindingFlags.Public | BindingFlags.Static,
                new[] { typeof(int) });

            FieldInfo itemTypeField = GetRequiredField(itemType, "type", BindingFlags.Public | BindingFlags.Instance);
            FieldInfo itemStackField = GetRequiredField(itemType, "stack", BindingFlags.Public | BindingFlags.Instance);

            FieldInfo mainRecipeField = GetRequiredField(mainType, "recipe", BindingFlags.Public | BindingFlags.Static);
            Array recipeArray = mainRecipeField.GetValue(null) as Array;
            if (recipeArray == null)
            {
                throw new InvalidOperationException("Terraria.Main.recipe is not initialized.");
            }

            FieldInfo requiredItemField = GetRequiredField(recipeType, "requiredItem", BindingFlags.Public | BindingFlags.Instance);
            FieldInfo customShimmerResultsField = GetRequiredField(recipeType, "customShimmerResults", BindingFlags.Public | BindingFlags.Instance);
            Func<int, string> getItemName = BuildItemNameResolver(itemIdType, langType, itemCount);

            var rows = new List<ShimmerRow>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int itemTransformCount = 0;
            int deconstructCount = 0;

            int directTransformUpperBound = Math.Min(itemCount, shimmerTransformToItem.Length);
            for (int inputItemId = 1; inputItemId < directTransformUpperBound; inputItemId++)
            {
                int outputItemId = shimmerTransformToItem[inputItemId];
                if (outputItemId <= 0)
                {
                    continue;
                }

                if (TryAddRow(rows, seen, inputItemId, getItemName(inputItemId), outputItemId, getItemName(outputItemId), 1, ItemTransformType))
                {
                    itemTransformCount++;
                }
            }

            for (int inputItemId = 1; inputItemId < itemCount; inputItemId++)
            {
                int equivalentItemId = GetEquivalentItemId(inputItemId, shimmerCountsAsItem);
                if (equivalentItemId <= 0)
                {
                    continue;
                }

                if (equivalentItemId < shimmerTransformToItem.Length && shimmerTransformToItem[equivalentItemId] > 0)
                {
                    continue;
                }

                int decraftingRecipeIndex = Convert.ToInt32(getDecraftingRecipeIndexMethod.Invoke(null, new object[] { equivalentItemId }));
                if (decraftingRecipeIndex < 0 || decraftingRecipeIndex >= recipeArray.Length)
                {
                    continue;
                }

                object recipe = recipeArray.GetValue(decraftingRecipeIndex);
                if (recipe == null)
                {
                    continue;
                }

                IEnumerable outputs = GetDeconstructOutputs(recipe, requiredItemField, customShimmerResultsField);
                foreach (object outputItem in outputs)
                {
                    if (outputItem == null)
                    {
                        continue;
                    }

                    int outputItemId = Convert.ToInt32(itemTypeField.GetValue(outputItem));
                    if (outputItemId <= 0)
                    {
                        break;
                    }

                    int outputAmount = Convert.ToInt32(itemStackField.GetValue(outputItem));
                    if (outputAmount <= 0)
                    {
                        outputAmount = 1;
                    }

                    if (TryAddRow(rows, seen, inputItemId, getItemName(inputItemId), outputItemId, getItemName(outputItemId), outputAmount, DeconstructType))
                    {
                        deconstructCount++;
                    }
                }
            }

            Console.WriteLine("[Shimmer] item_transform=" + itemTransformCount + ", deconstruct=" + deconstructCount + ", total=" + rows.Count);

            return rows
                .OrderBy(row => row.InputItemId)
                .ThenBy(row => row.Type, StringComparer.Ordinal)
                .ThenBy(row => row.OutputItemId)
                .ThenBy(row => row.OutputAmount)
                .ToList();
        }

        private static void PrepareTerrariaProgramState(Assembly terrariaAssembly, Type programType, Type mainType, ExtractionContext context)
        {
            FieldInfo savePathField = programType.GetField("SavePath", BindingFlags.Public | BindingFlags.Static);
            if (savePathField != null)
            {
                string savePath = Path.Combine(context.OutputDirectory, "_runtime", "TerrariaSave");
                Directory.CreateDirectory(savePath);
                savePathField.SetValue(null, savePath);
            }

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

        private static void EnsureRecipesInitialized(Type recipeType, Type recipeGroupType, Type mainType)
        {
            FieldInfo numRecipesField = GetRequiredField(recipeType, "numRecipes", BindingFlags.Public | BindingFlags.Static);
            FieldInfo maxRecipesField = GetRequiredField(recipeType, "maxRecipes", BindingFlags.Public | BindingFlags.Static);
            FieldInfo mainRecipeField = GetRequiredField(mainType, "recipe", BindingFlags.Public | BindingFlags.Static);
            MethodInfo setupRecipesMethod = GetRequiredMethod(recipeType, "SetupRecipes", BindingFlags.Public | BindingFlags.Static, Type.EmptyTypes);

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

        private static Func<int, string> BuildItemNameResolver(Type itemIdType, Type langType, int itemCount)
        {
            MethodInfo getItemNameValueMethod = langType.GetMethod(
                "GetItemNameValue",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(int) },
                null);

            object search = null;
            MethodInfo searchGetNameMethod = null;
            FieldInfo searchField = itemIdType.GetField("Search", BindingFlags.Public | BindingFlags.Static);
            if (searchField != null)
            {
                search = searchField.GetValue(null);
                if (search != null)
                {
                    searchGetNameMethod = search.GetType().GetMethod("GetName", new[] { typeof(short) })
                        ?? search.GetType().GetMethod("GetName", new[] { typeof(int) });
                }
            }

            var names = new string[itemCount];
            for (int itemId = 0; itemId < itemCount; itemId++)
            {
                string itemName = string.Empty;

                if (getItemNameValueMethod != null)
                {
                    try
                    {
                        itemName = Convert.ToString(getItemNameValueMethod.Invoke(null, new object[] { itemId })) ?? string.Empty;
                    }
                    catch
                    {
                        itemName = string.Empty;
                    }
                }

                if (string.IsNullOrWhiteSpace(itemName) && search != null && searchGetNameMethod != null)
                {
                    try
                    {
                        Type parameterType = searchGetNameMethod.GetParameters()[0].ParameterType;
                        object typedItemId = parameterType == typeof(short)
                            ? (object)unchecked((short)itemId)
                            : itemId;
                        itemName = Convert.ToString(searchGetNameMethod.Invoke(search, new[] { typedItemId })) ?? string.Empty;
                    }
                    catch
                    {
                        itemName = string.Empty;
                    }
                }

                if (string.IsNullOrWhiteSpace(itemName))
                {
                    itemName = "Item_" + itemId;
                }

                names[itemId] = itemName;
            }

            return itemId =>
            {
                if (itemId >= 0 && itemId < names.Length)
                {
                    return names[itemId];
                }

                return "Item_" + itemId;
            };
        }

        private static IEnumerable GetDeconstructOutputs(object recipe, FieldInfo requiredItemField, FieldInfo customShimmerResultsField)
        {
            object customResults = customShimmerResultsField.GetValue(recipe);
            if (customResults != null)
            {
                return (IEnumerable)customResults;
            }

            object requiredItems = requiredItemField.GetValue(recipe);
            return requiredItems as IEnumerable ?? Array.Empty<object>();
        }

        private static int GetEquivalentItemId(int inputItemId, int[] shimmerCountsAsItem)
        {
            if (inputItemId > 0 && inputItemId < shimmerCountsAsItem.Length)
            {
                int equivalent = shimmerCountsAsItem[inputItemId];
                if (equivalent > 0)
                {
                    return equivalent;
                }
            }

            return inputItemId;
        }

        private static bool TryAddRow(
            IList<ShimmerRow> rows,
            ISet<string> seen,
            int inputItemId,
            string inputItemName,
            int outputItemId,
            string outputItemName,
            int outputAmount,
            string type)
        {
            string dedupeKey = inputItemId + "|" + outputItemId + "|" + outputAmount + "|" + type;
            if (!seen.Add(dedupeKey))
            {
                return false;
            }

            rows.Add(new ShimmerRow
            {
                InputItemId = inputItemId,
                InputItemName = inputItemName,
                OutputItemId = outputItemId,
                OutputItemName = outputItemName,
                OutputAmount = outputAmount,
                Type = type
            });

            return true;
        }

        private static Assembly LoadTerrariaAssembly(string terrariaExePath)
        {
            string fullPath = Path.GetFullPath(terrariaExePath);
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

        private static string FindRepositoryDecompiledDirectory()
        {
            string assemblyLocation = typeof(ShimmerDataExtractor).Assembly.Location;
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

        private static string ResolveTerrariaPath(ExtractionContext context)
        {
            string[] args = context == null ? Array.Empty<string>() : context.CommandLineArgs;
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--terraria", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(args[i], "-t", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length && !string.IsNullOrWhiteSpace(args[i + 1]))
                    {
                        return Path.GetFullPath(args[i + 1]);
                    }
                }
            }

            return DefaultTerrariaExePath;
        }

        private static Type GetRequiredType(Assembly assembly, string fullName)
        {
            Type type = assembly.GetType(fullName, throwOnError: false);
            if (type == null)
            {
                throw new InvalidOperationException("Missing Terraria type: " + fullName);
            }

            return type;
        }

        private static MethodInfo GetRequiredMethod(Type type, string methodName, BindingFlags flags, Type[] parameterTypes)
        {
            MethodInfo method = type.GetMethod(methodName, flags, null, parameterTypes, null);
            if (method == null)
            {
                throw new InvalidOperationException("Missing method: " + type.FullName + "." + methodName);
            }

            return method;
        }

        private static FieldInfo GetRequiredField(Type type, string fieldName, BindingFlags flags)
        {
            FieldInfo field = type.GetField(fieldName, flags);
            if (field == null)
            {
                throw new InvalidOperationException("Missing field: " + type.FullName + "." + fieldName);
            }

            return field;
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
    }
}
