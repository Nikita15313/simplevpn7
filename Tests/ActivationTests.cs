using System;
using System.Diagnostics;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

internal static class ActivationCheck
{
    static Type type;
    static object Create(string name) { return Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { name }, null); }
    static void Call(object value, string method, params object[] args) { type.GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(value, args); }
    static void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine("PASS: " + message); }
    static int Main(string[] args)
    {
        type = Assembly.LoadFrom(args[0]).GetType("SimpleVPN.InstanceActivation", true);
        if (args.Length == 3 && args[1] == "--signal")
        {
            using (IDisposable sender = (IDisposable)Create(args[2])) Call(sender, "Signal");
            return 0;
        }
        string name = "Local\\SimpleVPN.ActivationCheck." + Guid.NewGuid().ToString("N");
        int count = 0;
        using (AutoResetEvent received = new AutoResetEvent(false))
        using (IDisposable receiver = (IDisposable)Create(name))
        {
            Call(receiver, "Signal");
            Call(receiver, "Listen", new Action(delegate { Interlocked.Increment(ref count); received.Set(); }));
            Check(received.WaitOne(3000), "signal before listener registration is retained");
            Call(receiver, "Signal");
            Check(received.WaitOne(3000), "second activation reaches the same listener");
            using (Process child = Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, "\"" + args[0] + "\" --signal " + name)
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden }))
            {
                Check(received.WaitOne(5000), "separate process activates existing same-user event");
                Check(child.WaitForExit(5000) && child.ExitCode == 0, "signalling process exits cleanly");
            }
            EventWaitHandle handle = (EventWaitHandle)type.GetField("signal", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(receiver);
            EventWaitHandleSecurity acl = handle.GetAccessControl();
            string sid = WindowsIdentity.GetCurrent().User.Value;
            bool owner = false;
            foreach (EventWaitHandleAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                string identity = rule.IdentityReference.Value;
                if (identity != sid && identity != "S-1-5-18") throw new Exception("Event permits another identity");
                if (identity == sid && rule.AccessControlType == AccessControlType.Allow) owner = true;
            }
            Check(owner && acl.AreAccessRulesProtected, "event ACL allows only the current user and Local System");
            receiver.Dispose();
            using (IDisposable sender = (IDisposable)Create(name)) Call(sender, "Signal");
            Check(!received.WaitOne(250) && count == 3, "disposed listener receives no further activation");
        }
        return 0;
    }
}
