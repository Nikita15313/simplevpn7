using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace SimpleVPN
{
    // Update credentials are confined to the repository API. WinHTTP is used so
    // TLS 1.2 also works under the CLR 2 supplied with Windows 7 SP1.
    public static class UpdateTransport
    {
        private const int LargestBody = 128 * 1024 * 1024;
        private const int CallTimeout = 30000;
        private const int DownloadDeadline = 180000;
        private const string RepositoryPath = "/repos/Nikita15313/simplevpn7/releases/";

        public static byte[] Get(string url, string token, string accept, int maximumBytes)
        { return Get(url, token, accept, maximumBytes, 0); }

        // A supplied proxy is always the app-owned loopback listener; neither
        // WinINet settings nor machine-wide WinHTTP settings are changed.
        public static byte[] Get(string url, string token, string accept, int maximumBytes, int proxyPort)
        {
            Uri current = ParseInitial(url);
            if (maximumBytes < 1 || maximumBytes > LargestBody || proxyPort < 0 || proxyPort > 65535)
                throw new IOException("UPDATE_ARGUMENTS");
            if (accept != "application/vnd.github+json" && accept != "application/octet-stream" && accept != "application/json")
                throw new IOException("UPDATE_ACCEPT");
            ValidateToken(token);
            Stopwatch elapsed = Stopwatch.StartNew();
            int deadline = maximumBytes <= 8 * 1024 * 1024 ? CallTimeout : DownloadDeadline;
            string proxy = proxyPort == 0 ? null : "127.0.0.1:" + proxyPort.ToString(CultureInfo.InvariantCulture);
            using (NativeHandle session = Handle(WinHttpOpen("SimpleVPN7-Updater/3", proxyPort == 0 ? 1U : 3U, proxy, null, 0), "OPEN"))
            {
                uint tls = 0x800; // WINHTTP_FLAG_SECURE_PROTOCOL_TLS1_2
                Require(WinHttpSetOption(session.Value, 84, ref tls, 4), "TLS12");
                Require(WinHttpSetTimeouts(session.Value, CallTimeout, CallTimeout, CallTimeout, CallTimeout), "TIMEOUTS");
                for (int redirects = 0; ; redirects++)
                {
                    CheckDeadline(elapsed, deadline);
                    using (NativeHandle connection = Handle(WinHttpConnect(session.Value, current.DnsSafeHost, 443, 0), "CONNECT"))
                    using (NativeHandle request = Handle(WinHttpOpenRequest(connection.Value, "GET", current.PathAndQuery, "HTTP/1.1", null, IntPtr.Zero, 0x00800000), "REQUEST"))
                    {
                        uint disabled = 7; // cookies, automatic redirects and automatic authentication
                        Require(WinHttpSetOption(request.Value, 63, ref disabled, 4), "OPTIONS");
                        string headers = "Accept: " + accept + "\r\nAccept-Encoding: identity\r\nX-GitHub-Api-Version: 2022-11-28\r\n";
                        if (ShouldAuthorize(current, redirects) && !String.IsNullOrEmpty(token))
                            headers += "Authorization: Bearer " + token + "\r\n";
                        SetTimeout(request, elapsed, deadline);
                        Require(WinHttpSendRequest(request.Value, headers, (uint)headers.Length, IntPtr.Zero, 0, 0, UIntPtr.Zero), "SEND");
                        SetTimeout(request, elapsed, deadline);
                        Require(WinHttpReceiveResponse(request.Value, IntPtr.Zero), "RECEIVE");
                        CheckDeadline(elapsed, deadline);
                        uint status, statusLength = 4;
                        Require(WinHttpQueryHeadersNumber(request.Value, 19 | 0x20000000, null, out status, ref statusLength, IntPtr.Zero), "STATUS");
                        if (status == 301 || status == 302 || status == 303 || status == 307 || status == 308)
                        {
                            if (redirects >= 4) throw new IOException("UPDATE_REDIRECT_LIMIT");
                            current = ResolveRedirect(current, Header(request, 33));
                            continue;
                        }
                        if (status != 200) throw new IOException("UPDATE_HTTP" + status.ToString(CultureInfo.InvariantCulture));
                        string encoding = Header(request, 29);
                        if (!String.IsNullOrEmpty(encoding) && !String.Equals(encoding.Trim(), "identity", StringComparison.OrdinalIgnoreCase))
                            throw new IOException("UPDATE_ENCODING");
                        string lengthHeader = Header(request, 5);
                        long expectedLength = -1;
                        if (!String.IsNullOrEmpty(lengthHeader) && (!Int64.TryParse(lengthHeader.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out expectedLength) || expectedLength < 0 || expectedLength > maximumBytes))
                            throw new IOException("UPDATE_SIZE");
                        using (MemoryStream output = new MemoryStream())
                        {
                            byte[] buffer = new byte[16384];
                            try
                            {
                                for (;;)
                                {
                                    SetTimeout(request, elapsed, deadline);
                                    uint read;
                                    Require(WinHttpReadData(request.Value, buffer, (uint)buffer.Length, out read), "READ");
                                    CheckDeadline(elapsed, deadline);
                                    if (read == 0) break;
                                    if (output.Length + read > maximumBytes) throw new IOException("UPDATE_SIZE");
                                    output.Write(buffer, 0, (int)read);
                                }
                                if (expectedLength >= 0 && output.Length != expectedLength) throw new IOException("UPDATE_TRUNCATED");
                                return output.ToArray();
                            }
                            finally { Array.Clear(buffer, 0, buffer.Length); }
                        }
                    }
                }
            }
        }

        internal static Uri ParseInitial(string url)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || !IsRepositoryApi(uri)) throw new IOException("UPDATE_URL");
            return uri;
        }

        internal static bool ShouldAuthorize(Uri uri, int redirects)
        { return redirects == 0 && IsRepositoryApi(uri); }

        private static bool SecureOrigin(Uri uri)
        {
            return uri != null && uri.IsAbsoluteUri && String.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase) &&
                uri.Port == 443 && String.IsNullOrEmpty(uri.UserInfo) && String.IsNullOrEmpty(uri.Fragment) && uri.HostNameType == UriHostNameType.Dns;
        }

        private static bool IsRepositoryApi(Uri uri)
        {
            if (!SecureOrigin(uri) || !String.Equals(uri.DnsSafeHost, "api.github.com", StringComparison.OrdinalIgnoreCase) || !String.IsNullOrEmpty(uri.Query)) return false;
            string path = uri.AbsolutePath;
            if (!path.StartsWith(RepositoryPath, StringComparison.OrdinalIgnoreCase)) return false;
            string suffix = path.Substring(RepositoryPath.Length);
            if (suffix == "latest") return true;
            const string assets = "assets/";
            if (!suffix.StartsWith(assets, StringComparison.Ordinal) || suffix.Length == assets.Length) return false;
            for (int i = assets.Length; i < suffix.Length; i++) if (suffix[i] < '0' || suffix[i] > '9') return false;
            return true;
        }

        internal static Uri ResolveRedirect(Uri current, string location)
        {
            Uri next;
            if (String.IsNullOrEmpty(location) || location.Length > 16384 || !Uri.TryCreate(current, location, out next) || !SecureOrigin(next))
                throw new IOException("UPDATE_REDIRECT");
            string host = next.DnsSafeHost;
            bool cdn = String.Equals(host, "release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(host, "objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(host, "github-production-release-asset-2e65be.s3.amazonaws.com", StringComparison.OrdinalIgnoreCase);
            if (!cdn && !IsRepositoryApi(next)) throw new IOException("UPDATE_REDIRECT_HOST");
            return next;
        }

        private static void ValidateToken(string token)
        {
            if (String.IsNullOrEmpty(token)) return;
            if (token.Length > 4096) throw new IOException("UPDATE_CREDENTIAL");
            foreach (char value in token) if (value < 33 || value > 126) throw new IOException("UPDATE_CREDENTIAL");
        }

        private static string Header(NativeHandle request, uint query)
        {
            uint size = 0;
            bool found = WinHttpQueryHeadersText(request.Value, query, null, null, ref size, IntPtr.Zero);
            int error = Marshal.GetLastWin32Error();
            if (!found && error == 12150) return null;
            if (!found && error != 122) throw NativeError("HEADER", error);
            if (size == 0) return String.Empty;
            if (size > 65536) throw new IOException("UPDATE_HEADER_SIZE");
            StringBuilder value = new StringBuilder((int)(size / 2 + 1));
            Require(WinHttpQueryHeadersText(request.Value, query, null, value, ref size, IntPtr.Zero), "HEADER");
            return value.ToString();
        }

        private static void SetTimeout(NativeHandle request, Stopwatch elapsed, int deadline)
        {
            CheckDeadline(elapsed, deadline);
            int remaining = Math.Min(CallTimeout, Math.Max(1, deadline - (int)elapsed.ElapsedMilliseconds));
            Require(WinHttpSetTimeouts(request.Value, remaining, remaining, remaining, remaining), "TIMEOUTS");
        }
        private static void CheckDeadline(Stopwatch elapsed, int deadline)
        { if (elapsed.ElapsedMilliseconds >= deadline) throw new IOException("UPDATE_TIMEOUT"); }
        private static NativeHandle Handle(IntPtr value, string operation)
        { int error = Marshal.GetLastWin32Error(); if (value == IntPtr.Zero) throw NativeError(operation, error); return new NativeHandle(value); }
        private static void Require(bool success, string operation)
        { int error = Marshal.GetLastWin32Error(); if (!success) throw NativeError(operation, error); }
        private static IOException NativeError(string operation, int error)
        { return new IOException("UPDATE_WINHTTP_" + operation + "_" + error.ToString(CultureInfo.InvariantCulture)); }
        private sealed class NativeHandle : IDisposable
        {
            internal IntPtr Value;
            internal NativeHandle(IntPtr value) { Value = value; }
            public void Dispose() { if (Value != IntPtr.Zero) { WinHttpCloseHandle(Value); Value = IntPtr.Zero; } }
        }

        [DllImport("winhttp.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr WinHttpOpen(string agent, uint access, string proxy, string bypass, uint flags);
        [DllImport("winhttp.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr WinHttpConnect(IntPtr session, string host, ushort port, uint reserved);
        [DllImport("winhttp.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr WinHttpOpenRequest(IntPtr connection, string verb, string path, string version, string referrer, IntPtr accept, uint flags);
        [DllImport("winhttp.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WinHttpSetOption(IntPtr handle, uint option, ref uint value, uint size);
        [DllImport("winhttp.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WinHttpSetTimeouts(IntPtr handle, int resolve, int connect, int send, int receive);
        [DllImport("winhttp.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WinHttpSendRequest(IntPtr request, string headers, uint headersLength, IntPtr body, uint bodyLength, uint totalLength, UIntPtr context);
        [DllImport("winhttp.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WinHttpReceiveResponse(IntPtr request, IntPtr reserved);
        [DllImport("winhttp.dll", EntryPoint = "WinHttpQueryHeaders", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WinHttpQueryHeadersNumber(IntPtr request, uint query, string name, out uint value, ref uint size, IntPtr index);
        [DllImport("winhttp.dll", EntryPoint = "WinHttpQueryHeaders", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WinHttpQueryHeadersText(IntPtr request, uint query, string name, StringBuilder value, ref uint size, IntPtr index);
        [DllImport("winhttp.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WinHttpReadData(IntPtr request, [Out] byte[] buffer, uint size, out uint read);
        [DllImport("winhttp.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WinHttpCloseHandle(IntPtr handle);
    }
}
