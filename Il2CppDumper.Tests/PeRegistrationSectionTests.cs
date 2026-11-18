using System.Buffers.Binary;
using Xunit;

namespace Il2CppDumper.Tests
{
    public class PeRegistrationSectionTests
    {
        [Theory]
        [InlineData(0x60000020u, true, false)]
        [InlineData(0x40000040u, false, true)]
        [InlineData(0xC0000040u, false, true)]
        [InlineData(0xC0000020u, false, true)]
        [InlineData(0x68000040u, true, false)]
        [InlineData(0x20000020u, true, false)]
        [InlineData(0x80000040u, false, true)]
        [InlineData(0xE0000040u, true, true)]
        [InlineData(0x62000020u, false, false)]
        [InlineData(0x42000040u, false, false)]
        [InlineData(0x00000040u, false, false)]
        public void ClassifiesSectionsByMemoryPermissions(uint flags, bool executable, bool data)
        {
            var bytes = ProtectionReportTests.CreateSyntheticPe(new[] { (".section", new byte[512], 512u, flags) });
            using var pe = new PE(new MemoryStream(bytes));

            var ranges = pe.GetSectionHelper(0, 0, 0);

            Assert.Equal(executable, ranges.Exec.Count > 0);
            Assert.Equal(data, ranges.Data.Count > 0);
        }

        [Theory]
        [InlineData(256, 512)]
        [InlineData(512, 256)]
        public void ScansOnlyMappedBytesAndRetainsVirtualTailForLegacyBssChecks(int virtualSize, int rawSize)
        {
            var bytes = ProtectionReportTests.CreateSyntheticPe(new[]
            {
                (".data", new byte[rawSize], (uint)virtualSize, 0xC0000020u)
            });
            using var pe = new PE(new MemoryStream(bytes));
            var ranges = pe.GetSectionHelper(0, 0, 0);
            var candidate = Assert.Single(ranges.Data);
            var backedSize = (ulong)Math.Min(virtualSize, rawSize);

            Assert.Equal(0x400ul, candidate.offset);
            Assert.Equal(0x400ul + backedSize, candidate.offsetEnd);
            Assert.Equal(pe.ImageBase + 0x1000ul, candidate.address);
            Assert.Equal(candidate.address + backedSize, candidate.addressEnd);
            Assert.Equal(candidate.address + (ulong)virtualSize, Assert.Single(ranges.Bss).addressEnd);
        }

        [Theory]
        [InlineData(0u, 0x1000u, 512u)] // Points into headers
        [InlineData(0x300u, 0x1000u, 512u)] // Points into header padding
        [InlineData(0x700u, 0x1000u, 512u)] // Beyond EOF
        [InlineData(0x500u, 0x1000u, 512u)] // Partially past EOF
        [InlineData(uint.MaxValue, 0x1000u, 512u)]
        [InlineData(0x400u, 0xFFFFFF00u, 512u)] // RVA range wraps
        [InlineData(0x400u, 0x1000u, 0u)]
        public void ExcludesInvalidOrEmptyMappedRanges(uint rawOffset, uint rva, uint virtualSize)
        {
            const int sectionHeader = 0x80 + 4 + 20 + 0xF0;
            var bytes = ProtectionReportTests.CreateSyntheticPe(new[]
            {
                (".mixed", new byte[512], 512u, 0xE0000040u)
            });
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(sectionHeader + 8), virtualSize);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(sectionHeader + 12), rva);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(sectionHeader + 20), rawOffset);
            using var pe = new PE(new MemoryStream(bytes));

            var ranges = pe.GetSectionHelper(0, 0, 0);

            Assert.Empty(ranges.Exec);
            Assert.Empty(ranges.Data);
            Assert.Empty(ranges.Bss);
        }

        [Fact]
        public void NativeSnapshotScansOnlyCapturedSections()
        {
            var bytes = ProtectionReportTests.CreateSyntheticPe(new[]
            {
                (".data", new byte[512], 512u, 0xC0000040u),
                (".extra", new byte[512], 512u, 0xC0000020u)
            });
            Array.Resize(ref bytes, 0x3000);
            using var pe = new PE(new MemoryStream(bytes));
            pe.LoadFromMemory(pe.ImageBase, section => section.VirtualAddress == 0x1000);

            var candidate = Assert.Single(pe.GetSectionHelper(0, 0, 0).Data);

            Assert.Equal(0x1000ul, candidate.offset);
        }
    }
}
