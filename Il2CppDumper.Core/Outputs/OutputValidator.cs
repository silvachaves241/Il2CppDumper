#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using Il2CppDumper.Core;

namespace Il2CppDumper
{
    internal static class OutputValidator
    {
        private const string DummyHelperAssemblyName = "Il2CppDummyDll";
        private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        private static readonly StringComparer PathComparer = StringComparer.FromComparison(PathComparison);

        private static string NormalizePath(string path) => path.Replace(Path.DirectorySeparatorChar, '/');

        internal static OutputValidationReport Validate(
            string outputDir, IEnumerable<string> writtenFiles, Metadata metadata, Il2Cpp binary)
        {
            var report = new OutputValidationReport();
            var inventory = writtenFiles
                .Select(path => NormalizePath(Path.GetRelativePath(outputDir, Path.GetFullPath(path, outputDir))))
                .Distinct(PathComparer)
                .OrderBy(path => path, StringComparer.Ordinal);
            AssemblyOwnership? ownership = null;
            foreach (var path in inventory)
            {
                bool isAssembly = path.StartsWith("DummyDll/", PathComparison);
                if (!isAssembly && !PathComparer.Equals(path, "script.json"))
                {
                    continue;
                }
                var artifact = new OutputArtifactValidation(path, isAssembly ? "assembly" : "script");
                report.Artifacts.Add(artifact);
                try
                {
                    using var stream = File.OpenRead(Path.Combine(outputDir, path));
                    if (isAssembly)
                    {
                        ownership ??= new AssemblyOwnership(metadata);
                        ValidateAssembly(stream, path["DummyDll/".Length..], ownership, artifact);
                    }
                    else
                    {
                        ValidateScript(stream, binary, artifact);
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException
                    or BadImageFormatException or ArgumentException or InvalidOperationException or JsonException)
                {
                    artifact.Error("artifactRead", path, e.Message);
                }
            }

            if (report.Artifacts.Count == 0)
            {
                report.Reason = "No generated assemblies or script.json were recorded for this run.";
            }
            else
            {
                report.Status = report.Artifacts.Any(a => a.Status == "failed") ? "failed"
                    : report.Artifacts.Any(a => a.Status == "partial") ? "partial" : "passed";
            }
            return report;
        }

        private static void ValidateAssembly(Stream stream, string modulePath, AssemblyOwnership ownership, OutputArtifactValidation artifact)
        {
            using var pe = new PEReader(stream, PEStreamOptions.PrefetchMetadata | PEStreamOptions.LeaveOpen);
            var reader = pe.GetMetadataReader(MetadataReaderOptions.None);
            var assemblyName = reader.GetString(reader.GetAssemblyDefinition().Name);
            var moduleName = NormalizePath(reader.GetString(reader.GetModuleDefinition().Name));
            if (!PathComparer.Equals(modulePath, moduleName))
            {
                artifact.Error("moduleName", "Module[1]", $"Module name '{moduleName}' differs from recorded path '{modulePath}'.");
            }

            ValidateMemberMap(pe, reader, TableIndex.PropertyMap, TableIndex.Property, TableIndex.PropertyPtr, artifact);
            ValidateMemberMap(pe, reader, TableIndex.EventMap, TableIndex.Event, TableIndex.EventPtr, artifact);
            ValidateNestedMap(pe, reader, artifact);
            // Walk the reopened TypeDef rows even when there are no source coordinates.
            artifact.Checks["typeDefinitions"] = reader.TypeDefinitions.Count;
            var countBeforeOwnership = artifact.ErrorCount;
            var exportedTypes = reader.TypeDefinitions.Select(handle => (Row: MetadataTokens.GetRowNumber(handle),
                Type: reader.GetTypeDefinition(handle))).ToArray();
            foreach (var (_, type) in exportedTypes)
            {
                _ = reader.GetString(type.Name);
                _ = reader.GetString(type.Namespace);
            }
            if (ownership.IsEmbeddedHelper(modulePath, assemblyName))
            {
                return;
            }
            if (countBeforeOwnership == 0)
            {
                ownership.Validate(modulePath, reader, exportedTypes, artifact);
            }
            else
            {
                artifact.Limitations.Add("Source ownership comparison skipped because assembly relationships are invalid.");
            }
        }

        private static void ValidateMemberMap(PEReader pe, MetadataReader reader, TableIndex map, TableIndex members,
            TableIndex pointers, OutputArtifactValidation artifact)
        {
            int rows = reader.GetTableRowCount(map);
            int memberCount = reader.GetTableRowCount(pointers);
            if (memberCount == 0)
            {
                memberCount = reader.GetTableRowCount(members);
            }
            artifact.Checks[map.ToString()] = rows;
            if (rows == 0)
            {
                if (memberCount > 0)
                {
                    artifact.Error("missingMemberMap", map.ToString(), $"{memberCount} member rows have no map.");
                }
                return;
            }

            int typeCount = reader.TypeDefinitions.Count;
            int parentWidth = typeCount < 65536 ? 2 : 4;
            int rowSize = reader.GetTableRowSize(map);
            var data = pe.GetMetadata().GetReader(reader.GetTableMetadataOffset(map), checked(rows * rowSize));
            var parents = new HashSet<uint>();
            uint previousStart = 0;
            for (int row = 1; row <= rows; row++)
            {
                var parent = ReadIndex(ref data, parentWidth);
                var start = ReadIndex(ref data, rowSize - parentWidth);
                if (parent == 0 || parent > typeCount || !parents.Add(parent))
                {
                    artifact.Error("invalidMapParent", $"{map}[{row}].Parent",
                        $"TypeDef row {parent} is out of range or already has a map parent row.");
                }
                if (start == 0 || start > memberCount || start <= previousStart || (row == 1 && start != 1))
                {
                    artifact.Error("invalidMemberList", $"{map}[{row}].{members}List", $"Invalid member list start row {start}.");
                }
                previousStart = start;
            }
        }

        private static void ValidateNestedMap(PEReader pe, MetadataReader reader, OutputArtifactValidation artifact)
        {
            int rows = reader.GetTableRowCount(TableIndex.NestedClass);
            artifact.Checks["NestedClass"] = rows;
            if (rows == 0)
            {
                return;
            }
            int rowSize = reader.GetTableRowSize(TableIndex.NestedClass);
            var data = pe.GetMetadata().GetReader(reader.GetTableMetadataOffset(TableIndex.NestedClass), checked(rows * rowSize));
            var parents = new int[reader.TypeDefinitions.Count + 1];
            for (int row = 1; row <= rows; row++)
            {
                uint child = ReadIndex(ref data, rowSize / 2);
                uint parent = ReadIndex(ref data, rowSize / 2);
                if (child == 0 || child >= parents.Length || parent == 0 || parent >= parents.Length || parents[child] != 0)
                {
                    artifact.Error("invalidNestedOwner", $"NestedClass[{row}]", $"TypeDef rows {child} and {parent} are out of range or the child was already assigned.");
                    continue;
                }
                parents[child] = (int)parent;
            }

            var visited = new byte[parents.Length];
            for (int child = 1; child < parents.Length; child++)
            {
                int parent = child;
                while (parent != 0 && visited[parent] == 0)
                {
                    visited[parent] = 1;
                    parent = parents[parent];
                }
                if (parent != 0 && visited[parent] == 1)
                {
                    artifact.Error("nestedOwnershipCycle", $"TypeDef[{parent}]", "Declaring types form a cycle.");
                }
                parent = child;
                while (parent != 0 && visited[parent] == 1)
                {
                    visited[parent] = 2;
                    parent = parents[parent];
                }
            }
        }

        private static uint ReadIndex(ref BlobReader reader, int width) => width switch
        {
            2 => reader.ReadUInt16(),
            4 => reader.ReadUInt32(),
            _ => throw new BadImageFormatException($"Unexpected metadata index width {width}.")
        };

        private sealed class ScriptAddresses
        {
            public List<ScriptMethodAddress>? ScriptMethod { get; set; }
            public List<ScriptUsageAddress>? ScriptMetadataMethod { get; set; }
            public ulong[]? Addresses { get; set; }
        }

        private sealed class ScriptMethodAddress
        {
            public ulong? Address { get; set; }
            public string? Name { get; set; }
        }

        private sealed class ScriptUsageAddress
        {
            public ulong? MethodAddress { get; set; }
            public string? Name { get; set; }
        }

        private static void ValidateScript(Stream stream, Il2Cpp binary, OutputArtifactValidation artifact)
        {
            var script = JsonSerializer.Deserialize<ScriptAddresses>(stream);
            if (script?.ScriptMethod == null || script.ScriptMetadataMethod == null || script.Addresses == null)
            {
                artifact.Error("scriptStructure", "$", "Expected ScriptMethod, ScriptMetadataMethod and Addresses arrays.");
                return;
            }
            artifact.Checks["scriptJson"] = 1;
            if (binary is not PE pe)
            {
                artifact.Limitations.Add("Native code RVA mapping is implemented for PE inputs only.");
                return;
            }

            int checkedAddresses = 0;
            void Check(ulong? rva, string array, int index, string field = "", string? method = null)
            {
                checkedAddresses++;
                if (rva is not ulong value || !pe.IsExecutableRva(value))
                {
                    string address = rva.HasValue ? $"RVA 0x{rva:X}" : "Missing RVA";
                    artifact.Error("invalidMethodRva", $"{array}[{index}]{field}",
                        $"{address} does not resolve to backed executable PE data{(method == null ? "" : $" ({method})")}.");
                }
            }

            for (int i = 0; i < script.ScriptMethod.Count; i++)
            {
                var method = script.ScriptMethod[i];
                Check(method?.Address, "ScriptMethod", i, ".Address", method?.Name);
            }
            for (int i = 0; i < script.ScriptMetadataMethod.Count; i++)
            {
                var usage = script.ScriptMetadataMethod[i];
                if (usage?.MethodAddress != 0)
                {
                    Check(usage?.MethodAddress, "ScriptMetadataMethod", i, ".MethodAddress", usage?.Name);
                }
            }
            for (int i = 0; i < script.Addresses.Length; i++)
            {
                Check(script.Addresses[i], "Addresses", i);
            }
            artifact.Checks["executableRvas"] = checkedAddresses;
        }

        private sealed class AssemblyOwnership
        {
            private readonly Metadata metadata;
            private readonly int[] sourceParents;
            private readonly ILookup<string, Il2CppImageDefinition> images;

            private readonly record struct TypeIdentity(string Namespace, string Name, string? ParentNamespace, string? ParentName);

            internal AssemblyOwnership(Metadata metadata)
            {
                this.metadata = metadata;
                images = metadata.imageDefs.ToLookup(image => NormalizePath(metadata.GetStringFromIndex(image.nameIndex)), PathComparer);
                sourceParents = new int[metadata.typeDefs.Length];
                Array.Fill(sourceParents, -1);
                for (int owner = 0; owner < metadata.typeDefs.Length; owner++)
                {
                    var type = metadata.typeDefs[owner];
                    for (int i = 0; i < type.nested_type_count; i++)
                    {
                        long entry = (long)type.nestedTypesStart + i;
                        if ((ulong)entry >= (ulong)metadata.nestedTypeIndices.Length)
                        {
                            break;
                        }
                        int child = metadata.nestedTypeIndices[entry];
                        if ((uint)child < (uint)sourceParents.Length)
                        {
                            sourceParents[child] = sourceParents[child] == -1 ? owner : -2;
                        }
                    }
                }
            }

            internal bool IsEmbeddedHelper(string modulePath, string assemblyName) =>
                PathComparer.Equals(modulePath, DummyHelperAssemblyName + ".dll")
                && string.Equals(assemblyName, DummyHelperAssemblyName, StringComparison.Ordinal)
                && !images.Contains(modulePath);

            internal void Validate(string modulePath, MetadataReader reader, (int Row, TypeDefinition Type)[] exportedTypes,
                OutputArtifactValidation artifact)
            {
                var candidates = images[modulePath].Take(2).ToArray();
                if (candidates.Length != 1)
                {
                    artifact.Limitations.Add("No unique source image matches the assembly path.");
                    return;
                }

                var image = candidates[0];
                long end = (long)image.typeStart + image.typeCount;
                if (image.typeStart < 0 || end > metadata.typeDefs.Length)
                {
                    artifact.Limitations.Add("The source image has no usable type index range.");
                    return;
                }
                var expected = new Dictionary<TypeIdentity, Queue<int>>();
                bool ambiguousSource = false;
                for (int i = image.typeStart; i < end; i++)
                {
                    var type = metadata.typeDefs[i];
                    string name = metadata.GetStringFromIndex(type.nameIndex);
                    if (name == "<Module>")
                    {
                        continue;
                    }
                    int owner = sourceParents[i];
                    ambiguousSource |= owner == -2;
                    var parent = owner >= 0 ? metadata.typeDefs[owner] : null;
                    var key = new TypeIdentity(metadata.GetStringFromIndex(type.namespaceIndex), name,
                        parent == null ? null : metadata.GetStringFromIndex(parent.namespaceIndex),
                        parent == null ? null : metadata.GetStringFromIndex(parent.nameIndex));
                    if (!expected.TryGetValue(key, out var indices))
                    {
                        expected.Add(key, indices = new Queue<int>());
                    }
                    indices.Enqueue(i);
                }
                if (ambiguousSource)
                {
                    artifact.Limitations.Add("Source ownership is ambiguous for at least one nested type in this image.");
                    return;
                }

                int matched = 0;
                foreach (var (row, type) in exportedTypes)
                {
                    string name = reader.GetString(type.Name);
                    if (name == "<Module>")
                    {
                        continue;
                    }
                    var parentHandle = type.GetDeclaringType();
                    var parent = parentHandle.IsNil ? default : reader.GetTypeDefinition(parentHandle);
                    var key = new TypeIdentity(reader.GetString(type.Namespace), name,
                        parentHandle.IsNil ? null : reader.GetString(parent.Namespace),
                        parentHandle.IsNil ? null : reader.GetString(parent.Name));
                    if (expected.TryGetValue(key, out var indices) && indices.TryDequeue(out _))
                    {
                        matched++;
                    }
                    else
                    {
                        artifact.Error("unexpectedExportedType", $"TypeDef[{row}]",
                            $"No remaining source type matches '{key.Namespace}.{key.Name}' with declaring type '{key.ParentNamespace}.{key.ParentName}'.");
                    }
                }
                artifact.Checks["sourceTypesMatched"] = matched;
                foreach (var (key, indices) in expected)
                {
                    foreach (int index in indices)
                    {
                        artifact.Error("missingSourceType", $"source TypeDefIndex {index}",
                            $"No export matches '{key.Namespace}.{key.Name}' with declaring type '{key.ParentNamespace}.{key.ParentName}'.");
                    }
                }
            }
        }
    }
}
