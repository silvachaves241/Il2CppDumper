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

            // Structured per-item triage context (#65)
            Assert.NotNull(manifestWarnings.Items);
            var item = Assert.Single(manifestWarnings.Items);
            Assert.Equal("constantDecode", item.Category);
            Assert.Equal("SyntheticEnum", item.TypeName);
            Assert.Equal("OptionA", item.Member);
            Assert.NotNull(item.TypeDefIndex);
            Assert.NotNull(item.RawBytes);
            Assert.StartsWith("E1", item.RawBytes);
            Assert.NotNull(item.Detail);
            Assert.Contains("0xE1", item.Detail);

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

            // Deduplication preserves exactly one structured item (#65)
            Assert.NotNull(manifestWarnings.Items);
            var item = Assert.Single(manifestWarnings.Items);
            Assert.Equal("constantDecode", item.Category);
            Assert.Equal("SyntheticEnum", item.TypeName);
            Assert.Equal("OptionA", item.Member);
            Assert.NotNull(item.RawBytes);
            Assert.StartsWith("E1", item.RawBytes);
        }
        [Fact]
        public void ConstantDecode_ExactDataIndexLookup_DoesNotCrossContaminateMatchingPrefixes()
        {
            // T1 regression: dataIndex 1 and 10 share string prefixes. Enriching dataIndex 1
            // must locate the exact entry by key and not cross-contaminate entry 10.
            var (metadata, il2Cpp, executor) = SyntheticEnvironment.Create(MetadataVersionFamily.V39);
            var offset = metadata.GetDefaultValueFromIndex(0);

            // Populate dataIndex 1, 10, and 100 with invalid compressed integer prefix (0xE1)
            metadata.Position = (ulong)(offset + 1);
            metadata.Write((byte)0xE1);
            metadata.Position = (ulong)(offset + 10);
            metadata.Write((byte)0xE1);
            metadata.Position = (ulong)(offset + 100);
            metadata.Write((byte)0xE1);

            var typeIndex = Array.FindIndex(il2Cpp.types, t => t.type == Il2CppTypeEnum.IL2CPP_TYPE_I4);
            if (typeIndex < 0) typeIndex = 0;

            // 1. Register 10 and 1 and 100 without context
            Assert.False(executor.TryGetDefaultValue(typeIndex, 10, out _));
            Assert.False(executor.TryGetDefaultValue(typeIndex, 1, out _));
            Assert.False(executor.TryGetDefaultValue(typeIndex, 100, out _));

            Assert.Equal(3, executor.ConstantDecodeFailedCount);

            // 2. Enrich dataIndex 1 with specific owner context
            Assert.False(executor.TryGetDefaultValue(typeIndex, 1, out _, typeDefIndex: 123, typeName: "OwnerOfOne", member: "FieldOne"));

            // Count remains 3
            Assert.Equal(3, executor.ConstantDecodeFailedCount);

            var item1 = executor.ConstantDecodeFailures.Single(i => i.Detail!.Contains("dataIndex 1):"));
            var item10 = executor.ConstantDecodeFailures.Single(i => i.Detail!.Contains("dataIndex 10):"));
            var item100 = executor.ConstantDecodeFailures.Single(i => i.Detail!.Contains("dataIndex 100):"));

            // Exact match: only dataIndex 1 is enriched
            Assert.Equal(123, item1.TypeDefIndex);
            Assert.Equal("OwnerOfOne", item1.TypeName);
            Assert.Equal("FieldOne", item1.Member);

            // 10 and 100 remain without context (not contaminated)
            Assert.Null(item10.TypeDefIndex);
            Assert.Null(item10.TypeName);
            Assert.Null(item10.Member);

            Assert.Null(item100.TypeDefIndex);
            Assert.Null(item100.TypeName);
            Assert.Null(item100.Member);
        }
    }
}

