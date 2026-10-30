using System.Collections.Concurrent;
using System.Text.Json;

namespace ClaudePulse.Core;

/// <summary>Conta do Claude (um e-mail numa organização). A chave separa a mesma pessoa em orgs diferentes.</summary>
public sealed record ClaudeAccount(string Key, string Email, string? Name, string? Organization, string? OrganizationType);

public sealed record OAuthCredential(
    string AccessToken,
    DateTimeOffset ExpiresAt,
    string? Plan,
    string Origin,
    string RootPath,
    ClaudeAccount? Account,
    DateTimeOffset WrittenAt)
{
    public bool IsExpired => ExpiresAt <= DateTimeOffset.UtcNow.AddSeconds(30);

    /// <summary>"" quando não dá para identificar a conta.</summary>
    public string AccountKey => Account?.Key ?? "";

    /// <summary>True quando <see cref="Account"/> veio do próprio token (/api/oauth/profile), e não do .claude.json.</summary>
    public bool AccountFromToken { get; init; }

    /// <summary>A conta que o .claude.json indica; pode ser outra, porque o app desktop também grava esse arquivo.</summary>
    public ClaudeAccount? FileAccount { get; init; }

    public bool AccountMismatch => AccountFromToken && Account != null && FileAccount != null && Account.Key != FileAccount.Key;
}

/// <summary>
/// Lê o .credentials.json que o Claude Code mantém em ~/.claude e a conta logada em ~/.claude.json.
/// Só leitura: quem renova o token é o próprio Claude Code, então o app nunca escreve nesses arquivos.
/// </summary>
public static class CredentialReader
{
    private static readonly ConcurrentDictionary<string, (DateTime Stamp, ClaudeAccount? Account)> AccountCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Um login por pasta do Claude Code encontrada.</summary>
    public static List<OAuthCredential> FindAll(IEnumerable<ClaudeRoot> roots) =>
        roots.Select(TryRead).OfType<OAuthCredential>().ToList();

    /// <summary>
    /// Escolhe o login a usar: o válido mais recente, opcionalmente restrito a uma conta fixada.
    /// Devolve um expirado só se não houver válido (para a mensagem de erro).
    /// </summary>
    public static OAuthCredential? PickBest(IEnumerable<OAuthCredential> credentials, string? pinnedAccount)
    {
        OAuthCredential? best = null;
        foreach (var credential in credentials)
        {
            if (pinnedAccount != null && credential.AccountKey != pinnedAccount)
                continue;
            if (best == null
                || (best.IsExpired && !credential.IsExpired)
                || (best.IsExpired == credential.IsExpired && credential.ExpiresAt > best.ExpiresAt))
            {
                best = credential;
            }
        }
        return best;
    }

    private static OAuthCredential? TryRead(ClaudeRoot root)
    {
        var file = Path.Combine(root.Path, ".credentials.json");
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists)
                return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth))
                return null;
            var token = oauth.Str("accessToken");
            if (string.IsNullOrEmpty(token))
                return null;
            var expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(oauth.Long("expiresAt"));
            return new OAuthCredential(token, expiresAt, oauth.Str("subscriptionType"), root.Origin, root.Path,
                ReadAccount(root.Path), new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
        }
        catch
        {
            // Arquivo sendo reescrito pelo Claude Code; tenta de novo no próximo ciclo.
            return null;
        }
    }

    /// <summary>
    /// A conta fica em .claude.json: dentro da pasta de config (CLAUDE_CONFIG_DIR) ou ao lado dela (~/.claude.json).
    /// </summary>
    private static ClaudeAccount? ReadAccount(string rootPath)
    {
        var parent = Path.GetDirectoryName(rootPath.TrimEnd('\\', '/'));
        string[] candidates = parent == null
            ? [Path.Combine(rootPath, ".claude.json")]
            : [Path.Combine(rootPath, ".claude.json"), Path.Combine(parent, ".claude.json")];

        foreach (var file in candidates)
        {
            try
            {
                var info = new FileInfo(file);
                if (!info.Exists)
                    continue;
                if (AccountCache.TryGetValue(file, out var cached) && cached.Stamp == info.LastWriteTimeUtc)
                {
                    if (cached.Account != null)
                        return cached.Account;
                    continue;
                }

                ClaudeAccount? account = null;
                using (var doc = JsonDocument.Parse(File.ReadAllText(file)))
                {
                    if (doc.RootElement.Has("oauthAccount", out var oa) && oa.Str("emailAddress") is { Length: > 0 } email)
                    {
                        var org = oa.Str("organizationUuid") ?? "";
                        account = new ClaudeAccount($"{email.ToLowerInvariant()}|{org}", email,
                            oa.Str("displayName") ?? oa.Str("fullName"), oa.Str("organizationName"), oa.Str("organizationType"));
                    }
                }
                AccountCache[file] = (info.LastWriteTimeUtc, account);
                if (account != null)
                    return account;
            }
            catch
            {
                // Sendo reescrito; fica para a próxima.
            }
        }
        return null;
    }
}
