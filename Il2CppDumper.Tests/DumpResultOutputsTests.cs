using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Il2CppDumper.Core;
using Xunit;

namespace Il2CppDumper.Tests
{
    public class DumpResultOutputsTests
    {
        private static byte[] CreateMinimalMetadata(int version = 29)
        {
            var bytes = new byte[1024];
            BitConverter.GetBytes(0xFAB11BAFu).CopyTo(bytes, 0); // magic
            BitConverter.GetBytes(version).CopyTo(bytes, 4);       // version
            BitConverter.GetBytes(256).CopyTo(bytes, 160);        // typeDefinitions offset
            BitConverter.GetBytes(88).CopyTo(bytes, 164);         // typeDefinitions size
            return bytes;
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

        private static (string wasmPath, string metaPath) CreateSyntheticFixtures(string fixturesDir)
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

        private static string ComputeSha256(string filePath)
        {
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(filePath);
            var hash = sha.ComputeHash(stream);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }

        [Fact]
        public void TwoSuccessfulRuns_ReusedDirectory_SecondRunDisablesJsonDump_ExcludesStaleDumpJsonFromOutputsAndProducedFiles()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "outputs_regr_json_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var fixturesDir = Path.Combine(tempDir, "fixtures");
                Directory.CreateDirectory(fixturesDir);
                var (wasmPath, metaPath) = CreateSyntheticFixtures(fixturesDir);

                var outDir = Path.Combine(tempDir, "out");
                Directory.CreateDirectory(outDir);

                var config = new Config
                {
                    GenerateJsonDump = true,
                    GenerateStruct = false,
                    GenerateDummyDll = false,
                    GenerateProtectionReport = false,
                    GenerateRunManifest = true
                };

                // First run: GenerateJsonDump = true
                var first = Dumper.Dump(new DumpRequest
                {
                    ExecutablePath = wasmPath,
                    MetadataPath = metaPath,
                    OutputDirectory = outDir,
                    Config = config
                });

                Assert.True(first.Success, first.Error);
                Assert.True(first.Outputs.ContainsKey("dump.json"));
                var dumpJsonPath = Path.Combine(outDir, "dump.json");
                Assert.Contains(dumpJsonPath, first.ProducedFiles);
                Assert.True(File.Exists(dumpJsonPath));
                var oldHash = ComputeSha256(dumpJsonPath);
                var firstInputHash = ComputeSha256(wasmPath);

                // Modify input identity: append a valid custom section to the WASM file
                using (var append = new FileStream(wasmPath, FileMode.Append))
                {
                    append.Write(new byte[] { 0, 2, 1, (byte)'x' });
                }
                var secondInputHash = ComputeSha256(wasmPath);
                Assert.NotEqual(firstInputHash, secondInputHash);

                // Second run: GenerateJsonDump = false in the same output directory
                config.GenerateJsonDump = false;
                var second = Dumper.Dump(new DumpRequest
                {
                    ExecutablePath = wasmPath,
                    MetadataPath = metaPath,
                    OutputDirectory = outDir,
                    Config = config
                });

                Assert.True(second.Success, second.Error);

                // The second call must not advertise stale dump.json in Outputs or ProducedFiles
                Assert.False(second.Outputs.ContainsKey("dump.json"));
                Assert.DoesNotContain(dumpJsonPath, second.ProducedFiles);

                // The old dump.json file on disk must not have been deleted or overwritten
                Assert.True(File.Exists(dumpJsonPath));
                Assert.Equal(oldHash, ComputeSha256(dumpJsonPath));

                // Current-run outputs are present
                Assert.True(second.Outputs.ContainsKey("dump.cs"));
                Assert.Contains(Path.Combine(outDir, "dump.cs"), second.ProducedFiles);

                // Manifest outputs also exclude the stale dump.json
                var manifestPath = Path.Combine(outDir, "run-manifest.json");
                using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
                var inManifest = manifest.RootElement.GetProperty("outputs")
                    .EnumerateArray()
                    .Any(x => x.GetProperty("path").GetString() == "dump.json");
                Assert.False(inManifest);
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
        public void TwoSuccessfulRuns_ReusedDirectory_SecondRunDisablesDummyDll_ExcludesStaleDummyDllFromOutputs()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "outputs_regr_dummydll_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var fixturesDir = Path.Combine(tempDir, "fixtures");
                Directory.CreateDirectory(fixturesDir);
                var (wasmPath, metaPath) = CreateSyntheticFixtures(fixturesDir);

                var outDir = Path.Combine(tempDir, "out");
                Directory.CreateDirectory(outDir);

                var config = new Config
                {
                    GenerateJsonDump = false,
                    GenerateStruct = false,
                    GenerateDummyDll = true,
                    GenerateProtectionReport = false
                };

                // Run 1: DummyDll enabled
                var first = Dumper.Dump(new DumpRequest
                {
                    ExecutablePath = wasmPath,
                    MetadataPath = metaPath,
                    OutputDirectory = outDir,
                    Config = config
                });

                Assert.True(first.Success, first.Error);
                Assert.True(first.Outputs.ContainsKey("DummyDll"));
                var dummyDllDir = Path.Combine(outDir, "DummyDll");
                Assert.True(Directory.Exists(dummyDllDir));
                Assert.Contains(first.ProducedFiles, f => f.StartsWith(dummyDllDir, StringComparison.OrdinalIgnoreCase));

                // Run 2: DummyDll disabled
                config.GenerateDummyDll = false;
                var second = Dumper.Dump(new DumpRequest
                {
                    ExecutablePath = wasmPath,
                    MetadataPath = metaPath,
                    OutputDirectory = outDir,
                    Config = config
                });

                Assert.True(second.Success, second.Error);
                Assert.False(second.Outputs.ContainsKey("DummyDll"));
                Assert.DoesNotContain(second.ProducedFiles, f => f.StartsWith(dummyDllDir, StringComparison.OrdinalIgnoreCase));

                // Directory on disk was not deleted by the second execution
                Assert.True(Directory.Exists(dummyDllDir));
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
        public void EarlyInitializationFailure_ExecutableNotFound_DoesNotAttributePriorRunArtifacts()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "outputs_regr_missing_exe_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var fixturesDir = Path.Combine(tempDir, "fixtures");
                Directory.CreateDirectory(fixturesDir);
                var (wasmPath, metaPath) = CreateSyntheticFixtures(fixturesDir);

                var outDir = Path.Combine(tempDir, "out");
                Directory.CreateDirectory(outDir);

                // Run 1: Full successful run
                var first = Dumper.Dump(new DumpRequest
                {
                    ExecutablePath = wasmPath,
                    MetadataPath = metaPath,
                    OutputDirectory = outDir,
                    Config = new Config { GenerateJsonDump = true, GenerateStruct = true }
                });
                Assert.True(first.Success, first.Error);
                Assert.True(first.Outputs.ContainsKey("dump.cs"));
                Assert.True(first.Outputs.ContainsKey("dump.json"));

                // Run 2: Missing executable in the same output directory
                var nonExistentExe = Path.Combine(tempDir, "non_existent.wasm");
                var second = Dumper.Dump(new DumpRequest
                {
                    ExecutablePath = nonExistentExe,
                    MetadataPath = metaPath,
                    OutputDirectory = outDir,
                    Config = new Config { GenerateRunManifest = true }
                });

                Assert.False(second.Success);
                Assert.Contains("Executable file not found", second.Error);

                // Stale artifacts from run 1 must not be attributed to run 2
                Assert.False(second.Outputs.ContainsKey("dump.cs"));
                Assert.False(second.Outputs.ContainsKey("dump.json"));
                Assert.False(second.Outputs.ContainsKey("script.json"));
                Assert.False(second.Outputs.ContainsKey("DummyDll"));

                // Run manifest from current failed execution is reported
                Assert.True(second.Outputs.ContainsKey("run-manifest.json"));
                var manifestPath = Path.Combine(outDir, "run-manifest.json");
                Assert.Single(second.ProducedFiles);
                Assert.Equal(manifestPath, second.ProducedFiles[0]);
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
        public void EarlyInitializationFailure_InvalidMetadataHeader_ReportsOnlyCurrentRunPartialArtifacts()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "outputs_regr_bad_header_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var fixturesDir = Path.Combine(tempDir, "fixtures");
                Directory.CreateDirectory(fixturesDir);
                var (wasmPath, metaPath) = CreateSyntheticFixtures(fixturesDir);

                var outDir = Path.Combine(tempDir, "out");
                Directory.CreateDirectory(outDir);

                // Run 1: Successful run
                var first = Dumper.Dump(new DumpRequest
                {
                    ExecutablePath = wasmPath,
                    MetadataPath = metaPath,
                    OutputDirectory = outDir,
                    Config = new Config { GenerateJsonDump = true }
                });
                Assert.True(first.Success, first.Error);

                // Create a metadata file with invalid magic bytes
                var badMetaPath = Path.Combine(fixturesDir, "bad_header.dat");
                File.WriteAllBytes(badMetaPath, new byte[] { 0x11, 0x22, 0x33, 0x44, 0x00, 0x00, 0x00, 0x00 });

                // Run 2: Invalid metadata header
                var second = Dumper.Dump(new DumpRequest
                {
                    ExecutablePath = wasmPath,
                    MetadataPath = badMetaPath,
                    OutputDirectory = outDir,
                    Config = new Config { GenerateRunManifest = true }
                });

                Assert.False(second.Success);
                Assert.Contains("Metadata file not found or encrypted", second.Error);

                // Stale artifacts from run 1 must not be returned
                Assert.False(second.Outputs.ContainsKey("dump.cs"));
                Assert.False(second.Outputs.ContainsKey("dump.json"));

                // Current-run partial artifacts (provenance sidecar, protection diagnostics, and run-manifest) must be reported
                Assert.True(second.Outputs.ContainsKey("dump-metadata.json"));
                Assert.True(second.Outputs.ContainsKey("protection-report.json"));
                Assert.True(second.Outputs.ContainsKey("run-manifest.json"));

                var dumpMetaPath = Path.Combine(outDir, "dump-metadata.json");
                var protectionReportPath = Path.Combine(outDir, "protection-report.json");
                var manifestPath = Path.Combine(outDir, "run-manifest.json");

                Assert.Equal(3, second.ProducedFiles.Count);
                Assert.Contains(dumpMetaPath, second.ProducedFiles);
                Assert.Contains(protectionReportPath, second.ProducedFiles);
                Assert.Contains(manifestPath, second.ProducedFiles);
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
        public void EarlyInitializationFailure_WithoutRunManifest_ReturnsEmptyOutputsAndProducedFiles()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "outputs_regr_no_manifest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var fixturesDir = Path.Combine(tempDir, "fixtures");
                Directory.CreateDirectory(fixturesDir);
                var (wasmPath, metaPath) = CreateSyntheticFixtures(fixturesDir);

                var outDir = Path.Combine(tempDir, "out");
                Directory.CreateDirectory(outDir);

                // Run 1: Successful run
                var first = Dumper.Dump(new DumpRequest
                {
                    ExecutablePath = wasmPath,
                    MetadataPath = metaPath,
                    OutputDirectory = outDir,
                    Config = new Config { GenerateJsonDump = true }
                });
                Assert.True(first.Success, first.Error);

                // Run 2: Missing executable with GenerateRunManifest = false
                var second = Dumper.Dump(new DumpRequest
                {
                    ExecutablePath = Path.Combine(tempDir, "non_existent.wasm"),
                    MetadataPath = metaPath,
                    OutputDirectory = outDir,
                    Config = new Config { GenerateRunManifest = false }
                });

                Assert.False(second.Success);
                Assert.Empty(second.Outputs);
                Assert.Empty(second.ProducedFiles);

                // Prior run files remain intact on disk
                Assert.True(File.Exists(Path.Combine(outDir, "dump.cs")));
                Assert.True(File.Exists(Path.Combine(outDir, "dump.json")));
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
        public void OutputsAndProducedFiles_RelationshipIsConsistent()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "outputs_regr_rel_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var fixturesDir = Path.Combine(tempDir, "fixtures");
                Directory.CreateDirectory(fixturesDir);
                var (wasmPath, metaPath) = CreateSyntheticFixtures(fixturesDir);

                var outDir = Path.Combine(tempDir, "out");
                Directory.CreateDirectory(outDir);

                var result = Dumper.Dump(new DumpRequest
                {
                    ExecutablePath = wasmPath,
                    MetadataPath = metaPath,
                    OutputDirectory = outDir,
                    Config = new Config
                    {
                        GenerateJsonDump = true,
                        GenerateStruct = true,
                        GenerateDummyDll = true,
                        GenerateProtectionReport = true,
                        GenerateRunManifest = true
                    }
                });

                Assert.True(result.Success, result.Error);
                Assert.NotEmpty(result.Outputs);
                Assert.NotEmpty(result.ProducedFiles);

                foreach (var (artifact, path) in result.Outputs)
                {
                    if (artifact == "DummyDll")
                    {
                        Assert.True(Directory.Exists(path), $"DummyDll directory '{path}' should exist.");
                        Assert.Contains(result.ProducedFiles, f => f.StartsWith(path, StringComparison.OrdinalIgnoreCase));
                    }
                    else
                    {
                        Assert.True(File.Exists(path), $"Artifact file '{path}' should exist.");
                        Assert.Contains(path, result.ProducedFiles);
                    }
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
    }
}
