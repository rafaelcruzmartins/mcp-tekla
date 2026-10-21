using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Tekla.Structures;
using Tekla.Structures.Drawing;
using Tekla.Structures.Model;
using TeklaMcp.Infrastructure;
using TeklaMcp.Server;

namespace TeklaMcp.Tools;

[McpServerToolType]
public static class DrawingTools
{
    private static DrawingHandler Handler()
    {
        _ = Tk.Model;
        var h = new DrawingHandler();
        if (!h.GetConnectionStatus()) throw new ToolException("Editor de desenhos indisponível.");
        return h;
    }

    private static object Describe(Drawing d) => new
    {
        type = d.GetType().Name,
        name = d.Name,
        mark = d.Mark,
        title1 = d.Title1, title2 = d.Title2, title3 = d.Title3,
        upToDate = d.UpToDateStatus.ToString(),
        issued = d.IsIssued, locked = d.IsLocked, frozen = d.IsFrozen, readyForIssue = d.IsReadyForIssue,
        created = d.CreationDate, modified = d.ModificationDate,
        sheet = Tk.Safe(() => new { width = d.Layout.SheetSize.Width, height = d.Layout.SheetSize.Height }),
    };

    private static List<Drawing> All(DrawingHandler h)
    {
        var e = h.GetDrawings();
        var l = new List<Drawing>();
        while (e.MoveNext()) l.Add(e.Current);
        return l;
    }

    private static Drawing Find(DrawingHandler h, string mark)
    {
        var all = All(h);
        return all.FirstOrDefault(d => string.Equals(d.Mark.Trim('[', ']', ' '), mark.Trim('[', ']', ' '), StringComparison.OrdinalIgnoreCase))
               ?? throw new ToolException($"Desenho '{mark}' não encontrado.", new { available = all.Take(50).Select(d => d.Mark).ToList() });
    }

    [McpServerTool(Name = "list_drawings", ReadOnly = true)]
    [Description("Lista os desenhos do modelo (Lista de Documentos): tipo, marca, nome, títulos, status de atualização e emissão.")]
    public static Task<CallToolResult> ListDrawings(
        [Description("Filtra por tipo: AssemblyDrawing, SinglePartDrawing, GADrawing, CastUnitDrawing, MultiDrawing.")] string? type = null,
        string? markContains = null, int limit = 300) =>
        ToolInvoker.Read("list_drawings", new { type, markContains }, () =>
        {
            var all = All(Handler())
                .Where(d => type is null || d.GetType().Name.Equals(type, StringComparison.OrdinalIgnoreCase))
                .Where(d => markContains is null || d.Mark.IndexOf(markContains, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();
            return new { total = all.Count, drawings = all.Take(limit).Select(Describe).ToList() };
        });

    [McpServerTool(Name = "create_drawing")]
    [Description("Cria um desenho: kind='assembly' (id de uma peça ou montagem), 'single_part' (id da peça), 'cast_unit' (id da peça/montagem) ou 'ga' (geral, usa o arquivo de atributos com as vistas). A numeração precisa estar atualizada para assembly/single_part.")]
    public static Task<CallToolResult> CreateDrawing(string kind, int? objectId = null,
        [Description("Arquivo de propriedades do desenho (ex.: 'standard').")] string attributesFile = "standard",
        [Description("Nome do desenho GA.")] string? name = null) =>
        ToolInvoker.Read("create_drawing", new { kind, objectId, attributesFile }, () =>
        {
            var h = Handler();
            Identifier Id(bool assembly)
            {
                if (objectId is null) throw new ArgumentException($"{kind} exige objectId.");
                var o = Tk.Get(objectId);
                if (assembly && o is Tekla.Structures.Model.Part p) return p.GetAssembly().Identifier;
                return o.Identifier;
            }
            Drawing d = kind?.ToLowerInvariant() switch
            {
                "assembly" => new AssemblyDrawing(Id(true), attributesFile),
                "single_part" => new SinglePartDrawing(Id(false), attributesFile),
                "cast_unit" => new CastUnitDrawing(Id(true), attributesFile),
                "ga" => new GADrawing(name ?? "GA", attributesFile),
                _ => throw new ArgumentException("kind deve ser assembly, single_part, cast_unit ou ga."),
            };
            if (!d.Insert()) throw new ToolException("O Tekla não criou o desenho (numeração desatualizada ou arquivo de atributos inexistente?).");
            return Describe(d);
        });

    [McpServerTool(Name = "open_drawing")]
    [Description("Abre um desenho no editor de desenhos pela marca (ex.: 'A1', '[A.1]', 'G1').")]
    public static Task<CallToolResult> OpenDrawing(string mark) =>
        ToolInvoker.Read("open_drawing", new { mark }, () =>
        {
            var h = Handler();
            var d = Find(h, mark);
            if (!h.SetActiveDrawing(d, true)) throw new ToolException("O Tekla não abriu o desenho.");
            return Describe(d);
        });

    [McpServerTool(Name = "close_drawing")]
    [Description("Fecha o desenho aberto, salvando ou não.")]
    public static Task<CallToolResult> CloseDrawing(bool save = true) =>
        ToolInvoker.Read("close_drawing", new { save }, () =>
        {
            var h = Handler();
            if (!h.IsAnyDrawingOpen()) return new { closed = false, reason = "nenhum desenho aberto" };
            return new { closed = h.CloseActiveDrawing(save) };
        });

    [McpServerTool(Name = "update_drawings")]
    [Description("Atualiza desenhos desatualizados (pela marca, ou todos os que não estão em dia).")]
    public static Task<CallToolResult> UpdateDrawings(string[]? marks = null) =>
        ToolInvoker.Read("update_drawings", new { marks }, () =>
        {
            var h = Handler();
            var targets = marks is { Length: > 0 }
                ? marks.Select(m => Find(h, m)).ToList()
                : All(h).Where(d => d.UpToDateStatus != DrawingUpToDateStatus.DrawingIsUpToDate).ToList();
            var res = targets.Select(d => new { mark = d.Mark, updated = h.UpdateDrawing(d) }).ToList();
            return new { count = res.Count, results = res };
        });

    [McpServerTool(Name = "export_drawings_pdf")]
    [Description("Imprime desenhos em PDF numa pasta (pela marca; vazio = todos).")]
    public static Task<CallToolResult> ExportDrawingsPdf(string outputFolder, string[]? marks = null) =>
        ToolInvoker.Read("export_drawings_pdf", new { outputFolder, marks }, () =>
        {
            var h = Handler();
            outputFolder = Path.GetFullPath(outputFolder);
            Directory.CreateDirectory(outputFolder);
            var targets = marks is { Length: > 0 } ? marks.Select(m => Find(h, m)).ToList() : All(h);
            var res = new List<object>();
            foreach (var d in targets)
            {
                var file = Path.Combine(outputFolder, Sanitize(d.Mark) + ".pdf");
                try
                {
                    if (d.UpToDateStatus != DrawingUpToDateStatus.DrawingIsUpToDate) h.UpdateDrawing(d);
                    var attrs = new DPMPrinterAttributes { OutputType = DotPrintOutputType.PDF, OutputFileName = file, OpenFileWhenFinished = false };
                    var ok = h.PrintDrawing(d, attrs, file);
                    // A impressão grava o arquivo de forma assíncrona.
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (ok && !File.Exists(file) && sw.ElapsedMilliseconds < 15000) Thread.Sleep(250);
                    res.Add(new { mark = d.Mark, file, ok, exists = File.Exists(file) });
                }
                catch (Exception ex)
                {
                    res.Add(new { mark = d.Mark, file, ok = false, exists = false, error = ex.Message });
                }
            }
            var created = Directory.GetFiles(outputFolder, "*.pdf").Length;
            return new
            {
                count = res.Count,
                results = res,
                hint = created == 0
                    ? "O Tekla aceitou a impressão mas nenhum PDF foi gravado. Isso acontece com a licença Educacional (verificado no SP2.1); com licença comercial o arquivo sai na pasta indicada."
                    : null,
            };
        });

    [McpServerTool(Name = "delete_drawings", Destructive = true)]
    [Description("Apaga desenhos pela marca.")]
    public static Task<CallToolResult> DeleteDrawings(string[] marks) =>
        ToolInvoker.Read("delete_drawings", new { marks }, () =>
        {
            var h = Handler();
            return marks.Select(m => { var d = Find(h, m); return new { mark = d.Mark, deleted = d.Delete() }; }).ToList();
        });

    [McpServerTool(Name = "get_drawing_objects", ReadOnly = true)]
    [Description("Ids dos objetos do modelo representados num desenho.")]
    public static Task<CallToolResult> GetDrawingObjects(string mark) =>
        ToolInvoker.Read("get_drawing_objects", new { mark }, () =>
        {
            var h = Handler();
            var ids = h.GetModelObjectIdentifiers(Find(h, mark)).Select(i => i.ID).ToList();
            return new { mark, count = ids.Count, ids };
        });

    private static string Sanitize(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Trim('[', ']', ' ').Replace(' ', '_');
    }
}
