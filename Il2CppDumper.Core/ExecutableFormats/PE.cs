using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using static Il2CppDumper.SectionCharacteristics;

namespace Il2CppDumper
{
    public sealed class PE : Il2Cpp
    {
        private readonly SectionHeader[] sections;
        private readonly ulong headersEnd;
        private Predicate<SectionHeader> hasCapturedBytes;

        public PE(Stream stream) : base(stream)
        {
            var dosHeader = ReadClass<DosHeader>();
            if (dosHeader.Magic != 0x5A4D)
            {
                throw new InvalidDataException("ERROR: Invalid PE file");
            }
            Position = dosHeader.Lfanew;
            if (ReadUInt32() != 0x4550u) //Signature
            {
                throw new InvalidDataException("ERROR: Invalid PE file");
            }
            var fileHeader = ReadClass<FileHeader>();
            var pos = Position;
            var magic = ReadUInt16();
            Position -= 2;
            uint sizeOfHeaders;
            if (magic == 0x10b)
            {
                Is32Bit = true;
                var optionalHeader = ReadClass<OptionalHeader>();
                ImageBase = optionalHeader.ImageBase;
                sizeOfHeaders = optionalHeader.SizeOfHeaders;
            }
            else if (magic == 0x20b)
            {
                var optionalHeader = ReadClass<OptionalHeader64>();
                ImageBase = optionalHeader.ImageBase;
                sizeOfHeaders = optionalHeader.SizeOfHeaders;
            }
            else
            {
                throw new NotSupportedException($"Invalid Optional header magic {magic}");
            }
            Position = pos + fileHeader.SizeOfOptionalHeader;
            sections = ReadClassArray<SectionHeader>(fileHeader.NumberOfSections);
            headersEnd = Math.Max(Position, sizeOfHeaders);
        }

        public void LoadFromMemory(ulong addr)
        {
            LoadFromMemory(addr, null);
        }

        internal void LoadFromMemory(ulong addr, Predicate<SectionHeader> hasCapturedBytes)
        {
            ImageBase = addr;
            this.hasCapturedBytes = hasCapturedBytes;
            foreach (var section in sections)
            {
                section.PointerToRawData = section.VirtualAddress;
                section.SizeOfRawData = section.VirtualSize;
            }
        }

        public override ulong MapVATR(ulong absAddr)
        {
            var addr = absAddr - ImageBase;
            var section = sections.FirstOrDefault(x =>
            {
                var maxLen = Math.Min(x.VirtualSize, x.SizeOfRawData);
                return addr >= x.VirtualAddress && addr < x.VirtualAddress + maxLen;
            });
            if (section == null)
            {
                return 0ul;
            }
            if ((ulong)section.PointerToRawData >= (ulong)Length)
            {
                return 0ul;
            }
            var delta = addr - section.VirtualAddress;
            if (delta >= (ulong)Length - (ulong)section.PointerToRawData)
            {
                return 0ul;
            }
            return (ulong)section.PointerToRawData + delta;
        }

        public override bool TryMapContiguousRange(ulong absAddr, ulong byteSize, out ulong offset)
        {
            offset = 0;
            if (absAddr < ImageBase)
            {
                return false;
            }
            var addr = absAddr - ImageBase;
            var section = sections.FirstOrDefault(x =>
            {
                var maxLen = Math.Min(x.VirtualSize, x.SizeOfRawData);
                return addr >= x.VirtualAddress && addr < x.VirtualAddress + maxLen;
            });
            if (section == null)
            {
                return false;
            }
            var backedLen = Math.Min(section.VirtualSize, section.SizeOfRawData);
            var sectionEndRva = section.VirtualAddress + backedLen;
            if (addr >= sectionEndRva)
            {
                return false;
            }
            var remainingSectionBytes = sectionEndRva - addr;
            if (byteSize > remainingSectionBytes)
            {
                return false;
            }
            if ((ulong)section.PointerToRawData >= (ulong)Length)
            {
                return false;
            }
            var delta = addr - section.VirtualAddress;
            if (delta >= (ulong)Length - (ulong)section.PointerToRawData)
            {
                return false;
            }
            var rawOffset = (ulong)section.PointerToRawData + delta;
            var remainingStreamBytes = (ulong)Length - rawOffset;
            if (byteSize > remainingStreamBytes)
            {
                return false;
            }
            offset = rawOffset;
            return true;
        }

        internal bool IsZeroFilledRange(ulong absAddr, ulong byteSize)
        {
            if (absAddr < ImageBase || byteSize == 0)
            {
                return false;
            }
            var rva = absAddr - ImageBase;
            foreach (var section in sections)
            {
                if (rva < section.VirtualAddress)
                {
                    continue;
                }
                var delta = rva - section.VirtualAddress;
                if (delta < section.VirtualSize)
                {
                    // PE virtual tails contain loader-supplied zeros and have no file offset.
                    return delta >= section.SizeOfRawData && byteSize <= section.VirtualSize - delta &&
                        (section.SizeOfRawData == 0 ||
                         (ulong)section.PointerToRawData + section.SizeOfRawData <= Length);
                }
            }
            return false;
        }

        public override ulong MapRTVA(ulong addr)
        {
            var section = sections.FirstOrDefault(x => addr >= x.PointerToRawData && addr < x.PointerToRawData + x.SizeOfRawData);
            if (section == null)
            {
                return 0ul;
            }
            return addr - section.PointerToRawData + section.VirtualAddress + ImageBase;
        }

        public override bool Search()
        {
            return false;
        }

        public override bool PlusSearch(int methodCount, int typeDefinitionsCount, int imageCount)
        {
            var sectionHelper = GetSectionHelper(methodCount, typeDefinitionsCount, imageCount);
            var codeRegistration = sectionHelper.FindCodeRegistration();
            var metadataRegistration = sectionHelper.FindMetadataRegistration();
            return AutoPlusInit(codeRegistration, metadataRegistration);
        }

        public override bool SymbolSearch()
        {
            return false;
        }

        public override bool IsValidMethodPointer(ulong pointer)
        {
            if (pointer == 0 || sections == null || sections.Length == 0 || pointer < ImageBase)
            {
                return false;
            }
            var addr = pointer - ImageBase;
            var section = sections.FirstOrDefault(x =>
            {
                var maxLen = Math.Min(x.VirtualSize, x.SizeOfRawData);
                return addr >= x.VirtualAddress && addr < x.VirtualAddress + maxLen;
            });
            if (section == null)
            {
                return false;
            }
            var hasExecSections = sections.Any(x => (x.Characteristics & 0x20000000) != 0);
            if (hasExecSections && (section.Characteristics & 0x20000000) == 0)
            {
                return false;
            }
            if ((ulong)section.PointerToRawData >= (ulong)Length)
            {
                return false;
            }
            var delta = addr - section.VirtualAddress;
            if (delta >= (ulong)Length - (ulong)section.PointerToRawData)
            {
                return false;
            }
            return true;
        }

        internal bool IsExecutableRva(ulong rva)
        {
            if (rva == 0 || rva > ulong.MaxValue - ImageBase)
            {
                return false;
            }
            foreach (var section in sections)
            {
                if (rva < section.VirtualAddress)
                {
                    continue;
                }
                var delta = rva - section.VirtualAddress;
                if (delta < Math.Min(section.VirtualSize, section.SizeOfRawData))
                {
                    return (section.Characteristics & 0x20000000) != 0 &&
                        (ulong)section.PointerToRawData + delta < Length;
                }
            }
            return false;
        }

        public override ulong GetRVA(ulong pointer)
        {
            return pointer - ImageBase;
        }

        public override SectionHelper GetSectionHelper(int methodCount, int typeDefinitionsCount, int imageCount)
        {
            var exec = new List<SearchSection>();
            var data = new List<SearchSection>();
            var bss = new List<SearchSection>();
            foreach (var section in sections)
            {
                var flags = (SectionCharacteristics)section.Characteristics;
                var backedSize = Math.Min(section.VirtualSize, section.SizeOfRawData);
                var rawOffset = (ulong)section.PointerToRawData;
                var virtualEnd = (ulong)section.VirtualAddress + section.VirtualSize;
                if ((hasCapturedBytes != null && !hasCapturedBytes(section)) ||
                    (flags & IMAGE_SCN_MEM_DISCARDABLE) != 0 ||
                    backedSize == 0 || rawOffset < headersEnd || rawOffset + backedSize > Length ||
                    virtualEnd > uint.MaxValue || virtualEnd > ulong.MaxValue - ImageBase)
                {
                    continue;
                }

                var address = ImageBase + section.VirtualAddress;
                var candidate = new SearchSection
                {
                    offset = rawOffset,
                    offsetEnd = rawOffset + backedSize,
                    address = address,
                    addressEnd = address + backedSize
                };

                var executable = (flags & IMAGE_SCN_MEM_EXECUTE) != 0;
                var writable = (flags & IMAGE_SCN_MEM_WRITE) != 0;
                if (executable)
                {
                    exec.Add(candidate);
                }
                // Writable executable sections can also contain registration data.
                if (writable || (!executable && (flags & IMAGE_SCN_MEM_READ) != 0))
                {
                    data.Add(candidate);
                    // Legacy metadata-usage pointers can refer to a section's zero-filled virtual tail.
                    bss.Add(new SearchSection
                    {
                        offset = candidate.offset,
                        offsetEnd = candidate.offsetEnd,
                        address = candidate.address,
                        addressEnd = ImageBase + virtualEnd
                    });
                }
            }
            var sectionHelper = new SectionHelper(this, methodCount, typeDefinitionsCount, metadataUsagesCount, imageCount);
            sectionHelper.SetSection(SearchSectionType.Exec, exec.ToArray());
            sectionHelper.SetSection(SearchSectionType.Data, data.ToArray());
            sectionHelper.SetSection(SearchSectionType.Bss, bss.ToArray());
            return sectionHelper;
        }

        public override bool CheckDump()
        {
            if (Is32Bit)
            {
                return ImageBase != 0x10000000;
            }
            else
            {
                return ImageBase != 0x180000000;
            }
        }
    }
}
