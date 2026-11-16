using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading;
using System.Windows.Forms;
using System.Security.Principal;
using System.Security.AccessControl;

namespace SimpleVPN {
 static class Program {
  [STAThread] static void Main(string[] args) {
   // HTTPS is handled by WinHTTP with explicit TLS 1.2, independently of CLR TLS defaults.
   ServicePointManager.Expect100Continue=false;
   if(args.Length==3&&args[0]=="--watchdog") {Watchdog(args);return;}
   bool first;
   string user=WindowsIdentity.GetCurrent().User.Value;
   // Create the event before choosing the owner: an early second launch remains
   // signalled until the first window is ready to listen.
   using(var activation=new InstanceActivation("Local\\SimpleVPN.Activate."+user))
   using(var mutex=new Mutex(true,"Local\\SimpleVPN.Instance."+user,out first)) {
    if(!first){activation.Signal();return;}
    Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
    AppController controller=null;
    try {
     var store=new LocalStore(AppDomain.CurrentDomain.BaseDirectory);
     var updates=new UpdateManager(store);
     if(updates.TryLaunchPending())return;
     controller=new AppController(store);
     Application.ThreadException+=(s,e)=>{store.Log("UI_EXCEPTION");controller.Shutdown();MessageBox.Show("Возникла ошибка. Закройте приложение и откройте его снова.","Simple VPN");Application.Exit();};
     using(var form=new MainForm(controller)) {
      form.Shown+=(s,e)=>{activation.Listen(form.ActivateFromSecondInstance);updates.CheckInBackground();};
      Application.Run(form);
     }
    }catch{MessageBox.Show("Не удалось запустить Simple VPN. Распакуйте всю папку приложения в доступное для записи место.","Simple VPN",MessageBoxButtons.OK,MessageBoxIcon.Information);}
    finally{if(controller!=null)controller.Shutdown();mutex.ReleaseMutex();}
   }
  }
  static void Watchdog(string[] args) {
   try {
    int pid=Int32.Parse(args[1]);long ticks=Int64.Parse(args[2]);var store=new LocalStore(AppDomain.CurrentDomain.BaseDirectory);var proxy=new ProxyManager(store);
    try {using(var owner=Process.GetProcessById(pid)) {if(owner.StartTime.ToUniversalTime().Ticks==ticks)owner.WaitForExit();}}catch(ArgumentException){}
    for(int i=0;i<3;i++) {try {proxy.RestoreForOwner(pid,ticks);return;}catch{Thread.Sleep(500);}}
    store.Log("WATCHDOG_RESTORE_FAILED");
   }catch{}
  }
 }

 // Same-user IPC only; no window-title matching or process enumeration.
 internal sealed class InstanceActivation : IDisposable {
  readonly EventWaitHandle signal;
  RegisteredWaitHandle registration;
  volatile bool disposed;
  internal InstanceActivation(string name) {
   var security=new EventWaitHandleSecurity();
   security.SetAccessRuleProtection(true,false);
   security.AddAccessRule(new EventWaitHandleAccessRule(WindowsIdentity.GetCurrent().User,EventWaitHandleRights.FullControl,AccessControlType.Allow));
   security.AddAccessRule(new EventWaitHandleAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),EventWaitHandleRights.FullControl,AccessControlType.Allow));
   bool created;
   signal=new EventWaitHandle(false,EventResetMode.AutoReset,name,out created,security);
  }
  internal void Signal() {if(!disposed)signal.Set();}
  internal void Listen(Action activate) {
   if(disposed||registration!=null)return;
   registration=ThreadPool.RegisterWaitForSingleObject(signal,delegate {
    if(disposed)return;
    try {activate();}catch(InvalidOperationException) { /* Window closed during dispatch. */ }
   },null,Timeout.Infinite,false);
  }
  public void Dispose() {
   if(disposed)return;
   disposed=true;
   if(registration!=null)registration.Unregister(null);
   signal.Close();
  }
 }
}

