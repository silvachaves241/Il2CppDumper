#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using static Il2CppDumper.Il2CppConstants;

namespace Il2CppDumper
{
    public class Il2CppExecutor
    {
        public Metadata metadata;
        public Il2Cpp il2Cpp;
        private readonly Dictionary<int, RunManifestWarningItem> constantDecodeFailuresByDataIndex = new();

        /// <summary>Number of distinct default-value entries that failed to decode into a constant.</summary>
        public int ConstantDecodeFailedCount => constantDecodeFailuresByDataIndex.Count;

        /// <summary>Diagnostic details (byte, position, entry identity) of the first constant decode failure, if any.</summary>
        public string? ConstantDecodeFirstFailureDetails { get; private set; }

        /// <summary>Structured list of constant decode failures for run-manifest reporting.</summary>
        public List<RunManifestWarningItem> ConstantDecodeFailures => constantDecodeFailuresByDataIndex.Values.ToList();

        private static readonly Dictionary<int, string> TypeString = new()
        {
            {1,"void"},
            {2,"bool"},
            {3,"char"},
            {4,"sbyte"},
            {5,"byte"},
            {6,"short"},
            {7,"ushort"},
            {8,"int"},
            {9,"uint"},
            {10,"long"},
            {11,"ulong"},
            {12,"float"},
            {13,"double"},
            {14,"string"},
            {22,"TypedReference"},
            {24,"IntPtr"},
            {25,"UIntPtr"},
            {28,"object"},
        };
        public ulong[] customAttributeGenerators;

        public Il2CppExecutor(Metadata metadata, Il2Cpp il2Cpp)
        {
            this.metadata = metadata;
            this.il2Cpp = il2Cpp;

            if (il2Cpp.Version >= 27 && il2Cpp.Version < 29)
            {
                customAttributeGenerators = new ulong[metadata.imageDefs.Sum(x => x.customAttributeCount)];
                foreach (var imageDef in metadata.imageDefs)
                {
                    var imageDefName = metadata.GetStringFromIndex(imageDef.nameIndex);
                    if (!il2Cpp.TryGetCodeGenModule(imageDefName, out var codeGenModule))
                    {
                        continue;
                    }
                    if (imageDef.customAttributeCount > 0)
                    {
                        var pointers = il2Cpp.ReadClassArray<ulong>(il2Cpp.MapVATR(codeGenModule.customAttributeCacheGenerator), imageDef.customAttributeCount);
                        pointers.CopyTo(customAttributeGenerators, imageDef.customAttributeStart);
                    }
                }
            }
            else if (il2Cpp.Version < 27)
            {
                customAttributeGenerators = il2Cpp.customAttributeGenerators;
            }
        }

        private Dictionary<Il2CppTypeDefinition, List<Il2CppTypeDefinition>>? nestedTypeCandidatesMap;
        private Dictionary<Il2CppTypeDefinition, Il2CppTypeDefinition?>? resolvedParentsMap;
        private readonly HashSet<Il2CppTypeDefinition> warnedDefinitions = new();

        private Dictionary<Il2CppTypeDefinition, List<Il2CppTypeDefinition>> GetNestedTypeCandidatesMap()
        {
            if (nestedTypeCandidatesMap == null)
            {
                var map = new Dictionary<Il2CppTypeDefinition, List<Il2CppTypeDefinition>>();
                if (metadata.nestedTypeIndices != null && metadata.typeDefs != null)
                {
                    foreach (var parent in metadata.typeDefs)
                    {
                        if (parent.nested_type_count > 0 && parent.nestedTypesStart >= 0)
                        {
                            for (int n = 0; n < parent.nested_type_count; n++)
                            {
                                var idx = parent.nestedTypesStart + n;
                                if (idx >= 0 && idx < metadata.nestedTypeIndices.Length)
                                {
                                    var childIdx = metadata.nestedTypeIndices[idx];
                                    if (childIdx >= 0 && childIdx < metadata.typeDefs.Length)
                                    {
                                        var child = metadata.typeDefs[childIdx];
                                        if (!map.TryGetValue(child, out var list))
                                        {
                                            list = new List<Il2CppTypeDefinition>();
                                            map[child] = list;
                                        }
                                        if (!list.Contains(parent))
                                        {
                                            list.Add(parent);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                nestedTypeCandidatesMap = map;
            }
            return nestedTypeCandidatesMap;
        }

        private Il2CppTypeDefinition? GetRawDeclaredParent(Il2CppTypeDefinition typeDef)
        {
            if (typeDef.declaringTypeIndex >= 0 && typeDef.declaringTypeIndex < il2Cpp.types.Length)
            {
                var parentType = il2Cpp.types[typeDef.declaringTypeIndex];
                if (parentType.type == Il2CppTypeEnum.IL2CPP_TYPE_GENERICINST)
                {
                    var genericClass = il2Cpp.MapVATR<Il2CppGenericClass>(parentType.data.generic_class);
                    return GetGenericClassTypeDefinition(genericClass);
                }
                else
                {
                    return GetTypeDefinitionFromIl2CppType(parentType);
                }
            }
            return null;
        }

        private bool DeclaredChainHasCycle(
            Il2CppTypeDefinition start,
            Il2CppTypeDefinition targetChild,
            Dictionary<Il2CppTypeDefinition, byte> resolvingState)
        {
            var visited = new HashSet<Il2CppTypeDefinition> { targetChild };
            var curr = start;
            while (curr != null)
            {
                if (resolvingState.TryGetValue(curr, out var st) && st == 1)
                {
                    return true;
                }
                if (!visited.Add(curr))
                {
                    return true;
                }
                curr = GetRawDeclaredParent(curr);
            }
            return false;
        }

        private static bool CandidateLeadsToRoot(
            Il2CppTypeDefinition candidate,
            Il2CppTypeDefinition targetChild,
            Dictionary<Il2CppTypeDefinition, Il2CppTypeDefinition?> currentMap)
        {
            var visited = new HashSet<Il2CppTypeDefinition> { targetChild };
            var curr = candidate;
            while (curr != null)
            {
                if (!visited.Add(curr))
                {
                    return false;
                }
                currentMap.TryGetValue(curr, out curr);
            }
            return true;
        }

        private Il2CppTypeDefinition? ResolveTypeParent(
            Il2CppTypeDefinition typeDef,
            Dictionary<Il2CppTypeDefinition, Il2CppTypeDefinition?> map,
            Dictionary<Il2CppTypeDefinition, byte> resolvingState)
        {
            if (resolvingState.TryGetValue(typeDef, out var state))
            {
                if (state == 2)
                {
                    return map.TryGetValue(typeDef, out var p) ? p : null;
                }
                if (state == 1)
                {
                    return null;
                }
            }

            resolvingState[typeDef] = 1;

            var declared = GetRawDeclaredParent(typeDef);
            if (declared != null && !DeclaredChainHasCycle(declared, typeDef, resolvingState))
            {
                ResolveTypeParent(declared, map, resolvingState);
                map[typeDef] = declared;
                resolvingState[typeDef] = 2;
                return declared;
            }

            var candidateMap = GetNestedTypeCandidatesMap();
            var validCandidates = new List<Il2CppTypeDefinition>();
            if (candidateMap.TryGetValue(typeDef, out var candidates))
            {
                foreach (var c in candidates)
                {
                    if (ReferenceEquals(c, typeDef))
                    {
                        continue;
                    }

                    if (resolvingState.TryGetValue(c, out var cState) && cState == 1)
                    {
                        continue;
                    }

                    ResolveTypeParent(c, map, resolvingState);

                    if (CandidateLeadsToRoot(c, typeDef, map))
                    {
                        validCandidates.Add(c);
                    }
                }
            }

            if (declared != null)
            {
                if (validCandidates.Count == 1)
                {
                    var fallback = validCandidates[0];
                    if (warnedDefinitions.Add(typeDef))
                    {
                        var childName = metadata.GetStringFromIndex(typeDef.nameIndex);
                        var fallbackName = metadata.GetStringFromIndex(fallback.nameIndex);
                        CoreLogger.LogWarning($"WARNING: Cyclic declaring type detected for '{childName}' (declaringTypeIndex points to self or creates a cycle). Falling back to candidate parent '{fallbackName}'.");
                    }
                    map[typeDef] = fallback;
                    resolvingState[typeDef] = 2;
                    return fallback;
                }

                if (warnedDefinitions.Add(typeDef))
                {
                    var childName = metadata.GetStringFromIndex(typeDef.nameIndex);
                    CoreLogger.LogWarning($"WARNING: Cyclic declaring type detected for '{childName}' with no unique acyclic parent candidate. Preserving as unparented root definition.");
                }
                map[typeDef] = null;
                resolvingState[typeDef] = 2;
                return null;
            }

            if (validCandidates.Count == 1)
            {
                var parent = validCandidates[0];
                map[typeDef] = parent;
                resolvingState[typeDef] = 2;
                return parent;
            }

            if (validCandidates.Count > 1)
            {
                if (warnedDefinitions.Add(typeDef))
                {
                    var childName = metadata.GetStringFromIndex(typeDef.nameIndex);
                    var parentNames = string.Join(", ", validCandidates.Select(c => metadata.GetStringFromIndex(c.nameIndex)));
                    CoreLogger.LogWarning($"WARNING: Ambiguous declaring type for nested type '{childName}': multiple candidate parents found ({parentNames}) with no valid declaringTypeIndex.");
                }
                map[typeDef] = null;
                resolvingState[typeDef] = 2;
                return null;
            }

            map[typeDef] = null;
            resolvingState[typeDef] = 2;
            return null;
        }

        private Dictionary<Il2CppTypeDefinition, Il2CppTypeDefinition?> EnsureParentMapResolved()
        {
            if (resolvedParentsMap != null)
            {
                return resolvedParentsMap;
            }

            var map = new Dictionary<Il2CppTypeDefinition, Il2CppTypeDefinition?>();
            var resolvingState = new Dictionary<Il2CppTypeDefinition, byte>();
            var allDefs = metadata.typeDefs ?? Array.Empty<Il2CppTypeDefinition>();

            foreach (var def in allDefs)
            {
                ResolveTypeParent(def, map, resolvingState);
            }

            resolvedParentsMap = map;
            return resolvedParentsMap;
        }

        public Il2CppTypeDefinition? GetParentTypeDefinition(Il2CppTypeDefinition typeDef)
        {
            var map = EnsureParentMapResolved();
            if (map.TryGetValue(typeDef, out var parent))
            {
                return parent;
            }

            var resolvingState = new Dictionary<Il2CppTypeDefinition, byte>();
            return ResolveTypeParent(typeDef, map, resolvingState);
        }

        private sealed class TypeHierarchyLevel
        {
            public Il2CppTypeDefinition TypeDef { get; set; } = null!;
            public string BaseName { get; set; } = string.Empty;
            public int ParamStart { get; set; }
            public int ParamCount { get; set; }
        }

        private List<TypeHierarchyLevel> GetTypeHierarchyLevels(Il2CppTypeDefinition typeDef)
        {
            var chain = new List<Il2CppTypeDefinition>();
            var current = typeDef;
            int depth = 0;
            while (current != null && depth < 64)
            {
                chain.Add(current);
                current = GetParentTypeDefinition(current);
                depth++;
            }
            chain.Reverse();

            var levels = new List<TypeHierarchyLevel>(chain.Count);
            int prevTotal = 0;

            for (int i = 0; i < chain.Count; i++)
            {
                var def = chain[i];
                var rawName = metadata.GetStringFromIndex(def.nameIndex);
                var tickIndex = rawName.IndexOf('`');
                int confirmedArity = -1;
                if (tickIndex != -1 && int.TryParse(rawName[(tickIndex + 1)..], out var arity))
                {
                    confirmedArity = arity;
                }

                bool isGeneric = def.genericContainerIndex >= 0
                    && metadata.genericContainers != null
                    && def.genericContainerIndex < metadata.genericContainers.Length;

                string baseName;
                int paramStart = 0;
                int paramCount = 0;

                if (isGeneric)
                {
                    baseName = confirmedArity >= 0 ? rawName[..tickIndex] : rawName;
                    var container = metadata.genericContainers![def.genericContainerIndex];
                    int total = container.type_argc;

                    if (i > 0)
                    {
                        if (confirmedArity >= 0)
                        {
                            paramCount = confirmedArity;
                            paramStart = total >= confirmedArity ? total - confirmedArity : prevTotal;
                        }
                        else if (total > prevTotal)
                        {
                            paramStart = prevTotal;
                            paramCount = total - prevTotal;
                        }
                        else
                        {
                            paramCount = 0;
                            paramStart = prevTotal;
                        }
                    }
                    else
                    {
                        paramStart = 0;
                        paramCount = confirmedArity >= 0 ? confirmedArity : total;
                    }

                    prevTotal = total;
                }
                else
                {
                    baseName = rawName;
                    paramStart = 0;
                    paramCount = 0;
                    prevTotal = 0;
                }

                levels.Add(new TypeHierarchyLevel
                {
                    TypeDef = def,
                    BaseName = baseName,
                    ParamStart = paramStart,
                    ParamCount = paramCount
                });
            }

            return levels;
        }

        public string GetTypeDefName(Il2CppTypeDefinition typeDef, bool addNamespace, bool genericParameter)
        {
            var levels = GetTypeHierarchyLevels(typeDef);
            if (levels.Count == 0)
            {
                return string.Empty;
            }

            var sb = new StringBuilder();
            if (addNamespace)
            {
                var rootDef = levels[0].TypeDef;
                var @namespace = metadata.GetStringFromIndex(rootDef.namespaceIndex);
                if (!string.IsNullOrEmpty(@namespace))
                {
                    sb.Append(@namespace).Append('.');
                }
            }

            for (int i = 0; i < levels.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append('.');
                }
                var level = levels[i];
                sb.Append(level.BaseName);

                if (genericParameter && level.ParamCount > 0 && level.TypeDef.genericContainerIndex >= 0 && metadata.genericParameters != null)
                {
                    var genericContainer = metadata.genericContainers[level.TypeDef.genericContainerIndex];
                    var paramNames = new List<string>();
                    for (int p = 0; p < level.ParamCount; p++)
                    {
                        var paramIndex = genericContainer.genericParameterStart + level.ParamStart + p;
                        if (paramIndex >= 0 && paramIndex < metadata.genericParameters.Length)
                        {
                            var paramDef = metadata.genericParameters[paramIndex];
                            paramNames.Add(metadata.GetStringFromIndex(paramDef.nameIndex));
                        }
                    }
                    if (paramNames.Count > 0)
                    {
                        sb.Append('<').Append(string.Join(", ", paramNames)).Append('>');
                    }
                }
            }

            return sb.ToString();
        }

        public string GetTypeName(Il2CppType il2CppType, bool addNamespace, bool is_nested = false)
        {
            switch (il2CppType.type)
            {
                case Il2CppTypeEnum.IL2CPP_TYPE_ARRAY:
                    {
                        var arrayType = il2Cpp.MapVATR<Il2CppArrayType>(il2CppType.data.array);
                        var elementType = il2Cpp.GetIl2CppType(arrayType.etype);
                        return $"{GetTypeName(elementType, addNamespace, false)}[{new string(',', arrayType.rank - 1)}]";
                    }
                case Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY:
                    {
                        var elementType = il2Cpp.GetIl2CppType(il2CppType.data.type);
                        return $"{GetTypeName(elementType, addNamespace, false)}[]";
                    }
                case Il2CppTypeEnum.IL2CPP_TYPE_PTR:
                    {
                        var oriType = il2Cpp.GetIl2CppType(il2CppType.data.type);
                        return $"{GetTypeName(oriType, addNamespace, false)}*";
                    }
                case Il2CppTypeEnum.IL2CPP_TYPE_VAR:
                case Il2CppTypeEnum.IL2CPP_TYPE_MVAR:
                    {
                        var param = GetGenericParameteFromIl2CppType(il2CppType);
                        return metadata.GetStringFromIndex(param.nameIndex);
                    }
                case Il2CppTypeEnum.IL2CPP_TYPE_CLASS:
                case Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE:
                case Il2CppTypeEnum.IL2CPP_TYPE_GENERICINST:
                    {
                        Il2CppTypeDefinition? typeDef;
                        Il2CppGenericClass? genericClass = null;
                        if (il2CppType.type == Il2CppTypeEnum.IL2CPP_TYPE_GENERICINST)
                        {
                            genericClass = il2Cpp.MapVATR<Il2CppGenericClass>(il2CppType.data.generic_class);
                            typeDef = GetGenericClassTypeDefinition(genericClass);
                        }
                        else
                        {
                            typeDef = GetTypeDefinitionFromIl2CppType(il2CppType);
                        }

                        if (typeDef == null)
                        {
                            return TypeString[(int)il2CppType.type];
                        }

                        var levels = GetTypeHierarchyLevels(typeDef);
                        if (levels.Count == 0)
                        {
                            return string.Empty;
                        }

                        var sb = new StringBuilder();
                        if (addNamespace)
                        {
                            var rootDef = levels[0].TypeDef;
                            var @namespace = metadata.GetStringFromIndex(rootDef.namespaceIndex);
                            if (!string.IsNullOrEmpty(@namespace))
                            {
                                sb.Append(@namespace).Append('.');
                            }
                        }

                        ulong[]? instPointers = null;
                        int instArgc = 0;
                        if (genericClass != null && genericClass.context.class_inst != 0)
                        {
                            var genericInst = il2Cpp.MapVATR<Il2CppGenericInst>(genericClass.context.class_inst);
                            instArgc = (int)genericInst.type_argc;
                            if (instArgc > 0 && genericInst.type_argv != 0)
                            {
                                instPointers = il2Cpp.MapVATR<ulong>(genericInst.type_argv, instArgc);
                            }
                        }

                        for (int i = 0; i < levels.Count; i++)
                        {
                            if (i > 0)
                            {
                                sb.Append('.');
                            }
                            var level = levels[i];
                            sb.Append(level.BaseName);

                            if (instPointers != null && level.ParamCount > 0)
                            {
                                var argNames = new List<string>();
                                for (int p = 0; p < level.ParamCount; p++)
                                {
                                    var argIndex = level.ParamStart + p;
                                    if (argIndex >= 0 && argIndex < instArgc)
                                    {
                                        var argType = il2Cpp.GetIl2CppType(instPointers[argIndex]);
                                        if (argType != null)
                                        {
                                            argNames.Add(GetTypeName(argType, false, false));
                                        }
                                    }
                                }
                                if (argNames.Count > 0)
                                {
                                    sb.Append('<').Append(string.Join(", ", argNames)).Append('>');
                                }
                            }
                            else if (genericClass == null && level.ParamCount > 0 && level.TypeDef.genericContainerIndex >= 0 && metadata.genericParameters != null)
                            {
                                var genericContainer = metadata.genericContainers[level.TypeDef.genericContainerIndex];
                                var paramNames = new List<string>();
                                for (int p = 0; p < level.ParamCount; p++)
                                {
                                    var paramIndex = genericContainer.genericParameterStart + level.ParamStart + p;
                                    if (paramIndex >= 0 && paramIndex < metadata.genericParameters.Length)
                                    {
                                        var paramDef = metadata.genericParameters[paramIndex];
                                        paramNames.Add(metadata.GetStringFromIndex(paramDef.nameIndex));
                                    }
                                }
                                if (paramNames.Count > 0)
                                {
                                    sb.Append('<').Append(string.Join(", ", paramNames)).Append('>');
                                }
                            }
                        }

                        return sb.ToString();
                    }
                default:
                    return TypeString[(int)il2CppType.type];
            }
        }

        public string GetGenericInstParams(Il2CppGenericInst genericInst)
        {
            var genericParameterNames = new List<string>();
            var pointers = il2Cpp.MapVATR<ulong>(genericInst.type_argv, genericInst.type_argc);
            for (int i = 0; i < genericInst.type_argc; i++)
            {
                var il2CppType = il2Cpp.GetIl2CppType(pointers[i]);
                genericParameterNames.Add(GetTypeName(il2CppType, false, false));
            }
            return $"<{string.Join(", ", genericParameterNames)}>";
        }

        public string GetGenericContainerParams(Il2CppGenericContainer genericContainer)
        {
            var genericParameterNames = new List<string>();
            for (int i = 0; i < genericContainer.type_argc; i++)
            {
                var genericParameterIndex = genericContainer.genericParameterStart + i;
                var genericParameter = metadata.genericParameters[genericParameterIndex];
                genericParameterNames.Add(metadata.GetStringFromIndex(genericParameter.nameIndex));
            }
            return $"<{string.Join(", ", genericParameterNames)}>";
        }

        public (string, string) GetMethodSpecName(Il2CppMethodSpec methodSpec, bool addNamespace = false)
        {
            var methodDef = metadata.methodDefs[methodSpec.methodDefinitionIndex];
            var typeDef = metadata.typeDefs[methodDef.declaringType];
            var typeName = GetTypeDefName(typeDef, addNamespace, false);
            if (methodSpec.classIndexIndex != -1)
            {
                var classInst = il2Cpp.genericInsts[methodSpec.classIndexIndex];
                typeName += GetGenericInstParams(classInst);
            }
            var methodName = metadata.GetStringFromIndex(methodDef.nameIndex);
            if (methodSpec.methodIndexIndex != -1)
            {
                var methodInst = il2Cpp.genericInsts[methodSpec.methodIndexIndex];
                methodName += GetGenericInstParams(methodInst);
            }
            return (typeName, methodName);
        }

        public Il2CppGenericContext GetMethodSpecGenericContext(Il2CppMethodSpec methodSpec)
        {
            var classInstPointer = 0ul;
            var methodInstPointer = 0ul;
            if (methodSpec.classIndexIndex != -1)
            {
                classInstPointer = il2Cpp.genericInstPointers[methodSpec.classIndexIndex];
            }
            if (methodSpec.methodIndexIndex != -1)
            {
                methodInstPointer = il2Cpp.genericInstPointers[methodSpec.methodIndexIndex];
            }
            return new Il2CppGenericContext { class_inst = classInstPointer, method_inst = methodInstPointer };
        }

        public Il2CppRGCTXDefinition[] GetRGCTXDefinition(string imageName, Il2CppTypeDefinition typeDef)
        {
            Il2CppRGCTXDefinition[] collection = null;
            if (il2Cpp.Version >= 24.2)
            {
                if (il2Cpp.TryGetRGCTXDataDictionary(imageName, out var rgctxs))
                {
                    rgctxs.TryGetValue(typeDef.token, out collection);
                }
            }
            else
            {
                if (typeDef.rgctxCount > 0)
                {
                    collection = new Il2CppRGCTXDefinition[typeDef.rgctxCount];
                    Array.Copy(metadata.rgctxEntries, typeDef.rgctxStartIndex, collection, 0, typeDef.rgctxCount);
                }
            }
            return collection;
        }

        public Il2CppRGCTXDefinition[] GetRGCTXDefinition(string imageName, Il2CppMethodDefinition methodDef)
        {
            Il2CppRGCTXDefinition[] collection = null;
            if (il2Cpp.Version >= 24.2)
            {
                if (il2Cpp.TryGetRGCTXDataDictionary(imageName, out var rgctxs))
                {
                    rgctxs.TryGetValue(methodDef.token, out collection);
                }
            }
            else
            {
                if (methodDef.rgctxCount > 0)
                {
                    collection = new Il2CppRGCTXDefinition[methodDef.rgctxCount];
                    Array.Copy(metadata.rgctxEntries, methodDef.rgctxStartIndex, collection, 0, methodDef.rgctxCount);
                }
            }
            return collection;
        }

        public Il2CppTypeDefinition GetGenericClassTypeDefinition(Il2CppGenericClass genericClass)
        {
            if (il2Cpp.Version >= 27)
            {
                var il2CppType = il2Cpp.GetIl2CppType(genericClass.type);
                if (il2CppType == null)
                {
                    return null;
                }
                return GetTypeDefinitionFromIl2CppType(il2CppType);
            }
            if (genericClass.typeDefinitionIndex == 4294967295 || genericClass.typeDefinitionIndex == -1)
            {
                return null;
            }
            return metadata.typeDefs[genericClass.typeDefinitionIndex];
        }

        public Il2CppTypeDefinition GetTypeDefinitionFromIl2CppType(Il2CppType il2CppType)
        {
            if (il2Cpp.Version >= 27 && il2Cpp.IsDumped)
            {
                var typeDefinitionsOffset = metadata.Version >= 38
                    ? (ulong)metadata.header.typeDefinitions.offset
                    : metadata.header.typeDefinitionsOffset;
                var offset = il2CppType.data.typeHandle - metadata.ImageBase - typeDefinitionsOffset;
                var index = offset / (ulong)metadata.SizeOf(typeof(Il2CppTypeDefinition));
                return metadata.typeDefs[index];
            }
            else
            {
                return metadata.typeDefs[il2CppType.data.klassIndex];
            }
        }

        public Il2CppGenericParameter GetGenericParameteFromIl2CppType(Il2CppType il2CppType)
        {
            if (il2Cpp.Version >= 27 && il2Cpp.IsDumped)
            {
                var genericParametersOffset = metadata.Version >= 38
                    ? (ulong)metadata.header.genericParameters.offset
                    : metadata.header.genericParametersOffset;
                var offset = il2CppType.data.genericParameterHandle - metadata.ImageBase - genericParametersOffset;
                var index = offset / (ulong)metadata.SizeOf(typeof(Il2CppGenericParameter));
                return metadata.genericParameters[index];
            }
            else
            {
                return metadata.genericParameters[il2CppType.data.genericParameterIndex];
            }
        }

        public SectionHelper GetSectionHelper()
        {
            return il2Cpp.GetSectionHelper(metadata.methodDefs.Count(x => x.methodIndex >= 0), metadata.typeDefs.Length, metadata.imageDefs.Length);
        }

        public bool TryGetDefaultValue(int typeIndex, int dataIndex, out object value) =>
            TryGetDefaultValue(typeIndex, dataIndex, out value, null, null, null);

        public bool TryGetDefaultValue(int typeIndex, int dataIndex, out object value, int? typeDefIndex, string? typeName, string? member)
        {
            var pointer = metadata.GetDefaultValueFromIndex(dataIndex);
            var defaultValueType = il2Cpp.types[typeIndex];
            metadata.Position = pointer;
            try
            {
                if (GetConstantValueFromBlob(defaultValueType.type, metadata.Reader, out var blobValue))
                {
                    value = blobValue.Value;
                    return true;
                }
            }
            catch (InvalidCompressedIntegerException e)
            {
                if (!constantDecodeFailuresByDataIndex.TryGetValue(dataIndex, out var failureItem))
                {
                    ConstantDecodeFirstFailureDetails ??=
                        $"type 0x{(int)defaultValueType.type:X}, dataIndex {dataIndex}, pointer 0x{pointer:X}: {e.Message}";

                    string? rawBytesHex = null;
                    try
                    {
                        var savePos = metadata.Position;
                        metadata.Position = pointer;
                        var len = (int)Math.Min(16L, Math.Max(0L, (long)metadata.Length - pointer));
                        if (len > 0)
                        {
                            var bytes = metadata.ReadBytes(len);
                            rawBytesHex = BitConverter.ToString(bytes).Replace("-", " ");
                        }
                        metadata.Position = savePos;
                    }
                    catch
                    {
                        // best effort
                    }

                    var detail = rawBytesHex != null
                        ? $"Invalid compressed integer format (pointer 0x{pointer:X}, dataIndex {dataIndex}): {e.Message}; raw bytes: {rawBytesHex}"
                        : $"Invalid compressed integer format (pointer 0x{pointer:X}, dataIndex {dataIndex}): {e.Message}";

                    failureItem = new RunManifestWarningItem
                    {
                        Category = "constantDecode",
                        TypeDefIndex = typeDefIndex,
                        TypeName = typeName,
                        Member = member,
                        RawBytes = rawBytesHex,
                        Detail = detail,
                        Count = 1
                    };
                    constantDecodeFailuresByDataIndex[dataIndex] = failureItem;
                }
                else if (typeDefIndex != null || !string.IsNullOrEmpty(typeName) || !string.IsNullOrEmpty(member))
                {
                    if (failureItem.TypeDefIndex == null && typeDefIndex != null) failureItem.TypeDefIndex = typeDefIndex;
                    if (string.IsNullOrEmpty(failureItem.TypeName) && !string.IsNullOrEmpty(typeName)) failureItem.TypeName = typeName;
                    if (string.IsNullOrEmpty(failureItem.Member) && !string.IsNullOrEmpty(member)) failureItem.Member = member;
                }
            }
            value = pointer;
            return false;
        }

        public bool GetConstantValueFromBlob(Il2CppTypeEnum type, BinaryReader reader, out BlobValue value)
        {
            value = new BlobValue
            {
                il2CppTypeEnum = type
            };
            switch (type)
            {
                case Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN:
                    value.Value = reader.ReadBoolean();
                    return true;
                case Il2CppTypeEnum.IL2CPP_TYPE_U1:
                    value.Value = reader.ReadByte();
                    return true;
                case Il2CppTypeEnum.IL2CPP_TYPE_I1:
                    value.Value = reader.ReadSByte();
                    return true;
                case Il2CppTypeEnum.IL2CPP_TYPE_CHAR:
                    value.Value = BitConverter.ToChar(reader.ReadBytes(2), 0);
                    return true;
                case Il2CppTypeEnum.IL2CPP_TYPE_U2:
                    value.Value = reader.ReadUInt16();
                    return true;
                case Il2CppTypeEnum.IL2CPP_TYPE_I2:
                    value.Value = reader.ReadInt16();
                    return true;
                case Il2CppTypeEnum.IL2CPP_TYPE_U4:
                    if (il2Cpp.Version >= 29)
                    {
                        value.Value = reader.ReadCompressedUInt32();
                    }
                    else
                    {
                        value.Value = reader.ReadUInt32();
                    }
                    return true;
                case Il2CppTypeEnum.IL2CPP_TYPE_I4:
                    if (il2Cpp.Version >= 29)
                    {
                        value.Value = reader.ReadCompressedInt32();
                    }
                    else
                    {
                        value.Value = reader.ReadInt32();
                    }
                    return true;
                case Il2CppTypeEnum.IL2CPP_TYPE_U8:
                    value.Value = reader.ReadUInt64();
                    return true;
                case Il2CppTypeEnum.IL2CPP_TYPE_I8:
                    value.Value = reader.ReadInt64();
                    return true;
                case Il2CppTypeEnum.IL2CPP_TYPE_I:
                    value.Value = il2Cpp.PointerSize == 4 ? (object)reader.ReadInt32() : reader.ReadInt64();
                    return true;
                case Il2CppTypeEnum.IL2CPP_TYPE_U:
                    value.Value = il2Cpp.PointerSize == 4 ? (object)reader.ReadUInt32() : reader.ReadUInt64();
                    return true;
                case Il2CppTypeEnum.IL2CPP_TYPE_R4:
                    value.Value = reader.ReadSingle();
                    return true;
                case Il2CppTypeEnum.IL2CPP_TYPE_R8:
                    value.Value = reader.ReadDouble();
                    return true;
                case Il2CppTypeEnum.IL2CPP_TYPE_STRING:
                    int length;
                    if (il2Cpp.Version >= 29)
                    {
                        length = reader.ReadCompressedInt32();
                        if (length == -1)
                        {
                            value.Value = null;
                        }
                        else
                        {
                            value.Value = Encoding.UTF8.GetString(reader.ReadBytes(length));
                        }
                    }
                    else
                    {
                        length = reader.ReadInt32();
                        value.Value = reader.ReadString(length);
                    }
                    return true;
                case Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY:
                    var arrayLen = reader.ReadCompressedInt32();
                    if (arrayLen == -1)
                    {
                        value.Value = null;
                    }
                    else
                    {
                        var array = new BlobValue[arrayLen];
                        var arrayElementType = ReadEncodedTypeEnum(reader, out var enumType);
                        var arrayElementsAreDifferent = reader.ReadByte();
                        for (int i = 0; i < arrayLen; i++)
                        {
                            var elementType = arrayElementType;
                            var elementEnumType = enumType;
                            if (arrayElementsAreDifferent == 1)
                            {
                                elementType = ReadEncodedTypeEnum(reader, out elementEnumType);
                            }
                            if (!GetConstantValueFromBlob(elementType, reader, out var data) || data == null)
                            {
                                data = new BlobValue();
                            }
                            data.il2CppTypeEnum = elementType;
                            data.EnumType = elementEnumType;
                            array[i] = data;
                        }
                        value.Value = array;
                    }
                    return true;
                case Il2CppTypeEnum.IL2CPP_TYPE_IL2CPP_TYPE_INDEX:
                    var typeIndex = reader.ReadCompressedInt32();
                    if (typeIndex == -1)
                    {
                        value.Value = null;
                    }
                    else
                    {
                        value.Value = il2Cpp.types[typeIndex];
                    }
                    return true;
                case Il2CppTypeEnum.IL2CPP_TYPE_CLASS:
                case Il2CppTypeEnum.IL2CPP_TYPE_OBJECT:
                case Il2CppTypeEnum.IL2CPP_TYPE_GENERICINST:
                case Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE:
                    value.Value = null;
                    return true;
                default:
                    return false;
            }
        }

        public Il2CppType GetEnumUnderlyingType(Il2CppTypeDefinition typeDef)
        {
            if (typeDef == null)
            {
                return null;
            }

            if (metadata.Version <= 31)
            {
                if (typeDef.elementTypeIndex >= 0 && typeDef.elementTypeIndex < il2Cpp.types.Length)
                {
                    var legacyType = il2Cpp.types[typeDef.elementTypeIndex];
                    if (IsValidEnumUnderlyingType(legacyType.type))
                    {
                        return legacyType;
                    }
                }
            }

            if (metadata.fieldDefs != null && typeDef.field_count > 0 && typeDef.fieldStart >= 0)
            {
                var fieldEnd = typeDef.fieldStart + typeDef.field_count;

                // Priority 1: Non-static field named "value__" with valid integral underlying type
                for (var i = typeDef.fieldStart; i < fieldEnd && i < metadata.fieldDefs.Length; ++i)
                {
                    var fieldDef = metadata.fieldDefs[i];
                    if (fieldDef.typeIndex >= 0 && fieldDef.typeIndex < il2Cpp.types.Length)
                    {
                        var fieldType = il2Cpp.types[fieldDef.typeIndex];
                        if ((fieldType.attrs & FIELD_ATTRIBUTE_STATIC) == 0 && IsValidEnumUnderlyingType(fieldType.type))
                        {
                            string fieldName = null;
                            try
                            {
                                fieldName = metadata.GetStringFromIndex(fieldDef.nameIndex);
                            }
                            catch
                            {
                            }
                            if (fieldName == "value__")
                            {
                                return fieldType;
                            }
                        }
                    }
                }

                // Priority 2: Any non-static field with valid integral underlying type
                for (var i = typeDef.fieldStart; i < fieldEnd && i < metadata.fieldDefs.Length; ++i)
                {
                    var fieldDef = metadata.fieldDefs[i];
                    if (fieldDef.typeIndex >= 0 && fieldDef.typeIndex < il2Cpp.types.Length)
                    {
                        var fieldType = il2Cpp.types[fieldDef.typeIndex];
                        if ((fieldType.attrs & FIELD_ATTRIBUTE_STATIC) == 0 && IsValidEnumUnderlyingType(fieldType.type))
                        {
                            return fieldType;
                        }
                    }
                }
            }

            return null;
        }

        private static bool IsValidEnumUnderlyingType(Il2CppTypeEnum type)
        {
            return type switch
            {
                Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN => true,
                Il2CppTypeEnum.IL2CPP_TYPE_CHAR => true,
                Il2CppTypeEnum.IL2CPP_TYPE_I1 => true,
                Il2CppTypeEnum.IL2CPP_TYPE_U1 => true,
                Il2CppTypeEnum.IL2CPP_TYPE_I2 => true,
                Il2CppTypeEnum.IL2CPP_TYPE_U2 => true,
                Il2CppTypeEnum.IL2CPP_TYPE_I4 => true,
                Il2CppTypeEnum.IL2CPP_TYPE_U4 => true,
                Il2CppTypeEnum.IL2CPP_TYPE_I8 => true,
                Il2CppTypeEnum.IL2CPP_TYPE_U8 => true,
                Il2CppTypeEnum.IL2CPP_TYPE_I => true,
                Il2CppTypeEnum.IL2CPP_TYPE_U => true,
                _ => false
            };
        }

        public Il2CppTypeEnum ReadEncodedTypeEnum(BinaryReader reader, out Il2CppType enumType)
        {
            enumType = null;
            var type = (Il2CppTypeEnum)reader.ReadByte();
            if (type == Il2CppTypeEnum.IL2CPP_TYPE_ENUM)
            {
                var enumTypeIndex = reader.ReadCompressedInt32();
                enumType = il2Cpp.types[enumTypeIndex];
                var typeDef = GetTypeDefinitionFromIl2CppType(enumType);
                var underlyingType = GetEnumUnderlyingType(typeDef);
                if (underlyingType == null)
                {
                    throw new InvalidDataException($"Unable to resolve underlying type for enum type index {enumTypeIndex}.");
                }
                type = underlyingType.type;
            }
            return type;
        }
    }
}
