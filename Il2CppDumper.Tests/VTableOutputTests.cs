using System.Buffers.Binary;
using System.Text.Json;
using System.Text.RegularExpressions;
using Il2CppDumper.Tests.Synthetic;
using Xunit;
using static Il2CppDumper.Il2CppConstants;

namespace Il2CppDumper.Tests;

public sealed class VTableOutputTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void InterfaceOffsetRecordsUseTheMetadataTypeIndexWidth(int width)
    {
        const int headerSize = 8 + 31 * 12;
        var bytes = new byte[headerSize + width + sizeof(int)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0xFAB11BAF);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), 39);
        WriteSection(bytes, 18, headerSize, width + sizeof(int), 1);
        bytes[headerSize] = 42;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(headerSize + width), 1900);

        using var metadata = new Metadata(new MemoryStream(bytes, writable: false));
        Assert.Equal((42, 1900), Assert.Single(metadata.InterfaceOffsets));
    }

    [Fact]
    public void JsonPreservesPhysicalSlotsAndReceiverSpecificInterfaceDispatch()
    {
        using var metadata = CreateMetadata();
        using var binary = CreateBinary();
        var executor = new Il2CppExecutor(metadata, binary);
        var config = new Config { DumpAttribute = false };

        var full = RoundTrip(executor, config);
        Assert.Null(full.ImageSelection);
        Assert.Empty(full.types.Single(type => type.typeDefIndex == 0).vtable!);
        var receiver = full.types.Single(type => type.typeDefIndex == 2);
        var derived = full.types.Single(type => type.typeDefIndex == 3);
        var unavailable = Assert.Single(derived.nested);
        var baseMethod = Assert.Single(receiver.methods);
        var derivedMethod = Assert.Single(derived.methods);

        Assert.Equal(2, baseMethod.methodDefIndex);
        Assert.Equal(0x06000003u, baseMethod.methodToken);
        Assert.Equal(3, derivedMethod.methodDefIndex);
        Assert.Equal(0x06000004u, derivedMethod.methodToken);

        var receiverSlots = receiver.vtable!;
        Assert.Equal(new[] { 0, 1, 2, 3 }, receiverSlots.Select(slot => slot.position));
        Assert.Equal(new int?[] { 2, null, 2, 2 }, receiverSlots.Select(slot => slot.methodDefIndex));
        Assert.Equal("interface", receiverSlots[0].interfaceStatus);
        Assert.Equal(new[] { 0, 1 }, receiverSlots[0].interfaces!.Select(origin => origin.interfaceMethodDefIndex));
        Assert.False(receiverSlots[1].resolved);
        Assert.Equal("interface", receiverSlots[1].interfaceStatus);
        Assert.Equal(0, Assert.Single(receiverSlots[1].interfaces!).interfaceMethodDefIndex);
        Assert.Equal(2, receiverSlots[2].declaredSlot);
        Assert.Equal("interface", receiverSlots[2].interfaceStatus);
        Assert.Equal("class", receiverSlots[3].interfaceStatus);
        Assert.Equal(0, receiverSlots[3].methodSpecIndex);

        var derivedSlots = derived.vtable!;
        Assert.Equal(new int?[] { 3, 2, 3, null }, derivedSlots.Select(slot => slot.methodDefIndex));
        Assert.Equal(5, derivedSlots[3].methodSpecIndex);
        Assert.False(derivedSlots[3].resolved);
        Assert.Collection(baseMethod.interfaceDispatch!,
            link => AssertLink(link, 2, 0, 2, 0, 1),
            link => AssertLink(link, 2, 2, 2, 1),
            link => AssertLink(link, 3, 1, 2, 1));
        AssertLink(Assert.Single(derivedMethod.interfaceDispatch!), 3, 0, 2, 0);
        var nestedMethod = Assert.Single(unavailable.methods);
        Assert.Equal(4, nestedMethod.methodDefIndex);
        Assert.Equal(0x06000005u, nestedMethod.methodToken);
        var unknown = Assert.Single(unavailable.vtable!);
        Assert.False(unknown.resolved);
        Assert.Equal("unknown", unknown.interfaceStatus);
        Assert.Null(unknown.interfaces);

        config.ImageSelectors = new[] { "id:2" };
        var scoped = RoundTrip(executor, config);
        Assert.NotNull(scoped.ImageSelection);
        var scopedDerived = Assert.Single(scoped.types);
        Assert.Equal(3, scopedDerived.typeDefIndex);
        Assert.Equal(4, Assert.Single(Assert.Single(scopedDerived.nested).methods).methodDefIndex);
        Assert.Equal(2, scopedDerived.vtable![1].methodDefIndex); // Target declared outside the selected images.
        var retainedOrigin = Assert.Single(scopedDerived.vtable[1].interfaces!);
        Assert.Equal(1, retainedOrigin.interfaceTypeDefIndex);
        Assert.Equal(1, retainedOrigin.interfaceTypeIndex);
        Assert.Equal(0, retainedOrigin.interfaceSlot);
        AssertLink(Assert.Single(Assert.Single(scopedDerived.methods).interfaceDispatch!), 3, 0, 2, 0);

        config.ImageSelectors = new[] { "id:1" };
        var scopedBase = Assert.Single(RoundTrip(executor, config).types);
        Assert.Equal(new[] { 2, 2 }, Assert.Single(scopedBase.methods).interfaceDispatch!.Select(link => link.receiverTypeDefIndex));

        config.DumpMethod = false;
        var withMethodsDisabled = Assert.Single(RoundTrip(executor, config).types);
        Assert.Empty(withMethodsDisabled.methods);
        Assert.Equal(2, withMethodsDisabled.vtable![0].methodDefIndex);
        Assert.Equal(2, withMethodsDisabled.vtable[0].interfaces!.Count);
    }

    [Fact]
    public void HeaderPreservesRepeatedInterfaceSlotsAndInheritedTargets()
    {
        using var metadata = CreateMetadata();
        using var binary = CreateBinary();
        metadata.vtableMethods[7] = 0xC0000001; // Valid MethodSpec for the inherited implementation.
        metadata.methodDefs[3].nameIndex = metadata.imageDefs[0].nameIndex;
        var executor = new Il2CppExecutor(metadata, binary);
        var model = JsonDumpGenerator.Create(executor, new Config { DumpAttribute = false });
        var receiver = model.types.Single(type => type.typeDefIndex == 2);
        Assert.Equal("interface", receiver.vtable![0].interfaceStatus);
        Assert.Equal(2, receiver.vtable[0].methodDefIndex);

        var tables = WriteHeaderVTables(executor);
        Assert.Collection(tables,
            entries => Assert.Equal(new[] { "_0_SyntheticMethod", "_1_unknown", "_2_SyntheticMethod", "_3_SyntheticMethod" }, entries),
            entries => Assert.Equal(new[] { "_0_SyntheticImage_dll", "_1_SyntheticMethod", "_2_SyntheticImage_dll", "_3_SyntheticMethod" }, entries),
            entries => Assert.Equal(new[] { "_0_unknown" }, entries));
    }

    [Fact]
    public void HeaderKeepsUnresolvedPositionsIncludingTruncatedMetadata()
    {
        using var metadata = CreateMetadata();
        using var binary = CreateBinary();
        metadata.typeDefs[2].vtableStart = -1;
        metadata.typeDefs[3].vtable_count = 6;
        metadata.vtableMethods[8] = 0x7FFFFFFF; // MethodDef index out of range.
        metadata.methodDefs[3].slot = ushort.MaxValue;

        var tables = WriteHeaderVTables(new Il2CppExecutor(metadata, binary));
        Assert.Collection(tables,
            entries => Assert.Equal(new[] { "_0_unknown", "_1_unknown", "_2_unknown", "_3_unknown" }, entries),
            entries => Assert.Equal(new[] { "_0_SyntheticMethod", "_1_SyntheticMethod", "_2_SyntheticMethod", "_3_unknown", "_4_unknown", "_5_unknown" }, entries),
            entries => Assert.Equal(new[] { "_0_unknown" }, entries));
    }

    private static string[][] WriteHeaderVTables(Il2CppExecutor executor)
    {
        var output = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"il2cpp-vtable-{Guid.NewGuid():N}"));
        try
        {
            new StructGenerator(executor).WriteScript(output.FullName + Path.DirectorySeparatorChar);
            var header = File.ReadAllText(Path.Combine(output.FullName, "il2cpp.h"));
            return Regex.Matches(header, @"(?m)^struct SyntheticClass\w*_VTable \{\n([^}]+)\};")
                .Select(table => table.Groups[1].Value.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(line =>
                    {
                        const string prefix = "VirtualInvokeData ";
                        var declaration = line.Trim();
                        Assert.StartsWith(prefix, declaration);
                        Assert.EndsWith(";", declaration);
                        return declaration[prefix.Length..^1];
                    })
                    .ToArray())
                .ToArray();
        }
        finally
        {
            output.Delete(recursive: true);
        }
    }

    private static void AssertLink(JsonDumpInterfaceDispatch link, int receiver, int position, int slot,
        params int[] originMethodIndices)
    {
        Assert.Equal(receiver, link.receiverTypeDefIndex);
        Assert.Equal(position, link.position);
        Assert.Equal(slot, link.declaredSlot);
        Assert.Equal(originMethodIndices, link.interfaces.Select(origin => origin.interfaceMethodDefIndex));
    }

    private static JsonDumpModel RoundTrip(Il2CppExecutor executor, Config config)
    {
        var model = JsonDumpGenerator.Create(executor, config);
        return JsonSerializer.Deserialize<JsonDumpModel>(JsonSerializer.Serialize(model))!;
    }

    private static Metadata CreateMetadata()
    {
        var bytes = SyntheticMetadataGenerator.Generate(MetadataVersionFamily.V39, seed: 42);
        var interfaceStart = bytes.Length;
        var pairs = new (int TypeIndex, int Offset)[]
        {
            (0, 0), (1, 0), (0, 1), (1, 2), // Receiver, including an unresolved slot and two origins at slot zero.
            (0, 0), (1, 1),                // Derived override and inherited implementation.
            (90, 0)                       // The interface metadata is unavailable.
        };
        var pairSize = pairs.Length * 8;
        Array.Resize(ref bytes, interfaceStart + pairSize);
        WriteSection(bytes, 18, interfaceStart, pairSize, pairs.Length);
        for (var i = 0; i < pairs.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(interfaceStart + i * 8), pairs[i].TypeIndex);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(interfaceStart + i * 8 + 4), pairs[i].Offset);
        }

        var metadata = new Metadata(new MemoryStream(bytes));
        var imageName = metadata.imageDefs[0].nameIndex;
        var typeName = metadata.typeDefs[0].nameIndex;
        var methodName = metadata.methodDefs[0].nameIndex;
        metadata.imageDefs = new[]
        {
            new Il2CppImageDefinition { nameIndex = imageName, typeStart = 0, typeCount = 2 },
            new Il2CppImageDefinition { nameIndex = typeName, typeStart = 2, typeCount = 1 },
            new Il2CppImageDefinition { nameIndex = methodName, typeStart = 3, typeCount = 2 }
        };
        metadata.typeDefs = new[]
        {
            new Il2CppTypeDefinition { nameIndex = typeName, methodStart = 0, method_count = 1, flags = TYPE_ATTRIBUTE_INTERFACE, declaringTypeIndex = -1, parentIndex = -1, genericContainerIndex = -1 },
            new Il2CppTypeDefinition { nameIndex = typeName, methodStart = 1, method_count = 1, flags = TYPE_ATTRIBUTE_INTERFACE, declaringTypeIndex = -1, parentIndex = -1, genericContainerIndex = -1 },
            new Il2CppTypeDefinition { nameIndex = typeName, methodStart = 2, method_count = 1, vtableStart = 0, vtable_count = 4, interfaceOffsetsStart = 0, interface_offsets_count = 4, declaringTypeIndex = -1, parentIndex = -1, genericContainerIndex = -1 },
            new Il2CppTypeDefinition { nameIndex = typeName, methodStart = 3, method_count = 1, vtableStart = 4, vtable_count = 4, interfaceOffsetsStart = 4, interface_offsets_count = 2, declaringTypeIndex = -1, parentIndex = 2, genericContainerIndex = -1 },
            new Il2CppTypeDefinition { nameIndex = typeName, methodStart = 4, method_count = 1, vtableStart = 8, vtable_count = 1, interfaceOffsetsStart = 6, interface_offsets_count = 1, declaringTypeIndex = 3, parentIndex = -1, genericContainerIndex = -1, flags = 2 }
        };
        metadata.methodDefs = Enumerable.Range(0, 5).Select(index => new Il2CppMethodDefinition
        {
            nameIndex = methodName,
            token = 0x06000001u + (uint)index,
            declaringType = index,
            returnType = -1,
            genericContainerIndex = -1,
            flags = METHOD_ATTRIBUTE_VIRTUAL,
            slot = (ushort)(index < 2 ? 0 : 2)
        }).ToArray();
        metadata.vtableMethods = new uint[]
        {
            0x60000005, 1, 0x60000005, 0xC0000001,         // Receiver: direct MethodDef 2, unresolved, repeat, MethodSpec 0.
            0x60000007, 0x60000005, 0x60000007, 0xC000000B, // Derived: override, inherited, override, missing MethodSpec 5.
            0x40000001                                  // Type usage, not a MethodDef.
        };
        return metadata;
    }

    private static SyntheticIl2Cpp CreateBinary() => new(39)
    {
        types = Enumerable.Range(0, 5).Select(index => new Il2CppType
        {
            type = Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
            data = new Il2CppType.Union { dummy = (uint)index }
        }).ToArray(),
        methodSpecs = new[] { new Il2CppMethodSpec { methodDefinitionIndex = 2 } }
    };

    private static void WriteSection(byte[] bytes, int index, int offset, int size, int count)
    {
        var section = bytes.AsSpan(8 + index * 12, 12);
        BinaryPrimitives.WriteInt32LittleEndian(section, offset);
        BinaryPrimitives.WriteInt32LittleEndian(section[4..], size);
        BinaryPrimitives.WriteInt32LittleEndian(section[8..], count);
    }
}
