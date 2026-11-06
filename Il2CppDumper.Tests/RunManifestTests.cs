using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Il2CppDumper.Core;
using Il2CppDumper.Tests.Synthetic;
using Xunit;

namespace Il2CppDumper.Tests
{
    public class RunManifestTests
    {
        private static string GetBuildOutputDir()
        {
            var loc = typeof(Program).Assembly.Location;
            return Path.GetDirectoryName(loc)!;
        }

        private static string SetupIsolatedApp(string rootDir, Action<Config>? configure = null)
        {
            var sourceDir = GetBuildOutputDir();
            var appDir = Path.Combine(rootDir, "app");
            Directory.CreateDirectory(appDir);

            foreach (var file in Directory.GetFiles(sourceDir))
            {
                var fileName = Path.GetFileName(file);
                File.Copy(file, Path.Combine(appDir, fileName), true);
            }

            var configPath = Path.Combine(appDir, "config.json");
            var config = JsonSerializer.Deserialize<Config>(File.ReadAllText(configPath))!;
            config.RequireAnyKey = false;
            configure?.Invoke(config);
            File.WriteAllText(configPath, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));

            return appDir;
        }

        private static byte[] WriteULeb128(int value)
        {
            var list = new List<byte>();
            while (value > 127)
            {
                list.Add((byte)((value & 127) | 128));
                value >>= 7;
            }
            list.Add((byte)value);
            return list.ToArray();
        }

        private static byte[] CreateMinimalMetadata(int version = 29)
        {
            var bytes = new byte[1024];
            BitConverter.GetBytes(0xFAB11BAFu).CopyTo(bytes, 0); // magic
            BitConverter.GetBytes(version).CopyTo(bytes, 4);           // version
            BitConverter.GetBytes(256).CopyTo(bytes, 160);        // typeDefinitions offset
            BitConverter.GetBytes(88).CopyTo(bytes, 164);         // typeDefinitions size
            return bytes;
        }

        private static byte[] CreateSyntheticWasm()
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            // WebAssembly magic and version 1
            w.Write(new byte[] { 0x00, 0x61, 0x73, 0x6d, 0x01, 0x00, 0x00, 0x00 });
            // Section 5: Memory section (1 page)
            w.Write(new byte[] { 0x05, 0x03, 0x01, 0x00, 0x01 });
            // Section 11: Data section
            var payload = new List<byte>();
            payload.AddRange(new byte[] { 0x01, 0x00, 0x41, 0x00, 0x0b }); // count=1, idx=0, i32.const, offset=0, end
            var data = new byte[4096];
            payload.AddRange(WriteULeb128(data.Length));
            payload.AddRange(data);
            w.Write((byte)11);
            w.Write(WriteULeb128(payload.Count));
            w.Write(payload.ToArray());
            return ms.ToArray();
        }

        private static (string wasmPath, string metadataPath) CreateSyntheticFixtures(string fixturesDir)
        {
            var metaPath = Path.Combine(fixturesDir, "metadata.dat");
            File.WriteAllBytes(metaPath, CreateMinimalMetadata());

            var wasmPath = Path.Combine(fixturesDir, "empty.wasm");
            File.WriteAllBytes(wasmPath, CreateSyntheticWasm());

            var scriptPath = Path.Combine(fixturesDir, "script.json");
            File.WriteAllText(scriptPath, JsonSerializer.Serialize(new
            {
                codeRegistration = 32UL,
                metadataRegistration = 256UL
            }));

            return (wasmPath, metaPath);
        }

        private static (int exitCode, string stdout, string stderr) RunCli(string appDir, params string[] args)
        {
            var dllPath = Path.Combine(appDir, "Il2CppDumper.dll");
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                WorkingDirectory = appDir
            };
            psi.ArgumentList.Add(dllPath);
            foreach (var arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            using var process = Process.Start(psi)!;
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(30000);
            return (process.ExitCode, stdout, stderr);
        }

        private static string ComputeFileSha256(string filePath)
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(stream);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        [Fact]
        public void Acceptance1_SchemaIsDeterministic_AndOutputsChecksumsVerifiedByRehashing()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "manifest_test_acc1_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var fixturesDir = Path.Combine(tempDir, "fixtures");
                var outDir = Path.Combine(tempDir, "out");
                Directory.CreateDirectory(fixturesDir);
                Directory.CreateDirectory(outDir);

                var (wasmPath, metaPath) = CreateSyntheticFixtures(fixturesDir);

                var config = new Config
                {
                    GenerateRunManifest = true,
                    GenerateJsonDump = true,
                    GenerateStruct = false,
                    GenerateDummyDll = false
                };

                var request = new DumpRequest
                {
                    ExecutablePath = wasmPath,
                    MetadataPath = metaPath,
                    OutputDirectory = outDir,
                    Config = config
                };

                var result = Dumper.Dump(request);
                Assert.True(result.Success, result.Error);

                var manifestPath = Path.Combine(outDir, "run-manifest.json");
                Assert.True(File.Exists(manifestPath));

                var manifestJson = File.ReadAllText(manifestPath);
                var manifest = JsonSerializer.Deserialize<RunManifest>(manifestJson);
                Assert.NotNull(manifest);

                // Check schema structure
                Assert.NotNull(manifest.Tool);
                Assert.Equal("Il2CppDumper", manifest.Tool.Name);
                Assert.False(string.IsNullOrWhiteSpace(manifest.Tool.Version));

                Assert.True(DateTimeOffset.TryParse(manifest.StartedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _));
                Assert.True(manifest.DurationMs >= 0);
                Assert.True(manifest.Success);

                Assert.NotNull(manifest.EffectiveConfig);
                Assert.True(manifest.EffectiveConfig.GenerateRunManifest);
                Assert.True(manifest.EffectiveConfig.GenerateJsonDump);

                Assert.NotNull(manifest.Counts);
                Assert.NotNull(manifest.Warnings);
                Assert.NotNull(manifest.Warnings.ByCategory);
                Assert.NotNull(manifest.Outputs);
                Assert.NotEmpty(manifest.Outputs);

                // Deterministic sorting check: outputs list must be sorted strictly by path
                for (int i = 0; i < manifest.Outputs.Count - 1; i++)
                {
                    var cmp = string.Compare(manifest.Outputs[i].Path, manifest.Outputs[i + 1].Path, StringComparison.Ordinal);
                    Assert.True(cmp <= 0, $"Outputs not deterministically sorted: '{manifest.Outputs[i].Path}' before '{manifest.Outputs[i + 1].Path}'");
                }

                // Verify every entry in outputs by re-hashing the actual file on disk
                foreach (var output in manifest.Outputs)
                {
                    var fullDiskPath = Path.Combine(outDir, output.Path.Replace('/', Path.DirectorySeparatorChar));
                    Assert.True(File.Exists(fullDiskPath), $"Output file {output.Path} recorded in manifest must exist on disk.");

                    var actualSha256 = ComputeFileSha256(fullDiskPath);
                    var actualBytes = new FileInfo(fullDiskPath).Length;

                    Assert.Equal(actualSha256, output.Sha256);
                    Assert.Equal(actualBytes, output.Bytes);
                }

                // run-manifest.json itself must NOT appear inside outputs
                Assert.DoesNotContain(manifest.Outputs, o => o.Path == "run-manifest.json");
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }

        [Fact]
        public void Acceptance2_ConfigOnlyConstruction_ManifestsEffectiveDefaults()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "manifest_test_acc2_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var fixturesDir = Path.Combine(tempDir, "fixtures");
                var outDir = Path.Combine(tempDir, "out");
                Directory.CreateDirectory(fixturesDir);
                Directory.CreateDirectory(outDir);

                var (wasmPath, metaPath) = CreateSyntheticFixtures(fixturesDir);

                // Config-only construction with no external overrides (default new Config())
                var config = new Config
                {
                    GenerateStruct = false,
                    GenerateDummyDll = false
                };

                var request = new DumpRequest
                {
                    ExecutablePath = wasmPath,
                    MetadataPath = metaPath,
                    OutputDirectory = outDir,
                    Config = config
                };

                var result = Dumper.Dump(request);
                Assert.True(result.Success, result.Error);

                var manifestPath = Path.Combine(outDir, "run-manifest.json");
                var manifestJson = File.ReadAllText(manifestPath);
                var manifest = JsonSerializer.Deserialize<RunManifest>(manifestJson)!;

                // Verifies that the run recorded what it actually used (extending #25)
                Assert.True(manifest.EffectiveConfig.DumpProperty);
                Assert.True(manifest.EffectiveConfig.DumpAttribute);
                Assert.True(manifest.EffectiveConfig.DumpMethod);
                Assert.True(manifest.EffectiveConfig.DumpField);
                Assert.False(manifest.EffectiveConfig.GenerateJsonDump);
                Assert.True(manifest.EffectiveConfig.GenerateRunManifest);
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
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
        public void Acceptance3_CountsCrossCheck_AssertManifestCountsEqualParsedOutputCounts(MetadataVersionFamily family)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "manifest_test_acc3_" + family + "_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var (metadata, il2Cpp, executor) = SyntheticEnvironment.Create(family, seed: 42);

                var config = new Config
                {
                    GenerateRunManifest = true,
                    GenerateJsonDump = true,
                    GenerateStruct = false,
                    DumpAttribute = false,
                    DumpField = true,
                    DumpMethod = true,
                    DumpProperty = true
                };

                var startedAt = DateTime.UtcNow;
                var success = Dumper.ExecuteDump(metadata, il2Cpp, config, tempDir + Path.DirectorySeparatorChar, out var dumpError, out var counts, out var warnings);
                Assert.True(success, dumpError);

                var manifest = RunManifestWriter.Write(
                    tempDir,
                    config,
                    startedAt,
                    durationMs: 15,
                    success: success,
                    counts: counts,
                    warnings: warnings
                );

                Assert.NotNull(manifest);

                // 1. Cross-check against parsed dump.json
                var dumpJsonPath = Path.Combine(tempDir, "dump.json");
                Assert.True(File.Exists(dumpJsonPath));
                var dumpModel = JsonSerializer.Deserialize<JsonDumpModel>(File.ReadAllText(dumpJsonPath))!;

                int parsedTypesCount = dumpModel.types.Count;
                int parsedFieldsCount = dumpModel.types.Sum(t => t.fields.Count);
                int parsedMethodsCount = dumpModel.types.Sum(t => t.methods.Count);
                int parsedPropertiesCount = dumpModel.types.Sum(t => t.properties.Count);

                Assert.Equal(parsedTypesCount, manifest.Counts.Types);
                Assert.Equal(parsedFieldsCount, manifest.Counts.Fields);
                Assert.Equal(parsedMethodsCount, manifest.Counts.Methods);
                Assert.Equal(parsedPropertiesCount, manifest.Counts.Properties);

                // Synthetic corpus guarantees 2 types, 3 fields, 1 method, 0 properties
                Assert.Equal(2, manifest.Counts.Types);
                Assert.Equal(3, manifest.Counts.Fields);
                Assert.Equal(1, manifest.Counts.Methods);
                Assert.Equal(0, manifest.Counts.Properties);

                // 2. Cross-check string literals count when GenerateStruct is false
                Assert.Equal(0, manifest.Counts.StringLiterals);

                // Zero attribute failure on valid corpus
                Assert.Equal(0, manifest.Counts.AttributesFailed);
                Assert.Equal(0, manifest.Warnings.Total);
                Assert.Equal(0, manifest.Warnings.ByCategory.AttributeDecode);
                Assert.Equal(0, manifest.Warnings.ByCategory.StringLiteral);

                // Verify output checksums on disk
                foreach (var output in manifest.Outputs)
                {
                    var fullDiskPath = Path.Combine(tempDir, output.Path.Replace('/', Path.DirectorySeparatorChar));
                    Assert.True(File.Exists(fullDiskPath), $"Output file {output.Path} must exist on disk.");
                    Assert.Equal(ComputeFileSha256(fullDiskPath), output.Sha256);
                    Assert.Equal(new FileInfo(fullDiskPath).Length, output.Bytes);
                }
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }

        [Fact]
        public void Acceptance4_FailureRun_BadBinFixture_WritesManifestWithSuccessFalseAndExitCode1()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "manifest_test_acc4_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var appDir = SetupIsolatedApp(tempDir);
                var fixturesDir = Path.Combine(tempDir, "fixtures");
                var outDir = Path.Combine(tempDir, "out");
                Directory.CreateDirectory(fixturesDir);
                Directory.CreateDirectory(outDir);

                // Create minimal valid metadata and unsupported bad.bin fixture
                var metaPath = Path.Combine(fixturesDir, "metadata.dat");
                File.WriteAllBytes(metaPath, CreateMinimalMetadata());

                var badBinPath = Path.Combine(fixturesDir, "bad.bin");
                File.WriteAllBytes(badBinPath, Encoding.ASCII.GetBytes("BAD!"));

                // Execute CLI: should exit with exit code 1 (Issue #8 behavior unchanged)
                var (exitCode, stdout, _) = RunCli(appDir, badBinPath, metaPath, outDir);

                Assert.Equal(1, exitCode);
                Assert.Contains("ERROR: il2cpp file not supported.", stdout);

                // Manifest must be written on failure run
                var manifestPath = Path.Combine(outDir, "run-manifest.json");
                Assert.True(File.Exists(manifestPath), "run-manifest.json must be written on failed runs.");

                var manifestJson = File.ReadAllText(manifestPath);
                var manifest = JsonSerializer.Deserialize<RunManifest>(manifestJson)!;

                Assert.False(manifest.Success);
                Assert.NotNull(manifest.Tool);
                Assert.Equal("Il2CppDumper", manifest.Tool.Name);
                Assert.True(manifest.DurationMs >= 0);

                // Partial counts before failure
                Assert.Equal(0, manifest.Counts.Types);
                Assert.Equal(0, manifest.Counts.Methods);
                Assert.Equal(0, manifest.Counts.Fields);
                Assert.Equal(0, manifest.Counts.Properties);
                Assert.Equal(0, manifest.Counts.Attributes);
                Assert.Equal(0, manifest.Counts.AttributesFailed);
                Assert.Equal(0, manifest.Counts.StringLiterals);

                // Dump metadata sidecar is preserved in partial outputs
                Assert.Contains(manifest.Outputs, o => o.Path == "dump-metadata.json");
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }

        [Fact]
        public void Acceptance5_GenerateRunManifestFalse_WritesNoFile_AllOtherOutputsUnchanged()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "manifest_test_acc5_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var fixturesDir = Path.Combine(tempDir, "fixtures");
                var outDir = Path.Combine(tempDir, "out");
                Directory.CreateDirectory(fixturesDir);
                Directory.CreateDirectory(outDir);

                var (wasmPath, metaPath) = CreateSyntheticFixtures(fixturesDir);

                var config = new Config
                {
                    GenerateRunManifest = false, // Explicitly disabled
                    GenerateJsonDump = true,
                    GenerateStruct = false,
                    GenerateDummyDll = false
                };

                var request = new DumpRequest
                {
                    ExecutablePath = wasmPath,
                    MetadataPath = metaPath,
                    OutputDirectory = outDir,
                    Config = config
                };

                var result = Dumper.Dump(request);
                Assert.True(result.Success, result.Error);

                var manifestPath = Path.Combine(outDir, "run-manifest.json");
                Assert.False(File.Exists(manifestPath), "run-manifest.json must not be written when GenerateRunManifest is false.");
                Assert.False(result.Outputs.ContainsKey("run-manifest.json"));

                // All other expected outputs are present and unmodified
                Assert.True(File.Exists(Path.Combine(outDir, "dump.cs")));
                Assert.True(File.Exists(Path.Combine(outDir, "dump.json")));
                Assert.True(File.Exists(Path.Combine(outDir, "dump-metadata.json")));
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }

        [Fact]
        public void Acceptance6_FailureCountersAndWarningCategories_PropagateCorrectly()
        {
            // Verify that attribute decoding failure and string literal failures correctly map to warning categories
            var tempDir = Path.Combine(Path.GetTempPath(), "manifest_test_acc6_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var (metadata, il2Cpp, executor) = SyntheticEnvironment.Create(MetadataVersionFamily.V29, seed: 42);

                var decompiler = new Il2CppDecompiler(executor);
                var config = new Config
                {
                    DumpAttribute = true,
                    GenerateRunManifest = true
                };

                decompiler.Decompile(config, tempDir + Path.DirectorySeparatorChar);

                // Verify counts and categories can be constructed and serialized cleanly
                var counts = new RunManifestCounts
                {
                    Types = decompiler.TypesCount,
                    Methods = decompiler.MethodsCount,
                    Fields = decompiler.FieldsCount,
                    Properties = decompiler.PropertiesCount,
                    Attributes = decompiler.CustomAttributeWrittenCount,
                    AttributesFailed = 1, // simulated attribute decode failure
                    StringLiterals = 1
                };

                var warnings = new RunManifestWarnings
                {
                    Total = 2,
                    ByCategory = new RunManifestWarningCategories
                    {
                        AttributeDecode = 1,
                        StringLiteral = 1
                    }
                };

                var manifest = RunManifestWriter.Write(
                    tempDir,
                    config,
                    DateTime.UtcNow,
                    durationMs: 42,
                    success: true,
                    counts: counts,
                    warnings: warnings
                );

                Assert.NotNull(manifest);
                Assert.Equal(1, manifest.Counts.AttributesFailed);
                Assert.Equal(2, manifest.Warnings.Total);
                Assert.Equal(1, manifest.Warnings.ByCategory.AttributeDecode);
                Assert.Equal(1, manifest.Warnings.ByCategory.StringLiteral);

                var manifestPath = Path.Combine(tempDir, "run-manifest.json");
                Assert.True(File.Exists(manifestPath));
                var reloaded = JsonSerializer.Deserialize<RunManifest>(File.ReadAllText(manifestPath))!;
                Assert.Equal(1, reloaded.Counts.AttributesFailed);
                Assert.Equal(2, reloaded.Warnings.Total);
                Assert.Equal(1, reloaded.Warnings.ByCategory.AttributeDecode);
                Assert.Equal(1, reloaded.Warnings.ByCategory.StringLiteral);
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }

        [Fact]
        public void Regression_P1_PreexistingFilesFromPreviousRuns_AreExcludedFromOutputs()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "manifest_regr_p1_stale_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var fixturesDir = Path.Combine(tempDir, "fixtures");
                Directory.CreateDirectory(fixturesDir);
                var (wasmPath, metaPath) = CreateSyntheticFixtures(fixturesDir);

                var outDir = Path.Combine(tempDir, "out");
                Directory.CreateDirectory(outDir);

                // Place stale outputs from a simulated previous run
                var staleJson = Path.Combine(outDir, "dump.json");
                File.WriteAllText(staleJson, "{\"stale\": true}");
                var staleDllDir = Path.Combine(outDir, "DummyDll");
                Directory.CreateDirectory(staleDllDir);
                var staleDll = Path.Combine(staleDllDir, "old.dll");
                File.WriteAllText(staleDll, "stale-dll-content");

                var req = new DumpRequest
                {
                    ExecutablePath = wasmPath,
                    MetadataPath = metaPath,
                    OutputDirectory = outDir,
                    Config = new Config
                    {
                        GenerateRunManifest = true,
                        GenerateJsonDump = false,
                        GenerateDummyDll = false,
                        GenerateStruct = false
                    }
                };

                var result = Dumper.Dump(req);
                Assert.True(result.Success, result.Error);

                var manifestPath = Path.Combine(outDir, "run-manifest.json");
                Assert.True(File.Exists(manifestPath));
                var manifest = JsonSerializer.Deserialize<RunManifest>(File.ReadAllText(manifestPath))!;

                // Stale files must NOT be in manifest.Outputs
                Assert.DoesNotContain(manifest.Outputs, o => o.Path == "dump.json");
                Assert.DoesNotContain(manifest.Outputs, o => o.Path.Contains("old.dll"));

                // Generated outputs of this run MUST be present
                Assert.Contains(manifest.Outputs, o => o.Path == "dump.cs");
                Assert.Contains(manifest.Outputs, o => o.Path == "dump-metadata.json");
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }

        [Fact]
        public void Regression_P1_UnwritableManifestDestination_LogsWarningAndPopulatesResultWarnings()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "manifest_regr_p1_unwritable_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var fixturesDir = Path.Combine(tempDir, "fixtures");
                Directory.CreateDirectory(fixturesDir);
                var (wasmPath, metaPath) = CreateSyntheticFixtures(fixturesDir);

                var outDir = Path.Combine(tempDir, "out");
                Directory.CreateDirectory(outDir);

                // Create a directory where the manifest file would be written
                var blockerDir = Path.Combine(outDir, "run-manifest.json");
                Directory.CreateDirectory(blockerDir);

                var req = new DumpRequest
                {
                    ExecutablePath = wasmPath,
                    MetadataPath = metaPath,
                    OutputDirectory = outDir,
                    Config = new Config
                    {
                        GenerateRunManifest = true,
                        GenerateJsonDump = false,
                        GenerateDummyDll = false,
                        GenerateStruct = false
                    }
                };

                var result = Dumper.Dump(req);
                Assert.True(result.Success);
                Assert.False(result.Outputs.ContainsKey("run-manifest.json"));
                Assert.Contains(result.Warnings, w => w.Contains("Failed to emit run manifest"));
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }

        [Fact]
        public void Regression_P1_LockedOutputFile_DoesNotAbortEntireManifestEmission()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "manifest_regr_p1_locked_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var file1 = Path.Combine(tempDir, "file1.txt");
                File.WriteAllText(file1, "hello");
                var file2 = Path.Combine(tempDir, "file2.txt");
                File.WriteAllText(file2, "world");

                // Lock file1 with FileShare.None
                using var lockedStream = new FileStream(file1, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

                var outputs = RunManifestWriter.CollectOutputs(tempDir, new[] { file1, file2 });

                // file2 was still hashed successfully despite file1 being locked
                Assert.Contains(outputs, o => o.Path == "file2.txt");
                Assert.DoesNotContain(outputs, o => o.Path == "file1.txt");
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }

        [Fact]
        public void Regression_P2_LegacyV24_CustomAttributes_IncrementsAttributesCount()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "manifest_regr_p2_v24_attrs_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var (metadata, il2Cpp, executor) = SyntheticEnvironment.Create(MetadataVersionFamily.V24, seed: 42);

                // Add an attribute type range and attribute type for custom attribute index 0
                metadata.attributeTypeRanges = new Il2CppCustomAttributeTypeRange[]
                {
                    new Il2CppCustomAttributeTypeRange { start = 0, count = 1 }
                };
                metadata.attributeTypes = new[] { 2 }; // maps to types[2] (int)
                executor.customAttributeGenerators = new ulong[] { 0x1000 };

                var decompiler = new Il2CppDecompiler(executor);
                var config = new Config
                {
                    DumpAttribute = true,
                    DumpField = false,
                    DumpMethod = false,
                    DumpProperty = false
                };

                // Type 0 has customAttributeIndex = 0
                metadata.typeDefs[0].customAttributeIndex = 0;

                var success = decompiler.Decompile(config, tempDir + Path.DirectorySeparatorChar);
                Assert.True(success);
                Assert.True(decompiler.CustomAttributeWrittenCount >= 1, "Legacy v24 custom attributes must increment CustomAttributeWrittenCount.");
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }

        [Fact]
        public void Regression_P2_CorruptedParentIndex_ExecutionFailsBeforeEmittingType_CountsTypesZero()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "manifest_regr_p2_corrupt_parent_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var (metadata, il2Cpp, _) = SyntheticEnvironment.Create(MetadataVersionFamily.V24, seed: 42);

                // Corrupt parent index of first type so resolution throws before type definition header is emitted
                metadata.typeDefs[0].parentIndex = int.MaxValue;

                var config = new Config
                {
                    DumpAttribute = false,
                    DumpField = false,
                    DumpMethod = false,
                    DumpProperty = false
                };

                var success = Dumper.ExecuteDump(metadata, il2Cpp, config, tempDir + Path.DirectorySeparatorChar, out _, out var counts, out _);
                Assert.False(success);
                Assert.Equal(0, counts.Types);
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }

        [Fact]
        public void Regression_R1_CliMetadataInvalid_DoesNotIncludeStaleFiles()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "manifest_regr_r1_cli_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var appDir = SetupIsolatedApp(tempDir, cfg =>
                {
                    cfg.GenerateRunManifest = true;
                    cfg.GenerateJsonDump = false;
                    cfg.GenerateDummyDll = false;
                    cfg.GenerateStruct = false;
                });

                var fixturesDir = Path.Combine(tempDir, "fixtures");
                Directory.CreateDirectory(fixturesDir);
                var (wasmPath, _) = CreateSyntheticFixtures(fixturesDir);

                // Create invalid metadata
                var badMetaPath = Path.Combine(fixturesDir, "bad_meta.dat");
                File.WriteAllBytes(badMetaPath, new byte[] { 0x01, 0x02, 0x03, 0x04 });

                var outDir = Path.Combine(tempDir, "out");
                Directory.CreateDirectory(outDir);

                // Stale files
                File.WriteAllText(Path.Combine(outDir, "dump.json"), "{\"stale\": true}");
                var staleDllDir = Path.Combine(outDir, "DummyDll");
                Directory.CreateDirectory(staleDllDir);
                File.WriteAllText(Path.Combine(staleDllDir, "old.dll"), "stale-dll");

                var (exitCode, _, _) = RunCli(appDir, wasmPath, badMetaPath, outDir);
                Assert.Equal(1, exitCode);

                var manifestPath = Path.Combine(outDir, "run-manifest.json");
                Assert.True(File.Exists(manifestPath));
                var manifest = JsonSerializer.Deserialize<RunManifest>(File.ReadAllText(manifestPath))!;
                Assert.False(manifest.Success);

                // Must not include stale files
                Assert.DoesNotContain(manifest.Outputs, o => o.Path == "dump.json");
                Assert.DoesNotContain(manifest.Outputs, o => o.Path.Contains("old.dll"));

                // Must include dump-metadata.json
                Assert.Contains(manifest.Outputs, o => o.Path == "dump-metadata.json");
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }

        [Fact]
        public void Regression_R2_LockedPriorManifest_IsNotPublishedInOutputs()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "manifest_regr_r2_locked_manifest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var fixturesDir = Path.Combine(tempDir, "fixtures");
                Directory.CreateDirectory(fixturesDir);
                var (wasmPath, metaPath) = CreateSyntheticFixtures(fixturesDir);

                var outDir = Path.Combine(tempDir, "out");
                Directory.CreateDirectory(outDir);

                // Pre-create old manifest with past startedAt
                var oldManifest = new RunManifest
                {
                    StartedAt = "2000-01-01T00:00:00Z",
                    Success = true
                };
                var manifestFile = Path.Combine(outDir, "run-manifest.json");
                File.WriteAllText(manifestFile, JsonSerializer.Serialize(oldManifest));

                // Lock it with FileShare.Read so delete and overwrite fail
                using var fs = new FileStream(manifestFile, FileMode.Open, FileAccess.Read, FileShare.Read);

                var req = new DumpRequest
                {
                    ExecutablePath = wasmPath,
                    MetadataPath = metaPath,
                    OutputDirectory = outDir,
                    Config = new Config
                    {
                        GenerateRunManifest = true,
                        GenerateJsonDump = false,
                        GenerateDummyDll = false,
                        GenerateStruct = false
                    }
                };

                var result = Dumper.Dump(req);
                Assert.True(result.Success);

                if (OperatingSystem.IsWindows())
                {
                    Assert.False(result.Outputs.ContainsKey("run-manifest.json"));
                    Assert.Contains(result.Warnings, w => w.Contains("Failed to emit run manifest"));
                }
                else
                {
                    Assert.True(result.Outputs.ContainsKey("run-manifest.json"));
                    var activeManifest = JsonSerializer.Deserialize<RunManifest>(File.ReadAllText(manifestFile))!;
                    Assert.NotEqual("2000-01-01T00:00:00Z", activeManifest.StartedAt);
                }
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }

        [Fact]
        public void Regression_R3_InvalidMetadataHeader_IncludesSidecarInManifestOutputs()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "manifest_regr_r3_invalid_header_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var fixturesDir = Path.Combine(tempDir, "fixtures");
                Directory.CreateDirectory(fixturesDir);
                var (wasmPath, _) = CreateSyntheticFixtures(fixturesDir);

                var badMetaPath = Path.Combine(fixturesDir, "bad_header.dat");
                File.WriteAllBytes(badMetaPath, new byte[] { 0x11, 0x22, 0x33, 0x44, 0x00, 0x00, 0x00, 0x00 });

                var outDir = Path.Combine(tempDir, "out");
                Directory.CreateDirectory(outDir);

                var req = new DumpRequest
                {
                    ExecutablePath = wasmPath,
                    MetadataPath = badMetaPath,
                    OutputDirectory = outDir,
                    Config = new Config
                    {
                        GenerateRunManifest = true
                    }
                };

                var result = Dumper.Dump(req);
                Assert.False(result.Success);

                var manifestPath = Path.Combine(outDir, "run-manifest.json");
                Assert.True(File.Exists(manifestPath));
                var manifest = JsonSerializer.Deserialize<RunManifest>(File.ReadAllText(manifestPath))!;
                Assert.False(manifest.Success);

                // dump-metadata.json must be present in manifest.Outputs
                Assert.Contains(manifest.Outputs, o => o.Path == "dump-metadata.json");
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }

        [Fact]
        public void Regression_R4_LockedOutputFile_PersistsOutputHashWarningInManifest()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "manifest_regr_r4_hash_warning_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var file1 = Path.Combine(tempDir, "file1.txt");
                File.WriteAllText(file1, "content1");
                var file2 = Path.Combine(tempDir, "file2.txt");
                File.WriteAllText(file2, "content2");

                using var locked = new FileStream(file1, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

                var manifest = RunManifestWriter.Write(
                    tempDir,
                    new Config { GenerateRunManifest = true },
                    DateTime.UtcNow,
                    10,
                    true,
                    new RunManifestCounts(),
                    new RunManifestWarnings(),
                    new[] { file1, file2 }
                );

                Assert.NotNull(manifest);
                Assert.True(manifest.Warnings.ByCategory.OutputHash >= 1);
                Assert.True(manifest.Warnings.Total >= 1);

                var reloaded = JsonSerializer.Deserialize<RunManifest>(File.ReadAllText(Path.Combine(tempDir, "run-manifest.json")))!;
                Assert.True(reloaded.Warnings.ByCategory.OutputHash >= 1);
                Assert.True(reloaded.Warnings.Total >= 1);
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }
}
}
