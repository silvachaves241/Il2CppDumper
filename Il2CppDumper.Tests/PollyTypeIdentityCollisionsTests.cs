#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Il2CppDumper.Diff;
using Xunit;
using static Il2CppDumper.Il2CppConstants;

namespace Il2CppDumper.Tests
{
    public class PollyTypeIdentityCollisionsTests
    {
        private static Metadata CreateEmptyMetadata()
        {
            var bytes = new byte[16384];
            BitConverter.GetBytes(0xFAB11BAFu).CopyTo(bytes, 0); // magic
            BitConverter.GetBytes(29).CopyTo(bytes, 4);           // version
            var m = new Metadata(new MemoryStream(bytes))
            {
                Version = 29
            };
            m.header.stringOffset = 1024;
            m.header.stringSize = 4096;
            m.header.fieldAndParameterDefaultValueDataOffset = 8192;
            m.header.fieldAndParameterDefaultValueDataSize = 1024;
            return m;
        }

        private static uint AddString(Metadata m, string value, ref uint pos)
        {
            var result = pos;
            var bytes = Encoding.UTF8.GetBytes(value + "\0");
            m.Position = m.header.stringOffset + pos;
            m.Writer.Write(bytes);
            pos += (uint)bytes.Length;
            return result;
        }

        private static (Metadata metadata, WebAssemblyMemory il2Cpp, Il2CppExecutor executor, Il2CppTypeDefinition[] typeDefs) CreatePollyEnvironment()
        {
            var m = CreateEmptyMetadata();
            uint strPos = 0;
            AddString(m, "", ref strPos);
            var imgName = AddString(m, "Polly.dll", ref strPos);
            var asmName = AddString(m, "Polly", ref strPos);
            var nsPolly = AddString(m, "Polly", ref strPos);

            var asyncPolicyName = AddString(m, "AsyncPolicy", ref strPos);
            var asyncPolicyGenericName = AddString(m, "AsyncPolicy`1", ref strPos);
            var policyBuilderName = AddString(m, "PolicyBuilder", ref strPos);
            var policyBuilderGenericName = AddString(m, "PolicyBuilder`1", ref strPos);

            var disp14GenericName = AddString(m, "<>c__DisplayClass14_0`1", ref strPos);
            var disp15GenericName = AddString(m, "<>c__DisplayClass15_0`1", ref strPos);
            var disp16GenericName = AddString(m, "<>c__DisplayClass16_0`1", ref strPos);

            var disp14Name = AddString(m, "<>c__DisplayClass14_0", ref strPos);
            var disp15Name = AddString(m, "<>c__DisplayClass15_0", ref strPos);
            var disp16Name = AddString(m, "<>c__DisplayClass16_0", ref strPos);

            var tresultParamName = AddString(m, "TResult", ref strPos);
            var fieldActionName = AddString(m, "action", ref strPos);

            m.assemblyDefs = new[]
            {
                new Il2CppAssemblyDefinition
                {
                    aname = new Il2CppAssemblyNameDefinition { nameIndex = asmName }
                }
            };

            m.imageDefs = new[]
            {
                new Il2CppImageDefinition
                {
                    nameIndex = imgName,
                    assemblyIndex = 0,
                    typeStart = 0,
                    typeCount = 12
                }
            };

            // Types:
            // 0: AsyncPolicy (non-generic), children: 2, 3, 4
            // 1: AsyncPolicy`1 (generic TResult), children: 5, 6, 7
            // 2: <>c__DisplayClass14_0`1 (in AsyncPolicy)
            // 3: <>c__DisplayClass15_0`1 (in AsyncPolicy)
            // 4: <>c__DisplayClass16_0`1 (in AsyncPolicy)
            // 5: <>c__DisplayClass14_0 (in AsyncPolicy`1)
            // 6: <>c__DisplayClass15_0 (in AsyncPolicy`1)
            // 7: <>c__DisplayClass16_0 (in AsyncPolicy`1)
            // 8: PolicyBuilder (non-generic), children: 10
            // 9: PolicyBuilder`1 (generic TResult), children: 11
            // 10: <>c__DisplayClass15_0`1 (in PolicyBuilder)
            // 11: <>c__DisplayClass15_0 (in PolicyBuilder`1)
            var typeDefs = new[]
            {
                // 0: AsyncPolicy
                new Il2CppTypeDefinition
                {
                    nameIndex = asyncPolicyName,
                    namespaceIndex = nsPolly,
                    declaringTypeIndex = -1,
                    parentIndex = -1,
                    genericContainerIndex = -1,
                    nestedTypesStart = 0,
                    nested_type_count = 3,
                    flags = TYPE_ATTRIBUTE_PUBLIC | TYPE_ATTRIBUTE_ABSTRACT
                },
                // 1: AsyncPolicy`1
                new Il2CppTypeDefinition
                {
                    nameIndex = asyncPolicyGenericName,
                    namespaceIndex = nsPolly,
                    declaringTypeIndex = -1,
                    parentIndex = -1,
                    genericContainerIndex = 0,
                    nestedTypesStart = 3,
                    nested_type_count = 3,
                    flags = TYPE_ATTRIBUTE_PUBLIC | TYPE_ATTRIBUTE_ABSTRACT
                },
                // 2: <>c__DisplayClass14_0`1 (in AsyncPolicy)
                new Il2CppTypeDefinition
                {
                    nameIndex = disp14GenericName,
                    namespaceIndex = 0,
                    declaringTypeIndex = 0,
                    parentIndex = -1,
                    genericContainerIndex = 1,
                    fieldStart = 0,
                    field_count = 1,
                    flags = TYPE_ATTRIBUTE_NESTED_PRIVATE | TYPE_ATTRIBUTE_SEALED
                },
                // 3: <>c__DisplayClass15_0`1 (in AsyncPolicy)
                new Il2CppTypeDefinition
                {
                    nameIndex = disp15GenericName,
                    namespaceIndex = 0,
                    declaringTypeIndex = 0,
                    parentIndex = -1,
                    genericContainerIndex = 2,
                    fieldStart = 1,
                    field_count = 1,
                    flags = TYPE_ATTRIBUTE_NESTED_PRIVATE | TYPE_ATTRIBUTE_SEALED
                },
                // 4: <>c__DisplayClass16_0`1 (in AsyncPolicy)
                new Il2CppTypeDefinition
                {
                    nameIndex = disp16GenericName,
                    namespaceIndex = 0,
                    declaringTypeIndex = 0,
                    parentIndex = -1,
                    genericContainerIndex = 3,
                    fieldStart = 2,
                    field_count = 1,
                    flags = TYPE_ATTRIBUTE_NESTED_PRIVATE | TYPE_ATTRIBUTE_SEALED
                },
                // 5: <>c__DisplayClass14_0 (in AsyncPolicy`1)
                new Il2CppTypeDefinition
                {
                    nameIndex = disp14Name,
                    namespaceIndex = 0,
                    declaringTypeIndex = 1,
                    parentIndex = -1,
                    genericContainerIndex = 4,
                    fieldStart = 3,
                    field_count = 1,
                    flags = TYPE_ATTRIBUTE_NESTED_PRIVATE | TYPE_ATTRIBUTE_SEALED
                },
                // 6: <>c__DisplayClass15_0 (in AsyncPolicy`1)
                new Il2CppTypeDefinition
                {
                    nameIndex = disp15Name,
                    namespaceIndex = 0,
                    declaringTypeIndex = 1,
                    parentIndex = -1,
                    genericContainerIndex = 5,
                    fieldStart = 4,
                    field_count = 1,
                    flags = TYPE_ATTRIBUTE_NESTED_PRIVATE | TYPE_ATTRIBUTE_SEALED
                },
                // 7: <>c__DisplayClass16_0 (in AsyncPolicy`1)
                new Il2CppTypeDefinition
                {
                    nameIndex = disp16Name,
                    namespaceIndex = 0,
                    declaringTypeIndex = 1,
                    parentIndex = -1,
                    genericContainerIndex = 6,
                    fieldStart = 5,
                    field_count = 1,
                    flags = TYPE_ATTRIBUTE_NESTED_PRIVATE | TYPE_ATTRIBUTE_SEALED
                },
                // 8: PolicyBuilder
                new Il2CppTypeDefinition
                {
                    nameIndex = policyBuilderName,
                    namespaceIndex = nsPolly,
                    declaringTypeIndex = -1,
                    parentIndex = -1,
                    genericContainerIndex = -1,
                    nestedTypesStart = 6,
                    nested_type_count = 1,
                    flags = TYPE_ATTRIBUTE_PUBLIC
                },
                // 9: PolicyBuilder`1
                new Il2CppTypeDefinition
                {
                    nameIndex = policyBuilderGenericName,
                    namespaceIndex = nsPolly,
                    declaringTypeIndex = -1,
                    parentIndex = -1,
                    genericContainerIndex = 7,
                    nestedTypesStart = 7,
                    nested_type_count = 1,
                    flags = TYPE_ATTRIBUTE_PUBLIC
                },
                // 10: <>c__DisplayClass15_0`1 (in PolicyBuilder)
                new Il2CppTypeDefinition
                {
                    nameIndex = disp15GenericName,
                    namespaceIndex = 0,
                    declaringTypeIndex = 8,
                    parentIndex = -1,
                    genericContainerIndex = 8,
                    fieldStart = 6,
                    field_count = 1,
                    flags = TYPE_ATTRIBUTE_NESTED_PRIVATE | TYPE_ATTRIBUTE_SEALED
                },
                // 11: <>c__DisplayClass15_0 (in PolicyBuilder`1)
                new Il2CppTypeDefinition
                {
                    nameIndex = disp15Name,
                    namespaceIndex = 0,
                    declaringTypeIndex = 9,
                    parentIndex = -1,
                    genericContainerIndex = 9,
                    fieldStart = 7,
                    field_count = 1,
                    flags = TYPE_ATTRIBUTE_NESTED_PRIVATE | TYPE_ATTRIBUTE_SEALED
                }
            };

            m.typeDefs = typeDefs;

            // Link nested types:
            // 0 (AsyncPolicy) -> [2, 3, 4]
            // 1 (AsyncPolicy`1) -> [5, 6, 7]
            // 8 (PolicyBuilder) -> [10]
            // 9 (PolicyBuilder`1) -> [11]
            m.nestedTypeIndices = new[] { 2, 3, 4, 5, 6, 7, 10, 11 };

            // Generic containers and generic parameters:
            m.genericParameters = new[]
            {
                new Il2CppGenericParameter { nameIndex = tresultParamName }
            };

            m.genericContainers = new[]
            {
                // 0: AsyncPolicy`1
                new Il2CppGenericContainer { genericParameterStart = 0, type_argc = 1 },
                // 1: <>c__DisplayClass14_0`1 in AsyncPolicy
                new Il2CppGenericContainer { genericParameterStart = 0, type_argc = 1 },
                // 2: <>c__DisplayClass15_0`1 in AsyncPolicy
                new Il2CppGenericContainer { genericParameterStart = 0, type_argc = 1 },
                // 3: <>c__DisplayClass16_0`1 in AsyncPolicy
                new Il2CppGenericContainer { genericParameterStart = 0, type_argc = 1 },
                // 4: <>c__DisplayClass14_0 in AsyncPolicy`1 (inherits container)
                new Il2CppGenericContainer { genericParameterStart = 0, type_argc = 1 },
                // 5: <>c__DisplayClass15_0 in AsyncPolicy`1 (inherits container)
                new Il2CppGenericContainer { genericParameterStart = 0, type_argc = 1 },
                // 6: <>c__DisplayClass16_0 in AsyncPolicy`1 (inherits container)
                new Il2CppGenericContainer { genericParameterStart = 0, type_argc = 1 },
                // 7: PolicyBuilder`1
                new Il2CppGenericContainer { genericParameterStart = 0, type_argc = 1 },
                // 8: <>c__DisplayClass15_0`1 in PolicyBuilder
                new Il2CppGenericContainer { genericParameterStart = 0, type_argc = 1 },
                // 9: <>c__DisplayClass15_0 in PolicyBuilder`1 (inherits container)
                new Il2CppGenericContainer { genericParameterStart = 0, type_argc = 1 }
            };

            m.fieldDefs = new[]
            {
                new Il2CppFieldDefinition { nameIndex = fieldActionName, typeIndex = 12, token = 0x04000001 },
                new Il2CppFieldDefinition { nameIndex = fieldActionName, typeIndex = 12, token = 0x04000002 },
                new Il2CppFieldDefinition { nameIndex = fieldActionName, typeIndex = 12, token = 0x04000003 },
                new Il2CppFieldDefinition { nameIndex = fieldActionName, typeIndex = 12, token = 0x04000004 },
                new Il2CppFieldDefinition { nameIndex = fieldActionName, typeIndex = 12, token = 0x04000005 },
                new Il2CppFieldDefinition { nameIndex = fieldActionName, typeIndex = 12, token = 0x04000006 },
                new Il2CppFieldDefinition { nameIndex = fieldActionName, typeIndex = 12, token = 0x04000007 },
                new Il2CppFieldDefinition { nameIndex = fieldActionName, typeIndex = 12, token = 0x04000008 }
            };

            m.methodDefs = Array.Empty<Il2CppMethodDefinition>();
            m.parameterDefs = Array.Empty<Il2CppParameterDefinition>();
            m.propertyDefs = Array.Empty<Il2CppPropertyDefinition>();

            var il2Cpp = new WebAssemblyMemory(new MemoryStream(new byte[8192]), 0)
            {
                Version = 16,
                methodPointers = Array.Empty<ulong>()
            };

            var typesList = new List<Il2CppType>();
            for (int i = 0; i < 12; i++)
            {
                typesList.Add(new Il2CppType
                {
                    type = Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                    data = new Il2CppType.Union { dummy = (ulong)i }
                });
            }
            // Field type 12 (int)
            typesList.Add(new Il2CppType { type = Il2CppTypeEnum.IL2CPP_TYPE_I4 });

            il2Cpp.types = typesList.ToArray();

            var executor = new Il2CppExecutor(m, il2Cpp);
            return (m, il2Cpp, executor, typeDefs);
        }

        [Fact]
        public void PollyCollisions_CanonicalNamesAreDistinct_AndNoCollisions()
        {
            var (m, il2Cpp, executor, typeDefs) = CreatePollyEnvironment();
            using (m)
            using (il2Cpp)
            {
                var names = typeDefs.Select(t => executor.GetTypeDefName(t, true, true)).ToList();

                Assert.Equal("Polly.AsyncPolicy", names[0]);
                Assert.Equal("Polly.AsyncPolicy<TResult>", names[1]);

                Assert.Equal("Polly.AsyncPolicy.<>c__DisplayClass14_0<TResult>", names[2]);
                Assert.Equal("Polly.AsyncPolicy.<>c__DisplayClass15_0<TResult>", names[3]);
                Assert.Equal("Polly.AsyncPolicy.<>c__DisplayClass16_0<TResult>", names[4]);

                Assert.Equal("Polly.AsyncPolicy<TResult>.<>c__DisplayClass14_0", names[5]);
                Assert.Equal("Polly.AsyncPolicy<TResult>.<>c__DisplayClass15_0", names[6]);
                Assert.Equal("Polly.AsyncPolicy<TResult>.<>c__DisplayClass16_0", names[7]);

                Assert.Equal("Polly.PolicyBuilder", names[8]);
                Assert.Equal("Polly.PolicyBuilder<TResult>", names[9]);

                Assert.Equal("Polly.PolicyBuilder.<>c__DisplayClass15_0<TResult>", names[10]);
                Assert.Equal("Polly.PolicyBuilder<TResult>.<>c__DisplayClass15_0", names[11]);

                // All 12 names must be distinct
                var uniqueNames = names.Distinct().ToList();
                Assert.Equal(12, uniqueNames.Count);
            }
        }

        [Fact]
        public void PollyDump_JsonDumpGenerator_StructureAndSelfDiff_SucceedsWithZeroChanges()
        {
            var (m, il2Cpp, executor, _) = CreatePollyEnvironment();
            using (m)
            using (il2Cpp)
            {
                var config = new Config
                {
                    DumpField = true,
                    DumpFieldOffset = false,
                    DumpAttribute = false,
                    DumpMethod = false,
                    DumpProperty = false
                };

                var model = JsonDumpGenerator.Create(executor, config);
                Assert.NotNull(model);

                // Root types must be only the non-nested types: AsyncPolicy, AsyncPolicy<TResult>, PolicyBuilder, PolicyBuilder<TResult>
                Assert.Equal(4, model.types.Count);
                Assert.Contains(model.types, t => t.fullName == "Polly.AsyncPolicy");
                Assert.Contains(model.types, t => t.fullName == "Polly.AsyncPolicy<TResult>");
                Assert.Contains(model.types, t => t.fullName == "Polly.PolicyBuilder");
                Assert.Contains(model.types, t => t.fullName == "Polly.PolicyBuilder<TResult>");

                var asyncPolicy = model.types.First(t => t.fullName == "Polly.AsyncPolicy");
                Assert.Equal(3, asyncPolicy.nested.Count);
                Assert.Contains(asyncPolicy.nested, n => n.fullName == "Polly.AsyncPolicy.<>c__DisplayClass14_0<TResult>");
                Assert.Contains(asyncPolicy.nested, n => n.fullName == "Polly.AsyncPolicy.<>c__DisplayClass15_0<TResult>");
                Assert.Contains(asyncPolicy.nested, n => n.fullName == "Polly.AsyncPolicy.<>c__DisplayClass16_0<TResult>");

                var asyncPolicyGeneric = model.types.First(t => t.fullName == "Polly.AsyncPolicy<TResult>");
                Assert.Equal(3, asyncPolicyGeneric.nested.Count);
                Assert.Contains(asyncPolicyGeneric.nested, n => n.fullName == "Polly.AsyncPolicy<TResult>.<>c__DisplayClass14_0");
                Assert.Contains(asyncPolicyGeneric.nested, n => n.fullName == "Polly.AsyncPolicy<TResult>.<>c__DisplayClass15_0");
                Assert.Contains(asyncPolicyGeneric.nested, n => n.fullName == "Polly.AsyncPolicy<TResult>.<>c__DisplayClass16_0");

                var policyBuilder = model.types.First(t => t.fullName == "Polly.PolicyBuilder");
                Assert.Single(policyBuilder.nested);
                Assert.Equal("Polly.PolicyBuilder.<>c__DisplayClass15_0<TResult>", policyBuilder.nested[0].fullName);

                var policyBuilderGeneric = model.types.First(t => t.fullName == "Polly.PolicyBuilder<TResult>");
                Assert.Single(policyBuilderGeneric.nested);
                Assert.Equal("Polly.PolicyBuilder<TResult>.<>c__DisplayClass15_0", policyBuilderGeneric.nested[0].fullName);

                // Self comparison must succeed with zero changes and no ambiguous collision exception
                var diffResult = DumpDiffer.Compare(model, model);
                Assert.NotNull(diffResult);
                Assert.False(diffResult.HasDifferences);
                Assert.Empty(diffResult.types.added);
                Assert.Empty(diffResult.types.removed);
                Assert.Empty(diffResult.types.changed);
            }
        }

        private static void RegisterTypeInTypeDic(Il2Cpp il2Cpp, ulong pointer, Il2CppType type)
        {
            var field = typeof(Il2Cpp).GetField("typeDic", BindingFlags.Instance | BindingFlags.NonPublic);
            var dict = (Dictionary<ulong, Il2CppType>)field!.GetValue(il2Cpp)!;
            dict[pointer] = type;
        }

        private static ulong CreateGenericInstanceInMemory(
            WebAssemblyMemory il2Cpp,
            uint baseAddr,
            int targetTypeDefIndex,
            ulong[] typeArgPointers)
        {
            var stream = (MemoryStream)typeof(BinaryStream)
                .GetField("stream", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(il2Cpp)!;

            var writer = new BinaryWriter(stream);

            // 1. Write argv array (each pointer is 4 bytes in 32-bit)
            var argvAddr = baseAddr;
            stream.Position = argvAddr;
            foreach (var ptr in typeArgPointers)
            {
                writer.Write((uint)ptr);
            }
            var pos = (uint)stream.Position;
            if ((pos % 4) != 0) pos += 4 - (pos % 4);

            // 2. Write Il2CppGenericInst
            var instAddr = pos;
            stream.Position = instAddr;
            writer.Write((int)typeArgPointers.Length); // type_argc
            writer.Write((uint)argvAddr);              // type_argv
            pos = (uint)stream.Position;
            if ((pos % 4) != 0) pos += 4 - (pos % 4);

            // 3. Write Il2CppGenericClass
            var classAddr = pos;
            stream.Position = classAddr;
            writer.Write((int)targetTypeDefIndex);     // typeDefinitionIndex
            writer.Write((uint)instAddr);              // context.class_inst
            writer.Write((uint)0);                     // context.method_inst
            writer.Write((uint)0);                     // cached_class

            return classAddr;
        }

        [Fact]
        public void PollyDump_CrossBuildDiff_DetectsFieldReferenceSwap_BetweenCorrectedGenericClosures()
        {
            // Point 1: A reference swap between AsyncPolicy.<>c__DisplayClass14_0<int>
            // and AsyncPolicy<int>.<>c__DisplayClass14_0 in metadata field typeIndex
            // must be detected by DumpDiffer.
            var tempDir = Path.Combine(Path.GetTempPath(), "polly-field-swap-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                // Left build: field references AsyncPolicy.<>c__DisplayClass14_0<int> (TypeDef 2)
                var (m1, il2Cpp1, exec1, _) = CreatePollyEnvironment();
                JsonDumpModel left;
                using (m1) using (il2Cpp1)
                {
                    const ulong intPtr = 0x1000;
                    RegisterTypeInTypeDic(il2Cpp1, intPtr, il2Cpp1.types[12]); // int type

                    var classAddr1 = CreateGenericInstanceInMemory(il2Cpp1, 2048, 2, new[] { intPtr });
                    var genType1 = new Il2CppType
                    {
                        type = Il2CppTypeEnum.IL2CPP_TYPE_GENERICINST,
                        data = new Il2CppType.Union { dummy = classAddr1 }
                    };

                    var types1 = il2Cpp1.types.ToList();
                    types1.Add(genType1);
                    il2Cpp1.types = types1.ToArray();

                    // Point field 0 to type 13
                    m1.fieldDefs[0].typeIndex = 13;

                    // Verify GetTypeName formatting
                    var leftFieldTypeName = exec1.GetTypeName(genType1, false, false);
                    Assert.Equal("AsyncPolicy.<>c__DisplayClass14_0<int>", leftFieldTypeName);

                    left = JsonDumpGenerator.Create(exec1, new Config { DumpField = true });
                }

                // Right build: field references AsyncPolicy<int>.<>c__DisplayClass14_0 (TypeDef 5)
                var (m2, il2Cpp2, exec2, _) = CreatePollyEnvironment();
                JsonDumpModel right;
                using (m2) using (il2Cpp2)
                {
                    const ulong intPtr = 0x1000;
                    RegisterTypeInTypeDic(il2Cpp2, intPtr, il2Cpp2.types[12]); // int type

                    var classAddr2 = CreateGenericInstanceInMemory(il2Cpp2, 2048, 5, new[] { intPtr });
                    var genType2 = new Il2CppType
                    {
                        type = Il2CppTypeEnum.IL2CPP_TYPE_GENERICINST,
                        data = new Il2CppType.Union { dummy = classAddr2 }
                    };

                    var types2 = il2Cpp2.types.ToList();
                    types2.Add(genType2);
                    il2Cpp2.types = types2.ToArray();

                    // Point field 0 to type 13
                    m2.fieldDefs[0].typeIndex = 13;

                    // Verify GetTypeName formatting
                    var rightFieldTypeName = exec2.GetTypeName(genType2, false, false);
                    Assert.Equal("AsyncPolicy<int>.<>c__DisplayClass14_0", rightFieldTypeName);

                    right = JsonDumpGenerator.Create(exec2, new Config { DumpField = true });
                }

                var leftPath = Path.Combine(tempDir, "left.json");
                var rightPath = Path.Combine(tempDir, "right.json");
                var diffPath = Path.Combine(tempDir, "diff.json");

                File.WriteAllText(leftPath, JsonSerializer.Serialize(left, JsonDumpGenerator.JsonOptions));
                File.WriteAllText(rightPath, JsonSerializer.Serialize(right, JsonDumpGenerator.JsonOptions));

                using var sw = new StringWriter();
                var exitCode = DumpDiffer.Execute(leftPath, rightPath, diffPath, sw);

                Assert.Equal(1, exitCode); // Differences must be found!

                var diff = JsonSerializer.Deserialize<DumpDiffResult>(File.ReadAllText(diffPath));
                Assert.NotNull(diff);
                Assert.True(diff.HasDifferences);
                Assert.Single(diff.types.changed);

                var changedType = diff.types.changed[0];
                Assert.Equal("Polly.dll!Polly.AsyncPolicy.<>c__DisplayClass14_0<TResult>", changedType.type);
                Assert.Single(changedType.fields.changed);
                Assert.Equal("action", changedType.fields.changed[0].field);
                Assert.Equal("AsyncPolicy.<>c__DisplayClass14_0<int>", changedType.fields.changed[0].left.type);
                Assert.Equal("AsyncPolicy<int>.<>c__DisplayClass14_0", changedType.fields.changed[0].right.type);
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
        public void PollyDump_DivergentMetadata_UsesUnifiedParentResolution_AndAvoidsArbitraryOrder()
        {
            // Point 2: If a child is listed in multiple parents' nestedTypeIndices:
            // Case A: declaringTypeIndex is valid -> binds strictly to declaringTypeIndex parent, not read order!
            var (mA, il2CppA, execA, typeDefsA) = CreatePollyEnvironment();
            using (mA) using (il2CppA)
            {
                typeDefsA[0].nested_type_count = 4;
                mA.nestedTypeIndices = new[] { 2, 3, 4, 5, 5, 6, 7, 10, 11 };
                typeDefsA[1].nestedTypesStart = 4; // points to [5, 6, 7]

                var parent = execA.GetParentTypeDefinition(typeDefsA[5]);
                Assert.Same(typeDefsA[1], parent); // Must resolve to declaring parent (AsyncPolicy<TResult>), NOT first traversed (AsyncPolicy)!

                var model = JsonDumpGenerator.Create(execA, new Config());
                var p0 = model.types.First(t => t.fullName == "Polly.AsyncPolicy");
                var p1 = model.types.First(t => t.fullName == "Polly.AsyncPolicy<TResult>");

                // Child 5 must be nested under p1, NOT p0
                Assert.DoesNotContain(p0.nested, n => n.fullName == "Polly.AsyncPolicy<TResult>.<>c__DisplayClass14_0");
                Assert.Contains(p1.nested, n => n.fullName == "Polly.AsyncPolicy<TResult>.<>c__DisplayClass14_0");
            }

            // Case B: declaringTypeIndex == -1 and multiple candidate parents exist (unresolved conflict)
            // Emits structured warning via CoreLogger (captured by API consumer) and returns null
            var (mB, il2CppB, execB, typeDefsB) = CreatePollyEnvironment();
            using (mB) using (il2CppB)
            {
                typeDefsB[5].declaringTypeIndex = -1; // No declared parent
                typeDefsB[0].nested_type_count = 4;
                mB.nestedTypeIndices = new[] { 2, 3, 4, 5, 5, 6, 7, 10, 11 };
                typeDefsB[1].nestedTypesStart = 4;

                var warnings = new List<string>();
                using var sw = new StringWriter();
                JsonDumpModel model;
                using (CoreLogger.Scope(sw, warnings))
                {
                    var parent = execB.GetParentTypeDefinition(typeDefsB[5]);
                    Assert.Null(parent); // Ambiguous conflict without declared parent -> returns null!

                    model = JsonDumpGenerator.Create(execB, new Config());
                }

                // Child 5 becomes root type, not adopted by arbitrary order
                var p0 = model.types.First(t => t.fullName == "Polly.AsyncPolicy");
                var p1 = model.types.First(t => t.fullName == "Polly.AsyncPolicy<TResult>");
                Assert.DoesNotContain(p1.nested, n => n.fullName.Contains("DisplayClass14_0"));
                Assert.Contains(model.types, t => t.fullName == "<>c__DisplayClass14_0<TResult>");

                // Warning must be captured in CoreLogger and API writer exactly once for this definition
                Assert.Single(warnings);
                Assert.Contains("WARNING: Ambiguous declaring type for nested type '<>c__DisplayClass14_0'", warnings[0]);
                Assert.Contains("AsyncPolicy", warnings[0]);
                Assert.Contains("AsyncPolicy`1", warnings[0]);
                Assert.Contains(warnings[0], sw.ToString());
            }
        }

        [Fact]
        public void PollyDump_ParentCycle_RejectsCycleAndFallsBackToAcyclicCandidate_PreservingDefinitionCount()
        {
            // Point 1: When declaringTypeIndex of def 5 points to itself (def 5),
            // but nestedTypeIndices table indicates def 1 (AsyncPolicy<TResult>) as parent:
            // The shared resolver rejects the cycle, falls back to the unique acyclic candidate (def 1),
            // emits a warning on CoreLogger, and the recursive definition count is preserved at 12 (does NOT drop to 11).
            var (m, il2Cpp, exec, typeDefs) = CreatePollyEnvironment();
            using (m) using (il2Cpp)
            {
                typeDefs[5].declaringTypeIndex = 5; // Mutate to point to itself!

                var warnings = new List<string>();
                using var sw = new StringWriter();
                JsonDumpModel model;
                using (CoreLogger.Scope(sw, warnings))
                {
                    var parent = exec.GetParentTypeDefinition(typeDefs[5]);
                    Assert.Same(typeDefs[1], parent); // Must reject self-cycle and fall back to def 1!

                    model = JsonDumpGenerator.Create(exec, new Config());
                }

                static int CountTypes(IEnumerable<JsonDumpType> types)
                {
                    int count = 0;
                    foreach (var t in types)
                    {
                        count += 1 + CountTypes(t.nested);
                    }
                    return count;
                }

                Assert.Equal(12, CountTypes(model.types)); // Must NOT drop to 11!

                var p1 = model.types.First(t => t.fullName == "Polly.AsyncPolicy<TResult>");
                Assert.Contains(p1.nested, n => n.fullName == "Polly.AsyncPolicy<TResult>.<>c__DisplayClass14_0");

                // Warning logged via CoreLogger
                Assert.Single(warnings);
                Assert.Contains("WARNING: Cyclic declaring type detected for '<>c__DisplayClass14_0'", warnings[0]);
                Assert.Contains("Falling back to candidate parent 'AsyncPolicy`1'", warnings[0]);
                Assert.Contains(warnings[0], sw.ToString());

                // Self-diff succeeds with 0 differences
                var diff = DumpDiffer.Compare(model, model);
                Assert.False(diff.HasDifferences);
            }
        }

        [Fact]
        public void PollyDump_ParentCycle_WithNoAcyclicCandidate_PreservesDefinitionAsRootWithDiagnostic()
        {
            // Point 1: If def 5 points to itself and has NO candidate in nestedTypeIndices:
            // It is preserved as an unparented root definition with a warning, and total count is still 12.
            var (m, il2Cpp, exec, typeDefs) = CreatePollyEnvironment();
            using (m) using (il2Cpp)
            {
                typeDefs[5].declaringTypeIndex = 5; // self-cycle
                typeDefs[1].nestedTypesStart = 4;
                typeDefs[1].nested_type_count = 2; // only [6, 7], removed 5

                var warnings = new List<string>();
                using var sw = new StringWriter();
                JsonDumpModel model;
                using (CoreLogger.Scope(sw, warnings))
                {
                    var parent = exec.GetParentTypeDefinition(typeDefs[5]);
                    Assert.Null(parent);

                    model = JsonDumpGenerator.Create(exec, new Config());
                }

                static int CountTypes(IEnumerable<JsonDumpType> types)
                {
                    int count = 0;
                    foreach (var t in types)
                    {
                        count += 1 + CountTypes(t.nested);
                    }
                    return count;
                }

                Assert.Equal(12, CountTypes(model.types)); // Definition 5 is preserved as root, count is 12!
                Assert.Contains(model.types, t => t.fullName.Contains("<>c__DisplayClass14_0"));

                Assert.Single(warnings);
                Assert.Contains("WARNING: Cyclic declaring type detected for '<>c__DisplayClass14_0'", warnings[0]);
                Assert.Contains("Preserving as unparented root definition", warnings[0]);
                Assert.Contains(warnings[0], sw.ToString());
            }
        }

                [Fact]
        public void PollyDump_NonGenericTypeWithBacktickInName_PreservesLiteralName_WithoutCollision()
        {
            // Point 3: A non-generic definition named AsyncPolicy`Literal (genericContainerIndex = -1)
            // must retain its literal name completely and not collide with AsyncPolicy.
            var m = CreateEmptyMetadata();
            uint strPos = 0;
            AddString(m, "", ref strPos);
            var imgName = AddString(m, "Polly.dll", ref strPos);
            var asmName = AddString(m, "Polly", ref strPos);
            var nsPolly = AddString(m, "Polly", ref strPos);

            var asyncPolicyName = AddString(m, "AsyncPolicy", ref strPos);
            var asyncPolicyLiteralName = AddString(m, "AsyncPolicy`Literal", ref strPos);

            m.assemblyDefs = new[]
            {
                new Il2CppAssemblyDefinition
                {
                    aname = new Il2CppAssemblyNameDefinition { nameIndex = asmName }
                }
            };

            m.imageDefs = new[]
            {
                new Il2CppImageDefinition
                {
                    nameIndex = imgName,
                    assemblyIndex = 0,
                    typeStart = 0,
                    typeCount = 2
                }
            };

            var typeDefs = new[]
            {
                new Il2CppTypeDefinition
                {
                    nameIndex = asyncPolicyName,
                    namespaceIndex = nsPolly,
                    declaringTypeIndex = -1,
                    genericContainerIndex = -1,
                    flags = TYPE_ATTRIBUTE_PUBLIC
                },
                new Il2CppTypeDefinition
                {
                    nameIndex = asyncPolicyLiteralName,
                    namespaceIndex = nsPolly,
                    declaringTypeIndex = -1,
                    genericContainerIndex = -1, // Non-generic!
                    flags = TYPE_ATTRIBUTE_PUBLIC
                }
            };
            m.typeDefs = typeDefs;
            m.nestedTypeIndices = Array.Empty<int>();
            m.genericContainers = Array.Empty<Il2CppGenericContainer>();
            m.genericParameters = Array.Empty<Il2CppGenericParameter>();
            m.methodDefs = Array.Empty<Il2CppMethodDefinition>();
            m.parameterDefs = Array.Empty<Il2CppParameterDefinition>();
            m.propertyDefs = Array.Empty<Il2CppPropertyDefinition>();
            m.fieldDefs = Array.Empty<Il2CppFieldDefinition>();

            var il2Cpp = new WebAssemblyMemory(new MemoryStream(new byte[1024]), 0)
            {
                Version = 16,
                methodPointers = Array.Empty<ulong>(),
                types = new[]
                {
                    new Il2CppType { type = Il2CppTypeEnum.IL2CPP_TYPE_CLASS, data = new Il2CppType.Union { dummy = 0 } },
                    new Il2CppType { type = Il2CppTypeEnum.IL2CPP_TYPE_CLASS, data = new Il2CppType.Union { dummy = 1 } }
                }
            };

            using (m)
            using (il2Cpp)
            {
                var executor = new Il2CppExecutor(m, il2Cpp);

                var name0 = executor.GetTypeDefName(typeDefs[0], true, true);
                var name1 = executor.GetTypeDefName(typeDefs[1], true, true);

                Assert.Equal("Polly.AsyncPolicy", name0);
                Assert.Equal("Polly.AsyncPolicy`Literal", name1);
                Assert.NotEqual(name0, name1); // Must not collide!

                var model = JsonDumpGenerator.Create(executor, new Config());
                Assert.Equal(2, model.types.Count);
                Assert.Contains(model.types, t => t.fullName == "Polly.AsyncPolicy");
                Assert.Contains(model.types, t => t.fullName == "Polly.AsyncPolicy`Literal");

                // Self-diff must succeed with zero differences and no collisions
                var diff = DumpDiffer.Compare(model, model);
                Assert.False(diff.HasDifferences);
                Assert.Empty(diff.types.added);
                Assert.Empty(diff.types.removed);
                Assert.Empty(diff.types.changed);
            }
        }

                [Fact]
        public void NestedType_DeclaringTypeIndexMinusOne_ResolvesParentFromNestedTypeIndices()
        {
            var m = CreateEmptyMetadata();
            uint strPos = 0;
            AddString(m, "", ref strPos);
            var imgName = AddString(m, "Assembly.dll", ref strPos);
            var parentName = AddString(m, "ParentClass`1", ref strPos);
            var childName = AddString(m, "ChildClass", ref strPos);
            var tParamName = AddString(m, "T", ref strPos);

            m.imageDefs = new[]
            {
                new Il2CppImageDefinition { nameIndex = imgName, assemblyIndex = -1, typeStart = 0, typeCount = 2 }
            };

            m.typeDefs = new[]
            {
                new Il2CppTypeDefinition
                {
                    nameIndex = parentName,
                    namespaceIndex = 0,
                    declaringTypeIndex = -1,
                    parentIndex = -1,
                    genericContainerIndex = 0,
                    nestedTypesStart = 0,
                    nested_type_count = 1,
                    flags = TYPE_ATTRIBUTE_PUBLIC
                },
                new Il2CppTypeDefinition
                {
                    nameIndex = childName,
                    namespaceIndex = 0,
                    declaringTypeIndex = -1, // declaringTypeIndex is -1 in metadata!
                    parentIndex = -1,
                    genericContainerIndex = -1,
                    flags = TYPE_ATTRIBUTE_NESTED_PUBLIC
                }
            };

            m.nestedTypeIndices = new[] { 1 };
            m.genericContainers = new[]
            {
                new Il2CppGenericContainer { genericParameterStart = 0, type_argc = 1 }
            };
            m.genericParameters = new[]
            {
                new Il2CppGenericParameter { nameIndex = tParamName }
            };

            m.methodDefs = Array.Empty<Il2CppMethodDefinition>();
            m.parameterDefs = Array.Empty<Il2CppParameterDefinition>();
            m.propertyDefs = Array.Empty<Il2CppPropertyDefinition>();
            m.fieldDefs = Array.Empty<Il2CppFieldDefinition>();

            var il2Cpp = new WebAssemblyMemory(new MemoryStream(new byte[8192]), 0)
            {
                Version = 16,
                methodPointers = Array.Empty<ulong>(),
                types = new[]
                {
                    new Il2CppType { type = Il2CppTypeEnum.IL2CPP_TYPE_CLASS, data = new Il2CppType.Union { dummy = 0 } },
                    new Il2CppType { type = Il2CppTypeEnum.IL2CPP_TYPE_CLASS, data = new Il2CppType.Union { dummy = 1 } }
                }
            };

            using (m)
            using (il2Cpp)
            {
                var executor = new Il2CppExecutor(m, il2Cpp);
                var childTypeDef = m.typeDefs[1];

                var fullName = executor.GetTypeDefName(childTypeDef, true, true);
                Assert.Equal("ParentClass<T>.ChildClass", fullName);
            }
        }

        [Fact]
        public void DeeplyNestedGenericTypes_ResolvesFullCanonicalPath()
        {
            var m = CreateEmptyMetadata();
            uint strPos = 0;
            AddString(m, "", ref strPos);
            var imgName = AddString(m, "Assembly.dll", ref strPos);
            var nsName = AddString(m, "TestNs", ref strPos);
            var outerName = AddString(m, "Outer`1", ref strPos);
            var middleName = AddString(m, "Middle`1", ref strPos);
            var innerName = AddString(m, "Inner`1", ref strPos);
            var paramT = AddString(m, "T", ref strPos);
            var paramU = AddString(m, "U", ref strPos);
            var paramV = AddString(m, "V", ref strPos);

            m.imageDefs = new[]
            {
                new Il2CppImageDefinition { nameIndex = imgName, assemblyIndex = -1, typeStart = 0, typeCount = 3 }
            };

            m.typeDefs = new[]
            {
                // 0: Outer`1
                new Il2CppTypeDefinition
                {
                    nameIndex = outerName,
                    namespaceIndex = nsName,
                    declaringTypeIndex = -1,
                    genericContainerIndex = 0,
                    nestedTypesStart = 0,
                    nested_type_count = 1,
                    flags = TYPE_ATTRIBUTE_PUBLIC
                },
                // 1: Middle`1
                new Il2CppTypeDefinition
                {
                    nameIndex = middleName,
                    namespaceIndex = 0,
                    declaringTypeIndex = 0,
                    genericContainerIndex = 1,
                    nestedTypesStart = 1,
                    nested_type_count = 1,
                    flags = TYPE_ATTRIBUTE_NESTED_PUBLIC
                },
                // 2: Inner`1
                new Il2CppTypeDefinition
                {
                    nameIndex = innerName,
                    namespaceIndex = 0,
                    declaringTypeIndex = 1,
                    genericContainerIndex = 2,
                    flags = TYPE_ATTRIBUTE_NESTED_PUBLIC
                }
            };

            m.nestedTypeIndices = new[] { 1, 2 };
            m.genericContainers = new[]
            {
                new Il2CppGenericContainer { genericParameterStart = 0, type_argc = 1 }, // T
                new Il2CppGenericContainer { genericParameterStart = 0, type_argc = 2 }, // T, U
                new Il2CppGenericContainer { genericParameterStart = 0, type_argc = 3 }  // T, U, V
            };
            m.genericParameters = new[]
            {
                new Il2CppGenericParameter { nameIndex = paramT },
                new Il2CppGenericParameter { nameIndex = paramU },
                new Il2CppGenericParameter { nameIndex = paramV }
            };

            m.methodDefs = Array.Empty<Il2CppMethodDefinition>();
            m.parameterDefs = Array.Empty<Il2CppParameterDefinition>();
            m.propertyDefs = Array.Empty<Il2CppPropertyDefinition>();
            m.fieldDefs = Array.Empty<Il2CppFieldDefinition>();

            var il2Cpp = new WebAssemblyMemory(new MemoryStream(new byte[8192]), 0)
            {
                Version = 16,
                methodPointers = Array.Empty<ulong>(),
                types = new[]
                {
                    new Il2CppType { type = Il2CppTypeEnum.IL2CPP_TYPE_CLASS, data = new Il2CppType.Union { dummy = 0 } },
                    new Il2CppType { type = Il2CppTypeEnum.IL2CPP_TYPE_CLASS, data = new Il2CppType.Union { dummy = 1 } },
                    new Il2CppType { type = Il2CppTypeEnum.IL2CPP_TYPE_CLASS, data = new Il2CppType.Union { dummy = 2 } }
                }
            };

            using (m)
            using (il2Cpp)
            {
                var executor = new Il2CppExecutor(m, il2Cpp);

                Assert.Equal("TestNs.Outer<T>", executor.GetTypeDefName(m.typeDefs[0], true, true));
                Assert.Equal("TestNs.Outer<T>.Middle<U>", executor.GetTypeDefName(m.typeDefs[1], true, true));
                Assert.Equal("TestNs.Outer<T>.Middle<U>.Inner<V>", executor.GetTypeDefName(m.typeDefs[2], true, true));
            }
        }
    }
}
