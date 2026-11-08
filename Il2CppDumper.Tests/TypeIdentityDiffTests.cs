#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Il2CppDumper.Core;
using Il2CppDumper.Diff;
using Il2CppDumper.Tests.Synthetic;
using Xunit;

namespace Il2CppDumper.Tests
{
    public class TypeIdentityDiffTests
    {
        private static JsonDumpModel CreateDuplicateTypesModel()
        {
            var (metadata, binary, executor) = SyntheticEnvironment.Create(MetadataVersionFamily.V29);
            using (metadata)
            using (binary)
            {
                var original = metadata.typeDefs[0];
                var duplicate = new Il2CppTypeDefinition();
                foreach (var field in typeof(Il2CppTypeDefinition).GetFields())
                {
                    field.SetValue(duplicate, field.GetValue(original));
                }

                metadata.typeDefs = metadata.typeDefs.Append(duplicate).ToArray();
                metadata.imageDefs = metadata.imageDefs.Append(new Il2CppImageDefinition
                {
                    nameIndex = original.nameIndex,
                    assemblyIndex = -1,
                    typeStart = metadata.typeDefs.Length - 1,
                    typeCount = 1
                }).ToArray();

                return JsonDumpGenerator.Create(executor, new Config { DumpAttribute = false, DumpField = true, DumpFieldOffset = true });
            }
        }

        [Fact]
        public void TypeSchema_PreservesImageAndAssemblyAttribution_RootAndNestedTypes()
        {
            var (metadata, il2Cpp, executor) = SyntheticEnvironment.Create(MetadataVersionFamily.V29, seed: 42);
            using (metadata)
            using (il2Cpp)
            {
                // Ensure at least one nested type exists in metadata
                metadata.nestedTypeIndices = new[] { 1 };
                metadata.typeDefs[0].nestedTypesStart = 0;
                metadata.typeDefs[0].nested_type_count = 1;

                var config = new Config
                {
                    DumpTypeDefIndex = true,
                    DumpAttribute = false,
                    DumpField = true,
                    DumpMethod = true,
                    DumpProperty = true
                };

                var model = JsonDumpGenerator.Create(executor, config);
                Assert.NotNull(model);
                Assert.NotEmpty(model.types);

                var rootWithNested = model.types.FirstOrDefault(t => t.nested != null && t.nested.Count > 0);
                Assert.NotNull(rootWithNested);
                Assert.NotEmpty(rootWithNested!.nested);

                // All root types must preserve non-empty image and assembly
                foreach (var type in model.types)
                {
                    Assert.False(string.IsNullOrWhiteSpace(type.image), $"Root type '{type.fullName}' must have image attribution.");
                    Assert.False(string.IsNullOrWhiteSpace(type.assembly), $"Root type '{type.fullName}' must have assembly attribution.");
                    Assert.Contains("!", type.GetIdentityKey());

                    if (type.nested != null && type.nested.Count > 0)
                    {
                        foreach (var nestedType in type.nested)
                        {
                            Assert.False(string.IsNullOrWhiteSpace(nestedType.image), $"Nested type '{nestedType.fullName}' must have image attribution.");
                            Assert.False(string.IsNullOrWhiteSpace(nestedType.assembly), $"Nested type '{nestedType.fullName}' must have assembly attribution.");
                            Assert.Equal(type.image, nestedType.image);
                            Assert.Equal(type.assembly, nestedType.assembly);
                        }
                    }
                }

                // Verify serialization roundtrip
                var json = JsonSerializer.Serialize(model, JsonDumpGenerator.JsonOptions);
                var deserialized = JsonSerializer.Deserialize<JsonDumpModel>(json);
                Assert.NotNull(deserialized);
                Assert.Equal(model.types[0].image, deserialized.types[0].image);
                Assert.Equal(model.types[0].assembly, deserialized.types[0].assembly);
            }
        }

        [Fact]
        public void DuplicateTypes_DumpDiffer_DetectsFirstOccurrenceFieldTypeChange()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "dump-diff-dup-" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                var left = CreateDuplicateTypesModel();
                var right = CreateDuplicateTypesModel();

                foreach (var type in left.types.Concat(right.types))
                {
                    foreach (var f in type.fields.Where(x => !x.isStatic))
                    {
                        f.offset = "0x10";
                    }
                }

                var duplicates = right.types.GroupBy(x => x.fullName).First(x => x.Count() == 2).ToList();
                // Mutate only the FIRST occurrence
                duplicates[0].fields.First(x => !x.isStatic).type = "string";

                var leftPath = Path.Combine(tempDir, "left.json");
                var rightPath = Path.Combine(tempDir, "right.json");
                var outPath = Path.Combine(tempDir, "diff.json");

                File.WriteAllText(leftPath, JsonSerializer.Serialize(left, JsonDumpGenerator.JsonOptions));
                File.WriteAllText(rightPath, JsonSerializer.Serialize(right, JsonDumpGenerator.JsonOptions));

                using var sw = new StringWriter();
                var exit = DumpDiffer.Execute(leftPath, rightPath, outPath, sw);

                Assert.Equal(1, exit);

                var diff = JsonSerializer.Deserialize<DumpDiffResult>(File.ReadAllText(outPath))!;
                Assert.True(diff.HasDifferences);
                Assert.Single(diff.types.changed);

                var changed = diff.types.changed[0];
                Assert.Equal(duplicates[0].GetIdentityKey(), changed.type);
                Assert.Equal(duplicates[0].image, changed.image);
                Assert.Equal(duplicates[0].assembly, changed.assembly);
                Assert.Single(changed.fields.changed);
                Assert.Equal("syntheticField", changed.fields.changed[0].field);
                Assert.Equal("int", changed.fields.changed[0].left.type);
                Assert.Equal("string", changed.fields.changed[0].right.type);
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
        public void DuplicateTypes_FieldOffsetDiff_DetectsSecondOccurrenceOffsetShift()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "drift-dup-" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                var left = CreateDuplicateTypesModel();
                var right = CreateDuplicateTypesModel();

                foreach (var type in left.types.Concat(right.types))
                {
                    foreach (var f in type.fields.Where(x => !x.isStatic))
                    {
                        f.offset = "0x10";
                    }
                }

                var duplicates = right.types.GroupBy(x => x.fullName).First(x => x.Count() == 2).ToList();
                // Mutate only the SECOND occurrence offset from 0x10 to 0x40
                var targetField = duplicates[1].fields.First(x => !x.isStatic);
                targetField.offset = "0x40";

                var leftPath = Path.Combine(tempDir, "left.json");
                var rightPath = Path.Combine(tempDir, "right.json");
                var outPath = Path.Combine(tempDir, "drift.json");

                File.WriteAllText(leftPath, JsonSerializer.Serialize(left, JsonDumpGenerator.JsonOptions));
                File.WriteAllText(rightPath, JsonSerializer.Serialize(right, JsonDumpGenerator.JsonOptions));

                using var sw = new StringWriter();
                var exit = FieldOffsetDiff.Execute(leftPath, rightPath, outPath, sw);

                Assert.Equal(1, exit);

                var report = JsonSerializer.Deserialize<FieldOffsetDriftReport>(File.ReadAllText(outPath))!;
                Assert.True(report.HasDrift);
                Assert.Single(report.structs);

                var drifted = report.structs[0];
                Assert.Equal(duplicates[1].GetIdentityKey(), drifted.@struct);
                Assert.Equal(duplicates[1].image, drifted.image);
                Assert.Equal(duplicates[1].assembly, drifted.assembly);
                Assert.Single(drifted.fields);
                Assert.Equal(targetField.name, drifted.fields[0].name);
                Assert.Equal("0x10", drifted.fields[0].left);
                Assert.Equal("0x40", drifted.fields[0].right);
                Assert.Equal(FieldOffsetDriftType.Shifted, drifted.fields[0].drift);
                Assert.Equal(FieldOffsetSeverity.BreaksExternalReaders, drifted.fields[0].severity);
                Assert.Equal(1, drifted.verdict.shifted);
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
        public void DuplicateTypes_ReorderingTypesAndImages_DoesNotAffectDiffOrProduceFalseDeltas()
        {
            var left = CreateDuplicateTypesModel();
            var right = CreateDuplicateTypesModel();

            foreach (var type in left.types.Concat(right.types))
            {
                foreach (var f in type.fields.Where(x => !x.isStatic))
                {
                    f.offset = "0x10";
                }
            }

            // In right, reverse the order of the types
            right.types.Reverse();

            // Mutate only the first occurrence in right
            var targetType = right.types.First(t => t.image == "SyntheticImage.dll");
            targetType.fields.First(x => !x.isStatic).type = "string";

            var result = DumpDiffer.Compare(new JsonDumpModel { types = left.types }, right);
            Assert.True(result.HasDifferences);
            Assert.Empty(result.types.added);
            Assert.Empty(result.types.removed);
            Assert.Single(result.types.changed);
            Assert.Equal(targetType.GetIdentityKey(), result.types.changed[0].type);

            // In FieldOffsetDiff, identical inputs with reversed order must report zero drift
            var identicalReversed = new JsonDumpModel { types = left.types.ToList() };
            identicalReversed.types.Reverse();
            var driftResult = FieldOffsetDiff.Compare(left, identicalReversed);
            Assert.False(driftResult.HasDrift);
            Assert.Empty(driftResult.structs);
        }

        [Fact]
        public void DuplicateTypes_IdenticalFullyIdentifiedInputs_ReportsNoDifferencesAndNoDrift()
        {
            var left = CreateDuplicateTypesModel();
            var right = CreateDuplicateTypesModel();

            foreach (var type in left.types.Concat(right.types))
            {
                foreach (var f in type.fields.Where(x => !x.isStatic))
                {
                    f.offset = "0x10";
                }
            }

            var diffResult = DumpDiffer.Compare(left, right);
            Assert.False(diffResult.HasDifferences);
            Assert.Empty(diffResult.types.added);
            Assert.Empty(diffResult.types.removed);
            Assert.Empty(diffResult.types.changed);

            var driftResult = FieldOffsetDiff.Compare(left, right);
            Assert.False(driftResult.HasDrift);
            Assert.Empty(driftResult.structs);
        }

        [Fact]
        public void LegacyDumps_UniqueFullNames_SucceedsCompatibly()
        {
            var left = new JsonDumpModel
            {
                types = new List<JsonDumpType>
                {
                    new() { fullName = "Game.Player", fields = new() { new() { name = "hp", type = "int", offset = "0x10" } } },
                    new() { fullName = "Game.Enemy", fields = new() { new() { name = "hp", type = "int", offset = "0x10" } } }
                }
            };

            var right = new JsonDumpModel
            {
                types = new List<JsonDumpType>
                {
                    new() { fullName = "Game.Player", fields = new() { new() { name = "hp", type = "float", offset = "0x10" } } },
                    new() { fullName = "Game.Enemy", fields = new() { new() { name = "hp", type = "int", offset = "0x10" } } }
                }
            };

            var diff = DumpDiffer.Compare(left, right);
            Assert.True(diff.HasDifferences);
            Assert.Single(diff.types.changed);
            Assert.Equal("Game.Player", diff.types.changed[0].type);
            Assert.Null(diff.types.changed[0].image);
            Assert.Null(diff.types.changed[0].assembly);

            var drift = FieldOffsetDiff.Compare(left, right);
            Assert.False(drift.HasDrift);
        }

        [Fact]
        public void LegacyDumps_DuplicateFullNames_ExplicitlyReportsAmbiguityErrorWithExitCode2()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "legacy-ambig-" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                var legacyColliding = new JsonDumpModel
                {
                    types = new List<JsonDumpType>
                    {
                        new() { fullName = "Game.Duplicate", fields = new() { new() { name = "x", type = "int", offset = "0x10" } } },
                        new() { fullName = "Game.Duplicate", fields = new() { new() { name = "y", type = "int", offset = "0x14" } } }
                    }
                };

                var normal = new JsonDumpModel
                {
                    types = new List<JsonDumpType>
                    {
                        new() { fullName = "Game.Unique", fields = new() { new() { name = "z", type = "int", offset = "0x10" } } }
                    }
                };

                var collidingPath = Path.Combine(tempDir, "colliding.json");
                var normalPath = Path.Combine(tempDir, "normal.json");
                var diffPath = Path.Combine(tempDir, "diff.json");
                var driftPath = Path.Combine(tempDir, "drift.json");

                File.WriteAllText(collidingPath, JsonSerializer.Serialize(legacyColliding, JsonDumpGenerator.JsonOptions));
                File.WriteAllText(normalPath, JsonSerializer.Serialize(normal, JsonDumpGenerator.JsonOptions));

                // DumpDiffer: Execute must exit 2 and log ERROR
                using var diffLog = new StringWriter();
                var diffExit = DumpDiffer.Execute(collidingPath, normalPath, diffPath, diffLog);
                Assert.Equal(2, diffExit);
                Assert.Contains("ERROR: Ambiguous type collision", diffLog.ToString());
                Assert.Contains("Game.Duplicate", diffLog.ToString());

                // FieldOffsetDiff: Execute must exit 2 and log ERROR
                using var driftLog = new StringWriter();
                var driftExit = FieldOffsetDiff.Execute(collidingPath, normalPath, driftPath, driftLog);
                Assert.Equal(2, driftExit);
                Assert.Contains("ERROR: Ambiguous type collision", driftLog.ToString());
                Assert.Contains("Game.Duplicate", driftLog.ToString());

                // Calling Compare directly must throw InvalidDataException
                var exDiff = Assert.Throws<InvalidDataException>(() => DumpDiffer.Compare(collidingPath, normalPath));
                Assert.Contains("Ambiguous type collision", exDiff.Message);
                Assert.Contains("Game.Duplicate", exDiff.Message);

                var exDrift = Assert.Throws<InvalidDataException>(() => FieldOffsetDiff.Compare(legacyColliding, normal));
                Assert.Contains("Ambiguous type collision", exDrift.Message);
                Assert.Contains("Game.Duplicate", exDrift.Message);
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
        public void SameImage_DuplicateFullNames_ExplicitlyReportsAmbiguityErrorWithExitCode2()
        {
            var collidingModel = new JsonDumpModel
            {
                types = new List<JsonDumpType>
                {
                    new() { image = "Assembly-CSharp.dll", fullName = "Game.Duplicate", fields = new() { new() { name = "x", type = "int" } } },
                    new() { image = "Assembly-CSharp.dll", fullName = "Game.Duplicate", fields = new() { new() { name = "y", type = "int" } } }
                }
            };

            var ex = Assert.Throws<InvalidDataException>(() => FieldOffsetDiff.Compare(collidingModel, collidingModel));
            Assert.Contains("Ambiguous type collision", ex.Message);
            Assert.Contains("Assembly-CSharp.dll!Game.Duplicate", ex.Message);
        }

        [Fact]
        public void MixedSchema_IdenticalTypes_ProducesZeroDifferencesAndZeroDrift()
        {
            var legacy = new JsonDumpModel
            {
                types = new List<JsonDumpType>
                {
                    new()
                    {
                        fullName = "Ns.Player",
                        fields = new() { new() { name = "hp", type = "int", offset = "0x10", isStatic = false } }
                    }
                }
            };

            var scoped = new JsonDumpModel
            {
                types = new List<JsonDumpType>
                {
                    new()
                    {
                        image = "Game.dll",
                        assembly = "Game",
                        fullName = "Ns.Player",
                        fields = new() { new() { name = "hp", type = "int", offset = "0x10", isStatic = false } }
                    }
                }
            };

            var diff = DumpDiffer.Compare(legacy, scoped);
            Assert.False(diff.HasDifferences);
            Assert.Empty(diff.types.added);
            Assert.Empty(diff.types.removed);
            Assert.Empty(diff.types.changed);

            var drift = FieldOffsetDiff.Compare(legacy, scoped);
            Assert.False(drift.HasDrift);
            Assert.Empty(drift.structs);
        }

        [Fact]
        public void MixedSchema_FieldChangeAndOffsetShift_DetectsDifferencesAndDrift()
        {
            var legacy = new JsonDumpModel
            {
                types = new List<JsonDumpType>
                {
                    new()
                    {
                        fullName = "Ns.Player",
                        fields = new() { new() { name = "hp", type = "int", offset = "0x10", isStatic = false } }
                    }
                }
            };

            var scoped = new JsonDumpModel
            {
                types = new List<JsonDumpType>
                {
                    new()
                    {
                        image = "Game.dll",
                        assembly = "Game",
                        fullName = "Ns.Player",
                        fields = new() { new() { name = "hp", type = "string", offset = "0x40", isStatic = false } }
                    }
                }
            };

            var diff = DumpDiffer.Compare(legacy, scoped);
            Assert.True(diff.HasDifferences);
            Assert.Empty(diff.types.added);
            Assert.Empty(diff.types.removed);
            Assert.Single(diff.types.changed);
            Assert.Equal("Game.dll!Ns.Player", diff.types.changed[0].type);
            Assert.Equal("Game.dll", diff.types.changed[0].image);
            Assert.Equal("Game", diff.types.changed[0].assembly);
            Assert.Equal("int", diff.types.changed[0].fields.changed[0].left.type);
            Assert.Equal("string", diff.types.changed[0].fields.changed[0].right.type);

            var drift = FieldOffsetDiff.Compare(legacy, scoped);
            Assert.True(drift.HasDrift);
            Assert.Single(drift.structs);
            Assert.Equal("Game.dll!Ns.Player", drift.structs[0].@struct);
            Assert.Equal("Game.dll", drift.structs[0].image);
            Assert.Equal("Game", drift.structs[0].assembly);
            Assert.Equal("0x10", drift.structs[0].fields[0].left);
            Assert.Equal("0x40", drift.structs[0].fields[0].right);
            Assert.Equal(FieldOffsetDriftType.Shifted, drift.structs[0].fields[0].drift);
        }

        [Fact]
        public void MixedSchema_PartialAttributionAmbiguity_ThrowsInvalidDataException_ExitCode2()
        {
            var legacyUnattributed = new JsonDumpModel
            {
                types = new List<JsonDumpType>
                {
                    new() { fullName = "Ns.Player", fields = new() { new() { name = "hp", type = "int", offset = "0x10" } } }
                }
            };

            var scopedAmbiguous = new JsonDumpModel
            {
                types = new List<JsonDumpType>
                {
                    new() { image = "A.dll", assembly = "A", fullName = "Ns.Player", fields = new() { new() { name = "hp", type = "int", offset = "0x10" } } },
                    new() { image = "B.dll", assembly = "B", fullName = "Ns.Player", fields = new() { new() { name = "hp", type = "int", offset = "0x10" } } }
                }
            };

            var exDiff = Assert.Throws<InvalidDataException>(() => DumpDiffer.Compare(legacyUnattributed, scopedAmbiguous));
            Assert.Contains("Ambiguous type collision", exDiff.Message);
            Assert.Contains("Ns.Player", exDiff.Message);

            var exDrift = Assert.Throws<InvalidDataException>(() => FieldOffsetDiff.Compare(legacyUnattributed, scopedAmbiguous));
            Assert.Contains("Ambiguous type collision", exDrift.Message);
            Assert.Contains("Ns.Player", exDrift.Message);
        }

        [Fact]
        public void ExplicitAttribution_DifferentImages_DoesNotFallbackToFullName()
        {
            var left = new JsonDumpModel
            {
                types = new List<JsonDumpType>
                {
                    new()
                    {
                        image = "A.dll",
                        assembly = "A",
                        fullName = "Ns.Player",
                        fields = new() { new() { name = "hp", type = "int", offset = "0x10", isStatic = false } }
                    }
                }
            };

            var right = new JsonDumpModel
            {
                types = new List<JsonDumpType>
                {
                    new()
                    {
                        image = "B.dll",
                        assembly = "B",
                        fullName = "Ns.Player",
                        fields = new() { new() { name = "hp", type = "int", offset = "0x10", isStatic = false } }
                    }
                }
            };

            var diff = DumpDiffer.Compare(left, right);
            Assert.True(diff.HasDifferences);
            Assert.Single(diff.types.removed);
            Assert.Equal("A.dll!Ns.Player", diff.types.removed[0]);
            Assert.Single(diff.types.added);
            Assert.Equal("B.dll!Ns.Player", diff.types.added[0]);
            Assert.Empty(diff.types.changed);

            var drift = FieldOffsetDiff.Compare(left, right);
            Assert.True(drift.HasDrift);
            Assert.Equal(2, drift.structs.Count);
        }

        [Fact]
        public void SeparatorCollision_PreservesComponentBoundaries_DetectsDistinctTypes()
        {
            var left = new JsonDumpModel
            {
                types = new List<JsonDumpType>
                {
                    new()
                    {
                        image = "A.dll!B.dll",
                        assembly = "A.B",
                        fullName = "Ns.Player",
                        fields = new() { new() { name = "hp", type = "int", offset = "0x10", isStatic = false } }
                    }
                }
            };

            var right = new JsonDumpModel
            {
                types = new List<JsonDumpType>
                {
                    new()
                    {
                        image = "A.dll",
                        assembly = "A",
                        fullName = "B.dll!Ns.Player",
                        fields = new() { new() { name = "hp", type = "int", offset = "0x10", isStatic = false } }
                    }
                }
            };

            var diff = DumpDiffer.Compare(left, right);
            Assert.True(diff.HasDifferences);
            Assert.Single(diff.types.removed);
            Assert.Single(diff.types.added);
            Assert.Empty(diff.types.changed);

            var drift = FieldOffsetDiff.Compare(left, right);
            Assert.True(drift.HasDrift);
            Assert.Equal(2, drift.structs.Count);
        }

        [Fact]
        public void OrderIndependence_ExactMatchTakesPrecedenceOverPartialMatch()
        {
            var itemA = new JsonDumpType
            {
                image = "A.dll",
                assembly = "Shared",
                fullName = "Ns.Player",
                fields = new() { new() { name = "hp", type = "int", offset = "0x10", isStatic = false } }
            };

            var itemB = new JsonDumpType
            {
                image = "B.dll",
                assembly = "Shared",
                fullName = "Ns.Player",
                fields = new() { new() { name = "hp", type = "float", offset = "0x40", isStatic = false } }
            };

            var itemPartial = new JsonDumpType
            {
                image = null,
                assembly = "Shared",
                fullName = "Ns.Player",
                fields = new() { new() { name = "hp", type = "float", offset = "0x40", isStatic = false } }
            };

            var left = new JsonDumpModel { types = new List<JsonDumpType> { itemA, itemB } };
            var rightPartialFirst = new JsonDumpModel { types = new List<JsonDumpType> { itemPartial, itemA } };
            var rightExactFirst = new JsonDumpModel { types = new List<JsonDumpType> { itemA, itemPartial } };

            // 1. Partial first in list
            var diff1 = DumpDiffer.Compare(left, rightPartialFirst);
            Assert.False(diff1.HasDifferences);
            Assert.Empty(diff1.types.added);
            Assert.Empty(diff1.types.removed);
            Assert.Empty(diff1.types.changed);

            var drift1 = FieldOffsetDiff.Compare(left, rightPartialFirst);
            Assert.False(drift1.HasDrift);
            Assert.Empty(drift1.structs);

            // 2. Exact first in list
            var diff2 = DumpDiffer.Compare(left, rightExactFirst);
            Assert.False(diff2.HasDifferences);
            Assert.Empty(diff2.types.added);
            Assert.Empty(diff2.types.removed);
            Assert.Empty(diff2.types.changed);

            var drift2 = FieldOffsetDiff.Compare(left, rightExactFirst);
            Assert.False(drift2.HasDrift);
            Assert.Empty(drift2.structs);
        }

        [Fact]
        public void PartialAssemblyMatch_AmbiguousAgainstMultipleCandidates_ThrowsInvalidDataException_ExitCode2()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "partial-ambig-" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                var left = new JsonDumpModel
                {
                    types = new List<JsonDumpType>
                    {
                        new() { image = null, assembly = "Shared", fullName = "Ns.Player", fields = new() { new() { name = "hp", type = "int", offset = "0x10" } } }
                    }
                };

                var right = new JsonDumpModel
                {
                    types = new List<JsonDumpType>
                    {
                        new() { image = "A.dll", assembly = "Shared", fullName = "Ns.Player", fields = new() { new() { name = "hp", type = "int", offset = "0x10" } } },
                        new() { image = "B.dll", assembly = "Shared", fullName = "Ns.Player", fields = new() { new() { name = "hp", type = "float", offset = "0x40" } } }
                    }
                };

                var leftPath = Path.Combine(tempDir, "left.json");
                var rightPath = Path.Combine(tempDir, "right.json");
                var diffPath = Path.Combine(tempDir, "diff.json");
                var driftPath = Path.Combine(tempDir, "drift.json");

                File.WriteAllText(leftPath, JsonSerializer.Serialize(left, JsonDumpGenerator.JsonOptions));
                File.WriteAllText(rightPath, JsonSerializer.Serialize(right, JsonDumpGenerator.JsonOptions));

                using var diffLog = new StringWriter();
                var diffExit = DumpDiffer.Execute(leftPath, rightPath, diffPath, diffLog);
                Assert.Equal(2, diffExit);
                Assert.Contains("ERROR: Ambiguous type collision", diffLog.ToString());
                Assert.Contains("Ns.Player", diffLog.ToString());

                using var driftLog = new StringWriter();
                var driftExit = FieldOffsetDiff.Execute(leftPath, rightPath, driftPath, driftLog);
                Assert.Equal(2, driftExit);
                Assert.Contains("ERROR: Ambiguous type collision", driftLog.ToString());
                Assert.Contains("Ns.Player", driftLog.ToString());

                var exDiff = Assert.Throws<InvalidDataException>(() => DumpDiffer.Compare(left, right));
                Assert.Contains("Ambiguous type collision", exDiff.Message);
                Assert.Contains("Ns.Player", exDiff.Message);

                var exDrift = Assert.Throws<InvalidDataException>(() => FieldOffsetDiff.Compare(left, right));
                Assert.Contains("Ambiguous type collision", exDrift.Message);
                Assert.Contains("Ns.Player", exDrift.Message);
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
        public void PartialAssemblyMatch_Inverted_AmbiguousAgainstMultipleCandidates_ThrowsInvalidDataException_ExitCode2()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "partial-ambig-inv-" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                var left = new JsonDumpModel
                {
                    types = new List<JsonDumpType>
                    {
                        new() { image = "A.dll", assembly = "Shared", fullName = "Ns.Player", fields = new() { new() { name = "hp", type = "int", offset = "0x10" } } },
                        new() { image = "B.dll", assembly = "Shared", fullName = "Ns.Player", fields = new() { new() { name = "hp", type = "float", offset = "0x40" } } }
                    }
                };

                var right = new JsonDumpModel
                {
                    types = new List<JsonDumpType>
                    {
                        new() { image = null, assembly = "Shared", fullName = "Ns.Player", fields = new() { new() { name = "hp", type = "int", offset = "0x10" } } }
                    }
                };

                var leftPath = Path.Combine(tempDir, "left.json");
                var rightPath = Path.Combine(tempDir, "right.json");
                var diffPath = Path.Combine(tempDir, "diff.json");
                var driftPath = Path.Combine(tempDir, "drift.json");

                File.WriteAllText(leftPath, JsonSerializer.Serialize(left, JsonDumpGenerator.JsonOptions));
                File.WriteAllText(rightPath, JsonSerializer.Serialize(right, JsonDumpGenerator.JsonOptions));

                using var diffLog = new StringWriter();
                var diffExit = DumpDiffer.Execute(leftPath, rightPath, diffPath, diffLog);
                Assert.Equal(2, diffExit);
                Assert.Contains("ERROR: Ambiguous type collision", diffLog.ToString());
                Assert.Contains("Ns.Player", diffLog.ToString());

                using var driftLog = new StringWriter();
                var driftExit = FieldOffsetDiff.Execute(leftPath, rightPath, driftPath, driftLog);
                Assert.Equal(2, driftExit);
                Assert.Contains("ERROR: Ambiguous type collision", driftLog.ToString());
                Assert.Contains("Ns.Player", driftLog.ToString());

                var exDiff = Assert.Throws<InvalidDataException>(() => DumpDiffer.Compare(left, right));
                Assert.Contains("Ambiguous type collision", exDiff.Message);
                Assert.Contains("Ns.Player", exDiff.Message);

                var exDrift = Assert.Throws<InvalidDataException>(() => FieldOffsetDiff.Compare(left, right));
                Assert.Contains("Ambiguous type collision", exDrift.Message);
                Assert.Contains("Ns.Player", exDrift.Message);
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
