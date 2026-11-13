using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CityGuardians.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace CityGuardians.Realtime.Tests
{
    public static class Json
    {
        public static string Str(this JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        public static int Int(this JsonElement e, string name) => e.GetProperty(name).GetInt32();

        public static float Num(this JsonElement e, string name) => e.GetProperty(name).GetSingle();

        public static bool Flag(this JsonElement e, string name) => e.GetProperty(name).GetBoolean();

        public static bool Is(this JsonElement e, string type) => e.Str("type") == type;

        public static bool IsEvent(this JsonElement e, string name) => e.Is("event") && e.Str("event") == name;

        public static JsonElement[] List(this JsonElement e, string name) => e.GetProperty(name).EnumerateArray().ToArray();

        public static JsonElement Player(this JsonElement state, string pid) => state.List("players").Single(p => p.Str("pid") == pid);
    }

    public static class Fixtures
    {
        /// <summary>게임 서버(PHP)의 테스트와 함께 보는 표본이 있는 곳 (server/tests/fixtures).</summary>
        public static string Dir
        {
            get
            {
                for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
                {
                    string candidate = Path.Combine(dir.FullName, "server", "tests", "fixtures");
                    if (File.Exists(Path.Combine(candidate, "coop-config.json"))) return candidate;
                }
                throw new FileNotFoundException("server/tests/fixtures/coop-config.json 을 찾지 못했습니다.");
            }
        }

        public static bool Updating => Environment.GetEnvironmentVariable("CG_UPDATE_FIXTURES") == "1";

        /// <summary>게임 서버가 실시간 서버에 주는 판의 설정 (PHP 의 테스트가 만든 것 그대로).</summary>
        public static JsonObject ConfigData() =>
            JsonNode.Parse(File.ReadAllText(Path.Combine(Dir, "coop-config.json"), Encoding.UTF8))["data"].AsObject();
    }

    public sealed class FakeSession
    {
        public string Id;
        public string Status = "lobby";
        public string HostPid;
        public List<string> Members = new List<string>();
        /// <summary>config 응답의 data.</summary>
        public JsonObject Data;
        public DateTime StartedAt;
        public int StartCalls;
        public int ResultCalls;
        public List<string> StartedWith = new List<string>();
        /// <summary>받아들였든 아니든, 반영한 결과의 본문.</summary>
        public JsonElement Report;
        public JsonObject Response;

        public int WaveCount => Data["zone"]["waves"].AsArray().Count;
        public string RejectReason => Response != null && Response["reject_reason"] != null ? Response["reject_reason"].GetValue<string>() : null;
        public bool Accepted => Response != null && Response["accepted"].GetValue<bool>();
    }

    /// <summary>
    /// 흉내 낸 게임 서버. 실시간 서버가 부르는 세 주소만 있다. 서명 확인과 결과 검증은 server/src 의 것을 따라 따로 구현했다
    /// (실시간 서버의 Signer 를 쓰지 않는다. 같은 코드로 확인하면 틀려도 모른다).
    /// </summary>
    public sealed class FakeGame : IAsyncDisposable
    {
        /// <summary>테스트에만 쓰는 값이다. 실제 비밀값이 아니다.</summary>
        public const string Secret = "unit-test-only-secret-000000000000000000";

        public readonly ConcurrentDictionary<string, FakeSession> Sessions = new ConcurrentDictionary<string, FakeSession>();
        public string Url;
        /// <summary>결과 요청을 이만큼 503 으로 돌려보낸다 (점검 중 흉내).</summary>
        public int FailResults;
        /// <summary>모든 요청을 503 으로 돌려보낸다.</summary>
        public volatile bool Down;
        public int BadSignatures;
        public int ConfigCalls;
        public int ResultAttempts;
        /// <summary>실시간 서버가 전투 시계를 몇 배로 돌리는가 (테스트에서만 빠르게 돌린다). 걸린 시간을 견줄 때 쓴다.</summary>
        public double TimeScale = 1.0;

        private WebApplication app;
        private int counter;

        private static byte[] Key(string purpose, string secret) => HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.ASCII.GetBytes(purpose));

        private static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        public static async Task<FakeGame> StartAsync()
        {
            var game = new FakeGame();
            WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            game.app = builder.Build();
            game.app.Map("/api/v1/internal/coop/sessions/{id}/{action}", game.HandleAsync);
            await game.app.StartAsync();
            game.Url = game.app.Urls.First() + "/api/v1";
            return game;
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }

        // ── 판과 입장권 ─────────────────────────────────────

        /// <summary>대기실을 하나 만든다. 설정은 PHP 가 만든 표본에서 시작한다.</summary>
        public FakeSession NewSession(int members, Action<JsonObject> tweak = null)
        {
            int n = Interlocked.Increment(ref counter);
            var session = new FakeSession { Id = "d_test" + n.ToString("D10"), Data = Fixtures.ConfigData() };
            for (int i = 0; i < members; i++) session.Members.Add("p_t" + n.ToString("D4") + "m" + i.ToString("D2") + "xxxxxx");
            session.HostPid = session.Members[0];
            JsonObject info = session.Data["session"].AsObject();
            info["id"] = session.Id;
            info["host_pid"] = session.HostPid;
            info["max_players"] = Math.Max(4, members);
            var list = new JsonArray();
            for (int i = 0; i < members; i++)
                list.Add(new JsonObject { ["pid"] = session.Members[i], ["name"] = "대원" + (i + 1), ["role"] = null, ["level"] = 1 });
            session.Data["members"] = list;
            tweak?.Invoke(session.Data);
            // 고친 값도 읽어 들인 값과 같은 모양이 되도록 다시 읽는다.
            session.Data = JsonNode.Parse(session.Data.ToJsonString()).AsObject();
            Sessions[session.Id] = session;
            return session;
        }

        /// <summary>짧은 구역: 기계 벌레 여섯 마리가 한 번 온다.</summary>
        public static void ShortZone(JsonObject data)
        {
            JsonObject zone = data["zone"].AsObject();
            zone["waves"] = JsonNode.Parse("[{\"groups\":[{\"enemy_code\":\"mech_insect\",\"count\":6,\"interval\":1,\"delay\":0}]}]");
            zone["min_seconds_per_wave"] = 5;
        }

        public static string[] AllStructures(JsonObject data) =>
            data["structures"].AsArray().Select(s => s["code"].GetValue<string>()).ToArray();

        /// <summary>입장권을 만든다. server/src/Infra/Signer.php 의 ticket() 과 같은 방법이다.</summary>
        public string Ticket(FakeSession session, int member, Action<JsonObject> tweak = null, string secret = Secret)
        {
            var payload = new JsonObject
            {
                ["v"] = 1, ["sid"] = session.Id, ["pid"] = session.Members[member], ["name"] = "대원" + (member + 1), ["role"] = null, ["level"] = 1,
                ["host"] = session.Members[member] == session.HostPid,
                ["structs"] = new JsonArray(AllStructures(session.Data).Select(code => (JsonNode)code).ToArray()),
                ["ping_blocked"] = false, ["exp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60,
                ["nonce"] = "n" + Guid.NewGuid().ToString("N").Substring(0, 11),
            };
            tweak?.Invoke(payload);
            return Sign(payload.ToJsonString(), secret);
        }

        public static string Sign(string payloadJson, string secret = Secret, string purpose = "cg-ticket-v1")
        {
            string body = B64(Encoding.UTF8.GetBytes(payloadJson));
            return body + "." + B64(HMACSHA256.HashData(Key(purpose, secret), Encoding.ASCII.GetBytes(body)));
        }

        // ── 실시간 서버가 부르는 주소 ───────────────────────

        private static IResult Reply(int status, JsonObject body) =>
            Results.Text(body.ToJsonString(), "application/json; charset=utf-8", Encoding.UTF8, status);

        private static IResult Ok(JsonNode data) => Reply(200, new JsonObject { ["ok"] = true, ["data"] = data, ["server_time"] = "2026-11-14T00:00:00Z" });

        private static IResult Error(int status, string code, string reason = null)
        {
            var error = new JsonObject { ["code"] = code, ["message"] = "오류" };
            if (reason != null) error["reason"] = reason;
            return Reply(status, new JsonObject { ["ok"] = false, ["error"] = error, ["server_time"] = "2026-11-14T00:00:00Z" });
        }

        /// <summary>server/src/Infra/Signer.php 의 verifyRequest() 와 같은 확인.</summary>
        private static bool Verify(string method, string path, byte[] body, string header)
        {
            Match m = Regex.Match(header ?? "", "^t=(\\d{1,12}),v1=([0-9a-f]{64})$");
            if (!m.Success) return false;
            long t = long.Parse(m.Groups[1].Value);
            if (Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - t) > 60) return false;
            string message = t + "\n" + method.ToUpperInvariant() + "\n" + path + "\n" + Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
            string mac = Convert.ToHexString(HMACSHA256.HashData(Key("cg-internal-v1", Secret), Encoding.UTF8.GetBytes(message))).ToLowerInvariant();
            return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(mac), Encoding.ASCII.GetBytes(m.Groups[2].Value));
        }

        private async Task<IResult> HandleAsync(HttpContext context, string id, string action)
        {
            byte[] body;
            using (var memory = new MemoryStream())
            {
                await context.Request.Body.CopyToAsync(memory);
                body = memory.ToArray();
            }
            string path = "/internal/coop/sessions/" + id + "/" + action;
            // 서명이 맞지 않으면 없는 주소처럼 답한다.
            if (!Verify(context.Request.Method, path, body, context.Request.Headers["X-CG-Signature"].ToString()))
            {
                Interlocked.Increment(ref BadSignatures);
                return Error(404, "NOT_FOUND");
            }
            if (action == "result") Interlocked.Increment(ref ResultAttempts);
            if (Down) return Error(503, "MAINTENANCE");
            if (!Sessions.TryGetValue(id, out FakeSession session)) return Error(404, "NOT_FOUND");
            lock (session)
            {
                switch (context.Request.Method + " " + action)
                {
                    case "GET config":
                        Interlocked.Increment(ref ConfigCalls);
                        session.Data["session"]["status"] = session.Status;
                        return Ok(session.Data.DeepClone());
                    case "POST start":
                        return Start(session, JsonDocument.Parse(body).RootElement);
                    case "POST result":
                        if (FailResults > 0)
                        {
                            FailResults--;
                            return Error(503, "MAINTENANCE");
                        }
                        return Result(session, JsonDocument.Parse(body).RootElement.Clone());
                    default:
                        return Error(400, "INVALID_REQUEST");
                }
            }
        }

        private static IResult Start(FakeSession session, JsonElement body)
        {
            session.StartCalls++;
            if (session.Status == "running")
                return Ok(new JsonObject { ["status"] = "running", ["players_at_start"] = session.StartedWith.Count, ["duplicate"] = true });
            if (session.Status != "lobby") return Error(409, "CONFLICT", "session_closed");
            if (body.Str("by") != session.HostPid) return Error(403, "FORBIDDEN", "not_host");
            var present = body.List("players").Select(p => p.GetString()).Where(session.Members.Contains).Distinct().ToList();
            if (present.Count < session.Data["rules"]["min_players_to_start"].GetValue<int>()) return Error(409, "CONFLICT", "not_enough_players");
            session.Status = "running";
            session.StartedAt = DateTime.UtcNow;
            session.StartedWith = present;
            return Ok(new JsonObject { ["status"] = "running", ["players_at_start"] = present.Count, ["duplicate"] = false });
        }

        /// <summary>server/src/Service/Coop.php 의 internalResult() 와 같은 검증.</summary>
        private IResult Result(FakeSession session, JsonElement body)
        {
            session.ResultCalls++;
            if (session.Status == "finished")
            {
                var again = (JsonObject)session.Response.DeepClone();
                again["duplicate"] = true;
                return Ok(again);
            }
            if (session.Status != "running") return Error(409, "CONFLICT", "session_closed");

            JsonObject zone = session.Data["zone"].AsObject();
            string result = body.Str("result");
            int waves = body.Int("waves_cleared");
            int gate = body.Int("gate_hp_left");
            int duration = body.Int("duration_seconds");
            string close = body.Str("close_reason");
            double elapsed = (DateTime.UtcNow - session.StartedAt).TotalSeconds * TimeScale;
            string reject = null;
            if (result != "win" && result != "lose") reject = "bad_result";
            else if (waves > session.WaveCount || (result == "win" && (waves != session.WaveCount || gate < 1))) reject = "bad_waves";
            else if (gate > session.Data["gate_hp"].GetValue<int>() || (result == "lose" && close == "gate_destroyed" && gate != 0)) reject = "bad_gate_hp";
            else if (elapsed < waves * zone["min_seconds_per_wave"].GetValue<double>() || duration > elapsed + 5) reject = "too_fast";
            else
            {
                var lines = new List<BudgetLine>();
                foreach (JsonElement line in body.List("budget"))
                {
                    // 종류마다 있어야 할 것이 있는지도 본다 (PHP 는 is_int 로 본다).
                    if (line.GetProperty("seq").ValueKind != JsonValueKind.Number || line.GetProperty("delta").ValueKind != JsonValueKind.Number
                        || line.GetProperty("balance_after").ValueKind != JsonValueKind.Number)
                    {
                        reject = "budget_format";
                        break;
                    }
                    lines.Add(new BudgetLine
                    {
                        seq = line.Int("seq"), pid = line.Str("pid"), kind = line.Str("kind"), delta = line.Int("delta"),
                        balanceAfter = line.Int("balance_after"),
                        slot = line.GetProperty("slot").ValueKind == JsonValueKind.Number ? line.Int("slot") : -1, code = line.Str("code"),
                        atMs = line.Int("at_ms"),
                    });
                }
                if (reject == null) reject = SharedBudget.FindInconsistency(lines, session.Members);
            }
            bool accepted = reject == null;
            session.Status = "finished";
            session.Report = body;
            session.Response = new JsonObject
            {
                ["accepted"] = accepted, ["reject_reason"] = reject, ["result"] = accepted ? result : null, ["waves_cleared"] = accepted ? waves : 0,
                ["reward_gc"] = accepted ? (result == "win" ? zone["win_gc"].GetValue<int>() : zone["lose_gc_per_wave"].GetValue<int>() * waves) : 0,
                ["duplicate"] = false,
            };
            return Ok(session.Response.DeepClone());
        }
    }

    /// <summary>테스트용 클라이언트. 받은 메시지를 모두 모아 둔다.</summary>
    public sealed class TestClient : IAsyncDisposable
    {
        private readonly ClientWebSocket socket = new ClientWebSocket();
        private readonly List<JsonElement> messages = new List<JsonElement>();
        private int cursor;
        private Task reader;

        public string Pid;
        public JsonElement Welcome;
        public WebSocketCloseStatus? CloseStatus { get; private set; }
        public string CloseReason { get; private set; }
        public Task Closed => reader;

        public static async Task<TestClient> ConnectAsync(string wsUrl, Action<ClientWebSocketOptions> tweak = null)
        {
            var client = new TestClient();
            tweak?.Invoke(client.socket.Options);
            using (var timeout = new CancellationTokenSource(5000))
                await client.socket.ConnectAsync(new Uri(wsUrl), timeout.Token);
            client.reader = Task.Run(client.ReadAsync);
            return client;
        }

        private async Task ReadAsync()
        {
            var buffer = new byte[1 << 20];
            try
            {
                while (true)
                {
                    int length = 0;
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, length, buffer.Length - length), CancellationToken.None);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            CloseStatus = result.CloseStatus;
                            CloseReason = result.CloseStatusDescription;
                            // 닫는 인사에 답한다.
                            using (var timeout = new CancellationTokenSource(1000))
                                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", timeout.Token);
                            return;
                        }
                        length += result.Count;
                    }
                    while (!result.EndOfMessage);
                    JsonElement message = JsonDocument.Parse(new ReadOnlyMemory<byte>(buffer, 0, length)).RootElement.Clone();
                    lock (messages) messages.Add(message);
                }
            }
            catch (Exception error) when (error is WebSocketException || error is OperationCanceledException || error is ObjectDisposedException
                || error is InvalidOperationException)
            {
                // 서버가 끊었다.
            }
        }

        public Task SendAsync(string json) =>
            socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)), WebSocketMessageType.Text, true, CancellationToken.None);

        public Task SendBytesAsync(byte[] bytes, WebSocketMessageType type) =>
            socket.SendAsync(new ArraySegment<byte>(bytes), type, true, CancellationToken.None);

        public List<JsonElement> All
        {
            get { lock (messages) return messages.ToList(); }
        }

        public int Count
        {
            get { lock (messages) return messages.Count; }
        }

        /// <summary>조건에 맞는 다음 메시지를 기다린다. 앞에서 이미 돌려준 메시지보다 뒤의 것만 본다.</summary>
        public async Task<JsonElement> WaitAsync(Func<JsonElement, bool> match, string what, int timeoutMs = 8000)
        {
            DateTime until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (true)
            {
                lock (messages)
                {
                    for (int i = cursor; i < messages.Count; i++)
                    {
                        if (!match(messages[i])) continue;
                        cursor = i + 1;
                        return messages[i];
                    }
                }
                if (DateTime.UtcNow >= until)
                    throw new TimeoutException("기다리던 메시지가 오지 않았습니다: " + what + " (" + Pid + ", 받은 것 " + Count + "개, 닫힘 " + CloseReason + ")");
                await Task.Delay(5);
            }
        }

        /// <summary>조건에 맞는 메시지를 받았거나 받을 때까지 기다린다 (이미 지나간 것도 본다).</summary>
        public async Task<JsonElement> SeenAsync(Func<JsonElement, bool> match, string what, int timeoutMs = 8000)
        {
            DateTime until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (true)
            {
                lock (messages)
                {
                    foreach (JsonElement message in messages)
                        if (match(message)) return message;
                }
                if (DateTime.UtcNow >= until)
                    throw new TimeoutException("기다리던 메시지가 오지 않았습니다: " + what + " (" + Pid + ", 받은 것 " + Count + "개, 닫힘 " + CloseReason + ")");
                await Task.Delay(5);
            }
        }

        public Task<JsonElement> EventAsync(string name, int timeoutMs = 8000) => WaitAsync(m => m.IsEvent(name), "event " + name, timeoutMs);

        /// <summary>지금부터 오는 상태 가운데 조건에 맞는 첫 번째.</summary>
        public Task<JsonElement> StateAsync(Func<JsonElement, bool> match = null, string what = "state", int timeoutMs = 8000)
        {
            lock (messages) cursor = messages.Count;
            return WaitAsync(m => m.Is("state") && (match == null || match(m)), what, timeoutMs);
        }

        /// <summary>지금까지 받은 것 가운데 조건에 맞는 것 (순서와 상관없이).</summary>
        public List<JsonElement> Received(Func<JsonElement, bool> match) => All.Where(match).ToList();

        public JsonElement LastState => All.Last(m => m.Is("state"));

        public void Abort() => socket.Abort();

        public async Task WaitClosedAsync(int timeoutMs = 5000)
        {
            if (await Task.WhenAny(reader, Task.Delay(timeoutMs)) != reader) throw new TimeoutException("연결이 닫히지 않았습니다 (" + Pid + ").");
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (socket.State == WebSocketState.Open)
                {
                    using (var timeout = new CancellationTokenSource(1000))
                        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", timeout.Token);
                }
            }
            catch (Exception)
            {
            }
            socket.Abort();
            socket.Dispose();
        }
    }

    /// <summary>테스트 한 번에 쓰는 무대: 흉내 낸 게임 서버 + 실시간 서버 + 붙은 클라이언트들.</summary>
    public sealed class Arena : IAsyncDisposable
    {
        public FakeGame Game;
        public RealtimeHost Host;
        public RealtimeOptions Options;
        public string WsUrl;
        public string HttpUrl;
        private readonly List<TestClient> clients = new List<TestClient>();
        private bool stopped;

        public static async Task<Arena> StartAsync(Action<RealtimeOptions> tweak = null)
        {
            var arena = new Arena { Game = await FakeGame.StartAsync() };
            arena.Options = new RealtimeOptions
            {
                Secret = FakeGame.Secret, GameApiUrl = arena.Game.Url, Listen = "http://127.0.0.1:0",
                // 전투 시계를 10배로 돌린다 (테스트에서만).
                TickMilliseconds = 10, MaxConnectionsPerIp = 200, ResultRetrySeconds = new[] { 0, 0, 0 }, SessionCheckSeconds = 3600,
                JoinTimeoutMs = 3000,
            };
            tweak?.Invoke(arena.Options);
            Assert(arena.Options.FindProblems().Count == 0, string.Join(" ", arena.Options.FindProblems()));
            arena.Game.TimeScale = 100.0 / arena.Options.TickMilliseconds;
            arena.Host = RealtimeHost.Build(arena.Options, logging: _ => { });
            await arena.Host.App.StartAsync();
            arena.HttpUrl = arena.Host.App.Urls.First();
            arena.WsUrl = arena.HttpUrl.Replace("http://", "ws://") + "/ws";
            return arena;
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        public async Task<TestClient> ConnectAsync(Action<ClientWebSocketOptions> tweak = null)
        {
            TestClient client = await TestClient.ConnectAsync(WsUrl, tweak);
            lock (clients) clients.Add(client);
            return client;
        }

        /// <summary>입장권을 내고 들어간다. welcome 을 받은 뒤에 돌아온다.</summary>
        public async Task<TestClient> JoinAsync(FakeSession session, int member, Action<JsonObject> ticket = null)
        {
            TestClient client = await ConnectAsync();
            client.Pid = session.Members[member];
            await client.SendAsync("{\"type\":\"join\",\"ticket\":\"" + Game.Ticket(session, member, ticket) + "\"}");
            client.Welcome = await client.WaitAsync(m => m.Is("welcome") || m.Is("error"), "welcome");
            if (!client.Welcome.Is("welcome")) throw new InvalidOperationException("들어가지 못했습니다: " + client.Welcome.Str("reason"));
            return client;
        }

        public async Task<List<TestClient>> JoinAllAsync(FakeSession session, int count)
        {
            var list = new List<TestClient>();
            for (int i = 0; i < count; i++) list.Add(await JoinAsync(session, i));
            return list;
        }

        /// <summary>입장권을 냈을 때 거절당하는 이유.</summary>
        public async Task<string> JoinErrorAsync(string ticket)
        {
            TestClient client = await ConnectAsync();
            await client.SendAsync("{\"type\":\"join\",\"ticket\":\"" + ticket + "\"}");
            JsonElement reply = await client.WaitAsync(m => m.Is("welcome") || m.Is("error"), "error");
            await client.WaitClosedAsync();
            return reply.Is("error") ? reply.Str("reason") : null;
        }

        /// <summary>방장이 시작하고, 모두가 시작 알림을 받을 때까지 기다린다.</summary>
        public async Task StartBattleAsync(List<TestClient> team)
        {
            await team[0].SendAsync("{\"type\":\"start\"}");
            foreach (TestClient client in team) await client.EventAsync("started");
        }

        public async Task StopAsync()
        {
            if (stopped) return;
            stopped = true;
            await Host.App.StopAsync();
        }

        public async ValueTask DisposeAsync()
        {
            List<TestClient> open;
            lock (clients) open = clients.ToList();
            foreach (TestClient client in open) await client.DisposeAsync();
            await StopAsync();
            await Host.App.DisposeAsync();
            await Game.DisposeAsync();
        }
    }
}
