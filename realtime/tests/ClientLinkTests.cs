using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CityGuardians.Domain;
using CityGuardians.Services;
using NUnit.Framework;

namespace CityGuardians.Realtime.Tests
{
    /// <summary>
    /// 게임 클라이언트의 연결 코드(client/Assets/CityGuardians/Services/Coop*.cs)를 그대로 가져와 실제 실시간 서버에 붙여 본다.
    /// Unity 에디터 없이, 클라이언트와 서버가 같은 말을 하는지 확인하는 테스트다.
    /// </summary>
    public sealed class ClientLinkTests
    {
        private sealed class Player : IDisposable
        {
            public WebSocketCoopSocket Socket;
            public RemoteCoopLink Link;
            public List<CoopEvent> Events = new List<CoopEvent>();
            public List<CoopActionResult> Results = new List<CoopActionResult>();
            private readonly Stopwatch watch = Stopwatch.StartNew();
            private double last;

            public void Pump()
            {
                double now = watch.Elapsed.TotalSeconds;
                Link.Update((float)(now - last));
                last = now;
                Events.AddRange(Link.DrainEvents());
                Results.AddRange(Link.DrainResults());
            }

            public async Task Until(Func<bool> condition, string what, int timeoutMs = 8000)
            {
                DateTime until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                while (true)
                {
                    Pump();
                    if (condition()) return;
                    if (DateTime.UtcNow >= until)
                        Assert.Fail("기다리던 일이 일어나지 않았습니다: " + what + " (상태 " + Link.State + ", 닫힌 이유 " + Link.ClosedReason + ")");
                    await Task.Delay(5);
                }
            }

            public void Dispose() => Socket.Dispose();
        }

        private static Player Connect(Arena arena, FakeSession session, int member, Action<System.Text.Json.Nodes.JsonObject> ticket = null)
        {
            var player = new Player { Socket = new WebSocketCoopSocket(arena.WsUrl) };
            player.Link = new RemoteCoopLink(player.Socket, arena.Game.Ticket(session, member, ticket));
            return player;
        }

        [Test]
        public async Task GameClient_PlaysAWholeBattle_AgainstTheRealServer()
        {
            await using Arena arena = await Arena.StartAsync();
            FakeSession session = arena.Game.NewSession(2, FakeGame.ShortZone);
            using Player me = Connect(arena, session, 0);
            Assert.AreEqual(CoopLinkStates.Connecting, me.Link.State);
            Assert.IsNull(me.Link.Snapshot);
            await me.Until(() => me.Link.State == CoopLinkStates.Lobby, "대기실");

            // 들어오면서 받은 것: 지도, 타워, 역할, 핑 규칙.
            RemoteCoopLink link = me.Link;
            Assert.IsFalse(link.Practice);
            Assert.AreEqual(session.Members[0], link.MyPid);
            Assert.AreEqual(session.HostPid, link.HostPid);
            Assert.AreEqual("종로구", link.RegionName);
            Assert.AreEqual("외곽 1구역", link.ZoneName);
            Assert.AreEqual(4, link.MaxPlayers);
            Assert.AreEqual(2, link.MinPlayersToStart);
            Assert.AreEqual(10, link.RepairEnergy);
            Assert.AreEqual(8f, link.RepairRangeM);
            Assert.AreEqual(100, link.TickMs);
            Assert.AreEqual(16, link.Map.slots.Length);
            Assert.AreEqual(8, link.Map.path.Length);
            Assert.AreEqual(116f, link.Map.PathLength, 0.01f);
            Assert.AreEqual("도시 관문", link.Map.landmarks.Single(l => l.kind == CoopLandmarkKinds.Gate).name);
            CoopMap same = CoopMap.ForZone("outskirts_1");
            for (int i = 0; i < same.slots.Length; i++)
            {
                Assert.AreEqual(same.slots[i].x, link.Map.slots[i].x);
                Assert.AreEqual(same.slots[i].z, link.Map.slots[i].z);
            }
            Assert.AreEqual(5, link.Structures.Count);
            Assert.IsTrue(link.Structures.All(s => s.usable));
            TowerDef gun = link.Structures.Single(s => s.def.code == "twr_machine_gun").def;
            Assert.AreEqual("기관총 타워", gun.name);
            Assert.AreEqual(100, gun.energyCost);
            Assert.AreEqual(8f, gun.range);
            Assert.Greater(gun.maxHp, 0);
            Assert.Greater(gun.upgradeEnergyCost, 0);
            CollectionAssert.AreEqual(new[] { "commander", "builder", "support", "fighter" }, link.Roles.Select(r => r.code).ToArray());
            Assert.AreEqual("타워 설치 담당", link.Roles[1].name);
            Assert.AreEqual(10, link.Roles[1].buildDiscountPercent);
            Assert.AreEqual(1, link.Roles[0].maxPerSession);
            Assert.AreEqual(3, link.PingRules.burst);
            Assert.AreEqual(8, link.PingRules.lifetimeSeconds);
            Assert.AreEqual(1, link.PingRules.routeOverlay);
            Assert.IsFalse(link.PingBlocked);
            Assert.AreEqual(1, link.Snapshot.players.Count);
            Assert.IsTrue(link.Snapshot.IsLobby);
            Assert.AreEqual(20, link.Snapshot.gateMaxHp);
            Assert.AreEqual(1, link.Snapshot.waveCount, "짧은 구역: 웨이브 한 번");
            Assert.IsNull(link.Snapshot.players[0].role, "역할을 고르지 않았다");

            // 팀원이 들어온다 (이쪽은 다른 클라이언트).
            TestClient mate = await arena.JoinAsync(session, 1);
            await me.Until(() => me.Events.Any(e => e.type == "player_joined" && e.pid == mate.Pid), "팀원 입장 알림");
            await me.Until(() => link.Snapshot.players.Count == 2, "팀원이 보인다");
            Assert.AreEqual("대원2", link.Snapshot.FindPlayer(mate.Pid).name);

            // 역할을 고르고, 시작한다.
            link.SetRole(CoopRoleCodes.Builder);
            await me.Until(() => link.Snapshot.FindPlayer(link.MyPid).role == CoopRoleCodes.Builder, "역할");
            Assert.IsTrue(me.Events.Any(e => e.type == CoopEventTypes.RoleChanged && e.pid == link.MyPid && e.code == CoopRoleCodes.Builder));
            link.SetRole("king");
            await me.Until(() => me.Results.Any(r => r.action == "set_role" && r.reject == CoopRejects.BadRole), "없는 역할");
            link.Start();
            await me.Until(() => link.State == CoopLinkStates.Battle, "전투 시작");
            Assert.IsTrue(me.Events.Any(e => e.type == CoopEventTypes.Started && e.value == 2));
            Assert.AreEqual(300, link.Snapshot.energy);

            // 타워를 세운다: 답은 요청 번호로 온다. 설치 담당이라 10% 싸다.
            string request = link.Build(15, "twr_machine_gun");
            Assert.IsNotNull(request);
            await me.Until(() => me.Results.Any(r => r.requestId == request), "설치의 답");
            CoopActionResult built = me.Results.Single(r => r.requestId == request);
            Assert.IsTrue(built.ok);
            Assert.IsNull(built.reject);
            Assert.AreEqual(210, built.energy);
            await me.Until(() => link.Snapshot.FindTower(15) != null, "타워가 보인다");
            Assert.AreEqual(link.MyPid, link.Snapshot.FindTower(15).owner);
            Assert.AreEqual(8f, link.Snapshot.FindTower(15).range, 0.001f);
            CoopEvent builtEvent = me.Events.Single(e => e.type == CoopEventTypes.TowerBuilt);
            Assert.AreEqual(15, builtEvent.slot);
            Assert.AreEqual("twr_machine_gun", builtEvent.code);
            // 같은 자리에 또 세울 수 없다. 팀원이 세워도 마찬가지다.
            string again = link.Build(15, "twr_machine_gun");
            await me.Until(() => me.Results.Any(r => r.requestId == again), "두 번째 설치의 답");
            Assert.AreEqual(CoopRejects.SlotTaken, me.Results.Single(r => r.requestId == again).reject);
            Assert.AreEqual(210, me.Results.Single(r => r.requestId == again).energy);
            Assert.AreEqual("다른 대원이 먼저 세웠어요.", CoopTexts.Reject(CoopRejects.SlotTaken));

            // 움직인다: 서버가 정한 위치가 온다.
            link.Move(-3f, -8f);
            await me.Until(() => Math.Abs(link.Snapshot.FindPlayer(link.MyPid).z - -8f) < 0.01f, "움직임");
            Assert.AreEqual(-3f, link.Snapshot.FindPlayer(link.MyPid).x, 0.01f);
            Assert.AreEqual(-8f, (await mate.StateAsync(s => Math.Abs(s.Player(link.MyPid).Num("z") - -8f) < 0.01f, "팀원이 본 내 위치")).Player(link.MyPid).Num("z"), 0.01f);

            // 핑을 주고받는다.
            link.Ping(PingKinds.Rally, 1f, 2f);
            await me.Until(() => me.Events.Any(e => e.type == CoopEventTypes.Ping && e.pid == link.MyPid), "내 핑");
            CoopEvent myPing = me.Events.First(e => e.type == CoopEventTypes.Ping);
            Assert.AreEqual(PingKinds.Rally, myPing.code);
            Assert.AreEqual(1f, myPing.x, 0.001f);
            Assert.AreEqual(2f, myPing.z, 0.001f);
            Assert.AreEqual(-1, myPing.slot, "자리가 없는 알림");
            await mate.SeenAsync(m => m.IsEvent("ping") && m.Str("pid") == link.MyPid, "팀원이 받은 핑");
            await me.Until(() => link.Snapshot.pings.Any(p => p.kind == PingKinds.Rally && p.leftMs > 0), "지도의 핑");
            await mate.SendAsync("{\"type\":\"ping\",\"kind\":\"repair_here\",\"x\":-4,\"z\":-13}");
            await me.Until(() => me.Events.Any(e => e.type == CoopEventTypes.Ping && e.pid == mate.Pid), "팀원의 핑");
            await me.Until(() => link.Snapshot.pings.Any(p => p.pid == mate.Pid), "지도에 보이는 팀원의 핑");
            link.Ping("insult", 0f, 0f);
            await me.Until(() => me.Results.Any(r => r.action == "ping" && r.reject == CoopRejects.PingKind), "모르는 핑");

            // 전술 미니맵: 받은 상태로 그릴 것을 만든다.
            MinimapView corner = Minimap.Build(link.Snapshot.ToMinimap(link.Map, link.MyPid), new MinimapOptions(), link.PingRules);
            Assert.AreEqual(1, corner.blips.Count(b => b.kind == MinimapBlipKinds.Self));
            Assert.AreEqual(1, corner.blips.Count(b => b.kind == MinimapBlipKinds.Teammate));
            Assert.AreEqual(1, corner.blips.Count(b => b.kind == MinimapBlipKinds.Tower));
            Assert.AreEqual(1, corner.blips.Count(b => b.kind == MinimapBlipKinds.Gate));
            Assert.IsTrue(corner.blips.Any(b => b.kind == MinimapBlipKinds.SupportRequest), "수리 요청은 긴급 지원 요청으로 보인다");
            Assert.AreEqual(0, corner.route.Count);
            MinimapView full = Minimap.Build(link.Snapshot.ToMinimap(link.Map, link.MyPid), new MinimapOptions { expanded = true }, link.PingRules);
            Assert.AreEqual(8, full.route.Count, "전술 지도에는 적 이동 경로가 있다");
            Assert.Greater(full.blips.Single(b => b.kind == MinimapBlipKinds.Tower).radius, 0f, "타워 사거리");

            // 팀원의 핑을 숨긴다.
            link.HidePings(mate.Pid, true);
            await me.Until(() => link.IsHidden(mate.Pid), "숨김");
            await me.Until(() => !link.Snapshot.pings.Any(p => p.pid == mate.Pid), "숨긴 핑은 지도에서 빠진다");

            // 적이 오고, 전투가 끝난다.
            await me.Until(() => link.Snapshot.enemies.Count > 0 || link.Finish != null, "적이 보인다");
            if (link.Snapshot.enemies.Count > 0)
            {
                CoopEnemyView enemy = link.Snapshot.enemies[0];
                Assert.AreEqual("mech_insect", enemy.code);
                Assert.Greater(enemy.maxHp, 0);
                Assert.AreEqual(1f, Math.Abs(enemy.dx) + Math.Abs(enemy.dz), 0.01f, "방향이 온다");
                link.Focus(enemy.id);
            }
            await me.Until(() => link.Finish != null && link.Finish.recordKnown, "결과", 20000);
            Assert.AreEqual(CoopLinkStates.Finished, link.State);
            Assert.IsTrue(link.Finish.Won);
            Assert.IsFalse(link.Finish.practice);
            Assert.AreEqual(1, link.Finish.wavesCleared);
            Assert.AreEqual(CoopCloseReasons.Completed, link.Finish.closeReason);
            Assert.IsTrue(link.Finish.recorded);
            Assert.IsTrue(link.Finish.accepted);
            Assert.IsFalse(link.Finish.pending);
            Assert.Greater(link.Finish.durationSeconds, 0);
            Assert.IsTrue(link.Snapshot.IsFinished);
            Assert.IsTrue(me.Events.Any(e => e.type == CoopEventTypes.WaveStarted));

            // 서버가 연결을 닫아도 결과 화면은 그대로다.
            await me.Until(() => me.Socket.IsClosed, "연결이 닫힌다");
            me.Pump();
            Assert.AreEqual(CoopLinkStates.Finished, link.State);
            Assert.IsNull(link.ClosedReason);

            // 게임 서버가 받은 결과에 이 클라이언트가 한 일이 들어 있다.
            Assert.IsTrue(session.Accepted);
            JsonElement mine = session.Report.List("members").Single(m => m.Str("pid") == link.MyPid);
            Assert.AreEqual("builder", mine.Str("role"));
            Assert.AreEqual(1, mine.Int("towers_built"));
            Assert.GreaterOrEqual(mine.Int("pings_sent"), 1);
            Assert.AreEqual(0, arena.Game.BadSignatures);
        }

        [Test]
        public async Task GameClient_IsToldWhyItCannotJoin()
        {
            await using Arena arena = await Arena.StartAsync();
            FakeSession session = arena.Game.NewSession(2);

            // 만료된 입장권.
            using (Player late = Connect(arena, session, 0, t => t["exp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1))
            {
                await late.Until(() => late.Link.State == CoopLinkStates.Closed, "거절");
                Assert.AreEqual("bad_ticket", late.Link.ClosedReason);
                Assert.AreEqual("입장권이 만료되었어요. 다시 들어와 주세요.", CoopTexts.Closed(late.Link.ClosedReason));
                Assert.IsNull(late.Link.Snapshot);
                // 닫힌 뒤의 요청은 조용히 버려진다.
                late.Link.Start();
                Assert.IsNull(late.Link.Build(1, "twr_machine_gun"));
            }

            // 끝난 판.
            FakeSession over = arena.Game.NewSession(2);
            over.Status = "finished";
            using (Player gone = Connect(arena, over, 0))
            {
                await gone.Until(() => gone.Link.State == CoopLinkStates.Closed, "거절");
                Assert.AreEqual("session_closed", gone.Link.ClosedReason);
            }

            // 닿지 않는 서버.
            using (var nowhere = new Player { Socket = new WebSocketCoopSocket("ws://127.0.0.1:9/ws") })
            {
                nowhere.Link = new RemoteCoopLink(nowhere.Socket, arena.Game.Ticket(session, 0));
                await nowhere.Until(() => nowhere.Link.State == CoopLinkStates.Closed, "연결 실패", 15000);
                Assert.AreEqual("unreachable", nowhere.Link.ClosedReason);
            }
        }

        [Test]
        public async Task GameClient_NoticesWhenTheRoomIsClosed_AndCanLeave()
        {
            await using Arena arena = await Arena.StartAsync();
            FakeSession session = arena.Game.NewSession(3);
            using Player me = Connect(arena, session, 0);
            using Player other = Connect(arena, session, 1);
            await me.Until(() => me.Link.State == CoopLinkStates.Lobby, "대기실");
            await other.Until(() => other.Link.State == CoopLinkStates.Lobby, "대기실");

            // 스스로 나간다: 남은 사람에게 알림이 간다.
            other.Link.Leave();
            Assert.AreEqual(CoopLinkStates.Closed, other.Link.State);
            Assert.AreEqual("left", other.Link.ClosedReason);
            await me.Until(() => me.Events.Any(e => e.type == "player_left" && e.pid == other.Link.MyPid), "팀원이 나감");
            await me.Until(() => !me.Link.Snapshot.FindPlayer(other.Link.MyPid).connected, "끊긴 것으로 보인다");

            // 같은 계정이 다른 기기에서 들어오면 이쪽은 끊긴다.
            using Player second = Connect(arena, session, 0);
            await second.Until(() => second.Link.State == CoopLinkStates.Lobby, "다른 기기");
            await me.Until(() => me.Link.State == CoopLinkStates.Closed, "먼저 있던 연결이 끊긴다");
            Assert.AreEqual("replaced", me.Link.ClosedReason);

            // 서버가 내려간다.
            await arena.StopAsync();
            await second.Until(() => second.Link.State == CoopLinkStates.Closed, "서버 종료");
            Assert.AreEqual("shutdown", second.Link.ClosedReason);
            Assert.AreEqual("서버 점검으로 방이 닫혔어요.", CoopTexts.Closed(second.Link.ClosedReason));
        }

        [Test]
        public void GameClient_OnlyConnectsOverSecureAddresses()
        {
            Assert.IsNull(WebSocketCoopSocket.UrlProblem("wss://rt.cityguardians.example/ws"));
            Assert.IsNull(WebSocketCoopSocket.UrlProblem("ws://127.0.0.1:8090/ws"), "기기 안의 개발 서버");
            Assert.IsNull(WebSocketCoopSocket.UrlProblem("ws://localhost:8090/ws"));
            Assert.IsNotNull(WebSocketCoopSocket.UrlProblem("ws://rt.cityguardians.example/ws"), "평문 연결");
            Assert.IsNotNull(WebSocketCoopSocket.UrlProblem("http://rt.cityguardians.example/ws"));
            Assert.IsNotNull(WebSocketCoopSocket.UrlProblem("wss://rt.cityguardians.example/ws?ticket=abc"), "입장권을 주소에 넣지 않는다");
            Assert.IsNotNull(WebSocketCoopSocket.UrlProblem("wss://user:pw@rt.cityguardians.example/ws"));
            Assert.IsNotNull(WebSocketCoopSocket.UrlProblem("rt.cityguardians.example/ws"));
            Assert.IsNotNull(WebSocketCoopSocket.UrlProblem(""));
            Assert.IsNotNull(WebSocketCoopSocket.UrlProblem(null));

            using (var socket = new WebSocketCoopSocket("ws://rt.cityguardians.example/ws"))
            {
                Assert.IsTrue(socket.IsClosed);
                Assert.IsFalse(socket.IsOpen);
                Assert.AreEqual("bad_url", socket.CloseReason);
                var link = new RemoteCoopLink(socket, "ticket");
                link.Update(0.1f);
                Assert.AreEqual(CoopLinkStates.Closed, link.State);
                Assert.AreEqual("bad_url", link.ClosedReason);
            }
        }
    }
}
