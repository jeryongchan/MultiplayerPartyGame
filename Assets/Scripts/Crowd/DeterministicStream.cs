using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace FriendSlop.Crowd
{
    // base for a zero-bandwidth deterministic spawn stream (the crowd, the buses). items are not replicated:
    // the server writes one shared seed + one stream-start time, and every machine reconstructs the identical
    // schedule from that seed and the synchronized NetworkManager.ServerTime. so a given item
    // spawns, moves, and despawns at the same instant on the host and every client, for the cost of essentially
    // one ulong regardless of how many items there are.
    //
    // model: one or more numbered streams (StreamCount). in a stream, item #k is "born" at
    // streamStart + k * Interval(stream), runs once, then despawns, never recycled. birth time and all
    // per-item traits are pure functions of (seed, stream, index) (see RngFor), so a late
    // joiner that skips earlier indices still computes bit-identical results.
    //
    // subclasses fill the small set of hooks below; this base owns the seed/clock NetworkVariables, the
    // two-variable sync latch, the deterministic RNG, and the per-frame catch-up spawn + Finished despawn loops.
    public abstract class DeterministicStream : NetworkBehaviour
    {
        // shared seed: the host writes it once, clients read it. every per-item trait derives from this, so the
        // whole schedule is reproducible on every machine. server-writable only; forced non-zero so 0 reliably
        // means "not set yet" for the latch below.
        private readonly NetworkVariable<ulong> _seed =
            new(writePerm: NetworkVariableWritePermission.Server);

        // the shared-clock time each stream counts index 0 from. the host writes it so every machine uses the
        // same value; capturing it locally would differ per machine (clients see the seed later) and desync
        // the indices. server-writable only; 0 means "not set yet".
        private readonly NetworkVariable<double> _streamStart =
            new(writePerm: NetworkVariableWritePermission.Server);

        // next index to instantiate, per stream. same on every machine since births are clock-derived.
        private int[] _nextIndex;

        private bool _streaming;

        // live items, polled each frame for Finished so we can despawn them. local bookkeeping only.
        private readonly List<IStreamItem> _active = new();

        // the shared clock time index 0 is counted from (server-written, same on every machine)
        protected double StreamStartTime => _streamStart.Value;

        // the shared seed (same on every machine). for subclasses needing a custom-salted RNG stream
        // independent of RngFor, e.g. the crowd rolls appearance off a distinct salt so it never
        // coincides with the movement-trait draws.
        protected ulong Seed => _seed.Value;

        // number of independent numbered streams. default 1 (the crowd); buses override to lane count.
        protected virtual int StreamCount => 1;

        // seconds between consecutive items entering the given stream. lower = denser.
        protected abstract float Interval(int stream);

        // offset applied to the stream-start time when the server first sets it. default 0 (start counting
        // from now). the crowd returns a negative value (minus one walk-duration) so the street opens at
        // steady-state density instead of filling from empty.
        protected virtual double StreamStartOffset => 0d;

        // true if item #index in stream, born at birth,
        // is still mid-run at now. a late joiner uses this to skip items that already finished
        // before it arrived (instantiating them just to despawn next frame is wasted work).
        protected abstract bool IsStillAlive(int stream, int index, double birth, double now);

        // instantiate item #index in stream locally, with its
        // seed-derived traits and clock-derived birth time. return the spawned item (so the
        // base can poll it for despawn), or null if nothing was spawned. every trait must be a pure function of
        // (seed, stream, index) so every machine, including late joiners, builds the identical item.
        protected abstract IStreamItem SpawnItem(int stream, int index, double birth);

        // called once both NetworkVariables have arrived and the stream is about to begin. optional.
        protected virtual void OnStreamReady() { }

        // called just before a finished item is destroyed, for any per-item cleanup (e.g. an index map)
        protected virtual void OnDespawn(IStreamItem item) { }

        // a fresh deterministic PRNG for one item: same (seed, stream, index) yields the same sequence on
        // every machine, at any join time. stream and index are mixed into the seed (Knuth multiplicative hash)
        // so items, and the streams themselves, get distinct but reproducible sequences.
        protected System.Random RngFor(int stream, int index) =>
            new((int)(_seed.Value
                ^ (ulong)((index + 1) * 2654435761)
                ^ (ulong)((stream + 1) * 40503)));

        public override void OnNetworkSpawn()
        {
            if (IsServer)
            {
                _seed.Value =
                    ((ulong)Random.Range(int.MinValue, int.MaxValue) ^ (ulong)System.DateTime.Now.Ticks) | 1UL;
                _streamStart.Value = NetworkManager.ServerTime.Time + StreamStartOffset;
            }

            _seed.OnValueChanged += OnStreamVarChanged;
            _streamStart.OnValueChanged += OnStreamVarChanged;
            TryBeginStream();
        }

        public override void OnNetworkDespawn()
        {
            _seed.OnValueChanged -= OnStreamVarChanged;
            _streamStart.OnValueChanged -= OnStreamVarChanged;
        }

        private void OnStreamVarChanged<T>(T _, T __) => TryBeginStream();

        // latch the stream once both NetworkVariables have arrived. on clients they sync independently and may
        // land in separate frames; starting before _streamStart is set would count births from 0 and desync the
        // whole schedule against the host. idempotent.
        private void TryBeginStream()
        {
            if (_streaming || _seed.Value == 0 || _streamStart.Value == 0)
                return;
            if (StreamCount < 1)
                return;

            _nextIndex = new int[StreamCount];
            OnStreamReady();
            _streaming = true;
        }

        private void Update()
        {
            if (!_streaming)
                return;

            double now = NetworkManager.ServerTime.Time;

            // spawn every item (per stream) whose birth time has arrived and that hasn't already finished
            for (int stream = 0; stream < _nextIndex.Length; stream++)
            {
                float interval = Interval(stream);
                while (_streamStart.Value + _nextIndex[stream] * interval <= now)
                {
                    int index = _nextIndex[stream];
                    double birth = _streamStart.Value + index * interval;
                    if (IsStillAlive(stream, index, birth, now))
                    {
                        IStreamItem item = SpawnItem(stream, index, birth);
                        if (item != null)
                            _active.Add(item);
                    }
                    _nextIndex[stream]++;
                }
            }

            // despawn any that finished their run. IStreamItem is only ever a Component (Npc/Bus), so cast to
            // one and use Unity's lifecycle-aware == (a plain reference null check misses a destroyed object).
            for (int i = _active.Count - 1; i >= 0; i--)
            {
                var component = _active[i] as Component;
                if (component == null || _active[i].Finished)
                {
                    if (component != null)
                    {
                        OnDespawn(_active[i]);
                        Destroy(component.gameObject);
                    }
                    _active.RemoveAt(i);
                }
            }
        }
    }
}
