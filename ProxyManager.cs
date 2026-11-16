using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Security.Principal;
using Microsoft.Win32;

namespace SimpleVPN {
 public class RegistryEntry { public string Key,Name,Text; public int Kind; public bool Exists; }
 public class ProxySnapshot {
  public List<RegistryEntry> Entries=new List<RegistryEntry>();
  public int Flags; public string Endpoint; public int OwnerPid; public long OwnerStart;
 }
 public interface IProxyPlatform {
  ProxySnapshot Capture();
  void Apply(string endpoint);
  void Restore(ProxySnapshot snapshot);
  bool Owns(string endpoint);
 }
 public sealed class ProxyManager {
  public const string InternetKey=@"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
  readonly LocalStore store; readonly IProxyPlatform platform;
  public ProxyManager(LocalStore value) : this(value,new WindowsProxyPlatform()) {}
  public ProxyManager(LocalStore value,IProxyPlatform system) {store=value;platform=system;}
  sealed class WindowsProxyPlatform:IProxyPlatform {
   public ProxySnapshot Capture(){return ProxyManager.Capture();}
   public void Apply(string endpoint){SetOptions(3,endpoint,"<local>;localhost;127.*;[::1]","");}
   public void Restore(ProxySnapshot snapshot){RestoreSnapshot(snapshot);}
   public bool Owns(string endpoint){using(var k=Registry.CurrentUser.OpenSubKey(InternetKey)) return k!=null&&Convert.ToString(k.GetValue("ProxyServer",""))==endpoint&&Convert.ToInt32(k.GetValue("ProxyEnable",0))==1;}
  }
  [StructLayout(LayoutKind.Explicit,Size=16)] struct Option { [FieldOffset(0)] public int Code; [FieldOffset(8)] public IntPtr Value; }
  [StructLayout(LayoutKind.Sequential)] struct OptionList { public int Size; public IntPtr Connection; public int Count,Error; public IntPtr Options; }
  [DllImport("wininet.dll",EntryPoint="InternetSetOptionW",SetLastError=true)] static extern bool Set(IntPtr h,int opt,IntPtr data,int size);
  [DllImport("wininet.dll",EntryPoint="InternetQueryOptionW",SetLastError=true)] static extern bool Query(IntPtr h,int opt,IntPtr data,ref int size);
  [DllImport("advapi32.dll",CharSet=CharSet.Unicode)] static extern int RegOpenKeyEx(IntPtr root,string sub,int reserved,int access,out IntPtr key);
  [DllImport("advapi32.dll",CharSet=CharSet.Unicode)] static extern int RegQueryValueEx(IntPtr key,string name,IntPtr reserved,out int kind,byte[] data,ref int size);
  [DllImport("advapi32.dll",CharSet=CharSet.Unicode)] static extern int RegSetValueEx(IntPtr key,string name,int reserved,int kind,byte[] data,int size);
  [DllImport("advapi32.dll")] static extern int RegCloseKey(IntPtr key);
  static byte[] ReadNone(string sub,string name) {
   IntPtr key; if(RegOpenKeyEx(new IntPtr(unchecked((int)0x80000001)),InternetKey+sub,0,1,out key)!=0)throw new IOException("PROXY_QUERY_FAILED");
   try {int kind,size=0;if(RegQueryValueEx(key,name,IntPtr.Zero,out kind,null,ref size)!=0||kind!=0||size<0||size>1048576)throw new IOException("PROXY_QUERY_FAILED");
    byte[] bytes=new byte[size];if(RegQueryValueEx(key,name,IntPtr.Zero,out kind,bytes,ref size)!=0)throw new IOException("PROXY_QUERY_FAILED");return bytes;
   }finally{RegCloseKey(key);}
  }
  static void WriteNone(string sub,string name,byte[] data) {
   IntPtr key;if(RegOpenKeyEx(new IntPtr(unchecked((int)0x80000001)),InternetKey+sub,0,2,out key)!=0)throw new IOException("PROXY_SET_FAILED");
   try{if(RegSetValueEx(key,name,0,0,data,data.Length)!=0)throw new IOException("PROXY_SET_FAILED");}finally{RegCloseKey(key);}
  }
  static int ReadFlags() {
   IntPtr options=Marshal.AllocHGlobal(16), list=IntPtr.Zero;
   try { Marshal.StructureToPtr(new Option {Code=1},options,false); var l=new OptionList {Size=Marshal.SizeOf(typeof(OptionList)),Count=1,Options=options}; list=Marshal.AllocHGlobal(l.Size); Marshal.StructureToPtr(l,list,false); int len=l.Size;
    if(!Query(IntPtr.Zero,75,list,ref len)) throw new IOException("PROXY_QUERY_FAILED");
    return ((Option)Marshal.PtrToStructure(options,typeof(Option))).Value.ToInt32();
   } finally { Marshal.FreeHGlobal(options); if(list!=IntPtr.Zero) Marshal.FreeHGlobal(list); }
  }
  public static void Notify() { if(!Set(IntPtr.Zero,95,IntPtr.Zero,0)) { Set(IntPtr.Zero,39,IntPtr.Zero,0); Set(IntPtr.Zero,37,IntPtr.Zero,0); } }
  static void SetOptions(int flags,string server,string bypass,string pac) { SetOptions(flags,server,bypass,pac,true); }
  static void SetOptions(int flags,string server,string bypass,string pac,bool notify) {
   var strings=new List<IntPtr>(); IntPtr options=Marshal.AllocHGlobal(16*4),list=IntPtr.Zero;
   try {
    Marshal.StructureToPtr(new Option {Code=1,Value=new IntPtr(flags)},options,false);
    string[] values={server,bypass,pac};
    for(int i=0;i<3;i++) { IntPtr s=Marshal.StringToHGlobalUni(values[i]??""); strings.Add(s); Marshal.StructureToPtr(new Option {Code=i+2,Value=s},new IntPtr(options.ToInt64()+(i+1)*16),false); }
    var l=new OptionList {Size=Marshal.SizeOf(typeof(OptionList)),Count=4,Options=options}; list=Marshal.AllocHGlobal(l.Size); Marshal.StructureToPtr(l,list,false);
    if(!Set(IntPtr.Zero,75,list,l.Size)) throw new IOException("PROXY_SET_FAILED"); if(notify)Notify();
   } finally { foreach(var p in strings) Marshal.FreeHGlobal(p); Marshal.FreeHGlobal(options); if(list!=IntPtr.Zero) Marshal.FreeHGlobal(list); }
  }
  public static ProxySnapshot Capture() {
   var s=new ProxySnapshot {Flags=ReadFlags()};
   foreach(string sub in new[]{"","\\Connections"}) using(var k=Registry.CurrentUser.OpenSubKey(InternetKey+sub)) {
    foreach(string n in (sub=="" ? new[]{"ProxyEnable","ProxyServer","ProxyOverride","AutoConfigURL","AutoDetect"} : new[]{"DefaultConnectionSettings","SavedLegacySettings"})) {
     var e=new RegistryEntry {Key=sub,Name=n,Exists=k!=null&&Array.IndexOf(k.GetValueNames(),n)>=0};
     if(e.Exists) { e.Kind=(int)k.GetValueKind(n); object v=k.GetValue(n,null,RegistryValueOptions.DoNotExpandEnvironmentNames); if(e.Kind==0||e.Kind==-1){v=ReadNone(sub,n);e.Kind=-1;} e.Text=(v is byte[]) ? Convert.ToBase64String((byte[])v) : (v is string[]) ? LocalStore.Json().Serialize(v) : Convert.ToString(v,System.Globalization.CultureInfo.InvariantCulture); }
     s.Entries.Add(e);
    }
   } return s;
  }
  static string Value(ProxySnapshot s,string name) { var e=s.Entries.Find(v=>v.Key==""&&v.Name==name); return e!=null&&e.Exists?e.Text:""; }
  public static void RestoreSnapshot(ProxySnapshot s) {
   SetOptions(s.Flags,Value(s,"ProxyServer"),Value(s,"ProxyOverride"),Value(s,"AutoConfigURL"),false);
   foreach(var e in s.Entries) using(var k=Registry.CurrentUser.CreateSubKey(InternetKey+e.Key)) {
    if(!e.Exists) k.DeleteValue(e.Name,false);
    else { object v=e.Text; var kind=(RegistryValueKind)e.Kind; if(e.Kind==-1){WriteNone(e.Key,e.Name,Convert.FromBase64String(e.Text));continue;} if(kind==RegistryValueKind.Binary) v=Convert.FromBase64String(e.Text); else if(kind==RegistryValueKind.MultiString) v=LocalStore.Json().Deserialize<string[]>(e.Text); else if(kind==RegistryValueKind.DWord) v=Int32.Parse(e.Text,System.Globalization.CultureInfo.InvariantCulture); else if(kind==RegistryValueKind.QWord) v=Int64.Parse(e.Text,System.Globalization.CultureInfo.InvariantCulture); k.SetValue(e.Name,v,kind); }
   } Notify();
  }
  public void Apply(int port) {
   using(var gate=new Mutex(false,MutexName)) { Lock(gate); try {
    if(File.Exists(Journal)) throw new IOException("RECOVERY_PENDING");
    var s=platform.Capture(); s.Endpoint="127.0.0.1:"+port; var p=System.Diagnostics.Process.GetCurrentProcess(); s.OwnerPid=p.Id; s.OwnerStart=p.StartTime.ToUniversalTime().Ticks;
    store.Save("proxy-session.dpapi",s);
    try { platform.Apply(s.Endpoint); }
    catch { platform.Restore(s); File.Delete(Journal); throw; }
   } finally { gate.ReleaseMutex(); } }
  }
  public string Journal {get{return Path.Combine(store.Data,"proxy-session.dpapi");}}
  public static string MutexName {get{return "Local\\SimpleVPN.Recovery."+WindowsIdentity.GetCurrent().User.Value;}}
  static void Lock(Mutex m) {try {m.WaitOne();}catch(AbandonedMutexException){}}
  public bool Restore(bool force) {return RestoreOwned(force,0,0);}
  public bool RestoreForOwner(int pid,long ticks) {return RestoreOwned(false,pid,ticks);}
  bool RestoreOwned(bool force,int pid,long ticks) {
   using(var gate=new Mutex(false,MutexName)) { Lock(gate); try {
    var s=store.Load<ProxySnapshot>("proxy-session.dpapi"); if(s==null) return false;
    if(pid!=0&&(s.OwnerPid!=pid||s.OwnerStart!=ticks))return false;
    bool own=platform.Owns(s.Endpoint);
    if(own||force) platform.Restore(s); else store.Log("PROXY_EXTERNALLY_CHANGED");
    File.Delete(Journal); return own||force;
   } finally { gate.ReleaseMutex(); } }
  }
 }
}

