using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using SimpleVPN;

// Explicit loopback proxy only. Never instantiates ProxyManager/AppController.
// Account data is decrypted in memory; output contains aggregate results only.
class LiveAcceptance {
 static int Main(string[] args) {
  try {
   byte[] bytes=ProtectedData.Unprotect(File.ReadAllBytes(args[0]),null,DataProtectionScope.CurrentUser);
   string url;try{url=Encoding.UTF8.GetString(bytes);}finally{Array.Clear(bytes,0,bytes.Length);}
   using(var cancel=new OperationCancel()) {
    var download=Network.DownloadSubscription(url,0,cancel);
    var list=SubscriptionParser.Parse(download.Body);
    Console.WriteLine("Subscription: "+list.Count+" profiles; metadata title="+(!String.IsNullOrEmpty(download.Metadata.Title))+"; traffic="+download.Metadata.TotalBytes.HasValue+"; expiry="+download.Metadata.ExpireUnixSeconds.HasValue+"; interval="+download.Metadata.UpdateIntervalHours.HasValue);
    var server=list.Find(s=>s.Name.IndexOf("Нидерланд",StringComparison.OrdinalIgnoreCase)>=0) ?? list[0];
    string directIp=Network.Download("https://api.ipify.org/",0,cancel).Trim();
    IPAddress ip; if(!IPAddress.TryParse(directIp,out ip))throw new Exception("IP reference did not return an IP address");
    foreach(string dns in new[]{"system","cloudflare","google"}) {
     using(var core=new CoreProcess(args[1])) {
      var timer=Stopwatch.StartNew();
      var settings=new AppSettings{DnsMode=dns,BlockAds=dns!="system",RoutingMode=dns=="system"?"all":"bypass-private"};
      core.Start(server,cancel,settings);
      long started=timer.ElapsedMilliseconds;
      if(!Network.Check(core.Port,settings.ConnectionTestUrl,6,cancel))throw new Exception("Connectivity test failed for "+dns);
      Console.WriteLine("Connect "+dns+": core="+started+"ms; verified="+timer.ElapsedMilliseconds+"ms");
      foreach(string site in new[]{"https://www.google.com/","https://www.youtube.com/","https://web.telegram.org/"}) {
       int ms=Network.Probe(core.Port,site,6000,cancel);
       Console.WriteLine("HTTPS "+new Uri(site).Host+" DNS="+dns+": "+(ms>=0?"PASS "+ms+"ms":"FAIL"));
       if(ms<0)throw new Exception("HTTPS site check failed");
      }
      string throughIp=Network.Download("https://api.ipify.org/",core.Port,cancel).Trim();
      if(!IPAddress.TryParse(throughIp,out ip)||throughIp==directIp)throw new Exception("Exit IP did not differ");
      Console.WriteLine("Exit IP differs: PASS (addresses omitted)");
      timer=Stopwatch.StartNew();core.Dispose();Console.WriteLine("Core stop: "+timer.ElapsedMilliseconds+"ms");
     }
    }
   }
   Console.WriteLine("PASS: current-host explicit-proxy requests only; no OS proxy, route, DNS or GUI changes."); return 0;
  }catch(Exception e){Console.WriteLine("FAIL "+e.GetType().Name);return 1;}
 }
}
