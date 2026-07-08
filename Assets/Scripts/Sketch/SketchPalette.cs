using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace FriendSlop.Sketch
{
    // the witness's colour palette: a fixed grid of 16 swatches driving SketchCanvas's brush colour. not a
    // general art tool, the colours are chosen to notate the criminal's appearance attributes (skin tone,
    // shirt/pants colour) the way the sniper reads them, per the GDD (characters vary by head shape +
    // skin/shirt/pants colour, no facial features). so: neutrals to outline the head shape, a few skin
    // tones, and distinct clothing colours a witness can match to what they see.
    //
    // kept separate from SketchCanvas so the canvas stays a pure drawing surface and this owns only
    // colour selection. put this on the palette's UI parent; it builds its 16 swatch buttons at runtime
    // from one swatchTemplate (like the lobby UI is generated), so you don't hand-place 16.
    public class SketchPalette : MonoBehaviour
    {
        // the canvas whose brush colour this palette sets. found in the scene if unset.
        [SerializeField]
        private SketchCanvas canvas;

        // one disabled Button used as the template for every swatch (cloned once per colour under
        // swatchParent). its Image is tinted to the swatch colour. must have an Image (the colour patch).
        [SerializeField]
        private Button swatchTemplate;

        // where the cloned swatches are parented (e.g. a GridLayoutGroup). defaults to the template's parent.
        [SerializeField]
        private RectTransform swatchParent;

        // optional outline/highlight shown on the currently-selected swatch, so the witness can see which
        // colour is active. if set, it's re-parented under the selected swatch each pick.
        [SerializeField]
        private RectTransform selectionHighlight;

        // the 16 colours. editable in the Inspector; Reset() seeds the attribute-oriented default below.
        [SerializeField]
        private Color[] colors = DefaultColors();

        private readonly List<Button> _swatches = new();
        private int _selected = -1;

        // 16 defaults, grouped by the attribute they help notate. distinct enough to tell apart at the
        // 256^2 canvas resolution and after PNG round-trip.
        private static Color[] DefaultColors() => new[]
        {
            // row 1: neutrals / outline (head-shape linework, dark/light clothing).
            Hex("000000"), // black
            Hex("555555"), // dark grey
            Hex("AAAAAA"), // light grey
            Hex("FFFFFF"), // white

            // row 2: skin tones (a criminal attribute).
            Hex("FFD9B3"), // pale
            Hex("E0A878"), // tan
            Hex("A56A43"), // brown
            Hex("5A3825"), // dark brown

            // row 3: warm clothing.
            Hex("E23B3B"), // red
            Hex("F08C2E"), // orange
            Hex("F4D03F"), // yellow
            Hex("8E5A2E"), // saddle brown

            // row 4: cool clothing.
            Hex("3FA34D"), // green
            Hex("2E86C1"), // blue
            Hex("7D3C98"), // purple
            Hex("E28FB0"), // pink
        };

        private static Color Hex(string rgb) =>
            ColorUtility.TryParseHtmlString("#" + rgb, out var c) ? c : Color.magenta;

        // re-seed the Inspector array to the attribute-oriented defaults (right-click component -> Reset).
        private void Reset() => colors = DefaultColors();

        private void Awake()
        {
            if (canvas == null)
                canvas = FindFirstObjectByType<SketchCanvas>();
            if (swatchTemplate != null && swatchParent == null)
                swatchParent = swatchTemplate.transform.parent as RectTransform;

            BuildSwatches();
        }

        private void OnEnable()
        {
            // default the brush to the first swatch each time the palette opens, so the witness always
            // starts on a known colour (black outline) rather than whatever was left selected.
            if (_swatches.Count > 0)
                Select(0);
        }

        private void BuildSwatches()
        {
            if (swatchTemplate == null)
            {
                Debug.LogWarning("[SketchPalette] No swatchTemplate assigned, can't build swatches.", this);
                return;
            }

            swatchTemplate.gameObject.SetActive(false); // the template itself never shows.

            for (int i = 0; i < colors.Length; i++)
            {
                int index = i; // capture for the closure.
                Button swatch = Instantiate(swatchTemplate, swatchParent);
                swatch.gameObject.SetActive(true);
                swatch.name = $"Swatch_{i}";

                // tint the swatch's colour patch: prefer the Button's targetGraphic (what actually shows
                // the fill), falling back to any Image on the swatch.
                Image patch = swatch.targetGraphic as Image ?? swatch.GetComponent<Image>();
                if (patch != null)
                    patch.color = colors[i];

                swatch.onClick.AddListener(() => Select(index));
                _swatches.Add(swatch);
            }
        }

        // set the brush to swatch #index and move the selection highlight onto it.
        public void Select(int index)
        {
            if (index < 0 || index >= colors.Length || canvas == null)
                return;

            _selected = index;
            canvas.BrushColor = colors[index];

            if (selectionHighlight != null && index < _swatches.Count)
            {
                selectionHighlight.SetParent(_swatches[index].transform, worldPositionStays: false);
                selectionHighlight.anchoredPosition = Vector2.zero;
                selectionHighlight.SetAsLastSibling();
            }
        }

        // currently selected colour, or the brush's current colour if none picked yet.
        public Color SelectedColor =>
            _selected >= 0 && _selected < colors.Length ? colors[_selected]
            : canvas != null ? canvas.BrushColor
            : Color.black;
    }
}
