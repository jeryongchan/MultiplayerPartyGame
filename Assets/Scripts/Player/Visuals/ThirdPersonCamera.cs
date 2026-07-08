using FriendSlop.Game;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace FriendSlop.Player
{
    // orbiting third-person follow camera with right-click aim-down-sights. mouse drives yaw/pitch around a
    // focus point; the owning player assigns itself as the target on spawn via SetTarget.
    //
    // first-person scope is not a separate camera: it's the same orbit with the distance lerped to 0 (camera
    // sits at the focus = the eye) and the FOV narrowed. scoping blends toward that; unscoping blends back.
    [RequireComponent(typeof(Camera))]
    public class ThirdPersonCamera : MonoBehaviour
    {
        [SerializeField]
        private Transform target;

        // fallback focus offset, used only if the target has no "Eye" child to pivot around.
        [SerializeField]
        private Vector3 targetOffset = new Vector3(0f, 1.5f, 0f);

        [SerializeField]
        private float distance = 6f;

        [SerializeField]
        private float lookSensitivity = 0.15f;

        // sensitivity scale while scoped (zoomed in sweeps more world per mouse-move, so slower). 1 = same as hip.
        [SerializeField]
        private float scopeSensitivityMultiplier = 0.4f;

        [SerializeField]
        private float minPitch = -20f;

        [SerializeField]
        private float maxPitch = 70f;

        [SerializeField]
        private CanvasGroup scopeGroup;

        [SerializeField]
        private RawImage sniperOverlay; // the round scope graphic (kept 1:1 by an AspectRatioFitter).

        // full-screen black behind the scope, filling the side bars the 1:1 circle leaves on wide screens.
        [SerializeField]
        private Graphic scopeBlackBacking;

        [SerializeField]
        private float normalFov = 60f;

        [SerializeField]
        private float scopeFov = 25f;

        [SerializeField]
        private float scopeFovLevel2 = 12f; // AWP-style "click again to zoom further" step, narrower than scopeFov.

        [SerializeField]
        private float scopeBlendSpeed = 12f; // scope blend rate; higher = snappier.

        // how fast recoil decays back to zero; higher = snaps back faster. aim recovers along the same curve
        // since aim reads the camera.
        [SerializeField]
        private float recoilRecoverSpeed = 8f;

        // live recoil offset added on top of look yaw/pitch, decayed toward zero each frame. pitch is negative-up
        // (matches _pitch -= mouseUp), so a shot kicks the view up by adding a negative pitch.
        private float _recoilPitch;
        private float _recoilYaw;

        private Camera _cam;
        private Transform _eye; // first-person anchor on the player (the "Eye" child), if present
        private NetworkPlayerController _player; // the followed player, for role-gating the scope
        private float _yaw;
        private float _pitch = 20f;
        private float _scopeBlend; // 0 = third-person, 1 = fully scoped first-person (either zoom level)
        private float _zoomFovBlend; // 0 = scopeFov, 1 = scopeFovLevel2. only matters while _scopeBlend > 0

        // the scene's single follow camera, so the player + shooter reach it without a per-frame
        // FindFirstObjectByType. set on Awake, cleared on destroy. null until the GameScene camera exists (a
        // client's player can spawn a frame or two before it).
        public static ThirdPersonCamera Instance { get; private set; }

        private void Awake()
        {
            _cam = GetComponent<Camera>();
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }

        // set the transform this camera orbits and follows.
        public void SetTarget(Transform newTarget)
        {
            target = newTarget;
            // FP anchor, optional. search the whole subtree, not just direct children: the Eye is nested
            // (Visual -> Character -> Eye), so Transform.Find("Eye") missed it and the camera fell back to
            // target+targetOffset, ~1.5 units above the real eye. that let the scoped view see over ledges the
            // muzzle-height laser couldn't, so sight-line and beam disagreed.
            _eye = newTarget != null ? FindDeep(newTarget, "Eye") : null;
            // the followed transform may be the visual mesh child, so search parents for the controller.
            _player = newTarget != null ? newTarget.GetComponentInParent<NetworkPlayerController>() : null;
            // cursor lock is driven per-frame in UpdateCursorLock, so it can free for the HUD/menus and re-lock in play.
        }

        // cursor locked by default (so mouse-look works), freed while Left Alt is held (escape hatch for
        // clicking the debug HUD) or while the local witness is drawing during the Sketch phase (they need the
        // cursor to draw/pick colours, the camera is parked at the crime scene then, so there's no mouse-look
        // to disrupt). gated to the witness so idle snipers/criminals keep a locked cursor during Sketch.
        private void UpdateCursorLock()
        {
            var kb = UnityEngine.InputSystem.Keyboard.current;
            bool altHeld = kb != null && kb.leftAltKey.isPressed;
            bool witnessSketching =
                GameFlowManager.Instance != null
                && GameFlowManager.Instance.CurrentPhase.Value == GamePhase.Sketch
                && NetworkPlayerController.Local?.Role.Value == PlayerRole.Witness;
            bool wantFree = altHeld || witnessSketching;

            Cursor.lockState = wantFree ? CursorLockMode.None : CursorLockMode.Locked;
            Cursor.visible = wantFree;
        }

        // kick the view by adding to the recoil offset (which decays back over time). pitchUp = upward kick in
        // degrees; yaw = sideways kick (caller randomizes the sign). accumulates, so back-to-back shots stack.
        public void AddRecoil(float pitchUp, float yaw)
        {
            _recoilPitch -= pitchUp; // _pitch is negative-up, so up = subtract
            _recoilYaw += yaw;
        }

        private void Start()
        {
            Vector3 angles = transform.eulerAngles;
            _yaw = angles.y;
            _pitch = angles.x;
        }

        // when set, the camera parks at this transform (pos + rotation) instead of following/orbiting, for the
        // witness's crime-scene view during Sketch. a future free-look could orbit this anchor instead.
        private Transform _fixedView;

        // park the camera at a fixed transform (crime-scene view), overriding follow/orbit.
        public void SetFixedView(Transform anchor) => _fixedView = anchor;

        // return to normal follow/orbit.
        public void ClearFixedView() => _fixedView = null;

        private void LateUpdate()
        {
            // crime-scene override: snap to the anchor and skip follow + orbit + scope. the drawing canvas (a UI
            // overlay) still draws on top, like tracing paper over a photo.
            if (_fixedView != null)
            {
                transform.SetPositionAndRotation(_fixedView.position, _fixedView.rotation);
                UpdateCursorLock();
                return;
            }

            if (target == null)
                return;

            UpdateCursorLock();

            // only steer the view while the cursor is locked (actively playing). when it's free (menus, Alt
            // held) moving the mouse to click must not drag the camera.
            Mouse mouse = Cursor.lockState == CursorLockMode.Locked ? Mouse.current : null;

            // zoom level (0 = hip, 1 = scoped, 2 = further) is an AWP-style toggle owned by the input reader;
            // this camera just reads the result. both levels count as "scoped" for the blend (distance,
            // sensitivity, overlay); only the FOV target differs (via _zoomFovBlend).
            int zoomLevel = _player != null ? _player.ZoomLevel : 0;
            _scopeBlend = Mathf.MoveTowards(
                _scopeBlend,
                zoomLevel > 0 ? 1f : 0f,
                scopeBlendSpeed * Time.deltaTime
            );
            _zoomFovBlend = Mathf.MoveTowards(
                _zoomFovBlend,
                zoomLevel >= 2 ? 1f : 0f,
                scopeBlendSpeed * Time.deltaTime
            );

            if (mouse != null)
            {
                // ease sensitivity down toward the scoped multiplier as we zoom in.
                float sensitivity = lookSensitivity * Mathf.Lerp(1f, scopeSensitivityMultiplier, _scopeBlend);
                Vector2 delta = mouse.delta.ReadValue();
                _yaw += delta.x * sensitivity;
                _pitch -= delta.y * sensitivity;
                _pitch = Mathf.Clamp(_pitch, minPitch, maxPitch);
            }

            // scoping pulls the camera in to distance 0 (first person) and narrows the FOV, blending through 3
            // targets: normal -> scopeFov (zoom 1) -> scopeFovLevel2 (zoom 2).
            float currentDistance = Mathf.Lerp(distance, 0f, _scopeBlend);
            float scopedFov = Mathf.Lerp(scopeFov, scopeFovLevel2, _zoomFovBlend);
            _cam.fieldOfView = Mathf.Lerp(normalFov, scopedFov, _scopeBlend);

            if (scopeGroup != null)
            {
                scopeGroup.gameObject.SetActive(true);
                scopeGroup.alpha = _scopeBlend;
            }
            else
            {
                SetScopeAlpha(sniperOverlay, _scopeBlend);
                SetScopeAlpha(scopeBlackBacking, _scopeBlend);
            }

            // recoil rides on top of look angles, then decays toward zero. since the shooter reads aim from
            // this camera's forward, the kick moves the shot direction too, so rapid fire walks off-target.
            Quaternion rotation = Quaternion.Euler(_pitch + _recoilPitch, _yaw + _recoilYaw, 0f);
            _recoilPitch = Mathf.MoveTowards(_recoilPitch, 0f, recoilRecoverSpeed * Time.deltaTime);
            _recoilYaw = Mathf.MoveTowards(_recoilYaw, 0f, recoilRecoverSpeed * Time.deltaTime);
            Vector3 focus = _eye != null ? _eye.position : target.position + targetOffset;
            transform.position = focus - rotation * Vector3.forward * currentDistance;
            transform.rotation = rotation;
        }

        // depth-first search of the whole subtree for a child by name (Transform.Find only checks direct
        // children). locates the Eye anchor nested under Visual -> Character -> Eye.
        private static Transform FindDeep(Transform root, string childName)
        {
            if (root.name == childName)
                return root;
            foreach (Transform child in root)
            {
                Transform found = FindDeep(child, childName);
                if (found != null)
                    return found;
            }
            return null;
        }

        // drives a scope UI layer's alpha from the blend, enabling it on first use (starts disabled so nothing
        // shows before a player spawns).
        private static void SetScopeAlpha(Graphic graphic, float alpha)
        {
            if (graphic == null)
                return;
            graphic.gameObject.SetActive(true);
            Color c = graphic.color;
            c.a = alpha;
            graphic.color = c;
        }
    }
}
