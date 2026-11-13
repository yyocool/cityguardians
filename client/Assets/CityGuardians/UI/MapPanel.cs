using System;
using CityGuardians.Domain;
using CityGuardians.Gameplay;
using CityGuardians.Inputs;
using CityGuardians.Services;
using UnityEngine;
using UnityEngine.UI;

namespace CityGuardians.UI
{
    /// <summary>
    /// 지도 도형을 그리는 그래픽. 꼭짓점은 지도 좌표(칸) 그대로 넣고,
    /// 부모 RectTransform의 위치와 배율로 화면에 맞춘다. 무엇을 그릴지는 populate가 정한다.
    /// </summary>
    // Image·Text와 달리 Graphic은 CanvasRenderer를 스스로 붙이지 않는다. 없으면 아무것도 그려지지 않는다.
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class MapMeshGraphic : MaskableGraphic
    {
        /// <summary>uGUI 메시 하나가 담을 수 있는 꼭짓점 수의 한계보다 조금 작게 잡은 값.</summary>
        public const int VertexBudget = 64000;

        public Action<VertexHelper> populate;

        public static MapMeshGraphic Create(Transform parent, string name, Vector2 size, Action<VertexHelper> populate)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.zero;
            rect.pivot = Vector2.zero;
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = size;
            var graphic = go.AddComponent<MapMeshGraphic>();
            graphic.raycastTarget = false;
            graphic.populate = populate;
            return graphic;
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            populate?.Invoke(vh);
        }

        /// <summary>두 점을 잇는 굵기 있는 선분 하나.</summary>
        public static void AddSegment(VertexHelper vh, Vector2 a, Vector2 b, float halfWidth, Color32 color)
        {
            if (vh.currentVertCount > VertexBudget - 4) return;
            Vector2 direction = b - a;
            float length = direction.magnitude;
            if (length < 0.0001f) return;
            direction /= length;
            Vector2 normal = new Vector2(-direction.y, direction.x) * halfWidth;
            // 꺾이는 곳에 틈이 보이지 않도록 양 끝을 조금 늘인다.
            Vector2 cap = direction * (halfWidth * 0.6f);
            int start = vh.currentVertCount;
            vh.AddVert(a - cap + normal, color, Vector4.zero);
            vh.AddVert(b + cap + normal, color, Vector4.zero);
            vh.AddVert(b + cap - normal, color, Vector4.zero);
            vh.AddVert(a - cap - normal, color, Vector4.zero);
            vh.AddTriangle(start, start + 1, start + 2);
            vh.AddTriangle(start, start + 2, start + 3);
        }
    }

    /// <summary>
    /// 대한민국 지도 화면 (docs/MAP_SYSTEM.md 3장). MapController(게임플레이)의 상태를 읽어 그리고,
    /// 마우스로 가리키고 누른 것을 MapController에 알린다. 확대와 이동은 보는 방법일 뿐이라 여기서 맡는다.
    /// </summary>
    public sealed class MapPanel : MonoBehaviour
    {
        private const float PanelWidth = 460f;
        private const float TopMargin = 176f;
        private const float BottomMargin = 104f;
        private const float SideMargin = 40f;
        private const float MaxScale = 8f;
        private const float DragThreshold = 8f;
        private const float ClickSlopPixels = 10f;

        private static readonly Color Sea = new Color(0.07f, 0.11f, 0.19f, 1f);
        private static readonly Color RegionLine = new Color(0.05f, 0.08f, 0.14f, 0.85f);
        private static readonly Color SidoLine = new Color(1f, 1f, 1f, 0.92f);
        private static readonly Color Dim = new Color(0.66f, 0.70f, 0.76f);
        private static readonly Color ButtonOff = new Color(0.20f, 0.23f, 0.30f, 1f);
        private static readonly Color ButtonText = new Color(0.10f, 0.10f, 0.14f);

        // 점검과 자동 테스트에서 읽는 값
        public bool Visible => _root.activeSelf;
        public string HeaderTitle => _title.text;
        public string GuideText => _guide.gameObject.activeSelf ? _guide.text : null;
        public string Breadcrumb => _crumb.text;
        public string InfoTitle => _infoTitle.text;
        public string InfoResidents => _residentsValue.text;
        public string InfoDefensePoints => _pointsValue.text;
        public string InfoNote => _infoNote.text;
        public string AttributionText => _attribution.text;
        /// <summary>지금 가리키고 있는 곳의 이름. 없으면 null.</summary>
        public string HoverText => _hoverRegion != null ? MapData.Instance.FindRegion(_hoverRegion)?.name
            : _hoverSido != null ? MapData.Instance.FindSido(_hoverSido)?.name
            : null;
        public bool TooltipVisible => _tooltip.activeSelf;
        public bool ResidencyButtonVisible => _residencyButton.gameObject.activeSelf;
        public string ResidencyButtonText => _residencyButtonText.text;
        public bool MyCityPinVisible => _myCityPin.gameObject.activeSelf;
        /// <summary>확대·이동 애니메이션이 끝났는가. 테스트가 화면 좌표를 읽기 전에 기다린다.</summary>
        public bool IsSettled =>
            Mathf.Abs(_scale - _targetScale) <= _targetScale * 0.002f && (_center - _targetCenter).sqrMagnitude < 0.25f;
        /// <summary>지금 배율 (지도 한 칸이 화면 기준 몇 단위인가).</summary>
        public float Scale => _scale;

        // 도시 위험도 (v8.0)
        /// <summary>지역을 위험 단계의 색으로 칠하고 있는가 (범례도 함께 보인다).</summary>
        public bool RiskColorsShown => _riskOn;
        public bool LegendVisible => _legend.activeSelf;
        public string LegendText => _legendAll;
        public string RiskButtonText => _riskButtonText.text;
        public string InfoRisk => _riskValue.text;
        public string InfoDefensePower => _powerValue.text;
        public string InfoRecentInvasions => _invasionValue.text;
        public string InfoContribution => _contributionValue.text;
        public string InfoRank => _rankValue.text;
        public string InfoSupport => _supportValue.text;
        public string InfoRiskLine => _riskLine.text;
        public string InfoRiskSummary => _riskSummary.gameObject.activeSelf ? _riskSummary.text : null;
        public bool CityRowsVisible => _cityRows.activeSelf;
        public bool SupportButtonVisible => _browseButtons.activeSelf && _supportButton.gameObject.activeSelf;
        public bool SheetVisible => _sheet.activeSelf;
        public string SheetTitle => _sheetTitle.text;
        public string SheetBody => _sheetBody.text;
        /// <summary>전국 보기에서 위험·긴급 도시에 붙인 표시의 수.</summary>
        public int RiskMarkersShown => _riskMarkerCount;
        /// <summary>그 지역의 이름표에 쓰인 글 (확대해서 볼 때). 이름표가 없으면 null.</summary>
        public string RegionLabelText(string regionCode)
        {
            for (int i = 0; i < _regionLabelCount; i++)
                if (_regionLabelOwners[i].code == regionCode) return _regionLabels[i].text;
            return null;
        }
        public string SidoLabelText(string sidoCode)
        {
            MapData data = MapData.Instance;
            for (int i = 0; i < data.Sidos.Count; i++)
                if (data.Sidos[i].code == sidoCode) return _sidoLabels[i].text;
            return null;
        }

        private Canvas _canvas;
        private GameObject _root;
        private RectTransform _viewport;
        private RectTransform _content;
        private MapMeshGraphic _fill;
        private MapMeshGraphic _regionLines;
        private MapMeshGraphic _overlay;
        private MapMeshGraphic _sidoLines;

        private Text[] _sidoLabels;
        private float[] _sidoLabelWidths;
        private Vector2[] _scratchAt;
        private bool[] _scratchShown;
        private Text[] _regionLabels;
        private float[] _regionLabelWidths;
        private MapRegion[] _regionLabelOwners;
        private int _regionLabelCount;
        private Text[] _markLabels;
        private RectTransform[] _markDots;
        private RectTransform _myCityPin;

        private Text _title;
        private Text _guide;
        private Text _crumb;
        private Text _infoTitle;
        private Text _infoSub;
        private Text _residentsValue;
        private Text _pointsValue;
        private Text _infoNote;
        private Image _residencyButton;
        private Text _residencyButtonText;
        private RectTransform _backButton;
        private RectTransform _closeButton;
        private Text _attribution;
        private GameObject _tooltip;
        private RectTransform _tooltipRect;
        private Text _tooltipText;

        // 도시 위험도 (v8.0): 색 + 모양 + 글자로 함께 보여 준다 (docs/CITY_RISK_SYSTEM.md 8장).
        private const int RiskMarkerPool = 40;
        private Text[] _riskMarkers;
        private float[] _riskMarkerWidths;
        private MapRegion[] _riskMarkerOwners;
        private int _riskMarkerCount;
        private GameObject _legend;
        private string _legendAll = "";
        private RectTransform _riskButton;
        private Text _riskButtonText;
        private GameObject _cityRows;
        private Text _riskValue;
        private Text _powerValue;
        private Text _invasionValue;
        private Text _contributionValue;
        private Text _rankValue;
        private Text _supportValue;
        private Text _riskSummary;
        private Text _riskLine;
        private GameObject _browseButtons;
        private RectTransform _rankingButton;
        private RectTransform _historyButton;
        private Image _supportButton;
        private GameObject _sheet;
        private Text _sheetTitle;
        private Text _sheetBody;
        private bool _riskOn;
        private int _shownRiskKey = int.MinValue;

        // 보는 방법: 화면 가운데에 놓인 지도 좌표와 배율. 목표값을 향해 부드럽게 움직인다.
        private Vector2 _center;
        private Vector2 _targetCenter;
        private float _scale = 0.07f;
        private float _targetScale = 0.07f;
        private float _builtScale = -1f;
        private Vector2 _viewOffset;
        private Vector2 _viewSize;

        private int _shownVersion = -1;
        private bool _wasOpen;
        private string _shownFocus;
        private string _shownSelected;
        private string _shownMine;
        private string _hoverSido;
        private string _hoverRegion;
        private bool _pressed;
        private bool _dragging;
        private Vector2 _pressLocal;
        private Vector2 _lastLocal;

        public static MapPanel Create(Canvas canvas, Transform root)
        {
            RectTransform rect = UiFactory.Stretch(root, "Map");
            var panel = rect.gameObject.AddComponent<MapPanel>();
            panel._canvas = canvas;
            panel._root = rect.gameObject;
            panel._viewport = rect;
            panel.Build();
            rect.gameObject.SetActive(false);
            return panel;
        }

        // ── 조립 ────────────────────────────────────────────────

        private void Build()
        {
            MapData data = MapData.Instance;
            var size = new Vector2(Mathf.Max(1, data.File.width), Mathf.Max(1, data.File.height));

            UiFactory.Fill(_viewport, Sea);

            // 지도 도형. 이 아래는 지도 좌표를 그대로 쓰고, _content의 위치와 배율로 화면에 맞춘다.
            var contentObject = new GameObject("Content", typeof(RectTransform));
            _content = (RectTransform)contentObject.transform;
            _content.SetParent(_viewport, false);
            _content.anchorMin = _content.anchorMax = new Vector2(0.5f, 0.5f);
            _content.pivot = Vector2.zero;
            _content.sizeDelta = size;

            _fill = MapMeshGraphic.Create(_content, "Fill", size, PopulateFill);
            _regionLines = MapMeshGraphic.Create(_content, "RegionLines", size, PopulateRegionLines);
            _overlay = MapMeshGraphic.Create(_content, "Overlay", size, PopulateOverlay);
            _sidoLines = MapMeshGraphic.Create(_content, "SidoLines", size, PopulateSidoLines);

            BuildLabels(data);
            BuildHeader();
            BuildInfoPanel();
            BuildFooter(data);
            BuildSheet();

            Image tooltip = UiFactory.Panel(_viewport, "Tooltip", new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(200f, 44f),
                new Color(0.04f, 0.05f, 0.09f, 0.94f));
            _tooltip = tooltip.gameObject;
            _tooltipRect = (RectTransform)tooltip.transform;
            _tooltipRect.pivot = new Vector2(0f, 0f);
            _tooltipText = UiFactory.Label(tooltip.transform, "Text", 24, TextAnchor.MiddleCenter, Color.white);
            _tooltipText.horizontalOverflow = HorizontalWrapMode.Overflow;
            _tooltip.SetActive(false);
        }

        private void BuildLabels(MapData data)
        {
            RectTransform layer = UiFactory.Stretch(_viewport, "Labels");

            _sidoLabels = new Text[data.Sidos.Count];
            _sidoLabelWidths = new float[data.Sidos.Count];
            int mostRegions = 1;
            for (int i = 0; i < data.Sidos.Count; i++)
            {
                _sidoLabels[i] = MapLabel(layer, "Sido_" + data.Sidos[i].code, 22, Color.white);
                _sidoLabels[i].text = data.Sidos[i].shortName;
                _sidoLabelWidths[i] = _sidoLabels[i].preferredWidth;
                int count = 0;
                for (int r = 0; r < data.Regions.Count; r++)
                    if (data.Regions[r].sido == data.Sidos[i].code) count++;
                mostRegions = Mathf.Max(mostRegions, count);
            }

            _regionLabels = new Text[mostRegions];
            _regionLabelWidths = new float[mostRegions];
            _regionLabelOwners = new MapRegion[mostRegions];
            for (int i = 0; i < mostRegions; i++) _regionLabels[i] = MapLabel(layer, "Region_" + i, 22, Color.white);

            MapMark[] marks = data.File.marks;
            _markLabels = new Text[marks.Length];
            _markDots = new RectTransform[marks.Length];
            for (int i = 0; i < marks.Length; i++)
            {
                Image dot = UiFactory.Panel(layer, "MarkDot_" + i, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(8f, 8f), UiFactory.Accent);
                _markDots[i] = (RectTransform)dot.transform;
                _markLabels[i] = MapLabel(layer, "Mark_" + i, 18, UiFactory.TextColor);
                _markLabels[i].text = marks[i].name;
            }

            // 전국 보기에서 위험·긴급 도시에 붙이는 표시 (모양 + 글자). 색만으로 알리지 않는다.
            _riskMarkers = new Text[RiskMarkerPool];
            _riskMarkerWidths = new float[RiskMarkerPool];
            _riskMarkerOwners = new MapRegion[RiskMarkerPool];
            for (int i = 0; i < RiskMarkerPool; i++)
            {
                _riskMarkers[i] = MapLabel(layer, "Risk_" + i, 20, Color.white);
                _riskMarkers[i].gameObject.SetActive(false);
            }

            Image pin = UiFactory.Panel(layer, "MyCity", new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(104f, 34f), UiFactory.Accent);
            _myCityPin = (RectTransform)pin.transform;
            _myCityPin.pivot = new Vector2(0.5f, 0f);
            UiFactory.Label(pin.transform, "Text", 20, TextAnchor.MiddleCenter, ButtonText).text = "내 도시";
        }

        private static Text MapLabel(Transform parent, string name, int fontSize, Color color)
        {
            Text label = UiFactory.LabelAt(parent, name, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(240f, 34f),
                fontSize, TextAnchor.MiddleCenter, color, true);
            label.horizontalOverflow = HorizontalWrapMode.Overflow;
            return label;
        }

        private void BuildHeader()
        {
            // 지도는 화면 전체에 그려지고 끌어 옮길 수 있다. 위아래 글자(특히 출처 표시)가
            // 지도 색에 묻히지 않도록 글자 뒤에 띠를 깐다.
            RectTransform top = UiFactory.Stretch(_viewport, "TopBar");
            top.anchorMin = new Vector2(0f, 1f);
            top.offsetMin = new Vector2(0f, -TopMargin + 12f);
            UiFactory.Fill(top, new Color(Sea.r, Sea.g, Sea.b, 0.9f));
            RectTransform bottom = UiFactory.Stretch(_viewport, "BottomBar");
            bottom.anchorMax = new Vector2(1f, 0f);
            bottom.offsetMax = new Vector2(0f, BottomMargin - 12f);
            UiFactory.Fill(bottom, new Color(Sea.r, Sea.g, Sea.b, 0.9f));

            _title = UiFactory.LabelAt(_viewport, "Title", new Vector2(0f, 1f), new Vector2(SideMargin, -24f), new Vector2(1200f, 58f),
                44, TextAnchor.MiddleLeft, Color.white);
            _guide = UiFactory.LabelAt(_viewport, "Guide", new Vector2(0f, 1f), new Vector2(SideMargin, -82f), new Vector2(1300f, 36f),
                26, TextAnchor.MiddleLeft, UiFactory.Accent);
            _crumb = UiFactory.LabelAt(_viewport, "Crumb", new Vector2(0f, 1f), new Vector2(SideMargin, -82f), new Vector2(1300f, 36f),
                26, TextAnchor.MiddleLeft, Dim);

            // 오른쪽 위: 누를 수 있는 뒤로 / 닫기
            _backButton = SmallButton("Back", new Vector2(-SideMargin - 230f, -30f), "뒤로   Esc");
            _closeButton = SmallButton("Close", new Vector2(-SideMargin, -30f), "닫기   M");
            // 위험도 색과 시·도 색을 바꿔 보는 단추
            _riskButton = SmallButton("RiskView", new Vector2(-SideMargin - 460f, -30f), "");
            _riskButtonText = _riskButton.GetComponentInChildren<Text>();
        }

        private RectTransform SmallButton(string name, Vector2 position, string text)
        {
            Image back = UiFactory.Panel(_viewport, name, new Vector2(1f, 1f), position, new Vector2(214f, 54f), new Color(0.16f, 0.20f, 0.30f, 1f));
            UiFactory.Label(back.transform, "Text", 26, TextAnchor.MiddleCenter, UiFactory.TextColor).text = text;
            return (RectTransform)back.transform;
        }

        private void BuildInfoPanel()
        {
            Image panel = UiFactory.Panel(_viewport, "Info", new Vector2(1f, 1f), new Vector2(-SideMargin, -110f),
                new Vector2(PanelWidth - 16f, 860f), new Color(0.09f, 0.12f, 0.20f, 0.96f));
            Transform root = panel.transform;
            float width = PanelWidth - 16f;
            UiFactory.Panel(root, "TopLine", new Vector2(0.5f, 1f), Vector2.zero, new Vector2(width, 6f), UiFactory.Accent);

            _infoTitle = UiFactory.LabelAt(root, "Title", new Vector2(0.5f, 1f), new Vector2(0f, -22f), new Vector2(width - 48f, 96f),
                36, TextAnchor.MiddleLeft, Color.white);
            _infoSub = UiFactory.LabelAt(root, "Sub", new Vector2(0.5f, 1f), new Vector2(0f, -122f), new Vector2(width - 48f, 34f),
                24, TextAnchor.MiddleLeft, Dim);

            UiFactory.LabelAt(root, "ResidentsLabel", new Vector2(0f, 1f), new Vector2(24f, -180f), new Vector2(200f, 44f),
                28, TextAnchor.MiddleLeft, UiFactory.TextColor).text = "가디언";
            _residentsValue = UiFactory.LabelAt(root, "Residents", new Vector2(1f, 1f), new Vector2(-24f, -180f), new Vector2(220f, 44f),
                30, TextAnchor.MiddleRight, Color.white);
            UiFactory.LabelAt(root, "PointsLabel", new Vector2(0f, 1f), new Vector2(24f, -232f), new Vector2(200f, 44f),
                28, TextAnchor.MiddleLeft, UiFactory.TextColor).text = "방어 포인트";
            _pointsValue = UiFactory.LabelAt(root, "Points", new Vector2(1f, 1f), new Vector2(-24f, -232f), new Vector2(220f, 44f),
                30, TextAnchor.MiddleRight, UiFactory.Accent);

            // 도시 위험도 (v8.0). 단계는 색과 함께 모양(●▲◆■)과 글자로 적는다.
            _riskValue = InfoRow(root, "Risk", "위험도", -290f);
            _cityRows = UiFactory.Stretch(root, "CityRows").gameObject;
            Transform rows = _cityRows.transform;
            _powerValue = InfoRow(rows, "Power", "방어력", -336f);
            _invasionValue = InfoRow(rows, "Invasions", "최근 침공", -382f);
            _contributionValue = InfoRow(rows, "Contribution", "도시 기여도", -428f);
            _rankValue = InfoRow(rows, "Rank", "지역 순위", -474f);
            _supportValue = InfoRow(rows, "Support", "지원 요청", -520f);
            // 시·도나 전국을 볼 때: 단계별 도시 수
            _riskSummary = UiFactory.LabelAt(root, "RiskSummary", new Vector2(0.5f, 1f), new Vector2(0f, -340f), new Vector2(width - 48f, 220f),
                26, TextAnchor.UpperLeft, UiFactory.TextColor);
            // 침공 진행 상황 또는 도시 분위기
            _riskLine = UiFactory.LabelAt(root, "RiskLine", new Vector2(0.5f, 1f), new Vector2(0f, -572f), new Vector2(width - 48f, 68f),
                22, TextAnchor.UpperLeft, Dim);

            _infoNote = UiFactory.LabelAt(root, "Note", new Vector2(0.5f, 1f), new Vector2(0f, -648f), new Vector2(width - 48f, 96f),
                26, TextAnchor.UpperLeft, UiFactory.TextColor);

            _residencyButton = UiFactory.Panel(root, "ResidencyButton", new Vector2(0.5f, 0f), new Vector2(0f, 24f),
                new Vector2(width - 48f, 76f), UiFactory.Accent);
            _residencyButtonText = UiFactory.Label(_residencyButton.transform, "Text", 30, TextAnchor.MiddleCenter, ButtonText);

            // 둘러볼 때의 단추: 지역 랭킹, 위험도 이력, 지원 출동
            _browseButtons = UiFactory.Stretch(root, "BrowseButtons").gameObject;
            Transform buttons = _browseButtons.transform;
            float half = (width - 48f - 12f) * 0.5f;
            _rankingButton = InfoButton(buttons, "Ranking", new Vector2(0f, 0f), new Vector2(24f, 70f), new Vector2(half, 40f), "지역 랭킹");
            _historyButton = InfoButton(buttons, "History", new Vector2(1f, 0f), new Vector2(-24f, 70f), new Vector2(half, 40f), "위험도 이력");
            _supportButton = UiFactory.Panel(buttons, "Support", new Vector2(0.5f, 0f), new Vector2(0f, 24f), new Vector2(width - 48f, 40f), UiFactory.Accent);
            UiFactory.Label(_supportButton.transform, "Text", 24, TextAnchor.MiddleCenter, ButtonText).text = "지원 출동";
        }

        private static Text InfoRow(Transform root, string name, string label, float y)
        {
            UiFactory.LabelAt(root, name + "Label", new Vector2(0f, 1f), new Vector2(24f, y), new Vector2(170f, 40f),
                26, TextAnchor.MiddleLeft, UiFactory.TextColor).text = label;
            Text value = UiFactory.LabelAt(root, name, new Vector2(1f, 1f), new Vector2(-24f, y), new Vector2(250f, 40f),
                26, TextAnchor.MiddleRight, Color.white);
            value.horizontalOverflow = HorizontalWrapMode.Overflow;
            return value;
        }

        private static RectTransform InfoButton(Transform parent, string name, Vector2 anchor, Vector2 position, Vector2 size, string text)
        {
            Image back = UiFactory.Panel(parent, name, anchor, position, size, new Color(0.16f, 0.20f, 0.30f, 1f));
            UiFactory.Label(back.transform, "Text", 24, TextAnchor.MiddleCenter, UiFactory.TextColor).text = text;
            return (RectTransform)back.transform;
        }

        /// <summary>지역 랭킹이나 위험도 이력을 지도 위에 띄우는 글 한 장.</summary>
        private void BuildSheet()
        {
            Image sheet = UiFactory.Panel(_viewport, "Sheet", new Vector2(0f, 1f), new Vector2(SideMargin, -TopMargin - 4f),
                new Vector2(1340f, 780f), new Color(0.06f, 0.10f, 0.18f, 0.97f));
            _sheet = sheet.gameObject;
            Transform root = sheet.transform;
            UiFactory.Panel(root, "TopLine", new Vector2(0.5f, 1f), Vector2.zero, new Vector2(1340f, 6f), UiFactory.Accent);
            _sheetTitle = UiFactory.LabelAt(root, "Title", new Vector2(0f, 1f), new Vector2(36f, -24f), new Vector2(1200f, 56f),
                40, TextAnchor.MiddleLeft, Color.white);
            _sheetBody = UiFactory.LabelAt(root, "Body", new Vector2(0f, 1f), new Vector2(36f, -100f), new Vector2(1268f, 600f),
                26, TextAnchor.UpperLeft, UiFactory.TextColor);
            // 글은 줄을 스스로 나눠서 온다.
            _sheetBody.horizontalOverflow = HorizontalWrapMode.Overflow;
            _sheetBody.verticalOverflow = VerticalWrapMode.Overflow;
            UiFactory.LabelAt(root, "Close", new Vector2(0f, 0f), new Vector2(36f, 20f), new Vector2(900f, 36f),
                24, TextAnchor.MiddleLeft, UiFactory.Accent).text = "E  닫기      마우스 오른쪽 버튼  뒤로";
            _sheet.SetActive(false);
        }

        private void BuildFooter(MapData data)
        {
            Text keys = UiFactory.LabelAt(_viewport, "Keys", new Vector2(0f, 0f), new Vector2(SideMargin, 46f), new Vector2(1500f, 36f),
                24, TextAnchor.MiddleLeft, UiFactory.TextColor);
            keys.horizontalOverflow = HorizontalWrapMode.Overflow;
            keys.text = "마우스 왼쪽 버튼  고르기      휠  확대·축소      누른 채 끌기  옮기기      마우스 오른쪽 버튼  뒤로";

            // 출처 표시는 경계 데이터의 이용 조건이다. 지우거나 가리지 않는다 (docs/MAP_SYSTEM.md 2장).
            _attribution = UiFactory.LabelAt(_viewport, "Attribution", new Vector2(0f, 0f), new Vector2(SideMargin, 14f), new Vector2(1840f, 26f),
                16, TextAnchor.MiddleLeft, Dim);
            _attribution.horizontalOverflow = HorizontalWrapMode.Overflow;
            _attribution.text = data.File.attribution ?? "";

            // 위험도 범례: 색 칸마다 모양과 이름, 범위를 함께 적는다. 지도 자리의 왼쪽 아래에 둔다.
            Image legend = UiFactory.Panel(_viewport, "Legend", new Vector2(0f, 0f), new Vector2(SideMargin, BottomMargin + 4f),
                new Vector2(760f, 44f), new Color(0.04f, 0.05f, 0.09f, 0.90f));
            _legend = legend.gameObject;
            string[] stages = { RiskStages.Safe, RiskStages.Caution, RiskStages.Danger, RiskStages.Emergency };
            string[] ranges = { "0–24", "25–49", "50–74", "75–100" };
            string all = "";
            for (int i = 0; i < stages.Length; i++)
            {
                float x = 14f + i * 186f;
                UiFactory.Panel(legend.transform, "Chip_" + i, new Vector2(0f, 0.5f), new Vector2(x, 0f), new Vector2(18f, 18f),
                    CityRiskService.StageColor(stages[i]));
                string text = $"{RiskStages.Icon(stages[i])} {RiskStages.DisplayName(stages[i])} {ranges[i]}";
                Text label = UiFactory.LabelAt(legend.transform, "Stage_" + i, new Vector2(0f, 0.5f), new Vector2(x + 26f, 0f), new Vector2(160f, 34f),
                    22, TextAnchor.MiddleLeft, Color.white);
                label.horizontalOverflow = HorizontalWrapMode.Overflow;
                label.text = text;
                all += (i > 0 ? "   " : "") + text;
            }
            _legendAll = all;
            _legend.SetActive(false);
        }

        // ── 매 프레임 ───────────────────────────────────────────

        /// <summary>
        /// Hud가 매 프레임 부른다. 이 화면이 꺼져 있는 동안에는 자신의 Update가 돌지 않으므로
        /// 켜고 끄는 일을 바깥에서 맡는다.
        /// </summary>
        public void Refresh(float dt)
        {
            MapController map = MapController.Instance;
            bool open = map != null && map.IsOpen;
            if (_root.activeSelf != open) _root.SetActive(open);
            if (!open)
            {
                _wasOpen = false;
                return;
            }

            Vector2 sizeBefore = _viewSize;
            MeasureView();
            bool justOpened = !_wasOpen;
            _wasOpen = true;
            // 창 크기가 바뀌면 보고 있던 곳을 새 크기에 다시 맞춘다.
            if (!justOpened && (_viewSize - sizeBefore).sqrMagnitude > 1f) FitTo(_shownFocus, true);
            if (justOpened)
            {
                _shownVersion = -1;
                _shownFocus = null;
                _hoverSido = null;
                _hoverRegion = null;
                _pressed = false;
                _dragging = false;
                FitTo(map.FocusedSido, true);
            }

            if (map.Version != _shownVersion)
            {
                _shownVersion = map.Version;
                ApplyState(map, justOpened);
            }

            HandlePointer(map);
            Animate(dt);
            PlaceLabels(map);
        }

        private void MeasureView()
        {
            _viewSize = _viewport.rect.size;
            float left = SideMargin;
            float right = _viewSize.x - PanelWidth - SideMargin - 20f;
            float bottom = BottomMargin;
            float top = _viewSize.y - TopMargin;
            // 지도를 놓는 자리(정보 패널과 위아래 글자를 뺀 곳)의 가운데. 화면 가운데를 (0,0)으로 본 좌표.
            _viewOffset = new Vector2((left + right) * 0.5f - _viewSize.x * 0.5f, (bottom + top) * 0.5f - _viewSize.y * 0.5f);
        }

        private Vector2 UsableSize => new Vector2(
            Mathf.Max(100f, _viewSize.x - PanelWidth - SideMargin * 2f - 20f),
            Mathf.Max(100f, _viewSize.y - TopMargin - BottomMargin));

        private float NationalScale
        {
            get
            {
                MapFile file = MapData.Instance.File;
                Vector2 usable = UsableSize;
                return Mathf.Min(usable.x / Mathf.Max(1, file.width), usable.y / Mathf.Max(1, file.height)) * 0.97f;
            }
        }

        /// <summary>시·도 하나(또는 전국)가 화면에 차도록 맞춘다.</summary>
        private void FitTo(string sidoCode, bool immediately)
        {
            MapData data = MapData.Instance;
            MapSido sido = data.FindSido(sidoCode);
            if (sido == null)
            {
                _targetScale = NationalScale;
                _targetCenter = new Vector2(data.File.width * 0.5f, data.File.height * 0.5f);
            }
            else
            {
                float width = Mathf.Max(1f, sido.focusMaxX - sido.focusMinX);
                float height = Mathf.Max(1f, sido.focusMaxY - sido.focusMinY);
                Vector2 usable = UsableSize;
                _targetScale = Mathf.Min(Mathf.Min(usable.x / width, usable.y / height) * 0.86f, MaxScale);
                _targetCenter = new Vector2((sido.focusMinX + sido.focusMaxX) * 0.5f, (sido.focusMinY + sido.focusMaxY) * 0.5f);
            }
            if (immediately)
            {
                _scale = _targetScale;
                _center = _targetCenter;
            }
        }

        private void ApplyState(MapController map, bool justOpened)
        {
            GameSession session = GameSession.Current;
            string mine = session != null && session.IsResident ? session.Me.regionCode : null;

            // 위험도 색으로 보는 중이고 값을 받았을 때만 위험도를 그린다. 값이 바뀌면 다시 칠한다.
            int riskKey = map.RiskView && map.RiskLoaded ? CityRiskService.Version : -1;
            bool riskChanged = riskKey != _shownRiskKey;
            _shownRiskKey = riskKey;
            _riskOn = riskKey != -1;

            bool focusChanged = map.FocusedSido != _shownFocus;
            if (focusChanged || map.SelectedRegion != _shownSelected || mine != _shownMine || justOpened || riskChanged)
            {
                _shownFocus = map.FocusedSido;
                _shownSelected = map.SelectedRegion;
                _shownMine = mine;
                if (focusChanged || justOpened || riskChanged)
                {
                    if (focusChanged && !justOpened) FitTo(_shownFocus, false);
                    if (focusChanged || justOpened)
                    {
                        _hoverRegion = null;
                        _hoverSido = null;
                    }
                    _fill.SetVerticesDirty();
                    _regionLines.SetVerticesDirty();
                    AssignRegionLabels();
                    RefreshSidoLabels();
                    AssignRiskMarkers();
                }
                _overlay.SetVerticesDirty();
            }
            RefreshTexts(map, session);
        }

        // ── 마우스 ──────────────────────────────────────────────

        private Camera UiCamera => _canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : _canvas.worldCamera;

        private Vector2 MapPointOf(Vector2 local) => (local - _viewOffset) / _scale + _center;
        private Vector2 LocalOf(Vector2 mapPoint) => _viewOffset + (mapPoint - _center) * _scale;

        private void HandlePointer(MapController map)
        {
            IPlayerInput input = PlayerInputHub.Current;
            bool active = map.AcceptsInput
                          && RectTransformUtility.ScreenPointToLocalPointInRectangle(_viewport, input.PointerPosition, UiCamera, out _lastLocal);
            if (!active)
            {
                _pressed = false;
                _dragging = false;
                SetHover(null, null);
                return;
            }

            Vector2 local = _lastLocal;
            bool overPanel = local.x > _viewSize.x * 0.5f - PanelWidth - SideMargin;
            // 랭킹이나 이력이 떠 있는 동안에는 지도를 만지지 않는다 (아무 곳이나 누르면 닫힌다).
            bool overMap = !overPanel && map.Sheet == MapSheet.None
                           && !Over(_backButton, input) && !Over(_closeButton, input) && !Over(_riskButton, input);

            // 휠: 가리키는 곳을 그대로 둔 채 확대·축소한다.
            float zoom = input.ZoomDelta;
            if (overMap && Mathf.Abs(zoom) > 0.001f)
            {
                Vector2 anchor = (local - _viewOffset) / _targetScale + _targetCenter;
                _targetScale = Mathf.Clamp(_targetScale * Mathf.Pow(1.25f, zoom), NationalScale * 0.7f, MaxScale);
                _targetCenter = anchor - (local - _viewOffset) / _targetScale;
            }

            if (input.PointerPressed)
            {
                _pressed = true;
                _dragging = false;
                _pressLocal = local;
            }
            else if (_pressed && input.PointerHeld)
            {
                if (!_dragging && overMap && (local - _pressLocal).magnitude > DragThreshold)
                {
                    _dragging = true;
                    _pressLocal = local;
                }
                if (_dragging)
                {
                    // 끌기: 잡은 곳이 손가락을 따라오게 한다.
                    Vector2 delta = (local - _pressLocal) / _scale;
                    _center -= delta;
                    _targetCenter = _center;
                    _targetScale = _scale;
                    _pressLocal = local;
                }
            }

            if (_pressed && input.PointerReleased)
            {
                bool wasDragging = _dragging;
                _pressed = false;
                _dragging = false;
                if (!wasDragging) Click(map, input, local, overMap);
                return;
            }

            if (_dragging || !overMap)
            {
                SetHover(null, null);
                return;
            }

            MapRegion region = MapData.Instance.RegionAt(MapPointOf(local), ClickSlopPixels / _scale);
            if (region == null) SetHover(null, null);
            else if (_shownFocus != null && region.sido == _shownFocus) SetHover(null, region.code);
            else SetHover(region.sido, null);
        }

        private void Click(MapController map, IPlayerInput input, Vector2 local, bool overMap)
        {
            if (Over(_backButton, input)) { map.Back(); return; }
            if (Over(_closeButton, input)) { map.Close(); return; }
            if (map.Sheet != MapSheet.None) { map.Back(); return; }
            if (Over(_riskButton, input)) { map.ToggleRiskView(); return; }
            if (_residencyButton.gameObject.activeSelf && Over((RectTransform)_residencyButton.transform, input))
            {
                map.RequestResidency();
                return;
            }
            if (_browseButtons.activeSelf)
            {
                if (Over(_rankingButton, input)) { map.ShowRankings(); return; }
                if (_historyButton.gameObject.activeSelf && Over(_historyButton, input)) { map.ShowHistory(); return; }
                if (_supportButton.gameObject.activeSelf && Over((RectTransform)_supportButton.transform, input))
                {
                    map.OfferSupport();
                    return;
                }
            }
            if (!overMap) return;

            MapRegion region = MapData.Instance.RegionAt(MapPointOf(local), ClickSlopPixels / _scale);
            if (region == null) return;
            // 전국 보기에서는 시·도를 고르고, 시·도 안에서는 시·군·구를 고른다.
            if (_shownFocus != null && region.sido == _shownFocus) map.SelectRegion(region.code);
            else map.FocusSido(region.sido);
        }

        private bool Over(RectTransform rect, IPlayerInput input) =>
            RectTransformUtility.RectangleContainsScreenPoint(rect, input.PointerPosition, UiCamera);

        private void SetHover(string sidoCode, string regionCode)
        {
            if (_hoverSido == sidoCode && _hoverRegion == regionCode)
            {
                if (_tooltip.activeSelf) PlaceTooltip();
                return;
            }
            _hoverSido = sidoCode;
            _hoverRegion = regionCode;
            _overlay.SetVerticesDirty();

            // 시·군·구는 가리키면 지도 위의 이름표가 나타나므로 말풍선이 필요 없다.
            // 시·도는 지도에 줄인 이름만 있어서 온전한 이름을 말풍선으로 보여 준다.
            string text = regionCode == null && sidoCode != null ? MapData.Instance.FindSido(sidoCode)?.name : null;
            _tooltip.SetActive(!string.IsNullOrEmpty(text));
            if (!_tooltip.activeSelf) return;
            _tooltipText.text = text;
            _tooltipRect.sizeDelta = new Vector2(_tooltipText.preferredWidth + 32f, 44f);
            PlaceTooltip();
        }

        private void PlaceTooltip()
        {
            _tooltipRect.anchoredPosition = _lastLocal + new Vector2(18f, 22f);
        }

        // ── 보기 ────────────────────────────────────────────────

        private void Animate(float dt)
        {
            float blend = 1f - Mathf.Exp(-12f * dt);
            _scale = Mathf.Lerp(_scale, _targetScale, blend);
            _center = Vector2.Lerp(_center, _targetCenter, blend);
            if (IsSettled)
            {
                _scale = _targetScale;
                _center = _targetCenter;
            }

            _content.localScale = new Vector3(_scale, _scale, 1f);
            _content.anchoredPosition = LocalOf(Vector2.zero);

            // 선의 굵기는 화면 기준으로 일정해야 한다. 배율이 눈에 띄게 달라지면 선을 다시 만든다.
            if (_builtScale <= 0f || _scale > _builtScale * 1.15f || _scale < _builtScale * 0.87f
                || (IsSettled && !Mathf.Approximately(_builtScale, _scale)))
            {
                _builtScale = _scale;
                _regionLines.SetVerticesDirty();
                _sidoLines.SetVerticesDirty();
                _overlay.SetVerticesDirty();
            }
        }

        /// <summary>화면 기준 굵기(px)를 지도 좌표의 반폭으로 바꾼다.</summary>
        private float HalfWidth(float pixels) => pixels * 0.5f / Mathf.Max(0.0001f, _builtScale > 0f ? _builtScale : _scale);

        private void AssignRegionLabels()
        {
            MapData data = MapData.Instance;
            _regionLabelCount = 0;
            if (_shownFocus != null)
            {
                for (int i = 0; i < data.Regions.Count && _regionLabelCount < _regionLabels.Length; i++)
                {
                    MapRegion region = data.Regions[i];
                    if (region.sido != _shownFocus) continue;
                    Text label = _regionLabels[_regionLabelCount];
                    // 위험도를 볼 때는 이름 옆에 단계의 모양과 수치를 적는다: "해운대구 ▲37"
                    CityStatusDto city = _riskOn ? CityRiskService.Of(region.code) : null;
                    label.text = city != null ? $"{region.name} {RiskStages.Icon(city.stage)}{city.risk}" : region.name;
                    _regionLabelWidths[_regionLabelCount] = label.preferredWidth;
                    _regionLabelOwners[_regionLabelCount] = region;
                    _regionLabelCount++;
                }
            }
            for (int i = 0; i < _regionLabels.Length; i++)
                _regionLabels[i].gameObject.SetActive(i < _regionLabelCount);
        }

        /// <summary>시·도 이름 옆에, 그 시·도에서 가장 높은 위험 단계의 모양을 붙인다: "부산 ◆"</summary>
        private void RefreshSidoLabels()
        {
            MapData data = MapData.Instance;
            for (int i = 0; i < data.Sidos.Count; i++)
            {
                MapSido sido = data.Sidos[i];
                string text = sido.shortName;
                if (_riskOn)
                {
                    string worst = null;
                    for (int r = 0; r < data.Regions.Count; r++)
                    {
                        if (data.Regions[r].sido != sido.code) continue;
                        CityStatusDto city = CityRiskService.Of(data.Regions[r].code);
                        if (city != null && (worst == null || RiskStages.Order(city.stage) > RiskStages.Order(worst))) worst = city.stage;
                    }
                    if (worst != null) text += " " + RiskStages.Icon(worst);
                }
                if (_sidoLabels[i].text == text) continue;
                _sidoLabels[i].text = text;
                _sidoLabelWidths[i] = _sidoLabels[i].preferredWidth;
            }
        }

        /// <summary>
        /// 전국 보기에서 위험·긴급 단계이거나 지원을 요청한 도시에 표시를 붙인다 (위험도가 높은 순서로, 자리 수만큼).
        /// 시·도를 확대하면 지역 이름표가 같은 정보를 보여 주므로 표시는 숨긴다.
        /// </summary>
        private void AssignRiskMarkers()
        {
            _riskMarkerCount = 0;
            if (_riskOn && _shownFocus == null && CityRiskService.Risks != null)
            {
                MapData data = MapData.Instance;
                var cities = CityRiskService.Risks.cities;
                // 자리가 적으므로 위험도가 높은 도시부터 고른다 (간단한 선택 정렬: 많아야 40번).
                int lastRisk = int.MaxValue;
                string lastCode = null;
                while (_riskMarkerCount < RiskMarkerPool)
                {
                    CityStatusDto best = null;
                    for (int i = 0; i < cities.Count; i++)
                    {
                        CityStatusDto city = cities[i];
                        if (RiskStages.Order(city.stage) < RiskStages.Order(RiskStages.Danger) && !city.SupportOpen) continue;
                        // 이미 고른 것보다 뒤에 오는 것만: (위험도 내림차순, 코드 오름차순)
                        bool after = city.risk < lastRisk
                                     || (city.risk == lastRisk && lastCode != null && string.CompareOrdinal(city.regionCode, lastCode) > 0);
                        if (!after) continue;
                        if (best == null || city.risk > best.risk
                            || (city.risk == best.risk && string.CompareOrdinal(city.regionCode, best.regionCode) < 0))
                            best = city;
                    }
                    if (best == null) break;
                    lastRisk = best.risk;
                    lastCode = best.regionCode;
                    MapRegion region = data.FindRegion(best.regionCode);
                    if (region == null) continue;
                    Text marker = _riskMarkers[_riskMarkerCount];
                    marker.text = RiskStages.Icon(best.stage) + (best.SupportOpen ? " 지원 요청" : " " + RiskStages.DisplayName(best.stage));
                    // 바탕이 이미 단계의 색이라, 글자는 흰색(검은 테두리)으로 써서 어느 색 위에서도 읽히게 한다.
                    marker.color = Color.white;
                    _riskMarkerWidths[_riskMarkerCount] = marker.preferredWidth;
                    _riskMarkerOwners[_riskMarkerCount] = region;
                    _riskMarkerCount++;
                }
            }
            for (int i = 0; i < _riskMarkers.Length; i++)
                _riskMarkers[i].gameObject.SetActive(i < _riskMarkerCount);
        }

        private void PlaceLabels(MapController map)
        {
            MapData data = MapData.Instance;

            // 시·도 이름: 전국 보기에서는 또렷하게, 시·도 안에서는 다른 시·도만 흐리게.
            EnsureScratch(Mathf.Max(_sidoLabels.Length, _regionLabels.Length));
            for (int i = 0; i < _sidoLabels.Length; i++)
            {
                MapSido sido = data.Sidos[i];
                bool show = sido.code != _shownFocus;
                Text label = _sidoLabels[i];
                if (label.gameObject.activeSelf != show) label.gameObject.SetActive(show);
                _scratchShown[i] = show;
                _scratchAt[i] = LocalOf(new Vector2(sido.labelX, sido.labelY));
                if (show) label.color = _shownFocus == null ? Color.white : Dim;
            }
            SpreadLabels(_scratchAt, _sidoLabelWidths, _scratchShown, _sidoLabels.Length, 28f);
            for (int i = 0; i < _sidoLabels.Length; i++)
                if (_scratchShown[i]) _sidoLabels[i].rectTransform.anchoredPosition = _scratchAt[i];

            // 시·군·구 이름: 그 지역이 화면에서 이름보다 좁으면 숨긴다 (가리키거나 고르면 보인다).
            for (int i = 0; i < _regionLabelCount; i++)
            {
                MapRegion region = _regionLabelOwners[i];
                bool emphasized = region.code == _shownSelected || region.code == _hoverRegion;
                bool fits = (region.maxX - region.minX) * _scale >= _regionLabelWidths[i] * 0.8f;
                Text label = _regionLabels[i];
                bool show = fits || emphasized;
                if (label.gameObject.activeSelf != show) label.gameObject.SetActive(show);
                _scratchShown[i] = show;
                _scratchAt[i] = LocalOf(new Vector2(region.labelX, region.labelY));
            }
            SpreadLabels(_scratchAt, _regionLabelWidths, _scratchShown, _regionLabelCount, 28f);
            for (int i = 0; i < _regionLabelCount; i++)
                if (_scratchShown[i]) _regionLabels[i].rectTransform.anchoredPosition = _scratchAt[i];

            MapMark[] marks = data.File.marks;
            for (int i = 0; i < marks.Length; i++)
            {
                Vector2 at = LocalOf(new Vector2(marks[i].x, marks[i].y));
                _markDots[i].anchoredPosition = at;
                // 오른쪽 끝에 붙은 섬이라 이름은 왼쪽에 둔다.
                _markLabels[i].rectTransform.anchoredPosition = at + new Vector2(-34f, 0f);
            }

            // 위험·긴급 도시의 표시: 이름표 자리의 바로 아래.
            for (int i = 0; i < _riskMarkerCount; i++)
            {
                MapRegion owner = _riskMarkerOwners[i];
                _riskMarkers[i].rectTransform.anchoredPosition = LocalOf(new Vector2(owner.labelX, owner.labelY)) + new Vector2(0f, -22f);
            }

            MapRegion mine = data.FindRegion(_shownMine);
            bool showPin = mine != null;
            if (_myCityPin.gameObject.activeSelf != showPin) _myCityPin.gameObject.SetActive(showPin);
            if (showPin) _myCityPin.anchoredPosition = LocalOf(new Vector2(mine.labelX, mine.labelY)) + new Vector2(0f, 20f);
        }

        private void EnsureScratch(int count)
        {
            if (_scratchAt != null && _scratchAt.Length >= count) return;
            _scratchAt = new Vector2[count];
            _scratchShown = new bool[count];
        }

        /// <summary>
        /// 서로 겹친 이름표를 조금씩 밀어 떼어 놓는다 (서울·인천처럼 붙어 있는 곳).
        /// 제자리에서 너무 멀어지면 어느 곳의 이름인지 알 수 없으므로 한 번에 미는 거리를 작게 잡고 몇 번만 되풀이한다.
        /// </summary>
        private static void SpreadLabels(Vector2[] at, float[] widths, bool[] shown, int count, float height)
        {
            for (int pass = 0; pass < 3; pass++)
            {
                bool moved = false;
                for (int i = 0; i < count; i++)
                {
                    if (!shown[i]) continue;
                    for (int j = i + 1; j < count; j++)
                    {
                        if (!shown[j]) continue;
                        float overlapX = (widths[i] + widths[j]) * 0.5f + 4f - Mathf.Abs(at[i].x - at[j].x);
                        float overlapY = height - Mathf.Abs(at[i].y - at[j].y);
                        if (overlapX <= 0f || overlapY <= 0f) continue;
                        moved = true;
                        // 덜 겹친 쪽으로 민다.
                        if (overlapY < overlapX)
                        {
                            float push = Mathf.Min(overlapY * 0.5f, 10f) * (at[i].y >= at[j].y ? 1f : -1f);
                            at[i].y += push;
                            at[j].y -= push;
                        }
                        else
                        {
                            float push = Mathf.Min(overlapX * 0.5f, 10f) * (at[i].x >= at[j].x ? 1f : -1f);
                            at[i].x += push;
                            at[j].x -= push;
                        }
                    }
                }
                if (!moved) break;
            }
        }

        // ── 글자 ────────────────────────────────────────────────

        private void RefreshTexts(MapController map, GameSession session)
        {
            MapData data = MapData.Instance;
            RegionCatalog catalog = session != null ? session.Backend.Regions : RegionCatalog.Default;
            bool residency = map.Mode == MapMode.Residency;

            _title.text = residency ? "전입신고  ·  살고 싶은 도시를 골라요" : "대한민국 지도";
            _guide.gameObject.SetActive(residency);
            _guide.text = "실제로 사는 곳과 달라도 괜찮아요. 좋아하는 도시를 골라요.";

            MapSido sido = data.FindSido(map.FocusedSido);
            MapRegion region = data.FindRegion(map.SelectedRegion);
            string crumb = "전국";
            if (sido != null) crumb += "   >   " + sido.name;
            if (region != null && region.name != sido?.name) crumb += "   >   " + region.name;
            _crumb.text = crumb;
            _crumb.rectTransform.anchoredPosition = new Vector2(SideMargin, residency ? -116f : -82f);

            int residents;
            long points;
            if (region != null)
            {
                RegionStatusDto status = map.StatusOf(region.code);
                residents = status.residents;
                points = status.defensePoints;
                _infoTitle.text = catalog.FullName(region.code) ?? region.name;
                _infoSub.text = region.code == _shownMine ? "내가 지키는 도시" : "";
            }
            else
            {
                Sum(map, data, sido?.code, out residents, out points, out int regionCount);
                _infoTitle.text = sido != null ? sido.name : "대한민국";
                _infoSub.text = sido != null ? $"시·군·구 {regionCount}곳" : $"시·도 {data.Sidos.Count}곳 · 시·군·구 {regionCount}곳";
            }
            _infoSub.color = region != null && region.code == _shownMine ? UiFactory.Accent : Dim;
            _residentsValue.text = map.StatusLoaded ? $"{residents:N0}명" : "…";
            _pointsValue.text = map.StatusLoaded ? points.ToString("N0") : "…";

            if (map.IsBusy) _infoNote.text = "잠시만 기다려 주세요…";
            else if (region == null) _infoNote.text = sido == null ? "시·도를 눌러 보세요." : "시·군·구를 눌러 보세요.";
            else if (residency) _infoNote.text = "이 도시의 가디언이 될까요?\n다른 도시를 눌러 바꿀 수 있어요.";
            else if (region.code == _shownMine) _infoNote.text = "방어전에서 이기면 이 도시의 방어 포인트가 올라요.";
            else if (session != null && !session.IsResident) _infoNote.text = "가디언이 되려면 주민센터의 정나래에게 전입신고를 해요.";
            else _infoNote.text = "";

            _residencyButton.gameObject.SetActive(residency);
            if (residency)
            {
                bool ready = map.CanRequestResidency;
                _residencyButton.color = ready ? UiFactory.Accent : ButtonOff;
                _residencyButtonText.color = ready ? ButtonText : Dim;
                _residencyButtonText.text = ready ? "E   여기로 전입신고" : "지도에서 도시를 골라요";
            }

            RefreshRiskTexts(map, session, data, sido, region);
        }

        /// <summary>
        /// 도시 위험도 칸 (docs/CITY_RISK_SYSTEM.md 8장). 수치는 서버가 계산해 준 것을 그대로 적는다.
        /// 단계는 색만으로 구분하지 않고 모양(●▲◆■)과 이름을 함께 쓴다.
        /// </summary>
        private void RefreshRiskTexts(MapController map, GameSession session, MapData data, MapSido sido, MapRegion region)
        {
            bool loaded = map.RiskLoaded;
            _legend.SetActive(_riskOn);
            _riskButtonText.text = map.RiskView ? "보기: 위험도" : "보기: 시·도";

            _cityRows.SetActive(region != null);
            _riskSummary.gameObject.SetActive(region == null);
            _riskLine.color = Dim;

            if (region != null)
            {
                CityStatusDto city = map.RiskOf(region.code);
                if (city == null)
                {
                    string waiting = loaded ? "—" : "…";
                    _riskValue.text = waiting;
                    _riskValue.color = Color.white;
                    _powerValue.text = waiting;
                    _invasionValue.text = waiting;
                    _contributionValue.text = waiting;
                    _rankValue.text = waiting;
                    _supportValue.text = waiting;
                    _supportValue.color = Color.white;
                    _riskLine.text = "";
                }
                else
                {
                    _riskValue.text = $"{CityRiskService.StageBadge(city.stage)} · {city.risk}";
                    _riskValue.color = CityRiskService.StageColor(city.stage);
                    _powerValue.text = city.defensePower.ToString("N0");
                    _invasionValue.text = $"{city.recentInvasions:N0}회";
                    _contributionValue.text = city.contribution.ToString("N0");
                    // 가디언이 지키는 도시만 순위에 든다. NPC 수비대만 지키는 도시는 따로 적는다.
                    _rankValue.text = city.playerDefended && city.rank > 0 ? $"전국 {city.rank}위" : "NPC 수비대 · 순위 밖";
                    bool supporting = session != null && session.SupportRegionCode == city.regionCode;
                    _supportValue.text = supporting ? "내가 지원 중" : city.SupportOpen ? "요청 중" : "없음";
                    _supportValue.color = supporting || city.SupportOpen ? CityRiskService.StageColor(RiskStages.Emergency) : Color.white;
                    if (city.HasInvasion)
                    {
                        _riskLine.text = $"{InvasionKinds.DisplayName(city.invasionKind)} 진행 중 · 격퇴 {city.invasionProgress} / {city.invasionRequired}";
                        _riskLine.color = CityRiskService.StageColor(RiskStages.Danger);
                    }
                    else
                    {
                        _riskLine.text = RiskStages.Mood(city.stage);
                    }
                }
            }
            else
            {
                // 전국이나 시·도: 단계별 도시 수를 글로 적는다.
                int safe = 0, caution = 0, danger = 0, emergency = 0, invasions = 0, supports = 0, total = 0;
                long riskSum = 0;
                for (int i = 0; i < data.Regions.Count; i++)
                {
                    MapRegion each = data.Regions[i];
                    if (sido != null && each.sido != sido.code) continue;
                    CityStatusDto city = map.RiskOf(each.code);
                    if (city == null) continue;
                    total++;
                    riskSum += city.risk;
                    switch (city.stage)
                    {
                        case RiskStages.Caution: caution++; break;
                        case RiskStages.Danger: danger++; break;
                        case RiskStages.Emergency: emergency++; break;
                        default: safe++; break;
                    }
                    if (city.HasInvasion) invasions++;
                    if (city.SupportOpen) supports++;
                }
                _riskValue.color = Color.white;
                if (total == 0)
                {
                    _riskValue.text = loaded ? "—" : "…";
                    _riskSummary.text = loaded ? "" : "도시 소식을 받는 중이에요…";
                    _riskLine.text = "";
                }
                else
                {
                    _riskValue.text = $"평균 {riskSum / total}";
                    _riskSummary.text =
                        $"{CityRiskService.StageBadge(RiskStages.Emergency)}   {emergency:N0}곳\n" +
                        $"{CityRiskService.StageBadge(RiskStages.Danger)}   {danger:N0}곳\n" +
                        $"{CityRiskService.StageBadge(RiskStages.Caution)}   {caution:N0}곳\n" +
                        $"{CityRiskService.StageBadge(RiskStages.Safe)}   {safe:N0}곳";
                    _riskLine.text = $"침공 진행 중 {invasions:N0}곳 · 지원 요청 {supports:N0}곳";
                }
            }

            // 단추: 둘러볼 때만. 이력은 도시를 골랐을 때, 지원은 나설 수 있을 때만 보인다.
            bool browse = map.Mode == MapMode.Browse;
            _browseButtons.SetActive(browse);
            if (browse)
            {
                _historyButton.gameObject.SetActive(region != null);
                _supportButton.gameObject.SetActive(map.CanOfferSupport);
            }

            bool sheet = map.Sheet != MapSheet.None;
            _sheet.SetActive(sheet);
            if (sheet)
            {
                _sheetTitle.text = map.SheetTitle ?? "";
                _sheetBody.text = map.SheetBody ?? "";
            }
        }

        private static void Sum(MapController map, MapData data, string sidoCode, out int residents, out long points, out int regionCount)
        {
            residents = 0;
            points = 0;
            regionCount = 0;
            for (int i = 0; i < data.Regions.Count; i++)
            {
                MapRegion region = data.Regions[i];
                if (sidoCode != null && region.sido != sidoCode) continue;
                RegionStatusDto status = map.StatusOf(region.code);
                residents += status.residents;
                points += status.defensePoints;
                regionCount++;
            }
        }

        // ── 그리기 ──────────────────────────────────────────────

        private void PopulateFill(VertexHelper vh)
        {
            MapData data = MapData.Instance;
            for (int r = 0; r < data.Regions.Count; r++)
            {
                MapRegion region = data.Regions[r];
                AddRegion(vh, region, FillColor(region, r));
            }
        }

        private Color32 FillColor(MapRegion region, int index)
        {
            Color color = RegionColors.OfSido(region.sido);
            // 위험도를 볼 때는 단계의 색으로 칠한다 (흰 글자가 읽히도록 조금 어둡게). 값이 없는 곳은 시·도의 색 그대로.
            CityStatusDto city = _riskOn ? CityRiskService.Of(region.code) : null;
            if (city != null)
            {
                Color stage = CityRiskService.StageColor(city.stage);
                color = new Color(stage.r * 0.80f, stage.g * 0.80f, stage.b * 0.80f, 1f);
            }
            // 같은 시·도 안에서도 지역마다 밝기를 조금씩 달리해 경계가 보이게 한다.
            float shade = 0.88f + 0.12f * ((index * 7) % 5) / 4f;
            color = new Color(color.r * shade, color.g * shade, color.b * shade, 1f);
            // 시·도를 확대해 보는 동안 다른 시·도는 바다 쪽으로 가라앉힌다.
            if (_shownFocus != null && region.sido != _shownFocus) color = Color.Lerp(color, Sea, 0.62f);
            return color;
        }

        /// <summary>시·군·구 하나를 그 색으로 칠한다. 대시보드의 축약 지도도 이것을 쓴다.</summary>
        public static void AddRegion(VertexHelper vh, MapRegion region, Color32 color)
        {
            int[] v = region.v;
            int[] t = region.t;
            if (vh.currentVertCount + v.Length / 2 > MapMeshGraphic.VertexBudget) return;
            int start = vh.currentVertCount;
            for (int i = 0; i + 1 < v.Length; i += 2)
                vh.AddVert(new Vector3(v[i], v[i + 1], 0f), color, Vector4.zero);
            for (int i = 0; i + 2 < t.Length; i += 3)
                vh.AddTriangle(start + t[i], start + t[i + 1], start + t[i + 2]);
        }

        private static void AddOutline(VertexHelper vh, MapRegion region, float halfWidth, Color32 color)
        {
            int[] v = region.v;
            int[] rings = region.rings;
            for (int r = 0; r + 1 < rings.Length; r += 2)
            {
                int start = rings[r], count = rings[r + 1];
                for (int i = 0; i < count; i++)
                {
                    int a = (start + i) * 2, b = (start + (i + 1) % count) * 2;
                    MapMeshGraphic.AddSegment(vh, new Vector2(v[a], v[a + 1]), new Vector2(v[b], v[b + 1]), halfWidth, color);
                }
            }
        }

        private void PopulateRegionLines(VertexHelper vh)
        {
            if (_shownFocus == null) return;
            MapData data = MapData.Instance;
            float half = HalfWidth(1.6f);
            for (int r = 0; r < data.Regions.Count; r++)
                if (data.Regions[r].sido == _shownFocus) AddOutline(vh, data.Regions[r], half, RegionLine);
        }

        private void PopulateSidoLines(VertexHelper vh)
        {
            MapData data = MapData.Instance;
            float half = HalfWidth(2.2f);
            for (int s = 0; s < data.Sidos.Count; s++)
            {
                int[] lines = data.Sidos[s].lines;
                int i = 0;
                while (i < lines.Length)
                {
                    int count = lines[i++];
                    for (int k = 0; k < count; k++)
                    {
                        int a = i + k * 2, b = i + ((k + 1) % count) * 2;
                        MapMeshGraphic.AddSegment(vh, new Vector2(lines[a], lines[a + 1]), new Vector2(lines[b], lines[b + 1]), half, SidoLine);
                    }
                    i += count * 2;
                }
            }
        }

        private void PopulateOverlay(VertexHelper vh)
        {
            MapData data = MapData.Instance;

            // 가리키는 시·도 전체를 밝힌다 (전국 보기, 또는 확대 중의 다른 시·도).
            if (_hoverSido != null)
            {
                var glow = new Color32(255, 255, 255, 60);
                for (int r = 0; r < data.Regions.Count; r++)
                    if (data.Regions[r].sido == _hoverSido) AddRegion(vh, data.Regions[r], glow);
            }

            MapRegion hover = data.FindRegion(_hoverRegion);
            if (hover != null && hover.code != _shownSelected) AddRegion(vh, hover, new Color32(255, 255, 255, 70));

            MapRegion mine = data.FindRegion(_shownMine);
            if (mine != null && mine.code != _shownSelected) AddOutline(vh, mine, HalfWidth(3f), UiFactory.Accent);

            MapRegion selected = data.FindRegion(_shownSelected);
            if (selected != null)
            {
                Color fill = UiFactory.Accent;
                fill.a = 0.55f;
                AddRegion(vh, selected, fill);
                AddOutline(vh, selected, HalfWidth(4f), Color.white);
            }
        }

        // ── 테스트용 ────────────────────────────────────────────

        /// <summary>그 지역의 이름표 자리가 지금 화면의 어디인가 (픽셀). 자동 테스트가 마우스로 누를 곳을 찾는 데 쓴다.</summary>
        public Vector2 ScreenPointOfRegion(string regionCode)
        {
            MapRegion region = MapData.Instance.FindRegion(regionCode);
            if (region == null) throw new ArgumentException("없는 지역: " + regionCode);
            return ScreenPointOf(new Vector2(region.labelX, region.labelY));
        }

        public Vector2 ScreenPointOfSido(string sidoCode)
        {
            MapSido sido = MapData.Instance.FindSido(sidoCode);
            if (sido == null) throw new ArgumentException("없는 시·도: " + sidoCode);
            return ScreenPointOf(new Vector2(sido.labelX, sido.labelY));
        }

        public Vector2 ScreenPointOfResidencyButton() => ScreenCenterOf((RectTransform)_residencyButton.transform);
        public Vector2 ScreenPointOfBackButton() => ScreenCenterOf(_backButton);

        /// <summary>지도 바깥의 바다 한 점 (지도 좌표의 왼쪽 아래 구석).</summary>
        public Vector2 ScreenPointOfOpenSea() => ScreenPointOf(new Vector2(40f, 40f));

        private Vector2 ScreenPointOf(Vector2 mapPoint) =>
            RectTransformUtility.WorldToScreenPoint(UiCamera, _viewport.TransformPoint(LocalOf(mapPoint)));

        private Vector2 ScreenCenterOf(RectTransform rect) =>
            RectTransformUtility.WorldToScreenPoint(UiCamera, rect.TransformPoint(rect.rect.center));
    }
}
