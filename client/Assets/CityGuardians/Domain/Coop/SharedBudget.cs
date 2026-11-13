using System;
using System.Collections.Generic;

namespace CityGuardians.Domain
{
    /// <summary>공동 예산 장부의 한 줄. 판이 끝나면 서버(defense_shared_budget)에 그대로 넘어간다.</summary>
    [Serializable]
    public sealed class BudgetLine
    {
        public int seq;
        /// <summary>쓴 사람의 공개 ID. 지급이면 null.</summary>
        public string pid;
        public string kind;
        public int delta;
        public int balanceAfter;
        /// <summary>타워 자리. 없으면 -1.</summary>
        public int slot = -1;
        public string code;
        /// <summary>전투 시작부터의 시간 (ms).</summary>
        public int atMs;
    }

    /// <summary>
    /// 팀이 한 판 안에서 함께 쓰는 예산 (에너지). GC 가 아니다.
    ///
    /// 쓰는 일은 한 번에 하나씩 일어난다: 잔액을 확인하고 빼는 것이 한 동작이라, 두 사람이 같은 순간에 사려고 해도
    /// 잔액이 음수가 되거나 한 번 빠질 것이 두 번 빠지지 않는다. 실시간 서버는 방마다 한 줄로 명령을 처리하므로
    /// 이 클래스 안에는 잠금이 없다 (여러 스레드에서 동시에 부르면 안 된다).
    /// 줄마다 "바뀐 뒤 잔액"을 적어 두어, 나중에 서버가 장부의 앞뒤를 다시 검사할 수 있다.
    /// </summary>
    public sealed class SharedBudget
    {
        private readonly List<BudgetLine> lines = new List<BudgetLine>();
        private readonly HashSet<int> occupied = new HashSet<int>();

        public int Balance { get; private set; }
        public int Granted { get; private set; }
        public int Spent { get; private set; }
        public IReadOnlyList<BudgetLine> Lines => lines;

        private BudgetLine Add(string pid, string kind, int delta, int slot, string code, int atMs)
        {
            Balance += delta;
            var line = new BudgetLine
            {
                seq = lines.Count + 1, pid = pid, kind = kind, delta = delta, balanceAfter = Balance, slot = slot, code = code,
                atMs = Math.Max(0, atMs),
            };
            lines.Add(line);
            return line;
        }

        /// <summary>예산을 넣는다 (시작, 웨이브를 막았을 때, 적을 쓰러뜨렸을 때). 0 이하는 넣지 않는다.</summary>
        public BudgetLine Grant(string kind, int amount, int atMs)
        {
            if (CoopBudgetKinds.Sign(kind) != 1) throw new ArgumentException("지급이 아닌 종류: " + kind);
            if (amount <= 0) return null;
            Granted += amount;
            return Add(null, kind, amount, -1, null, atMs);
        }

        /// <summary>
        /// 예산을 쓴다. 잔액이 모자라면 아무것도 바뀌지 않고 false.
        /// 설치(build)는 빈 자리에만, 강화(upgrade)는 타워가 있는 자리에만 된다.
        /// </summary>
        public bool TrySpend(string pid, string kind, int cost, int slot, string code, int atMs, out BudgetLine line)
        {
            line = null;
            if (CoopBudgetKinds.Sign(kind) != -1) throw new ArgumentException("지출이 아닌 종류: " + kind);
            if (string.IsNullOrEmpty(pid) || cost < 0 || cost > Balance) return false;
            if (kind == CoopBudgetKinds.Build)
            {
                if (slot < 0 || occupied.Contains(slot)) return false;
                occupied.Add(slot);
            }
            else if (slot < 0 || !occupied.Contains(slot))
            {
                return false;
            }
            Spent += cost;
            line = Add(pid, kind, -cost, slot, code, atMs);
            return true;
        }

        /// <summary>타워를 잃었다: 금액은 그대로이고 자리만 빈다.</summary>
        public BudgetLine Lost(int slot, string code, int atMs)
        {
            if (!occupied.Remove(slot)) return null;
            return Add(null, CoopBudgetKinds.Lost, 0, slot, code, atMs);
        }

        public bool IsOccupied(int slot) => occupied.Contains(slot);

        /// <summary>
        /// 장부의 앞뒤가 맞는지 검사한다. 맞으면 null, 아니면 이유. 서버(Coop::checkBudget)와 같은 검사다:
        /// 줄 번호가 1부터 이어지고, 잔액이 이어지고, 음수가 된 적이 없고, 이미 타워가 있는 자리에 다시 세운 적이 없다.
        /// </summary>
        public static string FindInconsistency(IReadOnlyList<BudgetLine> lines, ICollection<string> memberPids)
        {
            int balance = 0;
            var taken = new HashSet<int>();
            for (int i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                if (line == null || line.kind == null) return "budget_format";
                if (line.seq != i + 1) return "budget_sequence";
                int sign = CoopBudgetKinds.Sign(line.kind);
                if (sign == 2) return "budget_kind";
                if ((sign > 0 && line.delta < 0) || (sign < 0 && line.delta > 0) || (sign == 0 && line.delta != 0)) return "budget_sign";
                balance += line.delta;
                if (balance < 0) return "budget_negative";
                if (balance != line.balanceAfter) return "budget_balance";
                if (sign < 0 && (line.pid == null || (memberPids != null && !memberPids.Contains(line.pid)))) return "budget_spender";
                if (line.kind == CoopBudgetKinds.Build)
                {
                    if (line.slot < 0 || !taken.Add(line.slot)) return "budget_duplicate_slot";
                }
                else if (line.kind == CoopBudgetKinds.Lost && line.slot >= 0)
                {
                    taken.Remove(line.slot);
                }
                else if (line.kind == CoopBudgetKinds.Upgrade && (line.slot < 0 || !taken.Contains(line.slot)))
                {
                    return "budget_upgrade_empty_slot";
                }
            }
            return null;
        }

        public string FindInconsistency(ICollection<string> memberPids = null) => FindInconsistency(lines, memberPids);
    }
}
