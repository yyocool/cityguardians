using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CityGuardians.Domain;
using CityGuardians.Services;
using UnityEngine;

namespace CityGuardians.Gameplay
{
    /// <summary>
    /// 은행에서 하는 일들: 맡기기, 찾기, 이자 확인, 거래 내역 (docs/ECONOMY_SYSTEM.md 4장).
    /// 은행원과 내 방 컴퓨터가 같은 화면을 연다. 잔액과 이자는 언제나 서버가 계산하고,
    /// 여기서는 요청을 보내고 받은 값을 보여 주기만 한다.
    /// 금액은 숫자를 치지 않고 정해진 버튼(100, 500, 1,000, 전부)으로 고른다.
    /// </summary>
    public static class BankActions
    {
        public const string BankTag = "bank";
        public const string Title = "도시은행";
        public const string DepositTitle = "맡기기  (지갑 → 통장)";
        public const string WithdrawTitle = "찾기  (통장 → 지갑)";
        public const string HistoryTitle = "거래 내역";
        public const string TermLabel = "정기예금";
        public const string TermTitle = "정기예금";

        private static readonly long[] Amounts = { 100, 500, 1000 };
        /// <summary>가장 작은 금액 버튼. 지갑에 이만큼은 있어야 은행을 소개한다.</summary>
        public static long SmallestAmount => Amounts[0];

        private static string NewRequestId() => Guid.NewGuid().ToString("N");

        /// <summary>은행을 연다. 아직 쓸 수 없는 레벨이면 그 이유를 알려 준다.</summary>
        /// <param name="teller">은행원. 한마디 하게 하는 데 쓴다. 방 컴퓨터에서 열면 없다.</param>
        public static void Open(NpcAgent teller = null)
        {
            GameSession session = GameSession.Current;
            if (session == null) return;
            session.Backend.GetBank(result =>
            {
                if (GameSession.Current != session) return;
                if (!result.ok)
                {
                    Debug.LogWarning($"은행 열기 실패: {result.errorCode} {result.message}");
                    GameEvents.ShowToast("은행을 열지 못했어요. 잠시 뒤에 다시 시도해 주세요.");
                    return;
                }

                BankDto bank = result.data;
                session.ApplyBank(bank);
                if (!bank.unlocked)
                {
                    GameEvents.ShowToast($"은행은 레벨 {bank.unlockLevel}부터 이용할 수 있어요.  (지금 레벨 {session.Me.level})");
                    if (teller != null) teller.Say($"레벨 {bank.unlockLevel}이 되면 다시 와 줘요!", 3.5f);
                    return;
                }

                AnnounceInterest(session);
                if (session.Me.GetFlag(FlagKeys.BankVisited) != "1") session.SetFlag(FlagKeys.BankVisited, "1");
                ShowMain(session, bank, teller);
            });
        }

        /// <summary>
        /// 장면에 들어왔을 때 부른다. 통장에 돈이 있으면 서버에 밀린 이자를 정산하게 하고,
        /// 새로 들어온 이자가 있으면 한 번 알린다.
        /// </summary>
        public static void CheckInterest()
        {
            GameSession session = GameSession.Current;
            if (session == null || (session.Me.bankGc <= 0 && session.Me.termGc <= 0)) return;
            session.Backend.GetMe(result =>
            {
                if (GameSession.Current != session || !result.ok) return;
                session.ApplyMe(result.data);
                AnnounceInterest(session);
            });
        }

        /// <summary>새로 들어온 이자와 만기가 된 정기예금을 한 번 알린다.</summary>
        private static void AnnounceInterest(GameSession session)
        {
            long matured = session.TakeUnseenTermMaturities();
            long interest = session.TakeUnseenInterest();
            if (matured <= 0 && interest <= 0) return;
            Sfx.Play(Sfx.Id.Coin);
            if (matured > 0)
            {
                GameEvents.ShowToast(interest > 0
                    ? $"정기예금이 만기가 됐어요!  원금과 이자 +{interest:N0} GC가 통장에 들어왔어요!"
                    : "정기예금이 만기가 됐어요!  원금이 통장에 들어왔어요!");
            }
            else
            {
                GameEvents.ShowToast($"통장에 이자 +{interest:N0} GC가 들어왔어요!");
            }
        }

        // ── 첫 화면 ─────────────────────────────────────────────

        private static void ShowMain(GameSession session, BankDto bank, NpcAgent teller)
        {
            if (GameSession.Current != session) return;

            var deposit = new ChoiceOption
            {
                label = "맡기기",
                cost = "지갑 → 통장",
                detail = bank.gc > 0 ? "지갑의 GC를 통장에 넣어요. 언제든 다시 찾을 수 있어요." : "지갑이 비어 있어요.",
                enabled = bank.gc > 0,
                onChosen = () => ShowAmounts(session, bank, teller, true),
            };
            var withdraw = new ChoiceOption
            {
                label = "찾기",
                cost = "통장 → 지갑",
                detail = bank.bankGc > 0 ? "통장의 GC를 지갑으로 꺼내요." : "통장이 비어 있어요.",
                enabled = bank.bankGc > 0,
                onChosen = () => ShowAmounts(session, bank, teller, false),
            };
            bool hasHistory = bank.recent != null && bank.recent.Count > 0;
            var history = new ChoiceOption
            {
                label = "거래 내역",
                cost = bank.interestTotal > 0 ? $"받은 이자 {bank.interestTotal:N0} GC" : "",
                detail = hasHistory ? "최근에 맡기고 찾은 기록과 받은 이자를 봐요." : "아직 거래가 없어요.",
                enabled = hasHistory,
                onChosen = () => ShowHistory(session, bank, teller),
            };

            if (bank.terms == null || bank.terms.Count == 0)
            {
                ChoiceMenu.Instance.Open(Title, $"{BalanceLine(bank)}\n{InterestLine(bank)}", deposit, withdraw, history);
                ChoiceMenu.Instance.Tag = BankTag;
                return;
            }

            var term = new ChoiceOption
            {
                label = TermLabel,
                cost = bank.termGc > 0 ? $"넣어 둔 돈 {bank.termGc:N0} GC" : "",
                detail = "정해진 날까지 넣어 두면 이자를 훨씬 많이 받아요.",
                onChosen = () => ShowTerms(session, bank, teller),
            };
            ChoiceMenu.Instance.Open(Title, $"{BalanceLine(bank)}\n{InterestLine(bank)}", deposit, withdraw, term, history);
            ChoiceMenu.Instance.Tag = BankTag;
        }

        public static string BalanceLine(BankDto bank) => $"지갑  {bank.gc:N0} GC          통장  {bank.bankGc:N0} GC";

        /// <summary>
        /// 이자를 한 줄로. 서버가 준 값을 그대로 보여 준다.
        /// 오늘 맡긴 돈에는 오늘 치 이자가 붙지 않으므로, 내일 받을 이자와 그 뒤의 이자가 다르면 둘 다 적는다.
        /// </summary>
        public static string InterestLine(BankDto bank)
        {
            if (bank.dailyInterestBp <= 0) return "지금은 이자가 붙지 않아요.";
            if (bank.interestWhenSettled <= 0)
            {
                return bank.balanceForOneGc > 0
                    ? $"통장에 {bank.balanceForOneGc:N0} GC가 있으면 하루에 1 GC씩 이자가 붙어요."
                    : "돈을 맡겨 두면 매일 조금씩 불어나요.";
            }

            string line = bank.interestTomorrow >= bank.interestWhenSettled
                ? $"내일 받을 이자  +{bank.interestTomorrow:N0} GC"
                : $"내일 받을 이자  +{bank.interestTomorrow:N0} GC      모레부터 하루  +{bank.interestWhenSettled:N0} GC";
            if (bank.bankGc > bank.interestCap) line += $"      (이자는 {bank.interestCap:N0} GC까지 붙어요)";
            return line;
        }

        // ── 금액 고르기 ─────────────────────────────────────────

        private static void ShowAmounts(GameSession session, BankDto bank, NpcAgent teller, bool deposit)
        {
            if (GameSession.Current != session) return;

            long available = deposit ? bank.gc : bank.bankGc;
            var options = new List<ChoiceOption>();
            for (int i = 0; i < Amounts.Length; i++)
            {
                long amount = Amounts[i];
                bool enough = available >= amount;
                options.Add(new ChoiceOption
                {
                    label = $"{amount:N0} GC",
                    detail = enough ? AfterText(bank, amount, deposit) : deposit ? "지갑에 그만큼 없어요." : "통장에 그만큼 없어요.",
                    enabled = enough,
                    onChosen = () => Transfer(session, teller, amount, deposit),
                });
            }
            options.Add(new ChoiceOption
            {
                label = "전부",
                cost = $"{available:N0} GC",
                detail = available > 0 ? AfterText(bank, available, deposit) : deposit ? "지갑이 비어 있어요." : "통장이 비어 있어요.",
                enabled = available > 0,
                onChosen = () => Transfer(session, teller, available, deposit),
            });

            ChoiceMenu.Instance.Open(deposit ? DepositTitle : WithdrawTitle,
                $"{(deposit ? "얼마를 맡길까요?" : "얼마를 찾을까요?")}\n{BalanceLine(bank)}", options.ToArray());
            ChoiceMenu.Instance.Tag = BankTag;
            // E는 은행을 닫지 않고 첫 화면으로 돌아간다.
            ChoiceMenu.Instance.OnCancelled = () => ShowMain(session, bank, teller);
        }

        private static string AfterText(BankDto bank, long amount, bool deposit) => deposit
            ? $"맡기면  지갑 {bank.gc - amount:N0} GC  ·  통장 {bank.bankGc + amount:N0} GC"
            : $"찾으면  지갑 {bank.gc + amount:N0} GC  ·  통장 {bank.bankGc - amount:N0} GC";

        private static void Transfer(GameSession session, NpcAgent teller, long amount, bool deposit)
        {
            Action<ApiResult<BankTransferDto>> done = result =>
            {
                if (GameSession.Current != session) return;
                if (!result.ok)
                {
                    Debug.LogWarning($"은행 거래 실패 ({(deposit ? "입금" : "출금")} {amount}): {result.errorCode} {result.message}");
                    GameEvents.ShowToast(FailText(result.errorCode, deposit));
                    // 서버의 지금 잔액으로 다시 연다.
                    Open(teller);
                    return;
                }

                BankDto bank = result.data.bank;
                session.ApplyBank(bank);
                Sfx.Play(Sfx.Id.Coin);
                GameEvents.ShowToast(deposit
                    ? $"{result.data.amount:N0} GC를 맡겼어요!  통장 {bank.bankGc:N0} GC"
                    : $"{result.data.amount:N0} GC를 찾았어요!  지갑 {bank.gc:N0} GC");
                if (teller != null) teller.Say(deposit ? "잘 맡아 둘게요!" : "여기 있어요!", 3.5f);
                ShowMain(session, bank, teller);
            };

            if (deposit) session.Backend.BankDeposit(amount, NewRequestId(), done);
            else session.Backend.BankWithdraw(amount, NewRequestId(), done);
        }

        private static string FailText(string errorCode, bool deposit)
        {
            switch (errorCode)
            {
                case ApiErrors.InsufficientFunds: return deposit ? "지갑에 그만큼의 GC가 없어요." : "통장에 그만큼의 GC가 없어요.";
                case ApiErrors.Locked: return "아직 은행을 이용할 수 없어요.";
                default: return deposit ? "맡기지 못했어요. 잠시 뒤에 다시 시도해 주세요." : "찾지 못했어요. 잠시 뒤에 다시 시도해 주세요.";
            }
        }

        // ── 정기예금 ────────────────────────────────────────────

        /// <summary>"10%"처럼. 1만분의 1 단위의 이율을 읽기 쉽게 적는다.</summary>
        public static string Percent(int bp) => (bp / 100f).ToString("0.##", CultureInfo.InvariantCulture) + "%";

        private static void ShowTerms(GameSession session, BankDto bank, NpcAgent teller)
        {
            if (GameSession.Current != session) return;

            var options = new List<ChoiceOption>();
            for (int i = 0; i < bank.terms.Count && options.Count < ChoiceMenu.MaxOptions; i++)
            {
                TermProductDto product = bank.terms[i];
                if (product.open)
                {
                    options.Add(new ChoiceOption
                    {
                        label = $"{product.name} (가입 중)",
                        cost = $"{product.principal:N0} GC",
                        detail = product.daysLeft > 0
                            ? $"{product.daysLeft}일 뒤에 이자 +{product.interest:N0} GC와 함께 통장으로 들어와요."
                            : $"곧 이자 +{product.interest:N0} GC와 함께 통장으로 들어와요.",
                        onChosen = () => ConfirmCancelTerm(session, bank, teller, product),
                    });
                    continue;
                }

                bool enough = bank.gc >= product.minAmount;
                options.Add(new ChoiceOption
                {
                    label = product.name,
                    cost = $"이자 {Percent(product.rateBp)}",
                    detail = enough
                        ? $"{product.days}일 뒤에 원금과 이자를 통장으로 받아요.  ({product.minAmount:N0} ~ {product.maxAmount:N0} GC)"
                        : $"지갑에 {product.minAmount:N0} GC가 있어야 넣을 수 있어요.",
                    enabled = enough,
                    onChosen = () => ShowTermAmounts(session, bank, teller, product),
                });
            }

            ChoiceMenu.Instance.Open(TermTitle,
                $"기다리면 이자를 많이 받아요. 중간에 찾으면 이자가 없어요.\n지갑  {bank.gc:N0} GC          정기예금  {bank.termGc:N0} GC",
                options.ToArray());
            ChoiceMenu.Instance.Tag = BankTag;
            ChoiceMenu.Instance.OnCancelled = () => ShowMain(session, bank, teller);
        }

        /// <summary>만기에 받을 이자. 가입할 때 서버가 같은 규칙으로 정한다.</summary>
        private static long TermInterestFor(TermProductDto product, long amount) => amount * product.rateBp / 10000;

        private static void ShowTermAmounts(GameSession session, BankDto bank, NpcAgent teller, TermProductDto product)
        {
            if (GameSession.Current != session) return;

            var options = new List<ChoiceOption>();
            for (int i = 0; i < Amounts.Length; i++)
            {
                long amount = Amounts[i];
                if (amount < product.minAmount || amount > product.maxAmount) continue;
                bool enough = bank.gc >= amount;
                options.Add(new ChoiceOption
                {
                    label = $"{amount:N0} GC",
                    cost = enough ? $"만기에 +{TermInterestFor(product, amount):N0} GC" : "",
                    detail = enough ? TermAfterText(product, amount) : "지갑에 그만큼 없어요.",
                    enabled = enough,
                    onChosen = () => OpenTerm(session, teller, product, amount),
                });
            }
            // 지갑의 돈을 모두, 다만 상품의 한도까지만.
            long most = Math.Min(bank.gc, product.maxAmount);
            bool canMost = most >= product.minAmount;
            options.Add(new ChoiceOption
            {
                label = bank.gc > product.maxAmount ? "가장 많이" : "전부",
                cost = canMost ? $"{most:N0} GC" : "",
                detail = canMost ? TermAfterText(product, most) : $"지갑에 {product.minAmount:N0} GC가 있어야 해요.",
                enabled = canMost,
                onChosen = () => OpenTerm(session, teller, product, most),
            });

            ChoiceMenu.Instance.Open(product.name,
                $"얼마를 넣을까요?  {product.days}일 동안 넣어 두면 이자 {Percent(product.rateBp)}를 받아요.\n지갑  {bank.gc:N0} GC",
                options.ToArray());
            ChoiceMenu.Instance.Tag = BankTag;
            ChoiceMenu.Instance.OnCancelled = () => ShowTerms(session, bank, teller);
        }

        private static string TermAfterText(TermProductDto product, long amount) =>
            $"{product.days}일 뒤에 통장으로  {amount + TermInterestFor(product, amount):N0} GC";

        private static void OpenTerm(GameSession session, NpcAgent teller, TermProductDto product, long amount)
        {
            session.Backend.OpenTermDeposit(product.code, amount, NewRequestId(), result =>
            {
                if (GameSession.Current != session) return;
                if (!result.ok)
                {
                    Debug.LogWarning($"정기예금 가입 실패 ({product.code} {amount}): {result.errorCode} {result.message}");
                    GameEvents.ShowToast(TermFailText(result.errorCode, true));
                    Open(teller);
                    return;
                }

                BankDto bank = result.data.bank;
                session.ApplyBank(bank);
                Sfx.Play(Sfx.Id.Coin);
                GameEvents.ShowToast($"{product.name}에 {result.data.amount:N0} GC를 넣었어요!  {product.days}일 뒤에 이자 +{result.data.interest:N0} GC");
                if (teller != null) teller.Say("만기까지 잘 맡아 둘게요!", 3.5f);
                ShowTerms(session, bank, teller);
            });
        }

        /// <summary>중간에 찾으면 이자를 잃는다는 것을 알리고 한 번 더 묻는다. 첫 줄이 "계속 넣어 두기"다.</summary>
        private static void ConfirmCancelTerm(GameSession session, BankDto bank, NpcAgent teller, TermProductDto product)
        {
            if (GameSession.Current != session) return;
            ChoiceMenu.Instance.Open($"{product.name} 중간에 찾기",
                $"지금 찾으면 이자 {product.interest:N0} GC를 받지 못해요.\n넣어 둔 {product.principal:N0} GC만 통장으로 돌아와요.",
                new ChoiceOption
                {
                    label = "계속 넣어 두기",
                    detail = product.daysLeft > 0 ? $"{product.daysLeft}일만 더 기다리면 이자를 받아요." : "곧 이자를 받아요.",
                    onChosen = () => ShowTerms(session, bank, teller),
                },
                new ChoiceOption
                {
                    label = "지금 찾기",
                    cost = $"통장 +{product.principal:N0} GC",
                    detail = "이자 없이 넣어 둔 돈만 돌려받아요.",
                    onChosen = () => CancelTerm(session, teller, product),
                });
            ChoiceMenu.Instance.Tag = BankTag;
            ChoiceMenu.Instance.OnCancelled = () => ShowTerms(session, bank, teller);
        }

        private static void CancelTerm(GameSession session, NpcAgent teller, TermProductDto product)
        {
            session.Backend.CancelTermDeposit(product.code, NewRequestId(), result =>
            {
                if (GameSession.Current != session) return;
                if (!result.ok)
                {
                    Debug.LogWarning($"정기예금 해지 실패 ({product.code}): {result.errorCode} {result.message}");
                    GameEvents.ShowToast(TermFailText(result.errorCode, false));
                    Open(teller);
                    return;
                }

                BankDto bank = result.data.bank;
                session.ApplyBank(bank);
                Sfx.Play(Sfx.Id.Coin);
                GameEvents.ShowToast($"{result.data.amount:N0} GC를 통장으로 돌려받았어요.  통장 {bank.bankGc:N0} GC");
                if (teller != null) teller.Say("여기 있어요!", 3.5f);
                ShowTerms(session, bank, teller);
            });
        }

        private static string TermFailText(string errorCode, bool opening)
        {
            switch (errorCode)
            {
                case ApiErrors.InsufficientFunds: return "지갑에 그만큼의 GC가 없어요.";
                case ApiErrors.Locked: return "아직 은행을 이용할 수 없어요.";
                case ApiErrors.Conflict: return "이미 가입한 정기예금이에요.";
                case ApiErrors.NotFound: return opening ? "지금은 가입할 수 없는 정기예금이에요." : "이미 만기가 되어 통장에 들어왔어요.";
                default: return opening ? "넣지 못했어요. 잠시 뒤에 다시 시도해 주세요." : "찾지 못했어요. 잠시 뒤에 다시 시도해 주세요.";
            }
        }

        // ── 거래 내역 ───────────────────────────────────────────

        private static void ShowHistory(GameSession session, BankDto bank, NpcAgent teller)
        {
            DialogueRunner.Instance.ShowPanel(HistoryTitle, HistoryText(bank), false, () => ShowMain(session, bank, teller));
        }

        /// <summary>거래 내역을 읽기 쉬운 줄로. 새것이 위에 온다.</summary>
        public static string HistoryText(BankDto bank)
        {
            var text = new StringBuilder();
            if (bank.recent != null)
            {
                for (int i = 0; i < bank.recent.Count; i++)
                {
                    BankEntryDto entry = bank.recent[i];
                    string sign = entry.delta >= 0 ? "+" : "−";
                    // 한 건이 한 줄에 들어가야 한다. 줄이 접히면 안내판이 넘친다.
                    text.Append(ShortDate(entry.date)).Append("   ").Append(ReasonText(entry.reason)).Append("  ")
                        .Append(sign).Append(Math.Abs(entry.delta).ToString("N0")).Append(" GC   →  통장 ")
                        .Append(entry.balanceAfter.ToString("N0")).Append(" GC\n");
                }
            }
            if (text.Length == 0) text.Append("아직 거래가 없어요.\n");
            text.Append('\n').Append("지금까지 받은 이자  ").Append(bank.interestTotal.ToString("N0")).Append(" GC");
            return text.ToString();
        }

        private static string ReasonText(string reason)
        {
            switch (reason)
            {
                case LedgerReasons.BankDeposit: return "맡김";
                case LedgerReasons.BankWithdraw: return "찾음";
                case LedgerReasons.BankInterest: return "이자";
                case LedgerReasons.TermMatured: return "정기예금 만기";
                case LedgerReasons.TermInterest: return "정기예금 이자";
                case LedgerReasons.TermCancel: return "정기예금 찾음";
                default: return "기타";
            }
        }

        /// <summary>"2026-11-13" → "11월 13일".</summary>
        private static string ShortDate(string date)
        {
            return DateTime.TryParseExact(date, BankRules.DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None,
                out DateTime day)
                ? $"{day.Month}월 {day.Day}일"
                : "";
        }
    }
}
