using System;
using System.IO;
using Il2CppDumper;
using Xunit;

namespace Il2CppDumper.Tests
{
    /// <summary>
    /// Constants that cannot be decoded from the default-value blob fall back to the
    /// metadata-offset path instead of crashing downstream generators. This fixture pins
    /// the contract of the underlying reader: malformed compressed integers throw the
    /// dedicated, diagnosable exception type; callers translate it into fallback behavior.
    /// </summary>
    public class InvalidCompressedIntegerTests
    {
        private static BinaryReader ReaderFromBytes(params byte[] bytes)
        {
            var ms = new MemoryStream(bytes);
            return new BinaryReader(ms);
        }

        [Fact]
        public void ReadCompressedUInt32_UnknownPrefix_ThrowsDedicatedExceptionWithByteAndPosition()
        {
            // Layout: valid 1-byte value (0x01), then unknown prefix 0xE9 (0xE8 is tolerated by
            // a legacy workaround, 0xE0-0xE7 and 0xE9-0xEF are not).
            var reader = ReaderFromBytes(0x01, 0xE9);

            var valid = reader.ReadCompressedUInt32();
            Assert.Equal(1u, valid);

            var exception = Assert.Throws<InvalidCompressedIntegerException>(
                () => reader.ReadCompressedUInt32());

            Assert.Contains("0xE9", exception.Message, StringComparison.OrdinalIgnoreCase);
            // Stream position is 1-based-inclusive of the offending byte: prefix + 1 byte consumed.
            Assert.Contains("position 1", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ReadCompressedInt32_UnknownPrefix_PropagatesDedicatedException() 
        {
            var reader = ReaderFromBytes(0xE9);

            var exception = Assert.Throws<InvalidCompressedIntegerException>(
                () => reader.ReadCompressedInt32());

            Assert.Contains("0xE9", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ReadCompressedUInt32_EncodedPrefixes_StillDecode()
        {
            // Regression guard for the fallback change: all previously valid prefixes keep
            // decoding byte-identical values.
            var reader = ReaderFromBytes(
                0x7F,             // 1 byte          -> 0x7F
                0x80, 0x01,       // 2 bytes         -> (0 << 8) | 0x01
                0xC1, 0x00, 0x01, 0x00, // 4 bytes   -> 0x01000100
                0xF0, 0xAB, 0x00, 0x00, 0x00, // 5 bytes -> raw uint32
                0xE8,             // legacy tolerated prefix -> 0
                0xFE,             // Int32.MaxValue special  -> uint.MaxValue - 1
                0xFF              // uint.MaxValue special
            );

            Assert.Equal(0x7Fu, (uint)reader.ReadCompressedUInt32());
            Assert.Equal(1u, reader.ReadCompressedUInt32());
            Assert.Equal(0x01000100u, reader.ReadCompressedUInt32());
            Assert.Equal(0x000000ABu, reader.ReadCompressedUInt32());
            Assert.Equal(0u, reader.ReadCompressedUInt32());
            Assert.Equal(uint.MaxValue - 1, reader.ReadCompressedUInt32());
            Assert.Equal(uint.MaxValue, reader.ReadCompressedUInt32());
        }
    }
}
