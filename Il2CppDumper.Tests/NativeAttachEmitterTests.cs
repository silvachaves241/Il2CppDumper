#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Il2CppDumper.Core;
using Xunit;

namespace Il2CppDumper.Tests
{
    public class NativeAttachEmitterTests
    {
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

        private static byte[] CreateMinimalMetadata()
        {
            var bytes = new byte[1024];
            BitConverter.GetBytes(0xFAB11BAFu).CopyTo(bytes, 0); // magic
            BitConverter.GetBytes(29).CopyTo(bytes, 4); // version
            return bytes;
        }

        private static byte[] CreateSyntheticWasm()
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(new byte[] { 0x00, 0x61, 0x73, 0x6d, 0x01, 0x00, 0x00, 0x00 });
            w.Write(new byte[] { 0x05, 0x03, 0x01, 0x00, 0x01 });
            var payload = new List<byte>();
            payload.AddRange(new byte[] { 0x01, 0x00, 0x41, 0x00, 0x0b });
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

        private static ScriptJson CreateSampleScriptJson()
        {
            return new ScriptJson
            {
                ScriptMethod = new List<ScriptMethod>
                {
                    new()
                    {
                        Address = 0x1000,
                        Name = "MyGame.Player$$TakeDamage",
                        Signature = "void MyGame.Player$$TakeDamage (Player* __this, int damage, string source, const MethodInfo* method);"
                    },
                    new()
                    {
                        Address = 0x1080,
                        Name = "MyGame.Player$$Update",
                        Signature = "void MyGame.Player$$Update (Player* __this, const MethodInfo* method);"
                    },
                    new()
                    {
                        Address = 0x2000,
                        Name = "MyGame.NetworkManager$$SendPacket",
                        Signature = "bool MyGame.NetworkManager$$SendPacket (int packetId, const MethodInfo* method);"
                    }
                }
            };
        }

        private static JsonDumpModel CreateSampleDumpModel()
        {
            return new JsonDumpModel
            {
                types = new List<JsonDumpType>
                {
                    new()
                    {
                        fullName = "MyGame.Player",
                        methods = new List<JsonDumpMethod>
                        {
                            new()
                            {
                                name = "TakeDamage",
                                returnType = "void",
                                isStatic = false,
                                parameters = new List<JsonDumpParameter>
                                {
                                    new() { name = "damage", type = "int" },
                                    new() { name = "source", type = "string" }
                                }
                            },
                            new()
                            {
                                name = "Update",
                                returnType = "void",
                                isStatic = false,
                                parameters = new List<JsonDumpParameter>()
                            }
                        }
                    },
                    new()
                    {
                        fullName = "MyGame.NetworkManager",
                        methods = new List<JsonDumpMethod>
                        {
                            new()
                            {
                                name = "SendPacket",
                                returnType = "bool",
                                isStatic = true,
                                parameters = new List<JsonDumpParameter>
                                {
                                    new() { name = "packetId", type = "int" }
                                }
                            }
                        }
                    }
                }
            };
        }

        [Fact]
        public void GoldenFile_DeterministicByteEqualEmittedJs_AndNoEvalOrFetch()
        {
            var script = CreateSampleScriptJson();
            var dump = CreateSampleDumpModel();

            var js1 = FridaScriptGenerator.Generate(script, dump, "GameAssembly.dll");
            var js2 = FridaScriptGenerator.Generate(script, dump, "GameAssembly.dll");

            // 1. Assert deterministic byte-equal output across consecutive runs
            Assert.Equal(js1, js2);

            // 2. Assert header comments
            Assert.StartsWith("// frida_stubs.js (generated, static)\n// module: GameAssembly.dll", js1);

            // 3. Assert methods are sorted deterministically (Ordinal)
            var netIdx = js1.IndexOf("\"MyGame.NetworkManager.SendPacket\"", StringComparison.Ordinal);
            var takeDamageIdx = js1.IndexOf("\"MyGame.Player.TakeDamage\"", StringComparison.Ordinal);
            var updateIdx = js1.IndexOf("\"MyGame.Player.Update\"", StringComparison.Ordinal);

            Assert.True(netIdx >= 0 && takeDamageIdx >= 0 && updateIdx >= 0);
            Assert.True(netIdx < takeDamageIdx, "MyGame.NetworkManager must precede MyGame.Player");
            Assert.True(takeDamageIdx < updateIdx, "TakeDamage must precede Update");

            // 4. Assert signatures match structured dump
            Assert.Contains("\"sig\": \"bool (int)\"", js1);
            Assert.Contains("\"sig\": \"void (Player*, int, string)\"", js1);
            Assert.Contains("\"sig\": \"void (Player*)\"", js1);

            // 5. Assert attach helper function
            Assert.Contains("function attach(name, callbacks) {", js1);
            Assert.Contains("Process.findModuleByName(\"GameAssembly.dll\")", js1);
            Assert.Contains("Interceptor.attach(mod.base.add(ptr(method.va)), callbacks)", js1);

            // 6. Assert emitted JS is strictly static: scan for forbidden dynamic / network mechanisms
            Assert.DoesNotContain("eval", js1, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("fetch", js1, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("XMLHttpRequest", js1, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("WebSocket", js1, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("http://", js1, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("https://", js1, StringComparison.OrdinalIgnoreCase);

            // 7. Assert absence of timestamps, GUIDs, or nondeterministic content
            Assert.DoesNotContain("DateTime", js1);
            Assert.DoesNotContain("timestamp", js1, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void AddressFidelity_ParseBackMatchesInputForEveryEntry()
        {
            var script = new ScriptJson
            {
                ScriptMethod = new List<ScriptMethod>
                {
                    new() { Address = 0x1000, Name = "A.B$$M1" },
                    new() { Address = 0x2A50, Name = "A.B$$M2" },
                    new() { Address = 0x80000000UL, Name = "A.B$$M3" },
                    new() { Address = 0xDEADBEEFUL, Name = "C.D$$M4" },
                    new() { Address = 0x400, Name = "C.D$$M5" }
                }
            };

            // Test bookmarks.json parse-back
            var bookmarksJson = BookmarksGenerator.Generate(script);
            var entries = JsonSerializer.Deserialize<List<BookmarkEntry>>(bookmarksJson);
            Assert.NotNull(entries);
            Assert.Equal(script.ScriptMethod.Count, entries.Count);

            foreach (var m in script.ScriptMethod)
            {
                var normalized = FridaScriptGenerator.NormalizeMethodName(m.Name);
                var match = entries.FirstOrDefault(e => e.name == normalized);
                Assert.NotNull(match);
                Assert.Equal("method", match.category);

                var parsedAddress = Convert.ToUInt64(match.address, 16);
                Assert.Equal(m.Address, parsedAddress);
            }

            // Test frida_stubs.js parse-back
            var fridaJs = FridaScriptGenerator.Generate(script, null, "GameAssembly.dll");
            foreach (var m in script.ScriptMethod)
            {
                var normalized = FridaScriptGenerator.NormalizeMethodName(m.Name);
                var regex = new Regex($@"""{Regex.Escape(normalized)}"":\s*\{{\s*""va"":\s*""(0x[0-9A-Fa-f]+)""");
                var match = regex.Match(fridaJs);
                Assert.True(match.Success, $"Expected entry for {normalized} in frida_stubs.js");

                var parsedVa = Convert.ToUInt64(match.Groups[1].Value, 16);
                Assert.Equal(m.Address, parsedVa);
            }
        }

        [Fact]
        public void DegradedPath_StructuredDumpAbsent_SignaturesOmitted_NoCrash()
        {
            var script = CreateSampleScriptJson();

            // SUT: dumpModel is null (degraded path)
            var js = FridaScriptGenerator.Generate(script, dumpModel: null, "libil2cpp.so");

            Assert.NotNull(js);
            Assert.Contains("// module: libil2cpp.so", js);
            Assert.Contains("Process.findModuleByName(\"libil2cpp.so\")", js);

            // Assert stubs still generated
            Assert.Contains("\"MyGame.Player.TakeDamage\": { \"va\": \"0x1000\" }", js);
            Assert.Contains("\"MyGame.Player.Update\": { \"va\": \"0x1080\" }", js);
            Assert.Contains("\"MyGame.NetworkManager.SendPacket\": { \"va\": \"0x2000\" }", js);

            // Assert signature comments and "sig" properties are completely omitted
            Assert.DoesNotContain("\"sig\"", js);
            Assert.DoesNotContain("// sig", js);
            Assert.DoesNotContain("/* sig", js);
        }

        [Fact]
        public void Defaults_BothFlagsFalse_ZeroNewFilesEmitted()
        {
            var cfg = new Config();
            Assert.False(cfg.EmitFridaScript);
            Assert.False(cfg.EmitBookmarks);

            var tempDir = Path.Combine(Path.GetTempPath(), "emitter_defaults_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var fixturesDir = Path.Combine(tempDir, "fixtures");
                var outDir = Path.Combine(tempDir, "out");
                Directory.CreateDirectory(fixturesDir);
                Directory.CreateDirectory(outDir);

                var (wasmPath, metaPath) = CreateSyntheticFixtures(fixturesDir);

                var request = new DumpRequest
                {
                    ExecutablePath = wasmPath,
                    MetadataPath = metaPath,
                    OutputDirectory = outDir,
                    Config = new Config
                    {
                        GenerateRunManifest = true,
                        GenerateStruct = true,
                        GenerateDummyDll = false,
                        EmitFridaScript = false,
                        EmitBookmarks = false
                    }
                };

                var result = Dumper.Dump(request);
                Assert.True(result.Success, result.Error);

                // Zero new files emitted for native attach emitters
                Assert.False(File.Exists(Path.Combine(outDir, "frida_stubs.js")));
                Assert.False(File.Exists(Path.Combine(outDir, "bookmarks.json")));
                Assert.False(result.Outputs.ContainsKey("frida_stubs.js"));
                Assert.False(result.Outputs.ContainsKey("bookmarks.json"));
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public void EndToEnd_BothFlagsTrue_GeneratesFiles_AndManifestContainsHashes()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "emitter_e2e_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var fixturesDir = Path.Combine(tempDir, "fixtures");
                var outDir = Path.Combine(tempDir, "out");
                Directory.CreateDirectory(fixturesDir);
                Directory.CreateDirectory(outDir);

                var (wasmPath, metaPath) = CreateSyntheticFixtures(fixturesDir);

                var request = new DumpRequest
                {
                    ExecutablePath = wasmPath,
                    MetadataPath = metaPath,
                    OutputDirectory = outDir,
                    Config = new Config
                    {
                        GenerateRunManifest = true,
                        GenerateStruct = true,
                        GenerateJsonDump = true,
                        GenerateDummyDll = false,
                        EmitFridaScript = true,
                        EmitBookmarks = true
                    }
                };

                var result = Dumper.Dump(request);
                Assert.True(result.Success, result.Error);

                var fridaPath = Path.Combine(outDir, "frida_stubs.js");
                var bookmarksPath = Path.Combine(outDir, "bookmarks.json");
                var manifestPath = Path.Combine(outDir, "run-manifest.json");

                Assert.True(File.Exists(fridaPath), "frida_stubs.js should exist");
                Assert.True(File.Exists(bookmarksPath), "bookmarks.json should exist");
                Assert.True(File.Exists(manifestPath), "run-manifest.json should exist");

                Assert.True(result.Outputs.ContainsKey("frida_stubs.js"));
                Assert.True(result.Outputs.ContainsKey("bookmarks.json"));

                // Verify run-manifest.json tracks both outputs with valid checksums
                var manifestJson = File.ReadAllText(manifestPath);
                using var doc = JsonDocument.Parse(manifestJson);
                var root = doc.RootElement;
                var outputs = root.GetProperty("outputs").EnumerateArray().ToList();

                var fridaOutput = outputs.FirstOrDefault(o => o.GetProperty("path").GetString() == "frida_stubs.js");
                var bookmarksOutput = outputs.FirstOrDefault(o => o.GetProperty("path").GetString() == "bookmarks.json");

                Assert.True(fridaOutput.ValueKind != JsonValueKind.Undefined, "frida_stubs.js must be in manifest outputs");
                Assert.True(bookmarksOutput.ValueKind != JsonValueKind.Undefined, "bookmarks.json must be in manifest outputs");

                Assert.False(string.IsNullOrEmpty(fridaOutput.GetProperty("sha256").GetString()));
                Assert.True(fridaOutput.GetProperty("bytes").GetInt64() > 0);

                Assert.False(string.IsNullOrEmpty(bookmarksOutput.GetProperty("sha256").GetString()));
                Assert.True(bookmarksOutput.GetProperty("bytes").GetInt64() > 0);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public void BookmarksGenerator_SortsDeterministically()
        {
            var script = new ScriptJson
            {
                ScriptMethod = new List<ScriptMethod>
                {
                    new() { Address = 0x200, Name = "Z.Type$$Action" },
                    new() { Address = 0x100, Name = "A.Type$$Action" },
                    new() { Address = 0x150, Name = "A.Type$$Action" },
                    new() { Address = 0x50, Name = "M.Type$$Action" }
                }
            };

            var entries = BookmarksGenerator.CreateEntries(script);

            Assert.Equal("A.Type.Action", entries[0].name);
            Assert.Equal("0x100", entries[0].address);

            Assert.Equal("A.Type.Action", entries[1].name);
            Assert.Equal("0x150", entries[1].address);

            Assert.Equal("M.Type.Action", entries[2].name);
            Assert.Equal("0x50", entries[2].address);

            Assert.Equal("Z.Type.Action", entries[3].name);
            Assert.Equal("0x200", entries[3].address);
        }

        [Fact]
        public void OverloadedMethods_DisambiguatesKeysInFridaScript_PreservesAllMethods()
        {
            var script = new ScriptJson
            {
                ScriptMethod = new List<ScriptMethod>
                {
                    new() { Address = 0x1000, Name = "Player$$Attack" },
                    new() { Address = 0x1050, Name = "Player$$Attack" }
                }
            };

            var js = FridaScriptGenerator.Generate(script, null, "GameAssembly.dll");

            Assert.Contains("\"Player.Attack\": { \"va\": \"0x1000\" }", js);
            Assert.Contains("\"Player.Attack_0x1050\": { \"va\": \"0x1050\" }", js);
        }

        private static (int exitCode, string stdout, string stderr) RunNode(params string[] args)
        {
            var psi = new System.Diagnostics.ProcessStartInfo("node")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (var arg in args)
            {
                psi.ArgumentList.Add(arg);
            }
            using var p = System.Diagnostics.Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to launch node process.");

            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();
            var exited = p.WaitForExit(30000);
            if (!exited)
            {
                try { p.Kill(); } catch { }
            }
            p.WaitForExit();

            var stdout = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();

            Assert.True(exited, $"Node process timed out after 30s. StdOut: {stdout}, StdErr: {stderr}");
            return (p.ExitCode, stdout, stderr);
        }

        [Fact]
        public void R1_Frida17Api_CompilesAndAttachesInNodeWithProcessFindModuleByName()
        {
            var script = CreateSampleScriptJson();
            var dump = CreateSampleDumpModel();
            var js = FridaScriptGenerator.Generate(script, dump, "GameAssembly.dll");

            // Verify API surface conforms strictly to Frida 17+
            Assert.DoesNotContain("Module.findBaseAddress", js);
            Assert.Contains("Process.findModuleByName(\"GameAssembly.dll\")", js);
            Assert.Contains("mod.base.add(ptr(method.va))", js);

            // Execute in Node.js with modeled Frida 17 runtime surface
            var tempJs = Path.Combine(Path.GetTempPath(), "frida17_test_" + Guid.NewGuid().ToString("N") + ".js");
            var harnessJs = js + @"

const mockProcess = {
    findModuleByName: (name) => {
        if (name === ""GameAssembly.dll"") {
            return {
                name: ""GameAssembly.dll"",
                base: {
                    add: (p) => ""0xBase_"" + p
                }
            };
        }
        return null;
    }
};

const mockInterceptor = {
    attach: (target, callbacks) => {
        return { attachedTo: target };
    }
};

globalThis.Process = mockProcess;
globalThis.Interceptor = mockInterceptor;
globalThis.ptr = (v) => v;

const res = attach(""MyGame.Player.TakeDamage"", {});
if (!res || res.attachedTo !== ""0xBase_0x1000"") {
    console.error(""Failed to attach properly:"", res);
    process.exit(1);
}

// Verify missing method error
try {
    attach(""NonExistent.Method"", {});
    process.exit(2);
} catch (e) {
    if (!e.message.includes(""Method not found: NonExistent.Method"")) {
        process.exit(3);
    }
}

console.log(""R1_SUCCESS"");
";
            File.WriteAllText(tempJs, harnessJs);
            try
            {
                var (exitCode, stdout, stderr) = RunNode(tempJs);
                Assert.True(exitCode == 0, $"Node script failed with exit code {exitCode}. StdErr: {stderr}");
                Assert.Contains("R1_SUCCESS", stdout);
            }
            finally
            {
                try { File.Delete(tempJs); } catch { }
            }
        }

        [Fact]
        public void R2_ReusedOutputDirectoryWithStaleJsonResidues_DoesNotConsumeStaleArtifacts()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "emitter_r2_stale_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var fixturesDir = Path.Combine(tempDir, "fixtures");
                var outDir = Path.Combine(tempDir, "out");
                Directory.CreateDirectory(fixturesDir);
                Directory.CreateDirectory(outDir);

                var (wasmPath, metaPath) = CreateSyntheticFixtures(fixturesDir);

                // Plant stale residues from a completely different previous run in outDir
                var staleScriptPath = Path.Combine(outDir, "script.json");
                File.WriteAllText(staleScriptPath, JsonSerializer.Serialize(new ScriptJson
                {
                    ScriptMethod = new List<ScriptMethod>
                    {
                        new() { Address = 0xDEADBEEF, Name = "Other.Target.Old$$OldMethod" }
                    }
                }, new JsonSerializerOptions { IncludeFields = true }));

                var staleDumpPath = Path.Combine(outDir, "dump.json");
                File.WriteAllText(staleDumpPath, JsonSerializer.Serialize(new JsonDumpModel
                {
                    types = new List<JsonDumpType>
                    {
                        new()
                        {
                            fullName = "Other.Target.Old",
                            methods = new List<JsonDumpMethod>
                            {
                                new() { name = "OldMethod", returnType = "void", isStatic = true }
                            }
                        }
                    }
                }));

                // Run with GenerateStruct=false, but EmitFridaScript=true and EmitBookmarks=true
                var request = new DumpRequest
                {
                    ExecutablePath = wasmPath,
                    MetadataPath = metaPath,
                    OutputDirectory = outDir,
                    Config = new Config
                    {
                        GenerateRunManifest = true,
                        GenerateStruct = false, // structs NOT generated in this run!
                        GenerateJsonDump = false,
                        GenerateDummyDll = false,
                        EmitFridaScript = true,
                        EmitBookmarks = true
                    }
                };

                var result = Dumper.Dump(request);
                Assert.True(result.Success, result.Error);

                // Stale files should NOT be consumed, so neither frida_stubs.js nor bookmarks.json should be created
                var fridaPath = Path.Combine(outDir, "frida_stubs.js");
                var bookmarksPath = Path.Combine(outDir, "bookmarks.json");
                Assert.False(File.Exists(fridaPath), "frida_stubs.js must not be created from stale residues");
                Assert.False(File.Exists(bookmarksPath), "bookmarks.json must not be created from stale residues");
                Assert.False(result.Outputs.ContainsKey("frida_stubs.js"));
                Assert.False(result.Outputs.ContainsKey("bookmarks.json"));

                // Explicit diagnostic warning should be logged
                Assert.Contains(result.Warnings, w => w.Contains("Native attach emitters skipped: script.json was not generated"));
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public void R3_OverloadedMethodsWithSameArityDifferentTypes_OmitsAmbiguousSignatures()
        {
            // Overloaded methods with same arity: Set(int) and Set(string)
            var script = new ScriptJson
            {
                ScriptMethod = new List<ScriptMethod>
                {
                    new() { Address = 0x1000, Name = "N.Player$$Set" },
                    new() { Address = 0x1050, Name = "N.Player$$Set" },
                    new() { Address = 0x2000, Name = "N.Player$$UniqueMethod" }
                }
            };

            var dump = new JsonDumpModel
            {
                types = new List<JsonDumpType>
                {
                    new()
                    {
                        fullName = "N.Player",
                        methods = new List<JsonDumpMethod>
                        {
                            new()
                            {
                                name = "Set",
                                returnType = "void",
                                isStatic = false,
                                parameters = new List<JsonDumpParameter> { new() { name = "val", type = "int" } }
                            },
                            new()
                            {
                                name = "Set",
                                returnType = "void",
                                isStatic = false,
                                parameters = new List<JsonDumpParameter> { new() { name = "val", type = "string" } }
                            },
                            new()
                            {
                                name = "UniqueMethod",
                                returnType = "int",
                                isStatic = false,
                                parameters = new List<JsonDumpParameter>()
                            }
                        }
                    }
                }
            };

            var js = FridaScriptGenerator.Generate(script, dump, "GameAssembly.dll");

            // Overloaded RVAs must be preserved with address keys
            Assert.Contains("\"N.Player.Set\": { \"va\": \"0x1000\" }", js);
            Assert.Contains("\"N.Player.Set_0x1050\": { \"va\": \"0x1050\" }", js);

            // Ambiguous overloads must NOT receive any sig (neither string nor int)
            var setMatch = Regex.Match(js, @"""N\.Player\.Set""\s*:\s*\{[^}]*\}");
            Assert.True(setMatch.Success);
            Assert.DoesNotContain("\"sig\"", setMatch.Value);

            var set2Match = Regex.Match(js, @"""N\.Player\.Set_0x1050""\s*:\s*\{[^}]*\}");
            Assert.True(set2Match.Success);
            Assert.DoesNotContain("\"sig\"", set2Match.Value);

            // Unambiguous method receives exact typing signature
            Assert.Contains("\"N.Player.UniqueMethod\": { \"va\": \"0x2000\", \"sig\": \"int (Player*)\" }", js);
        }

        [Fact]
        public void R4_ObfuscatedMethodNamesWithControlCharacters_SerializesValidJsAndPreservesCharacters()
        {
            var script = new ScriptJson
            {
                ScriptMethod = new List<ScriptMethod>
                {
                    new() { Address = 0x1000, Name = "N.Type$$Line\nBreak\t\"Quote\"" }
                }
            };

            var js = FridaScriptGenerator.Generate(script, null, "Game\nAssembly.dll");

            // Header comment should have newlines normalized to spaces
            Assert.Contains("// module: Game Assembly.dll;", js);

            // Generated JS should compile cleanly with node -c
            var tempJs = Path.Combine(Path.GetTempPath(), "r4_ctrl_" + Guid.NewGuid().ToString("N") + ".js");
            File.WriteAllText(tempJs, js);
            try
            {
                var (exitCode, _, stderr) = RunNode("-c", tempJs);
                Assert.True(exitCode == 0, $"Node syntax check failed with exit code {exitCode}. StdErr: {stderr}");
            }
            finally
            {
                try { File.Delete(tempJs); } catch { }
            }
        }
    }
}
