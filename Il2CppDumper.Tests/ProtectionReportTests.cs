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
            bool relocsStripped = false)
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
            w.Write((uint)0); // TimeDateStamp
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
            w.Write((uint)0);       // CheckSum
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

        [Fact]
        public void MetadataSanity_CorruptedMagicOrVersion_ReportsNegativeSanityWithRemediation()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "meta_sanity_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var dummyExe = Path.Combine(tempDir, "dummy.bin");
                File.WriteAllBytes(dummyExe, new byte[] { 0, 0, 0, 0 });

                // Bad magic
                var badMagicMeta = Path.Combine(tempDir, "badmagic.dat");
                File.WriteAllBytes(badMagicMeta, CreateMinimalMetadata(magic: 0x12345678));

                var reportBadMagic = ProtectionDiagnostics.Analyze(dummyExe, badMagicMeta, new Config());
                var magicFinding = Assert.Single(reportBadMagic.Findings.Where(f => f.Finding == "MetadataSanity"));
                Assert.False(magicFinding.Ok);
                Assert.Contains("0x12345678", magicFinding.Detail);
                Assert.Contains("ForceIl2CppVersion=true", magicFinding.Remediation);
                Assert.Contains("ForceVersion=<version>", magicFinding.Remediation);
                Assert.Contains("ForceDump=true", magicFinding.Remediation);

                // Unsupported version
                var badVersionMeta = Path.Combine(tempDir, "badver.dat");
                File.WriteAllBytes(badVersionMeta, CreateMinimalMetadata(version: 99));

                var reportBadVer = ProtectionDiagnostics.Analyze(dummyExe, badVersionMeta, new Config());
                var verFinding = Assert.Single(reportBadVer.Findings.Where(f => f.Finding == "MetadataSanity"));
                Assert.False(verFinding.Ok);
                Assert.Contains("99", verFinding.Detail);
                Assert.Contains("ForceIl2CppVersion=true", verFinding.Remediation);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
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
                File.WriteAllBytes(metaPath, CreateMinimalMetadata(version: 99));

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
                    Assert.NotEmpty(matches);
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
    }
}
