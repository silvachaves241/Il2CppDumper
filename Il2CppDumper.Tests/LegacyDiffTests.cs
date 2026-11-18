using System.Security.Cryptography;
using System.Text.Json;
using Il2CppDumper.Diff;
using Mono.Cecil;
using Xunit;

namespace Il2CppDumper.Tests;

public sealed class LegacyDiffTests : IDisposable
{
    private readonly DirectoryInfo temp = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"il2cpp-legacy-{Guid.NewGuid():N}"));
    private string Left => Path.Combine(temp.FullName, "left");
    private string Right => Path.Combine(temp.FullName, "right");

    [Fact]
    public void ComparesSharedImagesAndOriginalTokensWithDistinctReconstructedTokens()
    {
        var dummy = WriteDummy(Left, "A.dll", ("Holder`1", "0x02000007"), ("Nested", "0x02000009"),
            ("WithoutAnnotation", null), ("LeftOnly", "0x0200000C"));
        WriteDummy(Left, "LegacyOnly.dll", ("OldImageType", "0x02000007"));
        WriteJson(Right,
            Type("A.dll", "Namespace.Holder<T>", 0x02000007, Type("A.dll", "Namespace.Holder<T>.Updated", 0x02000009)),
            Type("A.dll", "RightOnly", 0x0200000D), Type("ModernOnly.dll", "OtherImageType", 0x02000007));

        using var log = new StringWriter();
        var output = Path.Combine(temp.FullName, "out");
        Assert.Equal(1, LegacyDiff.Execute(Path.GetDirectoryName(dummy)!, Right, output, log));
        Assert.Contains("Coverage is partial", log.ToString());
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "legacy-diff.json")));
        var report = document.RootElement;
        Assert.Equal("partial", report.GetProperty("coverage").GetString());
        Assert.Equal("DummyDll", report.GetProperty("left").GetProperty("format").GetString());
        Assert.Equal("dump.json", report.GetProperty("right").GetProperty("format").GetString());
        Assert.Equal(2, report.GetProperty("comparedTokens").GetInt32());
        var images = report.GetProperty("images").EnumerateArray().ToArray();
        var shared = images[0];
        Assert.Equal("A.dll", shared.GetProperty("image").GetString());
        Assert.Equal(1, shared.GetProperty("sameNames").GetInt32());
        Assert.Equal(1, shared.GetProperty("leftTypesWithoutOriginalToken").GetInt32());
        var change = Assert.Single(shared.GetProperty("nameDifferences").EnumerateArray());
        Assert.Equal("0x02000009", change.GetProperty("originalToken").GetString());
        Assert.Equal("0x02000003", change.GetProperty("left").GetProperty("reconstructedToken").GetString());
        Assert.Equal("Namespace.Holder<T>.Nested", change.GetProperty("left").GetProperty("name").GetString());
        Assert.False(change.GetProperty("right").TryGetProperty("reconstructedToken", out _));
        Assert.Single(shared.GetProperty("leftOnlyTokens").EnumerateArray());
        Assert.Single(shared.GetProperty("rightOnlyTokens").EnumerateArray());
        Assert.Equal("left-only-image", images[1].GetProperty("status").GetString());
        Assert.Equal("right-only-image", images[2].GetProperty("status").GetString());
    }

    [Fact]
    public void MatchingPartialCoverageReturnsZeroDespiteUnavailableComparisons()
    {
        WriteDummy(Left, "A.dll", ("Same", "0x02000012"), ("Unannotated", null));
        WriteDummy(Right, "A.dll", ("Same", "0x02000012"));
        WriteDummy(Right, "B.dll", ("Other", "0x02000012"));
        using var log = new StringWriter();
        var output = Path.Combine(temp.FullName, "report.json");
        Assert.Equal(0, LegacyDiff.Execute(Left, Right, output, log));
        Assert.Contains("No differences found in compared tokens", log.ToString());
        Assert.Contains("Coverage is partial", log.ToString());
        var report = LegacyDiff.Compare(Left, Right);
        Assert.Contains("field-offset-drift", report.unavailable);
        Assert.Equal(1, report.images[0].leftTypesWithoutOriginalToken);
        Assert.Equal("right-only-image", report.images[1].status);
    }

    [Theory]
    [InlineData("missing annotation", "No shared original TypeDef tokens")]
    [InlineData("different original token", "No shared original TypeDef tokens")]
    [InlineData("duplicate annotation", "Duplicate original TypeDef token")]
    [InlineData("invalid token", "Malformed or duplicate Il2CppDummyDll.TokenAttribute")]
    [InlineData("invalid DLL", "Invalid left DummyDll/A.dll")]
    [InlineData("invalid JSON", "missing 'types' array")]
    public void InvalidOrInsufficientInputsPreserveExistingReport(string scenario, string expected)
    {
        var dll = scenario switch
        {
            "missing annotation" => WriteDummy(Left, "A.dll", ("Same", null)),
            "different original token" => WriteDummy(Left, "A.dll", ("Same", "0x02000003")),
            "duplicate annotation" => WriteDummy(Left, "A.dll", ("Same", "0x02000002"), ("Other", "0x02000002")),
            "invalid token" => WriteDummy(Left, "A.dll", ("Same", "0x06000002")),
            _ => WriteDummy(Left, "A.dll", ("Same", "0x02000002"))
        };
        if (scenario == "invalid DLL")
            File.WriteAllText(dll, "not a managed DLL");
        if (scenario == "invalid JSON")
            File.WriteAllText(Path.Combine(Left, "dump.json"), "{}");
        WriteJson(Right, Type("A.dll", "Namespace.Same", 0x02000002));
        var output = Path.Combine(temp.FullName, "report.json");
        File.WriteAllText(output, "existing");
        using var log = new StringWriter();
        Assert.Equal(2, LegacyDiff.Execute(Left, Right, output, log));
        Assert.Contains(expected, log.ToString());
        Assert.Equal("existing", File.ReadAllText(output));
        Assert.Equal(scenario == "invalid JSON", File.Exists(Path.Combine(Left, "dump.json")));
    }

    [Theory]
    [InlineData("__Generated")]
    [InlineData("__Generated.dll")]
    public void AcceptsExtensionlessImageWithItsOriginalNameOrDllSuffix(string fileName)
    {
        var image = "__Generated";
        var file = WriteDummyWithTokenNamespace(Left, image, "", ("Same", "0x02000007"));
        if (fileName != image)
            File.Move(file, Path.Combine(Path.GetDirectoryName(file)!, fileName));
        WriteJson(Right, Type(image, "Namespace.Same", 0x02000007));
        var report = LegacyDiff.Compare(Left, Right);
        Assert.Equal(1, report.comparedTokens);
        Assert.Equal(image, Assert.Single(report.images).image);
        Assert.False(report.HasDifferences);
    }

    [Fact]
    public void UsesOnlyListedDllsAndVerifiesTheirSnapshot()
    {
        var dll = WriteDummy(Left, "A.dll", ("Same", "0x02000007"));
        var bytes = File.ReadAllBytes(dll);
        var manifest = Path.Combine(Left, RunManifestWriter.FileName);
        var outputs = new List<RunManifestOutput>
        {
            new() { Path = "DummyDll/A.dll", Bytes = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)) }
        };
        File.WriteAllText(manifest, JsonSerializer.Serialize(new { outputs }));
        var stale = Path.Combine(Left, "DummyDll", "Stale.dll");
        File.WriteAllText(stale, "unlisted");
        WriteJson(Right, Type("A.dll", "Namespace.Same", 0x02000007));
        var report = LegacyDiff.Compare(Left, Right);
        Assert.Equal("verified", report.left.manifestStatus);
        Assert.Equal(outputs[0].Sha256, Assert.Single(report.left.assemblies).sha256);
        Assert.False(report.HasDifferences);

        outputs.Add(new RunManifestOutput { Path = "dump.json", Sha256 = "missing" });
        File.WriteAllText(manifest, JsonSerializer.Serialize(new { outputs }));
        Assert.Contains("dump.json is missing but listed", Assert.Throws<InvalidDataException>(() => LegacyDiff.Compare(Left, Right)).Message);
        outputs.RemoveAt(1);
        File.WriteAllText(manifest, JsonSerializer.Serialize(new { outputs }));
        bytes[^1] ^= 1;
        File.WriteAllBytes(dll, bytes);
        Assert.Contains("SHA-256 mismatch", Assert.Throws<InvalidDataException>(() => LegacyDiff.Compare(Left, Right)).Message);
        File.Delete(dll);
        Assert.Contains("A.dll is missing but listed", Assert.Throws<InvalidDataException>(() => LegacyDiff.Compare(Left, Right)).Message);
    }

    public void Dispose() => temp.Delete(recursive: true);

    private static string WriteDummy(string root, string image, params (string Name, string? Token)[] definitions)
        => WriteDummyWithTokenNamespace(root, image, "Il2CppDummyDll", definitions);

    private static string WriteDummyWithTokenNamespace(string root, string image, string attributeNamespace,
        params (string Name, string? Token)[] definitions)
    {
        using var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition(Path.GetFileNameWithoutExtension(image),
            new Version(1, 0)), image, ModuleKind.Dll);
        var module = assembly.MainModule;
        var helper = new AssemblyNameReference("Il2CppDummyDll", new Version(1, 0));
        module.AssemblyReferences.Add(helper);
        var attributeType = new TypeReference(attributeNamespace, "TokenAttribute", module, helper);
        var constructor = new MethodReference(".ctor", module.TypeSystem.Void, attributeType) { HasThis = true };
        TypeDefinition? holder = null;
        foreach (var (name, token) in definitions)
        {
            var type = new TypeDefinition("Namespace", name, Mono.Cecil.TypeAttributes.Public, module.TypeSystem.Object);
            if (name == "Nested")
            {
                type.Namespace = "";
                type.Attributes = Mono.Cecil.TypeAttributes.NestedPublic;
                type.GenericParameters.Add(new GenericParameter("T", type));
                holder!.NestedTypes.Add(type);
            }
            else
            {
                module.Types.Add(type);
                if (name == "Holder`1")
                {
                    holder = type;
                    holder.GenericParameters.Add(new GenericParameter("T", holder));
                }
            }
            if (token != null)
            {
                var attribute = new CustomAttribute(constructor);
                attribute.Fields.Add(new CustomAttributeNamedArgument("Token", new CustomAttributeArgument(module.TypeSystem.String, token)));
                type.CustomAttributes.Add(attribute);
            }
        }
        var path = Path.Combine(Directory.CreateDirectory(Path.Combine(root, "DummyDll")).FullName, image);
        assembly.Write(path);
        return path;
    }

    private static void WriteJson(string root, params JsonDumpType[] types) =>
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(root).FullName, "dump.json"),
            JsonSerializer.Serialize(new JsonDumpModel { types = types.ToList() }));

    private static JsonDumpType Type(string image, string name, uint token, params JsonDumpType[] nested) =>
        new() { image = image, fullName = name, typeToken = token, nested = nested.ToList() };
}
