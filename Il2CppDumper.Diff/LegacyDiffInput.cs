using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace Il2CppDumper.Diff;

internal sealed class LegacyDiffInput
{
    private const string FolderName = "DummyDll";
    private const string HelperName = "Il2CppDummyDll";
    private const int MaxNameDepth = 64;

    internal LegacyDiffEndpoint Endpoint { get; }
    internal Dictionary<string, ImageTypes> Images { get; } = new(StringComparer.Ordinal);

    private LegacyDiffInput(LegacyDiffEndpoint endpoint) => Endpoint = endpoint;

    internal static LegacyDiffInput Read(string input, string side)
    {
        if (File.Exists(input))
            return ReadJson(input, side);

        if (!Directory.Exists(input))
            throw new DirectoryNotFoundException($"Input {side} path not found: {input}");

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(input));
        if (Path.GetFileName(root) != FolderName && File.Exists(Path.Combine(root, "dump.json")))
            return ReadJson(root, side);
        var folder = Path.Combine(root, FolderName);
        if (Path.GetFileName(root) == FolderName)
        {
            folder = root;
            root = Path.GetDirectoryName(root)!;
        }
        if (!Directory.Exists(folder))
            throw new InvalidDataException($"No dump.json or DummyDll directory in {side} input '{input}'. Use a dump folder containing managed DummyDll assemblies.");

        var inventory = new DiffArtifactInventory(root, side, "dump.json");
        if (inventory.HasListedDeclarations && !File.Exists(Path.Combine(root, "dump.json")))
            throw new InvalidDataException($"{side} dump.json is missing but listed in {RunManifestWriter.FileName}.");

        var source = new LegacyDiffInput(new LegacyDiffEndpoint
        {
            source = folder, format = "DummyDll", manifestStatus = inventory.HasManifest ? "verified" : "absent"
        });
        var files = inventory.HasManifest
            ? inventory.OutputPaths.Where(path => path.StartsWith(FolderName + "/", StringComparison.Ordinal))
                .Select(path => path[(FolderName.Length + 1)..])
            : Directory.EnumerateFiles(folder).Select(Path.GetFileName).OfType<string>();

        foreach (var name in files.Where(IsAssemblyFile).OrderBy(name => name, StringComparer.Ordinal))
        {
            var relative = FolderName + "/" + name;
            var bytes = inventory.HasManifest
                ? inventory.ReadListedFile(relative)
                : File.ReadAllBytes(Path.Combine(folder, name));
            source.ReadAssembly(name, relative, bytes, side);
        }
        if (source.Images.Count == 0)
            throw new InvalidDataException($"No source image DLLs found in {side} '{folder}'. Supply DummyDll output and include its DLLs in any accompanying manifest.");
        return source;

        static bool IsAssemblyFile(string name) => name.Length > 0 && name is not ("." or "..") &&
            name.IndexOfAny(new[] { '/', '\\', ':' }) < 0 &&
            (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || !Path.HasExtension(name));
    }

    private static LegacyDiffInput ReadJson(string input, string side)
    {
        var (inventory, artifacts) = DumpDiffer.ReadDeclarations(input, side);
        var endpoint = new LegacyDiffEndpoint
        {
            source = artifacts.Endpoint.source, sha256 = artifacts.Endpoint.sha256, format = "dump.json",
            declarationsPath = Path.GetFullPath(Directory.Exists(input) ? Path.Combine(input, "dump.json") : input),
            manifestStatus = !inventory.HasManifest ? "absent" : inventory.HasUnlistedDeclarations ? "unlisted" : "verified"
        };
        var source = new LegacyDiffInput(endpoint);
        var declarations = Flatten(artifacts.Model.types).ToArray();
        if (artifacts.Model.ImageSelection is { } selection)
        {
            foreach (var image in DeclarationCoverage.Validate(selection, declarations, side))
                source.Images.Add(image, new ImageTypes());
        }
        var imageIds = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var type in declarations)
        {
            if (string.IsNullOrWhiteSpace(type.image))
            {
                endpoint.typesWithoutImage++;
                continue;
            }
            if (type.imageId is >= 0 and int id && !imageIds.TryAdd(type.image, id) && imageIds[type.image] != id)
                throw new InvalidDataException($"Ambiguous {side} dump.json image '{type.image}': multiple image IDs share its name.");
            if (!source.Images.TryGetValue(type.image, out var types))
                source.Images.Add(type.image, types = new ImageTypes());
            var name = string.IsNullOrWhiteSpace(type.fullName) ? null : type.fullName;
            types.Add(name, type.typeToken, reconstructed: null, $"{side} dump.json image '{type.image}'");
        }
        return source;

        static IEnumerable<JsonDumpType> Flatten(IEnumerable<JsonDumpType> types)
        {
            foreach (var type in types)
            {
                yield return type;
                if (type.nested != null)
                {
                    foreach (var child in Flatten(type.nested))
                        yield return child;
                }
            }
        }
    }

    private void ReadAssembly(string fileName, string relative, byte[] bytes, string side)
    {
        try
        {
            using var pe = new PEReader(new MemoryStream(bytes, writable: false));
            if (!pe.HasMetadata)
                throw new InvalidDataException("expected managed metadata.");
            var reader = pe.GetMetadataReader(MetadataReaderOptions.None);
            if (!reader.IsAssembly)
                throw new InvalidDataException("expected a managed assembly.");
            var image = reader.GetString(reader.GetModuleDefinition().Name);
            if (image.Length == 0 || (image != fileName && image + ".dll" != fileName))
                throw new InvalidDataException($"module name '{image}' differs from the file name. Restore the original DummyDll file name.");
            if (image == HelperName + ".dll" && reader.GetString(reader.GetAssemblyDefinition().Name) == HelperName)
                return;

            var types = new ImageTypes();
            if (!Images.TryAdd(image, types))
                throw new InvalidDataException($"duplicate image '{image}'.");

            var names = new Dictionary<TypeDefinitionHandle, string?>();
            foreach (var handle in reader.TypeDefinitions)
            {
                var type = reader.GetTypeDefinition(handle);
                var token = OriginalToken(reader, type);
                if (token == null && reader.GetString(type.Name) == "<Module>" && type.GetDeclaringType().IsNil)
                    continue;
                types.Add(token.HasValue ? NameOf(handle, 0) : null, token, (uint)MetadataTokens.GetToken(handle), image);
            }
            Endpoint.assemblies.Add(new LegacyDllArtifact(relative, image, Convert.ToHexString(SHA256.HashData(bytes))));

            string? NameOf(TypeDefinitionHandle handle, int depth)
            {
                if (depth >= MaxNameDepth)
                    return null; // Also bounds cyclic declaring types in older reconstructed assemblies.
                if (names.TryGetValue(handle, out var cached))
                    return cached;
                var definition = reader.GetTypeDefinition(handle);
                var parent = definition.GetDeclaringType();
                var rawName = reader.GetString(definition.Name);
                string? prefix = parent.IsNil ? reader.GetString(definition.Namespace) : NameOf(parent, depth + 1);
                if (prefix == null || string.IsNullOrWhiteSpace(rawName))
                    return names[handle] = null;
                var ownName = GenericName(reader, definition, rawName, parent);
                return names[handle] = prefix.Length == 0 ? ownName : prefix + "." + ownName;
            }
        }
        catch (Exception ex) when (ex is BadImageFormatException or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            throw new InvalidDataException($"Invalid {side} {relative}: {ex.Message}", ex);
        }
    }

    private static string GenericName(MetadataReader reader, TypeDefinition type, string name, TypeDefinitionHandle parent)
    {
        var parameters = type.GetGenericParameters();
        var tick = name.LastIndexOf('`');
        var explicitArity = tick >= 0 && int.TryParse(name.AsSpan(tick + 1), NumberStyles.None,
            CultureInfo.InvariantCulture, out var arity) ? arity : -1;
        if (parameters.Count == 0)
            return name;
        var prefix = explicitArity >= 0 ? name[..tick] : name;
        var inherited = parent.IsNil ? 0 : reader.GetTypeDefinition(parent).GetGenericParameters().Count;
        var start = explicitArity >= 0 && parameters.Count >= explicitArity
            ? (parent.IsNil ? 0 : parameters.Count - explicitArity)
            : inherited;
        var count = explicitArity >= 0 ? Math.Min(explicitArity, parameters.Count - start) : parameters.Count - start;
        return count <= 0 ? prefix : prefix + "<" + string.Join(", ", parameters.Skip(start).Take(count)
            .Select(parameter => reader.GetString(reader.GetGenericParameter(parameter).Name))) + ">";
    }

    private static uint? OriginalToken(MetadataReader reader, TypeDefinition type)
    {
        uint? token = null;
        foreach (var handle in type.GetCustomAttributes())
        {
            var attribute = reader.GetCustomAttribute(handle);
            if (attribute.Constructor.Kind != HandleKind.MemberReference)
                continue;
            var constructor = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
            if (constructor.Parent.Kind != HandleKind.TypeReference)
                continue;
            var reference = reader.GetTypeReference((TypeReferenceHandle)constructor.Parent);
            if ((!reference.Namespace.IsNil && !reader.StringComparer.Equals(reference.Namespace, HelperName)) ||
                !reader.StringComparer.Equals(reference.Name, "TokenAttribute") ||
                reference.ResolutionScope.Kind != HandleKind.AssemblyReference ||
                !reader.StringComparer.Equals(reader.GetAssemblyReference((AssemblyReferenceHandle)reference.ResolutionScope).Name, HelperName))
                continue;

            var signature = reader.GetBlobReader(constructor.Signature);
            var blob = reader.GetBlobReader(attribute.Value);
            // Parameterless .ctor and one named string field/property are the DummyDll token format.
            if (token.HasValue || !reader.StringComparer.Equals(constructor.Name, ".ctor") ||
                signature.Length != 3 || signature.ReadByte() != 0x20 || signature.ReadByte() != 0 || signature.ReadByte() != 1 ||
                blob.ReadUInt16() != 1 || blob.ReadUInt16() != 1 ||
                blob.ReadByte() is not (0x53 or 0x54) || blob.ReadByte() != 0x0e || blob.ReadSerializedString() != "Token" ||
                blob.ReadSerializedString() is not { } value || !value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
                !uint.TryParse(value.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var original) ||
                !IsTypeToken(original) || blob.RemainingBytes != 0)
                throw new InvalidDataException($"Malformed or duplicate Il2CppDummyDll.TokenAttribute on '{reader.GetString(type.Name)}'.");
            token = original;
        }
        return token;
    }

    private static bool IsTypeToken(uint token) => token is > 0x02000000 and <= 0x02FFFFFF;

    internal sealed class ImageTypes
    {
        public Dictionary<uint, LegacyTypeIdentity> Tokens { get; } = new();
        public int TypeCount { get; private set; }
        public int WithoutToken => TypeCount - Tokens.Count;

        public void Add(string? name, uint? token, uint? reconstructed, string source)
        {
            TypeCount++;
            if (token is not uint original || !IsTypeToken(original))
                return;
            var identity = new LegacyTypeIdentity(name, $"0x{original:X8}", reconstructed is uint rid ? $"0x{rid:X8}" : null);
            if (!Tokens.TryAdd(original, identity))
                throw new InvalidDataException($"Duplicate original TypeDef token {identity.originalToken} in '{source}'.");
        }
    }
}
