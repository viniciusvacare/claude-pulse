using System.Diagnostics;
using System.Text;

namespace ClaudePulse.Core;

/// <summary>Uma pasta de configuração do Claude Code (~/.claude) e de onde ela vem.</summary>
public sealed record ClaudeRoot(string Path, string Origin)
{
    public bool IsWsl => Origin.StartsWith("WSL", StringComparison.Ordinal);
}

/// <summary>
/// Localiza as pastas ~/.claude do Windows e, opcionalmente, das distros WSL em execução
/// (só as que já estão rodando, para nunca acordar uma distro parada).
/// </summary>
public static class ClaudeHome
{
    private static readonly object Gate = new();
    private static IReadOnlyList<ClaudeRoot>? _cache;
    private static DateTime _cacheAt;
    private static bool _cacheIncludesWsl;

    public static IReadOnlyList<ClaudeRoot> Discover(bool includeWsl)
    {
        lock (Gate)
        {
            if (_cache != null && _cacheIncludesWsl == includeWsl && DateTime.UtcNow - _cacheAt < TimeSpan.FromSeconds(60))
                return _cache;

            var roots = new List<ClaudeRoot>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Add(string path, string origin)
            {
                try
                {
                    if (Directory.Exists(path) && seen.Add(System.IO.Path.GetFullPath(path)))
                        roots.Add(new ClaudeRoot(path, origin));
                }
                catch
                {
                    // Sem permissão ou caminho inválido.
                }
            }

            var configDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            if (!string.IsNullOrWhiteSpace(configDir))
                Add(configDir, "Windows");
            Add(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"), "Windows");

            if (includeWsl)
            {
                foreach (var distro in RunningWslDistros())
                {
                    var unc = $@"\\wsl.localhost\{distro}";
                    try
                    {
                        foreach (var home in Directory.EnumerateDirectories(unc + @"\home"))
                            Add(System.IO.Path.Combine(home, ".claude"), $"WSL · {distro}");
                    }
                    catch
                    {
                        // Distro sem /home (ex.: docker-desktop).
                    }
                    Add(unc + @"\root\.claude", $"WSL · {distro}");
                }
            }

            _cache = roots;
            _cacheAt = DateTime.UtcNow;
            _cacheIncludesWsl = includeWsl;
            return roots;
        }
    }

    public static void Invalidate()
    {
        lock (Gate)
            _cache = null;
    }

    private static IEnumerable<string> RunningWslDistros()
    {
        try
        {
            var psi = new ProcessStartInfo("wsl.exe", "--list --running --quiet")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.Unicode,
            };
            using var process = Process.Start(psi);
            if (process == null)
                return [];
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(4000) || process.ExitCode != 0)
                return [];
            return output
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim('\0', ' ', '\t'))
                .Where(s => s.Length > 0)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }
}
