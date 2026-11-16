using System;
using System.IO;
using System.Text;
using SimpleVPN;

// Compile with application sources and /main:UiDataTests. No UI or network opens.
internal static class UiDataTests
{
    static int checks;
    static void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); checks++; }
    public static int Main()
    {
        try
        {
            Check(UiText.Bytes(536870912000L) == "500 ГБ", "Provider 500 GiB must match mobile 500 GB display");
            Check(UiText.Bytes(18038862643L) == "16,8 ГБ", "18 decimal GB must render 16.8 binary GB");
            Check(UiText.Bytes(1073741824L) == "1 ГБ", "Binary GB boundary");
            Check(UiText.Bytes(1024) == "1 КБ", "Binary KB boundary");
            Check(UiText.Bytes(0) == "0 Б", "Empty traffic");
            var now = new DateTime(2026, 11, 16, 11, 0, 0, DateTimeKind.Utc);
            var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var meta = new SubscriptionMetadata { ExpireUnixSeconds = (long)(now.AddDays(139).AddHours(2) - epoch).TotalSeconds };
            Check(UiText.Expiry(meta, now).IndexOf("140 дн.", StringComparison.Ordinal) >= 0, "Partial remaining day rounds up");
            meta.ExpireUnixSeconds = (long)(now - epoch).TotalSeconds;
            Check(UiText.Expiry(meta, now).StartsWith("Срок подписки истёк", StringComparison.Ordinal), "Exact expiry is expired");
            Check(UiText.Expiry(null, now) == "Срок действия не указан сервисом", "Missing expiry stays unknown");
            Check(UiText.ConfigurationCount(2) == "2 конфигурации" && UiText.ConfigurationCount(5) == "5 конфигураций" && UiText.ConfigurationCount(11) == "11 конфигураций" && UiText.ConfigurationCount(21) == "21 конфигурация", "Source count grammar");
            string file = Path.Combine(Path.GetTempPath(), "SimpleVPN-import-" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                File.WriteAllText(file, "Мой конфиг", Encoding.UTF8);
                Check(ConfigImportDialog.ReadImportFile(file) == "Мой конфиг", "UTF8 BOM file import");
                using (var oversized = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None)) oversized.SetLength(8 * 1024 * 1024 + 1);
                bool rejected = false; try { ConfigImportDialog.ReadImportFile(file); } catch (IOException) { rejected = true; }
                Check(rejected, "Oversize import rejected before reading");
            }
            finally { if (File.Exists(file)) File.Delete(file); }
            Console.WriteLine("UI data tests PASS: " + checks); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
    }
}
