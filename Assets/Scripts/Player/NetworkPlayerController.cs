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

        private const int BufferSize = 256;

        private CharacterController _controller;

        // sibling health component (alive/down state + HP). cached so the many external readers reach it as
        // playerController.Health.IsAlive without a repeated GetComponent, and so the controller's own freeze
        // path can read IsAlive. assigned in Awake.
        public PlayerHealth Health { get; private set; }

        // sibling appearance component (the replicated look + roll/steal). cached so CriminalMelee can reach it
        // as playerController.Appearances.StealOneGarment, and RespawnForRole can re-roll it. assigned in Awake.
        public PlayerAppearanceSync Appearances { get; private set; }

        // sibling input reader (owner-only local input to per-tick intent). cached so Update can latch edges and
        // OwnerTick can sample. only meaningful on the owner; other copies never call into it. assigned in Awake.
        public PlayerInputReader Input { get; private set; }

        // visual render-interpolation (simulate path): the two most recent tick poses + the time of the
        // latest tick. the mesh is drawn Lerp(prev, curr, timeSinceTick / tickDt) each frame, so it moves
        // smoothly between ticks. used by the owner and the host's copy of a remote, not pure remotes
        // (those use the _snap* snapshots instead). distinct from the authoritative pose in _stateBuffer.
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

        // owner-side ring buffers, indexed by tick % BufferSize.
        private readonly InputPayload[] _inputBuffer = new InputPayload[BufferSize];
        private readonly StatePayload[] _stateBuffer = new StatePayload[BufferSize];

        // mutable sim state the owner predicts forward; the server keeps its own copy in _authoritativeState.
        private float _verticalVelocity;
        private int _currentTick;
        private float _lastSimulatedSpeed;
        private CharacterPose _lastSimulatedPose;

        // owner-only: true once the follow camera has been found + targeted. until then the owner retries
        // each frame (the camera may not exist yet when a client's player spawns during scene-sync).
        private bool _cameraAttached;

        // owner-only: 0 = hip, 1 = scoped, 2 = scoped further in. drives ThirdPersonCamera's FOV.
        // forwarded from the input reader so the camera can keep reading it off the controller.
        public int ZoomLevel => Input != null ? Input.ZoomLevel : 0;

        // optional test hook: when set (e.g. by AutoStrafe), this raw move input replaces WASD on the owner.
        // forwarded to the input reader (which owns the actual sampling). stays null in normal play.
        public Vector2? MoveInputOverride
        {
            get => Input != null ? Input.MoveInputOverride : null;
            set { if (Input != null) Input.MoveInputOverride = value; }
        }

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

        private void Awake()
        {
            _controller = GetComponent<CharacterController>();
            Health = GetComponent<PlayerHealth>();
            Appearances = GetComponent<PlayerAppearanceSync>();
            Input = GetComponent<PlayerInputReader>();
        }

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

            NetworkManager.NetworkTickSystem.Tick += OnNetworkTick;
        }

        // owner-only: find the scene's follow camera and point it at this player. sets _cameraAttached on
        // success so the Update retry stops. safe to call repeatedly. the camera lives in the GameScene and
        // may appear a frame or two after a client's player spawns, hence the retry.
        private void TryAttachCamera()
        {
            var cam = ThirdPersonCamera.Instance;
            if (cam == null)
                return;
            cam.SetTarget(visual != null ? visual : transform); // follow the smoothed mesh
            _cameraAttached = true;
        }

        public override void OnNetworkDespawn()
        {
            _authoritativeState.OnValueChanged -= OnAuthoritativeStateChanged;
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

                // latch this frame's edge events (jump / zoom-cycle / pose-key), the reader consumes them at
                // tick time in OwnerTick so predict + reconcile agree on the same input for a tick.
                if (Input != null)
                    Input.LatchEdges(Role.Value);
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
            int tick = _currentTick;

            // sample this tick's intent from local input. this also consumes the latched edges (zoom cycle,
            // pose toggle, jump) at tick time, resolving them here, not in Update, is what keeps predict +
            // reconcile in agreement. zoom/pose still update even while frozen below (you can re-aim while
            // held); only the movement is zeroed.
            PlayerIntent intent = Input != null ? Input.Sample(Role.Value) : default;

            // hard input-freeze during the reporter cutscene (SketchReveal): the owner sends empty
            // movement so its own prediction and the server simulate the same still input, no reconcile
            // fight, no desync. physics (gravity/grounding) still runs on a zero MoveDir, so nobody floats.
            // note: sketch-phase containment is handled by scene geometry (invisible walls), not here.
            // frozen if the phase freezes everyone (SketchReveal cutscene) or this player is downed (a
            // spectating criminal can't move). physics still runs on zero input so nobody floats.
            bool frozen = (GameFlowManager.Instance != null && GameFlowManager.Instance.InputFrozen)
                || (Health != null && !Health.IsAlive.Value)
                || MovementLocked; // e.g. rooted mid-punch so the criminal can't glide while swinging.

            var input = new InputPayload
            {
                Tick = tick,
                MoveDir = frozen ? Vector3.zero : intent.MoveDir,
                Jump = !frozen && intent.Jump,
                Pose = frozen ? CharacterPose.None : intent.Pose,
                AimDir = intent.AimDir,
                Sprinting = !frozen && intent.Sprinting,
            };

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
            if (Health != null)
                Health.Revive(); // alive again with full body HP (mesh/hitbox re-show via OnValueChanged)
            if (Appearances != null)
                Appearances.Roll(round); // fresh look each round, a new character, not last round's outfit
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

        // payloads

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
