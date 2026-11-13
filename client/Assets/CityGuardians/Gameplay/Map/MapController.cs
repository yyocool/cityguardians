using System;
using System.Collections.Generic;
using CityGuardians.Domain;
using CityGuardians.Inputs;
using CityGuardians.Services;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CityGuardians.Gameplay
{
    public enum MapMode
    {
        /// <summary>둘러보기.</summary>
        Browse,
        /// <summary>전입신고할 도시를 고르는 중.</summary>
        Residency,
    }

    /// <summary>
    /// 대한민국 지도와 전입신고의 상태 (docs/MAP_SYSTEM.md 3장, docs/RESIDENCY_SYSTEM.md 4장).
    /// 지도는 장면에 속하지 않고 어디서든 겹쳐 띄운다. 화면은 UI가 이 객체의 상태를 읽어 그리고,
    /// 마우스로 고른 것을 FocusSido / SelectRegion 으로 알려 준다.
    /// 열려 있는 동안 플레이어 조작은 잠기지만 게임은 멈추지 않는다.
    /// </summary>
    public sealed class MapController : MonoBehaviour
    {
        /// <summary>이 단계부터 지도를 쓸 수 있다 (방어대장을 만난 뒤). 그 전에는 배울 것을 늘리지 않는다.</summary>
        public const int MinTutorialStep = 6;
        private const float EnemyTooClose = 14f;
        private const int SuggestionCount = 3;

        public static MapController Instance { get; private set; }

        public bool IsOpen { get; private set; }
        public MapMode Mode { get; private set; }
        /// <summary>확대해서 보고 있는 시·도. 전국을 보고 있으면 null.</summary>
        public string FocusedSido { get; private set; }
        /// <summary>고른 지역. 없으면 null.</summary>
        public string SelectedRegion { get; private set; }
        /// <summary>서버에 요청을 보내고 기다리는 중인가.</summary>
        public bool IsBusy { get; private set; }
        /// <summary>지역 상태를 받았는가. 받기 전에는 수치를 "…"로 보여 준다.</summary>
        public bool StatusLoaded { get; private set; }
        /// <summary>상태가 바뀔 때마다 1씩 오른다. 화면이 다시 그릴지 판단하는 데 쓴다.</summary>
        public int Version { get; private set; }

        /// <summary>전입신고가 끝났을 때. 장면이 등록 연출을 보여 준다.</summary>
        public event Action<ResidencyResultDto> ResidencyCompleted;

        private readonly Dictionary<string, RegionStatusDto> _status = new Dictionary<string, RegionStatusDto>();
        private readonly List<string> _offered = new List<string>();
        private readonly System.Random _random = new System.Random();
        private int _openedFrame = -1;
        // 응답이 돌아왔을 때 아직 그 요청을 기다리고 있는지 가리는 번호. 지도를 닫으면 둘 다 올려 응답을 버린다.
        private int _statusRequest;
        private int _actionRequest;

        private void Awake()
        {
            Instance = this;
            GameEvents.PlayerHurt += OnPlayerHurt;
        }

        private void OnDestroy()
        {
            GameEvents.PlayerHurt -= OnPlayerHurt;
        }

        // ── 열고 닫기 ───────────────────────────────────────────

        /// <summary>지금 지도를 열 수 있는가. 열 수 없으면 이유를 돌려준다 (알릴 필요가 없는 경우는 null).</summary>
        public bool CanOpen(out string reason)
        {
            reason = null;
            GameSession session = GameSession.Current;
            if (session == null || GameRoot.Instance.IsBusy || GameState.IsControlLocked) return false;
            if (session.TutorialStep < MinTutorialStep) return false;

            string scene = SceneManager.GetActiveScene().name;
            if (scene == SceneNames.Defense)
            {
                reason = "방어전 중에는 지도를 볼 수 없어요.";
                return false;
            }
            if (scene != SceneNames.Room && scene != SceneNames.City) return false;

            if (EnemyIsAfterPlayer())
            {
                reason = "로봇이 쫓아오는 동안에는 지도를 볼 수 없어요.";
                return false;
            }
            return true;
        }

        /// <summary>둘러보기로 연다. 주민이면 내 도시의 시·도를 먼저 보여 준다.</summary>
        public bool Open()
        {
            if (!CanOpen(out string reason))
            {
                if (reason != null) GameEvents.ShowToast(reason);
                return false;
            }
            OpenInternal(MapMode.Browse);
            return true;
        }

        /// <summary>전입신고할 도시를 고르도록 연다.</summary>
        public bool OpenForResidency()
        {
            GameSession session = GameSession.Current;
            if (session == null) return false;
            if (session.IsResident)
            {
                GameEvents.ShowToast("이미 전입신고를 했어요.");
                return false;
            }
            if (!CanOpen(out string reason))
            {
                if (reason != null) GameEvents.ShowToast(reason);
                return false;
            }
            OpenInternal(MapMode.Residency);
            return true;
        }

        private void OpenInternal(MapMode mode)
        {
            GameSession session = GameSession.Current;
            IsOpen = true;
            Mode = mode;
            IsBusy = false;
            _openedFrame = Time.frameCount;
            _offered.Clear();

            // 주민이면 내 도시부터 보여 준다.
            RegionDef mine = session.IsResident ? session.Backend.Regions.FindRegion(session.Me.regionCode) : null;
            FocusedSido = mine != null && mode == MapMode.Browse ? mine.sidoCode : null;
            SelectedRegion = mine != null && mode == MapMode.Browse ? mine.code : null;

            GameState.Lock(GameState.LockMap);
            GameEvents.SetPrompt(null);
            Sfx.Play(Sfx.Id.Interact);
            Version++;
            RefreshStatus();
        }

        public void Close()
        {
            if (!IsOpen) return;
            IsOpen = false;
            IsBusy = false;
            _statusRequest++;   // 기다리던 응답은 버린다
            _actionRequest++;
            FocusedSido = null;
            SelectedRegion = null;
            // 지도가 띄운 선택 메뉴가 남아 있지 않게 한다.
            ChoiceMenu.Instance.Close();
            GameState.Unlock(GameState.LockMap);
            Version++;
        }

        /// <summary>
        /// 한 단계 뒤로: 고른 지역 → 시·도 → 전국 → 닫기.
        /// 주민센터에서 시작한 전입신고도 전국 보기에서 한 번 더 누르면 그만둔다.
        /// </summary>
        public void Back()
        {
            if (!IsOpen || IsBusy) return;
            if (SelectedRegion != null) SelectedRegion = null;
            else if (FocusedSido != null) FocusedSido = null;
            else { Close(); return; }
            Version++;
        }

        // ── 고르기 ──────────────────────────────────────────────

        /// <summary>시·도를 확대해서 본다. null이면 전국 보기로 돌아간다.</summary>
        public void FocusSido(string sidoCode)
        {
            if (!IsOpen || IsBusy) return;
            if (sidoCode != null && MapData.Instance.FindSido(sidoCode) == null) return;
            if (FocusedSido == sidoCode && SelectedRegion == null) return;
            FocusedSido = sidoCode;
            SelectedRegion = null;
            Sfx.Play(Sfx.Id.Interact);
            Version++;
        }

        /// <summary>지역을 고른다. 그 지역의 시·도도 함께 확대된다.</summary>
        public void SelectRegion(string regionCode)
        {
            if (!IsOpen || IsBusy) return;
            MapRegion region = MapData.Instance.FindRegion(regionCode);
            if (region == null || SelectedRegion == regionCode) return;
            FocusedSido = region.sido;
            SelectedRegion = regionCode;
            Sfx.Play(Sfx.Id.Interact);
            Version++;
        }

        /// <summary>그 지역의 상태. 주민도 방어 포인트도 없으면 0으로 채운 값을 돌려준다.</summary>
        public RegionStatusDto StatusOf(string regionCode)
        {
            if (regionCode != null && _status.TryGetValue(regionCode, out RegionStatusDto status)) return status;
            return new RegionStatusDto { regionCode = regionCode };
        }

        /// <summary>전입신고 중이고, 고른 지역이 있어서 신고할 수 있는가.</summary>
        public bool CanRequestResidency => IsOpen && Mode == MapMode.Residency && SelectedRegion != null && !IsBusy;

        private void RefreshStatus()
        {
            GameSession session = GameSession.Current;
            if (session == null) return;
            StatusLoaded = false;
            int request = ++_statusRequest;
            session.Backend.GetRegions(result =>
            {
                if (request != _statusRequest || GameSession.Current != session) return;
                if (!result.ok)
                {
                    // 수치 없이도 지도는 쓸 수 있다. 알리기만 한다.
                    Debug.LogWarning($"지역 상태를 받지 못함: {result.errorCode} {result.message}");
                    GameEvents.ShowToast("도시 소식을 받지 못했어요. 지도는 그대로 볼 수 있어요.");
                    return;
                }
                _status.Clear();
                for (int i = 0; i < result.data.regions.Count; i++)
                    _status[result.data.regions[i].regionCode] = result.data.regions[i];
                StatusLoaded = true;
                Version++;
            });
        }

        // ── 전입신고 ────────────────────────────────────────────

        /// <summary>고른 지역으로 전입신고를 시작한다: 이름 고르기 → 확인 → 등록.</summary>
        public void RequestResidency()
        {
            if (!CanRequestResidency) return;
            OfferNicknames();
        }

        private void OfferNicknames() => OfferNicknames(1);

        /// <summary>추천 이름 가운데 쓸 수 있는 것을 서버에 물어 골라서 보여 준다.</summary>
        /// <param name="attempt">몇 번째 묶음인가. 내놓은 이름을 모두 누가 쓰고 있으면 다음 묶음으로 넘어간다.</param>
        private void OfferNicknames(int attempt)
        {
            const int MaxAttempts = 5;
            GameSession session = GameSession.Current;
            if (session == null || !IsOpen || SelectedRegion == null) return;
            string regionCode = SelectedRegion;

            // 방금 보여 준 이름은 다시 내놓지 않는다. 낼 이름이 모자라면 처음부터 다시 돈다.
            List<string> candidates = NicknameSuggester.Suggest(_random, SuggestionCount, _offered);
            if (candidates.Count < SuggestionCount)
            {
                _offered.Clear();
                candidates = NicknameSuggester.Suggest(_random, SuggestionCount);
            }
            _offered.AddRange(candidates);

            IsBusy = true;
            Version++;
            int request = ++_actionRequest;
            var available = new List<string>();
            int waiting = candidates.Count;
            bool failed = false;
            for (int i = 0; i < candidates.Count; i++)
            {
                string name = candidates[i];
                session.Backend.CheckNickname(name, result =>
                {
                    if (request != _actionRequest || GameSession.Current != session) return;
                    if (!result.ok) failed = true;
                    else if (result.data.available) available.Add(name);
                    if (--waiting > 0) return;

                    IsBusy = false;
                    Version++;
                    if (available.Count > 0)
                    {
                        ShowNicknameMenu(regionCode, available);
                    }
                    else if (failed || attempt >= MaxAttempts)
                    {
                        GameEvents.ShowToast("이름을 확인하지 못했어요. 잠시 뒤에 다시 시도해 주세요.");
                    }
                    else
                    {
                        // 모두 누가 쓰고 있다: 다른 이름으로 다시 묻는다.
                        OfferNicknames(attempt + 1);
                    }
                });
            }
        }

        private void ShowNicknameMenu(string regionCode, List<string> names)
        {
            var options = new List<ChoiceOption>();
            for (int i = 0; i < names.Count && i < ChoiceMenu.MaxOptions - 1; i++)
            {
                string name = names[i];
                options.Add(new ChoiceOption
                {
                    label = name,
                    detail = "이 이름으로 할래요.",
                    onChosen = () => ConfirmResidency(regionCode, name),
                });
            }
            options.Add(new ChoiceOption
            {
                label = "다른 이름 보기",
                detail = "마음에 드는 이름이 나올 때까지 볼 수 있어요.",
                onChosen = OfferNicknames,
            });
            ChoiceMenu.Instance.Open("가디언 이름 고르기",
                "도시에서 불릴 이름이에요.\n진짜 이름이나 전화번호는 쓰지 않아요.", options.ToArray());
        }

        private void ConfirmResidency(string regionCode, string nickname)
        {
            GameSession session = GameSession.Current;
            if (session == null || !IsOpen) return;
            string regionName = session.Backend.Regions.FullName(regionCode);
            int bonus = session.Defs.economy.residencyBonusGc;
            ChoiceMenu.Instance.Open("전입신고",
                $"{regionName}\n가디언 이름   {nickname}",
                new ChoiceOption
                {
                    label = "네, 전입신고할게요",
                    cost = bonus > 0 ? $"축하금 {bonus:N0} GC" : "",
                    detail = "이 도시의 가디언이 돼요. 실제로 사는 곳과 달라도 괜찮아요.",
                    onChosen = () => Register(regionCode, nickname),
                },
                new ChoiceOption
                {
                    label = "이름을 다시 고를래요",
                    detail = "다른 추천 이름을 봐요.",
                    onChosen = OfferNicknames,
                },
                new ChoiceOption
                {
                    label = "도시를 다시 고를래요",
                    detail = "지도로 돌아가요.",
                    onChosen = () => { },
                });
        }

        private void Register(string regionCode, string nickname)
        {
            GameSession session = GameSession.Current;
            if (session == null || !IsOpen) return;
            IsBusy = true;
            Version++;
            int request = ++_actionRequest;
            session.Backend.RegisterResidency(regionCode, nickname, result =>
            {
                if (request != _actionRequest || GameSession.Current != session) return;
                IsBusy = false;
                Version++;

                if (result.ok)
                {
                    session.ApplyResidency(result.data);
                    Close();
                    Celebrate(session, result.data);
                    ResidencyCompleted?.Invoke(result.data);
                    return;
                }

                switch (result.errorCode)
                {
                    case ApiErrors.NicknameTaken:
                    case ApiErrors.NicknameInvalid:
                        // 그 사이에 다른 사람이 가져갔거나 규칙이 바뀌었다: 이름만 다시 고른다.
                        GameEvents.ShowToast((result.message ?? "쓸 수 없는 이름이에요.") + " 다른 이름을 골라 주세요.");
                        OfferNicknames();
                        break;
                    case ApiErrors.AlreadyResident:
                        GameEvents.ShowToast("이미 전입신고를 했어요.");
                        session.Backend.GetMe(me => { if (me.ok && GameSession.Current == session) session.ApplyMe(me.data); });
                        Close();
                        break;
                    default:
                        Debug.LogWarning($"전입신고 실패: {result.errorCode} {result.message}");
                        GameEvents.ShowToast("전입신고를 하지 못했어요. 잠시 뒤에 다시 시도해 주세요.");
                        break;
                }
            });
        }

        /// <summary>가디언 등록 연출: 완장이 도시의 색으로 바뀌고, 축하금이 날아오고, 등록 안내판이 뜬다.</summary>
        private static void Celebrate(GameSession session, ResidencyResultDto result)
        {
            PlayerController player = PlayerController.Instance;
            if (player != null)
            {
                PlayerFactory.ApplyArmband(player);
                if (result.bonusGc > 0) Fx.Coins(player.transform.position + Vector3.up * 2.4f, 10, player.transform);
            }
            Sfx.Play(Sfx.Id.Victory);

            string regionName = session.Backend.Regions.FullName(result.regionCode);
            string bonus = result.bonusGc > 0 ? $"\n\n전입 축하금  +{result.bonusGc:N0} GC" : "";
            DialogueRunner.Instance.ShowPanel("가디언 등록 완료!",
                $"{result.nickname} 님은 이제\n{regionName}의 가디언이에요.{bonus}", false);
        }

        // ── 매 프레임 ───────────────────────────────────────────

        private void Update()
        {
            IPlayerInput input = PlayerInputHub.Current;

            if (!IsOpen)
            {
                // 잠금이 풀린 바로 그 프레임의 입력으로 열지 않는다 (지도를 닫은 M이 다시 여는 일을 막는다).
                if (input.MapPressed && !GameState.Decorating && Time.frameCount > GameState.LastUnlockFrame) Open();
                return;
            }

            // 장면이 바뀌거나 접속이 끊기면 닫는다.
            if (GameSession.Current == null || GameRoot.Instance.IsBusy)
            {
                Close();
                return;
            }

            // 멈춤, 선택 메뉴, 대화가 위에 떠 있는 동안에는 지도가 입력을 받지 않는다.
            if (!AcceptsInput) return;

            if (input.MapPressed) Close();
            else if (input.PointerBackPressed) Back();
            else if (input.InteractPressed) RequestResidency();
        }

        /// <summary>
        /// 지도가 지금 입력을 받는가. 화면(UI)도 마우스 입력을 처리하기 전에 이것을 본다.
        /// 다른 화면이 닫힌 바로 그 프레임에는 받지 않는다 (같은 입력이 두 번 쓰이지 않게).
        /// </summary>
        public bool AcceptsInput =>
            IsOpen && !IsBusy && GameState.IsOnlyLockedBy(GameState.LockMap)
            && Time.frameCount > _openedFrame && Time.frameCount > GameState.LastUnlockFrame;

        private void OnPlayerHurt()
        {
            // 지도를 보다가 맞으면 바로 닫아서 대응할 수 있게 한다.
            if (!IsOpen) return;
            Close();
            GameEvents.ShowToast("공격받고 있어요! 지도를 닫았어요.");
        }

        private static bool EnemyIsAfterPlayer()
        {
            PlayerController player = PlayerController.Instance;
            if (player == null) return false;
            Vector3 position = player.transform.position;
            foreach (EnemyAgent enemy in FindObjectsByType<EnemyAgent>(FindObjectsSortMode.None))
            {
                if (enemy.State != EnemyState.Chase && enemy.State != EnemyState.Attack) continue;
                Vector3 to = enemy.transform.position - position;
                to.y = 0f;
                if (to.sqrMagnitude <= EnemyTooClose * EnemyTooClose) return true;
            }
            return false;
        }
    }
}
