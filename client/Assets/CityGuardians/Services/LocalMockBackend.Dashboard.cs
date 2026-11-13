using System;
using System.Collections.Generic;
using System.Globalization;
using CityGuardians.Domain;

namespace CityGuardians.Services
{
    // 모의 서버의 실시간 대시보드 요약 (v10.0, docs/LIVE_DASHBOARD.md).
    //
    // 실제 서버의 GET /dashboard/summary (server/src/Service/Dashboard.php) 와 같은 일을 한다:
    // 새로 계산하지 않고, 이미 있는 응답(은행, 도시 위험도, 지역 랭킹, 도시 성장, 도시 재정)을 모아 준다.
    // 모의 서버에는 은행이 있어서 예금이 실제 값으로 나간다. 가상 주식과 대출은 여기에도 없다.
    public sealed partial class LocalMockBackend
    {
        private const int DashboardTrendDays = 7;
        private const int DashboardTop = 5;
        private const int DashboardUrgent = 5;
        private const int DashboardNationalNoticeMax = 3;

        /// <summary>
        /// 관리자가 정하는 값의 모의 서버 판. 기본값은 실제 서버와 같고, 개발용이라 샘플 표시만 켜 둔다.
        /// </summary>
        public DashboardSettings DashboardConfig { get; } = MockDashboardSettings();

        /// <summary>테스트용: 요약 요청을 실패하게 한다 (연결이 끊긴 상황).</summary>
        public bool DashboardOffline { get; set; }

        private static DashboardSettings MockDashboardSettings()
        {
            DashboardSettings settings = DashboardSettings.Default();
            settings.sampleData = true;
            return settings;
        }

        public void GetDashboardSummary(Action<ApiResult<DashboardSummaryDto>> done)
        {
            if (!RequirePlayer(done)) return;
            if (DashboardOffline)
            {
                done(ApiResult<DashboardSummaryDto>.Fail(ApiErrors.StorageFailed, "서버에 연결하지 못했어요."));
                return;
            }

            DateTime now = _clock.UtcNow;
            BankDto bank = null;
            CityRisksDto risks = null;
            RankingsDto rankings = null;
            GetBank(r => bank = r.data);
            GetCityRisks(r => risks = r.data);
            GetRankings(r => rankings = r.data);
            if (bank == null || risks == null || rankings == null)
            {
                done(ApiResult<DashboardSummaryDto>.Fail(ApiErrors.StorageFailed, "요약을 만들지 못했어요."));
                return;
            }

            var summary = new DashboardSummaryDto { updatedAt = now.ToString("o"), settings = CopySettings(DashboardConfig) };
            summary.finance = DashboardFinance(bank, now);
            summary.myCity = DashboardCity(risks, out InvasionEvent homeInvasion, out SupportRequest homeSupport);
            summary.national = DashboardNational(risks, rankings);
            DashboardNotices(summary, risks, homeInvasion, homeSupport, now);
            done(ApiResult<DashboardSummaryDto>.Success(summary));
        }

        private static DashboardSettings CopySettings(DashboardSettings from)
        {
            var copy = new DashboardSettings
            {
                refreshSeconds = from.SafeRefreshSeconds, cityAlertRisk = from.cityAlertRisk, nationalNotice = from.nationalNotice,
                sampleData = from.sampleData,
            };
            for (int i = 0; i < from.cards.Count; i++)
                copy.cards.Add(new DashboardCardDefault { id = from.cards[i].id, visible = from.cards[i].visible, expanded = from.cards[i].expanded });
            return copy;
        }

        private DashboardFinanceDto DashboardFinance(BankDto bank, DateTime now)
        {
            var finance = new DashboardFinanceDto
            {
                gc = bank.gc,
                bankAvailable = true, bankGc = bank.bankGc, termGc = bank.termGc,
                // 가상 주식과 대출은 아직 없다. 0 이 아니라 "없다"고 답한다.
                stocksAvailable = false, loansAvailable = false,
                netWorthGc = bank.gc + bank.bankGc + bank.termGc, netWorthPartial = true,
            };

            // 자산 변동: 원장에 적힌 지갑 잔액의 하루 끝 값 (한국 날짜).
            var dates = new List<string>();
            for (int i = DashboardTrendDays - 1; i >= 0; i--) dates.Add(KoreaDate(now.AddDays(-i)));
            var closing = new Dictionary<string, long>();
            long carry = _player.ledgerOpeningBalance;
            for (int i = 0; i < _player.ledger.Count; i++)
            {
                LedgerEntry entry = _player.ledger[i];
                if (!DateTime.TryParse(entry.createdAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime at)) continue;
                string day = KoreaDate(at.ToUniversalTime());
                if (string.CompareOrdinal(day, dates[0]) < 0) carry = entry.balanceAfter;
                else closing[day] = entry.balanceAfter;
            }
            for (int i = 0; i < dates.Count; i++)
            {
                if (closing.TryGetValue(dates[i], out long end)) carry = end;
                finance.trend.Add(new DashboardTrendPointDto { date = dates[i], gc = carry });
            }
            finance.trend[finance.trend.Count - 1].gc = bank.gc;
            return finance;
        }

        private DashboardCityDto DashboardCity(CityRisksDto risks, out InvasionEvent homeInvasion, out SupportRequest homeSupport)
        {
            homeInvasion = null;
            homeSupport = null;
            var city = new DashboardCityDto { stage = RiskStages.Safe, modelingStatus = ModelingStatuses.NotStarted };
            if (!IsResident(_player)) return city;

            string code = _player.regionCode;
            CityStatusDto status = risks.Find(code);
            CityDetailDto detail = null;
            CityGrowthDto growth = null;
            TreasuryDto treasury = null;
            GetCityDetail(code, r => detail = r.data);
            GetCityGrowthDetail(code, r => growth = r.data);
            GetTreasury(code, r => treasury = r.data);
            if (status == null || detail == null || growth == null || treasury == null) return city;

            for (int i = 0; i < detail.invasions.Count; i++)
                if (detail.invasions[i].IsActive) homeInvasion = detail.invasions[i];
            if (detail.support != null && detail.support.IsOpen) homeSupport = detail.support;

            city.resident = true;
            city.regionCode = code;
            city.name = growth.name;
            city.risk = status.risk;
            city.stage = status.stage;
            city.defensePower = status.defensePower;
            city.validResidents = growth.validResidents;
            city.treasuryGc = treasury.generalGc + treasury.reserveGc;
            city.invasionKind = status.invasionKind;
            city.invasionSeverity = status.invasionSeverity;
            city.invasionProgress = status.invasionProgress;
            city.invasionRequired = status.invasionRequired;
            city.invasionEndsAt = status.invasionEndsAt;
            city.myContribution = detail.myContribution;
            city.rank = status.rank;
            city.growthStage = growth.stage;
            city.growthStageName = growth.stageName;
            city.modelingStatus = growth.modelingStatus;
            city.supportOpen = status.SupportOpen;

            // 경고의 까닭. 침공이 가장 급하고, 그다음이 긴급 단계, 그다음이 관리자가 정한 위험도 기준이다.
            if (status.HasInvasion) city.alertReason = DashboardAlertReasons.Invasion;
            else if (status.stage == RiskStages.Emergency) city.alertReason = DashboardAlertReasons.Emergency;
            else if (status.risk >= DashboardConfig.cityAlertRisk) city.alertReason = DashboardAlertReasons.Risk;
            city.alert = city.alertReason != null;
            return city;
        }

        private string DashboardName(string regionCode) => Regions.ShortName(regionCode) ?? regionCode;

        private static bool DashboardIsUrgent(CityStatusDto city) => city.stage == RiskStages.Emergency || city.HasInvasion;

        private DashboardNationalDto DashboardNational(CityRisksDto risks, RankingsDto rankings)
        {
            var national = new DashboardNationalDto
            {
                totalCities = risks.cities.Count, averageSafety = 100, homeRank = rankings.home != null ? rankings.home.rank : 0,
            };
            long riskTotal = 0;
            var urgent = new List<CityStatusDto>();
            for (int i = 0; i < risks.cities.Count; i++)
            {
                CityStatusDto city = risks.cities[i];
                riskTotal += city.risk;
                if (city.stage == RiskStages.Emergency) national.emergencyCities++;
                else if (city.stage == RiskStages.Danger) national.dangerCities++;
                if (city.HasInvasion) national.invasionCities++;
                if (city.SupportOpen) national.supportRequests++;
                if (DashboardIsUrgent(city)) urgent.Add(city);
            }
            if (risks.cities.Count > 0)
                national.averageSafety = (int)Math.Round(100.0 - (double)riskTotal / risks.cities.Count, MidpointRounding.AwayFromZero);

            // 긴급 단계가 먼저, 그 안에서는 위험도가 높은 도시가 먼저.
            urgent.Sort((a, b) =>
            {
                int ea = a.stage == RiskStages.Emergency ? 1 : 0;
                int eb = b.stage == RiskStages.Emergency ? 1 : 0;
                if (ea != eb) return eb - ea;
                return a.risk != b.risk ? b.risk - a.risk : string.CompareOrdinal(a.regionCode, b.regionCode);
            });
            for (int i = 0; i < urgent.Count && i < DashboardUrgent; i++)
            {
                national.urgent.Add(new DashboardUrgentDto
                {
                    regionCode = urgent[i].regionCode, name = DashboardName(urgent[i].regionCode), risk = urgent[i].risk,
                    stage = urgent[i].stage, invasionKind = urgent[i].invasionKind,
                });
            }
            for (int i = 0; i < rankings.rows.Count && i < DashboardTop; i++)
            {
                RankingRowDto row = rankings.rows[i];
                national.top.Add(new DashboardRankDto
                {
                    regionCode = row.regionCode, name = DashboardName(row.regionCode), rank = row.rank, score = row.score,
                    risk = row.risk, stage = row.stage,
                });
            }
            return national;
        }

        /// <summary>같은 일에는 같은 id 를 붙인다. 침공은 침공마다, 단계와 위험도 경고는 하루에 한 번.</summary>
        private void DashboardNotices(DashboardSummaryDto summary, CityRisksDto risks, InvasionEvent homeInvasion, SupportRequest homeSupport,
            DateTime now)
        {
            string today = KoreaDate(now);
            DashboardCityDto home = summary.myCity;
            if (home.resident)
            {
                string code = home.regionCode;
                if (homeInvasion != null)
                {
                    summary.notifications.Add(new DashboardNoticeDto
                    {
                        id = "city_invasion:" + homeInvasion.id, kind = "city_invasion", level = DashboardNoticeLevels.Urgent, regionCode = code,
                        title = "우리 도시에 " + InvasionKinds.DisplayName(homeInvasion.kind) + "!",
                        body = home.name + "을(를) 지켜 주세요. 막은 횟수 " + homeInvasion.progress + " / " + homeInvasion.required,
                    });
                }
                if (home.stage == RiskStages.Emergency)
                {
                    summary.notifications.Add(new DashboardNoticeDto
                    {
                        id = "city_emergency:" + code + ":" + today, kind = "city_emergency", level = DashboardNoticeLevels.Urgent, regionCode = code,
                        title = "우리 도시가 긴급 단계예요", body = home.name + "의 위험도가 " + home.risk + "까지 올랐어요.",
                    });
                }
                else if (home.alertReason == DashboardAlertReasons.Risk)
                {
                    summary.notifications.Add(new DashboardNoticeDto
                    {
                        id = "city_risk:" + code + ":" + today, kind = "city_risk", level = DashboardNoticeLevels.Warn, regionCode = code,
                        title = "우리 도시의 위험도가 높아요", body = home.name + "의 위험도가 " + home.risk + "이에요.",
                    });
                }
                if (homeSupport != null)
                {
                    summary.notifications.Add(new DashboardNoticeDto
                    {
                        id = "city_support:" + homeSupport.id, kind = "city_support", level = DashboardNoticeLevels.Info, regionCode = code,
                        title = "우리 도시가 지원을 요청했어요", body = "이웃 도시의 가디언이 도우러 올 수 있어요.",
                    });
                }
            }

            int policy = DashboardConfig.nationalNotice;
            if (policy <= 0) return;
            var picked = new List<CityStatusDto>();
            for (int i = 0; i < risks.cities.Count; i++)
            {
                CityStatusDto city = risks.cities[i];
                if (home.resident && city.regionCode == home.regionCode) continue;
                if (city.stage == RiskStages.Emergency || (policy >= 2 && city.HasInvasion)) picked.Add(city);
            }
            picked.Sort((a, b) => a.risk != b.risk ? b.risk - a.risk : string.CompareOrdinal(a.regionCode, b.regionCode));
            for (int i = 0; i < picked.Count && i < DashboardNationalNoticeMax; i++)
            {
                CityStatusDto city = picked[i];
                string name = DashboardName(city.regionCode);
                bool emergency = city.stage == RiskStages.Emergency;
                summary.notifications.Add(new DashboardNoticeDto
                {
                    id = (emergency ? "national_emergency:" : "national_invasion:") + city.regionCode + ":" + today,
                    kind = emergency ? "national_emergency" : "national_invasion",
                    level = emergency ? DashboardNoticeLevels.Warn : DashboardNoticeLevels.Info, regionCode = city.regionCode,
                    title = emergency ? name + "이(가) 긴급 단계예요" : name + "에 " + InvasionKinds.DisplayName(city.invasionKind),
                    body = "위험도 " + city.risk + ". 전국 지도에서 볼 수 있어요.",
                });
            }
        }
    }
}
