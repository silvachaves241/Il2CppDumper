# Il2CppDumper

Analisa binários Unity IL2CPP e o `global-metadata.dat` da mesma versão do aplicativo. Gera declarações, assemblies gerenciados para inspeção, mapas de endereços nativos e relatórios que podem ser consumidos por scripts, agentes de IA e ferramentas de engenharia reversa. O utilitário `Il2CppDumper.Diff` compara duas exportações.

## Índice

- [Instalação e primeiro uso](#instalação-e-primeiro-uso)
- [Linha de comando e processamento em lote](#linha-de-comando-e-processamento-em-lote)
- [Biblioteca .NET](#biblioteca-net)
- [Seleção de imagens e cobertura](#seleção-de-imagens-e-cobertura)
- [Arquivos gerados e integração com ferramentas](#arquivos-gerados-e-integração-com-ferramentas)
- [Comparação entre versões](#comparação-entre-versões)
- [Referência de configuração](#referência-de-configuração)
- [Diagnóstico e contribuição](#diagnóstico-e-contribuição)

## Instalação e primeiro uso

Baixe e extraia o pacote da plataforma desejada. Os pacotes autossuficientes para Windows x64 e Linux x64 incluem o runtime .NET. Os nomes começam com `Il2CppDumper-` para o exportador e com `Il2CppDumper-Diff-` para o comparador. Os arquivos Windows terminam em `-win-x64.zip` e os de Linux em `-linux-x64.tar.gz`. Use `SHA256SUMS.txt` da mesma versão para conferir a integridade do pacote. [Versões publicadas](https://github.com/silvachaves241/Il2CppDumper/releases)

Separe o binário IL2CPP e os metadados do mesmo artefato de distribuição. No Windows a entrada costuma ser `GameAssembly.dll` e, no Android, `libil2cpp.so`. Os formatos de entrada incluem PE, ELF de 32 e 64 bits, Mach-O de 32 e 64 bits, Mach-O universal, NSO e WebAssembly. Há suporte para imagens ELF capturadas em memória. A compatibilidade depende do leiaute do binário e dos metadados. O projeto tem cobertura de testes para estruturas recentes como v35, v38 e v39 e implementações experimentais para leiautes da série v104 em diante.

No Windows, execute com PowerShell.

```powershell
.\Il2CppDumper.exe "C:\Jogo\GameAssembly.dll" "C:\Jogo\global-metadata.dat" ".\saida" --static-only --output-json
```

No Linux, extraia o pacote e execute `./Il2CppDumper`. Caso o arquivo tenha perdido a permissão de execução na cópia, aplique `chmod +x Il2CppDumper`.

```sh
./Il2CppDumper "./libil2cpp.so" "./global-metadata.dat" "./saida" --output-json
```

## Linha de comando e processamento em lote

```text
Il2CppDumper <binário> <metadados> [<diretório-de-saída>] [--static-only] [--output-json] [--output-oneof] [--image <nome|id:N>]...
Il2CppDumper --batch <lote.json> [<relatório.json>] [--static-only] [--stop-on-first-error] [--output-json] [--output-oneof] [--image <nome|id:N>]...
Il2CppDumper --help
Il2CppDumper --version
```

| Opção | Efeito |
| --- | --- |
| `--output-json` | Ativa a exportação estruturada `dump.json`. |
| `--output-oneof`, `--generate-oneof-map` | Ativa o mapa e o relatório de ambiguidades de `oneof` protobuf. |
| `--image VALOR`, `--image=VALOR` | Seleciona uma imagem para as declarações. Pode ser repetida. |
| `--static-only` | Impede o carregador nativo do Windows de carregar o PE. A busca estática, um arquivo de registro do Il2CppInspectorRedux e o registro manual continuam disponíveis. |
| `--batch ARQUIVO`, `--batch=ARQUIVO` | Processa as entradas de um lote JSON. |
| `--stop-on-first-error` | Encerra o lote na primeira falha de geração. |
| `--help`, `-h`, `/?`, `/h` | Exibe a ajuda. |
| `--version`, `-v` | Exibe a versão. |

A CLI lê `config.json` ao lado da aplicação. As opções da linha de comando são aplicadas sobre essa configuração. Use aspas em caminhos e nomes com espaços. Sem diretório de saída explícito, a execução individual usa o diretório da aplicação. No Windows, sem caminhos de entrada, a CLI pode abrir o seletor de arquivos. `RequireAnyKey: false` desativa a pausa final em um terminal interativo.

Na execução individual, o código `0` indica geração bem-sucedida, `1` indica falha de execução e `2` indica que faltou o valor de `--image`.

### Processamento em lote

Cada entrada tem `executable`, `metadata`, `output` e um objeto opcional `config` com as chaves que deseja sobrepor. A opção global `StaticOnly: true` continua ativa para todas as entradas.

```json
[
  {
    "executable": "versao-a/GameAssembly.dll",
    "metadata": "versao-a/global-metadata.dat",
    "output": "saida/a",
    "config": { "GenerateJsonDump": true }
  },
  {
    "executable": "versao-b/GameAssembly.dll",
    "metadata": "versao-b/global-metadata.dat",
    "output": "saida/b",
    "config": { "GenerateJsonDump": true, "ValidateOutputs": true }
  }
]
```

Execute `Il2CppDumper --batch lote.json --static-only`. Caminhos relativos de entrada são procurados primeiro no diretório do lote e depois no diretório de trabalho. A saída de cada entrada é relativa ao diretório do lote. O argumento opcional de relatório é relativo ao diretório de trabalho. Na ausência dele, `batch-result.json` é escrito nos diretórios de trabalho e do lote. Com `BatchMode` ativo, também é possível passar apenas o caminho do JSON ou executar sem argumentos com um `batch.json` no diretório de trabalho.

O relatório registra `index`, `executable`, `success`, `error` e `outputs` por entrada, além de `imageSelection` e `outputValidation` quando aplicáveis. O código `0` indica sucesso de todas as entradas, `1` indica uma ou mais falhas de geração e `2` indica entrada inválida, como JSON malformado, caminho de lote ausente ou diretórios de saída duplicados ou sobrepostos.

## Biblioteca .NET

Adicione uma referência a `Il2CppDumper.Core`. A API roda no processo do chamador e usa as opções de `Config` sem carregar o `config.json` da CLI.

```csharp
using System;
using Il2CppDumper;
using Il2CppDumper.Core;

var pedido = new DumpRequest
{
    ExecutablePath = "GameAssembly.dll",
    MetadataPath = "global-metadata.dat",
    OutputDirectory = "saida",
    Config = new Config { StaticOnly = true, GenerateJsonDump = true },
    Log = Console.Out
};

DumpResult resultado = Dumper.Dump(pedido);
if (!resultado.Success)
    throw new InvalidOperationException(resultado.Error);

foreach (string caminho in resultado.ProducedFiles)
    Console.WriteLine(caminho);
```

| Propriedade de `DumpRequest` | Uso |
| --- | --- |
| `ExecutablePath`, `MetadataPath` | Caminhos obrigatórios para o binário e os metadados correspondentes. |
| `OutputDirectory`, `Config`, `Log` | Com `null`, usa diretório temporário, configuração padrão e execução silenciosa, respectivamente. |
| `MachoFatIndex`, `MachoFatSelector` | Escolhe a fatia de um Mach-O universal por índice ou função. A seleção automática prefere uma fatia de 64 bits. |
| `DumpAddress`, `DumpAddressPrompt` | Fornece o endereço-base de uma imagem ELF em memória quando necessário. |
| `CodeRegistration`, `MetadataRegistration`, `ManualRegistrationPrompt` | Fornece os dois endereços de registro como alternativa quando a descoberta automática falha. |
| `StringLiteralDecryptor` | Define uma implementação de `IStringLiteralDecryptor` para esta solicitação e tem precedência sobre a opção por nome. |

`Dumper.Execute(request)` é um alias de `Dumper.Dump(request)`. A biblioteca ignora `RequireAnyKey`. `DumpResult` expõe `Success`, `Error` e `Warnings`. O mapa `Outputs` associa nomes lógicos aos caminhos e `ProducedFiles` lista os arquivos individuais publicados pela execução. `ImageSelection` e `OutputValidation` descrevem o escopo e a validação quando usados. A API de lote é `BatchProcessor.RunBatch(batchFilePath, baseConfig, log, errorLog, resultOutputPath)`.

Para inspecionar apenas os metadados, use `Metadata(Stream)` com um fluxo legível que permita reposicionar a leitura. O leitor assume a propriedade do fluxo e o fecha ao ser descartado. Operações de escrita e relocação precisam de um fluxo gravável.

A partir do formato v38, o leitor confere tamanho, contagem e limites das seções de registros fixos antes de carregar suas tabelas. Se a descrição for incompatível com a largura de leitura, o diagnóstico identifica a seção e a versão do metadata. A tabela de assemblies aceita registros de 68 bytes e de 72 bytes com token de módulo.

```csharp
using System;
using System.IO;

using var arquivo = File.OpenRead("global-metadata.dat");
using var metadados = new Il2CppDumper.Metadata(arquivo);
Console.WriteLine(metadados.typeDefs.Length);
```

## Seleção de imagens e cobertura

Uma imagem IL2CPP representa um agrupamento de tipos dos metadados, normalmente associado a um assembly gerenciado. Use o nome exato, incluindo a extensão, ou o ID decimal original, que começa em zero. A comparação de nomes ignora maiúsculas e minúsculas. O ID resolve nomes repetidos.

```sh
Il2CppDumper GameAssembly.dll global-metadata.dat saida --output-json --image Game.dll --image id:66
```

As seleções se somam, sem duplicar imagens, e mantêm a ordem e os índices originais. Consulte os IDs em `dump-by-image/index.json` de uma exportação completa. Nomes inexistentes ou ambíguos e IDs inválidos produzem erro. `--image` substitui a lista do `config.json`. Uma entrada de lote pode definir a própria lista `ImageSelectors`. Na biblioteca, use `Config.ImageSelectors`. Lista vazia ou `null` inclui todas as imagens.

| Saída | Efeito da seleção |
| --- | --- |
| `dump.cs`, `dump-by-image/`, `dump.json` | Incluem apenas as declarações selecionadas. Um tipo aninhado cujo tipo externo ficou fora da seleção é emitido na raiz com o nome qualificado. |
| `DummyDll/` | Conjunto completo de assemblies e dependências. Em uma execução com seleção, arquivos de mesmo nome são atualizados e arquivos de outras execuções podem permanecer no diretório. |
| Mapas nativos, literais, cabeçalho, `oneof` e marcadores | Abrangem a entrada inteira quando habilitados. A saída Frida também abrange a entrada inteira e omite o enriquecimento de assinaturas quando o JSON está limitado às imagens selecionadas. |
| Proveniência e validação | Usam o par completo de entradas. A validação examina os artefatos registrados pela execução. |

O índice, o JSON estruturado e o manifesto registram `imageSelection` quando aplicável. Os artefatos do manifesto identificam a abrangência com `imageScope` de valor `selected-images` ou `full-input`. A seleção limita a exportação de declarações. O binário e os metadados ainda são analisados por inteiro.

## Arquivos gerados e integração com ferramentas

### Declarações e DummyDll

`dump.cs` é a saída agregada em sintaxe semelhante a C#. `dump-by-image/index.json` referencia os arquivos por imagem, usando caminhos relativos ao índice, IDs e nomes originais, intervalo de tipos e contagem de declarações. Os estados são `written`, `empty` e `failed`. Uma imagem vazia tem arquivo apenas com cabeçalho. Uma falha registra o erro sem caminho atual para aquela imagem. As duas saídas são habilitadas por padrão e têm chaves independentes. `DumpTypeDefIndex` controla as anotações dos índices e das imagens no texto.

`DummyDll/` contém assemblies gerados com corpos de método de preenchimento. Abra-os no ILSpy ou no dnSpy para inspecionar a estrutura gerenciada, atributos, `MonoBehaviour` e `MonoScript`. Para comportamento em execução ou relações de chamada, consulte o binário nativo. Em uma execução sem seleção, o exportador recria a pasta ao gerar os assemblies.

### dump.json

Formato estruturado para automação. A raiz tem `types`; percorra também `nested` para incluir os tipos aninhados. Cada definição pode trazer `image`, `assembly`, `fullName`, `bases`, `size`, `fields`, `properties`, `methods`, `enums` e `attributes` como `[Serializable]`. Leia o esquema em [DumpJson.cs](Il2CppDumper.Core/Outputs/DumpJson.cs).

O JSON completo e o filtrado registram `imageId` (índice da imagem), `typeDefIndex` (índice global da definição de tipo) e `typeToken` (token numérico do TypeDef) para cada declaração, inclusive em todos os níveis de `nested`. Os índices começam em zero e conservam os valores do metadata original quando há seleção. O token é 0 quando o leitor não disponibiliza um token para o tipo. Use essas coordenadas para cruzar artefatos do mesmo dump. Índices e tokens podem mudar entre builds, e um token pode se repetir em imagens diferentes.

Dumps completos de versões anteriores podem omitir essas três propriedades. Consumidores que validam o esquema estritamente devem aceitá-las tanto no JSON completo quanto no filtrado. O campo de raiz `imageSelection` aparece quando há seleção por imagem.

Correlacione tipos por `<image>!<fullName>` ou, na ausência da imagem, por `<assembly>!<fullName>`. Campos com `isStatic: true` usam o armazenamento estático. Trate um `offset` nulo como indisponível. Assinaturas gerenciadas vêm de `methods`. Para endereços nativos, consulte o mapa `script.json` da mesma execução e use a identidade do método disponível ali.

#### Métodos, interfaces e vtable

Cada entrada em `methods` traz `methodDefIndex`, o índice global do método no metadata, e `methodToken`, o token numérico disponível no leitor. Tipos de topo e aninhados usam o mesmo contrato. Duplicatas de token entre imagens são possíveis.

Cada tipo traz `vtable` na ordem das posições físicas do metadata. A lista é vazia se o tipo não tem posições. Uma posição contém:

| Campo | Significado |
| --- | --- |
| `position` | Posição física no tipo receptor, a partir de zero. Posições que apontam para o mesmo destino aparecem separadamente. |
| `methodDefIndex`, `resolved` | Índice global da declaração de destino e estado da resolução. Uma posição desconhecida é preservada com `methodDefIndex: null` e `resolved: false`. |
| `declaredSlot` | Slot declarado pelo método de destino, quando disponível. Pode diferir de `position`. |
| `methodSpecIndex` | Índice de MethodSpec quando a entrada codificada referencia uma instância genérica. O destino resolvido identifica a declaração MethodDef correspondente. |
| `interfaceStatus` | `interface` indica pelo menos uma origem confirmada. `class` indica ausência de origem de interface nos offsets disponíveis. `unknown` indica que a origem não pôde ser determinada. |
| `interfaces` | Origens confirmadas. Cada vínculo traz `interfaceTypeIndex` (índice do tipo nativo, inclusive para instâncias genéricas), `interfaceTypeDefIndex` (definição da interface), `interfaceMethodDefIndex` e `interfaceSlot` (slot declarado na interface). |

Quando um método é destino de despacho de interface em um tipo exportado, `methods[].interfaceDispatch` registra `receiverTypeDefIndex`, `position`, `declaredSlot` quando disponível e a lista de origens daquela posição. Isso permite consultar várias interfaces e receptores, inclusive quando o método de destino foi declarado por uma classe base. Os índices referem-se ao metadata completo e não são renumerados com `--image`. A seleção limita as declarações e os receptores da lista inversa. O destino ou a interface de uma posição pode estar em outra imagem e continuar referenciado pelo índice.

O despacho vem dos offsets de interface e das entradas da vtable registrados no metadata. Para instâncias de interface genérica, o vínculo identifica a instância e a definição de origem. O exportador não infere despacho adicional em tempo de execução nem substitui MethodSpecs sem resolução por métodos de nome semelhante. `DumpMethod: false` deixa de emitir as declarações e os vínculos inversos, e a lista `vtable` permanece disponível. Dumps anteriores podem omitir os novos campos.

### script.json e il2cpp.h

`script.json` contém `ScriptMethod`, `ScriptString`, `ScriptMetadata`, `ScriptMetadataMethod` e `Addresses`. Métodos podem incluir `MethodSignature`, `Image` e `Assembly`, úteis para correlacionar sobrecargas e homônimos. Em PE, os endereços de métodos são RVAs, relativos à base da imagem. Use o espaço de endereçamento do formato de entrada ao importar mapas. `il2cpp.h` descreve estruturas nativas recuperadas, incluindo campos herdados, estáticos e específicos de tipo.

Nas estruturas `*_VTable`, o prefixo `_N_` de cada campo indica a posição física na tabela do metadata. A mesma definição de método pode aparecer em várias posições, inclusive nas entradas de despacho de interface. Se o alvo de uma posição estiver indisponível, o campo é emitido como `_N_unknown`.

### stringliteral.json

`stringliteral.json` relaciona valores a referências detectadas no binário em registros com `value` e `address`.

### stringliteral-indexed.json

Tabela de literais dos metadados, incluindo entradas sem referência recuperada no binário. Se uma chamada a `GetStringLiteralFromIndex(n)` usa o índice `n`, busque por `index`. Os campos `dataIndex` e `length` descrevem o deslocamento e a extensão em bytes dentro da seção de dados de literais do `global-metadata.dat`.

```sh
jq --argjson indice 128 '.[] | select(.index == $indice)' stringliteral-indexed.json
```

Quando uma tabela v39 inclui uma linha terminal que marca o fim do último valor, o exportador mantém essa linha com `"status": "boundary"`. O Diff pula a linha terminal. Literais vazios reais continuam sendo valores comparáveis. O manifesto conta as linhas indexadas, incluindo a terminal quando presente.

Para aplicar uma transformação conhecida, defina `"StringDecryptor": "xor"` e uma chave hexadecimal em `StringDecryptorKey`, ou use a forma compacta `"StringDecryptor": "xor:2b"`. Com `"none"`, a transformação fica desativada. A tabela guarda `raw` (Base64) e `decryptor` quando a transformação está ativa. Em caso de falha, o exportador registra um aviso e preserva o valor bruto. Implementações registradas via `StringLiteralDecryptorRegistry.Register` executam dentro do processo do exportador.

### oneof-members.json

Mapeia tags protobuf que compartilham um campo `object` de uma classe `IMessage`. Por tipo, `group` identifica o campo e seu deslocamento, e `members` associa `tag` aos `constantFields`. `oneof-ambiguities.json` explica agrupamentos que o extrator não conseguiu confirmar, por exemplo classes com múltiplos campos `object`.

Quando um campo comum de texto interrompe uma sequência de constantes, o extrator exige armazenamento separado e um enum de caso acessível por uma propriedade somente leitura. Os valores do enum precisam distinguir as tags do grupo da constante que pertence ao campo comum.

### frida_stubs.js

Arquivo opcional gerado com `EmitFridaScript` e `GenerateStruct`. A tabela de métodos guarda o endereço em `va` e pode incluir `sig` quando a exportação estruturada fornece uma assinatura não ambígua. A função gerada `attach` usa a base do módulo em execução. Confira as coordenadas e a identidade do método antes de usar o valor em qualquer integração.

### bookmarks.json

Arquivo opcional gerado com `EmitBookmarks` e `GenerateStruct`. Cada marcador tem `name`, `address` e `category`. As entradas são ordenadas por nome e endereço.

```json
[
  { "name": "Exemplo.Tipo.Metodo", "address": "0x1000", "category": "method" }
]
```

### dump-metadata.json

Registro de proveniência com `tool` (nome, versão e commit quando disponível), `inputs` (caminho, tamanho e `sha256` do binário e dos metadados), `metadataVersion`, `config` e `generatedAtUtc`. O arquivo é iniciado antes da análise e atualizado quando a versão é detectada. Falhas no início da execução podem deixar campos indisponíveis.

### run-manifest.json

Manifesto da execução com `success`, `startedAt`, `durationMs`, `tool`, `inputs`, `effectiveConfig`, `counts`, `warnings` e o inventário `outputs` com `path`, `bytes` e `sha256`. Em falhas, pode conter dados parciais. Os contadores de declarações se referem à geração C#. Os avisos podem indicar tabela, tipo, membro ou bytes de origem.

Ao consumir uma pasta reutilizada, use o inventário da execução e confira os hashes dos arquivos necessários. Combine o par de hashes dos insumos e o escopo de imagens ao cruzar dados.

### protection-report.json

Diagnóstico anterior à exportação. Para PE, registra `coffTimestamp`, `imageChecksum` e `sections` com `name`, `virtualSize`, `rawSize`, `sha256` e `entropy`. Os achados são heurísticos.

| Achado | Indicação |
| --- | --- |
| `HighEntropySection` | Entropia de Shannon da seção igual ou superior a 7,20 bits por byte. |
| `PackerSectionSignature` | Nome de seção coincide com uma assinatura conhecida de empacotador. |
| `RedirectionHeuristic` | Sinais relacionados a realocações, diferenças entre tamanhos físico e virtual ou inicialização dinâmica. |
| `MetadataSanity` | Verifica a assinatura `0xFAB11BAF` e se a versão declarada está entre 16 e 200. A aprovação dessa etapa valida apenas o cabeçalho. |

### Validação das saídas

Ative `ValidateOutputs` para reabrir os assemblies e o mapa publicados pela execução. O validador confere relações `PropertyMap`, `EventMap` e `NestedClass`, a identidade dos tipos quando a imagem está disponível e os RVAs executáveis em `ScriptMethod`, `ScriptMetadataMethod.MethodAddress` e `Addresses` para PE. O zero usado como marcador em `ScriptMetadataMethod.MethodAddress` é ignorado. A verificação de endereços em outros formatos tem cobertura parcial.

Leia `DumpResult.OutputValidation` ou `outputValidation` no manifesto e no relatório do lote. Os estados são `passed`, `partial`, `failed` e `notRun`. Cada artefato informa `checks`, `limitations`, `errorCount` e até 50 `findings`. Avalie a validação separadamente de `Success` e do código de saída. As verificações são estáticas e estruturais.

O auxiliar embutido `DummyDll/Il2CppDummyDll.dll` passa pelas verificações estruturais do assembly. A conferência de tipos de origem se aplica a esse caminho quando houver uma imagem de entrada com o mesmo nome.

### Integração com ferramentas

| Ferramenta | Arquivos e uso |
| --- | --- |
| IDA | Abra o binário correspondente e rode `ida_py3.py` para aplicar o mapa `script.json`. Use `ida_with_struct_py3.py` para aplicar também as estruturas de `il2cpp.h`. O repositório inclui as variantes antigas `ida.py` e `ida_with_struct.py`. |
| Ghidra | Use `ghidra.py` para nomes e anotações. Para estruturas, rode `il2cpp_header_to_ghidra.py` no diretório de `il2cpp.h`, importe `il2cpp_ghidra.h` e rode `ghidra_with_struct.py` com o mapa. Para WebAssembly use `ghidra_wasm.py` e uma extensão de importação WASM compatível. |
| Binary Ninja | O [plug-in do repositório](Il2CppDumper.Core/Il2CppBinaryNinja) aceita `script.json` e um cabeçalho opcional. Converta `il2cpp.h` em `il2cpp_binja.h` com `il2cpp_header_to_binja.py`. |
| Hopper | O script legado `hopper-py3.py` aplica nomes de métodos a partir de `script.json`. |

## Comparação entre versões

```text
Il2CppDumper.Diff <anterior> <atual> <saída>
Il2CppDumper.Diff --drift <anterior> <atual> <saída>
Il2CppDumper.Diff --help
Il2CppDumper.Diff --version
```

### Entradas e códigos de saída

Cada entrada é um diretório com `dump.json` ou o caminho do próprio arquivo. A saída pode ser um diretório ou um caminho terminado em `.json`. Os relatórios são calculados a partir de dados estruturados. `dump.cs` e `il2cpp.h` são artefatos para inspeção. Use `-d` como alias de `--drift`, `-h` ou `/?` para ajuda e `-v` para versão.

Os dois modos aceitam dois dumps completos ou dois dumps com os mesmos nomes exatos de imagens selecionadas. Cada dump usa os IDs e intervalos da própria origem. Seleções incompatíveis, incompletas ou ambíguas geram erro. Tipos de dumps legados sem `image` e `assembly` são correlacionados pelo nome completo quando ele é único.

| Código | Resultado |
| --- | --- |
| `0` | Sem diferenças nas seções comparadas. Consulte as notas para saber quais seções ficaram sem cobertura. |
| `1` | Diferenças, alterações de deslocamento ou ambiguidades relevantes encontradas. |
| `2` | Erro de uso ou de entrada. Um relatório anterior no destino é preservado. |

### dump-diff.json

Relata tipos adicionados, removidos ou alterados, diferenças de campos e métodos, mudanças de endereço e literais por índice. Mudanças que envolvem somente offsets de campo são tratadas por `--drift`. Quando há homônimos ou sobrecargas que o comparador não consegue resolver, `hasAmbiguousMethodAddresses` sinaliza a ambiguidade. Os campos `left` e `right` identificam as entradas.

Para diretório ou caminho direto, o Diff usa o `run-manifest.json` da pasta, quando existente. Confere tamanho e SHA-256 do dump e dos mapas registrados antes de lê-los. Um mapa ausente do inventário fica fora da comparação, com motivo em `methods.note` ou `literals.note`. Se o dump estiver fora do inventário, só as declarações entram e o relatório usa o hash do próprio dump. A ausência ou alteração de um arquivo que consta no inventário retorna erro. Exportações legadas sem manifesto usam os mapas pelos nomes `script.json` e `stringliteral-indexed.json`.

#### Exemplo de dump-diff.json

```json
{
  "left": { "source": "anterior/dump.json", "sha256": "..." },
  "right": { "source": "atual/dump.json", "sha256": "..." },
  "types": {
    "added": [],
    "removed": [],
    "changed": [
      {
        "type": "Exemplo.Tipo",
        "fields": {
          "added": ["novoCampo"],
          "removed": ["campoAntigo"],
          "changed": [{ "field": "valor", "left": { "type": "int", "offset": "0x10" }, "right": { "type": "long", "offset": "0x20" } }]
        },
        "methods": {
          "added": ["NovoMetodo"],
          "removed": ["MetodoAntigo"],
          "changed": [{ "method": "Ler", "left": { "returnType": "void", "parameters": "" }, "right": { "returnType": "int", "parameters": "" } }]
        }
      }
    ]
  },
  "methods": { "addressChanged": [{ "type": "Exemplo.Tipo", "method": "Executar", "left": "0x1000", "right": "0x2000" }] },
  "literals": { "addedByIndex": [], "valueChangedByIndex": [{ "index": 5, "left": "antes", "right": "depois" }] },
  "summary": { "addedTypes": 0, "removedTypes": 0, "addressChanges": 1, "valueChanges": 1 }
}
```

### field-offset-drift.json

`--drift` compara offsets de campos de instância recuperados e, quando disponível, o tamanho do tipo. `shifted`, `removed` e inserções `added` no meio do leiaute recebem `breaks-external-readers`. Uma adição ao final pode receber `leaf-consumers-may-survive`. Tipos sem alterações são omitidos. Uma mudança apenas de tamanho tem `fields: []`.

#### Exemplo de field-offset-drift.json

```json
{
  "schemaVersion": 1,
  "structs": [
    {
      "struct": "Exemplo.Tipo",
      "size": { "left": "0x120", "right": "0x128" },
      "fields": [
        { "name": "valor", "left": "0x28", "right": "0x2C", "drift": "shifted", "severity": "breaks-external-readers" },
        { "name": "reserva", "left": "0x2C", "right": null, "drift": "removed", "severity": "breaks-external-readers" }
      ],
      "verdict": { "shifted": 1, "removed": 1, "added": 0 }
    }
  ]
}
```

### Possíveis renomeações

Os dois modos podem emitir `renameCandidates`, com grupos de tipos e campos que merecem comparação. `status: "candidate"` indica uma correspondência plausível, ainda sujeita a revisão. `status: "ambiguous"` lista as alternativas em `left` e `right` e os motivos em `ambiguities`.

- **Tipos** são pesquisados entre nomes adicionados e removidos na mesma imagem. `evidence` registra o caminho com GUID normalizado de `<PrivateImplementationDetails>`, o ordinal de máquina de estados com forma compatível ou a coincidência da forma declarada. A forma usa as bases, os campos e as assinaturas dos métodos na ordem do dump. Na regra geral, os offsets recuperados também entram na comparação. Token e TypeDef coincidentes servem como evidência adicional. Um token isolado ou em conflito gera ambiguidade.
- **Campos** são pesquisados em um tipo já pareado pelo comparador nominal. Um candidato exige nomes diferentes, o mesmo tipo declarado e uma posição de instância exclusiva em ambos os dumps. Tipos diferentes ou offsets compartilhados geram ambiguidade.
- Um par de tipos candidato pode trazer `declarationChanges` com os valores originais das seções alteradas, `matchingInstanceOffsets` com os offsets coincidentes por nome e `layoutChanges` quando há diferença observada de offsets ou tamanho. A lista de filhos diretos é comparada pelo caminho relativo ao pai.

As listas nominais, o `summary`, os vereditos de `--drift` e os códigos de saída continuam calculados pelas regras de cada modo. Uma hipótese de renomeação pode acompanhar adições, remoções ou mudanças de leiaute no mesmo relatório.

## Referência de configuração

Os padrões abaixo valem para `new Config()` e o `config.json` distribuído, exceto onde indicado. No lote, inclua as mesmas chaves no objeto `config` da entrada.

| Chave | Padrão | Efeito |
| --- | --- | --- |
| `ImageSelectors` | `null` | Seleciona declarações por nome exato de imagem ou ID original. |
| `GenerateAggregateDump` | `true` | Gera `dump.cs`. |
| `GeneratePerImageDump` | `true` | Gera os arquivos separados e o índice em `dump-by-image/`. |
| `DumpMethod` | `true` | Inclui declarações de métodos. |
| `DumpField` | `true` | Inclui declarações de campos. |
| `DumpProperty` | `true` | Inclui declarações de propriedades. |
| `DumpAttribute` | `true` | Decodifica e inclui atributos personalizados. |
| `DumpFieldOffset` | `true` | Inclui deslocamentos de campos recuperados. |
| `DumpMethodOffset` | `true` | Anota endereços nativos de métodos nas declarações C#. |
| `DumpTypeDefIndex` | `true` | Anota o índice da definição de tipo e a imagem nas declarações C#. |
| `GenerateDummyDll` | `true` | Gera os assemblies em `DummyDll/`. |
| `GenerateStruct` | `true` | Gera `il2cpp.h`, `script.json` e os dois arquivos de literais. Também permite as saídas Frida e de marcadores quando ativadas. |
| `GenerateJsonDump` | `false` | Gera `dump.json`. |
| `ValidateOutputs` | `false` | Valida os artefatos publicados pela execução. |
| `GenerateRunManifest` | `true` | Grava `run-manifest.json`. |
| `GenerateProtectionReport` | `true` | Grava `protection-report.json`. |
| `GenerateOneofMap` | `false` | Gera o mapa de `oneof` e o relatório de ambiguidades. |
| `DummyDllAddToken` | `true` | Adiciona anotações de token nos assemblies gerados. |
| `RequireAnyKey` | `true` | Solicita uma tecla ao final da CLI interativa. |
| `ForceIl2CppVersion` | `false` | Ativa a versão indicada em `ForceVersion` para o leitor do binário. |
| `ForceVersion` | `24.3` na biblioteca, `16` no arquivo distribuído | Versão usada pelo leitor do binário quando `ForceIl2CppVersion` está ativo. |
| `ForceDump` | `false` | Trata a entrada como imagem de memória. Para ELF pode exigir o endereço-base. |
| `NoRedirectedPointer` | `false` | Desativa o recarregamento de ponteiros ao fornecer a base de uma imagem ELF em memória com ponteiros sem redirecionamento. |
| `StaticOnly` | `false` | Desativa o carregador nativo do Windows na busca de registros PE. |
| `StringDecryptor` | `"none"` | Seleciona a transformação aplicada aos literais. |
| `StringDecryptorKey` | `null` | Fornece a chave para a transformação selecionada. |
| `EmitFridaScript` | `false` | Gera `frida_stubs.js` quando `GenerateStruct` está ativo. |
| `EmitBookmarks` | `false` | Gera `bookmarks.json` quando `GenerateStruct` está ativo. |
| `BatchMode` | `false` | Permite iniciar o lote pelo caminho posicional ou pelo `batch.json` do diretório de trabalho. |
| `StopOnFirstError` | `false` | Interrompe o lote na primeira falha de geração. |

Para alimentar uma ferramenta com declarações de uma imagem, sem gerar mapas ou assemblies, use esta configuração junto aos padrões restantes.

```json
{
  "StaticOnly": true,
  "ImageSelectors": ["Game.dll"],
  "GenerateJsonDump": true,
  "GenerateStruct": false,
  "GenerateDummyDll": false,
  "RequireAnyKey": false
}
```

## Diagnóstico e contribuição

| Sintoma | O que verificar |
| --- | --- |
| `ERROR: Metadata file supplied is not valid metadata file.` | Confira se é o `global-metadata.dat` correto, se o cabeçalho está íntegro e se o arquivo veio do mesmo binário. |
| `ERROR: Can't use auto mode to process file, try manual mode.` | Confirme o par binário/metadados e a versão detectada. Quando tiver endereços confirmados, informe ambos pelo registro manual ou pelo `script.json` do Il2CppInspectorRedux ao lado do binário. |
| `ERROR: This file may be protected.` | Consulte `protection-report.json` e localize a etapa de análise que falhou. A mensagem indica uma heurística. |
| Um mapa da pasta parece desatualizado | Confira o inventário do manifesto e o hash do mapa. Para exportações sem manifesto, gere em uma pasta nova ou confirme a origem dos arquivos por outro meio. |

### Onde procurar no código

| Caminho | Responsabilidade |
| --- | --- |
| [Il2CppDumper/Program.cs](Il2CppDumper/Program.cs) | Argumentos, configuração distribuída e interação da CLI. |
| [Il2CppDumper.Core/Dumper.cs](Il2CppDumper.Core/Dumper.cs) | Orquestração, carregamento de entradas, registro e geração de artefatos. |
| [Il2CppDumper.Core/Il2Cpp/Metadata.cs](Il2CppDumper.Core/Il2Cpp/Metadata.cs) | Leitura do cabeçalho, tabelas de metadados e índices dependentes da versão. |
| [Il2CppDumper.Core/ExecutableFormats](Il2CppDumper.Core/ExecutableFormats) | Leitores de binários e mapeamento entre endereços virtuais e offsets. |
| [Il2CppDumper.Core/Outputs](Il2CppDumper.Core/Outputs) | Geradores, esquemas, inventário, validação e processamento em lote. |
| [Il2CppDumper.Diff](Il2CppDumper.Diff) | Comparação estruturada, verificação de cobertura e relatórios de offsets. |
| [Il2CppDumper.Tests](Il2CppDumper.Tests) | Testes e entradas sintéticas que podem ser compartilhadas em regressões. |

### Compilação, testes e desempenho

A solução tem alvos `net6.0` e `net8.0`. Instale um SDK .NET compatível e os runtimes correspondentes para executar toda a suíte. Rode os comandos a partir da raiz do repositório.

```sh
dotnet build Il2CppDumper.sln -c Release
dotnet run --project Il2CppDumper/Il2CppDumper.csproj -c Release -f net8.0 -- --help
dotnet run --project Il2CppDumper.Diff/Il2CppDumper.Diff.csproj -c Release -f net8.0 -- --help
dotnet test Il2CppDumper.Tests/Il2CppDumper.Tests.csproj -c Release
```

Use `-f net8.0` ou `-f net6.0` para testar um runtime de cada vez. A CI executa as duas versões em Windows e Ubuntu. O [fluxo de publicação](.github/workflows/release.yml) gera pacotes verificados a partir das tags `vX.Y.Z`.

Para medir tempo, pico de memória e alocações gerenciadas, rode o projeto `ExportProfile` com o mesmo par de arquivos e um diretório novo por amostra. A opção `--per-image` inclui os arquivos de declarações por imagem. Compare amostras do mesmo sistema e runtime.

```sh
dotnet run --project benchmarks/ExportProfile/ExportProfile.csproj -c Release -- COMMIT GameAssembly.dll global-metadata.dat saida-perfil
```

Ao relatar um problema, inclua o commit ou a versão, o comando, a plataforma, a versão de metadados detectada, a configuração efetiva e a mensagem de erro. Quando possível, use um exemplo sintético que reproduza o problema ou informe hashes dos insumos que você tem autorização para compartilhar.

## Licença e créditos

Licença MIT. Projeto original de Perfare, com contribuições de Jumboperson e dos demais colaboradores registrados no Git. [Licença](LICENSE) · [Projeto original](https://github.com/Perfare/Il2CppDumper)
