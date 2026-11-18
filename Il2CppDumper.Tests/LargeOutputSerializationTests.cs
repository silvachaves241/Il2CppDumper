using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Il2CppDumper;
using Xunit;

namespace Il2CppDumper.Tests;

public class LargeOutputSerializationTests
{
    [Fact]
    public void DumpJson_PreservesBytesWithoutAllocatingAnOutputSizedBuffer()
    {
        var dump = new JsonDumpModel();
        for (var i = 0; i < 16384; i++)
        {
            dump.types.Add(new JsonDumpType
            {
                image = "Example.dll",
                assembly = "Example",
                fullName = $"Example.Type{i}",
                nested = { new JsonDumpType { fullName = "Inner" } },
                fields = { new JsonDumpField { name = "Text", type = "string" } }
            });
        }

        AssertStreamedBytes(dump, JsonDumpGenerator.JsonOptions, path => JsonDumpGenerator.WriteModel(path, dump));
    }

    [Fact]
    public void ScriptJson_PreservesBytesWithoutAllocatingAnOutputSizedBuffer()
    {
        var script = new ScriptJson { Addresses = new ulong[] { 0x1000, 0x2000 } };
        for (var i = 0; i < 32768; i++)
        {
            script.ScriptMethod.Add(new ScriptMethod
            {
                Address = (ulong)(0x1000 + i),
                Name = $"Example.Type$$Method{i}",
                Signature = $"void Example_Type__Method{i}(Example_Type_o* __this, const MethodInfo* method);",
                TypeSignature = "vii",
                Image = "Example.dll",
                Assembly = "Example",
                MethodSignature = $"Method{i}()"
            });
        }
        script.ScriptString.Add(new ScriptString { Address = 0x2000, Value = "quote \" / newline\n / 漢字" });

        var options = new JsonSerializerOptions { WriteIndented = true, IncludeFields = true };
        AssertStreamedBytes(script, options, path => StagedOutput.WriteJson(path, script, options));
    }

    [Fact]
    public void StagedOutput_WhenWriterFails_PreservesPreviousOutputAndDiscardsPartialWrite()
    {
        var directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        try
        {
            var target = Path.Combine(directory.FullName, "script.json");
            File.WriteAllText(target, "previous");
            var failure = new InvalidDataException("Synthetic write failure");
            var caught = Assert.Throws<InvalidDataException>(() => StagedOutput.Write(target, stream =>
            {
                stream.WriteByte((byte)'[');
                throw failure;
            }));

            Assert.Same(failure, caught);
            Assert.Equal("previous", File.ReadAllText(target));
            Assert.Equal(target, Assert.Single(Directory.GetFiles(directory.FullName)));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static void AssertStreamedBytes<T>(T model, JsonSerializerOptions options, Action<string> write)
    {
        // The string-based writer is the byte oracle. Measure only the synchronous file write.
        var legacyBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(model, options));
        Assert.True(legacyBytes.Length > 8 * 1024 * 1024);
        var directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        try
        {
            var path = Path.Combine(directory.FullName, "synthetic.json");
            var before = GC.GetAllocatedBytesForCurrentThread();
            write(path);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            using var actual = File.OpenRead(path);
            Assert.Equal(legacyBytes.LongLength, actual.Length);
            using var hash = SHA256.Create();
            Assert.Equal(SHA256.HashData(legacyBytes), hash.ComputeHash(actual));
            Assert.True(allocated < actual.Length, $"Allocated {allocated:N0} bytes for a {actual.Length:N0}-byte file");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
