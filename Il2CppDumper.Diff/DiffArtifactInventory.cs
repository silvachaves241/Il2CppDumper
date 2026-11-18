#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace Il2CppDumper.Diff
{
    internal sealed class DiffArtifactInventory
    {
        internal const string ScriptFileName = "script.json";
        internal const string LiteralsFileName = "stringliteral-indexed.json";

        private readonly string directory;
        private readonly string label;
        private readonly string declarationName;
        private readonly List<RunManifestOutput>? outputs;
        private readonly RunManifestOutput? declaration;

        public DiffArtifactInventory(string directory, string label, string declarationName)
        {
            this.directory = directory;
            this.label = label;
            this.declarationName = declarationName;
            outputs = ReadInventory();
            declaration = FindOutput(declarationName);
        }

        public JsonDocument ReadDeclarations() => declaration == null
            ? ReadJsonFile(declarationName)
            : ReadVerifiedJson(declarationName, declaration);

        public bool HasUnlistedDeclarations => outputs != null && declaration == null;

        internal bool HasManifest => outputs != null;
        internal bool HasListedDeclarations => declaration != null;

        internal IEnumerable<string> OutputPaths => outputs?.Select(entry => entry?.Path).OfType<string>()
            ?? Enumerable.Empty<string>();

        internal byte[] ReadListedFile(string fileName)
        {
            var output = FindOutput(fileName)
                ?? throw new InvalidDataException($"{label} {fileName} is not listed in {RunManifestWriter.FileName}.");
            return ReadVerifiedBytes(fileName, output);
        }

        public JsonDocument? ReadOptional(string fileName, out string? omission)
        {
            omission = null;
            if (outputs == null)
            {
                return File.Exists(Path.Combine(directory, fileName)) ? ReadJsonFile(fileName) : null;
            }

            if (declaration == null)
            {
                omission = $"{fileName} skipped on {label} because {declarationName} is not listed in {RunManifestWriter.FileName}";
                return null;
            }

            var output = FindOutput(fileName);
            if (output == null)
            {
                omission = $"{fileName} not listed in {label} {RunManifestWriter.FileName}";
                return null;
            }

            return ReadVerifiedJson(fileName, output);
        }

        private List<RunManifestOutput>? ReadInventory()
        {
            var manifestPath = Path.Combine(directory, RunManifestWriter.FileName);
            try
            {
                using var stream = File.OpenRead(manifestPath);
                using var document = JsonDocument.Parse(stream);
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    !document.RootElement.TryGetProperty("outputs", out var entries) ||
                    entries.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidDataException($"Invalid {label} {RunManifestWriter.FileName}: expected an 'outputs' array.");
                }

                return entries.Deserialize<List<RunManifestOutput>>()!;
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                throw new InvalidDataException($"Failed to read {label} {RunManifestWriter.FileName}: {ex.Message}", ex);
            }
        }

        private RunManifestOutput? FindOutput(string fileName)
        {
            if (outputs == null)
                return null;

            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            RunManifestOutput? match = null;
            foreach (var entry in outputs)
            {
                if (!string.Equals(entry?.Path, fileName, comparison))
                    continue;

                if (match != null)
                    throw new InvalidDataException($"Invalid {label} {RunManifestWriter.FileName}: duplicate entry for {fileName}.");

                match = entry;
            }
            return match;
        }

        private JsonDocument ReadJsonFile(string fileName)
        {
            try
            {
                using var stream = File.OpenRead(Path.Combine(directory, fileName));
                return JsonDocument.Parse(stream);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"Failed to parse {label} {fileName}: {ex.Message}", ex);
            }
        }

        private byte[] ReadVerifiedBytes(string fileName, RunManifestOutput output)
        {
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(Path.Combine(directory, fileName));
            }
            catch (FileNotFoundException ex)
            {
                throw new InvalidDataException($"{label} {fileName} is missing but listed in {RunManifestWriter.FileName}.", ex);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidDataException($"Failed to read {label} {fileName} listed in {RunManifestWriter.FileName}: {ex.Message}", ex);
            }

            if (bytes.LongLength != output.Bytes)
                throw new InvalidDataException($"{label} {fileName} size mismatch against {RunManifestWriter.FileName}: expected {output.Bytes} bytes, found {bytes.LongLength}.");

            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            if (!string.Equals(hash, output.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"{label} {fileName} SHA-256 mismatch against {RunManifestWriter.FileName}.");

            return bytes;
        }

        private JsonDocument ReadVerifiedJson(string fileName, RunManifestOutput output)
        {
            var bytes = ReadVerifiedBytes(fileName, output);
            // Parse the verified snapshot, including legacy files that use a UTF-8 BOM.
            ReadOnlyMemory<byte> json = bytes;
            if (bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf)
                json = json.Slice(3);

            try
            {
                return JsonDocument.Parse(json);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"Failed to parse {label} {fileName}: {ex.Message}", ex);
            }
        }
    }
}
