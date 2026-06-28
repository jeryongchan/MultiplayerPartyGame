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
    // Simulate() is the linchpin: owner and server must run identical deterministic math, or every
    // server update looks like a misprediction and rubber-bands. reconcile here is a hard snap
    // (correct but steppy at high ping); smooth blending comes later.
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

        // owner render-interp: the two most recent predicted tick poses + the latest tick's time. the
        // mesh is drawn Lerp(prev, curr, timeSinceTick / tickDt) each frame.
        private Vector3 _prevPos, _currPos;
        private Quaternion _prevRot, _currRot;
        private float _lastTickTime;

        // authoritative state, server-write, read by everyone.
        private readonly NetworkVariable<StatePayload> _serverState =
            new NetworkVariable<StatePayload>(writePerm: NetworkVariableWritePermission.Server);

        // owner-side ring buffers, indexed by tick % BufferSize.
        private readonly InputPayload[] _inputBuffer = new InputPayload[BufferSize];
        private readonly StatePayload[] _stateBuffer = new StatePayload[BufferSize];

        // mutable sim state the owner predicts forward; the server keeps its own copy in _serverState.
        private float _verticalVelocity;
        private bool _jumpLatched;
        private int _currentTick;

        // remote interp: keep the two most recent snapshots and render at (now - interpolationDelay)
        // by lerping between them, ~one tick in the past so the "to" snapshot has arrived and we never
        // extrapolate.
        private StatePayload _snapFrom, _snapTo;
        private float _snapFromTime, _snapToTime;
        private int _snapCount;

        private void Awake() => _controller = GetComponent<CharacterController>();

        private float TickDt =>
            NetworkManager != null ? 1f / NetworkManager.NetworkConfig.TickRate : 1f / 30f;

        public override void OnNetworkSpawn()
        {
            if (IsServer)
            {
                int id = (int)OwnerClientId;
                float offset = ((id + 1) / 2) * 2f * (id % 2 == 0 ? 1f : -1f);
                TeleportTo(new Vector3(offset, 1.1f, 0f));
                _serverState.Value = new StatePayload
                {
                    Tick = 0,
                    Position = transform.position,
                    Rotation = transform.rotation,
                    VerticalVelocity = 0f,
                };
            }

            if (IsOwner)
            {
                // seed both interp poses to spawn so the mesh doesn't lerp in from the origin
                _prevPos = _currPos = transform.position;
                _prevRot = _currRot = transform.rotation;
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
                RecordSnapshot(_serverState.Value);
            }

            _serverState.OnValueChanged += OnServerStateChanged;

            NetworkManager.NetworkTickSystem.Tick += OnNetworkTick;
        }

        public override void OnNetworkDespawn()
        {
            _serverState.OnValueChanged -= OnServerStateChanged;
            if (NetworkManager != null && NetworkManager.NetworkTickSystem != null)
                NetworkManager.NetworkTickSystem.Tick -= OnNetworkTick;
        }

        private void Update()
        {
            // pure remote (our copy of someone else): interpolate between received snapshots, which
            // arrive delayed, so we render in the past between two of them.
            if (!IsOwner && !IsServer)
            {
                InterpolateRemote();
                return;
            }

            // host-only path, inert on a dedicated server: the host is the authority, so its copy of a
            // remote advances at tick rate inside SubmitInputServerRpc. we still render-interpolate the
            // mesh here like the owner does. on a dedicated server this branch never runs.
            if (!IsOwner)
            {
                RenderInterpolateVisual();
                return;
            }

            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb != null && kb.spaceKey.wasPressedThisFrame)
                _jumpLatched = true;

            // temp reconcile test: press T to yank the transform to a fake position; the next
            // authoritative state should snap us back.
            if (kb != null && kb.tKey.wasPressedThisFrame)
                TeleportTo(transform.position + new Vector3(5f, 0f, 0f));

            RenderInterpolateVisual();
        }

        // draw the mesh smoothly between the last two recorded tick poses. used by the owner and the
        // host's copies of remotes; both advance the root at tick rate and want full-framerate motion.
        private void RenderInterpolateVisual()
        {
            if (visual == null)
                return;
            float alpha = Mathf.Clamp01((Time.time - _lastTickTime) / TickDt);
            visual.position = Vector3.Lerp(_prevPos, _currPos, alpha);
            visual.rotation = Quaternion.Slerp(_prevRot, _currRot, alpha);
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
            };
            _jumpLatched = false;

            // predict with the shared step. Simulate() leaves the controller at the resulting position,
            // so no extra ApplyState is needed (that would re-teleport).
            StatePayload predicted = Simulate(CaptureState(tick), input, TickDt);

            // roll render-interp poses forward: last tick's result becomes prev, this one curr
            _prevPos = _currPos;
            _prevRot = _currRot;
            _currPos = transform.position;
            _currRot = transform.rotation;
            _lastTickTime = Time.time;

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
            StatePayload next;
            if (IsOwner)
            {
                // host-owner: OwnerTick already ran Simulate this tick, so just publish what it produced.
                // re-simulating here would apply physics twice (double-speed jump/gravity).
                next = _stateBuffer[input.Tick % BufferSize];
            }
            else
            {
                var prev = _serverState.Value;
                next = Simulate(prev, input, TickDt);
                ApplyState(next);
            }

            if (!IsOwner)
            {
                next.Tick = input.Tick;
                next.Position = transform.position;
                next.Rotation = transform.rotation;
                next.VerticalVelocity = _verticalVelocity;
            }
            _serverState.Value = next;

            // record tick poses so the host can render-interpolate this copy's mesh. non-owner copies
            // only: the host's own player records its poses in OwnerTick; doing it here too would
            // overwrite prev with curr and freeze the mesh.
            if (!IsOwner)
            {
                _prevPos = _currPos;
                _prevRot = _currRot;
                _currPos = transform.position;
                _currRot = transform.rotation;
                _lastTickTime = Time.time;
            }
        }

        // reconcile (owner) / follow (remote)
        private void OnServerStateChanged(StatePayload _, StatePayload server)
        {
            if (IsServer)
                return; // server is already the authority, nothing to reconcile

            if (IsOwner)
                Reconcile(server);
            else
                RecordSnapshot(server); // remote copy: buffer for interpolation in Update()
        }

        // new authoritative state for a remote: shift the latest into 'from', store the new one as
        // 'to', each stamped with local arrival time.
        private void RecordSnapshot(StatePayload server)
        {
            _snapFrom = _snapTo;
            _snapFromTime = _snapToTime;
            _snapTo = server;
            _snapToTime = Time.time;
            if (_snapCount < 2)
                _snapCount++;
        }

        // runs every render frame on a remote copy. render the mesh at (now - interpolationDelay) by
        // lerping between the two snapshots, so there's always a snapshot on each side (no overshoot).
        private void InterpolateRemote()
        {
            if (_snapCount == 0 || visual == null)
                return;

            if (_snapCount == 1)
            {
                visual.SetPositionAndRotation(_snapTo.Position, _snapTo.Rotation);
                return;
            }

            float renderTime = Time.time - interpolationDelay;
            float span = _snapToTime - _snapFromTime;
            float t = span > 0f ? Mathf.Clamp01((renderTime - _snapFromTime) / span) : 1f;

            visual.position = Vector3.Lerp(_snapFrom.Position, _snapTo.Position, t);
            visual.rotation = Quaternion.Slerp(_snapFrom.Rotation, _snapTo.Rotation, t);
        }

        private void Reconcile(StatePayload server)
        {
            // how far off was our prediction for the tick the server just processed?
            StatePayload predicted = _stateBuffer[server.Tick % BufferSize];
            float error = Vector3.Distance(predicted.Position, server.Position);
            if (error < reconcileThreshold)
                return; // close enough, keep our smoother local prediction

            // snap to the authoritative state, then replay every input the server hasn't seen yet
            ApplyState(server);
            _stateBuffer[server.Tick % BufferSize] = server;

            for (int t = server.Tick + 1; t < _currentTick; t++)
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

            Quaternion rotation = transform.rotation;
            if (input.MoveDir.sqrMagnitude > 0.001f)
            {
                Quaternion targetRot = Quaternion.LookRotation(input.MoveDir);
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

        // snapshot the controller's current transform into a state at the given tick
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

        private struct InputPayload : INetworkSerializable
        {
            public int Tick;
            public Vector3 MoveDir;
            public bool Jump;

            public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
            {
                s.SerializeValue(ref Tick);
                s.SerializeValue(ref MoveDir);
                s.SerializeValue(ref Jump);
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
