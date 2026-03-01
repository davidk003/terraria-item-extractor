using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace StandaloneExtractor.Helpers
{
    public sealed class XnbTextureData
    {
        public XnbTextureData(int width, int height, int surfaceFormat, string typeReader, byte[] rgbaPixels)
        {
            Width = width;
            Height = height;
            SurfaceFormat = surfaceFormat;
            TypeReader = typeReader ?? string.Empty;
            RgbaPixels = rgbaPixels ?? Array.Empty<byte>();
        }

        public int Width { get; private set; }

        public int Height { get; private set; }

        public int SurfaceFormat { get; private set; }

        public string TypeReader { get; private set; }

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

                bool isCompressed = (flags & CompressedFlag) != 0;
                bool isLz4 = (flags & Lz4Flag) != 0;

                byte[] payload;
                if (isCompressed)
                {
                    if (isLz4)
                    {
                        throw new NotSupportedException("XNB LZ4-compressed payloads are not supported by this extractor.");
                    }

                    int decompressedSize = reader.ReadInt32();
                    int compressedSize = declaredFileSize - 14;
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

                    payload = LzxDecoder.DecompressXnbPayload(compressedPayload, decompressedSize);
                }
                else
                {
                    int payloadSize = declaredFileSize - 10;
                    int bytesRemaining = checked((int)Math.Max(0L, fileStream.Length - fileStream.Position));
                    if (payloadSize < 0 || payloadSize > bytesRemaining)
                    {
                        payloadSize = bytesRemaining;
                    }

                    payload = reader.ReadBytes(payloadSize);
                    if (payload.Length != payloadSize)
                    {
                        throw new EndOfStreamException("Unexpected end of XNB stream while reading payload.");
                    }
                }

                using (var payloadStream = new MemoryStream(payload, writable: false))
                using (var payloadReader = new BinaryReader(payloadStream, Encoding.UTF8))
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

                    byte[] rgba;
                    if (surfaceFormat == SurfaceFormatColor)
                    {
                        int expected = checked(width * height * 4);
                        if (mip0Data.Length < expected)
                        {
                            throw new InvalidDataException(
                                "Color texture data length is smaller than expected. got=" + mip0Data.Length + ", expected=" + expected);
                        }

                        rgba = new byte[expected];
                        Buffer.BlockCopy(mip0Data, 0, rgba, 0, expected);
                    }
                    else if (surfaceFormat == SurfaceFormatDxt1)
                    {
                        rgba = DxtDecoder.DecodeDxt1(mip0Data, width, height);
                    }
                    else if (surfaceFormat == SurfaceFormatDxt3)
                    {
                        rgba = DxtDecoder.DecodeDxt3(mip0Data, width, height);
                    }
                    else if (surfaceFormat == SurfaceFormatDxt5)
                    {
                        rgba = DxtDecoder.DecodeDxt5(mip0Data, width, height);
                    }
                    else
                    {
                        throw new NotSupportedException("Unsupported Texture2D surface format: " + surfaceFormat + ".");
                    }

                    return new XnbTextureData(width, height, surfaceFormat, primaryReader, rgba);
                }
            }
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
    }
}
