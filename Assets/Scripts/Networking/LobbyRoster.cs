using System;
using Unity.Netcode;

namespace FriendSlop.Networking
{
    // replicated list of who's in the lobby, so both host and clients can show the same player list.
    // NetworkManager.ConnectedClientsIds is server-only (a client sees nothing there), so the lobby UI
    // can't read it directly; instead the server maintains this NetworkList and every machine renders
    // from it.
    //
    // lives on a scene NetworkObject in the MainMenu scene. the host spawns it right after StartHost
    // (LobbyController); clients receive it on connect. the server keeps it in sync with
    // connect/disconnect events; Changed lets the UI refresh reactively.
    public class LobbyRoster : NetworkBehaviour
    {
        public static LobbyRoster Instance { get; private set; }

        // connected client ids, server-written, replicated to all. client 0 (the host) is always first in.
        public readonly NetworkList<ulong> Players = new NetworkList<ulong>(
            writePerm: NetworkVariableWritePermission.Server);

        // fires on every machine whenever the roster changes (for the lobby UI to re-render)
        public event Action Changed;

        private void Awake() => Instance = this;

        public override void OnNetworkSpawn()
        {
            Players.OnListChanged += OnListChanged;

            if (IsServer)
            {
                // seed with everyone already connected (the host, plus any client that beat this spawn)
                foreach (var id in NetworkManager.ConnectedClientsIds)
                    if (!Contains(id))
                        Players.Add(id);

                NetworkManager.OnClientConnectedCallback += OnConnected;
                NetworkManager.OnClientDisconnectCallback += OnDisconnected;
            }

            Changed?.Invoke();
        }

        public override void OnNetworkDespawn()
        {
            Players.OnListChanged -= OnListChanged;
            if (IsServer && NetworkManager != null)
            {
                NetworkManager.OnClientConnectedCallback -= OnConnected;
                NetworkManager.OnClientDisconnectCallback -= OnDisconnected;
            }
        }

        public override void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
            base.OnDestroy();
        }

        private void OnConnected(ulong id)
        {
            if (IsServer && !Contains(id))
                Players.Add(id);
        }

        private void OnDisconnected(ulong id)
        {
            if (!IsServer)
                return;
            for (int i = 0; i < Players.Count; i++)
                if (Players[i] == id) { Players.RemoveAt(i); break; }
        }

        private void OnListChanged(NetworkListEvent<ulong> _) => Changed?.Invoke();

        private bool Contains(ulong id)
        {
            for (int i = 0; i < Players.Count; i++)
                if (Players[i] == id) return true;
            return false;
        }
    }
}
