using System;
using System.Collections.Generic;
using CityGuardians.Domain;
using NUnit.Framework;

namespace CityGuardians.Tests
{
    /// <summary>v8.0 공통 전투 정의: 세 전투 모드, 방어시설 15종, 전투 장비 8종 (docs/COMBAT_SYSTEM.md).</summary>
    public sealed class CombatDefsTests
    {
        private GameDefs _defs;

        [SetUp]
        public void SetUp() => _defs = DefaultDefs.Create();

        [Test]
        public void ThreeModes_EachWithItsOwnCamera()
        {
            Assert.AreEqual(3, _defs.combat.modes.Count);
            foreach (string code in CombatModeCodes.All)
            {
                CombatModeDef mode = _defs.combat.FindMode(code);
                Assert.IsNotNull(mode, code);
                Assert.IsFalse(mode.closed, "세 모드 모두 열려 있다");
                Assert.IsFalse(string.IsNullOrEmpty(mode.controls), "조작 안내가 있어야 한다");
            }
            Assert.AreEqual(CameraKinds.ThirdPerson, _defs.combat.FindMode(CombatModeCodes.Hunt).camera);
            Assert.AreEqual(CameraKinds.FirstPerson, _defs.combat.FindMode(CombatModeCodes.Fps).camera);
            Assert.IsFalse(CombatModeCodes.IsKnown("pvp"), "PvP 모드는 없다");
        }

        [Test]
        public void FifteenStructures_InFourTiers()
        {
            var perTier = new Dictionary<string, int>();
            foreach (TowerDef tower in _defs.towers)
            {
                perTier.TryGetValue(tower.tier, out int count);
                perTier[tower.tier] = count + 1;
            }
            Assert.AreEqual(15, _defs.towers.Count);
            Assert.AreEqual(3, perTier[StructureTiers.Basic]);
            Assert.AreEqual(4, perTier[StructureTiers.Mid]);
            Assert.AreEqual(4, perTier[StructureTiers.Advanced]);
            Assert.AreEqual(4, perTier[StructureTiers.Special]);
        }

        [Test]
        public void Structures_SeparateBattleBuilt_PermanentlyOwned_AndCityShared()
        {
            TowerDef machineGun = _defs.FindTower(TowerCodes.MachineGun);
            Assert.AreEqual(StructureOwnership.Battle, machineGun.ownership);
            Assert.AreEqual(0, machineGun.unlockGc, "기본 타워는 무료다");
            Assert.IsTrue(machineGun.IsBuildable);

            TowerDef laser = _defs.FindTower(TowerCodes.Laser);
            Assert.AreEqual(StructureOwnership.Permanent, laser.ownership);
            Assert.Greater(laser.unlockGc, 0);
            Assert.IsTrue(laser.IsBuildable);

            TowerDef shield = _defs.FindTower(TowerCodes.EnergyShield);
            Assert.IsTrue(shield.IsCityOwned);
            Assert.IsFalse(shield.IsBuildable, "도시 공용 시설은 개인이 전투에서 세우지 않는다");
            Assert.Greater(shield.cityGoalGc, 0);
            Assert.Greater(shield.cityPower, 0);

            // 아직 동작하지 않는 시설은 세울 수 없다고 표시되어 있어야 한다.
            Assert.IsTrue(_defs.FindTower(TowerCodes.Wall).comingSoon);
            Assert.IsFalse(_defs.FindTower(TowerCodes.Wall).IsBuildable);
        }

        [Test]
        public void EveryCoreBattle_CanBePlayedWithFreeGear()
        {
            var nothing = new List<OwnedGear>();
            var noLoadout = new List<LoadoutEntry>();

            GearDef weapon = GearRules.Equipped(_defs.combat, nothing, noLoadout, CombatModeCodes.Fps, GearSlots.Primary);
            Assert.IsNotNull(weapon, "FPS 임무에 무료 기본 무기가 있어야 한다");
            Assert.IsTrue(weapon.IsFree);
            Assert.Greater(weapon.damage, 0);

            foreach (FpsMissionDef mission in _defs.combat.fpsMissions)
                Assert.AreEqual(1, mission.unlockLevel, "첫 임무는 누구나 나설 수 있다");

            bool freeTower = false;
            foreach (TowerDef tower in _defs.towers) freeTower |= tower.IsBuildable && tower.unlockGc == 0 && tower.unlockLevel <= 1;
            Assert.IsTrue(freeTower, "방어전에 무료 기본 타워가 있어야 한다");
        }

        [Test]
        public void EightGear_AllPricedInGc_NoneNeedsRealMoney()
        {
            Assert.AreEqual(8, _defs.combat.gear.Count);
            var codes = new HashSet<string>();
            foreach (GearDef gear in _defs.combat.gear)
            {
                Assert.IsTrue(codes.Add(gear.code), "장비 코드가 겹친다: " + gear.code);
                Assert.IsTrue(GearSlots.IsKnown(gear.slot), gear.code);
                Assert.GreaterOrEqual(gear.priceGc, 0);
                Assert.IsTrue(gear.UsableIn(CombatModeCodes.Fps), gear.code);
            }
        }
    }

    /// <summary>전투 장비의 구매·강화·장착 규칙 (docs/EQUIPMENT_SYSTEM.md).</summary>
    public sealed class GearRulesTests
    {
        private CombatDefs _combat;
        private GearDef _blaster;
        private GearDef _rifle;

        [SetUp]
        public void SetUp()
        {
            _combat = DefaultDefs.Create().combat;
            _blaster = _combat.FindGear(GearCodes.EnergyBlaster);
            _rifle = _combat.FindGear(GearCodes.PrecisionRifle);
        }

        [Test]
        public void FreeGear_IsOwnedFromTheStart_PaidGearIsNot()
        {
            var owned = new List<OwnedGear>();
            Assert.AreEqual(1, GearRules.LevelOf(owned, _blaster));
            Assert.IsTrue(GearRules.Owns(owned, _blaster));
            Assert.AreEqual(0, GearRules.LevelOf(owned, _rifle));
            Assert.IsFalse(GearRules.Owns(owned, _rifle));
        }

        [Test]
        public void Buy_ChecksOwnership_Level_AndBalance_InThatOrder()
        {
            var owned = new List<OwnedGear>();
            Assert.AreEqual(GearReasons.Unknown, GearRules.CheckBuy(null, owned, 99, 99999));
            Assert.AreEqual(GearReasons.AlreadyOwned, GearRules.CheckBuy(_blaster, owned, 99, 99999));
            Assert.AreEqual(GearReasons.LevelTooLow, GearRules.CheckBuy(_rifle, owned, _rifle.unlockLevel - 1, 99999));
            Assert.AreEqual(GearReasons.InsufficientGc, GearRules.CheckBuy(_rifle, owned, _rifle.unlockLevel, _rifle.priceGc - 1));
            Assert.IsNull(GearRules.CheckBuy(_rifle, owned, _rifle.unlockLevel, _rifle.priceGc));

            owned.Add(new OwnedGear(_rifle.code, 1));
            Assert.AreEqual(GearReasons.AlreadyOwned, GearRules.CheckBuy(_rifle, owned, 99, 99999));
        }

        [Test]
        public void Upgrade_CostsMoreEachLevel_AndStopsAtTheLimit()
        {
            var owned = new List<OwnedGear>();
            Assert.AreEqual(_blaster.upgradeBaseGc, GearRules.UpgradeCost(_blaster, 1));
            Assert.AreEqual(_blaster.upgradeBaseGc * 2, GearRules.UpgradeCost(_blaster, 2));

            Assert.AreEqual(GearReasons.NotOwned, GearRules.CheckUpgrade(_rifle, owned, 99999));
            Assert.AreEqual(GearReasons.InsufficientGc, GearRules.CheckUpgrade(_blaster, owned, _blaster.upgradeBaseGc - 1));
            Assert.IsNull(GearRules.CheckUpgrade(_blaster, owned, _blaster.upgradeBaseGc));

            owned.Add(new OwnedGear(_blaster.code, _blaster.maxLevel));
            Assert.AreEqual(GearReasons.MaxLevel, GearRules.CheckUpgrade(_blaster, owned, 99999));
        }

        [Test]
        public void Upgrade_IsASmallBonus_NotAGuaranteedWin()
        {
            Assert.AreEqual(_blaster.damage, GearRules.Damage(_blaster, 1));
            int best = GearRules.Damage(_blaster, _blaster.maxLevel);
            Assert.Greater(best, _blaster.damage);
            // 끝까지 강화해도 피해는 기본의 1.5배를 넘지 않는다.
            Assert.LessOrEqual(best * 2, _blaster.damage * 3);

            GearDef heal = _combat.FindGear(GearCodes.HealDevice);
            Assert.Less(GearRules.Cooldown(heal, 2), GearRules.Cooldown(heal, 1));
            Assert.Greater(GearRules.Cooldown(heal, heal.maxLevel), heal.cooldownSeconds / 2f);
        }

        [Test]
        public void Equip_NeedsTheRightModeSlotAndOwnership()
        {
            var owned = new List<OwnedGear>();
            Assert.AreEqual(GearReasons.NotOwned, GearRules.CheckEquip(_rifle, owned, CombatModeCodes.Fps, GearSlots.Primary));
            owned.Add(new OwnedGear(_rifle.code, 1));
            Assert.IsNull(GearRules.CheckEquip(_rifle, owned, CombatModeCodes.Fps, GearSlots.Primary));
            Assert.AreEqual(GearReasons.WrongSlot, GearRules.CheckEquip(_rifle, owned, CombatModeCodes.Fps, GearSlots.Gadget));
            Assert.AreEqual(GearReasons.WrongMode, GearRules.CheckEquip(_rifle, owned, CombatModeCodes.Hunt, GearSlots.Primary));
            Assert.AreEqual(GearReasons.WrongMode, GearRules.CheckEquip(_rifle, owned, "pvp", GearSlots.Primary));
        }

        [Test]
        public void Equipped_FallsBackToTheFreeGear_WhenTheChoiceIsNoLongerValid()
        {
            var owned = new List<OwnedGear>();
            var loadouts = new List<LoadoutEntry>();
            Assert.AreSame(_blaster, GearRules.Equipped(_combat, owned, loadouts, CombatModeCodes.Fps, GearSlots.Primary));

            // 가지지 않은 장비가 장착 기록에 있어도 (조작된 저장 파일) 기본 장비가 쓰인다.
            GearRules.SetEntry(loadouts, CombatModeCodes.Fps, GearSlots.Primary, _rifle.code);
            Assert.AreSame(_blaster, GearRules.Equipped(_combat, owned, loadouts, CombatModeCodes.Fps, GearSlots.Primary));

            owned.Add(new OwnedGear(_rifle.code, 1));
            Assert.AreSame(_rifle, GearRules.Equipped(_combat, owned, loadouts, CombatModeCodes.Fps, GearSlots.Primary));

            // 지원 장비 칸에는 무료 기본 장비가 없다: 비어 있다.
            Assert.IsNull(GearRules.Equipped(_combat, owned, loadouts, CombatModeCodes.Fps, GearSlots.Support));
        }
    }

    /// <summary>FPS 임무 결과 검증 (docs/FPS_MODE.md). 보상은 서버가 임무 정의로 계산한다.</summary>
    public sealed class FpsRulesTests
    {
        private FpsMissionDef _mission;

        [SetUp]
        public void SetUp() => _mission = DefaultDefs.Create().combat.FindFpsMission(FpsMissionCodes.Downtown1);

        internal static FpsReport WinReport(FpsMissionDef mission)
        {
            var report = new FpsReport
            {
                result = BattleResults.Win,
                facilitiesSaved = mission.facilityCount,
                civiliansSaved = mission.civilianCount,
            };
            var seen = new HashSet<string>();
            foreach (WaveGroupDef group in mission.groups)
                if (seen.Add(group.enemyCode)) report.kills.Add(new KillCount(group.enemyCode, mission.CountOf(group.enemyCode)));
            return report;
        }

        private float EnoughTime => _mission.minSeconds + 1f;

        [Test]
        public void Mission_IsWhatTheDocsSay()
        {
            Assert.AreEqual(18, _mission.TotalEnemies);
            Assert.AreEqual(3, _mission.facilityCount);
            Assert.AreEqual(4, _mission.civilianCount);
            Assert.Greater(_mission.timeLimitSeconds, _mission.minSeconds);
        }

        [Test]
        public void Win_PaysBaseFirstBonusAndRescueBonuses()
        {
            FpsVerdict first = FpsRules.Evaluate(_mission, WinReport(_mission), EnoughTime, false, true);
            Assert.IsTrue(first.accepted, first.rejectReason);
            Assert.IsTrue(first.firstWin);
            int rescue = _mission.civilianBonusGc * _mission.civilianCount + _mission.facilityBonusGc * _mission.facilityCount;
            Assert.AreEqual(_mission.winGc + _mission.firstWinBonusGc + rescue, first.rewardGc);
            Assert.AreEqual(_mission.winXp, first.rewardXp);

            FpsVerdict again = FpsRules.Evaluate(_mission, WinReport(_mission), EnoughTime, true, true);
            Assert.IsFalse(again.firstWin);
            Assert.AreEqual(_mission.winGc + rescue, again.rewardGc);
        }

        [Test]
        public void Win_AfterTheDailyLimit_GivesXpButNoGc()
        {
            FpsVerdict verdict = FpsRules.Evaluate(_mission, WinReport(_mission), EnoughTime, true, false);
            Assert.IsTrue(verdict.accepted);
            Assert.AreEqual(0, verdict.rewardGc);
            Assert.AreEqual(_mission.winXp, verdict.rewardXp);
        }

        [Test]
        public void Win_TooFast_TooLate_OrWithoutAnyFacility_IsRefused()
        {
            Assert.AreEqual(FpsRejectReasons.TooFast,
                FpsRules.Evaluate(_mission, WinReport(_mission), _mission.minSeconds - 1f, false, true).rejectReason);
            Assert.AreEqual(FpsRejectReasons.TooLate,
                FpsRules.Evaluate(_mission, WinReport(_mission), _mission.timeLimitSeconds + FpsRules.LateGraceSeconds + 1f, false, true).rejectReason);

            FpsReport noFacility = WinReport(_mission);
            noFacility.facilitiesSaved = 0;
            FpsVerdict verdict = FpsRules.Evaluate(_mission, noFacility, EnoughTime, false, true);
            Assert.IsFalse(verdict.accepted);
            Assert.AreEqual(0, verdict.rewardGc);
            Assert.AreEqual(0, verdict.rewardXp);
        }

        [Test]
        public void Win_ClaimingAllFacilitiesWithoutTheKills_IsRefused()
        {
            // 아무도 쓰러뜨리지 않았는데 시설이 모두 멀쩡할 수는 없다.
            var report = new FpsReport { result = BattleResults.Win, facilitiesSaved = _mission.facilityCount, civiliansSaved = 0 };
            Assert.AreEqual(FpsRejectReasons.Inconsistent, FpsRules.Evaluate(_mission, report, EnoughTime, false, true).rejectReason);
        }

        [Test]
        public void Counts_MustFitTheMission()
        {
            FpsReport stranger = WinReport(_mission);
            stranger.kills.Add(new KillCount(EnemyCodes.ConstructionMecha, 1));
            Assert.AreEqual(FpsRejectReasons.UnknownEnemy, FpsRules.Evaluate(_mission, stranger, EnoughTime, false, true).rejectReason);

            FpsReport tooMany = WinReport(_mission);
            tooMany.kills[0] = new KillCount(tooMany.kills[0].enemyCode, tooMany.kills[0].count + 1);
            Assert.AreEqual(FpsRejectReasons.TooManyKills, FpsRules.Evaluate(_mission, tooMany, EnoughTime, false, true).rejectReason);

            FpsReport split = WinReport(_mission);
            split.kills.Add(new KillCount(split.kills[0].enemyCode, 1));
            Assert.AreEqual(FpsRejectReasons.InvalidCount, FpsRules.Evaluate(_mission, split, EnoughTime, false, true).rejectReason);

            FpsReport civilians = WinReport(_mission);
            civilians.civiliansSaved = _mission.civilianCount + 1;
            Assert.AreEqual(FpsRejectReasons.InvalidCivilians, FpsRules.Evaluate(_mission, civilians, EnoughTime, false, true).rejectReason);

            FpsReport facilities = WinReport(_mission);
            facilities.facilitiesSaved = _mission.facilityCount + 1;
            Assert.AreEqual(FpsRejectReasons.InvalidFacilities, FpsRules.Evaluate(_mission, facilities, EnoughTime, false, true).rejectReason);

            Assert.AreEqual(FpsRejectReasons.InvalidResult, FpsRules.Evaluate(_mission, null, EnoughTime, false, true).rejectReason);
            Assert.AreEqual(FpsRejectReasons.InvalidResult,
                FpsRules.Evaluate(_mission, new FpsReport { result = "draw" }, EnoughTime, false, true).rejectReason);
        }

        [Test]
        public void Defeat_IsRecordedWithNoReward()
        {
            var report = new FpsReport { result = BattleResults.Lose };
            FpsVerdict verdict = FpsRules.Evaluate(_mission, report, 5f, false, true);
            Assert.IsTrue(verdict.accepted, verdict.rejectReason);
            Assert.AreEqual(0, verdict.rewardGc);
            Assert.AreEqual(0, verdict.rewardXp);
            Assert.IsFalse(verdict.firstWin);
        }
    }

    /// <summary>도시 위험도 계산 (docs/CITY_RISK_SYSTEM.md).</summary>
    public sealed class CityRiskRulesTests
    {
        private RiskPolicy _policy;

        [SetUp]
        public void SetUp() => _policy = RiskPolicy.CreateDefault();

        [Test]
        public void Stages_FollowTheFourBands()
        {
            Assert.AreEqual(RiskStages.Safe, CityRiskRules.StageOf(0, _policy));
            Assert.AreEqual(RiskStages.Safe, CityRiskRules.StageOf(2499, _policy));
            Assert.AreEqual(RiskStages.Caution, CityRiskRules.StageOf(2500, _policy));
            Assert.AreEqual(RiskStages.Caution, CityRiskRules.StageOf(4999, _policy));
            Assert.AreEqual(RiskStages.Danger, CityRiskRules.StageOf(5000, _policy));
            Assert.AreEqual(RiskStages.Danger, CityRiskRules.StageOf(7499, _policy));
            Assert.AreEqual(RiskStages.Emergency, CityRiskRules.StageOf(7500, _policy));
            Assert.AreEqual(RiskStages.Emergency, CityRiskRules.StageOf(10000, _policy));
        }

        [Test]
        public void EveryStage_HasAnIconAndAName_SoColorIsNotTheOnlyCue()
        {
            var icons = new HashSet<string>();
            var names = new HashSet<string>();
            foreach (string stage in RiskStages.All)
            {
                Assert.IsTrue(icons.Add(RiskStages.Icon(stage)), "단계마다 모양이 달라야 한다");
                Assert.IsTrue(names.Add(RiskStages.DisplayName(stage)), "단계마다 이름이 달라야 한다");
                Assert.IsFalse(string.IsNullOrEmpty(RiskStages.Mood(stage)));
            }
        }

        [Test]
        public void Risk_StaysBetweenZeroAndAHundred()
        {
            Assert.AreEqual(10000, CityRiskRules.ApplySystem(9900, RiskReasons.ZeroEvent, 1, _policy).after);
            Assert.AreEqual(0, CityRiskRules.ApplySystem(300, RiskReasons.NpcHeld, 3, _policy).after);
            Assert.AreEqual(0, CityRiskRules.ApplyExact(100, RiskReasons.AdminAdjust, -99999, _policy).after);
            Assert.AreEqual(100, CityRiskRules.ToPoints(123456));
        }

        [Test]
        public void SystemEvents_ScaleWithSeverity()
        {
            RiskChange small = CityRiskRules.ApplySystem(2000, RiskReasons.InvasionStarted, 1, _policy);
            RiskChange large = CityRiskRules.ApplySystem(2000, RiskReasons.InvasionStarted, 3, _policy);
            Assert.AreEqual(_policy.Delta(RiskReasons.InvasionStarted), small.Delta);
            Assert.AreEqual(_policy.Delta(RiskReasons.InvasionStarted) * 3, large.Delta);
            Assert.IsTrue(large.StageChanged);
            Assert.AreEqual(RiskStages.Safe, large.stageBefore);
            Assert.AreEqual(RiskStages.Caution, large.stageAfter);
        }

        [Test]
        public void PlayerActivity_OnlyEverLowersRisk()
        {
            RiskChange win = CityRiskRules.ApplyPlayer(5000, RiskReasons.DefenseWon, 100, _policy, 1, 0, out int reduced);
            Assert.AreEqual(_policy.Delta(RiskReasons.DefenseWon), win.Delta);
            Assert.AreEqual(-win.Delta, reduced);

            // 올리는 사유를 플레이어의 활동으로 넣어도 위험도는 오르지 않는다.
            RiskChange raise = CityRiskRules.ApplyPlayer(5000, RiskReasons.InvasionStarted, 100, _policy, 1, 0, out reduced);
            Assert.AreEqual(0, raise.Delta);
            Assert.AreEqual(0, reduced);

            // 정책을 잘못 고쳐 내리는 사유가 양수가 되어도 마찬가지다.
            _policy.SetDelta(RiskReasons.DefenseWon, 900);
            Assert.AreEqual(0, CityRiskRules.ApplyPlayer(5000, RiskReasons.DefenseWon, 100, _policy, 1, 0, out reduced).Delta);
        }

        [Test]
        public void PlayerActivity_IsDilutedInCrowdedCities()
        {
            int norm = _policy.activePlayerNorm;
            Assert.AreEqual(100, CityRiskRules.ScalePercent(_policy, 1));
            Assert.AreEqual(100, CityRiskRules.ScalePercent(_policy, norm));
            Assert.AreEqual(50, CityRiskRules.ScalePercent(_policy, norm * 2));
            Assert.AreEqual(10, CityRiskRules.ScalePercent(_policy, norm * 10));

            int small = CityRiskRules.ApplyPlayer(5000, RiskReasons.DefenseWon, 100, _policy, norm, 0, out _).Delta;
            int crowded = CityRiskRules.ApplyPlayer(5000, RiskReasons.DefenseWon, 100, _policy, norm * 10, 0, out _).Delta;
            Assert.AreEqual(small / 10, crowded, "열 배 큰 도시에서는 한 사람의 몫이 열에 하나다");
        }

        [Test]
        public void PlayerActivity_StopsAtTheDailyCap()
        {
            int cap = _policy.dailyPlayerReduceCap * 100;
            int risk = 9000;
            int reduced = 0;
            for (int i = 0; i < 50; i++)
            {
                RiskChange change = CityRiskRules.ApplyPlayer(risk, RiskReasons.FpsRepelled, 100, _policy, 1, reduced, out reduced);
                risk = change.after;
            }
            Assert.AreEqual(cap, reduced);
            Assert.AreEqual(9000 - cap, risk);
        }

        [Test]
        public void Patrol_CountsForLess_ThanDefendingAgainstAnInvasion()
        {
            int full = CityRiskRules.ApplyPlayer(5000, RiskReasons.DefenseWon, 100, _policy, 1, 0, out _).Delta;
            int patrol = CityRiskRules.ApplyPlayer(5000, RiskReasons.DefenseWon, _policy.patrolPercent, _policy, 1, 0, out _).Delta;
            Assert.Less(full, patrol);
            Assert.Less(patrol, 0);
        }

        [Test]
        public void DailyStabilize_IsAShareOfTheCurrentRisk()
        {
            Assert.AreEqual(0, CityRiskRules.DailyStabilizeDelta(0, _policy));
            Assert.AreEqual(-(8000 * _policy.dailyStabilizePercent / 100), CityRiskRules.DailyStabilizeDelta(8000, _policy));
        }

        [Test]
        public void DefensePower_CountsPointsUpToACap_PlusCityStructures()
        {
            int garrison = CityRiskRules.DefensePower(_policy, 0, 0);
            Assert.AreEqual(_policy.baseGarrisonPower, garrison);
            Assert.AreEqual(garrison + 50, CityRiskRules.DefensePower(_policy, 500, 0));
            Assert.AreEqual(garrison + _policy.defensePointsPowerCap, CityRiskRules.DefensePower(_policy, 99999999, 0));
            Assert.AreEqual(garrison + 40, CityRiskRules.DefensePower(_policy, 0, 40));
        }
    }

    /// <summary>침공 이벤트 판정 (docs/CITY_RISK_SYSTEM.md 5장).</summary>
    public sealed class InvasionRulesTests
    {
        private RiskPolicy _policy;

        [SetUp]
        public void SetUp() => _policy = RiskPolicy.CreateDefault();

        [Test]
        public void Roll_IsRepeatable_AndWithinRange()
        {
            for (int tick = 0; tick < 200; tick++)
            {
                int a = InvasionRules.Roll(7, "26350", tick, 1, 1000);
                Assert.AreEqual(a, InvasionRules.Roll(7, "26350", tick, 1, 1000));
                Assert.GreaterOrEqual(a, 0);
                Assert.Less(a, 1000);
            }
            Assert.AreEqual(0, InvasionRules.Roll(7, "26350", 1, 1, 0));
        }

        [Test]
        public void Frequency_FollowsThePolicy()
        {
            IReadOnlyList<RegionDef> regions = RegionCatalog.Default.Regions;
            int started = 0, total = 0;
            for (int tick = 0; tick < 100; tick++)
                for (int i = 0; i < regions.Count; i++)
                {
                    total++;
                    if (InvasionRules.TryStart(_policy, regions[i].code, tick, 1) != null) started++;
                }
            double permille = started * 1000.0 / total;
            Assert.That(permille, Is.InRange(_policy.invasionChancePermille * 0.8, _policy.invasionChancePermille * 1.2),
                "침공 빈도가 정책의 값과 비슷해야 한다");

            _policy.invasionChancePermille = 0;
            Assert.IsNull(InvasionRules.TryStart(_policy, "26350", 5, 1), "빈도를 0으로 두면 침공이 없다");
            _policy.invasionChancePermille = 1000;
            Assert.IsNotNull(InvasionRules.TryStart(_policy, "26350", 5, 1));
        }

        [Test]
        public void StartedInvasion_HasAStableId_AKind_AndATimeLimit()
        {
            _policy.invasionChancePermille = 1000;
            InvasionEvent a = InvasionRules.TryStart(_policy, "26350", 42, 1);
            InvasionEvent b = InvasionRules.TryStart(_policy, "26350", 42, 1);
            Assert.AreEqual(a.id, b.id, "같은 판정을 두 번 돌려도 같은 침공이다");
            Assert.AreEqual(a.kind, b.kind);
            Assert.IsTrue(a.IsActive);
            Assert.That(a.severity, Is.InRange(1, 3));
            DateTime start = DateTime.Parse(a.startedAt, null, System.Globalization.DateTimeStyles.RoundtripKind);
            DateTime end = DateTime.Parse(a.endsAt, null, System.Globalization.DateTimeStyles.RoundtripKind);
            Assert.AreEqual(_policy.invasionMinutes, (end - start).TotalMinutes, 0.001);
            Assert.AreEqual(InvasionRules.TickStart(42, _policy), start.ToUniversalTime());
            Assert.AreEqual(42, InvasionRules.TickOf(start.ToUniversalTime().AddMinutes(1), _policy));
        }

        [Test]
        public void KindShares_FollowThePolicy()
        {
            _policy.invasionChancePermille = 1000;
            _policy.intrusionPermille = 1000;
            _policy.zeroEventPermille = 0;
            Assert.AreEqual(InvasionKinds.Intrusion, InvasionRules.TryStart(_policy, "26350", 1, 1).kind);
            _policy.intrusionPermille = 0;
            Assert.AreEqual(InvasionKinds.Outskirts, InvasionRules.TryStart(_policy, "26350", 1, 1).kind);
            _policy.zeroEventPermille = 1000;
            InvasionEvent zero = InvasionRules.TryStart(_policy, "26350", 1, 1);
            Assert.AreEqual(InvasionKinds.Zero, zero.kind);
            Assert.AreEqual(3, zero.severity);
        }

        [Test]
        public void EachKind_IsRepelledByItsOwnMode()
        {
            var outskirts = new InvasionEvent { kind = InvasionKinds.Outskirts, status = InvasionStatus.Active };
            var intrusion = new InvasionEvent { kind = InvasionKinds.Intrusion, status = InvasionStatus.Active };
            var zero = new InvasionEvent { kind = InvasionKinds.Zero, status = InvasionStatus.Active };

            Assert.IsTrue(InvasionRules.Counts(outskirts, CombatModeCodes.Defense));
            Assert.IsFalse(InvasionRules.Counts(outskirts, CombatModeCodes.Fps));
            Assert.IsTrue(InvasionRules.Counts(intrusion, CombatModeCodes.Fps));
            Assert.IsFalse(InvasionRules.Counts(intrusion, CombatModeCodes.Defense));
            Assert.IsTrue(InvasionRules.Counts(zero, CombatModeCodes.Defense));
            Assert.IsTrue(InvasionRules.Counts(zero, CombatModeCodes.Fps));
            Assert.IsFalse(InvasionRules.Counts(zero, CombatModeCodes.Hunt));
            Assert.IsFalse(InvasionRules.Counts(null, CombatModeCodes.Defense));

            outskirts.status = InvasionStatus.Repelled;
            Assert.IsFalse(InvasionRules.Counts(outskirts, CombatModeCodes.Defense), "끝난 침공에는 더 쌓이지 않는다");
        }

        [Test]
        public void RequiredWins_GrowWithSeverityAndCitySize()
        {
            int norm = _policy.activePlayerNorm;
            Assert.AreEqual(1, InvasionRules.RequiredWins(_policy, 1, 0));
            Assert.AreEqual(1, InvasionRules.RequiredWins(_policy, 1, norm));
            Assert.AreEqual(3, InvasionRules.RequiredWins(_policy, 3, norm));
            Assert.AreEqual(2, InvasionRules.RequiredWins(_policy, 1, norm + 1));
            Assert.AreEqual(30, InvasionRules.RequiredWins(_policy, 3, norm * 10));
        }

        [Test]
        public void NpcGarrison_HoldsBetterWithDefensePower_AndWorseAgainstBigInvasions()
        {
            var small = new InvasionEvent { regionCode = "26350", severity = 1, required = 1 };
            var big = new InvasionEvent { regionCode = "26350", severity = 3, required = 3 };
            int basePower = _policy.baseGarrisonPower;

            Assert.AreEqual(_policy.npcHoldBasePercent, InvasionRules.NpcHoldPercent(_policy, basePower, small));
            Assert.Less(InvasionRules.NpcHoldPercent(_policy, basePower, big), InvasionRules.NpcHoldPercent(_policy, basePower, small));
            Assert.Greater(InvasionRules.NpcHoldPercent(_policy, basePower + 200, small), InvasionRules.NpcHoldPercent(_policy, basePower, small));
            Assert.LessOrEqual(InvasionRules.NpcHoldPercent(_policy, 999999, small), 95, "아무리 강해도 확실한 것은 없다");

            // 플레이어가 조금이라도 싸웠다면 NPC가 막기 쉬워진다.
            big.progress = 2;
            var untouched = new InvasionEvent { regionCode = "26350", severity = 3, required = 3 };
            Assert.Greater(InvasionRules.NpcHoldPercent(_policy, basePower, big), InvasionRules.NpcHoldPercent(_policy, basePower, untouched));

            string result = InvasionRules.ResolveExpired(_policy, small, basePower, 1, 10);
            Assert.AreEqual(result, InvasionRules.ResolveExpired(_policy, small, basePower, 1, 10));
            Assert.That(result, Is.EqualTo(InvasionStatus.NpcHeld).Or.EqualTo(InvasionStatus.Failed));
        }

        [Test]
        public void Reasons_MatchTheKind()
        {
            Assert.AreEqual(RiskReasons.InvasionStarted, InvasionRules.StartReason(InvasionKinds.Outskirts));
            Assert.AreEqual(RiskReasons.ZeroEvent, InvasionRules.StartReason(InvasionKinds.Zero));
            Assert.AreEqual(RiskReasons.DefenseFailed, InvasionRules.FailReason(InvasionKinds.Outskirts));
            Assert.AreEqual(RiskReasons.IntrusionFailed, InvasionRules.FailReason(InvasionKinds.Intrusion));
            Assert.AreEqual(RiskReasons.SpecialMission, InvasionRules.RepelReason(InvasionKinds.Zero));
            Assert.AreEqual(RiskReasons.InvasionRepelled, InvasionRules.RepelReason(InvasionKinds.Outskirts));
            Assert.AreEqual(RiskReasons.FpsRepelled, InvasionRules.WinReason(CombatModeCodes.Fps));
            Assert.AreEqual(RiskReasons.DefenseWon, InvasionRules.WinReason(CombatModeCodes.Defense));
        }
    }

    /// <summary>도시 간 지원과 지역 랭킹 (docs/REGIONAL_COMPETITION.md).</summary>
    public sealed class RegionalRulesTests
    {
        private const string Haeundae = RegionCodes.Haeundae;
        private const string BusanJung = "26110";
        private const string SeoulJongno = "11110";

        private RiskPolicy _policy;
        private RankingWeights _weights;

        [SetUp]
        public void SetUp()
        {
            _policy = RiskPolicy.CreateDefault();
            _weights = new RankingWeights();
        }

        [Test]
        public void SupportRequest_OpensInAnEmergency_AndClosesWhenRiskFalls()
        {
            Assert.IsFalse(SupportRules.ShouldOpen(_policy, 7499, false));
            Assert.IsTrue(SupportRules.ShouldOpen(_policy, 7500, false));
            Assert.IsFalse(SupportRules.ShouldOpen(_policy, 9000, true), "한 도시에 요청은 하나다");
            Assert.IsFalse(SupportRules.IsResolved(_policy, 5000));
            Assert.IsTrue(SupportRules.IsResolved(_policy, 4999));
        }

        [Test]
        public void OnlyResidentsOfNearbyCities_CanJoin()
        {
            RegionCatalog catalog = RegionCatalog.Default;
            var request = new SupportRequest { id = "sup_1", regionCode = BusanJung, status = SupportStatus.Open };

            Assert.IsNull(SupportRules.CheckJoin(request, Haeundae, catalog));
            Assert.AreEqual(SupportReasons.NotResident, SupportRules.CheckJoin(request, null, catalog));
            Assert.AreEqual(SupportReasons.TooFar, SupportRules.CheckJoin(request, SeoulJongno, catalog));
            Assert.AreEqual(SupportReasons.OwnCity, SupportRules.CheckJoin(request, BusanJung, catalog));
            Assert.AreEqual(SupportReasons.NotOpen, SupportRules.CheckJoin(null, Haeundae, catalog));

            request.status = SupportStatus.Resolved;
            Assert.AreEqual(SupportReasons.NotOpen, SupportRules.CheckJoin(request, Haeundae, catalog));
        }

        [Test]
        public void Ranking_IsPerActivePlayer_SoBigCitiesGetNoAutomaticEdge()
        {
            var small = new RegionStats
            {
                regionCode = "a", activePlayers = 5, riskCenti = 3000, playerWins = 10, playerAttempts = 12,
                contribution = 250, supportGiven = 50, invasionSeverityTotal = 4, repelledSeverity = 2,
            };
            var big = new RegionStats
            {
                regionCode = "b", activePlayers = 500, riskCenti = 3000, playerWins = 1000, playerAttempts = 1200,
                contribution = 25000, supportGiven = 5000, invasionSeverityTotal = 400, repelledSeverity = 200,
            };
            RankingScore a = RankingRules.Score(small, _weights);
            RankingScore b = RankingRules.Score(big, _weights);
            Assert.AreEqual(a.contribution, b.contribution);
            Assert.AreEqual(a.cooperation, b.cooperation);
            Assert.AreEqual(a.response, b.response);
            Assert.AreEqual(a.safety, b.safety);
            Assert.That(Math.Abs(a.total - b.total), Is.LessThan(300), "같은 실력이면 도시의 크기와 관계없이 점수가 비슷하다");
        }

        [Test]
        public void Ranking_RewardsRepellingBigInvasions()
        {
            var easy = new RegionStats { regionCode = "a", activePlayers = 5, invasionSeverityTotal = 6, repelledSeverity = 1 };
            var hard = new RegionStats { regionCode = "b", activePlayers = 5, invasionSeverityTotal = 6, repelledSeverity = 3 };
            Assert.Greater(RankingRules.Score(hard, _weights).response, RankingRules.Score(easy, _weights).response);
            Assert.AreEqual(50, RankingRules.Score(new RegionStats { regionCode = "c", activePlayers = 5 }, _weights).response,
                "침공이 없던 도시는 중간값이다");
        }

        [Test]
        public void Ranking_PutsNpcOnlyCitiesBehindPlayerDefendedOnes()
        {
            var npcOnly = new RegionStats { regionCode = "npc", activePlayers = 0, riskCenti = 0, npcHeldCount = 20 };
            var players = new RegionStats { regionCode = "ply", activePlayers = 3, riskCenti = 6000, playerWins = 1, playerAttempts = 5 };
            List<RankingScore> ranked = RankingRules.Rank(new List<RegionStats> { npcOnly, players }, _weights);

            Assert.AreEqual("ply", ranked[0].regionCode);
            Assert.AreEqual(1, ranked[0].rank);
            Assert.IsTrue(ranked[0].playerDefended);
            Assert.AreEqual("npc", ranked[1].regionCode);
            Assert.IsFalse(ranked[1].playerDefended);
            Assert.Greater(ranked[1].safety, ranked[0].safety, "NPC가 지킨 도시가 더 안전해도 순위는 뒤다");
        }

        [Test]
        public void Ranking_HasNoPaymentTerm()
        {
            // 랭킹에 들어가는 값은 위험도, 승률, 기여도, 침공 대응, 협동뿐이다. 결제와 관계된 칸이 없다.
            foreach (System.Reflection.FieldInfo field in typeof(RegionStats).GetFields())
            {
                string name = field.Name.ToLowerInvariant();
                Assert.IsFalse(name.Contains("cash") || name.Contains("paid") || name.Contains("purchase") || name.Contains("gc"),
                    "랭킹 집계에 결제·GC 항목이 있다: " + field.Name);
            }
        }
    }
}
