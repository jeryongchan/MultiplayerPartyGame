using System.Collections.Generic;
using FriendSlop.Characters;
using FriendSlop.Game;
using FriendSlop.Player;
using Unity.Netcode;
using UnityEngine;

namespace FriendSlop.Sketch
{
    // builds the witness's "crime scene": a flashback lineup of the criminals, shown during the Sketch
    // phase. the figures are local, non-networked clones (the replicate-data-build-visual-locally pattern
    // used by the crowd and hit markers): we read each live criminal player's replicated
    // PlayerAppearance and paint a static figure with it, so the lineup is a fair likeness
    // of the actual criminals (who are elsewhere on the street) without spawning networked objects.
    //
    // rather than render the room to a texture, we simply move the witness's camera into the room for the
    // duration of Sketch (via ThirdPersonCamera.SetFixedView) and move it back after. the
    // drawing canvas is a UI overlay that draws on top, like tracing paper over a photo. simpler than a
    // RenderTexture plus isolated layer, and a natural base for future free-look. the room sits far off-map
    // so the camera, once parked there, sees only the lineup, never the live street.
    public class CrimeSceneView : MonoBehaviour
    {
        // prefab of a single static crime-scene figure: a character mesh with a CharacterAppearanceApplier,
        // no networking/controller.
        [SerializeField]
        private GameObject figurePrefab;

        // where each criminal figure stands in the lineup. supports up to this many criminals.
        [SerializeField]
        private Transform[] lineupSpots;

        // the camera parks here (position + rotation) during Sketch to view the lineup
        [SerializeField]
        private Transform cameraAnchor;

        // catalog used to paint the figures. must match the one players/NPCs use.
        [SerializeField]
        private CharacterAppearanceCatalog catalog;

        private readonly List<GameObject> _figures = new();
        private bool _built;
        private ThirdPersonCamera _witnessCamera;

        private void OnEnable()
        {
            TrySubscribe();
            Apply(GameFlowManager.Instance != null ? GameFlowManager.Instance.CurrentPhase.Value : GamePhase.Lobby);
        }

        private void OnDisable()
        {
            if (GameFlowManager.Instance != null)
                GameFlowManager.Instance.PhaseChanged -= Apply;
        }

        private void Start() => TrySubscribe();

        private void TrySubscribe()
        {
            if (GameFlowManager.Instance == null)
                return;
            GameFlowManager.Instance.PhaseChanged -= Apply; // idempotent
            GameFlowManager.Instance.PhaseChanged += Apply;
        }

        private void Apply(GamePhase phase)
        {
            bool sketching = phase == GamePhase.Sketch
                && NetworkPlayerController.Local?.Role.Value == PlayerRole.Witness;

            if (sketching && !_built)
            {
                BuildLineup();
                MoveCameraToRoom();
            }
            else if (!sketching && _built)
            {
                RestoreCamera();
                TearDown();
            }
        }

        // spawn one static figure per criminal, painted with that criminal's replicated appearance
        private void BuildLineup()
        {
            if (figurePrefab == null || catalog == null)
            {
                Debug.LogWarning("[CrimeSceneView] figurePrefab or catalog not assigned, can't build lineup.");
                return;
            }

            int spot = 0;
            foreach (var kv in NetworkManager.Singleton.SpawnManager.SpawnedObjects)
            {
                var netObj = kv.Value;
                if (netObj == null || !netObj.TryGetComponent(out NetworkPlayerController pc))
                    continue;
                if (pc.Role.Value != PlayerRole.Criminal)
                    continue;
                if (spot >= (lineupSpots?.Length ?? 0))
                    break; // out of lineup positions

                Transform where = lineupSpots[spot++];
                var figure = Instantiate(figurePrefab, where.position, where.rotation);
                var applier = figure.GetComponent<CharacterAppearanceApplier>()
                    ?? figure.GetComponentInChildren<CharacterAppearanceApplier>();
                if (applier != null)
                {
                    applier.SetCatalog(catalog);
                    applier.Apply(pc.Appearances.Appearance.Value);
                }
                _figures.Add(figure);
            }

            _built = true;
        }

        private void MoveCameraToRoom()
        {
            if (cameraAnchor == null)
                return;
            _witnessCamera = ThirdPersonCamera.Instance;
            if (_witnessCamera != null)
                _witnessCamera.SetFixedView(cameraAnchor);
        }

        private void RestoreCamera()
        {
            if (_witnessCamera != null)
            {
                _witnessCamera.ClearFixedView();
                _witnessCamera = null;
            }
        }

        private void TearDown()
        {
            foreach (var f in _figures)
                if (f != null)
                    Destroy(f);
            _figures.Clear();
            _built = false;
        }
    }
}
