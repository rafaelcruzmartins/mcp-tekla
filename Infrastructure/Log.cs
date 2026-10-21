using Microsoft.Extensions.Logging;

namespace TeklaMcp.Infrastructure;

/// <summary>Log em arquivo (%APPDATA%\TeklaMcp\logs). Nunca escreve no stdout (canal JSON-RPC).</summary>
public static class Log
{
    private static readonly object Gate = new();
    public static string Dir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TeklaMcp", "logs");

    private static string FilePath => Path.Combine(Dir, $"teklamcp-{DateTime.Now:yyyyMMdd}.log");

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg, Exception? ex = null) => Write("ERROR", ex is null ? msg : $"{msg}\n{ex}");

    private static void Write(string level, string msg)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                File.AppendAllText(FilePath, $"{DateTime.Now:HH:mm:ss.fff} [{level}] {msg}{Environment.NewLine}");
            }
        }
        catch
        {
            // log nunca pode derrubar o servidor
        }
    }
}

/// <summary>Provider do Microsoft.Extensions.Logging que redireciona para <see cref="Log"/>.</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName);
    public void Dispose() { }

    private sealed class FileLogger(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var msg = $"{category}: {formatter(state, exception)}";
            if (logLevel >= LogLevel.Error) Infrastructure.Log.Error(msg, exception);
            else Infrastructure.Log.Warn(msg);
        }
    }
}
