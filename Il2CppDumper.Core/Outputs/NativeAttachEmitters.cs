#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Il2CppDumper
{
    public class BookmarkEntry
    {
        public string name { get; set; } = string.Empty;
        public string address { get; set; } = string.Empty;
        public string category { get; set; } = "method";
    }

    public static class FridaScriptGenerator
    {
        public const string FileName = "frida_stubs.js";

        public static string NormalizeMethodName(string? rawName)
        {
            if (string.IsNullOrWhiteSpace(rawName))
                return "Unknown";
            return rawName.Replace("$$", ".");
        }

        public static string GetTypeShortName(string fullName)
        {
            if (string.IsNullOrWhiteSpace(fullName))
                return "Type";
            var shortName = fullName;
            var dot = shortName.LastIndexOf('.');
            if (dot >= 0 && dot < shortName.Length - 1)
                shortName = shortName.Substring(dot + 1);
            var tick = shortName.IndexOf('`');
            if (tick > 0)
                shortName = shortName.Substring(0, tick);
            return shortName;
        }

        private static void CollectTypes(IEnumerable<JsonDumpType>? types, List<JsonDumpType> target)
        {
            if (types == null)
                return;

            foreach (var type in types)
            {
                target.Add(type);
                CollectTypes(type.nested, target);
            }
        }

        public static Dictionary<string, string> BuildSignatureMap(JsonDumpModel? dumpModel)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (dumpModel?.types == null)
                return map;

            var allTypes = new List<JsonDumpType>();
            CollectTypes(dumpModel.types, allTypes);

            foreach (var type in allTypes)
            {
                if (string.IsNullOrWhiteSpace(type.fullName) || type.methods == null)
                    continue;

                var typeShort = GetTypeShortName(type.fullName);

                // Group methods by name to detect overloads in structured dump
                var methodsByName = type.methods.GroupBy(m => m.name, StringComparer.Ordinal);
                foreach (var group in methodsByName)
                {
                    // R3: Associate signatures only when there is an unambiguous 1:1 match.
                    // Overloaded methods in structured dump are ambiguous without exact type resolution,
                    // so we omit sig rather than guessing or picking an incorrect overload.
                    if (group.Count() != 1)
                        continue;

                    var method = group.First();
                    var retType = string.IsNullOrWhiteSpace(method.returnType) ? "void" : method.returnType;
                    var paramList = new List<string>();

                    if (!method.isStatic)
                    {
                        paramList.Add($"{typeShort}*");
                    }

                    if (method.parameters != null)
                    {
                        foreach (var p in method.parameters)
                        {
                            var pType = string.IsNullOrWhiteSpace(p.type) ? "void*" : p.type;
                            paramList.Add(pType);
                        }
                    }

                    var sig = $"{retType} ({string.Join(", ", paramList)})";
                    map[$"{type.fullName}.{group.Key}"] = sig;
                }
            }

            return map;
        }

        public static string Generate(ScriptJson? scriptJson, JsonDumpModel? dumpModel = null, string? moduleName = null)
        {
            var resolvedModuleName = string.IsNullOrWhiteSpace(moduleName) ? "GameAssembly.dll" : moduleName.Trim();
            // R4: Normalize newlines in header comment to prevent broken comment lines
            var cleanModuleComment = resolvedModuleName.Replace("\r", " ").Replace("\n", " ");

            var sb = new StringBuilder();
            sb.Append("// frida_stubs.js (generated, static)\n");
            sb.Append($"// module: {cleanModuleComment}; addresses are file offsets/VAs from script.json, resolved at runtime against module base\n");

            if (scriptJson?.ScriptMethod == null || scriptJson.ScriptMethod.Count == 0)
            {
                sb.Append("const methods = {};\n\n");
            }
            else
            {
                var sigMap = BuildSignatureMap(dumpModel);

                var methodsByBaseName = new Dictionary<string, List<ScriptMethod>>(StringComparer.Ordinal);
                foreach (var m in scriptJson.ScriptMethod)
                {
                    var baseName = NormalizeMethodName(m.Name);
                    if (!methodsByBaseName.TryGetValue(baseName, out var list))
                    {
                        list = new List<ScriptMethod>();
                        methodsByBaseName[baseName] = list;
                    }
                    list.Add(m);
                }

                var entries = new List<(string Key, string Va, string? Sig)>();

                foreach (var kvp in methodsByBaseName)
                {
                    var baseName = kvp.Key;
                    var methods = kvp.Value;

                    if (methods.Count == 1)
                    {
                        // Unambiguous method in script.json
                        var m = methods[0];
                        sigMap.TryGetValue(baseName, out var sig);
                        entries.Add((baseName, $"0x{m.Address:X}", sig));
                    }
                    else
                    {
                        // R3: Ambiguous overloads in script.json — omit sig to guarantee
                        // no RVA receives a mismatched signature, while preserving all RVAs with address keys.
                        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
                        for (int i = 0; i < methods.Count; i++)
                        {
                            var m = methods[i];
                            var key = i == 0 ? baseName : $"{baseName}_0x{m.Address:X}";
                            if (!seenKeys.Add(key))
                            {
                                key = $"{baseName}_0x{m.Address:X}";
                            }
                            entries.Add((key, $"0x{m.Address:X}", null));
                        }
                    }
                }

                entries.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.Ordinal));

                sb.Append("const methods = {\n");
                for (int i = 0; i < entries.Count; i++)
                {
                    var entry = entries[i];
                    var isLast = i == entries.Count - 1;
                    var comma = isLast ? "" : ",";

                    // R4: Serialize strings with JsonSerializer.Serialize to properly escape control characters (LF, CR, quotes)
                    var serializedKey = JsonSerializer.Serialize(entry.Key);
                    var serializedVa = JsonSerializer.Serialize(entry.Va);

                    if (!string.IsNullOrEmpty(entry.Sig))
                    {
                        var serializedSig = JsonSerializer.Serialize(entry.Sig);
                        sb.Append($"  {serializedKey}: {{ \"va\": {serializedVa}, \"sig\": {serializedSig} }}{comma}\n");
                    }
                    else
                    {
                        sb.Append($"  {serializedKey}: {{ \"va\": {serializedVa} }}{comma}\n");
                    }
                }
                sb.Append("};\n\n");
            }

            // R1 & R4: Use Process.findModuleByName and mod.base (Frida 17+ compatible)
            // with JSON-serialized literals for module name and error messages.
            var modStr = JsonSerializer.Serialize(resolvedModuleName);
            var errMethodStr = JsonSerializer.Serialize("Method not found: ");
            var errModStr = JsonSerializer.Serialize("Module not found: " + resolvedModuleName);

            sb.Append("function attach(name, callbacks) {\n");
            sb.Append("  const method = methods[name];\n");
            sb.Append($"  if (!method) throw new Error({errMethodStr} + name);\n");
            sb.Append($"  const mod = Process.findModuleByName({modStr});\n");
            sb.Append($"  if (!mod) throw new Error({errModStr});\n");
            sb.Append("  return Interceptor.attach(mod.base.add(ptr(method.va)), callbacks);\n");
            sb.Append("}\n");

            return sb.ToString();
        }

        public static string Write(string outputDir, ScriptJson? scriptJson, JsonDumpModel? dumpModel = null, string? moduleName = null)
        {
            var js = Generate(scriptJson, dumpModel, moduleName);
            var targetPath = Path.Combine(outputDir, FileName);
            File.WriteAllText(targetPath, js, new UTF8Encoding(false));
            return targetPath;
        }
    }

    public static class BookmarksGenerator
    {
        public const string FileName = "bookmarks.json";

        public static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true
        };

        public static List<BookmarkEntry> CreateEntries(ScriptJson? scriptJson)
        {
            var list = new List<BookmarkEntry>();
            if (scriptJson?.ScriptMethod == null)
                return list;

            foreach (var m in scriptJson.ScriptMethod)
            {
                list.Add(new BookmarkEntry
                {
                    name = FridaScriptGenerator.NormalizeMethodName(m.Name),
                    address = $"0x{m.Address:X}",
                    category = "method"
                });
            }

            return list
                .OrderBy(x => x.name, StringComparer.Ordinal)
                .ThenBy(x => x.address, StringComparer.Ordinal)
                .ToList();
        }

        public static string Generate(ScriptJson? scriptJson)
        {
            var entries = CreateEntries(scriptJson);
            return JsonSerializer.Serialize(entries, JsonOptions);
        }

        public static string Write(string outputDir, ScriptJson? scriptJson)
        {
            var json = Generate(scriptJson);
            var targetPath = Path.Combine(outputDir, FileName);
            File.WriteAllText(targetPath, json, new UTF8Encoding(false));
            return targetPath;
        }
    }
}
