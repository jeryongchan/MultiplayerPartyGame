using UnityEngine;

namespace FriendSlop.Player
{
    // the player's intent for one tick, sampled from local input by PlayerInputReader and folded
    // into the controller's networked InputPayload. camera-relative and already role-resolved, so the controller
    // only has to apply the freeze rules and simulate.
    public struct PlayerIntent
    {
        public Vector3 MoveDir; // camera-relative, normalized world direction (Vector3.zero if idle)
        public bool Jump;       // an edge event latched this frame
        public bool Sprinting;
        public CharacterPose Pose; // the deliberate pose to hold this tick (scope / exotic / none)
        public Vector3 AimDir;  // camera's horizontal forward; the body faces this when scoped
    }

    // reads the owner's local keyboard/mouse and turns it into a PlayerIntent for the controller's
    // tick. split out of NetworkPlayerController so the controller is pure movement netcode: this
    // component is the single home for "what did the player press."
    //
    // two timing phases, kept coordinated with the tick (the whole reason edge events are latched, not read
    // live): LatchEdges runs every frame in the owner's Update to catch jump / zoom-cycle /
    // pose-key presses; Sample runs at tick time to consume those latches and read the held state,
    // so predict + reconcile agree on the same input for a tick.
    public class PlayerInputReader : MonoBehaviour
    {
        // owner-only: the criminal's currently-selected exotic pose (sticky, held until changed). number
        // keys set it in Update; Sample folds it into the intent's Pose. scope (a hunter pose) is chosen
        // separately via _zoomLevel and doesn't touch this; the two never coexist (a criminal can't scope,
        // a hunter has no exotic poses), so the single CurrentPose enum resolves cleanly in Simulate.
        private CharacterPose _selectedPose = CharacterPose.None;
        private bool _poseKeyPressed;
        private CharacterPose _poseKeyValue;

        // owner-only: right-click cycles hip(0) -> zoom1(1) -> zoom2(2) -> hip(0), AWP-style toggle
        // instead of hold-to-scope. zoom level 1 and 2 both count as "scoped" for gameplay (movement
        // facing, aim animation, gun grip); only the camera FOV differs between them, read directly
        // by ThirdPersonCamera via ZoomLevel. not networked: only the owner's own camera needs the
        // exact level, and Scoped (in StatePayload) already replicates the gameplay-relevant bool.
        private int _zoomLevel;
        private bool _zoomCyclePressed;

        private bool _jumpLatched;

        // owner-only: 0 = hip, 1 = scoped, 2 = scoped further in. drives ThirdPersonCamera's FOV.
        public int ZoomLevel => _zoomLevel;

        // optional test hook: when set (e.g. by AutoStrafe), this raw move input replaces WASD on the
        // owner. stays null in normal play. lets a test drive movement through the real input path.
        public Vector2? MoveInputOverride { get; set; }

        // owner-only, called every frame from the controller's Update: catch the edge events (jump, zoom-cycle,
        // pose-key) that would be missed if only sampled at tick rate. gated by the player's replicated role,
        // only hunters cycle zoom, only criminals pick exotic poses. the latches are consumed in Sample.
        public void LatchEdges(PlayerRole role)
        {
            // movement is handled in Sample; jump is an edge event so it's latched here every frame
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb != null && kb.spaceKey.wasPressedThisFrame)
                _jumpLatched = true;

            // right-click cycles the zoom level (edge event, like Jump). only Snipers can scope,
            // a Criminal's right-click stays a no-op, same gating ReadScoped() already enforced.
            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (mouse != null && mouse.rightButton.wasPressedThisFrame && role.IsHunter())
                _zoomCyclePressed = true;

            // criminal exotic poses: number keys sticky-toggle a pose (press to enter, press the same key
            // or 0 to stand). edge-caught here, consumed in Sample -> intent.Pose. only the Criminal poses;
            // the server re-checks the role in Simulate so a hacked client can't pose as a hunter (and can't
            // scope-via-pose either). 0 clears back to None. (RolePickerDebug moved to F1/F2 so the number
            // row is free here; swap to a radial menu when that picker is deleted.)
            if (kb != null && role == PlayerRole.Criminal)
            {
                if (kb.digit1Key.wasPressedThisFrame) TogglePose(CharacterPose.Handstand);
                else if (kb.digit2Key.wasPressedThisFrame) TogglePose(CharacterPose.Shoelace);
                else if (kb.digit3Key.wasPressedThisFrame) TogglePose(CharacterPose.Split);
                else if (kb.digit0Key.wasPressedThisFrame) { _poseKeyPressed = true; _poseKeyValue = CharacterPose.None; }
            }
        }

        // owner-only, called at tick time from OwnerTick: consume the latched edges and read the held input into
        // a PlayerIntent for this tick. resolving the latches here (not in Update) is what keeps
        // predict + reconcile in agreement. jump is one-shot, consumed by this call.
        public PlayerIntent Sample(PlayerRole role)
        {
            // consume the right-click edge caught in LatchEdges: cycle hip -> zoom1 -> zoom2 -> hip
            if (_zoomCyclePressed)
            {
                _zoomLevel = (_zoomLevel + 1) % 3;
                _zoomCyclePressed = false;
            }

            // consume the pose-key edge caught in LatchEdges: sticky toggle (same key again returns to stand)
            if (_poseKeyPressed)
            {
                _selectedPose = _selectedPose == _poseKeyValue ? CharacterPose.None : _poseKeyValue;
                _poseKeyPressed = false;
            }

            Vector2 rawMove = MoveInputOverride ?? ReadMoveInput();
            var intent = new PlayerIntent
            {
                MoveDir = CameraRelativeDirection(rawMove),
                Jump = _jumpLatched,
                Sprinting = ReadSprintInput(),
                Pose = ResolvePose(role),
                AimDir = CameraForward(),
            };
            _jumpLatched = false;
            return intent;
        }

        // input helpers

        private static Vector3 CameraRelativeDirection(Vector2 input)
        {
            if (input.sqrMagnitude < 0.001f)
                return Vector3.zero;

            Transform cam = Camera.main != null ? Camera.main.transform : null;
            if (cam == null)
                return new Vector3(input.x, 0f, input.y);

            Vector3 forward = cam.forward;
            forward.y = 0f;
            forward.Normalize();
            Vector3 right = cam.right;
            right.y = 0f;
            right.Normalize();
            return (forward * input.y + right * input.x).normalized;
        }

        private static Vector2 ReadMoveInput()
        {
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb == null)
                return Vector2.zero;
            float x = (kb.dKey.isPressed ? 1f : 0f) - (kb.aKey.isPressed ? 1f : 0f);
            float y = (kb.wKey.isPressed ? 1f : 0f) - (kb.sKey.isPressed ? 1f : 0f);
            return new Vector2(x, y);
        }

        // owner-only: latch a pose-key edge for Sample to consume as a sticky toggle. (LatchEdges can't touch
        // _selectedPose directly, the toggle must resolve at tick time so predict + reconcile agree.)
        private void TogglePose(CharacterPose pose)
        {
            _poseKeyPressed = true;
            _poseKeyValue = pose;
        }

        // owner-only: the pose to send this tick. a hunter scoping in (zoom > 0) is the Scoped pose; otherwise
        // it's the criminal's sticky-selected exotic pose (None if unselected). scope and exotic poses never
        // coexist (role-exclusive), so this simple precedence is unambiguous. SanitizePose re-gates by role.
        private CharacterPose ResolvePose(PlayerRole role)
        {
            if (role.IsHunter() && _zoomLevel > 0)
                return CharacterPose.Scoped;
            return _selectedPose;
        }

        private bool ReadSprintInput()
        {
            var kb = UnityEngine.InputSystem.Keyboard.current;
            return kb != null && kb.shiftKey.isPressed;
        }

        // the camera's horizontal forward, the aim direction the body faces when scoped
        private static Vector3 CameraForward()
        {
            Transform cam = Camera.main != null ? Camera.main.transform : null;
            if (cam == null)
                return Vector3.forward;
            Vector3 forward = cam.forward;
            forward.y = 0f;
            return forward.normalized;
        }
    }
}
