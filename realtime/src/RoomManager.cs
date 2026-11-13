using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CityGuardians.Domain;
using Microsoft.Extensions.Logging;

namespace CityGuardians.Realtime
{
    /// <summary>
    /// 방의 목록. 판(session)마다 방이 하나다. 방은 이 서버의 메모리에만 있다 (실시간 서버를 한 대만 돌리는 구성).
    /// </summary>
    public sealed class RoomManager
    {
        private sealed class Slot
        {
            public Room Room;
            public string Error;
        }

        private sealed class Pending
        {
            public CoopReport Report;
            public DateTime GiveUpAt;
        }

        /// <summary>결과를 받지 못한 판은 이 시간 동안 계속 다시 보낸다.</summary>
        private static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(60);

        private readonly ConcurrentDictionary<string, Lazy<Task<Slot>>> rooms = new ConcurrentDictionary<string, Lazy<Task<Slot>>>();
        private readonly ConcurrentDictionary<string, Pending> pending = new ConcurrentDictionary<string, Pending>();
        private readonly IGameApi api;
        private readonly RealtimeOptions options;
        private readonly ILoggerFactory loggers;
        private readonly ILogger log;
        private readonly CancellationTokenSource stopping = new CancellationTokenSource();
        private readonly object pendingGate = new object();
        private Task pendingLoop;
        private volatile bool stopped;

        public RoomManager(IGameApi api, RealtimeOptions options, ILoggerFactory loggers)
        {
            this.api = api;
            this.options = options;
            this.loggers = loggers;
            log = loggers.CreateLogger("CG.Realtime.Rooms");
        }

        public int RoomCount => rooms.Count;
        public int PendingReports => pending.Count;

        /// <summary>그 판의 방을 찾는다. 없으면 게임 서버에서 설정을 받아 만든다. 못 만들면 이유를 돌려준다.</summary>
        public async Task<(Room room, string error)> FindOrCreateAsync(string sessionId)
        {
            if (stopped) return (null, "shutdown");
            // 결과를 아직 보고하지 못한 판은 끝난 판이다.
            if (pending.ContainsKey(sessionId)) return (null, "session_closed");
            if (!rooms.TryGetValue(sessionId, out Lazy<Task<Slot>> lazy))
            {
                if (rooms.Count >= options.MaxRooms) return (null, "server_full");
                lazy = rooms.GetOrAdd(sessionId, id => new Lazy<Task<Slot>>(() => CreateAsync(id)));
            }
            Slot slot;
            try
            {
                slot = await lazy.Value.ConfigureAwait(false);
            }
            catch (Exception error)
            {
                log.LogError(error, "방 {Session} 을 만들지 못했습니다.", sessionId);
                slot = new Slot { Error = "session_unavailable" };
            }
            if (slot.Room == null)
            {
                rooms.TryRemove(new KeyValuePair<string, Lazy<Task<Slot>>>(sessionId, lazy));
                return (null, slot.Error ?? "session_unavailable");
            }
            return (slot.Room, null);
        }

        private async Task<Slot> CreateAsync(string sessionId)
        {
            ApiReply reply = await api.ConfigAsync(sessionId, stopping.Token).ConfigureAwait(false);
            if (!reply.Ok)
            {
                if (reply.Status == 404)
                {
                    // 없는 판이거나, 서명이 맞지 않는다 (비밀값이 다르거나 두 서버의 시계가 1분 넘게 어긋났다).
                    log.LogWarning("게임 서버가 판 {Session} 의 설정을 주지 않았습니다 (404). 없는 판이 아니라면 REALTIME_SECRET 과 서버 시계를 확인하세요.",
                        sessionId);
                    return new Slot { Error = "session_not_found" };
                }
                log.LogWarning("게임 서버에서 판 {Session} 의 설정을 받지 못했습니다 ({Status} {Reason}).", sessionId, reply.Status, reply.Reason);
                return new Slot { Error = "session_unavailable" };
            }
            RoomConfig config = HttpGameApi.ReadConfig(reply.Data, sessionId);
            if (config == null)
            {
                log.LogError("판 {Session} 의 설정을 읽지 못했습니다.", sessionId);
                return new Slot { Error = "session_unavailable" };
            }
            if (config.Status == "running")
            {
                // 전투 중이라고 기록된 판인데 방이 없다: 실시간 서버가 전투 중에 다시 떴다. 그 판은 이어 갈 수 없으므로 닫아 둔다.
                if (options.CloseOrphans)
                {
                    log.LogWarning("판 {Session}: 방이 없는 전투 중 판을 닫습니다 (실시간 서버가 다시 시작됨).", sessionId);
                    await ReportAsync(sessionId, InterruptedReport(config), false).ConfigureAwait(false);
                }
                return new Slot { Error = "session_interrupted" };
            }
            if (config.Status != "lobby") return new Slot { Error = "session_closed" };
            if (stopped) return new Slot { Error = "shutdown" };
            var room = new Room(config, this, api, options, loggers.CreateLogger("CG.Realtime.Room"));
            room.Start();
            return new Slot { Room = room };
        }

        /// <summary>이어 갈 수 없게 된 판의 결과: 진 것으로, 걸린 시간 0 으로 보고한다 (보상도, 하루 횟수 차감도 없다).</summary>
        public static CoopReport InterruptedReport(RoomConfig config)
        {
            return new CoopReport
            {
                result = "lose", wavesCleared = 0, gateHpLeft = config.Sim.gateHp, durationSeconds = 0, closeReason = CoopCloseReasons.Shutdown,
            };
        }

        /// <summary>방이 닫힐 때 부른다.</summary>
        public void Forget(Room room)
        {
            if (rooms.TryGetValue(room.Id, out Lazy<Task<Slot>> lazy) && lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully
                && lazy.Value.Result.Room == room)
                rooms.TryRemove(new KeyValuePair<string, Lazy<Task<Slot>>>(room.Id, lazy));
        }

        // ── 결과 보고 ───────────────────────────────────────

        /// <summary>
        /// 결과를 게임 서버에 보고한다. 닿지 못하면 정해진 간격으로 다시 보낸다 (게임 서버는 같은 결과를 한 번만 반영한다).
        /// 끝내 닿지 못하면 뒤에서 계속 다시 보내도록 맡겨 두고 돌아온다.
        /// </summary>
        public async Task<ApiReply> ReportAsync(string sessionId, CoopReport report, bool quick)
        {
            ApiReply reply = null;
            int[] waits = quick ? new[] { 1 } : options.ResultRetrySeconds;
            for (int attempt = 0; attempt <= waits.Length; attempt++)
            {
                try
                {
                    reply = await api.ResultAsync(sessionId, report, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    log.LogError(error, "판 {Session} 의 결과를 보내다 오류가 났습니다.", sessionId);
                    reply = new ApiReply { Status = 0, Reason = "unreachable" };
                }
                if (!reply.Retryable) break;
                if (attempt < waits.Length)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(waits[attempt]), quick ? CancellationToken.None : stopping.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
            if (reply.Ok)
            {
                log.LogInformation("판 {Session}: 결과 반영됨", sessionId);
            }
            else if (reply.Retryable)
            {
                log.LogError("판 {Session}: 결과를 보고하지 못했습니다 ({Status} {Reason}). 계속 다시 보냅니다.", sessionId, reply.Status, reply.Reason);
                pending[sessionId] = new Pending { Report = report, GiveUpAt = DateTime.UtcNow + PendingLifetime };
                lock (pendingGate)
                {
                    if (pendingLoop == null || pendingLoop.IsCompleted) pendingLoop = Task.Run(RetryPendingAsync);
                }
            }
            else
            {
                log.LogWarning("판 {Session}: 게임 서버가 결과를 받지 않았습니다 ({Status} {Reason}).", sessionId, reply.Status, reply.Reason);
            }
            return reply;
        }

        private async Task RetryPendingAsync()
        {
            while (!pending.IsEmpty && !stopped)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), stopping.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                await FlushPendingAsync().ConfigureAwait(false);
            }
        }

        /// <summary>보고하지 못한 결과를 한 번씩 다시 보낸다.</summary>
        public async Task FlushPendingAsync()
        {
            foreach (var pair in pending.ToArray())
            {
                ApiReply reply;
                try
                {
                    reply = await api.ResultAsync(pair.Key, pair.Value.Report, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    reply = new ApiReply { Status = 0, Reason = "unreachable" };
                }
                if (!reply.Retryable)
                {
                    pending.TryRemove(pair.Key, out _);
                    log.LogInformation("판 {Session}: 미뤄 둔 결과 처리됨 ({Status} {Reason})", pair.Key, reply.Status, reply.Reason ?? "ok");
                }
                else if (DateTime.UtcNow >= pair.Value.GiveUpAt)
                {
                    pending.TryRemove(pair.Key, out _);
                    log.LogError("판 {Session}: 결과 보고를 포기합니다. 게임 서버에서 이 판은 시간 초과로 닫힙니다 (보상 없음).", pair.Key);
                }
            }
        }

        /// <summary>서버가 내려갈 때: 전투 중인 방은 그때까지의 결과를 보고하고, 모든 방을 닫는다.</summary>
        public async Task StopAsync(TimeSpan limit)
        {
            if (stopped) return;
            stopped = true;
            var waiting = new List<Task>();
            foreach (var pair in rooms.ToArray())
            {
                Lazy<Task<Slot>> lazy = pair.Value;
                if (!lazy.IsValueCreated || !lazy.Value.IsCompletedSuccessfully || lazy.Value.Result.Room == null) continue;
                Room room = lazy.Value.Result.Room;
                room.RequestStop();
                waiting.Add(room.Completion);
            }
            await Task.WhenAny(Task.WhenAll(waiting), Task.Delay(limit)).ConfigureAwait(false);
            // 미뤄 둔 결과가 있으면 마지막으로 한 번 더 보낸다.
            if (!pending.IsEmpty) await Task.WhenAny(FlushPendingAsync(), Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
            foreach (var pair in pending.ToArray())
                log.LogError("판 {Session}: 결과를 보고하지 못한 채 서버가 내려갑니다.", pair.Key);
            stopping.Cancel();
        }
    }
}
