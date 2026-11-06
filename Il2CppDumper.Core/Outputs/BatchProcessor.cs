#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Il2CppDumper.Core
{
    public sealed class BatchEntry
    {
        [JsonPropertyName("executable")]
        public string Executable { get; set; } = string.Empty;

        [JsonPropertyName("metadata")]
        public string Metadata { get; set; } = string.Empty;

        [JsonPropertyName("output")]
        public string Output { get; set; } = string.Empty;

        [JsonPropertyName("config")]
        public JsonElement? Config { get; set; }
    }

    public sealed class BatchResultEntry
    {
        [JsonPropertyName("index")]
        public int Index { get; set; }

        [JsonPropertyName("executable")]
        public string Executable { get; set; } = string.Empty;

        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }

        [JsonPropertyName("outputs")]
        public List<string> Outputs { get; set; } = new();
    }

    public sealed class BatchRunResult
    {
        public int ExitCode { get; set; }
        public List<BatchResultEntry> Results { get; set; } = new();
        public string? ErrorMessage { get; set; }
        public string? OffendingPath { get; set; }
    }

    public static class BatchProcessor
    {
        public const string ResultFileName = "batch-result.json";

        public static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        public static BatchRunResult RunBatch(
            string batchFilePath,
            Config baseConfig,
            TextWriter? log = null,
            TextWriter? errorLog = null,
            string? resultOutputPath = null)
        {
            if (string.IsNullOrWhiteSpace(batchFilePath))
            {
                var msg = "ERROR: Missing batch file path.";
                errorLog?.WriteLine(msg);
                return new BatchRunResult { ExitCode = 2, ErrorMessage = msg, OffendingPath = string.Empty };
            }

            if (!File.Exists(batchFilePath))
            {
                var msg = $"ERROR: Batch file not found: {batchFilePath}";
                errorLog?.WriteLine(msg);
                return new BatchRunResult { ExitCode = 2, ErrorMessage = msg, OffendingPath = batchFilePath };
            }

            List<BatchEntry>? entries;
            try
            {
                var jsonBytes = File.ReadAllBytes(batchFilePath);
                using var doc = JsonDocument.Parse(jsonBytes);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    var msg = $"ERROR: Malformed batch file '{batchFilePath}': Root must be a JSON array.";
                    errorLog?.WriteLine(msg);
                    return new BatchRunResult { ExitCode = 2, ErrorMessage = msg, OffendingPath = batchFilePath };
                }

                entries = JsonSerializer.Deserialize<List<BatchEntry>>(jsonBytes, JsonOptions);
                if (entries == null)
                {
                    var msg = $"ERROR: Malformed batch file '{batchFilePath}': Failed to deserialize batch entries.";
                    errorLog?.WriteLine(msg);
                    return new BatchRunResult { ExitCode = 2, ErrorMessage = msg, OffendingPath = batchFilePath };
                }
            }
            catch (Exception ex)
            {
                var msg = $"ERROR: Malformed batch file '{batchFilePath}': {ex.Message}";
                errorLog?.WriteLine(msg);
                return new BatchRunResult { ExitCode = 2, ErrorMessage = msg, OffendingPath = batchFilePath };
            }

            var batchDir = Path.GetDirectoryName(Path.GetFullPath(batchFilePath)) ?? Directory.GetCurrentDirectory();

            return ExecuteEntries(entries, baseConfig, batchDir, batchFilePath, log, errorLog, resultOutputPath);
        }

        public static BatchRunResult ExecuteEntries(
            IReadOnlyList<BatchEntry> entries,
            Config baseConfig,
            string batchDir,
            string batchFilePath,
            TextWriter? log = null,
            TextWriter? errorLog = null,
            string? resultOutputPath = null)
        {
            var results = new List<BatchResultEntry>();

            // Up-front validation: check required fields and duplicate output directories
            var comparison = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var seenOutputs = new Dictionary<string, int>(comparison);

            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (string.IsNullOrWhiteSpace(entry.Executable))
                {
                    var msg = $"ERROR: Malformed batch file '{batchFilePath}': Entry {i} is missing 'executable'.";
                    errorLog?.WriteLine(msg);
                    return new BatchRunResult { ExitCode = 2, ErrorMessage = msg, OffendingPath = batchFilePath };
                }
                if (string.IsNullOrWhiteSpace(entry.Metadata))
                {
                    var msg = $"ERROR: Malformed batch file '{batchFilePath}': Entry {i} is missing 'metadata'.";
                    errorLog?.WriteLine(msg);
                    return new BatchRunResult { ExitCode = 2, ErrorMessage = msg, OffendingPath = batchFilePath };
                }
                if (string.IsNullOrWhiteSpace(entry.Output))
                {
                    var msg = $"ERROR: Malformed batch file '{batchFilePath}': Entry {i} is missing 'output'.";
                    errorLog?.WriteLine(msg);
                    return new BatchRunResult { ExitCode = 2, ErrorMessage = msg, OffendingPath = batchFilePath };
                }

                var resolvedOut = ResolveOutputDir(entry.Output, batchDir);
                var normalizedOut = Path.GetFullPath(resolvedOut).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (seenOutputs.TryGetValue(normalizedOut, out var previousIndex))
                {
                    var msg = $"ERROR: Duplicate output directory '{entry.Output}' detected across batch entries {previousIndex} and {i}.";
                    errorLog?.WriteLine(msg);
                    return new BatchRunResult { ExitCode = 2, ErrorMessage = msg, OffendingPath = entry.Output };
                }
                seenOutputs.Add(normalizedOut, i);
            }

            // Execute entries sequentially
            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                var effectiveConfig = MergeConfig(baseConfig, entry.Config);

                var resolvedExecutable = ResolveInputPath(entry.Executable, batchDir);
                var resolvedMetadata = ResolveInputPath(entry.Metadata, batchDir);
                var resolvedOutputDir = ResolveOutputDir(entry.Output, batchDir);

                var resultEntry = new BatchResultEntry
                {
                    Index = i,
                    Executable = entry.Executable
                };

                var request = new DumpRequest
                {
                    ExecutablePath = resolvedExecutable,
                    MetadataPath = resolvedMetadata,
                    OutputDirectory = resolvedOutputDir,
                    Config = effectiveConfig,
                    Log = log
                };

                var dumpResult = Dumper.Dump(request);
                if (dumpResult.Success)
                {
                    resultEntry.Success = true;
                    resultEntry.Error = null;
                    resultEntry.Outputs = CollectOutputs(resolvedOutputDir, entry.Output);
                }
                else
                {
                    resultEntry.Success = false;
                    resultEntry.Error = dumpResult.Error ?? "ERROR: An error occurred while processing.";
                    resultEntry.Outputs = new List<string>();
                }

                results.Add(resultEntry);

                // StopOnFirstError switch
                if (!resultEntry.Success && (effectiveConfig.StopOnFirstError || baseConfig.StopOnFirstError))
                {
                    break;
                }
            }

            // Write aggregate batch-result.json
            WriteBatchResult(results, batchDir, resultOutputPath);

            var exitCode = results.All(r => r.Success) ? 0 : 1;
            return new BatchRunResult
            {
                ExitCode = exitCode,
                Results = results
            };
        }

        public static string ResolveInputPath(string path, string batchDir)
        {
            if (Path.IsPathRooted(path))
            {
                return Path.GetFullPath(path);
            }

            if (!string.IsNullOrEmpty(batchDir))
            {
                var fromBatch = Path.Combine(batchDir, path);
                if (File.Exists(fromBatch) || Directory.Exists(fromBatch))
                {
                    return Path.GetFullPath(fromBatch);
                }
            }

            if (File.Exists(path) || Directory.Exists(path))
            {
                return Path.GetFullPath(path);
            }

            if (!string.IsNullOrEmpty(batchDir))
            {
                return Path.GetFullPath(Path.Combine(batchDir, path));
            }

            return Path.GetFullPath(path);
        }

        public static string ResolveOutputDir(string path, string batchDir)
        {
            if (Path.IsPathRooted(path))
            {
                return Path.GetFullPath(path);
            }

            if (!string.IsNullOrEmpty(batchDir))
            {
                return Path.GetFullPath(Path.Combine(batchDir, path));
            }

            return Path.GetFullPath(path);
        }

        private static List<string> CollectOutputs(string fullOutputDir, string originalEntryOutput)
        {
            var list = new List<string>();
            if (!Directory.Exists(fullOutputDir))
            {
                return list;
            }

            var normalizedEntry = originalEntryOutput.Replace('\\', '/').TrimEnd('/');

            var files = Directory.EnumerateFiles(fullOutputDir, "*", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                var rel = Path.GetRelativePath(fullOutputDir, file).Replace('\\', '/');
                var entryPath = string.IsNullOrEmpty(normalizedEntry)
                    ? rel
                    : $"{normalizedEntry}/{rel}";
                list.Add(entryPath);
            }

            list.Sort(StringComparer.Ordinal);
            return list;
        }

        private static void WriteBatchResult(List<BatchResultEntry> results, string batchDir, string? resultOutputPath)
        {
            var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(results, JsonOptions);

            if (!string.IsNullOrWhiteSpace(resultOutputPath))
            {
                var fullDest = Path.GetFullPath(resultOutputPath);
                var parent = Path.GetDirectoryName(fullDest);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }
                File.WriteAllBytes(fullDest, jsonBytes);
                return;
            }

            var cwdDest = Path.Combine(Directory.GetCurrentDirectory(), ResultFileName);
            File.WriteAllBytes(cwdDest, jsonBytes);

            if (!string.IsNullOrEmpty(batchDir))
            {
                var batchDest = Path.Combine(batchDir, ResultFileName);
                if (!string.Equals(Path.GetFullPath(batchDest), Path.GetFullPath(cwdDest), StringComparison.OrdinalIgnoreCase))
                {
                    File.WriteAllBytes(batchDest, jsonBytes);
                }
            }
        }

        public static Config MergeConfig(Config baseConfig, JsonElement? overrides)
        {
            var clone = Dumper.CloneConfig(baseConfig);
            if (!overrides.HasValue || overrides.Value.ValueKind != JsonValueKind.Object)
            {
                return clone;
            }

            try
            {
                var baseNode = JsonSerializer.SerializeToNode(clone)!.AsObject();
                foreach (var prop in overrides.Value.EnumerateObject())
                {
                    var valNode = JsonNode.Parse(prop.Value.GetRawText());
                    baseNode[prop.Name] = valNode;
                }

                return JsonSerializer.Deserialize<Config>(baseNode.ToJsonString(), JsonOptions) ?? clone;
            }
            catch
            {
                return clone;
            }
        }
    }
}
