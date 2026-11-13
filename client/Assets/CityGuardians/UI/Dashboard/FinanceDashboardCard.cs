using System.Collections.Generic;
using CityGuardians.Domain;
using CityGuardians.Gameplay;
using CityGuardians.Services;
using UnityEngine;
using UnityEngine.UI;

namespace CityGuardians.UI
{
    /// <summary>
    /// 카드 1: 내 계좌 및 투자현황 (docs/FINANCE_DASHBOARD.md).
    /// 보유 GC, 은행 예금, 가상 주식 평가액, 평가손익, 대출 잔액, 순자산. 값은 모두 서버가 준 요약에서 온다.
    /// 서버에 아직 없는 항목은 "준비 중" 또는 "개발용 샘플" 꼬리표가 붙고, 순자산에 더해지지 않는다.
    /// </summary>
    public sealed class FinanceDashboardCard : DashboardCardBase
    {
        public override string CardId => DashboardCards.Finance;
        public override float ExpandedHeight => 240f;

        protected override void BuildBody(RectTransform body) => AddRows(body, 6, 6f);

        protected override void Render(DashboardSummaryDto summary, LiveDashboardController dashboard, bool expanded)
        {
            SetAccent(UiFactory.Accent, false);
            DashboardFinanceDto finance = summary != null ? summary.finance : null;
            if (finance == null)
            {
                SetKey("…", DashboardStyle.Dim);
                ShowMessage(DashboardCardText.Waiting(dashboard));
                return;
            }
            ShowRows(DashboardViews.Finance(finance, dashboard.SamplesAllowed));
            // 접힌 카드에는 핵심 수치 하나(순자산)가 남는다.
            SetKey(expanded ? "" : DashboardViews.FinanceKey(finance, Compact), UiFactory.Accent);
        }
    }

    internal static class DashboardCardText
    {
        /// <summary>요약을 아직 받지 못한 카드에 적는 말.</summary>
        public static string Waiting(LiveDashboardController dashboard)
        {
            bool offline = dashboard.Data != null && dashboard.Data.Connection == DashboardConnection.Offline;
            return offline ? "서버에 연결하지 못했어요.\n다시 이어지면 바로 보여 드릴게요." : "불러오는 중…";
        }
    }
}
