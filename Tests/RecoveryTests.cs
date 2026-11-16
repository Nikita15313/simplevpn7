using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using SimpleVPN;

// Isolated regression tests. Every ProxyManager receives FakeProxyPlatform.
// No calls to the native proxy platform, Registry.SetValue, SetStartup,
// external network, GUI, or VPN core processes are made by this executable.
internal static class RecoveryTests
{
    private static string root;
    private static int passed;
    private static int failed;
    private static readonly List<string> report = new List<string>();

    private static int Main(string[] args)
    {
        string outputRoot = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "recovery-test-data"));
        root = Path.Combine(outputRoot, "run-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(root);
        Line("Simple VPN recovery regression tests");
        Line("Backend: fake in-memory proxy; real user-scoped DPAPI and filesystem only.");
        Run("Flags, PAC, registry types and missing values round-trip exactly", RoundTrip);
        Run("Failed apply rolls back prior state and clears recovery journal", ApplyRollback);
        Run("Failed apply plus failed rollback retains journal for a later retry", ApplyRollbackFailure);
        Run("New manager recovers a simulated crashed session from disk", CrashRecovery);
        Run("Repeated disconnect is idempotent", IdempotentDisconnect);
        Run("Delayed watchdog with wrong PID cannot restore a newer session", WatchdogWrongPid);
        Run("Reused PID with wrong start time cannot restore a newer session", WatchdogWrongStart);
        Run("Matching watchdog owner restores the session", WatchdogMatchingOwner);
        Run("External proxy replacement remains untouched", ExternalProxyReplacement);
        Run("External proxy disabling remains untouched", ExternalProxyDisable);
        Run("Failed disconnect restore retains encrypted journal and retries", FailedRestoreRetry);
        Run("Pending recovery prevents capturing over the original snapshot", PendingJournalBlocksApply);
        Run("Explicit forced recovery restores snapshot despite external change", ForcedRestore);
        Run("Settings and cached credentials are encrypted; no plaintext or temp residue", EncryptedSecrets);
        Run("Corrupted encrypted journal fails closed and is retained", CorruptJournal);
        Run("Diagnostic logging accepts event codes and rejects URLs or injected text", SecretSafeLog);
        Line("RESULT: " + passed + " passed; " + failed + " failed.");
        File.WriteAllLines(Path.Combine(outputRoot, "recovery-results.txt"), report.ToArray(), Encoding.UTF8);
        return failed == 0 ? 0 : 1;
    }

    private static void Run(string name, Action test)
    {
        try { test(); passed++; Line("PASS " + name); }
        catch (Exception error) { failed++; Line("FAIL " + name + ": " + error.GetType().Name + " " + error.Message); }
    }

    private static void Line(string value) { report.Add(value); Console.WriteLine(value); }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void ExpectFailure(Action operation)
    {
        bool threw = false;
        try { operation(); } catch (IOException) { threw = true; }
        Assert(threw, "Expected an I/O failure.");
    }

    private sealed class Fixture
    {
        internal readonly LocalStore Store;
        internal readonly FakeProxyPlatform Platform;
        internal readonly ProxyManager Manager;
        internal readonly string Original;
        internal Fixture(string name)
        {
            Store = new LocalStore(Path.Combine(Path.Combine(root, "Русская папка с пробелами"), name));
            Platform = new FakeProxyPlatform(OriginalSnapshot());
            Manager = new ProxyManager(Store, Platform);
            Original = State(Platform.Current);
        }
        internal void AssertOriginal()
        {
            Assert(State(Platform.Current) == Original, "Original configuration was not restored exactly.");
        }
    }

    private static ProxySnapshot OriginalSnapshot()
    {
        return new ProxySnapshot
        {
            Flags = 13, // DIRECT | AUTO_PROXY_URL | AUTO_DETECT
            Entries = new List<RegistryEntry>
            {
                Entry("", "ProxyEnable", RegistryValueKind.DWord, "0"),
                Entry("", "ProxyServer", RegistryValueKind.String, "old-proxy.example.test:8080"),
                Entry("", "ProxyOverride", RegistryValueKind.ExpandString, "%USERDOMAIN%;<local>;*.example.test"),
                Entry("", "AutoConfigURL", RegistryValueKind.String, "https://pac.example.test/settings.pac"),
                new RegistryEntry { Key = "", Name = "AutoDetect", Exists = false },
                Entry("\\Connections", "DefaultConnectionSettings", RegistryValueKind.Binary, Convert.ToBase64String(new byte[] { 70, 0, 0, 0, 255, 1, 0, 13, 22, 48, 0 })),
                new RegistryEntry { Key = "\\Connections", Name = "SavedLegacySettings", Exists = false }
            }
        };
    }

    private static RegistryEntry Entry(string key, string name, RegistryValueKind kind, string text)
    {
        return new RegistryEntry { Key = key, Name = name, Kind = (int)kind, Text = text, Exists = true };
    }

    private static string State(ProxySnapshot snapshot)
    {
        return LocalStore.Json().Serialize(new { snapshot.Flags, snapshot.Entries });
    }

    private static ProxySnapshot Clone(ProxySnapshot snapshot)
    {
        return LocalStore.Json().Deserialize<ProxySnapshot>(LocalStore.Json().Serialize(snapshot));
    }

    private sealed class FakeProxyPlatform : IProxyPlatform
    {
        internal ProxySnapshot Current;
        internal int CaptureCalls;
        internal int ApplyCalls;
        internal int RestoreCalls;
        internal bool FailApplyAfterMutation;
        internal bool FailRestore;

        internal FakeProxyPlatform(ProxySnapshot initial) { Current = Clone(initial); }
        public ProxySnapshot Capture() { CaptureCalls++; return Clone(Current); }
        public void Apply(string endpoint)
        {
            ApplyCalls++;
            Current.Flags = 3;
            Set("ProxyEnable", "1", RegistryValueKind.DWord);
            Set("ProxyServer", endpoint, RegistryValueKind.String);
            Set("ProxyOverride", "<local>;localhost;127.*;[::1]", RegistryValueKind.String);
            Set("AutoConfigURL", "", RegistryValueKind.String);
            Set("AutoDetect", "0", RegistryValueKind.DWord);
            Current.Entries.Find(e => e.Name == "DefaultConnectionSettings").Text = Convert.ToBase64String(new byte[] { 1, 2, 3, 4 });
            if (FailApplyAfterMutation) throw new IOException("SIMULATED_APPLY_FAILURE");
        }
        public void Restore(ProxySnapshot snapshot)
        {
            RestoreCalls++;
            if (FailRestore) throw new IOException("SIMULATED_RESTORE_FAILURE");
            Current = Clone(snapshot);
        }
        public bool Owns(string endpoint)
        {
            var enable = Current.Entries.Find(e => e.Key == "" && e.Name == "ProxyEnable");
            var server = Current.Entries.Find(e => e.Key == "" && e.Name == "ProxyServer");
            return enable != null && enable.Exists && enable.Text == "1" && server != null && server.Exists && server.Text == endpoint;
        }
        internal void Set(string name, string value, RegistryValueKind kind)
        {
            var entry = Current.Entries.Find(e => e.Key == "" && e.Name == name);
            if (entry == null) { entry = new RegistryEntry { Key = "", Name = name }; Current.Entries.Add(entry); }
            entry.Exists = true;
            entry.Text = value;
            entry.Kind = (int)kind;
        }
    }

    private static void RoundTrip()
    {
        var f = new Fixture("roundtrip");
        f.Manager.Apply(43001);
        Assert(f.Platform.Owns("127.0.0.1:43001"), "Expected applied endpoint.");
        var journal = f.Store.Load<ProxySnapshot>("proxy-session.dpapi");
        Assert(State(journal) == f.Original, "Journal lost flags, PAC, registry types or missing values.");
        Assert(journal.OwnerPid == Process.GetCurrentProcess().Id && journal.OwnerStart > 0, "Missing process ownership.");
        Assert(f.Manager.Restore(false), "Restore should report success.");
        f.AssertOriginal();
        Assert(!File.Exists(f.Manager.Journal), "Journal was not cleared after successful restore.");
    }

    private static void ApplyRollback()
    {
        var f = new Fixture("apply-rollback");
        f.Platform.FailApplyAfterMutation = true;
        ExpectFailure(() => f.Manager.Apply(43002));
        f.AssertOriginal();
        Assert(f.Platform.RestoreCalls == 1, "Rollback was not called once.");
        Assert(!File.Exists(f.Manager.Journal), "Successful rollback left a stale journal.");
    }

    private static void ApplyRollbackFailure()
    {
        var f = new Fixture("apply-rollback-failure");
        f.Platform.FailApplyAfterMutation = true;
        f.Platform.FailRestore = true;
        ExpectFailure(() => f.Manager.Apply(43003));
        Assert(File.Exists(f.Manager.Journal), "Failed rollback lost recovery journal.");
        f.Platform.FailRestore = false;
        Assert(new ProxyManager(f.Store, f.Platform).Restore(false), "Recovery retry did not restore.");
        f.AssertOriginal();
    }

    private static void CrashRecovery()
    {
        var f = new Fixture("crash-recovery");
        f.Manager.Apply(43004);
        var reopenedStore = new LocalStore(f.Store.Root);
        var restartedManager = new ProxyManager(reopenedStore, f.Platform);
        Assert(restartedManager.Restore(false), "Restarted manager did not recover the session.");
        f.AssertOriginal();
        Assert(!File.Exists(restartedManager.Journal), "Recovered journal remains on disk.");
    }

    private static void IdempotentDisconnect()
    {
        var f = new Fixture("idempotent");
        Assert(!f.Manager.Restore(false), "Empty recovery should be a no-op.");
        f.Manager.Apply(43005);
        Assert(f.Manager.Restore(false), "First disconnect failed.");
        Assert(!f.Manager.Restore(false), "Repeated disconnect should be a no-op.");
        Assert(f.Platform.RestoreCalls == 1, "Repeated disconnect wrote the backend again.");
        f.AssertOriginal();
    }

    private static void WatchdogWrongPid()
    {
        var f = new Fixture("watchdog-wrong-pid");
        f.Manager.Apply(43006);
        var journal = f.Store.Load<ProxySnapshot>("proxy-session.dpapi");
        byte[] before = File.ReadAllBytes(f.Manager.Journal);
        Assert(!f.Manager.RestoreForOwner(journal.OwnerPid + 1, journal.OwnerStart), "Wrong watchdog restored the session.");
        Assert(f.Platform.RestoreCalls == 0 && f.Platform.Owns(journal.Endpoint), "Wrong watchdog mutated the active endpoint.");
        Assert(before.SequenceEqual(File.ReadAllBytes(f.Manager.Journal)), "Wrong watchdog changed the newer journal.");
        f.Manager.Restore(false);
    }

    private static void WatchdogWrongStart()
    {
        var f = new Fixture("watchdog-wrong-start");
        f.Manager.Apply(43007);
        var journal = f.Store.Load<ProxySnapshot>("proxy-session.dpapi");
        Assert(!f.Manager.RestoreForOwner(journal.OwnerPid, journal.OwnerStart - 1), "Reused PID restored the session.");
        Assert(f.Platform.RestoreCalls == 0 && File.Exists(f.Manager.Journal), "Reused PID changed state.");
        f.Manager.Restore(false);
    }

    private static void WatchdogMatchingOwner()
    {
        var f = new Fixture("watchdog-match");
        f.Manager.Apply(43008);
        var journal = f.Store.Load<ProxySnapshot>("proxy-session.dpapi");
        Assert(f.Manager.RestoreForOwner(journal.OwnerPid, journal.OwnerStart), "Matching watchdog did not restore.");
        f.AssertOriginal();
        Assert(!File.Exists(f.Manager.Journal), "Matching watchdog left a journal.");
    }

    private static void ExternalProxyReplacement()
    {
        var f = new Fixture("external-replacement");
        f.Manager.Apply(43009);
        f.Platform.Set("ProxyServer", "new-proxy.example.test:8888", RegistryValueKind.String);
        string changed = State(f.Platform.Current);
        Assert(!f.Manager.Restore(false), "Externally changed settings should not be restored.");
        Assert(State(f.Platform.Current) == changed && f.Platform.RestoreCalls == 0, "External settings were overwritten.");
        Assert(!File.Exists(f.Manager.Journal), "External replacement should retire this session.");
        Assert(File.ReadAllText(Path.Combine(f.Store.Data, "diagnostic.log")).Contains("PROXY_EXTERNALLY_CHANGED"), "Missing external-change event.");
    }

    private static void ExternalProxyDisable()
    {
        var f = new Fixture("external-disabled");
        f.Manager.Apply(43010);
        f.Platform.Set("ProxyEnable", "0", RegistryValueKind.DWord);
        string changed = State(f.Platform.Current);
        Assert(!f.Manager.Restore(false), "User-disabled proxy should not be restored.");
        Assert(State(f.Platform.Current) == changed && f.Platform.RestoreCalls == 0, "User-disabled settings were overwritten.");
    }

    private static void FailedRestoreRetry()
    {
        var f = new Fixture("restore-retry");
        f.Manager.Apply(43011);
        byte[] originalJournal = File.ReadAllBytes(f.Manager.Journal);
        f.Platform.FailRestore = true;
        ExpectFailure(() => f.Manager.Restore(false));
        Assert(File.Exists(f.Manager.Journal), "Failed restore lost journal.");
        Assert(originalJournal.SequenceEqual(File.ReadAllBytes(f.Manager.Journal)), "Failed restore rewrote journal.");
        f.Platform.FailRestore = false;
        Assert(f.Manager.Restore(false), "Second restoration attempt did not succeed.");
        f.AssertOriginal();
        Assert(f.Platform.RestoreCalls == 2 && !File.Exists(f.Manager.Journal), "Retry lifecycle is incorrect.");
    }

    private static void PendingJournalBlocksApply()
    {
        var f = new Fixture("pending-journal");
        f.Manager.Apply(43012);
        byte[] journal = File.ReadAllBytes(f.Manager.Journal);
        ExpectFailure(() => f.Manager.Apply(43013));
        Assert(f.Platform.CaptureCalls == 1 && f.Platform.ApplyCalls == 1, "Reapply captured over the original state.");
        Assert(journal.SequenceEqual(File.ReadAllBytes(f.Manager.Journal)), "Reapply changed recovery journal.");
        f.Manager.Restore(false);
        f.AssertOriginal();
    }

    private static void ForcedRestore()
    {
        var f = new Fixture("forced-recovery");
        f.Manager.Apply(43014);
        f.Platform.Set("ProxyEnable", "0", RegistryValueKind.DWord);
        Assert(f.Manager.Restore(true), "Explicit force should restore original snapshot.");
        f.AssertOriginal();
    }

    private static void EncryptedSecrets()
    {
        var f = new Fixture("encrypted-settings");
        const string secret = "SYNTHETIC_SUBSCRIPTION_TOKEN_9dbe87";
        const string address = "https://subscription.example.test/s/" + secret;
        var settings = new AppSettings { SubscriptionUrl = address, SelectedServerId = "test-server", AutoConnect = true };
        f.Store.Save("settings.dpapi", settings);
        const string cache = "trojan://SYNTHETIC_PASSWORD_37d1@test.example.test:443#Synthetic";
        f.Store.Save("servers.dpapi", cache);
        Assert(f.Store.Load<AppSettings>("settings.dpapi").SubscriptionUrl == address, "DPAPI settings round-trip failed.");
        Assert(f.Store.Load<string>("servers.dpapi") == cache, "DPAPI credentials round-trip failed.");
        foreach (string file in Directory.GetFiles(f.Store.Data))
        {
            byte[] bytes = File.ReadAllBytes(file);
            Assert(!Contains(bytes, Encoding.UTF8.GetBytes(secret)) && !Contains(bytes, Encoding.Unicode.GetBytes(secret)), "Plaintext subscription secret found on disk.");
            Assert(!Contains(bytes, Encoding.UTF8.GetBytes("SYNTHETIC_PASSWORD_37d1")), "Plaintext password found on disk.");
        }
        settings.AutoConnect = false;
        f.Store.Save("settings.dpapi", settings);
        Assert(!f.Store.Load<AppSettings>("settings.dpapi").AutoConnect, "Atomic replacement did not persist.");
        Assert(Directory.GetFiles(f.Store.Data, "*.tmp").Length == 0, "Atomic save left temporary files.");
    }

    private static bool Contains(byte[] bytes, byte[] marker)
    {
        for (int i = 0; i <= bytes.Length - marker.Length; i++)
        {
            bool equal = true;
            for (int j = 0; j < marker.Length; j++) if (bytes[i + j] != marker[j]) { equal = false; break; }
            if (equal) return true;
        }
        return false;
    }

    private static void CorruptJournal()
    {
        var f = new Fixture("corrupt-journal");
        f.Manager.Apply(43015);
        byte[] original = File.ReadAllBytes(f.Manager.Journal);
        byte[] damaged = (byte[])original.Clone();
        damaged[damaged.Length - 12] ^= 0x40;
        File.WriteAllBytes(f.Manager.Journal, damaged);
        bool rejected = false;
        try { f.Manager.Restore(false); } catch (CryptographicException) { rejected = true; }
        Assert(rejected, "Tampered DPAPI journal was accepted.");
        Assert(File.Exists(f.Manager.Journal) && f.Platform.RestoreCalls == 0, "Corrupted journal mutated state or was deleted.");
        File.WriteAllBytes(f.Manager.Journal, original);
        Assert(f.Manager.Restore(false), "Authentic journal did not recover after corruption test.");
        f.AssertOriginal();
    }

    private static void SecretSafeLog()
    {
        var f = new Fixture("safe-log");
        const string secretUrl = "https://subscription.example.test/s/SYNTHETIC_TOKEN_481";
        f.Store.Log("CONNECTED");
        f.Store.Log(secretUrl);
        f.Store.Log("https://example.test/uuid=synthetic-private-value");
        f.Store.Log("FAILED\n" + secretUrl);
        f.Store.Log("error: " + secretUrl);
        f.Store.Log(new string('A', 65));
        f.Store.Log("DISCONNECTED");
        string[] lines = File.ReadAllLines(Path.Combine(f.Store.Data, "diagnostic.log"));
        Assert(lines.Length == 2 && lines[0].EndsWith(" CONNECTED") && lines[1].EndsWith(" DISCONNECTED"), "Log accepted non-event or injected content.");
        Assert(!String.Join("\n", lines).Contains("http") && !String.Join("\n", lines).Contains("SYNTHETIC"), "Secret found in diagnostic log.");
    }
}

