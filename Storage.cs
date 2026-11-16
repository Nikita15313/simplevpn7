using System;
using System.IO;
using System.Text;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace SimpleVPN {
 public sealed class SubscriptionMetadata {
  public string Title, WebPageUrl, SupportUrl;
  public long? UploadBytes, DownloadBytes, TotalBytes, ExpireUnixSeconds;
  public int? UpdateIntervalHours;
  public DateTime RetrievedUtc = DateTime.MinValue;
 }
 public sealed class SubscriptionDownload {
  public string Body;
  public SubscriptionMetadata Metadata;
 }
 public class AppSettings {
  public const int MinimumUpdateIntervalHours = 1, MaximumUpdateIntervalHours = 168;
  public static int ClampUpdateIntervalHours(int hours) { return Math.Max(MinimumUpdateIntervalHours,Math.Min(MaximumUpdateIntervalHours,hours)); }
  public void ApplyProviderUpdateInterval() {
   if(FollowProviderUpdateInterval && Metadata!=null && Metadata.UpdateIntervalHours.HasValue)
    UpdateIntervalHours=ClampUpdateIntervalHours(Metadata.UpdateIntervalHours.Value);
  }
  public string SubscriptionUrl = "", SelectedServerId = "";
  public string SelectedSourceId = "";
  public bool StartWithWindows, AutoConnect;
  public DateTime LastUpdatedUtc = DateTime.MinValue;
  public SubscriptionMetadata Metadata = new SubscriptionMetadata();
  public int UpdateIntervalHours = 12;
  public bool FollowProviderUpdateInterval = true;
  public bool PingOnOpen, SortByLatency;
  public string RoutingMode = "all", DnsMode = "system";
  public string DirectDomains = "", ProxyDomains = "";
  public bool BlockAds, RememberWindowPosition;
  public int WindowX, WindowY;
  public string ConnectionTestUrl = "https://www.gstatic.com/generate_204";
  public int ConnectionTimeoutSeconds = 6;
  public string Theme = "dark";
  public bool AutoSelectFastest;
 }
 public sealed class LocalStore {
  [System.Runtime.InteropServices.DllImport("kernel32.dll",SetLastError=true)] static extern bool FlushFileBuffers(Microsoft.Win32.SafeHandles.SafeFileHandle file);
  public readonly string Root, Data;
  public LocalStore(string root) { Root=Path.GetFullPath(root); Data=Path.Combine(Root,"data"); Directory.CreateDirectory(Data); ProtectDirectory(Data); }
  public static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength=8*1024*1024, RecursionLimit=64 }; }
  public static void ProtectDirectory(string path) {
   var acl=new DirectorySecurity(); acl.SetAccessRuleProtection(true,false);
   acl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
   acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
   new DirectoryInfo(path).SetAccessControl(acl);
  }
  public void Save<T>(string name,T value) {
   byte[] plain=Encoding.UTF8.GetBytes(Json().Serialize(value));
   try { Atomic(Path.Combine(Data,name),ProtectedData.Protect(plain,null,DataProtectionScope.CurrentUser)); } finally { Array.Clear(plain,0,plain.Length); }
  }
  public T Load<T>(string name) {
   string path=Path.Combine(Data,name); if(!File.Exists(path)) return default(T);
   byte[] plain=ProtectedData.Unprotect(File.ReadAllBytes(path),null,DataProtectionScope.CurrentUser);
   try { return Json().Deserialize<T>(Encoding.UTF8.GetString(plain)); } finally { Array.Clear(plain,0,plain.Length); }
  }
  public AppSettings LoadSettings() {
   AppSettings settings=Load<AppSettings>("settings.dpapi") ?? new AppSettings();
   int interval=AppSettings.ClampUpdateIntervalHours(settings.UpdateIntervalHours);
   if(settings.UpdateIntervalHours!=interval) {
    settings.UpdateIntervalHours=interval;
    // Older builds copied the provider's unrestricted interval into preferences.
    // Keep the recovered settings usable even if their file cannot be rewritten.
    try { Save("settings.dpapi",settings); } catch { Log("SETTINGS_INTERVAL_SAVE_FAILED"); }
   }
   return settings;
  }
  public static void Atomic(string path,byte[] bytes) {
   string tmp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
   try { using(var f=new FileStream(tmp,FileMode.CreateNew,FileAccess.Write,FileShare.None)) { f.Write(bytes,0,bytes.Length); f.Flush(); if(!FlushFileBuffers(f.SafeFileHandle))throw new IOException("SAVE_FLUSH_FAILED"); }
    if(File.Exists(path)) File.Replace(tmp,path,null); else File.Move(tmp,path);
   } finally { if(File.Exists(tmp)) File.Delete(tmp); }
  }
  public void Log(string code) {
   // Accept event identifiers only. Never accept exception text, URLs or core output.
   if(!System.Text.RegularExpressions.Regex.IsMatch(code,"^[A-Z0-9_]{1,64}$")) return;
   try { string p=Path.Combine(Data,"diagnostic.log"); if(File.Exists(p)&&new FileInfo(p).Length>131072) File.Delete(p); File.AppendAllText(p,DateTime.UtcNow.ToString("O")+" "+code+Environment.NewLine,Encoding.UTF8); } catch { }
  }
  public void SetStartup(bool enabled) {
   using(var k=Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) {
    if(enabled) k.SetValue("SimpleVPN", "\""+Path.Combine(Root,"SimpleVPN.exe")+"\" --startup",RegistryValueKind.String);
    else k.DeleteValue("SimpleVPN",false);
   }
  }
 }
}
