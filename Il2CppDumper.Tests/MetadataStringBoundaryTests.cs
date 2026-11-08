using System;
using System.IO;
using System.Text;
using Xunit;

namespace Il2CppDumper.Tests
{
    public class MetadataStringBoundaryTests
    {
        private static byte[] CreateBaseV31Buffer(uint stringOffset, int stringSize, byte[] stringBytes, int totalBufferSize = 8192)
        {
            var bytes = new byte[totalBufferSize];
            BitConverter.GetBytes(0xFAB11BAFu).CopyTo(bytes, 0); // sanity
            BitConverter.GetBytes(31).CopyTo(bytes, 4);           // version 31
            BitConverter.GetBytes(stringOffset).CopyTo(bytes, 24); // header.stringOffset
            BitConverter.GetBytes(stringSize).CopyTo(bytes, 28);   // header.stringSize

            if (stringBytes != null && stringBytes.Length > 0)
            {
                Buffer.BlockCopy(stringBytes, 0, bytes, (int)stringOffset, stringBytes.Length);
            }

            return bytes;
        }

        private static byte[] CreateBaseV38Buffer(int stringOffset, int stringSize, byte[] stringBytes, int totalBufferSize = 8192)
        {
            var bytes = new byte[totalBufferSize];
            BitConverter.GetBytes(0xFAB11BAFu).CopyTo(bytes, 0); // sanity
            BitConverter.GetBytes(38).CopyTo(bytes, 4);           // version 38

            // strings is the 3rd Il2CppSectionMetadata:
            // sanity (4) + version (4) = 8
            // stringLiterals @ 8..19 (12 bytes)
            // stringLiteralData @ 20..31 (12 bytes)
            // strings @ 32..43 (12 bytes: offset @ 32, sectionSize @ 36, count @ 40)
            BitConverter.GetBytes(stringOffset).CopyTo(bytes, 32);
            BitConverter.GetBytes(stringSize).CopyTo(bytes, 36);
            BitConverter.GetBytes(0).CopyTo(bytes, 40);

            if (stringBytes != null && stringBytes.Length > 0 && stringOffset >= 0 && (stringOffset + stringBytes.Length) <= totalBufferSize)
            {
                Buffer.BlockCopy(stringBytes, 0, bytes, stringOffset, stringBytes.Length);
            }

            return bytes;
        }

        [Fact]
        public void OneByteSection_NoTerminator_FailsDecodeAndDoesNotReadNeighboringBytes()
        {
            // Reproducer for Issue #79:
            // Declared strings section is 1 byte ('A'), immediately followed outside the section by 'B' and NUL.
            var rawBytes = new byte[] { (byte)'A', (byte)'B', 0 };
            var buffer = CreateBaseV31Buffer(stringOffset: 512, stringSize: 1, rawBytes);

            using var metadata = new Metadata(new MemoryStream(buffer));

            var accepted = metadata.TryGetStringFromIndex(0, out var name);

            Assert.False(accepted);
            Assert.Equal(string.Empty, name);
            Assert.Equal(string.Empty, metadata.GetStringFromIndex(0));

            // Verify failed decodes are not cached as successful results
            var secondAttempt = metadata.TryGetStringFromIndex(0, out var cachedName);
            Assert.False(secondAttempt);
            Assert.Equal(string.Empty, cachedName);
        }

        [Fact]
        public void PositiveControl_TerminatorInsideSection_DecodesCorrectly()
        {
            var rawBytes = new byte[] { (byte)'A', 0 };
            var buffer = CreateBaseV31Buffer(stringOffset: 512, stringSize: 2, rawBytes);

            using var metadata = new Metadata(new MemoryStream(buffer));

            var accepted = metadata.TryGetStringFromIndex(0, out var name);

            Assert.True(accepted);
            Assert.Equal("A", name);
            Assert.Equal("A", metadata.GetStringFromIndex(0));
        }

        [Fact]
        public void ValidEmptyString_AtNullByte_DecodesSuccessfully()
        {
            var rawBytes = new byte[] { 0 };
            var buffer = CreateBaseV31Buffer(stringOffset: 512, stringSize: 1, rawBytes);

            using var metadata = new Metadata(new MemoryStream(buffer));

            var accepted = metadata.TryGetStringFromIndex(0, out var name);

            Assert.True(accepted);
            Assert.Equal(string.Empty, name);
            Assert.Equal(string.Empty, metadata.GetStringFromIndex(0));
        }

        [Fact]
        public void TerminatorAtLastAllowedByte_Succeeds()
        {
            var rawBytes = Encoding.UTF8.GetBytes("Test\0"); // 5 bytes, NUL at index 4 (last byte of size 5)
            var buffer = CreateBaseV31Buffer(stringOffset: 512, stringSize: 5, rawBytes);

            using var metadata = new Metadata(new MemoryStream(buffer));

            var accepted = metadata.TryGetStringFromIndex(0, out var name);

            Assert.True(accepted);
            Assert.Equal("Test", name);
        }

        [Fact]
        public void InvalidStartIndices_ReturnFalseWithoutThrowing()
        {
            var rawBytes = Encoding.UTF8.GetBytes("Test\0");
            var buffer = CreateBaseV31Buffer(stringOffset: 512, stringSize: 5, rawBytes);

            using var metadata = new Metadata(new MemoryStream(buffer));

            // index == stringSize (at end boundary)
            Assert.False(metadata.TryGetStringFromIndex(5, out var name1));
            Assert.Equal(string.Empty, name1);

            // index > stringSize (past section boundary)
            Assert.False(metadata.TryGetStringFromIndex(6, out var name2));
            Assert.Equal(string.Empty, name2);

            // uint.MaxValue
            Assert.False(metadata.TryGetStringFromIndex(uint.MaxValue, out var name3));
            Assert.Equal(string.Empty, name3);
        }

        [Fact]
        public void SectionTruncatedByEof_ReturnsFalseGracefully()
        {
            // Total file length is 520 bytes.
            // stringOffset = 512, stringSize claims to be 100 bytes (up to 612).
            var totalBufferSize = 520;
            var buffer = new byte[totalBufferSize];
            BitConverter.GetBytes(0xFAB11BAFu).CopyTo(buffer, 0);
            BitConverter.GetBytes(31).CopyTo(buffer, 4);
            BitConverter.GetBytes(512u).CopyTo(buffer, 24);
            BitConverter.GetBytes(100).CopyTo(buffer, 28);

            // String at index 0: "OK\0" (offsets 512..514, inside 520)
            buffer[512] = (byte)'O';
            buffer[513] = (byte)'K';
            buffer[514] = 0;

            // String at index 5: "NOP" (offsets 517..519, stream cuts off at 520 without NUL)
            buffer[517] = (byte)'N';
            buffer[518] = (byte)'O';
            buffer[519] = (byte)'P';

            using var metadata = new Metadata(new MemoryStream(buffer));

            // String at index 0 succeeds because terminator is within both section and stream length
            Assert.True(metadata.TryGetStringFromIndex(0, out var okName));
            Assert.Equal("OK", okName);

            // String at index 5 fails because stream terminates at EOF before encountering NUL
            Assert.False(metadata.TryGetStringFromIndex(5, out var unterminatedName));
            Assert.Equal(string.Empty, unterminatedName);

            // String starting past EOF (512 + 10 = 522 >= 520) fails
            Assert.False(metadata.TryGetStringFromIndex(10, out var outOfBoundsName));
            Assert.Equal(string.Empty, outOfBoundsName);
        }

        [Fact]
        public void V38PlusSectionHeader_EnforcesSameStringBoundaries()
        {
            // Unterminated 1-byte section in v38
            var rawBytes = new byte[] { (byte)'A', (byte)'B', 0 };
            var buffer = CreateBaseV38Buffer(stringOffset: 512, stringSize: 1, rawBytes);

            using var metadata = new Metadata(new MemoryStream(buffer));

            var accepted = metadata.TryGetStringFromIndex(0, out var name);
            Assert.False(accepted);
            Assert.Equal(string.Empty, name);

            // Positive control in v38
            var validBytes = new byte[] { (byte)'A', 0 };
            var validBuffer = CreateBaseV38Buffer(stringOffset: 512, stringSize: 2, validBytes);

            using var validMetadata = new Metadata(new MemoryStream(validBuffer));
            Assert.True(validMetadata.TryGetStringFromIndex(0, out var validName));
            Assert.Equal("A", validName);
        }

        [Fact]
        public void V38PlusSectionHeader_InvalidNegativeOffsetOrZeroSize_ReturnsFalse()
        {
            var bufferNeg = CreateBaseV38Buffer(stringOffset: -1, stringSize: 10, Array.Empty<byte>());
            using var metadataNeg = new Metadata(new MemoryStream(bufferNeg));
            Assert.False(metadataNeg.TryGetStringFromIndex(0, out var nameNeg));
            Assert.Equal(string.Empty, nameNeg);

            var bufferZero = CreateBaseV38Buffer(stringOffset: 512, stringSize: 0, Array.Empty<byte>());
            using var metadataZero = new Metadata(new MemoryStream(bufferZero));
            Assert.False(metadataZero.TryGetStringFromIndex(0, out var nameZero));
            Assert.Equal(string.Empty, nameZero);
        }

        [Fact]
        public void BinaryStream_TryReadStringToNull_BoundsAndEofHandling()
        {
            var data = Encoding.UTF8.GetBytes("HelloWorld\0");
            using var stream = new BinaryStream(new MemoryStream(data));

            // Exact bounds to include terminator (11 bytes: 10 chars + NUL)
            Assert.True(stream.TryReadStringToNull(0, 11, out var fullStr));
            Assert.Equal("HelloWorld", fullStr);

            // Bounds too short (10 bytes: stops before NUL)
            Assert.False(stream.TryReadStringToNull(0, 10, out var truncatedStr));
            Assert.Equal(string.Empty, truncatedStr);

            // Zero or negative maxBytes
            Assert.False(stream.TryReadStringToNull(0, 0, out _));
            Assert.False(stream.TryReadStringToNull(0, -5, out _));

            // Address >= Length
            Assert.False(stream.TryReadStringToNull((ulong)data.Length, 5, out _));

            // ReadStringToNull with maxBytes throws on boundary overrun
            Assert.Throws<InvalidDataException>(() => stream.ReadStringToNull(0, 10));
            Assert.Equal("HelloWorld", stream.ReadStringToNull(0, 11));
        }
    }
}
