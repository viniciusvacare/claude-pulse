using System.Text;
using System.Text.Json;

namespace ClaudePulse.Core;

/// <summary>Estado de uma sessão do Claude Code, extraído do transcript .jsonl.</summary>
public sealed record SessionInfo(
    string SessionId,
    string Origin,
    string? Title,
    string? LastPrompt,
    string? Cwd,
    string? Branch,
    string? Model,
    long ContextTokens,
    DateTimeOffset LastActivity)
{
    public bool IsWslOrigin() => Origin.StartsWith("WSL", StringComparison.Ordinal);

    public string Project => ProjectName(Cwd);

    /// <summary>
    /// Comando para retomar a sessão. O --resume só acha a sessão na pasta do projeto, então entra nela antes:
    /// em PowerShell para sessões do Windows e em bash para as do WSL (o cwd é um caminho Linux).
    /// </summary>
    public string ResumeCommand()
    {
        var resume = $"claude --resume {SessionId}";
        if (string.IsNullOrEmpty(Cwd))
            return resume;
        var quoted = "'" + Cwd.Replace("'", IsWslOrigin() ? "'\\''" : "''") + "'";
        return IsWslOrigin() ? $"cd {quoted} && {resume}" : $"cd -LiteralPath {quoted}; {resume}";
    }

    public static string ProjectName(string? cwd) =>
        cwd?.TrimEnd('/', '\\').Split('/', '\\').LastOrDefault(s => s.Length > 0) ?? "sessão";

    public string DisplayTitle
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Title))
                return Title!;
            if (!string.IsNullOrWhiteSpace(LastPrompt))
            {
                var line = LastPrompt!.ReplaceLineEndings(" ").Trim();
                return line.Length > 60 ? line[..60] + "…" : line;
            }
            return Project;
        }
    }
}

/// <summary>
/// Lê os transcripts de ~/.claude/projects de forma incremental (só os bytes novos de cada arquivo),
/// então pode rodar a cada poucos segundos sem custo relevante. Na primeira passada lê tudo o que
/// existe, o que alimenta o histórico diário.
/// </summary>
public sealed class SessionTracker
{
    private sealed record Turn(string Root, DateTimeOffset At, string? Entrypoint, string? Model, string SessionKey, string Project,
        long Input, long Output, long CacheRead, long CacheWrite5m, long CacheWrite1h)
    {
        public long Total => Input + Output + CacheRead + CacheWrite5m + CacheWrite1h;

        public DateOnly Day => DateOnly.FromDateTime(At.LocalDateTime);
    }

    private sealed class FileState
    {
        public long Offset;
        public bool IsSubagent;
        public string SessionId = "";
        public string Origin = "";
        public string Root = "";
        public string? Title, LastPrompt, Cwd, Branch, Model;
        public long ContextTokens;
        public DateTimeOffset LastActivity;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, FileState> _files = new(StringComparer.OrdinalIgnoreCase);
    // Todas as respostas já vistas, por id da mensagem (o mesmo id pode aparecer em vários arquivos).
    private readonly Dictionary<string, Turn> _turns = new();
    private bool _turnsChanged;
    private int _resolverVersion = -1;

    /// <summary>
    /// Varre os transcripts. <c>Days</c> (conta → dia → totais) só vem preenchido quando algo mudou desde a
    /// última chamada: novas respostas ou uma nova versão da linha do tempo de contas.
    /// </summary>
    /// <param name="accountAt">(pasta do Claude Code, momento, entrypoint da resposta) → conta que estava logada.</param>
    public (List<SessionInfo> Sessions, Dictionary<string, Dictionary<DateOnly, DayRecord>>? Days) Scan(
        IEnumerable<ClaudeRoot> roots, Func<string, DateTimeOffset, string?, string> accountAt, int resolverVersion, int maxSessions = 4)
    {
        lock (_gate)
        {
            foreach (var root in roots)
            {
                var projects = Path.Combine(root.Path, "projects");
                IEnumerable<string> files;
                try
                {
                    if (!Directory.Exists(projects))
                        continue;
                    files = Directory.EnumerateFiles(projects, "*.jsonl", SearchOption.AllDirectories).ToList();
                }
                catch
                {
                    continue;
                }

                foreach (var file in files)
                {
                    try
                    {
                        var info = new FileInfo(file);
                        if (!_files.TryGetValue(file, out var state))
                        {
                            state = new FileState
                            {
                                Origin = root.Origin,
                                Root = root.Path,
                                SessionId = Path.GetFileNameWithoutExtension(file),
                                IsSubagent = !string.Equals(
                                    Path.GetDirectoryName(Path.GetDirectoryName(file)), projects, StringComparison.OrdinalIgnoreCase),
                            };
                            _files[file] = state;
                        }
                        if (info.Length != state.Offset)
                            ReadNewLines(file, state);
                    }
                    catch
                    {
                        // Arquivo em uso/removido: ignora neste ciclo.
                    }
                }
            }

            var sessions = _files.Values
                .Where(f => !f.IsSubagent && f.ContextTokens > 0 && f.LastActivity > DateTimeOffset.UtcNow.AddHours(-24))
                .OrderByDescending(f => f.LastActivity)
                .Take(maxSessions)
                .Select(f => new SessionInfo(f.SessionId, f.Origin, f.Title, f.LastPrompt, f.Cwd, f.Branch, f.Model, f.ContextTokens, f.LastActivity))
                .ToList();

            Dictionary<string, Dictionary<DateOnly, DayRecord>>? days = null;
            if (_turnsChanged || resolverVersion != _resolverVersion)
            {
                days = _turns.Values
                    .GroupBy(t => accountAt(t.Root, t.At, t.Entrypoint))
                    .ToDictionary(g => g.Key, g => BuildDays(g));
                _turnsChanged = false;
                _resolverVersion = resolverVersion;
            }
            return (sessions, days);
        }
    }

    private static Dictionary<DateOnly, DayRecord> BuildDays(IEnumerable<Turn> turns)
    {
        var days = new Dictionary<DateOnly, DayRecord>();
        var sessions = new Dictionary<DateOnly, HashSet<string>>();
        foreach (var t in turns)
        {
            if (!days.TryGetValue(t.Day, out var day))
            {
                days[t.Day] = day = new DayRecord();
                sessions[t.Day] = [];
            }
            double cost = Models.CostUsd(t.Model, t.Input, t.Output, t.CacheRead, t.CacheWrite5m, t.CacheWrite1h);
            long cacheWrite = t.CacheWrite5m + t.CacheWrite1h;

            day.Responses++;
            day.Input += t.Input;
            day.Output += t.Output;
            day.CacheRead += t.CacheRead;
            day.CacheWrite += cacheWrite;
            day.CostUsd += cost;
            sessions[t.Day].Add(t.SessionKey);

            var modelName = Models.DisplayName(t.Model);
            if (!day.Models.TryGetValue(modelName, out var model))
                day.Models[modelName] = model = new ModelUsage();
            model.Responses++;
            model.Input += t.Input;
            model.Output += t.Output;
            model.CacheRead += t.CacheRead;
            model.CacheWrite += cacheWrite;
            model.CostUsd += cost;

            day.Projects[t.Project] = day.Projects.GetValueOrDefault(t.Project) + t.Total;
        }
        foreach (var (date, ids) in sessions)
            days[date].Sessions = ids.Count;
        return days;
    }

    private void ReadNewLines(string file, FileState state)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length < state.Offset)
            state.Offset = 0; // Arquivo reescrito.
        stream.Seek(state.Offset, SeekOrigin.Begin);

        var buffer = new byte[1 << 20];
        var pending = new MemoryStream();
        long chunkStart = state.Offset;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            int lineStart = 0;
            int newline;
            while ((newline = Array.IndexOf(buffer, (byte)'\n', lineStart, read - lineStart)) >= 0)
            {
                string line;
                if (pending.Length > 0)
                {
                    pending.Write(buffer, lineStart, newline - lineStart);
                    line = Encoding.UTF8.GetString(pending.GetBuffer(), 0, (int)pending.Length);
                    pending.SetLength(0);
                }
                else
                {
                    line = Encoding.UTF8.GetString(buffer, lineStart, newline - lineStart);
                }

                ProcessLine(line, state);
                lineStart = newline + 1;
                state.Offset = chunkStart + lineStart;
            }
            pending.Write(buffer, lineStart, read - lineStart);
            chunkStart += read;
        }
        // Uma linha incompleta no fim (ainda sendo escrita) fica para o próximo ciclo.
    }

    private void ProcessLine(string line, FileState state)
    {
        bool isAssistant = line.Contains("\"type\":\"assistant\"", StringComparison.Ordinal);
        bool isTitle = !isAssistant && line.Contains("\"type\":\"ai-title\"", StringComparison.Ordinal);
        bool isSummary = !isAssistant && line.Contains("\"type\":\"summary\"", StringComparison.Ordinal);
        bool isPrompt = !isAssistant && line.Contains("\"type\":\"last-prompt\"", StringComparison.Ordinal);
        if (!(isAssistant || isTitle || isSummary || isPrompt))
            return;

        try
        {
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            if (isTitle)
            {
                state.Title = r.Str("aiTitle") ?? state.Title;
                return;
            }
            if (isSummary)
            {
                state.Title ??= r.Str("summary");
                return;
            }
            if (isPrompt)
            {
                state.LastPrompt = r.Str("lastPrompt") ?? state.LastPrompt;
                return;
            }
            ProcessAssistant(r, state);
        }
        catch (JsonException)
        {
            // Linha malformada: ignora.
        }
    }

    private void ProcessAssistant(JsonElement r, FileState state)
    {
        if (!r.Has("message", out var message) || !message.Has("usage", out var usage))
            return;
        var model = message.Str("model");
        if (model == null || model.StartsWith('<')) // "<synthetic>" = mensagens locais, sem uso real
            return;

        long input = usage.Long("input_tokens");
        long output = usage.Long("output_tokens");
        long cacheRead = usage.Long("cache_read_input_tokens");
        long cacheWrite = usage.Long("cache_creation_input_tokens");
        long cacheWrite1h = usage.Has("cache_creation", out var cc) ? cc.Long("ephemeral_1h_input_tokens") : 0;
        cacheWrite1h = Math.Min(cacheWrite1h, cacheWrite);
        var timestamp = r.Date("timestamp") ?? DateTimeOffset.UtcNow;
        state.Cwd ??= r.Str("cwd"); // pasta onde a sessão começou (o cwd muda com cd)

        if (!state.IsSubagent && !r.Bool("isSidechain"))
        {
            // O tamanho do contexto é tudo que entrou no último pedido + a resposta gerada.
            state.ContextTokens = input + cacheRead + cacheWrite + output;
            state.Model = model;
            state.Branch = r.Str("gitBranch") ?? state.Branch;
            state.LastActivity = timestamp;
            if (r.Str("sessionId") is { } sid)
                state.SessionId = sid;
        }

        // Uma resposta aparece em várias linhas (uma por bloco de conteúdo); a última traz o uso final.
        var id = message.Str("id") ?? r.Str("requestId") ?? r.Str("uuid");
        if (id == null)
            return;
        // "claude-desktop", "cli", "claude-vscode"…: diz qual login respondeu (o do app desktop ou o do .credentials.json).
        var turn = new Turn(state.Root, timestamp, r.Str("entrypoint"), model, r.Str("sessionId") ?? state.SessionId,
            SessionInfo.ProjectName(state.Cwd), input, output, cacheRead, cacheWrite - cacheWrite1h, cacheWrite1h);
        if (!_turns.TryGetValue(id, out var previous) || previous != turn)
        {
            _turns[id] = turn;
            _turnsChanged = true;
        }
    }
}
