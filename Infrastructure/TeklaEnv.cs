using System.Diagnostics;
using System.Reflection;
using Microsoft.Win32;

namespace TeklaMcp.Infrastructure;

/// <summary>
/// Localiza a pasta bin do Tekla e resolve as DLLs da Open API a partir dela. Assim o mesmo exe funciona
/// após um service pack: o canal de remoting usa a versão do arquivo carregado, que passa a ser a instalada.
/// </summary>
public static class TeklaEnv
{
    public static string BinDir { get; } = FindBin();

    public static void InstallResolver()
    {
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
        {
            var name = new AssemblyName(e.Name).Name;
            foreach (var dir in new[] { Path.Combine(BinDir, "Net48Runtime"), BinDir })
            {
                var p = Path.Combine(dir, name + ".dll");
                if (File.Exists(p)) return Assembly.LoadFrom(p);
            }
            return null;
        };
    }

    private static string FindBin()
    {
        var env = Environment.GetEnvironmentVariable("TEKLA_BIN");
        if (IsBin(env)) return env!;

        try
        {
            foreach (var p in Process.GetProcessesByName("TeklaStructures"))
            {
                var dir = Path.GetDirectoryName(p.MainModule?.FileName);
                if (IsBin(dir)) return dir!;
            }
        }
        catch
        {
            // sem acesso ao processo: cai para o registro
        }

        foreach (var root in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
        {
            using var key = Registry.LocalMachine.OpenSubKey(root);
            if (key is null) continue;
            var candidates = key.GetSubKeyNames()
                .Select(n => key.OpenSubKey(n))
                .Where(k => k is not null && (k.GetValue("DisplayName") as string ?? "").StartsWith("Tekla Structures 20"))
                .Select(k => Path.Combine(k!.GetValue("InstallLocation") as string ?? "", "bin"))
                .Where(IsBin)
                .OrderByDescending(d => d)
                .ToList();
            if (candidates.Count > 0) return candidates[0];
        }

        return @"C:\TeklaStructures\2026.0\bin";
    }

    private static bool IsBin(string? dir) =>
        !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, "Tekla.Structures.Model.dll"));
}
