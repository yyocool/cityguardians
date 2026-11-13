using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using CityGuardians.Domain;
using CityGuardians.Services;
using NUnit.Framework;

namespace CityGuardians.Tests
{
    /// <summary>은행 규칙: 입출금 검사와 이자 계산 (docs/ECONOMY_SYSTEM.md 4장). 실제 서버가 같은 규칙을 지켜야 한다.</summary>
    public sealed class BankRulesTests
    {
        private BankConfig _config;

        [SetUp]
        public void SetUp() => _config = DefaultDefs.Create().bank;

        private static BankAccountState Account(long balance, long dayLow, string date) =>
            new BankAccountState { balance = balance, dayLow = dayLow, interestDate = date };

        [Test]
        public void Defaults_AreTheDocumentedFirstValues()
        {
            Assert.AreEqual(100, _config.dailyInterestBp, "하루 1%");
            Assert.AreEqual(10000, _config.interestCap);
            Assert.AreEqual(3, _config.unlockLevel);
            Assert.AreEqual(100, BankRules.DailyInterest(long.MaxValue / 20000, _config), "한도까지 채웠을 때 하루 이자는 100 GC");
            // 코드에 적어 둔 기본값과 초기 콘텐츠 값이 같아야 한다.
            Assert.AreEqual(_config.dailyInterestBp, new BankConfig().dailyInterestBp);
            Assert.AreEqual(_config.interestCap, new BankConfig().interestCap);
        }

        [Test]
        public void DailyInterest_RoundsDown_AndStopsAtTheCap()
        {
            Assert.AreEqual(0, BankRules.DailyInterest(0, _config));
            Assert.AreEqual(0, BankRules.DailyInterest(-500, _config));
            Assert.AreEqual(0, BankRules.DailyInterest(99, _config), "1 GC 미만은 버린다");
            Assert.AreEqual(1, BankRules.DailyInterest(100, _config));
            Assert.AreEqual(1, BankRules.DailyInterest(199, _config));
            Assert.AreEqual(5, BankRules.DailyInterest(500, _config));
            Assert.AreEqual(12, BankRules.DailyInterest(1234, _config));
            Assert.AreEqual(100, BankRules.DailyInterest(10000, _config));
            Assert.AreEqual(100, BankRules.DailyInterest(25000, _config), "한도를 넘는 금액에는 이자가 붙지 않는다");

            // 이율이 낮은 설정에서도 같은 식이다 (1만분의 1 단위).
            var low = new BankConfig { dailyInterestBp = 10, interestCap = 100000 };
            Assert.AreEqual(0, BankRules.DailyInterest(999, low));
            Assert.AreEqual(1, BankRules.DailyInterest(1000, low));
            Assert.AreEqual(12, BankRules.DailyInterest(12345, low));

            Assert.AreEqual(0, BankRules.DailyInterest(5000, new BankConfig { dailyInterestBp = 0 }));
            Assert.AreEqual(0, BankRules.DailyInterest(5000, null));
        }

        [Test]
        public void BalanceForOneGc_TellsWhereInterestStarts()
        {
            Assert.AreEqual(100, BankRules.BalanceForOneGc(_config));
            Assert.AreEqual(1000, BankRules.BalanceForOneGc(new BankConfig { dailyInterestBp = 10, interestCap = 100000 }));
            Assert.AreEqual(1, BankRules.DailyInterest(BankRules.BalanceForOneGc(_config), _config));
            Assert.AreEqual(0, BankRules.DailyInterest(BankRules.BalanceForOneGc(_config) - 1, _config));

            Assert.AreEqual(334, BankRules.BalanceForOneGc(new BankConfig { dailyInterestBp = 30 }));
            Assert.AreEqual(0, BankRules.BalanceForOneGc(new BankConfig { dailyInterestBp = 0 }), "이자가 없는 설정");
            Assert.AreEqual(0, BankRules.BalanceForOneGc(new BankConfig { dailyInterestBp = 10, interestCap = 500 }),
                "한도가 낮아 1 GC에 이를 수 없다");
        }

        [Test]
        public void CheckDepositAndWithdraw_LevelAmountAndBalance()
        {
            Assert.AreEqual(BankReasons.Locked, BankRules.CheckDeposit(_config, 2, 100, 500));
            Assert.AreEqual(BankReasons.Locked, BankRules.CheckWithdraw(_config, 2, 100, 500));

            Assert.IsNull(BankRules.CheckDeposit(_config, 3, 500, 500));
            Assert.AreEqual(BankReasons.InsufficientWallet, BankRules.CheckDeposit(_config, 3, 501, 500));
            Assert.AreEqual(BankReasons.InvalidAmount, BankRules.CheckDeposit(_config, 3, 0, 500));
            Assert.AreEqual(BankReasons.InvalidAmount, BankRules.CheckDeposit(_config, 3, -100, 500));
            Assert.AreEqual(BankReasons.InvalidAmount, BankRules.CheckDeposit(_config, 3, _config.maxTransfer + 1, long.MaxValue));

            Assert.IsNull(BankRules.CheckWithdraw(_config, 3, 500, 500));
            Assert.AreEqual(BankReasons.InsufficientAccount, BankRules.CheckWithdraw(_config, 3, 501, 500));
            Assert.AreEqual(BankReasons.InvalidAmount, BankRules.CheckWithdraw(_config, 3, 0, 500));
            Assert.AreEqual(BankReasons.InvalidAmount, BankRules.CheckWithdraw(_config, 3, -1, 500));
        }

        [Test]
        public void Deposit_DoesNotRaiseTodaysLowestBalance_ButWithdrawLowersIt()
        {
            BankAccountState account = Account(5000, 5000, "2026-11-13");
            BankRules.Deposit(account, 3000);
            Assert.AreEqual(8000, account.balance);
            Assert.AreEqual(5000, account.dayLow, "오늘 맡긴 돈에는 오늘 치 이자가 붙지 않는다");
            Assert.AreEqual(50, BankRules.InterestTomorrow(account, _config));
            Assert.AreEqual(80, BankRules.InterestWhenSettled(account, _config));

            BankRules.Withdraw(account, 2000);
            Assert.AreEqual(6000, account.balance);
            Assert.AreEqual(5000, account.dayLow, "최저 잔액보다 위에서 찾으면 그대로다");

            BankRules.Withdraw(account, 4000);
            Assert.AreEqual(2000, account.balance);
            Assert.AreEqual(2000, account.dayLow);

            // 다시 넣어도 오늘의 최저 잔액은 돌아오지 않는다.
            BankRules.Deposit(account, 4000);
            Assert.AreEqual(2000, account.dayLow);
            Assert.AreEqual(20, BankRules.InterestTomorrow(account, _config));
        }

        [Test]
        public void Settle_FirstUse_StartsCountingToday_AndPaysNothing()
        {
            BankAccountState account = Account(0, 0, null);
            List<InterestPayment> paid = BankRules.Settle(account, "2026-11-13", _config);
            Assert.AreEqual(0, paid.Count);
            Assert.AreEqual("2026-11-13", account.interestDate);
        }

        [Test]
        public void Settle_PaysYesterdayOnce_OnTheLowestBalance()
        {
            BankAccountState account = Account(8000, 5000, "2026-11-13");

            Assert.AreEqual(0, BankRules.Settle(account, "2026-11-13", _config).Count, "같은 날에는 지급하지 않는다");
            Assert.AreEqual(8000, account.balance);

            List<InterestPayment> paid = BankRules.Settle(account, "2026-11-14", _config);
            Assert.AreEqual(1, paid.Count);
            Assert.AreEqual("2026-11-13", paid[0].date, "어느 날의 이자인지 남긴다");
            Assert.AreEqual(50, paid[0].amount, "최저 잔액 5,000에 붙는다");
            Assert.AreEqual(8050, account.balance);
            Assert.AreEqual(8050, account.dayLow, "새 날은 지금 잔액에서 시작한다");
            Assert.AreEqual("2026-11-14", account.interestDate);

            Assert.AreEqual(0, BankRules.Settle(account, "2026-11-14", _config).Count, "다시 불러도 한 번만 지급된다");
            Assert.AreEqual(8050, account.balance);
        }

        [Test]
        public void Settle_CatchesUpMissedDays_OneByOne_WithCompounding()
        {
            BankAccountState account = Account(9900, 9900, "2026-12-30");
            List<InterestPayment> paid = BankRules.Settle(account, "2027-01-02", _config);

            // 12/30: 99 (→ 9,999), 12/31: 99 (→ 10,098), 1/1: 100 (한도 10,000까지만)
            Assert.AreEqual(3, paid.Count);
            Assert.AreEqual("2026-12-30", paid[0].date);
            Assert.AreEqual(99, paid[0].amount);
            Assert.AreEqual("2026-12-31", paid[1].date);
            Assert.AreEqual(99, paid[1].amount);
            Assert.AreEqual("2027-01-01", paid[2].date, "해가 바뀌어도 날짜가 이어진다");
            Assert.AreEqual(100, paid[2].amount);
            Assert.AreEqual(9900 + 298, account.balance);
            Assert.AreEqual("2027-01-02", account.interestDate);
        }

        [Test]
        public void Settle_SmallBalance_PaysNothing_ButStillMovesTheDate()
        {
            BankAccountState account = Account(50, 50, "2026-11-13");
            Assert.AreEqual(0, BankRules.Settle(account, "2026-11-20", _config).Count);
            Assert.AreEqual(50, account.balance);
            Assert.AreEqual("2026-11-20", account.interestDate);
        }

        [Test]
        public void Settle_WhenTheClockGoesBack_DoesNothing()
        {
            BankAccountState account = Account(5000, 5000, "2026-11-13");
            Assert.AreEqual(0, BankRules.Settle(account, "2026-11-10", _config).Count);
            Assert.AreEqual(5000, account.balance);
            Assert.AreEqual("2026-11-13", account.interestDate, "날짜를 되돌리면 같은 날의 이자를 두 번 받을 수 있다");
            Assert.Throws<ArgumentException>(() => BankRules.Settle(account, "어제", _config));
        }

        [Test]
        public void Settle_AfterAVeryLongTime_IsBounded()
        {
            BankAccountState account = Account(100000, 100000, "1990-01-01");
            List<InterestPayment> paid = BankRules.Settle(account, "2026-11-13", _config);
            Assert.AreEqual(BankRules.MaxCatchUpDays, paid.Count);
            Assert.AreEqual(100000 + 100L * BankRules.MaxCatchUpDays, account.balance);
        }
    }

    /// <summary>모의 서버의 은행: 입출금은 원장 두 줄로 남고, 같은 요청은 한 번만, 이자는 하루에 한 번만.</summary>
    public sealed class LocalMockBackendBankTests
    {
        private sealed class FakeClock : IClock
        {
            /// <summary>한국 시간 2026-11-13 12:00.</summary>
            public DateTime UtcNow { get; set; } = new DateTime(2026, 11, 13, 3, 0, 0, DateTimeKind.Utc);
            public void Advance(double seconds) => UtcNow = UtcNow.AddSeconds(seconds);
            public void NextDay() => UtcNow = UtcNow.AddDays(1);
        }

        private MemoryTextStore _store;
        private FakeClock _clock;

        [SetUp]
        public void SetUp()
        {
            _store = new MemoryTextStore();
            _clock = new FakeClock();
        }

        private static GameDefs Defs(int unlockLevel = 1, long interestCap = 10000)
        {
            GameDefs defs = DefaultDefs.Create();
            defs.FindEnemy(EnemyCodes.MalfunctionRobot).dropChance = 0f;
            defs.bank.unlockLevel = unlockLevel;
            defs.bank.interestCap = interestCap;
            return defs;
        }

        private LocalMockBackend NewBackend(GameDefs defs = null) => new LocalMockBackend(_store, _clock, defs ?? Defs(), 7);

        private static T Ok<T>(ApiResult<T> result)
        {
            Assert.IsNotNull(result, "응답 콜백이 불리지 않았다");
            Assert.IsTrue(result.ok, $"실패 응답: {result.errorCode} {result.message}");
            return result.data;
        }

        private static string Error<T>(ApiResult<T> result)
        {
            Assert.IsNotNull(result, "응답 콜백이 불리지 않았다");
            Assert.IsFalse(result.ok, "실패해야 하는 요청이 성공했다");
            return result.errorCode;
        }

        private static MeDto Boot(LocalMockBackend backend)
        {
            ApiResult<MeDto> result = null;
            backend.Bootstrap(r => result = r);
            return Ok(result);
        }

        private static MeDto Me(LocalMockBackend backend)
        {
            ApiResult<MeDto> result = null;
            backend.GetMe(r => result = r);
            return Ok(result);
        }

        private static BankDto Bank(LocalMockBackend backend)
        {
            ApiResult<BankDto> result = null;
            backend.GetBank(r => result = r);
            return Ok(result);
        }

        private static ApiResult<BankTransferDto> Deposit(LocalMockBackend backend, long amount, string requestId)
        {
            ApiResult<BankTransferDto> result = null;
            backend.BankDeposit(amount, requestId, r => result = r);
            return result;
        }

        private static ApiResult<BankTransferDto> Withdraw(LocalMockBackend backend, long amount, string requestId)
        {
            ApiResult<BankTransferDto> result = null;
            backend.BankWithdraw(amount, requestId, r => result = r);
            return result;
        }

        /// <summary>사냥으로 GC를 번다 (로봇 한 대 10 GC, 경험치 10). 한 번에 240 GC까지.</summary>
        private void Earn(LocalMockBackend backend, int gc)
        {
            ApiResult<HuntSessionDto> started = null;
            backend.StartHunt(ZoneCodes.NorthEast, r => started = r);
            string session = Ok(started).sessionId;
            _clock.Advance(60);
            ApiResult<HuntReportDto> report = null;
            backend.ReportHunt(session, 1, new List<KillCount> { new KillCount(EnemyCodes.MalfunctionRobot, gc / 10) },
                r => report = r);
            Assert.AreEqual(gc, Ok(report).rewardGc, "테스트 준비: 사냥 보상");
        }

        /// <summary>전입 축하금과 사냥으로 넉넉한 GC를 만든다. 이자를 눈으로 확인할 수 있는 금액이 필요하다.</summary>
        private long EarnALot(LocalMockBackend backend)
        {
            for (int i = 0; i < 12; i++) Earn(backend, 240);
            return Me(backend).gc;
        }

        [Test]
        public void NewGuest_HasAnEmptyAccount_AndTheBankIsLockedUntilLevel3()
        {
            LocalMockBackend backend = NewBackend(Defs(unlockLevel: 3));
            MeDto me = Boot(backend);
            Assert.AreEqual(0, me.bankGc);
            Assert.AreEqual(0, me.bankInterestTotal);

            Earn(backend, 100);
            Assert.AreEqual(2, Me(backend).level, "테스트 준비: 아직 레벨 2");

            BankDto bank = Bank(backend);
            Assert.IsFalse(bank.unlocked);
            Assert.AreEqual(3, bank.unlockLevel);
            Assert.AreEqual(100, bank.gc);
            Assert.AreEqual(0, bank.bankGc);

            Assert.AreEqual(ApiErrors.Locked, Error(Deposit(backend, 100, "d1")));
            Assert.AreEqual(ApiErrors.Locked, Error(Withdraw(backend, 100, "w1")));
            Assert.AreEqual(100, Me(backend).gc);
            Assert.AreEqual(0, backend.CurrentBankLedger.Count);

            // 레벨 3이 되면 열린다.
            Earn(backend, 140);
            Assert.AreEqual(3, Me(backend).level);
            Assert.IsTrue(Bank(backend).unlocked);
            Assert.AreEqual(100, Ok(Deposit(backend, 100, "d1")).bank.bankGc, "거절됐던 요청 번호는 다시 쓸 수 있다");
        }

        [Test]
        public void Deposit_MovesGcFromWalletToAccount_WithOneLedgerRowOnEachSide()
        {
            LocalMockBackend backend = NewBackend();
            Boot(backend);
            Earn(backend, 240);

            BankTransferDto moved = Ok(Deposit(backend, 100, "req-dep"));
            Assert.AreEqual(100, moved.amount);
            Assert.IsFalse(moved.duplicate);
            Assert.AreEqual(140, moved.bank.gc);
            Assert.AreEqual(100, moved.bank.bankGc);

            MeDto me = Me(backend);
            Assert.AreEqual(140, me.gc);
            Assert.AreEqual(100, me.bankGc);

            // 지갑 원장: 사냥 +240, 입금 −100
            Assert.AreEqual(2, backend.CurrentLedger.Count);
            LedgerEntry walletRow = backend.CurrentLedger[1];
            Assert.AreEqual(-100, walletRow.delta);
            Assert.AreEqual(140, walletRow.balanceAfter);
            Assert.AreEqual(LedgerReasons.BankDeposit, walletRow.reason);
            Assert.AreEqual("req-dep", walletRow.reference);

            // 통장 원장: 입금 +100
            Assert.AreEqual(1, backend.CurrentBankLedger.Count);
            LedgerEntry accountRow = backend.CurrentBankLedger[0];
            Assert.AreEqual(100, accountRow.delta);
            Assert.AreEqual(100, accountRow.balanceAfter);
            Assert.AreEqual(LedgerReasons.BankDeposit, accountRow.reason);
            Assert.AreEqual("req-dep", accountRow.reference);

            Assert.IsNull(backend.FindLedgerInconsistency());
        }

        [Test]
        public void Withdraw_MovesGcBack_AndTheTotalNeverChanges()
        {
            LocalMockBackend backend = NewBackend();
            Boot(backend);
            Earn(backend, 240);
            Ok(Deposit(backend, 200, "d1"));

            BankTransferDto moved = Ok(Withdraw(backend, 150, "w1"));
            Assert.AreEqual(150, moved.amount);
            Assert.AreEqual(190, moved.bank.gc);
            Assert.AreEqual(50, moved.bank.bankGc);
            Assert.AreEqual(240, moved.bank.gc + moved.bank.bankGc, "옮기기만 했으니 합은 그대로다");

            Assert.AreEqual(LedgerReasons.BankWithdraw, backend.CurrentLedger[2].reason);
            Assert.AreEqual(150, backend.CurrentLedger[2].delta);
            Assert.AreEqual(LedgerReasons.BankWithdraw, backend.CurrentBankLedger[1].reason);
            Assert.AreEqual(-150, backend.CurrentBankLedger[1].delta);
            Assert.AreEqual(50, backend.CurrentBankLedger[1].balanceAfter);
            Assert.IsNull(backend.FindLedgerInconsistency());

            // 거래 내역은 새것이 앞에 온다.
            BankDto bank = Bank(backend);
            Assert.AreEqual(2, bank.recent.Count);
            Assert.AreEqual(LedgerReasons.BankWithdraw, bank.recent[0].reason);
            Assert.AreEqual(-150, bank.recent[0].delta);
            Assert.AreEqual(50, bank.recent[0].balanceAfter);
            Assert.AreEqual("2026-11-13", bank.recent[0].date);
            Assert.AreEqual(LedgerReasons.BankDeposit, bank.recent[1].reason);
        }

        [Test]
        public void Transfers_RejectWhatCannotBeMoved_AndChangeNothing()
        {
            LocalMockBackend backend = NewBackend();
            Boot(backend);
            Earn(backend, 240);
            Ok(Deposit(backend, 100, "d1"));

            Assert.AreEqual(ApiErrors.InsufficientFunds, Error(Deposit(backend, 141, "a")), "지갑보다 많이");
            Assert.AreEqual(ApiErrors.InsufficientFunds, Error(Withdraw(backend, 101, "b")), "통장보다 많이");
            Assert.AreEqual(ApiErrors.InvalidRequest, Error(Deposit(backend, 0, "c")));
            Assert.AreEqual(ApiErrors.InvalidRequest, Error(Deposit(backend, -50, "d")), "음수로 반대 방향 거래를 만들 수 없다");
            Assert.AreEqual(ApiErrors.InvalidRequest, Error(Withdraw(backend, -50, "e")));
            Assert.AreEqual(ApiErrors.InvalidRequest, Error(Deposit(backend, 10, "")), "요청 번호가 없으면 받지 않는다");
            Assert.AreEqual(ApiErrors.InvalidRequest, Error(Withdraw(backend, 10, null)));

            MeDto me = Me(backend);
            Assert.AreEqual(140, me.gc);
            Assert.AreEqual(100, me.bankGc);
            Assert.AreEqual(2, backend.CurrentLedger.Count);
            Assert.AreEqual(1, backend.CurrentBankLedger.Count);
            Assert.IsNull(backend.FindLedgerInconsistency());
        }

        [Test]
        public void ResendingTheSameTransfer_MovesOnlyOnce_EvenAfterARestart()
        {
            LocalMockBackend backend = NewBackend();
            Boot(backend);
            Earn(backend, 240);

            Ok(Deposit(backend, 100, "req-1"));
            BankTransferDto again = Ok(Deposit(backend, 100, "req-1"));
            Assert.IsTrue(again.duplicate);
            Assert.AreEqual(100, again.amount);
            Assert.AreEqual(140, again.bank.gc);
            Assert.AreEqual(100, again.bank.bankGc);

            LocalMockBackend restarted = NewBackend();
            Boot(restarted);
            Assert.IsTrue(Ok(Deposit(restarted, 100, "req-1")).duplicate);
            Assert.AreEqual(100, Me(restarted).bankGc);

            // 같은 번호를 다른 거래에 쓰면 거절한다.
            Assert.AreEqual(ApiErrors.Conflict, Error(Deposit(restarted, 50, "req-1")));
            Assert.AreEqual(ApiErrors.Conflict, Error(Withdraw(restarted, 100, "req-1")));
            Assert.AreEqual(100, Me(restarted).bankGc);

            // 새 번호면 새 거래다.
            Assert.AreEqual(200, Ok(Deposit(restarted, 100, "req-2")).bank.bankGc);
            Assert.AreEqual(2, restarted.CurrentBankLedger.Count);
            Assert.IsNull(restarted.FindLedgerInconsistency());
        }

        [Test]
        public void Interest_ComesAtKoreanMidnight_FromTheDayAfterTheDeposit()
        {
            LocalMockBackend backend = NewBackend();
            Boot(backend);
            long earned = EarnALot(backend);
            Assert.GreaterOrEqual(earned, 2500, "테스트 준비: 이자가 보일 만큼의 GC");

            // 11월 13일 (한국 시간): 2,500 GC를 맡긴다.
            BankDto day1 = Ok(Deposit(backend, 2500, "d1")).bank;
            Assert.AreEqual(0, day1.interestTomorrow, "오늘 맡긴 돈에는 오늘 치 이자가 붙지 않는다");
            Assert.AreEqual(25, day1.interestWhenSettled);
            Assert.AreEqual(100, day1.dailyInterestBp);
            Assert.AreEqual(100, day1.balanceForOneGc);

            // 한국 시간 23:59: 아직 같은 날이다.
            _clock.UtcNow = new DateTime(2026, 11, 13, 14, 59, 0, DateTimeKind.Utc);
            Assert.AreEqual(0, Bank(backend).interestTotal);

            // 한국 시간 11월 14일 00:00: 13일 치를 정산한다 (최저 잔액 0 → 이자 없음).
            _clock.UtcNow = new DateTime(2026, 11, 13, 15, 0, 0, DateTimeKind.Utc);
            BankDto day2 = Bank(backend);
            Assert.AreEqual(2500, day2.bankGc);
            Assert.AreEqual(0, day2.interestTotal);
            Assert.AreEqual(25, day2.interestTomorrow, "이제 내일은 이자를 받는다");

            // 11월 15일: 14일 치 이자 25 GC.
            _clock.NextDay();
            BankDto day3 = Bank(backend);
            Assert.AreEqual(2525, day3.bankGc);
            Assert.AreEqual(25, day3.interestTotal);
            Assert.AreEqual(earned - 2500, day3.gc, "이자는 지갑이 아니라 통장에 들어온다");

            LedgerEntry row = backend.CurrentBankLedger[backend.CurrentBankLedger.Count - 1];
            Assert.AreEqual(LedgerReasons.BankInterest, row.reason);
            Assert.AreEqual(25, row.delta);
            Assert.AreEqual(2525, row.balanceAfter);
            Assert.AreEqual("2026-11-14", row.reference, "어느 날의 이자인지 남는다");
            Assert.AreEqual(LedgerReasons.BankInterest, day3.recent[0].reason);
            Assert.AreEqual("2026-11-14", day3.recent[0].date);

            // 같은 날에는 몇 번을 물어도, 껐다 켜도 한 번만 받는다.
            Bank(backend);
            Me(backend);
            LocalMockBackend restarted = NewBackend();
            MeDto me = Boot(restarted);
            Assert.AreEqual(2525, me.bankGc);
            Assert.AreEqual(25, me.bankInterestTotal);
            Assert.AreEqual(2525, Bank(restarted).bankGc);
            Assert.IsNull(restarted.FindLedgerInconsistency());
        }

        [Test]
        public void Interest_ForDaysAway_IsPaidOncePerDay_WhenThePlayerComesBack()
        {
            LocalMockBackend backend = NewBackend();
            Boot(backend);
            EarnALot(backend);
            Ok(Deposit(backend, 2000, "d1"));

            // 열흘 뒤에 돌아와 게임을 켠다: 14일~22일 치 아홉 번 (13일은 맡긴 날).
            // 이자에도 이자가 붙는다: 20 GC씩 다섯 번(2,100이 됨), 그 뒤로 21 GC씩 네 번.
            _clock.UtcNow = _clock.UtcNow.AddDays(10);
            LocalMockBackend restarted = NewBackend();
            MeDto me = Boot(restarted);
            Assert.AreEqual(2184, me.bankGc);
            Assert.AreEqual(184, me.bankInterestTotal);

            int interestRows = 0;
            foreach (LedgerEntry entry in restarted.CurrentBankLedger)
                if (entry.reason == LedgerReasons.BankInterest) interestRows++;
            Assert.AreEqual(9, interestRows);
            Assert.IsNull(restarted.FindLedgerInconsistency());

            Assert.AreEqual(2184, Bank(restarted).bankGc, "다시 물어도 더 받지 않는다");
        }

        [Test]
        public void Interest_UsesTheLowestBalanceOfTheDay_SoQuickInAndOutEarnsNothing()
        {
            LocalMockBackend backend = NewBackend();
            Boot(backend);
            EarnALot(backend);

            // 자정 직전에 넣었다가 직후에 빼기.
            _clock.UtcNow = new DateTime(2026, 11, 13, 14, 59, 0, DateTimeKind.Utc);
            Ok(Deposit(backend, 2500, "d1"));
            _clock.UtcNow = new DateTime(2026, 11, 13, 15, 1, 0, DateTimeKind.Utc);
            BankDto after = Ok(Withdraw(backend, 2500, "w1")).bank;
            Assert.AreEqual(0, after.bankGc);
            Assert.AreEqual(0, after.interestTotal);

            _clock.NextDay();
            Assert.AreEqual(0, Bank(backend).interestTotal, "잠깐 넣었다 뺀 돈에는 이자가 없다");

            // 하루를 온전히 둔 돈에서 일부를 찾으면, 그날 이자는 남은 돈에만 붙는다.
            Ok(Deposit(backend, 2500, "d2"));
            _clock.NextDay();
            Assert.AreEqual(25, Bank(backend).interestTomorrow);
            BankDto lowered = Ok(Withdraw(backend, 1400, "w2")).bank;
            Assert.AreEqual(11, lowered.interestTomorrow);
            BankDto refilled = Ok(Deposit(backend, 1400, "d3")).bank;
            Assert.AreEqual(11, refilled.interestTomorrow, "다시 넣어도 오늘의 최저 잔액은 돌아오지 않는다");
            Assert.AreEqual(25, refilled.interestWhenSettled);

            _clock.NextDay();
            BankDto next = Bank(backend);
            Assert.AreEqual(2511, next.bankGc);
            Assert.AreEqual(11, next.interestTotal);
            Assert.IsNull(backend.FindLedgerInconsistency());
        }

        [Test]
        public void Interest_StopsGrowingAtTheCap()
        {
            LocalMockBackend backend = NewBackend(Defs(interestCap: 1500));
            Boot(backend);
            EarnALot(backend);
            Ok(Deposit(backend, 2800, "d1"));

            BankDto bank = Bank(backend);
            Assert.AreEqual(1500, bank.interestCap);
            Assert.AreEqual(15, bank.interestWhenSettled, "한도 1,500까지만 이자가 붙는다");

            _clock.NextDay();
            _clock.NextDay();
            Assert.AreEqual(2815, Bank(backend).bankGc);
        }

        [Test]
        public void TransferAfterMidnight_SettlesYesterdayFirst()
        {
            LocalMockBackend backend = NewBackend();
            Boot(backend);
            EarnALot(backend);
            Ok(Deposit(backend, 2500, "d1"));
            _clock.NextDay();
            Bank(backend);

            // 14일을 온전히 둔 뒤, 15일의 첫 요청이 "전부 찾기"여도 14일 치 이자는 받는다.
            _clock.NextDay();
            BankDto after = Ok(Withdraw(backend, 2500, "w1")).bank;
            Assert.AreEqual(25, after.interestTotal);
            Assert.AreEqual(25, after.bankGc, "이자 25 GC가 남는다");
            Assert.IsNull(backend.FindLedgerInconsistency());
        }

        [Test]
        public void SaveFromBeforeTheBank_GetsAnEmptyAccount_AndKeepsEverythingElse()
        {
            LocalMockBackend old = NewBackend();
            MeDto created = Boot(old);
            Earn(old, 240);

            // 은행 기능이 생기기 전의 저장 데이터: 통장 항목이 없다.
            string json = _store.Read(LocalMockBackend.ServerFile);
            string stripped = Regex.Replace(json, "\"bankLedger\":\\[[^\\]]*\\],?", "");
            stripped = Regex.Replace(stripped, "\"bank[A-Za-z]+\":(\"[^\"]*\"|-?\\d+),?", "");
            // 마지막 항목을 지웠다면 남은 쉼표를 정리한다.
            stripped = Regex.Replace(stripped, ",\\s*}", "}");
            StringAssert.DoesNotContain("bank", stripped);
            _store.Write(LocalMockBackend.ServerFile, stripped);

            LocalMockBackend upgraded = NewBackend();
            MeDto me = Boot(upgraded);
            Assert.AreEqual(created.playerId, me.playerId);
            Assert.AreEqual(240, me.gc);
            Assert.AreEqual(0, me.bankGc);
            Assert.IsNull(upgraded.FindLedgerInconsistency());

            Assert.AreEqual(100, Ok(Deposit(upgraded, 100, "d1")).bank.bankGc);
            Assert.IsNull(upgraded.FindLedgerInconsistency());
        }

        [Test]
        public void Session_AnnouncesNewInterestOnce_AndRemembersItAfterARestart()
        {
            LocalMockBackend backend = NewBackend(Defs(unlockLevel: 3));
            GameSession session = GameSession.Start(backend, Boot(backend));
            try
            {
                Assert.IsFalse(session.BankUnlocked);
                EarnALot(backend);
                session.ApplyMe(Me(backend));
                Assert.IsTrue(session.BankUnlocked);
                Assert.AreEqual(0, session.TakeUnseenInterest());

                BankDto bank = Ok(Deposit(backend, 2500, "d1")).bank;
                session.ApplyBank(bank);
                Assert.AreEqual(2500, session.Me.bankGc);
                Assert.AreEqual(bank.gc, session.Me.gc);

                _clock.NextDay();
                _clock.NextDay();
                session.ApplyMe(Me(backend));
                Assert.AreEqual(2525, session.Me.bankGc);
                Assert.AreEqual(25, session.TakeUnseenInterest(), "새로 들어온 이자");
                Assert.AreEqual(0, session.TakeUnseenInterest(), "한 번만 알린다");

                // 껐다 켜도 같은 이자를 다시 알리지 않는다.
                LocalMockBackend restarted = NewBackend(Defs(unlockLevel: 3));
                GameSession restored = GameSession.Start(restarted, Boot(restarted));
                Assert.AreEqual(0, restored.TakeUnseenInterest());

                // 다음 날의 이자는 새로 알린다.
                _clock.NextDay();
                restored.ApplyMe(Me(restarted));
                Assert.AreEqual(25, restored.TakeUnseenInterest());
            }
            finally
            {
                GameSession.End();
            }
        }
    }
}
