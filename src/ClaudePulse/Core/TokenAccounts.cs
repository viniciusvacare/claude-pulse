using System.Collections.Concurrent;

namespace ClaudePulse.Core;

/// <summary>
/// Descobre de qual conta é cada token perguntando para a própria API (/api/oauth/profile).
/// O .claude.json não é confiável para isso: o app desktop grava nele a conta dele, enquanto o
/// .credentials.json continua com o login da CLI, que pode ser de outra organização ou plano.
/// </summary>
public static class TokenAccounts
{
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(5);
    private static readonly ConcurrentDictionary<string, Lookup> ByToken = new();
    private static readonly ConcurrentDictionary<string, ClaudeAccount> ByRoot = new(StringComparer.OrdinalIgnoreCase);

    private sealed record Lookup(Task<ClaudeAccount?> Task, DateTime StartedAt);

    /// <summary>Troca a conta de cada login pela do token; sem resposta da API, mantém a do .claude.json.</summary>
    public static async Task<List<OAuthCredential>> ResolveAsync(List<OAuthCredential> credentials)
    {
        foreach (var token in ByToken.Keys)
        {
            if (credentials.All(c => c.AccessToken != token))
                ByToken.TryRemove(token, out _); // token já renovado ou deslogado
        }

        var accounts = await Task.WhenAll(credentials.Select(ResolveOneAsync));
        return credentials
            .Select((c, i) => accounts[i] is { } account
                ? c with { Account = account, AccountFromToken = true, FileAccount = c.Account }
                : c)
            .ToList();
    }

    private static async Task<ClaudeAccount?> ResolveOneAsync(OAuthCredential credential)
    {
        if (!credential.IsExpired)
        {
            var lookup = ByToken.AddOrUpdate(credential.AccessToken,
                token => new Lookup(FetchAsync(token), DateTime.UtcNow),
                (token, old) => old.Task.IsCompleted && old.Task.Result == null && DateTime.UtcNow - old.StartedAt > RetryAfterFailure
                    ? new Lookup(FetchAsync(token), DateTime.UtcNow)
                    : old);
            if (await lookup.Task is { } account)
            {
                ByRoot[credential.RootPath] = account;
                return account;
            }
        }
        // Token expirado (a API recusaria) ou consulta falhou: a última conta confirmada nesta pasta.
        return ByRoot.TryGetValue(credential.RootPath, out var known) ? known : null;
    }

    private static async Task<ClaudeAccount?> FetchAsync(string token)
    {
        try
        {
            var profile = await UsageApi.FetchProfileAsync(token);
            if (string.IsNullOrEmpty(profile.Email) || string.IsNullOrEmpty(profile.OrganizationUuid))
                return null;
            return new ClaudeAccount($"{profile.Email.ToLowerInvariant()}|{profile.OrganizationUuid}", profile.Email,
                profile.Name, profile.Organization, profile.OrganizationType);
        }
        catch
        {
            // Sem rede, 429 ou resposta inesperada: tenta de novo depois.
            return null;
        }
    }
}
