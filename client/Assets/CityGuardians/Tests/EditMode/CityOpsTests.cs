using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using CityGuardians.Domain;
using CityGuardians.Services;
using NUnit.Framework;

namespace CityGuardians.Tests
{
    /// <summary>v9.0 모의 서버: 도시 성장, 이사, 가상 지방세·도시 재정·참여예산·협동 방어전의 "준비 중" 응답.</summary>
    public sealed class LocalMockBackendCityOpsTests
    {
        private MockKit _kit;

        [SetUp]
        public void SetUp() => _kit = new MockKit();

        private static T Call<T>(Action<Action<ApiResult<T>>> request)
        {
            ApiResult<T> result = null;
            request(r => result = r);
            return MockKit.Ok(result);
        }

        private static ApiResult<T> Raw<T>(Action<Action<ApiResult<T>>> request)
        {
            ApiResult<T> result = null;
            request(r => result = r);
            Assert.IsNotNull(result, "응답 콜백이 불리지 않았다");
            return result;
        }

        private static ApiResult<MoveResultDto> Move(LocalMockBackend backend, string region, string requestId) =>
            Raw<MoveResultDto>(done => backend.MoveResidency(region, requestId, done));

        [Test]
        public void Client_HasNoWayToSendPopulationTaxOrVoteNumbers()
        {
            // 전입자 수, 세율, 세액, 재정 잔액, 표의 수는 서버가 정한다. 그런 값을 받는 요청이 없다.
            var v9 = new Regex("Growth|Residency|CityContent|Tax|Treasury|Expenditures|Budget|Coop");
            var banned = new Regex("resident|population|rate|tax|balance|weight|votes|reward|energy|kills|result", RegexOptions.IgnoreCase);
            int found = 0;
            foreach (MethodInfo method in typeof(IGameBackend).GetMethods())
            {
                if (!v9.IsMatch(method.Name) || method.Name == "RegisterResidency") continue;
                found++;
                foreach (ParameterInfo parameter in method.GetParameters())
                    Assert.IsFalse(banned.IsMatch(parameter.Name), $"{method.Name}({parameter.Name}): 서버가 정할 값을 클라이언트가 보낸다");
            }
            Assert.AreEqual(20, found, "v9.0 요청의 수가 바뀌었다. 새 요청이 위의 규칙을 지키는지 보고 이 수를 고친다.");
            // 세금을 "내는" 요청, 표의 무게를 정하는 요청, 전투 결과를 보내는 요청은 없다.
            foreach (string name in new[] { "PayTax", "CollectTax", "FinishCoop", "ReportCoop", "ExecuteBudget", "SetResidents" })
                Assert.IsNull(typeof(IGameBackend).GetMethod(name), name);
            // 투표 요청은 찬반만 받는다.
            CollectionAssert.AreEqual(new[] { "proposalId", "choice", "requestId", "done" },
                typeof(IGameBackend).GetMethod("VoteBudget").GetParameters().Select(p => p.Name).ToArray());
        }

        [Test]
        public void Growth_CountsOnlyValidResidents_NotGuestAccounts()
        {
            LocalMockBackend backend = _kit.NewBackend(CityKit.QuietDefs());
            MockKit.Boot(backend);
            CityKit.Register(backend);

            // 모의 서버의 계정은 게스트다. 전입신고를 해도 유효 전입자 수는 늘지 않는다.
            ResidencyInfoDto mine = Call<ResidencyInfoDto>(backend.GetResidency);
            Assert.IsTrue(mine.IsResident);
            Assert.AreEqual(CityKit.Haeundae, mine.regionCode);
            Assert.AreEqual("부산 해운대구", mine.regionName);
            Assert.IsFalse(mine.counted);
            Assert.AreEqual("guest_account", mine.notCountedReason);
            Assert.AreEqual("linked", mine.countRule);
            CityGrowthDto city = Call<CityGrowthDto>(done => backend.GetCityGrowthDetail(CityKit.Haeundae, done));
            Assert.AreEqual(0, city.validResidents);
            Assert.AreEqual(0, city.stage);
            Assert.AreEqual(100, city.nextThreshold);

            // 게스트 계정을 여러 개 만들어 같은 도시에 전입해도 마찬가지다.
            for (int i = 0; i < 3; i++)
            {
                var kit = new MockKit();
                LocalMockBackend other = new LocalMockBackend(kit.Store, _kit.Clock, CityKit.QuietDefs(), 100 + i);
                MockKit.Boot(other);
                CityKit.Register(other, CityKit.Haeundae, new[] { "초록고래", "노란고래", "붉은고래" }[i]);
                Assert.AreEqual(0, Call<CityGrowthDto>(done => other.GetCityGrowthDetail(CityKit.Haeundae, done)).validResidents);
            }
        }

        [Test]
        public void Growth_StagesFollowTheThresholds()
        {
            LocalMockBackend backend = _kit.NewBackend(CityKit.QuietDefs());
            MockKit.Boot(backend);
            backend.MockSetOtherResidents(CityKit.Haeundae, 99);
            backend.MockSetOtherResidents(CityKit.BusanJung, 100);
            backend.MockSetOtherResidents(CityKit.SeoulJongno, 1200);

            CityGrowthListDto list = Call<CityGrowthListDto>(backend.GetCityGrowth);
            CollectionAssert.AreEqual(new[] { 100, 500, 1000, 3000 }, list.milestones.Select(m => m.threshold).ToArray());
            CollectionAssert.AreEqual(new[] { "상징물 제작 검토", "상징거리 제작 후보", "상징거리 개방 목표", "추가 특화 구역 검토" },
                list.milestones.Select(m => m.name).ToArray());
            Assert.AreEqual(1, list.milestones.Count(m => m.opensStreet));
            CollectionAssert.AreEqual(new[] { CityKit.SeoulJongno, CityKit.BusanJung, CityKit.Haeundae }, list.cities.Select(c => c.regionCode).ToArray(),
                "전입자가 많은 도시부터");

            CityGrowthDto small = list.cities[2];
            Assert.AreEqual(0, small.stage);
            Assert.IsNull(small.stageName);
            Assert.AreEqual(100, small.nextThreshold);
            CityGrowthDto town = list.cities[1];
            Assert.AreEqual(1, town.stage);
            Assert.AreEqual("상징물 제작 검토", town.stageName);
            Assert.AreEqual(500, town.nextThreshold);
            Assert.IsFalse(town.thresholdReached);

            CityGrowthDto big = Call<CityGrowthDto>(done => backend.GetCityGrowthDetail(CityKit.SeoulJongno, done));
            Assert.AreEqual(3, big.stage);
            Assert.AreEqual("상징거리 개방 목표", big.stageName);
            Assert.AreEqual(3000, big.nextThreshold);
            Assert.IsTrue(big.thresholdReached);
            CollectionAssert.AreEqual(new[] { true, true, true, false }, big.milestones.Select(m => m.reached).ToArray());
            // 기준에 닿았다고 상징거리가 열리지는 않는다: 제작·검수·배포를 거쳐야 한다.
            Assert.AreEqual(ModelingStatuses.NotStarted, big.modelingStatus);
            Assert.IsFalse(big.streetOpen);
            Assert.AreEqual("제작 전", ModelingStatuses.DisplayName(big.modelingStatus));

            Assert.AreEqual(ApiErrors.NotFound, MockKit.Error(Raw<CityGrowthDto>(done => backend.GetCityGrowthDetail("99999", done))));
            Assert.AreEqual(0, Call<CityContentListDto>(backend.GetCityContent).items.Count);
        }

        [Test]
        public void Move_ChangesTheHomeCity_UpdatesBothCities_AndChargesOnce()
        {
            LocalMockBackend backend = _kit.NewBackend(CityKit.QuietDefs());
            backend.CityOps.countGuests = true;
            MockKit.Boot(backend);
            CityKit.Register(backend);
            _kit.Earn(backend, 3000);
            long before = MockKit.Me(backend).gc;
            Assert.AreEqual(1, Call<CityGrowthDto>(done => backend.GetCityGrowthDetail(CityKit.Haeundae, done)).validResidents);

            // 전입한 지 7일이 지나야 이사할 수 있다.
            ResidencyInfoDto info = Call<ResidencyInfoDto>(backend.GetResidency);
            Assert.IsTrue(info.counted);
            Assert.IsTrue(info.moveEnabled);
            Assert.AreEqual(1000, info.moveFeeGc);
            Assert.AreEqual(7, info.moveCooldownDays);
            Assert.IsNotNull(info.canMoveAt);
            Assert.AreEqual(ApiErrors.Locked, MockKit.Error(Move(backend, CityKit.SeoulJongno, "move-early")));
            _kit.Clock.AddDays(7);
            _kit.Clock.Advance(60);
            Assert.IsNull(Call<ResidencyInfoDto>(backend.GetResidency).canMoveAt);

            Assert.AreEqual(ApiErrors.Conflict, MockKit.Error(Move(backend, CityKit.Haeundae, "move-same")), "이미 사는 도시");
            Assert.AreEqual(ApiErrors.NotFound, MockKit.Error(Move(backend, "99999", "move-nowhere")));
            Assert.AreEqual(ApiErrors.InvalidRequest, MockKit.Error(Move(backend, CityKit.SeoulJongno, "")));
            Assert.AreEqual(before, MockKit.Me(backend).gc, "실패한 이사는 수수료를 받지 않는다");

            MoveResultDto moved = MockKit.Ok(Move(backend, CityKit.SeoulJongno, "move-1"));
            Assert.AreEqual(CityKit.SeoulJongno, moved.regionCode);
            Assert.AreEqual("서울 종로구", moved.regionName);
            Assert.AreEqual(CityKit.Haeundae, moved.previousRegionCode);
            Assert.AreEqual(1000, moved.feeGc);
            Assert.AreEqual(before - 1000, moved.gc);
            Assert.IsNotNull(moved.canMoveAt);

            // 한 계정은 한 도시에만 산다: 이전 도시에서 빠지고 새 도시에 들어갔다.
            Assert.AreEqual(CityKit.SeoulJongno, MockKit.Me(backend).regionCode);
            Assert.AreEqual(0, Call<CityGrowthDto>(done => backend.GetCityGrowthDetail(CityKit.Haeundae, done)).validResidents);
            Assert.AreEqual(1, Call<CityGrowthDto>(done => backend.GetCityGrowthDetail(CityKit.SeoulJongno, done)).validResidents);
            RegionsDto regions = Call<RegionsDto>(backend.GetRegions);
            Assert.AreEqual(1, regions.regions.Sum(r => r.residents), "주민 수의 합은 그대로다");
            Assert.AreEqual(1, regions.regions.Single(r => r.regionCode == CityKit.SeoulJongno).residents);

            // 같은 요청을 다시 보내도 한 번만 이사하고 한 번만 낸다.
            MoveResultDto again = MockKit.Ok(Move(backend, CityKit.SeoulJongno, "move-1"));
            Assert.AreEqual(CityKit.SeoulJongno, again.regionCode);
            Assert.AreEqual(before - 1000, again.gc);
            Assert.AreEqual(before - 1000, MockKit.Me(backend).gc);
            Assert.AreEqual(ApiErrors.Conflict, MockKit.Error(Move(backend, CityKit.BusanJung, "move-1")), "같은 번호를 다른 일에 쓸 수 없다");
            // 곧바로 또 이사할 수는 없다.
            Assert.AreEqual(ApiErrors.Locked, MockKit.Error(Move(backend, CityKit.Haeundae, "move-2")));

            // 원장에 이사 수수료가 한 줄 남고, 잔액과 맞는다.
            Assert.AreEqual(1, backend.CurrentLedger.Count(l => l.reason == LedgerReasons.ResidencyMove));
            Assert.AreEqual(-1000, backend.CurrentLedger.Single(l => l.reason == LedgerReasons.ResidencyMove).delta);
            Assert.IsNull(backend.FindLedgerInconsistency());

            // 다시 켜도 그대로다.
            LocalMockBackend reloaded = _kit.NewBackend(CityKit.QuietDefs());
            reloaded.CityOps.countGuests = true;
            Assert.AreEqual(CityKit.SeoulJongno, MockKit.Boot(reloaded).regionCode);
            Assert.AreEqual(1, Call<CityGrowthDto>(done => reloaded.GetCityGrowthDetail(CityKit.SeoulJongno, done)).validResidents);
        }

        [Test]
        public void Move_NeedsResidency_Funds_AndCanBeClosedByTheOperator()
        {
            LocalMockBackend backend = _kit.NewBackend(CityKit.QuietDefs());
            MockKit.Boot(backend);
            Assert.AreEqual("stranger", Call<ResidencyInfoDto>(backend.GetResidency).residency);
            Assert.AreEqual(ApiErrors.Conflict, MockKit.Error(Move(backend, CityKit.SeoulJongno, "m1")), "이방인은 전입신고부터");

            CityKit.Register(backend);
            _kit.Clock.AddDays(8);
            long gc = MockKit.Me(backend).gc;
            Assert.Less(gc, 1000);
            Assert.AreEqual(ApiErrors.InsufficientFunds, MockKit.Error(Move(backend, CityKit.SeoulJongno, "m2")));
            Assert.AreEqual(CityKit.Haeundae, MockKit.Me(backend).regionCode);
            Assert.AreEqual(gc, MockKit.Me(backend).gc);

            backend.CityOps.moveEnabled = false;
            Assert.IsFalse(Call<ResidencyInfoDto>(backend.GetResidency).moveEnabled);
            Assert.AreEqual(ApiErrors.Locked, MockKit.Error(Move(backend, CityKit.SeoulJongno, "m3")));

            // 수수료가 0 이면 원장에 줄이 남지 않는다.
            backend.CityOps.moveEnabled = true;
            backend.CityOps.moveFeeGc = 0;
            MoveResultDto moved = MockKit.Ok(Move(backend, CityKit.SeoulJongno, "m4"));
            Assert.AreEqual(0, moved.feeGc);
            Assert.AreEqual(gc, moved.gc);
            Assert.AreEqual(0, backend.CurrentLedger.Count(l => l.reason == LedgerReasons.ResidencyMove));
        }

        [Test]
        public void Taxes_AreVirtual_AndOffUntilTheOperatorTurnsThemOn()
        {
            LocalMockBackend backend = _kit.NewBackend(CityKit.QuietDefs());
            MockKit.Boot(backend);
            CityKit.Register(backend);

            TaxPoliciesDto taxes = Call<TaxPoliciesDto>(backend.GetTaxPolicies);
            CollectionAssert.AreEqual(new[] { TaxTypes.Trade, TaxTypes.Business, TaxTypes.Property }, taxes.policies.Select(p => p.taxType).ToArray());
            CollectionAssert.AreEqual(new[] { "거래세", "사업세", "재산세" }, taxes.policies.Select(p => p.name).ToArray());
            Assert.IsTrue(taxes.policies.All(p => !p.active && p.exempt), "기본은 꺼져 있다");
            Assert.IsTrue(taxes.policies.All(p => p.minLevel > 1 && p.graceDays > 0), "초보자 보호 기준이 있다");
            Assert.AreEqual("3%", taxes.policies[0].RateText);
            Assert.IsTrue(taxes.policies[0].included, "거래세는 값에 들어 있다");
            StringAssert.Contains("실제 돈", taxes.notice);
            StringAssert.Contains("현실의 세금", taxes.notice);

            // 꺼져 있는 동안에는 무엇을 사고 벌어도 세금이 걷히지 않는다.
            _kit.Earn(backend, 2000);
            long gc = MockKit.Me(backend).gc;
            TaxTransactionsDto paid = Call<TaxTransactionsDto>(backend.GetTaxTransactions);
            Assert.AreEqual(0, paid.transactions.Count);
            Assert.AreEqual(gc, paid.gc);

            TreasuryDto treasury = Call<TreasuryDto>(done => backend.GetTreasury(CityKit.Haeundae, done));
            Assert.AreEqual(0, treasury.generalGc + treasury.reserveGc);
            Assert.AreEqual(3, treasury.income.Count);
            StringAssert.Contains("CITY PASS", treasury.notice);
            StringAssert.Contains("장학", treasury.notice);
            Assert.AreEqual(0, Call<ExpendituresDto>(done => backend.GetExpenditures(CityKit.Haeundae, done)).expenditures.Count);
            Assert.AreEqual(ApiErrors.NotFound, MockKit.Error(Raw<TreasuryDto>(done => backend.GetTreasury("99999", done))));

            // 재정과 세금의 응답에는 현실의 돈을 뜻하는 칸이 없다.
            var real = new Regex("krw|won|cash|bank|account(?!$)|card|pay", RegexOptions.IgnoreCase);
            foreach (Type dto in new[] { typeof(TaxPolicyDto), typeof(TaxPoliciesDto), typeof(TaxTransactionDto), typeof(TreasuryDto),
                         typeof(TreasuryLineDto), typeof(ExpenditureDto), typeof(BudgetProposalDto), typeof(BudgetOptionsDto) })
                foreach (FieldInfo field in dto.GetFields())
                    Assert.IsFalse(real.IsMatch(field.Name), $"{dto.Name}.{field.Name}");
        }

        [Test]
        public void Budget_IsClosedUntilOpened_AndVotesAreOnePerAccount()
        {
            LocalMockBackend backend = _kit.NewBackend(CityKit.QuietDefs());
            MockKit.Boot(backend);
            CityKit.Register(backend);

            BudgetOptionsDto options = Call<BudgetOptionsDto>(backend.GetBudgetOptions);
            Assert.IsFalse(options.enabled);
            Assert.AreEqual(6, options.categories.Count);
            Assert.IsFalse(options.categories.Any(c => c.category == "reserve"), "예비비는 제안할 수 없다");
            StringAssert.Contains("한 사람이 한 표", options.notice);
            BudgetListDto list = Call<BudgetListDto>(done => backend.GetBudgetProposals(CityKit.Haeundae, done));
            Assert.IsFalse(list.options.enabled);
            Assert.AreEqual(0, list.proposals.Count);
            Assert.AreEqual(ApiErrors.FeatureDisabled, MockKit.Error(Raw<BudgetProposalResultDto>(done =>
                backend.ProposeBudget("education", "", "도서관", "어린이 도서관을 지어요", 1000, "p1", done))));
            Assert.AreEqual(ApiErrors.FeatureDisabled, MockKit.Error(Raw<BudgetProposalResultDto>(done => backend.VoteBudget("b_x", "yes", "v1", done))));

            // 제안의 응답에는 표의 무게나 낸 세금이 없다: 표는 찬성과 반대의 수뿐이다.
            foreach (FieldInfo field in typeof(BudgetProposalDto).GetFields())
                Assert.IsFalse(Regex.IsMatch(field.Name, "weight|tax|gcHeld|balance", RegexOptions.IgnoreCase), field.Name);
            Assert.AreEqual("주민 투표 중", BudgetStatuses.DisplayName(BudgetStatuses.Voting));
        }

        [Test]
        public void OnlineCoop_IsClosedInTheMock_ButPracticeNeedsNoServer()
        {
            LocalMockBackend backend = _kit.NewBackend(CityKit.QuietDefs());
            MockKit.Boot(backend);
            CityKit.Register(backend);

            CoopConfigDto config = Call<CoopConfigDto>(backend.GetCoopConfig);
            Assert.IsFalse(config.available);
            Assert.AreEqual("realtime_not_configured", config.unavailableReason);
            Assert.AreEqual("실시간 서버가 준비되지 않았어요.", config.UnavailableMessage);
            Assert.IsFalse(config.roleRequired, "역할은 강제가 아니다");
            CollectionAssert.AreEqual(new[] { "commander", "builder", "support", "fighter" }, config.roles.Select(r => r.code).ToArray());
            Assert.AreEqual(4, config.defaultPlayers);
            Assert.AreEqual(8, config.maxPlayers);
            Assert.AreEqual(0, Call<CoopSessionsDto>(backend.GetCoopSessions).sessions.Count);
            Assert.AreEqual(ApiErrors.FeatureDisabled, MockKit.Error(Raw<CoopSessionResultDto>(done =>
                backend.CreateCoopSession(StageCodes.Outskirts1, 4, null, "c1", done))));
            Assert.AreEqual(ApiErrors.FeatureDisabled, MockKit.Error(Raw<CoopTicketDto>(done => backend.GetCoopTicket("d_x", done))));
            Assert.AreEqual(ApiErrors.FeatureDisabled, MockKit.Error(Raw<CoopResultDto>(done => backend.GetCoopResult("d_x", done))));

            // 연습전의 설정은 게임 정의에서 만든다.
            CoopSimConfig practice = CoopPractice.Config(backend.Defs, 4);
            Assert.AreEqual(StageCodes.Outskirts1, practice.zone.code);
            Assert.AreEqual(1, practice.rules.minPlayersToStart);
            Assert.IsTrue(practice.structures.All(s => s.IsBuildable));
            Assert.IsTrue(practice.structures.Any(s => s.code == TowerCodes.MachineGun));
            Assert.AreEqual(4, practice.maxPlayers);
        }
    }

    /// <summary>협동 방어전 연습전 (LocalCoopLink) 과, 온라인 연결(RemoteCoopLink)이 서버의 메시지를 읽는 방법.</summary>
    public sealed class CoopLinkTests
    {
        private static LocalCoopLink Practice(int helpers = 3, params string[] unlocked)
        {
            if (unlocked.Length == 0) unlocked = new[] { TowerCodes.MachineGun };
            return new LocalCoopLink(CoopPractice.Config(MockKit.Defs(), 4), "푸른고래", 3, unlocked, helpers);
        }

        private static void Run(ICoopLink link, float seconds)
        {
            for (float t = 0f; t < seconds; t += 0.1f) link.Update(0.1f);
        }

        [Test]
        public void Practice_StartsInALobby_WithNpcHelpers_ClearlyLabelled()
        {
            LocalCoopLink link = Practice();
            Assert.IsTrue(link.Practice);
            Assert.AreEqual(CoopLinkStates.Lobby, link.State);
            Assert.AreEqual(4, link.Snapshot.players.Count);
            CoopPlayerView me = link.Snapshot.FindPlayer(link.MyPid);
            Assert.AreEqual("푸른고래", me.name);
            Assert.IsTrue(me.host);
            Assert.IsFalse(me.npc);
            Assert.IsNull(me.role, "역할을 고르지 않아도 된다");
            foreach (CoopPlayerView helper in link.Snapshot.players.Where(p => p.pid != link.MyPid))
            {
                // NPC 는 실제 플레이어인 것처럼 보이지 않는다.
                Assert.IsTrue(helper.npc);
                StringAssert.StartsWith(CoopPractice.NpcTag, helper.name);
                Assert.IsNotNull(helper.role);
            }
            Assert.AreEqual(16, link.Map.slots.Length);
            Assert.IsTrue(link.Structures.Single(s => s.def.code == TowerCodes.MachineGun).usable);
            Assert.IsFalse(link.Structures.Single(s => s.def.code == TowerCodes.Blast).usable, "해금하지 않은 타워");
            Assert.AreEqual(4, link.Roles.Count);

            // 대기실에서는 시간이 가도 전투가 시작되지 않는다.
            Run(link, 5f);
            Assert.IsTrue(link.Snapshot.IsLobby);
            Assert.AreEqual(0, link.Snapshot.energy);

            // 혼자서도 해 볼 수 있다.
            LocalCoopLink alone = Practice(0);
            Assert.AreEqual(1, alone.Snapshot.players.Count);
            alone.Start();
            Assert.AreEqual(CoopLinkStates.Battle, alone.State);
            Assert.AreEqual(200, alone.Snapshot.energy);
        }

        [Test]
        public void Practice_BuildUpgradeRepairPing_AnswerLikeTheServerDoes()
        {
            LocalCoopLink link = Practice();
            link.Start();
            Assert.AreEqual(CoopLinkStates.Battle, link.State);
            Assert.IsTrue(link.DrainEvents().Any(e => e.type == CoopEventTypes.Started && e.value == 4));
            Assert.AreEqual(500, link.Snapshot.energy, "네 명: 200 × 250%");
            link.DrainResults();

            string build = link.Build(8, TowerCodes.MachineGun);
            CoopActionResult built = link.DrainResults().Single();
            Assert.AreEqual(build, built.requestId);
            Assert.IsTrue(built.ok);
            Assert.AreEqual(400, built.energy);
            Assert.AreEqual(link.MyPid, link.Snapshot.FindTower(8).owner);

            link.Build(8, TowerCodes.MachineGun);
            Assert.AreEqual(CoopRejects.SlotTaken, link.DrainResults().Single().reject);
            link.Build(9, TowerCodes.Blast);
            Assert.AreEqual(CoopRejects.NotUnlocked, link.DrainResults().Single().reject);
            link.Upgrade(8);
            Assert.IsTrue(link.DrainResults().Single().ok);
            Assert.AreEqual(1, link.Snapshot.FindTower(8).level);
            link.Repair(8);
            Assert.AreEqual(CoopRejects.NotDamaged, link.DrainResults().Single().reject);
            Assert.AreEqual("고칠 곳이 없어요.", CoopTexts.Reject(CoopRejects.NotDamaged));

            // 핑: 지도에 보이고, 너무 자주 보내면 막힌다.
            link.Ping(PingKinds.DefendHere, 0f, 10f);
            Assert.AreEqual(1, link.Snapshot.pings.Count);
            Assert.AreEqual(PingKinds.DefendHere, link.Snapshot.pings[0].kind);
            Assert.IsTrue(link.DrainEvents().Any(e => e.type == CoopEventTypes.Ping && e.pid == link.MyPid));
            for (int i = 0; i < 6; i++) link.Ping(PingKinds.Danger, i, 0f);
            Assert.IsTrue(link.DrainResults().Any(r => r.action == "ping" && r.reject == CoopRejects.PingRate));
            link.Ping("insult", 0f, 0f);
            Assert.AreEqual(CoopRejects.PingKind, link.DrainResults().Last().reject);

            // 역할: 고를 수도, 내려놓을 수도 있다.
            link.SetRole(CoopRoleCodes.Commander);
            Assert.AreEqual(CoopRoleCodes.Commander, link.Snapshot.FindPlayer(link.MyPid).role);
            link.SetRole(null);
            Assert.IsNull(link.Snapshot.FindPlayer(link.MyPid).role);
            link.SetRole("king");
            Assert.AreEqual(CoopRejects.BadRole, link.DrainResults().Last().reject);

            // 움직임도 서버와 같은 속도로만.
            link.Move(20f, -14f);
            link.Update(1f);
            Assert.Less(link.Snapshot.FindPlayer(link.MyPid).x, -3f + 6f * 1f + 0.01f);
            Assert.Greater(link.Snapshot.FindPlayer(link.MyPid).x, -3f);
        }

        [Test]
        public void Practice_PlaysToTheEnd_WithoutRewards()
        {
            LocalCoopLink link = Practice(3, TowerCodes.MachineGun, TowerCodes.Blast);
            link.Start();
            link.Move(0f, -4f);
            int[] slots = { 8, 10, 11, 14, 7, 5, 3, 2, 12, 0, 1, 4 };
            for (int step = 0; step < 12000 && link.Finish == null; step++)
            {
                link.Update(0.1f);
                if (step % 20 != 0) continue;
                // 예산이 생기는 대로 세우고 강화한다.
                int free = Array.FindIndex(slots, s => link.Snapshot.FindTower(s) == null);
                if (free >= 0 && link.Snapshot.energy >= 100) link.Build(slots[free], TowerCodes.MachineGun);
                else
                {
                    CoopTowerView weak = link.Snapshot.towers.OrderBy(t => t.level).FirstOrDefault();
                    if (weak != null && link.Snapshot.energy >= 80) link.Upgrade(weak.slot);
                }
            }
            Assert.IsNotNull(link.Finish, "전투가 끝난다");
            Assert.AreEqual(CoopLinkStates.Finished, link.State);
            Assert.IsTrue(link.Finish.practice);
            Assert.IsFalse(link.Finish.recordKnown, "연습전은 서버에 기록하지 않는다");
            Assert.IsTrue(link.Finish.Won, "웨이브 " + link.Finish.wavesCleared + ", 관문 " + link.Finish.gateHpLeft);
            Assert.AreEqual(5, link.Finish.wavesCleared);
            Assert.AreEqual(CoopCloseReasons.Completed, link.Finish.closeReason);
            // NPC 설치 담당은 몇 개만 세운다. 나머지는 플레이어의 몫이다.
            int npcTowers = link.Snapshot.players.Where(p => p.npc).Sum(p => p.towersBuilt);
            Assert.GreaterOrEqual(npcTowers, 1);
            Assert.LessOrEqual(npcTowers, 4);
            Assert.Greater(link.Snapshot.FindPlayer(link.MyPid).towersBuilt, 0);
            // 공동 예산 장부는 연습전에서도 맞는다.
            Assert.IsNull(link.Sim.Budget.FindInconsistency());
            Assert.GreaterOrEqual(link.Snapshot.energy, 0);

            // 끝난 뒤에는 더 흐르지 않는다.
            long tick = link.Snapshot.tick;
            Run(link, 3f);
            Assert.AreEqual(tick, link.Snapshot.tick);
        }

        [Test]
        public void Practice_Leave_ClosesTheLink()
        {
            LocalCoopLink link = Practice();
            link.Start();
            Run(link, 2f);
            link.Leave();
            Assert.AreEqual(CoopLinkStates.Closed, link.State);
            Assert.AreEqual("left", link.ClosedReason);
            long tick = link.Snapshot.tick;
            Run(link, 2f);
            Assert.AreEqual(tick, link.Snapshot.tick);
            Assert.IsNull(link.Finish);
        }

        [Test]
        public void Snapshot_FeedsTheMinimap()
        {
            LocalCoopLink link = Practice();
            link.Start();
            link.Build(8, TowerCodes.MachineGun);
            link.Ping(PingKinds.RepairHere, 0f, 12f);
            Run(link, 12f);
            Assert.Greater(link.Snapshot.enemies.Count, 0);

            MinimapView corner = Minimap.Build(link.Snapshot.ToMinimap(link.Map, link.MyPid), new MinimapOptions(), link.PingRules);
            Assert.AreEqual(1, corner.blips.Count(b => b.kind == MinimapBlipKinds.Self));
            Assert.AreEqual(3, corner.blips.Count(b => b.kind == MinimapBlipKinds.Teammate));
            Assert.AreEqual(1, corner.blips.Count(b => b.kind == MinimapBlipKinds.Gate));
            Assert.IsFalse(corner.expanded);
            MinimapView full = Minimap.Build(link.Snapshot.ToMinimap(link.Map, link.MyPid), new MinimapOptions { expanded = true }, link.PingRules);
            Assert.AreEqual(link.Snapshot.enemies.Count, full.blips.Count(b => b.kind == MinimapBlipKinds.Enemy || b.kind == MinimapBlipKinds.Boss));
            Assert.IsTrue(full.blips.Where(b => b.kind == MinimapBlipKinds.Enemy).All(b => b.heading >= 0f), "적이 향하는 방향");
            Assert.GreaterOrEqual(full.blips.Count(b => b.kind == MinimapBlipKinds.Tower), 1);
            Assert.AreEqual(8, full.route.Count);

            // 같은 순간의 시뮬레이션에서 바로 만든 것과 같다.
            MinimapView direct = Minimap.Build(MinimapSource.FromSim(link.Sim, link.MyPid), new MinimapOptions { expanded = true }, link.PingRules);
            Assert.AreEqual(direct.blips.Count, full.blips.Count);
            for (int i = 0; i < direct.blips.Count; i++)
            {
                Assert.AreEqual(direct.blips[i].kind, full.blips[i].kind);
                Assert.AreEqual(direct.blips[i].u, full.blips[i].u, 0.0001f);
                Assert.AreEqual(direct.blips[i].v, full.blips[i].v, 0.0001f);
            }
        }

        // ── 온라인 연결이 서버의 메시지를 읽는 방법 ─────────────

        private sealed class FakeSocket : ICoopSocket
        {
            public readonly List<string> Sent = new List<string>();
            public readonly Queue<string> Incoming = new Queue<string>();
            public bool IsOpen { get; set; } = true;
            public bool IsClosed { get; set; }
            public string CloseReason { get; set; }
            public void Send(string json) => Sent.Add(json);
            public bool TryReceive(out string json)
            {
                json = Incoming.Count > 0 ? Incoming.Dequeue() : null;
                return json != null;
            }
            public void Close()
            {
                IsOpen = false;
                IsClosed = true;
            }
        }

        private const string State0 =
            "{\"type\":\"state\",\"tick\":10,\"phase\":\"lobby\",\"time_ms\":0,\"wave\":0,\"wave_count\":5,\"waves_cleared\":0,\"rest_ms\":0," +
            "\"gate_hp\":20,\"gate_max_hp\":20,\"energy\":0,\"players\":[{\"pid\":\"p_me\",\"name\":\"푸른고래\",\"role\":null,\"level\":3," +
            "\"host\":true,\"connected\":true,\"npc\":false,\"x\":-3,\"z\":-14,\"kills\":0,\"towers_built\":0,\"towers_upgraded\":0,\"repairs\":0}]," +
            "\"enemies\":[],\"towers\":[],\"pings\":[],\"dangers\":[]}";

        private static string Welcome() =>
            "{\"type\":\"welcome\",\"you\":\"p_me\",\"session\":{\"id\":\"d_abc\",\"region_code\":\"26350\",\"region_name\":\"해운대구\"," +
            "\"host_pid\":\"p_me\",\"max_players\":4},\"tick_ms\":100,\"snapshot_every_ticks\":2,\"zone\":{\"code\":\"outskirts_1\",\"name\":\"외곽 1구역\"," +
            "\"wave_count\":5},\"map\":{\"min_x\":-26,\"max_x\":26,\"min_z\":-26,\"max_z\":44,\"path\":[{\"x\":0,\"z\":40},{\"x\":0,\"z\":-20}]," +
            "\"slots\":[{\"x\":4,\"z\":32},{\"x\":-4,\"z\":32}],\"landmarks\":[{\"code\":\"gate\",\"name\":\"도시 관문\",\"kind\":\"gate\",\"x\":0,\"z\":-20}]}," +
            "\"structures\":[{\"code\":\"twr_machine_gun\",\"name\":\"기관총 타워\",\"description\":\"\",\"kind\":\"gun\",\"tier\":\"basic\",\"energy_cost\":100," +
            "\"upgrade_energy_cost\":60,\"max_upgrade_level\":2,\"damage\":8,\"shots_per_second\":2.5,\"range\":8,\"splash_radius\":0,\"max_hp\":120," +
            "\"max_per_battle\":0,\"effect_value\":0,\"effect_seconds\":0,\"usable\":true}],\"roles\":[{\"code\":\"builder\",\"name\":\"타워 설치 담당\"," +
            "\"description\":\"\",\"build_discount_percent\":10,\"repair_bonus_percent\":0,\"attack_bonus_percent\":0,\"ping_cooldown_percent\":0," +
            "\"max_per_session\":0}],\"rules\":{\"min_players_to_start\":2,\"repair_energy\":10,\"repair_hp\":30,\"repair_range_m\":8," +
            "\"player_range_m\":14,\"player_speed\":6},\"ping\":{\"enabled\":1,\"burst\":3,\"refill_seconds\":4,\"lifetime_seconds\":8," +
            "\"max_active_per_player\":3,\"route_overlay\":1,\"range_overlay\":0,\"kinds\":[\"focus_fire\"],\"blocked\":true},\"state\":" + State0 + "}";

        [Test]
        public void Remote_SendsTheTicketFirst_AndOnlyRequests()
        {
            var socket = new FakeSocket { IsOpen = false };
            var link = new RemoteCoopLink(socket, "abc.def");
            link.Update(0.1f);
            Assert.AreEqual(0, socket.Sent.Count, "연결되기 전에는 보내지 않는다");
            link.Start();
            Assert.IsNull(link.Build(0, "twr_machine_gun"), "들어가기 전의 요청은 버린다");

            socket.IsOpen = true;
            link.Update(0.1f);
            Assert.AreEqual("{\"type\":\"join\",\"ticket\":\"abc.def\"}", socket.Sent.Single());
            Assert.AreEqual(CoopLinkStates.Connecting, link.State);

            socket.Incoming.Enqueue(Welcome());
            link.Update(0.1f);
            Assert.AreEqual(CoopLinkStates.Lobby, link.State);
            Assert.AreEqual("p_me", link.MyPid);
            Assert.AreEqual("해운대구", link.RegionName);
            Assert.AreEqual(2, link.Map.slots.Length);
            Assert.AreEqual(60f, link.Map.PathLength, 0.001f);
            Assert.AreEqual(2.5f, link.Structures[0].def.shotsPerSecond);
            Assert.AreEqual(10, link.Roles[0].buildDiscountPercent);
            Assert.AreEqual(0, link.PingRules.rangeOverlay);
            Assert.IsTrue(link.PingBlocked);
            Assert.IsNull(link.Snapshot.players[0].role);
            socket.Sent.Clear();

            // 요청은 "무엇을 하겠다"뿐이다: 예산, 위치의 결과, 보상을 보내지 않는다.
            link.Start();
            string build = link.Build(1, "twr_machine_gun");
            string upgrade = link.Upgrade(1);
            string repair = link.Repair(1);
            link.Ping(PingKinds.FocusFire, 1.5f, -2.25f);
            link.HidePings("p_other", true);
            link.SetRole("builder");
            link.SetRole(null);
            link.Focus(7);
            CollectionAssert.AreEqual(new[]
            {
                "{\"type\":\"start\"}",
                "{\"type\":\"build\",\"request_id\":\"" + build + "\",\"slot\":1,\"code\":\"twr_machine_gun\"}",
                "{\"type\":\"upgrade\",\"request_id\":\"" + upgrade + "\",\"slot\":1}",
                "{\"type\":\"repair\",\"request_id\":\"" + repair + "\",\"slot\":1}",
                "{\"type\":\"ping\",\"kind\":\"focus_fire\",\"x\":1.5,\"z\":-2.25}",
                "{\"type\":\"hide_pings\",\"pid\":\"p_other\",\"hide\":true}",
                "{\"type\":\"set_role\",\"role\":\"builder\"}",
                "{\"type\":\"set_role\",\"role\":null}",
                "{\"type\":\"focus\",\"enemy_id\":7}",
            }, socket.Sent);
            Assert.AreNotEqual(build, upgrade);
            foreach (string sent in socket.Sent)
                Assert.IsFalse(Regex.IsMatch(sent, "energy|reward|gc|hp|kills|result"), sent);

            // 움직임은 0.1초에 한 번만 보낸다. 같은 곳이면 다시 보내지 않는다.
            socket.Sent.Clear();
            for (int i = 0; i < 10; i++)
            {
                link.Move(i, 0f);
                link.Update(0.02f);
            }
            Assert.AreEqual(2, socket.Sent.Count);
            StringAssert.StartsWith("{\"type\":\"move\",\"x\":", socket.Sent[0]);
            link.Update(0.2f);
            socket.Sent.Clear();
            link.Move(9f, 0f);
            link.Update(0.2f);
            Assert.AreEqual(0, socket.Sent.Count);
            // 이상한 값은 0 으로 보낸다 (서버도 받지 않지만).
            link.Move(float.NaN, float.PositiveInfinity);
            link.Update(0.2f);
            Assert.AreEqual("{\"type\":\"move\",\"x\":0,\"z\":0}", socket.Sent.Single());

            // 가만히 있어도 살아 있다고 알린다.
            socket.Sent.Clear();
            link.Update(10.5f);
            Assert.AreEqual("{\"type\":\"alive\"}", socket.Sent.Single());

            // 따옴표가 든 값이 메시지를 깨뜨리지 못한다.
            socket.Sent.Clear();
            link.Ping("x\",\"kind\":\"rally", 0f, 0f);
            Assert.AreEqual("{\"type\":\"ping\",\"kind\":\"x\\\",\\\"kind\\\":\\\"rally\",\"x\":0,\"z\":0}", socket.Sent.Single());
        }

        [Test]
        public void Remote_ReadsStateEventsResults_AndTheEnd()
        {
            var socket = new FakeSocket();
            var link = new RemoteCoopLink(socket, "t");
            link.Update(0f);
            socket.Incoming.Enqueue(Welcome());
            socket.Incoming.Enqueue(
                "{\"type\":\"state\",\"tick\":40,\"phase\":\"wave\",\"time_ms\":3000,\"wave\":1,\"wave_count\":5,\"waves_cleared\":0,\"rest_ms\":0," +
                "\"gate_hp\":18,\"gate_max_hp\":20,\"energy\":210,\"players\":[{\"pid\":\"p_me\",\"name\":\"푸른고래\",\"role\":\"builder\",\"level\":3," +
                "\"host\":true,\"connected\":true,\"npc\":false,\"x\":-2.5,\"z\":-10,\"kills\":2,\"towers_built\":1,\"towers_upgraded\":0,\"repairs\":0}," +
                "{\"pid\":\"p_b\",\"name\":\"은빛여우\",\"role\":null,\"level\":1,\"host\":false,\"connected\":false,\"npc\":false,\"x\":3,\"z\":-14,\"kills\":0," +
                "\"towers_built\":0,\"towers_upgraded\":0,\"repairs\":0}],\"enemies\":[{\"id\":5,\"code\":\"mech_insect\",\"x\":0,\"z\":30,\"dx\":0,\"dz\":-1," +
                "\"hp\":20,\"max_hp\":32,\"slowed\":true,\"stunned\":false,\"boss\":false}],\"towers\":[{\"slot\":1,\"code\":\"twr_machine_gun\",\"level\":1," +
                "\"hp\":90,\"max_hp\":120,\"owner\":\"p_me\",\"range\":8.8}],\"pings\":[{\"id\":3,\"pid\":\"p_b\",\"kind\":\"repair_here\",\"x\":-4,\"z\":32," +
                "\"left_ms\":5200}],\"dangers\":[{\"x\":0,\"z\":30,\"enemies\":6,\"near_gate\":false}]}");
            socket.Incoming.Enqueue("{\"type\":\"event\",\"event\":\"tower_built\",\"value\":0,\"pid\":\"p_me\",\"slot\":1,\"code\":\"twr_machine_gun\"}");
            socket.Incoming.Enqueue("{\"type\":\"event\",\"event\":\"wave_started\",\"value\":1}");
            socket.Incoming.Enqueue("{\"type\":\"event\",\"event\":\"ping\",\"value\":0,\"pid\":\"p_b\",\"code\":\"repair_here\",\"x\":-4,\"z\":32,\"id\":3}");
            socket.Incoming.Enqueue("{\"type\":\"event\",\"event\":\"player_left\",\"pid\":\"p_b\",\"name\":\"은빛여우\"}");
            socket.Incoming.Enqueue("{\"type\":\"result\",\"action\":\"build\",\"request_id\":\"c1-1\",\"ok\":true,\"reject\":null,\"duplicate\":false,\"energy\":210}");
            socket.Incoming.Enqueue("{\"type\":\"result\",\"action\":\"upgrade\",\"request_id\":\"c1-2\",\"ok\":false,\"reject\":\"insufficient_budget\"," +
                                    "\"duplicate\":false,\"energy\":10}");
            socket.Incoming.Enqueue("{\"type\":\"reject\",\"action\":\"ping\",\"reason\":\"ping_blocked\"}");
            socket.Incoming.Enqueue("{\"type\":\"hidden\",\"pid\":\"p_b\",\"hide\":true}");
            socket.Incoming.Enqueue("not json");
            socket.Incoming.Enqueue("{\"type\":\"future_message\",\"anything\":[1,2,3]}");
            link.Update(0.1f);

            Assert.AreEqual(CoopLinkStates.Battle, link.State);
            CoopSnapshot s = link.Snapshot;
            Assert.AreEqual(40, s.tick);
            Assert.AreEqual(CoopPhaseNames.Wave, s.phase);
            Assert.AreEqual(18, s.gateHp);
            Assert.AreEqual(210, s.energy);
            Assert.AreEqual("builder", s.FindPlayer("p_me").role);
            Assert.AreEqual(-2.5f, s.FindPlayer("p_me").x);
            Assert.AreEqual(1, s.FindPlayer("p_me").towersBuilt);
            Assert.IsFalse(s.FindPlayer("p_b").connected);
            Assert.IsNull(s.FindPlayer("p_b").role);
            Assert.AreEqual(1, s.ConnectedCount);
            Assert.AreEqual(32, s.FindEnemy(5).maxHp);
            Assert.IsTrue(s.FindEnemy(5).slowed);
            Assert.AreEqual(-1f, s.FindEnemy(5).dz);
            Assert.AreEqual(8.8f, s.FindTower(1).range, 0.0001f);
            Assert.AreEqual(5200, s.pings.Single().leftMs);
            Assert.AreEqual(6, s.dangers.Single().enemies);

            List<CoopEvent> events = link.DrainEvents();
            CollectionAssert.AreEqual(new[] { "tower_built", "wave_started", "ping", "player_left" }, events.Select(e => e.type).ToArray());
            Assert.AreEqual(1, events[0].slot);
            Assert.AreEqual("twr_machine_gun", events[0].code);
            Assert.AreEqual(-1, events[1].slot, "자리가 없는 알림");
            Assert.AreEqual(1, events[1].value);
            Assert.AreEqual(-4f, events[2].x);
            Assert.AreEqual(3, events[2].id);
            Assert.AreEqual("p_b", events[3].pid);
            List<CoopActionResult> results = link.DrainResults();
            Assert.AreEqual(3, results.Count);
            Assert.IsTrue(results[0].ok);
            Assert.IsNull(results[0].reject);
            Assert.AreEqual("c1-1", results[0].requestId);
            Assert.AreEqual(CoopRejects.NoBudget, results[1].reject);
            Assert.AreEqual(10, results[1].energy);
            Assert.AreEqual(CoopRejects.PingBlocked, results[2].reject);
            Assert.AreEqual(-1, results[2].energy);
            Assert.IsTrue(link.IsHidden("p_b"));
            Assert.AreEqual(0, link.DrainEvents().Count, "꺼낸 것은 다시 나오지 않는다");

            // 늦게 온 옛 상태는 버린다.
            socket.Incoming.Enqueue(State0);
            link.Update(0.1f);
            Assert.AreEqual(40, link.Snapshot.tick);

            // 끝: 결과가 오고, 게임 서버가 받았는지가 따로 온다. 보상은 여기에 없다.
            socket.Incoming.Enqueue("{\"type\":\"finished\",\"result\":\"lose\",\"waves_cleared\":2,\"gate_hp_left\":0,\"duration_seconds\":95," +
                                    "\"close_reason\":\"gate_destroyed\"}");
            link.Update(0.1f);
            Assert.AreEqual(CoopLinkStates.Finished, link.State);
            Assert.IsFalse(link.Finish.Won);
            Assert.AreEqual(2, link.Finish.wavesCleared);
            Assert.AreEqual(CoopCloseReasons.GateDestroyed, link.Finish.closeReason);
            Assert.IsFalse(link.Finish.recordKnown);
            socket.Incoming.Enqueue("{\"type\":\"recorded\",\"ok\":false,\"accepted\":false,\"pending\":true}");
            link.Update(0.1f);
            Assert.IsTrue(link.Finish.recordKnown);
            Assert.IsFalse(link.Finish.recorded);
            Assert.IsTrue(link.Finish.pending);
            foreach (FieldInfo field in typeof(CoopFinish).GetFields())
                Assert.IsFalse(Regex.IsMatch(field.Name, "reward|gc|xp", RegexOptions.IgnoreCase), field.Name);

            // 끝난 뒤에는 요청을 보내지 않고, 서버가 연결을 닫아도 결과는 남는다.
            int sent = socket.Sent.Count;
            link.Build(0, "twr_machine_gun");
            link.Ping(PingKinds.Rally, 0f, 0f);
            Assert.AreEqual(sent, socket.Sent.Count);
            socket.Close();
            link.Update(0.1f);
            Assert.AreEqual(CoopLinkStates.Finished, link.State);
        }

        [Test]
        public void Remote_ReportsWhyTheConnectionEnded()
        {
            var refused = new RemoteCoopLink(new FakeSocket(), "t");
            var socket = new FakeSocket();
            var link = new RemoteCoopLink(socket, "t");
            link.Update(0f);
            socket.Incoming.Enqueue("{\"type\":\"error\",\"reason\":\"session_full\"}");
            link.Update(0.1f);
            Assert.AreEqual(CoopLinkStates.Closed, link.State);
            Assert.AreEqual("session_full", link.ClosedReason);
            Assert.AreEqual("자리가 없어요.", CoopTexts.Closed(link.ClosedReason));
            Assert.AreEqual(CoopLinkStates.Connecting, refused.State);

            socket = new FakeSocket();
            link = new RemoteCoopLink(socket, "t");
            link.Update(0f);
            socket.Incoming.Enqueue(Welcome());
            socket.Incoming.Enqueue("{\"type\":\"closed\",\"reason\":\"lobby_timeout\"}");
            link.Update(0.1f);
            Assert.AreEqual("lobby_timeout", link.ClosedReason);

            // 이유 없이 끊기면 "끊겼다"고만 안다. 서버가 닫으며 이유를 주면 그것을 쓴다.
            socket = new FakeSocket();
            link = new RemoteCoopLink(socket, "t");
            link.Update(0f);
            socket.Incoming.Enqueue(Welcome());
            link.Update(0.1f);
            socket.IsOpen = false;
            socket.IsClosed = true;
            link.Update(0.1f);
            Assert.AreEqual(CoopLinkStates.Closed, link.State);
            Assert.AreEqual("disconnected", link.ClosedReason);
            Assert.AreEqual("연결이 끊겼어요.", CoopTexts.Closed(link.ClosedReason));

            socket = new FakeSocket();
            link = new RemoteCoopLink(socket, "t");
            link.Update(0f);
            socket.Incoming.Enqueue(Welcome());
            link.Update(0.1f);
            socket.IsOpen = false;
            socket.IsClosed = true;
            socket.CloseReason = "replaced";
            link.Update(0.1f);
            Assert.AreEqual("replaced", link.ClosedReason);

            // 스스로 나가면 나간다고 알리고 닫는다.
            socket = new FakeSocket();
            link = new RemoteCoopLink(socket, "t");
            link.Update(0f);
            socket.Incoming.Enqueue(Welcome());
            link.Update(0.1f);
            link.Leave();
            Assert.AreEqual("{\"type\":\"leave\"}", socket.Sent.Last());
            Assert.IsTrue(socket.IsClosed);
            Assert.AreEqual("left", link.ClosedReason);
        }
    }
}
