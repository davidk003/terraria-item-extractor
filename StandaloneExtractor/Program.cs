using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using StandaloneExtractor.Extractors;
using StandaloneExtractor.Models;
using StandaloneExtractor.Writers;

namespace StandaloneExtractor
{
    internal static class Program
    {
        private const string DefaultTerrariaDirectory = @"C:\Program Files (x86)\Steam\steamapps\common\Terraria";
        private const string WorkerPhaseArgument = "--worker-phase";
        private const string PhaseResultArgument = "--phase-result";

        private static readonly PhaseDefinition[] OrderedPhases =
        {
            new PhaseDefinition("items", "items"),
            new PhaseDefinition("shimmer", "shimmer"),
            new PhaseDefinition("recipes", "recipes"),
            new PhaseDefinition("npc_shops", "npc_shops")
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
                return RunWorker(normalizedArgs);
            }

            return RunOrchestrator(normalizedArgs);
        }

        private static int RunOrchestrator(string[] normalizedArgs)
        {
            string outputDirectory = ResolveOutputDirectory(normalizedArgs);
            Directory.CreateDirectory(outputDirectory);

            string terrariaExePath = ResolveTerrariaExecutablePath(normalizedArgs);
            string terrariaDirectory = Path.GetDirectoryName(terrariaExePath) ?? string.Empty;
            string decompiledDirectory = FindRepositoryDecompiledDirectory();

            Console.WriteLine("StandaloneExtractor");
            Console.WriteLine("Mode: orchestrator");
            Console.WriteLine("Output directory: " + outputDirectory);
            Console.WriteLine("Terraria.exe: " + terrariaExePath);
            Console.WriteLine("Command line: " + string.Join(" ", normalizedArgs));
            Console.WriteLine();

            BootstrapReport bootstrapReport = RunBootstrap("orchestrator", outputDirectory, terrariaExePath, terrariaDirectory, decompiledDirectory);
            Console.WriteLine();

            string resultDirectory = Path.Combine(outputDirectory, "_runtime", "phase-results");
            Directory.CreateDirectory(resultDirectory);

            var phaseResults = new List<PhaseExecutionResult>();
            foreach (PhaseDefinition phase in OrderedPhases)
            {
                PhaseExecutionResult phaseResult = ExecutePhaseInWorker(
                    phase,
                    normalizedArgs,
                    outputDirectory,
                    terrariaExePath,
                    terrariaDirectory,
                    resultDirectory);
                phaseResults.Add(phaseResult);
            }

            PrintFinalSummary(phaseResults, outputDirectory, bootstrapReport);

            return phaseResults.All(r => r.Succeeded) ? 0 : 1;
        }

        private static int RunWorker(string[] normalizedArgs)
        {
            string phaseKey = GetArgumentValue(normalizedArgs, WorkerPhaseArgument);
            string phaseResultPath = GetArgumentValue(normalizedArgs, PhaseResultArgument);
            string outputDirectory = ResolveOutputDirectory(normalizedArgs);

            Directory.CreateDirectory(outputDirectory);

            if (!TryGetPhaseDefinition(phaseKey, out PhaseDefinition phase))
            {
                var unknownPhase = CreateFailedResult("unknown-phase", "unknown", outputDirectory, "Unknown phase key: " + (phaseKey ?? "<null>"));
                WritePhaseResultSafely(phaseResultPath, unknownPhase);
                return 1;
            }

            string terrariaExePath = ResolveTerrariaExecutablePath(normalizedArgs);
            string terrariaDirectory = Path.GetDirectoryName(terrariaExePath) ?? string.Empty;
            string decompiledDirectory = FindRepositoryDecompiledDirectory();

            Console.WriteLine("[Worker] Phase " + phase.Key + " starting");

            BootstrapReport bootstrapReport = RunBootstrap(
                "worker:" + phase.Key,
                outputDirectory,
                terrariaExePath,
                terrariaDirectory,
                decompiledDirectory);

            IJsonWriter jsonWriter = new JsonWriter();
            ICsvWriter csvWriter = new CsvWriter();
            var context = new ExtractionContext(outputDirectory, normalizedArgs);

            PhaseExecutionResult result;

            if (!File.Exists(terrariaExePath))
            {
                result = CreateFailedResult(phase.Key, phase.FileStem, outputDirectory, "Terraria executable was not found: " + terrariaExePath);
                EnsurePlaceholderOutputs(result.JsonPath, result.CsvPath);
            }
            else
            {
                try
                {
                    result = ExecuteConcretePhase(phase.Key, phase.FileStem, context, jsonWriter, csvWriter);
                }
                catch (Exception ex)
                {
                    result = CreateFailedResult(phase.Key, phase.FileStem, outputDirectory, "worker crash: " + ex);
                    foreach (string diagnostic in BuildDependencyDiagnostics(ex, terrariaDirectory, decompiledDirectory))
                    {
                        result.Errors.Add(diagnostic);
                    }

                    EnsurePlaceholderOutputs(result.JsonPath, result.CsvPath);
                }
            }

            if (!result.Succeeded
                && bootstrapReport.DependencyProbe != null
                && bootstrapReport.DependencyProbe.MissingAssemblies.Count > 0)
            {
                string missingMessage = "dependency probe missing assemblies: "
                    + string.Join(", ", bootstrapReport.DependencyProbe.MissingAssemblies.OrderBy(name => name, StringComparer.Ordinal));

                if (!result.Errors.Contains(missingMessage))
                {
                    result.Errors.Add(missingMessage);
                }
            }

            if (!result.Succeeded
                && !string.IsNullOrWhiteSpace(bootstrapReport.DependencyProbeError)
                && !result.Errors.Contains("dependency probe error: " + bootstrapReport.DependencyProbeError))
            {
                result.Errors.Add("dependency probe error: " + bootstrapReport.DependencyProbeError);
            }

            EnsurePlaceholderOutputs(result.JsonPath, result.CsvPath);

            WritePhaseResultSafely(phaseResultPath, result);

            Console.WriteLine("[Worker] Phase " + phase.Key + " " + (result.Succeeded ? "PASS" : "FAIL")
                + " (rows=" + result.RowCount + ")");

            return result.Succeeded ? 0 : 1;
        }

        private static PhaseExecutionResult ExecutePhaseInWorker(
            PhaseDefinition phase,
            string[] normalizedArgs,
            string outputDirectory,
            string terrariaExePath,
            string terrariaDirectory,
            string resultDirectory)
        {
            string executablePath = Assembly.GetExecutingAssembly().Location;
            string resultPath = Path.Combine(resultDirectory, phase.Key + ".json");
            string jsonPath = Path.Combine(outputDirectory, phase.FileStem + ".json");
            string csvPath = Path.Combine(outputDirectory, phase.FileStem + ".csv");

            if (File.Exists(resultPath))
            {
                File.Delete(resultPath);
            }

            var workerArguments = new List<string>
            {
                WorkerPhaseArgument,
                phase.Key,
                PhaseResultArgument,
                resultPath,
                "--output",
                outputDirectory,
                "--terraria",
                terrariaExePath,
                "--terraria-dir",
                terrariaDirectory
            };

            Console.WriteLine("[Orchestrator] Starting isolated phase " + phase.Key + "...");

            int exitCode = RunProcess(executablePath, workerArguments, out string stdout, out string stderr);
            PrintProcessOutput("worker:" + phase.Key, stdout, stderr);

            PhaseExecutionResult result = TryReadPhaseResult(resultPath)
                ?? CreateFailedResult(phase.Key, phase.FileStem, outputDirectory, "worker did not produce phase result file");

            result.PhaseKey = phase.Key;

            if (!string.Equals(result.JsonPath, jsonPath, StringComparison.OrdinalIgnoreCase))
            {
                result.JsonPath = jsonPath;
            }

            if (!string.Equals(result.CsvPath, csvPath, StringComparison.OrdinalIgnoreCase))
            {
                result.CsvPath = csvPath;
            }

            if (exitCode != 0)
            {
                result.Succeeded = false;
                result.Errors.Add("worker exit code: " + exitCode);
            }

            if (!File.Exists(jsonPath) || !File.Exists(csvPath))
            {
                result.Succeeded = false;
                result.Errors.Add("worker did not produce full output set; writing placeholders");
                EnsurePlaceholderOutputs(jsonPath, csvPath);
            }

            Console.WriteLine("[Orchestrator] Phase " + phase.Key + " " + (result.Succeeded ? "PASS" : "FAIL")
                + " (rows=" + result.RowCount + ")");
            Console.WriteLine();

            return result;
        }

        private static PhaseExecutionResult ExecuteConcretePhase(
            string phaseKey,
            string fileStem,
            ExtractionContext context,
            IJsonWriter jsonWriter,
            ICsvWriter csvWriter)
        {
            if (string.Equals(phaseKey, "items", StringComparison.Ordinal))
            {
                return RunPhase(new ItemDataExtractor(), context, jsonWriter, csvWriter, fileStem, phaseKey);
            }

            if (string.Equals(phaseKey, "shimmer", StringComparison.Ordinal))
            {
                return RunPhase(new ShimmerDataExtractor(), context, jsonWriter, csvWriter, fileStem, phaseKey);
            }

            if (string.Equals(phaseKey, "recipes", StringComparison.Ordinal))
            {
                return RunPhase(new RecipeDataExtractor(), context, jsonWriter, csvWriter, fileStem, phaseKey);
            }

            if (string.Equals(phaseKey, "npc_shops", StringComparison.Ordinal))
            {
                return RunPhase(new NpcShopDataExtractor(), context, jsonWriter, csvWriter, fileStem, phaseKey);
            }

            return CreateFailedResult(phaseKey, fileStem, context.OutputDirectory, "Unsupported phase key: " + phaseKey);
        }

        private static BootstrapReport RunBootstrap(
            string scope,
            string outputDirectory,
            string terrariaExePath,
            string terrariaDirectory,
            string decompiledDirectory)
        {
            var report = new BootstrapReport();
            report.Scope = scope;

            var stage1 = new BootstrapStageResult
            {
                Stage = "1-paths",
                Succeeded = true,
                Detail = "output=" + outputDirectory
                    + ", terrariaExe=" + terrariaExePath
                    + ", decompiled=" + (string.IsNullOrWhiteSpace(decompiledDirectory) ? "<not-found>" : decompiledDirectory)
            };
            report.Stages.Add(stage1);
            LogBootstrap(scope, stage1);

            bool terrariaExists = File.Exists(terrariaExePath);
            var stage2 = new BootstrapStageResult
            {
                Stage = "2-terraria-exe",
                Succeeded = terrariaExists,
                Detail = terrariaExists
                    ? "found " + terrariaExePath
                    : "missing " + terrariaExePath + " (use --terraria or --terraria-dir to override)"
            };
            report.Stages.Add(stage2);
            LogBootstrap(scope, stage2);

            if (terrariaExists)
            {
                DependencyProbeResult dependencyProbe = ProbeTerrariaDependencies(terrariaExePath, terrariaDirectory, decompiledDirectory);
                report.DependencyProbe = dependencyProbe;
                report.DependencyProbeError = dependencyProbe.ErrorMessage;

                bool probePassed = string.IsNullOrWhiteSpace(dependencyProbe.ErrorMessage)
                    && dependencyProbe.MissingAssemblies.Count == 0;

                string probeDetail;
                if (!string.IsNullOrWhiteSpace(dependencyProbe.ErrorMessage))
                {
                    probeDetail = "probe failed: " + dependencyProbe.ErrorMessage;
                }
                else if (dependencyProbe.MissingAssemblies.Count == 0)
                {
                    probeDetail = "referenced=" + dependencyProbe.ReferencedAssemblyCount + ", missing=0";
                }
                else
                {
                    probeDetail = "referenced=" + dependencyProbe.ReferencedAssemblyCount
                        + ", missing=" + dependencyProbe.MissingAssemblies.Count
                        + " => " + string.Join(", ", dependencyProbe.MissingAssemblies.OrderBy(n => n, StringComparer.Ordinal));
                }

                var stage3 = new BootstrapStageResult
                {
                    Stage = "3-dependency-probe",
                    Succeeded = probePassed,
                    Detail = probeDetail
                };
                report.Stages.Add(stage3);
                LogBootstrap(scope, stage3);
            }
            else
            {
                var skippedStage = new BootstrapStageResult
                {
                    Stage = "3-dependency-probe",
                    Succeeded = false,
                    Detail = "skipped; Terraria.exe is missing"
                };
                report.Stages.Add(skippedStage);
                LogBootstrap(scope, skippedStage);
            }

            bool runtimeDirectoryReady;
            string runtimeDetail;
            try
            {
                Directory.CreateDirectory(outputDirectory);
                Directory.CreateDirectory(Path.Combine(outputDirectory, "_runtime"));
                Directory.CreateDirectory(Path.Combine(outputDirectory, "_runtime", "phase-results"));
                runtimeDirectoryReady = true;
                runtimeDetail = "runtime folders are ready";
            }
            catch (Exception ex)
            {
                runtimeDirectoryReady = false;
                runtimeDetail = "failed to create runtime folders: " + ex.Message;
            }

            var stage4 = new BootstrapStageResult
            {
                Stage = "4-runtime-dirs",
                Succeeded = runtimeDirectoryReady,
                Detail = runtimeDetail
            };
            report.Stages.Add(stage4);
            LogBootstrap(scope, stage4);

            return report;
        }

        private static DependencyProbeResult ProbeTerrariaDependencies(
            string terrariaExePath,
            string terrariaDirectory,
            string decompiledDirectory)
        {
            var result = new DependencyProbeResult();

            try
            {
                Assembly reflectionAssembly = Assembly.ReflectionOnlyLoadFrom(terrariaExePath);
                AssemblyName[] references = reflectionAssembly.GetReferencedAssemblies();
                result.ReferencedAssemblyCount = references.Length;

                foreach (AssemblyName reference in references.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(reference.Name) || IsFrameworkAssembly(reference.Name))
                    {
                        continue;
                    }

                    if (!CanResolveAssembly(reference.Name, terrariaDirectory, decompiledDirectory))
                    {
                        result.MissingAssemblies.Add(reference.Name);
                    }
                }
            }
            catch (Exception ex)
            {
                result.ErrorMessage = ex.Message;
            }

            return result;
        }

        private static bool CanResolveAssembly(string assemblySimpleName, string terrariaDirectory, string decompiledDirectory)
        {
            if (!string.IsNullOrWhiteSpace(terrariaDirectory) && Directory.Exists(terrariaDirectory))
            {
                if (File.Exists(Path.Combine(terrariaDirectory, assemblySimpleName + ".dll"))
                    || File.Exists(Path.Combine(terrariaDirectory, assemblySimpleName + ".exe")))
                {
                    return true;
                }
            }

            string appBase = AppDomain.CurrentDomain.BaseDirectory;
            if (!string.IsNullOrWhiteSpace(appBase) && Directory.Exists(appBase))
            {
                if (File.Exists(Path.Combine(appBase, assemblySimpleName + ".dll"))
                    || File.Exists(Path.Combine(appBase, assemblySimpleName + ".exe")))
                {
                    return true;
                }
            }

            if (!string.IsNullOrWhiteSpace(decompiledDirectory) && Directory.Exists(decompiledDirectory))
            {
                string pattern = "Terraria.Libraries.*." + assemblySimpleName + ".dll";
                return Directory.GetFiles(decompiledDirectory, pattern).Length > 0;
            }

            return false;
        }

        private static bool IsFrameworkAssembly(string assemblySimpleName)
        {
            if (string.IsNullOrWhiteSpace(assemblySimpleName))
            {
                return true;
            }

            if (assemblySimpleName.Equals("mscorlib", StringComparison.OrdinalIgnoreCase)
                || assemblySimpleName.Equals("netstandard", StringComparison.OrdinalIgnoreCase)
                || assemblySimpleName.Equals("WindowsBase", StringComparison.OrdinalIgnoreCase)
                || assemblySimpleName.Equals("PresentationCore", StringComparison.OrdinalIgnoreCase)
                || assemblySimpleName.Equals("PresentationFramework", StringComparison.OrdinalIgnoreCase)
                || assemblySimpleName.Equals("System", StringComparison.OrdinalIgnoreCase)
                || assemblySimpleName.Equals("Microsoft.CSharp", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return assemblySimpleName.StartsWith("System.", StringComparison.OrdinalIgnoreCase);
        }

        private static List<string> BuildDependencyDiagnostics(Exception ex, string terrariaDirectory, string decompiledDirectory)
        {
            var diagnostics = new List<string>();

            List<string> missingDependencyNames = ExtractMissingDependencyNames(ex);
            if (missingDependencyNames.Count > 0)
            {
                diagnostics.Add("missing dependency candidates: " + string.Join(", ", missingDependencyNames));
            }

            if (ex is BadImageFormatException || ContainsExceptionType<BadImageFormatException>(ex))
            {
                diagnostics.Add("detected BadImageFormatException; verify x86 runtime and x86 Terraria dependencies.");
            }

            if (missingDependencyNames.Count > 0)
            {
                diagnostics.Add("ensure missing assemblies exist in: " + terrariaDirectory);

                if (!string.IsNullOrWhiteSpace(decompiledDirectory) && Directory.Exists(decompiledDirectory))
                {
                    diagnostics.Add("fallback library directory detected: " + decompiledDirectory);
                }
                else
                {
                    diagnostics.Add("decompiled fallback libraries not found; run A1 decompilation to populate decompiled/.");
                }

                diagnostics.Add("if needed, pass --terraria <path-to-Terraria.exe> explicitly.");
            }

            return diagnostics;
        }

        private static bool ContainsExceptionType<TException>(Exception ex)
            where TException : Exception
        {
            Exception current = ex;
            while (current != null)
            {
                if (current is TException)
                {
                    return true;
                }

                current = current.InnerException;
            }

            return false;
        }

        private static List<string> ExtractMissingDependencyNames(Exception ex)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<Exception>();
            queue.Enqueue(ex);

            while (queue.Count > 0)
            {
                Exception current = queue.Dequeue();
                if (current == null)
                {
                    continue;
                }

                if (current is FileNotFoundException fileNotFound)
                {
                    string fileNameName = ExtractSimpleAssemblyName(fileNotFound.FileName);
                    if (!string.IsNullOrWhiteSpace(fileNameName))
                    {
                        names.Add(fileNameName);
                    }

                    string messageName = ExtractSimpleAssemblyNameFromMessage(fileNotFound.Message);
                    if (!string.IsNullOrWhiteSpace(messageName))
                    {
                        names.Add(messageName);
                    }
                }

                if (current is FileLoadException fileLoad)
                {
                    string fileNameName = ExtractSimpleAssemblyName(fileLoad.FileName);
                    if (!string.IsNullOrWhiteSpace(fileNameName))
                    {
                        names.Add(fileNameName);
                    }

                    string messageName = ExtractSimpleAssemblyNameFromMessage(fileLoad.Message);
                    if (!string.IsNullOrWhiteSpace(messageName))
                    {
                        names.Add(messageName);
                    }
                }

                if (current is DllNotFoundException)
                {
                    string messageName = ExtractSimpleAssemblyNameFromMessage(current.Message);
                    if (!string.IsNullOrWhiteSpace(messageName))
                    {
                        names.Add(messageName);
                    }
                }

                if (current.InnerException != null)
                {
                    queue.Enqueue(current.InnerException);
                }
            }

            return names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static string ExtractSimpleAssemblyName(string rawName)
        {
            if (string.IsNullOrWhiteSpace(rawName))
            {
                return null;
            }

            string candidate = rawName.Trim();

            try
            {
                var parsed = new AssemblyName(candidate);
                if (!string.IsNullOrWhiteSpace(parsed.Name))
                {
                    return parsed.Name;
                }
            }
            catch
            {
            }

            if (candidate.IndexOf(',') >= 0)
            {
                candidate = candidate.Substring(0, candidate.IndexOf(','));
            }

            if (candidate.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                || candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                candidate = Path.GetFileNameWithoutExtension(candidate);
            }

            candidate = candidate.Trim('"', '\'', ' ');
            return string.IsNullOrWhiteSpace(candidate) ? null : candidate;
        }

        private static string ExtractSimpleAssemblyNameFromMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return null;
            }

            Match quoted = Regex.Match(message, "'([^']+)'");
            if (quoted.Success)
            {
                return ExtractSimpleAssemblyName(quoted.Groups[1].Value);
            }

            Match dllMatch = Regex.Match(message, "([A-Za-z0-9_.-]+\\.dll)", RegexOptions.IgnoreCase);
            if (dllMatch.Success)
            {
                return ExtractSimpleAssemblyName(dllMatch.Groups[1].Value);
            }

            return null;
        }

        private static int RunProcess(string executablePath, IList<string> arguments, out string stdout, out string stderr)
        {
            var stdoutBuilder = new StringBuilder();
            var stderrBuilder = new StringBuilder();

            var processStartInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = JoinArguments(arguments),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = ResolveExtractorRootDirectory()
            };

            using (var process = new Process())
            {
                process.StartInfo = processStartInfo;
                process.OutputDataReceived += (_, e) =>
                {
                    if (e.Data != null)
                    {
                        stdoutBuilder.AppendLine(e.Data);
                    }
                };

                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data != null)
                    {
                        stderrBuilder.AppendLine(e.Data);
                    }
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();

                stdout = stdoutBuilder.ToString();
                stderr = stderrBuilder.ToString();
                return process.ExitCode;
            }
        }

        private static string JoinArguments(IList<string> arguments)
        {
            return string.Join(" ", arguments.Select(QuoteArgument));
        }

        private static string QuoteArgument(string argument)
        {
            if (argument == null)
            {
                return "\"\"";
            }

            if (argument.Length == 0)
            {
                return "\"\"";
            }

            if (argument.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
            {
                return argument;
            }

            return "\"" + argument.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private static void PrintProcessOutput(string scope, string stdout, string stderr)
        {
            foreach (string line in SplitLines(stdout))
            {
                Console.WriteLine("[" + scope + "] " + line);
            }

            foreach (string line in SplitLines(stderr))
            {
                Console.WriteLine("[" + scope + ":stderr] " + line);
            }
        }

        private static IEnumerable<string> SplitLines(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return Enumerable.Empty<string>();
            }

            return value
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                .Where(line => !string.IsNullOrWhiteSpace(line));
        }

        private static void EnsurePlaceholderOutputs(string jsonPath, string csvPath)
        {
            string jsonDirectory = Path.GetDirectoryName(jsonPath);
            if (!string.IsNullOrWhiteSpace(jsonDirectory))
            {
                Directory.CreateDirectory(jsonDirectory);
            }

            string csvDirectory = Path.GetDirectoryName(csvPath);
            if (!string.IsNullOrWhiteSpace(csvDirectory))
            {
                Directory.CreateDirectory(csvDirectory);
            }

            if (!File.Exists(jsonPath))
            {
                File.WriteAllText(jsonPath, "[]");
            }

            if (!File.Exists(csvPath))
            {
                File.WriteAllText(csvPath, string.Empty);
            }
        }

        private static void WritePhaseResultSafely(string phaseResultPath, PhaseExecutionResult result)
        {
            if (string.IsNullOrWhiteSpace(phaseResultPath))
            {
                return;
            }

            try
            {
                WritePhaseResult(phaseResultPath, result);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[Worker] Failed to persist phase result: " + ex.Message);
            }
        }

        private static void WritePhaseResult(string resultPath, PhaseExecutionResult result)
        {
            string directory = Path.GetDirectoryName(resultPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var lines = new List<string>
            {
                "PhaseName=" + EncodeField(result.PhaseName),
                "PhaseKey=" + EncodeField(result.PhaseKey),
                "RowCount=" + result.RowCount,
                "Succeeded=" + result.Succeeded,
                "JsonPath=" + EncodeField(result.JsonPath),
                "CsvPath=" + EncodeField(result.CsvPath),
                "ElapsedTicks=" + result.Elapsed.Ticks,
                "ErrorCount=" + result.Errors.Count
            };

            for (int i = 0; i < result.Errors.Count; i++)
            {
                lines.Add("Error" + i + "=" + EncodeField(result.Errors[i]));
            }

            File.WriteAllLines(resultPath, lines);
        }

        private static PhaseExecutionResult TryReadPhaseResult(string resultPath)
        {
            if (string.IsNullOrWhiteSpace(resultPath) || !File.Exists(resultPath))
            {
                return null;
            }

            try
            {
                string[] lines = File.ReadAllLines(resultPath);
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    int splitIndex = line.IndexOf('=');
                    if (splitIndex <= 0)
                    {
                        continue;
                    }

                    string key = line.Substring(0, splitIndex);
                    string value = splitIndex + 1 < line.Length ? line.Substring(splitIndex + 1) : string.Empty;
                    map[key] = value;
                }

                var result = new PhaseExecutionResult
                {
                    PhaseName = DecodeField(GetMapValue(map, "PhaseName")) ?? string.Empty,
                    PhaseKey = DecodeField(GetMapValue(map, "PhaseKey")) ?? string.Empty,
                    RowCount = ParseInt(GetMapValue(map, "RowCount")),
                    Succeeded = ParseBool(GetMapValue(map, "Succeeded")),
                    JsonPath = DecodeField(GetMapValue(map, "JsonPath")) ?? string.Empty,
                    CsvPath = DecodeField(GetMapValue(map, "CsvPath")) ?? string.Empty,
                    Elapsed = TimeSpan.FromTicks(ParseLong(GetMapValue(map, "ElapsedTicks")))
                };

                int errorCount = ParseInt(GetMapValue(map, "ErrorCount"));
                for (int i = 0; i < errorCount; i++)
                {
                    string errorValue = DecodeField(GetMapValue(map, "Error" + i));
                    if (!string.IsNullOrWhiteSpace(errorValue))
                    {
                        result.Errors.Add(errorValue);
                    }
                }

                return result;
            }
            catch
            {
                return null;
            }
        }

        private static string GetMapValue(IDictionary<string, string> map, string key)
        {
            return map.TryGetValue(key, out string value) ? value : null;
        }

        private static int ParseInt(string raw)
        {
            return int.TryParse(raw, out int value) ? value : 0;
        }

        private static long ParseLong(string raw)
        {
            return long.TryParse(raw, out long value) ? value : 0L;
        }

        private static bool ParseBool(string raw)
        {
            return bool.TryParse(raw, out bool value) && value;
        }

        private static string EncodeField(string value)
        {
            string safe = value ?? string.Empty;
            byte[] bytes = Encoding.UTF8.GetBytes(safe);
            return Convert.ToBase64String(bytes);
        }

        private static string DecodeField(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            try
            {
                byte[] bytes = Convert.FromBase64String(value);
                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static PhaseExecutionResult CreateFailedResult(string phaseKey, string fileStem, string outputDirectory, string error)
        {
            var result = new PhaseExecutionResult
            {
                PhaseName = phaseKey,
                PhaseKey = phaseKey,
                RowCount = 0,
                JsonPath = Path.Combine(outputDirectory, fileStem + ".json"),
                CsvPath = Path.Combine(outputDirectory, fileStem + ".csv"),
                Elapsed = TimeSpan.Zero,
                Succeeded = false
            };

            if (!string.IsNullOrWhiteSpace(error))
            {
                result.Errors.Add(error);
            }

            return result;
        }

        private static void LogBootstrap(string scope, BootstrapStageResult stage)
        {
            Console.WriteLine("[Bootstrap:" + scope + "] " + stage.Stage + " " + (stage.Succeeded ? "PASS" : "FAIL")
                + " - " + stage.Detail);
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

        private static PhaseExecutionResult RunPhase<T>(
            IExtractorPhase<T> extractor,
            ExtractionContext context,
            IJsonWriter jsonWriter,
            ICsvWriter csvWriter,
            string fileStem,
            string phaseKey)
        {
            string label = extractor.PhaseName;
            string jsonPath = Path.Combine(context.OutputDirectory, fileStem + ".json");
            string csvPath = Path.Combine(context.OutputDirectory, fileStem + ".csv");

            var result = new PhaseExecutionResult
            {
                PhaseName = label,
                PhaseKey = phaseKey,
                JsonPath = jsonPath,
                CsvPath = csvPath
            };

            DateTime startedAt = DateTime.UtcNow;
            List<T> rows = new List<T>();

            Console.WriteLine("[Phase] " + label + " - start");

            try
            {
                rows = (extractor.Extract(context) ?? Enumerable.Empty<T>()).ToList();
                result.RowCount = rows.Count;
                Console.WriteLine("[Phase] " + label + " - extracted " + rows.Count + " rows");

                if (rows.Count == 0)
                {
                    result.Errors.Add("extracted 0 rows");
                }
            }
            catch (Exception ex)
            {
                result.Succeeded = false;
                result.Errors.Add("extract failed: " + ex);
                Console.WriteLine("[Phase] " + label + " - ERROR extracting rows");
                Console.WriteLine(ex.ToString());

                string terrariaPath = ResolveTerrariaExecutablePath(context.CommandLineArgs ?? new string[0]);
                string terrariaDirectory = Path.GetDirectoryName(terrariaPath) ?? string.Empty;
                string decompiledDirectory = FindRepositoryDecompiledDirectory();

                foreach (string diagnostic in BuildDependencyDiagnostics(ex, terrariaDirectory, decompiledDirectory))
                {
                    if (!result.Errors.Contains(diagnostic))
                    {
                        result.Errors.Add(diagnostic);
                    }
                }
            }

            try
            {
                jsonWriter.Write(context.OutputDirectory, fileStem + ".json", rows);
                csvWriter.Write(context.OutputDirectory, fileStem + ".csv", rows);

                Console.WriteLine("[Phase] " + label + " - wrote " + Path.GetFileName(jsonPath) + " and " + Path.GetFileName(csvPath));
            }
            catch (Exception ex)
            {
                result.Succeeded = false;
                result.Errors.Add("write failed: " + ex);
                Console.WriteLine("[Phase] " + label + " - ERROR writing outputs");
                Console.WriteLine(ex.ToString());
            }

            if (!File.Exists(jsonPath))
            {
                result.Succeeded = false;
                result.Errors.Add("missing output: " + jsonPath);
            }

            if (!File.Exists(csvPath))
            {
                result.Succeeded = false;
                result.Errors.Add("missing output: " + csvPath);
            }

            result.Elapsed = DateTime.UtcNow - startedAt;

            if (result.Errors.Count == 0)
            {
                result.Succeeded = true;
            }

            Console.WriteLine(
                "[Phase] " + label
                + " - " + (result.Succeeded ? "completed" : "completed with errors")
                + " (rows=" + result.RowCount + ", elapsed=" + result.Elapsed.TotalSeconds.ToString("F2") + "s)");
            Console.WriteLine();

            return result;
        }

        private static bool TryGetPhaseDefinition(string phaseKey, out PhaseDefinition definition)
        {
            definition = OrderedPhases.FirstOrDefault(
                phase => string.Equals(phase.Key, phaseKey, StringComparison.OrdinalIgnoreCase));
            return definition != null;
        }

        private static string ResolveOutputDirectory(string[] args)
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

        private static string ResolveTerrariaExecutablePath(IList<string> args)
        {
            string terraria = GetArgumentValue(args, "--terraria", "-t");
            if (!string.IsNullOrWhiteSpace(terraria))
            {
                return Path.GetFullPath(terraria);
            }

            string terrariaDirectory = GetArgumentValue(args, "--terraria-dir");
            if (!string.IsNullOrWhiteSpace(terrariaDirectory))
            {
                return Path.Combine(Path.GetFullPath(terrariaDirectory), "Terraria.exe");
            }

            return Path.Combine(DefaultTerrariaDirectory, "Terraria.exe");
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

        private static string GetArgumentValue(IList<string> args, params string[] names)
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

        private static string ResolveExtractorRootDirectory()
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

        private static string FindRepositoryDecompiledDirectory()
        {
            string currentDirectory = Environment.CurrentDirectory;
            string fromCurrent = FindDecompiledFrom(new DirectoryInfo(currentDirectory));
            if (!string.IsNullOrWhiteSpace(fromCurrent))
            {
                return fromCurrent;
            }

            string assemblyDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string fromAssembly = FindDecompiledFrom(new DirectoryInfo(assemblyDirectory));
            if (!string.IsNullOrWhiteSpace(fromAssembly))
            {
                return fromAssembly;
            }

            return null;
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

        private static string FindDecompiledFrom(DirectoryInfo start)
        {
            DirectoryInfo current = start;
            while (current != null)
            {
                string directPath = Path.Combine(current.FullName, "decompiled");
                string directReLogicPath = Path.Combine(directPath, "Terraria.Libraries.ReLogic.ReLogic.dll");
                if (File.Exists(directReLogicPath))
                {
                    return directPath;
                }

                string nestedPath = Path.Combine(current.FullName, "extract-mod", "decompiled");
                string nestedReLogicPath = Path.Combine(nestedPath, "Terraria.Libraries.ReLogic.ReLogic.dll");
                if (File.Exists(nestedReLogicPath))
                {
                    return nestedPath;
                }

                current = current.Parent;
            }

            return null;
        }

        private static void PrintFinalSummary(
            IList<PhaseExecutionResult> phaseResults,
            string outputDirectory,
            BootstrapReport bootstrapReport)
        {
            int successCount = phaseResults.Count(result => result.Succeeded);
            int failureCount = phaseResults.Count - successCount;

            Console.WriteLine("========== Extraction Summary ==========");
            foreach (PhaseExecutionResult result in phaseResults)
            {
                Console.WriteLine("Phase " + result.PhaseName + ": " + (result.Succeeded ? "PASS" : "FAIL")
                    + " | rows=" + result.RowCount
                    + " | json=" + Path.GetFileName(result.JsonPath)
                    + " | csv=" + Path.GetFileName(result.CsvPath)
                    + " | elapsed=" + result.Elapsed.TotalSeconds.ToString("F2") + "s");

                foreach (string error in result.Errors)
                {
                    Console.WriteLine("  - " + error);
                }
            }

            Console.WriteLine("----------------------------------------");
            Console.WriteLine("Bootstrap stage results:");
            foreach (BootstrapStageResult stage in bootstrapReport.Stages)
            {
                Console.WriteLine("  - " + stage.Stage + ": " + (stage.Succeeded ? "PASS" : "FAIL") + " | " + stage.Detail);
            }

            Console.WriteLine("----------------------------------------");
            Console.WriteLine("Output directory: " + outputDirectory);
            Console.WriteLine("Phases passed: " + successCount + "/" + phaseResults.Count + ", failed: " + failureCount);
            Console.WriteLine("Expected output files: 8");
            Console.WriteLine("Troubleshooting guide: README.md#7-troubleshooting");
            Console.WriteLine("========================================");
        }

        private sealed class PhaseDefinition
        {
            public PhaseDefinition(string key, string fileStem)
            {
                Key = key;
                FileStem = fileStem;
            }

            public string Key { get; private set; }

            public string FileStem { get; private set; }
        }

        private sealed class BootstrapReport
        {
            public BootstrapReport()
            {
                Stages = new List<BootstrapStageResult>();
            }

            public string Scope { get; set; }

            public List<BootstrapStageResult> Stages { get; private set; }

            public DependencyProbeResult DependencyProbe { get; set; }

            public string DependencyProbeError { get; set; }
        }

        private sealed class BootstrapStageResult
        {
            public string Stage { get; set; }

            public bool Succeeded { get; set; }

            public string Detail { get; set; }
        }

        private sealed class DependencyProbeResult
        {
            public DependencyProbeResult()
            {
                MissingAssemblies = new List<string>();
            }

            public int ReferencedAssemblyCount { get; set; }

            public List<string> MissingAssemblies { get; private set; }

            public string ErrorMessage { get; set; }
        }

        private sealed class PhaseExecutionResult
        {
            public PhaseExecutionResult()
            {
                Errors = new List<string>();
            }

            public string PhaseName { get; set; }

            public string PhaseKey { get; set; }

            public int RowCount { get; set; }

            public bool Succeeded { get; set; }

            public string JsonPath { get; set; }

            public string CsvPath { get; set; }

            public TimeSpan Elapsed { get; set; }

            public List<string> Errors { get; private set; }
        }
    }
}
