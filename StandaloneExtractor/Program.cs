using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace StandaloneExtractor
{
    internal static class Program
    {
        internal const string WorkerPhaseArgument = "--worker-phase";
        internal const string PhaseResultArgument = "--phase-result";

        internal static readonly PhaseDefinition[] OrderedPhases =
        {
            new PhaseDefinition("items", "items"),
            new PhaseDefinition("shimmer", "shimmer"),
            new PhaseDefinition("recipes", "recipes"),
            new PhaseDefinition("npc_shops", "npc_shops"),
            new PhaseDefinition("sprites", "sprite_manifest")
        };

        private static int Main(string[] args)
        {
            if (HasHelpFlag(args))
            {
                PrintHelp();
                return 0;
            }

            string[] normalizedArgs = NormalizeArguments(args);
            if (HasWorkerFlag(normalizedArgs))
            {
                return WorkerRunner.RunWorker(normalizedArgs);
            }

            return Orchestrator.RunOrchestrator(normalizedArgs, OrderedPhases);
        }

        internal static bool TryGetPhaseDefinition(string phaseKey, out PhaseDefinition definition)
        {
            definition = OrderedPhases.FirstOrDefault(
                phase => string.Equals(phase.Key, phaseKey, StringComparison.OrdinalIgnoreCase));
            return definition != null;
        }

        internal static string ResolveOutputDirectory(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--output", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(args[i], "-o", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length && !string.IsNullOrWhiteSpace(args[i + 1]))
                    {
                        return Path.GetFullPath(args[i + 1]);
                    }

                    Console.WriteLine("Missing value for --output/-o. Falling back to default Output directory.");
                }
            }

            return Path.GetFullPath(Path.Combine(ResolveExtractorRootDirectory(), "Output"));
        }

        internal static string GetArgumentValue(IList<string> args, params string[] names)
        {
            for (int i = 0; i < args.Count; i++)
            {
                bool nameMatched = names.Any(name => string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase));
                if (!nameMatched)
                {
                    continue;
                }

                if (i + 1 < args.Count && !string.IsNullOrWhiteSpace(args[i + 1]))
                {
                    return args[i + 1];
                }
            }

            return null;
        }

        internal static string ResolveExtractorRootDirectory()
        {
            string currentDirectory = Environment.CurrentDirectory;
            string fromCurrent = FindExtractorRootFrom(new DirectoryInfo(currentDirectory));
            if (!string.IsNullOrWhiteSpace(fromCurrent))
            {
                return fromCurrent;
            }

            string assemblyDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string fromAssembly = FindExtractorRootFrom(new DirectoryInfo(assemblyDirectory));
            if (!string.IsNullOrWhiteSpace(fromAssembly))
            {
                return fromAssembly;
            }

            return currentDirectory;
        }

        private static bool HasWorkerFlag(IList<string> args)
        {
            return args.Any(arg => string.Equals(arg, WorkerPhaseArgument, StringComparison.OrdinalIgnoreCase));
        }

        private static bool HasHelpFlag(string[] args)
        {
            return args.Any(arg => string.Equals(arg, "--help", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "-h", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "/?", StringComparison.OrdinalIgnoreCase));
        }

        private static void PrintHelp()
        {
            Console.WriteLine("StandaloneExtractor");
            Console.WriteLine("Usage:");
            Console.WriteLine("  StandaloneExtractor.exe [--output <path>] [--terraria <Terraria.exe>] [--terraria-dir <directory>]");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  --output, -o       Output directory for JSON/CSV files.");
            Console.WriteLine("  --terraria, -t     Full path to Terraria.exe.");
            Console.WriteLine("  --terraria-dir     Directory containing Terraria.exe.");
            Console.WriteLine("  --help, -h, /? Show this help text.");
        }

        private static string[] NormalizeArguments(string[] args)
        {
            var normalized = new List<string>(args ?? new string[0]);
            string terrariaPath = GetArgumentValue(normalized, "--terraria", "-t");
            string terrariaDirectory = GetArgumentValue(normalized, "--terraria-dir");

            if (!string.IsNullOrWhiteSpace(terrariaPath) && string.IsNullOrWhiteSpace(terrariaDirectory))
            {
                string parentDirectory = Path.GetDirectoryName(Path.GetFullPath(terrariaPath));
                if (!string.IsNullOrWhiteSpace(parentDirectory))
                {
                    normalized.Add("--terraria-dir");
                    normalized.Add(parentDirectory);
                }
            }

            if (string.IsNullOrWhiteSpace(terrariaPath) && !string.IsNullOrWhiteSpace(terrariaDirectory))
            {
                normalized.Add("--terraria");
                normalized.Add(Path.Combine(Path.GetFullPath(terrariaDirectory), "Terraria.exe"));
            }

            return normalized.ToArray();
        }

        private static string FindExtractorRootFrom(DirectoryInfo start)
        {
            DirectoryInfo current = start;
            while (current != null)
            {
                string directProjectPath = Path.Combine(current.FullName, "StandaloneExtractor.csproj");
                if (File.Exists(directProjectPath))
                {
                    return current.FullName;
                }

                string nestedProjectPath = Path.Combine(current.FullName, "extract-mod", "StandaloneExtractor", "StandaloneExtractor.csproj");
                if (File.Exists(nestedProjectPath))
                {
                    return Path.GetDirectoryName(nestedProjectPath);
                }

                current = current.Parent;
            }

            return null;
        }

    }
}
