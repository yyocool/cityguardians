using System;
using System.Collections.Generic;
using CityGuardians.Domain;

namespace CityGuardians.Services
{
    /// <summary>
    /// 게임 서버와의 통신 창구 (docs/DEVELOPMENT_PLAN.md 4장).
    /// 게임 코드는 이 인터페이스만 알고, 구현은 로컬 모의 서버 또는 실제 HTTP API로 바꿔 끼운다.
    /// 금액과 보상은 항상 구현(서버) 쪽이 계산한다.
    /// </summary>
    public interface IGameBackend
    {
        /// <summary>정의 데이터. Bootstrap이 성공한 뒤에 쓸 수 있다.</summary>
        GameDefs Defs { get; }

        /// <summary>기기에 게스트 정보가 있으면 접속하고, 없으면 새 게스트를 만든다.</summary>
        void Bootstrap(Action<ApiResult<MeDto>> done);

        /// <summary>읽을 수 없는 게스트 정보를 따로 보관해 두고 새 게스트로 시작한다. 사용자가 동의했을 때만 부른다.</summary>
        void StartOverAsNewGuest(Action<ApiResult<MeDto>> done);

        void GetMe(Action<ApiResult<MeDto>> done);

        void SetFlag(string key, string value, Action<ApiResult<MeDto>> done);

        void StartHunt(string zoneCode, Action<ApiResult<HuntSessionDto>> done);

        /// <param name="seq">보고 순번. 1부터 1씩 증가. 같은 순번을 다시 보내면 처음 결과를 그대로 받는다.</param>
        void ReportHunt(string sessionId, int seq, List<KillCount> kills, Action<ApiResult<HuntReportDto>> done);

        void ClaimQuest(string questCode, Action<ApiResult<QuestRewardDto>> done);

        /// <summary>방어전 시작. 서버가 시작 시각을 기록한다.</summary>
        void StartBattle(string stageCode, Action<ApiResult<BattleSessionDto>> done);

        /// <summary>방어전 종료 보고. 같은 전투를 다시 보고하면 처음 결과를 그대로 받는다 (두 번 지급하지 않는다).</summary>
        void FinishBattle(string sessionId, BattleReport report, Action<ApiResult<BattleResultDto>> done);

        /// <summary>GC를 내고 타워를 해금한다. 값은 서버가 정의에서 읽는다.</summary>
        void UnlockTower(string towerCode, Action<ApiResult<TowerUnlockDto>> done);

        /// <summary>상점의 진열과 가격. 가격은 서버가 정한다.</summary>
        void GetShop(string shopCode, Action<ApiResult<ShopDto>> done);

        /// <summary>
        /// 아이템 하나를 산다. 가격은 서버가 정의에서 읽고, 잔액 차감·인벤토리 추가·원장 기록을 한꺼번에 한다.
        /// </summary>
        /// <param name="requestId">중복 방지 키. 같은 키로 다시 보내면 새로 사지 않고 처음 결과를 받는다.</param>
        void BuyItem(string shopCode, string itemCode, string requestId, Action<ApiResult<PurchaseResultDto>> done);

        /// <summary>
        /// 물건 하나를 그 상점에 되판다. 값은 서버가 정의에서 읽는다. 입고 있던 물건이면 그 칸이 비고,
        /// 가구는 넣어 둔 것만 팔 수 있다.
        /// </summary>
        /// <param name="requestId">중복 방지 키. 같은 키로 다시 보내면 한 번만 판다.</param>
        void SellItem(string shopCode, string itemCode, string requestId, Action<ApiResult<SellResultDto>> done);

        /// <summary>장착 칸(EquipSlots)을 바꾼다. itemCode가 비어 있으면 그 칸을 비운다.</summary>
        void EquipItem(string slot, string itemCode, Action<ApiResult<EquipResultDto>> done);

        /// <summary>가구를 방의 그 칸에 놓거나 옮긴다. 놓을 수 있는지는 서버가 검사한다 (RoomRules).</summary>
        /// <param name="x">차지할 칸들 가운데 서쪽 끝 칸.</param>
        /// <param name="z">차지할 칸들 가운데 남쪽 끝 칸.</param>
        /// <param name="rot">90도씩 돌린 횟수 (0~3).</param>
        void PlaceFurniture(string furnitureId, int x, int z, int rot, Action<ApiResult<RoomDto>> done);

        /// <summary>놓은 가구를 넣어 둔다. 이미 넣어 둔 가구여도 성공한다.</summary>
        void StoreFurniture(string furnitureId, Action<ApiResult<RoomDto>> done);

        /// <summary>소모품 하나를 쓴다. 효과의 크기는 서버가 정의에서 읽어 돌려준다.</summary>
        /// <param name="requestId">중복 방지 키. 같은 키로 다시 보내면 한 번만 쓴다.</param>
        void UseItem(string itemCode, string requestId, Action<ApiResult<ItemUseResultDto>> done);

        /// <summary>
        /// 은행: 지갑과 통장의 잔액, 내일 받을 이자, 최근 거래. 밀린 이자가 있으면 서버가 먼저 정산한다.
        /// 아직 쓸 수 없는 레벨이어도 요청은 성공하고 unlocked가 false다.
        /// </summary>
        void GetBank(Action<ApiResult<BankDto>> done);

        /// <summary>지갑에서 통장으로 옮긴다. 지갑 원장과 통장 원장에 한 줄씩 남는다.</summary>
        /// <param name="requestId">중복 방지 키. 같은 키로 다시 보내면 한 번만 옮긴다.</param>
        void BankDeposit(long amount, string requestId, Action<ApiResult<BankTransferDto>> done);

        /// <summary>통장에서 지갑으로 옮긴다.</summary>
        /// <param name="requestId">중복 방지 키. 같은 키로 다시 보내면 한 번만 옮긴다.</param>
        void BankWithdraw(long amount, string requestId, Action<ApiResult<BankTransferDto>> done);

        /// <summary>
        /// 지갑의 돈으로 정기예금에 가입한다. 만기일이 되면 원금과 약속한 이자가 통장으로 들어온다.
        /// 이자는 가입할 때 서버가 정한다.
        /// </summary>
        /// <param name="requestId">중복 방지 키. 같은 키로 다시 보내면 한 번만 가입한다.</param>
        void OpenTermDeposit(string productCode, long amount, string requestId, Action<ApiResult<TermResultDto>> done);

        /// <summary>정기예금을 만기 전에 찾는다. 원금만 통장으로 들어오고 이자는 없다.</summary>
        /// <param name="requestId">중복 방지 키. 같은 키로 다시 보내면 한 번만 찾는다.</param>
        void CancelTermDeposit(string productCode, string requestId, Action<ApiResult<TermResultDto>> done);

        /// <summary>지역 목록. 서버의 regions 테이블에 대응한다. Bootstrap이 성공한 뒤에 쓸 수 있다.</summary>
        RegionCatalog Regions { get; }

        /// <summary>지역별 주민 수와 방어 포인트. 지도를 열 때마다 새로 받는다.</summary>
        void GetRegions(Action<ApiResult<RegionsDto>> done);

        /// <summary>닉네임을 쓸 수 있는지 미리 물어본다. 쓸 수 없어도 요청 자체는 성공이다.</summary>
        void CheckNickname(string nickname, Action<ApiResult<NicknameCheckDto>> done);

        /// <summary>전입신고. 지역과 닉네임을 등록하고, 처음이면 축하금을 준다. 이미 주민이면 실패한다.</summary>
        void RegisterResidency(string regionCode, string nickname, Action<ApiResult<ResidencyResultDto>> done);

        // ── v8.0 공통 전투 (docs/API_SPEC.md 14장) ──────────────

        /// <summary>전투 모드 목록, 가진 장비, 모드별 장착, 오늘의 보상 현황.</summary>
        void GetCombatOverview(Action<ApiResult<CombatOverviewDto>> done);

        /// <summary>전투 장비를 산다. 값은 서버가 정의에서 읽는다.</summary>
        /// <param name="requestId">중복 방지 키. 같은 키로 다시 보내면 한 번만 산다.</param>
        void BuyGear(string gearCode, string requestId, Action<ApiResult<GearResultDto>> done);

        /// <summary>가진 전투 장비를 한 단계 강화한다. 값은 서버가 계산한다.</summary>
        /// <param name="requestId">중복 방지 키. 같은 키로 다시 보내면 한 번만 강화한다.</param>
        void UpgradeGear(string gearCode, string requestId, Action<ApiResult<GearResultDto>> done);

        /// <summary>그 모드의 그 칸에 장비를 낀다. gearCode가 비어 있으면 기본 장비로 돌아간다.</summary>
        void EquipGear(string modeCode, string slot, string gearCode, Action<ApiResult<GearResultDto>> done);

        // ── v8.0 FPS 임무 (docs/API_SPEC.md 16장) ───────────────

        /// <summary>FPS 임무를 시작한다. 서버가 시작 시각을 기록한다.</summary>
        void StartFpsMission(string missionCode, Action<ApiResult<FpsSessionDto>> done);

        /// <summary>FPS 임무 종료 보고. 같은 임무를 다시 보고하면 처음 결과를 그대로 받는다.</summary>
        void FinishFpsMission(string sessionId, FpsReport report, Action<ApiResult<FpsResultDto>> done);

        // ── v8.0 도시 위험도, 랭킹, 지원 (docs/API_SPEC.md 17장) ─
        // 위험도는 서버가 계산한다. 위험도 숫자를 보내는 요청은 없다.

        /// <summary>전국 모든 도시의 위험도와 방어 현황.</summary>
        void GetCityRisks(Action<ApiResult<CityRisksDto>> done);

        /// <summary>도시 하나의 상세: 위험도 이력, 침공 현황, 시설, 공용 방어시설, 랭킹 점수.</summary>
        void GetCityDetail(string regionCode, Action<ApiResult<CityDetailDto>> done);

        /// <summary>지역 랭킹.</summary>
        void GetRankings(Action<ApiResult<RankingsDto>> done);

        /// <summary>열려 있는 도시 간 지원 요청.</summary>
        void GetSupportRequests(Action<ApiResult<SupportListDto>> done);

        /// <summary>
        /// 다른 도시의 지원 요청에 나선다. 그 뒤의 방어전·FPS 승리는 그 도시에 반영된다. 소속 도시는 바뀌지 않는다.
        /// </summary>
        void JoinSupport(string requestId, Action<ApiResult<SupportResultDto>> done);

        /// <summary>지원을 그만두고 우리 도시로 돌아온다.</summary>
        void LeaveSupport(Action<ApiResult<SupportResultDto>> done);

        /// <summary>소속 도시의 피해 입은 시설을 한 단계 고친다. 값은 서버가 정의에서 읽는다.</summary>
        /// <param name="requestId">중복 방지 키.</param>
        void RepairFacility(string facilityCode, string requestId, Action<ApiResult<CityActionDto>> done);

        /// <summary>소속 도시의 공용 방어시설 기금에 GC를 낸다. 낸 GC는 개인 자산이 아니게 된다.</summary>
        /// <param name="requestId">중복 방지 키.</param>
        void FundCityStructure(string structureCode, int amountGc, string requestId, Action<ApiResult<CityActionDto>> done);

        // ── 현실 혜택: CITY PASS, GUARDIANS SCHOLARSHIP (docs/REAL_WORLD_REWARDS_POLICY.md) ──
        //
        // 아래 요청은 GC, 전투 성적, 랭킹, 레벨을 받지도 바꾸지도 않는다.
        // 일부러 두지 않은 요청: GC 로 쿠폰 사기, 쿠폰을 GC 로 바꾸기, 쿠폰을 "사용됨"으로 바꾸기(상점의 일),
        // 장학 신청·심사·지급 (홈페이지와 운영팀의 일). 게임은 쿠폰을 보여 주고 장학 프로그램을 안내할 뿐이다.
        // 운영 승인(법률 검토 포함)이 끝나지 않은 기능은 서버가 닫아 둔다: 조회는 "준비 중"으로, 발급은 거절로 돌아온다.

        /// <summary>홈페이지 로그인에 한 번 쓰는 코드를 받는다. 새로 받으면 앞의 코드는 못 쓴다.</summary>
        void IssueWebLoginCode(Action<ApiResult<WebLoginCodeDto>> done);

        void GetCityPass(Action<ApiResult<CityPassProfileDto>> done);

        /// <summary>CITY PASS 에 가입한다. 값은 치르지 않는다. 이미 가입했으면 그대로 돌려준다.</summary>
        /// <param name="interestRegionCode">관심 지역. 비우면 전입신고한 지역. 실제 거주지가 아니어도 된다.</param>
        void JoinCityPass(bool agreeTerms, string interestRegionCode, Action<ApiResult<CityPassProfileDto>> done);

        void SetCityPassRegion(string regionCode, Action<ApiResult<CityPassProfileDto>> done);

        /// <param name="regionCode">이 지역에 지점이 있는 혜택만. 비우면 전국.</param>
        /// <param name="category">업종. 비우면 전부.</param>
        void GetCityPassOffers(string regionCode, string category, Action<ApiResult<PassOffersDto>> done);

        /// <summary>제휴 상점. 운영팀이 승인한 (계약이 끝난) 상점만 온다.</summary>
        void GetCityPassMerchants(string regionCode, string category, Action<ApiResult<PassMerchantsDto>> done);

        void GetCityPassCoupons(Action<ApiResult<PassCouponsDto>> done);

        /// <summary>혜택의 쿠폰을 받는다. 값은 치르지 않는다 (GC 도 들지 않는다).</summary>
        /// <param name="requestId">중복 방지 키. 같은 키로 다시 보내면 쿠폰이 두 장 생기지 않는다.</param>
        void ClaimCityPassCoupon(string offerId, string requestId, Action<ApiResult<PassClaimDto>> done);

        /// <summary>
        /// 쿠폰을 상점에 보여 줄 일회용 코드를 받는다 (5분). 이것만으로는 쿠폰이 쓰이지 않는다:
        /// 사용 처리는 상점이 확인 화면에서 하고, 서버가 한 번만 받아들인다.
        /// </summary>
        void PresentCityPassCoupon(string couponId, Action<ApiResult<PassPresentDto>> done);

        void GetCityPassHistory(Action<ApiResult<PassHistoryDto>> done);

        /// <summary>장학 프로그램의 공고 목록. 안내용이다.</summary>
        void GetScholarships(Action<ApiResult<ScholarshipListDto>> done);

        void GetScholarship(string programId, Action<ApiResult<ScholarshipProgramDto>> done);

        /// <summary>홈페이지에서 낸 내 신청의 진행 상태. 신청서의 내용은 오지 않는다.</summary>
        void GetMyScholarshipApplications(Action<ApiResult<ScholarshipApplicationsDto>> done);

        // ── v9.0 도시 성장, 이사, 지역 콘텐츠 (docs/API_SPEC.md 19장) ──
        // 전입자 수와 성장 단계는 서버가 센다. 숫자를 보내는 요청은 없다.

        /// <summary>전국 도시의 유효 전입자 수와 성장 단계.</summary>
        void GetCityGrowth(Action<ApiResult<CityGrowthListDto>> done);

        /// <summary>한 도시의 전입자 현황, 성장 단계, 상징거리 제작 상태.</summary>
        void GetCityGrowthDetail(string regionCode, Action<ApiResult<CityGrowthDto>> done);

        /// <summary>내 전입 상태와 이사 조건.</summary>
        void GetResidency(Action<ApiResult<ResidencyInfoDto>> done);

        /// <summary>
        /// 이사: 주거 도시를 바꾼다. 한 계정은 한 도시에만 산다. 이전 도시와 새 도시의 전입자 수가 함께 바뀐다.
        /// 이사 비용과 다시 이사할 수 있는 때는 서버가 정한다.
        /// </summary>
        /// <param name="requestId">중복 방지 키. 같은 키로 다시 보내면 한 번만 이사한다.</param>
        void MoveResidency(string regionCode, string requestId, Action<ApiResult<MoveResultDto>> done);

        /// <summary>이 클라이언트가 받을 수 있는 지역 콘텐츠 (상징물, 상징거리). 받은 뒤 CityContent 로 다시 검사한다.</summary>
        void GetCityContent(Action<ApiResult<CityContentListDto>> done);

        // ── v9.0 가상 지방세, 도시 재정, 참여예산 (docs/API_SPEC.md 20장) ──
        // 게임 안의 GC 로만 움직인다. 실제 돈, 현실의 세금, CITY PASS, 장학 사업의 예산과 이어지지 않는다.
        // 세금은 서버가 거래를 처리하면서 함께 걷는다. "세금을 낸다"는 요청은 없다.

        void GetTaxPolicies(Action<ApiResult<TaxPoliciesDto>> done);

        /// <summary>내가 낸 세금의 명세.</summary>
        void GetTaxTransactions(Action<ApiResult<TaxTransactionsDto>> done);

        /// <summary>도시 재정 현황. 누구나 볼 수 있다.</summary>
        void GetTreasury(string regionCode, Action<ApiResult<TreasuryDto>> done);

        /// <summary>예산 집행 내역. 누구나 볼 수 있다.</summary>
        void GetExpenditures(string regionCode, Action<ApiResult<ExpendituresDto>> done);

        void GetBudgetOptions(Action<ApiResult<BudgetOptionsDto>> done);

        void GetBudgetProposals(string regionCode, Action<ApiResult<BudgetListDto>> done);

        /// <summary>내 주거 도시에 예산을 제안한다. 운영팀의 검토를 거친 뒤에 주민 투표에 올라간다.</summary>
        /// <param name="requestId">중복 방지 키.</param>
        void ProposeBudget(string category, string targetCode, string title, string description, int requestedGc, string requestId,
            Action<ApiResult<BudgetProposalResultDto>> done);

        /// <summary>주민 투표. 한 계정이 한 표다 (가진 GC 나 낸 세금과 관계가 없다). 한 번 던진 표는 바꿀 수 없다.</summary>
        /// <param name="choice">"yes" 또는 "no".</param>
        void VoteBudget(string proposalId, string choice, string requestId, Action<ApiResult<BudgetProposalResultDto>> done);

        // ── v9.0 협동 타워디펜스 (docs/API_SPEC.md 21~22장) ──
        // 여기에는 판에 들어가고 나가는 것, 입장권, 끝난 뒤의 결과만 있다.
        // 전투 중의 상태(적, 타워, 공동 예산, 전술 핑)는 실시간 서버와 주고받는다 (RemoteCoopLink).

        void GetCoopConfig(Action<ApiResult<CoopConfigDto>> done);

        void GetCoopSessions(Action<ApiResult<CoopSessionsDto>> done);

        /// <param name="roleCode">고른 역할. 비워도 된다 (역할은 강제가 아니다).</param>
        void CreateCoopSession(string zoneCode, int maxPlayers, string roleCode, string requestId, Action<ApiResult<CoopSessionResultDto>> done);

        void JoinCoopSession(string sessionId, string roleCode, string requestId, Action<ApiResult<CoopSessionResultDto>> done);

        void LeaveCoopSession(string sessionId, Action<ApiResult<CoopSessionResultDto>> done);

        /// <summary>실시간 서버에 들어갈 입장권을 받는다. 짧게만, 한 번만 쓸 수 있다.</summary>
        void GetCoopTicket(string sessionId, Action<ApiResult<CoopTicketDto>> done);

        /// <summary>끝난 방어전의 결과와 내 보상. 보상은 서버가 정한다.</summary>
        void GetCoopResult(string sessionId, Action<ApiResult<CoopResultDto>> done);

        // ── v10.0 실시간 대시보드 (docs/API_SPEC.md 23장) ──
        // 화면 오른쪽의 카드 세 장이 한 번에 받는 요약. 받는 인자가 없다: 언제나 접속한 사람 자신의 값이다.
        // 상세 화면은 위의 기존 API (GetBank, GetCityDetail, GetRankings, GetTreasury …) 를 그대로 쓴다.

        void GetDashboardSummary(Action<ApiResult<DashboardSummaryDto>> done);
    }

    public interface IClock
    {
        DateTime UtcNow { get; }
    }

    public sealed class SystemClock : IClock
    {
        public DateTime UtcNow => DateTime.UtcNow;
    }

    /// <summary>이름으로 글을 읽고 쓰는 저장소. 파일 또는 메모리.</summary>
    public interface ITextStore
    {
        /// <summary>없으면 null.</summary>
        string Read(string name);
        void Write(string name, string text);
        /// <summary>내용을 지우지 않고 다른 이름으로 옮긴다. 원본이 없으면 아무 일도 하지 않는다.</summary>
        void Move(string name, string newName);
    }

    public sealed class MemoryTextStore : ITextStore
    {
        private readonly Dictionary<string, string> _items = new Dictionary<string, string>();

        public string Read(string name) => _items.TryGetValue(name, out string text) ? text : null;

        public void Write(string name, string text) => _items[name] = text;

        public void Move(string name, string newName)
        {
            if (!_items.TryGetValue(name, out string text)) return;
            _items.Remove(name);
            _items[newName] = text;
        }
    }
}
