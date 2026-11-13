using CityGuardians.Domain;
using CityGuardians.Inputs;
using CityGuardians.Services;
using UnityEngine;

namespace CityGuardians.Gameplay
{
    // 전투 모드 컨트롤러 (docs/COMBAT_SYSTEM.md 13장).
    // 모드마다 다른 것(카메라, 입력의 뜻, 조작 안내, 모드 고유의 규칙 연결)만 여기에 둔다.
    // 캐릭터, 이동, 체력, 가방, 장비, 보상, 도시 위험도는 공통 서비스(CombatServices.cs)를 쓴다.

    /// <summary>전투 장면 하나를 맡는 모드 컨트롤러의 바탕.</summary>
    public abstract class CombatModeController : MonoBehaviour
    {
        /// <summary>CombatModeCodes.</summary>
        public abstract string ModeCode { get; }

        public CombatModeDef Mode => CombatService.ModeDef(ModeCode);

        /// <summary>지금 켜 둔 카메라 (CameraKinds).</summary>
        public string CameraKind { get; private set; }

        protected virtual void OnEnable() => CombatService.Enter(this);

        protected virtual void OnDisable() => CombatService.Exit(this);

        /// <summary>이 모드의 카메라로 바꾼다. 입력의 뜻도 함께 바뀐다 (같은 마우스가 1인칭에서는 겨누기다).</summary>
        protected bool UseCamera(string kind)
        {
            // 이미 그 카메라면 건드리지 않는다 (장면에 들어올 때 잡아 둔 시점이 흐트러지지 않게).
            if (CameraRig.Kind == kind)
            {
                CameraKind = kind;
                return true;
            }
            if (!CameraRig.Use(kind)) return false;
            CameraKind = kind;
            return true;
        }

        /// <summary>정의에 적힌 이 모드의 첫 카메라. 정의가 없으면 3인칭.</summary>
        protected string DefaultCamera
        {
            get
            {
                CombatModeDef mode = Mode;
                return mode != null && !string.IsNullOrEmpty(mode.camera) ? mode.camera : CameraKinds.ThirdPerson;
            }
        }
    }

    /// <summary>
    /// 디펜스 모드 (docs/DEFENSE_MODE.md). 전투 규칙은 DefenseBattle 이 맡고,
    /// 여기서는 시점 전환(V: 3인칭 ↔ 전략 시점)과 조작 안내를 맡는다.
    /// </summary>
    public sealed class DefenseModeController : CombatModeController
    {
        public override string ModeCode => CombatModeCodes.Defense;

        public DefenseBattle Battle { get; private set; }
        public bool StrategyView => CameraKind == CameraKinds.Strategy;

        public static DefenseModeController Create(GameObject host, DefenseBattle battle)
        {
            var controller = host.AddComponent<DefenseModeController>();
            controller.Battle = battle;
            return controller;
        }

        private void Start()
        {
            UseCamera(DefaultCamera);
            ShowHint();
        }

        private void OnDestroy()
        {
            GameEvents.SetKeyHint(null);
        }

        private void Update()
        {
            if (GameState.IsControlLocked || Time.frameCount <= GameState.LastUnlockFrame) return;
            if (!PlayerInputHub.Current.CameraTogglePressed) return;
            SetStrategyView(!StrategyView);
        }

        public void SetStrategyView(bool strategy)
        {
            if (!UseCamera(strategy ? CameraKinds.Strategy : CameraKinds.ThirdPerson)) return;
            Sfx.Play(Sfx.Id.Interact);
            ShowHint();
            GameEvents.ShowToast(strategy
                ? "전략 시점이에요. 마우스로 돌리고 휠로 당겨요. V 키로 돌아가요."
                : "3인칭 시점으로 돌아왔어요.");
        }

        private void ShowHint()
        {
            GameEvents.SetKeyHint(StrategyView
                ? "E  타워 세우기·강화      V  3인칭 시점      휠  당기기·밀기"
                : "E  타워 세우기·강화      V  전략 시점      마우스 왼쪽 버튼  공격      Shift  달리기");
        }
    }

    /// <summary>
    /// 헌트 모드 (docs/HUNT_MODE.md). 사냥 규칙은 HuntZone 이 맡고, 여기서는 사냥이 도시 위험도에
    /// 반영된 결과를 알리고, 위험도가 높은 도시에서 로봇이 더 많이 나오게 한다.
    /// </summary>
    public sealed class HuntModeController : CombatModeController
    {
        public override string ModeCode => CombatModeCodes.Hunt;

        /// <summary>위험도 단계에 따라 전투 구역에 더 나오는 로봇 수.</summary>
        public int ExtraEnemies { get; private set; }

        private int _shownVersion = -1;
        private string _announcedStage;

        protected override void OnEnable()
        {
            base.OnEnable();
            HuntZone.ReportConfirmed += OnReportConfirmed;
        }

        protected override void OnDisable()
        {
            HuntZone.ReportConfirmed -= OnReportConfirmed;
            HuntZone.ExtraAlive = 0;
            base.OnDisable();
        }

        private void Start()
        {
            UseCamera(DefaultCamera);
        }

        private void Update()
        {
            GameSession session = GameSession.Current;
            // 이방인의 사냥은 어느 도시에도 반영되지 않는다.
            if (session == null || !session.IsResident) return;

            CityRiskService.RefreshIfStale();
            if (CityRiskService.Version == _shownVersion) return;
            _shownVersion = CityRiskService.Version;
            ApplyRisk(CityRiskService.Home);
        }

        /// <summary>사냥은 언제나 소속 도시에 반영된다. 그 도시의 위험도에 맞춰 사냥터를 바꾼다.</summary>
        private void ApplyRisk(CityStatusDto home)
        {
            string stage = home != null ? home.stage : RiskStages.Safe;
            ExtraEnemies = stage == RiskStages.Emergency ? 2 : stage == RiskStages.Danger ? 1 : 0;
            HuntZone.ExtraAlive = ExtraEnemies;

            if (home == null || stage == _announcedStage) return;
            bool first = _announcedStage == null;
            _announcedStage = stage;
            // 처음 들어왔을 때는 평화로우면 조용히 넘어간다.
            if (first && ExtraEnemies == 0) return;
            if (ExtraEnemies > 0)
            {
                Sfx.Play(Sfx.Id.Alarm, 0.5f);
                GameEvents.ShowToast($"위험 구역 소탕 임무!  {CityRiskService.StageBadge(stage)} · 로봇이 더 많이 나오고, 사냥이 위험도를 더 많이 낮춰요.");
            }
            else
            {
                GameEvents.ShowToast($"도시가 안정을 되찾았어요.  {CityRiskService.StageBadge(stage)}");
            }
        }

        private void OnReportConfirmed(HuntZone zone, HuntReportDto report)
        {
            if (report == null) return;
            RewardService.AnnounceCity(report.city);
        }
    }

    /// <summary>
    /// FPS 모드 (docs/FPS_MODE.md). 임무 규칙은 FpsMission 이, 무기와 장치는 FpsWeapon 이 맡고,
    /// 여기서는 1인칭 카메라와 조작 안내를 맡는다. 이동은 헌트 모드와 같은 PlayerController 를 그대로 쓴다.
    /// </summary>
    public sealed class FPSModeController : CombatModeController
    {
        public override string ModeCode => CombatModeCodes.Fps;

        public FpsMission Mission { get; private set; }
        public FpsWeapon Weapon { get; private set; }

        public static FPSModeController Create(GameObject host, FpsMission mission, PlayerController player)
        {
            var controller = host.AddComponent<FPSModeController>();
            controller.Mission = mission;
            controller.Weapon = FpsWeapon.Attach(player, mission);
            return controller;
        }

        private void Start()
        {
            UseCamera(CameraKinds.FirstPerson);
            GameEvents.SetKeyHint("마우스 왼쪽  발사    오른쪽  겨누기    R  재장전    Q  보조 장치    F  지원 장비    E  상호작용");
        }

        private void OnDestroy()
        {
            GameEvents.SetKeyHint(null);
        }
    }
}
