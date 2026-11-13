using System;
using System.Collections.Generic;
using System.Globalization;

namespace CityGuardians.Domain
{
    // v10.0 실시간 대시보드의 규칙 (docs/LIVE_DASHBOARD.md, docs/HUD_LAYOUT_SYSTEM.md).
    //
    // 게임 화면 오른쪽에 카드 세 장이 세로로 놓인다: 내 계좌, 우리 도시, 전국 현황.
    // 여기에는 Unity 가 필요 없는 규칙만 있다: 카드의 상태, 전투 모드에 따른 자동 접기와 되돌리기, 자리 잡기,
    // 갱신 간격, 숫자를 글자로 바꾸는 법. 화면(UI/Dashboard)은 이 규칙이 정한 대로 그리기만 한다.

    /// <summary>카드 세 장의 이름표.</summary>
    public static class DashboardCards
    {
        public const string Finance = "finance";
        public const string City = "city";
        public const string National = "national";

        /// <summary>위에서 아래로 놓이는 순서.</summary>
        public static readonly string[] All = { Finance, City, National };
        public const int Count = 3;

        /// <summary>
        /// 자리가 모자랄 때 먼저 접히는 순서. 우리 도시 카드는 긴급 알림을 싣고 있어서 맨 나중에 접힌다.
        /// </summary>
        public static readonly string[] CollapseOrder = { National, Finance, City };

        public static int IndexOf(string id)
        {
            for (int i = 0; i < All.Length; i++)
                if (All[i] == id) return i;
            return -1;
        }

        /// <summary>카드 머리에 쓰는 짧은 이름.</summary>
        public static string Title(string id)
        {
            switch (id)
            {
                case Finance: return "내 계좌";
                case City: return "우리 도시";
                default: return "전국 현황";
            }
        }

        /// <summary>상세 화면과 문서에 쓰는 이름.</summary>
        public static string FullTitle(string id)
        {
            switch (id)
            {
                case Finance: return "내 계좌 및 투자현황";
                case City: return "우리 도시 현황";
                default: return "전국 지도 현황";
            }
        }

        /// <summary>이 카드를 접고 펴는 키의 이름 (docs/HUD_LAYOUT_SYSTEM.md 5장).</summary>
        public static string KeyName(string id)
        {
            switch (id)
            {
                case Finance: return "F7";
                case City: return "F8";
                default: return "F9";
            }
        }

        /// <summary>대시보드 전체를 보이고 숨기는 키.</summary>
        public const string ToggleAllKeyName = "F6";
    }

    /// <summary>카드의 상태 (지시서 5장).</summary>
    public static class DashboardCardStates
    {
        public const string Expanded = "expanded";
        public const string Collapsed = "collapsed";
        public const string Hidden = "hidden";

        public static bool IsValid(string state) => state == Expanded || state == Collapsed || state == Hidden;
    }

    /// <summary>대시보드가 자리를 잡는 방식. 전투 모드에서 온다 (지시서 6장).</summary>
    public static class DashboardModes
    {
        /// <summary>방, 도시, 사냥: 이용자가 정한 대로 보인다.</summary>
        public const string Normal = "normal";
        /// <summary>FPS: 세 카드를 접고 시야를 비운다.</summary>
        public const string Fps = "fps";
        /// <summary>타워디펜스(혼자·협동): 전술 미니맵과 웨이브 정보가 먼저다.</summary>
        public const string Defense = "defense";

        /// <param name="combatMode">CombatModeCodes 의 값. 전투 장면이 아니면 null.</param>
        public static string Of(string combatMode)
        {
            switch (combatMode)
            {
                case CombatModeCodes.Fps: return Fps;
                case CombatModeCodes.Defense: return Defense;
                default: return Normal;
            }
        }
    }

    /// <summary>타워디펜스에서 카드를 어디에 어떻게 둘지 (이용자가 고른다).</summary>
    public static class DashboardPlacements
    {
        /// <summary>오른쪽에 접어 둔다 (기본).</summary>
        public const string Collapsed = "collapsed";
        /// <summary>오른쪽에, 평소에 정한 상태 그대로.</summary>
        public const string Right = "right";
        /// <summary>왼쪽으로 옮겨서, 평소에 정한 상태 그대로.</summary>
        public const string Left = "left";

        public static readonly string[] All = { Collapsed, Right, Left };

        public static bool IsValid(string placement) => Array.IndexOf(All, placement) >= 0;

        public static string DisplayName(string placement)
        {
            switch (placement)
            {
                case Right: return "오른쪽에 그대로";
                case Left: return "왼쪽으로 옮기기";
                default: return "오른쪽에 접어 두기";
            }
        }

        public static string Next(string placement)
        {
            int index = Array.IndexOf(All, placement);
            return All[(index + 1 + All.Length) % All.Length];
        }
    }

    /// <summary>관리자가 정한 카드 한 장의 기본값.</summary>
    [Serializable]
    public sealed class DashboardCardDefault
    {
        public string id;
        public bool visible = true;
        public bool expanded = true;
    }

    /// <summary>
    /// 관리자가 정한 값 (요약 응답의 settings). 이용자가 자기 화면에서 바꾼 것은 여기에 없다 (DashboardPrefs).
    /// </summary>
    [Serializable]
    public sealed class DashboardSettings
    {
        public const int MinRefreshSeconds = 5;
        public const int MaxRefreshSeconds = 300;

        /// <summary>데이터 갱신 간격(초).</summary>
        public int refreshSeconds = 20;
        /// <summary>우리 도시의 위험도가 이 값 이상이면 카드에 경고가 뜬다. 판정은 서버가 한다.</summary>
        public int cityAlertRisk = 70;
        /// <summary>전국 긴급 알림 표시 정책. 0 표시 안 함, 1 긴급 단계인 도시, 2 긴급 단계이거나 침공 중인 도시.</summary>
        public int nationalNotice = 1;
        /// <summary>준비되지 않은 금융 항목을 "개발용 샘플"로 보여 줘도 되는가. 아니면 "준비 중"으로 보인다.</summary>
        public bool sampleData;
        public List<DashboardCardDefault> cards = new List<DashboardCardDefault>();

        /// <summary>서버의 기본값과 같은 값 (요약을 받기 전에 쓴다).</summary>
        public static DashboardSettings Default()
        {
            var settings = new DashboardSettings();
            settings.cards.Add(new DashboardCardDefault { id = DashboardCards.Finance, visible = true, expanded = true });
            settings.cards.Add(new DashboardCardDefault { id = DashboardCards.City, visible = true, expanded = true });
            settings.cards.Add(new DashboardCardDefault { id = DashboardCards.National, visible = true, expanded = false });
            return settings;
        }

        public DashboardCardDefault Find(string id)
        {
            for (int i = 0; cards != null && i < cards.Count; i++)
                if (cards[i] != null && cards[i].id == id) return cards[i];
            return null;
        }

        /// <summary>이용자가 아직 손대지 않은 카드의 상태.</summary>
        public string DefaultState(string id)
        {
            DashboardCardDefault card = Find(id);
            if (card == null) return DashboardCardStates.Expanded;
            if (!card.visible) return DashboardCardStates.Hidden;
            return card.expanded ? DashboardCardStates.Expanded : DashboardCardStates.Collapsed;
        }

        /// <summary>범위 안으로 당긴 갱신 간격. 서버가 이상한 값을 보내도 너무 자주 묻지 않는다.</summary>
        public int SafeRefreshSeconds => Math.Max(MinRefreshSeconds, Math.Min(MaxRefreshSeconds, refreshSeconds));
    }

    [Serializable]
    public sealed class DashboardCardPref
    {
        public string id;
        public string state;
    }

    /// <summary>이용자가 자기 화면에서 정한 것. 기기에 저장된다 (DashboardPreferenceService).</summary>
    [Serializable]
    public sealed class DashboardPrefs
    {
        public const int CurrentVersion = 1;
        public const int MinScalePercent = 80;
        public const int MaxScalePercent = 140;
        public const int ScaleStepPercent = 20;

        public int version = CurrentVersion;
        /// <summary>대시보드 전체를 보이는가 (F6).</summary>
        public bool visible = true;
        /// <summary>글자와 카드의 크기(%). 80 ~ 140.</summary>
        public int uiScalePercent = 100;
        /// <summary>타워디펜스에서의 자리 (DashboardPlacements).</summary>
        public string defensePlacement = DashboardPlacements.Collapsed;
        /// <summary>이용자가 손댄 카드만 들어 있다. 없는 카드는 관리자의 기본값을 따른다.</summary>
        public List<DashboardCardPref> cards = new List<DashboardCardPref>();

        /// <summary>이용자가 정한 상태. 손댄 적이 없으면 null.</summary>
        public string StateOf(string id)
        {
            for (int i = 0; i < cards.Count; i++)
                if (cards[i].id == id) return cards[i].state;
            return null;
        }

        public void SetState(string id, string state)
        {
            if (DashboardCards.IndexOf(id) < 0 || !DashboardCardStates.IsValid(state)) return;
            for (int i = 0; i < cards.Count; i++)
            {
                if (cards[i].id != id) continue;
                cards[i].state = state;
                return;
            }
            cards.Add(new DashboardCardPref { id = id, state = state });
        }

        /// <summary>저장된 글을 읽은 뒤에 부른다. 모르는 카드, 모르는 상태, 범위를 벗어난 값을 걸러 낸다.</summary>
        public void Normalize()
        {
            version = CurrentVersion;
            uiScalePercent = ClampScale(uiScalePercent);
            if (!DashboardPlacements.IsValid(defensePlacement)) defensePlacement = DashboardPlacements.Collapsed;
            if (cards == null) cards = new List<DashboardCardPref>();
            var seen = new HashSet<string>();
            for (int i = cards.Count - 1; i >= 0; i--)
            {
                DashboardCardPref card = cards[i];
                if (card == null || DashboardCards.IndexOf(card.id) < 0 || !DashboardCardStates.IsValid(card.state) || !seen.Add(card.id))
                    cards.RemoveAt(i);
            }
        }

        public static int ClampScale(int percent) => Math.Max(MinScalePercent, Math.Min(MaxScalePercent, percent));
    }

    /// <summary>
    /// 카드의 상태를 정하는 곳. 이용자가 정한 것(Prefs), 관리자의 기본값(Settings), 지금의 전투 모드를 합쳐
    /// "지금 화면에 어떻게 보여야 하는가"를 답한다.
    ///
    /// 전투 모드가 카드를 접을 때 이용자가 정한 것은 건드리지 않는다. 그래서 전투가 끝나면 아무 일도 하지 않아도
    /// 이전 상태로 돌아간다. 전투 중에 이용자가 카드를 펴거나 숨긴 것은 그 전투 동안만 지킨다.
    /// 대시보드 전체를 숨기는 것(F6)만은 언제 눌러도 이용자의 설정으로 남는다.
    /// </summary>
    public sealed class DashboardState
    {
        private readonly Dictionary<string, string> _battle = new Dictionary<string, string>();

        public DashboardPrefs Prefs { get; private set; }
        public DashboardSettings Settings { get; private set; }
        public string Mode { get; private set; } = DashboardModes.Normal;
        /// <summary>무엇이든 바뀌면 오른다. 화면이 다시 자리를 잡을 때를 안다.</summary>
        public int Version { get; private set; }
        /// <summary>저장해야 할 변경이 있다.</summary>
        public bool PrefsDirty { get; private set; }

        public DashboardState(DashboardPrefs prefs = null, DashboardSettings settings = null)
        {
            Prefs = prefs ?? new DashboardPrefs();
            Prefs.Normalize();
            Settings = settings ?? DashboardSettings.Default();
        }

        public bool InBattle => Mode != DashboardModes.Normal;
        public bool Visible => Prefs.visible;

        /// <summary>전투 모드와 상관없이, 이용자가 정한 (정하지 않았으면 관리자의 기본) 상태.</summary>
        public string UserState(string id) => Prefs.StateOf(id) ?? Settings.DefaultState(id);

        /// <summary>지금 화면에 보여야 하는 상태.</summary>
        public string StateOf(string id)
        {
            if (!Prefs.visible) return DashboardCardStates.Hidden;
            if (_battle.TryGetValue(id, out string chosen)) return chosen;
            string user = UserState(id);
            if (user == DashboardCardStates.Hidden) return DashboardCardStates.Hidden;
            if (Mode == DashboardModes.Fps) return DashboardCardStates.Collapsed;
            if (Mode == DashboardModes.Defense && Prefs.defensePlacement == DashboardPlacements.Collapsed)
                return DashboardCardStates.Collapsed;
            return user;
        }

        /// <summary>카드를 왼쪽 기둥에 두는가. 타워디펜스에서 이용자가 그렇게 골랐을 때뿐이다.</summary>
        public bool DockLeft => Mode == DashboardModes.Defense && Prefs.defensePlacement == DashboardPlacements.Left;

        /// <summary>관리자의 설정이 새로 왔다.</summary>
        public void ApplySettings(DashboardSettings settings)
        {
            if (settings == null) return;
            Settings = settings;
            Version++;
        }

        /// <summary>전투 모드가 바뀌었다. 전투 중에만 지키던 선택은 버린다 (이전 상태로 돌아간다).</summary>
        public bool SetMode(string mode)
        {
            if (mode != DashboardModes.Fps && mode != DashboardModes.Defense) mode = DashboardModes.Normal;
            if (mode == Mode) return false;
            Mode = mode;
            _battle.Clear();
            Version++;
            return true;
        }

        /// <summary>F6: 대시보드 전체를 보이거나 숨긴다.</summary>
        public void ToggleAll()
        {
            Prefs.visible = !Prefs.visible;
            Changed(true);
        }

        /// <summary>F7~F9: 카드를 접거나 편다. 숨겨져 있었으면 편다. 대시보드가 숨겨져 있었으면 대시보드도 보인다.</summary>
        public void Toggle(string id)
        {
            if (DashboardCards.IndexOf(id) < 0) return;
            string next = StateOf(id) == DashboardCardStates.Expanded ? DashboardCardStates.Collapsed : DashboardCardStates.Expanded;
            bool shown = !Prefs.visible;
            Prefs.visible = true;
            Set(id, next, shown);
        }

        /// <summary>Ctrl+F7~F9: 카드를 숨기거나 다시 보인다.</summary>
        public void ToggleHidden(string id)
        {
            if (DashboardCards.IndexOf(id) < 0) return;
            if (!Prefs.visible)
            {
                Prefs.visible = true;
                Set(id, DashboardCardStates.Expanded, true);
                return;
            }
            bool hidden = StateOf(id) == DashboardCardStates.Hidden;
            string shown = InBattle ? DashboardCardStates.Collapsed : DashboardCardStates.Expanded;
            Set(id, hidden ? shown : DashboardCardStates.Hidden, false);
        }

        private void Set(string id, string state, bool prefsTouched)
        {
            if (InBattle)
            {
                _battle[id] = state;
                Changed(prefsTouched);
                return;
            }
            Prefs.SetState(id, state);
            Changed(true);
        }

        public void SetScale(int percent)
        {
            int clamped = DashboardPrefs.ClampScale(percent);
            if (clamped == Prefs.uiScalePercent) return;
            Prefs.uiScalePercent = clamped;
            Changed(true);
        }

        /// <summary>80 → 100 → 120 → 140 → 80 …</summary>
        public void CycleScale()
        {
            int next = Prefs.uiScalePercent + DashboardPrefs.ScaleStepPercent;
            SetScale(next > DashboardPrefs.MaxScalePercent ? DashboardPrefs.MinScalePercent : next);
        }

        public void CyclePlacement()
        {
            Prefs.defensePlacement = DashboardPlacements.Next(Prefs.defensePlacement);
            _battle.Clear();
            Changed(true);
        }

        /// <summary>이용자가 정한 것을 모두 지우고 관리자의 기본값으로 돌아간다.</summary>
        public void ResetToDefaults()
        {
            Prefs = new DashboardPrefs();
            _battle.Clear();
            Changed(true);
        }

        public void MarkSaved() => PrefsDirty = false;

        private void Changed(bool prefsTouched)
        {
            if (prefsTouched) PrefsDirty = true;
            Version++;
        }
    }

    // ── 자리 잡기 ───────────────────────────────────────────

    public sealed class DashboardLayoutInput
    {
        /// <summary>캔버스의 크기 (기준 단위. 1920×1080 화면에서는 1920×1080).</summary>
        public float canvasWidth = 1920f;
        public float canvasHeight = 1080f;
        public int uiScalePercent = 100;
        public bool dockLeft;
        /// <summary>카드를 놓을 기둥에서, 위쪽의 다른 HUD 가 쓰고 있는 높이 (화면 위에서부터).</summary>
        public float topReserve;
        /// <summary>아래쪽의 다른 HUD 가 쓰고 있는 높이 (화면 아래에서부터).</summary>
        public float bottomReserve;
        /// <summary>카드마다의 상태 (DashboardCards.All 의 순서).</summary>
        public string[] states = { DashboardCardStates.Expanded, DashboardCardStates.Expanded, DashboardCardStates.Expanded };
        /// <summary>펼친 카드의 높이 (배율을 곱하기 전).</summary>
        public float[] expandedHeights = { 240f, 270f, 330f };
    }

    public struct DashboardBox
    {
        /// <summary>왼쪽 위 모서리. 화면 왼쪽 위가 (0, 0) 이고 y 는 아래로 자란다.</summary>
        public float x;
        public float y;
        public float width;
        public float height;
        /// <summary>자리를 잡고 난 뒤의 상태. 자리가 모자라면 펼친 카드가 접혀 있을 수 있다.</summary>
        public string state;
        /// <summary>이용자는 펼쳐 두었는데 자리가 모자라서 접혔다.</summary>
        public bool autoCollapsed;
        /// <summary>한 줄에 세 장을 나란히 놓은 작은 모양이다.</summary>
        public bool compact;

        public bool Shown => state != DashboardCardStates.Hidden;
    }

    public sealed class DashboardLayoutResult
    {
        public readonly DashboardBox[] boxes = new DashboardBox[DashboardCards.Count];
        /// <summary>카드 안의 글자와 그림에 곱하는 배율.</summary>
        public float scale = 1f;
        /// <summary>접어도 자리가 모자라서 세 장을 한 줄에 놓았다.</summary>
        public bool compactRow;
    }

    /// <summary>
    /// 카드 세 장의 자리를 정한다 (docs/HUD_LAYOUT_SYSTEM.md 3장).
    ///
    /// 1. 카드의 폭은 340 × 배율이고, 화면 폭의 24% 를 넘지 않는다 (게임 시야를 가리지 않도록).
    /// 2. 다른 HUD 가 쓰는 자리(위·아래)를 뺀 높이에 위에서부터 쌓는다.
    /// 3. 넘치면 펼친 카드를 전국 → 계좌 → 도시 순으로 접는다.
    /// 4. 다 접어도 넘치면 세 장을 한 줄에 나란히 놓는다 (머리 한 줄의 높이만 쓴다).
    /// </summary>
    public static class DashboardLayout
    {
        public const float CardWidth = 340f;
        public const float HeaderHeight = 44f;
        public const float Gap = 8f;
        public const float Margin = 32f;
        public const float EdgeMargin = 28f;
        public const float CompactGap = 6f;
        public const float MaxWidthRatio = 0.24f;

        public static DashboardLayoutResult Compute(DashboardLayoutInput input)
        {
            var result = new DashboardLayoutResult();
            float scale = ScaleOf(input);
            result.scale = scale;

            Column(input, out float x, out float width);
            float header = HeaderHeight * scale;
            float top = Math.Max(input.topReserve + Gap, EdgeMargin);
            float bottom = Math.Max(input.bottomReserve + Gap, EdgeMargin);
            float available = input.canvasHeight - top - bottom;

            string[] states = new string[DashboardCards.Count];
            bool[] auto = new bool[DashboardCards.Count];
            for (int i = 0; i < states.Length; i++)
                states[i] = DashboardCardStates.IsValid(input.states[i]) ? input.states[i] : DashboardCardStates.Hidden;

            // 넘치면 정해진 순서로 접는다.
            for (int step = 0; step < DashboardCards.CollapseOrder.Length && Total(states, input.expandedHeights, scale) > available; step++)
            {
                int index = DashboardCards.IndexOf(DashboardCards.CollapseOrder[step]);
                if (states[index] != DashboardCardStates.Expanded) continue;
                states[index] = DashboardCardStates.Collapsed;
                auto[index] = true;
            }

            int shown = 0;
            for (int i = 0; i < states.Length; i++)
                if (states[i] != DashboardCardStates.Hidden) shown++;

            if (shown > 1 && Total(states, input.expandedHeights, scale) > available)
            {
                // 한 줄에 나란히. 줄 하나의 높이도 없으면 아래쪽 HUD 바로 위에 붙인다.
                result.compactRow = true;
                float y = Math.Min(top, input.canvasHeight - bottom - header);
                y = Math.Max(EdgeMargin, y);
                float chip = (width - CompactGap * (shown - 1)) / shown;
                float cx = x;
                for (int i = 0; i < states.Length; i++)
                {
                    bool visible = states[i] != DashboardCardStates.Hidden;
                    result.boxes[i] = new DashboardBox
                    {
                        x = cx, y = y, width = visible ? chip : 0f, height = visible ? header : 0f,
                        state = visible ? DashboardCardStates.Collapsed : DashboardCardStates.Hidden,
                        autoCollapsed = auto[i], compact = visible,
                    };
                    if (visible) cx += chip + CompactGap;
                }
                return result;
            }

            float cy = top;
            for (int i = 0; i < states.Length; i++)
            {
                float height = HeightOf(states[i], input.expandedHeights[i], scale);
                result.boxes[i] = new DashboardBox { x = x, y = cy, width = width, height = height, state = states[i], autoCollapsed = auto[i] };
                if (states[i] != DashboardCardStates.Hidden) cy += height + Gap;
            }
            return result;
        }

        /// <summary>이용자가 고른 배율. 다만 카드의 폭이 화면 폭의 24% 를 넘지 않게 줄인다.</summary>
        public static float ScaleOf(DashboardLayoutInput input)
        {
            float scale = DashboardPrefs.ClampScale(input.uiScalePercent) / 100f;
            float maxWidth = input.canvasWidth * MaxWidthRatio;
            return CardWidth * scale > maxWidth ? maxWidth / CardWidth : scale;
        }

        /// <summary>카드를 쌓는 기둥의 왼쪽 끝과 폭. 다른 HUD 가 이 기둥을 얼마나 쓰는지 잴 때에도 쓴다.</summary>
        public static void Column(DashboardLayoutInput input, out float x, out float width)
        {
            width = CardWidth * ScaleOf(input);
            x = input.dockLeft ? Margin : input.canvasWidth - Margin - width;
        }

        private static float HeightOf(string state, float expanded, float scale)
        {
            if (state == DashboardCardStates.Expanded) return expanded * scale;
            return state == DashboardCardStates.Collapsed ? HeaderHeight * scale : 0f;
        }

        private static float Total(string[] states, float[] expanded, float scale)
        {
            float total = 0f;
            int shown = 0;
            for (int i = 0; i < states.Length; i++)
            {
                if (states[i] == DashboardCardStates.Hidden) continue;
                total += HeightOf(states[i], expanded[i], scale);
                shown++;
            }
            return total + Gap * Math.Max(0, shown - 1);
        }

        /// <summary>
        /// 화면 해상도에서 캔버스의 크기를 구한다. HUD 캔버스는 1920×1080 기준이고 폭과 높이를 절반씩 따른다
        /// (UiFactory.CreateCanvas 의 matchWidthOrHeight 0.5).
        /// </summary>
        public static void CanvasSize(int screenWidth, int screenHeight, out float canvasWidth, out float canvasHeight)
        {
            double factor = Math.Pow(2.0, 0.5 * Math.Log(screenWidth / 1920.0, 2.0) + 0.5 * Math.Log(screenHeight / 1080.0, 2.0));
            canvasWidth = (float)(screenWidth / factor);
            canvasHeight = (float)(screenHeight / factor);
        }
    }

    /// <summary>접고 펴는 움직임의 한 걸음. 끝에 가까울수록 느려진다.</summary>
    public static class DashboardEase
    {
        public const float Duration = 0.18f;
        /// <summary>이만큼 가까워지면 도착한 것으로 본다 (그 뒤로는 움직이지 않는다).</summary>
        public const float Epsilon = 0.25f;

        public static float Step(float current, float target, float deltaSeconds)
        {
            float distance = target - current;
            if (Math.Abs(distance) <= Epsilon || deltaSeconds >= Duration * 4f) return target;
            if (deltaSeconds <= 0f) return current;
            // 지수 감쇠: Duration 이 지나면 거리의 95% 를 간다.
            float moved = 1f - (float)Math.Exp(-3f * deltaSeconds / Duration);
            float next = current + distance * moved;
            return Math.Abs(target - next) <= Epsilon ? target : next;
        }
    }

    // ── 데이터 갱신 ─────────────────────────────────────────

    /// <summary>서버와의 연결 상태. 카드 아래에 글자와 모양으로 보인다.</summary>
    public static class DashboardConnection
    {
        public const string Loading = "loading";
        public const string Live = "live";
        public const string Offline = "offline";

        public static string Of(bool hasData, int failures) =>
            failures > 0 ? Offline : hasData ? Live : Loading;
    }

    /// <summary>
    /// 요약을 언제 다시 받을지 정한다 (지시서 7장: 매 프레임 조회하지 않는다).
    ///
    /// - 평소에는 관리자가 정한 간격마다 한 번.
    /// - 금융 거래나 전투 결과처럼 값이 바뀌는 일이 있으면 곧 다시 받는다. 다만 요청 사이를 2초보다 좁히지 않는다.
    /// - 실패하면 간격을 두 배씩 늘린다 (최대 2분). 성공하면 원래 간격으로 돌아온다.
    /// - 요청이 나가 있는 동안에는 또 보내지 않는다.
    /// </summary>
    public sealed class DashboardRefreshPolicy
    {
        public const double EventGapSeconds = 2.0;
        public const double EventDelaySeconds = 0.3;
        public const double MaxBackoffSeconds = 120.0;

        private double _lastRequestAt = double.NegativeInfinity;

        public int IntervalSeconds { get; private set; } = 20;
        public bool Pending { get; private set; }
        public int Failures { get; private set; }
        /// <summary>다음에 요청할 때. 처음에는 곧바로다.</summary>
        public double NextAt { get; private set; }

        public void Configure(int refreshSeconds)
        {
            IntervalSeconds = Math.Max(DashboardSettings.MinRefreshSeconds, Math.Min(DashboardSettings.MaxRefreshSeconds, refreshSeconds));
        }

        public bool ShouldRequest(double now) => !Pending && now >= NextAt;

        public void Started(double now)
        {
            Pending = true;
            _lastRequestAt = now;
        }

        public void Succeeded(double now)
        {
            Pending = false;
            Failures = 0;
            NextAt = now + IntervalSeconds;
        }

        public void Failed(double now)
        {
            Pending = false;
            Failures++;
            double delay = IntervalSeconds * Math.Pow(2.0, Math.Min(Failures - 1, 6));
            NextAt = now + Math.Min(MaxBackoffSeconds, Math.Max(MinRetrySeconds, delay));
        }

        private const double MinRetrySeconds = 5.0;

        /// <summary>값이 바뀌는 일이 있었다. 다음 요청을 앞당긴다.</summary>
        public void Nudge(double now)
        {
            double at = Math.Max(now + EventDelaySeconds, _lastRequestAt + EventGapSeconds);
            if (at < NextAt) NextAt = at;
        }
    }

    // ── 글자 ────────────────────────────────────────────────

    /// <summary>숫자와 시간을 카드에 쓰는 글자로 바꾼다.</summary>
    public static class DashboardFormat
    {
        private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

        /// <summary>"12,340 GC"</summary>
        public static string Gc(long amount) => amount.ToString("N0", Culture) + " GC";

        /// <summary>좁은 자리에 쓰는 수. 1만 미만은 그대로, 그 위는 "1.2만", "3.4억".</summary>
        public static string Compact(long amount)
        {
            long abs = Math.Abs(amount);
            string sign = amount < 0 ? "−" : "";
            if (abs < 10000) return sign + abs.ToString("N0", Culture);
            if (abs < 100000000) return sign + Trim(abs / 10000.0) + "만";
            return sign + Trim(abs / 100000000.0) + "억";
        }

        private static string Trim(double value)
        {
            // 올림하지 않는다: 가진 것보다 많아 보이면 안 된다.
            double floored = Math.Floor(value * 10.0) / 10.0;
            return floored >= 100.0 ? Math.Floor(floored).ToString("N0", Culture) : floored.ToString("0.#", Culture);
        }

        /// <summary>늘었으면 "▲ +1,200 GC", 줄었으면 "▼ −300 GC". 색을 보지 않아도 구분된다.</summary>
        public static string SignedGc(long amount)
        {
            if (amount > 0) return "▲ +" + amount.ToString("N0", Culture) + " GC";
            if (amount < 0) return "▼ −" + Math.Abs(amount).ToString("N0", Culture) + " GC";
            return "0 GC";
        }

        public static string Ago(double seconds)
        {
            if (seconds < 5) return "방금";
            if (seconds < 60) return (int)seconds + "초 전";
            if (seconds < 3600) return (int)(seconds / 60) + "분 전";
            return (int)(seconds / 3600) + "시간 전";
        }

        /// <summary>카드 아래의 연결 상태 줄.</summary>
        /// <param name="lastUpdate">마지막으로 받은 때 (이 기기의 시각). 받은 적이 없으면 null.</param>
        public static string Status(string connection, DateTime? lastUpdate, double secondsSince)
        {
            string at = lastUpdate.HasValue ? lastUpdate.Value.ToString("HH:mm:ss", Culture) : null;
            switch (connection)
            {
                case DashboardConnection.Live:
                    return "● 실시간 · " + at + " 갱신";
                case DashboardConnection.Offline:
                    return at != null
                        ? "▲ 연결 끊김 · 마지막 갱신 " + at + " (" + Ago(secondsSince) + ")"
                        : "▲ 연결 끊김 · 아직 받지 못했어요";
                default:
                    return "… 불러오는 중";
            }
        }

        /// <summary>값들을 막대 높이(0~7)로 바꾼다. 가장 작은 값이 1, 가장 큰 값이 7. 모두 같으면 4.</summary>
        public static int[] Bars(IList<long> values)
        {
            int[] bars = new int[values.Count];
            if (values.Count == 0) return bars;
            long min = values[0], max = values[0];
            for (int i = 1; i < values.Count; i++)
            {
                if (values[i] < min) min = values[i];
                if (values[i] > max) max = values[i];
            }
            for (int i = 0; i < values.Count; i++)
                bars[i] = max == min ? 4 : 1 + (int)Math.Round(6.0 * (values[i] - min) / (max - min));
            return bars;
        }

        private const string BarGlyphs = " ▁▂▃▄▅▆▇";

        /// <summary>글자로 그린 작은 그래프. 예: "▁▁▃▃▇▇▇"</summary>
        public static string Sparkline(IList<long> values)
        {
            int[] bars = Bars(values);
            var chars = new char[bars.Length];
            for (int i = 0; i < bars.Length; i++) chars[i] = BarGlyphs[bars[i]];
            return new string(chars);
        }
    }

    // ── 개발용 샘플 ─────────────────────────────────────────

    public sealed class SampleHolding
    {
        public string name;
        public int quantity;
        public long averagePrice;
        public long price;

        public long Value => quantity * price;
        public long Profit => quantity * (price - averagePrice);
    }

    public sealed class SampleTrade
    {
        public string when;
        public string side;
        public string name;
        public int quantity;
        public long price;
    }

    public sealed class SampleInstallment
    {
        public int number;
        public string due;
        public long principal;
        public long interest;
    }

    /// <summary>
    /// 아직 만들지 않은 금융 기능(가상 주식, 대출)의 자리를 보여 주는 개발용 샘플 (지시서 14장).
    ///
    /// 실제 데이터가 아니다. 서버가 주지 않은 숫자이고, 어느 계정의 것도 아니며, 누구에게나 같다.
    /// 화면에는 언제나 "개발용 샘플"이라는 표시와 함께 나가고, 순자산에는 더하지 않는다.
    /// 회사 이름은 모두 지어낸 것이다.
    /// </summary>
    public static class DashboardSamples
    {
        public const string Tag = "개발용 샘플";
        public const string NotReadyTag = "준비 중";

        public static readonly SampleHolding[] Holdings =
        {
            new SampleHolding { name = "가디언 전자", quantity = 12, averagePrice = 1000, price = 1150 },
            new SampleHolding { name = "새벽 에너지", quantity = 5, averagePrice = 2400, price = 2260 },
            new SampleHolding { name = "배턴 로보틱스", quantity = 8, averagePrice = 750, price = 905 },
        };

        public static readonly SampleTrade[] Trades =
        {
            new SampleTrade { when = "샘플 1일 전", side = "매수", name = "배턴 로보틱스", quantity = 3, price = 880 },
            new SampleTrade { when = "샘플 3일 전", side = "매도", name = "가디언 전자", quantity = 2, price = 1120 },
            new SampleTrade { when = "샘플 6일 전", side = "매수", name = "새벽 에너지", quantity = 5, price = 2400 },
        };

        public static readonly SampleInstallment[] Installments =
        {
            new SampleInstallment { number = 1, due = "샘플 7일 뒤", principal = 1000, interest = 60 },
            new SampleInstallment { number = 2, due = "샘플 14일 뒤", principal = 1000, interest = 40 },
            new SampleInstallment { number = 3, due = "샘플 21일 뒤", principal = 1000, interest = 20 },
        };

        public static long StockValue
        {
            get
            {
                long total = 0;
                for (int i = 0; i < Holdings.Length; i++) total += Holdings[i].Value;
                return total;
            }
        }

        public static long StockProfit
        {
            get
            {
                long total = 0;
                for (int i = 0; i < Holdings.Length; i++) total += Holdings[i].Profit;
                return total;
            }
        }

        public static long LoanBalance
        {
            get
            {
                long total = 0;
                for (int i = 0; i < Installments.Length; i++) total += Installments[i].principal;
                return total;
            }
        }
    }
}
