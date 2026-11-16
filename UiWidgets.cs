using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace SimpleVPN
{
    internal static class UiPalette
    {
        internal static bool Dark = true;
        internal static bool HighContrast { get { return SystemInformation.HighContrast; } }
        internal static Color Background { get { return HighContrast ? SystemColors.Window : Dark ? Color.FromArgb(17, 20, 43) : Color.FromArgb(243, 244, 251); } }
        internal static Color Surface { get { return HighContrast ? SystemColors.Window : Dark ? Color.FromArgb(30, 34, 65) : Color.White; } }
        internal static Color Raised { get { return HighContrast ? SystemColors.Window : Dark ? Color.FromArgb(38, 42, 78) : Color.FromArgb(233, 234, 249); } }
        internal static Color Text { get { return HighContrast ? SystemColors.WindowText : Dark ? Color.FromArgb(239, 240, 251) : Color.FromArgb(28, 31, 60); } }
        internal static Color Muted { get { return HighContrast ? SystemColors.WindowText : Dark ? Color.FromArgb(153, 161, 194) : Color.FromArgb(103, 110, 139); } }
        internal static Color Accent { get { return HighContrast ? SystemColors.Highlight : Dark ? Color.FromArgb(147, 124, 255) : Color.FromArgb(104, 76, 222); } }
        internal static Color AccentFill { get { return HighContrast ? SystemColors.Highlight : Color.FromArgb(111, 82, 232); } }
        internal static Color Connected { get { return HighContrast ? SystemColors.Highlight : Dark ? Color.FromArgb(104, 224, 196) : Color.FromArgb(14, 143, 115); } }
        internal static Color Border { get { return HighContrast ? SystemColors.WindowText : Dark ? Color.FromArgb(47, 52, 89) : Color.FromArgb(219, 222, 237); } }
        internal static Color OnAccent { get { return HighContrast ? SystemColors.HighlightText : Color.White; } }
        internal static Color Blend(Color a, Color b, float amount) { return Color.FromArgb((int)(a.R + (b.R - a.R) * amount), (int)(a.G + (b.G - a.G) * amount), (int)(a.B + (b.B - a.B) * amount)); }
    }

    internal static class UiDraw
    {
        internal static Font Font(float size, bool bold) { return new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Point); }
        internal static GraphicsPath Round(RectangleF r, float radius)
        {
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            var p = new GraphicsPath();
            if (d <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90); p.CloseFigure(); return p;
        }
        internal static void Rounded(Graphics g, RectangleF r, float radius, Color fill, Color? border)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            using (var path = Round(r, radius)) { using (var brush = new SolidBrush(fill)) g.FillPath(brush, path); if (border.HasValue) using (var pen = new Pen(border.Value)) g.DrawPath(pen, path); }
        }
        internal static void Text(Graphics g, string value, RectangleF bounds, float size, Color ink, bool bold, StringAlignment alignment)
        {
            using (var font = Font(size, bold)) using (var brush = new SolidBrush(ink)) using (var format = new StringFormat())
            { format.Alignment = alignment; format.LineAlignment = StringAlignment.Center; format.Trimming = StringTrimming.EllipsisCharacter; format.FormatFlags = StringFormatFlags.NoWrap; g.DrawString(value ?? "", font, brush, bounds, format); }
        }
        internal static void Icon(Graphics g, string kind, RectangleF box, Color color)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = Math.Min(box.Width, box.Height), cx = box.X + box.Width / 2, cy = box.Y + box.Height / 2;
            using (var p = new Pen(color, Math.Max(1.6F, s / 13)))
            {
                p.StartCap = p.EndCap = LineCap.Round; p.LineJoin = LineJoin.Round;
                if (kind == "plus") { g.DrawLine(p, cx - s * .28F, cy, cx + s * .28F, cy); g.DrawLine(p, cx, cy - s * .28F, cx, cy + s * .28F); }
                else if (kind == "theme") { g.DrawEllipse(p, cx - s * .3F, cy - s * .3F, s * .6F, s * .6F); using (var brush = new SolidBrush(color)) g.FillPie(brush, cx - s * .3F, cy - s * .3F, s * .6F, s * .6F, 90, 180); }
                else if (kind == "refresh") { g.DrawArc(p, cx - s * .3F, cy - s * .3F, s * .6F, s * .6F, 36, 286); g.DrawLines(p, new[] { new PointF(cx + s * .25F, cy - s * .35F), new PointF(cx + s * .27F, cy - s * .1F), new PointF(cx + s * .02F, cy - s * .12F) }); }
                else if (kind == "ping") { g.DrawArc(p, cx - s * .34F, cy - s * .3F, s * .68F, s * .68F, 140, 260); g.DrawLine(p, cx, cy + s * .02F, cx + s * .2F, cy - s * .22F); }
                else if (kind == "down") g.DrawLines(p, new[] { new PointF(cx - s * .22F, cy - s * .1F), new PointF(cx, cy + s * .12F), new PointF(cx + s * .22F, cy - s * .1F) });
                else if (kind == "right") g.DrawLines(p, new[] { new PointF(cx - s * .09F, cy - s * .22F), new PointF(cx + s * .13F, cy), new PointF(cx - s * .09F, cy + s * .22F) });
                else if (kind == "check") g.DrawLines(p, new[] { new PointF(cx - s * .24F, cy), new PointF(cx - s * .05F, cy + s * .19F), new PointF(cx + s * .28F, cy - s * .2F) });
                else if (kind == "more") { using (var b = new SolidBrush(color)) for (int i = -1; i <= 1; i++) g.FillEllipse(b, cx + i * s * .24F - 1.8F, cy - 1.8F, 3.6F, 3.6F); }
                else if (kind == "link") { g.DrawArc(p, cx - s * .34F, cy - s * .17F, s * .4F, s * .34F, 48, 278); g.DrawArc(p, cx - s * .05F, cy - s * .17F, s * .4F, s * .34F, 230, 278); g.DrawLine(p, cx - s * .13F, cy, cx + s * .13F, cy); }
                else if (kind == "eye") { g.DrawArc(p, cx - s * .32F, cy - s * .18F, s * .64F, s * .36F, 0, 180); g.DrawArc(p, cx - s * .32F, cy - s * .18F, s * .64F, s * .36F, 180, 180); g.DrawEllipse(p, cx - s * .075F, cy - s * .075F, s * .15F, s * .15F); }
                else if (kind == "gear")
                {
                    var points = new PointF[48]; for (int i = 0; i < points.Length; i++) { double a = Math.PI * 2 * i / points.Length; float r = s * (i % 6 < 3 ? .34F : .26F); points[i] = new PointF(cx + (float)Math.Cos(a) * r, cy + (float)Math.Sin(a) * r); }
                    g.DrawPolygon(p, points); g.DrawEllipse(p, cx - s * .11F, cy - s * .11F, s * .22F, s * .22F);
                }
            }
        }
    }

    // A real keyboard-accessible dropdown whose selected value is always painted
    // by the same code on CLR2, newer Windows and windowless release previews.
    internal sealed class ChoiceButton : Button
    {
        readonly string[] choices; int selected;
        internal int SelectedIndex { get { return selected; } set { selected = Math.Max(0, Math.Min(choices.Length - 1, value)); Text = choices[selected]; AccessibleDescription = Text; Invalidate(); } }
        internal ChoiceButton(string[] values, int index)
        {
            choices = (string[])values.Clone(); SelectedIndex = index; Cursor = Cursors.Hand; AccessibleRole = AccessibleRole.ComboBox;
            FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(UiPalette.Background); UiDraw.Rounded(e.Graphics, new RectangleF(.5F, .5F, Width - 1, Height - 1), 8, UiPalette.Surface, UiPalette.Border);
            UiDraw.Text(e.Graphics, choices[selected], new RectangleF(10, 0, Width - 42, Height), 10.5F, Enabled ? UiPalette.Text : UiPalette.Muted, false, StringAlignment.Near);
            UiDraw.Icon(e.Graphics, "down", new RectangleF(Width - 29, (Height - 21) / 2F, 20, 21), UiPalette.Muted);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -3, -3), UiPalette.Text, UiPalette.Surface);
        }
        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e); if (!Enabled) return;
            var menu = new ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = true, BackColor = UiPalette.Surface, ForeColor = UiPalette.Text, Font = UiDraw.Font(10.5F, false) };
            for (int i = 0; i < choices.Length; i++) { int index = i; var item = new ToolStripMenuItem(choices[i]) { Checked = i == selected, BackColor = UiPalette.Surface, ForeColor = UiPalette.Text }; item.Click += delegate { SelectedIndex = index; }; menu.Items.Add(item); }
            menu.Closed += delegate { menu.Dispose(); }; menu.Show(this, new Point(0, Height));
        }
        protected override bool IsInputKey(Keys keyData) { Keys key = keyData & Keys.KeyCode; return key == Keys.Up || key == Keys.Down || base.IsInputKey(keyData); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Down || e.KeyCode == Keys.Up) { if (e.Alt) OnClick(EventArgs.Empty); else SelectedIndex += e.KeyCode == Keys.Down ? 1 : -1; e.Handled = e.SuppressKeyPress = true; return; } base.OnKeyDown(e);
        }
    }

    internal sealed class IconButton : Button
    {
        internal string Kind; internal bool OnCard; private bool hover;
        internal IconButton(string kind, string name)
        {
            Kind = kind; AccessibleName = name; AccessibleRole = AccessibleRole.PushButton; Text = name; Cursor = Cursors.Hand;
            FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            Color bg = OnCard ? UiPalette.Surface : UiPalette.Background; e.Graphics.Clear(bg);
            if (hover && Enabled) UiDraw.Rounded(e.Graphics, new RectangleF(2, 2, Width - 4, Height - 4), 10, UiPalette.Raised, null);
            UiDraw.Icon(e.Graphics, Kind, new RectangleF(Width * .19F, Height * .19F, Width * .62F, Height * .62F), Enabled ? UiPalette.Text : UiPalette.Muted);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -2, -2), UiPalette.Text, bg);
        }
    }

    internal sealed class PowerButton : Button
    {
        internal bool Connected, Busy, CanCancel; private bool hover, pressed;
        internal PowerButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; BackColor = UiPalette.Background; Cursor = Cursors.Hand; AccessibleRole = AccessibleRole.PushButton;
        }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; g.Clear(UiPalette.Background); g.SmoothingMode = SmoothingMode.AntiAlias;
            float size = Math.Min(Width, Height), u = size / 240F, x = (Width - size) / 2, y = (Height - size) / 2;
            Color accent = Connected ? UiPalette.Connected : UiPalette.Accent;
            if (!UiPalette.HighContrast)
                for (int ring = 12; ring >= 1; ring--) { float margin = 16 - ring; Color glow = UiPalette.Blend(UiPalette.Background, accent, .012F + (12 - ring) * .005F); using (var b = new SolidBrush(glow)) g.FillEllipse(b, x + margin * u, y + margin * u, size - margin * 2 * u, size - margin * 2 * u); }
            var circle = new RectangleF(x + 22 * u, y + 22 * u, size - 44 * u, size - 44 * u); if (pressed && Enabled) circle.Offset(0, 2 * u);
            Color outer = Connected ? Color.FromArgb(42, 149, 134) : UiPalette.AccentFill;
            if (hover && Enabled) outer = ControlPaint.Light(outer, .08F);
            if (Busy) outer = UiPalette.Blend(outer, UiPalette.Background, .2F);
            if (UiPalette.HighContrast) outer = SystemColors.Highlight;
            using (var b = new LinearGradientBrush(circle, UiPalette.Blend(outer, Color.White, .2F), outer, 90)) g.FillEllipse(b, circle);
            var inner = RectangleF.Inflate(circle, -10 * u, -10 * u);
            Color center = Connected ? Color.FromArgb(25, 96, 98) : UiPalette.Dark ? Color.FromArgb(53, 44, 122) : Color.FromArgb(94, 66, 210);
            if (UiPalette.HighContrast) center = SystemColors.Highlight;
            using (var b = new LinearGradientBrush(inner, UiPalette.Blend(center, outer, .2F), center, 90)) g.FillEllipse(b, inner);
            using (var p = new Pen(Color.FromArgb(85, accent), 1.1F * u)) g.DrawEllipse(p, circle);
            float cx = x + size / 2, gy = y + 72 * u;
            using (var p = new Pen(UiPalette.OnAccent, 6.5F * u))
            { p.StartCap = p.EndCap = LineCap.Round; if (Busy) { if (CanCancel) { g.DrawLine(p, cx - 17 * u, gy + 4 * u, cx + 17 * u, gy + 38 * u); g.DrawLine(p, cx + 17 * u, gy + 4 * u, cx - 17 * u, gy + 38 * u); } else { using (var b = new SolidBrush(UiPalette.OnAccent)) for (int i = -1; i <= 1; i++) g.FillEllipse(b, cx + i * 21 * u - 4 * u, gy + 17 * u, 8 * u, 8 * u); } }
                else { g.DrawArc(p, cx - 27 * u, gy + 8 * u, 54 * u, 54 * u, -48, 276); g.DrawLine(p, cx, gy - 2 * u, cx, gy + 30 * u); } }
            string title = Busy ? (CanCancel ? "ОТМЕНИТЬ" : "ПОДОЖДИТЕ") : Connected ? "ОТКЛЮЧИТЬ" : "ПОДКЛЮЧИТЬ";
            UiDraw.Text(g, title, new RectangleF(x + 34 * u, y + 149 * u, size - 68 * u, 28 * u), 10.2F * u, UiPalette.OnAccent, true, StringAlignment.Center);
            if (Focused && ShowFocusCues) using (var p = new Pen(accent, 1.5F)) { p.DashStyle = DashStyle.Dot; g.DrawEllipse(p, x + 2 * u, y + 2 * u, size - 4 * u, size - 4 * u); }
        }
    }

    internal sealed class SubscriptionCard : Control
    {
        internal UiState State; internal bool Expanded = true;
        internal event EventHandler ExpandClicked, RefreshClicked, PingClicked, SettingsClicked, WebsiteClicked, SourceClicked;
        readonly IconButton expand, refresh, ping, more, link;
        readonly ToolTip tips = new ToolTip();
        internal SubscriptionCard()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            expand = Add("down", "Свернуть или показать серверы", delegate { if (ExpandClicked != null) ExpandClicked(this, EventArgs.Empty); });
            refresh = Add("refresh", "Обновить подписку", delegate { if (RefreshClicked != null) RefreshClicked(this, EventArgs.Empty); });
            ping = Add("ping", "Проверить задержку серверов", delegate { if (PingClicked != null) PingClicked(this, EventArgs.Empty); });
            more = Add("more", "Настройки подписки", delegate { if (SettingsClicked != null) SettingsClicked(this, EventArgs.Empty); });
            link = Add("link", "Открыть личный кабинет сервиса", delegate { if (WebsiteClicked != null) WebsiteClicked(this, EventArgs.Empty); });
        }
        IconButton Add(string kind, string label, EventHandler action) { var b = new IconButton(kind, label) { OnCard = true, Size = new Size(32, 32) }; b.Click += action; Controls.Add(b); tips.SetToolTip(b, label); return b; }
        protected override void OnResize(EventArgs e) { base.OnResize(e); expand.SetBounds(10, 11, 30, 30); more.SetBounds(Width - 44, 12, 32, 32); ping.SetBounds(Width - 80, 12, 32, 32); refresh.SetBounds(Width - 116, 12, 32, 32); link.SetBounds(Width - 43, Height - 38, 30, 28); }
        internal void Bind(UiState state, bool expanded)
        {
            State = state; Expanded = expanded; expand.Kind = expanded ? "down" : "right";
            refresh.Enabled = !state.Busy && !state.Pinging && !String.IsNullOrEmpty(state.Preferences.SubscriptionUrl); ping.Enabled = !state.Pinging && !state.Busy && state.Servers.Count > 0; more.Enabled = !state.Busy && !state.Pinging;
            link.Visible = UiText.SafeWeb(state.Metadata == null ? null : state.Metadata.WebPageUrl) != null; Invalidate(); foreach (Control c in Controls) c.Invalidate();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(UiPalette.Background); g.SmoothingMode = SmoothingMode.AntiAlias;
            UiDraw.Rounded(g, new RectangleF(.5F, .5F, Width - 1, Height - 1), 16, UiPalette.Surface, UiPalette.Border);
            if (State == null) return;
            var m = State.Metadata;
            string title = UiText.SubscriptionTitle(m == null ? null : m.Title); if (title.Length == 0 && State.Sources.Count > 0) { var source = State.Sources.Find(s => s.Id == State.Preferences.SelectedSourceId); if (source != null) title = UiText.SubscriptionTitle(source.Name); } if (title.Length == 0) title = State.HasSubscription ? "Ваша подписка" : "Добавьте конфигурацию";
            if (State.Sources.Count > 1) title += "  ▾";
            UiDraw.Text(g, title, new RectangleF(43, 10, Math.Max(20, Width - 164), 27), 14, UiPalette.Text, true, StringAlignment.Near);
            string updated = State.Preferences.LastUpdatedUtc == DateTime.MinValue ? "Серверы ещё не загружены" : "Обновлено " + State.Preferences.LastUpdatedUtc.ToLocalTime().ToString("dd.MM, HH:mm", CultureInfo.InvariantCulture);
            if (State.Sources.Count > 1) updated += "  ·  " + UiText.ConfigurationCount(State.Sources.Count);
            UiDraw.Text(g, updated, new RectangleF(44, 36, Width - 60, 22), 8.8F, UiPalette.Muted, false, StringAlignment.Near);
            RectangleF bar = new RectangleF(18, 71, Width - 36, 6);
            UiDraw.Rounded(g, bar, 3, UiPalette.Raised, null);
            double used = m == null ? 0 : Math.Max(0, (double)(m.UploadBytes ?? 0)) + Math.Max(0, (double)(m.DownloadBytes ?? 0));
            if (m != null && m.TotalBytes.HasValue && m.TotalBytes.Value > 0 && m.DownloadBytes.HasValue && m.UploadBytes.HasValue)
            { float ratio = (float)Math.Min(1, used / m.TotalBytes.Value); if (ratio > 0) UiDraw.Rounded(g, new RectangleF(bar.X, bar.Y, Math.Max(4, bar.Width * ratio), bar.Height), 3, UiPalette.Accent, null); }
            string traffic = "Трафик не указан сервисом";
            if (m != null && m.UploadBytes.HasValue && m.DownloadBytes.HasValue) traffic = UiText.Bytes(used) + (m.TotalBytes.HasValue && m.TotalBytes.Value > 0 ? " / " + UiText.Bytes(m.TotalBytes.Value) : " использовано");
            else if (m != null && m.DownloadBytes.HasValue) traffic = "Скачано: " + UiText.Bytes(m.DownloadBytes.Value);
            else if (m != null && m.UploadBytes.HasValue) traffic = "Отправлено: " + UiText.Bytes(m.UploadBytes.Value);
            UiDraw.Text(g, traffic, new RectangleF(18, 83, Width * .53F, 24), 10.2F, UiPalette.Text, false, StringAlignment.Near);
            string expiry = UiText.Expiry(m);
            UiDraw.Text(g, expiry, new RectangleF(18, 105, Width - (link.Visible ? 66 : 34), 22), 9.3F, UiPalette.Muted, false, StringAlignment.Near);
        }
        protected override void OnMouseDown(MouseEventArgs e)
        { base.OnMouseDown(e); if (e.Button == MouseButtons.Left && e.X >= 42 && e.X < Width - 120 && e.Y < 61 && State != null && !State.Busy && !State.Pinging && SourceClicked != null) SourceClicked(this, EventArgs.Empty); }
        protected override void Dispose(bool disposing) { if (disposing) tips.Dispose(); base.Dispose(disposing); }
    }

    internal sealed class ServerListView : ScrollableControl
    {
        internal event Action<ServerProfile> ServerChosen;
        internal event Action<ServerProfile> ServerDetails;
        internal event Action<ServerProfile> ServerRemove;
        List<ServerProfile> rows = new List<ServerProfile>(); Dictionary<string, int> latencies = new Dictionary<string, int>();
        string selected; int cursor = -1; bool canChoose, pinging, collapsed; const int RowHeight = 70;
        internal ServerListView()
        { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true); AutoScroll = true; TabStop = true; AccessibleRole = AccessibleRole.List; AccessibleName = "Серверы VPN. Стрелки выбирают строку, Enter выбирает сервер."; }
        internal void Bind(UiState state, bool expanded)
        {
            string focused = cursor >= 0 && cursor < rows.Count ? rows[cursor].Id : state.Preferences.SelectedServerId;
            rows = new List<ServerProfile>(state.Servers); latencies = state.Latencies; selected = state.Preferences.SelectedServerId; canChoose = !state.Busy; pinging = state.Pinging; collapsed = !expanded;
            if (state.Preferences.SortByLatency)
            {
                var originalOrder = new Dictionary<string, int>(); for (int i = 0; i < state.Servers.Count; i++) originalOrder[state.Servers[i].Id] = i;
                rows.Sort(delegate(ServerProfile a, ServerProfile b) { int x, y; if (!latencies.TryGetValue(a.Id, out x) || x < 0) x = Int32.MaxValue; if (!latencies.TryGetValue(b.Id, out y) || y < 0) y = Int32.MaxValue; int result = x.CompareTo(y); return result == 0 ? originalOrder[a.Id].CompareTo(originalOrder[b.Id]) : result; });
            }
            cursor = rows.FindIndex(s => s.Id == focused); if (cursor < 0 && rows.Count > 0) cursor = 0;
            AutoScrollMinSize = new Size(0, collapsed ? 0 : rows.Count * RowHeight); Invalidate();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; g.Clear(UiPalette.Background); g.SmoothingMode = SmoothingMode.AntiAlias;
            if (collapsed || rows.Count == 0)
            { UiDraw.Text(g, collapsed ? "Список серверов скрыт" : "Здесь появятся ваши серверы", new RectangleF(12, 16, Width - 24, 35), 12, UiPalette.Muted, false, StringAlignment.Center); UiDraw.Text(g, collapsed ? "Нажмите стрелку в карточке подписки" : "Нажмите + и добавьте ссылку подписки", new RectangleF(12, 47, Width - 24, 28), 9.5F, UiPalette.Muted, false, StringAlignment.Center); return; }
            int width = ClientSize.Width;
            for (int i = 0; i < rows.Count; i++)
            {
                float y = i * RowHeight + AutoScrollPosition.Y; if (y + RowHeight < 0 || y > Height) continue;
                var row = rows[i]; bool active = row.Id == selected; Color background = active ? UiPalette.Raised : UiPalette.Surface;
                UiDraw.Rounded(g, new RectangleF(0, y + 2, width - 1, RowHeight - 5), 10, background, active ? (Color?)UiPalette.Accent : null);
                if (active) UiDraw.Rounded(g, new RectangleF(0, y + 17, 3, 34), 1, UiPalette.Accent, null);
                FlagArtwork.Draw(g, new RectangleF(16, y + 21, 31, 23), row.Name);
                int latency; string delay = latencies.TryGetValue(row.Id, out latency) ? latency < 0 ? "Нет ответа" : Math.Max(1, latency) + " мс" : pinging ? "Проверка" : "—";
                Color latencyColor = latencies.TryGetValue(row.Id, out latency) && latency >= 0 ? (latency <= 150 ? UiPalette.Connected : UiPalette.Text) : UiPalette.Muted;
                UiDraw.Text(g, UiText.CleanName(row.Name, true), new RectangleF(59, y + 9, Math.Max(40, width - 172), 29), 12.3F, UiPalette.Text, active, StringAlignment.Near);
                UiDraw.Text(g, UiText.Protocol(row), new RectangleF(60, y + 36, Math.Max(40, width - 179), 23), 8.8F, UiPalette.Muted, false, StringAlignment.Near);
                UiDraw.Text(g, delay, new RectangleF(width - 119, y + 15, 78, 30), 9.4F, latencyColor, false, StringAlignment.Far);
                UiDraw.Icon(g, active ? "check" : "right", new RectangleF(width - 31, y + 22, 20, 22), active ? UiPalette.Accent : UiPalette.Muted);
                if (Focused && ShowFocusCues && cursor == i) ControlPaint.DrawFocusRectangle(g, new Rectangle(5, (int)y + 6, width - 12, RowHeight - 13), UiPalette.Text, background);
            }
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e); if (collapsed) return; int index = (e.Y - AutoScrollPosition.Y) / RowHeight; if (index < 0 || index >= rows.Count) return; Focus(); cursor = index; Invalidate();
            if (e.Button == MouseButtons.Right) { ShowMenu(rows[index], e.Location); return; }
            if (e.Button != MouseButtons.Left) return;
            if (e.X >= ClientSize.Width - 37) ShowMenu(rows[index], e.Location);
            else if (canChoose && ServerChosen != null) ServerChosen(rows[index]);
        }
        void ShowMenu(ServerProfile server, Point location)
        {
            var menu = new ContextMenuStrip { ShowImageMargin = false, BackColor = UiPalette.Surface, ForeColor = UiPalette.Text, Font = UiDraw.Font(10.5F, false) };
            var choose = new ToolStripMenuItem("Выбрать сервер") { Enabled = canChoose }; choose.Click += delegate { if (ServerChosen != null) ServerChosen(server); }; menu.Items.Add(choose);
            var details = new ToolStripMenuItem("О сервере"); details.Click += delegate { if (ServerDetails != null) ServerDetails(server); }; menu.Items.Add(details);
            var remove = new ToolStripMenuItem("Удалить из списка…") { Enabled = canChoose && !pinging }; remove.Click += delegate { if (ServerRemove != null) ServerRemove(server); }; menu.Items.Add(remove);
            menu.Closed += delegate { menu.Dispose(); }; menu.Show(this, location);
        }
        protected override bool IsInputKey(Keys keyData) { Keys key = keyData & Keys.KeyCode; return key == Keys.Up || key == Keys.Down || key == Keys.Home || key == Keys.End || base.IsInputKey(keyData); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e); if (rows.Count == 0 || collapsed) return;
            if (e.KeyCode == Keys.Up) cursor = Math.Max(0, cursor - 1); else if (e.KeyCode == Keys.Down) cursor = Math.Min(rows.Count - 1, cursor + 1); else if (e.KeyCode == Keys.Home) cursor = 0; else if (e.KeyCode == Keys.End) cursor = rows.Count - 1;
            else if ((e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) && canChoose && cursor >= 0) { if (ServerChosen != null) ServerChosen(rows[cursor]); }
            else if (e.KeyCode == Keys.Delete && canChoose && !pinging && cursor >= 0) { if (ServerRemove != null) ServerRemove(rows[cursor]); }
            else if (e.KeyCode == Keys.Apps && cursor >= 0) { ShowMenu(rows[cursor], new Point(20, Math.Max(0, cursor * RowHeight + AutoScrollPosition.Y))); }
            else return;
            int top = cursor * RowHeight, visibleTop = -AutoScrollPosition.Y; if (top < visibleTop) AutoScrollPosition = new Point(0, top); else if (top + RowHeight > visibleTop + ClientSize.Height) AutoScrollPosition = new Point(0, top + RowHeight - ClientSize.Height); e.Handled = true; Invalidate();
        }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    }

    internal static class FlagArtwork
    {
        internal static void Draw(Graphics g, RectangleF r, string name)
        {
            string key = (name ?? "").ToLowerInvariant(); string country = Country(key);
            GraphicsState state = g.Save(); using (var p = UiDraw.Round(r, 5)) g.SetClip(p);
            Color[] colors = null; bool vertical = false;
            if (country == "ru") colors = new[] { Color.White, Color.FromArgb(49, 99, 217), Color.FromArgb(226, 62, 83) };
            if (country == "nl") colors = new[] { Color.FromArgb(205, 55, 73), Color.White, Color.FromArgb(49, 84, 157) };
            if (country == "de") colors = new[] { Color.FromArgb(27, 29, 34), Color.FromArgb(214, 51, 68), Color.FromArgb(255, 205, 68) };
            if (country == "fr") { colors = new[] { Color.FromArgb(49, 89, 180), Color.White, Color.FromArgb(231, 70, 77) }; vertical = true; }
            if (country == "it") { colors = new[] { Color.FromArgb(36, 155, 100), Color.White, Color.FromArgb(222, 68, 73) }; vertical = true; }
            if (country == "pl") colors = new[] { Color.White, Color.FromArgb(220, 56, 89) };
            if (colors != null) for (int i = 0; i < colors.Length; i++) using (var b = new SolidBrush(colors[i])) g.FillRectangle(b, vertical ? new RectangleF(r.X + r.Width * i / colors.Length, r.Y, r.Width / colors.Length + 1, r.Height) : new RectangleF(r.X, r.Y + r.Height * i / colors.Length, r.Width, r.Height / colors.Length + 1));
            else if (country == "fi" || country == "se")
            { using (var b = new SolidBrush(country == "fi" ? Color.White : Color.FromArgb(39, 107, 172))) g.FillRectangle(b, r); using (var b = new SolidBrush(country == "fi" ? Color.FromArgb(56, 95, 167) : Color.FromArgb(249, 204, 63))) { g.FillRectangle(b, r.X + r.Width * .3F, r.Y, r.Width * .18F, r.Height); g.FillRectangle(b, r.X, r.Y + r.Height * .4F, r.Width, r.Height * .23F); } }
            else if (country == "us")
            { for (int i = 0; i < 7; i++) using (var b = new SolidBrush(i % 2 == 0 ? Color.FromArgb(211, 67, 86) : Color.White)) g.FillRectangle(b, r.X, r.Y + r.Height * i / 7, r.Width, r.Height / 7 + 1); using (var b = new SolidBrush(Color.FromArgb(58, 80, 139))) g.FillRectangle(b, r.X, r.Y, r.Width * .46F, r.Height * .57F); }
            else
            { using (var b = new SolidBrush(UiPalette.Raised)) g.FillRectangle(b, r); using (var p = new Pen(UiPalette.Muted, 1.1F)) { g.DrawEllipse(p, r.X + 6, r.Y + 2, r.Width - 12, r.Height - 4); g.DrawEllipse(p, r.X + 11, r.Y + 2, r.Width - 22, r.Height - 4); g.DrawLine(p, r.X + 6, r.Y + r.Height / 2, r.Right - 6, r.Y + r.Height / 2); } }
            g.Restore(state);
        }
        static string Country(string name)
        {
            for (int i = 0; i + 3 < name.Length; i++) if (Char.IsHighSurrogate(name[i]) && Char.IsLowSurrogate(name[i + 1]) && Char.IsHighSurrogate(name[i + 2]) && Char.IsLowSurrogate(name[i + 3])) { int a = Char.ConvertToUtf32(name, i) - 0x1f1e6, b = Char.ConvertToUtf32(name, i + 2) - 0x1f1e6; if (a >= 0 && a < 26 && b >= 0 && b < 26) return new string(new[] { (char)('a' + a), (char)('a' + b) }); }
            string[] codes = { "ru", "nl", "de", "fr", "fi", "se", "us", "it", "pl" };
            string[] native = { "росси", "нидерланд", "германи", "франци", "финлянд", "швеци", "сша", "итали", "польш" };
            string[] english = { "russia", "netherland", "germany", "france", "finland", "sweden", "united states", "italy", "poland" };
            for (int i = 0; i < codes.Length; i++) if (name.IndexOf(native[i], StringComparison.Ordinal) >= 0 || name.IndexOf(english[i], StringComparison.Ordinal) >= 0) return codes[i]; return "";
        }
    }

    internal static class UiText
    {
        // Subscription decorations must not depend on emoji fonts absent in Windows 7.
        // This is display-only: keep the provider's original metadata unchanged.
        internal static string SubscriptionTitle(string value)
        {
            if (String.IsNullOrEmpty(value)) return "";
            var result = new StringBuilder(); bool separator = false;
            for (int i = 0; i < value.Length; i++)
            {
                char first = value[i]; int code = first; int length = 1;
                if (Char.IsHighSurrogate(first) && i + 1 < value.Length && Char.IsLowSurrogate(value[i + 1]))
                { code = Char.ConvertToUtf32(first, value[i + 1]); length = 2; }
                else if (Char.IsSurrogate(first)) { separator = true; continue; }
                if (code == 0x200d || code == 0x20e3 || (code >= 0xfe00 && code <= 0xfe0f) ||
                    (code >= 0xe0020 && code <= 0xe007f) || (code >= 0xe0100 && code <= 0xe01ef))
                { i += length - 1; continue; }
                bool pictograph = (code >= 0x2600 && code <= 0x27bf) || (code >= 0x1f000 && code <= 0x1faff) ||
                    (code >= 0x2194 && code <= 0x2199) || code == 0x21a9 || code == 0x21aa ||
                    code == 0x231a || code == 0x231b || code == 0x2328 || code == 0x23cf ||
                    (code >= 0x23e9 && code <= 0x23f3) || (code >= 0x23f8 && code <= 0x23fa) ||
                    code == 0x24c2 || code == 0x25aa || code == 0x25ab || code == 0x25b6 || code == 0x25c0 ||
                    (code >= 0x25fb && code <= 0x25fe) || code == 0x2934 || code == 0x2935 ||
                    (code >= 0x2b05 && code <= 0x2b07) || code == 0x2b1b || code == 0x2b1c ||
                    code == 0x2b50 || code == 0x2b55 || code == 0x3030 || code == 0x303d ||
                    code == 0x3297 || code == 0x3299;
                if (pictograph || Char.IsWhiteSpace(first) || Char.IsControl(first))
                { separator = true; i += length - 1; continue; }
                if (separator && result.Length > 0) result.Append(' ');
                separator = false;
                result.Append(value, i, length); i += length - 1;
            }
            return result.ToString();
        }
        internal static string CleanName(string value, bool fallback)
        { if (String.IsNullOrEmpty(value)) return fallback ? "Сервер" : ""; var b = new StringBuilder(); foreach (char c in value) if (!Char.IsSurrogate(c) && !Char.IsControl(c) && c != '\ufe0f' && c != '\u200d') b.Append(c); string result = b.ToString().Trim(); return result.Length == 0 && fallback ? "Сервер" : result; }
        // VPN clients conventionally label binary gigabytes as GB. Use the same
        // 1024 divisor as the provider's mobile client instead of inflating by 7.4%.
        internal static string Bytes(double count) { string[] units = { "Б", "КБ", "МБ", "ГБ", "ТБ" }; int i = 0; while (count >= 1024 && i < units.Length - 1) { count /= 1024; i++; } return count.ToString(i == 0 ? "0" : "0.#", CultureInfo.GetCultureInfo("ru-RU")) + " " + units[i]; }
        internal static string ConfigurationCount(int count) { int tail = count % 100, digit = count % 10; string word = tail >= 11 && tail <= 14 ? "конфигураций" : digit == 1 ? "конфигурация" : digit >= 2 && digit <= 4 ? "конфигурации" : "конфигураций"; return count.ToString(CultureInfo.InvariantCulture) + " " + word; }
        internal static string Expiry(SubscriptionMetadata m)
        { return Expiry(m, DateTime.UtcNow); }
        internal static string Expiry(SubscriptionMetadata m, DateTime utcNow)
        {
            if (m == null || !m.ExpireUnixSeconds.HasValue || m.ExpireUnixSeconds.Value <= 0) return "Срок действия не указан сервисом";
            try { DateTime date = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(m.ExpireUnixSeconds.Value); int days = (int)Math.Ceiling((date - utcNow).TotalDays); return days <= 0 ? "Срок подписки истёк " + date.ToLocalTime().ToString("dd.MM.yyyy") : "До " + date.ToLocalTime().ToString("dd.MM.yyyy") + "  ·  осталось " + days + " дн."; } catch (ArgumentOutOfRangeException) { return "Срок действия не указан сервисом"; }
        }
        internal static string SafeWeb(string text) { Uri uri; return Uri.TryCreate(text, UriKind.Absolute, out uri) && uri.Scheme == "https" && String.IsNullOrEmpty(uri.UserInfo) ? uri.AbsoluteUri : null; }
        internal static string Protocol(ServerProfile profile)
        {
            string protocol = (profile.Protocol ?? "VPN").ToUpperInvariant(); if (protocol == "SHADOWSOCKS") protocol = "Shadowsocks";
            if (profile.Outbound == null) return protocol; object streamValue; if (!profile.Outbound.TryGetValue("streamSettings", out streamValue)) return protocol;
            var stream = streamValue as Dictionary<string, object>; if (stream == null) return protocol;
            object network, security; if (stream.TryGetValue("network", out network) && network is string) protocol += "  /  " + ((string)network).ToUpperInvariant(); if (stream.TryGetValue("security", out security) && security is string && (string)security != "none") protocol += "  /  " + ((string)security).ToUpperInvariant(); return protocol;
        }
    }

    internal static class AppArtwork
    {
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);
        internal static Icon CreateIcon()
        {
            using (var image = new Bitmap(64, 64, PixelFormat.Format32bppArgb)) using (var g = Graphics.FromImage(image))
            { g.SmoothingMode = SmoothingMode.AntiAlias; using (var b = new SolidBrush(Color.FromArgb(111, 82, 232))) g.FillEllipse(b, 2, 2, 60, 60); using (var p = new Pen(Color.White, 4.5F)) { p.StartCap = p.EndCap = LineCap.Round; g.DrawArc(p, 17, 19, 30, 30, -48, 276); g.DrawLine(p, 32, 14, 32, 33); } IntPtr icon = image.GetHicon(); try { using (var owned = Icon.FromHandle(icon)) return (Icon)owned.Clone(); } finally { DestroyIcon(icon); } }
        }
    }
}
