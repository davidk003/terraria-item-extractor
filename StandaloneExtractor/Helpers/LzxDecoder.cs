using System;
using System.IO;

namespace StandaloneExtractor.Helpers
{
    // This implementation is based on the MonoGame/libmspack LZX decoder lineage.
    public sealed class LzxDecoder
    {
        private static uint[] positionBase;
        private static byte[] extraBits;

        private LzxState state;

        public LzxDecoder(int windowBits)
        {
            if (windowBits < 15 || windowBits > 21)
            {
                throw new ArgumentOutOfRangeException(nameof(windowBits), "LZX window size must be in the range [15, 21].");
            }

            uint windowSize = (uint)(1 << windowBits);
            int positionSlots;

            state = new LzxState();
            state.window = new byte[windowSize];
            for (int i = 0; i < windowSize; i++)
            {
                state.window[i] = 0xDC;
            }

            state.actualSize = windowSize;
            state.windowSize = windowSize;
            state.windowPosition = 0;

            if (extraBits == null)
            {
                extraBits = new byte[52];
                for (int i = 0, j = 0; i <= 50; i += 2)
                {
                    extraBits[i] = extraBits[i + 1] = (byte)j;
                    if (i != 0 && j < 17)
                    {
                        j++;
                    }
                }
            }

            if (positionBase == null)
            {
                positionBase = new uint[51];
                for (int i = 0, j = 0; i <= 50; i++)
                {
                    positionBase[i] = (uint)j;
                    j += 1 << extraBits[i];
                }
            }

            if (windowBits == 20)
            {
                positionSlots = 42;
            }
            else if (windowBits == 21)
            {
                positionSlots = 50;
            }
            else
            {
                positionSlots = windowBits << 1;
            }

            state.R0 = 1;
            state.R1 = 1;
            state.R2 = 1;
            state.mainElements = (ushort)(LzxConstants.NumChars + (positionSlots << 3));
            state.headerRead = 0;
            state.framesRead = 0;
            state.blockRemaining = 0;
            state.blockType = LzxBlockType.Invalid;
            state.intelCurrentPosition = 0;
            state.intelStarted = 0;

            state.preTreeTable = new ushort[(1 << LzxConstants.PreTreeTableBits) + (LzxConstants.PreTreeMaxSymbols << 1)];
            state.preTreeLengths = new byte[LzxConstants.PreTreeMaxSymbols + LzxConstants.LengthTableSafety];
            state.mainTreeTable = new ushort[(1 << LzxConstants.MainTreeTableBits) + (LzxConstants.MainTreeMaxSymbols << 1)];
            state.mainTreeLengths = new byte[LzxConstants.MainTreeMaxSymbols + LzxConstants.LengthTableSafety];
            state.lengthTable = new ushort[(1 << LzxConstants.LengthTableBits) + (LzxConstants.LengthMaxSymbols << 1)];
            state.lengthLengths = new byte[LzxConstants.LengthMaxSymbols + LzxConstants.LengthTableSafety];
            state.alignedTable = new ushort[(1 << LzxConstants.AlignedTableBits) + (LzxConstants.AlignedMaxSymbols << 1)];
            state.alignedLengths = new byte[LzxConstants.AlignedMaxSymbols + LzxConstants.LengthTableSafety];
        }

        public static byte[] DecompressXnbPayload(byte[] compressedPayload, int decompressedSize, int windowBits = 16)
        {
            if (compressedPayload == null)
            {
                throw new ArgumentNullException(nameof(compressedPayload));
            }

            if (decompressedSize < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(decompressedSize));
            }

            var decoder = new LzxDecoder(windowBits);

            using (var input = new MemoryStream(compressedPayload, writable: false))
            using (var output = new MemoryStream(decompressedSize > 0 ? decompressedSize : 256))
            {
                long payloadStart = input.Position;
                long compressedTotal = compressedPayload.Length;

                while ((input.Position - payloadStart) < compressedTotal && output.Length < decompressedSize)
                {
                    if ((input.Position - payloadStart) + 2 > compressedTotal)
                    {
                        break;
                    }

                    int hi = input.ReadByte();
                    int lo = input.ReadByte();
                    if (hi < 0 || lo < 0)
                    {
                        break;
                    }

                    int blockSize = (hi << 8) | lo;
                    int frameSize = 0x8000;

                    if (hi == 0xFF)
                    {
                        int frameLo = input.ReadByte();
                        int blockHi = input.ReadByte();
                        int blockLo = input.ReadByte();
                        if (frameLo < 0 || blockHi < 0 || blockLo < 0)
                        {
                            throw new InvalidDataException("Unexpected end of stream while reading LZX frame header.");
                        }

                        frameSize = (lo << 8) | frameLo;
                        blockSize = (blockHi << 8) | blockLo;
                    }
                    else
                    {
                        blockSize &= 0x7FFF;
                    }

                    if (blockSize == 0 || frameSize == 0)
                    {
                        break;
                    }

                    long blockDataStart = input.Position;
                    if ((blockDataStart - payloadStart) + blockSize > compressedTotal)
                    {
                        throw new InvalidDataException("LZX frame exceeds available compressed payload size.");
                    }

                    int remaining = decompressedSize - (int)output.Length;
                    if (remaining <= 0)
                    {
                        break;
                    }

                    int requestedFrameSize = Math.Min(frameSize, remaining);
                    int result = decoder.Decompress(input, blockSize, output, requestedFrameSize);
                    if (result != 0)
                    {
                        throw new InvalidDataException("LZX decompression failed for one or more frames.");
                    }

                    long expectedBlockEnd = blockDataStart + blockSize;
                    input.Position = expectedBlockEnd;
                }

                byte[] decompressed = output.ToArray();
                if (decompressed.Length != decompressedSize)
                {
                    throw new InvalidDataException(
                        "LZX decompression produced " + decompressed.Length + " bytes, expected " + decompressedSize + ".");
                }

                return decompressed;
            }
        }

        public int Decompress(Stream input, int inputLength, Stream output, int outputLength)
        {
            var bitBuffer = new BitBuffer(input);
            long inputStart = input.Position;
            long inputEnd = input.Position + inputLength;

            byte[] window = state.window;

            uint windowPosition = state.windowPosition;
            uint windowSize = state.windowSize;
            uint r0 = state.R0;
            uint r1 = state.R1;
            uint r2 = state.R2;
            uint i;
            uint j;

            int toGo = outputLength;
            int thisRun;
            int mainElement;
            int matchLength;
            int matchOffset;
            int lengthFooter;
            int extra;
            int verbatimBits;
            int runDestination;
            int runSource;
            int copyLength;
            int alignedBits;

            bitBuffer.InitBitStream();

            if (state.headerRead == 0)
            {
                uint intel = bitBuffer.ReadBits(1);
                if (intel != 0)
                {
                    i = bitBuffer.ReadBits(16);
                    j = bitBuffer.ReadBits(16);
                    state.intelFileSize = (int)((i << 16) | j);
                }

                state.headerRead = 1;
            }

            while (toGo > 0)
            {
                if (state.blockRemaining == 0)
                {
                    if (state.blockType == LzxBlockType.Uncompressed)
                    {
                        if ((state.blockLength & 1) == 1)
                        {
                            input.ReadByte();
                        }

                        bitBuffer.InitBitStream();
                    }

                    state.blockType = (LzxBlockType)bitBuffer.ReadBits(3);
                    i = bitBuffer.ReadBits(16);
                    j = bitBuffer.ReadBits(8);
                    state.blockRemaining = state.blockLength = (i << 8) | j;

                    switch (state.blockType)
                    {
                        case LzxBlockType.Aligned:
                            for (i = 0, j = 0; i < 8; i++)
                            {
                                j = bitBuffer.ReadBits(3);
                                state.alignedLengths[i] = (byte)j;
                            }

                            MakeDecodeTable(LzxConstants.AlignedMaxSymbols, LzxConstants.AlignedTableBits, state.alignedLengths, state.alignedTable);
                            goto case LzxBlockType.Verbatim;

                        case LzxBlockType.Verbatim:
                            ReadLengths(state.mainTreeLengths, 0, 256, bitBuffer);
                            ReadLengths(state.mainTreeLengths, 256, state.mainElements, bitBuffer);
                            MakeDecodeTable(LzxConstants.MainTreeMaxSymbols, LzxConstants.MainTreeTableBits, state.mainTreeLengths, state.mainTreeTable);
                            if (state.mainTreeLengths[0xE8] != 0)
                            {
                                state.intelStarted = 1;
                            }

                            ReadLengths(state.lengthLengths, 0, LzxConstants.NumSecondaryLengths, bitBuffer);
                            MakeDecodeTable(LzxConstants.LengthMaxSymbols, LzxConstants.LengthTableBits, state.lengthLengths, state.lengthTable);
                            break;

                        case LzxBlockType.Uncompressed:
                            state.intelStarted = 1;
                            bitBuffer.EnsureBits(16);
                            if (bitBuffer.BitsLeft > 16)
                            {
                                input.Seek(-2, SeekOrigin.Current);
                            }

                            byte hi;
                            byte middleHigh;
                            byte middleLow;
                            byte lo;
                            lo = ReadRequiredByte(input);
                            middleLow = ReadRequiredByte(input);
                            middleHigh = ReadRequiredByte(input);
                            hi = ReadRequiredByte(input);
                            r0 = (uint)(lo | (middleLow << 8) | (middleHigh << 16) | (hi << 24));

                            lo = ReadRequiredByte(input);
                            middleLow = ReadRequiredByte(input);
                            middleHigh = ReadRequiredByte(input);
                            hi = ReadRequiredByte(input);
                            r1 = (uint)(lo | (middleLow << 8) | (middleHigh << 16) | (hi << 24));

                            lo = ReadRequiredByte(input);
                            middleLow = ReadRequiredByte(input);
                            middleHigh = ReadRequiredByte(input);
                            hi = ReadRequiredByte(input);
                            r2 = (uint)(lo | (middleLow << 8) | (middleHigh << 16) | (hi << 24));
                            break;

                        default:
                            return -1;
                    }
                }

                if (input.Position > inputStart + inputLength)
                {
                    if (input.Position > inputStart + inputLength + 2 || bitBuffer.BitsLeft < 16)
                    {
                        return -1;
                    }
                }

                while ((thisRun = (int)state.blockRemaining) > 0 && toGo > 0)
                {
                    if (thisRun > toGo)
                    {
                        thisRun = toGo;
                    }

                    toGo -= thisRun;
                    state.blockRemaining -= (uint)thisRun;

                    windowPosition &= windowSize - 1;
                    if (windowPosition + thisRun > windowSize)
                    {
                        return -1;
                    }

                    switch (state.blockType)
                    {
                        case LzxBlockType.Verbatim:
                            while (thisRun > 0)
                            {
                                mainElement = (int)ReadHuffmanSymbol(state.mainTreeTable, state.mainTreeLengths, LzxConstants.MainTreeMaxSymbols, LzxConstants.MainTreeTableBits, bitBuffer);
                                if (mainElement < LzxConstants.NumChars)
                                {
                                    window[windowPosition++] = (byte)mainElement;
                                    thisRun--;
                                }
                                else
                                {
                                    mainElement -= LzxConstants.NumChars;

                                    matchLength = mainElement & LzxConstants.NumPrimaryLengths;
                                    if (matchLength == LzxConstants.NumPrimaryLengths)
                                    {
                                        lengthFooter = (int)ReadHuffmanSymbol(state.lengthTable, state.lengthLengths, LzxConstants.LengthMaxSymbols, LzxConstants.LengthTableBits, bitBuffer);
                                        matchLength += lengthFooter;
                                    }

                                    matchLength += LzxConstants.MinMatch;

                                    matchOffset = mainElement >> 3;

                                    if (matchOffset > 2)
                                    {
                                        if (matchOffset != 3)
                                        {
                                            extra = extraBits[matchOffset];
                                            verbatimBits = (int)bitBuffer.ReadBits((byte)extra);
                                            matchOffset = (int)positionBase[matchOffset] - 2 + verbatimBits;
                                        }
                                        else
                                        {
                                            matchOffset = 1;
                                        }

                                        r2 = r1;
                                        r1 = r0;
                                        r0 = (uint)matchOffset;
                                    }
                                    else if (matchOffset == 0)
                                    {
                                        matchOffset = (int)r0;
                                    }
                                    else if (matchOffset == 1)
                                    {
                                        matchOffset = (int)r1;
                                        r1 = r0;
                                        r0 = (uint)matchOffset;
                                    }
                                    else
                                    {
                                        matchOffset = (int)r2;
                                        r2 = r0;
                                        r0 = (uint)matchOffset;
                                    }

                                    runDestination = (int)windowPosition;
                                    thisRun -= matchLength;

                                    if (windowPosition >= matchOffset)
                                    {
                                        runSource = runDestination - matchOffset;
                                    }
                                    else
                                    {
                                        runSource = runDestination + ((int)windowSize - matchOffset);
                                        copyLength = matchOffset - (int)windowPosition;
                                        if (copyLength < matchLength)
                                        {
                                            matchLength -= copyLength;
                                            windowPosition += (uint)copyLength;
                                            while (copyLength-- > 0)
                                            {
                                                window[runDestination++] = window[runSource++];
                                            }

                                            runSource = 0;
                                        }
                                    }

                                    windowPosition += (uint)matchLength;
                                    while (matchLength-- > 0)
                                    {
                                        window[runDestination++] = window[runSource++];
                                    }
                                }
                            }

                            break;

                        case LzxBlockType.Aligned:
                            while (thisRun > 0)
                            {
                                mainElement = (int)ReadHuffmanSymbol(state.mainTreeTable, state.mainTreeLengths, LzxConstants.MainTreeMaxSymbols, LzxConstants.MainTreeTableBits, bitBuffer);
                                if (mainElement < LzxConstants.NumChars)
                                {
                                    window[windowPosition++] = (byte)mainElement;
                                    thisRun--;
                                }
                                else
                                {
                                    mainElement -= LzxConstants.NumChars;

                                    matchLength = mainElement & LzxConstants.NumPrimaryLengths;
                                    if (matchLength == LzxConstants.NumPrimaryLengths)
                                    {
                                        lengthFooter = (int)ReadHuffmanSymbol(state.lengthTable, state.lengthLengths, LzxConstants.LengthMaxSymbols, LzxConstants.LengthTableBits, bitBuffer);
                                        matchLength += lengthFooter;
                                    }

                                    matchLength += LzxConstants.MinMatch;
                                    matchOffset = mainElement >> 3;

                                    if (matchOffset > 2)
                                    {
                                        extra = extraBits[matchOffset];
                                        matchOffset = (int)positionBase[matchOffset] - 2;
                                        if (extra > 3)
                                        {
                                            extra -= 3;
                                            verbatimBits = (int)bitBuffer.ReadBits((byte)extra);
                                            matchOffset += verbatimBits << 3;
                                            alignedBits = (int)ReadHuffmanSymbol(state.alignedTable, state.alignedLengths, LzxConstants.AlignedMaxSymbols, LzxConstants.AlignedTableBits, bitBuffer);
                                            matchOffset += alignedBits;
                                        }
                                        else if (extra == 3)
                                        {
                                            alignedBits = (int)ReadHuffmanSymbol(state.alignedTable, state.alignedLengths, LzxConstants.AlignedMaxSymbols, LzxConstants.AlignedTableBits, bitBuffer);
                                            matchOffset += alignedBits;
                                        }
                                        else if (extra > 0)
                                        {
                                            verbatimBits = (int)bitBuffer.ReadBits((byte)extra);
                                            matchOffset += verbatimBits;
                                        }
                                        else
                                        {
                                            matchOffset = 1;
                                        }

                                        r2 = r1;
                                        r1 = r0;
                                        r0 = (uint)matchOffset;
                                    }
                                    else if (matchOffset == 0)
                                    {
                                        matchOffset = (int)r0;
                                    }
                                    else if (matchOffset == 1)
                                    {
                                        matchOffset = (int)r1;
                                        r1 = r0;
                                        r0 = (uint)matchOffset;
                                    }
                                    else
                                    {
                                        matchOffset = (int)r2;
                                        r2 = r0;
                                        r0 = (uint)matchOffset;
                                    }

                                    runDestination = (int)windowPosition;
                                    thisRun -= matchLength;

                                    if (windowPosition >= matchOffset)
                                    {
                                        runSource = runDestination - matchOffset;
                                    }
                                    else
                                    {
                                        runSource = runDestination + ((int)windowSize - matchOffset);
                                        copyLength = matchOffset - (int)windowPosition;
                                        if (copyLength < matchLength)
                                        {
                                            matchLength -= copyLength;
                                            windowPosition += (uint)copyLength;
                                            while (copyLength-- > 0)
                                            {
                                                window[runDestination++] = window[runSource++];
                                            }

                                            runSource = 0;
                                        }
                                    }

                                    windowPosition += (uint)matchLength;
                                    while (matchLength-- > 0)
                                    {
                                        window[runDestination++] = window[runSource++];
                                    }
                                }
                            }

                            break;

                        case LzxBlockType.Uncompressed:
                            if (input.Position + thisRun > inputEnd)
                            {
                                return -1;
                            }

                            var buffer = new byte[thisRun];
                            int read = input.Read(buffer, 0, thisRun);
                            if (read != thisRun)
                            {
                                return -1;
                            }

                            Buffer.BlockCopy(buffer, 0, window, (int)windowPosition, thisRun);
                            windowPosition += (uint)thisRun;
                            break;

                        default:
                            return -1;
                    }
                }
            }

            if (toGo != 0)
            {
                return -1;
            }

            int startWindowPosition = (int)windowPosition;
            if (startWindowPosition == 0)
            {
                startWindowPosition = (int)windowSize;
            }

            startWindowPosition -= outputLength;
            output.Write(window, startWindowPosition, outputLength);

            state.windowPosition = windowPosition;
            state.R0 = r0;
            state.R1 = r1;
            state.R2 = r2;

            if (state.framesRead++ < 32768 && state.intelFileSize != 0)
            {
                if (outputLength <= 6 || state.intelStarted == 0)
                {
                    state.intelCurrentPosition += outputLength;
                }
                else
                {
                    state.intelCurrentPosition += outputLength;
                }
            }

            return 0;
        }

        private static byte ReadRequiredByte(Stream stream)
        {
            int value = stream.ReadByte();
            if (value < 0)
            {
                throw new EndOfStreamException("Unexpected end of stream while reading LZX block.");
            }

            return (byte)value;
        }

        private int MakeDecodeTable(uint symbolCount, uint tableBits, byte[] lengths, ushort[] table)
        {
            ushort symbol;
            uint leaf;
            byte bitNumber = 1;
            uint fill;
            uint position = 0;
            uint tableMask = (uint)(1 << (int)tableBits);
            uint bitMask = tableMask >> 1;
            uint nextSymbol = bitMask;

            while (bitNumber <= tableBits)
            {
                for (symbol = 0; symbol < symbolCount; symbol++)
                {
                    if (lengths[symbol] == bitNumber)
                    {
                        leaf = position;

                        if ((position += bitMask) > tableMask)
                        {
                            return 1;
                        }

                        fill = bitMask;
                        while (fill-- > 0)
                        {
                            table[leaf++] = symbol;
                        }
                    }
                }

                bitMask >>= 1;
                bitNumber++;
            }

            if (position != tableMask)
            {
                for (symbol = (ushort)position; symbol < tableMask; symbol++)
                {
                    table[symbol] = 0;
                }

                position <<= 16;
                tableMask <<= 16;
                bitMask = 1 << 15;

                while (bitNumber <= 16)
                {
                    for (symbol = 0; symbol < symbolCount; symbol++)
                    {
                        if (lengths[symbol] == bitNumber)
                        {
                            leaf = position >> 16;
                            for (fill = 0; fill < bitNumber - tableBits; fill++)
                            {
                                if (table[leaf] == 0)
                                {
                                    table[nextSymbol << 1] = 0;
                                    table[(nextSymbol << 1) + 1] = 0;
                                    table[leaf] = (ushort)(nextSymbol++);
                                }

                                leaf = (uint)(table[leaf] << 1);
                                if (((position >> (int)(15 - fill)) & 1) == 1)
                                {
                                    leaf++;
                                }
                            }

                            table[leaf] = symbol;

                            if ((position += bitMask) > tableMask)
                            {
                                return 1;
                            }
                        }
                    }

                    bitMask >>= 1;
                    bitNumber++;
                }
            }

            if (position == tableMask)
            {
                return 0;
            }

            for (symbol = 0; symbol < symbolCount; symbol++)
            {
                if (lengths[symbol] != 0)
                {
                    return 1;
                }
            }

            return 0;
        }

        private void ReadLengths(byte[] lengths, uint first, uint last, BitBuffer bitBuffer)
        {
            uint x;
            uint y;
            int z;

            for (x = 0; x < 20; x++)
            {
                y = bitBuffer.ReadBits(4);
                state.preTreeLengths[x] = (byte)y;
            }

            MakeDecodeTable(LzxConstants.PreTreeMaxSymbols, LzxConstants.PreTreeTableBits, state.preTreeLengths, state.preTreeTable);

            for (x = first; x < last;)
            {
                z = (int)ReadHuffmanSymbol(state.preTreeTable, state.preTreeLengths, LzxConstants.PreTreeMaxSymbols, LzxConstants.PreTreeTableBits, bitBuffer);
                if (z == 17)
                {
                    y = bitBuffer.ReadBits(4);
                    y += 4;
                    while (y-- != 0)
                    {
                        lengths[x++] = 0;
                    }
                }
                else if (z == 18)
                {
                    y = bitBuffer.ReadBits(5);
                    y += 20;
                    while (y-- != 0)
                    {
                        lengths[x++] = 0;
                    }
                }
                else if (z == 19)
                {
                    y = bitBuffer.ReadBits(1);
                    y += 4;
                    z = (int)ReadHuffmanSymbol(state.preTreeTable, state.preTreeLengths, LzxConstants.PreTreeMaxSymbols, LzxConstants.PreTreeTableBits, bitBuffer);
                    z = lengths[x] - z;
                    if (z < 0)
                    {
                        z += 17;
                    }

                    while (y-- != 0)
                    {
                        lengths[x++] = (byte)z;
                    }
                }
                else
                {
                    z = lengths[x] - z;
                    if (z < 0)
                    {
                        z += 17;
                    }

                    lengths[x++] = (byte)z;
                }
            }
        }

        private uint ReadHuffmanSymbol(ushort[] table, byte[] lengths, uint symbolCount, uint tableBits, BitBuffer bitBuffer)
        {
            uint i;
            uint j;
            bitBuffer.EnsureBits(16);
            if ((i = table[bitBuffer.PeekBits((byte)tableBits)]) >= symbolCount)
            {
                j = (uint)(1 << (sizeof(uint) * 8 - (int)tableBits));
                do
                {
                    j >>= 1;
                    i <<= 1;
                    i |= (bitBuffer.Buffer & j) != 0 ? 1u : 0u;
                    if (j == 0)
                    {
                        return 0;
                    }
                }
                while ((i = table[i]) >= symbolCount);
            }

            j = lengths[i];
            bitBuffer.RemoveBits((byte)j);
            return i;
        }

        private sealed class BitBuffer
        {
            private uint buffer;
            private byte bitsLeft;
            private readonly Stream byteStream;

            public BitBuffer(Stream stream)
            {
                byteStream = stream;
                InitBitStream();
            }

            public uint Buffer
            {
                get { return buffer; }
            }

            public byte BitsLeft
            {
                get { return bitsLeft; }
            }

            public void InitBitStream()
            {
                buffer = 0;
                bitsLeft = 0;
            }

            public void EnsureBits(byte bits)
            {
                while (bitsLeft < bits)
                {
                    int lo = byteStream.ReadByte();
                    int hi = byteStream.ReadByte();
                    if (lo < 0 || hi < 0)
                    {
                        throw new EndOfStreamException("Unexpected end of stream while filling LZX bit buffer.");
                    }

                    buffer |= (uint)(((hi << 8) | lo) << (sizeof(uint) * 8 - 16 - bitsLeft));
                    bitsLeft += 16;
                }
            }

            public uint PeekBits(byte bits)
            {
                return buffer >> (sizeof(uint) * 8 - bits);
            }

            public void RemoveBits(byte bits)
            {
                buffer <<= bits;
                bitsLeft -= bits;
            }

            public uint ReadBits(byte bits)
            {
                uint value = 0;
                if (bits > 0)
                {
                    EnsureBits(bits);
                    value = PeekBits(bits);
                    RemoveBits(bits);
                }

                return value;
            }
        }

        private enum LzxBlockType
        {
            Invalid = 0,
            Verbatim = 1,
            Aligned = 2,
            Uncompressed = 3
        }

        private static class LzxConstants
        {
            public const ushort MinMatch = 2;
            public const ushort NumChars = 256;
            public const ushort NumPrimaryLengths = 7;
            public const ushort NumSecondaryLengths = 249;

            public const ushort PreTreeMaxSymbols = 20;
            public const ushort PreTreeTableBits = 6;
            public const ushort MainTreeMaxSymbols = NumChars + 50 * 8;
            public const ushort MainTreeTableBits = 12;
            public const ushort LengthMaxSymbols = NumSecondaryLengths + 1;
            public const ushort LengthTableBits = 12;
            public const ushort AlignedMaxSymbols = 8;
            public const ushort AlignedTableBits = 7;

            public const ushort LengthTableSafety = 64;
        }

        private struct LzxState
        {
            public uint R0;
            public uint R1;
            public uint R2;
            public ushort mainElements;
            public int headerRead;
            public LzxBlockType blockType;
            public uint blockLength;
            public uint blockRemaining;
            public uint framesRead;
            public int intelFileSize;
            public int intelCurrentPosition;
            public int intelStarted;

            public ushort[] preTreeTable;
            public byte[] preTreeLengths;
            public ushort[] mainTreeTable;
            public byte[] mainTreeLengths;
            public ushort[] lengthTable;
            public byte[] lengthLengths;
            public ushort[] alignedTable;
            public byte[] alignedLengths;

            public uint actualSize;
            public byte[] window;
            public uint windowSize;
            public uint windowPosition;
        }
    }
}
