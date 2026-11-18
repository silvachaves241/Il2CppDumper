#nullable enable

namespace Il2CppDumper;

internal static class VTableMethodResolver
{
    internal static (int? MethodDefIndex, int? MethodSpecIndex) Resolve(
        Metadata metadata, Il2Cpp il2Cpp, Il2CppTypeDefinition type, int position)
    {
        var tableIndex = (long)type.vtableStart + position;
        if (type.vtableStart < 0 || (uint)position >= type.vtable_count || tableIndex >= metadata.vtableMethods.Length)
            return default;

        var encoded = metadata.vtableMethods[(int)tableIndex];
        var usage = (Il2CppMetadataUsage)metadata.GetEncodedIndexTypeForVersion(encoded);
        var index = (int)metadata.GetDecodedMethodIndex(encoded);
        if (usage == Il2CppMetadataUsage.kIl2CppMetadataUsageMethodDef)
            return (index < metadata.methodDefs.Length ? index : null, null);

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
}
