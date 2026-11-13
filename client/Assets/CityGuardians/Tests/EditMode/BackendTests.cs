using System;
using System.Collections.Generic;
using CityGuardians.Domain;
using CityGuardians.Services;
using NUnit.Framework;

namespace CityGuardians.Tests
{
    /// <summary>
    /// 로컬 모의 서버 검증. 실제 PHP 서버가 지켜야 할 규칙을 같은 방식으로 확인한다:
    /// 금액은 서버가 계산하고, 모든 증감은 원장에 남고, 같은 요청이 두 번 와도 한 번만 지급한다.
    /// </summary>
    public sealed class LocalMockBackendTests
    {
        private sealed class FakeClock : IClock
        {
            public DateTime UtcNow { get; set; } = new DateTime(2026, 11, 13, 3, 0, 0, DateTimeKind.Utc);
            public void Advance(double seconds) => UtcNow = UtcNow.AddSeconds(seconds);
        }

        private MemoryTextStore _store;
        private FakeClock _clock;

        [SetUp]
        public void SetUp()
        {
            _store = new MemoryTextStore();
            _clock = new FakeClock();
        }

        private LocalMockBackend NewBackend() => new LocalMockBackend(_store, _clock, null, 7);

        private static T Ok<T>(ApiResult<T> result)
        {
            Assert.IsNotNull(result, "응답 콜백이 불리지 않았다");
            Assert.IsTrue(result.ok, $"실패 응답: {result.errorCode} {result.message}");
            return result.data;
        }

        private static MeDto Boot(LocalMockBackend backend)
        {
            ApiResult<MeDto> result = null;
            backend.Bootstrap(r => result = r);
            return Ok(result);
        }

        private static string StartHunt(LocalMockBackend backend, string zone)
        {
            ApiResult<HuntSessionDto> result = null;
            backend.StartHunt(zone, r => result = r);
            return Ok(result).sessionId;
        }

        private static ApiResult<HuntReportDto> Report(LocalMockBackend backend, string session, int seq, string enemy, int count)
        {
            ApiResult<HuntReportDto> result = null;
            backend.ReportHunt(session, seq, new List<KillCount> { new KillCount(enemy, count) }, r => result = r);
            Assert.IsNotNull(result, "응답 콜백이 불리지 않았다");
            return result;
        }

        [Test]
        public void FirstBoot_CreatesAStrangerWithNothing()
        {
            MeDto me = Boot(NewBackend());
            StringAssert.StartsWith("p_", me.playerId);
            StringAssert.StartsWith("이방인-", me.displayName);
            Assert.AreEqual("stranger", me.residency);
            Assert.AreEqual(1, me.level);
            Assert.AreEqual(0, me.gc);
            Assert.AreEqual(100, me.hpMax);
            Assert.AreEqual("0", me.GetFlag(FlagKeys.TutorialStep));
            Assert.AreEqual(WeaponCodes.SparkBaton, me.equippedMelee);
        }

        [Test]
        public void Restart_RestoresTheSamePlayerAndProgress()
        {
            LocalMockBackend first = NewBackend();
            MeDto created = Boot(first);
            string session = StartHunt(first, ZoneCodes.NorthEast);
            _clock.Advance(10);
            Ok(Report(first, session, 1, EnemyCodes.MalfunctionRobot, 2));
            first.SetFlag(FlagKeys.TutorialStep, "6", _ => { });

            // 게임을 껐다 켠 상황: 같은 저장소, 새 서버 객체
            MeDto restored = Boot(NewBackend());
            Assert.AreEqual(created.playerId, restored.playerId);
            Assert.AreEqual(created.displayName, restored.displayName);
            Assert.AreEqual(20, restored.gc);
            Assert.AreEqual(20, restored.xp);
            Assert.AreEqual("6", restored.GetFlag(FlagKeys.TutorialStep));
        }

        [Test]
        public void GuestSecret_IsNotStoredOnTheServerSide()
        {
            Boot(NewBackend());
            string guest = _store.Read(LocalMockBackend.GuestFile);
            string server = _store.Read(LocalMockBackend.ServerFile);
            Assert.IsNotNull(guest);
            Assert.IsNotNull(server);

            // 서버 쪽에는 비밀키의 해시만 있어야 한다.
            int start = guest.IndexOf("\"guestSecret\":\"", StringComparison.Ordinal) + "\"guestSecret\":\"".Length;
            string secret = guest.Substring(start, guest.IndexOf('"', start) - start);
            Assert.Greater(secret.Length, 20);
            StringAssert.DoesNotContain(secret, server);
        }

        [Test]
        public void HuntReward_GoesThroughTheLedger()
        {
            LocalMockBackend backend = NewBackend();
            Boot(backend);
            string session = StartHunt(backend, ZoneCodes.NorthEast);
            _clock.Advance(10);

            HuntReportDto report = Ok(Report(backend, session, 1, EnemyCodes.MalfunctionRobot, 3));
            Assert.AreEqual(30, report.rewardGc);
            Assert.AreEqual(30, report.gc);
            Assert.AreEqual(30, report.dailyHuntGc);

            Assert.AreEqual(1, backend.CurrentLedger.Count);
            LedgerEntry entry = backend.CurrentLedger[0];
            Assert.AreEqual(30, entry.delta);
            Assert.AreEqual(30, entry.balanceAfter);
            Assert.AreEqual(LedgerReasons.HuntReward, entry.reason);
            Assert.AreEqual(session + "#1", entry.reference);
            Assert.IsNull(backend.FindLedgerInconsistency());
        }

        [Test]
        public void ResendingTheSameReport_PaysOnlyOnce()
        {
            LocalMockBackend backend = NewBackend();
            Boot(backend);
            string session = StartHunt(backend, ZoneCodes.NorthEast);
            _clock.Advance(10);

            HuntReportDto first = Ok(Report(backend, session, 1, EnemyCodes.MalfunctionRobot, 2));
            // 응답을 못 받은 줄 알고 같은 순번으로 다시 보낸다.
            HuntReportDto again = Ok(Report(backend, session, 1, EnemyCodes.MalfunctionRobot, 2));

            Assert.AreEqual(first.gc, again.gc);
            Assert.AreEqual(20, again.gc);
            Assert.AreEqual(1, backend.CurrentLedger.Count);
        }

        [Test]
        public void OutOfOrderReport_IsRefused()
        {
            LocalMockBackend backend = NewBackend();
            Boot(backend);
            string session = StartHunt(backend, ZoneCodes.NorthEast);

            ApiResult<HuntReportDto> skipped = Report(backend, session, 5, EnemyCodes.MalfunctionRobot, 1);
            Assert.IsFalse(skipped.ok);
            Assert.AreEqual(ApiErrors.Conflict, skipped.errorCode);
            Assert.AreEqual(0, backend.CurrentLedger.Count);
        }

        [Test]
        public void UnknownHuntSession_IsRefused()
        {
            LocalMockBackend backend = NewBackend();
            Boot(backend);
            ApiResult<HuntReportDto> result = Report(backend, "h_does_not_exist", 1, EnemyCodes.MalfunctionRobot, 1);
            Assert.IsFalse(result.ok);
            Assert.AreEqual(ApiErrors.NotFound, result.errorCode);
        }

        [Test]
        public void CheatedKillCount_IsCutDown()
        {
            LocalMockBackend backend = NewBackend();
            Boot(backend);
            string session = StartHunt(backend, ZoneCodes.NorthEast);
            _clock.Advance(1);

            HuntReportDto report = Ok(Report(backend, session, 1, EnemyCodes.MalfunctionRobot, 10_000));
            Assert.AreEqual(50, report.gc, "시작 여유분 5마리만 인정");
            Assert.AreEqual(HuntRejectReasons.TooFast, report.rejectReason);
            Assert.IsNull(backend.FindLedgerInconsistency());
        }

        [Test]
        public void DailyLimit_ResetsAtKoreanMidnight()
        {
            LocalMockBackend backend = NewBackend();
            Boot(backend);
            int limit = backend.Defs.economy.dailyHuntGcLimit;

            // 한도를 채운다: 1분에 24마리(240 GC)씩
            string session = StartHunt(backend, ZoneCodes.NorthEast);
            int seq = 1;
            long gc = 0;
            for (int i = 0; i < 40 && gc < limit; i++)
            {
                _clock.Advance(60);
                gc = Ok(Report(backend, session, seq++, EnemyCodes.MalfunctionRobot, 24)).gc;
            }
            Assert.AreEqual(limit, gc);

            _clock.Advance(60);
            HuntReportDto capped = Ok(Report(backend, session, seq++, EnemyCodes.MalfunctionRobot, 5));
            Assert.AreEqual(0, capped.rewardGc);
            Assert.AreEqual(HuntRejectReasons.DailyLimit, capped.rejectReason);
            Assert.Greater(capped.rewardXp, 0, "한도를 넘어도 경험치는 받는다");

            // 한국 시간 자정(UTC 15:00)을 넘긴다
            _clock.UtcNow = new DateTime(2026, 11, 13, 15, 0, 1, DateTimeKind.Utc);
            HuntReportDto nextDay = Ok(Report(backend, session, seq, EnemyCodes.MalfunctionRobot, 5));
            Assert.AreEqual(50, nextDay.rewardGc);
            Assert.AreEqual(50, nextDay.dailyHuntGc);
            Assert.IsNull(backend.FindLedgerInconsistency());
        }

        [Test]
        public void FirstRescueReward_NeedsAConfirmedKill_AndIsPaidOnce()
        {
            LocalMockBackend backend = NewBackend();
            Boot(backend);

            ApiResult<QuestRewardDto> early = null;
            backend.ClaimQuest(QuestCodes.FirstRescue, r => early = r);
            Assert.IsFalse(early.ok);
            Assert.AreEqual(ApiErrors.ValidationFailed, early.errorCode);

            string session = StartHunt(backend, ZoneCodes.HomeStreet);
            Ok(Report(backend, session, 1, EnemyCodes.TutorialRobot, 1));

            ApiResult<QuestRewardDto> claimed = null;
            backend.ClaimQuest(QuestCodes.FirstRescue, r => claimed = r);
            Assert.AreEqual(100, Ok(claimed).rewardGc);
            Assert.AreEqual(110, claimed.data.gc);

            ApiResult<QuestRewardDto> twice = null;
            backend.ClaimQuest(QuestCodes.FirstRescue, r => twice = r);
            Assert.IsFalse(twice.ok);
            Assert.AreEqual(ApiErrors.Conflict, twice.errorCode);

            Assert.AreEqual(2, backend.CurrentLedger.Count);
            Assert.AreEqual(LedgerReasons.QuestReward, backend.CurrentLedger[1].reason);
            Assert.IsNull(backend.FindLedgerInconsistency());
        }

        [Test]
        public void UnreadableGuestFile_IsReported_NotSilentlyReplaced()
        {
            Boot(NewBackend());
            _store.Write(LocalMockBackend.GuestFile, "이건 JSON이 아니에요 {{{");

            LocalMockBackend backend = NewBackend();
            ApiResult<MeDto> result = null;
            backend.Bootstrap(r => result = r);
            Assert.IsFalse(result.ok);
            Assert.AreEqual(ApiErrors.GuestDataCorrupt, result.errorCode);
            Assert.AreEqual("이건 JSON이 아니에요 {{{", _store.Read(LocalMockBackend.GuestFile), "실패했을 때 파일을 건드리면 안 된다");
        }

        [Test]
        public void StartOver_KeepsTheOldFileUnderAnotherName()
        {
            Boot(NewBackend());
            _store.Write(LocalMockBackend.GuestFile, "broken");

            LocalMockBackend backend = NewBackend();
            backend.Bootstrap(_ => { });
            ApiResult<MeDto> fresh = null;
            backend.StartOverAsNewGuest(r => fresh = r);

            MeDto me = Ok(fresh);
            Assert.AreEqual(0, me.gc);
            string kept = _store.Read($"guest.unreadable-{_clock.UtcNow:yyyyMMdd-HHmmss}.json");
            Assert.AreEqual("broken", kept, "읽을 수 없던 파일은 지우지 않고 보관한다");
            Assert.IsNotNull(_store.Read(LocalMockBackend.GuestFile));
        }

        [Test]
        public void WrongSecret_IsUnauthorized()
        {
            MeDto me = Boot(NewBackend());
            _store.Write(LocalMockBackend.GuestFile, "{\"playerId\":\"" + me.playerId + "\",\"guestSecret\":\"guess\"}");

            ApiResult<MeDto> result = null;
            NewBackend().Bootstrap(r => result = r);
            Assert.IsFalse(result.ok);
            Assert.AreEqual(ApiErrors.Unauthorized, result.errorCode);
        }

        [Test]
        public void CallsBeforeBoot_AreUnauthorized()
        {
            ApiResult<HuntSessionDto> result = null;
            NewBackend().StartHunt(ZoneCodes.NorthEast, r => result = r);
            Assert.IsFalse(result.ok);
            Assert.AreEqual(ApiErrors.Unauthorized, result.errorCode);
        }
    }
}
