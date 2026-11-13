using CityGuardians.Gameplay;
using UnityEngine;
using UnityEngine.UI;

namespace CityGuardians.UI
{
    /// <summary>화면 UI 조각을 코드로 만드는 도우미. 기준 해상도는 1920×1080.</summary>
    public static class UiFactory
    {
        public static readonly Color PanelColor = new Color(0.08f, 0.10f, 0.16f, 0.82f);
        public static readonly Color TextColor = new Color(0.97f, 0.97f, 0.95f);
        public static readonly Color Accent = new Color(1f, 0.84f, 0.25f);

        public static Canvas CreateCanvas(string name, int sortingOrder)
        {
            var go = new GameObject(name);
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = sortingOrder;
            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            return canvas;
        }

        /// <param name="anchor">부모 안에서의 기준점 (0~1). 피벗도 같은 값으로 둔다.</param>
        public static RectTransform Rect(Transform parent, string name, Vector2 anchor, Vector2 position, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = anchor;
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
            return rect;
        }

        public static RectTransform Stretch(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            return rect;
        }

        public static Image Fill(RectTransform rect, Color color)
        {
            var image = rect.gameObject.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            return image;
        }

        public static Image Panel(Transform parent, string name, Vector2 anchor, Vector2 position, Vector2 size, Color color)
        {
            return Fill(Rect(parent, name, anchor, position, size), color);
        }

        /// <summary>부모를 가득 채우는 글자. padding만큼 안쪽으로 들어간다.</summary>
        public static Text Label(Transform parent, string name, int fontSize, TextAnchor alignment, Color color,
            float padding = 0f, bool outline = false)
        {
            RectTransform rect = Stretch(parent, name);
            rect.offsetMin = new Vector2(padding, padding * 0.5f);
            rect.offsetMax = new Vector2(-padding, -padding * 0.5f);
            return AddText(rect, fontSize, alignment, color, outline);
        }

        public static Text LabelAt(Transform parent, string name, Vector2 anchor, Vector2 position, Vector2 size,
            int fontSize, TextAnchor alignment, Color color, bool outline = false)
        {
            return AddText(Rect(parent, name, anchor, position, size), fontSize, alignment, color, outline);
        }

        private static Text AddText(RectTransform rect, int fontSize, TextAnchor alignment, Color color, bool outline)
        {
            var text = rect.gameObject.AddComponent<Text>();
            text.font = UiFont.Get();
            text.fontSize = fontSize;
            text.alignment = alignment;
            text.color = color;
            text.raycastTarget = false;
            text.supportRichText = false;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            if (outline)
            {
                var effect = rect.gameObject.AddComponent<Outline>();
                effect.effectColor = new Color(0f, 0f, 0f, 0.8f);
                effect.effectDistance = new Vector2(2f, -2f);
            }
            return text;
        }

        /// <summary>왼쪽에서 차오르는 막대. 돌려준 RectTransform의 가로 비율을 SetBar로 바꾼다.</summary>
        public static RectTransform Bar(Transform parent, string name, Vector2 anchor, Vector2 position, Vector2 size,
            Color back, Color fill)
        {
            Image background = Panel(parent, name, anchor, position, size, back);
            RectTransform track = Stretch(background.transform, "Track");
            track.offsetMin = new Vector2(3f, 3f);
            track.offsetMax = new Vector2(-3f, -3f);
            RectTransform rect = Stretch(track, "Fill");
            Fill(rect, fill);
            return rect;
        }

        public static void SetBar(RectTransform fill, float ratio)
        {
            fill.anchorMax = new Vector2(Mathf.Clamp01(ratio), 1f);
        }
    }
}
