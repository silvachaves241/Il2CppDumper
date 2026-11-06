#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Il2CppDumper.Core
{
    public static class Dumper
    {
        public static DumpResult Dump(DumpRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }
            if (string.IsNullOrWhiteSpace(request.ExecutablePath))
            {
                throw new ArgumentException("ExecutablePath is required.", nameof(request));
            }
            if (string.IsNullOrWhiteSpace(request.MetadataPath))
            {
                throw new ArgumentException("MetadataPath is required.", nameof(request));
            }

            var warnings = new List<string>();
            var outputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            using (CoreLogger.Scope(request.Log, warnings))
            {
                var stopwatch = Stopwatch.StartNew();
                var startedAtUtc = DateTime.UtcNow;
                var counts = new RunManifestCounts();
                var manifestWarnings = new RunManifestWarnings();

                string outputDir;
                if (string.IsNullOrWhiteSpace(request.OutputDirectory))
                {
                    outputDir = Path.Combine(Path.GetTempPath(), "il2cpp-dump-" + Guid.NewGuid().ToString("N"));
                }
                else
                {
                    outputDir = Path.GetFullPath(request.OutputDirectory);
                }

                try
                {
                    Directory.CreateDirectory(outputDir);
                }
                catch (Exception e)
                {
                    var msg = $"ERROR: Failed to create output directory: {e.Message}";
                    CoreLogger.WriteLine(msg);
                    return new DumpResult(false, msg, warnings, outputs);
                }

                if (!outputDir.EndsWith(Path.DirectorySeparatorChar.ToString()) && !outputDir.EndsWith(Path.AltDirectorySeparatorChar.ToString()))
                {
                    outputDir += Path.DirectorySeparatorChar;
                }

                var config = request.Config != null ? CloneConfig(request.Config) : new Config();
                var writtenFiles = new List<string>();

                if (config.GenerateRunManifest && Directory.Exists(outputDir))
                {
                    var manifestPath = Path.Combine(outputDir, RunManifestWriter.FileName);
                    if (File.Exists(manifestPath))
                    {
                        try { File.Delete(manifestPath); } catch { }
                    }
                }

                void WriteRunManifest(bool success)
                {
                    if (!config.GenerateRunManifest)
                        return;

                    try
                    {
                        var manifest = RunManifestWriter.Write(
                            outputDir,
                            config,
                            startedAtUtc,
                            stopwatch.ElapsedMilliseconds,
                            success,
                            counts,
                            manifestWarnings,
                            writtenFiles
                        );
                        if (manifest != null)
                        {
                            outputs["run-manifest.json"] = Path.Combine(outputDir, RunManifestWriter.FileName);
                        }
                    }
                    catch (Exception ex)
                    {
                        var warn = $"WARNING: Failed to emit run manifest: {ex.Message}";
                        CoreLogger.WriteLine(warn);
                        warnings.Add(warn);
                    }
                }

                var il2cppPath = Path.GetFullPath(request.ExecutablePath);
                var metadataPath = Path.GetFullPath(request.MetadataPath);

                if (!File.Exists(il2cppPath))
                {
                    var msg = $"ERROR: Executable file not found: {il2cppPath}";
                    CoreLogger.WriteLine(msg);
                    WriteRunManifest(false);
                    PopulateOutputs(outputDir, outputs);
                    return new DumpResult(false, msg, warnings, outputs);
                }

                if (!File.Exists(metadataPath))
                {
                    var msg = "ERROR: Metadata file not found or encrypted.";
                    CoreLogger.WriteLine(msg);
                    WriteRunManifest(false);
                    PopulateOutputs(outputDir, outputs);
                    return new DumpResult(false, msg, warnings, outputs);
                }

                try
                {
                    var metaHeaderBytes = new byte[4];
                    using (var fs = new FileStream(metadataPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        var read = fs.Read(metaHeaderBytes, 0, 4);
                        if (read < 4 || BitConverter.ToUInt32(metaHeaderBytes, 0) != 0xFAB11BAF)
                        {
                            try
                            {
                                DumpMetadataWriter.WriteInitial(outputDir, il2cppPath, metadataPath, config);
                                writtenFiles.Add(Path.Combine(outputDir, DumpMetadataWriter.FileName));
                            }
                            catch
                            {
                                // ignored
                            }

                            var msg = "ERROR: Metadata file not found or encrypted.";
                            CoreLogger.WriteLine(msg);
                            WriteRunManifest(false);
                            PopulateOutputs(outputDir, outputs);
                            return new DumpResult(false, msg, warnings, outputs);
                        }
                    }
                }
                catch (Exception e)
                {
                    var msg = e.ToString();
                    CoreLogger.WriteLine(msg);
                    WriteRunManifest(false);
                    PopulateOutputs(outputDir, outputs);
                    return new DumpResult(false, msg, warnings, outputs);
                }

                DumpMetadata? dumpMetadata = null;
                try
                {
                    dumpMetadata = DumpMetadataWriter.WriteInitial(outputDir, il2cppPath, metadataPath, config);
                    writtenFiles.Add(Path.Combine(outputDir, DumpMetadataWriter.FileName));
                    PopulateOutputs(outputDir, outputs);
                }
                catch (Exception e)
                {
                    CoreLogger.WriteLine(e);
                    var msg = $"ERROR: Failed to write dump metadata: {e.Message}";
                    CoreLogger.WriteLine(msg);
                    WriteRunManifest(false);
                    PopulateOutputs(outputDir, outputs);
                    return new DumpResult(false, msg, warnings, outputs);
                }

                Metadata? metadata = null;
                Il2Cpp? il2Cpp = null!;
                try
                {
                    if (!Init(il2cppPath, metadataPath, config, out metadata, out il2Cpp, outputDir, dumpMetadata, request))
                    {
                        WriteRunManifest(false);
                        PopulateOutputs(outputDir, outputs);
                        return new DumpResult(false, "ERROR: An error occurred while processing.", warnings, outputs);
                    }

                    var dumpSuccess = ExecuteDump(metadata, il2Cpp, config, outputDir, out var dumpError, out counts, out manifestWarnings, out var execWrittenFiles, Path.GetFileName(request.ExecutablePath));
                    writtenFiles.AddRange(execWrittenFiles);
                    WriteRunManifest(dumpSuccess);
                    PopulateOutputs(outputDir, outputs);

                    return new DumpResult(
                        dumpSuccess,
                        dumpSuccess ? null : dumpError ?? "ERROR: An error occurred while processing.",
                        warnings,
                        outputs
                    );
                }
                catch (Exception e)
                {
                    CoreLogger.WriteLine(e);
                    WriteRunManifest(false);
                    PopulateOutputs(outputDir, outputs);
                    return new DumpResult(false, e.ToString(), warnings, outputs);
                }
                finally
                {
                    metadata?.Dispose();
                    il2Cpp?.Dispose();
                }
            }
        }

        public static DumpResult Execute(DumpRequest request) => Dump(request);

        internal static bool Init(
            string il2cppPath,
            string metadataPath,
            Config config,
            out Metadata metadata,
            out Il2Cpp il2Cpp,
            string? outputDir = null,
            DumpMetadata? dumpMetadata = null,
            DumpRequest? request = null)
        {
            CoreLogger.WriteLine("Initializing metadata...");
            var metadataBytes = File.ReadAllBytes(metadataPath);
            metadata = new Metadata(new MemoryStream(metadataBytes));
            var decryptor = request?.StringLiteralDecryptor;
            if (decryptor == null && !string.IsNullOrWhiteSpace(config.StringDecryptor) && !config.StringDecryptor.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                decryptor = StringLiteralDecryptorRegistry.Resolve(config.StringDecryptor, config.StringDecryptorKey);
            }
            metadata.StringDecryptor = decryptor;
            CoreLogger.WriteLine($"Metadata Version: {metadata.Version}");
            if (outputDir != null && dumpMetadata != null)
            {
                DumpMetadataWriter.UpdateMetadataVersion(outputDir, dumpMetadata, metadata.Version);
            }

            CoreLogger.WriteLine("Initializing il2cpp file...");
            var il2cppBytes = File.ReadAllBytes(il2cppPath);
            var il2cppMagic = BitConverter.ToUInt32(il2cppBytes, 0);
            var il2CppMemory = new MemoryStream(il2cppBytes);
            switch (il2cppMagic)
            {
                default:
                    throw new NotSupportedException("ERROR: il2cpp file not supported.");
                case 0x6D736100:
                    var web = new WebAssembly(il2CppMemory);
                    il2Cpp = web.CreateMemory();
                    break;
                case 0x304F534E:
                    var nso = new NSO(il2CppMemory);
                    il2Cpp = nso.UnCompress();
                    break;
                case uint peMagic when (peMagic & 0xFFFF) == 0x5A4D: //PE
                    il2Cpp = new PE(il2CppMemory);
                    break;
                case 0x464c457f: //ELF
                    if (il2cppBytes[4] == 2) //ELF64
                    {
                        il2Cpp = new Elf64(il2CppMemory);
                    }
                    else
                    {
                        il2Cpp = new Elf(il2CppMemory);
                    }
                    break;
                case 0xCAFEBABE: //FAT Mach-O
                case 0xBEBAFECA:
                    var machofat = new MachoFat(new MemoryStream(il2cppBytes));
                    if (machofat.fats == null || machofat.fats.Length == 0)
                    {
                        il2Cpp = null!;
                        return false;
                    }
                    int index;
                    if (request?.MachoFatIndex != null && request.MachoFatIndex.Value >= 0 && request.MachoFatIndex.Value < machofat.fats.Length)
                    {
                        index = request.MachoFatIndex.Value;
                    }
                    else if (request?.MachoFatSelector != null)
                    {
                        var selected = request.MachoFatSelector(machofat.fats);
                        if (!selected.HasValue || selected.Value < 0 || selected.Value >= machofat.fats.Length)
                        {
                            il2Cpp = null!;
                            return false;
                        }
                        index = selected.Value;
                    }
                    else
                    {
                        // Prefer 64-bit if available, otherwise first platform
                        index = 0;
                        for (var i = 0; i < machofat.fats.Length; i++)
                        {
                            if (machofat.fats[i].magic == 0xFEEDFACF)
                            {
                                index = i;
                                break;
                            }
                        }
                    }
                    var magic = machofat.fats[index].magic;
                    il2cppBytes = machofat.GetMacho(index);
                    il2CppMemory = new MemoryStream(il2cppBytes);
                    if (magic == 0xFEEDFACF)
                        goto case 0xFEEDFACF;
                    else
                        goto case 0xFEEDFACE;
                case 0xFEEDFACF: // 64bit Mach-O
                    il2Cpp = new Macho64(il2CppMemory);
                    break;
                case 0xFEEDFACE: // 32bit Mach-O
                    il2Cpp = new Macho(il2CppMemory);
                    break;
            }
            var version = config.ForceIl2CppVersion ? config.ForceVersion : metadata.Version;
            il2Cpp.SetProperties(version, metadata.metadataUsagesCount, metadata);
            CoreLogger.WriteLine($"Il2Cpp Version: {il2Cpp.Version}");
            if (config.ForceDump || il2Cpp.CheckDump())
            {
                if (il2Cpp is ElfBase elf)
                {
                    CoreLogger.WriteLine("Detected this may be a dump file.");
                    ulong? dumpAddr = null;
                    if (request?.DumpAddress.HasValue == true)
                    {
                        dumpAddr = request.DumpAddress.Value;
                    }
                    else if (request?.DumpAddressPrompt != null)
                    {
                        dumpAddr = request.DumpAddressPrompt();
                    }
                    if (dumpAddr.HasValue && dumpAddr.Value != 0)
                    {
                        il2Cpp.ImageBase = dumpAddr.Value;
                        il2Cpp.IsDumped = true;
                        if (!config.NoRedirectedPointer)
                        {
                            elf.Reload();
                        }
                    }
                }
                else
                {
                    il2Cpp.IsDumped = true;
                }
            }

            CoreLogger.WriteLine("Searching...");
            try
            {
                var flag = il2Cpp.PlusSearch(metadata.methodDefs.Count(x => x.methodIndex >= 0), metadata.typeDefs.Length, metadata.imageDefs.Length);
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    if (!flag && il2Cpp is PE)
                    {
                        CoreLogger.WriteLine("Use custom PE loader");
                        il2Cpp = PELoader.Load(il2cppPath);
                        il2Cpp.SetProperties(version, metadata.metadataUsagesCount, metadata);
                        flag = il2Cpp.PlusSearch(metadata.methodDefs.Count(x => x.methodIndex >= 0), metadata.typeDefs.Length, metadata.imageDefs.Length);
                    }
                }
                if (!flag)
                {
                    flag = il2Cpp.Search();
                }
                if (!flag)
                {
                    flag = il2Cpp.SymbolSearch();
                }
                if (!flag)
                {
                    // Try to use Il2CppInspectorRedux's script.json
                    var bridgeResult = Il2CppInspectorBridge.AutoDetect(il2cppPath);
                    if (bridgeResult.HasValue)
                    {
                        var (codeRegistration, metadataRegistration) = bridgeResult.Value;
                        il2Cpp.Init(codeRegistration, metadataRegistration);
                        flag = true;
                    }
                }

                if (!flag)
                {
                    if (request?.CodeRegistration.HasValue == true && request?.MetadataRegistration.HasValue == true)
                    {
                        il2Cpp.Init(request.CodeRegistration.Value, request.MetadataRegistration.Value);
                        flag = true;
                    }
                    else
                    {
                        CoreLogger.WriteLine("ERROR: Can't use auto mode to process file, try manual mode.");
                        CoreLogger.WriteLine("TIP: You can run Il2CppInspectorRedux first to generate script.json");
                        if (request?.ManualRegistrationPrompt != null)
                        {
                            var manual = request.ManualRegistrationPrompt();
                            if (manual.HasValue)
                            {
                                il2Cpp.Init(manual.Value.CodeRegistration, manual.Value.MetadataRegistration);
                                flag = true;
                            }
                        }
                    }
                }

                if (!flag)
                {
                    CoreLogger.WriteLine("ERROR: An error occurred while processing.");
                    return false;
                }
                if (il2Cpp.Version >= 27 && il2Cpp.IsDumped)
                {
                    var typeDef = metadata.typeDefs[0];
                    var il2CppType = il2Cpp.types[typeDef.byvalTypeIndex];
                    var typeDefinitionsOffset = metadata.Version >= 38
                        ? (ulong)metadata.header.typeDefinitions.offset
                        : metadata.header.typeDefinitionsOffset;
                    metadata.ImageBase = il2CppType.data.typeHandle - typeDefinitionsOffset;
                }
            }
            catch (Exception e)
            {
                CoreLogger.WriteLine(e);
                CoreLogger.WriteLine("ERROR: An error occurred while processing.");
                return false;
            }
            return true;
        }

        internal static bool ExecuteDump(
            Metadata metadata,
            Il2Cpp il2Cpp,
            Config config,
            string outputDir,
            out string? errorMessage,
            out RunManifestCounts counts,
            out RunManifestWarnings manifestWarnings,
            out List<string> writtenFiles,
            string? moduleName = null)
        {
            if (!outputDir.EndsWith(Path.DirectorySeparatorChar.ToString()) && !outputDir.EndsWith(Path.AltDirectorySeparatorChar.ToString()))
            {
                outputDir += Path.DirectorySeparatorChar;
            }
            var success = true;
            var errors = new List<string>();
            counts = new RunManifestCounts();
            manifestWarnings = new RunManifestWarnings();
            writtenFiles = new List<string>();

            if (metadata.StringDecryptor == null && !string.IsNullOrWhiteSpace(config.StringDecryptor) && !config.StringDecryptor.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                metadata.StringDecryptor = StringLiteralDecryptorRegistry.Resolve(config.StringDecryptor, config.StringDecryptorKey);
            }
            CoreLogger.WriteLine("Dumping...");
            var executor = new Il2CppExecutor(metadata, il2Cpp);
            var decompiler = new Il2CppDecompiler(executor);
            if (!decompiler.Decompile(config, outputDir))
            {
                success = false;
                if (!string.IsNullOrEmpty(decompiler.ErrorMessage))
                {
                    errors.Add(decompiler.ErrorMessage);
                }
                else
                {
                    errors.Add("ERROR: Some errors in dumping");
                }
            }
            writtenFiles.AddRange(decompiler.WrittenFiles);
            CoreLogger.WriteLine("Done!");

            counts.Types = decompiler.TypesCount;
            counts.Methods = decompiler.MethodsCount;
            counts.Fields = decompiler.FieldsCount;
            counts.Properties = decompiler.PropertiesCount;
            counts.Attributes = decompiler.CustomAttributeWrittenCount;
            counts.AttributesFailed = decompiler.CustomAttributeFailedCount;
            manifestWarnings.ByCategory.AttributeDecode = (int)decompiler.CustomAttributeFailedCount;


            StructGenerator? scriptGenerator = null;
            if (config.GenerateStruct)
            {
                CoreLogger.WriteLine("Generate struct...");
                try
                {
                    scriptGenerator = new StructGenerator(executor);
                    scriptGenerator.WriteScript(outputDir);
                    CoreLogger.WriteLine("Done!");
                }
                catch (Exception e)
                {
                    CoreLogger.WriteLine(e);
                    CoreLogger.WriteLine("ERROR: Some errors in generating struct");
                    errors.Add("ERROR: Some errors in generating struct");
                    success = false;
                }
                finally
                {
                    if (scriptGenerator != null)
                    {
                        counts.StringLiterals = scriptGenerator.StringLiteralsCount;
                        manifestWarnings.ByCategory.StringLiteral = scriptGenerator.StringLiteralsFailedCount;
                        writtenFiles.AddRange(scriptGenerator.WrittenFiles);
                    }
                    manifestWarnings.ByCategory.StringDecryptor = metadata.StringDecryptorFailedCount;
                }
            }
            if (config.GenerateDummyDll)
            {
                CoreLogger.WriteLine("Generate dummy dll...");
                try
                {
                    DummyAssemblyExporter.Export(executor, outputDir, config.DummyDllAddToken, writtenFiles);

                    CoreLogger.WriteLine("Done!");
                }
                catch (Exception e)
                {
                    CoreLogger.WriteLine(e);
                    CoreLogger.WriteLine("ERROR: Some errors in generating dummy dll");
                    errors.Add("ERROR: Some errors in generating dummy dll");
                    success = false;
                }
            }

            if (config.EmitFridaScript || config.EmitBookmarks)
            {
                var scriptJson = scriptGenerator?.ScriptJson;
                if (scriptJson == null)
                {
                    CoreLogger.WriteLine("WARNING: Native attach emitters skipped: script.json was not generated in the current execution.");
                }
                else
                {
                    var dumpModel = decompiler?.DumpModel;

                    if (config.EmitFridaScript)
                    {
                        CoreLogger.WriteLine("Generate frida stubs...");
                        try
                        {
                            var fridaPath = FridaScriptGenerator.Write(outputDir, scriptJson, dumpModel, moduleName);
                            writtenFiles.Add(fridaPath);
                            CoreLogger.WriteLine("Done!");
                        }
                        catch (Exception e)
                        {
                            CoreLogger.WriteLine(e);
                            CoreLogger.WriteLine("ERROR: Some errors in generating frida stubs");
                            errors.Add("ERROR: Some errors in generating frida stubs");
                            success = false;
                        }
                    }

                    if (config.EmitBookmarks)
                    {
                        CoreLogger.WriteLine("Generate bookmarks...");
                        try
                        {
                            var bookmarksPath = BookmarksGenerator.Write(outputDir, scriptJson);
                            writtenFiles.Add(bookmarksPath);
                            CoreLogger.WriteLine("Done!");
                        }
                        catch (Exception e)
                        {
                            CoreLogger.WriteLine(e);
                            CoreLogger.WriteLine("ERROR: Some errors in generating bookmarks");
                            errors.Add("ERROR: Some errors in generating bookmarks");
                            success = false;
                        }
                    }
                }
            }

            manifestWarnings.Total = manifestWarnings.ByCategory.AttributeDecode + manifestWarnings.ByCategory.StringLiteral + manifestWarnings.ByCategory.StringDecryptor;
            errorMessage = errors.Count > 0 ? string.Join(Environment.NewLine, errors) : null;
            return success;
        }

        internal static bool ExecuteDump(
            Metadata metadata,
            Il2Cpp il2Cpp,
            Config config,
            string outputDir,
            out string? errorMessage,
            out RunManifestCounts counts,
            out RunManifestWarnings manifestWarnings)
        {
            return ExecuteDump(metadata, il2Cpp, config, outputDir, out errorMessage, out counts, out manifestWarnings, out _);
        }

        internal static bool ExecuteDump(Metadata metadata, Il2Cpp il2Cpp, Config config, string outputDir, out string? errorMessage)
        {
            return ExecuteDump(metadata, il2Cpp, config, outputDir, out errorMessage, out _, out _);
        }

        internal static bool ExecuteDump(Metadata metadata, Il2Cpp il2Cpp, Config config, string outputDir)
        {
            return ExecuteDump(metadata, il2Cpp, config, outputDir, out _);
        }

        private static void PopulateOutputs(string outputDir, Dictionary<string, string> outputs)
        {
            var candidates = new[]
            {
                DumpMetadataWriter.FileName,
                "dump.cs",
                "dump.json",
                "script.json",
                "stringliteral.json",
                "stringliteral-indexed.json",
                "il2cpp.h",
                "frida_stubs.js",
                "bookmarks.json",
                "DummyDll"
            };

            foreach (var name in candidates)
            {
                var fullPath = Path.Combine(outputDir, name);
                if (name == "DummyDll" ? Directory.Exists(fullPath) : File.Exists(fullPath))
                {
                    outputs[name] = fullPath;
                }
            }
        }

        internal static Config CloneConfig(Config source)
        {
            return new Config
            {
                DumpMethod = source.DumpMethod,
                DumpField = source.DumpField,
                DumpProperty = source.DumpProperty,
                DumpAttribute = source.DumpAttribute,
                DumpFieldOffset = source.DumpFieldOffset,
                DumpMethodOffset = source.DumpMethodOffset,
                DumpTypeDefIndex = source.DumpTypeDefIndex,
                GenerateDummyDll = source.GenerateDummyDll,
                GenerateStruct = source.GenerateStruct,
                GenerateJsonDump = source.GenerateJsonDump,
                GenerateRunManifest = source.GenerateRunManifest,
                DummyDllAddToken = source.DummyDllAddToken,
                RequireAnyKey = source.RequireAnyKey,
                ForceIl2CppVersion = source.ForceIl2CppVersion,
                ForceVersion = source.ForceVersion,
                ForceDump = source.ForceDump,
                NoRedirectedPointer = source.NoRedirectedPointer,
                StringDecryptor = source.StringDecryptor,
                StringDecryptorKey = source.StringDecryptorKey,
                EmitFridaScript = source.EmitFridaScript,
                EmitBookmarks = source.EmitBookmarks,
                BatchMode = source.BatchMode,
                StopOnFirstError = source.StopOnFirstError
            };
        }
    }
}
