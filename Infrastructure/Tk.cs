using System.Collections;
using Tekla.Structures;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model;
using TeklaMcp.Server;

namespace TeklaMcp.Infrastructure;

/// <summary>Conexão com o Tekla e utilitários comuns às ferramentas (busca de objetos, pontos, descrição).</summary>
public static class Tk
{
    private static Model? _model;

    /// <summary>Modelo conectado; recria o proxy se o Tekla foi reaberto ou trocou de modelo.</summary>
    public static Model Model
    {
        get
        {
            if (_model is not null && _model.GetConnectionStatus()) return _model;
            var m = new Model();
            if (!m.GetConnectionStatus())
                throw new ToolException(
                    "Sem conexão com o Tekla Structures. Abra o Tekla Structures com um modelo (a Open API só responde com modelo aberto).",
                    new { teklaBin = TeklaEnv.BinDir });
            _model = m;
            return m;
        }
    }

    public static object Status()
    {
        var m = new Model();
        var connected = m.GetConnectionStatus();
        if (!connected)
            return new { connected, teklaBin = TeklaEnv.BinDir, hint = "Abra o Tekla Structures com um modelo." };
        _model = m;
        var info = m.GetInfo();
        var proj = m.GetProjectInfo();
        return new
        {
            connected,
            version = TeklaStructuresInfo.GetCurrentProgramVersion(),
            build = TeklaStructuresInfo.GetBuildNumber(),
            user = TeklaStructuresInfo.GetCurrentUser(),
            teklaBin = TeklaEnv.BinDir,
            model = new
            {
                name = info.ModelName,
                path = info.ModelPath,
                currentPhase = info.CurrentPhase,
                singleUser = info.SingleUserModel,
                shared = info.SharedModel,
                northDirection = info.NorthDirection,
            },
            project = new { name = proj.Name, number = proj.ProjectNumber, builder = proj.Builder, designer = proj.Designer },
            numberingUpToDate = Tekla.Structures.Model.Operations.Operation.IsNumberingUpToDateAll(),
            units = "mm",
        };
    }

    // ---------- pontos ----------

    public static Point Pt(double[]? a, string name)
    {
        if (a is null || a.Length < 2 || a.Length > 3)
            throw new ArgumentException($"'{name}' deve ser [x, y] ou [x, y, z] em mm.");
        return new Point(a[0], a[1], a.Length == 3 ? a[2] : 0);
    }

    public static List<Point> Pts(double[][]? a, string name, int min)
    {
        if (a is null || a.Length < min) throw new ArgumentException($"'{name}' precisa de pelo menos {min} pontos.");
        return a.Select((p, i) => Pt(p, $"{name}[{i}]")).ToList();
    }

    public static double[] P(Point? p) => p is null ? [] : [Fmt.R(p.X), Fmt.R(p.Y), Fmt.R(p.Z)];

    // ---------- objetos ----------

    public static ModelObject Get(int? id, string? guid = null)
    {
        ModelObject? o = null;
        if (id is > 0) o = Model.SelectModelObject(new Identifier(id.Value));
        else if (!string.IsNullOrWhiteSpace(guid)) o = Model.SelectModelObject(Model.GetIdentifierByGUID(guid));
        else throw new ArgumentException("Informe 'id' ou 'guid'.");
        if (o is null || !o.Identifier.IsValid() || o.Identifier.ID == 0)
            throw new ToolException($"Objeto não encontrado (id={id}, guid={guid}).");
        return o;
    }

    public static List<ModelObject> GetMany(int[]? ids)
    {
        if (ids is null || ids.Length == 0) throw new ArgumentException("Informe ao menos um id em 'ids'.");
        var list = new List<ModelObject>();
        var missing = new List<int>();
        foreach (var id in ids)
        {
            var o = Model.SelectModelObject(new Identifier(id));
            if (o is null || o.Identifier.ID == 0) missing.Add(id); else list.Add(o);
        }
        if (missing.Count > 0) throw new ToolException($"Ids não encontrados: {string.Join(", ", missing)}.", new { missing });
        return list;
    }

    public static T As<T>(ModelObject o, string role) where T : ModelObject =>
        o as T ?? throw new ToolException($"{role}: o objeto {o.Identifier.ID} é {o.GetType().Name}, esperado {typeof(T).Name}.");

    public static IEnumerable<ModelObject> Enumerate(ModelObjectEnumerator e)
    {
        while (e.MoveNext())
            if (e.Current is { } o) yield return o;
    }

    /// <summary>Tipos concretos de peça (o seletor por tipo não aceita a classe base Part).</summary>
    public static readonly Type[] PartTypes =
        [typeof(Beam), typeof(PolyBeam), typeof(ContourPlate), typeof(BentPlate), typeof(LoftedPlate), typeof(SpiralBeam), typeof(Brep)];

    public static Type ModelType(string name) =>
        typeof(Beam).Assembly.GetType("Tekla.Structures.Model." + name, false, true) is { } t && typeof(ModelObject).IsAssignableFrom(t)
            ? t
            : throw new ArgumentException($"Tipo '{name}' desconhecido. Exemplos: Beam, PolyBeam, ContourPlate, Assembly, BoltArray, BoltGroup, Weld, Connection, Component, Detail, Grid, SingleRebar, RebarGroup, Fitting, CutPlane, BooleanPart.");

    public static TEnum ParseEnum<TEnum>(string? value, TEnum fallback) where TEnum : struct
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        if (Enum.TryParse<TEnum>(value!.Trim(), true, out var r)) return r;
        throw new ArgumentException($"Valor '{value}' inválido. Use: {string.Join(", ", Enum.GetNames(typeof(TEnum)))}.");
    }

    // ---------- descrição ----------

    public static Dictionary<string, object?> Describe(ModelObject o, bool detail = false)
    {
        var d = new Dictionary<string, object?>
        {
            ["id"] = o.Identifier.ID,
            ["guid"] = o.Identifier.GUID.ToString(),
            ["type"] = o.GetType().Name,
        };
        switch (o)
        {
            case Part p:
                d["name"] = p.Name;
                d["profile"] = p.Profile?.ProfileString;
                d["material"] = p.Material?.MaterialString;
                d["class"] = p.Class;
                if (p is Beam b)
                {
                    d["beamType"] = b.Type.ToString();
                    d["start"] = P(b.StartPoint);
                    d["end"] = P(b.EndPoint);
                }
                else if (p is ContourPlate cp) d["points"] = ContourPts(cp.Contour);
                else if (p is PolyBeam pb) d["points"] = ContourPts(pb.Contour);
                if (detail)
                {
                    d["finish"] = p.Finish;
                    d["partNumber"] = new { prefix = p.PartNumber?.Prefix, start = p.PartNumber?.StartNumber };
                    d["assemblyNumber"] = new { prefix = p.AssemblyNumber?.Prefix, start = p.AssemblyNumber?.StartNumber };
                    d["position"] = new
                    {
                        plane = p.Position.Plane.ToString(), planeOffset = p.Position.PlaneOffset,
                        rotation = p.Position.Rotation.ToString(), rotationOffset = p.Position.RotationOffset,
                        depth = p.Position.Depth.ToString(), depthOffset = p.Position.DepthOffset,
                    };
                    d["castUnitType"] = p.CastUnitType.ToString();
                    d["partMark"] = Safe(() => p.GetPartMark());
                    d["assemblyId"] = Safe(() => p.GetAssembly()?.Identifier.ID);
                }
                break;
            case BoltGroup bg:
                d["boltSize"] = Fmt.R(bg.BoltSize, 2);
                d["standard"] = bg.BoltStandard;
                d["partToBoltTo"] = bg.PartToBoltTo?.Identifier.ID;
                d["partToBeBolted"] = bg.PartToBeBolted?.Identifier.ID;
                d["firstPosition"] = P(bg.FirstPosition);
                d["secondPosition"] = P(bg.SecondPosition);
                d["boltCount"] = bg.BoltPositions?.Count;
                if (detail)
                {
                    d["boltType"] = bg.BoltType.ToString();
                    d["tolerance"] = bg.Tolerance;
                    d["threadInMaterial"] = bg.ThreadInMaterial.ToString();
                    d["otherParts"] = bg.GetOtherPartsToBolt()?.Cast<Part>().Select(x => x.Identifier.ID).ToList();
                    if (bg is BoltArray ba)
                    {
                        d["distX"] = Enumerable.Range(0, ba.GetBoltDistXCount()).Select(ba.GetBoltDistX).ToList();
                        d["distY"] = Enumerable.Range(0, ba.GetBoltDistYCount()).Select(ba.GetBoltDistY).ToList();
                    }
                }
                break;
            case BaseWeld w:
                d["mainObject"] = w.MainObject?.Identifier.ID;
                d["secondaryObject"] = w.SecondaryObject?.Identifier.ID;
                d["sizeAbove"] = w.SizeAbove;
                d["typeAbove"] = w.TypeAbove.ToString();
                d["sizeBelow"] = w.SizeBelow;
                d["typeBelow"] = w.TypeBelow.ToString();
                d["shopWeld"] = w.ShopWeld;
                break;
            case BaseComponent c:
                d["name"] = c.Name;
                d["number"] = c.Number;
                if (c is Connection cn)
                {
                    d["primary"] = cn.GetPrimaryObject()?.Identifier.ID;
                    d["secondaries"] = cn.GetSecondaryObjects()?.Cast<ModelObject>().Select(x => x.Identifier.ID).ToList();
                    d["status"] = cn.Status.ToString();
                }
                else if (c is Detail dt)
                {
                    d["primary"] = dt.GetPrimaryObject()?.Identifier.ID;
                    d["referencePoint"] = P(dt.GetReferencePoint());
                    d["status"] = dt.Status.ToString();
                }
                break;
            case Assembly a:
                d["name"] = a.Name;
                d["assemblyNumber"] = new { prefix = a.AssemblyNumber?.Prefix, start = a.AssemblyNumber?.StartNumber };
                d["mainPart"] = Safe(() => a.GetMainPart()?.Identifier.ID);
                d["assemblyType"] = Safe(() => a.GetAssemblyType().ToString());
                if (detail)
                {
                    d["secondaries"] = Safe(() => a.GetSecondaries()?.Cast<ModelObject>().Select(x => x.Identifier.ID).ToList());
                    d["subAssemblies"] = Safe(() => a.GetSubAssemblies()?.Cast<ModelObject>().Select(x => x.Identifier.ID).ToList());
                }
                break;
            case Grid g:
                d["coordinateX"] = g.CoordinateX;
                d["coordinateY"] = g.CoordinateY;
                d["coordinateZ"] = g.CoordinateZ;
                d["labelX"] = g.LabelX;
                d["labelY"] = g.LabelY;
                d["labelZ"] = g.LabelZ;
                break;
        }
        return d;
    }

    private static List<double[]> ContourPts(Contour? c) =>
        c?.ContourPoints?.Cast<Point>().Select(P).ToList() ?? [];

    public static T? Safe<T>(Func<T> f)
    {
        try { return f(); }
        catch { return default; }
    }

    // ---------- propriedades ----------

    public static readonly string[] DefaultReportProps =
        ["PART_POS", "ASSEMBLY_POS", "PROFILE_TYPE", "LENGTH", "WEIGHT", "WEIGHT_NET", "AREA", "VOLUME", "PHASE"];

    /// <summary>Lê propriedades de relatório tentando double, int e string (a API exige o tipo certo).</summary>
    public static Dictionary<string, object?> Report(ModelObject o, IEnumerable<string> names)
    {
        var r = new Dictionary<string, object?>();
        foreach (var raw in names)
        {
            var n = raw.Trim();
            if (n.Length == 0) continue;
            double dv = 0; int iv = 0; string sv = "";
            if (o.GetReportProperty(n, ref dv)) r[n] = Fmt.R(dv, 4);
            else if (o.GetReportProperty(n, ref iv)) r[n] = iv;
            else if (o.GetReportProperty(n, ref sv)) r[n] = sv;
            else r[n] = null;
        }
        return r;
    }

    public static Dictionary<string, object?> Udas(ModelObject o)
    {
        var h = new Hashtable();
        o.GetAllUserProperties(ref h);
        return h.Cast<DictionaryEntry>().ToDictionary(e => e.Key.ToString()!, e => e.Value is double x ? Fmt.R(x, 6) : e.Value);
    }

    /// <summary>Grava UDAs: número inteiro → int, número com fração → double, resto → string.</summary>
    public static List<string> SetUdas(ModelObject o, Dictionary<string, System.Text.Json.JsonElement>? props)
    {
        var failed = new List<string>();
        if (props is null) return failed;
        foreach (var kv in props)
        {
            bool ok;
            var v = kv.Value;
            if (v.ValueKind == System.Text.Json.JsonValueKind.Number)
                ok = v.TryGetInt32(out var i) ? o.SetUserProperty(kv.Key, i) : o.SetUserProperty(kv.Key, v.GetDouble());
            else
                ok = o.SetUserProperty(kv.Key, v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() ?? "" : v.ToString());
            if (!ok) failed.Add(kv.Key);
        }
        return failed;
    }
}
