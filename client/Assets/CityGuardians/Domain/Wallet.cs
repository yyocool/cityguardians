using System;
using System.Collections.Generic;

namespace CityGuardians.Domain
{
    public static class LedgerReasons
    {
        public const string HuntReward = "hunt_reward";
        public const string QuestReward = "quest_reward";
        public const string BattleReward = "battle_reward";
        public const string TowerUnlock = "tower_unlock";
        public const string ResidencyBonus = "residency_bonus";
        public const string ShopPurchase = "shop_purchase";
        public const string BankDeposit = "bank_deposit";
        public const string BankWithdraw = "bank_withdraw";
        public const string BankInterest = "bank_interest";
        /// <summary>상점에 되판 값.</summary>
        public const string ShopSale = "shop_sale";
        /// <summary>정기예금 가입 (지갑에서 나감).</summary>
        public const string TermOpen = "term_open";
        /// <summary>정기예금 만기: 원금이 통장으로 들어옴.</summary>
        public const string TermMatured = "term_matured";
        /// <summary>정기예금 만기: 약속한 이자가 통장으로 들어옴.</summary>
        public const string TermInterest = "term_interest";
        /// <summary>정기예금 중도 해지: 원금만 통장으로 들어옴.</summary>
        public const string TermCancel = "term_cancel";
    }

    /// <summary>원장의 한 행 (docs/ECONOMY_SYSTEM.md 3장). 추가만 하고 고치지 않는다.</summary>
    [Serializable]
    public sealed class LedgerEntry
    {
        public long seq;
        public long delta;
        public long balanceAfter;
        public string reason;
        public string reference;
        public string createdAt;
    }

    public sealed class InsufficientFundsException : Exception
    {
        public InsufficientFundsException(long balance, long delta)
            : base($"잔액이 모자랍니다. 잔액 {balance}, 변경 {delta}") { }
    }

    /// <summary>
    /// GC 잔액과 원장. 모든 증감은 Apply를 지나가며 원장에 남는다.
    /// 잔액은 항상 마지막 원장 행의 balanceAfter와 같다.
    /// </summary>
    public sealed class Wallet
    {
        private readonly List<LedgerEntry> _ledger;
        private long _nextSeq;

        public long Balance { get; private set; }
        public IReadOnlyList<LedgerEntry> Ledger => _ledger;

        public Wallet() : this(0, new List<LedgerEntry>(), 1) { }

        public Wallet(long balance, List<LedgerEntry> ledger, long nextSeq)
        {
            if (balance < 0) throw new ArgumentOutOfRangeException(nameof(balance));
            Balance = balance;
            _ledger = ledger ?? new List<LedgerEntry>();
            _nextSeq = Math.Max(1, nextSeq);
        }

        public long NextSeq => _nextSeq;

        public LedgerEntry Apply(long delta, string reason, string reference, DateTime utcNow)
        {
            if (string.IsNullOrEmpty(reason)) throw new ArgumentException("사유가 필요합니다.", nameof(reason));
            if (delta == 0) throw new ArgumentException("0인 거래는 기록하지 않습니다.", nameof(delta));
            long after = checked(Balance + delta);
            if (after < 0) throw new InsufficientFundsException(Balance, delta);

            var entry = new LedgerEntry
            {
                seq = _nextSeq++,
                delta = delta,
                balanceAfter = after,
                reason = reason,
                reference = reference ?? string.Empty,
                createdAt = utcNow.ToString("o"),
            };
            Balance = after;
            _ledger.Add(entry);
            return entry;
        }

        /// <summary>원장과 잔액이 맞는지 검사한다. 어긋난 곳이 있으면 설명을 돌려준다.</summary>
        public string FindInconsistency(long openingBalance)
        {
            long running = openingBalance;
            for (int i = 0; i < _ledger.Count; i++)
            {
                running += _ledger[i].delta;
                if (running < 0) return $"원장 {_ledger[i].seq}행에서 잔액이 음수";
                if (_ledger[i].balanceAfter != running) return $"원장 {_ledger[i].seq}행의 거래 후 잔액 불일치";
            }
            return running == Balance ? null : "지갑 잔액과 원장 합계 불일치";
        }
    }
}
