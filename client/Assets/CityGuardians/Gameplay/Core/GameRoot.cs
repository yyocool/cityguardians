using System.Collections;
using System.IO;
using CityGuardians.Inputs;
using CityGuardians.Services;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CityGuardians.Gameplay
{
    public static class SceneNames
    {
        public const string Boot = "Boot";
        public const string Room = "Room";
        public const string City = "City";
        /// <summary>방어전 전장 "외곽 1구역".</summary>
        public const string Defense = "Defense";
        /// <summary>FPS 임무 전장 "도심 상가 구역".</summary>
        public const string Fps = "Fps";
        /// <summary>협동 방어전 전장 (v9.0). 여러 대원이 함께 관문을 지킨다.</summary>
        public const string Coop = "Coop";
    }

    public static class SpawnIds
    {
        /// <summary>방: 침대 옆 (게임을 켰을 때).</summary>
        public const string Bed = "bed";
        /// <summary>방: 문 안쪽 (도시에서 들어왔을 때).</summary>
        public const string RoomDoor = "room_door";
        /// <summary>도시: 내 집 앞.</summary>
        public const string Home = "home";
        /// <summary>도시: 방어기지 출동 게이트 앞 (방어전에서 돌아왔을 때).</summary>
        public const string DefenseGate = "defense_gate";
        /// <summary>방어전 전장: 방어 거점 앞.</summary>
        public const string DefenseBase = "defense_base";
        /// <summary>FPS 임무 전장: 가운데 광장.</summary>
        public const string FpsPlaza = "fps_plaza";
        /// <summary>협동 방어전 전장: 관문 앞.</summary>
        public const string CoopBase = "coop_base";
    }

    /// <summary>
    /// 장면이 바뀌어도 남아 있는 게임의 뿌리. 접속(부트), 장면 이동, 일시정지, 커서 잠금을 맡는다.
    /// </summary>
    public sealed class GameRoot : MonoBehaviour
    {
        public static GameRoot Instance { get; private set; }

        /// <summary>저장 폴더를 바꾼다. 자동 테스트가 실제 저장 데이터를 건드리지 않게 하는 데 쓴다.</summary>
        public static string StorageDirOverride;

        /// <summary>
        /// 모의 서버가 쓰는 시계를 바꾼다. 자동 테스트가 게임 시간을 빠르게 돌릴 때
        /// 서버 시간도 함께 흐르게 하는 데 쓴다. 비어 있으면 실제 시계를 쓴다.
        /// </summary>
        public static IClock ClockOverride;

        /// <summary>
        /// 모의 서버가 쓰는 정의를 바꾼다. 자동 테스트가 확률(드롭)을 정해 놓고 확인할 때 쓴다.
        /// 비어 있으면 기본 정의를 쓴다.
        /// </summary>
        public static CityGuardians.Domain.GameDefs DefsOverride;

        /// <summary>다음 장면에서 플레이어가 나타날 자리.</summary>
        public static string PendingSpawn = SpawnIds.Bed;

        public bool IsBusy => _transitioning || _booting;
        public bool BootHasFailed => _bootFailCode != null;

        private bool _transitioning;
        private bool _booting;
        private string _bootFailCode;
        private IGameBackend _backend;

        /// <summary>전투 모드와 도시 현황처럼 접속마다 새로 받아야 하는 것을 비운다.</summary>
        private static void ResetCombatState()
        {
            CombatService.ResetState();
            CityRiskService.ResetState();
            RegionService.ResetState();
            HuntZone.ExtraAlive = 0;
            CoopLaunch.Discard();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Initialize()
        {
            // 에디터에서 도메인 다시 불러오기를 꺼 둔 경우에도 깨끗하게 시작한다.
            GameState.ClearAll();
            GameSession.End();
            AttackSlots.Clear();
            ItemActions.ResetState();
            ResetCombatState();
            PendingSpawn = SpawnIds.Bed;

            var go = new GameObject("GameRoot");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<GameRoot>();
            go.AddComponent<DialogueRunner>();
            go.AddComponent<ChoiceMenu>();
            go.AddComponent<MapController>();
            go.AddComponent<BagController>();
            go.AddComponent<CouponPresenter>();
            go.AddComponent<PhoneController>();
            go.AddComponent<LiveDashboardController>();
            Sfx.Attach(go);
            TimeFx.Attach(go);
        }

        public static string StorageDirectory =>
            !string.IsNullOrEmpty(StorageDirOverride)
                ? StorageDirOverride
                : Path.Combine(Application.persistentDataPath, "dev_local");

        // ── 접속 ────────────────────────────────────────────────

        /// <summary>서버(지금은 로컬 모의 서버)에 접속하고 방으로 간다.</summary>
        public void BeginBoot()
        {
            if (_booting) return;
            _booting = true;
            _bootFailCode = null;
            GameEvents.RaiseBootFailed(null, null);
            GameState.Lock(GameState.LockBoot);
            GameSession.End();

            try
            {
                _backend = new LocalMockBackend(new FileTextStore(StorageDirectory), ClockOverride ?? new Services.SystemClock(),
                    DefsOverride);
            }
            catch (System.Exception e)
            {
                OnBootResult(ApiResult<MeDto>.Fail(ApiErrors.StorageFailed, e.Message));
                return;
            }
            _backend.Bootstrap(OnBootResult);
        }

        private void OnBootResult(ApiResult<MeDto> result)
        {
            _booting = false;
            if (!result.ok)
            {
                _bootFailCode = result.errorCode ?? "UNKNOWN";
                Debug.LogWarning($"접속 실패: {result.errorCode} {result.message}");
                GameEvents.RaiseBootFailed(_bootFailCode, result.message);
                return;
            }

            GameSession.Start(_backend, result.data);
            GameState.Unlock(GameState.LockBoot);
            PendingSpawn = SpawnIds.Bed;
            LoadScene(SceneNames.Room, SpawnIds.Bed);
        }

        private bool CanStartOver =>
            _bootFailCode == ApiErrors.GuestDataCorrupt || _bootFailCode == ApiErrors.Unauthorized;

        /// <summary>접속을 끊고 처음 상태로 돌아간다. 자동 테스트에서 "게임을 껐다 켜는" 상황을 만든다.</summary>
        public void Shutdown()
        {
            StopAllCoroutines();
            HuntZone.FlushAll();
            DialogueRunner.Instance.CloseSilently();
            CouponPresenter.Instance.CloseSilently();
            MapController.Instance.Close();
            ChoiceMenu.Instance.Close();
            _transitioning = false;
            _booting = false;
            _bootFailCode = null;
            _backend = null;
            GameSession.End();
            GameState.ClearAll();
            ResetCombatState();
            GameEvents.SetObjective(null);
            GameEvents.SetKeyHint(null);
            GameEvents.RequestFade(false, 0f);
        }

        // ── 장면 이동 ───────────────────────────────────────────

        public void LoadScene(string sceneName, string spawnId)
        {
            if (_transitioning) return;
            StartCoroutine(LoadRoutine(sceneName, spawnId));
        }

        private IEnumerator LoadRoutine(string sceneName, string spawnId)
        {
            _transitioning = true;
            GameState.Lock(GameState.LockTransition);
            GameEvents.RequestFade(true, 0.3f);
            yield return new WaitForSecondsRealtime(0.3f);

            // 떠나기 전에 남은 사냥 보고를 보낸다.
            HuntZone.FlushAll();
            DialogueRunner.Instance.CloseSilently();
            CouponPresenter.Instance.CloseSilently();
            MapController.Instance.Close();
            ChoiceMenu.Instance.Close();
            GameEvents.SetPrompt(null);
            PendingSpawn = spawnId;

            AsyncOperation load = SceneManager.LoadSceneAsync(sceneName);
            while (load != null && !load.isDone) yield return null;
            yield return null;

            GameEvents.RequestFade(false, 0.35f);
            GameState.Unlock(GameState.LockTransition);
            _transitioning = false;

            // 방과 도시에 들어올 때 밤사이 들어온 이자가 있으면 알린다.
            if (sceneName == SceneNames.Room || sceneName == SceneNames.City) BankActions.CheckInterest();
        }

        // ── 매 프레임: 일시정지, 접속 실패 처리, 커서 ───────────

        private void Update()
        {
            IPlayerInput input = PlayerInputHub.Current;

            if (BootHasFailed)
            {
                if (input.RetryPressed) BeginBoot();
                else if (CanStartOver && input.NewGamePressed) StartOver();
            }
            else if (GameSession.Current != null && !IsBusy)
            {
                if (input.PausePressed)
                {
                    // 지도가 맨 위에 떠 있으면 Esc는 지도에서 한 단계 뒤로 간다. 그 밖에는 멈춤이다.
                    MapController map = MapController.Instance;
                    if (map.AcceptsInput) map.Back();
                    else SetPaused(!GameState.IsPaused);
                }
                else if (GameState.IsPaused && input.QuitPressed) Quit();
            }

            UpdateCursor();
        }

        private void StartOver()
        {
            if (_backend == null) return;
            _booting = true;
            _bootFailCode = null;
            GameEvents.RaiseBootFailed(null, null);
            _backend.StartOverAsNewGuest(OnBootResult);
        }

        public void SetPaused(bool paused)
        {
            if (paused == GameState.IsPaused) return;
            if (paused)
            {
                GameState.Lock(GameState.LockPause);
                Time.timeScale = 0f;
            }
            else
            {
                GameState.Unlock(GameState.LockPause);
                Time.timeScale = 1f;
            }
            GameEvents.RaisePauseChanged(paused);
        }

        private static void Quit()
        {
            HuntZone.FlushAll();
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        private void UpdateCursor()
        {
            if (Application.isBatchMode) return;
            // 지도와 전술 지도는 마우스로 가리키는 화면이라 커서를 풀어 둔다.
            // 대시보드의 카드를 마우스로 누르려고 Alt 를 누르고 있는 동안에도 풀어 둔다.
            bool playing = GameSession.Current != null && !GameState.IsPaused && !BootHasFailed && Application.isFocused
                           && !MapController.Instance.IsOpen && !GameState.IsLockedBy(GameState.LockTacticalMap)
                           && !LiveDashboardController.Instance.PointerMode;
            CursorLockMode wanted = playing ? CursorLockMode.Locked : CursorLockMode.None;
            if (Cursor.lockState != wanted) Cursor.lockState = wanted;
            if (Cursor.visible == playing) Cursor.visible = !playing;
        }

        private void OnApplicationFocus(bool focused)
        {
            // 창을 벗어나면 멈춘다. 돌아왔을 때 갑자기 맞고 있지 않도록.
            if (!focused && GameSession.Current != null && !IsBusy && !Application.isBatchMode
                && !Application.isEditor)
                SetPaused(true);
        }
    }

    /// <summary>
    /// 방·도시 장면을 에디터에서 바로 실행했을 때 접속부터 하도록 돌려보낸다.
    /// </summary>
    public static class SceneGuard
    {
        public static bool EnsureSession()
        {
            if (GameSession.Current != null) return true;
            if (!GameRoot.Instance.IsBusy && !GameRoot.Instance.BootHasFailed)
                SceneManager.LoadScene(SceneNames.Boot);
            return false;
        }
    }
}
