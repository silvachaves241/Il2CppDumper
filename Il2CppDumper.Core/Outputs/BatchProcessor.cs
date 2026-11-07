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
            log ??= Console.Out;
            errorLog ??= Console.Error;
            if (string.IsNullOrWhiteSpace(batchFilePath))
            {
                var msg = "ERROR: Missing batch file path.";
                errorLog.WriteLine(msg);
                return new BatchRunResult { ExitCode = 2, ErrorMessage = msg, OffendingPath = string.Empty };
            }

            if (!File.Exists(batchFilePath))
            {
                var msg = $"ERROR: Batch file not found: {batchFilePath}";
                errorLog.WriteLine(msg);
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
                    errorLog.WriteLine(msg);
                    return new BatchRunResult { ExitCode = 2, ErrorMessage = msg, OffendingPath = batchFilePath };
                }

                int elementIndex = 0;
                foreach (var elem in doc.RootElement.EnumerateArray())
                {
                    if (elem.ValueKind == JsonValueKind.Null || elem.ValueKind != JsonValueKind.Object)
                    {
                        var msg = $"ERROR: Malformed batch file '{batchFilePath}': Entry {elementIndex} (index {elementIndex}) is null or not a JSON object.";
                        errorLog.WriteLine(msg);
                        return new BatchRunResult { ExitCode = 2, ErrorMessage = msg, OffendingPath = batchFilePath };
                    }
                    elementIndex++;
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
            log ??= Console.Out;
            errorLog ??= Console.Error;
            var results = new List<BatchResultEntry>();

            if (entries == null)
            {
                var msg = $"ERROR: Malformed batch file '{batchFilePath}': Entries collection is null.";
                errorLog.WriteLine(msg);
                return new BatchRunResult { ExitCode = 2, ErrorMessage = msg, OffendingPath = batchFilePath };
            }

            // Up-front validation: check null entries, required fields, invalid config overrides, and overlapping output directories
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var normalizedDirs = new List<(string Normalized, int Index, string Original)>();
            var preparedConfigs = new Config[entries.Count];

            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (entry == null)
                {
                    var msg = $"ERROR: Malformed batch file '{batchFilePath}': Entry {i} is null.";
                    errorLog?.WriteLine(msg);
                    return new BatchRunResult { ExitCode = 2, ErrorMessage = msg, OffendingPath = batchFilePath };
                }
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

                // Check for overlapping output directories (exact equality or ancestor/descendant relationship)
                var norm = NormalizeDirectoryPath(entry.Output, batchDir);
                foreach (var prev in normalizedDirs)
                {
                    if (AreOverlappingDirectories(prev.Normalized, norm, comparison))
                    {
                        var kind = string.Equals(prev.Normalized, norm, comparison) ? "Duplicate" : "Overlapping";
                        var msg = $"ERROR: {kind} output directory detected between batch entry {prev.Index} ('{prev.Original}') and entry {i} ('{entry.Output}').";
                        errorLog.WriteLine(msg);
                        return new BatchRunResult { ExitCode = 2, ErrorMessage = msg, OffendingPath = entry.Output };
                    }
                }
                normalizedDirs.Add((norm, i, entry.Output));

                // Validate and prepare configuration overrides up-front
                if (entry.Config.HasValue && entry.Config.Value.ValueKind != JsonValueKind.Null)
                {
                    if (entry.Config.Value.ValueKind != JsonValueKind.Object)
                    {
                        var msg = $"ERROR: Malformed batch file '{batchFilePath}': Entry {i} 'config' must be a JSON object.";
                        errorLog.WriteLine(msg);
                        return new BatchRunResult { ExitCode = 2, ErrorMessage = msg, OffendingPath = batchFilePath };
                    }

                    try
                    {
                        preparedConfigs[i] = MergeConfigStrict(baseConfig, entry.Config.Value, i);
                    }
                    catch (Exception ex)
                    {
                        var msg = $"ERROR: Malformed batch file '{batchFilePath}': Entry {i} has invalid configuration override: {ex.Message}";
                        errorLog.WriteLine(msg);
                        return new BatchRunResult { ExitCode = 2, ErrorMessage = msg, OffendingPath = batchFilePath };
                    }
                }
                else
                {
                    preparedConfigs[i] = Dumper.CloneConfig(baseConfig);
                }
            }

            // Execute entries sequentially
            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                var effectiveConfig = preparedConfigs[i];

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
                CoreLogger.DetachWriter();
                if (dumpResult.Success)
                {
                    resultEntry.Success = true;
                    resultEntry.Error = null;
                    resultEntry.Outputs = FormatProducedOutputs(dumpResult.ProducedFiles, resolvedOutputDir, entry.Output);
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

        private static string NormalizeDirectoryPath(string path, string batchDir)
        {
            var resolved = ResolveOutputDir(path, batchDir);
            var full = Path.GetFullPath(resolved);
            if (!full.EndsWith(Path.DirectorySeparatorChar.ToString()) && !full.EndsWith(Path.AltDirectorySeparatorChar.ToString()))
            {
                full += Path.DirectorySeparatorChar;
            }
            return full;
        }

        private static bool AreOverlappingDirectories(string dirA, string dirB, StringComparison comparison)
        {
            // Both dirA and dirB end with DirectorySeparatorChar
            return string.Equals(dirA, dirB, comparison)
                || dirB.StartsWith(dirA, comparison)
                || dirA.StartsWith(dirB, comparison);
        }

        private static List<string> FormatProducedOutputs(
            IEnumerable<string>? producedFiles,
            string fullOutputDir,
            string originalEntryOutput)
        {
            var list = new List<string>();
            var normalizedEntry = originalEntryOutput.Replace('\\', '/').TrimEnd('/');

            if (producedFiles != null)
            {
                foreach (var file in producedFiles)
                {
                    if (!File.Exists(file))
                        continue;

                    var fullFile = Path.GetFullPath(file);
                    var rel = Path.GetRelativePath(fullOutputDir, fullFile).Replace('\\', '/');
                    var entryPath = string.IsNullOrEmpty(normalizedEntry)
                        ? rel
                        : $"{normalizedEntry}/{rel}";
                    list.Add(entryPath);
                }
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
            if (!overrides.HasValue || overrides.Value.ValueKind != JsonValueKind.Object)
            {
                return Dumper.CloneConfig(baseConfig);
            }
            return MergeConfigStrict(baseConfig, overrides.Value, -1);
        }

        public static Config MergeConfigStrict(Config baseConfig, JsonElement overrides, int entryIndex)
        {
            var clone = Dumper.CloneConfig(baseConfig);
            if (overrides.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException($"Entry {entryIndex} 'config' must be a JSON object.");
            }

            var baseNode = JsonSerializer.SerializeToNode(clone)!.AsObject();
            foreach (var prop in overrides.EnumerateObject())
            {
                var valNode = JsonNode.Parse(prop.Value.GetRawText());
                baseNode[prop.Name] = valNode;
            }

            var mergedJson = baseNode.ToJsonString();
            var merged = JsonSerializer.Deserialize<Config>(mergedJson, JsonOptions);
            if (merged == null)
            {
                throw new JsonException($"Entry {entryIndex} configuration deserialized to null.");
            }
            return merged;
        }
    }
}
