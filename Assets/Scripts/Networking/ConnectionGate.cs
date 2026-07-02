using Unity.Netcode;
using UnityEngine;

namespace FriendSlop.Networking
{
    // lives in the MainMenu scene alongside NetworkManager (both persist across the scene load). registers
    // the connection-approval callback so that connecting a client does not auto-spawn its player object;
    // standard MP practice: in the lobby you're just a connection, and the player is spawned later, once
    // the gameplay scene has loaded (see PlayerSpawner).
    //
    // the callback approves everyone (no auth yet) and sets CreatePlayerObject = false. we keep the
    // NetworkManager's Player Prefab assigned (it stays the spawn source); we just suppress the automatic
    // per-connection spawn. requires "Connection Approval" enabled on the NetworkManager.
    public class ConnectionGate : MonoBehaviour
    {
        private void Awake()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null)
            {
                Debug.LogError("[ConnectionGate] No NetworkManager.Singleton, put this in the scene with it.");
                return;
            }
            nm.ConnectionApprovalCallback = Approve;
        }

        // force a clean shutdown when leaving play mode so the UDP port is always released. without this,
        // virtual players can leave the socket bound, causing "address already in use" on the next Host
        // until the editor restarts.
        //
        // note: this is only on OnApplicationQuit, not OnDestroy. ConnectionGate lives in the MainMenu
        // scene, which is unloaded when the host loads the GameScene (LoadScene Single); running Shutdown()
        // in OnDestroy there would kill the live session mid-load. NetworkManager itself persists across
        // the load; this component does not, and must not drag networking down with it.
        private void OnApplicationQuit()
        {
            var nm = NetworkManager.Singleton;
            if (nm != null && (nm.IsClient || nm.IsServer))
                nm.Shutdown();
        }

        // approve every connection but don't create the player object; PlayerSpawner does that after the
        // Game scene loads. Pending = false means "decide now" (no async handshake).
        private void Approve(NetworkManager.ConnectionApprovalRequest request,
                             NetworkManager.ConnectionApprovalResponse response)
        {
            response.Approved = true;
            response.CreatePlayerObject = false;
            response.Pending = false;
        }
    }
}
