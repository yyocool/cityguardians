using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace CityGuardians.Realtime
{
    /// <summary>
    /// 실시간 서버의 설정. 환경변수로만 받는다 (docs/SERVER_DEPLOYMENT.md). 비밀값은 저장소와 문서에 적지 않는다.
    /// </summary>
    public sealed class RealtimeOptions
    {
        public const int MinSecretLength = 32;

        /// <summary>게임 서버(PHP)와 나눠 가진 비밀값 (REALTIME_SECRET). 32자 이상.</summary>
        public string Secret = "";
        /// <summary>게임 서버 API 의 주소 (GAME_API_URL). 예: https://game.example/api/v1. 끝의 / 는 뗀다.</summary>
        public string GameApiUrl = "";
        /// <summary>듣는 주소 (REALTIME_LISTEN). TLS 는 앞단의 프록시가 맡는다.</summary>
        public string Listen = "http://127.0.0.1:8090";
        /// <summary>브라우저에서 올 때 허용할 Origin (REALTIME_ALLOWED_ORIGINS, 쉼표로 구분). Origin 이 없는 연결(앱)은 그대로 받는다.</summary>
        public HashSet<string> AllowedOrigins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>앞단 프록시의 X-Forwarded-For 를 믿을까 (REALTIME_TRUST_PROXY=1). 프록시 뒤에 있을 때만 켠다.</summary>
        public bool TrustProxy;
        public int MaxConnections = 2000;
        public int MaxConnectionsPerIp = 20;
        public int MaxRooms = 300;
        /// <summary>
        /// 방이 없는데 게임 서버에는 "전투 중"으로 남아 있는 판을 닫을까 (REALTIME_CLOSE_ORPHANS).
        /// 실시간 서버가 전투 중에 재시작되면 그 판은 이어 갈 수 없다. 실시간 서버를 한 대만 돌릴 때만 켠다 (기본).
        /// </summary>
        public bool CloseOrphans = true;

        // ── 아래는 환경변수로 바꾸지 않는다 (테스트에서만 줄인다) ──
        /// <summary>한 틱의 실제 시간 (ms). 전투 시계 0.1초가 실제 0.1초다. 테스트에서만 줄인다.</summary>
        public int TickMilliseconds = 100;
        /// <summary>상태를 몇 틱마다 보낼까 (2 = 초당 5번).</summary>
        public int SnapshotEveryTicks = 2;
        public int JoinTimeoutMs = 10000;
        /// <summary>이 시간 동안 아무것도 보내지 않는 연결은 끊는다. 클라이언트는 15초마다 alive 를 보낸다.</summary>
        public int IdleTimeoutMs = 45000;
        public int MaxMessageBytes = 2048;
        /// <summary>한 연결이 1초에 보낼 수 있는 메시지 수 (넘으면 끊는다).</summary>
        public int MaxMessagesPerSecond = 40;
        /// <summary>대기실에 아무도 없는 채로 이 시간이 지나면 방을 치운다 (게임 서버의 판은 그대로 둔다).</summary>
        public int EmptyLobbySeconds = 120;
        /// <summary>게임 서버의 판 상태를 다시 확인하는 간격 (관리자가 닫았는지).</summary>
        public int SessionCheckSeconds = 30;
        /// <summary>결과를 게임 서버에 다시 보내는 간격 (초). 다 쓰면 포기하고 기록을 남긴다.</summary>
        public int[] ResultRetrySeconds = { 1, 2, 4, 8, 15, 30, 30, 30 };
        public int ApiTimeoutSeconds = 10;

        public static string Env(Func<string, string> env, string name, string fallback = "")
        {
            string value = env(name);
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        private static int Number(Func<string, string> env, string name, int fallback, int min, int max)
        {
            return int.TryParse(Env(env, name), out int value) ? Math.Max(min, Math.Min(max, value)) : fallback;
        }

        public static RealtimeOptions FromEnvironment(Func<string, string> env = null)
        {
            env = env ?? Environment.GetEnvironmentVariable;
            var options = new RealtimeOptions
            {
                Secret = Env(env, "REALTIME_SECRET"),
                GameApiUrl = Env(env, "GAME_API_URL").TrimEnd('/'),
                Listen = Env(env, "REALTIME_LISTEN", "http://127.0.0.1:8090"),
                TrustProxy = Env(env, "REALTIME_TRUST_PROXY") == "1",
                CloseOrphans = Env(env, "REALTIME_CLOSE_ORPHANS", "1") == "1",
            };
            options.MaxConnections = Number(env, "REALTIME_MAX_CONNECTIONS", options.MaxConnections, 1, 100000);
            options.MaxConnectionsPerIp = Number(env, "REALTIME_MAX_CONNECTIONS_PER_IP", options.MaxConnectionsPerIp, 1, 10000);
            options.MaxRooms = Number(env, "REALTIME_MAX_ROOMS", options.MaxRooms, 1, 10000);
            foreach (string origin in Env(env, "REALTIME_ALLOWED_ORIGINS").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                options.AllowedOrigins.Add(origin.TrimEnd('/'));
            return options;
        }

        /// <summary>설정의 문제 목록. 비어 있어야 서버를 띄운다. 비밀값 자체는 메시지에 넣지 않는다.</summary>
        public List<string> FindProblems()
        {
            var problems = new List<string>();
            if (Secret == null || Secret.Length < MinSecretLength)
                problems.Add("REALTIME_SECRET 이 없거나 짧습니다 (" + MinSecretLength + "자 이상).");
            if (!Uri.TryCreate(GameApiUrl, UriKind.Absolute, out Uri api) || (api.Scheme != "https" && api.Scheme != "http"))
                problems.Add("GAME_API_URL 이 올바른 주소가 아닙니다.");
            else if (api.Scheme == "http" && !api.IsLoopback)
                problems.Add("GAME_API_URL 은 https 여야 합니다 (같은 서버의 127.0.0.1 만 http 를 쓸 수 있습니다).");
            else if (!string.IsNullOrEmpty(api.Query) || !string.IsNullOrEmpty(api.Fragment) || !string.IsNullOrEmpty(api.UserInfo))
                problems.Add("GAME_API_URL 에 질의나 계정 정보를 넣을 수 없습니다.");
            if (!Regex.IsMatch(Listen ?? "", @"^http://[A-Za-z0-9.\-\[\]:*+]+:\d{1,5}$"))
                problems.Add("REALTIME_LISTEN 은 http://주소:포트 형식이어야 합니다 (TLS 는 앞단의 프록시가 맡습니다).");
            foreach (string origin in AllowedOrigins)
                if (!Regex.IsMatch(origin, @"^https?://[A-Za-z0-9.\-]+(:\d{1,5})?$"))
                    problems.Add("REALTIME_ALLOWED_ORIGINS 에 올바르지 않은 값이 있습니다.");
            return problems;
        }
    }
}
