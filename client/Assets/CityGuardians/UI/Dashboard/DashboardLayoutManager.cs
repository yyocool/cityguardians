using CityGuardians.Domain;
using CityGuardians.Gameplay;
using CityGuardians.Inputs;
using CityGuardians.Services;
using UnityEngine;
using UnityEngine.UI;

namespace CityGuardians.UI
{
    /// <summary>
    /// 대시보드 카드 세 장을 화면에 놓는다 (docs/HUD_LAYOUT_SYSTEM.md 3장).
    ///
    /// 자리를 정하는 규칙은 DashboardLayout(순수 계산)에 있다. 여기서는 그 계산에 넣을 것을 잰다:
    /// 캔버스의 크기와, 카드를 쌓을 기둥을 다른 HUD(GC 표시, 임무 현황, 미니맵, 무기 칸 …)가 위아래로 얼마나 쓰고 있는지.
    /// 다른 HUD 의 이름을 알지 못해도 되도록 화면에 켜져 있는 칸을 직접 잰다. 새 HUD 가 생겨도 카드가 알아서 비켜 선다.
    /// </summary>
    public sealed class DashboardLayoutManager : MonoBehaviour
    {
        /// <summary>다른 HUD 를 재는 간격. 매 프레임 재지 않는다.</summary>
        private const float MeasureInterval = 0.25f;
        /// <summary>잰 값이 이보다 적게 달라지면 무시한다 (GC 표시가 커졌다 작아지는 것 같은 떨림).</summary>
        private const float ReserveSlack = 6f;
        /// <summary>기둥의 폭 가운데 이만큼 넘게 겹치는 칸만 자리를 차지한 것으로 본다.</summary>
        private const float OverlapRatio = 0.3f;

        public FinanceDashboardCard Finance { get; private set; }
        public CityDashboardCard City { get; private set; }
        public NationalDashboardCard National { get; private set; }
        public DashboardCardBase Card(int index) => _cards[index];

        // 점검과 자동 테스트에서 읽는 값
        public bool Visible => _root.activeSelf;
        public DashboardLayoutResult Layout { get; private set; }
        public float TopReserve { get; private set; }
        public float BottomReserve { get; private set; }
        /// <summary>대시보드를 숨겨 둔 동안 우리 도시에 급한 일이 생기면 뜨는 작은 알림. 없으면 null.</summary>
        public string AlertChipText => _alertChip.activeSelf ? _alertChipText.text : null;

        private Canvas _canvas;
        private RectTransform _hud;
        private GameObject _root;
        private RectTransform _rootRect;
        private readonly DashboardCardBase[] _cards = new DashboardCardBase[DashboardCards.Count];
        private GameObject _alertChip;
        private RectTransform _alertChipRect;
        private Text _alertChipText;

        private readonly DashboardLayoutInput _input = new DashboardLayoutInput();
        private readonly Vector3[] _corners = new Vector3[4];
        private float _measureTimer;
        private int _stateVersion = -1;
        private Vector2 _canvasSize;
        private bool _placedOnce;
        private float _columnX, _columnWidth;
        private float _measuredTop, _measuredBottom;

        public static DashboardLayoutManager Create(Canvas canvas, Transform hudRoot)
        {
            RectTransform rect = UiFactory.Stretch(hudRoot, "Dashboard");
            var manager = rect.gameObject.AddComponent<DashboardLayoutManager>();
            manager.Build(canvas, (RectTransform)hudRoot, rect);
            return manager;
        }

        private void Build(Canvas canvas, RectTransform hud, RectTransform rect)
        {
            _canvas = canvas;
            _hud = hud;
            _root = rect.gameObject;
            _rootRect = rect;
            // 카드가 움직이거나 글자가 바뀌어도 다른 HUD 를 다시 묶지 않도록 제 캔버스에 둔다 (그 반대도 같다).
            _root.AddComponent<Canvas>();

            Finance = DashboardCardBase.Create<FinanceDashboardCard>(rect);
            City = DashboardCardBase.Create<CityDashboardCard>(rect);
            National = DashboardCardBase.Create<NationalDashboardCard>(rect);
            _cards[0] = Finance;
            _cards[1] = City;
            _cards[2] = National;
            for (int i = 0; i < _cards.Length; i++) _input.expandedHeights[i] = _cards[i].ExpandedHeight;

            Image chip = DashboardStyle.RoundedPanel(rect, "AlertChip", new Vector2(0f, 1f), Vector2.zero,
                new Vector2(DashboardLayout.CardWidth, 40f), DashboardStyle.AlertBack);
            _alertChip = chip.gameObject;
            _alertChipRect = chip.rectTransform;
            _alertChipText = DashboardStyle.Line(chip.transform, "Text", new Vector2(0.5f, 0.5f), Vector2.zero,
                new Vector2(DashboardLayout.CardWidth - 24f, 40f), 16, TextAnchor.MiddleCenter, Color.white);
            _alertChip.SetActive(false);
            _root.SetActive(false);
        }

        /// <summary>Hud 가 매 프레임 부른다.</summary>
        public void Refresh(float dt)
        {
            LiveDashboardController dashboard = LiveDashboardController.Instance;
            bool show = dashboard != null && dashboard.Available;
            if (_root.activeSelf != show)
            {
                _root.SetActive(show);
                _placedOnce = false;
                _stateVersion = -1;
            }
            if (!show) return;

            DashboardState state = dashboard.State;
            Vector2 canvasSize = ((RectTransform)_canvas.transform).rect.size;
            bool resized = (canvasSize - _canvasSize).sqrMagnitude > 0.25f;
            bool stateChanged = state.Version != _stateVersion;

            _measureTimer -= dt;
            bool measured = false;
            if (_measureTimer <= 0f || resized || stateChanged)
            {
                _measureTimer = MeasureInterval;
                _canvasSize = canvasSize;
                _input.canvasWidth = canvasSize.x;
                _input.canvasHeight = canvasSize.y;
                _input.uiScalePercent = state.Prefs.uiScalePercent;
                _input.dockLeft = state.DockLeft;
                measured = Measure();
            }

            if (measured || resized || stateChanged || !_placedOnce)
            {
                _stateVersion = state.Version;
                for (int i = 0; i < _cards.Length; i++) _input.states[i] = state.StateOf(DashboardCards.All[i]);
                Layout = DashboardLayout.Compute(_input);
                // 해상도가 바뀌었을 때에는 미끄러지지 않고 곧바로 새 자리에 놓는다.
                bool instant = resized || !_placedOnce;
                for (int i = 0; i < _cards.Length; i++) _cards[i].Place(Layout.boxes[i], Layout.scale, instant);
                _placedOnce = true;
            }

            for (int i = 0; i < _cards.Length; i++) _cards[i].Refresh(dt, dashboard);
            RefreshAlertChip(dashboard);
            HandlePointer(dashboard);
        }

        // ── 다른 HUD 재기 ───────────────────────────────────────

        /// <summary>카드를 쌓을 기둥에서 다른 HUD 가 위아래로 쓰는 높이를 잰다. 값이 달라졌으면 true.</summary>
        private bool Measure()
        {
            DashboardLayout.Column(_input, out _columnX, out _columnWidth);
            _measuredTop = 0f;
            _measuredBottom = 0f;

            // 대시보드보다 먼저 만든 것들만 본다. 그 뒤의 것(지도, 선택 메뉴, 결과, 멈춤)은 화면을 덮는 화면이다.
            int own = _rootRect.GetSiblingIndex();
            for (int i = 0; i < own && i < _hud.childCount; i++)
                if (_hud.GetChild(i) is RectTransform child) Consider(child, 1);

            bool changed = Mathf.Abs(_measuredTop - TopReserve) >= ReserveSlack || Mathf.Abs(_measuredBottom - BottomReserve) >= ReserveSlack
                           || (_measuredTop <= 0f) != (TopReserve <= 0f) || (_measuredBottom <= 0f) != (BottomReserve <= 0f);
            if (!changed) return false;
            TopReserve = _measuredTop;
            BottomReserve = _measuredBottom;
            _input.topReserve = TopReserve;
            _input.bottomReserve = BottomReserve;
            return true;
        }

        private void Consider(RectTransform rect, int depth)
        {
            if (!rect.gameObject.activeSelf) return;
            Image image = rect.GetComponent<Image>();
            if (image != null && image.enabled)
            {
                AddObstacle(rect);
                return;
            }
            // 칸이 아니라 묶음이면 (FPS 화면, 무기 칸, 이름·체력) 그 안의 칸들을 본다.
            if (depth >= 2) return;
            for (int i = 0; i < rect.childCount; i++)
                if (rect.GetChild(i) is RectTransform child) Consider(child, depth + 1);
        }

        private void AddObstacle(RectTransform rect)
        {
            rect.GetWorldCorners(_corners);
            float halfWidth = _canvasSize.x * 0.5f, halfHeight = _canvasSize.y * 0.5f;
            float left = float.MaxValue, right = float.MinValue, top = float.MaxValue, bottom = float.MinValue;
            for (int i = 0; i < 4; i++)
            {
                Vector3 local = _hud.InverseTransformPoint(_corners[i]);
                float x = local.x + halfWidth, y = halfHeight - local.y;
                if (x < left) left = x;
                if (x > right) right = x;
                if (y < top) top = y;
                if (y > bottom) bottom = y;
            }

            // 화면을 덮는 칸(피격 때 붉어지는 막)은 자리를 차지하는 HUD 가 아니다.
            if (right - left > _canvasSize.x * 0.5f && bottom - top > _canvasSize.y * 0.5f) return;
            float overlap = Mathf.Min(right, _columnX + _columnWidth) - Mathf.Max(left, _columnX);
            if (overlap < _columnWidth * OverlapRatio) return;

            if ((top + bottom) * 0.5f < halfHeight) _measuredTop = Mathf.Max(_measuredTop, bottom);
            else _measuredBottom = Mathf.Max(_measuredBottom, _canvasSize.y - top);
        }

        // ── 숨겨 둔 동안의 긴급 알림 ────────────────────────────

        private void RefreshAlertChip(LiveDashboardController dashboard)
        {
            // 전투 중에도, 대시보드를 숨겨 두었어도 우리 도시의 긴급 알림은 볼 수 있어야 한다.
            DashboardNoticeDto alert = dashboard.Notices.CityAlert;
            bool show = !dashboard.State.Visible && alert != null && alert.level == DashboardNoticeLevels.Urgent;
            if (_alertChip.activeSelf != show) _alertChip.SetActive(show);
            if (!show) return;
            _alertChipText.text = "■ " + alert.title + "   " + DashboardCards.ToggleAllKeyName + " 보기";
            _alertChipRect.anchoredPosition = new Vector2(_columnX,
                -Mathf.Max(TopReserve + DashboardLayout.Gap, DashboardLayout.EdgeMargin));
            float scale = _columnWidth / DashboardLayout.CardWidth;
            _alertChipRect.localScale = new Vector3(scale, scale, 1f);
        }

        // ── 마우스 ──────────────────────────────────────────────

        private void HandlePointer(LiveDashboardController dashboard)
        {
            if (!dashboard.PointerMode)
            {
                for (int i = 0; i < _cards.Length; i++) _cards[i].SetHover(false);
                return;
            }

            IPlayerInput input = PlayerInputHub.Current;
            Vector2 point = input.PointerPosition;
            DashboardCardBase over = null;
            for (int i = 0; i < _cards.Length; i++)
            {
                bool hit = over == null && _cards[i].Contains(point);
                _cards[i].SetHover(hit);
                if (hit) over = _cards[i];
            }
            if (over == null) return;

            if (input.PointerPressed)
            {
                // 머리 줄: 접고 편다. 펼친 카드의 몸통: 상세 보기.
                if (over.ShownState != DashboardCardStates.Expanded || over.HeaderContains(point)) dashboard.Toggle(over.CardId);
                else DashboardActions.OpenDetail(over.CardId);
            }
            else if (input.PointerBackPressed)
            {
                dashboard.ToggleHidden(over.CardId);
            }
        }
    }
}
