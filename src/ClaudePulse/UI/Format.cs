using System.Globalization;

namespace ClaudePulse.UI;

public static class Format
{
    public static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    public static string Tokens(long n) => n switch
    {
        >= 1_000_000 => (n / 1_000_000d).ToString(n >= 10_000_000 ? "0" : "0.#", PtBr) + "M",
        >= 1_000 => (n / 1_000d).ToString(n >= 100_000 ? "0" : "0.#", PtBr) + "k",
        _ => n.ToString(PtBr),
    };

    public static string Percent(double p) => $"{Math.Round(p):0}%";

    public static string Duration(TimeSpan t)
    {
        if (t <= TimeSpan.Zero)
            return "agora";
        if (t.TotalDays >= 1)
            return $"{(int)t.TotalDays}d {t.Hours}h";
        if (t.TotalHours >= 1)
            return $"{(int)t.TotalHours}h {t.Minutes:00}min";
        if (t.TotalMinutes >= 1)
            return $"{(int)t.TotalMinutes}min";
        return "<1min";
    }

    /// <summary>Hora local; inclui o dia da semana quando não é hoje.</summary>
    public static string When(DateTimeOffset at)
    {
        var local = at.ToLocalTime();
        // A API devolve horários como 02:59:59.9; arredonda para o minuto mais próximo.
        local = new DateTimeOffset((local.Ticks + TimeSpan.TicksPerMinute / 2) / TimeSpan.TicksPerMinute * TimeSpan.TicksPerMinute, local.Offset);
        if (local.Date == DateTime.Today)
            return local.ToString("HH:mm", PtBr);
        if (local.Date == DateTime.Today.AddDays(1))
            return "amanhã " + local.ToString("HH:mm", PtBr);
        return local.ToString("ddd HH:mm", PtBr);
    }

    public static string Ago(DateTimeOffset at)
    {
        var t = DateTimeOffset.Now - at;
        if (t.TotalSeconds < 10)
            return "agora";
        if (t.TotalMinutes < 1)
            return $"há {(int)t.TotalSeconds}s";
        if (t.TotalHours < 1)
            return $"há {(int)t.TotalMinutes} min";
        if (t.TotalDays < 1)
            return $"há {(int)t.TotalHours} h";
        return $"há {(int)t.TotalDays} d";
    }

    public static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpper(s[0], PtBr) + s[1..];

    public static string Usd(double v) => "US$ " + v.ToString(v >= 100 ? "N0" : "N2", PtBr);

    public static string Plan(string? plan) => plan?.ToLowerInvariant() switch
    {
        null or "" => "",
        "pro" => "Pro",
        "max" => "Max",
        "team" => "Team",
        "enterprise" => "Enterprise",
        var p => char.ToUpperInvariant(p[0]) + p[1..],
    };

    /// <summary>"Minha Empresa (Team)" ou, em conta pessoal, "fulano@gmail.com (Pro)".</summary>
    public static string Account(Core.ClaudeAccount account)
    {
        string type = Plan(account.OrganizationType?.Replace("claude_", ""));
        bool personal = account.OrganizationType is null or "claude_pro" or "claude_max" or "claude_free";
        string name = personal ? account.Email : account.Organization ?? account.Email;
        return type.Length > 0 ? $"{name} ({type})" : name;
    }
}
