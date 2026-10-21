using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TeklaMcp.Infrastructure;

namespace TeklaMcp;

/// <summary>
/// Servidor MCP (stdio) para o Tekla Structures. Roda como processo próprio, lançado pelo Claude Desktop,
/// e conversa com o TeklaStructures.exe aberto pela Open API (Trimble.Remoting).
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        // Antes de qualquer tipo do Tekla ser tocado: as DLLs vêm da instalação, não da pasta do exe.
        TeklaEnv.InstallResolver();

        // A Open API escreve mensagens de conexão no Console; o stdout é o canal JSON-RPC.
        // O transporte stdio do SDK usa Console.OpenStandardOutput(), que não é afetado por SetOut.
        Console.SetOut(Console.Error);

        if (args.Length > 0 && args[0] == "--check") return Check();
        return Run(args);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Check()
    {
        Console.Error.WriteLine($"Tekla bin: {TeklaEnv.BinDir}");
        try
        {
            Console.Error.WriteLine(Fmt.Out(Tk.Status()));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Run(string[] args)
    {
        Log.Info($"TeklaMcp {typeof(Program).Assembly.GetName().Version} iniciando; Tekla bin = {TeklaEnv.BinDir}");
        try
        {
            var builder = Host.CreateApplicationBuilder(args);
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(new FileLoggerProvider());
            builder.Services
                .AddMcpServer(o =>
                {
                    o.ServerInfo = new() { Name = "tekla", Version = "1.0.0" };
                    o.ServerInstructions = Instructions;
                })
                .WithStdioServerTransport()
                .WithToolsFromAssembly(typeof(Program).Assembly, Fmt.Options);
            builder.Build().Run();
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("falha fatal", ex);
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private const string Instructions =
        "Ferramentas para o Tekla Structures aberto nesta máquina (Open API). " +
        "Unidades: milímetros e graus; coordenadas no sistema do plano de trabalho atual do modelo. " +
        "Objetos são identificados pelo 'id' numérico (ou 'guid'). Comece por get_tekla_status. " +
        "Cada ferramenta de escrita faz CommitChanges ao final; save_model grava em disco. " +
        "Para o que não houver ferramenta, use execute_csharp (Open API completa: Model, DrawingHandler, Catalogs).";
}
