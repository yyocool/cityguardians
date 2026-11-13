using System;
using System.Collections.Generic;
using CityGuardians.Domain;
using CityGuardians.Services;
using UnityEngine;

namespace CityGuardians.Gameplay
{
    // 공통 전투 시스템 (docs/COMBAT_SYSTEM.md 12장).
    // 세 전투 모드(디펜스·헌트·FPS)는 조작과 규칙이 다르지만, 계정·레벨·GC·가방·장비·소속 도시·도시 위험도는
    // 하나를 함께 쓴다. 여기 있는 서비스들이 그 "함께 쓰는 것"의 창구다. 모드별 규칙은 모드 컨트롤러가 맡는다.
    // 어느 서비스도 값을 스스로 계산해 확정하지 않는다: 보상, 장비 구매, 위험도는 모두 서버 응답을 받아 반영한다.

    /// <summary>
    /// 지금 어떤 전투 모드인지와, 전투의 시작·결과 보고를 맡는다.
    /// </summary>
    public static class CombatService
    {
        /// <summary>지금 장면을 맡은 모드 컨트롤러. 전투 장면이 아니면 null.</summary>
        public static CombatModeController Active { get; private set; }

        /// <summary>지금의 전투 모드 (CombatModeCodes). 전투 장면이 아니면 null.</summary>
        public static string ActiveMode => Active != null ? Active.ModeCode : null;

        public static bool IsFps => ActiveMode == CombatModeCodes.Fps;

        /// <summary>모드가 바뀌었을 때. 인자는 새 모드 (없으면 null).</summary>
        public static event Action<string> ModeChanged;

        public static void ResetState()
        {
            Active = null;
        }

        public static string NewRequestId() => Guid.NewGuid().ToString("N");

        internal static void Enter(CombatModeController controller)
        {
            if (Active == controller) return;
            Active = controller;
            ModeChanged?.Invoke(ActiveMode);
        }

        internal static void Exit(CombatModeController controller)
        {
            if (Active != controller) return;
            Active = null;
            ModeChanged?.Invoke(null);
        }

        public static CombatModeDef ModeDef(string modeCode)
        {
            GameSession session = GameSession.Current;
            return session?.Defs.combat.FindMode(modeCode);
        }

        /// <summary>그 모드에 지금 들어갈 수 있는가. 관리자가 모드를 잠시 닫을 수 있다.</summary>
        public static bool IsOpen(string modeCode)
        {
            CombatModeDef def = ModeDef(modeCode);
            return def != null && !def.closed;
        }

        /// <summary>FPS 임무를 시작한다. 걸린 시간은 서버가 잰다.</summary>
        public static void StartFps(string missionCode, Action<ApiResult<FpsSessionDto>> done)
        {
            GameSession session = GameSession.Current;
            if (session == null) return;
            session.Backend.StartFpsMission(missionCode, done);
        }

        /// <summary>
        /// FPS 임무 결과를 보고한다. 보상과 도시 위험도는 서버가 정하고, 여기서는 받은 값을 반영만 한다.
        /// 같은 임무를 다시 보내도 서버는 처음 결과를 그대로 돌려준다.
        /// </summary>
        public static void FinishFps(string sessionId, FpsReport report, Action<ApiResult<FpsResultDto>> done)
        {
            GameSession session = GameSession.Current;
            if (session == null) return;
            session.Backend.FinishFpsMission(sessionId, report, result =>
            {
                if (result.ok && GameSession.Current == session)
                {
                    session.ApplyFpsResult(result.data);
                    CityRiskService.Apply(result.data.city);
                }
                done?.Invoke(result);
            });
        }
    }

    /// <summary>
    /// 전투 보상과 도시 반영 결과를 플레이어에게 알리는 말을 만든다.
    /// 숫자는 모두 서버 응답에서 온 것이다.
    /// </summary>
    public static class RewardService
    {
        /// <summary>결과 화면에 넣는 한 줄. 반영된 것이 없으면 null.</summary>
        public static string CityLine(CityEffectDto effect)
        {
            if (effect == null) return null;
            string city = RegionService.ShortName(effect.regionCode) ?? "도시";
            string who = effect.actor == RiskActors.Supporter ? "지원 " : "";
            string stage = RiskStages.DisplayName(effect.stage);
            string icon = RiskStages.Icon(effect.stage);
            string line = effect.riskAfter != effect.riskBefore
                ? $"{who}{city} 위험도  {effect.riskBefore} → {effect.riskAfter}  {icon} {stage}"
                : $"{who}{city} 위험도  {effect.riskAfter}  {icon} {stage}";
            if (effect.capped && effect.riskDeltaCenti == 0) line += "  (오늘 낮출 수 있는 만큼 낮췄어요)";
            return line;
        }

        /// <summary>침공을 막는 데 쓰였다면 그 진행 상황 한 줄. 아니면 null.</summary>
        public static string InvasionLine(CityEffectDto effect)
        {
            if (effect == null || string.IsNullOrEmpty(effect.invasionId)) return null;
            string kind = InvasionKinds.DisplayName(effect.invasionKind);
            return effect.invasionRepelled
                ? $"{kind} 격퇴 성공!"
                : $"{kind} 격퇴  {effect.invasionProgress} / {effect.invasionRequired}";
        }

        /// <summary>도시 기여도 한 줄. 얻은 것이 없으면 null.</summary>
        public static string ContributionLine(CityEffectDto effect) =>
            effect == null || effect.contribution <= 0 ? null : $"도시 기여도  +{effect.contribution}";

        /// <summary>결과 화면에 넣는 묶음 (앞에 빈 줄 없이, 줄마다 \n 으로 시작). 없으면 빈 글.</summary>
        public static string CityBlock(CityEffectDto effect)
        {
            string text = "";
            string line = CityLine(effect);
            if (line != null) text += "\n" + line;
            line = InvasionLine(effect);
            if (line != null) text += "\n" + line;
            line = ContributionLine(effect);
            if (line != null) text += "\n" + line;
            return text;
        }

        /// <summary>
        /// 사냥처럼 결과 화면이 없는 전투에서 도시 반영을 알린다. 눈에 띄는 변화(위험도 숫자, 단계, 침공 격퇴)만 알린다.
        /// </summary>
        public static void AnnounceCity(CityEffectDto effect)
        {
            if (effect == null) return;
            CityRiskService.Apply(effect);

            if (effect.invasionRepelled)
            {
                Sfx.Play(Sfx.Id.Victory, 0.7f);
                GameEvents.ShowToast($"{InvasionKinds.DisplayName(effect.invasionKind)} 격퇴 성공! {CityLine(effect)}");
                return;
            }
            if (effect.StageChanged)
            {
                Sfx.Play(Sfx.Id.LevelUp, 0.7f);
                GameEvents.ShowToast($"{CityLine(effect)}  ·  {RiskStages.Mood(effect.stage)}");
                return;
            }
            if (effect.riskAfter != effect.riskBefore) GameEvents.ShowToast("순찰 성과!  " + CityLine(effect));
        }
    }

    /// <summary>
    /// 도시 위험도 (docs/CITY_RISK_SYSTEM.md). 서버가 계산한 값을 받아 두고 화면에 내어 준다.
    /// 클라이언트에는 위험도를 바꾸는 요청이 없다: 전투 결과, 시설 복구, 도시 기금을 서버에 알릴 뿐이다.
    /// </summary>
    public static class CityRiskService
    {
        private const float StaleSeconds = 30f;

        /// <summary>마지막으로 받은 전국 위험도. 아직 받지 못했으면 null.</summary>
        public static CityRisksDto Risks { get; private set; }
        /// <summary>내용이 바뀔 때마다 1씩 오른다.</summary>
        public static int Version { get; private set; }
        public static bool Loading { get; private set; }
        /// <summary>지금 접속한 계정의 전국 위험도를 받아 두었는가.</summary>
        public static bool Loaded => Risks != null && _owner == GameSession.Current;

        private static float _loadedAt = -999f;
        private static GameSession _owner;

        public static void ResetState()
        {
            Risks = null;
            Loading = false;
            _loadedAt = -999f;
            _owner = null;
            Version++;
        }

        public static CityStatusDto Of(string regionCode)
        {
            if (Risks == null || regionCode == null || _owner != GameSession.Current) return null;
            return Risks.Find(regionCode);
        }

        /// <summary>소속 도시의 현황. 이방인이거나 아직 받지 못했으면 null.</summary>
        public static CityStatusDto Home
        {
            get
            {
                GameSession session = GameSession.Current;
                return session != null && session.IsResident ? Of(session.Me.regionCode) : null;
            }
        }

        /// <summary>전투가 반영되는 도시(지원 중이면 그 도시)의 현황.</summary>
        public static CityStatusDto CombatCity
        {
            get
            {
                GameSession session = GameSession.Current;
                return session != null ? Of(session.CombatRegionCode) : null;
            }
        }

        public static void Refresh(Action done = null)
        {
            GameSession session = GameSession.Current;
            if (session == null) return;
            if (Loading)
            {
                done?.Invoke();
                return;
            }
            Loading = true;
            session.Backend.GetCityRisks(result =>
            {
                Loading = false;
                if (GameSession.Current != session) return;
                if (result.ok)
                {
                    Risks = result.data;
                    _owner = session;
                    _loadedAt = Time.unscaledTime;
                    Version++;
                }
                else
                {
                    Debug.LogWarning($"도시 위험도를 받지 못함: {result.errorCode} {result.message}");
                }
                done?.Invoke();
            });
        }

        /// <summary>받은 지 오래됐으면 다시 받는다. 매 프레임 불러도 된다.</summary>
        public static void RefreshIfStale()
        {
            GameSession session = GameSession.Current;
            if (session == null || Loading) return;
            if (_owner == session && Time.unscaledTime - _loadedAt < StaleSeconds) return;
            // 실패해도 곧바로 다시 묻지 않는다.
            _loadedAt = Time.unscaledTime;
            _owner = session;
            Refresh();
        }

        /// <summary>전투 응답에 딸려 온 값으로 받아 둔 현황을 고친다 (다음에 전체를 다시 받을 때까지).</summary>
        public static void Apply(CityEffectDto effect)
        {
            CityStatusDto city = effect != null ? Of(effect.regionCode) : null;
            if (city == null) return;
            city.risk = effect.riskAfter;
            city.stage = effect.stage;
            if (!string.IsNullOrEmpty(effect.invasionId))
            {
                city.invasionProgress = effect.invasionProgress;
                city.invasionRequired = effect.invasionRequired;
                if (effect.invasionRepelled) city.invasionKind = null;
            }
            Version++;
        }

        public static void GetDetail(string regionCode, Action<ApiResult<CityDetailDto>> done)
        {
            GameSession session = GameSession.Current;
            if (session == null) return;
            session.Backend.GetCityDetail(regionCode, done);
        }

        /// <summary>피해 입은 시설을 GC로 고친다. 값과 효과는 서버가 정한다.</summary>
        public static void Repair(string facilityCode, Action<ApiResult<CityActionDto>> done)
        {
            GameSession session = GameSession.Current;
            if (session == null) return;
            session.Backend.RepairFacility(facilityCode, CombatService.NewRequestId(), result =>
            {
                if (result.ok && GameSession.Current == session)
                {
                    session.ApplyCityAction(result.data);
                    Apply(result.data.city);
                }
                done?.Invoke(result);
            });
        }

        /// <summary>도시 공용 방어시설에 기금을 낸다. 개인 자산이 되지 않는다.</summary>
        public static void Fund(string structureCode, int amountGc, Action<ApiResult<CityActionDto>> done)
        {
            GameSession session = GameSession.Current;
            if (session == null) return;
            session.Backend.FundCityStructure(structureCode, amountGc, CombatService.NewRequestId(), result =>
            {
                if (result.ok && GameSession.Current == session)
                {
                    session.ApplyCityAction(result.data);
                    Apply(result.data.city);
                }
                done?.Invoke(result);
            });
        }

        // 단계의 색: 안전 초록, 주의 노랑, 위험 주황, 긴급 빨강.
        // 색만으로 구분하지 않는다: 언제나 모양(RiskStages.Icon)과 글자(DisplayName)를 함께 쓴다.
        private static readonly Color SafeColor = new Color(0.30f, 0.78f, 0.42f);
        private static readonly Color CautionColor = new Color(0.98f, 0.84f, 0.22f);
        private static readonly Color DangerColor = new Color(0.98f, 0.56f, 0.16f);
        private static readonly Color EmergencyColor = new Color(0.92f, 0.26f, 0.22f);

        public static Color StageColor(string stage)
        {
            switch (stage)
            {
                case RiskStages.Caution: return CautionColor;
                case RiskStages.Danger: return DangerColor;
                case RiskStages.Emergency: return EmergencyColor;
                default: return SafeColor;
            }
        }

        /// <summary>"▲ 주의" 처럼 모양과 글자를 함께 쓴 단계 표시.</summary>
        public static string StageBadge(string stage) => $"{RiskStages.Icon(stage)} {RiskStages.DisplayName(stage)}";
    }

    /// <summary>
    /// 지역 경쟁과 도시 간 지원 (docs/REGIONAL_COMPETITION.md). 지원을 나가도 소속 도시는 바뀌지 않는다.
    /// </summary>
    public static class RegionService
    {
        public static RankingsDto Rankings { get; private set; }
        public static SupportListDto Support { get; private set; }
        public static int Version { get; private set; }

        private static GameSession _owner;

        public static void ResetState()
        {
            Rankings = null;
            Support = null;
            _owner = null;
            Version++;
        }

        private static void Own(GameSession session)
        {
            if (_owner == session) return;
            _owner = session;
            Rankings = null;
            Support = null;
        }

        public static string ShortName(string regionCode)
        {
            if (string.IsNullOrEmpty(regionCode)) return null;
            GameSession session = GameSession.Current;
            RegionCatalog catalog = session != null ? session.Backend.Regions : RegionCatalog.Default;
            return catalog.ShortName(regionCode);
        }

        public static string FullName(string regionCode)
        {
            if (string.IsNullOrEmpty(regionCode)) return null;
            GameSession session = GameSession.Current;
            RegionCatalog catalog = session != null ? session.Backend.Regions : RegionCatalog.Default;
            return catalog.FullName(regionCode);
        }

        public static void RefreshRankings(Action done = null)
        {
            GameSession session = GameSession.Current;
            if (session == null) return;
            session.Backend.GetRankings(result =>
            {
                if (GameSession.Current != session) return;
                Own(session);
                if (result.ok)
                {
                    Rankings = result.data;
                    Version++;
                }
                else
                {
                    Debug.LogWarning($"지역 랭킹을 받지 못함: {result.errorCode} {result.message}");
                }
                done?.Invoke();
            });
        }

        public static void RefreshSupport(Action done = null)
        {
            GameSession session = GameSession.Current;
            if (session == null) return;
            session.Backend.GetSupportRequests(result =>
            {
                if (GameSession.Current != session) return;
                Own(session);
                if (result.ok)
                {
                    Support = result.data;
                    Version++;
                }
                else
                {
                    Debug.LogWarning($"지원 요청을 받지 못함: {result.errorCode} {result.message}");
                }
                done?.Invoke();
            });
        }

        /// <summary>긴급 도시의 지원 요청에 나선다. 그 뒤의 방어전·FPS 승리가 그 도시에 반영된다.</summary>
        public static void JoinSupport(string requestId, Action<ApiResult<SupportResultDto>> done)
        {
            GameSession session = GameSession.Current;
            if (session == null) return;
            session.Backend.JoinSupport(requestId, result =>
            {
                if (result.ok && GameSession.Current == session)
                {
                    session.ApplySupport(result.data);
                    Version++;
                }
                done?.Invoke(result);
            });
        }

        public static void LeaveSupport(Action<ApiResult<SupportResultDto>> done)
        {
            GameSession session = GameSession.Current;
            if (session == null) return;
            session.Backend.LeaveSupport(result =>
            {
                if (result.ok && GameSession.Current == session)
                {
                    session.ApplySupport(result.data);
                    Version++;
                }
                done?.Invoke(result);
            });
        }
    }

    /// <summary>
    /// 가방 (docs/ITEM_SYSTEM.md). 세 전투 모드가 같은 가방을 쓴다.
    /// 물건을 사고 쓰는 일은 기존 ItemActions 가 맡고, 여기서는 전투 쪽에서 필요한 것만 내어 준다.
    /// </summary>
    public static class InventorySystem
    {
        public static int CountOf(string itemCode)
        {
            GameSession session = GameSession.Current;
            return session != null ? session.CountOf(itemCode) : 0;
        }

        public static bool Owns(string itemCode) => CountOf(itemCode) > 0;

        public static int RepairKits => CountOf(ItemCodes.RepairKit);

        /// <summary>수리 키트를 쓴다 (숫자 3). 실제 회복량과 재고는 서버 응답으로 확정된다.</summary>
        public static void UseRepairKit() => ItemActions.UseRepairKit();
    }

    /// <summary>
    /// 전투 장비 (docs/EQUIPMENT_SYSTEM.md): 구매, 강화, 모드별 장착.
    /// 장비는 플레이어마다 한 번만 저장하고, 모드마다 따로 사지 않는다.
    /// 무료 기본 장비로 모든 전투에 나설 수 있고, 강화 효과는 단계마다 10%로 작다.
    /// </summary>
    public static class EquipmentSystem
    {
        public const string MenuTag = "gear";

        public static GearDef Equipped(string mode, string slot)
        {
            GameSession session = GameSession.Current;
            return session?.EquippedGear(mode, slot);
        }

        public static int Level(GearDef def)
        {
            GameSession session = GameSession.Current;
            return session != null ? session.GearLevel(def) : 0;
        }

        public static void Buy(GearDef def, Action<bool> done = null)
        {
            GameSession session = GameSession.Current;
            if (session == null || def == null) return;
            session.Backend.BuyGear(def.code, CombatService.NewRequestId(), result =>
            {
                if (GameSession.Current != session) return;
                if (result.ok)
                {
                    session.ApplyGear(result.data);
                    Sfx.Play(Sfx.Id.Build);
                    GameEvents.ShowToast($"{Korean.WithEul(def.name)} 샀어요!");
                }
                else
                {
                    ShowError(result.errorCode, result.message);
                }
                done?.Invoke(result.ok);
            });
        }

        public static void Upgrade(GearDef def, Action<bool> done = null)
        {
            GameSession session = GameSession.Current;
            if (session == null || def == null) return;
            session.Backend.UpgradeGear(def.code, CombatService.NewRequestId(), result =>
            {
                if (GameSession.Current != session) return;
                if (result.ok)
                {
                    session.ApplyGear(result.data);
                    Sfx.Play(Sfx.Id.LevelUp, 0.7f);
                    GameEvents.ShowToast($"{def.name} 강화 {result.data.level}단계!");
                }
                else
                {
                    ShowError(result.errorCode, result.message);
                }
                done?.Invoke(result.ok);
            });
        }

        public static void Equip(string mode, GearDef def, Action<bool> done = null)
        {
            GameSession session = GameSession.Current;
            if (session == null || def == null) return;
            session.Backend.EquipGear(mode, def.slot, def.code, result =>
            {
                if (GameSession.Current != session) return;
                if (result.ok)
                {
                    session.ApplyGear(result.data);
                    Sfx.Play(Sfx.Id.Interact);
                    GameEvents.ShowToast($"{Korean.WithEul(def.name)} 장착했어요.");
                }
                else
                {
                    ShowError(result.errorCode, result.message);
                }
                done?.Invoke(result.ok);
            });
        }

        private static void ShowError(string code, string message)
        {
            if (code == ApiErrors.InsufficientFunds) GameEvents.ShowToast("GC가 모자라요.");
            else if (!string.IsNullOrEmpty(message)) GameEvents.ShowToast(message);
            else GameEvents.ShowToast("처리하지 못했어요. 잠시 뒤에 다시 시도해 주세요.");
        }

        // ── 장비 상점 화면 (숫자 키로 고르는 선택 메뉴) ─────────

        /// <summary>전투 장비 상점: 칸 고르기 → 장비 고르기 → 사기·강화·장착.</summary>
        public static void OpenShop(Action onBack = null)
        {
            GameSession session = GameSession.Current;
            if (session == null) return;
            const string mode = CombatModeCodes.Fps;

            var options = new List<ChoiceOption>();
            for (int i = 0; i < GearSlots.All.Length; i++)
            {
                string slot = GearSlots.All[i];
                GearDef equipped = session.EquippedGear(mode, slot);
                options.Add(new ChoiceOption
                {
                    label = GearSlots.DisplayName(slot),
                    cost = equipped != null ? equipped.name : "비어 있음",
                    detail = SlotHint(slot),
                    onChosen = () => OpenSlot(slot, onBack),
                });
            }
            ChoiceMenu.Instance.Open("전투 장비",
                $"시내 긴급 출동(FPS)에 들고 갈 장비예요.\n기본 장비만으로도 임무를 끝낼 수 있어요.   가진 GC  {session.Me.gc:N0}",
                options.ToArray());
            ChoiceMenu.Instance.Tag = MenuTag;
            ChoiceMenu.Instance.OnCancelled = onBack;
        }

        private static string SlotHint(string slot)
        {
            switch (slot)
            {
                case GearSlots.Primary: return "마우스 왼쪽 버튼으로 쏘는 무기예요.";
                case GearSlots.Gadget: return "Q 키로 쓰는 장치예요.";
                default: return "F 키로 쓰는 장비예요.";
            }
        }

        private static void OpenSlot(string slot, Action onBack)
        {
            GameSession session = GameSession.Current;
            if (session == null) return;
            const string mode = CombatModeCodes.Fps;
            GearDef equipped = session.EquippedGear(mode, slot);

            var options = new List<ChoiceOption>();
            List<GearDef> all = session.Defs.combat.gear;
            for (int i = 0; i < all.Count && options.Count < ChoiceMenu.MaxOptions; i++)
            {
                GearDef def = all[i];
                if (def.slot != slot || !def.UsableIn(mode)) continue;
                int level = session.GearLevel(def);
                string state = def.comingSoon ? "준비 중"
                    : level <= 0 ? (session.Me.level < def.unlockLevel ? $"레벨 {def.unlockLevel}부터" : $"{def.priceGc:N0} GC")
                    : def == equipped ? $"장착 중 · {level}단계"
                    : $"보유 · {level}단계";
                options.Add(new ChoiceOption
                {
                    label = def.name,
                    cost = state,
                    detail = def.description,
                    enabled = !def.comingSoon,
                    onChosen = () => OpenGear(def, onBack),
                });
            }
            if (options.Count == 0) return;
            ChoiceMenu.Instance.Open(GearSlots.DisplayName(slot), SlotHint(slot), options.ToArray());
            ChoiceMenu.Instance.Tag = MenuTag;
            ChoiceMenu.Instance.OnCancelled = () => OpenShop(onBack);
        }

        private static void OpenGear(GearDef def, Action onBack)
        {
            GameSession session = GameSession.Current;
            if (session == null) return;
            const string mode = CombatModeCodes.Fps;
            int level = session.GearLevel(def);
            bool owned = level > 0;
            bool equipped = session.EquippedGear(mode, def.slot) == def;
            Action reopen = () => OpenGear(def, onBack);

            var options = new List<ChoiceOption>();
            if (!owned)
            {
                string reason = GearRules.CheckBuy(def, session.Me.gear, session.Me.level, session.Me.gc);
                options.Add(new ChoiceOption
                {
                    label = "사기",
                    cost = $"{def.priceGc:N0} GC",
                    detail = reason == null ? "한 번 사면 계속 쓸 수 있어요."
                        : reason == GearReasons.LevelTooLow ? $"레벨 {def.unlockLevel}부터 살 수 있어요."
                        : GearReasons.Message(reason),
                    enabled = reason == null,
                    onChosen = () => Buy(def, _ => reopen()),
                });
            }
            else
            {
                options.Add(new ChoiceOption
                {
                    label = equipped ? "장착 중" : "장착하기",
                    detail = equipped ? "지금 들고 있는 장비예요." : "다음 출동부터 이 장비를 써요.",
                    enabled = !equipped,
                    onChosen = () => Equip(mode, def, _ => reopen()),
                });
                if (def.maxLevel > 1 && def.upgradeBaseGc > 0)
                {
                    string reason = GearRules.CheckUpgrade(def, session.Me.gear, session.Me.gc);
                    bool max = reason == GearReasons.MaxLevel;
                    options.Add(new ChoiceOption
                    {
                        label = max ? "강화 완료" : $"강화 ({level} → {level + 1}단계)",
                        cost = max ? "" : $"{GearRules.UpgradeCost(def, level):N0} GC",
                        detail = max ? "더 강화할 수 없어요."
                            : reason != null ? GearReasons.Message(reason)
                            : def.IsWeapon ? $"피해가 {GearRules.UpgradeBonusPercent}% 늘어요."
                            : $"다시 쓸 수 있을 때까지의 시간이 {GearRules.UpgradeBonusPercent}% 줄어요.",
                        enabled = reason == null,
                        onChosen = () => Upgrade(def, _ => reopen()),
                    });
                }
            }
            options.Add(new ChoiceOption
            {
                label = "뒤로",
                detail = "장비 목록으로 돌아가요.",
                onChosen = () => OpenSlot(def.slot, onBack),
            });

            ChoiceMenu.Instance.Open(def.name, def.description + "\n" + Stats(def, Mathf.Max(1, level)), options.ToArray());
            ChoiceMenu.Instance.Tag = MenuTag;
            ChoiceMenu.Instance.OnCancelled = () => OpenSlot(def.slot, onBack);
        }

        /// <summary>장비의 성능 한 줄. 화면 표시용이고, 실제 판정에 쓰는 값과 같은 식이다.</summary>
        public static string Stats(GearDef def, int level)
        {
            if (def == null) return "";
            if (def.IsWeapon)
            {
                string shape = def.coneDegrees > 0f ? $"   범위 {def.coneDegrees:0}°" : "";
                return $"피해 {GearRules.Damage(def, level)}   초당 {def.attacksPerSecond:0.#}발   사거리 {def.range:0}m   탄창 {def.clipSize}{shape}";
            }
            string text = $"다시 쓰기까지 {GearRules.Cooldown(def, level):0.#}초";
            if (def.effectSeconds > 0f) text += $"   지속 {def.effectSeconds:0.#}초";
            if (def.effectRadius > 0f) text += $"   범위 {def.effectRadius:0}m";
            if (def.effectValue > 0) text += $"   효과 {GearRules.EffectValue(def, level)}";
            return text;
        }
    }
}
