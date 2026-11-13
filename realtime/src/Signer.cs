using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CityGuardians.Realtime
{
    /// <summary>입장권의 내용. 게임 서버가 서명해서 플레이어에게 준 것이다.</summary>
    public sealed class Ticket
    {
        public string Sid;
        public string Pid;
        public string Name;
        /// <summary>대기실에서 고른 역할. 고르지 않았으면 null.</summary>
        public string Role;
        public int Level = 1;
        public bool Host;
        /// <summary>이 사람이 세울 수 있는 타워의 코드.</summary>
        public List<string> Structs = new List<string>();
        /// <summary>관리자가 전술 핑을 막은 계정인가.</summary>
        public bool PingBlocked;
        /// <summary>만료 시각 (유닉스 초).</summary>
        public long Exp;
        public string Nonce;
    }

    /// <summary>
    /// 게임 서버(PHP)와 실시간 서버 사이의 서명. server/src/Infra/Signer.php 와 똑같이 계산한다
    /// (docs/COOP_DEFENSE_SYSTEM.md 4장). 두 구현이 같은 값을 내는지는 양쪽 테스트의 고정 벡터가 확인한다.
    ///
    ///   입장권      payload(base64url) + "." + base64url(HMAC-SHA256(payload(base64url)))   키: HMAC(비밀값, "cg-ticket-v1")
    ///   내부 요청   X-CG-Signature: t=시각,v1=hex(HMAC-SHA256("시각\n방식\n경로\n" + hex(SHA-256(본문))))   키: HMAC(비밀값, "cg-internal-v1")
    /// </summary>
    public sealed class Signer
    {
        public const int MaxTicketLength = 4096;

        private static readonly Regex SessionId = new Regex("^d_[a-z0-9]{14}$", RegexOptions.CultureInvariant);
        private static readonly Regex PublicId = new Regex("^[A-Za-z0-9_]{4,32}$", RegexOptions.CultureInvariant);
        private static readonly Regex Code = new Regex("^[a-z0-9_]{1,40}$", RegexOptions.CultureInvariant);
        private static readonly Regex NonceFormat = new Regex("^[A-Za-z0-9_-]{8,64}$", RegexOptions.CultureInvariant);
        private static readonly Regex Base64Url = new Regex("^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant);

        private readonly byte[] ticketKey;
        private readonly byte[] internalKey;

        public Signer(string secret)
        {
            if (secret == null || secret.Length < RealtimeOptions.MinSecretLength) throw new ArgumentException("REALTIME_SECRET 이 설정되지 않았습니다.");
            byte[] key = Encoding.UTF8.GetBytes(secret);
            ticketKey = HMACSHA256.HashData(key, Encoding.ASCII.GetBytes("cg-ticket-v1"));
            internalKey = HMACSHA256.HashData(key, Encoding.ASCII.GetBytes("cg-internal-v1"));
        }

        public static string ToBase64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private static byte[] FromBase64Url(string text)
        {
            if (string.IsNullOrEmpty(text) || !Base64Url.IsMatch(text)) return null;
            string padded = text.Replace('-', '+').Replace('_', '/') + new string('=', (4 - text.Length % 4) % 4);
            try
            {
                return Convert.FromBase64String(padded);
            }
            catch (FormatException)
            {
                return null;
            }
        }

        // ── 입장권 ──────────────────────────────────────────

        /// <summary>
        /// 입장권을 확인한다. 서명이 맞고, 만료되지 않았고, 내용의 형식이 맞아야 한다. 아니면 null.
        /// 무엇이 틀렸는지는 알려 주지 않는다.
        /// </summary>
        public Ticket VerifyTicket(string ticket, long nowUnix)
        {
            if (string.IsNullOrEmpty(ticket) || ticket.Length > MaxTicketLength) return null;
            int dot = ticket.IndexOf('.');
            if (dot <= 0 || dot != ticket.LastIndexOf('.')) return null;
            string body = ticket.Substring(0, dot);
            byte[] signature = FromBase64Url(ticket.Substring(dot + 1));
            if (signature == null || !Base64Url.IsMatch(body)) return null;
            byte[] expected = HMACSHA256.HashData(ticketKey, Encoding.ASCII.GetBytes(body));
            // 걸리는 시간으로 서명을 짐작할 수 없게 견준다.
            if (!CryptographicOperations.FixedTimeEquals(expected, signature)) return null;
            byte[] json = FromBase64Url(body);
            if (json == null) return null;
            try
            {
                using (JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 }))
                {
                    JsonElement root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) return null;
                    if (!root.TryGetProperty("v", out JsonElement version) || version.ValueKind != JsonValueKind.Number
                        || !version.TryGetInt32(out int v) || v != 1) return null;
                    if (!root.TryGetProperty("exp", out JsonElement exp) || exp.ValueKind != JsonValueKind.Number
                        || !exp.TryGetInt64(out long expires) || nowUnix >= expires) return null;
                    var result = new Ticket { Exp = expires, Sid = Text(root, "sid"), Pid = Text(root, "pid"), Nonce = Text(root, "nonce") };
                    if (result.Sid == null || !SessionId.IsMatch(result.Sid)) return null;
                    if (result.Pid == null || !PublicId.IsMatch(result.Pid)) return null;
                    if (result.Nonce == null || !NonceFormat.IsMatch(result.Nonce)) return null;
                    string name = Text(root, "name") ?? "";
                    result.Name = name.Length > 40 ? name.Substring(0, 40) : name;
                    string role = Text(root, "role");
                    result.Role = role != null && Code.IsMatch(role) ? role : null;
                    if (root.TryGetProperty("level", out JsonElement level) && level.ValueKind == JsonValueKind.Number && level.TryGetInt32(out int lv))
                        result.Level = Math.Max(1, Math.Min(999, lv));
                    result.Host = root.TryGetProperty("host", out JsonElement host) && host.ValueKind == JsonValueKind.True;
                    result.PingBlocked = root.TryGetProperty("ping_blocked", out JsonElement blocked) && blocked.ValueKind == JsonValueKind.True;
                    if (root.TryGetProperty("structs", out JsonElement structs) && structs.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement item in structs.EnumerateArray())
                        {
                            if (result.Structs.Count >= 64) break;
                            if (item.ValueKind == JsonValueKind.String && Code.IsMatch(item.GetString())) result.Structs.Add(item.GetString());
                        }
                    }
                    return result;
                }
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string Text(JsonElement root, string name) =>
            root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        // ── 내부 요청 ───────────────────────────────────────

        /// <summary>게임 서버의 /internal/... 을 부를 때 붙일 X-CG-Signature 의 값.</summary>
        public string SignRequest(string method, string path, byte[] body, long nowUnix)
        {
            string bodyHash = Convert.ToHexString(SHA256.HashData(body ?? Array.Empty<byte>())).ToLowerInvariant();
            string message = nowUnix + "\n" + method.ToUpperInvariant() + "\n" + path + "\n" + bodyHash;
            string mac = Convert.ToHexString(HMACSHA256.HashData(internalKey, Encoding.UTF8.GetBytes(message))).ToLowerInvariant();
            return "t=" + nowUnix + ",v1=" + mac;
        }
    }

    /// <summary>
    /// 한 번 쓴 입장권을 다시 쓰지 못하게 한다. 입장권은 수명이 짧으므로 (기본 60초) 만료될 때까지만 기억한다.
    /// </summary>
    public sealed class NonceCache
    {
        private readonly Dictionary<string, long> used = new Dictionary<string, long>();
        private readonly object gate = new object();
        private long lastSweep;

        /// <summary>처음 보는 입장권이면 true 를 돌려주고 기억한다.</summary>
        public bool TryUse(Ticket ticket, long nowUnix)
        {
            string key = ticket.Sid + "\n" + ticket.Pid + "\n" + ticket.Nonce;
            lock (gate)
            {
                if (nowUnix - lastSweep >= 30)
                {
                    lastSweep = nowUnix;
                    var expired = new List<string>();
                    foreach (var pair in used)
                        if (pair.Value <= nowUnix) expired.Add(pair.Key);
                    foreach (string old in expired) used.Remove(old);
                }
                if (used.ContainsKey(key)) return false;
                used[key] = ticket.Exp;
                return true;
            }
        }

        public int Count
        {
            get { lock (gate) return used.Count; }
        }
    }
}
