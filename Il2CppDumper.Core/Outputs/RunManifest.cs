#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Il2CppDumper
{
    public class RunManifest
    {
        [JsonPropertyName("tool")]
        public RunManifestTool Tool { get; set; } = new();

        [JsonPropertyName("startedAt")]
        public string StartedAt { get; set; } = string.Empty;

        [JsonPropertyName("durationMs")]
        public long DurationMs { get; set; }

        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("effectiveConfig")]
        public Config EffectiveConfig { get; set; } = new();

        [JsonPropertyName("counts")]
        public RunManifestCounts Counts { get; set; } = new();

        [JsonPropertyName("warnings")]
        public RunManifestWarnings Warnings { get; set; } = new();

        [JsonPropertyName("outputs")]
        public List<RunManifestOutput> Outputs { get; set; } = new();
    }

    public class RunManifestTool
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "Il2CppDumper";

        [JsonPropertyName("version")]
        public string Version { get; set; } = "1.0.0.0";
    }

    public class RunManifestCounts
    {
        [JsonPropertyName("types")]
        public int Types { get; set; }

        [JsonPropertyName("methods")]
        public int Methods { get; set; }

        [JsonPropertyName("fields")]
        public int Fields { get; set; }

        [JsonPropertyName("properties")]
        public int Properties { get; set; }

        [JsonPropertyName("attributes")]
        public long Attributes { get; set; }

        [JsonPropertyName("attributesFailed")]
        public long AttributesFailed { get; set; }

        [JsonPropertyName("stringLiterals")]
        public int StringLiterals { get; set; }
    }

    public class RunManifestWarnings
    {
        [JsonPropertyName("total")]
        public int Total { get; set; }

        [JsonPropertyName("byCategory")]
        public RunManifestWarningCategories ByCategory { get; set; } = new();
    }

    public class RunManifestWarningCategories
    {
        [JsonPropertyName("attributeDecode")]
        public int AttributeDecode { get; set; }

        [JsonPropertyName("stringLiteral")]
        public int StringLiteral { get; set; }

        [JsonPropertyName("stringDecryptor")]
        public int StringDecryptor { get; set; }

        [JsonPropertyName("outputHash")]
        public int OutputHash { get; set; }
    }

    public class RunManifestOutput
    {
        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;

        [JsonPropertyName("sha256")]
        public string Sha256 { get; set; } = string.Empty;

        [JsonPropertyName("bytes")]
        public long Bytes { get; set; }
    }

    public static class RunManifestWriter
    {
        public const string FileName = "run-manifest.json";

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true
        };

        public static RunManifestTool GetToolInfo()
        {
            var assembly = typeof(RunManifest).Assembly;
            var version = assembly.GetName().Version?.ToString() ?? "1.0.0.0";
            return new RunManifestTool
            {
                Name = "Il2CppDumper",
                Version = version
            };
        }

        public static List<RunManifestOutput> CollectOutputs(string outputDir, IEnumerable<string>? producedFiles = null, RunManifestWarnings? warnings = null)
        {
            var list = new List<RunManifestOutput>();
            if (string.IsNullOrEmpty(outputDir) || !Directory.Exists(outputDir))
            {
                return list;
            }

            var fullOutputDir = Path.GetFullPath(outputDir);
            if (!fullOutputDir.EndsWith(Path.DirectorySeparatorChar.ToString()) && !fullOutputDir.EndsWith(Path.AltDirectorySeparatorChar.ToString()))
            {
                fullOutputDir += Path.DirectorySeparatorChar;
            }

            var filesToProcess = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (producedFiles != null)
            {
                foreach (var file in producedFiles)
                {
                    if (string.IsNullOrWhiteSpace(file))
                        continue;

                    var fullPath = Path.IsPathRooted(file)
                        ? Path.GetFullPath(file)
                        : Path.GetFullPath(Path.Combine(fullOutputDir, file));

                    if (!File.Exists(fullPath))
                        continue;

                    if (string.Equals(Path.GetFileName(fullPath), FileName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    filesToProcess.Add(fullPath);
                }
            }
            else
            {
                var knownNames = new[]
                {
                    DumpMetadataWriter.FileName,
                    "dump.cs",
                    "dump.json",
                    "script.json",
                    "stringliteral.json",
                    "stringliteral-indexed.json",
                    "il2cpp.h"
                };

                foreach (var name in knownNames)
                {
                    var filePath = Path.Combine(fullOutputDir, name);
                    if (File.Exists(filePath))
                        filesToProcess.Add(filePath);
                }

                var dummyDllDir = Path.Combine(fullOutputDir, "DummyDll");
                if (Directory.Exists(dummyDllDir))
                {
                    foreach (var file in Directory.EnumerateFiles(dummyDllDir, "*", SearchOption.AllDirectories))
                    {
                        filesToProcess.Add(file);
                    }
                }
            }

            foreach (var fullPath in filesToProcess.OrderBy(p => p, StringComparer.Ordinal))
            {
                var relPath = Path.GetRelativePath(fullOutputDir, fullPath).Replace('\\', '/');
                try
                {
                    var info = new FileInfo(fullPath);
                    using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: false);
                    var sha256 = DumpMetadataWriter.ComputeStreamSha256(stream);
                    list.Add(new RunManifestOutput
                    {
                        Path = relPath,
                        Sha256 = sha256,
                        Bytes = info.Length
                    });
                }
                catch (Exception ex)
                {
                    CoreLogger.WriteLine($"WARNING: Could not hash output file '{relPath}': {ex.Message}");
                    if (warnings != null)
                    {
                        warnings.ByCategory.OutputHash++;
                        warnings.Total++;
                    }
                }
            }

            list.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.Ordinal));
            return list;
        }

        public static RunManifest Create(
            Config config,
            DateTime startedAtUtc,
            long durationMs,
            bool success,
            RunManifestCounts counts,
            RunManifestWarnings warnings,
            string outputDir,
            IEnumerable<string>? producedFiles = null)
        {
            var manifestWarnings = warnings ?? new RunManifestWarnings();
            var outputs = CollectOutputs(outputDir, producedFiles, manifestWarnings);
            return new RunManifest
            {
                Tool = GetToolInfo(),
                StartedAt = startedAtUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                DurationMs = Math.Max(0, durationMs),
                Success = success,
                EffectiveConfig = config ?? new Config(),
                Counts = counts ?? new RunManifestCounts(),
                Warnings = manifestWarnings,
                Outputs = outputs
            };
        }

        public static RunManifest Write(
            string outputDir,
            Config config,
            DateTime startedAtUtc,
            long durationMs,
            bool success,
            RunManifestCounts counts,
            RunManifestWarnings warnings,
            IEnumerable<string>? producedFiles = null)
        {
            if (config != null && !config.GenerateRunManifest)
            {
                return null!;
            }

            Directory.CreateDirectory(outputDir);
            var manifest = Create(config ?? new Config(), startedAtUtc, durationMs, success, counts, warnings, outputDir, producedFiles);
            var manifestPath = Path.Combine(outputDir, FileName);
            var json = JsonSerializer.Serialize(manifest, JsonOptions);
            File.WriteAllText(manifestPath, json, new UTF8Encoding(false));
            return manifest;
        }
    }
}
