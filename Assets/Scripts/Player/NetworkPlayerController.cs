using FriendSlop.Characters;
using FriendSlop.Game;
using Unity.Netcode;
using UnityEngine;

namespace FriendSlop.Player
{
    // client prediction. flow per network tick:
    //   owner:   sample input -> Simulate() locally -> buffer input -> send to server.
    //   server:  receive input -> Simulate() with the same code -> publish authoritative state.
    //   owner:   when authoritative state arrives, snap to it and replay buffered inputs newer than
    //            the server's tick, so we don't lose moves the server hasn't seen.
    //   remote:  non-owners interpolate between buffered snapshots (~interpolationDelay behind).
    [RequireComponent(typeof(CharacterController))]
    public class NetworkPlayerController : NetworkBehaviour
    {
        [SerializeField]
        private float moveSpeed = 5f;

        [SerializeField]
        private float sprintSpeed = 8.5f;

        [SerializeField]
        private float gravity = -20f;

        [SerializeField]
        private float turnSpeed = 720f;

        [SerializeField]
        private float jumpHeight = 1.5f;

        [SerializeField]
        private float reconcileThreshold = 0.1f;

        // how far in the past remotes are rendered. must be >= ~1 tick so the buffer holds a snapshot
        // on both sides of the render time to lerp between; the ring buffer spans a few ticks of jitter.
        [SerializeField]
        private float interpolationDelay = 0.1f;

        // the visual mesh, a child with no CharacterController. the root steps at tick rate; the mesh
        // render-interpolates between the owner's previous and current predicted tick, so the owner sees
        // smooth motion at full framerate. the camera follows this.
        [SerializeField]
        private Transform visual;

        [Header("Appearance")]
        // paints the modular character mesh from the replicated PlayerAppearance. lives on the Character child.
        [SerializeField]
        private CharacterAppearanceApplier appearanceApplier;

        // shared lookup table mapping appearance indices to meshes. must be the same asset on every machine
        // (it's a project asset, so it is). the server rolls indices against it; every client resolves them.
        [SerializeField]
        private CharacterAppearanceCatalog appearanceCatalog;

        private const int BufferSize = 256;

        private CharacterController _controller;

        // visual render-interp (simulate path): the two most recent tick poses + the latest tick's time.
        // the mesh is drawn Lerp(prev, curr, timeSinceTick / tickDt) each frame. used by the owner and
        // the host's copy of a remote, not pure remotes (those use the _snap* snapshots).
        private Vector3 _prevVisualPos,
            _currVisualPos;
        private Quaternion _prevVisualRot,
            _currVisualRot;
        private float _lastTickTime;

        // authoritative state, server-write, read by everyone.
        private readonly NetworkVariable<StatePayload> _authoritativeState =
            new NetworkVariable<StatePayload>(writePerm: NetworkVariableWritePermission.Server);

        // this player's role, written by the server (from RoleRegistry) and readable on every copy.
        // composition over inheritance: role is data, not a subclass; ability components (NetworkShooter,
        // future WitnessBeam / CriminalPoses) gate themselves on this instead of a SniperController : Player
        // tree. owner reads it to gate input locally; server reads it to enforce authoritatively.
        public readonly NetworkVariable<PlayerRole> Role =
            new NetworkVariable<PlayerRole>(writePerm: NetworkVariableWritePermission.Server);

        // this player's randomized look, rolled once by the server on spawn and replicated to every copy as
        // a small index struct (never mesh data). each machine paints its own character mesh from it via
        // the shared catalog. server-write like Role; read and applied by all.
        public readonly NetworkVariable<PlayerAppearance> Appearance =
            new NetworkVariable<PlayerAppearance>(writePerm: NetworkVariableWritePermission.Server);

        // owner-side ring buffers, indexed by tick % BufferSize.
        private readonly InputPayload[] _inputBuffer = new InputPayload[BufferSize];
        private readonly StatePayload[] _stateBuffer = new StatePayload[BufferSize];

        // mutable sim state the owner predicts forward; the server keeps its own copy in _authoritativeState.
        private float _verticalVelocity;
        private bool _jumpLatched;
        private int _currentTick;
        private float _lastSimulatedSpeed;
        private bool _lastSimulatedScoped;

        // owner-only: right-click cycles hip(0) -> zoom1(1) -> zoom2(2) -> hip(0), AWP-style toggle
        // instead of hold-to-scope. zoom level 1 and 2 both count as "scoped" for gameplay (movement
        // facing, aim animation, gun grip); only the camera FOV differs between them, read directly
        // by ThirdPersonCamera via ZoomLevel. not networked: only the owner's own camera needs the
        // exact level, and Scoped (in StatePayload) already replicates the gameplay-relevant bool.
        private int _zoomLevel;
        private bool _zoomCyclePressed;

        // owner-only: 0 = hip, 1 = scoped, 2 = scoped further in. drives ThirdPersonCamera's FOV.
        public int ZoomLevel => _zoomLevel;

        // optional test hook: when set (e.g. by AutoStrafe), this raw move input replaces WASD on the
        // owner. stays null in normal play. lets a test drive movement through the real input path.
        public Vector2? MoveInputOverride { get; set; }

        // the current horizontal speed of the character. works on owner, server, and pure remotes.
        public float CurrentSpeed
        {
            get
            {
                bool iSimulateThis = IsOwner || IsServer;
                if (iSimulateThis)
                {
                    // for owner/server, we use the intended speed from the last simulation tick.
                    // CharacterController.velocity is unreliable in frame-based Update when
                    // movement happens in fixed-interval Ticks.
                    return _lastSimulatedSpeed;
                }
                else
                {
                    // remote interpolation
                    if (_snapFrom == null || _snapTo == null) return 0f;
                    Vector3 displacement = _snapTo.Value.State.Position - _snapFrom.Value.State.Position;
                    displacement.y = 0f;
                    float timeSpan = _snapTo.Value.ArrivalTime - _snapFrom.Value.ArrivalTime;
                    if (timeSpan <= 0.001f) return 0f;
                    return displacement.magnitude / timeSpan;
                }
            }
        }

        // true if the character is actively sprinting (moving faster than walking speed). works on
        // owner, server, and pure remotes.
        public bool IsSprinting => CurrentSpeed > (moveSpeed + 0.1f);

        // true while this player is scoped in (aiming). works on owner, server, and pure remotes;
        // remotes read it from the replicated StatePayload, same as position/rotation.
        public bool IsScoped
        {
            get
            {
                bool iSimulateThis = IsOwner || IsServer;
                if (iSimulateThis)
                    return _lastSimulatedScoped;

                return _snapTo?.State.Scoped ?? false;
            }
        }

        // remote entity-interpolation: keep the two most recent snapshots. each frame, render the
        // mesh at (now - interpolationDelay) by lerping between them, ~one tick in the past, so
        // the "to" snapshot has already arrived and we never extrapolate. a snapshot = an authoritative
        // state plus the local time it arrived (needed for the time-based lerp).
        private struct TimedSnapshot
        {
            public StatePayload State;
            public float ArrivalTime;
        }

        private TimedSnapshot? _snapFrom;
        private TimedSnapshot? _snapTo;

        private void Awake() => _controller = GetComponent<CharacterController>();

        private float TickDt =>
            NetworkManager != null ? 1f / NetworkManager.NetworkConfig.TickRate : 1f / 30f;

        public override void OnNetworkSpawn()
        {
            if (IsServer)
            {
                var role = RoleRegistry.Instance != null
                    ? RoleRegistry.Instance.GetRole(OwnerClientId)
                    : PlayerRole.Sniper;
                Role.Value = role;
                var spawnPos = SpawnPointManager.Instance != null
                    ? SpawnPointManager.Instance.GetSpawnPoint(role)
                    : DebugSpawnPosition((int)OwnerClientId);
                TeleportTo(spawnPos);
                _authoritativeState.Value = CaptureState(0);

                // roll a look once, server-side, seeded by client id so a given player is stable across a
                // re-spawn this session. replicated via the NetworkVariable; every copy paints from it.
                if (appearanceCatalog != null)
                {
                    var rng = new System.Random(unchecked((int)OwnerClientId * 73856093) ^ (int)System.DateTime.Now.Ticks);
                    Appearance.Value = CharacterAppearanceApplier.Roll(appearanceCatalog, rng);
                }
            }

            if (IsOwner)
            {
                // seed both interp poses to spawn so the mesh doesn't lerp in from the origin
                _prevVisualPos = _currVisualPos = transform.position;
                _prevVisualRot = _currVisualRot = transform.rotation;
                _lastTickTime = Time.time;

                var cam = Object.FindFirstObjectByType<ThirdPersonCamera>();
                if (cam != null)
                    cam.SetTarget(visual != null ? visual : transform); // follow the smoothed mesh
            }

            // a pure remote copy never simulates, it only displays interpolated snapshots. disable the
            // controller so it doesn't fight direct transform writes, and seed with current authoritative state.
            if (!IsOwner && !IsServer)
            {
                _controller.enabled = false;
                RecordSnapshot(_authoritativeState.Value);
            }

            _authoritativeState.OnValueChanged += OnAuthoritativeStateChanged;

            // paint the character mesh from the replicated appearance, and repaint if it changes later
            if (appearanceApplier != null)
            {
                if (appearanceCatalog != null)
                    appearanceApplier.SetCatalog(appearanceCatalog);
                Appearance.OnValueChanged += OnAppearanceChanged;
                // late joiners (and the server itself) already have a value here, apply it now. it may be
                // uninitialized (default) on the very first server frame; harmless, the OnValueChanged repaints.
                if (Appearance.Value.IsValid)
                    appearanceApplier.Apply(Appearance.Value);
            }

            NetworkManager.NetworkTickSystem.Tick += OnNetworkTick;
        }

        public override void OnNetworkDespawn()
        {
            _authoritativeState.OnValueChanged -= OnAuthoritativeStateChanged;
            Appearance.OnValueChanged -= OnAppearanceChanged;
            if (NetworkManager != null && NetworkManager.NetworkTickSystem != null)
                NetworkManager.NetworkTickSystem.Tick -= OnNetworkTick;
        }

        // runs every frame on every copy, smooths the mesh between tick updates
        private void Update()
        {
            // every copy steps at tick rate (choppy), so every copy interpolates. the split is whether
            // this copy simulates: if so (owner predicts / server computes) we have fresh tick poses to
            // interpolate between; if not (remote, only gets snapshots) we interpolate from delayed
            // snapshots in the past.
            bool iSimulateThis = IsOwner || IsServer;
            if (!iSimulateThis)
            {
                InterpolateTransformDelayed();
                return;
            }
            // only the owner reads the keyboard; the server's copy of a remote already got its input via
            // RPC, so here it just renders.
            if (IsOwner)
            {
                // movement is in OwnerTick; jump is an edge event so latch it here
                var kb = UnityEngine.InputSystem.Keyboard.current;
                if (kb != null && kb.spaceKey.wasPressedThisFrame)
                    _jumpLatched = true;

                // right-click cycles the zoom level (edge event, like Jump). only Snipers can scope,
                // a Criminal's right-click stays a no-op, same gating ReadScoped() already enforced.
                var mouse = UnityEngine.InputSystem.Mouse.current;
                if (mouse != null && mouse.rightButton.wasPressedThisFrame && Role.Value.IsHunter())
                    _zoomCyclePressed = true;
            }
            InterpolateTransform();
        }

        // smooths the visual child for a copy we simulate (owner, or host's copy of a remote), using our
        // own two latest tick poses. the root steps at tick rate; this lerps the mesh to fill the gap.
        private void InterpolateTransform()
        {
            if (visual == null)
                return;
            float alpha = Mathf.Clamp01((Time.time - _lastTickTime) / TickDt);
            visual.position = Vector3.Lerp(_prevVisualPos, _currVisualPos, alpha);
            visual.rotation = Quaternion.Slerp(_prevVisualRot, _currVisualRot, alpha);
        }

        // smooths the visual child for a pure remote, using two snapshots received over the network
        // (delayed + jittery). we render slightly in the past (now - interpolationDelay) and lerp
        // between them; rendering behind guarantees both ends have arrived, so we never extrapolate.
        private void InterpolateTransformDelayed()
        {
            if (_snapFrom == null || _snapTo == null || visual == null)
                return; // need two snapshots to lerp between, and a mesh to draw

            TimedSnapshot from = _snapFrom.Value;
            TimedSnapshot to = _snapTo.Value;
            float renderTime = Time.time - interpolationDelay;
            float span = to.ArrivalTime - from.ArrivalTime;
            float t = span > 0f ? Mathf.Clamp01((renderTime - from.ArrivalTime) / span) : 1f;

            visual.position = Vector3.Lerp(from.State.Position, to.State.Position, t);
            visual.rotation = Quaternion.Slerp(from.State.Rotation, to.State.Rotation, t);
        }

        // roll the visual-interp poses forward: last tick's result becomes prev, the current transform
        // becomes curr. called once per tick on the copies that simulate (owner + host).
        private void RecordTickPose()
        {
            _prevVisualPos = _currVisualPos;
            _prevVisualRot = _currVisualRot;
            _currVisualPos = transform.position;
            _currVisualRot = transform.rotation;
            _lastTickTime = Time.time;
        }

        private void OnNetworkTick()
        {
            if (IsOwner)
                OwnerTick();
        }

        // owner: predict locally, buffer, send to server
        private void OwnerTick()
        {
            // consume the right-click edge caught in Update: cycle hip -> zoom1 -> zoom2 -> hip
            if (_zoomCyclePressed)
            {
                _zoomLevel = (_zoomLevel + 1) % 3;
                _zoomCyclePressed = false;
            }

            int tick = _currentTick;

            // hard input-freeze during the reporter cutscene (SketchReveal): the owner sends empty
            // movement so its own prediction and the server simulate the same still input, no reconcile
            // fight, no desync. physics (gravity/grounding) still runs on a zero MoveDir, so nobody floats.
            // note: sketch-phase containment is handled by scene geometry (invisible walls), not here.
            bool frozen = GameFlowManager.Instance != null && GameFlowManager.Instance.InputFrozen;

            Vector2 rawMove = frozen ? Vector2.zero : (MoveInputOverride ?? ReadMoveInput());
            var input = new InputPayload
            {
                Tick = tick,
                MoveDir = CameraRelativeDirection(rawMove),
                Jump = !frozen && _jumpLatched,
                Scoped = !frozen && ReadScoped(),
                AimDir = CameraForward(),
                Sprinting = !frozen && ReadSprintInput(),
            };
            _jumpLatched = false;

            // predict with the shared step. Simulate() leaves the controller at the resulting position,
            // so no extra ApplyState is needed (that would re-teleport).
            StatePayload predicted = Simulate(CaptureState(tick), input, TickDt);
            RecordTickPose();

            // record this tick so we can replay it during reconcile
            _inputBuffer[tick % BufferSize] = input;
            _stateBuffer[tick % BufferSize] = predicted;

            SubmitInputServerRpc(input);

            _currentTick++;
        }

        // server: authoritative simulation
        [Rpc(SendTo.Server)]
        private void SubmitInputServerRpc(InputPayload input)
        {
            // host's own player: OwnerTick already simulated this tick and recorded its visual pose, so
            // just publish what it produced. re-simulating would apply physics twice (double jump/gravity).
            if (IsOwner)
            {
                _authoritativeState.Value = _stateBuffer[input.Tick % BufferSize];
                return;
            }

            // a client's input: simulate now to get the authoritative result, publish it, and record the
            // visual pose so the host can render-interpolate this remote copy's mesh.
            StatePayload nextState = Simulate(_authoritativeState.Value, input, TickDt);
            nextState.Tick = input.Tick; // stamp with the input's tick so the owner can reconcile
            _authoritativeState.Value = nextState;
            RecordTickPose();
        }

        // authoritative state arrived: owner reconciles, pure remote buffers it for interp
        private void OnAuthoritativeStateChanged(StatePayload _, StatePayload authoritativeState)
        {
            if (IsServer)
                return; // server is already the authority, nothing to reconcile

            if (IsOwner)
                Reconcile(authoritativeState);
            else
                RecordSnapshot(authoritativeState); // remote copy: buffer for interpolation in Update()
        }

        // appearance replicated in (or changed): repaint the character mesh on this copy. runs on every
        // machine, owner, server, and remotes, so all see the same look from the same index struct.
        private void OnAppearanceChanged(PlayerAppearance _, PlayerAppearance appearance)
        {
            if (appearanceApplier != null)
                appearanceApplier.Apply(appearance);
        }

        // pure remote: a new authoritative snapshot arrived. shift the latest into 'from' and store the
        // new one as 'to', each stamped with local arrival time (consumed by InterpolateTransformDelayed).
        private void RecordSnapshot(StatePayload authoritativeState)
        {
            _snapFrom = _snapTo;
            _snapTo = new TimedSnapshot { State = authoritativeState, ArrivalTime = Time.time };
        }

        // currently a hard snap (correct but steppy at high ping)
        private void Reconcile(StatePayload authoritativeState)
        {
            // how far off was our prediction for the tick the server just processed?
            StatePayload predicted = _stateBuffer[authoritativeState.Tick % BufferSize];
            float error = Vector3.Distance(predicted.Position, authoritativeState.Position);
            if (error < reconcileThreshold)
                return; // close enough, keep our smoother local prediction

            // snap to the authoritative state, then replay every input the server hasn't seen yet
            ApplyState(authoritativeState);
            _stateBuffer[authoritativeState.Tick % BufferSize] = authoritativeState;

            for (int t = authoritativeState.Tick + 1; t < _currentTick; t++)
            {
                InputPayload input = _inputBuffer[t % BufferSize];
                StatePayload replayed = Simulate(CaptureState(t), input, TickDt);
                _stateBuffer[t % BufferSize] = replayed;
            }
        }

        // shared deterministic step, called by both owner and server. same inputs -> same output on
        // every machine. uses CharacterController.Move, so it reads/writes the live transform; callers
        // position the transform first via ApplyState.
        private StatePayload Simulate(StatePayload state, InputPayload input, float dt)
        {
            // place the controller at the state we're stepping from
            ApplyState(state);

            if (_controller.isGrounded && _verticalVelocity < 0f)
                _verticalVelocity = -2f;
            if (input.Jump && _controller.isGrounded)
                _verticalVelocity = Mathf.Sqrt(2f * -gravity * jumpHeight);
            _verticalVelocity += gravity * dt;

            // only allow sprinting if we have actual movement input, and we are not scoped
            float currentSpeed = (input.Sprinting && !input.Scoped && input.MoveDir.sqrMagnitude > 0.001f) ? sprintSpeed : moveSpeed;

            // track speed + scope for animation (owner/server side)
            _lastSimulatedSpeed = input.MoveDir.sqrMagnitude > 0.001f ? currentSpeed : 0f;
            _lastSimulatedScoped = input.Scoped;

            Vector3 velocity = input.MoveDir * currentSpeed + Vector3.up * _verticalVelocity;
            _controller.Move(velocity * dt);

            // facing: scoped faces the aim direction (body points where you shoot, A/D strafe relative
            // to it); hip faces the movement direction (turn-to-move). movement is camera-relative in
            // both, only what the body turns toward changes.
            Vector3 faceDir = input.Scoped ? input.AimDir : input.MoveDir;

            Quaternion rotation = transform.rotation;
            if (faceDir.sqrMagnitude > 0.001f)
            {
                Quaternion targetRot = Quaternion.LookRotation(faceDir);
                rotation = Quaternion.RotateTowards(transform.rotation, targetRot, turnSpeed * dt);
                transform.rotation = rotation;
            }

            return new StatePayload
            {
                Tick = state.Tick,
                Position = transform.position,
                Rotation = rotation,
                VerticalVelocity = _verticalVelocity,
                Scoped = input.Scoped,
            };
        }

        // build a StatePayload from the controller's current transform, labeled with the given tick.
        // Scoped defaults to false here (used for spawn-time init and reconcile's CaptureState calls,
        // neither of which have a live input to read).
        private StatePayload CaptureState(int tick) =>
            new StatePayload
            {
                Tick = tick,
                Position = transform.position,
                Rotation = transform.rotation,
                VerticalVelocity = _verticalVelocity,
            };

        // move the controller to a state (disable it for the teleport so it doesn't fight us)
        private void ApplyState(StatePayload state)
        {
            _verticalVelocity = state.VerticalVelocity;
            if (transform.position != state.Position)
                TeleportTo(state.Position);
            transform.rotation = state.Rotation;
        }

        // server-only. called by RoleRegistry.SetRoleRpc when a client (re)picks a role (approach B).
        // moves the player to the new role's spawn point and republishes authoritative state so owner
        // and remote copies both follow. under a real lobby (approach A) role is set before spawn, so
        // this re-teleport never fires, OnNetworkSpawn places the player correctly the first time.
        public void RespawnForRole(PlayerRole role)
        {
            if (!IsServer)
                return;

            Role.Value = role;
            var spawnPos = SpawnPointManager.Instance != null
                ? SpawnPointManager.Instance.GetSpawnPoint(role)
                : DebugSpawnPosition((int)OwnerClientId);
            TeleportTo(spawnPos);
            _authoritativeState.Value = CaptureState(_authoritativeState.Value.Tick);
        }

        private void TeleportTo(Vector3 position)
        {
            bool wasEnabled = _controller.enabled;
            _controller.enabled = false;
            transform.position = position;
            _controller.enabled = wasEnabled;
        }

        // placeholder spawn: fan players out along x by client id so capsules don't overlap in testing.
        // replace with real role-based spawn points.
        private static Vector3 DebugSpawnPosition(int clientId)
        {
            float offset = ((clientId + 1) / 2) * 2f * (clientId % 2 == 0 ? 1f : -1f);
            return new Vector3(offset, 1.1f, 0f);
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

        // scoped while zoom level > 0 (AWP-style toggle: right-click cycles hip -> zoom1 -> zoom2 -> hip,
        // caught as an edge in Update and consumed in OwnerTick, see _zoomCyclePressed/_zoomLevel).
        // both zoom levels count as scoped for movement facing/strafe. only Snipers scope; a Criminal's
        // right-click must not flip the body into face-aim/strafe (that's a sniper-only stance); the
        // Update-side edge-catch already gates on Role, so _zoomLevel simply never advances for them.
        private bool ReadScoped() => _zoomLevel > 0;

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

        private struct InputPayload : INetworkSerializable
        {
            public int Tick;
            public Vector3 MoveDir;
            public bool Jump;
            public bool Scoped; // zoomed in: face the aim direction and strafe
            public Vector3 AimDir; // camera's horizontal forward, the body faces this when scoped
            public bool Sprinting;

            // one method both serializes and deserializes (s decides direction), so field order matters
            public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
            {
                s.SerializeValue(ref Tick);
                s.SerializeValue(ref MoveDir);
                s.SerializeValue(ref Jump);
                s.SerializeValue(ref Scoped);
                s.SerializeValue(ref AimDir);
                s.SerializeValue(ref Sprinting);
            }
        }

        private struct StatePayload : INetworkSerializable
        {
            public int Tick;
            public Vector3 Position;
            public Quaternion Rotation;
            public float VerticalVelocity;
            public bool Scoped;

            public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
            {
                s.SerializeValue(ref Tick);
                s.SerializeValue(ref Position);
                s.SerializeValue(ref Rotation);
                s.SerializeValue(ref VerticalVelocity);
                s.SerializeValue(ref Scoped);
            }
        }
    }
}

// with one host and one joiner there are four copies of the pair, and the (IsOwner, IsServer) flags
// pick each one's behaviour:
//   host-owner    (owner + server): predicts its own input, is the authority, renders via InterpolateTransform.
//   server-remote (server only):    simulates the client's input, renders via InterpolateTransform.
//   client-owner  (owner only):     predicts and reconciles against the server, renders via InterpolateTransform.
//   pure remote   (neither):        only gets snapshots, renders via InterpolateTransformDelayed.
