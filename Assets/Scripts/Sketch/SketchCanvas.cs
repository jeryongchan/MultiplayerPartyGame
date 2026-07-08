using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace FriendSlop.Sketch
{
    // local, no netcode: a paintable canvas. a RawImage is backed by a Texture2D; dragging the mouse over
    // it paints a filled circle of pixels in a single brush color. this is the witness's drawing surface,
    // testable in one editor instance before any networking exists.
    //
    // put this on the RawImage GameObject inside a Canvas. the RawImage's RectTransform defines the
    // paintable area; we convert pointer position to local rect to texel and stamp a brush there.
    // painting happens between mouse-down and mouse-up while the pointer is over the image.
    //
    // texture space is small on purpose (default 256^2) so the eventual PNG payload is tiny.
    [RequireComponent(typeof(RawImage))]
    public class SketchCanvas : MonoBehaviour
    {
        [Header("Canvas")]
        // pixel resolution of the drawing texture (square). keep small, it becomes a network payload later.
        [SerializeField]
        private int resolution = 256;

        [SerializeField]
        private Color backgroundColor = Color.white;

        [Header("Brush")]
        [SerializeField]
        private Color brushColor = Color.black;

        // brush radius in texture pixels
        [SerializeField]
        private int brushRadius = 4;

        // colour subsequent strokes paint in. set by the palette when the witness picks a swatch.
        public Color BrushColor
        {
            get => brushColor;
            set => brushColor = value;
        }

        private RawImage _image;
        private RectTransform _rect;
        private Texture2D _texture;
        private Color32[] _pixels;

        // last painted texel while dragging, so we can interpolate between frames (fast drags don't
        // leave gaps). -1 means "no previous point this stroke".
        private Vector2Int _lastTexel = new Vector2Int(-1, -1);
        private bool _dirty;

        private void Awake()
        {
            _image = GetComponent<RawImage>();
            _rect = (RectTransform)transform;
            BuildTexture();
        }

        private void BuildTexture()
        {
            _texture = new Texture2D(resolution, resolution, TextureFormat.RGBA32, mipChain: false)
            {
                // point filtering keeps the brush crisp at low res rather than blurry
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            _pixels = new Color32[resolution * resolution];
            _image.texture = _texture;
            Clear();
        }

        // wipe the canvas back to the background color
        public void Clear()
        {
            Color32 bg = backgroundColor;
            for (int i = 0; i < _pixels.Length; i++)
                _pixels[i] = bg;
            _texture.SetPixels32(_pixels);
            _texture.Apply(updateMipmaps: false);
        }

        // the current drawing as PNG bytes, used by the network submit step
        public byte[] EncodeToPng() => _texture.EncodeToPNG();

        // send the current drawing to everyone via the sync manager
        public void Submit()
        {
            if (SketchSyncManager.Instance == null)
            {
                Debug.LogWarning("[SketchCanvas] No SketchSyncManager in scene, can't submit.");
                return;
            }
            byte[] png = EncodeToPng();
            SketchSyncManager.Instance.SubmitSketch(png);
            Debug.Log($"[SketchCanvas] Submitted sketch ({png.Length} bytes).");
        }

        private void Update()
        {
            var mouse = Mouse.current;
            if (mouse == null)
                return;

            // quick hotkeys while iterating (real clear/submit UI comes later):
            //   C     = clear the canvas
            //   Enter = submit the current drawing (PNG to SketchSyncManager to all clients)
            var kb = Keyboard.current;
            if (kb != null && kb.cKey.wasPressedThisFrame)
                Clear();
            if (kb != null && kb.enterKey.wasPressedThisFrame)
                Submit();

            if (mouse.leftButton.isPressed)
            {
                if (TryGetTexel(mouse.position.ReadValue(), out Vector2Int texel))
                {
                    if (_lastTexel.x >= 0)
                        StampLine(_lastTexel, texel); // fill the gap since last frame
                    else
                        StampCircle(texel.x, texel.y);
                    _lastTexel = texel;
                }
            }
            else
            {
                _lastTexel = new Vector2Int(-1, -1); // stroke ended
            }

            if (_dirty)
            {
                _texture.SetPixels32(_pixels);
                _texture.Apply(updateMipmaps: false);
                _dirty = false;
            }
        }

        // pointer (screen) position to texel coordinate inside the RawImage rect. returns false if the
        // pointer is outside the image. uses RectTransformUtility so it works for any canvas render mode.
        private bool TryGetTexel(Vector2 screenPos, out Vector2Int texel)
        {
            texel = default;

            // overlay canvases pass a null camera to the util; others use the canvas camera
            Canvas canvas = _image.canvas;
            Camera cam = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay
                ? canvas.worldCamera
                : null;

            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(_rect, screenPos, cam, out Vector2 local))
                return false;

            // local is relative to the rect's pivot; convert to 0..1 across the rect
            Rect r = _rect.rect;
            float u = (local.x - r.xMin) / r.width;
            float v = (local.y - r.yMin) / r.height;
            if (u < 0f || u > 1f || v < 0f || v > 1f)
                return false;

            texel = new Vector2Int(
                Mathf.Clamp((int)(u * resolution), 0, resolution - 1),
                Mathf.Clamp((int)(v * resolution), 0, resolution - 1)
            );
            return true;
        }

        // stamp a filled circle of brushColor centered at (cx,cy) into the pixel buffer
        private void StampCircle(int cx, int cy)
        {
            Color32 c = brushColor;
            int r2 = brushRadius * brushRadius;
            int minX = Mathf.Max(0, cx - brushRadius);
            int maxX = Mathf.Min(resolution - 1, cx + brushRadius);
            int minY = Mathf.Max(0, cy - brushRadius);
            int maxY = Mathf.Min(resolution - 1, cy + brushRadius);

            for (int y = minY; y <= maxY; y++)
            {
                int dy = y - cy;
                for (int x = minX; x <= maxX; x++)
                {
                    int dx = x - cx;
                    if (dx * dx + dy * dy <= r2)
                        _pixels[y * resolution + x] = c;
                }
            }
            _dirty = true;
        }

        // stamp circles along the segment from a to b so a fast drag leaves a continuous line, not dots
        private void StampLine(Vector2Int a, Vector2Int b)
        {
            int steps = Mathf.Max(Mathf.Abs(b.x - a.x), Mathf.Abs(b.y - a.y));
            if (steps <= 0)
            {
                StampCircle(b.x, b.y);
                return;
            }
            for (int i = 0; i <= steps; i++)
            {
                float t = (float)i / steps;
                StampCircle(
                    Mathf.RoundToInt(Mathf.Lerp(a.x, b.x, t)),
                    Mathf.RoundToInt(Mathf.Lerp(a.y, b.y, t))
                );
            }
        }
    }
}
