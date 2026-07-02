using UnityEngine;
using UnityEngine.UI;

namespace FriendSlop.Sketch
{
    // shows the synced sketch. subscribes to SketchSyncManager.SketchReceived and blits the received PNG
    // bytes onto its RawImage. put this on a RawImage (e.g. a fullscreen reveal image, or a small preview)
    // on any client, it decodes and displays whatever the witness submitted.
    //
    // phase-gating (show only during SketchReveal) is layered on later via PhaseActivatedObject; for now
    // this just displays the latest sketch whenever one arrives.
    [RequireComponent(typeof(RawImage))]
    public class SketchDisplay : MonoBehaviour
    {
        private RawImage _image;
        private Texture2D _texture;

        private void Awake() => _image = GetComponent<RawImage>();

        private void OnEnable()
        {
            TrySubscribe();
            // if a sketch already arrived before we enabled, show it immediately
            if (SketchSyncManager.Instance != null && SketchSyncManager.Instance.LatestSketchPng != null)
                Display(SketchSyncManager.Instance.LatestSketchPng);
        }

        private void OnDisable()
        {
            if (SketchSyncManager.Instance != null)
                SketchSyncManager.Instance.SketchReceived -= Display;
        }

        private void Start() => TrySubscribe();

        private void TrySubscribe()
        {
            if (SketchSyncManager.Instance == null)
                return;
            SketchSyncManager.Instance.SketchReceived -= Display; // idempotent
            SketchSyncManager.Instance.SketchReceived += Display;
        }

        private void Display(byte[] png)
        {
            if (png == null || png.Length == 0)
                return;
            if (_texture == null)
                _texture = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false) { filterMode = FilterMode.Point };
            // LoadImage resizes the texture to the PNG's dimensions automatically
            _texture.LoadImage(png);
            _image.texture = _texture;
        }
    }
}
