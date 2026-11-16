using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace SimpleVPN
{
    public sealed class ServerProfile
    {
        public string Id;
        public string Name;
        public string Protocol;
        public Dictionary<string, object> Outbound;
        public override string ToString() { return Name; }
    }

    // Imports connection links or extracts whitelisted remote profiles from Xray JSON.
    // Supplied listeners, DNS, routing, logging, API and plugins are never executed.
    public static class SubscriptionParser
    {
        private const int MaximumTextLength = 4 * 1024 * 1024;
        private static JavaScriptSerializer Json()
        {
            return new JavaScriptSerializer { MaxJsonLength = MaximumTextLength, RecursionLimit = 24 };
        }

        public static List<ServerProfile> Parse(string text)
        {
            int skipped; return Parse(text, out skipped);
        }

        public static List<ServerProfile> Parse(string text, out int skipped)
        {
            skipped = 0;
            if (Compat.IsBlank(text)) throw Bad("Подписка пуста.");
            if (text.Length > MaximumTextLength) throw Bad("Подписка слишком большая (более 4 МБ).");
            text = text.Trim().TrimStart('\ufeff');
            if (text.StartsWith("{") || text.StartsWith("[")) return ParseJsonSubscription(text, out skipped);
            if (text.IndexOf("://", StringComparison.Ordinal) < 0)
            {
                try { text = Decode64(text); }
                catch (FormatException) { throw Bad("Ответ не содержит ссылок серверов. Нужна подписка VLESS, VMess, Trojan или Shadowsocks."); }
            }
            text = text.Trim().TrimStart('\ufeff');
            if (text.StartsWith("{") || text.StartsWith("[")) return ParseJsonSubscription(text, out skipped);
            if (text.StartsWith("<"))
                throw Bad("Сервер вернул страницу или полный конфигурационный файл. Нужна подписка со ссылками серверов.");

            List<ServerProfile> result = new List<ServerProfile>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            List<string> errors = new List<string>();
            string[] lines = text.Replace("\r", "").Split('\n');
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("//")) continue;
                try
                {
                    if (line.Length > 65536) throw Bad("Ссылка сервера слишком длинная.");
                    ServerProfile profile;
                    if (line.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase)) profile = ParseVmess(line);
                    else if (line.StartsWith("vless://", StringComparison.OrdinalIgnoreCase)) profile = ParseUri(line, "vless");
                    else if (line.StartsWith("trojan://", StringComparison.OrdinalIgnoreCase)) profile = ParseUri(line, "trojan");
                    else if (line.StartsWith("ss://", StringComparison.OrdinalIgnoreCase)) profile = ParseShadowsocks(line);
                    else throw Bad("Неподдерживаемый формат ссылки.");
                    if (seen.Add(profile.Id)) result.Add(profile);
                    if (result.Count > 2000) throw new InvalidOperationException("В подписке больше 2000 серверов.");
                }
                catch (FormatException ex) { skipped++; if (errors.Count < 3 && !errors.Contains(ex.Message)) errors.Add(ex.Message); }
                catch (ArgumentException) { skipped++; if (errors.Count < 3) errors.Add("Повреждённая ссылка сервера."); }
            }
            if (result.Count == 0)
                throw Bad("В подписке нет поддерживаемых серверов. " + String.Join(" ", errors.ToArray()));
            return result;
        }

        private static List<ServerProfile> ParseJsonSubscription(string text, out int skipped)
        {
            skipped = 0;
            object root;
            try { root = Json().DeserializeObject(text); }
            catch (ArgumentException) { throw Bad("Повреждённый JSON подписки."); }
            catch (InvalidOperationException) { throw Bad("Повреждённый JSON подписки."); }
            IList configs = root as IList;
            if (configs == null) configs = new object[] { root };
            if (configs.Count > 2000) throw Bad("В подписке больше 2000 серверов.");
            List<ServerProfile> result = new List<ServerProfile>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            List<string> errors = new List<string>();
            foreach (object item in configs)
            {
                try
                {
                    Dictionary<string, object> config = Object(item);
                    IList outbounds = Value(config, "outbounds") as IList;
                    if (outbounds == null) throw Bad("В JSON нет списка серверов Xray.");
                    Dictionary<string, object> remote = null;
                    foreach (object raw in outbounds)
                    {
                        Dictionary<string, object> outbound = Object(raw);
                        string protocol = Scalar(outbound, "protocol", "").ToLowerInvariant();
                        if (protocol == "freedom" || protocol == "blackhole" || protocol == "dns") continue;
                        if (protocol != "vless" && protocol != "vmess" && protocol != "trojan" && protocol != "shadowsocks")
                            throw Bad("JSON содержит неподдерживаемый протокол сервера.");
                        if (remote != null) throw Bad("Цепочки и несколько прокси в одном JSON не поддерживаются.");
                        remote = outbound;
                    }
                    if (remote == null) throw Bad("В JSON не найден поддерживаемый удалённый сервер.");
                    ServerProfile profile = ExtractJsonOutbound(remote, Scalar(config, "remarks", ""));
                    if (seen.Add(profile.Id)) result.Add(profile);
                }
                catch (FormatException ex) { skipped++; if (errors.Count < 3 && !errors.Contains(ex.Message)) errors.Add(ex.Message); }
            }
            if (result.Count == 0) throw Bad("В JSON нет поддерживаемых серверов. " + String.Join(" ", errors.ToArray()));
            return result;
        }

        private static ServerProfile ExtractJsonOutbound(Dictionary<string, object> outbound, string name)
        {
            Fields(outbound, "tag", "protocol", "settings", "streamSettings", "mux");
            if (Value(outbound, "mux") != null)
            {
                Dictionary<string, object> mux = Object(outbound["mux"]);
                if (Scalar(mux, "enabled", "false").ToLowerInvariant() != "false") throw Bad("Профиль с включённым Mux пока не поддерживается.");
            }
            string protocol = Scalar(outbound, "protocol", "").ToLowerInvariant();
            Dictionary<string, object> settings = Object(Value(outbound, "settings"));
            Dictionary<string, object> endpoint;
            Dictionary<string, object> account;
            if (protocol == "vless" || protocol == "vmess")
            {
                if (settings.ContainsKey("vnext"))
                {
                    Fields(settings, "vnext", "packetEncoding");
                    endpoint = SingleObject(settings["vnext"]);
                    Fields(endpoint, "address", "port", "users");
                    account = SingleObject(Value(endpoint, "users"));
                }
                else
                {
                    Fields(settings, "address", "port", "id", "encryption", "flow", "security", "alterId", "level", "packetEncoding");
                    endpoint = settings;
                    account = new Dictionary<string, object>(settings);
                    account.Remove("address"); account.Remove("port"); account.Remove("packetEncoding");
                }
                Fields(account, "id", "encryption", "flow", "security", "alterId", "level", "email");
            }
            else
            {
                if (settings.ContainsKey("servers"))
                {
                    Fields(settings, "servers", "packetEncoding");
                    endpoint = SingleObject(settings["servers"]);
                }
                else endpoint = settings;
                Fields(endpoint, "address", "port", "password", "method", "level", "email", "packetEncoding");
                account = endpoint;
            }
            string host = NormalizeHost(Scalar(endpoint, "address", ""));
            int port = Port(Scalar(endpoint, "port", ""));
            Dictionary<string, string> q = JsonStream(Value(outbound, "streamSettings"), protocol);
            if (settings.ContainsKey("packetEncoding")) q["packetEncoding"] = Scalar(settings, "packetEncoding", "");
            if (protocol == "vless" || protocol == "trojan")
            {
                string user = Scalar(account, protocol == "vless" ? "id" : "password", "");
                if (protocol == "vless")
                {
                    q["encryption"] = Scalar(account, "encryption", "none");
                    q["flow"] = Scalar(account, "flow", "");
                }
                return ParseUri(MakeLink(protocol, user, host, port, q, name), protocol);
            }
            if (protocol == "shadowsocks")
            {
                if (Get(q, "type", "tcp") != "tcp" || Get(q, "security", "none") != "none" || q.Count > 2)
                    throw Bad("Этот транспорт Shadowsocks JSON не поддерживается.");
                return ParseShadowsocks(MakeLink("ss", Scalar(account, "method", "") + ":" + Scalar(account, "password", ""), host, port,
                    new Dictionary<string, string>(), name));
            }
            string id = Scalar(account, "id", "");
            ValidateId(id);
            if (Scalar(account, "alterId", "0") != "0") throw Bad("Устаревший VMess alterId не поддерживается.");
            string cipher = Scalar(account, "security", "auto").ToLowerInvariant();
            if (cipher != "auto" && cipher != "aes-128-gcm" && cipher != "chacha20-poly1305") throw Bad("Неподдерживаемое шифрование VMess.");
            Dictionary<string, object> output = D("vnext", new object[] { D("address", host, "port", port,
                "users", new object[] { D("id", NormalizeId(id), "alterId", 0, "security", cipher) }) });
            if (q.ContainsKey("packetEncoding"))
            {
                if (q["packetEncoding"].Length > 0) throw Bad("Параметр packetEncoding в этом режиме подключения не поддерживается.");
            }
            return Profile(protocol, name, host, port, output, Stream(q, host, Get(q, "security", "none")));
        }

        private static Dictionary<string, string> JsonStream(object raw, string protocol)
        {
            Dictionary<string, object> stream = raw == null ? D() : Object(raw);
            Fields(stream, "network", "security", "tcpSettings", "rawSettings", "tlsSettings", "realitySettings", "wsSettings",
                "grpcSettings", "httpupgradeSettings", "xhttpSettings", "splithttpSettings");
            Dictionary<string, string> q = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            q["type"] = Scalar(stream, "network", "tcp").ToLowerInvariant();
            q["security"] = Scalar(stream, "security", protocol == "trojan" ? "tls" : "none").ToLowerInvariant();
            if (q["type"] == "raw") q["type"] = "tcp";
            if (q["type"] == "splithttp") q["type"] = "xhttp";
            if (q["type"] == "websocket") q["type"] = "ws";
            if (Value(stream, "tlsSettings") != null)
            {
                if (q["security"] != "tls") throw Bad("Параметры TLS указаны без TLS.");
                Dictionary<string, object> tls = Object(stream["tlsSettings"]);
                Fields(tls, "serverName", "fingerprint", "allowInsecure", "alpn");
                Copy(tls, q, "serverName", "sni"); Copy(tls, q, "fingerprint", "fp"); Copy(tls, q, "allowInsecure", "allowInsecure");
                if (Value(tls, "alpn") != null) q["alpn"] = StringArray(tls["alpn"]);
            }
            if (Value(stream, "realitySettings") != null)
            {
                if (q["security"] != "reality") throw Bad("Параметры REALITY указаны без REALITY.");
                Dictionary<string, object> reality = Object(stream["realitySettings"]);
                Fields(reality, "show", "serverName", "fingerprint", "publicKey", "password", "shortId", "spiderX", "mldsa65Verify");
                Copy(reality, q, "serverName", "sni"); Copy(reality, q, "fingerprint", "fp");
                Copy(reality, q, "publicKey", "pbk"); Copy(reality, q, "password", "publicKey");
                Copy(reality, q, "shortId", "sid"); Copy(reality, q, "spiderX", "spx"); Copy(reality, q, "mldsa65Verify", "pqv");
            }
            foreach (string key in new string[] { "tcpSettings", "rawSettings", "wsSettings", "grpcSettings", "httpupgradeSettings", "xhttpSettings", "splithttpSettings" })
            {
                if (Value(stream, key) == null) continue;
                Dictionary<string, object> transport = Object(stream[key]);
                string expected = key == "rawSettings" || key == "tcpSettings" ? "tcp" :
                    key == "splithttpSettings" || key == "xhttpSettings" ? "xhttp" : key.Substring(0, key.Length - 8);
                if (expected != q["type"] && transport.Count > 0) throw Bad("JSON содержит противоречащие настройки транспорта.");
                if (expected == "tcp")
                {
                    Fields(transport, "header");
                    if (Value(transport, "header") == null) continue;
                    Dictionary<string, object> header = Object(transport["header"]);
                    Fields(header, "type", "request");
                    q["headerType"] = Scalar(header, "type", "none");
                    if (Value(header, "request") != null)
                    {
                        Dictionary<string, object> request = Object(header["request"]);
                        Fields(request, "version", "method", "path", "headers");
                        if (Scalar(request, "version", "1.1") != "1.1" || Scalar(request, "method", "GET") != "GET") throw Bad("Неподдерживаемый HTTP-заголовок TCP.");
                        if (Value(request, "path") != null)
                        {
                            IList paths = request["path"] as IList;
                            if (paths == null || paths.Count != 1) throw Bad("Неподдерживаемый список путей TCP.");
                            q["path"] = Convert.ToString(paths[0], CultureInfo.InvariantCulture);
                        }
                        if (Value(request, "headers") != null)
                        {
                            Dictionary<string, object> headers = Object(request["headers"]);
                            Fields(headers, "Host");
                            if (Value(headers, "Host") != null) q["host"] = StringArray(headers["Host"]);
                        }
                    }
                }
                else if (expected == "ws" || expected == "httpupgrade")
                {
                    Fields(transport, "path", "host", "headers", "maxEarlyData", "earlyDataHeaderName");
                    Copy(transport, q, "path", "path"); Copy(transport, q, "host", "host");
                    Copy(transport, q, "maxEarlyData", "ed"); Copy(transport, q, "earlyDataHeaderName", "eh");
                    if (Value(transport, "headers") != null)
                    {
                        Dictionary<string, object> headers = Object(transport["headers"]);
                        Fields(headers, "Host"); Copy(headers, q, "Host", "host");
                    }
                }
                else if (expected == "grpc")
                {
                    Fields(transport, "serviceName", "authority", "multiMode");
                    Copy(transport, q, "serviceName", "serviceName"); Copy(transport, q, "authority", "authority");
                    string multi = Scalar(transport, "multiMode", "false").ToLowerInvariant();
                    if (multi != "true" && multi != "false") throw Bad("Неверный режим gRPC.");
                    q["mode"] = multi == "true" ? "multi" : "gun";
                }
                else if (expected == "xhttp")
                {
                    Fields(transport, "path", "host", "mode", "extra");
                    Copy(transport, q, "path", "path"); Copy(transport, q, "host", "host"); Copy(transport, q, "mode", "mode");
                    if (Value(transport, "extra") != null) q["extra"] = Json().Serialize(Object(transport["extra"]));
                }
            }
            return q;
        }

        private static string MakeLink(string protocol, string user, string host, int port, Dictionary<string, string> query, string name)
        {
            StringBuilder link = new StringBuilder(protocol + "://" + Uri.EscapeDataString(user) + "@" + (host.Contains(":") ? "[" + host + "]" : host) + ":" + port);
            bool first = true;
            foreach (KeyValuePair<string, string> pair in query)
            {
                link.Append(first ? '?' : '&'); first = false;
                link.Append(Uri.EscapeDataString(pair.Key)).Append('=').Append(Uri.EscapeDataString(pair.Value));
            }
            link.Append('#').Append(Uri.EscapeDataString(name));
            return link.ToString();
        }
        private static object Value(Dictionary<string, object> obj, string key) { object value; return obj.TryGetValue(key, out value) ? value : null; }
        private static Dictionary<string, object> Object(object value)
        {
            Dictionary<string, object> result = value as Dictionary<string, object>;
            if (result == null) throw Bad("Неверная структура JSON сервера.");
            return result;
        }
        private static Dictionary<string, object> SingleObject(object value)
        {
            IList list = value as IList;
            if (list == null || list.Count != 1) throw Bad("В профиле должен быть один сервер и одна учётная запись.");
            return Object(list[0]);
        }
        private static string Scalar(Dictionary<string, object> obj, string key, string fallback)
        {
            object value = Value(obj, key);
            if (value == null) return fallback;
            if (value is IDictionary || value is IList) throw Bad("Неверный тип параметра JSON.");
            string text = Convert.ToString(value, CultureInfo.InvariantCulture);
            ValidatePlain(text);
            return text;
        }
        private static void Fields(Dictionary<string, object> obj, params string[] allowed)
        {
            HashSet<string> names = new HashSet<string>(allowed, StringComparer.Ordinal);
            foreach (string key in obj.Keys) if (!names.Contains(key)) throw Bad("JSON содержит неподдерживаемые настройки сервера.");
        }
        private static void Copy(Dictionary<string, object> from, Dictionary<string, string> to, string source, string target)
        {
            if (Value(from, source) != null) to[target] = Scalar(from, source, "");
        }
        private static string StringArray(object raw)
        {
            IList array = raw as IList;
            if (array == null) throw Bad("Неверный список параметров JSON.");
            List<string> result = new List<string>();
            foreach (object item in array)
            {
                if (!(item is string)) throw Bad("Неверный список параметров JSON.");
                ValidatePlain((string)item); result.Add((string)item);
            }
            return String.Join(",", result.ToArray());
        }

        public static string BuildConfig(ServerProfile server, int port)
        {
            if (server == null || server.Outbound == null) throw new ArgumentException("Сервер не выбран.");
            if (port < 1024 || port > 65535) throw new ArgumentOutOfRangeException("port");
            Dictionary<string, object> config = D(
                "log", D("access", "none", "error", "none", "loglevel", "none"),
                "inbounds", new object[] { D("tag", "local-http", "listen", "127.0.0.1", "port", port,
                    "protocol", "http", "settings", D("allowTransparent", false)) },
                "outbounds", new object[] { server.Outbound });
            return Json().Serialize(config);
        }

        private static ServerProfile ParseUri(string link, string protocol)
        {
            LinkParts u = SplitLink(link.Substring(link.IndexOf("://", StringComparison.Ordinal) + 3));
            string user = Decode(u.UserInfo);
            Dictionary<string, string> q = u.Query;
            CheckKeys(q, "type", "security", "encryption", "flow", "sni", "serverName", "peer", "fp", "fingerprint",
                "pbk", "publicKey", "sid", "shortId", "spx", "spiderX", "pqv", "alpn", "allowInsecure", "insecure",
                "host", "path", "serviceName", "authority", "mode", "headerType", "ed", "eh", "extra",
                "packetEncoding", "remarks", "group", "tag");
            string security = Get(q, "security", protocol == "trojan" ? "tls" : "none").ToLowerInvariant();
            Dictionary<string, object> stream = Stream(q, u.Host, security);
            Dictionary<string, object> settings;
            if (protocol == "vless")
            {
                ValidateId(user);
                string encryption = Get(q, "encryption", "none");
                if (encryption != "none") throw Bad("Этот вариант VLESS Encryption пока не поддерживается.");
                if (security == "none") throw Bad("VLESS без TLS или REALITY не поддерживается.");
                string flow = Get(q, "flow", "");
                if (flow != "" && flow != "xtls-rprx-vision" && flow != "xtls-rprx-vision-udp443")
                    throw Bad("Неподдерживаемый режим VLESS flow.");
                if (flow.Length > 0 && (string)stream["network"] != "tcp")
                    throw Bad("XTLS Vision требует транспорт TCP/RAW.");
                Dictionary<string, object> account = D("id", NormalizeId(user), "encryption", "none");
                if (flow.Length > 0) account["flow"] = flow;
                settings = D("vnext", new object[] { D("address", u.Host, "port", u.Port, "users", new object[] { account }) });
            }
            else
            {
                Require(user, "Не указан пароль Trojan.");
                if (security != "tls" && security != "reality") throw Bad("Trojan требует TLS или REALITY.");
                if (Get(q, "flow", "").Length > 0 || Get(q, "encryption", "").Length > 0)
                    throw Bad("Неподдерживаемые параметры Trojan.");
                settings = D("servers", new object[] { D("address", u.Host, "port", u.Port, "password", user) });
            }
            string packet = Get(q, "packetEncoding", "");
            if (packet.Length > 0) throw Bad("Параметр packetEncoding в этом режиме подключения не поддерживается.");
            return Profile(protocol, u.Name, u.Host, u.Port, settings, stream);
        }

        private static ServerProfile ParseVmess(string link)
        {
            string body = link.Substring(8);
            int hash = body.IndexOf('#');
            string fragment = hash < 0 ? "" : Decode(body.Substring(hash + 1));
            if (hash >= 0) body = body.Substring(0, hash);
            Dictionary<string, object> v;
            try { v = Json().DeserializeObject(Decode64(body)) as Dictionary<string, object>; }
            catch (Exception ex)
            {
                if (!(ex is ArgumentException) && !(ex is InvalidOperationException) && !(ex is FormatException)) throw;
                throw Bad("Повреждённая ссылка VMess.");
            }
            if (v == null) throw Bad("Повреждённая ссылка VMess.");
            Dictionary<string, string> f = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, object> item in v)
            {
                if (item.Value is IDictionary || item.Value is IList) throw Bad("Неподдерживаемые вложенные поля VMess.");
                f[item.Key] = Convert.ToString(item.Value, CultureInfo.InvariantCulture);
            }
            CheckKeys(f, "v", "ps", "add", "port", "id", "aid", "scy", "net", "type", "host", "path", "tls", "sni",
                "alpn", "fp", "allowInsecure", "insecure", "serviceName", "authority", "mode", "security");
            string host = NormalizeHost(Get(f, "add", ""));
            int port = Port(Get(f, "port", ""));
            string id = Get(f, "id", "");
            ValidateId(id);
            if (Get(f, "aid", "0") != "0") throw Bad("Устаревший VMess alterId не поддерживается. Нужен VMess AEAD.");
            string cipher = Get(f, "scy", Get(f, "security", "auto")).ToLowerInvariant();
            if (cipher != "auto" && cipher != "aes-128-gcm" && cipher != "chacha20-poly1305")
                throw Bad("Неподдерживаемое шифрование VMess.");
            Dictionary<string, string> q = new Dictionary<string, string>(f, StringComparer.OrdinalIgnoreCase);
            q["headerType"] = Get(f, "type", "none");
            q["type"] = Get(f, "net", "tcp");
            string net = q["type"].ToLowerInvariant();
            if (net == "grpc")
            {
                q["serviceName"] = Get(f, "serviceName", Get(f, "path", ""));
                q["mode"] = Get(f, "mode", Get(f, "type", "gun") == "multi" ? "multi" : "gun");
                q["headerType"] = "none";
            }
            string security = Get(f, "tls", "none").ToLowerInvariant();
            if (security == "") security = "none";
            Dictionary<string, object> stream = Stream(q, host, security);
            Dictionary<string, object> settings = D("vnext", new object[] { D("address", host, "port", port,
                "users", new object[] { D("id", NormalizeId(id), "alterId", 0, "security", cipher) }) });
            return Profile("vmess", fragment.Length > 0 ? fragment : Get(f, "ps", ""), host, port, settings, stream);
        }

        private static ServerProfile ParseShadowsocks(string link)
        {
            string body = link.Substring(5);
            int hash = body.IndexOf('#');
            string name = hash < 0 ? "" : Decode(body.Substring(hash + 1));
            if (hash >= 0) body = body.Substring(0, hash);
            if (body.IndexOf('@') < 0)
            {
                int query = body.IndexOf('?');
                if (query >= 0) throw Bad("Неподдерживаемый формат Shadowsocks.");
                body = Decode64(body);
            }
            LinkParts u = SplitLink(body);
            CheckKeys(u.Query, "plugin", "remarks", "group", "tag");
            if (Get(u.Query, "plugin", "").Length > 0) throw Bad("Shadowsocks с внешним плагином не поддерживается.");
            string credentials = Decode(u.UserInfo);
            if (credentials.IndexOf(':') < 0) credentials = Decode64(credentials);
            int colon = credentials.IndexOf(':');
            if (colon <= 0) throw Bad("Повреждены учётные данные Shadowsocks.");
            string method = credentials.Substring(0, colon).ToLowerInvariant();
            string password = credentials.Substring(colon + 1);
            Require(password, "Не указан пароль Shadowsocks.");
            if (method == "chacha20-poly1305" || method == "aead_chacha20_poly1305") method = "chacha20-ietf-poly1305";
            if (method == "aead_aes_128_gcm") method = "aes-128-gcm";
            if (method == "aead_aes_256_gcm") method = "aes-256-gcm";
            if (method != "aes-128-gcm" && method != "aes-256-gcm" && method != "chacha20-ietf-poly1305" &&
                method != "xchacha20-ietf-poly1305" && method != "2022-blake3-aes-128-gcm" &&
                method != "2022-blake3-aes-256-gcm" && method != "2022-blake3-chacha20-poly1305")
                throw Bad("Неподдерживаемое шифрование Shadowsocks.");
            if (method.StartsWith("2022-", StringComparison.Ordinal))
            {
                int keySize = method == "2022-blake3-aes-128-gcm" ? 16 : 32;
                foreach (string key in password.Split(':'))
                    if (Decode64Bytes(key).Length != keySize) throw Bad("Неверная длина ключа Shadowsocks 2022.");
            }
            return Profile("shadowsocks", name, u.Host, u.Port,
                D("servers", new object[] { D("address", u.Host, "port", u.Port, "method", method, "password", password) }),
                D("network", "tcp", "security", "none"));
        }

        private static Dictionary<string, object> Stream(Dictionary<string, string> q, string serverHost, string security)
        {
            foreach (string flag in new string[] { "allowInsecure", "insecure" })
            {
                string value = Get(q, flag, "").ToLowerInvariant();
                if (value != "" && value != "0" && value != "false")
                    throw Bad("Сервер требует отключить проверку сертификата. Такая ссылка не поддерживается.");
            }
            string network = Get(q, "type", "tcp").ToLowerInvariant();
            if (network == "raw") network = "tcp";
            if (network == "websocket") network = "ws";
            if (network == "splithttp") network = "xhttp";
            if (network != "tcp" && network != "ws" && network != "grpc" && network != "httpupgrade" && network != "xhttp")
                throw Bad("Неподдерживаемый транспорт сервера.");
            if (security != "none" && security != "tls" && security != "reality") throw Bad("Неподдерживаемая защита соединения.");
            Dictionary<string, object> stream = D("network", network, "security", security);
            string sni = Alias(q, "sni", "serverName", "peer");
            if (sni.Length > 0) sni = NormalizeHost(sni);
            string fingerprint = Alias(q, "fp", "fingerprint").ToLowerInvariant();
            string alpn = Get(q, "alpn", "");
            if (fingerprint.Length > 0 && !Regex.IsMatch(fingerprint, "^(chrome|firefox|safari|ios|android|edge|360|qq|random|randomized)$"))
                throw Bad("Неподдерживаемый TLS fingerprint.");
            if (security == "tls")
            {
                Dictionary<string, object> tls = D("serverName", sni.Length == 0 ? serverHost : sni, "allowInsecure", false);
                if (fingerprint.Length > 0) tls["fingerprint"] = fingerprint;
                if (alpn.Length > 0)
                {
                    string[] parts = alpn.Split(',');
                    foreach (string part in parts) if (part != "h2" && part != "http/1.1" && part != "h3") throw Bad("Неподдерживаемый ALPN.");
                    tls["alpn"] = parts;
                }
                stream["tlsSettings"] = tls;
            }
            if (security == "reality")
            {
                if (network != "tcp" && network != "grpc" && network != "xhttp") throw Bad("REALITY требует TCP, gRPC или XHTTP.");
                Require(sni, "Для REALITY не указано имя сервера (SNI).");
                string key = Alias(q, "pbk", "publicKey");
                if (!Regex.IsMatch(key, "^[A-Za-z0-9_-]{43}$") || Decode64Bytes(key).Length != 32) throw Bad("Неверный ключ REALITY.");
                string sid = Alias(q, "sid", "shortId").ToLowerInvariant();
                if (!Regex.IsMatch(sid, "^(?:[0-9a-f]{2}){0,8}$")) throw Bad("Неверный shortId REALITY.");
                Dictionary<string, object> reality = D("show", false, "serverName", sni, "fingerprint", fingerprint.Length == 0 ? "chrome" : fingerprint,
                    "publicKey", key, "shortId", sid, "spiderX", Alias(q, "spx", "spiderX"));
                string pqv = Get(q, "pqv", "");
                if (pqv.Length > 0)
                {
                    if (!Regex.IsMatch(pqv, "^[A-Za-z0-9_-]+$")) throw Bad("Неверный ключ проверки REALITY.");
                    reality["mldsa65Verify"] = pqv;
                }
                if (alpn.Length > 0) throw Bad("Ссылка REALITY содержит неподдерживаемый параметр ALPN.");
                stream["realitySettings"] = reality;
            }
            if (security != "reality" && (Alias(q, "pbk", "publicKey").Length > 0 || Alias(q, "sid", "shortId").Length > 0 ||
                Alias(q, "spx", "spiderX").Length > 0 || Get(q, "pqv", "").Length > 0)) throw Bad("Параметры REALITY указаны без REALITY.");
            if (security == "none" && (sni.Length > 0 || fingerprint.Length > 0 || alpn.Length > 0)) throw Bad("Параметры TLS указаны без TLS.");

            string host = Get(q, "host", "");
            string path = Get(q, "path", "/");
            ValidatePlain(host); ValidatePlain(path);
            string header = Get(q, "headerType", "none").ToLowerInvariant();
            if (network == "tcp")
            {
                if (header == "http")
                {
                    Dictionary<string, object> request = D("method", "GET", "version", "1.1", "path", new string[] { path });
                    if (host.Length > 0) request["headers"] = D("Host", host.Split(','));
                    stream["tcpSettings"] = D("header", D("type", "http", "request", request));
                }
                else if (header != "none" && header != "") throw Bad("Неподдерживаемый заголовок TCP.");
            }
            else if (header != "none" && header != "") throw Bad("Заголовок этого транспорта не поддерживается.");
            if (network == "ws" || network == "httpupgrade")
            {
                Dictionary<string, object> transport = D("path", path);
                if (host.Length > 0)
                {
                    transport["host"] = host;
                }
                string ed = Get(q, "ed", "");
                if (ed.Length > 0)
                {
                    int number;
                    if (!Int32.TryParse(ed, out number) || number < 0 || number > 65536) throw Bad("Неверный параметр ранней передачи данных.");
                    if (Regex.IsMatch(path, "(?:[?&])ed=")) throw Bad("В ссылке повторяется настройка ранних данных.");
                    transport["path"] = path + (path.Contains("?") ? "&" : "?") + "ed=" + number.ToString(CultureInfo.InvariantCulture);
                }
                string eh = Get(q, "eh", "");
                if (eh.Length > 0)
                {
                    if (!eh.Equals("Sec-WebSocket-Protocol", StringComparison.OrdinalIgnoreCase))
                        throw Bad("Нестандартный заголовок ранних данных не поддерживается ядром Xray.");
                }
                stream[network == "ws" ? "wsSettings" : "httpupgradeSettings"] = transport;
            }
            else if (Get(q, "ed", "").Length > 0 || Get(q, "eh", "").Length > 0) throw Bad("Параметр ранних данных не подходит к транспорту.");
            if (network == "grpc")
            {
                string mode = Get(q, "mode", "gun");
                if (mode != "gun" && mode != "multi" && mode != "") throw Bad("Неподдерживаемый режим gRPC.");
                Dictionary<string, object> grpc = D("serviceName", Get(q, "serviceName", ""), "multiMode", mode == "multi");
                string authority = Get(q, "authority", "");
                ValidatePlain(authority);
                if (authority.Length > 0) grpc["authority"] = authority;
                stream["grpcSettings"] = grpc;
            }
            if (network == "xhttp")
            {
                string mode = Get(q, "mode", "auto");
                if (mode != "auto" && mode != "packet-up" && mode != "stream-up" && mode != "stream-one") throw Bad("Неподдерживаемый режим XHTTP.");
                Dictionary<string, object> xhttp = D("path", path, "mode", mode);
                if (host.Length > 0) xhttp["host"] = host;
                string extra = Get(q, "extra", "");
                if (extra.Length > 0) xhttp["extra"] = XhttpExtra(extra);
                stream["xhttpSettings"] = xhttp;
            }
            else if (Get(q, "extra", "").Length > 0) throw Bad("Параметр extra не подходит к транспорту.");
            return stream;
        }

        // Import only client transport tuning. Never copy supplied listeners,
        // API, routing, files, dialer chains or another embedded StreamConfig.
        private static Dictionary<string, object> XhttpExtra(string text)
        {
            if(text.Length > 32768) throw Bad("Слишком большие параметры XHTTP.");
            Dictionary<string, object> input;
            try { input = Object(Json().DeserializeObject(text)); }
            catch(ArgumentException) { throw Bad("Повреждённые параметры XHTTP."); }
            catch(InvalidOperationException) { throw Bad("Повреждённые параметры XHTTP."); }
            Fields(input,"host","path","mode","xmux","noGRPCHeader","noSSEHeader","xPaddingBytes","scMaxEachPostBytes","scMinPostsIntervalMs","scMaxBufferedPosts","scStreamUpServerSecs","serverMaxHeaderBytes","headers");
            var output = D();
            foreach(var item in input)
            {
                if(item.Key == "host" || item.Key == "path" || item.Key == "mode")
                {
                    // Xray replaces these extra values with the outer fields.
                    // Validate and omit them to preserve the same precedence.
                    if(!(item.Value is string) || ((string)item.Value).Length > 8192) throw Bad("Неверный параметр XHTTP.");
                    ValidatePlain((string)item.Value);
                }
                else if(item.Key == "xmux")
                {
                    var mux = Object(item.Value); Fields(mux,"maxConcurrency","maxConnections","cMaxReuseTimes","hMaxRequestTimes","hMaxReusableSecs","hKeepAlivePeriod");
                    var clean = D();
                    foreach(var setting in mux)
                    {
                        object value = XhttpRange(setting.Value, setting.Key == "maxConnections" || setting.Key == "maxConcurrency" ? 1024 : Int32.MaxValue);
                        if(setting.Key == "hKeepAlivePeriod") {
                            if(Convert.ToString(value,CultureInfo.InvariantCulture).IndexOf('-') >= 0) throw Bad("Для keep-alive XHTTP нужно одно число.");
                            value = Convert.ToInt32(value,CultureInfo.InvariantCulture);
                        }
                        clean[setting.Key] = value;
                    }
                    output[item.Key] = clean;
                }
                else if(item.Key == "headers")
                {
                    var headers = Object(item.Value); if(headers.Count > 32) throw Bad("Слишком много заголовков XHTTP.");
                    var clean = D();
                    foreach(var header in headers)
                    {
                        if(!Regex.IsMatch(header.Key,"^[A-Za-z0-9!#$%&'*+.^_`|~-]{1,128}$") || !(header.Value is string)) throw Bad("Неверные заголовки XHTTP.");
                        string value = (string)header.Value; ValidatePlain(value);
                        if(value.Length > 8192) throw Bad("Слишком большой заголовок XHTTP.");
                        clean[header.Key] = value;
                    }
                    output[item.Key] = clean;
                }
                else if(item.Key == "noGRPCHeader" || item.Key == "noSSEHeader")
                {
                    if(!(item.Value is bool)) throw Bad("Неверный переключатель XHTTP.");
                    output[item.Key] = item.Value;
                }
                else
                {
                    int maximum = item.Key == "xPaddingBytes" ? 1048576 : item.Key == "scMaxEachPostBytes" ? 67108864 :
                        item.Key == "serverMaxHeaderBytes" ? 262144 : item.Key == "scMaxBufferedPosts" ? 1024 : Int32.MaxValue;
                    object value = XhttpRange(item.Value, maximum);
                    if(item.Key == "scMaxBufferedPosts" || item.Key == "serverMaxHeaderBytes") {
                        if(Convert.ToString(value,CultureInfo.InvariantCulture).IndexOf('-') >= 0) throw Bad("Для этого параметра XHTTP нужно одно число.");
                        value = Convert.ToInt32(value,CultureInfo.InvariantCulture);
                    }
                    output[item.Key] = value;
                }
            }
            return output;
        }
        private static object XhttpRange(object raw, int maximum)
        {
            if(!(raw is int) && !(raw is long) && !(raw is string)) throw Bad("Неверный числовой параметр XHTTP.");
            string value = Convert.ToString(raw,CultureInfo.InvariantCulture);
            if(!Regex.IsMatch(value,"^[0-9]{1,10}(?:-[0-9]{1,10})?$")) throw Bad("Неверный диапазон XHTTP.");
            string[] parts = value.Split('-'); int first,last;
            if(!Int32.TryParse(parts[0],NumberStyles.None,CultureInfo.InvariantCulture,out first) || first > maximum) throw Bad("Слишком большой параметр XHTTP.");
            last = first;
            if(parts.Length == 2 && (!Int32.TryParse(parts[1],NumberStyles.None,CultureInfo.InvariantCulture,out last) || last < first || last > maximum))
                throw Bad("Неверный диапазон XHTTP.");
            return raw is string ? (object)value : first;
        }

        private sealed class LinkParts
        {
            public string UserInfo, Host, Name;
            public int Port;
            public Dictionary<string, string> Query;
        }

        private static LinkParts SplitLink(string body)
        {
            LinkParts parts = new LinkParts();
            int hash = body.IndexOf('#');
            parts.Name = hash < 0 ? "" : Decode(body.Substring(hash + 1));
            if (hash >= 0) body = body.Substring(0, hash);
            int query = body.IndexOf('?');
            parts.Query = Query(query < 0 ? "" : body.Substring(query + 1));
            if (query >= 0) body = body.Substring(0, query);
            if (body.EndsWith("/", StringComparison.Ordinal)) body = body.Substring(0, body.Length - 1);
            int at = body.LastIndexOf('@');
            if (at <= 0) throw Bad("В ссылке не указаны учётные данные сервера.");
            parts.UserInfo = body.Substring(0, at);
            string endpoint = body.Substring(at + 1);
            int colon = endpoint.LastIndexOf(':');
            if (colon <= 0) throw Bad("В ссылке не указан порт сервера.");
            string host = endpoint.Substring(0, colon);
            if (host.IndexOf(':') >= 0 && !(host.StartsWith("[") && host.EndsWith("]"))) throw Bad("Адрес IPv6 должен быть в квадратных скобках.");
            parts.Host = NormalizeHost(Decode(host));
            parts.Port = Port(endpoint.Substring(colon + 1));
            return parts;
        }

        private static Dictionary<string, string> Query(string value)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string entry in value.Split('&'))
            {
                if (entry.Length == 0) continue;
                int eq = entry.IndexOf('=');
                string key = Decode(eq < 0 ? entry : entry.Substring(0, eq));
                string val = Decode(eq < 0 ? "" : entry.Substring(eq + 1));
                if (result.ContainsKey(key)) throw Bad("В ссылке повторяются параметры.");
                result.Add(key, val);
            }
            return result;
        }

        private static string Decode(string value)
        {
            if (Regex.IsMatch(value, "%(?![0-9a-fA-F]{2})")) throw Bad("Неверное кодирование ссылки.");
            // '+' is a literal in RFC 3986 connection links, not an HTML form space.
            string decoded = Uri.UnescapeDataString(value);
            ValidatePlain(decoded);
            return decoded;
        }

        private static string Decode64(string value)
        {
            try { return new UTF8Encoding(false, true).GetString(Decode64Bytes(value)); }
            catch (DecoderFallbackException) { throw Bad("Неверное кодирование текста подписки."); }
        }

        private static byte[] Decode64Bytes(string value)
        {
            value = Regex.Replace(value, "\\s", "").Replace('-', '+').Replace('_', '/');
            int rem = value.Length % 4;
            if (rem == 1) throw Bad("Неверное кодирование Base64.");
            if (rem > 0) value = value.PadRight(value.Length + 4 - rem, '=');
            try { return Convert.FromBase64String(value); }
            catch (FormatException) { throw Bad("Неверное кодирование Base64."); }
        }

        private static string NormalizeHost(string host)
        {
            host = host.Trim();
            if (host.StartsWith("[") && host.EndsWith("]")) host = host.Substring(1, host.Length - 2);
            Require(host, "Не указан адрес сервера.");
            IPAddress address;
            if (IPAddress.TryParse(host, out address)) return address.ToString();
            if (host.IndexOfAny(new char[] { '/', '\\', '@', ':', '?', '#', '%', ' ' }) >= 0) throw Bad("Неверный адрес сервера.");
            string ascii;
            try { ascii = new IdnMapping().GetAscii(host.TrimEnd('.')).ToLowerInvariant(); }
            catch (ArgumentException) { throw Bad("Неверное доменное имя сервера."); }
            if (ascii.Length > 253 || !Regex.IsMatch(ascii, "^(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)(?:\\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)*$"))
                throw Bad("Неверное доменное имя сервера.");
            return ascii;
        }

        private static int Port(string value)
        {
            int port;
            if (!Int32.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port < 1 || port > 65535)
                throw Bad("Неверный порт сервера.");
            return port;
        }

        private static void ValidateId(string id)
        {
            Guid parsed;
            if (Compat.TryGuid(id, out parsed)) return;
            if (Compat.IsBlank(id) || Encoding.UTF8.GetByteCount(id) >= 30) throw Bad("Неверный идентификатор VLESS/VMess.");
            ValidatePlain(id);
        }
        private static string NormalizeId(string id) { Guid parsed; return Compat.TryGuid(id, out parsed) ? parsed.ToString("D") : id; }
        private static void ValidatePlain(string value)
        {
            foreach (char c in value) if (Char.IsControl(c)) throw Bad("Ссылка содержит недопустимые управляющие символы.");
        }
        private static void Require(string value, string message) { if (Compat.IsBlank(value)) throw Bad(message); }
        private static FormatException Bad(string message) { return new FormatException(message); }
        private static string Get(Dictionary<string, string> q, string key, string fallback)
        {
            string value;
            return q.TryGetValue(key, out value) ? value : fallback;
        }
        private static string Alias(Dictionary<string, string> q, params string[] keys)
        {
            string found = "";
            foreach (string key in keys)
            {
                string value = Get(q, key, "");
                if (found.Length > 0 && value.Length > 0 && found != value) throw Bad("Противоречащие параметры ссылки.");
                if (value.Length > 0) found = value;
            }
            return found;
        }
        private static void CheckKeys(Dictionary<string, string> q, params string[] supported)
        {
            HashSet<string> keys = new HashSet<string>(supported, StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> p in q)
                if (p.Value.Length > 0 && !keys.Contains(p.Key)) throw Bad("Ссылка содержит неподдерживаемые параметры.");
        }
        private static Dictionary<string, object> D(params object[] pairs)
        {
            Dictionary<string, object> d = new Dictionary<string, object>(StringComparer.Ordinal);
            for (int i = 0; i < pairs.Length; i += 2) d.Add((string)pairs[i], pairs[i + 1]);
            return d;
        }

        private static ServerProfile Profile(string protocol, string name, string host, int port,
            Dictionary<string, object> settings, Dictionary<string, object> stream)
        {
            Dictionary<string, object> outbound = D("tag", "proxy", "protocol", protocol, "settings", settings, "streamSettings", stream);
            string canonical = Json().Serialize(Canonical(outbound));
            string id;
            using (SHA256 sha = SHA256.Create()) id = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(canonical))).Replace("-", "").ToLowerInvariant();
            StringBuilder clean = new StringBuilder();
            foreach (char c in name ?? "")
                if (!Char.IsControl(c) && !(c >= '\u202a' && c <= '\u202e') && !(c >= '\u2066' && c <= '\u2069')) clean.Append(c);
            name = clean.ToString().Trim();
            if (name.Length > 100) name = name.Substring(0, Char.IsHighSurrogate(name[99]) ? 99 : 100);
            if (name.Length == 0) name = host + ":" + port.ToString(CultureInfo.InvariantCulture);
            return new ServerProfile { Id = id, Name = name, Protocol = protocol, Outbound = outbound };
        }

        private static object Canonical(object value)
        {
            IDictionary<string, object> dict = value as IDictionary<string, object>;
            if (dict != null)
            {
                SortedDictionary<string, object> sorted = new SortedDictionary<string, object>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, object> pair in dict) sorted[pair.Key] = Canonical(pair.Value);
                return sorted;
            }
            IEnumerable array = value as IEnumerable;
            if (array != null && !(value is string))
            {
                List<object> items = new List<object>();
                foreach (object item in array) items.Add(Canonical(item));
                return items;
            }
            return value;
        }
    }
}

