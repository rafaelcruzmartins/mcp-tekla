using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Tekla.Structures.Catalogs;
using TeklaMcp.Infrastructure;
using TeklaMcp.Server;

namespace TeklaMcp.Tools;

[McpServerToolType]
public static class CatalogTools
{
    private static bool Has(string? value, string? part) =>
        string.IsNullOrWhiteSpace(part) || (value ?? "").IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;

    private static CatalogHandler Catalog()
    {
        _ = Tk.Model; // garante conexão com mensagem amigável
        var c = new CatalogHandler();
        if (!c.GetConnectionStatus()) throw new ToolException("Catálogo indisponível (Tekla sem modelo aberto?).");
        return c;
    }

    [McpServerTool(Name = "list_profiles", ReadOnly = true)]
    [Description("Procura perfis no catálogo do ambiente (biblioteca). Use antes de criar peças para acertar o nome exato.")]
    public static Task<CallToolResult> ListProfiles(
        [Description("Trecho do nome, ex.: 'W310', 'HEA', 'CS', 'L76'.")] string? contains = null,
        [Description("Tipo: I, L, Z, U, PL, D, PD, P, C, T, ZZ, CC, CW... (ProfileItemTypeEnum sem prefixo). Opcional.")] string? type = null,
        [Description("Inclui parâmetros (altura, largura, espessuras...).")] bool withParameters = false,
        int limit = 100) =>
        ToolInvoker.Read("list_profiles", new { contains, type }, () =>
        {
            var e = Catalog().GetLibraryProfileItems();
            var list = new List<object>();
            var total = 0;
            while (e.MoveNext())
            {
                if (e.Current is not LibraryProfileItem p) continue;
                if (!Has(p.ProfileName, contains)) continue;
                if (!string.IsNullOrWhiteSpace(type) && !p.ProfileItemType.ToString().EndsWith("_" + type!.ToUpperInvariant()) &&
                    !p.ProfileItemType.ToString().Equals(type, StringComparison.OrdinalIgnoreCase)) continue;
                total++;
                if (list.Count >= limit) continue;
                list.Add(new
                {
                    name = p.ProfileName,
                    type = p.ProfileItemType.ToString(),
                    parameters = withParameters
                        ? p.aProfileItemParameters?.Cast<ProfileItemParameter>().ToDictionary(x => x.Property, x => (object)Fmt.R(x.Value, 3))
                        : null,
                });
            }
            return new { total, returned = list.Count, profiles = list };
        });

    [McpServerTool(Name = "list_materials", ReadOnly = true)]
    [Description("Lista materiais do catálogo (nome, tipo, densidade, E).")]
    public static Task<CallToolResult> ListMaterials(string? contains = null, int limit = 200) =>
        ToolInvoker.Read("list_materials", new { contains }, () =>
        {
            var e = Catalog().GetMaterialItems();
            var list = new List<object>();
            while (e.MoveNext() && list.Count < limit)
            {
                var m = e.Current;
                if (!Has(m.MaterialName, contains) && !Has(m.AliasName1, contains)) continue;
                list.Add(new { name = m.MaterialName, alias = m.AliasName1, type = m.Type.ToString(), density = m.ProfileDensity, e = m.ModulusOfElasticity });
            }
            return list;
        });

    [McpServerTool(Name = "list_bolts", ReadOnly = true)]
    [Description("Lista padrões e diâmetros de parafusos do catálogo (com comprimentos disponíveis).")]
    public static Task<CallToolResult> ListBolts(string? standardContains = null, bool withLengths = false) =>
        ToolInvoker.Read("list_bolts", new { standardContains }, () =>
        {
            var e = Catalog().GetBoltItems();
            var list = new List<BoltItem>();
            while (e.MoveNext()) if (Has(e.Current.Standard, standardContains)) list.Add(e.Current);
            return list.GroupBy(b => b.Standard).Select(g => new
            {
                standard = g.Key,
                type = g.First().Type.ToString(),
                sizes = g.Select(b => withLengths ? (object)new { size = Fmt.R(b.Size, 2), lengths = b.Lengths } : Fmt.R(b.Size, 2)).ToList(),
            }).ToList();
        });

    [McpServerTool(Name = "list_components", ReadOnly = true)]
    [Description("Procura componentes no catálogo (conexões, detalhes, plugins, customizados): nome, nome na interface, número e tipo. Use com insert_component.")]
    public static Task<CallToolResult> ListComponents(string? contains = null, int limit = 100) =>
        ToolInvoker.Read("list_components", new { contains }, () =>
        {
            var e = Catalog().GetComponentItems();
            var list = new List<object>();
            var total = 0;
            while (e.MoveNext())
            {
                var c = e.Current;
                if (!Has(c.Name, contains) && !Has(c.UIName, contains) && !(contains is not null && c.Number.ToString() == contains)) continue;
                total++;
                if (list.Count < limit) list.Add(new { name = c.Name, uiName = c.UIName, number = c.Number, type = c.Type.ToString() });
            }
            return new { total, returned = list.Count, components = list };
        });

    [McpServerTool(Name = "list_user_properties", ReadOnly = true)]
    [Description("Lista definições de UDAs (atributos definidos pelo usuário) do ambiente: nome, tipo, nível e se afeta numeração.")]
    public static Task<CallToolResult> ListUserProperties(string? contains = null, int limit = 300) =>
        ToolInvoker.Read("list_user_properties", new { contains }, () =>
        {
            var e = Catalog().GetUserPropertyItems();
            var list = new List<object>();
            while (e.MoveNext() && list.Count < limit)
            {
                var u = e.Current;
                if (!Has(u.Name, contains)) continue;
                list.Add(new { name = u.Name, type = u.Type.ToString(), field = u.FieldType.ToString(), level = u.Level.ToString(), affectsNumbering = u.AffectsNumbering, unique = u.Unique });
            }
            return list;
        });
}
