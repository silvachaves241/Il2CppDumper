using System.Buffers.Binary;
using Il2CppDumper.Tests.Synthetic;
using Xunit;

namespace Il2CppDumper.Tests;

public class MetadataSectionLayoutTests
{
    [Theory]
    [InlineData(5, "methods", 25, 1)]
    [InlineData(5, "methods", 0, 1)]
    [InlineData(21, "assemblies", 70, 1)]
    [InlineData(23, "referencedAssemblies", 8, 1)]
    public void V39_RejectsIncompatibleFixedSectionBeforeReading(int sectionIndex, string section, int size, int count)
    {
        var bytes = SyntheticMetadataGenerator.Generate(MetadataVersionFamily.V39, seed: 42);
        var descriptor = bytes.AsSpan(8 + sectionIndex * 12, 12);
        var offset = BinaryPrimitives.ReadInt32LittleEndian(descriptor);
        BinaryPrimitives.WriteInt32LittleEndian(descriptor[4..], size);
        BinaryPrimitives.WriteInt32LittleEndian(descriptor[8..], count);
        Array.Resize(ref bytes, Math.Max(bytes.Length, offset + size));

        var error = Assert.Throws<InvalidDataException>(() => new Metadata(new MemoryStream(bytes)));
        Assert.Contains($"'{section}'", error.Message);
        Assert.Contains("v39", error.Message);
        Assert.Contains($"offset {offset}", error.Message);
        Assert.Contains($"size {size}", error.Message);
        Assert.Contains($"count {count}", error.Message);
        Assert.Contains("width", error.Message);
    }

    [Theory]
    [InlineData(106, 32, 14, "genericContainers", 2)]
    [InlineData(108, 41, 37, "invokerIndices", 3)]
    public void CompactFormat_RejectsAWidthItsReaderCannotConsume(
        int version, int sections, int sectionIndex, string section, int width)
    {
        var headerSize = 8 + sections * 12;
        var bytes = new byte[headerSize + width];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0xFAB11BAF);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), version);
        var descriptor = bytes.AsSpan(8 + sectionIndex * 12, 12);
        BinaryPrimitives.WriteInt32LittleEndian(descriptor, headerSize);
        BinaryPrimitives.WriteInt32LittleEndian(descriptor[4..], width);
        BinaryPrimitives.WriteInt32LittleEndian(descriptor[8..], 1);

        var error = Assert.Throws<InvalidDataException>(() => new Metadata(new MemoryStream(bytes)));
        Assert.Contains($"'{section}'", error.Message);
        Assert.Contains($"v{version}", error.Message);
    }

    [Fact]
    public void V39_UsesFourBytesForPrimitivesAndIgnoresVariablePoolCounts()
    {
        var bytes = SyntheticMetadataGenerator.Generate(MetadataVersionFamily.V39, seed: 42);
        var payloadOffset = bytes.Length;
        Array.Resize(ref bytes, payloadOffset + 8);
        var vtableDescriptor = bytes.AsSpan(8 + 17 * 12, 12);
        BinaryPrimitives.WriteInt32LittleEndian(vtableDescriptor, payloadOffset);
        BinaryPrimitives.WriteInt32LittleEndian(vtableDescriptor[4..], 8);
        BinaryPrimitives.WriteInt32LittleEndian(vtableDescriptor[8..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(payloadOffset), 0x60000001);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(payloadOffset + 4), 0xC0000003);
        // The string pool's entry count is not its byte size.
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8 + 2 * 12 + 8), 1);

        using var metadata = new Metadata(new MemoryStream(bytes));
        Assert.Equal(4, metadata.SizeOf(typeof(uint)));
        Assert.Equal(4, metadata.SizeOf(typeof(int)));
        Assert.Equal(new uint[] { 0x60000001, 0xC0000003 }, metadata.vtableMethods);
    }
}
