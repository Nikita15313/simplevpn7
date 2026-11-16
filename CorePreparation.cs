using System;
using System.IO;
using System.Threading;

namespace SimpleVPN
{
    // Owns one unconnected, loopback-only core until Take transfers ownership.
    // Preparation never changes the system proxy or performs a health request.
    public sealed class CorePreparation : IDisposable
    {
        private readonly object gate = new object();
        private readonly OperationCancel cancellation;
        private readonly Thread worker;
        private CoreProcess candidate;
        private bool completed, disposed, taken;
        private Exception failure;
        public string Key { get; private set; }
        public bool HasCompleted { get { lock (gate) return completed; } }
        public bool Failed { get { lock (gate) return completed && failure != null; } }
        public bool Ready { get { lock (gate) return completed && failure == null && !disposed && !taken; } }
        public bool IsAlive { get { lock (gate) return completed && !disposed && !taken && candidate != null && candidate.IsAlive; } }

        public CorePreparation(string executable, ServerProfile server, AppSettings preferences, string key)
        {
            if (server == null) throw new ArgumentNullException("server");
            if (key == null) throw new ArgumentNullException("key");
            // A caller can change selection/settings while preparation is running.
            // Serialize now so the worker sees exactly the generation named by Key.
            ServerProfile snapshot = LocalStore.Json().Deserialize<ServerProfile>(LocalStore.Json().Serialize(server));
            AppSettings options = preferences == null ? null : LocalStore.Json().Deserialize<AppSettings>(LocalStore.Json().Serialize(preferences));
            Key = key;
            candidate = new CoreProcess(executable);
            CoreProcess created = candidate;
            cancellation = new OperationCancel();
            try
            {
                worker = new Thread(delegate() { Prepare(created, snapshot, options); });
                worker.IsBackground = true;
                worker.Start();
            }
            catch
            {
                created.Dispose();
                cancellation.Dispose();
                throw;
            }
        }

        private void Prepare(CoreProcess created, ServerProfile server, AppSettings preferences)
        {
            Exception error = null;
            try { created.Start(server, cancellation, preferences); }
            catch (Exception caught) { error = caught; }
            finally
            {
                // Dispose() independently owns cleanup once it marks the slot
                // disposed, including a worker that has not entered Start yet.
                try { if (error != null) created.Dispose(); }
                catch (Exception cleanupError) { if (error == null) error = cleanupError; }
                // Start's cancellation registration has gone out of scope. On a
                // successful warm core cancelling this token cannot kill it now.
                // Only this worker closes the token's wait handle.
                try { cancellation.Dispose(); }
                catch (Exception cleanupError) { if (error == null) error = cleanupError; }
                lock (gate)
                {
                    failure = error;
                    completed = true;
                    Monitor.PulseAll(gate);
                }
            }
        }

        public CoreProcess Take(OperationCancel cancel)
        {
            if (cancel == null) throw new ArgumentNullException("cancel");
            // Registration also handles an already-cancelled take: the warm core
            // is stopped rather than left running after a cancelled connection.
            using (cancel.Register(Dispose))
            {
                lock (gate)
                {
                    for (;;)
                    {
                        cancel.ThrowIfCancellationRequested();
                        if (disposed) throw new ObjectDisposedException("CorePreparation");
                        if (taken) throw new InvalidOperationException("CORE_ALREADY_TAKEN");
                        if (completed) break;
                        Monitor.Wait(gate);
                    }
                    if (failure != null) throw failure;
                    if (candidate == null || !candidate.IsAlive) throw new IOException("CORE_EXITED");
                    cancel.ThrowIfCancellationRequested();
                    CoreProcess result = candidate;
                    candidate = null;
                    taken = true;
                    return result;
                }
            }
        }

        public void Dispose()
        {
            CoreProcess abandoned;
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                abandoned = taken ? null : candidate;
                Monitor.PulseAll(gate);
            }
            // Do not hold the monitor while cancellation/termination waits for a
            // native start call. A waiting Take can observe cancellation promptly.
            cancellation.Cancel();
            if (abandoned != null) abandoned.Dispose();
        }
    }
}
