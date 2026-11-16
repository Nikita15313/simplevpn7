using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using SimpleVPN;

class OptionsTests {
 static int checks;
 static void Assert(bool ok,string message){if(!ok)throw new Exception(message);checks++;}
 static int Main(string[] args) {
  try {
   var server=SubscriptionParser.Parse("vless://00112233-4455-6677-8899-aabbccddeeff@server.invalid:443?security=reality&type=tcp&sni=example.com&pbk=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA#Fixture")[0];
   string original=LocalStore.Json().Serialize(server.Outbound);
   foreach(string dns in new[]{"system","cloudflare","google"})foreach(string mode in new[]{"all","bypass-private"}) {
    var preferences=new AppSettings{DnsMode=dns,RoutingMode=mode,BlockAds=true,DirectDomains="example.org\nпример.рф",ProxyDomains="video.example.org"};
    string config=ConnectionOptions.BuildConfig(server,23001,preferences);
    var parsed=LocalStore.Json().Deserialize<Dictionary<string,object>>(config);
    var routing=(Dictionary<string,object>)parsed["routing"];
    var rules=(ArrayList)routing["rules"];
    Assert((string)((Dictionary<string,object>)rules[0])["outboundTag"]=="proxy","Explicit proxy priority lost");
    Assert((string)((Dictionary<string,object>)rules[1])["outboundTag"]=="proxy","Explicit proxy priority lost");
    Assert((string)((Dictionary<string,object>)rules[2])["outboundTag"]=="direct","Explicit direct rule lost");
    Assert(config.Contains("xn--e1afmkfd.xn--p1ai"),"IDN not normalized");
    Assert(parsed.ContainsKey("dns")== (dns!="system"),"DNS preference inactive");
    if(args.Length>0) {
     var start=new ProcessStartInfo(args[0],"run -test -config stdin:"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=Path.GetDirectoryName(Path.GetFullPath(args[0]))};
     using(var p=Process.Start(start)){byte[] bytes=Encoding.UTF8.GetBytes(config);p.StandardInput.BaseStream.Write(bytes,0,bytes.Length);p.StandardInput.Close();string result=p.StandardOutput.ReadToEnd()+p.StandardError.ReadToEnd();p.WaitForExit();Assert(p.ExitCode==0,"Core rejected options: "+result);}
    }
   }
   Assert(LocalStore.Json().Serialize(server.Outbound)==original,"Settings changed subscription source object");
   string forcedHealth=ConnectionOptions.BuildConfig(server,23001,new AppSettings { DirectDomains="www.youtube.com\nwww.microsoft.com\nwww.gstatic.com" });
   var forcedConfig=LocalStore.Json().Deserialize<Dictionary<string,object>>(forcedHealth);
   var forcedRules=(ArrayList)((Dictionary<string,object>)forcedConfig["routing"])["rules"];
   var forcedFirst=(Dictionary<string,object>)forcedRules[0];
   Assert((string)forcedFirst["outboundTag"]=="proxy"&&LocalStore.Json().Serialize(forcedFirst["domain"]).Contains("full:www.youtube.com"),"YouTube fallback could bypass broken proxy through direct-domain preference");
   foreach(string invalid in new[]{"https://example.com/path","example.com:443","x.com\"},{\"evil\":true","*","localhost"}) {
    bool failed=false;try{ConnectionOptions.Domains(invalid);}catch(ArgumentException){failed=true;}Assert(failed,"Invalid domain allowed");
   }
   bool rejected=false;try{ConnectionOptions.Validate(new AppSettings{ConnectionTimeoutSeconds=60});}catch(ArgumentException){rejected=true;}Assert(rejected,"Unbounded timeout accepted");
   Console.WriteLine("PASS options: "+checks+" assertions; six real core configurations; no network or system changes.");return 0;
  }catch(Exception e){Console.WriteLine("FAIL "+e.Message);return 1;}
 }
}
