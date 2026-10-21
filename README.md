# TeklaMcp

Servidor MCP para o **Tekla Structures 2026** (testado no SP2.1, build 60107). Com ele, o Claude Desktop lê e modifica o modelo aberto pela Open API.

## Por que feito do zero

- **A Trimble não tem um MCP nativo.** O *Tekla Model Assistant* (prévia no SP2) e o *Trimble Assistant* são assistentes internos do Tekla e não expõem MCP.
- **Existe um projeto comunitário,** [teknovizier/tekla_mcp_server](https://github.com/teknovizier/tekla_mcp_server), em Python e sob GPL-3. Ele cobre seleção, vistas, propriedades, componentes, desenhos e modelagem, mas não executa código arbitrário da Open API.
- **Este servidor oferece mais:**
  - **62 ferramentas tipadas** cobrindo modelagem, consulta, catálogos, desenhos, IFC, relatórios, NC, numeração e captura de imagem da vista.
  - **`execute_csharp`** dá acesso a qualquer parte da Open API que não tenha ferramenta própria.

## Como funciona

```
Claude Desktop ──stdio──► TeklaMcp.exe (.NET Framework 4.8, processo próprio)
                              │ Open API (Trimble.Remoting, memória compartilhada)
                              ▼
                         TeklaStructures.exe (com um modelo aberto)
```

- **Sem plugin dentro do Tekla e sem proxy:** o Claude Desktop lança o exe diretamente.
- **As DLLs da Open API vêm da própria instalação** (`C:\TeklaStructures\<versão>\bin`) e são resolvidas em runtime.
  - Os pacotes NuGet não servem: o canal de remoting leva a versão exata do arquivo. O NuGet 2026.0.3 é o build 7667, e o instalado é o 60107.
  - Depois de um service pack, basta rodar o exe de novo. Ele também acha a instalação pelo processo aberto, pelo registro ou pela variável `TEKLA_BIN`.
- **A Open API só responde com um modelo aberto.**

## Instalação

```powershell
cd C:\Claude\TeklaMcp
.\build-and-deploy.ps1 -Register
```

- O script compila, publica em `%LOCALAPPDATA%\TeklaMcp\app` e grava a entrada `tekla` no `claude_desktop_config.json`, com backup em `.bak-teklamcp`.
- Depois, reinicie o Claude Desktop.
- Para conferir a conexão: `TeklaMcp.exe --check`.

## Ferramentas

| Área | Ferramentas |
|---|---|
| Modelo | `get_tekla_status`, `get_project_info`, `set_project_info`, `list_phases`, `save_model`, `open_model` |
| Consulta | `list_objects`, `get_object_properties`, `get_report_properties`, `get_selected_objects`, `select_objects`, `get_objects_in_box`, `material_takeoff`, `check_numbering` |
| Criação | `create_beam`, `create_column`, `create_polybeam`, `create_contour_plate`, `create_grid`, `create_bolt_group`, `create_weld`, `insert_component` |
| Edição | `modify_part`, `set_user_properties`, `get_component_attributes`, `set_component_attributes`, `delete_objects`, `move_objects`, `copy_objects`, `mirror_objects`, `split_part`, `combine_beams`, `add_to_assembly` |
| Vista | `list_views`, `color_objects`, `set_visibility`, `clear_colors`, `zoom_to_objects`, `redraw_views`, `set_representation`, `show_only_selected`, `capture_view` (devolve PNG) |
| Catálogos | `list_profiles`, `list_materials`, `list_bolts`, `list_components`, `list_user_properties`, `list_report_templates` |
| Desenhos | `list_drawings`, `create_drawing`, `open_drawing`, `close_drawing`, `update_drawings`, `export_drawings_pdf`, `delete_drawings`, `get_drawing_objects` |
| Produção | `run_numbering`, `create_report`, `export_ifc`, `create_nc_files`, `run_macro` |
| Livre | `execute_csharp`: script Roslyn com `Model`, `Drawings`, `Catalog`, `Print()`, `Get(id)`, `Selected()`, `All(typeof(Beam))` |

**Convenções:**
- Unidades em mm e graus.
- Pontos no formato `[x, y, z]`.
- Objetos identificados pelo `id`.
- Toda ferramenta de escrita faz `CommitChanges`, então dá para desfazer com Ctrl+Z no Tekla.

## Limitações conhecidas

- **`export_drawings_pdf` com licença Educacional:** o Tekla aceita a impressão e roda o `DPMPrinterCommand`, mas não grava o PDF. A ferramenta avisa quando isso acontece.
- **Desenhos e relatórios dependem de numeração em dia.** Rode `run_numbering` antes.

## Teste

Com o Tekla aberto num **modelo de teste**:

```powershell
python -I tests\smoke_test.py                    # só leitura
python -I tests\smoke_test.py --write --extra    # cria objetos MCP_TESTE_*, desenhos, IFC, relatório e salva
```

Os logs ficam em `%APPDATA%\TeklaMcp\logs\`.
