using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Mono.Cecil;
using Xunit;
using static Il2CppDumper.Il2CppConstants;

namespace Il2CppDumper.Tests
{
    public class DummyAssemblyCustomAttributeExportTests
    {
        [Fact]
        public void Export_EnumWithPrimitiveParentIndex_EncodesEnumAttributeAndPreservesPropertyAttributesAndConstants()
        {
            var (metadata, il2Cpp) = CreateProtocolMetadataEnvironment(includeBrokenEnumAttribute: false);
            var executor = new Il2CppExecutor(metadata, il2Cpp);

            var warnings = new List<string>();
            var tempDir = Path.Combine(Path.GetTempPath(), "il2cpp_issue102_valid_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                List<string> written;
                using (CoreLogger.Scope(null, warnings))
                {
                    written = DummyAssemblyExporter.Export(executor, tempDir, addToken: true);
                }

                Assert.Empty(warnings);
                var dllPath = Assert.Single(written, p => Path.GetFileName(p) == "TestProtocol.dll");

                using var assembly = AssemblyDefinition.ReadAssembly(dllPath);
                var module = assembly.MainModule;

                var enumType = module.GetType("System.ComponentModel.EditorBrowsableState");
                Assert.NotNull(enumType);
                Assert.True(enumType.IsEnum);
                Assert.Equal("System.Enum", enumType.BaseType?.FullName);

                var neverField = enumType.Fields.Single(f => f.Name == "Never");
                Assert.True(neverField.IsLiteral);
                Assert.True(neverField.HasConstant);
                Assert.Equal(1, neverField.Constant);

                var reflectionHolder = module.GetType("TestNamespace.ProtocolReflection");
                Assert.NotNull(reflectionHolder);
                var editorAttr = Assert.Single(
                    reflectionHolder.CustomAttributes,
                    a => a.AttributeType.FullName == "System.ComponentModel.EditorBrowsableAttribute");
                var editorArg = Assert.Single(editorAttr.ConstructorArguments);
                Assert.Equal("System.ComponentModel.EditorBrowsableState", editorArg.Type.FullName);
                Assert.Equal(1, editorArg.Value);

                var messageType = module.GetType("TestNamespace.SampleMessage");
                Assert.NotNull(messageType);
                var payloadProp = Assert.Single(messageType.Properties, p => p.Name == "Payload");

                Assert.Contains(
                    payloadProp.CustomAttributes,
                    a => a.AttributeType.FullName == "System.Diagnostics.DebuggerNonUserCodeAttribute");

                var generatedCodeAttr = Assert.Single(
                    payloadProp.CustomAttributes,
                    a => a.AttributeType.FullName == "System.CodeDom.Compiler.GeneratedCodeAttribute");
                Assert.Equal(2, generatedCodeAttr.ConstructorArguments.Count);
                Assert.Equal("protoc", generatedCodeAttr.ConstructorArguments[0].Value);
                Assert.Equal("3.21.0", generatedCodeAttr.ConstructorArguments[1].Value);
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        [Fact]
        public void Export_WithFailingEnumValuedAttributeInMetadata_PreservesValidPropertyAttributesAndReportsOmission()
        {
            var (metadata, il2Cpp) = CreateProtocolMetadataEnvironment(includeBrokenEnumAttribute: true);
            var executor = new Il2CppExecutor(metadata, il2Cpp);

            var warnings = new List<string>();
            var tempDir = Path.Combine(Path.GetTempPath(), "il2cpp_issue102_mixed_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                List<string> written;
                using (CoreLogger.Scope(null, warnings))
                {
                    written = DummyAssemblyExporter.Export(executor, tempDir, addToken: true);
                }

                var dllPath = Assert.Single(written, p => Path.GetFileName(p) == "TestProtocol.dll");

                var warning = Assert.Single(warnings);
                Assert.Contains("TestProtocol", warning);
                Assert.Contains("TestNamespace.SampleMessage.BrokenProperty", warning);
                Assert.Contains("BrokenEnumAttribute", warning);
                Assert.Contains("Unable to resolve underlying type for enum", warning);

                using var assembly = AssemblyDefinition.ReadAssembly(dllPath);
                var module = assembly.MainModule;

                var messageType = module.GetType("TestNamespace.SampleMessage");
                Assert.NotNull(messageType);

                var brokenProp = Assert.Single(messageType.Properties, p => p.Name == "BrokenProperty");
                Assert.DoesNotContain(
                    brokenProp.CustomAttributes,
                    a => a.AttributeType.Name == "BrokenEnumAttribute");

                var validProp = Assert.Single(messageType.Properties, p => p.Name == "Payload");
                Assert.Contains(
                    validProp.CustomAttributes,
                    a => a.AttributeType.FullName == "System.Diagnostics.DebuggerNonUserCodeAttribute");

                var generatedCodeAttr = Assert.Single(
                    validProp.CustomAttributes,
                    a => a.AttributeType.FullName == "System.CodeDom.Compiler.GeneratedCodeAttribute");
                Assert.Equal(2, generatedCodeAttr.ConstructorArguments.Count);
                Assert.Equal("protoc", generatedCodeAttr.ConstructorArguments[0].Value);
                Assert.Equal("3.21.0", generatedCodeAttr.ConstructorArguments[1].Value);
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        [Fact]
        public void WriteAssembly_WithFailingEnumAttributeOnSeparateMember_PreservesValidPropertyAttributesAndReportsOmission()
        {
            using var assembly = AssemblyDefinition.CreateAssembly(
                new AssemblyNameDefinition("ProtocolSample", new Version(1, 0, 0, 0)),
                "ProtocolSample.dll",
                ModuleKind.Dll);

            var module = assembly.MainModule;

            var brokenEnumType = new TypeDefinition(
                "ProtocolSample",
                "BrokenEnum",
                Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Sealed,
                module.ImportReference(typeof(Enum)));
            module.Types.Add(brokenEnumType);

            var brokenEnumAttrType = CreateAttributeType(module, "ProtocolSample", "BrokenEnumAttribute", brokenEnumType);
            var dbgNonUserCodeAttrType = CreateAttributeType(module, "System.Diagnostics", "DebuggerNonUserCodeAttribute");
            var generatedCodeAttrType = CreateAttributeType(
                module,
                "System.CodeDom.Compiler",
                "GeneratedCodeAttribute",
                module.TypeSystem.String,
                module.TypeSystem.String);

            var messageType = new TypeDefinition(
                "ProtocolSample",
                "SampleMessage",
                Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class,
                module.TypeSystem.Object);
            module.Types.Add(messageType);

            var brokenProperty = new PropertyDefinition("BrokenField", Mono.Cecil.PropertyAttributes.None, module.TypeSystem.Int32);
            var brokenAttr = new CustomAttribute(brokenEnumAttrType.Methods.Single(m => m.Name == ".ctor"));
            brokenAttr.ConstructorArguments.Add(new CustomAttributeArgument(brokenEnumType, 1));
            brokenProperty.CustomAttributes.Add(brokenAttr);
            messageType.Properties.Add(brokenProperty);

            var validProperty = new PropertyDefinition("ValidField", Mono.Cecil.PropertyAttributes.None, module.TypeSystem.Int32);
            validProperty.CustomAttributes.Add(new CustomAttribute(dbgNonUserCodeAttrType.Methods.Single(m => m.Name == ".ctor")));
            var genAttr = new CustomAttribute(generatedCodeAttrType.Methods.Single(m => m.Name == ".ctor"));
            genAttr.ConstructorArguments.Add(new CustomAttributeArgument(module.TypeSystem.String, "protoc"));
            genAttr.ConstructorArguments.Add(new CustomAttributeArgument(module.TypeSystem.String, "3.21.0"));
            validProperty.CustomAttributes.Add(genAttr);
            messageType.Properties.Add(validProperty);

            var warnings = new List<string>();
            byte[] emittedBytes;
            using (CoreLogger.Scope(null, warnings))
            {
                DummyAssemblyExporter.SanitizeCustomAttributes(assembly);
                emittedBytes = DummyAssemblyExporter.WriteAssembly(assembly);
            }

            var warning = Assert.Single(warnings);
            Assert.Contains("ProtocolSample", warning);
            Assert.Contains("ProtocolSample.SampleMessage.BrokenField", warning);
            Assert.Contains("ProtocolSample.BrokenEnumAttribute", warning);
            Assert.Contains("ProtocolSample.BrokenEnum", warning);

            using var reopened = AssemblyDefinition.ReadAssembly(new MemoryStream(emittedBytes));
            var reopenedMessage = reopened.MainModule.GetType("ProtocolSample.SampleMessage");
            Assert.NotNull(reopenedMessage);

            var reopenedBrokenProp = reopenedMessage.Properties.Single(p => p.Name == "BrokenField");
            Assert.Empty(reopenedBrokenProp.CustomAttributes);

            var reopenedValidProp = reopenedMessage.Properties.Single(p => p.Name == "ValidField");
            Assert.Contains(
                reopenedValidProp.CustomAttributes,
                a => a.AttributeType.FullName == "System.Diagnostics.DebuggerNonUserCodeAttribute");

            var reopenedGenAttr = Assert.Single(
                reopenedValidProp.CustomAttributes,
                a => a.AttributeType.FullName == "System.CodeDom.Compiler.GeneratedCodeAttribute");
            Assert.Equal("protoc", reopenedGenAttr.ConstructorArguments[0].Value);
            Assert.Equal("3.21.0", reopenedGenAttr.ConstructorArguments[1].Value);
        }

        private static TypeDefinition CreateAttributeType(ModuleDefinition module, string ns, string name, params TypeReference[] ctorParamTypes)
        {
            var attrType = new TypeDefinition(
                ns,
                name,
                Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class,
                module.ImportReference(typeof(Attribute)));
            module.Types.Add(attrType);

            var ctor = new MethodDefinition(
                ".ctor",
                Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.HideBySig | Mono.Cecil.MethodAttributes.SpecialName | Mono.Cecil.MethodAttributes.RTSpecialName,
                module.TypeSystem.Void);
            for (var i = 0; i < ctorParamTypes.Length; i++)
            {
                ctor.Parameters.Add(new ParameterDefinition($"p{i}", Mono.Cecil.ParameterAttributes.None, ctorParamTypes[i]));
            }
            attrType.Methods.Add(ctor);
            return attrType;
        }

        private static (Metadata metadata, WebAssemblyMemory il2Cpp) CreateProtocolMetadataEnvironment(bool includeBrokenEnumAttribute)
        {
            var bytes = new byte[16384];
            BitConverter.GetBytes(0xFAB11BAFu).CopyTo(bytes, 0);
            BitConverter.GetBytes(29).CopyTo(bytes, 4);
            var m = new Metadata(new MemoryStream(bytes))
            {
                Version = 29
            };
            m.header.stringOffset = 512;
            m.header.stringSize = 2048;
            m.header.attributeDataOffset = 4096;
            m.header.fieldAndParameterDefaultValueDataOffset = 8192;

            uint strPos = 0;
            uint WriteStr(string s)
            {
                var ret = strPos;
                var b = Encoding.UTF8.GetBytes(s + "\0");
                Buffer.BlockCopy(b, 0, bytes, (int)(m.header.stringOffset + strPos), b.Length);
                strPos += (uint)b.Length;
                return ret;
            }

            // Default value data at offset 8192: compressed int32 value 1 (encoded as 1 << 1 = 2) for EditorBrowsableState.Never
            bytes[m.header.fieldAndParameterDefaultValueDataOffset] = 2;

            // Types in il2Cpp.types:
            // 0: System.Void
            // 1: System.Int32 (attrs = instance field for value__)
            // 2: System.String
            // 3: EditorBrowsableState (VALUETYPE, data.dummy = 0 -> typeDefs[0], static literal HasDefault for Never)
            // 4: EditorBrowsableState (VALUETYPE, data.dummy = 0 -> typeDefs[0], parameter type)
            // 5: BrokenEnum (VALUETYPE, data.dummy = 1 -> typeDefs[1], parameter type)
            // 6: EditorBrowsableAttribute (CLASS, data.dummy = 2 -> typeDefs[2])
            // 7: DebuggerNonUserCodeAttribute (CLASS, data.dummy = 3 -> typeDefs[3])
            // 8: GeneratedCodeAttribute (CLASS, data.dummy = 4 -> typeDefs[4])
            // 9: BrokenEnumAttribute (CLASS, data.dummy = 5 -> typeDefs[5])
            // 10: ProtocolReflection (CLASS, data.dummy = 6 -> typeDefs[6])
            // 11: SampleMessage (CLASS, data.dummy = 7 -> typeDefs[7])
            var enumStateTypeDef = new Il2CppTypeDefinition
            {
                nameIndex = WriteStr("EditorBrowsableState"),
                namespaceIndex = WriteStr("System.ComponentModel"),
                byvalTypeIndex = 4,
                declaringTypeIndex = -1,
                parentIndex = 1, // Points to System.Int32 in IL2CPP metadata!
                genericContainerIndex = -1,
                flags = (uint)(Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Sealed),
                bitfield = 3, // ValueType + Enum
                fieldStart = 0,
                field_count = 2,
                token = 0x02000001
            };

            var brokenEnumTypeDef = new Il2CppTypeDefinition
            {
                nameIndex = WriteStr("BrokenEnum"),
                namespaceIndex = WriteStr("TestNamespace"),
                byvalTypeIndex = 5,
                declaringTypeIndex = -1,
                parentIndex = 1,
                genericContainerIndex = -1,
                flags = (uint)(Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Sealed),
                bitfield = 3, // ValueType + Enum, but 0 fields (no value__)
                fieldStart = 0,
                field_count = 0,
                token = 0x02000002
            };

            var editorAttrTypeDef = new Il2CppTypeDefinition
            {
                nameIndex = WriteStr("EditorBrowsableAttribute"),
                namespaceIndex = WriteStr("System.ComponentModel"),
                byvalTypeIndex = 6,
                declaringTypeIndex = -1,
                parentIndex = -1,
                genericContainerIndex = -1,
                flags = (uint)Mono.Cecil.TypeAttributes.Public,
                methodStart = 0,
                method_count = 1,
                token = 0x02000003
            };

            var dbgAttrTypeDef = new Il2CppTypeDefinition
            {
                nameIndex = WriteStr("DebuggerNonUserCodeAttribute"),
                namespaceIndex = WriteStr("System.Diagnostics"),
                byvalTypeIndex = 7,
                declaringTypeIndex = -1,
                parentIndex = -1,
                genericContainerIndex = -1,
                flags = (uint)Mono.Cecil.TypeAttributes.Public,
                methodStart = 1,
                method_count = 1,
                token = 0x02000004
            };

            var genAttrTypeDef = new Il2CppTypeDefinition
            {
                nameIndex = WriteStr("GeneratedCodeAttribute"),
                namespaceIndex = WriteStr("System.CodeDom.Compiler"),
                byvalTypeIndex = 8,
                declaringTypeIndex = -1,
                parentIndex = -1,
                genericContainerIndex = -1,
                flags = (uint)Mono.Cecil.TypeAttributes.Public,
                methodStart = 2,
                method_count = 1,
                token = 0x02000005
            };

            var brokenAttrTypeDef = new Il2CppTypeDefinition
            {
                nameIndex = WriteStr("BrokenEnumAttribute"),
                namespaceIndex = WriteStr("TestNamespace"),
                byvalTypeIndex = 9,
                declaringTypeIndex = -1,
                parentIndex = -1,
                genericContainerIndex = -1,
                flags = (uint)Mono.Cecil.TypeAttributes.Public,
                methodStart = 3,
                method_count = 1,
                token = 0x02000006
            };

            var reflectionTypeDef = new Il2CppTypeDefinition
            {
                nameIndex = WriteStr("ProtocolReflection"),
                namespaceIndex = WriteStr("TestNamespace"),
                byvalTypeIndex = 10,
                declaringTypeIndex = -1,
                parentIndex = -1,
                genericContainerIndex = -1,
                flags = (uint)Mono.Cecil.TypeAttributes.Public,
                customAttributeIndex = 0,
                token = 0x02000007
            };

            var messageTypeDef = new Il2CppTypeDefinition
            {
                nameIndex = WriteStr("SampleMessage"),
                namespaceIndex = WriteStr("TestNamespace"),
                byvalTypeIndex = 11,
                declaringTypeIndex = -1,
                parentIndex = -1,
                genericContainerIndex = -1,
                flags = (uint)Mono.Cecil.TypeAttributes.Public,
                methodStart = 4,
                method_count = 2,
                propertyStart = 0,
                property_count = includeBrokenEnumAttribute ? (ushort)2 : (ushort)1,
                token = 0x02000008
            };

            m.typeDefs = new[]
            {
                enumStateTypeDef,
                brokenEnumTypeDef,
                editorAttrTypeDef,
                dbgAttrTypeDef,
                genAttrTypeDef,
                brokenAttrTypeDef,
                reflectionTypeDef,
                messageTypeDef
            };

            m.fieldDefs = new[]
            {
                new Il2CppFieldDefinition
                {
                    nameIndex = WriteStr("value__"),
                    typeIndex = 1,
                    token = 0x04000001
                },
                new Il2CppFieldDefinition
                {
                    nameIndex = WriteStr("Never"),
                    typeIndex = 3,
                    token = 0x04000002
                }
            };

            var fieldDefaultValuesDic = new Dictionary<int, Il2CppFieldDefaultValue>
            {
                [1] = new Il2CppFieldDefaultValue
                {
                    fieldIndex = 1,
                    typeIndex = 1,
                    dataIndex = 0
                }
            };
            typeof(Metadata).GetField("fieldDefaultValuesDic", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(m, fieldDefaultValuesDic);

            m.parameterDefs = new[]
            {
                new Il2CppParameterDefinition { nameIndex = WriteStr("state"), typeIndex = 4, token = 0x08000001 },
                new Il2CppParameterDefinition { nameIndex = WriteStr("tool"), typeIndex = 2, token = 0x08000002 },
                new Il2CppParameterDefinition { nameIndex = WriteStr("version"), typeIndex = 2, token = 0x08000003 },
                new Il2CppParameterDefinition { nameIndex = WriteStr("mode"), typeIndex = 5, token = 0x08000004 }
            };

            var ctorFlags = (ushort)(Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.HideBySig | Mono.Cecil.MethodAttributes.SpecialName | Mono.Cecil.MethodAttributes.RTSpecialName);
            var getterFlags = (ushort)(Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.HideBySig | Mono.Cecil.MethodAttributes.SpecialName);

            m.methodDefs = new[]
            {
                // 0: EditorBrowsableAttribute..ctor(EditorBrowsableState)
                new Il2CppMethodDefinition { nameIndex = WriteStr(".ctor"), declaringType = 2, returnType = 0, parameterStart = 0, parameterCount = 1, genericContainerIndex = -1, flags = ctorFlags, token = 0x06000001 },
                // 1: DebuggerNonUserCodeAttribute..ctor()
                new Il2CppMethodDefinition { nameIndex = WriteStr(".ctor"), declaringType = 3, returnType = 0, parameterStart = 0, parameterCount = 0, genericContainerIndex = -1, flags = ctorFlags, token = 0x06000002 },
                // 2: GeneratedCodeAttribute..ctor(string, string)
                new Il2CppMethodDefinition { nameIndex = WriteStr(".ctor"), declaringType = 4, returnType = 0, parameterStart = 1, parameterCount = 2, genericContainerIndex = -1, flags = ctorFlags, token = 0x06000003 },
                // 3: BrokenEnumAttribute..ctor(BrokenEnum)
                new Il2CppMethodDefinition { nameIndex = WriteStr(".ctor"), declaringType = 5, returnType = 0, parameterStart = 3, parameterCount = 1, genericContainerIndex = -1, flags = ctorFlags, token = 0x06000004 },
                // 4: SampleMessage.get_Payload()
                new Il2CppMethodDefinition { nameIndex = WriteStr("get_Payload"), declaringType = 7, returnType = 1, parameterStart = 0, parameterCount = 0, genericContainerIndex = -1, flags = getterFlags, token = 0x06000005 },
                // 5: SampleMessage.get_BrokenProperty()
                new Il2CppMethodDefinition { nameIndex = WriteStr("get_BrokenProperty"), declaringType = 7, returnType = 1, parameterStart = 0, parameterCount = 0, genericContainerIndex = -1, flags = getterFlags, token = 0x06000006 }
            };

            m.propertyDefs = includeBrokenEnumAttribute
                ? new[]
                {
                    new Il2CppPropertyDefinition { nameIndex = WriteStr("Payload"), get = 0, set = -1, customAttributeIndex = 1, token = 0x17000001 },
                    new Il2CppPropertyDefinition { nameIndex = WriteStr("BrokenProperty"), get = 1, set = -1, customAttributeIndex = 2, token = 0x17000002 }
                }
                : new[]
                {
                    new Il2CppPropertyDefinition { nameIndex = WriteStr("Payload"), get = 0, set = -1, customAttributeIndex = 1, token = 0x17000001 }
                };

            m.eventDefs = Array.Empty<Il2CppEventDefinition>();
            m.interfaceIndices = Array.Empty<int>();
            m.nestedTypeIndices = Array.Empty<int>();

            // Build attribute blobs:
            // Blob 0 (token 0x02000007, ProtocolReflection): [EditorBrowsable(EditorBrowsableState.Never)]
            var blob0 = BuildEditorBrowsableBlob(ctorMethodIndex: 0, enumTypeIndex: 4, enumValue: 1);
            // Blob 1 (token 0x17000001, SampleMessage.Payload): [DebuggerNonUserCode], [GeneratedCode("protoc", "3.21.0")]
            var blob1 = BuildProtobufPropertyMarkersBlob(dbgCtorIndex: 1, genCtorIndex: 2, tool: "protoc", version: "3.21.0");
            // Blob 2 (token 0x17000002, SampleMessage.BrokenProperty): [BrokenEnum(BrokenEnum.1)]
            var blob2 = BuildEditorBrowsableBlob(ctorMethodIndex: 3, enumTypeIndex: 5, enumValue: 1);

            m.Position = (ulong)m.header.attributeDataOffset;
            m.Writer.Write(blob0);
            var offset1 = (uint)blob0.Length;
            m.Writer.Write(blob1);
            var offset2 = offset1 + (uint)blob1.Length;
            m.Writer.Write(blob2);
            var offset3 = offset2 + (uint)blob2.Length;

            m.attributeDataRanges = new[]
            {
                new Il2CppCustomAttributeDataRange { token = 0x02000007, startOffset = 0 },
                new Il2CppCustomAttributeDataRange { token = 0x17000001, startOffset = offset1 },
                new Il2CppCustomAttributeDataRange { token = 0x17000002, startOffset = offset2 },
                new Il2CppCustomAttributeDataRange { token = 0, startOffset = offset3 }
            };

            var imageDef = new Il2CppImageDefinition
            {
                nameIndex = WriteStr("TestProtocol.dll"),
                assemblyIndex = 0,
                typeStart = 0,
                typeCount = (uint)m.typeDefs.Length,
                customAttributeStart = 0,
                customAttributeCount = includeBrokenEnumAttribute ? 3u : 2u
            };
            m.imageDefs = new[] { imageDef };
            m.assemblyDefs = new[]
            {
                new Il2CppAssemblyDefinition
                {
                    aname = new Il2CppAssemblyNameDefinition
                    {
                        nameIndex = WriteStr("TestProtocol"),
                        major = 1,
                        minor = 0,
                        build = 0,
                        revision = 0
                    }
                }
            };

            var attrDic = new Dictionary<Il2CppImageDefinition, Dictionary<uint, int>>
            {
                [imageDef] = new Dictionary<uint, int>
                {
                    [0x02000007] = 0,
                    [0x17000001] = 1,
                    [0x17000002] = 2
                }
            };
            typeof(Metadata).GetField("attributeTypeRangesDic", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(m, attrDic);

            var literalFieldAttrs = (ushort)(FIELD_ATTRIBUTE_PUBLIC | FIELD_ATTRIBUTE_STATIC | FIELD_ATTRIBUTE_LITERAL | 0x8000);
            var instanceFieldAttrs = (ushort)(FIELD_ATTRIBUTE_PUBLIC | 0x0200 | 0x0400);

            var il = new WebAssemblyMemory(new MemoryStream(new byte[256]), 0)
            {
                Version = 29,
                codeGenModuleMethodPointers = new Dictionary<string, ulong[]>(StringComparer.OrdinalIgnoreCase)
                {
                    ["TestProtocol.dll"] = new ulong[m.methodDefs.Length]
                },
                types = new[]
                {
                    new Il2CppType { type = Il2CppTypeEnum.IL2CPP_TYPE_VOID },
                    new Il2CppType { type = Il2CppTypeEnum.IL2CPP_TYPE_I4, attrs = instanceFieldAttrs },
                    new Il2CppType { type = Il2CppTypeEnum.IL2CPP_TYPE_STRING },
                    new Il2CppType { type = Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE, attrs = literalFieldAttrs, data = new Il2CppType.Union { dummy = 0 } },
                    new Il2CppType { type = Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE, data = new Il2CppType.Union { dummy = 0 } },
                    new Il2CppType { type = Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE, data = new Il2CppType.Union { dummy = 1 } },
                    new Il2CppType { type = Il2CppTypeEnum.IL2CPP_TYPE_CLASS, data = new Il2CppType.Union { dummy = 2 } },
                    new Il2CppType { type = Il2CppTypeEnum.IL2CPP_TYPE_CLASS, data = new Il2CppType.Union { dummy = 3 } },
                    new Il2CppType { type = Il2CppTypeEnum.IL2CPP_TYPE_CLASS, data = new Il2CppType.Union { dummy = 4 } },
                    new Il2CppType { type = Il2CppTypeEnum.IL2CPP_TYPE_CLASS, data = new Il2CppType.Union { dummy = 5 } },
                    new Il2CppType { type = Il2CppTypeEnum.IL2CPP_TYPE_CLASS, data = new Il2CppType.Union { dummy = 6 } },
                    new Il2CppType { type = Il2CppTypeEnum.IL2CPP_TYPE_CLASS, data = new Il2CppType.Union { dummy = 7 } }
                }
            };

            return (m, il);
        }

        private static byte[] BuildEditorBrowsableBlob(int ctorMethodIndex, int enumTypeIndex, int enumValue)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write((byte)1); // Count = 1
            bw.Write(ctorMethodIndex);
            bw.Write((byte)1); // 1 constructor arg
            bw.Write((byte)0); // 0 fields
            bw.Write((byte)0); // 0 properties
            bw.Write((byte)Il2CppTypeEnum.IL2CPP_TYPE_ENUM);
            bw.Write((byte)(enumTypeIndex << 1)); // compressed int32 for enumTypeIndex
            bw.Write((byte)(enumValue << 1)); // compressed int32 for enumValue
            return ms.ToArray();
        }

        private static byte[] BuildProtobufPropertyMarkersBlob(int dbgCtorIndex, int genCtorIndex, string tool, string version)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write((byte)2); // Count = 2
            bw.Write(dbgCtorIndex);
            bw.Write(genCtorIndex);

            // Attr 0: DebuggerNonUserCodeAttribute()
            bw.Write((byte)0);
            bw.Write((byte)0);
            bw.Write((byte)0);

            // Attr 1: GeneratedCodeAttribute(string tool, string version)
            bw.Write((byte)2);
            bw.Write((byte)0);
            bw.Write((byte)0);

            WriteStringBlobValue(bw, tool);
            WriteStringBlobValue(bw, version);
            return ms.ToArray();
        }

        private static void WriteStringBlobValue(BinaryWriter bw, string value)
        {
            bw.Write((byte)Il2CppTypeEnum.IL2CPP_TYPE_STRING);
            var utf8 = Encoding.UTF8.GetBytes(value);
            bw.Write((byte)(utf8.Length << 1)); // compressed int32 length
            bw.Write(utf8);
        }
    }
}
