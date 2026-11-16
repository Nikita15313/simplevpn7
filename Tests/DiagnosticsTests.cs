using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Threading;
using SimpleVPN;

// Synthetic credentials only. Real DPAPI storage; proxy platform is fake.
internal static class DiagnosticsTests
{
    private const string Secret = "https://fixture.invalid/private-token?credential=do-not-log";
    private const string Link = "vless://00112233-4455-6677-8899-aabbccddeeff@server.invalid:443?security=reality&type=tcp&sni=www.example.com&pbk=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA#Synthetic";
    private static int passed;
    private static string root;
    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Run(string name, Action test) { test(); passed++; Console.WriteLine("PASS " + name); }
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length != 1) throw new ArgumentException("Expected scratch directory.");
            root = Path.Combine(Path.GetFullPath(args[0]), Guid.NewGuid().ToString("N"));
            Run("Internal startup codes remain distinguishable", StartupCodes);
            Run("Code prefixes and arbitrary exception messages are never logged", Redaction);
            Run("Nested native errors preserve an integer without leaking paths", NativeCode);
            Run("Custom exception Message is never evaluated", CustomException);
            Run("Unavailable server and local startup failure have different guidance", Guidance);
            Run("Missing core reports local failure and never applies the proxy", MissingCore);
            Console.WriteLine("Diagnostic checks passed: " + passed + "; CLR: " + Environment.Version);
            return 0;
        }
        catch (Exception error) { Console.WriteLine("FAIL " + error.GetType().Name + ": " + error.Message); return 1; }
    }
    private static void StartupCodes()
    {
        foreach (string code in new[] { "CORE_MISSING", "JOB_CREATE_FAILED", "JOB_SETUP_FAILED", "CORE_START_FAILED", "CORE_JOB_FAILED", "CORE_EXITED", "CORE_TIMEOUT", "WATCHDOG_START_FAILED", "CONNECTIVITY_CHECK_FAILED", "PROXY_QUERY_FAILED", "PROXY_SET_FAILED", "RECOVERY_PENDING", "SAVE_FLUSH_FAILED" })
        {
            Assert(ConnectionFailure.FromException(new IOException(code)).Code == code, "Internal code was lost: " + code);
        }
        Assert(ConnectionFailure.FromException(new FileNotFoundException("CORE_MISSING", Secret)).Code == "CORE_MISSING", "Missing core classification failed.");
    }
    private static string Log(LocalStore store) { return File.ReadAllText(Path.Combine(store.Data, "diagnostic.log")); }
    private static void Redaction()
    {
        var store = new LocalStore(Path.Combine(root, "redaction"));
        foreach (Exception error in new Exception[] {
            new IOException("CORE_EXITED " + Secret),
            new IOException("CORE_EXITED\r\nCONNECTED"),
            new IOException(Secret),
            new ArgumentException(Secret, Secret),
            new FileNotFoundException(Secret, Secret),
            new InvalidOperationException(Secret, new IOException(Secret)) })
        {
            ConnectionFailure failure = ConnectionFailure.FromException(error);
            failure.WriteTo(store);
            Assert(failure.Code != "CORE_EXITED", "A prefixed internal code was accepted.");
            Assert(failure.Notice.IndexOf(Secret, StringComparison.Ordinal) < 0, "Message leaked into notice.");
        }
        string log = Log(store);
        Assert(log.IndexOf("private-token", StringComparison.Ordinal) < 0 && log.IndexOf("fixture.invalid", StringComparison.Ordinal) < 0, "Credentials leaked into log.");
        Assert(log.IndexOf("CONNECTED", StringComparison.Ordinal) < 0, "Message injected a connection event.");
    }
    private static void NativeCode()
    {
        var store = new LocalStore(Path.Combine(root, "native"));
        var failure = ConnectionFailure.FromException(new TypeInitializationException(Secret, new Win32Exception(193, Secret)));
        Assert(failure.Code == "NATIVE_ERROR" && failure.ExceptionKind == "TYPE_INITIALIZATION" && failure.NativeErrorCode == 193, "Native failure evidence missing.");
        failure.WriteTo(store);
        Assert(Log(store).IndexOf("CONNECT_NATIVE_000000C1", StringComparison.Ordinal) >= 0, "Native error hex not logged.");
        Assert(Log(store).IndexOf(Secret, StringComparison.Ordinal) < 0, "Nested native message leaked.");
        ConnectionFailure.FromException(new Win32Exception(-1, Secret)).WriteTo(store);
        Assert(Log(store).IndexOf("CONNECT_NATIVE_FFFFFFFF", StringComparison.Ordinal) >= 0, "Signed native error was lost.");
    }
    private sealed class UntrustedException : IOException
    {
        public override string Message { get { throw new InvalidOperationException("Message must not be evaluated."); } }
    }
    private static void CustomException()
    {
        var failure = ConnectionFailure.FromException(new UntrustedException());
        Assert(failure.Code == "UNEXPECTED" && failure.ExceptionKind == "IO", "Untrusted exception classification failed.");
        Assert(ConnectionFailure.FromException(null).Code == "UNEXPECTED", "Null classification failed.");
    }
    private static void Guidance()
    {
        var check = ConnectionFailure.FromException(new IOException("CONNECTIVITY_CHECK_FAILED"));
        var job = ConnectionFailure.FromException(new IOException("CORE_JOB_FAILED"));
        var missing = ConnectionFailure.FromException(new FileNotFoundException("CORE_MISSING"));
        Assert(check.DetailText == "Сервер не ответил", "Check failure was not identified as server timeout.");
        Assert(job.Notice.IndexOf("другой сервер", StringComparison.Ordinal) < 0, "Local failure blames selected server.");
        Assert(missing.Notice.IndexOf("Установите", StringComparison.Ordinal) >= 0, "Missing component guidance is not actionable.");
    }
    private sealed class FakeProxy : IProxyPlatform
    {
        internal bool Applied;
        public ProxySnapshot Capture() { return new ProxySnapshot { Flags = 13, Entries = new List<RegistryEntry>() }; }
        public void Apply(string endpoint) { Applied = true; }
        public bool Owns(string endpoint) { return Applied; }
        public void Restore(ProxySnapshot snapshot) { Applied = false; }
    }
    private static void Wait(WorkTicket ticket)
    {
        using (var done = new ManualResetEvent(false))
        {
            Exception failure = null;
            ticket.OnCompleted(delegate(Exception error) { failure = error; done.Set(); });
            Assert(done.WaitOne(10000, false), "Operation timed out.");
            Assert(failure == null, "Worker threw instead of reporting failure.");
        }
    }
    private static void MissingCore()
    {
        var store = new LocalStore(Path.Combine(root, "missing-core"));
        ServerProfile server = SubscriptionParser.Parse(Link)[0];
        store.Save("servers.dpapi", Link);
        store.Save("settings.dpapi", new AppSettings { SubscriptionUrl = Secret, SelectedServerId = server.Id, LastUpdatedUtc = DateTime.UtcNow });
        var proxy = new FakeProxy();
        var controller = new AppController(store, proxy, delegate { return Link; }, delegate { throw new InvalidOperationException("Must not reach the checker."); }, false);
        try
        {
            Wait(controller.InitializeAsync());
            Wait(controller.ToggleAsync());
            Assert(!controller.IsConnected && !proxy.Applied && !controller.IsBusy, "Missing core left connected or busy state.");
            Assert(controller.StatusText == "Ошибка подключения" && controller.DetailText == "Не удалось запустить VPN", "Local failure status incorrect.");
            string log = Log(store);
            Assert(log.IndexOf("CONNECT_REASON_CORE_MISSING", StringComparison.Ordinal) >= 0 && log.IndexOf("CONNECT_TYPE_FILE_NOT_FOUND", StringComparison.Ordinal) >= 0, "Controller omitted safe failure evidence.");
            Assert(log.IndexOf(Secret, StringComparison.Ordinal) < 0 && log.IndexOf("CORE_READY", StringComparison.Ordinal) < 0, "Sensitive or false success diagnostic found.");
        }
        finally { controller.Shutdown(); }
    }
}
