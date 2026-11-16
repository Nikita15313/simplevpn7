using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using SimpleVPN;

// Genuine core process/loopback tests with fake HTTP and fake proxy settings.
// No GUI, external requests, native proxy writes, startup writes or watchdogs.
internal static class LifecycleTests
{
    private static string executable;
    private static string root;
    private static int passed;
    private const string Link = "vless://00112233-4455-6677-8899-aabbccddeeff@server.invalid:443?security=reality&type=tcp&sni=www.example.com&pbk=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA#Synthetic";

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length != 2) throw new ArgumentException("Usage: LifecycleTests <core-path> <scratch-root>");
            executable = Path.GetFullPath(args[0]);
            root = Path.Combine(Path.GetFullPath(args[1]), Guid.NewGuid().ToString("N").Substring(0, 8));
            Run("Operation cancellation registration, removal, late registration", Cancellation);
            Run("Connect, disconnect and reconnect use the real hidden core", Reconnect);
            Run("Repeated click while busy does not start another operation", DoubleClick);
            Run("Shutdown during blocked HTTPS restores immediately with no late reconnect", CloseDuringCheck);
            Run("Immediate shutdown during start leaves no applied proxy", CloseDuringStart);
            Run("Failed HTTPS verification rolls back the fake proxy and stops core", FailedCheck);
            Run("Restore failure keeps actionable status and retries on shutdown", FailedRestore);
            Run("Cancel during blocked check restores immediately and reconnects", CancelDuringCheck);
            Run("Invalid routing input reports through ticket without dropping connection", InvalidPreferences);
            Console.WriteLine("Lifecycle checks passed: " + passed + "; CLR: " + Environment.Version);
            return 0;
        }
        catch (Exception error) { Console.WriteLine("FAIL " + error.GetType().Name + ": " + error.Message); return 1; }
    }
    private static void Run(string name, Action test) { test(); passed++; Console.WriteLine("PASS " + name); }
    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Wait(WorkTicket ticket)
    {
        var done = new ManualResetEvent(false);
        Exception failure = null;
        ticket.OnCompleted(delegate(Exception error) { failure = error; done.Set(); });
        if (!done.WaitOne(20000, false)) throw new TimeoutException("Background operation did not complete.");
        done.Close();
        if (failure != null) throw new InvalidOperationException("Background operation failed: " + failure.GetType().Name);
    }
    private static bool PortOpen(int port)
    {
        if (port == 0) return false;
        using (var client = new TcpClient())
        {
            try
            {
                IAsyncResult pending = client.BeginConnect(IPAddress.Loopback, port, null, null);
                using (var ready = pending.AsyncWaitHandle)
                {
                    if (!ready.WaitOne(500, false)) return false;
                    client.EndConnect(pending); return true;
                }
            }
            catch (SocketException) { return false; }
        }
    }
    private sealed class FakeProxy : IProxyPlatform
    {
        internal bool Applied;
        internal bool FailRestore;
        internal int ApplyCalls;
        internal int RestoreCalls;
        internal int Port;
        public ProxySnapshot Capture()
        {
            return new ProxySnapshot { Flags = 13, Entries = new List<RegistryEntry>
            { new RegistryEntry { Key = "", Name = "ProxyEnable", Exists = true, Kind = 4, Text = "0" } } };
        }
        public void Apply(string endpoint)
        {
            ApplyCalls++; Applied = true; Port = Int32.Parse(endpoint.Substring(endpoint.LastIndexOf(':') + 1));
        }
        public bool Owns(string endpoint) { return Applied && endpoint == "127.0.0.1:" + Port; }
        public void Restore(ProxySnapshot snapshot)
        {
            RestoreCalls++; if (FailRestore) throw new IOException("SYNTHETIC_RESTORE_FAILURE"); Applied = false;
        }
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly LocalStore Store;
        internal readonly FakeProxy Proxy = new FakeProxy();
        internal readonly AppController Controller;
        internal Fixture(string name, Func<int, OperationCancel, bool> checker)
        {
            Store = new LocalStore(Path.Combine(root, name));
            string coreDirectory = Path.Combine(Store.Root, "core"); Directory.CreateDirectory(coreDirectory);
            File.Copy(executable, Path.Combine(coreDirectory, "xray.exe"));
            ServerProfile profile = SubscriptionParser.Parse(Link)[0];
            Store.Save("servers.dpapi", Link);
            Store.Save("settings.dpapi", new AppSettings
            {
                SubscriptionUrl = "https://fixture.example.test/subscription",
                SelectedServerId = profile.Id, LastUpdatedUtc = DateTime.UtcNow
            });
            Controller = new AppController(Store, Proxy, delegate { return Link; }, checker, false);
            Wait(Controller.InitializeAsync());
            Assert(Controller.Servers.Count == 1 && !Controller.IsBusy, "Initialization did not restore cached server.");
        }
        public void Dispose() { Controller.Shutdown(); }
    }
    private static void Cancellation()
    {
        using (var cancel = new OperationCancel())
        {
            int called = 0;
            IDisposable removed = cancel.Register(delegate { called += 100; }); removed.Dispose();
            cancel.Register(delegate { called++; }); cancel.Cancel(); cancel.Cancel();
            cancel.Register(delegate { called += 10; });
            Assert(called == 11 && cancel.IsCancellationRequested && cancel.Wait(0), "Cancellation callback semantics failed.");
            bool threw = false;
            try { cancel.ThrowIfCancellationRequested(); } catch (OperationCancelledException) { threw = true; }
            Assert(threw, "Cancellation was not reported.");
        }
    }
    private static void Reconnect()
    {
        using (var f = new Fixture("reconnect", delegate { return true; }))
        {
            Wait(f.Controller.ToggleAsync());
            Assert(f.Controller.IsConnected && f.Proxy.Applied && PortOpen(f.Proxy.Port), "Core did not listen after connect.");
            Wait(f.Controller.ToggleAsync());
            Assert(!f.Controller.IsConnected, "Disconnect kept controller connected."); Assert(!f.Proxy.Applied, "Disconnect kept fake proxy applied; restores=" + f.Proxy.RestoreCalls); Assert(!PortOpen(f.Proxy.Port), "Disconnect left core port open.");
            Wait(f.Controller.ToggleAsync());
            Assert(f.Controller.IsConnected && f.Proxy.ApplyCalls == 2 && PortOpen(f.Proxy.Port), "Reconnect failed.");
        }
    }
    private static void DoubleClick()
    {
        using (var entered = new ManualResetEvent(false))
        using (var release = new ManualResetEvent(false))
        using (var f = new Fixture("double-click", delegate { entered.Set(); release.WaitOne(); return true; }))
        {
            WorkTicket first = f.Controller.ToggleAsync();
            Assert(entered.WaitOne(10000, false), "Core did not reach connectivity check.");
            Wait(f.Controller.ToggleAsync());
            Assert(f.Proxy.ApplyCalls == 1 && f.Controller.IsBusy, "Second click started a concurrent transition.");
            release.Set(); Wait(first);
        }
    }
    private static void CloseDuringCheck()
    {
        using (var entered = new ManualResetEvent(false))
        using (var release = new ManualResetEvent(false))
        using (var f = new Fixture("close-check", delegate { entered.Set(); release.WaitOne(); return true; }))
        {
            WorkTicket pending = f.Controller.ToggleAsync();
            Assert(entered.WaitOne(10000, false), "Core did not reach connectivity check.");
            var timer = Stopwatch.StartNew(); f.Controller.Shutdown(); timer.Stop();
            Assert(timer.ElapsedMilliseconds < 3000, "Shutdown waited for the blocked network worker.");
            Assert(!f.Proxy.Applied && !f.Controller.IsConnected && !PortOpen(f.Proxy.Port), "Shutdown did not recover immediately.");
            release.Set(); Wait(pending);
            Assert(!f.Proxy.Applied && !f.Controller.IsConnected, "Late worker completion re-enabled connection.");
        }
    }
    private static void CloseDuringStart()
    {
        using (var f = new Fixture("close-start", delegate { return true; }))
        {
            WorkTicket pending = f.Controller.ToggleAsync(); f.Controller.Shutdown(); Wait(pending);
            Assert(!f.Proxy.Applied && !f.Controller.IsConnected && !PortOpen(f.Proxy.Port), "Immediate shutdown left an active connection.");
        }
    }
    private static void FailedCheck()
    {
        using (var f = new Fixture("failed-check", delegate { return false; }))
        {
            Wait(f.Controller.ToggleAsync());
            Assert(!f.Controller.IsConnected && !f.Proxy.Applied && !PortOpen(f.Proxy.Port), "Failed check did not roll back.");
            Assert(f.Proxy.ApplyCalls == 1 && f.Proxy.RestoreCalls == 1 && f.Controller.StatusText == "Ошибка подключения", "Failure status or recovery counts are wrong.");
        }
    }
    private static void FailedRestore()
    {
        using (var f = new Fixture("failed-restore", delegate { return true; }))
        {
            Wait(f.Controller.ToggleAsync()); f.Proxy.FailRestore = true; Wait(f.Controller.ToggleAsync());
            Assert(f.Controller.StatusText == "Нужно восстановить интернет", "Restore failure was hidden by a generic connection status.");
            Assert(File.Exists(Path.Combine(f.Store.Data, "proxy-session.dpapi")), "Failed restore lost the journal.");
            f.Proxy.FailRestore = false; f.Controller.Shutdown();
            Assert(!f.Proxy.Applied && !File.Exists(Path.Combine(f.Store.Data, "proxy-session.dpapi")), "Shutdown did not retry recovery.");
        }
    }
    private static void CancelDuringCheck()
    {
        using(var entered=new ManualResetEvent(false))
        using(var release=new ManualResetEvent(false))
        using(var f=new Fixture("cancel-check",delegate {entered.Set(); release.WaitOne(); return true;}))
        {
            WorkTicket pending=f.Controller.ToggleAsync();
            Assert(entered.WaitOne(10000,false),"Check never started");
            Assert(f.Controller.CanCancelConnection,"Cancellation unavailable during check");
            f.Controller.CancelConnection();
            var timer=Stopwatch.StartNew();
            while(f.Proxy.Applied&&timer.ElapsedMilliseconds<2500)Thread.Sleep(20);
            Assert(!f.Proxy.Applied&&!PortOpen(f.Proxy.Port),"Cancel failed to restore while checker blocked");
            release.Set();Wait(pending);
            Assert(!f.Controller.IsConnected&&f.Controller.StatusText=="Отключено","Cancelled operation reported wrong state");
            Wait(f.Controller.ToggleAsync());
            Assert(f.Controller.IsConnected,"Cancellation poisoned future connection");
        }
    }
    private static void InvalidPreferences()
    {
        using(var f=new Fixture("invalid-options",delegate{return true;}))
        using(var done=new ManualResetEvent(false))
        {
            Wait(f.Controller.ToggleAsync());
            var settings=f.Controller.CopySettings();settings.DirectDomains="https://example.com/not-a-domain";
            Exception failure=null;
            f.Controller.SavePreferencesAsync(settings).OnCompleted(delegate(Exception error){failure=error;done.Set();});
            Assert(done.WaitOne(10000,false),"Invalid preference ticket never finished");
            Assert(failure is ArgumentException && !f.Controller.IsBusy,"Invalid routing did not return a recoverable validation error");
            Assert(f.Controller.IsConnected&&f.Proxy.Applied,"Invalid routing disconnected the existing session");
        }
    }
}

