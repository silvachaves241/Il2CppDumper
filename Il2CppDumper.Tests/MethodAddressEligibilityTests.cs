using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Il2CppDumper;
using Mono.Cecil;
using Xunit;

namespace Il2CppDumper.Tests
{
    public class MethodAddressEligibilityTests
    {
        private static (byte[] peBytes, ulong validCodeVa, ulong dataVa) CreateTestPe()
        {
            var sections = new (string, byte[], uint, uint)[]
            {
                (".text", new byte[512], 512, 0x60000020), // RVA 0x1000, raw 0x400, executable
                (".data", new byte[512], 512, 0xC0000040)  // RVA 0x2000, raw 0x600, non-executable data
            };
            var peBytes = ProtectionReportTests.CreateSyntheticPe(sections);
            // ImageBase in CreateSyntheticPe is 0x180000000
            var validCodeVa = 0x180001000ul;
            var dataVa = 0x180002000ul;
            return (peBytes, validCodeVa, dataVa);
        }

        private static Metadata CreateTestMetadata(
            string imageName,
            string typeName,
            bool isInterface,
            (string methodName, bool isAbstract, uint token)[] methods)
        {
            var bytes = new byte[8192];
            BitConverter.GetBytes(0xFAB11BAFu).CopyTo(bytes, 0);
            BitConverter.GetBytes(29).CopyTo(bytes, 4);

            var m = new Metadata(new MemoryStream(bytes));
            m.header.stringOffset = 1024;
            m.header.stringSize = 4096;

            uint pos = 0;
            uint AddStr(string val)
            {
                var r = pos;
                var strBytes = Encoding.UTF8.GetBytes(val + "\0");
                m.Position = m.header.stringOffset + pos;
                m.Writer.Write(strBytes);
                pos += (uint)strBytes.Length;
                return r;
            }

            AddStr("");
            var imgNameIdx = AddStr(imageName);
            var typeNameIdx = AddStr(typeName);

            m.imageDefs = new[]
            {
                new Il2CppImageDefinition
                {
                    nameIndex = imgNameIdx,
                    assemblyIndex = -1,
                    typeStart = 0,
                    typeCount = 1
                }
            };

            m.typeDefs = new[]
            {
                new Il2CppTypeDefinition
                {
                    nameIndex = typeNameIdx,
                    declaringTypeIndex = -1,
                    parentIndex = -1,
                    genericContainerIndex = -1,
                    methodStart = 0,
                    method_count = (ushort)methods.Length,
                    flags = isInterface ? (uint)(Il2CppConstants.TYPE_ATTRIBUTE_INTERFACE | Il2CppConstants.TYPE_ATTRIBUTE_PUBLIC) : (uint)Il2CppConstants.TYPE_ATTRIBUTE_PUBLIC,
                    byvalTypeIndex = 0
                }
            };

            var methodDefList = new List<Il2CppMethodDefinition>();
            for (int i = 0; i < methods.Length; i++)
            {
                var (mName, isAbs, tok) = methods[i];
                var mNameIdx = AddStr(mName);
                ushort flags = isAbs
                    ? (ushort)(Il2CppConstants.METHOD_ATTRIBUTE_ABSTRACT | Il2CppConstants.METHOD_ATTRIBUTE_VIRTUAL | Il2CppConstants.METHOD_ATTRIBUTE_PUBLIC)
                    : (ushort)Il2CppConstants.METHOD_ATTRIBUTE_PUBLIC;

                methodDefList.Add(new Il2CppMethodDefinition
                {
                    nameIndex = mNameIdx,
                    declaringType = 0,
                    returnType = 0,
                    parameterCount = 0,
                    parameterStart = 0,
                    genericContainerIndex = -1,
                    methodIndex = 0,
                    token = tok,
                    flags = flags,
                    slot = ushort.MaxValue
                });
            }

            m.methodDefs = methodDefList.ToArray();
            m.parameterDefs = Array.Empty<Il2CppParameterDefinition>();
            m.propertyDefs = Array.Empty<Il2CppPropertyDefinition>();
            m.fieldDefs = Array.Empty<Il2CppFieldDefinition>();
            return m;
        }

        [Fact]
        public void SyntheticAbstractInterfaceMethod_WithNonZeroInvalidCandidatePointer_EmitsNoResolvedAddress_AcrossAllOutputs()
        {
            var (peBytes, validCodeVa, _) = CreateTestPe();
            var invalidCandidatePointer = 6144708780086416ul; // 0x15D4948015D490 from Issue #88

            using var pe = new PE(new MemoryStream(peBytes));
            pe.Version = 29;
            pe.types = new[] { new Il2CppType { type = Il2CppTypeEnum.IL2CPP_TYPE_VOID } };
            pe.codeGenModuleMethodPointers = new Dictionary<string, ulong[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["gdio.plugin.serializer.dll"] = new[] { invalidCandidatePointer }
            };

            using var metadata = CreateTestMetadata(
                "gdio.plugin.serializer.dll",
                "gdio.plugin.serializer.ICustomSerializer",
                isInterface: true,
                new[] { ("SupportedTypes", true, 0x06000001u) });

            pe.SetProperties(29, 0, metadata);
            var executor = new Il2CppExecutor(metadata, pe);

            // 1. Direct engine check: GetMethodPointer must return 0 for abstract/interface declarations
            var pointer = pe.GetMethodPointer("gdio.plugin.serializer.dll", metadata.methodDefs[0]);
            Assert.Equal(0ul, pointer);

            var tempDir = Path.Combine(Path.GetTempPath(), "issue88_abstract_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                // 2. StructGenerator: script.json must NOT contain ScriptMethod entry for abstract method
                var structGenerator = new StructGenerator(executor);
                structGenerator.WriteScript(tempDir + Path.DirectorySeparatorChar);

                var scriptJsonPath = Path.Combine(tempDir, "script.json");
                Assert.True(File.Exists(scriptJsonPath));
                var scriptJsonText = File.ReadAllText(scriptJsonPath);
                using (var doc = JsonDocument.Parse(scriptJsonText))
                {
                    var scriptMethods = doc.RootElement.GetProperty("ScriptMethod");
                    var matching = scriptMethods.EnumerateArray()
                        .Where(x => x.GetProperty("Name").GetString()!.Contains("SupportedTypes"))
                        .ToList();
                    Assert.Empty(matching);
                }

                // 3. Il2CppDecompiler: dump.cs must emit '// RVA: -1 Offset: -1'
                var decompiler = new Il2CppDecompiler(executor);
                var config = new Config { DumpMethod = true, DumpMethodOffset = true, DumpTypeDefIndex = true, DumpAttribute = false };
                decompiler.Decompile(config, tempDir);

                var dumpCsPath = Path.Combine(tempDir, "dump.cs");
                Assert.True(File.Exists(dumpCsPath));
                var dumpCsText = File.ReadAllText(dumpCsPath);
                Assert.Contains("// RVA: -1 Offset: -1", dumpCsText);
                Assert.DoesNotContain("0x15D4948015D490", dumpCsText);

                // 4. DummyAssemblyGenerator: DummyDll must NOT attach AddressAttribute
                var dummyGenerator = new DummyAssemblyGenerator(executor, false);
                var assembly = dummyGenerator.Assemblies.First(a => a.MainModule.Name == "gdio.plugin.serializer.dll");
                var method = assembly.MainModule.Types.First(t => t.Name == "gdio.plugin.serializer.ICustomSerializer").Methods[0];
                Assert.DoesNotContain(method.CustomAttributes, a => a.AttributeType.Name == "AddressAttribute");
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
        public void ConcreteMethod_WithValidPointer_IsExportedConsistently_AcrossAllOutputs()
        {
            var (peBytes, validCodeVa, _) = CreateTestPe();

            using var pe = new PE(new MemoryStream(peBytes));
            pe.Version = 29;
            pe.types = new[] { new Il2CppType { type = Il2CppTypeEnum.IL2CPP_TYPE_VOID } };
            pe.codeGenModuleMethodPointers = new Dictionary<string, ulong[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["TestModule.dll"] = new[] { validCodeVa } // 0x180001000: RVA 0x1000, Offset 0x400
            };

            using var metadata = CreateTestMetadata(
                "TestModule.dll",
                "TestNamespace.ConcreteClass",
                isInterface: false,
                new[] { ("Execute", false, 0x06000001u) });

            pe.SetProperties(29, 0, metadata);
            var executor = new Il2CppExecutor(metadata, pe);

            // 1. Direct engine check: valid pointer in .text is resolved
            var pointer = pe.GetMethodPointer("TestModule.dll", metadata.methodDefs[0]);
            Assert.Equal(validCodeVa, pointer);

            var tempDir = Path.Combine(Path.GetTempPath(), "issue88_concrete_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                // 2. StructGenerator: script.json contains ScriptMethod with resolved RVA 0x1000
                var structGenerator = new StructGenerator(executor);
                structGenerator.WriteScript(tempDir + Path.DirectorySeparatorChar);

                var scriptJsonPath = Path.Combine(tempDir, "script.json");
                Assert.True(File.Exists(scriptJsonPath));
                using (var doc = JsonDocument.Parse(File.ReadAllText(scriptJsonPath)))
                {
                    var scriptMethods = doc.RootElement.GetProperty("ScriptMethod");
                    var matching = scriptMethods.EnumerateArray()
                        .Where(x => x.GetProperty("Name").GetString()!.Contains("Execute"))
                        .ToList();
                    Assert.Single(matching);
                    Assert.Equal(0x1000ul, matching[0].GetProperty("Address").GetUInt64());
                }

                // 3. Il2CppDecompiler: dump.cs contains RVA: 0x1000 Offset: 0x400 VA: 0x180001000
                var decompiler = new Il2CppDecompiler(executor);
                var config = new Config { DumpMethod = true, DumpMethodOffset = true, DumpAttribute = false };
                decompiler.Decompile(config, tempDir);

                var dumpCsPath = Path.Combine(tempDir, "dump.cs");
                var dumpCsText = File.ReadAllText(dumpCsPath);
                Assert.Contains("// RVA: 0x1000 Offset: 0x400 VA: 0x180001000", dumpCsText);

                // 4. DummyAssemblyGenerator: DummyDll attaches AddressAttribute with RVA, Offset, VA
                var dummyGenerator = new DummyAssemblyGenerator(executor, false);
                var assembly = dummyGenerator.Assemblies.First(a => a.MainModule.Name == "TestModule.dll");
                var method = assembly.MainModule.Types.First(t => t.Name == "TestNamespace.ConcreteClass").Methods[0];
                var addrAttr = method.CustomAttributes.FirstOrDefault(a => a.AttributeType.Name == "AddressAttribute");
                Assert.NotNull(addrAttr);
                Assert.Equal("0x1000", addrAttr.Fields.First(f => f.Name == "RVA").Argument.Value);
                Assert.Equal("0x400", addrAttr.Fields.First(f => f.Name == "Offset").Argument.Value);
                Assert.Equal("0x180001000", addrAttr.Fields.First(f => f.Name == "VA").Argument.Value);
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
        public void ConcreteMethod_WithCandidatePointerOutsideExecutableSections_EmitsNoResolvedAddress()
        {
            var (peBytes, _, dataVa) = CreateTestPe();
            var outsideSectionVa = 6144708780086416ul;

            // Test both: outside all sections AND inside non-executable .data section
            foreach (var testPointer in new[] { outsideSectionVa, dataVa })
            {
                using var pe = new PE(new MemoryStream(peBytes));
                pe.Version = 29;
                pe.types = new[] { new Il2CppType { type = Il2CppTypeEnum.IL2CPP_TYPE_VOID } };
                pe.codeGenModuleMethodPointers = new Dictionary<string, ulong[]>(StringComparer.OrdinalIgnoreCase)
                {
                    ["TestModule.dll"] = new[] { testPointer }
                };

                using var metadata = CreateTestMetadata(
                    "TestModule.dll",
                    "TestNamespace.ConcreteClass",
                    isInterface: false,
                    new[] { ("Compute", false, 0x06000001u) });

                pe.SetProperties(29, 0, metadata);
                var executor = new Il2CppExecutor(metadata, pe);

                // Must be rejected as invalid method pointer
                Assert.False(pe.IsValidMethodPointer(testPointer));
                Assert.Equal(0ul, pe.GetMethodPointer("TestModule.dll", metadata.methodDefs[0]));

                var tempDir = Path.Combine(Path.GetTempPath(), "issue88_invalid_concrete_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                try
                {
                    var structGenerator = new StructGenerator(executor);
                    structGenerator.WriteScript(tempDir + Path.DirectorySeparatorChar);

                    var scriptJsonPath = Path.Combine(tempDir, "script.json");
                    using (var doc = JsonDocument.Parse(File.ReadAllText(scriptJsonPath)))
                    {
                        var scriptMethods = doc.RootElement.GetProperty("ScriptMethod");
                        var matching = scriptMethods.EnumerateArray()
                            .Where(x => x.GetProperty("Name").GetString()!.Contains("Compute"))
                            .ToList();
                        Assert.Empty(matching);
                    }

                    var decompiler = new Il2CppDecompiler(executor);
                    decompiler.Decompile(new Config { DumpMethod = true, DumpMethodOffset = true, DumpAttribute = false }, tempDir);

                    var dumpCsText = File.ReadAllText(Path.Combine(tempDir, "dump.cs"));
                    Assert.Contains("// RVA: -1 Offset: -1", dumpCsText);

                    var dummyGenerator = new DummyAssemblyGenerator(executor, false);
                    var assembly = dummyGenerator.Assemblies.First(a => a.MainModule.Name == "TestModule.dll");
                    var method = assembly.MainModule.Types.First(t => t.Name == "TestNamespace.ConcreteClass").Methods[0];
                    Assert.DoesNotContain(method.CustomAttributes, a => a.AttributeType.Name == "AddressAttribute");
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

        [Fact]
        public void DumpedPeMemoryPath_ValidPointerInExecutableSection_ExportsResolvedAddress()
        {
            var (peBytes, _, _) = CreateTestPe();

            using var pe = new PE(new MemoryStream(peBytes));
            // Simulate dumped binary loaded at base address 0x200000000
            pe.LoadFromMemory(0x200000000ul);
            pe.Version = 29;
            pe.types = new[] { new Il2CppType { type = Il2CppTypeEnum.IL2CPP_TYPE_VOID } };

            var dumpedCodeVa = 0x200001000ul; // RVA 0x1000 in .text
            pe.codeGenModuleMethodPointers = new Dictionary<string, ulong[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["DumpedModule.dll"] = new[] { dumpedCodeVa }
            };

            using var metadata = CreateTestMetadata(
                "DumpedModule.dll",
                "DumpedNamespace.DumpedClass",
                isInterface: false,
                new[] { ("DumpedMethod", false, 0x06000001u) });

            pe.SetProperties(29, 0, metadata);
            var executor = new Il2CppExecutor(metadata, pe);

            Assert.True(pe.IsValidMethodPointer(dumpedCodeVa));
            Assert.Equal(dumpedCodeVa, pe.GetMethodPointer("DumpedModule.dll", metadata.methodDefs[0]));
            Assert.Equal(0x1000ul, pe.GetRVA(dumpedCodeVa));
            Assert.Equal(0x1000ul, pe.MapVATR(dumpedCodeVa)); // In memory dump, MapVATR = RVA
        }

        [Fact]
        public void RealTargetIssue88Regression_AllThreeICustomSerializerMethods_OmittedFromScriptJsonAndUnresolvedInDumpCs()
        {
            var (peBytes, _, _) = CreateTestPe();

            // The exact three methods and candidate pointers reported in Issue #88
            var candidatePointers = new ulong[]
            {
                6144708780086416ul,  // SupportedTypes
                6144751835038524ul,  // Serialize
                451243860565546221ul // Deserialize
            };

            using var pe = new PE(new MemoryStream(peBytes));
            pe.Version = 39;
            pe.types = new[] { new Il2CppType { type = Il2CppTypeEnum.IL2CPP_TYPE_VOID } };
            pe.codeGenModuleMethodPointers = new Dictionary<string, ulong[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["gdio.plugin.serializer.dll"] = candidatePointers
            };

            using var metadata = CreateTestMetadata(
                "gdio.plugin.serializer.dll",
                "gdio.plugin.serializer.ICustomSerializer",
                isInterface: true,
                new[]
                {
                    ("SupportedTypes", true, 0x06000001u),
                    ("Serialize", true, 0x06000002u),
                    ("Deserialize", true, 0x06000003u)
                });

            pe.SetProperties(39, 0, metadata);
            var executor = new Il2CppExecutor(metadata, pe);

            for (int i = 0; i < 3; i++)
            {
                Assert.Equal(0ul, pe.GetMethodPointer("gdio.plugin.serializer.dll", metadata.methodDefs[i]));
            }

            var tempDir = Path.Combine(Path.GetTempPath(), "issue88_real_regression_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                // Verify script.json contains NONE of the three declarations in ScriptMethod
                var structGenerator = new StructGenerator(executor);
                structGenerator.WriteScript(tempDir + Path.DirectorySeparatorChar);

                var scriptJsonPath = Path.Combine(tempDir, "script.json");
                using (var doc = JsonDocument.Parse(File.ReadAllText(scriptJsonPath)))
                {
                    var scriptMethods = doc.RootElement.GetProperty("ScriptMethod");
                    var matched = scriptMethods.EnumerateArray()
                        .Where(x => x.GetProperty("Name").GetString()!.StartsWith("gdio.plugin.serializer.ICustomSerializer$$"))
                        .ToList();
                    Assert.Empty(matched);
                }

                // Verify dump.cs marks all three as unresolved: // RVA: -1 Offset: -1
                var decompiler = new Il2CppDecompiler(executor);
                decompiler.Decompile(new Config { DumpMethod = true, DumpMethodOffset = true, DumpAttribute = false }, tempDir);

                var dumpCsLines = File.ReadAllLines(Path.Combine(tempDir, "dump.cs"));
                var methodLines = dumpCsLines.Where(l => l.Contains("SupportedTypes()") || l.Contains("Serialize(") || l.Contains("Deserialize(")).ToList();
                Assert.Equal(3, methodLines.Count);

                // Verify DummyDll contains no AddressAttribute on any of the three
                var dummyGenerator = new DummyAssemblyGenerator(executor, false);
                var assembly = dummyGenerator.Assemblies.First(a => a.MainModule.Name == "gdio.plugin.serializer.dll");
                var typeDef = assembly.MainModule.Types.First(t => t.Name == "gdio.plugin.serializer.ICustomSerializer");
                foreach (var m in typeDef.Methods)
                {
                    Assert.DoesNotContain(m.CustomAttributes, a => a.AttributeType.Name == "AddressAttribute");
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
