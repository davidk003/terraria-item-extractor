using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
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
        private const int TravelShopSamplingRuns = 200;
        private static readonly Regex IfRegex = new Regex(@"^(?:else\s+)?if\s*\((.+)\)", RegexOptions.Compiled);
        private static readonly Regex CaseRegex = new Regex(@"^case\s+(\d+)\s*:", RegexOptions.Compiled);
        private static readonly Regex SetDefaultsRegex = new Regex(@"\.SetDefaults\((\d+)\)", RegexOptions.Compiled);
        private static readonly Dictionary<ushort, OpCode> OpCodeByValue = BuildOpCodeByValueMap();

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

        private static NpcShopRow ExtractTravelShop(ShopMapping mapping, TerrariaRuntime runtime)
        {
            if (runtime.SetupTravelShopMethod == null || runtime.TravelShopField == null)
            {
                Console.WriteLine("[npc-shops] SetupTravelShop members were not found; returning empty Travelling Merchant shop.");
                return new NpcShopRow
                {
                    NpcId = mapping.NpcId,
                    NpcName = ResolveNpcName(runtime, mapping.NpcId),
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
                    Name = ResolveItemName(runtime, item, itemId),
                    BuyPrice = (int)runtime.ItemGetStoreValueMethod.Invoke(item, null)
                });
            }

            Console.WriteLine("[npc-shops] Travelling Merchant sampled " + TravelShopSamplingRuns + " rolls and found " + items.Count + " unique items");

            return new NpcShopRow
            {
                NpcId = mapping.NpcId,
                NpcName = ResolveNpcName(runtime, mapping.NpcId),
                ShopName = mapping.ShopName,
                Items = items
            };
        }

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
                    Name = ResolveItemName(runtime, item, itemId),
                    BuyPrice = (int)runtime.ItemGetStoreValueMethod.Invoke(item, null)
                });
            }

            return new NpcShopRow
            {
                NpcId = mapping.NpcId,
                NpcName = ResolveNpcName(runtime, mapping.NpcId),
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

            List<IlInstruction> instructions = ParseIlInstructions(runtime.SetupShopMethod);
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
                if (!TryFindClosestIntConstant(instructions, i, caseStart, out itemId))
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
            if (!TryFindMethodCallOffset(instructions, bestiaryToken, out callOffset))
            {
                return false;
            }

            if (TryResolveCaseRangeByTypeComparison(instructions, il.Length, 23, callOffset, out caseStart, out caseEnd))
            {
                return true;
            }

            int switchInstructionIndex;
            IlInstruction switchInstruction = FindPrimarySwitchInstruction(instructions, out switchInstructionIndex);
            if (switchInstruction == null)
            {
                return false;
            }

            int directCaseStart;
            int directCaseEnd;
            if (TryResolveSwitchCaseRangeByTypeValue(instructions, switchInstructionIndex, switchInstruction, il.Length, 23, out directCaseStart, out directCaseEnd)
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

        private static IlInstruction FindPrimarySwitchInstruction(List<IlInstruction> instructions, out int switchInstructionIndex)
        {
            for (int i = 0; i < instructions.Count; i++)
            {
                IlInstruction instruction = instructions[i];
                if (instruction.OpCode.Value == OpCodes.Switch.Value
                    && instruction.SwitchTargets != null
                    && instruction.SwitchTargets.Length > 0)
                {
                    switchInstructionIndex = i;
                    return instruction;
                }
            }

            switchInstructionIndex = -1;
            return null;
        }

        private static bool TryResolveSwitchCaseRangeByTypeValue(
            List<IlInstruction> instructions,
            int switchInstructionIndex,
            IlInstruction switchInstruction,
            int ilLength,
            int typeValue,
            out int caseStart,
            out int caseEnd)
        {
            caseStart = 0;
            caseEnd = 0;

            if (switchInstruction == null || switchInstruction.SwitchTargets == null || switchInstruction.SwitchTargets.Length == 0)
            {
                return false;
            }

            int caseBase = 0;
            TryResolveSwitchCaseBase(instructions, switchInstructionIndex, out caseBase);

            int switchIndex = typeValue - caseBase;
            if (switchIndex < 0 || switchIndex >= switchInstruction.SwitchTargets.Length)
            {
                return false;
            }

            int start = switchInstruction.SwitchTargets[switchIndex];
            if (start < 0 || start >= ilLength)
            {
                return false;
            }

            int[] orderedTargets = switchInstruction.SwitchTargets
                .Where(target => target >= 0 && target < ilLength)
                .Distinct()
                .OrderBy(target => target)
                .ToArray();
            if (orderedTargets.Length == 0)
            {
                return false;
            }

            int nextBoundary = ilLength;
            foreach (int target in orderedTargets)
            {
                if (target > start)
                {
                    nextBoundary = target;
                    break;
                }
            }

            caseStart = start;
            caseEnd = nextBoundary;
            return true;
        }

        private static bool TryResolveSwitchCaseBase(List<IlInstruction> instructions, int switchInstructionIndex, out int caseBase)
        {
            caseBase = 0;
            if (instructions == null || switchInstructionIndex <= 0)
            {
                return false;
            }

            int lowerBound = Math.Max(0, switchInstructionIndex - 8);
            for (int i = switchInstructionIndex - 1; i >= lowerBound; i--)
            {
                short opcode = instructions[i].OpCode.Value;
                if (opcode != OpCodes.Sub.Value && opcode != OpCodes.Sub_Ovf.Value && opcode != OpCodes.Sub_Ovf_Un.Value)
                {
                    continue;
                }

                if (i - 1 < 0)
                {
                    continue;
                }

                int value;
                if (TryGetLdcI4Value(instructions[i - 1], out value))
                {
                    caseBase = value;
                    return true;
                }
            }

            return false;
        }

        private static bool TryFindMethodCallOffset(List<IlInstruction> instructions, int methodToken, out int offset)
        {
            foreach (IlInstruction instruction in instructions)
            {
                short opcode = instruction.OpCode.Value;
                if (opcode != OpCodes.Call.Value && opcode != OpCodes.Callvirt.Value)
                {
                    continue;
                }

                if (!instruction.Int32Operand.HasValue || instruction.Int32Operand.Value != methodToken)
                {
                    continue;
                }

                offset = instruction.Offset;
                return true;
            }

            offset = -1;
            return false;
        }

        private static bool TryResolveCaseRangeByTypeComparison(
            List<IlInstruction> instructions,
            int ilLength,
            int typeValue,
            int requiredOffset,
            out int caseStart,
            out int caseEnd)
        {
            caseStart = 0;
            caseEnd = 0;

            for (int i = 0; i < instructions.Count; i++)
            {
                int value;
                if (!TryGetLdcI4Value(instructions[i], out value) || value != typeValue)
                {
                    continue;
                }

                int branchIndexLimit = Math.Min(instructions.Count - 1, i + 3);
                for (int branchIndex = i + 1; branchIndex <= branchIndexLimit; branchIndex++)
                {
                    IlInstruction branchInstruction = instructions[branchIndex];
                    if (!branchInstruction.BranchTarget.HasValue)
                    {
                        continue;
                    }

                    int branchTarget = branchInstruction.BranchTarget.Value;
                    if (branchTarget < 0 || branchTarget > ilLength)
                    {
                        continue;
                    }

                    short opcode = branchInstruction.OpCode.Value;
                    if (opcode != OpCodes.Bne_Un.Value
                        && opcode != OpCodes.Bne_Un_S.Value
                        && opcode != OpCodes.Brfalse.Value
                        && opcode != OpCodes.Brfalse_S.Value)
                    {
                        continue;
                    }

                    int fallthroughStart = branchIndex + 1 < instructions.Count
                        ? instructions[branchIndex + 1].Offset
                        : ilLength;
                    if (branchTarget <= fallthroughStart)
                    {
                        continue;
                    }

                    if (requiredOffset < fallthroughStart || requiredOffset >= branchTarget)
                    {
                        continue;
                    }

                    caseStart = fallthroughStart;
                    caseEnd = branchTarget;
                    return true;
                }
            }

            return false;
        }

        private static bool TryFindClosestIntConstant(List<IlInstruction> instructions, int fromIndex, int lowerBoundOffset, out int value)
        {
            value = 0;
            int maxBacktrack = 12;
            int minIndex = Math.Max(0, fromIndex - maxBacktrack);
            for (int i = fromIndex - 1; i >= minIndex; i--)
            {
                IlInstruction candidate = instructions[i];
                if (candidate.Offset < lowerBoundOffset)
                {
                    break;
                }

                if (TryGetLdcI4Value(candidate, out value))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryGetLdcI4Value(IlInstruction instruction, out int value)
        {
            switch (instruction.OpCode.Value)
            {
                case 0x15:
                    value = -1;
                    return true;
                case 0x16:
                    value = 0;
                    return true;
                case 0x17:
                    value = 1;
                    return true;
                case 0x18:
                    value = 2;
                    return true;
                case 0x19:
                    value = 3;
                    return true;
                case 0x1A:
                    value = 4;
                    return true;
                case 0x1B:
                    value = 5;
                    return true;
                case 0x1C:
                    value = 6;
                    return true;
                case 0x1D:
                    value = 7;
                    return true;
                case 0x1E:
                    value = 8;
                    return true;
                case 0x1F:
                case 0x20:
                    if (instruction.Int32Operand.HasValue)
                    {
                        value = instruction.Int32Operand.Value;
                        return true;
                    }

                    break;
            }

            value = 0;
            return false;
        }

        private static List<IlInstruction> ParseIlInstructions(MethodInfo method)
        {
            var instructions = new List<IlInstruction>();
            MethodBody methodBody = method.GetMethodBody();
            if (methodBody == null)
            {
                return instructions;
            }

            byte[] il = methodBody.GetILAsByteArray();
            if (il == null || il.Length == 0)
            {
                return instructions;
            }

            int i = 0;
            while (i < il.Length)
            {
                int offset = i;
                ushort opcodeValue = il[i++];
                if (opcodeValue == 0xFE)
                {
                    if (i >= il.Length)
                    {
                        break;
                    }

                    opcodeValue = (ushort)(0xFE00 | il[i++]);
                }

                OpCode opcode;
                if (!OpCodeByValue.TryGetValue(opcodeValue, out opcode))
                {
                    break;
                }

                int operandStart = i;
                int operandSize = GetOperandSize(opcode.OperandType, il, operandStart);
                if (operandSize < 0 || operandStart + operandSize > il.Length)
                {
                    break;
                }

                int? int32Operand = null;
                int? branchTarget = null;
                int[] switchTargets = null;
                if (opcode.OperandType == OperandType.ShortInlineI)
                {
                    int32Operand = unchecked((sbyte)il[operandStart]);
                }
                else if (opcode.OperandType == OperandType.ShortInlineBrTarget)
                {
                    int relativeTarget = unchecked((sbyte)il[operandStart]);
                    int nextOffset = operandStart + operandSize;
                    branchTarget = nextOffset + relativeTarget;
                }
                else if (opcode.OperandType == OperandType.InlineI
                    || opcode.OperandType == OperandType.InlineMethod
                    || opcode.OperandType == OperandType.InlineField
                    || opcode.OperandType == OperandType.InlineType
                    || opcode.OperandType == OperandType.InlineTok
                    || opcode.OperandType == OperandType.InlineString
                    || opcode.OperandType == OperandType.InlineSig)
                {
                    int32Operand = BitConverter.ToInt32(il, operandStart);
                }
                else if (opcode.OperandType == OperandType.InlineBrTarget)
                {
                    int relativeTarget = BitConverter.ToInt32(il, operandStart);
                    int nextOffset = operandStart + operandSize;
                    branchTarget = nextOffset + relativeTarget;
                }
                else if (opcode.OperandType == OperandType.InlineSwitch)
                {
                    int count = BitConverter.ToInt32(il, operandStart);
                    switchTargets = new int[count];
                    int tableStart = operandStart + 4;
                    int nextOffset = operandStart + operandSize;
                    for (int index = 0; index < count; index++)
                    {
                        int relativeTarget = BitConverter.ToInt32(il, tableStart + index * 4);
                        switchTargets[index] = nextOffset + relativeTarget;
                    }
                }

                instructions.Add(new IlInstruction
                {
                    Offset = offset,
                    OpCode = opcode,
                    Int32Operand = int32Operand,
                    BranchTarget = branchTarget,
                    SwitchTargets = switchTargets
                });

                i += operandSize;
            }

            return instructions;
        }

        private static int GetOperandSize(OperandType operandType, byte[] il, int operandStart)
        {
            switch (operandType)
            {
                case OperandType.InlineNone:
                    return 0;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar:
                    return 1;
                case OperandType.InlineVar:
                    return 2;
                case OperandType.InlineBrTarget:
                case OperandType.InlineField:
                case OperandType.InlineI:
                case OperandType.InlineMethod:
                case OperandType.InlineSig:
                case OperandType.InlineString:
                case OperandType.InlineTok:
                case OperandType.InlineType:
                case OperandType.ShortInlineR:
                    return 4;
                case OperandType.InlineI8:
                case OperandType.InlineR:
                    return 8;
                case OperandType.InlineSwitch:
                    if (operandStart + 4 > il.Length)
                    {
                        return -1;
                    }

                    int count = BitConverter.ToInt32(il, operandStart);
                    return 4 + count * 4;
                default:
                    return -1;
            }
        }

        private static Dictionary<ushort, OpCode> BuildOpCodeByValueMap()
        {
            var map = new Dictionary<ushort, OpCode>();
            foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (field.FieldType != typeof(OpCode))
                {
                    continue;
                }

                var opcode = (OpCode)field.GetValue(null);
                map[unchecked((ushort)opcode.Value)] = opcode;
            }

            return map;
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
                foreach (int value in ExtractIntConstantsFromMethodBody(method))
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

        private static IEnumerable<int> ExtractIntConstantsFromMethodBody(MethodInfo method)
        {
            MethodBody methodBody = method.GetMethodBody();
            if (methodBody == null)
            {
                yield break;
            }

            byte[] il = methodBody.GetILAsByteArray();
            if (il == null || il.Length == 0)
            {
                yield break;
            }

            for (int i = 0; i < il.Length; i++)
            {
                byte opcode = il[i];
                switch (opcode)
                {
                    case 0x15:
                        yield return -1;
                        break;
                    case 0x16:
                        yield return 0;
                        break;
                    case 0x17:
                        yield return 1;
                        break;
                    case 0x18:
                        yield return 2;
                        break;
                    case 0x19:
                        yield return 3;
                        break;
                    case 0x1A:
                        yield return 4;
                        break;
                    case 0x1B:
                        yield return 5;
                        break;
                    case 0x1C:
                        yield return 6;
                        break;
                    case 0x1D:
                        yield return 7;
                        break;
                    case 0x1E:
                        yield return 8;
                        break;
                    case 0x1F:
                        if (i + 1 < il.Length)
                        {
                            i += 1;
                            yield return unchecked((sbyte)il[i]);
                        }
                        break;
                    case 0x20:
                        if (i + 4 < il.Length)
                        {
                            int value = BitConverter.ToInt32(il, i + 1);
                            i += 4;
                            yield return value;
                        }
                        break;
                }
            }
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
            MethodInfo setupTravelShopMethod = ResolveSetupTravelShopMethod(chestType);
            FieldInfo chestItemsField = chestType.GetField("item", BindingFlags.Public | BindingFlags.Instance);
            FieldInfo travelShopField = ResolveTravelShopField(mainType, chestType);
            FieldInfo itemTypeField = itemType.GetField("type", BindingFlags.Public | BindingFlags.Instance);
            FieldInfo itemSpecialCurrencyField = itemType.GetField("shopSpecialCurrency", BindingFlags.Public | BindingFlags.Instance);
            PropertyInfo itemIsAirProperty = itemType.GetProperty("IsAir", BindingFlags.Public | BindingFlags.Instance);
            PropertyInfo itemNameProperty = itemType.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
            MethodInfo itemGetStoreValueMethod = itemType.GetMethod("GetStoreValue", BindingFlags.Public | BindingFlags.Instance);
            MethodInfo itemSetDefaultsMethod = itemType.GetMethod("SetDefaults", new[] { typeof(int) });
            FieldInfo itemCountField = itemIdType == null ? null : itemIdType.GetField("Count", BindingFlags.Public | BindingFlags.Static);
            int itemIdCount = itemCountField == null ? 0 : Convert.ToInt32(itemCountField.GetValue(null), CultureInfo.InvariantCulture);
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
                setupTravelShopMethod,
                chestItemsField,
                travelShopField,
                itemType,
                itemTypeField,
                itemSpecialCurrencyField,
                itemIsAirProperty,
                itemNameProperty,
                itemGetStoreValueMethod,
                itemSetDefaultsMethod,
                itemIdCount,
                getNpcNameMethod,
                itemSearch,
                itemSearchGetNameMethod,
                npcSearch,
                npcSearchGetNameMethod);
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
                new ShopMapping(19, 368, "Shop 19", useTravelShop: true),
                new ShopMapping(20, 453, "Shop 20"),
                new ShopMapping(21, 550, "Shop 21"),
                new ShopMapping(22, 588, "Shop 22"),
                new ShopMapping(23, 633, "Shop 23", useZoologistILFallback: true),
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

        private sealed class IlInstruction
        {
            public int Offset { get; set; }

            public OpCode OpCode { get; set; }

            public int? Int32Operand { get; set; }

            public int? BranchTarget { get; set; }

            public int[] SwitchTargets { get; set; }
        }

        private sealed class ShopMapping
        {
            public ShopMapping(
                int shopId,
                int npcId,
                string shopName,
                bool useTravelShop = false,
                bool useZoologistILFallback = false)
            {
                ShopId = shopId;
                NpcId = npcId;
                ShopName = shopName;
                UseTravelShop = useTravelShop;
                UseZoologistILFallback = useZoologistILFallback;
            }

            public int ShopId { get; private set; }

            public int NpcId { get; private set; }

            public string ShopName { get; private set; }

            public bool UseTravelShop { get; private set; }

            public bool UseZoologistILFallback { get; private set; }
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
                MethodInfo setupTravelShopMethod,
                FieldInfo chestItemsField,
                FieldInfo travelShopField,
                Type itemType,
                FieldInfo itemTypeField,
                FieldInfo itemSpecialCurrencyField,
                PropertyInfo itemIsAirProperty,
                PropertyInfo itemNameProperty,
                MethodInfo itemGetStoreValueMethod,
                MethodInfo itemSetDefaultsMethod,
                int itemIdCount,
                MethodInfo getNpcNameMethod,
                object itemSearch,
                MethodInfo itemSearchGetNameMethod,
                object npcSearch,
                MethodInfo npcSearchGetNameMethod)
            {
                ChestType = chestType;
                SetupShopMethod = setupShopMethod;
                SetupTravelShopMethod = setupTravelShopMethod;
                ChestItemsField = chestItemsField;
                TravelShopField = travelShopField;
                ItemType = itemType;
                ItemTypeField = itemTypeField;
                ItemSpecialCurrencyField = itemSpecialCurrencyField;
                ItemIsAirProperty = itemIsAirProperty;
                ItemNameProperty = itemNameProperty;
                ItemGetStoreValueMethod = itemGetStoreValueMethod;
                ItemSetDefaultsMethod = itemSetDefaultsMethod;
                ItemIdCount = itemIdCount;
                GetNpcNameMethod = getNpcNameMethod;
                ItemSearch = itemSearch;
                ItemSearchGetNameMethod = itemSearchGetNameMethod;
                NpcSearch = npcSearch;
                NpcSearchGetNameMethod = npcSearchGetNameMethod;
            }

            public Type ChestType { get; private set; }

            public MethodInfo SetupShopMethod { get; private set; }

            public MethodInfo SetupTravelShopMethod { get; private set; }

            public FieldInfo ChestItemsField { get; private set; }

            public FieldInfo TravelShopField { get; private set; }

            public Type ItemType { get; private set; }

            public FieldInfo ItemTypeField { get; private set; }

            public FieldInfo ItemSpecialCurrencyField { get; private set; }

            public PropertyInfo ItemIsAirProperty { get; private set; }

            public PropertyInfo ItemNameProperty { get; private set; }

            public MethodInfo ItemGetStoreValueMethod { get; private set; }

            public MethodInfo ItemSetDefaultsMethod { get; private set; }

            public int ItemIdCount { get; private set; }

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
