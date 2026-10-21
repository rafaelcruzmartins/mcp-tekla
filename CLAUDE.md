# TeklaMcp: como foi feito e como refazer

Este arquivo tem duas partes:
- **Tutorial (Partes 1 a 9):** como este servidor MCP do Tekla Structures foi construído, passo a passo, para que outra IA consiga refazê-lo do zero. Serve também para adaptá-lo a outra versão do Tekla ou a outro software com API .NET.
- **Guia de manutenção (Parte 10):** como trabalhar neste repositório.

O panorama para o usuário está no `README.md`.

---

## Resultado final (o que você vai construir)

```
Claude Desktop ──stdio (JSON-RPC)──► TeklaMcp.exe  (.NET Framework 4.8, x64, processo próprio)
                                        │  Tekla Open API (Trimble.Remoting, memória compartilhada)
                                        ▼
                                   TeklaStructures.exe  (com um MODELO aberto)
```

- **Ferramentas:** 62 ferramentas tipadas e mais `execute_csharp`, que roda código livre na Open API.
- **Sem plugin dentro do Tekla, sem servidor HTTP, sem proxy:** o Claude Desktop lança o exe direto.
- **Testado em:** Tekla Structures 2026 SP2.1 (build 60107), ambiente Brazil, licença Educacional, Windows 11, .NET SDK 10.

---

## Parte 1: decidir entre um MCP existente e um feito do zero

1. **Procure um MCP nativo do fabricante.** Faça uma busca web por "Tekla MCP server", "Trimble Tekla Model Context Protocol" e "Tekla 2026 AI".
   - **Resultado em 2026-10:** não há MCP nativo. O *Tekla Model Assistant* (prévia no SP2) e o *Trimble Assistant* são assistentes internos e não expõem MCP.
   - **Projeto comunitário encontrado:** `teknovizier/tekla_mcp_server`, em Python e sob GPL-3. Ele não oferece execução de código arbitrário.
2. **Critério de decisão:** se o existente tiver menos funcionalidade que a Open API completa, faça do zero.
   - O ponto-chave é ter uma ferramenta "escape hatch" (`execute_csharp`) que dá acesso a tudo o que a API oferece.

## Parte 2: descobrir a instalação e o runtime

Não adivinhe: inspecione a máquina. Os comandos abaixo são em PowerShell.

```powershell
# Processo aberto e caminho do exe
Get-Process | ? ProcessName -match 'Tekla' | Select ProcessName,Id,Path
# Versão instalada (service pack = build)
Get-ItemProperty HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\* | ? DisplayName -match 'Tekla' | Select DisplayName,DisplayVersion,InstallLocation
# Runtime do host: clr.dll = .NET Framework, coreclr.dll = .NET (Core)
(Get-Process -Id <PID>).Modules | ? ModuleName -match '^(clr|coreclr)\.dll' | Select ModuleName
# DLLs da Open API e o framework alvo de cada uma
Get-ChildItem C:\TeklaStructures\2026.0\bin -Filter 'Tekla.Structures*.dll'
```

O que se descobriu:
- **Host:** o `TeklaStructures.exe` roda em **.NET Framework 4.8** (`clr.dll`).
- **DLLs principais:** `Tekla.Structures.Model/Drawing/Datatype/Catalogs.dll` ficam em `bin\` e são `netstandard2.0`.
- **`Tekla.Structures.dll`:** só existe em `bin\Net48Runtime\` (net48).
- **Comunicação:** a Open API é **out-of-process**. Um exe externo conversa com o Tekla aberto via `Trimble.Remoting` (memória compartilhada). Logo, **não é preciso um plugin dentro do host**, ao contrário do Revit e do Civil 3D.

## Parte 3: provar a conexão antes de escrever o servidor

Antes de qualquer arquitetura, faça um console mínimo que conecte. Foram feitas duas tentativas:

**Tentativa A (falhou): net10 com os pacotes NuGet `Tekla.Structures.Model` 2026.0.3.**
- **Erro:** `Cannot connect to remoting service 'Tekla.Structures.Model-TeklaStructures-Console:2026.0.7667.0' because it does not exist`.
- **Causa:** o nome do canal de remoting embute a **versão de arquivo** da DLL cliente. O NuGet é o build 7667 e o instalado é o 60107.
- **Detalhe extra:** os pacotes NuGet também excluem o runtime de `Trimble.Remoting`, `Tekla.Technology.Serialization` e `Tekla.Common.Geometry`. Eles esperam carregar essas DLLs da instalação.

**Tentativa B (funcionou): net48 referenciando as DLLs da instalação.**

```xml
<Reference Include="Tekla.Structures"><HintPath>C:\TeklaStructures\2026.0\bin\Net48Runtime\Tekla.Structures.dll</HintPath><Private>false</Private></Reference>
<Reference Include="Tekla.Structures.Model"><HintPath>C:\TeklaStructures\2026.0\bin\Tekla.Structures.Model.dll</HintPath><Private>false</Private></Reference>
```

```csharp
AppDomain.CurrentDomain.AssemblyResolve += (s, a) => {
    var n = new AssemblyName(a.Name).Name;
    foreach (var d in new[] { Bin + @"\Net48Runtime", Bin }) { var p = Path.Combine(d, n + ".dll"); if (File.Exists(p)) return Assembly.LoadFrom(p); }
    return null; };
Run();   // método separado, [NoInlining]: nenhum tipo do Tekla pode ser tocado antes do resolver existir
static void Run() { var m = new Tekla.Structures.Model.Model(); Console.WriteLine(m.GetConnectionStatus()); }
```

**Segunda armadilha:** mesmo com as DLLs certas, `GetConnectionStatus()` dava `false`.
- **Causa:** o Tekla estava na **tela inicial**. Os canais de remoting só existem com **um modelo aberto**.
- **Solução:** criar um modelo de teste pela interface (Novo → nome `MCP_TESTE` → template "Em branco"). Usamos computer-use para isso.

## Parte 4: mapear a API por reflexão (não confie na memória)

A documentação em developer.tekla.com bloqueia fetch (403). A fonte de verdade são as próprias DLLs. Em PowerShell 5.1, que é .NET Framework e carrega as DLLs net48:

```powershell
$b='C:\TeklaStructures\2026.0\bin'
[AppDomain]::CurrentDomain.add_AssemblyResolve({ param($s,$a) $n=(New-Object Reflection.AssemblyName($a.Name)).Name;
  foreach($d in "$b\Net48Runtime",$b){ $p="$d\$n.dll"; if(Test-Path $p){ return [Reflection.Assembly]::LoadFrom($p) } }; $null })
$m=[Reflection.Assembly]::LoadFrom("$b\Tekla.Structures.Model.dll")
$t=$m.GetType('Tekla.Structures.Model.Operations.Operation')
$t.GetMethods('Public,Static,DeclaredOnly') | % { $_.Name + '(' + (($_.GetParameters()|%{$_.ParameterType.Name+' '+$_.Name}) -join ', ') + ')' }
```

Tipos inspecionados e o que importa em cada um:

| Tipo | Uso |
|---|---|
| `Model` | `GetConnectionStatus`, `GetInfo`, `GetProjectInfo`, `SelectModelObject(Identifier)`, `GetModelObjectSelector`, `CommitChanges`, `GetReportPropertyDouble(ids, prop)` (em lote) |
| `ModelObject` | `Insert/Modify/Delete/Select`, `GetReportProperty(name, ref T)`, `GetAllUserProperties(ref Hashtable)`, `SetUserProperty` |
| `Beam`, `PolyBeam`, `ContourPlate`, `Position`, `Profile`, `Material` | Criação de peças (`new Beam(Beam.BeamTypeEnum.COLUMN)` para pilar) |
| `BoltArray`, `Weld`, `Connection`, `Detail`, `Component`, `ComponentInput` | Ligações e componentes |
| `Operations.Operation` | Move, copia, espelha, divide, combina, `RunMacro`, `IsNumberingUpToDateAll`, `CreateReportFromAll`, `CreateIFC4ExportFromAll`, `CreateNCFilesFromAll` |
| `ModelHandler` | `Save(comment, user)`, `Open(folder, autosaved)` |
| `UI.ModelObjectSelector`, `UI.ViewHandler`, `UI.ModelObjectVisualization`, `UI.View.CreateSnapshot` | Seleção, vistas, cores temporárias e captura de PNG |
| `Catalogs.CatalogHandler` | Perfis, materiais, parafusos, componentes, UDAs, impressoras |
| `Drawing.DrawingHandler`, `AssemblyDrawing`, `SinglePartDrawing`, `GADrawing`, `DPMPrinterAttributes` | Desenhos |
| `TeklaStructuresSettings.GetAdvancedOption` | Pastas do ambiente (`XS_MACRO_DIRECTORY`, `XS_SYSTEM`...) |

Dois cuidados na reflexão:
- **Enums ficam aninhados:** por exemplo `Beam+BeamTypeEnum`, `Position+PlaneEnum` e `BoltGroup+BoltTypeEnum`. Liste os nomes com `GetEnumNames()`.
- **`UI.SnapshotSettings` é struct:** não tem construtor público, use campos.

## Parte 5: esqueleto do projeto

```
TeklaMcp.csproj          net48, Exe, x64, LangVersion latest, AutoGenerateBindingRedirects
Program.cs               resolver → stdout para stderr → Host MCP stdio
Infrastructure/TeklaEnv  acha a pasta bin (TEKLA_BIN → processo aberto → registro → padrão) e instala o AssemblyResolve
Infrastructure/Tk        conexão (Model), busca de objetos, pontos, Describe/Report/Udas, PartTypes
Infrastructure/Fmt, Log  JSON (System.Text.Json) e log em %APPDATA%\TeklaMcp\logs
Server/ToolInvoker       fila única (SemaphoreSlim), CommitChanges nas escritas, envelope {ok,...}/isError
Tools/*.cs               uma classe [McpServerToolType] por área
tests/smoke_test.py      cliente MCP stdio cru (sem dependências)
build-and-deploy.ps1     compila, publica em %LOCALAPPDATA%\TeklaMcp\app e registra no Claude Desktop
```

Pacotes:
- `ModelContextProtocol` 2.2.0, que tem alvo `netstandard2.0` e por isso roda em net48.
- `Microsoft.Extensions.Hosting` 10.0.0.
- `Microsoft.CodeAnalysis.CSharp.Scripting` 4.14.0.

Referências do Tekla:
- Sempre com `Private=false` e HintPath em `$(TeklaBin)`. Elas **não** são copiadas e são resolvidas em runtime da instalação.
- Assim o mesmo exe conecta depois de um service pack: carrega a DLL nova e o canal casa com a versão.

`Program.Main`, nesta ordem:
1. `TeklaEnv.InstallResolver()` antes de qualquer tipo do Tekla. O resto fica em métodos `[MethodImpl(NoInlining)]`.
2. `Console.SetOut(Console.Error)`: a Open API escreve mensagens no Console, e no stdio o stdout é o canal JSON-RPC. O transporte do SDK usa `Console.OpenStandardOutput()`, então não é afetado.
3. `Host.CreateApplicationBuilder` → `AddMcpServer(...)` → `.WithStdioServerTransport()` → `.WithToolsFromAssembly(asm, Fmt.Options)`. Sem lista manual de ferramentas.
4. Modo `--check`: imprime o status da conexão. Útil para diagnóstico sem cliente MCP.

## Parte 6: o padrão de cada ferramenta

```csharp
[McpServerToolType]
public static class CreateTools
{
    [McpServerTool(Name = "create_beam")]
    [Description("Cria uma viga (Beam) entre dois pontos. ...")]   // PT-BR, diga unidades e formato
    public static Task<CallToolResult> CreateBeam(
        [Description("[x,y,z] mm")] double[] start, [Description("[x,y,z] mm")] double[] end, string profile, string? material = null, ...) =>
        ToolInvoker.Write("create_beam", new { start, end, profile }, () =>
        {
            var b = new Beam(Beam.BeamTypeEnum.BEAM) { StartPoint = Tk.Pt(start, "start"), EndPoint = Tk.Pt(end, "end") };
            ...
            if (!b.Insert()) throw new ToolException("O Tekla recusou a peça. Confira o perfil (list_profiles)...");
            return Tk.Describe(b);
        });
}
```

Regras que valeram a pena:
- **`ToolInvoker`:**
  - Use `Read` para consultas e `Write` para alterações. `Write` faz `Model.CommitChanges("MCP: <tool>")`, o que redesenha o modelo e permite Ctrl+Z.
  - **Uma chamada por vez**, com `SemaphoreSlim`: o remoting do Tekla não gosta de concorrência.
- **Entradas:**
  - Pontos como `double[]`, em mm e graus.
  - Objetos pelo `id` numérico, com `guid` como alternativa.
  - Enums como string, com `Tk.ParseEnum`. Na mensagem de erro, liste os valores válidos.
- **Erros:**
  - `ToolException(mensagem, details)` para erros que o modelo de IA consegue corrigir sozinho. Diga *qual ferramenta usar* para descobrir o valor certo.
- **Saída:**
  - Resumos com `Tk.Describe` em vez de serializar objetos do Tekla, que não serializam bem.
  - Paginação (`offset/limit`) e `total` nas listagens.
- **Propriedades de relatório:**
  - A API exige o tipo certo, então tente double → int → string.
  - Em lote, use `Model.GetReportPropertyDouble(ids, prop)`.
- **`GetAllObjectsWithType` não aceita a classe base `Part`:** use os tipos concretos (`Tk.PartTypes`).
- **Imagem:** `capture_view` gera o PNG com `View.CreateSnapshot` e anexa `ImageContentBlock.FromBytes(bytes, "image/png")` ao resultado.
- **Recursos sem API, como a numeração:** gere uma macro Akit (`.cs`) na pasta `XS_MACRO_DIRECTORY\modeling` e rode com `Operation.RunMacro`. A numeração é `akit.Callback("acmd_partnumbers_all", "" | "full", "main_frame")`.
  - Para descobrir comandos, procure macros prontas do ambiente: `Select-String` em `Environments\common\macros\modeling\*.cs`.

## Parte 7: `execute_csharp` (o escape hatch)

- **Roslyn:** `CSharpScript.Create<object>("#line 1\n" + code, options, typeof(ScriptGlobals))`.
  - `#line 1` acerta a linha nos erros.
  - Cache por SHA-256 do código.
  - Compila **fora** da fila e executa **dentro** dela.
- **Referências:** assemblies de `Model`, `Identifier` (Tekla.Structures), `DrawingHandler`, `CatalogHandler`, `Datatype`, além do próprio assembly do servidor.
- **Imports:** `System`, Linq, coleções, IO, `Tekla.Structures`, `.Model`, `.Geometry3d` e `.Model.Operations`.
  - **Não** importe `Tekla.Structures.Drawing`: `Part`, `View` e `ModelObject` ficam ambíguos. O script usa `using TSD = Tekla.Structures.Drawing;`.
- **Globals:** `Model`, `Drawings`, `Catalog`, `Print(obj)`, `Get(id)`, `Selected()`, `All(typeof(Beam))` e `P(x,y,z)`.
- **Retorno:** o valor da última expressão é simplificado (`ModelObject` vira o resumo de `Describe`). Com `commit=true`, roda `CommitChanges` ao final.

## Parte 8: testar como o Claude Desktop vai usar

`tests/smoke_test.py` fala MCP cru via stdio:
1. Envia `initialize` e depois `notifications/initialized`.
2. Envia `tools/list` e uma série de `tools/call`.
3. Imprime `[OK ]` ou `[ERR]` por chamada.

```powershell
python -I tests\smoke_test.py                    # leitura
python -I tests\smoke_test.py --write --extra    # cria MCP_TESTE_*, desenhos, IFC, relatório, salva
```

- Rode sempre num **modelo de teste**.
- Para depurar uma chamada isolada, use um script que manda um único `tools/call` com os argumentos vindos de um `.json`. Escreva caminhos com `/` ou gere o JSON em Python: heredocs no Git Bash podem estragar `\\`.
- Experimentos rápidos de API devem ir por `execute_csharp`, sem recompilar.

## Parte 9: publicar e registrar no Claude Desktop

- **Publicar:** `build-and-deploy.ps1 -Register`.
  - Compila e encerra o `TeklaMcp.exe` publicado, que o Desktop mantém aberto.
  - Copia para `%LOCALAPPDATA%\TeklaMcp\app`.
  - Grava em `mcpServers` a entrada `"tekla": {"command": "<app>\\TeklaMcp.exe", "args": []}`.
- **Onde fica o config:** nesta máquina, em `%LOCALAPPDATA%\Claude-Data\<id>\claude_desktop_config.json`, não em `%APPDATA%\Claude`.
  - Edite com Python `json` (`ensure_ascii=False`) e faça backup antes. O `ConvertTo-Json` do PowerShell 5.1 reformata e escapa o arquivo.
- **Depois:** reinicie o Claude Desktop.

### Checklist para adaptar a outra versão ou outro software

1. Existe MCP nativo ou comunitário? Compare com "API completa + execução de código".
2. A API é in-process (plugin, como Revit e AutoCAD) ou out-of-process (como o Tekla)? Isso decide entre plugin + HTTP e exe stdio.
3. Que runtime e que DLLs a **instalação** usa? Prefira as DLLs dela a pacotes NuGet de versão diferente.
4. Prove a conexão num console mínimo, com o host no estado certo (modelo aberto).
5. Mapeie a API por reflexão e escreva as ferramentas no padrão `ToolInvoker`.
6. Proteja o stdout, serialize as chamadas, devolva erros acionáveis e tenha um `execute_<linguagem>`.
7. Faça um smoke test via protocolo real, publique fora da pasta de build e registre no cliente.

---

## Parte 10: guia de manutenção

### Armadilhas já resolvidas

| Sintoma | Solução |
|---|---|
| `Cannot connect to remoting service '...:2026.0.7667.0'` | Usar as DLLs da instalação, não o NuGet |
| `GetConnectionStatus()` falso com o Tekla aberto | O Tekla está na tela inicial: só há canal com modelo aberto |
| Ambiguidade `ModelObject`/`Part`/`View` | Conflito entre `Tekla.Structures.Model` e `.Drawing`: use alias (`using DrawingHandler = ...`) |
| Ambiguidade `Task` | Existe `Tekla.Structures.Model.Task`: qualifique `System.Threading.Tasks.Task.WhenAny/Delay` |
| Ambiguidade `Component` | Conflito com `System.ComponentModel.Component`: qualifique `Tekla.Structures.Model.Component` |
| Relatório/IFC "ok" sem arquivo | O Tekla não aceita `/` nos caminhos: use `Path.GetFullPath` |
| Relatório com acentos quebrados | Os `.xsr` saem em ANSI 1252: `ReadText` tenta UTF-8 estrito antes |
| Macro termina "na hora" | `RunMacro` é assíncrono: espere `IsMacroRunning()` ficar true antes de esperar o fim |
| Templates de relatório não encontrados | Estão nas pastas do `XS_SYSTEM` (por papel), não só em `XS_TEMPLATE_DIRECTORY` |
| Peça criada com peso 0 | Sem material: a resposta traz `warning`; informe `material` (use `list_materials`) |
| PDF de desenho não sai (licença Educacional) | `PrintDrawing` retorna true e o `DPMPrinterCommand` roda (logs em `%LOCALAPPDATA%\Trimble\Tekla Structures\2026.0\Logs\DPMPrinter-*.log`), mas não grava; a resposta traz `hint` |

### Fluxo de trabalho

1. **Nova ferramenta:**
   - Crie um método estático `[McpServerTool(Name = "...")]` numa classe `[McpServerToolType]` em `Tools/`.
   - Escreva a `Description` em PT-BR.
   - Retorne `ToolInvoker.Read/Write(...)`. Não há lista de registro.
2. **Deploy:** rode `.\build-and-deploy.ps1`. Com `-Register`, também grava a entrada no Desktop.
3. **Teste:** abra o Tekla com `D:\TeklaStructures Model\MCP_TESTE` e rode `python -I tests\smoke_test.py --write --extra`.
4. **Logs:** `%APPDATA%\TeklaMcp\logs\`.
