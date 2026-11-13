using System.Collections.Generic;
using CityGuardians.Domain;
using CityGuardians.Services;
using NUnit.Framework;

namespace CityGuardians.Tests
{
    /// <summary>정기예금 규칙 (docs/ECONOMY_SYSTEM.md 4.3). 실제 서버가 같은 규칙을 지켜야 한다.</summary>
    public sealed class TermDepositRulesTests
    {
        private BankConfig _config;
        private TermProductDef _week;
        private TermProductDef _month;

        [SetUp]
        public void SetUp()
        {
            _config = DefaultDefs.Create().bank;
            _week = _config.FindTerm(TermProductCodes.Days7);
            _month = _config.FindTerm(TermProductCodes.Days30);
        }

        private static BankAccountState Account(long balance, string date) =>
            new BankAccountState { balance = balance, dayLow = balance, interestDate = date };

        [Test]
        public void Defaults_AreTheDocumentedFirstValues()
        {
            Assert.AreEqual(2, _config.termProducts.Count);
            Assert.AreEqual(7, _week.days);
            Assert.AreEqual(1000, _week.rateBp, "7일에 10%");
            Assert.AreEqual(30, _month.days);
            Assert.AreEqual(5000, _month.rateBp, "30일에 50%");
            Assert.AreEqual(100, _week.minAmount);
            Assert.AreEqual(10000, _week.maxAmount);
            Assert.AreEqual(100, _month.minAmount);
            Assert.AreEqual(10000, _month.maxAmount);
            Assert.IsNull(_config.FindTerm("term_nothing"));
        }

        [Test]
        public void Defaults_LockingMoneyUpPaysMoreThanLeavingItInTheAccount_AndLongerPaysMorePerDay()
        {
            foreach (TermProductDef product in _config.termProducts)
            {
                foreach (long amount in new long[] { 100, 1000, 10000 })
                {
                    // 같은 돈을 같은 기간 보통예금에 두었을 때 (이자에 이자가 붙는다).
                    BankAccountState account = Account(amount, "2026-01-01");
                    BankRules.Settle(account, BankRules.MaturityDate("2026-01-01", product.days), _config);
                    long savings = account.balance - amount;
                    Assert.Greater(BankRules.TermInterest(amount, product), savings, $"{product.code} {amount} GC");
                }
            }

            double weekPerDay = BankRules.TermInterest(10000, _week) / (double)_week.days;
            double monthPerDay = BankRules.TermInterest(10000, _month) / (double)_month.days;
            Assert.Greater(monthPerDay, weekPerDay, "오래 묶을수록 하루로 친 이자도 많아야 한다");
        }

        [Test]
        public void TermInterest_IsAFractionOfThePrincipal_RoundedDown()
        {
            Assert.AreEqual(10, BankRules.TermInterest(100, _week));
            Assert.AreEqual(100, BankRules.TermInterest(1000, _week));
            Assert.AreEqual(123, BankRules.TermInterest(1234, _week), "1 GC 미만은 버린다");
            Assert.AreEqual(1000, BankRules.TermInterest(10000, _week));
            Assert.AreEqual(5000, BankRules.TermInterest(10000, _month));
            Assert.AreEqual(0, BankRules.TermInterest(0, _week));
            Assert.AreEqual(0, BankRules.TermInterest(-5, _week));
            Assert.AreEqual(0, BankRules.TermInterest(1000, null));
            Assert.AreEqual(0, BankRules.TermInterest(9, _week));
        }

        [Test]
        public void MaturityDate_CountsCalendarDays()
        {
            Assert.AreEqual("2026-11-20", BankRules.MaturityDate("2026-11-13", 7));
            Assert.AreEqual("2026-12-13", BankRules.MaturityDate("2026-11-13", 30));
            Assert.AreEqual("2027-01-04", BankRules.MaturityDate("2026-12-28", 7), "해를 넘긴다");
            Assert.AreEqual("2028-03-01", BankRules.MaturityDate("2028-02-23", 7), "윤년의 2월 29일을 지난다");

            TermDeposit term = BankRules.OpenTerm(_week, 1000, "2026-11-13");
            Assert.AreEqual(TermProductCodes.Days7, term.productCode);
            Assert.AreEqual(1000, term.principal);
            Assert.AreEqual(100, term.interest);
            Assert.AreEqual("2026-11-13", term.openedDate);
            Assert.AreEqual("2026-11-20", term.maturesDate);
            Assert.AreEqual(7, BankRules.DaysLeft(term, "2026-11-13"));
            Assert.AreEqual(1, BankRules.DaysLeft(term, "2026-11-19"));
            Assert.AreEqual(0, BankRules.DaysLeft(term, "2026-11-20"));
            Assert.AreEqual(0, BankRules.DaysLeft(term, "2026-12-25"));
        }

        [Test]
        public void CheckOpenTerm_Reasons()
        {
            var none = new List<TermDeposit>();
            Assert.IsNull(BankRules.CheckOpenTerm(_config, 3, TermProductCodes.Days7, 100, 100, none));
            Assert.IsNull(BankRules.CheckOpenTerm(_config, 3, TermProductCodes.Days7, 10000, 10000, none));
            Assert.AreEqual(BankReasons.Locked, BankRules.CheckOpenTerm(_config, 2, TermProductCodes.Days7, 100, 100, none));
            Assert.AreEqual(BankReasons.UnknownProduct, BankRules.CheckOpenTerm(_config, 3, "term_nothing", 100, 100, none));
            Assert.AreEqual(BankReasons.InvalidAmount, BankRules.CheckOpenTerm(_config, 3, TermProductCodes.Days7, 0, 100, none));
            Assert.AreEqual(BankReasons.InvalidAmount, BankRules.CheckOpenTerm(_config, 3, TermProductCodes.Days7, -100, 100, none));
            Assert.AreEqual(BankReasons.BelowMinimum, BankRules.CheckOpenTerm(_config, 3, TermProductCodes.Days7, 99, 1000, none));
            Assert.AreEqual(BankReasons.AboveMaximum, BankRules.CheckOpenTerm(_config, 3, TermProductCodes.Days7, 10001, 20000, none));
            Assert.AreEqual(BankReasons.InsufficientWallet, BankRules.CheckOpenTerm(_config, 3, TermProductCodes.Days7, 500, 499, none));

            var open = new List<TermDeposit> { BankRules.OpenTerm(_week, 500, "2026-11-13") };
            Assert.AreEqual(BankReasons.AlreadyOpen, BankRules.CheckOpenTerm(_config, 3, TermProductCodes.Days7, 100, 100, open),
                "상품마다 하나씩만 가입한다");
            Assert.IsNull(BankRules.CheckOpenTerm(_config, 3, TermProductCodes.Days30, 100, 100, open), "다른 상품은 가입할 수 있다");

            Assert.IsNull(BankRules.CheckCancelTerm(_config, TermProductCodes.Days7, open));
            Assert.AreEqual(BankReasons.NotOpen, BankRules.CheckCancelTerm(_config, TermProductCodes.Days30, open));
            Assert.AreEqual(BankReasons.UnknownProduct, BankRules.CheckCancelTerm(_config, "term_nothing", open));
        }

        [Test]
        public void SettleAll_PaysTheTermOnItsMaturityDay_InOrderWithTheDailyInterest()
        {
            BankAccountState account = Account(1000, "2026-11-13");
            var terms = new List<TermDeposit>
            {
                new TermDeposit
                {
                    productCode = TermProductCodes.Days7, principal = 1000, interest = 100,
                    openedDate = "2026-11-08", maturesDate = "2026-11-15",
                },
            };

            List<BankEvent> events = BankRules.SettleAll(account, terms, "2026-11-16", _config);

            // 13일 치 이자 10 → 14일 치 이자 10 → 15일이 되면서 만기 (1,000 + 100) → 15일 치 이자 21 (만기로 들어온 돈에도 붙는다).
            Assert.AreEqual(4, events.Count);
            Assert.AreEqual((BankEvent.Interest, "2026-11-13", 10L), (events[0].kind, events[0].date, events[0].amount));
            Assert.AreEqual((BankEvent.Interest, "2026-11-14", 10L), (events[1].kind, events[1].date, events[1].amount));
            Assert.AreEqual((BankEvent.TermMatured, "2026-11-15", 1000L), (events[2].kind, events[2].date, events[2].amount));
            Assert.AreEqual(100, events[2].termInterest);
            Assert.AreEqual(TermProductCodes.Days7, events[2].productCode);
            Assert.AreEqual((BankEvent.Interest, "2026-11-15", 21L), (events[3].kind, events[3].date, events[3].amount));

            Assert.AreEqual(1000 + 10 + 10 + 1100 + 21, account.balance);
            Assert.AreEqual(account.balance, account.dayLow);
            Assert.AreEqual("2026-11-16", account.interestDate);
            Assert.AreEqual(0, terms.Count, "만기가 된 정기예금은 목록에서 빠진다");

            Assert.AreEqual(0, BankRules.SettleAll(account, terms, "2026-11-16", _config).Count, "다시 불러도 한 번만");
        }

        [Test]
        public void SettleAll_LeavesTermsThatHaveNotMatured()
        {
            BankAccountState account = Account(0, "2026-11-13");
            var terms = new List<TermDeposit>
            {
                BankRules.OpenTerm(_week, 1000, "2026-11-13"),
                BankRules.OpenTerm(_month, 2000, "2026-11-13"),
            };

            Assert.AreEqual(0, BankRules.SettleAll(account, terms, "2026-11-19", _config).Count, "만기 하루 전");
            Assert.AreEqual(2, terms.Count);
            Assert.AreEqual(0, account.balance);

            List<BankEvent> events = BankRules.SettleAll(account, terms, "2026-11-20", _config);
            Assert.AreEqual(1, events.Count);
            Assert.AreEqual(BankEvent.TermMatured, events[0].kind);
            Assert.AreEqual(1100, account.balance);
            Assert.AreEqual(1100, account.dayLow, "만기로 들어온 돈은 그 날의 시작 잔액이다");
            Assert.AreEqual(1, terms.Count);
            Assert.AreEqual(TermProductCodes.Days30, terms[0].productCode);

            // 오래 접속하지 않아도 만기일에 들어온 것으로 계산된다: 12월 13일 만기, 그 뒤로 보통예금 이자.
            events = BankRules.SettleAll(account, terms, "2026-12-15", _config);
            Assert.AreEqual(0, terms.Count);
            long expected = 1100;
            for (int day = 0; day < 23; day++) expected += expected / 100;      // 11월 20일 ~ 12월 12일 치
            expected += 3000;                                                     // 12월 13일: 만기 2,000 + 1,000
            for (int day = 0; day < 2; day++) expected += expected / 100;       // 12월 13일, 14일 치
            Assert.AreEqual(expected, account.balance);
        }

        [Test]
        public void Settle_WithoutTerms_IsUnchanged()
        {
            BankAccountState account = Account(5000, "2026-11-13");
            List<InterestPayment> paid = BankRules.Settle(account, "2026-11-15", _config);
            Assert.AreEqual(2, paid.Count);
            Assert.AreEqual(50, paid[0].amount);
            Assert.AreEqual("2026-11-13", paid[0].date);
            Assert.AreEqual(50, paid[1].amount);
            Assert.AreEqual(5100, account.balance);
        }
    }

    /// <summary>모의 서버의 정기예금: 가입, 만기, 중도 해지. 같은 요청은 한 번만 처리한다.</summary>
    public sealed class LocalMockBackendTermTests
    {
        private MockKit _kit;

        [SetUp]
        public void SetUp() => _kit = new MockKit();

        private static TermProductDto Product(BankDto bank, string code) => bank.terms.Find(t => t.code == code);

        [Test]
        public void Open_MovesMoneyOutOfTheWallet_AndShowsUpInTheBank()
        {
            LocalMockBackend backend = _kit.NewBackend();
            MockKit.Boot(backend);
            _kit.Earn(backend, 1500);

            BankDto before = MockKit.Bank(backend);
            Assert.AreEqual(2, before.terms.Count);
            Assert.IsFalse(Product(before, TermProductCodes.Days7).open);
            Assert.AreEqual(1000, Product(before, TermProductCodes.Days7).rateBp);
            Assert.AreEqual(7, Product(before, TermProductCodes.Days7).days);
            Assert.AreEqual(0, before.termGc);

            TermResultDto opened = MockKit.Ok(MockKit.OpenTerm(backend, TermProductCodes.Days7, 1000, "t1"));
            Assert.AreEqual(1000, opened.amount);
            Assert.AreEqual(100, opened.interest, "이자는 가입할 때 서버가 정한다");
            Assert.IsFalse(opened.duplicate);
            Assert.AreEqual(500, opened.bank.gc);
            Assert.AreEqual(0, opened.bank.bankGc, "통장에 들어가는 돈이 아니다");
            Assert.AreEqual(1000, opened.bank.termGc);

            TermProductDto product = Product(opened.bank, TermProductCodes.Days7);
            Assert.IsTrue(product.open);
            Assert.AreEqual(1000, product.principal);
            Assert.AreEqual(100, product.interest);
            Assert.AreEqual("2026-11-20", product.maturesDate);
            Assert.AreEqual(7, product.daysLeft);
            Assert.IsFalse(Product(opened.bank, TermProductCodes.Days30).open);

            LedgerEntry entry = backend.CurrentLedger[backend.CurrentLedger.Count - 1];
            Assert.AreEqual(-1000, entry.delta);
            Assert.AreEqual(LedgerReasons.TermOpen, entry.reason);
            StringAssert.Contains(TermProductCodes.Days7, entry.reference);
            Assert.AreEqual(0, backend.CurrentBankLedger.Count);
            Assert.AreEqual(1500, backend.CurrentTotalGc, "돈이 생기거나 사라지지 않는다");
            Assert.IsNull(backend.FindLedgerInconsistency());

            MeDto me = MockKit.Me(backend);
            Assert.AreEqual(500, me.gc);
            Assert.AreEqual(1000, me.termGc);
        }

        [Test]
        public void Open_RejectsWhatIsNotAllowed_AndChangesNothing()
        {
            GameDefs defs = MockKit.Defs();
            defs.bank.unlockLevel = 3;
            LocalMockBackend backend = _kit.NewBackend(defs);
            MockKit.Boot(backend);
            _kit.Earn(backend, 100);
            Assert.AreEqual(ApiErrors.Locked, MockKit.Error(MockKit.OpenTerm(backend, TermProductCodes.Days7, 100, "t0")));

            _kit.Earn(backend, 10400);
            Assert.GreaterOrEqual(MockKit.Me(backend).level, 3);
            Assert.AreEqual(ApiErrors.InvalidRequest, MockKit.Error(MockKit.OpenTerm(backend, TermProductCodes.Days7, 99, "t1")));
            Assert.AreEqual(ApiErrors.InvalidRequest, MockKit.Error(MockKit.OpenTerm(backend, TermProductCodes.Days7, 10001, "t2")));
            Assert.AreEqual(ApiErrors.InvalidRequest, MockKit.Error(MockKit.OpenTerm(backend, TermProductCodes.Days7, 0, "t3")));
            Assert.AreEqual(ApiErrors.NotFound, MockKit.Error(MockKit.OpenTerm(backend, "term_nothing", 100, "t4")));
            Assert.AreEqual(ApiErrors.InvalidRequest, MockKit.Error(MockKit.OpenTerm(backend, TermProductCodes.Days7, 100, "")));
            Assert.AreEqual(10500, MockKit.Me(backend).gc);
            Assert.AreEqual(0, MockKit.Me(backend).termGc);

            MockKit.Ok(MockKit.OpenTerm(backend, TermProductCodes.Days7, 10000, "t5"));
            Assert.AreEqual(ApiErrors.Conflict, MockKit.Error(MockKit.OpenTerm(backend, TermProductCodes.Days7, 100, "t6")),
                "같은 상품에 두 번 가입할 수 없다");
            Assert.AreEqual(ApiErrors.InsufficientFunds, MockKit.Error(MockKit.OpenTerm(backend, TermProductCodes.Days30, 600, "t7")),
                "지갑에 500 GC만 남았다");
            Assert.AreEqual(500, MockKit.Me(backend).gc);
            Assert.AreEqual(10000, MockKit.Me(backend).termGc);
            Assert.AreEqual(10500, backend.CurrentTotalGc, "거절된 요청은 돈을 건드리지 않는다");
        }

        [Test]
        public void ResendingTheSameOpen_OpensOnlyOnce()
        {
            LocalMockBackend backend = _kit.NewBackend();
            MockKit.Boot(backend);
            _kit.Earn(backend, 1000);

            MockKit.Ok(MockKit.OpenTerm(backend, TermProductCodes.Days7, 400, "t1"));
            TermResultDto again = MockKit.Ok(MockKit.OpenTerm(backend, TermProductCodes.Days7, 400, "t1"));
            Assert.IsTrue(again.duplicate);
            Assert.AreEqual(400, again.amount);
            Assert.AreEqual(40, again.interest);
            Assert.AreEqual(600, again.bank.gc);
            Assert.AreEqual(400, again.bank.termGc);

            // 껐다 켠 뒤에도, 그리고 같은 번호를 다른 금액이나 상품에 쓰면 거절한다.
            LocalMockBackend restarted = _kit.NewBackend();
            MockKit.Boot(restarted);
            Assert.IsTrue(MockKit.Ok(MockKit.OpenTerm(restarted, TermProductCodes.Days7, 400, "t1")).duplicate);
            Assert.AreEqual(ApiErrors.Conflict, MockKit.Error(MockKit.OpenTerm(restarted, TermProductCodes.Days7, 500, "t1")));
            Assert.AreEqual(ApiErrors.Conflict, MockKit.Error(MockKit.OpenTerm(restarted, TermProductCodes.Days30, 400, "t1")));
            Assert.AreEqual(600, MockKit.Me(restarted).gc);
            Assert.AreEqual(1000, restarted.CurrentTotalGc);
        }

        [Test]
        public void Maturity_PaysPrincipalAndPromisedInterestIntoTheAccount_EvenIfTheGameWasOff()
        {
            LocalMockBackend backend = _kit.NewBackend();
            MockKit.Boot(backend);
            _kit.Earn(backend, 1000);
            // 은행을 처음 쓰는 것이 정기예금이어도 만기가 돌아야 한다.
            MockKit.Ok(MockKit.OpenTerm(backend, TermProductCodes.Days7, 1000, "t1"));

            _kit.Clock.AddDays(6);
            BankDto dayBefore = MockKit.Bank(backend);
            Assert.AreEqual(0, dayBefore.bankGc, "만기 하루 전");
            Assert.AreEqual(1, Product(dayBefore, TermProductCodes.Days7).daysLeft);
            Assert.AreEqual(0, MockKit.Me(backend).termMaturedCount);

            // 만기일에 게임을 켠다.
            _kit.Clock.NextDay();
            LocalMockBackend restarted = _kit.NewBackend();
            MeDto me = MockKit.Boot(restarted);
            Assert.AreEqual(1100, me.bankGc);
            Assert.AreEqual(0, me.gc);
            Assert.AreEqual(0, me.termGc);
            Assert.AreEqual(100, me.bankInterestTotal, "정기예금 이자도 받은 이자에 들어간다");
            Assert.AreEqual(1, me.termMaturedCount);

            BankDto bank = MockKit.Bank(restarted);
            Assert.IsFalse(Product(bank, TermProductCodes.Days7).open, "만기가 되면 다시 가입할 수 있다");
            Assert.AreEqual(2, restarted.CurrentBankLedger.Count);
            Assert.AreEqual(LedgerReasons.TermMatured, restarted.CurrentBankLedger[0].reason);
            Assert.AreEqual(1000, restarted.CurrentBankLedger[0].delta);
            Assert.AreEqual(LedgerReasons.TermInterest, restarted.CurrentBankLedger[1].reason);
            Assert.AreEqual(100, restarted.CurrentBankLedger[1].delta);
            Assert.AreEqual("2026-11-20", bank.recent[0].date, "만기일이 적힌다");
            Assert.AreEqual(1100, restarted.CurrentTotalGc);
            Assert.IsNull(restarted.FindLedgerInconsistency());

            // 다시 물어도 한 번만 받는다. 다음 날에는 보통예금 이자가 붙는다 (만기일의 시작 잔액 1,100의 1%).
            Assert.AreEqual(1100, MockKit.Bank(restarted).bankGc);
            _kit.Clock.NextDay();
            Assert.AreEqual(1111, MockKit.Bank(restarted).bankGc);
            Assert.AreEqual(1, MockKit.Me(restarted).termMaturedCount);
        }

        [Test]
        public void Cancel_ReturnsOnlyThePrincipal_ToTheAccount()
        {
            LocalMockBackend backend = _kit.NewBackend();
            MockKit.Boot(backend);
            _kit.Earn(backend, 1000);
            MockKit.Ok(MockKit.OpenTerm(backend, TermProductCodes.Days30, 1000, "t1"));
            _kit.Clock.AddDays(29);

            TermResultDto cancelled = MockKit.Ok(MockKit.CancelTerm(backend, TermProductCodes.Days30, "c1"));
            Assert.AreEqual(1000, cancelled.amount);
            Assert.AreEqual(500, cancelled.interest, "받지 못한 이자");
            Assert.AreEqual(1000, cancelled.bank.bankGc, "원금만 통장으로 돌아온다");
            Assert.AreEqual(0, cancelled.bank.gc);
            Assert.AreEqual(0, cancelled.bank.termGc);
            Assert.AreEqual(0, cancelled.bank.interestTotal);
            Assert.IsFalse(Product(cancelled.bank, TermProductCodes.Days30).open);
            Assert.AreEqual(0, cancelled.bank.interestTomorrow, "오늘 들어온 돈에는 오늘 치 이자가 붙지 않는다");
            Assert.AreEqual(10, cancelled.bank.interestWhenSettled);

            Assert.AreEqual(1, backend.CurrentBankLedger.Count);
            Assert.AreEqual(LedgerReasons.TermCancel, backend.CurrentBankLedger[0].reason);
            Assert.AreEqual(1000, backend.CurrentTotalGc);
            Assert.AreEqual(0, MockKit.Me(backend).termMaturedCount, "중도 해지는 만기가 아니다");

            // 같은 요청은 한 번만. 더 찾을 것은 없다.
            TermResultDto again = MockKit.Ok(MockKit.CancelTerm(backend, TermProductCodes.Days30, "c1"));
            Assert.IsTrue(again.duplicate);
            Assert.AreEqual(1000, again.amount);
            Assert.AreEqual(1000, again.bank.bankGc);
            Assert.AreEqual(ApiErrors.NotFound, MockKit.Error(MockKit.CancelTerm(backend, TermProductCodes.Days30, "c2")));
            Assert.AreEqual(ApiErrors.NotFound, MockKit.Error(MockKit.CancelTerm(backend, TermProductCodes.Days7, "c3")));
            Assert.AreEqual(ApiErrors.NotFound, MockKit.Error(MockKit.CancelTerm(backend, "term_nothing", "c4")));
            Assert.IsNull(backend.FindLedgerInconsistency());

            // 다음 날: 다시 가입할 수 있다.
            _kit.Clock.NextDay();
            Assert.AreEqual(1000, MockKit.Bank(backend).bankGc);
        }

        [Test]
        public void CancelOnTheMaturityDay_ComesTooLate_TheInterestIsAlreadyPaid()
        {
            LocalMockBackend backend = _kit.NewBackend();
            MockKit.Boot(backend);
            _kit.Earn(backend, 1000);
            MockKit.Ok(MockKit.OpenTerm(backend, TermProductCodes.Days7, 1000, "t1"));
            _kit.Clock.AddDays(7);

            // 만기일에 화면을 새로 고치지 않은 채 "중간에 찾기"를 눌러도 이자를 잃지 않는다.
            Assert.AreEqual(ApiErrors.NotFound, MockKit.Error(MockKit.CancelTerm(backend, TermProductCodes.Days7, "c1")));
            BankDto bank = MockKit.Bank(backend);
            Assert.AreEqual(1100, bank.bankGc);
            Assert.AreEqual(100, bank.interestTotal);
            Assert.AreEqual(1, bank.termMaturedCount);
        }

        [Test]
        public void PromisedInterest_DoesNotChangeWhenTheRateChangesLater()
        {
            LocalMockBackend backend = _kit.NewBackend();
            MockKit.Boot(backend);
            _kit.Earn(backend, 2000);
            MockKit.Ok(MockKit.OpenTerm(backend, TermProductCodes.Days7, 1000, "t1"));

            // 관리자가 이율을 내렸다.
            GameDefs changed = MockKit.Defs();
            changed.bank.FindTerm(TermProductCodes.Days7).rateBp = 100;
            LocalMockBackend later = _kit.NewBackend(changed);
            MockKit.Boot(later);
            Assert.AreEqual(100, MockKit.Bank(later).terms.Find(t => t.code == TermProductCodes.Days7).interest,
                "이미 가입한 것은 약속한 이자 그대로");

            _kit.Clock.AddDays(7);
            Assert.AreEqual(1100, MockKit.Bank(later).bankGc);

            // 새로 가입하면 새 이율이다.
            Assert.AreEqual(10, MockKit.Ok(MockKit.OpenTerm(later, TermProductCodes.Days7, 1000, "t2")).interest);
        }

        [Test]
        public void TermsAndSavings_RunSideBySide()
        {
            LocalMockBackend backend = _kit.NewBackend();
            MockKit.Boot(backend);
            _kit.Earn(backend, 3000);
            MockKit.Ok(MockKit.Deposit(backend, 1000, "d1"));
            MockKit.Ok(MockKit.OpenTerm(backend, TermProductCodes.Days7, 1000, "t1"));
            MockKit.Ok(MockKit.OpenTerm(backend, TermProductCodes.Days30, 1000, "t2"));
            Assert.AreEqual(0, MockKit.Me(backend).gc);
            Assert.AreEqual(3000, backend.CurrentTotalGc);

            // 8일 뒤 (11월 21일): 14~19일 치 이자 여섯 번, 20일에 만기 1,100, 20일 치 이자.
            _kit.Clock.AddDays(8);
            long expected = 1000;
            for (int day = 0; day < 6; day++) expected += expected / 100;
            expected += 1100;
            expected += expected / 100;
            BankDto bank = MockKit.Bank(backend);
            Assert.AreEqual(expected, bank.bankGc);
            Assert.AreEqual(1000, bank.termGc, "30일 정기예금은 아직 남아 있다");
            Assert.AreEqual(22, bank.terms.Find(t => t.code == TermProductCodes.Days30).daysLeft);
            Assert.AreEqual(expected - 2000, bank.interestTotal, "보통예금 이자와 정기예금 이자의 합");
            Assert.AreEqual(1, bank.termMaturedCount);
            Assert.IsNull(backend.FindLedgerInconsistency());
        }

        [Test]
        public void Session_AnnouncesAMaturityOnce()
        {
            LocalMockBackend backend = _kit.NewBackend();
            GameSession session = GameSession.Start(backend, MockKit.Boot(backend));
            try
            {
                _kit.Earn(backend, 500);
                MockKit.Ok(MockKit.OpenTerm(backend, TermProductCodes.Days7, 500, "t1"));
                session.ApplyMe(MockKit.Me(backend));
                Assert.AreEqual(500, session.Me.termGc);
                Assert.AreEqual(0, session.TakeUnseenTermMaturities());

                _kit.Clock.AddDays(7);
                session.ApplyBank(MockKit.Bank(backend));
                Assert.AreEqual(550, session.Me.bankGc);
                Assert.AreEqual(0, session.Me.termGc);
                Assert.AreEqual(1, session.TakeUnseenTermMaturities());
                Assert.AreEqual(0, session.TakeUnseenTermMaturities(), "한 번만 알린다");
                Assert.AreEqual(50, session.TakeUnseenInterest(), "정기예금 이자도 이자 알림에 들어간다");

                // 껐다 켜도 다시 알리지 않는다.
                LocalMockBackend restarted = _kit.NewBackend();
                GameSession restored = GameSession.Start(restarted, MockKit.Boot(restarted));
                Assert.AreEqual(0, restored.TakeUnseenTermMaturities());
                Assert.AreEqual(0, restored.TakeUnseenInterest());
            }
            finally
            {
                GameSession.End();
            }
        }
    }
}
