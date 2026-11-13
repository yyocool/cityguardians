using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CityGuardians.Domain;

namespace CityGuardians.Realtime
{
    /// <summary>한 판을 여는 데 필요한 것 (게임 서버의 /internal/coop/sessions/{id}/config).</summary>
    public sealed class RoomConfig
    {
        public string SessionId;
        /// <summary>lobby, running, finished, cancelled, expired, abandoned.</summary>
        public string Status;
        public string RegionCode;
        public string RegionName;
        public string HostPid;
        public int ExpiresInSeconds;
        /// <summary>한 판의 최대 시간 (분).</summary>
        public int SessionMaxMinutes = 60;
        public CoopSimConfig Sim;
    }

    public sealed class ApiReply
    {
        public bool Ok;
        /// <summary>HTTP 상태. 닿지 못했으면 0.</summary>
        public int Status;
        /// <summary>실패한 이유 (게임 서버의 reason 또는 code). 닿지 못했으면 unreachable.</summary>
        public string Reason;
        public JsonElement Data;
        /// <summary>잠시 뒤에 다시 해 볼 만한 실패인가 (닿지 못함, 점검 중, 서버 오류).</summary>
        public bool Retryable => !Ok && (Status == 0 || Status == 429 || Status >= 500);
    }

    /// <summary>게임 서버(PHP)에 하는 요청. 판이 열릴 때, 시작할 때, 끝날 때에만 부른다.</summary>
    public interface IGameApi
    {
        Task<ApiReply> ConfigAsync(string sessionId, CancellationToken cancel);
        Task<ApiReply> StartAsync(string sessionId, string byPid, IReadOnlyList<string> connectedPids, CancellationToken cancel);
        Task<ApiReply> ResultAsync(string sessionId, CoopReport report, CancellationToken cancel);
    }

    public sealed class HttpGameApi : IGameApi, IDisposable
    {
        private const int MaxResponseBytes = 2 * 1024 * 1024;

        private readonly HttpClient http;
        private readonly Signer signer;
        private readonly string baseUrl;
        private readonly Func<long> clock;

        public HttpGameApi(RealtimeOptions options, Signer signer, Func<long> clock = null, HttpMessageHandler handler = null)
        {
            this.signer = signer;
            this.clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            baseUrl = options.GameApiUrl.TrimEnd('/');
            // 다른 곳으로 넘기는 응답을 따라가지 않고, 시스템 프록시도 쓰지 않는다 (서명한 요청이 엉뚱한 곳으로 가지 않게).
            http = new HttpClient(handler ?? new SocketsHttpHandler
            {
                AllowAutoRedirect = false, UseProxy = false, UseCookies = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5), ConnectTimeout = TimeSpan.FromSeconds(5),
            })
            {
                Timeout = TimeSpan.FromSeconds(Math.Max(1, options.ApiTimeoutSeconds)),
                MaxResponseContentBufferSize = MaxResponseBytes,
            };
        }

        public void Dispose() => http.Dispose();

        private static string Path(string sessionId, string action) => "/internal/coop/sessions/" + sessionId + "/" + action;

        public Task<ApiReply> ConfigAsync(string sessionId, CancellationToken cancel) => SendAsync(HttpMethod.Get, Path(sessionId, "config"), null, cancel);

        public Task<ApiReply> StartAsync(string sessionId, string byPid, IReadOnlyList<string> connectedPids, CancellationToken cancel) =>
            SendAsync(HttpMethod.Post, Path(sessionId, "start"), Wire.Bytes(new { by = byPid, players = connectedPids }), cancel);

        public Task<ApiReply> ResultAsync(string sessionId, CoopReport report, CancellationToken cancel) =>
            SendAsync(HttpMethod.Post, Path(sessionId, "result"), Wire.Bytes(ResultBody(report)), cancel);

        /// <summary>결과 보고의 본문. 보상 금액은 넣지 않는다 (게임 서버가 방어 구역 정의에서 계산한다).</summary>
        public static object ResultBody(CoopReport report)
        {
            var members = new List<object>();
            foreach (CoopMemberReport m in report.members)
                members.Add(new
                {
                    m.pid, m.role, m.connectedSeconds, m.kills, m.towersBuilt, m.towersUpgraded, m.repairs, m.damageDealt, m.pingsSent, m.pingsRejected,
                });
            var budget = new List<object>();
            foreach (BudgetLine line in report.budget)
                budget.Add(new
                {
                    line.seq, line.pid, line.kind, line.delta, line.balanceAfter, slot = line.slot >= 0 ? (int?)line.slot : null, line.code, line.atMs,
                });
            var pings = new List<object>();
            foreach (PingRecord ping in report.pings)
                pings.Add(new { ping.pid, ping.kind, ping.x, ping.z, ping.atMs, ping.accepted });
            return new
            {
                report.result, report.wavesCleared, report.gateHpLeft, report.durationSeconds, report.closeReason, members, budget, pings,
            };
        }

        private async Task<ApiReply> SendAsync(HttpMethod method, string path, byte[] body, CancellationToken cancel)
        {
            try
            {
                using (var request = new HttpRequestMessage(method, baseUrl + path))
                {
                    request.Headers.TryAddWithoutValidation("X-CG-Signature", signer.SignRequest(method.Method, path, body, clock()));
                    request.Headers.TryAddWithoutValidation("Accept", "application/json");
                    if (body != null)
                    {
                        request.Content = new ByteArrayContent(body);
                        request.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
                    }
                    using (HttpResponseMessage response = await http.SendAsync(request, cancel).ConfigureAwait(false))
                    {
                        byte[] bytes = await response.Content.ReadAsByteArrayAsync(cancel).ConfigureAwait(false);
                        return Parse((int)response.StatusCode, bytes);
                    }
                }
            }
            catch (Exception error) when (error is HttpRequestException || error is TaskCanceledException || error is OperationCanceledException)
            {
                if (cancel.IsCancellationRequested) throw;
                return new ApiReply { Status = 0, Reason = "unreachable" };
            }
        }

        public static ApiReply Parse(int status, byte[] bytes)
        {
            var reply = new ApiReply { Status = status, Reason = "bad_response" };
            try
            {
                using (JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 }))
                {
                    JsonElement root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) return reply;
                    bool ok = root.TryGetProperty("ok", out JsonElement flag) && flag.ValueKind == JsonValueKind.True;
                    if (ok && status == 200 && root.TryGetProperty("data", out JsonElement data))
                    {
                        reply.Ok = true;
                        reply.Reason = null;
                        reply.Data = data.Clone();
                        return reply;
                    }
                    if (root.TryGetProperty("error", out JsonElement error) && error.ValueKind == JsonValueKind.Object)
                    {
                        string reason = error.TryGetProperty("reason", out JsonElement r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
                        string code = error.TryGetProperty("code", out JsonElement c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                        reply.Reason = Clean(reason ?? code) ?? "error";
                    }
                    return reply;
                }
            }
            catch (JsonException)
            {
                return reply;
            }
        }

        /// <summary>게임 서버가 준 이유를 그대로 클라이언트에 넘기기 전에 형식을 확인한다.</summary>
        private static string Clean(string reason)
        {
            if (string.IsNullOrEmpty(reason) || reason.Length > 40) return null;
            foreach (char c in reason)
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_')) return null;
            return reason.ToLowerInvariant();
        }

        // ── 응답 읽기 ───────────────────────────────────────

        private static string Text(JsonElement parent, string name) =>
            parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;

        private static int Number(JsonElement parent, string name, int fallback) =>
            parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt32(out int number) ? number : fallback;

        /// <summary>
        /// config 응답에서 방의 설정을 만든다. 형식이 맞지 않거나 값이 터무니없으면 null (그 판은 열지 않는다).
        /// </summary>
        public static RoomConfig ReadConfig(JsonElement data, string sessionId)
        {
            try
            {
                if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("session", out JsonElement session)) return null;
                if (Text(session, "id") != sessionId) return null;
                var config = new RoomConfig
                {
                    SessionId = sessionId, Status = Text(session, "status") ?? "", RegionCode = Text(session, "region_code") ?? "",
                    RegionName = Text(session, "region_name") ?? "", HostPid = Text(session, "host_pid") ?? "",
                    ExpiresInSeconds = Math.Max(0, Number(session, "expires_in_seconds", 0)),
                };
                var sim = new CoopSimConfig { maxPlayers = Math.Max(1, Math.Min(16, Number(session, "max_players", 4))) };
                if (!data.TryGetProperty("zone", out JsonElement zone) || zone.ValueKind != JsonValueKind.Object) return null;
                sim.zone = Wire.Read<StageDef>(zone);
                sim.gateHp = Math.Max(1, Math.Min(1000000, Number(data, "gate_hp", sim.zone != null ? sim.zone.baseHp : 1)));
                if (data.TryGetProperty("enemies", out JsonElement enemies)) sim.enemies = Wire.Read<List<EnemyDef>>(enemies) ?? new List<EnemyDef>();
                if (data.TryGetProperty("structures", out JsonElement structures))
                    sim.structures = Wire.Read<List<TowerDef>>(structures) ?? new List<TowerDef>();
                if (data.TryGetProperty("roles", out JsonElement roles)) sim.roles = Wire.Read<List<CoopRoleDef>>(roles) ?? new List<CoopRoleDef>();
                if (data.TryGetProperty("rules", out JsonElement rules) && rules.ValueKind == JsonValueKind.Object)
                {
                    sim.rules = Wire.Read<CoopRules>(rules) ?? new CoopRules();
                    config.SessionMaxMinutes = Math.Max(5, Math.Min(240, Number(rules, "session_max_minutes", 60)));
                }
                if (data.TryGetProperty("ping", out JsonElement ping) && ping.ValueKind == JsonValueKind.Object)
                    sim.ping = Wire.Read<PingRules>(ping) ?? new PingRules();
                config.Sim = sim;
                return FindProblem(sim) == null ? config : null;
            }
            catch (Exception error) when (error is JsonException || error is InvalidOperationException || error is NotSupportedException)
            {
                return null;
            }
        }

        /// <summary>시뮬레이션을 돌릴 수 없는 설정인가. 문제가 없으면 null.</summary>
        public static string FindProblem(CoopSimConfig sim)
        {
            StageDef zone = sim.zone;
            if (zone == null || string.IsNullOrEmpty(zone.code) || zone.waves == null || zone.waves.Count == 0 || zone.waves.Count > 100) return "zone";
            int total = 0;
            foreach (WaveDef wave in zone.waves)
            {
                if (wave == null || wave.groups == null || wave.groups.Count == 0 || wave.groups.Count > 50) return "waves";
                foreach (WaveGroupDef group in wave.groups)
                {
                    if (group == null || group.count < 0 || group.count > 1000 || !(group.interval >= 0f) || !(group.delay >= 0f)
                        || group.interval > 600f || group.delay > 3600f) return "waves";
                    total += group.count;
                }
            }
            if (total <= 0 || total > 20000) return "waves";
            if (zone.startEnergy < 0 || zone.waveClearEnergy < 0 || !(zone.restSeconds >= 0f) || zone.restSeconds > 600f) return "zone";
            if (sim.enemies == null || sim.enemies.Count == 0) return "enemies";
            foreach (EnemyDef enemy in sim.enemies)
                if (enemy == null || string.IsNullOrEmpty(enemy.code) || enemy.maxHp <= 0 || !(enemy.moveSpeed > 0f) || enemy.moveSpeed > 100f
                    || enemy.baseDamage < 0 || enemy.rewardEnergy < 0) return "enemies";
            if (sim.structures == null) return "structures";
            foreach (TowerDef tower in sim.structures)
                if (tower == null || string.IsNullOrEmpty(tower.code) || tower.energyCost < 0 || tower.upgradeEnergyCost < 0 || tower.maxHp <= 0
                    || !(tower.range >= 0f) || tower.range > 200f || !(tower.shotsPerSecond >= 0f) || tower.shotsPerSecond > 100f) return "structures";
            if (sim.roles == null || sim.rules == null || sim.ping == null) return "rules";
            return null;
        }
    }
}
