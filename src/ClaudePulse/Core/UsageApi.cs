using System.Net.Http.Headers;
using System.Text.Json;

namespace ClaudePulse.Core;

/// <summary>Uma janela de limite do plano (ex.: sessão de 5h, semana).</summary>
public sealed record UsageWindow(string Key, string Label, double Percent, DateTimeOffset? ResetsAt, TimeSpan Length)
{
    /// <summary>Fração da janela que já passou (0..1), ou null se não dá para saber.</summary>
    public double? Elapsed(DateTimeOffset now)
    {
        if (ResetsAt is not { } reset || Length <= TimeSpan.Zero)
            return null;
        var start = reset - Length;
        return Math.Clamp((now - start) / Length, 0, 1);
    }

    /// <summary>Quando o limite esgota mantendo o ritmo atual (null se não esgota antes do reset).</summary>
    public DateTimeOffset? ExhaustsAt(DateTimeOffset now)
    {
        if (ResetsAt is not { } reset || Elapsed(now) is not { } elapsed || elapsed < 0.03 || Percent <= 0.5 || Percent >= 100)
            return null;
        var start = reset - Length;
        var hitsFull = start + (now - start) * (100 / Percent);
        return hitsFull < reset ? hitsFull : null;
    }

    /// <summary>Percentual estimado no momento do reset mantendo o ritmo atual.</summary>
    public double? ProjectedAtReset(DateTimeOffset now) =>
        Elapsed(now) is { } elapsed && elapsed >= 0.03 ? Percent / elapsed : null;
}

public sealed record BreakdownItem(string Name, double Percent);

public sealed class UsageSnapshot
{
    public List<UsageWindow> Windows { get; } = [];
    public List<BreakdownItem> WeeklyBreakdown { get; } = [];
    public DateTimeOffset FetchedAt { get; init; } = DateTimeOffset.Now;

    public UsageWindow? Get(string key) => Windows.FirstOrDefault(w => w.Key == key);
}

public sealed record Profile(string? Name, string? Email, string? Organization, string? OrganizationUuid, string? OrganizationType);

public sealed class ApiException(int status, TimeSpan? retryAfter) : Exception($"HTTP {status}")
{
    public int Status { get; } = status;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>
/// Os mesmos endpoints que o Claude Code usa no /usage, autenticados com o token OAuth da conta logada.
/// </summary>
public static class UsageApi
{
    private static readonly HttpClient Http = new()
    {
        BaseAddress = new Uri("https://api.anthropic.com/"),
        Timeout = TimeSpan.FromSeconds(20),
    };

    private static readonly (string Key, string Label, TimeSpan Length)[] KnownWindows =
    [
        ("five_hour", "Sessão atual · 5h", TimeSpan.FromHours(5)),
        ("seven_day", "Semana · todos os modelos", TimeSpan.FromDays(7)),
        ("seven_day_opus", "Semana · Opus", TimeSpan.FromDays(7)),
        ("seven_day_sonnet", "Semana · Sonnet", TimeSpan.FromDays(7)),
        ("seven_day_cowork", "Semana · Cowork", TimeSpan.FromDays(7)),
    ];

    public static async Task<UsageSnapshot> FetchUsageAsync(string token, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync("api/oauth/usage", token, ct);
        var root = doc.RootElement;
        var snapshot = new UsageSnapshot();

        foreach (var (key, label, length) in KnownWindows)
        {
            if (!root.Has(key, out var w) || w.Double("utilization") is not { } pct)
                continue;
            snapshot.Windows.Add(new UsageWindow(key, label, pct, w.Date("resets_at"), length));
        }

        if (root.Has("extra_usage", out var extra) && extra.Bool("is_enabled") && extra.Double("utilization") is { } extraPct)
            snapshot.Windows.Add(new UsageWindow("extra_usage", "Créditos extras · mês", extraPct, null, TimeSpan.Zero));

        if (root.Has("seven_day_breakdown", out var breakdown) && breakdown.Has("rows", out var rows) && rows.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in rows.EnumerateArray())
            {
                var pct = row.Double("percent") ?? 0;
                if (pct > 0)
                    snapshot.WeeklyBreakdown.Add(new BreakdownItem(row.Str("display_name") ?? row.Str("key") ?? "?", pct));
            }
        }

        return snapshot;
    }

    public static async Task<Profile> FetchProfileAsync(string token, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync("api/oauth/profile", token, ct);
        var root = doc.RootElement;
        root.Has("account", out var account);
        root.Has("organization", out var org);
        var name = account.Str("display_name") ?? account.Str("full_name");
        return new Profile(name, account.Str("email") ?? account.Str("email_address"), org.Str("name"),
            org.Str("uuid"), org.Str("organization_type"));
    }

    private static async Task<JsonDocument> GetJsonAsync(string path, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
        request.Headers.UserAgent.ParseAdd("ClaudePulse/1.0");

        using var response = await Http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new ApiException((int)response.StatusCode, response.Headers.RetryAfter?.Delta);

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }
}
