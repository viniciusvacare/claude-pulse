namespace ClaudePulse.Core;

/// <summary>Preços de API (US$ por milhão de tokens) e janelas de contexto, para estimativas.</summary>
public static class Models
{
    private sealed record Rate(double Input, double Output, double CacheRead);

    // Ordem importa: chaves mais específicas primeiro.
    private static readonly (string Key, Rate Rate)[] Rates =
    [
        ("fable-5-1", new(10, 50, 0.25)),
        ("mythos-5-1", new(10, 50, 0.25)),
        ("fable", new(10, 50, 1.0)),
        ("mythos", new(10, 50, 1.0)),
        ("opus-5-5", new(4, 20, 0.20)),
        ("opus-5", new(5, 25, 0.50)),
        ("opus-4-8", new(5, 25, 0.50)),
        ("opus-4-7", new(5, 25, 0.50)),
        ("opus-4-6", new(5, 25, 0.50)),
        ("opus-4-5", new(5, 25, 0.50)),
        ("opus", new(15, 75, 1.50)),
        ("sonnet-5", new(2, 10, 0.20)),
        ("sonnet", new(3, 15, 0.30)),
        ("haiku-5", new(0.10, 0.50, 0.01)),
        ("haiku-4-5", new(1, 5, 0.10)),
        ("haiku", new(0.80, 4, 0.08)),
    ];

    private static readonly string[] MillionTokenContext =
        ["fable", "mythos", "opus-5", "opus-4-8", "opus-4-7", "opus-4-6", "sonnet-5", "sonnet-4-6", "haiku-5"];

    public const long Context200K = 200_000;
    public const long Context1M = 1_000_000;

    /// <summary>Custo equivalente na API. Escrita de cache: 1,25× (5 min) ou 2× (1 h) o preço de entrada.</summary>
    public static double CostUsd(string? model, long input, long output, long cacheRead, long cacheWrite5m, long cacheWrite1h)
    {
        var rate = RateFor(model);
        return (input * rate.Input
              + cacheWrite5m * rate.Input * 1.25
              + cacheWrite1h * rate.Input * 2
              + cacheRead * rate.CacheRead
              + output * rate.Output) / 1_000_000d;
    }

    public static long ContextLimit(string? model, long used, long overrideLimit)
    {
        if (overrideLimit > 0)
            return Math.Max(overrideLimit, used);
        var m = model?.ToLowerInvariant() ?? "";
        var limit = MillionTokenContext.Any(m.Contains) ? Context1M : Context200K;
        return used > limit ? Context1M : limit;
    }

    /// <summary>"claude-opus-5-5" → "Opus 5.5"; "claude-sonnet-4-5-20250929" → "Sonnet 4.5".</summary>
    public static string DisplayName(string? model)
    {
        if (string.IsNullOrEmpty(model))
            return "—";
        var parts = model.Replace("claude-", "", StringComparison.OrdinalIgnoreCase)
            .Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !(p.Length == 8 && p.All(char.IsDigit)))
            .ToList();
        var family = parts.FirstOrDefault(p => !p.All(char.IsDigit));
        if (family == null)
            return model;
        var version = string.Join('.', parts.Where(p => p.All(char.IsDigit)));
        var name = char.ToUpperInvariant(family[0]) + family[1..];
        return version.Length > 0 ? $"{name} {version}" : name;
    }

    private static Rate RateFor(string? model)
    {
        var m = model?.ToLowerInvariant() ?? "";
        foreach (var (key, rate) in Rates)
        {
            if (m.Contains(key))
                return rate;
        }
        return new Rate(3, 15, 0.30);
    }
}
