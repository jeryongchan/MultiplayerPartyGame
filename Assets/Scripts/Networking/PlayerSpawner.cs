using Unity.Netcode;
using UnityEngine;

namespace FriendSlop.Networking
{
    // server-side manual player spawner, living in the game scene (a networked manager object). because
    // ConnectionGate suppresses NGO's automatic player spawn, no player exists while clients
    // sit in the lobby. when this object spawns (i.e. the Game scene has network-loaded), the server spawns
    // one player object per already-connected client, and keeps spawning for any late joiners.
    //
    // this is the standard MP flow: connect (lobby), load gameplay scene, spawn players into it. the
    // player's own OnNetworkSpawn then reads its role/spawn point as before, so nothing downstream changes.
    public class PlayerSpawner : NetworkBehaviour
    {
        public override void OnNetworkSpawn()
        {
            Debug.Log($"[PlayerSpawner] OnNetworkSpawn: IsServer={IsServer}, connected={NetworkManager.ConnectedClientsIds.Count}");
            if (!IsServer)
                return;

            // spawn for everyone already connected (host + any clients who were in the lobby)
            foreach (var clientId in NetworkManager.ConnectedClientsIds)
                SpawnPlayerFor(clientId);

            // and for anyone who connects later (mid-match join)
            NetworkManager.OnClientConnectedCallback += SpawnPlayerFor;
        }

        public override void OnNetworkDespawn()
        {
            if (NetworkManager != null)
                NetworkManager.OnClientConnectedCallback -= SpawnPlayerFor;
        }

        private void SpawnPlayerFor(ulong clientId)
        {
            if (!IsServer)
                return;

            // already has a player object? (e.g. this fires twice on a race.) skip.
            if (NetworkManager.ConnectedClients.TryGetValue(clientId, out var client) && client.PlayerObject != null)
                return;

            var prefab = NetworkManager.NetworkConfig.PlayerPrefab;
            if (prefab == null)
            {
                Debug.LogError("[PlayerSpawner] No Player Prefab assigned on the NetworkManager.");
                return;
            }

            var instance = Instantiate(prefab);
            var netObj = instance.GetComponent<NetworkObject>();
            // SpawnAsPlayerObject wires it as this client's PlayerObject (so LocalClient.PlayerObject works,
            // ownership is the client, etc.), same result the auto-spawn would have produced.
            netObj.SpawnAsPlayerObject(clientId);
        }
    }
}
