using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using NUnit.Framework;

namespace CityGuardians.Realtime.Tests
{
    /// <summary>
    /// 입장권과 내부 요청의 서명. 게임 서버(PHP)와 같은 값을 내야 한다:
    /// 아래의 고정 벡터는 server/tests/CoopContractTest.php 에도 똑같이 적혀 있다.
    /// </summary>
    public sealed class SignerTests
    {
        private const string Secret = FakeGame.Secret;
        private const string PhpTicket =
            "eyJ2IjoxLCJzaWQiOiJkX2FiY2RlZmdoMTIzNDU2IiwicGlkIjoicF8wMTIzNDU2Nzg5YWJjZCIsIm5hbWUiOiLsiJjtmLjrjIDsm5AgQS9CIiwicm9sZSI6ImJ1aWxkZXIiLCJsZXZlbCI6NywiaG9zdCI6dHJ1ZSwic3RydWN0cyI6WyJ0d3JfbWFjaGluZV9ndW4iLCJ0d3Jfc2xvdyJdLCJwaW5nX2Jsb2NrZWQiOmZhbHNlLCJleHAiOjIwMDAwMDAwMDAsIm5vbmNlIjoibjBuY2VfYWJjREVGIn0.ria6JTRyfYrqqCBEZiq_ZSo9SgRuYdtwxC8NY3m5XAE";

        private static JsonObject Payload(Action<JsonObject> tweak = null)
        {
            var payload = new JsonObject
            {
                ["v"] = 1, ["sid"] = "d_abcdefgh123456", ["pid"] = "p_0123456789abcd", ["name"] = "대원", ["role"] = null, ["level"] = 3, ["host"] = false,
                ["structs"] = new JsonArray("twr_machine_gun"), ["ping_blocked"] = true, ["exp"] = 2000, ["nonce"] = "nonce_000001",
            };
            tweak?.Invoke(payload);
            return payload;
        }

        [Test]
        public void TicketMadeByTheGameServer_IsAccepted()
        {
            var signer = new Signer(Secret);
            Ticket ticket = signer.VerifyTicket(PhpTicket, 1999999999);
            Assert.IsNotNull(ticket);
            Assert.AreEqual("d_abcdefgh123456", ticket.Sid);
            Assert.AreEqual("p_0123456789abcd", ticket.Pid);
            Assert.AreEqual("수호대원 A/B", ticket.Name);
            Assert.AreEqual("builder", ticket.Role);
            Assert.AreEqual(7, ticket.Level);
            Assert.IsTrue(ticket.Host);
            Assert.IsFalse(ticket.PingBlocked);
            CollectionAssert.AreEqual(new[] { "twr_machine_gun", "twr_slow" }, ticket.Structs);
            Assert.AreEqual(2000000000, ticket.Exp);
            Assert.AreEqual("n0nce_abcDEF", ticket.Nonce);
            // 만료 시각이 되면 받지 않는다 (게임 서버와 같은 기준: now >= exp).
            Assert.IsNull(signer.VerifyTicket(PhpTicket, 2000000000));
            Assert.IsNull(signer.VerifyTicket(PhpTicket, 2000000001));
        }

        [Test]
        public void RequestSignature_IsTheSameAsTheGameServerComputes()
        {
            var signer = new Signer(Secret);
            Assert.AreEqual("t=1794538800,v1=5541373ba3cc5cf57f2a8f0e10257dfb7c905af59de1d9d39afe6e3e7c4575c8",
                signer.SignRequest("post", "/internal/coop/sessions/d_abcdefgh123456/result",
                    Encoding.UTF8.GetBytes("{\"result\":\"win\",\"waves_cleared\":5}"), 1794538800));
            Assert.AreEqual("t=1794538800,v1=0cfc865951a7fe245092a576951a9f74e9ac2d5cf5e19b700403407fc9266619",
                signer.SignRequest("GET", "/internal/coop/sessions/d_abcdefgh123456/config", null, 1794538800));
            // 방식, 경로, 본문, 시각 가운데 하나만 달라도 서명이 달라진다.
            string baseline = signer.SignRequest("GET", "/a", null, 100);
            Assert.AreNotEqual(baseline, signer.SignRequest("POST", "/a", null, 100));
            Assert.AreNotEqual(baseline, signer.SignRequest("GET", "/b", null, 100));
            Assert.AreNotEqual(baseline, signer.SignRequest("GET", "/a", new byte[] { 1 }, 100));
            Assert.AreNotEqual(baseline.Substring(baseline.IndexOf(',')), signer.SignRequest("GET", "/a", null, 101).Substring(baseline.IndexOf(',')));
            Assert.AreNotEqual(baseline, new Signer(Secret + "x").SignRequest("GET", "/a", null, 100));
        }

        [Test]
        public void ForgedOrDamagedTickets_AreRejected()
        {
            var signer = new Signer(Secret);
            string good = FakeGame.Sign(Payload().ToJsonString());
            Assert.IsNotNull(signer.VerifyTicket(good, 1000));

            // 다른 비밀값으로 만든 것.
            Assert.IsNull(signer.VerifyTicket(FakeGame.Sign(Payload().ToJsonString(), "another-secret-0000000000000000000000000"), 1000));
            // 내부 요청용 키로 서명한 것 (용도가 다른 키는 서로 쓸 수 없다).
            Assert.IsNull(signer.VerifyTicket(FakeGame.Sign(Payload().ToJsonString(), Secret, "cg-internal-v1"), 1000));
            // 내용을 바꾸고 서명은 그대로 둔 것 (방장인 척).
            string forged = FakeGame.Sign(Payload(p => p["host"] = true).ToJsonString());
            Assert.IsNull(signer.VerifyTicket(forged.Split('.')[0] + "." + good.Split('.')[1], 1000));
            // 서명이 없거나, 잘렸거나, 조각이 더 있는 것.
            Assert.IsNull(signer.VerifyTicket(good.Split('.')[0], 1000));
            Assert.IsNull(signer.VerifyTicket(good.Split('.')[0] + ".", 1000));
            Assert.IsNull(signer.VerifyTicket(good.Substring(0, good.Length - 2), 1000));
            Assert.IsNull(signer.VerifyTicket(good + ".x", 1000));
            Assert.IsNull(signer.VerifyTicket("." + good.Split('.')[1], 1000));
            Assert.IsNull(signer.VerifyTicket(good.Replace('.', ' '), 1000));
            Assert.IsNull(signer.VerifyTicket("", 1000));
            Assert.IsNull(signer.VerifyTicket(null, 1000));
            Assert.IsNull(signer.VerifyTicket(new string('a', 5000), 1000));
        }

        [Test]
        public void SignedTicketsWithBadContents_AreRejectedToo()
        {
            var signer = new Signer(Secret);
            string Make(Action<JsonObject> tweak) => FakeGame.Sign(Payload(tweak).ToJsonString());
            Assert.IsNull(signer.VerifyTicket(Make(p => p["v"] = 2), 1000), "모르는 판(version)");
            Assert.IsNull(signer.VerifyTicket(Make(p => p.Remove("exp")), 1000), "만료 시각이 없다");
            Assert.IsNull(signer.VerifyTicket(Make(p => p["exp"] = "2000"), 1000), "만료 시각이 숫자가 아니다");
            Assert.IsNull(signer.VerifyTicket(Make(p => p["sid"] = "d_../../etc"), 1000), "판 번호의 형식");
            Assert.IsNull(signer.VerifyTicket(Make(p => p["sid"] = 5), 1000));
            Assert.IsNull(signer.VerifyTicket(Make(p => p["pid"] = "a b"), 1000), "계정 번호의 형식");
            Assert.IsNull(signer.VerifyTicket(Make(p => p.Remove("nonce")), 1000), "한 번만 쓰게 하는 값이 없다");
            Assert.IsNull(signer.VerifyTicket(FakeGame.Sign("[1,2,3]"), 1000));
            Assert.IsNull(signer.VerifyTicket(FakeGame.Sign("not json"), 1000));

            // 덜 중요한 값은 고쳐서 받는다.
            Ticket ticket = signer.VerifyTicket(Make(p =>
            {
                p["name"] = new string('가', 100);
                p["role"] = "<script>";
                p["level"] = 100000;
                p["structs"] = new JsonArray("twr_slow", "DROP TABLE", 5, "twr_emp");
            }), 1000);
            Assert.IsNotNull(ticket);
            Assert.AreEqual(40, ticket.Name.Length);
            Assert.IsNull(ticket.Role);
            Assert.AreEqual(999, ticket.Level);
            CollectionAssert.AreEqual(new[] { "twr_slow", "twr_emp" }, ticket.Structs);
            Assert.IsTrue(ticket.PingBlocked);
            Assert.IsFalse(ticket.Host);
        }

        [Test]
        public void ShortSecret_IsRefused()
        {
            Assert.Throws<ArgumentException>(() => new Signer("too-short"));
            Assert.Throws<ArgumentException>(() => new Signer(null));
        }

        [Test]
        public void TicketCanBeUsedOnce()
        {
            var cache = new NonceCache();
            var signer = new Signer(Secret);
            Ticket ticket = signer.VerifyTicket(FakeGame.Sign(Payload().ToJsonString()), 1000);
            Assert.IsTrue(cache.TryUse(ticket, 1000));
            Assert.IsFalse(cache.TryUse(ticket, 1001), "같은 입장권을 두 번 쓸 수 없다");
            Ticket other = signer.VerifyTicket(FakeGame.Sign(Payload(p => p["nonce"] = "nonce_000002").ToJsonString()), 1000);
            Assert.IsTrue(cache.TryUse(other, 1001));
            Assert.AreEqual(2, cache.Count);
            // 만료된 입장권은 기억에서 지운다 (어차피 서명 확인에서 걸린다).
            Ticket later = signer.VerifyTicket(FakeGame.Sign(Payload(p =>
            {
                p["nonce"] = "nonce_000003";
                p["exp"] = 9000;
            }).ToJsonString()), 1000);
            Assert.IsTrue(cache.TryUse(later, 2100));
            Assert.AreEqual(1, cache.Count);
        }

        [Test]
        public void Names_AreConvertedLikeTheGameServerDoes()
        {
            var expected = new Dictionary<string, string>
            {
                ["playerShotsPer10s"] = "player_shots_per10s", ["playerRangeM"] = "player_range_m", ["balanceAfter"] = "balance_after",
                ["atMs"] = "at_ms", ["gateHpLeft"] = "gate_hp_left", ["hpScalePercentPerPlayer"] = "hp_scale_percent_per_player", ["x"] = "x",
                ["winGc"] = "win_gc",
            };
            foreach (var pair in expected) Assert.AreEqual(pair.Value, SnakeCasePolicy.Instance.ConvertName(pair.Key));
            string json = Encoding.UTF8.GetString(Wire.Bytes(new { wavesCleared = 5, name = "수호대원 <A&B>", slot = (int?)null }));
            Assert.AreEqual("{\"waves_cleared\":5,\"name\":\"수호대원 <A&B>\",\"slot\":null}", json);
        }

        [Test]
        public void Settings_AreCheckedBeforeTheServerStarts()
        {
            var env = new Dictionary<string, string>
            {
                ["REALTIME_SECRET"] = new string('s', 40), ["GAME_API_URL"] = "https://game.example/api/v1/",
                ["REALTIME_ALLOWED_ORIGINS"] = "https://play.example, https://www.example/", ["REALTIME_MAX_ROOMS"] = "50",
            };
            RealtimeOptions Read() => RealtimeOptions.FromEnvironment(name => env.TryGetValue(name, out string value) ? value : null);
            RealtimeOptions options = Read();
            Assert.AreEqual(0, options.FindProblems().Count);
            Assert.AreEqual("https://game.example/api/v1", options.GameApiUrl);
            Assert.AreEqual("http://127.0.0.1:8090", options.Listen, "기본값은 이 PC 에서만 듣는다");
            Assert.AreEqual(50, options.MaxRooms);
            Assert.IsTrue(options.AllowedOrigins.Contains("https://www.example"));
            Assert.IsFalse(options.TrustProxy);
            Assert.AreEqual(100, options.TickMilliseconds, "전투 시계의 빠르기는 환경변수로 바꿀 수 없다");

            void Bad(string name, string value, string expect)
            {
                string before = env.TryGetValue(name, out string old) ? old : null;
                env[name] = value;
                List<string> problems = Read().FindProblems();
                Assert.IsTrue(problems.Any(p => p.Contains(expect)), name + "=" + value + " → " + string.Join(" / ", problems));
                Assert.IsFalse(problems.Any(p => value != null && value.Length > 8 && p.Contains(value)), "값을 그대로 내보이지 않는다");
                env[name] = before;
            }
            Bad("REALTIME_SECRET", "short-secret", "REALTIME_SECRET");
            Bad("REALTIME_SECRET", null, "REALTIME_SECRET");
            Bad("GAME_API_URL", "http://game.example/api/v1", "https");
            Bad("GAME_API_URL", "ftp://game.example/", "GAME_API_URL");
            Bad("GAME_API_URL", "https://user:pw@game.example/api", "GAME_API_URL");
            Bad("GAME_API_URL", null, "GAME_API_URL");
            Bad("REALTIME_LISTEN", "https://0.0.0.0:443", "REALTIME_LISTEN");
            Bad("REALTIME_ALLOWED_ORIGINS", "play.example", "REALTIME_ALLOWED_ORIGINS");
            env["GAME_API_URL"] = "http://127.0.0.1:8080/api/v1";
            Assert.AreEqual(0, Read().FindProblems().Count, "같은 서버 안에서는 http 를 쓸 수 있다");
        }
    }
}
