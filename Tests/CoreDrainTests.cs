using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using SimpleVPN;

// Independent process experiment; it never changes the invoking app's ThreadPool.
// No external network, GUI, OS proxy, subscription or production source changes.
internal static class CoreDrainTests
{
    static string Executable { get { return Assembly.GetExecutingAssembly().Location; } }
    static void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); }
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 1 && args[0] == "--child")
            {
                Console.Out.WriteLine("READY"); Console.Out.Flush();
                Console.Error.WriteLine("READY"); Console.Error.Flush();
                Console.In.ReadLine(); return 0;
            }
            if (args.Length == 2 && args[0] == "--worker") return Worker(args[1] == "dedicated");
            if (args.Length == 2 && args[0] == "--production") return Production(args[1]);
            Check(args.Length <= 1, "Optional core path expected.");
            Run("legacy"); Run("dedicated");
            if (args.Length == 1) Run("production", "--production \"" + Path.GetFullPath(args[0]) + "\"");
            Console.WriteLine("Core drain regression PASS: constrained worker pool, loopback HTTP, timer cancellation and pipe cleanup.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine("FAIL " + e.GetType().Name + ": " + e.Message); return 1; }
    }
    static ProcessStartInfo StartInfo(string arguments)
    {
        return new ProcessStartInfo(Executable, arguments) {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
    }
    static void Stop(Process child)
    {
        try { if (!child.HasExited) child.Kill(); } catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        try { child.WaitForExit(2000); } catch (InvalidOperationException) { }
    }
    static void Run(string mode)
    { Run(mode, "--worker " + mode); }
    static void Run(string mode, string arguments)
    {
        using (Process worker = Process.Start(StartInfo(arguments)))
        {
            try
            {
                Check(worker.WaitForExit(20000), mode + " worker exceeded deadline.");
                string output = worker.StandardOutput.ReadToEnd(), error = worker.StandardError.ReadToEnd();
                Console.Write(output); if (error.Length > 0) Console.Error.Write(error);
                Check(worker.ExitCode == 0, mode + " experiment failed.");
            }
            finally { Stop(worker); worker.StandardInput.Close(); worker.StandardOutput.Close(); worker.StandardError.Close(); }
        }
    }
    static Thread ProductionDrain(CoreProcess core, string name)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object drain = typeof(CoreProcess).GetField(name, flags).GetValue(core);
        return (Thread)drain.GetType().GetField("thread", flags).GetValue(drain);
    }
    static int Production(string path)
    {
        int maxWorkers, maxIo, minWorkers, minIo;
        ThreadPool.GetMaxThreads(out maxWorkers, out maxIo); ThreadPool.GetMinThreads(out minWorkers, out minIo);
        Check(ThreadPool.SetMinThreads(1, minIo) && ThreadPool.SetMaxThreads(2, maxIo), "Cannot constrain production test pool.");
        using (var core = new CoreProcess(path))
        using (var cancel = new OperationCancel())
        using (var healthDone = new ManualResetEvent(false))
        using (var timerDone = new ManualResetEvent(false))
        using (var cancelDone = new ManualResetEvent(false))
        {
            var startup = Stopwatch.StartNew();
            core.Start(SubscriptionParser.Parse("vless://00112233-4455-6677-8899-aabbccddeeff@server.invalid:443?security=reality&type=tcp&sni=example.invalid&pbk=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA#Synthetic")[0], cancel);
            startup.Stop();
            Thread output = ProductionDrain(core, "outputDrain"), error = ProductionDrain(core, "errorDrain");
            int available, io; ThreadPool.GetAvailableThreads(out available, out io);
            int port = core.Port;
            bool healthy = false; long healthMs = -1, timerMs = -1, cancelMs = -1;
            var watch = Stopwatch.StartNew();
            using (cancel.Register(core.Dispose))
            using (var timer = new Timer(delegate { timerMs = watch.ElapsedMilliseconds; timerDone.Set(); }, null, 100, Timeout.Infinite))
            using (var cancellation = new Timer(delegate { cancelMs = watch.ElapsedMilliseconds; cancel.Cancel(); cancelDone.Set(); }, null, 300, Timeout.Infinite))
            {
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try { using (var socket = new TcpClient()) { socket.Connect(IPAddress.Loopback, port); healthy = socket.Connected; } }
                    catch (SocketException) { }
                    finally { healthMs = watch.ElapsedMilliseconds; healthDone.Set(); }
                });
                Check(cancelDone.WaitOne(1500, false), "Production cancellation was starved.");
                Check(healthDone.WaitOne(0, false) && timerDone.WaitOne(0, false) && healthy, "Production loopback or timer was starved.");
                Check(!core.IsAlive && output.Join(500) && error.Join(500), "Production child or drain thread survived cancellation.");
                bool portClosed = false;
                using (var socket = new TcpClient()) { try { socket.Connect(IPAddress.Loopback, port); } catch (SocketException) { portClosed = true; } }
                Check(portClosed && available == 2 && healthMs < 500 && timerMs < 500, "Production core consumed pool slots or kept its port open.");
                Console.WriteLine("PRODUCTION max_workers=2 available_after_drain=" + available + " core_ready_ms=" + startup.ElapsedMilliseconds +
                    " loopback_health_ms=" + healthMs + " timer_100ms_actual=" + timerMs + " cancel_300ms_actual=" + cancelMs + " child_stopped=True port_closed=True drain_threads_stopped=True");
            }
        }
        return 0;
    }
    static Thread Drain(StreamReader reader, ManualResetEvent ready, ManualResetEvent done)
    {
        var thread = new Thread(delegate()
        {
            var buffer = new char[1024];
            try { while (reader.Read(buffer, 0, buffer.Length) != 0) ready.Set(); }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            finally { Array.Clear(buffer, 0, buffer.Length); reader.Close(); done.Set(); }
        });
        thread.IsBackground = true; thread.Start(); return thread;
    }
    static int Worker(bool dedicated)
    {
        int maxWorkers, maxIo, minWorkers, minIo;
        ThreadPool.GetMaxThreads(out maxWorkers, out maxIo);
        ThreadPool.GetMinThreads(out minWorkers, out minIo);
        Check(ThreadPool.SetMinThreads(1, minIo), "Cannot set worker minimum.");
        int constrained = 2;
        if (!ThreadPool.SetMaxThreads(constrained, maxIo))
        {
            constrained = Math.Max(2, Environment.ProcessorCount);
            Check(constrained <= 64 && ThreadPool.SetMaxThreads(constrained, maxIo), "Cannot establish bounded pool.");
        }
        int extra = constrained - 2, blockersReady = 0;
        using (var blockersRelease = new ManualResetEvent(false))
        using (var stdoutReady = new ManualResetEvent(false))
        using (var stderrReady = new ManualResetEvent(false))
        using (var stdoutDone = new ManualResetEvent(false))
        using (var stderrDone = new ManualResetEvent(false))
        using (var healthDone = new ManualResetEvent(false))
        using (var timerDone = new ManualResetEvent(false))
        using (var cancelDone = new ManualResetEvent(false))
        using (Process child = Process.Start(StartInfo("--child")))
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Thread first = null, second = null, server = null;
            Timer timer = null, cancellation = null;
            bool healthy = false; long healthMs = -1, timerMs = -1, cancellationMs = -1;
            try
            {
                // Some CLRs require max >= CPU count. Occupy the extra slots to
                // leave exactly two available slots in both comparison workers.
                if (extra > 0)
                {
                    Check(ThreadPool.SetMinThreads(constrained, minIo), "Cannot initialize filler slots.");
                    for (int i = 0; i < extra; i++) ThreadPool.QueueUserWorkItem(delegate { Interlocked.Increment(ref blockersReady); blockersRelease.WaitOne(); });
                    var filled = Stopwatch.StartNew();
                    while (Interlocked.CompareExchange(ref blockersReady, 0, 0) != extra && filled.ElapsedMilliseconds < 5000) Thread.Sleep(10);
                    Check(blockersReady == extra, "Filler slots did not initialize.");
                    Check(ThreadPool.SetMinThreads(1, minIo), "Cannot restore constrained minimum.");
                }
                if (dedicated)
                {
                    first = Drain(child.StandardOutput, stdoutReady, stdoutDone);
                    second = Drain(child.StandardError, stderrReady, stderrDone);
                }
                else
                {
                    child.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data == null) stdoutDone.Set(); else stdoutReady.Set(); };
                    child.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data == null) stderrDone.Set(); else stderrReady.Set(); };
                    child.BeginOutputReadLine(); child.BeginErrorReadLine();
                }
                Check(stdoutReady.WaitOne(5000, false) && stderrReady.WaitOne(5000, false), "Child pipe readers did not initialize.");
                Thread.Sleep(50);
                int available, availableIo; ThreadPool.GetAvailableThreads(out available, out availableIo);
                if (!dedicated)
                {
                    // READY can be delivered before AsyncStreamReader queues its
                    // next blocking read. Measure only after both reads are idle.
                    var primed = Stopwatch.StartNew();
                    while (available != 0 && primed.ElapsedMilliseconds < 5000)
                    { Thread.Sleep(10); ThreadPool.GetAvailableThreads(out available, out availableIo); }
                    Check(available == 0, "Legacy readers did not occupy the constrained pool.");
                }
                server = new Thread(delegate()
                {
                    try
                    {
                        using (TcpClient socket = listener.AcceptTcpClient())
                        using (NetworkStream stream = socket.GetStream())
                        {
                            socket.ReceiveTimeout = 2000; var bytes = new byte[1024]; stream.Read(bytes, 0, bytes.Length);
                            bytes = Encoding.ASCII.GetBytes("HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                            stream.Write(bytes, 0, bytes.Length);
                        }
                    }
                    catch (SocketException) { }
                    catch (IOException) { }
                    catch (ObjectDisposedException) { }
                });
                server.IsBackground = true; server.Start();
                var watch = Stopwatch.StartNew();
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        using (var socket = new TcpClient())
                        {
                            socket.ReceiveTimeout = 1500; socket.SendTimeout = 1500; socket.Connect(IPAddress.Loopback, port);
                            using (NetworkStream stream = socket.GetStream())
                            {
                                byte[] bytes = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n");
                                stream.Write(bytes, 0, bytes.Length); bytes = new byte[256]; int count = stream.Read(bytes, 0, bytes.Length);
                                healthy = Encoding.ASCII.GetString(bytes, 0, count).StartsWith("HTTP/1.1 204", StringComparison.Ordinal);
                            }
                        }
                    }
                    catch (Exception) { healthy = false; }
                    finally { healthMs = watch.ElapsedMilliseconds; healthDone.Set(); }
                });
                timer = new Timer(delegate { timerMs = watch.ElapsedMilliseconds; timerDone.Set(); }, null, 100, Timeout.Infinite);
                cancellation = new Timer(delegate { cancellationMs = watch.ElapsedMilliseconds; Stop(child); cancelDone.Set(); }, null, 300, Timeout.Infinite);
                bool timelyCancel = cancelDone.WaitOne(1000, false);
                bool timelyHealth = healthDone.WaitOne(0, false), timelyTimer = timerDone.WaitOne(0, false);
                if (!timelyCancel) Stop(child); // Rescue only this owned fixture.
                Check(healthDone.WaitOne(3000, false) && timerDone.WaitOne(3000, false) && cancelDone.WaitOne(3000, false), "Pool did not recover after child exit.");
                Check(stdoutDone.WaitOne(2000, false) && stderrDone.WaitOne(2000, false), "Pipe drains did not terminate after cancellation.");
                if (first != null) Check(first.Join(1000) && second.Join(1000), "Dedicated drain threads remained alive.");
                Check(child.HasExited && healthy, "Owned child cleanup or loopback HTTP failed.");
                Console.WriteLine((dedicated ? "DEDICATED" : "LEGACY") + " max_workers=" + constrained + " background_fillers=" + extra + " available_after_drain=" + available +
                    " health_ms=" + healthMs + " timer_100ms_actual=" + timerMs + " cancel_300ms_actual=" + cancellationMs +
                    " health_timely=" + timelyHealth + " timer_timely=" + timelyTimer + " cancel_timely=" + timelyCancel + " child_stopped=True drains_stopped=True CLR=" + Environment.Version);
                if (dedicated) Check(timelyCancel && timelyHealth && timelyTimer && healthMs < 1000 && timerMs < 500, "Dedicated drains starved callbacks.");
                else Check(!timelyCancel && !timelyHealth && !timelyTimer && available == 0, "Legacy starvation was not reproduced on this runtime.");
                return 0;
            }
            finally
            {
                Stop(child); blockersRelease.Set();
                if (timer != null) timer.Dispose(); if (cancellation != null) cancellation.Dispose();
                listener.Stop(); if (server != null) server.Join(2000);
                // Dedicated workers own and close readers. Legacy async readers
                // finish at EOF; Process.Dispose itself does not own their Close.
                try { child.StandardInput.Close(); } catch (InvalidOperationException) { }
                if (dedicated) { if (first != null) first.Join(1000); if (second != null) second.Join(1000); }
            }
        }
    }
}
