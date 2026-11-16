using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using SimpleVPN;

// CLI: PreparedControllerTests <core-path> <scratch-root>
// Real hidden Xray, synthetic profiles, fake proxy and fake health checker.
// No GUI, external network, native proxy or startup-registry writes.
internal static class PreparedControllerTests
{
    const string Profiles =
        "vless://00112233-4455-6677-8899-aabbccddeeff@server.invalid:443?security=reality&type=tcp&sni=example.invalid&pbk=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA#Fixture-A\n" +
        "vless://11223344-5566-7788-99aa-bbccddeeff00@server.invalid:8443?security=reality&type=tcp&sni=example.invalid&pbk=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA#Fixture-B";
    static string executable, root;
    static int passed;
    static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static int Main(string[] args)
    {
        try
        {
            if (args.Length != 2) throw new ArgumentException("Usage: PreparedControllerTests <core-path> <scratch-root>");
            executable = Path.GetFullPath(args[0]);
            root = Path.Combine(Path.GetFullPath(args[1]), Guid.NewGuid().ToString("N").Substring(0, 8));
            Run("Warm OFF preserves proxy; connect reuses core; reconnect requires fresh health", ReuseAndFreshHealth);
            Run("Server selection replaces stale preparation", Selection);
            Run("Connection options replace stale preparation without startup writes", Options);
            Run("A failed preparation retries a restored core on Connect", FailedPreparationRecovery);
            Run("Shutdown cancels a queued replacement without resurrection", ShutdownQueuedReplacement);
            Run("Repeated immediate shutdown stops preparation", ShutdownImmediately);
            Run("Cancel prepared connection restores proxy while health is blocked", CancelBlockedCheck);
            Run("Shutdown prepared connection rejects late health success", ShutdownBlockedCheck);
            Run("Measure cold and prepared local startup with synthetic health", Benchmark);
            Assert(OwnedProcesses().Count == 0, "Test-owned core remained alive.");
            Console.WriteLine("Prepared controller tests PASS: " + passed + "; CLR: " + Environment.Version);
            Console.WriteLine("Timing is local core/proxy orchestration only; no real HTTPS or Samsung timing claim.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine("FAIL " + error.GetType().Name + ": " + error.Message); return 1; }
        finally
        {
            // Only a process whose executable lies in this unique test directory.
            // A failed assertion must not leave its test core alive.
            if (root != null) foreach (Process process in OwnedProcesses())
            { using (process) { try { process.Kill(); process.WaitForExit(2000); } catch { } } }
        }
    }
    static void Run(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
    static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    static void Until(Func<bool> condition, string failure)
    {
        var timer = Stopwatch.StartNew();
        while (!condition()) { if (timer.ElapsedMilliseconds > 15000) throw new TimeoutException(failure); Thread.Sleep(10); }
    }
    static void Wait(WorkTicket ticket)
    {
        using (var done = new ManualResetEvent(false))
        {
            Exception failure = null;
            ticket.OnCompleted(delegate(Exception error) { failure = error; done.Set(); });
            Assert(done.WaitOne(20000, false), "Background work timed out.");
            if (failure != null) throw new InvalidOperationException("Background work failed: " + failure.GetType().Name);
        }
    }
    static bool PortOpen(int port)
    {
        if (port <= 0) return false;
        using (var socket = new TcpClient())
        {
            try
            {
                IAsyncResult pending = socket.BeginConnect(IPAddress.Loopback, port, null, null);
                using (var ready = pending.AsyncWaitHandle)
                { if (!ready.WaitOne(300, false)) return false; socket.EndConnect(pending); return true; }
            }
            catch (SocketException) { return false; }
        }
    }
    static List<Process> OwnedProcesses()
    {
        var result = new List<Process>();
        foreach (Process process in Process.GetProcessesByName("xray"))
        {
            bool owned = false;
            try { owned = process.MainModule.FileName.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase); } catch { }
            if (owned) result.Add(process); else process.Dispose();
        }
        return result;
    }
    static object Field(object value, string name) { return value.GetType().GetField(name, Private).GetValue(value); }
    static CorePreparation Preparation(AppController controller)
    { lock (Field(controller, "transition")) return (CorePreparation)Field(controller, "prepared"); }
    static CoreProcess Candidate(CorePreparation preparation)
    { lock (Field(preparation, "gate")) return (CoreProcess)Field(preparation, "candidate"); }
    sealed class Warm
    {
        internal CorePreparation Preparation;
        internal CoreProcess Core;
        internal int Port, Pid;
    }
    static Warm Ready(Fixture fixture)
    {
        Until(delegate { return fixture.Controller.IsConnectionPrepared; }, "Preparation never became ready.");
        var preparation = Preparation(fixture.Controller);
        var candidate = Candidate(preparation);
        Assert(candidate != null && candidate.IsAlive && PortOpen(candidate.Port), "Prepared core is not listening.");
        return new Warm { Preparation = preparation, Core = candidate, Port = candidate.Port, Pid = candidate.Pid };
    }
    static void Stopped(Warm warm)
    { Until(delegate { return !warm.Core.IsAlive && !PortOpen(warm.Port); }, "Previous core/port survived."); }
    sealed class FakeProxy : IProxyPlatform
    {
        internal volatile bool Applied;
        internal int ApplyCalls, RestoreCalls, Port;
        public ProxySnapshot Capture()
        { return new ProxySnapshot { Flags = 1, Entries = new List<RegistryEntry> { new RegistryEntry { Key = "", Name = "ProxyEnable", Exists = true, Kind = 4, Text = "0" } } }; }
        public void Apply(string endpoint)
        { Port = Int32.Parse(endpoint.Substring(endpoint.LastIndexOf(':') + 1)); Interlocked.Increment(ref ApplyCalls); Applied = true; }
        public bool Owns(string endpoint) { return Applied && endpoint == "127.0.0.1:" + Port; }
        public void Restore(ProxySnapshot snapshot) { Interlocked.Increment(ref RestoreCalls); Applied = false; }
    }
    sealed class Fixture : IDisposable
    {
        internal readonly LocalStore Store;
        internal readonly FakeProxy Proxy = new FakeProxy();
        internal readonly AppController Controller;
        internal Fixture(string name, Func<int, OperationCancel, bool> check, bool prepare, bool copyCore = true)
        {
            Store = new LocalStore(Path.Combine(root, name));
            string directory = Path.Combine(Store.Root, "core"); Directory.CreateDirectory(directory);
            if (copyCore) File.Copy(executable, Path.Combine(directory, "xray.exe"));
            Store.Save("servers.dpapi", Profiles);
            Store.Save("settings.dpapi", new AppSettings {
                SubscriptionUrl = "https://fixture.example.test/subscription",
                SelectedServerId = SubscriptionParser.Parse(Profiles)[0].Id, LastUpdatedUtc = DateTime.UtcNow
            });
            Controller = new AppController(Store, Proxy, delegate { return Profiles; }, check, false, prepare);
            Wait(Controller.InitializeAsync());
        }
        internal string Log { get { string path = Path.Combine(Store.Data, "diagnostic.log"); return File.Exists(path) ? File.ReadAllText(path) : ""; } }
        public void Dispose() { Controller.Shutdown(); }
    }

    static void ReuseAndFreshHealth()
    {
        int health = 0;
        using (var f = new Fixture("reuse", delegate { return Interlocked.Increment(ref health) != 2; }, true))
        {
            Warm first = Ready(f);
            Assert(!f.Controller.IsConnected && !f.Proxy.Applied && f.Proxy.ApplyCalls == 0 && f.Proxy.RestoreCalls == 0 && health == 0, "Preparation changed OFF proxy/health state.");
            Assert(!File.Exists(Path.Combine(f.Store.Data, "proxy-session.dpapi")), "Preparation created a proxy journal.");
            Wait(f.Controller.ToggleAsync());
            Assert(f.Controller.IsConnected && f.Proxy.Port == first.Port && first.Core.Pid == first.Pid && first.Core.IsAlive, "Connect did not adopt prepared process.");
            Assert(f.Log.Contains("CORE_PREPARED_REUSED") && health == 1, "Prepared reuse/fresh health was not recorded.");
            Wait(f.Controller.ToggleAsync()); Stopped(first);
            Warm second = Ready(f);
            Assert(second.Pid != first.Pid && !f.Proxy.Applied && health == 1, "Disconnect did not prepare a new OFF core.");
            Wait(f.Controller.ToggleAsync()); Stopped(second);
            Assert(health == 2 && !f.Controller.IsConnected && !f.Proxy.Applied && f.Proxy.RestoreCalls == 2, "Second connection reused stale health success.");
        }
    }
    static void Selection()
    {
        using (var f = new Fixture("selection", delegate { return true; }, true))
        {
            Warm old = Ready(f);
            f.Controller.SelectServer(f.Controller.Servers[1]);
            Until(delegate { return f.Controller.IsConnectionPrepared && !Object.ReferenceEquals(Preparation(f.Controller), old.Preparation); }, "New server was not prepared.");
            Warm fresh = Ready(f); Stopped(old);
            Assert(fresh.Preparation.Key == ConnectionOptions.BuildConfig(f.Controller.Servers[1], 49151, f.Controller.CopySettings()), "Prepared key is stale after selection.");
            Assert(f.Proxy.ApplyCalls == 0 && !f.Proxy.Applied, "Selection enabled proxy while OFF.");
            Wait(f.Controller.ToggleAsync());
            Assert(f.Controller.IsConnected && f.Proxy.Port == fresh.Port, "Selected server did not use new preparation.");
        }
    }
    static void Options()
    {
        using (var f = new Fixture("options", delegate { return true; }, true))
        {
            Warm old = Ready(f);
            // Valid SavePreferencesAsync also writes the real startup registry.
            // Mutate public settings + synthetic refresh to exercise PrepareSelected
            // and key comparison without invoking that unrelated platform operation.
            f.Controller.Settings.DnsMode = "google";
            Wait(f.Controller.RefreshAsync());
            Until(delegate { return f.Controller.IsConnectionPrepared && !Object.ReferenceEquals(Preparation(f.Controller), old.Preparation); }, "Changed options were not prepared.");
            Warm fresh = Ready(f); Stopped(old);
            Assert(fresh.Preparation.Key != old.Preparation.Key, "DNS change kept stale key.");
            Assert(fresh.Preparation.Key == ConnectionOptions.BuildConfig(f.Controller.Servers[0], 49151, f.Controller.CopySettings()), "DNS preparation key is inconsistent.");
            Assert(f.Proxy.ApplyCalls == 0 && !f.Proxy.Applied, "Preference preparation changed proxy.");
            Wait(f.Controller.ToggleAsync());
            Assert(f.Controller.IsConnected && f.Proxy.Port == fresh.Port, "Connection reused stale options.");
        }
    }
    static void ShutdownQueuedReplacement()
    {
        using (var f = new Fixture("shutdown-queued", delegate { return true; }, true))
        {
            Warm old = Ready(f);
            // Hold the old core's gate so replacement disposal cannot finish.
            // Shutdown invalidates the generation while that worker is queued.
            lock (Field(old.Core, "gate"))
            {
                f.Controller.SelectServer(f.Controller.Servers[1]);
                Assert(Preparation(f.Controller) == null, "Replacement was not queued.");
                f.Controller.Shutdown();
            }
            Stopped(old);
            Thread.Sleep(150);
            Assert(!f.Controller.IsConnectionPrepared && Preparation(f.Controller) == null && !f.Proxy.Applied && f.Proxy.ApplyCalls == 0, "Replacement resurrected after shutdown.");
        }
    }
    static void FailedPreparationRecovery()
    {
        int health = 0;
        using (var f = new Fixture("failed-preparation", delegate { Interlocked.Increment(ref health); return true; }, true, false))
        {
            CorePreparation failed = Preparation(f.Controller);
            Assert(failed != null, "Missing core did not create a preparation.");
            Until(delegate { return failed.HasCompleted; }, "Missing core preparation did not complete.");
            Assert(failed.Failed && !f.Controller.IsConnectionPrepared && health == 0 && f.Proxy.ApplyCalls == 0, "Failed preparation affected OFF state.");
            File.Copy(executable, Path.Combine(Path.Combine(f.Store.Root, "core"), "xray.exe"));
            Wait(f.Controller.ToggleAsync());
            Assert(f.Controller.IsConnected && f.Proxy.Applied && PortOpen(f.Proxy.Port) && health == 1, "Connect repeated stale preparation error after the core was restored.");
            Assert(!f.Log.Contains("CORE_PREPARED_REUSED") && !f.Log.Contains("CONNECT_FAILED"), "Failed preparation was reported as a reused core or a failed connection.");
        }
    }
    static void ShutdownImmediately()
    {
        for (int i = 0; i < 4; i++)
        {
            using (var f = new Fixture("shutdown-immediate-" + i, delegate { return true; }, true))
            {
                CorePreparation pending = Preparation(f.Controller);
                f.Controller.Shutdown();
                if (pending != null) Until(delegate { return pending.HasCompleted; }, "Preparation worker did not finish after shutdown.");
                Assert(!f.Controller.IsConnectionPrepared && Preparation(f.Controller) == null && f.Proxy.ApplyCalls == 0 && !f.Controller.IsConnected, "Immediate shutdown left preparation/connection.");
            }
        }
    }
    static void CancelBlockedCheck()
    {
        int health = 0;
        using (var entered = new ManualResetEvent(false))
        using (var release = new ManualResetEvent(false))
        using (var f = new Fixture("cancel-check", delegate { Interlocked.Increment(ref health); entered.Set(); release.WaitOne(); return true; }, true))
        {
            try
            {
                Warm old = Ready(f);
                WorkTicket operation = f.Controller.ToggleAsync();
                Assert(entered.WaitOne(10000, false) && f.Controller.CanCancelConnection, "Prepared connect did not reach cancellable health.");
                f.Controller.CancelConnection();
                Until(delegate { return !f.Proxy.Applied; }, "Cancellation waited for blocked health.");
                Stopped(old); release.Set(); Wait(operation);
                Ready(f);
                Assert(!f.Controller.IsConnected && !f.Controller.CanCancelConnection && f.Proxy.ApplyCalls == 1 && health == 1, "Cancelled late success resurrected connection.");
                Wait(f.Controller.ToggleAsync());
                Assert(f.Controller.IsConnected && health == 2, "Cancellation poisoned next prepared connection.");
            }
            finally { release.Set(); }
        }
    }
    static void ShutdownBlockedCheck()
    {
        using (var entered = new ManualResetEvent(false))
        using (var release = new ManualResetEvent(false))
        using (var f = new Fixture("shutdown-check", delegate { entered.Set(); release.WaitOne(); return true; }, true))
        {
            try
            {
                Warm old = Ready(f);
                WorkTicket operation = f.Controller.ToggleAsync();
                Assert(entered.WaitOne(10000, false), "Prepared connect did not reach health.");
                f.Controller.Shutdown(); Stopped(old); release.Set(); Wait(operation);
                Assert(!f.Proxy.Applied && !f.Controller.IsConnected && !f.Controller.IsConnectionPrepared && Preparation(f.Controller) == null, "Late health resurrected closed controller.");
            }
            finally { release.Set(); }
        }
    }
    static long Median(List<long> values) { values.Sort(); return values[values.Count / 2]; }
    static void Benchmark()
    {
        var cold = new List<long>(); var warm = new List<long>(); var preparation = new List<long>();
        using (var f = new Fixture("timing-cold", delegate { return true; }, false))
        {
            for (int i = 0; i < 3; i++)
            {
                var timer = Stopwatch.StartNew(); Wait(f.Controller.ToggleAsync()); timer.Stop();
                Assert(f.Controller.IsConnected, "Cold benchmark connect failed."); cold.Add(timer.ElapsedMilliseconds);
                Wait(f.Controller.ToggleAsync());
            }
        }
        using (var f = new Fixture("timing-warm", delegate { return true; }, true))
        {
            for (int i = 0; i < 3; i++)
            {
                var prepareTimer = Stopwatch.StartNew(); Ready(f); prepareTimer.Stop(); preparation.Add(prepareTimer.ElapsedMilliseconds);
                var timer = Stopwatch.StartNew(); Wait(f.Controller.ToggleAsync()); timer.Stop();
                Assert(f.Controller.IsConnected && f.Log.Contains("CORE_PREPARED_REUSED"), "Prepared benchmark connect failed."); warm.Add(timer.ElapsedMilliseconds);
                Wait(f.Controller.ToggleAsync());
            }
        }
        Console.WriteLine("LOCAL_TIMING synthetic_health samples=3 cold_connect_median_ms=" + Median(cold) + " prepared_connect_median_ms=" + Median(warm) + " wait_for_preparation_median_ms=" + Median(preparation));
    }
}
