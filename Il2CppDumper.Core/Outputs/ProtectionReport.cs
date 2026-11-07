#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Il2CppDumper
{
    public class ProtectionReport
    {
        [JsonPropertyName("sections")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<ProtectionSectionEntry>? Sections { get; set; }

        [JsonPropertyName("coffTimestamp")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? CoffTimestamp { get; set; }

        [JsonPropertyName("imageChecksum")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public uint? ImageChecksum { get; set; }

        [JsonPropertyName("findings")]
        public List<ProtectionFinding> Findings { get; set; } = new();

        [JsonPropertyName("notes")]
        public List<string> Notes { get; set; } = new();
    }

    public class ProtectionSectionEntry
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("virtualSize")]
        public uint VirtualSize { get; set; }

        [JsonPropertyName("rawSize")]
        public uint RawSize { get; set; }

        [JsonPropertyName("sha256")]
        public string Sha256 { get; set; } = string.Empty;

        [JsonPropertyName("entropy")]
        public double Entropy { get; set; }
    }

    public class ProtectionFinding
    {
        [JsonPropertyName("finding")]
        public string Finding { get; set; } = string.Empty;

        [JsonPropertyName("target")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Target { get; set; }

        [JsonPropertyName("entropy")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? Entropy { get; set; }

        [JsonPropertyName("detail")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Detail { get; set; }

        [JsonPropertyName("ok")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? Ok { get; set; }

        [JsonPropertyName("remediation")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Remediation { get; set; }
    }

    public static class ProtectionDiagnostics
    {
        public const double HighEntropyThreshold = 7.20;

        public static readonly IReadOnlyCollection<string> GenericPackerSignatures = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "UPX0", "UPX1", "UPX2",
            ".aspack", ".adata",
            ".mpress1", ".mpress2",
            ".themida", ".vmp0", ".vmp1",
            ".pack", "PELOCK"
        };

        public static (double entropy, string sha256) CalculateSectionMetrics(Stream stream, long offset, long length, int bufferSize = 65536)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            if (length < 0 || offset < 0 || (stream.Length - offset) < length)
                throw new ArgumentOutOfRangeException(nameof(length), "Requested section range is out of stream bounds.");

            if (length == 0)
                return (0.0, "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");

            stream.Position = offset;
            var frequencies = new long[256];
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[Math.Min(bufferSize, (int)Math.Min(length, 65536))];
            long remaining = length;

            while (remaining > 0)
            {
                int toRead = (int)Math.Min(remaining, buffer.Length);
                int read = stream.Read(buffer, 0, toRead);
                if (read <= 0) break;

                hasher.AppendData(buffer, 0, read);
                for (int i = 0; i < read; i++)
                {
                    frequencies[buffer[i]]++;
                }
                remaining -= read;
            }

            if (remaining > 0)
                throw new EndOfStreamException($"Stream ended prematurely while reading section bytes (expected {length}, remaining {remaining}).");

            var hashBytes = hasher.GetHashAndReset();
            var sha256 = Convert.ToHexString(hashBytes).ToLowerInvariant();

            double entropy = 0.0;
            double invTotal = 1.0 / length;

            for (int i = 0; i < 256; i++)
            {
                long count = frequencies[i];
                if (count > 0)
                {
                    double p = count * invTotal;
                    entropy -= p * Math.Log2(p);
                }
            }

            return (Math.Round(entropy, 2, MidpointRounding.AwayFromZero), sha256);
        }

        public static double CalculateEntropy(Stream stream, long offset, long length, int bufferSize = 65536)
        {
            return CalculateSectionMetrics(stream, offset, length, bufferSize).entropy;
        }

        public static double CalculateEntropy(byte[] data, int offset, int length)
        {
            if (data == null || length <= 0 || offset < 0 || offset + length > data.Length)
                return 0.0;

            using var ms = new MemoryStream(data, offset, length, writable: false);
            return CalculateEntropy(ms, 0, length);
        }

        public static ProtectionReport Analyze(string executablePath, string metadataPath, Config config)
        {
            var findings = new List<ProtectionFinding>();
            var notes = new List<string>();
            List<ProtectionSectionEntry>? peSections = null;
            string? coffTimestamp = null;
            uint? imageChecksum = null;

            if (!string.IsNullOrWhiteSpace(executablePath) && File.Exists(executablePath))
            {
                try
                {
                    AnalyzeExecutable(executablePath, findings, notes, out peSections, out coffTimestamp, out imageChecksum);
                }
                catch (Exception ex)
                {
                    notes.Add($"Executable diagnostics warning: {ex.Message}");
                }
            }
            else
            {
                notes.Add("Executable file not found or inaccessible for protection diagnostics.");
            }

            if (!string.IsNullOrWhiteSpace(metadataPath))
            {
                try
                {
                    AnalyzeMetadata(metadataPath, findings, notes);
                }
                catch (Exception ex)
                {
                    notes.Add($"Metadata diagnostics warning: {ex.Message}");
                }
            }

            return new ProtectionReport
            {
                Sections = peSections,
                CoffTimestamp = coffTimestamp,
                ImageChecksum = imageChecksum,
                Findings = findings,
                Notes = notes
            };
        }

        private static void AnalyzeExecutable(
            string executablePath,
            List<ProtectionFinding> findings,
            List<string> notes,
            out List<ProtectionSectionEntry>? peSections,
            out string? coffTimestamp,
            out uint? imageChecksum)
        {
            peSections = null;
            coffTimestamp = null;
            imageChecksum = null;

            using var fs = new FileStream(executablePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var fileLen = fs.Length;
            if (fileLen < 4)
            {
                notes.Add("Executable file is too small to inspect headers.");
                return;
            }

            using var reader = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);
            var magic = reader.ReadUInt32();
            fs.Position = 0;

            var disambiguator = new SectionNameDisambiguator();

            if ((magic & 0xFFFF) == 0x5A4D) // 'MZ' -> PE
            {
                AnalyzePE(fs, reader, disambiguator, findings, notes, out peSections, out coffTimestamp, out imageChecksum);
            }
            else if (magic == 0x464C457F) // '\x7FELF' -> ELF
            {
                AnalyzeElf(fs, reader, disambiguator, findings, notes);
            }
            else if (magic == 0xFEEDFACE || magic == 0xFEEDFACF || magic == 0xCEFAEDFE || magic == 0xCFFAEDFE) // Mach-O
            {
                AnalyzeMacho(fs, reader, disambiguator, findings, notes);
            }
            else if (magic == 0x6D736100) // '\0asm' -> WebAssembly
            {
                AnalyzeWasm(fs, reader, disambiguator, findings, notes);
            }
            else
            {
                AnalyzeGenericBinary(fs, disambiguator, findings, notes);
            }
        }

        private static void AnalyzePE(
            FileStream fs,
            BinaryReader reader,
            SectionNameDisambiguator disambiguator,
            List<ProtectionFinding> findings,
            List<string> notes,
            out List<ProtectionSectionEntry>? sectionsOut,
            out string? coffTimestampOut,
            out uint? imageChecksumOut)
        {
            sectionsOut = null;
            coffTimestampOut = null;
            imageChecksumOut = null;

            if (fs.Length < 0x40) return;

            fs.Position = 0x3C;
            var lfanew = reader.ReadUInt32();
            if (lfanew + 24 > fs.Length) return;

            fs.Position = lfanew;
            var peSig = reader.ReadUInt32();
            if (peSig != 0x00004550) return; // "PE\0\0"

            var machine = reader.ReadUInt16();
            var numberOfSections = reader.ReadUInt16();
            var timeDateStamp = reader.ReadUInt32();
            var pointerToSymbolTable = reader.ReadUInt32();
            var numberOfSymbols = reader.ReadUInt32();
            var sizeOfOptionalHeader = reader.ReadUInt16();
            var characteristics = reader.ReadUInt16();

            var optHeaderPos = fs.Position;
            var optMagic = sizeOfOptionalHeader >= 2 ? reader.ReadUInt16() : (ushort)0;

            uint checkSum = 0;
            if (sizeOfOptionalHeader >= 68 && (optMagic == 0x10b || optMagic == 0x20b))
            {
                fs.Position = optHeaderPos + 64;
                checkSum = reader.ReadUInt32();
            }

            coffTimestampOut = DateTimeOffset.FromUnixTimeSeconds(timeDateStamp).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            imageChecksumOut = checkSum;

            bool redirectionFound = false;
            // Check relocs stripped
            if ((characteristics & 0x0001) != 0)
            {
                redirectionFound = true;
            }

            var sectionEntries = new List<ProtectionSectionEntry>();
            var sectionPos = optHeaderPos + sizeOfOptionalHeader;
            for (int i = 0; i < numberOfSections && sectionPos + 40 <= fs.Length; i++)
            {
                fs.Position = sectionPos + (i * 40);
                var rawNameBytes = reader.ReadBytes(8);
                var rawName = Encoding.ASCII.GetString(rawNameBytes).TrimEnd('\0').Trim();
                var name = disambiguator.Disambiguate(rawName);

                var virtualSize = reader.ReadUInt32();
                var virtualAddress = reader.ReadUInt32();
                var sizeOfRawData = reader.ReadUInt32();
                var pointerToRawData = reader.ReadUInt32();
                reader.ReadUInt32(); // pointerToRelocations
                reader.ReadUInt32(); // pointerToLinenumbers
                reader.ReadUInt16(); // numberOfRelocations
                reader.ReadUInt16(); // numberOfLinenumbers
                var sectionCharacteristics = reader.ReadUInt32();

                // Check packer signature
                if (GenericPackerSignatures.Contains(rawName))
                {
                    findings.Add(new ProtectionFinding
                    {
                        Finding = "PackerSectionSignature",
                        Target = name,
                        Detail = $"Section '{name}' matches generic packer signature.",
                        Remediation = "Target appears packed; consider an unpacked or memory-dumped binary, or try ForceDump=true"
                    });
                }

                // Validate section raw data bounds
                if (sizeOfRawData > 0)
                {
                    if (pointerToRawData > fs.Length || (fs.Length - pointerToRawData) < sizeOfRawData)
                    {
                        throw new InvalidDataException(
                            $"Section '{name}' raw data range [0x{pointerToRawData:X}..0x{(long)pointerToRawData + sizeOfRawData:X}) " +
                            $"exceeds executable length (0x{fs.Length:X}).");
                    }
                }

                // Compute baseline metrics (entropy & sha256)
                double entropy = 0.0;
                string sha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
                if (sizeOfRawData > 0)
                {
                    (entropy, sha256) = CalculateSectionMetrics(fs, pointerToRawData, sizeOfRawData);
                    if (entropy >= HighEntropyThreshold)
                    {
                        findings.Add(new ProtectionFinding
                        {
                            Finding = "HighEntropySection",
                            Target = name,
                            Entropy = entropy,
                            Remediation = "Consider a memory-dumped input; if pointers are redirected set NoRedirectedPointer=true"
                        });
                    }
                }

                sectionEntries.Add(new ProtectionSectionEntry
                {
                    Name = name,
                    VirtualSize = virtualSize,
                    RawSize = sizeOfRawData,
                    Sha256 = sha256,
                    Entropy = entropy
                });

                // Check redirection indicators
                if (sizeOfRawData == 0 && virtualSize > 0 && (sectionCharacteristics & 0x20000020) != 0)
                {
                    redirectionFound = true;
                }
            }

            sectionsOut = sectionEntries;

            if (redirectionFound)
            {
                findings.Add(new ProtectionFinding
                {
                    Finding = "RedirectionHeuristic",
                    Target = "PE",
                    Detail = "Section virtual size exceeds raw data size or relocations are missing/redirected.",
                    Remediation = "Try NoRedirectedPointer=true"
                });
            }
        }

        private static void AnalyzeElf(FileStream fs, BinaryReader reader, SectionNameDisambiguator disambiguator, List<ProtectionFinding> findings, List<string> notes)
        {
            if (fs.Length < 52) return;

            fs.Position = 4;
            var eiClass = reader.ReadByte(); // 1 = 32-bit, 2 = 64-bit

            long shoff = 0;
            ushort shentsize = 0;
            ushort shnum = 0;
            ushort shstrndx = 0;

            if (eiClass == 1) // ELF32
            {
                fs.Position = 32;
                shoff = reader.ReadUInt32();
                fs.Position = 46;
                shentsize = reader.ReadUInt16();
                shnum = reader.ReadUInt16();
                shstrndx = reader.ReadUInt16();
            }
            else // ELF64
            {
                if (fs.Length < 64) return;
                fs.Position = 40;
                shoff = (long)reader.ReadUInt64();
                fs.Position = 58;
                shentsize = reader.ReadUInt16();
                shnum = reader.ReadUInt16();
                shstrndx = reader.ReadUInt16();
            }

            if (shoff == 0 || shnum == 0)
            {
                findings.Add(new ProtectionFinding
                {
                    Finding = "RedirectionHeuristic",
                    Target = "ELF",
                    Detail = "Section headers stripped (e_shoff=0); program headers indicate potential memory-dumped or redirected binary.",
                    Remediation = "Try NoRedirectedPointer=true"
                });
                return;
            }

            if (shoff + (shnum * shentsize) > fs.Length || shstrndx >= shnum)
            {
                return;
            }

            // Read shstrtab string table offset and size
            long shstrtabOffset = 0;
            long shstrtabSize = 0;
            long strtabEntryPos = shoff + (shstrndx * shentsize);
            if (eiClass == 1)
            {
                fs.Position = strtabEntryPos + 16;
                shstrtabOffset = reader.ReadUInt32();
                shstrtabSize = reader.ReadUInt32();
            }
            else
            {
                fs.Position = strtabEntryPos + 24;
                shstrtabOffset = (long)reader.ReadUInt64();
                shstrtabSize = (long)reader.ReadUInt64();
            }

            byte[] shstrtabBytes = Array.Empty<byte>();
            if (shstrtabOffset + shstrtabSize <= fs.Length)
            {
                fs.Position = shstrtabOffset;
                shstrtabBytes = reader.ReadBytes((int)shstrtabSize);
            }

            string GetStringFromTable(uint offset)
            {
                if (offset >= shstrtabBytes.Length) return string.Empty;
                int end = (int)offset;
                while (end < shstrtabBytes.Length && shstrtabBytes[end] != 0) end++;
                return Encoding.UTF8.GetString(shstrtabBytes, (int)offset, end - (int)offset);
            }

            bool dynamicInitFound = false;

            for (int i = 0; i < shnum; i++)
            {
                long entryPos = shoff + (i * shentsize);
                fs.Position = entryPos;
                var shNameIdx = reader.ReadUInt32();
                var shType = reader.ReadUInt32();

                long shOffset = 0;
                long shSize = 0;

                if (eiClass == 1)
                {
                    fs.Position = entryPos + 16;
                    shOffset = reader.ReadUInt32();
                    shSize = reader.ReadUInt32();
                }
                else
                {
                    fs.Position = entryPos + 24;
                    shOffset = (long)reader.ReadUInt64();
                    shSize = (long)reader.ReadUInt64();
                }

                var rawName = GetStringFromTable(shNameIdx);
                var name = disambiguator.Disambiguate(rawName);

                if (GenericPackerSignatures.Contains(rawName))
                {
                    findings.Add(new ProtectionFinding
                    {
                        Finding = "PackerSectionSignature",
                        Target = name,
                        Detail = $"Section '{name}' matches generic packer signature.",
                        Remediation = "Target appears packed; consider an unpacked or memory-dumped binary, or try ForceDump=true"
                    });
                }

                if (rawName == ".init_proc" || rawName == ".init")
                {
                    dynamicInitFound = true;
                }

                if (shSize > 0 && shOffset + shSize <= fs.Length && shType != 8) // SHT_NOBITS = 8
                {
                    var entropy = CalculateEntropy(fs, shOffset, shSize);
                    if (entropy >= HighEntropyThreshold)
                    {
                        findings.Add(new ProtectionFinding
                        {
                            Finding = "HighEntropySection",
                            Target = name,
                            Entropy = entropy,
                            Remediation = "Consider a memory-dumped input; if pointers are redirected set NoRedirectedPointer=true"
                        });
                    }
                }
            }

            if (dynamicInitFound)
            {
                findings.Add(new ProtectionFinding
                {
                    Finding = "RedirectionHeuristic",
                    Target = "ELF",
                    Detail = "Dynamic initialization or .init_proc hook detected in executable headers.",
                    Remediation = "Try NoRedirectedPointer=true"
                });
            }
        }

        private static void AnalyzeMacho(FileStream fs, BinaryReader reader, SectionNameDisambiguator disambiguator, List<ProtectionFinding> findings, List<string> notes)
        {
            if (fs.Length < 32) return;

            fs.Position = 0;
            var magic = reader.ReadUInt32();
            bool is64Bit = (magic == 0xFEEDFACF || magic == 0xCFFAEDFE);

            fs.Position = 16;
            var nCmds = reader.ReadUInt32();
            var sizeOfCmds = reader.ReadUInt32();

            var pos = is64Bit ? 32L : 28L;
            for (int i = 0; i < nCmds && pos < fs.Length; i++)
            {
                fs.Position = pos;
                var cmd = reader.ReadUInt32();
                var cmdSize = reader.ReadUInt32();
                if (cmdSize == 0) break;

                if (cmd == 0x1 || cmd == 0x19) // LC_SEGMENT (32-bit) or LC_SEGMENT_64
                {
                    if (cmd == 0x1) // 32-bit segment (header size = 56 bytes, section stride = 68 bytes)
                    {
                        fs.Position = pos + 48; // nsects is at offset 48 from cmd start
                        var nsects = reader.ReadUInt32();

                        const long segmentHeaderSize = 56L;
                        const long sectionStride = 68L;

                        for (int j = 0; j < nsects; j++)
                        {
                            long sectionRecordStart = pos + segmentHeaderSize + (j * sectionStride);
                            if (sectionRecordStart + sectionStride > pos + cmdSize || sectionRecordStart + sectionStride > fs.Length)
                                break;

                            fs.Position = sectionRecordStart;
                            var sectNameBytes = reader.ReadBytes(16);
                            var rawSectName = Encoding.UTF8.GetString(sectNameBytes).TrimEnd('\0').Trim();
                            fs.Position = sectionRecordStart + 32; // skip sectname(16) + segname(16)
                            var addr = reader.ReadUInt32();
                            var size = reader.ReadUInt32();
                            var offset = reader.ReadUInt32();

                            var name = disambiguator.Disambiguate(rawSectName);
                            if (GenericPackerSignatures.Contains(rawSectName))
                            {
                                findings.Add(new ProtectionFinding
                                {
                                    Finding = "PackerSectionSignature",
                                    Target = name,
                                    Detail = $"Section '{name}' matches generic packer signature.",
                                    Remediation = "Target appears packed; consider an unpacked or memory-dumped binary, or try ForceDump=true"
                                });
                            }

                            if (size > 0 && offset + size <= fs.Length)
                            {
                                var entropy = CalculateEntropy(fs, offset, size);
                                if (entropy >= HighEntropyThreshold)
                                {
                                    findings.Add(new ProtectionFinding
                                    {
                                        Finding = "HighEntropySection",
                                        Target = name,
                                        Entropy = entropy,
                                        Remediation = "Consider a memory-dumped input; if pointers are redirected set NoRedirectedPointer=true"
                                    });
                                }
                            }
                        }
                    }
                    else // 64-bit segment (header size = 72 bytes, section stride = 80 bytes)
                    {
                        fs.Position = pos + 64; // nsects is at offset 64 from cmd start
                        var nsects = reader.ReadUInt32();

                        const long segmentHeaderSize = 72L;
                        const long sectionStride = 80L;

                        for (int j = 0; j < nsects; j++)
                        {
                            long sectionRecordStart = pos + segmentHeaderSize + (j * sectionStride);
                            if (sectionRecordStart + sectionStride > pos + cmdSize || sectionRecordStart + sectionStride > fs.Length)
                                break;

                            fs.Position = sectionRecordStart;
                            var sectNameBytes = reader.ReadBytes(16);
                            var rawSectName = Encoding.UTF8.GetString(sectNameBytes).TrimEnd('\0').Trim();
                            fs.Position = sectionRecordStart + 32; // skip sectname(16) + segname(16)
                            var addr = reader.ReadUInt64();
                            var size = reader.ReadUInt64();
                            var offset = reader.ReadUInt32();

                            var name = disambiguator.Disambiguate(rawSectName);
                            if (GenericPackerSignatures.Contains(rawSectName))
                            {
                                findings.Add(new ProtectionFinding
                                {
                                    Finding = "PackerSectionSignature",
                                    Target = name,
                                    Detail = $"Section '{name}' matches generic packer signature.",
                                    Remediation = "Target appears packed; consider an unpacked or memory-dumped binary, or try ForceDump=true"
                                });
                            }

                            if (size > 0 && offset + size <= (ulong)fs.Length)
                            {
                                var entropy = CalculateEntropy(fs, offset, (long)size);
                                if (entropy >= HighEntropyThreshold)
                                {
                                    findings.Add(new ProtectionFinding
                                    {
                                        Finding = "HighEntropySection",
                                        Target = name,
                                        Entropy = entropy,
                                        Remediation = "Consider a memory-dumped input; if pointers are redirected set NoRedirectedPointer=true"
                                    });
                                }
                            }
                        }
                    }
                }
                else if (cmd == 0x21 || cmd == 0x2C) // LC_ENCRYPTION_INFO / LC_ENCRYPTION_INFO_64
                {
                    reader.ReadUInt32(); // cryptoff
                    reader.ReadUInt32(); // cryptsize
                    var cryptid = reader.ReadUInt32();
                    if (cryptid != 0)
                    {
                        findings.Add(new ProtectionFinding
                        {
                            Finding = "RedirectionHeuristic",
                            Target = "Mach-O",
                            Detail = $"LC_ENCRYPTION_INFO indicates encrypted binary (cryptid={cryptid}).",
                            Remediation = "Consider a memory-dumped input; if pointers are redirected set NoRedirectedPointer=true"
                        });
                    }
                }

                pos += cmdSize;
            }
        }

        private static void AnalyzeWasm(FileStream fs, BinaryReader reader, SectionNameDisambiguator disambiguator, List<ProtectionFinding> findings, List<string> notes)
        {
            fs.Position = 8; // skip 4-byte magic and 4-byte version
            while (fs.Position < fs.Length)
            {
                var sectionId = reader.ReadByte();
                var sectionSize = ReadULeb128(reader);
                if (fs.Position + sectionSize > fs.Length) break;

                var rawName = sectionId switch
                {
                    0 => "custom",
                    1 => "type",
                    2 => "import",
                    3 => "function",
                    4 => "table",
                    5 => "memory",
                    6 => "global",
                    7 => "export",
                    8 => "start",
                    9 => "element",
                    10 => "code",
                    11 => "data",
                    12 => "data_count",
                    _ => $"section_{sectionId}"
                };

                var name = disambiguator.Disambiguate(rawName);
                var sectionStart = fs.Position;
                var nextSectionPos = sectionStart + sectionSize;
                if (sectionSize > 0)
                {
                    var entropy = CalculateEntropy(fs, sectionStart, sectionSize);
                    if (entropy >= HighEntropyThreshold)
                    {
                        findings.Add(new ProtectionFinding
                        {
                            Finding = "HighEntropySection",
                            Target = name,
                            Entropy = entropy,
                            Remediation = "Consider a memory-dumped input; if pointers are redirected set NoRedirectedPointer=true"
                        });
                    }
                }
                fs.Position = nextSectionPos;
            }
        }

        private static void AnalyzeGenericBinary(FileStream fs, SectionNameDisambiguator disambiguator, List<ProtectionFinding> findings, List<string> notes)
        {
            if (fs.Length <= 0) return;

            var entropy = CalculateEntropy(fs, 0, fs.Length);
            if (entropy >= HighEntropyThreshold)
            {
                findings.Add(new ProtectionFinding
                {
                    Finding = "HighEntropySection",
                    Target = disambiguator.Disambiguate(".data"),
                    Entropy = entropy,
                    Remediation = "Consider a memory-dumped input; if pointers are redirected set NoRedirectedPointer=true"
                });
            }
        }

        private static void AnalyzeMetadata(string metadataPath, List<ProtectionFinding> findings, List<string> notes)
        {
            if (!File.Exists(metadataPath))
            {
                findings.Add(new ProtectionFinding
                {
                    Finding = "MetadataSanity",
                    Ok = false,
                    Detail = "Metadata file not found.",
                    Remediation = "Provide a valid global-metadata.dat path or verify file existence."
                });
                return;
            }

            var fi = new FileInfo(metadataPath);
            if (fi.Length < 8)
            {
                findings.Add(new ProtectionFinding
                {
                    Finding = "MetadataSanity",
                    Ok = false,
                    Detail = "Metadata file is truncated or too small for header.",
                    Remediation = "Restore or re-dump global-metadata.dat; file is truncated."
                });
                return;
            }

            try
            {
                using var fs = new FileStream(metadataPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var reader = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);
                var magic = reader.ReadUInt32();
                var version = reader.ReadInt32();

                if (magic != 0xFAB11BAF)
                {
                    findings.Add(new ProtectionFinding
                    {
                        Finding = "MetadataSanity",
                        Ok = false,
                        Detail = $"Invalid metadata header magic: 0x{magic:X8} (expected 0xFAB11BAF).",
                        Remediation = "Provide a decrypted or valid global-metadata.dat; magic header does not match."
                    });
                }
                else if (version < 16 || version > 200)
                {
                    findings.Add(new ProtectionFinding
                    {
                        Finding = "MetadataSanity",
                        Ok = false,
                        Detail = $"Unsupported metadata version: {version} (expected 16-200).",
                        Remediation = "Provide a compatible global-metadata.dat with supported format version (16-200)."
                    });
                }
                else
                {
                    findings.Add(new ProtectionFinding
                    {
                        Finding = "MetadataSanity",
                        Ok = true
                    });
                }
            }
            catch (Exception ex)
            {
                findings.Add(new ProtectionFinding
                {
                    Finding = "MetadataSanity",
                    Ok = false,
                    Detail = $"Error reading metadata header: {ex.Message}",
                    Remediation = "Verify file permissions and integrity for global-metadata.dat."
                });
            }
        }

        private static uint ReadULeb128(BinaryReader reader)
        {
            uint result = 0;
            int shift = 0;
            while (true)
            {
                byte b = reader.ReadByte();
                result |= (uint)(b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                    break;
                shift += 7;
            }
            return result;
        }

        private class SectionNameDisambiguator
        {
            private readonly Dictionary<string, int> nameCounts = new(StringComparer.OrdinalIgnoreCase);

            public string Disambiguate(string rawName)
            {
                var clean = string.IsNullOrWhiteSpace(rawName) ? "unnamed" : rawName;
                if (!nameCounts.TryGetValue(clean, out var count))
                {
                    nameCounts[clean] = 1;
                    return clean;
                }
                else
                {
                    count++;
                    nameCounts[clean] = count;
                    return $"{clean}({count})";
                }
            }
        }
    }

    public static class ProtectionReportWriter
    {
        public const string FileName = "protection-report.json";

        public static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public static string? Write(string outputDir, string executablePath, string metadataPath, Config config)
        {
            if (config != null && !config.GenerateProtectionReport)
            {
                return null;
            }

            Directory.CreateDirectory(outputDir);
            var report = ProtectionDiagnostics.Analyze(executablePath, metadataPath, config ?? new Config());
            var reportPath = Path.Combine(outputDir, FileName);
            var json = JsonSerializer.Serialize(report, JsonOptions);
            File.WriteAllText(reportPath, json, new UTF8Encoding(false));
            return reportPath;
        }
    }
}
