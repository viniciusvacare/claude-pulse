using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudePulse.Core;

public sealed class ModelUsage
{
    public int Responses { get; set; }
    public long Input { get; set; }
    public long Output { get; set; }
    public long CacheRead { get; set; }
    public long CacheWrite { get; set; }
    public double CostUsd { get; set; }

    [JsonIgnore]
    public long Total => Input + Output + CacheRead + CacheWrite;

    public void Add(ModelUsage o)
    {
        Responses += o.Responses;
        Input += o.Input;
        Output += o.Output;
        CacheRead += o.CacheRead;
        CacheWrite += o.CacheWrite;
        CostUsd += o.CostUsd;
    }
}

/// <summary>Totais de um dia (fuso local).</summary>
public sealed class DayRecord
{
    public int Responses { get; set; }
    public int Sessions { get; set; }
    public long Input { get; set; }
    public long Output { get; set; }
    public long CacheRead { get; set; }
    public long CacheWrite { get; set; }
    public double CostUsd { get; set; }

    /// <summary>Por modelo ("Opus 5.5").</summary>
    public Dictionary<string, ModelUsage> Models { get; set; } = [];

    /// <summary>Tokens totais por projeto (pasta onde a sessão começou).</summary>
    public Dictionary<string, long> Projects { get; set; } = [];

    /// <summary>Maior percentual visto no dia (só enquanto o app estava aberto).</summary>
    public double? Peak5h { get; set; }
    public double? PeakWeekly { get; set; }

    [JsonIgnore]
    public long Total => Input + Output + CacheRead + CacheWrite;

    [JsonIgnore]
    public long TotalInput => Input + CacheRead + CacheWrite;

    [JsonIgnore]
    public double CacheHitRate => TotalInput == 0 ? 0 : (double)CacheRead / TotalInput;

    public bool SameTotals(DayRecord other) =>
        Responses == other.Responses && Sessions == other.Sessions && Total == other.Total && Output == other.Output;

    /// <summary>Soma de vários dias/contas. Picos não são somáveis e ficam de fora.</summary>
    public static DayRecord Combine(IEnumerable<DayRecord> records)
    {
        var sum = new DayRecord();
        foreach (var r in records)
        {
            sum.Responses += r.Responses;
            sum.Sessions += r.Sessions;
            sum.Input += r.Input;
            sum.Output += r.Output;
            sum.CacheRead += r.CacheRead;
            sum.CacheWrite += r.CacheWrite;
            sum.CostUsd += r.CostUsd;
            foreach (var (name, usage) in r.Models)
            {
                if (!sum.Models.TryGetValue(name, out var m))
                    sum.Models[name] = m = new ModelUsage();
                m.Add(usage);
            }
            foreach (var (project, tokens) in r.Projects)
                sum.Projects[project] = sum.Projects.GetValueOrDefault(project) + tokens;
        }
        return sum;
    }
}

public sealed class AccountHistory
{
    /// <summary>Chave "yyyy-MM-dd".</summary>
    public SortedDictionary<string, DayRecord> Days { get; set; } = new(StringComparer.Ordinal);
}

public sealed class AccountLabel
{
    public string? Email { get; set; }
    public string? Organization { get; set; }
    public string? OrganizationType { get; set; }
    public string? Plan { get; set; }
}

/// <summary>A partir de <see cref="Since"/>, a pasta do Claude Code estava logada em <see cref="Account"/>.</summary>
public sealed class AccountSpan
{
    public DateTimeOffset Since { get; set; }
    public string Account { get; set; } = "";
}

/// <summary>
/// Histórico diário por conta, persistido em %APPDATA%\ClaudePulse\history.json.
/// O Claude Code apaga transcripts antigos (~30 dias por padrão); este arquivo guarda os totais para sempre.
/// Como os transcripts não dizem qual conta respondeu, o app mantém uma linha do tempo de logins por
/// pasta do Claude Code e atribui cada resposta à conta ativa naquela pasta no momento dela.
/// </summary>
public sealed class UsageHistory
{
    public const string UnknownAccount = "";

    public int Version { get; set; } = 2;

    public Dictionary<string, AccountHistory> Accounts { get; set; } = [];

    public Dictionary<string, AccountLabel> Labels { get; set; } = [];

    /// <summary>Por pasta do Claude Code (caminho em minúsculas): a conta do .claude.json, que é a do app desktop.</summary>
    public Dictionary<string, List<AccountSpan>> Timelines { get; set; } = [];

    /// <summary>
    /// Por pasta do Claude Code: a conta do token do .credentials.json (CLI, VS Code), confirmada pela API.
    /// Começa quando foi observada pela primeira vez; antes disso vale <see cref="Timelines"/>.
    /// </summary>
    public Dictionary<string, List<AccountSpan>> CliTimelines { get; set; } = [];

    /// <summary>Entrypoint gravado nos transcripts pelo Claude Code do app desktop, que usa o próprio login.</summary>
    private const string DesktopEntrypoint = "claude-desktop";

    /// <summary>Formato da versão 1 (antes das contas); migrado ao carregar.</summary>
    [JsonPropertyName("Days")]
    public SortedDictionary<string, DayRecord>? LegacyDays { get; set; }

    [JsonIgnore]
    public bool Dirty { get; private set; }

    /// <summary>Muda sempre que a linha do tempo de contas muda (força recálculo da atribuição).</summary>
    [JsonIgnore]
    public int TimelineVersion { get; private set; }

    // Dias a partir dos quais respostas mudaram de conta e o recálculo deve prevalecer sobre o guardado.
    private DateOnly? _reattributeFrom;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudePulse");

    private static string FilePath => Path.Combine(Folder, "history.json");

    public static UsageHistory Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var history = JsonSerializer.Deserialize<UsageHistory>(File.ReadAllText(FilePath), JsonOptions);
                if (history != null)
                {
                    history.Normalize();
                    return history;
                }
            }
        }
        catch
        {
            // Preserva o arquivo ilegível em vez de sobrescrevê-lo.
            try
            {
                File.Copy(FilePath, FilePath + ".corrompido-" + DateTime.Now.ToString("yyyyMMddHHmmss"), overwrite: true);
            }
            catch
            {
                // Sem o que fazer.
            }
        }
        return new UsageHistory();
    }

    private void Normalize()
    {
        foreach (var bucket in Accounts.Values)
            bucket.Days = new SortedDictionary<string, DayRecord>(bucket.Days, StringComparer.Ordinal);
        if (LegacyDays is { Count: > 0 } legacy)
        {
            // v1 → v2: fica como "conta desconhecida" até a primeira conta ser observada.
            Accounts.TryAdd(UnknownAccount, new AccountHistory { Days = new SortedDictionary<string, DayRecord>(legacy, StringComparer.Ordinal) });
            Dirty = true;
        }
        LegacyDays = null;
        Version = 2;
    }

    public void Save()
    {
        if (!Dirty)
            return;
        try
        {
            Directory.CreateDirectory(Folder);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(temp, FilePath, overwrite: true);
            Dirty = false;
        }
        catch
        {
            // Tenta de novo no próximo ciclo.
        }
    }

    public static string Key(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateOnly ParseKey(string key) => DateOnly.ParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    // ───────────────────────────── Contas ─────────────────────────────

    /// <summary>Contas com histórico, a mais recentemente usada primeiro.</summary>
    public IReadOnlyList<string> AccountKeys =>
        Accounts.Where(a => a.Value.Days.Values.Any(d => d.Responses > 0))
            .OrderByDescending(a => a.Value.Days.Keys.LastOrDefault() ?? "")
            .Select(a => a.Key)
            .ToList();

    public string AccountLabelText(string? key, bool withOrganization = false)
    {
        if (key == null)
            return "Todas as contas";
        if (key == UnknownAccount)
            return "Conta não identificada";
        if (!Labels.TryGetValue(key, out var label) || label.Email == null)
            return key.Split('|')[0];
        bool showOrg = withOrganization && label.Organization != null && label.OrganizationType is not (null or "claude_pro" or "claude_max" or "claude_free");
        return showOrg ? $"{label.Email} · {label.Organization}" : label.Email;
    }

    /// <summary>Registra a conta do .claude.json numa pasta do Claude Code. Chamado a cada varredura.</summary>
    public void ObserveAccount(string rootPath, string accountKey, DateTimeOffset loggedInAt, AccountLabel label)
    {
        UpdateLabel(accountKey, label);

        bool firstEver = Timelines.Count == 0;
        var root = rootPath.ToLowerInvariant();
        bool changed;
        if (!Timelines.TryGetValue(root, out var spans))
        {
            // Primeira vez que vemos esta pasta: tudo o que já existe nela fica com a conta atual.
            Timelines[root] = [new AccountSpan { Since = DateTimeOffset.MinValue, Account = accountKey }];
            changed = true;
        }
        else
        {
            changed = SwitchAccount(spans, accountKey, loggedInAt);
        }

        if (firstEver && Accounts.Remove(UnknownAccount, out var legacy))
        {
            // Histórico de antes das contas pertence à primeira conta observada.
            var bucket = Bucket(accountKey);
            foreach (var (key, record) in legacy.Days)
                bucket.Days.TryAdd(key, record);
            changed = true;
        }

        if (changed)
        {
            TimelineVersion++;
            Dirty = true;
        }
    }

    /// <summary>Registra a conta do token do .credentials.json (CLI, VS Code) numa pasta do Claude Code.</summary>
    public void ObserveCliAccount(string rootPath, string accountKey, DateTimeOffset loggedInAt, AccountLabel label)
    {
        UpdateLabel(accountKey, label);

        var root = rootPath.ToLowerInvariant();
        bool changed;
        if (!CliTimelines.TryGetValue(root, out var spans))
        {
            // Só vale daqui em diante: as respostas anteriores ficam com a conta que já tinham.
            CliTimelines[root] = [new AccountSpan { Since = DateTimeOffset.UtcNow, Account = accountKey }];
            changed = true;
        }
        else
        {
            changed = SwitchAccount(spans, accountKey, loggedInAt);
        }

        if (changed)
        {
            TimelineVersion++;
            Dirty = true;
        }
    }

    private void UpdateLabel(string accountKey, AccountLabel label)
    {
        if (!Labels.TryGetValue(accountKey, out var known)
            || known.Email != label.Email || known.Organization != label.Organization
            || known.OrganizationType != label.OrganizationType || (label.Plan != null && known.Plan != label.Plan))
        {
            label.Plan ??= known?.Plan;
            Labels[accountKey] = label;
            Dirty = true;
        }
    }

    /// <summary>Abre um trecho novo na linha do tempo se a conta mudou. Devolve true se mudou.</summary>
    private bool SwitchAccount(List<AccountSpan> spans, string accountKey, DateTimeOffset loggedInAt)
    {
        if (spans[^1].Account == accountKey)
            return false;
        // A hora do login é a última gravação do .credentials.json (nunca no futuro nem antes do trecho anterior).
        var since = loggedInAt > DateTimeOffset.UtcNow ? DateTimeOffset.UtcNow : loggedInAt;
        if (since <= spans[^1].Since)
            since = DateTimeOffset.UtcNow;
        spans.Add(new AccountSpan { Since = since, Account = accountKey });
        var day = DateOnly.FromDateTime(since.LocalDateTime);
        _reattributeFrom = _reattributeFrom is { } current && current < day ? current : day;
        return true;
    }

    /// <summary>
    /// Cópia imutável das linhas do tempo, para uso em outra thread: (pasta, momento, entrypoint) → conta.
    /// O app desktop responde com o login dele (o do .claude.json); CLI e VS Code, com o do .credentials.json.
    /// Respostas sem entrypoint (transcripts antigos) ficam com a conta do .claude.json.
    /// </summary>
    public Func<string, DateTimeOffset, string?, string> CreateResolver()
    {
        var desktop = Snapshot(Timelines);
        var cli = Snapshot(CliTimelines);
        return (rootPath, at, entrypoint) =>
        {
            var root = rootPath.ToLowerInvariant();
            if (entrypoint is not (null or DesktopEntrypoint) && cli.TryGetValue(root, out var cliSpans) && AccountAt(cliSpans, at) is { } account)
                return account;
            if (!desktop.TryGetValue(root, out var spans) || spans.Length == 0)
                return UnknownAccount;
            return AccountAt(spans, at) ?? spans[0].Account;
        };
    }

    private static Dictionary<string, (DateTimeOffset Since, string Account)[]> Snapshot(Dictionary<string, List<AccountSpan>> timelines) =>
        timelines.ToDictionary(t => t.Key, t => t.Value.Select(s => (s.Since, s.Account)).ToArray());

    private static string? AccountAt((DateTimeOffset Since, string Account)[] spans, DateTimeOffset at)
    {
        for (int i = spans.Length - 1; i >= 0; i--)
        {
            if (spans[i].Since <= at)
                return spans[i].Account;
        }
        return null;
    }

    private AccountHistory Bucket(string account)
    {
        if (!Accounts.TryGetValue(account, out var bucket))
            Accounts[account] = bucket = new AccountHistory();
        return bucket;
    }

    // ───────────────────────────── Dias ─────────────────────────────

    /// <param name="account">null = soma de todas as contas.</param>
    public DayRecord? Get(string? account, DateOnly day)
    {
        var key = Key(day);
        if (account != null)
            return Accounts.TryGetValue(account, out var bucket) ? bucket.Days.GetValueOrDefault(key) : null;
        var records = Accounts.Values.Select(b => b.Days.GetValueOrDefault(key)).OfType<DayRecord>().ToList();
        return records.Count == 0 ? null : records.Count == 1 ? records[0] : DayRecord.Combine(records);
    }

    public DateOnly? FirstDay(string? account)
    {
        var keys = (account == null ? Accounts.Values : Accounts.Where(a => a.Key == account).Select(a => a.Value))
            .Select(b => b.Days.Keys.FirstOrDefault())
            .OfType<string>()
            .ToList();
        return keys.Count == 0 ? null : ParseKey(keys.Min(StringComparer.Ordinal)!);
    }

    /// <summary>
    /// Incorpora os totais recalculados dos transcripts. Se um dia tem menos respostas do que o guardado,
    /// é porque transcripts foram apagados: mantém o registro antigo (exceto em dias que mudaram de conta).
    /// </summary>
    public void Merge(IReadOnlyDictionary<string, Dictionary<DateOnly, DayRecord>> computed)
    {
        var reattributeFrom = _reattributeFrom;
        _reattributeFrom = null;

        foreach (var (account, days) in computed)
        {
            var bucket = Bucket(account);
            foreach (var (day, record) in days)
            {
                var key = Key(day);
                if (bucket.Days.TryGetValue(key, out var stored))
                {
                    bool authoritative = reattributeFrom is { } from && day >= from;
                    if ((!authoritative && record.Responses < stored.Responses) || record.SameTotals(stored))
                        continue;
                    record.Peak5h = Max(stored.Peak5h, record.Peak5h);
                    record.PeakWeekly = Max(stored.PeakWeekly, record.PeakWeekly);
                }
                bucket.Days[key] = record;
                Dirty = true;
            }
        }

        if (reattributeFrom is not { } start)
            return;
        // Dias recentes cujas respostas foram todas para outra conta: zera o uso (mantém os picos).
        foreach (var (account, bucket) in Accounts)
        {
            computed.TryGetValue(account, out var days);
            foreach (var key in bucket.Days.Keys.ToList())
            {
                var day = ParseKey(key);
                var stored = bucket.Days[key];
                if (day < start || stored.Responses == 0 || (days != null && days.ContainsKey(day)))
                    continue;
                bucket.Days[key] = new DayRecord { Peak5h = stored.Peak5h, PeakWeekly = stored.PeakWeekly };
                Dirty = true;
            }
        }
    }

    public void RecordPeaks(string account, DateOnly day, double? fiveHour, double? weekly)
    {
        var bucket = Bucket(account);
        var key = Key(day);
        if (!bucket.Days.TryGetValue(key, out var record))
            bucket.Days[key] = record = new DayRecord();
        var new5h = Max(record.Peak5h, fiveHour);
        var newWeekly = Max(record.PeakWeekly, weekly);
        if (new5h != record.Peak5h || newWeekly != record.PeakWeekly)
        {
            record.Peak5h = new5h;
            record.PeakWeekly = newWeekly;
            Dirty = true;
        }
    }

    /// <summary>CSV de um mês (separador ";" e vírgula decimal, como o Excel em pt-BR espera).</summary>
    public string ToCsv(string? account, int year, int month)
    {
        var pt = CultureInfo.GetCultureInfo("pt-BR");
        var sb = new StringBuilder();
        sb.AppendLine("data;conta;respostas;sessoes;entrada;cache_lido;cache_escrito;saida;total;custo_api_usd;pico_sessao_5h;pico_semana;modelos;projetos");
        IReadOnlyList<string> accounts = account != null ? [account] : AccountKeys;
        int days = DateTime.DaysInMonth(year, month);
        for (int d = 1; d <= days; d++)
        {
            var date = new DateOnly(year, month, d);
            foreach (var acc in accounts)
            {
                var r = Get(acc, date);
                if (r == null && account == null)
                    continue; // na visão "todas", só linhas com dados
                r ??= new DayRecord();
                string models = string.Join(", ", r.Models.OrderByDescending(m => m.Value.Total).Select(m => $"{m.Key} {m.Value.Total}"));
                string projects = string.Join(", ", r.Projects.OrderByDescending(p => p.Value).Select(p => $"{p.Key} {p.Value}"));
                sb.Append(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(';')
                  .Append(Csv(AccountLabelText(acc, withOrganization: true))).Append(';')
                  .Append(r.Responses).Append(';')
                  .Append(r.Sessions).Append(';')
                  .Append(r.Input).Append(';')
                  .Append(r.CacheRead).Append(';')
                  .Append(r.CacheWrite).Append(';')
                  .Append(r.Output).Append(';')
                  .Append(r.Total).Append(';')
                  .Append(r.CostUsd.ToString("0.00", pt)).Append(';')
                  .Append(r.Peak5h?.ToString("0", pt)).Append(';')
                  .Append(r.PeakWeekly?.ToString("0", pt)).Append(';')
                  .Append(Csv(models)).Append(';')
                  .Append(Csv(projects))
                  .AppendLine();
            }
        }
        return sb.ToString();
    }

    private static string Csv(string s) => s.Contains(';') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    private static double? Max(double? a, double? b) => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);
}
