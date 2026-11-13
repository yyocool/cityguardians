using System;
using System.Collections.Generic;
using System.Globalization;

namespace CityGuardians.Domain
{
    /// <summary>은행 설정 (docs/ECONOMY_SYSTEM.md 4장). 수치는 초기값이고 서버(관리자)가 정한다.</summary>
    [Serializable]
    public sealed class BankConfig
    {
        /// <summary>보통예금의 하루 이율. 1만분의 1 단위 (100 = 1%). 돈 계산에 소수를 쓰지 않기 위해 정수로 둔다.</summary>
        public int dailyInterestBp = 100;
        /// <summary>이자가 붙는 잔액의 한도. 넘는 금액은 맡길 수는 있지만 이자가 붙지 않는다.</summary>
        public long interestCap = 10000;
        /// <summary>은행을 쓸 수 있는 레벨.</summary>
        public int unlockLevel = 3;
        /// <summary>한 번에 옮길 수 있는 금액의 한도.</summary>
        public long maxTransfer = 1000000000;
        /// <summary>정기예금 상품. 비어 있으면 정기예금이 없다.</summary>
        public List<TermProductDef> termProducts = new List<TermProductDef>();

        public TermProductDef FindTerm(string code)
        {
            if (termProducts == null) return null;
            for (int i = 0; i < termProducts.Count; i++)
                if (termProducts[i].code == code) return termProducts[i];
            return null;
        }
    }

    public static class TermProductCodes
    {
        public const string Days7 = "term_7d";
        public const string Days30 = "term_30d";
    }

    /// <summary>
    /// 정기예금 상품 (docs/ECONOMY_SYSTEM.md 4.3). 만기까지 두면 원금과 약속한 이자를 통장으로 받고,
    /// 중간에 찾으면 원금만 받는다. 이율은 서버(관리자)가 정하고, 가입한 뒤에는 바뀌지 않는다.
    /// </summary>
    [Serializable]
    public sealed class TermProductDef
    {
        public string code;
        public string name;
        /// <summary>만기까지의 날 수 (한국 날짜 기준).</summary>
        public int days;
        /// <summary>만기에 받는 이자의 비율. 1만분의 1 단위 (1000 = 원금의 10%). 하루 이율이 아니라 기간 전체의 이율이다.</summary>
        public int rateBp;
        public long minAmount = 100;
        public long maxAmount = 10000;
    }

    /// <summary>가입한 정기예금 한 건. 상품마다 하나씩만 가입할 수 있다.</summary>
    [Serializable]
    public sealed class TermDeposit
    {
        public string productCode;
        public long principal;
        /// <summary>만기에 받을 이자. 가입할 때 정해지고 그 뒤에 상품의 이율이 바뀌어도 그대로다.</summary>
        public long interest;
        /// <summary>가입한 날 (한국 날짜, yyyy-MM-dd).</summary>
        public string openedDate;
        /// <summary>만기일. 이 날이 되면 원금과 이자가 통장으로 들어온다.</summary>
        public string maturesDate;
    }

    public static class BankReasons
    {
        public const string Locked = "locked";
        public const string InvalidAmount = "invalid_amount";
        public const string InsufficientWallet = "insufficient_wallet";
        public const string InsufficientAccount = "insufficient_account";
        public const string UnknownProduct = "unknown_product";
        /// <summary>그 상품에 이미 가입해 있음.</summary>
        public const string AlreadyOpen = "already_open";
        public const string BelowMinimum = "below_minimum";
        public const string AboveMaximum = "above_maximum";
        /// <summary>그 상품에 가입해 있지 않음.</summary>
        public const string NotOpen = "not_open";
    }

    /// <summary>통장에 돈이 들어온 일 한 건 (이자 또는 정기예금 만기). 일어난 순서대로 원장에 적는다.</summary>
    public struct BankEvent
    {
        public const string Interest = "interest";
        public const string TermMatured = "term_matured";

        public string kind;
        /// <summary>이자면 어느 날의 이자인가, 만기면 만기일 (yyyy-MM-dd).</summary>
        public string date;
        /// <summary>이자면 이자 금액, 만기면 원금.</summary>
        public long amount;
        /// <summary>만기에 함께 받은 정기예금 이자. 보통예금 이자면 0.</summary>
        public long termInterest;
        public string productCode;
    }

    /// <summary>보통예금 계좌의 상태. 이자 계산에 필요한 것만 담는다.</summary>
    public sealed class BankAccountState
    {
        public long balance;
        /// <summary>오늘의 최저 잔액. 이자는 이 값에 붙는다.</summary>
        public long dayLow;
        /// <summary>지금 이자를 세고 있는 날 (한국 날짜, yyyy-MM-dd). 이 날 이전의 이자는 모두 지급됐다.</summary>
        public string interestDate;
    }

    /// <summary>지급한 이자 한 건.</summary>
    public struct InterestPayment
    {
        /// <summary>어느 날의 이자인가 (yyyy-MM-dd).</summary>
        public string date;
        public long amount;
    }

    /// <summary>
    /// 은행 규칙: 입출금 검사와 이자 계산. 서버(지금은 모의 서버)가 쓴다.
    /// 클라이언트는 서버가 준 값을 보여 주기만 한다.
    /// </summary>
    public static class BankRules
    {
        public const string DateFormat = "yyyy-MM-dd";
        /// <summary>한 번에 정산하는 밀린 날짜의 한도. 시계가 크게 틀어졌을 때 끝없이 돌지 않게 한다.</summary>
        public const int MaxCatchUpDays = 3660;

        /// <summary>하루 이자 = floor(min(그날의 최저 잔액, 한도) × 하루 이율). 1 GC 미만은 버린다.</summary>
        public static long DailyInterest(long lowestBalance, BankConfig config)
        {
            if (config == null || config.dailyInterestBp <= 0 || lowestBalance <= 0) return 0;
            long counted = Math.Min(lowestBalance, Math.Max(0, config.interestCap));
            return counted * config.dailyInterestBp / 10000;
        }

        /// <summary>이자 1 GC가 붙기 시작하는 잔액. 화면에서 "얼마를 맡기면 하루 1 GC"를 알려 줄 때 쓴다. 이자가 없으면 0.</summary>
        public static long BalanceForOneGc(BankConfig config)
        {
            if (config == null || config.dailyInterestBp <= 0) return 0;
            long needed = (10000 + config.dailyInterestBp - 1) / config.dailyInterestBp;
            return needed <= config.interestCap ? needed : 0;
        }

        public static string CheckDeposit(BankConfig config, int level, long amount, long walletBalance)
        {
            if (level < config.unlockLevel) return BankReasons.Locked;
            if (amount <= 0 || amount > config.maxTransfer) return BankReasons.InvalidAmount;
            if (amount > walletBalance) return BankReasons.InsufficientWallet;
            return null;
        }

        public static string CheckWithdraw(BankConfig config, int level, long amount, long accountBalance)
        {
            if (level < config.unlockLevel) return BankReasons.Locked;
            if (amount <= 0 || amount > config.maxTransfer) return BankReasons.InvalidAmount;
            if (amount > accountBalance) return BankReasons.InsufficientAccount;
            return null;
        }

        /// <summary>돈을 맡긴 뒤의 계좌. 오늘의 최저 잔액은 그대로다 (오늘 맡긴 돈에는 오늘 치 이자가 붙지 않는다).</summary>
        public static void Deposit(BankAccountState account, long amount)
        {
            account.balance = checked(account.balance + amount);
        }

        /// <summary>돈을 찾은 뒤의 계좌. 오늘의 최저 잔액이 내려갈 수 있다.</summary>
        public static void Withdraw(BankAccountState account, long amount)
        {
            account.balance -= amount;
            if (account.balance < account.dayLow) account.dayLow = account.balance;
        }

        /// <summary>
        /// 오늘까지 밀린 이자를 하루씩 정산한다. 같은 날에 여러 번 불러도 한 번만 지급된다.
        /// 지급한 이자는 잔액에 더해지고(복리), 목록으로 돌려준다.
        /// 날짜가 뒤로 갔으면 아무것도 하지 않는다.
        /// </summary>
        public static List<InterestPayment> Settle(BankAccountState account, string today, BankConfig config)
        {
            List<BankEvent> events = SettleAll(account, null, today, config);
            var paid = new List<InterestPayment>(events.Count);
            for (int i = 0; i < events.Count; i++)
                paid.Add(new InterestPayment { date = events[i].date, amount = events[i].amount });
            return paid;
        }

        /// <summary>
        /// 오늘까지 밀린 일을 하루씩 처리한다: 그날 치 보통예금 이자를 주고, 날이 바뀌면 그 날이 만기인 정기예금의
        /// 원금과 이자를 통장에 넣는다. 만기로 들어온 돈은 그 날의 시작 잔액이라 그 날부터 보통예금 이자가 붙는다.
        /// 만기가 된 정기예금은 terms에서 빠진다. 일어난 순서대로 돌려준다. 같은 날에 여러 번 불러도 한 번만 처리된다.
        /// </summary>
        public static List<BankEvent> SettleAll(BankAccountState account, List<TermDeposit> terms, string today, BankConfig config)
        {
            var events = new List<BankEvent>();
            if (!TryParseDate(today, out DateTime end)) throw new ArgumentException("날짜 형식이 올바르지 않습니다.", nameof(today));

            if (!TryParseDate(account.interestDate, out DateTime day))
            {
                // 처음 쓰는 계좌: 오늘부터 센다.
                account.interestDate = today;
                account.dayLow = account.balance;
                return events;
            }
            if (end <= day) return events;

            if ((end - day).TotalDays > MaxCatchUpDays) day = end.AddDays(-MaxCatchUpDays);
            if (account.dayLow > account.balance) account.dayLow = account.balance;

            while (day < end)
            {
                long interest = DailyInterest(account.dayLow, config);
                if (interest > 0)
                {
                    account.balance = checked(account.balance + interest);
                    events.Add(new BankEvent
                    {
                        kind = BankEvent.Interest, date = day.ToString(DateFormat, CultureInfo.InvariantCulture), amount = interest,
                    });
                }
                day = day.AddDays(1);
                PayMaturedTerms(account, terms, day, events);
                // 새 날은 지금 잔액에서 시작한다.
                account.dayLow = account.balance;
            }
            account.interestDate = today;
            return events;
        }

        private static void PayMaturedTerms(BankAccountState account, List<TermDeposit> terms, DateTime day, List<BankEvent> events)
        {
            if (terms == null) return;
            for (int i = 0; i < terms.Count; i++)
            {
                TermDeposit term = terms[i];
                // 날짜를 읽을 수 없는 건은 건드리지 않는다 (돈을 잃게 하지 않는다).
                if (!TryParseDate(term.maturesDate, out DateTime matures) || matures > day) continue;
                account.balance = checked(account.balance + term.principal + term.interest);
                events.Add(new BankEvent
                {
                    kind = BankEvent.TermMatured, date = day.ToString(DateFormat, CultureInfo.InvariantCulture),
                    amount = term.principal, termInterest = term.interest, productCode = term.productCode,
                });
                terms.RemoveAt(i);
                i--;
            }
        }

        // ── 정기예금 ────────────────────────────────────────────

        /// <summary>만기에 받는 이자 = floor(원금 × 기간 이율). 1 GC 미만은 버린다.</summary>
        public static long TermInterest(long principal, TermProductDef product)
        {
            if (product == null || product.rateBp <= 0 || principal <= 0) return 0;
            return checked(principal * product.rateBp) / 10000;
        }

        /// <summary>가입한 날에서 days일 뒤의 날짜.</summary>
        public static string MaturityDate(string openedDate, int days)
        {
            if (!TryParseDate(openedDate, out DateTime opened)) throw new ArgumentException("날짜 형식이 올바르지 않습니다.", nameof(openedDate));
            return opened.AddDays(Math.Max(1, days)).ToString(DateFormat, CultureInfo.InvariantCulture);
        }

        /// <summary>만기까지 남은 날 수. 이미 지났으면 0.</summary>
        public static int DaysLeft(TermDeposit term, string today)
        {
            if (term == null || !TryParseDate(term.maturesDate, out DateTime matures) || !TryParseDate(today, out DateTime now)) return 0;
            return Math.Max(0, (int)(matures - now).TotalDays);
        }

        public static TermDeposit FindTerm(IList<TermDeposit> terms, string productCode)
        {
            if (terms == null) return null;
            for (int i = 0; i < terms.Count; i++)
                if (terms[i].productCode == productCode) return terms[i];
            return null;
        }

        /// <summary>지갑의 돈으로 그 정기예금에 가입할 수 있는가. 가입할 수 있으면 null, 아니면 이유.</summary>
        public static string CheckOpenTerm(BankConfig config, int level, string productCode, long amount,
            long walletBalance, IList<TermDeposit> terms)
        {
            if (level < config.unlockLevel) return BankReasons.Locked;
            TermProductDef product = config.FindTerm(productCode);
            if (product == null) return BankReasons.UnknownProduct;
            if (FindTerm(terms, productCode) != null) return BankReasons.AlreadyOpen;
            if (amount <= 0 || amount > config.maxTransfer) return BankReasons.InvalidAmount;
            if (amount < product.minAmount) return BankReasons.BelowMinimum;
            if (amount > product.maxAmount) return BankReasons.AboveMaximum;
            if (amount > walletBalance) return BankReasons.InsufficientWallet;
            return null;
        }

        /// <summary>가입할 때의 정기예금 한 건을 만든다. 이자는 이때 정해진다.</summary>
        public static TermDeposit OpenTerm(TermProductDef product, long amount, string today)
        {
            if (product == null) throw new ArgumentNullException(nameof(product));
            return new TermDeposit
            {
                productCode = product.code,
                principal = amount,
                interest = TermInterest(amount, product),
                openedDate = today,
                maturesDate = MaturityDate(today, product.days),
            };
        }

        /// <summary>그 정기예금을 중간에 찾을 수 있는가. 레벨이 내려가는 일은 없으므로 가입해 있기만 하면 된다.</summary>
        public static string CheckCancelTerm(BankConfig config, string productCode, IList<TermDeposit> terms)
        {
            if (config.FindTerm(productCode) == null && FindTerm(terms, productCode) == null) return BankReasons.UnknownProduct;
            return FindTerm(terms, productCode) == null ? BankReasons.NotOpen : null;
        }

        /// <summary>지금대로 두면 다음 자정에 받을 이자.</summary>
        public static long InterestTomorrow(BankAccountState account, BankConfig config) =>
            DailyInterest(Math.Min(account.dayLow, account.balance), config);

        /// <summary>오늘 맡긴 돈까지 모두 이자가 붙기 시작했을 때의 하루 이자 (모레부터).</summary>
        public static long InterestWhenSettled(BankAccountState account, BankConfig config) =>
            DailyInterest(account.balance, config);

        private static bool TryParseDate(string text, out DateTime date)
        {
            date = default;
            return !string.IsNullOrEmpty(text) && DateTime.TryParseExact(text, DateFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out date);
        }
    }
}
