using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using SimpleVPN;

internal static class XhttpExtraTests
{
    static int checks;
    const string Base = "vless://00112233-4455-6677-8899-aabbccddeeff@server.invalid:443?security=reality&type=xhttp&sni=example.invalid&pbk=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA&path=%2Fouter&mode=packet-up";
    static void Assert(bool value,string name) { if(!value) throw new Exception(name); checks++; }
    static ServerProfile Parse(string extra) { return SubscriptionParser.Parse(Base+"&extra="+Uri.EscapeDataString(extra)+"#Synthetic")[0]; }
    static void Reject(string extra)
    { bool rejected=false; try { Parse(extra); } catch(FormatException) { rejected=true; } Assert(rejected,"Unsupported or unsafe XHTTP field was accepted"); }
    static void CoreCheck(ServerProfile server,string executable)
    {
        string config=ConnectionOptions.BuildConfig(server,23001,new AppSettings());
        var start=new ProcessStartInfo(executable,"run -test -config stdin:") { UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=Path.GetDirectoryName(Path.GetFullPath(executable)) };
        using(var child=Process.Start(start)) {
            try {
                byte[] bytes=Encoding.UTF8.GetBytes(config); child.StandardInput.BaseStream.Write(bytes,0,bytes.Length); child.StandardInput.Close();
                string output=child.StandardOutput.ReadToEnd()+child.StandardError.ReadToEnd();
                Assert(child.WaitForExit(10000)&&child.ExitCode==0,"Synthetic XHTTP extra rejected by bundled core: "+output);
            } finally { try { if(!child.HasExited) child.Kill(); } catch { } child.StandardOutput.Close(); child.StandardError.Close(); }
        }
    }
    public static int Main(string[] args)
    {
        try {
            string tuning="{\"path\":\"/ignored\",\"noGRPCHeader\":true,\"xPaddingBytes\":\"100-1000\",\"scMaxEachPostBytes\":1000000,\"scMinPostsIntervalMs\":30,\"xmux\":{\"cMaxReuseTimes\":0,\"maxConcurrency\":\"16-32\",\"maxConnections\":0,\"hKeepAlivePeriod\":0,\"hMaxRequestTimes\":\"600-900\",\"hMaxReusableSecs\":\"1800-3000\"}}";
            ServerProfile profile=Parse(tuning);
            var stream=(Dictionary<string,object>)profile.Outbound["streamSettings"];
            var transport=(Dictionary<string,object>)stream["xhttpSettings"];
            var extra=(Dictionary<string,object>)transport["extra"];
            Assert((string)transport["path"]=="/outer"&&!extra.ContainsKey("path"),"Extra path overrode outer transport path");
            Assert((string)extra["xPaddingBytes"]=="100-1000"&&(bool)extra["noGRPCHeader"],"Provider transport tuning lost");
            Assert(((Dictionary<string,object>)extra["xmux"]).ContainsKey("hMaxReusableSecs"),"XHTTP connection reuse settings lost");
            string raw=LocalStore.Json().Serialize(new Dictionary<string,object> { {"remarks","Fixture"},{"outbounds",new object[]{profile.Outbound}} });
            var roundTrip=SubscriptionParser.Parse(raw);
            Assert(roundTrip.Count==1&&roundTrip[0].Id==profile.Id,"JSON transport round-trip changed normalized profile");
            int skipped;
            var mixed=SubscriptionParser.Parse("["+raw+",{\"outbounds\":[{\"protocol\":\"unsupported\"}]}]",out skipped);
            Assert(mixed.Count==1&&skipped==1,"Unsupported JSON item disappeared without a count");
            mixed=SubscriptionParser.Parse(Base+"#Known\nunsupported://opaque",out skipped);
            Assert(mixed.Count==1&&skipped==1,"Unsupported link disappeared without a count");
            if(args.Length>0) { CoreCheck(profile,args[0]); CoreCheck(Parse("{\"headers\":{\"X-Test\":\"safe\"},\"noSSEHeader\":true}"),args[0]); }
            Reject("{\"downloadSettings\":{\"sockopt\":{\"dialerProxy\":\"outside\"}}}");
            Reject("{\"api\":{}}"); Reject("{\"headers\":{\"X-Test\":\"line\\r\\ninjection\"}}");
            Reject("{\"xPaddingBytes\":\"1000-100\"}"); Reject("{\"xPaddingBytes\":\"99999999999\"}"); Reject("{\"xPaddingBytes\":1073741824}");
            Reject("{\"xmux\":{\"maxConcurrency\":4096}}"); Reject("{\"xmux\":{\"hKeepAlivePeriod\":\"1-2\"}}");
            Reject("{\"noGRPCHeader\":\"true\"}"); Reject("{\"xmux\":{\"unknown\":0}}");
            Reject("{\"scMaxBufferedPosts\":1000000}"); Reject("{\"serverMaxHeaderBytes\":2147483647}");
            Console.WriteLine("XHTTP extra checks PASS: "+checks+"; synthetic range validation, JSON round-trip, remote-field isolation and optional bundled-core configs."); return 0;
        } catch(Exception error) { Console.WriteLine("FAIL "+error.GetType().Name+": "+error.Message); return 1; }
    }
}
