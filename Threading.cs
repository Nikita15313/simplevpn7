using System;
using System.Collections.Generic;
using System.Threading;

namespace SimpleVPN
{
    public sealed class OperationCancelledException : Exception { }

    public sealed class OperationCancel : IDisposable
    {
        private readonly object gate = new object();
        private readonly ManualResetEvent signal = new ManualResetEvent(false);
        private readonly List<Action> callbacks = new List<Action>();
        private volatile bool cancelled;
        public bool IsCancellationRequested { get { return cancelled; } }
        public void ThrowIfCancellationRequested() { if (cancelled) throw new OperationCancelledException(); }
        public bool Wait(int milliseconds) { return signal.WaitOne(milliseconds, false); }
        public IDisposable Register(Action callback)
        {
            if (callback == null) throw new ArgumentNullException("callback");
            bool run;
            lock (gate) { run = cancelled; if (!run) callbacks.Add(callback); }
            if (run) callback();
            return new Registration(this, callback);
        }
        public void Cancel()
        {
            Action[] run;
            lock (gate)
            {
                if (cancelled) return;
                cancelled = true;
                signal.Set();
                run = callbacks.ToArray();
                callbacks.Clear();
            }
            foreach (Action callback in run) { try { callback(); } catch { } }
        }
        public void Dispose() { Cancel(); signal.Close(); }
        private sealed class Registration : IDisposable
        {
            private OperationCancel owner;
            private readonly Action callback;
            internal Registration(OperationCancel value, Action action) { owner = value; callback = action; }
            public void Dispose()
            {
                OperationCancel value = Interlocked.Exchange(ref owner, null);
                if (value != null) lock (value.gate) value.callbacks.Remove(callback);
            }
        }
    }

    public sealed class WorkTicket
    {
        private readonly object gate = new object();
        private bool finished;
        private Exception error;
        private readonly List<Action<Exception>> callbacks = new List<Action<Exception>>();
        public void OnCompleted(Action<Exception> callback)
        {
            Exception result;
            lock (gate) { if (!finished) { callbacks.Add(callback); return; } result = error; }
            callback(result);
        }
        internal void Complete(Exception failure)
        {
            Action<Exception>[] run;
            lock (gate) { if (finished) return; finished = true; error = failure; run = callbacks.ToArray(); callbacks.Clear(); }
            foreach (Action<Exception> callback in run) callback(failure);
        }
    }

    internal static class Compat
    {
        internal static bool IsBlank(string value) { return value == null || value.Trim().Length == 0; }
        internal static bool TryGuid(string value, out Guid guid)
        {
            try { guid = new Guid(value); return true; }
            catch (ArgumentException) { guid = Guid.Empty; return false; }
            catch (FormatException) { guid = Guid.Empty; return false; }
            catch (OverflowException) { guid = Guid.Empty; return false; }
        }
    }
}
