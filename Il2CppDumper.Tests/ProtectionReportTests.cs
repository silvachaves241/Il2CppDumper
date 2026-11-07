#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Il2CppDumper.Core;
using Xunit;

namespace Il2CppDumper.Tests
{
    public class ProtectionReportTests
    {
        private static byte[] CreateMinimalMetadata(int version = 29, uint magic = 0xFAB11BAFu)
        {
            var bytes = new byte[1024];
            BitConverter.GetBytes(magic).CopyTo(bytes, 0);
            BitConverter.GetBytes(version).CopyTo(bytes, 4);
            BitConverter.GetBytes(256).CopyTo(bytes, 160); // typeDefinitions offset
            BitConverter.GetBytes(88).CopyTo(bytes, 164);  // typeDefinitions size
            return bytes;
        }

        private static byte[] CreateSyntheticPe(
            (string name, byte[] data, uint virtualSize, uint characteristics)[] sections,
            bool relocsStripped = false,
            uint timeDateStamp = 0,
            uint checkSum = 0)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);

            // DOS Header (0x40 bytes)
            w.Write((ushort)0x5A4D); // e_magic = 'MZ'
            for (int i = 0; i < 29; i++) w.Write((ushort)0);
            w.Write((uint)0x80); // e_lfanew = 0x80

            // Padding to 0x80
            while (ms.Position < 0x80) w.Write((byte)0);

            // PE Header Signature "PE\0\0"
            w.Write((uint)0x00004550);

            // FileHeader (20 bytes)
            w.Write((ushort)0x8664); // Machine = AMD64
            w.Write((ushort)sections.Length); // NumberOfSections
            w.Write(timeDateStamp); // TimeDateStamp
            w.Write((uint)0); // PointerToSymbolTable
            w.Write((uint)0); // NumberOfSymbols
            w.Write((ushort)0xF0); // SizeOfOptionalHeader
            w.Write((ushort)(relocsStripped ? 0x0001 : 0x0000)); // Characteristics

            // OptionalHeader64 (240 bytes)
            var optStart = ms.Position;
            w.Write((ushort)0x20b); // Magic = PE32+
            w.Write((byte)14);      // MajorLinkerVersion
            w.Write((byte)0);       // MinorLinkerVersion
            w.Write((uint)0x1000);  // SizeOfCode
            w.Write((uint)0x1000);  // SizeOfInitializedData
            w.Write((uint)0);       // SizeOfUninitializedData
            w.Write((uint)0x1000);  // AddressOfEntryPoint
            w.Write((uint)0x1000);  // BaseOfCode
            w.Write((ulong)0x180000000); // ImageBase
            w.Write((uint)0x1000);  // SectionAlignment
            w.Write((uint)0x200);   // FileAlignment
            w.Write((ushort)6);     // MajorOperatingSystemVersion
            w.Write((ushort)0);     // MinorOperatingSystemVersion
            w.Write((ushort)0);     // MajorImageVersion
            w.Write((ushort)0);     // MinorImageVersion
            w.Write((ushort)6);     // MajorSubsystemVersion
            w.Write((ushort)0);     // MinorSubsystemVersion
            w.Write((uint)0);       // Win32VersionValue
            w.Write((uint)0x10000); // SizeOfImage
            w.Write((uint)0x400);   // SizeOfHeaders
            w.Write(checkSum);       // CheckSum
            w.Write((ushort)3);     // Subsystem = Windows CUI
            w.Write((ushort)0x8160);// DllCharacteristics
            w.Write((ulong)0x100000); // SizeOfStackReserve
            w.Write((ulong)0x1000);   // SizeOfStackCommit
            w.Write((ulong)0x100000); // SizeOfHeapReserve
            w.Write((ulong)0x1000);   // SizeOfHeapCommit
            w.Write((uint)0);       // LoaderFlags
            w.Write((uint)16);      // NumberOfRvaAndSizes

            // Data directories (16 * 8 = 128 bytes)
            for (int i = 0; i < 16; i++)
            {
                w.Write((uint)0);
                w.Write((uint)0);
            }

            while (ms.Position < optStart + 0xF0) w.Write((byte)0);

            // Section Headers
            uint curRva = 0x1000;
            uint curRaw = 0x400;
            var rawPositions = new List<(uint rawOffset, byte[] data)>();

            foreach (var sec in sections)
            {
                var nameBytes = new byte[8];
                var enc = Encoding.ASCII.GetBytes(sec.name);
                Array.Copy(enc, nameBytes, Math.Min(enc.Length, 8));
                w.Write(nameBytes);

                w.Write(sec.virtualSize);
                w.Write(curRva);
                w.Write((uint)sec.data.Length);
                w.Write(curRaw);
                w.Write((uint)0); // PointerToRelocations
                w.Write((uint)0); // PointerToLinenumbers
                w.Write((ushort)0);
                w.Write((ushort)0);
                w.Write(sec.characteristics);

                rawPositions.Add((curRaw, sec.data));

                curRva += Math.Max(0x1000, ((sec.virtualSize + 0xFFF) / 0x1000) * 0x1000);
                curRaw += (uint)((sec.data.Length + 0x1FF) / 0x200) * 0x200;
            }

            // Write Section Raw Data
            foreach (var item in rawPositions)
            {
                while (ms.Position < item.rawOffset) w.Write((byte)0);
                w.Write(item.data);
            }

            while (ms.Position % 0x200 != 0) w.Write((byte)0);

            return ms.ToArray();
        }

        private static byte[] CreateSyntheticWasm(byte[] dataPayload)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            // Magic and version
            w.Write(new byte[] { 0x00, 0x61, 0x73, 0x6d, 0x01, 0x00, 0x00, 0x00 });
            // Section 5: Memory
            w.Write(new byte[] { 0x05, 0x03, 0x01, 0x00, 0x01 });
            // Section 11: Data
            var payload = new List<byte>();
            payload.AddRange(new byte[] { 0x01, 0x00, 0x41, 0x00, 0x0b });
            payload.AddRange(WriteULeb128(dataPayload.Length));
            payload.AddRange(dataPayload);

            w.Write((byte)11);
            w.Write(WriteULeb128(payload.Count));
            w.Write(payload.ToArray());
            return ms.ToArray();
        }

        private static byte[] CreateMultiSectionSyntheticWasm((byte sectionId, byte[] payload)[] sections)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            // Magic and version (\0asm\1\0\0\0)
            w.Write(new byte[] { 0x00, 0x61, 0x73, 0x6d, 0x01, 0x00, 0x00, 0x00 });
            foreach (var (sectionId, payload) in sections)
            {
                w.Write(sectionId);
                w.Write(WriteULeb128(payload.Length));
                w.Write(payload);
            }
            return ms.ToArray();
        }

        private static byte[] CreateSyntheticMacho32((string name, byte[] data)[] sections)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);

            // Mach-O 32 header (28 bytes)
            w.Write((uint)0xFEEDFACE); // magic
            w.Write((uint)0x07);       // cputype (x86)
            w.Write((uint)0x03);       // cpusubtype
            w.Write((uint)0x02);       // filetype (MH_EXECUTE)
            w.Write((uint)1);          // ncmds
            uint cmdSize = (uint)(56 + (sections.Length * 68));
            w.Write(cmdSize);          // sizeofcmds
            w.Write((uint)0);          // flags

            // LC_SEGMENT (56 bytes header + 68 bytes per section)
            w.Write((uint)0x01);       // cmd = LC_SEGMENT
            w.Write(cmdSize);          // cmdsize
            var segName = new byte[16];
            Encoding.ASCII.GetBytes("__TEXT").CopyTo(segName, 0);
            w.Write(segName);          // segname (16 bytes)
            w.Write((uint)0x1000);     // vmaddr
            w.Write((uint)0x10000);    // vmsize
            w.Write((uint)0);          // fileoff
            w.Write((uint)0x10000);    // filesize
            w.Write((uint)7);          // maxprot
            w.Write((uint)5);          // initprot
            w.Write((uint)sections.Length); // nsects
            w.Write((uint)0);          // flags

            uint curOffset = (uint)(28 + cmdSize);
            var sectionData = new List<(uint offset, byte[] data)>();

            foreach (var sec in sections)
            {
                var sectName = new byte[16];
                var enc = Encoding.ASCII.GetBytes(sec.name);
                Array.Copy(enc, sectName, Math.Min(enc.Length, 16));
                w.Write(sectName);     // sectname (16 bytes)
                w.Write(segName);      // segname (16 bytes)
                w.Write((uint)0x1000); // addr
                w.Write((uint)sec.data.Length); // size
                w.Write(curOffset);    // offset
                w.Write((uint)2);      // align
                w.Write((uint)0);      // reloff
                w.Write((uint)0);      // nreloc
                w.Write((uint)0);      // flags
                w.Write((uint)0);      // reserved1
                w.Write((uint)0);      // reserved2

                sectionData.Add((curOffset, sec.data));
                curOffset += (uint)sec.data.Length;
            }

            foreach (var item in sectionData)
            {
                while (ms.Position < item.offset) w.Write((byte)0);
                w.Write(item.data);
            }

            return ms.ToArray();
        }

        private static byte[] CreateSyntheticMacho64((string name, byte[] data)[] sections)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);

            // Mach-O 64 header (32 bytes)
            w.Write((uint)0xFEEDFACF); // magic
            w.Write((uint)0x01000007); // cputype (x86_64)
            w.Write((uint)0x03);       // cpusubtype
            w.Write((uint)0x02);       // filetype (MH_EXECUTE)
            w.Write((uint)1);          // ncmds
            uint cmdSize = (uint)(72 + (sections.Length * 80));
            w.Write(cmdSize);          // sizeofcmds
            w.Write((uint)0);          // flags
            w.Write((uint)0);          // reserved

            // LC_SEGMENT_64 (72 bytes header + 80 bytes per section)
            w.Write((uint)0x19);       // cmd = LC_SEGMENT_64
            w.Write(cmdSize);          // cmdsize
            var segName = new byte[16];
            Encoding.ASCII.GetBytes("__TEXT").CopyTo(segName, 0);
            w.Write(segName);          // segname (16 bytes)
            w.Write((ulong)0x1000);    // vmaddr
            w.Write((ulong)0x10000);   // vmsize
            w.Write((ulong)0);         // fileoff
            w.Write((ulong)0x10000);   // filesize
            w.Write((uint)7);          // maxprot
            w.Write((uint)5);          // initprot
            w.Write((uint)sections.Length); // nsects
            w.Write((uint)0);          // flags

            uint curOffset = (uint)(32 + cmdSize);
            var sectionData = new List<(uint offset, byte[] data)>();

            foreach (var sec in sections)
            {
                var sectName = new byte[16];
                var enc = Encoding.ASCII.GetBytes(sec.name);
                Array.Copy(enc, sectName, Math.Min(enc.Length, 16));
                w.Write(sectName);     // sectname (16 bytes)
                w.Write(segName);      // segname (16 bytes)
                w.Write((ulong)0x1000);// addr
                w.Write((ulong)sec.data.Length); // size
                w.Write(curOffset);    // offset
                w.Write((uint)3);      // align
                w.Write((uint)0);      // reloff
                w.Write((uint)0);      // nreloc
                w.Write((uint)0);      // flags
                w.Write((uint)0);      // reserved1
                w.Write((uint)0);      // reserved2
                w.Write((uint)0);      // reserved3

                sectionData.Add((curOffset, sec.data));
                curOffset += (uint)sec.data.Length;
            }

            foreach (var item in sectionData)
            {
                while (ms.Position < item.offset) w.Write((byte)0);
                w.Write(item.data);
            }

            return ms.ToArray();
        }

        private static void WriteSyntheticElf64(string filePath, string sectionName, long sectionSize, Action<Stream> writePayload)
        {
            using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
            using var w = new BinaryWriter(fs);

            // ELF Header (64 bytes)
            w.Write(new byte[] { 0x7F, 0x45, 0x4C, 0x46, 2, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
            w.Write((ushort)2);        // e_type (ET_EXEC)
            w.Write((ushort)0x3E);     // e_machine (AMD x86-64)
            w.Write((uint)1);          // e_version
            w.Write((ulong)0x1000);    // e_entry
            w.Write((ulong)0);         // e_phoff
            ulong shoff = 64;
            w.Write(shoff);            // e_shoff (at offset 64)
            w.Write((uint)0);          // e_flags
            w.Write((ushort)64);       // e_ehsize
            w.Write((ushort)56);       // e_phentsize
            w.Write((ushort)0);        // e_phnum
            w.Write((ushort)64);       // e_shentsize
            w.Write((ushort)3);        // e_shnum: 0=NULL, 1=.shstrtab, 2=sectionName
            w.Write((ushort)1);        // e_shstrndx = 1

            // Prepare shstrtab bytes
            var shstrtabMs = new MemoryStream();
            shstrtabMs.WriteByte(0); // index 0 = \0
            uint shstrtabNameIdx = 1;
            var shstrBytes = Encoding.ASCII.GetBytes(".shstrtab");
            shstrtabMs.Write(shstrBytes, 0, shstrBytes.Length);
            shstrtabMs.WriteByte(0);

            uint customNameIdx = (uint)shstrtabMs.Position;
            var custBytes = Encoding.ASCII.GetBytes(sectionName);
            shstrtabMs.Write(custBytes, 0, custBytes.Length);
            shstrtabMs.WriteByte(0);

            var shstrtab = shstrtabMs.ToArray();

            ulong shstrtabOffset = 64 + (3 * 64); // 256
            ulong payloadOffset = shstrtabOffset + (ulong)shstrtab.Length;
            if (payloadOffset % 16 != 0)
            {
                payloadOffset += 16 - (payloadOffset % 16);
            }

            // Section 0 (NULL)
            for (int i = 0; i < 8; i++) w.Write((ulong)0);

            // Section 1 (.shstrtab)
            w.Write(shstrtabNameIdx); // sh_name
            w.Write((uint)3);          // sh_type (SHT_STRTAB)
            w.Write((ulong)0);         // sh_flags
            w.Write((ulong)0);         // sh_addr
            w.Write(shstrtabOffset);   // sh_offset
            w.Write((ulong)shstrtab.Length); // sh_size
            w.Write((uint)0);          // sh_link
            w.Write((uint)0);          // sh_info
            w.Write((ulong)1);         // sh_addralign
            w.Write((ulong)0);         // sh_entsize

            // Section 2 (custom section)
            w.Write(customNameIdx);    // sh_name
            w.Write((uint)1);          // sh_type (SHT_PROGBITS)
            w.Write((ulong)6);         // sh_flags (SHF_ALLOC | SHF_EXECINSTR)
            w.Write((ulong)0x1000);    // sh_addr
            w.Write(payloadOffset);    // sh_offset
            w.Write((ulong)sectionSize); // sh_size
            w.Write((uint)0);          // sh_link
            w.Write((uint)0);          // sh_info
            w.Write((ulong)16);        // sh_addralign
            w.Write((ulong)0);         // sh_entsize

            // Write shstrtab
            while ((ulong)fs.Position < shstrtabOffset) w.Write((byte)0);
            w.Write(shstrtab);

            // Write custom section payload
            while ((ulong)fs.Position < payloadOffset) w.Write((byte)0);
            writePayload(fs);
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

        [Fact]
        public void EntropyCalculation_DeterministicAndAccurate()
        {
            // All identical bytes: entropy = 0.00
            var lowBytes = new byte[1024];
            var lowEntropy = ProtectionDiagnostics.CalculateEntropy(lowBytes, 0, lowBytes.Length);
            Assert.Equal(0.00, lowEntropy);

            // Exactly 256 unique bytes, 1 of each: entropy = 8.00
            var uniformBytes = new byte[256];
            for (int i = 0; i < 256; i++) uniformBytes[i] = (byte)i;
            var uniformEntropy = ProtectionDiagnostics.CalculateEntropy(uniformBytes, 0, uniformBytes.Length);
            Assert.Equal(8.00, uniformEntropy);

            // Known reproducible distribution
            var fixedRandom = new Random(1337);
            var crafted = new byte[4096];
            fixedRandom.NextBytes(crafted);
            var craftedEntropy = ProtectionDiagnostics.CalculateEntropy(crafted, 0, crafted.Length);
            Assert.True(craftedEntropy >= 7.90 && craftedEntropy <= 8.00, $"Expected high entropy, got {craftedEntropy}");
        }

        [Fact]
        public void LowEntropySyntheticInput_NoHighEntropyFinding_CraftedFixture_FindingPresentWithReproducibleScore()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "diag_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var metaPath = Path.Combine(tempDir, "global-metadata.dat");
                File.WriteAllBytes(metaPath, CreateMinimalMetadata());

                // 1. Low entropy PE input (all zeroes in sections)
                var lowPeSections = new[]
                {
                    (".text", new byte[1024], 1024u, 0x60000020u),
                    (".rdata", new byte[512], 512u, 0x40000040u)
                };
                var lowPeBytes = CreateSyntheticPe(lowPeSections);
                var lowPePath = Path.Combine(tempDir, "low.exe");
                File.WriteAllBytes(lowPePath, lowPeBytes);

                var reportLow = ProtectionDiagnostics.Analyze(lowPePath, metaPath, new Config());
                Assert.DoesNotContain(reportLow.Findings, f => f.Finding == "HighEntropySection");
                Assert.Contains(reportLow.Findings, f => f.Finding == "MetadataSanity" && f.Ok == true);

                // 2. High entropy PE input with crafted high-entropy .text section duplicate
                var highBytes = new byte[2048];
                var rnd = new Random(42);
                rnd.NextBytes(highBytes);
                var expectedScore = ProtectionDiagnostics.CalculateEntropy(highBytes, 0, highBytes.Length);

                var highPeSections = new[]
                {
                    (".text", new byte[1024], 1024u, 0x60000020u),
                    (".text", highBytes, 2048u, 0x60000020u) // duplicate section .text(2)
                };
                var highPeBytes = CreateSyntheticPe(highPeSections);
                var highPePath = Path.Combine(tempDir, "high.exe");
                File.WriteAllBytes(highPePath, highPeBytes);

                var reportHigh = ProtectionDiagnostics.Analyze(highPePath, metaPath, new Config());
                var highFinding = Assert.Single(reportHigh.Findings.Where(f => f.Finding == "HighEntropySection"));
                Assert.Equal(".text(2)", highFinding.Target);
                Assert.Equal(expectedScore, highFinding.Entropy);
                Assert.Equal("Consider a memory-dumped input; if pointers are redirected set NoRedirectedPointer=true", highFinding.Remediation);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public void PackerSectionSignatures_DetectedAndReported()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "packer_diag_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var metaPath = Path.Combine(tempDir, "global-metadata.dat");
                File.WriteAllBytes(metaPath, CreateMinimalMetadata());

                var peSections = new[]
                {
                    ("UPX0", new byte[512], 4096u, 0xC0000080u),
                    ("UPX1", new byte[512], 4096u, 0xE0000020u),
                    (".rsrc", new byte[512], 512u, 0x40000040u)
                };
                var peBytes = CreateSyntheticPe(peSections);
                var pePath = Path.Combine(tempDir, "packed.exe");
                File.WriteAllBytes(pePath, peBytes);

                var report = ProtectionDiagnostics.Analyze(pePath, metaPath, new Config());
                var packerFindings = report.Findings.Where(f => f.Finding == "PackerSectionSignature").ToList();
                Assert.Equal(2, packerFindings.Count);
                Assert.Contains(packerFindings, f => f.Target == "UPX0");
                Assert.Contains(packerFindings, f => f.Target == "UPX1");
                Assert.All(packerFindings, f => Assert.Contains("ForceDump=true", f.Remediation));
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public void RedirectionHeuristics_RelocsStrippedOrVirtualExceedsRaw_ReportsFinding()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "redir_diag_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var metaPath = Path.Combine(tempDir, "global-metadata.dat");
                File.WriteAllBytes(metaPath, CreateMinimalMetadata());

                var peSections = new[]
                {
                    (".text", new byte[512], 512u, 0x60000020u)
                };
                // relocsStripped = true triggers redirection heuristic
                var peBytes = CreateSyntheticPe(peSections, relocsStripped: true);
                var pePath = Path.Combine(tempDir, "redir.exe");
                File.WriteAllBytes(pePath, peBytes);

                var report = ProtectionDiagnostics.Analyze(pePath, metaPath, new Config());
                var redirFinding = Assert.Single(report.Findings.Where(f => f.Finding == "RedirectionHeuristic"));
                Assert.Equal("PE", redirFinding.Target);
                Assert.Equal("Try NoRedirectedPointer=true", redirFinding.Remediation);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Theory]
        [InlineData(16)]
        [InlineData(29)]
        [InlineData(35)]
        [InlineData(38)]
        [InlineData(106)]
        [InlineData(200)]
        public void D1_MetadataSanity_SupportedVersionRange16To200_ReportsOkTrue(int version)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "d1_ok_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var metaPath = Path.Combine(tempDir, "global-metadata.dat");
                File.WriteAllBytes(metaPath, CreateMinimalMetadata(version: version));

                var exePath = Path.Combine(tempDir, "dummy.bin");
                File.WriteAllBytes(exePath, new byte[] { 0, 0, 0, 0 });

                var report = ProtectionDiagnostics.Analyze(exePath, metaPath, new Config());
                var sanity = Assert.Single(report.Findings.Where(f => f.Finding == "MetadataSanity"));
                Assert.True(sanity.Ok, $"Expected Ok=true for version {version}");
                Assert.Null(sanity.Detail);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Theory]
        [InlineData(15)]
        [InlineData(201)]
        public void D1_MetadataSanity_OutOfRangeVersion_ReportsNegativeSanityWithClearDetail(int version)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "d1_fail_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var metaPath = Path.Combine(tempDir, "global-metadata.dat");
                File.WriteAllBytes(metaPath, CreateMinimalMetadata(version: version));

                var exePath = Path.Combine(tempDir, "dummy.bin");
                File.WriteAllBytes(exePath, new byte[] { 0, 0, 0, 0 });

                var report = ProtectionDiagnostics.Analyze(exePath, metaPath, new Config());
                var sanity = Assert.Single(report.Findings.Where(f => f.Finding == "MetadataSanity"));
                Assert.False(sanity.Ok);
                Assert.Contains(version.ToString(), sanity.Detail);
                Assert.Contains("expected 16-200", sanity.Detail);
                Assert.Equal("Provide a compatible global-metadata.dat with supported format version (16-200).", sanity.Remediation);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public void D2_MachO_32BitAnd64Bit_SecondSection_FindingsDetected()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "d2_macho_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var metaPath = Path.Combine(tempDir, "global-metadata.dat");
                File.WriteAllBytes(metaPath, CreateMinimalMetadata());

                // Prepare second section bytes: uniform 256 bytes repeating -> entropy 8.00
                var uniformBytes = new byte[256];
                for (int i = 0; i < 256; i++) uniformBytes[i] = (byte)i;

                var sections = new[]
                {
                    ("__text", new byte[512]), // benign section 1
                    (".vmp0", uniformBytes)    // packed & high-entropy section 2
                };

                // 1. Mach-O 32-bit test
                var macho32Bytes = CreateSyntheticMacho32(sections);
                var macho32Path = Path.Combine(tempDir, "sample32.macho");
                File.WriteAllBytes(macho32Path, macho32Bytes);

                var report32 = ProtectionDiagnostics.Analyze(macho32Path, metaPath, new Config());
                var packer32 = Assert.Single(report32.Findings.Where(f => f.Finding == "PackerSectionSignature"));
                Assert.Equal(".vmp0", packer32.Target);
                var entropy32 = Assert.Single(report32.Findings.Where(f => f.Finding == "HighEntropySection"));
                Assert.Equal(".vmp0", entropy32.Target);
                Assert.Equal(8.00, entropy32.Entropy);

                // 2. Mach-O 64-bit test
                var macho64Bytes = CreateSyntheticMacho64(sections);
                var macho64Path = Path.Combine(tempDir, "sample64.macho");
                File.WriteAllBytes(macho64Path, macho64Bytes);

                var report64 = ProtectionDiagnostics.Analyze(macho64Path, metaPath, new Config());
                var packer64 = Assert.Single(report64.Findings.Where(f => f.Finding == "PackerSectionSignature"));
                Assert.Equal(".vmp0", packer64.Target);
                var entropy64 = Assert.Single(report64.Findings.Where(f => f.Finding == "HighEntropySection"));
                Assert.Equal(".vmp0", entropy64.Target);
                Assert.Equal(8.00, entropy64.Entropy);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public void D3_Elf_SectionLargerThan16MiB_EntropyComputedAcrossEntireSectionWithoutTruncation()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "d3_elf_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var metaPath = Path.Combine(tempDir, "global-metadata.dat");
                File.WriteAllBytes(metaPath, CreateMinimalMetadata());

                var elfPath = Path.Combine(tempDir, "large_section.elf");
                const long sectionSize = 20L * 1024 * 1024; // 20 MiB

                // 20 MiB section: 16 MiB uniform bytes followed by 4 MiB zeroes -> exact entropy = 7.10
                WriteSyntheticElf64(elfPath, ".text", sectionSize, stream =>
                {
                    var block = new byte[65536];
                    for (int i = 0; i < block.Length; i++) block[i] = (byte)(i % 256);

                    // 16 MiB of uniform bytes
                    long written = 0;
                    while (written < 16L * 1024 * 1024)
                    {
                        stream.Write(block, 0, block.Length);
                        written += block.Length;
                    }

                    // 4 MiB of zeroes
                    Array.Clear(block, 0, block.Length);
                    while (written < sectionSize)
                    {
                        stream.Write(block, 0, block.Length);
                        written += block.Length;
                    }
                });

                var report = ProtectionDiagnostics.Analyze(elfPath, metaPath, new Config());
                // Total entropy is 7.10 (< 7.20 threshold) -> must NOT trigger HighEntropySection
                Assert.DoesNotContain(report.Findings, f => f.Finding == "HighEntropySection");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public void D4_MetadataSanity_NegativeSanity_RemediationNeverSuggestsPostMetadataFlags()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "d4_remediation_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var dummyExe = Path.Combine(tempDir, "dummy.bin");
                File.WriteAllBytes(dummyExe, new byte[] { 0, 0, 0, 0 });

                // 1. Missing metadata file
                var reportMissing = ProtectionDiagnostics.Analyze(dummyExe, Path.Combine(tempDir, "nonexistent.dat"), new Config());
                var sanityMissing = Assert.Single(reportMissing.Findings.Where(f => f.Finding == "MetadataSanity"));
                Assert.False(sanityMissing.Ok);
                AssertInapplicableSwitchesOmitted(sanityMissing.Remediation);

                // 2. Truncated metadata
                var truncPath = Path.Combine(tempDir, "trunc.dat");
                File.WriteAllBytes(truncPath, new byte[] { 0xAF, 0x1B, 0xB1 });
                var reportTrunc = ProtectionDiagnostics.Analyze(dummyExe, truncPath, new Config());
                var sanityTrunc = Assert.Single(reportTrunc.Findings.Where(f => f.Finding == "MetadataSanity"));
                Assert.False(sanityTrunc.Ok);
                AssertInapplicableSwitchesOmitted(sanityTrunc.Remediation);

                // 3. Bad magic
                var badMagicPath = Path.Combine(tempDir, "badmagic.dat");
                File.WriteAllBytes(badMagicPath, CreateMinimalMetadata(magic: 0x12345678));
                var reportMagic = ProtectionDiagnostics.Analyze(dummyExe, badMagicPath, new Config());
                var sanityMagic = Assert.Single(reportMagic.Findings.Where(f => f.Finding == "MetadataSanity"));
                Assert.False(sanityMagic.Ok);
                AssertInapplicableSwitchesOmitted(sanityMagic.Remediation);

                // 4. Unsupported version
                var badVerPath = Path.Combine(tempDir, "badver.dat");
                File.WriteAllBytes(badVerPath, CreateMinimalMetadata(version: 201));
                var reportVer = ProtectionDiagnostics.Analyze(dummyExe, badVerPath, new Config());
                var sanityVer = Assert.Single(reportVer.Findings.Where(f => f.Finding == "MetadataSanity"));
                Assert.False(sanityVer.Ok);
                AssertInapplicableSwitchesOmitted(sanityVer.Remediation);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }

            static void AssertInapplicableSwitchesOmitted(string? remediation)
            {
                Assert.NotNull(remediation);
                Assert.DoesNotContain("ForceIl2CppVersion", remediation);
                Assert.DoesNotContain("ForceVersion", remediation);
                Assert.DoesNotContain("ForceDump", remediation);
                Assert.DoesNotContain("NoRedirectedPointer", remediation);
            }
        }

        [Fact]
        public void RemediationStrings_ReferenceOnlyValidConfigKeys()
        {
            var configProperties = typeof(Config).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => p.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var keyPattern = new Regex(@"\b([A-Za-z0-9_]+)=");

            var tempDir = Path.Combine(Path.GetTempPath(), "remediation_check_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var metaPath = Path.Combine(tempDir, "global-metadata.dat");
                File.WriteAllBytes(metaPath, CreateMinimalMetadata());

                var rnd = new Random(123);
                var rndBytes = new byte[1024];
                rnd.NextBytes(rndBytes);

                var peSections = new[]
                {
                    ("UPX0", rndBytes, 4096u, 0x60000020u)
                };
                var pePath = Path.Combine(tempDir, "test.exe");
                File.WriteAllBytes(pePath, CreateSyntheticPe(peSections, relocsStripped: true));

                var report = ProtectionDiagnostics.Analyze(pePath, metaPath, new Config());
                Assert.NotEmpty(report.Findings);

                foreach (var finding in report.Findings)
                {
                    if (string.IsNullOrEmpty(finding.Remediation))
                        continue;

                    var matches = keyPattern.Matches(finding.Remediation);
                    foreach (Match m in matches)
                    {
                        var key = m.Groups[1].Value;
                        Assert.True(configProperties.Contains(key),
                            $"Remediation string '{finding.Remediation}' references nonexistent config key '{key}'");
                    }
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public void D3_R1_Wasm_MultipleSections_EntropyComputedForSubsequentSectionsWithoutCursorDrift()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "wasm_diag_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var metaPath = Path.Combine(tempDir, "global-metadata.dat");
                File.WriteAllBytes(metaPath, CreateMinimalMetadata());

                // Section 1: Type section (id 1) with low entropy (256 zeroes -> entropy 0.00)
                var lowPayload = new byte[256];

                // Section 2: Data section (id 11) with maximum entropy (256 distinct bytes -> entropy 8.00)
                var uniformHighPayload = new byte[256];
                for (int i = 0; i < 256; i++) uniformHighPayload[i] = (byte)i;

                // Section 3: Custom section (id 0) with another distinct high entropy payload
                var uniformHighPayload2 = new byte[256];
                for (int i = 0; i < 256; i++) uniformHighPayload2[i] = (byte)(255 - i);

                var wasmBytes = CreateMultiSectionSyntheticWasm(new[]
                {
                    ((byte)1, lowPayload),
                    ((byte)11, uniformHighPayload),
                    ((byte)0, uniformHighPayload2)
                });

                var wasmPath = Path.Combine(tempDir, "multi_section.wasm");
                File.WriteAllBytes(wasmPath, wasmBytes);

                var report = ProtectionDiagnostics.Analyze(wasmPath, metaPath, new Config());

                // Diagnostics notes must not report read errors or stream bounds issues
                Assert.DoesNotContain(report.Notes, n => n.Contains("warning") || n.Contains("error") || n.Contains("inaccessible"));

                // HighEntropySection should be reported for both subsequent high entropy sections (data and custom)
                var highFindings = report.Findings.Where(f => f.Finding == "HighEntropySection").ToList();
                Assert.Equal(2, highFindings.Count);
                Assert.Contains(highFindings, f => f.Target == "data" && f.Entropy == 8.00);
                Assert.Contains(highFindings, f => f.Target == "custom" && f.Entropy == 8.00);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public void GenerateProtectionReport_False_NoFileWritten()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "report_flag_false_" + Guid.NewGuid().ToString("N"));
            var outDir = Path.Combine(tempDir, "out");
            Directory.CreateDirectory(tempDir);
            try
            {
                var metaPath = Path.Combine(tempDir, "global-metadata.dat");
                File.WriteAllBytes(metaPath, CreateMinimalMetadata());

                var pePath = Path.Combine(tempDir, "test.exe");
                File.WriteAllBytes(pePath, CreateSyntheticPe(new[] { (".text", new byte[512], 512u, 0x60000020u) }));

                var config = new Config
                {
                    GenerateProtectionReport = false,
                    RequireAnyKey = false
                };

                var request = new DumpRequest
                {
                    ExecutablePath = pePath,
                    MetadataPath = metaPath,
                    OutputDirectory = outDir,
                    Config = config
                };

                var result = Dumper.Dump(request);
                var reportPath = Path.Combine(outDir, "protection-report.json");
                Assert.False(File.Exists(reportPath));
                Assert.DoesNotContain("protection-report.json", result.Outputs.Keys);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public void ByteEqualityRegression_DumpOutputsIdentical_WithReportFlagOnAndOff()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "byte_eq_test_" + Guid.NewGuid().ToString("N"));
            var outOn = Path.Combine(tempDir, "out_on");
            var outOff = Path.Combine(tempDir, "out_off");
            Directory.CreateDirectory(tempDir);
            try
            {
                var metaPath = Path.Combine(tempDir, "global-metadata.dat");
                File.WriteAllBytes(metaPath, CreateMinimalMetadata());

                var wasmBytes = CreateSyntheticWasm(new byte[1024]);
                var wasmPath = Path.Combine(tempDir, "test.wasm");
                File.WriteAllBytes(wasmPath, wasmBytes);

                var scriptPath = Path.Combine(tempDir, "script.json");
                File.WriteAllText(scriptPath, JsonSerializer.Serialize(new
                {
                    codeRegistration = 32UL,
                    metadataRegistration = 256UL
                }));

                // Run with GenerateProtectionReport = true
                var configOn = new Config
                {
                    GenerateProtectionReport = true,
                    GenerateRunManifest = false, // exclude run-manifest to compare core dump outputs
                    RequireAnyKey = false
                };
                var reqOn = new DumpRequest
                {
                    ExecutablePath = wasmPath,
                    MetadataPath = metaPath,
                    OutputDirectory = outOn,
                    Config = configOn
                };
                var resOn = Dumper.Dump(reqOn);

                // Run with GenerateProtectionReport = false
                var configOff = new Config
                {
                    GenerateProtectionReport = false,
                    GenerateRunManifest = false,
                    RequireAnyKey = false
                };
                var reqOff = new DumpRequest
                {
                    ExecutablePath = wasmPath,
                    MetadataPath = metaPath,
                    OutputDirectory = outOff,
                    Config = configOff
                };
                var resOff = Dumper.Dump(reqOff);

                // Compare common dump outputs (excluding metadata/manifest/diagnostic reports)
                var metadataFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "dump-metadata.json",
                    "run-manifest.json",
                    "protection-report.json"
                };

                var filesOff = Directory.GetFiles(outOff)
                    .Select(Path.GetFileName)
                    .Where(f => !metadataFiles.Contains(f!))
                    .ToList();
                Assert.NotEmpty(filesOff);

                foreach (var fileName in filesOff)
                {
                    var fileOn = Path.Combine(outOn, fileName!);
                    var fileOff = Path.Combine(outOff, fileName!);

                    Assert.True(File.Exists(fileOn), $"File {fileName} missing from outOn");
                    var bytesOn = File.ReadAllBytes(fileOn);
                    var bytesOff = File.ReadAllBytes(fileOff);
                    Assert.Equal(bytesOff, bytesOn);
                }

                // Verify protection-report.json exists only in outOn
                Assert.True(File.Exists(Path.Combine(outOn, "protection-report.json")));
                Assert.False(File.Exists(Path.Combine(outOff, "protection-report.json")));
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public void PeIntegrityBaseline_Sections_CoffTimestamp_ImageChecksum_ReportedAccurately()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "pe_baseline_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var metaPath = Path.Combine(tempDir, "global-metadata.dat");
                File.WriteAllBytes(metaPath, CreateMinimalMetadata());

                var textBytes = new byte[1024];
                for (int i = 0; i < textBytes.Length; i++) textBytes[i] = (byte)(i % 256);
                var expectedTextSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(textBytes)).ToLowerInvariant();
                var expectedTextEntropy = ProtectionDiagnostics.CalculateEntropy(textBytes, 0, textBytes.Length);

                var rdataBytes = new byte[512];
                var expectedRdataSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(rdataBytes)).ToLowerInvariant();
                var expectedRdataEntropy = ProtectionDiagnostics.CalculateEntropy(rdataBytes, 0, rdataBytes.Length);

                var sections = new[]
                {
                    (".text", textBytes, 4096u, 0x60000020u),
                    (".rdata", rdataBytes, 1024u, 0x40000040u)
                };

                // TimeDateStamp = 1762473600 -> 2025-11-07T00:00:00Z
                uint timeDateStamp = 1762473600u;
                uint imageChecksum = 0x12345678u;

                var peBytes = CreateSyntheticPe(sections, relocsStripped: false, timeDateStamp: timeDateStamp, checkSum: imageChecksum);
                var pePath = Path.Combine(tempDir, "baseline_target.dll");
                File.WriteAllBytes(pePath, peBytes);

                var report = ProtectionDiagnostics.Analyze(pePath, metaPath, new Config());

                Assert.NotNull(report.Sections);
                Assert.Equal(2, report.Sections.Count);

                var textSec = report.Sections[0];
                Assert.Equal(".text", textSec.Name);
                Assert.Equal(4096u, textSec.VirtualSize);
                Assert.Equal((uint)textBytes.Length, textSec.RawSize);
                Assert.Equal(expectedTextSha256, textSec.Sha256);
                Assert.Equal(expectedTextEntropy, textSec.Entropy);

                var rdataSec = report.Sections[1];
                Assert.Equal(".rdata", rdataSec.Name);
                Assert.Equal(1024u, rdataSec.VirtualSize);
                Assert.Equal((uint)rdataBytes.Length, rdataSec.RawSize);
                Assert.Equal(expectedRdataSha256, rdataSec.Sha256);
                Assert.Equal(expectedRdataEntropy, rdataSec.Entropy);

                Assert.Equal("2025-11-07T00:00:00Z", report.CoffTimestamp);
                Assert.Equal(0x12345678u, report.ImageChecksum);

                // Serialized JSON schema validation against issue #67 proposal
                var json = JsonSerializer.Serialize(report, ProtectionReportWriter.JsonOptions);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                Assert.True(root.TryGetProperty("sections", out var sectionsProp));
                Assert.Equal(JsonValueKind.Array, sectionsProp.ValueKind);
                Assert.Equal(2, sectionsProp.GetArrayLength());

                var firstElem = sectionsProp[0];
                Assert.Equal(".text", firstElem.GetProperty("name").GetString());
                Assert.Equal(4096u, firstElem.GetProperty("virtualSize").GetUInt32());
                Assert.Equal(1024u, firstElem.GetProperty("rawSize").GetUInt32());
                Assert.Equal(expectedTextSha256, firstElem.GetProperty("sha256").GetString());
                Assert.Equal(expectedTextEntropy, firstElem.GetProperty("entropy").GetDouble());

                Assert.Equal("2025-11-07T00:00:00Z", root.GetProperty("coffTimestamp").GetString());
                Assert.Equal(0x12345678u, root.GetProperty("imageChecksum").GetUInt32());
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public void NonPeInputs_OmitSectionsAndPeFieldsFromProtectionReport()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "non_pe_baseline_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var metaPath = Path.Combine(tempDir, "global-metadata.dat");
                File.WriteAllBytes(metaPath, CreateMinimalMetadata());

                var wasmBytes = CreateSyntheticWasm(new byte[256]);
                var wasmPath = Path.Combine(tempDir, "target.wasm");
                File.WriteAllBytes(wasmPath, wasmBytes);

                var report = ProtectionDiagnostics.Analyze(wasmPath, metaPath, new Config());

                Assert.Null(report.Sections);
                Assert.Null(report.CoffTimestamp);
                Assert.Null(report.ImageChecksum);

                var json = JsonSerializer.Serialize(report, ProtectionReportWriter.JsonOptions);
                Assert.DoesNotContain("\"sections\":", json);
                Assert.DoesNotContain("\"coffTimestamp\":", json);
                Assert.DoesNotContain("\"imageChecksum\":", json);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public void PeIntegrityBaseline_TruncatedSection_InvalidatesBaselineAndRecordsWarning()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "pe_trunc_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var metaPath = Path.Combine(tempDir, "global-metadata.dat");
                File.WriteAllBytes(metaPath, CreateMinimalMetadata());

                var textBytes = new byte[1024];
                var peSections = new[]
                {
                    (".text", textBytes, 1024u, 0x60000020u)
                };
                var peBytes = CreateSyntheticPe(peSections);

                // Truncate the file so .text raw data range extends beyond file length
                // In CreateSyntheticPe, raw data is written starting at offset 0x400 (1024) to 0x800 (2048)
                // Truncate to 1536 bytes (inside the section raw data range)
                var truncatedPeBytes = new byte[1536];
                Array.Copy(peBytes, truncatedPeBytes, 1536);

                var pePath = Path.Combine(tempDir, "truncated.exe");
                File.WriteAllBytes(pePath, truncatedPeBytes);

                var report = ProtectionDiagnostics.Analyze(pePath, metaPath, new Config());

                // Baseline must be completely invalidated rather than emitting an empty/corrupted digest
                Assert.Null(report.Sections);
                Assert.Contains(report.Notes, note => note.Contains("raw data range") && note.Contains("exceeds executable length"));
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public void PeIntegrityBaseline_EmptySection_ReportsEmptySha256AndZeroEntropy()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "pe_empty_sec_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var metaPath = Path.Combine(tempDir, "global-metadata.dat");
                File.WriteAllBytes(metaPath, CreateMinimalMetadata());

                var peSections = new[]
                {
                    (".bss", Array.Empty<byte>(), 4096u, 0xC0000080u)
                };
                var peBytes = CreateSyntheticPe(peSections);
                var pePath = Path.Combine(tempDir, "empty_sec.exe");
                File.WriteAllBytes(pePath, peBytes);

                var report = ProtectionDiagnostics.Analyze(pePath, metaPath, new Config());

                Assert.NotNull(report.Sections);
                var sec = Assert.Single(report.Sections);
                Assert.Equal(".bss", sec.Name);
                Assert.Equal(0u, sec.RawSize);
                Assert.Equal(4096u, sec.VirtualSize);
                Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", sec.Sha256);
                Assert.Equal(0.0, sec.Entropy);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public void PeIntegrityBaseline_SectionLargerThan64KiB_StreamsAcrossMultipleChunks()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "pe_large_sec_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var metaPath = Path.Combine(tempDir, "global-metadata.dat");
                File.WriteAllBytes(metaPath, CreateMinimalMetadata());

                var largeData = new byte[100_000];
                for (int i = 0; i < largeData.Length; i++)
                {
                    largeData[i] = (byte)(i % 251);
                }
                var expectedSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(largeData)).ToLowerInvariant();
                var expectedEntropy = ProtectionDiagnostics.CalculateEntropy(largeData, 0, largeData.Length);

                var peSections = new[]
                {
                    (".text", largeData, (uint)largeData.Length, 0x60000020u)
                };
                var peBytes = CreateSyntheticPe(peSections);
                var pePath = Path.Combine(tempDir, "large_sec.exe");
                File.WriteAllBytes(pePath, peBytes);

                var report = ProtectionDiagnostics.Analyze(pePath, metaPath, new Config());

                Assert.NotNull(report.Sections);
                var sec = Assert.Single(report.Sections);
                Assert.Equal(".text", sec.Name);
                Assert.Equal((uint)largeData.Length, sec.RawSize);
                Assert.Equal(expectedSha256, sec.Sha256);
                Assert.Equal(expectedEntropy, sec.Entropy);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }
}
