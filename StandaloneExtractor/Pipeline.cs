using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization.Json;
using System.Text;

namespace StandaloneExtractor
{
    // === JSON Writer ===

    internal sealed class JsonWriter
    {
        public void Write<T>(string outputDirectory, string fileName, IEnumerable<T> rows)
        {
            Directory.CreateDirectory(outputDirectory);
            string filePath = Path.Combine(outputDirectory, fileName);
            List<T> materializedRows = rows == null ? new List<T>() : rows.ToList();

            var serializer = new DataContractJsonSerializer(typeof(List<T>));
            using (var stream = File.Create(filePath))
            {
                serializer.WriteObject(stream, materializedRows);
            }
        }
    }

    // === CSV Writer ===

    internal sealed class CsvWriter
    {
        public void Write<T>(string outputDirectory, string fileName, IEnumerable<T> rows)
        {
            Directory.CreateDirectory(outputDirectory);
            string filePath = Path.Combine(outputDirectory, fileName);
            T[] materializedRows = rows == null ? new T[0] : rows.ToArray();
            PropertyInfo[] properties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);

            using (var writer = new StreamWriter(filePath, false))
            {
                if (properties.Length > 0)
                {
                    writer.WriteLine(string.Join(",", properties.Select(p => Escape(p.Name))));
                }

                foreach (T row in materializedRows)
                {
                    string line = string.Join(",", properties.Select(p => Escape(ToCsvCell(p.GetValue(row, null)))));
                    writer.WriteLine(line);
                }
            }
        }

        private static string ToCsvCell(object value)
        {
            if (value == null)
            {
                return string.Empty;
            }

            var enumerable = value as System.Collections.IEnumerable;
            if (!(value is string) && enumerable != null)
            {
                var items = new List<string>();
                foreach (object item in enumerable)
                {
                    items.Add(item == null ? string.Empty : Convert.ToString(item, CultureInfo.InvariantCulture));
                }

                return string.Join(";", items);
            }

            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static string Escape(string value)
        {
            if (value == null)
            {
                return string.Empty;
            }

            string escaped = value.Replace("\"", "\"\"");
            if (escaped.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0)
            {
                return "\"" + escaped + "\"";
            }

            return escaped;
        }
    }

    // === Orchestrator ===

    internal static class Orchestrator
    {
        internal static int RunOrchestrator(string[] normalizedArgs, PhaseDefinition[] orderedPhases)
        {
            string outputDirectory = Program.ResolveOutputDirectory(normalizedArgs);
            Directory.CreateDirectory(outputDirectory);

            string terrariaExePath = TerrariaPathResolver.ResolveTerrariaExePath(normalizedArgs);
            string terrariaDirectory = Path.GetDirectoryName(terrariaExePath) ?? string.Empty;
            string decompiledDirectory = TerrariaPathResolver.FindRepositoryDecompiledDirectory();

            Console.WriteLine("StandaloneExtractor");
            Console.WriteLine("Mode: orchestrator");
            Console.WriteLine("Output directory: " + outputDirectory);
            Console.WriteLine("Terraria.exe: " + terrariaExePath);
            Console.WriteLine("Command line: " + string.Join(" ", normalizedArgs));
            Console.WriteLine();

            BootstrapReport bootstrapReport = BootstrapRunner.RunBootstrap("orchestrator", outputDirectory, terrariaExePath, terrariaDirectory, decompiledDirectory);
            Console.WriteLine();

            string resultDirectory = Path.Combine(outputDirectory, "_runtime", "phase-results");
            Directory.CreateDirectory(resultDirectory);

            var phaseResults = new List<PhaseExecutionResult>();
            foreach (PhaseDefinition phase in orderedPhases)
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

            PrintFinalSummary(phaseResults, outputDirectory, bootstrapReport, orderedPhases.Length);

            return phaseResults.All(r => r.Succeeded) ? 0 : 1;
        }

        internal static void EnsurePlaceholderOutputs(string jsonPath, string csvPath)
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

        internal static void WritePhaseResultSafely(string phaseResultPath, PhaseExecutionResult result)
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

        internal static PhaseExecutionResult TryReadPhaseResult(string resultPath)
        {
            if (string.IsNullOrWhiteSpace(resultPath) || !File.Exists(resultPath))
            {
                return null;
            }

            try
            {
                return DeserializePhaseResult(resultPath);
            }
            catch
            {
                return null;
            }
        }

        internal static PhaseExecutionResult CreateFailedResult(string phaseKey, string fileStem, string outputDirectory, string error)
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
                Program.WorkerPhaseArgument,
                phase.Key,
                Program.PhaseResultArgument,
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
                WorkingDirectory = Program.ResolveExtractorRootDirectory()
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

        private static void PrintFinalSummary(
            IList<PhaseExecutionResult> phaseResults,
            string outputDirectory,
            BootstrapReport bootstrapReport,
            int totalPhaseCount)
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
            Console.WriteLine("Expected output files: " + (totalPhaseCount * 2));
            Console.WriteLine("Troubleshooting guide: README.md#7-troubleshooting");
            Console.WriteLine("========================================");
        }

        private static readonly DataContractJsonSerializer PhaseResultSerializer =
            new DataContractJsonSerializer(typeof(PhaseExecutionResult));

        private static void WritePhaseResult(string phaseResultPath, PhaseExecutionResult result)
        {
            string directory = Path.GetDirectoryName(phaseResultPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using (var stream = new FileStream(phaseResultPath, FileMode.Create, FileAccess.Write))
            {
                PhaseResultSerializer.WriteObject(stream, result);
            }
        }

        private static PhaseExecutionResult DeserializePhaseResult(string resultPath)
        {
            using (var stream = File.OpenRead(resultPath))
            {
                return (PhaseExecutionResult)PhaseResultSerializer.ReadObject(stream);
            }
        }
    }

    // === Bootstrap Runner ===

    internal static class BootstrapRunner
    {
        internal static BootstrapReport RunBootstrap(
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

        internal static List<string> BuildDependencyDiagnostics(Exception ex, string terrariaDirectory, string decompiledDirectory)
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

                    if (!CanResolveAssembly(reference.Name, terrariaDirectory, decompiledDirectory, terrariaExePath))
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

        private static bool CanResolveAssembly(string assemblySimpleName, string terrariaDirectory, string decompiledDirectory, string terrariaExePath)
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
                if (Directory.GetFiles(decompiledDirectory, pattern).Length > 0)
                {
                    return true;
                }
            }

            if (TerrariaDependencyResolver.HasEmbeddedDependency(terrariaExePath, assemblySimpleName))
            {
                return true;
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

            if (assemblySimpleName.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
                || assemblySimpleName.StartsWith("Microsoft.Xna.Framework", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
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
            Exception current = ex;

            while (current != null)
            {
                string fileName = null;
                if (current is FileNotFoundException fnf) fileName = fnf.FileName;
                else if (current is FileLoadException fle) fileName = fle.FileName;

                if (current is FileNotFoundException || current is FileLoadException || current is DllNotFoundException)
                {
                    AddIfValid(names, ExtractSimpleAssemblyName(fileName));
                    AddIfValid(names, ExtractSimpleAssemblyNameFromMessage(current.Message));
                }

                current = current.InnerException;
            }

            return names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static void AddIfValid(ISet<string> set, string value)
        {
            if (!string.IsNullOrWhiteSpace(value)) set.Add(value);
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

            var quoted = System.Text.RegularExpressions.Regex.Match(message, "'([^']+)'");
            if (quoted.Success)
            {
                return ExtractSimpleAssemblyName(quoted.Groups[1].Value);
            }

            var dllMatch = System.Text.RegularExpressions.Regex.Match(message, "([A-Za-z0-9_.-]+\\.dll)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (dllMatch.Success)
            {
                return ExtractSimpleAssemblyName(dllMatch.Groups[1].Value);
            }

            return null;
        }

        private static void LogBootstrap(string scope, BootstrapStageResult stage)
        {
            Console.WriteLine("[Bootstrap:" + scope + "] " + stage.Stage + " " + (stage.Succeeded ? "PASS" : "FAIL")
                + " - " + stage.Detail);
        }
    }

    // === Worker Runner ===

    internal static class WorkerRunner
    {
        private static readonly Dictionary<string, Func<ExtractionContext, JsonWriter, CsvWriter, string, string, PhaseExecutionResult>> PhaseRegistry =
            new Dictionary<string, Func<ExtractionContext, JsonWriter, CsvWriter, string, string, PhaseExecutionResult>>(StringComparer.Ordinal)
            {
                { "items", (ctx, jw, cw, fs, pk) => RunPhase(new ItemDataExtractor(), ctx, jw, cw, fs, pk) },
                { "shimmer", (ctx, jw, cw, fs, pk) => RunPhase(new ShimmerDataExtractor(), ctx, jw, cw, fs, pk) },
                { "recipes", (ctx, jw, cw, fs, pk) => RunPhase(new RecipeDataExtractor(), ctx, jw, cw, fs, pk) },
                { "npc_shops", (ctx, jw, cw, fs, pk) => RunPhase(new NpcShopDataExtractor(), ctx, jw, cw, fs, pk) },
                { "sprites", (ctx, jw, cw, fs, pk) => RunPhase(new SpriteExtractorPhase(), ctx, jw, cw, fs, pk) }
            };

        internal static int RunWorker(string[] normalizedArgs)
        {
            string phaseKey = Program.GetArgumentValue(normalizedArgs, Program.WorkerPhaseArgument);
            string phaseResultPath = Program.GetArgumentValue(normalizedArgs, Program.PhaseResultArgument);
            string outputDirectory = Program.ResolveOutputDirectory(normalizedArgs);

            Directory.CreateDirectory(outputDirectory);

            if (!Program.TryGetPhaseDefinition(phaseKey, out PhaseDefinition phase))
            {
                var unknownPhase = Orchestrator.CreateFailedResult("unknown-phase", "unknown", outputDirectory, "Unknown phase key: " + (phaseKey ?? "<null>"));
                Orchestrator.WritePhaseResultSafely(phaseResultPath, unknownPhase);
                return 1;
            }

            string terrariaExePath = TerrariaPathResolver.ResolveTerrariaExePath(normalizedArgs);
            string terrariaDirectory = Path.GetDirectoryName(terrariaExePath) ?? string.Empty;
            string decompiledDirectory = TerrariaPathResolver.FindRepositoryDecompiledDirectory();

            Console.WriteLine("[Worker] Phase " + phase.Key + " starting");

            BootstrapReport bootstrapReport = BootstrapRunner.RunBootstrap(
                "worker:" + phase.Key,
                outputDirectory,
                terrariaExePath,
                terrariaDirectory,
                decompiledDirectory);

            var jsonWriter = new JsonWriter();
            var csvWriter = new CsvWriter();

            Assembly terrariaAssembly = null;
            if (File.Exists(terrariaExePath))
            {
                try
                {
                    terrariaAssembly = TerrariaBootstrap.LoadTerrariaAssembly(terrariaExePath, decompiledDirectory);
                    TerrariaBootstrap.InitializeProgramState(terrariaAssembly);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[Worker] Failed to load Terraria assembly: " + ex.Message);
                }
            }

            var context = new ExtractionContext(outputDirectory, normalizedArgs, terrariaAssembly, terrariaDirectory);

            PhaseExecutionResult result;

            if (!File.Exists(terrariaExePath))
            {
                result = Orchestrator.CreateFailedResult(phase.Key, phase.FileStem, outputDirectory, "Terraria executable was not found: " + terrariaExePath);
                Orchestrator.EnsurePlaceholderOutputs(result.JsonPath, result.CsvPath);
            }
            else
            {
                try
                {
                    result = ExecuteConcretePhase(phase.Key, phase.FileStem, context, jsonWriter, csvWriter);
                }
                catch (Exception ex)
                {
                    result = Orchestrator.CreateFailedResult(phase.Key, phase.FileStem, outputDirectory, "worker crash: " + ex);
                    foreach (string diagnostic in BootstrapRunner.BuildDependencyDiagnostics(ex, terrariaDirectory, decompiledDirectory))
                    {
                        result.Errors.Add(diagnostic);
                    }

                    Orchestrator.EnsurePlaceholderOutputs(result.JsonPath, result.CsvPath);
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

            Orchestrator.EnsurePlaceholderOutputs(result.JsonPath, result.CsvPath);

            Orchestrator.WritePhaseResultSafely(phaseResultPath, result);

            Console.WriteLine("[Worker] Phase " + phase.Key + " " + (result.Succeeded ? "PASS" : "FAIL")
                + " (rows=" + result.RowCount + ")");

            return result.Succeeded ? 0 : 1;
        }

        private static PhaseExecutionResult ExecuteConcretePhase(
            string phaseKey,
            string fileStem,
            ExtractionContext context,
            JsonWriter jsonWriter,
            CsvWriter csvWriter)
        {
            if (PhaseRegistry.TryGetValue(phaseKey, out var factory))
            {
                return factory(context, jsonWriter, csvWriter, fileStem, phaseKey);
            }

            return Orchestrator.CreateFailedResult(phaseKey, fileStem, context.OutputDirectory, "Unsupported phase key: " + phaseKey);
        }

        private static PhaseExecutionResult RunPhase<T>(
            IExtractorPhase<T> extractor,
            ExtractionContext context,
            JsonWriter jsonWriter,
            CsvWriter csvWriter,
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

                string diagTerrariaDir = context.TerrariaDirectory;
                string diagDecompiledDir = TerrariaPathResolver.FindRepositoryDecompiledDirectory();

                foreach (string diagnostic in BootstrapRunner.BuildDependencyDiagnostics(ex, diagTerrariaDir, diagDecompiledDir))
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
    }
}
