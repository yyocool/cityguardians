using System;
using CityGuardians.Domain;
using CityGuardians.Services;

namespace CityGuardians.Gameplay
{
    /// <summary>
    /// 대시보드 카드의 "상세 보기"와 대시보드 설정 (docs/LIVE_DASHBOARD.md 5장).
    ///
    /// 상세 화면을 새로 만들지 않는다. 은행, 도시 방어 현황, 도시 살림, 지역 랭킹, 지원 요청, 전국 지도는
    /// 이미 있는 화면을 그대로 열고, 그 화면들이 기존 API 로 값을 받는다.
    /// 여기서 여는 은행 화면은 읽기 전용이다: 맡기기·찾기는 은행에서만 한다.
    /// </summary>
    public static class DashboardActions
    {
        public const string MenuTag = "dashboard";

        /// <summary>상세 보기와 설정은 걸어 다닐 때만 연다. 전투 중에는 시야와 조작이 먼저다.</summary>
        public static bool CanOpen(out string reason)
        {
            reason = null;
            LiveDashboardController dashboard = LiveDashboardController.Instance;
            if (GameSession.Current == null || dashboard == null || !dashboard.Available) return false;
            if (dashboard.State.InBattle)
            {
                reason = "전투가 끝나면 자세히 볼 수 있어요.";
                return false;
            }
            // 다른 화면(대화, 메뉴, 지도)이 떠 있거나 방을 꾸미는 중이면 조용히 넘어간다.
            return !GameState.IsControlLocked && !GameState.Decorating;
        }

        private static bool Begin()
        {
            if (CanOpen(out string reason))
            {
                Sfx.Play(Sfx.Id.Interact);
                return true;
            }
            if (reason != null) GameEvents.ShowToast(reason);
            return false;
        }

        public static void OpenDetail(string cardId)
        {
            if (!Begin()) return;
            switch (cardId)
            {
                case DashboardCards.Finance: ShowFinance(); break;
                case DashboardCards.City: ShowCity(); break;
                case DashboardCards.National: ShowNational(); break;
            }
        }

        private static bool Alive(GameSession session) => GameSession.Current == session;

        private static void Panel(string title, string body, Action back) =>
            DialogueRunner.Instance.ShowPanel(title, body, false, back);

        // ── 카드 1: 내 계좌 및 투자현황 ─────────────────────────

        private static void ShowFinance()
        {
            GameSession session = GameSession.Current;
            LiveDashboardController dashboard = LiveDashboardController.Instance;
            if (session == null || dashboard == null) return;
            DashboardSummaryDto summary = dashboard.Summary;
            if (summary == null || summary.finance == null)
            {
                GameEvents.ShowToast("아직 계좌 정보를 받지 못했어요. 잠시 뒤에 다시 열어 주세요.");
                return;
            }

            DashboardFinanceDto finance = summary.finance;
            bool samples = dashboard.SamplesAllowed;
            Action reopen = ShowFinance;
            string stockNote = finance.stocksAvailable ? "" : samples ? DashboardSamples.Tag : DashboardSamples.NotReadyTag;
            string loanNote = finance.loansAvailable ? "" : samples ? DashboardSamples.Tag : DashboardSamples.NotReadyTag;
            string worth = (finance.netWorthPartial ? "순자산 (있는 항목만)  " : "순자산  ") + DashboardFormat.Gc(finance.netWorthGc);

            ChoiceMenu.Instance.Open(DashboardCards.FullTitle(DashboardCards.Finance),
                $"보유 GC  {DashboardFormat.Gc(finance.gc)}      {worth}\n{dashboard.StatusLine}",
                new ChoiceOption
                {
                    label = "은행 계좌",
                    cost = finance.bankAvailable ? DashboardFormat.Gc(finance.bankGc + finance.termGc) : "",
                    detail = "통장과 정기예금의 잔액, 이자를 봐요. 맡기고 찾는 일은 은행에서 해요.",
                    onChosen = () => ShowBank(session, false, reopen),
                },
                new ChoiceOption
                {
                    label = "예금 거래 내역",
                    detail = "통장에 들어오고 나간 돈을 최근 것부터 봐요.",
                    onChosen = () => ShowBank(session, true, reopen),
                },
                new ChoiceOption
                {
                    label = "주식 보유 종목",
                    cost = stockNote,
                    detail = "가상 주식의 종목별 수량과 평가손익이에요.",
                    onChosen = () => Panel("주식 보유 종목", DashboardViews.StocksText(samples), reopen),
                },
                new ChoiceOption
                {
                    label = "주식 거래 내역",
                    cost = stockNote,
                    detail = "가상 주식을 사고판 기록이에요.",
                    onChosen = () => Panel("주식 거래 내역", DashboardViews.StockTradesText(samples), reopen),
                },
                new ChoiceOption
                {
                    label = "대출 상환 일정",
                    cost = loanNote,
                    detail = "게임 속 대출의 남은 금액과 갚을 날짜예요. 현실의 돈과는 관계가 없어요.",
                    onChosen = () => Panel("대출 상환 일정", DashboardViews.LoanText(samples), reopen),
                },
                new ChoiceOption
                {
                    label = "자산 변동 그래프",
                    cost = DashboardFormat.Sparkline(DashboardViews.TrendValues(finance)),
                    detail = "지난 7일 동안 보유 GC 가 어떻게 달라졌는지 봐요.",
                    onChosen = () => Panel("자산 변동 (7일)", DashboardViews.TrendText(finance), reopen),
                });
            ChoiceMenu.Instance.Tag = MenuTag;
        }

        /// <summary>은행 계좌를 읽기만 한다. 값은 기존 은행 API (GET /bank) 가 준 그대로다.</summary>
        private static void ShowBank(GameSession session, bool history, Action back)
        {
            session.Backend.GetBank(result =>
            {
                if (!Alive(session)) return;
                if (!result.ok)
                {
                    GameEvents.ShowToast("은행 정보를 불러오지 못했어요. 잠시 뒤에 다시 시도해 주세요.");
                    back?.Invoke();
                    return;
                }

                BankDto bank = result.data;
                session.ApplyBank(bank);
                if (!bank.unlocked)
                {
                    Panel("은행 계좌", $"은행은 레벨 {bank.unlockLevel}부터 이용할 수 있어요.\n지금 레벨  {session.Me.level}", back);
                    return;
                }
                if (history)
                {
                    Panel("예금 거래 내역", BankActions.HistoryText(bank), back);
                    return;
                }
                string term = bank.termGc > 0
                    ? $"정기예금  {bank.termGc:N0} GC" + (bank.termMaturedCount > 0 ? $"      만기가 된 예금 {bank.termMaturedCount}건" : "")
                    : "정기예금  없음";
                Panel("은행 계좌",
                    BankActions.BalanceLine(bank) + "\n" + term + "\n\n" + BankActions.InterestLine(bank)
                    + $"\n지금까지 받은 이자  {bank.interestTotal:N0} GC\n\n맡기고 찾는 일은 은행에서 해요.", back);
            });
        }

        // ── 카드 2: 우리 도시 현황 ──────────────────────────────

        private static void ShowCity()
        {
            GameSession session = GameSession.Current;
            LiveDashboardController dashboard = LiveDashboardController.Instance;
            if (session == null || dashboard == null) return;
            if (!session.IsResident)
            {
                GameEvents.ShowToast("전입신고를 하면 우리 도시의 현황을 볼 수 있어요.");
                return;
            }

            DashboardSummaryDto summary = dashboard.Summary;
            DashboardCityDto city = summary != null ? summary.myCity : null;
            Action reopen = ShowCity;
            string name = RegionService.ShortName(session.Me.regionCode) ?? "우리 도시";
            string body = city != null && city.resident
                ? $"{DashboardViews.StageBadge(city.stage)} · 위험도 {city.risk}      {DashboardViews.InvasionLine(city)}\n{dashboard.StatusLine}"
                : dashboard.StatusLine;

            ChoiceMenu.Instance.Open(name + " 현황", body,
                new ChoiceOption
                {
                    label = "도시 방어 현황",
                    cost = city != null && city.resident ? "방어력 " + city.defensePower.ToString("N0") : "",
                    detail = "방어 시설, 침공, 내 기여도를 보고 도시를 도와요.",
                    onChosen = () => CityActions.Open(reopen),
                },
                new ChoiceOption
                {
                    label = "도시 재정과 지방세",
                    cost = city != null && city.resident ? DashboardFormat.Gc(city.treasuryGc) : "",
                    detail = "걷힌 세금과 쓰인 곳, 세금 정책, 참여예산을 봐요.",
                    onChosen = () => CityOpsActions.Open(reopen),
                },
                new ChoiceOption
                {
                    label = "성장 단계와 상징거리",
                    cost = city != null && city.resident && city.growthStage > 0 ? city.growthStage + "단계" : "",
                    detail = "전입자 수에 따른 성장 단계와 상징거리 모델링 현황이에요.",
                    enabled = city != null && city.resident,
                    onChosen = () => Panel("성장 단계와 상징거리", DashboardViews.GrowthText(city), reopen),
                },
                new ChoiceOption
                {
                    label = "지역 순위",
                    cost = city != null && city.rank > 0 ? city.rank + "위" : "",
                    detail = "전국 도시의 랭킹과 우리 도시의 순위를 봐요.",
                    onChosen = () => CityActions.OpenRankings(reopen),
                },
                new ChoiceOption
                {
                    label = "긴급 지원 요청",
                    cost = city != null && city.supportOpen ? "요청 중" : "",
                    detail = "도움을 청한 이웃 도시를 보고 지원하러 가요.",
                    onChosen = () => CityActions.OpenSupport(reopen),
                });
            ChoiceMenu.Instance.Tag = MenuTag;
        }

        // ── 카드 3: 전국 지도 기반 현황 ─────────────────────────

        private static void ShowNational()
        {
            MapController map = MapController.Instance;
            // 전국 지도는 이미 있다: 확대·축소하고, 시·군·구마다 위험도를 볼 수 있다.
            if (map.CanOpen(out string reason) && map.Open()) return;

            // 지도를 열 수 없는 곳에서는 같은 내용을 글로 보여 준다.
            LiveDashboardController dashboard = LiveDashboardController.Instance;
            DashboardSummaryDto summary = dashboard != null ? dashboard.Summary : null;
            if (summary == null || summary.national == null)
            {
                GameEvents.ShowToast(string.IsNullOrEmpty(reason) ? "아직 전국 현황을 받지 못했어요." : reason);
                return;
            }
            Panel("전국 현황", DashboardViews.NationalText(summary.national), null);
        }

        // ── 대시보드 설정 ───────────────────────────────────────

        public static void OpenSettings()
        {
            if (!Begin()) return;
            ShowSettings();
        }

        private static void ShowSettings()
        {
            LiveDashboardController dashboard = LiveDashboardController.Instance;
            if (dashboard == null) return;
            DashboardState state = dashboard.State;
            Action reopen = ShowSettings;

            ChoiceMenu.Instance.Open("대시보드 설정",
                $"{DashboardCards.ToggleAllKeyName} 전체 숨기기 · F7 계좌 · F8 우리 도시 · F9 전국\n" +
                "Shift + 카드 키  상세 보기      Ctrl + 카드 키  그 카드만 숨기기      왼쪽 Alt 를 누른 채 마우스로 누르기",
                new ChoiceOption
                {
                    label = "글자와 카드 크기",
                    cost = state.Prefs.uiScalePercent + "%",
                    detail = "누를 때마다 80% → 100% → 120% → 140% 로 바뀌어요.",
                    onChosen = () =>
                    {
                        state.CycleScale();
                        dashboard.SaveNow();
                        reopen();
                    },
                },
                new ChoiceOption
                {
                    label = "타워디펜스에서 카드 자리",
                    cost = DashboardPlacements.DisplayName(state.Prefs.defensePlacement),
                    detail = "방어전에서는 미니맵과 웨이브 정보가 먼저예요. 카드를 접어 둘지, 어디에 둘지 골라요.",
                    onChosen = () =>
                    {
                        state.CyclePlacement();
                        dashboard.SaveNow();
                        reopen();
                    },
                },
                new ChoiceOption
                {
                    label = "기본값으로 되돌리기",
                    detail = "카드의 펼침·숨김, 크기, 자리를 처음 상태로 돌려요.",
                    onChosen = () =>
                    {
                        state.ResetToDefaults();
                        dashboard.SaveNow();
                        GameEvents.ShowToast("대시보드를 기본값으로 되돌렸어요.");
                        reopen();
                    },
                });
            ChoiceMenu.Instance.Tag = MenuTag;
        }
    }
}
