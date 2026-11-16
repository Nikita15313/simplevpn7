using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using SimpleVPN;

internal static class UpdateTests {
 static int checks;
 static void Check(bool value,string code) { if(!value)throw new Exception(code);checks++; }
 static void Reject(Action action,string code) { bool rejected=false;try{action();}catch{rejected=true;}Check(rejected,code); }
 public static int Main(string[] args) {
  try {
   using(var rsa=new RSACryptoServiceProvider(new CspParameters(24))) {
    rsa.PersistKeyInCsp=false;
    byte[] file=new byte[2048];file[0]=(byte)'M';file[1]=(byte)'Z';
    string hash;using(var sha=SHA256.Create())hash=BitConverter.ToString(sha.ComputeHash(file)).Replace("-","");
    var manifest=new UpdateManifest {Version="3.0.1.0",Size=file.Length,Sha256=hash,AssetUrl="https://api.github.com/repos/Nikita15313/simplevpn7/releases/assets/123"};
    byte[] bytes=Encoding.UTF8.GetBytes(LocalStore.Json().Serialize(manifest));
    string signature=Convert.ToBase64String(rsa.SignData(bytes,CryptoConfig.MapNameToOID("SHA256")));
    var actual=UpdateManager.ValidateManifest(bytes,signature,rsa.ToXmlString(false));Check(actual.Version=="3.0.1.0","signed manifest");
    UpdateManager.ValidateInstaller(file,actual);checks++;
    file[10]^=1;Reject(delegate{UpdateManager.ValidateInstaller(file,actual);},"changed installer rejected");file[10]^=1;
    Reject(delegate{UpdateManager.ValidateInstaller(new byte[1024],actual);},"truncated installer");
    bytes[3]^=1;Reject(delegate{UpdateManager.ValidateManifest(bytes,signature,rsa.ToXmlString(false));},"changed manifest");bytes[3]^=1;
    Reject(delegate{UpdateManager.ValidateManifest(bytes,"bad signature",rsa.ToXmlString(false));},"invalid signature encoding");
    manifest.AssetUrl="https://api.github.com/repos/another/repository/releases/assets/1";
    bytes=Encoding.UTF8.GetBytes(LocalStore.Json().Serialize(manifest));signature=Convert.ToBase64String(rsa.SignData(bytes,CryptoConfig.MapNameToOID("SHA256")));
    Reject(delegate{UpdateManager.ValidateManifest(bytes,signature,rsa.ToXmlString(false));},"wrong repo even signed");
   }
   foreach(string invalid in new[]{"http://api.github.com/repos/Nikita15313/simplevpn7/releases/assets/1","https://api.github.com.evil.test/repos/Nikita15313/simplevpn7/releases/assets/1","https://api.github.com/repos/Nikita15313/simplevpn7/releases/assets/1?token=secret","https://user@api.github.com/repos/Nikita15313/simplevpn7/releases/assets/1"})
    Reject(delegate{UpdateManager.ValidateAssetUrl(invalid);},"untrusted URL");
   if(args.Length>0) {
    var store=new LocalStore(args[0]);var manager=new UpdateManager(store);
    Check(manager.LoadPreferences().AutoUpdate,"default automatic");
    manager.SavePreferences(true,"synthetic_read_key");manager.SavePreferences(false,"");
    Check(!manager.LoadPreferences().AutoUpdate&&manager.LoadPreferences().AccessToken=="","public updates discard supplied key");
    string wire=Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(store.Data,"updates.dpapi")));Check(wire.IndexOf("synthetic_read_key")<0,"no plain key");
    store.Save("updates.dpapi",new UpdatePreferences { AutoUpdate=false,AccessToken="legacy_key" });
    Check(!manager.LoadPreferences().AutoUpdate&&manager.LoadPreferences().AccessToken=="","legacy credential removed, preference preserved");
    Check(store.Load<UpdatePreferences>("updates.dpapi").AccessToken=="","legacy credential removed from stored settings");
    manager.ClearToken();Check(manager.LoadPreferences().AccessToken=="","clear key");
    Check(!manager.TryLaunchPending(),"no pending installer");
   }
   Console.WriteLine("Update tests PASS: "+checks+" signed manifest, hash, URL and DPAPI checks.");return 0;
  } catch(Exception e) { Console.Error.WriteLine("FAIL "+e.GetType().Name+" "+e.Message);return 1; }
 }
}
