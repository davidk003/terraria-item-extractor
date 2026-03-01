using System;
using System.IO;
using System.IO.Compression;

namespace StandaloneExtractor.Helpers
{
    internal static class PngWriter
    {
        private static readonly byte[] PngSignature =
        {
            0x89, 0x50, 0x4E, 0x47,
            0x0D, 0x0A, 0x1A, 0x0A
        };

        private static readonly uint[] CrcTable = CreateCrcTable();

        public static void WritePng(string outputPath, int width, int height, byte[] rgbaPixels)
        {
            if (rgbaPixels == null)
            {
                throw new ArgumentNullException(nameof(rgbaPixels));
            }

            if (width <= 0 || height <= 0)
            {
                throw new ArgumentOutOfRangeException("width/height", "PNG dimensions must be positive.");
            }

            int expectedLength = checked(width * height * 4);
            if (rgbaPixels.Length < expectedLength)
            {
                throw new InvalidDataException(
                    "RGBA buffer is smaller than expected. got=" + rgbaPixels.Length + ", expected=" + expectedLength);
            }

            string directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using (var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
            {
                output.Write(PngSignature, 0, PngSignature.Length);

                WriteIhdr(output, width, height);
                WriteIdat(output, width, height, rgbaPixels);
                WriteChunk(output, "IEND", Array.Empty<byte>(), 0, 0);
            }
        }

        private static void WriteIhdr(Stream output, int width, int height)
        {
            var ihdr = new byte[13];
            WriteBigEndianInt32(ihdr, 0, width);
            WriteBigEndianInt32(ihdr, 4, height);
            ihdr[8] = 8;
            ihdr[9] = 6;
            ihdr[10] = 0;
            ihdr[11] = 0;
            ihdr[12] = 0;
            WriteChunk(output, "IHDR", ihdr, 0, ihdr.Length);
        }

        private static void WriteIdat(Stream output, int width, int height, byte[] rgbaPixels)
        {
            byte[] compressed;

            using (var buffer = new MemoryStream())
            {
                buffer.WriteByte(0x78);
                buffer.WriteByte(0x01);

                uint adler = 1;
                int stride = width * 4;
                var filterByte = new byte[] { 0 };

                using (var deflate = new DeflateStream(buffer, CompressionLevel.Fastest, true))
                {
                    for (int y = 0; y < height; y++)
                    {
                        deflate.Write(filterByte, 0, 1);
                        adler = UpdateAdler32(adler, filterByte, 0, 1);

                        int srcOffset = y * stride;
                        deflate.Write(rgbaPixels, srcOffset, stride);
                        adler = UpdateAdler32(adler, rgbaPixels, srcOffset, stride);
                    }
                }

                var adlerBytes = new byte[4];
                WriteBigEndianUInt32(adlerBytes, 0, adler);
                buffer.Write(adlerBytes, 0, adlerBytes.Length);

                compressed = buffer.ToArray();
            }

            WriteChunk(output, "IDAT", compressed, 0, compressed.Length);
        }

        private static void WriteChunk(Stream output, string chunkType, byte[] data, int offset, int length)
        {
            var lengthBytes = new byte[4];
            WriteBigEndianInt32(lengthBytes, 0, length);
            output.Write(lengthBytes, 0, lengthBytes.Length);

            var typeBytes = new byte[4]
            {
                (byte)chunkType[0],
                (byte)chunkType[1],
                (byte)chunkType[2],
                (byte)chunkType[3]
            };
            output.Write(typeBytes, 0, typeBytes.Length);

            if (length > 0)
            {
                output.Write(data, offset, length);
            }

            uint crc = 0xFFFFFFFFu;
            crc = UpdateCrc(crc, typeBytes, 0, typeBytes.Length);
            if (length > 0)
            {
                crc = UpdateCrc(crc, data, offset, length);
            }
            crc ^= 0xFFFFFFFFu;

            var crcBytes = new byte[4];
            WriteBigEndianUInt32(crcBytes, 0, crc);
            output.Write(crcBytes, 0, crcBytes.Length);
        }

        private static uint[] CreateCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                {
                    if ((c & 1u) != 0)
                    {
                        c = 0xEDB88320u ^ (c >> 1);
                    }
                    else
                    {
                        c >>= 1;
                    }
                }

                table[n] = c;
            }

            return table;
        }

        private static uint UpdateCrc(uint crc, byte[] data, int offset, int length)
        {
            for (int i = offset; i < offset + length; i++)
            {
                crc = CrcTable[(int)((crc ^ data[i]) & 0xFF)] ^ (crc >> 8);
            }

            return crc;
        }

        private static uint UpdateAdler32(uint adler, byte[] data, int offset, int length)
        {
            uint s1 = adler & 0xFFFFu;
            uint s2 = (adler >> 16) & 0xFFFFu;

            for (int i = offset; i < offset + length; i++)
            {
                s1 += data[i];
                if (s1 >= 65521u)
                {
                    s1 -= 65521u;
                }

                s2 += s1;
                if (s2 >= 65521u)
                {
                    s2 -= 65521u;
                }
            }

            return (s2 << 16) | s1;
        }

        private static void WriteBigEndianInt32(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)((value >> 24) & 0xFF);
            buffer[offset + 1] = (byte)((value >> 16) & 0xFF);
            buffer[offset + 2] = (byte)((value >> 8) & 0xFF);
            buffer[offset + 3] = (byte)(value & 0xFF);
        }

        private static void WriteBigEndianUInt32(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)((value >> 24) & 0xFF);
            buffer[offset + 1] = (byte)((value >> 16) & 0xFF);
            buffer[offset + 2] = (byte)((value >> 8) & 0xFF);
            buffer[offset + 3] = (byte)(value & 0xFF);
        }
    }
}
