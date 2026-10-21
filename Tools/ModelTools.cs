using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Tekla.Structures.Model;
using TeklaMcp.Infrastructure;
using TeklaMcp.Server;

namespace TeklaMcp.Tools;

[McpServerToolType]
public static class ModelTools
{
    [McpServerTool(Name = "get_tekla_status", ReadOnly = true)]
    [Description("Verifica a conexão com o Tekla Structures: versão, modelo aberto (nome, pasta, fase atual), projeto e se a numeração está atualizada. Use primeiro.")]
    public static async Task<CallToolResult> GetTeklaStatus()
    {
        // Sem a exigência de conexão: o status precisa responder mesmo com o Tekla fechado.
        return await ToolInvoker.Read("get_tekla_status", null, Tk.Status);
    }

    [McpServerTool(Name = "get_project_info", ReadOnly = true)]
    [Description("Lê as propriedades do projeto (Informações do projeto) e seus atributos definidos pelo usuário (UDAs).")]
    public static Task<CallToolResult> GetProjectInfo() =>
        ToolInvoker.Read("get_project_info", null, () =>
        {
            var p = Tk.Model.GetProjectInfo();
            var udas = new Dictionary<string, object?>();
            foreach (var get in new Func<System.Collections.Hashtable>[]
                     {
                         () => { var h = new System.Collections.Hashtable(); p.GetStringUserProperties(ref h); return h; },
                         () => { var h = new System.Collections.Hashtable(); p.GetDoubleUserProperties(ref h); return h; },
                         () => { var h = new System.Collections.Hashtable(); p.GetIntegerUserProperties(ref h); return h; },
                     })
                foreach (System.Collections.DictionaryEntry e in get()) udas[e.Key.ToString()!] = e.Value;
            return new
            {
                p.Name, p.ProjectNumber, p.Description, p.Builder, p.Designer, p.Object, p.Location, p.Address,
                p.Town, p.Region, p.PostalCode, p.Country, p.StartDate, p.EndDate, p.Info1, p.Info2, p.GUID,
                userProperties = udas,
            };
        });

    [McpServerTool(Name = "set_project_info", Destructive = false)]
    [Description("Altera propriedades do projeto. Campos não informados ficam como estão.")]
    public static Task<CallToolResult> SetProjectInfo(
        string? name = null, string? projectNumber = null, string? description = null, string? builder = null,
        string? designer = null, string? objectName = null, string? location = null, string? address = null,
        string? town = null, string? region = null, string? country = null, string? startDate = null,
        string? endDate = null, string? info1 = null, string? info2 = null,
        [Description("UDAs do projeto: {\"NOME\": valor}")] Dictionary<string, JsonElement>? userProperties = null) =>
        ToolInvoker.Write("set_project_info", new { name, projectNumber }, () =>
        {
            var p = Tk.Model.GetProjectInfo();
            if (name is not null) p.Name = name;
            if (projectNumber is not null) p.ProjectNumber = projectNumber;
            if (description is not null) p.Description = description;
            if (builder is not null) p.Builder = builder;
            if (designer is not null) p.Designer = designer;
            if (objectName is not null) p.Object = objectName;
            if (location is not null) p.Location = location;
            if (address is not null) p.Address = address;
            if (town is not null) p.Town = town;
            if (region is not null) p.Region = region;
            if (country is not null) p.Country = country;
            if (startDate is not null) p.StartDate = startDate;
            if (endDate is not null) p.EndDate = endDate;
            if (info1 is not null) p.Info1 = info1;
            if (info2 is not null) p.Info2 = info2;
            var failed = new List<string>();
            if (userProperties is not null)
                foreach (var kv in userProperties)
                {
                    var v = kv.Value;
                    var ok = v.ValueKind == JsonValueKind.Number
                        ? (v.TryGetInt32(out var i) ? p.SetUserProperty(kv.Key, i) : p.SetUserProperty(kv.Key, v.GetDouble()))
                        : p.SetUserProperty(kv.Key, v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString());
                    if (!ok) failed.Add(kv.Key);
                }
            if (!p.Modify()) throw new ToolException("O Tekla recusou a alteração das informações do projeto.");
            return new { modified = true, failedUserProperties = failed };
        });

    [McpServerTool(Name = "list_phases", ReadOnly = true)]
    [Description("Lista as fases do modelo (número, nome, comentário, atual).")]
    public static Task<CallToolResult> ListPhases() =>
        ToolInvoker.Read("list_phases", null, () =>
            Tk.Model.GetPhases().Cast<Phase>()
                .Select(p => new { number = p.PhaseNumber, name = p.PhaseName, comment = p.PhaseComment, current = p.IsCurrentPhase != 0 })
                .ToList());

    [McpServerTool(Name = "save_model")]
    [Description("Salva o modelo aberto em disco (equivale a Ctrl+S).")]
    public static Task<CallToolResult> SaveModel(
        [Description("Comentário do salvamento (histórico do modelo).")] string comment = "Salvo pelo MCP") =>
        ToolInvoker.Read("save_model", null, () =>
        {
            var ok = new ModelHandler().Save(comment, Environment.UserName);
            if (!ok) throw new ToolException("O Tekla não salvou o modelo (há um comando ou diálogo em andamento?).");
            return new { saved = true };
        });

    [McpServerTool(Name = "open_model", Destructive = true)]
    [Description("Abre outro modelo pela pasta (ex.: D:\\TeklaStructures Model\\Projeto). O modelo atual é fechado; salve antes com save_model.")]
    public static Task<CallToolResult> OpenModel(
        [Description("Pasta do modelo (a que contém o .db1).")] string modelFolder) =>
        ToolInvoker.Read("open_model", new { modelFolder }, () =>
        {
            if (!Directory.Exists(modelFolder)) throw new ArgumentException($"Pasta não existe: {modelFolder}");
            var ok = new ModelHandler().Open(modelFolder, false);
            if (!ok) throw new ToolException("O Tekla não abriu o modelo.");
            return Tk.Status();
        });
}
