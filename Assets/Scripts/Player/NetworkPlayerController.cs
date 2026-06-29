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

        // owner-side ring buffers, indexed by tick % BufferSize.
        private readonly InputPayload[] _inputBuffer = new InputPayload[BufferSize];
        private readonly StatePayload[] _stateBuffer = new StatePayload[BufferSize];

        // mutable sim state the owner predicts forward; the server keeps its own copy in _authoritativeState.
        private float _verticalVelocity;
        private bool _jumpLatched;
        private int _currentTick;

        // remote interp: keep the two most recent snapshots and render at (now - interpolationDelay) by
        // lerping between them, ~one tick in the past so the "to" snapshot has arrived and we never
        // extrapolate. a snapshot = an authoritative state + the local time it arrived.
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
                // PLACEHOLDER, spread player so they don't overlap
                TeleportTo(DebugSpawnPosition((int)OwnerClientId));
                _authoritativeState.Value = CaptureState(0);
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

            NetworkManager.NetworkTickSystem.Tick += OnNetworkTick;
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
                // movement is in OwnerTick; jump is an edge event so latch it here
                var kb = UnityEngine.InputSystem.Keyboard.current;
                if (kb != null && kb.spaceKey.wasPressedThisFrame)
                    _jumpLatched = true;
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
            var input = new InputPayload
            {
                Tick = tick,
                MoveDir = CameraRelativeDirection(ReadMoveInput()),
                Jump = _jumpLatched,
                Scoped = ReadScoped(),
                AimDir = CameraForward(),
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

        // pure remote: a new snapshot arrived. shift the latest into 'from' and store the new one as
        // 'to', each stamped with local arrival time (consumed by InterpolateTransformDelayed).
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

            Vector3 velocity = input.MoveDir * moveSpeed + Vector3.up * _verticalVelocity;
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
            };
        }

        // build a StatePayload from the controller's current transform, labeled with the given tick
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

        // scoped while right mouse is held; a level state read at tick time like WASD, not latched
        private static bool ReadScoped()
        {
            var mouse = UnityEngine.InputSystem.Mouse.current;
            return mouse != null && mouse.rightButton.isPressed;
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
            public bool Scoped; // right mouse held: face the aim direction and strafe
            public Vector3 AimDir; // camera's horizontal forward, the body faces this when scoped

            // one method both serializes and deserializes (s decides direction), so field order matters
            public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
            {
                s.SerializeValue(ref Tick);
                s.SerializeValue(ref MoveDir);
                s.SerializeValue(ref Jump);
                s.SerializeValue(ref Scoped);
                s.SerializeValue(ref AimDir);
            }
        }

        private struct StatePayload : INetworkSerializable
        {
            public int Tick;
            public Vector3 Position;
            public Quaternion Rotation;
            public float VerticalVelocity;

            public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
            {
                s.SerializeValue(ref Tick);
                s.SerializeValue(ref Position);
                s.SerializeValue(ref Rotation);
                s.SerializeValue(ref VerticalVelocity);
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
