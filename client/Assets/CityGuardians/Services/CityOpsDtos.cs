using System;
using System.Collections.Generic;
using CityGuardians.Domain;

namespace CityGuardians.Services
{
    // v9.0: 도시 성장, 이사, 지역 콘텐츠, 가상 지방세, 도시 재정, 참여예산, 협동 방어전의 응답 형식.
    // docs/API_SPEC.md 19~22장에 대응한다.
    //
    // 전입자 수, 세율, 세액, 재정 잔액, 표의 수를 서버로 보내는 요청 형식은 없다. 여기 있는 숫자는 모두 서버가 내려 주는 값이다.
    // 여기의 GC 는 게임 안에서만 쓰는 가상의 돈이다. 실제 돈, 현실의 세금, CITY PASS, 장학 사업과는 관계가 없다.

    public static class ModelingStatuses
    {
        public const string NotStarted = "NOT_STARTED";
        public const string Planning = "PLANNING";
        public const string Modeling = "MODELING";
        public const string Review = "REVIEW";
        public const string Ready = "READY";
        public const string Deployed = "DEPLOYED";

        public static string DisplayName(string status)
        {
            switch (status)
            {
                case Planning: return "기획 중";
                case Modeling: return "제작 중";
                case Review: return "검수 중";
                case Ready: return "개방 준비 완료";
                case Deployed: return "개방됨";
                default: return "제작 전";
            }
        }
    }

    [Serializable]
    public sealed class GrowthMilestoneDto
    {
        public string code;
        public string name;
        public int threshold;
        public string description;
        /// <summary>이 단계에 닿으면 상징거리를 여는 것이 목표가 되는가.</summary>
        public bool opensStreet;
        /// <summary>도시 상세에서: 이 도시가 이미 닿았는가.</summary>
        public bool reached;
    }

    [Serializable]
    public sealed class GrowthDayDto
    {
        public string date;
        public int validResidents;
    }

    /// <summary>한 도시의 전입자 현황과 성장 단계.</summary>
    [Serializable]
    public sealed class CityGrowthDto
    {
        public string regionCode;
        public string name;
        public string sidoCode;
        /// <summary>유효 전입자 수. 서버가 정한 기준으로 센다 (기본: 계정을 연결한 주민만. 게스트 계정을 여러 개 만들어도 늘지 않는다).</summary>
        public int validResidents;
        /// <summary>닿은 성장 단계의 수.</summary>
        public int stage;
        public string stageName;
        /// <summary>다음 단계의 기준 인원. 더 없으면 0.</summary>
        public int nextThreshold;
        /// <summary>상징거리 모델링 기준 인원.</summary>
        public int streetThreshold;
        public bool thresholdReached;
        /// <summary>ModelingStatuses.</summary>
        public string modelingStatus = ModelingStatuses.NotStarted;
        public string contentVersion;
        /// <summary>상징거리가 정식으로 열렸는가.</summary>
        public bool streetOpen;
        public string openedAt;

        // 상세에서만 온다.
        public int cumulativeResidents;
        public int growth7d;
        public int growth30d;
        public List<GrowthMilestoneDto> milestones = new List<GrowthMilestoneDto>();
        public List<GrowthDayDto> trend = new List<GrowthDayDto>();
    }

    /// <summary>GET /cities/growth.</summary>
    [Serializable]
    public sealed class CityGrowthListDto
    {
        public List<CityGrowthDto> cities = new List<CityGrowthDto>();
        public List<GrowthMilestoneDto> milestones = new List<GrowthMilestoneDto>();
        /// <summary>"linked": 연결된 계정만 센다. "linked_or_proven": 활동이 확인된 게스트도 센다.</summary>
        public string countRule;
    }

    /// <summary>GET /residency: 내 전입 상태와 이사 조건.</summary>
    [Serializable]
    public sealed class ResidencyInfoDto
    {
        /// <summary>"resident" 또는 "stranger".</summary>
        public string residency;
        public string regionCode;
        public string regionName;
        public string since;
        /// <summary>내가 유효 전입자로 세어지고 있는가.</summary>
        public bool counted;
        public string countRule;
        /// <summary>세어지지 않는 이유: "guest_account", "not_proven".</summary>
        public string notCountedReason;
        public bool moveEnabled;
        public int moveFeeGc;
        public int moveCooldownDays;
        /// <summary>다시 이사할 수 있는 때. 지금 할 수 있으면 비어 있다.</summary>
        public string canMoveAt;
        public long gc;

        public bool IsResident => residency == "resident";
    }

    /// <summary>POST /residency/move.</summary>
    [Serializable]
    public sealed class MoveResultDto
    {
        public string regionCode;
        public string regionName;
        public string previousRegionCode;
        public int feeGc;
        public long gc;
        public string canMoveAt;
    }

    /// <summary>GET /cities/content: 이 클라이언트가 받을 수 있는 지역 콘텐츠.</summary>
    [Serializable]
    public sealed class CityContentListDto
    {
        /// <summary>서버에 콘텐츠 저장소가 준비되어 있는가. 아니면 아무것도 내려오지 않는다.</summary>
        public bool available;
        /// <summary>이 계정이 받아도 되는 묶음. 클라이언트는 CityContent.IsUsable 로 다시 확인한다 (저장소 주소는 클라이언트에 들어 있다).</summary>
        public List<CityContentEntry> items = new List<CityContentEntry>();
    }

    public static class TaxTypes
    {
        public const string Trade = "trade";
        public const string Business = "business";
        public const string Property = "property";
    }

    [Serializable]
    public sealed class TaxAppliesDto
    {
        public string reason;
        public string name;
    }

    [Serializable]
    public sealed class TaxPolicyDto
    {
        /// <summary>TaxTypes.</summary>
        public string taxType;
        public string name;
        public string description;
        /// <summary>관리자가 켰는가. 꺼져 있으면 걷지 않는다.</summary>
        public bool active;
        /// <summary>세율 (1/100 %). 300 이면 3%.</summary>
        public int rateBp;
        public List<TaxAppliesDto> appliesTo = new List<TaxAppliesDto>();
        public int exemptAmount;
        /// <summary>이 레벨부터 낸다 (초보자 보호).</summary>
        public int minLevel;
        /// <summary>처음 전입한 뒤 이만큼은 내지 않는다 (초보자 보호, 일).</summary>
        public int graceDays;
        public int capGc;
        public string period;
        /// <summary>값에 들어 있는 세금인가 (거래세). 그러면 값이 더 비싸지지 않는다.</summary>
        public bool included;
        /// <summary>내가 지금 이 세금을 내지 않는가.</summary>
        public bool exempt;
        public string exemptReason;
        public string exemptMessage;

        public string RateText => (rateBp / 100f).ToString("0.##") + "%";
    }

    /// <summary>GET /tax/policies.</summary>
    [Serializable]
    public sealed class TaxPoliciesDto
    {
        public List<TaxPolicyDto> policies = new List<TaxPolicyDto>();
        public string notice;
        public int businessIncomeToday;
        public int businessTaxToday;
    }

    [Serializable]
    public sealed class TaxTransactionDto
    {
        public string id;
        public string taxType;
        public string taxName;
        public int baseGc;
        public int rateBp;
        public int taxGc;
        public bool included;
        public string sourceReason;
        public string regionCode;
        public string regionName;
        public string at;
    }

    [Serializable]
    public sealed class TaxTotalDto
    {
        public string taxType;
        public string taxName;
        public long totalGc;
        public int count;
    }

    /// <summary>GET /tax/transactions: 내가 낸 세금의 명세.</summary>
    [Serializable]
    public sealed class TaxTransactionsDto
    {
        public List<TaxTransactionDto> transactions = new List<TaxTransactionDto>();
        public List<TaxTotalDto> totals = new List<TaxTotalDto>();
        public long gc;
    }

    [Serializable]
    public sealed class TreasuryIncomeDto
    {
        public string taxType;
        public string taxName;
        public long totalGc;
        public long last30dGc;
    }

    [Serializable]
    public sealed class TreasurySpentDto
    {
        public string category;
        public string categoryName;
        public long totalGc;
        public int count;
    }

    [Serializable]
    public sealed class TreasuryLineDto
    {
        /// <summary>"general" 또는 "reserve".</summary>
        public string account;
        public long deltaGc;
        public long balanceAfter;
        public string reason;
        public string reasonName;
        public string at;
    }

    /// <summary>GET /cities/{code}/treasury: 도시 재정 현황. 누구나 볼 수 있다.</summary>
    [Serializable]
    public sealed class TreasuryDto
    {
        public string regionCode;
        public string regionName;
        public long generalGc;
        /// <summary>예비비.</summary>
        public long reserveGc;
        public List<TreasuryIncomeDto> income = new List<TreasuryIncomeDto>();
        public List<TreasurySpentDto> spent = new List<TreasurySpentDto>();
        public List<TreasuryLineDto> recent = new List<TreasuryLineDto>();
        public int reservePercent;
        public string notice;
    }

    [Serializable]
    public sealed class ExpenditureDto
    {
        public string id;
        public string category;
        public string categoryName;
        public string title;
        public string description;
        public long amountGc;
        public string account;
        public string effectDetail;
        /// <summary>주민 투표로 정한 집행인가.</summary>
        public bool byVote;
        public string proposalId;
        public string at;
    }

    /// <summary>GET /cities/{code}/treasury/expenditures: 예산 집행 내역. 누구나 볼 수 있다.</summary>
    [Serializable]
    public sealed class ExpendituresDto
    {
        public string regionCode;
        public List<ExpenditureDto> expenditures = new List<ExpenditureDto>();
    }

    public static class BudgetStatuses
    {
        public const string Submitted = "submitted";
        public const string Voting = "voting";
        public const string Passed = "passed";
        public const string Failed = "failed";
        public const string Executed = "executed";
        public const string Rejected = "rejected";
        public const string Withdrawn = "withdrawn";
        public const string Cancelled = "cancelled";

        public static string DisplayName(string status)
        {
            switch (status)
            {
                case Submitted: return "검토 대기";
                case Voting: return "주민 투표 중";
                case Passed: return "가결 (집행 대기)";
                case Failed: return "부결";
                case Executed: return "집행 완료";
                case Rejected: return "반려";
                case Withdrawn: return "철회";
                case Cancelled: return "취소";
                default: return status;
            }
        }
    }

    [Serializable]
    public sealed class BudgetCategoryDto
    {
        public string category;
        public string name;
        /// <summary>집행되면 게임에 생기는 효과: BudgetEffects.</summary>
        public string effectType;
    }

    public static class BudgetEffects
    {
        /// <summary>도시 공용 방어시설의 건설 기금에 보탠다. 대상 시설을 골라야 한다.</summary>
        public const string StructureFund = "structure_fund";
        /// <summary>부서진 도시 시설을 고친다. 대상 시설을 골라야 한다.</summary>
        public const string FacilityRepair = "facility_repair";
        /// <summary>집행 내역만 남는다.</summary>
        public const string Record = "record";
    }

    /// <summary>제안의 대상으로 고를 수 있는 시설.</summary>
    [Serializable]
    public sealed class BudgetTargetDto
    {
        public string code;
        public string name;
    }

    /// <summary>GET /cities/budget/options: 제안할 때 고를 수 있는 것들과 자격.</summary>
    [Serializable]
    public sealed class BudgetOptionsDto
    {
        public bool enabled;
        public List<BudgetCategoryDto> categories = new List<BudgetCategoryDto>();
        /// <summary>"공동 방어시설" 제안의 대상.</summary>
        public List<BudgetTargetDto> structures = new List<BudgetTargetDto>();
        /// <summary>"방어시설 유지보수" 제안의 대상.</summary>
        public List<BudgetTargetDto> facilities = new List<BudgetTargetDto>();
        public int maxRequestGc;
        public int proposerMinLevel;
        public int voterMinLevel;
        public int voterMinResidentDays;
        public int voteDays;
        public int quorumVotes;
        public string notice;
    }

    [Serializable]
    public sealed class BudgetProposalDto
    {
        public string id;
        public string regionCode;
        public string regionName;
        public string category;
        public string categoryName;
        public string title;
        public string description;
        public int requestedGc;
        public string targetCode;
        public string targetName;
        /// <summary>BudgetStatuses.</summary>
        public string status;
        public string statusName;
        /// <summary>제안한 주민의 게임 이름.</summary>
        public string proposer;
        public bool mine;
        /// <summary>반려 사유. 제안한 사람에게만 온다.</summary>
        public string reviewNote;
        public int yesCount;
        public int noCount;
        /// <summary>가결에 필요한 최소 투표 수.</summary>
        public int quorum;
        public string voteStartsAt;
        public string voteEndsAt;
        /// <summary>내가 던진 표: "yes", "no". 아직이면 비어 있다.</summary>
        public string myVote;
        public bool canVote;
        /// <summary>투표할 수 없는 이유: "already_voted", "not_resident_here", "level_too_low", "resident_too_new", ...</summary>
        public string voteBlockReason;
        public string voteBlockMessage;
        public string createdAt;
    }

    /// <summary>GET /cities/{code}/budget/proposals.</summary>
    [Serializable]
    public sealed class BudgetListDto
    {
        public string regionCode;
        public string regionName;
        public List<BudgetProposalDto> proposals = new List<BudgetProposalDto>();
        public BudgetOptionsDto options = new BudgetOptionsDto();
    }

    /// <summary>POST /cities/budget/proposals (제안), POST .../vote (투표). 한 계정이 한 표다. 가진 GC 나 낸 세금과 관계가 없다.</summary>
    [Serializable]
    public sealed class BudgetProposalResultDto
    {
        public BudgetProposalDto proposal;
    }

    [Serializable]
    public sealed class CoopMemberDto
    {
        public string playerId;
        public string nickname;
        public int level;
        public string roleCode;
        public string status;
        public bool isHost;
        public bool isMe;
    }

    [Serializable]
    public sealed class CoopSessionDto
    {
        public string id;
        /// <summary>"lobby", "running", "finished", ...</summary>
        public string status;
        public string statusName;
        public string regionCode;
        public string regionName;
        public string zoneCode;
        public string zoneName;
        public int maxPlayers;
        public int players;
        public List<CoopMemberDto> members = new List<CoopMemberDto>();
        public bool mine;
        public bool isHost;
        public string expiresAt;
    }

    [Serializable]
    public sealed class CoopPingKindDto
    {
        public string kind;
        public string name;
    }

    /// <summary>전술 핑의 운영 설정. 빈도 제한은 실시간 서버가 지킨다. 여기 값은 안내용이다.</summary>
    [Serializable]
    public sealed class CoopPingConfigDto
    {
        public bool enabled = true;
        /// <summary>운영팀이 이 계정의 핑을 막았는가.</summary>
        public bool blocked;
        public int burst;
        public int refillSeconds;
        public int lifetimeSeconds;
        public bool routeOverlay;
        public bool rangeOverlay;
        public List<CoopPingKindDto> kinds = new List<CoopPingKindDto>();
    }

    /// <summary>GET /coop/config.</summary>
    [Serializable]
    public sealed class CoopConfigDto
    {
        /// <summary>온라인 협동 방어전을 할 수 있는가. 운영팀이 켜고 실시간 서버가 준비되어야 한다.</summary>
        public bool available;
        public string unavailableReason;
        public int defaultPlayers = 4;
        public int maxPlayers = 8;
        public int minPlayersToStart = 2;
        public bool allowLateJoin = true;
        public int dailyRewardLimit;
        public int rewardsLeftToday;
        public List<CoopRoleDef> roles = new List<CoopRoleDef>();
        /// <summary>역할은 고르지 않아도 된다.</summary>
        public bool roleRequired;
        public CoopPingConfigDto ping = new CoopPingConfigDto();
        public CoopSessionDto activeSession;
        public string regionCode;

        public string UnavailableMessage
        {
            get
            {
                switch (unavailableReason)
                {
                    case "coop_disabled": return "협동 방어전은 아직 열리지 않았어요.";
                    case "realtime_not_configured": return "실시간 서버가 준비되지 않았어요.";
                    case "mode_closed": return "지금은 방어전이 잠시 닫혀 있어요.";
                    default: return "지금은 협동 방어전을 할 수 없어요.";
                }
            }
        }
    }

    /// <summary>GET /coop/sessions.</summary>
    [Serializable]
    public sealed class CoopSessionsDto
    {
        public string regionCode;
        public List<CoopSessionDto> sessions = new List<CoopSessionDto>();
    }

    /// <summary>POST /coop/sessions, .../join, .../leave.</summary>
    [Serializable]
    public sealed class CoopSessionResultDto
    {
        public CoopSessionDto session;
    }

    /// <summary>POST /coop/sessions/{id}/ticket: 실시간 서버에 들어갈 입장권 (짧게만 쓸 수 있고, 한 번만 쓸 수 있다).</summary>
    [Serializable]
    public sealed class CoopTicketDto
    {
        public string ticket;
        /// <summary>실시간 서버의 주소 (wss://).</summary>
        public string url;
        public string expiresAt;
    }

    /// <summary>방어전에서의 대원 한 사람의 활약 (서버가 센 값).</summary>
    [Serializable]
    public sealed class CoopTeamMemberDto
    {
        public string playerId;
        public string nickname;
        public string roleCode;
        public int kills;
        public int towersBuilt;
        public int towersUpgraded;
        public int repairs;
        public int damageDealt;
        public bool isMe;
    }

    /// <summary>방어전의 결과와 내 보상. 보상은 서버가 정한다.</summary>
    [Serializable]
    public sealed class CoopMyResultDto
    {
        public string sessionId;
        public string status;
        /// <summary>"win", "lose". 아직 끝나지 않았으면 비어 있다.</summary>
        public string result;
        public string zoneCode;
        public int wavesCleared;
        public int gateHpLeft;
        public int durationSeconds;
        public int playersAtStart;
        public int rewardGc;
        public int rewardXp;
        /// <summary>보상을 받지 못한 이유: "low_participation", "daily_limit", "rejected". 받았으면 비어 있다.</summary>
        public string rewardNote;
        public string rewardMessage;
        public int kills;
        public int towersBuilt;
        public int towersUpgraded;
        public int repairs;
        public int damageDealt;
        public int pingsSent;
        public int energyGranted;
        public int energySpent;
        public long gc;
        public int level;
        public int xp;
        public int xpNext;
        public string endedAt;
    }

    /// <summary>GET /coop/sessions/{id}/result: 방어전의 결과 (팀원에게만).</summary>
    [Serializable]
    public sealed class CoopResultDto
    {
        /// <summary>실시간 서버의 기록이 게임 서버에 반영되었는가. 아니면 보상이 아직 정해지지 않았다.</summary>
        public bool finished;
        public CoopMyResultDto result = new CoopMyResultDto();
        public List<CoopTeamMemberDto> team = new List<CoopTeamMemberDto>();
    }
}