using System;
using Services;
using UnityEngine;

namespace SS2Revive
{
    /// <summary>
    /// Shows one native bottom-screen prompt after the player reaches the lobby. The network check
    /// may finish earlier or later; its result is held until the lobby UI actually exists so this
    /// never flashes over the main menu, loading screen or a playable level.
    /// </summary>
    internal sealed class UgcConnectionNotice : MonoBehaviour
    {
        private bool _requested;
        private bool _shown;
        private bool? _connected;

        internal static void Install(GameObject host)
        {
            if (host != null && host.GetComponent<UgcConnectionNotice>() == null)
                host.AddComponent<UgcConnectionNotice>();
        }

        private void Update()
        {
            if (_shown || !IsInLobby()) return;

            if (!_requested)
            {
                _requested = true;
                CommunityCatalogClient.CheckConnection(connected => _connected = connected);
                return;
            }

            if (!_connected.HasValue) return;

            _shown = true;
            TerminalMessage.Show(_connected.Value
                    ? "Connected to UGC."
                    : "Not connected to UGC.",
                isWarning: !_connected.Value,
                seconds: 5f);
        }

        private static bool IsInLobby()
        {
            try
            {
                var shell = Shell.Instance;
                if (shell == null) return false;
                var levels = shell.GetLevelService();
                return levels != null && levels.IsInLevel() && levels.IsInLobby();
            }
            catch (Exception)
            {
                // Shell and its services are assembled over several startup frames. Waiting for
                // the next one is expected and avoids turning normal boot ordering into log spam.
                return false;
            }
        }
    }
}
