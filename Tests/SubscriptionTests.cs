using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Web.Script.Serialization;
using SimpleVPN;

// Compile separately with Subscriptions.cs. Optional arguments: xray.exe, encrypted subscription.
// Only fixture names and counts are printed, never provider credentials or core error text.
internal static class SubscriptionTests
{
    private static int checks;
    private const string UserId = "00112233-4455-6677-8899-aabbccddeeff";
    private static readonly string RealityKey = Convert.ToBase64String(new byte[32]).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string B64(string s) { return Convert.ToBase64String(Encoding.UTF8.GetBytes(s)); }
    private static string Reality(string suffix)
    {
        return "vless://" + UserId + "@server.invalid:443?security=reality&type=tcp&sni=www.example.com&pbk=" + RealityKey + suffix;
    }
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("Fixture failed: " + name);
        checks++;
    }
    private static void Reject(string input, string name)
    {
        bool rejected = false;
        try { SubscriptionParser.Parse(input); } catch (FormatException) { rejected = true; }
        Check(rejected, name);
    }
    public static int Main(string[] args)
    {
        try
        {
            List<ServerProfile> profiles = new List<ServerProfile>();
            ServerProfile reality = SubscriptionParser.Parse(Reality("&flow=xtls-rprx-vision&sid=0123&spx=%2F#%D0%A4%D0%B8%D0%BD%D0%BB%D1%8F%D0%BD%D0%B4%D0%B8%D1%8F"))[0];
            Check(reality.Name == "Финляндия", "Russian display name");
            profiles.Add(reality);
            string renamed = Reality("&sid=0123&spx=%2F&flow=xtls-rprx-vision#Another");
            Check(SubscriptionParser.Parse(renamed)[0].Id == reality.Id, "Stable ID ignores name and query ordering");
            Check(SubscriptionParser.Parse(renamed + "\n" + Reality("&flow=xtls-rprx-vision&sid=0123&spx=%2F#Name")).Count == 1, "Deduplication");
            Check(SubscriptionParser.Parse(B64(renamed)).Count == 1, "Base64 subscription");
            Check(SubscriptionParser.Parse(B64(renamed).TrimEnd('=').Replace('+', '-').Replace('/', '_')).Count == 1, "Base64url subscription");
            Check(SubscriptionParser.Parse("not-a-link\n" + renamed).Count == 1, "Skip invalid entries");
            string ipv6 = "vless://" + UserId + "@[2001:db8::1]:443?encryption=none&security=tls&type=ws&host=cdn.example.com&path=%2Fhello%3Fed%3D2048#IPv6";
            profiles.Add(SubscriptionParser.Parse(ipv6)[0]);
            Check(SubscriptionParser.BuildConfig(profiles[1], 18081).Contains("2001:db8::1"), "IPv6 address");
            profiles.Add(SubscriptionParser.Parse("vless://" + UserId + "@server.invalid:443?security=tls&type=grpc&serviceName=service&mode=multi&authority=grpc.example.com")[0]);
            profiles.Add(SubscriptionParser.Parse("vless://" + UserId + "@server.invalid:443?security=tls&type=httpupgrade&path=%2Fvpn&host=cdn.example.com")[0]);
            profiles.Add(SubscriptionParser.Parse("vless://" + UserId + "@server.invalid:443?security=tls&type=xhttp&mode=stream-up&path=%2Fvpn&host=cdn.example.com")[0]);
            profiles.Add(SubscriptionParser.Parse(Reality("").Replace("type=tcp", "type=xhttp") + "&mode=auto&path=%2Fvpn")[0]);
            profiles.Add(SubscriptionParser.Parse("trojan://p%40ss%3Aword%2Bplus@server.invalid:443?security=tls&type=tcp#Trojan")[0]);
            Check(SubscriptionParser.BuildConfig(profiles[6], 18081).Contains("p@ss:word+plus"), "Password URL decoding preserves plus");
            string vmess = "{\"v\":\"2\",\"ps\":\"VMess\",\"add\":\"server.invalid\",\"port\":\"443\",\"id\":\"" + UserId + "\",\"aid\":\"0\",\"net\":\"ws\",\"type\":\"none\",\"host\":\"cdn.example.com\",\"path\":\"/vpn\",\"tls\":\"tls\",\"sni\":\"cdn.example.com\"}";
            profiles.Add(SubscriptionParser.Parse("vmess://" + B64(vmess))[0]);
            profiles.Add(SubscriptionParser.Parse("ss://" + B64("aes-128-gcm:colon:and+password").TrimEnd('=') + "@server.invalid:8388#SS")[0]);
            profiles.Add(SubscriptionParser.Parse("ss://" + B64("aes-256-gcm:password@[2001:db8::2]:8388") + "#Legacy")[0]);
            profiles.Add(SubscriptionParser.Parse("ss://2022-blake3-aes-128-gcm:" + Uri.EscapeDataString(Convert.ToBase64String(new byte[16])) + "@server.invalid:8388#SS2022")[0]);
            string config = SubscriptionParser.BuildConfig(reality, 18081);
            Check(config.Contains("\"listen\":\"127.0.0.1\"") && config.Contains("\"protocol\":\"http\""), "Loopback HTTP inbound");
            Check(!config.Contains("freedom") && config.Contains("\"access\":\"none\""), "No direct fallback or access logs");
            JavaScriptSerializer json = new JavaScriptSerializer();
            Dictionary<string, object> supplied = new Dictionary<string, object>();
            supplied["remarks"] = "JSON profile";
            supplied["outbounds"] = new object[] { reality.Outbound, new Dictionary<string, object> { { "protocol", "freedom" } } };
            supplied["inbounds"] = new object[] { new Dictionary<string, object> { { "listen", "0.0.0.0" }, { "port", 1234 } } };
            supplied["routing"] = new Dictionary<string, object> { { "domainStrategy", "IPIfNonMatch" } };
            supplied["api"] = new Dictionary<string, object> { { "tag", "external-api" } };
            string jsonProfile = json.Serialize(supplied);
            ServerProfile extracted = SubscriptionParser.Parse(jsonProfile)[0];
            Check(extracted.Id == reality.Id && extracted.Name == "JSON profile", "JSON remote profile extraction");
            string rebuilt = SubscriptionParser.BuildConfig(extracted, 18081);
            Check(!rebuilt.Contains("0.0.0.0") && !rebuilt.Contains("IPIfNonMatch") && !rebuilt.Contains("external-api"), "JSON listeners routing and API ignored");
            Check(SubscriptionParser.Parse("[" + jsonProfile + "]").Count == 1, "JSON array subscription");
            Check(SubscriptionParser.Parse(B64(jsonProfile)).Count == 1, "Base64 JSON subscription");
            Check(SubscriptionParser.Parse(SubscriptionParser.BuildConfig(profiles[7], 18081))[0].Id == profiles[7].Id, "VMess JSON reconstruction");
            Check(SubscriptionParser.Parse(SubscriptionParser.BuildConfig(profiles[8], 18081))[0].Id == profiles[8].Id, "Shadowsocks JSON reconstruction");
            supplied["outbounds"] = new object[] { reality.Outbound, profiles[6].Outbound };
            Reject(json.Serialize(supplied), "Multiple proxy outbounds rejected");
            Dictionary<string, object> chained = new Dictionary<string, object>(reality.Outbound);
            chained["proxySettings"] = new Dictionary<string, object> { { "tag", "chained" } };
            supplied["outbounds"] = new object[] { chained };
            Reject(json.Serialize(supplied), "Chained proxy rejected");
            chained.Remove("proxySettings");
            chained["mux"] = new Dictionary<string, object> { { "enabled", true } };
            Reject(json.Serialize(supplied), "JSON enabled mux rejected");
            Check(SubscriptionParser.BuildConfig(SubscriptionParser.Parse(ipv6.Replace("path=%2Fhello%3Fed%3D2048", "path=%2Fhello&ed=2048"))[0], 18081).Contains("/hello?ed=2048"), "Early data encoded for Xray");
            Reject("{\"outbounds\":[{\"protocol\":\"freedom\"}]}", "Arbitrary JSON rejected");
            Reject(Reality("&allowInsecure=1"), "Insecure rejected");
            Reject(Reality("&security=tls"), "Duplicate security rejected");
            Reject(Reality("").Replace("security=reality", "security=xtls"), "Legacy XTLS rejected");
            Reject("vless://" + UserId + "@server.invalid:443?security=none", "Unencrypted VLESS rejected");
            Reject(Reality("").Replace("type=tcp", "type=quic"), "Unsupported transport rejected");
            Reject(Reality("&flow=xtls-rprx-vision").Replace("type=tcp", "type=grpc"), "Incompatible Vision rejected");
            Reject(Reality("").Replace("type=tcp", "type=ws"), "Reality WebSocket rejected");
            Reject(Reality("&sid=abc"), "Odd shortId rejected");
            Reject(Reality("&fp=unsafe"), "Unsafe fingerprint rejected");
            Reject(Reality("&path=%ZZ"), "Malformed percent rejected");
            Reject(Reality("&path=%0D%0AInjected"), "Header injection rejected");
            Reject(Reality("&finalmask=%7B%7D"), "Unknown connection extension rejected");
            Reject(Reality("&packetEncoding=xudp"), "Unsupported packet extension rejected");
            Reject("vmess://" + B64(vmess.Replace("\"aid\":\"0\"", "\"aid\":\"64\"")), "Legacy VMess rejected");
            Reject("ss://" + B64("aes-128-gcm:password") + "@server.invalid:8388/?plugin=v2ray-plugin%3Btls", "SS plugin rejected");
            Reject(Reality("").Replace(":443?", ":65536?"), "Invalid port rejected");
            Console.WriteLine("Parser checks passed: " + checks + "; supported core fixtures: " + profiles.Count);
            if (args.Length > 0) ValidateCore(args[0], profiles, "fixtures");
            if (args.Length > 1)
            {
                byte[] clear = ProtectedData.Unprotect(File.ReadAllBytes(args[1]), null, DataProtectionScope.CurrentUser);
                string text;
                try { text = new UTF8Encoding(false, true).GetString(clear); }
                finally { Array.Clear(clear, 0, clear.Length); }
                List<ServerProfile> actual = SubscriptionParser.Parse(text);
                Console.WriteLine("Provider subscription imported; profiles: " + actual.Count);
                ValidateCore(args[0], actual, "provider profiles");
            }
            return 0;
        }
        catch (Exception ex)
        {
            if (ex.Message.StartsWith("Fixture failed:")) Console.WriteLine(ex.Message);
            else if (ex is FormatException) Console.WriteLine("Validation failed: " + ex.Message);
            else Console.WriteLine("Validation failed: " + ex.GetType().Name);
            return 1;
        }
    }
    private static void ValidateCore(string executable, List<ServerProfile> profiles, string label)
    {
        string directory = Path.Combine(Path.GetTempPath(), "SimpleVpnParserTest-" + Guid.NewGuid().ToString("N"));
        DirectorySecurity security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        Directory.CreateDirectory(directory, security);
        string path = Path.Combine(directory, "config.json");
        try
        {
            int passed = 0;
            foreach (ServerProfile profile in profiles)
            {
                File.WriteAllText(path, SubscriptionParser.BuildConfig(profile, 18081), new UTF8Encoding(false));
                ProcessStartInfo psi = new ProcessStartInfo(Path.GetFullPath(executable), "run -test -config \"" + path + "\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process process = Process.Start(psi))
                {
                    process.OutputDataReceived += delegate { };
                    process.ErrorDataReceived += delegate { };
                    process.BeginOutputReadLine(); process.BeginErrorReadLine();
                    if (!process.WaitForExit(20000)) { process.Kill(); throw new Exception("Core timed out."); }
                    if (process.ExitCode == 0) passed++;
                }
                File.Delete(path);
            }
            Console.WriteLine("Xray config validation (" + label + "): " + passed + "/" + profiles.Count);
            if (passed != profiles.Count) throw new Exception("Core rejected a generated configuration.");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            Directory.Delete(directory);
        }
    }
}
