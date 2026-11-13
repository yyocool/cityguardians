using System;
using System.Collections.Generic;
using CityGuardians.Domain;
using CityGuardians.Services;
using NUnit.Framework;

namespace CityGuardians.Tests
{
    /// <summary>테스트가 시간을 직접 움직이는 시계. 시작은 2026-11-13 12:00 (한국 시간).</summary>
    internal sealed class TestClock : IClock
    {
        public DateTime UtcNow { get; set; } = new DateTime(2026, 11, 13, 3, 0, 0, DateTimeKind.Utc);
        public void Advance(double seconds) => UtcNow = UtcNow.AddSeconds(seconds);
        public void NextDay() => UtcNow = UtcNow.AddDays(1);
        public void AddDays(int days) => UtcNow = UtcNow.AddDays(days);
    }

    /// <summary>모의 서버 테스트의 공통 준비: 저장소, 시계, 접속, GC 벌기.</summary>
    internal sealed class MockKit
    {
        public readonly MemoryTextStore Store = new MemoryTextStore();
        public readonly TestClock Clock = new TestClock();

        /// <summary>
        /// 기본 정의에서 드롭을 끄고(가방의 수를 정확히 세기 위해) 은행을 처음부터 열어 둔 것.
        /// 큰돈이 필요한 테스트를 위해 하루 사냥 한도도 넉넉히 둔다.
        /// </summary>
        public static GameDefs Defs()
        {
            GameDefs defs = DefaultDefs.Create();
            defs.FindEnemy(EnemyCodes.MalfunctionRobot).dropChance = 0f;
            defs.bank.unlockLevel = 1;
            defs.economy.dailyHuntGcLimit = 1000000;
            return defs;
        }

        public LocalMockBackend NewBackend(GameDefs defs = null) => new LocalMockBackend(Store, Clock, defs ?? Defs(), 7);

        public static T Ok<T>(ApiResult<T> result)
        {
            Assert.IsNotNull(result, "응답 콜백이 불리지 않았다");
            Assert.IsTrue(result.ok, $"실패 응답: {result.errorCode} {result.message}");
            return result.data;
        }

        public static string Error<T>(ApiResult<T> result)
        {
            Assert.IsNotNull(result, "응답 콜백이 불리지 않았다");
            Assert.IsFalse(result.ok, "실패해야 하는 요청이 성공했다");
            return result.errorCode;
        }

        public static MeDto Boot(LocalMockBackend backend)
        {
            ApiResult<MeDto> result = null;
            backend.Bootstrap(r => result = r);
            return Ok(result);
        }

        public static MeDto Me(LocalMockBackend backend)
        {
            ApiResult<MeDto> result = null;
            backend.GetMe(r => result = r);
            return Ok(result);
        }

        public static BankDto Bank(LocalMockBackend backend)
        {
            ApiResult<BankDto> result = null;
            backend.GetBank(r => result = r);
            return Ok(result);
        }

        /// <summary>사냥으로 GC를 번다 (로봇 한 대 10 GC). 10의 배수로, 얼마든지. 시계가 조금씩 간다.</summary>
        public void Earn(LocalMockBackend backend, int gc)
        {
            Assert.AreEqual(0, gc % 10, "테스트 준비: 10 GC 단위로 번다");
            while (gc > 0)
            {
                int chunk = Math.Min(240, gc);
                ApiResult<HuntSessionDto> started = null;
                backend.StartHunt(ZoneCodes.NorthEast, r => started = r);
                string session = Ok(started).sessionId;
                Clock.Advance(60);
                ApiResult<HuntReportDto> report = null;
                backend.ReportHunt(session, 1, new List<KillCount> { new KillCount(EnemyCodes.MalfunctionRobot, chunk / 10) },
                    r => report = r);
                Assert.AreEqual(chunk, Ok(report).rewardGc, "테스트 준비: 사냥 보상");
                gc -= chunk;
            }
        }

        public static ApiResult<PurchaseResultDto> Buy(LocalMockBackend backend, string shop, string itemCode, string requestId)
        {
            ApiResult<PurchaseResultDto> result = null;
            backend.BuyItem(shop, itemCode, requestId, r => result = r);
            return result;
        }

        public static ApiResult<SellResultDto> Sell(LocalMockBackend backend, string shop, string itemCode, string requestId)
        {
            ApiResult<SellResultDto> result = null;
            backend.SellItem(shop, itemCode, requestId, r => result = r);
            return result;
        }

        public static ApiResult<EquipResultDto> Equip(LocalMockBackend backend, string slot, string itemCode)
        {
            ApiResult<EquipResultDto> result = null;
            backend.EquipItem(slot, itemCode, r => result = r);
            return result;
        }

        public static ApiResult<RoomDto> Place(LocalMockBackend backend, string id, int x, int z, int rot = 0)
        {
            ApiResult<RoomDto> result = null;
            backend.PlaceFurniture(id, x, z, rot, r => result = r);
            return result;
        }

        public static ApiResult<RoomDto> StoreAway(LocalMockBackend backend, string id)
        {
            ApiResult<RoomDto> result = null;
            backend.StoreFurniture(id, r => result = r);
            return result;
        }

        public static ApiResult<BankTransferDto> Deposit(LocalMockBackend backend, long amount, string requestId)
        {
            ApiResult<BankTransferDto> result = null;
            backend.BankDeposit(amount, requestId, r => result = r);
            return result;
        }

        public static ApiResult<TermResultDto> OpenTerm(LocalMockBackend backend, string productCode, long amount, string requestId)
        {
            ApiResult<TermResultDto> result = null;
            backend.OpenTermDeposit(productCode, amount, requestId, r => result = r);
            return result;
        }

        public static ApiResult<TermResultDto> CancelTerm(LocalMockBackend backend, string productCode, string requestId)
        {
            ApiResult<TermResultDto> result = null;
            backend.CancelTermDeposit(productCode, requestId, r => result = r);
            return result;
        }
    }
}
