using System.Collections.Generic;
using CityGuardians.Domain;
using CityGuardians.Inputs;
using CityGuardians.Services;
using UnityEngine;

namespace CityGuardians.Gameplay
{
    /// <summary>
    /// 실시간 대시보드의 지휘자 (docs/LIVE_DASHBOARD.md 3장). 장면이 바뀌어도 남아 있는 GameRoot 에 붙는다.
    ///
    /// 하는 일은 넷이다: 접속한 사람이 바뀌면 데이터 서비스를 새로 잇고, 전투 모드를 카드의 상태에 알리고,
    /// 키 입력을 카드의 상태로 옮기고, 이용자가 고친 설정을 저장한다.
    /// 화면은 UI 계층(DashboardLayoutManager 와 카드들)이 이 객체의 값을 읽어서 그린다. 여기서는 그리지 않는다.
    /// </summary>
    public sealed class LiveDashboardController : MonoBehaviour
    {
        public static LiveDashboardController Instance { get; private set; }

        /// <summary>대시보드는 GC 표시와 함께 나타난다 (첫 보상을 받은 뒤).</summary>
        public const int MinTutorialStep = 4;
        /// <summary>설정을 고친 뒤 이만큼 조용하면 저장한다. 키를 연달아 눌러도 파일은 한 번 쓴다.</summary>
        private const float SaveDelay = 0.6f;
        /// <summary>요약 한 번에 띄우는 한 줄 알림의 수. 나머지는 카드에서 본다.</summary>
        private const int ToastsPerUpdate = 2;

        public DashboardState State { get; private set; } = new DashboardState();
        public DashboardDataService Data { get; private set; }
        public DashboardNotificationService Notices { get; private set; } = new DashboardNotificationService();
        public DashboardSummaryDto Summary => Data != null ? Data.Summary : null;

        /// <summary>대시보드를 보여 줄 수 있는가 (접속했고, 첫 보상을 받았다).</summary>
        public bool Available { get; private set; }
        /// <summary>Alt 를 누르고 있어서 커서가 풀려 있는가. 그동안 카드를 마우스로 누를 수 있다.</summary>
        public bool PointerMode { get; private set; }
        /// <summary>카드 아래에 적는 연결 상태 한 줄.</summary>
        public string StatusLine => Data != null ? Data.StatusLine() : "";
        /// <summary>샘플 표시를 허용하는가 (관리자 설정). 서버에 아직 없는 금융 항목에만 쓰인다.</summary>
        public bool SamplesAllowed => State.Settings.sampleData;

        private GameSession _session;
        private DashboardPreferenceService _prefs;
        private float _saveAt = -1f;
        private int _savedVersion;
        private long _gc, _bankGc, _termGc;
        private string _regionCode;

        private void Awake()
        {
            Instance = this;
        }

        private void OnDestroy()
        {
            Unbind();
            if (Instance == this) Instance = null;
        }

        private void OnApplicationQuit() => SaveNow();

        // ── 접속한 사람에 맞추기 ────────────────────────────────

        private void Bind(GameSession session)
        {
            Unbind();
            _session = session;
            Notices = new DashboardNotificationService();
            if (session == null)
            {
                State = new DashboardState();
                return;
            }

            _prefs = new DashboardPreferenceService(new FileTextStore(GameRoot.StorageDirectory));
            State = new DashboardState(_prefs.Load());
            _savedVersion = State.Version;
            Data = new DashboardDataService(session.Backend, GameRoot.ClockOverride);
            Data.Updated += OnSummary;
            session.Changed += OnSessionChanged;
            session.CityEffectApplied += OnCityEffect;
            RememberFinance(session.Me);
        }

        private void Unbind()
        {
            SaveNow();
            if (Data != null)
            {
                Data.Updated -= OnSummary;
                Data.Stop();
                Data = null;
            }
            if (_session != null)
            {
                _session.Changed -= OnSessionChanged;
                _session.CityEffectApplied -= OnCityEffect;
                _session = null;
            }
            Available = false;
            PointerMode = false;
        }

        private void RememberFinance(MeDto me)
        {
            _gc = me.gc;
            _bankGc = me.bankGc;
            _termGc = me.termGc;
            _regionCode = me.regionCode;
        }

        /// <summary>
        /// 내 정보가 바뀌었다. 돈이나 소속 도시가 달라졌을 때만 요약을 앞당겨 받는다
        /// (경험치처럼 카드와 상관없는 변화로는 서버에 묻지 않는다). 카드의 값을 여기서 고쳐 쓰지는 않는다.
        /// </summary>
        private void OnSessionChanged()
        {
            if (_session == null || Data == null) return;
            MeDto me = _session.Me;
            if (me.gc == _gc && me.bankGc == _bankGc && me.termGc == _termGc && me.regionCode == _regionCode) return;
            RememberFinance(me);
            Data.Nudge();
        }

        private void OnCityEffect(CityEffectDto effect)
        {
            if (Data != null) Data.Nudge();
        }

        private void OnSummary(DashboardSummaryDto summary)
        {
            if (summary == null) return;
            if (summary.settings != null) State.ApplySettings(summary.settings);

            // 전국 지도의 색은 기존 도시 위험도 서비스의 값을 쓴다. 오래됐으면 그쪽이 스스로 다시 받는다.
            CityRiskService.RefreshIfStale();

            IReadOnlyList<DashboardNoticeDto> fresh = Notices.Accept(summary);
            int shown = 0;
            // 우리 도시의 알림이 먼저다.
            for (int pass = 0; pass < 2 && shown < ToastsPerUpdate; pass++)
            {
                for (int i = 0; i < fresh.Count && shown < ToastsPerUpdate; i++)
                {
                    DashboardNoticeDto notice = fresh[i];
                    if (notice.IsCity != (pass == 0)) continue;
                    if (!DashboardNotificationService.ShouldToast(notice, State.InBattle)) continue;
                    GameEvents.ShowToast(notice.title);
                    shown++;
                }
            }
        }

        // ── 매 프레임 ───────────────────────────────────────────

        private void Update()
        {
            GameSession session = GameSession.Current;
            if (session != _session) Bind(session);
            if (session == null) return;

            GameRoot root = GameRoot.Instance;
            Available = !root.BootHasFailed && (session.TutorialStep >= MinTutorialStep || session.Me.gc > 0);
            if (!Available)
            {
                PointerMode = false;
                return;
            }

            // 전투 모드: 들어가면 접고, 끝나면 되돌린다. 바뀌는 순간에 요약도 다시 받는다.
            if (State.SetMode(DashboardModes.Of(CombatService.ActiveMode))) Data.Nudge();

            // 서버에는 정해진 간격으로만 묻는다 (DashboardRefreshPolicy). 매 프레임 부르지만 대부분 아무 일도 하지 않는다.
            Data.Tick(Time.unscaledTimeAsDouble);

            IPlayerInput input = PlayerInputHub.Current;
            bool free = !root.IsBusy && !GameState.IsPaused && !MapController.Instance.IsOpen
                        && !GameState.IsLockedBy(GameState.LockTacticalMap);
            PointerMode = free && State.Visible && input.DashboardPointerHeld;
            if (free) HandleKeys(input);

            if (State.PrefsDirty && State.Version != _savedVersion)
            {
                _savedVersion = State.Version;
                _saveAt = Time.unscaledTime + SaveDelay;
            }
            if (_saveAt >= 0f && Time.unscaledTime >= _saveAt) SaveNow();
        }

        private void HandleKeys(IPlayerInput input)
        {
            bool toggle = input.DashboardTogglePressed;
            int card = input.DashboardCardPressed;
            if (!toggle && card == 0) return;

            if (toggle)
            {
                if (input.DashboardDetailHeld) DashboardActions.OpenSettings();
                else ToggleAll();
                return;
            }
            if (card < 1 || card > DashboardCards.Count) return;
            string id = DashboardCards.All[card - 1];
            if (input.DashboardDetailHeld) DashboardActions.OpenDetail(id);
            else if (input.DashboardHideHeld) ToggleHidden(id);
            else Toggle(id);
        }

        // ── 카드 조작 (키와 마우스가 같은 길로 온다) ────────────

        public void ToggleAll()
        {
            State.ToggleAll();
            Sfx.Play(Sfx.Id.Interact);
            if (!State.Visible) GameEvents.ShowToast($"대시보드를 숨겼어요.  {DashboardCards.ToggleAllKeyName}  다시 보기");
        }

        public void Toggle(string cardId)
        {
            State.Toggle(cardId);
            Sfx.Play(Sfx.Id.Interact);
        }

        public void ToggleHidden(string cardId)
        {
            State.ToggleHidden(cardId);
            Sfx.Play(Sfx.Id.Interact);
            if (State.StateOf(cardId) == DashboardCardStates.Hidden)
                GameEvents.ShowToast($"{DashboardCards.Title(cardId)} 카드를 숨겼어요.  {DashboardCards.KeyName(cardId)}  다시 보기");
        }

        /// <summary>설정 메뉴에서 값을 바꾼 뒤 부른다. 다음 프레임을 기다리지 않고 저장한다.</summary>
        public void SaveNow()
        {
            _saveAt = -1f;
            if (_prefs == null || State == null || !State.PrefsDirty) return;
            if (_prefs.Save(State.Prefs)) State.MarkSaved();
            else Debug.LogWarning("대시보드 설정을 저장하지 못했어요. 이번 실행 동안에는 그대로 쓰입니다.");
            _savedVersion = State.Version;
        }
    }
}
