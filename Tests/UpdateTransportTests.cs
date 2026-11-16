using System;
using System.IO;
using SimpleVPN;

internal static class UpdateTransportTests
{
    private static int checks;
    private static void Check(bool pass, string name) { if (!pass) throw new Exception(name); checks++; }
    private static void Refused(Action action, string name)
    { try { action(); } catch (IOException) { checks++; return; } throw new Exception(name); }
    public static int Main()
    {
        try
        {
            const string api = "https://api.github.com/repos/Nikita15313/simplevpn7/releases/";
            Uri latest = UpdateTransport.ParseInitial(api + "latest");
            Uri asset = UpdateTransport.ParseInitial(api + "assets/123456789");
            Check(UpdateTransport.ShouldAuthorize(latest, 0), "First API request receives authorization");
            Check(UpdateTransport.ShouldAuthorize(asset, 0), "First asset API request receives authorization");
            Check(!UpdateTransport.ShouldAuthorize(latest, 1), "API redirect strips authorization");
            Check(!UpdateTransport.ShouldAuthorize(asset, 4), "Later redirect strips authorization");
            string[] refused = {
                "http://api.github.com/repos/Nikita15313/simplevpn7/releases/latest",
                "https://api.github.com:444/repos/Nikita15313/simplevpn7/releases/latest",
                "https://secret@api.github.com/repos/Nikita15313/simplevpn7/releases/latest",
                api + "latest#fragment", api + "latest?token=secret", api + "assets/12x", api + "assets/",
                "https://api.github.com/repos/OtherOwner/simplevpn7/releases/latest",
                "https://api.github.com/repos/Nikita15313/other/releases/latest",
                "https://api.github.com/user", "https://api.github.com.evil.invalid/", "https://127.0.0.1/",
                "file:///C:/Windows/win.ini", "https://[::1]/", "https://api.github.com./repos/Nikita15313/simplevpn7/releases/latest"
            };
            foreach (string value in refused) { string test = value; Refused(delegate { UpdateTransport.ParseInitial(test); }, "Initial URL accepted"); }
            Uri cdn = UpdateTransport.ResolveRedirect(asset, "https://release-assets.githubusercontent.com/github-production-release-asset/123/test?signature=temporary");
            Check(!UpdateTransport.ShouldAuthorize(cdn, 0), "CDN never receives authorization even with wrong redirect count");
            Check(!UpdateTransport.ShouldAuthorize(cdn, 1), "CDN redirect has no authorization");
            Check(UpdateTransport.ResolveRedirect(latest, "assets/456").AbsolutePath.EndsWith("/assets/456"), "Allowed relative redirect");
            Check(UpdateTransport.ResolveRedirect(asset, "https://objects.githubusercontent.com/asset").DnsSafeHost == "objects.githubusercontent.com", "Allowed secondary GitHub CDN");
            string[] badRedirects = {
                "http://release-assets.githubusercontent.com/file", "https://release-assets.githubusercontent.com:444/file",
                "https://release-assets.githubusercontent.com.evil.invalid/file", "https://user:password@release-assets.githubusercontent.com/file",
                "https://release-assets.githubusercontent.com/file#fragment", "https://evil.invalid/file", "https://127.0.0.1/file",
                "https://10.1.2.3/file", "https://[::1]/file", "https://api.github.com/user",
                "https://api.github.com/repos/OtherOwner/simplevpn7/releases/assets/1", "file:///C:/Windows/win.ini", ""
            };
            foreach (string value in badRedirects) { string test = value; Refused(delegate { UpdateTransport.ResolveRedirect(asset, test); }, "Redirect accepted"); }
            Refused(delegate { UpdateTransport.Get(api + "latest", "injected\r\nHeader: value", "application/vnd.github+json", 100); }, "Token CRLF accepted");
            Refused(delegate { UpdateTransport.Get(api + "latest", "", "application/json\r\nInjected: yes", 100); }, "Accept injection accepted");
            Refused(delegate { UpdateTransport.Get(api + "latest", "", "application/vnd.github+json", 0); }, "Zero cap accepted");
            Refused(delegate { UpdateTransport.Get(api + "latest", "", "application/vnd.github+json", 128 * 1024 * 1024 + 1); }, "Excessive cap accepted");
            Refused(delegate { UpdateTransport.Get(api + "latest", "", "application/vnd.github+json", 100, 65536); }, "Invalid proxy accepted");
            Console.WriteLine("Update transport contract PASS: " + checks + " origin, credential, redirect and argument assertions; no network or system-proxy writes.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine("FAIL " + error.GetType().Name + ": " + error.Message); return 1; }
    }
}
