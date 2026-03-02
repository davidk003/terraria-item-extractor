using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;

namespace StandaloneExtractor
{
    public sealed class NpcShopDataExtractor : IExtractorPhase<NpcShopRow>
    {
        private const int TravelShopSamplingRuns = 200;
        private static readonly Regex IfRegex = new Regex(@"^(?:else\s+)?if\s*\((.+)\)", RegexOptions.Compiled);
        private static readonly Regex CaseRegex = new Regex(@"^case\s+(\d+)\s*:", RegexOptions.Compiled);
        private static readonly Regex SetDefaultsRegex = new Regex(@"\.SetDefaults\((\d+)\)", RegexOptions.Compiled);

        public string PhaseName
        {
            get { return "npc-shops"; }
        }

        public IEnumerable<NpcShopRow> Extract(ExtractionContext context)
        {
            Assembly terrariaAssembly = context.TerrariaAssembly;
            if (terrariaAssembly == null)
            {
                Console.WriteLine("[npc-shops] Terraria assembly was not loaded.");
                return new List<NpcShopRow>();
            }

            ParsedShopSource parsedShopSource = ParseShopSourceData();
            TerrariaRuntime runtime;
            try
            {
                runtime = BootstrapRuntime(terrariaAssembly);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[npc-shops] Runtime bootstrap failed: " + ex);
                return new List<NpcShopRow>();
            }

            var shops = new List<NpcShopRow>();
            foreach (ShopMapping mapping in GetShopMappings())
            {
                NpcShopRow extractedShop = ExtractShop(mapping, runtime, parsedShopSource);
                if (extractedShop != null)
                {
                    shops.Add(extractedShop);
                }
            }

            int totalItemRows = shops.Sum(s => s.Items.Count);
            Console.WriteLine("[npc-shops] Extracted " + shops.Count + " shops and " + totalItemRows + " items");
            return shops;
        }

        // === Shop Extraction ===

        private static NpcShopRow ExtractShop(
            ShopMapping mapping,
            TerrariaRuntime runtime,
            ParsedShopSource parsedShopSource)
        {
            if (mapping.UseTravelShop)
            {
                return ExtractTravelShop(mapping, runtime);
            }

            object chest = Activator.CreateInstance(runtime.ChestType, new object[] { false });
            try
            {
                runtime.SetupShopMethod.Invoke(chest, new object[] { mapping.ShopId });
            }
            catch (Exception ex)
            {
                string reason = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
                Console.WriteLine("[npc-shops] SetupShop(" + mapping.ShopId + ") failed: " + reason);

                if (mapping.UseZoologistILFallback)
                {
                    NpcShopRow fallback = ExtractZoologistShopFromSetupShopIL(mapping, runtime);
                    if (fallback != null && fallback.Items.Count > 0)
                    {
                        Console.WriteLine("[npc-shops] SetupShop(" + mapping.ShopId + ") fallback extracted " + fallback.Items.Count + " items from SetupShop IL");
                        return fallback;
                    }
                }

                return ExtractShopFromSource(mapping, runtime, parsedShopSource);
            }

            Array chestItems = runtime.ChestItemsField.GetValue(chest) as Array;
            if (chestItems == null)
            {
                return null;
            }

            var itemsById = new Dictionary<int, NpcShopItemRow>();
            for (int slot = 0; slot < chestItems.Length; slot++)
            {
                object item = chestItems.GetValue(slot);
                if (item == null)
                {
                    continue;
                }

                if ((bool)runtime.ItemIsAirProperty.GetValue(item, null))
                {
                    continue;
                }

                int itemId = (int)runtime.ItemTypeField.GetValue(item);
                if (itemId <= 0)
                {
                    continue;
                }

                NpcShopItemRow row;
                if (!itemsById.TryGetValue(itemId, out row))
                {
                    row = new NpcShopItemRow
                    {
                        ItemId = itemId,
                        Name = runtime.GetItemName(itemId),
                        BuyPrice = (int)runtime.ItemGetStoreValueMethod.Invoke(item, null)
                    };
                    itemsById[itemId] = row;
                }

                int specialCurrency = (int)runtime.ItemSpecialCurrencyField.GetValue(item);
                if (specialCurrency >= 0)
                {
                    string specialCurrencyCondition = "specialCurrency=" + specialCurrency.ToString(CultureInfo.InvariantCulture);
                    if (!row.Conditions.Contains(specialCurrencyCondition))
                    {
                        row.Conditions.Add(specialCurrencyCondition);
                    }
                }

                AddParsedConditions(mapping.ShopId, itemId, parsedShopSource.ConditionsByShop, row.Conditions);
            }

            return new NpcShopRow
            {
                NpcId = mapping.NpcId,
                NpcName = runtime.GetNpcName(mapping.NpcId),
                ShopName = mapping.ShopName,
                Items = itemsById.Values.OrderBy(i => i.ItemId).ToList()
            };
        }

        // === Travel Shop Sampling ===

        private static NpcShopRow ExtractTravelShop(ShopMapping mapping, TerrariaRuntime runtime)
        {
            if (runtime.SetupTravelShopMethod == null || runtime.TravelShopField == null)
            {
                Console.WriteLine("[npc-shops] SetupTravelShop members were not found; returning empty Travelling Merchant shop.");
                return new NpcShopRow
                {
                    NpcId = mapping.NpcId,
                    NpcName = runtime.GetNpcName(mapping.NpcId),
                    ShopName = mapping.ShopName,
                    Items = new List<NpcShopItemRow>()
                };
            }

            EnsureTravelShopStorage(runtime.TravelShopField, 40);

            var discoveredItemIds = new HashSet<int>();
            bool setupTravelShopFailed = false;
            for (int sample = 0; sample < TravelShopSamplingRuns; sample++)
            {
                string failureReason;
                if (!TryInvokeStaticMethodWithDefaults(runtime.SetupTravelShopMethod, out failureReason))
                {
                    Console.WriteLine("[npc-shops] SetupTravelShop() failed: " + failureReason);
                    setupTravelShopFailed = true;
                    break;
                }

                Array travelShop = runtime.TravelShopField.GetValue(null) as Array;
                if (travelShop == null)
                {
                    continue;
                }

                for (int slot = 0; slot < travelShop.Length; slot++)
                {
                    object value = travelShop.GetValue(slot);
                    if (value == null)
                    {
                        continue;
                    }

                    int itemId;
                    try
                    {
                        itemId = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                    }
                    catch
                    {
                        continue;
                    }

                    if (itemId > 0)
                    {
                        discoveredItemIds.Add(itemId);
                    }
                }
            }

            if (discoveredItemIds.Count == 0)
            {
                int countBeforeFallback = discoveredItemIds.Count;
                PopulateTravelShopByMethodConstants(runtime, discoveredItemIds);
                if (setupTravelShopFailed || discoveredItemIds.Count > countBeforeFallback)
                {
                    Console.WriteLine("[npc-shops] Travelling Merchant fallback added " + (discoveredItemIds.Count - countBeforeFallback) + " items via method IL constants");
                }
            }

            var items = new List<NpcShopItemRow>();
            foreach (int itemId in discoveredItemIds.OrderBy(id => id))
            {
                object item = Activator.CreateInstance(runtime.ItemType);
                runtime.ItemSetDefaultsMethod.Invoke(item, new object[] { itemId });

                items.Add(new NpcShopItemRow
                {
                    ItemId = itemId,
                    Name = runtime.GetItemName(itemId),
                    BuyPrice = (int)runtime.ItemGetStoreValueMethod.Invoke(item, null)
                });
            }

            Console.WriteLine("[npc-shops] Travelling Merchant sampled " + TravelShopSamplingRuns + " rolls and found " + items.Count + " unique items");

            return new NpcShopRow
            {
                NpcId = mapping.NpcId,
                NpcName = runtime.GetNpcName(mapping.NpcId),
                ShopName = mapping.ShopName,
                Items = items
            };
        }

        // === Zoologist IL Fallback ===

        private static NpcShopRow ExtractZoologistShopFromSetupShopIL(ShopMapping mapping, TerrariaRuntime runtime)
        {
            var discoveredItemIds = new HashSet<int>();
            PopulateZoologistShopBySetupShopIL(runtime, discoveredItemIds);
            if (discoveredItemIds.Count == 0)
            {
                return null;
            }

            var items = new List<NpcShopItemRow>();
            foreach (int itemId in discoveredItemIds.OrderBy(id => id))
            {
                object item = Activator.CreateInstance(runtime.ItemType);
                runtime.ItemSetDefaultsMethod.Invoke(item, new object[] { itemId });

                items.Add(new NpcShopItemRow
                {
                    ItemId = itemId,
                    Name = runtime.GetItemName(itemId),
                    BuyPrice = (int)runtime.ItemGetStoreValueMethod.Invoke(item, null)
                });
            }

            return new NpcShopRow
            {
                NpcId = mapping.NpcId,
                NpcName = runtime.GetNpcName(mapping.NpcId),
                ShopName = mapping.ShopName,
                Items = items
            };
        }

        private static void PopulateZoologistShopBySetupShopIL(TerrariaRuntime runtime, HashSet<int> destination)
        {
            if (runtime == null || runtime.SetupShopMethod == null || runtime.ItemSetDefaultsMethod == null)
            {
                return;
            }

            List<IlInstruction> instructions = IlParser.ParseIlInstructions(runtime.SetupShopMethod);
            if (instructions.Count == 0)
            {
                return;
            }

            int caseStart;
            int caseEnd;
            if (!TryResolveCaseRangeContainingBestiaryCall(runtime.SetupShopMethod, instructions, out caseStart, out caseEnd))
            {
                return;
            }

            int setDefaultsToken = runtime.ItemSetDefaultsMethod.MetadataToken;
            for (int i = 0; i < instructions.Count; i++)
            {
                IlInstruction instruction = instructions[i];
                if (instruction.Offset < caseStart || instruction.Offset >= caseEnd)
                {
                    continue;
                }

                short opcode = instruction.OpCode.Value;
                if (opcode != OpCodes.Call.Value && opcode != OpCodes.Callvirt.Value)
                {
                    continue;
                }

                if (!instruction.Int32Operand.HasValue || instruction.Int32Operand.Value != setDefaultsToken)
                {
                    continue;
                }

                int itemId;
                if (!IlParser.TryFindClosestIntConstant(instructions, i, caseStart, out itemId))
                {
                    continue;
                }

                if (itemId < 1)
                {
                    continue;
                }

                if (runtime.ItemIdCount > 0 && itemId > runtime.ItemIdCount)
                {
                    continue;
                }

                destination.Add(itemId);
            }
        }

        private static bool TryResolveCaseRangeContainingBestiaryCall(
            MethodInfo setupShopMethod,
            List<IlInstruction> instructions,
            out int caseStart,
            out int caseEnd)
        {
            caseStart = 0;
            caseEnd = 0;

            MethodBody body = setupShopMethod.GetMethodBody();
            if (body == null)
            {
                return false;
            }

            byte[] il = body.GetILAsByteArray();
            if (il == null || il.Length == 0)
            {
                return false;
            }

            Type mainType = setupShopMethod.DeclaringType.Assembly.GetType("Terraria.Main", throwOnError: false);
            if (mainType == null)
            {
                return false;
            }

            const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            MethodInfo bestiaryMethod = mainType.GetMethod("GetBestiaryProgressReport", Flags);
            if (bestiaryMethod == null)
            {
                return false;
            }

            int bestiaryToken = bestiaryMethod.MetadataToken;
            int callOffset;
            if (!IlParser.TryFindMethodCallOffset(instructions, bestiaryToken, out callOffset))
            {
                return false;
            }

            if (IlParser.TryResolveCaseRangeByTypeComparison(instructions, il.Length, 23, callOffset, out caseStart, out caseEnd))
            {
                return true;
            }

            int switchInstructionIndex;
            IlInstruction switchInstruction = IlParser.FindPrimarySwitchInstruction(instructions, out switchInstructionIndex);
            if (switchInstruction == null)
            {
                return false;
            }

            int directCaseStart;
            int directCaseEnd;
            if (IlParser.TryResolveSwitchCaseRangeByTypeValue(instructions, switchInstructionIndex, switchInstruction, il.Length, 23, out directCaseStart, out directCaseEnd)
                && callOffset >= directCaseStart
                && callOffset < directCaseEnd)
            {
                caseStart = directCaseStart;
                caseEnd = directCaseEnd;
                return true;
            }

            int[] boundaries = switchInstruction.SwitchTargets
                .Where(target => target >= 0 && target < il.Length)
                .Distinct()
                .OrderBy(target => target)
                .ToArray();

            if (boundaries.Length == 0)
            {
                return false;
            }

            for (int i = 0; i < boundaries.Length; i++)
            {
                int start = boundaries[i];
                int end = i + 1 < boundaries.Length ? boundaries[i + 1] : il.Length;
                if (callOffset < start || callOffset >= end)
                {
                    continue;
                }

                caseStart = start;
                caseEnd = end;
                return true;
            }

            return false;
        }

        private static void PopulateTravelShopByMethodConstants(TerrariaRuntime runtime, HashSet<int> destination)
        {
            const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            MethodInfo[] travelMethods = runtime.ChestType
                .GetMethods(Flags)
                .Where(method =>
                    method.Name.IndexOf("SetupTravelShop_", StringComparison.Ordinal) >= 0
                    && (method.Name.IndexOf("GetItem", StringComparison.Ordinal) >= 0
                        || method.Name.IndexOf("GetPainting", StringComparison.Ordinal) >= 0
                        || method.Name.IndexOf("AddToShop", StringComparison.Ordinal) >= 0))
                .ToArray();

            foreach (MethodInfo method in travelMethods)
            {
                foreach (int value in IlParser.ExtractIntConstantsFromMethodBody(method))
                {
                    if (!IsLikelyTravelShopItemId(value, runtime.ItemIdCount))
                    {
                        continue;
                    }

                    destination.Add(value);
                }
            }
        }

        private static bool IsLikelyTravelShopItemId(int itemId, int itemIdCount)
        {
            if (itemId <= 0)
            {
                return false;
            }

            if (itemIdCount > 0 && itemId > itemIdCount)
            {
                return false;
            }

            return itemId >= 1000;
        }

        private static void EnsureTravelShopStorage(FieldInfo travelShopField, int minimumLength)
        {
            if (travelShopField == null || travelShopField.FieldType != typeof(int[]))
            {
                return;
            }

            int[] existing = travelShopField.GetValue(null) as int[];
            if (existing != null && existing.Length >= minimumLength)
            {
                return;
            }

            travelShopField.SetValue(null, new int[minimumLength]);
        }

        private static NpcShopRow ExtractShopFromSource(ShopMapping mapping, TerrariaRuntime runtime, ParsedShopSource parsedShopSource)
        {
            HashSet<int> itemIds;
            if (!parsedShopSource.ItemIdsByShop.TryGetValue(mapping.ShopId, out itemIds) || itemIds.Count == 0)
            {
                return null;
            }

            var items = new List<NpcShopItemRow>();
            foreach (int itemId in itemIds.OrderBy(id => id))
            {
                object item = Activator.CreateInstance(runtime.ItemType);
                runtime.ItemSetDefaultsMethod.Invoke(item, new object[] { itemId });

                var row = new NpcShopItemRow
                {
                    ItemId = itemId,
                    Name = runtime.GetItemName(itemId),
                    BuyPrice = (int)runtime.ItemGetStoreValueMethod.Invoke(item, null)
                };

                AddParsedConditions(mapping.ShopId, itemId, parsedShopSource.ConditionsByShop, row.Conditions);
                items.Add(row);
            }

            return new NpcShopRow
            {
                NpcId = mapping.NpcId,
                NpcName = runtime.GetNpcName(mapping.NpcId),
                ShopName = mapping.ShopName,
                Items = items
            };
        }

        private static void AddParsedConditions(
            int shopId,
            int itemId,
            Dictionary<int, Dictionary<int, List<string>>> extractedConditionsByShop,
            List<string> destination)
        {
            Dictionary<int, List<string>> conditionsByItem;
            if (!extractedConditionsByShop.TryGetValue(shopId, out conditionsByItem))
            {
                return;
            }

            List<string> parsedConditions;
            if (!conditionsByItem.TryGetValue(itemId, out parsedConditions))
            {
                return;
            }

            foreach (string condition in parsedConditions)
            {
                if (!destination.Contains(condition))
                {
                    destination.Add(condition);
                }
            }
        }

        private static Func<int, string> BuildNpcNameResolver(Type langType, Type npcIdType)
        {
            MethodInfo getNpcNameMethod = langType.GetMethod("GetNPCNameValue", BindingFlags.Public | BindingFlags.Static);
            object npcSearch = null;
            MethodInfo npcSearchGetNameMethod = null;
            if (npcIdType != null)
            {
                FieldInfo searchField = npcIdType.GetField("Search", BindingFlags.Public | BindingFlags.Static);
                npcSearch = searchField == null ? null : searchField.GetValue(null);
                if (npcSearch != null)
                {
                    npcSearchGetNameMethod = npcSearch.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .FirstOrDefault(m => m.Name == "GetName" && m.GetParameters().Length == 1);
                }
            }

            return delegate(int npcId)
            {
                if (getNpcNameMethod != null)
                {
                    try
                    {
                        string name = Convert.ToString(getNpcNameMethod.Invoke(null, new object[] { npcId }), CultureInfo.InvariantCulture);
                        if (!string.IsNullOrWhiteSpace(name)) return name;
                    }
                    catch { }
                }

                if (npcSearch != null && npcSearchGetNameMethod != null)
                {
                    try
                    {
                        object typedId = Convert.ChangeType(npcId, npcSearchGetNameMethod.GetParameters()[0].ParameterType, CultureInfo.InvariantCulture);
                        string name = Convert.ToString(npcSearchGetNameMethod.Invoke(npcSearch, new[] { typedId }), CultureInfo.InvariantCulture);
                        if (!string.IsNullOrWhiteSpace(name)) return name;
                    }
                    catch { }
                }

                return "NPC " + npcId.ToString(CultureInfo.InvariantCulture);
            };
        }

        // === Bootstrap Runtime ===

        private static TerrariaRuntime BootstrapRuntime(Assembly terrariaAssembly)
        {
            Type mainType = ReflectionHelpers.GetRequiredType(terrariaAssembly, "Terraria.Main");
            Type chestType = ReflectionHelpers.GetRequiredType(terrariaAssembly, "Terraria.Chest");
            Type itemType = ReflectionHelpers.GetRequiredType(terrariaAssembly, "Terraria.Item");
            Type playerType = ReflectionHelpers.GetRequiredType(terrariaAssembly, "Terraria.Player");
            Type npcType = ReflectionHelpers.GetRequiredType(terrariaAssembly, "Terraria.NPC");
            Type langType = ReflectionHelpers.GetRequiredType(terrariaAssembly, "Terraria.Lang");
            Type itemIdType = terrariaAssembly.GetType("Terraria.ID.ItemID", throwOnError: false);
            Type npcIdType = terrariaAssembly.GetType("Terraria.ID.NPCID", throwOnError: false);

            FieldInfo playerField = mainType.GetField("player", BindingFlags.Public | BindingFlags.Static);
            FieldInfo npcField = mainType.GetField("npc", BindingFlags.Public | BindingFlags.Static);
            FieldInfo myPlayerField = mainType.GetField("myPlayer", BindingFlags.Public | BindingFlags.Static);

            if (playerField == null || npcField == null || myPlayerField == null)
            {
                throw new InvalidOperationException("Main static fields were not found.");
            }

            Array players = Array.CreateInstance(playerType, 255);
            object localPlayer = Activator.CreateInstance(playerType);
            InitializePlayerInventory(localPlayer, itemType);
            players.SetValue(localPlayer, 0);
            playerField.SetValue(null, players);

            Array npcs = Array.CreateInstance(npcType, 200);
            for (int i = 0; i < npcs.Length; i++)
            {
                npcs.SetValue(CreateInstanceWithoutConstructor(npcType), i);
            }

            npcField.SetValue(null, npcs);
            myPlayerField.SetValue(null, 0);

            SetIfPresent(mainType, "worldSurface", 250.0);
            SetIfPresent(mainType, "rockLayer", 400.0);
            SetIfPresent(mainType, "maxTilesX", 8400);
            SetIfPresent(mainType, "maxTilesY", 2400);
            SetIfPresent(mainType, "screenWidth", 1920);
            SetIfPresent(mainType, "screenHeight", 1080);
            SetIfPresent(mainType, "buffScanAreaWidth", 170);
            SetIfPresent(mainType, "buffScanAreaHeight", 125);
            SetIfPresent(mainType, "hardMode", true);

            FieldInfo mainRandField = mainType.GetField("rand", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            Type unifiedRandomType = terrariaAssembly.GetType("Terraria.Utilities.UnifiedRandom", throwOnError: false);
            if (mainRandField != null && unifiedRandomType != null && mainRandField.GetValue(null) == null)
            {
                object mainRandom = CreateUnifiedRandomInstance(unifiedRandomType, 0);
                if (mainRandom != null)
                {
                    mainRandField.SetValue(null, mainRandom);
                }
            }

            SetIfPresent(npcType, "downedBoss1", true);
            SetIfPresent(npcType, "downedBoss2", true);
            SetIfPresent(npcType, "downedBoss3", true);
            SetIfPresent(npcType, "downedQueenBee", true);
            SetIfPresent(npcType, "downedMechBoss1", true);
            SetIfPresent(npcType, "downedMechBoss2", true);
            SetIfPresent(npcType, "downedMechBoss3", true);
            SetIfPresent(npcType, "downedMechBossAny", true);
            SetIfPresent(npcType, "downedPlantBoss", true);
            SetIfPresent(npcType, "downedGolemBoss", true);
            SetIfPresent(npcType, "downedAncientCultist", true);
            SetIfPresent(npcType, "downedMoonlord", true);
            SetIfPresent(npcType, "downedFishron", true);

            MethodInfo chestInitializeMethod = chestType.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static);
            if (chestInitializeMethod != null)
            {
                chestInitializeMethod.Invoke(null, null);
            }

            MethodInfo setupShopMethod = chestType.GetMethod("SetupShop", BindingFlags.Public | BindingFlags.Instance);
            FieldInfo chestItemsField = chestType.GetField("item", BindingFlags.Public | BindingFlags.Instance);
            FieldInfo itemTypeField = itemType.GetField("type", BindingFlags.Public | BindingFlags.Instance);
            FieldInfo itemSpecialCurrencyField = itemType.GetField("shopSpecialCurrency", BindingFlags.Public | BindingFlags.Instance);
            PropertyInfo itemIsAirProperty = itemType.GetProperty("IsAir", BindingFlags.Public | BindingFlags.Instance);
            MethodInfo itemGetStoreValueMethod = itemType.GetMethod("GetStoreValue", BindingFlags.Public | BindingFlags.Instance);
            MethodInfo itemSetDefaultsMethod = itemType.GetMethod("SetDefaults", new[] { typeof(int) });
            FieldInfo itemCountField = itemIdType == null ? null : itemIdType.GetField("Count", BindingFlags.Public | BindingFlags.Static);

            if (setupShopMethod == null || chestItemsField == null || itemTypeField == null
                || itemSpecialCurrencyField == null || itemIsAirProperty == null
                || itemGetStoreValueMethod == null || itemSetDefaultsMethod == null)
            {
                throw new InvalidOperationException("Required Terraria members for NPC shop extraction were not found.");
            }

            Func<int, string> getItemName = ItemNameResolver.Build(terrariaAssembly);
            Func<int, string> getNpcName = BuildNpcNameResolver(langType, npcIdType);

            return new TerrariaRuntime
            {
                ChestType = chestType,
                SetupShopMethod = setupShopMethod,
                SetupTravelShopMethod = ResolveSetupTravelShopMethod(chestType),
                ChestItemsField = chestItemsField,
                TravelShopField = ResolveTravelShopField(mainType, chestType),
                ItemType = itemType,
                ItemTypeField = itemTypeField,
                ItemSpecialCurrencyField = itemSpecialCurrencyField,
                ItemIsAirProperty = itemIsAirProperty,
                ItemGetStoreValueMethod = itemGetStoreValueMethod,
                ItemSetDefaultsMethod = itemSetDefaultsMethod,
                ItemIdCount = itemCountField == null ? 0 : Convert.ToInt32(itemCountField.GetValue(null), CultureInfo.InvariantCulture),
                GetItemName = getItemName,
                GetNpcName = getNpcName
            };
        }

        private static object CreateUnifiedRandomInstance(Type unifiedRandomType, int seed)
        {
            if (unifiedRandomType == null)
            {
                return null;
            }

            ConstructorInfo seededConstructor = unifiedRandomType.GetConstructor(new[] { typeof(int) });
            if (seededConstructor != null)
            {
                return seededConstructor.Invoke(new object[] { seed });
            }

            ConstructorInfo parameterlessConstructor = unifiedRandomType.GetConstructor(Type.EmptyTypes);
            if (parameterlessConstructor != null)
            {
                return parameterlessConstructor.Invoke(null);
            }

            return null;
        }

        private static FieldInfo ResolveTravelShopField(Type mainType, Type chestType)
        {
            const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

            FieldInfo travelShopField = ResolveTravelShopFieldFromType(mainType, Flags);
            if (travelShopField != null)
            {
                return travelShopField;
            }

            return ResolveTravelShopFieldFromType(chestType, Flags);
        }

        private static FieldInfo ResolveTravelShopFieldFromType(Type type, BindingFlags flags)
        {
            if (type == null)
            {
                return null;
            }

            FieldInfo travelShopField = type.GetField("travelShop", flags);
            if (travelShopField != null && travelShopField.FieldType == typeof(int[]))
            {
                return travelShopField;
            }

            FieldInfo nameHintField = type
                .GetFields(flags)
                .FirstOrDefault(field =>
                    field.FieldType == typeof(int[])
                    && field.Name.IndexOf("travel", StringComparison.OrdinalIgnoreCase) >= 0
                    && field.Name.IndexOf("shop", StringComparison.OrdinalIgnoreCase) >= 0);
            if (nameHintField != null)
            {
                return nameHintField;
            }

            return type
                .GetFields(flags)
                .Where(field => field.FieldType == typeof(int[]))
                .FirstOrDefault(field =>
                {
                    int[] values = field.GetValue(null) as int[];
                    return values != null && values.Length >= 10;
                });
        }

        private static MethodInfo ResolveSetupTravelShopMethod(Type chestType)
        {
            const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

            var methods = chestType
                .GetMethods(Flags)
                .Where(method => string.Equals(method.Name, "SetupTravelShop", StringComparison.Ordinal))
                .OrderBy(method => method.GetParameters().Count(parameter => !parameter.IsOptional && !parameter.IsOut && !parameter.ParameterType.IsByRef))
                .ThenBy(method => method.GetParameters().Length)
                .ToList();

            if (methods.Count == 0)
            {
                return null;
            }

            return methods[0];
        }

        private static bool TryInvokeStaticMethodWithDefaults(MethodInfo method, out string failureReason)
        {
            failureReason = null;
            if (method == null)
            {
                failureReason = "method not found";
                return false;
            }

            object[] args = BuildMethodArguments(method);
            try
            {
                method.Invoke(null, args);
                return true;
            }
            catch (Exception ex)
            {
                Exception root = ex.InnerException ?? ex;
                failureReason = root.Message;
                return false;
            }
        }

        private static object[] BuildMethodArguments(MethodInfo method)
        {
            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length == 0)
            {
                return null;
            }

            var args = new object[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                ParameterInfo parameter = parameters[i];
                Type argumentType = parameter.ParameterType.IsByRef
                    ? parameter.ParameterType.GetElementType()
                    : parameter.ParameterType;

                if (parameter.IsOptional && parameter.DefaultValue != DBNull.Value)
                {
                    args[i] = parameter.DefaultValue;
                }
                else
                {
                    args[i] = GetDefaultValue(argumentType);
                }
            }

            return args;
        }

        private static object GetDefaultValue(Type type)
        {
            if (type == null || !type.IsValueType)
            {
                return null;
            }

            return Activator.CreateInstance(type);
        }

        private static void InitializePlayerInventory(object player, Type itemType)
        {
            if (player == null)
            {
                return;
            }

            FieldInfo inventoryField = player.GetType().GetField("inventory", BindingFlags.Public | BindingFlags.Instance);
            if (inventoryField == null)
            {
                return;
            }

            Array inventory = inventoryField.GetValue(player) as Array;
            if (inventory == null)
            {
                return;
            }

            for (int i = 0; i < inventory.Length; i++)
            {
                if (inventory.GetValue(i) == null)
                {
                    inventory.SetValue(Activator.CreateInstance(itemType), i);
                }
            }
        }

        private static object CreateInstanceWithoutConstructor(Type type)
        {
            try
            {
                return Activator.CreateInstance(type);
            }
            catch
            {
                return FormatterServices.GetUninitializedObject(type);
            }
        }

        private static void SetIfPresent(Type type, string fieldName, object value)
        {
            FieldInfo field = type.GetField(fieldName, BindingFlags.Public | BindingFlags.Static);
            if (field != null)
            {
                field.SetValue(null, value);
            }
        }

        // === Shop Mappings ===

        private static ShopMapping[] GetShopMappings()
        {
            return new[]
            {
                new ShopMapping { ShopId = 1, NpcId = 17, ShopName = "Shop 1" },
                new ShopMapping { ShopId = 2, NpcId = 19, ShopName = "Shop 2" },
                new ShopMapping { ShopId = 3, NpcId = 20, ShopName = "Shop 3" },
                new ShopMapping { ShopId = 4, NpcId = 38, ShopName = "Shop 4" },
                new ShopMapping { ShopId = 5, NpcId = 54, ShopName = "Shop 5" },
                new ShopMapping { ShopId = 6, NpcId = 107, ShopName = "Shop 6" },
                new ShopMapping { ShopId = 7, NpcId = 108, ShopName = "Shop 7" },
                new ShopMapping { ShopId = 8, NpcId = 124, ShopName = "Shop 8" },
                new ShopMapping { ShopId = 9, NpcId = 142, ShopName = "Shop 9" },
                new ShopMapping { ShopId = 10, NpcId = 160, ShopName = "Shop 10" },
                new ShopMapping { ShopId = 11, NpcId = 178, ShopName = "Shop 11" },
                new ShopMapping { ShopId = 12, NpcId = 207, ShopName = "Shop 12" },
                new ShopMapping { ShopId = 13, NpcId = 208, ShopName = "Shop 13" },
                new ShopMapping { ShopId = 14, NpcId = 209, ShopName = "Shop 14" },
                new ShopMapping { ShopId = 15, NpcId = 227, ShopName = "Shop 15" },
                new ShopMapping { ShopId = 16, NpcId = 228, ShopName = "Shop 16" },
                new ShopMapping { ShopId = 17, NpcId = 229, ShopName = "Shop 17" },
                new ShopMapping { ShopId = 18, NpcId = 353, ShopName = "Shop 18" },
                new ShopMapping { ShopId = 19, NpcId = 368, ShopName = "Shop 19", UseTravelShop = true },
                new ShopMapping { ShopId = 20, NpcId = 453, ShopName = "Shop 20" },
                new ShopMapping { ShopId = 21, NpcId = 550, ShopName = "Shop 21" },
                new ShopMapping { ShopId = 22, NpcId = 588, ShopName = "Shop 22" },
                new ShopMapping { ShopId = 23, NpcId = 633, ShopName = "Shop 23", UseZoologistILFallback = true },
                new ShopMapping { ShopId = 24, NpcId = 663, ShopName = "Shop 24" },
                new ShopMapping { ShopId = 25, NpcId = 227, ShopName = "Shop 25" }
            };
        }

        // === Decompiled Source Parsing ===

        private static ParsedShopSource ParseShopSourceData()
        {
            string chestSourcePath = ResolveChestSourcePath();
            if (string.IsNullOrWhiteSpace(chestSourcePath) || !File.Exists(chestSourcePath))
            {
                return new ParsedShopSource
                {
                    ConditionsByShop = new Dictionary<int, Dictionary<int, List<string>>>(),
                    ItemIdsByShop = new Dictionary<int, HashSet<int>>()
                };
            }

            string[] lines = File.ReadAllLines(chestSourcePath);
            int methodStart = Array.FindIndex(lines, l => l.Contains("public void SetupShop(int type)"));
            if (methodStart < 0)
            {
                return new ParsedShopSource
                {
                    ConditionsByShop = new Dictionary<int, Dictionary<int, List<string>>>(),
                    ItemIdsByShop = new Dictionary<int, HashSet<int>>()
                };
            }

            var conditionsByShop = new Dictionary<int, Dictionary<int, List<string>>>();
            var itemIdsByShop = new Dictionary<int, HashSet<int>>();
            var activeConditions = new Stack<ConditionScope>();
            int braceDepth = 0;
            int currentShopId = -1;
            int switchTypeDepth = -1;
            bool waitingForTypeSwitchBrace = false;
            string pendingCondition = null;

            for (int i = methodStart; i < lines.Length; i++)
            {
                string line = lines[i];
                string trimmed = line.Trim();

                if (switchTypeDepth < 0 && trimmed.StartsWith("switch (type)", StringComparison.Ordinal))
                {
                    waitingForTypeSwitchBrace = true;
                }

                Match caseMatch = CaseRegex.Match(trimmed);
                if (caseMatch.Success && switchTypeDepth >= 0 && braceDepth == switchTypeDepth)
                {
                    currentShopId = int.Parse(caseMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                    activeConditions.Clear();
                    pendingCondition = null;
                }

                Match ifMatch = IfRegex.Match(trimmed);
                if (ifMatch.Success)
                {
                    pendingCondition = NormalizeCondition(ifMatch.Groups[1].Value);
                }

                if (currentShopId > 0)
                {
                    foreach (Match itemMatch in SetDefaultsRegex.Matches(line))
                    {
                        int itemId = int.Parse(itemMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                        AddItemRecord(itemIdsByShop, currentShopId, itemId);
                        AddConditionRecord(conditionsByShop, currentShopId, itemId, activeConditions);
                    }
                }

                foreach (char c in line)
                {
                    if (c == '{')
                    {
                        braceDepth++;
                        if (waitingForTypeSwitchBrace)
                        {
                            switchTypeDepth = braceDepth;
                            waitingForTypeSwitchBrace = false;
                        }

                        if (!string.IsNullOrWhiteSpace(pendingCondition))
                        {
                            activeConditions.Push(new ConditionScope { Expression = pendingCondition, Depth = braceDepth });
                            pendingCondition = null;
                        }
                    }
                    else if (c == '}')
                    {
                        while (activeConditions.Count > 0 && activeConditions.Peek().Depth == braceDepth)
                        {
                            activeConditions.Pop();
                        }

                        braceDepth--;
                        if (braceDepth < switchTypeDepth)
                        {
                            switchTypeDepth = -1;
                            currentShopId = -1;
                        }
                    }
                }

                if (braceDepth <= 0 && i > methodStart)
                {
                    break;
                }
            }

            return new ParsedShopSource
            {
                ConditionsByShop = conditionsByShop,
                ItemIdsByShop = itemIdsByShop
            };
        }

        private static void AddItemRecord(IDictionary<int, HashSet<int>> itemIdsByShop, int shopId, int itemId)
        {
            HashSet<int> itemIds;
            if (!itemIdsByShop.TryGetValue(shopId, out itemIds))
            {
                itemIds = new HashSet<int>();
                itemIdsByShop[shopId] = itemIds;
            }

            itemIds.Add(itemId);
        }

        private static void AddConditionRecord(
            IDictionary<int, Dictionary<int, List<string>>> result,
            int shopId,
            int itemId,
            IEnumerable<ConditionScope> activeConditions)
        {
            var orderedConditions = activeConditions.Reverse().Select(c => c.Expression).ToList();
            if (orderedConditions.Count == 0)
            {
                return;
            }

            Dictionary<int, List<string>> byItem;
            if (!result.TryGetValue(shopId, out byItem))
            {
                byItem = new Dictionary<int, List<string>>();
                result[shopId] = byItem;
            }

            List<string> conditions;
            if (!byItem.TryGetValue(itemId, out conditions))
            {
                conditions = new List<string>();
                byItem[itemId] = conditions;
            }

            foreach (string condition in orderedConditions)
            {
                if (!conditions.Contains(condition))
                {
                    conditions.Add(condition);
                }
            }
        }

        private static string NormalizeCondition(string condition)
        {
            if (string.IsNullOrWhiteSpace(condition))
            {
                return string.Empty;
            }

            string normalized = condition.Trim();
            normalized = normalized.Replace("Main.player[Main.myPlayer].", "player.");
            normalized = normalized.Replace("Main.LocalPlayer.", "player.");
            normalized = Regex.Replace(normalized, "\\s+", " ");
            return normalized;
        }

        private static string ResolveChestSourcePath()
        {
            var directory = new DirectoryInfo(Environment.CurrentDirectory);
            while (directory != null)
            {
                string candidateDirect = Path.Combine(directory.FullName, "decompiled", "Terraria", "Chest.cs");
                if (File.Exists(candidateDirect))
                {
                    return candidateDirect;
                }

                string candidateNested = Path.Combine(directory.FullName, "extract-mod", "decompiled", "Terraria", "Chest.cs");
                if (File.Exists(candidateNested))
                {
                    return candidateNested;
                }

                directory = directory.Parent;
            }

            return null;
        }

        // === Nested Types ===

        private sealed class ConditionScope
        {
            public string Expression;
            public int Depth;
        }

        private sealed class ParsedShopSource
        {
            public Dictionary<int, Dictionary<int, List<string>>> ConditionsByShop;
            public Dictionary<int, HashSet<int>> ItemIdsByShop;
        }

        private sealed class ShopMapping
        {
            public int ShopId;
            public int NpcId;
            public string ShopName;
            public bool UseTravelShop;
            public bool UseZoologistILFallback;
        }

        private sealed class TerrariaRuntime
        {
            public Type ChestType;
            public MethodInfo SetupShopMethod;
            public MethodInfo SetupTravelShopMethod;
            public FieldInfo ChestItemsField;
            public FieldInfo TravelShopField;
            public Type ItemType;
            public FieldInfo ItemTypeField;
            public FieldInfo ItemSpecialCurrencyField;
            public PropertyInfo ItemIsAirProperty;
            public MethodInfo ItemGetStoreValueMethod;
            public MethodInfo ItemSetDefaultsMethod;
            public int ItemIdCount;
            public Func<int, string> GetItemName;
            public Func<int, string> GetNpcName;
        }
    }
}
