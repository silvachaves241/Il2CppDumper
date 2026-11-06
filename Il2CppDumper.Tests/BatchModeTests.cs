using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Il2CppDumper.Core;
using Xunit;

namespace Il2CppDumper.Tests
{
    public class BatchModeTests
    {
        private static byte[] CreateMinimalMetadata(int version = 29)
        {
            var bytes = new byte[1024];
            BitConverter.GetBytes(0xFAB11BAFu).CopyTo(bytes, 0); // magic
            BitConverter.GetBytes(version).CopyTo(bytes, 4);      // version
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

        private static (string wasmPath, string metaPath, string scriptPath) CreateSyntheticFixtures(string fixturesDir, string wasmName = "sample.wasm")
        {
            Directory.CreateDirectory(fixturesDir);
            var metaPath = Path.Combine(fixturesDir, "metadata.dat");
            if (!File.Exists(metaPath))
            {
                File.WriteAllBytes(metaPath, CreateMinimalMetadata());
            }

            var wasmPath = Path.Combine(fixturesDir, wasmName);
            if (!File.Exists(wasmPath))
            {
                File.WriteAllBytes(wasmPath, CreateSyntheticWasm());
            }

            var scriptPath = Path.Combine(fixturesDir, "script.json");
            if (!File.Exists(scriptPath))
            {
                File.WriteAllText(scriptPath, JsonSerializer.Serialize(new
                {
                    codeRegistration = 32UL,
                    metadataRegistration = 256UL
                }));
            }

            return (wasmPath, metaPath, scriptPath);
        }

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

        [Fact]
        public void Batch_ThreeEntryFixture_TwoValidOneInvalid_RecordsErrorAndCompletesValidSets()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "batch-three-entry-" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                var appDir = SetupIsolatedApp(tempDir);
                var fixturesDir = Path.Combine(tempDir, "fixtures");
                var (wasm1, metaPath, _) = CreateSyntheticFixtures(fixturesDir, "sample1.wasm");
                var wasm2 = Path.Combine(fixturesDir, "sample2.wasm");
                File.Copy(wasm1, wasm2, true);

                var badBinPath = Path.Combine(fixturesDir, "bad.bin");
                File.WriteAllBytes(badBinPath, Encoding.ASCII.GetBytes("BAD!"));

                var outDir1 = Path.Combine(tempDir, "out1");
                var outDirBad = Path.Combine(tempDir, "out_bad");
                var outDir2 = Path.Combine(tempDir, "out2");

                var batchJsonPath = Path.Combine(tempDir, "batch.json");
                var batchEntries = new[]
                {
                    new { executable = wasm1, metadata = metaPath, output = outDir1 },
                    new { executable = badBinPath, metadata = metaPath, output = outDirBad },
                    new { executable = wasm2, metadata = metaPath, output = outDir2 }
                };
                File.WriteAllText(batchJsonPath, JsonSerializer.Serialize(batchEntries, new JsonSerializerOptions { WriteIndented = true }));

                // Run via CLI
                var (exitCode, stdout, stderr) = RunCli(appDir, "--batch", batchJsonPath);

                // Partial failure exits 1
                Assert.Equal(1, exitCode);

                // Both valid output sets exist and completed
                Assert.True(File.Exists(Path.Combine(outDir1, "dump.cs")));
                Assert.True(File.Exists(Path.Combine(outDir2, "dump.cs")));

                // Aggregate result file exists
                var resultJsonPath = Path.Combine(tempDir, "batch-result.json");
                Assert.True(File.Exists(resultJsonPath), $"Expected batch-result.json at {resultJsonPath}");

                var resultEntries = JsonSerializer.Deserialize<List<BatchResultEntry>>(File.ReadAllText(resultJsonPath), BatchProcessor.JsonOptions);
                Assert.NotNull(resultEntries);
                Assert.Equal(3, resultEntries.Count);

                // Entry 0: success
                Assert.Equal(0, resultEntries[0].Index);
                Assert.Equal(wasm1, resultEntries[0].Executable);
                Assert.True(resultEntries[0].Success);
                Assert.Null(resultEntries[0].Error);
                Assert.NotEmpty(resultEntries[0].Outputs);
                Assert.Contains(resultEntries[0].Outputs, p => p.EndsWith("dump.cs"));

                // Entry 1: failure with single-run error text
                Assert.Equal(1, resultEntries[1].Index);
                Assert.Equal(badBinPath, resultEntries[1].Executable);
                Assert.False(resultEntries[1].Success);
                Assert.NotNull(resultEntries[1].Error);
                Assert.Contains("ERROR: il2cpp file not supported.", resultEntries[1].Error);
                Assert.Empty(resultEntries[1].Outputs);

                // Entry 2: success
                Assert.Equal(2, resultEntries[2].Index);
                Assert.Equal(wasm2, resultEntries[2].Executable);
                Assert.True(resultEntries[2].Success);
                Assert.Null(resultEntries[2].Error);
                Assert.NotEmpty(resultEntries[2].Outputs);
                Assert.Contains(resultEntries[2].Outputs, p => p.EndsWith("dump.cs"));
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
        public void Batch_StopOnFirstError_True_HaltsImmediatelyAndAssertsNoSideEffectsOnSubsequentEntries()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "batch-stop-first-" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                var appDir = SetupIsolatedApp(tempDir);
                var fixturesDir = Path.Combine(tempDir, "fixtures");
                var (wasm1, metaPath, _) = CreateSyntheticFixtures(fixturesDir, "sample1.wasm");
                var wasm2 = Path.Combine(fixturesDir, "sample2.wasm");
                File.Copy(wasm1, wasm2, true);

                var badBinPath = Path.Combine(fixturesDir, "bad.bin");
                File.WriteAllBytes(badBinPath, Encoding.ASCII.GetBytes("BAD!"));

                var outDir1 = Path.Combine(tempDir, "out1");
                var outDirBad = Path.Combine(tempDir, "out_bad");
                var outDir2 = Path.Combine(tempDir, "out2_unattempted");

                var batchJsonPath = Path.Combine(tempDir, "batch.json");
                var batchEntries = new[]
                {
                    new { executable = wasm1, metadata = metaPath, output = outDir1 },
                    new { executable = badBinPath, metadata = metaPath, output = outDirBad },
                    new { executable = wasm2, metadata = metaPath, output = outDir2 }
                };
                File.WriteAllText(batchJsonPath, JsonSerializer.Serialize(batchEntries, new JsonSerializerOptions { WriteIndented = true }));

                // Run via CLI with --stop-on-first-error
                var (exitCode, stdout, stderr) = RunCli(appDir, "--batch", batchJsonPath, "--stop-on-first-error");

                Assert.Equal(1, exitCode);

                // Entry 0 succeeded
                Assert.True(File.Exists(Path.Combine(outDir1, "dump.cs")));

                // Entry 2 was not attempted: directory must not exist or be empty
                Assert.False(Directory.Exists(outDir2) && Directory.EnumerateFileSystemEntries(outDir2).Any(),
                    "Subsequent entry output directory had side effects after StopOnFirstError");

                // Aggregate result records only attempted entries
                var resultJsonPath = Path.Combine(tempDir, "batch-result.json");
                Assert.True(File.Exists(resultJsonPath));
                var resultEntries = JsonSerializer.Deserialize<List<BatchResultEntry>>(File.ReadAllText(resultJsonPath), BatchProcessor.JsonOptions);
                Assert.NotNull(resultEntries);
                Assert.Equal(2, resultEntries.Count);
                Assert.True(resultEntries[0].Success);
                Assert.False(resultEntries[1].Success);
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
        public void Batch_AllSuccess_ExitsZero()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "batch-all-success-" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                var appDir = SetupIsolatedApp(tempDir);
                var fixturesDir = Path.Combine(tempDir, "fixtures");
                var (wasm1, metaPath, _) = CreateSyntheticFixtures(fixturesDir, "sample1.wasm");
                var wasm2 = Path.Combine(fixturesDir, "sample2.wasm");
                File.Copy(wasm1, wasm2, true);

                var outDir1 = Path.Combine(tempDir, "out1");
                var outDir2 = Path.Combine(tempDir, "out2");

                var batchJsonPath = Path.Combine(tempDir, "batch.json");
                var batchEntries = new[]
                {
                    new { executable = wasm1, metadata = metaPath, output = outDir1 },
                    new { executable = wasm2, metadata = metaPath, output = outDir2 }
                };
                File.WriteAllText(batchJsonPath, JsonSerializer.Serialize(batchEntries, new JsonSerializerOptions { WriteIndented = true }));

                var (exitCode, stdout, stderr) = RunCli(appDir, "--batch", batchJsonPath);

                Assert.Equal(0, exitCode);
                Assert.True(File.Exists(Path.Combine(outDir1, "dump.cs")));
                Assert.True(File.Exists(Path.Combine(outDir2, "dump.cs")));

                var resultJsonPath = Path.Combine(tempDir, "batch-result.json");
                Assert.True(File.Exists(resultJsonPath));
                var resultEntries = JsonSerializer.Deserialize<List<BatchResultEntry>>(File.ReadAllText(resultJsonPath), BatchProcessor.JsonOptions);
                Assert.NotNull(resultEntries);
                Assert.Equal(2, resultEntries.Count);
                Assert.All(resultEntries, r => Assert.True(r.Success));
                Assert.All(resultEntries, r => Assert.Null(r.Error));
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
        public void Batch_MalformedBatchFile_NonExistentFile_ExitsTwoWithOffendingPathInStderr()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "batch-missing-file-" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                var appDir = SetupIsolatedApp(tempDir);
                var nonExistentPath = Path.Combine(tempDir, "does-not-exist.json");

                var (exitCode, stdout, stderr) = RunCli(appDir, "--batch", nonExistentPath);

                Assert.Equal(2, exitCode);
                Assert.Contains(nonExistentPath, stderr);
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
        public void Batch_MalformedBatchFile_InvalidJsonSyntax_ExitsTwoWithOffendingPathInStderr()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "batch-syntax-error-" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                var appDir = SetupIsolatedApp(tempDir);
                var malformedPath = Path.Combine(tempDir, "broken.json");
                File.WriteAllText(malformedPath, "{ this is not valid json");

                var (exitCode, stdout, stderr) = RunCli(appDir, "--batch", malformedPath);

                Assert.Equal(2, exitCode);
                Assert.Contains(malformedPath, stderr);
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
        public void Batch_MalformedBatchFile_NotAnArray_ExitsTwoWithOffendingPathInStderr()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "batch-not-array-" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                var appDir = SetupIsolatedApp(tempDir);
                var notArrayPath = Path.Combine(tempDir, "not_array.json");
                File.WriteAllText(notArrayPath, "{\"executable\": \"a\", \"metadata\": \"b\", \"output\": \"c\"}");

                var (exitCode, stdout, stderr) = RunCli(appDir, "--batch", notArrayPath);

                Assert.Equal(2, exitCode);
                Assert.Contains(notArrayPath, stderr);
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
        public void Batch_MalformedBatchFile_MissingRequiredFields_ExitsTwoWithOffendingPathInStderr()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "batch-missing-field-" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                var appDir = SetupIsolatedApp(tempDir);
                var missingFieldPath = Path.Combine(tempDir, "missing_field.json");
                File.WriteAllText(missingFieldPath, "[{\"executable\": \"a\", \"metadata\": \"\"}]");

                var (exitCode, stdout, stderr) = RunCli(appDir, "--batch", missingFieldPath);

                Assert.Equal(2, exitCode);
                Assert.Contains(missingFieldPath, stderr);
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
        public void Batch_DuplicateOutputDirectories_RejectedUpFront_ExitsTwoWithOffendingPathInStderr()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "batch-duplicate-out-" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                var appDir = SetupIsolatedApp(tempDir);
                var fixturesDir = Path.Combine(tempDir, "fixtures");
                var (wasm1, metaPath, _) = CreateSyntheticFixtures(fixturesDir);

                var duplicateOut = Path.Combine(tempDir, "out_shared");
                var batchJsonPath = Path.Combine(tempDir, "batch.json");
                var batchEntries = new[]
                {
                    new { executable = wasm1, metadata = metaPath, output = duplicateOut },
                    new { executable = wasm1, metadata = metaPath, output = duplicateOut }
                };
                File.WriteAllText(batchJsonPath, JsonSerializer.Serialize(batchEntries, new JsonSerializerOptions { WriteIndented = true }));

                var (exitCode, stdout, stderr) = RunCli(appDir, "--batch", batchJsonPath);

                Assert.Equal(2, exitCode);
                Assert.Contains("Duplicate output directory", stderr);
                Assert.Contains(duplicateOut, stderr);

                // Asserts rejected up front: no dump files were written
                Assert.False(Directory.Exists(duplicateOut) && Directory.EnumerateFileSystemEntries(duplicateOut).Any());
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
        public void Batch_OutputIsolation_EntryNDirectoryNeverReceivesEntryMFiles()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "batch-isolation-" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                var appDir = SetupIsolatedApp(tempDir);
                var fixturesDir = Path.Combine(tempDir, "fixtures");
                var (wasm1, metaPath, _) = CreateSyntheticFixtures(fixturesDir, "sample1.wasm");
                var wasm2 = Path.Combine(fixturesDir, "sample2.wasm");
                File.Copy(wasm1, wasm2, true);

                var outDir1 = Path.Combine(tempDir, "out_n");
                var outDir2 = Path.Combine(tempDir, "out_m");

                var batchJsonPath = Path.Combine(tempDir, "batch.json");
                var batchEntries = new[]
                {
                    new { executable = wasm1, metadata = metaPath, output = outDir1 },
                    new { executable = wasm2, metadata = metaPath, output = outDir2 }
                };
                File.WriteAllText(batchJsonPath, JsonSerializer.Serialize(batchEntries, new JsonSerializerOptions { WriteIndented = true }));

                var (exitCode, _, _) = RunCli(appDir, "--batch", batchJsonPath);
                Assert.Equal(0, exitCode);

                // Check sidecar input sha256 to prove isolation
                var sidecar1 = Path.Combine(outDir1, "dump-metadata.json");
                var sidecar2 = Path.Combine(outDir2, "dump-metadata.json");
                Assert.True(File.Exists(sidecar1));
                Assert.True(File.Exists(sidecar2));

                var meta1 = JsonSerializer.Deserialize<DumpMetadata>(File.ReadAllText(sidecar1));
                var meta2 = JsonSerializer.Deserialize<DumpMetadata>(File.ReadAllText(sidecar2));

                Assert.NotNull(meta1);
                Assert.NotNull(meta2);
                Assert.Equal(wasm1, meta1.Inputs.Il2Cpp.Path);
                Assert.Equal(wasm2, meta2.Inputs.Il2Cpp.Path);
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
        public void Batch_BatchResultJson_IsDeterministic_NoTimestampsAndPreservesOrder()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "batch-det-" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                var fixturesDir = Path.Combine(tempDir, "fixtures");
                var (wasm1, metaPath, _) = CreateSyntheticFixtures(fixturesDir, "sample1.wasm");
                var wasm2 = Path.Combine(fixturesDir, "sample2.wasm");
                File.Copy(wasm1, wasm2, true);

                var outDir1 = Path.Combine(tempDir, "out1");
                var outDir2 = Path.Combine(tempDir, "out2");

                var entries = new List<BatchEntry>
                {
                    new() { Executable = wasm1, Metadata = metaPath, Output = outDir1 },
                    new() { Executable = wasm2, Metadata = metaPath, Output = outDir2 }
                };

                var baseConfig = new Config { RequireAnyKey = false };

                // Run twice
                var result1 = BatchProcessor.ExecuteEntries(entries, baseConfig, tempDir, "batch.json");
                var json1 = JsonSerializer.Serialize(result1.Results, BatchProcessor.JsonOptions);

                // Clear outputs before run 2
                Directory.Delete(outDir1, true);
                Directory.Delete(outDir2, true);

                var result2 = BatchProcessor.ExecuteEntries(entries, baseConfig, tempDir, "batch.json");
                var json2 = JsonSerializer.Serialize(result2.Results, BatchProcessor.JsonOptions);

                // Byte-equal determinism
                Assert.Equal(json1, json2);

                // Assert no timestamps exist in the aggregate json
                Assert.DoesNotContain("timestamp", json1, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("startedAt", json1, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("duration", json1, StringComparison.OrdinalIgnoreCase);

                // Assert order preserved
                Assert.Equal(0, result1.Results[0].Index);
                Assert.Equal(1, result1.Results[1].Index);
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
        public void Batch_PerEntryConfigOverride_IsRespected()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "batch-cfg-override-" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                var fixturesDir = Path.Combine(tempDir, "fixtures");
                var (wasm1, metaPath, _) = CreateSyntheticFixtures(fixturesDir, "sample1.wasm");
                var wasm2 = Path.Combine(fixturesDir, "sample2.wasm");
                File.Copy(wasm1, wasm2, true);

                var outDir1 = Path.Combine(tempDir, "out1");
                var outDir2 = Path.Combine(tempDir, "out2");

                using var doc = JsonDocument.Parse("{\"GenerateDummyDll\": false}");
                var entries = new List<BatchEntry>
                {
                    new() { Executable = wasm1, Metadata = metaPath, Output = outDir1, Config = doc.RootElement.Clone() },
                    new() { Executable = wasm2, Metadata = metaPath, Output = outDir2 }
                };

                var baseConfig = new Config { RequireAnyKey = false, GenerateDummyDll = true };

                var result = BatchProcessor.ExecuteEntries(entries, baseConfig, tempDir, "batch.json");
                Assert.Equal(0, result.ExitCode);

                // Entry 0 should NOT have DummyDll
                Assert.False(Directory.Exists(Path.Combine(outDir1, "DummyDll")));

                // Entry 1 should have DummyDll
                Assert.True(Directory.Exists(Path.Combine(outDir2, "DummyDll")));
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
