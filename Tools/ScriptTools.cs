using System.Collections.Concurrent;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using DrawingHandler = Tekla.Structures.Drawing.DrawingHandler;
using Tekla.Structures.Model;
using TeklaMcp.Infrastructure;
using TeklaMcp.Server;
using TSM = Tekla.Structures.Model;

namespace TeklaMcp.Tools;

/// <summary>Variáveis visíveis no código do execute_csharp.</summary>
public sealed class ScriptGlobals
{
    public TSM.Model Model { get; set; } = null!;
    public DrawingHandler Drawings => new();
    public Tekla.Structures.Catalogs.CatalogHandler Catalog => new();
    public StringWriter Out { get; } = new();

    /// <summary>Escreve no log devolvido ao Claude (objetos viram JSON).</summary>
    public void Print(object? o) => Out.WriteLine(o is string s ? s : Fmt.Out(o));

    /// <summary>Objeto do modelo pelo id.</summary>
    public ModelObject Get(int id) => Tk.Get(id);

    /// <summary>Objetos selecionados na interface.</summary>
    public List<ModelObject> Selected() => Tk.Enumerate(new TSM.UI.ModelObjectSelector().GetSelectedObjects()).ToList();

    /// <summary>Todos os objetos dos tipos dados (ex.: All(typeof(Beam))).</summary>
    public List<ModelObject> All(params Type[] types) =>
        Tk.Enumerate(Model.GetModelObjectSelector().GetAllObjectsWithType(types.Length > 0 ? types : Tk.PartTypes)).ToList();

    public static Tekla.Structures.Geometry3d.Point P(double x, double y, double z = 0) => new(x, y, z);
}

[McpServerToolType]
public static class ScriptTools
{
    private static readonly ConcurrentDictionary<string, Script<object>> Cache = new();

    private static readonly Lazy<ScriptOptions> Options = new(() => ScriptOptions.Default
        .WithReferences(
            typeof(object).Assembly, typeof(Enumerable).Assembly, typeof(System.Collections.ArrayList).Assembly,
            typeof(Uri).Assembly, typeof(System.Drawing.Color).Assembly,
            typeof(TSM.Model).Assembly, typeof(Tekla.Structures.Identifier).Assembly, typeof(DrawingHandler).Assembly,
            typeof(Tekla.Structures.Catalogs.CatalogHandler).Assembly, typeof(Tekla.Structures.Datatype.Distance).Assembly,
            typeof(ScriptGlobals).Assembly)
        .WithImports(
            "System", "System.Linq", "System.Collections", "System.Collections.Generic", "System.IO", "System.Text",
            "Tekla.Structures", "Tekla.Structures.Model", "Tekla.Structures.Geometry3d", "Tekla.Structures.Model.Operations")
        .WithEmitDebugInformation(false));

    [McpServerTool(Name = "execute_csharp")]
    [Description("Executa C# (script Roslyn) com a Open API completa do Tekla. Variáveis: Model (Tekla.Structures.Model.Model), Drawings (DrawingHandler), Catalog (CatalogHandler), " +
                 "Print(obj) (texto/JSON no retorno), Get(id), Selected(), All(typeof(Beam), ...), P(x,y,z). " +
                 "Imports: System, Linq, Collections, IO, Tekla.Structures, .Model, .Geometry3d, .Model.Operations. Para desenhos use 'using TSD = Tekla.Structures.Drawing;'. " +
                 "O valor da última expressão (ou 'return x;') é devolvido em JSON. Com commit=true faz Model.CommitChanges() ao final.")]
    public static Task<CallToolResult> ExecuteCSharp(
        [Description("Código C#.")] string code,
        [Description("Chama Model.CommitChanges() ao final (necessário para ver alterações na tela).")] bool commit = true,
        int timeoutSeconds = 120) =>
        ToolInvoker.Custom("execute_csharp", new { codeLength = code?.Length, commit }, async () =>
        {
            if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("code vazio.");
            var script = Compile(code!);
            var globals = new ScriptGlobals();
            object? value = null;
            var run = ToolInvoker.Locked(() =>
            {
                globals.Model = Tk.Model;
                value = script.RunAsync(globals).GetAwaiter().GetResult().ReturnValue;
                if (commit) globals.Model.CommitChanges("MCP: execute_csharp");
                return null;
            });
            if (await System.Threading.Tasks.Task.WhenAny(run, System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(timeoutSeconds))).ConfigureAwait(false) != run)
                throw new TimeoutException($"O script passou de {timeoutSeconds}s (continua rodando no servidor até terminar).");
            await run.ConfigureAwait(false);
            return new { value = Simplify(value), output = globals.Out.ToString() };
        });

    private static Script<object> Compile(string code)
    {
        var key = Convert.ToBase64String(SHA256.Create().ComputeHash(Encoding.UTF8.GetBytes(code)));
        return Cache.GetOrAdd(key, _ =>
        {
            var s = CSharpScript.Create<object>("#line 1\n" + code, Options.Value, typeof(ScriptGlobals));
            var errors = s.Compile().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            if (errors.Count > 0)
                throw new ToolException("Erro de compilação", errors.Take(20).Select(d => new
                {
                    line = d.Location.GetLineSpan().StartLinePosition.Line + 1,
                    col = d.Location.GetLineSpan().StartLinePosition.Character + 1,
                    id = d.Id,
                    message = d.GetMessage(),
                }).ToList());
            return s;
        });
    }

    /// <summary>Objetos do Tekla não serializam bem: vira descrição; coleções de objetos idem.</summary>
    private static object? Simplify(object? v) => v switch
    {
        null => null,
        ModelObject mo => Tk.Describe(mo),
        Tekla.Structures.Geometry3d.Point p => Tk.P(p),
        string or bool or int or long or double or float or decimal => v,
        System.Collections.IDictionary => v,
        System.Collections.IEnumerable e => e.Cast<object?>().Take(1000).Select(Simplify).ToList(),
        _ => SafeJson(v),
    };

    private static object? SafeJson(object v)
    {
        try
        {
            Fmt.Out(v);
            return v;
        }
        catch
        {
            return v.ToString();
        }
    }
}
