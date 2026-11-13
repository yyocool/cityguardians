using UnityEngine;
using UnityEngine.UI;

namespace CityGuardians.Gameplay
{
    /// <summary>
    /// 한글 글꼴. Phase 1에서는 Windows에 설치된 글꼴을 실행 중에 불러 쓴다 (글꼴 파일을 배포하지 않음).
    /// 모바일 확장 때는 배포 가능한 글꼴을 게임에 넣어야 한다.
    /// </summary>
    public static class UiFont
    {
        private static Font _font;

        public static Font Get()
        {
            if (_font != null) return _font;
            _font = Font.CreateDynamicFontFromOSFont(
                new[] { "Malgun Gothic", "맑은 고딕", "Noto Sans CJK KR", "NanumGothic", "Arial" }, 32);
            return _font;
        }
    }

    /// <summary>3D 공간에 떠 있는 글자. 이름표, 말풍선, 간판, 피해 숫자에 쓴다.</summary>
    public sealed class WorldLabel : MonoBehaviour
    {
        private const float MaxPixelsPerUnit = 220f;
        private const float MaxFontSize = 300f;

        public Text Text { get; private set; }
        public Image Background { get; private set; }
        public bool faceCamera = true;

        private CanvasGroup _group;

        public float Alpha
        {
            get => _group.alpha;
            set => _group.alpha = value;
        }

        /// <param name="worldHeight">글자 한 줄의 높이 (미터).</param>
        public static WorldLabel Create(Transform parent, Vector3 localPos, string text, float worldHeight,
            Color color, Color? background = null, float worldWidth = 4f, bool faceCamera = true)
        {
            var go = new GameObject("Label");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;

            // 가까이서도 흐리지 않게 글자를 크게 그리고 줄여서 놓는다. 글꼴 크기는 300을 넘기지 않는다.
            float pixelsPerUnit = Mathf.Min(MaxPixelsPerUnit, MaxFontSize / Mathf.Max(0.01f, worldHeight));

            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            var rect = (RectTransform)go.transform;
            float pixelHeight = worldHeight * pixelsPerUnit * 1.6f;
            rect.sizeDelta = new Vector2(worldWidth * pixelsPerUnit, pixelHeight);
            rect.localScale = Vector3.one / pixelsPerUnit;

            var label = go.AddComponent<WorldLabel>();
            label._group = go.AddComponent<CanvasGroup>();
            label._group.interactable = false;
            label._group.blocksRaycasts = false;
            label.faceCamera = faceCamera;

            if (background.HasValue)
            {
                var bgObject = new GameObject("Bg");
                bgObject.transform.SetParent(go.transform, false);
                var bgRect = bgObject.AddComponent<RectTransform>();
                bgRect.anchorMin = Vector2.zero;
                bgRect.anchorMax = Vector2.one;
                bgRect.offsetMin = Vector2.zero;
                bgRect.offsetMax = Vector2.zero;
                label.Background = bgObject.AddComponent<Image>();
                label.Background.color = background.Value;
                label.Background.raycastTarget = false;
            }

            var textObject = new GameObject("Text");
            textObject.transform.SetParent(go.transform, false);
            var textRect = textObject.AddComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(8f, 0f);
            textRect.offsetMax = new Vector2(-8f, 0f);

            var uiText = textObject.AddComponent<Text>();
            uiText.font = UiFont.Get();
            uiText.fontSize = Mathf.RoundToInt(worldHeight * pixelsPerUnit);
            uiText.alignment = TextAnchor.MiddleCenter;
            uiText.horizontalOverflow = HorizontalWrapMode.Overflow;
            uiText.verticalOverflow = VerticalWrapMode.Overflow;
            uiText.color = color;
            uiText.raycastTarget = false;
            uiText.text = text;
            label.Text = uiText;

            if (!background.HasValue)
            {
                var outline = textObject.AddComponent<Outline>();
                outline.effectColor = new Color(0f, 0f, 0f, 0.85f);
                outline.effectDistance = new Vector2(2f, -2f);
            }

            return label;
        }

        public void SetText(string text)
        {
            Text.text = text;
        }

        /// <summary>글 길이와 줄 수에 맞춰 바탕의 크기를 조절한다.</summary>
        public void FitWidthToText()
        {
            float width = Text.preferredWidth + Text.fontSize * 1.2f;
            float height = Text.preferredHeight + Text.fontSize * 0.5f;
            var rect = (RectTransform)transform;
            rect.sizeDelta = new Vector2(width, height);
        }

        /// <summary>긴 글을 두 줄로 나눈다. 가운데에서 가장 가까운 띄어쓰기에서 끊는다.</summary>
        public static string WrapInTwo(string text, int maxLineLength)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLineLength || text.IndexOf('\n') >= 0) return text;
            int middle = text.Length / 2;
            int best = -1;
            for (int i = 1; i < text.Length - 1; i++)
            {
                if (text[i] != ' ') continue;
                if (best < 0 || Mathf.Abs(i - middle) < Mathf.Abs(best - middle)) best = i;
            }
            return best < 0 ? text : text.Substring(0, best) + "\n" + text.Substring(best + 1);
        }

        private void LateUpdate()
        {
            if (!faceCamera) return;
            Camera cam = Camera.main;
            if (cam == null) return;
            // 캔버스의 앞면은 -Z를 향하므로 카메라가 보는 방향과 같은 쪽을 보게 한다.
            transform.rotation = Quaternion.LookRotation(cam.transform.forward, Vector3.up);
        }
    }
}
