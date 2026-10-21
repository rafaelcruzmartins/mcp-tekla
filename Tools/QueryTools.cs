using System.Collections;
using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Tekla.Structures;
using Tekla.Structures.Model;
using TeklaMcp.Infrastructure;
using TeklaMcp.Server;
using UI = Tekla.Structures.Model.UI;

namespace TeklaMcp.Tools;

[McpServerToolType]
public static class QueryTools
{
    [McpServerTool(Name = "list_objects", ReadOnly = true)]
    [Description("Lista objetos do modelo com filtros. Retorna id, tipo, nome, perfil, material, classe e geometria resumida. " +
                 "Para contar/somar por perfil use material_takeoff.")]
    public static Task<CallToolResult> ListObjects(
        [Description("Tipos da Open API: Beam, PolyBeam, ContourPlate, Assembly, BoltArray, BoltGroup, Weld, Connection, Component, Detail, Grid, SingleRebar, RebarGroup, Fitting, CutPlane, BooleanPart... Vazio = todas as peças (Part).")] string[]? types = null,
        [Description("Nome de um filtro de seleção salvo no Tekla (ex.: 'standard', 'beam'). Opcional.")] string? filterName = null,
        [Description("Trecho do nome (case-insensitive).")] string? nameContains = null,
        [Description("Trecho do perfil (ex.: 'W310', 'PL').")] string? profileContains = null,
        [Description("Trecho do material (ex.: 'A572').")] string? materialContains = null,
        [Description("Classe exata (ex.: '3').")] string? className = null,
        [Description("Propriedades de relatório extras por objeto (ex.: ['WEIGHT','LENGTH','ASSEMBLY_POS']). Deixa a consulta mais lenta.")] string[]? reportProperties = null,
        int offset = 0,
        int limit = 200) =>
        ToolInvoker.Read("list_objects", new { types, filterName, nameContains, profileContains }, () =>
        {
            var sel = Tk.Model.GetModelObjectSelector();
            var typeList = types is { Length: > 0 } ? types.Select(Tk.ModelType).ToArray() : Tk.PartTypes;
            ModelObjectEnumerator e = !string.IsNullOrWhiteSpace(filterName)
                ? sel.GetObjectsByFilterName(filterName)
                : sel.GetAllObjectsWithType(typeList);
            e.SelectInstances = false;

            var items = Tk.Enumerate(e)
                .Where(o => typeList.Any(t => t.IsInstanceOfType(o)))
                .Where(o => Matches(o, nameContains, profileContains, materialContains, className));

            var total = 0;
            var page = new List<Dictionary<string, object?>>();
            foreach (var o in items)
            {
                if (total >= offset && page.Count < limit)
                {
                    o.Select();
                    var d = Tk.Describe(o);
                    if (reportProperties is { Length: > 0 }) d["report"] = Tk.Report(o, reportProperties);
                    page.Add(d);
                }
                total++;
            }
            return new { total, offset, returned = page.Count, objects = page };
        });

    private static bool Matches(ModelObject o, string? name, string? profile, string? material, string? cls)
    {
        if (name is null && profile is null && material is null && cls is null) return true;
        o.Select();
        string? n = null, pr = null, ma = null, cl = null;
        switch (o)
        {
            case Part p: n = p.Name; pr = p.Profile?.ProfileString; ma = p.Material?.MaterialString; cl = p.Class; break;
            case Assembly a: n = a.Name; break;
            case BaseComponent c: n = c.Name; break;
        }
        return Has(n, name) && Has(pr, profile) && Has(ma, material) && (cls is null || cl == cls);
    }

    private static bool Has(string? value, string? part) =>
        part is null || (value ?? "").IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;

    [McpServerTool(Name = "get_object_properties", ReadOnly = true)]
    [Description("Propriedades completas de um objeto: dados do tipo, posição, numeração, UDAs, propriedades de relatório e objetos filhos (parafusos, soldas, componentes).")]
    public static Task<CallToolResult> GetObjectProperties(
        int? id = null, string? guid = null,
        [Description("Propriedades de relatório a ler. Padrão: PART_POS, ASSEMBLY_POS, PROFILE_TYPE, LENGTH, WEIGHT, WEIGHT_NET, AREA, VOLUME, PHASE.")] string[]? reportProperties = null) =>
        ToolInvoker.Read("get_object_properties", new { id, guid }, () =>
        {
            var o = Tk.Get(id, guid);
            var d = Tk.Describe(o, detail: true);
            d["report"] = Tk.Report(o, reportProperties is { Length: > 0 } ? reportProperties : Tk.DefaultReportProps);
            d["userProperties"] = Tk.Udas(o);
            d["modified"] = o.ModificationTime;
            if (o is Part p)
            {
                d["bolts"] = Tk.Enumerate(p.GetBolts()).Select(x => x.Identifier.ID).ToList();
                d["welds"] = Tk.Enumerate(p.GetWelds()).Select(x => x.Identifier.ID).ToList();
                d["components"] = Tk.Enumerate(p.GetComponents()).Select(x => new { id = x.Identifier.ID, type = x.GetType().Name, name = (x as BaseComponent)?.Name }).ToList();
                d["booleans"] = Tk.Enumerate(p.GetBooleans()).Select(x => new { id = x.Identifier.ID, type = x.GetType().Name }).ToList();
                d["reinforcements"] = Tk.Enumerate(p.GetReinforcements()).Select(x => x.Identifier.ID).ToList();
                var solid = Tk.Safe(() => p.GetSolid());
                if (solid is not null) d["boundingBox"] = new { min = Tk.P(solid.MinimumPoint), max = Tk.P(solid.MaximumPoint) };
            }
            var father = Tk.Safe(() => o.GetFatherComponent());
            if (father is not null && father.Identifier.ID != 0) d["fatherComponent"] = new { id = father.Identifier.ID, name = father.Name, number = father.Number };
            return d;
        });

    [McpServerTool(Name = "get_report_properties", ReadOnly = true)]
    [Description("Lê propriedades de relatório (template attributes) de vários objetos de uma vez. Ex.: WEIGHT, LENGTH, ASSEMBLY_POS, PART_POS, PROFILE, MATERIAL, CLASS, AREA, VOLUME, COG_X, ASSEMBLY.MAINPART.PROFILE, USERDEFINED.<UDA>.")]
    public static Task<CallToolResult> GetReportProperties(int[] ids, string[] properties) =>
        ToolInvoker.Read("get_report_properties", new { ids, properties }, () =>
        {
            if (properties is null || properties.Length == 0) throw new ArgumentException("Informe 'properties'.");
            return Tk.GetMany(ids).Select(o => new { id = o.Identifier.ID, type = o.GetType().Name, values = Tk.Report(o, properties) }).ToList();
        });

    [McpServerTool(Name = "get_selected_objects", ReadOnly = true)]
    [Description("Objetos atualmente selecionados pelo usuário no Tekla.")]
    public static Task<CallToolResult> GetSelectedObjects(
        [Description("Inclui propriedades de relatório padrão (peso, comprimento, posição).")] bool includeReport = false,
        int limit = 500) =>
        ToolInvoker.Read("get_selected_objects", null, () =>
        {
            var e = new UI.ModelObjectSelector().GetSelectedObjects();
            var all = Tk.Enumerate(e).ToList();
            return new
            {
                count = all.Count,
                objects = all.Take(limit).Select(o =>
                {
                    var d = Tk.Describe(o);
                    if (includeReport) d["report"] = Tk.Report(o, Tk.DefaultReportProps);
                    return d;
                }).ToList(),
            };
        });

    [McpServerTool(Name = "select_objects")]
    [Description("Seleciona objetos na interface do Tekla (destaca para o usuário). Lista vazia limpa a seleção.")]
    public static Task<CallToolResult> SelectObjects(int[]? ids = null) =>
        ToolInvoker.Read("select_objects", new { ids }, () =>
        {
            var list = ids is { Length: > 0 } ? Tk.GetMany(ids) : [];
            var ok = new UI.ModelObjectSelector().Select(new ArrayList(list));
            return new { selected = list.Count, ok };
        });

    [McpServerTool(Name = "get_objects_in_box", ReadOnly = true)]
    [Description("Objetos cujo volume intercepta uma caixa alinhada aos eixos (mm).")]
    public static Task<CallToolResult> GetObjectsInBox(double[] min, double[] max, string[]? types = null, int limit = 500) =>
        ToolInvoker.Read("get_objects_in_box", new { min, max, types }, () =>
        {
            var e = Tk.Model.GetModelObjectSelector().GetObjectsByBoundingBox(Tk.Pt(min, "min"), Tk.Pt(max, "max"));
            var tl = types is { Length: > 0 } ? types.Select(Tk.ModelType).ToArray() : null;
            var all = Tk.Enumerate(e).Where(o => tl is null || tl.Any(t => t.IsInstanceOfType(o))).ToList();
            return new { count = all.Count, objects = all.Take(limit).Select(o => Tk.Describe(o)).ToList() };
        });

    [McpServerTool(Name = "material_takeoff", ReadOnly = true)]
    [Description("Levantamento de material das peças: agrupa por perfil+material (ou outro critério) com quantidade, comprimento total (m) e peso total (kg).")]
    public static Task<CallToolResult> MaterialTakeoff(
        [Description("Agrupar por: 'profile_material' (padrão), 'profile', 'material', 'class', 'name' ou 'assembly'.")] string groupBy = "profile_material",
        [Description("Filtro de seleção salvo no Tekla (opcional).")] string? filterName = null,
        [Description("Só objetos selecionados no Tekla.")] bool selectedOnly = false) =>
        ToolInvoker.Read("material_takeoff", new { groupBy, filterName, selectedOnly }, () =>
        {
            IEnumerable<ModelObject> src = selectedOnly
                ? Tk.Enumerate(new UI.ModelObjectSelector().GetSelectedObjects())
                : Tk.Enumerate(string.IsNullOrWhiteSpace(filterName)
                    ? Tk.Model.GetModelObjectSelector().GetAllObjectsWithType(Tk.PartTypes)
                    : Tk.Model.GetModelObjectSelector().GetObjectsByFilterName(filterName));
            var parts = src.OfType<Part>().ToList();
            if (parts.Count == 0) return new { parts = 0, groups = new List<object>() };

            var ids = parts.Select(p => p.Identifier).ToList();
            var weight = Tk.Model.GetReportPropertyDouble(ids, "WEIGHT");
            var length = Tk.Model.GetReportPropertyDouble(ids, "LENGTH");
            var asm = groupBy == "assembly" ? Tk.Model.GetReportPropertyStr(ids, "ASSEMBLY_POS") : null;

            var rows = parts.Select((p, i) =>
            {
                p.Select();
                var key = groupBy switch
                {
                    "profile" => p.Profile.ProfileString,
                    "material" => p.Material.MaterialString,
                    "class" => p.Class,
                    "name" => p.Name,
                    "assembly" => asm![i],
                    _ => p.Profile.ProfileString + " | " + p.Material.MaterialString,
                };
                return (key, w: weight[i], l: length[i]);
            });
            var groups = rows.GroupBy(r => r.key)
                .Select(g => new { key = g.Key, count = g.Count(), lengthM = Fmt.R(g.Sum(r => r.l) / 1000.0, 3), weightKg = Fmt.R(g.Sum(r => r.w), 2) })
                .OrderByDescending(g => g.weightKg)
                .ToList();
            return new
            {
                parts = parts.Count,
                totalWeightKg = Fmt.R(groups.Sum(g => g.weightKg), 2),
                totalLengthM = Fmt.R(groups.Sum(g => g.lengthM), 3),
                groups,
            };
        });

    [McpServerTool(Name = "check_numbering", ReadOnly = true)]
    [Description("Informa se a numeração está atualizada (no modelo todo ou para os ids informados) e lista objetos com a mesma numeração de um id.")]
    public static Task<CallToolResult> CheckNumbering(int[]? ids = null, [Description("Id para listar peças similares (mesma posição).")] int? similarTo = null) =>
        ToolInvoker.Read("check_numbering", new { ids, similarTo }, () =>
        {
            return new
            {
                allUpToDate = Tekla.Structures.Model.Operations.Operation.IsNumberingUpToDateAll(),
                perObject = ids is { Length: > 0 }
                    ? Tk.GetMany(ids).Select(o => new { id = o.Identifier.ID, upToDate = Tekla.Structures.Model.Operations.Operation.IsNumberingUpToDate(o) }).ToList()
                    : null,
                similar = similarTo is > 0
                    ? Tekla.Structures.Model.Operations.Operation.GetSimilarNumberedObjects(Tk.Get(similarTo)).Select(o => o.Identifier.ID).ToList()
                    : null,
            };
        });
}
