using System;
using System.IO;
using System.Text.Json;
using Il2CppDumper;
using Xunit;

namespace Il2CppDumper.Tests
{
    public class PeSectionBoundaryMappingTests
    {
        [Fact]
        public void MapRTVA_AdjacentSectionBoundary_MapsToSecondSection()
        {
            var sections = new (string, byte[], uint, uint)[]
            {
                (".text", new byte[512], 512, 0x60000020),
                (".data", new byte[512], 512, 0xC0000040)
            };
            var peBytes = ProtectionReportTests.CreateSyntheticPe(sections);
            using var pe = new PE(new MemoryStream(peBytes));

            // .text raw range is [0x400, 0x600), RVA 0x1000
            // .data raw range is [0x600, 0x800), RVA 0x2000
            // ImageBase is 0x180000000
            var beforeBoundary = pe.MapRTVA(0x5FF);
            var atBoundary = pe.MapRTVA(0x600);
            var afterBoundary = pe.MapRTVA(0x601);

            Assert.Equal(0x1800011FFul, beforeBoundary);
            Assert.Equal(0x180002000ul, atBoundary);
            Assert.Equal(0x180002001ul, afterBoundary);
        }

        [Fact]
        public void MapRTVA_UnmappedOffsetPastSectionEnd_ReturnsZero()
        {
            var sections = new (string, byte[], uint, uint)[]
            {
                (".text", new byte[512], 512, 0x60000020),
                (".data", new byte[512], 512, 0xC0000040)
            };
            var peBytes = ProtectionReportTests.CreateSyntheticPe(sections);
            using var pe = new PE(new MemoryStream(peBytes));

            // Offset 0x800 is immediately past the end of .data [0x600, 0x800)
            // It must not be assigned to .data
            var pastEnd = pe.MapRTVA(0x800);
            Assert.Equal(0ul, pastEnd);

            // An offset before the first section [0x400)
            var beforeFirst = pe.MapRTVA(0x3FF);
            Assert.Equal(0ul, beforeFirst);
        }

        [Fact]
        public void StructGenerator_AdjacentSectionBoundary_EmitsExpectedRvaInJsonArtifacts()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "pe_boundary_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                var sections = new (string, byte[], uint, uint)[]
                {
                    (".text", new byte[512], 512, 0x60000020),
                    (".data", new byte[512], 512, 0xC0000040)
                };
                var peBytes = ProtectionReportTests.CreateSyntheticPe(sections);

                // Place encoded metadata usage for v29 string literal at raw offset 0x600 (start of .data)
                // 0xA0000001UL: usage = 5 (kIl2CppMetadataUsageStringLiteral), index = 0
                BitConverter.GetBytes(0xA0000001UL).CopyTo(peBytes, 0x600);

                var metadataBytes = new byte[8192];
                BitConverter.GetBytes(0xFAB11BAFu).CopyTo(metadataBytes, 0); // magic
                BitConverter.GetBytes(29).CopyTo(metadataBytes, 4);         // version 29
                BitConverter.GetBytes(256).CopyTo(metadataBytes, 8);        // stringLiteralsOffset
                BitConverter.GetBytes(8).CopyTo(metadataBytes, 12);         // stringLiteralsCount
                BitConverter.GetBytes(1024).CopyTo(metadataBytes, 16);      // stringLiteralDataOffset
                BitConverter.GetBytes(1).CopyTo(metadataBytes, 20);         // stringLiteralDataCount
                BitConverter.GetBytes(1u).CopyTo(metadataBytes, 256);       // stringLiteral[0].length = 1
                metadataBytes[1024] = (byte)'A';

                using var pe = new PE(new MemoryStream(peBytes));
                using var metadata = new Metadata(new MemoryStream(metadataBytes));
                pe.Version = 29;
                pe.types = Array.Empty<Il2CppType>();

                var executor = new Il2CppExecutor(metadata, pe);
                var generator = new StructGenerator(executor);
                generator.WriteScript(tempDir + Path.DirectorySeparatorChar);

                // Verify stringliteral.json contains address 0x2000
                var stringLiteralJsonPath = Path.Combine(tempDir, "stringliteral.json");
                Assert.True(File.Exists(stringLiteralJsonPath));
                using (var doc = JsonDocument.Parse(File.ReadAllText(stringLiteralJsonPath)))
                {
                    var items = doc.RootElement;
                    Assert.True(items.GetArrayLength() > 0);
                    var addressProp = items[0].GetProperty("address").GetString();
                    Assert.Equal("0x2000", addressProp);
                }

                // Verify script.json contains ScriptString with address 0x2000 (8192)
                var scriptJsonPath = Path.Combine(tempDir, "script.json");
                Assert.True(File.Exists(scriptJsonPath));
                using (var doc = JsonDocument.Parse(File.ReadAllText(scriptJsonPath)))
                {
                    var scriptStringArray = doc.RootElement.GetProperty("ScriptString");
                    Assert.True(scriptStringArray.GetArrayLength() > 0);
                    var addressProp = scriptStringArray[0].GetProperty("Address").GetUInt64();
                    Assert.Equal(0x2000ul, addressProp);
                }
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
