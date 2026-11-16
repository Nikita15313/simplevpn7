using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace SimpleVPN
{
    internal sealed class ConfigImportDialog : Form
    {
        readonly TextBox name, configuration;
        readonly Label message;
        internal string ConfigurationName { get { return name.Text.Trim(); } }
        internal string ConfigurationText { get { return configuration.Text.Trim(); } }
        internal ConfigImportDialog(Icon icon)
        {
            Text = "Добавить конфигурацию"; Icon = icon; Font = UiDraw.Font(10.5F, false);
            AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
            ClientSize = new Size(550, 420); MinimumSize = new Size(450, 360); StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false; MinimizeBox = MaximizeBox = false;
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new Padding(22) };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 39)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 31));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 49));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 41)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40)); Controls.Add(root);
            root.Controls.Add(new Label { Text = "Подписка или конфиги", Font = UiDraw.Font(18, true), Dock = DockStyle.Fill }, 0, 0);
            root.Controls.Add(new Label { Text = "Название (необязательно)", Dock = DockStyle.Fill, Tag = "muted", TextAlign = ContentAlignment.MiddleLeft }, 0, 1);
            name = new TextBox { Dock = DockStyle.Fill, Font = UiDraw.Font(11, false), MaxLength = 100, AccessibleName = "Название конфигурации" }; root.Controls.Add(name, 0, 2);
            root.Controls.Add(new Label { Text = "Вставьте ссылку подписки, ссылки серверов или JSON.\nМожно добавить несколько ссылок — по одной в строке.", Dock = DockStyle.Fill, Font = UiDraw.Font(9.4F, false), Tag = "muted", Padding = new Padding(0, 7, 0, 0) }, 0, 3);
            configuration = new TextBox { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true, Font = UiDraw.Font(10, false), MaxLength = 8 * 1024 * 1024, AccessibleName = "Ссылка подписки или конфигурация" }; root.Controls.Add(configuration, 0, 4);
            var extras = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 5, 0, 0) };
            var paste = new TextButton("Вставить", false) { Width = 110, Height = 32, Compact = true }; paste.Click += delegate { try { if (Clipboard.ContainsText()) configuration.Text = Clipboard.GetText(); } catch { message.Text = "Не удалось прочитать буфер обмена."; } };
            var file = new TextButton("Из файла…", false) { Width = 120, Height = 32, Compact = true }; file.Click += delegate { LoadFile(); };
            extras.Controls.Add(paste); extras.Controls.Add(file); message = new Label { AutoEllipsis = true, Width = 255, Height = 31, Font = UiDraw.Font(9, false), Tag = "muted", TextAlign = ContentAlignment.MiddleLeft }; extras.Controls.Add(message); root.Controls.Add(extras, 0, 5);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
            var accept = new TextButton("Добавить", true) { Size = new Size(130, 36) }; accept.Click += delegate { if (ConfigurationText.Length == 0) { message.Text = "Вставьте ссылку или откройте файл."; configuration.Focus(); return; } DialogResult = DialogResult.OK; Close(); };
            var cancel = new TextButton("Отмена", false) { Size = new Size(100, 36), DialogResult = DialogResult.Cancel }; actions.Controls.Add(accept); actions.Controls.Add(cancel); root.Controls.Add(actions, 0, 6); AcceptButton = accept; CancelButton = cancel; UiStyles.Apply(this);
        }
        void LoadFile()
        {
            using (var dialog = new OpenFileDialog { Title = "Открыть конфигурацию", Filter = "Конфигурации (*.json;*.txt;*.conf)|*.json;*.txt;*.conf|Все файлы (*.*)|*.*", CheckFileExists = true, Multiselect = false })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try { configuration.Text = ReadImportFile(dialog.FileName); if (String.IsNullOrEmpty(name.Text)) name.Text = Path.GetFileNameWithoutExtension(dialog.FileName); message.Text = "Файл прочитан."; }
                catch { message.Text = "Не удалось прочитать файл."; }
            }
        }
        internal static string ReadImportFile(string path)
        {
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (file.Length > 8 * 1024 * 1024) throw new IOException("CONFIG_FILE_TOO_LARGE");
                // Share only reads while checking and loading the file: it cannot
                // grow past the bound between the size check and the actual read.
                using (var reader = new StreamReader(file, new UTF8Encoding(false, true), true)) return reader.ReadToEnd();
            }
        }
    }
}
