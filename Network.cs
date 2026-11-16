using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SimpleVPN
{
    // WinHTTP is available on stock Windows 7. All settings below are per handle;
    // no registry, WinINet, system proxy, trust-store or Schannel policy is changed.
    public static class Network
    {
        private const int MaximumBody = 2 * 1024 * 1024;
        private const int RequestTimeout = 20000;
        private const string UserAgent = "v2rayN/7.16.9 SimpleVPN/1.0";
        private const uint SecureFlag = 0x00800000;
        private const uint SecureProtocolsOption = 84;
        private const uint Tls12 = 0x00000800;
        private const uint DisableFeatureOption = 63;
        private const uint DisableCookiesRedirectsAuthentication = 1 | 2 | 4;
        private const uint StatusCodeQuery = 19 | 0x20000000;
        private const uint ContentLengthQuery = 5;
        private const uint ContentEncodingQuery = 29;
        private const uint LocationQuery = 33;
        private const uint CustomHeaderQuery = 65535;
        private const int MaximumMetadataHeader = 4096;
        private const int HeaderNotFound = 12150;
        private const int InsufficientBuffer = 122;

        public static Uri SubscriptionUri(string value)
        {
            Uri uri;
            if (value == null || value.Length > 16384)
                throw InvalidSubscription();
            string trimmed = value.Trim();
            for (int i = 0; i < trimmed.Length; i++)
                if (trimmed[i] < 32 || trimmed[i] == '\\') throw InvalidSubscription();
            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out uri) ||
                !String.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                String.IsNullOrEmpty(uri.Host) || uri.HostNameType == UriHostNameType.Unknown ||
                !String.IsNullOrEmpty(uri.UserInfo) || !String.IsNullOrEmpty(uri.Fragment))
                throw InvalidSubscription();
            return uri;
        }

        private static InvalidOperationException InvalidSubscription()
        {
            return new InvalidOperationException("Вставьте полную ссылку подписки, начинающуюся с https://.");
        }

        public static string Download(string url, int proxyPort, OperationCancel cancel)
        {
            return DownloadSubscription(url, proxyPort, cancel).Body;
        }

        public static SubscriptionDownload DownloadSubscription(string url, int proxyPort, OperationCancel cancel)
        {
            if (cancel == null) throw new ArgumentNullException("cancel");
            ValidatePort(proxyPort, true);
            Uri original = SubscriptionUri(url);
            Uri current = original;
            Stopwatch elapsed = Stopwatch.StartNew();
            using (NativeHandle session = OpenSession(proxyPort, cancel))
            {
                for (int redirects = 0; redirects <= 4; redirects++)
                {
                    CheckDeadline(elapsed, cancel);
                    using (NativeHandle connection = OpenConnection(session, current, cancel))
                    using (NativeHandle request = OpenRequest(connection, current, cancel))
                    {
                        SendAndReceive(request, elapsed, cancel);
                        uint status = Status(request, cancel);
                        if (status == 301 || status == 302 || status == 303 || status == 307 || status == 308)
                        {
                            if (redirects == 4) throw new IOException("SUBSCRIPTION_REDIRECTS");
                            string location = Header(request, LocationQuery, cancel);
                            Uri next;
                            if (String.IsNullOrEmpty(location) || !Uri.TryCreate(current, location, out next) ||
                                !SameOrigin(original, next) || !String.IsNullOrEmpty(next.UserInfo) ||
                                !String.IsNullOrEmpty(next.Fragment))
                                throw new InvalidOperationException("Сервис перенаправляет подписку на другой сайт. Нужна прямая ссылка подписки.");
                            current = next;
                            continue;
                        }
                        if (status != 200) throw new IOException("SUBSCRIPTION_STATUS_" + status.ToString(CultureInfo.InvariantCulture));
                        string lengthValue = Header(request, ContentLengthQuery, cancel);
                        long length;
                        if (!String.IsNullOrEmpty(lengthValue))
                        {
                            if (!Int64.TryParse(lengthValue, NumberStyles.None, CultureInfo.InvariantCulture, out length))
                                throw new IOException("SUBSCRIPTION_LENGTH");
                            if (length > MaximumBody) throw new IOException("SUBSCRIPTION_TOO_LARGE");
                        }
                        string encoding = Header(request, ContentEncodingQuery, cancel);
                        byte[] wireBody = ReadBody(request, elapsed, cancel);
                        byte[] body = null;
                        try
                        {
                            body = DecodeBody(wireBody, encoding, cancel);
                            CheckDeadline(elapsed, cancel);
                            string text = new UTF8Encoding(false, true).GetString(body).TrimStart('\uFEFF');
                            SubscriptionMetadata metadata = ReadSubscriptionMetadata(request, cancel);
                            CheckDeadline(elapsed, cancel);
                            metadata.RetrievedUtc = DateTime.UtcNow;
                            return new SubscriptionDownload { Body = text, Metadata = metadata };
                        }
                        finally
                        {
                            Array.Clear(wireBody, 0, wireBody.Length);
                            if (body != null && !Object.ReferenceEquals(body, wireBody)) Array.Clear(body, 0, body.Length);
                        }
                    }
                }
            }
            throw new IOException("SUBSCRIPTION_REDIRECTS");
        }

        private static SubscriptionMetadata ReadSubscriptionMetadata(NativeHandle request, OperationCancel cancel)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in new[] { "subscription-userinfo", "profile-title", "profile-update-interval", "profile-web-page-url", "support-url" })
            {
                string value = MetadataHeader(request, name, cancel);
                if (value != null) headers.Add(name, value);
            }
            return ParseSubscriptionMetadata(headers);
        }

        private static string MetadataHeader(NativeHandle request, string name, OperationCancel cancel)
        {
            cancel.ThrowIfCancellationRequested();
            uint size = 0;
            bool found = WinHttpQueryHeadersText(request.Value, CustomHeaderQuery, name, null, ref size, IntPtr.Zero);
            int error = Marshal.GetLastWin32Error();
            cancel.ThrowIfCancellationRequested();
            if (!found && error == HeaderNotFound) return null;
            if (!found && error != InsufficientBuffer) throw NativeError("METADATA", error);
            // Optional malformed metadata must not prevent the profile list from updating.
            if (size == 0 || size > (MaximumMetadataHeader + 1) * 2) return null;
            StringBuilder value = new StringBuilder((int)(size / 2 + 1));
            uint index = 0;
            Require(WinHttpQueryHeadersIndexed(request.Value, CustomHeaderQuery, name, value, ref size, ref index), "METADATA", cancel);
            uint nextSize = 0;
            bool another = WinHttpQueryHeadersIndexed(request.Value, CustomHeaderQuery, name, null, ref nextSize, ref index);
            error = Marshal.GetLastWin32Error();
            cancel.ThrowIfCancellationRequested();
            if (another || error == InsufficientBuffer) return null;
            if (error != HeaderNotFound) throw NativeError("METADATA", error);
            return value.ToString();
        }

        public static SubscriptionMetadata ParseSubscriptionMetadata(IDictionary<string, string> headers)
        {
            var result = new SubscriptionMetadata();
            if (headers == null || headers.Count > 128) return result;
            result.Title = MetadataTitle(MetadataValue(headers, "profile-title"));
            result.WebPageUrl = MetadataUrl(MetadataValue(headers, "profile-web-page-url"));
            result.SupportUrl = MetadataUrl(MetadataValue(headers, "support-url"));
            long? interval = MetadataNumber(MetadataValue(headers, "profile-update-interval"));
            if (interval.HasValue && interval.Value >= 1 && interval.Value <= 8760)
                result.UpdateIntervalHours = (int)interval.Value;
            string userinfo = MetadataValue(headers, "subscription-userinfo");
            if (userinfo == null || userinfo.Length > 1024) return result;
            string[] items = userinfo.Split(';');
            if (items.Length > 32) return result;
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string item in items)
            {
                int equal = item.IndexOf('=');
                if (equal <= 0) continue;
                string key = item.Substring(0, equal).Trim();
                if (key != "upload" && key != "download" && key != "total" && key != "expire")
                {
                    key = key.ToLowerInvariant();
                    if (key != "upload" && key != "download" && key != "total" && key != "expire") continue;
                }
                // Repeated values are ambiguous even when they happen to be identical.
                if (fields.ContainsKey(key)) fields[key] = null;
                else fields.Add(key, item.Substring(equal + 1).Trim());
            }
            result.UploadBytes = MetadataNumber(MetadataValue(fields, "upload"));
            result.DownloadBytes = MetadataNumber(MetadataValue(fields, "download"));
            result.TotalBytes = MetadataNumber(MetadataValue(fields, "total"));
            long? expire = MetadataNumber(MetadataValue(fields, "expire"));
            if (expire.HasValue && expire.Value <= 253402300799L) result.ExpireUnixSeconds = expire;
            return result;
        }

        private static string MetadataValue(IDictionary<string, string> headers, string name)
        {
            string value = null;
            bool found = false;
            foreach (var header in headers)
            {
                if (!String.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase)) continue;
                if (found) return null;
                found = true;
                value = header.Value;
            }
            if (String.IsNullOrEmpty(value) || value.Length > MaximumMetadataHeader) return null;
            for (int i = 0; i < value.Length; i++) if (Char.IsControl(value[i])) return null;
            return value.Trim();
        }

        private static long? MetadataNumber(string value)
        {
            long parsed;
            if (String.IsNullOrEmpty(value) || value.Length > 19 ||
                !Int64.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out parsed) || parsed < 0) return null;
            return parsed;
        }

        private static string MetadataTitle(string value)
        {
            if (String.IsNullOrEmpty(value)) return null;
            if (value.StartsWith("base64:", StringComparison.OrdinalIgnoreCase))
            {
                string encoded = value.Substring(7);
                if (encoded.Length == 0 || encoded.Length > 2048) return null;
                for (int i = 0; i < encoded.Length; i++)
                    if (!(encoded[i] >= 'A' && encoded[i] <= 'Z') && !(encoded[i] >= 'a' && encoded[i] <= 'z') &&
                        !(encoded[i] >= '0' && encoded[i] <= '9') && encoded[i] != '+' && encoded[i] != '/' && encoded[i] != '=') return null;
                byte[] bytes = null;
                try
                {
                    bytes = Convert.FromBase64String(encoded);
                    value = new UTF8Encoding(false, true).GetString(bytes);
                }
                catch (FormatException) { return null; }
                catch (DecoderFallbackException) { return null; }
                finally { if (bytes != null) Array.Clear(bytes, 0, bytes.Length); }
            }
            var clean = new StringBuilder();
            for (int i = 0; i < value.Length; i++)
            {
                UnicodeCategory category = Char.GetUnicodeCategory(value, i);
                if (Char.IsControl(value[i]) || category == UnicodeCategory.Format) continue;
                if (Char.IsHighSurrogate(value[i]))
                {
                    if (i + 1 < value.Length && Char.IsLowSurrogate(value[i + 1])) { clean.Append(value[i]); clean.Append(value[++i]); }
                    continue;
                }
                if (!Char.IsLowSurrogate(value[i])) clean.Append(Char.IsWhiteSpace(value[i]) ? ' ' : value[i]);
            }
            value = clean.ToString().Trim();
            if (value.Length == 0) return null;
            // Happ's documented display limit is 25 characters; do not split UTF-16 pairs.
            var elements = StringInfo.GetTextElementEnumerator(value);
            var title = new StringBuilder();
            for (int count = 0; count < 25 && elements.MoveNext(); count++) title.Append(elements.GetTextElement());
            return title.ToString();
        }

        private static string MetadataUrl(string value)
        {
            if (String.IsNullOrEmpty(value) || value.Length > 2048 || value.IndexOf('\\') >= 0) return null;
            for (int i = 0; i < value.Length; i++) if (Char.IsControl(value[i])) return null;
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) ||
                !String.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                String.IsNullOrEmpty(uri.Host) || uri.HostNameType == UriHostNameType.Unknown || !String.IsNullOrEmpty(uri.UserInfo)) return null;
            return uri.AbsoluteUri;
        }

        public static bool Check(int port, OperationCancel cancel)
        {
            return Check(port, "https://www.gstatic.com/generate_204", 6, cancel);
        }

        public static bool Check(int port, string httpsUrl, int timeoutSeconds, OperationCancel cancel)
        {
            if (cancel == null) throw new ArgumentNullException("cancel");
            ValidatePort(port, false);
            if (timeoutSeconds < 2 || timeoutSeconds > 15) throw new ArgumentOutOfRangeException("timeoutSeconds");
            string target = ProbeUri(httpsUrl).AbsoluteUri;
            cancel.ThrowIfCancellationRequested();
            if (!String.Equals(target, "https://www.gstatic.com/generate_204", StringComparison.OrdinalIgnoreCase))
                return Probe(port, target, timeoutSeconds * 1000, cancel) >= 0;
            using (var race = new ProbeRace(port, timeoutSeconds * 1000, cancel))
                return race.Run();
        }

        public static int Probe(int port, string httpsUrl, int timeoutMilliseconds, OperationCancel cancel)
        {
            if (cancel == null) throw new ArgumentNullException("cancel");
            ValidatePort(port, false); // Never silently fall back to a direct request.
            if (timeoutMilliseconds < 2000 || timeoutMilliseconds > 15000) throw new ArgumentOutOfRangeException("timeoutMilliseconds");
            Uri target = ProbeUri(httpsUrl);
            cancel.ThrowIfCancellationRequested();
            Stopwatch elapsed = Stopwatch.StartNew();
            using (var local = new OperationCancel())
            using (cancel.Register(local.Cancel))
            using (var request = new AsyncProbe(elapsed, timeoutMilliseconds))
            using (local.Register(request.Cancel))
            using (var timer = new Timer(delegate(object state) { local.Cancel(); }, null, timeoutMilliseconds, Timeout.Infinite))
            {
                try
                {
                    request.Start(port, target, timeoutMilliseconds, local);
                    int remaining = Math.Max(0, timeoutMilliseconds - (int)elapsed.ElapsedMilliseconds);
                    if (!request.Wait(remaining)) local.Cancel();
                    cancel.ThrowIfCancellationRequested();
                    return request.Result;
                }
                catch (OperationCancelledException) { cancel.ThrowIfCancellationRequested(); return -1; }
                catch (IOException) { cancel.ThrowIfCancellationRequested(); return -1; }
            }
        }

        private static Uri ProbeUri(string value)
        {
            string safe = MetadataUrl(value);
            if (safe == null) throw new ArgumentException("Нужен корректный HTTPS-адрес проверки.", "httpsUrl");
            return new Uri(safe);
        }

        private sealed class ProbeRace : IDisposable
        {
            private readonly object gate = new object();
            private readonly ManualResetEvent done = new ManualResetEvent(false);
            private readonly OperationCancel local = new OperationCancel();
            private readonly OperationCancel parent;
            private readonly IDisposable link;
            private readonly int port, timeout;
            private int remaining = 3;
            private bool success, returned;
            internal ProbeRace(int port, int timeout, OperationCancel parent)
            {
                this.port = port; this.timeout = timeout; this.parent = parent;
                link = parent.Register(delegate
                {
                    local.Cancel();
                    lock (gate) { if (!returned) done.Set(); }
                });
            }
            internal bool Run()
            {
                ThreadPool.QueueUserWorkItem(delegate { Worker("https://www.gstatic.com/generate_204"); });
                ThreadPool.QueueUserWorkItem(delegate { Worker("https://www.microsoft.com/"); });
                // Some subscription entries intentionally proxy only YouTube.
                // Such an entry must not be labelled dead solely because two
                // unrelated connectivity-test domains are unavailable through it.
                ThreadPool.QueueUserWorkItem(delegate { Worker("https://www.youtube.com/"); });
                done.WaitOne(timeout, false);
                parent.ThrowIfCancellationRequested();
                lock (gate) return success;
            }
            private void Worker(string url)
            {
                bool passed = false;
                try { passed = Probe(port, url, timeout, local) >= 0; }
                catch (OperationCancelledException) { }
                catch (Exception) { /* Health failures never escape a thread-pool worker. */ }
                lock (gate)
                {
                    success |= passed;
                    remaining--;
                    if (success || remaining == 0) done.Set();
                    if (remaining == 0 && returned) { done.Close(); local.Dispose(); }
                }
                if (passed) local.Cancel();
            }
            public void Dispose()
            {
                local.Cancel();
                link.Dispose();
                lock (gate)
                {
                    returned = true;
                    if (remaining == 0) { done.Close(); local.Dispose(); }
                }
            }
        }

        // Async request handles are essential here: Microsoft explicitly prohibits
        // closing an in-flight synchronous request from a cancellation thread.
        private sealed class AsyncProbe : IDisposable
        {
            private const uint AsyncFlag = 0x10000000;
            private const uint ContextOption = 45;
            private const uint SendComplete = 0x00400000, HeadersAvailable = 0x00020000;
            private const uint RequestError = 0x00200000, HandleClosing = 0x00000800;
            private static readonly WinHttpStatusCallback Callback = OnStatus;
            private readonly object gate = new object();
            private readonly ManualResetEvent done = new ManualResetEvent(false);
            private readonly Stopwatch elapsed;
            private readonly int timeout;
            private NativeHandle session, connection, request;
            private GCHandle root;
            private bool callbackReady, nativeReleased, completed, disposed;
            private int result = -1;
            internal AsyncProbe(Stopwatch elapsed, int timeout) { this.elapsed = elapsed; this.timeout = timeout; }
            internal int Result { get { lock (gate) return result; } }
            internal bool Wait(int milliseconds) { return done.WaitOne(milliseconds, false); }
            internal void Start(int port, Uri target, int timeoutMilliseconds, OperationCancel cancel)
            {
                lock (gate)
                {
                    if (completed || disposed) return;
                    session = OpenSession(port, cancel, AsyncFlag);
                    connection = OpenConnection(session, target, cancel);
                    request = OpenRequest(connection, target, cancel);
                    Require(WinHttpSetTimeouts(request.Value, timeoutMilliseconds, timeoutMilliseconds, timeoutMilliseconds, timeoutMilliseconds), "PROBE_TIMEOUT", cancel);
                    root = GCHandle.Alloc(this);
                    UIntPtr context = (UIntPtr)(ulong)GCHandle.ToIntPtr(root).ToInt64();
                    Require(WinHttpSetOptionPointer(request.Value, ContextOption, ref context, (uint)IntPtr.Size), "PROBE_CONTEXT", cancel);
                    IntPtr previous = WinHttpSetStatusCallback(request.Value, Callback, SendComplete | HeadersAvailable | RequestError | 0x00000C00, UIntPtr.Zero);
                    if (previous == new IntPtr(-1)) throw NativeError("PROBE_CALLBACK", Marshal.GetLastWin32Error());
                    callbackReady = true;
                    cancel.ThrowIfCancellationRequested();
                    const string headers = "Accept: */*\r\nConnection: close\r\n";
                    if (!WinHttpSendRequest(request.Value, headers, (uint)headers.Length, IntPtr.Zero, 0, 0, context)) Finish(false);
                }
            }
            private static void OnStatus(IntPtr handle, UIntPtr context, uint status, IntPtr information, uint length)
            {
                if (context == UIntPtr.Zero) return;
                // The GCHandle remains rooted until HANDLE_CLOSING, WinHTTP's final callback.
                AsyncProbe state = null;
                try
                {
                    state = (AsyncProbe)GCHandle.FromIntPtr((IntPtr)(long)context.ToUInt64()).Target;
                    state.StatusChanged(status);
                }
                catch { if (state != null) state.Cancel(); }
            }
            private void StatusChanged(uint status)
            {
                lock (gate)
                {
                    if (status == HandleClosing)
                    {
                        nativeReleased = true;
                        CloseParents();
                        if (root.IsAllocated) root.Free();
                        if (disposed) done.Close();
                        return;
                    }
                    if (completed || disposed || request == null || request.Value == IntPtr.Zero) return;
                    if (status == SendComplete)
                    {
                        if (!WinHttpReceiveResponse(request.Value, IntPtr.Zero)) Finish(false);
                    }
                    else if (status == HeadersAvailable)
                    {
                        uint code, size = 4;
                        bool read = WinHttpQueryHeadersNumber(request.Value, StatusCodeQuery, null, out code, ref size, IntPtr.Zero);
                        Finish(read && code >= 200 && code < 400);
                    }
                    else if (status == RequestError) Finish(false);
                }
            }
            private void Finish(bool success)
            {
                if (completed || disposed) return;
                completed = true;
                long milliseconds = elapsed.ElapsedMilliseconds;
                if (success && milliseconds <= timeout) result = (int)Math.Max(1, milliseconds);
                done.Set();
            }
            internal void Cancel()
            {
                lock (gate)
                {
                    if (disposed) return;
                    Finish(false);
                    CloseRequest();
                }
            }
            private void CloseRequest()
            {
                if (request != null && request.Value != IntPtr.Zero)
                {
                    IntPtr handle = request.Value;
                    request.Value = IntPtr.Zero; // No later callback can use the closed handle.
                    WinHttpCloseHandle(handle);
                }
                if (!callbackReady)
                {
                    nativeReleased = true;
                    CloseParents();
                    if (root.IsAllocated) root.Free();
                }
            }
            private void CloseParents()
            {
                if (connection != null) { connection.Dispose(); connection = null; }
                if (session != null) { session.Dispose(); session = null; }
            }
            public void Dispose()
            {
                lock (gate)
                {
                    if (disposed) return;
                    Finish(false);
                    disposed = true;
                    CloseRequest();
                    if (nativeReleased) done.Close();
                }
            }
        }

        private static void ValidatePort(int port, bool allowDirect)
        {
            if (port < (allowDirect ? 0 : 1) || port > 65535) throw new ArgumentOutOfRangeException("port");
        }

        private static bool SameOrigin(Uri left, Uri right)
        {
            return String.Equals(right.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
                String.Equals(left.DnsSafeHost, right.DnsSafeHost, StringComparison.OrdinalIgnoreCase) && left.Port == right.Port;
        }

        private static NativeHandle OpenSession(int proxyPort, OperationCancel cancel)
        {
            return OpenSession(proxyPort, cancel, 0);
        }

        private static NativeHandle OpenSession(int proxyPort, OperationCancel cancel, uint flags)
        {
            cancel.ThrowIfCancellationRequested();
            string proxy = proxyPort == 0 ? null : "127.0.0.1:" + proxyPort.ToString(CultureInfo.InvariantCulture);
            NativeHandle session = Handle(WinHttpOpen(UserAgent, proxyPort == 0 ? 1U : 3U, proxy, null, flags), "OPEN", cancel);
            try
            {
                uint tls = Tls12;
                Require(WinHttpSetOption(session.Value, SecureProtocolsOption, ref tls, 4), "TLS12", cancel);
                Require(WinHttpSetTimeouts(session.Value, RequestTimeout, RequestTimeout, RequestTimeout, RequestTimeout), "TIMEOUTS", cancel);
                return session;
            }
            catch { session.Dispose(); throw; }
        }

        private static NativeHandle OpenConnection(NativeHandle session, Uri uri, OperationCancel cancel)
        {
            cancel.ThrowIfCancellationRequested();
            string host = uri.HostNameType == UriHostNameType.Dns ? new IdnMapping().GetAscii(uri.DnsSafeHost) : uri.DnsSafeHost;
            return Handle(WinHttpConnect(session.Value, host, (ushort)uri.Port, 0), "CONNECT", cancel);
        }

        private static NativeHandle OpenRequest(NativeHandle connection, Uri uri, OperationCancel cancel)
        {
            cancel.ThrowIfCancellationRequested();
            NativeHandle request = Handle(WinHttpOpenRequest(connection.Value, "GET", uri.PathAndQuery, "HTTP/1.1", null, IntPtr.Zero, SecureFlag), "REQUEST", cancel);
            try
            {
                uint disabled = DisableCookiesRedirectsAuthentication;
                Require(WinHttpSetOption(request.Value, DisableFeatureOption, ref disabled, 4), "OPTIONS", cancel);
                // No security-ignore flags or credentials are supplied. Windows validates
                // certificate names, dates and chains. Redirects are handled above.
                return request;
            }
            catch { request.Dispose(); throw; }
        }

        private static void SendAndReceive(NativeHandle request, Stopwatch elapsed, OperationCancel cancel)
        {
            SetRemainingTimeout(request, elapsed, cancel);
            const string headers = "Accept: */*\r\nAccept-Encoding: gzip\r\nCache-Control: no-cache\r\nPragma: no-cache\r\n";
            Require(WinHttpSendRequest(request.Value, headers, (uint)headers.Length, IntPtr.Zero, 0, 0, UIntPtr.Zero), "SEND", cancel);
            SetRemainingTimeout(request, elapsed, cancel);
            Require(WinHttpReceiveResponse(request.Value, IntPtr.Zero), "RECEIVE", cancel);
            CheckDeadline(elapsed, cancel);
        }

        private static uint Status(NativeHandle request, OperationCancel cancel)
        {
            uint status, size = 4;
            Require(WinHttpQueryHeadersNumber(request.Value, StatusCodeQuery, null, out status, ref size, IntPtr.Zero), "STATUS", cancel);
            return status;
        }

        private static string Header(NativeHandle request, uint query, OperationCancel cancel)
        {
            cancel.ThrowIfCancellationRequested();
            uint size = 0;
            bool found = WinHttpQueryHeadersText(request.Value, query, null, null, ref size, IntPtr.Zero);
            int error = Marshal.GetLastWin32Error();
            cancel.ThrowIfCancellationRequested();
            if (!found && error == HeaderNotFound) return null;
            if (!found && error != InsufficientBuffer) throw NativeError("HEADER", error);
            if (size == 0) return String.Empty;
            if (size > 65536) throw new IOException("HTTP_HEADER_TOO_LARGE");
            StringBuilder text = new StringBuilder((int)(size / 2 + 1));
            Require(WinHttpQueryHeadersText(request.Value, query, null, text, ref size, IntPtr.Zero), "HEADER", cancel);
            return text.ToString();
        }

        private static byte[] ReadBody(NativeHandle request, Stopwatch elapsed, OperationCancel cancel)
        {
            using (MemoryStream output = new MemoryStream())
            {
                byte[] buffer = new byte[8192];
                try
                {
                    for (;;)
                    {
                        SetRemainingTimeout(request, elapsed, cancel);
                        uint count;
                        Require(WinHttpReadData(request.Value, buffer, (uint)buffer.Length, out count), "READ", cancel);
                        CheckDeadline(elapsed, cancel);
                        if (count == 0) break;
                        if (output.Length + count > MaximumBody) throw new IOException("SUBSCRIPTION_TOO_LARGE");
                        output.Write(buffer, 0, (int)count);
                    }
                    return output.ToArray();
                }
                finally { Array.Clear(buffer, 0, buffer.Length); }
            }
        }

        private static byte[] DecodeBody(byte[] wireBody, string encoding, OperationCancel cancel)
        {
            if (String.IsNullOrEmpty(encoding) || String.Equals(encoding.Trim(), "identity", StringComparison.OrdinalIgnoreCase)) return wireBody;
            if (!String.Equals(encoding.Trim(), "gzip", StringComparison.OrdinalIgnoreCase)) throw new IOException("SUBSCRIPTION_ENCODING");
            using (MemoryStream input = new MemoryStream(wireBody, false))
            using (GZipStream gzip = new GZipStream(input, CompressionMode.Decompress))
            using (MemoryStream output = new MemoryStream())
            {
                byte[] buffer = new byte[8192];
                try
                {
                    int count;
                    while ((count = gzip.Read(buffer, 0, buffer.Length)) != 0)
                    {
                        cancel.ThrowIfCancellationRequested();
                        if (output.Length + count > MaximumBody) throw new IOException("SUBSCRIPTION_TOO_LARGE");
                        output.Write(buffer, 0, count);
                    }
                    return output.ToArray();
                }
                finally { Array.Clear(buffer, 0, buffer.Length); }
            }
        }

        private static void SetRemainingTimeout(NativeHandle request, Stopwatch elapsed, OperationCancel cancel)
        {
            CheckDeadline(elapsed, cancel);
            int remaining = Math.Max(1, RequestTimeout - (int)elapsed.ElapsedMilliseconds);
            Require(WinHttpSetTimeouts(request.Value, remaining, remaining, remaining, remaining), "TIMEOUTS", cancel);
        }

        private static void CheckDeadline(Stopwatch elapsed, OperationCancel cancel)
        {
            cancel.ThrowIfCancellationRequested();
            if (elapsed.ElapsedMilliseconds >= RequestTimeout) throw new IOException("WINHTTP_TIMEOUT");
        }

        private static NativeHandle Handle(IntPtr value, string operation, OperationCancel cancel)
        {
            int error = Marshal.GetLastWin32Error();
            if (value == IntPtr.Zero) { cancel.ThrowIfCancellationRequested(); throw NativeError(operation, error); }
            return new NativeHandle(value);
        }

        private static void Require(bool success, string operation, OperationCancel cancel)
        {
            int error = Marshal.GetLastWin32Error();
            cancel.ThrowIfCancellationRequested();
            if (!success) throw NativeError(operation, error);
        }

        private static IOException NativeError(string operation, int error)
        {
            // Never include the request URL, response body or native exception text.
            return new IOException("WINHTTP_" + operation + "_" + error.ToString(CultureInfo.InvariantCulture));
        }

        private sealed class NativeHandle : IDisposable
        {
            internal IntPtr Value;
            internal NativeHandle(IntPtr value) { Value = value; }
            public void Dispose()
            {
                // Only the owning worker closes its handles, after synchronous calls
                // return. Closing an in-flight synchronous handle from a cancellation
                // callback is expressly unsafe in WinHTTP; native timeouts bound it.
                if (Value != IntPtr.Zero) { WinHttpCloseHandle(Value); Value = IntPtr.Zero; }
            }
        }

        [DllImport("winhttp.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr WinHttpOpen(string userAgent, uint accessType, string proxy, string proxyBypass, uint flags);
        [DllImport("winhttp.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr WinHttpConnect(IntPtr session, string serverName, ushort serverPort, uint reserved);
        [DllImport("winhttp.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr WinHttpOpenRequest(IntPtr connection, string verb, string objectName, string version, string referrer, IntPtr acceptTypes, uint flags);
        [DllImport("winhttp.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WinHttpSetOption(IntPtr handle, uint option, ref uint value, uint length);
        [DllImport("winhttp.dll", EntryPoint = "WinHttpSetOption", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WinHttpSetOptionPointer(IntPtr handle, uint option, ref UIntPtr value, uint length);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void WinHttpStatusCallback(IntPtr handle, UIntPtr context, uint status, IntPtr information, uint length);
        [DllImport("winhttp.dll", SetLastError = true)]
        private static extern IntPtr WinHttpSetStatusCallback(IntPtr handle, WinHttpStatusCallback callback, uint notifications, UIntPtr reserved);
        [DllImport("winhttp.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WinHttpSetTimeouts(IntPtr handle, int resolve, int connect, int send, int receive);
        [DllImport("winhttp.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WinHttpSendRequest(IntPtr request, string headers, uint headersLength, IntPtr optional, uint optionalLength, uint totalLength, UIntPtr context);
        [DllImport("winhttp.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WinHttpReceiveResponse(IntPtr request, IntPtr reserved);
        [DllImport("winhttp.dll", EntryPoint = "WinHttpQueryHeaders", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WinHttpQueryHeadersNumber(IntPtr request, uint query, string name, out uint buffer, ref uint length, IntPtr index);
        [DllImport("winhttp.dll", EntryPoint = "WinHttpQueryHeaders", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WinHttpQueryHeadersText(IntPtr request, uint query, string name, StringBuilder buffer, ref uint length, IntPtr index);
        [DllImport("winhttp.dll", EntryPoint = "WinHttpQueryHeaders", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WinHttpQueryHeadersIndexed(IntPtr request, uint query, string name, StringBuilder buffer, ref uint length, ref uint index);
        [DllImport("winhttp.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WinHttpReadData(IntPtr request, [Out] byte[] buffer, uint bytesToRead, out uint bytesRead);
        [DllImport("winhttp.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WinHttpCloseHandle(IntPtr handle);
    }
}
