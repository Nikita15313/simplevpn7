using System;
using System.IO;
using System.Diagnostics;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;

namespace SimpleVPN
{
    public sealed class CoreProcess : IDisposable
    {
        private readonly object gate = new object();
        private readonly string executable;
        private Process child;
        private PipeDrain outputDrain, errorDrain;
        private IntPtr job;
        private bool disposed;
        public int Port { get; private set; }
        public int Pid { get { lock (gate) return child == null ? 0 : child.Id; } }
        public bool IsAlive { get { lock (gate) { try { return child != null && !child.HasExited; } catch { return false; } } } }
        public CoreProcess(string path) { executable = Path.GetFullPath(path); }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr a, string n);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr j, int c, IntPtr p, uint l);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr j, IntPtr p);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

        // Called only on a background worker. The core receives UTF-8 over stdin.
        public void Start(ServerProfile server, OperationCancel token)
        { Start(server, token, null); }
        public void Start(ServerProfile server, OperationCancel token, AppSettings preferences)
        {
            token.ThrowIfCancellationRequested();
            Stream input;
            Process started;
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException("CoreProcess");
                if (!File.Exists(executable)) throw new FileNotFoundException("CORE_MISSING");
                var listener = new TcpListener(IPAddress.Loopback, 0);
                try { listener.Start(); Port = ((IPEndPoint)listener.LocalEndpoint).Port; }
                finally { listener.Stop(); }
                job = CreateJobObject(IntPtr.Zero, null);
                if (job == IntPtr.Zero) throw new IOException("JOB_CREATE_FAILED", new Win32Exception(Marshal.GetLastWin32Error()));
                IntPtr info = Marshal.AllocHGlobal(144);
                try
                {
                    for (int i = 0; i < 144; i++) Marshal.WriteByte(info, i, 0);
                    Marshal.WriteInt32(info, 16, 0x2000);
                    if (!SetInformationJobObject(job, 9, info, 144)) throw new IOException("JOB_SETUP_FAILED", new Win32Exception(Marshal.GetLastWin32Error()));
                }
                finally { Marshal.FreeHGlobal(info); }
                child = new Process { StartInfo = new ProcessStartInfo(executable, "run -config stdin:")
                {
                    UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = Path.GetDirectoryName(executable), RedirectStandardInput = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8
                } };
                if (!child.Start()) throw new IOException("CORE_START_FAILED");
                if (!AssignProcessToJobObject(job, child.Handle))
                {
                    int error = Marshal.GetLastWin32Error();
                    try { child.Kill(); } catch { /* Dispose still waits and retries termination. */ }
                    throw new IOException("CORE_JOB_FAILED", new Win32Exception(error));
                }
                // CLR 2 implements asynchronous reads of these synchronous pipes
                // with pool workers. A quiet core can hold those workers forever,
                // delaying connection checks and cancellation on a small CPU.
                outputDrain = new PipeDrain(child.StandardOutput);
                errorDrain = new PipeDrain(child.StandardError);
                input = child.StandardInput.BaseStream;
                started = child;
            }
            using (token.Register(Dispose))
            {
                token.ThrowIfCancellationRequested();
                byte[] config = System.Text.Encoding.UTF8.GetBytes(preferences == null ? SubscriptionParser.BuildConfig(server, Port) : ConnectionOptions.BuildConfig(server, Port, preferences));
                try { input.Write(config, 0, config.Length); input.Flush(); started.StandardInput.Close(); }
                finally { Array.Clear(config, 0, config.Length); }
                for (int i = 0; i < 80; i++)
                {
                    token.ThrowIfCancellationRequested();
                    if (!IsAlive) throw new IOException("CORE_EXITED");
                    using (var socket = new TcpClient())
                    {
                        try
                        {
                            IAsyncResult pending = socket.BeginConnect(IPAddress.Loopback, Port, null, null);
                            using (var completed = pending.AsyncWaitHandle)
                            {
                                if (completed.WaitOne(100, false)) { socket.EndConnect(pending); if (IsAlive) return; }
                            }
                        }
                        catch (SocketException) { }
                    }
                    if (token.Wait(100)) token.ThrowIfCancellationRequested();
                }
                throw new IOException("CORE_TIMEOUT");
            }
        }

        public void Dispose()
        {
            Process ended;
            IntPtr endedJob;
            PipeDrain endedOutput, endedError;
            lock (gate) {
                disposed = true; ended = child; child = null; endedJob = job; job = IntPtr.Zero;
                endedOutput = outputDrain; outputDrain = null; endedError = errorDrain; errorDrain = null;
            }
            if (endedJob != IntPtr.Zero) CloseHandle(endedJob);
            if (ended != null)
            {
                try { if (!ended.HasExited) ended.Kill(); } catch { }
                // Closing a job starts termination; Kill can race it and report access denied.
                // Still wait on the process handle before reporting that its port is closed.
                try { ended.WaitForExit(1500); } catch { }
                try { ended.StandardInput.Close(); } catch { }
                if (endedOutput != null) endedOutput.Wait();
                else { try { ended.StandardOutput.Close(); } catch { } }
                if (endedError != null) endedError.Wait();
                else { try { ended.StandardError.Close(); } catch { } }
                ended.Dispose();
            }
        }

        private sealed class PipeDrain
        {
            private readonly StreamReader reader;
            private readonly Thread thread;
            internal PipeDrain(StreamReader source)
            {
                reader = source;
                thread = new Thread(Read) { IsBackground = true };
                try { thread.Start(); }
                catch { reader.Close(); throw; }
            }
            private void Read()
            {
                char[] buffer = new char[1024];
                try { while (reader.Read(buffer, 0, buffer.Length) > 0) { } }
                catch { /* Output is deliberately discarded, including credentials. */ }
                finally { Array.Clear(buffer, 0, buffer.Length); try { reader.Close(); } catch { } }
            }
            internal void Wait()
            {
                // Terminating the job closes every writer. The reader owns and
                // closes its stream at EOF; Process.Dispose does not close it.
                try { thread.Join(200); } catch { }
            }
        }
    }
}
