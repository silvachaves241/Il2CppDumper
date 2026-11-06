using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Il2CppDumper.Tests.Synthetic;
using Xunit;

namespace Il2CppDumper.Tests
{
    public class SyntheticMetadataGeneratorTests
    {
        private static string FindRepoRoot()
        {
            var current = AppContext.BaseDirectory;
            while (!string.IsNullOrEmpty(current))
            {
                if (File.Exists(Path.Combine(current, "Il2CppDumper.sln")) ||
                    (File.Exists(Path.Combine(current, "README.md")) && File.Exists(Path.Combine(current, "README.zh-CN.md"))))
                {
                    return Path.GetFullPath(current);
                }

                var parent = Directory.GetParent(current);
                if (parent == null)
                    break;

                current = parent.FullName;
            }

            throw new FileNotFoundException($"Could not locate repository root from {AppContext.BaseDirectory}");
        }

        [Theory]
        [InlineData(MetadataVersionFamily.V16)]
        [InlineData(MetadataVersionFamily.V21)]
        [InlineData(MetadataVersionFamily.V24)]
        [InlineData(MetadataVersionFamily.V27)]
        [InlineData(MetadataVersionFamily.V29)]
        [InlineData(MetadataVersionFamily.V31)]
        [InlineData(MetadataVersionFamily.V35)]
        [InlineData(MetadataVersionFamily.V38)]
        [InlineData(MetadataVersionFamily.V39)]
        public void PositiveFixture_ParsesCleanly_AndContainsExpectedEntities(MetadataVersionFamily family)
        {
            var bytes = SyntheticMetadataGenerator.Generate(family, seed: 42);
            Assert.NotNull(bytes);
            Assert.True(bytes.Length > 0);

            using var metadata = new Metadata(new MemoryStream(bytes));

            // Verify version
            Assert.Equal((int)family, (int)metadata.Version);

            // Verify images and assemblies
            Assert.NotNull(metadata.imageDefs);
            Assert.Single(metadata.imageDefs);
            Assert.Equal("SyntheticImage.dll", metadata.GetStringFromIndex(metadata.imageDefs[0].nameIndex));

            Assert.NotNull(metadata.assemblyDefs);
            Assert.Single(metadata.assemblyDefs);
            Assert.Equal("SyntheticAssembly", metadata.GetStringFromIndex(metadata.assemblyDefs[0].aname.nameIndex));

            // Verify type definitions (1 class + 1 enum)
            Assert.NotNull(metadata.typeDefs);
            Assert.Equal(2, metadata.typeDefs.Length);

            var classDef = metadata.typeDefs[0];
            Assert.Equal("SyntheticClass", metadata.GetStringFromIndex(classDef.nameIndex));
            Assert.Equal("SyntheticNamespace", metadata.GetStringFromIndex(classDef.namespaceIndex));
            Assert.False(classDef.IsEnum);
            Assert.Equal(1, classDef.field_count);
            Assert.Equal(1, classDef.method_count);

            var enumDef = metadata.typeDefs[1];
            Assert.Equal("SyntheticEnum", metadata.GetStringFromIndex(enumDef.nameIndex));
            Assert.Equal("SyntheticNamespace", metadata.GetStringFromIndex(enumDef.namespaceIndex));
            Assert.True(enumDef.IsEnum);
            Assert.Equal(2, enumDef.field_count);
            Assert.Equal(0, enumDef.method_count);

            // Verify method definition (1 method)
            Assert.NotNull(metadata.methodDefs);
            Assert.Single(metadata.methodDefs);
            var methodDef = metadata.methodDefs[0];
            Assert.Equal("SyntheticMethod", metadata.GetStringFromIndex(methodDef.nameIndex));
            Assert.Equal(1, methodDef.parameterCount);

            // Verify field definitions (3 fields: syntheticField, value__, OptionA)
            Assert.NotNull(metadata.fieldDefs);
            Assert.Equal(3, metadata.fieldDefs.Length);
            Assert.Equal("syntheticField", metadata.GetStringFromIndex(metadata.fieldDefs[0].nameIndex));
            Assert.Equal("value__", metadata.GetStringFromIndex(metadata.fieldDefs[1].nameIndex));
            Assert.Equal("OptionA", metadata.GetStringFromIndex(metadata.fieldDefs[2].nameIndex));

            // Verify enum default value
            Assert.True(metadata.GetFieldDefaultValueFromIndex(2, out var defaultValue));
            Assert.Equal(2, defaultValue.fieldIndex);
            var valueOffset = metadata.GetDefaultValueFromIndex(defaultValue.dataIndex);
            Assert.True(valueOffset < (uint)bytes.Length);
            var value = BitConverter.ToInt32(bytes, (int)valueOffset);
            Assert.Equal(1, value);

            // Verify string literal
            Assert.NotNull(metadata.stringLiterals);
            Assert.Single(metadata.stringLiterals);
            Assert.True(metadata.TryGetStringLiteral(0, out var literalValue, out _, out _));
            Assert.Equal("HelloSynthetic", literalValue);
        }

        [Theory]
        [InlineData(MetadataVersionFamily.V16)]
        [InlineData(MetadataVersionFamily.V21)]
        [InlineData(MetadataVersionFamily.V24)]
        [InlineData(MetadataVersionFamily.V27)]
        [InlineData(MetadataVersionFamily.V29)]
        [InlineData(MetadataVersionFamily.V31)]
        [InlineData(MetadataVersionFamily.V35)]
        [InlineData(MetadataVersionFamily.V38)]
        [InlineData(MetadataVersionFamily.V39)]
        public void PositiveFixture_Decompilation_MatchesGoldenOutputs(MetadataVersionFamily family)
        {
            var repoRoot = FindRepoRoot();
            var goldensDir = Path.Combine(repoRoot, "Il2CppDumper.Tests", "Goldens", family.ToString());
            Directory.CreateDirectory(goldensDir);

            var tempDir = Path.Combine(Path.GetTempPath(), "synthetic_golden_" + family + "_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                var (metadata, il2Cpp, executor) = SyntheticEnvironment.Create(family, seed: 42);
                var config = new Config
                {
                    DumpAttribute = false,
                    DumpField = true,
                    DumpMethod = true,
                    DumpProperty = true,
                    DumpTypeDefIndex = false
                };

                SyntheticEnvironment.DecompileToDirectory(executor, config, tempDir);

                var actualDumpCsPath = Path.Combine(tempDir, "dump.cs");
                var actualDumpJsonPath = Path.Combine(tempDir, "dump.json");
                var goldenDumpCsPath = Path.Combine(goldensDir, "dump.cs");
                var goldenDumpJsonPath = Path.Combine(goldensDir, "dump.json");

                var updateGoldens = Environment.GetEnvironmentVariable("UPDATE_GOLDENS") == "1" ||
                                    Environment.GetEnvironmentVariable("REGENERATE_GOLDEN_FILES") == "1";

                AssertOrUpdateGoldenFiles(goldensDir, actualDumpCsPath, actualDumpJsonPath, updateGoldens);

                var expectedCs = File.ReadAllText(goldenDumpCsPath).Replace("\r\n", "\n");
                var actualCs = File.ReadAllText(actualDumpCsPath).Replace("\r\n", "\n");
                Assert.Equal(expectedCs, actualCs);

                var expectedJson = File.ReadAllText(goldenDumpJsonPath).Replace("\r\n", "\n");
                var actualJson = File.ReadAllText(actualDumpJsonPath).Replace("\r\n", "\n");
                Assert.Equal(expectedJson, actualJson);
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        [Fact]
        public void MissingGolden_WithoutUpdateOptIn_FailsAndDoesNotRecreateFile()
        {
            var tempBase = Path.Combine(Path.GetTempPath(), "missing_golden_test_" + Guid.NewGuid().ToString("N"));
            var mockGoldensDir = Path.Combine(tempBase, "MockGoldens");
            var mockTempDir = Path.Combine(tempBase, "MockTemp");
            Directory.CreateDirectory(mockGoldensDir);
            Directory.CreateDirectory(mockTempDir);

            try
            {
                var existingJsonContent = "{\n  \"baseline\": true\n}";
                var mockGoldenJsonPath = Path.Combine(mockGoldensDir, "dump.json");
                File.WriteAllText(mockGoldenJsonPath, existingJsonContent);

                var mockGoldenCsPath = Path.Combine(mockGoldensDir, "dump.cs");
                Assert.False(File.Exists(mockGoldenCsPath)); // Intentionally missing

                var actualCsPath = Path.Combine(mockTempDir, "dump.cs");
                var actualJsonPath = Path.Combine(mockTempDir, "dump.json");
                File.WriteAllText(actualCsPath, "// actual cs content");
                File.WriteAllText(actualJsonPath, "{\n  \"actual\": true\n}");

                // Call the exact same shared verification logic without UPDATE_GOLDENS=1
                var updateGoldens = false;

                var ex = Assert.Throws<Xunit.Sdk.TrueException>(() =>
                {
                    AssertOrUpdateGoldenFiles(mockGoldensDir, actualCsPath, actualJsonPath, updateGoldens);
                });

                // Verify the missing golden was NOT created and existing sibling was untouched
                Assert.False(File.Exists(mockGoldenCsPath), "Missing golden must not be created without UPDATE_GOLDENS=1.");
                Assert.Equal(existingJsonContent, File.ReadAllText(mockGoldenJsonPath));
            }
            finally
            {
                if (Directory.Exists(tempBase))
                {
                    Directory.Delete(tempBase, true);
                }
            }
        }

        internal static void AssertOrUpdateGoldenFiles(string goldensDir, string actualDumpCsPath, string actualDumpJsonPath, bool updateGoldens)
        {
            var goldenDumpCsPath = Path.Combine(goldensDir, "dump.cs");
            var goldenDumpJsonPath = Path.Combine(goldensDir, "dump.json");

            if (updateGoldens)
            {
                File.Copy(actualDumpCsPath, goldenDumpCsPath, true);
                File.Copy(actualDumpJsonPath, goldenDumpJsonPath, true);
            }
            else
            {
                Assert.True(File.Exists(goldenDumpCsPath),
                    $"Golden file missing: '{goldenDumpCsPath}'. Golden files must not be silently recreated without opt-in. Set UPDATE_GOLDENS=1 to generate or regenerate golden outputs.");
                Assert.True(File.Exists(goldenDumpJsonPath),
                    $"Golden file missing: '{goldenDumpJsonPath}'. Golden files must not be silently recreated without opt-in. Set UPDATE_GOLDENS=1 to generate or regenerate golden outputs.");
            }
        }

        [Theory]
        [InlineData(MetadataVersionFamily.V16)]
        [InlineData(MetadataVersionFamily.V21)]
        [InlineData(MetadataVersionFamily.V24)]
        [InlineData(MetadataVersionFamily.V27)]
        [InlineData(MetadataVersionFamily.V29)]
        [InlineData(MetadataVersionFamily.V31)]
        [InlineData(MetadataVersionFamily.V35)]
        [InlineData(MetadataVersionFamily.V38)]
        [InlineData(MetadataVersionFamily.V39)]
        public void Corruption_WrongMagic_ThrowsInvalidDataException(MetadataVersionFamily family)
        {
            var bytes = SyntheticMetadataGenerator.Generate(family, seed: 42, MetadataCorruptionType.WrongMagic);
            var ex = Assert.Throws<InvalidDataException>(() => new Metadata(new MemoryStream(bytes)));
            Assert.Contains("not valid metadata file", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData(MetadataVersionFamily.V16)]
        [InlineData(MetadataVersionFamily.V21)]
        [InlineData(MetadataVersionFamily.V24)]
        [InlineData(MetadataVersionFamily.V27)]
        [InlineData(MetadataVersionFamily.V29)]
        [InlineData(MetadataVersionFamily.V31)]
        [InlineData(MetadataVersionFamily.V35)]
        [InlineData(MetadataVersionFamily.V38)]
        [InlineData(MetadataVersionFamily.V39)]
        public void Corruption_OutOfRangeVersion_ThrowsSupportedOrInvalidDataException(MetadataVersionFamily family)
        {
            var bytes = SyntheticMetadataGenerator.Generate(family, seed: 42, MetadataCorruptionType.OutOfRangeVersion);
            Assert.ThrowsAny<Exception>(() => new Metadata(new MemoryStream(bytes)));
        }

        [Theory]
        [InlineData(MetadataVersionFamily.V16)]
        [InlineData(MetadataVersionFamily.V21)]
        [InlineData(MetadataVersionFamily.V24)]
        [InlineData(MetadataVersionFamily.V27)]
        [InlineData(MetadataVersionFamily.V29)]
        [InlineData(MetadataVersionFamily.V31)]
        [InlineData(MetadataVersionFamily.V35)]
        [InlineData(MetadataVersionFamily.V38)]
        [InlineData(MetadataVersionFamily.V39)]
        public void Corruption_TruncatedSection_ThrowsException(MetadataVersionFamily family)
        {
            // TruncatedSection retains 100% of the header and truncates inside the final non-empty
            // section payload (assemblies), exercising failure during section reading.
            var bytes = SyntheticMetadataGenerator.Generate(family, seed: 42, MetadataCorruptionType.TruncatedSection);
            Assert.ThrowsAny<Exception>(() => new Metadata(new MemoryStream(bytes)));
        }

        [Theory]
        [InlineData(MetadataVersionFamily.V16)]
        [InlineData(MetadataVersionFamily.V21)]
        [InlineData(MetadataVersionFamily.V24)]
        [InlineData(MetadataVersionFamily.V27)]
        [InlineData(MetadataVersionFamily.V29)]
        [InlineData(MetadataVersionFamily.V31)]
        [InlineData(MetadataVersionFamily.V35)]
        [InlineData(MetadataVersionFamily.V38)]
        [InlineData(MetadataVersionFamily.V39)]
        public void Corruption_TruncatedHeader_ThrowsException(MetadataVersionFamily family)
        {
            // TruncatedHeader truncates within the header itself.
            var bytes = SyntheticMetadataGenerator.Generate(family, seed: 42, MetadataCorruptionType.TruncatedHeader);
            Assert.ThrowsAny<Exception>(() => new Metadata(new MemoryStream(bytes)));
        }

        [Theory]
        [InlineData(MetadataVersionFamily.V16)]
        [InlineData(MetadataVersionFamily.V21)]
        [InlineData(MetadataVersionFamily.V24)]
        [InlineData(MetadataVersionFamily.V27)]
        [InlineData(MetadataVersionFamily.V29)]
        [InlineData(MetadataVersionFamily.V31)]
        [InlineData(MetadataVersionFamily.V35)]
        [InlineData(MetadataVersionFamily.V38)]
        [InlineData(MetadataVersionFamily.V39)]
        public void Corruption_LiteralExtentPastSectionEnd_RejectsSafelyWithoutThrowing(MetadataVersionFamily family)
        {
            var bytes = SyntheticMetadataGenerator.Generate(family, seed: 42, MetadataCorruptionType.LiteralExtentPastSectionEnd);
            using var metadata = new Metadata(new MemoryStream(bytes));
            var success = metadata.TryGetStringLiteral(0, out var value, out _, out _);
            Assert.False(success);
            Assert.Equal(string.Empty, value);
        }

        [Theory]
        [InlineData(MetadataVersionFamily.V16)]
        [InlineData(MetadataVersionFamily.V21)]
        [InlineData(MetadataVersionFamily.V24)]
        [InlineData(MetadataVersionFamily.V27)]
        [InlineData(MetadataVersionFamily.V29)]
        [InlineData(MetadataVersionFamily.V31)]
        [InlineData(MetadataVersionFamily.V35)]
        [InlineData(MetadataVersionFamily.V38)]
        [InlineData(MetadataVersionFamily.V39)]
        public void Corruption_ZeroedMetadataRecords_ParsedSafely(MetadataVersionFamily family)
        {
            var bytes = SyntheticMetadataGenerator.Generate(family, seed: 42, MetadataCorruptionType.ZeroedMetadataRecords);
            using var metadata = new Metadata(new MemoryStream(bytes));
            Assert.NotNull(metadata);
            Assert.Empty(metadata.imageDefs);
            Assert.Empty(metadata.typeDefs);
        }

        [Theory]
        [InlineData(MetadataVersionFamily.V16)]
        [InlineData(MetadataVersionFamily.V21)]
        [InlineData(MetadataVersionFamily.V24)]
        [InlineData(MetadataVersionFamily.V27)]
        [InlineData(MetadataVersionFamily.V29)]
        [InlineData(MetadataVersionFamily.V31)]
        [InlineData(MetadataVersionFamily.V35)]
        [InlineData(MetadataVersionFamily.V38)]
        [InlineData(MetadataVersionFamily.V39)]
        public void ZeroedRegistrationRecords_ExecutedThroughRealInit_SafelyInitializesEmptyArrays(MetadataVersionFamily family)
        {
            // Exercises the real Il2Cpp.Init(codeRegistration, metadataRegistration) path using in-memory
            // zeroed registration records buffer, ensuring all pointers and counts handle zeroed structures.
            var il2Cpp = SyntheticEnvironment.CreateZeroedRegistrationFixture((double)family);
            Assert.NotNull(il2Cpp);
            Assert.NotNull(il2Cpp.types);
            Assert.Empty(il2Cpp.types);
            Assert.NotNull(il2Cpp.genericMethodPointers);
            Assert.Empty(il2Cpp.genericMethodPointers);
            Assert.NotNull(il2Cpp.invokerPointers);
            Assert.Empty(il2Cpp.invokerPointers);
            Assert.NotNull(il2Cpp.genericInsts);
            Assert.Empty(il2Cpp.genericInsts);
        }

        [Fact]
        public void Determinism_SameFamilyAndSeed_ProducesIdenticalBytes()
        {
            foreach (MetadataVersionFamily family in Enum.GetValues(typeof(MetadataVersionFamily)))
            {
                var bytesA = SyntheticMetadataGenerator.Generate(family, seed: 12345);
                var bytesB = SyntheticMetadataGenerator.Generate(family, seed: 12345);
                Assert.Equal(bytesA, bytesB);

                var bytesDifferentSeed = SyntheticMetadataGenerator.Generate(family, seed: 99999);
                Assert.NotEqual(bytesA, bytesDifferentSeed);
            }
        }

        [Theory]
        [InlineData(MetadataVersionFamily.V16)]
        [InlineData(MetadataVersionFamily.V21)]
        [InlineData(MetadataVersionFamily.V24)]
        [InlineData(MetadataVersionFamily.V27)]
        [InlineData(MetadataVersionFamily.V29)]
        [InlineData(MetadataVersionFamily.V31)]
        [InlineData(MetadataVersionFamily.V35)]
        [InlineData(MetadataVersionFamily.V38)]
        [InlineData(MetadataVersionFamily.V39)]
        public void ParserRoundTrip_LayoutUnions_Preserved(MetadataVersionFamily family)
        {
            var originalBytes = SyntheticMetadataGenerator.Generate(family, seed: 42);
            using var metadata = new Metadata(new MemoryStream(originalBytes));

            // Verify structure layout fields are intact after parsing
            Assert.Equal((int)family, (int)metadata.Version);
            Assert.Equal(2, metadata.typeDefs.Length);
            Assert.Single(metadata.methodDefs);
            Assert.Equal(3, metadata.fieldDefs.Length);
            Assert.Single(metadata.imageDefs);
            Assert.Single(metadata.assemblyDefs);
            Assert.Single(metadata.stringLiterals);

            // Re-serialize the parsed Metadata object model and assert exact byte equality
            var reserializedBytes = SyntheticMetadataGenerator.ReSerialize(metadata, family);
            Assert.Equal(originalBytes, reserializedBytes);

            // Verify mutation sensitivity: altering a parsed field changes the reserialized output
            var originalFlags = metadata.typeDefs[0].flags;
            metadata.typeDefs[0].flags ^= 0x10;
            var mutatedBytes = SyntheticMetadataGenerator.ReSerialize(metadata, family);
            Assert.NotEqual(originalBytes, mutatedBytes);
            metadata.typeDefs[0].flags = originalFlags; // restore
        }

        [Fact]
        public void RepoHygiene_AssertNoCommittedBinaryBlobs()
        {
            var repoRoot = FindRepoRoot();
            var testsDir = Path.Combine(repoRoot, "Il2CppDumper.Tests");
            var forbiddenExtensions = new[] { ".dat", ".bin", ".wasm", ".so", ".elf", ".apk" };

            var files = Directory.GetFiles(testsDir, "*.*", SearchOption.AllDirectories)
                .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) &&
                            !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
                .ToList();

            foreach (var file in files)
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                Assert.DoesNotContain(ext, forbiddenExtensions);
            }
        }

        [Fact]
        public void SuiteRuntimeBudget_AssertUnderLimit()
        {
            // Suite runtime budget: generating and parsing the full matrix (9 families x 6 variants = 54 fixtures)
            // PLUS end-to-end decompilation of all 9 positive families must execute comfortably within 5000 ms.
            var sw = Stopwatch.StartNew();

            var tempDir = Path.Combine(Path.GetTempPath(), "budget_bench_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                var config = new Config
                {
                    DumpAttribute = false,
                    DumpField = true,
                    DumpMethod = true,
                    DumpProperty = true,
                    DumpTypeDefIndex = false
                };

                foreach (MetadataVersionFamily family in Enum.GetValues(typeof(MetadataVersionFamily)))
                {
                    // 1. Generation & parsing across all variants
                    foreach (MetadataCorruptionType corruption in new[]
                    {
                        MetadataCorruptionType.None,
                        MetadataCorruptionType.TruncatedSection,
                        MetadataCorruptionType.WrongMagic,
                        MetadataCorruptionType.OutOfRangeVersion,
                        MetadataCorruptionType.LiteralExtentPastSectionEnd,
                        MetadataCorruptionType.ZeroedMetadataRecords
                    })
                    {
                        var bytes = SyntheticMetadataGenerator.Generate(family, seed: 42, corruption);
                        try
                        {
                            using var m = new Metadata(new MemoryStream(bytes));
                            _ = m.Version;
                        }
                        catch
                        {
                            // Expected for corruption variants
                        }
                    }

                    // 2. End-to-end decompilation of positive fixture
                    var (_, _, executor) = SyntheticEnvironment.Create(family, seed: 42);
                    var familyOutDir = Path.Combine(tempDir, family.ToString());
                    SyntheticEnvironment.DecompileToDirectory(executor, config, familyOutDir);
                }
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }

            sw.Stop();
            Assert.True(sw.ElapsedMilliseconds < 5000, $"Corpus execution exceeded budget: {sw.ElapsedMilliseconds}ms >= 5000ms");
        }
    }
}
