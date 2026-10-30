using ClaudePulse.Core;
using ClaudePulse.UI;

namespace ClaudePulse;

/// <summary>Contexto principal: ícone da bandeja, timers de atualização, menu e notificações.</summary>
public sealed class TrayApp : ApplicationContext
{
    private readonly Settings _settings = Settings.Load();
    private readonly SessionTracker _tracker = new();
    private readonly UsageHistory _history = UsageHistory.Load();
    private HistoryForm? _historyForm;
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu;
    private readonly PopupForm _popup = new();
    private readonly System.Windows.Forms.Timer _apiTimer = new();
    private readonly System.Windows.Forms.Timer _localTimer = new() { Interval = 8000 };
    private readonly System.Windows.Forms.Timer _saveTimer = new() { Interval = 60_000 };

    private UsageSnapshot? _usage;
    private string? _usageAccount;
    private string? _usageError;
    private OAuthCredential? _credential;
    private List<OAuthCredential> _accounts = [];
    private ToolStripMenuItem _accountMenu = null!;
    private List<SessionInfo> _sessions = [];
    private bool _fetching, _scanning;
    private bool _loginRejected, _renewing;
    private string? _renewError;
    private DateTime _apiBlockedUntil = DateTime.MinValue;

    // Notificações já disparadas, para não repetir.
    private readonly Dictionary<string, int> _limitLevel = [];
    private readonly Dictionary<string, double> _lastPercent = [];
    private readonly HashSet<string> _contextWarned = [];

    public TrayApp(WaitHandle showSignal, WaitHandle historySignal, bool showOnStart, bool historyOnStart)
    {
        _menu = BuildMenu();
        _menu.Opening += (_, _) =>
        {
            _popup.MenuOpen = true;
            _checkRefreshers.ForEach(refresh => refresh());
            RebuildAccountMenu();
        };
        _menu.Closed += (_, _) =>
        {
            _popup.MenuOpen = false;
            if (_popup.Visible)
                _popup.Activate();
        };

        _tray = new NotifyIcon
        {
            Text = "Claude Pulse",
            Icon = IconRenderer.Render(null, null, _settings.IconStyle, Theme.ForLimit),
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                TogglePopup();
        };
        _tray.BalloonTipClicked += (_, _) => ShowPopup();

        _popup.Icon = IconRenderer.AppIcon();
        _popup.RefreshClicked += () => _ = RefreshAllAsync(force: true);
        _popup.MenuClicked += () => _menu.Show(Cursor.Position);
        _popup.HistoryClicked += () => ShowHistory();
        _popup.RenewClicked += () => _ = RenewLoginAsync();

        _apiTimer.Interval = Math.Max(60, _settings.RefreshSeconds) * 1000;
        _apiTimer.Tick += (_, _) => _ = RefreshUsageAsync(force: false);
        _localTimer.Tick += (_, _) => _ = ScanLocalAsync();
        _saveTimer.Tick += (_, _) => _history.Save();
        _apiTimer.Start();
        _localTimer.Start();
        _saveTimer.Start();

        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnThemeChanged;

        _ = _popup.Handle; // garante o handle para receber BeginInvoke de outras threads
        ThreadPool.RegisterWaitForSingleObject(showSignal, (_, _) => _popup.BeginInvoke(ShowPopup), null, Timeout.Infinite, executeOnlyOnce: false);
        ThreadPool.RegisterWaitForSingleObject(historySignal, (_, _) => _popup.BeginInvoke(() => ShowHistory()), null, Timeout.Infinite, executeOnlyOnce: false);

        _ = RefreshAllAsync(force: true);
        if (showOnStart)
            ShowPopup();
        if (historyOnStart)
            ShowHistory();
    }

    // ───────────────────────────── Atualização ─────────────────────────────

    private async Task RefreshAllAsync(bool force)
    {
        if (force)
            ClaudeHome.Invalidate();
        await Task.WhenAll(ScanLocalAsync(), RefreshUsageAsync(force));
    }

    private async Task RefreshUsageAsync(bool force)
    {
        if (_fetching || (!force && DateTime.UtcNow < _apiBlockedUntil))
            return;
        _fetching = true;
        PushState();
        try
        {
            bool includeWsl = _settings.IncludeWsl;
            _accounts = await TokenAccounts.ResolveAsync(await Task.Run(() => CredentialReader.FindAll(ClaudeHome.Discover(includeWsl))));
            _credential = CredentialReader.PickBest(_accounts, _settings.PinnedAccount);
            if (_credential == null)
            {
                _usageError = _settings.PinnedAccount is { } pinned
                    ? $"A conta fixada ({_history.AccountLabelText(pinned)}) não está logada no Claude Code. Faça /login nela ou volte para \"Automática\" no menu Conta."
                    : "Nenhum login encontrado. Abra o Claude Code e faça /login.";
                _usage = null;
                return;
            }
            if (_credential.IsExpired)
            {
                _usageError = _renewError ?? "O token do Claude Code expirou. Renove pela CLI para ver os limites.";
                return;
            }

            var token = _credential.AccessToken;
            if (_usage != null && _usageAccount != _credential.AccountKey)
                _usage = null; // trocou de conta: não mostra limites da anterior
            _usage = await UsageApi.FetchUsageAsync(token);
            _usageError = null;
            _renewError = null;
            _loginRejected = false;
            _usageAccount = _credential.AccountKey;
            _history.RecordPeaks(_credential.AccountKey, DateOnly.FromDateTime(DateTime.Today),
                _usage.Get("five_hour")?.Percent, _usage.Get("seven_day")?.Percent);
            CheckLimitNotifications();
        }
        catch (ApiException ex) when (ex.Status == 429)
        {
            var wait = ex.RetryAfter ?? TimeSpan.FromMinutes(5);
            _apiBlockedUntil = DateTime.UtcNow + wait;
            _usageError = $"Muitas consultas; nova tentativa em {Format.Duration(wait)}.";
        }
        catch (ApiException ex) when (ex.Status is 401 or 403)
        {
            _loginRejected = true;
            _usageError = _renewError ?? "Login recusado. Renove pela CLI do Claude Code.";
        }
        catch (ApiException ex)
        {
            _usageError = $"Serviço indisponível ({ex.Message}).";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _usageError = "Sem conexão com a Anthropic.";
        }
        finally
        {
            _fetching = false;
            PushState();
        }
    }

    /// <summary>Roda a CLI do Claude Code para ela renovar o login em uso; o app só relê o resultado.</summary>
    private async Task RenewLoginAsync()
    {
        if (_renewing || _credential is not { } credential)
            return;
        _renewing = true;
        _renewError = null;
        PushState();
        try
        {
            var result = await CliRenewer.RenewAsync(credential);
            _renewError = result.Error;
            if (result.Ok)
            {
                _loginRejected = false;
                _popup.Flash("Login renovado pela CLI do Claude Code");
            }
        }
        finally
        {
            _renewing = false;
        }
        await RefreshUsageAsync(force: true);
        PushState();
    }

    private async Task ScanLocalAsync()
    {
        if (_scanning)
            return;
        _scanning = true;
        try
        {
            bool includeWsl = _settings.IncludeWsl;
            var (roots, credentials) = await Task.Run(() =>
            {
                var found = ClaudeHome.Discover(includeWsl);
                return (found, CredentialReader.FindAll(found));
            });
            credentials = await TokenAccounts.ResolveAsync(credentials);
            _accounts = credentials;

            // As linhas do tempo de contas precisam estar em dia antes de atribuir as respostas.
            // O app desktop grava os transcripts na mesma pasta com o login dele (o do .claude.json), mesmo quando a
            // CLI está em outra conta; o entrypoint de cada resposta diz qual das duas contas vale.
            foreach (var c in credentials)
            {
                if ((c.FileAccount ?? c.Account) is { } account)
                {
                    _history.ObserveAccount(c.RootPath, account.Key, c.WrittenAt, new AccountLabel
                    {
                        Email = account.Email,
                        Organization = account.Organization,
                        OrganizationType = account.OrganizationType,
                        Plan = c.AccountMismatch ? null : Format.Plan(c.Plan), // o plano do token é da outra conta
                    });
                }
                if (c is { AccountFromToken: true, Account: { } tokenAccount })
                {
                    _history.ObserveCliAccount(c.RootPath, tokenAccount.Key, c.WrittenAt, new AccountLabel
                    {
                        Email = tokenAccount.Email,
                        Organization = tokenAccount.Organization,
                        OrganizationType = tokenAccount.OrganizationType,
                        Plan = Format.Plan(c.Plan),
                    });
                }
            }
            var accountAt = _history.CreateResolver();
            int version = _history.TimelineVersion;

            var (sessions, days) = await Task.Run(() => _tracker.Scan(roots, accountAt, version));
            _sessions = sessions;
            if (days != null)
                _history.Merge(days);
            CheckContextNotifications();
        }
        catch
        {
            // Próximo ciclo tenta de novo.
        }
        finally
        {
            _scanning = false;
            PushState();
        }
    }

    private void PushState()
    {
        _popup.SetState(new PanelState
        {
            AccountName = _credential?.Account?.Email,
            AccountWarning = MismatchWarning(_credential),
            Plan = Format.Plan(_credential?.Plan),
            Origin = _credential?.Origin,
            Usage = _usage,
            UsageError = _usageError,
            Loading = _fetching,
            CanRenew = _credential != null && (_credential.IsExpired || _loginRejected),
            Renewing = _renewing,
            Sessions = _sessions,
            Today = _history.Get(ActiveAccountKey, DateOnly.FromDateTime(DateTime.Today)),
            AccountPinned = _settings.PinnedAccount != null,
            ContextLimitOverride = _settings.ContextLimitOverride,
        });
        UpdateTrayIcon();
        _historyForm?.DataChanged();
    }

    /// <summary>Aviso quando o token é de outra conta que a do .claude.json (ex.: CLI logada no Pro, app desktop no Team).</summary>
    private static string? MismatchWarning(OAuthCredential? credential)
    {
        if (credential is not { AccountMismatch: true, Account: { } token, FileAccount: { } file })
            return null;
        string where = "do " + credential.Origin;
        return $"O login {where} é de {Format.Account(token)}, não de {Format.Account(file)} como diz o .claude.json. "
            + "Os limites são os desse login; rode /login na CLI para trocar.";
    }

    // ───────────────────────────── Ícone ─────────────────────────────

    private void UpdateTrayIcon()
    {
        var session = _usage?.Get("five_hour");
        var weekly = _usage?.Get("seven_day");
        var current = _sessions.FirstOrDefault();
        double? contextPct = current == null
            ? null
            : 100.0 * current.ContextTokens / Models.ContextLimit(current.Model, current.ContextTokens, _settings.ContextLimitOverride);

        (double? primary, double? secondary, Func<double, Color> color) = _settings.IconMetric switch
        {
            IconMetric.Weekly => (weekly?.Percent, session?.Percent, (Func<double, Color>)Theme.ForLimit),
            IconMetric.Context => (contextPct, session?.Percent, Theme.ForContext),
            _ => (session?.Percent, weekly?.Percent, Theme.ForLimit),
        };

        var old = _tray.Icon;
        _tray.Icon = IconRenderer.Render(primary, secondary, _settings.IconStyle, color);
        old?.Dispose();
        _tray.Text = BuildTooltip(session, weekly, current, contextPct);
    }

    private string BuildTooltip(UsageWindow? session, UsageWindow? weekly, SessionInfo? current, double? contextPct)
    {
        var lines = new List<string> { "Claude Pulse" };
        if (session != null)
            lines.Add($"Sessão 5h: {Format.Percent(session.Percent)}" + (session.ResetsAt is { } r ? $" · reinicia {Format.When(r)}" : ""));
        if (weekly != null)
            lines.Add($"Semana: {Format.Percent(weekly.Percent)}");
        if (current != null && contextPct != null)
            lines.Add($"Contexto: {contextPct:0}% ({Format.Tokens(current.ContextTokens)})");
        if (_usage == null && _usageError != null)
            lines.Add(_usageError);
        var text = string.Join("\n", lines);
        return text.Length <= 127 ? text : text[..127];
    }

    // ───────────────────────────── Notificações ─────────────────────────────

    private void CheckLimitNotifications()
    {
        if (_usage == null)
            return;
        foreach (var w in _usage.Windows)
        {
            int level = w.Percent >= 100 ? 100 : w.Percent >= 90 ? 90 : w.Percent >= 75 ? 75 : 0;
            bool known = _limitLevel.TryGetValue(w.Key, out var previous);
            _limitLevel[w.Key] = level;

            if (_lastPercent.TryGetValue(w.Key, out var lastPct) && lastPct >= 50 && w.Percent < lastPct - 40)
                Notify("Limite renovado", $"{w.Label}: a janela reiniciou e você está com {Format.Percent(w.Percent)} de uso.");
            else if (known && level > previous)
            {
                string reset = w.ResetsAt is { } r ? $" Reinicia em {Format.Duration(r - DateTimeOffset.Now)}." : "";
                Notify(level >= 100 ? "Limite atingido" : $"{level}% do limite usado",
                    $"{w.Label}: {Format.Percent(w.Percent)}.{reset}", level >= 90 ? ToolTipIcon.Warning : ToolTipIcon.Info);
            }
            _lastPercent[w.Key] = w.Percent;
        }
    }

    private void CheckContextNotifications()
    {
        foreach (var s in _sessions)
        {
            long limit = Models.ContextLimit(s.Model, s.ContextTokens, _settings.ContextLimitOverride);
            double pct = 100.0 * s.ContextTokens / limit;
            if (pct >= 80 && DateTimeOffset.Now - s.LastActivity < TimeSpan.FromMinutes(10) && _contextWarned.Add(s.SessionId))
            {
                Notify("Contexto quase cheio",
                    $"\"{s.DisplayTitle}\" está em {pct:0}% ({Format.Tokens(s.ContextTokens)}/{Format.Tokens(limit)}). Considere /compact ou /clear.",
                    ToolTipIcon.Warning);
            }
            else if (pct < 60)
            {
                _contextWarned.Remove(s.SessionId); // Depois de compactar, pode avisar de novo.
            }
        }
    }

    private void Notify(string title, string text, ToolTipIcon icon = ToolTipIcon.Info)
    {
        if (_settings.Notifications)
            _tray.ShowBalloonTip(5000, title, text, icon);
    }

    // ───────────────────────────── Painel ─────────────────────────────

    private void TogglePopup()
    {
        if (_popup.Visible)
            _popup.HidePanel();
        else if ((DateTime.Now - _popup.HiddenAt).TotalMilliseconds > 300) // o clique que fechou o painel não reabre
            ShowPopup();
    }

    private void ShowPopup()
    {
        PushState();
        _popup.ShowNearTray();
        if (_usage == null || DateTimeOffset.Now - _usage.FetchedAt > TimeSpan.FromSeconds(45))
            _ = RefreshUsageAsync(force: false);
        _ = ScanLocalAsync();
    }

    private void ShowHistory(DateOnly? day = null)
    {
        if (_historyForm == null || _historyForm.IsDisposed)
        {
            _historyForm = new HistoryForm(_history, () => ActiveAccountKey, day);
            _historyForm.FormClosed += (_, _) =>
            {
                _historyForm = null;
                _history.Save();
            };
            _historyForm.Show();
        }
        else
        {
            if (day is { } d)
                _historyForm.SelectDay(d);
            if (_historyForm.WindowState == FormWindowState.Minimized)
                _historyForm.WindowState = FormWindowState.Normal;
        }
        _historyForm.Activate();
    }

    /// <summary>Conta em uso pelo painel (null antes do primeiro login encontrado).</summary>
    private string? ActiveAccountKey => _credential?.AccountKey;

    private void OnThemeChanged(object? sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (e.Category == Microsoft.Win32.UserPreferenceCategory.General)
            PushState();
    }

    // ───────────────────────────── Menu ─────────────────────────────

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = true };

        menu.Items.Add("Abrir painel", null, (_, _) => ShowPopup()).Font = new Font(menu.Font, FontStyle.Bold);
        menu.Items.Add("Histórico de uso…", null, (_, _) => ShowHistory());
        menu.Items.Add("Atualizar agora", null, (_, _) => _ = RefreshAllAsync(force: true));
        menu.Items.Add(new ToolStripSeparator());

        _accountMenu = new ToolStripMenuItem("Conta");
        _accountMenu.DropDownItems.Add("(procurando logins…)").Enabled = false;
        menu.Items.Add(_accountMenu);
        menu.Items.Add(Submenu("O ícone mostra",
            Choice("Sessão atual (5h)", () => _settings.IconMetric == IconMetric.Session, () => _settings.IconMetric = IconMetric.Session),
            Choice("Limite semanal", () => _settings.IconMetric == IconMetric.Weekly, () => _settings.IconMetric = IconMetric.Weekly),
            Choice("Contexto da sessão mais recente", () => _settings.IconMetric == IconMetric.Context, () => _settings.IconMetric = IconMetric.Context)));
        menu.Items.Add(Submenu("Estilo do ícone",
            Choice("Número colorido", () => _settings.IconStyle == IconStyle.Number, () => _settings.IconStyle = IconStyle.Number),
            Choice("Duas barras (principal + secundária)", () => _settings.IconStyle == IconStyle.Bars, () => _settings.IconStyle = IconStyle.Bars)));
        menu.Items.Add(Submenu("Consultar limites a cada",
            Choice("1 minuto", () => _settings.RefreshSeconds == 60, () => SetInterval(60)),
            Choice("2 minutos", () => _settings.RefreshSeconds == 120, () => SetInterval(120)),
            Choice("5 minutos", () => _settings.RefreshSeconds == 300, () => SetInterval(300))));
        menu.Items.Add(Submenu("Janela de contexto",
            Choice("Automática (pelo modelo)", () => _settings.ContextLimitOverride == 0, () => _settings.ContextLimitOverride = 0),
            Choice("200k tokens", () => _settings.ContextLimitOverride == Models.Context200K, () => _settings.ContextLimitOverride = Models.Context200K),
            Choice("1M tokens", () => _settings.ContextLimitOverride == Models.Context1M, () => _settings.ContextLimitOverride = Models.Context1M)));
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(Toggle("Notificações", () => _settings.Notifications, v => _settings.Notifications = v));
        menu.Items.Add(Toggle("Incluir Claude Code do WSL", () => _settings.IncludeWsl, v =>
        {
            _settings.IncludeWsl = v;
            ClaudeHome.Invalidate();
            _ = RefreshAllAsync(force: true);
        }));
        menu.Items.Add(Toggle("Iniciar com o Windows", () => Autostart.IsEnabled, v =>
        {
            try
            {
                Autostart.Set(v);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Não foi possível alterar a inicialização: " + ex.Message, "Claude Pulse");
            }
        }));
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add("Ver uso no claude.ai", null, (_, _) => Open("https://claude.ai/settings/usage"));
        menu.Items.Add("Sair", null, (_, _) => ExitThread());
        return menu;
    }

    /// <summary>Lista os logins encontrados (Windows e WSL), agrupados por conta.</summary>
    private void RebuildAccountMenu()
    {
        var items = _accountMenu.DropDownItems;
        items.Clear();

        var auto = new ToolStripMenuItem("Automática (login mais recente)") { Checked = _settings.PinnedAccount == null };
        auto.Click += (_, _) => PinAccount(null);
        items.Add(auto);
        items.Add(new ToolStripSeparator());

        var groups = _accounts.GroupBy(c => c.AccountKey).ToList();
        foreach (var group in groups)
        {
            var first = group.First();
            string name = first.Account is { } a ? _history.AccountLabelText(a.Key, withOrganization: true) : "Conta não identificada";
            string plan = Format.Plan(group.Select(c => c.Plan).FirstOrDefault(p => p != null));
            string where = string.Join(", ", group.Select(c => c.Origin).Distinct());
            bool allExpired = group.All(c => c.IsExpired);
            bool mismatch = group.Any(c => c.AccountMismatch);
            string text = $"{name}  —  {(plan.Length > 0 ? plan + " · " : "")}{where}{(allExpired ? " · token expirado" : "")}"
                + (mismatch ? " · ⚠ difere do .claude.json" : "");

            var item = new ToolStripMenuItem(text) { Checked = _settings.PinnedAccount == group.Key };
            if (_settings.PinnedAccount == null && _credential?.AccountKey == group.Key)
                item.Font = new Font(item.Font, FontStyle.Bold); // a que está em uso agora
            item.Click += (_, _) => PinAccount(group.Key);
            items.Add(item);
        }

        if (_settings.PinnedAccount is { } pinned && groups.All(g => g.Key != pinned))
            items.Add(new ToolStripMenuItem($"{_history.AccountLabelText(pinned)} (não está logada)") { Checked = true, Enabled = false });
        if (groups.Count == 0)
            items.Add(new ToolStripMenuItem("Nenhum login do Claude Code encontrado") { Enabled = false });
    }

    private void PinAccount(string? accountKey)
    {
        _settings.PinnedAccount = accountKey;
        _settings.Save();
        _usage = null;
        _ = RefreshUsageAsync(force: true);
        PushState();
    }

    private void SetInterval(int seconds)
    {
        _settings.RefreshSeconds = seconds;
        _apiTimer.Interval = seconds * 1000;
    }

    private static ToolStripMenuItem Submenu(string text, params ToolStripItem[] items)
    {
        var item = new ToolStripMenuItem(text);
        item.DropDownItems.AddRange(items);
        return item;
    }

    private ToolStripMenuItem Choice(string text, Func<bool> isChecked, Action select)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += (_, _) =>
        {
            select();
            _settings.Save();
            PushState();
        };
        _checkRefreshers.Add(() => item.Checked = isChecked());
        return item;
    }

    private ToolStripMenuItem Toggle(string text, Func<bool> get, Action<bool> set)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += (_, _) =>
        {
            set(!get());
            _settings.Save();
            PushState();
        };
        _checkRefreshers.Add(() => item.Checked = get());
        return item;
    }

    // Atualiza os checks toda vez que o menu abre.
    private readonly List<Action> _checkRefreshers = [];

    private static void Open(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // Sem navegador padrão.
        }
    }

    protected override void ExitThreadCore()
    {
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnThemeChanged;
        _apiTimer.Stop();
        _localTimer.Stop();
        _saveTimer.Stop();
        _history.Save();
        _historyForm?.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _popup.Dispose();
        _menu.Dispose();
        base.ExitThreadCore();
    }
}
