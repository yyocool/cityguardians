using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CityGuardians.Domain;
using NUnit.Framework;

namespace CityGuardians.Tests
{
    /// <summary>
    /// v9.0 협동 타워디펜스의 규칙: 서버 권한 시뮬레이션, 한 자리에 타워 하나, 공동 예산의 원자적 차감, 역할, 중도 이탈
    /// (docs/COOP_DEFENSE_SYSTEM.md). 실시간 서버가 같은 코드를 돌린다.
    /// </summary>
    public sealed class CoopSimTests
    {
        private GameDefs _defs;

        [SetUp]
        public void SetUp() => _defs = DefaultDefs.Create();

        private CoopSimConfig Config(int maxPlayers = 4, StageDef zone = null)
        {
            return new CoopSimConfig
            {
                zone = zone ?? _defs.FindStage(StageCodes.Outskirts1), gateHp = 20, enemies = _defs.enemies, structures = _defs.towers,
                roles = CoopDefaults.Roles(), rules = new CoopRules(), ping = new PingRules(), maxPlayers = maxPlayers,
            };
        }

        private string[] Buildable() => _defs.towers.Where(t => t.IsBuildable).Select(t => t.code).ToArray();

        /// <summary>n 명이 들어온 대기실. p1 이 방장이다.</summary>
        private CoopSim Lobby(int n, CoopSimConfig config = null)
        {
            var sim = new CoopSim(config ?? Config(Math.Max(4, n)));
            for (int i = 1; i <= n; i++)
                Assert.IsTrue(sim.Join("p" + i, "시민" + i, null, 1, i == 1, Buildable(), false).Ok);
            return sim;
        }

        private CoopSim Started(int n, CoopSimConfig config = null)
        {
            var sim = Lobby(n, config);
            Assert.IsTrue(sim.Start("p1").Ok);
            return sim;
        }

        private static StageDef OneWave(string enemyCode, int count, float interval = 1f)
        {
            return new StageDef
            {
                code = "test_zone", name = "시험 구역", baseHp = 20, startEnergy = 200, waveClearEnergy = 50, restSeconds = 2f,
                waves = new List<WaveDef> { new WaveDef { groups = new List<WaveGroupDef> {
                    new WaveGroupDef { enemyCode = enemyCode, count = count, interval = interval, delay = 0f } } } },
                winGc = 300, winXp = 150, loseGcPerWave = 20, minSecondsPerWave = 10f,
            };
        }

        private static string[] Pids(CoopSim sim) => sim.Players.Select(p => p.pid).ToArray();

        // ── 지도 ────────────────────────────────────────────

        [Test]
        public void Map_IsConsistent_AndThePathRunsFromSpawnToGate()
        {
            CoopMap map = CoopMap.ForZone(StageCodes.Outskirts1);
            Assert.IsNull(map.FindProblem());
            Assert.AreEqual(116f, map.PathLength, 0.001f);
            Assert.AreEqual(16, map.slots.Length);
            Assert.AreEqual(0f, CoopVec.Distance(map.Spawn, map.PointAt(0f)), 0.001f);
            Assert.AreEqual(0f, CoopVec.Distance(map.Gate, map.PointAt(999f)), 0.001f);
            Assert.AreEqual(0f, CoopVec.Distance(new CoopVec(0f, 30f), map.PointAt(10f)), 0.001f);
            Assert.AreEqual(0f, CoopVec.Distance(new CoopVec(-4f, 24f), map.PointAt(20f)), 0.001f, "첫 굽이를 돈 뒤");
            Assert.AreEqual(-1f, map.DirectionAt(5f).z, 0.001f, "처음에는 남쪽으로 내려온다");
            Assert.AreEqual(-1f, map.DirectionAt(20f).x, 0.001f, "굽이를 돌면 서쪽으로");
            // 관문, 방어벽, 주요 시설이 있다.
            foreach (string kind in new[] { CoopLandmarkKinds.Gate, CoopLandmarkKinds.Wall, CoopLandmarkKinds.Facility, CoopLandmarkKinds.Spawn })
                Assert.IsTrue(map.landmarks.Any(l => l.kind == kind), kind);
            // 지도 밖의 점과 숫자가 아닌 값은 지도 안으로 들어온다.
            CoopVec clamped = map.Clamp(new CoopVec(999f, float.NaN));
            Assert.AreEqual(map.maxX, clamped.x);
            Assert.IsFalse(float.IsNaN(clamped.z));
        }

        // ── 시작 ────────────────────────────────────────────

        [Test]
        public void Start_IsHostOnly_NeedsEnoughPlayers_AndScalesBudgetAndEnemiesWithTeamSize()
        {
            CoopSim solo = Lobby(1);
            Assert.AreEqual(CoopRejects.NotEnoughPlayers, solo.Start("p1").reject, "기본값은 두 명부터");

            CoopSim sim = Lobby(4);
            Assert.AreEqual(CoopRejects.NotHost, sim.Start("p2").reject);
            Assert.AreEqual(CoopRejects.NotMember, sim.Start("nobody").reject);
            Assert.AreEqual(CoopRejects.NotRunning, sim.Build("p1", "r1", 3, TowerCodes.MachineGun).reject, "대기실에서는 세울 수 없다");
            Assert.AreEqual(0, sim.Budget.Balance);
            Assert.IsTrue(sim.Start("p1").Ok);
            Assert.AreEqual(CoopRejects.NotLobby, sim.Start("p1").reject, "두 번 시작되지 않는다");
            Assert.AreEqual(4, sim.PlayersAtStart);
            // 공동 예산: 200 × (100 + 50 × 3)% = 500.
            Assert.AreEqual(500, sim.Budget.Balance);
            Assert.AreEqual(1, sim.Budget.Lines.Count);
            Assert.AreEqual(CoopBudgetKinds.GrantStart, sim.Budget.Lines[0].kind);

            // 준비 시간 5초가 지나면 첫 웨이브가 나온다. 적 체력: 60 × (100 + 60 × 3)% = 168.
            Assert.AreEqual(CoopPhase.Rest, sim.Phase);
            sim.Run(CoopSim.StartCountdownSeconds);
            Assert.AreEqual(CoopPhase.Wave, sim.Phase);
            Assert.AreEqual(1, sim.Wave);
            Assert.AreEqual(1, sim.Enemies.Count);
            Assert.AreEqual(168, sim.Enemies[0].maxHp);

            CoopSim pair = Started(2);
            Assert.AreEqual(300, pair.Budget.Balance, "두 명이면 200 × 150%");
            pair.Run(CoopSim.StartCountdownSeconds);
            Assert.AreEqual(96, pair.Enemies[0].maxHp, "두 명이면 60 × 160%");
        }

        // ── 한 자리에 타워 하나 ─────────────────────────────

        [Test]
        public void TwoPlayersBuildingOnTheSameSlotAtOnce_OnlyTheFirstSucceeds_AndTheBudgetIsChargedOnce()
        {
            // 누가 먼저 처리되든 결과는 같다: 타워 하나, 차감 한 번.
            foreach (var order in new[] { new[] { "p1", "p2", "p3", "p4" }, new[] { "p4", "p3", "p2", "p1" }, new[] { "p2", "p4", "p1", "p3" } })
            {
                CoopSim sim = Started(4);
                var outcomes = order.Select(pid => sim.Build(pid, "build-" + pid, 6, TowerCodes.MachineGun)).ToArray();
                Assert.IsTrue(outcomes[0].Ok, order[0]);
                for (int i = 1; i < outcomes.Length; i++) Assert.AreEqual(CoopRejects.SlotTaken, outcomes[i].reject, order[i]);
                Assert.AreEqual(1, sim.Towers.Count());
                Assert.AreEqual(order[0], sim.TowerAt(6).ownerPid);
                Assert.AreEqual(400, sim.Budget.Balance, "100 이 한 번만 빠진다");
                Assert.AreEqual(1, sim.Budget.Lines.Count(l => l.kind == CoopBudgetKinds.Build));
                Assert.AreEqual(1, sim.Players.Sum(p => p.towersBuilt));
                Assert.IsNull(sim.Budget.FindInconsistency(Pids(sim)));
            }
        }

        [Test]
        public void Build_ChecksSlotStructureUnlockAndLimit()
        {
            CoopSim sim = Started(2);
            Assert.AreEqual(CoopRejects.BadSlot, sim.Build("p1", "a1", -1, TowerCodes.MachineGun).reject);
            Assert.AreEqual(CoopRejects.BadSlot, sim.Build("p1", "a2", 16, TowerCodes.MachineGun).reject);
            Assert.AreEqual(CoopRejects.BadStructure, sim.Build("p1", "a3", 0, "twr_nothing").reject);
            Assert.AreEqual(CoopRejects.BadStructure, sim.Build("p1", "a4", 0, TowerCodes.EnergyShield).reject, "도시 공용 시설은 세울 수 없다");
            Assert.AreEqual(CoopRejects.BadStructure, sim.Build("p1", "a5", 0, TowerCodes.Wall).reject, "준비 중인 시설은 세울 수 없다");
            Assert.AreEqual(CoopRejects.BadRequest, sim.Build("p1", "", 0, TowerCodes.MachineGun).reject);
            Assert.AreEqual(CoopRejects.BadRequest, sim.Build("p1", "요청 번호", 0, TowerCodes.MachineGun).reject);
            Assert.AreEqual(CoopRejects.NotMember, sim.Build("nobody", "a6", 0, TowerCodes.MachineGun).reject);

            // 해금하지 않은 타워는 그 사람이 세울 수 없다 (입장권에 실려 온 목록).
            sim.Join("p2", "시민2", null, 1, false, new[] { TowerCodes.MachineGun }, false);
            Assert.AreEqual(CoopRejects.NotUnlocked, sim.Build("p2", "b1", 0, TowerCodes.Slow).reject);
            Assert.IsTrue(sim.Build("p2", "b2", 0, TowerCodes.MachineGun).Ok);

            // 한 전투에 세울 수 있는 수 (감속 장치는 둘).
            Assert.IsTrue(sim.Build("p1", "c1", 1, TowerCodes.Slow).Ok);
            Assert.AreEqual(CoopRejects.NoBudget, sim.Build("p1", "c2", 2, TowerCodes.Slow).reject, "300 - 100 - 120 = 80 < 120");
            Assert.AreEqual(80, sim.Budget.Balance);
            Assert.AreEqual(2, sim.Towers.Count());
            Assert.IsNull(sim.Budget.FindInconsistency(Pids(sim)));

            var rich = Config(4);
            rich.zone = OneWave(EnemyCodes.MalfunctionRobot, 1);
            rich.zone.startEnergy = 1000;
            CoopSim many = Started(2, rich);
            Assert.IsTrue(many.Build("p1", "d1", 1, TowerCodes.Slow).Ok);
            Assert.IsTrue(many.Build("p2", "d2", 2, TowerCodes.Slow).Ok);
            Assert.AreEqual(CoopRejects.TooMany, many.Build("p1", "d3", 3, TowerCodes.Slow).reject);
            Assert.IsNull(many.TowerAt(3));
        }

        // ── 공동 예산 ───────────────────────────────────────

        [Test]
        public void SharedBudget_IsNeverOverdrawn_WhenSeveralPlayersBuyAtOnce()
        {
            CoopSim sim = Started(4);           // 500
            // 네 명이 같은 순간에 서로 다른 자리에 폭발 타워(175)를 산다: 둘만 된다.
            var outcomes = new List<CoopOutcome>();
            for (int i = 1; i <= 4; i++) outcomes.Add(sim.Build("p" + i, "buy", i, TowerCodes.Blast));
            Assert.AreEqual(2, outcomes.Count(o => o.Ok));
            Assert.AreEqual(2, outcomes.Count(o => o.reject == CoopRejects.NoBudget));
            Assert.AreEqual(150, sim.Budget.Balance);
            Assert.AreEqual(2, sim.Towers.Count());
            Assert.AreEqual(500, sim.Budget.Granted);
            Assert.AreEqual(350, sim.Budget.Spent);
            Assert.IsTrue(sim.Budget.Lines.All(l => l.balanceAfter >= 0));
            Assert.IsNull(sim.Budget.FindInconsistency(Pids(sim)));
            // 거절된 사람의 자리는 비어 있고, 그 사람의 기록도 늘지 않았다.
            Assert.AreEqual(2, sim.Players.Sum(p => p.towersBuilt));
        }

        [Test]
        public void SameRequestSentTwice_IsExecutedOnce()
        {
            CoopSim sim = Started(2);           // 300
            CoopOutcome first = sim.Build("p1", "req-1", 3, TowerCodes.MachineGun);
            CoopOutcome again = sim.Build("p1", "req-1", 3, TowerCodes.MachineGun);
            Assert.IsTrue(first.Ok && !first.duplicate);
            Assert.IsTrue(again.Ok && again.duplicate, "다시 보낸 요청은 처음 결과를 돌려받는다 (slot_taken 이 아니다)");
            Assert.AreEqual(200, sim.Budget.Balance);

            // 강화 요청을 연달아 두 번 누른 경우: 한 단계만 오른다.
            Assert.IsTrue(sim.Upgrade("p1", "up-1", 3).Ok);
            Assert.IsTrue(sim.Upgrade("p1", "up-1", 3).duplicate);
            Assert.AreEqual(1, sim.TowerAt(3).level);
            Assert.AreEqual(140, sim.Budget.Balance);
            Assert.AreEqual(1, sim.FindPlayer("p1").towersUpgraded);

            // 거절된 요청도 같은 답을 돌려준다. 예산이 생긴 뒤에 같은 번호로 다시 보내도 실행되지 않는다.
            Assert.AreEqual(CoopRejects.NoBudget, sim.Build("p2", "big", 4, TowerCodes.Laser).reject);
            CoopOutcome replay = sim.Build("p2", "big", 4, TowerCodes.Laser);
            Assert.IsTrue(replay.duplicate);
            Assert.AreEqual(CoopRejects.NoBudget, replay.reject);
            // 요청 번호는 사람마다 따로다: 다른 사람이 같은 번호를 써도 된다.
            Assert.IsTrue(sim.Build("p2", "req-1", 5, TowerCodes.MachineGun).Ok);
            Assert.AreEqual(40, sim.Budget.Balance);
            Assert.AreEqual(4, sim.Budget.Lines.Count);
            Assert.IsNull(sim.Budget.FindInconsistency(Pids(sim)));
        }

        [Test]
        public void Budget_GrowsWithKillsAndClearedWaves_AndEveryLineContinuesTheBalance()
        {
            var config = Config(4, OneWave(EnemyCodes.MechInsect, 5, 0.5f));
            config.zone.waves.Add(new WaveDef { groups = new List<WaveGroupDef> {
                new WaveGroupDef { enemyCode = EnemyCodes.MechInsect, count = 3, interval = 0.5f } } });
            CoopSim sim = Started(2, config);
            // 두 사람이 길목에 서서 직접 쏜다.
            sim.Move("p1", 3f, 30f);
            sim.Move("p2", -3f, 30f);
            sim.Run(60f);
            Assert.AreEqual(CoopPhase.Won, sim.Phase);
            Assert.AreEqual(8, sim.TotalKills);
            Assert.AreEqual(8, sim.Players.Sum(p => p.kills));
            // 시작 300 + 처치 8 × 3 + 웨이브 하나 75 (마지막 웨이브는 지급이 없다).
            Assert.AreEqual(300 + 24 + 75, sim.Budget.Balance);
            Assert.AreEqual(24, sim.Budget.Lines.Where(l => l.kind == CoopBudgetKinds.GrantKill).Sum(l => l.delta));
            Assert.AreEqual(1, sim.Budget.Lines.Count(l => l.kind == CoopBudgetKinds.GrantWave));
            Assert.IsNull(sim.Budget.FindInconsistency(Pids(sim)));
            for (int i = 1; i < sim.Budget.Lines.Count; i++)
            {
                Assert.AreEqual(sim.Budget.Lines[i - 1].balanceAfter + sim.Budget.Lines[i].delta, sim.Budget.Lines[i].balanceAfter);
                Assert.GreaterOrEqual(sim.Budget.Lines[i].atMs, sim.Budget.Lines[i - 1].atMs);
            }
        }

        [Test]
        public void LedgerCheck_FindsTamperedLines_LikeTheServerDoes()
        {
            BudgetLine L(int seq, string kind, int delta, int after, string pid = null, int slot = -1) =>
                new BudgetLine { seq = seq, kind = kind, delta = delta, balanceAfter = after, pid = pid, slot = slot };
            var members = new[] { "p1", "p2" };
            string Check(params BudgetLine[] lines) => SharedBudget.FindInconsistency(lines, members);

            Assert.IsNull(Check(L(1, "grant_start", 400, 400), L(2, "build", -100, 300, "p1", 3), L(3, "upgrade", -60, 240, "p2", 3),
                L(4, "lost", 0, 240, null, 3), L(5, "build", -100, 140, "p2", 3)));
            Assert.AreEqual("budget_sequence", Check(L(1, "grant_start", 400, 400), L(3, "build", -100, 300, "p1", 3)));
            Assert.AreEqual("budget_kind", Check(L(1, "gift", 400, 400)));
            Assert.AreEqual("budget_sign", Check(L(1, "grant_start", -1, -1)));
            Assert.AreEqual("budget_sign", Check(L(1, "grant_start", 400, 400), L(2, "build", 100, 500, "p1", 3)));
            Assert.AreEqual("budget_sign", Check(L(1, "grant_start", 400, 400), L(2, "lost", 5, 405, null, 3)));
            Assert.AreEqual("budget_negative", Check(L(1, "grant_start", 50, 50), L(2, "build", -100, -50, "p1", 3)));
            Assert.AreEqual("budget_balance", Check(L(1, "grant_start", 400, 400), L(2, "build", -100, 350, "p1", 3)));
            Assert.AreEqual("budget_spender", Check(L(1, "grant_start", 400, 400), L(2, "build", -100, 300, null, 3)));
            Assert.AreEqual("budget_spender", Check(L(1, "grant_start", 400, 400), L(2, "build", -100, 300, "stranger", 3)));
            Assert.AreEqual("budget_duplicate_slot", Check(L(1, "grant_start", 400, 400), L(2, "build", -100, 300, "p1", 3),
                L(3, "build", -100, 200, "p2", 3)));
            Assert.AreEqual("budget_upgrade_empty_slot", Check(L(1, "grant_start", 400, 400), L(2, "upgrade", -60, 340, "p1", 3)));
        }

        // ── 역할 ────────────────────────────────────────────

        [Test]
        public void Roles_AreOptional_GiveSmallBonuses_AndCanBeChangedDuringTheBattle()
        {
            var config = Config(4);
            CoopSim sim = new CoopSim(config);
            sim.Join("p1", "지휘", CoopRoleCodes.Commander, 1, true, Buildable(), false);
            sim.Join("p2", "설치", CoopRoleCodes.Builder, 1, false, Buildable(), false);
            sim.Join("p3", "지원", CoopRoleCodes.Support, 3, false, Buildable(), false);
            sim.Join("p4", "자유", null, 3, false, Buildable(), false);
            Assert.IsNull(sim.FindPlayer("p4").role, "역할은 고르지 않아도 된다");
            // 지휘관은 한 판에 한 명.
            Assert.AreEqual(CoopRejects.RoleTaken, sim.SetRole("p4", CoopRoleCodes.Commander).reject);
            Assert.AreEqual(CoopRejects.BadRole, sim.SetRole("p4", "king").reject);
            Assert.IsTrue(sim.Start("p1").Ok);

            // 설치 담당은 10% 싸게 세운다.
            Assert.IsTrue(sim.Build("p2", "b1", 6, TowerCodes.MachineGun).Ok);
            Assert.AreEqual(500 - 90, sim.Budget.Balance);
            Assert.IsTrue(sim.Build("p4", "b2", 7, TowerCodes.MachineGun).Ok);
            Assert.AreEqual(500 - 90 - 100, sim.Budget.Balance);
            Assert.IsTrue(sim.Upgrade("p2", "u1", 7).Ok, "남이 세운 타워도 강화할 수 있다");
            Assert.AreEqual(500 - 90 - 100 - 54, sim.Budget.Balance);

            // 지원 담당은 50% 더 고친다. 직접 전투 담당은 20% 더 세게 쏜다 (레벨 3: 12 × 110% = 13, 전투 담당 16).
            Assert.AreEqual(45, sim.RepairAmount(sim.FindPlayer("p3")));
            Assert.AreEqual(30, sim.RepairAmount(sim.FindPlayer("p4")));
            Assert.AreEqual(13, sim.PlayerDamage(sim.FindPlayer("p4")));
            Assert.AreEqual(12, sim.PlayerDamage(sim.FindPlayer("p1")));
            Assert.IsTrue(sim.SetRole("p4", CoopRoleCodes.Fighter).Ok, "전투 중에도 바꿀 수 있다");
            Assert.AreEqual(16, sim.PlayerDamage(sim.FindPlayer("p4")));
            // 지휘관이 내려놓으면 다른 사람이 맡을 수 있다.
            Assert.IsTrue(sim.SetRole("p1", null).Ok);
            Assert.IsTrue(sim.SetRole("p4", CoopRoleCodes.Commander).Ok);
            Assert.AreEqual(2, sim.DrainEvents().Count(e => e.type == CoopEventTypes.RoleChanged && e.pid == "p4"));
            Assert.AreEqual(CoopRoleCodes.Commander, sim.BuildReport().members.Single(m => m.pid == "p4").role);
        }

        // ── 이동, 직접 공격, 수리 ───────────────────────────

        [Test]
        public void Movement_IsDecidedByTheServer_NoTeleporting()
        {
            CoopSim sim = Started(2);
            CoopVec start = sim.FindPlayer("p1").pos;
            Assert.IsTrue(sim.Move("p1", 0f, 40f).Ok);
            sim.Step();
            Assert.AreEqual(CoopSim.PlayerSpeed * CoopSim.TickSeconds, CoopVec.Distance(start, sim.FindPlayer("p1").pos), 0.001f,
                "한 틱에 0.6 m 만 움직인다");
            sim.Run(3f);
            Assert.Less(CoopVec.Distance(start, sim.FindPlayer("p1").pos), 6f * 3.2f);
            // 지도 밖을 가리켜도 지도 안까지만 간다.
            sim.Move("p2", 9999f, -9999f);
            sim.Run(30f);
            Assert.AreEqual(sim.Map.maxX, sim.FindPlayer("p2").pos.x, 0.001f);
            Assert.AreEqual(sim.Map.minZ, sim.FindPlayer("p2").pos.z, 0.001f);
            Assert.AreEqual(CoopRejects.NotMember, sim.Move("nobody", 0f, 0f).reject);
        }

        [Test]
        public void Repair_NeedsToBeNearADamagedTower_AndCostsSharedBudget()
        {
            CoopSim sim = Started(2);
            Assert.IsTrue(sim.Build("p1", "b1", 15, TowerCodes.MachineGun).Ok);         // (-4, -13): 시작 자리 가까이
            Assert.IsTrue(sim.Build("p1", "b2", 0, TowerCodes.MachineGun).Ok);          // (4, 32): 먼 곳
            Assert.AreEqual(CoopRejects.NotDamaged, sim.Repair("p1", "r1", 15).reject);
            Assert.AreEqual(CoopRejects.SlotEmpty, sim.Repair("p1", "r2", 5).reject);
            sim.TowerAt(15).hp = 50;
            sim.TowerAt(0).hp = 50;
            Assert.AreEqual(CoopRejects.TooFar, sim.Repair("p1", "r3", 0).reject);
            Assert.AreEqual(100, sim.Budget.Balance);
            Assert.IsTrue(sim.Repair("p1", "r4", 15).Ok);
            Assert.AreEqual(80, sim.TowerAt(15).hp);
            Assert.AreEqual(90, sim.Budget.Balance);
            Assert.IsTrue(sim.Repair("p1", "r5", 15).Ok);
            Assert.IsTrue(sim.Repair("p1", "r6", 15).Ok);
            Assert.AreEqual(120, sim.TowerAt(15).hp, "최대 내구도를 넘지 않는다");
            Assert.AreEqual(3, sim.FindPlayer("p1").repairs);
            Assert.AreEqual(3, sim.Budget.Lines.Count(l => l.kind == CoopBudgetKinds.Repair));
            Assert.IsNull(sim.Budget.FindInconsistency(Pids(sim)));
        }

        [Test]
        public void EnemyThatAttacksStructures_DestroysATower_AndTheSlotCanBeRebuilt()
        {
            var config = Config(4, OneWave(EnemyCodes.ConstructionMecha, 1));
            config.zone.startEnergy = 400;
            CoopSim sim = Started(2, config);
            Assert.IsTrue(sim.Build("p1", "b1", 0, TowerCodes.MachineGun).Ok);          // 들머리 (4, 32)
            sim.Run(40f);
            Assert.IsNull(sim.TowerAt(0), "건설 메카가 길 가까이의 타워를 부순다");
            Assert.IsTrue(sim.DrainEvents().Any(e => e.type == CoopEventTypes.TowerLost && e.slot == 0));
            BudgetLine lost = sim.Budget.Lines.Single(l => l.kind == CoopBudgetKinds.Lost);
            Assert.AreEqual(0, lost.delta);
            Assert.AreEqual(0, lost.slot);
            // 빈 자리에는 다시 세울 수 있고, 장부는 여전히 앞뒤가 맞는다.
            Assert.IsTrue(sim.Build("p2", "b2", 0, TowerCodes.MachineGun).Ok);
            Assert.IsNull(sim.Budget.FindInconsistency(Pids(sim)));
            Assert.AreEqual(2, sim.Budget.Lines.Count(l => l.kind == CoopBudgetKinds.Build && l.slot == 0));
        }

        [Test]
        public void SlowAndEmpTowers_HoldEnemiesBack()
        {
            float Reached(string tower)
            {
                var config = Config(4, OneWave(EnemyCodes.MalfunctionRobot, 1));
                config.zone.startEnergy = 400;
                CoopSim sim = Started(2, config);
                // 두 사람은 멀리 있어 쏘지 않는다.
                if (tower != null) Assert.IsTrue(sim.Build("p1", "b1", 0, tower).Ok);
                sim.Run(CoopSim.StartCountdownSeconds + 6f);
                return sim.Enemies[0].distance;
            }
            float free = Reached(null);
            Assert.AreEqual(2.8f * 6f, free, 0.4f);
            Assert.Less(Reached(TowerCodes.Slow), free - 2f, "감속 장치 옆에서는 느리다");
            Assert.Less(Reached(TowerCodes.Emp), free - 2f, "EMP 타워 옆에서는 멈춘다");
        }

        // ── 중도 이탈 ───────────────────────────────────────

        [Test]
        public void BattleGoesOn_WhenAPlayerLeaves_AndTheyCanComeBack()
        {
            var config = Config(4, OneWave(EnemyCodes.MalfunctionRobot, 4, 2f));
            CoopSim sim = Started(3, config);
            sim.Move("p1", 3f, 30f);
            sim.Move("p2", -3f, 30f);
            sim.Run(8f);
            sim.Disconnect("p2");
            sim.Disconnect("p3");
            Assert.AreEqual(1, sim.ConnectedCount);
            sim.Run(20f);
            Assert.IsTrue(sim.IsRunning || sim.Phase == CoopPhase.Won, "한 명이 남아도 전투는 이어진다");
            // 나간 사람의 접속 시간은 멈추고, 남은 사람의 것만 는다.
            Assert.AreEqual(80, sim.FindPlayer("p2").connectedTicks);
            // 나간 사람이 세운 타워는 남아 있고, 다시 들어오면 기록이 이어진다.
            int killsBefore = sim.FindPlayer("p2").kills;
            Assert.IsTrue(sim.Join("p2", "시민2", null, 1, false, Buildable(), false).Ok);
            Assert.AreEqual(3, sim.Players.Count, "다시 들어와도 사람이 늘지 않는다");
            Assert.AreEqual(killsBefore, sim.FindPlayer("p2").kills);
            sim.Run(60f);
            Assert.AreEqual(CoopPhase.Won, sim.Phase);
            CoopReport report = sim.BuildReport();
            Assert.AreEqual("win", report.result);
            Assert.AreEqual(3, report.members.Count, "나간 사람도 기록에 있다 (보상은 서버가 참여 시간으로 정한다)");
            Assert.Less(report.members.Single(m => m.pid == "p3").connectedSeconds, report.durationSeconds * 30 / 100 + 6);
        }

        [Test]
        public void EmptyRoom_IsClosedAfterAMinute_AsALoss()
        {
            CoopSim sim = Started(2);
            sim.Disconnect("p1");
            sim.Disconnect("p2");
            sim.Run(CoopSim.AbandonSeconds - 1f);
            Assert.IsTrue(sim.IsRunning);
            sim.Run(2f);
            Assert.AreEqual(CoopPhase.Lost, sim.Phase);
            Assert.AreEqual(CoopCloseReasons.AllLeft, sim.CloseReason);
            CoopReport report = sim.BuildReport();
            Assert.AreEqual("lose", report.result);
            Assert.LessOrEqual(report.wavesCleared, 1, "아무도 없어도 서버의 전투는 흘러간다 (적이 관문까지 간다)");
            Assert.Less(report.gateHpLeft, 20);
            Assert.AreEqual(0, report.members.Sum(m => m.connectedSeconds));
            // 끝난 판에서는 아무것도 되지 않는다.
            Assert.AreEqual(CoopRejects.NotRunning, sim.Build("p1", "late", 0, TowerCodes.MachineGun).reject);
            Assert.AreEqual(CoopRejects.NotRunning, sim.Join("p9", "늦은시민", null, 1, false, Buildable(), false).reject);
        }

        [Test]
        public void LateJoin_FollowsTheSetting_AndTheRoomHasACapacity()
        {
            CoopSim sim = Started(2, Config(3));
            Assert.IsTrue(sim.Join("p3", "늦은시민", null, 1, false, Buildable(), false).Ok, "기본값은 전투 중 참가 허용");
            Assert.AreEqual(CoopRejects.SessionFull, sim.Join("p4", "넘친시민", null, 1, false, Buildable(), false).reject);
            Assert.AreEqual(2, sim.PlayersAtStart, "늦게 들어와도 적의 체력과 예산의 배율은 시작할 때의 인원 그대로다");

            var closed = Config(4);
            closed.rules.allowLateJoin = 0;
            CoopSim strict = Started(2, closed);
            Assert.AreEqual(CoopRejects.LateJoinClosed, strict.Join("p3", "늦은시민", null, 1, false, Buildable(), false).reject);
            strict.Disconnect("p2");
            Assert.IsTrue(strict.Join("p2", "시민2", null, 1, false, Buildable(), false).Ok, "원래 있던 사람은 다시 들어올 수 있다");
        }

        // ── 한 판 전체 ──────────────────────────────────────

        /// <summary>간단한 팀: 길 한가운데에 모여 쏘고, 예산이 생기는 대로 타워를 세우고 강화한다.</summary>
        private static void PlayAsTeam(CoopSim sim, float seconds)
        {
            int[] slots = { 8, 9, 6, 10, 7, 11, 5, 13, 3, 12, 14, 15, 2, 4, 0, 1 };
            var spots = new[] { new CoopVec(-3f, 12f), new CoopVec(3f, 12f), new CoopVec(-3f, 4f), new CoopVec(3f, 4f) };
            for (int i = 0; i < sim.Players.Count; i++) sim.Move(sim.Players[i].pid, spots[i % spots.Length].x, spots[i % spots.Length].z);
            int request = 0;
            int steps = (int)Math.Round(seconds / CoopSim.TickSeconds);
            for (int step = 0; step < steps && !sim.IsFinished; step++)
            {
                if (step % 10 == 0)
                {
                    string pid = sim.Players[step / 10 % sim.Players.Count].pid;
                    int empty = Array.FindIndex(slots, s => sim.TowerAt(s) == null);
                    if (empty >= 0 && sim.Budget.Balance >= 100) sim.Build(pid, "t" + request++, slots[empty], TowerCodes.MachineGun);
                    else if (sim.Budget.Balance >= 60)
                    {
                        var tower = sim.Towers.FirstOrDefault(t => t.level < t.def.maxUpgradeLevel);
                        if (tower != null) sim.Upgrade(pid, "t" + request++, tower.slot);
                    }
                    foreach (var tower in sim.Towers.Where(t => t.hp < t.maxHp).ToList())
                    {
                        var near = sim.Players.FirstOrDefault(p => CoopVec.Distance(p.pos, tower.pos) <= CoopSim.RepairRangeM);
                        if (near != null) sim.Repair(near.pid, "t" + request++, tower.slot);
                    }
                }
                sim.Step();
            }
        }

        [Test]
        public void FullBattle_FourPlayersWorkingTogether_Win_AndTheReportPassesTheServerChecks()
        {
            CoopSim sim = Started(4);
            PlayAsTeam(sim, 900f);
            Assert.AreEqual(CoopPhase.Won, sim.Phase, "웨이브 " + sim.Wave + ", 관문 " + sim.GateHp);
            CoopReport report = sim.BuildReport();
            Assert.AreEqual("win", report.result);
            Assert.AreEqual(CoopCloseReasons.Completed, report.closeReason);
            Assert.AreEqual(5, report.wavesCleared);
            Assert.GreaterOrEqual(report.gateHpLeft, 1);
            Assert.LessOrEqual(report.gateHpLeft, 20);
            // 서버는 웨이브마다 최소 10초가 걸렸는지 본다.
            Assert.GreaterOrEqual(report.durationSeconds, 5 * 10);
            Assert.AreEqual(4, report.members.Count);
            Assert.IsTrue(report.members.All(m => m.connectedSeconds == report.durationSeconds));
            Assert.AreEqual(sim.TotalKills, report.members.Sum(m => m.kills));
            Assert.AreEqual(_defs.FindStage(StageCodes.Outskirts1).TotalEnemies, sim.TotalKills + (20 - report.gateHpLeft),
                "나온 적은 모두 쓰러졌거나 관문에 닿았다");
            Assert.IsNull(SharedBudget.FindInconsistency(report.budget, report.members.Select(m => m.pid).ToArray()));
            Assert.Less(report.budget.Count, 6000, "서버가 받는 장부의 줄 수 한도");
            Assert.AreEqual(sim.Budget.Granted - sim.Budget.Spent, sim.Budget.Balance);
            // 보상 금액은 보고서에 없다 (서버가 정한다).
            Assert.IsFalse(typeof(CoopReport).GetFields().Any(f => f.Name.ToLowerInvariant().Contains("gc")));
            Assert.IsFalse(typeof(CoopMemberReport).GetFields().Any(f => f.Name.ToLowerInvariant().Contains("reward")));
        }

        [Test]
        public void FullBattle_NobodyDefending_LosesWhenTheGateFalls()
        {
            CoopSim sim = Started(2);
            // 두 사람이 지도 구석에 서서 아무것도 하지 않는다.
            sim.Move("p1", -26f, -26f);
            sim.Move("p2", 26f, -26f);
            sim.Run(900f);
            Assert.AreEqual(CoopPhase.Lost, sim.Phase);
            Assert.AreEqual(CoopCloseReasons.GateDestroyed, sim.CloseReason);
            CoopReport report = sim.BuildReport();
            Assert.AreEqual("lose", report.result);
            Assert.AreEqual(0, report.gateHpLeft);
            Assert.Less(report.wavesCleared, 5);
            Assert.IsTrue(sim.DrainEvents().Any(e => e.type == CoopEventTypes.GateHit));
        }

        private static string Fingerprint(CoopSim sim)
        {
            var text = new StringBuilder();
            text.Append(sim.Phase).Append('|').Append(sim.Tick).Append('|').Append(sim.Wave).Append('|').Append(sim.GateHp).Append('|')
                .Append(sim.Budget.Balance).Append('|').Append(sim.TotalKills);
            foreach (var enemy in sim.Enemies) text.Append('|').Append(enemy.id).Append(':').Append(enemy.hp).Append(':').Append(enemy.distance.ToString("R"));
            foreach (var tower in sim.Towers) text.Append('|').Append(tower.slot).Append(':').Append(tower.level).Append(':').Append(tower.hp);
            foreach (var player in sim.Players) text.Append('|').Append(player.pos.x.ToString("R")).Append(',').Append(player.pos.z.ToString("R"))
                .Append(':').Append(player.kills).Append(':').Append(player.damageDealt);
            foreach (var line in sim.Budget.Lines) text.Append('|').Append(line.seq).Append(line.kind).Append(line.delta).Append('@').Append(line.atMs);
            return text.ToString();
        }

        [Test]
        public void SameCommands_GiveTheSameBattle()
        {
            CoopSim a = Started(3), b = Started(3);
            PlayAsTeam(a, 120f);
            PlayAsTeam(b, 120f);
            Assert.AreEqual(Fingerprint(a), Fingerprint(b));
            Assert.Greater(a.TotalKills, 0);
        }

        [Test]
        public void EightPlayers_FitInOneRoom()
        {
            var config = Config(8);
            CoopSim sim = Started(8, config);
            Assert.AreEqual(8, sim.PlayersAtStart);
            Assert.AreEqual(200 * 450 / 100, sim.Budget.Balance);
            // 시작 자리가 겹치지 않는다.
            Assert.AreEqual(8, sim.Players.Select(p => p.pos.x + "," + p.pos.z).Distinct().Count());
            Assert.AreEqual(CoopRejects.SessionFull, sim.Join("p9", "넘친시민", null, 1, false, Buildable(), false).reject);
            PlayAsTeam(sim, 60f);
            Assert.IsTrue(sim.IsRunning);
            Assert.IsNull(sim.Budget.FindInconsistency(Pids(sim)));
        }

        [Test]
        public void NpcHelpers_AreMarked_AndLeftOutOfTheReport()
        {
            CoopSim sim = new CoopSim(Config(4));
            sim.Join("p1", "시민1", null, 1, true, Buildable(), false);
            sim.Join("npc_1", "[NPC] 방위대원", CoopRoleCodes.Support, 1, false, Buildable(), false, npc: true);
            Assert.IsTrue(sim.FindPlayer("npc_1").npc);
            Assert.IsTrue(sim.FindPlayer("npc_1").name.StartsWith("[NPC]"));
            Assert.IsTrue(sim.Start("p1").Ok);
            sim.Run(10f);
            CoopReport report = sim.BuildReport();
            Assert.AreEqual(1, report.members.Count);
            Assert.AreEqual("p1", report.members[0].pid);
        }
    }

    /// <summary>v9.0 전술 핑과 미니맵의 규칙 (docs/TACTICAL_MINIMAP.md).</summary>
    public sealed class TacticalMinimapTests
    {
        private static PingBoard Board(PingRules rules = null) => new PingBoard(rules ?? new PingRules(), CoopMap.ForZone(StageCodes.Outskirts1));

        private static string Send(PingBoard board, string pid, float now, string kind = PingKinds.FocusFire) =>
            board.Send(pid, kind, new CoopVec(1f, 2f), now, out _);

        [Test]
        public void SixKindsOfPings_WithKoreanNames()
        {
            Assert.AreEqual(6, PingKinds.All.Length);
            CollectionAssert.AreEqual(new[] { "공격 집중", "방어 요청", "타워 설치 제안", "수리 요청", "위험 경고", "집결 위치" },
                PingKinds.All.Select(PingKinds.DisplayName).ToArray());
            Assert.IsFalse(PingKinds.IsKnown("insult"));
        }

        [Test]
        public void Pings_AreRateLimited_ByATokenBucket()
        {
            PingBoard board = Board();
            // 연달아 세 번까지.
            Assert.IsNull(Send(board, "p1", 0f));
            Assert.IsNull(Send(board, "p1", 0.1f));
            Assert.IsNull(Send(board, "p1", 0.2f));
            Assert.AreEqual(CoopRejects.PingRate, Send(board, "p1", 0.3f));
            // 4초마다 하나씩 다시 찬다.
            Assert.AreEqual(CoopRejects.PingRate, Send(board, "p1", 3.9f));
            Assert.IsNull(Send(board, "p1", 4.3f));
            Assert.AreEqual(CoopRejects.PingRate, Send(board, "p1", 4.4f));
            // 다른 사람의 제한은 따로다.
            Assert.IsNull(Send(board, "p2", 4.4f));
            // 오래 쉬어도 세 번을 넘게 모이지 않는다.
            for (int i = 0; i < 3; i++) Assert.IsNull(Send(board, "p1", 100f + i * 0.1f));
            Assert.AreEqual(CoopRejects.PingRate, Send(board, "p1", 100.4f));
            // 거절된 핑도 기록에 남는다.
            Assert.AreEqual(4, board.RejectedOf("p1"));
            Assert.AreEqual(11, board.SentBy("p1"));
            Assert.AreEqual(0, board.RejectedOf("p2"));
        }

        [Test]
        public void RepeatedAbuse_MutesTheSenderForAWhile()
        {
            PingBoard board = Board();
            for (int i = 0; i < 3; i++) Assert.IsNull(Send(board, "p1", 0f));
            // 제한을 다섯 번 어기면 30초 동안 막힌다.
            for (int i = 0; i < 5; i++) Assert.AreEqual(CoopRejects.PingRate, Send(board, "p1", 0.1f * (i + 1)));
            Assert.IsTrue(board.IsMuted("p1", 1f));
            Assert.AreEqual(CoopRejects.PingMuted, Send(board, "p1", 20f), "토큰이 다시 찼어도 막혀 있다");
            Assert.AreEqual(CoopRejects.PingMuted, Send(board, "p1", 30.4f));
            Assert.IsFalse(board.IsMuted("p1", 30.6f));
            Assert.IsNull(Send(board, "p1", 30.6f));
            Assert.IsFalse(board.IsMuted("p2", 1f));
        }

        [Test]
        public void BlockedAccounts_DisabledPings_AndUnknownKinds_AreRejected()
        {
            PingBoard board = Board();
            board.SetBlocked("p1", true, 0f);
            Assert.AreEqual(CoopRejects.PingBlocked, Send(board, "p1", 0f));
            Assert.AreEqual(0, board.Active.Count);
            Assert.AreEqual(1, board.Records.Count, "막힌 사람이 보낸 것도 기록된다");
            Assert.IsFalse(board.Records[0].accepted);
            board.SetBlocked("p1", false, 1f);
            Assert.IsNull(Send(board, "p1", 1f));

            Assert.AreEqual(CoopRejects.PingKind, Send(board, "p1", 2f, "insult"));
            Assert.AreEqual(2, board.Records.Count, "모르는 종류는 기록하지 않는다");
            Assert.AreEqual(CoopRejects.NotMember, Send(board, null, 2f));

            PingBoard off = Board(new PingRules { enabled = 0 });
            Assert.AreEqual(CoopRejects.PingDisabled, Send(off, "p1", 0f));
        }

        [Test]
        public void Commander_RefillsFaster()
        {
            PingBoard board = Board();
            board.SetCooldownPercent("p1", 50, 0f);
            for (int i = 0; i < 3; i++) Assert.IsNull(Send(board, "p1", 0f));
            Assert.AreEqual(CoopRejects.PingRate, Send(board, "p1", 1.9f));
            Assert.IsNull(Send(board, "p1", 2.1f), "지휘관은 2초마다 하나씩 찬다");
        }

        [Test]
        public void Pings_Expire_AreCappedPerPlayer_AndStayInsideTheMap()
        {
            var rules = new PingRules { burst = 10, maxActivePerPlayer = 3 };
            PingBoard board = Board(rules);
            for (int i = 0; i < 5; i++) Assert.IsNull(board.Send("p1", PingKinds.Rally, new CoopVec(i, 0f), i * 0.1f, out _));
            Assert.AreEqual(3, board.Active.Count, "한 사람이 띄울 수 있는 핑은 셋. 오래된 것부터 지워진다");
            Assert.AreEqual(2f, board.Active[0].pos.x, 0.001f);
            Assert.IsNull(board.Send("p2", PingKinds.Danger, new CoopVec(9999f, -9999f), 1f, out TacticalPing far));
            Assert.AreEqual(26f, far.pos.x, 0.001f);
            Assert.AreEqual(-26f, far.pos.z, 0.001f);
            Assert.AreEqual(2600, board.Records.Last().x);
            board.Expire(8.25f);
            Assert.AreEqual(3, board.Active.Count, "8초가 지난 것부터 사라진다");
            board.Expire(8.5f);
            Assert.AreEqual(1, board.Active.Count);
            board.Expire(9.1f);
            Assert.AreEqual(0, board.Active.Count);
        }

        [Test]
        public void TeammateCanHideSomeonesPings_ForThemselvesOnly()
        {
            PingBoard board = Board();
            Assert.IsNull(Send(board, "p1", 0f));
            Assert.IsNull(Send(board, "p2", 0f));
            board.Hide("p3", "p1", true);
            CollectionAssert.AreEqual(new[] { "p2" }, board.VisibleTo("p3").Select(p => p.pid).ToArray());
            Assert.AreEqual(2, board.VisibleTo("p4").Count, "다른 사람에게는 그대로 보인다");
            board.Hide("p3", "p3", true);
            Assert.IsFalse(board.IsHidden("p3", "p3"), "자기 핑은 숨길 수 없다");
            board.Hide("p3", "p1", false);
            Assert.AreEqual(2, board.VisibleTo("p3").Count);
        }

        [Test]
        public void RecordsAreCapped()
        {
            PingBoard board = Board();
            for (int i = 0; i < PingBoard.MaxRecords + 500; i++) Send(board, "p1", i * 0.001f);
            Assert.AreEqual(PingBoard.MaxRecords, board.Records.Count);
        }

        // ── 판 안에서의 핑 ──────────────────────────────────

        private static CoopSim Battle(int players = 3)
        {
            GameDefs defs = DefaultDefs.Create();
            var sim = new CoopSim(new CoopSimConfig
            {
                zone = defs.FindStage(StageCodes.Outskirts1), gateHp = 20, enemies = defs.enemies, structures = defs.towers,
                roles = CoopDefaults.Roles(), maxPlayers = 4,
            });
            for (int i = 1; i <= players; i++)
                sim.Join("p" + i, "시민" + i, i == 1 ? CoopRoleCodes.Commander : null, 1, i == 1, new[] { TowerCodes.MachineGun }, i == 3);
            sim.Start("p1");
            return sim;
        }

        [Test]
        public void PingsInABattle_AreSharedCountedAndReported()
        {
            CoopSim sim = Battle();
            sim.DrainEvents();
            Assert.IsTrue(sim.Ping("p2", PingKinds.BuildHere, -7f, 12f).Ok);
            CoopEvent shared = sim.DrainEvents().Single(e => e.type == CoopEventTypes.Ping);
            Assert.AreEqual("p2", shared.pid);
            Assert.AreEqual(PingKinds.BuildHere, shared.code);
            Assert.AreEqual(-7f, shared.x, 0.001f);
            // 관리자가 막은 계정 (입장권의 ping_blocked).
            Assert.AreEqual(CoopRejects.PingBlocked, sim.Ping("p3", PingKinds.Danger, 0f, 0f).reject);
            Assert.AreEqual(0, sim.DrainEvents().Count, "거절된 핑은 팀원에게 가지 않는다");
            Assert.AreEqual(CoopRejects.PingKind, sim.Ping("p2", "insult", 0f, 0f).reject);
            Assert.AreEqual(CoopRejects.NotMember, sim.Ping("nobody", PingKinds.Danger, 0f, 0f).reject);
            for (int i = 0; i < 4; i++) sim.Ping("p2", PingKinds.Danger, 0f, 0f);
            Assert.AreEqual(5, sim.FindPlayer("p2").pingsSent);
            Assert.AreEqual(2, sim.FindPlayer("p2").pingsRejected);
            CoopReport report = sim.BuildReport();
            Assert.AreEqual(6, report.pings.Count);
            Assert.AreEqual(3, report.pings.Count(p => !p.accepted));
            Assert.AreEqual(1, report.members.Single(m => m.pid == "p3").pingsRejected);
            // 핑은 8초 뒤에 지도에서 사라진다.
            sim.Run(8.2f);
            Assert.AreEqual(0, sim.Pings.Active.Count);
        }

        // ── 미니맵 ──────────────────────────────────────────

        [Test]
        public void CornerMinimap_ShowsWhatIsAroundMe_AndPinsImportantThingsToTheEdge()
        {
            CoopSim sim = Battle();
            sim.Build("p1", "b1", 15, TowerCodes.MachineGun);          // (-4, -13) 가까운 타워
            sim.Build("p1", "b2", 0, TowerCodes.MachineGun);           // (4, 32) 먼 타워
            sim.Move("p2", 0f, 30f);                                    // 멀리 가는 팀원
            sim.Run(CoopSim.StartCountdownSeconds + 9f);
            sim.Ping("p2", PingKinds.DefendHere, 0f, 30f);
            MinimapView view = Minimap.Build(MinimapSource.FromSim(sim, "p1"), new MinimapOptions());
            Assert.IsFalse(view.expanded);
            Assert.AreEqual(56f, view.widthM, 0.001f);

            MinimapBlip me = view.blips.Single(b => b.kind == MinimapBlipKinds.Self);
            Assert.AreEqual(0.5f, me.u, 0.001f);
            Assert.AreEqual(0.5f, me.v, 0.001f);
            Assert.AreEqual(me, view.blips.Last(), "나는 맨 위에 그린다");
            // 가까운 팀원(p3)은 보이고, 멀리 간 팀원(p2)은 접힌 미니맵에 없다.
            CollectionAssert.AreEqual(new[] { "p3" }, view.blips.Where(b => b.kind == MinimapBlipKinds.Teammate).Select(b => b.pid).ToArray());
            // 가까운 타워만.
            CollectionAssert.AreEqual(new[] { 15 }, view.blips.Where(b => b.kind == MinimapBlipKinds.Tower).Select(b => b.id).ToArray());
            // 관문과 방어벽, 주요 시설.
            MinimapBlip gate = view.blips.Single(b => b.kind == MinimapBlipKinds.Gate);
            Assert.IsFalse(gate.clamped);
            Assert.AreEqual(1f, gate.hpRatio, 0.001f);
            Assert.AreEqual(2, view.blips.Count(b => b.kind == MinimapBlipKinds.Wall));
            Assert.AreEqual(3, view.blips.Count(b => b.kind == MinimapBlipKinds.Facility));
            // 긴급 지원 요청은 범위 밖이어도 가장자리에 붙어 보인다 (북쪽이 위).
            MinimapBlip request = view.blips.Single(b => b.kind == MinimapBlipKinds.SupportRequest);
            Assert.IsTrue(request.clamped);
            Assert.Less(request.v, 0.1f);
            Assert.AreEqual("방어 요청", request.label);
            // 접힌 미니맵에는 경로와 사거리를 그리지 않는다.
            Assert.AreEqual(0, view.route.Count);
            Assert.IsTrue(view.blips.All(b => b.kind != MinimapBlipKinds.Tower || b.radius == 0f));
            Assert.IsTrue(view.blips.All(b => b.u >= 0f && b.u <= 1f && b.v >= 0f && b.v <= 1f));
        }

        [Test]
        public void ExpandedTacticalMap_ShowsEverything_WithOptionalRouteAndRanges()
        {
            CoopSim sim = Battle();
            sim.Build("p1", "b1", 15, TowerCodes.MachineGun);
            sim.Build("p1", "b2", 0, TowerCodes.MachineGun);
            sim.Move("p2", 0f, 30f);
            sim.Run(CoopSim.StartCountdownSeconds + 9f);
            MinimapSource source = MinimapSource.FromSim(sim, "p1");
            var options = new MinimapOptions { expanded = true };
            MinimapView view = Minimap.Build(source, options);
            Assert.AreEqual(70f, view.widthM, 0.001f, "지도 전체가 들어가는 정사각형");
            Assert.AreEqual(2, view.blips.Count(b => b.kind == MinimapBlipKinds.Teammate));
            Assert.AreEqual(2, view.blips.Count(b => b.kind == MinimapBlipKinds.Tower));
            Assert.AreEqual(1, view.blips.Count(b => b.kind == MinimapBlipKinds.Spawn));
            Assert.IsTrue(view.blips.All(b => !b.clamped));
            // 적의 위치와 이동 방향: 처음에는 남쪽(180도)으로 내려온다.
            var foes = view.blips.Where(b => b.kind == MinimapBlipKinds.Enemy).ToList();
            Assert.Greater(foes.Count, 0);
            Assert.IsTrue(foes.All(f => Math.Abs(f.heading - 180f) < 0.01f || Math.Abs(f.heading - 270f) < 0.01f));
            // 적 이동 경로와 타워 사거리.
            Assert.AreEqual(8, view.route.Count);
            Assert.AreEqual(0.5f, view.route[0].u, 0.001f);
            Assert.Less(view.route[0].v, view.route[7].v, "길은 위(북)에서 아래(남)로");
            Assert.AreEqual(8f / 70f, view.blips.First(b => b.kind == MinimapBlipKinds.Tower).radius, 0.001f);

            // 고르지 않으면 그리지 않는다. 서버 설정이 막아도 그리지 않는다.
            MinimapView plain = Minimap.Build(source, new MinimapOptions { expanded = true, showRoute = false, showRanges = false });
            Assert.AreEqual(0, plain.route.Count);
            Assert.IsTrue(plain.blips.All(b => b.kind != MinimapBlipKinds.Tower || b.radius == 0f));
            MinimapView locked = Minimap.Build(source, options, new PingRules { routeOverlay = 0, rangeOverlay = 0 });
            Assert.AreEqual(0, locked.route.Count);
            Assert.IsTrue(locked.blips.All(b => b.kind != MinimapBlipKinds.Tower || b.radius == 0f));

            // 지도를 눌러 핑을 찍을 때: 화면의 점이 지도 위의 곳으로 바뀐다.
            MinimapBlip tower = view.blips.First(b => b.kind == MinimapBlipKinds.Tower && b.id == 0);
            CoopVec world = Minimap.ToWorld(source, options, tower.u, tower.v);
            Assert.AreEqual(4f, world.x, 0.01f);
            Assert.AreEqual(32f, world.z, 0.01f);
            CoopVec mine = Minimap.ToWorld(source, new MinimapOptions(), 0.5f, 0.5f);
            Assert.AreEqual(0f, CoopVec.Distance(mine, sim.FindPlayer("p1").pos), 0.01f);
        }

        [Test]
        public void Minimap_HidesPingsOfMutedTeammates_AndMarksDisconnectedOnes()
        {
            CoopSim sim = Battle();
            sim.Ping("p2", PingKinds.Rally, 0f, -10f);
            sim.Pings.Hide("p1", "p2", true);
            sim.Disconnect("p3");
            var options = new MinimapOptions { expanded = true };
            Assert.AreEqual(0, Minimap.Build(MinimapSource.FromSim(sim, "p1"), options).blips.Count(b => b.kind == MinimapBlipKinds.Ping));
            MinimapView other = Minimap.Build(MinimapSource.FromSim(sim, "p3"), options);
            Assert.AreEqual(1, other.blips.Count(b => b.kind == MinimapBlipKinds.Ping));
            Assert.AreEqual("집결 위치", other.blips.Single(b => b.kind == MinimapBlipKinds.Ping).label);
            MinimapView mine = Minimap.Build(MinimapSource.FromSim(sim, "p1"), options);
            Assert.IsTrue(mine.blips.Single(b => b.pid == "p3").label.Contains("연결 끊김"));
        }

        [Test]
        public void DangerZones_AppearWhereEnemiesGather_AndNearTheGate()
        {
            GameDefs defs = DefaultDefs.Create();
            var zone = new StageDef
            {
                code = "test_zone", name = "시험 구역", baseHp = 50, startEnergy = 200, waveClearEnergy = 0, restSeconds = 2f,
                waves = new List<WaveDef> { new WaveDef { groups = new List<WaveGroupDef> {
                    new WaveGroupDef { enemyCode = EnemyCodes.MechInsect, count = 8, interval = 0.2f } } } },
            };
            var sim = new CoopSim(new CoopSimConfig { zone = zone, gateHp = 50, enemies = defs.enemies, structures = defs.towers, maxPlayers = 4 });
            sim.Join("p1", "시민1", null, 1, true, new string[0], false);
            sim.Join("p2", "시민2", null, 1, false, new string[0], false);
            sim.Move("p1", -26f, -26f);
            sim.Move("p2", 26f, -26f);
            sim.Start("p1");
            Assert.AreEqual(0, sim.DangerZones().Count);
            sim.Run(CoopSim.StartCountdownSeconds + 3f);
            // 여덟 마리가 한 구간에 몰려 있다.
            var early = sim.DangerZones();
            Assert.AreEqual(1, early.Count);
            Assert.IsFalse(early[0].nearGate);
            Assert.AreEqual(8, early[0].enemies);
            // 관문 가까이(길의 마지막 30%)까지 오면 관문 위험으로 바뀐다.
            sim.Run(15.5f);
            var late = sim.DangerZones();
            Assert.IsTrue(late.Any(z => z.nearGate));
            MinimapView view = Minimap.Build(MinimapSource.FromSim(sim, "p1"), new MinimapOptions());
            MinimapBlip danger = view.blips.First(b => b.kind == MinimapBlipKinds.Danger);
            Assert.AreEqual("관문이 위험해요", danger.label);
            Assert.Greater(danger.radius, 0f);
        }
    }

    /// <summary>v9.0 도시 콘텐츠를 안전하게 받는 규칙 (docs/CITY_MODELING_PIPELINE.md 6장).</summary>
    public sealed class CityContentTests
    {
        private const string Base = "https://cdn.cg.test/content";

        private static CityContentEntry Entry(string version = "1.0.0", string kind = CityContent.Street)
        {
            return new CityContentEntry
            {
                contentId = "c_test", regionCode = "11110", kind = kind, version = version, addressKey = "city/11110/street",
                catalogUrl = Base + "/city-11110/" + version + "/catalog.json", contentHash = new string('a', 64), sizeBytes = 3,
                minClientVersion = "9.0.0",
            };
        }

        [Test]
        public void Versions_AreComparedByNumber()
        {
            Assert.Greater(CityContent.CompareVersions("1.10.0", "1.9.9"), 0);
            Assert.Less(CityContent.CompareVersions("8.1.0", "9.0.0"), 0);
            Assert.AreEqual(0, CityContent.CompareVersions("9.0", "9.0.0"));
            Assert.Less(CityContent.CompareVersions("abc", "0.0.1"), 0, "형식이 틀리면 가장 낮은 것으로 본다");
            Assert.Less(CityContent.CompareVersions(null, "0.0.1"), 0);
        }

        [Test]
        public void Content_IsUsedOnlyFromTheKnownStore_ForANewEnoughClient()
        {
            Assert.IsTrue(CityContent.IsUsable(Entry(), "9.0.0", Base));
            Assert.IsTrue(CityContent.IsUsable(Entry(), "9.1.0", Base + "/"));
            Assert.IsFalse(CityContent.IsUsable(Entry(), "8.1.0", Base), "오래된 클라이언트는 받지 않는다");
            Assert.IsFalse(CityContent.IsUsable(Entry(), "9.0.0", "http://cdn.cg.test/content"), "https 가 아닌 저장소");
            Assert.IsFalse(CityContent.IsUsable(Entry(), "9.0.0", ""));
            Assert.IsFalse(CityContent.IsUsable(null, "9.0.0", Base));

            void Bad(Action<CityContentEntry> change, string why)
            {
                CityContentEntry entry = Entry();
                change(entry);
                Assert.IsFalse(CityContent.IsUsable(entry, "9.0.0", Base), why);
            }
            Bad(e => e.catalogUrl = "https://evil.example/content/catalog.json", "다른 곳의 주소");
            Bad(e => e.catalogUrl = Base + ".evil.example/catalog.json", "저장소 주소로 시작하는 척하는 주소");
            Bad(e => e.catalogUrl = Base + "/../secret/catalog.json", "저장소 밖으로 나가는 경로");
            Bad(e => e.catalogUrl = Base + "/a//b.json", "빈 경로 조각");
            Bad(e => e.catalogUrl = Base + "/a.json?x=1", "질의가 붙은 주소");
            Bad(e => e.catalogUrl = Base + "/%2e%2e/a.json", "인코딩된 경로");
            Bad(e => e.catalogUrl = Base + "/", "파일이 없는 주소");
            Bad(e => e.contentHash = "zz", "해시 형식");
            Bad(e => e.contentHash = new string('A', 64), "대문자 해시");
            Bad(e => e.sizeBytes = 0, "크기");
            Bad(e => e.kind = "malware", "모르는 종류");
            Bad(e => e.addressKey = "", "주소 키");
            Bad(e => e.regionCode = null, "지역");
        }

        [Test]
        public void Pick_ChoosesTheNewestUsableVersion_OrNothing()
        {
            var entries = new List<CityContentEntry>
            {
                Entry("1.0.0"), Entry("1.2.0"), Entry("1.10.0"), Entry("2.0.0", CityContent.Landmark), null,
            };
            entries.Add(Entry("9.9.9"));
            entries[5].catalogUrl = "https://evil.example/catalog.json";
            entries.Add(Entry("3.0.0"));
            entries[6].minClientVersion = "10.0.0";
            Assert.AreEqual("1.10.0", CityContent.Pick(entries, "11110", CityContent.Street, "9.0.0", Base).version);
            Assert.AreEqual("3.0.0", CityContent.Pick(entries, "11110", CityContent.Street, "10.0.0", Base).version);
            Assert.AreEqual("2.0.0", CityContent.Pick(entries, "11110", CityContent.Landmark, "9.0.0", Base).version);
            Assert.IsNull(CityContent.Pick(entries, "26110", CityContent.Street, "9.0.0", Base), "콘텐츠가 없는 도시는 기본 도시를 쓴다");
            Assert.IsNull(CityContent.Pick(entries, "11110", CityContent.Street, "8.0.0", Base));
            Assert.IsNull(CityContent.Pick(null, "11110", CityContent.Street, "9.0.0", Base));
        }

        [Test]
        public void DownloadedFile_IsUsedOnlyWhenItsHashMatches()
        {
            CityContentEntry entry = Entry();
            // "abc" 의 SHA-256.
            entry.contentHash = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
            Assert.IsTrue(CityContent.HashMatches(entry, Encoding.ASCII.GetBytes("abc")));
            Assert.IsFalse(CityContent.HashMatches(entry, Encoding.ASCII.GetBytes("abd")), "내용이 바뀐 파일");
            Assert.IsFalse(CityContent.HashMatches(entry, Encoding.ASCII.GetBytes("abcd")), "크기가 다른 파일");
            Assert.IsFalse(CityContent.HashMatches(entry, null));
            entry.contentHash = "not-a-hash";
            Assert.IsFalse(CityContent.HashMatches(entry, Encoding.ASCII.GetBytes("abc")));
        }
    }
}
