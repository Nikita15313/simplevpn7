using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using SimpleVPN;

// Compile against Network.cs, Storage.cs, Threading.cs and .NET 3.5 references.
// Default tests use synthetic metadata and a stalled loopback proxy only.
// Optional: --subscription <DPAPI file> fetches once, prints presence booleans only.
internal static class MetadataTests
{
    private static int checks;
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("Fixture failed: " + name);
        checks++;
    }
    private static Dictionary<string, string> Headers(params string[] pairs)
    {
        var result = new Dictionary<string, string>();
        for (int i = 0; i < pairs.Length; i += 2) result.Add(pairs[i], pairs[i + 1]);
        return result;
    }
    private static string B64(string value) { return "base64:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(value)); }
    public static int Main(string[] args)
    {
        try
        {
            var empty = Network.ParseSubscriptionMetadata(null);
            Check(empty.Title == null && empty.TotalBytes == null && empty.ExpireUnixSeconds == null && empty.UpdateIntervalHours == null, "Absent metadata stays absent");
            Check(empty.RetrievedUtc == DateTime.MinValue, "Pure parser does not invent retrieval time");
            var metadata = Network.ParseSubscriptionMetadata(Headers(
                "PROFILE-TITLE", B64("Семейная подписка"), "Subscription-Userinfo", "upload=123; download=456; total=999; expire=1893456000",
                "Profile-Update-Interval", "12", "Support-URL", "https://t.me/example_support", "Profile-Web-Page-URL", "https://example.invalid/account?token=synthetic"));
            Check(metadata.Title == "Семейная подписка", "Case-insensitive headers and Russian UTF8 title");
            Check(metadata.UploadBytes == 123 && metadata.DownloadBytes == 456 && metadata.TotalBytes == 999 && metadata.ExpireUnixSeconds == 1893456000L, "Exact userinfo fields");
            Check(metadata.UpdateIntervalHours == 12 && metadata.SupportUrl != null && metadata.WebPageUrl != null, "Interval and explicit links");
            var zero = Network.ParseSubscriptionMetadata(Headers("subscription-userinfo", "upload=0;download=0;total=0;expire=0"));
            Check(zero.UploadBytes == 0 && zero.DownloadBytes == 0 && zero.TotalBytes == 0 && zero.ExpireUnixSeconds == 0, "Explicit zero is different from unknown");
            var partial = Network.ParseSubscriptionMetadata(Headers("subscription-userinfo", "download=17; upload=-1; total=9223372036854775808; expire=253402300800"));
            Check(partial.DownloadBytes == 17 && partial.UploadBytes == null && partial.TotalBytes == null && partial.ExpireUnixSeconds == null, "Overflow and invalid values leave other fields intact");
            var repeated = Network.ParseSubscriptionMetadata(Headers("subscription-userinfo", "upload=1;UPLOAD=2;download=3;unknown=ignored"));
            Check(repeated.UploadBytes == null && repeated.DownloadBytes == 3, "Conflicting userinfo keys rejected");
            Check(Network.ParseSubscriptionMetadata(Headers("profile-title", "one", "Profile-Title", "two")).Title == null, "Ambiguous case-duplicate header rejected");
            Check(Network.ParseSubscriptionMetadata(Headers("profile-title", "base64:not-valid%%")).Title == null, "Malformed base64 rejected");
            Check(Network.ParseSubscriptionMetadata(Headers("profile-title", "base64:/w==")).Title == null, "Invalid UTF8 rejected");
            Check(Network.ParseSubscriptionMetadata(Headers("profile-title", "x\r\nInjected: true")).Title == null, "Header injection rejected");
            Check(Network.ParseSubscriptionMetadata(Headers("profile-title", B64("hello\u202Eworld\0"))).Title == "helloworld", "Display control and bidi formatting removed");
            Check(Network.ParseSubscriptionMetadata(Headers("profile-title", new string('a', 26))).Title == new string('a', 25), "Documented25-character display limit");
            Check(Network.ParseSubscriptionMetadata(Headers("profile-title", new string('a', 24) + "\U0001F30D")).Title == new string('a', 24) + "\U0001F30D", "Truncation preserves surrogate pairs");
            Check(Network.ParseSubscriptionMetadata(Headers("profile-title", new string('a', 4097))).Title == null, "Oversized header ignored");
            foreach (string bad in new[] { "javascript:alert(1)", "file:///c:/windows", "happ://routing/anything", "http://example.invalid", "https://user:pass@example.invalid", "https://example.invalid\\bad" })
                Check(Network.ParseSubscriptionMetadata(Headers("support-url", bad)).SupportUrl == null, "Unsafe metadata URL rejected");
            foreach (string bad in new[] { "0", "-1", "1.5", "8761", "2147483648" })
                Check(Network.ParseSubscriptionMetadata(Headers("profile-update-interval", bad)).UpdateIntervalHours == null, "Invalid or excessive interval ignored");
            Check(Network.ParseSubscriptionMetadata(Headers("upload", "99", "custom-tunnel-config", "{}", "routing", "happ://routing/test")).UploadBytes == null, "No arbitrary app directives or invented userinfo");
            Persistence(metadata, args.Length == 1 && args[0] != "--probe-online" ? args[0] : null);
            ProbeBoundaries();
            if (args.Length == 2 && args[0] == "--subscription") RealSubscription(args[1]);
            if (args.Length == 1 && args[0] == "--probe-online") OnlineProbe();
            Console.WriteLine("PASS: " + checks + " metadata/settings/async probe checks.");
            return 0;
        }
        catch (Exception ex)
        {
            // Fixture identifiers contain no provider data. Native failures disclose type only.
            string message = ex.Message.StartsWith("Fixture failed: ", StringComparison.Ordinal) ? ex.Message : ex.GetType().Name;
            Console.WriteLine("FAIL: " + message);
            return 1;
        }
    }
    private static void Persistence(SubscriptionMetadata metadata, string suppliedRoot)
    {
        string root = suppliedRoot ?? Path.Combine(Path.GetTempPath(), "Happ7-Metadata-" + Guid.NewGuid().ToString("N"));
        var store = new LocalStore(root);
        var defaults = new AppSettings();
        Check(defaults.UpdateIntervalHours == 12 && !defaults.PingOnOpen && !defaults.SortByLatency && defaults.RoutingMode == "all" && defaults.DnsMode == "system", "App preference defaults");
        Check(!defaults.BlockAds && !defaults.RememberWindowPosition && defaults.WindowX == 0 && defaults.WindowY == 0 && defaults.ConnectionTimeoutSeconds == 6 && defaults.Theme == "dark" && !defaults.AutoSelectFastest, "Connection and window defaults");
        defaults.Metadata = metadata;
        defaults.SubscriptionUrl = "https://example.invalid/synthetic-private-token";
        defaults.UpdateIntervalHours = 24; defaults.Theme = "light"; defaults.DirectDomains = "example.invalid"; defaults.ProxyDomains = "proxy.invalid";
        defaults.PingOnOpen = true; defaults.SortByLatency = true; defaults.BlockAds = true; defaults.RememberWindowPosition = true;
        defaults.WindowX = -1000; defaults.WindowY = 250; defaults.AutoSelectFastest = true;
        store.Save("metadata-tests.dpapi", defaults);
        var actual = store.Load<AppSettings>("metadata-tests.dpapi");
        Check(actual.Metadata.Title == metadata.Title && actual.Metadata.UploadBytes == metadata.UploadBytes && actual.Metadata.WebPageUrl == metadata.WebPageUrl, "Metadata DPAPI round trip");
        Check(actual.Theme == "light" && actual.UpdateIntervalHours == 24 && actual.WindowX == -1000 && actual.AutoSelectFastest && actual.DirectDomains == "example.invalid" && actual.ProxyDomains == "proxy.invalid", "New settings DPAPI round trip");
        string encrypted = Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(store.Data, "metadata-tests.dpapi")));
        Check(encrypted.IndexOf("synthetic-private-token", StringComparison.Ordinal) < 0 && encrypted.IndexOf("example.invalid", StringComparison.Ordinal) < 0, "URLs absent from ciphertext");
        store.Save("legacy-tests.dpapi", new Dictionary<string, object> { { "SubscriptionUrl", "https://example.invalid/legacy" } });
        var migrated = store.Load<AppSettings>("legacy-tests.dpapi");
        Check(migrated.Metadata != null && migrated.Metadata.TotalBytes == null && migrated.UpdateIntervalHours == 12 && migrated.Theme == "dark", "Existing settings preserve new defaults");
        File.Delete(Path.Combine(store.Data, "metadata-tests.dpapi"));
        File.Delete(Path.Combine(store.Data, "legacy-tests.dpapi"));
        if (suppliedRoot == null) { Directory.Delete(store.Data); Directory.Delete(root); }
    }
    private static void ProbeBoundaries()
    {
        using (var cancel = new OperationCancel())
        {
            bool rejected = false;
            try { Network.Probe(0, "https://example.invalid/", 2000, cancel); } catch (ArgumentOutOfRangeException) { rejected = true; }
            Check(rejected, "Probe cannot use direct network");
            rejected = false;
            try { Network.Probe(1, "http://example.invalid/", 2000, cancel); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "Probe requires validated HTTPS URL");
        }
        using (var proxy = new StalledProxy())
        using (var cancel = new OperationCancel())
        {
            Stopwatch watch = Stopwatch.StartNew();
            Check(Network.Probe(proxy.Port, "https://example.invalid/", 2000, cancel) == -1, "Stalled proxy times out");
            Check(watch.ElapsedMilliseconds < 3500 && !cancel.IsCancellationRequested, "Hard deadline does not cancel caller token");
            Check(proxy.Accepted > 0, "Request reached only explicit loopback proxy");
        }
        using (var proxy = new StalledProxy())
        using (var cancel = new OperationCancel())
        using (var timer = new Timer(delegate { cancel.Cancel(); }, null, 150, Timeout.Infinite))
        {
            Stopwatch watch = Stopwatch.StartNew();
            bool cancelled = false;
            try { Network.Probe(proxy.Port, "https://example.invalid/", 10000, cancel); } catch (OperationCancelledException) { cancelled = true; }
            Check(cancelled && watch.ElapsedMilliseconds < 2000, "Caller cancellation promptly cancels async request");
        }
        using (var proxy = new StalledProxy())
        using (var cancel = new OperationCancel())
        {
            Stopwatch watch = Stopwatch.StartNew();
            Check(!Network.Check(proxy.Port, "https://www.gstatic.com/generate_204", 2, cancel), "Both stalled health probes fail");
            Check(watch.ElapsedMilliseconds < 3500 && proxy.Accepted == 2 && !cancel.IsCancellationRequested, "Default health checks race instead of serial timeouts");
        }
    }
    private static void RealSubscription(string path)
    {
        byte[] bytes = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
        string url;
        try { url = new UTF8Encoding(false, true).GetString(bytes); }
        finally { Array.Clear(bytes, 0, bytes.Length); }
        using (var cancel = new OperationCancel())
        {
            SubscriptionDownload download = Network.DownloadSubscription(url, 0, cancel);
            var m = download.Metadata;
            Console.WriteLine("REAL_HEADER_PRESENCE title=" + (m.Title != null) + " upload=" + m.UploadBytes.HasValue + " download=" + m.DownloadBytes.HasValue + " total=" + m.TotalBytes.HasValue + " expire=" + m.ExpireUnixSeconds.HasValue + " interval=" + m.UpdateIntervalHours.HasValue + " webpage=" + (m.WebPageUrl != null) + " support=" + (m.SupportUrl != null));
            Check(!String.IsNullOrEmpty(download.Body) && m.RetrievedUtc.Kind == DateTimeKind.Utc, "Real download body and retrieval timestamp");
        }
    }
    private static void OnlineProbe()
    {
        using (var proxy = new RelayProxy())
        using (var cancel = new OperationCancel())
        {
            int elapsed = Network.Probe(proxy.Port, "https://www.gstatic.com/generate_204", 6000, cancel);
            Check(elapsed > 0 && elapsed <= 6000, "Valid TLS headers complete through explicit CONNECT proxy");
            proxy.ExpectRace = true;
            Stopwatch watch = Stopwatch.StartNew();
            Check(Network.Check(proxy.Port, "https://www.gstatic.com/generate_204", 6, cancel), "Race returns first successful HTTPS result");
            Check(watch.ElapsedMilliseconds < 5500 && !cancel.IsCancellationRequested, "Successful race does not wait for stalled alternative or cancel caller");
            Stopwatch closeWait = Stopwatch.StartNew();
            while (proxy.ClosedStalled == 0 && closeWait.ElapsedMilliseconds < 1500) Thread.Sleep(20);
            Check(proxy.ClosedStalled > 0, "Successful race cancels and closes the losing request");
        }
    }
    private sealed class RelayProxy : IDisposable
    {
        private readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        private readonly List<TcpClient> clients = new List<TcpClient>();
        private readonly Thread thread;
        private volatile bool stopped;
        internal int ClosedStalled, StalledRequests;
        internal volatile bool ExpectRace;
        internal int Port { get { return ((IPEndPoint)listener.LocalEndpoint).Port; } }
        internal RelayProxy()
        {
            listener.Start();
            thread = new Thread(delegate()
            {
                while (!stopped)
                {
                    try
                    {
                        TcpClient client = listener.AcceptTcpClient();
                        lock (clients) clients.Add(client);
                        ThreadPool.QueueUserWorkItem(delegate { Relay(client); });
                    }
                    catch (SocketException) { if (!stopped) throw; }
                }
            });
            thread.IsBackground = true; thread.Start();
        }
        private void Relay(TcpClient client)
        {
            try
            {
                NetworkStream incoming = client.GetStream();
                var header = new StringBuilder();
                while (header.Length < 4096)
                {
                    int next = incoming.ReadByte();
                    if (next < 0) return;
                    header.Append((char)next);
                    if (header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal)) break;
                }
                if (header.ToString().StartsWith("CONNECT www.microsoft.com:443 ", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref StalledRequests);
                    try { while (incoming.ReadByte() >= 0) { } }
                    finally { Interlocked.Increment(ref ClosedStalled); }
                    return;
                }
                if (!header.ToString().StartsWith("CONNECT www.gstatic.com:443 ", StringComparison.Ordinal)) return;
                Stopwatch ready = Stopwatch.StartNew();
                while (ExpectRace && StalledRequests == 0 && ready.ElapsedMilliseconds < 1000) Thread.Sleep(10);
                var remote = new TcpClient();
                lock (clients) clients.Add(remote);
                remote.Connect("www.gstatic.com", 443);
                byte[] response = Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n");
                incoming.Write(response, 0, response.Length);
                NetworkStream outgoing = remote.GetStream();
                ThreadPool.QueueUserWorkItem(delegate { Copy(incoming, outgoing); });
                Copy(outgoing, incoming);
            }
            catch (IOException) { }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
        }
        private static void Copy(Stream source, Stream destination)
        {
            try
            {
                byte[] buffer = new byte[8192];
                int count;
                while ((count = source.Read(buffer, 0, buffer.Length)) > 0) destination.Write(buffer, 0, count);
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }
        public void Dispose()
        {
            stopped = true; listener.Stop(); thread.Join(1000);
            lock (clients) foreach (TcpClient client in clients) client.Close();
        }
    }
    private sealed class StalledProxy : IDisposable
    {
        private readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        private readonly List<TcpClient> clients = new List<TcpClient>();
        private readonly Thread thread;
        private volatile bool stopped;
        internal int Accepted;
        internal int Port { get { return ((IPEndPoint)listener.LocalEndpoint).Port; } }
        internal StalledProxy()
        {
            listener.Start();
            thread = new Thread(delegate()
            {
                while (!stopped)
                {
                    try
                    {
                        TcpClient client = listener.AcceptTcpClient();
                        lock (clients) { clients.Add(client); Interlocked.Increment(ref Accepted); }
                    }
                    catch (SocketException) { if (!stopped) throw; }
                }
            });
            thread.IsBackground = true; thread.Start();
        }
        public void Dispose()
        {
            stopped = true; listener.Stop(); thread.Join(1000);
            lock (clients) foreach (TcpClient client in clients) client.Close();
        }
    }
}
