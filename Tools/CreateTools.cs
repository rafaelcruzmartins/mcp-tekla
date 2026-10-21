using System.Collections;
using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model;
using TeklaMcp.Infrastructure;
using TeklaMcp.Server;

namespace TeklaMcp.Tools;

[McpServerToolType]
public static class CreateTools
{
    private const string PosDoc = "Posição: plane MIDDLE|LEFT|RIGHT, rotation FRONT|TOP|BACK|BELOW, depth MIDDLE|FRONT|BEHIND.";

    [McpServerTool(Name = "create_beam")]
    [Description("Cria uma viga (Beam) entre dois pontos. " + PosDoc)]
    public static Task<CallToolResult> CreateBeam(
        [Description("[x,y,z] mm")] double[] start,
        [Description("[x,y,z] mm")] double[] end,
        [Description("Perfil do catálogo, ex.: 'W310X38.7', 'HEA200', 'PL10*200', 'U200'.")] string profile,
        [Description("Material, ex.: 'ASTM A572-50', 'S355JR'. Vazio = padrão do ambiente.")] string? material = null,
        string? name = "BEAM", string? partClass = null, string? finish = null,
        string? partPrefix = null, int? partStartNumber = null, string? assemblyPrefix = null, int? assemblyStartNumber = null,
        string? plane = null, string? rotation = null, string? depth = null,
        double planeOffset = 0, double rotationOffset = 0, double depthOffset = 0,
        [Description("UDAs: {\"NOME\": valor}")] Dictionary<string, JsonElement>? userProperties = null) =>
        ToolInvoker.Write("create_beam", new { start, end, profile }, () =>
        {
            var b = new Beam(Beam.BeamTypeEnum.BEAM) { StartPoint = Tk.Pt(start, "start"), EndPoint = Tk.Pt(end, "end") };
            ApplyPart(b, profile, material, name, partClass, finish, partPrefix, partStartNumber, assemblyPrefix, assemblyStartNumber,
                plane, rotation, depth, planeOffset, rotationOffset, depthOffset, Position.PlaneEnum.MIDDLE, Position.RotationEnum.FRONT, Position.DepthEnum.MIDDLE);
            return Insert(b, userProperties);
        });

    [McpServerTool(Name = "create_column")]
    [Description("Cria um pilar (Beam tipo COLUMN) a partir do ponto da base, subindo 'height' em Z (ou até 'top').")]
    public static Task<CallToolResult> CreateColumn(
        [Description("[x,y,z] da base, mm")] double[] basePoint,
        string profile,
        [Description("Altura em mm (ignorada se 'top' for informado).")] double height = 3000,
        [Description("[x,y,z] do topo (opcional).")] double[]? top = null,
        string? material = null, string? name = "COLUMN", string? partClass = null, string? finish = null,
        string? partPrefix = null, int? partStartNumber = null, string? assemblyPrefix = null, int? assemblyStartNumber = null,
        [Description("Rotação em torno do eixo (graus).")] double rotationOffset = 0,
        string? plane = null, string? rotation = null, string? depth = null,
        Dictionary<string, JsonElement>? userProperties = null) =>
        ToolInvoker.Write("create_column", new { basePoint, profile, height }, () =>
        {
            var p0 = Tk.Pt(basePoint, "basePoint");
            var p1 = top is null ? new Point(p0.X, p0.Y, p0.Z + height) : Tk.Pt(top, "top");
            var c = new Beam(Beam.BeamTypeEnum.COLUMN) { StartPoint = p0, EndPoint = p1 };
            ApplyPart(c, profile, material, name, partClass, finish, partPrefix, partStartNumber, assemblyPrefix, assemblyStartNumber,
                plane, rotation, depth, 0, rotationOffset, 0, Position.PlaneEnum.MIDDLE, Position.RotationEnum.FRONT, Position.DepthEnum.MIDDLE);
            return Insert(c, userProperties);
        });

    [McpServerTool(Name = "create_polybeam")]
    [Description("Cria uma viga poligonal (PolyBeam) passando por 3+ pontos. " + PosDoc)]
    public static Task<CallToolResult> CreatePolyBeam(
        [Description("[[x,y,z], ...] mm")] double[][] points,
        string profile, string? material = null, string? name = "POLYBEAM", string? partClass = null,
        string? plane = null, string? rotation = null, string? depth = null,
        Dictionary<string, JsonElement>? userProperties = null) =>
        ToolInvoker.Write("create_polybeam", new { points, profile }, () =>
        {
            var pb = new PolyBeam();
            foreach (var p in Tk.Pts(points, "points", 2)) pb.AddContourPoint(new ContourPoint(p, null));
            ApplyPart(pb, profile, material, name, partClass, null, null, null, null, null,
                plane, rotation, depth, 0, 0, 0, Position.PlaneEnum.MIDDLE, Position.RotationEnum.FRONT, Position.DepthEnum.MIDDLE);
            return Insert(pb, userProperties);
        });

    [McpServerTool(Name = "create_contour_plate")]
    [Description("Cria uma chapa/laje poligonal (ContourPlate) pelos vértices. Perfil = espessura, ex.: 'PL12' (aço) ou '200' (laje de concreto).")]
    public static Task<CallToolResult> CreateContourPlate(
        [Description("Vértices [[x,y,z], ...] mm, no mínimo 3.")] double[][] points,
        string profile = "PL10", string? material = null, string? name = "PLATE", string? partClass = null, string? finish = null,
        string? partPrefix = null, int? partStartNumber = null, string? assemblyPrefix = null, int? assemblyStartNumber = null,
        [Description("Profundidade: MIDDLE, FRONT (acima do contorno) ou BEHIND.")] string? depth = null,
        double depthOffset = 0,
        Dictionary<string, JsonElement>? userProperties = null) =>
        ToolInvoker.Write("create_contour_plate", new { points, profile }, () =>
        {
            var cp = new ContourPlate();
            foreach (var p in Tk.Pts(points, "points", 3)) cp.AddContourPoint(new ContourPoint(p, null));
            ApplyPart(cp, profile, material, name, partClass, finish, partPrefix, partStartNumber, assemblyPrefix, assemblyStartNumber,
                null, null, depth, 0, 0, depthOffset, Position.PlaneEnum.MIDDLE, Position.RotationEnum.FRONT, Position.DepthEnum.FRONT);
            return Insert(cp, userProperties);
        });

    [McpServerTool(Name = "create_grid")]
    [Description("Cria um grid (eixos). Coordenadas no formato do Tekla, relativas: '0 6000 2*7500' em X/Y e '0 3000 6000' em Z (níveis).")]
    public static Task<CallToolResult> CreateGrid(
        [Description("Ex.: '0 4*6000'")] string coordinateX,
        [Description("Ex.: '0 3*8000'")] string coordinateY,
        [Description("Níveis, ex.: '0 3500 7000'")] string coordinateZ = "0",
        [Description("Rótulos X, ex.: '1 2 3 4 5'")] string labelX = "1 2 3 4 5 6 7 8 9 10 11 12 13 14 15",
        [Description("Rótulos Y, ex.: 'A B C D'")] string labelY = "A B C D E F G H I J K L M",
        [Description("Rótulos Z, ex.: '+0 +3500 +7000'")] string labelZ = "",
        [Description("Origem [x,y,z] do grid.")] double[]? origin = null,
        double extensionLeft = 2000, double extensionRight = 2000) =>
        ToolInvoker.Write("create_grid", new { coordinateX, coordinateY, coordinateZ }, () =>
        {
            var g = new Grid
            {
                CoordinateX = coordinateX, CoordinateY = coordinateY, CoordinateZ = coordinateZ,
                LabelX = labelX, LabelY = labelY, LabelZ = labelZ,
                ExtensionLeftX = extensionLeft, ExtensionLeftY = extensionLeft, ExtensionLeftZ = extensionLeft,
                ExtensionRightX = extensionRight, ExtensionRightY = extensionRight, ExtensionRightZ = extensionRight,
            };
            if (origin is not null) g.Origin = Tk.Pt(origin, "origin");
            if (!g.Insert()) throw new ToolException("O Tekla recusou o grid (confira o formato das coordenadas e rótulos).");
            return Tk.Describe(g);
        });

    [McpServerTool(Name = "create_bolt_group")]
    [Description("Cria um grupo de parafusos (BoltArray) ligando peças. Os pontos definem o eixo do grupo; distX ao longo do eixo e distY transversal.")]
    public static Task<CallToolResult> CreateBoltGroup(
        [Description("Id da peça principal (parafusada a).")] int partToBoltTo,
        [Description("Id da peça secundária (a ser parafusada).")] int partToBeBolted,
        [Description("Primeiro ponto [x,y,z].")] double[] firstPoint,
        [Description("Segundo ponto [x,y,z] (direção do grupo).")] double[] secondPoint,
        [Description("Espaçamentos ao longo do eixo, mm. Ex.: [0, 75, 75] = 3 linhas.")] double[]? distX = null,
        [Description("Espaçamentos transversais, mm. Ex.: [0] = linha única; [100] = duas linhas a 100 mm.")] double[]? distY = null,
        double boltSize = 20, string boltStandard = "7990", double tolerance = 2,
        [Description("Outras peças atravessadas (ids).")] int[]? otherParts = null,
        [Description("Offset da primeira linha a partir do primeiro ponto (Dx), mm.")] double startOffset = 0,
        string? plane = null, string? rotation = null, string? depth = null,
        [Description("true = de montagem (site); false = de fábrica (workshop).")] bool site = true,
        [Description("Conecta montagens diferentes (padrão true).")] bool connectAssemblies = true) =>
        ToolInvoker.Write("create_bolt_group", new { partToBoltTo, partToBeBolted, boltSize, boltStandard }, () =>
        {
            var b = new BoltArray
            {
                PartToBoltTo = Tk.As<Part>(Tk.Get(partToBoltTo), "partToBoltTo"),
                PartToBeBolted = Tk.As<Part>(Tk.Get(partToBeBolted), "partToBeBolted"),
                FirstPosition = Tk.Pt(firstPoint, "firstPoint"),
                SecondPosition = Tk.Pt(secondPoint, "secondPoint"),
                BoltSize = boltSize,
                BoltStandard = boltStandard,
                Tolerance = tolerance,
                BoltType = site ? BoltGroup.BoltTypeEnum.BOLT_TYPE_SITE : BoltGroup.BoltTypeEnum.BOLT_TYPE_WORKSHOP,
                ThreadInMaterial = BoltGroup.BoltThreadInMaterialEnum.THREAD_IN_MATERIAL_YES,
                ConnectAssemblies = connectAssemblies,
                Bolt = true, Washer1 = true, Washer2 = true, Washer3 = true, Nut1 = true, Nut2 = true,
                Hole1 = true, Hole2 = true, Hole3 = true, Hole4 = true, Hole5 = true,
            };
            b.StartPointOffset.Dx = startOffset;
            b.Position.Plane = Tk.ParseEnum(plane, Position.PlaneEnum.MIDDLE);
            b.Position.Rotation = Tk.ParseEnum(rotation, Position.RotationEnum.FRONT);
            b.Position.Depth = Tk.ParseEnum(depth, Position.DepthEnum.MIDDLE);
            foreach (var x in distX is { Length: > 0 } ? distX : [0]) b.AddBoltDistX(x);
            foreach (var y in distY is { Length: > 0 } ? distY : [0]) b.AddBoltDistY(y);
            if (otherParts is not null)
                foreach (var o in Tk.GetMany(otherParts)) b.AddOtherPartToBolt(Tk.As<Part>(o, "otherParts"));
            if (!b.Insert()) throw new ToolException("O Tekla recusou o grupo de parafusos (padrão/diâmetro inexistente no catálogo ou peças sem interseção?).");
            b.Select();
            return Tk.Describe(b, detail: true);
        });

    [McpServerTool(Name = "create_weld")]
    [Description("Cria uma solda entre duas peças. Tipos comuns: WELD_TYPE_FILLET, WELD_TYPE_BEVEL_GROOVE_SINGLE_V_BUTT, WELD_TYPE_BEVEL_GROOVE_SINGLE_BEVEL_BUTT, WELD_TYPE_SQUARE_GROOVE_SQUARE_BUTT.")]
    public static Task<CallToolResult> CreateWeld(
        [Description("Id da peça principal.")] int mainPart,
        [Description("Id da peça secundária.")] int secondaryPart,
        double sizeAbove = 6, string typeAbove = "WELD_TYPE_FILLET",
        double sizeBelow = 0, string? typeBelow = null,
        [Description("true = solda de fábrica.")] bool shopWeld = true,
        bool aroundWeld = false) =>
        ToolInvoker.Write("create_weld", new { mainPart, secondaryPart, sizeAbove, typeAbove }, () =>
        {
            var w = new Weld
            {
                MainObject = Tk.Get(mainPart),
                SecondaryObject = Tk.Get(secondaryPart),
                SizeAbove = sizeAbove,
                TypeAbove = Tk.ParseEnum(typeAbove, BaseWeld.WeldTypeEnum.WELD_TYPE_FILLET),
                SizeBelow = sizeBelow,
                TypeBelow = Tk.ParseEnum(typeBelow, BaseWeld.WeldTypeEnum.WELD_TYPE_NONE),
                ShopWeld = shopWeld,
                AroundWeld = aroundWeld,
            };
            if (!w.Insert()) throw new ToolException("O Tekla recusou a solda (as peças se tocam?).");
            return Tk.Describe(w);
        });

    [McpServerTool(Name = "insert_component")]
    [Description("Insere um componente do Tekla (conexão, detalhe, componente/plugin). " +
                 "kind='connection': primaryId + secondaryIds (ex.: Placa de base 1004, Placa de extremidade 144, Cantoneira dupla 141). " +
                 "kind='detail': primaryId + point. kind='component': points e/ou inputObjectIds. " +
                 "Use 'number' para componentes do sistema ou number=-1 + name para plugins. Veja list_components para descobrir nome/número.")]
    public static Task<CallToolResult> InsertComponent(
        [Description("connection | detail | component")] string kind,
        [Description("Número do componente (ex.: 144). Plugins: -1.")] int number,
        [Description("Nome (obrigatório para plugins/customizados; para sistema pode ficar vazio).")] string? name = null,
        int? primaryId = null, int[]? secondaryIds = null,
        [Description("Ponto de referência (detail) [x,y,z].")] double[]? point = null,
        [Description("Pontos de entrada (component).")] double[][]? points = null,
        [Description("Objetos de entrada (component).")] int[]? inputObjectIds = null,
        [Description("Arquivo de atributos salvo (ex.: 'standard').")] string? attributesFile = "standard",
        [Description("Atributos do diálogo do componente: {\"tpl1\": 20, \"mat\": \"S355JR\"}.")] Dictionary<string, JsonElement>? attributes = null,
        [Description("Classe de conexão para cor/relatório (opcional).")] int? connectionClass = null) =>
        ToolInvoker.Write("insert_component", new { kind, number, name, primaryId, secondaryIds }, () =>
        {
            BaseComponent c;
            switch (kind?.ToLowerInvariant())
            {
                case "connection":
                {
                    var cn = new Connection();
                    if (primaryId is null || secondaryIds is not { Length: > 0 }) throw new ArgumentException("connection exige primaryId e secondaryIds.");
                    cn.SetPrimaryObject(Tk.Get(primaryId));
                    if (secondaryIds.Length == 1) cn.SetSecondaryObject(Tk.Get(secondaryIds[0]));
                    else cn.SetSecondaryObjects(new ArrayList(Tk.GetMany(secondaryIds)));
                    if (connectionClass is not null) cn.Class = connectionClass.Value;
                    c = cn;
                    break;
                }
                case "detail":
                {
                    var dt = new Detail();
                    if (primaryId is null || point is null) throw new ArgumentException("detail exige primaryId e point.");
                    dt.SetPrimaryObject(Tk.Get(primaryId));
                    dt.SetReferencePoint(Tk.Pt(point, "point"));
                    if (connectionClass is not null) dt.Class = connectionClass.Value;
                    c = dt;
                    break;
                }
                case "component":
                {
                    var ci = new ComponentInput();
                    if (inputObjectIds is { Length: > 0 }) foreach (var o in Tk.GetMany(inputObjectIds)) ci.AddInputObject(o);
                    if (points is { Length: > 0 }) foreach (var p in Tk.Pts(points, "points", 1)) ci.AddOneInputPosition(p);
                    if (ci.Count == 0) throw new ArgumentException("component exige points e/ou inputObjectIds.");
                    c = new Tekla.Structures.Model.Component(ci);
                    break;
                }
                default:
                    throw new ArgumentException("kind deve ser connection, detail ou component.");
            }
            c.Number = number;
            if (!string.IsNullOrWhiteSpace(name)) c.Name = name;
            if (!string.IsNullOrWhiteSpace(attributesFile)) c.LoadAttributesFromFile(attributesFile);
            ApplyAttributes(c, attributes);
            if (!c.Insert()) throw new ToolException("O Tekla recusou o componente (número/nome incorreto ou entradas inválidas).");
            c.Select();
            var d = Tk.Describe(c);
            d["createdObjects"] = Tk.Enumerate(c.GetChildren()).Select(o => new { id = o.Identifier.ID, type = o.GetType().Name }).Take(200).ToList();
            return d;
        });

    // ---------- helpers ----------

    internal static void ApplyAttributes(BaseComponent c, Dictionary<string, JsonElement>? attributes)
    {
        if (attributes is null) return;
        foreach (var kv in attributes)
        {
            var v = kv.Value;
            if (v.ValueKind == JsonValueKind.Number)
            {
                if (v.TryGetInt32(out var i)) c.SetAttribute(kv.Key, i);
                else c.SetAttribute(kv.Key, v.GetDouble());
            }
            else c.SetAttribute(kv.Key, v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString());
        }
    }

    internal static void ApplyPart(Part p, string? profile, string? material, string? name, string? partClass, string? finish,
        string? partPrefix, int? partStart, string? asmPrefix, int? asmStart,
        string? plane, string? rotation, string? depth, double planeOffset, double rotationOffset, double depthOffset,
        Position.PlaneEnum dPlane, Position.RotationEnum dRot, Position.DepthEnum dDepth)
    {
        if (!string.IsNullOrWhiteSpace(profile)) p.Profile.ProfileString = profile;
        if (!string.IsNullOrWhiteSpace(material)) p.Material.MaterialString = material;
        if (name is not null) p.Name = name;
        if (partClass is not null) p.Class = partClass;
        if (finish is not null) p.Finish = finish;
        if (partPrefix is not null) p.PartNumber.Prefix = partPrefix;
        if (partStart is not null) p.PartNumber.StartNumber = partStart.Value;
        if (asmPrefix is not null) p.AssemblyNumber.Prefix = asmPrefix;
        if (asmStart is not null) p.AssemblyNumber.StartNumber = asmStart.Value;
        p.Position.Plane = Tk.ParseEnum(plane, dPlane);
        p.Position.Rotation = Tk.ParseEnum(rotation, dRot);
        p.Position.Depth = Tk.ParseEnum(depth, dDepth);
        p.Position.PlaneOffset = planeOffset;
        p.Position.RotationOffset = rotationOffset;
        p.Position.DepthOffset = depthOffset;
    }

    private static object Insert(Part p, Dictionary<string, JsonElement>? udas)
    {
        if (!p.Insert())
            throw new ToolException($"O Tekla recusou a peça. Confira o perfil '{p.Profile.ProfileString}' (list_profiles) e o material '{p.Material.MaterialString}' (list_materials).");
        var failed = Tk.SetUdas(p, udas);
        if (udas is { Count: > 0 }) p.Modify();
        p.Select();
        var d = Tk.Describe(p);
        if (failed.Count > 0) d["failedUserProperties"] = failed;
        var w = 0.0;
        if (p.GetReportProperty("WEIGHT", ref w)) d["weightKg"] = Fmt.R(w, 2);
        if (string.IsNullOrWhiteSpace(p.Material.MaterialString))
            d["warning"] = "Peça sem material (peso 0). Informe 'material' (veja list_materials) ou ajuste com modify_part.";
        return d;
    }
}
