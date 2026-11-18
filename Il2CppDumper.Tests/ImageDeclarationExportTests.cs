using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Il2CppDumper.Core;
using Il2CppDumper.Tests.Synthetic;
using Xunit;

namespace Il2CppDumper.Tests
{
    public sealed class ImageDeclarationExportTests : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "il2cpp-image-dump-" + Guid.NewGuid().ToString("N"));

        public ImageDeclarationExportTests() => Directory.CreateDirectory(root);

        public void Dispose() => Directory.Delete(root, true);

        [Fact]
        public void TwoImages_ShareAggregateFormatting_AndRecordEmptyImage()
        {
            var (metadata, binary, executor) = SyntheticEnvironment.Create(MetadataVersionFamily.V39);
            using (metadata)
            using (binary)
            {
                SplitFixtureImages(metadata);
                var config = new Config { DumpAttribute = false, GeneratePerImageDump = false };
                var decompiler = new Il2CppDecompiler(executor);
                Assert.True(decompiler.Decompile(config, root), decompiler.ErrorMessage);
                var baseline = File.ReadAllText(Path.Combine(root, "dump.cs"));
                Assert.Single(decompiler.WrittenFiles);

                config.GeneratePerImageDump = true;
                Assert.True(decompiler.Decompile(config, root), decompiler.ErrorMessage);
                Assert.Equal(baseline, File.ReadAllText(Path.Combine(root, "dump.cs")));

                using var index = ReadIndex();
                var images = index.RootElement.GetProperty("images").EnumerateArray().ToArray();
                Assert.Equal(3, images.Length);
                var files = images.Select(ImageFile).ToArray();
                Assert.Equal(3, files.Distinct(StringComparer.OrdinalIgnoreCase).Count());
                Assert.Equal(5, decompiler.WrittenFiles.Count);
                for (int i = 0; i < 2; i++)
                {
                    Assert.Equal(i, images[i].GetProperty("imageId").GetInt32());
                    Assert.Equal(i, images[i].GetProperty("typeStart").GetInt32());
                    Assert.Equal(1, images[i].GetProperty("declarationCount").GetInt32());
                    var text = File.ReadAllText(files[i]);
                    Assert.Contains($"TypeDefIndex: {i} | ImageId: {i}", text);
                    Assert.DoesNotContain($"TypeDefIndex: {1 - i}", text);
                }
                var declaredTypes = string.Concat(files.Select(file => WithoutHeader(File.ReadAllText(file))));
                var aggregateHeader = string.Concat(images.Select((_, i) =>
                    $"// Image {i}: SyntheticImage.dll - {i}\n"));
                Assert.Equal(aggregateHeader + declaredTypes, baseline);

                Assert.Equal("empty", images[2].GetProperty("status").GetString());
                Assert.Equal(0, images[2].GetProperty("declarationCount").GetInt32());
                Assert.Empty(WithoutHeader(File.ReadAllText(files[2])));

                config.GenerateAggregateDump = false;
                Assert.True(decompiler.Decompile(config, root), decompiler.ErrorMessage);
                Assert.Equal(baseline, File.ReadAllText(Path.Combine(root, "dump.cs")));
                Assert.DoesNotContain(Path.Combine(root, "dump.cs"), decompiler.WrittenFiles);
                using var repeat = ReadIndex();
                Assert.Equal(index.RootElement.GetRawText(), repeat.RootElement.GetRawText());
            }
        }

        [Fact]
        public void FailedImage_DoesNotAdvertiseStaleFile_AndOtherImagesReachManifest()
        {
            var (metadata, binary, executor) = SyntheticEnvironment.Create(MetadataVersionFamily.V39);
            using (metadata)
            using (binary)
            {
                SplitFixtureImages(metadata);
                var config = new Config
                {
                    GenerateAggregateDump = false,
                    GeneratePerImageDump = true,
                    GenerateStruct = false,
                    GenerateDummyDll = false,
                    DumpAttribute = false
                };
                Assert.True(new Il2CppDecompiler(executor).Decompile(config, root));
                using var first = ReadIndex();
                var stale = ImageFile(first.RootElement.GetProperty("images")[0]);
                var staleText = File.ReadAllText(stale);
                metadata.typeDefs[0].parentIndex = int.MaxValue;

                Assert.False(Dumper.ExecuteDump(metadata, binary, config, root, out _, out var counts, out var warnings, out var files));
                using var current = ReadIndex();
                var images = current.RootElement.GetProperty("images");
                Assert.Equal("failed", images[0].GetProperty("status").GetString());
                Assert.Equal(JsonValueKind.Null, images[0].GetProperty("path").ValueKind);
                Assert.Equal("written", images[1].GetProperty("status").GetString());
                Assert.Equal(staleText, File.ReadAllText(stale));
                Assert.DoesNotContain(stale, files);
                Assert.Contains(ImageFile(images[1]), files);
                Assert.Contains(ImageFile(images[2]), files);
                Assert.Contains(Path.Combine(root, ImageDeclarationExport.DirectoryName, ImageDeclarationExport.IndexFileName), files);
                Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(stale)!, "*.tmp"));

                var manifest = RunManifestWriter.Create(config, DateTime.UtcNow, 0, false, counts, warnings, root, files);
                Assert.False(manifest.Success);
                Assert.Equal(3, manifest.Outputs.Count);
                Assert.DoesNotContain(manifest.Outputs, output => output.Path.EndsWith(Path.GetFileName(stale), StringComparison.Ordinal));
            }
        }

        [Fact]
        public void FileNames_StayInsideOutputDirectory_EvenWhenSlugsCollide()
        {
            var export = new ImageDeclarationExport(root);
            var directory = Path.Combine(root, ImageDeclarationExport.DirectoryName);
            foreach (var (id, name) in new[] { (0, "../A:B"), (1, "..\\A?B"), (2, new string('A', 1000)), (3, "CON") })
            {
                var entry = export.AddImage(id, name, 0, 0);
                using var file = export.OpenImage(entry);
                var path = file.Publish();
                Assert.Equal(directory, Path.GetDirectoryName(path));
                Assert.Matches(@"^[0-9]{6}-[A-Za-z0-9_.-]{1,64}\.cs$", Path.GetFileName(path));
            }
            var names = Directory.GetFiles(directory).Select(path => Path.GetFileName(path)!).OrderBy(name => name, StringComparer.Ordinal).ToArray();
            Assert.Equal(4, names.Length);
            Assert.Equal(names[0][6..], names[1][6..]);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void TeeWriter_ContinuesWritingTheHealthyDestination(bool firstFails)
        {
            using var healthy = new StringWriter();
            using var failing = new FailingWriter();
            int firstFailures = 0;
            int secondFailures = 0;
            using var tee = new ImageDeclarationExport.TeeWriter(
                firstFails ? failing : healthy,
                firstFails ? healthy : failing,
                _ => firstFailures++,
                _ => secondFailures++);

            tee.Write("first");
            tee.Write("second");

            Assert.Equal("firstsecond", healthy.ToString());
            Assert.Equal(1, failing.Writes);
            Assert.Equal(firstFails ? 1 : 0, firstFailures);
            Assert.Equal(firstFails ? 0 : 1, secondFailures);
        }

        private sealed class FailingWriter : StringWriter
        {
            public int Writes { get; private set; }

            public override void Write(string? value)
            {
                Writes++;
                throw new IOException("Injected write failure");
            }
        }

        private static void SplitFixtureImages(Metadata metadata)
        {
            uint name = metadata.imageDefs[0].nameIndex;
            metadata.imageDefs = new[]
            {
                new Il2CppImageDefinition { nameIndex = name, typeStart = 0, typeCount = 1 },
                new Il2CppImageDefinition { nameIndex = name, typeStart = 1, typeCount = 1 },
                new Il2CppImageDefinition { nameIndex = name, typeStart = 2, typeCount = 0 }
            };
        }

        private JsonDocument ReadIndex() => JsonDocument.Parse(File.ReadAllText(Path.Combine(root, ImageDeclarationExport.IndexArtifact)));

        private string ImageFile(JsonElement entry) =>
            Path.Combine(root, ImageDeclarationExport.DirectoryName, entry.GetProperty("path").GetString()!);

        private static string WithoutHeader(string text) => text[(text.IndexOf('\n') + 1)..];
    }
}
