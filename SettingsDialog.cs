using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace SimpleVPN
{
    internal sealed class SettingsDialog : Form
    {
        readonly AppController controller; readonly bool preview; AppSettings preferences;
        readonly TableLayoutPanel root, body; readonly Panel navigation, host;
        readonly List<FlowLayoutPanel> pages = new List<FlowLayoutPanel>(); readonly List<TextButton> tabs = new List<TextButton>();
        readonly TextBox url, directDomains, proxyDomains, testUrl; readonly ChoiceButton theme, routing, dns;
        readonly NumericUpDown interval, timeout; readonly CheckBox startup, autoConnect, pingOnOpen, sortLatency, fastest, ads;
        readonly TextButton save, refresh; readonly IconButton reveal; readonly Label message, updated; bool working;
        readonly UpdateManager updater; readonly CheckBox autoUpdate; readonly Label updateStatus; readonly TextButton checkUpdate;
        internal SettingsDialog(AppController value, Icon icon, int section) : this(value, icon, section, false) { }
        SettingsDialog(AppController value, Icon icon, int section, bool isPreview)
        {
            controller = value; preview = isPreview; preferences = preview ? UiState.Sample(false, !UiPalette.Dark).Preferences : controller.CopySettings();
            Text = "Настройки — SimpleVPN7"; Icon = icon; Font = UiDraw.Font(10.5F, false); AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(750, 590); MinimumSize = new Size(670, 540); StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false; MinimizeBox = false; MaximizeBox = false;
            root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(18, 12, 18, 12), Margin = Padding.Empty };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 94)); Controls.Add(root);
            var heading = new Label { Text = "Настройки", Font = UiDraw.Font(20, true), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty }; root.Controls.Add(heading, 0, 0);
            body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty }; body.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150)); body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            navigation = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 10, 10, 0) }; host = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty }; body.Controls.Add(navigation, 0, 0); body.Controls.Add(host, 1, 0); root.Controls.Add(body, 0, 1);
            string[] sections = { "Интерфейс", "Подписка", "Подключение", "Маршрутизация", "Дополнительно", "Обновления" };
            for (int i = 0; i < sections.Length; i++) { int index = i; var tab = new TextButton(sections[i], false) { Height = 39, Width = 138, Top = i * 48, TabIndex = i }; tab.Click += delegate { SelectPage(index); }; navigation.Controls.Add(tab); tabs.Add(tab); pages.Add(Page()); }

            var p = pages[0]; Title(p, "Интерфейс"); Hint(p, "Настройте внешний вид и поведение приложения.", 37);
            theme = Choice(p, "Тема оформления", new[] { "Тёмная", "Светлая" }, preferences.Theme == "light" ? 1 : 0);
            startup = Toggle(p, "Запускать вместе с Windows", preferences.StartWithWindows);
            autoConnect = Toggle(p, "Подключаться при запуске", preferences.AutoConnect);
            pingOnOpen = Toggle(p, "Проверять серверы при запуске", preferences.PingOnOpen);
            sortLatency = Toggle(p, "Быстрые серверы выше в списке", preferences.SortByLatency);
            Hint(p, "Свернуть: окно останется в панели задач.\nКрестик: приложение продолжит работу в трее.", 58);

            p = pages[1]; Title(p, "Выбранная подписка"); Hint(p, "Новую подписку или конфиги добавляйте кнопкой + в главном окне. Здесь можно изменить ссылку выбранной подписки.", 58);
            Caption(p, "Ссылка на подписку"); var urlRow = new Panel { Height = 37, Margin = new Padding(0, 0, 0, 9) };
            reveal = new IconButton("eye", "Показать или скрыть ссылку") { Dock = DockStyle.Right, Width = 36 }; reveal.Click += delegate { url.UseSystemPasswordChar = !url.UseSystemPasswordChar; reveal.AccessibleName = url.UseSystemPasswordChar ? "Показать ссылку" : "Скрыть ссылку"; };
            url = new TextBox { Text = preview ? "https://example.com/subscription/demo" : preferences.SubscriptionUrl ?? "", UseSystemPasswordChar = true, BorderStyle = BorderStyle.FixedSingle, Dock = DockStyle.Fill, Font = UiDraw.Font(11, false), MaxLength = 8192, AccessibleName = "Ссылка на подписку" };
            var paste = new TextButton("Вставить", false) { Dock = DockStyle.Right, Width = 79, Compact = true }; paste.Click += delegate { try { if (Clipboard.ContainsText()) url.Text = Clipboard.GetText().Trim(); } catch { message.Text = "Не удалось прочитать буфер обмена."; } };
            urlRow.Controls.Add(url); urlRow.Controls.Add(reveal); urlRow.Controls.Add(paste); p.Controls.Add(urlRow);
            interval = Number(p, "Обновлять каждые, часов", preferences.UpdateIntervalHours, 1, 168);
            updated = Hint(p, "", 36); refresh = new TextButton("Обновить серверы сейчас", false) { Height = 37, Margin = new Padding(0, 0, 0, 9) }; refresh.Click += delegate { RefreshSubscription(); }; p.Controls.Add(refresh);
            var metadata = preview ? preferences.Metadata : controller.Metadata;
            if (metadata != null && metadata.UpdateIntervalHours.HasValue) Hint(p, "Интервал, указанный сервисом: " + metadata.UpdateIntervalHours.Value + " ч. Вы можете выбрать свой.", 45);
            if (metadata != null && UiText.SafeWeb(metadata.SupportUrl) != null) { var support = new TextButton("Помощь VPN-сервиса", false) { Height = 36 }; support.Click += delegate { MainForm.OpenWebsite(metadata.SupportUrl); }; p.Controls.Add(support); }
            url.TextChanged += delegate { UpdateState(); };

            p = pages[2]; Title(p, "Подключение"); Caption(p, "Режим работы"); Hint(p, "Системный прокси Windows", 35, true);
            Hint(p, "Подключаются браузеры и программы, которые используют системный HTTP/HTTPS-прокси. Режима TUN и перенаправления всего UDP-трафика здесь нет.", 75);
            timeout = Number(p, "Ожидание подключения, секунд", preferences.ConnectionTimeoutSeconds, 2, 15);
            fastest = Toggle(p, "Выбирать самый быстрый сервер", preferences.AutoSelectFastest);
            Hint(p, "Выбор выполняется по результатам проверки задержки. Нажмите значок спидометра на карточке подписки.", 60);

            p = pages[3]; Title(p, "Маршрутизация"); Hint(p, "Правила для трафика, который проходит через системный прокси приложения.", 44);
            routing = Choice(p, "Основное правило", new[] { "Все сайты через VPN", "Локальная сеть напрямую" }, preferences.RoutingMode == "bypass-private" ? 1 : 0);
            ads = Toggle(p, "Блокировать рекламные домены в VPN", preferences.BlockAds);
            directDomains = Multiline(p, "Напрямую — по одному домену в строке", preferences.DirectDomains ?? "", 62);
            proxyDomains = Multiline(p, "Через VPN — по одному домену в строке", preferences.ProxyDomains ?? "", 62);
            Hint(p, "Например: example.com. Без https:// и пути страницы. Для пересекающихся правил действуют проверки приложения.", 54);

            p = pages[4]; Title(p, "Дополнительно"); Hint(p, "Эти параметры обычно можно оставить без изменений.", 37);
            dns = Choice(p, "DNS для сайтов через VPN", new[] { "Системный", "Cloudflare — 1.1.1.1", "Google — 8.8.8.8" }, preferences.DnsMode == "cloudflare" ? 1 : preferences.DnsMode == "google" ? 2 : 0);
            Hint(p, "Настройка не меняет DNS самой Windows. Для прямых соединений используется системное разрешение имён.", 57);
            Caption(p, "HTTPS-адрес проверки соединения"); testUrl = new TextBox { Text = preferences.ConnectionTestUrl ?? "https://www.gstatic.com/generate_204", Font = UiDraw.Font(10.5F, false), BorderStyle = BorderStyle.FixedSingle, Height = 29, Margin = new Padding(0, 0, 0, 10), MaxLength = 2048, AccessibleName = "HTTPS адрес проверки соединения" }; p.Controls.Add(testUrl);
            Hint(p, "Приложение проверяет доступ к этому адресу через выбранный сервер. Используйте надёжную короткую HTTPS-страницу.", 58);
            Hint(p, "SimpleVPN7 · Windows 7 SP1 x64\n.NET Framework 3.5 · Xray\nСсылка подписки и пароли не записываются в журнал.", 78);

            p = pages[5]; Title(p, "Обновления приложения");
            if (!preview) updater = new UpdateManager(new LocalStore(AppDomain.CurrentDomain.BaseDirectory));
            var updatePreferences = preview ? new UpdatePreferences() : updater.LoadPreferences();
            Hint(p, "Версия " + typeof(MainForm).Assembly.GetName().Version.ToString(3), 32, true);
            autoUpdate = Toggle(p, "Автоматически загружать обновления", updatePreferences.AutoUpdate);
            Hint(p, "Новая версия загрузится в фоне и установится при следующем запуске приложения. Подписки и настройки сохранятся.", 64);
            updateStatus = Hint(p, preview ? "Установлена текущая версия приложения." : updater.LoadStatus(), 48);
            checkUpdate = new TextButton("Проверить обновления сейчас", false) { Height = 37, Margin = new Padding(0, 0, 0, 12) }; checkUpdate.Click += delegate { CheckUpdates(); }; p.Controls.Add(checkUpdate);
            var project = new TextButton("Страница приложения", false) { Height = 35, Margin = new Padding(0, 0, 0, 9) }; project.Click += delegate { if (!preview) MainForm.OpenWebsite(UpdateManager.RepositoryUrl); }; p.Controls.Add(project);

            var bottom = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0) }; message = new Label { Dock = DockStyle.Top, Height = 38, Font = UiDraw.Font(9.2F, false), Tag = "muted", TextAlign = ContentAlignment.MiddleLeft };
            var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = Padding.Empty };
            save = new TextButton("Сохранить настройки", true) { Size = new Size(180, 38), Margin = new Padding(8, 0, 0, 0) }; save.Click += delegate { SavePreferences(); };
            var close = new TextButton("Закрыть", false) { Size = new Size(98, 38), Margin = Padding.Empty }; close.Click += delegate { Close(); }; actions.Controls.Add(save); actions.Controls.Add(close); bottom.Controls.Add(message); bottom.Controls.Add(actions); root.Controls.Add(bottom, 0, 2); AcceptButton = save; CancelButton = close;
            if (!preview) controller.Changed += ControllerChanged; SelectPage(Math.Max(0, Math.Min(pages.Count - 1, section))); UpdateState(); UiStyles.Apply(this);
        }
        FlowLayoutPanel Page()
        {
            var page = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(14, 7, 9, 8), Margin = Padding.Empty, Visible = false };
            page.SizeChanged += delegate { FitPage(page); }; host.Controls.Add(page); return page;
        }
        static void FitPage(FlowLayoutPanel page) { int width = Math.Max(180, page.ClientSize.Width - page.Padding.Horizontal - 20); foreach (Control c in page.Controls) c.Width = width; }
        static void Title(FlowLayoutPanel page, string text) { page.Controls.Add(new Label { Text = text, Font = UiDraw.Font(16, true), Height = 34, Margin = new Padding(0, 0, 0, 5) }); }
        static void Caption(FlowLayoutPanel page, string text) { page.Controls.Add(new Label { Text = text, Font = UiDraw.Font(10, false), Height = 25, Margin = Padding.Empty, TextAlign = ContentAlignment.MiddleLeft }); }
        static Label Hint(FlowLayoutPanel page, string text, int height) { return Hint(page, text, height, false); }
        static Label Hint(FlowLayoutPanel page, string text, int height, bool bold) { var label = new Label { Text = text, Font = UiDraw.Font(bold ? 11 : 9.3F, bold), Height = height, Tag = bold ? null : "muted", Margin = new Padding(0, 0, 0, 7) }; page.Controls.Add(label); return label; }
        static CheckBox Toggle(FlowLayoutPanel page, string text, bool value) { var check = new CheckBox { Text = text, Checked = value, Height = 35, AutoSize = false, Font = UiDraw.Font(10, false), Margin = new Padding(0, 0, 0, 3), AccessibleName = text }; page.Controls.Add(check); return check; }
        static ChoiceButton Choice(FlowLayoutPanel page, string title, string[] choices, int selected)
        {
            Caption(page, title); var choice = new ChoiceButton(choices, selected) { Height = 35, Margin = new Padding(0, 0, 0, 12), AccessibleName = title }; page.Controls.Add(choice); return choice;
        }
        static NumericUpDown Number(FlowLayoutPanel page, string title, int value, int minimum, int maximum) { Caption(page, title); var number = new NumericUpDown { Minimum = minimum, Maximum = maximum, Value = Math.Max(minimum, Math.Min(maximum, value)), Font = UiDraw.Font(11, false), BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 0, 0, 12), AccessibleName = title }; page.Controls.Add(number); return number; }
        static TextBox Multiline(FlowLayoutPanel page, string title, string text, int height) { Caption(page, title); var input = new TextBox { Text = text, Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical, Height = height, BorderStyle = BorderStyle.FixedSingle, Font = UiDraw.Font(10, false), MaxLength = 16384, Margin = new Padding(0, 0, 0, 9), AccessibleName = title }; page.Controls.Add(input); return input; }
        void SelectPage(int index) { for (int i = 0; i < pages.Count; i++) { pages[i].Visible = i == index; tabs[i].Selected = i == index; tabs[i].Invalidate(); } pages[index].BringToFront(); FitPage(pages[index]); }
        void ControllerChanged(object sender, EventArgs e) { if (IsDisposed || Disposing) return; if (InvokeRequired) { try { BeginInvoke(new MethodInvoker(UpdateState)); } catch (InvalidOperationException) { } } else UpdateState(); }
        void UpdateState()
        {
            if (save == null || IsDisposed) return; bool idle = !working && (preview || !controller.IsBusy); save.Enabled = idle; foreach (var page in pages) page.Enabled = idle;
            string savedUrl = preview ? "https://example.com/subscription/demo" : controller.Settings.SubscriptionUrl ?? ""; url.ReadOnly = !preview && controller.IsPinging; refresh.Enabled = idle && (preview || !controller.IsPinging) && savedUrl.Length > 0 && String.Equals(url.Text.Trim(), savedUrl, StringComparison.Ordinal);
            DateTime time = preview ? preferences.LastUpdatedUtc : controller.Settings.LastUpdatedUtc; updated.Text = time == DateTime.MinValue ? "Серверы ещё не загружены" : "Последнее обновление: " + time.ToLocalTime().ToString("dd.MM.yyyy, HH:mm");
            if (!working && String.IsNullOrEmpty(message.Text) && !preview && controller.IsConnected) message.Text = "Параметры подключения применятся после переподключения.";
        }
        AppSettings Gather()
        {
            var result = controller.CopySettings(); result.SubscriptionUrl = url.Text.Trim(); result.StartWithWindows = startup.Checked; result.AutoConnect = autoConnect.Checked; result.Theme = theme.SelectedIndex == 1 ? "light" : "dark";
            result.PingOnOpen = pingOnOpen.Checked; result.SortByLatency = sortLatency.Checked; result.UpdateIntervalHours = (int)interval.Value; result.ConnectionTimeoutSeconds = (int)timeout.Value; result.AutoSelectFastest = fastest.Checked;
            result.RoutingMode = routing.SelectedIndex == 1 ? "bypass-private" : "all"; result.BlockAds = ads.Checked; result.DirectDomains = directDomains.Text; result.ProxyDomains = proxyDomains.Text;
            result.DnsMode = dns.SelectedIndex == 1 ? "cloudflare" : dns.SelectedIndex == 2 ? "google" : "system"; result.ConnectionTestUrl = testUrl.Text.Trim(); return result;
        }
        void SavePreferences()
        {
            if (preview || working || controller.IsBusy) return;
            if (controller.IsPinging && url.Text.Trim() != (controller.Settings.SubscriptionUrl ?? "")) { SelectPage(1); message.Text = "Дождитесь проверки серверов, чтобы изменить ссылку."; return; }
            if (url.Text.Trim().Length > 0 && UiText.SafeWeb(url.Text.Trim()) == null) { SelectPage(1); message.Text = "Нужна полная HTTPS-ссылка подписки без логина и пароля в адресе."; url.Focus(); return; }
            if (UiText.SafeWeb(testUrl.Text.Trim()) == null) { SelectPage(4); message.Text = "Для проверки соединения нужен корректный HTTPS-адрес."; testUrl.Focus(); return; }
            var chosen = Gather(); working = true; message.Text = "Сохраняем настройки…"; UpdateState();
            controller.SavePreferencesAsync(chosen).OnCompleted(delegate(Exception error)
            {
                working = false; if (IsDisposed || Disposing) return; url.UseSystemPasswordChar = true;
                if (error != null) message.Text = "Не удалось сохранить. Проверьте ссылку и правила маршрутизации.";
                else { preferences = controller.CopySettings(); UiPalette.Dark = preferences.Theme != "light"; UiStyles.Apply(this); message.Text = !String.IsNullOrEmpty(controller.Notice) ? controller.Notice : "Настройки сохранены."; try { updater.SavePreferences(autoUpdate.Checked, ""); } catch { message.Text = "Параметры VPN сохранены. Не удалось сохранить настройки обновления."; } }
                UpdateState();
            });
        }
        void CheckUpdates()
        {
            if (preview || working || controller.IsBusy) return;
            try { updater.SavePreferences(autoUpdate.Checked, ""); }
            catch { message.Text = "Не удалось сохранить настройки обновления."; return; }
            working = true; updateStatus.Text = "Проверяем обновления…"; UpdateState();
            var worker = new BackgroundWorker(); string result = null;
            worker.DoWork += delegate { result = updater.CheckAndStage(); };
            worker.RunWorkerCompleted += delegate(object sender, RunWorkerCompletedEventArgs args) { working = false; if (!IsDisposed) { updateStatus.Text = args.Error == null ? result : "Не удалось проверить обновления. Повторите позже."; UpdateState(); } worker.Dispose(); };
            worker.RunWorkerAsync();
        }
        void RefreshSubscription()
        {
            if (preview || working || controller.IsBusy || controller.IsPinging) return; working = true; message.Text = "Обновляем подписку…"; UpdateState(); controller.RefreshAsync().OnCompleted(delegate(Exception error) { working = false; if (IsDisposed) return; message.Text = error != null ? "Не удалось обновить серверы. Проверьте интернет." : controller.Notice ?? "Серверы обновлены."; UpdateState(); });
        }
        protected override void OnFormClosing(FormClosingEventArgs e) { if (working && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; message.Text = "Пожалуйста, дождитесь сохранения."; return; } base.OnFormClosing(e); }
        protected override void Dispose(bool disposing) { if (disposing && controller != null) controller.Changed -= ControllerChanged; base.Dispose(disposing); }
        static void HiddenHandles(Control control) { IntPtr unused = control.Handle; foreach (Control child in control.Controls) HiddenHandles(child); control.PerformLayout(); }
        internal static void SaveSyntheticPreview(string path, int section, bool light)
        {
            UiPalette.Dark = !light; using (var icon = AppArtwork.CreateIcon()) using (var dialog = new SettingsDialog(null, icon, section, true))
            { dialog.PerformLayout(); HiddenHandles(dialog.root); foreach (var page in dialog.pages) FitPage(page); dialog.root.PerformLayout(); using (var image = new Bitmap(dialog.ClientSize.Width, dialog.ClientSize.Height, PixelFormat.Format32bppArgb)) { dialog.root.DrawToBitmap(image, new Rectangle(Point.Empty, dialog.ClientSize)); image.Save(path, ImageFormat.Png); } }
        }
    }
}
