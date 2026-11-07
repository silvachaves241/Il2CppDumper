#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using static Il2CppDumper.Il2CppConstants;

namespace Il2CppDumper
{
    public class OneofContainer
    {
        [JsonPropertyName("group")]
        public OneofGroup Group { get; set; } = new();

        [JsonPropertyName("members")]
        public List<OneofMember> Members { get; set; } = new();
    }

    public class OneofGroup
    {
        [JsonPropertyName("property")]
        public string Property { get; set; } = string.Empty;

        [JsonPropertyName("offset")]
        public string Offset { get; set; } = string.Empty;
    }

    public class OneofMember
    {
        [JsonPropertyName("tag")]
        public int Tag { get; set; }

        [JsonPropertyName("constantFields")]
        public List<string> ConstantFields { get; set; } = new();
    }

    public class OneofAmbiguity
    {
        [JsonPropertyName("className")]
        public string ClassName { get; set; } = string.Empty;

        [JsonPropertyName("typeDefIndex")]
        public int TypeDefIndex { get; set; }

        [JsonPropertyName("reason")]
        public string Reason { get; set; } = string.Empty;
    }

    public class OneofExtractionResult
    {
        public SortedDictionary<string, OneofContainer> Classes { get; set; } = new(StringComparer.Ordinal);
        public List<OneofAmbiguity> Ambiguities { get; set; } = new();
        public int IMessageClassesScanned { get; set; }
        public int OneofTagsCount => Classes.Values.Sum(c => c.Members.Count);
    }

    public class OneofMemberExtractor
    {
        public const string FileName = "oneof-members.json";
        public const string AmbiguityFileName = "oneof-ambiguities.json";

        public static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true
        };

        private readonly Il2CppExecutor executor;
        private readonly Metadata metadata;
        private readonly Il2Cpp il2Cpp;

        public List<string> WrittenFiles { get; } = new();
        public OneofExtractionResult Result { get; private set; } = new();

        public int OneofTagsCount => Result.OneofTagsCount;
        public int AmbiguitiesCount => Result.Ambiguities.Count;
        public int ClassesCount => Result.Classes.Count;

        public OneofMemberExtractor(Il2CppExecutor executor)
        {
            this.executor = executor;
            this.metadata = executor.metadata;
            this.il2Cpp = executor.il2Cpp;
        }

        public OneofExtractionResult Extract(Config config, bool onlyProtobufMessages = true)
        {
            var result = new OneofExtractionResult();

            if (metadata.typeDefs == null)
            {
                Result = result;
                return result;
            }

            for (int typeDefIndex = 0; typeDefIndex < metadata.typeDefs.Length; typeDefIndex++)
            {
                var typeDef = metadata.typeDefs[typeDefIndex];
                if (typeDef.IsEnum || typeDef.IsValueType)
                    continue;

                if (onlyProtobufMessages && !IsProtobufMessage(typeDef))
                    continue;

                result.IMessageClassesScanned++;

                var (container, classAmbiguities) = AnalyzeType(typeDef, typeDefIndex, config);

                if (classAmbiguities != null && classAmbiguities.Count > 0)
                {
                    result.Ambiguities.AddRange(classAmbiguities);
                }
                else if (container != null)
                {
                    var className = executor.GetTypeDefName(typeDef, true, true);
                    result.Classes[className] = container;
                }
            }

            Result = result;
            return result;
        }

        public (OneofContainer? Container, List<OneofAmbiguity> Ambiguities) AnalyzeType(
            Il2CppTypeDefinition typeDef,
            int typeDefIndex,
            Config config)
        {
            var ambiguities = new List<OneofAmbiguity>();
            var className = executor.GetTypeDefName(typeDef, true, true);

            if (typeDef.field_count == 0 || metadata.fieldDefs == null)
            {
                return (null, ambiguities);
            }

            var pendingConsts = new List<(string Name, int Tag)>();
            var objectFields = new List<(string Name, ulong Offset)>();
            var oneofGroups = new List<(string Property, string Offset, List<(string Name, int Tag)> Tags)>();

            var fieldEnd = typeDef.fieldStart + typeDef.field_count;
            for (int i = typeDef.fieldStart; i < fieldEnd && i < metadata.fieldDefs.Length; i++)
            {
                var fieldDef = metadata.fieldDefs[i];
                if (fieldDef.typeIndex < 0 || fieldDef.typeIndex >= il2Cpp.types.Length)
                    continue;

                var fieldType = il2Cpp.types[fieldDef.typeIndex];
                var fieldName = metadata.GetStringFromIndex(fieldDef.nameIndex);
                var isConst = (fieldType.attrs & FIELD_ATTRIBUTE_LITERAL) != 0;
                var isStatic = (fieldType.attrs & FIELD_ATTRIBUTE_STATIC) != 0;

                if (isConst)
                {
                    if (metadata.GetFieldDefaultValueFromIndex(i, out var fieldDefaultValue) && fieldDefaultValue.dataIndex != -1)
                    {
                        if (executor.TryGetDefaultValue(fieldDefaultValue.typeIndex, fieldDefaultValue.dataIndex, out var val, typeDefIndex, metadata.GetStringFromIndex(typeDef.nameIndex), fieldName))
                        {
                            if (val != null)
                            {
                                try
                                {
                                    var tag = Convert.ToInt32(val);
                                    if (tag > 0)
                                    {
                                        pendingConsts.Add((fieldName, tag));
                                    }
                                }
                                catch
                                {
                                    // non-integral const is not a protobuf tag
                                }
                            }
                        }
                    }
                    continue;
                }

                if (isStatic)
                {
                    // Static non-const fields (e.g. _parser, _descriptor) do not consume field number wire slots
                    continue;
                }

                // Instance field
                var fieldTypeName = executor.GetTypeName(fieldType, false, false);
                var isObject = fieldType.type == Il2CppTypeEnum.IL2CPP_TYPE_OBJECT
                            || fieldTypeName == "object"
                            || fieldTypeName == "System.Object";

                var fieldOffset = il2Cpp.GetFieldOffsetFromIndex(typeDefIndex, i - typeDef.fieldStart, i, typeDef.IsValueType, isStatic);

                if (isObject)
                {
                    objectFields.Add((fieldName, (ulong)fieldOffset));
                    if (pendingConsts.Count > 0)
                    {
                        oneofGroups.Add((fieldName, $"0x{fieldOffset:X}", new List<(string Name, int Tag)>(pendingConsts)));
                        pendingConsts.Clear();
                    }
                    else
                    {
                        ambiguities.Add(new OneofAmbiguity
                        {
                            ClassName = className,
                            TypeDefIndex = typeDefIndex,
                            Reason = $"object field '{fieldName}' without preceding const field numbers"
                        });
                    }
                }
                else
                {
                    if (pendingConsts.Count == 1)
                    {
                        // Clean single field matching its non-object instance storage
                        pendingConsts.Clear();
                    }
                    else if (pendingConsts.Count > 1)
                    {
                        ambiguities.Add(new OneofAmbiguity
                        {
                            ClassName = className,
                            TypeDefIndex = typeDefIndex,
                            Reason = $"const run of {pendingConsts.Count} tags ({string.Join(", ", pendingConsts.Select(p => p.Name))}) followed by non-object field '{fieldName}' of type '{fieldTypeName}'"
                        });
                        pendingConsts.Clear();
                    }
                    // pendingConsts.Count == 0: helper field without field number (e.g. discriminator case enum or _unknownFields); clean.
                }
            }

            if (pendingConsts.Count > 0)
            {
                ambiguities.Add(new OneofAmbiguity
                {
                    ClassName = className,
                    TypeDefIndex = typeDefIndex,
                    Reason = $"trailing consts ({string.Join(", ", pendingConsts.Select(p => p.Name))}) without following field"
                });
            }

            if (objectFields.Count > 1)
            {
                ambiguities.Add(new OneofAmbiguity
                {
                    ClassName = className,
                    TypeDefIndex = typeDefIndex,
                    Reason = $"multiple object fields ({string.Join(", ", objectFields.Select(o => o.Name))})"
                });
            }

            if (ambiguities.Count > 0)
            {
                return (null, ambiguities);
            }

            if (oneofGroups.Count == 1)
            {
                var groupInfo = oneofGroups[0];
                var group = new OneofGroup
                {
                    Property = groupInfo.Property,
                    Offset = groupInfo.Offset
                };

                var membersByTag = new Dictionary<int, List<string>>();
                foreach (var (name, tag) in groupInfo.Tags)
                {
                    if (!membersByTag.TryGetValue(tag, out var constList))
                    {
                        constList = new List<string>();
                        membersByTag[tag] = constList;
                    }
                    if (!constList.Contains(name))
                    {
                        constList.Add(name);
                    }
                }

                var members = membersByTag
                    .OrderBy(kv => kv.Key)
                    .Select(kv => new OneofMember
                    {
                        Tag = kv.Key,
                        ConstantFields = kv.Value
                    })
                    .ToList();

                var container = new OneofContainer
                {
                    Group = group,
                    Members = members
                };

                return (container, ambiguities);
            }

            return (null, ambiguities);
        }

        public void Write(string outputDir, Config config)
        {
            if (Result.Classes.Count == 0 && Result.Ambiguities.Count == 0 && Result.IMessageClassesScanned == 0)
            {
                Extract(config);
            }

            var membersJson = JsonSerializer.Serialize(Result.Classes, JsonOptions);
            var membersPath = Path.Combine(outputDir, FileName);
            File.WriteAllText(membersPath, membersJson, new UTF8Encoding(false));
            WrittenFiles.Add(membersPath);

            var ambiguitiesJson = JsonSerializer.Serialize(Result.Ambiguities, JsonOptions);
            var ambiguitiesPath = Path.Combine(outputDir, AmbiguityFileName);
            File.WriteAllText(ambiguitiesPath, ambiguitiesJson, new UTF8Encoding(false));
            WrittenFiles.Add(ambiguitiesPath);
        }

        public bool IsProtobufMessage(Il2CppTypeDefinition typeDef)
        {
            if (HasIMessageInterface(typeDef))
                return true;

            var currentTypeDef = typeDef;
            while (currentTypeDef.parentIndex >= 0 && currentTypeDef.parentIndex < il2Cpp.types.Length)
            {
                var parentType = il2Cpp.types[currentTypeDef.parentIndex];
                if (parentType.type == Il2CppTypeEnum.IL2CPP_TYPE_CLASS)
                {
                    var parentDef = executor.GetTypeDefinitionFromIl2CppType(parentType);
                    if (parentDef == null || parentDef == currentTypeDef)
                        break;
                    if (HasIMessageInterface(parentDef))
                        return true;
                    currentTypeDef = parentDef;
                }
                else if (parentType.type == Il2CppTypeEnum.IL2CPP_TYPE_GENERICINST)
                {
                    var genericClass = il2Cpp.MapVATR<Il2CppGenericClass>(parentType.data.generic_class);
                    var parentDef = executor.GetGenericClassTypeDefinition(genericClass);
                    if (parentDef == null || parentDef == currentTypeDef)
                        break;
                    if (HasIMessageInterface(parentDef))
                        return true;
                    currentTypeDef = parentDef;
                }
                else
                {
                    break;
                }
            }

            return false;
        }

        private bool HasIMessageInterface(Il2CppTypeDefinition typeDef)
        {
            if (typeDef.interfaces_count <= 0 || metadata.interfaceIndices == null)
                return false;

            for (int i = 0; i < typeDef.interfaces_count; i++)
            {
                var interfaceIndex = typeDef.interfacesStart + i;
                if (interfaceIndex >= 0 && interfaceIndex < metadata.interfaceIndices.Length)
                {
                    var typeIdx = metadata.interfaceIndices[interfaceIndex];
                    if (typeIdx >= 0 && typeIdx < il2Cpp.types.Length)
                    {
                        var ifaceType = il2Cpp.types[typeIdx];
                        try
                        {
                            var ifaceName = executor.GetTypeName(ifaceType, false, false);
                            if (IsIMessageInterfaceName(ifaceName))
                                return true;
                            var ifaceFullName = executor.GetTypeName(ifaceType, true, false);
                            if (IsIMessageInterfaceName(ifaceFullName))
                                return true;
                        }
                        catch
                        {
                            // Ignore unresolvable interface type
                        }
                    }
                }
            }
            return false;
        }

        private static bool IsIMessageInterfaceName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;
            return name == "IMessage"
                || name.StartsWith("IMessage<", StringComparison.Ordinal)
                || name.EndsWith(".IMessage", StringComparison.Ordinal)
                || name.Contains(".IMessage<", StringComparison.Ordinal)
                || name.Contains("Google.Protobuf.IMessage", StringComparison.OrdinalIgnoreCase);
        }
    }
}
