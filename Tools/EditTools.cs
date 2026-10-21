using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model;
using Tekla.Structures.Model.Operations;
using TeklaMcp.Infrastructure;
using TeklaMcp.Server;

namespace TeklaMcp.Tools;

[McpServerToolType]
public static class EditTools
{
    [McpServerTool(Name = "modify_part")]
    [Description("Altera uma peça: perfil, material, nome, classe, acabamento, numeração, pontos, posição e UDAs. Só o que for informado muda.")]
    public static Task<CallToolResult> ModifyPart(
        int id,
        string? profile = null, string? material = null, string? name = null, string? partClass = null, string? finish = null,
        string? partPrefix = null, int? partStartNumber = null, string? assemblyPrefix = null, int? assemblyStartNumber = null,
        [Description("Novo ponto inicial (Beam) [x,y,z].")] double[]? start = null,
        [Description("Novo ponto final (Beam) [x,y,z].")] double[]? end = null,
        [Description("Novo contorno (ContourPlate/PolyBeam).")] double[][]? points = null,
        string? plane = null, string? rotation = null, string? depth = null,
        double? planeOffset = null, double? rotationOffset = null, double? depthOffset = null,
        Dictionary<string, JsonElement>? userProperties = null) =>
        ToolInvoker.Write("modify_part", new { id, profile, material, name }, () =>
        {
            var p = Tk.As<Part>(Tk.Get(id), "id");
            if (profile is not null) p.Profile.ProfileString = profile;
            if (material is not null) p.Material.MaterialString = material;
            if (name is not null) p.Name = name;
            if (partClass is not null) p.Class = partClass;
            if (finish is not null) p.Finish = finish;
            if (partPrefix is not null) p.PartNumber.Prefix = partPrefix;
            if (partStartNumber is not null) p.PartNumber.StartNumber = partStartNumber.Value;
            if (assemblyPrefix is not null) p.AssemblyNumber.Prefix = assemblyPrefix;
            if (assemblyStartNumber is not null) p.AssemblyNumber.StartNumber = assemblyStartNumber.Value;
            if (plane is not null) p.Position.Plane = Tk.ParseEnum(plane, p.Position.Plane);
            if (rotation is not null) p.Position.Rotation = Tk.ParseEnum(rotation, p.Position.Rotation);
            if (depth is not null) p.Position.Depth = Tk.ParseEnum(depth, p.Position.Depth);
            if (planeOffset is not null) p.Position.PlaneOffset = planeOffset.Value;
            if (rotationOffset is not null) p.Position.RotationOffset = rotationOffset.Value;
            if (depthOffset is not null) p.Position.DepthOffset = depthOffset.Value;
            if (p is Beam b)
            {
                if (start is not null) b.StartPoint = Tk.Pt(start, "start");
                if (end is not null) b.EndPoint = Tk.Pt(end, "end");
            }
            if (points is not null)
            {
                var c = new Contour();
                foreach (var pt in Tk.Pts(points, "points", 2)) c.AddContourPoint(new ContourPoint(pt, null));
                switch (p)
                {
                    case ContourPlate cp: cp.Contour = c; break;
                    case PolyBeam pb: pb.Contour = c; break;
                    default: throw new ArgumentException("'points' só vale para ContourPlate e PolyBeam.");
                }
            }
            var failed = Tk.SetUdas(p, userProperties);
            if (!p.Modify()) throw new ToolException("O Tekla recusou a alteração (perfil/material inexistente?).");
            p.Select();
            var d = Tk.Describe(p, detail: true);
            if (failed.Count > 0) d["failedUserProperties"] = failed;
            return d;
        });

    [McpServerTool(Name = "set_user_properties")]
    [Description("Grava atributos definidos pelo usuário (UDAs) em vários objetos. Números inteiros viram int, com fração viram double, o resto texto.")]
    public static Task<CallToolResult> SetUserProperties(int[] ids, [Description("{\"COMMENT\": \"texto\", \"USER_FIELD_1\": 10}")] Dictionary<string, JsonElement> properties) =>
        ToolInvoker.Write("set_user_properties", new { ids, properties }, () =>
        {
            var res = Tk.GetMany(ids).Select(o =>
            {
                var failed = Tk.SetUdas(o, properties);
                return new { id = o.Identifier.ID, failed };
            }).ToList();
            return new { updated = res.Count(r => r.failed.Count == 0), results = res };
        });

    [McpServerTool(Name = "set_component_attributes")]
    [Description("Altera atributos de um componente/conexão existente (nomes do diálogo, ex.: 'tpl1', 'mat', 'nb'), opcionalmente carregando antes um arquivo de atributos.")]
    public static Task<CallToolResult> SetComponentAttributes(int id, Dictionary<string, JsonElement>? attributes = null, string? attributesFile = null,
        [Description("Nomes de atributos para ler de volta após a alteração.")] string[]? readBack = null) =>
        ToolInvoker.Write("set_component_attributes", new { id, attributes, attributesFile }, () =>
        {
            var c = Tk.As<BaseComponent>(Tk.Get(id), "id");
            if (!string.IsNullOrWhiteSpace(attributesFile)) c.LoadAttributesFromFile(attributesFile);
            CreateTools.ApplyAttributes(c, attributes);
            if (!c.Modify()) throw new ToolException("O Tekla recusou a alteração do componente.");
            var back = new Dictionary<string, object?>();
            foreach (var n in readBack ?? attributes?.Keys.ToArray() ?? [])
            {
                string s = ""; double dv = 0; int iv = 0;
                if (c.GetAttribute(n, ref dv)) back[n] = dv;
                else if (c.GetAttribute(n, ref iv)) back[n] = iv;
                else if (c.GetAttribute(n, ref s)) back[n] = s;
                else back[n] = null;
            }
            return new { id, modified = true, attributes = back };
        });

    [McpServerTool(Name = "get_component_attributes", ReadOnly = true)]
    [Description("Lê atributos de um componente/conexão pelos nomes internos do diálogo (ex.: 'tpl1', 'mat', 'nb', 'lbd').")]
    public static Task<CallToolResult> GetComponentAttributes(int id, string[] names) =>
        ToolInvoker.Read("get_component_attributes", new { id, names }, () =>
        {
            var c = Tk.As<BaseComponent>(Tk.Get(id), "id");
            var r = new Dictionary<string, object?>();
            foreach (var n in names)
            {
                string s = ""; double dv = 0; int iv = 0;
                if (c.GetAttribute(n, ref s) && s.Length > 0) r[n] = s;
                else if (c.GetAttribute(n, ref dv)) r[n] = dv;
                else if (c.GetAttribute(n, ref iv)) r[n] = iv;
                else r[n] = null;
            }
            return new { id, name = c.Name, number = c.Number, attributes = r };
        });

    [McpServerTool(Name = "delete_objects", Destructive = true)]
    [Description("Apaga objetos pelo id (peças, parafusos, componentes, grids...). Pode ser desfeito com Ctrl+Z no Tekla.")]
    public static Task<CallToolResult> DeleteObjects(int[] ids) =>
        ToolInvoker.Write("delete_objects", new { ids }, () =>
        {
            var objs = Tk.GetMany(ids);
            var failed = objs.Where(o => !o.Delete()).Select(o => o.Identifier.ID).ToList();
            return new { deleted = objs.Count - failed.Count, failed };
        });

    [McpServerTool(Name = "move_objects")]
    [Description("Move objetos por um vetor [dx,dy,dz] mm.")]
    public static Task<CallToolResult> MoveObjects(int[] ids, double[] vector) =>
        ToolInvoker.Write("move_objects", new { ids, vector }, () =>
        {
            var v = Tk.Pt(vector, "vector");
            var objs = Tk.GetMany(ids);
            var failed = objs.Where(o => !Operation.MoveObject(o, new Vector(v.X, v.Y, v.Z))).Select(o => o.Identifier.ID).ToList();
            return new { moved = objs.Count - failed.Count, failed };
        });

    [McpServerTool(Name = "copy_objects")]
    [Description("Copia objetos por um vetor [dx,dy,dz]; com copies>1 faz cópias em série (vetor acumulado).")]
    public static Task<CallToolResult> CopyObjects(int[] ids, double[] vector, int copies = 1) =>
        ToolInvoker.Write("copy_objects", new { ids, vector, copies }, () =>
        {
            if (copies < 1 || copies > 500) throw new ArgumentException("copies deve estar entre 1 e 500.");
            var v = Tk.Pt(vector, "vector");
            var objs = Tk.GetMany(ids);
            var created = new List<object>();
            for (var k = 1; k <= copies; k++)
                foreach (var o in objs)
                {
                    var c = Operation.CopyObject(o, new Vector(v.X * k, v.Y * k, v.Z * k));
                    if (c is not null) created.Add(new { source = o.Identifier.ID, id = c.Identifier.ID, type = c.GetType().Name });
                }
            return new { created = created.Count, objects = created };
        });

    [McpServerTool(Name = "mirror_objects")]
    [Description("Espelha (copy=true) ou move-espelha objetos em relação a uma linha no plano XY que passa por (x,y) com ângulo em graus.")]
    public static Task<CallToolResult> MirrorObjects(int[] ids, double x, double y, double angle, bool copy = true) =>
        ToolInvoker.Write("mirror_objects", new { ids, x, y, angle, copy }, () =>
        {
            var objs = Tk.GetMany(ids);
            var res = new List<object>();
            foreach (var o in objs)
            {
                if (copy)
                {
                    var c = Operation.CopyObjectMirror(o, x, y, angle);
                    res.Add(new { source = o.Identifier.ID, id = c?.Identifier.ID });
                }
                else res.Add(new { source = o.Identifier.ID, ok = Operation.MoveObjectMirror(o, x, y, angle) });
            }
            return res;
        });

    [McpServerTool(Name = "split_part")]
    [Description("Divide uma viga (Beam/PolyBeam) num ponto. Retorna o id da nova peça.")]
    public static Task<CallToolResult> SplitPart(int id, double[] point) =>
        ToolInvoker.Write("split_part", new { id, point }, () =>
        {
            var o = Tk.Get(id);
            var pt = Tk.Pt(point, "point");
            ModelObject? n = o switch
            {
                Beam b => Operation.Split(b, pt),
                PolyBeam pb => Operation.Split(pb, pt),
                _ => throw new ArgumentException("split_part aceita Beam ou PolyBeam."),
            };
            if (n is null) throw new ToolException("O Tekla não dividiu a peça (ponto fora do eixo?).");
            return new { original = id, newPart = n.Identifier.ID };
        });

    [McpServerTool(Name = "combine_beams")]
    [Description("Combina duas vigas colineares numa só (a segunda é absorvida pela primeira).")]
    public static Task<CallToolResult> CombineBeams(int target, int other) =>
        ToolInvoker.Write("combine_beams", new { target, other }, () =>
        {
            var r = Operation.Combine(Tk.As<Beam>(Tk.Get(target), "target"), Tk.As<Beam>(Tk.Get(other), "other"));
            if (r is null) throw new ToolException("O Tekla não combinou as vigas.");
            return Tk.Describe(r);
        });

    [McpServerTool(Name = "add_to_assembly")]
    [Description("Adiciona peças/sub-montagens à montagem de uma peça principal (equivale a 'Adicionar à montagem').")]
    public static Task<CallToolResult> AddToAssembly([Description("Id de uma peça da montagem de destino.")] int mainPartId, int[] partIds) =>
        ToolInvoker.Write("add_to_assembly", new { mainPartId, partIds }, () =>
        {
            var asm = Tk.As<Part>(Tk.Get(mainPartId), "mainPartId").GetAssembly();
            foreach (var p in Tk.GetMany(partIds))
                if (p is Part part) asm.Add(part.GetAssembly());
                else throw new ArgumentException($"{p.Identifier.ID} não é peça.");
            if (!asm.Modify()) throw new ToolException("O Tekla recusou a alteração da montagem.");
            return Tk.Describe(asm, detail: true);
        });
}
