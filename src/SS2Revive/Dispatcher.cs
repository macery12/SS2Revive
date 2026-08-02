using System;
using System.Collections.Generic;
using UnityEngine;

namespace SS2Revive
{
    /// <summary>
    /// Runs work on a later frame on the Unity main thread.
    ///
    /// This exists because of a real ordering hazard. Bossa's calls into their backend were
    /// asynchronous, so every completion callback landed some frames after the call that started
    /// it. When we replace one of those calls with a local answer, the callback fires *inside*
    /// the caller's stack instead - and startup code that was written assuming a gap breaks.
    ///
    /// Concretely, Shell.OnStart is:
    ///     _platformService.InitPlatforms();   // Steam login -> AuthenticateUser
    ///     _analyticsService.Initialise(...);  // Analytics.Init
    /// A synchronous auth completion reaches Analytics.RegisterTelemetryServer before
    /// Analytics.Init has run, and dereferences a null telemetry service.
    ///
    /// So: keep our answers local, but keep them late.
    /// </summary>
    internal sealed class Dispatcher : MonoBehaviour
    {
        private static Dispatcher _instance;

        private readonly List<Action> _pending = new List<Action>();
        private readonly List<Action> _running = new List<Action>();

        internal static void Install(GameObject host)
        {
            if (_instance == null)
                _instance = host.AddComponent<Dispatcher>();
        }

        /// <summary>Queues work for the next frame. Runs inline if the dispatcher is missing.</summary>
        internal static void NextFrame(Action action)
        {
            if (action == null)
                return;

            if (_instance == null)
            {
                Plugin.Log.LogWarning("Dispatcher not installed; running inline.");
                action();
                return;
            }

            lock (_instance._pending)
                _instance._pending.Add(action);
        }

        private void Update()
        {
            // Before the deferred queue, and every frame regardless of whether there is any: game
            // traffic arriving a frame late is latency the player feels, and Steam holds packets
            // until they are read.
            if (Plugin.SteamTransport.Value)
                SteamTransport.Pump();

            // Both of these are main-thread-only Steam work that the backend cannot do for itself
            // - it answers on a worker thread, and it was constructed before Steam existed. Both
            // stop costing anything as soon as they have their answer.
            LocalBackendHost.EnsureLocalIdentity();
            SteamPeers.Tick(Time.realtimeSinceStartup);

            lock (_pending)
            {
                if (_pending.Count == 0)
                    return;

                _running.AddRange(_pending);
                _pending.Clear();
            }

            for (var i = 0; i < _running.Count; i++)
            {
                try
                {
                    _running[i]();
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError("Deferred work threw: " + ex);
                }
            }

            _running.Clear();
        }
    }
}
