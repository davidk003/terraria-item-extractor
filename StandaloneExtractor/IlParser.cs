using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace StandaloneExtractor
{
    internal sealed class IlInstruction
    {
        public int Offset { get; set; }

        public OpCode OpCode { get; set; }

        public int? Int32Operand { get; set; }

        public int? BranchTarget { get; set; }

        public int[] SwitchTargets { get; set; }
    }

    internal static class IlParser
    {
        private static readonly Dictionary<ushort, OpCode> OpCodeByValue = BuildOpCodeByValueMap();

        public static List<IlInstruction> ParseIlInstructions(MethodInfo method)
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

        public static bool TryGetLdcI4Value(IlInstruction instruction, out int value)
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

        public static IEnumerable<int> ExtractIntConstantsFromMethodBody(MethodInfo method)
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

        public static bool TryFindClosestIntConstant(List<IlInstruction> instructions, int fromIndex, int lowerBoundOffset, out int value)
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

        public static bool TryFindMethodCallOffset(List<IlInstruction> instructions, int methodToken, out int offset)
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

        public static IlInstruction FindPrimarySwitchInstruction(List<IlInstruction> instructions, out int switchInstructionIndex)
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

        public static bool TryResolveSwitchCaseRangeByTypeValue(
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

        public static bool TryResolveCaseRangeByTypeComparison(
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
    }
}
