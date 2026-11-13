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
