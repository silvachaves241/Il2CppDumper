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

            bool matchedOrdinaryField = false;
            (int Field, List<(string Name, int Tag)> Tags, OneofAmbiguity Warning)? interruptedRun = null;
            var fieldEnd = typeDef.fieldStart + typeDef.field_count;
            for (int i = typeDef.fieldStart; i < fieldEnd && i < metadata.fieldDefs.Length; i++)
            {
                var fieldDef = metadata.fieldDefs[i];
                if (fieldDef.typeIndex < 0 || fieldDef.typeIndex >= il2Cpp.types.Length)
                    continue;

                var fieldType = il2Cpp.types[fieldDef.typeIndex];
                var fieldName = metadata.GetStringFromIndex(fieldDef.nameIndex);
                var isLiteral = (fieldType.attrs & FIELD_ATTRIBUTE_LITERAL) != 0;
                var isStatic = (fieldType.attrs & FIELD_ATTRIBUTE_STATIC) != 0;

                if (isLiteral)
                {
                    var isPublic = (fieldType.attrs & FIELD_ATTRIBUTE_FIELD_ACCESS_MASK) == FIELD_ATTRIBUTE_PUBLIC;
                    var isInt32 = fieldType.type == Il2CppTypeEnum.IL2CPP_TYPE_I4;

                    if (!isPublic || !isInt32)
                    {
                        // Auxiliary constants (e.g. private const, non-Int32 literals) do not participate in protobuf tag mappings
                        continue;
                    }

                    // Candidate public const int protobuf tag: must resolve a positive Int32 default value
                    if (!metadata.GetFieldDefaultValueFromIndex(i, out var fieldDefaultValue) || fieldDefaultValue.dataIndex == -1)
                    {
                        ambiguities.Add(new OneofAmbiguity
                        {
                            ClassName = className,
                            TypeDefIndex = typeDefIndex,
                            Reason = $"missing default value for public const int field '{fieldName}'"
                        });
                        continue;
                    }

                    if (!executor.TryGetDefaultValue(fieldDefaultValue.typeIndex, fieldDefaultValue.dataIndex, out var val, typeDefIndex, metadata.GetStringFromIndex(typeDef.nameIndex), fieldName) || val == null)
                    {
                        ambiguities.Add(new OneofAmbiguity
                        {
                            ClassName = className,
                            TypeDefIndex = typeDefIndex,
                            Reason = $"failed to decode constant default value for public const int field '{fieldName}'"
                        });
                        continue;
                    }

                    if (val is int tag && tag > 0)
                    {
                        pendingConsts.Add((fieldName, tag));
                    }
                    else
                    {
                        ambiguities.Add(new OneofAmbiguity
                        {
                            ClassName = className,
                            TypeDefIndex = typeDefIndex,
                            Reason = $"public const int field '{fieldName}' evaluated to invalid tag value '{val}'"
                        });
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
                    else if (interruptedRun is { } run && run.Field == i - 1 &&
                        TryResolveInterleavedCases(typeDef, typeDefIndex, i, fieldOffset, run.Tags, out var caseTags))
                    {
                        oneofGroups.Add((fieldName, $"0x{fieldOffset:X}", caseTags));
                        ambiguities.Remove(run.Warning);
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
                        matchedOrdinaryField = true;
                        pendingConsts.Clear();
                    }
                    else if (pendingConsts.Count > 1)
                    {
                        var warning = new OneofAmbiguity
                        {
                            ClassName = className,
                            TypeDefIndex = typeDefIndex,
                            Reason = $"const run of {pendingConsts.Count} tags ({string.Join(", ", pendingConsts.Select(p => p.Name))}) followed by non-object field '{fieldName}' of type '{fieldTypeName}'"
                        };
                        ambiguities.Add(warning);
                        interruptedRun = (i, new List<(string Name, int Tag)>(pendingConsts), warning);
                        pendingConsts.Clear();
                    }
                    // pendingConsts.Count == 0: helper field without field number (e.g. discriminator case enum or _unknownFields); clean.
                }
            }

            // Constants after clean ordinary fields cannot describe object storage that is absent.
            if (pendingConsts.Count > 0 && !(objectFields.Count == 0 && matchedOrdinaryField && ambiguities.Count == 0))
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

        private bool TryResolveInterleavedCases(
            Il2CppTypeDefinition owner, int ownerIndex, int objectIndex, int objectOffset,
            List<(string Name, int Tag)> candidates, out List<(string Name, int Tag)> members)
        {
            members = new();
            // Only recognize ordinary field / object slot / case enum in consecutive instance fields.
            var discriminatorIndex = objectIndex + 1;
            if (objectOffset <= 0 || discriminatorIndex >= (long)owner.fieldStart + owner.field_count ||
                discriminatorIndex >= metadata.fieldDefs.Length)
            {
                return false;
            }

            var typeIndex = metadata.fieldDefs[discriminatorIndex].typeIndex;
            if ((uint)typeIndex >= (uint)il2Cpp.types.Length)
            {
                return false;
            }
            var discriminator = il2Cpp.types[typeIndex];
            if (discriminator.type != Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE ||
                (discriminator.attrs & (FIELD_ATTRIBUTE_STATIC | FIELD_ATTRIBUTE_LITERAL)) != 0 ||
                il2Cpp.GetFieldOffsetFromIndex(ownerIndex, discriminatorIndex - owner.fieldStart,
                    discriminatorIndex, false, false) != objectOffset + (long)il2Cpp.PointerSize)
            {
                return false;
            }

            try
            {
                var caseEnum = executor.GetTypeDefinitionFromIl2CppType(discriminator);
                if (!caseEnum.IsEnum || !IsNestedCaseEnum(owner, caseEnum) ||
                    !HasReadOnlyCaseProperty(owner, ownerIndex, caseEnum))
                {
                    return false;
                }

                // Generated case enums use one zero sentinel and positive, distinct wire field numbers.
                int zeroCases = 0;
                var tags = new HashSet<int>();
                bool hasInt32Storage = false;
                for (int i = 0; i < caseEnum.field_count; i++)
                {
                    var fieldIndex = checked(caseEnum.fieldStart + i);
                    var fieldType = il2Cpp.types[metadata.fieldDefs[fieldIndex].typeIndex];
                    if ((fieldType.attrs & FIELD_ATTRIBUTE_LITERAL) == 0)
                    {
                        if (hasInt32Storage || (fieldType.attrs & FIELD_ATTRIBUTE_STATIC) != 0 ||
                            fieldType.type != Il2CppTypeEnum.IL2CPP_TYPE_I4)
                        {
                            return false;
                        }
                        hasInt32Storage = true;
                        continue;
                    }
                    if (!metadata.GetFieldDefaultValueFromIndex(fieldIndex, out var entry) || entry.dataIndex < 0 ||
                        !executor.TryGetDefaultValue(entry.typeIndex, entry.dataIndex, out var value) ||
                        value is not int tag || tag < 0)
                    {
                        return false;
                    }
                    if (tag == 0)
                    {
                        zeroCases++;
                    }
                    else if (!tags.Add(tag))
                    {
                        return false;
                    }
                }

                // Exactly one constant belongs to the ordinary field, and every case maps unambiguously.
                if (!hasInt32Storage || zeroCases != 1 || tags.Count == 0 || tags.Count != candidates.Count - 1 ||
                    candidates.Select(c => c.Tag).Distinct().Count() != candidates.Count ||
                    !tags.IsSubsetOf(candidates.Select(c => c.Tag)))
                {
                    return false;
                }

                members = candidates.Where(c => tags.Contains(c.Tag)).ToList();
                return true;
            }
            catch (Exception e) when (e is IndexOutOfRangeException or ArgumentOutOfRangeException or EndOfStreamException or OverflowException)
            {
                return false;
            }
        }

        private bool IsNestedCaseEnum(Il2CppTypeDefinition owner, Il2CppTypeDefinition caseEnum)
        {
            for (int i = 0; i < owner.nested_type_count; i++)
            {
                int childIndex = metadata.nestedTypeIndices[owner.nestedTypesStart + i];
                if (ReferenceEquals(metadata.typeDefs[childIndex], caseEnum))
                {
                    return true;
                }
            }
            return false;
        }

        private bool HasReadOnlyCaseProperty(Il2CppTypeDefinition owner, int ownerIndex, Il2CppTypeDefinition caseEnum)
        {
            for (int i = 0; i < owner.property_count; i++)
            {
                var property = metadata.propertyDefs[owner.propertyStart + i];
                if (property.set >= 0 || (uint)property.get >= owner.method_count)
                {
                    continue;
                }
                var getter = metadata.methodDefs[metadata.GetMethodIndexFromTypeDefinition(ownerIndex, property.get)];
                var returnType = il2Cpp.types[getter.returnType];
                if ((getter.flags & METHOD_ATTRIBUTE_STATIC) == 0 && getter.parameterCount == 0 &&
                    returnType.type == Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE &&
                    ReferenceEquals(executor.GetTypeDefinitionFromIl2CppType(returnType), caseEnum))
                {
                    return true;
                }
            }
            return false;
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

        private bool HasIMessageInterface(Il2CppTypeDefinition typeDef, HashSet<Il2CppTypeDefinition>? visited = null)
        {
            if (typeDef.interfaces_count <= 0 || metadata.interfaceIndices == null)
                return false;

            visited ??= new HashSet<Il2CppTypeDefinition>();
            if (!visited.Add(typeDef))
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
                            Il2CppTypeDefinition? ifaceDef = null;
                            if (ifaceType.type == Il2CppTypeEnum.IL2CPP_TYPE_CLASS || ifaceType.type == Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE)
                            {
                                ifaceDef = executor.GetTypeDefinitionFromIl2CppType(ifaceType);
                            }
                            else if (ifaceType.type == Il2CppTypeEnum.IL2CPP_TYPE_GENERICINST)
                            {
                                var genericClass = il2Cpp.MapVATR<Il2CppGenericClass>(ifaceType.data.generic_class);
                                ifaceDef = executor.GetGenericClassTypeDefinition(genericClass);
                            }

                            if (ifaceDef != null)
                            {
                                var ifaceNs = metadata.GetStringFromIndex(ifaceDef.namespaceIndex);
                                var ifaceName = metadata.GetStringFromIndex(ifaceDef.nameIndex);

                                if (IsProtobufIMessageIdentity(ifaceNs, ifaceName))
                                    return true;

                                if (HasIMessageInterface(ifaceDef, visited))
                                    return true;
                            }
                            else
                            {
                                var fullName = executor.GetTypeName(ifaceType, true, false);
                                if (IsProtobufIMessageFullName(fullName))
                                    return true;
                            }
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

        private static bool IsProtobufIMessageIdentity(string? ns, string? name)
        {
            if (string.IsNullOrEmpty(name))
                return false;

            if (ns == "Google.Protobuf")
            {
                return name == "IMessage" || name == "IMessage`1";
            }

            if (string.IsNullOrEmpty(ns))
            {
                return name == "Google.Protobuf.IMessage"
                    || name.StartsWith("Google.Protobuf.IMessage<", StringComparison.Ordinal)
                    || name.StartsWith("Google.Protobuf.IMessage`1", StringComparison.Ordinal);
            }

            return false;
        }

        private static bool IsProtobufIMessageFullName(string? fullName)
        {
            if (string.IsNullOrEmpty(fullName))
                return false;
            return fullName == "Google.Protobuf.IMessage"
                || fullName.StartsWith("Google.Protobuf.IMessage<", StringComparison.Ordinal)
                || fullName.StartsWith("Google.Protobuf.IMessage`1", StringComparison.Ordinal);
        }
    }
}
