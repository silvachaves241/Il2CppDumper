#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Serialization;
using static Il2CppDumper.Il2CppConstants;

namespace Il2CppDumper;

public sealed class JsonDumpVTableEntry
{
    public int position { get; init; }
    public int? methodDefIndex { get; init; }
    public bool resolved => methodDefIndex.HasValue;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? methodSpecIndex { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? declaredSlot { get; init; }

    public string interfaceStatus { get; set; } = "class";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<JsonDumpInterfaceMethod>? interfaces { get; set; }
}

public sealed class JsonDumpInterfaceMethod
{
    public int interfaceTypeIndex { get; init; }
    public int interfaceTypeDefIndex { get; init; }
    public int interfaceMethodDefIndex { get; init; }
    public int interfaceSlot { get; init; }
}

public sealed class JsonDumpInterfaceDispatch
{
    public int receiverTypeDefIndex { get; init; }
    public int position { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? declaredSlot { get; init; }

    public List<JsonDumpInterfaceMethod> interfaces { get; init; } = new();
}

internal sealed class JsonVTableExporter
{
    private readonly Il2CppExecutor executor;
    private readonly Metadata metadata;
    private readonly Il2Cpp il2Cpp;
    private readonly bool includeMethodLinks;
    private readonly Dictionary<Il2CppTypeDefinition, int> typeDefIndices = new();
    private readonly Dictionary<int, InterfaceLayout> interfaceLayouts = new();
    private readonly Dictionary<int, List<JsonDumpInterfaceDispatch>> dispatchByMethod = new();

    internal JsonVTableExporter(Il2CppExecutor executor, bool includeMethodLinks)
    {
        this.executor = executor;
        this.includeMethodLinks = includeMethodLinks;
        metadata = executor.metadata;
        il2Cpp = executor.il2Cpp;
        for (var index = 0; index < metadata.typeDefs.Length; index++)
            typeDefIndices.TryAdd(metadata.typeDefs[index], index);
    }

    internal JsonDumpVTableEntry[] CreateVTable(Il2CppTypeDefinition type, int typeDefIndex)
    {
        if (type.vtable_count == 0)
            return Array.Empty<JsonDumpVTableEntry>();

        var entries = new JsonDumpVTableEntry[type.vtable_count];
        for (var position = 0; position < entries.Length; position++)
        {
            var tableIndex = (long)type.vtableStart + position;
            var target = type.vtableStart >= 0 && tableIndex < metadata.vtableMethods.Length
                ? ResolveTarget(metadata.vtableMethods[(int)tableIndex])
                : default;
            var slot = target.MethodDefIndex is int index ? metadata.methodDefs[index].slot : ushort.MaxValue;
            entries[position] = new JsonDumpVTableEntry
            {
                position = position,
                methodDefIndex = target.MethodDefIndex,
                methodSpecIndex = target.MethodSpecIndex,
                declaredSlot = slot == ushort.MaxValue ? null : slot
            };
        }

        var complete = AddInterfaceOrigins(type, entries);
        foreach (var entry in entries)
        {
            entry.interfaceStatus = entry.interfaces != null ? "interface" : complete ? "class" : "unknown";
            if (!includeMethodLinks || entry.methodDefIndex is not int methodIndex || entry.interfaces == null)
                continue;

            if (!dispatchByMethod.TryGetValue(methodIndex, out var links))
                dispatchByMethod.Add(methodIndex, links = new());
            links.Add(new JsonDumpInterfaceDispatch
            {
                receiverTypeDefIndex = typeDefIndex,
                position = entry.position,
                declaredSlot = entry.declaredSlot,
                interfaces = entry.interfaces
            });
        }
        return entries;
    }

    internal void LinkMethods(IEnumerable<JsonDumpType> types)
    {
        if (dispatchByMethod.Count == 0)
            return;

        foreach (var type in types)
        {
            foreach (var method in type.methods)
            {
                if (method.methodDefIndex is int index && dispatchByMethod.TryGetValue(index, out var links))
                    method.interfaceDispatch = links;
            }
        }
    }

    private (int? MethodDefIndex, int? MethodSpecIndex) ResolveTarget(uint encoded)
    {
        var usage = (Il2CppMetadataUsage)metadata.GetEncodedIndexTypeForVersion(encoded);
        var index = (int)metadata.GetDecodedMethodIndex(encoded);
        if (usage == Il2CppMetadataUsage.kIl2CppMetadataUsageMethodDef)
            return ((uint)index < (uint)metadata.methodDefs.Length ? index : null, null);

        if (usage == Il2CppMetadataUsage.kIl2CppMetadataUsageMethodRef)
        {
            var specs = il2Cpp.methodSpecs;
            if (specs != null && index < specs.Length && specs[index] is { } spec &&
                (uint)spec.methodDefinitionIndex < (uint)metadata.methodDefs.Length)
                return (spec.methodDefinitionIndex, index);

            return (null, index);
        }
        return default;
    }

    private bool AddInterfaceOrigins(Il2CppTypeDefinition receiver, JsonDumpVTableEntry[] entries)
    {
        if (receiver.interface_offsets_count == 0)
            return true;
        if (receiver.interfaceOffsetsStart < 0 ||
            (long)receiver.interfaceOffsetsStart + receiver.interface_offsets_count > metadata.InterfaceOffsets.Length)
            return false;

        var complete = true;
        var seenOffsets = new HashSet<(int TypeIndex, int Offset)>();
        for (var i = 0; i < receiver.interface_offsets_count; i++)
        {
            var pair = metadata.InterfaceOffsets[receiver.interfaceOffsetsStart + i];
            if (!seenOffsets.Add(pair))
                continue;

            var layout = GetInterfaceLayout(pair.TypeIndex);
            if (pair.Offset < 0 || layout.Methods == null)
            {
                complete = false;
                continue;
            }

            complete &= layout.Complete;
            foreach (var method in layout.Methods)
            {
                var position = (long)pair.Offset + method.interfaceSlot;
                if (position >= entries.Length)
                {
                    complete = false;
                    continue;
                }
                (entries[(int)position].interfaces ??= new()).Add(method);
            }
        }
        return complete;
    }

    private InterfaceLayout GetInterfaceLayout(int typeIndex)
    {
        if (interfaceLayouts.TryGetValue(typeIndex, out var cached))
            return cached;

        var layout = ReadInterfaceLayout(typeIndex);
        interfaceLayouts.Add(typeIndex, layout);
        return layout;
    }

    private InterfaceLayout ReadInterfaceLayout(int typeIndex)
    {
        if ((uint)typeIndex >= (uint)il2Cpp.types.Length ||
            ResolveInterfaceDefinition(il2Cpp.types[typeIndex]) is not { } definition ||
            (definition.flags & TYPE_ATTRIBUTE_INTERFACE) == 0 ||
            !typeDefIndices.TryGetValue(definition, out var typeDefIndex))
            return default;

        var methods = new List<JsonDumpInterfaceMethod>();
        var complete = true;
        for (var ordinal = 0; ordinal < definition.method_count; ordinal++)
        {
            var index = metadata.GetMethodIndexFromTypeDefinition(typeDefIndex, ordinal);
            if ((uint)index >= (uint)metadata.methodDefs.Length ||
                metadata.methodDefs[index].declaringType != typeDefIndex)
            {
                complete = false;
                continue;
            }
            var method = metadata.methodDefs[index];
            if ((method.flags & METHOD_ATTRIBUTE_STATIC) != 0 || (method.flags & METHOD_ATTRIBUTE_VIRTUAL) == 0)
                continue;
            if (method.slot == ushort.MaxValue)
            {
                complete = false;
                continue;
            }

            methods.Add(new JsonDumpInterfaceMethod
            {
                interfaceTypeIndex = typeIndex,
                interfaceTypeDefIndex = typeDefIndex,
                interfaceMethodDefIndex = index,
                interfaceSlot = method.slot
            });
        }
        return new InterfaceLayout(methods, complete);
    }

    private Il2CppTypeDefinition? ResolveInterfaceDefinition(Il2CppType? type)
    {
        if (type == null)
            return null;

        try
        {
            if (type.type == Il2CppTypeEnum.IL2CPP_TYPE_GENERICINST)
            {
                var address = type.data.generic_class;
                var size = il2Cpp.Is32Bit ? 16UL : 32UL;
                return address != 0 && il2Cpp.TryMapContiguousRange(address, size, out var offset)
                    ? executor.GetGenericClassTypeDefinition(il2Cpp.ReadClass<Il2CppGenericClass>(offset))
                    : null;
            }
            return type.type == Il2CppTypeEnum.IL2CPP_TYPE_CLASS
                ? executor.GetTypeDefinitionFromIl2CppType(type)
                : null;
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or IOException)
        {
            // A definition can be unavailable when an interface references unmapped metadata.
            return null;
        }
    }

    private readonly record struct InterfaceLayout(List<JsonDumpInterfaceMethod>? Methods, bool Complete);
}
