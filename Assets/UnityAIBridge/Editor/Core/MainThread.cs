using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace UnityAIBridge
{
    /// <summary>
    /// Marshals work onto the Unity main thread. Network I/O and MCP requests run on
    /// background threads; every AssetDatabase/Editor API call goes through here.
    /// </summary>
    public static class MainThread
    {
        static readonly ConcurrentQueue<Action> Queue = new ConcurrentQueue<Action>();
        static int _mainThreadId;

        public static void Init()
        {
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            EditorApplication.update -= Pump;
            EditorApplication.update += Pump;
        }

        public static bool IsMainThread
        {
            get { return _mainThreadId != 0 && Thread.CurrentThread.ManagedThreadId == _mainThreadId; }
        }

        public static void Run(Action action)
        {
            if (action == null) return;
            if (IsMainThread) { SafeInvoke(action); return; }
            Queue.Enqueue(action);
        }

        public static T Invoke<T>(Func<T> func, int timeoutMs = 30000)
        {
            if (func == null) throw new ArgumentNullException("func");
            if (IsMainThread) return func();

            var completion = new TaskCompletionSource<T>();
            Queue.Enqueue(() =>
            {
                try { completion.TrySetResult(func()); }
                catch (Exception e) { completion.TrySetException(e); }
            });

            if (!completion.Task.Wait(timeoutMs))
                throw new BridgeException("Timed out after " + timeoutMs + " ms waiting for the Unity main thread.");
            return completion.Task.GetAwaiter().GetResult();
        }

        public static void Invoke(Action action, int timeoutMs = 30000)
        {
            Invoke(() => { action(); return true; }, timeoutMs);
        }

        /// <summary>Used by batch-mode test runners that drive the editor manually.</summary>
        public static void PumpNow()
        {
            Action action;
            int guard = 0;
            while (Queue.TryDequeue(out action) && guard++ < 500)
                SafeInvoke(action);
        }

        static void Pump()
        {
            Action action;
            int guard = 0;
            while (Queue.TryDequeue(out action) && guard++ < 200)
                SafeInvoke(action);
        }

        static void SafeInvoke(Action action)
        {
            try { action(); }
            catch (Exception e)
            {
                BridgeLog.Error("mainthread", "Queued action failed: " + e.Message, e);
            }
        }
    }
}
