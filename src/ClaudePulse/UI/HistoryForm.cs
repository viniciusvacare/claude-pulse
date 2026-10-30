using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using ClaudePulse.Core;

namespace ClaudePulse.UI;

/// <summary>
/// Histórico mensal: barras por dia (clique para selecionar), resumo do mês e detalhe do dia
/// por modelo e por projeto. Desenhado em GDI+ em pixels lógicos, como o painel.
/// </summary>
public sealed class HistoryForm : Form
{
    private enum Metric { Tokens, Output, Cost }

    private const float W = 680;
    private const float Pad = 16;
    private const int MaxRows = 4;

    private readonly UsageHistory _history;
    private readonly Func<string?> _activeAccount;
    private string? _account; // null = todas as contas
    private readonly List<(RectangleF Rect, string Id, Action Action)> _hits = [];
    private Palette _p = Palette.Current();
    private float _scale = 1;
    private float _height = 700;
    private DateOnly _month;
    private DateOnly _selected;
    private Metric _metric = Metric.Tokens;
    private string? _hoverId;
    private DateOnly? _hoverDay;
    private string? _flash;

    private readonly string _iconFamily;
    private Font _fTitle = null!, _fBody = null!, _fBodyBold = null!, _fSmall = null!, _fTiny = null!, _fTinyBold = null!,
        _fCaption = null!, _fStat = null!, _fValue = null!, _fIcon = null!, _fIconSmall = null!;

    public HistoryForm(UsageHistory history, Func<string?> activeAccount, DateOnly? select = null)
    {
        _history = history;
        _activeAccount = activeAccount;
        var keys = history.AccountKeys;
        var active = activeAccount();
        _account = active != null && keys.Contains(active) ? active : keys.Count == 1 ? keys[0] : null;
        _selected = select ?? DateOnly.FromDateTime(DateTime.Today);
        _month = new DateOnly(_selected.Year, _selected.Month, 1);

        Text = "Claude Pulse · Histórico de uso";
        Icon = IconRenderer.AppIcon();
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.None;
        KeyPreview = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);

        _iconFamily = FontFamily.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
        CreateFonts();
    }

    /// <summary>Chamado quando o histórico muda (nova resposta, novo pico).</summary>
    public void DataChanged()
    {
        if (Visible)
            Invalidate();
    }

    public void SelectDay(DateOnly day)
    {
        _selected = day;
        _month = new DateOnly(day.Year, day.Month, 1);
        Invalidate();
    }

    // ───────────────────────────── Janela ─────────────────────────────

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyTheme();
        _scale = DeviceDpi / 96f;
        CreateFonts();
        Relayout();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        _scale = e.DeviceDpiNew / 96f;
        CreateFonts();
        Relayout();
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        var current = Palette.Current();
        if (current.Dark != _p.Dark)
        {
            _p = current;
            ApplyTheme();
        }
        Invalidate();
    }

    private void ApplyTheme()
    {
        BackColor = _p.Background;
        int dark = _p.Dark ? 1 : 0;
        DwmSetWindowAttribute(Handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref dark, sizeof(int));
    }

    private void Relayout()
    {
        using var bmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bmp);
        _height = Render(g);
        ClientSize = new Size((int)Math.Ceiling(W * _scale), (int)Math.Ceiling(_height * _scale));
        Invalidate();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.KeyCode)
        {
            case Keys.Escape: Close(); break;
            case Keys.Left: MoveSelection(-1); break;
            case Keys.Right: MoveSelection(1); break;
            case Keys.PageUp: ChangeMonth(-1); break;
            case Keys.PageDown: ChangeMonth(1); break;
            case Keys.Home: SelectDay(Today); break;
        }
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    private void MoveSelection(int days)
    {
        var next = _selected.AddDays(days);
        if (next <= Today)
            SelectDay(next);
    }

    private void ChangeMonth(int delta)
    {
        var month = _month.AddMonths(delta);
        if (month > new DateOnly(Today.Year, Today.Month, 1))
            return;
        _month = month;
        int lastDay = DateTime.DaysInMonth(month.Year, month.Month);
        var candidate = new DateOnly(month.Year, month.Month, Math.Min(_selected.Day, lastDay));
        _selected = candidate > Today ? Today : candidate;
        Invalidate();
    }

    // ───────────────────────────── Mouse ─────────────────────────────

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hit = HitTest(e.Location);
        Cursor = hit != null ? Cursors.Hand : Cursors.Default;
        DateOnly? day = hit?.Id.StartsWith("day:") == true ? DateOnly.Parse(hit.Value.Id[4..]) : null;
        if (hit?.Id != _hoverId || day != _hoverDay)
        {
            _hoverId = hit?.Id;
            _hoverDay = day;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverId = null;
        _hoverDay = null;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left)
            HitTest(e.Location)?.Action();
    }

    private (RectangleF Rect, string Id, Action Action)? HitTest(Point p)
    {
        var logical = new PointF(p.X / _scale, p.Y / _scale);
        for (int i = _hits.Count - 1; i >= 0; i--)
        {
            if (_hits[i].Rect.Contains(logical))
                return _hits[i];
        }
        return null;
    }

    // ───────────────────────────── Desenho ─────────────────────────────

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(_p.Background);
        g.ScaleTransform(_scale, _scale);
        Render(g);
    }

    private float Render(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        _hits.Clear();

        var days = MonthDays();
        float y = Pad;
        y = DrawNav(g, y);
        y += 12;
        y = DrawSummary(g, y, days);
        y += 12;
        var chart = new RectangleF(Pad, y, W - Pad * 2, 236);
        y = DrawChart(g, chart, days);
        y += 12;
        y = DrawDetail(g, y);
        y += 12;
        y = DrawFooter(g, y);
        DrawTooltip(g, chart, days);
        return y + Pad;
    }

    private List<(DateOnly Date, DayRecord? Record)> MonthDays()
    {
        int count = DateTime.DaysInMonth(_month.Year, _month.Month);
        return Enumerable.Range(1, count)
            .Select(d => new DateOnly(_month.Year, _month.Month, d))
            .Select(d => (d, _history.Get(_account, d)))
            .ToList();
    }

    private float DrawNav(Graphics g, float y)
    {
        bool isCurrentMonth = _month.Year == Today.Year && _month.Month == Today.Month;
        bool hasEarlier = _history.FirstDay(_account) is { } first && new DateOnly(first.Year, first.Month, 1) < _month;

        IconButton(g, new RectangleF(Pad, y + 2, 32, 32), "prev", "", () => ChangeMonth(-1), enabled: hasEarlier);
        string title = Format.Capitalize(_month.ToString("MMMM 'de' yyyy", Format.PtBr));
        var size = g.MeasureString(title, _fTitle);
        Gfx.Text(g, title, _fTitle, _p.Text, new RectangleF(Pad + 38, y + 4, size.Width + 4, 28));
        IconButton(g, new RectangleF(Pad + 42 + size.Width, y + 2, 32, 32), "next", "", () => ChangeMonth(1), enabled: !isCurrentMonth);

        // Seletor de conta.
        string accountText = _history.AccountLabelText(_account);
        float maxW = W - Pad - (Pad + 82 + size.Width) - 16;
        float accountW = Math.Min(maxW, g.MeasureString(accountText, _fSmall).Width + 52);
        var accountRect = new RectangleF(W - Pad - accountW, y + 4, accountW, 28);
        using (var path = Shapes.Round(accountRect, 6))
        {
            using var fill = new SolidBrush(_hoverId == "account" ? _p.Hover : _p.Card);
            using var border = new Pen(_p.CardBorder, 1);
            g.FillPath(fill, path);
            g.DrawPath(border, path);
        }
        Gfx.Text(g, "\uE77B", _fIcon, _p.SubText, new RectangleF(accountRect.X + 6, accountRect.Y, 20, accountRect.Height), StringAlignment.Center, StringAlignment.Center);
        Gfx.Text(g, accountText, _fSmall, _p.Text, new RectangleF(accountRect.X + 28, accountRect.Y, accountRect.Width - 50, accountRect.Height), StringAlignment.Near, StringAlignment.Center);
        Gfx.Text(g, "\uE70D", _fIconSmall, _p.SubText, new RectangleF(accountRect.Right - 22, accountRect.Y, 16, accountRect.Height), StringAlignment.Center, StringAlignment.Center);
        _hits.Add((accountRect, "account", () => ShowAccountMenu(accountRect)));
        return y + 36;
    }

    private float DrawSummary(Graphics g, float y, List<(DateOnly Date, DayRecord? Record)> days)
    {
        var records = days.Where(d => d.Record is { Responses: > 0 }).Select(d => d.Record!).ToList();
        long total = records.Sum(r => r.Total);
        long output = records.Sum(r => r.Output);
        double cost = records.Sum(r => r.CostUsd);
        int active = records.Count;

        (string Value, string Label)[] tiles =
        [
            (Format.Tokens(total), "tokens no mês"),
            (Format.Tokens(output), "tokens gerados"),
            (Format.Usd(cost), "≈ custo na API"),
            (active.ToString(), $"dias com uso · de {days.Count}"),
        ];
        float gap = 10, tileW = (W - Pad * 2 - gap * (tiles.Length - 1)) / tiles.Length;
        for (int i = 0; i < tiles.Length; i++)
        {
            var r = new RectangleF(Pad + i * (tileW + gap), y, tileW, 64);
            Gfx.Card(g, r, _p);
            Gfx.Text(g, tiles[i].Value, _fStat, _p.Text, new RectangleF(r.X + 14, r.Y + 9, r.Width - 20, 28));
            Gfx.Text(g, tiles[i].Label, _fSmall, _p.SubText, new RectangleF(r.X + 14, r.Y + 39, r.Width - 20, 16));
        }
        return y + 64;
    }

    private void DrawMetricChips(Graphics g, float right, float y)
    {
        (Metric Metric, string Label)[] chips = [(Metric.Tokens, "Tokens"), (Metric.Output, "Saída"), (Metric.Cost, "Custo")];
        float x = right;
        for (int i = chips.Length - 1; i >= 0; i--)
        {
            var (metric, label) = chips[i];
            float w = g.MeasureString(label, _fSmall).Width + 22;
            x -= w;
            var r = new RectangleF(x, y, w, 24);
            bool selected = _metric == metric;
            string id = "metric:" + label;
            using (var path = Shapes.Round(r, 12))
            {
                using var fill = new SolidBrush(selected ? _p.Text : _hoverId == id ? _p.Hover : _p.Card);
                g.FillPath(fill, path);
                if (!selected)
                {
                    using var border = new Pen(_p.CardBorder, 1);
                    g.DrawPath(border, path);
                }
            }
            Gfx.Text(g, label, _fSmall, selected ? _p.Background : _p.Text, r, StringAlignment.Center, StringAlignment.Center);
            _hits.Add((r, id, () => { _metric = metric; Invalidate(); }));
            x -= 6;
        }
    }

    private void ShowAccountMenu(RectangleF anchor)
    {
        var menu = new ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = true };
        void Add(string? key, string text)
        {
            var item = new ToolStripMenuItem(text) { Checked = _account == key };
            item.Click += (_, _) =>
            {
                _account = key;
                Invalidate();
            };
            menu.Items.Add(item);
        }
        Add(null, "Todas as contas");
        menu.Items.Add(new ToolStripSeparator());
        var keys = _history.AccountKeys;
        foreach (var key in keys)
            Add(key, _history.AccountLabelText(key, withOrganization: true) + (key == _activeAccount() ? "  (em uso)" : ""));
        if (keys.Count == 0)
            menu.Items.Add(new ToolStripMenuItem("Nenhuma conta com histórico") { Enabled = false });
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        menu.Show(this, new Point((int)(anchor.X * _scale), (int)(anchor.Bottom * _scale) + 2));
    }

    private double Value(DayRecord? r) => r == null ? 0 : _metric switch
    {
        Metric.Output => r.Output,
        Metric.Cost => r.CostUsd,
        _ => r.Total,
    };

    private string FormatValue(double v) => _metric == Metric.Cost ? Format.Usd(v) : Format.Tokens((long)v);

    private float DrawChart(Graphics g, RectangleF card, List<(DateOnly Date, DayRecord? Record)> days)
    {
        Gfx.Card(g, card, _p);
        string caption = _metric switch
        {
            Metric.Output => "TOKENS GERADOS POR DIA",
            Metric.Cost => "CUSTO EQUIVALENTE NA API POR DIA (US$)",
            _ => "TOKENS POR DIA (ENTRADA + CACHE + SAÍDA)",
        };
        Gfx.Text(g, caption, _fCaption, _p.SubText, new RectangleF(card.X + 14, card.Y + 12, card.Width - 230, 16));
        DrawMetricChips(g, card.Right - 10, card.Y + 8);

        var plot = RectangleF.FromLTRB(card.X + 58, card.Y + 40, card.Right - 14, card.Bottom - 30);
        double max = days.Max(d => Value(d.Record));
        if (max <= 0)
        {
            Gfx.Text(g, "Sem uso registrado neste mês.", _fBody, _p.SubText, plot, StringAlignment.Center, StringAlignment.Center);
            return card.Bottom;
        }
        double top = NiceCeiling(max);

        // Grade recessiva: 0, metade e topo.
        using (var grid = new Pen(_p.Separator, 1))
        {
            for (int i = 0; i <= 2; i++)
            {
                float gy = plot.Bottom - plot.Height * i / 2f;
                g.DrawLine(grid, plot.X, gy, plot.Right, gy);
                Gfx.Text(g, i == 0 ? "0" : FormatValue(top * i / 2), _fTiny, _p.SubText,
                    new RectangleF(card.X + 4, gy - 8, plot.X - card.X - 10, 16), StringAlignment.Far);
            }
        }

        float slot = plot.Width / days.Count;
        float barW = Math.Min(24, slot * 0.62f);
        var series = Gfx.Series(_p.Dark);
        var seriesStrong = Gfx.SeriesStrong(_p.Dark);
        foreach (var (date, record) in days)
        {
            int i = date.Day - 1;
            float cx = plot.X + slot * (i + 0.5f);
            var column = new RectangleF(plot.X + slot * i, plot.Y - 6, slot, plot.Height + 28);
            bool future = date > Today;
            bool selected = date == _selected;
            bool hover = _hoverDay == date;

            if (selected || hover)
            {
                using var band = new SolidBrush(selected ? _p.Hover : Color.FromArgb(_p.Dark ? 70 : 110, _p.Hover));
                using var path = Shapes.Round(column, 6);
                g.FillPath(band, path);
            }

            double v = Value(record);
            if (v > 0)
            {
                float h = Math.Max(2, (float)(v / top) * plot.Height);
                var bar = new RectangleF(cx - barW / 2, plot.Bottom - h, barW, h);
                using var brush = new SolidBrush(selected ? seriesStrong : series);
                using var path = Gfx.TopRounded(bar, Math.Min(4, barW / 2));
                g.FillPath(brush, path);
            }

            bool emphasize = selected || date == Today;
            Gfx.Text(g, date.Day.ToString(), emphasize ? _fTinyBold : _fTiny,
                future ? Color.FromArgb(110, _p.SubText) : emphasize ? _p.Text : _p.SubText,
                new RectangleF(column.X - 4, plot.Bottom + 6, column.Width + 8, 14), StringAlignment.Center);

            if (!future)
                _hits.Add((column, "day:" + date.ToString("yyyy-MM-dd"), () => SelectDay(date)));
        }
        return card.Bottom;
    }

    private void DrawTooltip(Graphics g, RectangleF chart, List<(DateOnly Date, DayRecord? Record)> days)
    {
        if (_hoverDay is not { } day || day.Month != _month.Month || day.Year != _month.Year)
            return;
        var record = _history.Get(_account, day);
        string title = day.ToString("ddd, dd/MM", Format.PtBr);
        string line = record is { Responses: > 0 }
            ? $"{Format.Tokens(record.Total)} tokens · {Format.Tokens(record.Output)} gerados · {Format.Usd(record.CostUsd)}"
            : "Sem uso";

        var plotX = chart.X + 58;
        float slot = (chart.Right - 14 - plotX) / days.Count;
        float cx = plotX + slot * (day.Day - 0.5f);
        float w = Math.Max(g.MeasureString(line, _fSmall).Width, g.MeasureString(title, _fBodyBold).Width) + 20;
        float x = Math.Clamp(cx - w / 2, chart.X + 4, chart.Right - w - 4);
        var r = new RectangleF(x, chart.Y + 30, w, 44);

        using (var path = Shapes.Round(r, 6))
        {
            using var fill = new SolidBrush(_p.Dark ? Color.FromArgb(58, 58, 58) : Color.White);
            using var border = new Pen(_p.CardBorder, 1);
            g.FillPath(fill, path);
            g.DrawPath(border, path);
        }
        Gfx.Text(g, title, _fBodyBold, _p.Text, new RectangleF(r.X + 10, r.Y + 5, r.Width - 16, 18));
        Gfx.Text(g, line, _fSmall, _p.SubText, new RectangleF(r.X + 10, r.Y + 24, r.Width - 16, 16));
    }

    private float DrawDetail(Graphics g, float y)
    {
        const float h = 268;
        var card = new RectangleF(Pad, y, W - Pad * 2, h);
        Gfx.Card(g, card, _p);
        float x = card.X + 16, right = card.Right - 16, width = right - x;

        string dayTitle = Format.Capitalize(_selected.ToString("dddd, d 'de' MMMM", Format.PtBr));
        if (_selected == Today)
            dayTitle += " · hoje";
        Gfx.Text(g, dayTitle, _fValue, _p.Text, new RectangleF(x, y + 12, width * 0.62f, 22));

        var r = _history.Get(_account, _selected);
        if (r == null || r.Responses == 0)
        {
            Gfx.Text(g, "Nenhum uso do Claude Code registrado neste dia.", _fBody, _p.SubText,
                new RectangleF(x, y + 40, width, h - 56), StringAlignment.Center, StringAlignment.Center);
            return card.Bottom;
        }

        string sessions = r.Sessions == 1 ? "1 sessão" : $"{r.Sessions} sessões";
        Gfx.Text(g, $"{r.Responses:N0} respostas · {sessions}".Replace(",", "."), _fSmall, _p.SubText,
            new RectangleF(x + width * 0.5f, y + 16, width * 0.5f, 16), StringAlignment.Far);

        (string Value, string Label)[] stats =
        [
            (Format.Tokens(r.Input), "entrada"),
            (Format.Tokens(r.CacheRead), "cache lido"),
            (Format.Tokens(r.CacheWrite), "cache escrito"),
            (Format.Tokens(r.Output), "saída"),
            (Format.Usd(r.CostUsd), "≈ custo API"),
        ];
        float colW = width / stats.Length;
        for (int i = 0; i < stats.Length; i++)
        {
            Gfx.Text(g, stats[i].Value, _fValue, _p.Text, new RectangleF(x + colW * i, y + 44, colW - 6, 22));
            Gfx.Text(g, stats[i].Label, _fSmall, _p.SubText, new RectangleF(x + colW * i, y + 67, colW - 6, 16));
        }

        string peaks = _account == null
            ? "Picos de limite são por conta: escolha uma conta no topo para vê-los."
            : r.Peak5h is null && r.PeakWeekly is null
            ? "Pico de limites: não registrado (o Claude Pulse não estava aberto neste dia)"
            : "Pico de limites no dia: " + string.Join(" · ", new[]
            {
                r.Peak5h is { } p5 ? $"sessão 5h {p5:0}%" : null,
                r.PeakWeekly is { } pw ? $"semana {pw:0}%" : null,
            }.Where(s => s != null));
        Gfx.Text(g, peaks, _fSmall, _p.SubText, new RectangleF(x, y + 92, width, 16));

        using (var sep = new Pen(_p.Separator, 1))
            g.DrawLine(sep, x, y + 116, right, y + 116);

        float colGap = 24, half = (width - colGap) / 2;
        var models = r.Models.OrderByDescending(m => m.Value.Total).Select(m => (m.Key, (double)m.Value.Total)).ToList();
        var projects = r.Projects.OrderByDescending(p => p.Value).Select(p => (p.Key, (double)p.Value)).ToList();
        BreakdownList(g, new RectangleF(x, y + 126, half, h - 136), "POR MODELO", models, r.Total, colorByName: true);
        BreakdownList(g, new RectangleF(x + half + colGap, y + 126, half, h - 136), "POR PROJETO", projects, r.Total, colorByName: false);
        return card.Bottom;
    }

    private void BreakdownList(Graphics g, RectangleF area, string caption, List<(string Name, double Value)> items, long total, bool colorByName)
    {
        Gfx.Text(g, caption, _fCaption, _p.SubText, new RectangleF(area.X, area.Y, area.Width, 16));
        if (items.Count > MaxRows)
        {
            // Excedentes viram "Outros" (nunca uma cor gerada).
            double rest = items.Skip(MaxRows - 1).Sum(i => i.Value);
            items = [.. items.Take(MaxRows - 1), ("Outros", rest)];
        }
        double max = Math.Max(1, items.Count == 0 ? 1 : items.Max(i => i.Value));
        float ry = area.Y + 22;
        foreach (var (name, value) in items)
        {
            var color = colorByName ? Gfx.ModelColor(name, _p.Dark) : Gfx.Series(_p.Dark);
            if (name == "Outros")
                color = _p.SubText;
            using (var dot = new SolidBrush(color))
                g.FillEllipse(dot, area.X, ry + 5, 8, 8);

            string share = total > 0 ? $"{100.0 * value / total:0}%" : "";
            string amount = $"{Format.Tokens((long)value)} · {share}";
            float amountW = g.MeasureString(amount, _fSmall).Width + 2;
            Gfx.Text(g, name, _fBody, _p.Text, new RectangleF(area.X + 14, ry, area.Width - 14 - amountW - 6, 18));
            Gfx.Text(g, amount, _fSmall, _p.SubText, new RectangleF(area.Right - amountW, ry + 1, amountW, 16), StringAlignment.Far);

            var track = new RectangleF(area.X + 14, ry + 21, area.Width - 14, 4);
            using (var trackBrush = new SolidBrush(_p.Track))
            using (var trackPath = Shapes.Round(track, 2))
                g.FillPath(trackBrush, trackPath);
            var fill = track with { Width = Math.Max(4, track.Width * (float)(value / max)) };
            using (var fillBrush = new SolidBrush(color))
            using (var fillPath = Shapes.Round(fill, 2))
                g.FillPath(fillBrush, fillPath);
            ry += 28;
        }
    }

    private float DrawFooter(Graphics g, float y)
    {
        float x = Pad;
        x = TextButton(g, x, y, "csv", "", "Exportar CSV do mês", ExportCsv) + 8;
        TextButton(g, x, y, "folder", "", "Abrir pasta de dados", OpenDataFolder);

        string note = _flash ?? "Salvo localmente · os transcripts do Claude Code expiram em ~30 dias, este histórico não.";
        Gfx.Text(g, note, _fTiny, _flash != null ? Theme.Good : _p.SubText,
            new RectangleF(W - Pad - 250, y + 1, 250, 30), StringAlignment.Far, StringAlignment.Center, wrap: true);
        return y + 32;
    }

    private float TextButton(Graphics g, float x, float y, string id, string glyph, string label, Action action)
    {
        float w = g.MeasureString(label, _fSmall).Width + 40;
        var r = new RectangleF(x, y, w, 32);
        using (var path = Shapes.Round(r, 6))
        {
            using var fill = new SolidBrush(_hoverId == id ? _p.Hover : _p.Card);
            using var border = new Pen(_p.CardBorder, 1);
            g.FillPath(fill, path);
            g.DrawPath(border, path);
        }
        Gfx.Text(g, glyph, _fIcon, _p.Text, new RectangleF(r.X + 8, r.Y, 20, r.Height), StringAlignment.Center, StringAlignment.Center);
        Gfx.Text(g, label, _fSmall, _p.Text, new RectangleF(r.X + 30, r.Y, w - 34, r.Height), StringAlignment.Near, StringAlignment.Center);
        _hits.Add((r, id, action));
        return r.Right;
    }

    private void IconButton(Graphics g, RectangleF r, string id, string glyph, Action action, bool enabled)
    {
        if (enabled && _hoverId == id)
        {
            using var hover = new SolidBrush(_p.Hover);
            using var path = Shapes.Round(r, 6);
            g.FillPath(hover, path);
        }
        Gfx.Text(g, glyph, _fIcon, enabled ? _p.Text : Color.FromArgb(90, _p.SubText), r, StringAlignment.Center, StringAlignment.Center);
        if (enabled)
            _hits.Add((r, id, action));
    }

    // ───────────────────────────── Ações ─────────────────────────────

    private void ExportCsv()
    {
        using var dialog = new SaveFileDialog
        {
            Title = "Exportar histórico do mês",
            Filter = "CSV (*.csv)|*.csv",
            FileName = $"claude-uso-{_month:yyyy-MM}-{FileSafe(_account == null ? "todas" : _history.AccountLabelText(_account).Split('@')[0])}.csv",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        try
        {
            // BOM para o Excel reconhecer UTF-8 (acentos).
            File.WriteAllText(dialog.FileName, _history.ToCsv(_account, _month.Year, _month.Month), new System.Text.UTF8Encoding(true));
            _flash = "Exportado: " + Path.GetFileName(dialog.FileName);
        }
        catch (Exception ex)
        {
            _flash = "Falha ao exportar: " + ex.Message;
        }
        Invalidate();
    }

    private static string FileSafe(string s) =>
        string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '-' : c));

    private static void OpenDataFolder()
    {
        try
        {
            Directory.CreateDirectory(UsageHistory.Folder);
            System.Diagnostics.Process.Start("explorer.exe", $"\"{UsageHistory.Folder}\"");
        }
        catch
        {
            // Sem Explorer.
        }
    }

    // ───────────────────────────── Utilidades ─────────────────────────────

    /// <summary>Arredonda para cima em 1, 2 ou 5 × 10^n, para a escala ficar legível.</summary>
    private static double NiceCeiling(double v)
    {
        double exp = Math.Pow(10, Math.Floor(Math.Log10(v)));
        double f = v / exp;
        double nice = f <= 1 ? 1 : f <= 2 ? 2 : f <= 5 ? 5 : 10;
        return nice * exp;
    }

    private void CreateFonts()
    {
        foreach (var f in new[] { _fTitle, _fBody, _fBodyBold, _fSmall, _fTiny, _fTinyBold, _fCaption, _fStat, _fValue, _fIcon, _fIconSmall })
            f?.Dispose();
        const string ui = "Segoe UI";
        string semibold = FontFamily.Families.Any(f => f.Name == "Segoe UI Semibold") ? "Segoe UI Semibold" : ui;
        _fTitle = new Font(semibold, 20, FontStyle.Regular, GraphicsUnit.Pixel);
        _fBody = new Font(ui, 13, FontStyle.Regular, GraphicsUnit.Pixel);
        _fBodyBold = new Font(semibold, 13, FontStyle.Regular, GraphicsUnit.Pixel);
        _fSmall = new Font(ui, 12, FontStyle.Regular, GraphicsUnit.Pixel);
        _fTiny = new Font(ui, 10.5f, FontStyle.Regular, GraphicsUnit.Pixel);
        _fTinyBold = new Font(semibold, 10.5f, FontStyle.Regular, GraphicsUnit.Pixel);
        _fCaption = new Font(semibold, 10.5f, FontStyle.Regular, GraphicsUnit.Pixel);
        _fStat = new Font(semibold, 21, FontStyle.Regular, GraphicsUnit.Pixel);
        _fValue = new Font(semibold, 15, FontStyle.Regular, GraphicsUnit.Pixel);
        _fIcon = new Font(_iconFamily, 14, FontStyle.Regular, GraphicsUnit.Pixel);
        _fIconSmall = new Font(_iconFamily, 9, FontStyle.Regular, GraphicsUnit.Pixel);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var f in new[] { _fTitle, _fBody, _fBodyBold, _fSmall, _fTiny, _fTinyBold, _fCaption, _fStat, _fValue, _fIcon, _fIconSmall })
                f?.Dispose();
        }
        base.Dispose(disposing);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
