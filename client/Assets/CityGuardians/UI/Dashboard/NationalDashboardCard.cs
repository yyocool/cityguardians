using System.Collections.Generic;
using CityGuardians.Domain;
using CityGuardians.Gameplay;
using CityGuardians.Services;
using UnityEngine;
using UnityEngine.UI;

namespace CityGuardians.UI
{
    /// <summary>
    /// 카드 3: 전국 지도 기반 현황 (docs/NATIONAL_MAP_DASHBOARD.md).
    /// 시·군·구를 위험도 단계의 색으로 칠한 축약 지도, 전국 평균 안전도, 긴급 도시 수, 우리 도시 순위, 1위 도시, 지원 요청.
    /// 확대·축소할 수 있는 전체 지도는 상세 보기(기존 지도 화면)에서 본다.
    /// </summary>
    public sealed class NationalDashboardCard : DashboardCardBase
    {
        private const int UrgentLines = 4;
        private const int NameLimit = 9;

        public override string CardId => DashboardCards.National;
        public override float ExpandedHeight => 330f;

        public NationalMapPreview Preview { get; private set; }
        public string UrgentText(int index) => _urgent[index].gameObject.activeSelf ? _urgent[index].text : null;

        private Text _urgentTitle;
        private readonly Text[] _urgent = new Text[UrgentLines];
        private readonly List<string> _marked = new List<string>();

        // 지도의 색은 기존 도시 위험도 서비스의 값이다. 그 값이 바뀌면 다시 칠한다.
        protected override int ExtraVersion => CityRiskService.Version;

        protected override void BuildBody(RectTransform body)
        {
            Preview = NationalMapPreview.Create(body, new Vector2(Padding, -6f), new Vector2(112f, 104f));
            DashboardStyle.Line(body, "Credit", new Vector2(0f, 1f), new Vector2(Padding, -110f), new Vector2(BodyWidth, 14f),
                10, TextAnchor.MiddleLeft, DashboardStyle.Dim).text = NationalMapPreview.Credit;

            float x = Padding + 112f + 12f;
            _urgentTitle = DashboardStyle.Line(body, "UrgentTitle", new Vector2(0f, 1f), new Vector2(x, -6f), new Vector2(180f, 20f),
                13, TextAnchor.MiddleLeft, DashboardStyle.Dim);
            for (int i = 0; i < UrgentLines; i++)
            {
                _urgent[i] = DashboardStyle.Line(body, "Urgent" + (i + 1), new Vector2(0f, 1f), new Vector2(x, -(27f + i * 20.5f)),
                    new Vector2(180f, 20f), 15, TextAnchor.MiddleLeft, DashboardStyle.Text);
            }
            AddRows(body, 5, 128f);
        }

        protected override void Render(DashboardSummaryDto summary, LiveDashboardController dashboard, bool expanded)
        {
            DashboardNationalDto national = summary != null ? summary.national : null;
            if (national == null)
            {
                SetAccent(DashboardStyle.Dim, false);
                SetKey("…", DashboardStyle.Dim);
                ShowMessage(DashboardCardText.Waiting(dashboard));
                Preview.gameObject.SetActive(false);
                _urgentTitle.text = "";
                for (int i = 0; i < UrgentLines; i++) _urgent[i].gameObject.SetActive(false);
                return;
            }

            if (!Preview.gameObject.activeSelf) Preview.gameObject.SetActive(true);
            ShowRows(DashboardViews.National(national));
            string worst = national.emergencyCities > 0 ? RiskStages.Emergency : national.dangerCities > 0 ? RiskStages.Danger : RiskStages.Safe;
            Color color = CityRiskService.StageColor(worst);
            SetAccent(color, false);
            SetKey(expanded ? "" : DashboardViews.NationalKey(national, Compact), color);
            if (!expanded) return;

            // 지도 옆: 지금 가장 급한 도시들
            _marked.Clear();
            int count = Mathf.Min(UrgentLines, national.urgent.Count);
            _urgentTitle.text = count > 0 ? "급한 도시" : "● 급한 도시가 없어요";
            for (int i = 0; i < UrgentLines; i++)
            {
                bool used = i < count;
                _urgent[i].gameObject.SetActive(used);
                if (!used) continue;
                DashboardUrgentDto row = national.urgent[i];
                string name = row.name ?? "";
                if (name.Length > NameLimit) name = name.Substring(0, NameLimit - 1) + "…";
                _urgent[i].text = RiskStages.Icon(row.stage) + " " + name + "  " + row.risk;
                _urgent[i].color = CityRiskService.StageColor(row.stage);
                _marked.Add(row.regionCode);
            }

            DashboardCityDto home = summary.myCity;
            Preview.Show(home != null && home.resident ? home.regionCode : null, _marked);
        }
    }
}
