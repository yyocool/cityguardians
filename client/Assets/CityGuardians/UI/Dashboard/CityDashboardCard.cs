using System.Collections.Generic;
using CityGuardians.Domain;
using CityGuardians.Gameplay;
using CityGuardians.Services;
using UnityEngine;
using UnityEngine.UI;

namespace CityGuardians.UI
{
    /// <summary>
    /// 카드 2: 우리 도시 현황 (docs/CITY_STATUS_DASHBOARD.md).
    /// 도시 이름, 위험도, 방어력, 유효 전입자 수, 도시 재정, 침공 상태, 내 기여도.
    /// 위험도 단계는 색과 함께 모양(●▲◆■)과 낱말로 적고, 급한 일이 있으면 경고 줄이 붉게 켜진다.
    /// </summary>
    public sealed class CityDashboardCard : DashboardCardBase
    {
        public override string CardId => DashboardCards.City;
        public override float ExpandedHeight => 270f;

        /// <summary>경고 줄의 글자. 경고가 없으면 null.</summary>
        public string AlertText => _alert != null && _alertOn ? _alertText.text : null;

        private Image _alert;
        private Text _alertText;
        private bool _alertOn;

        protected override void BuildBody(RectTransform body)
        {
            AddRows(body, 6, 6f);
            _alert = DashboardStyle.RoundedPanel(body, "Alert", new Vector2(0f, 1f), new Vector2(Padding, -168f), new Vector2(BodyWidth, 28f),
                DashboardStyle.Calm, 1.6f);
            _alertText = DashboardStyle.Line(_alert.transform, "Text", new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(BodyWidth - 16f, 28f),
                15, TextAnchor.MiddleCenter, DashboardStyle.Dim);
        }

        protected override void Render(DashboardSummaryDto summary, LiveDashboardController dashboard, bool expanded)
        {
            DashboardCityDto city = summary != null ? summary.myCity : null;
            if (city == null || !city.resident)
            {
                _alertOn = false;
                _alert.gameObject.SetActive(false);
                SetAccent(DashboardStyle.Dim, false);
                SetKey(city == null ? "…" : expanded ? "" : DashboardViews.CityKey(city, Compact), DashboardStyle.Dim);
                ShowMessage(city == null ? DashboardCardText.Waiting(dashboard)
                    : "아직 전입신고를 하지 않았어요.\n전입신고를 하면 우리 도시의\n현황이 여기에 보여요.");
                return;
            }

            ShowRows(DashboardViews.City(city));
            Color stage = CityRiskService.StageColor(city.stage);
            string alert = DashboardViews.CityAlertText(city);
            _alertOn = alert != null;
            _alert.gameObject.SetActive(true);
            _alert.color = _alertOn ? DashboardStyle.AlertBack : DashboardStyle.Calm;
            _alertText.text = _alertOn ? alert : "● 지금은 경고가 없어요";
            _alertText.color = _alertOn ? Color.white : DashboardStyle.Dim;

            // 침공과 긴급 단계는 접힌 카드에서도 붉게 깜박인다. 관리자가 정한 위험도 기준을 넘은 것은 색만 바뀐다.
            bool urgent = city.alert && city.alertReason != DashboardAlertReasons.Risk;
            SetAccent(urgent ? CityRiskService.StageColor(RiskStages.Emergency) : stage, urgent);
            SetKey(expanded ? city.name : DashboardViews.CityHeader(city, Compact), expanded ? DashboardStyle.Text : urgent ? Color.white : stage);
        }
    }
}
