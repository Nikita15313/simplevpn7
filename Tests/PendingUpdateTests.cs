using System;
using System.IO;
using System.Text;
using SimpleVPN;

// No installer can be started by the default fixtures: every staged manifest is
// missing or deliberately invalid. An optional signed CURRENT release fixture
// exercises cleanup without launching an installer or requiring a private key.
internal static class PendingUpdateTests
{
    private static int checks;
    private static void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; }
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length != 1 && args.Length != 3) throw new ArgumentException("Fixture directory and optional current manifest/signature expected.");
            var store = new LocalStore(Path.Combine(Path.GetFullPath(args[0]), "app"));
            var updater = new UpdateManager(store);
            string pendingPath = Path.Combine(store.Data, "pending-update.dpapi");
            Check(!updater.TryLaunchPending(), "No pending update must preserve normal startup");
            updater.SavePreferences(false, "");
            store.Save("pending-update.dpapi", new PendingUpdate { Manifest = "invalid", Signature = "invalid" });
            Check(!updater.TryLaunchPending() && File.Exists(pendingPath), "Disabled automatic updates leave pending record untouched");
            updater.SavePreferences(true, "");
            Check(!updater.TryLaunchPending(), "Malformed pending update must preserve normal startup");
            Check(File.Exists(pendingPath), "Failed pending update remains diagnosable");
            Check(updater.LoadStatus().IndexOf("не установилось", StringComparison.Ordinal) >= 0, "Failure status is usable and contains no manifest data");
            store.Save("pending-update.dpapi", new PendingUpdate {
                Manifest = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"Version\":\"99.0.0.0\",\"Size\":2048,\"Sha256\":\"" + new string('0', 64) + "\",\"AssetUrl\":\"https://api.github.com/repos/Nikita15313/simplevpn7/releases/assets/1\"}")),
                Signature = Convert.ToBase64String(new byte[128])
            });
            Check(!updater.TryLaunchPending(), "Unsigned pending update must preserve normal startup");
            Check(store.Load<PendingUpdate>("pending-update.dpapi").AttemptUtc == DateTime.MinValue, "Untrusted pending update must never reach installer attempt");
            File.WriteAllBytes(pendingPath, new byte[] { 1, 2, 3, 4 });
            Check(!updater.TryLaunchPending(), "Corrupt DPAPI record must preserve normal startup");
            File.Delete(pendingPath);
            if (args.Length == 3)
            {
                store.Save("pending-update.dpapi", new PendingUpdate {
                    Manifest = Convert.ToBase64String(File.ReadAllBytes(args[1])),
                    Signature = File.ReadAllText(args[2]).Trim()
                });
                Check(!updater.TryLaunchPending(), "Current signed version must not start an installer");
                Check(!File.Exists(pendingPath), "Current signed version clears its obsolete pending record");
            }
            Console.WriteLine("Pending update tests PASS: " + checks + " disabled, malformed, signature and startup-continuation checks; no installer or system-proxy writes.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine("FAIL " + error.GetType().Name + ": " + error.Message); return 1; }
    }
}
