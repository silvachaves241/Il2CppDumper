using Il2CppDumper.Tests.Synthetic;
using Xunit;

namespace Il2CppDumper.Tests
{
    public class MetadataStreamTests
    {
        [Fact]
        public void ReadsAndClosesReadOnlyMetadataFile()
        {
            var path = Path.GetTempFileName();
            try
            {
                File.WriteAllBytes(path, SyntheticMetadataGenerator.Generate(MetadataVersionFamily.V39));
                using var input = File.OpenRead(path);
                using (var metadata = new Metadata(input))
                {
                    Assert.False(input.CanWrite);
                    Assert.Equal(39d, metadata.Version);
                    Assert.Equal(2, metadata.typeDefs.Length);
                    Assert.Equal("SyntheticImage.dll", metadata.GetStringFromIndex(Assert.Single(metadata.imageDefs).nameIndex));
                    Assert.Equal("SyntheticClass", metadata.GetStringFromIndex(metadata.typeDefs[0].nameIndex));

                    var position = metadata.Position;
                    Assert.Throws<NotSupportedException>(() => metadata.Writer);
                    Assert.Throws<NotSupportedException>(() => metadata.Write(42));
                    Assert.Equal(position, metadata.Position);
                }

                Assert.False(input.CanRead);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void WritableMetadataSupportsBothWriteEntrypointsAndClosesInput()
        {
            using var input = new MemoryStream(SyntheticMetadataGenerator.Generate(MetadataVersionFamily.V39));
            using var metadata = new Metadata(input);
            Assert.Equal(2, metadata.typeDefs.Length);

            var offset = metadata.Length - 8;
            metadata.Position = offset;
            metadata.Write(17);
            metadata.Writer.Write(42);
            metadata.Position = offset;
            Assert.Equal(17, metadata.ReadInt32());
            Assert.Equal(42, metadata.ReadInt32());

            metadata.Dispose();
            metadata.Dispose();
            Assert.False(input.CanRead);
        }

        [Fact]
        public void MutationCapableBinaryStreamStillRequiresWritableInput()
        {
            using var input = new MemoryStream(new byte[16], writable: false);

            Assert.Throws<ArgumentException>(() => new BinaryStream(input));
            Assert.True(input.CanRead);
        }

        [Fact]
        public void MetadataRejectsNonSeekableInputBeforeReadingIt()
        {
            using var input = new NonSeekableMemoryStream();

            var error = Assert.Throws<ArgumentException>(() => new Metadata(input));

            Assert.Equal("stream", error.ParamName);
            Assert.Contains("seekable", error.Message);
            Assert.True(input.CanRead);
        }

        private sealed class NonSeekableMemoryStream : MemoryStream
        {
            public override bool CanSeek => false;
        }
    }
}
