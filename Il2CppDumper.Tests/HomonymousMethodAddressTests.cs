#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Il2CppDumper.Core;
using Il2CppDumper.Diff;
using Il2CppDumper.Tests.Synthetic;
using Xunit;

namespace Il2CppDumper.Tests
{
    public class HomonymousMethodAddressTests
    {
        private static void CreateFixtures(
            string dir,
            JsonDumpModel dumpModel,
            ScriptJson? script = null)
        {
            Directory.CreateDirectory(dir);
            var dumpJson = JsonSerializer.Serialize(dumpModel, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(dir, "dump.json"), dumpJson, new UTF8Encoding(false));

            if (script != null)
            {
                var scriptJson = JsonSerializer.Serialize(script, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true });
                File.WriteAllText(Path.Combine(dir, "script.json"), scriptJson, new UTF8Encoding(false));
            }
        }

        private static JsonDumpModel CreateTwoImagesModel()
        {
            return new JsonDumpModel
            {
                types = new List<JsonDumpType>
                {
                    new()
                    {
                        image = "A.dll",
                        assembly = "A",
                        fullName = "Ns.Player",
                        methods = new List<JsonDumpMethod>
                        {
                            new() { name = "Run", returnType = "void", parameters = new List<JsonDumpParameter>() }
                        }
                    },
                    new()
                    {
                        image = "B.dll",
                        assembly = "B",
                        fullName = "Ns.Player",
                        methods = new List<JsonDumpMethod>
                        {
                            new() { name = "Run", returnType = "void", parameters = new List<JsonDumpParameter>() }
                        }
                    }
                }
            };
        }

        [Fact]
        public void Issue90_ReproductionScenario_LegacyScript_AddressSwap_DetectsAmbiguityAndExits1()
        {
            // Exact scenario from Issue #90:
            // Two types with same fullName 'Ns.Player' in A.dll and B.dll
            // Left script: [4096 (0x1000), 8192 (0x2000)] without Image/Assembly
            // Right script: [8192 (0x2000), 4096 (0x1000)] without Image/Assembly
            // Old behavior: exit 0, addressChanges 0, no ambiguity (multiset equality masked the swap)
            // Expected behavior: exit 1, hasAmbiguousMethodAddresses true, explicit unresolved correlation note
            var tempDir = Path.Combine(Path.GetTempPath(), "issue90-swap-" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                var leftDir = Path.Combine(tempDir, "left");
                var rightDir = Path.Combine(tempDir, "right");
                var outPath = Path.Combine(tempDir, "diff.json");

                var model = CreateTwoImagesModel();
                var leftScript = new ScriptJson
                {
                    ScriptMethod = new List<ScriptMethod>
                    {
                        new() { Name = "Ns.Player$$Run", Signature = "void Run()", Address = 0x1000 },
                        new() { Name = "Ns.Player$$Run", Signature = "void Run()", Address = 0x2000 }
                    }
                };
                var rightScript = new ScriptJson
                {
                    ScriptMethod = new List<ScriptMethod>
                    {
                        new() { Name = "Ns.Player$$Run", Signature = "void Run()", Address = 0x2000 },
                        new() { Name = "Ns.Player$$Run", Signature = "void Run()", Address = 0x1000 }
                    }
                };

                CreateFixtures(leftDir, model, leftScript);
                CreateFixtures(rightDir, model, rightScript);

                var exitCode = DumpDiffer.Execute(leftDir, rightDir, outPath);

                Assert.Equal(1, exitCode);
                var diff = JsonSerializer.Deserialize<DumpDiffResult>(File.ReadAllText(outPath))!;

                Assert.True(diff.HasDifferences);
                Assert.True(diff.hasAmbiguousMethodAddresses);
                Assert.NotNull(diff.methods.note);
                Assert.Contains("Address comparison has unresolved ambiguous overloads: unable to correlate", diff.methods.note);
                Assert.Contains("void Run()", diff.methods.note);
                Assert.Equal(0, diff.summary.addressChanges);
                Assert.Empty(diff.methods.addressChanged!);
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
        public void Issue90_ControlScenario_LegacyScript_OneAddressChange_Exits1()
        {
            // Control scenario from Issue #90:
            // Left: [4096 (0x1000), 8192 (0x2000)]
            // Control: [4096 (0x1000), 12288 (0x3000)]
            // One address matches, one changes (0x2000 -> 0x3000)
            var tempDir = Path.Combine(Path.GetTempPath(), "issue90-control-" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                var leftDir = Path.Combine(tempDir, "left");
                var rightDir = Path.Combine(tempDir, "control");
                var outPath = Path.Combine(tempDir, "diff.json");

                var model = CreateTwoImagesModel();
                var leftScript = new ScriptJson
                {
                    ScriptMethod = new List<ScriptMethod>
                    {
                        new() { Name = "Ns.Player$$Run", Signature = "void Run()", Address = 0x1000 },
                        new() { Name = "Ns.Player$$Run", Signature = "void Run()", Address = 0x2000 }
                    }
                };
                var rightScript = new ScriptJson
                {
                    ScriptMethod = new List<ScriptMethod>
                    {
                        new() { Name = "Ns.Player$$Run", Signature = "void Run()", Address = 0x1000 },
                        new() { Name = "Ns.Player$$Run", Signature = "void Run()", Address = 0x3000 }
                    }
                };

                CreateFixtures(leftDir, model, leftScript);
                CreateFixtures(rightDir, model, rightScript);

                var exitCode = DumpDiffer.Execute(leftDir, rightDir, outPath);

                Assert.Equal(1, exitCode);
                var diff = JsonSerializer.Deserialize<DumpDiffResult>(File.ReadAllText(outPath))!;

                Assert.True(diff.HasDifferences);
                Assert.Equal(1, diff.summary.addressChanges);
                Assert.NotNull(diff.methods.addressChanged);
                Assert.Single(diff.methods.addressChanged);
                Assert.Equal("0x2000", diff.methods.addressChanged[0].left);
                Assert.Equal("0x3000", diff.methods.addressChanged[0].right);
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
        public void Issue90_QualifiedScriptWithOwnership_AddressSwap_ReportsBothChangesWithAttribution()
        {
            // When script.json records preserve Image/Assembly ownership additively:
            // Both methods swap addresses between builds:
            // A.dll!Ns.Player.Run: 0x1000 -> 0x2000
            // B.dll!Ns.Player.Run: 0x2000 -> 0x1000
            // Must report BOTH changes independently with image and assembly attribution, exit 1, and no false ambiguity!
            var tempDir = Path.Combine(Path.GetTempPath(), "issue90-qualified-swap-" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                var leftDir = Path.Combine(tempDir, "left");
                var rightDir = Path.Combine(tempDir, "right");
                var outPath = Path.Combine(tempDir, "diff.json");

                var model = CreateTwoImagesModel();
                var leftScript = new ScriptJson
                {
                    ScriptMethod = new List<ScriptMethod>
                    {
                        new() { Name = "Ns.Player$$Run", Signature = "void Run()", Address = 0x1000, Image = "A.dll", Assembly = "A" },
                        new() { Name = "Ns.Player$$Run", Signature = "void Run()", Address = 0x2000, Image = "B.dll", Assembly = "B" }
                    }
                };
                var rightScript = new ScriptJson
                {
                    ScriptMethod = new List<ScriptMethod>
                    {
                        new() { Name = "Ns.Player$$Run", Signature = "void Run()", Address = 0x2000, Image = "A.dll", Assembly = "A" },
                        new() { Name = "Ns.Player$$Run", Signature = "void Run()", Address = 0x1000, Image = "B.dll", Assembly = "B" }
                    }
                };

                CreateFixtures(leftDir, model, leftScript);
                CreateFixtures(rightDir, model, rightScript);

                var exitCode = DumpDiffer.Execute(leftDir, rightDir, outPath);

                Assert.Equal(1, exitCode);
                var diff = JsonSerializer.Deserialize<DumpDiffResult>(File.ReadAllText(outPath))!;

                Assert.True(diff.HasDifferences);
                Assert.False(diff.hasAmbiguousMethodAddresses);
                Assert.Null(diff.methods.note);
                Assert.Equal(2, diff.summary.addressChanges);
                Assert.NotNull(diff.methods.addressChanged);
                Assert.Equal(2, diff.methods.addressChanged.Count);

                var changeA = diff.methods.addressChanged.FirstOrDefault(c => c.type == "A.dll!Ns.Player");
                Assert.NotNull(changeA);
                Assert.Equal("A.dll", changeA!.image);
                Assert.Equal("A", changeA.assembly);
                Assert.Equal("Run", changeA.method);
                Assert.Equal("0x1000", changeA.left);
                Assert.Equal("0x2000", changeA.right);

                var changeB = diff.methods.addressChanged.FirstOrDefault(c => c.type == "B.dll!Ns.Player");
                Assert.NotNull(changeB);
                Assert.Equal("B.dll", changeB!.image);
                Assert.Equal("B", changeB.assembly);
                Assert.Equal("Run", changeB.method);
                Assert.Equal("0x2000", changeB.left);
                Assert.Equal("0x1000", changeB.right);
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
        public void Issue90_SharedNativeImplementation_IdenticalAddresses_TreatedAsUnchangedWithoutFalseAmbiguity()
        {
            // Requirement 4: shared native implementation alone is not a bug.
            // Two homonymous methods or generic instantiations share the exact same address 0x1000 on left and right.
            // Even under permutation, every method remains at 0x1000. Must exit 0 without false ambiguity.
            var tempDir = Path.Combine(Path.GetTempPath(), "issue90-shared-addr-" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                var leftDir = Path.Combine(tempDir, "left");
                var rightDir = Path.Combine(tempDir, "right");
                var outPath = Path.Combine(tempDir, "diff.json");

                var model = CreateTwoImagesModel();
                var leftScript = new ScriptJson
                {
                    ScriptMethod = new List<ScriptMethod>
                    {
                        new() { Name = "Ns.Player$$Run", Signature = "void Run()", Address = 0x1000 },
                        new() { Name = "Ns.Player$$Run", Signature = "void Run()", Address = 0x1000 }
                    }
                };
                var rightScript = new ScriptJson
                {
                    ScriptMethod = new List<ScriptMethod>
                    {
                        new() { Name = "Ns.Player$$Run", Signature = "void Run()", Address = 0x1000 },
                        new() { Name = "Ns.Player$$Run", Signature = "void Run()", Address = 0x1000 }
                    }
                };

                CreateFixtures(leftDir, model, leftScript);
                CreateFixtures(rightDir, model, rightScript);

                var exitCode = DumpDiffer.Execute(leftDir, rightDir, outPath);

                Assert.Equal(0, exitCode);
                var diff = JsonSerializer.Deserialize<DumpDiffResult>(File.ReadAllText(outPath))!;

                Assert.False(diff.HasDifferences);
                Assert.False(diff.hasAmbiguousMethodAddresses);
                Assert.Null(diff.methods.note);
                Assert.Equal(0, diff.summary.addressChanges);
                Assert.Empty(diff.methods.addressChanged!);
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
        public void StructGenerator_GeneratesScriptJsonWithImageAndAssemblyAttribution()
        {
            // Requirement 1: Preserve image/assembly ownership additively in method address exports
            // Verify StructGenerator outputs Image and Assembly in script.json
            var (metadata, il2Cpp, executor) = SyntheticEnvironment.Create(MetadataVersionFamily.V29);
            using (metadata)
            using (il2Cpp)
            {
                foreach (var imageDef in metadata.imageDefs)
                {
                    var imageName = metadata.GetStringFromIndex(imageDef.nameIndex);
                    il2Cpp.codeGenModuleMethodPointers[imageName] = Enumerable.Repeat(0x1000ul, metadata.methodDefs.Length + 10).ToArray();
                }

                var tempDir = Path.Combine(Path.GetTempPath(), "struct-gen-ownership-" + Guid.NewGuid()) + Path.DirectorySeparatorChar;
                Directory.CreateDirectory(tempDir);
                try
                {
                    var generator = new StructGenerator(executor);
                    generator.WriteScript(tempDir);

                    var scriptJsonPath = Path.Combine(tempDir, "script.json");
                    Assert.True(File.Exists(scriptJsonPath), "script.json should be generated.");

                    var text = File.ReadAllText(scriptJsonPath);
                    using var doc = JsonDocument.Parse(text);
                    Assert.True(doc.RootElement.TryGetProperty("ScriptMethod", out var methods));

                    var array = methods.EnumerateArray().ToList();
                    Assert.NotEmpty(array);

                    foreach (var item in array)
                    {
                        Assert.True(item.TryGetProperty("Image", out var imgEl), "ScriptMethod must have Image property.");
                        Assert.False(string.IsNullOrEmpty(imgEl.GetString()), "Image property must not be empty.");

                        Assert.True(item.TryGetProperty("Assembly", out var asmEl), "ScriptMethod must have Assembly property.");
                        Assert.False(string.IsNullOrEmpty(asmEl.GetString()), "Assembly property must not be empty.");
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

        [Fact]
        public void UniqueType_LegacyScriptWithoutOwnership_AttributedFromDumpJsonUnambiguously()
        {
            // If a type name is unique across all images in dump.json, legacy script entries for that type
            // are safely attributed to that unique type's image/assembly without causing ambiguity.
            var tempDir = Path.Combine(Path.GetTempPath(), "unique-type-attr-" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                var leftDir = Path.Combine(tempDir, "left");
                var rightDir = Path.Combine(tempDir, "right");
                var outPath = Path.Combine(tempDir, "diff.json");

                var model = new JsonDumpModel
                {
                    types = new List<JsonDumpType>
                    {
                        new()
                        {
                            image = "GameAssembly.dll",
                            assembly = "GameAssembly",
                            fullName = "Game.UniquePlayer",
                            methods = new List<JsonDumpMethod>
                            {
                                new() { name = "Run", returnType = "void", parameters = new List<JsonDumpParameter>() }
                            }
                        }
                    }
                };

                var leftScript = new ScriptJson
                {
                    ScriptMethod = new List<ScriptMethod>
                    {
                        new() { Name = "Game.UniquePlayer$$Run", Signature = "void Run()", Address = 0x1000 }
                    }
                };
                var rightScript = new ScriptJson
                {
                    ScriptMethod = new List<ScriptMethod>
                    {
                        new() { Name = "Game.UniquePlayer$$Run", Signature = "void Run()", Address = 0x2000 }
                    }
                };

                CreateFixtures(leftDir, model, leftScript);
                CreateFixtures(rightDir, model, rightScript);

                var exitCode = DumpDiffer.Execute(leftDir, rightDir, outPath);

                Assert.Equal(1, exitCode);
                var diff = JsonSerializer.Deserialize<DumpDiffResult>(File.ReadAllText(outPath))!;

                Assert.True(diff.HasDifferences);
                Assert.False(diff.hasAmbiguousMethodAddresses);
                Assert.Null(diff.methods.note);
                Assert.Equal(1, diff.summary.addressChanges);

                Assert.NotNull(diff.methods.addressChanged);
                Assert.Single(diff.methods.addressChanged);
                var change = diff.methods.addressChanged[0];
                Assert.Equal("GameAssembly.dll!Game.UniquePlayer", change.type);
                Assert.Equal("GameAssembly.dll", change.image);
                Assert.Equal("GameAssembly", change.assembly);
                Assert.Equal("Run", change.method);
                Assert.Equal("0x1000", change.left);
                Assert.Equal("0x2000", change.right);
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
