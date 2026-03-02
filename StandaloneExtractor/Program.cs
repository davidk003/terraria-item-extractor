using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using CommandLine;

namespace StandaloneExtractor
{
    internal static class Program
    {
        internal const string WorkerPhaseArgument = "--worker-phase";
        internal const string PhaseResultArgument = "--phase-result";

        private sealed class CliOptions
        {
            [Option('o', "output")]
            public string OutputDirectory { get; set; }

            [Option('t', "terraria")]
            public string TerrariaPath { get; set; }

            [Option("terraria-dir")]
            public string TerrariaDirectory { get; set; }

            [Option("worker-phase")]
            public string WorkerPhase { get; set; }

            [Option("phase-result")]
            public string PhaseResultPath { get; set; }

            [Option('h', "help")]
            public bool Help { get; set; }
        }

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
            string[] sanitizedArgs = NormalizeHelpAliases(args);
            CliOptions parsedOptions = ParseCliOptions(sanitizedArgs);

            if (parsedOptions.Help)
            {
                PrintHelp();
                return 0;
            }

            string[] normalizedArgs = NormalizeArguments(sanitizedArgs, parsedOptions);
            if (IsWorkerInvocation(normalizedArgs, parsedOptions))
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

        private static bool IsWorkerInvocation(IList<string> args, CliOptions options)
        {
            return !string.IsNullOrWhiteSpace(options.WorkerPhase)
                || args.Any(arg => string.Equals(arg, WorkerPhaseArgument, StringComparison.OrdinalIgnoreCase));
        }

        private static string[] NormalizeHelpAliases(string[] args)
        {
            if (args == null || args.Length == 0)
            {
                return new string[0];
            }

            return args
                .Select(arg => string.Equals(arg, "/?", StringComparison.OrdinalIgnoreCase) ? "--help" : arg)
                .ToArray();
        }

        private static CliOptions ParseCliOptions(string[] args)
        {
            var parser = new Parser(settings =>
            {
                settings.IgnoreUnknownArguments = true;
                settings.HelpWriter = null;
            });

            CliOptions parsed = null;
            parser
                .ParseArguments<CliOptions>(args ?? new string[0])
                .WithParsed(options => parsed = options);

            if (parsed != null)
            {
                return parsed;
            }

            IList<string> fallbackArgs = args ?? new string[0];
            return new CliOptions
            {
                OutputDirectory = GetArgumentValue(fallbackArgs, "--output", "-o"),
                TerrariaPath = GetArgumentValue(fallbackArgs, "--terraria", "-t"),
                TerrariaDirectory = GetArgumentValue(fallbackArgs, "--terraria-dir"),
                WorkerPhase = GetArgumentValue(fallbackArgs, WorkerPhaseArgument),
                PhaseResultPath = GetArgumentValue(fallbackArgs, PhaseResultArgument),
                Help = fallbackArgs.Any(arg => string.Equals(arg, "--help", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(arg, "-h", StringComparison.OrdinalIgnoreCase))
            };
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

        private static string[] NormalizeArguments(string[] args, CliOptions parsedOptions)
        {
            var normalized = new List<string>(args ?? new string[0]);
            string terrariaPath = parsedOptions.TerrariaPath;
            string terrariaDirectory = parsedOptions.TerrariaDirectory;

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
