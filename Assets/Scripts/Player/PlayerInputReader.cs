using UnityEngine;

namespace FriendSlop.Player
{
    // the player's intent for one tick, sampled from local input by PlayerInputReader and folded into the
    // controller's networked InputPayload. camera-relative and role-resolved, so the controller only applies
    // the freeze rules and simulates.
    public struct PlayerIntent
    {
        public Vector3 MoveDir; // camera-relative, normalized world direction (zero if idle).
        public bool Jump;       // an edge event latched this frame.
        public bool Sprinting;
        public CharacterPose Pose; // the pose to hold this tick (scope / exotic / none).
        public Vector3 AimDir;  // camera's horizontal forward; the body faces this when scoped.
    }

    // reads the owner's keyboard/mouse into a PlayerIntent for the controller's tick. split out of the
    // controller so it stays pure movement netcode.
    //
    // two timing phases keep edge events aligned to the tick: LatchEdges runs every frame (owner's Update) to
    // catch jump / zoom-cycle / pose-key presses; Sample runs at tick time to consume those latches and read
    // held state, so predict + reconcile agree on the same input for a tick.
    public class PlayerInputReader : MonoBehaviour
    {
        // owner-only: the criminal's selected exotic pose (sticky, held until changed). number keys set it in
        // LatchEdges; Sample folds it into the intent. scope (a hunter pose) rides _zoomLevel separately and
        // the two never coexist (a criminal can't scope, a hunter has no exotic poses).
        private CharacterPose _selectedPose = CharacterPose.None;
        private bool _poseKeyPressed;
        private CharacterPose _poseKeyValue;

        // owner-only: right-click cycles hip(0) -> zoom1 -> zoom2 -> hip, AWP-style, instead of hold-to-scope.
        // both zoom levels count as "scoped" for gameplay; only the camera FOV differs (read via ZoomLevel).
        // not networked: only the owner's camera needs the exact level, and the pose already replicates.
        private int _zoomLevel;
        private bool _zoomCyclePressed;

        private bool _jumpLatched;

        public int ZoomLevel => _zoomLevel; // 0 = hip, 1 = scoped, 2 = scoped further; drives the camera FOV.

        // test hook: when set, this raw move replaces WASD on the owner (drives movement through the real input
        // path). null in normal play.
        public Vector2? MoveInputOverride { get; set; }

        // owner-only, every frame from the controller's Update: catch the edge events (jump, zoom-cycle,
        // pose-key) that a tick-rate sample would miss. role-gated (only hunters cycle zoom, only criminals pose).
        // consumed in Sample.
        public void LatchEdges(PlayerRole role)
        {
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb != null && kb.spaceKey.wasPressedThisFrame)
                _jumpLatched = true;

            // right-click cycles zoom; hunters only (a criminal's right-click stays a no-op).
            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (mouse != null && mouse.rightButton.wasPressedThisFrame && role.IsHunter())
                _zoomCyclePressed = true;

            // criminal exotic poses: number keys sticky-toggle (press to enter, same key or 0 to stand). the
            // server re-checks the role in SanitizePose so a hacked client can't pose as a hunter.
            if (kb != null && role == PlayerRole.Criminal)
            {
                if (kb.digit1Key.wasPressedThisFrame) TogglePose(CharacterPose.Handstand);
                else if (kb.digit2Key.wasPressedThisFrame) TogglePose(CharacterPose.Shoelace);
                else if (kb.digit3Key.wasPressedThisFrame) TogglePose(CharacterPose.Split);
                else if (kb.digit0Key.wasPressedThisFrame) { _poseKeyPressed = true; _poseKeyValue = CharacterPose.None; }
            }
        }

        // owner-only, at tick time from OwnerTick: consume the latched edges and read held input into a
        // PlayerIntent. resolving the latches here (not in Update) keeps predict + reconcile in agreement.
        public PlayerIntent Sample(PlayerRole role)
        {
            if (_zoomCyclePressed) // cycle hip -> zoom1 -> zoom2 -> hip.
            {
                _zoomLevel = (_zoomLevel + 1) % 3;
                _zoomCyclePressed = false;
            }

            if (_poseKeyPressed) // sticky toggle (same key again -> stand).
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
            _jumpLatched = false; // Jump is one-shot, consumed here.
            return intent;
        }

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

        // latch a pose-key edge for Sample to consume (the toggle must resolve at tick time so predict +
        // reconcile agree, so LatchEdges can't touch _selectedPose directly).
        private void TogglePose(CharacterPose pose)
        {
            _poseKeyPressed = true;
            _poseKeyValue = pose;
        }

        // the pose to send this tick: a scoping hunter is Scoped, otherwise the criminal's sticky pose. the two
        // never coexist (role-exclusive), so this precedence is unambiguous; SanitizePose re-gates by role.
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

        // the camera's horizontal forward: the aim direction the body faces when scoped.
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
