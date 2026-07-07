using FriendSlop.Game;
using Unity.Netcode;
using UnityEngine;

namespace FriendSlop.Player
{
    // server-authoritative movement with client prediction. flow per network tick:
    //   owner:   sample input -> Simulate() locally (move now) -> buffer input -> send to server.
    //   server:  receive input -> Simulate() with the same code -> publish authoritative state.
    //   owner:   when authoritative state arrives, snap to it and replay buffered inputs newer than the
    //            server's tick (so we don't lose moves the server hasn't seen yet).
    //   remote:  non-owners interpolate between buffered snapshots (~interpolationDelay behind).
    // also the facade other components hang off (Health, Appearances, Input). see the table at the bottom
    // of the file for the 4 copies.
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

        // how far in the past remotes are rendered. must be >= ~1 tick so the buffer holds a snapshot on both
        // sides of the render time to lerp between; the ring buffer holds enough to span a few ticks of jitter.
        [SerializeField]
        private float interpolationDelay = 0.1f;

        // the visual mesh, a child with no CharacterController. the root steps at tick rate; the mesh
        // render-interpolates between the owner's previous and current predicted tick, so the owner sees smooth
        // motion at full framerate. the camera follows this.
        [SerializeField]
        private Transform visual;

        private const int BufferSize = 256;

        private CharacterController _controller;

        // sibling components, cached in Awake so external readers reach them as pc.Health / pc.Appearances /
        // pc.Input without a repeated GetComponent (and the controller's freeze path can read Health.IsAlive).
        public PlayerHealth Health { get; private set; }
        public PlayerAppearanceSync Appearances { get; private set; }
        public PlayerInputReader Input { get; private set; } // owner-only local input -> per-tick intent.

        // visual render-interp (simulate path): the two most recent tick poses + the latest tick's time. the
        // mesh is drawn Lerp(prev, curr, timeSinceTick / tickDt) each frame. used by the owner and the host's
        // copy of a remote, not pure remotes (those use _snap* below).
        private Vector3 _prevVisualPos,
            _currVisualPos;
        private Quaternion _prevVisualRot,
            _currVisualRot;
        private float _lastTickTime;

        // authoritative state: server-write, read by everyone.
        private readonly NetworkVariable<StatePayload> _authoritativeState =
            new NetworkVariable<StatePayload>(writePerm: NetworkVariableWritePermission.Server);

        // this player's role, server-write (from RoleRegistry), read on every copy. role is data: ability
        // components gate on it instead of a subclass tree. owner reads it to gate input, server to enforce.
        public readonly NetworkVariable<PlayerRole> Role =
            new NetworkVariable<PlayerRole>(writePerm: NetworkVariableWritePermission.Server);

        // owner-side ring buffers, indexed by tick % BufferSize.
        private readonly InputPayload[] _inputBuffer = new InputPayload[BufferSize];
        private readonly StatePayload[] _stateBuffer = new StatePayload[BufferSize];

        // mutable sim state the owner predicts forward; the server keeps its own in _authoritativeState.
        private float _verticalVelocity;
        private int _currentTick;
        private float _lastSimulatedSpeed;
        private CharacterPose _lastSimulatedPose;

        // owner-only: true once the follow camera is found + targeted; until then Update retries each frame
        // (the camera may not exist yet when a client's player spawns during scene-sync).
        private bool _cameraAttached;

        // owner-only: 0 = hip, 1 = scoped, 2 = further. drives the camera FOV; forwarded from the input reader.
        public int ZoomLevel => Input != null ? Input.ZoomLevel : 0;

        // test hook forwarded to the input reader: when set, this raw move replaces WASD on the owner. null in play.
        public Vector2? MoveInputOverride
        {
            get => Input != null ? Input.MoveInputOverride : null;
            set { if (Input != null) Input.MoveInputOverride = value; }
        }

        // owner-only movement root: while true, OwnerTick feeds zero move input (same as the freeze path), so
        // the player can't glide during an action that should plant them (a criminal's punch). physics still runs.
        public bool MovementLocked { get; set; }

        // current horizontal speed. works on owner, server, and pure remotes.
        public float CurrentSpeed
        {
            get
            {
                bool iSimulateThis = IsOwner || IsServer;
                if (iSimulateThis)
                {
                    // owner/server: the intended speed from the last sim tick. CharacterController.velocity is
                    // unreliable in a frame-based Update when movement happens in fixed-interval ticks.
                    return _lastSimulatedSpeed;
                }
                else
                {
                    // remote: derive it from the two interp snapshots.
                    if (_snapFrom == null || _snapTo == null) return 0f;
                    Vector3 displacement = _snapTo.Value.State.Position - _snapFrom.Value.State.Position;
                    displacement.y = 0f;
                    float timeSpan = _snapTo.Value.ArrivalTime - _snapFrom.Value.ArrivalTime;
                    if (timeSpan <= 0.001f) return 0f;
                    return displacement.magnitude / timeSpan;
                }
            }
        }

        // sprinting (faster than walk). works on owner, server, and pure remotes.
        public bool IsSprinting => CurrentSpeed > (moveSpeed + 0.1f);

        // this player's current pose (aim / exotic / none), layered over locomotion. works everywhere; remotes
        // read it from the replicated StatePayload like position. single source of truth for the aim stance and
        // exotic poses; drives the Animator on every copy (server included) for correct hitbox rewind.
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

        // scoped in (aiming): a thin shim over CurrentPose so the camera/shooter/gun-grip readers don't learn the enum.
        public bool IsScoped => CurrentPose == CharacterPose.Scoped;

        // remote entity-interp: keep the two most recent snapshots and render the mesh at
        // (now - interpolationDelay) by lerping between them (~one tick in the past, so the "to" snapshot has
        // arrived and we never extrapolate). a snapshot = an authoritative state + the local time it arrived.
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
                // seed both interp poses to spawn so the mesh doesn't lerp in from the origin.
                _prevVisualPos = _currVisualPos = transform.position;
                _prevVisualRot = _currVisualRot = transform.rotation;
                _lastTickTime = Time.time;

                // on the host the camera exists at spawn; on a client the player spawns during scene-sync and
                // the camera may not exist yet, so TryAttachCamera keeps retrying in Update (else the client is
                // left with a frozen, un-followed view).
                TryAttachCamera();
            }

            // a pure remote (not owner, not server) never simulates, only displays interpolated snapshots.
            // disable the controller so it doesn't fight direct transform writes, and seed the buffer.
            if (!IsOwner && !IsServer)
            {
                _controller.enabled = false;
                RecordSnapshot(_authoritativeState.Value);
            }

            _authoritativeState.OnValueChanged += OnAuthoritativeStateChanged;

            NetworkManager.NetworkTickSystem.Tick += OnNetworkTick;
        }

        // owner-only: find the scene's follow camera and point it at this player. sets _cameraAttached so the
        // Update retry stops. safe to call repeatedly (the camera may appear a frame or two after spawn).
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
            // 4 copies exist, all choppy at tick rate so all interpolate. one question splits them: do I
            // simulate this copy?
            //   yes (owner predicts / server computes) -> fresh tick poses, interpolate between them (real-time).
            //   no  (remote, only gets snapshots)      -> delayed + jittery, interpolate from past snapshots.
            bool iSimulateThis = IsOwner || IsServer;
            if (!iSimulateThis)
            {
                InterpolateTransformDelayed();
                return;
            }
            // "I simulate" isn't real-time: a remote's input still traveled to reach the server, so this copy is
            // a bit behind; the win is no second hop between computing and drawing it.
            if (IsOwner)
            {
                if (!_cameraAttached)
                    TryAttachCamera();

                // latch this frame's edge events (jump / zoom / pose); the reader consumes them at tick time in
                // OwnerTick so predict + reconcile agree on the same input.
                if (Input != null)
                    Input.LatchEdges(Role.Value);
            }
            InterpolateTransform();
        }

        // smooths the 'visual' child (not the root) for a copy this simulates (owner, or host's copy of a
        // remote), lerping between the two latest tick poses. alpha = how far into the tick.
        private void InterpolateTransform()
        {
            if (visual == null)
                return;
            float alpha = Mathf.Clamp01((Time.time - _lastTickTime) / TickDt);
            visual.position = Vector3.Lerp(_prevVisualPos, _currVisualPos, alpha);
            visual.rotation = Quaternion.Slerp(_prevVisualRot, _currVisualRot, alpha);
        }

        // smooths the 'visual' child for a pure remote, lerping between two snapshots received over the network.
        // rendered slightly in the past (now - interpolationDelay) so both ends have arrived: interpolate, never
        // extrapolate.
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

        // roll the interp poses forward: last tick's result becomes 'prev', the current transform 'curr'. once
        // per tick on the copies that simulate (owner + host).
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

            // sample this tick's intent, which also consumes the latched edges (zoom / pose / jump) at tick
            // time; resolving them here, not in Update, keeps predict + reconcile in agreement. zoom/pose still
            // update while frozen (you can re-aim); only movement is zeroed.
            PlayerIntent intent = Input != null ? Input.Sample(Role.Value) : default;

            // frozen if the phase freezes everyone (the SketchReveal cutscene), the player is downed, or it's
            // rooted mid-punch. the owner then sends zero movement so its prediction and the server simulate the
            // same still input (no reconcile fight); physics still runs on zero input, so nobody floats. (sketch
            // containment is scene geometry, not here.)
            bool frozen = (GameFlowManager.Instance != null && GameFlowManager.Instance.InputFrozen)
                || (Health != null && !Health.IsAlive.Value)
                || MovementLocked;

            var input = new InputPayload
            {
                Tick = tick,
                MoveDir = frozen ? Vector3.zero : intent.MoveDir,
                Jump = !frozen && intent.Jump,
                Pose = frozen ? CharacterPose.None : intent.Pose,
                AimDir = intent.AimDir,
                Sprinting = !frozen && intent.Sprinting,
            };

            // predict: advance our own copy with the shared step. Simulate leaves the controller at the result,
            // so no extra ApplyState is needed (that would re-teleport).
            StatePayload predicted = Simulate(CaptureState(tick), input, TickDt);
            RecordTickPose();

            // record this tick so we can replay it during reconcile.
            _inputBuffer[tick % BufferSize] = input;
            _stateBuffer[tick % BufferSize] = predicted;

            SubmitInputServerRpc(input);

            _currentTick++;
        }

        // server: authoritative simulation
        [Rpc(SendTo.Server)]
        private void SubmitInputServerRpc(InputPayload input)
        {
            // host's own player: OwnerTick already simulated this tick and recorded its pose. just publish it;
            // re-simulating would apply physics twice.
            if (IsOwner)
            {
                _authoritativeState.Value = _stateBuffer[input.Tick % BufferSize];
                return;
            }

            // a client's input: simulate it now for the authoritative result, publish, and record the pose so
            // the host can render-interpolate this remote copy's mesh.
            StatePayload nextState = Simulate(_authoritativeState.Value, input, TickDt);
            nextState.Tick = input.Tick; // stamp with the input's tick so the owner can reconcile
            _authoritativeState.Value = nextState;
            RecordTickPose();
        }

        // authoritative state arrived: owner reconciles, pure remote buffers it for interp
        private void OnAuthoritativeStateChanged(StatePayload _, StatePayload authoritativeState)
        {
            if (IsServer)
                return; // already the authority; nothing to reconcile.

            if (IsOwner)
                Reconcile(authoritativeState);
            else
                RecordSnapshot(authoritativeState); // remote: buffer for interpolation.
        }

        // pure remote: a snapshot arrived. shift the latest into 'from' and store the new one as 'to', each
        // stamped with local arrival time (consumed by InterpolateTransformDelayed).
        private void RecordSnapshot(StatePayload authoritativeState)
        {
            _snapFrom = _snapTo;
            _snapTo = new TimedSnapshot { State = authoritativeState, ArrivalTime = Time.time };
        }

        // currently a hard snap (correct but visibly steppy at high ping).
        private void Reconcile(StatePayload authoritativeState)
        {
            // how far off was our prediction for the tick the server just processed?
            StatePayload predicted = _stateBuffer[authoritativeState.Tick % BufferSize];
            float error = Vector3.Distance(predicted.Position, authoritativeState.Position);
            if (error < reconcileThreshold)
                return; // close enough; keep our smoother local prediction.

            // snap to authority, then replay every input the server hasn't seen yet.
            ApplyState(authoritativeState);
            _stateBuffer[authoritativeState.Tick % BufferSize] = authoritativeState;

            for (int t = authoritativeState.Tick + 1; t < _currentTick; t++)
            {
                InputPayload input = _inputBuffer[t % BufferSize];
                StatePayload replayed = Simulate(CaptureState(t), input, TickDt);
                _stateBuffer[t % BufferSize] = replayed;
            }
        }

        // shared deterministic step (owner and server both call this): given a starting state + input, return
        // the next state. deterministic (same inputs -> same output everywhere). uses CharacterController.Move,
        // so it reads/writes the live transform; callers position it first via ApplyState.
        private StatePayload Simulate(StatePayload state, InputPayload input, float dt)
        {
            // place the controller at the state we're stepping from.
            ApplyState(state);

            // authority re-check: sanitize the claimed pose against the server-known role (a hacked criminal
            // can't send Scoped, a hacked hunter can't send an exotic pose). runs identically on owner + server,
            // so prediction matches authority and never reconcile-fights.
            CharacterPose pose = SanitizePose(input.Pose);

            if (_controller.isGrounded && _verticalVelocity < 0f)
                _verticalVelocity = -2f;
            if (input.Jump && _controller.isGrounded)
                _verticalVelocity = Mathf.Sqrt(2f * -gravity * jumpHeight);
            _verticalVelocity += gravity * dt;

            // poses don't change walk speed (a criminal moves and jumps freely in any pose, "maintained
            // mid-air"), but any non-None pose blocks sprint, so a posed criminal moves at walk speed only.
            bool posed = pose != CharacterPose.None;
            float currentSpeed = (input.Sprinting && !posed && input.MoveDir.sqrMagnitude > 0.001f) ? sprintSpeed : moveSpeed;

            // track speed + pose for animation (owner/server).
            _lastSimulatedSpeed = input.MoveDir.sqrMagnitude > 0.001f ? currentSpeed : 0f;
            _lastSimulatedPose = pose;

            Vector3 velocity = input.MoveDir * currentSpeed + Vector3.up * _verticalVelocity;
            _controller.Move(velocity * dt);

            // facing: scoped faces the aim direction (body points where you shoot, A/D strafe relative to it);
            // otherwise face the movement direction. movement is camera-relative either way. exotic poses use
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

        // a StatePayload from the controller's current transform, labeled with the given tick. pose defaults to
        // None (used for spawn init + reconcile's CaptureState, neither of which has a live input).
        private StatePayload CaptureState(int tick) =>
            new StatePayload
            {
                Tick = tick,
                Position = transform.position,
                Rotation = transform.rotation,
                VerticalVelocity = _verticalVelocity,
            };

        // move the controller to a state (disabled for the teleport so it doesn't fight us).
        private void ApplyState(StatePayload state)
        {
            _verticalVelocity = state.VerticalVelocity;
            if (transform.position != state.Position)
                TeleportTo(state.Position);
            transform.rotation = state.Rotation;
        }

        // server-only. called by RoleRegistry when a client (re)picks a role (approach B): move to the new
        // spawn and republish authoritative state so owner + remotes follow. under a real lobby (A) role is set
        // before spawn, so this never fires (OnNetworkSpawn places the player the first time).
        public void RespawnForRole(PlayerRole role, int round)
        {
            if (!IsServer)
                return;

            Role.Value = role;
            if (Health != null)
                Health.Revive(); // alive with full body HP (mesh/hitbox re-show via OnValueChanged).
            if (Appearances != null)
                Appearances.Roll(round); // fresh look each round, not last round's outfit.
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

        // placeholder spawn: fan players out along x by client id so they don't overlap when there's no
        // SpawnPointManager. real placement comes from SpawnPointManager.
        private static Vector3 DebugSpawnPosition(int clientId)
        {
            float offset = ((clientId + 1) / 2) * 2f * (clientId % 2 == 0 ? 1f : -1f);
            return new Vector3(offset, 1.1f, 0f);
        }

        // in Simulate on both owner and server: force the claimed pose legal for this player's role, so a
        // tampered client can't pose out of its role. hunters may only be None/Scoped; criminals any but Scoped.
        private CharacterPose SanitizePose(CharacterPose pose)
        {
            bool hunter = Role.Value.IsHunter();
            if (hunter)
                return pose == CharacterPose.Scoped ? CharacterPose.Scoped : CharacterPose.None;
            // criminal: drop a spoofed Scoped, allow the exotic poses + None.
            return pose == CharacterPose.Scoped ? CharacterPose.None : pose;
        }

        private struct InputPayload : INetworkSerializable
        {
            public int Tick;
            public Vector3 MoveDir;
            public bool Jump;
            public CharacterPose Pose; // chosen pose (aim/exotic/none), layered over locomotion.
            public Vector3 AimDir; // camera's horizontal forward; the body faces this when scoped.
            public bool Sprinting;

            // one method serializes and deserializes (s decides direction), so field order is followed.
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
