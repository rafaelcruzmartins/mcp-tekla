using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Tekla.Structures.Model.Operations;
using TeklaMcp.Infrastructure;
using TeklaMcp.Server;
using UI = Tekla.Structures.Model.UI;

namespace TeklaMcp.Tools;

[McpServerToolType]
public static class ExportTools
{
    [McpServerTool(Name = "export_ifc")]
    [Description("Exporta IFC (IFC4 por padrão) do modelo todo ou dos objetos selecionados no Tekla.")]
    public static Task<CallToolResult> ExportIfc(
        [Description("Caminho completo do .ifc.")] string filePath,
        [Description("IFC4, IFC43 ou IFC2x3.")] string version = "IFC4",
        [Description("REFERENCE_VIEW, DESIGN_TRANSFER_VIEW, PRECAST_VIEW, BRIDGE_VIEW...")] string viewType = "REFERENCE_VIEW",
        bool selectedOnly = false,
        [Description("Conjuntos de propriedades (property sets) configurados no Tekla. Padrão: ['Default'].")] string[]? propertySets = null,
        [Description("GLOBAL, WORK_PLANE ou BASE_POINT.")] string location = "GLOBAL",
        [Description("Camadas por: 'NAME' ou 'CLASS'... (valor do diálogo).")] string layersAs = "NAME",
        [Description("Cores: 'CLASS' ou 'VIEW' (valor do diálogo).")] string coloring = "CLASS") =>
        ToolInvoker.Read("export_ifc", new { filePath, version, viewType, selectedOnly }, () =>
        {
            _ = Tk.Model;
            filePath = Path.GetFullPath(filePath); // o Tekla não aceita "/" nos caminhos
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            var v = Tk.ParseEnum(version, Operation.IFCExportVersionEnum.IFC4);
            var view = Tk.ParseEnum(viewType, Operation.IFCExportViewTypeEnum.REFERENCE_VIEW);
            var loc = Tk.ParseEnum(location, Operation.ExportBasePoint.GLOBAL);
            var psets = (propertySets is { Length: > 0 } ? propertySets : ["Default"]).ToList();
            var flags = new Operation.IFCExportFlags();
            var ok = selectedOnly
                ? Operation.CreateIFC4ExportFromSelected(filePath, view, v, new List<string>(), psets, loc, layersAs, coloring, flags, "")
                : Operation.CreateIFC4ExportFromAll(filePath, view, v, new List<string>(), psets, loc, layersAs, coloring, flags, "");
            return new { ok, filePath, exists = File.Exists(filePath), sizeKb = File.Exists(filePath) ? new FileInfo(filePath).Length / 1024 : 0 };
        });

    [McpServerTool(Name = "create_report")]
    [Description("Gera um relatório do Tekla a partir de um template (.rpt do ambiente, ex.: 'Material_List', 'Assembly_list', 'Part_list') para o modelo todo ou a seleção. Devolve o conteúdo se for texto.")]
    public static Task<CallToolResult> CreateReport(string template,
        [Description("Arquivo de saída (caminho completo). Padrão: pasta Reports do modelo.")] string? outputFile = null,
        bool selectedOnly = false, string title1 = "", string title2 = "", string title3 = "",
        [Description("Devolve até N caracteres do conteúdo.")] int returnChars = 20000) =>
        ToolInvoker.Read("create_report", new { template, outputFile, selectedOnly }, () =>
        {
            var model = Tk.Model;
            var file = Path.GetFullPath(outputFile ?? Path.Combine(model.GetInfo().ModelPath, "Reports", template + ".xsr"));
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            if (File.Exists(file)) File.Delete(file);
            var ok = selectedOnly
                ? Operation.CreateReportFromSelected(template, file, title1, title2, title3)
                : Operation.CreateReportFromAll(template, file, title1, title2, title3);
            if (!ok)
                throw new ToolException($"O Tekla não gerou o relatório '{template}'. Confira o nome com list_report_templates (sem '.rpt') e se a numeração está em dia (run_numbering).");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!File.Exists(file) && sw.ElapsedMilliseconds < 15000) Thread.Sleep(200);
            string? content = null;
            if (File.Exists(file) && returnChars > 0)
            {
                var text = ReadText(file);
                content = text.Length > returnChars ? text.Substring(0, returnChars) + "\n…(truncado)" : text;
            }
            return new { ok, file, exists = File.Exists(file), content };
        });

    [McpServerTool(Name = "create_nc_files")]
    [Description("Gera arquivos NC (DSTV) com uma configuração NC do Tekla (ex.: 'DSTV for plates', 'DSTV for profiles').")]
    public static Task<CallToolResult> CreateNcFiles(string ncSettings, string destinationFolder, bool selectedOnly = false) =>
        ToolInvoker.Read("create_nc_files", new { ncSettings, destinationFolder, selectedOnly }, () =>
        {
            _ = Tk.Model;
            destinationFolder = Path.GetFullPath(destinationFolder);
            Directory.CreateDirectory(destinationFolder);
            var ok = selectedOnly
                ? Operation.CreateNCFilesFromSelected(ncSettings, destinationFolder)
                : Operation.CreateNCFilesFromAll(ncSettings, destinationFolder);
            return new { ok, destinationFolder, files = Directory.GetFiles(destinationFolder).Length };
        });

    [McpServerTool(Name = "run_macro")]
    [Description("Executa uma macro do Tekla: pelo nome de um arquivo existente na pasta de macros (modeling), ou pelo código C# da macro em 'code' " +
                 "(namespace Tekla.Technology.Akit.UserScript; classe Script com método static Run(IScript akit)). " +
                 "Use para comandos de interface sem API (ex.: akit.Callback(\"acmd_partnumbers_all\", \"\", \"main_frame\")). Para numerar use run_numbering.")]
    public static Task<CallToolResult> RunMacro(
        [Description("Nome do arquivo da macro (ex.: 'MinhaMacro.cs').")] string? fileName = null,
        [Description("Código C# completo da macro (alternativa a fileName).")] string? code = null,
        [Description("Espera a macro terminar (s).")] int waitSeconds = 60) =>
        ToolInvoker.Read("run_macro", new { fileName, hasCode = code is not null }, () => RunMacroCore(fileName, code, waitSeconds));

    [McpServerTool(Name = "run_numbering")]
    [Description("Numera o modelo (posições de peças e montagens). full=false numera só o modificado; full=true refaz a numeração completa. Necessário antes de desenhos e relatórios.")]
    public static Task<CallToolResult> RunNumbering(bool full = false) =>
        ToolInvoker.Read("run_numbering", new { full }, () =>
        {
            var macro = RunMacroCore(null, NumberingMacro.Replace("@ARG@", full ? "full" : ""), 300);
            return new { macro, upToDate = Operation.IsNumberingUpToDateAll() };
        });

    private const string NumberingMacro = """
        namespace Tekla.Technology.Akit.UserScript
        {
            public class Script
            {
                public static void Run(Tekla.Technology.Akit.IScript akit)
                {
                    akit.Callback("acmd_partnumbers_all", "@ARG@", "main_frame");
                }
            }
        }
        """;

    internal static object RunMacroCore(string? fileName, string? code, int waitSeconds)
    {
        _ = Tk.Model;
        string? temp = null;
        if (code is not null)
        {
            temp = Path.Combine(MacroDir(), $"_mcp_{Guid.NewGuid():N}.cs");
            File.WriteAllText(temp, code);
            fileName = Path.GetFileName(temp);
        }
        if (string.IsNullOrWhiteSpace(fileName)) throw new ArgumentException("Informe fileName ou code.");
        try
        {
            if (!Operation.RunMacro(fileName!))
                throw new ToolException($"O Tekla não executou a macro '{fileName}' (arquivo fora da pasta de macros ou outra macro em execução).", new { macroDir = MacroDir() });
            // A macro começa de forma assíncrona: espera ela aparecer como "rodando" antes de esperar o fim.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!Operation.IsMacroRunning() && sw.ElapsedMilliseconds < 1500) Thread.Sleep(50);
            while (Operation.IsMacroRunning() && sw.Elapsed.TotalSeconds < waitSeconds) Thread.Sleep(200);
            return new { ran = fileName, finished = !Operation.IsMacroRunning(), seconds = Math.Round(sw.Elapsed.TotalSeconds, 1) };
        }
        finally
        {
            if (temp is not null && !Operation.IsMacroRunning()) try { File.Delete(temp); } catch { /* fica para a próxima */ }
        }
    }

    [McpServerTool(Name = "list_report_templates", ReadOnly = true)]
    [Description("Lista os templates de relatório (.rpt) disponíveis no ambiente, empresa, projeto e modelo, para usar em create_report.")]
    public static Task<CallToolResult> ListReportTemplates(string? contains = null) =>
        ToolInvoker.Read("list_report_templates", new { contains }, () =>
        {
            var names = ReportTemplateDirs()
                .SelectMany(d => Directory.GetFiles(d, "*.rpt"))
                .Select(f => Path.GetFileNameWithoutExtension(f))
                .Where(n => contains is null || n.IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n)
                .ToList();
            return new { count = names.Count, templates = names };
        });

    private static List<string> ReportTemplateDirs()
    {
        var dirs = new List<string> { Tk.Model.GetInfo().ModelPath };
        foreach (var opt in new[] { "XS_TEMPLATE_DIRECTORY", "XS_TEMPLATE_DIRECTORY_SYSTEM", "XS_PROJECT", "XS_FIRM", "XS_SYSTEM" })
        {
            var raw = "";
            Tekla.Structures.TeklaStructuresSettings.GetAdvancedOption(opt, ref raw);
            dirs.AddRange((raw ?? "").Split(';').Select(s => s.Trim()).Where(s => s.Length > 0));
        }
        return dirs.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    [McpServerTool(Name = "show_only_selected")]
    [Description("Mostra só os objetos selecionados (os demais ficam ocultos ou transparentes), como 'Mostrar apenas selecionados'.")]
    public static Task<CallToolResult> ShowOnlySelected([Description("hidden ou transparent")] string unselected = "hidden") =>
        ToolInvoker.Read("show_only_selected", new { unselected }, () =>
        {
            Operation.ShowOnlySelected(unselected.Equals("transparent", StringComparison.OrdinalIgnoreCase)
                ? Operation.UnselectedModeEnum.Transparent
                : Operation.UnselectedModeEnum.Hidden);
            return new { ok = true };
        });

    /// <summary>Relatórios saem em UTF-8 ou ANSI conforme o template: tenta UTF-8 estrito e cai para 1252.</summary>
    private static string ReadText(string file)
    {
        var bytes = File.ReadAllBytes(file);
        try
        {
            return new System.Text.UTF8Encoding(false, true).GetString(bytes).TrimStart('﻿');
        }
        catch (System.Text.DecoderFallbackException)
        {
            return System.Text.Encoding.GetEncoding(1252).GetString(bytes);
        }
    }

    /// <summary>Primeira pasta de macros de modelagem (XS_MACRO_DIRECTORY\modeling).</summary>
    internal static string MacroDir()
    {
        var raw = "";
        Tekla.Structures.TeklaStructuresSettings.GetAdvancedOption("XS_MACRO_DIRECTORY", ref raw);
        foreach (var d in (raw ?? "").Split(';').Select(s => s.Trim()).Where(s => s.Length > 0))
        {
            var m = Path.Combine(d, "modeling");
            if (Directory.Exists(m)) return m;
        }
        throw new ToolException("Pasta de macros não encontrada (XS_MACRO_DIRECTORY).", new { XS_MACRO_DIRECTORY = raw });
    }
}
