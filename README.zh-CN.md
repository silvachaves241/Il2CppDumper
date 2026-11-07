# Il2CppDumper

[![GitHub Release](https://img.shields.io/github/v/release/silvachaves241/Il2CppDumper?include_prereleases&label=release)](https://github.com/silvachaves241/Il2CppDumper/releases)
[![CI](https://github.com/silvachaves241/Il2CppDumper/actions/workflows/dotnet.yml/badge.svg)](https://github.com/silvachaves241/Il2CppDumper/actions/workflows/dotnet.yml)

Unity il2cpp逆向工程

## 关于此 Fork

此 Fork 针对 Unity/IL2CPP 高版本 metadata 做了实验性适配，包括 Unity 6000.x 使用的 v35/v38/v39。当前主要改动包括高版本 metadata 解析、方法地址恢复、DummyDll 生成修复，以及高版本 `il2cpp.h` 生成。

高版本适配只在少量游戏上做过自测。不同 Unity 版本、平台、项目配置和保护方式都会影响 dump 结果。如果遇到无法 dump 的游戏，需要自行调试并修改源码适配。本项目不承诺对每个游戏或每个保护壳做一一适配。

## 发布与下载 (Releases & Downloads)

可在本 Fork 的 [Releases 页面](https://github.com/silvachaves241/Il2CppDumper/releases) 下载适用于 Windows x64 和 Linux x64 的独立自包含（self-contained）安装包，无需在系统中预先安装 .NET SDK 或 Runtime。

### 发布产物
- **`Il2CppDumper-<version>-win-x64.zip`**：包含主 dump 工具、IDA/Ghidra Python 脚本、`config.json` 与许可证的 Windows x64 独立运行包。
- **`Il2CppDumper-<version>-linux-x64.tar.gz`**：包含完整可执行权限的 Linux x64 独立运行包。
- **`Il2CppDumper-Diff-<version>-win-x64.zip`**：Windows x64 独立 Diff 与结构体偏移漂移分析工具。
- **`Il2CppDumper-Diff-<version>-linux-x64.tar.gz`**：Linux x64 独立 Diff 与结构体偏移漂移分析工具。
- **`SHA256SUMS.txt`**：用于校验所有发布压缩包完整性的 SHA-256 校验和清单。

### 运行方式
- **Windows**：
  ```cmd
  Il2CppDumper.exe <executable-file> <global-metadata.dat> <output-directory>
  ```
- **Linux**：
  ```bash
  tar -xzvf Il2CppDumper-v0.1.0-linux-x64.tar.gz
  chmod +x Il2CppDumper
  ./Il2CppDumper <executable-file> <global-metadata.dat> <output-directory>
  ```

### 版本规范与发布维护
- **版本规范**：Fork 采用语义化版本标签，格式为 `vX.Y.Z`（例如 `v0.1.0`）。
- **自动化工作流**：推送 `v*` 标签或通过 GitHub Actions 手动触发 `.github/workflows/release.yml`，系统会在原生 Windows 和 Linux 运行环境中完成全量测试、自包含编译、解压冒烟测试，并自动生成带有精确 commit SHA 与校验和的 GitHub Release。

## 定位

本 Fork 面向逆向工程工作——包括上游明确不欢迎的用例。这是有意为之。

**在范围内：**

* IL2CPP 应用的取证分析与安全研究
* 对所分析游戏的作弊开发与反作弊研究
* 保护与混淆研究（metadata 混淆属于受支持的研究目标，不拒绝处理）
* 协议与客户端重建、模组开发、互操作
* 基于生成产物的自动化与工具链（`dump.cs`、`dump.json`、`il2cpp.h`、DummyDll、`script.json`）

功能请求、修复与改进应围绕这些工作流。询问如何遵守某游戏 EULA 的 issue 不在范围内；关于解析更难目标的 issue 在范围内。

## 法律声明

基于上游的 MIT 许可证。责任在你，司法辖区在你，目标对象也在你。本文不授予违反合同或法律的权利——那是你自己权衡的事。

## 功能

* 还原DLL文件（不包含代码），可用于提取`MonoBehaviour`和`MonoScript`
* 支持ELF, ELF64, Mach-O, PE, NSO和WASM格式
* 支持 Unity 5.3 - 2022.2，并实验性适配 Unity 高版本 metadata（如 v35/v38/v39，Unity 6000.x）
* 生成IDA和Ghidra的脚本，帮助IDA和Ghidra更好的分析il2cpp文件
* 生成结构体头文件
* 支持从内存dump的`libil2cpp.so`文件以绕过保护
* 支持绕过简单的PE保护

## 使用说明

直接运行Il2CppDumper.exe并依次选择il2cpp的可执行文件和global-metadata.dat文件，然后根据提示输入相应信息。

程序运行完成后将在当前运行目录下生成输出文件

### 命令行

```
Il2CppDumper.exe <executable-file> <global-metadata> <output-directory>
Il2CppDumper.exe --batch <batch-file.json> [--stop-on-first-error]
```

#### 批处理模式（Batch Mode, `--batch <file>`）

文件驱动的批处理：在单个进程中批量 dump N 组 `(executable, metadata, output)` 对，支持每条目独立配置覆盖、严格的输出目录隔离，并生成确定性汇总文件 `batch-result.json`。

* **退出码：**
  * `0`: 所有条目均成功。
  * `1`: 任何条目失败（部分失败）。
  * `2`: 批处理文件格式错误、未指定路径或存在重复的输出目录。

##### 批处理文件格式（输入）
```
[
  { "executable": "GameAssembly.dll", "metadata": "global-metadata.dat", "output": "out/abc", "config": { "RequireAnyKey": false } },
  { "executable": "libil2cpp.so",     "metadata": "global-metadata.dat", "output": "out/def" }
]
```

##### batch-result.json（汇总输出）
```
[
  { "index": 0, "executable": "GameAssembly.dll", "success": true,  "error": null, "outputs": ["out/abc/dump.cs"] },
  { "index": 1, "executable": "libil2cpp.so",     "success": false, "error": "...", "outputs": [] }
]
```

**Português:** Processamento em lote orientado a arquivo (`--batch <arquivo.json>`): executa o dump de N pares `(executável, metadados, saída)` em um único processo com sobreposição de configuração por entrada, isolamento estrito de saída e geração determinística de `batch-result.json`. Códigos de saída: `0` = todas as entradas com sucesso, `1` = falha em uma ou mais entradas (falha parcial), `2` = arquivo de lote malformado ou diretórios de saída duplicados.

### 类库调用（Library usage）

`Il2CppDumper.Core` 提供了无控制台依赖（headless）、进程内直接调用的类型化 API，无需启动外部子进程或刮取终端文本：

```csharp
using Il2CppDumper;
using Il2CppDumper.Core;

var request = new DumpRequest
{
    ExecutablePath = "GameAssembly.dll",
    MetadataPath = "global-metadata.dat",
    OutputDirectory = "output",           // 可选；null 将使用临时目录
    Config = new Config(),                // 可选；null 将使用默认的完整配置
    Log = Console.Out                     // 可选；null 为静默运行
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

> **关于 `RequireAnyKey` 的说明：** 在进程内直接调用 `Il2CppDumper.Core` 时，`Config.RequireAnyKey` 配置项**完全不生效**。类库在无人值守或自动化调用路径中绝不会因等待按键而产生阻塞。

### 输出文件

#### DummyDll

文件夹，包含所有还原的DLL文件

使用[dnSpy](https://github.com/0xd4d/dnSpy)，[ILSpy](https://github.com/icsharpcode/ILSpy)或者其他.Net反编译工具即可查看具体信息

可用于提取Unity的`MonoBehaviour`和`MonoScript`，适用于[UtinyRipper](https://github.com/mafaca/UtinyRipper)或者[UABE](https://7daystodie.com/forums/showthread.php?22675-Unity-Assets-Bundle-Extractor)等

#### ida.py

用于IDA

#### ida_with_struct.py

用于IDA, 读取il2cpp.h文件并在IDA中应用结构信息

#### il2cpp.h

包含结构体的头文件

#### ghidra.py

用于Ghidra

#### Il2CppBinaryNinja

用于BinaryNinja

#### ghidra_wasm.py

用于Ghidra, 和[ghidra-wasm-plugin](https://github.com/nneonneo/ghidra-wasm-plugin)一起工作

#### script.json

用于IDA和Ghidra脚本

#### stringliteral.json

包含由 `StructGenerator` 生成的所有 stringLiteral 引用信息（`value`、`address`）。保留二进制文件中的所有内存引用和调用点地址。

**Português:** Contém todas as referências de uso de stringLiteral emitidas pelo `StructGenerator` (`value`, `address`). Preserva todas as referências de memória e endereços de chamada no binário.

#### stringliteral-indexed.json

包含由 `StructGenerator` 生成的完整 stringLiteral 取证结构化表（`index`、`value`、`dataIndex`、`length`）。提供 `global-metadata.dat` 中每个字面量的严格顺序 1 对 1 映射表（包含未被引用的字面量和末尾条目）。

**取证用途（Forensic usage）：** 在逆向分析二进制文件或反编译 IL 中通过索引引用字符串字面量的调用点（例如 `GetStringLiteralFromIndex(n)`）时，可以通过匹配 `index = n` 直接定位解析。`dataIndex` 与 `length` 字段提供了在 metadata 字符串字面量数据区段内的精确相对字节偏移与长度，便于与 `global-metadata.dat` 原始字节进行比对验证。若需在混淆游戏版本或补丁更新之间比对字符串表，可通过 `index` 关联条目进行 diff（例如 `jq '.[] | {index, value}' stringliteral-indexed.json`）。

**Português:** Tabela forense completa de literais de string emitida pelo `StructGenerator` como JSON estruturado (`index`, `value`, `dataIndex`, `length`). Fornece uma tabela estritamente sequencial e 1-para-1 de cada literal no `global-metadata.dat` (incluindo literais sem referência e a última entrada).
**Uso forense:** Ao analisar binários ou chamadas de IL descompiladas que referenciam literais de string por índice (ex.: `GetStringLiteralFromIndex(n)`), as entradas podem ser resolvidas diretamente buscando `index = n`. Os campos `dataIndex` e `length` fornecem deslocamentos e comprimentos exatos em bytes dentro da seção de dados de literais para correlação direta com o `global-metadata.dat`. Para comparar tabelas de strings entre versões ofuscadas ou atualizações do jogo, correlacione as entradas por `index` (ex.: `jq '.[] | {index, value}' stringliteral-indexed.json`).

#### dump-metadata.json

每次运行开始时（无论成功或失败）生成的溯源信息文件（provenance sidecar），记录 `tool`（`name`、`version`、`commit`）、`inputs`（`il2cpp` 和 `metadata` 的解析路径 `path`、字节长度 `length` 与流式计算的 `sha256`）、实际检测到的 `metadataVersion`、生效的 `config` 配置以及 `generatedAtUtc`（ISO-8601）。

**Português:** Arquivo lateral de proveniência (`dump-metadata.json`) gravado no início de cada execução (sucesso ou falha), registrando `tool` (`name`, `version`, `commit`), `inputs` (`il2cpp` e `metadata` com `path` resolvido, `length` e `sha256` em streaming), `metadataVersion` resolvida, `config` efetiva e `generatedAtUtc` (ISO-8601).

#### dump.json

机器可读的结构化转储输出（`GenerateJsonDump`），以 JSON 格式捕获与 `dump.cs` 相同的结构化信息（`types`、`fields`、`properties`、`methods`、`enums`、`nested`）。类型、字段、属性与方法均保留声明的 `attributes`（包括标记类的 `[Serializable]`）。

**Português:** Saída de dump legível por máquina (`dump.json`, chave `GenerateJsonDump`), capturando as mesmas informações estruturadas de `dump.cs` em formato JSON (`types`, `fields`, `properties`, `methods`, `enums`, `nested`). Tipos, campos, propriedades e métodos preservam `attributes` declarados (incluindo `[Serializable]` em tipos anotados).

#### run-manifest.json

运行状态清单文件（`GenerateRunManifest`，默认值为 `true`），在每次运行结束时（成功或失败）生成，记录 `tool`（`name`、`version`）、`startedAt`（ISO-8601 UTC）、`durationMs`、`success`、`effectiveConfig`（当前生效的完整配置快照）、`counts`（`types`、`methods`、`fields`、`properties`、`attributes`、`attributesFailed`、`stringLiterals`）、`warnings`（`total`、包含 `attributeDecode`、`stringLiteral`、`stringDecryptor` 与 `constantDecode` 的 `byCategory`）以及确定性的流式 `outputs`（`path`、`sha256`、`bytes`）。在失败运行时，清单将以 `success: false` 和部分数据写入，并保持退出代码语义不变。

**Português:** Arquivo de manifesto operacional de execução (`run-manifest.json`, chave `GenerateRunManifest`, padrão `true`), gravado ao final de cada execução (sucesso ou falha). Registra `tool` (`name`, `version`), `startedAt` (ISO-8601 UTC), `durationMs`, `success`, `effectiveConfig` (snapshot das configurações efetivas aplicadas), `counts` (`types`, `methods`, `fields`, `properties`, `attributes`, `attributesFailed`, `stringLiterals`), `warnings` (`total`, `byCategory` com `attributeDecode`, `stringLiteral`, `stringDecryptor` e `constantDecode`) e a lista determinística de `outputs` (`path`, `sha256`, `bytes`). Em execuções com falha, o manifesto é gravado com `success: false` e dados parciais, preservando a semântica de código de saída.

#### protection-report.json

预分析防护诊断报告（`GenerateProtectionReport`，默认值为 `true`），在转储分析前作为辅助侧信道生成。该诊断 pass 解释受保护或被混淆目标失败的根本原因，绝不会阻塞转储流程或更改转储行为。包含的诊断词汇（`findings`）：
- `HighEntropySection`：当二进制节的香农熵（Shannon entropy，0.00 至 8.00 bits/byte 尺度）达到或超过 `7.20` 时触发，指示加密、加壳或压缩的代码/数据。
- `PackerSectionSignature`：节名称匹配通用加壳器特征列表（`UPX0`、`UPX1`、`UPX2`、`.aspack`、`.adata`、`.mpress1`、`.mpress2`、`.themida`、`.vmp0`、`.vmp1`、`.pack`、`PELOCK`）。
- `RedirectionHeuristic`：检测重定位表剥离、虚拟尺寸与原始尺寸严重不匹配或动态初始化标记（`DT_INIT`、`.init_proc`、`LC_ENCRYPTION_INFO`），指示潜在的指针重定向。
- `MetadataSanity`：对 `global-metadata.dat` 的 magic（`0xFAB11BAF`）与版本范围（16 至 200）进行预检，输出 `ok: true` 或 `ok: false`。

修复建议字符串仅引用现有的有效配置项（`NoRedirectedPointer=true`、`ForceDump=true`、`ForceIl2CppVersion=true`、`ForceVersion=<version>`）。当 `GenerateProtectionReport` 设置为 `false` 时，不会写入该文件。

**Português:** Relatório de diagnóstico de proteção pré-análise (`protection-report.json`, chave `GenerateProtectionReport`, padrão `true`), gerado como canal auxiliar antes da tentativa de dump. O diagnóstico esclarece o motivo pelo qual alvos protegidos ou ofuscados falham, sem alterar o fluxo do dump nem bloquear o processo. Vocabulário de achados (`findings`):
- `HighEntropySection`: Acionado quando a entropia da seção atinge ou supera `7.20` na escala de Shannon (0.00 a 8.00 bits por byte), indicando código/dados compactados, criptografados ou empacotados.
- `PackerSectionSignature`: Nome de seção compatível com assinaturas genéricas de packers (`UPX0`, `UPX1`, `UPX2`, `.aspack`, `.adata`, `.mpress1`, `.mpress2`, `.themida`, `.vmp0`, `.vmp1`, `.pack`, `PELOCK`).
- `RedirectionHeuristic`: Identifica remoção de realocações, discrepâncias entre tamanho virtual e dados brutos, ou tags dinâmicas (`DT_INIT`, `.init_proc`, `LC_ENCRYPTION_INFO`) indicando redirecionamento de ponteiros.
- `MetadataSanity`: Valida o cabeçalho do `global-metadata.dat` quanto ao magic (`0xFAB11BAF`) e faixa de versões suportadas (16 a 200), emitindo `ok: true` ou `ok: false`.

Orientações de remediação recomendam opções de configuração existentes (`NoRedirectedPointer=true`, `ForceDump=true`, `ForceIl2CppVersion=true`, `ForceVersion=<version>`). Se `GenerateProtectionReport` for `false`, nenhum arquivo é gerado.

#### frida_stubs.js

用于 Frida 的原生附加桩代码生成（`EmitFridaScript`，默认值为 `false`）。生成确定性、纯静态的方法桩代码，使用 `script.json` 中的地址并在运行时基于模块基地址解析，并在存在 `dump.json` 时整合类型签名（降级路径下省略 `"sig"` 且不报错）。生成的代码不包含 `eval` 或远程网络请求。

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

工具中立的通用书签导出（`EmitBookmarks`，默认值为 `false`）。以确定性顺序按名称和地址导出结构化的方法项，便于导入反汇编器或调试器等外部逆向工具。

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

### 差异对比 (Diffing / Il2CppDumper.Diff)

独立结构化比对工具，消费两次分析生成的结构化转储（`dump.json` 与 `script.json` 地址映射），输出机器可读的增量差异报告 `dump-diff.json`。

```
Il2CppDumper.Diff <left> <right> <output>
```

#### 输入 (Inputs)
* `<left>`: 包含基线版本 `dump.json`（及可选的 `script.json`、`stringliteral-indexed.json`、`dump-metadata.json`）的目录或直接指向 `dump.json` 的文件路径。
* `<right>`: 包含更新版本 `dump.json`（及可选的 `script.json`、`stringliteral-indexed.json`、`dump-metadata.json`）的目录或直接指向 `dump.json` 的文件路径。
* `<output>`: 输出目录（将在此目录下生成 `dump-diff.json`）或目标 json 文件的完整路径。
* 注意：`dump.cs` 文本绝不作为输入。

#### 退出状态码 (Exit codes)
* `0`: 完全一致（未发现差异）。
* `1`: 发现差异。
* `2`: 参数使用或输入错误。

#### 数据模式参考 (Schema reference - `dump-diff.json`)
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
    "note": "若某侧缺少 script.json 时的说明"
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
* **Esquema:** inclui hashes de proveniência (`dump-metadata.json` quando disponível ou SHA-256 do arquivo em streaming), tipos adicionados/removidos/alterados, métodos com endereços alterados, literais por índice e sumário.

#### 结构体字段偏移漂移报告 (Field offset drift report - `field-offset-drift.json`)

专注于原生结构体内存布局稳定性的专用比对模式：消费两次分析生成的结构化转储（`dump.json`），生成每个结构体的字段偏移漂移报告（`field-offset-drift.json`）。对于原生内存读取器、ReClass 布局以及运行时/外挂访问层而言至关重要（偏移漂移会导致内存指针链在无感知状态下失效，而单纯按名称的类型比对无法直接回答此类问题）。

```
Il2CppDumper.Diff --drift <left> <right> <output>
```

* **输入 (Inputs):** 包含 `dump.json` 的目录或直接指向 `dump.json` 的路径。偏移量严格从结构化 `dump.json` 中读取（绝不从 `il2cpp.h` 文本中反向解析）。
* **退出状态码 (Exit codes):**
  * `0`: 完全一致（未发现字段偏移漂移）。
  * `1`: 在一个或多个结构体中发现漂移。
  * `2`: 参数使用或输入错误。
* **漂移分类与严重性等级 (Classifications & Severity Map):**
  * `unchanged`: 从报告中省略（无漂移且大小未变的结构体完全不在报告中出现）。
  * `shifted`: 字段偏移在不同构建版本间发生改变（`severity: "breaks-external-readers"`）。
  * `removed`: 字段在基线版本中存在但在新版本中被移除（`severity: "breaks-external-readers"`）。
  * `added`（末尾追加）: 字段被添加到所有现有字段的偏移之后（`severity: "leaf-consumers-may-survive"`）。
  * `added`（中间插入）: 字段插入在现有偏移之前或相同位置（`severity: "breaks-external-readers"`）。
  * `size`: 同时报告结构体级别的尺寸变化；仅尺寸变化但无字段变动的结构体报告空 `fields: []`。

##### 数据模式参考 (Schema reference - `field-offset-drift.json`)
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

### 关于config.json

* `DumpMethod`，`DumpField`，`DumpProperty`，`DumpAttribute`，`DumpFieldOffset`, `DumpMethodOffset`, `DumpTypeDefIndex`
  * 是否在dump.cs输出相应的内容（`DumpProperty` 和 `DumpAttribute` 默认值为 `true`）

* `GenerateDummyDll`，`GenerateStruct`，`GenerateJsonDump`，`GenerateRunManifest`，`GenerateProtectionReport`
  * 是否生成这些内容（`GenerateStruct` 控制 `StructGenerator` 输出的内容：`script.json`、`stringliteral.json`、`stringliteral-indexed.json` 和 `il2cpp.h`；`GenerateJsonDump` 控制 `dump.json` 输出，默认值为 `false`；`GenerateRunManifest` 控制运行状态清单 `run-manifest.json` 的输出，默认值为 `true`；`GenerateProtectionReport` 控制预分析诊断报告 `protection-report.json` 的输出，默认值为 `true`）

* `DummyDllAddToken`
  * 是否在DummyDll中添加token

* `RequireAnyKey`
  * 在程序结束时是否需要按键退出

* `ForceIl2CppVersion`，`ForceVersion`  
  * 当ForceIl2CppVersion为`true`时，程序将根据ForceVersion指定的版本读取il2cpp的可执行文件（Metadata仍然使用header里的版本），在部分低版本的il2cpp中可能会用到（比如安卓20版本下，你可能需要设置ForceVersion为16程序才能正常工作）

* `ForceDump`
  * 强制将文件视为dump文件

* `NoRedirectedPointer`
  * 将dump文件中的指针视为未重定向的, 从某些设备dump出的文件需要设置该项为`true`

* `StringDecryptor`, `StringDecryptorKey`
  * 从元数据解析字符串字面量后应用的可选解密转换插件（默认值为 `"none"` / `null`，保持字节级完全一致）。内置支持 `"none"` 以及 `"xor"`（通过 `StringDecryptorKey` 或简写 `"xor:<key>"`，key 为十六进制字节如 `"2b"` 或 `"0x2b"`）。亦可通过公共接口 `IStringLiteralDecryptor` 与 `StringLiteralDecryptorRegistry` 注册自定义插件。
  * **JSON 输出结构（附加字段）**：
    * 未启用解密器：`{"index": 128, "value": "raw text", "dataIndex": 100, "length": 8}`
    * 启用解密器：`{"index": 128, "value": "decrypted text", "dataIndex": 100, "length": 8, "raw": "AAjCEk=", "decryptor": "xor:2b"}`
  * 转换失败（异常或密钥不匹配）时将记录包含 literal 索引的警告日志，并安全输出 raw 原始值，不丢弃条目且不中断运行。
  * **自定义 Hook 安全提示（Trust note）：** 自定义 `IStringLiteralDecryptor` 实现是在 dumper 进程上下文中执行代码；在分析不可信的 metadata 时，请仅注册受信任的解密插件。
  * **Português:** Transformação opcional aplicada após a resolução de literais de string dos metadados (padrão `"none"` / `null`, com comportamento idêntico em bytes). Suporta modo embutido `"xor"` (via `StringDecryptorKey` ou `"xor:<chave>"` em hexadecimal) e ganchos customizados via `IStringLiteralDecryptor`. Registra os campos aditivos `raw` (Base64) e `decryptor` enquanto ativo. Falhas registram aviso com o índice e emitem o valor bruto sem abortar a execução. Nota de segurança: ganchos customizados executam código no processo do dumper; use apenas decifradores confiáveis.

* `EmitFridaScript`, `EmitBookmarks`
  * 是否生成原生附加粘合桩代码（`EmitFridaScript` 生成 `frida_stubs.js`，默认值为 `false`；`EmitBookmarks` 生成 `bookmarks.json`，默认值为 `false`）。禁用时不输出任何新文件。
  * **Português:** Controla a geração de scripts de attach nativo (`EmitFridaScript` gera `frida_stubs.js`, padrão `false`; `EmitBookmarks` gera `bookmarks.json`, padrão `false`). Quando desativadas, nenhum arquivo adicional é gravado no diretório de saída.

* `BatchMode`, `StopOnFirstError`
  * 是否启用文件驱动的批处理模式（`BatchMode`，默认值为 `false`）以及是否在批处理中遇到第一个失败条目时立即终止（`StopOnFirstError`，默认值为 `false`，继续处理后续条目）。当 `StopOnFirstError` 为 `true` 时，失败之后的条目将不会被执行，其输出目录也不会产生任何副作用。
  * **Português:** Configurações de modo em lote (`BatchMode`, padrão `false`) e parada na primeira falha (`StopOnFirstError`, padrão `false`, continuando o processamento das demais entradas). Quando `StopOnFirstError` for `true`, entradas subsequentes à falha não são executadas e seus diretórios de saída permanecem intocados.

## 常见问题

#### `ERROR: Metadata file supplied is not valid metadata file.`

global-metadata.dat已被加密。加密/混淆的 metadata 在本项目中属于受支持的研究目标，但不承诺对具体游戏逐一适配——请自行调试并修改解析器。

如果你的文件是`libil2cpp.so`并且你拥有一台已root的安卓手机，你可以尝试我的另一个项目[Zygisk-Il2CppDumper](https://github.com/Perfare/Zygisk-Il2CppDumper)，它能够无视global-metadata.dat加密

#### `ERROR: Can't use auto mode to process file, try manual mode.`

请注意PC平台的可执行文件是`GameAssembly.dll`或者`*Assembly.dll`

如果是 Unity 高版本或带保护的构建，请自行定位失败的解析路径并修改源码适配。本项目不承诺对具体游戏逐一适配。

#### `ERROR: This file may be protected.`

Il2CppDumper检测到可执行文件已被保护，使用`GameGuardian`从游戏内存中dump `libil2cpp.so`，然后使用Il2CppDumper载入按提示操作，可绕过大部分保护

如果你拥有一台已root的安卓手机，你可以尝试我的另一个项目[Zygisk-Il2CppDumper](https://github.com/Perfare/Zygisk-Il2CppDumper)，它能够绕过几乎所有保护

## 感谢

- Jumboperson - [Il2CppDumper](https://github.com/Jumboperson/Il2CppDumper)
