using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace FriendSlop.Player
{
    // orbiting follow camera with right-click aim-down-sights. mouse drives yaw/pitch around a focus
    // point; the owning player sets itself as the target on spawn via SetTarget. the scope isn't a
    // separate camera, it's the same orbit with distance lerped to 0 (camera ends up at the eye) and
    // fov narrowed. holding right mouse blends toward that, releasing blends back.
    [RequireComponent(typeof(Camera))]
    public class ThirdPersonCamera : MonoBehaviour
    {
        [Header("Target")]
        [SerializeField]
        private Transform target;

        // fallback focus offset, used only if the target has no "Eye" child to pivot around
        [SerializeField]
        private Vector3 targetOffset = new Vector3(0f, 1.5f, 0f);

        [Header("Orbit")]
        [SerializeField]
        private float distance = 6f;

        [SerializeField]
        private float lookSensitivity = 0.15f;

        // scales mouse sensitivity while scoped (zoomed in sweeps more world per mouse-move, so slower
        // is better). 1 = same as hip, lower = less sensitive when aiming.
        [SerializeField]
        private float scopeSensitivityMultiplier = 0.4f;

        [SerializeField]
        private float minPitch = -20f;

        [SerializeField]
        private float maxPitch = 70f;

        [Header("Scope (right-click ADS)")]
        [SerializeField]
        private CanvasGroup scopeGroup;

        [SerializeField]
        private RawImage sniperOverlay; // round scope graphic, kept 1:1 by an AspectRatioFitter

        // full-screen black behind the scope graphic, fills the side bars left by the 1:1 circle on
        // wide screens so there are no transparent gaps. faded together with the scope.
        [SerializeField]
        private Graphic scopeBlackBacking;

        [SerializeField]
        private float normalFov = 60f;

        [SerializeField]
        private float scopeFov = 25f;

        // zoom level 2 (the AWP-style "click again to zoom further" step) narrows the FOV past scopeFov
        [SerializeField]
        private float scopeFovLevel2 = 12f;

        // how fast we blend in/out of the scope; higher = snappier
        [SerializeField]
        private float scopeBlendSpeed = 12f;

        [Header("Recoil")]
        // how fast accumulated recoil decays back to zero (units/sec of recoil-offset recovered).
        // higher snaps back faster; the aim recovers along the same curve since it reads the camera.
        [SerializeField]
        private float recoilRecoverSpeed = 8f;

        // live recoil offset added on top of look yaw/pitch, decayed toward zero every frame. pitch is
        // negative-up (matches _pitch -= mouseUp), so a shot kicks the view up by adding a negative pitch.
        private float _recoilPitch;
        private float _recoilYaw;

        private Camera _cam;
        private Transform _eye; // first-person anchor on the player (the "Eye" child), if present
        private NetworkPlayerController _player; // the followed player, for role-gating the scope
        private float _yaw;
        private float _pitch = 20f;
        private float _scopeBlend; // 0 = third-person, 1 = fully scoped first-person (either zoom level)
        private float _zoomFovBlend; // 0 = scopeFov, 1 = scopeFovLevel2. only matters while _scopeBlend > 0

        private void Awake() => _cam = GetComponent<Camera>();

        // sets the transform this camera orbits and follows, and locks the cursor
        public void SetTarget(Transform newTarget)
        {
            target = newTarget;
            // FP anchor, optional. search the whole subtree, not just direct children; the Eye is
            // nested a couple levels down (Visual -> Character -> Eye), so Transform.Find("Eye") (direct
            // children only) silently missed it and the camera fell back to target+targetOffset, sitting
            // ~1.5 units above the real eye. that made the scoped view see over ledges the muzzle-height
            // laser correctly could not, so sight-line and beam disagreed.
            _eye = newTarget != null ? FindDeep(newTarget, "Eye") : null;
            // the followed transform may be the visual mesh child, so search parents for the controller
            _player = newTarget != null ? newTarget.GetComponentInParent<NetworkPlayerController>() : null;

            // cursor lock is now driven per-frame in LateUpdate (UpdateCursorLock) rather than latched
            // here, so it can free up for the debug HUD / menus outside of active play and re-lock in Hunt.
        }

        // cursor is locked by default (so mouse-look works), and freed only while the player holds left
        // Alt, the escape hatch for clicking the debug HUD (Host/Start/Shutdown IMGUI buttons) without
        // the lock swallowing the click. no phase dependency: look works in every phase; you just hold
        // Alt when you need the pointer.
        private void UpdateCursorLock()
        {
            var kb = UnityEngine.InputSystem.Keyboard.current;
            bool wantFree = kb != null && kb.leftAltKey.isPressed;

            Cursor.lockState = wantFree ? CursorLockMode.None : CursorLockMode.Locked;
            Cursor.visible = wantFree;
        }

        // kick the view by adding to the recoil offset (which decays back over time). pitchUp is the
        // upward kick in degrees (positive = up); yaw is the sideways kick (caller randomizes the sign).
        // accumulates, so back-to-back shots stack before recovery catches up.
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

        private void LateUpdate()
        {
            if (target == null)
                return;

            UpdateCursorLock();

            // only steer the view while the cursor is locked (actively playing). when it's free, menus,
            // lobby, Alt held, moving the mouse to click must not drag the camera.
            Mouse mouse = Cursor.lockState == CursorLockMode.Locked ? Mouse.current : null;

            // zoom level (0 = hip, 1 = scoped, 2 = scoped further) is an AWP-style toggle owned by
            // NetworkPlayerController; right-click cycles it there, this camera just reads the result.
            // both zoom levels count as "scoped" for the blend (distance-in, sensitivity, overlay);
            // only the FOV target differs between them (via _zoomFovBlend below).
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
                // ease sensitivity down toward the scoped multiplier as we zoom in
                float sensitivity = lookSensitivity * Mathf.Lerp(1f, scopeSensitivityMultiplier, _scopeBlend);
                Vector2 delta = mouse.delta.ReadValue();
                _yaw += delta.x * sensitivity;
                _pitch -= delta.y * sensitivity;
                _pitch = Mathf.Clamp(_pitch, minPitch, maxPitch);
            }

            // scoping pulls the camera in to distance 0 (first person) and narrows the fov. fov blends
            // through 3 targets: normal -> scopeFov (zoom 1) -> scopeFovLevel2 (zoom 2).
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

            // recoil rides on top of look angles, then decays toward zero. because the shooter reads aim
            // from this camera's forward, the kick moves the actual shot direction too, so rapid fire
            // walks off-target until it recovers.
            Quaternion rotation = Quaternion.Euler(_pitch + _recoilPitch, _yaw + _recoilYaw, 0f);
            _recoilPitch = Mathf.MoveTowards(_recoilPitch, 0f, recoilRecoverSpeed * Time.deltaTime);
            _recoilYaw = Mathf.MoveTowards(_recoilYaw, 0f, recoilRecoverSpeed * Time.deltaTime);
            Vector3 focus = _eye != null ? _eye.position : target.position + targetOffset;
            transform.position = focus - rotation * Vector3.forward * currentDistance;
            transform.rotation = rotation;
        }

        // depth-first search of the whole subtree for a child by name (unlike Transform.Find, which only
        // checks direct children). used to locate the Eye anchor nested under Visual -> Character -> Eye.
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

        // drives a scope UI layer's alpha from the blend, enabling it on first use (it starts disabled
        // so nothing shows before a player spawns)
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
