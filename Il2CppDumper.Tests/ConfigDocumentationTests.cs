using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace Il2CppDumper.Tests
{
    public class ConfigDocumentationTests
    {
        private static string FindRepoRoot()
        {
            var current = AppContext.BaseDirectory;
            while (!string.IsNullOrEmpty(current))
            {
                if (File.Exists(Path.Combine(current, "README.md")) &&
                    File.Exists(Path.Combine(current, "README.zh-CN.md")))
                {
                    return Path.GetFullPath(current);
                }

                var parent = Directory.GetParent(current);
                if (parent == null)
                    break;

                current = parent.FullName;
            }

            throw new FileNotFoundException($"Could not locate repository root from {AppContext.BaseDirectory}");
        }

        [Fact]
        public void Readme_English_DocumentsGenerateStruct_AndDoesNotDocumentGenerateScript()
        {
            var repoRoot = FindRepoRoot();
            var readmePath = Path.Combine(repoRoot, "README.md");
            var content = File.ReadAllText(readmePath);

            Assert.DoesNotContain("GenerateScript", content);
            Assert.Contains("`GenerateStruct`", content);
            Assert.Contains("`script.json`", content);
            Assert.Contains("`stringliteral.json`", content);
            Assert.Contains("`stringliteral-indexed.json`", content);
            Assert.Contains("`il2cpp.h`", content);
        }

        [Fact]
        public void Readme_Chinese_DocumentsGenerateStruct_AndDoesNotDocumentGenerateScript()
        {
            var repoRoot = FindRepoRoot();
            var readmePath = Path.Combine(repoRoot, "README.zh-CN.md");
            var content = File.ReadAllText(readmePath);

            Assert.DoesNotContain("GenerateScript", content);
            Assert.Contains("`GenerateStruct`", content);
            Assert.Contains("`script.json`", content);
            Assert.Contains("`stringliteral.json`", content);
            Assert.Contains("`stringliteral-indexed.json`", content);
            Assert.Contains("`il2cpp.h`", content);
        }

        [Fact]
        public void Readme_English_DocumentsDumpPropertyAndDumpAttributeDefaults()
        {
            var repoRoot = FindRepoRoot();
            var readmePath = Path.Combine(repoRoot, "README.md");
            var content = File.ReadAllText(readmePath);

            Assert.Contains("`DumpProperty` and `DumpAttribute` default to `true`", content);
        }

        [Fact]
        public void Readme_Chinese_DocumentsDumpPropertyAndDumpAttributeDefaults()
        {
            var repoRoot = FindRepoRoot();
            var readmePath = Path.Combine(repoRoot, "README.zh-CN.md");
            var content = File.ReadAllText(readmePath);

            Assert.Contains("`DumpProperty` \u548c `DumpAttribute` \u9ed8\u8ba4\u503c\u4e3a `true`", content);
        }

        [Fact]
        public void Readme_English_DocumentsLibraryUsage_WithDumpRequestContractAndRequireAnyKeyNote()
        {
            var repoRoot = FindRepoRoot();
            var readmePath = Path.Combine(repoRoot, "README.md");
            var content = File.ReadAllText(readmePath);

            Assert.Contains("### Library usage", content);
            Assert.Contains("using Il2CppDumper;", content);
            Assert.Contains("`Il2CppDumper.Core`", content);
            Assert.Contains("DumpRequest", content);
            Assert.Contains("DumpResult", content);
            Assert.Contains("Dumper.Dump", content);
            Assert.Contains("`RequireAnyKey`", content);
            Assert.Contains("`Outputs`", content);
            Assert.Contains("`ProducedFiles`", content);
        }

        [Fact]
        public void Readme_Chinese_DocumentsLibraryUsage_WithDumpRequestContractAndRequireAnyKeyNote()
        {
            var repoRoot = FindRepoRoot();
            var readmePath = Path.Combine(repoRoot, "README.zh-CN.md");
            var content = File.ReadAllText(readmePath);

            Assert.Contains("### 类库调用（Library usage）", content);
            Assert.Contains("using Il2CppDumper;", content);
            Assert.Contains("`Il2CppDumper.Core`", content);
            Assert.Contains("DumpRequest", content);
            Assert.Contains("DumpResult", content);
            Assert.Contains("Dumper.Dump", content);
            Assert.Contains("`RequireAnyKey`", content);
            Assert.Contains("`Outputs`", content);
            Assert.Contains("`ProducedFiles`", content);
        }

        [Fact]
        public void Config_DeserializingGenerateStructFalse_DisablesGenerationGate()
        {
            // Verifies the exact scenario from Issue #11:
            // Documenting GenerateStruct ensures setting it to false in JSON successfully disables the gate.
            const string json = "{\"GenerateStruct\": false}";
            var config = JsonSerializer.Deserialize<Config>(json);

            Assert.NotNull(config);
            Assert.False(config.GenerateStruct);
        }

        [Fact]
        public void Config_DeserializingGenerateStructTrue_EnablesGenerationGate()
        {
            const string json = "{\"GenerateStruct\": true}";
            var config = JsonSerializer.Deserialize<Config>(json);

            Assert.NotNull(config);
            Assert.True(config.GenerateStruct);
        }

        [Fact]
        public void Config_DefaultConstructor_HasDumpPropertyAndDumpAttributeEnabledByDefault()
        {
            var config = new Config();

            Assert.True(config.DumpProperty);
            Assert.True(config.DumpAttribute);
        }

        [Fact]
        public void Config_DeserializingEmptyJson_ProducesTrueForDumpPropertyAndDumpAttribute()
        {
            var config = JsonSerializer.Deserialize<Config>("{}");

            Assert.NotNull(config);
            Assert.True(config.DumpProperty);
            Assert.True(config.DumpAttribute);
        }

        [Fact]
        public void Config_ShippedConfigJson_AlignsWithCodeDefaultsForPropertiesAndAttributes()
        {
            var repoRoot = FindRepoRoot();
            var configJsonPath = Path.Combine(repoRoot, "Il2CppDumper", "config.json");
            Assert.True(File.Exists(configJsonPath), $"Expected config.json at {configJsonPath}");

            var json = File.ReadAllText(configJsonPath);
            var shippedConfig = JsonSerializer.Deserialize<Config>(json);
            var codeDefaultConfig = new Config();

            Assert.NotNull(shippedConfig);
            Assert.True(shippedConfig.DumpProperty);
            Assert.True(shippedConfig.DumpAttribute);
            Assert.Equal(codeDefaultConfig.DumpProperty, shippedConfig.DumpProperty);
            Assert.Equal(codeDefaultConfig.DumpAttribute, shippedConfig.DumpAttribute);
        }

        [Fact]
        public void Config_ExplicitFalseJson_AllowsDisablingDumpPropertyAndDumpAttribute()
        {
            const string json = "{\"DumpProperty\": false, \"DumpAttribute\": false}";
            var config = JsonSerializer.Deserialize<Config>(json);

            Assert.NotNull(config);
            Assert.False(config.DumpProperty);
            Assert.False(config.DumpAttribute);
        }

        [Fact]
        public void Config_AllOptionsDocumentedInEnglishReadme_MatchConfigProperties()
        {
            var expectedOptions = new[]
            {
                "DumpMethod",
                "DumpField",
                "DumpProperty",
                "DumpAttribute",
                "DumpFieldOffset",
                "DumpMethodOffset",
                "DumpTypeDefIndex",
                "GenerateDummyDll",
                "GenerateStruct",
                "GenerateJsonDump",
                "GenerateRunManifest",
                "GenerateProtectionReport",
                "GenerateOneofMap",
                "DummyDllAddToken",
                "RequireAnyKey",
                "ForceIl2CppVersion",
                "ForceVersion",
                "ForceDump",
                "NoRedirectedPointer"
            };

            var repoRoot = FindRepoRoot();
            var content = File.ReadAllText(Path.Combine(repoRoot, "README.md"));

            foreach (var option in expectedOptions)
            {
                Assert.Contains($"`{option}`", content);
                var prop = typeof(Config).GetProperty(option, BindingFlags.Public | BindingFlags.Instance);
                Assert.True(prop != null, $"Property {option} documented in README.md must exist on Config class.");
            }
        }

        [Fact]
        public void Config_AllOptionsDocumentedInChineseReadme_MatchConfigProperties()
        {
            var expectedOptions = new[]
            {
                "DumpMethod",
                "DumpField",
                "DumpProperty",
                "DumpAttribute",
                "DumpFieldOffset",
                "DumpMethodOffset",
                "DumpTypeDefIndex",
                "GenerateDummyDll",
                "GenerateStruct",
                "GenerateJsonDump",
                "GenerateRunManifest",
                "GenerateProtectionReport",
                "GenerateOneofMap",
                "DummyDllAddToken",
                "RequireAnyKey",
                "ForceIl2CppVersion",
                "ForceVersion",
                "ForceDump",
                "NoRedirectedPointer"
            };

            var repoRoot = FindRepoRoot();
            var content = File.ReadAllText(Path.Combine(repoRoot, "README.zh-CN.md"));

            foreach (var option in expectedOptions)
            {
                Assert.Contains($"`{option}`", content);
                var prop = typeof(Config).GetProperty(option, BindingFlags.Public | BindingFlags.Instance);
                Assert.True(prop != null, $"Property {option} documented in README.zh-CN.md must exist on Config class.");
            }
        }

        [Fact]
        public void Readme_English_DocumentsDumpMetadataJson_WithPortugueseSection()
        {
            var repoRoot = FindRepoRoot();
            var readmePath = Path.Combine(repoRoot, "README.md");
            var content = File.ReadAllText(readmePath);

            Assert.Contains("#### dump-metadata.json", content);
            Assert.Contains("`sha256`", content);
            Assert.Contains("`metadataVersion`", content);
            Assert.Contains("`generatedAtUtc`", content);
            Assert.Contains("**Português:**", content);
            Assert.Contains("proveniência", content);
        }

        [Fact]
        public void Config_DefaultConstructor_HasGenerateJsonDumpFalseByDefault()
        {
            var config = new Config();

            Assert.False(config.GenerateJsonDump);
        }

        [Fact]
        public void Config_DeserializingEmptyJson_ProducesFalseForGenerateJsonDump()
        {
            var config = JsonSerializer.Deserialize<Config>("{}");

            Assert.NotNull(config);
            Assert.False(config.GenerateJsonDump);
        }

        [Fact]
        public void Config_DeserializingGenerateJsonDumpTrue_EnablesGenerationGate()
        {
            const string json = "{\"GenerateJsonDump\": true}";
            var config = JsonSerializer.Deserialize<Config>(json);

            Assert.NotNull(config);
            Assert.True(config.GenerateJsonDump);
        }

        [Fact]
        public void Config_DeserializingGenerateJsonDumpFalse_DisablesGenerationGate()
        {
            const string json = "{\"GenerateJsonDump\": false}";
            var config = JsonSerializer.Deserialize<Config>(json);

            Assert.NotNull(config);
            Assert.False(config.GenerateJsonDump);
        }

        [Fact]
        public void Config_ShippedConfigJson_AlignsWithCodeDefaultsForGenerateJsonDump()
        {
            var repoRoot = FindRepoRoot();
            var configJsonPath = Path.Combine(repoRoot, "Il2CppDumper", "config.json");
            Assert.True(File.Exists(configJsonPath), $"Expected config.json at {configJsonPath}");

            var json = File.ReadAllText(configJsonPath);
            var shippedConfig = JsonSerializer.Deserialize<Config>(json);
            var codeDefaultConfig = new Config();

            Assert.NotNull(shippedConfig);
            Assert.False(shippedConfig.GenerateJsonDump);
            Assert.Equal(codeDefaultConfig.GenerateJsonDump, shippedConfig.GenerateJsonDump);
        }

        [Fact]
        public void Readme_English_DocumentsDumpJson_WithPortugueseSection()
        {
            var repoRoot = FindRepoRoot();
            var readmePath = Path.Combine(repoRoot, "README.md");
            var content = File.ReadAllText(readmePath);

            Assert.Contains("#### dump.json", content);
            Assert.Contains("`GenerateJsonDump`", content);
            Assert.Contains("`types`", content);
            Assert.Contains("`fields`", content);
            Assert.Contains("`properties`", content);
            Assert.Contains("`methods`", content);
            Assert.Contains("`enums`", content);
            Assert.Contains("`nested`", content);
            Assert.Contains("`attributes`", content);
            Assert.Contains("[Serializable]", content);
            Assert.Contains("**Português:**", content);
        }

        [Fact]
        public void Readme_Chinese_DocumentsDumpJson_WithPortugueseSection()
        {
            var repoRoot = FindRepoRoot();
            var readmePath = Path.Combine(repoRoot, "README.zh-CN.md");
            var content = File.ReadAllText(readmePath);

            Assert.Contains("#### dump.json", content);
            Assert.Contains("`GenerateJsonDump`", content);
            Assert.Contains("`types`", content);
            Assert.Contains("`fields`", content);
            Assert.Contains("`properties`", content);
            Assert.Contains("`methods`", content);
            Assert.Contains("`enums`", content);
            Assert.Contains("`nested`", content);
            Assert.Contains("`attributes`", content);
            Assert.Contains("[Serializable]", content);
            Assert.Contains("**Português:**", content);
        }

        [Fact]
        public void Readme_Chinese_DocumentsDumpMetadataJson_WithPortugueseSection()
        {
            var repoRoot = FindRepoRoot();
            var readmePath = Path.Combine(repoRoot, "README.zh-CN.md");
            var content = File.ReadAllText(readmePath);

            Assert.Contains("#### dump-metadata.json", content);
            Assert.Contains("`sha256`", content);
            Assert.Contains("`metadataVersion`", content);
            Assert.Contains("`generatedAtUtc`", content);
            Assert.Contains("**Português:**", content);
            Assert.Contains("proveniência", content);
        }

        [Fact]
        public void Readme_English_DocumentsStringLiteralForensicUsage()
        {
            var repoRoot = FindRepoRoot();
            var readmePath = Path.Combine(repoRoot, "README.md");
            var content = File.ReadAllText(readmePath);

            Assert.Contains("#### stringliteral-indexed.json", content);
            Assert.Contains("**Forensic usage:**", content);
            Assert.Contains("`GetStringLiteralFromIndex(n)`", content);
            Assert.Contains("`dataIndex` and `length`", content);
            Assert.Contains("**Uso forense:**", content);
        }

        [Fact]
        public void Readme_Chinese_DocumentsStringLiteralForensicUsage()
        {
            var repoRoot = FindRepoRoot();
            var readmePath = Path.Combine(repoRoot, "README.zh-CN.md");
            var content = File.ReadAllText(readmePath);

            Assert.Contains("#### stringliteral-indexed.json", content);
            Assert.Contains("**取证用途（Forensic usage）：**", content);
            Assert.Contains("`GetStringLiteralFromIndex(n)`", content);
            Assert.Contains("`dataIndex` 与 `length`", content);
            Assert.Contains("**Uso forense:**", content);
        }
        [Fact]
        public void Readme_English_DocumentsDiffingSection()
        {
            var repoRoot = FindRepoRoot();
            var readmePath = Path.Combine(repoRoot, "README.md");
            var content = File.ReadAllText(readmePath);

            Assert.Contains("### Diffing (Il2CppDumper.Diff)", content);
            Assert.Contains("Il2CppDumper.Diff <left> <right> <output>", content);
            Assert.Contains("#### Inputs", content);
            Assert.Contains("`dump.json`", content);
            Assert.Contains("`dump.cs` text is never an input", content);
            Assert.Contains("#### Exit codes", content);
            Assert.Contains("`0`: Identical", content);
            Assert.Contains("`1`: Differences found", content);
            Assert.Contains("`2`: Usage / input error", content);
            Assert.Contains("#### Schema reference (`dump-diff.json`)", content);
            Assert.Contains("\"addressChanged\"", content);
            Assert.Contains("\"addedByIndex\"", content);
            Assert.Contains("\"valueChangedByIndex\"", content);
            Assert.Contains("\"summary\"", content);
            Assert.Contains("**Português:**", content);

            // B4: Schema confrontation test - ensure the documented schema deserializes cleanly into DumpDiffResult
            var jsonStart = content.IndexOf("```json", StringComparison.Ordinal);
            Assert.True(jsonStart >= 0);
            var codeStart = content.IndexOf('{', jsonStart);
            var codeEnd = content.IndexOf("```", codeStart, StringComparison.Ordinal);
            var jsonSnippet = content.Substring(codeStart, codeEnd - codeStart).Trim();

            var parsed = System.Text.Json.JsonSerializer.Deserialize<Il2CppDumper.Diff.DumpDiffResult>(jsonSnippet);
            Assert.NotNull(parsed);
            Assert.Equal("f1", parsed.types.changed[0].fields.added[0]);
            Assert.Equal("f2", parsed.types.changed[0].fields.removed[0]);
            Assert.Equal("f3", parsed.types.changed[0].fields.changed[0].field);
            Assert.Equal("int", parsed.types.changed[0].fields.changed[0].left.type);
            Assert.Equal("0x10", parsed.types.changed[0].fields.changed[0].left.offset);
            Assert.Equal("m1", parsed.types.changed[0].methods.added[0]);
            Assert.Equal("m2", parsed.types.changed[0].methods.removed[0]);
            Assert.Equal("m3", parsed.types.changed[0].methods.changed[0].method);
            Assert.Equal("void", parsed.types.changed[0].methods.changed[0].left.returnType);
            Assert.NotNull(parsed.methods.addressChanged);
            Assert.Equal("Namespace.TypeName", parsed.methods.addressChanged[0].type);
            Assert.Equal("MethodName", parsed.methods.addressChanged[0].method);
        }

        [Fact]
        public void Readme_Chinese_DocumentsDiffingSection()
        {
            var repoRoot = FindRepoRoot();
            var readmePath = Path.Combine(repoRoot, "README.zh-CN.md");
            var content = File.ReadAllText(readmePath);

            Assert.Contains("### 差异对比 (Diffing / Il2CppDumper.Diff)", content);
            Assert.Contains("Il2CppDumper.Diff <left> <right> <output>", content);
            Assert.Contains("#### 输入 (Inputs)", content);
            Assert.Contains("`dump.json`", content);
            Assert.Contains("`dump.cs` 文本绝不作为输入", content);
            Assert.Contains("#### 退出状态码 (Exit codes)", content);
            Assert.Contains("`0`: 完全一致", content);
            Assert.Contains("`1`: 发现差异", content);
            Assert.Contains("`2`: 参数使用或输入错误", content);
            Assert.Contains("#### 数据模式参考 (Schema reference - `dump-diff.json`)", content);
            Assert.Contains("\"addressChanged\"", content);
            Assert.Contains("\"addedByIndex\"", content);
            Assert.Contains("\"valueChangedByIndex\"", content);
            Assert.Contains("\"summary\"", content);
            Assert.Contains("**Português:**", content);

            // B4: Schema confrontation test - ensure Chinese README schema matches emitted model
            var jsonStart = content.IndexOf("```json", StringComparison.Ordinal);
            Assert.True(jsonStart >= 0);
            var codeStart = content.IndexOf('{', jsonStart);
            var codeEnd = content.IndexOf("```", codeStart, StringComparison.Ordinal);
            var jsonSnippet = content.Substring(codeStart, codeEnd - codeStart).Trim();

            var parsed = System.Text.Json.JsonSerializer.Deserialize<Il2CppDumper.Diff.DumpDiffResult>(jsonSnippet);
            Assert.NotNull(parsed);
            Assert.Equal("f1", parsed.types.changed[0].fields.added[0]);
            Assert.Equal("f2", parsed.types.changed[0].fields.removed[0]);
            Assert.Equal("f3", parsed.types.changed[0].fields.changed[0].field);
            Assert.Equal("m1", parsed.types.changed[0].methods.added[0]);
            Assert.Equal("m2", parsed.types.changed[0].methods.removed[0]);
            Assert.Equal("m3", parsed.types.changed[0].methods.changed[0].method);
            Assert.NotNull(parsed.methods.addressChanged);
            Assert.Equal("Namespace.TypeName", parsed.methods.addressChanged[0].type);
        }

        [Fact]
        public void Readme_English_DocumentsFieldOffsetDriftSection()
        {
            var repoRoot = FindRepoRoot();
            var readmePath = Path.Combine(repoRoot, "README.md");
            var content = File.ReadAllText(readmePath);

            Assert.Contains("#### Field offset drift report (`field-offset-drift.json`)", content);
            Assert.Contains("Il2CppDumper.Diff --drift <left> <right> <output>", content);
            Assert.Contains("`0`: Identical (no field offset drift found).", content);
            Assert.Contains("`1`: Drift found in one or more structs.", content);
            Assert.Contains("`2`: Usage / input error.", content);
            Assert.Contains("`breaks-external-readers`", content);
            Assert.Contains("`leaf-consumers-may-survive`", content);

            // Schema confrontation test
            var sectionStart = content.IndexOf("##### Schema reference (`field-offset-drift.json`)", StringComparison.Ordinal);
            Assert.True(sectionStart >= 0);
            var jsonStart = content.IndexOf("```json", sectionStart, StringComparison.Ordinal);
            Assert.True(jsonStart >= 0);
            var codeStart = content.IndexOf('{', jsonStart);
            var codeEnd = content.IndexOf("```", codeStart, StringComparison.Ordinal);
            var jsonSnippet = content.Substring(codeStart, codeEnd - codeStart).Trim();

            var parsed = System.Text.Json.JsonSerializer.Deserialize<Il2CppDumper.Diff.FieldOffsetDriftReport>(jsonSnippet);
            Assert.NotNull(parsed);
            Assert.Equal(1, parsed.schemaVersion);
            Assert.Single(parsed.structs);
            Assert.Equal("Ns.Obf", parsed.structs[0].@struct);
            Assert.Equal("0x120", parsed.structs[0].size.left);
            Assert.Equal("0x128", parsed.structs[0].size.right);
            Assert.Equal("hp", parsed.structs[0].fields[0].name);
            Assert.Equal("0x28", parsed.structs[0].fields[0].left);
            Assert.Equal("0x2C", parsed.structs[0].fields[0].right);
            Assert.Equal("shifted", parsed.structs[0].fields[0].drift);
            Assert.Equal("breaks-external-readers", parsed.structs[0].fields[0].severity);
            Assert.Equal("mp", parsed.structs[0].fields[1].name);
            Assert.Equal("0x2C", parsed.structs[0].fields[1].left);
            Assert.Null(parsed.structs[0].fields[1].right);
            Assert.Equal("removed", parsed.structs[0].fields[1].drift);
            Assert.Equal(1, parsed.structs[0].verdict.shifted);
            Assert.Equal(1, parsed.structs[0].verdict.removed);
            Assert.Equal(0, parsed.structs[0].verdict.added);
        }

        [Fact]
        public void Readme_Chinese_DocumentsFieldOffsetDriftSection()
        {
            var repoRoot = FindRepoRoot();
            var readmePath = Path.Combine(repoRoot, "README.zh-CN.md");
            var content = File.ReadAllText(readmePath);

            Assert.Contains("#### 结构体字段偏移漂移报告 (Field offset drift report - `field-offset-drift.json`)", content);
            Assert.Contains("Il2CppDumper.Diff --drift <left> <right> <output>", content);
            Assert.Contains("`0`: 完全一致（未发现字段偏移漂移）。", content);
            Assert.Contains("`1`: 在一个或多个结构体中发现漂移。", content);
            Assert.Contains("`2`: 参数使用或输入错误。", content);
            Assert.Contains("`breaks-external-readers`", content);
            Assert.Contains("`leaf-consumers-may-survive`", content);

            // Schema confrontation test
            var sectionStart = content.IndexOf("##### 数据模式参考 (Schema reference - `field-offset-drift.json`)", StringComparison.Ordinal);
            Assert.True(sectionStart >= 0);
            var jsonStart = content.IndexOf("```json", sectionStart, StringComparison.Ordinal);
            Assert.True(jsonStart >= 0);
            var codeStart = content.IndexOf('{', jsonStart);
            var codeEnd = content.IndexOf("```", codeStart, StringComparison.Ordinal);
            var jsonSnippet = content.Substring(codeStart, codeEnd - codeStart).Trim();

            var parsed = System.Text.Json.JsonSerializer.Deserialize<Il2CppDumper.Diff.FieldOffsetDriftReport>(jsonSnippet);
            Assert.NotNull(parsed);
            Assert.Equal(1, parsed.schemaVersion);
            Assert.Single(parsed.structs);
            Assert.Equal("Ns.Obf", parsed.structs[0].@struct);
            Assert.Equal("0x120", parsed.structs[0].size.left);
            Assert.Equal("0x128", parsed.structs[0].size.right);
            Assert.Equal("hp", parsed.structs[0].fields[0].name);
            Assert.Equal("shifted", parsed.structs[0].fields[0].drift);
            Assert.Equal("mp", parsed.structs[0].fields[1].name);
            Assert.Equal("removed", parsed.structs[0].fields[1].drift);
        }
        [Fact]
        public void Config_DefaultConstructor_HasGenerateRunManifestTrueByDefault()
        {
            var config = new Config();
            Assert.True(config.GenerateRunManifest);
        }

        [Fact]
        public void Config_DeserializingEmptyJson_ProducesTrueForGenerateRunManifest()
        {
            var config = JsonSerializer.Deserialize<Config>("{}");
            Assert.NotNull(config);
            Assert.True(config.GenerateRunManifest);
        }

        [Fact]
        public void Config_DeserializingGenerateRunManifestTrue_EnablesGenerationGate()
        {
            const string json = "{\"GenerateRunManifest\": true}";
            var config = JsonSerializer.Deserialize<Config>(json);
            Assert.NotNull(config);
            Assert.True(config.GenerateRunManifest);
        }

        [Fact]
        public void Config_DeserializingGenerateRunManifestFalse_DisablesGenerationGate()
        {
            const string json = "{\"GenerateRunManifest\": false}";
            var config = JsonSerializer.Deserialize<Config>(json);
            Assert.NotNull(config);
            Assert.False(config.GenerateRunManifest);
        }

        [Fact]
        public void Config_ShippedConfigJson_AlignsWithCodeDefaultsForGenerateRunManifest()
        {
            var repoRoot = FindRepoRoot();
            var configJsonPath = Path.Combine(repoRoot, "Il2CppDumper", "config.json");
            Assert.True(File.Exists(configJsonPath), $"Expected config.json at {configJsonPath}");

            var json = File.ReadAllText(configJsonPath);
            var shippedConfig = JsonSerializer.Deserialize<Config>(json);
            var codeDefaultConfig = new Config();

            Assert.NotNull(shippedConfig);
            Assert.True(shippedConfig.GenerateRunManifest);
            Assert.Equal(codeDefaultConfig.GenerateRunManifest, shippedConfig.GenerateRunManifest);
        }

        [Fact]
        public void Readme_English_DocumentsRunManifestJson_WithPortugueseSection()
        {
            var repoRoot = FindRepoRoot();
            var readmePath = Path.Combine(repoRoot, "README.md");
            var content = File.ReadAllText(readmePath);

            Assert.Contains("#### run-manifest.json", content);
            Assert.Contains("`GenerateRunManifest`", content);
            Assert.Contains("`effectiveConfig`", content);
            Assert.Contains("`counts`", content);
            Assert.Contains("`warnings`", content);
            Assert.Contains("`outputs`", content);
            Assert.Contains("**Português:**", content);
            Assert.Contains("manifesto operacional de execução", content);
        }

        [Fact]
        public void Readme_Chinese_DocumentsRunManifestJson_WithPortugueseSection()
        {
            var repoRoot = FindRepoRoot();
            var readmePath = Path.Combine(repoRoot, "README.zh-CN.md");
            var content = File.ReadAllText(readmePath);

            Assert.Contains("#### run-manifest.json", content);
            Assert.Contains("`GenerateRunManifest`", content);
            Assert.Contains("`effectiveConfig`", content);
            Assert.Contains("`counts`", content);
            Assert.Contains("`warnings`", content);
            Assert.Contains("`outputs`", content);
            Assert.Contains("**Português:**", content);
            Assert.Contains("manifesto operacional de execução", content);
        }

        [Fact]
        public void Config_ShippedConfigJson_AlignsWithCodeDefaultsForStringDecryptor()
        {
            var repoRoot = FindRepoRoot();
            var configJsonPath = Path.Combine(repoRoot, "Il2CppDumper", "config.json");
            Assert.True(File.Exists(configJsonPath), $"Expected config.json at {configJsonPath}");

            var json = File.ReadAllText(configJsonPath);
            var shippedConfig = JsonSerializer.Deserialize<Config>(json);
            var codeDefaultConfig = new Config();

            Assert.NotNull(shippedConfig);
            Assert.Equal(codeDefaultConfig.StringDecryptor, shippedConfig.StringDecryptor);
            Assert.Equal(codeDefaultConfig.StringDecryptorKey, shippedConfig.StringDecryptorKey);
        }

        [Fact]
        public void Readme_English_DocumentsStringDecryptor_WithBothShapesAndTrustNote()
        {
            var repoRoot = FindRepoRoot();
            var readmePath = Path.Combine(repoRoot, "README.md");
            var content = File.ReadAllText(readmePath);

            Assert.Contains("`StringDecryptor`, `StringDecryptorKey`", content);
            Assert.Contains("\"none\"", content);
            Assert.Contains("\"xor\"", content);
            Assert.Contains("xor:2b", content);
            Assert.Contains("AAjCEk=", content);
            Assert.Contains("Trust note for custom hooks:", content);
            Assert.Contains("Transform failure (exception or key mismatch)", content);
            Assert.Contains("**Português:**", content);
        }

        [Fact]
        public void Readme_Chinese_DocumentsStringDecryptor_WithBothShapesAndTrustNote()
        {
            var repoRoot = FindRepoRoot();
            var readmePath = Path.Combine(repoRoot, "README.zh-CN.md");
            var content = File.ReadAllText(readmePath);

            Assert.Contains("`StringDecryptor`, `StringDecryptorKey`", content);
            Assert.Contains("\"none\"", content);
            Assert.Contains("\"xor\"", content);
            Assert.Contains("xor:2b", content);
            Assert.Contains("AAjCEk=", content);
            Assert.Contains("自定义 Hook 安全提示（Trust note）：", content);
            Assert.Contains("转换失败（异常或密钥不匹配）", content);
            Assert.Contains("**Português:**", content);
        }

        [Fact]
        public void Config_ShippedConfigJson_AlignsWithCodeDefaultsForNativeAttachEmitters()
        {
            var repoRoot = FindRepoRoot();
            var configJsonPath = Path.Combine(repoRoot, "Il2CppDumper", "config.json");
            Assert.True(File.Exists(configJsonPath), $"Expected config.json at {configJsonPath}");

            var json = File.ReadAllText(configJsonPath);
            var shippedConfig = JsonSerializer.Deserialize<Config>(json);
            var codeDefaultConfig = new Config();

            Assert.NotNull(shippedConfig);
            Assert.False(shippedConfig.EmitFridaScript);
            Assert.False(shippedConfig.EmitBookmarks);
            Assert.Equal(codeDefaultConfig.EmitFridaScript, shippedConfig.EmitFridaScript);
            Assert.Equal(codeDefaultConfig.EmitBookmarks, shippedConfig.EmitBookmarks);
        }

        [Fact]
        public void Readme_English_DocumentsNativeAttachEmitters_WithSampleOutputs()
        {
            var repoRoot = FindRepoRoot();
            var readmePath = Path.Combine(repoRoot, "README.md");
            var content = File.ReadAllText(readmePath);

            Assert.Contains("#### frida_stubs.js", content);
            Assert.Contains("#### bookmarks.json", content);
            Assert.Contains("`EmitFridaScript`", content);
            Assert.Contains("`EmitBookmarks`", content);
            Assert.Contains("Process.findModuleByName", content);
            Assert.Contains("Interceptor.attach(mod.base.add(ptr(method.va)), callbacks)", content);
            Assert.Contains("\"category\": \"method\"", content);
            Assert.Contains("**Português:**", content);
        }

        [Fact]
        public void Readme_Chinese_DocumentsNativeAttachEmitters_WithSampleOutputs()
        {
            var repoRoot = FindRepoRoot();
            var readmePath = Path.Combine(repoRoot, "README.zh-CN.md");
            var content = File.ReadAllText(readmePath);

            Assert.Contains("#### frida_stubs.js", content);
            Assert.Contains("#### bookmarks.json", content);
            Assert.Contains("`EmitFridaScript`", content);
            Assert.Contains("`EmitBookmarks`", content);
            Assert.Contains("Process.findModuleByName", content);
            Assert.Contains("Interceptor.attach(mod.base.add(ptr(method.va)), callbacks)", content);
            Assert.Contains("\"category\": \"method\"", content);
            Assert.Contains("**Português:**", content);
        }

        [Fact]
        public void Config_ShippedConfigJson_AlignsWithCodeDefaultsForBatchMode()
        {
            var repoRoot = FindRepoRoot();
            var configJsonPath = Path.Combine(repoRoot, "Il2CppDumper", "config.json");
            Assert.True(File.Exists(configJsonPath), $"Expected config.json at {configJsonPath}");

            var json = File.ReadAllText(configJsonPath);
            var shippedConfig = JsonSerializer.Deserialize<Config>(json);
            var codeDefaultConfig = new Config();

            Assert.NotNull(shippedConfig);
            Assert.False(shippedConfig.BatchMode);
            Assert.False(shippedConfig.StopOnFirstError);
            Assert.Equal(codeDefaultConfig.BatchMode, shippedConfig.BatchMode);
            Assert.Equal(codeDefaultConfig.StopOnFirstError, shippedConfig.StopOnFirstError);
        }

        [Fact]
        public void Readme_English_DocumentsBatchMode_AndStopOnFirstError()
        {
            var repoRoot = FindRepoRoot();
            var readmePath = Path.Combine(repoRoot, "README.md");
            var content = File.ReadAllText(readmePath);

            Assert.Contains("--batch <batch-file.json>", content);
            Assert.Contains("#### Batch Mode (`--batch <file>`)", content);
            Assert.Contains("batch-result.json", content);
            Assert.Contains("`BatchMode`, `StopOnFirstError`", content);
            Assert.Contains("**Português:**", content);
            Assert.Contains("Processamento em lote orientado a arquivo", content);
        }

        [Fact]
        public void Readme_Chinese_DocumentsBatchMode_AndStopOnFirstError()
        {
            var repoRoot = FindRepoRoot();
            var readmePath = Path.Combine(repoRoot, "README.zh-CN.md");
            var content = File.ReadAllText(readmePath);

            Assert.Contains("--batch <batch-file.json>", content);
            Assert.Contains("#### 批处理模式（Batch Mode, `--batch <file>`）", content);
            Assert.Contains("batch-result.json", content);
            Assert.Contains("`BatchMode`, `StopOnFirstError`", content);
            Assert.Contains("**Português:**", content);
            Assert.Contains("Processamento em lote orientado a arquivo", content);
        }

        [Fact]
        public void Config_DefaultConstructor_HasGenerateProtectionReportTrueByDefault()
        {
            var config = new Config();
            Assert.True(config.GenerateProtectionReport);
        }

        [Fact]
        public void Config_DeserializingEmptyJson_ProducesTrueForGenerateProtectionReport()
        {
            var config = JsonSerializer.Deserialize<Config>("{}");
            Assert.NotNull(config);
            Assert.True(config.GenerateProtectionReport);
        }

        [Fact]
        public void Config_DeserializingGenerateProtectionReportTrue_EnablesGenerationGate()
        {
            const string json = "{\"GenerateProtectionReport\": true}";
            var config = JsonSerializer.Deserialize<Config>(json);
            Assert.NotNull(config);
            Assert.True(config.GenerateProtectionReport);
        }

        [Fact]
        public void Config_DeserializingGenerateProtectionReportFalse_DisablesGenerationGate()
        {
            const string json = "{\"GenerateProtectionReport\": false}";
            var config = JsonSerializer.Deserialize<Config>(json);
            Assert.NotNull(config);
            Assert.False(config.GenerateProtectionReport);
        }

        [Fact]
        public void Config_ShippedConfigJson_AlignsWithCodeDefaultsForGenerateProtectionReport()
        {
            var repoRoot = FindRepoRoot();
            var configJsonPath = Path.Combine(repoRoot, "Il2CppDumper", "config.json");
            Assert.True(File.Exists(configJsonPath), $"Expected config.json at {configJsonPath}");

            var json = File.ReadAllText(configJsonPath);
            var shippedConfig = JsonSerializer.Deserialize<Config>(json);
            var codeDefaultConfig = new Config();

            Assert.NotNull(shippedConfig);
            Assert.True(shippedConfig.GenerateProtectionReport);
            Assert.Equal(codeDefaultConfig.GenerateProtectionReport, shippedConfig.GenerateProtectionReport);
        }

        [Fact]
        public void Readme_English_DocumentsProtectionReportJson_WithPortugueseSection()
        {
            var repoRoot = FindRepoRoot();
            var readmePath = Path.Combine(repoRoot, "README.md");
            var content = File.ReadAllText(readmePath);

            Assert.Contains("#### protection-report.json", content);
            Assert.Contains("`GenerateProtectionReport`", content);
            Assert.Contains("`HighEntropySection`", content);
            Assert.Contains("`PackerSectionSignature`", content);
            Assert.Contains("`RedirectionHeuristic`", content);
            Assert.Contains("`MetadataSanity`", content);
            Assert.Contains("**Português:**", content);
        }

        [Fact]
        public void Readme_Chinese_DocumentsProtectionReportJson_WithPortugueseSection()
        {
            var repoRoot = FindRepoRoot();
            var readmePath = Path.Combine(repoRoot, "README.zh-CN.md");
            var content = File.ReadAllText(readmePath);

            Assert.Contains("#### protection-report.json", content);
            Assert.Contains("`GenerateProtectionReport`", content);
            Assert.Contains("`HighEntropySection`", content);
            Assert.Contains("`PackerSectionSignature`", content);
            Assert.Contains("`RedirectionHeuristic`", content);
            Assert.Contains("`MetadataSanity`", content);
            Assert.Contains("**Português:**", content);
        }

        [Fact]
        public void Config_DefaultConstructor_HasGenerateOneofMapFalseByDefault()
        {
            var config = new Config();
            Assert.False(config.GenerateOneofMap);
        }

        [Fact]
        public void Config_DeserializingEmptyJson_ProducesFalseForGenerateOneofMap()
        {
            var config = JsonSerializer.Deserialize<Config>("{}");
            Assert.NotNull(config);
            Assert.False(config.GenerateOneofMap);
        }

        [Fact]
        public void Config_DeserializingGenerateOneofMapTrue_EnablesGenerationGate()
        {
            const string json = "{\"GenerateOneofMap\": true}";
            var config = JsonSerializer.Deserialize<Config>(json);
            Assert.NotNull(config);
            Assert.True(config.GenerateOneofMap);
        }

        [Fact]
        public void Config_DeserializingGenerateOneofMapFalse_DisablesGenerationGate()
        {
            const string json = "{\"GenerateOneofMap\": false}";
            var config = JsonSerializer.Deserialize<Config>(json);
            Assert.NotNull(config);
            Assert.False(config.GenerateOneofMap);
        }

        [Fact]
        public void Config_ShippedConfigJson_AlignsWithCodeDefaultsForGenerateOneofMap()
        {
            var repoRoot = FindRepoRoot();
            var configJsonPath = Path.Combine(repoRoot, "Il2CppDumper", "config.json");
            Assert.True(File.Exists(configJsonPath), $"Expected config.json at {configJsonPath}");

            var json = File.ReadAllText(configJsonPath);
            var shippedConfig = JsonSerializer.Deserialize<Config>(json);
            var codeDefaultConfig = new Config();

            Assert.NotNull(shippedConfig);
            Assert.False(shippedConfig.GenerateOneofMap);
            Assert.Equal(codeDefaultConfig.GenerateOneofMap, shippedConfig.GenerateOneofMap);
        }

        [Fact]
        public void Readme_English_DocumentsOneofMap_WithPortugueseSection()
        {
            var repoRoot = FindRepoRoot();
            var readmePath = Path.Combine(repoRoot, "README.md");
            var content = File.ReadAllText(readmePath);

            Assert.Contains("#### oneof-members.json", content);
            Assert.Contains("`GenerateOneofMap`", content);
            Assert.Contains("`oneof-ambiguities.json`", content);
            Assert.Contains("**Português:**", content);
        }

        [Fact]
        public void Readme_Chinese_DocumentsOneofMap_WithPortugueseSection()
        {
            var repoRoot = FindRepoRoot();
            var readmePath = Path.Combine(repoRoot, "README.zh-CN.md");
            var content = File.ReadAllText(readmePath);

            Assert.Contains("#### oneof-members.json", content);
            Assert.Contains("`GenerateOneofMap`", content);
            Assert.Contains("`oneof-ambiguities.json`", content);
            Assert.Contains("**Português:**", content);
        }
    }
}
