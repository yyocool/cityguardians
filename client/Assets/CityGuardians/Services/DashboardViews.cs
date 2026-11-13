using System.Collections.Generic;
using System.Text;
using CityGuardians.Domain;

namespace CityGuardians.Services
{
    /// <summary>카드의 한 줄: 이름과 값. 색을 정하는 것은 화면이고, 여기서는 뜻만 정한다.</summary>
    public sealed class DashboardRow
    {
        public const int ToneNormal = 0;
        public const int ToneGood = 1;
        public const int ToneBad = 2;
        public const int ToneDim = 3;

        public string label;
        public string value;
        /// <summary>값 앞에 붙는 꼬리표. "개발용 샘플" 또는 "준비 중". 실제 값이면 null.</summary>
        public string tag;
        public int tone;
        /// <summary>굵게 보이는 줄 (순자산).</summary>
        public bool strong;
        /// <summary>RiskStages. 위험도 줄에만 있다. 화면이 단계의 색과 막대를 그린다.</summary>
        public string stage;
        /// <summary>0~100. 막대로 그리는 값. 없으면 −1.</summary>
        public int bar = -1;
    }

    /// <summary>
    /// 요약을 카드에 쓰는 줄로 바꾼다 (docs/FINANCE_DASHBOARD.md, CITY_STATUS_DASHBOARD.md, NATIONAL_MAP_DASHBOARD.md).
    ///
    /// 여기서 하는 일은 서버가 준 값을 글자로 옮기는 것뿐이다. 금액을 더하거나 빼서 새 값을 만들지 않는다
    /// (순자산도 서버가 준 값이다). 서버에 없는 금융 항목은 "준비 중"이라고 쓰거나, 관리자가 허용했을 때에만
    /// "개발용 샘플"이라는 꼬리표와 함께 샘플 값을 쓴다.
    /// </summary>
    public static class DashboardViews
    {
        // ── 내 계좌 ─────────────────────────────────────────

        public static List<DashboardRow> Finance(DashboardFinanceDto f, bool samples)
        {
            var rows = new List<DashboardRow>
            {
                new DashboardRow { label = "보유 GC", value = DashboardFormat.Gc(f.gc) },
                Asset("은행 예금", f.bankAvailable, f.bankGc + f.termGc, false, 0),
                Asset("가상 주식 평가액", f.stocksAvailable, f.stockValueGc, samples, DashboardSamples.StockValue),
                Profit(f, samples),
                Asset("대출 잔액", f.loansAvailable, f.loanBalanceGc, samples, DashboardSamples.LoanBalance),
                new DashboardRow
                {
                    label = f.netWorthPartial ? "순자산 (있는 항목만)" : "순자산",
                    value = DashboardFormat.Gc(f.netWorthGc), strong = true,
                },
            };
            return rows;
        }

        private static DashboardRow Asset(string label, bool available, long amount, bool samples, long sample)
        {
            if (available) return new DashboardRow { label = label, value = DashboardFormat.Gc(amount) };
            if (samples && sample != 0)
                return new DashboardRow { label = label, value = DashboardFormat.Gc(sample), tag = DashboardSamples.Tag, tone = DashboardRow.ToneDim };
            return new DashboardRow { label = label, value = "—", tag = DashboardSamples.NotReadyTag, tone = DashboardRow.ToneDim };
        }

        private static DashboardRow Profit(DashboardFinanceDto f, bool samples)
        {
            if (f.stocksAvailable)
            {
                return new DashboardRow
                {
                    label = "평가손익", value = DashboardFormat.SignedGc(f.stockProfitGc),
                    tone = f.stockProfitGc > 0 ? DashboardRow.ToneGood : f.stockProfitGc < 0 ? DashboardRow.ToneBad : DashboardRow.ToneNormal,
                };
            }
            if (samples)
                return new DashboardRow { label = "평가손익", value = DashboardFormat.SignedGc(DashboardSamples.StockProfit), tag = DashboardSamples.Tag, tone = DashboardRow.ToneDim };
            return new DashboardRow { label = "평가손익", value = "—", tag = DashboardSamples.NotReadyTag, tone = DashboardRow.ToneDim };
        }

        /// <summary>접힌 카드에 남는 수 하나: 순자산.</summary>
        public static string FinanceKey(DashboardFinanceDto f, bool compact) =>
            compact ? "GC " + DashboardFormat.Compact(f.netWorthGc) : "순자산 " + DashboardFormat.Gc(f.netWorthGc);

        /// <summary>자산 변동 그래프에 쓰는 값들 (오래된 날이 앞).</summary>
        public static List<long> TrendValues(DashboardFinanceDto f)
        {
            var values = new List<long>();
            for (int i = 0; f.trend != null && i < f.trend.Count; i++) values.Add(f.trend[i].gc);
            return values;
        }

        /// <summary>상세 화면의 자산 변동: 글자 그래프와 날짜별 잔액.</summary>
        public static string TrendText(DashboardFinanceDto f)
        {
            List<long> values = TrendValues(f);
            if (values.Count == 0) return "아직 기록이 없어요.";
            var text = new StringBuilder();
            text.Append("최근 ").Append(values.Count).Append("일의 하루 끝 보유 GC예요.\n\n");
            text.Append("    ").Append(DashboardFormat.Sparkline(values)).Append("\n\n");
            for (int i = 0; i < f.trend.Count; i++)
            {
                DashboardTrendPointDto day = f.trend[i];
                long change = i > 0 ? day.gc - f.trend[i - 1].gc : 0;
                text.Append(ShortDate(day.date)).Append("    ").Append(DashboardFormat.Gc(day.gc));
                if (i > 0 && change != 0) text.Append("    ").Append(DashboardFormat.SignedGc(change));
                text.Append('\n');
            }
            text.Append("\n은행 예금, 가상 주식, 대출이 준비되면 순자산의 변동으로 바뀌어요.");
            return text.ToString();
        }

        /// <summary>"2026-11-14" → "11-14".</summary>
        private static string ShortDate(string date) => !string.IsNullOrEmpty(date) && date.Length >= 10 ? date.Substring(5, 5) : date ?? "";

        private const string SampleNote = "※ 개발용 샘플이에요. 실제 데이터가 아니고, 내 자산에 더해지지 않아요.";
        private const string NotReadyNote = "아직 준비 중인 기능이에요.";

        public static string StocksText(bool samples)
        {
            if (!samples) return NotReadyNote + "\n가상 주식이 열리면 보유 종목이 여기에 보여요.";
            var text = new StringBuilder(SampleNote).Append("\n\n");
            for (int i = 0; i < DashboardSamples.Holdings.Length; i++)
            {
                SampleHolding h = DashboardSamples.Holdings[i];
                text.Append(h.name).Append("   ").Append(h.quantity).Append("주   평가액 ").Append(DashboardFormat.Gc(h.Value))
                    .Append("   ").Append(DashboardFormat.SignedGc(h.Profit)).Append('\n');
            }
            text.Append("\n평가액 합계  ").Append(DashboardFormat.Gc(DashboardSamples.StockValue))
                .Append("      평가손익  ").Append(DashboardFormat.SignedGc(DashboardSamples.StockProfit));
            return text.ToString();
        }

        public static string StockTradesText(bool samples)
        {
            if (!samples) return NotReadyNote + "\n가상 주식이 열리면 거래 내역이 여기에 보여요.";
            var text = new StringBuilder(SampleNote).Append("\n\n");
            for (int i = 0; i < DashboardSamples.Trades.Length; i++)
            {
                SampleTrade t = DashboardSamples.Trades[i];
                text.Append(t.when).Append("   ").Append(t.side).Append("   ").Append(t.name).Append("   ").Append(t.quantity).Append("주 × ")
                    .Append(DashboardFormat.Gc(t.price)).Append('\n');
            }
            return text.ToString();
        }

        public static string LoanText(bool samples)
        {
            if (!samples) return NotReadyNote + "\n대출이 열리면 상환 일정이 여기에 보여요.";
            var text = new StringBuilder(SampleNote).Append("\n\n");
            for (int i = 0; i < DashboardSamples.Installments.Length; i++)
            {
                SampleInstallment s = DashboardSamples.Installments[i];
                text.Append(s.number).Append("회차   ").Append(s.due).Append("   원금 ").Append(DashboardFormat.Gc(s.principal))
                    .Append("   이자 ").Append(DashboardFormat.Gc(s.interest)).Append('\n');
            }
            text.Append("\n남은 원금  ").Append(DashboardFormat.Gc(DashboardSamples.LoanBalance))
                .Append("\n\n게임 안의 대출은 GC로만 갚아요. 실제 돈이나 현실의 신용과는 관계가 없어요.");
            return text.ToString();
        }

        // ── 우리 도시 ───────────────────────────────────────

        public static string StageBadge(string stage) => RiskStages.Icon(stage) + " " + RiskStages.DisplayName(stage);

        public static string InvasionLine(DashboardCityDto c)
        {
            if (!c.HasInvasion) return "침공 없음";
            return "■ " + InvasionKinds.DisplayName(c.invasionKind) + "  " + c.invasionProgress + " / " + c.invasionRequired;
        }

        public static List<DashboardRow> City(DashboardCityDto c)
        {
            return new List<DashboardRow>
            {
                new DashboardRow { label = "위험도", value = c.risk + "  " + StageBadge(c.stage), stage = c.stage, bar = c.risk },
                new DashboardRow { label = "방어력", value = c.defensePower.ToString("N0") },
                new DashboardRow { label = "유효 전입자", value = c.validResidents.ToString("N0") + "명" },
                new DashboardRow { label = "도시 재정", value = DashboardFormat.Gc(c.treasuryGc) },
                new DashboardRow
                {
                    label = "침공", value = InvasionLine(c),
                    tone = c.HasInvasion ? DashboardRow.ToneBad : DashboardRow.ToneDim,
                },
                new DashboardRow { label = "내 기여도", value = c.myContribution.ToString("N0") },
            };
        }

        /// <summary>접힌 카드에 남는 수 하나: 위험도와 단계 (색 없이도 읽히도록 모양과 글자를 함께).</summary>
        public static string CityKey(DashboardCityDto c, bool compact)
        {
            if (!c.resident) return compact ? "전입 전" : "전입신고 전";
            return compact ? RiskStages.Icon(c.stage) + " " + c.risk : "위험도 " + c.risk + "  " + StageBadge(c.stage);
        }

        /// <summary>
        /// 접힌 도시 카드의 머리 줄. 침공 중이면 위험도보다 침공을 먼저 알린다
        /// (접혀 있어도, 전투 중에도 도시의 급한 일은 보여야 한다).
        /// </summary>
        public static string CityHeader(DashboardCityDto c, bool compact)
        {
            if (!c.resident || !c.HasInvasion) return CityKey(c, compact);
            return compact ? "■ 침공" : "■ " + InvasionKinds.DisplayName(c.invasionKind) + " · 위험도 " + c.risk;
        }

        /// <summary>카드의 경고 줄. 경고가 없으면 null.</summary>
        public static string CityAlertText(DashboardCityDto c)
        {
            if (!c.alert) return null;
            switch (c.alertReason)
            {
                case DashboardAlertReasons.Invasion: return "■ " + InvasionKinds.DisplayName(c.invasionKind) + "! 도시를 지켜 주세요";
                case DashboardAlertReasons.Emergency: return "■ 긴급 단계예요. 도시를 지켜 주세요";
                default: return "◆ 위험도가 높아요";
            }
        }

        /// <summary>상세 화면의 성장 단계와 상징거리 모델링 현황.</summary>
        public static string GrowthText(DashboardCityDto c)
        {
            string stage = string.IsNullOrEmpty(c.growthStageName) ? "아직 첫 단계 전이에요" : c.growthStage + "단계 · " + c.growthStageName;
            return "유효 전입자  " + c.validResidents.ToString("N0") + "명\n"
                + "도시 성장 단계  " + stage + "\n"
                + "상징거리 모델링  " + ModelingStatuses.DisplayName(c.modelingStatus);
        }

        // ── 전국 ────────────────────────────────────────────

        public static List<DashboardRow> National(DashboardNationalDto n)
        {
            DashboardRankDto first = n.top != null && n.top.Count > 0 ? n.top[0] : null;
            return new List<DashboardRow>
            {
                new DashboardRow { label = "전국 평균 안전도", value = n.averageSafety.ToString(), bar = n.averageSafety },
                new DashboardRow
                {
                    label = "긴급 도시", value = n.emergencyCities > 0 ? "■ " + n.emergencyCities + "곳" : "없음",
                    tone = n.emergencyCities > 0 ? DashboardRow.ToneBad : DashboardRow.ToneDim,
                },
                new DashboardRow { label = "우리 도시 순위", value = n.homeRank > 0 ? n.homeRank + "위 / " + n.totalCities : "—" },
                new DashboardRow { label = "1위 도시", value = first != null ? first.name + "  " + first.score + "점" : "—" },
                new DashboardRow
                {
                    label = "지원 요청", value = n.supportRequests > 0 ? n.supportRequests + "곳" : "없음",
                    tone = n.supportRequests > 0 ? DashboardRow.ToneNormal : DashboardRow.ToneDim,
                },
            };
        }

        /// <summary>접힌 카드에 남는 수 하나: 긴급 도시가 있으면 그 수, 없으면 평균 안전도.</summary>
        public static string NationalKey(DashboardNationalDto n, bool compact)
        {
            if (n.emergencyCities > 0) return compact ? "■ " + n.emergencyCities + "곳" : "■ 긴급 " + n.emergencyCities + "곳";
            return compact ? "● " + n.averageSafety : "● 안전도 " + n.averageSafety;
        }

        /// <summary>전체 지도를 열 수 없을 때(전투 중)의 상세: 숫자와 목록.</summary>
        public static string NationalText(DashboardNationalDto n)
        {
            var text = new StringBuilder();
            text.Append("전국 평균 안전도  ").Append(n.averageSafety).Append("      긴급 ").Append(n.emergencyCities).Append("곳  ·  위험 ")
                .Append(n.dangerCities).Append("곳  ·  침공 ").Append(n.invasionCities).Append("곳\n");
            text.Append("우리 도시 순위  ").Append(n.homeRank > 0 ? n.homeRank + "위" : "—").Append(" / ").Append(n.totalCities)
                .Append("      지원 요청  ").Append(n.supportRequests).Append("곳\n\n");
            text.Append("지역 순위\n");
            for (int i = 0; n.top != null && i < n.top.Count; i++)
            {
                DashboardRankDto row = n.top[i];
                text.Append(row.rank).Append("위   ").Append(row.name).Append("   ").Append(row.score).Append("점   ")
                    .Append(StageBadge(row.stage)).Append(' ').Append(row.risk).Append('\n');
            }
            if (n.urgent != null && n.urgent.Count > 0)
            {
                text.Append("\n급한 도시\n");
                for (int i = 0; i < n.urgent.Count; i++)
                {
                    DashboardUrgentDto row = n.urgent[i];
                    text.Append(StageBadge(row.stage)).Append("   ").Append(row.name).Append("   위험도 ").Append(row.risk);
                    if (!string.IsNullOrEmpty(row.invasionKind)) text.Append("   ").Append(InvasionKinds.DisplayName(row.invasionKind));
                    text.Append('\n');
                }
            }
            return text.ToString();
        }
    }
}
