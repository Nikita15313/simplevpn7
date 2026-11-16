using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using SimpleVPN;

internal static class ConfigSourceTests
{
    const string First = "vless://00112233-4455-6677-8899-aabbccddeeff@first.invalid:443?security=reality&type=tcp&sni=example.invalid&pbk=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA#First";
    const string Second = "vless://10112233-4455-6677-8899-aabbccddeeff@second.invalid:443?security=reality&type=tcp&sni=example.invalid&pbk=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA#Second";
    static int checks, downloads;
    static void Assert(bool value, string name) { if(!value) throw new Exception(name); checks++; }
    sealed class FakeProxy : IProxyPlatform
    {
        public ProxySnapshot Capture() { throw new Exception("Unexpected proxy capture"); }
        public void Apply(string endpoint) { throw new Exception("Unexpected proxy mutation"); }
        public void Restore(ProxySnapshot snapshot) { throw new Exception("Unexpected proxy restore"); }
        public bool Owns(string endpoint) { return false; }
    }
    static AppController Controller(LocalStore store)
    {
        return new AppController(store,new FakeProxy(),delegate { downloads++; return First+"\n"+Second; },delegate { throw new Exception("Unexpected HTTP check"); },false,false);
    }
    static Exception Wait(WorkTicket ticket)
    {
        using(var done=new ManualResetEvent(false)) {
            Exception failed=null;
            ticket.OnCompleted(delegate(Exception error) { failed=error; done.Set(); });
            Assert(done.WaitOne(10000,false),"operation timeout"); return failed;
        }
    }
    static void Success(WorkTicket ticket) { Assert(Wait(ticket)==null,"operation failed"); }
    public static int Main(string[] args)
    {
        AppController controller=null;
        try {
            if(args.Length!=1) throw new ArgumentException("Scratch root required");
            string root=Path.Combine(args[0],"sources-"+Guid.NewGuid().ToString("N"));
            var store=new LocalStore(root);
            var second=SubscriptionParser.Parse(Second)[0];
            store.Save("settings.dpapi",new AppSettings { SubscriptionUrl="https://subscription.invalid/test",SelectedServerId=second.Id,
                LastUpdatedUtc=DateTime.UtcNow, Metadata=new SubscriptionMetadata {Title="Legacy",TotalBytes=500L*1024*1024*1024} });
            store.Save("servers.dpapi",First+"\n"+Second);
            controller=Controller(store); Success(controller.InitializeAsync());
            Assert(controller.Sources.Count==1&&controller.Servers.Count==2,"legacy migration counts");
            Assert(controller.Metadata.Title=="Legacy"&&controller.Settings.SelectedServerId==second.Id,"legacy metadata selection preserved");
            Assert(downloads==0,"cached fixture downloaded unexpectedly");
            string legacy=controller.Sources[0].Id;
            var snapshot=controller.Sources; snapshot.Clear();
            Assert(controller.Sources.Count==1,"source snapshot mutated controller");
            Success(controller.RemoveServerAsync(second.Id));
            Assert(controller.Servers.Count==1&&controller.Sources[0].HiddenServerIds.Contains(second.Id),"server deletion not persisted");
            Success(controller.RefreshAsync());
            Assert(controller.Servers.Count==1&&downloads==1,"refresh resurrected hidden server");
            Success(controller.AddSourceAsync("Local",Second));
            string local=controller.Settings.SelectedSourceId;
            Assert(controller.Sources.Count==2&&local!=legacy&&controller.Servers.Count==1,"manual import did not add independent source");
            Assert(controller.Settings.SubscriptionUrl==""&&controller.Metadata.TotalBytes==null,"local source leaked legacy metadata");
            Success(controller.SelectSourceAsync(legacy));
            Assert(controller.Servers.Count==1&&controller.Metadata.Title=="Legacy","source switching failed");
            Success(controller.RestoreServersAsync());
            Assert(controller.Servers.Count==2,"hidden servers not restored");
            Success(controller.AddSourceAsync("Other","https://second-subscription.invalid/test"));
            Assert(controller.Sources.Count==3&&downloads==2&&controller.Servers.Count==2,"URL import failed");
            string third=controller.Settings.SelectedSourceId;
            Assert(Wait(controller.AddSourceAsync("Bad","not a config"))!=null,"invalid config accepted");
            Assert(controller.Sources.Count==3&&controller.Settings.SelectedSourceId==third,"invalid config changed selected group");
            Success(controller.AddSourceAsync("Duplicate","https://second-subscription.invalid/test"));
            Assert(controller.Sources.Count==3&&downloads==2,"duplicate subscription added/downloaded");
            controller.Shutdown(); controller=Controller(store); Success(controller.InitializeAsync());
            Assert(controller.Sources.Count==3&&controller.Settings.SelectedSourceId==third,"library restart lost sources or selection");
            Success(controller.DeleteSourceAsync(legacy)); Success(controller.DeleteSourceAsync(local)); Success(controller.DeleteSourceAsync(third));
            Assert(controller.Sources.Count==0&&controller.Servers.Count==0&&controller.Settings.SubscriptionUrl=="","deleting all sources left stale state");
            controller.Shutdown(); controller=Controller(store); Success(controller.InitializeAsync());
            Assert(controller.Sources.Count==0&&controller.Servers.Count==0,"deleted legacy source resurrected on restart");
            Assert(File.Exists(Path.Combine(store.Data,"sources.dpapi")),"DPAPI library missing");
            byte[] encrypted=File.ReadAllBytes(Path.Combine(store.Data,"sources.dpapi"));
            Assert(System.Text.Encoding.UTF8.GetString(encrypted).IndexOf("subscription.invalid",StringComparison.Ordinal)<0,"library contains plaintext URL");
            Console.WriteLine("Config source checks PASS: "+checks+"; migration, imports, deletion, refresh, snapshots, DPAPI, restart; no OS proxy/startup writes."); return 0;
        } catch(Exception error) { Console.WriteLine("FAIL "+error.GetType().Name+": "+error.Message); return 1; }
        finally { if(controller!=null) controller.Shutdown(); }
    }
}
