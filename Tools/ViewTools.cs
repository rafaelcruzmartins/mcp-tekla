using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model;
using TeklaMcp.Infrastructure;
using TeklaMcp.Server;
using UI = Tekla.Structures.Model.UI;

namespace TeklaMcp.Tools;

[McpServerToolType]
public static class ViewTools
{
    [McpServerTool(Name = "list_views", ReadOnly = true)]
    [Description("Lista as vistas do modelo (abertas e salvas): id, nome, visível, representação, área de trabalho.")]
    public static Task<CallToolResult> ListViews([Description("Só as vistas abertas na tela.")] bool visibleOnly = false) =>
        ToolInvoker.Read("list_views", null, () =>
        {
            var e = visibleOnly ? UI.ViewHandler.GetVisibleViews() : UI.ViewHandler.GetAllViews();
            var active = Tk.Safe(() => UI.ViewHandler.GetActiveView()?.Identifier.ID);
            var list = new List<object>();
            while (e.MoveNext())
            {
                var v = e.Current;
                list.Add(new
                {
                    id = v.Identifier.ID, name = v.Name, visible = v.IsVisible(), active = v.Identifier.ID == active,
                    representation = v.CurrentRepresentation, filter = v.ViewFilter, projection = v.ViewProjection.ToString(),
                    workArea = v.WorkArea is null ? null : new { min = Tk.P(v.WorkArea.MinPoint), max = Tk.P(v.WorkArea.MaxPoint) },
                });
            }
            return list;
        });

    [McpServerTool(Name = "color_objects")]
    [Description("Pinta objetos temporariamente na vista (como 'color splash'), com cor RGB 0..1 e transparência opcional. Some ao redesenhar/clear_colors.")]
    public static Task<CallToolResult> ColorObjects(int[] ids,
        [Description("[r,g,b] de 0 a 1, ex.: [1,0,0] vermelho.")] double[] rgb,
        [Description("0 = opaco, 1 = invisível (opcional).")] double? transparency = null) =>
        ToolInvoker.Read("color_objects", new { ids, rgb }, () =>
        {
            if (rgb is not { Length: 3 }) throw new ArgumentException("rgb deve ter 3 valores entre 0 e 1.");
            var color = transparency is null ? new UI.Color(rgb[0], rgb[1], rgb[2]) : new UI.Color(rgb[0], rgb[1], rgb[2], transparency.Value);
            var ok = UI.ModelObjectVisualization.SetTemporaryState(Tk.GetMany(ids), color);
            return new { colored = ids.Length, ok };
        });

    [McpServerTool(Name = "set_visibility")]
    [Description("Transparência temporária: HIDDEN, TRANSPARENT, SEMITRANSPARENT, SEMIVISIBLE, VISIBLE. Sem ids aplica a todos (útil: tudo TRANSPARENT e depois alguns VISIBLE).")]
    public static Task<CallToolResult> SetVisibility(string mode, int[]? ids = null) =>
        ToolInvoker.Read("set_visibility", new { mode, ids }, () =>
        {
            var t = Tk.ParseEnum(mode, UI.TemporaryTransparency.VISIBLE);
            var ok = ids is { Length: > 0 }
                ? UI.ModelObjectVisualization.SetTransparency(Tk.GetMany(ids), t)
                : UI.ModelObjectVisualization.SetTransparencyForAll(t);
            return new { ok, mode = t.ToString() };
        });

    [McpServerTool(Name = "clear_colors")]
    [Description("Remove cores e transparências temporárias de todos os objetos.")]
    public static Task<CallToolResult> ClearColors() =>
        ToolInvoker.Read("clear_colors", null, () => new { ok = UI.ModelObjectVisualization.ClearAllTemporaryStates() });

    [McpServerTool(Name = "zoom_to_objects")]
    [Description("Dá zoom na vista ativa para enquadrar os objetos informados (ou a caixa min/max).")]
    public static Task<CallToolResult> ZoomToObjects(int[]? ids = null, double[]? min = null, double[]? max = null, double margin = 500) =>
        ToolInvoker.Read("zoom_to_objects", new { ids, min, max }, () =>
        {
            AABB box;
            if (min is not null && max is not null) box = new AABB(Tk.Pt(min, "min"), Tk.Pt(max, "max"));
            else
            {
                var pts = new List<Point>();
                foreach (var o in Tk.GetMany(ids))
                {
                    var s = o switch
                    {
                        Part p => Tk.Safe(() => p.GetSolid()),
                        _ => null,
                    };
                    if (s is not null) { pts.Add(s.MinimumPoint); pts.Add(s.MaximumPoint); }
                    else if (o is BoltGroup bg) { pts.Add(bg.FirstPosition); pts.Add(bg.SecondPosition); }
                }
                if (pts.Count == 0) throw new ToolException("Não foi possível calcular a caixa dos objetos.");
                box = new AABB(pts);
            }
            box = new AABB(new Point(box.MinPoint.X - margin, box.MinPoint.Y - margin, box.MinPoint.Z - margin),
                           new Point(box.MaxPoint.X + margin, box.MaxPoint.Y + margin, box.MaxPoint.Z + margin));
            return new { ok = UI.ViewHandler.ZoomToBoundingBox(box), min = Tk.P(box.MinPoint), max = Tk.P(box.MaxPoint) };
        });

    [McpServerTool(Name = "redraw_views")]
    [Description("Redesenha todas as vistas visíveis (após alterações por macro/código).")]
    public static Task<CallToolResult> RedrawViews() =>
        ToolInvoker.Read("redraw_views", null, () =>
        {
            var e = UI.ViewHandler.GetVisibleViews();
            var n = 0;
            while (e.MoveNext()) if (UI.ViewHandler.RedrawView(e.Current)) n++;
            return new { redrawn = n };
        });

    [McpServerTool(Name = "set_representation")]
    [Description("Aplica uma configuração de representação de objetos (cores/transparência por grupo), ex.: 'standard', 'Phase', 'Class'.")]
    public static Task<CallToolResult> SetRepresentation(string name) =>
        ToolInvoker.Read("set_representation", new { name }, () => new { ok = UI.ViewHandler.SetRepresentation(name) });

    [McpServerTool(Name = "capture_view", ReadOnly = true)]
    [Description("Captura uma imagem PNG da vista ativa (ou da vista 'viewId') e devolve a imagem para análise visual.")]
    public static async Task<CallToolResult> CaptureView(int? viewId = null, int width = 1600, int height = 1000, bool whiteBackground = true)
    {
        var file = Path.Combine(Path.GetTempPath(), $"teklamcp_view_{Guid.NewGuid():N}.png");
        var res = await ToolInvoker.Read("capture_view", new { viewId, width, height }, () =>
        {
            UI.View? view = null;
            if (viewId is > 0)
            {
                var e = UI.ViewHandler.GetAllViews();
                while (e.MoveNext()) if (e.Current.Identifier.ID == viewId) view = e.Current;
            }
            view ??= UI.ViewHandler.GetActiveView() ?? throw new ToolException("Nenhuma vista ativa no Tekla.");
            var s = new UI.SnapshotSettings { DPI = 96, Width = width, Height = height, UseSmoothLines = 1, LineWidth = 1, WhiteBG = whiteBackground ? 1 : 0 };
            if (!view.CreateSnapshot(file, s, true) || !File.Exists(file))
                throw new ToolException("O Tekla não gerou a imagem (a vista está aberta na tela?).");
            return new { view = view.Name, file };
        });
        if (res.IsError == true || !File.Exists(file)) return res;
        var bytes = File.ReadAllBytes(file);
        try { File.Delete(file); } catch { /* temporário */ }
        res.Content.Add(ImageContentBlock.FromBytes(bytes, "image/png"));
        return res;
    }
}
