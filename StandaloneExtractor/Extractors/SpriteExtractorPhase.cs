using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using StandaloneExtractor.Helpers;
using StandaloneExtractor.Models;

namespace StandaloneExtractor.Extractors
{
    public sealed class SpriteExtractorPhase : IExtractorPhase<SpriteManifestRow>
    {
        private const string DefaultTerrariaExePath = @"C:\Program Files (x86)\Steam\steamapps\common\Terraria\Terraria.exe";

        public string PhaseName
        {
            get { return "sprites"; }
        }

        public IEnumerable<SpriteManifestRow> Extract(ExtractionContext context)
        {
            string terrariaExePath = ResolveTerrariaPath(context == null ? null : context.CommandLineArgs);
            if (!File.Exists(terrariaExePath))
            {
                Console.WriteLine("[sprites] Terraria.exe not found: " + terrariaExePath);
                return new List<SpriteManifestRow>();
            }

            string terrariaDirectory = Path.GetDirectoryName(terrariaExePath) ?? string.Empty;
            string imagesDirectory = Path.Combine(terrariaDirectory, "Content", "Images");
            if (!Directory.Exists(imagesDirectory))
            {
                Console.WriteLine("[sprites] Content/Images directory not found: " + imagesDirectory);
                return new List<SpriteManifestRow>();
            }

            string outputDirectory = context == null ? string.Empty : context.OutputDirectory;
            string spriteRootDirectory = Path.Combine(outputDirectory, "sprites");
            string itemOutputDirectory = Path.Combine(spriteRootDirectory, "items");
            string npcOutputDirectory = Path.Combine(spriteRootDirectory, "npcs");

            Directory.CreateDirectory(itemOutputDirectory);
            Directory.CreateDirectory(npcOutputDirectory);

            Dictionary<int, string> itemInternalNames = LoadItemInternalNameMap(outputDirectory);

            List<SpriteAsset> assets = EnumerateAssets(imagesDirectory, "Item_", "item", "items", itemOutputDirectory)
                .Concat(EnumerateAssets(imagesDirectory, "NPC_", "npc", "npcs", npcOutputDirectory))
                .OrderBy(a => a.CategorySortOrder)
                .ThenBy(a => a.Id)
                .ToList();

            int total = assets.Count;
            int processed = 0;
            int saved = 0;
            int failed = 0;

            var manifestRows = new List<SpriteManifestRow>(total);

            foreach (SpriteAsset asset in assets)
            {
                processed++;
                try
                {
                    XnbTextureData texture = XnbReader.ReadTexture2D(asset.SourcePath);
                    string outputPngPath = Path.Combine(asset.OutputDirectory, asset.PngFileName);
                    SaveRgbaPng(outputPngPath, texture.Width, texture.Height, texture.RgbaPixels);

                    manifestRows.Add(new SpriteManifestRow
                    {
                        Id = asset.Id,
                        Category = asset.Category,
                        InternalName = ResolveInternalName(asset, itemInternalNames),
                        SpriteFile = asset.ManifestPath,
                        Width = texture.Width,
                        Height = texture.Height
                    });

                    saved++;
                }
                catch (Exception ex)
                {
                    failed++;
                    Console.WriteLine("[sprites] failed to decode " + Path.GetFileName(asset.SourcePath) + ": " + ex.Message);
                }

                if (processed % 500 == 0 || processed == total)
                {
                    Console.WriteLine("[sprites] progress " + processed + "/" + total + " | saved=" + saved + " | failed=" + failed);
                }
            }

            Console.WriteLine("[sprites] extracted " + manifestRows.Count + " sprites (failed=" + failed + ")");

            return manifestRows
                .OrderBy(row => string.Equals(row.Category, "item", StringComparison.Ordinal) ? 0 : 1)
                .ThenBy(row => row.Id)
                .ToList();
        }

        private static IEnumerable<SpriteAsset> EnumerateAssets(
            string imagesDirectory,
            string filePrefix,
            string category,
            string manifestDirectoryName,
            string outputDirectory)
        {
            string pattern = filePrefix + "*.xnb";
            foreach (string path in Directory.EnumerateFiles(imagesDirectory, pattern, SearchOption.TopDirectoryOnly))
            {
                string fileName = Path.GetFileName(path);
                if (!TryParseNumericId(fileName, filePrefix, out int id))
                {
                    continue;
                }

                string pngName = Path.GetFileNameWithoutExtension(fileName) + ".png";
                yield return new SpriteAsset
                {
                    Id = id,
                    Category = category,
                    CategorySortOrder = string.Equals(category, "item", StringComparison.Ordinal) ? 0 : 1,
                    SourcePath = path,
                    OutputDirectory = outputDirectory,
                    PngFileName = pngName,
                    ManifestPath = "sprites/" + manifestDirectoryName + "/" + pngName
                };
            }
        }

        private static bool TryParseNumericId(string fileName, string prefix, out int id)
        {
            id = 0;
            if (string.IsNullOrWhiteSpace(fileName)
                || !fileName.EndsWith(".xnb", StringComparison.OrdinalIgnoreCase)
                || !fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string value = fileName.Substring(prefix.Length, fileName.Length - prefix.Length - 4);
            return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out id);
        }

        private static string ResolveInternalName(SpriteAsset asset, IDictionary<int, string> itemInternalNames)
        {
            if (string.Equals(asset.Category, "item", StringComparison.Ordinal))
            {
                if (itemInternalNames.TryGetValue(asset.Id, out string itemName) && !string.IsNullOrWhiteSpace(itemName))
                {
                    return itemName;
                }

                return "Item_" + asset.Id.ToString(CultureInfo.InvariantCulture);
            }

            return "NPC_" + asset.Id.ToString(CultureInfo.InvariantCulture);
        }

        private static Dictionary<int, string> LoadItemInternalNameMap(string outputDirectory)
        {
            string itemsJsonPath = Path.Combine(outputDirectory ?? string.Empty, "items.json");
            if (!File.Exists(itemsJsonPath))
            {
                return new Dictionary<int, string>();
            }

            try
            {
                using (var stream = File.OpenRead(itemsJsonPath))
                {
                    var serializer = new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(List<ItemRow>));
                    var rows = serializer.ReadObject(stream) as List<ItemRow>;
                    if (rows == null)
                    {
                        return new Dictionary<int, string>();
                    }

                    var map = new Dictionary<int, string>();
                    foreach (ItemRow row in rows)
                    {
                        if (row == null || row.Id <= 0)
                        {
                            continue;
                        }

                        if (!map.ContainsKey(row.Id))
                        {
                            map[row.Id] = row.InternalName ?? string.Empty;
                        }
                    }

                    return map;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[sprites] warning: failed to read items.json internal names: " + ex.Message);
                return new Dictionary<int, string>();
            }
        }

        private static string ResolveTerrariaPath(IList<string> args)
        {
            if (args != null)
            {
                for (int i = 0; i < args.Count; i++)
                {
                    if ((string.Equals(args[i], "--terraria", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(args[i], "-t", StringComparison.OrdinalIgnoreCase))
                        && i + 1 < args.Count
                        && !string.IsNullOrWhiteSpace(args[i + 1]))
                    {
                        return Path.GetFullPath(args[i + 1]);
                    }

                    if (string.Equals(args[i], "--terraria-dir", StringComparison.OrdinalIgnoreCase)
                        && i + 1 < args.Count
                        && !string.IsNullOrWhiteSpace(args[i + 1]))
                    {
                        return Path.Combine(Path.GetFullPath(args[i + 1]), "Terraria.exe");
                    }
                }
            }

            return DefaultTerrariaExePath;
        }

        private static void SaveRgbaPng(string outputPath, int width, int height, byte[] rgbaPixels)
        {
            if (rgbaPixels == null)
            {
                throw new ArgumentNullException(nameof(rgbaPixels));
            }

            int expectedLength = checked(width * height * 4);
            if (rgbaPixels.Length < expectedLength)
            {
                throw new InvalidDataException(
                    "RGBA buffer is smaller than expected. got=" + rgbaPixels.Length + ", expected=" + expectedLength);
            }

            string outputDirectory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            using (var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb))
            {
                Rectangle rect = new Rectangle(0, 0, width, height);
                BitmapData bitmapData = bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try
                {
                    int srcStride = width * 4;
                    int dstStride = bitmapData.Stride;
                    int absDstStride = Math.Abs(dstStride);
                    IntPtr basePointer = dstStride > 0
                        ? bitmapData.Scan0
                        : IntPtr.Add(bitmapData.Scan0, dstStride * (height - 1));

                    var rowBuffer = new byte[srcStride];
                    for (int y = 0; y < height; y++)
                    {
                        int srcOffset = y * srcStride;
                        for (int x = 0; x < width; x++)
                        {
                            int srcPixel = srcOffset + x * 4;
                            int dstPixel = x * 4;
                            rowBuffer[dstPixel] = rgbaPixels[srcPixel + 2];
                            rowBuffer[dstPixel + 1] = rgbaPixels[srcPixel + 1];
                            rowBuffer[dstPixel + 2] = rgbaPixels[srcPixel];
                            rowBuffer[dstPixel + 3] = rgbaPixels[srcPixel + 3];
                        }

                        IntPtr rowPointer = IntPtr.Add(basePointer, y * absDstStride);
                        Marshal.Copy(rowBuffer, 0, rowPointer, srcStride);
                    }
                }
                finally
                {
                    bitmap.UnlockBits(bitmapData);
                }

                bitmap.Save(outputPath, ImageFormat.Png);
            }
        }

        private sealed class SpriteAsset
        {
            public int Id { get; set; }

            public string Category { get; set; }

            public int CategorySortOrder { get; set; }

            public string SourcePath { get; set; }

            public string OutputDirectory { get; set; }

            public string PngFileName { get; set; }

            public string ManifestPath { get; set; }
        }
    }
}
