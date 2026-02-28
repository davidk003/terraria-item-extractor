using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using StandaloneExtractor.Models;

namespace StandaloneExtractor.Extractors
{
    public sealed class NpcShopDataExtractor : IExtractorPhase<NpcShopRow>
    {
        private const string DefaultTerrariaExePath = @"C:\Program Files (x86)\Steam\steamapps\common\Terraria\Terraria.exe";
        private static readonly object AssemblyResolveLock = new object();
        private static bool _assemblyResolveRegistered;
        private static string _terrariaDirectoryForResolve;
        private static string _decompiledDirectoryForResolve;
        private static readonly Regex IfRegex = new Regex(@"^(?:else\s+)?if\s*\((.+)\)", RegexOptions.Compiled);
        private static readonly Regex CaseRegex = new Regex(@"^case\s+(\d+)\s*:", RegexOptions.Compiled);
        private static readonly Regex SetDefaultsRegex = new Regex(@"\.SetDefaults\((\d+)\)", RegexOptions.Compiled);

        public string PhaseName
        {
            get { return "npc-shops"; }
        }

        public IEnumerable<NpcShopRow> Extract(ExtractionContext context)
        {
            string terrariaExePath = ResolveTerrariaExePath(context.CommandLineArgs);
            if (!File.Exists(terrariaExePath))
            {
                Console.WriteLine("[npc-shops] Terraria.exe not found: " + terrariaExePath);
                return new List<NpcShopRow>();
            }

            Assembly terrariaAssembly;
            try
            {
                terrariaAssembly = LoadTerrariaAssembly(terrariaExePath);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[npc-shops] Failed to load Terraria assembly: " + ex.Message);
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

        private static NpcShopRow ExtractShop(
            ShopMapping mapping,
            TerrariaRuntime runtime,
            ParsedShopSource parsedShopSource)
        {
            object chest = Activator.CreateInstance(runtime.ChestType, new object[] { false });
            try
            {
                runtime.SetupShopMethod.Invoke(chest, new object[] { mapping.ShopId });
            }
            catch (Exception ex)
            {
                string reason = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
                Console.WriteLine("[npc-shops] SetupShop(" + mapping.ShopId + ") failed: " + reason);
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
                        Name = ResolveItemName(runtime, item, itemId),
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
                NpcName = ResolveNpcName(runtime, mapping.NpcId),
                ShopName = mapping.ShopName,
                Items = itemsById.Values.OrderBy(i => i.ItemId).ToList()
            };
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
                    Name = ResolveItemName(runtime, item, itemId),
                    BuyPrice = (int)runtime.ItemGetStoreValueMethod.Invoke(item, null)
                };

                AddParsedConditions(mapping.ShopId, itemId, parsedShopSource.ConditionsByShop, row.Conditions);
                items.Add(row);
            }

            return new NpcShopRow
            {
                NpcId = mapping.NpcId,
                NpcName = ResolveNpcName(runtime, mapping.NpcId),
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

        private static string ResolveItemName(TerrariaRuntime runtime, object item, int itemId)
        {
            string name = Convert.ToString(runtime.ItemNameProperty.GetValue(item, null), CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(name) && !name.StartsWith("ItemName.", StringComparison.Ordinal))
            {
                return name;
            }

            try
            {
                if (runtime.ItemSearch != null && runtime.ItemSearchGetNameMethod != null)
                {
                    ParameterInfo parameter = runtime.ItemSearchGetNameMethod.GetParameters()[0];
                    object typedId = Convert.ChangeType(itemId, parameter.ParameterType, CultureInfo.InvariantCulture);
                    string resolved = Convert.ToString(runtime.ItemSearchGetNameMethod.Invoke(runtime.ItemSearch, new[] { typedId }), CultureInfo.InvariantCulture);
                    if (!string.IsNullOrWhiteSpace(resolved))
                    {
                        return resolved;
                    }
                }
            }
            catch
            {
            }

            return "Item " + itemId.ToString(CultureInfo.InvariantCulture);
        }

        private static string ResolveNpcName(TerrariaRuntime runtime, int npcId)
        {
            try
            {
                string value = Convert.ToString(runtime.GetNpcNameMethod.Invoke(null, new object[] { npcId }), CultureInfo.InvariantCulture);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
            catch
            {
            }

            try
            {
                if (runtime.NpcSearch != null && runtime.NpcSearchGetNameMethod != null)
                {
                    ParameterInfo parameter = runtime.NpcSearchGetNameMethod.GetParameters()[0];
                    object typedId = Convert.ChangeType(npcId, parameter.ParameterType, CultureInfo.InvariantCulture);
                    string value = Convert.ToString(runtime.NpcSearchGetNameMethod.Invoke(runtime.NpcSearch, new[] { typedId }), CultureInfo.InvariantCulture);
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }
            }
            catch
            {
            }

            return "NPC " + npcId.ToString(CultureInfo.InvariantCulture);
        }

        private static TerrariaRuntime BootstrapRuntime(Assembly terrariaAssembly)
        {
            PrepareTerrariaProgramState(terrariaAssembly);

            Type mainType = terrariaAssembly.GetType("Terraria.Main", throwOnError: true);
            Type chestType = terrariaAssembly.GetType("Terraria.Chest", throwOnError: true);
            Type itemType = terrariaAssembly.GetType("Terraria.Item", throwOnError: true);
            Type playerType = terrariaAssembly.GetType("Terraria.Player", throwOnError: true);
            Type npcType = terrariaAssembly.GetType("Terraria.NPC", throwOnError: true);
            Type langType = terrariaAssembly.GetType("Terraria.Lang", throwOnError: true);
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
            PropertyInfo itemNameProperty = itemType.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
            MethodInfo itemGetStoreValueMethod = itemType.GetMethod("GetStoreValue", BindingFlags.Public | BindingFlags.Instance);
            MethodInfo itemSetDefaultsMethod = itemType.GetMethod("SetDefaults", new[] { typeof(int) });
            MethodInfo getNpcNameMethod = langType.GetMethod("GetNPCNameValue", BindingFlags.Public | BindingFlags.Static);
            FieldInfo itemSearchField = itemIdType == null ? null : itemIdType.GetField("Search", BindingFlags.Public | BindingFlags.Static);
            object itemSearch = itemSearchField == null ? null : itemSearchField.GetValue(null);
            MethodInfo itemSearchGetNameMethod = itemSearch == null
                ? null
                : itemSearch.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(method => method.Name == "GetName" && method.GetParameters().Length == 1);
            FieldInfo npcSearchField = npcIdType == null ? null : npcIdType.GetField("Search", BindingFlags.Public | BindingFlags.Static);
            object npcSearch = npcSearchField == null ? null : npcSearchField.GetValue(null);
            MethodInfo npcSearchGetNameMethod = npcSearch == null
                ? null
                : npcSearch.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(method => method.Name == "GetName" && method.GetParameters().Length == 1);

            if (setupShopMethod == null
                || chestItemsField == null
                || itemTypeField == null
                || itemSpecialCurrencyField == null
                || itemIsAirProperty == null
                || itemNameProperty == null
                || itemGetStoreValueMethod == null
                || itemSetDefaultsMethod == null
                || getNpcNameMethod == null)
            {
                throw new InvalidOperationException("Required Terraria members for NPC shop extraction were not found.");
            }

            return new TerrariaRuntime(
                chestType,
                setupShopMethod,
                chestItemsField,
                itemType,
                itemTypeField,
                itemSpecialCurrencyField,
                itemIsAirProperty,
                itemNameProperty,
                itemGetStoreValueMethod,
                itemSetDefaultsMethod,
                getNpcNameMethod,
                itemSearch,
                itemSearchGetNameMethod,
                npcSearch,
                npcSearchGetNameMethod);
        }

        private static void PrepareTerrariaProgramState(Assembly terrariaAssembly)
        {
            Type programType = terrariaAssembly.GetType("Terraria.Program", throwOnError: false);
            if (programType == null)
            {
                return;
            }

            FieldInfo savePathField = programType.GetField("SavePath", BindingFlags.Public | BindingFlags.Static);
            if (savePathField != null)
            {
                string savePath = Path.Combine(Environment.CurrentDirectory, "_terraria-save");
                Directory.CreateDirectory(savePath);
                savePathField.SetValue(null, savePath);
            }

            FieldInfo launchParametersField = programType.GetField("LaunchParameters", BindingFlags.Public | BindingFlags.Static);
            if (launchParametersField != null)
            {
                launchParametersField.SetValue(null, new Dictionary<string, string>());
            }
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

        private static Assembly LoadTerrariaAssembly(string terrariaExePath)
        {
            string terrariaDirectory = Path.GetDirectoryName(terrariaExePath);
            string decompiledDirectory = FindRepositoryDecompiledDirectory();

            lock (AssemblyResolveLock)
            {
                _terrariaDirectoryForResolve = terrariaDirectory;
                _decompiledDirectoryForResolve = decompiledDirectory;
                if (!_assemblyResolveRegistered)
                {
                    AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
                    _assemblyResolveRegistered = true;
                }
            }

            return Assembly.LoadFrom(terrariaExePath);
        }

        private static Assembly OnAssemblyResolve(object sender, ResolveEventArgs args)
        {
            string assemblyName = new AssemblyName(args.Name).Name;

            string terrariaDirectory = _terrariaDirectoryForResolve;
            if (!string.IsNullOrWhiteSpace(terrariaDirectory) && Directory.Exists(terrariaDirectory))
            {
                string dllPath = Path.Combine(terrariaDirectory, assemblyName + ".dll");
                if (File.Exists(dllPath))
                {
                    return Assembly.LoadFrom(dllPath);
                }

                string exePath = Path.Combine(terrariaDirectory, assemblyName + ".exe");
                if (File.Exists(exePath))
                {
                    return Assembly.LoadFrom(exePath);
                }
            }

            string decompiledDirectory = _decompiledDirectoryForResolve;
            if (!string.IsNullOrWhiteSpace(decompiledDirectory) && Directory.Exists(decompiledDirectory))
            {
                foreach (string libraryPath in Directory.GetFiles(decompiledDirectory, "Terraria.Libraries.*." + assemblyName + ".dll"))
                {
                    return Assembly.LoadFrom(libraryPath);
                }
            }

            return null;
        }

        private static string FindRepositoryDecompiledDirectory()
        {
            var roots = new List<string>();
            if (!string.IsNullOrWhiteSpace(AppDomain.CurrentDomain.BaseDirectory))
            {
                roots.Add(AppDomain.CurrentDomain.BaseDirectory);
            }

            if (!string.IsNullOrWhiteSpace(Environment.CurrentDirectory)
                && !roots.Any(path => string.Equals(path, Environment.CurrentDirectory, StringComparison.OrdinalIgnoreCase)))
            {
                roots.Add(Environment.CurrentDirectory);
            }

            foreach (string root in roots)
            {
                DirectoryInfo current = new DirectoryInfo(root);
                while (current != null)
                {
                    string candidatePath = Path.Combine(current.FullName, "decompiled");
                    string relogicLibraryPath = Path.Combine(candidatePath, "Terraria.Libraries.ReLogic.ReLogic.dll");
                    if (File.Exists(relogicLibraryPath))
                    {
                        return candidatePath;
                    }

                    candidatePath = Path.Combine(current.FullName, "extract-mod", "decompiled");
                    relogicLibraryPath = Path.Combine(candidatePath, "Terraria.Libraries.ReLogic.ReLogic.dll");
                    if (File.Exists(relogicLibraryPath))
                    {
                        return candidatePath;
                    }

                    current = current.Parent;
                }
            }

            return null;
        }

        private static string ResolveTerrariaExePath(string[] args)
        {
            if (args != null)
            {
                for (int i = 0; i < args.Length; i++)
                {
                    bool isTerrariaFlag = string.Equals(args[i], "--terraria", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(args[i], "-t", StringComparison.OrdinalIgnoreCase);
                    if (!isTerrariaFlag)
                    {
                        continue;
                    }

                    if (i + 1 < args.Length && !string.IsNullOrWhiteSpace(args[i + 1]))
                    {
                        return Path.GetFullPath(args[i + 1]);
                    }
                }
            }

            return DefaultTerrariaExePath;
        }

        private static IEnumerable<ShopMapping> GetShopMappings()
        {
            return new[]
            {
                new ShopMapping(1, 17, "Shop 1"),
                new ShopMapping(2, 19, "Shop 2"),
                new ShopMapping(3, 20, "Shop 3"),
                new ShopMapping(4, 38, "Shop 4"),
                new ShopMapping(5, 54, "Shop 5"),
                new ShopMapping(6, 107, "Shop 6"),
                new ShopMapping(7, 108, "Shop 7"),
                new ShopMapping(8, 124, "Shop 8"),
                new ShopMapping(9, 142, "Shop 9"),
                new ShopMapping(10, 160, "Shop 10"),
                new ShopMapping(11, 178, "Shop 11"),
                new ShopMapping(12, 207, "Shop 12"),
                new ShopMapping(13, 208, "Shop 13"),
                new ShopMapping(14, 209, "Shop 14"),
                new ShopMapping(15, 227, "Shop 15"),
                new ShopMapping(16, 228, "Shop 16"),
                new ShopMapping(17, 229, "Shop 17"),
                new ShopMapping(18, 353, "Shop 18"),
                new ShopMapping(19, 368, "Shop 19"),
                new ShopMapping(20, 453, "Shop 20"),
                new ShopMapping(21, 550, "Shop 21"),
                new ShopMapping(22, 588, "Shop 22"),
                new ShopMapping(23, 633, "Shop 23"),
                new ShopMapping(24, 663, "Shop 24"),
                new ShopMapping(25, 227, "Shop 25")
            };
        }

        private static ParsedShopSource ParseShopSourceData()
        {
            string chestSourcePath = ResolveChestSourcePath();
            if (string.IsNullOrWhiteSpace(chestSourcePath) || !File.Exists(chestSourcePath))
            {
                return new ParsedShopSource(
                    new Dictionary<int, Dictionary<int, List<string>>>(),
                    new Dictionary<int, HashSet<int>>());
            }

            string[] lines = File.ReadAllLines(chestSourcePath);
            int methodStart = Array.FindIndex(lines, l => l.Contains("public void SetupShop(int type)"));
            if (methodStart < 0)
            {
                return new ParsedShopSource(
                    new Dictionary<int, Dictionary<int, List<string>>>(),
                    new Dictionary<int, HashSet<int>>());
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
                            activeConditions.Push(new ConditionScope(pendingCondition, braceDepth));
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

            return new ParsedShopSource(conditionsByShop, itemIdsByShop);
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

        private sealed class ShopMapping
        {
            public ShopMapping(int shopId, int npcId, string shopName)
            {
                ShopId = shopId;
                NpcId = npcId;
                ShopName = shopName;
            }

            public int ShopId { get; private set; }

            public int NpcId { get; private set; }

            public string ShopName { get; private set; }
        }

        private sealed class ConditionScope
        {
            public ConditionScope(string expression, int depth)
            {
                Expression = expression;
                Depth = depth;
            }

            public string Expression { get; private set; }

            public int Depth { get; private set; }
        }

        private sealed class TerrariaRuntime
        {
            public TerrariaRuntime(
                Type chestType,
                MethodInfo setupShopMethod,
                FieldInfo chestItemsField,
                Type itemType,
                FieldInfo itemTypeField,
                FieldInfo itemSpecialCurrencyField,
                PropertyInfo itemIsAirProperty,
                PropertyInfo itemNameProperty,
                MethodInfo itemGetStoreValueMethod,
                MethodInfo itemSetDefaultsMethod,
                MethodInfo getNpcNameMethod,
                object itemSearch,
                MethodInfo itemSearchGetNameMethod,
                object npcSearch,
                MethodInfo npcSearchGetNameMethod)
            {
                ChestType = chestType;
                SetupShopMethod = setupShopMethod;
                ChestItemsField = chestItemsField;
                ItemType = itemType;
                ItemTypeField = itemTypeField;
                ItemSpecialCurrencyField = itemSpecialCurrencyField;
                ItemIsAirProperty = itemIsAirProperty;
                ItemNameProperty = itemNameProperty;
                ItemGetStoreValueMethod = itemGetStoreValueMethod;
                ItemSetDefaultsMethod = itemSetDefaultsMethod;
                GetNpcNameMethod = getNpcNameMethod;
                ItemSearch = itemSearch;
                ItemSearchGetNameMethod = itemSearchGetNameMethod;
                NpcSearch = npcSearch;
                NpcSearchGetNameMethod = npcSearchGetNameMethod;
            }

            public Type ChestType { get; private set; }

            public MethodInfo SetupShopMethod { get; private set; }

            public FieldInfo ChestItemsField { get; private set; }

            public Type ItemType { get; private set; }

            public FieldInfo ItemTypeField { get; private set; }

            public FieldInfo ItemSpecialCurrencyField { get; private set; }

            public PropertyInfo ItemIsAirProperty { get; private set; }

            public PropertyInfo ItemNameProperty { get; private set; }

            public MethodInfo ItemGetStoreValueMethod { get; private set; }

            public MethodInfo ItemSetDefaultsMethod { get; private set; }

            public MethodInfo GetNpcNameMethod { get; private set; }

            public object ItemSearch { get; private set; }

            public MethodInfo ItemSearchGetNameMethod { get; private set; }

            public object NpcSearch { get; private set; }

            public MethodInfo NpcSearchGetNameMethod { get; private set; }
        }

        private sealed class ParsedShopSource
        {
            public ParsedShopSource(
                Dictionary<int, Dictionary<int, List<string>>> conditionsByShop,
                Dictionary<int, HashSet<int>> itemIdsByShop)
            {
                ConditionsByShop = conditionsByShop;
                ItemIdsByShop = itemIdsByShop;
            }

            public Dictionary<int, Dictionary<int, List<string>>> ConditionsByShop { get; private set; }

            public Dictionary<int, HashSet<int>> ItemIdsByShop { get; private set; }
        }
    }
}
