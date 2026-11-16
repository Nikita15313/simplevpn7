using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using SimpleVPN;

class CorePreparationTests
{
    static string executable, scratch;
    static int passed;
    const string Link = "vless://00112233-4455-6677-8899-aabbccddeeff@server.invalid:443?security=reality&type=tcp&sni=example.com&pbk=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA#Fixture";
    static ServerProfile Server() { return SubscriptionParser.Parse(Link)[0]; }
    static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    static void Run(string name, Action test) { test(); passed++; Console.WriteLine("PASS " + name); }
    // Observe the exact owned process, so a disposed flag cannot hide a leaked
    // process. Reflection is read-only and is confined to the regression fixture.
    static CoreProcess Owned(CorePreparation preparation)
    {
        return (CoreProcess)typeof(CorePreparation).GetField("candidate", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(preparation);
    }
    static void Finished(CorePreparation preparation)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        while (!preparation.HasCompleted && elapsed.ElapsedMilliseconds < 20000) Thread.Sleep(10);
        Assert(preparation.HasCompleted, "Preparation did not finish");
    }
    static bool PortOpen(int port)
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
                    client.EndConnect(pending);
                    return true;
                }
            }
            catch (SocketException) { return false; }
        }
    }
    static void Stopped(CoreProcess core)
    {
        Assert(!core.IsAlive && !PortOpen(core.Port), "Owned core or its listening port survived cleanup");
    }

    static int Main(string[] args)
    {
        try
        {
            if (args.Length != 2) throw new ArgumentException("Usage: CorePreparationTests <core-path> <scratch-root>");
            executable = Path.GetFullPath(args[0]);
            scratch = Path.Combine(Path.GetFullPath(args[1]), Guid.NewGuid().ToString("N"));
            Run("Ready preparation owns a real loopback core and disposal stops it", ReadyDispose);
            Run("Take waits for startup and transfers ownership exactly once", Transfer);
            Run("Caller mutations cannot alter the prepared settings or profile", Snapshot);
            Run("Actual startup failures are preserved and never retried", Failures);
            Run("Ready core death remains distinguishable from startup failure", DeadReady);
            Run("Already-cancelled Take stops the unclaimed core", CancelledTake);
            Run("Early disposal cannot resurrect a background core", EarlyDispose);
            Run("Cancellation racing Take leaves no unowned process", CancellationRace);
            Run("Concurrent Take calls have a single winner", ConcurrentTake);
            Console.WriteLine("Preparation checks passed: " + passed + "; real Xray/loopback only; no external requests or proxy changes; CLR: " + Environment.Version);
            return 0;
        }
        catch (Exception error) { Console.WriteLine("FAIL " + error.GetType().Name + ": " + error.Message); return 1; }
    }

    static void ReadyDispose()
    {
        using (var preparation = new CorePreparation(executable, Server(), new AppSettings(), "ready"))
        {
            CoreProcess observed = Owned(preparation);
            Finished(preparation);
            Assert(preparation.Key == "ready" && preparation.Ready && !preparation.Failed && preparation.IsAlive, "Ready state is incorrect");
            Assert(PortOpen(observed.Port), "Prepared listener is unavailable");
            preparation.Dispose();
            Stopped(observed);
            Assert(!preparation.Ready && !preparation.IsAlive, "Disposed preparation remained ready");
        }
    }
    static void Transfer()
    {
        CoreProcess result = null;
        using (var preparation = new CorePreparation(executable, Server(), new AppSettings(), "transfer"))
        using (var cancel = new OperationCancel())
        {
            try
            {
                result = preparation.Take(cancel);
                Assert(result.IsAlive && PortOpen(result.Port), "Taken core was not ready");
                bool rejected = false;
                try { preparation.Take(cancel); } catch (InvalidOperationException) { rejected = true; }
                Assert(rejected, "Core ownership was transferred twice");
                preparation.Dispose();
                Assert(result.IsAlive && PortOpen(result.Port), "Disposing a taken preparation killed the caller's core");
            }
            finally { if (result != null) result.Dispose(); }
            Stopped(result);
        }
    }
    static void Snapshot()
    {
        var server = Server();
        var settings = new AppSettings();
        using (var preparation = new CorePreparation(executable, server, settings, "snapshot"))
        using (var cancel = new OperationCancel())
        {
            server.Outbound.Clear(); settings.DnsMode = "invalid-after-construction";
            using (CoreProcess core = preparation.Take(cancel)) Assert(core.IsAlive, "Mutable caller state changed the preparation");
        }
    }
    static void Failures()
    {
        using (var missing = new CorePreparation(Path.Combine(scratch, "missing.exe"), Server(), new AppSettings(), "missing"))
        using (var cancel = new OperationCancel())
        {
            CoreProcess observed = Owned(missing);
            bool exact = false;
            try { missing.Take(cancel); } catch (FileNotFoundException error) { exact = error.Message == "CORE_MISSING"; }
            Assert(exact && missing.Failed && missing.HasCompleted && !missing.Ready, "Missing-core diagnosis was lost");
            Stopped(observed);
        }
        using (var invalid = new CorePreparation(executable, Server(), new AppSettings { DnsMode = "invalid" }, "invalid"))
        using (var cancel = new OperationCancel())
        {
            CoreProcess observed = Owned(invalid);
            bool exact = false;
            try { invalid.Take(cancel); } catch (ArgumentException) { exact = true; }
            Assert(exact && invalid.Failed, "Invalid settings were hidden by a fallback or wrapper");
            Stopped(observed);
        }
    }
    static void DeadReady()
    {
        using (var preparation = new CorePreparation(executable, Server(), new AppSettings(), "dead"))
        using (var cancel = new OperationCancel())
        {
            Finished(preparation);
            CoreProcess observed = Owned(preparation);
            observed.Dispose();
            Assert(preparation.Ready && !preparation.Failed && !preparation.IsAlive, "Completed-but-dead state cannot be detected");
            bool exact = false;
            try { preparation.Take(cancel); } catch (IOException error) { exact = error.Message == "CORE_EXITED"; }
            Assert(exact, "Taking a dead core did not report CORE_EXITED");
        }
    }
    static void CancelledTake()
    {
        using (var preparation = new CorePreparation(executable, Server(), new AppSettings(), "cancelled"))
        using (var cancel = new OperationCancel())
        {
            CoreProcess observed = Owned(preparation);
            cancel.Cancel();
            bool cancelled = false;
            try { preparation.Take(cancel); } catch (OperationCancelledException) { cancelled = true; }
            Assert(cancelled, "A cancelled Take did not preserve cancellation");
            Finished(preparation); Stopped(observed);
        }
    }
    static void EarlyDispose()
    {
        for (int i = 0; i < 3; i++)
        using (var preparation = new CorePreparation(executable, Server(), new AppSettings(), "early"))
        {
            CoreProcess observed = Owned(preparation);
            preparation.Dispose(); Finished(preparation); Stopped(observed);
            Assert(!preparation.Ready, "Late completion resurrected a disposed preparation");
        }
    }
    static void CancellationRace()
    {
        for (int i = 0; i < 4; i++)
        using (var preparation = new CorePreparation(executable, Server(), new AppSettings(), "race"))
        using (var cancel = new OperationCancel())
        {
            CoreProcess observed = Owned(preparation), result = null;
            Exception failure = null;
            var taker = new Thread(delegate() { try { result = preparation.Take(cancel); } catch (Exception error) { failure = error; } });
            taker.IsBackground = true;
            try
            {
                taker.Start(); cancel.Cancel();
                Assert(taker.Join(20000), "Cancelled Take remained blocked");
                Assert((result != null && failure == null) || failure is OperationCancelledException, "Cancellation race lost its outcome");
            }
            finally { preparation.Dispose(); if (result != null) result.Dispose(); }
            Finished(preparation); Stopped(observed);
        }
    }
    static void ConcurrentTake()
    {
        using (var preparation = new CorePreparation(executable, Server(), new AppSettings(), "two-takers"))
        using (var cancel = new OperationCancel())
        {
            CoreProcess one = null, two = null;
            Exception firstError = null, secondError = null;
            var first = new Thread(delegate() { try { one = preparation.Take(cancel); } catch (Exception error) { firstError = error; } });
            var second = new Thread(delegate() { try { two = preparation.Take(cancel); } catch (Exception error) { secondError = error; } });
            first.IsBackground = second.IsBackground = true;
            try
            {
                first.Start(); second.Start();
                Assert(first.Join(20000) && second.Join(20000), "Concurrent Take calls did not finish");
                Assert((one != null) != (two != null), "Ownership did not have exactly one winner");
                Assert(firstError is InvalidOperationException || secondError is InvalidOperationException, "Losing Take did not report an ownership error");
                preparation.Dispose();
                Assert((one ?? two).IsAlive, "Preparation disposal killed the winning caller's core");
            }
            finally { if (one != null) one.Dispose(); if (two != null) two.Dispose(); }
        }
    }
}
