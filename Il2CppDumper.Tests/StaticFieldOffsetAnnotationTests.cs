using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Il2CppDumper.Tests
{
    public class StaticFieldOffsetAnnotationTests
    {
        private static Metadata CreateTestMetadata()
        {
            var bytes = new byte[8192];
            BitConverter.GetBytes(0xFAB11BAFu).CopyTo(bytes, 0);
            BitConverter.GetBytes(29).CopyTo(bytes, 4);
            var m = new Metadata(new MemoryStream(bytes));
            m.header.stringOffset = 1024;
            m.header.stringSize = 2048;
            return m;
        }

        private static uint AddString(Metadata m, string value, ref uint pos)
        {
            var result = pos;
            var bytes = Encoding.UTF8.GetBytes(value + "\0");
            m.Position = m.header.stringOffset + pos;
            m.Writer.Write(bytes);
            pos += (uint)bytes.Length;
            return result;
        }

        [Fact]
        public void Decompiler_AnnotatesStaticFieldsAsStaticOffset_AndInstanceFieldsAsOffset()
        {
            // Issue #66: Static field storage slots in dump.cs must be annotated as `// static 0x...`
            // to clearly distinguish them from instance object memory layout offsets (`// 0x...`).
            using var m = CreateTestMetadata();
            uint pos = 0;
            AddString(m, "", ref pos);
            var image = AddString(m, "TestAssembly.dll", ref pos);
            var type = AddString(m, "SampleClass", ref pos);
            var instanceFieldName = AddString(m, "_instanceField", ref pos);
            var staticFieldName = AddString(m, "s_staticField", ref pos);
            var constFieldName = AddString(m, "c_constField", ref pos);

            m.imageDefs = new[]
            {
                new Il2CppImageDefinition { nameIndex = image, typeCount = 1 }
            };
            m.typeDefs = new[]
            {
                new Il2CppTypeDefinition
                {
                    nameIndex = type,
                    declaringTypeIndex = -1,
                    parentIndex = -1,
                    genericContainerIndex = -1,
                    fieldStart = 0,
                    field_count = 3,
                    flags = 1 // Public class
                }
            };
            m.fieldDefs = new[]
            {
                new Il2CppFieldDefinition { nameIndex = instanceFieldName, typeIndex = 0 },
                new Il2CppFieldDefinition { nameIndex = staticFieldName, typeIndex = 1 },
                new Il2CppFieldDefinition { nameIndex = constFieldName, typeIndex = 2 },
            };

            using var il = new WebAssemblyMemory(new MemoryStream(new byte[64]), 0);
            il.Version = 29;
            il.types = new[]
            {
                new Il2CppType { type = Il2CppTypeEnum.IL2CPP_TYPE_I4, attrs = (uint)Il2CppConstants.FIELD_ATTRIBUTE_PUBLIC }, // instance
                new Il2CppType { type = Il2CppTypeEnum.IL2CPP_TYPE_I4, attrs = (uint)(Il2CppConstants.FIELD_ATTRIBUTE_PUBLIC | Il2CppConstants.FIELD_ATTRIBUTE_STATIC) }, // static
                new Il2CppType { type = Il2CppTypeEnum.IL2CPP_TYPE_I4, attrs = (uint)(Il2CppConstants.FIELD_ATTRIBUTE_PUBLIC | Il2CppConstants.FIELD_ATTRIBUTE_STATIC | Il2CppConstants.FIELD_ATTRIBUTE_LITERAL) }, // const
            };

            // Set field offsets: instance field at 0x10, static field storage slot at 0x8
            typeof(Il2Cpp).GetField("fieldOffsets", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(il, new ulong[] { 0x10, 0x8, 0x0 });
            typeof(Il2Cpp).GetField("fieldOffsetsArePointers", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(il, false);

            var tempDir = Path.Combine(Path.GetTempPath(), "il2cpp-issue-66-repro-" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                var executor = new Il2CppExecutor(m, il);
                var decompiler = new Il2CppDecompiler(executor);
                decompiler.Decompile(
                    new Config { DumpAttribute = false, DumpProperty = false, DumpMethod = false, DumpField = true, DumpFieldOffset = true },
                    tempDir + Path.DirectorySeparatorChar);

                var dumpPath = Path.Combine(tempDir, "dump.cs");
                var lines = File.ReadAllLines(dumpPath);

                var instanceLine = lines.First(l => l.Contains("_instanceField"));
                var staticLine = lines.First(l => l.Contains("s_staticField"));
                var constLine = lines.First(l => l.Contains("c_constField"));

                // 1. Instance field has `// 0x10` and does NOT have `static`
                Assert.Contains("; // 0x10", instanceLine);
                Assert.DoesNotContain("// static", instanceLine);

                // 2. Static field has `// static 0x8` and does NOT have raw `; // 0x8`
                Assert.Contains("; // static 0x8", staticLine);
                Assert.DoesNotContain("; // 0x8", staticLine);

                // 3. Const field has no offset comment
                Assert.EndsWith(";", constLine.TrimEnd());
                Assert.DoesNotContain("// 0x", constLine);
                Assert.DoesNotContain("// static", constLine);

                // 4. Blind instance offset regex consumer only binds instance fields
                var instanceOffsetRegex = new Regex(@";\s*//\s*0x([0-9A-Fa-f]+)");
                Assert.Matches(instanceOffsetRegex, instanceLine);
                Assert.Equal("10", instanceOffsetRegex.Match(instanceLine).Groups[1].Value);

                Assert.DoesNotMatch(instanceOffsetRegex, staticLine);

                // 5. Dedicated static offset regex matches static storage slot
                var staticOffsetRegex = new Regex(@";\s*//\s*static\s*0x([0-9A-Fa-f]+)");
                Assert.Matches(staticOffsetRegex, staticLine);
                Assert.Equal("8", staticOffsetRegex.Match(staticLine).Groups[1].Value);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public void Decompiler_WhenDumpFieldOffsetDisabled_EmitsNoOffsetCommentsForAnyField()
        {
            using var m = CreateTestMetadata();
            uint pos = 0;
            AddString(m, "", ref pos);
            var image = AddString(m, "TestAssembly.dll", ref pos);
            var type = AddString(m, "SampleClass", ref pos);
            var instanceFieldName = AddString(m, "_instanceField", ref pos);
            var staticFieldName = AddString(m, "s_staticField", ref pos);

            m.imageDefs = new[] { new Il2CppImageDefinition { nameIndex = image, typeCount = 1 } };
            m.typeDefs = new[]
            {
                new Il2CppTypeDefinition
                {
                    nameIndex = type,
                    declaringTypeIndex = -1,
                    parentIndex = -1,
                    genericContainerIndex = -1,
                    fieldStart = 0,
                    field_count = 2,
                    flags = 1
                }
            };
            m.fieldDefs = new[]
            {
                new Il2CppFieldDefinition { nameIndex = instanceFieldName, typeIndex = 0 },
                new Il2CppFieldDefinition { nameIndex = staticFieldName, typeIndex = 1 }
            };

            using var il = new WebAssemblyMemory(new MemoryStream(new byte[64]), 0);
            il.Version = 29;
            il.types = new[]
            {
                new Il2CppType { type = Il2CppTypeEnum.IL2CPP_TYPE_I4, attrs = (uint)Il2CppConstants.FIELD_ATTRIBUTE_PUBLIC },
                new Il2CppType { type = Il2CppTypeEnum.IL2CPP_TYPE_I4, attrs = (uint)(Il2CppConstants.FIELD_ATTRIBUTE_PUBLIC | Il2CppConstants.FIELD_ATTRIBUTE_STATIC) }
            };

            typeof(Il2Cpp).GetField("fieldOffsets", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(il, new ulong[] { 0x10, 0x8 });
            typeof(Il2Cpp).GetField("fieldOffsetsArePointers", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(il, false);

            var tempDir = Path.Combine(Path.GetTempPath(), "il2cpp-issue-66-nooffsets-" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                var executor = new Il2CppExecutor(m, il);
                var decompiler = new Il2CppDecompiler(executor);
                decompiler.Decompile(
                    new Config { DumpAttribute = false, DumpProperty = false, DumpMethod = false, DumpField = true, DumpFieldOffset = false },
                    tempDir + Path.DirectorySeparatorChar);

                var dumpPath = Path.Combine(tempDir, "dump.cs");
                var lines = File.ReadAllLines(dumpPath);

                var instanceLine = lines.First(l => l.Contains("_instanceField"));
                var staticLine = lines.First(l => l.Contains("s_staticField"));

                Assert.EndsWith(";", instanceLine.TrimEnd());
                Assert.EndsWith(";", staticLine.TrimEnd());
                Assert.DoesNotContain("// 0x", instanceLine);
                Assert.DoesNotContain("// static", staticLine);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }
}
