using CityGuardians.Services;
using UnityEngine;
using UnityEngine.UI;

namespace CityGuardians.UI
{
    /// <summary>
    /// 대시보드 카드의 디자인 시스템 (docs/HUD_LAYOUT_SYSTEM.md 2장): 반투명한 어두운 유리, 얇은 테두리,
    /// 둥근 모서리, 옅은 그림자. 그림 파일을 쓰지 않고 작은 무늬를 실행 중에 한 번 만들어서 모든 카드가 함께 쓴다.
    /// </summary>
    public static class DashboardStyle
    {
        public static readonly Color Glass = new Color(0.055f, 0.075f, 0.115f, 0.80f);
        public static readonly Color GlassHover = new Color(0.10f, 0.13f, 0.19f, 0.88f);
        public static readonly Color Border = new Color(1f, 1f, 1f, 0.17f);
        public static readonly Color Shadow = new Color(0f, 0f, 0f, 0.36f);
        public static readonly Color Divider = new Color(1f, 1f, 1f, 0.09f);
        public static readonly Color Text = new Color(0.96f, 0.97f, 0.98f);
        public static readonly Color Dim = new Color(0.68f, 0.72f, 0.79f);
        public static readonly Color Good = new Color(0.45f, 0.86f, 0.56f);
        public static readonly Color Bad = new Color(1f, 0.50f, 0.44f);
        public static readonly Color Track = new Color(1f, 1f, 1f, 0.10f);
        public static readonly Color TagBack = new Color(1f, 0.84f, 0.25f, 0.16f);
        public static readonly Color TagText = new Color(1f, 0.88f, 0.48f);
        public static readonly Color ChipBack = new Color(1f, 1f, 1f, 0.10f);
        public static readonly Color AlertBack = new Color(0.74f, 0.15f, 0.14f, 0.90f);
        public static readonly Color Calm = new Color(1f, 1f, 1f, 0.05f);
        public static readonly Color Offline = new Color(1f, 0.78f, 0.36f);

        private const int TextureSize = 32;
        private const float CornerRadius = 10f;
        private const float SliceBorder = 13f;

        private static Sprite _rounded;
        private static Sprite _ring;
        private static Sprite _soft;

        /// <summary>속이 찬 둥근 사각형. 9조각으로 늘여서 어떤 크기에도 쓴다.</summary>
        public static Sprite Rounded => _rounded != null ? _rounded : _rounded = Make("DashboardRounded", 0);
        /// <summary>둥근 사각형의 얇은 테두리.</summary>
        public static Sprite Ring => _ring != null ? _ring : _ring = Make("DashboardRing", 1);
        /// <summary>가장자리가 번지는 그림자.</summary>
        public static Sprite Soft => _soft != null ? _soft : _soft = Make("DashboardSoft", 2);

        private static Sprite Make(string name, int kind)
        {
            var texture = new Texture2D(TextureSize, TextureSize, TextureFormat.RGBA32, false)
            {
                name = name, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave,
            };
            var pixels = new Color32[TextureSize * TextureSize];
            for (int y = 0; y < TextureSize; y++)
            {
                for (int x = 0; x < TextureSize; x++)
                {
                    float alpha;
                    if (kind == 2)
                    {
                        // 안쪽으로 6 들어간 둥근 사각형에서 바깥으로 갈수록 옅어진다.
                        float d = Distance(x + 0.5f, y + 0.5f, 6f, CornerRadius);
                        float t = Mathf.Clamp01(0.5f - d / 12f);
                        alpha = t * t;
                    }
                    else
                    {
                        float d = Distance(x + 0.5f, y + 0.5f, 0f, CornerRadius);
                        alpha = Mathf.Clamp01(0.5f - d);
                        // 테두리: 가장자리에서 1.25 안쪽까지만 남긴다.
                        if (kind == 1) alpha *= Mathf.Clamp01(0.5f + d + 1.25f);
                    }
                    pixels[y * TextureSize + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(alpha * 255f));
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            Sprite sprite = Sprite.Create(texture, new Rect(0f, 0f, TextureSize, TextureSize), new Vector2(0.5f, 0.5f), 100f, 0,
                SpriteMeshType.FullRect, new Vector4(SliceBorder, SliceBorder, SliceBorder, SliceBorder));
            sprite.name = name;
            sprite.hideFlags = HideFlags.HideAndDontSave;
            return sprite;
        }

        /// <summary>둥근 사각형까지의 거리. 안쪽이면 음수.</summary>
        private static float Distance(float x, float y, float inset, float radius)
        {
            float min = inset + radius, max = TextureSize - inset - radius;
            float cx = Mathf.Clamp(x, min, max), cy = Mathf.Clamp(y, min, max);
            float dx = x - cx, dy = y - cy;
            return Mathf.Sqrt(dx * dx + dy * dy) - radius;
        }

        /// <summary>둥근 사각형 그림을 입힌 칸.</summary>
        public static Image RoundedPanel(Transform parent, string name, Vector2 anchor, Vector2 position, Vector2 size, Color color,
            float cornerScale = 1f)
        {
            Image image = UiFactory.Panel(parent, name, anchor, position, size, color);
            Round(image, Rounded, cornerScale);
            return image;
        }

        /// <param name="cornerScale">1 이면 모서리 반지름 10. 2 면 5 (작은 꼬리표용).</param>
        public static void Round(Image image, Sprite sprite, float cornerScale = 1f)
        {
            image.sprite = sprite;
            image.type = Image.Type.Sliced;
            image.pixelsPerUnitMultiplier = cornerScale;
        }

        /// <summary>한 줄짜리 글자. 넘쳐도 줄을 바꾸지 않는다.</summary>
        public static Text Line(Transform parent, string name, Vector2 anchor, Vector2 position, Vector2 size, int fontSize,
            TextAnchor alignment, Color color)
        {
            Text text = UiFactory.LabelAt(parent, name, anchor, position, size, fontSize, alignment, color);
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            return text;
        }

        public static Color Tone(int tone)
        {
            switch (tone)
            {
                case DashboardRow.ToneGood: return Good;
                case DashboardRow.ToneBad: return Bad;
                case DashboardRow.ToneDim: return Dim;
                default: return Text;
            }
        }
    }
}
