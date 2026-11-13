using System.Collections.Generic;
using CityGuardians.Gameplay;
using CityGuardians.Services;
using UnityEngine;
using UnityEngine.UI;

namespace CityGuardians.UI
{
    /// <summary>
    /// 화면 UI 전체 (docs/USER_FLOW.md 7장). 게임플레이가 내는 GameEvents와 GameSession의 값을 받아 그린다.
    /// 요소는 필요해지는 순간에 하나씩 나타난다: 체력은 첫 전투, GC는 첫 보상.
    /// 조작은 모두 키보드라서 누를 수 있는 버튼이 없다.
    /// </summary>
    public sealed class Hud : MonoBehaviour
    {
        public static Hud Instance { get; private set; }

        public Canvas Canvas { get; private set; }
        public ChoicePanel Choice { get; private set; }
        public DefenseHud Defense { get; private set; }
        public MapPanel Map { get; private set; }
        public Hotbar Hotbar { get; private set; }
        public string NameText => _nameText.text;
        /// <summary>소속 도시 줄. 이방인이면 null.</summary>
        public string CityText => _cityText.gameObject.activeSelf ? _cityText.text : null;

        // 점검과 자동 테스트에서 읽는 값
        public string ObjectiveText => _objectiveText.text;
        public bool ObjectiveVisible => _objective.activeSelf;
        public string PromptText => _prompt.activeSelf ? _promptText.text : null;
        public string KeyHintText => _keyHint.activeSelf ? _keyHintText.text : null;
        public bool DialogueVisible => _dialogue.activeSelf;
        public string DialogueBody => _dialogueText.text;
        public bool PanelVisible => _panel.activeSelf;
        public string PanelTitle => _panelTitle.text;
        public string PanelBody => _panelBody.text;
        /// <summary>안내판 본문이 제 칸 안에 들어가는가 (가장 긴 줄의 너비와 전체 높이).</summary>
        public bool PanelBodyFits
        {
            get
            {
                Rect rect = _panelBody.rectTransform.rect;
                return _panelBody.preferredWidth <= rect.width + 0.5f && _panelBody.preferredHeight <= rect.height + 0.5f;
            }
        }
        public int PanelBodyFontSize => _panelBody.fontSize;
        private const int PanelBodyMaxSize = 36;
        private const int PanelBodyMinSize = 22;
        public bool StatusVisible => _status.activeSelf;
        public bool WalletVisible => _wallet.activeSelf;
        public string WalletText => _walletText.text;
        public bool BootFailVisible => _bootFail.activeSelf;
        public string LastToast { get; private set; }

        private GameObject _status;
        private Text _nameText;
        private Text _cityText;
        private Image _cityChip;
        private RectTransform _hpFill;
        private Text _hpText;
        private RectTransform _xpFill;

        private GameObject _wallet;
        private Text _walletText;
        private float _shownGc = -1f;
        private float _walletPulse;

        private GameObject _objective;
        private Text _objectiveText;
        private GameObject _prompt;
        private Text _promptText;
        private GameObject _keyHint;
        private Text _keyHintText;

        private GameObject _dialogue;
        private Text _dialogueSpeaker;
        private Text _dialogueText;

        private GameObject _panel;
        private Image _panelBack;
        private Text _panelTitle;
        private Text _panelBody;

        private GameObject _toast;
        private CanvasGroup _toastGroup;
        private Text _toastText;
        private const float ToastHold = 3.2f;
        private const float ToastShortHold = 1.3f;
        private const float ToastShortestHold = 0.8f;
        private const float ToastFadeTime = 0.25f;
        private readonly Queue<string> _toastQueue = new Queue<string>();
        private string _lastQueuedToast;
        private float _toastTime = -1f;

        private Image _hurt;
        private float _hurtAlpha;
        private Image _fade;
        private float _fadeFrom;
        private float _fadeTo;
        private float _fadeDuration;
        private float _fadeTime;

        private GameObject _pause;
        private GameObject _bootFail;
        private Text _bootFailMessage;
        private Text _bootFailKeys;

        private int _hp;
        private int _hpMax = 1;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Initialize()
        {
            var go = new GameObject("Hud");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<Hud>();
        }

        private void Awake()
        {
            Canvas = UiFactory.CreateCanvas("HudCanvas", 100);
            Canvas.transform.SetParent(transform, false);
            Build(Canvas.transform);
            Subscribe();
        }

        private void OnDestroy()
        {
            Unsubscribe();
        }

        // ── 조립 ────────────────────────────────────────────────

        private void Build(Transform root)
        {
            // 피격 때 화면 가장자리가 붉어진다
            _hurt = UiFactory.Fill(UiFactory.Stretch(root, "Hurt"), new Color(1f, 0.1f, 0.1f, 0f));

            // 왼쪽 위: 이름, 레벨, 체력, 경험치
            RectTransform status = UiFactory.Rect(root, "Status", new Vector2(0f, 1f), new Vector2(32f, -28f), new Vector2(400f, 136f));
            _status = status.gameObject;
            _nameText = UiFactory.LabelAt(status, "Name", new Vector2(0f, 1f), Vector2.zero, new Vector2(400f, 38f),
                26, TextAnchor.MiddleLeft, UiFactory.TextColor, true);
            _nameText.horizontalOverflow = HorizontalWrapMode.Overflow;
            // 주민이면 경험치 막대 아래에 소속 도시가 완장 색과 함께 보인다.
            _cityChip = UiFactory.Panel(status, "CityChip", new Vector2(0f, 1f), new Vector2(2f, -110f), new Vector2(16f, 16f), Color.white);
            _cityText = UiFactory.LabelAt(status, "City", new Vector2(0f, 1f), new Vector2(28f, -102f), new Vector2(372f, 32f),
                22, TextAnchor.MiddleLeft, UiFactory.TextColor, true);
            _cityText.horizontalOverflow = HorizontalWrapMode.Overflow;
            _cityText.gameObject.SetActive(false);
            _cityChip.gameObject.SetActive(false);
            _hpFill = UiFactory.Bar(status, "Hp", new Vector2(0f, 1f), new Vector2(0f, -42f), new Vector2(380f, 34f),
                new Color(0.10f, 0.10f, 0.14f, 0.9f), new Color(0.36f, 0.86f, 0.46f));
            _hpText = UiFactory.Label(_hpFill.parent.parent, "HpText", 20, TextAnchor.MiddleCenter, Color.white, 0f, true);
            _xpFill = UiFactory.Bar(status, "Xp", new Vector2(0f, 1f), new Vector2(0f, -80f), new Vector2(380f, 16f),
                new Color(0.10f, 0.10f, 0.14f, 0.9f), new Color(0.42f, 0.78f, 1f));

            // 오른쪽 위: GC
            Image wallet = UiFactory.Panel(root, "Wallet", new Vector2(1f, 1f), new Vector2(-32f, -28f), new Vector2(280f, 60f), UiFactory.PanelColor);
            _wallet = wallet.gameObject;
            UiFactory.Panel(wallet.transform, "Coin", new Vector2(0f, 0.5f), new Vector2(16f, 0f), new Vector2(30f, 30f), UiFactory.Accent);
            _walletText = UiFactory.Label(wallet.transform, "Amount", 30, TextAnchor.MiddleRight, UiFactory.Accent, 18f);

            // 오른쪽 위, GC 아래: 방어전 상태 (방어전 중에만 보인다)
            Defense = gameObject.AddComponent<DefenseHud>();
            Defense.BuildStatus(root);

            // 가운데 위: 지금 할 일 한 줄
            Image objective = UiFactory.Panel(root, "Objective", new Vector2(0.5f, 1f), new Vector2(0f, -28f), new Vector2(900f, 60f), UiFactory.PanelColor);
            _objective = objective.gameObject;
            UiFactory.Panel(objective.transform, "Mark", new Vector2(0f, 0.5f), new Vector2(0f, 0f), new Vector2(8f, 60f), UiFactory.Accent);
            _objectiveText = UiFactory.Label(objective.transform, "Text", 28, TextAnchor.MiddleCenter, UiFactory.TextColor, 20f);

            // 알림
            Image toast = UiFactory.Panel(root, "Toast", new Vector2(0.5f, 1f), new Vector2(0f, -104f), new Vector2(900f, 56f), new Color(0.16f, 0.30f, 0.24f, 0.92f));
            _toast = toast.gameObject;
            _toastGroup = _toast.AddComponent<CanvasGroup>();
            _toastText = UiFactory.Label(toast.transform, "Text", 26, TextAnchor.MiddleCenter, UiFactory.TextColor, 20f);

            // 왼쪽 아래: 조작 키 안내
            Image keyHint = UiFactory.Panel(root, "KeyHint", new Vector2(0f, 0f), new Vector2(32f, 28f), new Vector2(900f, 50f), new Color(0.08f, 0.10f, 0.16f, 0.6f));
            _keyHint = keyHint.gameObject;
            _keyHintText = UiFactory.Label(keyHint.transform, "Text", 24, TextAnchor.MiddleLeft, UiFactory.TextColor, 18f);

            // 가운데 아래: 상호작용 안내
            Image prompt = UiFactory.Panel(root, "Prompt", new Vector2(0.5f, 0f), new Vector2(0f, 250f), new Vector2(440f, 64f), UiFactory.PanelColor);
            _prompt = prompt.gameObject;
            _promptText = UiFactory.Label(prompt.transform, "Text", 30, TextAnchor.MiddleCenter, UiFactory.Accent, 12f);

            // 대화창
            Image dialogue = UiFactory.Panel(root, "Dialogue", new Vector2(0.5f, 0f), new Vector2(0f, 40f), new Vector2(1300f, 220f), new Color(0.06f, 0.08f, 0.14f, 0.92f));
            _dialogue = dialogue.gameObject;
            Image speakerTag = UiFactory.Panel(dialogue.transform, "SpeakerTag", new Vector2(0f, 1f), new Vector2(28f, 26f), new Vector2(280f, 52f), new Color(0.20f, 0.60f, 0.52f, 1f));
            _dialogueSpeaker = UiFactory.Label(speakerTag.transform, "Speaker", 28, TextAnchor.MiddleCenter, Color.white, 8f);
            _dialogueText = UiFactory.LabelAt(dialogue.transform, "Text", new Vector2(0.5f, 0.5f), new Vector2(0f, 6f), new Vector2(1220f, 140f),
                34, TextAnchor.MiddleLeft, UiFactory.TextColor);
            UiFactory.LabelAt(dialogue.transform, "Next", new Vector2(1f, 0f), new Vector2(-28f, 14f), new Vector2(300f, 36f),
                24, TextAnchor.MiddleRight, UiFactory.Accent).text = "E  다음";

            // 안내판 (컴퓨터 화면)
            Image panel = UiFactory.Panel(root, "Panel", new Vector2(0.5f, 0.5f), new Vector2(0f, 20f), new Vector2(1000f, 560f), new Color(0.06f, 0.10f, 0.18f, 0.96f));
            _panel = panel.gameObject;
            _panelBack = panel;
            _panelTitle = UiFactory.LabelAt(panel.transform, "Title", new Vector2(0.5f, 1f), new Vector2(0f, -36f), new Vector2(920f, 90f),
                60, TextAnchor.MiddleCenter, Color.white);
            _panelBody = UiFactory.LabelAt(panel.transform, "Body", new Vector2(0.5f, 0.5f), new Vector2(0f, -30f), new Vector2(920f, 320f),
                36, TextAnchor.MiddleCenter, UiFactory.TextColor);
            // 안내판의 글은 줄을 스스로 나눠서 온다. 여기서 접지 않고, 넘치면 글자를 줄인다 (FitPanelBody).
            _panelBody.horizontalOverflow = HorizontalWrapMode.Overflow;
            UiFactory.LabelAt(panel.transform, "Close", new Vector2(0.5f, 0f), new Vector2(0f, 24f), new Vector2(400f, 44f),
                28, TextAnchor.MiddleCenter, UiFactory.Accent).text = "E  닫기";

            // 오른쪽 아래: 무기 칸과 수리 키트
            Hotbar = Hotbar.Create(root);

            // 지도: 걸어 다니는 화면의 요소들을 모두 덮는다. 알림은 지도 위에서도 보여야 하므로 그 위로 올린다.
            Map = MapPanel.Create(Canvas, root);
            _toast.transform.SetAsLastSibling();

            // 선택 메뉴와 방어전 결과
            Choice = ChoicePanel.Create(root);
            Defense.BuildResult(root);

            // 잠시 멈춤
            Image pause = UiFactory.Fill(UiFactory.Stretch(root, "Pause"), new Color(0f, 0f, 0f, 0.62f));
            _pause = pause.gameObject;
            UiFactory.LabelAt(pause.transform, "Title", new Vector2(0.5f, 0.5f), new Vector2(0f, 60f), new Vector2(1000f, 120f),
                80, TextAnchor.MiddleCenter, Color.white).text = "잠시 멈춤";
            UiFactory.LabelAt(pause.transform, "Keys", new Vector2(0.5f, 0.5f), new Vector2(0f, -60f), new Vector2(1200f, 60f),
                34, TextAnchor.MiddleCenter, UiFactory.Accent).text = "Esc  계속하기          Q  게임 끝내기";
            UiFactory.LabelAt(pause.transform, "Note", new Vector2(0.5f, 0.5f), new Vector2(0f, -130f), new Vector2(1200f, 50f),
                24, TextAnchor.MiddleCenter, UiFactory.TextColor).text = "진행 상황은 자동으로 저장돼요.";

            // 장면 전환용 검은 막
            _fade = UiFactory.Fill(UiFactory.Stretch(root, "Fade"), new Color(0f, 0f, 0f, 1f));
            _fadeFrom = _fadeTo = 1f;

            // 접속 실패 안내 (검은 막 위)
            Image bootFail = UiFactory.Fill(UiFactory.Stretch(root, "BootFail"), new Color(0.05f, 0.06f, 0.10f, 1f));
            _bootFail = bootFail.gameObject;
            UiFactory.LabelAt(bootFail.transform, "Title", new Vector2(0.5f, 0.5f), new Vector2(0f, 150f), new Vector2(1400f, 100f),
                64, TextAnchor.MiddleCenter, Color.white).text = "게임을 시작할 수 없어요";
            _bootFailMessage = UiFactory.LabelAt(bootFail.transform, "Message", new Vector2(0.5f, 0.5f), new Vector2(0f, 20f), new Vector2(1400f, 140f),
                32, TextAnchor.MiddleCenter, UiFactory.TextColor);
            _bootFailKeys = UiFactory.LabelAt(bootFail.transform, "Keys", new Vector2(0.5f, 0.5f), new Vector2(0f, -130f), new Vector2(1500f, 110f),
                32, TextAnchor.MiddleCenter, UiFactory.Accent);

            _status.SetActive(false);
            _wallet.SetActive(false);
            _objective.SetActive(false);
            _toast.SetActive(false);
            _keyHint.SetActive(false);
            _prompt.SetActive(false);
            _dialogue.SetActive(false);
            _panel.SetActive(false);
            _pause.SetActive(false);
            _bootFail.SetActive(false);
        }

        // ── 이벤트 ──────────────────────────────────────────────

        private void Subscribe()
        {
            GameEvents.ObjectiveChanged += OnObjective;
            GameEvents.PromptChanged += OnPrompt;
            GameEvents.Toast += OnToast;
            GameEvents.KeyHintChanged += OnKeyHint;
            GameEvents.DialogueLine += OnDialogueLine;
            GameEvents.DialogueClosed += OnDialogueClosed;
            GameEvents.PanelOpened += OnPanelOpened;
            GameEvents.PanelClosed += OnPanelClosed;
            GameEvents.Fade += OnFade;
            GameEvents.PauseChanged += OnPause;
            GameEvents.PlayerHealthChanged += OnHealth;
            GameEvents.PlayerHurt += OnHurt;
            GameEvents.BootFailed += OnBootFailed;
        }

        private void Unsubscribe()
        {
            GameEvents.ObjectiveChanged -= OnObjective;
            GameEvents.PromptChanged -= OnPrompt;
            GameEvents.Toast -= OnToast;
            GameEvents.KeyHintChanged -= OnKeyHint;
            GameEvents.DialogueLine -= OnDialogueLine;
            GameEvents.DialogueClosed -= OnDialogueClosed;
            GameEvents.PanelOpened -= OnPanelOpened;
            GameEvents.PanelClosed -= OnPanelClosed;
            GameEvents.Fade -= OnFade;
            GameEvents.PauseChanged -= OnPause;
            GameEvents.PlayerHealthChanged -= OnHealth;
            GameEvents.PlayerHurt -= OnHurt;
            GameEvents.BootFailed -= OnBootFailed;
        }

        private void OnObjective(string text)
        {
            _objective.SetActive(!string.IsNullOrEmpty(text));
            _objectiveText.text = text ?? "";
        }

        private void OnPrompt(string text)
        {
            _prompt.SetActive(!string.IsNullOrEmpty(text));
            _promptText.text = text ?? "";
        }

        private void OnKeyHint(string text)
        {
            _keyHint.SetActive(!string.IsNullOrEmpty(text));
            _keyHintText.text = text ?? "";
            if (!string.IsNullOrEmpty(text))
            {
                var rect = (RectTransform)_keyHint.transform;
                // 글자 양쪽 여백(18 × 2)보다 넉넉히 잡고, 폭 계산이 조금 어긋나도 줄이 꺾이지 않게 한다.
                _keyHintText.horizontalOverflow = HorizontalWrapMode.Overflow;
                rect.sizeDelta = new Vector2(_keyHintText.preferredWidth + 64f, rect.sizeDelta.y);
            }
        }

        private void OnToast(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            LastToast = text;

            // 같은 알림이 연달아 오면 (수리 키트를 여러 개 살 때) 줄을 세우지 않는다.
            if (_toastQueue.Count == 0 && _toastTime >= 0f && _toastText.text == text)
            {
                // 지금 보이는 것과 같다: 보이는 시간만 다시 센다.
                _toastTime = Mathf.Min(_toastTime, ToastFadeTime);
                return;
            }
            if (_lastQueuedToast == text && _toastQueue.Count > 0) return;

            _toastQueue.Enqueue(text);
            _lastQueuedToast = text;
        }

        private void OnDialogueLine(string speaker, string text)
        {
            _dialogue.SetActive(true);
            _dialogueSpeaker.text = speaker ?? "";
            _dialogueText.text = text ?? "";
        }

        private void OnDialogueClosed() => _dialogue.SetActive(false);

        private void OnPanelOpened(string title, string body, bool alert)
        {
            _panel.SetActive(true);
            _panelTitle.text = title ?? "";
            _panelBody.text = body ?? "";
            FitPanelBody();
            _panelBack.color = alert ? new Color(0.30f, 0.05f, 0.05f, 0.96f) : new Color(0.06f, 0.10f, 0.18f, 0.96f);
            _panelTitle.color = alert ? new Color(1f, 0.85f, 0.3f) : Color.white;
        }

        /// <summary>
        /// 가장 긴 줄과 전체 높이가 칸 안에 들어갈 때까지 글자를 줄인다.
        /// 줄이 접히거나 넘치면 제목과 닫기 안내를 덮는다 (거래 내역처럼 줄이 길고 많은 글).
        /// </summary>
        private void FitPanelBody()
        {
            for (int size = PanelBodyMaxSize; size >= PanelBodyMinSize; size -= 2)
            {
                _panelBody.fontSize = size;
                if (PanelBodyFits) break;
            }
        }

        private void OnPanelClosed() => _panel.SetActive(false);

        private void OnFade(bool toBlack, float seconds)
        {
            _fadeFrom = _fade.color.a;
            _fadeTo = toBlack ? 1f : 0f;
            _fadeDuration = seconds;
            _fadeTime = 0f;
            if (seconds <= 0f) SetFadeAlpha(_fadeTo);

            // 화면이 어두워지면 다른 곳으로 간다. 지난 곳의 알림이 새 곳까지 따라와 새 알림을 밀어내지 않게 한다.
            if (toBlack)
            {
                _toastQueue.Clear();
                _lastQueuedToast = null;
                _toastTime = -1f;
                _toast.SetActive(false);
            }
        }

        private void OnPause(bool paused) => _pause.SetActive(paused);

        private void OnHealth(int current, int max)
        {
            _hp = current;
            _hpMax = Mathf.Max(1, max);
        }

        private void OnHurt() => _hurtAlpha = 0.35f;

        private void OnBootFailed(string code, string message)
        {
            if (code == null)
            {
                _bootFail.SetActive(false);
                return;
            }
            _bootFail.SetActive(true);
            _bootFailMessage.text = string.IsNullOrEmpty(message) ? "알 수 없는 문제가 생겼어요." : message;
            bool canStartOver = code == ApiErrors.GuestDataCorrupt || code == ApiErrors.Unauthorized;
            _bootFailKeys.text = canStartOver
                ? "R  다시 시도\nN  새로 시작하기 (기존 정보는 지우지 않고 따로 보관해요)"
                : "R  다시 시도";
        }

        // ── 매 프레임 ───────────────────────────────────────────

        private void Update()
        {
            float dt = Time.unscaledDeltaTime;
            UpdateSession(dt);
            UpdateToast(dt);
            UpdateFade(dt);
            Map.Refresh(dt);
            Hotbar.Refresh();
            Choice.Refresh();
            Defense.Refresh(dt);

            if (_hurtAlpha > 0f)
            {
                _hurtAlpha = Mathf.MoveTowards(_hurtAlpha, 0f, dt * 0.9f);
                _hurt.color = new Color(1f, 0.1f, 0.1f, _hurtAlpha);
            }
        }

        private void UpdateSession(float dt)
        {
            GameSession session = GameSession.Current;
            if (session == null)
            {
                _status.SetActive(false);
                _wallet.SetActive(false);
                _shownGc = -1f;
                return;
            }

            MeDto me = session.Me;
            int step = session.TutorialStep;

            // 체력은 첫 전투부터 보인다.
            bool showStatus = step >= 3;
            if (_status.activeSelf != showStatus) _status.SetActive(showStatus);
            if (showStatus)
            {
                _nameText.text = $"레벨 {me.level}   {me.displayName}";
                string city = session.IsResident ? session.Backend.Regions.ShortName(me.regionCode) : null;
                bool showCity = city != null;
                if (_cityText.gameObject.activeSelf != showCity)
                {
                    _cityText.gameObject.SetActive(showCity);
                    _cityChip.gameObject.SetActive(showCity);
                }
                if (showCity)
                {
                    _cityText.text = city + " 가디언";
                    _cityChip.color = RegionColors.OfRegion(me.regionCode);
                }
                float ratio = _hp / (float)_hpMax;
                UiFactory.SetBar(_hpFill, ratio);
                _hpFill.GetComponent<Image>().color = ratio > 0.35f ? new Color(0.36f, 0.86f, 0.46f) : new Color(0.95f, 0.36f, 0.30f);
                _hpText.text = $"{_hp} / {_hpMax}";
                UiFactory.SetBar(_xpFill, me.xpNext > 0 ? me.xp / (float)me.xpNext : 0f);
            }

            // GC는 첫 보상부터 보인다. 서버가 아직 확정하지 않은 보상도 합쳐서 보여 준다.
            long target = me.gc + session.PendingGc;
            bool showWallet = target > 0 || step >= 4;
            if (_wallet.activeSelf != showWallet) _wallet.SetActive(showWallet);
            if (showWallet)
            {
                if (_shownGc < 0f) _shownGc = target;
                if (!Mathf.Approximately(_shownGc, target))
                {
                    float speed = Mathf.Max(30f, Mathf.Abs(target - _shownGc) * 4f);
                    if (target > _shownGc) _walletPulse = 1f;
                    _shownGc = Mathf.MoveTowards(_shownGc, target, speed * dt);
                }
                _walletText.text = $"{Mathf.RoundToInt(_shownGc):N0} GC";
                _walletPulse = Mathf.MoveTowards(_walletPulse, 0f, dt * 3f);
                _wallet.transform.localScale = Vector3.one * (1f + _walletPulse * 0.08f);
            }
        }

        private void UpdateToast(float dt)
        {
            const float FadeTime = ToastFadeTime;
            // 기다리는 알림이 있으면 지금 것을 일찍 넘긴다. 그러지 않으면 알림이 밀려서
            // 방금 한 일과 상관없는 옛 알림이 한참 뒤에 나온다.
            float hold = _toastQueue.Count >= 2 ? ToastShortestHold : _toastQueue.Count == 1 ? ToastShortHold : ToastHold;

            if (_toastTime < 0f)
            {
                if (_toastQueue.Count == 0) return;
                _toastText.text = _toastQueue.Dequeue();
                _toast.SetActive(true);
                _toastTime = 0f;
            }

            _toastTime += dt;
            float alpha = Mathf.Min(_toastTime / FadeTime, (hold - _toastTime) / FadeTime);
            _toastGroup.alpha = Mathf.Clamp01(alpha);
            if (_toastTime >= hold)
            {
                _toast.SetActive(false);
                _toastTime = -1f;
            }
        }

        private void UpdateFade(float dt)
        {
            if (Mathf.Approximately(_fade.color.a, _fadeTo)) return;
            _fadeTime += dt;
            float t = _fadeDuration <= 0f ? 1f : Mathf.Clamp01(_fadeTime / _fadeDuration);
            SetFadeAlpha(Mathf.Lerp(_fadeFrom, _fadeTo, t));
        }

        private void SetFadeAlpha(float alpha)
        {
            _fade.color = new Color(0f, 0f, 0f, alpha);
            bool visible = alpha > 0.001f;
            if (_fade.enabled != visible) _fade.enabled = visible;
        }
    }
}
