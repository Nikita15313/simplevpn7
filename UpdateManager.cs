using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace SimpleVPN {
 public sealed class UpdatePreferences { public bool AutoUpdate = true; public string AccessToken = ""; }
 public sealed class UpdateManifest { public string Version, Sha256, AssetUrl; public long Size; }
 public sealed class PendingUpdate { public string Manifest, Signature; public DateTime AttemptUtc; }
 public sealed class UpdateManager {
  public const string RepositoryUrl = "https://github.com/Nikita15313/simplevpn7";
  public const string TokenSetupUrl = "https://github.com/settings/personal-access-tokens/new?name=SimpleVPN7%20updates&description=Read%20private%20SimpleVPN7%20releases&target_name=Nikita15313&contents=read";
  const string Api = "https://api.github.com/repos/Nikita15313/simplevpn7/releases/";
  readonly LocalStore store;
  System.Threading.Timer periodic;
  public UpdateManager(LocalStore local) { if(local==null)throw new ArgumentNullException("local");store=local; }
  public UpdatePreferences LoadPreferences() { try { return store.Load<UpdatePreferences>("updates.dpapi") ?? new UpdatePreferences(); } catch { return new UpdatePreferences(); } }
  public void SavePreferences(bool auto,string accessToken) {
   var value=LoadPreferences(); value.AutoUpdate=auto;
   if(!String.IsNullOrEmpty(accessToken)) {
    accessToken=accessToken.Trim();
    if(accessToken.Length>1024)throw new ArgumentException("Ключ слишком длинный.");
    foreach(char c in accessToken)if(c<33||c>126)throw new ArgumentException("Ключ содержит недопустимые символы.");
    value.AccessToken=accessToken;
   }
   store.Save("updates.dpapi",value);
  }
  public void ClearToken() { var value=LoadPreferences();value.AccessToken="";store.Save("updates.dpapi",value); }
  public string LoadStatus() { try { return store.Load<string>("update-status.dpapi") ?? "Обновления ещё не проверялись."; } catch { return "Обновления ещё не проверялись."; } }
  string Status(string message) { try { store.Save("update-status.dpapi",message); } catch {} return message; }
  byte[] Get(string url,string token,string accept,int maximum) {
   int port=0;
   try {
    var snapshot=store.Load<ProxySnapshot>("proxy-session.dpapi");
    using(var owner=Process.GetCurrentProcess())
     if(snapshot!=null&&snapshot.OwnerPid==owner.Id&&snapshot.OwnerStart==owner.StartTime.ToUniversalTime().Ticks&&snapshot.Endpoint!=null&&snapshot.Endpoint.StartsWith("127.0.0.1:",StringComparison.Ordinal))
      Int32.TryParse(snapshot.Endpoint.Substring(10),out port);
   } catch { port=0; }
   if(port>0&&port<=65535) {
    try { return UpdateTransport.Get(url,token,accept,maximum,port); } catch(IOException) { }
   }
   return UpdateTransport.Get(url,token,accept,maximum);
  }
  public void CheckInBackground() {
   if(periodic==null)periodic=new System.Threading.Timer(delegate { if(LoadPreferences().AutoUpdate) { try { CheckAndStage(); } catch { store.Log("UPDATE_CHECK_FAILED"); } } },null,6*60*60*1000,6*60*60*1000);
   if(!LoadPreferences().AutoUpdate)return;
   System.Threading.ThreadPool.QueueUserWorkItem(delegate { try { CheckAndStage(); } catch { store.Log("UPDATE_CHECK_FAILED"); } });
  }
  public string CheckAndStage() {
   using(var gate=new System.Threading.Mutex(false,"Local\\SimpleVPN.Update."+System.Security.Principal.WindowsIdentity.GetCurrent().User.Value)) {
    bool owned=false;
    try {
     try { owned=gate.WaitOne(0,false); } catch(System.Threading.AbandonedMutexException) { owned=true; }
     if(!owned)return "Проверка обновления уже выполняется.";
     var prefs=LoadPreferences();
     if(String.IsNullOrEmpty(prefs.AccessToken))return Status("Добавьте ключ чтения GitHub, чтобы получать приватные обновления.");
     var json=LocalStore.Json();
     var release=json.Deserialize<Dictionary<string,object>>(Encoding.UTF8.GetString(Get(Api+"latest",prefs.AccessToken,"application/vnd.github+json",2*1024*1024)));
     object draft, prerelease;
     if(release==null||(release.TryGetValue("draft",out draft)&&Convert.ToBoolean(draft))||(release.TryGetValue("prerelease",out prerelease)&&Convert.ToBoolean(prerelease)))throw new IOException("UPDATE_RELEASE_INVALID");
     string manifestUrl=FindAsset(release,"update.json"), signatureUrl=FindAsset(release,"update.json.sig");
     byte[] bytes=Get(manifestUrl,prefs.AccessToken,"application/octet-stream",8192);
     string signature=Encoding.ASCII.GetString(Get(signatureUrl,prefs.AccessToken,"application/octet-stream",2048)).Trim();
     UpdateManifest manifest=ValidateManifest(bytes,signature,UpdateTrust.PublicKeyXml);
     Version version=new Version(manifest.Version), current=Assembly.GetExecutingAssembly().GetName().Version;
     if(version.CompareTo(current)<=0)return Status("Установлена последняя версия: "+current.ToString(3)+".");
     string folder=Path.Combine(store.Data,"updates");Directory.CreateDirectory(folder);LocalStore.ProtectDirectory(folder);
     byte[] installer=Get(manifest.AssetUrl,prefs.AccessToken,"application/octet-stream",128*1024*1024);
     ValidateInstaller(installer,manifest);
     LocalStore.Atomic(Path.Combine(folder,"setup.exe"),installer);
     store.Save("pending-update.dpapi",new PendingUpdate { Manifest=Convert.ToBase64String(bytes),Signature=signature });
     return Status("Версия "+version.ToString(3)+" загружена. Установится при следующем запуске приложения.");
    } catch(Exception error) {
     store.Log("UPDATE_CHECK_FAILED");
     var io=error as IOException;
     if(io!=null&&(io.Message=="UPDATE_HTTP401"||io.Message=="UPDATE_HTTP403"||io.Message=="UPDATE_HTTP404"))return Status("GitHub не предоставил доступ. Проверьте срок ключа и разрешение Contents: read для SimpleVPN7.");
     return Status("Не удалось получить обновление. Текущая версия продолжит работать.");
    } finally { if(owned)gate.ReleaseMutex(); }
   }
  }
  static string FindAsset(Dictionary<string,object> release,string name) {
   object assets;
   if(release.TryGetValue("assets",out assets)) {
    var sequence=assets as System.Collections.IEnumerable;
    if(sequence!=null)foreach(object item in sequence) {
     var asset=item as Dictionary<string,object>; object assetName,url;
     if(asset!=null&&asset.TryGetValue("name",out assetName)&&Convert.ToString(assetName)==name&&asset.TryGetValue("url",out url)) {
      string value=Convert.ToString(url); ValidateAssetUrl(value);return value;
     }
    }
   }
   throw new IOException("UPDATE_ASSET_MISSING");
  }
  public static void ValidateAssetUrl(string value) {
   if(value==null||!System.Text.RegularExpressions.Regex.IsMatch(value,"^https://api\\.github\\.com/repos/Nikita15313/simplevpn7/releases/assets/[0-9]+$",System.Text.RegularExpressions.RegexOptions.IgnoreCase))throw new IOException("UPDATE_ASSET_URL");
  }
  public static UpdateManifest ValidateManifest(byte[] bytes,string signature,string publicKey) {
   if(bytes==null||bytes.Length==0||bytes.Length>8192||signature==null||signature.Length>2048)throw new IOException("UPDATE_MANIFEST_INVALID");
   using(var rsa=new RSACryptoServiceProvider(new CspParameters(24))) {
    rsa.PersistKeyInCsp=false;rsa.FromXmlString(publicKey);
    if(!rsa.VerifyData(bytes,CryptoConfig.MapNameToOID("SHA256"),Convert.FromBase64String(signature)))throw new IOException("UPDATE_SIGNATURE_INVALID");
   }
   var manifest=LocalStore.Json().Deserialize<UpdateManifest>(new UTF8Encoding(false,true).GetString(bytes));
   Version version;
   try { version=new Version(manifest.Version); } catch { throw new IOException("UPDATE_VERSION_INVALID"); }
   if(version.Major<1||version.Revision<0||manifest.Size<1024||manifest.Size>128L*1024*1024||manifest.Sha256==null||!System.Text.RegularExpressions.Regex.IsMatch(manifest.Sha256,"^[a-fA-F0-9]{64}$"))throw new IOException("UPDATE_MANIFEST_INVALID");
   ValidateAssetUrl(manifest.AssetUrl);return manifest;
  }
  public static void ValidateInstaller(byte[] bytes,UpdateManifest manifest) {
   if(bytes==null||bytes.LongLength!=manifest.Size||bytes.Length<2||bytes[0]!='M'||bytes[1]!='Z')throw new IOException("UPDATE_INSTALLER_INVALID");
   string hash;using(var sha=SHA256.Create())hash=BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","");
   if(!String.Equals(hash,manifest.Sha256,StringComparison.OrdinalIgnoreCase))throw new IOException("UPDATE_HASH_INVALID");
  }
  // Runs before the single-instance window is created. The standard installer
  // updates application files; data lives outside its packaged file list.
  public bool TryLaunchPending() {
   try {
    if(!LoadPreferences().AutoUpdate)return false;
    var pending=store.Load<PendingUpdate>("pending-update.dpapi");if(pending==null)return false;
    var manifest=ValidateManifest(Convert.FromBase64String(pending.Manifest),pending.Signature,UpdateTrust.PublicKeyXml);
    if(new Version(manifest.Version).CompareTo(Assembly.GetExecutingAssembly().GetName().Version)<=0) { File.Delete(Path.Combine(store.Data,"pending-update.dpapi"));return false; }
    // A failed installer must not make every launch exit in an update loop.
    if(pending.AttemptUtc!=DateTime.MinValue) { Status("Предыдущее обновление не завершилось. Нажмите «Проверить обновления» для повторной загрузки.");return false; }
    string installer=Path.Combine(Path.Combine(store.Data,"updates"),"setup.exe");
    if(new FileInfo(installer).Length!=manifest.Size)throw new IOException("UPDATE_INSTALLER_SIZE");
    ValidateInstaller(File.ReadAllBytes(installer),manifest);
    pending.AttemptUtc=DateTime.UtcNow;store.Save("pending-update.dpapi",pending);
    string installRoot=Directory.GetParent(store.Root).FullName;
    if(Path.GetFileName(store.Root).Equals("app",StringComparison.OrdinalIgnoreCase)==false)throw new IOException("UPDATE_LAYOUT_INVALID");
    Process started=Process.Start(new ProcessStartInfo(installer,"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /AUTOUPDATE=1 /DIR=\""+installRoot+"\"") { UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=Path.GetDirectoryName(installer) });
    if(started==null)throw new IOException("UPDATE_START_FAILED");started.Dispose();return true;
   } catch {store.Log("UPDATE_APPLY_FAILED");Status("Обновление не установилось. Можно продолжать пользоваться текущей версией.");return false;}
  }
 }
}
