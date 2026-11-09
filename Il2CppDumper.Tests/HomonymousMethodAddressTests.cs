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
        public void Issue90_ControlScenario_LegacyScript_AddressChangeInAmbiguousGroup_PreservesAmbiguityWithoutFabricatedTransition()
        {
            // Reviewer Point 3: Canceling equal addresses in an ambiguous group must not fabricate a transition.
            // With homonymous methods in A and B:
            // Left: [0x1000, 0x2000]
            // Right: [0x1000, 0x3000] (or [0x3000, 0x1000])
            // In an unresolved group, address 0x1000 may belong to different methods across builds.
            // The tool must detect the set change, preserve ambiguity (hasAmbiguousMethodAddresses = true),
            // and reserve addressChanged for demonstrable pairs rather than fabricating 0x2000 -> 0x3000.
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
                Assert.True(diff.hasAmbiguousMethodAddresses);
                Assert.NotNull(diff.methods.note);
                Assert.Contains("Address comparison has unresolved ambiguous overloads: unable to correlate", diff.methods.note);
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
        public void Issue90_QualifiedScriptWithOwnership_AddressSwap_ReportsBothChangesWithAttribution()
        {
            // When script.json records preserve Image/Assembly ownership additively:
            // Both methods swap addresses between builds:
            // A.dll!Ns.Player.Run: 0x1000 -> 0x2000
            // B.dll!Ns.Player.Run: 0x2000 -> 0x1000
            // Must report BOTH changes independently with image and assembly attribution, exit 1, and no false ambiguity!
            // Also verifies Reviewer Point 5: public 'type' field emits simple type name "Ns.Player".
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

                var changeA = diff.methods.addressChanged.FirstOrDefault(c => c.image == "A.dll");
                Assert.NotNull(changeA);
                Assert.Equal("Ns.Player", changeA!.type); // Reviewer Point 5: Simple type name!
                Assert.Equal("A.dll", changeA.image);
                Assert.Equal("A", changeA.assembly);
                Assert.Equal("Run", changeA.method);
                Assert.Equal("0x1000", changeA.left);
                Assert.Equal("0x2000", changeA.right);

                var changeB = diff.methods.addressChanged.FirstOrDefault(c => c.image == "B.dll");
                Assert.NotNull(changeB);
                Assert.Equal("Ns.Player", changeB!.type); // Reviewer Point 5: Simple type name!
                Assert.Equal("B.dll", changeB.image);
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
        public void ReviewerP2_LegacyVsQualified_AddressDiff_BothOrders_DetectsAmbiguityAndExits1()
        {
            // Reviewer Point 1: Key intersection must not drop methods when comparing legacy with qualified map.
            // Left: [0x1000, 0x2000] without owners.
            // Right: A=0x1000, B=0x3000 with owners.
            // Both orders must return exit code 1, ambiguity true.
            var tempDir = Path.Combine(Path.GetTempPath(), "reviewer-legacy-vs-qualified-" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                var model = CreateTwoImagesModel();
                var legacyScript = new ScriptJson
                {
                    ScriptMethod = new List<ScriptMethod>
                    {
                        new() { Name = "Ns.Player$$Run", Signature = "void Run()", Address = 0x1000 },
                        new() { Name = "Ns.Player$$Run", Signature = "void Run()", Address = 0x2000 }
                    }
                };
                var qualifiedScript = new ScriptJson
                {
                    ScriptMethod = new List<ScriptMethod>
                    {
                        new() { Name = "Ns.Player$$Run", Signature = "void Run()", Address = 0x1000, Image = "A.dll", Assembly = "A" },
                        new() { Name = "Ns.Player$$Run", Signature = "void Run()", Address = 0x3000, Image = "B.dll", Assembly = "B" }
                    }
                };

                // Order 1: Legacy left, Qualified right
                var dir1 = Path.Combine(tempDir, "run1");
                var left1 = Path.Combine(dir1, "left");
                var right1 = Path.Combine(dir1, "right");
                var out1 = Path.Combine(dir1, "diff.json");
                CreateFixtures(left1, model, legacyScript);
                CreateFixtures(right1, model, qualifiedScript);

                var exit1 = DumpDiffer.Execute(left1, right1, out1);
                Assert.Equal(1, exit1);
                var diff1 = JsonSerializer.Deserialize<DumpDiffResult>(File.ReadAllText(out1))!;
                Assert.True(diff1.HasDifferences);
                Assert.True(diff1.hasAmbiguousMethodAddresses);

                // Order 2: Qualified left, Legacy right
                var dir2 = Path.Combine(tempDir, "run2");
                var left2 = Path.Combine(dir2, "left");
                var right2 = Path.Combine(dir2, "right");
                var out2 = Path.Combine(dir2, "diff.json");
                CreateFixtures(left2, model, qualifiedScript);
                CreateFixtures(right2, model, legacyScript);

                var exit2 = DumpDiffer.Execute(left2, right2, out2);
                Assert.Equal(1, exit2);
                var diff2 = JsonSerializer.Deserialize<DumpDiffResult>(File.ReadAllText(out2))!;
                Assert.True(diff2.HasDifferences);
                Assert.True(diff2.hasAmbiguousMethodAddresses);
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
        public void ReviewerP2_GenericInstance_BoxInt_WithGenericDefinitionBoxT_BothOrders_ReportsAddressChange()
        {
            // Reviewer Point 1: Single generic Ns.Box<int>$$Run going from 0x1000 to 0x2000,
            // with Ns.Box<T> in dump.json. Both orders must succeed and report the address change.
            var tempDir = Path.Combine(Path.GetTempPath(), "reviewer-generic-box-" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                var model = new JsonDumpModel
                {
                    types = new List<JsonDumpType>
                    {
                        new()
                        {
                            image = "A.dll",
                            assembly = "A",
                            fullName = "Ns.Box<T>",
                            methods = new List<JsonDumpMethod>
                            {
                                new() { name = "Run", returnType = "void", parameters = new List<JsonDumpParameter>() }
                            }
                        }
                    }
                };

                var script1 = new ScriptJson
                {
                    ScriptMethod = new List<ScriptMethod>
                    {
                        new() { Name = "Ns.Box<int>$$Run", Signature = "void Run()", Address = 0x1000, Image = "A.dll", Assembly = "A" }
                    }
                };

                // Legacy script lacking Image/Assembly on right
                var script2 = new ScriptJson
                {
                    ScriptMethod = new List<ScriptMethod>
                    {
                        new() { Name = "Ns.Box<int>$$Run", Signature = "void Run()", Address = 0x2000 }
                    }
                };

                // Order 1: Qualified left, Legacy right
                var dir1 = Path.Combine(tempDir, "run1");
                var left1 = Path.Combine(dir1, "left");
                var right1 = Path.Combine(dir1, "right");
                var out1 = Path.Combine(dir1, "diff.json");
                CreateFixtures(left1, model, script1);
                CreateFixtures(right1, model, script2);

                var exit1 = DumpDiffer.Execute(left1, right1, out1);
                Assert.Equal(1, exit1);
                var diff1 = JsonSerializer.Deserialize<DumpDiffResult>(File.ReadAllText(out1))!;
                Assert.NotNull(diff1.methods.addressChanged);
                Assert.Single(diff1.methods.addressChanged);
                Assert.Equal("Ns.Box<int>", diff1.methods.addressChanged[0].type);
                Assert.Equal("0x1000", diff1.methods.addressChanged[0].left);
                Assert.Equal("0x2000", diff1.methods.addressChanged[0].right);

                // Order 2: Legacy left, Qualified right
                var dir2 = Path.Combine(tempDir, "run2");
                var left2 = Path.Combine(dir2, "left");
                var right2 = Path.Combine(dir2, "right");
                var out2 = Path.Combine(dir2, "diff.json");
                CreateFixtures(left2, model, script2);
                CreateFixtures(right2, model, script1);

                var exit2 = DumpDiffer.Execute(left2, right2, out2);
                Assert.Equal(1, exit2);
                var diff2 = JsonSerializer.Deserialize<DumpDiffResult>(File.ReadAllText(out2))!;
                Assert.NotNull(diff2.methods.addressChanged);
                Assert.Single(diff2.methods.addressChanged);
                Assert.Equal("Ns.Box<int>", diff2.methods.addressChanged[0].type);
                Assert.Equal("0x2000", diff2.methods.addressChanged[0].left);
                Assert.Equal("0x1000", diff2.methods.addressChanged[0].right);
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
        public void ReviewerP2_ExplicitIncompatibleOwners_DoNotPairAsAddressChange()
        {
            // Reviewer Point 1: Explicit incompatible owners (e.g. Left in A.dll, Right in B.dll)
            // must NOT be paired as an address change.
            var tempDir = Path.Combine(Path.GetTempPath(), "reviewer-incompatible-owners-" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                var model = CreateTwoImagesModel();
                var leftScript = new ScriptJson
                {
                    ScriptMethod = new List<ScriptMethod>
                    {
                        new() { Name = "Ns.Player$$Run", Signature = "void Run()", Address = 0x1000, Image = "A.dll", Assembly = "A" }
                    }
                };
                var rightScript = new ScriptJson
                {
                    ScriptMethod = new List<ScriptMethod>
                    {
                        new() { Name = "Ns.Player$$Run", Signature = "void Run()", Address = 0x2000, Image = "B.dll", Assembly = "B" }
                    }
                };

                var leftDir = Path.Combine(tempDir, "left");
                var rightDir = Path.Combine(tempDir, "right");
                var outPath = Path.Combine(tempDir, "diff.json");
                CreateFixtures(leftDir, model, leftScript);
                CreateFixtures(rightDir, model, rightScript);

                var exitCode = DumpDiffer.Execute(leftDir, rightDir, outPath);
                var diff = JsonSerializer.Deserialize<DumpDiffResult>(File.ReadAllText(outPath))!;

                // Since left belongs to A.dll and right belongs to B.dll, they are incompatible owners.
                // No addressChanged should pair them together!
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
        public void ReviewerP2_OnlyAssembly_MultipleCandidates_PreservesAmbiguityWithoutFalseAttribution()
        {
            // Reviewer Point 2: When A.dll and B.dll share the same Assembly "Shared",
            // exporting only Assembly: "Shared" must NOT falsely attribute changes to an arbitrary image.
            // Multiple candidates must require ambiguity handling.
            var tempDir = Path.Combine(Path.GetTempPath(), "reviewer-only-assembly-" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                var model = new JsonDumpModel
                {
                    types = new List<JsonDumpType>
                    {
                        new()
                        {
                            image = "A.dll",
                            assembly = "Shared",
                            fullName = "Ns.Player",
                            methods = new List<JsonDumpMethod>
                            {
                                new() { name = "Run", returnType = "void", parameters = new List<JsonDumpParameter>() }
                            }
                        },
                        new()
                        {
                            image = "B.dll",
                            assembly = "Shared",
                            fullName = "Ns.Player",
                            methods = new List<JsonDumpMethod>
                            {
                                new() { name = "Run", returnType = "void", parameters = new List<JsonDumpParameter>() }
                            }
                        }
                    }
                };

                // Moving A from 0x1000 to 0x3000 while B stays at 0x2000, with ONLY Assembly: "Shared"
                var leftScript = new ScriptJson
                {
                    ScriptMethod = new List<ScriptMethod>
                    {
                        new() { Name = "Ns.Player$$Run", Signature = "void Run()", Address = 0x1000, Assembly = "Shared" },
                        new() { Name = "Ns.Player$$Run", Signature = "void Run()", Address = 0x2000, Assembly = "Shared" }
                    }
                };
                var rightScript = new ScriptJson
                {
                    ScriptMethod = new List<ScriptMethod>
                    {
                        new() { Name = "Ns.Player$$Run", Signature = "void Run()", Address = 0x3000, Assembly = "Shared" },
                        new() { Name = "Ns.Player$$Run", Signature = "void Run()", Address = 0x2000, Assembly = "Shared" }
                    }
                };

                var leftDir = Path.Combine(tempDir, "left");
                var rightDir = Path.Combine(tempDir, "right");
                var outPath = Path.Combine(tempDir, "diff.json");
                CreateFixtures(leftDir, model, leftScript);
                CreateFixtures(rightDir, model, rightScript);

                var exitCode = DumpDiffer.Execute(leftDir, rightDir, outPath);
                Assert.Equal(1, exitCode);

                var diff = JsonSerializer.Deserialize<DumpDiffResult>(File.ReadAllText(outPath))!;
                Assert.True(diff.hasAmbiguousMethodAddresses);
                // Must NOT attribute falsely to B.dll without ambiguity
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
            // Reviewer Point 5: Emits simple type name in 'type', with additive image and assembly.
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
                Assert.Equal("Game.UniquePlayer", change.type); // Reviewer Point 5: Simple type name!
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

        [Fact]
        public void ReviewerRound2_P2_GenericInstances_DistinctTypeArgs_BoxIntVsBoxString_DoNotPair()
        {
            // Review 2, Point 1: Distinct generic instantiations must not be treated as the same method.
            // With declaration Ns.Box<T> in A.dll, compare Ns.Box<int>$$Run at 0x1000 and Ns.Box<string>$$Run at 0x2000.
            // Must NOT emit Ns.Box<int>.Run 0x1000 -> 0x2000.
            var tempDir = Path.Combine(Path.GetTempPath(), "reviewer2-box-distinct-" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                var model = new JsonDumpModel
                {
                    types = new List<JsonDumpType>
                    {
                        new()
                        {
                            image = "A.dll",
                            assembly = "A",
                            fullName = "Ns.Box<T>",
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
                        new() { Name = "Ns.Box<int>$$Run", Signature = "void Run()", Address = 0x1000, Image = "A.dll", Assembly = "A" }
                    }
                };
                var rightScript = new ScriptJson
                {
                    ScriptMethod = new List<ScriptMethod>
                    {
                        new() { Name = "Ns.Box<string>$$Run", Signature = "void Run()", Address = 0x2000, Image = "A.dll", Assembly = "A" }
                    }
                };

                var leftDir = Path.Combine(tempDir, "left");
                var rightDir = Path.Combine(tempDir, "right");
                var outPath = Path.Combine(tempDir, "diff.json");
                CreateFixtures(leftDir, model, leftScript);
                CreateFixtures(rightDir, model, rightScript);

                var exitCode = DumpDiffer.Execute(leftDir, rightDir, outPath);
                var diff = JsonSerializer.Deserialize<DumpDiffResult>(File.ReadAllText(outPath))!;

                // Distinct concrete type arguments must not be paired as an address change
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
        public void ReviewerRound2_P2_GenericInstances_SameTypeArg_BoxInt_ReportsAddressChange()
        {
            // Review 2, Point 1: Same generic instantiation Ns.Box<int> with changed RVA must report the change.
            var tempDir = Path.Combine(Path.GetTempPath(), "reviewer2-box-same-" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                var model = new JsonDumpModel
                {
                    types = new List<JsonDumpType>
                    {
                        new()
                        {
                            image = "A.dll",
                            assembly = "A",
                            fullName = "Ns.Box<T>",
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
                        new() { Name = "Ns.Box<int>$$Run", Signature = "void Run()", Address = 0x1000, Image = "A.dll", Assembly = "A" }
                    }
                };
                var rightScript = new ScriptJson
                {
                    ScriptMethod = new List<ScriptMethod>
                    {
                        new() { Name = "Ns.Box<int>$$Run", Signature = "void Run()", Address = 0x2000, Image = "A.dll", Assembly = "A" }
                    }
                };

                var leftDir = Path.Combine(tempDir, "left");
                var rightDir = Path.Combine(tempDir, "right");
                var outPath = Path.Combine(tempDir, "diff.json");
                CreateFixtures(leftDir, model, leftScript);
                CreateFixtures(rightDir, model, rightScript);

                var exitCode = DumpDiffer.Execute(leftDir, rightDir, outPath);
                Assert.Equal(1, exitCode);

                var diff = JsonSerializer.Deserialize<DumpDiffResult>(File.ReadAllText(outPath))!;
                Assert.NotNull(diff.methods.addressChanged);
                Assert.Single(diff.methods.addressChanged);
                Assert.Equal("Ns.Box<int>", diff.methods.addressChanged[0].type);
                Assert.Equal("0x1000", diff.methods.addressChanged[0].left);
                Assert.Equal("0x2000", diff.methods.addressChanged[0].right);
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
        public void ReviewerRound2_P2_UnmatchedOverload_CarriedToPhase2_BothDirections()
        {
            // Review 2, Point 2: Premature marking in Phase 1 must not consume overloads that have no match in Phase 1.
            // In A.dll: Run() is qualified on both sides. Run(int) is legacy on left, qualified as A.dll on right.
            // RVA of Run(int) changes from 0x2000 to 0x3000.
            // Must NOT exit 0 with 0 changes!
            var tempDir = Path.Combine(Path.GetTempPath(), "reviewer2-unmatched-overload-" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                var model = CreateTwoImagesModel();

                var leftScript = new ScriptJson
                {
                    ScriptMethod = new List<ScriptMethod>
                    {
                        new() { Name = "Ns.Player$$Run", Signature = "void Run()", Address = 0x1000, Image = "A.dll", Assembly = "A" },
                        new() { Name = "Ns.Player$$Run", Signature = "void Run(int v)", Address = 0x2000 } // legacy / unparented
                    }
                };
                var rightScript = new ScriptJson
                {
                    ScriptMethod = new List<ScriptMethod>
                    {
                        new() { Name = "Ns.Player$$Run", Signature = "void Run()", Address = 0x1000, Image = "A.dll", Assembly = "A" },
                        new() { Name = "Ns.Player$$Run", Signature = "void Run(int v)", Address = 0x3000, Image = "A.dll", Assembly = "A" }
                    }
                };

                // Direction 1: Left has legacy Run(int), Right has qualified Run(int)
                var dir1 = Path.Combine(tempDir, "run1");
                var left1 = Path.Combine(dir1, "left");
                var right1 = Path.Combine(dir1, "right");
                var out1 = Path.Combine(dir1, "diff.json");
                CreateFixtures(left1, model, leftScript);
                CreateFixtures(right1, model, rightScript);

                var exit1 = DumpDiffer.Execute(left1, right1, out1);
                Assert.Equal(1, exit1);
                var diff1 = JsonSerializer.Deserialize<DumpDiffResult>(File.ReadAllText(out1))!;
                Assert.True(diff1.HasDifferences);

                // Direction 2: Inverted
                var dir2 = Path.Combine(tempDir, "run2");
                var left2 = Path.Combine(dir2, "left");
                var right2 = Path.Combine(dir2, "right");
                var out2 = Path.Combine(dir2, "diff.json");
                CreateFixtures(left2, model, rightScript);
                CreateFixtures(right2, model, leftScript);

                var exit2 = DumpDiffer.Execute(left2, right2, out2);
                Assert.Equal(1, exit2);
                var diff2 = JsonSerializer.Deserialize<DumpDiffResult>(File.ReadAllText(out2))!;
                Assert.True(diff2.HasDifferences);
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
        public void ReviewerRound2_P2_IndeterminateOwner_SignalsAmbiguityWithoutFalsePairing()
        {
            // Review 2, Point 3: When dump.json declares Ns.Player.Run in both A and B:
            // Left provides ONLY legacy record of A at 0x1000 without owner.
            // Right provides ONLY record of B qualified at 0x2000.
            // An absent owner with multiple candidate declarations is indeterminate.
            // Must NOT assert address change in B! Must signal ambiguity.
            var tempDir = Path.Combine(Path.GetTempPath(), "reviewer2-indeterminate-owner-" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                var model = CreateTwoImagesModel();
                var leftScript = new ScriptJson
                {
                    ScriptMethod = new List<ScriptMethod>
                    {
                        new() { Name = "Ns.Player$$Run", Signature = "void Run()", Address = 0x1000 } // no owner, indeterminate
                    }
                };
                var rightScript = new ScriptJson
                {
                    ScriptMethod = new List<ScriptMethod>
                    {
                        new() { Name = "Ns.Player$$Run", Signature = "void Run()", Address = 0x2000, Image = "B.dll", Assembly = "B" }
                    }
                };

                var leftDir = Path.Combine(tempDir, "left");
                var rightDir = Path.Combine(tempDir, "right");
                var outPath = Path.Combine(tempDir, "diff.json");
                CreateFixtures(leftDir, model, leftScript);
                CreateFixtures(rightDir, model, rightScript);

                var exitCode = DumpDiffer.Execute(leftDir, rightDir, outPath);
                Assert.Equal(1, exitCode);

                var diff = JsonSerializer.Deserialize<DumpDiffResult>(File.ReadAllText(outPath))!;
                Assert.True(diff.hasAmbiguousMethodAddresses);
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
        public void ReviewerRound2_P2_CollidingSignaturesInSameImage_CancellingRVAsPreservesAmbiguity()
        {
            // Review 2, Point 4: In mscorlib.dll, System.Enum.ToObject has two entries with colliding C signature "uint16_t value".
            // Left: [0x42D6D50, 0x42D7190].
            // Right: [0x1000, 0x42D7190].
            // Must NOT report 0x42D6D50 -> 0x1000 without ambiguity! Must detect ambiguity.
            var tempDir = Path.Combine(Path.GetTempPath(), "reviewer2-dofus-colliding-sig-" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                var model = new JsonDumpModel
                {
                    types = new List<JsonDumpType>
                    {
                        new()
                        {
                            image = "mscorlib.dll",
                            assembly = "mscorlib",
                            fullName = "System.Enum",
                            methods = new List<JsonDumpMethod>
                            {
                                new() { name = "ToObject", returnType = "object" },
                                new() { name = "ToObject", returnType = "object" }
                            }
                        }
                    }
                };

                var sigColliding = "Il2CppObject* System_Enum__ToObject (System_Type_o* enumType, uint16_t value, const MethodInfo* method);";
                var leftScript = new ScriptJson
                {
                    ScriptMethod = new List<ScriptMethod>
                    {
                        new() { Name = "System.Enum$$ToObject", Signature = sigColliding, Address = 0x42D6D50, Image = "mscorlib.dll", Assembly = "mscorlib" },
                        new() { Name = "System.Enum$$ToObject", Signature = sigColliding, Address = 0x42D7190, Image = "mscorlib.dll", Assembly = "mscorlib" }
                    }
                };
                var rightScript = new ScriptJson
                {
                    ScriptMethod = new List<ScriptMethod>
                    {
                        new() { Name = "System.Enum$$ToObject", Signature = sigColliding, Address = 0x1000, Image = "mscorlib.dll", Assembly = "mscorlib" },
                        new() { Name = "System.Enum$$ToObject", Signature = sigColliding, Address = 0x42D7190, Image = "mscorlib.dll", Assembly = "mscorlib" }
                    }
                };

                var leftDir = Path.Combine(tempDir, "left");
                var rightDir = Path.Combine(tempDir, "right");
                var outPath = Path.Combine(tempDir, "diff.json");
                CreateFixtures(leftDir, model, leftScript);
                CreateFixtures(rightDir, model, rightScript);

                var exitCode = DumpDiffer.Execute(leftDir, rightDir, outPath);
                Assert.Equal(1, exitCode);

                var diff = JsonSerializer.Deserialize<DumpDiffResult>(File.ReadAllText(outPath))!;
                Assert.True(diff.hasAmbiguousMethodAddresses);
                Assert.Empty(diff.methods.addressChanged!);
                Assert.NotNull(diff.methods.note);
                Assert.Contains(sigColliding, diff.methods.note);
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
        public void ReviewerRound3_P2_NestedGenericHierarchy_PreservesSegmentsAndArity_AmbiguityOnHomonymousDeclarations()
        {
            // Review 3, Point 1: Nested generic type Ns.Outer<T>.Nested<U> declared in both A.dll and B.dll
            // Left: legacy Ns.Outer.Nested<int, string>$$Run at 0x1000 (from A)
            // Right: B's record Image: "B.dll" at 0x2000
            // Must NOT attribute 0x1000 -> 0x2000 to B without ambiguity!
            var tempDir = Path.Combine(Path.GetTempPath(), "reviewer3-nested-generic-" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                var model = new JsonDumpModel
                {
                    types = new List<JsonDumpType>
                    {
                        new()
                        {
                            image = "A.dll",
                            assembly = "A",
                            fullName = "Ns.Outer<T>.Nested<U>",
                            methods = new List<JsonDumpMethod>
                            {
                                new() { name = "Run", returnType = "void" }
                            }
                        },
                        new()
                        {
                            image = "B.dll",
                            assembly = "B",
                            fullName = "Ns.Outer<T>.Nested<U>",
                            methods = new List<JsonDumpMethod>
                            {
                                new() { name = "Run", returnType = "void" }
                            }
                        }
                    }
                };

                var leftScript = new ScriptJson
                {
                    ScriptMethod = new List<ScriptMethod>
                    {
                        new() { Name = "Ns.Outer.Nested<int, string>$$Run", Signature = "void Run()", Address = 0x1000 }
                    }
                };
                var rightScript = new ScriptJson
                {
                    ScriptMethod = new List<ScriptMethod>
                    {
                        new() { Name = "Ns.Outer.Nested<int, string>$$Run", Signature = "void Run()", Address = 0x2000, Image = "B.dll", Assembly = "B" }
                    }
                };

                var leftDir = Path.Combine(tempDir, "left");
                var rightDir = Path.Combine(tempDir, "right");
                var outPath = Path.Combine(tempDir, "diff.json");
                CreateFixtures(leftDir, model, leftScript);
                CreateFixtures(rightDir, model, rightScript);

                var exitCode = DumpDiffer.Execute(leftDir, rightDir, outPath);
                Assert.Equal(1, exitCode);

                var diff = JsonSerializer.Deserialize<DumpDiffResult>(File.ReadAllText(outPath))!;
                Assert.True(diff.hasAmbiguousMethodAddresses);
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
        public void ReviewerRound3_P2_QualifiedPhaseUnitShortcut_DistinctOverloads_DoNotPairWithoutMatchingSignature()
        {
            // Review 3, Point 2: In A.dll, declare Run(int) and Run(string) in both dump.json.
            // Left: RVA only to Run(int) at 0x1000.
            // Right: RVA only to Run(string) at 0x2000.
            // When 1 RVA exists on each side, must NOT pair Run(int) with Run(string) as an address change!
            var tempDir = Path.Combine(Path.GetTempPath(), "reviewer3-distinct-overloads-" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                var model = new JsonDumpModel
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
                                new() { name = "Run", returnType = "void", parameters = new List<JsonDumpParameter> { new() { name = "x", type = "int" } } },
                                new() { name = "Run", returnType = "void", parameters = new List<JsonDumpParameter> { new() { name = "s", type = "string" } } }
                            }
                        }
                    }
                };

                var leftScript = new ScriptJson
                {
                    ScriptMethod = new List<ScriptMethod>
                    {
                        new() { Name = "Ns.Player$$Run", Signature = "void Run(int)", Address = 0x1000, Image = "A.dll", Assembly = "A" }
                    }
                };
                var rightScript = new ScriptJson
                {
                    ScriptMethod = new List<ScriptMethod>
                    {
                        new() { Name = "Ns.Player$$Run", Signature = "void Run(string)", Address = 0x2000, Image = "A.dll", Assembly = "A" }
                    }
                };

                var leftDir = Path.Combine(tempDir, "left");
                var rightDir = Path.Combine(tempDir, "right");
                var outPath = Path.Combine(tempDir, "diff.json");
                CreateFixtures(leftDir, model, leftScript);
                CreateFixtures(rightDir, model, rightScript);

                var exitCode = DumpDiffer.Execute(leftDir, rightDir, outPath);
                Assert.Equal(0, exitCode);

                var diff = JsonSerializer.Deserialize<DumpDiffResult>(File.ReadAllText(outPath))!;
                Assert.False(diff.hasAmbiguousMethodAddresses);
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
        public void ReviewerRound3_P2_LegacyPhaseMultiEntry_DisjointExplicitOwners_DoNotTriggerFalseAmbiguity()
        {
            // Review 3, Point 3: Ns.Player.Run in 4 images (A, B, C, D).
            // Left: RVAs for A and B.
            // Right: RVAs for C and D.
            // All entries have explicit owners, but Left owners {A, B} and Right owners {C, D} are completely disjoint.
            // Must NOT trigger false ambiguity! Must remain unmatched without flagging ambiguity.
            var tempDir = Path.Combine(Path.GetTempPath(), "reviewer3-disjoint-owners-" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                var model = new JsonDumpModel
                {
                    types = new List<JsonDumpType>
                    {
                        new() { image = "A.dll", assembly = "A", fullName = "Ns.Player", methods = new List<JsonDumpMethod> { new() { name = "Run", returnType = "void" } } },
                        new() { image = "B.dll", assembly = "B", fullName = "Ns.Player", methods = new List<JsonDumpMethod> { new() { name = "Run", returnType = "void" } } },
                        new() { image = "C.dll", assembly = "C", fullName = "Ns.Player", methods = new List<JsonDumpMethod> { new() { name = "Run", returnType = "void" } } },
                        new() { image = "D.dll", assembly = "D", fullName = "Ns.Player", methods = new List<JsonDumpMethod> { new() { name = "Run", returnType = "void" } } }
                    }
                };

                var leftScript = new ScriptJson
                {
                    ScriptMethod = new List<ScriptMethod>
                    {
                        new() { Name = "Ns.Player$$Run", Signature = "void Run()", Address = 0x1000, Image = "A.dll", Assembly = "A" },
                        new() { Name = "Ns.Player$$Run", Signature = "void Run()", Address = 0x1100, Image = "B.dll", Assembly = "B" }
                    }
                };
                var rightScript = new ScriptJson
                {
                    ScriptMethod = new List<ScriptMethod>
                    {
                        new() { Name = "Ns.Player$$Run", Signature = "void Run()", Address = 0x2000, Image = "C.dll", Assembly = "C" },
                        new() { Name = "Ns.Player$$Run", Signature = "void Run()", Address = 0x2100, Image = "D.dll", Assembly = "D" }
                    }
                };

                var leftDir = Path.Combine(tempDir, "left");
                var rightDir = Path.Combine(tempDir, "right");
                var outPath = Path.Combine(tempDir, "diff.json");
                CreateFixtures(leftDir, model, leftScript);
                CreateFixtures(rightDir, model, rightScript);

                var exitCode = DumpDiffer.Execute(leftDir, rightDir, outPath);
                Assert.Equal(0, exitCode);

                var diff = JsonSerializer.Deserialize<DumpDiffResult>(File.ReadAllText(outPath))!;
                Assert.False(diff.hasAmbiguousMethodAddresses);
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
        public void ReviewerRound4_P2_CrossQualifiedOverloads_UniqueDeclaration_EmitsTransitions()
        {
            // When Ns.Player is uniquely declared in A.dll, cross-qualified overloads resolve to A.dll unambiguously
            var tempDir = Path.Combine(Path.GetTempPath(), "reviewer4-cross-qualified-" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                var model = new JsonDumpModel
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
                                new() { name = "Run", returnType = "void", parameters = new List<JsonDumpParameter> { new() { name = "x", type = "int" } } },
                                new() { name = "Run", returnType = "void", parameters = new List<JsonDumpParameter> { new() { name = "s", type = "string" } } }
                            }
                        }
                    }
                };

                var leftScript = new ScriptJson
                {
                    ScriptMethod = new List<ScriptMethod>
                    {
                        new() { Name = "Ns.Player$$Run", Signature = "void Run(int)", Address = 0x1000, Image = "A.dll", Assembly = "A" },
                        new() { Name = "Ns.Player$$Run", Signature = "void Run(string)", Address = 0x2000 }
                    }
                };
                var rightScript = new ScriptJson
                {
                    ScriptMethod = new List<ScriptMethod>
                    {
                        new() { Name = "Ns.Player$$Run", Signature = "void Run(int)", Address = 0x3000 },
                        new() { Name = "Ns.Player$$Run", Signature = "void Run(string)", Address = 0x4000, Image = "A.dll", Assembly = "A" }
                    }
                };

                var leftDir = Path.Combine(tempDir, "left");
                var rightDir = Path.Combine(tempDir, "right");
                var outPath = Path.Combine(tempDir, "diff.json");
                CreateFixtures(leftDir, model, leftScript);
                CreateFixtures(rightDir, model, rightScript);

                var exitCode = DumpDiffer.Execute(leftDir, rightDir, outPath);
                Assert.Equal(1, exitCode);

                var diff = JsonSerializer.Deserialize<DumpDiffResult>(File.ReadAllText(outPath))!;
                Assert.False(diff.hasAmbiguousMethodAddresses);
                Assert.NotNull(diff.methods.addressChanged);
                Assert.Equal(2, diff.methods.addressChanged.Count);

                var changeInt = diff.methods.addressChanged.FirstOrDefault(c => c.method == "void Run(int)");
                Assert.NotNull(changeInt);
                Assert.Equal("0x1000", changeInt.left);
                Assert.Equal("0x3000", changeInt.right);

                var changeStr = diff.methods.addressChanged.FirstOrDefault(c => c.method == "void Run(string)");
                Assert.NotNull(changeStr);
                Assert.Equal("0x2000", changeStr.left);
                Assert.Equal("0x4000", changeStr.right);
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
        public void ReviewerRound4_P2_LegacySignaturelessGroups_DetectsAmbiguityWithMultipleCandidates()
        {
            // Review 4, Point 2: Ns.Player.Run in A and B.
            // Left: two legacy records without signature at 0x1000 and 0x2000.
            // Right: two qualified records with signature at 0x3000 and 0x4000 (A and B).
            // Diff must signal ambiguity in both directions!
            var tempDir = Path.Combine(Path.GetTempPath(), "reviewer4-legacy-sigless-" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                var model = CreateTwoImagesModel();
                var leftScript = new ScriptJson
                {
                    ScriptMethod = new List<ScriptMethod>
                    {
                        new() { Name = "Ns.Player$$Run", Address = 0x1000 },
                        new() { Name = "Ns.Player$$Run", Address = 0x2000 }
                    }
                };
                var rightScript = new ScriptJson
                {
                    ScriptMethod = new List<ScriptMethod>
                    {
                        new() { Name = "Ns.Player$$Run", Signature = "void Run()", Address = 0x3000, Image = "A.dll", Assembly = "A" },
                        new() { Name = "Ns.Player$$Run", Signature = "void Run()", Address = 0x4000, Image = "B.dll", Assembly = "B" }
                    }
                };

                var leftDir = Path.Combine(tempDir, "left");
                var rightDir = Path.Combine(tempDir, "right");
                var outPath = Path.Combine(tempDir, "diff.json");
                CreateFixtures(leftDir, model, leftScript);
                CreateFixtures(rightDir, model, rightScript);

                var exitCode = DumpDiffer.Execute(leftDir, rightDir, outPath);
                Assert.Equal(1, exitCode);

                var diff = JsonSerializer.Deserialize<DumpDiffResult>(File.ReadAllText(outPath))!;
                Assert.True(diff.hasAmbiguousMethodAddresses);
                Assert.Empty(diff.methods.addressChanged!);

                // Test reverse direction
                var outPathRev = Path.Combine(tempDir, "diff_rev.json");
                var exitCodeRev = DumpDiffer.Execute(rightDir, leftDir, outPathRev);
                Assert.Equal(1, exitCodeRev);
                var diffRev = JsonSerializer.Deserialize<DumpDiffResult>(File.ReadAllText(outPathRev))!;
                Assert.True(diffRev.hasAmbiguousMethodAddresses);
                Assert.Empty(diffRev.methods.addressChanged!);
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
        public void ReviewerRound4_P2_ArrayRankComma_DoesNotAlterGenericArity()
        {
            // Review 4, Point 3: Declare Box<T> in A and B and also Box<T, U> in B.
            // Left: legacy Box<int[,]>$$Run at 0x1000 (from A).
            // Right: Box<int[,]>.Run at 0x2000 in B with Image "B.dll".
            // The comma in [,] must not count as generic parameter separator!
            // Box<int[,]> must resolve to arity 1 (present in both A and B), causing IsIndeterminate = true and ambiguity!
            var tempDir = Path.Combine(Path.GetTempPath(), "reviewer4-array-rank-" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                var model = new JsonDumpModel
                {
                    types = new List<JsonDumpType>
                    {
                        new()
                        {
                            image = "A.dll",
                            assembly = "A",
                            fullName = "Box<T>",
                            methods = new List<JsonDumpMethod> { new() { name = "Run", returnType = "void" } }
                        },
                        new()
                        {
                            image = "B.dll",
                            assembly = "B",
                            fullName = "Box<T>",
                            methods = new List<JsonDumpMethod> { new() { name = "Run", returnType = "void" } }
                        },
                        new()
                        {
                            image = "B.dll",
                            assembly = "B",
                            fullName = "Box<T, U>",
                            methods = new List<JsonDumpMethod> { new() { name = "Run", returnType = "void" } }
                        }
                    }
                };

                var leftScript = new ScriptJson
                {
                    ScriptMethod = new List<ScriptMethod>
                    {
                        new() { Name = "Box<int[,]>$$Run", Signature = "void Run()", Address = 0x1000 }
                    }
                };
                var rightScript = new ScriptJson
                {
                    ScriptMethod = new List<ScriptMethod>
                    {
                        new() { Name = "Box<int[,]>$$Run", Signature = "void Run()", Address = 0x2000, Image = "B.dll", Assembly = "B" }
                    }
                };

                var leftDir = Path.Combine(tempDir, "left");
                var rightDir = Path.Combine(tempDir, "right");
                var outPath = Path.Combine(tempDir, "diff.json");
                CreateFixtures(leftDir, model, leftScript);
                CreateFixtures(rightDir, model, rightScript);

                var exitCode = DumpDiffer.Execute(leftDir, rightDir, outPath);
                Assert.Equal(1, exitCode);

                var diff = JsonSerializer.Deserialize<DumpDiffResult>(File.ReadAllText(outPath))!;
                Assert.True(diff.hasAmbiguousMethodAddresses);
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
        public void ReviewerRound4_P2_RenamedParameterInSingleMethod_EmitsRvaTransition()
        {
            // Review 4, Point 4: In A, single method Run(int). Rename 'value' to 'count'. RVA 0x1000 -> 0x2000.
            // Must report the RVA transition!
            var tempDir = Path.Combine(Path.GetTempPath(), "reviewer4-renamed-param-" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                var leftModel = new JsonDumpModel
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
                                new()
                                {
                                    name = "Run",
                                    returnType = "void",
                                    parameters = new List<JsonDumpParameter> { new() { name = "value", type = "int" } }
                                }
                            }
                        }
                    }
                };
                var rightModel = new JsonDumpModel
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
                                new()
                                {
                                    name = "Run",
                                    returnType = "void",
                                    parameters = new List<JsonDumpParameter> { new() { name = "count", type = "int" } }
                                }
                            }
                        }
                    }
                };

                var leftScript = new ScriptJson
                {
                    ScriptMethod = new List<ScriptMethod>
                    {
                        new() { Name = "Ns.Player$$Run", Signature = "void Run(int value)", Address = 0x1000, Image = "A.dll", Assembly = "A" }
                    }
                };
                var rightScript = new ScriptJson
                {
                    ScriptMethod = new List<ScriptMethod>
                    {
                        new() { Name = "Ns.Player$$Run", Signature = "void Run(int count)", Address = 0x2000, Image = "A.dll", Assembly = "A" }
                    }
                };

                var leftDir = Path.Combine(tempDir, "left");
                var rightDir = Path.Combine(tempDir, "right");
                var outPath = Path.Combine(tempDir, "diff.json");
                CreateFixtures(leftDir, leftModel, leftScript);
                CreateFixtures(rightDir, rightModel, rightScript);

                var exitCode = DumpDiffer.Execute(leftDir, rightDir, outPath);
                Assert.Equal(1, exitCode);

                var diff = JsonSerializer.Deserialize<DumpDiffResult>(File.ReadAllText(outPath))!;
                Assert.False(diff.hasAmbiguousMethodAddresses);
                Assert.NotNull(diff.methods.addressChanged);
                Assert.Single(diff.methods.addressChanged);
                var change = diff.methods.addressChanged[0];
                Assert.Equal("Ns.Player", change.type);
                Assert.Equal("Run", change.method);
                Assert.Equal("0x1000", change.left);
                Assert.Equal("0x2000", change.right);
                Assert.Equal("A.dll", change.image);
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
        public void ReviewerRound5_P2_HomonymousType_RunStringWithExplicitAssemblyB_DoesNotPairWithQualifiedA()
        {
            // Review 5, Point 1: Homonymous type Ns.Player in A and B.
            // Left: Run(int) in A at 0x1000 (Image "A.dll", Assembly "A"), Run(string) in B at 0x2000 with Assembly: "B".
            // Right: Run(string) qualified in A at 0x3000 (Image "A.dll", Assembly "A").
            // Must NOT emit 0x2000 -> 0x3000 in A!
            var tempDir = Path.Combine(Path.GetTempPath(), "reviewer5-explicit-asm-b-" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                var model = new JsonDumpModel
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
                                new() { name = "Run", returnType = "void", parameters = new List<JsonDumpParameter> { new() { name = "x", type = "int" } } },
                                new() { name = "Run", returnType = "void", parameters = new List<JsonDumpParameter> { new() { name = "s", type = "string" } } }
                            }
                        },
                        new()
                        {
                            image = "B.dll",
                            assembly = "B",
                            fullName = "Ns.Player",
                            methods = new List<JsonDumpMethod>
                            {
                                new() { name = "Run", returnType = "void", parameters = new List<JsonDumpParameter> { new() { name = "x", type = "int" } } },
                                new() { name = "Run", returnType = "void", parameters = new List<JsonDumpParameter> { new() { name = "s", type = "string" } } }
                            }
                        }
                    }
                };

                var leftScript = new ScriptJson
                {
                    ScriptMethod = new List<ScriptMethod>
                    {
                        new() { Name = "Ns.Player$$Run", Signature = "void Run(int)", Address = 0x1000, Image = "A.dll", Assembly = "A" },
                        new() { Name = "Ns.Player$$Run", Signature = "void Run(string)", Address = 0x2000, Assembly = "B" }
                    }
                };
                var rightScript = new ScriptJson
                {
                    ScriptMethod = new List<ScriptMethod>
                    {
                        new() { Name = "Ns.Player$$Run", Signature = "void Run(string)", Address = 0x3000, Image = "A.dll", Assembly = "A" }
                    }
                };

                var leftDir = Path.Combine(tempDir, "left");
                var rightDir = Path.Combine(tempDir, "right");
                var outPath = Path.Combine(tempDir, "diff.json");
                CreateFixtures(leftDir, model, leftScript);
                CreateFixtures(rightDir, model, rightScript);

                var exitCode = DumpDiffer.Execute(leftDir, rightDir, outPath);
                var diff = JsonSerializer.Deserialize<DumpDiffResult>(File.ReadAllText(outPath))!;

                // Incompatible owners (B vs A): must NOT pair as address change in A!
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
        public void ReviewerRound5_P2_HomonymousType_RunStringWithoutOwner_SignalsAmbiguityWithoutFalsePairingToA()
        {
            // Review 5, Point 1: Homonymous type Ns.Player in A and B.
            // Left: Run(int) in A at 0x1000 (Image "A.dll", Assembly "A"), Run(string) at 0x2000 WITHOUT owner.
            // Right: Run(string) qualified in A at 0x3000 (Image "A.dll", Assembly "A").
            // Absent owner with multiple candidate declarations is indeterminate. Must NOT pair with A; must signal ambiguity!
            var tempDir = Path.Combine(Path.GetTempPath(), "reviewer5-no-owner-indeterminate-" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                var model = new JsonDumpModel
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
                                new() { name = "Run", returnType = "void", parameters = new List<JsonDumpParameter> { new() { name = "x", type = "int" } } },
                                new() { name = "Run", returnType = "void", parameters = new List<JsonDumpParameter> { new() { name = "s", type = "string" } } }
                            }
                        },
                        new()
                        {
                            image = "B.dll",
                            assembly = "B",
                            fullName = "Ns.Player",
                            methods = new List<JsonDumpMethod>
                            {
                                new() { name = "Run", returnType = "void", parameters = new List<JsonDumpParameter> { new() { name = "x", type = "int" } } },
                                new() { name = "Run", returnType = "void", parameters = new List<JsonDumpParameter> { new() { name = "s", type = "string" } } }
                            }
                        }
                    }
                };

                var leftScript = new ScriptJson
                {
                    ScriptMethod = new List<ScriptMethod>
                    {
                        new() { Name = "Ns.Player$$Run", Signature = "void Run(int)", Address = 0x1000, Image = "A.dll", Assembly = "A" },
                        new() { Name = "Ns.Player$$Run", Signature = "void Run(string)", Address = 0x2000 }
                    }
                };
                var rightScript = new ScriptJson
                {
                    ScriptMethod = new List<ScriptMethod>
                    {
                        new() { Name = "Ns.Player$$Run", Signature = "void Run(string)", Address = 0x3000, Image = "A.dll", Assembly = "A" }
                    }
                };

                var leftDir = Path.Combine(tempDir, "left");
                var rightDir = Path.Combine(tempDir, "right");
                var outPath = Path.Combine(tempDir, "diff.json");
                CreateFixtures(leftDir, model, leftScript);
                CreateFixtures(rightDir, model, rightScript);

                var exitCode = DumpDiffer.Execute(leftDir, rightDir, outPath);
                Assert.Equal(1, exitCode);

                var diff = JsonSerializer.Deserialize<DumpDiffResult>(File.ReadAllText(outPath))!;
                Assert.True(diff.hasAmbiguousMethodAddresses);
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
        public void ReviewerRound5_P2_CollidingSignature_SingleRvaEachSide_SignalsAmbiguityWithoutFalseTransition()
        {
            // Review 5, Point 2: In mscorlib.dll, System.Enum.ToObject has overloads with char and ushort parameters.
            // Both map to C signature "object System.Enum$$ToObject (uint16_t value)".
            // Left has ONLY 0x42D7190; Right has ONLY 0x42D6D50.
            // When C signature represents more than one declaration and cannot be distinguished, must signal ambiguity!
            var tempDir = Path.Combine(Path.GetTempPath(), "reviewer5-dofus-single-rva-" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                var model = new JsonDumpModel
                {
                    types = new List<JsonDumpType>
                    {
                        new()
                        {
                            image = "mscorlib.dll",
                            assembly = "mscorlib",
                            fullName = "System.Enum",
                            methods = new List<JsonDumpMethod>
                            {
                                new()
                                {
                                    name = "ToObject",
                                    returnType = "object",
                                    parameters = new List<JsonDumpParameter>
                                    {
                                        new() { name = "enumType", type = "Type" },
                                        new() { name = "value", type = "char" }
                                    }
                                },
                                new()
                                {
                                    name = "ToObject",
                                    returnType = "object",
                                    parameters = new List<JsonDumpParameter>
                                    {
                                        new() { name = "enumType", type = "Type" },
                                        new() { name = "value", type = "ushort" }
                                    }
                                }
                            }
                        }
                    }
                };

                var sigColliding = "Il2CppObject* System_Enum__ToObject (System_Type_o* enumType, uint16_t value, const MethodInfo* method);";
                var leftScript = new ScriptJson
                {
                    ScriptMethod = new List<ScriptMethod>
                    {
                        new() { Name = "System.Enum$$ToObject", Signature = sigColliding, Address = 0x42D7190, Image = "mscorlib.dll", Assembly = "mscorlib" }
                    }
                };
                var rightScript = new ScriptJson
                {
                    ScriptMethod = new List<ScriptMethod>
                    {
                        new() { Name = "System.Enum$$ToObject", Signature = sigColliding, Address = 0x42D6D50, Image = "mscorlib.dll", Assembly = "mscorlib" }
                    }
                };

                var leftDir = Path.Combine(tempDir, "left");
                var rightDir = Path.Combine(tempDir, "right");
                var outPath = Path.Combine(tempDir, "diff.json");
                CreateFixtures(leftDir, model, leftScript);
                CreateFixtures(rightDir, model, rightScript);

                var exitCode = DumpDiffer.Execute(leftDir, rightDir, outPath);
                Assert.Equal(1, exitCode);

                var diff = JsonSerializer.Deserialize<DumpDiffResult>(File.ReadAllText(outPath))!;
                Assert.True(diff.hasAmbiguousMethodAddresses);
                Assert.Empty(diff.methods.addressChanged!);
                Assert.NotNull(diff.methods.note);
                Assert.Contains(sigColliding, diff.methods.note);
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
        public void ReviewerRound5_P2_IdenticalLegacyMaps_HomonymousSameRva_UnchangedWithoutFalseAmbiguity()
        {
            // Review 5, Point 3: Two identical legacy maps, each with two homonymous entries without signature at 0x1000.
            // Any correlation preserves 0x1000. Must return 0 without false ambiguity!
            var tempDir = Path.Combine(Path.GetTempPath(), "reviewer5-identical-legacy-same-rva-" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                var model = CreateTwoImagesModel();
                var leftScript = new ScriptJson
                {
                    ScriptMethod = new List<ScriptMethod>
                    {
                        new() { Name = "Ns.Player$$Run", Address = 0x1000 },
                        new() { Name = "Ns.Player$$Run", Address = 0x1000 }
                    }
                };
                var rightScript = new ScriptJson
                {
                    ScriptMethod = new List<ScriptMethod>
                    {
                        new() { Name = "Ns.Player$$Run", Address = 0x1000 },
                        new() { Name = "Ns.Player$$Run", Address = 0x1000 }
                    }
                };

                var leftDir = Path.Combine(tempDir, "left");
                var rightDir = Path.Combine(tempDir, "right");
                var outPath = Path.Combine(tempDir, "diff.json");
                CreateFixtures(leftDir, model, leftScript);
                CreateFixtures(rightDir, model, rightScript);

                var exitCode = DumpDiffer.Execute(leftDir, rightDir, outPath);
                Assert.Equal(0, exitCode);

                var diff = JsonSerializer.Deserialize<DumpDiffResult>(File.ReadAllText(outPath))!;
                Assert.False(diff.hasAmbiguousMethodAddresses);
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
        public void ReviewerRound5_P2_DisjointSignaturesInRemaining_DoNotTriggerFalseAmbiguity()
        {
            // Review 5, Point 3: Left has Run(int) and Run(string). Right has Run(float) and Run(double).
            // All signatures are known and distinct with no compatible pair.
            // Must NOT flag ambiguity! Must return 0 without changes.
            var tempDir = Path.Combine(Path.GetTempPath(), "reviewer5-disjoint-sigs-remaining-" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                var model = new JsonDumpModel
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
                                new() { name = "Run", returnType = "void", parameters = new List<JsonDumpParameter> { new() { name = "x", type = "int" } } },
                                new() { name = "Run", returnType = "void", parameters = new List<JsonDumpParameter> { new() { name = "s", type = "string" } } },
                                new() { name = "Run", returnType = "void", parameters = new List<JsonDumpParameter> { new() { name = "f", type = "float" } } },
                                new() { name = "Run", returnType = "void", parameters = new List<JsonDumpParameter> { new() { name = "d", type = "double" } } }
                            }
                        }
                    }
                };

                var leftScript = new ScriptJson
                {
                    ScriptMethod = new List<ScriptMethod>
                    {
                        new() { Name = "Ns.Player$$Run", Signature = "void Run(int)", Address = 0x1000 },
                        new() { Name = "Ns.Player$$Run", Signature = "void Run(string)", Address = 0x2000 }
                    }
                };
                var rightScript = new ScriptJson
                {
                    ScriptMethod = new List<ScriptMethod>
                    {
                        new() { Name = "Ns.Player$$Run", Signature = "void Run(float)", Address = 0x3000 },
                        new() { Name = "Ns.Player$$Run", Signature = "void Run(double)", Address = 0x4000 }
                    }
                };

                var leftDir = Path.Combine(tempDir, "left");
                var rightDir = Path.Combine(tempDir, "right");
                var outPath = Path.Combine(tempDir, "diff.json");
                CreateFixtures(leftDir, model, leftScript);
                CreateFixtures(rightDir, model, rightScript);

                var exitCode = DumpDiffer.Execute(leftDir, rightDir, outPath);
                Assert.Equal(0, exitCode);

                var diff = JsonSerializer.Deserialize<DumpDiffResult>(File.ReadAllText(outPath))!;
                Assert.False(diff.hasAmbiguousMethodAddresses);
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
        public void ReviewerRound6_P2_Dofus_EnumToObject_RealNativeSignature_SignalsAmbiguityWithoutDelta()
        {
            // Review 6, Point 1: System.Enum.ToObject(Type, char) vs ToObject(Type, ushort)
            // Real native signature: Il2CppObject* System_Enum__ToObject (System_Type_o* enumType, uint16_t value, const MethodInfo* method);
            // Must NOT emit false delta 0x42D7190 -> 0x42D6D50!
            var tempDir = Path.Combine(Path.GetTempPath(), "reviewer6-enum-toobject-" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                var model = new JsonDumpModel
                {
                    types = new List<JsonDumpType>
                    {
                        new()
                        {
                            image = "mscorlib.dll",
                            assembly = "mscorlib",
                            fullName = "System.Enum",
                            methods = new List<JsonDumpMethod>
                            {
                                new()
                                {
                                    name = "ToObject",
                                    returnType = "object",
                                    parameters = new List<JsonDumpParameter>
                                    {
                                        new() { name = "enumType", type = "Type" },
                                        new() { name = "value", type = "char" }
                                    }
                                },
                                new()
                                {
                                    name = "ToObject",
                                    returnType = "object",
                                    parameters = new List<JsonDumpParameter>
                                    {
                                        new() { name = "enumType", type = "Type" },
                                        new() { name = "value", type = "ushort" }
                                    }
                                }
                            }
                        }
                    }
                };

                var sigReal = "Il2CppObject* System_Enum__ToObject (System_Type_o* enumType, uint16_t value, const MethodInfo* method);";
                var leftScript = new ScriptJson
                {
                    ScriptMethod = new List<ScriptMethod>
                    {
                        new() { Name = "System.Enum$$ToObject", Signature = sigReal, Address = 0x42D7190, Image = "mscorlib.dll", Assembly = "mscorlib" }
                    }
                };
                var rightScript = new ScriptJson
                {
                    ScriptMethod = new List<ScriptMethod>
                    {
                        new() { Name = "System.Enum$$ToObject", Signature = sigReal, Address = 0x42D6D50, Image = "mscorlib.dll", Assembly = "mscorlib" }
                    }
                };

                var leftDir = Path.Combine(tempDir, "left");
                var rightDir = Path.Combine(tempDir, "right");
                var outPath = Path.Combine(tempDir, "diff.json");
                CreateFixtures(leftDir, model, leftScript);
                CreateFixtures(rightDir, model, rightScript);

                var exitCode = DumpDiffer.Execute(leftDir, rightDir, outPath);
                Assert.Equal(1, exitCode);

                var diff = JsonSerializer.Deserialize<DumpDiffResult>(File.ReadAllText(outPath))!;
                Assert.True(diff.hasAmbiguousMethodAddresses);
                Assert.Empty(diff.methods.addressChanged!);
                Assert.NotNull(diff.methods.note);
                Assert.Contains(sigReal, diff.methods.note);
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
        public void ReviewerRound6_P2_String_IndexOf_RealNativeSignature_ReportsRvaChange()
        {
            // Review 6, Point 2: System.String.IndexOf(string) valid RVA change 0x40AE360 -> 0x40AE370
            // Real native signature: int32_t System_String__IndexOf (System_String_o* __this, System_String_o* value, const MethodInfo* method);
            // Must NOT be suppressed as ambiguous! Must cleanly report address change!
            var tempDir = Path.Combine(Path.GetTempPath(), "reviewer6-string-indexof-" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                var model = new JsonDumpModel
                {
                    types = new List<JsonDumpType>
                    {
                        new()
                        {
                            image = "mscorlib.dll",
                            assembly = "mscorlib",
                            fullName = "System.String",
                            methods = new List<JsonDumpMethod>
                            {
                                new()
                                {
                                    name = "IndexOf",
                                    returnType = "int",
                                    parameters = new List<JsonDumpParameter>
                                    {
                                        new() { name = "value", type = "char" }
                                    }
                                },
                                new()
                                {
                                    name = "IndexOf",
                                    returnType = "int",
                                    parameters = new List<JsonDumpParameter>
                                    {
                                        new() { name = "value", type = "string" }
                                    }
                                },
                                new()
                                {
                                    name = "IndexOf",
                                    returnType = "int",
                                    parameters = new List<JsonDumpParameter>
                                    {
                                        new() { name = "value", type = "string" },
                                        new() { name = "startIndex", type = "int" }
                                    }
                                },
                                new()
                                {
                                    name = "IndexOf",
                                    returnType = "int",
                                    parameters = new List<JsonDumpParameter>
                                    {
                                        new() { name = "value", type = "string" },
                                        new() { name = "comparisonType", type = "StringComparison" }
                                    }
                                }
                            }
                        }
                    }
                };

                var sigReal = "int32_t System_String__IndexOf (System_String_o* __this, System_String_o* value, const MethodInfo* method);";
                var leftScript = new ScriptJson
                {
                    ScriptMethod = new List<ScriptMethod>
                    {
                        new() { Name = "System.String$$IndexOf", Signature = sigReal, Address = 0x40AE360, Image = "mscorlib.dll", Assembly = "mscorlib" }
                    }
                };
                var rightScript = new ScriptJson
                {
                    ScriptMethod = new List<ScriptMethod>
                    {
                        new() { Name = "System.String$$IndexOf", Signature = sigReal, Address = 0x40AE370, Image = "mscorlib.dll", Assembly = "mscorlib" }
                    }
                };

                var leftDir = Path.Combine(tempDir, "left");
                var rightDir = Path.Combine(tempDir, "right");
                var outPath = Path.Combine(tempDir, "diff.json");
                CreateFixtures(leftDir, model, leftScript);
                CreateFixtures(rightDir, model, rightScript);

                var exitCode = DumpDiffer.Execute(leftDir, rightDir, outPath);
                Assert.Equal(1, exitCode);

                var diff = JsonSerializer.Deserialize<DumpDiffResult>(File.ReadAllText(outPath))!;
                Assert.False(diff.hasAmbiguousMethodAddresses);
                Assert.Single(diff.methods.addressChanged!);
                var change = diff.methods.addressChanged![0];
                Assert.Equal("System.String", change.type);
                Assert.Equal("0x40AE360", change.left);
                Assert.Equal("0x40AE370", change.right);
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
        public void ReviewerRound6_P2_BitConverter_GetBytes_RealNativeSignature_SignalsAmbiguityWithoutDelta()
        {
            // Review 6, Point 1: BitConverter.GetBytes(char) vs GetBytes(ushort)
            // Real native signature: Il2CppArray* System_BitConverter__GetBytes (uint16_t value, const MethodInfo* method);
            // Must NOT emit false delta!
            var tempDir = Path.Combine(Path.GetTempPath(), "reviewer6-bitconverter-" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                var model = new JsonDumpModel
                {
                    types = new List<JsonDumpType>
                    {
                        new()
                        {
                            image = "mscorlib.dll",
                            assembly = "mscorlib",
                            fullName = "System.BitConverter",
                            methods = new List<JsonDumpMethod>
                            {
                                new()
                                {
                                    name = "GetBytes",
                                    returnType = "byte[]",
                                    parameters = new List<JsonDumpParameter>
                                    {
                                        new() { name = "value", type = "char" }
                                    }
                                },
                                new()
                                {
                                    name = "GetBytes",
                                    returnType = "byte[]",
                                    parameters = new List<JsonDumpParameter>
                                    {
                                        new() { name = "value", type = "ushort" }
                                    }
                                }
                            }
                        }
                    }
                };

                var sigReal = "Il2CppArray* System_BitConverter__GetBytes (uint16_t value, const MethodInfo* method);";
                var leftScript = new ScriptJson
                {
                    ScriptMethod = new List<ScriptMethod>
                    {
                        new() { Name = "System.BitConverter$$GetBytes", Signature = sigReal, Address = 0x1000, Image = "mscorlib.dll", Assembly = "mscorlib" }
                    }
                };
                var rightScript = new ScriptJson
                {
                    ScriptMethod = new List<ScriptMethod>
                    {
                        new() { Name = "System.BitConverter$$GetBytes", Signature = sigReal, Address = 0x2000, Image = "mscorlib.dll", Assembly = "mscorlib" }
                    }
                };

                var leftDir = Path.Combine(tempDir, "left");
                var rightDir = Path.Combine(tempDir, "right");
                var outPath = Path.Combine(tempDir, "diff.json");
                CreateFixtures(leftDir, model, leftScript);
                CreateFixtures(rightDir, model, rightScript);

                var exitCode = DumpDiffer.Execute(leftDir, rightDir, outPath);
                Assert.Equal(1, exitCode);

                var diff = JsonSerializer.Deserialize<DumpDiffResult>(File.ReadAllText(outPath))!;
                Assert.True(diff.hasAmbiguousMethodAddresses);
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
        public void ReviewerRound6_MethodSignature_DistinguishesOverloadsWithoutCollision()
        {
            // When MethodSignature is present, overloads with identical C signatures (e.g. char vs ushort)
            // are unambiguously resolved by managed signature!
            var tempDir = Path.Combine(Path.GetTempPath(), "reviewer6-methodsig-" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                var model = new JsonDumpModel
                {
                    types = new List<JsonDumpType>
                    {
                        new()
                        {
                            image = "mscorlib.dll",
                            assembly = "mscorlib",
                            fullName = "System.Enum",
                            methods = new List<JsonDumpMethod>
                            {
                                new()
                                {
                                    name = "ToObject",
                                    returnType = "object",
                                    parameters = new List<JsonDumpParameter>
                                    {
                                        new() { name = "enumType", type = "Type" },
                                        new() { name = "value", type = "char" }
                                    }
                                },
                                new()
                                {
                                    name = "ToObject",
                                    returnType = "object",
                                    parameters = new List<JsonDumpParameter>
                                    {
                                        new() { name = "enumType", type = "Type" },
                                        new() { name = "value", type = "ushort" }
                                    }
                                }
                            }
                        }
                    }
                };

                var cSig = "Il2CppObject* System_Enum__ToObject (System_Type_o* enumType, uint16_t value, const MethodInfo* method);";
                var leftScript = new ScriptJson
                {
                    ScriptMethod = new List<ScriptMethod>
                    {
                        new() { Name = "System.Enum$$ToObject", Signature = cSig, MethodSignature = "ToObject(Type, char)", Address = 0x1000, Image = "mscorlib.dll", Assembly = "mscorlib" }
                    }
                };
                var rightScript = new ScriptJson
                {
                    ScriptMethod = new List<ScriptMethod>
                    {
                        new() { Name = "System.Enum$$ToObject", Signature = cSig, MethodSignature = "ToObject(Type, char)", Address = 0x2000, Image = "mscorlib.dll", Assembly = "mscorlib" }
                    }
                };

                var leftDir = Path.Combine(tempDir, "left");
                var rightDir = Path.Combine(tempDir, "right");
                var outPath = Path.Combine(tempDir, "diff.json");
                CreateFixtures(leftDir, model, leftScript);
                CreateFixtures(rightDir, model, rightScript);

                var exitCode = DumpDiffer.Execute(leftDir, rightDir, outPath);
                Assert.Equal(1, exitCode);

                var diff = JsonSerializer.Deserialize<DumpDiffResult>(File.ReadAllText(outPath))!;
                Assert.False(diff.hasAmbiguousMethodAddresses);
                Assert.Single(diff.methods.addressChanged!);
                Assert.Equal("0x1000", diff.methods.addressChanged![0].left);
                Assert.Equal("0x2000", diff.methods.addressChanged![0].right);
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
        public void ReviewerRound7_P2_ToObject_MultipleEntries_SameCSig_OneRvaChanged_ReportsDeltaWithoutAmbiguity()
        {
            // Review 7, Point 1: ToObject(Type, char) and ToObject(Type, ushort) in the same image with shared C signature.
            // When char RVA changes (0x1000 -> 0x2000) and ushort is unchanged (0x3000), must report 1 delta and NO ambiguity!
            var tempDir = Path.Combine(Path.GetTempPath(), "reviewer7-toobject-delta-" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                var model = new JsonDumpModel
                {
                    types = new List<JsonDumpType>
                    {
                        new()
                        {
                            image = "mscorlib.dll",
                            assembly = "mscorlib",
                            fullName = "System.Enum",
                            methods = new List<JsonDumpMethod>
                            {
                                new()
                                {
                                    name = "ToObject",
                                    returnType = "object",
                                    parameters = new List<JsonDumpParameter>
                                    {
                                        new() { name = "enumType", type = "Type" },
                                        new() { name = "value", type = "char" }
                                    }
                                },
                                new()
                                {
                                    name = "ToObject",
                                    returnType = "object",
                                    parameters = new List<JsonDumpParameter>
                                    {
                                        new() { name = "enumType", type = "Type" },
                                        new() { name = "value", type = "ushort" }
                                    }
                                }
                            }
                        }
                    }
                };

                var cSig = "Il2CppObject* System_Enum__ToObject (System_Type_o* enumType, uint16_t value, const MethodInfo* method);";
                var leftScript = new ScriptJson
                {
                    ScriptMethod = new List<ScriptMethod>
                    {
                        new() { Name = "System.Enum$$ToObject", Signature = cSig, MethodSignature = "ToObject(System.Type, char)", Address = 0x1000, Image = "mscorlib.dll", Assembly = "mscorlib" },
                        new() { Name = "System.Enum$$ToObject", Signature = cSig, MethodSignature = "ToObject(System.Type, ushort)", Address = 0x3000, Image = "mscorlib.dll", Assembly = "mscorlib" }
                    }
                };
                var rightScript = new ScriptJson
                {
                    ScriptMethod = new List<ScriptMethod>
                    {
                        new() { Name = "System.Enum$$ToObject", Signature = cSig, MethodSignature = "ToObject(System.Type, char)", Address = 0x2000, Image = "mscorlib.dll", Assembly = "mscorlib" },
                        new() { Name = "System.Enum$$ToObject", Signature = cSig, MethodSignature = "ToObject(System.Type, ushort)", Address = 0x3000, Image = "mscorlib.dll", Assembly = "mscorlib" }
                    }
                };

                var leftDir = Path.Combine(tempDir, "left");
                var rightDir = Path.Combine(tempDir, "right");
                var outPath = Path.Combine(tempDir, "diff.json");
                CreateFixtures(leftDir, model, leftScript);
                CreateFixtures(rightDir, model, rightScript);

                var exitCode = DumpDiffer.Execute(leftDir, rightDir, outPath);
                Assert.Equal(1, exitCode);

                var diff = JsonSerializer.Deserialize<DumpDiffResult>(File.ReadAllText(outPath))!;
                Assert.False(diff.hasAmbiguousMethodAddresses);
                Assert.Single(diff.methods.addressChanged!);
                Assert.Equal("0x1000", diff.methods.addressChanged![0].left);
                Assert.Equal("0x2000", diff.methods.addressChanged![0].right);
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
        public void ReviewerRound7_P2_ToObject_SelfComparison_ReturnsZeroWithoutAmbiguity()
        {
            // Review 7, Point 1: Comparing the qualified map containing multiple overloads with shared C signature against itself
            // must return exit code 0, 0 address changes and false ambiguity!
            var tempDir = Path.Combine(Path.GetTempPath(), "reviewer7-toobject-self-" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                var model = new JsonDumpModel
                {
                    types = new List<JsonDumpType>
                    {
                        new()
                        {
                            image = "mscorlib.dll",
                            assembly = "mscorlib",
                            fullName = "System.Enum",
                            methods = new List<JsonDumpMethod>
                            {
                                new()
                                {
                                    name = "ToObject",
                                    returnType = "object",
                                    parameters = new List<JsonDumpParameter>
                                    {
                                        new() { name = "enumType", type = "Type" },
                                        new() { name = "value", type = "char" }
                                    }
                                },
                                new()
                                {
                                    name = "ToObject",
                                    returnType = "object",
                                    parameters = new List<JsonDumpParameter>
                                    {
                                        new() { name = "enumType", type = "Type" },
                                        new() { name = "value", type = "ushort" }
                                    }
                                }
                            }
                        }
                    }
                };

                var cSig = "Il2CppObject* System_Enum__ToObject (System_Type_o* enumType, uint16_t value, const MethodInfo* method);";
                var script = new ScriptJson
                {
                    ScriptMethod = new List<ScriptMethod>
                    {
                        new() { Name = "System.Enum$$ToObject", Signature = cSig, MethodSignature = "ToObject(System.Type, char)", Address = 0x1000, Image = "mscorlib.dll", Assembly = "mscorlib" },
                        new() { Name = "System.Enum$$ToObject", Signature = cSig, MethodSignature = "ToObject(System.Type, ushort)", Address = 0x3000, Image = "mscorlib.dll", Assembly = "mscorlib" }
                    }
                };

                var leftDir = Path.Combine(tempDir, "left");
                var rightDir = Path.Combine(tempDir, "right");
                var outPath = Path.Combine(tempDir, "diff.json");
                CreateFixtures(leftDir, model, script);
                CreateFixtures(rightDir, model, script);

                var exitCode = DumpDiffer.Execute(leftDir, rightDir, outPath);
                Assert.Equal(0, exitCode);

                var diff = JsonSerializer.Deserialize<DumpDiffResult>(File.ReadAllText(outPath))!;
                Assert.False(diff.hasAmbiguousMethodAddresses);
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
        public void ReviewerRound7_P2_ToObject_DisjointEntries_CharLeft_UshortRight_IntCommon_DoesNotEmitFalseDelta()
        {
            // Review 7, Point 1: char only on left, ushort only on right, int common on both.
            // Shared C signature for char and ushort must NOT fabricate 0x42D7190 -> 0x42D6D50 delta!
            var tempDir = Path.Combine(Path.GetTempPath(), "reviewer7-toobject-disjoint-" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                var model = new JsonDumpModel
                {
                    types = new List<JsonDumpType>
                    {
                        new()
                        {
                            image = "mscorlib.dll",
                            assembly = "mscorlib",
                            fullName = "System.Enum",
                            methods = new List<JsonDumpMethod>
                            {
                                new()
                                {
                                    name = "ToObject",
                                    returnType = "object",
                                    parameters = new List<JsonDumpParameter>
                                    {
                                        new() { name = "enumType", type = "Type" },
                                        new() { name = "value", type = "char" }
                                    }
                                },
                                new()
                                {
                                    name = "ToObject",
                                    returnType = "object",
                                    parameters = new List<JsonDumpParameter>
                                    {
                                        new() { name = "enumType", type = "Type" },
                                        new() { name = "value", type = "ushort" }
                                    }
                                },
                                new()
                                {
                                    name = "ToObject",
                                    returnType = "object",
                                    parameters = new List<JsonDumpParameter>
                                    {
                                        new() { name = "enumType", type = "Type" },
                                        new() { name = "value", type = "int" }
                                    }
                                }
                            }
                        }
                    }
                };

                var cSigCharUshort = "Il2CppObject* System_Enum__ToObject (System_Type_o* enumType, uint16_t value, const MethodInfo* method);";
                var cSigInt = "Il2CppObject* System_Enum__ToObject_int (System_Type_o* enumType, int32_t value, const MethodInfo* method);";

                var leftScript = new ScriptJson
                {
                    ScriptMethod = new List<ScriptMethod>
                    {
                        new() { Name = "System.Enum$$ToObject", Signature = cSigCharUshort, MethodSignature = "ToObject(System.Type, char)", Address = 0x42D7190, Image = "mscorlib.dll", Assembly = "mscorlib" },
                        new() { Name = "System.Enum$$ToObject", Signature = cSigInt, MethodSignature = "ToObject(System.Type, int)", Address = 0x4000, Image = "mscorlib.dll", Assembly = "mscorlib" }
                    }
                };
                var rightScript = new ScriptJson
                {
                    ScriptMethod = new List<ScriptMethod>
                    {
                        new() { Name = "System.Enum$$ToObject", Signature = cSigCharUshort, MethodSignature = "ToObject(System.Type, ushort)", Address = 0x42D6D50, Image = "mscorlib.dll", Assembly = "mscorlib" },
                        new() { Name = "System.Enum$$ToObject", Signature = cSigInt, MethodSignature = "ToObject(System.Type, int)", Address = 0x4000, Image = "mscorlib.dll", Assembly = "mscorlib" }
                    }
                };

                var leftDir = Path.Combine(tempDir, "left");
                var rightDir = Path.Combine(tempDir, "right");
                var outPath = Path.Combine(tempDir, "diff.json");
                CreateFixtures(leftDir, model, leftScript);
                CreateFixtures(rightDir, model, rightScript);

                var exitCode = DumpDiffer.Execute(leftDir, rightDir, outPath);
                Assert.Equal(0, exitCode);

                var diff = JsonSerializer.Deserialize<DumpDiffResult>(File.ReadAllText(outPath))!;
                Assert.False(diff.hasAmbiguousMethodAddresses);
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
        public void ReviewerRound7_P2_ArrayHelpers_Clear_ValVsRef_PreservesByref_DoesNotEmitFalseDelta()
        {
            // Review 7, Point 2: Dofus real case
            // Clear<TValue>(TValue[], int) vs Clear<TValue>(TValue[], ref int)
            // Left has val overload at 0x3460310, Right has ref overload at 0x34602E0.
            // With MethodSignature preserving 'ref int', they must NOT be paired into a false delta!
            var tempDir = Path.Combine(Path.GetTempPath(), "reviewer7-clear-byref-" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                var model = new JsonDumpModel
                {
                    types = new List<JsonDumpType>
                    {
                        new()
                        {
                            image = "UnityEngine.InputSystem.dll",
                            assembly = "UnityEngine.InputSystem",
                            fullName = "UnityEngine.InputSystem.Utilities.ArrayHelpers",
                            methods = new List<JsonDumpMethod>
                            {
                                new()
                                {
                                    name = "Clear",
                                    returnType = "void",
                                    parameters = new List<JsonDumpParameter>
                                    {
                                        new() { name = "array", type = "TValue[]" },
                                        new() { name = "count", type = "int" }
                                    }
                                },
                                new()
                                {
                                    name = "Clear",
                                    returnType = "void",
                                    parameters = new List<JsonDumpParameter>
                                    {
                                        new() { name = "array", type = "TValue[]" },
                                        new() { name = "count", type = "ref int" }
                                    }
                                }
                            }
                        }
                    }
                };

                var leftScript = new ScriptJson
                {
                    ScriptMethod = new List<ScriptMethod>
                    {
                        new()
                        {
                            Name = "UnityEngine.InputSystem.Utilities.ArrayHelpers$$Clear",
                            Signature = "void ArrayHelpers_Clear (Il2CppArray* array, int32_t count, const MethodInfo* method);",
                            MethodSignature = "Clear<TValue>(TValue[], int)",
                            Address = 0x3460310,
                            Image = "UnityEngine.InputSystem.dll",
                            Assembly = "UnityEngine.InputSystem"
                        }
                    }
                };
                var rightScript = new ScriptJson
                {
                    ScriptMethod = new List<ScriptMethod>
                    {
                        new()
                        {
                            Name = "UnityEngine.InputSystem.Utilities.ArrayHelpers$$Clear",
                            Signature = "void ArrayHelpers_Clear_ref (Il2CppArray* array, int32_t* count, const MethodInfo* method);",
                            MethodSignature = "Clear<TValue>(TValue[], ref int)",
                            Address = 0x34602E0,
                            Image = "UnityEngine.InputSystem.dll",
                            Assembly = "UnityEngine.InputSystem"
                        }
                    }
                };

                var leftDir = Path.Combine(tempDir, "left");
                var rightDir = Path.Combine(tempDir, "right");
                var outPath = Path.Combine(tempDir, "diff.json");
                CreateFixtures(leftDir, model, leftScript);
                CreateFixtures(rightDir, model, rightScript);

                var exitCode = DumpDiffer.Execute(leftDir, rightDir, outPath);
                Assert.Equal(0, exitCode);

                var diff = JsonSerializer.Deserialize<DumpDiffResult>(File.ReadAllText(outPath))!;
                Assert.False(diff.hasAmbiguousMethodAddresses);
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
    }
}
