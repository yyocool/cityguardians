using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using CityGuardians.Domain;
using Microsoft.Extensions.Logging;

namespace CityGuardians.Realtime
{
    public abstract class RoomCommand
    {
    }

    public sealed class JoinCommand : RoomCommand
    {
        public Ticket Ticket;
        public Connection Connection;
        /// <summary>null 이면 들어왔다. 아니면 거절한 이유.</summary>
        public readonly TaskCompletionSource<string> Done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed class LeaveCommand : RoomCommand
    {
        public string Pid;
        public Connection Connection;
    }

    public sealed class ClientCommand : RoomCommand
    {
        public string Pid;
        public Connection Connection;
        public ClientMessage Message;
    }

    public sealed class StopCommand : RoomCommand
    {
    }

    /// <summary>
    /// 협동 방어전 한 판의 방 (docs/COOP_DEFENSE_SYSTEM.md 4장).
    ///
    /// 방마다 일꾼이 하나 있고, 그 일꾼만 시뮬레이션을 만진다. 플레이어의 명령은 줄(inbox)에 들어와 한 번에 하나씩 처리된다.
    /// 그래서 두 사람이 같은 자리에 동시에 타워를 세우려 해도, 같은 순간에 공동 예산을 쓰려 해도 순서가 정해지고 한 번만 처리된다.
    /// 전투 시계는 실제 시간보다 빨리 가지 않는다 (밀린 틱은 버린다).
    /// </summary>
    public sealed class Room
    {
        private const int MaxCatchUpTicks = 5;

        private readonly RoomConfig config;
        private readonly CoopSim sim;
        private readonly RoomManager manager;
        private readonly IGameApi api;
        private readonly RealtimeOptions options;
        private readonly ILogger log;
        private readonly Channel<RoomCommand> inbox = Channel.CreateBounded<RoomCommand>(
            new BoundedChannelOptions(4096) { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
        private readonly SemaphoreSlim signal = new SemaphoreSlim(0);
        private readonly Dictionary<string, Connection> connections = new Dictionary<string, Connection>();
        private readonly Stopwatch watch = Stopwatch.StartNew();
        private readonly CancellationTokenSource stopping = new CancellationTokenSource();
        private long startedAtMs = -1;
        private long emptySinceMs;
        private long lastCheckMs;
        private Task<ApiReply> check;
        private bool done;
        private volatile bool stopRequested;

        public string Id => config.SessionId;
        public Task Completion { get; private set; } = Task.CompletedTask;
        /// <summary>지금 연결된 사람 수 (상태 확인용. 정확한 값은 방 일꾼만 안다).</summary>
        public int ConnectionCount => Volatile.Read(ref connectionCount);
        private int connectionCount;

        public Room(RoomConfig config, RoomManager manager, IGameApi api, RealtimeOptions options, ILogger log)
        {
            this.config = config;
            this.manager = manager;
            this.api = api;
            this.options = options;
            this.log = log;
            sim = new CoopSim(config.Sim);
        }

        public void Start()
        {
            Completion = Task.Run(RunAsync);
        }

        // ── 밖에서 부르는 것 (어느 스레드에서든) ────────────

        /// <summary>명령을 줄에 넣는다. 방이 닫혔거나 줄이 꽉 찼으면 false.</summary>
        public bool Post(RoomCommand command)
        {
            if (!inbox.Writer.TryWrite(command)) return false;
            if (signal.CurrentCount == 0) signal.Release();
            return true;
        }

        /// <summary>입장권을 확인한 연결을 방에 넣는다. null 이면 들어왔다. 아니면 거절한 이유.</summary>
        public async Task<string> JoinAsync(Ticket ticket, Connection connection)
        {
            var command = new JoinCommand { Ticket = ticket, Connection = connection };
            if (!Post(command)) return "session_closed";
            Task finished = await Task.WhenAny(command.Done.Task, Task.Delay(15000, connection.Closed)).ConfigureAwait(false);
            return finished == command.Done.Task ? command.Done.Task.Result : "busy";
        }

        /// <summary>서버가 내려갈 때. 전투 중이면 그때까지의 결과를 보고하고 닫는다.</summary>
        public void RequestStop()
        {
            if (!Post(new StopCommand()))
            {
                stopRequested = true;
                if (signal.CurrentCount == 0) signal.Release();
            }
        }

        // ── 방 일꾼 ─────────────────────────────────────────

        private async Task RunAsync()
        {
            try
            {
                int tickMs = Math.Max(1, options.TickMilliseconds);
                long nextTickAt = watch.ElapsedMilliseconds + tickMs;
                while (!done)
                {
                    while (!done && inbox.Reader.TryRead(out RoomCommand command)) await HandleAsync(command).ConfigureAwait(false);
                    if (stopRequested && !done) await StopAsync().ConfigureAwait(false);
                    if (done) break;

                    long now = watch.ElapsedMilliseconds;
                    int steps = 0;
                    while (now >= nextTickAt && steps < MaxCatchUpTicks && !sim.IsFinished)
                    {
                        StepOnce();
                        nextTickAt += tickMs;
                        steps++;
                    }
                    // 밀린 틱은 버린다: 전투 시계가 실제 시간을 앞지르지 않는다.
                    if (now >= nextTickAt) nextTickAt = now + tickMs;

                    if (sim.IsFinished)
                    {
                        await FinishAsync(false).ConfigureAwait(false);
                        break;
                    }
                    if (steps > 0) Housekeeping(now);
                    if (done) break;
                    int wait = (int)Math.Max(1, Math.Min(tickMs, nextTickAt - watch.ElapsedMilliseconds));
                    await signal.WaitAsync(wait).ConfigureAwait(false);
                }
            }
            catch (Exception error)
            {
                // 방 하나의 문제로 서버 전체가 내려가지 않게 한다. 내용은 기록에만 남긴다.
                log.LogError(error, "방 {Session} 이 오류로 닫힙니다.", Id);
            }
            finally
            {
                done = true;
                inbox.Writer.TryComplete();
                while (inbox.Reader.TryRead(out RoomCommand left))
                    if (left is JoinCommand join) join.Done.TrySetResult("session_closed");
                foreach (Connection connection in connections.Values) connection.Close(WebSocketCloseStatus.NormalClosure, "closed");
                connections.Clear();
                Volatile.Write(ref connectionCount, 0);
                stopping.Cancel();
                manager.Forget(this);
            }
        }

        private void StepOnce()
        {
            sim.Step();
            FlushEvents();
            if (sim.Tick % Math.Max(1, options.SnapshotEveryTicks) == 0) BroadcastState();
        }

        private void Send(Connection connection, object message) => connection.Send(Wire.Bytes(message));

        private void Broadcast(object message)
        {
            if (connections.Count == 0) return;
            byte[] bytes = Wire.Bytes(message);
            foreach (Connection connection in connections.Values) connection.Send(bytes);
        }

        /// <summary>시뮬레이션에 쌓인 알림을 모두에게 보낸다. 핑은 그 사람의 핑을 숨긴 팀원에게는 보내지 않는다.</summary>
        private void FlushEvents()
        {
            List<CoopEvent> events = sim.DrainEvents();
            for (int i = 0; i < events.Count; i++)
            {
                CoopEvent e = events[i];
                if (connections.Count == 0) continue;
                byte[] bytes = Wire.Bytes(Messages.Event(e));
                foreach (var pair in connections)
                {
                    if (e.type == CoopEventTypes.Ping && sim.Pings.IsHidden(pair.Key, e.pid)) continue;
                    pair.Value.Send(bytes);
                }
            }
        }

        private object StateFor(string pid, out bool personal)
        {
            List<TacticalPing> visible = sim.Pings.VisibleTo(pid);
            personal = visible.Count != sim.Pings.Active.Count;
            return Messages.State(sim, Messages.Pings(visible, sim.Clock));
        }

        private void BroadcastState()
        {
            if (connections.Count == 0) return;
            byte[] common = null;
            foreach (var pair in connections)
            {
                // 누군가의 핑을 숨긴 사람에게는 그 핑을 뺀 상태를 따로 만들어 보낸다.
                if (sim.Pings.VisibleTo(pair.Key).Count != sim.Pings.Active.Count)
                {
                    pair.Value.Send(Wire.Bytes(StateFor(pair.Key, out _)));
                    continue;
                }
                if (common == null) common = Wire.Bytes(Messages.State(sim, Messages.Pings(sim.Pings.Active, sim.Clock)));
                pair.Value.Send(common);
            }
        }

        // ── 명령 ────────────────────────────────────────────

        private async Task HandleAsync(RoomCommand command)
        {
            switch (command)
            {
                case JoinCommand join:
                    HandleJoin(join);
                    break;
                case LeaveCommand leave:
                    HandleLeave(leave);
                    break;
                case ClientCommand client:
                    // 이미 다른 연결로 바뀐 사람의 옛 연결에서 온 명령은 버린다.
                    if (!connections.TryGetValue(client.Pid, out Connection current) || current != client.Connection) break;
                    if (client.Message.Type == ClientMessage.Start) await HandleStartAsync(client).ConfigureAwait(false);
                    else HandleClient(client);
                    FlushEvents();
                    break;
                case StopCommand _:
                    await StopAsync().ConfigureAwait(false);
                    break;
            }
        }

        private void HandleJoin(JoinCommand join)
        {
            Ticket ticket = join.Ticket;
            if (join.Connection.IsClosing)
            {
                join.Done.TrySetResult("gone");
                return;
            }
            CoopOutcome outcome = sim.Join(ticket.Pid, ticket.Name, ticket.Role, ticket.Level, ticket.Host, ticket.Structs, ticket.PingBlocked);
            if (!outcome.Ok)
            {
                join.Done.TrySetResult(outcome.reject);
                return;
            }
            // 같은 사람이 다시 들어오면 (다시 연결, 다른 기기) 옛 연결을 끊는다. 한 사람은 한 연결만 가진다.
            if (connections.TryGetValue(ticket.Pid, out Connection old) && old != join.Connection)
                old.Close(WebSocketCloseStatus.PolicyViolation, "replaced");
            connections[ticket.Pid] = join.Connection;
            Volatile.Write(ref connectionCount, connections.Count);
            join.Connection.Pid = ticket.Pid;
            CoopPlayerState player = sim.FindPlayer(ticket.Pid);
            Send(join.Connection, Messages.Welcome(ticket, config, sim, options, StateFor(ticket.Pid, out _)));
            Broadcast(Messages.PlayerEvent("player_joined", player));
            FlushEvents();
            join.Done.TrySetResult(null);
            log.LogInformation("방 {Session}: {Pid} 입장 ({Count}명 연결)", Id, ticket.Pid, connections.Count);
        }

        private void HandleLeave(LeaveCommand leave)
        {
            if (!connections.TryGetValue(leave.Pid, out Connection current) || current != leave.Connection) return;
            connections.Remove(leave.Pid);
            Volatile.Write(ref connectionCount, connections.Count);
            sim.Disconnect(leave.Pid);
            CoopPlayerState player = sim.FindPlayer(leave.Pid);
            if (player != null) Broadcast(Messages.PlayerEvent("player_left", player));
            if (connections.Count == 0) emptySinceMs = watch.ElapsedMilliseconds;
            log.LogInformation("방 {Session}: {Pid} 연결 끊김 ({Count}명 연결)", Id, leave.Pid, connections.Count);
        }

        private void HandleClient(ClientCommand command)
        {
            ClientMessage m = command.Message;
            string pid = command.Pid;
            switch (m.Type)
            {
                case ClientMessage.Move:
                    sim.Move(pid, m.X, m.Z);
                    break;
                case ClientMessage.Focus:
                    sim.Focus(pid, m.EnemyId);
                    break;
                case ClientMessage.Build:
                    Send(command.Connection, Messages.Result(m.Type, m.RequestId, sim.Build(pid, m.RequestId, m.Slot, m.Code), sim.Budget.Balance));
                    break;
                case ClientMessage.Upgrade:
                    Send(command.Connection, Messages.Result(m.Type, m.RequestId, sim.Upgrade(pid, m.RequestId, m.Slot), sim.Budget.Balance));
                    break;
                case ClientMessage.Repair:
                    Send(command.Connection, Messages.Result(m.Type, m.RequestId, sim.Repair(pid, m.RequestId, m.Slot), sim.Budget.Balance));
                    break;
                case ClientMessage.Ping:
                {
                    CoopOutcome outcome = sim.Ping(pid, m.Kind, m.X, m.Z);
                    if (!outcome.Ok) Send(command.Connection, Messages.Reject(m.Type, outcome.reject));
                    break;
                }
                case ClientMessage.HidePings:
                    if (sim.FindPlayer(m.Pid) == null || m.Pid == pid)
                    {
                        Send(command.Connection, Messages.Reject(m.Type, CoopRejects.BadRequest));
                        break;
                    }
                    sim.Pings.Hide(pid, m.Pid, m.Hide);
                    Send(command.Connection, new { type = "hidden", pid = m.Pid, hide = m.Hide });
                    break;
                case ClientMessage.SetRole:
                {
                    CoopOutcome outcome = sim.SetRole(pid, m.Role);
                    if (!outcome.Ok) Send(command.Connection, Messages.Reject(m.Type, outcome.reject));
                    break;
                }
            }
        }

        /// <summary>
        /// 방장이 시작을 눌렀다. 게임 서버에 먼저 알리고 (판의 상태가 running 이 되고 시작 시각이 기록된다) 그 뒤에 전투를 시작한다.
        /// 걸린 시간은 게임 서버가 이때부터 잰다.
        /// </summary>
        private async Task HandleStartAsync(ClientCommand command)
        {
            CoopPlayerState player = sim.FindPlayer(command.Pid);
            string reject = null;
            if (player == null) reject = CoopRejects.NotMember;
            else if (sim.Phase != CoopPhase.Lobby) reject = CoopRejects.NotLobby;
            else if (!player.host) reject = CoopRejects.NotHost;
            else if (sim.ConnectedCount < Math.Max(1, sim.Config.rules.minPlayersToStart)) reject = CoopRejects.NotEnoughPlayers;
            if (reject == null)
            {
                var present = new List<string>();
                foreach (CoopPlayerState p in sim.Players)
                    if (p.connected && !p.npc) present.Add(p.pid);
                ApiReply reply = await api.StartAsync(Id, command.Pid, present, stopping.Token).ConfigureAwait(false);
                if (!reply.Ok)
                {
                    reject = reply.Reason ?? "unavailable";
                    log.LogWarning("방 {Session}: 게임 서버가 시작을 받지 않음 ({Status} {Reason})", Id, reply.Status, reply.Reason);
                }
                else
                {
                    CoopOutcome outcome = sim.Start(command.Pid);
                    if (!outcome.Ok)
                    {
                        // 게임 서버는 시작했다고 기록했는데 여기에서는 시작하지 못했다. 그 판을 닫아 둔다.
                        log.LogError("방 {Session}: 시작하지 못함 ({Reason})", Id, outcome.reject);
                        await manager.ReportAsync(Id, RoomManager.InterruptedReport(config), false).ConfigureAwait(false);
                        Broadcast(new { type = "closed", reason = "session_interrupted" });
                        done = true;
                        return;
                    }
                    startedAtMs = watch.ElapsedMilliseconds;
                    log.LogInformation("방 {Session}: 전투 시작 ({Count}명)", Id, sim.PlayersAtStart);
                    FlushEvents();
                    BroadcastState();
                }
            }
            if (reject != null) Send(command.Connection, Messages.Reject(ClientMessage.Start, reject));
        }

        // ── 살림 ────────────────────────────────────────────

        private void Housekeeping(long now)
        {
            if (sim.Phase == CoopPhase.Lobby)
            {
                if (connections.Count == 0 && now - emptySinceMs >= options.EmptyLobbySeconds * 1000L)
                {
                    // 대기실에 아무도 없다. 방만 치운다 (게임 서버의 판은 그쪽에서 시간이 지나면 닫힌다).
                    done = true;
                    return;
                }
                if (now >= config.ExpiresInSeconds * 1000L)
                {
                    Broadcast(new { type = "closed", reason = "lobby_timeout" });
                    done = true;
                    return;
                }
            }
            else if (sim.IsRunning && startedAtMs >= 0 && now - startedAtMs >= (config.SessionMaxMinutes * 60L - 30L) * 1000L)
            {
                // 게임 서버가 시간 초과로 닫기 전에 여기에서 먼저 끝내고 결과를 보낸다.
                sim.Abort(CoopCloseReasons.Timeout);
                return;
            }

            // 게임 서버에서 판이 닫혔는지 (관리자가 닫음, 방장이 취소함) 가끔 확인한다. 기다리지 않는다: 전투가 멈추면 안 된다.
            if (check != null && check.IsCompleted)
            {
                Task<ApiReply> finished = check;
                check = null;
                if (finished.Status == TaskStatus.RanToCompletion)
                {
                    ApiReply reply = finished.Result;
                    string status = reply.Ok && reply.Data.ValueKind == System.Text.Json.JsonValueKind.Object
                        && reply.Data.TryGetProperty("session", out var session) && session.TryGetProperty("status", out var value)
                        ? value.GetString() : null;
                    if (reply.Status == 404 || (status != null && status != "lobby" && status != "running"))
                    {
                        log.LogInformation("방 {Session}: 게임 서버에서 닫힌 판 ({Status})", Id, status ?? "없음");
                        Broadcast(new { type = "closed", reason = "session_closed" });
                        done = true;
                        return;
                    }
                }
            }
            if (check == null && now - lastCheckMs >= options.SessionCheckSeconds * 1000L)
            {
                lastCheckMs = now;
                check = api.ConfigAsync(Id, stopping.Token);
            }
        }

        /// <summary>판이 끝났다. 모두에게 알리고, 게임 서버에 결과를 보고한 뒤 방을 닫는다.</summary>
        private async Task FinishAsync(bool quick)
        {
            FlushEvents();
            BroadcastState();
            CoopReport report = sim.BuildReport();
            Broadcast(new
            {
                type = "finished", report.result, report.wavesCleared, report.gateHpLeft, report.durationSeconds, report.closeReason,
            });
            log.LogInformation("방 {Session}: 전투 끝 ({Result}, 웨이브 {Waves}, {Reason})", Id, report.result, report.wavesCleared, report.closeReason);
            ApiReply reply = await manager.ReportAsync(Id, report, quick).ConfigureAwait(false);
            bool accepted = reply.Ok && reply.Data.ValueKind == System.Text.Json.JsonValueKind.Object
                && reply.Data.TryGetProperty("accepted", out var flag) && flag.ValueKind == System.Text.Json.JsonValueKind.True;
            // 보상은 여기에서 알려 주지 않는다. 클라이언트가 게임 서버의 GET /coop/sessions/{id}/result 로 확인한다.
            Broadcast(new { type = "recorded", ok = reply.Ok, accepted, pending = reply.Retryable });
            done = true;
        }

        private async Task StopAsync()
        {
            stopRequested = false;
            if (sim.IsRunning)
            {
                sim.Abort(CoopCloseReasons.Shutdown);
                await FinishAsync(true).ConfigureAwait(false);
                return;
            }
            Broadcast(new { type = "closed", reason = "shutdown" });
            done = true;
        }
    }
}
