using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace StandaloneExtractor
{
    // === PNG Writer (System.Drawing) ===

    internal static class PngWriter
    {
        public static void WritePng(string outputPath, int width, int height, byte[] rgbaPixels)
        {
            if (rgbaPixels == null)
                throw new ArgumentNullException(nameof(rgbaPixels));
            if (width <= 0 || height <= 0)
                throw new ArgumentOutOfRangeException("width/height", "PNG dimensions must be positive.");

            string directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            using (var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb))
            {
                var rect = new Rectangle(0, 0, width, height);
                var bmpData = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                byte[] bgra = new byte[width * height * 4];
                for (int i = 0; i < width * height; i++)
                {
                    int s = i * 4;
                    bgra[s + 0] = rgbaPixels[s + 2];
                    bgra[s + 1] = rgbaPixels[s + 1];
                    bgra[s + 2] = rgbaPixels[s + 0];
                    bgra[s + 3] = rgbaPixels[s + 3];
                }
                Marshal.Copy(bgra, 0, bmpData.Scan0, bgra.Length);
                bmp.UnlockBits(bmpData);
                bmp.Save(outputPath, ImageFormat.Png);
            }
        }
    }

    // === XNB Reader ===

    public sealed class XnbTextureData
    {
        public XnbTextureData(int width, int height, byte[] rgbaPixels)
        {
            Width = width;
            Height = height;
            RgbaPixels = rgbaPixels ?? Array.Empty<byte>();
        }

        public int Width { get; private set; }

        public int Height { get; private set; }

        public byte[] RgbaPixels { get; private set; }
    }

    public static class XnbReader
    {
        private const byte CompressedFlag = 0x80;
        private const byte Lz4Flag = 0x40;

        private const int SurfaceFormatColor = 0;
        private const int SurfaceFormatDxt1 = 4;
        private const int SurfaceFormatDxt3 = 5;
        private const int SurfaceFormatDxt5 = 6;

        public static XnbTextureData ReadTexture2D(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("File path is required.", nameof(filePath));
            }

            using (var fileStream = File.OpenRead(filePath))
            using (var reader = new BinaryReader(fileStream, Encoding.UTF8))
            {
                XnbHeader header = ReadHeader(reader);
                byte[] payload = DecompressPayload(reader, fileStream, header);

                using (var payloadStream = new MemoryStream(payload, writable: false))
                using (var payloadReader = new BinaryReader(payloadStream, Encoding.UTF8))
                {
                    ReadTypeReaders(payloadReader, header.Platform);
                    return ReadTextureData(payloadReader);
                }
            }
        }

        private static XnbHeader ReadHeader(BinaryReader reader)
        {
            if (reader.ReadByte() != (byte)'X'
                || reader.ReadByte() != (byte)'N'
                || reader.ReadByte() != (byte)'B')
            {
                throw new InvalidDataException("Invalid XNB magic header.");
            }

            byte platform = reader.ReadByte();
            byte version = reader.ReadByte();
            byte flags = reader.ReadByte();
            int declaredFileSize = reader.ReadInt32();

            if (version < 4)
            {
                throw new NotSupportedException("Unsupported XNB version " + version + ".");
            }

            return new XnbHeader
            {
                Platform = platform,
                Version = version,
                Flags = flags,
                DeclaredFileSize = declaredFileSize,
                IsCompressed = (flags & CompressedFlag) != 0,
                IsLz4 = (flags & Lz4Flag) != 0
            };
        }

        private static byte[] DecompressPayload(BinaryReader reader, Stream fileStream, XnbHeader header)
        {
            if (header.IsCompressed)
            {
                if (header.IsLz4)
                {
                    throw new NotSupportedException("XNB LZ4-compressed payloads are not supported by this extractor.");
                }

                int decompressedSize = reader.ReadInt32();
                int compressedSize = header.DeclaredFileSize - 14;
                int bytesRemaining = checked((int)Math.Max(0L, fileStream.Length - fileStream.Position));
                if (compressedSize < 0 || compressedSize > bytesRemaining)
                {
                    compressedSize = bytesRemaining;
                }

                byte[] compressedPayload = reader.ReadBytes(compressedSize);
                if (compressedPayload.Length != compressedSize)
                {
                    throw new EndOfStreamException("Unexpected end of XNB stream while reading compressed payload.");
                }

                return LzxDecoder.DecompressXnbPayload(compressedPayload, decompressedSize);
            }

            int payloadSize = header.DeclaredFileSize - 10;
            int remaining = checked((int)Math.Max(0L, fileStream.Length - fileStream.Position));
            if (payloadSize < 0 || payloadSize > remaining)
            {
                payloadSize = remaining;
            }

            byte[] payload = reader.ReadBytes(payloadSize);
            if (payload.Length != payloadSize)
            {
                throw new EndOfStreamException("Unexpected end of XNB stream while reading payload.");
            }

            return payload;
        }

        private static string ReadTypeReaders(BinaryReader payloadReader, byte platform)
        {
            int typeReaderCount = Read7BitEncodedInt(payloadReader);
            if (typeReaderCount <= 0)
            {
                throw new InvalidDataException("XNB payload does not contain any type readers.");
            }

            var typeReaders = new List<string>(typeReaderCount);
            for (int i = 0; i < typeReaderCount; i++)
            {
                string readerType = payloadReader.ReadString();
                payloadReader.ReadInt32();
                typeReaders.Add(readerType ?? string.Empty);
            }

            Read7BitEncodedInt(payloadReader);

            int primaryObjectReaderIndex = Read7BitEncodedInt(payloadReader);
            if (primaryObjectReaderIndex <= 0 || primaryObjectReaderIndex > typeReaders.Count)
            {
                throw new InvalidDataException("Invalid root object reader index in XNB payload.");
            }

            string primaryReader = typeReaders[primaryObjectReaderIndex - 1];
            if (primaryReader.IndexOf("Texture2DReader", StringComparison.OrdinalIgnoreCase) < 0)
            {
                throw new NotSupportedException(
                    "XNB root object reader is not a Texture2D reader: " + primaryReader + " (platform=" + (char)platform + ")");
            }

            return primaryReader;
        }

        private static XnbTextureData ReadTextureData(BinaryReader payloadReader)
        {
            int surfaceFormat = payloadReader.ReadInt32();
            int width = payloadReader.ReadInt32();
            int height = payloadReader.ReadInt32();
            int mipCount = payloadReader.ReadInt32();

            if (width <= 0 || height <= 0)
            {
                throw new InvalidDataException("Invalid texture dimensions in XNB payload: " + width + "x" + height + ".");
            }

            if (mipCount <= 0)
            {
                throw new InvalidDataException("Texture does not contain any mip data.");
            }

            byte[] mip0Data = null;
            for (int mipIndex = 0; mipIndex < mipCount; mipIndex++)
            {
                int dataLength = payloadReader.ReadInt32();
                if (dataLength < 0)
                {
                    throw new InvalidDataException("Negative mip data length encountered in XNB payload.");
                }

                byte[] mipData = payloadReader.ReadBytes(dataLength);
                if (mipData.Length != dataLength)
                {
                    throw new EndOfStreamException("Unexpected end of XNB stream while reading mip data.");
                }

                if (mipIndex == 0)
                {
                    mip0Data = mipData;
                }
            }

            if (mip0Data == null)
            {
                throw new InvalidDataException("Missing mip-0 texture data.");
            }

            byte[] rgba = DecodePixels(surfaceFormat, mip0Data, width, height);
            return new XnbTextureData(width, height, rgba);
        }

        private static byte[] DecodePixels(int surfaceFormat, byte[] mip0Data, int width, int height)
        {
            if (surfaceFormat == SurfaceFormatColor)
            {
                int expected = checked(width * height * 4);
                if (mip0Data.Length < expected)
                {
                    throw new InvalidDataException(
                        "Color texture data length is smaller than expected. got=" + mip0Data.Length + ", expected=" + expected);
                }

                byte[] rgba = new byte[expected];
                Buffer.BlockCopy(mip0Data, 0, rgba, 0, expected);
                return rgba;
            }

            if (surfaceFormat == SurfaceFormatDxt1)
            {
                return DxtDecoder.DecodeDxt1(mip0Data, width, height);
            }

            if (surfaceFormat == SurfaceFormatDxt3)
            {
                return DxtDecoder.DecodeDxt3(mip0Data, width, height);
            }

            if (surfaceFormat == SurfaceFormatDxt5)
            {
                return DxtDecoder.DecodeDxt5(mip0Data, width, height);
            }

            throw new NotSupportedException("Unsupported Texture2D surface format: " + surfaceFormat + ".");
        }

        private static int Read7BitEncodedInt(BinaryReader reader)
        {
            int count = 0;
            int shift = 0;

            while (shift < 35)
            {
                byte b = reader.ReadByte();
                count |= (b & 0x7F) << shift;
                shift += 7;

                if ((b & 0x80) == 0)
                {
                    return count;
                }
            }

            throw new FormatException("Invalid 7-bit encoded integer in XNB stream.");
        }

        private struct XnbHeader
        {
            public byte Platform;
            public byte Version;
            public byte Flags;
            public int DeclaredFileSize;
            public bool IsCompressed;
            public bool IsLz4;
        }
    }

    // === DXT Decoder ===

    public static class DxtDecoder
    {
        private delegate void BlockDecoder(byte[] src, int srcOffset, byte[] dst, int width, int height, int blockX, int blockY);

        public static byte[] DecodeDxt1(byte[] data, int width, int height)
        {
            return DecodeBlocks(data, width, height, 8, "DXT1", DecodeDxt1Block);
        }

        public static byte[] DecodeDxt3(byte[] data, int width, int height)
        {
            return DecodeBlocks(data, width, height, 16, "DXT3", DecodeDxt3Block);
        }

        public static byte[] DecodeDxt5(byte[] data, int width, int height)
        {
            return DecodeBlocks(data, width, height, 16, "DXT5", DecodeDxt5Block);
        }

        private static byte[] DecodeBlocks(byte[] data, int width, int height, int blockByteSize, string formatName, BlockDecoder decoder)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            byte[] rgba = new byte[checked(width * height * 4)];
            int blockWidth = (width + 3) / 4;
            int blockHeight = (height + 3) / 4;
            int offset = 0;

            for (int by = 0; by < blockHeight; by++)
            {
                for (int bx = 0; bx < blockWidth; bx++)
                {
                    if (offset + blockByteSize > data.Length)
                    {
                        throw new ArgumentException(
                            formatName + " data length is smaller than expected for the provided dimensions.", nameof(data));
                    }

                    decoder(data, offset, rgba, width, height, bx, by);
                    offset += blockByteSize;
                }
            }

            return rgba;
        }

        private static void ReadColorEndpoints(byte[] src, int offset, out ushort c0, out ushort c1, out uint code)
        {
            c0 = (ushort)(src[offset] | (src[offset + 1] << 8));
            c1 = (ushort)(src[offset + 2] | (src[offset + 3] << 8));
            code = (uint)(src[offset + 4]
                | (src[offset + 5] << 8)
                | (src[offset + 6] << 16)
                | (src[offset + 7] << 24));
        }

        private static byte[] BuildOpaqueColorPalette(ushort c0, ushort c1)
        {
            var colors = new byte[16];
            DecodeRgb565(c0, colors, 0);
            colors[3] = 255;
            DecodeRgb565(c1, colors, 4);
            colors[7] = 255;

            colors[8] = (byte)((2 * colors[0] + colors[4]) / 3);
            colors[9] = (byte)((2 * colors[1] + colors[5]) / 3);
            colors[10] = (byte)((2 * colors[2] + colors[6]) / 3);
            colors[11] = 255;

            colors[12] = (byte)((colors[0] + 2 * colors[4]) / 3);
            colors[13] = (byte)((colors[1] + 2 * colors[5]) / 3);
            colors[14] = (byte)((colors[2] + 2 * colors[6]) / 3);
            colors[15] = 255;

            return colors;
        }

        private static void DecodeDxt1Block(byte[] src, int srcOffset, byte[] dst, int width, int height, int blockX, int blockY)
        {
            ReadColorEndpoints(src, srcOffset, out ushort c0, out ushort c1, out uint code);

            byte[] colors;
            if (c0 > c1)
            {
                colors = BuildOpaqueColorPalette(c0, c1);
            }
            else
            {
                colors = new byte[16];
                DecodeRgb565(c0, colors, 0);
                colors[3] = 255;
                DecodeRgb565(c1, colors, 4);
                colors[7] = 255;

                colors[8] = (byte)((colors[0] + colors[4]) / 2);
                colors[9] = (byte)((colors[1] + colors[5]) / 2);
                colors[10] = (byte)((colors[2] + colors[6]) / 2);
                colors[11] = 255;
            }

            WriteBlockPixels(dst, width, height, blockX, blockY, colors, code, null);
        }

        private static void DecodeDxt3Block(byte[] src, int srcOffset, byte[] dst, int width, int height, int blockX, int blockY)
        {
            ulong alphaBits = 0UL;
            for (int i = 0; i < 8; i++)
            {
                alphaBits |= (ulong)src[srcOffset + i] << (8 * i);
            }

            ReadColorEndpoints(src, srcOffset + 8, out ushort c0, out ushort c1, out uint code);
            byte[] colors = BuildOpaqueColorPalette(c0, c1);

            var alpha = new byte[16];
            for (int i = 0; i < 16; i++)
            {
                int nibble = (int)((alphaBits >> (i * 4)) & 0xF);
                alpha[i] = (byte)((nibble << 4) | nibble);
            }

            WriteBlockPixels(dst, width, height, blockX, blockY, colors, code, alpha);
        }

        private static void DecodeDxt5Block(byte[] src, int srcOffset, byte[] dst, int width, int height, int blockX, int blockY)
        {
            byte a0 = src[srcOffset];
            byte a1 = src[srcOffset + 1];

            ulong alphaBits = 0UL;
            for (int i = 0; i < 6; i++)
            {
                alphaBits |= (ulong)src[srcOffset + 2 + i] << (8 * i);
            }

            var alphaPalette = new byte[8];
            alphaPalette[0] = a0;
            alphaPalette[1] = a1;
            if (a0 > a1)
            {
                alphaPalette[2] = (byte)((6 * a0 + a1) / 7);
                alphaPalette[3] = (byte)((5 * a0 + 2 * a1) / 7);
                alphaPalette[4] = (byte)((4 * a0 + 3 * a1) / 7);
                alphaPalette[5] = (byte)((3 * a0 + 4 * a1) / 7);
                alphaPalette[6] = (byte)((2 * a0 + 5 * a1) / 7);
                alphaPalette[7] = (byte)((a0 + 6 * a1) / 7);
            }
            else
            {
                alphaPalette[2] = (byte)((4 * a0 + a1) / 5);
                alphaPalette[3] = (byte)((3 * a0 + 2 * a1) / 5);
                alphaPalette[4] = (byte)((2 * a0 + 3 * a1) / 5);
                alphaPalette[5] = (byte)((a0 + 4 * a1) / 5);
                alphaPalette[6] = 0;
                alphaPalette[7] = 255;
            }

            var alpha = new byte[16];
            for (int i = 0; i < 16; i++)
            {
                int index = (int)((alphaBits >> (3 * i)) & 0x7);
                alpha[i] = alphaPalette[index];
            }

            ReadColorEndpoints(src, srcOffset + 8, out ushort c0, out ushort c1, out uint code);
            byte[] colors = BuildOpaqueColorPalette(c0, c1);

            WriteBlockPixels(dst, width, height, blockX, blockY, colors, code, alpha);
        }

        private static void WriteBlockPixels(
            byte[] dst,
            int width,
            int height,
            int blockX,
            int blockY,
            byte[] colors,
            uint colorIndices,
            byte[] alphaOverride)
        {
            for (int py = 0; py < 4; py++)
            {
                int y = blockY * 4 + py;
                if (y >= height)
                {
                    continue;
                }

                for (int px = 0; px < 4; px++)
                {
                    int x = blockX * 4 + px;
                    if (x >= width)
                    {
                        continue;
                    }

                    int pixelIndex = py * 4 + px;
                    int colorIndex = (int)((colorIndices >> (2 * pixelIndex)) & 0x3);
                    int paletteOffset = colorIndex * 4;
                    int dstOffset = (y * width + x) * 4;

                    dst[dstOffset] = colors[paletteOffset];
                    dst[dstOffset + 1] = colors[paletteOffset + 1];
                    dst[dstOffset + 2] = colors[paletteOffset + 2];
                    dst[dstOffset + 3] = alphaOverride == null ? colors[paletteOffset + 3] : alphaOverride[pixelIndex];
                }
            }
        }

        private static void DecodeRgb565(ushort value, byte[] destination, int offset)
        {
            int r = (value >> 11) & 0x1F;
            int g = (value >> 5) & 0x3F;
            int b = value & 0x1F;

            destination[offset] = (byte)((r << 3) | (r >> 2));
            destination[offset + 1] = (byte)((g << 2) | (g >> 4));
            destination[offset + 2] = (byte)((b << 3) | (b >> 2));
        }
    }
}
