using System;
using Unity.Netcode;
using UnityEngine;

namespace FriendSlop.Sketch
{
    // the sketch's one networked deliverable (GDD: "canvas texture synced as a single end-of-phase
    // payload"). the witness submits their canvas as PNG bytes; this rides client to server to all
    // clients, and the server caches the latest submission for the round.
    //
    // one scene NetworkBehaviour (put it on the GameManagers object alongside
    // GameFlowManager/RoleRegistry/ScoreManager). kept separate from SketchCanvas so the canvas stays a
    // pure local drawing surface and the netcode lives in one place.
    //
    // why RPC plus a cached byte[] instead of a NetworkVariable: the sketch is a one-shot blob, not a
    // continuously-changing value. RPC is NGO's standard path for large one-off payloads (a byte[]
    // NetworkVariable churns GC and bumps size limits). late-joiner resync (server pushes its cache to a
    // newly-connected client) is a later addition; for now everyone present at submit time gets it.
    public class SketchSyncManager : NetworkBehaviour
    {
        public static SketchSyncManager Instance { get; private set; }

        // server-only cache of the latest submitted sketch this round (so a resync/late-joiner path can
        // read it later). on clients this is filled by the broadcast so the display can read it.
        private byte[] _latestSketchPng;

        // the most recently received sketch PNG (server cache or client copy), or null if none
        public byte[] LatestSketchPng => _latestSketchPng;

        // fires on every machine when a new sketch arrives (the display subscribes to this)
        public event Action<byte[]> SketchReceived;

        private void Awake() => Instance = this;

        public override void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
            base.OnDestroy();
        }

        // called by the witness's SketchCanvas on submit. sends the PNG to the server. (owner-side role
        // gating happens at the canvas; the server re-checks nothing yet, the sketch is cosmetic. a
        // malicious client could submit a bogus image, which only misleads the hunters who chose to trust
        // it; acceptable, same spirit as the cosmetic laser.)
        public void SubmitSketch(byte[] png)
        {
            if (png == null || png.Length == 0)
                return;
            SubmitSketchServerRpc(png);
        }

        // client to server: the witness's submitted canvas. cache it, then fan it out to everyone.
        [Rpc(SendTo.Server)]
        private void SubmitSketchServerRpc(byte[] png)
        {
            _latestSketchPng = png;
            Receive(png); // the server/host is also a viewer
            BroadcastSketchClientRpc(png);
        }

        // server to all clients (host already handled above, so skip it to avoid a double-apply)
        [Rpc(SendTo.NotServer)]
        private void BroadcastSketchClientRpc(byte[] png) => Receive(png);

        private void Receive(byte[] png)
        {
            _latestSketchPng = png;
            SketchReceived?.Invoke(png);
        }
    }
}
