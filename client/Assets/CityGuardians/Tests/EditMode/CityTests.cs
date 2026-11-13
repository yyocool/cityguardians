using System;
using System.Collections.Generic;
using System.Reflection;
using CityGuardians.Domain;
using CityGuardians.Services;
using NUnit.Framework;

namespace CityGuardians.Tests
{
    /// <summary>v8.0 모의 서버 테스트의 공통 도우미.</summary>
    internal static class CityKit
    {
        public const string Haeundae = RegionCodes.Haeundae;
        public const string BusanJung = "26110";
        public const string SeoulJongno = "11110";

        /// <summary>침공이 저절로 일어나지 않는 정의. 테스트가 필요한 사건만 만든다.</summary>
        public static GameDefs QuietDefs()
        {
            GameDefs defs = MockKit.Defs();
            defs.combat.risk.invasionChancePermille = 0;
            defs.combat.risk.initialSpread = 0;
            return defs;
        }

        /// <summary>모든 도시에 외곽 침공(규모는 판정에 따름)이 곧바로 일어나는 정의.</summary>
        public static GameDefs InvadedDefs(string kind = InvasionKinds.Outskirts)
        {
            GameDefs defs = QuietDefs();
            defs.combat.risk.invasionChancePermille = 1000;
            defs.combat.risk.zeroEventPermille = kind == InvasionKinds.Zero ? 1000 : 0;
            defs.combat.risk.intrusionPermille = kind == InvasionKinds.Intrusion ? 1000 : 0;
            return defs;
        }

        public static void Register(LocalMockBackend backend, string region = Haeundae, string nickname = "푸른고래")
        {
            ApiResult<ResidencyResultDto> result = null;
            backend.RegisterResidency(region, nickname, r => result = r);
            MockKit.Ok(result);
        }

        public static CityRisksDto Risks(LocalMockBackend backend)
        {
            ApiResult<CityRisksDto> result = null;
            backend.GetCityRisks(r => result = r);
            return MockKit.Ok(result);
        }

        public static CityDetailDto Detail(LocalMockBackend backend, string region)
        {
            ApiResult<CityDetailDto> result = null;
            backend.GetCityDetail(region, r => result = r);
            return MockKit.Ok(result);
        }

        public static RankingsDto Rankings(LocalMockBackend backend)
        {
            ApiResult<RankingsDto> result = null;
            backend.GetRankings(r => result = r);
            return MockKit.Ok(result);
        }

        public static SupportListDto Supports(LocalMockBackend backend)
        {
            ApiResult<SupportListDto> result = null;
            backend.GetSupportRequests(r => result = r);
            return MockKit.Ok(result);
        }

        public static ApiResult<SupportResultDto> Join(LocalMockBackend backend, string requestId)
        {
            ApiResult<SupportResultDto> result = null;
            backend.JoinSupport(requestId, r => result = r);
            Assert.IsNotNull(result, "응답 콜백이 불리지 않았다");
            return result;
        }

        public static CombatOverviewDto Overview(LocalMockBackend backend)
        {
            ApiResult<CombatOverviewDto> result = null;
            backend.GetCombatOverview(r => result = r);
            return MockKit.Ok(result);
        }

        public static ApiResult<GearResultDto> BuyGear(LocalMockBackend backend, string gear, string requestId)
        {
            ApiResult<GearResultDto> result = null;
            backend.BuyGear(gear, requestId, r => result = r);
            Assert.IsNotNull(result, "응답 콜백이 불리지 않았다");
            return result;
        }

        public static ApiResult<GearResultDto> UpgradeGear(LocalMockBackend backend, string gear, string requestId)
        {
            ApiResult<GearResultDto> result = null;
            backend.UpgradeGear(gear, requestId, r => result = r);
            Assert.IsNotNull(result, "응답 콜백이 불리지 않았다");
            return result;
        }

        public static ApiResult<GearResultDto> EquipGear(LocalMockBackend backend, string mode, string slot, string gear)
        {
            ApiResult<GearResultDto> result = null;
            backend.EquipGear(mode, slot, gear, r => result = r);
            Assert.IsNotNull(result, "응답 콜백이 불리지 않았다");
            return result;
        }

        public static FpsSessionDto StartFps(LocalMockBackend backend)
        {
            ApiResult<FpsSessionDto> result = null;
            backend.StartFpsMission(FpsMissionCodes.Downtown1, r => result = r);
            return MockKit.Ok(result);
        }

        public static FpsResultDto FinishFps(LocalMockBackend backend, string session, FpsReport report)
        {
            ApiResult<FpsResultDto> result = null;
            backend.FinishFpsMission(session, report, r => result = r);
            return MockKit.Ok(result);
        }

        /// <summary>FPS 임무를 정상적으로 이긴다 (시계가 2분 간다).</summary>
        public static FpsResultDto WinFps(MockKit kit, LocalMockBackend backend)
        {
            string session = StartFps(backend).sessionId;
            kit.Clock.Advance(120);
            return FinishFps(backend, session, FpsRulesTests.WinReport(backend.Defs.combat.FindFpsMission(FpsMissionCodes.Downtown1)));
        }

        /// <summary>방어전을 정상적으로 이긴다 (시계가 5분 간다).</summary>
        public static BattleResultDto WinDefense(MockKit kit, LocalMockBackend backend)
        {
            ApiResult<BattleSessionDto> started = null;
            backend.StartBattle(StageCodes.Outskirts1, r => started = r);
            string session = MockKit.Ok(started).sessionId;
            kit.Clock.Advance(300);
            ApiResult<BattleResultDto> finished = null;
            backend.FinishBattle(session, BattleRulesTests.WinReport(backend.Defs.FindStage(StageCodes.Outskirts1)), r => finished = r);
            return MockKit.Ok(finished);
        }

        public static BattleResultDto LoseDefense(MockKit kit, LocalMockBackend backend)
        {
            ApiResult<BattleSessionDto> started = null;
            backend.StartBattle(StageCodes.Outskirts1, r => started = r);
            string session = MockKit.Ok(started).sessionId;
            kit.Clock.Advance(5);
            StageDef stage = backend.Defs.FindStage(StageCodes.Outskirts1);
            ApiResult<BattleResultDto> finished = null;
            backend.FinishBattle(session, new BattleReport { result = BattleResults.Lose, wavesCleared = 0, baseHpLeft = stage.baseHp },
                r => finished = r);
            return MockKit.Ok(finished);
        }

        public static ApiResult<CityActionDto> Repair(LocalMockBackend backend, string facility, string requestId)
        {
            ApiResult<CityActionDto> result = null;
            backend.RepairFacility(facility, requestId, r => result = r);
            Assert.IsNotNull(result, "응답 콜백이 불리지 않았다");
            return result;
        }

        public static ApiResult<CityActionDto> Fund(LocalMockBackend backend, string structure, int amount, string requestId)
        {
            ApiResult<CityActionDto> result = null;
            backend.FundCityStructure(structure, amount, requestId, r => result = r);
            Assert.IsNotNull(result, "응답 콜백이 불리지 않았다");
            return result;
        }

        public static LedgerEntry LastLedger(LocalMockBackend backend) => backend.CurrentLedger[backend.CurrentLedger.Count - 1];
    }

    /// <summary>전투 장비: 구매·강화·장착은 서버가 검증하고 원장에 남긴다 (docs/EQUIPMENT_SYSTEM.md).</summary>
    public sealed class LocalMockBackendGearTests
    {
        private MockKit _kit;
        private GameDefs _defs;

        [SetUp]
        public void SetUp()
        {
            _kit = new MockKit();
            _defs = CityKit.QuietDefs();
            // 레벨 조건은 따로 시험한다. 나머지 시험에서는 GC만 있으면 살 수 있게 둔다.
            _defs.combat.FindGear(GearCodes.Sensor).unlockLevel = 1;
            _defs.combat.FindGear(GearCodes.EmpDevice).unlockLevel = 1;
        }

        [Test]
        public void Overview_ListsTheThreeModes_AndTheDailyLimits()
        {
            LocalMockBackend backend = _kit.NewBackend(_defs);
            MockKit.Boot(backend);
            CombatOverviewDto overview = CityKit.Overview(backend);
            Assert.AreEqual(3, overview.modes.Count);
            Assert.AreEqual(_defs.combat.dailyFpsRewardLimit, overview.fpsRewardLimit);
            Assert.AreEqual(0, overview.fpsRewardsToday);
            Assert.AreEqual(_defs.combat.risk.dailyPlayerReduceCap, overview.riskReduceCap);
            Assert.AreEqual(0, overview.gear.Count, "기본 장비는 행 없이도 가진 것으로 친다");
        }

        [Test]
        public void BuyGear_NeedsGc_ChargesOnceThroughTheLedger()
        {
            LocalMockBackend backend = _kit.NewBackend(_defs);
            MockKit.Boot(backend);
            GearDef sensor = _defs.combat.FindGear(GearCodes.Sensor);

            Assert.AreEqual(ApiErrors.InsufficientFunds, MockKit.Error(CityKit.BuyGear(backend, sensor.code, "req-gear-0001")));
            Assert.AreEqual(0, backend.CurrentLedger.Count);

            _kit.Earn(backend, 400);
            int ledgerBefore = backend.CurrentLedger.Count;
            GearResultDto bought = MockKit.Ok(CityKit.BuyGear(backend, sensor.code, "req-gear-0002"));
            Assert.AreEqual(sensor.priceGc, bought.costGc);
            Assert.AreEqual(400 - sensor.priceGc, bought.gc);
            Assert.AreEqual(1, bought.level);
            Assert.IsFalse(bought.duplicate);
            Assert.AreEqual(ledgerBefore + 1, backend.CurrentLedger.Count);
            Assert.AreEqual(-sensor.priceGc, CityKit.LastLedger(backend).delta);
            Assert.AreEqual(LedgerReasons.GearPurchase, CityKit.LastLedger(backend).reason);

            // 같은 요청 번호로 다시 보내면 처음 결과를 돌려주고 다시 받지 않는다.
            GearResultDto again = MockKit.Ok(CityKit.BuyGear(backend, sensor.code, "req-gear-0002"));
            Assert.IsTrue(again.duplicate);
            Assert.AreEqual(bought.gc, again.gc);
            Assert.AreEqual(ledgerBefore + 1, backend.CurrentLedger.Count);

            // 새 요청 번호로 같은 장비를 또 살 수는 없다.
            Assert.AreEqual(ApiErrors.AlreadyOwned, MockKit.Error(CityKit.BuyGear(backend, sensor.code, "req-gear-0003")));
            // 쓴 요청 번호를 다른 장비에 다시 쓸 수 없다.
            Assert.AreEqual(ApiErrors.Conflict, MockKit.Error(CityKit.BuyGear(backend, GearCodes.EmpDevice, "req-gear-0002")));
            Assert.IsNull(backend.FindLedgerInconsistency());

            // 껐다 켜도 남는다.
            MeDto restored = MockKit.Boot(_kit.NewBackend(_defs));
            Assert.AreEqual(1, restored.gear.Count);
            Assert.AreEqual(sensor.code, restored.gear[0].code);
            Assert.AreEqual(bought.gc, restored.gc);
        }

        [Test]
        public void BuyGear_RefusesUnknown_FreeAndLevelLockedGear()
        {
            LocalMockBackend backend = _kit.NewBackend(_defs);
            MockKit.Boot(backend);
            _kit.Earn(backend, 2000);
            int ledger = backend.CurrentLedger.Count;

            Assert.AreEqual(ApiErrors.NotFound, MockKit.Error(CityKit.BuyGear(backend, "gear_nothing", "req-gear-0001")));
            Assert.AreEqual(ApiErrors.AlreadyOwned, MockKit.Error(CityKit.BuyGear(backend, GearCodes.EnergyBlaster, "req-gear-0002")));
            Assert.AreEqual(ApiErrors.InvalidRequest, MockKit.Error(CityKit.BuyGear(backend, GearCodes.Sensor, "")));

            _defs.combat.FindGear(GearCodes.SupportDrone).unlockLevel = 99;
            Assert.AreEqual(ApiErrors.Locked, MockKit.Error(CityKit.BuyGear(backend, GearCodes.SupportDrone, "req-gear-0003")));
            Assert.AreEqual(ledger, backend.CurrentLedger.Count, "거절된 구매는 GC를 건드리지 않는다");
        }

        [Test]
        public void UpgradeGear_RaisesTheLevel_AndCostsMoreEachTime()
        {
            LocalMockBackend backend = _kit.NewBackend(_defs);
            MockKit.Boot(backend);
            GearDef blaster = _defs.combat.FindGear(GearCodes.EnergyBlaster);

            Assert.AreEqual(ApiErrors.InsufficientFunds, MockKit.Error(CityKit.UpgradeGear(backend, blaster.code, "req-up-0001")));
            _kit.Earn(backend, 1000);

            GearResultDto second = MockKit.Ok(CityKit.UpgradeGear(backend, blaster.code, "req-up-0002"));
            Assert.AreEqual(2, second.level);
            Assert.AreEqual(blaster.upgradeBaseGc, second.costGc);
            Assert.AreEqual(LedgerReasons.GearUpgrade, CityKit.LastLedger(backend).reason);

            GearResultDto replay = MockKit.Ok(CityKit.UpgradeGear(backend, blaster.code, "req-up-0002"));
            Assert.IsTrue(replay.duplicate);
            Assert.AreEqual(2, replay.level, "같은 요청으로 두 번 강화되지 않는다");
            Assert.AreEqual(second.gc, replay.gc);

            GearResultDto third = MockKit.Ok(CityKit.UpgradeGear(backend, blaster.code, "req-up-0003"));
            Assert.AreEqual(3, third.level);
            Assert.AreEqual(blaster.upgradeBaseGc * 2, third.costGc);
            Assert.AreEqual(1000 - blaster.upgradeBaseGc * 3, third.gc);

            Assert.AreEqual(ApiErrors.Conflict, MockKit.Error(CityKit.UpgradeGear(backend, blaster.code, "req-up-0004")), "한도까지 강화했다");
            Assert.AreEqual(ApiErrors.NotOwned, MockKit.Error(CityKit.UpgradeGear(backend, GearCodes.PrecisionRifle, "req-up-0005")));
            Assert.IsNull(backend.FindLedgerInconsistency());
        }

        [Test]
        public void EquipGear_NeedsOwnership_AndIsRemembered()
        {
            LocalMockBackend backend = _kit.NewBackend(_defs);
            MockKit.Boot(backend);

            Assert.AreEqual(ApiErrors.NotOwned,
                MockKit.Error(CityKit.EquipGear(backend, CombatModeCodes.Fps, GearSlots.Gadget, GearCodes.EmpDevice)));
            Assert.AreEqual(ApiErrors.InvalidRequest,
                MockKit.Error(CityKit.EquipGear(backend, "pvp", GearSlots.Gadget, GearCodes.HealDevice)));
            Assert.AreEqual(ApiErrors.InvalidRequest,
                MockKit.Error(CityKit.EquipGear(backend, CombatModeCodes.Fps, GearSlots.Primary, GearCodes.HealDevice)), "칸이 맞지 않는다");

            _kit.Earn(backend, 600);
            MockKit.Ok(CityKit.BuyGear(backend, GearCodes.EmpDevice, "req-gear-0001"));
            GearResultDto equipped = MockKit.Ok(CityKit.EquipGear(backend, CombatModeCodes.Fps, GearSlots.Gadget, GearCodes.EmpDevice));
            Assert.AreEqual(1, equipped.loadouts.Count);
            Assert.AreEqual(0, equipped.costGc, "장착에는 GC가 들지 않는다");

            MeDto restored = MockKit.Boot(_kit.NewBackend(_defs));
            Assert.AreEqual(GearCodes.EmpDevice,
                GearRules.Equipped(_defs.combat, restored.gear, restored.loadouts, CombatModeCodes.Fps, GearSlots.Gadget).code);

            // 칸을 비우면 무료 기본 장비로 돌아간다.
            LocalMockBackend second = _kit.NewBackend(_defs);
            MockKit.Boot(second);
            GearResultDto cleared = MockKit.Ok(CityKit.EquipGear(second, CombatModeCodes.Fps, GearSlots.Gadget, null));
            Assert.AreEqual(0, cleared.loadouts.Count);
            Assert.AreEqual(GearCodes.HealDevice,
                GearRules.Equipped(_defs.combat, cleared.gear, cleared.loadouts, CombatModeCodes.Fps, GearSlots.Gadget).code);
        }

        [Test]
        public void GearCalls_BeforeBoot_AreUnauthorized()
        {
            LocalMockBackend backend = _kit.NewBackend(_defs);
            Assert.AreEqual(ApiErrors.Unauthorized, MockKit.Error(CityKit.BuyGear(backend, GearCodes.Sensor, "req-gear-0001")));
            ApiResult<CityRisksDto> risks = null;
            backend.GetCityRisks(r => risks = r);
            Assert.AreEqual(ApiErrors.Unauthorized, MockKit.Error(risks));
        }
    }

    /// <summary>FPS 임무: 결과는 서버가 검증하고, 보상은 한 번만, 하루 한도 안에서 준다 (docs/FPS_MODE.md).</summary>
    public sealed class LocalMockBackendFpsTests
    {
        private MockKit _kit;
        private GameDefs _defs;
        private FpsMissionDef _mission;

        [SetUp]
        public void SetUp()
        {
            _kit = new MockKit();
            _defs = CityKit.QuietDefs();
            _mission = _defs.combat.FindFpsMission(FpsMissionCodes.Downtown1);
        }

        private int FullReward(bool first) =>
            _mission.winGc + (first ? _mission.firstWinBonusGc : 0)
            + _mission.civilianBonusGc * _mission.civilianCount + _mission.facilityBonusGc * _mission.facilityCount;

        [Test]
        public void Win_PaysThroughTheLedger_AndMarksTheMissionCleared()
        {
            LocalMockBackend backend = _kit.NewBackend(_defs);
            MockKit.Boot(backend);
            string session = CityKit.StartFps(backend).sessionId;
            _kit.Clock.Advance(120);
            FpsResultDto result = CityKit.FinishFps(backend, session, FpsRulesTests.WinReport(_mission));

            Assert.IsTrue(result.accepted, result.rejectReason);
            Assert.IsTrue(result.firstWin);
            Assert.AreEqual(FullReward(true), result.rewardGc);
            Assert.AreEqual(result.rewardGc, result.gc);
            Assert.AreEqual(_mission.winXp, result.rewardXp);
            CollectionAssert.Contains(result.clearedFpsMissions, _mission.code);
            Assert.AreEqual(1, result.fpsRewardsToday);
            Assert.IsNull(result.city, "이방인의 전투는 어느 도시에도 반영되지 않는다");

            Assert.AreEqual(1, backend.CurrentLedger.Count);
            Assert.AreEqual(LedgerReasons.FpsReward, backend.CurrentLedger[0].reason);
            Assert.AreEqual(session, backend.CurrentLedger[0].reference);
            Assert.IsNull(backend.FindLedgerInconsistency());

            // 다시 보내도 처음 결과 그대로이고 한 번만 지급된다.
            FpsResultDto again = CityKit.FinishFps(backend, session, FpsRulesTests.WinReport(_mission));
            Assert.AreEqual(result.rewardGc, again.rewardGc);
            Assert.AreEqual(result.gc, again.gc);
            Assert.AreEqual(1, backend.CurrentLedger.Count);

            CollectionAssert.Contains(MockKit.Boot(_kit.NewBackend(_defs)).clearedFpsMissions, _mission.code);
        }

        [Test]
        public void ImpossiblyFastWin_IsNotPaid_AndCannotBeResent()
        {
            LocalMockBackend backend = _kit.NewBackend(_defs);
            MockKit.Boot(backend);
            string session = CityKit.StartFps(backend).sessionId;
            _kit.Clock.Advance(3);
            FpsResultDto result = CityKit.FinishFps(backend, session, FpsRulesTests.WinReport(_mission));
            Assert.IsFalse(result.accepted);
            Assert.AreEqual(FpsRejectReasons.TooFast, result.rejectReason);
            Assert.AreEqual(0, result.rewardGc);
            Assert.AreEqual(1, result.level);

            _kit.Clock.Advance(200);
            Assert.IsFalse(CityKit.FinishFps(backend, session, FpsRulesTests.WinReport(_mission)).accepted);
            Assert.AreEqual(0, backend.CurrentLedger.Count);
        }

        [Test]
        public void Defeat_PaysNothing()
        {
            LocalMockBackend backend = _kit.NewBackend(_defs);
            MockKit.Boot(backend);
            string session = CityKit.StartFps(backend).sessionId;
            _kit.Clock.Advance(40);
            FpsResultDto result = CityKit.FinishFps(backend, session, new FpsReport { result = BattleResults.Lose });
            Assert.IsTrue(result.accepted);
            Assert.AreEqual(0, result.rewardGc);
            Assert.AreEqual(0, result.rewardXp);
            Assert.AreEqual(0, result.clearedFpsMissions.Count);
            Assert.AreEqual(0, backend.CurrentLedger.Count);
        }

        [Test]
        public void DailyRewardLimit_StopsGcButNotXp_AndResetsTheNextDay()
        {
            LocalMockBackend backend = _kit.NewBackend(_defs);
            MockKit.Boot(backend);
            int limit = _defs.combat.dailyFpsRewardLimit;

            long gc = 0;
            for (int i = 0; i < limit; i++)
            {
                FpsResultDto paid = CityKit.WinFps(_kit, backend);
                Assert.AreEqual(FullReward(i == 0), paid.rewardGc);
                Assert.IsFalse(paid.rewardLimited);
                gc = paid.gc;
            }

            FpsResultDto over = CityKit.WinFps(_kit, backend);
            Assert.IsTrue(over.accepted);
            Assert.IsTrue(over.rewardLimited);
            Assert.AreEqual(0, over.rewardGc);
            Assert.AreEqual(_mission.winXp, over.rewardXp, "한도를 넘어도 경험치는 받는다");
            Assert.AreEqual(gc, over.gc);
            Assert.AreEqual(limit, backend.CurrentLedger.Count);

            _kit.Clock.NextDay();
            FpsResultDto nextDay = CityKit.WinFps(_kit, backend);
            Assert.AreEqual(FullReward(false), nextDay.rewardGc);
            Assert.IsNull(backend.FindLedgerInconsistency());
        }

        [Test]
        public void UnknownMissionOrSession_IsRefused()
        {
            LocalMockBackend backend = _kit.NewBackend(_defs);
            MockKit.Boot(backend);

            ApiResult<FpsSessionDto> start = null;
            backend.StartFpsMission("no_such_mission", r => start = r);
            Assert.AreEqual(ApiErrors.NotFound, MockKit.Error(start));

            ApiResult<FpsResultDto> finish = null;
            backend.FinishFpsMission("f_does_not_exist", new FpsReport { result = BattleResults.Win }, r => finish = r);
            Assert.AreEqual(ApiErrors.NotFound, MockKit.Error(finish));

            _mission.unlockLevel = 5;
            backend.StartFpsMission(_mission.code, r => start = r);
            Assert.AreEqual(ApiErrors.Locked, MockKit.Error(start));
        }
    }

    /// <summary>
    /// 도시 위험도: 서버가 계산하고, 이력을 남기고, 플레이어의 활동으로는 내려가기만 한다 (docs/CITY_RISK_SYSTEM.md).
    /// </summary>
    public sealed class LocalMockBackendCityRiskTests
    {
        private MockKit _kit;

        [SetUp]
        public void SetUp() => _kit = new MockKit();

        [Test]
        public void Client_HasNoWayToSendARiskValue()
        {
            // 위험도를 받는 요청은 없다. 전투 결과와 요청 번호만 보낼 수 있다.
            foreach (MethodInfo method in typeof(IGameBackend).GetMethods())
                foreach (ParameterInfo parameter in method.GetParameters())
                {
                    string name = parameter.Name.ToLowerInvariant();
                    Assert.IsFalse(name.Contains("risk") || name.Contains("delta") || name.Contains("stage") && name != "stagecode",
                        $"{method.Name}({parameter.Name}): 위험도를 클라이언트가 정하는 인자가 있다");
                }
            foreach (Type report in new[] { typeof(BattleReport), typeof(FpsReport) })
                foreach (FieldInfo field in report.GetFields())
                {
                    string name = field.Name.ToLowerInvariant();
                    Assert.IsFalse(name.Contains("risk") || name.Contains("reward") || name.Contains("gc"),
                        $"{report.Name}.{field.Name}: 전투 보고에 위험도·보상 칸이 있다");
                }
        }

        [Test]
        public void EveryCity_HasARisk_AStage_AndARank()
        {
            LocalMockBackend backend = _kit.NewBackend(CityKit.QuietDefs());
            MockKit.Boot(backend);
            CityRisksDto risks = CityKit.Risks(backend);

            Assert.AreEqual(backend.Regions.Regions.Count, risks.cities.Count);
            Assert.IsNull(risks.homeRegionCode);
            var ranks = new HashSet<int>();
            foreach (CityStatusDto city in risks.cities)
            {
                Assert.That(city.risk, Is.InRange(0, 100));
                Assert.AreEqual(CityRiskRules.StageOf(city.risk * 100, backend.Defs.combat.risk), city.stage);
                Assert.Greater(city.defensePower, 0);
                Assert.IsTrue(ranks.Add(city.rank), "순위가 겹친다");
                Assert.IsFalse(city.playerDefended, "아직 가디언이 없는 도시다");
            }
            Assert.AreEqual(backend.Defs.combat.risk.initialRisk, risks.Find(CityKit.Haeundae).risk);
            Assert.IsNull(backend.FindWorldInconsistency());
        }

        [Test]
        public void InitialRisk_VariesBetweenCities_ButStaysCalm()
        {
            GameDefs defs = CityKit.QuietDefs();
            defs.combat.risk.initialSpread = 12;
            LocalMockBackend backend = _kit.NewBackend(defs);
            MockKit.Boot(backend);

            var seen = new HashSet<int>();
            foreach (CityStatusDto city in CityKit.Risks(backend).cities)
            {
                seen.Add(city.risk);
                Assert.That(city.risk, Is.InRange(defs.combat.risk.initialRisk - 12, defs.combat.risk.initialRisk + 12));
            }
            Assert.Greater(seen.Count, 5, "도시마다 처음 위험도가 달라야 한다");
        }

        [Test]
        public void DefenseWin_LowersTheHomeCityRisk_AndLeavesAHistoryRow()
        {
            LocalMockBackend backend = _kit.NewBackend(CityKit.QuietDefs());
            MockKit.Boot(backend);
            CityKit.Register(backend);
            int before = CityKit.Risks(backend).Find(CityKit.Haeundae).risk;

            BattleResultDto win = CityKit.WinDefense(_kit, backend);
            Assert.IsNotNull(win.city);
            Assert.AreEqual(CityKit.Haeundae, win.city.regionCode);
            Assert.AreEqual(RiskActors.Player, win.city.actor);
            Assert.AreEqual(RiskReasons.DefenseWon, win.city.reason);
            Assert.AreEqual(before, win.city.riskBefore);
            Assert.Less(win.city.riskDeltaCenti, 0);
            Assert.Greater(win.city.contribution, 0);

            // 침공이 없을 때의 승리는 순찰이다: 절반만 반영된다.
            RiskPolicy policy = backend.Defs.combat.risk;
            Assert.AreEqual(policy.Delta(RiskReasons.DefenseWon) * policy.patrolPercent / 100, win.city.riskDeltaCenti);

            CityDetailDto detail = CityKit.Detail(backend, CityKit.Haeundae);
            Assert.IsTrue(detail.isHome);
            Assert.AreEqual(win.city.riskAfter, detail.status.risk);
            Assert.AreEqual(1, detail.history.Count);
            RiskHistoryEntry row = detail.history[0];
            Assert.AreEqual(RiskReasons.DefenseWon, row.reason);
            Assert.AreEqual(RiskActors.Player, row.actor);
            Assert.AreEqual(win.city.riskDeltaCenti, row.deltaCenti);
            Assert.IsFalse(string.IsNullOrEmpty(row.reference), "어느 전투였는지 남는다");
            Assert.AreEqual(win.city.contribution, detail.myContribution);
            Assert.AreEqual(win.city.contribution, MockKit.Me(backend).cityContribution);
            Assert.IsNull(backend.FindWorldInconsistency());
        }

        [Test]
        public void LosingOnPurpose_NeverRaisesRisk()
        {
            LocalMockBackend backend = _kit.NewBackend(CityKit.InvadedDefs());
            MockKit.Boot(backend);
            CityKit.Register(backend);
            int before = CityKit.Risks(backend).Find(CityKit.Haeundae).risk;
            int historyBefore = CityKit.Detail(backend, CityKit.Haeundae).history.Count;

            for (int i = 0; i < 10; i++)
                Assert.IsNull(CityKit.LoseDefense(_kit, backend).city);

            CityDetailDto detail = CityKit.Detail(backend, CityKit.Haeundae);
            Assert.AreEqual(before, detail.status.risk);
            Assert.AreEqual(historyBefore, detail.history.Count);
        }

        [Test]
        public void Stranger_AffectsNoCity()
        {
            LocalMockBackend backend = _kit.NewBackend(CityKit.QuietDefs());
            MockKit.Boot(backend);
            Assert.IsNull(CityKit.WinDefense(_kit, backend).city);
            Assert.IsNull(CityKit.WinFps(_kit, backend).city);
            foreach (CityStatusDto city in CityKit.Risks(backend).cities)
                Assert.AreEqual(backend.Defs.combat.risk.initialRisk, city.risk);
        }

        [Test]
        public void OnePlayer_CannotLowerRiskPastTheDailyCap()
        {
            GameDefs defs = CityKit.QuietDefs();
            defs.combat.risk.initialRisk = 90;
            LocalMockBackend backend = _kit.NewBackend(defs);
            MockKit.Boot(backend);
            CityKit.Register(backend);

            CityEffectDto last = null;
            for (int i = 0; i < 12; i++) last = CityKit.WinDefense(_kit, backend).city;
            Assert.IsTrue(last.capped);
            Assert.AreEqual(0, last.riskDeltaCenti, "한도에 닿은 뒤의 승리는 위험도를 더 내리지 않는다");
            Assert.Greater(last.contribution, 0, "기여도는 계속 쌓인다");
            Assert.AreEqual(90 - defs.combat.risk.dailyPlayerReduceCap, CityKit.Risks(backend).Find(CityKit.Haeundae).risk);
            Assert.AreEqual(defs.combat.risk.dailyPlayerReduceCap, CityKit.Overview(backend).riskReducedToday);
        }

        [Test]
        public void Hunting_LowersRiskALittle_EveryTenKills()
        {
            LocalMockBackend backend = _kit.NewBackend(CityKit.QuietDefs());
            MockKit.Boot(backend);
            CityKit.Register(backend);
            RiskPolicy policy = backend.Defs.combat.risk;

            ApiResult<HuntSessionDto> started = null;
            backend.StartHunt(ZoneCodes.NorthEast, r => started = r);
            string session = MockKit.Ok(started).sessionId;
            _kit.Clock.Advance(60);

            ApiResult<HuntReportDto> report = null;
            backend.ReportHunt(session, 1, new List<KillCount> { new KillCount(EnemyCodes.MalfunctionRobot, 9) }, r => report = r);
            Assert.IsNull(MockKit.Ok(report).city, "열 마리가 되기 전에는 반영되지 않는다");

            _kit.Clock.Advance(60);
            backend.ReportHunt(session, 2, new List<KillCount> { new KillCount(EnemyCodes.MalfunctionRobot, 1) }, r => report = r);
            CityEffectDto effect = MockKit.Ok(report).city;
            Assert.IsNotNull(effect, "아홉 마리와 한 마리가 이어져 열 마리가 된다");
            Assert.AreEqual(RiskReasons.HuntCompleted, effect.reason);
            Assert.AreEqual(policy.Delta(RiskReasons.HuntCompleted), effect.riskDeltaCenti);
            Assert.AreEqual(policy.contribHuntStep, effect.contribution);

            // 방어전 한 번이 사냥 열 마리보다 훨씬 크다 ("사냥은 조금 낮춘다").
            Assert.Less(policy.Delta(RiskReasons.DefenseWon), policy.Delta(RiskReasons.HuntCompleted) * 3);
        }

        [Test]
        public void Hunting_InADangerousCity_CountsAsASpecialMission()
        {
            GameDefs defs = CityKit.QuietDefs();
            defs.combat.risk.initialRisk = 60;
            LocalMockBackend backend = _kit.NewBackend(defs);
            MockKit.Boot(backend);
            CityKit.Register(backend);

            ApiResult<HuntSessionDto> started = null;
            backend.StartHunt(ZoneCodes.NorthEast, r => started = r);
            _kit.Clock.Advance(60);
            ApiResult<HuntReportDto> report = null;
            backend.ReportHunt(MockKit.Ok(started).sessionId, 1,
                new List<KillCount> { new KillCount(EnemyCodes.MalfunctionRobot, 10) }, r => report = r);
            RiskPolicy policy = defs.combat.risk;
            Assert.AreEqual(policy.Delta(RiskReasons.HuntCompleted) * policy.huntHighRiskPercent / 100, MockKit.Ok(report).city.riskDeltaCenti);
        }

        [Test]
        public void Risk_NeverTouchesTheWalletOrTheBank()
        {
            GameDefs defs = CityKit.InvadedDefs(InvasionKinds.Zero);
            defs.combat.risk.npcHoldBasePercent = 0;
            LocalMockBackend backend = _kit.NewBackend(defs);
            MockKit.Boot(backend);
            CityKit.Register(backend);
            _kit.Earn(backend, 1000);
            MockKit.Ok(MockKit.Deposit(backend, 400, "req-bank-0001"));

            long gc = MockKit.Me(backend).gc;
            long bank = MockKit.Bank(backend).bankGc;
            int ledger = backend.CurrentLedger.Count;

            // ZERO 이벤트가 이어지고 아무도 막지 않는다: 도시는 긴급 단계까지 간다.
            for (int i = 0; i < 10; i++)
            {
                _kit.Clock.Advance(3600);
                CityKit.Risks(backend);
            }
            CityStatusDto home = CityKit.Risks(backend).Find(CityKit.Haeundae);
            Assert.AreEqual(RiskStages.Emergency, home.stage);

            Assert.AreEqual(gc, MockKit.Me(backend).gc, "위험도가 올라도 GC는 그대로다");
            Assert.AreEqual(bank, MockKit.Bank(backend).bankGc, "위험도가 올라도 통장은 그대로다");
            Assert.AreEqual(ledger, backend.CurrentLedger.Count);
            Assert.IsNull(backend.FindLedgerInconsistency());
        }

        [Test]
        public void WorldState_IsKeptInItsOwnFile_AndSurvivesARestart()
        {
            LocalMockBackend backend = _kit.NewBackend(CityKit.InvadedDefs());
            MockKit.Boot(backend);
            CityKit.Register(backend);
            CityKit.WinDefense(_kit, backend);
            CityRisksDto before = CityKit.Risks(backend);
            Assert.IsNotNull(_kit.Store.Read(LocalMockBackend.WorldFile));

            LocalMockBackend restarted = _kit.NewBackend(CityKit.InvadedDefs());
            MockKit.Boot(restarted);
            CityRisksDto after = CityKit.Risks(restarted);
            for (int i = 0; i < before.cities.Count; i++)
            {
                Assert.AreEqual(before.cities[i].regionCode, after.cities[i].regionCode);
                Assert.AreEqual(before.cities[i].risk, after.cities[i].risk);
                Assert.AreEqual(before.cities[i].invasionKind, after.cities[i].invasionKind);
            }
            Assert.AreEqual(1, CityKit.Detail(restarted, CityKit.Haeundae).invasions[0].progress, "침공 진행도도 남는다");
        }

        [Test]
        public void UnreadableWorldFile_IsSetAside_NotSilentlyTrusted()
        {
            _kit.Store.Write(LocalMockBackend.WorldFile, "{ not json");
            LocalMockBackend backend = _kit.NewBackend(CityKit.QuietDefs());
            MockKit.Boot(backend);
            Assert.AreEqual(backend.Regions.Regions.Count, CityKit.Risks(backend).cities.Count);
            Assert.IsNull(backend.FindWorldInconsistency());
        }

        [Test]
        public void AMonthOfDefaultPolicy_KeepsTheCountryPlayable()
        {
            // 기본 정책으로 30일을 돌린다. 아무도 싸우지 않아도 나라 전체가 긴급 단계로 굳어지면 안 되고,
            // 그렇다고 침공이 아무 일도 아니어서도 안 된다.
            LocalMockBackend backend = _kit.NewBackend(MockKit.Defs());
            MockKit.Boot(backend);
            CityKit.Register(backend);

            var stages = new Dictionary<string, int>();
            long riskSum = 0;
            int samples = 0, invaded = 0;
            for (int day = 0; day < 30; day++)
            {
                for (int step = 0; step < 4; step++)
                {
                    _kit.Clock.Advance(6 * 3600);
                    CityRisksDto risks = CityKit.Risks(backend);
                    if (day < 10) continue;
                    foreach (CityStatusDto city in risks.cities)
                    {
                        stages.TryGetValue(city.stage, out int count);
                        stages[city.stage] = count + 1;
                        riskSum += city.risk;
                        samples++;
                        if (city.HasInvasion) invaded++;
                    }
                }
            }
            Assert.IsNull(backend.FindWorldInconsistency());

            double average = (double)riskSum / samples;
            stages.TryGetValue(RiskStages.Safe, out int safe);
            stages.TryGetValue(RiskStages.Caution, out int caution);
            stages.TryGetValue(RiskStages.Danger, out int danger);
            stages.TryGetValue(RiskStages.Emergency, out int emergency);
            TestContext.WriteLine($"평균 위험도 {average:F1}, 안전 {safe * 100.0 / samples:F1}%, 주의 {caution * 100.0 / samples:F1}%, " +
                                  $"위험 {danger * 100.0 / samples:F1}%, 긴급 {emergency * 100.0 / samples:F1}%, 침공 중 {invaded * 100.0 / samples:F1}%");

            Assert.That(average, Is.InRange(8.0, 45.0), "평균 위험도가 너무 낮거나 높다");
            Assert.Greater(safe + caution, samples / 2, "대부분의 도시는 안전하거나 주의 단계여야 한다");
            Assert.Less(emergency, samples / 10, "긴급 단계의 도시는 드물어야 한다");
            Assert.Greater(caution + danger + emergency, samples / 50, "침공이 도시의 단계를 바꾸기도 해야 한다");
            Assert.Greater(invaded, 0);

            // 이력에는 누가 바꿨는지 남고, 아무도 싸우지 않았으므로 플레이어의 것은 없다.
            foreach (RiskHistoryEntry row in CityKit.Detail(backend, CityKit.Haeundae).history)
            {
                Assert.IsFalse(RiskActors.IsRealPlayer(row.actor), "플레이어가 하지 않은 일이 플레이어의 기여로 적혔다");
                Assert.IsFalse(string.IsNullOrEmpty(row.reason));
                Assert.IsFalse(string.IsNullOrEmpty(row.at));
            }
        }
    }

    /// <summary>침공 이벤트의 한살이: 시작 → 플레이어가 격퇴하거나, 시간이 다 되어 NPC 수비대가 판정.</summary>
    public sealed class LocalMockBackendInvasionTests
    {
        private MockKit _kit;

        [SetUp]
        public void SetUp() => _kit = new MockKit();

        [Test]
        public void Invasion_RaisesRisk_AndShowsOnTheMap()
        {
            GameDefs defs = CityKit.InvadedDefs();
            LocalMockBackend backend = _kit.NewBackend(defs);
            MockKit.Boot(backend);

            CityStatusDto home = CityKit.Risks(backend).Find(CityKit.Haeundae);
            Assert.AreEqual(InvasionKinds.Outskirts, home.invasionKind);
            Assert.That(home.invasionSeverity, Is.InRange(1, 3));
            Assert.AreEqual(home.invasionSeverity, home.invasionRequired, "가디언이 적은 도시는 규모만큼만 이기면 된다");
            Assert.AreEqual(0, home.invasionProgress);
            Assert.AreEqual(1, home.recentInvasions);
            RiskPolicy policy = defs.combat.risk;
            Assert.AreEqual(policy.initialRisk + policy.Delta(RiskReasons.InvasionStarted) * home.invasionSeverity / 100, home.risk);

            CityDetailDto detail = CityKit.Detail(backend, CityKit.Haeundae);
            Assert.AreEqual(1, detail.invasions.Count);
            Assert.AreEqual(RiskReasons.InvasionStarted, detail.history[0].reason);
            Assert.AreEqual(RiskActors.System, detail.history[0].actor);
            Assert.AreEqual(detail.invasions[0].id, detail.history[0].reference);
        }

        [Test]
        public void DefenseWins_RepelAnOutskirtsInvasion()
        {
            LocalMockBackend backend = _kit.NewBackend(CityKit.InvadedDefs());
            MockKit.Boot(backend);
            CityKit.Register(backend);
            CityStatusDto start = CityKit.Risks(backend).Find(CityKit.Haeundae);

            // 시내 침입이 아니므로 FPS 승리는 이 침공에 쌓이지 않는다 (순찰로만 반영).
            FpsResultDto fps = CityKit.WinFps(_kit, backend);
            Assert.IsNull(fps.city.invasionId);
            Assert.AreEqual(0, CityKit.Risks(backend).Find(CityKit.Haeundae).invasionProgress);

            CityEffectDto effect = null;
            for (int i = 0; i < start.invasionRequired; i++)
            {
                effect = CityKit.WinDefense(_kit, backend).city;
                Assert.AreEqual(i + 1, effect.invasionProgress);
                Assert.AreEqual(start.invasionRequired, effect.invasionRequired);
                Assert.AreEqual(InvasionKinds.Outskirts, effect.invasionKind);
            }
            Assert.IsTrue(effect.invasionRepelled);

            CityDetailDto detail = CityKit.Detail(backend, CityKit.Haeundae);
            Assert.IsFalse(detail.status.HasInvasion);
            Assert.AreEqual(InvasionStatus.Repelled, detail.invasions[0].status);
            Assert.IsFalse(string.IsNullOrEmpty(detail.invasions[0].resolvedAt));
            Assert.Less(detail.status.risk, start.risk);
            Assert.IsTrue(detail.history.Exists(h => h.reason == RiskReasons.InvasionRepelled && h.actor == RiskActors.Player));
            Assert.AreEqual(100, detail.score.response, "플레이어가 막은 침공은 침공 대응 점수가 된다");
            Assert.IsNull(backend.FindWorldInconsistency());

            // 침공 중의 승리는 순찰보다 기여도가 크다.
            int patrol = CityKit.WinDefense(_kit, backend).city.contribution;
            Assert.Greater(effect.contribution, patrol);
        }

        [Test]
        public void FpsWins_RepelAnIntrusion_AndTheSessionKnowsAboutIt()
        {
            LocalMockBackend backend = _kit.NewBackend(CityKit.InvadedDefs(InvasionKinds.Intrusion));
            MockKit.Boot(backend);
            CityKit.Register(backend);
            CityStatusDto start = CityKit.Risks(backend).Find(CityKit.Haeundae);
            Assert.AreEqual(InvasionKinds.Intrusion, start.invasionKind);

            FpsSessionDto session = CityKit.StartFps(backend);
            Assert.IsFalse(string.IsNullOrEmpty(session.invasionId), "긴급 출동이라는 것을 임무가 안다");
            Assert.AreEqual(CityKit.Haeundae, session.regionCode);

            // 방어전은 시내 침입을 막는 데 쓰이지 않는다.
            Assert.IsNull(CityKit.WinDefense(_kit, backend).city.invasionId);

            CityEffectDto effect = null;
            for (int i = 0; i < start.invasionRequired; i++) effect = CityKit.WinFps(_kit, backend).city;
            Assert.IsTrue(effect.invasionRepelled);
            Assert.AreEqual(RiskReasons.FpsRepelled, effect.reason);
            Assert.AreEqual(InvasionStatus.Repelled, CityKit.Detail(backend, CityKit.Haeundae).invasions[0].status);
        }

        [Test]
        public void ZeroEvent_CanBeRepelledInEitherMode()
        {
            LocalMockBackend backend = _kit.NewBackend(CityKit.InvadedDefs(InvasionKinds.Zero));
            MockKit.Boot(backend);
            CityKit.Register(backend);
            CityStatusDto start = CityKit.Risks(backend).Find(CityKit.Haeundae);
            Assert.AreEqual(InvasionKinds.Zero, start.invasionKind);
            Assert.AreEqual(3, start.invasionRequired);

            Assert.AreEqual(1, CityKit.WinDefense(_kit, backend).city.invasionProgress);
            Assert.AreEqual(2, CityKit.WinFps(_kit, backend).city.invasionProgress);
            CityEffectDto last = CityKit.WinDefense(_kit, backend).city;
            Assert.IsTrue(last.invasionRepelled);
            Assert.IsTrue(CityKit.Detail(backend, CityKit.Haeundae).history
                .Exists(h => h.reason == RiskReasons.SpecialMission), "ZERO 이벤트를 막으면 특별 위험 제거로 적힌다");
        }

        [Test]
        public void UnansweredInvasion_IsSettledByTheNpcGarrison_NotCreditedToPlayers()
        {
            GameDefs defs = CityKit.InvadedDefs();
            LocalMockBackend backend = _kit.NewBackend(defs);
            MockKit.Boot(backend);
            CityKit.Register(backend);
            CityKit.Risks(backend);

            _kit.Clock.Advance((defs.combat.risk.invasionMinutes + defs.combat.risk.tickMinutes) * 60);
            CityRisksDto risks = CityKit.Risks(backend);

            int held = 0, failed = 0;
            foreach (CityStatusDto status in risks.cities)
            {
                CityDetailDto detail = CityKit.Detail(backend, status.regionCode);
                InvasionEvent first = detail.invasions[detail.invasions.Count - 1];
                Assert.IsFalse(first.IsActive, "제한 시간이 지난 침공은 끝나 있어야 한다");
                Assert.AreNotEqual(InvasionStatus.Repelled, first.status, "아무도 싸우지 않았다");
                if (first.status == InvasionStatus.NpcHeld) held++;
                else failed++;

                RiskHistoryEntry settled = detail.history.Find(h => h.reference == first.id && h.reason != RiskReasons.InvasionStarted);
                Assert.IsNotNull(settled);
                Assert.AreEqual(first.status == InvasionStatus.NpcHeld ? RiskActors.Npc : RiskActors.System, settled.actor);
                Assert.AreEqual(first.status == InvasionStatus.NpcHeld ? RiskReasons.NpcHeld : RiskReasons.DefenseFailed, settled.reason);
                Assert.AreEqual(0, detail.score.response, "NPC가 막은 침공은 플레이어의 침공 대응 점수가 아니다");
            }
            Assert.Greater(held, failed, "NPC 수비대가 대부분 막는다");
            Assert.Greater(failed, 0, "그래도 늘 막는 것은 아니다");
            Assert.IsNull(backend.FindWorldInconsistency());
        }

        [Test]
        public void FailedIntrusion_DamagesAFacility()
        {
            GameDefs defs = CityKit.InvadedDefs(InvasionKinds.Intrusion);
            defs.combat.risk.npcHoldBasePercent = 0;
            LocalMockBackend backend = _kit.NewBackend(defs);
            MockKit.Boot(backend);
            CityKit.Risks(backend);
            _kit.Clock.Advance((defs.combat.risk.invasionMinutes + defs.combat.risk.tickMinutes) * 60);
            CityKit.Risks(backend);

            int damaged = 0;
            foreach (RegionDef region in backend.Regions.Regions)
            {
                CityDetailDto detail = CityKit.Detail(backend, region.code);
                int damage = 0;
                foreach (FacilityStatusDto facility in detail.facilities) damage += facility.damage;
                if (damage == 0) continue;
                damaged++;
                Assert.IsTrue(detail.history.Exists(h => h.reason == RiskReasons.FacilityDamaged && h.actor == RiskActors.System));
                Assert.IsTrue(detail.history.Exists(h => h.reason == RiskReasons.IntrusionFailed));
            }
            Assert.Greater(damaged, backend.Regions.Regions.Count / 2);
        }

        [Test]
        public void CatchingUp_AfterALongBreak_IsBounded_AndConsistent()
        {
            LocalMockBackend backend = _kit.NewBackend(MockKit.Defs());
            MockKit.Boot(backend);
            CityKit.Risks(backend);
            _kit.Clock.AddDays(90);
            CityRisksDto risks = CityKit.Risks(backend);
            Assert.AreEqual(backend.Regions.Regions.Count, risks.cities.Count);
            Assert.IsNull(backend.FindWorldInconsistency());
            Assert.Less(_kit.Store.Read(LocalMockBackend.WorldFile).Length, 2000000, "도시 기록이 끝없이 커지면 안 된다");
        }
    }

    /// <summary>도시 간 지원, 시설 복구, 도시 기금, 지역 랭킹 (docs/REGIONAL_COMPETITION.md).</summary>
    public sealed class LocalMockBackendRegionalTests
    {
        private MockKit _kit;

        [SetUp]
        public void SetUp() => _kit = new MockKit();

        /// <summary>모든 도시가 곧바로 긴급 단계가 되는 정의.</summary>
        private static GameDefs EmergencyDefs()
        {
            GameDefs defs = CityKit.InvadedDefs();
            defs.combat.risk.initialRisk = 95;
            return defs;
        }

        [Test]
        public void EmergencyCity_AsksNearbyGuardiansForHelp()
        {
            LocalMockBackend backend = _kit.NewBackend(EmergencyDefs());
            MockKit.Boot(backend);
            CityKit.Register(backend);

            SupportListDto list = CityKit.Supports(backend);
            Assert.Greater(list.requests.Count, 1);
            Assert.IsNull(list.joinedRequestId);
            RegionCatalog catalog = backend.Regions;
            foreach (SupportRowDto row in list.requests)
            {
                Assert.AreEqual("26", catalog.FindRegion(row.regionCode).sidoCode, "같은 시·도의 요청만 보인다");
                Assert.AreEqual(RiskStages.Emergency, row.stage);
                Assert.AreEqual(row.regionCode != CityKit.Haeundae, row.canJoin);
            }
            Assert.IsTrue(CityKit.Risks(backend).Find(CityKit.BusanJung).SupportOpen);
            Assert.IsNotNull(CityKit.Detail(backend, CityKit.BusanJung).support);
        }

        [Test]
        public void Supporter_FightsForTheOtherCity_ButKeepsTheirHome()
        {
            LocalMockBackend backend = _kit.NewBackend(EmergencyDefs());
            MockKit.Boot(backend);
            CityKit.Register(backend);
            string requestId = CityKit.Risks(backend).Find(CityKit.BusanJung).supportRequestId;
            int homeBefore = CityKit.Risks(backend).Find(CityKit.Haeundae).risk;
            int targetBefore = CityKit.Risks(backend).Find(CityKit.BusanJung).risk;

            SupportResultDto joined = MockKit.Ok(CityKit.Join(backend, requestId));
            Assert.AreEqual(CityKit.BusanJung, joined.joinedRegionCode);
            Assert.AreEqual(CityKit.Haeundae, joined.homeRegionCode);
            MeDto me = MockKit.Me(backend);
            Assert.AreEqual(CityKit.Haeundae, me.regionCode, "소속 도시는 바뀌지 않는다");
            Assert.AreEqual(CityKit.BusanJung, me.supportRegionCode);

            BattleResultDto win = CityKit.WinDefense(_kit, backend);
            Assert.AreEqual(CityKit.BusanJung, win.city.regionCode);
            Assert.AreEqual(RiskActors.Supporter, win.city.actor);
            Assert.AreEqual(CityKit.BusanJung, win.regionCode, "방어 포인트도 도운 도시에 쌓인다");

            CityRisksDto risks = CityKit.Risks(backend);
            Assert.Less(risks.Find(CityKit.BusanJung).risk, targetBefore);
            Assert.AreEqual(homeBefore, risks.Find(CityKit.Haeundae).risk);
            Assert.AreEqual(0, MockKit.Me(backend).cityContribution, "지원 기여는 소속 도시의 기여도와 따로 센다");

            SupportListDto list = CityKit.Supports(backend);
            Assert.AreEqual(requestId, list.joinedRequestId);
            Assert.AreEqual(win.city.contribution, list.mySupportPoints);
            SupportRowDto row = list.requests.Find(r => r.requestId == requestId);
            Assert.AreEqual(1, row.wins);
            Assert.AreEqual(win.city.contribution, row.points);

            CityDetailDto target = CityKit.Detail(backend, CityKit.BusanJung);
            Assert.IsFalse(target.isHome);
            Assert.IsTrue(target.history.Exists(h => h.actor == RiskActors.Supporter));
            Assert.Greater(CityKit.Detail(backend, CityKit.Haeundae).score.cooperation, 0, "도운 것은 우리 도시의 협동 점수가 된다");

            // 돌아오면 다시 우리 도시를 위해 싸운다.
            ApiResult<SupportResultDto> left = null;
            backend.LeaveSupport(r => left = r);
            Assert.IsNull(MockKit.Ok(left).joinedRegionCode);
            Assert.AreEqual(CityKit.Haeundae, CityKit.WinDefense(_kit, backend).city.regionCode);
            Assert.IsNull(backend.FindWorldInconsistency());
        }

        [Test]
        public void Support_IsRefused_ForStrangers_FarCities_OwnCity_AndClosedRequests()
        {
            LocalMockBackend backend = _kit.NewBackend(EmergencyDefs());
            MockKit.Boot(backend);
            CityRisksDto risks = CityKit.Risks(backend);
            string busan = risks.Find(CityKit.BusanJung).supportRequestId;
            string seoul = risks.Find(CityKit.SeoulJongno).supportRequestId;
            string own = risks.Find(CityKit.Haeundae).supportRequestId;

            Assert.AreEqual(ApiErrors.Locked, MockKit.Error(CityKit.Join(backend, busan)), "이방인");
            Assert.AreEqual(0, CityKit.Supports(backend).requests.Count, "이방인에게는 요청이 보이지 않는다");

            CityKit.Register(backend);
            Assert.AreEqual(ApiErrors.ValidationFailed, MockKit.Error(CityKit.Join(backend, seoul)), "다른 시·도");
            Assert.AreEqual(ApiErrors.ValidationFailed, MockKit.Error(CityKit.Join(backend, own)), "우리 도시");
            Assert.AreEqual(ApiErrors.NotFound, MockKit.Error(CityKit.Join(backend, "sup_999999")));
            Assert.AreEqual(ApiErrors.NotFound, MockKit.Error(CityKit.Join(backend, null)));
            Assert.IsNull(MockKit.Me(backend).supportRegionCode);
        }

        [Test]
        public void UnansweredSupportRequest_Expires_AndRaisesRisk()
        {
            GameDefs defs = CityKit.QuietDefs();
            defs.combat.risk.initialRisk = 80;
            defs.combat.risk.dailyStabilizePercent = 0;
            LocalMockBackend backend = _kit.NewBackend(defs);
            MockKit.Boot(backend);
            Assert.IsTrue(CityKit.Risks(backend).Find(CityKit.BusanJung).SupportOpen);

            _kit.Clock.Advance((defs.combat.risk.supportMinutes + defs.combat.risk.tickMinutes) * 60);
            CityDetailDto detail = CityKit.Detail(backend, CityKit.BusanJung);
            Assert.IsTrue(detail.history.Exists(h => h.reason == RiskReasons.EmergencyUnresolved && h.actor == RiskActors.System));
            Assert.AreEqual(80 + defs.combat.risk.Delta(RiskReasons.EmergencyUnresolved) / 100, detail.status.risk);
            Assert.IsNotNull(detail.support, "아직 긴급 단계라 새 요청이 열린다");
            Assert.IsNull(backend.FindWorldInconsistency());
        }

        [Test]
        public void RepairingAFacility_CostsGc_LowersRisk_AndIsLimitedPerDay()
        {
            GameDefs defs = CityKit.InvadedDefs(InvasionKinds.Intrusion);
            defs.combat.risk.npcHoldBasePercent = 0;
            defs.combat.risk.dailyRepairLimit = 1;
            defs.economy.residencyBonusGc = 0;
            LocalMockBackend backend = _kit.NewBackend(defs);
            MockKit.Boot(backend);
            CityKit.Risks(backend);
            _kit.Clock.Advance((defs.combat.risk.invasionMinutes + defs.combat.risk.tickMinutes) * 60);
            CityKit.Risks(backend);

            // 시설이 피해를 입은 도시에 전입한다.
            string home = null, facility = null;
            foreach (RegionDef region in backend.Regions.Regions)
            {
                FacilityStatusDto hit = CityKit.Detail(backend, region.code).facilities.Find(f => f.damage > 0);
                if (hit == null) continue;
                home = region.code;
                facility = hit.code;
                break;
            }
            Assert.IsNotNull(home, "테스트 준비: 피해 입은 도시가 있어야 한다");

            Assert.AreEqual(ApiErrors.Locked, MockKit.Error(CityKit.Repair(backend, facility, "req-fix-0001")), "이방인은 고칠 수 없다");
            CityKit.Register(backend, home);
            Assert.AreEqual(ApiErrors.InsufficientFunds, MockKit.Error(CityKit.Repair(backend, facility, "req-fix-0002")));

            _kit.Earn(backend, 500);
            long gc = MockKit.Me(backend).gc;
            int riskBefore = CityKit.Detail(backend, home).status.risk;
            FacilityDef def = defs.combat.FindFacility(facility);

            CityActionDto repaired = MockKit.Ok(CityKit.Repair(backend, facility, "req-fix-0003"));
            Assert.AreEqual(def.repairGc, repaired.costGc);
            Assert.AreEqual(gc - def.repairGc, repaired.gc);
            Assert.AreEqual(defs.combat.risk.contribRepair, repaired.contribution);
            Assert.AreEqual(RiskReasons.FacilityRepaired, repaired.city.reason);
            Assert.Less(repaired.city.riskDeltaCenti, 0);
            Assert.LessOrEqual(repaired.detail.status.risk, riskBefore);
            Assert.AreEqual(0, repaired.detail.facilities.Find(f => f.code == facility).damage);
            Assert.AreEqual(LedgerReasons.CityRepair, CityKit.LastLedger(backend).reason);
            Assert.AreEqual(-def.repairGc, CityKit.LastLedger(backend).delta);

            // 같은 요청 번호로 다시 보내도 한 번만 낸다.
            CityActionDto replay = MockKit.Ok(CityKit.Repair(backend, facility, "req-fix-0003"));
            Assert.IsTrue(replay.duplicate);
            Assert.AreEqual(repaired.gc, replay.gc);

            Assert.AreEqual(ApiErrors.Conflict, MockKit.Error(CityKit.Repair(backend, facility, "req-fix-0004")), "이미 다 고쳤다");
            Assert.AreEqual(ApiErrors.NotFound, MockKit.Error(CityKit.Repair(backend, "fac_nothing", "req-fix-0005")));
            Assert.AreEqual(repaired.gc, MockKit.Me(backend).gc);
            Assert.IsNull(backend.FindLedgerInconsistency());
            Assert.IsNull(backend.FindWorldInconsistency());
        }

        [Test]
        public void FundingACityStructure_IsShared_Limited_AndRaisesDefensePower()
        {
            GameDefs defs = CityKit.QuietDefs();
            TowerDef shield = defs.FindTower(TowerCodes.EnergyShield);
            shield.cityGoalGc = 500;
            LocalMockBackend backend = _kit.NewBackend(defs);
            MockKit.Boot(backend);

            Assert.AreEqual(ApiErrors.Locked, MockKit.Error(CityKit.Fund(backend, shield.code, 100, "req-fund-0001")), "이방인");
            // 사냥으로 위험도를 낮추기 전에(이방인일 때) 벌어 둔다.
            _kit.Earn(backend, 3000);
            CityKit.Register(backend);
            long gc = MockKit.Me(backend).gc;
            long contributionBefore = MockKit.Me(backend).cityContribution;
            int powerBefore = CityKit.Detail(backend, CityKit.Haeundae).status.defensePower;

            Assert.AreEqual(ApiErrors.InvalidRequest, MockKit.Error(CityKit.Fund(backend, shield.code, 150, "req-fund-0002")));
            Assert.AreEqual(ApiErrors.InvalidRequest, MockKit.Error(CityKit.Fund(backend, shield.code, 0, "req-fund-0003")));
            Assert.AreEqual(ApiErrors.InvalidRequest, MockKit.Error(CityKit.Fund(backend, shield.code, -500, "req-fund-0004")));
            Assert.AreEqual(ApiErrors.NotFound, MockKit.Error(CityKit.Fund(backend, TowerCodes.MachineGun, 100, "req-fund-0005")),
                "개인이 세우는 타워에는 기금을 낼 수 없다");

            CityActionDto part = MockKit.Ok(CityKit.Fund(backend, shield.code, 300, "req-fund-0006"));
            Assert.AreEqual(300, part.costGc);
            Assert.AreEqual(gc - 300, part.gc);
            Assert.AreEqual(defs.combat.risk.contribPerFund100Gc * 3, part.contribution);
            Assert.AreEqual(0, part.city.riskDeltaCenti, "단계가 완성되기 전에는 위험도가 그대로다");
            CityStructureDto building = part.detail.structures.Find(s => s.code == shield.code);
            Assert.AreEqual(0, building.level);
            Assert.AreEqual(300, building.fundedGc);
            Assert.AreEqual(LedgerReasons.CityFund, CityKit.LastLedger(backend).reason);

            CityActionDto done = MockKit.Ok(CityKit.Fund(backend, shield.code, 200, "req-fund-0007"));
            building = done.detail.structures.Find(s => s.code == shield.code);
            Assert.AreEqual(1, building.level);
            Assert.AreEqual(shield.cityPower, building.power);
            Assert.AreEqual(powerBefore + shield.cityPower, done.detail.status.defensePower);
            Assert.AreEqual(RiskReasons.JointDefense, done.city.reason);
            Assert.Less(done.city.riskDeltaCenti, 0);

            CityActionDto replay = MockKit.Ok(CityKit.Fund(backend, shield.code, 200, "req-fund-0007"));
            Assert.IsTrue(replay.duplicate);
            Assert.AreEqual(done.gc, replay.gc);

            // 하루 한도: 이미 500을 냈다.
            int room = defs.combat.risk.dailyFundLimitGc - 500;
            Assert.AreEqual(ApiErrors.LimitReached, MockKit.Error(CityKit.Fund(backend, shield.code, room + 100, "req-fund-0008")));
            Assert.AreEqual(done.gc, MockKit.Me(backend).gc);
            Assert.AreEqual(contributionBefore + defs.combat.risk.contribPerFund100Gc * 5, MockKit.Me(backend).cityContribution);

            // 시설은 도시의 것이다: 플레이어의 가방이나 타워 목록에는 생기지 않는다.
            CollectionAssert.DoesNotContain(MockKit.Me(backend).unlockedTowers, shield.code);
            Assert.IsNull(backend.FindLedgerInconsistency());
            Assert.IsNull(backend.FindWorldInconsistency());
        }

        [Test]
        public void FullyBuiltStructure_TakesNoMoreMoney()
        {
            GameDefs defs = CityKit.QuietDefs();
            TowerDef shield = defs.FindTower(TowerCodes.EnergyShield);
            shield.cityGoalGc = 200;
            shield.cityMaxLevel = 1;
            LocalMockBackend backend = _kit.NewBackend(defs);
            MockKit.Boot(backend);
            CityKit.Register(backend);
            _kit.Earn(backend, 1000);
            long gc = MockKit.Me(backend).gc;

            // 남은 목표보다 많이 내려 해도 남은 만큼만 받는다.
            CityActionDto done = MockKit.Ok(CityKit.Fund(backend, shield.code, 500, "req-fund-0001"));
            Assert.AreEqual(200, done.costGc);
            Assert.AreEqual(gc - 200, done.gc);
            Assert.AreEqual(ApiErrors.Conflict, MockKit.Error(CityKit.Fund(backend, shield.code, 100, "req-fund-0002")));
            Assert.AreEqual(gc - 200, MockKit.Me(backend).gc);
        }

        [Test]
        public void Rankings_ListEveryCity_AndPutPlayerDefendedCitiesFirst()
        {
            LocalMockBackend backend = _kit.NewBackend(CityKit.QuietDefs());
            MockKit.Boot(backend);
            RankingsDto before = CityKit.Rankings(backend);
            Assert.AreEqual(backend.Regions.Regions.Count, before.totalCities);
            Assert.AreEqual(0, before.playerCities);
            Assert.IsNull(before.home);

            CityKit.Register(backend);
            CityKit.WinDefense(_kit, backend);
            RankingsDto after = CityKit.Rankings(backend);
            Assert.AreEqual(1, after.playerCities);
            Assert.IsNotNull(after.home);
            Assert.AreEqual(1, after.home.rank, "가디언이 지키는 도시가 NPC만 있는 도시보다 앞에 온다");
            Assert.AreEqual(CityKit.Haeundae, after.rows[0].regionCode);
            Assert.IsTrue(after.rows[0].playerDefended);
            Assert.AreEqual(1, after.rows[0].activePlayers);
            for (int i = 0; i < after.rows.Count; i++) Assert.AreEqual(i + 1, after.rows[i].rank);
            Assert.AreEqual(1, CityKit.Risks(backend).Find(CityKit.Haeundae).rank);
        }

        [Test]
        public void InactiveResidents_StopCountingAsActivePlayers()
        {
            GameDefs defs = CityKit.QuietDefs();
            LocalMockBackend backend = _kit.NewBackend(defs);
            MockKit.Boot(backend);
            CityKit.Register(backend);
            Assert.AreEqual(1, CityKit.Risks(backend).Find(CityKit.Haeundae).activePlayers);

            _kit.Clock.AddDays(defs.combat.risk.activeDays + 1);
            CityStatusDto home = CityKit.Risks(backend).Find(CityKit.Haeundae);
            Assert.AreEqual(1, home.residents);
            Assert.AreEqual(0, home.activePlayers);
            Assert.IsFalse(home.playerDefended);
        }
    }
}
