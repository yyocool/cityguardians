using System;
using System.Collections.Generic;
using CityGuardians.Domain;

namespace CityGuardians.Services
{
    // GET /dashboard/summary 의 응답 (docs/API_SPEC.md 23장).
    // 값은 모두 서버가 계산한 것이다. 클라이언트는 받아서 보여 주기만 한다.
    // 서버의 응답과 맞는지는 tools/check/rules/WireContractTests.cs 가 대조한다.

    [Serializable]
    public sealed class DashboardTrendPointDto
    {
        /// <summary>한국 날짜 (yyyy-MM-dd).</summary>
        public string date;
        /// <summary>그날 끝의 지갑 잔액. 마지막 값은 지금의 잔액이다.</summary>
        public long gc;
    }

    /// <summary>
    /// 내 계좌. 요청한 사람 자신의 값이다.
    /// 서버에 아직 없는 기능은 ...Available 이 false 이고 금액은 0 이다 (0 GC 가 있다는 뜻이 아니다).
    /// </summary>
    [Serializable]
    public sealed class DashboardFinanceDto
    {
        /// <summary>보유 GC (지갑).</summary>
        public long gc;
        public bool bankAvailable;
        /// <summary>통장의 GC.</summary>
        public long bankGc;
        /// <summary>정기예금에 넣어 둔 원금.</summary>
        public long termGc;
        public bool stocksAvailable;
        public long stockValueGc;
        public long stockProfitGc;
        public bool loansAvailable;
        public long loanBalanceGc;
        /// <summary>순자산: 서버에 있는 자산에서 부채를 뺀 값.</summary>
        public long netWorthGc;
        /// <summary>아직 없는 기능이 있어서 순자산에 빠진 항목이 있다.</summary>
        public bool netWorthPartial;
        /// <summary>자산 변동: 최근 며칠의 하루 끝 지갑 잔액. 오래된 날이 앞이다.</summary>
        public List<DashboardTrendPointDto> trend = new List<DashboardTrendPointDto>();
    }

    /// <summary>우리 도시. 전입신고를 하지 않았으면 resident 가 false 이고 나머지는 비어 있다.</summary>
    [Serializable]
    public sealed class DashboardCityDto
    {
        public bool resident;
        public string regionCode;
        public string name;
        /// <summary>위험도 0~100. 서버가 계산한다.</summary>
        public int risk;
        /// <summary>RiskStages.</summary>
        public string stage;
        public int defensePower;
        public int validResidents;
        /// <summary>게임 안의 도시 재정 (일반 + 예비). 실제 돈이 아니고 CITY PASS·장학 예산과 따로다.</summary>
        public long treasuryGc;
        /// <summary>진행 중인 침공 (InvasionKinds). 없으면 null.</summary>
        public string invasionKind;
        public int invasionSeverity;
        public int invasionProgress;
        public int invasionRequired;
        public string invasionEndsAt;
        public long myContribution;
        public int rank;
        public int growthStage;
        public string growthStageName;
        /// <summary>상징거리 모델링 상태 (ModelingStatuses).</summary>
        public string modelingStatus;
        /// <summary>우리 도시가 지원을 요청해 둔 상태인가.</summary>
        public bool supportOpen;
        /// <summary>카드에 경고를 띄울 것인가. 서버가 관리자의 기준으로 판정한다.</summary>
        public bool alert;
        /// <summary>경고의 까닭 (DashboardAlertReasons). 없으면 null.</summary>
        public string alertReason;

        public bool HasInvasion => !string.IsNullOrEmpty(invasionKind);
    }

    public static class DashboardAlertReasons
    {
        public const string Invasion = "invasion";
        public const string Emergency = "emergency";
        public const string Risk = "risk";
    }

    [Serializable]
    public sealed class DashboardRankDto
    {
        public string regionCode;
        public string name;
        public int rank;
        public int score;
        public int risk;
        public string stage;
    }

    [Serializable]
    public sealed class DashboardUrgentDto
    {
        public string regionCode;
        public string name;
        public int risk;
        public string stage;
        public string invasionKind;
    }

    [Serializable]
    public sealed class DashboardNationalDto
    {
        public int totalCities;
        /// <summary>전국 평균 안전도 (100 − 평균 위험도).</summary>
        public int averageSafety;
        public int emergencyCities;
        public int dangerCities;
        public int invasionCities;
        /// <summary>우리 도시의 순위. 주민이 아니면 0.</summary>
        public int homeRank;
        /// <summary>열려 있는 지원 요청의 수.</summary>
        public int supportRequests;
        public List<DashboardRankDto> top = new List<DashboardRankDto>();
        /// <summary>긴급 단계이거나 침공 중인 도시. 급한 도시가 앞이다.</summary>
        public List<DashboardUrgentDto> urgent = new List<DashboardUrgentDto>();
    }

    public static class DashboardNoticeLevels
    {
        public const string Info = "info";
        public const string Warn = "warn";
        public const string Urgent = "urgent";

        public static int Order(string level)
        {
            switch (level)
            {
                case Urgent: return 2;
                case Warn: return 1;
                default: return 0;
            }
        }
    }

    [Serializable]
    public sealed class DashboardNoticeDto
    {
        /// <summary>같은 일에는 같은 id 가 온다. 한 번 알린 것은 다시 알리지 않는다.</summary>
        public string id;
        /// <summary>city_invasion, city_emergency, city_risk, city_support, national_emergency, national_invasion.</summary>
        public string kind;
        /// <summary>DashboardNoticeLevels.</summary>
        public string level;
        public string regionCode;
        public string title;
        public string body;

        /// <summary>우리 도시의 알림인가 (전투 중에도 알린다).</summary>
        public bool IsCity => kind != null && kind.StartsWith("city_", StringComparison.Ordinal);
    }

    [Serializable]
    public sealed class DashboardSummaryDto
    {
        public DashboardFinanceDto finance = new DashboardFinanceDto();
        public DashboardCityDto myCity = new DashboardCityDto();
        public DashboardNationalDto national = new DashboardNationalDto();
        public List<DashboardNoticeDto> notifications = new List<DashboardNoticeDto>();
        /// <summary>관리자가 정한 값: 카드별 기본 표시·펼침, 갱신 간격, 경고 기준, 전국 알림 정책.</summary>
        public DashboardSettings settings = DashboardSettings.Default();
        /// <summary>서버가 이 요약을 만든 때 (ISO 8601, UTC).</summary>
        public string updatedAt;
    }
}
