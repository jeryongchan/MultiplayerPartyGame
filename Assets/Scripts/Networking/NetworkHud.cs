using FriendSlop.Game;
using Unity.Netcode;
using UnityEngine;

namespace FriendSlop.Networking
{
    // debug HUD to start a session: Host/Client/Server buttons before connecting, status line after.
    // throwaway until there's a real lobby UI.
    public class NetworkHud : MonoBehaviour
    {
        // clean shutdown on exit so the UDP port (7777) is released. otherwise virtual players can
        // leave the socket bound and the next Host hits "address already in use" until an editor restart.
        private void OnApplicationQuit() => ShutdownIfRunning();
        private void OnDestroy() => ShutdownIfRunning();

        private static void ShutdownIfRunning()
        {
            var nm = NetworkManager.Singleton;
            if (nm != null && (nm.IsClient || nm.IsServer))
                nm.Shutdown();
        }

        private void OnGUI()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null) return;

            GUILayout.BeginArea(new Rect(10, 10, 220, 200));

            // before connecting: offer the three start modes
            if (!nm.IsClient && !nm.IsServer)
            {
                if (GUILayout.Button("Host (server + client)"))
                {
                    bool ok = nm.StartHost();
                    Debug.Log($"[NetworkHud] StartHost() returned {ok}");
                }
                if (GUILayout.Button("Client"))
                {
                    bool ok = nm.StartClient();
                    Debug.Log($"[NetworkHud] StartClient() returned {ok}");
                }
                if (GUILayout.Button("Server (headless)"))
                {
                    bool ok = nm.StartServer();
                    Debug.Log($"[NetworkHud] StartServer() returned {ok}");
                }
            }
            else
            {
                // connected: show role and connection count
                string mode = nm.IsHost ? "Host" : nm.IsServer ? "Server" : "Client";
                GUILayout.Label($"Mode: {mode}");
                GUILayout.Label($"Connected clients: {nm.ConnectedClientsList.Count}");

                // host-only: start the match (Lobby -> RoleAssign). only shows while still in Lobby, so
                // it disappears once the loop is running. the RPC is server-gated, so a client can't fire it.
                var flow = GameFlowManager.Instance;
                if (nm.IsServer && flow != null && flow.CurrentPhase.Value == GamePhase.Lobby)
                {
                    if (GUILayout.Button("Start Match"))
                        flow.StartMatchRpc();
                }
                else if (flow != null)
                {
                    GUILayout.Label($"Phase: {flow.CurrentPhase.Value}");
                }

                if (GUILayout.Button("Shutdown")) nm.Shutdown();
            }

            GUILayout.EndArea();
        }
    }
}
