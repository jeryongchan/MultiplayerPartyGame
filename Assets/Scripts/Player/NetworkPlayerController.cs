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

        // the player's shootable body hitbox (the dedicated CapsuleCollider NetworkShooter raycasts). hidden
        // together with the mesh when the player is downed, so a corpse can't be shot again. optional; if
        // unassigned, only the mesh hides.
        [SerializeField]
        private Collider hitCollider;

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

        // alive/down state for the round. server-write, read everywhere. a criminal shot during Hunt is
        // set false: its mesh + hitbox hide on every copy (OnValueChanged) and, on the owner, its input is
        // frozen so it spectates in place until the next round. RespawnForRole revives it. defaults true so
        // nobody spawns dead (and non-round systems that never touch it see a live player).
        public readonly NetworkVariable<bool> IsAlive =
            new NetworkVariable<bool>(true, writePerm: NetworkVariableWritePermission.Server);

        // how many body hits it takes to down this player. a head hit is always an instant kill regardless.
        // server-only tuning; exposed so you can tweak survivability in the Inspector while testing.
        [SerializeField]
        private int bodyHitsToKill = 3;

        // remaining body hits before this player goes down, server-side only (never networked; clients don't
        // need the number, only the resulting IsAlive flip). refilled to bodyHitsToKill on spawn/respawn.
        private int _hp;

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
        private CharacterPose _lastSimulatedPose;

        // owner-only: the criminal's currently-selected exotic pose (sticky, held until changed). number
        // keys set it in Update; OwnerTick folds it into InputPayload.Pose. scope (a hunter pose) is chosen
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

        // owner-only: true once the follow camera has been found + targeted. until then the owner retries
        // each frame (the camera may not exist yet when a client's player spawns during scene-sync).
        private bool _cameraAttached;

        // owner-only: 0 = hip, 1 = scoped, 2 = scoped further in. drives ThirdPersonCamera's FOV.
        public int ZoomLevel => _zoomLevel;

        // optional test hook: when set (e.g. by AutoStrafe), this raw move input replaces WASD on the
        // owner. stays null in normal play. lets a test drive movement through the real input path.
        public Vector2? MoveInputOverride { get; set; }

        // owner-only movement root: while true, OwnerTick feeds zero move input (same as the freeze path), so
        // the player can't glide during an action that should plant them, e.g. a criminal's punch. set/cleared
        // by CriminalMelee around the swing. physics (gravity/grounding) still runs on the zero input.
        public bool MovementLocked { get; set; }

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

        // this player's current deliberate pose (aim / exotic / none), layered over locomotion. works on
        // owner, server, and pure remotes; remotes read it from the replicated StatePayload, same as
        // position/rotation. the single source of truth for both the aim stance and the exotic poses; drives
        // the Animator on every copy (server included) so per-bone hitboxes are posed for correct rewind.
        public CharacterPose CurrentPose
        {
            get
            {
                bool iSimulateThis = IsOwner || IsServer;
                if (iSimulateThis)
                    return _lastSimulatedPose;

                return _snapTo?.State.Pose ?? CharacterPose.None;
            }
        }

        // true while this player is scoped in (aiming), a thin shim over CurrentPose so the
        // camera, shooter, and gun-grip readers don't all have to learn the enum. scoped is just one pose.
        public bool IsScoped => CurrentPose == CharacterPose.Scoped;

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
                _hp = bodyHitsToKill; // start the round at full body HP.
                var spawnPos = SpawnPointManager.Instance != null
                    ? SpawnPointManager.Instance.GetSpawnPoint(role)
                    : DebugSpawnPosition((int)OwnerClientId);
                TeleportTo(spawnPos);
                _authoritativeState.Value = CaptureState(0);

                // roll the initial look (round 0). re-rolled each round in RespawnForRole so a new round is
                // a fresh character. replicated via the NetworkVariable; every copy paints from it.
                RollAppearance(0);
            }

            if (IsOwner)
            {
                // seed both interp poses to spawn so the mesh doesn't lerp in from the origin
                _prevVisualPos = _currVisualPos = transform.position;
                _prevVisualRot = _currVisualRot = transform.rotation;
                _lastTickTime = Time.time;

                // attach the follow camera. on the host the GameScene camera already exists at spawn; on a
                // client the player spawns during NGO scene-sync and the camera may not exist yet, so we may
                // not find it here; TryAttachCamera() keeps retrying each frame in Update until it does
                // (otherwise the client is left with a frozen, un-followed view).
                TryAttachCamera();
            }

            // a pure remote copy never simulates, it only displays interpolated snapshots. disable the
            // controller so it doesn't fight direct transform writes, and seed with current authoritative state.
            if (!IsOwner && !IsServer)
            {
                _controller.enabled = false;
                RecordSnapshot(_authoritativeState.Value);
            }

            _authoritativeState.OnValueChanged += OnAuthoritativeStateChanged;

            // down/alive visuals: hide the mesh + hitbox on every copy when a player is downed. apply the
            // current value now (covers late joiners who arrive mid-round) and react to changes after.
            IsAlive.OnValueChanged += OnAliveChanged;
            ApplyAliveVisual(IsAlive.Value);

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

        // owner-only: find the scene's follow camera and point it at this player. sets _cameraAttached on
        // success so the Update retry stops. safe to call repeatedly. the camera lives in the GameScene and
        // may appear a frame or two after a client's player spawns, hence the retry.
        private void TryAttachCamera()
        {
            var cam = Object.FindFirstObjectByType<ThirdPersonCamera>();
            if (cam == null)
                return;
            cam.SetTarget(visual != null ? visual : transform); // follow the smoothed mesh
            _cameraAttached = true;
        }

        public override void OnNetworkDespawn()
        {
            _authoritativeState.OnValueChanged -= OnAuthoritativeStateChanged;
            Appearance.OnValueChanged -= OnAppearanceChanged;
            IsAlive.OnValueChanged -= OnAliveChanged;
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
                // keep trying to grab the follow camera until we have it (see OnNetworkSpawn note)
                if (!_cameraAttached)
                    TryAttachCamera();

                // movement is handled in OwnerTick; Jump is an edge event so it's latched here in Update
                var kb = UnityEngine.InputSystem.Keyboard.current;
                if (kb != null && kb.spaceKey.wasPressedThisFrame)
                    _jumpLatched = true;

                // right-click cycles the zoom level (edge event, like Jump). only Snipers can scope,
                // a Criminal's right-click stays a no-op, same gating ResolvePose() already enforces.
                var mouse = UnityEngine.InputSystem.Mouse.current;
                if (mouse != null && mouse.rightButton.wasPressedThisFrame && Role.Value.IsHunter())
                    _zoomCyclePressed = true;

                // criminal exotic poses: number keys sticky-toggle a pose (press to enter, press the same key
                // or 0 to stand). edge-caught here, consumed in OwnerTick -> InputPayload.Pose. only the
                // Criminal poses; the server re-checks the role in Simulate so a hacked client can't pose as a
                // hunter (and can't scope-via-pose either). 0 clears back to None. (RolePickerDebug moved to
                // F1/F2 so the number row is free here; swap to a radial menu when that picker is deleted.)
                if (kb != null && Role.Value == PlayerRole.Criminal)
                {
                    if (kb.digit1Key.wasPressedThisFrame) TogglePose(CharacterPose.Handstand);
                    else if (kb.digit2Key.wasPressedThisFrame) TogglePose(CharacterPose.Shoelace);
                    else if (kb.digit3Key.wasPressedThisFrame) TogglePose(CharacterPose.Split);
                    else if (kb.digit0Key.wasPressedThisFrame) { _poseKeyPressed = true; _poseKeyValue = CharacterPose.None; }
                }
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

            // consume the pose-key edge caught in Update: sticky toggle (same key again returns to stand)
            if (_poseKeyPressed)
            {
                _selectedPose = _selectedPose == _poseKeyValue ? CharacterPose.None : _poseKeyValue;
                _poseKeyPressed = false;
            }

            int tick = _currentTick;

            // hard input-freeze during the reporter cutscene (SketchReveal): the owner sends empty
            // movement so its own prediction and the server simulate the same still input, no reconcile
            // fight, no desync. physics (gravity/grounding) still runs on a zero MoveDir, so nobody floats.
            // note: sketch-phase containment is handled by scene geometry (invisible walls), not here.
            // frozen if the phase freezes everyone (SketchReveal cutscene) or this player is downed (a
            // spectating criminal can't move).
            bool frozen = (GameFlowManager.Instance != null && GameFlowManager.Instance.InputFrozen)
                || !IsAlive.Value
                || MovementLocked; // e.g. rooted mid-punch so the criminal can't glide while swinging.

            Vector2 rawMove = frozen ? Vector2.zero : (MoveInputOverride ?? ReadMoveInput());
            var input = new InputPayload
            {
                Tick = tick,
                MoveDir = CameraRelativeDirection(rawMove),
                Jump = !frozen && _jumpLatched,
                Pose = frozen ? CharacterPose.None : ResolvePose(),
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

            // authority re-check: the pose is a role-gated ability, so sanitize the client's claimed pose
            // against its server-known role. a hacked criminal can't send Scoped (a hunter stance), and a
            // hacked hunter can't send an exotic pose. runs identically on owner + server (owner's Role is
            // the same replicated value), so prediction matches authority and never reconcile-fights.
            CharacterPose pose = SanitizePose(input.Pose);

            if (_controller.isGrounded && _verticalVelocity < 0f)
                _verticalVelocity = -2f;
            if (input.Jump && _controller.isGrounded)
                _verticalVelocity = Mathf.Sqrt(2f * -gravity * jumpHeight);
            _verticalVelocity += gravity * dt;

            // poses don't change the walk speed, a criminal still moves freely (and jumps) in any pose (GDD:
            // "exotic poses are maintained mid-air"). but sprint is disallowed while posed: scoped (a sniper
            // stance) and the exotic poses both block it, so a posed criminal moves only at walk speed. any
            // non-None pose means no sprint.
            bool posed = pose != CharacterPose.None;
            float currentSpeed = (input.Sprinting && !posed && input.MoveDir.sqrMagnitude > 0.001f) ? sprintSpeed : moveSpeed;

            // track speed + pose for animation (owner/server side)
            _lastSimulatedSpeed = input.MoveDir.sqrMagnitude > 0.001f ? currentSpeed : 0f;
            _lastSimulatedPose = pose;

            Vector3 velocity = input.MoveDir * currentSpeed + Vector3.up * _verticalVelocity;
            _controller.Move(velocity * dt);

            // facing: scoped faces the aim direction (so the body points where you shoot, and A/D
            // strafe relative to it). otherwise faces the movement direction (turn-to-move). movement itself
            // is camera-relative in both; only what the body turns toward changes. exotic poses use
            // movement-facing (they're not aim stances).
            Vector3 faceDir = pose == CharacterPose.Scoped ? input.AimDir : input.MoveDir;

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
                Pose = pose,
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
        public void RespawnForRole(PlayerRole role, int round)
        {
            if (!IsServer)
                return;

            Role.Value = role;
            IsAlive.Value = true; // revive for the new round (mesh/hitbox re-show via OnValueChanged)
            _hp = bodyHitsToKill; // refill body HP for the new round
            RollAppearance(round); // fresh look each round, a new character, not last round's outfit
            var spawnPos = SpawnPointManager.Instance != null
                ? SpawnPointManager.Instance.GetSpawnPoint(role)
                : DebugSpawnPosition((int)OwnerClientId);
            TeleportTo(spawnPos);
            _authoritativeState.Value = CaptureState(_authoritativeState.Value.Tick);
        }

        // server-only. roll this player's replicated appearance for the given round, seeded by (clientId,
        // round) so it's deterministic (reproducible/debuggable) yet genuinely different each round; mixing
        // the round in is what stops it re-rolling the identical outfit. every copy repaints via OnValueChanged.
        private void RollAppearance(int round)
        {
            if (appearanceCatalog == null)
                return;
            int seed = unchecked((int)OwnerClientId * 73856093 ^ round * 19349663);
            Appearance.Value = CharacterAppearanceApplier.Roll(appearanceCatalog, new System.Random(seed));
        }

        // server-only. steal one garment from source (an NPC's look) into this player's, the
        // criminal's disguise-steal. which slot is taken is chosen from pool seeded by
        // seed (the NPC index), so the same NPC always yields the same piece on every
        // machine, deterministic, no networking. taking one piece per punch (not the whole outfit) keeps the
        // disguise from being too strong, so the sketch phase still matters: a full look takes several NPCs.
        //
        // only that one slot changes; the rest of this player's look is kept. writes the replicated Appearance,
        // so every copy repaints via OnValueChanged (zero mesh bandwidth, just the index struct). no-op off
        // the server. names not found in the catalog are skipped when narrowing the pool.
        //
        // returns the catalog slot index that was stolen, or -1 if nothing was (so the caller can tell the
        // NPC which garment to remove). the pick is deterministic in seed.
        public int StealOneGarment(PlayerAppearance source, int seed, params string[] pool)
        {
            if (!IsServer || appearanceCatalog == null || !source.IsValid || pool == null || pool.Length == 0)
                return -1;

            var slots = appearanceCatalog.Slots;

            // resolve the pool names to valid catalog slot indices (skip any missing / out of range)
            var candidates = new System.Collections.Generic.List<int>(pool.Length);
            foreach (var name in pool)
            {
                int idx = System.Array.FindIndex(slots, s => s.childName == name);
                if (idx >= 0 && idx < source.slots.Length)
                    candidates.Add(idx);
            }
            if (candidates.Count == 0)
                return -1;

            // pick one candidate slot, seeded by the NPC index so it's the same everywhere and per-NPC stable
            int pick = candidates[new System.Random(seed * 83492791).Next(candidates.Count)];

            // start from this player's current look so we overwrite only the stolen slot
            var current = Appearance.Value.IsValid && Appearance.Value.slots != null
                ? (sbyte[])Appearance.Value.slots.Clone()
                : new sbyte[appearanceCatalog.SlotCount];
            if (pick < current.Length)
                current[pick] = source.slots[pick]; // copy the NPC's variant for this one slot (may be Hidden)

            Appearance.Value = new PlayerAppearance(current);
            return pick;
        }

        // server-only. mark this player down (or revive). down players hide their mesh + hitbox (so they
        // can't be shot again) on every copy and, on the owner, freeze input to spectate in place. called
        // by NetworkShooter when a criminal is hit during Hunt.
        public void SetAlive(bool alive)
        {
            if (!IsServer)
                return;
            IsAlive.Value = alive;
            if (alive)
                _hp = bodyHitsToKill; // reviving refills body HP
        }

        // server-only. apply one hit in the given zone. a head hit downs instantly; a body hit decrements
        // HP and only downs at zero. returns true if this hit was the killing blow (so the caller can
        // score the kill exactly once). no-ops if already down. body HP refills on respawn/SetAlive(true).
        public bool TakeHit(HitZone zone)
        {
            if (!IsServer || !IsAlive.Value)
                return false;

            if (zone == HitZone.Head)
            {
                IsAlive.Value = false;
                return true;
            }

            _hp--;
            if (_hp <= 0)
            {
                IsAlive.Value = false;
                return true;
            }
            return false; // survived, a body hit that didn't kill
        }

        // runs on every copy when IsAlive flips: show/hide the mesh + shootable hitbox
        private void OnAliveChanged(bool _, bool alive) => ApplyAliveVisual(alive);

        private void ApplyAliveVisual(bool alive)
        {
            if (visual != null)
                visual.gameObject.SetActive(alive);
            if (hitCollider != null)
                hitCollider.enabled = alive;
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

        // owner-only: latch a pose-key edge for OwnerTick to consume as a sticky toggle. (Update can't touch
        // _selectedPose directly, the toggle must resolve at tick time so predict + reconcile agree.)
        private void TogglePose(CharacterPose pose)
        {
            _poseKeyPressed = true;
            _poseKeyValue = pose;
        }

        // owner-only: the pose to send this tick. a hunter scoping in (zoom > 0) is the Scoped pose; otherwise
        // it's the criminal's sticky-selected exotic pose (None if unselected). scope and exotic poses never
        // coexist (role-exclusive), so this simple precedence is unambiguous. SanitizePose re-gates by role.
        private CharacterPose ResolvePose()
        {
            if (Role.Value.IsHunter() && _zoomLevel > 0)
                return CharacterPose.Scoped;
            return _selectedPose;
        }

        // runs in Simulate on both owner and server: force the claimed pose to be legal for this player's
        // replicated role, so a tampered client can't pose out of its role. hunters may only be None/Scoped;
        // criminals may hold any pose except Scoped (that's a hunter aim stance, not an exotic pose).
        private CharacterPose SanitizePose(CharacterPose pose)
        {
            bool hunter = Role.Value.IsHunter();
            if (hunter)
                return pose == CharacterPose.Scoped ? CharacterPose.Scoped : CharacterPose.None;
            // criminal (or any non-hunter): drop a spoofed Scoped, allow the exotic poses + None
            return pose == CharacterPose.Scoped ? CharacterPose.None : pose;
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

        private struct InputPayload : INetworkSerializable
        {
            public int Tick;
            public Vector3 MoveDir;
            public bool Jump;
            public CharacterPose Pose; // chosen deliberate pose (aim/exotic/none), layered over locomotion
            public Vector3 AimDir; // camera's horizontal forward, the body faces this when scoped
            public bool Sprinting;

            // one method both serializes and deserializes (s decides direction), so field order matters
            public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
            {
                s.SerializeValue(ref Tick);
                s.SerializeValue(ref MoveDir);
                s.SerializeValue(ref Jump);
                s.SerializeValue(ref Pose);
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
            public CharacterPose Pose;

            public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
            {
                s.SerializeValue(ref Tick);
                s.SerializeValue(ref Position);
                s.SerializeValue(ref Rotation);
                s.SerializeValue(ref VerticalVelocity);
                s.SerializeValue(ref Pose);
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
