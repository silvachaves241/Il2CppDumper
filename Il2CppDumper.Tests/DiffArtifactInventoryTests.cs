using System.Text;
using System.Text.Json;
using Il2CppDumper.Diff;
using Xunit;

namespace Il2CppDumper.Tests
{
    public sealed class DiffArtifactInventoryTests : IDisposable
    {
        private const string Dump = "dump.json";
        private const string Script = "script.json";
        private const string Literals = "stringliteral-indexed.json";
        private readonly string root = Path.Combine(Path.GetTempPath(), $"diff-inventory-{Guid.NewGuid()}");
        private string Left => Path.Combine(root, "left");
        private string Right => Path.Combine(root, "right");

        public DiffArtifactInventoryTests()
        {
            WriteRun(Left, 1, "a");
            WriteRun(Right, 2, "b");
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ExcludesUnlistedSidecarsFromIdenticalDeclarations(bool directFile)
        {
            WriteManifest(Left, Dump, Script, Literals);
            WriteManifest(Right, Dump);
            var input = directFile ? Path.Combine(Right, Dump) : Right;

            var diff = DumpDiffer.Compare(Left, input);

            Assert.False(diff.HasDifferences);
            Assert.Null(diff.methods.addressChanged);
            Assert.Empty(diff.literals.valueChangedByIndex);
            Assert.Contains("script.json not listed in right run-manifest.json", diff.methods.note);
            Assert.Contains("stringliteral-indexed.json not listed in right run-manifest.json", diff.literals.note);
        }

        [Fact]
        public void ComparesVerifiedSidecarWhenOtherKindIsUnlisted()
        {
            WriteManifest(Left, Dump, Script, Literals);
            WriteManifest(Right, Dump, Literals);
            File.WriteAllText(Path.Combine(Right, Script), "stale, corrupted JSON");

            var diff = DumpDiffer.Compare(Left, Right);

            Assert.Null(diff.methods.addressChanged);
            Assert.Single(diff.literals.valueChangedByIndex);
            Assert.Null(diff.literals.note);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ComparesSidecarChangesWithOrWithoutAManifest(bool withManifest)
        {
            if (withManifest)
            {
                WriteManifest(Left, Dump, Script, Literals);
                WriteManifest(Right, Dump, Script, Literals);
            }

            var diff = DumpDiffer.Compare(Left, Path.Combine(Right, Dump));

            Assert.Single(diff.methods.addressChanged!);
            Assert.Single(diff.literals.valueChangedByIndex);
        }

        [Fact]
        public void DirectUnlistedDeclarationsDoNotInheritSiblingArtifacts()
        {
            WriteManifest(Left, Dump, Script, Literals);
            WriteManifest(Right, Dump, Script, Literals);
            var standalone = Path.Combine(Right, "imported.json");
            File.Copy(Path.Combine(Right, Dump), standalone);
            File.WriteAllText(Path.Combine(Right, "dump-metadata.json"),
                "{\"inputs\":{\"il2cpp\":{\"sha256\":\"other-run\",\"path\":\"OtherAssembly.dll\"}}}");

            var diff = DumpDiffer.Compare(Left, standalone);

            Assert.False(diff.HasDifferences);
            Assert.Contains("imported.json is not listed in run-manifest.json", diff.methods.note);
            Assert.Contains("imported.json is not listed in run-manifest.json", diff.literals.note);
            Assert.Equal(standalone, diff.right.source);
            using var stream = File.OpenRead(standalone);
            Assert.Equal(DumpMetadataWriter.ComputeStreamSha256(stream), diff.right.sha256);
        }

        [Theory]
        [InlineData(Script, "replace", "SHA-256 mismatch")]
        [InlineData(Literals, "replace", "SHA-256 mismatch")]
        [InlineData(Script, "delete", "missing but listed")]
        [InlineData(Literals, "delete", "missing but listed")]
        [InlineData(Literals, "append", "size mismatch")]
        [InlineData(Dump, "append", "size mismatch")]
        public void InvalidRecordedFileReturnsInputError(string file, string mutation, string diagnostic)
        {
            WriteManifest(Left, Dump, Script, Literals);
            WriteManifest(Right, Dump, Script, Literals);
            var target = Path.Combine(Right, file);
            if (mutation == "delete")
                File.Delete(target);
            else if (mutation == "append")
                File.AppendAllText(target, " ");
            else
            {
                var bytes = File.ReadAllBytes(target);
                var position = Array.IndexOf(bytes, file == Script ? (byte)'2' : (byte)'b');
                Assert.True(position >= 0);
                bytes[position]++;
                File.WriteAllBytes(target, bytes);
            }

            var output = Path.Combine(root, "existing-report.json");
            File.WriteAllText(output, "previous report");
            using var log = new StringWriter();

            Assert.Equal(2, DumpDiffer.Execute(Left, Path.Combine(Right, Dump), output, log));
            Assert.Contains($"right {file}", log.ToString());
            Assert.Contains(diagnostic, log.ToString());
            Assert.Contains(RunManifestWriter.FileName, log.ToString());
            Assert.Equal("previous report", File.ReadAllText(output));
        }

        [Fact]
        public void ManifestRequiresAnInventory()
        {
            File.WriteAllText(Path.Combine(Right, RunManifestWriter.FileName), "{}");

            var error = Assert.Throws<InvalidDataException>(() => DumpDiffer.Compare(Left, Right));

            Assert.Contains("right run-manifest.json", error.Message);
            Assert.Contains("'outputs' array", error.Message);
        }

        [Fact]
        public void DuplicateSidecarInventoryEntriesAreRejected()
        {
            var outputs = RunManifestWriter.CollectOutputs(Right, new[] { Dump, Script, Literals });
            outputs.Add(outputs.Single(output => output.Path == Script));
            File.WriteAllText(Path.Combine(Right, RunManifestWriter.FileName),
                JsonSerializer.Serialize(new RunManifest { Outputs = outputs }));

            var error = Assert.Throws<InvalidDataException>(() => DumpDiffer.Compare(Left, Right));

            Assert.Contains("duplicate entry for script.json", error.Message);
        }

        private static void WriteRun(string directory, int address, string literal)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, Dump), "{\"types\":[]}");
            File.WriteAllText(Path.Combine(directory, Script),
                $"{{\"ScriptMethod\":[{{\"Address\":{address},\"Name\":\"Example.Type$$Method\"}}]}}");
            File.WriteAllText(Path.Combine(directory, Literals),
                $"[{{\"index\":0,\"value\":\"{literal}\"}}]", Encoding.UTF8);
        }

        private static void WriteManifest(string directory, params string[] files)
        {
            var outputs = RunManifestWriter.CollectOutputs(directory, files);
            File.WriteAllText(Path.Combine(directory, RunManifestWriter.FileName),
                JsonSerializer.Serialize(new RunManifest { Success = true, Outputs = outputs }));
        }

        public void Dispose() => Directory.Delete(root, recursive: true);
    }
}
