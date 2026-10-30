using System.Diagnostics;
using System.Text;

namespace ClaudePulse.Core;

public sealed record RenewResult(bool Ok, string? Error);

/// <summary>
/// Pede para a CLI do Claude Code renovar o próprio login, com uma pergunta mínima ao Haiku.
/// O app continua sem gravar os arquivos do Claude Code: quem renova e regrava o .credentials.json é a CLI,
/// então não há disputa com sessões abertas pelo refresh token.
/// </summary>
public static class CliRenewer
{
    // Sem ferramentas, MCP nem transcript: só o suficiente para a CLI precisar de um token válido.
    private const string PromptArgs = "-p ok --model haiku --no-session-persistence --strict-mcp-config";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    // Variáveis que fariam a CLI usar outro login (ou se achar dentro de outra sessão) em vez do .credentials.json.
    private static readonly string[] InheritedAuthVars =
    [
        "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "CLAUDE_CODE_OAUTH_TOKEN", "CLAUDE_CODE_SDK_HAS_HOST_AUTH_REFRESH",
        "CLAUDECODE", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CONFIG_DIR",
    ];

    public static async Task<RenewResult> RenewAsync(OAuthCredential credential)
    {
        var file = Path.Combine(credential.RootPath, ".credentials.json");
        var before = File.GetLastWriteTimeUtc(file);

        ProcessStartInfo psi;
        if (WslTarget(credential.RootPath) is { } wsl)
        {
            psi = new ProcessStartInfo("wsl.exe");
            foreach (var arg in new[] { "-d", wsl.Distro, "-u", wsl.User, "--", "bash", "-lic", "claude " + PromptArgs })
                psi.ArgumentList.Add(arg);
        }
        else
        {
            var cli = FindWindowsCli();
            if (cli == null)
                return new RenewResult(false, "CLI do Claude Code não encontrada (claude.exe ou claude.cmd).");
            psi = cli.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                ? new ProcessStartInfo("cmd.exe", $"/d /s /c \"\"{cli}\" {PromptArgs}\"")
                : new ProcessStartInfo(cli, PromptArgs);
            foreach (var name in InheritedAuthVars)
                psi.Environment.Remove(name);
            if (!IsDefaultRoot(credential.RootPath))
                psi.Environment["CLAUDE_CONFIG_DIR"] = credential.RootPath;
            if ((!psi.Environment.TryGetValue("CLAUDE_CODE_GIT_BASH_PATH", out var gitBash) || string.IsNullOrEmpty(gitBash))
                && FindGitBash() is { } bash)
                psi.Environment["CLAUDE_CODE_GIT_BASH_PATH"] = bash;
            psi.WorkingDirectory = Path.GetTempPath();
        }
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;

        try
        {
            using var process = Process.Start(psi);
            if (process == null)
                return new RenewResult(false, "Não foi possível iniciar a CLI do Claude Code.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(Timeout);
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                return new RenewResult(false, "A CLI do Claude Code não respondeu a tempo.");
            }

            if (File.GetLastWriteTimeUtc(file) > before)
                return new RenewResult(true, null);
            var detail = LastLine(await stderr) ?? LastLine(await stdout);
            return new RenewResult(false, process.ExitCode != 0 && detail != null
                ? $"A CLI falhou: {detail}"
                : "A CLI não renovou o login. Rode /login no Claude Code.");
        }
        catch (Exception ex)
        {
            return new RenewResult(false, "Não foi possível rodar a CLI do Claude Code: " + ex.Message);
        }
    }

    /// <summary>PATH primeiro (respeita a instalação escolhida), depois os locais padrão do instalador e do npm.</summary>
    private static string? FindWindowsCli()
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"))
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"));
        foreach (var dir in dirs)
        {
            foreach (var name in new[] { "claude.exe", "claude.cmd" })
            {
                try
                {
                    var path = Path.Combine(dir, name);
                    if (File.Exists(path))
                        return path;
                }
                catch
                {
                    // Entrada inválida no PATH.
                }
            }
        }
        return null;
    }

    /// <summary>
    /// A CLI no Windows exige o bash do Git e só o acha sozinha pelo PATH. Instalado só para o usuário
    /// (%LOCALAPPDATA%\Programs\Git), ele costuma ficar fora do PATH de quem abre o app pelo Windows.
    /// </summary>
    private static string? FindGitBash()
    {
        var fromPath = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(dir => dir.EndsWith(@"Git\cmd", StringComparison.OrdinalIgnoreCase))
            .Select(dir => Path.Combine(Path.GetDirectoryName(dir)!, "bin", "bash.exe"));
        string[] known =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Git", "bin", "bash.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop", "apps", "git", "current", "bin", "bash.exe"),
        ];
        return fromPath.Concat(known).FirstOrDefault(File.Exists);
    }

    /// <summary>\\wsl.localhost\{distro}\home\{user}\.claude ou \\wsl.localhost\{distro}\root\.claude.</summary>
    private static (string Distro, string User)? WslTarget(string rootPath)
    {
        const string prefix = @"\\wsl.localhost\";
        if (!rootPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;
        var parts = rootPath[prefix.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 4 && parts[1].Equals("home", StringComparison.OrdinalIgnoreCase))
            return (parts[0], parts[2]);
        if (parts.Length >= 3 && parts[1].Equals("root", StringComparison.OrdinalIgnoreCase))
            return (parts[0], "root");
        return null;
    }

    private static bool IsDefaultRoot(string rootPath) =>
        string.Equals(Path.GetFullPath(rootPath).TrimEnd('\\'),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"),
            StringComparison.OrdinalIgnoreCase);

    private static string? LastLine(string text)
    {
        var line = text.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);
        return line == null ? null : line.Length <= 120 ? line : line[..117] + "…";
    }
}
