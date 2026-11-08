using System;
using System.IO;
using System.Linq;
using System.Text;
using Il2CppDumper.Tests.Synthetic;
using Mono.Cecil;
using Xunit;

namespace Il2CppDumper.Tests
{
    public class NestedTypeTableWidthTests
    {
        private const int NestedTypesSectionHeaderOffset = 8 + 15 * 12; // 188

        private static byte[] CreateV39BufferWithNestedTypes(byte[] nestedTypeBytes, int sectionSize, int count, int offsetAdjustment = 0)
        {
            var baseBytes = SyntheticMetadataGenerator.Generate(MetadataVersionFamily.V39, seed: 42);
            var appendOffset = baseBytes.Length + offsetAdjustment;
            var totalLength = baseBytes.Length + (nestedTypeBytes?.Length ?? 0);
            var buffer = new byte[Math.Max(baseBytes.Length, totalLength)];
            Buffer.BlockCopy(baseBytes, 0, buffer, 0, baseBytes.Length);

            if (nestedTypeBytes != null && nestedTypeBytes.Length > 0 && appendOffset >= 0 && (appendOffset + nestedTypeBytes.Length) <= buffer.Length)
            {
                Buffer.BlockCopy(nestedTypeBytes, 0, buffer, appendOffset, nestedTypeBytes.Length);
            }

            BitConverter.GetBytes(appendOffset).CopyTo(buffer, NestedTypesSectionHeaderOffset);
            BitConverter.GetBytes(sectionSize).CopyTo(buffer, NestedTypesSectionHeaderOffset + 4);
            BitConverter.GetBytes(count).CopyTo(buffer, NestedTypesSectionHeaderOffset + 8);

            return buffer;
        }

        [Fact]
        public void SyntheticV39_FourByteEntries_DecodesIndicesCorrectly_ReproducingAndFixingIssue87()
        {
            // Reproducer for Issue #87:
            // In v39 metadata where typeDefinitions.count < 65535 (here count = 2),
            // typeDefinitionIndexSize was previously computed as 1 (or 2).
            // Passing typeDefinitionIndexSize to ReadMetadataIndexArray caused 4-byte entries [0, 1]
            // to be read as 1-byte entries [0, 0], corrupting type ownership.
            // Deriving width from sectionSize / count correctly identifies width = 4 and reads [0, 1].
            var payload = new byte[8];
            BitConverter.GetBytes(0).CopyTo(payload, 0);
            BitConverter.GetBytes(1).CopyTo(payload, 4);

            var buffer = CreateV39BufferWithNestedTypes(payload, sectionSize: 8, count: 2);
            using var metadata = new Metadata(new MemoryStream(buffer));

            Assert.Equal(2, metadata.typeDefs.Length);
            Assert.NotNull(metadata.nestedTypeIndices);
            Assert.Equal(2, metadata.nestedTypeIndices.Length);
            Assert.Equal(0, metadata.nestedTypeIndices[0]);
            Assert.Equal(1, metadata.nestedTypeIndices[1]);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(4)]
        public void SupportedWidths_1_2_4_DecodeCorrectly(int width)
        {
            int count = 3;
            var payload = new byte[count * width];
            int[] expected = { 1, 2, 3 };

            for (int i = 0; i < count; i++)
            {
                var val = expected[i];
                if (width == 1)
                {
                    payload[i] = (byte)val;
                }
                else if (width == 2)
                {
                    BitConverter.GetBytes((ushort)val).CopyTo(payload, i * 2);
                }
                else
                {
                    BitConverter.GetBytes(val).CopyTo(payload, i * 4);
                }
            }

            var buffer = CreateV39BufferWithNestedTypes(payload, sectionSize: count * width, count: count);
            using var metadata = new Metadata(new MemoryStream(buffer));

            Assert.NotNull(metadata.nestedTypeIndices);
            Assert.Equal(expected, metadata.nestedTypeIndices);
        }

        [Fact]
        public void V39_EmptySection_ReturnsEmptyArray()
        {
            var buffer = CreateV39BufferWithNestedTypes(Array.Empty<byte>(), sectionSize: 0, count: 0);
            using var metadata = new Metadata(new MemoryStream(buffer));

            Assert.NotNull(metadata.nestedTypeIndices);
            Assert.Empty(metadata.nestedTypeIndices);
        }

        [Fact]
        public void InconsistentExtent_CountZero_PositiveSectionSize_ThrowsInvalidDataException()
        {
            var buffer = CreateV39BufferWithNestedTypes(new byte[8], sectionSize: 8, count: 0);
            var ex = Assert.Throws<InvalidDataException>(() => new Metadata(new MemoryStream(buffer)));
            Assert.Contains("nestedTypes", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void InconsistentExtent_CountPositive_ZeroSectionSize_ThrowsInvalidDataException()
        {
            var buffer = CreateV39BufferWithNestedTypes(new byte[8], sectionSize: 0, count: 2);
            var ex = Assert.Throws<InvalidDataException>(() => new Metadata(new MemoryStream(buffer)));
            Assert.Contains("nestedTypes", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void InconsistentExtent_NonDivisibleSectionSize_ThrowsInvalidDataException()
        {
            var buffer = CreateV39BufferWithNestedTypes(new byte[7], sectionSize: 7, count: 2);
            var ex = Assert.Throws<InvalidDataException>(() => new Metadata(new MemoryStream(buffer)));
            Assert.Contains("nestedTypes", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData(3)]
        [InlineData(5)]
        [InlineData(8)]
        public void UnsupportedEntryWidth_ThrowsInvalidDataException(int width)
        {
            int count = 2;
            int sectionSize = count * width;
            var buffer = CreateV39BufferWithNestedTypes(new byte[sectionSize], sectionSize: sectionSize, count: count);
            var ex = Assert.Throws<InvalidDataException>(() => new Metadata(new MemoryStream(buffer)));
            Assert.Contains("nestedTypes", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void OutOfBoundsSection_ThrowsInvalidDataException()
        {
            // Section offset + sectionSize extends beyond stream length
            var buffer = CreateV39BufferWithNestedTypes(new byte[8], sectionSize: 8, count: 2, offsetAdjustment: 100);
            var ex = Assert.Throws<InvalidDataException>(() => new Metadata(new MemoryStream(buffer)));
            Assert.Contains("nestedTypes", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void NegativeSectionOffset_ThrowsInvalidDataException()
        {
            var baseBytes = SyntheticMetadataGenerator.Generate(MetadataVersionFamily.V39, seed: 42);
            var buffer = (byte[])baseBytes.Clone();
            BitConverter.GetBytes(-1).CopyTo(buffer, NestedTypesSectionHeaderOffset);
            BitConverter.GetBytes(4).CopyTo(buffer, NestedTypesSectionHeaderOffset + 4);
            BitConverter.GetBytes(1).CopyTo(buffer, NestedTypesSectionHeaderOffset + 8);

            var ex = Assert.Throws<InvalidDataException>(() => new Metadata(new MemoryStream(buffer)));
            Assert.Contains("nestedTypes", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void NegativeSectionCount_ThrowsInvalidDataException()
        {
            var baseBytes = SyntheticMetadataGenerator.Generate(MetadataVersionFamily.V39, seed: 42);
            var buffer = (byte[])baseBytes.Clone();
            BitConverter.GetBytes(baseBytes.Length).CopyTo(buffer, NestedTypesSectionHeaderOffset);
            BitConverter.GetBytes(4).CopyTo(buffer, NestedTypesSectionHeaderOffset + 4);
            BitConverter.GetBytes(-1).CopyTo(buffer, NestedTypesSectionHeaderOffset + 8);

            var ex = Assert.Throws<InvalidDataException>(() => new Metadata(new MemoryStream(buffer)));
            Assert.Contains("nestedTypes", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void LegacyMetadata_ValidSection_ReadsCorrectly()
        {
            // Build v24 buffer with nestedTypesOffset at 128, nestedTypesSize at 132
            var buffer = new byte[2048];
            BitConverter.GetBytes(0xFAB11BAFu).CopyTo(buffer, 0); // sanity
            BitConverter.GetBytes(24).CopyTo(buffer, 4);           // version 24

            uint nestedTypesOffset = 512;
            int nestedTypesSize = 8; // 2 * 4 bytes
            BitConverter.GetBytes(nestedTypesOffset).CopyTo(buffer, 128);
            BitConverter.GetBytes(nestedTypesSize).CopyTo(buffer, 132);

            BitConverter.GetBytes(10).CopyTo(buffer, (int)nestedTypesOffset);
            BitConverter.GetBytes(20).CopyTo(buffer, (int)nestedTypesOffset + 4);

            using var metadata = new Metadata(new MemoryStream(buffer));
            Assert.NotNull(metadata.nestedTypeIndices);
            Assert.Equal(new[] { 10, 20 }, metadata.nestedTypeIndices);
        }

        [Fact]
        public void LegacyMetadata_EmptySection_ReturnsEmptyArray()
        {
            var buffer = new byte[2048];
            BitConverter.GetBytes(0xFAB11BAFu).CopyTo(buffer, 0);
            BitConverter.GetBytes(24).CopyTo(buffer, 4);

            BitConverter.GetBytes(512u).CopyTo(buffer, 128);
            BitConverter.GetBytes(0).CopyTo(buffer, 132); // size = 0

            using var metadata = new Metadata(new MemoryStream(buffer));
            Assert.NotNull(metadata.nestedTypeIndices);
            Assert.Empty(metadata.nestedTypeIndices);
        }

        [Fact]
        public void LegacyMetadata_NonMultipleOfFour_ThrowsInvalidDataException()
        {
            var buffer = new byte[2048];
            BitConverter.GetBytes(0xFAB11BAFu).CopyTo(buffer, 0);
            BitConverter.GetBytes(24).CopyTo(buffer, 4);

            BitConverter.GetBytes(512u).CopyTo(buffer, 128);
            BitConverter.GetBytes(6).CopyTo(buffer, 132); // size = 6, not multiple of 4

            var ex = Assert.Throws<InvalidDataException>(() => new Metadata(new MemoryStream(buffer)));
            Assert.Contains("legacy nestedTypes size", ex.Message);
        }

        [Fact]
        public void LegacyMetadata_OutOfBounds_ThrowsInvalidDataException()
        {
            var buffer = new byte[512];
            BitConverter.GetBytes(0xFAB11BAFu).CopyTo(buffer, 0);
            BitConverter.GetBytes(24).CopyTo(buffer, 4);

            BitConverter.GetBytes(500u).CopyTo(buffer, 128);
            BitConverter.GetBytes(32).CopyTo(buffer, 132); // 500 + 32 > 512

            var ex = Assert.Throws<InvalidDataException>(() => new Metadata(new MemoryStream(buffer)));
            Assert.Contains("Legacy nestedTypes table", ex.Message);
        }

        [Fact]
        public void ExportVerification_NestedTypeAttachedProperlyInGeneratedAssembly()
        {
            // Verify that with correctly resolved nestedTypeIndices, DummyAssemblyExporter
            // attaches the child type to the parent's NestedTypes collection and sets DeclaringType.
            var bytes = new byte[4096];
            BitConverter.GetBytes(0xFAB11BAFu).CopyTo(bytes, 0);
            BitConverter.GetBytes(29).CopyTo(bytes, 4);

            var metadata = new Metadata(new MemoryStream(bytes));
            metadata.header.stringOffset = 1024;
            metadata.header.stringSize = 2048;

            uint strPos = 0;
            AddString(metadata, "", ref strPos);
            var imgName = AddString(metadata, "TestAssembly.dll", ref strPos);
            var parentName = AddString(metadata, "ParentClass", ref strPos);
            var childName = AddString(metadata, "ChildClass", ref strPos);

            metadata.imageDefs = new[]
            {
                new Il2CppImageDefinition { nameIndex = imgName, assemblyIndex = -1, typeStart = 0, typeCount = 2 }
            };

            metadata.typeDefs = new[]
            {
                new Il2CppTypeDefinition
                {
                    nameIndex = parentName,
                    declaringTypeIndex = -1,
                    parentIndex = -1,
                    genericContainerIndex = -1,
                    method_count = 0,
                    property_count = 0,
                    nestedTypesStart = 0,
                    nested_type_count = 1,
                    flags = (uint)TypeAttributes.Public
                },
                new Il2CppTypeDefinition
                {
                    nameIndex = childName,
                    declaringTypeIndex = -1,
                    parentIndex = -1,
                    genericContainerIndex = -1,
                    method_count = 0,
                    property_count = 0,
                    nestedTypesStart = -1,
                    nested_type_count = 0,
                    flags = (uint)TypeAttributes.NestedPublic
                }
            };

            metadata.methodDefs = Array.Empty<Il2CppMethodDefinition>();
            metadata.parameterDefs = Array.Empty<Il2CppParameterDefinition>();
            metadata.propertyDefs = Array.Empty<Il2CppPropertyDefinition>();

            // nestedTypeIndices points entry 0 to typeDef 1 (ChildClass)
            metadata.nestedTypeIndices = new[] { 1 };

            var il2Cpp = new WebAssemblyMemory(new MemoryStream(new byte[64]), 0)
            {
                Version = 16,
                methodPointers = Array.Empty<ulong>(),
                types = new[]
                {
                    new Il2CppType { type = Il2CppTypeEnum.IL2CPP_TYPE_VOID },
                    new Il2CppType { type = Il2CppTypeEnum.IL2CPP_TYPE_VOID }
                }
            };

            var executor = new Il2CppExecutor(metadata, il2Cpp);

            var tempDir = Path.Combine(Path.GetTempPath(), "Il2CppDumper_Issue87_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                DummyAssemblyExporter.Export(executor, tempDir, false);

                var dllPath = Path.Combine(tempDir, "DummyDll", "TestAssembly.dll");
                Assert.True(File.Exists(dllPath), $"Expected generated DLL at {dllPath}");

                using var assembly = AssemblyDefinition.ReadAssembly(dllPath);
                var module = assembly.MainModule;

                var parentType = module.Types.FirstOrDefault(t => t.Name == "ParentClass");
                Assert.NotNull(parentType);

                // ChildClass must be attached to ParentClass.NestedTypes and not directly in module.Types
                Assert.Single(parentType.NestedTypes);
                var nestedChild = parentType.NestedTypes[0];
                Assert.Equal("ChildClass", nestedChild.Name);
                Assert.Equal(parentType, nestedChild.DeclaringType);

                Assert.DoesNotContain(module.Types, t => t.Name == "ChildClass");
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
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
    }
}
