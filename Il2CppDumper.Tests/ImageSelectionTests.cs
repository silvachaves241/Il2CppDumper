using System.Text;
using System.Text.Json;
using Il2CppDumper.Core;
using Il2CppDumper.Tests.Synthetic;
using Mono.Cecil;
using Xunit;

namespace Il2CppDumper.Tests;

public sealed class ImageSelectionTests : IDisposable
{
    private readonly string root = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(root, true);

    [Fact]
    public void ScopedDeclarations_KeepCoordinatesAndResolveDependencyFromFullDummyDllSet()
    {
        using var metadata = CreateMetadata();
        using var binary = CreateBinary();
        var config = new Config { DumpAttribute = false, GenerateDummyDll = true, GenerateJsonDump = true, GenerateStruct = false };
        Assert.True(Dumper.ExecuteDump(metadata, binary, config, root, out var baselineError), baselineError);
        var splitDirectory = Path.Combine(root, "dump-by-image");
        var unselected = Path.Combine(splitDirectory, "000000-Dependency.dll.cs");
        var selectedFile = Path.Combine(splitDirectory, "000001-Selected.dll.cs");
        var originalSelected = File.ReadAllText(selectedFile);
        var originalUnselected = File.ReadAllText(unselected);
        var dllDirectory = Path.Combine(root, "DummyDll");
        var staleAssembly = Path.Combine(dllDirectory, "OldRun.dll");
        File.Copy(Path.Combine(dllDirectory, "Dependency.dll"), staleAssembly);

        config.ImageSelectors = new[] { "selected.DLL", "ID:01", "Selected.dll" };
        var selection = ImageSelection.Resolve(metadata, config.ImageSelectors);
        Assert.NotNull(selection);
        var selectedImage = Assert.Single(selection!.Resolved);
        Assert.Equal(1, selectedImage.ImageId);
        Assert.Equal(1, selectedImage.TypeStart);
        Assert.True(Dumper.ExecuteDump(metadata, binary, config, root, out var error, out var counts,
            out var warnings, out var files, selection: selection), error);

        Assert.Equal(1, counts.Types);
        Assert.Equal(originalSelected, File.ReadAllText(selectedFile));
        Assert.Equal(originalUnselected, File.ReadAllText(unselected));
        Assert.DoesNotContain(unselected, files);
        Assert.Contains(selectedFile, files);
        using (var index = JsonDocument.Parse(File.ReadAllText(Path.Combine(splitDirectory, "index.json"))))
        {
            Assert.Equal(2, index.RootElement.GetProperty("imageSelection").GetProperty("sourceImageCount").GetInt32());
            var entry = Assert.Single(index.RootElement.GetProperty("images").EnumerateArray());
            Assert.Equal(1, entry.GetProperty("imageId").GetInt32());
        }
        var aggregate = File.ReadAllText(Path.Combine(root, "dump.cs"));
        Assert.Contains("Selected declaration images: 1 of 2", aggregate);
        Assert.Contains("TypeDefIndex: 1 | ImageId: 1", aggregate);
        Assert.Contains("ChildType : BaseType", aggregate);
        Assert.DoesNotContain("TypeDefIndex: 0", aggregate);
        var model = JsonSerializer.Deserialize<JsonDumpModel>(File.ReadAllText(Path.Combine(root, "dump.json")))!;
        Assert.Equal(config.ImageSelectors, model.ImageSelection!.Requested.ToArray());
        var type = Assert.Single(model.types);
        Assert.Equal(1, type.imageId);
        Assert.Equal(1, type.typeDefIndex);
        Assert.Equal(0x02000017u, type.typeToken);
        Assert.Equal("BaseType", Assert.Single(type.bases));
        Assert.NotNull(Assert.Single(JsonDumpGenerator.Create(new Il2CppExecutor(metadata, binary), config).types).typeToken);

        using var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(dllDirectory);
        using var reopened = AssemblyDefinition.ReadAssembly(Path.Combine(dllDirectory, "Selected.dll"),
            new ReaderParameters { AssemblyResolver = resolver });
        var child = reopened.MainModule.GetType("ChildType");
        Assert.Equal("Dependency.dll", child.BaseType.Resolve().Module.Name);
        Assert.True(File.Exists(staleAssembly));
        Assert.DoesNotContain(staleAssembly, files);

        RunManifestWriter.Write(root, config, DateTime.UtcNow, 0, true, counts, warnings, files,
            inputs: null, outputValidation: null, selection: selection);
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, RunManifestWriter.FileName)));
        var recorded = manifest.RootElement.GetProperty("outputs").EnumerateArray()
            .ToDictionary(item => item.GetProperty("path").GetString()!, item => item.GetProperty("imageScope").GetString());
        Assert.Equal("selected-images", recorded["dump.cs"]);
        Assert.Equal("selected-images", recorded["dump.json"]);
        Assert.Equal("full-input", recorded["DummyDll/Dependency.dll"]);
        Assert.DoesNotContain("DummyDll/OldRun.dll", recorded.Keys);
        Assert.DoesNotContain("dump-by-image/000000-Dependency.dll.cs", recorded.Keys);
    }

    [Fact]
    public void CompleteJson_PreservesCoordinatesOfHomonymsAndNestedTypes()
    {
        using var metadata = CreateMetadata();
        using var binary = CreateBinary();

        var dependency = metadata.typeDefs[0];
        var selected = metadata.typeDefs[1];
        var childName = selected.nameIndex;
        selected.nameIndex = dependency.nameIndex;
        selected.token = dependency.token; // CLR tokens can repeat across images.
        selected.parentIndex = 2;

        metadata.imageDefs[1].typeCount = 3;
        metadata.typeDefs = new[]
        {
            dependency, selected,
            new Il2CppTypeDefinition
            {
                nameIndex = childName, byvalTypeIndex = 3, declaringTypeIndex = 1,
                parentIndex = 2, genericContainerIndex = -1, flags = 2, token = 0x02000017
            },
            new Il2CppTypeDefinition
            {
                nameIndex = dependency.nameIndex, byvalTypeIndex = 4, declaringTypeIndex = 3,
                parentIndex = 2, genericContainerIndex = -1, flags = 2, token = 0x02000021
            }
        };
        binary.types = binary.types.Concat(new[]
        {
            new Il2CppType { type = Il2CppTypeEnum.IL2CPP_TYPE_CLASS, data = new Il2CppType.Union { dummy = 2 } },
            new Il2CppType { type = Il2CppTypeEnum.IL2CPP_TYPE_CLASS, data = new Il2CppType.Union { dummy = 3 } }
        }).ToArray();

        var executor = new Il2CppExecutor(metadata, binary);
        var config = new Config { DumpAttribute = false };
        var path = Path.Combine(root, "dump.json");
        JsonDumpGenerator.Write(root, executor, config);
        var full = JsonSerializer.Deserialize<JsonDumpModel>(File.ReadAllText(path))!;
        Assert.Null(full.ImageSelection);
        Assert.Equal(2, full.types.Count);
        var dependencyType = Assert.Single(full.types.Where(type => type.image == "Dependency.dll"));
        var selectedType = Assert.Single(full.types.Where(type => type.image == "Selected.dll"));

        Assert.Equal(dependencyType.fullName, selectedType.fullName);
        Assert.Equal((0, 0, 0x02000001u), Coordinates(dependencyType));
        Assert.Equal((1, 1, 0x02000001u), Coordinates(selectedType));
        var child = Assert.Single(selectedType.nested);
        Assert.Equal("BaseType.ChildType", child.fullName);
        Assert.Equal((1, 2, 0x02000017u), Coordinates(child));
        var grandchild = Assert.Single(child.nested);
        Assert.Equal("BaseType.ChildType.BaseType", grandchild.fullName);
        Assert.Equal((1, 3, 0x02000021u), Coordinates(grandchild));

        config.ImageSelectors = new[] { "id:1" };
        JsonDumpGenerator.Write(root, executor, config);
        var scoped = JsonSerializer.Deserialize<JsonDumpModel>(File.ReadAllText(path))!;
        Assert.NotNull(scoped.ImageSelection);
        var scopedType = Assert.Single(scoped.types);
        var scopedChild = Assert.Single(scopedType.nested);
        Assert.Equal(Coordinates(selectedType), Coordinates(scopedType));
        Assert.Equal(Coordinates(child), Coordinates(scopedChild));
        Assert.Equal(Coordinates(grandchild), Coordinates(Assert.Single(scopedChild.nested)));

        static (int? ImageId, int? TypeDefIndex, uint? Token) Coordinates(JsonDumpType type) =>
            (type.imageId, type.typeDefIndex, type.typeToken);
    }

    [Fact]
    public void FailedSelectedSplit_ReportsFailureAndStillPublishesHealthySelectedOutputs()
    {
        using var metadata = CreateMetadata();
        using var binary = CreateBinary();
        var config = new Config
        {
            ImageSelectors = new[] { "id:1" },
            GenerateDummyDll = false,
            GenerateStruct = false,
            GenerateJsonDump = true,
            DumpAttribute = false
        };
        var splitDirectory = Path.Combine(root, "dump-by-image");
        var failed = Directory.CreateDirectory(Path.Combine(splitDirectory, "000001-Selected.dll.cs")).FullName;
        var stale = Path.Combine(splitDirectory, "000000-Dependency.dll.cs");
        File.WriteAllText(stale, "stale");

        Assert.False(Dumper.ExecuteDump(metadata, binary, config, root, out var error, out _, out _, out var files));
        Assert.Contains("publish declaration output for image 1", error);
        Assert.DoesNotContain(failed, files);
        Assert.DoesNotContain(stale, files);
        Assert.Equal("stale", File.ReadAllText(stale));
        Assert.Contains(Path.Combine(root, "dump.cs"), files);
        Assert.Contains(Path.Combine(root, "dump.json"), files);
        using var index = JsonDocument.Parse(File.ReadAllText(Path.Combine(splitDirectory, "index.json")));
        var entry = Assert.Single(index.RootElement.GetProperty("images").EnumerateArray());
        Assert.Equal("failed", entry.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("path").ValueKind);
    }

    [Theory]
    [InlineData("Missing.dll", "Unknown image name")]
    [InlineData("id:-1", "invalid image ID")]
    [InlineData("id:2", "invalid image ID")]
    [InlineData("id:", "invalid image ID")]
    [InlineData(" ", "exact names")]
    public void InvalidSelectors_AreRejected(string selector, string expected)
    {
        using var metadata = CreateMetadata();
        var ex = Assert.Throws<ArgumentException>(() => ImageSelection.Resolve(metadata, new[] { selector }));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void DuplicateNames_RequireIdsAndResolutionRetainsMetadataOrder()
    {
        using var metadata = CreateMetadata();
        metadata.imageDefs[0].nameIndex = metadata.imageDefs[1].nameIndex;
        var ex = Assert.Throws<ArgumentException>(() => ImageSelection.Resolve(metadata, new[] { "selected.DLL" }));
        Assert.Contains("id:0, id:1", ex.Message);
        var selection = ImageSelection.Resolve(metadata, new[] { "id:1", "id:0", "ID:1" })!;
        Assert.Equal(new[] { 0, 1 }, selection.Resolved.Select(selected => selected.ImageId).ToArray());
        Assert.Null(ImageSelection.Resolve(metadata, Array.Empty<string>()));
    }

    [Fact]
    public void BatchConfiguration_OverridesAndClearsInheritedImageSelection()
    {
        var config = new Config { ImageSelectors = new[] { "id:1" } };
        using var replace = JsonDocument.Parse("{\"ImageSelectors\":[\"id:0\"]}");
        using var clear = JsonDocument.Parse("{\"ImageSelectors\":[]}");
        using var clearWithNull = JsonDocument.Parse("{\"ImageSelectors\":null}");
        Assert.Equal(new[] { "id:0" }, BatchProcessor.MergeConfig(config, replace.RootElement).ImageSelectors);
        Assert.Empty(BatchProcessor.MergeConfig(config, clear.RootElement).ImageSelectors!);
        Assert.Null(BatchProcessor.MergeConfig(config, clearWithNull.RootElement).ImageSelectors);
        Assert.Equal(new[] { "id:1" }, config.ImageSelectors);
        Assert.NotSame(config.ImageSelectors, Dumper.CloneConfig(config).ImageSelectors);
    }

    private static Metadata CreateMetadata()
    {
        var bytes = new byte[4096];
        BitConverter.GetBytes(0xFAB11BAFu).CopyTo(bytes, 0);
        BitConverter.GetBytes(29).CopyTo(bytes, 4);
        var metadata = new Metadata(new MemoryStream(bytes));
        metadata.header.stringOffset = 512;
        metadata.header.stringSize = 512;
        metadata.Position = 513;
        uint AddName(string name)
        {
            var index = (uint)(metadata.Position - metadata.header.stringOffset);
            metadata.Writer.Write(Encoding.UTF8.GetBytes(name + "\0"));
            return index;
        }

        metadata.imageDefs = new[]
        {
            new Il2CppImageDefinition { nameIndex = AddName("Dependency.dll"), typeStart = 0, typeCount = 1, assemblyIndex = 0 },
            new Il2CppImageDefinition { nameIndex = AddName("Selected.dll"), typeStart = 1, typeCount = 1, assemblyIndex = 1 }
        };
        metadata.assemblyDefs = new[]
        {
            new Il2CppAssemblyDefinition { aname = new Il2CppAssemblyNameDefinition { nameIndex = AddName("Dependency"), major = 1 } },
            new Il2CppAssemblyDefinition { aname = new Il2CppAssemblyNameDefinition { nameIndex = AddName("Selected"), major = 1 } }
        };
        metadata.typeDefs = new[]
        {
            new Il2CppTypeDefinition { nameIndex = AddName("BaseType"), byvalTypeIndex = 0, declaringTypeIndex = -1, parentIndex = 2, genericContainerIndex = -1, token = 0x02000001, flags = 1 },
            new Il2CppTypeDefinition { nameIndex = AddName("ChildType"), byvalTypeIndex = 1, declaringTypeIndex = -1, parentIndex = 0, genericContainerIndex = -1, token = 0x02000017, flags = 1 }
        };
        return metadata;
    }

    private static SyntheticIl2Cpp CreateBinary() => new()
    {
        types = new[]
        {
            new Il2CppType { type = Il2CppTypeEnum.IL2CPP_TYPE_CLASS, data = new Il2CppType.Union { dummy = 0 } },
            new Il2CppType { type = Il2CppTypeEnum.IL2CPP_TYPE_CLASS, data = new Il2CppType.Union { dummy = 1 } },
            new Il2CppType { type = Il2CppTypeEnum.IL2CPP_TYPE_OBJECT }
        }
    };
}
