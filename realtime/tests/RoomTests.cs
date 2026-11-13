using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using NUnit.Framework;

namespace CityGuardians.Realtime.Tests
{
    /// <summary>
    /// 실시간 서버를 띄우고 실제 WebSocket 으로 여러 클라이언트를 붙여 본다 (docs/TEST_PLAN.md):
    /// 다중 플레이어 동시 타워 설치, 공동 예산 중복 차감 방지, 미니맵 실시간 위치 동기화, 중도 이탈, 결과 보고.
    /// 전투 시계는 10배로 돌린다 (테스트에서만).
    /// </summary>
    public sealed class RoomTests
    {
        /// <summary>적이 나오지 않는 구역 (첫 무리가 아주 늦게 온다). 타워와 예산만 볼 때 쓴다.</summary>
        private static void QuietZone(JsonObject data)
        {
            data["zone"]["waves"] = JsonNode.Parse("[{\"groups\":[{\"enemy_code\":\"mech_insect\",\"count\":1,\"interval\":1,\"delay\":3000}]}]");
        }

        private static string Build(string requestId, int slot, string code = "twr_machine_gun") =>
            "{\"type\":\"build\",\"request_id\":\"" + requestId + "\",\"slot\":" + slot + ",\"code\":\"" + code + "\"}";

        private static string Ping(string kind, float x, float z) =>
            "{\"type\":\"ping\",\"kind\":\"" + kind + "\",\"x\":" + x.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"z\":"
            + z.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}";

        private static Task<JsonElement> ResultAsync(TestClient client, string requestId) =>
            client.WaitAsync(m => m.Is("result") && m.Str("request_id") == requestId, "result " + requestId);

        private static Task<JsonElement> RejectAsync(TestClient client, string action) =>
            client.WaitAsync(m => m.Is("reject") && m.Str("action") == action, "reject " + action);

        private static async Task EventuallyAsync(Func<bool> condition, string what, int timeoutMs = 8000)
        {
            DateTime until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (!condition())
            {
                if (DateTime.UtcNow >= until) Assert.Fail("기다리던 일이 일어나지 않았습니다: " + what);
                await Task.Delay(10);
            }
        }

        // ── 입장 ────────────────────────────────────────────

        [Test]
        public async Task Join_NeedsAValidUnusedTicket_ForAnOpenSession()
        {
            await using Arena arena = await Arena.StartAsync(o => o.JoinTimeoutMs = 400);
            FakeSession session = arena.Game.NewSession(2);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            // 첫 메시지가 입장권이 아니면 끊는다.
            TestClient rude = await arena.ConnectAsync();
            await rude.SendAsync("{\"type\":\"move\",\"x\":1,\"z\":1}");
            Assert.AreEqual("bad_request", (await rude.WaitAsync(m => m.Is("error"), "error")).Str("reason"));
            await rude.WaitClosedAsync();
            Assert.AreEqual(WebSocketCloseStatus.PolicyViolation, rude.CloseStatus);

            Assert.AreEqual("bad_ticket", await arena.JoinErrorAsync("garbage.garbage"));
            Assert.AreEqual("bad_ticket", await arena.JoinErrorAsync(arena.Game.Ticket(session, 0, null, "another-secret-0000000000000000000000000")));
            Assert.AreEqual("bad_ticket", await arena.JoinErrorAsync(arena.Game.Ticket(session, 0, t => t["exp"] = now - 1)), "만료된 입장권");
            Assert.AreEqual(0, arena.Game.ConfigCalls, "입장권이 틀리면 게임 서버에 묻지도 않는다");
            Assert.AreEqual(0, arena.Host.Rooms.RoomCount);

            // 맞는 입장권.
            string ticket = arena.Game.Ticket(session, 0);
            TestClient a = await arena.ConnectAsync();
            await a.SendAsync("{\"type\":\"join\",\"ticket\":\"" + ticket + "\"}");
            JsonElement welcome = await a.WaitAsync(m => m.Is("welcome"), "welcome");
            Assert.AreEqual(session.Members[0], welcome.Str("you"));
            Assert.AreEqual(session.Id, welcome.GetProperty("session").Str("id"));
            Assert.AreEqual("종로구", welcome.GetProperty("session").Str("region_name"));
            Assert.AreEqual(session.HostPid, welcome.GetProperty("session").Str("host_pid"));
            Assert.AreEqual(100, welcome.Int("tick_ms"));
            Assert.AreEqual(16, welcome.GetProperty("map").List("slots").Length);
            Assert.AreEqual(8, welcome.GetProperty("map").List("path").Length);
            Assert.AreEqual(7, welcome.GetProperty("map").List("landmarks").Length);
            Assert.AreEqual(5, welcome.List("structures").Length);
            Assert.IsTrue(welcome.List("structures").All(s => s.Flag("usable")));
            Assert.AreEqual(100, welcome.List("structures").Single(s => s.Str("code") == "twr_machine_gun").Int("energy_cost"));
            Assert.AreEqual(4, welcome.List("roles").Length);
            Assert.AreEqual(6, welcome.GetProperty("ping").List("kinds").Length);
            Assert.AreEqual(5, welcome.GetProperty("zone").Int("wave_count"));
            JsonElement state = welcome.GetProperty("state");
            Assert.AreEqual("lobby", state.Str("phase"));
            Assert.AreEqual(1, state.List("players").Length);
            Assert.IsTrue(state.Player(session.Members[0]).Flag("host"));
            Assert.AreEqual(20, state.Int("gate_hp"));
            Assert.AreEqual(0, state.Int("energy"));
            // 비밀값이나 입장권이 응답에 섞여 나가지 않는다.
            Assert.IsFalse(welcome.GetRawText().Contains(FakeGame.Secret) || welcome.GetRawText().Contains(ticket.Substring(0, 40)));

            // 같은 입장권을 다시 쓸 수 없다 (가로챈 입장권으로 들어오지 못한다).
            Assert.AreEqual("bad_ticket", await arena.JoinErrorAsync(ticket));
            Assert.AreEqual(1, arena.Host.Rooms.RoomCount);
            Assert.AreEqual(1, arena.Game.ConfigCalls, "방은 한 번만 만든다");

            // 없는 판, 이미 끝난 판.
            var ghost = new FakeSession { Id = "d_ghost000000000", Data = Fixtures.ConfigData(), Members = { "p_ghost0000000000" }, HostPid = "p_ghost0000000000" };
            Assert.AreEqual("session_not_found", await arena.JoinErrorAsync(arena.Game.Ticket(ghost, 0)));
            FakeSession over = arena.Game.NewSession(2);
            over.Status = "finished";
            Assert.AreEqual("session_closed", await arena.JoinErrorAsync(arena.Game.Ticket(over, 0)));
            Assert.AreEqual(1, arena.Host.Rooms.RoomCount, "열지 못한 방은 남지 않는다");

            // 아무것도 보내지 않는 연결은 곧 끊긴다.
            TestClient silent = await arena.ConnectAsync();
            await silent.WaitClosedAsync(3000);
            Assert.AreEqual(0, arena.Game.BadSignatures, "실시간 서버의 요청은 모두 서명이 맞았다");
        }

        [Test]
        public async Task SamePlayerJoiningAgain_ReplacesTheOldConnection()
        {
            await using Arena arena = await Arena.StartAsync();
            FakeSession session = arena.Game.NewSession(2);
            TestClient first = await arena.JoinAsync(session, 0);
            TestClient other = await arena.JoinAsync(session, 1);
            TestClient second = await arena.JoinAsync(session, 0);
            await first.WaitClosedAsync();
            Assert.AreEqual("replaced", first.CloseReason);
            JsonElement state = await second.StateAsync();
            Assert.AreEqual(2, state.List("players").Length, "같은 사람이 둘이 되지 않는다");
            Assert.IsTrue(state.Player(session.Members[0]).Flag("connected"));
            // 옛 연결이 끊긴 것이 "나갔다"로 처리되지 않는다.
            await Task.Delay(100);
            Assert.IsTrue((await other.StateAsync()).Player(session.Members[0]).Flag("connected"));
            Assert.AreEqual(0, other.Received(m => m.IsEvent("player_left")).Count);
        }

        // ── 시작 ────────────────────────────────────────────

        [Test]
        public async Task Start_IsHostOnly_NeedsEnoughPlayers_AndIsRecordedOnceOnTheGameServer()
        {
            await using Arena arena = await Arena.StartAsync();
            FakeSession session = arena.Game.NewSession(3);
            TestClient host = await arena.JoinAsync(session, 0);
            await host.SendAsync("{\"type\":\"start\"}");
            Assert.AreEqual("not_enough_players", (await RejectAsync(host, "start")).Str("reason"));

            TestClient b = await arena.JoinAsync(session, 1);
            await host.EventAsync("player_joined");
            await b.SendAsync("{\"type\":\"start\"}");
            Assert.AreEqual("not_host", (await RejectAsync(b, "start")).Str("reason"));
            Assert.AreEqual(0, session.StartCalls, "안 되는 시작은 게임 서버에 가지 않는다");
            Assert.AreEqual("lobby", session.Status);

            await host.SendAsync("{\"type\":\"start\"}");
            JsonElement started = await host.EventAsync("started");
            Assert.AreEqual(session.HostPid, started.Str("pid"));
            Assert.AreEqual(2, started.Int("value"));
            await b.EventAsync("started");
            Assert.AreEqual(1, session.StartCalls);
            Assert.AreEqual("running", session.Status);
            CollectionAssert.AreEquivalent(new[] { session.Members[0], session.Members[1] }, session.StartedWith);

            await host.SendAsync("{\"type\":\"start\"}");
            Assert.AreEqual("not_lobby", (await RejectAsync(host, "start")).Str("reason"));
            Assert.AreEqual(1, session.StartCalls);

            JsonElement state = await b.StateAsync();
            Assert.AreEqual("rest", state.Str("phase"));
            Assert.AreEqual(300, state.Int("energy"), "두 명: 200 × 150%");
            await b.EventAsync("wave_started");
            state = await b.StateAsync(s => s.List("enemies").Length > 0, "적이 나온다");
            Assert.AreEqual("wave", state.Str("phase"));
            Assert.AreEqual(1, state.Int("wave"));
            Assert.AreEqual(96, state.List("enemies")[0].Int("max_hp"), "두 명: 60 × 160%");

            // 전투 중에 들어온 팀원은 지금의 상태를 받는다.
            TestClient late = await arena.JoinAsync(session, 2);
            Assert.AreNotEqual("lobby", late.Welcome.GetProperty("state").Str("phase"));
            Assert.AreEqual(3, late.Welcome.GetProperty("state").List("players").Length);
        }

        [Test]
        public async Task StartRefusedByTheGameServer_DoesNotStartTheBattle()
        {
            await using Arena arena = await Arena.StartAsync();
            FakeSession session = arena.Game.NewSession(2);
            TestClient a = await arena.JoinAsync(session, 0);
            // 게임 서버의 기록으로는 방장이 아닌 사람 (입장권을 받은 뒤에 방장이 바뀐 경우).
            session.HostPid = session.Members[1];
            TestClient b = await arena.JoinAsync(session, 1, t => t["host"] = false);
            await a.SendAsync("{\"type\":\"start\"}");
            Assert.AreEqual("not_host", (await RejectAsync(a, "start")).Str("reason"));
            Assert.AreEqual(1, session.StartCalls);
            Assert.AreEqual("lobby", (await b.StateAsync()).Str("phase"));

            // 게임 서버에 닿지 않을 때.
            session.HostPid = session.Members[0];
            arena.Game.Down = true;
            await a.SendAsync("{\"type\":\"start\"}");
            Assert.AreEqual("maintenance", (await RejectAsync(a, "start")).Str("reason"));
            Assert.AreEqual("lobby", (await b.StateAsync()).Str("phase"));
            arena.Game.Down = false;
            await a.SendAsync("{\"type\":\"start\"}");
            await b.EventAsync("started");
        }

        // ── 다중 플레이어 동시 타워 설치 ────────────────────

        [Test]
        public async Task FourPlayersBuildingOnTheSameSlotAtOnce_GetExactlyOneTower_ChargedOnce()
        {
            await using Arena arena = await Arena.StartAsync();
            FakeSession session = arena.Game.NewSession(4, QuietZone);
            List<TestClient> team = await arena.JoinAllAsync(session, 4);
            await arena.StartBattleAsync(team);

            int energy = 500;
            foreach (int slot in new[] { 6, 7, 8, 9 })
            {
                // 네 사람이 같은 순간에 같은 자리에 세운다.
                await Task.WhenAll(team.Select((client, i) => client.SendAsync(Build("slot" + slot + "-p" + i, slot))));
                var results = new List<JsonElement>();
                for (int i = 0; i < team.Count; i++) results.Add(await ResultAsync(team[i], "slot" + slot + "-p" + i));
                Assert.AreEqual(1, results.Count(r => r.Flag("ok")), "자리 " + slot + ": 한 사람만 된다");
                Assert.AreEqual(3, results.Count(r => r.Str("reject") == "slot_taken"));
                energy -= 100;
                JsonElement won = results.Single(r => r.Flag("ok"));
                Assert.AreEqual(energy, won.Int("energy"), "공동 예산은 한 번만 빠진다");
                string winner = team[results.IndexOf(won)].Pid;
                // 모두가 같은 알림을 한 번씩 받는다.
                foreach (TestClient client in team)
                {
                    JsonElement built = await client.SeenAsync(m => m.IsEvent("tower_built") && m.Int("slot") == slot, "tower_built " + slot);
                    Assert.AreEqual(winner, built.Str("pid"));
                }
            }
            // 모두가 보는 상태가 같다: 타워 넷, 예산 100.
            foreach (TestClient client in team)
            {
                JsonElement state = await client.StateAsync(s => s.List("towers").Length == 4, "타워 넷");
                Assert.AreEqual(100, state.Int("energy"));
                CollectionAssert.AreEqual(new[] { 6, 7, 8, 9 }, state.List("towers").Select(t => t.Int("slot")).ToArray());
                Assert.AreEqual(4, state.List("players").Sum(p => p.Int("towers_built")));
                Assert.AreEqual(4, client.Received(m => m.IsEvent("tower_built")).Count, "알림이 겹쳐 오지 않는다");
            }
            // 남의 이름으로 세울 수는 없다: 누가 보냈는지는 연결(입장권)이 정한다.
            await team[1].SendAsync("{\"type\":\"build\",\"request_id\":\"spoof\",\"slot\":10,\"code\":\"twr_machine_gun\",\"pid\":\"" + team[0].Pid + "\"}");
            Assert.IsTrue((await ResultAsync(team[1], "spoof")).Flag("ok"));
            JsonElement after = await team[0].StateAsync(s => s.List("towers").Length == 5, "타워 다섯");
            Assert.AreEqual(team[1].Pid, after.List("towers").Single(t => t.Int("slot") == 10).Str("owner"));
        }

        // ── 공동 예산 중복 차감 방지 ────────────────────────

        [Test]
        public async Task SharedBudget_IsNeverOverdrawn_AndRepeatedRequestsAreChargedOnce()
        {
            await using Arena arena = await Arena.StartAsync();
            FakeSession session = arena.Game.NewSession(4, QuietZone);
            List<TestClient> team = await arena.JoinAllAsync(session, 4);
            await arena.StartBattleAsync(team);

            // 예산 500. 네 사람이 같은 순간에 서로 다른 자리에 폭발 타워(175)를 산다: 둘만 된다.
            await Task.WhenAll(team.Select((client, i) => client.SendAsync(Build("blast-" + i, i + 1, "twr_blast"))));
            var results = new List<JsonElement>();
            for (int i = 0; i < 4; i++) results.Add(await ResultAsync(team[i], "blast-" + i));
            Assert.AreEqual(2, results.Count(r => r.Flag("ok")));
            Assert.AreEqual(2, results.Count(r => r.Str("reject") == "insufficient_budget"));
            Assert.IsTrue(results.All(r => r.Int("energy") >= 0));
            JsonElement state = await team[0].StateAsync(s => s.List("towers").Length == 2, "타워 둘");
            Assert.AreEqual(150, state.Int("energy"));
            int built = state.List("towers")[0].Int("slot");

            // 같은 요청을 다섯 번 보낸다 (다시 보내기, 연달아 누르기): 한 번만 처리된다.
            string upgrade = "{\"type\":\"upgrade\",\"request_id\":\"up-once\",\"slot\":" + built + "}";
            await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => team[0].SendAsync(upgrade)));
            var replies = new List<JsonElement>();
            for (int i = 0; i < 5; i++) replies.Add(await ResultAsync(team[0], "up-once"));
            Assert.IsTrue(replies.All(r => r.Flag("ok")));
            Assert.AreEqual(1, replies.Count(r => !r.Flag("duplicate")));
            Assert.AreEqual(4, replies.Count(r => r.Flag("duplicate")));
            Assert.IsTrue(replies.All(r => r.Int("energy") == 45), "150 - 105 = 45, 한 번만");

            // 남은 45 로는 아무도 강화할 수 없다. 네 사람이 한꺼번에 눌러도 그대로다.
            await Task.WhenAll(team.Select((client, i) => client.SendAsync("{\"type\":\"upgrade\",\"request_id\":\"broke-" + i + "\",\"slot\":" + built + "}")));
            for (int i = 0; i < 4; i++) Assert.AreEqual("insufficient_budget", (await ResultAsync(team[i], "broke-" + i)).Str("reject"));
            state = await team[3].StateAsync(s => s.List("towers").Any(t => t.Int("level") == 1), "강화된 타워");
            Assert.AreEqual(45, state.Int("energy"));
            Assert.AreEqual(1, state.List("towers").Sum(t => t.Int("level")), "한 단계만 올랐다");
            await team[2].StateAsync(s => s.Int("tick") >= state.Int("tick"), "같은 때의 상태");
            Assert.AreEqual(1, team[2].Received(m => m.IsEvent("tower_upgraded")).Count, "강화 알림도 한 번");
            // 어느 순간에도 예산이 음수였던 적이 없다.
            foreach (TestClient client in team)
                Assert.IsTrue(client.Received(m => m.Is("state")).All(s => s.Int("energy") >= 0));

            // 잘못된 요청들.
            await team[0].SendAsync(Build("bad-1", 99));
            Assert.AreEqual("bad_slot", (await ResultAsync(team[0], "bad-1")).Str("reject"));
            await team[0].SendAsync(Build("bad-2", 12, "twr_energy_shield"));
            Assert.AreEqual("bad_structure", (await ResultAsync(team[0], "bad-2")).Str("reject"));
            await team[0].SendAsync("{\"type\":\"repair\",\"request_id\":\"bad-3\",\"slot\":" + built + "}");
            Assert.AreEqual("not_damaged", (await ResultAsync(team[0], "bad-3")).Str("reject"));
        }

        [Test]
        public async Task PlayerWithoutTheUnlock_CannotBuildThatTower()
        {
            await using Arena arena = await Arena.StartAsync();
            FakeSession session = arena.Game.NewSession(2, QuietZone);
            TestClient a = await arena.JoinAsync(session, 0);
            // 기관총 타워만 해금한 사람.
            TestClient b = await arena.JoinAsync(session, 1, t => t["structs"] = new JsonArray("twr_machine_gun"));
            Assert.IsFalse(b.Welcome.List("structures").Single(s => s.Str("code") == "twr_slow").Flag("usable"));
            await arena.StartBattleAsync(new List<TestClient> { a, b });
            await b.SendAsync(Build("locked", 3, "twr_slow"));
            Assert.AreEqual("not_unlocked", (await ResultAsync(b, "locked")).Str("reject"));
            await a.SendAsync(Build("open", 3, "twr_slow"));
            Assert.IsTrue((await ResultAsync(a, "open")).Flag("ok"));
        }

        // ── 미니맵 실시간 위치 동기화 ───────────────────────

        [Test]
        public async Task Positions_AreDecidedByTheServer_AndEveryTeammateSeesTheSameMovement()
        {
            await using Arena arena = await Arena.StartAsync();
            FakeSession session = arena.Game.NewSession(3);
            List<TestClient> team = await arena.JoinAllAsync(session, 3);
            TestClient a = team[0], b = team[1];
            JsonElement before = (await b.StateAsync()).Player(a.Pid);
            Assert.AreEqual(-3f, before.Num("x"), 0.001f);
            Assert.AreEqual(-14f, before.Num("z"), 0.001f);

            // A 가 동쪽으로 20 m 떨어진 곳을 가리킨다. B 의 화면에서 A 는 조금씩 움직여 간다 (순간 이동하지 않는다).
            int from = b.Count;
            await a.SendAsync("{\"type\":\"move\",\"x\":17,\"z\":-14}");
            await b.StateAsync(s => Math.Abs(s.Player(a.Pid).Num("x") - 17f) < 0.001f, "A 가 도착한다");
            List<JsonElement> seen = b.All.Skip(from).Where(m => m.Is("state")).ToList();
            float last = -3f;
            int lastTick = seen[0].Int("tick") - 2;
            int moving = 0;
            foreach (JsonElement state in seen)
            {
                float x = state.Player(a.Pid).Num("x");
                int ticks = state.Int("tick") - lastTick;
                Assert.GreaterOrEqual(x, last - 0.001f, "뒤로 가지 않는다");
                Assert.LessOrEqual(x - last, 0.6f * ticks + 0.011f, "한 틱에 0.6 m (초속 6 m) 보다 빨리 움직이지 않는다");
                Assert.AreEqual(-14f, state.Player(a.Pid).Num("z"), 0.001f);
                if (x > last + 0.001f) moving++;
                last = x;
                lastTick = state.Int("tick");
            }
            Assert.Greater(moving, 10, "여러 번에 걸쳐 움직였다");
            // A 자신이 받은 위치도 같다 (클라이언트가 자기 위치를 정하지 않는다).
            JsonElement mine = await a.StateAsync();
            Assert.AreEqual(17f, mine.Player(a.Pid).Num("x"), 0.001f);

            // 지도 밖을 가리키면 지도 끝까지만 간다. 터무니없는 값은 받지 않는다.
            await a.SendAsync("{\"type\":\"move\",\"x\":99999,\"z\":-99999}");
            JsonElement edge = await b.StateAsync(s => s.Player(a.Pid).Num("x") >= 25.99f && s.Player(a.Pid).Num("z") <= -25.99f, "지도 끝");
            Assert.AreEqual(26f, edge.Player(a.Pid).Num("x"), 0.001f);
            Assert.AreEqual(-26f, edge.Player(a.Pid).Num("z"), 0.001f);
            await a.SendAsync("{\"type\":\"move\",\"x\":1e30,\"z\":0}");
            Assert.AreEqual("bad_request", (await RejectAsync(a, "message")).Str("reason"));
            await a.SendAsync("{\"type\":\"move\",\"x\":\"east\",\"z\":0}");
            Assert.AreEqual("bad_request", (await RejectAsync(a, "message")).Str("reason"));
            Assert.AreEqual(26f, (await b.StateAsync()).Player(a.Pid).Num("x"), 0.001f);
        }

        [Test]
        public async Task Enemies_TheirDirection_Towers_AndDangerZones_AreSharedLive()
        {
            await using Arena arena = await Arena.StartAsync();
            FakeSession session = arena.Game.NewSession(2, data =>
            {
                // 기계 벌레 여덟 마리가 몰려온다.
                data["zone"]["waves"] = JsonNode.Parse("[{\"groups\":[{\"enemy_code\":\"mech_insect\",\"count\":8,\"interval\":0.2,\"delay\":0}]}]");
            });
            List<TestClient> team = await arena.JoinAllAsync(session, 2);
            // 두 사람은 구석으로 비켜서 쏘지 않는다.
            await team[0].SendAsync("{\"type\":\"move\",\"x\":-26,\"z\":-26}");
            await team[1].SendAsync("{\"type\":\"move\",\"x\":26,\"z\":-26}");
            await team[0].StateAsync(s => s.Player(team[0].Pid).Num("x") < -25.9f && s.Player(team[1].Pid).Num("x") > 25.9f, "비켜서기");
            await arena.StartBattleAsync(team);
            // 관문 앞에 타워를 하나 세워 둔다.
            await team[0].SendAsync(Build("gun", 15));
            Assert.IsTrue((await ResultAsync(team[0], "gun")).Flag("ok"));

            JsonElement first = await team[1].StateAsync(s => s.List("enemies").Length == 8, "여덟 마리");
            foreach (JsonElement enemy in first.List("enemies"))
            {
                Assert.AreEqual("mech_insect", enemy.Str("code"));
                Assert.AreEqual(0f, enemy.Num("x"), 0.001f);
                Assert.AreEqual(0f, enemy.Num("dx"), 0.001f);
                Assert.AreEqual(-1f, enemy.Num("dz"), 0.001f, "처음에는 남쪽으로 내려온다");
                Assert.Greater(enemy.Int("hp"), 0);
                Assert.IsFalse(enemy.Flag("boss"));
            }
            // 타워와 그 사거리.
            JsonElement tower = first.List("towers").Single();
            Assert.AreEqual(15, tower.Int("slot"));
            Assert.AreEqual(8f, tower.Num("range"), 0.001f);
            Assert.AreEqual(team[0].Pid, tower.Str("owner"));
            // 적이 몰려 있는 곳이 위험 지역으로 온다.
            JsonElement danger = (await team[1].StateAsync(s => s.List("dangers").Length > 0, "위험 지역")).List("dangers")[0];
            Assert.GreaterOrEqual(danger.Int("enemies"), 6);

            // 두 사람이 받는 적의 위치가 같다 (같은 틱의 상태를 견준다).
            await team[0].StateAsync(s => s.List("enemies").Length > 0 && s.List("enemies")[0].Num("dx") < -0.5f, "첫 굽이를 돈다");
            var mine = new Dictionary<int, string>();
            foreach (JsonElement state in team[0].Received(m => m.Is("state") && m.List("enemies").Length > 0))
                mine[state.Int("tick")] = state.GetProperty("enemies").GetRawText();
            var theirs = new Dictionary<int, string>();
            foreach (JsonElement state in team[1].Received(m => m.Is("state") && m.List("enemies").Length > 0))
                theirs[state.Int("tick")] = state.GetProperty("enemies").GetRawText();
            int compared = 0;
            foreach (var pair in theirs)
            {
                if (!mine.TryGetValue(pair.Key, out string enemies)) continue;
                Assert.AreEqual(enemies, pair.Value, "틱 " + pair.Key);
                compared++;
            }
            Assert.Greater(compared, 10);

            // 관문 가까이 오면 관문 위험으로 바뀌고, 닿으면 관문 내구도가 준다.
            JsonElement near = await team[1].StateAsync(s => s.List("dangers").Any(d => d.Flag("near_gate")), "관문 위험");
            Assert.IsTrue(near.List("dangers").First(d => d.Flag("near_gate")).Int("enemies") > 0);
            await team[1].EventAsync("gate_hit");
            JsonElement hit = await team[0].StateAsync(s => s.Int("gate_hp") < 20, "관문 내구도");
            Assert.AreEqual(20, hit.Int("gate_max_hp"));
        }

        [Test]
        public async Task TacticalPings_AreSharedLive_RateLimited_Blockable_AndHideable()
        {
            await using Arena arena = await Arena.StartAsync();
            FakeSession session = arena.Game.NewSession(4, QuietZone);
            TestClient a = await arena.JoinAsync(session, 0);
            TestClient b = await arena.JoinAsync(session, 1);
            TestClient c = await arena.JoinAsync(session, 2);
            // 관리자가 핑을 막은 계정 (입장권에 실려 온다).
            TestClient d = await arena.JoinAsync(session, 3, t => t["ping_blocked"] = true);
            Assert.IsTrue(d.Welcome.GetProperty("ping").Flag("blocked"));
            var team = new List<TestClient> { a, b, c, d };
            await arena.StartBattleAsync(team);

            // A 의 방어 요청이 모두에게 간다 (보낸 사람에게도).
            await a.SendAsync(Ping("defend_here", 0f, 30f));
            foreach (TestClient client in team)
            {
                JsonElement ping = await client.EventAsync("ping");
                Assert.AreEqual(a.Pid, ping.Str("pid"));
                Assert.AreEqual("defend_here", ping.Str("code"));
                Assert.AreEqual(0f, ping.Num("x"), 0.001f);
                Assert.AreEqual(30f, ping.Num("z"), 0.001f);
            }
            JsonElement shown = (await b.StateAsync(s => s.List("pings").Length == 1, "지도의 핑")).List("pings")[0];
            Assert.AreEqual("defend_here", shown.Str("kind"));
            Assert.Greater(shown.Int("left_ms"), 0);
            Assert.LessOrEqual(shown.Int("left_ms"), 8000);
            // 8초(전투 시계)가 지나면 지도에서 사라진다.
            await b.StateAsync(s => s.List("pings").Length == 0, "핑이 사라진다");

            // 지도 밖을 찍으면 지도 끝에 찍힌다. 모르는 종류는 받지 않는다.
            await b.SendAsync(Ping("rally", 5000f, 5000f));
            JsonElement clamped = await c.EventAsync("ping");
            Assert.AreEqual(26f, clamped.Num("x"), 0.001f);
            Assert.AreEqual(44f, clamped.Num("z"), 0.001f);
            await b.SendAsync(Ping("insult", 0f, 0f));
            Assert.AreEqual("ping_kind", (await RejectAsync(b, "ping")).Str("reason"));

            // 막힌 계정의 핑은 아무에게도 가지 않는다.
            int seenByA = a.Received(m => m.IsEvent("ping")).Count;
            await d.SendAsync(Ping("danger", 1f, 1f));
            Assert.AreEqual("ping_blocked", (await RejectAsync(d, "ping")).Str("reason"));

            // C 가 A 의 핑을 숨긴다: C 에게만 오지 않는다.
            await c.SendAsync("{\"type\":\"hide_pings\",\"pid\":\"" + a.Pid + "\",\"hide\":true}");
            Assert.AreEqual(a.Pid, (await c.WaitAsync(m => m.Is("hidden"), "hidden")).Str("pid"));
            int seenByC = c.Received(m => m.IsEvent("ping")).Count;
            await a.SendAsync(Ping("focus_fire", -14f, 16f));
            JsonElement toB = await b.WaitAsync(m => m.IsEvent("ping") && m.Str("code") == "focus_fire", "B 가 받는 핑");
            Assert.AreEqual(a.Pid, toB.Str("pid"));
            JsonElement stateB = await b.StateAsync(s => s.List("pings").Any(p => p.Str("kind") == "focus_fire"), "B 의 지도");
            JsonElement stateC = await c.StateAsync(s => s.Int("tick") >= stateB.Int("tick"), "C 의 지도");
            Assert.IsFalse(stateC.List("pings").Any(p => p.Str("pid") == a.Pid), "숨긴 사람의 핑은 지도에도 없다");
            Assert.AreEqual(seenByC, c.Received(m => m.IsEvent("ping")).Count);
            Assert.AreEqual(seenByA + 1, a.Received(m => m.IsEvent("ping")).Count, "막힌 계정의 핑은 오지 않았다");
            // 자기 핑이나 없는 사람은 숨길 수 없다.
            await c.SendAsync("{\"type\":\"hide_pings\",\"pid\":\"" + c.Pid + "\"}");
            Assert.AreEqual("bad_request", (await RejectAsync(c, "hide_pings")).Str("reason"));

            // 남용: 한꺼번에 열 개를 보내면 몇 개만 되고 나머지는 빈도 제한에 걸린다. 계속하면 잠시 막힌다.
            int before = c.Received(m => m.IsEvent("ping") && m.Str("pid") == b.Pid).Count;
            await Task.WhenAll(Enumerable.Range(0, 10).Select(i => b.SendAsync(Ping("danger", i, 0f))));
            await b.WaitAsync(m => m.Is("reject") && m.Str("reason") == "ping_muted", "잠시 막힘");
            await Task.Delay(200);
            int rejected = b.Received(m => m.Is("reject") && m.Str("action") == "ping" && m.Str("reason") != "ping_kind").Count;
            Assert.GreaterOrEqual(rejected, 6);
            int delivered = c.Received(m => m.IsEvent("ping") && m.Str("pid") == b.Pid).Count - before;
            Assert.LessOrEqual(delivered, 4, "연달아 셋까지 (그 사이에 하나쯤 다시 찰 수 있다)");
            Assert.AreEqual(10, delivered + rejected);
        }

        [Test]
        public async Task Roles_AreOptional_AndTheCommanderSeatIsSingle()
        {
            await using Arena arena = await Arena.StartAsync();
            FakeSession session = arena.Game.NewSession(3, QuietZone);
            TestClient a = await arena.JoinAsync(session, 0);
            // 대기실에서 고른 역할은 입장권에 실려 온다.
            TestClient b = await arena.JoinAsync(session, 1, t => t["role"] = "builder");
            TestClient c = await arena.JoinAsync(session, 2);
            JsonElement state = await c.StateAsync();
            Assert.AreEqual("builder", state.Player(b.Pid).Str("role"));
            Assert.IsNull(state.Player(a.Pid).Str("role"), "역할을 고르지 않아도 된다");

            await a.SendAsync("{\"type\":\"set_role\",\"role\":\"commander\"}");
            foreach (TestClient client in new[] { a, b, c })
            {
                JsonElement changed = await client.EventAsync("role_changed");
                Assert.AreEqual(a.Pid, changed.Str("pid"));
                Assert.AreEqual("commander", changed.Str("code"));
            }
            await c.SendAsync("{\"type\":\"set_role\",\"role\":\"commander\"}");
            Assert.AreEqual("role_taken", (await RejectAsync(c, "set_role")).Str("reason"));
            await c.SendAsync("{\"type\":\"set_role\",\"role\":\"king\"}");
            Assert.AreEqual("bad_role", (await RejectAsync(c, "set_role")).Str("reason"));

            await arena.StartBattleAsync(new List<TestClient> { a, b, c });
            // 설치 담당은 10% 싸게 세운다.
            await b.SendAsync(Build("cheap", 6));
            Assert.AreEqual(400 - 90, (await ResultAsync(b, "cheap")).Int("energy"));
            await c.SendAsync(Build("full", 7));
            Assert.AreEqual(400 - 90 - 100, (await ResultAsync(c, "full")).Int("energy"));
            // 전투 중에도 바꿀 수 있다. 지휘관이 내려놓으면 다른 사람이 맡는다.
            await a.SendAsync("{\"type\":\"set_role\",\"role\":null}");
            await c.WaitAsync(m => m.IsEvent("role_changed") && m.Str("pid") == a.Pid && m.Str("code") == null, "지휘관을 내려놓음");
            await c.SendAsync("{\"type\":\"set_role\",\"role\":\"commander\"}");
            await a.WaitAsync(m => m.IsEvent("role_changed") && m.Str("pid") == c.Pid, "새 지휘관");
            Assert.AreEqual("commander", (await a.StateAsync()).Player(c.Pid).Str("role"));
        }

        // ── 중도 이탈, 결과 보고 ────────────────────────────

        [Test]
        public async Task BattleGoesOn_WhenAPlayerDrops_TheyCanReturn_AndTheResultIsReportedOnce()
        {
            await using Arena arena = await Arena.StartAsync();
            FakeSession session = arena.Game.NewSession(3, FakeGame.ShortZone);
            List<TestClient> team = await arena.JoinAllAsync(session, 3);
            TestClient a = team[0], b = team[1], c = team[2];
            await arena.StartBattleAsync(team);
            await a.SendAsync(Build("gun", 15));
            Assert.IsTrue((await ResultAsync(a, "gun")).Flag("ok"));

            // C 의 연결이 갑자기 끊긴다.
            c.Abort();
            JsonElement left = await a.EventAsync("player_left");
            Assert.AreEqual(c.Pid, left.Str("pid"));
            JsonElement state = await b.StateAsync(s => !s.Player(c.Pid).Flag("connected"), "C 가 끊긴 것으로 보인다");
            Assert.AreEqual(3, state.List("players").Length, "자리는 남아 있다");
            // 전투는 그대로 흘러간다.
            await EventuallyAsync(() => a.Received(m => m.IsEvent("wave_started")).Count > 0, "웨이브가 시작된다");
            await b.StateAsync(s => s.List("enemies").Length > 0, "적이 온다");

            // C 가 새 입장권으로 돌아온다.
            TestClient back = await arena.JoinAsync(session, 2);
            Assert.AreEqual("wave", back.Welcome.GetProperty("state").Str("phase"));
            Assert.AreEqual(1, back.Welcome.GetProperty("state").List("towers").Length, "그동안의 상태를 그대로 받는다");
            await a.WaitAsync(m => m.IsEvent("player_joined") && m.Str("pid") == c.Pid, "C 가 돌아옴");
            Assert.IsTrue((await b.StateAsync()).Player(c.Pid).Flag("connected"));

            // 끝까지 간다.
            JsonElement finished = await a.WaitAsync(m => m.Is("finished"), "finished", 20000);
            Assert.AreEqual("win", finished.Str("result"));
            Assert.AreEqual(1, finished.Int("waves_cleared"));
            Assert.AreEqual("completed", finished.Str("close_reason"));
            Assert.IsFalse(finished.GetRawText().Contains("reward"), "보상은 게임 서버에서 확인한다");
            foreach (TestClient client in new[] { a, b, back })
            {
                JsonElement recorded = await client.WaitAsync(m => m.Is("recorded"), "recorded");
                Assert.IsTrue(recorded.Flag("ok"));
                Assert.IsTrue(recorded.Flag("accepted"));
                await client.WaitClosedAsync();
            }

            // 게임 서버는 결과를 한 번 받았고, 받아들였다 (장부와 걸린 시간의 검증을 지났다).
            Assert.AreEqual("finished", session.Status);
            Assert.AreEqual(1, session.ResultCalls);
            Assert.IsNull(session.RejectReason);
            Assert.IsTrue(session.Accepted);
            JsonElement report = session.Report;
            Assert.AreEqual("win", report.Str("result"));
            Assert.AreEqual("completed", report.Str("close_reason"));
            CollectionAssert.AreEquivalent(session.Members, report.List("members").Select(m => m.Str("pid")).ToArray());
            Assert.AreEqual(6, report.List("members").Sum(m => m.Int("kills")) + (20 - report.Int("gate_hp_left")), "여섯 마리 모두 쓰러졌거나 관문에 닿았다");
            JsonElement dropped = report.List("members").Single(m => m.Str("pid") == c.Pid);
            JsonElement stayed = report.List("members").Single(m => m.Str("pid") == a.Pid);
            Assert.Less(dropped.Int("connected_seconds"), stayed.Int("connected_seconds"), "끊겨 있던 시간은 빠진다");
            Assert.AreEqual(report.Int("duration_seconds"), stayed.Int("connected_seconds"));
            Assert.AreEqual(1, stayed.Int("towers_built"));
            JsonElement[] budget = report.List("budget");
            Assert.AreEqual("grant_start", budget[0].Str("kind"));
            Assert.AreEqual(400, budget[0].Int("delta"), "세 명: 200 × 200%");
            Assert.AreEqual(1, budget.Count(l => l.Str("kind") == "build"));
            Assert.IsFalse(report.GetRawText().Contains("reward"));

            // 방은 치워졌고, 끝난 판에는 다시 들어갈 수 없다.
            await EventuallyAsync(() => arena.Host.Rooms.RoomCount == 0, "방이 치워진다");
            Assert.AreEqual("session_closed", await arena.JoinErrorAsync(arena.Game.Ticket(session, 0)));
            Assert.AreEqual(1, session.ResultCalls);
        }

        [Test]
        public async Task EveryoneLeaving_ClosesTheBattleAsALoss_AfterAMinute()
        {
            await using Arena arena = await Arena.StartAsync();
            FakeSession session = arena.Game.NewSession(2, QuietZone);
            List<TestClient> team = await arena.JoinAllAsync(session, 2);
            await arena.StartBattleAsync(team);
            team[0].Abort();
            team[1].Abort();
            // 전투 시계로 60초 (테스트에서는 6초) 동안 아무도 돌아오지 않으면 닫는다.
            await EventuallyAsync(() => session.Status == "finished", "판이 닫힌다", 15000);
            Assert.IsTrue(session.Accepted);
            Assert.AreEqual("lose", session.Report.Str("result"));
            Assert.AreEqual("all_left", session.Report.Str("close_reason"));
            Assert.GreaterOrEqual(session.Report.Int("duration_seconds"), 60);
            Assert.IsTrue(session.Report.List("members").All(m => m.Int("connected_seconds") <= 1));
            await EventuallyAsync(() => arena.Host.Rooms.RoomCount == 0, "방이 치워진다");
        }

        [Test]
        public async Task Result_IsRetried_UntilTheGameServerTakesIt()
        {
            await using Arena arena = await Arena.StartAsync();
            FakeSession session = arena.Game.NewSession(2, FakeGame.ShortZone);
            List<TestClient> team = await arena.JoinAllAsync(session, 2);
            await arena.StartBattleAsync(team);
            // 게임 서버가 두 번은 "점검 중"이라고 답한다.
            arena.Game.FailResults = 2;
            JsonElement recorded = await team[0].WaitAsync(m => m.Is("recorded"), "recorded", 20000);
            Assert.IsTrue(recorded.Flag("ok"));
            Assert.IsTrue(recorded.Flag("accepted"));
            Assert.AreEqual(3, arena.Game.ResultAttempts);
            Assert.AreEqual(1, session.ResultCalls, "반영은 한 번");
            Assert.IsTrue(session.Accepted);
        }

        [Test]
        public async Task ResultThatCannotBeDelivered_IsKeptAndSentLater()
        {
            await using Arena arena = await Arena.StartAsync(o => o.ResultRetrySeconds = new[] { 0 });
            FakeSession session = arena.Game.NewSession(2, FakeGame.ShortZone);
            List<TestClient> team = await arena.JoinAllAsync(session, 2);
            await arena.StartBattleAsync(team);
            arena.Game.Down = true;
            JsonElement recorded = await team[0].WaitAsync(m => m.Is("recorded"), "recorded", 20000);
            Assert.IsFalse(recorded.Flag("ok"));
            Assert.IsTrue(recorded.Flag("pending"), "나중에 다시 보낸다고 알려 준다");
            Assert.AreEqual(1, arena.Host.Rooms.PendingReports);
            Assert.AreEqual("running", session.Status);
            // 그 사이에 다시 들어오려 해도 끝난 판이다 (새로 열리지 않고, 중단된 판으로 닫히지도 않는다).
            await EventuallyAsync(() => arena.Host.Rooms.RoomCount == 0, "방이 치워진다");
            arena.Game.Down = false;
            Assert.AreEqual("session_closed", await arena.JoinErrorAsync(arena.Game.Ticket(session, 0)));
            Assert.AreEqual("running", session.Status);

            await arena.Host.Rooms.FlushPendingAsync();
            Assert.AreEqual(0, arena.Host.Rooms.PendingReports);
            Assert.AreEqual("finished", session.Status);
            Assert.IsTrue(session.Accepted);
            Assert.AreEqual("win", session.Report.Str("result"));
        }

        [Test]
        public async Task BattleInterruptedByARestart_IsClosedWithoutRewards()
        {
            await using Arena arena = await Arena.StartAsync();
            // 게임 서버에는 "전투 중"으로 남아 있는데 실시간 서버에는 방이 없다 (전투 중에 다시 떴다).
            FakeSession session = arena.Game.NewSession(2);
            session.Status = "running";
            session.StartedAt = DateTime.UtcNow.AddSeconds(-30);
            Assert.AreEqual("session_interrupted", await arena.JoinErrorAsync(arena.Game.Ticket(session, 0)));
            Assert.AreEqual("finished", session.Status);
            Assert.IsTrue(session.Accepted);
            Assert.AreEqual("lose", session.Report.Str("result"));
            Assert.AreEqual("shutdown", session.Report.Str("close_reason"));
            Assert.AreEqual(0, session.Report.Int("duration_seconds"), "걸린 시간 0: 보상도 하루 횟수 차감도 없다");
            Assert.AreEqual(0, session.Report.List("members").Length);
            Assert.AreEqual(0, arena.Host.Rooms.RoomCount);
            Assert.AreEqual("session_closed", await arena.JoinErrorAsync(arena.Game.Ticket(session, 1)));
        }

        [Test]
        public async Task OrphanClosing_CanBeTurnedOff()
        {
            await using Arena arena = await Arena.StartAsync(o => o.CloseOrphans = false);
            FakeSession session = arena.Game.NewSession(2);
            session.Status = "running";
            session.StartedAt = DateTime.UtcNow;
            Assert.AreEqual("session_interrupted", await arena.JoinErrorAsync(arena.Game.Ticket(session, 0)));
            Assert.AreEqual("running", session.Status);
            Assert.AreEqual(0, session.ResultCalls);
        }

        [Test]
        public async Task ServerShutdown_ReportsRunningBattles_AndTellsEveryone()
        {
            await using Arena arena = await Arena.StartAsync();
            FakeSession battle = arena.Game.NewSession(2, QuietZone);
            List<TestClient> team = await arena.JoinAllAsync(battle, 2);
            await arena.StartBattleAsync(team);
            await team[0].SendAsync(Build("gun", 6));
            Assert.IsTrue((await ResultAsync(team[0], "gun")).Flag("ok"));
            FakeSession lobby = arena.Game.NewSession(2);
            TestClient waiting = await arena.JoinAsync(lobby, 0);

            await arena.StopAsync();

            JsonElement finished = await team[1].WaitAsync(m => m.Is("finished"), "finished");
            Assert.AreEqual("lose", finished.Str("result"));
            Assert.AreEqual("shutdown", finished.Str("close_reason"));
            Assert.IsTrue((await team[1].WaitAsync(m => m.Is("recorded"), "recorded")).Flag("ok"));
            Assert.AreEqual("shutdown", (await waiting.WaitAsync(m => m.Is("closed"), "closed")).Str("reason"));
            await waiting.WaitClosedAsync();
            Assert.AreEqual("finished", battle.Status);
            Assert.IsTrue(battle.Accepted);
            Assert.AreEqual("shutdown", battle.Report.Str("close_reason"));
            Assert.AreEqual(1, battle.Report.List("budget").Count(l => l.Str("kind") == "build"), "그때까지의 기록이 함께 간다");
            Assert.AreEqual("lobby", lobby.Status, "대기실은 게임 서버에 그대로 남는다");
            Assert.AreEqual(0, lobby.ResultCalls);
        }

        [Test]
        public async Task SessionClosedOnTheGameServer_IsNoticed_AndNothingIsReported()
        {
            await using Arena arena = await Arena.StartAsync(o => o.SessionCheckSeconds = 0);
            FakeSession lobby = arena.Game.NewSession(2);
            TestClient waiting = await arena.JoinAsync(lobby, 0);
            FakeSession battle = arena.Game.NewSession(2, QuietZone);
            List<TestClient> team = await arena.JoinAllAsync(battle, 2);
            await arena.StartBattleAsync(team);
            await team[0].StateAsync();

            // 방장이 대기실을 취소했다. 관리자가 전투 중인 판을 닫았다.
            lobby.Status = "cancelled";
            battle.Status = "abandoned";
            Assert.AreEqual("session_closed", (await waiting.WaitAsync(m => m.Is("closed"), "closed")).Str("reason"));
            Assert.AreEqual("session_closed", (await team[1].WaitAsync(m => m.Is("closed"), "closed")).Str("reason"));
            await team[0].WaitClosedAsync();
            await EventuallyAsync(() => arena.Host.Rooms.RoomCount == 0, "방이 치워진다");
            Assert.AreEqual(0, battle.ResultCalls + lobby.ResultCalls, "이미 닫힌 판에는 결과를 보내지 않는다");
        }

        [Test]
        public async Task EightPlayers_PlayInOneRoom()
        {
            await using Arena arena = await Arena.StartAsync();
            FakeSession session = arena.Game.NewSession(9, data =>
            {
                QuietZone(data);
                data["session"]["max_players"] = 8;
            });
            List<TestClient> team = await arena.JoinAllAsync(session, 8);
            // 아홉 번째 사람은 들어오지 못한다.
            Assert.AreEqual("session_full", await arena.JoinErrorAsync(arena.Game.Ticket(session, 8)));
            await arena.StartBattleAsync(team);
            JsonElement state = await team[7].StateAsync();
            Assert.AreEqual(8, state.List("players").Length);
            Assert.AreEqual(8, state.List("players").Select(p => p.Num("x") + "," + p.Num("z")).Distinct().Count(), "시작 자리가 겹치지 않는다");
            Assert.AreEqual(200 * 450 / 100, state.Int("energy"), "여덟 명: 200 × 450%");
            Assert.AreEqual(8, session.StartedWith.Count);

            // 여덟 명이 한꺼번에 여덟 자리에 세운다: 모두 되고, 예산은 정확히 800 이 빠진다.
            await Task.WhenAll(team.Select((client, i) => client.SendAsync(Build("eight-" + i, i))));
            for (int i = 0; i < 8; i++) Assert.IsTrue((await ResultAsync(team[i], "eight-" + i)).Flag("ok"));
            state = await team[0].StateAsync(s => s.List("towers").Length == 8, "타워 여덟");
            Assert.AreEqual(100, state.Int("energy"));
        }

        // ── 남용 방지 ───────────────────────────────────────

        [Test]
        public async Task OversizedBinaryAndFloodingConnections_AreCut_WithoutDisturbingTheRoom()
        {
            await using Arena arena = await Arena.StartAsync();
            FakeSession session = arena.Game.NewSession(4, QuietZone);
            List<TestClient> team = await arena.JoinAllAsync(session, 4);

            // 알 수 없는 메시지는 거절하지만 끊지는 않는다.
            await team[0].SendAsync("{\"type\":\"dance\"}");
            Assert.AreEqual("bad_request", (await RejectAsync(team[0], "message")).Str("reason"));
            await team[0].SendAsync("not json at all");
            Assert.AreEqual("bad_request", (await RejectAsync(team[0], "message")).Str("reason"));
            await team[0].SendAsync("{\"type\":\"alive\"}");
            await team[0].StateAsync();

            // 너무 큰 메시지.
            await team[1].SendAsync("{\"type\":\"alive\",\"pad\":\"" + new string('x', 3000) + "\"}");
            await team[1].WaitClosedAsync();
            Assert.AreEqual(WebSocketCloseStatus.MessageTooBig, team[1].CloseStatus);

            // 글이 아닌 메시지.
            await team[2].SendBytesAsync(new byte[] { 1, 2, 3 }, WebSocketMessageType.Binary);
            await team[2].WaitClosedAsync();
            Assert.AreEqual(WebSocketCloseStatus.InvalidMessageType, team[2].CloseStatus);

            // 메시지를 쏟아붓는 연결.
            try
            {
                for (int i = 0; i < 400; i++) await team[3].SendAsync("{\"type\":\"move\",\"x\":" + (i % 20) + ",\"z\":0}");
            }
            catch (WebSocketException)
            {
                // 보내는 도중에 끊겼다.
            }
            await team[3].WaitClosedAsync();
            Assert.AreEqual("flood", team[3].CloseReason);

            // 남은 사람은 그대로 상태를 받는다. 끊긴 사람들은 나간 것으로 보인다.
            JsonElement state = await team[0].StateAsync(s => s.List("players").Count(p => p.Flag("connected")) == 1, "혼자 남음");
            Assert.AreEqual(4, state.List("players").Length);
            Assert.AreEqual(3, team[0].Received(m => m.IsEvent("player_left")).Count);
        }

        [Test]
        public async Task OnlyAllowedOrigins_AndALimitedNumberOfConnectionsPerAddress()
        {
            await using Arena arena = await Arena.StartAsync(o =>
            {
                o.AllowedOrigins.Add("https://play.cg.test");
                o.MaxConnectionsPerIp = 3;
            });
            // 브라우저가 다른 사이트에서 붙으려 하면 받지 않는다.
            WebSocketException refused = Assert.ThrowsAsync<WebSocketException>(async () =>
                await arena.ConnectAsync(o => o.SetRequestHeader("Origin", "https://evil.example")));
            StringAssert.Contains("403", refused.Message);
            // 허용한 곳과, Origin 이 없는 연결(앱)은 받는다.
            TestClient web = await arena.ConnectAsync(o => o.SetRequestHeader("Origin", "https://play.cg.test"));
            TestClient app1 = await arena.ConnectAsync();
            TestClient app2 = await arena.ConnectAsync();
            Assert.AreEqual(3, arena.Host.Limits.Total);
            // 한 주소에서 너무 많이 붙을 수 없다.
            WebSocketException full = Assert.ThrowsAsync<WebSocketException>(async () => await arena.ConnectAsync());
            StringAssert.Contains("503", full.Message);
            await app2.DisposeAsync();
            await EventuallyAsync(() => arena.Host.Limits.Total == 2, "자리가 난다");
            await arena.ConnectAsync();

            using (var http = new HttpClient())
            {
                HttpResponseMessage health = await http.GetAsync(arena.HttpUrl + "/health");
                Assert.AreEqual(HttpStatusCode.OK, health.StatusCode);
                string body = await health.Content.ReadAsStringAsync();
                StringAssert.Contains("\"ok\":true", body);
                Assert.IsFalse(body.Contains(FakeGame.Secret) || body.Contains("127.0.0.1"), "상태 확인은 수만 알려 준다");
                Assert.IsFalse(health.Headers.Contains("Server"));
                Assert.AreEqual(HttpStatusCode.BadRequest, (await http.GetAsync(arena.HttpUrl + "/ws")).StatusCode, "WebSocket 이 아닌 요청");
                Assert.AreEqual(HttpStatusCode.NotFound, (await http.GetAsync(arena.HttpUrl + "/")).StatusCode);
                Assert.AreEqual(HttpStatusCode.NotFound, (await http.GetAsync(arena.HttpUrl + "/internal/coop/sessions/x/config")).StatusCode);
            }
            GC.KeepAlive(web);
            GC.KeepAlive(app1);
        }

        [Test]
        public async Task TooManyRooms_AreRefused()
        {
            await using Arena arena = await Arena.StartAsync(o => o.MaxRooms = 2);
            await arena.JoinAsync(arena.Game.NewSession(2), 0);
            await arena.JoinAsync(arena.Game.NewSession(2), 0);
            FakeSession third = arena.Game.NewSession(2);
            Assert.AreEqual("server_full", await arena.JoinErrorAsync(arena.Game.Ticket(third, 0)));
            Assert.AreEqual(2, arena.Host.Rooms.RoomCount);
        }

        [Test]
        public async Task EmptyLobby_IsForgotten_ButTheSessionStaysOnTheGameServer()
        {
            await using Arena arena = await Arena.StartAsync(o => o.EmptyLobbySeconds = 1);
            FakeSession session = arena.Game.NewSession(2);
            TestClient a = await arena.JoinAsync(session, 0);
            Assert.AreEqual(1, arena.Host.Rooms.RoomCount);
            await a.DisposeAsync();
            await EventuallyAsync(() => arena.Host.Rooms.RoomCount == 0, "빈 방이 치워진다");
            Assert.AreEqual("lobby", session.Status);
            Assert.AreEqual(0, session.ResultCalls);
            // 다시 오면 방이 새로 열린다.
            TestClient again = await arena.JoinAsync(session, 0);
            Assert.AreEqual("lobby", again.Welcome.GetProperty("state").Str("phase"));
        }
    }
}
