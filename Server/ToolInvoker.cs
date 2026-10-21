using System.Diagnostics;
using ModelContextProtocol.Protocol;
using TeklaMcp.Infrastructure;

namespace TeklaMcp.Server;

/// <summary>Erro de ferramenta com dados estruturados para o modelo.</summary>
public sealed class ToolException(string message, object? details = null) : Exception(message)
{
    public object? Details { get; } = details;
}

/// <summary>
/// Ponto único de execução das ferramentas: serializa as chamadas ao Tekla (uma por vez), faz
/// CommitChanges nas escritas e converte resultado/erro em JSON {ok, tool, durationMs, units, result}.
/// </summary>
public static class ToolInvoker
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Consulta: sem CommitChanges.</summary>
    public static Task<CallToolResult> Read(string tool, object? args, Func<object?> body) =>
        Run(tool, args, () => Locked(() => body()));

    /// <summary>Escrita: CommitChanges ao final (o Tekla redesenha e registra o undo).</summary>
    public static Task<CallToolResult> Write(string tool, object? args, Func<object?> body) =>
        Run(tool, args, () => Locked(() =>
        {
            var r = body();
            Tk.Model.CommitChanges("MCP: " + tool);
            return r;
        }));

    /// <summary>Corpo assíncrono livre (ex.: compilar antes de entrar na fila do Tekla).</summary>
    public static Task<CallToolResult> Custom(string tool, object? args, Func<Task<object?>> body) =>
        Run(tool, args, body);

    /// <summary>Executa no pool com exclusão mútua: o remoting do Tekla não gosta de chamadas concorrentes.</summary>
    public static async Task<object?> Locked(Func<object?> body)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(body).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<CallToolResult> Run(string tool, object? args, Func<Task<object?>> exec)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await exec().ConfigureAwait(false);
            sw.Stop();
            Log.Info($"{tool} ok {sw.ElapsedMilliseconds}ms");
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = Fmt.Out(new { ok = true, tool, durationMs = sw.ElapsedMilliseconds, units = "mm", result }) }],
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            var e = ex is AggregateException { InnerException: not null } ag ? ag.InnerException! : ex;
            if (e is System.Reflection.TargetInvocationException { InnerException: not null } ti) e = ti.InnerException!;
            var validation = e is ArgumentException or ToolException or InvalidOperationException;
            Log.Warn($"{tool} erro {sw.ElapsedMilliseconds}ms: {e.GetType().Name}: {e.Message} args={SafeArgs(args)}");
            return new CallToolResult
            {
                IsError = true,
                Content =
                [
                    new TextContentBlock
                    {
                        Text = Fmt.Out(new
                        {
                            ok = false,
                            tool,
                            durationMs = sw.ElapsedMilliseconds,
                            error = new
                            {
                                type = e.GetType().Name,
                                message = e.Message,
                                details = (e as ToolException)?.Details,
                                stackTrace = validation ? null : e.StackTrace,
                            },
                        }),
                    },
                ],
            };
        }
    }

    private static string SafeArgs(object? args)
    {
        try
        {
            var t = Fmt.Out(args);
            return t.Length > 500 ? t.Substring(0, 500) + "…" : t;
        }
        catch { return "?"; }
    }
}
