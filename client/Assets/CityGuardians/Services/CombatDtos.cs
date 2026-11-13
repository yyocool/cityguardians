using System;
using System.Collections.Generic;
using CityGuardians.Domain;

namespace CityGuardians.Services
{
    // v8.0: 전투 모드, 전투 장비, FPS 임무, 도시 위험도, 지역 랭킹, 도시 간 지원의 응답 형식.
    // docs/API_SPEC.md 14~17장에 대응한다.
    // 위험도 숫자를 서버로 보내는 요청 형식은 없다. 여기 있는 위험도는 모두 서버가 내려 주는 값이다.

    /// <summary>
    /// 전투 결과가 도시에 반영된 내용. 방어전·사냥·FPS 응답에 함께 온다.
    /// 이방인이거나 반영된 것이 없으면 응답에서 null 이다.
    /// </summary>
    [Serializable]
    public sealed class CityEffectDto
    {
        public string regionCode;
        /// <summary>RiskActors.Player (우리 도시) 또는 Supporter (지원 나간 도시).</summary>
        public string actor;
        /// <summary>RiskReasons.</summary>
        public string reason;
        /// <summary>바뀌기 전과 뒤의 위험도 (0~100).</summary>
        public int riskBefore;
        public int riskAfter;
        /// <summary>바뀐 양 (1/100 단위). 음수면 내려갔다.</summary>
        public int riskDeltaCenti;
        public string stageBefore;
        public string stage;
        /// <summary>이번에 얻은 도시 기여도.</summary>
        public int contribution;
        /// <summary>이번 승리가 막는 데 쓰인 침공. 없으면 비어 있다 (순찰).</summary>
        public string invasionId;
        public string invasionKind;
        public int invasionProgress;
        public int invasionRequired;
        /// <summary>이번 승리로 침공을 끝까지 막아 냈는가.</summary>
        public bool invasionRepelled;
        /// <summary>오늘 한 사람이 낮출 수 있는 한도에 닿아 더 내려가지 않았는가.</summary>
        public bool capped;

        public bool StageChanged => stageBefore != stage;
    }

    /// <summary>GET /combat/overview: 전투 모드, 가진 장비, 모드별 장착, 오늘의 보상 현황.</summary>
    [Serializable]
    public sealed class CombatOverviewDto
    {
        public List<CombatModeDef> modes = new List<CombatModeDef>();
        public List<OwnedGear> gear = new List<OwnedGear>();
        public List<LoadoutEntry> loadouts = new List<LoadoutEntry>();
        public List<string> clearedFpsMissions = new List<string>();
        /// <summary>오늘 GC 보상을 받은 FPS 임무의 수와 한도.</summary>
        public int fpsRewardsToday;
        public int fpsRewardLimit;
        /// <summary>오늘 내가 낮춘 도시 위험도와 한도 (0~100).</summary>
        public int riskReducedToday;
        public int riskReduceCap;
    }

    /// <summary>장비 구매·강화·장착의 응답.</summary>
    [Serializable]
    public sealed class GearResultDto
    {
        public string gearCode;
        /// <summary>처리한 뒤의 강화 단계.</summary>
        public int level;
        /// <summary>이번에 낸 GC. 장착은 0.</summary>
        public int costGc;
        public long gc;
        public List<OwnedGear> gear = new List<OwnedGear>();
        public List<LoadoutEntry> loadouts = new List<LoadoutEntry>();
        /// <summary>같은 요청을 다시 보낸 것이라 새로 처리하지 않았는가.</summary>
        public bool duplicate;
    }

    [Serializable]
    public sealed class FpsSessionDto
    {
        public string sessionId;
        public string missionCode;
        /// <summary>이 임무가 막으려는 침공. 없으면 순찰 출동이다.</summary>
        public string invasionId;
        /// <summary>임무가 반영될 도시. 이방인이면 비어 있다.</summary>
        public string regionCode;
    }

    /// <summary>POST /fps/missions/{id}/finish 의 응답.</summary>
    [Serializable]
    public sealed class FpsResultDto
    {
        public bool accepted;
        public string result;
        public int rewardGc;
        public int rewardXp;
        public bool firstWin;
        /// <summary>오늘의 보상 횟수를 다 써서 GC를 받지 못했는가.</summary>
        public bool rewardLimited;
        public long gc;
        public int level;
        public int xp;
        public int xpNext;
        public int hpMax;
        public int levelsGained;
        public string rejectReason;
        public int facilitiesSaved;
        public int civiliansSaved;
        public List<string> clearedFpsMissions = new List<string>();
        public int defensePoints;
        public int fpsRewardsToday;
        public int fpsRewardLimit;
        public CityEffectDto city;
    }

    /// <summary>도시 하나의 위험도와 방어 현황. 전국 지도에 쓴다.</summary>
    [Serializable]
    public sealed class CityStatusDto
    {
        public string regionCode;
        /// <summary>위험도 0~100.</summary>
        public int risk;
        /// <summary>RiskStages.</summary>
        public string stage;
        public int residents;
        /// <summary>최근에 접속한 주민 수. 랭킹과 위험도 보정의 기준이다.</summary>
        public int activePlayers;
        public long defensePoints;
        /// <summary>도시 방어력: NPC 수비대 + 방어 포인트 + 도시 공용 방어시설.</summary>
        public int defensePower;
        /// <summary>진행 중인 침공. 없으면 종류가 비어 있다.</summary>
        public string invasionKind;
        public int invasionSeverity;
        public int invasionProgress;
        public int invasionRequired;
        public string invasionEndsAt;
        /// <summary>최근 7일 동안의 침공 수.</summary>
        public int recentInvasions;
        /// <summary>플레이어가 쌓은 도시 기여도 (NPC의 방어는 들어가지 않는다).</summary>
        public long contribution;
        public int rank;
        /// <summary>랭킹 종합 점수 (0~10000).</summary>
        public int score;
        /// <summary>활성 가디언이 있는가. 없으면 NPC 수비대만 지키는 도시다.</summary>
        public bool playerDefended;
        /// <summary>열려 있는 지원 요청. 없으면 비어 있다.</summary>
        public string supportRequestId;

        public bool HasInvasion => !string.IsNullOrEmpty(invasionKind);
        public bool SupportOpen => !string.IsNullOrEmpty(supportRequestId);
    }

    /// <summary>GET /cities/risk: 전국 위험도.</summary>
    [Serializable]
    public sealed class CityRisksDto
    {
        public string serverTime;
        public List<CityStatusDto> cities = new List<CityStatusDto>();
        public string homeRegionCode;
        /// <summary>지금 지원 나가 있는 도시. 없으면 비어 있다.</summary>
        public string supportRegionCode;

        public CityStatusDto Find(string regionCode)
        {
            for (int i = 0; i < cities.Count; i++)
                if (cities[i].regionCode == regionCode) return cities[i];
            return null;
        }
    }

    [Serializable]
    public sealed class FacilityStatusDto
    {
        public string code;
        public string name;
        /// <summary>피해 단계. 0이면 멀쩡하다.</summary>
        public int damage;
        public int maxDamage;
        /// <summary>한 단계를 고치는 값.</summary>
        public int repairGc;
    }

    /// <summary>도시 공용 방어시설의 현황. 개인 자산이 아니다.</summary>
    [Serializable]
    public sealed class CityStructureDto
    {
        public string code;
        public string name;
        public int level;
        public int maxLevel;
        /// <summary>다음 단계까지 모인 기금.</summary>
        public long fundedGc;
        public int goalGc;
        /// <summary>지금 단계가 올려 주는 도시 방어력.</summary>
        public int power;
    }

    /// <summary>GET /cities/{code}: 도시 상세.</summary>
    [Serializable]
    public sealed class CityDetailDto
    {
        public CityStatusDto status;
        public string mood;
        public RankingScore score;
        /// <summary>최근 위험도 변경 이력. 새 것이 앞이다.</summary>
        public List<RiskHistoryEntry> history = new List<RiskHistoryEntry>();
        /// <summary>진행 중이거나 최근에 끝난 침공. 새 것이 앞이다.</summary>
        public List<InvasionEvent> invasions = new List<InvasionEvent>();
        public List<FacilityStatusDto> facilities = new List<FacilityStatusDto>();
        public List<CityStructureDto> structures = new List<CityStructureDto>();
        public SupportRequest support;
        /// <summary>내가 이 도시에 쌓은 기여도 (우리 도시일 때만).</summary>
        public long myContribution;
        public bool isHome;
    }

    [Serializable]
    public sealed class RankingRowDto
    {
        public string regionCode;
        public int rank;
        public int score;
        public int safety;
        public int successRate;
        public int contribution;
        public int response;
        public int cooperation;
        public bool playerDefended;
        public int risk;
        public string stage;
        public int activePlayers;
    }

    /// <summary>GET /cities/rankings.</summary>
    [Serializable]
    public sealed class RankingsDto
    {
        /// <summary>순위 순서. 활성 가디언이 있는 도시가 먼저 온다.</summary>
        public List<RankingRowDto> rows = new List<RankingRowDto>();
        public int totalCities;
        /// <summary>활성 가디언이 있는 도시의 수.</summary>
        public int playerCities;
        /// <summary>우리 도시의 줄. 이방인이면 null.</summary>
        public RankingRowDto home;
    }

    [Serializable]
    public sealed class SupportRowDto
    {
        public string requestId;
        public string regionCode;
        public int risk;
        public string stage;
        public string closesAt;
        public int wins;
        public long points;
        public bool canJoin;
        /// <summary>나설 수 없는 이유 (SupportReasons). 나설 수 있으면 비어 있다.</summary>
        public string reason;
    }

    /// <summary>GET /cities/support: 열려 있는 지원 요청.</summary>
    [Serializable]
    public sealed class SupportListDto
    {
        public List<SupportRowDto> requests = new List<SupportRowDto>();
        public string joinedRequestId;
        public string joinedRegionCode;
        /// <summary>내가 지금까지 다른 도시를 도와 쌓은 기여도.</summary>
        public long mySupportPoints;
    }

    /// <summary>지원 참가·그만두기의 응답. 소속 도시는 바뀌지 않는다.</summary>
    [Serializable]
    public sealed class SupportResultDto
    {
        public string joinedRequestId;
        public string joinedRegionCode;
        public string homeRegionCode;
    }

    /// <summary>시설 복구와 도시 기금의 응답.</summary>
    [Serializable]
    public sealed class CityActionDto
    {
        public int costGc;
        public long gc;
        public int contribution;
        public CityEffectDto city;
        public CityDetailDto detail;
        public bool duplicate;
    }
}
