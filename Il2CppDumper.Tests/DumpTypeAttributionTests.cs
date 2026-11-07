using System;
using System.IO;
using System.Text.RegularExpressions;
using Il2CppDumper.Core;
using Il2CppDumper.Tests.Synthetic;
using Xunit;

namespace Il2CppDumper.Tests
{
    public class DumpTypeAttributionTests
    {
        [Fact]
        public void DumpCs_WithDumpTypeDefIndex_EmitsImageAttributionSuffix()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "dumpcs-attribution-" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                var (metadata, il2Cpp, executor) = SyntheticEnvironment.Create(MetadataVersionFamily.V29, seed: 42);
                var config = new Config
                {
                    DumpTypeDefIndex = true,
                    DumpAttribute = false,
                    DumpField = true,
                    DumpMethod = true,
                    DumpProperty = true
                };

                var decompiler = new Il2CppDecompiler(executor);
                var success = decompiler.Decompile(config, tempDir);
                Assert.True(success);

                var dumpCsPath = Path.Combine(tempDir, "dump.cs");
                Assert.True(File.Exists(dumpCsPath));

                var content = File.ReadAllText(dumpCsPath);

                // Top-level image headers still exist
                Assert.Contains("// Image 0:", content);

                // Types must have the attribution comment suffix
                var match = Regex.Match(content, @"// TypeDefIndex: (\d+) \| ImageId: (\d+) \| Image: ([^\r\n]+)");
                Assert.True(match.Success, "Expected per-type image attribution comment suffix matching '// TypeDefIndex: N | ImageId: N | Image: name'");

                // Verify image id matches and image name is not empty
                Assert.Equal("0", match.Groups[2].Value);
                Assert.False(string.IsNullOrWhiteSpace(match.Groups[3].Value));
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }

        [Fact]
        public void DumpCs_WithoutDumpTypeDefIndex_OmitsAttributionSuffix()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "dumpcs-no-attribution-" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                var (metadata, il2Cpp, executor) = SyntheticEnvironment.Create(MetadataVersionFamily.V29, seed: 42);
                var config = new Config
                {
                    DumpTypeDefIndex = false,
                    DumpAttribute = false,
                    DumpField = false,
                    DumpMethod = false,
                    DumpProperty = false
                };

                var decompiler = new Il2CppDecompiler(executor);
                var success = decompiler.Decompile(config, tempDir);
                Assert.True(success);

                var dumpCsPath = Path.Combine(tempDir, "dump.cs");
                Assert.True(File.Exists(dumpCsPath));

                var content = File.ReadAllText(dumpCsPath);
                Assert.DoesNotContain("// TypeDefIndex:", content);
                Assert.DoesNotContain("| ImageId:", content);
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }

        [Fact]
        public void DumpCs_ConsumerCanClassifyTypesByAssemblyFromDumpCsAlone()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "dumpcs-classify-" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                var (metadata, il2Cpp, executor) = SyntheticEnvironment.Create(MetadataVersionFamily.V29, seed: 42);
                var config = new Config
                {
                    DumpTypeDefIndex = true,
                    DumpAttribute = false,
                    DumpField = true,
                    DumpMethod = true
                };

                var decompiler = new Il2CppDecompiler(executor);
                var success = decompiler.Decompile(config, tempDir);
                Assert.True(success);

                var dumpCsPath = Path.Combine(tempDir, "dump.cs");
                var lines = File.ReadAllLines(dumpCsPath);

                int classifiedTypes = 0;
                var pattern = new Regex(@"// TypeDefIndex: (?<typeDef>\d+) \| ImageId: (?<imageId>\d+) \| Image: (?<imageName>[^\r\n]+)");

                foreach (var line in lines)
                {
                    var m = pattern.Match(line);
                    if (m.Success)
                    {
                        classifiedTypes++;
                        var typeDefIdx = int.Parse(m.Groups["typeDef"].Value);
                        var imageId = int.Parse(m.Groups["imageId"].Value);
                        var imageName = m.Groups["imageName"].Value;

                        Assert.True(typeDefIdx >= 0);
                        Assert.True(imageId >= 0);
                        Assert.NotEmpty(imageName);
                    }
                }

                Assert.True(classifiedTypes > 0, "At least one type should be classified by assembly directly from dump.cs");
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }
        [Fact]
        public void DumpCs_MultipleImages_AttributesTypesToTheirRespectiveImageDef()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "dumpcs-multi-image-" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                var (metadata, il2Cpp, executor) = SyntheticEnvironment.Create(MetadataVersionFamily.V29, seed: 42);
                Assert.True(metadata.typeDefs.Length >= 2, "Synthetic environment should have at least 2 typeDefs");

                // Split the 2 types across 2 distinct images
                var origImg = metadata.imageDefs[0];
                metadata.imageDefs = new Il2CppImageDefinition[2];
                metadata.imageDefs[0] = new Il2CppImageDefinition
                {
                    nameIndex = origImg.nameIndex,
                    typeStart = 0,
                    typeCount = 1
                };
                metadata.imageDefs[1] = new Il2CppImageDefinition
                {
                    nameIndex = metadata.typeDefs[1].nameIndex,
                    typeStart = 1,
                    typeCount = 1
                };

                var config = new Config
                {
                    DumpTypeDefIndex = true,
                    DumpAttribute = false,
                    DumpField = true,
                    DumpMethod = true
                };

                var decompiler = new Il2CppDecompiler(executor);
                var success = decompiler.Decompile(config, tempDir);
                Assert.True(success);

                var dumpCsPath = Path.Combine(tempDir, "dump.cs");
                var content = File.ReadAllText(dumpCsPath);

                // Header comments at top for both images
                Assert.Contains("// Image 0:", content);
                Assert.Contains("// Image 1:", content);

                var img0Name = metadata.GetStringFromIndex(metadata.imageDefs[0].nameIndex);
                var img1Name = metadata.GetStringFromIndex(metadata.imageDefs[1].nameIndex);
                Assert.NotEqual(img0Name, img1Name);

                // Type 0 must belong to Image 0
                Assert.Contains($"// TypeDefIndex: 0 | ImageId: 0 | Image: {img0Name}", content);

                // Type 1 must belong to Image 1
                Assert.Contains($"// TypeDefIndex: 1 | ImageId: 1 | Image: {img1Name}", content);
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }
    }
}