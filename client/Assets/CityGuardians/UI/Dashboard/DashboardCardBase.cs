using System.Collections.Generic;
using CityGuardians.Domain;
using CityGuardians.Gameplay;
using CityGuardians.Services;
using UnityEngine;
using UnityEngine.UI;

namespace CityGuardians.UI
{
    /// <summary>카드 안의 한 줄: 이름, 꼬리표, 값, 막대. 한 번 만들어 두고 글자만 바꿔 쓴다.</summary>
    public sealed class DashboardRowView
    {
        private const float TagHeight = 18f;
        private const float TagPadding = 8f;

        public readonly GameObject root;
        private readonly Text _label;
        private readonly Text _value;
        private readonly GameObject _tag;
        private readonly RectTransform _tagRect;
        private readonly Text _tagText;
        private readonly GameObject _bar;
        private readonly RectTransform _barFill;
        private readonly Image _barImage;
        private string _shownTag;

        public string LabelText => _label.text;
        public string ValueText => _value.text;
        public string TagText => _tag.activeSelf ? _tagText.text : null;

        public DashboardRowView(Transform parent, int index, float top, float width)
        {
            RectTransform row = UiFactory.Rect(parent, "Row" + (index + 1), new Vector2(0f, 1f),
                new Vector2(DashboardCardBase.Padding, -(top + index * DashboardCardBase.RowHeight)),
                new Vector2(width, DashboardCardBase.RowHeight));
            root = row.gameObject;
            _label = DashboardStyle.Line(row, "Label", new Vector2(0f, 0.5f), Vector2.zero, new Vector2(170f, DashboardCardBase.RowHeight),
                17, TextAnchor.MiddleLeft, DashboardStyle.Dim);
            _value = DashboardStyle.Line(row, "Value", new Vector2(1f, 0.5f), Vector2.zero, new Vector2(200f, DashboardCardBase.RowHeight),
                18, TextAnchor.MiddleRight, DashboardStyle.Text);

            Image tag = DashboardStyle.RoundedPanel(row, "Tag", new Vector2(0f, 0.5f), Vector2.zero, new Vector2(70f, TagHeight),
                DashboardStyle.TagBack, 2f);
            _tag = tag.gameObject;
            _tagRect = tag.rectTransform;
            _tagText = DashboardStyle.Line(tag.transform, "Text", new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(70f, TagHeight),
                12, TextAnchor.MiddleCenter, DashboardStyle.TagText);
            _tag.SetActive(false);

            Image track = UiFactory.Panel(row, "Bar", new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(width, 3f), DashboardStyle.Track);
            _bar = track.gameObject;
            _barImage = UiFactory.Panel(track.transform, "Fill", new Vector2(0f, 0f), Vector2.zero, new Vector2(width, 3f), DashboardStyle.Good);
            _barFill = _barImage.rectTransform;
            _bar.SetActive(false);
        }

        public void Set(DashboardRow row)
        {
            _label.text = row.label ?? "";
            _value.text = row.value ?? "";
            Color tone = row.stage != null ? CityRiskService.StageColor(row.stage) : DashboardStyle.Tone(row.tone);
            _value.color = row.strong ? UiFactory.Accent : tone;
            _value.fontStyle = row.strong ? FontStyle.Bold : FontStyle.Normal;
            _label.color = row.strong ? DashboardStyle.Text : DashboardStyle.Dim;

            bool tagged = !string.IsNullOrEmpty(row.tag);
            if (_tag.activeSelf != tagged) _tag.SetActive(tagged);
            if (tagged)
            {
                if (_shownTag != row.tag)
                {
                    _shownTag = row.tag;
                    _tagText.text = row.tag;
                    float width = _tagText.preferredWidth + TagPadding * 2f;
                    _tagRect.sizeDelta = new Vector2(width, TagHeight);
                    _tagText.rectTransform.sizeDelta = new Vector2(width, TagHeight);
                }
                // 이름 바로 뒤에 붙인다.
                _tagRect.anchoredPosition = new Vector2(_label.preferredWidth + 8f, 0f);
            }

            bool bar = row.bar >= 0;
            if (_bar.activeSelf != bar) _bar.SetActive(bar);
            if (bar)
            {
                float ratio = Mathf.Clamp01(row.bar / 100f);
                _barFill.sizeDelta = new Vector2(((RectTransform)_bar.transform).sizeDelta.x * ratio, 3f);
                _barImage.color = row.stage != null ? CityRiskService.StageColor(row.stage) : DashboardStyle.Good;
            }
        }
    }

    /// <summary>
    /// 대시보드 카드의 공통 틀 (docs/HUD_LAYOUT_SYSTEM.md 2장): 유리판, 테두리, 그림자, 머리 줄, 몸통, 상태 줄.
    ///
    /// 카드는 자기가 어디에 놓일지 정하지 않는다. DashboardLayoutManager 가 정해 준 상자로 움직이고,
    /// LiveDashboardController 가 가진 요약을 글자로 옮겨 적는다. 값이 바뀌지 않으면 다시 적지 않는다.
    /// </summary>
    public abstract class DashboardCardBase : MonoBehaviour
    {
        public const float Width = DashboardLayout.CardWidth;
        public const float HeaderHeight = DashboardLayout.HeaderHeight;
        public const float RowHeight = 26f;
        public const float Padding = 14f;
        public const float FooterHeight = 20f;
        protected const float BodyWidth = Width - Padding * 2f;

        public abstract string CardId { get; }
        /// <summary>펼쳤을 때의 높이 (배율을 곱하기 전).</summary>
        public abstract float ExpandedHeight { get; }

        // 점검과 자동 테스트에서 읽는 값
        public bool Visible => _root.activeSelf;
        public string ShownState { get; private set; } = DashboardCardStates.Hidden;
        public bool Compact { get; private set; }
        public string TitleText => _title.text;
        public string KeyText => _key.text;
        public string FooterText => _footer.text;
        public string MessageText => _message.gameObject.activeSelf ? _message.text : null;
        public IReadOnlyList<DashboardRowView> Rows => _rows;
        public RectTransform Rect { get; private set; }
        public bool Animating => _animation.Moving;

        protected RectTransform Body { get; private set; }

        private GameObject _root;
        private CanvasGroup _group;
        private RectTransform _scaled;
        private Image _glass;
        private Image _accent;
        private RectTransform _header;
        private GameObject _divider;
        private Text _title;
        private GameObject _keyChip;
        private Text _key;
        private CanvasGroup _bodyGroup;
        private Text _footer;
        private Text _hint;
        private Text _message;
        private readonly List<DashboardRowView> _rows = new List<DashboardRowView>();
        private readonly DashboardAnimationController _animation = new DashboardAnimationController();

        private float _scale = 1f;
        private bool _hover;
        private bool _pulse;
        private Color _accentColor = UiFactory.Accent;
        private int _dataVersion = -1;
        private int _stateVersion = -1;
        private int _extraVersion = -1;
        private string _renderedState;
        private bool _renderedCompact;
        private float _footerTimer;

        public static T Create<T>(Transform parent) where T : DashboardCardBase
        {
            RectTransform rect = UiFactory.Rect(parent, typeof(T).Name, new Vector2(0f, 1f), Vector2.zero, new Vector2(Width, HeaderHeight));
            T card = rect.gameObject.AddComponent<T>();
            card.Build(rect);
            return card;
        }

        private void Build(RectTransform rect)
        {
            Rect = rect;
            _root = rect.gameObject;
            _group = _root.AddComponent<CanvasGroup>();
            _group.interactable = false;
            _group.blocksRaycasts = false;

            // 그림자: 카드보다 조금 크고 조금 아래에 깔린다.
            RectTransform shadow = UiFactory.Stretch(rect, "Shadow");
            shadow.offsetMin = new Vector2(-9f, -12f);
            shadow.offsetMax = new Vector2(9f, 6f);
            DashboardStyle.Round(UiFactory.Fill(shadow, DashboardStyle.Shadow), DashboardStyle.Soft);

            // 안의 것들은 340 폭을 기준으로 놓고, 배율만큼 통째로 키운다.
            _scaled = UiFactory.Rect(rect, "Scaled", new Vector2(0f, 1f), Vector2.zero, new Vector2(Width, HeaderHeight));
            _glass = UiFactory.Fill(_scaled, DashboardStyle.Glass);
            DashboardStyle.Round(_glass, DashboardStyle.Rounded);
            _scaled.gameObject.AddComponent<RectMask2D>();

            // 상태를 색으로 알리는 왼쪽 띠. 색만으로 알리지 않는다: 머리 줄의 글자에도 모양(●▲◆■)이 들어간다.
            _accent = UiFactory.Panel(_scaled, "Accent", new Vector2(0f, 1f), new Vector2(0f, -10f), new Vector2(4f, HeaderHeight - 20f),
                _accentColor);

            RectTransform header = UiFactory.Rect(_scaled, "Header", new Vector2(0f, 1f), Vector2.zero, new Vector2(Width, HeaderHeight));
            _header = header;
            _title = DashboardStyle.Line(header, "Title", new Vector2(0f, 0.5f), new Vector2(Padding, 0f), new Vector2(110f, HeaderHeight),
                19, TextAnchor.MiddleLeft, DashboardStyle.Text);
            _title.fontStyle = FontStyle.Bold;
            _title.text = DashboardCards.Title(CardId);
            Image chip = DashboardStyle.RoundedPanel(header, "KeyChip", new Vector2(0f, 0.5f), new Vector2(0f, 0f), new Vector2(30f, 18f),
                DashboardStyle.ChipBack, 2f);
            _keyChip = chip.gameObject;
            DashboardStyle.Line(chip.transform, "Text", new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(30f, 18f),
                12, TextAnchor.MiddleCenter, DashboardStyle.Dim).text = DashboardCards.KeyName(CardId);
            _key = DashboardStyle.Line(header, "Key", new Vector2(1f, 0.5f), new Vector2(-Padding, 0f), new Vector2(200f, HeaderHeight),
                17, TextAnchor.MiddleRight, DashboardStyle.Text);

            _divider = UiFactory.Panel(_scaled, "Divider", new Vector2(0f, 1f), new Vector2(Padding, -HeaderHeight),
                new Vector2(BodyWidth, 1f), DashboardStyle.Divider).gameObject;

            Body = UiFactory.Rect(_scaled, "Body", new Vector2(0f, 1f), new Vector2(0f, -HeaderHeight),
                new Vector2(Width, ExpandedHeight - HeaderHeight));
            _bodyGroup = Body.gameObject.AddComponent<CanvasGroup>();
            _bodyGroup.interactable = false;
            _bodyGroup.blocksRaycasts = false;

            _message = UiFactory.LabelAt(Body, "Message", new Vector2(0.5f, 0.5f), new Vector2(0f, 8f), new Vector2(BodyWidth, 90f),
                17, TextAnchor.MiddleCenter, DashboardStyle.Dim);
            _message.gameObject.SetActive(false);

            // 맨 아래: 연결 상태와 마지막 갱신 시각, 상세 보기 키
            _footer = DashboardStyle.Line(Body, "Status", new Vector2(0f, 0f), new Vector2(Padding, 6f), new Vector2(220f, FooterHeight),
                13, TextAnchor.MiddleLeft, DashboardStyle.Dim);
            _hint = DashboardStyle.Line(Body, "Hint", new Vector2(1f, 0f), new Vector2(-Padding, 6f), new Vector2(110f, FooterHeight),
                13, TextAnchor.MiddleRight, DashboardStyle.Dim);
            _hint.text = "Shift+" + DashboardCards.KeyName(CardId) + "  상세";

            BuildBody(Body);

            // 테두리는 맨 위에 그려서 안의 것들에 가려지지 않게 한다.
            RectTransform border = UiFactory.Stretch(_scaled, "Border");
            DashboardStyle.Round(UiFactory.Fill(border, DashboardStyle.Border), DashboardStyle.Ring);

            _root.SetActive(false);
        }

        /// <summary>몸통에 들어갈 것을 만든다. 줄은 AddRows 로 만든다.</summary>
        protected abstract void BuildBody(RectTransform body);

        /// <summary>요약을 카드에 적는다. 요약을 아직 받지 못했으면 summary 가 null 이다.</summary>
        protected abstract void Render(DashboardSummaryDto summary, LiveDashboardController dashboard, bool expanded);

        /// <summary>요약 말고도 카드를 다시 그려야 하는 까닭이 있으면 그 판 번호 (전국 카드: 도시 위험도).</summary>
        protected virtual int ExtraVersion => 0;

        protected void AddRows(RectTransform body, int count, float top)
        {
            for (int i = 0; i < count; i++) _rows.Add(new DashboardRowView(body, i, top, BodyWidth));
        }

        protected void ShowRows(List<DashboardRow> rows)
        {
            _message.gameObject.SetActive(false);
            for (int i = 0; i < _rows.Count; i++)
            {
                bool used = rows != null && i < rows.Count;
                if (_rows[i].root.activeSelf != used) _rows[i].root.SetActive(used);
                if (used) _rows[i].Set(rows[i]);
            }
        }

        /// <summary>줄 대신 한마디를 보인다 (불러오는 중, 전입신고 전).</summary>
        protected void ShowMessage(string text)
        {
            for (int i = 0; i < _rows.Count; i++)
                if (_rows[i].root.activeSelf) _rows[i].root.SetActive(false);
            _message.gameObject.SetActive(true);
            _message.text = text ?? "";
        }

        /// <summary>머리 줄 오른쪽의 글자. 접힌 카드에서는 핵심 수치 하나다.</summary>
        protected void SetKey(string text, Color color)
        {
            _key.text = text ?? "";
            _key.color = color;
        }

        /// <param name="pulse">급한 일이 있다: 띠가 깜박이고 머리 줄이 붉어진다.</param>
        protected void SetAccent(Color color, bool pulse)
        {
            _accentColor = color;
            _pulse = pulse;
            _accent.color = color;
            ApplyGlass();
        }

        private void ApplyGlass()
        {
            Color glass = _hover ? DashboardStyle.GlassHover : DashboardStyle.Glass;
            if (_pulse) glass = Color.Lerp(glass, new Color(0.42f, 0.08f, 0.08f, glass.a), 0.55f);
            _glass.color = glass;
        }

        // ── 자리와 움직임 ───────────────────────────────────────

        /// <summary>자리를 정해 준다. 실제로 옮기는 것은 Refresh 가 조금씩 한다.</summary>
        public void Place(DashboardBox box, float scale, bool instant = false)
        {
            _scale = Mathf.Max(0.01f, scale);
            bool changed = ShownState != box.state || Compact != box.compact;
            ShownState = box.state;
            Compact = box.compact;
            _animation.SetTarget(box, instant);
            if (box.Shown && !_root.activeSelf) _root.SetActive(true);
            if (changed) _stateVersion = -1;
        }

        public void SetHover(bool hover)
        {
            if (_hover == hover) return;
            _hover = hover;
            ApplyGlass();
        }

        /// <summary>Hud 가 매 프레임 부른다.</summary>
        public void Refresh(float dt, LiveDashboardController dashboard)
        {
            if (!_root.activeSelf) return;

            if (_animation.Step(dt)) ApplyAnimation();
            if (_animation.Gone)
            {
                _root.SetActive(false);
                return;
            }

            bool expanded = ShownState == DashboardCardStates.Expanded;
            int dataVersion = dashboard.Data != null ? dashboard.Data.Version : 0;
            int extra = ExtraVersion;
            if (dataVersion != _dataVersion || dashboard.State.Version != _stateVersion || extra != _extraVersion
                || _renderedState != ShownState || _renderedCompact != Compact)
            {
                _dataVersion = dataVersion;
                _stateVersion = dashboard.State.Version;
                _extraVersion = extra;
                _renderedState = ShownState;
                _renderedCompact = Compact;
                ApplyHeaderShape();
                Render(dashboard.Summary, dashboard, expanded);
                _footerTimer = 0f;
            }

            // 상태 줄은 1초에 한 번만 다시 적는다 ("2분 전"이 흘러가도록).
            if (expanded)
            {
                _footerTimer -= dt;
                if (_footerTimer <= 0f)
                {
                    _footerTimer = 1f;
                    string status = dashboard.StatusLine;
                    _footer.text = status;
                    _footer.color = dashboard.Data != null && dashboard.Data.Connection == DashboardConnection.Offline
                        ? DashboardStyle.Offline : DashboardStyle.Dim;
                }
            }

            if (_pulse)
            {
                float wave = 0.55f + 0.45f * Mathf.Sin(Time.unscaledTime * 5f);
                _accent.color = new Color(_accentColor.r, _accentColor.g, _accentColor.b, wave);
            }
        }

        /// <summary>한 줄에 세 장을 놓은 작은 모양에서는 이름과 키 표시를 빼고 핵심 수치만 가운데에 둔다.</summary>
        private void ApplyHeaderShape()
        {
            if (_title.gameObject.activeSelf == Compact) _title.gameObject.SetActive(!Compact);
            if (_keyChip.activeSelf == Compact) _keyChip.SetActive(!Compact);
            RectTransform key = _key.rectTransform;
            if (Compact)
            {
                key.anchorMin = key.anchorMax = key.pivot = new Vector2(0.5f, 0.5f);
                key.anchoredPosition = new Vector2(2f, 0f);
                _key.alignment = TextAnchor.MiddleCenter;
                _key.fontSize = 16;
            }
            else
            {
                key.anchorMin = key.anchorMax = key.pivot = new Vector2(1f, 0.5f);
                key.anchoredPosition = new Vector2(-Padding, 0f);
                _key.alignment = TextAnchor.MiddleRight;
                _key.fontSize = 17;
                ((RectTransform)_keyChip.transform).anchoredPosition = new Vector2(Padding + _title.preferredWidth + 8f, 0f);
            }
        }

        private void ApplyAnimation()
        {
            Rect.anchoredPosition = new Vector2(_animation.X, -_animation.Y);
            Rect.sizeDelta = new Vector2(_animation.Width, _animation.Height);
            _scaled.localScale = new Vector3(_scale, _scale, 1f);
            float width = _animation.Width / _scale, height = _animation.Height / _scale;
            _scaled.sizeDelta = new Vector2(width, height);
            _header.sizeDelta = new Vector2(width, HeaderHeight);
            _group.alpha = _animation.Alpha;

            // 몸통은 펼쳐지는 만큼 드러난다.
            float open = Mathf.Clamp01((height - HeaderHeight) / Mathf.Max(1f, ExpandedHeight - HeaderHeight));
            _bodyGroup.alpha = open * open;
            bool showBody = open > 0.01f;
            if (Body.gameObject.activeSelf != showBody)
            {
                Body.gameObject.SetActive(showBody);
                _divider.SetActive(showBody);
            }
        }

        // ── 마우스 ──────────────────────────────────────────────

        public bool Contains(Vector2 screenPoint) =>
            _root.activeSelf && RectTransformUtility.RectangleContainsScreenPoint(Rect, screenPoint, null);

        /// <summary>머리 줄 위인가. 머리 줄을 누르면 접고 펴고, 몸통을 누르면 상세 보기가 열린다.</summary>
        public bool HeaderContains(Vector2 screenPoint)
        {
            if (!Contains(screenPoint)) return false;
            return RectTransformUtility.RectangleContainsScreenPoint(_header, screenPoint, null);
        }
    }
}
