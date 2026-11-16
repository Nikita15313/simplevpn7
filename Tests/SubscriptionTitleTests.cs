using System;
using SimpleVPN;

// Compile with the application sources and /main:SubscriptionTitleTests.
// Text-only fixtures: no form, network, core, or Windows proxy is started.
internal static class SubscriptionTitleTests
{
    static int checks;
    static void Equal(string expected, string actual, string name)
    {
        if (expected != actual) throw new InvalidOperationException("Failed: " + name);
        checks++;
    }
    public static int Main()
    {
        try
        {
            var metadata = new SubscriptionMetadata { Title = "\u26a1VPNBAZA\u26a1" };
            Equal("VPNBAZA", UiText.SubscriptionTitle(metadata.Title), "Win7 provider caption");
            Equal("\u26a1VPNBAZA\u26a1", metadata.Title, "Raw metadata remains intact");
            Equal("Семейный VPN — Ёжик № 2", UiText.SubscriptionTitle(" \ud83d\ude80 Семейный  VPN — Ёжик № 2 \u2764\ufe0f "), "Cyrillic and punctuation");
            Equal("Мой VPN", UiText.SubscriptionTitle("Мой\ud83d\udc69\u200d\ud83d\udcbbVPN"), "Joined emoji does not join words");
            Equal("План A+B/C #1 · 50% (RU)", UiText.SubscriptionTitle("План A+B/C #1 · 50% (RU)"), "Useful text symbols preserved");
            Equal("Дом & офис", UiText.SubscriptionTitle("\tДом \u00a0 \r\n &  офис\n"), "Whitespace normalized");
            Equal("", UiText.SubscriptionTitle("\u26a1\ufe0f\ud83c\uddfa\ud83c\uddf8\ud83d\ude80"), "Emoji-only title uses caller fallback");
            Equal("", UiText.SubscriptionTitle(null), "Missing title uses caller fallback");
            Equal("VPN", UiText.SubscriptionTitle("\ud83dVPN\udc00"), "Malformed surrogate ignored");
            Equal("Cafe\u0301 «Тест»", UiText.SubscriptionTitle("Cafe\u0301 «Тест»"), "Accents and quotes preserved");
            Equal("\ud801\udc00 VPN", UiText.SubscriptionTitle("\ud801\udc00 VPN"), "Supplementary letter is not an emoji");
            Console.WriteLine("Subscription title tests PASS: " + checks);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
    }
}
