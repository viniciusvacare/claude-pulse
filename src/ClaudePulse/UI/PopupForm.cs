using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using ClaudePulse.Core;

namespace ClaudePulse.UI;

/// <summary>Tudo que o painel precisa para se desenhar.</summary>
public sealed class PanelState
{
    public string? AccountName { get; init; }
    /// <summary>O token em uso é de outra conta que a do .claude.json.</summary>
    public string? AccountWarning { get; init; }
    public string? Plan { get; init; }
    public string? Origin { get; init; }
    public UsageSnapshot? Usage { get; init; }
    public string? UsageError { get; init; }
    public bool Loading { get; init; }
    /// <summary>Login expirado ou recusado: o painel oferece renovar pela CLI.</summary>
    public bool CanRenew { get; init; }
    public bool Renewing { get; init; }
    public IReadOnlyList<SessionInfo> Sessions { get; init; } = [];
    public DayRecord? Today { get; init; }
    public long ContextLimitOverride { get; init; }
    public bool AccountPinned { get; init; }
}

/// <summary>
/// Painel flutuante no estilo dos flyouts do Windows 11, desenhado à mão (GDI+) para ficar leve.
/// Todas as medidas são em pixels lógicos (96 DPI) e escaladas no OnPaint.
/// </summary>
public sealed class PopupForm : Form
{
    private const float W = 372;
    private const float Pad = 14;
    private const string UsageUrl = "https://claude.ai/settings/usage";

    public event Action? RefreshClicked;
    public event Action? MenuClicked;
    public event Action? HistoryClicked;
    public event Action? RenewClicked;

    private readonly List<(RectangleF Rect, string Id, Action Action)> _hits = [];
    private readonly System.Windows.Forms.Timer _clock = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer _fade = new() { Interval = 15 };
    private PanelState _state = new() { Loading = true };
    private Palette _p = Palette.Current();
    private float _scale = 1;
    private string? _hoverId;
    private string? _flash;
    private DateTime _flashUntil;
    private DateTime _hiddenAt;
    private float _slide;

    private readonly string _iconFamily;
    private Font _fTitle = null!, _fBody = null!, _fBodyBold = null!, _fSmall = null!, _fCaption = null!, _fBig = null!, _fStat = null!, _fIcon = null!;

    public bool MenuOpen { get; set; }

    public PopupForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        KeyPreview = true;
        Text = "Claude Pulse";
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);

        _iconFamily = FontFamily.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
        CreateFonts();

        _clock.Tick += (_, _) =>
        {
            if (Visible)
                Invalidate();
        };
        _fade.Tick += (_, _) => StepFade();
    }

    /// <summary>Hora em que o painel foi escondido (para o clique no ícone não reabrir na hora).</summary>
    public DateTime HiddenAt => _hiddenAt;

    public void SetState(PanelState state)
    {
        _state = state;
        if (Visible)
            Relayout();
    }

    public void Flash(string message)
    {
        _flash = message;
        _flashUntil = DateTime.Now.AddSeconds(3);
        Invalidate();
    }

    public void ShowNearTray()
    {
        _p = Palette.Current();
        var anchor = Cursor.Position;
        _scale = DpiAt(anchor) / 96f;
        CreateFonts();
        BackColor = _p.Background;
        Relayout(reposition: false);
        PlaceNear(anchor);

        Opacity = 0;
        _slide = 10;
        Show();
        Activate();
        _clock.Start();
        _fade.Start();
    }

    public void HidePanel()
    {
        if (!Visible)
            return;
        _clock.Stop();
        _fade.Stop();
        Hide();
        _hiddenAt = DateTime.Now;
    }

    // ───────────────────────────── Janela ─────────────────────────────

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80; // WS_EX_TOOLWINDOW: fora do Alt+Tab
            cp.ClassStyle |= 0x20000; // CS_DROPSHADOW
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (!MenuOpen)
            HidePanel();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape)
            HidePanel();
        else if (e.KeyCode == Keys.F5)
            RefreshClicked?.Invoke();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        e.Cancel = true;
        _scale = e.DeviceDpiNew / 96f;
        CreateFonts();
        Relayout();
    }

    private void StepFade()
    {
        Opacity = Math.Min(1, Opacity + 0.16);
        _slide = Math.Max(0, _slide - 2);
        Invalidate();
        if (Opacity >= 1 && _slide <= 0)
            _fade.Stop();
    }

    private void Relayout(bool reposition = true)
    {
        using var bmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bmp);
        float height = Render(g, measureOnly: true);
        var size = new Size((int)Math.Ceiling(W * _scale), (int)Math.Ceiling(height * _scale));
        if (Size != size)
        {
            var bottomRight = new Point(Right, Bottom);
            Size = size;
            if (reposition && Visible)
                Location = new Point(bottomRight.X - Width, bottomRight.Y - Height);
            if (reposition && Visible)
                ClampToScreen();
        }
        Invalidate();
    }

    private void PlaceNear(Point anchor)
    {
        var screen = Screen.FromPoint(anchor);
        var wa = screen.WorkingArea;
        int margin = (int)(12 * _scale);
        bool taskbarTop = wa.Top > screen.Bounds.Top;
        bool taskbarLeft = wa.Left > screen.Bounds.Left;
        int x = taskbarLeft ? wa.Left + margin : wa.Right - Width - margin;
        int y = taskbarTop ? wa.Top + margin : wa.Bottom - Height - margin;
        Location = new Point(x, y);
    }

    private void ClampToScreen()
    {
        var wa = Screen.FromControl(this).WorkingArea;
        int margin = (int)(12 * _scale);
        Location = new Point(
            Math.Clamp(Left, wa.Left + margin, Math.Max(wa.Left + margin, wa.Right - Width - margin)),
            Math.Clamp(Top, wa.Top + margin, Math.Max(wa.Top + margin, wa.Bottom - Height - margin)));
    }

    // ───────────────────────────── Mouse ─────────────────────────────

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hit = HitTest(e.Location);
        var id = hit?.Id;
        Cursor = hit != null ? Cursors.Hand : Cursors.Default;
        if (id != _hoverId)
        {
            _hoverId = id;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverId != null)
        {
            _hoverId = null;
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left)
            HitTest(e.Location)?.Action();
    }

    private (RectangleF Rect, string Id, Action Action)? HitTest(Point p)
    {
        var logical = new PointF(p.X / _scale, p.Y / _scale - _slide);
        foreach (var hit in _hits)
        {
            if (hit.Rect.Contains(logical))
                return hit;
        }
        return null;
    }

    // ───────────────────────────── Desenho ─────────────────────────────

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(_p.Background);
        g.ScaleTransform(_scale, _scale);
        g.TranslateTransform(0, _slide);
        Render(g, measureOnly: false);
    }

    /// <summary>Desenha o painel e devolve a altura total em pixels lógicos.</summary>
    private float Render(Graphics g, bool measureOnly)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        _hits.Clear();
        var now = DateTimeOffset.Now;

        float y = Pad;
        y = DrawHeader(g, y);
        y += 12;

        y = DrawCaption(g, y, "LIMITES DO PLANO");
        y = DrawLimits(g, y, now);
        y += 14;

        y = DrawCaption(g, y, "JANELA DE CONTEXTO · CLAUDE CODE");
        y = DrawSessions(g, y, now);
        y += 14;

        y = DrawCaption(g, y, "HOJE");
        y = DrawToday(g, y);
        y += 10;

        y = DrawFooter(g, y, now);
        return y + Pad - 4;
    }

    private float DrawHeader(Graphics g, float y)
    {
        IconRenderer.DrawSpark(g, new RectangleF(Pad + 2, y + 3, 28, 28), Palette.Accent);

        DrawText(g, "Claude Pulse", _fTitle, _p.Text, new RectangleF(Pad + 40, y, 200, 20));

        var who = new List<string>();
        if (!string.IsNullOrWhiteSpace(_state.AccountName))
            who.Add(_state.AccountName!);
        if (!string.IsNullOrWhiteSpace(_state.Plan))
            who.Add(_state.Plan!);
        if (!string.IsNullOrWhiteSpace(_state.Origin))
            who.Add(_state.Origin!);
        if (_state.AccountPinned)
            who.Add("conta fixada");
        DrawText(g, who.Count > 0 ? string.Join(" · ", who) : "Procurando login do Claude Code…",
            _fSmall, _p.SubText, new RectangleF(Pad + 40, y + 19, W - Pad * 2 - 40 - 106, 16));

        float bx = W - Pad - 32;
        IconButton(g, new RectangleF(bx, y + 1, 32, 32), "menu", "", () => MenuClicked?.Invoke(), _p.SubText);
        IconButton(g, new RectangleF(bx - 34, y + 1, 32, 32), "refresh", "\uE72C", () => RefreshClicked?.Invoke(),
            _state.Loading ? Palette.Accent : _p.SubText);
        IconButton(g, new RectangleF(bx - 68, y + 1, 32, 32), "history", "\uE787", OpenHistory, _p.SubText);
        return y + 34;
    }

    private float DrawCaption(Graphics g, float y, string text)
    {
        DrawText(g, text, _fCaption, _p.SubText, new RectangleF(Pad + 4, y, W - Pad * 2, 16));
        return y + 20;
    }

    private float DrawLimits(Graphics g, float y, DateTimeOffset now)
    {
        var usage = _state.Usage;
        if (_state.AccountWarning != null)
        {
            string warning = "⚠ " + _state.AccountWarning;
            float textH = g.MeasureString(warning, _fSmall, (int)(W - Pad * 2 - 28)).Height;
            y = MessageCard(g, y, warning, Theme.Warn, Math.Max(48, textH + 18)) + 8;
        }
        if (_state.CanRenew || _state.Renewing)
        {
            y = RenewCard(g, y);
            if (usage == null || usage.Windows.Count == 0)
                return y;
            y += 8; // abaixo, os últimos limites conhecidos
        }
        if (usage == null || usage.Windows.Count == 0)
        {
            string msg = _state.UsageError ?? (_state.Loading ? "Consultando limites da conta…" : "Sem dados de uso ainda.");
            return MessageCard(g, y, msg);
        }

        const float rowH = 66;
        float breakdownH = usage.WeeklyBreakdown.Count > 0 ? 50 : 0;
        float cardH = usage.Windows.Count * rowH + breakdownH + 4;
        Card(g, new RectangleF(Pad, y, W - Pad * 2, cardH));

        float ry = y + 2;
        for (int i = 0; i < usage.Windows.Count; i++)
        {
            if (i > 0)
                Separator(g, ry);
            LimitRow(g, usage.Windows[i], ry, now);
            ry += rowH;
        }
        if (breakdownH > 0)
        {
            Separator(g, ry);
            BreakdownRow(g, usage.WeeklyBreakdown, ry);
        }
        return y + cardH;
    }

    private void LimitRow(Graphics g, UsageWindow w, float y, DateTimeOffset now)
    {
        float x = Pad + 14, right = W - Pad - 14, width = right - x;
        var color = Theme.ForLimit(w.Percent);

        DrawText(g, w.Label, _fBody, _p.Text, new RectangleF(x, y + 9, width - 70, 18));
        DrawText(g, Format.Percent(w.Percent), _fBig, color, new RectangleF(right - 80, y + 6, 80, 22), StringAlignment.Far);

        var bar = new RectangleF(x, y + 33, width, 6);
        Bar(g, bar, w.Percent, color);
        if (w.Elapsed(now) is { } elapsed && elapsed > 0.01 && elapsed < 0.99)
        {
            // Marcador de ritmo: onde você estaria se gastasse o limite de forma uniforme.
            float mx = bar.X + bar.Width * (float)elapsed;
            using var pen = new Pen(Color.FromArgb(_p.Dark ? 200 : 150, _p.Text), 1.6f);
            g.DrawLine(pen, mx, bar.Y - 3.5f, mx, bar.Bottom + 3.5f);
        }

        string left;
        if (w.ResetsAt is { } reset)
            left = $"Reinicia em {Format.Duration(reset - now)} · {Format.When(reset)}";
        else if (w.Key == "five_hour")
            left = "Nenhuma sessão ativa";
        else
            left = "Ciclo mensal";

        string? pace = null;
        Color paceColor = _p.SubText;
        if (w.ExhaustsAt(now) is { } exhausts)
        {
            pace = $"⚠ esgota ~{Format.When(exhausts)}";
            paceColor = Theme.Warn;
        }
        else if (w.ProjectedAtReset(now) is { } projected && w.Percent >= 1)
        {
            pace = $"projeção ~{Math.Min(projected, 100):0}%";
        }

        float paceW = pace == null ? 0 : g.MeasureString(pace, _fSmall).Width + 2;
        if (pace != null)
            DrawText(g, pace, _fSmall, paceColor, new RectangleF(right - paceW, y + 45, paceW, 16), StringAlignment.Far);
        DrawText(g, left, _fSmall, _p.SubText, new RectangleF(x, y + 45, width - paceW - 6, 16));
    }

    private void BreakdownRow(Graphics g, List<BreakdownItem> items, float y)
    {
        float x = Pad + 14, right = W - Pad - 14, width = right - x;
        DrawText(g, "Uso da semana por app", _fSmall, _p.SubText, new RectangleF(x, y + 8, width, 16));

        var bar = new RectangleF(x, y + 27, width, 6);
        using (var track = new SolidBrush(_p.Track))
        using (var path = Shapes.Round(bar, 3))
            g.FillPath(track, path);

        double total = Math.Max(100, items.Sum(i => i.Percent));
        float cursor = bar.X;
        var state = g.Save();
        using (var clip = Shapes.Round(bar, 3))
            g.SetClip(clip);
        for (int i = 0; i < items.Count; i++)
        {
            float segW = bar.Width * (float)(items[i].Percent / total);
            using var brush = new SolidBrush(Theme.Categories[i % Theme.Categories.Length]);
            g.FillRectangle(brush, cursor, bar.Y, segW + 0.5f, bar.Height);
            cursor += segW;
        }
        g.Restore(state);

        float lx = x;
        for (int i = 0; i < items.Count; i++)
        {
            var label = $"{items[i].Name} {items[i].Percent:0}%";
            var size = g.MeasureString(label, _fSmall);
            if (lx + size.Width + 12 > right)
                break;
            using (var dot = new SolidBrush(Theme.Categories[i % Theme.Categories.Length]))
                g.FillEllipse(dot, lx, y + 40, 6, 6);
            DrawText(g, label, _fSmall, _p.SubText, new RectangleF(lx + 9, y + 35, size.Width + 4, 16));
            lx += size.Width + 20;
        }
    }

    private float DrawSessions(Graphics g, float y, DateTimeOffset now)
    {
        var sessions = _state.Sessions;
        if (sessions.Count == 0)
            return MessageCard(g, y, "Nenhuma sessão do Claude Code nas últimas 24h.");

        const float rowH = 64;
        float cardH = sessions.Count * rowH + 4;
        Card(g, new RectangleF(Pad, y, W - Pad * 2, cardH));

        float ry = y + 2;
        for (int i = 0; i < sessions.Count; i++)
        {
            if (i > 0)
                Separator(g, ry);
            SessionRow(g, sessions[i], ry, now);
            ry += rowH;
        }
        return y + cardH;
    }

    private void SessionRow(Graphics g, SessionInfo s, float y, DateTimeOffset now)
    {
        float x = Pad + 14, right = W - Pad - 14, width = right - x;
        long limit = Models.ContextLimit(s.Model, s.ContextTokens, _state.ContextLimitOverride);
        double pct = 100.0 * s.ContextTokens / limit;
        bool active = now - s.LastActivity < TimeSpan.FromMinutes(15);
        var color = Theme.ForContext(pct);
        string id = "session:" + s.SessionId;

        var rowRect = new RectangleF(Pad + 2, y + 1, W - Pad * 2 - 4, 62);
        if (_hoverId == id)
        {
            using var hover = new SolidBrush(_p.Hover);
            using var path = Shapes.Round(rowRect, 6);
            g.FillPath(hover, path);
        }
        _hits.Add((rowRect, id, () =>
        {
            try
            {
                Clipboard.SetText(s.ResumeCommand());
                Flash(s.IsWslOrigin() ? "Copiado: retomar no bash do WSL" : "Copiado: retomar no PowerShell");
            }
            catch
            {
                // Área de transferência ocupada.
            }
        }));

        // Indicador de sessão ativa.
        using (var dot = new SolidBrush(active ? Theme.Good : _p.Track))
            g.FillEllipse(dot, x, y + 14, 7, 7);

        string amount = $"{Format.Tokens(s.ContextTokens)} / {Format.Tokens(limit)}";
        DrawText(g, s.DisplayTitle, _fBodyBold, _p.Text, new RectangleF(x + 13, y + 8, width - 13 - 92, 18));
        DrawText(g, amount, _fSmall, _p.SubText, new RectangleF(right - 92, y + 10, 92, 16), StringAlignment.Far);

        Bar(g, new RectangleF(x, y + 32, width, 6), pct, color);

        bool mixedOrigins = _state.Sessions.Select(o => o.IsWslOrigin()).Distinct().Count() > 1;
        var meta = new List<string> { Models.DisplayName(s.Model), Format.Ago(s.LastActivity) };
        if (!string.IsNullOrEmpty(s.Branch) && s.Branch != "HEAD")
            meta.Insert(0, s.Branch!);
        if (mixedOrigins)
            meta.Insert(0, s.IsWslOrigin() ? "WSL" : "Windows");
        if (!string.Equals(s.Project, s.DisplayTitle, StringComparison.OrdinalIgnoreCase))
            meta.Insert(0, s.Project);

        long free = Math.Max(0, limit - s.ContextTokens);
        string remaining = $"{pct:0}% · livre {Format.Tokens(free)}";
        float remainingW = g.MeasureString(remaining, _fSmall).Width + 2;
        DrawText(g, remaining, _fSmall, pct >= 80 ? color : _p.SubText,
            new RectangleF(right - remainingW, y + 44, remainingW, 16), StringAlignment.Far);
        DrawText(g, string.Join(" · ", meta), _fSmall, _p.SubText, new RectangleF(x, y + 44, width - remainingW - 8, 16));
    }

    private float DrawToday(Graphics g, float y)
    {
        var t = _state.Today;
        if (t == null)
            return MessageCard(g, y, "Lendo histórico local…");

        const float cardH = 84;
        var cardRect = new RectangleF(Pad, y, W - Pad * 2, cardH);
        Card(g, cardRect);
        if (_hoverId == "today")
        {
            using var hover = new SolidBrush(_p.Hover);
            using var path = Shapes.Round(RectangleF.Inflate(cardRect, -2, -2), 6);
            g.FillPath(hover, path);
        }
        _hits.Add((cardRect, "today", OpenHistory));

        float x = Pad + 14, width = W - Pad * 2 - 28;
        float colW = width / 3;
        (string Value, string Label)[] stats =
        [
            (t.Responses.ToString("N0", Format.PtBr), "respostas"),
            (Format.Tokens(t.Output), "tokens gerados"),
            (Format.Usd(t.CostUsd), "≈ custo na API"),
        ];
        for (int i = 0; i < stats.Length; i++)
        {
            float cx = x + colW * i;
            DrawText(g, stats[i].Value, _fStat, i == 2 ? Palette.Accent : _p.Text, new RectangleF(cx, y + 9, colW - 6, 26));
            DrawText(g, stats[i].Label, _fSmall, _p.SubText, new RectangleF(cx, y + 35, colW - 6, 16));
        }

        string cache = t.TotalInput > 0
            ? $"{t.Sessions} {(t.Sessions == 1 ? "sessão" : "sessões")} · {Format.Tokens(t.TotalInput)} tokens de entrada · {t.CacheHitRate.ToString("P0", Format.PtBr)} via cache"
            : "Nenhuma resposta do Claude Code hoje.";
        DrawText(g, cache, _fSmall, _p.SubText, new RectangleF(x, y + 59, width - 20, 16));
        DrawText(g, "\uE76C", _fIcon, _p.SubText, new RectangleF(cardRect.Right - 30, y, 20, cardH), StringAlignment.Center, StringAlignment.Center);
        return y + cardH;
    }

    private float DrawFooter(Graphics g, float y, DateTimeOffset now)
    {
        string text;
        Color color = _p.SubText;
        if (_flash != null && DateTime.Now < _flashUntil)
        {
            text = _flash;
            color = Theme.Good;
        }
        else if (_state.Loading)
        {
            text = "Atualizando…";
        }
        else if (_state.UsageError != null && _state.Usage != null && !_state.CanRenew)
        {
            text = _state.UsageError;
            color = Theme.Warn;
        }
        else if (_state.Usage != null)
        {
            text = "Atualizado " + Format.Ago(_state.Usage.FetchedAt);
        }
        else
        {
            text = "";
        }
        DrawText(g, text, _fSmall, color, new RectangleF(Pad + 4, y + 2, W - Pad * 2 - 130, 16));

        var linkRect = new RectangleF(W - Pad - 124, y, 124, 20);
        bool hover = _hoverId == "link";
        DrawText(g, "Abrir no claude.ai ↗", hover ? _fBodyBold : _fSmall, Palette.Accent, linkRect, StringAlignment.Far);
        _hits.Add((linkRect, "link", () => OpenUrl(UsageUrl)));
        return y + 20;
    }

    private void OpenHistory()
    {
        HidePanel();
        HistoryClicked?.Invoke();
    }

    // ───────────────────────────── Primitivas ─────────────────────────────

    private float MessageCard(Graphics g, float y, string message, Color? color = null, float h = 48)
    {
        Card(g, new RectangleF(Pad, y, W - Pad * 2, h));
        DrawText(g, message, _fSmall, color ?? _p.SubText, new RectangleF(Pad + 14, y + 8, W - Pad * 2 - 28, h - 16),
            StringAlignment.Near, StringAlignment.Center, wrap: true);
        return y + h;
    }

    /// <summary>Aviso de login expirado com o botão que pede para a CLI do Claude Code renovar.</summary>
    private float RenewCard(Graphics g, float y)
    {
        const float h = 56, buttonW = 92;
        Card(g, new RectangleF(Pad, y, W - Pad * 2, h));

        string message = _state.Renewing
            ? "Renovando o login pela CLI do Claude Code…"
            : _state.UsageError ?? "O login do Claude Code expirou.";
        DrawText(g, message, _fSmall, _state.Renewing ? _p.SubText : Theme.Warn,
            new RectangleF(Pad + 14, y + 6, W - Pad * 2 - 28 - buttonW - 10, h - 12),
            StringAlignment.Near, StringAlignment.Center, wrap: true);

        var button = new RectangleF(W - Pad - 14 - buttonW, y + (h - 28) / 2, buttonW, 28);
        bool hover = _hoverId == "renew" && !_state.Renewing;
        var fill = _state.Renewing ? _p.Track : hover ? ControlPaint.Light(Palette.Accent, 0.2f) : Palette.Accent;
        using (var brush = new SolidBrush(fill))
        using (var path = Shapes.Round(button, 6))
            g.FillPath(brush, path);
        DrawText(g, _state.Renewing ? "Renovando…" : "Renovar", _fBodyBold, _state.Renewing ? _p.SubText : Color.White,
            button, StringAlignment.Center, StringAlignment.Center);
        if (!_state.Renewing)
            _hits.Add((button, "renew", () => RenewClicked?.Invoke()));
        return y + h;
    }

    private void Card(Graphics g, RectangleF r)
    {
        using var path = Shapes.Round(r, 8);
        using var fill = new SolidBrush(_p.Card);
        using var border = new Pen(_p.CardBorder, 1);
        g.FillPath(fill, path);
        g.DrawPath(border, path);
    }

    private void Separator(Graphics g, float y)
    {
        using var pen = new Pen(_p.Separator, 1);
        g.DrawLine(pen, Pad + 12, y, W - Pad - 12, y);
    }

    private void Bar(Graphics g, RectangleF r, double percent, Color color)
    {
        using (var track = new SolidBrush(_p.Track))
        using (var path = Shapes.Round(r, r.Height / 2))
            g.FillPath(track, path);
        if (percent <= 0)
            return;
        var fill = r with { Width = Math.Max(r.Height, r.Width * (float)Math.Clamp(percent, 0, 100) / 100) };
        using var brush = new LinearGradientBrush(fill, ControlPaint.Light(color, 0.15f), color, LinearGradientMode.Horizontal);
        using var fillPath = Shapes.Round(fill, r.Height / 2);
        g.FillPath(brush, fillPath);
    }

    private void IconButton(Graphics g, RectangleF r, string id, string glyph, Action action, Color color)
    {
        if (_hoverId == id)
        {
            using var hover = new SolidBrush(_p.Hover);
            using var path = Shapes.Round(r, 6);
            g.FillPath(hover, path);
        }
        DrawText(g, glyph, _fIcon, color, r, StringAlignment.Center, StringAlignment.Center);
        _hits.Add((r, id, action));
    }

    private static void DrawText(Graphics g, string text, Font font, Color color, RectangleF r,
        StringAlignment align = StringAlignment.Near, StringAlignment lineAlign = StringAlignment.Near, bool wrap = false)
    {
        using var brush = new SolidBrush(color);
        using var format = new StringFormat
        {
            Alignment = align,
            LineAlignment = lineAlign,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = wrap ? 0 : StringFormatFlags.NoWrap,
        };
        g.DrawString(text, font, brush, r, format);
    }

    private void CreateFonts()
    {
        foreach (var f in new[] { _fTitle, _fBody, _fBodyBold, _fSmall, _fCaption, _fBig, _fStat, _fIcon })
            f?.Dispose();
        const string ui = "Segoe UI";
        string semibold = FontFamily.Families.Any(f => f.Name == "Segoe UI Semibold") ? "Segoe UI Semibold" : ui;
        _fTitle = new Font(semibold, 15, FontStyle.Regular, GraphicsUnit.Pixel);
        _fBody = new Font(ui, 13, FontStyle.Regular, GraphicsUnit.Pixel);
        _fBodyBold = new Font(semibold, 13, FontStyle.Regular, GraphicsUnit.Pixel);
        _fSmall = new Font(ui, 11.5f, FontStyle.Regular, GraphicsUnit.Pixel);
        _fCaption = new Font(semibold, 10.5f, FontStyle.Regular, GraphicsUnit.Pixel);
        _fBig = new Font(semibold, 17, FontStyle.Regular, GraphicsUnit.Pixel);
        _fStat = new Font(semibold, 19, FontStyle.Regular, GraphicsUnit.Pixel);
        _fIcon = new Font(_iconFamily, 14, FontStyle.Regular, GraphicsUnit.Pixel);
    }

    private static void OpenUrl(string url)
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _clock.Dispose();
            _fade.Dispose();
            foreach (var f in new[] { _fTitle, _fBody, _fBodyBold, _fSmall, _fCaption, _fBig, _fStat, _fIcon })
                f?.Dispose();
        }
        base.Dispose(disposing);
    }

    // ───────────────────────────── Win32 ─────────────────────────────

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Point pt, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    private static int DpiAt(Point p)
    {
        try
        {
            var monitor = MonitorFromPoint(p, 2 /* MONITOR_DEFAULTTONEAREST */);
            if (GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0)
                return (int)dpiX;
        }
        catch
        {
            // Windows antigo.
        }
        return 96;
    }
}
