using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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

        public static List<RunManifestOutput> CollectOutputs(string outputDir)
        {
            var list = new List<RunManifestOutput>();
            if (string.IsNullOrEmpty(outputDir) || !Directory.Exists(outputDir))
            {
                return list;
            }

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
                var filePath = Path.Combine(outputDir, name);
                if (File.Exists(filePath))
                {
                    var info = new FileInfo(filePath);
                    using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: false);
                    var sha256 = DumpMetadataWriter.ComputeStreamSha256(stream);
                    list.Add(new RunManifestOutput
                    {
                        Path = name,
                        Sha256 = sha256,
                        Bytes = info.Length
                    });
                }
            }

            var dummyDllDir = Path.Combine(outputDir, "DummyDll");
            if (Directory.Exists(dummyDllDir))
            {
                foreach (var file in Directory.EnumerateFiles(dummyDllDir, "*", SearchOption.AllDirectories))
                {
                    var relPath = Path.GetRelativePath(outputDir, file).Replace('\\', '/');
                    var info = new FileInfo(file);
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: false);
                    var sha256 = DumpMetadataWriter.ComputeStreamSha256(stream);
                    list.Add(new RunManifestOutput
                    {
                        Path = relPath,
                        Sha256 = sha256,
                        Bytes = info.Length
                    });
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
            string outputDir)
        {
            var outputs = CollectOutputs(outputDir);
            return new RunManifest
            {
                Tool = GetToolInfo(),
                StartedAt = startedAtUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                DurationMs = Math.Max(0, durationMs),
                Success = success,
                EffectiveConfig = config ?? new Config(),
                Counts = counts ?? new RunManifestCounts(),
                Warnings = warnings ?? new RunManifestWarnings(),
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
            RunManifestWarnings warnings)
        {
            if (config != null && !config.GenerateRunManifest)
            {
                return null!;
            }

            Directory.CreateDirectory(outputDir);
            var manifest = Create(config ?? new Config(), startedAtUtc, durationMs, success, counts, warnings, outputDir);
            var manifestPath = Path.Combine(outputDir, FileName);
            var json = JsonSerializer.Serialize(manifest, JsonOptions);
            File.WriteAllText(manifestPath, json, new UTF8Encoding(false));
            return manifest;
        }
    }
}
