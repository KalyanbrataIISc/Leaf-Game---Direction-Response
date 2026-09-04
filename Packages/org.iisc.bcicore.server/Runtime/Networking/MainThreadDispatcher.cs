// ============================================================================
//  MainThreadDispatcher.cs — Marshals callbacks from background threads to
//  the Unity main thread.
//
//  Usage:
//    MainThreadDispatcher.Enqueue(() => someUnityCallback());
//
//  A persistent singleton MonoBehaviour (DontDestroyOnLoad) drains the queue
//  each Update(). Place one instance in your bootstrap scene or let the first
//  call to Enqueue() auto-create it.
// ============================================================================
using System;
using System.Collections.Concurrent;
using UnityEngine;

namespace BciCore
{
    [DefaultExecutionOrder(-10000)]  // run before any other MonoBehaviour
    public sealed class MainThreadDispatcher : MonoBehaviour
    {
        static MainThreadDispatcher _instance;
        static readonly ConcurrentQueue<Action> _queue = new ConcurrentQueue<Action>();

        // ── Auto-creation ─────────────────────────────────────────────────────
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Initialize()
        {
            if (_instance != null) return;
            var go = new GameObject("[BciCore] MainThreadDispatcher");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<MainThreadDispatcher>();
        }

        void Update()
        {
            while (_queue.TryDequeue(out Action action))
            {
                try { action(); }
                catch (Exception e) { Debug.LogException(e); }
            }
        }

        /// <summary>
        /// Enqueue an action to run on the Unity main thread in the next Update().
        /// Safe to call from any thread.
        /// </summary>
        public static void Enqueue(Action action)
        {
            if (action == null) return;
            _queue.Enqueue(action);
        }

        void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }
    }
}
