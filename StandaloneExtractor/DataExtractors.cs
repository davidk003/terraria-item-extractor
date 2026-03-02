using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace StandaloneExtractor
{
    // === Extractor Interface ===

    public interface IExtractorPhase<T>
    {
        string PhaseName { get; }

        IEnumerable<T> Extract(ExtractionContext context);
    }

    // === Item Data Extractor ===

    public sealed class ItemDataExtractor : IExtractorPhase<ItemRow>
    {
        public string PhaseName
        {
            get { return "item-pricing"; }
        }

        public IEnumerable<ItemRow> Extract(ExtractionContext context)
        {
            Assembly terrariaAssembly = context.TerrariaAssembly;
            if (terrariaAssembly == null)
            {
                throw new InvalidOperationException("Terraria assembly was not loaded.");
            }

            Type itemType = terrariaAssembly.GetType("Terraria.Item", throwOnError: true);
            Type itemIdType = terrariaAssembly.GetType("Terraria.ID.ItemID", throwOnError: true);

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
    }

    // === Shimmer Data Extractor ===

    public sealed class ShimmerDataExtractor : IExtractorPhase<ShimmerRow>
    {
        private const string ItemTransformType = "item_transform";
        private const string DeconstructType = "deconstruct";

        public string PhaseName
        {
            get { return "shimmer"; }
        }

        public IEnumerable<ShimmerRow> Extract(ExtractionContext context)
        {
            Assembly terrariaAssembly = context.TerrariaAssembly;
            if (terrariaAssembly == null)
            {
                throw new InvalidOperationException("Terraria assembly was not loaded.");
            }

            Type itemType = ReflectionHelpers.GetRequiredType(terrariaAssembly, "Terraria.Item");
            Type mainType = ReflectionHelpers.GetRequiredType(terrariaAssembly, "Terraria.Main");
            Type recipeType = ReflectionHelpers.GetRequiredType(terrariaAssembly, "Terraria.Recipe");
            Type itemIdType = ReflectionHelpers.GetRequiredType(terrariaAssembly, "Terraria.ID.ItemID");
            Type itemIdSetsType = ReflectionHelpers.GetRequiredType(terrariaAssembly, "Terraria.ID.ItemID+Sets");
            Type langType = ReflectionHelpers.GetRequiredType(terrariaAssembly, "Terraria.Lang");
            Type shimmerTransformsType = ReflectionHelpers.GetRequiredType(terrariaAssembly, "Terraria.GameContent.ShimmerTransforms");

            ReflectionHelpers.TryInvokeStaticIgnoringErrors(langType, "InitializeLegacyLocalization");
            TerrariaBootstrap.EnsureRecipesInitialized(terrariaAssembly);

            int itemCount = Convert.ToInt32(ReflectionHelpers.GetRequiredField(itemIdType, "Count", BindingFlags.Public | BindingFlags.Static).GetValue(null));
            int[] shimmerTransformToItem = (int[])ReflectionHelpers.GetRequiredField(itemIdSetsType, "ShimmerTransformToItem", BindingFlags.Public | BindingFlags.Static).GetValue(null);
            int[] shimmerCountsAsItem = (int[])ReflectionHelpers.GetRequiredField(itemIdSetsType, "ShimmerCountsAsItem", BindingFlags.Public | BindingFlags.Static).GetValue(null);

            MethodInfo getDecraftingRecipeIndexMethod = ReflectionHelpers.GetRequiredMethod(
                shimmerTransformsType,
                "GetDecraftingRecipeIndex",
                BindingFlags.Public | BindingFlags.Static,
                new[] { typeof(int) });

            FieldInfo itemTypeField = ReflectionHelpers.GetRequiredField(itemType, "type", BindingFlags.Public | BindingFlags.Instance);
            FieldInfo itemStackField = ReflectionHelpers.GetRequiredField(itemType, "stack", BindingFlags.Public | BindingFlags.Instance);

            FieldInfo mainRecipeField = ReflectionHelpers.GetRequiredField(mainType, "recipe", BindingFlags.Public | BindingFlags.Static);
            Array recipeArray = mainRecipeField.GetValue(null) as Array;
            if (recipeArray == null)
            {
                throw new InvalidOperationException("Terraria.Main.recipe is not initialized.");
            }

            FieldInfo requiredItemField = ReflectionHelpers.GetRequiredField(recipeType, "requiredItem", BindingFlags.Public | BindingFlags.Instance);
            FieldInfo customShimmerResultsField = ReflectionHelpers.GetRequiredField(recipeType, "customShimmerResults", BindingFlags.Public | BindingFlags.Instance);
            Func<int, string> getItemName = ItemNameResolver.Build(terrariaAssembly);

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
    }

    // === Recipe Data Extractor ===

    public sealed class RecipeDataExtractor : IExtractorPhase<RecipeRow>
    {
        public string PhaseName
        {
            get { return "recipes"; }
        }

        public IEnumerable<RecipeRow> Extract(ExtractionContext context)
        {
            var rows = new List<RecipeRow>();
            Assembly terrariaAssembly = context.TerrariaAssembly;

            if (terrariaAssembly == null)
            {
                Console.WriteLine("[recipes] Terraria assembly was not loaded.");
                return rows;
            }

            try
            {
                Type recipeType = ReflectionHelpers.GetRequiredType(terrariaAssembly, "Terraria.Recipe");
                Type mainType = ReflectionHelpers.GetRequiredType(terrariaAssembly, "Terraria.Main");
                Type langType = ReflectionHelpers.GetRequiredType(terrariaAssembly, "Terraria.Lang");
                Type tileIdType = ReflectionHelpers.GetRequiredType(terrariaAssembly, "Terraria.ID.TileID");

                EnsureMainDataInitialized(mainType);
                ReflectionHelpers.TryInvokeStaticIgnoringErrors(langType, "InitializeLegacyLocalization");
                TerrariaBootstrap.EnsureRecipesInitialized(terrariaAssembly);
                int recipeCount = ReflectionHelpers.GetStaticFieldValue<int>(recipeType, "numRecipes");
                Array recipes = ReflectionHelpers.GetStaticFieldValue<Array>(mainType, "recipe");

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

            object resultItem = ReflectionHelpers.GetInstanceFieldValue<object>(recipe, "createItem");
            if (resultItem != null)
            {
                row.ResultItemId = ReflectionHelpers.GetInstanceFieldValue<int>(resultItem, "type");
                row.ResultItemName = GetItemName(resultItem);
                row.ResultAmount = ReflectionHelpers.GetInstanceFieldValue<int>(resultItem, "stack");
            }

            Array requiredItems = ReflectionHelpers.GetInstanceFieldValue<Array>(recipe, "requiredItem");
            if (requiredItems != null)
            {
                foreach (object ingredient in requiredItems)
                {
                    if (ingredient == null)
                    {
                        continue;
                    }

                    int ingredientItemId = ReflectionHelpers.GetInstanceFieldValue<int>(ingredient, "type");
                    if (ingredientItemId <= 0)
                    {
                        break;
                    }

                    row.Ingredients.Add(new RecipeIngredientRow
                    {
                        ItemId = ingredientItemId,
                        Name = GetItemName(ingredient),
                        Count = ReflectionHelpers.GetInstanceFieldValue<int>(ingredient, "stack")
                    });
                }
            }

            Array requiredTiles = ReflectionHelpers.GetInstanceFieldValue<Array>(recipe, "requiredTile");
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

            return "ItemID." + ReflectionHelpers.GetInstanceFieldValue<int>(item, "type");
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
                if (ReflectionHelpers.GetInstanceFieldValue<bool>(source, fieldName))
                {
                    conditions.Add(label);
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
    }
}
