using System;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace Il2CppDumper.Tests
{
    public class InvalidUtf8StringLiteralTests
    {
        private static Metadata CreateV31Metadata(byte[] literalDataBytes, params (uint length, int dataIndex)[] literals)
        {
            var streamBytes = new byte[8192];
            BitConverter.GetBytes(0xFAB11BAFu).CopyTo(streamBytes, 0); // magic
            BitConverter.GetBytes(31).CopyTo(streamBytes, 4);           // version 31

            var literalTableOffset = 256u;
            var literalTableSize = (uint)(literals.Length * 8);
            var dataOffset = 1024u;
            var dataSize = (uint)literalDataBytes.Length;

            BitConverter.GetBytes(literalTableOffset).CopyTo(streamBytes, 8);
            BitConverter.GetBytes(literalTableSize).CopyTo(streamBytes, 12);
            BitConverter.GetBytes(dataOffset).CopyTo(streamBytes, 16);
            BitConverter.GetBytes(dataSize).CopyTo(streamBytes, 20);

            for (int i = 0; i < literals.Length; i++)
            {
                var entryPos = (int)literalTableOffset + (i * 8);
                BitConverter.GetBytes(literals[i].length).CopyTo(streamBytes, entryPos);
                BitConverter.GetBytes(literals[i].dataIndex).CopyTo(streamBytes, entryPos + 4);
            }

            if (literalDataBytes.Length > 0)
            {
                Buffer.BlockCopy(literalDataBytes, 0, streamBytes, (int)dataOffset, literalDataBytes.Length);
            }

            return new Metadata(new MemoryStream(streamBytes))
            {
                Version = 31
            };
        }

        private sealed class TestDecryptor : IStringLiteralDecryptor
        {
            private readonly Func<uint, byte[], string> decryptFunc;
            public string Id { get; }

            public TestDecryptor(string id, Func<uint, byte[], string> decryptFunc)
            {
                Id = id;
                this.decryptFunc = decryptFunc;
            }

            public string Decrypt(uint index, byte[] cipherBytes) => decryptFunc(index, cipherBytes);
        }

        [Fact]
        public void DistinctInvalidByteSequences_PreserveRawBytesAndEmitDiagnostics()
        {
            // Reproducer for Issue #80:
            // Two 1-byte literals: 0xFF and 0xFE.
            var dataBytes = new byte[] { 0xFF, 0xFE };
            using var metadata = CreateV31Metadata(dataBytes, (1u, 0), (1u, 1));

            var literals = metadata.DumpStringLiterals(out int failures, out var warnings);

            // Must detect invalid UTF-8 and emit diagnostics
            Assert.Equal(2, failures);
            Assert.Equal(2, warnings.Count);

            // Structured warnings must identify the affected literals
            Assert.Equal("stringLiteral", warnings[0].Category);
            Assert.Equal("stringLiteral[0]", warnings[0].Member);
            Assert.False(string.IsNullOrEmpty(warnings[0].RawBytes));

            Assert.Equal("stringLiteral", warnings[1].Category);
            Assert.Equal("stringLiteral[1]", warnings[1].Member);
            Assert.False(string.IsNullOrEmpty(warnings[1].RawBytes));

            // Preserves index, dataIndex, and length
            Assert.Equal(0u, literals[0].index);
            Assert.Equal(0, literals[0].dataIndex);
            Assert.Equal(1, literals[0].length);

            Assert.Equal(1u, literals[1].index);
            Assert.Equal(1, literals[1].dataIndex);
            Assert.Equal(1, literals[1].length);

            // Raw bytes must be preserved in additive representation and distinguishable
            Assert.NotNull(literals[0].raw);
            Assert.NotNull(literals[1].raw);
            Assert.NotEqual(literals[0].raw, literals[1].raw);

            var decodedRaw0 = Convert.FromBase64String(literals[0].raw!);
            var decodedRaw1 = Convert.FromBase64String(literals[1].raw!);
            Assert.Equal(new byte[] { 0xFF }, decodedRaw0);
            Assert.Equal(new byte[] { 0xFE }, decodedRaw1);

            // Fallback policy: replacement string "\uFFFD"
            Assert.Equal("\uFFFD", literals[0].value);
            Assert.Equal("\uFFFD", literals[1].value);
        }

        [Fact]
        public void GenuineUtf8_EncodingOfReplacementChar_NotConflatedWithInvalidBytes()
        {
            // 0xEF, 0xBF, 0xBD is the genuine, valid 3-byte UTF-8 encoding of U+FFFD
            var genuineUtf8ReplacementBytes = new byte[] { 0xEF, 0xBF, 0xBD };
            using var metadata = CreateV31Metadata(genuineUtf8ReplacementBytes, (3u, 0));

            var literals = metadata.DumpStringLiterals(out int failures, out var warnings);

            // Valid UTF-8 must NOT produce warnings or decoder failures
            Assert.Equal(0, failures);
            Assert.Empty(warnings);

            Assert.Single(literals);
            Assert.Equal("\uFFFD", literals[0].value);
            // Inactive-decryptor valid output retains existing behavior: raw is null
            Assert.Null(literals[0].raw);
            Assert.Null(literals[0].decryptor);
        }

        [Fact]
        public void ValidUtf8AndEmbeddedNul_RetainsExistingBehavior()
        {
            var validBytes = Encoding.UTF8.GetBytes("Hello\0World");
            using var metadata = CreateV31Metadata(validBytes, ((uint)validBytes.Length, 0));

            var literals = metadata.DumpStringLiterals(out int failures, out var warnings);

            Assert.Equal(0, failures);
            Assert.Empty(warnings);
            Assert.Single(literals);
            Assert.Equal("Hello\0World", literals[0].value);
            Assert.Null(literals[0].raw);
        }

        [Fact]
        public void ConfiguredDecryptor_ReceivesOriginalBytes_SuccessNotLabeledAsDecoderFailure()
        {
            // Ciphertext bytes: 0xFF, 0xFE (not valid UTF-8 plaintext)
            var cipherBytes = new byte[] { 0xFF, 0xFE };
            using var metadata = CreateV31Metadata(cipherBytes, (2u, 0));

            // Decryptor receives the raw bytes and decrypts them
            metadata.StringDecryptor = new TestDecryptor("custom-xor", (idx, raw) =>
            {
                Assert.Equal(cipherBytes, raw);
                return "DecryptedPlayerName";
            });

            var literals = metadata.DumpStringLiterals(out int failures, out var warnings);

            // Decryptor succeeded: must NOT be mislabeled as decoder failure
            Assert.Equal(0, failures);
            Assert.Empty(warnings);

            Assert.Single(literals);
            Assert.Equal("DecryptedPlayerName", literals[0].value);
            Assert.Equal(Convert.ToBase64String(cipherBytes), literals[0].raw);
            Assert.Equal("custom-xor", literals[0].decryptor);
        }

        [Fact]
        public void ConfiguredDecryptor_Failure_SeparatelyRecordedFromDecoderFailure()
        {
            // Ciphertext bytes are invalid UTF-8
            var cipherBytes = new byte[] { 0xFF, 0xFE };
            using var metadata = CreateV31Metadata(cipherBytes, (2u, 0));

            metadata.StringDecryptor = new TestDecryptor("faulty-decryptor", (idx, raw) =>
            {
                throw new InvalidOperationException("Key derivation failed");
            });

            var literals = metadata.DumpStringLiterals(out int failures, out var warnings);

            // Decoder failure reported in warnings/failures
            Assert.Equal(1, failures);
            Assert.Single(warnings);
            Assert.Equal("stringLiteral", warnings[0].Category);

            // Decryptor failure reported separately in StringDecryptorFailures
            Assert.Equal(1, metadata.StringDecryptorFailedCount);
            Assert.Single(metadata.StringDecryptorFailures);
            Assert.Equal("stringDecryptor", metadata.StringDecryptorFailures[0].Category);
            Assert.Contains("faulty-decryptor", metadata.StringDecryptorFailures[0].Detail);
        }

        [Fact]
        public void TryGetStringLiteral_ReportsInvalidUtf8Flag()
        {
            var invalidBytes = new byte[] { 0xFF };
            using var metadata = CreateV31Metadata(invalidBytes, (1u, 0));

            var success = metadata.TryGetStringLiteral(
                0,
                out var val,
                out var dIdx,
                out var len,
                out var raw,
                out var decryptor,
                out var isInvalidUtf8);

            Assert.True(success);
            Assert.True(isInvalidUtf8);
            Assert.Equal("\uFFFD", val);
            Assert.Equal(0, dIdx);
            Assert.Equal(1, len);
            Assert.Equal(Convert.ToBase64String(invalidBytes), raw);
            Assert.Null(decryptor);
        }
    }
}
