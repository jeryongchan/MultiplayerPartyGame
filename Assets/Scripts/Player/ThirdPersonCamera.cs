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
        private float _scopeBlend; // 0 = third-person, 1 = fully scoped first-person

        private void Awake() => _cam = GetComponent<Camera>();

        // sets the transform this camera orbits and follows, and locks the cursor
        public void SetTarget(Transform newTarget)
        {
            target = newTarget;
            _eye = newTarget != null ? newTarget.Find("Eye") : null; // FP anchor, optional
            // the followed transform may be the visual mesh child, so search parents for the controller
            _player = newTarget != null ? newTarget.GetComponentInParent<NetworkPlayerController>() : null;

            // lock cursor only once we have a player to control, so the connection HUD stays clickable
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
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

            Mouse mouse = Mouse.current;

            // blend toward scoped (1) while right mouse is held, back to hip (0) when released. done
            // before the look so this frame's sensitivity already reflects how scoped we are.
            // only Snipers carry a scope; Criminals right-clicking does nothing (the scope overlay is a
            // sniper tell, they shouldn't see it). Witness gets its own device later.
            bool canScope = _player != null && _player.Role.Value == PlayerRole.Sniper;
            bool scoping = canScope && mouse != null && mouse.rightButton.isPressed;
            _scopeBlend = Mathf.MoveTowards(
                _scopeBlend,
                scoping ? 1f : 0f,
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

            // scoping pulls the camera in to distance 0 (first person) and narrows the fov
            float currentDistance = Mathf.Lerp(distance, 0f, _scopeBlend);
            _cam.fieldOfView = Mathf.Lerp(normalFov, scopeFov, _scopeBlend);

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
