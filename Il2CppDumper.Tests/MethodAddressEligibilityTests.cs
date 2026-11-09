using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Il2CppDumper;
using Mono.Cecil;
using Xunit;

namespace Il2CppDumper.Tests
{
    public class MethodAddressEligibilityTests
    {
        private static (byte[] peBytes, ulong validCodeVa, ulong dataVa) CreateTestPe(uint textVirtualSize = 512, uint textRawSize = 512)
        {
            var sections = new (string, byte[], uint, uint)[]
            {
                (".text", new byte[textRawSize], textVirtualSize, 0x60000020), // RVA 0x1000, executable
                (".data", new byte[512], 512, 0xC0000040)                      // RVA 0x2000, non-executable data
            };
            var peBytes = ProtectionReportTests.CreateSyntheticPe(sections);
            // ImageBase in CreateSyntheticPe is 0x180000000
            var validCodeVa = 0x180001000ul;
            var dataVa = 0x180002000ul;
            return (peBytes, validCodeVa, dataVa);
        }

        private static byte[] CreateSyntheticElf64WithSegment(ulong p_vaddr, ulong p_offset, ulong p_memsz, ulong p_filesz, uint p_flags = 7)
        {
            var dynamicOffset = Math.Max(p_offset + p_filesz, 0x600ul) + 0x100ul;
            var dynamicVaddr = 0x5000ul;
            var totalSize = (int)dynamicOffset + 0x2000;
            var data = new byte[totalSize];
            using var ms = new MemoryStream(data);
            using var w = new BinaryWriter(ms);

            w.Write(new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F' }); // e_ident[0..3]
            w.Write((byte)2); // EI_CLASS: ELFCLASS64
            w.Write((byte)1); // EI_DATA: ELFDATA2LSB
            w.Write((byte)1); // EI_VERSION: EV_CURRENT
            w.Write(new byte[9]); // padding

            w.Write((ushort)3); // e_type: ET_DYN
            w.Write((ushort)183); // e_machine: EM_AARCH64
            w.Write((uint)1); // e_version: EV_CURRENT
            w.Write((ulong)0); // e_entry
            w.Write((ulong)64); // e_phoff = 64
            w.Write((ulong)0x1000); // e_shoff = 0x1000 (non-zero so IsDumped is false, preserving disk p_filesz)
            w.Write((uint)0); // e_flags
            w.Write((ushort)64); // e_ehsize = 64
            w.Write((ushort)56); // e_phentsize = 56
            w.Write((ushort)2); // e_phnum = 2 (PT_LOAD and PT_DYNAMIC)
            w.Write((ushort)64); // e_shentsize = 64
            w.Write((ushort)0); // e_shnum = 0
            w.Write((ushort)0); // e_shstrndx = 0

            // Program header 0: PT_LOAD
            w.Write((uint)1); // p_type: PT_LOAD
            w.Write(p_flags); // p_flags: PF_X
            w.Write(p_offset); // p_offset
            w.Write(p_vaddr); // p_vaddr
            w.Write(p_vaddr); // p_paddr
            w.Write(p_filesz); // p_filesz
            w.Write(p_memsz); // p_memsz
            w.Write((ulong)4096); // p_align

            // Program header 1: PT_DYNAMIC (required by Elf64.Load)
            w.Write((uint)2); // p_type: PT_DYNAMIC
            w.Write((uint)6); // p_flags: PF_R | PF_W
            w.Write(dynamicOffset); // p_offset
            w.Write(dynamicVaddr); // p_vaddr
            w.Write(dynamicVaddr); // p_paddr
            w.Write((ulong)16); // p_filesz (1 entry = 16 bytes)
            w.Write((ulong)16); // p_memsz
            w.Write((ulong)8); // p_align

            // Dynamic table entry: DT_NULL
            ms.Position = (long)dynamicOffset;
            w.Write((long)0); // d_tag = DT_NULL
            w.Write((ulong)0); // d_un = 0

            return data;
        }

        private static byte[] CreateSyntheticMacho64TwoAdjacentSections(ulong addr1, ulong size1, uint off1, ulong addr2, ulong size2, uint off2)
        {
            var totalSize = (int)Math.Max(off1 + size1, off2 + size2) + 0x400;
            var bytes = new byte[totalSize];
            using var ms = new MemoryStream(bytes);
            using var w = new BinaryWriter(ms);

            // Mach-O 64 header (32 bytes)
            w.Write((uint)0xFEEDFACF); // magic
            w.Write((uint)0x01000007); // cputype (x86_64)
            w.Write((uint)0x03);       // cpusubtype
            w.Write((uint)0x02);       // filetype (MH_EXECUTE)
            w.Write((uint)2);          // ncmds = 2 segments
            uint cmdSize = 72 + 80;    // 152 bytes per segment with 1 section
            w.Write((uint)(cmdSize * 2)); // sizeofcmds
            w.Write((uint)0);          // flags
            w.Write((uint)0);          // reserved

            void WriteSegment(string name, ulong vmaddr, ulong vmsize, uint fileoff, ulong filesize, ulong secAddr, ulong secSize, uint secOff)
            {
                w.Write((uint)0x19);   // LC_SEGMENT_64
                w.Write(cmdSize);
                var segBytes = new byte[16];
                Encoding.ASCII.GetBytes(name).CopyTo(segBytes, 0);
                w.Write(segBytes);
                w.Write(vmaddr);
                w.Write(vmsize);
                w.Write((ulong)fileoff);
                w.Write(filesize);
                w.Write((uint)7);      // maxprot
                w.Write((uint)5);      // initprot
                w.Write((uint)1);      // nsects = 1
                w.Write((uint)0);      // flags

                // section
                var sectBytes = new byte[16];
                Encoding.ASCII.GetBytes("__text").CopyTo(sectBytes, 0);
                w.Write(sectBytes);
                w.Write(segBytes);
                w.Write(secAddr);
                w.Write(secSize);
                w.Write(secOff);
                w.Write((uint)3);      // align
                w.Write((uint)0);      // reloff
                w.Write((uint)0);      // nreloc
                w.Write((uint)0x80000400); // flags: executable code
                w.Write((uint)0);
                w.Write((uint)0);
                w.Write((uint)0);
            }

            WriteSegment("__TEXT1", addr1, size1, off1, size1, addr1, size1, off1);
            WriteSegment("__TEXT2", addr2, size2, off2, size2, addr2, size2, off2);

            return bytes;
        }

        private static Metadata CreateTestMetadata(
            string imageName,
            string typeName,
            bool isInterface,
            (string methodName, bool isAbstract, uint token)[] methods)
        {
            var bytes = new byte[8192];
            BitConverter.GetBytes(0xFAB11BAFu).CopyTo(bytes, 0);
            BitConverter.GetBytes(29).CopyTo(bytes, 4);

            var m = new Metadata(new MemoryStream(bytes));
            m.header.stringOffset = 1024;
            m.header.stringSize = 4096;

            uint pos = 0;
            uint AddStr(string val)
            {
                var r = pos;
                var strBytes = Encoding.UTF8.GetBytes(val + "\0");
                m.Position = m.header.stringOffset + pos;
                m.Writer.Write(strBytes);
                pos += (uint)strBytes.Length;
                return r;
            }

            AddStr("");
            var imgNameIdx = AddStr(imageName);
            var typeNameIdx = AddStr(typeName);

            m.imageDefs = new[]
            {
                new Il2CppImageDefinition
                {
                    nameIndex = imgNameIdx,
                    assemblyIndex = -1,
                    typeStart = 0,
                    typeCount = 1
                }
            };

            m.typeDefs = new[]
            {
                new Il2CppTypeDefinition
                {
                    nameIndex = typeNameIdx,
                    declaringTypeIndex = -1,
                    parentIndex = -1,
                    genericContainerIndex = -1,
                    methodStart = 0,
                    method_count = (ushort)methods.Length,
                    flags = isInterface ? (uint)(Il2CppConstants.TYPE_ATTRIBUTE_INTERFACE | Il2CppConstants.TYPE_ATTRIBUTE_PUBLIC) : (uint)Il2CppConstants.TYPE_ATTRIBUTE_PUBLIC,
                    byvalTypeIndex = 0
                }
            };

            var methodDefList = new List<Il2CppMethodDefinition>();
            for (int i = 0; i < methods.Length; i++)
            {
                var (mName, isAbs, tok) = methods[i];
                var mNameIdx = AddStr(mName);
                ushort flags = isAbs
                    ? (ushort)(Il2CppConstants.METHOD_ATTRIBUTE_ABSTRACT | Il2CppConstants.METHOD_ATTRIBUTE_VIRTUAL | Il2CppConstants.METHOD_ATTRIBUTE_PUBLIC)
                    : (ushort)Il2CppConstants.METHOD_ATTRIBUTE_PUBLIC;

                methodDefList.Add(new Il2CppMethodDefinition
                {
                    nameIndex = mNameIdx,
                    declaringType = 0,
                    returnType = 0,
                    parameterCount = 0,
                    parameterStart = 0,
                    genericContainerIndex = -1,
                    methodIndex = 0,
                    token = tok,
                    flags = flags,
                    slot = ushort.MaxValue
                });
            }

            m.methodDefs = methodDefList.ToArray();
            m.parameterDefs = Array.Empty<Il2CppParameterDefinition>();
            m.propertyDefs = Array.Empty<Il2CppPropertyDefinition>();
            m.fieldDefs = Array.Empty<Il2CppFieldDefinition>();
            return m;
        }

        [Fact]
        public void SyntheticAbstractInterfaceMethod_WithNonZeroInvalidCandidatePointer_EmitsNoResolvedAddress_AcrossAllOutputs()
        {
            var (peBytes, validCodeVa, _) = CreateTestPe();
            var invalidCandidatePointer = 6144708780086416ul; // 0x15D4948015D490 from Issue #88

            using var pe = new PE(new MemoryStream(peBytes));
            pe.Version = 29;
            pe.types = new[] { new Il2CppType { type = Il2CppTypeEnum.IL2CPP_TYPE_VOID } };
            pe.codeGenModuleMethodPointers = new Dictionary<string, ulong[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["gdio.plugin.serializer.dll"] = new[] { invalidCandidatePointer }
            };

            using var metadata = CreateTestMetadata(
                "gdio.plugin.serializer.dll",
                "gdio.plugin.serializer.ICustomSerializer",
                isInterface: true,
                new[] { ("SupportedTypes", true, 0x06000001u) });

            pe.SetProperties(29, 0, metadata);
            var executor = new Il2CppExecutor(metadata, pe);

            // 1. Direct engine check: GetMethodPointer must return 0 for abstract declarations
            var pointer = pe.GetMethodPointer("gdio.plugin.serializer.dll", metadata.methodDefs[0]);
            Assert.Equal(0ul, pointer);

            var tempDir = Path.Combine(Path.GetTempPath(), "issue88_abstract_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                // 2. StructGenerator: script.json must NOT contain ScriptMethod entry or invalid Address
                var structGenerator = new StructGenerator(executor);
                structGenerator.WriteScript(tempDir + Path.DirectorySeparatorChar);

                var scriptJsonPath = Path.Combine(tempDir, "script.json");
                Assert.True(File.Exists(scriptJsonPath));
                var scriptJsonText = File.ReadAllText(scriptJsonPath);
                using (var doc = JsonDocument.Parse(scriptJsonText))
                {
                    var scriptMethods = doc.RootElement.GetProperty("ScriptMethod");
                    var matching = scriptMethods.EnumerateArray()
                        .Where(x => x.GetProperty("Name").GetString()!.Contains("SupportedTypes"))
                        .ToList();
                    Assert.Empty(matching);

                    // P1: script.json.Addresses must also be filtered by IsValidMethodPointer
                    var addresses = doc.RootElement.GetProperty("Addresses").EnumerateArray().Select(x => x.GetUInt64()).ToHashSet();
                    Assert.DoesNotContain(invalidCandidatePointer, addresses);
                    Assert.DoesNotContain(pe.GetRVA(invalidCandidatePointer), addresses);
                }

                // 3. Il2CppDecompiler: dump.cs must emit '// RVA: -1 Offset: -1'
                var decompiler = new Il2CppDecompiler(executor);
                var config = new Config { DumpMethod = true, DumpMethodOffset = true, DumpTypeDefIndex = true, DumpAttribute = false };
                decompiler.Decompile(config, tempDir);

                var dumpCsPath = Path.Combine(tempDir, "dump.cs");
                Assert.True(File.Exists(dumpCsPath));
                var dumpCsText = File.ReadAllText(dumpCsPath);
                Assert.Contains("// RVA: -1 Offset: -1", dumpCsText);
                Assert.DoesNotContain("0x15D4948015D490", dumpCsText);

                // 4. DummyAssemblyGenerator: DummyDll must NOT attach AddressAttribute
                var dummyGenerator = new DummyAssemblyGenerator(executor, false);
                var assembly = dummyGenerator.Assemblies.First(a => a.MainModule.Name == "gdio.plugin.serializer.dll");
                var method = assembly.MainModule.Types.First(t => t.Name == "gdio.plugin.serializer.ICustomSerializer").Methods[0];
                Assert.DoesNotContain(method.CustomAttributes, a => a.AttributeType.Name == "AddressAttribute");
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
        public void InterfaceMethod_WithConcreteBody_E_g_StaticConstructor_IsExportedConsistently_AcrossAllOutputs()
        {
            // P1: Interface type does NOT imply abstract method; non-abstract interface methods (e.g. .cctor) have valid bodies
            var (peBytes, validCodeVa, _) = CreateTestPe();

            using var pe = new PE(new MemoryStream(peBytes));
            pe.Version = 29;
            pe.types = new[] { new Il2CppType { type = Il2CppTypeEnum.IL2CPP_TYPE_VOID } };
            pe.codeGenModuleMethodPointers = new Dictionary<string, ulong[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["InterfaceModule.dll"] = new[] { validCodeVa } // 0x180001000
            };

            using var metadata = CreateTestMetadata(
                "InterfaceModule.dll",
                "InterfaceModule.IFoo",
                isInterface: true,
                new[] { (".cctor", false, 0x06000001u) }); // non-abstract method on interface

            pe.SetProperties(29, 0, metadata);
            var executor = new Il2CppExecutor(metadata, pe);

            // Engine check: non-abstract method on interface resolves valid pointer
            var pointer = pe.GetMethodPointer("InterfaceModule.dll", metadata.methodDefs[0]);
            Assert.Equal(validCodeVa, pointer);

            var tempDir = Path.Combine(Path.GetTempPath(), "issue88_interface_concrete_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var structGenerator = new StructGenerator(executor);
                structGenerator.WriteScript(tempDir + Path.DirectorySeparatorChar);

                var scriptJsonPath = Path.Combine(tempDir, "script.json");
                using (var doc = JsonDocument.Parse(File.ReadAllText(scriptJsonPath)))
                {
                    var scriptMethods = doc.RootElement.GetProperty("ScriptMethod");
                    var matching = scriptMethods.EnumerateArray()
                        .Where(x => x.GetProperty("Name").GetString()!.Contains(".cctor"))
                        .ToList();
                    Assert.Single(matching);
                    Assert.Equal(0x1000ul, matching[0].GetProperty("Address").GetUInt64());

                    var addresses = doc.RootElement.GetProperty("Addresses").EnumerateArray().Select(x => x.GetUInt64()).ToHashSet();
                    Assert.Contains(0x1000ul, addresses);
                }

                var decompiler = new Il2CppDecompiler(executor);
                decompiler.Decompile(new Config { DumpMethod = true, DumpMethodOffset = true, DumpAttribute = false }, tempDir);

                var dumpCsText = File.ReadAllText(Path.Combine(tempDir, "dump.cs"));
                Assert.Contains("// RVA: 0x1000 Offset: 0x400 VA: 0x180001000", dumpCsText);

                var dummyGenerator = new DummyAssemblyGenerator(executor, false);
                var assembly = dummyGenerator.Assemblies.First(a => a.MainModule.Name == "InterfaceModule.dll");
                var method = assembly.MainModule.Types.First(t => t.Name == "InterfaceModule.IFoo").Methods[0];
                var addrAttr = method.CustomAttributes.FirstOrDefault(a => a.AttributeType.Name == "AddressAttribute");
                Assert.NotNull(addrAttr);
                Assert.Equal("0x1000", addrAttr.Fields.First(f => f.Name == "RVA").Argument.Value);
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
        public void PE_Disk_CandidatePointerInVirtualTailWithoutRawData_IsRejected_AndMapVATRReturnsZero()
        {
            // P2 Point 1: .text at RVA 0x1000, VirtualSize=0x400, SizeOfRawData=0x200
            // Candidate RVA 0x1200 is in the virtual tail past SizeOfRawData (0x1000 + 0x200 = 0x1200)
            var (peBytes, _, _) = CreateTestPe(textVirtualSize: 0x400, textRawSize: 0x200);

            using var pe = new PE(new MemoryStream(peBytes));
            var tailVa = 0x180001200ul; // RVA 0x1200

            Assert.False(pe.IsValidMethodPointer(tailVa));
            Assert.Equal(0ul, pe.MapVATR(tailVa));

            // Positive control within SizeOfRawData (RVA 0x1180 -> raw 0x580)
            var validVa = 0x180001180ul;
            Assert.True(pe.IsValidMethodPointer(validVa));
            Assert.Equal(0x580ul, pe.MapVATR(validVa));
        }

        [Fact]
        public void ELF64_VirtualTailBeyondFilesz_IsRejected_AndMapVATRThrowsInvalidOperation()
        {
            // P2 Point 1: PT_LOAD executable segment with p_vaddr=0x1400, p_offset=0x400, p_memsz=0x400, p_filesz=0x200
            var elfBytes = CreateSyntheticElf64WithSegment(p_vaddr: 0x1400, p_offset: 0x400, p_memsz: 0x400, p_filesz: 0x200);

            using var elf = new Elf64(new MemoryStream(elfBytes));
            var tailVa = 0x1600ul; // 0x1400 + 0x200 = 0x1600 (virtual tail beyond filesz)

            Assert.False(elf.IsValidMethodPointer(tailVa));
            Assert.Throws<InvalidOperationException>(() => elf.MapVATR(tailVa));

            // Positive control within filesz (0x1500 -> offset 0x500)
            var validVa = 0x1500ul;
            Assert.True(elf.IsValidMethodPointer(validVa));
            Assert.Equal(0x500ul, elf.MapVATR(validVa));
        }

        [Fact]
        public void Macho64_AdjacentSectionsBoundary_SelectsSecondSectionForBothValidationAndMapVATR()
        {
            // P2 Point 2: Mach-O 64 with 2 adjacent code sections:
            // Section 1 at addr=0x1000, size=0x1000, offset=0x1000.
            // Section 2 at addr=0x2000, size=0x1000, offset=0x2000.
            var machoBytes = CreateSyntheticMacho64TwoAdjacentSections(
                addr1: 0x1000, size1: 0x1000, off1: 0x1000,
                addr2: 0x2000, size2: 0x1000, off2: 0x2000);

            using var macho = new Macho64(new MemoryStream(machoBytes));
            var boundaryVa = 0x2000ul;

            // At VA 0x2000, section 2 must be chosen exclusively
            Assert.True(macho.IsValidMethodPointer(boundaryVa));
            Assert.Equal(0x2000ul, macho.MapVATR(boundaryVa)); // Must return 0x2000, NOT 0x1000!
        }

        [Fact]
        public void PE_DumpedMemory_StreamTruncated_CandidateOffsetOutsideStream_IsRejected()
        {
            // P2: Memory dump loaded via LoadFromMemory, where stream length is 0x800 bytes
            // Candidate pointer at RVA 0x1000 translates to offset 0x1000, which is >= stream Length (0x800)
            var (peBytes, _, _) = CreateTestPe(textVirtualSize: 0x200, textRawSize: 0x200);

            // Truncate stream to 0x800 bytes
            var truncatedBytes = new byte[0x800];
            Array.Copy(peBytes, truncatedBytes, Math.Min(peBytes.Length, 0x800));

            using var pe = new PE(new MemoryStream(truncatedBytes));
            pe.LoadFromMemory(0x180000000ul);

            var candidateVa = 0x180001000ul; // RVA 0x1000 -> raw offset 0x1000 >= 0x800

            Assert.False(pe.IsValidMethodPointer(candidateVa));
            Assert.Equal(0ul, pe.MapVATR(candidateVa));
        }

        [Fact]
        public void AddMetadataUsageMethodRef_WithInvalidGenericMethodBodyPointer_PreservesSlotAddressAndEmitsStructuredWarning()
        {
            // P2 Point 3: AddMetadataUsageMethodRef with candidate VA 0x180900000 outside PE bounds
            var (peBytes, validCodeVa, _) = CreateTestPe();

            using var pe = new PE(new MemoryStream(peBytes));
            pe.Version = 29;
            pe.types = new[] { new Il2CppType { type = Il2CppTypeEnum.IL2CPP_TYPE_VOID } };

            var invalidGenericMethodPointer = 0x180900000ul;
            var testMethodSpec = new Il2CppMethodSpec { methodDefinitionIndex = 0, classIndexIndex = -1, methodIndexIndex = -1 };
            pe.methodSpecs = new[] { testMethodSpec };
            pe.methodSpecGenericMethodPointers = new Dictionary<Il2CppMethodSpec, ulong>
            {
                [testMethodSpec] = invalidGenericMethodPointer
            };

            using var metadata = CreateTestMetadata(
                "TestModule.dll",
                "TestNamespace.GenericContainer",
                isInterface: false,
                new[] { ("GenericMethod", false, 0x06000001u) });

            pe.SetProperties(29, 0, metadata);
            var executor = new Il2CppExecutor(metadata, pe);

            var structGenerator = new StructGenerator(executor);
            var scriptJson = new ScriptJson();

            var addMethodRefMethod = typeof(StructGenerator).GetMethod("AddMetadataUsageMethodRef", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(addMethodRefMethod);

            var usageSlotVa = 0x180001020ul; // Valid RVA 0x1020 in .text
            var warnings = new List<string>();
            using (CoreLogger.Scope(null, warnings))
            {
                addMethodRefMethod.Invoke(structGenerator, new object[] { scriptJson, 0u, usageSlotVa });
            }

            Assert.Single(scriptJson.ScriptMetadataMethod);
            var entry = scriptJson.ScriptMetadataMethod[0];

            // Slot address must be preserved (RVA 0x1020)
            Assert.Equal(0x1020ul, entry.Address);
            // Native method body pointer must be cleared to 0 (contract absence value) because 0x180900000 is invalid
            Assert.Equal(0ul, entry.MethodAddress);

            // P2 Point 3: Structured warning must be emitted in CoreLogger and recorded in MethodMappingFailures
            Assert.Contains(warnings, w => w.Contains("0x180900000") && w.Contains("MetadataUsageMethodRef"));
            Assert.Single(pe.MethodMappingFailures);
            var failure = pe.MethodMappingFailures[0];
            Assert.Equal("methodMapping", failure.Category);
            Assert.Equal("0x180900000", failure.RawBytes);
            Assert.Contains("MetadataUsageMethodRef", failure.Detail);
        }

        [Fact]
        public void ConcreteMethod_WithValidPointer_IsExportedConsistently_AcrossAllOutputs()
        {
            var (peBytes, validCodeVa, _) = CreateTestPe();

            using var pe = new PE(new MemoryStream(peBytes));
            pe.Version = 29;
            pe.types = new[] { new Il2CppType { type = Il2CppTypeEnum.IL2CPP_TYPE_VOID } };
            pe.codeGenModuleMethodPointers = new Dictionary<string, ulong[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["TestModule.dll"] = new[] { validCodeVa } // 0x180001000: RVA 0x1000, Offset 0x400
            };

            using var metadata = CreateTestMetadata(
                "TestModule.dll",
                "TestNamespace.ConcreteClass",
                isInterface: false,
                new[] { ("Execute", false, 0x06000001u) });

            pe.SetProperties(29, 0, metadata);
            var executor = new Il2CppExecutor(metadata, pe);

            // 1. Direct engine check: valid pointer in .text is resolved
            var pointer = pe.GetMethodPointer("TestModule.dll", metadata.methodDefs[0]);
            Assert.Equal(validCodeVa, pointer);

            var tempDir = Path.Combine(Path.GetTempPath(), "issue88_concrete_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                // 2. StructGenerator: script.json contains ScriptMethod with resolved RVA 0x1000
                var structGenerator = new StructGenerator(executor);
                structGenerator.WriteScript(tempDir + Path.DirectorySeparatorChar);

                var scriptJsonPath = Path.Combine(tempDir, "script.json");
                Assert.True(File.Exists(scriptJsonPath));
                using (var doc = JsonDocument.Parse(File.ReadAllText(scriptJsonPath)))
                {
                    var scriptMethods = doc.RootElement.GetProperty("ScriptMethod");
                    var matching = scriptMethods.EnumerateArray()
                        .Where(x => x.GetProperty("Name").GetString()!.Contains("Execute"))
                        .ToList();
                    Assert.Single(matching);
                    Assert.Equal(0x1000ul, matching[0].GetProperty("Address").GetUInt64());

                    var addresses = doc.RootElement.GetProperty("Addresses").EnumerateArray().Select(x => x.GetUInt64()).ToHashSet();
                    Assert.Contains(0x1000ul, addresses);
                }

                // 3. Il2CppDecompiler: dump.cs contains RVA: 0x1000 Offset: 0x400 VA: 0x180001000
                var decompiler = new Il2CppDecompiler(executor);
                var config = new Config { DumpMethod = true, DumpMethodOffset = true, DumpAttribute = false };
                decompiler.Decompile(config, tempDir);

                var dumpCsPath = Path.Combine(tempDir, "dump.cs");
                var dumpCsText = File.ReadAllText(dumpCsPath);
                Assert.Contains("// RVA: 0x1000 Offset: 0x400 VA: 0x180001000", dumpCsText);

                // 4. DummyAssemblyGenerator: DummyDll attaches AddressAttribute with RVA, Offset, VA
                var dummyGenerator = new DummyAssemblyGenerator(executor, false);
                var assembly = dummyGenerator.Assemblies.First(a => a.MainModule.Name == "TestModule.dll");
                var method = assembly.MainModule.Types.First(t => t.Name == "TestNamespace.ConcreteClass").Methods[0];
                var addrAttr = method.CustomAttributes.FirstOrDefault(a => a.AttributeType.Name == "AddressAttribute");
                Assert.NotNull(addrAttr);
                Assert.Equal("0x1000", addrAttr.Fields.First(f => f.Name == "RVA").Argument.Value);
                Assert.Equal("0x400", addrAttr.Fields.First(f => f.Name == "Offset").Argument.Value);
                Assert.Equal("0x180001000", addrAttr.Fields.First(f => f.Name == "VA").Argument.Value);
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
        public void ConcreteMethod_WithCandidatePointerOutsideExecutableSections_EmitsNoResolvedAddress()
        {
            var (peBytes, _, dataVa) = CreateTestPe();
            var outsideSectionVa = 6144708780086416ul;

            // Test both: outside all sections AND inside non-executable .data section
            foreach (var testPointer in new[] { outsideSectionVa, dataVa })
            {
                using var pe = new PE(new MemoryStream(peBytes));
                pe.Version = 29;
                pe.types = new[] { new Il2CppType { type = Il2CppTypeEnum.IL2CPP_TYPE_VOID } };
                pe.codeGenModuleMethodPointers = new Dictionary<string, ulong[]>(StringComparer.OrdinalIgnoreCase)
                {
                    ["TestModule.dll"] = new[] { testPointer }
                };

                using var metadata = CreateTestMetadata(
                    "TestModule.dll",
                    "TestNamespace.ConcreteClass",
                    isInterface: false,
                    new[] { ("Compute", false, 0x06000001u) });

                pe.SetProperties(29, 0, metadata);
                var executor = new Il2CppExecutor(metadata, pe);

                // Must be rejected as invalid method pointer
                Assert.False(pe.IsValidMethodPointer(testPointer));
                Assert.Equal(0ul, pe.GetMethodPointer("TestModule.dll", metadata.methodDefs[0]));

                var tempDir = Path.Combine(Path.GetTempPath(), "issue88_invalid_concrete_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                try
                {
                    var structGenerator = new StructGenerator(executor);
                    structGenerator.WriteScript(tempDir + Path.DirectorySeparatorChar);

                    var scriptJsonPath = Path.Combine(tempDir, "script.json");
                    using (var doc = JsonDocument.Parse(File.ReadAllText(scriptJsonPath)))
                    {
                        var scriptMethods = doc.RootElement.GetProperty("ScriptMethod");
                        var matching = scriptMethods.EnumerateArray()
                            .Where(x => x.GetProperty("Name").GetString()!.Contains("Compute"))
                            .ToList();
                        Assert.Empty(matching);
                    }

                    var decompiler = new Il2CppDecompiler(executor);
                    decompiler.Decompile(new Config { DumpMethod = true, DumpMethodOffset = true, DumpAttribute = false }, tempDir);

                    var dumpCsText = File.ReadAllText(Path.Combine(tempDir, "dump.cs"));
                    Assert.Contains("// RVA: -1 Offset: -1", dumpCsText);

                    var dummyGenerator = new DummyAssemblyGenerator(executor, false);
                    var assembly = dummyGenerator.Assemblies.First(a => a.MainModule.Name == "TestModule.dll");
                    var method = assembly.MainModule.Types.First(t => t.Name == "TestNamespace.ConcreteClass").Methods[0];
                    Assert.DoesNotContain(method.CustomAttributes, a => a.AttributeType.Name == "AddressAttribute");
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
        public void DumpedPeMemoryPath_ValidPointerInExecutableSection_ExportsResolvedAddress()
        {
            var (peBytes, _, _) = CreateTestPe();

            // In a dumped binary, memory spans virtual space so bytes are effectively present at RVA 0x1000
            var dumpedMemoryBytes = new byte[0x2000];
            Array.Copy(peBytes, dumpedMemoryBytes, peBytes.Length);

            using var pe = new PE(new MemoryStream(dumpedMemoryBytes));
            // Simulate dumped binary loaded at base address 0x200000000
            pe.LoadFromMemory(0x200000000ul);
            pe.Version = 29;
            pe.types = new[] { new Il2CppType { type = Il2CppTypeEnum.IL2CPP_TYPE_VOID } };

            var dumpedCodeVa = 0x200001000ul; // RVA 0x1000 in .text
            pe.codeGenModuleMethodPointers = new Dictionary<string, ulong[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["DumpedModule.dll"] = new[] { dumpedCodeVa }
            };

            using var metadata = CreateTestMetadata(
                "DumpedModule.dll",
                "DumpedNamespace.DumpedClass",
                isInterface: false,
                new[] { ("DumpedMethod", false, 0x06000001u) });

            pe.SetProperties(29, 0, metadata);
            var executor = new Il2CppExecutor(metadata, pe);

            Assert.True(pe.IsValidMethodPointer(dumpedCodeVa));
            Assert.Equal(dumpedCodeVa, pe.GetMethodPointer("DumpedModule.dll", metadata.methodDefs[0]));
            Assert.Equal(0x1000ul, pe.GetRVA(dumpedCodeVa));
            Assert.Equal(0x1000ul, pe.MapVATR(dumpedCodeVa)); // In memory dump, MapVATR = RVA
        }

        [Fact]
        public void RealTargetIssue88Regression_AllThreeICustomSerializerMethods_OmittedFromScriptJsonAndUnresolvedInDumpCs()
        {
            var (peBytes, _, _) = CreateTestPe();

            // The exact three methods and candidate pointers reported in Issue #88
            var candidatePointers = new ulong[]
            {
                6144708780086416ul,  // SupportedTypes
                6144751835038524ul,  // Serialize
                451243860565546221ul // Deserialize
            };

            using var pe = new PE(new MemoryStream(peBytes));
            pe.Version = 39;
            pe.types = new[] { new Il2CppType { type = Il2CppTypeEnum.IL2CPP_TYPE_VOID } };
            pe.codeGenModuleMethodPointers = new Dictionary<string, ulong[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["gdio.plugin.serializer.dll"] = candidatePointers
            };

            using var metadata = CreateTestMetadata(
                "gdio.plugin.serializer.dll",
                "gdio.plugin.serializer.ICustomSerializer",
                isInterface: true,
                new[]
                {
                    ("SupportedTypes", true, 0x06000001u),
                    ("Serialize", true, 0x06000002u),
                    ("Deserialize", true, 0x06000003u)
                });

            pe.SetProperties(39, 0, metadata);
            var executor = new Il2CppExecutor(metadata, pe);

            for (int i = 0; i < 3; i++)
            {
                Assert.Equal(0ul, pe.GetMethodPointer("gdio.plugin.serializer.dll", metadata.methodDefs[i]));
            }

            var tempDir = Path.Combine(Path.GetTempPath(), "issue88_real_regression_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                // Verify script.json contains NONE of the three declarations in ScriptMethod OR in Addresses
                var structGenerator = new StructGenerator(executor);
                structGenerator.WriteScript(tempDir + Path.DirectorySeparatorChar);

                var scriptJsonPath = Path.Combine(tempDir, "script.json");
                using (var doc = JsonDocument.Parse(File.ReadAllText(scriptJsonPath)))
                {
                    var scriptMethods = doc.RootElement.GetProperty("ScriptMethod");
                    var matched = scriptMethods.EnumerateArray()
                        .Where(x => x.GetProperty("Name").GetString()!.StartsWith("gdio.plugin.serializer.ICustomSerializer$$"))
                        .ToList();
                    Assert.Empty(matched);

                    var addresses = doc.RootElement.GetProperty("Addresses").EnumerateArray().Select(x => x.GetUInt64()).ToHashSet();
                    foreach (var ptr in candidatePointers)
                    {
                        Assert.DoesNotContain(ptr, addresses);
                        Assert.DoesNotContain(pe.GetRVA(ptr), addresses);
                    }
                }

                // Verify dump.cs marks all three as unresolved: // RVA: -1 Offset: -1
                var decompiler = new Il2CppDecompiler(executor);
                decompiler.Decompile(new Config { DumpMethod = true, DumpMethodOffset = true, DumpAttribute = false }, tempDir);

                var dumpCsLines = File.ReadAllLines(Path.Combine(tempDir, "dump.cs"));
                var methodLines = dumpCsLines.Where(l => l.Contains("SupportedTypes()") || l.Contains("Serialize(") || l.Contains("Deserialize(")).ToList();
                Assert.Equal(3, methodLines.Count);

                // Verify DummyDll contains no AddressAttribute on any of the three
                var dummyGenerator = new DummyAssemblyGenerator(executor, false);
                var assembly = dummyGenerator.Assemblies.First(a => a.MainModule.Name == "gdio.plugin.serializer.dll");
                var typeDef = assembly.MainModule.Types.First(t => t.Name == "gdio.plugin.serializer.ICustomSerializer");
                foreach (var m in typeDef.Methods)
                {
                    Assert.DoesNotContain(m.CustomAttributes, a => a.AttributeType.Name == "AddressAttribute");
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
