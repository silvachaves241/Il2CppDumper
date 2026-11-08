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
        public void V39_MultiImageParentChild_ExportVerification_PreservesOwnership()
        {
            // Regression test for Issue #87:
            // Constructs a real v39 metadata buffer containing 2 images (ImageA.dll, ImageB.dll),
            // 4 type definitions (ParentA/ChildA in ImageA, ParentB/ChildB in ImageB), and a 4-byte
            // nestedTypes table containing [1, 3] (sectionSize = 8, count = 2).
            // On the unfixed base code, typeDefinitionIndexSize evaluates to 1 (since 4 < 255),
            // causing nestedTypes to be read as [1, 0]. ParentB is mistakenly linked to ParentA (from ImageA),
            // ChildB is left unattached, and ownership assertions fail.
            // On the fixed head, width is correctly resolved to 4 from sectionSize / count, nestedTypes is [1, 3],
            // and re-opened assemblies verify exact parent-child attachment, declaring types, and absence from root.
            var buffer = CreateV39MultiImageBufferWithNestedTypes(new[] { 1, 3 });

            using var metadata = new Metadata(new MemoryStream(buffer));

            // Verify that metadata parsed nestedTypeIndices directly from stream without manual intervention
            Assert.Equal(new[] { 1, 3 }, metadata.nestedTypeIndices);

            var il2Cpp = new SyntheticIl2Cpp(version: 39);
            var executor = new Il2CppExecutor(metadata, il2Cpp);

            var tempDir = Path.Combine(Path.GetTempPath(), "Il2CppDumper_Issue87_MultiImage_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                DummyAssemblyExporter.Export(executor, tempDir, false);

                var dllA = Path.Combine(tempDir, "DummyDll", "ImageA.dll");
                var dllB = Path.Combine(tempDir, "DummyDll", "ImageB.dll");
                Assert.True(File.Exists(dllA), $"Expected generated DLL at {dllA}");
                Assert.True(File.Exists(dllB), $"Expected generated DLL at {dllB}");

                // Verify ImageA.dll
                using (var assemblyA = AssemblyDefinition.ReadAssembly(dllA))
                {
                    var moduleA = assemblyA.MainModule;
                    var parentA = moduleA.Types.FirstOrDefault(t => t.Name == "ParentA");
                    Assert.NotNull(parentA);
                    Assert.Single(parentA.NestedTypes);
                    var childA = parentA.NestedTypes[0];
                    Assert.Equal("ChildA", childA.Name);
                    Assert.Equal(parentA, childA.DeclaringType);
                    Assert.DoesNotContain(moduleA.Types, t => t.Name == "ChildA");
                }

                // Verify ImageB.dll
                using (var assemblyB = AssemblyDefinition.ReadAssembly(dllB))
                {
                    var moduleB = assemblyB.MainModule;
                    var parentB = moduleB.Types.FirstOrDefault(t => t.Name == "ParentB");
                    Assert.NotNull(parentB);
                    Assert.Single(parentB.NestedTypes);
                    var childB = parentB.NestedTypes[0];
                    Assert.Equal("ChildB", childB.Name);
                    Assert.Equal(parentB, childB.DeclaringType);
                    Assert.DoesNotContain(moduleB.Types, t => t.Name == "ChildB");
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

        private static byte[] CreateV39MultiImageBufferWithNestedTypes(int[] nestedTypes)
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms, Encoding.UTF8);

            // String table
            using var stringMs = new MemoryStream();
            using var stringWriter = new BinaryWriter(stringMs, Encoding.UTF8);
            var stringOffsetMap = new System.Collections.Generic.Dictionary<string, uint>();

            uint GetOrAddString(string val)
            {
                if (!stringOffsetMap.TryGetValue(val, out var offset))
                {
                    offset = (uint)stringMs.Position;
                    var bytes = Encoding.UTF8.GetBytes(val);
                    stringWriter.Write(bytes);
                    stringWriter.Write((byte)0);
                    stringOffsetMap[val] = offset;
                }
                return offset;
            }

            GetOrAddString(string.Empty);
            var asmA = GetOrAddString("AssemblyA");
            var asmB = GetOrAddString("AssemblyB");
            var imgA = GetOrAddString("ImageA.dll");
            var imgB = GetOrAddString("ImageB.dll");
            var ns = GetOrAddString("TestNamespace");
            var parentAName = GetOrAddString("ParentA");
            var childAName = GetOrAddString("ChildA");
            var parentBName = GetOrAddString("ParentB");
            var childBName = GetOrAddString("ChildB");

            var stringBytes = stringMs.ToArray();

            // Nested types payload: 4 bytes per entry
            var nestedTypesBytes = new byte[nestedTypes.Length * 4];
            for (int i = 0; i < nestedTypes.Length; i++)
            {
                BitConverter.GetBytes(nestedTypes[i]).CopyTo(nestedTypesBytes, i * 4);
            }

            static void WriteMetadataIndexLocal(BinaryWriter w, int value, int size)
            {
                if (size == 4)
                    w.Write(value);
                else if (size == 2)
                    w.Write(value == -1 ? ushort.MaxValue : (ushort)value);
                else
                    w.Write(value == -1 ? byte.MaxValue : (byte)value);
            }

            // Type definitions (4 types: ParentA=0, ChildA=1, ParentB=2, ChildB=3)
            byte[] typeDefsBytes;
            using (var tMs = new MemoryStream())
            using (var tWriter = new BinaryWriter(tMs, Encoding.UTF8))
            {
                void WriteType(uint nameIdx, int nestedTypesStart, ushort nestedTypeCount, uint flags)
                {
                    tWriter.Write(nameIdx);
                    tWriter.Write(ns);
                    WriteMetadataIndexLocal(tWriter, 0, 4); // byvalTypeIndex
                    WriteMetadataIndexLocal(tWriter, -1, 4); // declaringTypeIndex
                    WriteMetadataIndexLocal(tWriter, -1, 4); // parentIndex
                    WriteMetadataIndexLocal(tWriter, -1, 1); // genericContainerIndex
                    tWriter.Write(flags);
                    WriteMetadataIndexLocal(tWriter, -1, 4); // fieldStart
                    WriteMetadataIndexLocal(tWriter, -1, 4); // methodStart
                    WriteMetadataIndexLocal(tWriter, -1, 4); // eventStart
                    WriteMetadataIndexLocal(tWriter, -1, 4); // propertyStart
                    WriteMetadataIndexLocal(tWriter, nestedTypesStart, 4); // nestedTypesStart
                    WriteMetadataIndexLocal(tWriter, -1, 4); // interfacesStart
                    tWriter.Write(-1); // vtableStart
                    WriteMetadataIndexLocal(tWriter, -1, 4); // interfaceOffsetsStart
                    tWriter.Write((ushort)0); // method_count
                    tWriter.Write((ushort)0); // property_count
                    tWriter.Write((ushort)0); // field_count
                    tWriter.Write((ushort)0); // event_count
                    tWriter.Write(nestedTypeCount); // nested_type_count
                    tWriter.Write((ushort)0); // vtable_count
                    tWriter.Write((ushort)0); // interfaces_count
                    tWriter.Write((ushort)0); // interface_offsets_count
                    tWriter.Write(0u); // bitfield
                    tWriter.Write(0u); // token
                }

                // Type 0: ParentA (nestedTypesStart: 0, count: 1)
                WriteType(parentAName, nestedTypesStart: 0, nestedTypeCount: 1, flags: 0x00000001u); // Public
                // Type 1: ChildA (nestedTypesStart: -1, count: 0)
                WriteType(childAName, nestedTypesStart: -1, nestedTypeCount: 0, flags: 0x00000002u); // NestedPublic
                // Type 2: ParentB (nestedTypesStart: 1, count: 1)
                WriteType(parentBName, nestedTypesStart: 1, nestedTypeCount: 1, flags: 0x00000001u); // Public
                // Type 3: ChildB (nestedTypesStart: -1, count: 0)
                WriteType(childBName, nestedTypesStart: -1, nestedTypeCount: 0, flags: 0x00000002u); // NestedPublic

                typeDefsBytes = tMs.ToArray();
            }

            // Images (2 images: ImageA.dll=0, ImageB.dll=1)
            byte[] imagesBytes;
            using (var iMs = new MemoryStream())
            using (var iWriter = new BinaryWriter(iMs, Encoding.UTF8))
            {
                // Image 0: ImageA.dll -> types 0..1 (typeStart: 0, typeCount: 2)
                iWriter.Write(imgA);
                iWriter.Write(0); // assemblyIndex
                WriteMetadataIndexLocal(iWriter, 0, 1); // typeStart
                iWriter.Write(2u); // typeCount
                WriteMetadataIndexLocal(iWriter, -1, 1); // exportedTypeStart
                iWriter.Write(0u); // exportedTypeCount
                WriteMetadataIndexLocal(iWriter, -1, 4); // entryPointIndex
                iWriter.Write(1u); // token
                iWriter.Write(0); // customAttributeStart
                iWriter.Write(0u); // customAttributeCount

                // Image 1: ImageB.dll -> types 2..3 (typeStart: 2, typeCount: 2)
                iWriter.Write(imgB);
                iWriter.Write(1); // assemblyIndex
                WriteMetadataIndexLocal(iWriter, 2, 1); // typeStart
                iWriter.Write(2u); // typeCount
                WriteMetadataIndexLocal(iWriter, -1, 1); // exportedTypeStart
                iWriter.Write(0u); // exportedTypeCount
                WriteMetadataIndexLocal(iWriter, -1, 4); // entryPointIndex
                iWriter.Write(2u); // token
                iWriter.Write(0); // customAttributeStart
                iWriter.Write(0u); // customAttributeCount

                imagesBytes = iMs.ToArray();
            }

            // Assemblies (2 assemblies: AssemblyA=0, AssemblyB=1)
            byte[] assembliesBytes;
            using (var aMs = new MemoryStream())
            using (var aWriter = new BinaryWriter(aMs, Encoding.UTF8))
            {
                var asmA_def = new Il2CppAssemblyDefinition
                {
                    imageIndex = 0,
                    token = 0x20000001,
                    unusedCustomAttributeIndex = -1,
                    referencedAssemblyStart = -1,
                    referencedAssemblyCount = 0,
                    aname = new Il2CppAssemblyNameDefinition
                    {
                        nameIndex = asmA,
                        cultureIndex = 0,
                        publicKeyIndex = 0,
                        hash_alg = 0,
                        hash_len = 0,
                        flags = 0,
                        major = 1,
                        minor = 0,
                        build = 0,
                        revision = 0,
                        public_key_token = new byte[8]
                    },
                    moduleToken = 0x00000001
                };
                var asmB_def = new Il2CppAssemblyDefinition
                {
                    imageIndex = 1,
                    token = 0x20000002,
                    unusedCustomAttributeIndex = -1,
                    referencedAssemblyStart = -1,
                    referencedAssemblyCount = 0,
                    aname = new Il2CppAssemblyNameDefinition
                    {
                        nameIndex = asmB,
                        cultureIndex = 0,
                        publicKeyIndex = 0,
                        hash_alg = 0,
                        hash_len = 0,
                        flags = 0,
                        major = 1,
                        minor = 0,
                        build = 0,
                        revision = 0,
                        public_key_token = new byte[8]
                    },
                    moduleToken = 0x00000002
                };
                SyntheticMetadataGenerator.WriteClass(aWriter, asmA_def, 39);
                SyntheticMetadataGenerator.WriteClass(aWriter, asmB_def, 39);
                assembliesBytes = aMs.ToArray();
            }

            // Header size: 4 magic + 4 version + 31 * 12 sections = 380 bytes
            uint currentOffset = 380;

            uint stringsOffset = currentOffset;
            int stringsSize = stringBytes.Length;
            currentOffset += (uint)stringsSize;

            uint nestedTypesOffset = currentOffset;
            int nestedTypesSize = nestedTypesBytes.Length;
            currentOffset += (uint)nestedTypesSize;

            uint typeDefsOffset = currentOffset;
            int typeDefsSize = typeDefsBytes.Length;
            currentOffset += (uint)typeDefsSize;

            uint imagesOffset = currentOffset;
            int imagesSize = imagesBytes.Length;
            currentOffset += (uint)imagesSize;

            uint assembliesOffset = currentOffset;
            int assembliesSize = assembliesBytes.Length;
            currentOffset += (uint)assembliesSize;

            writer.Write(0xFAB11BAFu); // magic
            writer.Write(39);           // version 39

            Il2CppSectionMetadata Sec(uint off, int size, int count) => new()
            {
                offset = (int)off,
                sectionSize = size,
                count = count
            };

            var sections = new System.Collections.Generic.List<Il2CppSectionMetadata>
            {
                Sec(currentOffset, 0, 0), // stringLiterals
                Sec(currentOffset, 0, 0), // stringLiteralData
                Sec(stringsOffset, stringsSize, stringsSize), // strings
                Sec(currentOffset, 0, 0), // events
                Sec(currentOffset, 0, 0), // properties
                Sec(currentOffset, 0, 0), // methods
                Sec(currentOffset, 0, 0), // parameterDefaultValues
                Sec(currentOffset, 0, 0), // fieldDefaultValues
                Sec(currentOffset, 0, 0), // fieldAndParameterDefaultValueData
                Sec(currentOffset, 0, 0), // fieldMarshaledSizes
                Sec(currentOffset, 0, 0), // parameters
                Sec(currentOffset, 0, 0), // fields
                Sec(currentOffset, 0, 0), // genericParameters
                Sec(currentOffset, 0, 0), // genericParameterConstraints
                Sec(currentOffset, 0, 0), // genericContainers
                Sec(nestedTypesOffset, nestedTypesSize, nestedTypes.Length), // nestedTypes
                Sec(currentOffset, 0, 0), // interfaces
                Sec(currentOffset, 0, 0), // vtableMethods
                Sec(currentOffset, 0, 0), // interfaceOffsets
                Sec(typeDefsOffset, typeDefsSize, 4), // typeDefinitions
                Sec(imagesOffset, imagesSize, 2), // images
                Sec(assembliesOffset, assembliesSize, 2), // assemblies
                Sec(currentOffset, 0, 0), // fieldRefs
                Sec(currentOffset, 0, 0), // referencedAssemblies
                Sec(currentOffset, 0, 0), // attributeData
                Sec(currentOffset, 0, 0), // attributeDataRanges
                Sec(currentOffset, 0, 0), // unresolvedIndirectCallParameterTypes
                Sec(currentOffset, 0, 0), // unresolvedIndirectCallParameterRanges
                Sec(currentOffset, 0, 0), // windowsRuntimeTypeNames
                Sec(currentOffset, 0, 0), // windowsRuntimeStrings
                Sec(currentOffset, 0, 0), // exportedTypeDefinitions
            };

            foreach (var s in sections)
            {
                writer.Write(s.offset);
                writer.Write(s.sectionSize);
                writer.Write(s.count);
            }

            writer.Write(stringBytes);
            writer.Write(nestedTypesBytes);
            writer.Write(typeDefsBytes);
            writer.Write(imagesBytes);
            writer.Write(assembliesBytes);

            writer.Flush();
            return ms.ToArray();
        }
    }
}
