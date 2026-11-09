# Il2CppDumper

[![GitHub Release](https://img.shields.io/github/v/release/silvachaves241/Il2CppDumper?include_prereleases&label=release)](https://github.com/silvachaves241/Il2CppDumper/releases)
[![CI](https://github.com/silvachaves241/Il2CppDumper/actions/workflows/dotnet.yml/badge.svg)](https://github.com/silvachaves241/Il2CppDumper/actions/workflows/dotnet.yml)

中文说明请戳[这里](README.zh-CN.md)

Unity il2cpp reverse engineer

## About This Fork

This fork includes experimental adaptations for newer Unity/IL2CPP metadata versions, including v35/v38/v39 used by Unity 6000.x. The changes improve metadata parsing, method address recovery, DummyDll generation, and `il2cpp.h` generation for newer versions.

The newer-version support has only been self-tested on a small number of games. IL2CPP layouts, metadata formats, and protections vary by Unity version, platform, and project. If a specific game cannot be dumped, you need to debug and modify the source code yourself. I do not provide one-by-one adaptation for every game or protected build.

## Releases & Downloads

Pre-built self-contained packages for Windows x64 and Linux x64 are available on the [Releases page](https://github.com/silvachaves241/Il2CppDumper/releases). No .NET SDK or runtime installation is required.

### Release Artifacts
- **`Il2CppDumper-<version>-win-x64.zip`**: Standalone Windows x64 CLI dumper bundled with Python IDA/Ghidra scripts, `config.json`, and license.
- **`Il2CppDumper-<version>-linux-x64.tar.gz`**: Standalone Linux x64 CLI dumper with execution permissions preserved.
- **`Il2CppDumper-Diff-<version>-win-x64.zip`**: Standalone Windows x64 diff and struct drift analysis CLI tool.
- **`Il2CppDumper-Diff-<version>-linux-x64.tar.gz`**: Standalone Linux x64 diff and struct drift analysis CLI tool.
- **`SHA256SUMS.txt`**: SHA-256 checksums to verify archive integrity.

### Quick Execution
- **Windows**:
  ```cmd
  Il2CppDumper.exe <executable-file> <global-metadata.dat> <output-directory>
  ```
- **Linux**:
  ```bash
  tar -xzvf Il2CppDumper-v0.1.0-linux-x64.tar.gz
  chmod +x Il2CppDumper
  ./Il2CppDumper <executable-file> <global-metadata.dat> <output-directory>
  ```

### Release Versioning and Maintenance
- **Version Convention**: Releases follow semantic versioning tags in the form `vX.Y.Z` (e.g., `v0.1.0`).
- **Automated Workflow**: Pushing a tag matching `v*` or triggering `.github/workflows/release.yml` manually builds, smoke-tests on native OS runners, generates checksums, and creates a GitHub Release draft with the exact commit SHA recorded.
- **Português:** Pacotes independentes (self-contained) para Windows x64 e Linux x64 estão disponíveis na página de [Releases](https://github.com/silvachaves241/Il2CppDumper/releases), dispensando a instalação prévia do .NET SDK ou runtime. No Linux, extraia o `.tar.gz` e atribua permissão com `chmod +x Il2CppDumper`. A convenção de tags segue o padrão `vX.Y.Z` e o workflow em `.github/workflows/release.yml` valida build, testes e execuções de fumaça antes de criar/anexar a release e seus checksums SHA-256.

## Positioning

This fork is maintained for reverse engineering work — including the use cases upstream frowns on. That is deliberate.

**In scope:**

* Forensic analysis and security research of IL2CPP applications
* Cheat development and anti-cheat research on games you analyze
* Protection and obfuscation research (metadata obfuscation included — it is on-topic, not rejected)
* Protocol and client reconstruction, mod development, interoperability
* Automation and tooling built on top of the generated outputs (`dump.cs`, `dump.json`, `il2cpp.h`, DummyDll, `script.json`)

Feature requests, fixes, and improvements should target these workflows. Issues asking how to comply with a game's EULA are out of scope; issues about parsing harder targets are in.

## Legal

MIT licensed, inherited from upstream. Your responsibility, your jurisdiction, your target. Nothing here grants a right to break a contract or a law — those are yours to weigh.

## Features

* Complete DLL restore (except code), can be used to extract `MonoBehaviour` and `MonoScript`
* Supports ELF, ELF64, Mach-O, PE, NSO and WASM format
* Supports Unity 5.3 - 2022.2, with experimental support for newer Unity metadata versions such as v35/v38/v39 (Unity 6000.x)
* Supports generate IDA, Ghidra and Binary Ninja scripts to help them better analyze il2cpp files
* Supports generate structures header file
* Supports Android memory dumped `libil2cpp.so` file to bypass protection
* Support bypassing simple PE protection

## Usage

Run `Il2CppDumper.exe` and choose the il2cpp executable file and `global-metadata.dat` file, then enter the information as prompted

The program will then generate all the output files in current working directory

### Command-line

```
Il2CppDumper.exe <executable-file> <global-metadata> <output-directory>
Il2CppDumper.exe --batch <batch-file.json> [--stop-on-first-error]
```

#### Batch Mode (`--batch <file>`)

File-driven batch processing: one process dumps N `(executable, metadata, output)` pairs with per-entry configuration overrides, strict per-entry output isolation, and a deterministic aggregate summary file (`batch-result.json`).

* **Exit codes:**
  * `0`: All entries succeeded.
  * `1`: Any entry failed (partial failure).
  * `2`: Malformed batch file, missing batch file path, or duplicate output directories.

##### Batch file format (input)
```
[
  { "executable": "GameAssembly.dll", "metadata": "global-metadata.dat", "output": "out/abc", "config": { "RequireAnyKey": false } },
  { "executable": "libil2cpp.so",     "metadata": "global-metadata.dat", "output": "out/def" }
]
```

##### batch-result.json (aggregate output)
```
[
  { "index": 0, "executable": "GameAssembly.dll", "success": true,  "error": null, "outputs": ["out/abc/dump.cs"] },
  { "index": 1, "executable": "libil2cpp.so",     "success": false, "error": "...", "outputs": [] }
]
```

**Português:** Processamento em lote orientado a arquivo (`--batch <arquivo.json>`): executa o dump de N pares `(executável, metadados, saída)` em um único processo com sobreposição de configuração por entrada, isolamento estrito de saída e geração determinística de `batch-result.json`. Códigos de saída: `0` = todas as entradas com sucesso, `1` = falha em uma ou mais entradas (falha parcial), `2` = arquivo de lote malformado ou diretórios de saída duplicados.

### Library usage

`Il2CppDumper.Core` provides headless, in-process invocation without requiring console interaction or process spawning:

```csharp
using Il2CppDumper;
using Il2CppDumper.Core;

var request = new DumpRequest
{
    ExecutablePath = "GameAssembly.dll",
    MetadataPath = "global-metadata.dat",
    OutputDirectory = "output",           // optional; null uses a temporary directory
    Config = new Config(),                // optional; null uses default documented settings
    Log = Console.Out                     // optional; null for silent operation
};

DumpResult result = Dumper.Dump(request);
if (result.Success)
{
    foreach (var (artifact, path) in result.Outputs)
    {
        Console.WriteLine($"{artifact} -> {path}");
    }
}
else
{
    Console.WriteLine($"Dump failed: {result.Error}");
}
```

> **Note on `RequireAnyKey`:** When invoking `Il2CppDumper.Core` in-process, `Config.RequireAnyKey` has **no effect**. The library never blocks or prompts for key presses during unattended or automated runs.
>
> **Note on `Outputs` and `ProducedFiles`:** `DumpResult.Outputs` (mapping artifact identifier to its file or directory path) and `DumpResult.ProducedFiles` (concrete list of written file paths) contain only artifacts produced or updated during the current execution. When reusing an output directory across multiple runs, stale files from prior executions are excluded from the current run's results without deleting unrelated user files.

### Outputs

#### DummyDll

Folder, containing all restored dll files

Use [dnSpy](https://github.com/0xd4d/dnSpy), [ILSpy](https://github.com/icsharpcode/ILSpy) or other .Net decompiler tools to view

Can be used to extract Unity `MonoBehaviour` and `MonoScript`, for [UtinyRipper](https://github.com/mafaca/UtinyRipper), [UABE](https://7daystodie.com/forums/showthread.php?22675-Unity-Assets-Bundle-Extractor)

#### ida.py

For IDA

#### ida_with_struct.py

For IDA, read il2cpp.h file and apply structure information in IDA

#### il2cpp.h

structure information header file

#### ghidra.py

For Ghidra

#### Il2CppBinaryNinja

For BinaryNinja

#### ghidra_wasm.py

For Ghidra, work with [ghidra-wasm-plugin](https://github.com/nneonneo/ghidra-wasm-plugin)

#### script.json

For ida.py, ghidra.py and Il2CppBinaryNinja

#### stringliteral.json

Contains all stringLiteral reference usages emitted by `StructGenerator` (`value`, `address`). Preserves all memory references and call-site addresses in the binary.

**Português:** Contém todas as referências de uso de stringLiteral emitidas pelo `StructGenerator` (`value`, `address`). Preserva todas as referências de memória e endereços de chamada no binário.

#### stringliteral-indexed.json

Complete forensic string literal table emitted by `StructGenerator` as structured JSON (`index`, `value`, `dataIndex`, `length`). Provides a strictly sequential, 1-to-1 table of every literal in `global-metadata.dat` (including unreferenced literals and the final entry).

**Forensic usage:** When analyzing binaries or decompiled IL call sites that reference string literals by index (e.g., `GetStringLiteralFromIndex(n)`), entries can be resolved directly by querying `index = n`. The `dataIndex` and `length` fields provide exact section-relative byte offsets and sizes within the metadata string literal data section for byte-level validation against `global-metadata.dat`. To diff string tables across obfuscated game builds or patch updates, correlate entries by `index` (e.g. `jq '.[] | {index, value}' stringliteral-indexed.json`).

**Português:** Tabela forense completa de literais de string emitida pelo `StructGenerator` como JSON estruturado (`index`, `value`, `dataIndex`, `length`). Fornece uma tabela estritamente sequencial e 1-para-1 de cada literal no `global-metadata.dat` (incluindo literais sem referência e a última entrada).
**Uso forense:** Ao analisar binários ou chamadas de IL descompiladas que referenciam literais de string por índice (ex.: `GetStringLiteralFromIndex(n)`), as entradas podem ser resolvidas diretamente buscando `index = n`. Os campos `dataIndex` e `length` fornecem deslocamentos e comprimentos exatos em bytes dentro da seção de dados de literais para correlação direta com o `global-metadata.dat`. Para comparar tabelas de strings entre versões ofuscadas ou atualizações do jogo, correlacione as entradas por `index` (ex.: `jq '.[] | {index, value}' stringliteral-indexed.json`).

#### dump-metadata.json

Provenance sidecar written at the start of every run (success or failure), recording `tool` (`name`, `version`, `commit`), `inputs` (`il2cpp` and `metadata` resolved `path`, `length`, and streamed `sha256`), resolved `metadataVersion`, effective `config`, and `generatedAtUtc` (ISO-8601).

**Português:** Arquivo lateral de proveniência (`dump-metadata.json`) gravado no início de cada execução (sucesso ou falha), registrando `tool` (`name`, `version`, `commit`), `inputs` (`il2cpp` e `metadata` com `path` resolvido, `length` e `sha256` em streaming), `metadataVersion` resolvida, `config` efetiva e `generatedAtUtc` (ISO-8601).

#### dump.json

Machine-readable dump output (`GenerateJsonDump`) capturing the same structured information as `dump.cs` in JSON format (`types`, `fields`, `properties`, `methods`, `enums`, `nested`). Types, fields, properties, and methods preserve declared `attributes` (including `[Serializable]` on decorated types). Types (including nested types) preserve image and assembly attribution (`image`, `assembly`), establishing a stable cross-build type identity (`<image>!<fullName>`) to prevent homonymous types in different images from colliding.

**Português:** Saída de dump legível por máquina (`dump.json`, chave `GenerateJsonDump`), capturando as mesmas informações estruturadas de `dump.cs` em formato JSON (`types`, `fields`, `properties`, `methods`, `enums`, `nested`). Tipos, campos, propriedades e métodos preservam `attributes` declarados (incluindo `[Serializable]` em tipos anotados). Tipos (incluindo tipos aninhados) preservam atribuição de imagem e assembly (`image`, `assembly`), estabelecendo uma identidade estável entre builds (`<image>!<fullName>`) para evitar colisões entre tipos homônimos de imagens distintas.

#### run-manifest.json

Operational summary file (`GenerateRunManifest`, default `true`) written at the end of every run (success or failure), recording `tool` (`name`, `version`), inline `inputs` (`il2cpp`, `metadata` with `path`, `length`, `sha256`), `startedAt` (ISO-8601 UTC), `durationMs`, `success`, `effectiveConfig` (snapshot of the configuration in effect), `counts` (`types`, `methods`, `fields`, `properties`, `attributes`, `attributesFailed`, `stringLiterals`), `warnings` (`total`, `byCategory` containing `attributeDecode`, `stringLiteral`, `stringDecryptor`, `constantDecode`, and structured `items` array with `category`, `typeDefIndex`, `typeName`, `member`, `rawBytes`, `detail`), and deterministic streaming `outputs` (`path`, `sha256`, `bytes`). On failed runs, the manifest is written with `success: false` and partial data while preserving exit code semantics.

**Português:** Arquivo de manifesto operacional de execução (`run-manifest.json`, chave `GenerateRunManifest`, padrão `true`), gravado ao final de cada execução (sucesso ou falha). Registra `tool` (`name`, `version`), `inputs` inline (`il2cpp`, `metadata` com `path`, `length`, `sha256`), `startedAt` (ISO-8601 UTC), `durationMs`, `success`, `effectiveConfig` (snapshot das configurações efetivas aplicadas), `counts` (`types`, `methods`, `fields`, `properties`, `attributes`, `attributesFailed`, `stringLiterals`), `warnings` (`total`, `byCategory` com `attributeDecode`, `stringLiteral`, `stringDecryptor`, `constantDecode` e lista estruturada `items` com `category`, `typeDefIndex`, `typeName`, `member`, `rawBytes`, `detail`) e a lista determinística de `outputs` (`path`, `sha256`, `bytes`). Em execuções com falha, o manifesto é gravado com `success: false` e dados parciais, preservando a semântica de código de saída.

#### protection-report.json

Pre-analysis protection diagnostics report (`GenerateProtectionReport`, default `true`) generated as a side-channel before dumping. The diagnostic pass explains why protected binaries or obfuscated targets fail without altering the dump execution flow or blocking the process.

For Portable Executable (PE) targets, the report emits a per-section integrity baseline (`sections`) recording each section's `name`, `virtualSize`, `rawSize`, incremental `sha256`, and Shannon `entropy`, alongside header metadata `coffTimestamp` (ISO-8601 UTC) and `imageChecksum` (optional header checksum). Non-PE targets omit these PE-specific fields.

Findings vocabulary includes:
- `HighEntropySection`: Triggered when binary section entropy reaches or exceeds `7.20` on a Shannon scale of 0.00 to 8.00 bits per byte (indicates encrypted, packed, or compressed code/data).
- `PackerSectionSignature`: Section name matches generic packer signatures (`UPX0`, `UPX1`, `UPX2`, `.aspack`, `.adata`, `.mpress1`, `.mpress2`, `.themida`, `.vmp0`, `.vmp1`, `.pack`, `PELOCK`).
- `RedirectionHeuristic`: Identifies relocation stripping, raw/virtual size discrepancies, or dynamic initialization tags (`DT_INIT`, `.init_proc`, `LC_ENCRYPTION_INFO`) indicating pointer redirection.
- `MetadataSanity`: Validates `global-metadata.dat` magic (`0xFAB11BAF`) and supported version range (16–200), reporting `ok: true` or `ok: false`.

Remediation strings suggest valid configuration switches (`NoRedirectedPointer=true`, `ForceDump=true`, `ForceIl2CppVersion=true`, `ForceVersion=<version>`). When `GenerateProtectionReport` is `false`, no report file is written.

**Português:** Relatório de diagnóstico de proteção pré-análise (`protection-report.json`, chave `GenerateProtectionReport`, padrão `true`), gerado como canal auxiliar antes da tentativa de dump. O diagnóstico esclarece o motivo pelo qual alvos protegidos ou ofuscados falham, sem alterar o fluxo do dump nem bloquear o processo.

Para alvos Portable Executable (PE), o relatório inclui uma linha de base de integridade por seção (`sections`) contendo `name`, `virtualSize`, `rawSize`, `sha256` incremental e `entropy` de Shannon para cada seção, além de metadados de cabeçalho `coffTimestamp` (ISO-8601 UTC) e `imageChecksum` (checksum do cabeçalho opcional). Alvos que não utilizam formato PE omitem esses campos específicos.

Vocabulário de achados (`findings`):
- `HighEntropySection`: Acionado quando a entropia da seção atinge ou supera `7.20` na escala de Shannon (0.00 a 8.00 bits por byte), indicando código/dados compactados, criptografados ou empacotados.
- `PackerSectionSignature`: Nome de seção compatível com assinaturas genéricas de packers (`UPX0`, `UPX1`, `UPX2`, `.aspack`, `.adata`, `.mpress1`, `.mpress2`, `.themida`, `.vmp0`, `.vmp1`, `.pack`, `PELOCK`).
- `RedirectionHeuristic`: Identifica remoção de realocações, discrepâncias entre tamanho virtual e dados brutos, ou tags dinâmicas (`DT_INIT`, `.init_proc`, `LC_ENCRYPTION_INFO`) indicando redirecionamento de ponteiros.
- `MetadataSanity`: Valida o cabeçalho do `global-metadata.dat` quanto ao magic (`0xFAB11BAF`) e faixa de versões suportadas (16 a 200), emitindo `ok: true` ou `ok: false`.

Orientações de remediação recomendam opções de configuração existentes (`NoRedirectedPointer=true`, `ForceDump=true`, `ForceIl2CppVersion=true`, `ForceVersion=<version>`). Se `GenerateProtectionReport` for `false`, nenhum arquivo é gerado.

#### frida_stubs.js

Per-method native attach stubs for Frida (`EmitFridaScript`, default `false`). Emits deterministic, static method stubs with addresses from `script.json` resolved at runtime against the module base, with typing signatures sourced from `dump.json` when present (degraded path omits `"sig"` without failing when structured dump is absent). The emitted code contains no `eval` or remote network requests.

```javascript
// frida_stubs.js (generated, static)
// module: GameAssembly.dll; addresses are file offsets/VAs from script.json, resolved at runtime against module base
const methods = {
  "Namespace.Type.Method": { "va": "0x1000", "sig": "int (Type*, int, string)" }
};

function attach(name, callbacks) {
  const method = methods[name];
  if (!method) throw new Error("Method not found: " + name);
  const mod = Process.findModuleByName("GameAssembly.dll");
  if (!mod) throw new Error("Module not found: GameAssembly.dll");
  return Interceptor.attach(mod.base.add(ptr(method.va)), callbacks);
}
```

**Português:** Gerador de stubs de attach nativo para Frida (`frida_stubs.js`, chave `EmitFridaScript`, padrão `false`). Emite stubs estáticos e determinísticos com endereços de `script.json` resolvidos em runtime contra a base do módulo e assinaturas tipadas de `dump.json` quando disponível (em modo degradado omite `"sig"` sem falhar). Não utiliza `eval` nem chamadas de rede.

#### bookmarks.json

Tool-neutral bookmark export (`EmitBookmarks`, default `false`). Emits structured method entries sorted deterministically by name and address for integration with debuggers and analysis tooling.

```
[
  {
    "name": "Namespace.Type.Method",
    "address": "0x1000",
    "category": "method"
  }
]
```

**Português:** Exportação neutra de favoritos/marcadores (`bookmarks.json`, chave `EmitBookmarks`, padrão `false`). Emite entradas de métodos estruturadas e ordenadas deterministicamente por nome e endereço para importação em ferramentas de depuração e análise.

### Diffing (Il2CppDumper.Diff)

Standalone comparison tool consuming two structured dumps (`dump.json` and `script.json` address maps) and emitting a machine-readable delta `dump-diff.json`.

```
Il2CppDumper.Diff <left> <right> <output>
```

#### Inputs
* `<left>`: Directory containing `dump.json` (and optionally `script.json`, `stringliteral-indexed.json`, `dump-metadata.json`) or direct path to `dump.json`.
* `<right>`: Directory containing `dump.json` (and optionally `script.json`, `stringliteral-indexed.json`, `dump-metadata.json`) or direct path to `dump.json`.
* `<output>`: Output directory (where `dump-diff.json` will be written) or full path to destination json file.
* Note: `dump.cs` text is never an input.

#### Exit codes
* `0`: Identical (no differences found).
* `1`: Differences found.
* `2`: Usage / input error.

#### Type identity & ambiguity policy
Types are correlated across builds using the stable composite key `<image>!<fullName>` (or `<assembly>!<fullName>` when image is absent).
* **Legacy compatibility:** Legacy dumps lacking `image`/`assembly` metadata continue to correlate by `fullName` when names are globally unique.
* **Ambiguity policy:** If legacy input contains duplicate type full names without image/assembly identity, comparators exit with code `2` (input error) and report an explicit collision error instead of silently collapsing types or hiding deltas.
* **Additive schema fields:** In `dump-diff.json`, `DumpDiffTypeChange` emits additive `image` and `assembly` fields when available.
* **Method address ownership & ambiguity policy:** `ScriptMethod` records preserve additive `Image` and `Assembly` fields; in `dump-diff.json`, `DumpDiffAddressChange` emits additive `image` and `assembly` fields when available. Qualified methods correlate independently across images. Legacy exports lacking ownership detect unresolved homonymous permutations instead of asserting equivalence based solely on address multiset equality, setting `hasAmbiguousMethodAddresses` to `true` and exiting with code `1`. Shared generic addresses and valid unique methods remain supported without false ambiguity.

#### Schema reference (`dump-diff.json`)
```json
{
  "left":  { "source": "...", "sha256": "..." },
  "right": { "source": "...", "sha256": "..." },
  "types": {
    "added": [ "Namespace.TypeName" ],
    "removed": [ "Namespace.TypeName" ],
    "changed": [
      {
        "type": "Namespace.TypeName",
        "fields": {
          "added": [ "f1" ],
          "removed": [ "f2" ],
          "changed": [
            {
              "field": "f3",
              "left": { "type": "int", "offset": "0x10" },
              "right": { "type": "long", "offset": "0x10" }
            }
          ]
        },
        "methods": {
          "added": [ "m1" ],
          "removed": [ "m2" ],
          "changed": [
            {
              "method": "m3",
              "left": { "returnType": "void", "parameters": "" },
              "right": { "returnType": "int", "parameters": "" }
            }
          ]
        }
      }
    ]
  },
  "methods": {
    "addressChanged": [
      { "type": "Namespace.TypeName", "method": "MethodName", "left": "0x1000", "right": "0x2000" }
    ],
    "note": "Optional explanation if script.json is missing on one side"
  },
  "literals": {
    "addedByIndex": [ { "index": 12, "value": "new text" } ],
    "valueChangedByIndex": [ { "index": 5, "left": "old text", "right": "new text" } ]
  },
  "summary": {
    "addedTypes": 0,
    "removedTypes": 0,
    "addressChanges": 0,
    "valueChanges": 0
  }
}
```

**Português:** Ferramenta autônoma de comparação estruturada (`Il2CppDumper.Diff`), consumindo dois dumps estruturados (`dump.json` e mapas de endereço de `script.json`) e emitindo um delta legível por máquina (`dump-diff.json`).
* **Entradas:** diretórios ou caminhos diretos para `dump.json` de ambas as versões. `dump.cs` nunca é consumido.
* **Códigos de saída:** `0` = idênticos, `1` = diferenças encontradas, `2` = erro de uso ou entrada.
* **Identidade de tipos e ambiguidade:** tipos são correlacionados pela chave estável `<image>!<fullName>` (ou `<assembly>!<fullName>`). Dumps legados com nomes únicos mantêm compatibilidade via `fullName`; colisões sem imagem/assembly geram erro explícito com código `2`. `DumpDiffTypeChange` e `DumpDiffAddressChange` emitem campos aditivos `image` e `assembly`. Permutações de métodos homônimos sem posse geram ambiguidade explícita em vez de falso negativo por igualdade de multiconjunto.
* **Esquema:** inclui hashes de proveniência (`dump-metadata.json` quando disponível ou SHA-256 do arquivo em streaming), tipos adicionados/removidos/alterados, métodos com endereços alterados, literais por índice e sumário.

#### Field offset drift report (`field-offset-drift.json`)

Companion mode narrowed to native struct layout stability: given two structured dumps (`dump.json`), emits a per-struct field-offset drift report (`field-offset-drift.json`). Useful for native memory readers, ReClass layouts, and cheat/runtime accessors where offset shifts break memory layouts without signal.

```
Il2CppDumper.Diff --drift <left> <right> <output>
```

* **Inputs:** Directory containing `dump.json` or direct path to `dump.json`. Offsets are read strictly from structured `dump.json` (never parsed from `il2cpp.h` text).
* **Exit codes:**
  * `0`: Identical (no field offset drift found).
  * `1`: Drift found in one or more structs.
  * `2`: Usage / input error.
* **Classifications & Severity Map:**
  * `unchanged`: Omitted from report (structs with zero drift and unchanged size are completely absent).
  * `shifted`: Field offset changed between builds (`severity: "breaks-external-readers"`).
  * `removed`: Field present in baseline but missing in updated build (`severity: "breaks-external-readers"`).
  * `added` (appended at end): Field added at an offset past all existing fields (`severity: "leaf-consumers-may-survive"`).
  * `added` (middle-insert): Field inserted before or at an existing offset (`severity: "breaks-external-readers"`).
  * `size`: Struct-level size delta reported alongside; structs with size changes but no field drift report empty `fields: []`.

##### Schema reference (`field-offset-drift.json`)
```json
{
  "schemaVersion": 1,
  "structs": [
    {
      "struct": "Ns.Obf",
      "size": {
        "left": "0x120",
        "right": "0x128"
      },
      "fields": [
        {
          "name": "hp",
          "left": "0x28",
          "right": "0x2C",
          "drift": "shifted",
          "severity": "breaks-external-readers"
        },
        {
          "name": "mp",
          "left": "0x2C",
          "right": null,
          "drift": "removed",
          "severity": "breaks-external-readers"
        }
      ],
      "verdict": {
        "shifted": 1,
        "removed": 1,
        "added": 0
      }
    }
  ]
}
```

**Português:** Relatório de drift de offset de campos (`FieldOffsetDiff -> field-offset-drift.json`), focado na estabilidade de layout nativo de structs entre dois dumps (`dump.json`). Essencial para leitores nativos de memória, layouts ReClass e rotinas de runtime onde o deslocamento silencioso de offsets quebra cadeias derivadas sem sinalização em diffs de tipos por nome.
* **Comando:** `Il2CppDumper.Diff --drift <left> <right> <output>`
* **Entradas:** `dump.json` de ambas as versões (offsets extraídos estritamente do dump estruturado, nunca de texto de `il2cpp.h`).
* **Códigos de saída:** `0` = sem drift, `1` = drift detectado, `2` = erro de uso ou arquivo.
* **Classificação e severidade:** `shifted` e `removed` marcam `breaks-external-readers`; adições no final (`added` com offset superior aos existentes) marcam `leaf-consumers-may-survive`; structs sem drift são omitidas; mudanças apenas de tamanho geram `fields: []`.

#### oneof-members.json

Oneof member mapping extractor (`GenerateOneofMap`, default `false`). Recovers the canonical mapping of protobuf oneof member tags sharing a single `object` polymorphic storage slot across `IMessage` container classes, avoiding downstream heuristic regex divergence:

```json
{
  "jnj": {
    "group": { "property": "eptp", "offset": "0x68" },
    "members": [
      { "tag": 1, "constantFields": ["eptk"] }
    ]
  }
}
```

Alongside the mappings, an ambiguity channel (`oneof-ambiguities.json`) records non-clean const-run groupings (e.g. multiple `object` fields or consts without following instance storage).

**Português:** Extrator de mapeamento de membros oneof (`oneof-members.json`, chave `GenerateOneofMap`, padrão `false`). Recupera o mapeamento canônico de tags membros de oneof protobuf que compartilham o mesmo slot de armazenamento polimórfico `object` em classes contêineres `IMessage`:

```json
{
  "jnj": {
    "group": { "property": "eptp", "offset": "0x68" },
    "members": [
      { "tag": 1, "constantFields": ["eptk"] }
    ]
  }
}
```

Junto aos mapeamentos, o canal de ambiguidade (`oneof-ambiguities.json`) registra agrupamentos não limpos (ex.: múltiplos campos `object` ou consts sem campo de armazenamento correspondente).

### Configuration

All the configuration options are located in `config.json`

Available options:

* `DumpMethod`, `DumpField`, `DumpProperty`, `DumpAttribute`, `DumpFieldOffset`, `DumpMethodOffset`, `DumpTypeDefIndex`
  * Whether to output these information to dump.cs (`DumpProperty` and `DumpAttribute` default to `true`). In `dump.cs`, instance field offsets are emitted as `// 0x...` while static fields are explicitly annotated as `// static 0x...` to disambiguate statics-area storage slots from instance object layout.

* `GenerateDummyDll`, `GenerateStruct`, `GenerateJsonDump`, `GenerateRunManifest`, `GenerateProtectionReport`, `GenerateOneofMap`
  * Whether to generate these things (`GenerateStruct` controls `StructGenerator` output: `script.json`, `stringliteral.json`, `stringliteral-indexed.json`, and `il2cpp.h`; `GenerateJsonDump` controls `dump.json` output, defaults to `false`; `GenerateRunManifest` controls `run-manifest.json` operational summary, defaults to `true`; `GenerateProtectionReport` controls `protection-report.json` pre-analysis diagnostics, defaults to `true`; `GenerateOneofMap` controls `oneof-members.json` and `oneof-ambiguities.json` output, defaults to `false`)

* `DummyDllAddToken`
  * Whether to add token in DummyDll

* `RequireAnyKey`
  * Whether to press any key to exit at the end

* `ForceIl2CppVersion`, `ForceVersion`
  * If `ForceIl2CppVersion` is `true`, the program will use the version number specified in `ForceVersion` to choose parser for il2cpp binaries (does not affect the choice of metadata parser). This may be useful on some older il2cpp version (e.g. the program may need to use v16 parser on il2cpp v20 (Android) binaries in order to work properly)

* `ForceDump`
  * Force files to be treated as dumped

* `NoRedirectedPointer`
  * Treat pointers in dumped files as unredirected, This option needs to be `true` for files dumped from some devices

* `StringDecryptor`, `StringDecryptorKey`
  * Optional pluggable transform applied after a string literal resolves from metadata (default `"none"` / `null` — byte-identical to base behavior). Supported built-in modes include `"none"` and `"xor"` (with `StringDecryptorKey` or compact `"xor:<key>"` notation where key is a hex byte/string e.g. `"2b"` or `"0x2b"`). Custom transforms can be registered via `IStringLiteralDecryptor` or `StringLiteralDecryptorRegistry`.
  * **Additive JSON shapes**:
    * Inactive decryptor: `{"index": 128, "value": "raw text", "dataIndex": 100, "length": 8}`
    * Active decryptor: `{"index": 128, "value": "decrypted text", "dataIndex": 100, "length": 8, "raw": "AAjCEk=", "decryptor": "xor:2b"}`
  * Transform failure (exception or key mismatch) logs a warning with the literal index and emits the raw value without dropping entries.
  * **Trust note for custom hooks:** Custom `IStringLiteralDecryptor` implementations execute arbitrary in-process code in the context of the dumper runner; only register trusted decryptors when processing untrusted metadata.
  * **Português:** Transformação opcional aplicada após a resolução de literais de string dos metadados (padrão `"none"` / `null`, com comportamento idêntico em bytes). Suporta modo embutido `"xor"` (via `StringDecryptorKey` ou `"xor:<chave>"` em hexadecimal) e ganchos customizados via `IStringLiteralDecryptor`. Registra os campos aditivos `raw` (Base64) e `decryptor` enquanto ativo. Falhas registram aviso com o índice e emitem o valor bruto sem abortar a execução. Nota de segurança: ganchos customizados executam código no processo do dumper; use apenas decifradores confiáveis.

* `EmitFridaScript`, `EmitBookmarks`
  * Whether to generate native attach glue stubs (`EmitFridaScript` generates `frida_stubs.js`, defaults to `false`; `EmitBookmarks` generates `bookmarks.json`, defaults to `false`). When disabled, no new files are emitted.
  * **Português:** Controla a geração de scripts de attach nativo (`EmitFridaScript` gera `frida_stubs.js`, padrão `false`; `EmitBookmarks` gera `bookmarks.json`, padrão `false`). Quando desativadas, nenhum arquivo adicional é gravado no diretório de saída.

* `BatchMode`, `StopOnFirstError`
  * Whether to enable file-driven batch processing (`BatchMode`, default `false`) and whether to abort immediately upon encountering the first failed entry in a batch (`StopOnFirstError`, default `false`, which continues processing remaining entries). When `StopOnFirstError` is `true`, entries after the first failure are not attempted and no side effects are performed on their output directories.
  * **Português:** Configurações de modo em lote (`BatchMode`, padrão `false`) e parada na primeira falha (`StopOnFirstError`, padrão `false`, continuando o processamento das demais entradas). Quando `StopOnFirstError` for `true`, entradas subsequentes à falha não são executadas e seus diretórios de saída permanecem intocados.

## Common errors

#### `ERROR: Metadata file supplied is not valid metadata file.`  

Make sure you choose the correct file. Games may obfuscate this file for content protection purposes. Obfuscated metadata is a supported research target here, but per-game adaptation is not guaranteed — expect to debug and patch the parser yourself.

If your file is `libil2cpp.so` and you have a rooted Android phone, you can try my other project [Zygisk-Il2CppDumper](https://github.com/Perfare/Zygisk-Il2CppDumper), it can bypass this protection.

#### `ERROR: Can't use auto mode to process file, try manual mode.`

Please note that the executable file for the PC platform is `GameAssembly.dll` or `*Assembly.dll`

For newer Unity versions or protected builds, please investigate the failing parser path and patch the source locally. Per-game adaptation is not guaranteed.

#### `ERROR: This file may be protected.`

Il2CppDumper detected that the executable file has been protected, use `GameGuardian` to dump `libil2cpp.so` from the game memory, then use Il2CppDumper to load and follow the prompts, can bypass most protections.

If you have a rooted Android phone, you can try my other project [Zygisk-Il2CppDumper](https://github.com/Perfare/Zygisk-Il2CppDumper), it can bypass almost all protections.

## Credits

- Jumboperson - [Il2CppDumper](https://github.com/Jumboperson/Il2CppDumper)
