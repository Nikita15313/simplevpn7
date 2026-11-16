using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Windows.Forms;

namespace SimpleVPN
{
    internal sealed class UiState
    {
        internal AppSettings Preferences; internal SubscriptionMetadata Metadata;
        internal List<ServerProfile> Servers = new List<ServerProfile>(); internal Dictionary<string, int> Latencies = new Dictionary<string, int>();
        internal List<ConfigSource> Sources = new List<ConfigSource>();
        internal string Status, Detail, Notice; internal bool Connected, Busy, Pinging, CanCancel;
        internal bool HasSubscription { get { return Servers.Count > 0 || !String.IsNullOrEmpty(Preferences.SubscriptionUrl); } }
        internal static UiState Read(AppController controller)
        { return new UiState { Preferences = controller.CopySettings(), Metadata = controller.Metadata, Sources = new List<ConfigSource>(controller.Sources), Servers = new List<ServerProfile>(controller.Servers), Latencies = controller.Latencies, Status = controller.StatusText, Detail = controller.DetailText, Notice = controller.Notice, Connected = controller.IsConnected, Busy = controller.IsBusy, Pinging = controller.IsPinging, CanCancel = controller.CanCancelConnection }; }
        internal static UiState Sample(bool connected, bool light)
        {
            var metadata = new SubscriptionMetadata { Title = "VPN BAZA", UploadBytes = 1181116006L, DownloadBytes = 7279960064L, TotalBytes = 536870912000L, ExpireUnixSeconds = (long)(DateTime.UtcNow.AddDays(140) - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds, RetrievedUtc = DateTime.UtcNow, UpdateIntervalHours = 4 };
            var settings = new AppSettings { SelectedServerId = "demo-0", Theme = light ? "light" : "dark", LastUpdatedUtc = DateTime.UtcNow, UpdateIntervalHours = 4, Metadata = metadata };
            var state = new UiState { Preferences = settings, Metadata = metadata, Connected = connected, Status = connected ? "Подключено" : "Готово к подключению", Detail = connected ? "Нидерланды · системный прокси" : "Выбран сервер: Нидерланды", Notice = "" };
            settings.SelectedSourceId = "sample-source";
            state.Sources.Add(new ConfigSource { Id = "sample-source", Name = "VPN BAZA", Metadata = metadata, LastUpdatedUtc = DateTime.UtcNow });
            state.Sources.Add(new ConfigSource { Id = "sample-manual", Name = "Мои конфиги" });
            string[] names = { "Нидерланды", "Германия", "Франция", "Финляндия", "Россия · YouTube", "США", "Швеция", "Польша" }; int[] times = { 47, 62, 79, 91, 34, 140, 102, -1 };
            for (int i = 0; i < names.Length; i++) { string id = "demo-" + i; state.Servers.Add(new ServerProfile { Id = id, Name = names[i], Protocol = "vless", Outbound = new Dictionary<string, object> { { "streamSettings", new Dictionary<string, object> { { "network", "tcp" }, { "security", "reality" } } } } }); state.Latencies[id] = times[i]; }
            return state;
        }
    }

    public sealed class MainForm : Form
    {
        readonly AppController controller; readonly bool preview; readonly TableLayoutPanel root, header;
        readonly PowerButton power; readonly IconButton settings, add, theme; readonly SubscriptionCard card; readonly ServerListView servers;
        readonly Label title, subtitle, status, detail, notice, count, footer; readonly TextButton sort;
        readonly NotifyIcon tray; readonly ContextMenuStrip trayMenu; readonly ToolStripMenuItem trayToggle; readonly ToolTip tips;
        readonly Icon artwork; UiState state; bool closing, forceClose, localBusy, expanded = true, trayHint;
        public MainForm(AppController value) : this(value, false) { }
        public MainForm(AppController value, bool isPreview)
        {
            if (value == null && !isPreview) throw new ArgumentNullException("value"); controller = value; preview = isPreview;
            state = preview ? UiState.Sample(false, false) : UiState.Read(controller); UiPalette.Dark = state.Preferences.Theme != "light";
            Text = "SimpleVPN7"; Font = UiDraw.Font(11, false); AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(540, 740); MinimumSize = new Size(460, 600); StartPosition = FormStartPosition.CenterScreen;
            BackColor = UiPalette.Background; ForeColor = UiPalette.Text; artwork = AppArtwork.CreateIcon(); Icon = artwork;
            root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new Padding(20, 10, 20, 10), Margin = Padding.Empty };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 190)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 61)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 138)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 27)); Controls.Add(root);
            header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = Padding.Empty };
            header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42)); header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42)); header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42));
            settings = new IconButton("gear", "Настройки") { Dock = DockStyle.Fill, Margin = Padding.Empty, TabIndex = 3 }; settings.Click += delegate { OpenSettings(0); }; header.Controls.Add(settings, 0, 0);
            var branding = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty }; title = Label("SimpleVPN7", 17, true, ContentAlignment.MiddleCenter); title.Dock = DockStyle.Top; title.Height = 29;
            subtitle = Label("ВАШЕ ЛИЧНОЕ ПОДКЛЮЧЕНИЕ", 7.2F, false, ContentAlignment.MiddleCenter); subtitle.Tag = "muted"; subtitle.Dock = DockStyle.Fill; branding.Controls.Add(subtitle); branding.Controls.Add(title); header.Controls.Add(branding, 1, 0);
            theme = new IconButton("theme", "Сменить тёмную или светлую тему") { Dock = DockStyle.Fill, Margin = Padding.Empty, TabIndex = 4 }; theme.Click += delegate { ToggleTheme(); }; header.Controls.Add(theme, 2, 0);
            add = new IconButton("plus", "Добавить подписку или конфиги") { Dock = DockStyle.Fill, Margin = Padding.Empty, TabIndex = 5 }; add.Click += delegate { AddConfiguration(); }; header.Controls.Add(add, 3, 0); root.Controls.Add(header, 0, 0);
            power = new PowerButton { Dock = DockStyle.Fill, Margin = Padding.Empty, TabIndex = 0 }; power.Click += delegate { ToggleConnection(); }; root.Controls.Add(power, 0, 1);
            var statusPanel = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            status = Label("", 17, true, ContentAlignment.MiddleCenter); status.SetBounds(0, 0, 490, 28); status.Dock = DockStyle.Top;
            detail = Label("", 9.6F, false, ContentAlignment.MiddleCenter); detail.Tag = "muted"; detail.Dock = DockStyle.Top; detail.Height = 23;
            notice = Label("", 9.1F, false, ContentAlignment.MiddleCenter); notice.Tag = "muted"; notice.Dock = DockStyle.Fill; notice.AutoEllipsis = true;
            statusPanel.Controls.Add(notice); statusPanel.Controls.Add(detail); statusPanel.Controls.Add(status); root.Controls.Add(statusPanel, 0, 2);
            card = new SubscriptionCard { Dock = DockStyle.Fill, Margin = Padding.Empty, TabIndex = 6 }; card.ExpandClicked += delegate { expanded = !expanded; ApplyState(); }; card.RefreshClicked += delegate { RefreshSubscription(); }; card.PingClicked += delegate { PingServers(); }; card.SettingsClicked += delegate { ShowSourceMenu(); }; card.SourceClicked += delegate { ShowSourceMenu(); }; card.WebsiteClicked += delegate { OpenWebsite(state.Metadata == null ? null : state.Metadata.WebPageUrl); }; root.Controls.Add(card, 0, 3);
            var toolbar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty }; toolbar.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 138));
            count = Label("СЕРВЕРЫ", 8.5F, true, ContentAlignment.MiddleLeft); count.Tag = "muted"; count.Dock = DockStyle.Fill; toolbar.Controls.Add(count, 0, 0);
            sort = new TextButton("По скорости", false) { Dock = DockStyle.Fill, Margin = new Padding(0, 5, 0, 4), TabIndex = 6, Compact = true }; sort.Click += delegate { SortServers(); }; toolbar.Controls.Add(sort, 1, 0); root.Controls.Add(toolbar, 0, 4);
            servers = new ServerListView { Dock = DockStyle.Fill, Margin = Padding.Empty, TabIndex = 1 }; servers.ServerChosen += delegate(ServerProfile server) { if (!preview) controller.SelectServer(server); }; servers.ServerDetails += ShowServerDetails; servers.ServerRemove += RemoveServer; root.Controls.Add(servers, 0, 5);
            footer = Label("Системный прокси Windows  ·  Xray", 8.1F, false, ContentAlignment.MiddleCenter); footer.Tag = "muted"; footer.Dock = DockStyle.Fill; root.Controls.Add(footer, 0, 6);
            tips = new ToolTip(); tips.SetToolTip(settings, "Все настройки приложения"); tips.SetToolTip(add, "Добавить новую подписку, ссылку сервера или файл конфигурации"); tips.SetToolTip(theme, "Сменить тёмную или светлую тему"); tips.SetToolTip(sort, "Сортировать по измеренной задержке; непроверенные серверы будут ниже");
            if (!preview)
            {
                trayMenu = new ContextMenuStrip(); var open = new ToolStripMenuItem("Открыть SimpleVPN7"); open.Click += delegate { RestoreWindow(); }; trayToggle = new ToolStripMenuItem("Подключить"); trayToggle.Click += delegate { ToggleConnection(); }; var exit = new ToolStripMenuItem("Выход — отключить VPN"); exit.Click += delegate { forceClose = true; Close(); }; trayMenu.Items.Add(open); trayMenu.Items.Add(trayToggle); trayMenu.Items.Add(new ToolStripSeparator()); trayMenu.Items.Add(exit);
                tray = new NotifyIcon { Icon = artwork, Text = "SimpleVPN7", ContextMenuStrip = trayMenu, Visible = true }; tray.DoubleClick += delegate { RestoreWindow(); };
                controller.Changed += ControllerChanged; Shown += FirstShown;
            }
            Resize += delegate { if (root != null) { root.RowStyles[1].Height = ClientSize.Height < 710 ? 178 : ClientSize.Height < 790 ? 196 : 222; root.PerformLayout(); } };
            ApplyState();
        }
        static Label Label(string text, float size, bool bold, ContentAlignment alignment) { return new Label { Text = text, Font = UiDraw.Font(size, bold), TextAlign = alignment, AutoEllipsis = true, Margin = Padding.Empty }; }
        void FirstShown(object sender, EventArgs args)
        {
            Shown -= FirstShown; Rectangle area = Screen.FromControl(this).WorkingArea; int available = Math.Max(570, area.Height - 66); if (ClientSize.Height > available) ClientSize = new Size(ClientSize.Width, available); if (Bottom > area.Bottom) Top = Math.Max(area.Top, area.Bottom - Height);
            localBusy = true; ApplyState(); controller.InitializeAsync().OnCompleted(delegate(Exception error) { localBusy = false; if (closing || IsDisposed) return; if (error != null) ShowError("Не удалось подготовить приложение. Откройте его ещё раз."); ApplyState(); });
        }
        void ControllerChanged(object sender, EventArgs args) { if (closing || IsDisposed) return; if (InvokeRequired) { try { BeginInvoke(new MethodInvoker(ApplyState)); } catch (InvalidOperationException) { } } else ApplyState(); }
        void ApplyState()
        {
            if (closing || IsDisposed) return; if (!preview) state = UiState.Read(controller); state.Busy = state.Busy || localBusy;
            bool darkTheme = state.Preferences.Theme != "light"; if (UiPalette.Dark != darkTheme) UiPalette.Dark = darkTheme;
            UiStyles.Apply(this); power.Connected = state.Connected; power.Busy = state.Busy; power.CanCancel = state.CanCancel; power.Enabled = !state.Busy || state.CanCancel;
            power.AccessibleName = state.CanCancel ? "Отменить подключение" : state.Connected ? "Отключить VPN" : "Подключить VPN";
            status.Text = state.Status ?? "Отключено"; status.ForeColor = state.Connected ? UiPalette.Connected : UiPalette.Text; detail.Text = UiText.CleanName(state.Detail, false); notice.Text = state.Notice ?? ""; tips.SetToolTip(notice, notice.Text);
            bool hasNotice = !String.IsNullOrEmpty(notice.Text); root.RowStyles[2].Height = hasNotice ? 82 : 58; notice.Visible = hasNotice;
            settings.Enabled = theme.Enabled = !state.Busy; add.Enabled = !state.Busy && !state.Pinging; card.Bind(state, expanded); servers.Bind(state, expanded);
            count.Text = state.Pinging ? "ПРОВЕРЯЕМ СЕРВЕРЫ…" : "СЕРВЕРЫ  /  " + state.Servers.Count; sort.Selected = state.Preferences.SortByLatency; sort.Enabled = !state.Busy && state.Servers.Count > 0; sort.Invalidate();
            if (tray != null) { tray.Text = "SimpleVPN7 — " + (state.Connected ? "подключено" : state.Busy ? "подождите" : "отключено"); trayToggle.Text = state.CanCancel ? "Отменить подключение" : state.Connected ? "Отключить" : "Подключить"; trayToggle.Enabled = power.Enabled; }
            power.Invalidate(); card.Invalidate(); root.PerformLayout();
        }
        void ToggleConnection()
        {
            if (preview || closing) return; if (controller.CanCancelConnection) { controller.CancelConnection(); return; } if (localBusy || controller.IsBusy) return;
            if (!controller.IsConnected && controller.Servers.Count == 0) { AddConfiguration(); return; }
            localBusy = true; ApplyState(); controller.ToggleAsync().OnCompleted(delegate(Exception error) { localBusy = false; if (closing || IsDisposed) return; if (error != null) ShowError("Не удалось подключиться. Выберите другой сервер и попробуйте снова."); ApplyState(); });
        }
        void RefreshSubscription() { if (preview || controller.IsBusy || controller.IsPinging) return; controller.RefreshAsync().OnCompleted(delegate(Exception error) { if (closing || IsDisposed) return; if (error != null) ShowError("Не удалось обновить подписку. Проверьте интернет."); ApplyState(); }); }
        void PingServers() { if (preview || controller.IsPinging || controller.IsBusy) return; controller.PingAllAsync().OnCompleted(delegate(Exception error) { if (closing || IsDisposed) return; if (error != null) ShowError("Не удалось проверить серверы. Попробуйте ещё раз."); ApplyState(); }); }
        void SortServers() { if (preview || controller.IsBusy) return; AppSettings copy = controller.CopySettings(); copy.SortByLatency = !copy.SortByLatency; controller.SavePreferencesAsync(copy).OnCompleted(delegate(Exception error) { if (closing || IsDisposed) return; if (error != null) ShowError("Не удалось сохранить порядок серверов."); ApplyState(); }); }
        void OpenSettings(int section) { if (preview || closing || localBusy || controller.IsBusy) return; using (var dialog = new SettingsDialog(controller, artwork, section)) dialog.ShowDialog(this); ApplyState(); }
        void ToggleTheme()
        {
            if (closing || localBusy || (!preview && controller.IsBusy)) return;
            if (preview) { state.Preferences.Theme = state.Preferences.Theme == "light" ? "dark" : "light"; ApplyState(); return; }
            var chosen = controller.CopySettings(); chosen.Theme = chosen.Theme == "light" ? "dark" : "light";
            controller.SavePreferencesAsync(chosen).OnCompleted(delegate(Exception error) { if (closing || IsDisposed) return; if (error != null) ShowError("Не удалось сохранить тему."); ApplyState(); });
        }
        void AddConfiguration()
        {
            if (preview || closing || localBusy || controller.IsBusy || controller.IsPinging) return;
            using (var dialog = new ConfigImportDialog(artwork))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                localBusy = true; ApplyState();
                controller.AddSourceAsync(dialog.ConfigurationName, dialog.ConfigurationText).OnCompleted(delegate(Exception error) { localBusy = false; if (closing || IsDisposed) return; if (error != null) ShowError("Не удалось добавить конфигурацию. Проверьте ссылку или содержимое файла."); ApplyState(); });
            }
        }
        ContextMenuStrip CreateSourceMenu()
        { return new ContextMenuStrip { ShowImageMargin = false, BackColor = UiPalette.Surface, ForeColor = UiPalette.Text, Font = UiDraw.Font(10.5F, false) }; }
        void ShowSourceMenu()
        {
            if (preview || closing || localBusy || controller.IsBusy || controller.IsPinging) return;
            var menu = CreateSourceMenu();
            foreach (var source in controller.Sources)
            {
                string id = source.Id; string name = UiText.SubscriptionTitle(source.Name); if (name.Length == 0) name = "Мои конфиги";
                var item = new ToolStripMenuItem(name) { Checked = id == controller.Settings.SelectedSourceId };
                item.Click += delegate { controller.SelectSourceAsync(id).OnCompleted(delegate(Exception error) { if (closing || IsDisposed) return; if (error != null) ShowError("Не удалось выбрать конфигурацию."); ApplyState(); }); }; menu.Items.Add(item);
            }
            if (menu.Items.Count > 0) menu.Items.Add(new ToolStripSeparator());
            var addItem = new ToolStripMenuItem("Добавить подписку или конфиги…"); addItem.Click += delegate { AddConfiguration(); }; menu.Items.Add(addItem);
            if (controller.Sources.Count > 0)
            {
                var options = new ToolStripMenuItem("Настройки выбранной подписки…"); options.Click += delegate { OpenSettings(1); }; menu.Items.Add(options);
                var restore = new ToolStripMenuItem("Вернуть удалённые серверы"); restore.Click += delegate { controller.RestoreServersAsync().OnCompleted(delegate(Exception error) { if (closing || IsDisposed) return; if (error != null) ShowError("Не удалось вернуть серверы."); ApplyState(); }); }; menu.Items.Add(restore);
                var remove = new ToolStripMenuItem("Удалить выбранную конфигурацию…"); remove.Click += delegate { DeleteConfiguration(); }; menu.Items.Add(remove);
            }
            menu.Closed += delegate { menu.Dispose(); }; menu.Show(card, new Point(Math.Max(0, card.Width - 280), 44));
        }
        void DeleteConfiguration()
        {
            if (preview || controller.IsBusy || controller.IsPinging) return;
            if (MessageBox.Show(this, "Удалить выбранную конфигурацию и её список серверов?", "Удалить конфигурацию", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            controller.DeleteSourceAsync(controller.Settings.SelectedSourceId).OnCompleted(delegate(Exception error) { if (closing || IsDisposed) return; if (error != null) ShowError("Не удалось удалить конфигурацию."); ApplyState(); });
        }
        void RemoveServer(ServerProfile server)
        {
            if (preview || controller.IsBusy || controller.IsPinging || server == null) return;
            if (MessageBox.Show(this, "Удалить «" + UiText.CleanName(server.Name, true) + "» из этого списка?\n\nВернуть сервер можно через меню подписки.", "Удалить сервер", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            controller.RemoveServerAsync(server.Id).OnCompleted(delegate(Exception error) { if (closing || IsDisposed) return; if (error != null) ShowError("Не удалось удалить сервер."); ApplyState(); });
        }
        void ShowServerDetails(ServerProfile server)
        { if (preview) return; int latency; string delay = state.Latencies.TryGetValue(server.Id, out latency) ? latency < 0 ? "Сервер пока не ответил на проверку" : "Задержка: " + latency + " мс" : "Задержка ещё не измерена"; MessageBox.Show(this, UiText.CleanName(server.Name, true) + "\n\n" + UiText.Protocol(server) + "\n" + delay + "\n\n" + (server.Id == state.Preferences.SelectedServerId ? "Этот сервер выбран для подключения." : "Чтобы выбрать сервер, нажмите на его название в списке."), "Сервер — SimpleVPN7", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        void ShowError(string message) { if (!closing && !IsDisposed) MessageBox.Show(this, message, "SimpleVPN7", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        internal static void OpenWebsite(string address) { string safe = UiText.SafeWeb(address); if (safe == null) return; try { Process.Start(new ProcessStartInfo(safe) { UseShellExecute = true }); } catch { MessageBox.Show("Не удалось открыть браузер.", "SimpleVPN7", MessageBoxButtons.OK, MessageBoxIcon.Information); } }
        void RestoreWindow() { if (closing || IsDisposed || Disposing) return; Show(); WindowState = FormWindowState.Normal; BringToFront(); Activate(); }
        internal void ActivateFromSecondInstance() { if (closing || IsDisposed || Disposing) return; if (InvokeRequired) { try { BeginInvoke(new MethodInvoker(ActivateFromSecondInstance)); } catch (InvalidOperationException) { } return; } RestoreWindow(); foreach (Form dialog in OwnedForms) if (dialog.Visible && !dialog.IsDisposed) { dialog.BringToFront(); dialog.Activate(); break; } }
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!preview && !forceClose && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); if (!trayHint) { trayHint = true; tray.ShowBalloonTip(3500, "SimpleVPN7 работает в фоне", "Откройте окно ярлыком. Для завершения выберите «Выход» у значка рядом с часами.", ToolTipIcon.Info); } return; }
            base.OnFormClosing(e); if (e.Cancel || closing) return; closing = true; if (controller != null) { controller.Changed -= ControllerChanged; controller.Shutdown(); } if (tray != null) tray.Visible = false;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { if (controller != null) controller.Changed -= ControllerChanged; if (tray != null) { tray.Visible = false; tray.Dispose(); } if (trayMenu != null) trayMenu.Dispose(); if (tips != null) tips.Dispose(); if (artwork != null) artwork.Dispose(); } base.Dispose(disposing);
        }
        public void SavePreview(string path)
        {
            if (!preview) throw new InvalidOperationException("Use the preview constructor to render without a tray icon or background work.");
            if (Visible) throw new InvalidOperationException("Preview must remain invisible.");
            PerformLayout(); CreateHiddenHandles(root); root.PerformLayout();
            using (var image = new Bitmap(ClientSize.Width, ClientSize.Height, PixelFormat.Format32bppArgb)) { root.DrawToBitmap(image, new Rectangle(Point.Empty, ClientSize)); image.Save(path, ImageFormat.Png); }
        }
        static void CreateHiddenHandles(Control control) { IntPtr unused = control.Handle; foreach (Control child in control.Controls) CreateHiddenHandles(child); control.PerformLayout(); }
        public static void SaveSyntheticPreview(string path, bool connected, bool light, int width, int height)
        { using (var form = new MainForm(null, true)) { form.state = UiState.Sample(connected, light); form.ClientSize = new Size(width, height); form.ApplyState(); form.SavePreview(path); } }
    }

    internal sealed class TextButton : Button
    {
        internal bool Primary, Selected, Compact; bool hover;
        internal TextButton(string title, bool primary) { Text = title; Primary = primary; Cursor = Cursors.Hand; FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent == null ? UiPalette.Background : Parent.BackColor); Color fill = Primary ? UiPalette.AccentFill : Selected ? UiPalette.Raised : UiPalette.Surface; if (hover && Enabled) fill = UiPalette.Blend(fill, UiPalette.Accent, .13F);
            UiDraw.Rounded(e.Graphics, new RectangleF(.5F, .5F, Width - 1, Height - 1), Compact ? 8 : 10, fill, Primary ? null : (Color?)UiPalette.Border);
            UiDraw.Text(e.Graphics, Text, new RectangleF(8, 0, Width - 16, Height), Compact ? 8.5F : 10, !Enabled ? UiPalette.Muted : Primary ? UiPalette.OnAccent : Selected ? UiPalette.Accent : UiPalette.Text, Primary || Selected, StringAlignment.Center);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -4, -4), UiPalette.Text, fill);
        }
    }

    internal static class UiStyles
    {
        internal static void Apply(Control control)
        {
            control.BackColor = control is TextBox || control is ComboBox || control is NumericUpDown ? UiPalette.Surface : UiPalette.Background;
            control.ForeColor = (control.Tag as string) == "muted" ? UiPalette.Muted : UiPalette.Text;
            foreach (Control child in control.Controls) Apply(child); control.Invalidate();
        }
    }
}
