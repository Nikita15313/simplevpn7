using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;

namespace SimpleVPN {
 public static class ConnectionOptions {
  public static AppSettings Validate(AppSettings settings) {
   if(settings==null)throw new ArgumentNullException("settings");
   var s=LocalStore.Json().Deserialize<AppSettings>(LocalStore.Json().Serialize(settings));
   if(s.UpdateIntervalHours<AppSettings.MinimumUpdateIntervalHours||s.UpdateIntervalHours>AppSettings.MaximumUpdateIntervalHours)throw new ArgumentException("Обновление подписки: от 1 до 168 часов.");
   if(s.ConnectionTimeoutSeconds<2||s.ConnectionTimeoutSeconds>15)throw new ArgumentException("Ожидание ответа: от 2 до 15 секунд.");
   if(s.RoutingMode!="all"&&s.RoutingMode!="bypass-private")throw new ArgumentException("Выберите режим маршрутизации.");
   if(s.DnsMode!="system"&&s.DnsMode!="cloudflare"&&s.DnsMode!="google")throw new ArgumentException("Выберите DNS.");
   if(s.Theme!="dark"&&s.Theme!="light")s.Theme="dark";
   Network.SubscriptionUri(s.ConnectionTestUrl);
   Domains(s.DirectDomains); Domains(s.ProxyDomains);
   return s;
  }
  public static string[] Domains(string text) {
   var result=new List<string>();
   if(String.IsNullOrEmpty(text))return result.ToArray();
   if(text.Length>8192)throw new ArgumentException("Слишком большой список доменов.");
   foreach(string entry in text.Split(new[]{'\r','\n',',',';'},StringSplitOptions.RemoveEmptyEntries)) {
    string name=entry.Trim().TrimEnd('.');
    if(name.StartsWith("*."))name=name.Substring(2);
    try{name=new IdnMapping().GetAscii(name).ToLowerInvariant();}catch{throw new ArgumentException("Проверьте домены: укажите имена сайтов без https:// и путей.");}
    if(Uri.CheckHostName(name)!=UriHostNameType.Dns||name.IndexOf('.')<0||name.Length>253)throw new ArgumentException("Укажите домен сайта, например example.com, без https:// и путей.");
    if(!result.Contains("domain:"+name))result.Add("domain:"+name);
    if(result.Count>128)throw new ArgumentException("В одном списке допускается до 128 доменов.");
   }
   return result.ToArray();
  }
  static Dictionary<string,object> D(params object[] pairs) {
   var d=new Dictionary<string,object>();for(int i=0;i<pairs.Length;i+=2)d[(string)pairs[i]]=pairs[i+1];return d;
  }
  public static string BuildConfig(ServerProfile server,int port,AppSettings preferences) {
   AppSettings s=Validate(preferences);
   var config=LocalStore.Json().Deserialize<Dictionary<string,object>>(SubscriptionParser.BuildConfig(server,port));
   var outbound=LocalStore.Json().Deserialize<Dictionary<string,object>>(LocalStore.Json().Serialize(server.Outbound));
   outbound["tag"]="proxy";
   config["outbounds"]=new object[]{outbound,D("tag","direct","protocol","freedom","settings",D()),D("tag","block","protocol","blackhole","settings",D())};
   var rules=new List<object>();
   // A direct-domain exception must never make a broken proxy pass its own check.
   Uri probe=Network.SubscriptionUri(s.ConnectionTestUrl);
   string probeHost=probe.DnsSafeHost;
   IPAddress probeIp;
   rules.Add(D("type","field","domain",new[]{"full:www.microsoft.com","full:www.youtube.com","full:"+new IdnMapping().GetAscii(probeHost)},"outboundTag","proxy"));
   if(IPAddress.TryParse(probeHost,out probeIp))rules.Add(D("type","field","ip",new[]{probeHost},"outboundTag","proxy"));
   string[] via=Domains(s.ProxyDomains),direct=Domains(s.DirectDomains);
   if(via.Length>0)rules.Add(D("type","field","domain",via,"outboundTag","proxy"));
   if(direct.Length>0)rules.Add(D("type","field","domain",direct,"outboundTag","direct"));
   if(s.BlockAds)rules.Add(D("type","field","domain",new[]{"geosite:category-ads-all"},"outboundTag","block"));
   if(s.RoutingMode=="bypass-private")rules.Add(D("type","field","ip",new[]{"geoip:private"},"outboundTag","direct"));
   config["routing"]=D("domainStrategy",s.RoutingMode=="bypass-private"?"IPIfNonMatch":"AsIs","rules",rules.ToArray());
   if(s.DnsMode!="system") {
    config["dns"]=D("servers",new[]{s.DnsMode=="google"?"https://8.8.8.8/dns-query":"https://1.1.1.1/dns-query"},"queryStrategy","UseIPv4");
    // Xray resolves requested target names using its selected resolver before dialing.
    outbound["targetStrategy"]="UseIPv4";
   }
   return LocalStore.Json().Serialize(config);
  }
 }
}
