using System.Collections.Generic;
using CityGuardians.Domain;
using CityGuardians.Gameplay;
using CityGuardians.Inputs;
using CityGuardians.Services;
using UnityEngine;
using UnityEngine.UI;

namespace CityGuardians.UI
{
    /// <summary>
    /// 협동 방어전의 전술 미니맵 (docs/TACTICAL_MINIMAP.md).
    /// 평소에는 화면 오른쪽 위에 내 둘레가 보이고, M 키로 전체 전술 지도를 펼친다.
    /// 무엇을 어디에 그릴지는 규칙(Domain/Coop/Minimap.cs)이 정한다. 여기서는 그 결과를 점과 선으로 옮기기만 한다.
    /// 펼친 지도를 누르면 그곳에 전술 핑을 보낸다.
    /// </summary>
    public sealed class MinimapPanel : MonoBehaviour
    {
        private const float CornerSize = 280f;
        private const float TacticalSize = 820f;
        private const float RedrawEvery = 0.066f;

        private static readonly Color MapBack = new Color(0.07f, 0.10f, 0.13f, 0.86f);
        private static readonly Color RouteColor = new Color(1f, 0.80f, 0.20f, 0.55f);
        private static readonly Color SelfColor = Color.white;
        private static readonly Color MateColor = new Color(0.45f, 0.95f, 0.55f);
        private static readonly Color MateGone = new Color(0.55f, 0.60f, 0.58f);
        private static readonly Color EnemyColor = new Color(1f, 0.32f, 0.28f);
        private static readonly Color TowerColor = new Color(0.40f, 0.86f, 0.95f);
        private static readonly Color TowerHurt = new Color(1f, 0.66f, 0.25f);
        private static readonly Color GateColor = new Color(0.36f, 0.78f, 1f);
        private static readonly Color GateLow = new Color(0.95f, 0.36f, 0.30f);
        private static readonly Color WallColor = new Color(0.62f, 0.70f, 0.66f);
        private static readonly Color FacilityColor = new Color(0.96f, 0.90f, 0.72f);
        private static readonly Color SpawnColor = new Color(0.78f, 0.42f, 1f);
        private static readonly Color DangerColor = new Color(1f, 0.20f, 0.16f, 0.26f);

        public bool CornerVisible => _corner.activeSelf;
        public bool TacticalVisible => _tactical.activeSelf;
        /// <summary>지금 그려진 표시의 수 (점검용).</summary>
        public int BlipCount { get; private set; }

        private sealed class Layer
        {
            public RectTransform area;
            public float size;
            public bool labels;
            public readonly List<Image> marks = new List<Image>();
            public readonly List<Text> texts = new List<Text>();
            public int markCount;
            public int textCount;
        }

        private GameObject _corner;
        private GameObject _tactical;
        private Text _tacticalTitle;
        private readonly Layer _small = new Layer { size = CornerSize };
        private readonly Layer _full = new Layer { size = TacticalSize, labels = true };
        private float _sinceDraw = 1f;
        private bool _drawnTactical;

        /// <summary>오른쪽 위, GC 표시 바로 아래의 미니맵.</summary>
        public void BuildCorner(Transform root)
        {
            Image frame = UiFactory.Panel(root, "Minimap", new Vector2(1f, 1f), new Vector2(-32f, -100f),
                new Vector2(CornerSize + 12f, CornerSize + 46f), UiFactory.PanelColor);
            _corner = frame.gameObject;
            _small.area = MapArea(frame.transform, new Vector2(6f, -6f), CornerSize);
            UiFactory.LabelAt(frame.transform, "Hint", new Vector2(0.5f, 0f), new Vector2(0f, 4f), new Vector2(CornerSize, 30f),
                20, TextAnchor.MiddleCenter, UiFactory.Accent).text = "M  전술 지도      Q  전술 핑";
            _corner.SetActive(false);
        }

        /// <summary>펼친 전술 지도. 걸어 다니는 화면의 요소들을 덮고, 선택 메뉴보다는 아래에 놓는다.</summary>
        public void BuildTactical(Transform root)
        {
            Image frame = UiFactory.Panel(root, "TacticalMap", new Vector2(0.5f, 0.5f), new Vector2(0f, 0f),
                new Vector2(TacticalSize + 380f, TacticalSize + 110f), new Color(0.05f, 0.07f, 0.11f, 0.96f));
            _tactical = frame.gameObject;
            UiFactory.Panel(frame.transform, "TopLine", new Vector2(0.5f, 1f), Vector2.zero, new Vector2(TacticalSize + 380f, 8f), UiFactory.Accent);
            _tacticalTitle = UiFactory.LabelAt(frame.transform, "Title", new Vector2(0f, 1f), new Vector2(24f, -14f), new Vector2(TacticalSize, 44f),
                32, TextAnchor.MiddleLeft, Color.white);
            _full.area = MapArea(frame.transform, new Vector2(24f, -62f), TacticalSize);
            UiFactory.LabelAt(frame.transform, "Keys", new Vector2(0f, 0f), new Vector2(24f, 6f), new Vector2(TacticalSize, 36f),
                24, TextAnchor.MiddleLeft, UiFactory.Accent).text = "지도를 누르면 그곳에 전술 핑을 보내요.          M 또는 E  닫기";

            // 범례
            RectTransform legend = UiFactory.Rect(frame.transform, "Legend", new Vector2(1f, 1f), new Vector2(-24f, -62f), new Vector2(310f, TacticalSize));
            UiFactory.LabelAt(legend, "Head", new Vector2(0f, 1f), new Vector2(0f, 0f), new Vector2(310f, 36f),
                26, TextAnchor.MiddleLeft, Color.white).text = "지도 읽는 법";
            float y = -48f;
            LegendRow(legend, ref y, SelfColor, true, "나");
            LegendRow(legend, ref y, MateColor, true, "대원");
            LegendRow(legend, ref y, EnemyColor, true, "적 (뾰족한 쪽으로 가요)");
            LegendRow(legend, ref y, TowerColor, false, "타워 · 둘레는 사거리");
            LegendRow(legend, ref y, GateColor, false, "관문");
            LegendRow(legend, ref y, WallColor, false, "방어벽");
            LegendRow(legend, ref y, FacilityColor, false, "주요 시설");
            LegendRow(legend, ref y, SpawnColor, false, "침입 지점");
            LegendRow(legend, ref y, new Color(1f, 0.20f, 0.16f, 0.8f), true, "위험 지역 (적이 몰린 곳)");
            LegendRow(legend, ref y, RouteColor, false, "적 이동 경로");
            y -= 14f;
            UiFactory.LabelAt(legend, "PingHead", new Vector2(0f, 1f), new Vector2(0f, y), new Vector2(310f, 36f),
                26, TextAnchor.MiddleLeft, Color.white).text = "전술 핑";
            y -= 44f;
            for (int i = 0; i < PingKinds.All.Length; i++)
            {
                string kind = PingKinds.All[i];
                string suffix = PingKinds.IsSupportRequest(kind) ? "  (긴급 지원)" : "";
                LegendRow(legend, ref y, CoopBattle.PingColor(kind), false, PingKinds.DisplayName(kind) + suffix, 45f);
            }
            UiFactory.LabelAt(legend, "Note", new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(310f, 96f),
                20, TextAnchor.LowerLeft, UiFactory.TextColor).text = "경로와 사거리 표시는\n팀 메뉴(F)에서 켜고 꺼요.";
            _tactical.SetActive(false);
        }

        private static void LegendRow(Transform legend, ref float y, Color color, bool round, string text, float rotate = 0f)
        {
            RectTransform chip = UiFactory.Rect(legend, "Chip", new Vector2(0f, 1f), new Vector2(11f, y - 15f), new Vector2(18f, 18f));
            chip.pivot = new Vector2(0.5f, 0.5f);
            chip.localRotation = Quaternion.Euler(0f, 0f, rotate);
            Image image = UiFactory.Fill(chip, color);
            if (round) image.sprite = DiscSprite();
            Text label = UiFactory.LabelAt(legend, "Text", new Vector2(0f, 1f), new Vector2(34f, y), new Vector2(276f, 30f),
                22, TextAnchor.MiddleLeft, UiFactory.TextColor);
            label.text = text;
            label.horizontalOverflow = HorizontalWrapMode.Overflow;
            y -= 34f;
        }

        /// <summary>지도가 그려지는 네모. 왼쪽 위가 원점이다 (u 는 오른쪽으로, v 는 아래로).</summary>
        private static RectTransform MapArea(Transform parent, Vector2 position, float size)
        {
            RectTransform area = UiFactory.Rect(parent, "Area", new Vector2(0f, 1f), position, new Vector2(size, size));
            UiFactory.Fill(area, MapBack);
            // 사거리 원과 위험 지역이 지도 밖으로 비어져 나오지 않게 자른다.
            area.gameObject.AddComponent<RectMask2D>();
            return area;
        }

        // ── 매 프레임 ───────────────────────────────────────────

        /// <summary>Hud 가 매 프레임 부른다.</summary>
        public void Refresh(float dt)
        {
            CoopBattle battle = CoopBattle.Current;
            bool show = battle != null && battle.Snapshot != null && battle.Map != null
                        && battle.Link.State != CoopLinkStates.Closed && battle.Link.State != CoopLinkStates.Finished;
            bool tactical = show && battle.TacticalOpen;
            if (_corner.activeSelf != (show && !tactical)) _corner.SetActive(show && !tactical);
            if (_tactical.activeSelf != tactical) _tactical.SetActive(tactical);
            if (!show)
            {
                BlipCount = 0;
                return;
            }

            _sinceDraw += dt;
            if (_sinceDraw >= RedrawEvery || _drawnTactical != tactical)
            {
                _sinceDraw = 0f;
                _drawnTactical = tactical;
                MinimapView view = battle.BuildMinimap();
                if (view != null)
                {
                    Draw(tactical ? _full : _small, view);
                    if (tactical)
                        _tacticalTitle.text = $"전술 지도   {battle.Link.ZoneName}   (한 변 {view.widthM:0} m)";
                }
            }
            if (tactical) HandleClick(battle);
        }

        private void HandleClick(CoopBattle battle)
        {
            // 지도가 맨 위에 있을 때만 받는다 (핑을 고르는 메뉴나 멈춤 화면이 떠 있으면 받지 않는다).
            if (!GameState.IsOnlyLockedBy(GameState.LockTacticalMap)) return;
            IPlayerInput input = PlayerInputHub.Current;
            if (!input.PointerPressed) return;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(_full.area, input.PointerPosition, null, out Vector2 local)) return;
            float u = local.x / _full.size;
            float v = -local.y / _full.size;
            if (u < 0f || u > 1f || v < 0f || v > 1f) return;
            battle.PingOnMap(u, v);
        }

        // ── 그리기 ──────────────────────────────────────────────

        private void Draw(Layer layer, MinimapView view)
        {
            layer.markCount = 0;
            layer.textCount = 0;
            float size = layer.size;

            // 적 이동 경로 (펼친 지도에서, 켜 두었을 때만 온다)
            for (int i = 0; i + 1 < view.route.Count; i++)
                Line(layer, Point(view.route[i].u, view.route[i].v, size), Point(view.route[i + 1].u, view.route[i + 1].v, size), RouteColor, 8f);

            float pulse = 1f + Mathf.Sin(Time.unscaledTime * 6f) * 0.18f;
            for (int i = 0; i < view.blips.Count; i++)
            {
                MinimapBlip blip = view.blips[i];
                Vector2 at = Point(blip.u, blip.v, size);
                switch (blip.kind)
                {
                    case MinimapBlipKinds.Danger:
                        Mark(layer, at, Mathf.Max(24f, blip.radius * size * 2f), DangerColor, DiscSprite());
                        break;
                    case MinimapBlipKinds.Tower:
                        if (blip.radius > 0f)
                            Mark(layer, at, blip.radius * size * 2f, new Color(TowerColor.r, TowerColor.g, TowerColor.b, 0.5f), RingSprite());
                        Mark(layer, at, layer.labels ? 16f : 11f, blip.hpRatio >= 0f && blip.hpRatio < 0.5f ? TowerHurt : TowerColor, null);
                        break;
                    case MinimapBlipKinds.Gate:
                        Mark(layer, at, layer.labels ? 30f : 20f, blip.hpRatio >= 0f && blip.hpRatio <= 0.35f ? GateLow : GateColor, null);
                        Label(layer, at, layer.labels ? blip.label : "관문", GateColor, layer.labels ? 22 : 18, -24f);
                        break;
                    case MinimapBlipKinds.Wall:
                        Bar(layer, at, layer.labels ? 60f : 30f, layer.labels ? 10f : 6f, WallColor);
                        break;
                    case MinimapBlipKinds.Facility:
                        Mark(layer, at, layer.labels ? 20f : 12f, FacilityColor, null);
                        if (layer.labels) Label(layer, at, blip.label, FacilityColor, 18, -22f);
                        break;
                    case MinimapBlipKinds.Spawn:
                        Mark(layer, at, layer.labels ? 26f : 16f, SpawnColor, null);
                        if (layer.labels) Label(layer, at, blip.label, SpawnColor, 20, 24f);
                        break;
                    case MinimapBlipKinds.Enemy:
                    case MinimapBlipKinds.Boss:
                    {
                        bool boss = blip.kind == MinimapBlipKinds.Boss;
                        float dot = (boss ? 20f : 10f) * (layer.labels ? 1.3f : 1f);
                        // 가는 방향: 점에서 그쪽으로 뻗은 짧은 막대
                        if (blip.heading >= 0f) Nose(layer, at, dot, blip.heading, EnemyColor);
                        Mark(layer, at, dot, EnemyColor, DiscSprite());
                        if (boss) Label(layer, at, "보스", EnemyColor, layer.labels ? 20 : 16, 20f);
                        break;
                    }
                    case MinimapBlipKinds.Teammate:
                    {
                        bool gone = blip.label != null && blip.label.EndsWith("(연결 끊김)");
                        Mark(layer, at, layer.labels ? 18f : 13f, gone ? MateGone : MateColor, DiscSprite());
                        if (layer.labels) Label(layer, at, blip.label, gone ? MateGone : MateColor, 18, 20f);
                        break;
                    }
                    case MinimapBlipKinds.Ping:
                    case MinimapBlipKinds.SupportRequest:
                    {
                        // 긴급 지원 요청은 더 크고, 깜빡인다. 범위 밖이면 가장자리에 붙어서 온다.
                        bool support = blip.kind == MinimapBlipKinds.SupportRequest;
                        Color color = CoopBattle.PingColor(blip.code);
                        float diamond = (support ? 22f : 16f) * (layer.labels ? 1.3f : 1f) * (support ? pulse : 1f);
                        Mark(layer, at, diamond, color, null, 45f);
                        Label(layer, at, blip.label, color, layer.labels ? 20 : 16, 22f);
                        break;
                    }
                    case MinimapBlipKinds.Self:
                        Mark(layer, at, layer.labels ? 24f : 18f, new Color(0f, 0f, 0f, 0.7f), DiscSprite());
                        Mark(layer, at, layer.labels ? 18f : 13f, SelfColor, DiscSprite());
                        if (layer.labels) Label(layer, at, "나", SelfColor, 20, 22f);
                        break;
                }
            }

            for (int i = layer.markCount; i < layer.marks.Count; i++)
                if (layer.marks[i].gameObject.activeSelf) layer.marks[i].gameObject.SetActive(false);
            for (int i = layer.textCount; i < layer.texts.Count; i++)
                if (layer.texts[i].gameObject.activeSelf) layer.texts[i].gameObject.SetActive(false);
            BlipCount = view.blips.Count;
        }

        private static Vector2 Point(float u, float v, float size) => new Vector2(u * size, -v * size);

        private static Image NextMark(Layer layer)
        {
            if (layer.markCount == layer.marks.Count)
            {
                RectTransform rect = UiFactory.Rect(layer.area, "Mark", new Vector2(0f, 1f), Vector2.zero, new Vector2(8f, 8f));
                rect.pivot = new Vector2(0.5f, 0.5f);
                layer.marks.Add(UiFactory.Fill(rect, Color.white));
            }
            Image image = layer.marks[layer.markCount++];
            if (!image.gameObject.activeSelf) image.gameObject.SetActive(true);
            // 그리는 순서대로 겹친다.
            image.transform.SetSiblingIndex(layer.markCount - 1);
            return image;
        }

        private static void Mark(Layer layer, Vector2 at, float size, Color color, Sprite sprite, float rotate = 0f)
        {
            Image image = NextMark(layer);
            RectTransform rect = image.rectTransform;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = at;
            rect.sizeDelta = new Vector2(size, size);
            rect.localRotation = Quaternion.Euler(0f, 0f, rotate);
            image.sprite = sprite;
            image.color = color;
        }

        private static void Bar(Layer layer, Vector2 at, float width, float height, Color color)
        {
            Image image = NextMark(layer);
            RectTransform rect = image.rectTransform;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = at;
            rect.sizeDelta = new Vector2(width, height);
            rect.localRotation = Quaternion.identity;
            image.sprite = null;
            image.color = color;
        }

        /// <summary>두 점을 잇는 선.</summary>
        private static void Line(Layer layer, Vector2 from, Vector2 to, Color color, float thickness)
        {
            Vector2 delta = to - from;
            float length = delta.magnitude;
            if (length < 0.5f) return;
            Image image = NextMark(layer);
            RectTransform rect = image.rectTransform;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = (from + to) * 0.5f;
            rect.sizeDelta = new Vector2(length + thickness, thickness);
            rect.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg);
            image.sprite = null;
            image.color = color;
        }

        /// <summary>적이 가는 방향. heading 은 북쪽(지도 위쪽)에서 시계 방향으로 잰 각도다.</summary>
        private static void Nose(Layer layer, Vector2 at, float dot, float heading, Color color)
        {
            float radians = heading * Mathf.Deg2Rad;
            var direction = new Vector2(Mathf.Sin(radians), Mathf.Cos(radians));
            Image image = NextMark(layer);
            RectTransform rect = image.rectTransform;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = at + direction * dot * 0.75f;
            rect.sizeDelta = new Vector2(dot * 0.36f, dot * 0.9f);
            rect.localRotation = Quaternion.Euler(0f, 0f, -heading);
            image.sprite = null;
            image.color = color;
        }

        private static void Label(Layer layer, Vector2 at, string text, Color color, int fontSize, float offsetY)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (layer.textCount == layer.texts.Count)
            {
                Text created = UiFactory.LabelAt(layer.area, "Label", new Vector2(0f, 1f), Vector2.zero, new Vector2(240f, 28f),
                    fontSize, TextAnchor.MiddleCenter, color, true);
                created.rectTransform.pivot = new Vector2(0.5f, 0.5f);
                created.horizontalOverflow = HorizontalWrapMode.Overflow;
                layer.texts.Add(created);
            }
            Text label = layer.texts[layer.textCount++];
            if (!label.gameObject.activeSelf) label.gameObject.SetActive(true);
            // 글자는 지도 밖으로 나가지 않게 가장자리에서 안쪽으로 당긴다.
            float x = Mathf.Clamp(at.x, 60f, layer.size - 60f);
            float y = Mathf.Clamp(at.y + offsetY, -layer.size + 14f, -14f);
            label.rectTransform.anchoredPosition = new Vector2(x, y);
            label.fontSize = fontSize;
            label.color = color;
            label.text = text;
            // 글자는 늘 표시들 위에 온다.
            label.transform.SetAsLastSibling();
        }

        // ── 둥근 표시에 쓰는 그림 (코드로 만든다) ───────────────

        private static Sprite _sharedDisc;
        private static Sprite _sharedRing;

        private static Sprite DiscSprite() => _sharedDisc != null ? _sharedDisc : (_sharedDisc = MakeSprite(false));

        private static Sprite RingSprite() => _sharedRing != null ? _sharedRing : (_sharedRing = MakeSprite(true));

        private static Sprite MakeSprite(bool ring)
        {
            const int size = 128;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = ring ? "cg_minimap_ring" : "cg_minimap_disc",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            var pixels = new Color32[size * size];
            float center = (size - 1) * 0.5f;
            float outer = size * 0.5f - 1f;
            float inner = outer - 5f;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float distance = Mathf.Sqrt((x - center) * (x - center) + (y - center) * (y - center));
                // 가장자리는 한 픽셀에 걸쳐 흐려진다.
                float alpha = Mathf.Clamp01(outer - distance + 0.5f);
                if (ring) alpha *= Mathf.Clamp01(distance - inner + 0.5f);
                pixels[y * size + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(alpha * 255f));
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
        }
    }
}
