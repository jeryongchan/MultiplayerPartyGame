using System.Text;
using FriendSlop.Game;
using TMPro;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.UI;

namespace FriendSlop.Networking
{
    // drives the MainMenu UI: a Menu panel (Host / Join / Quit) and a Lobby panel (player list + host-only
    // Start). lives in the MainMenu scene. replaces the old IMGUI NetworkHud.
    //
    // flow: Host or Join, on connection swap Menu panel for Lobby panel, host clicks Start, network-load
    // the GameScene (PlayerSpawner then spawns everyone), GameFlowManager.StartMatchRpc begins the round.
    //
    // this is UI + session bootstrapping only; it holds no gameplay authority. the player list is rebuilt
    // from NetworkManager.ConnectedClientsIds whenever clients connect/disconnect.
    public class LobbyController : MonoBehaviour
    {
        [Header("Panels")]
        [SerializeField] private GameObject menuPanel;
        [SerializeField] private GameObject lobbyPanel;

        [Header("Menu panel")]
        [SerializeField] private Button hostButton;
        [SerializeField] private Button joinButton;
        [SerializeField] private Button quitButton;
        [SerializeField] private TMP_InputField ipInput;

        [Header("Lobby panel")]
        [SerializeField] private TMP_Text playerListText;
        [SerializeField] private TMP_Text statusText;
        [SerializeField] private Button startButton;
        [SerializeField] private Button leaveButton;

        [Header("Scene to load on start")]
        [SerializeField] private string gameSceneName = "GameScene";

        [Header("Networked lobby roster (scene NetworkObject in MainMenu)")]
        [SerializeField] private NetworkObject rosterObject;

        private void Awake()
        {
            hostButton.onClick.AddListener(OnHost);
            joinButton.onClick.AddListener(OnJoin);
            quitButton.onClick.AddListener(OnQuit);
            startButton.onClick.AddListener(OnStart);
            leaveButton.onClick.AddListener(OnLeave);
            ShowMenu();
        }

        // the roster spawns asynchronously (host spawns it after StartHost; clients receive it on connect),
        // so we can't just subscribe in OnEnable; poll for it and hook Changed once it appears.
        private LobbyRoster _roster;

        private void Update()
        {
            if (_roster == null && LobbyRoster.Instance != null)
            {
                _roster = LobbyRoster.Instance;
                _roster.Changed += RefreshLobby;
                RefreshLobby();
            }
        }

        private void OnDisable()
        {
            if (_roster != null)
                _roster.Changed -= RefreshLobby;
        }

        // menu actions

        private void OnHost()
        {
            if (!NetworkManager.Singleton.StartHost())
                return;

            // spawn the replicated roster so clients can see the player list too. it's a scene NetworkObject
            // in MainMenu; the host spawns it now (clients receive it on connect).
            if (rosterObject != null && !rosterObject.IsSpawned)
                rosterObject.Spawn();

            ShowLobby();
        }

        private void OnJoin()
        {
            // point the transport at the typed IP (defaults to localhost if blank), then connect
            var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
            if (transport != null && ipInput != null && !string.IsNullOrWhiteSpace(ipInput.text))
                transport.ConnectionData.Address = ipInput.text.Trim();

            if (NetworkManager.Singleton.StartClient())
                ShowLobby(); // the list fills in once we're actually connected (OnClientChanged)
        }

        private void OnQuit()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        // lobby actions

        // host only: network-load the gameplay scene. players spawn there (PlayerSpawner); then kick off the
        // match. we wait for the scene load to complete before starting the round so managers exist.
        private void OnStart()
        {
            var nm = NetworkManager.Singleton;
            Debug.Log($"[Lobby] OnStart: IsServer={nm.IsServer}, IsListening={nm.IsListening}, sceneMgr={(nm.SceneManager != null)}");
            if (!nm.IsServer) return;

            nm.SceneManager.OnLoadComplete += OnGameSceneLoaded;
            var status = nm.SceneManager.LoadScene(gameSceneName, UnityEngine.SceneManagement.LoadSceneMode.Single);
            Debug.Log($"[Lobby] LoadScene('{gameSceneName}') status = {status}");
        }

        // fires per client as the scene finishes; we only act once, on the server, for the game scene
        private void OnGameSceneLoaded(ulong clientId, string sceneName, UnityEngine.SceneManagement.LoadSceneMode mode)
        {
            var nm = NetworkManager.Singleton;
            Debug.Log($"[Lobby] OnLoadComplete: client={clientId}, scene='{sceneName}', local={nm.LocalClientId}");
            if (!nm.IsServer || sceneName != gameSceneName || clientId != nm.LocalClientId)
                return;
            nm.SceneManager.OnLoadComplete -= OnGameSceneLoaded;
            Debug.Log("[Lobby] Game scene loaded on server, calling StartMatchRpc");

            // managers spawned with the scene; begin the round loop (Lobby -> RoleAssign -> ...)
            if (GameFlowManager.Instance != null)
                GameFlowManager.Instance.StartMatchRpc();
        }

        private void OnLeave()
        {
            NetworkManager.Singleton.Shutdown();
            ShowMenu();
        }

        // UI state

        private void ShowMenu()
        {
            if (menuPanel != null) menuPanel.SetActive(true);
            if (lobbyPanel != null) lobbyPanel.SetActive(false);
        }

        private void ShowLobby()
        {
            if (menuPanel != null) menuPanel.SetActive(false);
            if (lobbyPanel != null) lobbyPanel.SetActive(true);
            RefreshLobby();
        }

        // rebuild the player list + host/client status from the replicated roster (visible on clients too)
        private void RefreshLobby()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null) return;

            int count = 0;
            var sb = new StringBuilder();
            if (_roster != null)
            {
                count = _roster.Players.Count;
                foreach (var id in _roster.Players)
                {
                    bool isMe = id == nm.LocalClientId;
                    bool isHost = id == NetworkManager.ServerClientId;
                    sb.Append("- ");
                    sb.Append(isMe ? "You" : $"Player #{id}");
                    if (isHost) sb.Append(" (host)");
                    sb.AppendLine();
                }
            }
            if (playerListText != null)
                playerListText.text = sb.ToString();

            // host sees Start; clients see a waiting message
            bool isServer = nm.IsServer;
            if (startButton != null) startButton.gameObject.SetActive(isServer);
            if (statusText != null)
                statusText.text = isServer ? $"Players: {count}" : "Waiting for host to start...";
        }
    }
}
