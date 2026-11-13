using System;
using CityGuardians.Domain;

namespace CityGuardians.Services
{
    /// <summary>
    /// 접속 중인 플레이어의 상태. 원본은 서버이고, 여기 있는 값은 서버 응답을 받아 둔 사본이다.
    /// 화면은 Changed 이벤트를 받아 갱신한다.
    /// </summary>
    public sealed class GameSession
    {
        public static GameSession Current { get; private set; }

        public IGameBackend Backend { get; }
        public GameDefs Defs => Backend.Defs;
        public MeDto Me { get; private set; }

        /// <summary>서버에 아직 보고하지 않았거나 응답을 기다리는 사냥 보상의 추정치. 화면 표시용.</summary>
        public int PendingGc { get; private set; }

        public event Action Changed;
        /// <summary>레벨이 올랐을 때. 인자는 새 레벨.</summary>
        public event Action<int> LeveledUp;

        private GameSession(IGameBackend backend, MeDto me)
        {
            Backend = backend;
            Me = me;
        }

        public static GameSession Start(IGameBackend backend, MeDto me)
        {
            if (backend == null) throw new ArgumentNullException(nameof(backend));
            if (me == null) throw new ArgumentNullException(nameof(me));
            Current = new GameSession(backend, me);
            return Current;
        }

        public static void End()
        {
            Current = null;
        }

        public int TutorialStep
        {
            get
            {
                string text = Me.GetFlag(FlagKeys.TutorialStep, "0");
                return int.TryParse(text, out int step) ? step : 0;
            }
        }

        /// <summary>튜토리얼 단계를 앞으로만 옮긴다. 이미 지난 단계로는 돌아가지 않는다.</summary>
        public void AdvanceTutorial(int step)
        {
            if (step <= TutorialStep) return;
            SetFlag(FlagKeys.TutorialStep, step.ToString());
        }

        public void SetFlag(string key, string value)
        {
            Backend.SetFlag(key, value, result =>
            {
                if (result.ok) ApplyMe(result.data);
            });
        }

        public WeaponDef EquippedMelee => Defs.FindWeapon(Me.equippedMelee) ?? Defs.FindWeapon(WeaponCodes.SparkBaton);

        /// <summary>장착한 원거리 무기. 없으면 null.</summary>
        public WeaponDef EquippedRanged =>
            string.IsNullOrEmpty(Me.equippedRanged) ? null : Defs.FindWeapon(Me.equippedRanged);

        /// <summary>손에 든 무기 칸 (EquipSlots). 원거리 무기가 없으면 언제나 근접이다.</summary>
        public string ActiveSlot =>
            EquippedRanged != null && Me.GetFlag(FlagKeys.ActiveSlot) == EquipSlots.Ranged
                ? EquipSlots.Ranged
                : EquipSlots.Melee;

        /// <summary>지금 손에 든 무기.</summary>
        public WeaponDef ActiveWeapon => ActiveSlot == EquipSlots.Ranged ? EquippedRanged : EquippedMelee;

        /// <summary>손에 든 무기를 바꾼다. 그 칸에 무기가 없으면 false.</summary>
        public bool SelectSlot(string slot)
        {
            if (slot != EquipSlots.Melee && slot != EquipSlots.Ranged) return false;
            if (slot == EquipSlots.Ranged && EquippedRanged == null) return false;
            if (ActiveSlot == slot) return true;
            SetFlag(FlagKeys.ActiveSlot, slot);
            return ActiveSlot == slot;
        }

        public int CountOf(string itemCode) => InventoryRules.CountOf(Me.inventory, itemCode);

        public bool Owns(string itemCode) => CountOf(itemCode) > 0;

        /// <summary>머리나 몸(EquipSlots.Head, Body)에 입은 물건. 없으면 null.</summary>
        public ItemDef Worn(string slot)
        {
            string code = slot == EquipSlots.Head ? Me.equippedHead : slot == EquipSlots.Body ? Me.equippedBody : null;
            return string.IsNullOrEmpty(code) ? null : Defs.FindItem(code);
        }

        /// <summary>입은 방어구로 줄어드는 피해의 비율(%).</summary>
        public int Defense => InventoryRules.TotalDefense(Defs, Me.equippedHead, Me.equippedBody);

        /// <summary>넣어 둔 그 가구의 수.</summary>
        public int StoredFurniture(string itemCode) => RoomRules.CountStored(Me.furniture, itemCode);

        public void ApplyPurchase(PurchaseResultDto purchase)
        {
            if (purchase == null) throw new ArgumentNullException(nameof(purchase));
            Me.gc = purchase.gc;
            Me.inventory = new System.Collections.Generic.List<ItemStack>(purchase.inventory);
            if (purchase.furniture != null)
                Me.furniture = new System.Collections.Generic.List<FurniturePiece>(purchase.furniture);
            Changed?.Invoke();
        }

        /// <summary>되판 결과를 반영한다. 입고 있던 물건을 팔았다면 그 칸도 빈다.</summary>
        public void ApplySale(SellResultDto sale)
        {
            if (sale == null) throw new ArgumentNullException(nameof(sale));
            Me.gc = sale.gc;
            Me.inventory = new System.Collections.Generic.List<ItemStack>(sale.inventory);
            Me.furniture = new System.Collections.Generic.List<FurniturePiece>(sale.furniture);
            Me.equippedMelee = sale.equippedMelee;
            Me.equippedRanged = sale.equippedRanged;
            Me.equippedHead = sale.equippedHead;
            Me.equippedBody = sale.equippedBody;
            Changed?.Invoke();
        }

        public void ApplyEquip(EquipResultDto equip)
        {
            if (equip == null) throw new ArgumentNullException(nameof(equip));
            Me.equippedMelee = equip.equippedMelee;
            Me.equippedRanged = equip.equippedRanged;
            Me.equippedHead = equip.equippedHead;
            Me.equippedBody = equip.equippedBody;
            Changed?.Invoke();
        }

        /// <summary>가구를 놓거나 넣어 둔 결과를 반영한다.</summary>
        public void ApplyRoom(RoomDto room)
        {
            if (room == null) throw new ArgumentNullException(nameof(room));
            Me.furniture = new System.Collections.Generic.List<FurniturePiece>(room.furniture);
            Changed?.Invoke();
        }

        public void ApplyItemUse(ItemUseResultDto use)
        {
            if (use == null) throw new ArgumentNullException(nameof(use));
            Me.inventory = new System.Collections.Generic.List<ItemStack>(use.inventory);
            Changed?.Invoke();
        }

        public void ApplyMe(MeDto me)
        {
            Me = me ?? throw new ArgumentNullException(nameof(me));
            Changed?.Invoke();
        }

        /// <summary>은행을 쓸 수 있는 레벨인가. 실제 판정은 서버가 한다.</summary>
        public bool BankUnlocked => Me.level >= Defs.bank.unlockLevel;

        /// <summary>은행 응답의 잔액을 반영한다.</summary>
        public void ApplyBank(BankDto bank)
        {
            if (bank == null) throw new ArgumentNullException(nameof(bank));
            Me.gc = bank.gc;
            Me.bankGc = bank.bankGc;
            Me.bankInterestTotal = bank.interestTotal;
            Me.termGc = bank.termGc;
            Me.termMaturedCount = bank.termMaturedCount;
            Changed?.Invoke();
        }

        /// <summary>
        /// 아직 알리지 않은 이자. 알렸다고 표시하고 그 금액을 돌려준다. 없으면 0.
        /// 서버가 준 합계와 "어디까지 알렸나" 플래그의 차이라서, 껐다 켜도 같은 이자를 두 번 알리지 않는다.
        /// </summary>
        public long TakeUnseenInterest()
        {
            long seen = long.TryParse(Me.GetFlag(FlagKeys.BankInterestSeen, "0"), out long value) ? value : 0;
            long unseen = Me.bankInterestTotal - seen;
            if (unseen <= 0) return 0;
            // SetFlag의 응답이 Me를 통째로 바꾸므로 합계를 먼저 적어 둔다.
            long total = Me.bankInterestTotal;
            SetFlag(FlagKeys.BankInterestSeen, total.ToString());
            return unseen;
        }

        /// <summary>아직 알리지 않은 정기예금 만기의 수. 알렸다고 표시하고 그 수를 돌려준다. 없으면 0.</summary>
        public long TakeUnseenTermMaturities()
        {
            long seen = long.TryParse(Me.GetFlag(FlagKeys.TermMaturedSeen, "0"), out long value) ? value : 0;
            long unseen = Me.termMaturedCount - seen;
            if (unseen <= 0) return 0;
            long total = Me.termMaturedCount;
            SetFlag(FlagKeys.TermMaturedSeen, total.ToString());
            return unseen;
        }

        public void AddPendingGc(int amount)
        {
            PendingGc = Math.Max(0, PendingGc + amount);
            Changed?.Invoke();
        }

        public void ApplyHuntReport(HuntReportDto report, int pendingToClear)
        {
            PendingGc = Math.Max(0, PendingGc - pendingToClear);
            Me.gc = report.gc;
            Me.level = report.level;
            Me.xp = report.xp;
            Me.xpNext = report.xpNext;
            Me.hpMax = report.hpMax;
            Me.dailyHuntGc = report.dailyHuntGc;
            Me.dailyHuntGcLimit = report.dailyHuntGcLimit;
            // 드롭이 있었을 때만 인벤토리가 함께 온다.
            if (report.inventory != null && report.drops != null && report.drops.Count > 0)
                Me.inventory = new System.Collections.Generic.List<ItemStack>(report.inventory);
            ApplyCityEffect(report.city, false);
            Changed?.Invoke();
            if (report.levelsGained > 0) LeveledUp?.Invoke(report.level);
        }

        public void ApplyQuestReward(QuestRewardDto reward)
        {
            Me.gc = reward.gc;
            Changed?.Invoke();
        }

        /// <summary>어느 도시의 주민(가디언)인가. 아니면 이방인이다.</summary>
        public bool IsResident => Me.residency == ResidencyStatus.Resident && !string.IsNullOrEmpty(Me.regionCode);

        /// <summary>전입신고 결과를 반영한다. 이름, 소속, 잔액이 함께 바뀐다.</summary>
        public void ApplyResidency(ResidencyResultDto result)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            ApplyMe(result.me);
        }

        public bool IsTowerUnlocked(string towerCode) => Me.unlockedTowers != null && Me.unlockedTowers.Contains(towerCode);

        public bool HasCleared(string stageCode) => Me.clearedStages != null && Me.clearedStages.Contains(stageCode);

        public void ApplyBattleResult(BattleResultDto result)
        {
            Me.gc = result.gc;
            Me.level = result.level;
            Me.xp = result.xp;
            Me.xpNext = result.xpNext;
            Me.hpMax = result.hpMax;
            Me.clearedStages = new System.Collections.Generic.List<string>(result.clearedStages);
            ApplyCityEffect(result.city, false);
            Changed?.Invoke();
            if (result.levelsGained > 0) LeveledUp?.Invoke(result.level);
        }

        public void ApplyTowerUnlock(TowerUnlockDto unlock)
        {
            Me.gc = unlock.gc;
            Me.unlockedTowers = new System.Collections.Generic.List<string>(unlock.unlockedTowers);
            Changed?.Invoke();
        }

        // ── v8.0: 전투 장비, FPS 임무, 도시 (docs/COMBAT_SYSTEM.md 12장) ──
        // 세 전투 모드가 같은 계정·레벨·GC·장비를 쓴다. 모드마다 따로 저장하는 값은 없다.

        /// <summary>전투 결과가 도시에 반영됐을 때. 화면이 위험도 변화를 알리는 데 쓴다.</summary>
        public event Action<CityEffectDto> CityEffectApplied;

        /// <summary>마지막으로 받은 도시 반영 결과. 없으면 null.</summary>
        public CityEffectDto LastCityEffect { get; private set; }

        /// <summary>지원 나가 있는 도시. 없으면 null.</summary>
        public string SupportRegionCode => string.IsNullOrEmpty(Me.supportRegionCode) ? null : Me.supportRegionCode;

        /// <summary>전투가 반영되는 도시: 지원 나가 있으면 그 도시, 아니면 소속 도시. 이방인이면 null.</summary>
        public string CombatRegionCode => !IsResident ? null : SupportRegionCode ?? Me.regionCode;

        /// <summary>그 모드의 그 칸에 낀 장비. 고른 것이 없으면 무료 기본 장비다 (없으면 null).</summary>
        public GearDef EquippedGear(string mode, string slot) =>
            GearRules.Equipped(Defs.combat, Me.gear, Me.loadouts, mode, slot);

        /// <summary>가진 장비의 강화 단계. 기본 장비는 1, 없으면 0.</summary>
        public int GearLevel(GearDef def) => GearRules.LevelOf(Me.gear, def);

        public bool OwnsGear(GearDef def) => GearRules.Owns(Me.gear, def);

        public bool HasClearedFps(string missionCode) =>
            Me.clearedFpsMissions != null && Me.clearedFpsMissions.Contains(missionCode);

        /// <summary>장비 구매·강화·장착의 결과를 반영한다.</summary>
        public void ApplyGear(GearResultDto result)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            Me.gc = result.gc;
            Me.gear = new System.Collections.Generic.List<OwnedGear>(result.gear);
            Me.loadouts = new System.Collections.Generic.List<LoadoutEntry>(result.loadouts);
            Changed?.Invoke();
        }

        public void ApplyFpsResult(FpsResultDto result)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            Me.gc = result.gc;
            Me.level = result.level;
            Me.xp = result.xp;
            Me.xpNext = result.xpNext;
            Me.hpMax = result.hpMax;
            if (result.clearedFpsMissions != null)
                Me.clearedFpsMissions = new System.Collections.Generic.List<string>(result.clearedFpsMissions);
            ApplyCityEffect(result.city, false);
            Changed?.Invoke();
            if (result.levelsGained > 0) LeveledUp?.Invoke(result.level);
        }

        /// <summary>시설 복구·도시 기금의 결과를 반영한다.</summary>
        public void ApplyCityAction(CityActionDto action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            Me.gc = action.gc;
            ApplyCityEffect(action.city, false);
            if (action.city == null && action.contribution > 0) Me.cityContribution += action.contribution;
            Changed?.Invoke();
        }

        /// <summary>지원 참가·그만두기의 결과를 반영한다. 소속 도시는 바뀌지 않는다.</summary>
        public void ApplySupport(SupportResultDto result)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            Me.supportRegionCode = result.joinedRegionCode;
            Changed?.Invoke();
        }

        /// <summary>
        /// 전투 결과에 딸려 온 도시 반영 내용을 알린다. 위험도 수치는 서버가 계산한 값이고,
        /// 클라이언트는 받아서 보여 주기만 한다.
        /// </summary>
        public void ApplyCityEffect(CityEffectDto effect, bool notifyChanged = true)
        {
            if (effect == null) return;
            LastCityEffect = effect;
            // 우리 도시에 쌓은 기여도만 내 기여도다. 지원 나가서 쌓은 것은 서버가 따로 센다.
            if (effect.actor == RiskActors.Player && effect.contribution > 0) Me.cityContribution += effect.contribution;
            if (notifyChanged) Changed?.Invoke();
            CityEffectApplied?.Invoke(effect);
        }
    }
}
