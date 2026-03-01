using System;

namespace StandaloneExtractor.Helpers
{
    public static class DxtDecoder
    {
        public static byte[] DecodeDxt1(byte[] data, int width, int height)
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
                    if (offset + 8 > data.Length)
                    {
                        throw new ArgumentException("DXT1 data length is smaller than expected for the provided dimensions.", nameof(data));
                    }

                    DecodeDxt1Block(data, offset, rgba, width, height, bx, by);
                    offset += 8;
                }
            }

            return rgba;
        }

        public static byte[] DecodeDxt3(byte[] data, int width, int height)
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
                    if (offset + 16 > data.Length)
                    {
                        throw new ArgumentException("DXT3 data length is smaller than expected for the provided dimensions.", nameof(data));
                    }

                    DecodeDxt3Block(data, offset, rgba, width, height, bx, by);
                    offset += 16;
                }
            }

            return rgba;
        }

        public static byte[] DecodeDxt5(byte[] data, int width, int height)
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
                    if (offset + 16 > data.Length)
                    {
                        throw new ArgumentException("DXT5 data length is smaller than expected for the provided dimensions.", nameof(data));
                    }

                    DecodeDxt5Block(data, offset, rgba, width, height, bx, by);
                    offset += 16;
                }
            }

            return rgba;
        }

        private static void DecodeDxt1Block(byte[] src, int srcOffset, byte[] dst, int width, int height, int blockX, int blockY)
        {
            ushort c0 = (ushort)(src[srcOffset] | (src[srcOffset + 1] << 8));
            ushort c1 = (ushort)(src[srcOffset + 2] | (src[srcOffset + 3] << 8));
            uint code = (uint)(src[srcOffset + 4]
                | (src[srcOffset + 5] << 8)
                | (src[srcOffset + 6] << 16)
                | (src[srcOffset + 7] << 24));

            var colors = new byte[16];
            DecodeRgb565(c0, colors, 0);
            colors[3] = 255;
            DecodeRgb565(c1, colors, 4);
            colors[7] = 255;

            if (c0 > c1)
            {
                colors[8] = (byte)((2 * colors[0] + colors[4]) / 3);
                colors[9] = (byte)((2 * colors[1] + colors[5]) / 3);
                colors[10] = (byte)((2 * colors[2] + colors[6]) / 3);
                colors[11] = 255;

                colors[12] = (byte)((colors[0] + 2 * colors[4]) / 3);
                colors[13] = (byte)((colors[1] + 2 * colors[5]) / 3);
                colors[14] = (byte)((colors[2] + 2 * colors[6]) / 3);
                colors[15] = 255;
            }
            else
            {
                colors[8] = (byte)((colors[0] + colors[4]) / 2);
                colors[9] = (byte)((colors[1] + colors[5]) / 2);
                colors[10] = (byte)((colors[2] + colors[6]) / 2);
                colors[11] = 255;

                colors[12] = 0;
                colors[13] = 0;
                colors[14] = 0;
                colors[15] = 0;
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

            int colorOffset = srcOffset + 8;
            ushort c0 = (ushort)(src[colorOffset] | (src[colorOffset + 1] << 8));
            ushort c1 = (ushort)(src[colorOffset + 2] | (src[colorOffset + 3] << 8));
            uint code = (uint)(src[colorOffset + 4]
                | (src[colorOffset + 5] << 8)
                | (src[colorOffset + 6] << 16)
                | (src[colorOffset + 7] << 24));

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

            int colorOffset = srcOffset + 8;
            ushort c0 = (ushort)(src[colorOffset] | (src[colorOffset + 1] << 8));
            ushort c1 = (ushort)(src[colorOffset + 2] | (src[colorOffset + 3] << 8));
            uint code = (uint)(src[colorOffset + 4]
                | (src[colorOffset + 5] << 8)
                | (src[colorOffset + 6] << 16)
                | (src[colorOffset + 7] << 24));

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
