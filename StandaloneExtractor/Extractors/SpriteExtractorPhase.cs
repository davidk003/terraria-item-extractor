using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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

            var manifestRows = new ConcurrentBag<SpriteManifestRow>();
            int maxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount);

            Parallel.ForEach(
                assets,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = maxDegreeOfParallelism
                },
                asset =>
            {
                try
                {
                    XnbTextureData texture = XnbReader.ReadTexture2D(asset.SourcePath);

                    string outputPngPath = Path.Combine(asset.OutputDirectory, asset.PngFileName);
                    PngWriter.WritePng(outputPngPath, texture.Width, texture.Height, texture.RgbaPixels);

                    manifestRows.Add(new SpriteManifestRow
                    {
                        Id = asset.Id,
                        Category = asset.Category,
                        InternalName = ResolveInternalName(asset, itemInternalNames),
                        SpriteFile = asset.ManifestPath,
                        Width = texture.Width,
                        Height = texture.Height
                    });

                    Interlocked.Increment(ref saved);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    Console.WriteLine("[sprites] failed to decode " + Path.GetFileName(asset.SourcePath) + ": " + ex.Message);
                }

                int current = Interlocked.Increment(ref processed);
                if (current % 500 == 0 || current == total)
                {
                    Console.WriteLine(
                        "[sprites] progress " + current + "/" + total
                        + " | saved=" + Volatile.Read(ref saved)
                        + " | failed=" + Volatile.Read(ref failed));
                }
            });

            Console.WriteLine("[sprites] extracted " + manifestRows.Count + " sprites (failed=" + Volatile.Read(ref failed) + ")");

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
