#nullable enable
using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using Il2CppDumper.Core;
using Il2CppDumper.Tests.Synthetic;
using Mono.Cecil;
using Xunit;

namespace Il2CppDumper.Tests
{
    public sealed class OutputValidatorTests : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "il2cpp-validation-" + Guid.NewGuid().ToString("N"));

        public OutputValidatorTests() => Directory.CreateDirectory(root);

        public void Dispose() => Directory.Delete(root, true);

        [Fact]
        public void DuplicatePropertyMapParent_IsReportedForNestedExtensionlessAssembly()
        {
            const string moduleName = "__Generated/__Generated";
            var path = Path.Combine(root, "DummyDll", moduleName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var assembly = AssemblyDefinition.CreateAssembly(
                new AssemblyNameDefinition("__Generated", new Version(1, 0)), moduleName, ModuleKind.Dll))
            {
                var module = assembly.MainModule;
                foreach (var name in new[] { "First", "Second" })
                {
                    var type = new TypeDefinition("Example", name, TypeAttributes.Public, module.TypeSystem.Object);
                    module.Types.Add(type);
                    type.Properties.Add(new PropertyDefinition("Value", PropertyAttributes.None, module.TypeSystem.Int32));
                }
                assembly.Write(path);
            }
            var bytes = File.ReadAllBytes(path);
            using (var pe = new PEReader(new MemoryStream(bytes)))
            {
                var reader = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
                Assert.Equal(2, reader.GetTableRowCount(TableIndex.PropertyMap));
                var start = pe.PEHeaders.MetadataStartOffset + reader.GetTableMetadataOffset(TableIndex.PropertyMap);
                var secondParent = start + reader.GetTableRowSize(TableIndex.PropertyMap);
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(secondParent, 2),
                    BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(start, 2)));
            }
            File.WriteAllBytes(path, bytes);

            // Neither stale files nor the producer's directory marker are artifacts to validate.
            File.WriteAllText(Path.Combine(root, "DummyDll", "stale.dll"), "broken");
            File.WriteAllText(Path.Combine(root, "script.json"), "broken");
            var (metadata, binary, _) = SyntheticEnvironment.Create(MetadataVersionFamily.V39);
            using (metadata)
            using (binary)
            {
                var result = OutputValidator.Validate(root, new[] { path, Path.Combine(root, "DummyDll") }, metadata, binary);
                Assert.Equal("failed", result.Status);
                var artifact = Assert.Single(result.Artifacts);
                Assert.Equal("DummyDll/__Generated/__Generated", artifact.Path);
                Assert.Equal(2, artifact.Checks["PropertyMap"]);
                var error = Assert.Single(artifact.Findings);
                Assert.Equal("invalidMapParent", error.Category);
                Assert.Equal("PropertyMap[2].Parent", error.Location);
            }
        }

        [Theory]
        [InlineData(false, "passed", 0)]
        [InlineData(true, "failed", 4)]
        public void ScriptValidation_ChecksCodeRvasAndIgnoresUnresolvedMetadataMethod(bool corrupt, string status, int errors)
        {
            var script = Path.Combine(root, "script.json");
            File.WriteAllText(script, JsonSerializer.Serialize(new
            {
                ScriptMethod = new[] { new { Address = corrupt ? 6144708780086416ul : 0x1010ul, Name = "Sample" } },
                ScriptMetadataMethod = new[] { new { MethodAddress = 0ul }, new { MethodAddress = corrupt ? 0x2000ul : 0x1010ul } },
                Addresses = corrupt ? new[] { 0x1000ul, 0x1200ul, ulong.MaxValue } : new[] { 0x1000ul, 0x11FFul }
            }));
            var (metadata, synthetic, _) = SyntheticEnvironment.Create(MetadataVersionFamily.V39);
            using (metadata)
            using (synthetic)
            using (var pe = new PE(new MemoryStream(ProtectionReportTests.CreateSyntheticPe(new[]
            {
                (".text", new byte[0x200], 0x400u, 0x60000020u),
                (".data", new byte[0x200], 0x200u, 0xC0000040u)
            }))))
            {
                var report = OutputValidator.Validate(root, new[] { "script.json" }, metadata, pe);
                Assert.Equal(status, report.Status);
                var artifact = Assert.Single(report.Artifacts);
                Assert.Equal(errors, artifact.ErrorCount);
                Assert.Equal(corrupt ? 5 : 4, artifact.Checks["executableRvas"]);
                Assert.All(artifact.Findings, item => Assert.Equal("invalidMethodRva", item.Category));

                var unsupported = OutputValidator.Validate(root, new[] { script }, metadata, synthetic);
                Assert.Equal("partial", unsupported.Status);
                Assert.Single(Assert.Single(unsupported.Artifacts).Limitations);
            }
        }

        [Fact]
        public void SourceOwnership_ReportsAnOmittedTypeAndCoordinates()
        {
            var (metadata, binary, executor) = SyntheticEnvironment.Create(MetadataVersionFamily.V39);
            using (metadata)
            using (binary)
            {
                var files = DummyAssemblyExporter.Export(executor, root, addToken: false);
                var assemblyPath = files.Single(path => Path.GetFileName(path) == "SyntheticImage.dll");
                var report = OutputValidator.Validate(root, new[] { assemblyPath }, metadata, binary);
                Assert.Equal("passed", report.Status);
                Assert.Equal(2, Assert.Single(report.Artifacts).Checks["sourceTypesMatched"]);

                using (var assembly = AssemblyDefinition.ReadAssembly(assemblyPath, new ReaderParameters { InMemory = true }))
                {
                    var outputModule = assembly.MainModule;
                    Assert.True(outputModule.Types.Remove(outputModule.Types.Single(type => type.Name == "SyntheticEnum")));
                    assembly.Write(assemblyPath);
                }
                var altered = OutputValidator.Validate(root, new[] { assemblyPath }, metadata, binary);
                var error = Assert.Single(Assert.Single(altered.Artifacts).Findings);
                Assert.Equal("missingSourceType", error.Category);
                Assert.StartsWith("source TypeDefIndex ", error.Location);
                Assert.Contains("SyntheticEnum", error.Detail);
            }
        }
    }
}
