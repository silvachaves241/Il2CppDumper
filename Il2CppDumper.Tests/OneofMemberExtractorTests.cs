#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Xunit;
using static Il2CppDumper.Il2CppConstants;

namespace Il2CppDumper.Tests
{
    public class OneofMemberExtractorTests
    {
        private class TestEnv : IDisposable
        {
            public Metadata M { get; set; } = null!;
            public WebAssemblyMemory Il { get; set; } = null!;
            public Il2CppExecutor Executor { get; set; } = null!;

            public void Dispose()
            {
                M?.Dispose();
                Il?.Dispose();
            }
        }

        private static Metadata CreateTestMetadata(int version = 24)
        {
            var bytes = new byte[32768];
            BitConverter.GetBytes(0xFAB11BAFu).CopyTo(bytes, 0);
            BitConverter.GetBytes(version).CopyTo(bytes, 4);
            var m = new Metadata(new MemoryStream(bytes));
            m.header.stringOffset = 1024;
            m.header.stringSize = 8192;
            m.header.fieldAndParameterDefaultValueDataOffset = 16384;
            return m;
        }

        private static uint AddString(Metadata m, string value, ref uint pos)
        {
            var result = pos;
            var bytes = Encoding.UTF8.GetBytes(value + "\0");
            m.Position = (ulong)(m.header.stringOffset + pos);
            m.Writer.Write(bytes);
            pos += (uint)bytes.Length;
            return result;
        }

        private static TestEnv CreateProtobufEnvironment(
            string className,
            (string name, int? constTag, Il2CppTypeEnum type, ulong offset, bool isStatic)[] fields,
            bool isProtobuf = true)
        {
            var m = CreateTestMetadata();
            uint pos = 0;
            AddString(m, "", ref pos);
            var image = AddString(m, "Protocol.dll", ref pos);
            var iface = AddString(m, "Google.Protobuf.IMessage", ref pos);
            var classStr = AddString(m, className, ref pos);

            var fieldNameIndices = new uint[fields.Length];
            for (int i = 0; i < fields.Length; i++)
            {
                fieldNameIndices[i] = AddString(m, fields[i].name, ref pos);
            }

            m.imageDefs = new[] { new Il2CppImageDefinition { nameIndex = image, typeCount = isProtobuf ? 2u : 1u } };

            if (isProtobuf)
            {
                m.typeDefs = new[]
                {
                    new Il2CppTypeDefinition
                    {
                        nameIndex = iface,
                        declaringTypeIndex = -1,
                        parentIndex = -1,
                        genericContainerIndex = -1,
                        flags = (uint)TYPE_ATTRIBUTE_INTERFACE
                    },
                    new Il2CppTypeDefinition
                    {
                        nameIndex = classStr,
                        declaringTypeIndex = -1,
                        parentIndex = -1,
                        genericContainerIndex = -1,
                        interfacesStart = 0,
                        interfaces_count = (ushort)1,
                        fieldStart = 0,
                        field_count = (ushort)fields.Length,
                        flags = 1
                    }
                };
                m.interfaceIndices = new[] { 0 };
            }
            else
            {
                m.typeDefs = new[]
                {
                    new Il2CppTypeDefinition
                    {
                        nameIndex = classStr,
                        declaringTypeIndex = -1,
                        parentIndex = -1,
                        genericContainerIndex = -1,
                        interfacesStart = -1,
                        interfaces_count = 0,
                        fieldStart = 0,
                        field_count = (ushort)fields.Length,
                        flags = 1
                    }
                };
            }

            m.fieldDefs = new Il2CppFieldDefinition[fields.Length];
            var defaultVals = new Dictionary<int, Il2CppFieldDefaultValue>();
            int dataOffset = 0;

            for (int i = 0; i < fields.Length; i++)
            {
                var f = fields[i];
                var typeIndex = i + 1; // index 0 is interface type
                m.fieldDefs[i] = new Il2CppFieldDefinition
                {
                    nameIndex = fieldNameIndices[i],
                    typeIndex = typeIndex
                };

                if (f.constTag.HasValue)
                {
                    m.Position = (ulong)(m.header.fieldAndParameterDefaultValueDataOffset + dataOffset);
                    m.Writer.Write(f.constTag.Value);
                    defaultVals[i] = new Il2CppFieldDefaultValue
                    {
                        fieldIndex = i,
                        typeIndex = typeIndex,
                        dataIndex = dataOffset
                    };
                    dataOffset += 4;
                }
            }

            typeof(Metadata).GetField("fieldDefaultValuesDic", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(m, defaultVals);

            var il = new WebAssemblyMemory(new MemoryStream(new byte[64]), 0);
            il.Version = 24;

            var ilTypes = new List<Il2CppType>
            {
                // type 0: interface Google.Protobuf.IMessage (pointing to typeDefs[0])
                new()
                {
                    type = Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                    datapoint = 0,
                    data = new Il2CppType.Union { dummy = 0 }
                }
            };

            var offsets = new List<ulong>();
            for (int i = 0; i < fields.Length; i++)
            {
                var f = fields[i];
                uint attrs = f.isStatic ? (uint)FIELD_ATTRIBUTE_STATIC : (uint)FIELD_ATTRIBUTE_PRIVATE;
                if (f.constTag.HasValue)
                {
                    attrs |= (uint)(FIELD_ATTRIBUTE_PUBLIC | FIELD_ATTRIBUTE_STATIC | FIELD_ATTRIBUTE_LITERAL);
                }
                var t = new Il2CppType
                {
                    type = f.type,
                    attrs = attrs,
                    datapoint = 0,
                    data = new Il2CppType.Union { dummy = 0 }
                };
                ilTypes.Add(t);
                offsets.Add(f.offset);
            }

            il.types = ilTypes.ToArray();
            typeof(Il2Cpp).GetField("fieldOffsets", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(il, offsets.ToArray());
            typeof(Il2Cpp).GetField("fieldOffsetsArePointers", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(il, false);

            var executor = new Il2CppExecutor(m, il);
            return new TestEnv { M = m, Il = il, Executor = executor };
        }

        [Fact]
        public void OneofExtraction_MatchesIssue68_ProofShape_ClassJnj()
        {
            // Issue #68 proof shape: class `jnj`
            // consts 1, 6, 9, 11, 15 followed by a single `private object eptp; // 0x68`
            var fields = new (string name, int? constTag, Il2CppTypeEnum type, ulong offset, bool isStatic)[]
            {
                ("eptk", 1, Il2CppTypeEnum.IL2CPP_TYPE_I4, 0, true),
                ("f6", 6, Il2CppTypeEnum.IL2CPP_TYPE_I4, 0, true),
                ("f9", 9, Il2CppTypeEnum.IL2CPP_TYPE_I4, 0, true),
                ("f11", 11, Il2CppTypeEnum.IL2CPP_TYPE_I4, 0, true),
                ("f15", 15, Il2CppTypeEnum.IL2CPP_TYPE_I4, 0, true),
                ("eptp", null, Il2CppTypeEnum.IL2CPP_TYPE_OBJECT, 0x68, false),
                ("eptpCase_", null, Il2CppTypeEnum.IL2CPP_TYPE_I4, 0x70, false)
            };

            using var env = CreateProtobufEnvironment("jnj", fields, isProtobuf: true);
            var extractor = new OneofMemberExtractor(env.Executor);
            var config = new Config { GenerateOneofMap = true };

            var tempDir = Path.Combine(Path.GetTempPath(), "il2cpp-issue-68-repro-" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                extractor.Write(tempDir, config);

                Assert.Equal(1, extractor.ClassesCount);
                Assert.Equal(5, extractor.OneofTagsCount);
                Assert.Equal(0, extractor.AmbiguitiesCount);

                var membersPath = Path.Combine(tempDir, OneofMemberExtractor.FileName);
                Assert.True(File.Exists(membersPath));

                var ambiguitiesPath = Path.Combine(tempDir, OneofMemberExtractor.AmbiguityFileName);
                Assert.True(File.Exists(ambiguitiesPath));

                var json = File.ReadAllText(membersPath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                Assert.True(root.TryGetProperty("jnj", out var jnj));
                Assert.True(jnj.TryGetProperty("group", out var group));
                Assert.Equal("eptp", group.GetProperty("property").GetString());
                Assert.Equal("0x68", group.GetProperty("offset").GetString());

                Assert.True(jnj.TryGetProperty("members", out var members));
                Assert.Equal(5, members.GetArrayLength());

                var tags = new List<int>();
                foreach (var member in members.EnumerateArray())
                {
                    tags.Add(member.GetProperty("tag").GetInt32());
                }
                Assert.Equal(new[] { 1, 6, 9, 11, 15 }, tags);

                var firstMember = members[0];
                Assert.Equal(1, firstMember.GetProperty("tag").GetInt32());
                var constFields = firstMember.GetProperty("constantFields");
                Assert.Equal(1, constFields.GetArrayLength());
                Assert.Equal("eptk", constFields[0].GetString());

                var ambiguitiesJson = File.ReadAllText(ambiguitiesPath);
                using var ambDoc = JsonDocument.Parse(ambiguitiesJson);
                Assert.Equal(0, ambDoc.RootElement.GetArrayLength());
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public void OneofExtraction_NormalProtobufFields_ProducesZeroOneofs_AndZeroAmbiguities()
        {
            var fields = new (string name, int? constTag, Il2CppTypeEnum type, ulong offset, bool isStatic)[]
            {
                ("FooFieldNumber", 1, Il2CppTypeEnum.IL2CPP_TYPE_I4, 0, true),
                ("foo_", null, Il2CppTypeEnum.IL2CPP_TYPE_I4, 0x10, false),
                ("BarFieldNumber", 2, Il2CppTypeEnum.IL2CPP_TYPE_I4, 0, true),
                ("bar_", null, Il2CppTypeEnum.IL2CPP_TYPE_STRING, 0x18, false)
            };

            using var env = CreateProtobufEnvironment("NormalMsg", fields, isProtobuf: true);
            var extractor = new OneofMemberExtractor(env.Executor);
            var result = extractor.Extract(new Config { GenerateOneofMap = true });

            Assert.Equal(1, result.IMessageClassesScanned);
            Assert.Empty(result.Classes);
            Assert.Empty(result.Ambiguities);
        }

        [Fact]
        public void OneofExtraction_MultipleObjectFields_ReportsToAmbiguityChannel()
        {
            var fields = new (string name, int? constTag, Il2CppTypeEnum type, ulong offset, bool isStatic)[]
            {
                ("c1", 1, Il2CppTypeEnum.IL2CPP_TYPE_I4, 0, true),
                ("obj1", null, Il2CppTypeEnum.IL2CPP_TYPE_OBJECT, 0x10, false),
                ("c2", 2, Il2CppTypeEnum.IL2CPP_TYPE_I4, 0, true),
                ("obj2", null, Il2CppTypeEnum.IL2CPP_TYPE_OBJECT, 0x18, false)
            };

            using var env = CreateProtobufEnvironment("AmbiguousMultipleObjects", fields, isProtobuf: true);
            var extractor = new OneofMemberExtractor(env.Executor);
            var result = extractor.Extract(new Config { GenerateOneofMap = true });

            Assert.Empty(result.Classes);
            Assert.NotEmpty(result.Ambiguities);
            Assert.Contains(result.Ambiguities, a => a.Reason.Contains("multiple object fields"));
        }

        [Fact]
        public void OneofExtraction_ConstRunFollowedByNonObjectField_ReportsToAmbiguityChannel()
        {
            var fields = new (string name, int? constTag, Il2CppTypeEnum type, ulong offset, bool isStatic)[]
            {
                ("c1", 1, Il2CppTypeEnum.IL2CPP_TYPE_I4, 0, true),
                ("c2", 2, Il2CppTypeEnum.IL2CPP_TYPE_I4, 0, true),
                ("intField", null, Il2CppTypeEnum.IL2CPP_TYPE_I4, 0x10, false)
            };

            using var env = CreateProtobufEnvironment("AmbiguousNonObjectRun", fields, isProtobuf: true);
            var extractor = new OneofMemberExtractor(env.Executor);
            var result = extractor.Extract(new Config { GenerateOneofMap = true });

            Assert.Empty(result.Classes);
            Assert.NotEmpty(result.Ambiguities);
            Assert.Contains(result.Ambiguities, a => a.Reason.Contains("const run of 2 tags"));
        }

        [Fact]
        public void OneofExtraction_TrailingConsts_ReportsToAmbiguityChannel()
        {
            var fields = new (string name, int? constTag, Il2CppTypeEnum type, ulong offset, bool isStatic)[]
            {
                ("c1", 1, Il2CppTypeEnum.IL2CPP_TYPE_I4, 0, true)
            };

            using var env = CreateProtobufEnvironment("TrailingConstsClass", fields, isProtobuf: true);
            var extractor = new OneofMemberExtractor(env.Executor);
            var result = extractor.Extract(new Config { GenerateOneofMap = true });

            Assert.Empty(result.Classes);
            Assert.NotEmpty(result.Ambiguities);
            Assert.Contains(result.Ambiguities, a => a.Reason.Contains("trailing consts"));
        }

        [Fact]
        public void OneofExtraction_MultipleConstsWithSameTag_GroupIntoConstantFields()
        {
            var fields = new (string name, int? constTag, Il2CppTypeEnum type, ulong offset, bool isStatic)[]
            {
                ("tag1_a", 1, Il2CppTypeEnum.IL2CPP_TYPE_I4, 0, true),
                ("tag1_b", 1, Il2CppTypeEnum.IL2CPP_TYPE_I4, 0, true),
                ("objStorage", null, Il2CppTypeEnum.IL2CPP_TYPE_OBJECT, 0x20, false)
            };

            using var env = CreateProtobufEnvironment("DuplicateTagClass", fields, isProtobuf: true);
            var extractor = new OneofMemberExtractor(env.Executor);
            var result = extractor.Extract(new Config { GenerateOneofMap = true });

            Assert.Single(result.Classes);
            var container = result.Classes["DuplicateTagClass"];
            Assert.Single(container.Members);
            Assert.Equal(1, container.Members[0].Tag);
            Assert.Equal(new[] { "tag1_a", "tag1_b" }, container.Members[0].ConstantFields);
            Assert.Empty(result.Ambiguities);
        }

        [Fact]
        public void OneofExtraction_NonProtobufClass_IsSkippedByScan()
        {
            var fields = Array.Empty<(string name, int? constTag, Il2CppTypeEnum type, ulong offset, bool isStatic)>();
            using var env = CreateProtobufEnvironment("PlainClass", fields, isProtobuf: false);
            var extractor = new OneofMemberExtractor(env.Executor);
            var result = extractor.Extract(new Config { GenerateOneofMap = true }, onlyProtobufMessages: true);

            Assert.Equal(0, result.IMessageClassesScanned);
            Assert.Empty(result.Classes);
            Assert.Empty(result.Ambiguities);
        }
    }
}
