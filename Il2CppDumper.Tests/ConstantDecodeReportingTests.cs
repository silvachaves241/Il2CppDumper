using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Il2CppDumper;
using Il2CppDumper.Core;
using Il2CppDumper.Tests.Synthetic;
using Mono.Cecil;
using Xunit;

namespace Il2CppDumper.Tests
{
    /// <summary>
    /// Integration coverage for constant decode failures reported in
    /// run-manifest.json (warnings.byCategory.constantDecode). C1 regression:
    /// the count and the aggregate warning must be captured AFTER the last
    /// consumer of TryGetDefaultValue, including the DummyDll-only path where
    /// DumpField/DumpMethod/GenerateStruct are disabled.
    /// </summary>
    public class ConstantDecodeReportingTests : IDisposable
    {
        private readonly string outputDir;

        public ConstantDecodeReportingTests()
        {
            outputDir = Path.Combine(Path.GetTempPath(), "il2cpp-constant-decode-" + Guid.NewGuid());
            Directory.CreateDirectory(outputDir);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true); } catch { /* best effort */ }
        }

        private const MetadataCorruptionType MalformedConstant = MetadataCorruptionType.ConstantBlobInvalidPrefix;

        private static (bool Success, List<string> Warnings, string DummyDllPath,
            RunManifestWarnings ManifestWarnings, string Diagnostics) Execute(bool dumpField, string dir)
        {
            var (metadata, il2Cpp, executor) = SyntheticEnvironment.Create(
                MetadataVersionFamily.V39, corruption: MalformedConstant);

            var config = new Config
            {
                DumpMethod = false,
                DumpField = dumpField,
                DumpProperty = false,
                DumpAttribute = false,
                DumpFieldOffset = false,
                DumpMethodOffset = false,
                DumpTypeDefIndex = false,
                GenerateDummyDll = true,
                GenerateStruct = false,
                GenerateJsonDump = false,
                GenerateRunManifest = true,
                GenerateProtectionReport = false,
                DummyDllAddToken = false,
                RequireAnyKey = false,
                StringDecryptor = "none"
            };

            var warnings = new List<string>();
            RunManifestWarnings manifestWarnings;
            bool success;
            string errorMessage;
            Directory.CreateDirectory(dir);
            var console = new StringWriter();
            using (CoreLogger.Scope(console, warnings))
            {
                success = Dumper.ExecuteDump(
                    metadata, il2Cpp, config, dir,
                    out errorMessage, out _, out manifestWarnings, out _);
            }

            var dummyDllPath = Path.Combine(dir, "DummyDll", "SyntheticImage.dll");
            return (success, warnings, dummyDllPath, manifestWarnings,
                errorMessage + Environment.NewLine + console.ToString());
        }

        private static FieldDefinition ReadDumpedField(string dllPath)
        {
            using var stream = File.OpenRead(dllPath);
            var asm = AssemblyDefinition.ReadAssembly(stream);
            var type = asm.MainModule.Types.Single(t => t.Name == "SyntheticEnum");
            return type.Fields.Single(f => f.Name == "OptionA");
        }

        [Fact]
        public void DummyDllOnlyConsumption_StillReportsConstantDecodeFallback()
        {
            // C1 reproduction: DumpField=false keeps the decompiler away from the field
            // default; the malformed constant is read exclusively by the DummyDll
            // generator. The warning and manifest category must still be captured.
            var dir = Path.Combine(outputDir, "dummy-only");
            var (success, warnings, dummyDllPath, manifestWarnings, diagnostics) = Execute(dumpField: false, dir);

            Assert.True(success, $"run must succeed with the fallback path; diagnostics: {diagnostics}");
            Assert.True(File.Exists(dummyDllPath), $"Expected DummyDll artifact: {dummyDllPath}");

            // Exactly one aggregate WARNING; constantDecode accounted.
            Assert.Equal(1, warnings.Count);
            Assert.Matches(@"WARNING: Constant default value decoding completed with 1 failure\(s\)", warnings[0]);
            Assert.Contains("dataIndex 0", warnings[0]);

            // Manifest reflects the failure that surfaced only via the DummyDll consumer.
            Assert.Equal(1, manifestWarnings.ByCategory.ConstantDecode);
            Assert.Equal(1, manifestWarnings.Total);

            // The consumed constant fell back to the metadata-offset attribute path.
            var field = ReadDumpedField(dummyDllPath);
            var offsetAttribute = field.CustomAttributes
                .SingleOrDefault(a => a.AttributeType.Name == "MetadataOffsetAttribute");
            Assert.NotNull(offsetAttribute);
            var offsetValue = Assert.Single(offsetAttribute.Fields, f => f.Name == "Offset");
            Assert.StartsWith("0x", (string)offsetValue.Argument.Value);
        }

        [Fact]
        public void MultipleConsumersReadingSameEntry_DeduplicateToOne()
        {
            // Same constant entry read by both the decompiler (DumpField=true) and the
            // DummyDll generator: identical (dataIndex) entries deduplicate, count stays 1.
            var dir = Path.Combine(outputDir, "multi-consumer");
            var (_, warnings, dummyDllPath, manifestWarnings, diagnostics) = Execute(dumpField: true, dir);

            Assert.True(File.Exists(dummyDllPath), $"Expected DummyDll artifact: {dummyDllPath}; diagnostics: {diagnostics}");
            Assert.Equal(1, warnings.Count);
            Assert.Matches(@"with 1 failure\(s\)", warnings[0]);
            Assert.Equal(1, manifestWarnings.ByCategory.ConstantDecode);
            Assert.Equal(1, manifestWarnings.Total);
        }
    }
}
