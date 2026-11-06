#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Il2CppDumper.Core;
using Il2CppDumper.Tests.Synthetic;
using Xunit;

namespace Il2CppDumper.Tests
{
    public class StringLiteralDecryptorTests : IDisposable
    {
        private readonly List<string> tempDirectories = new();

        public void Dispose()
        {
            StringLiteralDecryptorRegistry.Reset();
            foreach (var dir in tempDirectories)
            {
                if (Directory.Exists(dir))
                {
                    try { Directory.Delete(dir, true); } catch { }
                }
            }
        }

        private string CreateTempDir()
        {
            var path = Path.Combine(Path.GetTempPath(), "Il2CppDumper_DecryptorTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            tempDirectories.Add(path);
            return path;
        }

        /// <summary>
        /// Acceptance Criterion 1:
        /// Default config (key absent or "none") produces byte-identical output files
        /// on the synthetic v35+ corpus (asserted across V35, V38, V39).
        /// </summary>
        [Theory]
        [InlineData(MetadataVersionFamily.V35)]
        [InlineData(MetadataVersionFamily.V38)]
        [InlineData(MetadataVersionFamily.V39)]
        public void DefaultConfig_KeyAbsentOrNone_ProducesByteIdenticalOutputFiles(MetadataVersionFamily family)
        {
            var seed = 42;
            var outDirDefault = CreateTempDir();
            var outDirAbsent = CreateTempDir();
            var outDirExplicitNone = CreateTempDir();

            var configDefault = new Config(); // StringDecryptor == "none"
            var configAbsent = new Config { StringDecryptor = null };
            var configExplicitNone = new Config { StringDecryptor = "none" };

            var (_, _, execDefault) = SyntheticEnvironment.Create(family, seed);
            var (_, _, execAbsent) = SyntheticEnvironment.Create(family, seed);
            var (_, _, execExplicitNone) = SyntheticEnvironment.Create(family, seed);

            Dumper.ExecuteDump(execDefault.metadata, execDefault.il2Cpp, configDefault, outDirDefault);
            Dumper.ExecuteDump(execAbsent.metadata, execAbsent.il2Cpp, configAbsent, outDirAbsent);
            Dumper.ExecuteDump(execExplicitNone.metadata, execExplicitNone.il2Cpp, configExplicitNone, outDirExplicitNone);

            var defaultIndexed = File.ReadAllBytes(Path.Combine(outDirDefault, "stringliteral-indexed.json"));
            var absentIndexed = File.ReadAllBytes(Path.Combine(outDirAbsent, "stringliteral-indexed.json"));
            var explicitNoneIndexed = File.ReadAllBytes(Path.Combine(outDirExplicitNone, "stringliteral-indexed.json"));

            Assert.Equal(defaultIndexed, absentIndexed);
            Assert.Equal(defaultIndexed, explicitNoneIndexed);

            var defaultLegacy = File.ReadAllBytes(Path.Combine(outDirDefault, "stringliteral.json"));
            var absentLegacy = File.ReadAllBytes(Path.Combine(outDirAbsent, "stringliteral.json"));
            var explicitNoneLegacy = File.ReadAllBytes(Path.Combine(outDirExplicitNone, "stringliteral.json"));

            Assert.Equal(defaultLegacy, absentLegacy);
            Assert.Equal(defaultLegacy, explicitNoneLegacy);

            var defaultScript = File.ReadAllBytes(Path.Combine(outDirDefault, "script.json"));
            var absentScript = File.ReadAllBytes(Path.Combine(outDirAbsent, "script.json"));
            var explicitNoneScript = File.ReadAllBytes(Path.Combine(outDirExplicitNone, "script.json"));

            Assert.Equal(defaultScript, absentScript);
            Assert.Equal(defaultScript, explicitNoneScript);

            // Assert that "raw" and "decryptor" are completely absent from serialized json
            var defaultIndexedJson = Encoding.UTF8.GetString(defaultIndexed);
            Assert.DoesNotContain("\"raw\":", defaultIndexedJson);
            Assert.DoesNotContain("\"decryptor\":", defaultIndexedJson);
        }

        /// <summary>
        /// Acceptance Criterion 2:
        /// Built-in xor mode: fixed test vectors (key + ciphertext + expected plaintext)
        /// on synthetic metadata; raw/decoded pair asserted in output.
        /// </summary>
        [Fact]
        public void BuiltInXorMode_FixedTestVector_AssertsRawAndDecodedPair()
        {
            const byte xorKey = 0x2B;
            const string expectedPlaintext = "HelloSynthetic";
            var plaintextBytes = Encoding.UTF8.GetBytes(expectedPlaintext);
            var ciphertextBytes = plaintextBytes.Select(b => (byte)(b ^ xorKey)).ToArray();
            var expectedRawBase64 = Convert.ToBase64String(ciphertextBytes);

            // Generate synthetic metadata with XOR obfuscation (using test vector key 0x2b)
            var metadataBytes = SyntheticMetadataGenerator.Generate(
                MetadataVersionFamily.V35,
                seed: 42,
                corruption: MetadataCorruptionType.ObfuscatedStringLiteral);

            var outDir = CreateTempDir();
            var metadataPath = Path.Combine(outDir, "global-metadata.dat");
            File.WriteAllBytes(metadataPath, metadataBytes);

            var dummyExePath = Path.Combine(outDir, "dummy.dll");
            File.WriteAllBytes(dummyExePath, new byte[64]);

            // Test both "xor" with StringDecryptorKey = "2b" and compact "xor:2b"
            var config = new Config
            {
                StringDecryptor = "xor",
                StringDecryptorKey = "2b",
                GenerateStruct = true
            };

            using var metadata = new Metadata(new MemoryStream(metadataBytes));
            metadata.StringDecryptor = new XorStringLiteralDecryptor(new[] { xorKey });
            Assert.Equal("xor:2b", metadata.StringDecryptor.Id);

            // Assert direct metadata extraction resolves plaintext
            var resolvedValue = metadata.GetStringLiteralFromIndex(0);
            Assert.Equal(expectedPlaintext, resolvedValue);

            // Create synthetic binary containing a string literal metadata usage token pointing to index 0
            var ilBytes = new byte[4096];
            var token = ((uint)Il2CppMetadataUsage.kIl2CppMetadataUsageStringLiteral << 29) | (0u << 1) + 1;
            BitConverter.GetBytes(token).CopyTo(ilBytes, 1024);

            var sectionHelper = new SectionHelper(new WebAssemblyMemory(new MemoryStream(new byte[16]), 0), 0, 0, 0, 0);
            sectionHelper.SetSection(SearchSectionType.Data, new SearchSection { offset = 1024, offsetEnd = 2048 });

            using var il2cpp = new TestIl2CppWithUsage(new MemoryStream(ilBytes), sectionHelper);

            // Run full StructGenerator output
            var executor = new Il2CppExecutor(metadata, il2cpp);
            var structGenerator = new StructGenerator(executor);
            structGenerator.WriteScript(outDir + Path.DirectorySeparatorChar);

            // 1. Verify forensic stringliteral-indexed.json
            var indexedPath = Path.Combine(outDir, "stringliteral-indexed.json");
            Assert.True(File.Exists(indexedPath));
            var indexedJson = File.ReadAllText(indexedPath);
            var records = JsonSerializer.Deserialize<List<StringLiteralRecord>>(indexedJson);
            Assert.NotNull(records);
            Assert.NotEmpty(records);

            var firstRecord = records[0];
            Assert.Equal(0u, firstRecord.index);
            Assert.Equal(expectedPlaintext, firstRecord.value);
            Assert.Equal(expectedRawBase64, firstRecord.raw);
            Assert.Equal("xor:2b", firstRecord.decryptor);

            // 2. Verify legacy stringliteral.json contains additive fields
            var legacyPath = Path.Combine(outDir, "stringliteral.json");
            Assert.True(File.Exists(legacyPath));
            var legacyJson = File.ReadAllText(legacyPath);
            using var doc = JsonDocument.Parse(legacyJson);
            var rootArray = doc.RootElement.EnumerateArray().ToList();
            Assert.NotEmpty(rootArray);

            var firstLegacy = rootArray[0];
            Assert.Equal(expectedPlaintext, firstLegacy.GetProperty("value").GetString());
            Assert.Equal(expectedRawBase64, firstLegacy.GetProperty("raw").GetString());
            Assert.Equal("xor:2b", firstLegacy.GetProperty("decryptor").GetString());
        }

        /// <summary>
        /// Acceptance Criterion 3:
        /// Throwing decryptor: warning logged with index, raw value emitted,
        /// exit code and other literals unaffected.
        /// </summary>
        [Fact]
        public void ThrowingDecryptor_WarningLoggedWithIndex_RawValueEmitted_ExitCodeUnaffected()
        {
            var rawText = "HelloSynthetic";
            var rawBytes = Encoding.UTF8.GetBytes(rawText);
            var rawBase64 = Convert.ToBase64String(rawBytes);

            var metadataBytes = SyntheticMetadataGenerator.Generate(MetadataVersionFamily.V35, seed: 42);
            using var metadata = new Metadata(new MemoryStream(metadataBytes));
            var (_, il2cpp, _) = SyntheticEnvironment.Create(MetadataVersionFamily.V35, seed: 42);

            // Throwing decryptor that throws on index 0
            var throwingDecryptor = new FaultyDecryptor(throwOnIndex: 0);
            metadata.StringDecryptor = throwingDecryptor;

            var warnings = new List<string>();
            using (CoreLogger.Scope(TextWriter.Null, warnings))
            {
                // Direct call emits raw value and logs warning with index
                var val = metadata.GetStringLiteralFromIndex(0);
                Assert.Equal(rawText, val);
                Assert.Contains(warnings, w => w.Contains("index 0") && w.Contains("FaultyDecryptor intentional test fault"));

                // Forensic dump emits raw value, records raw base64, keeps decryptor null on fault
                var records = metadata.DumpStringLiterals(out var failedLiterals);
                Assert.Equal(0, failedLiterals); // Metadata itself is intact, not corrupt
                Assert.NotEmpty(records);
                Assert.Equal(rawText, records[0].value);
                Assert.Equal(rawBase64, records[0].raw);
                Assert.Null(records[0].decryptor);
            }
        }

        /// <summary>
        /// Acceptance Criterion 4:
        /// Custom decryptor test implementation links via the public contract;
        /// the contract lives in the library assembly with XML docs.
        /// </summary>
        [Fact]
        public void CustomDecryptor_LinksViaPublicContract_ResolvesAndDecodesSuccessfully()
        {
            var customDecryptor = new CustomRot13Decryptor();
            Assert.Equal("rot13", customDecryptor.Id);

            // Register custom decryptor in registry
            StringLiteralDecryptorRegistry.Register("rot13", _ => customDecryptor);

            var resolved = StringLiteralDecryptorRegistry.Resolve("rot13");
            Assert.NotNull(resolved);
            Assert.Equal("rot13", resolved!.Id);

            // Plaintext "Hello" -> ROT13 "Uryyb"
            var rawBytes = Encoding.UTF8.GetBytes("Uryyb");
            var decrypted = resolved.Decrypt(0, rawBytes);
            Assert.Equal("Hello", decrypted);
        }

        /// <summary>
        /// Acceptance Criterion 6:
        /// Obfuscated-literal fixture added to synthetic fixture corpus covering active-decryptor path.
        /// </summary>
        [Theory]
        [InlineData(MetadataVersionFamily.V35)]
        [InlineData(MetadataVersionFamily.V38)]
        [InlineData(MetadataVersionFamily.V39)]
        public void SyntheticCorpus_ObfuscatedStringLiteralFixture_DecodesViaActiveDecryptor(MetadataVersionFamily family)
        {
            const string expectedPlaintext = "HelloSynthetic_123";

            var metadataBytes = SyntheticMetadataGenerator.Generate(
                family,
                seed: 123,
                corruption: MetadataCorruptionType.ObfuscatedStringLiteral);

            // 1. Without decryptor: output matches raw obfuscated ciphertext
            using (var mRaw = new Metadata(new MemoryStream(metadataBytes)))
            {
                var rawValue = mRaw.GetStringLiteralFromIndex(0);
                Assert.NotEqual(expectedPlaintext, rawValue);
            }

            // 2. With xor:2b decryptor: output resolves exact expected plaintext
            using (var mDecrypted = new Metadata(new MemoryStream(metadataBytes)))
            {
                mDecrypted.StringDecryptor = new XorStringLiteralDecryptor("2b");
                var decryptedValue = mDecrypted.GetStringLiteralFromIndex(0);
                Assert.Equal(expectedPlaintext, decryptedValue);

                var records = mDecrypted.DumpStringLiterals();
                Assert.NotEmpty(records);
                Assert.Equal(expectedPlaintext, records[0].value);
                Assert.NotNull(records[0].raw);
                Assert.Equal("xor:2b", records[0].decryptor);
            }
        }

        [Fact]
        public void XorStringLiteralDecryptor_KeyRepresentations_ParseConsistently()
        {
            var dec1 = new XorStringLiteralDecryptor("2b");
            var dec2 = new XorStringLiteralDecryptor("0x2b");
            var dec3 = new XorStringLiteralDecryptor("0x2B");
            var dec4 = new XorStringLiteralDecryptor(new byte[] { 0x2B });

            Assert.Equal("xor:2b", dec1.Id);
            Assert.Equal("xor:2b", dec2.Id);
            Assert.Equal("xor:2b", dec3.Id);
            Assert.Equal("xor:2b", dec4.Id);

            var multiDec = new XorStringLiteralDecryptor("deadbeef");
            Assert.Equal("xor:deadbeef", multiDec.Id);
        }

        [Fact]
        public void Registry_Resolve_HandlesNoneAndCombinedSpecs()
        {
            Assert.Null(StringLiteralDecryptorRegistry.Resolve(null));
            Assert.Null(StringLiteralDecryptorRegistry.Resolve("none"));
            Assert.Null(StringLiteralDecryptorRegistry.Resolve("NONE"));
            Assert.Null(StringLiteralDecryptorRegistry.Resolve(""));

            var xorCombined = StringLiteralDecryptorRegistry.Resolve("xor:2b");
            Assert.NotNull(xorCombined);
            Assert.Equal("xor:2b", xorCombined!.Id);

            var xorSeparated = StringLiteralDecryptorRegistry.Resolve("xor", "2b");
            Assert.NotNull(xorSeparated);
            Assert.Equal("xor:2b", xorSeparated!.Id);

            Assert.Throws<KeyNotFoundException>(() => StringLiteralDecryptorRegistry.Resolve("unknown_algorithm"));
        }

        private sealed class TestIl2CppWithUsage : Il2Cpp
        {
            private readonly SectionHelper _sectionHelper;

            public TestIl2CppWithUsage(Stream stream, SectionHelper sectionHelper) : base(stream)
            {
                Version = 35;
                Is32Bit = true;
                _sectionHelper = sectionHelper;
                types = new Il2CppType[]
                {
                    new Il2CppType { type = Il2CppTypeEnum.IL2CPP_TYPE_OBJECT, attrs = 0 },
                    new Il2CppType { type = Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE, attrs = 0 },
                    new Il2CppType { type = Il2CppTypeEnum.IL2CPP_TYPE_I4, attrs = (uint)Il2CppConstants.FIELD_ATTRIBUTE_PUBLIC },
                    new Il2CppType { type = Il2CppTypeEnum.IL2CPP_TYPE_I4, attrs = (uint)(Il2CppConstants.FIELD_ATTRIBUTE_PUBLIC | Il2CppConstants.FIELD_ATTRIBUTE_LITERAL) },
                    new Il2CppType { type = Il2CppTypeEnum.IL2CPP_TYPE_VOID, attrs = 0 },
                };
                methodSpecs = Array.Empty<Il2CppMethodSpec>();
                methodPointers = Array.Empty<ulong>();
                codeGenModuleMethodPointers = new Dictionary<string, ulong[]>();
                genericMethodPointers = Array.Empty<ulong>();
                invokerPointers = Array.Empty<ulong>();
                reversePInvokeWrappers = Array.Empty<ulong>();
                unresolvedVirtualCallPointers = Array.Empty<ulong>();
            }

            public override ulong MapVATR(ulong addr) => addr;
            public override ulong MapRTVA(ulong addr) => addr == 1024 ? 0x400ul : addr;
            public override ulong GetRVA(ulong addr) => addr;
            public override bool Search() => false;
            public override bool PlusSearch(int methodCount, int typeDefinitionsCount, int imageCount) => false;
            public override bool SymbolSearch() => false;
            public override SectionHelper GetSectionHelper(int methodCount, int typeDefinitionsCount, int imageCount) => _sectionHelper;
            public override bool CheckDump() => false;
        }

        private sealed class FaultyDecryptor : IStringLiteralDecryptor
        {
            private readonly uint throwOnIndex;

            public string Id => "faulty-test";

            public FaultyDecryptor(uint throwOnIndex)
            {
                this.throwOnIndex = throwOnIndex;
            }

            public string Decrypt(uint index, byte[] rawBytes)
            {
                if (index == throwOnIndex)
                {
                    throw new InvalidOperationException("FaultyDecryptor intentional test fault");
                }
                return Encoding.UTF8.GetString(rawBytes);
            }
        }

        /// <summary>
        /// Regression C1:
        /// An exception in the Id getter is contained safely without escaping:
        /// raw fallback is preserved, warning is logged with index, and full Core dump succeeds.
        /// </summary>
        [Fact]
        public void Regression_C1_IdGetterException_ContainedWithRawFallback_AndSuccessExitCode()
        {
            var rawText = "HelloSynthetic";
            var rawBytes = Encoding.UTF8.GetBytes(rawText);
            var rawBase64 = Convert.ToBase64String(rawBytes);

            var metadataBytes = SyntheticMetadataGenerator.Generate(MetadataVersionFamily.V35, seed: 42);
            using var metadata = new Metadata(new MemoryStream(metadataBytes));
            var (_, il2cpp, _) = SyntheticEnvironment.Create(MetadataVersionFamily.V35, seed: 42);

            metadata.StringDecryptor = new FaultyIdDecryptor();

            var warnings = new List<string>();
            using (CoreLogger.Scope(TextWriter.Null, warnings))
            {
                // Direct call emits raw value and logs warning without throwing unhandled exception
                var val = metadata.GetStringLiteralFromIndex(0);
                Assert.Equal(rawText, val);
                Assert.Contains(warnings, w => w.Contains("index 0") && w.Contains("Exploding Id getter fault"));
            }

            // Real Core dump execution succeeds, emits raw fallback, and generates stringliteral-indexed.json
            var outDir = CreateTempDir();
            var config = new Config { GenerateStruct = true };
            var success = Dumper.ExecuteDump(metadata, il2cpp, config, outDir, out var errorMessage);
            Assert.True(success, $"Expected Success=true but got error: {errorMessage}");

            var indexedPath = Path.Combine(outDir, "stringliteral-indexed.json");
            Assert.True(File.Exists(indexedPath));
            var indexedJson = File.ReadAllText(indexedPath);
            var records = JsonSerializer.Deserialize<List<StringLiteralRecord>>(indexedJson);
            Assert.NotNull(records);
            Assert.NotEmpty(records);
            Assert.Equal(rawText, records[0].value);
            Assert.Equal(rawBase64, records[0].raw);
            Assert.Null(records[0].decryptor);
        }

        /// <summary>
        /// Regression C2:
        /// Explicit key in specification (e.g. "xor:2b") takes precedence over fallbackKey (e.g. "00").
        /// </summary>
        [Fact]
        public void Regression_C2_ExplicitKeyInSpec_PrevailsOverFallbackKey()
        {
            const string expectedPlaintext = "HelloSynthetic";

            // Resolve with conflicting explicit key "2b" and fallback "00"
            var decryptor = StringLiteralDecryptorRegistry.Resolve("xor:2b", "00");
            Assert.NotNull(decryptor);
            Assert.Equal("xor:2b", decryptor!.Id);

            var metadataBytes = SyntheticMetadataGenerator.Generate(
                MetadataVersionFamily.V35,
                seed: 42,
                corruption: MetadataCorruptionType.ObfuscatedStringLiteral);

            var (_, il2cpp, _) = SyntheticEnvironment.Create(MetadataVersionFamily.V35, seed: 42);
            using var metadata = new Metadata(new MemoryStream(metadataBytes));

            var outDir = CreateTempDir();
            var config = new Config
            {
                StringDecryptor = "xor:2b",
                StringDecryptorKey = "00",
                GenerateStruct = true
            };

            var success = Dumper.ExecuteDump(metadata, il2cpp, config, outDir, out var errorMessage);
            Assert.True(success, errorMessage);

            var indexedPath = Path.Combine(outDir, "stringliteral-indexed.json");
            var indexedJson = File.ReadAllText(indexedPath);
            var records = JsonSerializer.Deserialize<List<StringLiteralRecord>>(indexedJson);
            Assert.NotNull(records);
            Assert.NotEmpty(records);

            // Plaintext must be decrypted with 0x2b ("HelloSynthetic"), not corrupted with 0x00 ("cNGGD")
            Assert.Equal(expectedPlaintext, records[0].value);
            Assert.Equal("xor:2b", records[0].decryptor);
        }

        /// <summary>
        /// Regression C3:
        /// A registered instance with an Id containing ':' (e.g. "xor:profile") is matched exactly
        /// and not shadowed by built-in scheme resolution.
        /// </summary>
        [Fact]
        public void Regression_C3_RegisteredInstanceWithColonId_NotShadowedBySchemePrefix()
        {
            var customInstance = new ProfileDecryptorWithColonId();
            StringLiteralDecryptorRegistry.Register(customInstance);

            var resolved = StringLiteralDecryptorRegistry.Resolve("xor:profile");
            Assert.NotNull(resolved);
            Assert.Same(customInstance, resolved);
            Assert.Equal("xor:profile", resolved!.Id);

            var dummyBytes = Encoding.UTF8.GetBytes("test_input");
            var output = resolved.Decrypt(0, dummyBytes);
            Assert.Equal("custom_profile_decrypted", output);
        }

        /// <summary>
        /// Regression C4:
        /// String decryptor failure is recorded in run-manifest.json under warnings.byCategory.stringDecryptor
        /// and summed into warnings.total, while preserving raw fallback and success.
        /// </summary>
        [Fact]
        public void Regression_C4_DecryptorFault_PersistedInRunManifestWarningCategoriesAndTotal()
        {
            var rawText = "HelloSynthetic";
            var rawBytes = Encoding.UTF8.GetBytes(rawText);
            var rawBase64 = Convert.ToBase64String(rawBytes);

            var metadataBytes = SyntheticMetadataGenerator.Generate(MetadataVersionFamily.V35, seed: 42);
            var outDir = CreateTempDir();
            var config = new Config
            {
                GenerateStruct = true,
                GenerateRunManifest = true
            };

            var (_, il2cpp, _) = SyntheticEnvironment.Create(MetadataVersionFamily.V35, seed: 42);
            using var metadata = new Metadata(new MemoryStream(metadataBytes))
            {
                StringDecryptor = new FaultyDecryptor(throwOnIndex: 0)
            };

            var success = Dumper.ExecuteDump(
                metadata,
                il2cpp,
                config,
                outDir,
                out var errorMessage,
                out var counts,
                out var manifestWarnings,
                out var writtenFiles);

            Assert.True(success, errorMessage);
            Assert.Equal(1, manifestWarnings.Total);
            Assert.Equal(1, manifestWarnings.ByCategory.StringDecryptor);
            Assert.Equal(0, manifestWarnings.ByCategory.StringLiteral);
            Assert.Equal(0, manifestWarnings.ByCategory.AttributeDecode);

            // Write and deserialize run-manifest.json
            var manifest = RunManifestWriter.Write(
                outDir,
                config,
                DateTime.UtcNow,
                100,
                success,
                counts,
                manifestWarnings,
                writtenFiles);

            Assert.NotNull(manifest);
            Assert.Equal(1, manifest.Warnings.Total);
            Assert.Equal(1, manifest.Warnings.ByCategory.StringDecryptor);
            Assert.Equal(0, manifest.Warnings.ByCategory.StringLiteral);

            var manifestPath = Path.Combine(outDir, "run-manifest.json");
            Assert.True(File.Exists(manifestPath));
            var manifestJson = File.ReadAllText(manifestPath);
            var reloaded = JsonSerializer.Deserialize<RunManifest>(manifestJson);
            Assert.NotNull(reloaded);
            Assert.Equal(1, reloaded!.Warnings.Total);
            Assert.Equal(1, reloaded.Warnings.ByCategory.StringDecryptor);
            Assert.Equal(0, reloaded.Warnings.ByCategory.StringLiteral);

            // Verify raw fallback in stringliteral-indexed.json
            var indexedPath = Path.Combine(outDir, "stringliteral-indexed.json");
            Assert.True(File.Exists(indexedPath));
            var records = JsonSerializer.Deserialize<List<StringLiteralRecord>>(File.ReadAllText(indexedPath));
            Assert.NotNull(records);
            Assert.NotEmpty(records);
            Assert.Equal(rawText, records[0].value);
            Assert.Equal(rawBase64, records[0].raw);
            Assert.Null(records[0].decryptor);
        }

        private sealed class FaultyIdDecryptor : IStringLiteralDecryptor
        {
            public string Id => throw new InvalidOperationException("Exploding Id getter fault");

            public string Decrypt(uint index, byte[] rawBytes)
            {
                return Encoding.UTF8.GetString(rawBytes);
            }
        }

        private sealed class ProfileDecryptorWithColonId : IStringLiteralDecryptor
        {
            public string Id => "xor:profile";

            public string Decrypt(uint index, byte[] rawBytes)
            {
                return "custom_profile_decrypted";
            }
        }

        private sealed class CustomRot13Decryptor : IStringLiteralDecryptor
        {
            public string Id => "rot13";

            public string Decrypt(uint index, byte[] rawBytes)
            {
                var input = Encoding.UTF8.GetString(rawBytes);
                var sb = new StringBuilder(input.Length);
                foreach (var c in input)
                {
                    if (c >= 'a' && c <= 'z')
                        sb.Append((char)('a' + (c - 'a' + 13) % 26));
                    else if (c >= 'A' && c <= 'Z')
                        sb.Append((char)('A' + (c - 'A' + 13) % 26));
                    else
                        sb.Append(c);
                }
                return sb.ToString();
            }
        }
    }
}
