using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CityGuardians.Domain;
using NUnit.Framework;

namespace CityGuardians.Realtime.Tests
{
    /// <summary>
    /// 게임 서버(PHP)와의 약속. 두 서버의 테스트가 같은 표본(server/tests/fixtures)을 본다:
    ///   coop-config.json   PHP 가 만든다. 여기에서 읽어 방을 열 수 있어야 한다.
    ///   coop-report.json   여기에서 만든다 (실제 전투를 돌린 결과). PHP 의 테스트가 그것을 받아들이는지 본다.
    /// 표본을 새로 만들 때는 CG_UPDATE_FIXTURES=1 로 돌린다 (server/tests/CoopContractTest.php 의 설명).
    /// </summary>
    public sealed class ContractTests
    {
        private static RoomConfig Config()
        {
            JsonObject data = Fixtures.ConfigData();
            using (JsonDocument document = JsonDocument.Parse(data.ToJsonString()))
                return HttpGameApi.ReadConfig(document.RootElement.Clone(), "d_fixture0000000");
        }

        [Test]
        public void ConfigFromTheGameServer_IsReadCompletely()
        {
            RoomConfig config = Config();
            Assert.IsNotNull(config, "게임 서버가 준 설정을 읽지 못했다");
            Assert.AreEqual("lobby", config.Status);
            Assert.AreEqual("11110", config.RegionCode);
            Assert.AreEqual("종로구", config.RegionName);
            Assert.AreEqual("p_fixture_a", config.HostPid);
            Assert.AreEqual(900, config.ExpiresInSeconds);
            Assert.AreEqual(60, config.SessionMaxMinutes);

            CoopSimConfig sim = config.Sim;
            Assert.AreEqual(4, sim.maxPlayers);
            Assert.AreEqual("outskirts_1", sim.zone.code);
            Assert.AreEqual(20, sim.zone.baseHp);
            Assert.AreEqual(20, sim.gateHp);
            Assert.AreEqual(200, sim.zone.startEnergy);
            Assert.AreEqual(50, sim.zone.waveClearEnergy);
            Assert.AreEqual(10f, sim.zone.restSeconds);
            Assert.AreEqual(10f, sim.zone.minSecondsPerWave);
            Assert.AreEqual(5, sim.zone.waves.Count);
            Assert.AreEqual("malfunction_robot", sim.zone.waves[0].groups[0].enemyCode);
            Assert.AreEqual(6, sim.zone.waves[0].groups[0].count);
            Assert.AreEqual(2.5f, sim.zone.waves[0].groups[0].interval);
            Assert.AreEqual(14f, sim.zone.waves[1].groups[1].delay);
            Assert.Greater(sim.zone.TotalEnemies, 100);

            // 적: 이름이 다른 필드(max_hp, move_speed, base_damage ...)가 모두 들어온다.
            EnemyDef mecha = sim.enemies.Single(e => e.code == "construction_mecha");
            Assert.AreEqual(1500, mecha.maxHp);
            Assert.AreEqual(1.5f, mecha.moveSpeed);
            Assert.AreEqual(5, mecha.baseDamage);
            Assert.AreEqual(60, mecha.rewardEnergy);
            Assert.IsTrue(mecha.attacksStructures);
            Assert.IsTrue(mecha.staggerImmune);
            Assert.AreEqual(60, mecha.structureDamage);
            Assert.AreEqual(2.6f, mecha.attackRange);
            EnemyDef robot = sim.enemies.Single(e => e.code == "malfunction_robot");
            Assert.AreEqual(60, robot.maxHp);
            Assert.AreEqual(2.8f, robot.moveSpeed);
            Assert.AreEqual(6, robot.rewardEnergy);
            Assert.IsFalse(robot.attacksStructures);

            // 타워: 전투 중에 세울 수 있는 것만 온다.
            Assert.IsTrue(sim.structures.All(t => t.IsBuildable));
            CollectionAssert.AreEquivalent(new[] { "twr_machine_gun", "twr_blast", "twr_laser", "twr_slow", "twr_emp" },
                sim.structures.Select(t => t.code).ToArray());
            TowerDef gun = sim.structures.Single(t => t.code == "twr_machine_gun");
            Assert.AreEqual(100, gun.energyCost);
            Assert.AreEqual(6, gun.damage);
            Assert.AreEqual(5f, gun.shotsPerSecond);
            Assert.AreEqual(8f, gun.range);
            Assert.AreEqual(120, gun.maxHp);
            Assert.AreEqual(60, gun.upgradeEnergyCost);
            Assert.AreEqual(3, gun.maxUpgradeLevel);
            Assert.AreEqual(StructureKinds.Gun, gun.kind);
            TowerDef slow = sim.structures.Single(t => t.code == "twr_slow");
            Assert.AreEqual(StructureKinds.Slow, slow.kind);
            Assert.AreEqual(40, slow.effectValue);
            Assert.AreEqual(2, slow.maxPerBattle);
            TowerDef emp = sim.structures.Single(t => t.code == "twr_emp");
            Assert.AreEqual(1.5f, emp.effectSeconds);
            Assert.AreEqual(2.5f, sim.structures.Single(t => t.code == "twr_blast").splashRadius);

            // 역할과 규칙.
            CollectionAssert.AreEqual(new[] { "commander", "builder", "support", "fighter" }, sim.roles.Select(r => r.code).ToArray());
            Assert.AreEqual(1, sim.roles[0].maxPerSession);
            Assert.AreEqual(50, sim.roles[0].pingCooldownPercent);
            Assert.AreEqual(10, sim.roles[1].buildDiscountPercent);
            Assert.AreEqual(50, sim.roles[2].repairBonusPercent);
            Assert.AreEqual(20, sim.roles[3].attackBonusPercent);
            Assert.AreEqual(60, sim.rules.hpScalePercentPerPlayer);
            Assert.AreEqual(50, sim.rules.energyScalePercentPerPlayer);
            Assert.AreEqual(2, sim.rules.minPlayersToStart);
            Assert.AreEqual(30, sim.rules.playerShotsPer10s, "숫자가 든 이름(player_shots_per10s)");
            Assert.AreEqual(14, sim.rules.playerRangeM);
            Assert.AreEqual(12, sim.rules.playerDamage);
            Assert.AreEqual(30, sim.rules.repairHp);
            Assert.AreEqual(10, sim.rules.repairEnergy);
            Assert.AreEqual(1, sim.rules.allowLateJoin);
            Assert.AreEqual(3, sim.ping.burst);
            Assert.AreEqual(4, sim.ping.refillSeconds);
            Assert.AreEqual(8, sim.ping.lifetimeSeconds);
            Assert.AreEqual(5, sim.ping.autoMuteStrikes);
            Assert.AreEqual(1, sim.ping.routeOverlay);

            // 게임 서버가 아는 핑의 종류와 규칙 코드가 아는 종류가 같다.
            CollectionAssert.AreEqual(PingKinds.All, Fixtures.ConfigData()["ping_kinds"].AsArray().Select(k => k.GetValue<string>()).ToArray());
        }

        [Test]
        public void BrokenConfig_IsRefused()
        {
            RoomConfig Read(Action<JsonObject> change, string sessionId = "d_fixture0000000")
            {
                JsonObject data = Fixtures.ConfigData();
                change(data);
                using (JsonDocument document = JsonDocument.Parse(data.ToJsonString()))
                    return HttpGameApi.ReadConfig(document.RootElement.Clone(), sessionId);
            }
            Assert.IsNotNull(Read(d => { }));
            Assert.IsNull(Read(d => { }, "d_another0000000"), "다른 판의 설정");
            Assert.IsNull(Read(d => d.Remove("zone")));
            Assert.IsNull(Read(d => d["zone"]["waves"] = new JsonArray()));
            Assert.IsNull(Read(d => d["zone"]["waves"][0]["groups"][0]["count"] = 1000000), "터무니없는 수");
            Assert.IsNull(Read(d => d["zone"]["waves"][0]["groups"][0]["interval"] = -1));
            Assert.IsNull(Read(d => d["enemies"] = new JsonArray()));
            Assert.IsNull(Read(d => d["enemies"][0]["move_speed"] = 0));
            Assert.IsNull(Read(d => d["enemies"][0]["max_hp"] = "많이"));
            Assert.IsNull(Read(d => d["structures"][0]["max_hp"] = 0));
            Assert.IsNull(Read(d => d["zone"] = "outskirts_1"));
            // 정원은 범위 안으로 당긴다.
            Assert.AreEqual(16, Read(d => d["session"]["max_players"] = 500).Sim.maxPlayers);
        }

        // ── 실시간 서버가 보내는 결과 ───────────────────────

        /// <summary>두 사람이 한 판을 끝까지 한다: 길목에 서서 쏘고, 예산이 생기면 타워를 세우고 강화하고 고치고, 가끔 핑을 보낸다.</summary>
        private static CoopSim PlayFixtureBattle()
        {
            RoomConfig config = Config();
            var sim = new CoopSim(config.Sim);
            string[] all = config.Sim.structures.Select(t => t.code).ToArray();
            Assert.IsTrue(sim.Join("p_fixture_a", "표본대원A", null, 1, true, all, false).Ok);
            Assert.IsTrue(sim.Join("p_fixture_b", "표본대원B", "builder", 1, false, all, false).Ok);
            sim.Ping("p_fixture_a", PingKinds.Rally, -3f, 6f);         // 대기실에서 보낸 핑
            Assert.IsTrue(sim.Start("p_fixture_a").Ok);
            sim.Move("p_fixture_a", -3f, 12f);
            sim.Move("p_fixture_b", 3f, 4f);
            int[] slots = { 8, 9, 6, 11, 7, 10, 5, 13, 3, 12, 14, 15, 2, 4, 0, 1 };
            int request = 0;
            for (int step = 0; step < 20000 && !sim.IsFinished; step++)
            {
                if (step % 10 == 0)
                {
                    // 설치 담당(B)이 세우고 강화한다 (10% 싸다). A 는 고친다.
                    int empty = Array.FindIndex(slots, s => sim.TowerAt(s) == null);
                    CoopTowerState weakest = sim.Towers.Where(t => t.level < t.def.maxUpgradeLevel).OrderBy(t => t.level).FirstOrDefault();
                    if (empty >= 0 && sim.Budget.Balance >= 90 && sim.Towers.Count() < 8) sim.Build("p_fixture_b", "b" + request++, slots[empty], "twr_machine_gun");
                    else if (weakest != null && sim.Budget.Balance >= 54) sim.Upgrade("p_fixture_b", "u" + request++, weakest.slot);
                    else if (empty >= 0 && sim.Budget.Balance >= 90) sim.Build("p_fixture_b", "b" + request++, slots[empty], "twr_machine_gun");
                    foreach (CoopTowerState tower in sim.Towers.Where(t => t.hp < t.maxHp).ToList())
                    {
                        CoopPlayerState near = sim.Players.FirstOrDefault(p => CoopVec.Distance(p.pos, tower.pos) <= CoopSim.RepairRangeM);
                        if (near != null) sim.Repair(near.pid, "r" + request++, tower.slot);
                    }
                }
                // 30초마다 핑 다섯 개를 몰아 보낸다: 셋은 되고 둘은 빈도 제한에 걸린다.
                if (step % 300 == 150)
                    for (int i = 0; i < 5; i++) sim.Ping("p_fixture_b", i % 2 == 0 ? PingKinds.DefendHere : PingKinds.RepairHere, 14f, 0.5f * i);
                if (step % 300 == 0) sim.Ping("p_fixture_a", PingKinds.FocusFire, -14f, 16f);
                sim.Step();
            }
            return sim;
        }

        [Test]
        public void ReportOfARealBattle_MatchesTheFixtureTheGameServerIsTestedWith()
        {
            CoopSim sim = PlayFixtureBattle();
            Assert.IsTrue(sim.IsFinished);
            CoopReport report = sim.BuildReport();
            Assert.AreEqual("win", report.result, "표본은 이긴 판이다 (웨이브 " + sim.Wave + ", 관문 " + sim.GateHp + ")");
            Assert.AreEqual(5, report.wavesCleared);
            // 게임 서버가 보는 것들을 여기에서도 한 번 본다.
            Assert.IsNull(SharedBudget.FindInconsistency(report.budget, new[] { "p_fixture_a", "p_fixture_b" }));
            Assert.GreaterOrEqual(report.durationSeconds, 5 * 10);
            Assert.Less(report.budget.Count, 6000);
            Assert.Less(report.pings.Count, 3000);
            Assert.Greater(report.pings.Count(p => !p.accepted), 0, "거절된 핑도 기록에 들어 있다");
            Assert.Greater(report.members.Sum(m => m.towersBuilt), 0);
            Assert.Greater(report.members.Sum(m => m.towersUpgraded), 0);
            Assert.AreEqual("builder", report.members.Single(m => m.pid == "p_fixture_b").role);
            Assert.IsNull(report.members.Single(m => m.pid == "p_fixture_a").role);

            string json = Encoding.UTF8.GetString(Wire.Bytes(HttpGameApi.ResultBody(report))) + "\n";
            // 보내는 모양: snake_case, 보상 금액은 없다, 자리가 없는 줄은 slot 이 null 이다.
            using (JsonDocument document = JsonDocument.Parse(json))
            {
                JsonElement root = document.RootElement;
                CollectionAssert.AreEquivalent(
                    new[] { "result", "waves_cleared", "gate_hp_left", "duration_seconds", "close_reason", "members", "budget", "pings" },
                    root.EnumerateObject().Select(p => p.Name).ToArray());
                CollectionAssert.AreEquivalent(
                    new[] { "pid", "role", "connected_seconds", "kills", "towers_built", "towers_upgraded", "repairs", "damage_dealt", "pings_sent",
                        "pings_rejected" },
                    root.GetProperty("members")[0].EnumerateObject().Select(p => p.Name).ToArray());
                CollectionAssert.AreEquivalent(new[] { "seq", "pid", "kind", "delta", "balance_after", "slot", "code", "at_ms" },
                    root.GetProperty("budget")[0].EnumerateObject().Select(p => p.Name).ToArray());
                CollectionAssert.AreEquivalent(new[] { "pid", "kind", "x", "z", "at_ms", "accepted" },
                    root.GetProperty("pings")[0].EnumerateObject().Select(p => p.Name).ToArray());
                JsonElement first = root.GetProperty("budget")[0];
                Assert.AreEqual("grant_start", first.Str("kind"));
                Assert.AreEqual(JsonValueKind.Null, first.GetProperty("slot").ValueKind);
                Assert.AreEqual(JsonValueKind.Null, first.GetProperty("pid").ValueKind);
                Assert.AreEqual(300, first.Int("delta"), "두 명: 200 × 150%");
                JsonElement build = root.List("budget").First(l => l.Str("kind") == "build");
                Assert.AreEqual(-90, build.Int("delta"), "설치 담당은 10% 싸게 세운다");
                Assert.AreEqual("p_fixture_b", build.Str("pid"));
                Assert.AreEqual(8, build.Int("slot"));
            }
            Assert.IsFalse(json.Contains("reward") || json.Contains("\"gc\""), "보상 금액은 게임 서버가 정한다");

            string path = Path.Combine(Fixtures.Dir, "coop-report.json");
            if (Fixtures.Updating) File.WriteAllText(path, json, new UTF8Encoding(false));
            Assert.IsTrue(File.Exists(path), "server/tests/fixtures/coop-report.json 이 없다. CG_UPDATE_FIXTURES=1 로 만든다.");
            Assert.AreEqual(File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n"), json,
                "실시간 서버가 보내는 결과가 바뀌었다. 표본을 새로 만들고 (CG_UPDATE_FIXTURES=1) 게임 서버의 CoopContractTest 를 다시 돌린다.");
        }
    }
}
