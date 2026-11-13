using System;
using System.Collections.Generic;
using CityGuardians.Domain;

namespace CityGuardians.Services
{
    /// <summary>서버 응답 한 건. docs/API_SPEC.md 1장의 { ok, data, error } 형식에 대응한다.</summary>
    public sealed class ApiResult<T>
    {
        public bool ok;
        public T data;
        public string errorCode;
        public string message;

        public static ApiResult<T> Success(T data) => new ApiResult<T> { ok = true, data = data };

        public static ApiResult<T> Fail(string errorCode, string message) =>
            new ApiResult<T> { ok = false, errorCode = errorCode, message = message };
    }

    public static class ApiErrors
    {
        public const string InvalidRequest = "INVALID_REQUEST";
        public const string Unauthorized = "UNAUTHORIZED";
        public const string NotFound = "NOT_FOUND";
        public const string Conflict = "CONFLICT";
        public const string ValidationFailed = "VALIDATION_FAILED";
        /// <summary>기기에 저장된 게스트 정보를 읽을 수 없음. 조용히 덮어쓰지 않고 사용자에게 묻는다.</summary>
        public const string GuestDataCorrupt = "GUEST_DATA_CORRUPT";
        public const string StorageFailed = "STORAGE_FAILED";
        public const string InsufficientFunds = "INSUFFICIENT_FUNDS";
        /// <summary>닉네임이 규칙에 맞지 않음. message에 이유가 들어 있다.</summary>
        public const string NicknameInvalid = "NICKNAME_INVALID";
        public const string NicknameTaken = "NICKNAME_TAKEN";
        /// <summary>이미 어느 도시의 주민임. 도시를 바꾸려면 이사를 해야 한다.</summary>
        public const string AlreadyResident = "ALREADY_RESIDENT";
        /// <summary>이미 가진 장비를 또 사려 함.</summary>
        public const string AlreadyOwned = "ALREADY_OWNED";
        /// <summary>가방에 더 넣을 수 없음 (칸이 모자라거나 겹칠 수 있는 수를 넘음).</summary>
        public const string InventoryFull = "INVENTORY_FULL";
        /// <summary>가지고 있지 않은 아이템을 쓰거나 장착하려 함.</summary>
        public const string NotOwned = "NOT_OWNED";
        /// <summary>아직 쓸 수 없는 기능 (레벨 조건 등).</summary>
        public const string Locked = "LOCKED";

        /// <summary>아이템 규칙(InventoryRules)의 이유를 응답 오류 코드로 바꾼다.</summary>
        public static string FromItemReason(string reason)
        {
            switch (reason)
            {
                case ItemReasons.UnknownItem:
                case ItemReasons.UnknownShop:
                case ItemReasons.NotForSale:
                    return NotFound;
                case ItemReasons.AlreadyOwned: return AlreadyOwned;
                case ItemReasons.StackFull:
                case ItemReasons.BagFull:
                case ItemReasons.FurnitureFull:
                    return InventoryFull;
                case RoomReasons.StillPlaced: return Conflict;
                case ItemReasons.InsufficientFunds: return InsufficientFunds;
                case ItemReasons.NotOwned: return NotOwned;
                default: return InvalidRequest;
            }
        }
    }

    public static class FlagKeys
    {
        public const string TutorialStep = "tutorial_step";
        /// <summary>전입신고 권유에 "나중에"라고 답했는가 ("1"). 답한 뒤에는 다시 권하지 않는다.</summary>
        public const string ResidencyDeclined = "residency_declined";
        /// <summary>손에 든 무기 칸 (EquipSlots). 다시 켰을 때 같은 무기를 들고 있게 한다.</summary>
        public const string ActiveSlot = "active_slot";
        /// <summary>화면에서 알린 이자의 합. 서버의 값(MeDto.bankInterestTotal)이 이보다 크면 새 이자가 들어온 것이다.</summary>
        public const string BankInterestSeen = "bank_interest_seen";
        /// <summary>은행 화면을 한 번이라도 열었는가. 열고 나면 은행 안내를 그만 띄운다.</summary>
        public const string BankVisited = "bank_visited";
        /// <summary>화면에서 알린 정기예금 만기의 수. 서버의 값(MeDto.termMaturedCount)이 이보다 크면 새로 만기가 된 것이다.</summary>
        public const string TermMaturedSeen = "term_matured_seen";
    }

    public static class QuestCodes
    {
        public const string FirstRescue = "first_rescue";
    }

    [Serializable]
    public sealed class FlagDto
    {
        public string key;
        public string value;
    }

    /// <summary>GET /me 에 대응하는 시작 데이터.</summary>
    [Serializable]
    public sealed class MeDto
    {
        public string playerId;
        public string displayName;
        /// <summary>ResidencyStatus.Stranger 또는 Resident.</summary>
        public string residency;
        /// <summary>전입한 지역. 이방인이면 비어 있다.</summary>
        public string regionCode;
        public int level;
        public int xp;
        public int xpNext;
        public int hpMax;
        public long gc;
        /// <summary>통장의 GC.</summary>
        public long bankGc;
        /// <summary>지금까지 받은 은행 이자의 합.</summary>
        public long bankInterestTotal;
        public string equippedMelee;
        /// <summary>장착한 원거리 무기. 없으면 비어 있다.</summary>
        public string equippedRanged;
        /// <summary>머리에 입은 방어구나 의상. 없으면 비어 있다.</summary>
        public string equippedHead;
        /// <summary>몸에 입은 방어구나 의상. 없으면 비어 있다.</summary>
        public string equippedBody;
        /// <summary>가진 아이템. 장착한 장비도 여기에 남아 있다.</summary>
        public List<ItemStack> inventory = new List<ItemStack>();
        /// <summary>가진 가구 (방에 놓은 것과 넣어 둔 것).</summary>
        public List<FurniturePiece> furniture = new List<FurniturePiece>();
        /// <summary>정기예금에 넣어 둔 원금의 합.</summary>
        public long termGc;
        /// <summary>지금까지 만기가 된 정기예금의 수.</summary>
        public long termMaturedCount;
        public int dailyHuntGc;
        public int dailyHuntGcLimit;
        public List<FlagDto> flags = new List<FlagDto>();
        /// <summary>쓸 수 있는 타워. 처음부터 쓸 수 있는 것과 GC로 해금한 것을 합친 목록.</summary>
        public List<string> unlockedTowers = new List<string>();
        /// <summary>한 번 이상 이긴 방어전 스테이지.</summary>
        public List<string> clearedStages = new List<string>();

        public string GetFlag(string key, string fallback = null)
        {
            for (int i = 0; i < flags.Count; i++)
                if (flags[i].key == key) return flags[i].value;
            return fallback;
        }
    }

    [Serializable]
    public sealed class HuntSessionDto
    {
        public string sessionId;
        public string zoneCode;
    }

    [Serializable]
    public sealed class HuntReportDto
    {
        public List<KillCount> accepted = new List<KillCount>();
        public int rewardGc;
        public int rewardXp;
        public long gc;
        public int level;
        public int xp;
        public int xpNext;
        public int hpMax;
        public int levelsGained;
        public int dailyHuntGc;
        public int dailyHuntGcLimit;
        public string rejectReason;
        /// <summary>이번 보고에서 서버가 판정한 드롭. 없으면 비어 있다.</summary>
        public List<ItemStack> drops = new List<ItemStack>();
        /// <summary>드롭이 있었을 때의 새 인벤토리. 드롭이 없으면 null.</summary>
        public List<ItemStack> inventory;
    }

    [Serializable]
    public sealed class QuestRewardDto
    {
        public string questCode;
        public int rewardGc;
        public long gc;
    }

    [Serializable]
    public sealed class BattleSessionDto
    {
        public string sessionId;
        public string stageCode;
    }

    /// <summary>POST /battles/{id}/finish 의 응답.</summary>
    [Serializable]
    public sealed class BattleResultDto
    {
        /// <summary>서버가 보고를 인정했는가. 인정하지 않으면 보상이 없다.</summary>
        public bool accepted;
        public string result;
        public int wavesCleared;
        public int rewardGc;
        public int rewardXp;
        public bool firstWin;
        public long gc;
        public int level;
        public int xp;
        public int xpNext;
        public int hpMax;
        public int levelsGained;
        public string rejectReason;
        public List<string> clearedStages = new List<string>();
        /// <summary>이번 전투로 소속 도시에 쌓인 방어 포인트. 이방인이거나 졌으면 0.</summary>
        public int defensePoints;
        /// <summary>방어 포인트가 쌓인 지역. 쌓이지 않았으면 비어 있다.</summary>
        public string regionCode;
    }

    [Serializable]
    public sealed class TowerUnlockDto
    {
        public string towerCode;
        public int costGc;
        public long gc;
        public List<string> unlockedTowers = new List<string>();
    }

    [Serializable]
    public sealed class ShopItemDto
    {
        public string itemCode;
        /// <summary>서버가 정한 가격.</summary>
        public int priceGc;
        /// <summary>이 상점이 되사는 값. 0이면 되사지 않는다.</summary>
        public int sellPriceGc;
    }

    /// <summary>GET /shops/{code} 의 응답.</summary>
    [Serializable]
    public sealed class ShopDto
    {
        public string code;
        public string name;
        public List<ShopItemDto> items = new List<ShopItemDto>();
    }

    /// <summary>POST /shops/{code}/buy 의 응답.</summary>
    [Serializable]
    public sealed class PurchaseResultDto
    {
        public string itemCode;
        public int priceGc;
        public long gc;
        public List<ItemStack> inventory = new List<ItemStack>();
        /// <summary>가진 가구. 가구를 샀으면 새 가구가 넣어 둔 상태로 들어 있다.</summary>
        public List<FurniturePiece> furniture = new List<FurniturePiece>();
        /// <summary>같은 요청을 다시 보낸 것이라 새로 사지 않았는가.</summary>
        public bool duplicate;
    }

    /// <summary>POST /shops/{code}/sell 의 응답. 판 물건을 입고 있었다면 그 칸이 비어서 온다.</summary>
    [Serializable]
    public sealed class SellResultDto
    {
        public string itemCode;
        /// <summary>받은 값 (서버의 아이템 정의 값).</summary>
        public int priceGc;
        public long gc;
        public List<ItemStack> inventory = new List<ItemStack>();
        public List<FurniturePiece> furniture = new List<FurniturePiece>();
        public string equippedMelee;
        public string equippedRanged;
        public string equippedHead;
        public string equippedBody;
        /// <summary>같은 요청을 다시 보낸 것이라 새로 팔지 않았는가.</summary>
        public bool duplicate;
    }

    /// <summary>PUT /character/equipment 의 응답.</summary>
    [Serializable]
    public sealed class EquipResultDto
    {
        public string equippedMelee;
        public string equippedRanged;
        public string equippedHead;
        public string equippedBody;
    }

    /// <summary>PUT /room/furniture/{id}, DELETE /room/furniture/{id}/placement 의 응답: 가진 가구 전체.</summary>
    [Serializable]
    public sealed class RoomDto
    {
        public List<FurniturePiece> furniture = new List<FurniturePiece>();
    }

    /// <summary>POST /inventory/use 의 응답.</summary>
    [Serializable]
    public sealed class ItemUseResultDto
    {
        public string itemCode;
        /// <summary>회복한 체력 (서버의 아이템 정의 값).</summary>
        public int healHp;
        public int remaining;
        public List<ItemStack> inventory = new List<ItemStack>();
        public bool duplicate;
    }

    /// <summary>은행 거래 내역의 한 줄.</summary>
    [Serializable]
    public sealed class BankEntryDto
    {
        /// <summary>LedgerReasons의 bank_deposit, bank_withdraw, bank_interest, term_matured, term_interest, term_cancel.</summary>
        public string reason;
        /// <summary>통장 잔액의 변화. 맡기면 +, 찾으면 −.</summary>
        public long delta;
        public long balanceAfter;
        /// <summary>한국 날짜 (yyyy-MM-dd).</summary>
        public string date;
    }

    /// <summary>GET /bank 의 응답. 금액과 이자는 모두 서버가 계산한 값이다.</summary>
    [Serializable]
    public sealed class BankDto
    {
        /// <summary>은행을 쓸 수 있는가 (레벨 조건).</summary>
        public bool unlocked;
        public int unlockLevel;
        /// <summary>지갑의 GC.</summary>
        public long gc;
        /// <summary>통장의 GC.</summary>
        public long bankGc;
        /// <summary>지금대로 두면 다음 자정(한국 시간)에 받을 이자.</summary>
        public long interestTomorrow;
        /// <summary>오늘 맡긴 돈까지 이자가 붙기 시작한 뒤의 하루 이자.</summary>
        public long interestWhenSettled;
        /// <summary>하루 이율, 1만분의 1 단위 (100 = 1%).</summary>
        public int dailyInterestBp;
        public long interestCap;
        /// <summary>하루 이자 1 GC가 붙기 시작하는 잔액. 이자가 없으면 0.</summary>
        public long balanceForOneGc;
        /// <summary>지금까지 받은 이자의 합.</summary>
        public long interestTotal;
        /// <summary>최근 거래. 새것이 앞에 온다.</summary>
        public List<BankEntryDto> recent = new List<BankEntryDto>();
        /// <summary>정기예금 상품과, 가입했다면 그 내용.</summary>
        public List<TermProductDto> terms = new List<TermProductDto>();
        /// <summary>정기예금에 넣어 둔 원금의 합.</summary>
        public long termGc;
        /// <summary>지금까지 만기가 된 정기예금의 수.</summary>
        public long termMaturedCount;
    }

    /// <summary>정기예금 상품 하나와 내 가입 상태.</summary>
    [Serializable]
    public sealed class TermProductDto
    {
        public string code;
        public string name;
        public int days;
        /// <summary>만기에 받는 이자의 비율, 1만분의 1 단위 (1000 = 원금의 10%).</summary>
        public int rateBp;
        public long minAmount;
        public long maxAmount;
        /// <summary>가입해 있는가.</summary>
        public bool open;
        /// <summary>넣어 둔 원금. 가입하지 않았으면 0.</summary>
        public long principal;
        /// <summary>만기에 받을 이자 (가입할 때 정해진 값).</summary>
        public long interest;
        /// <summary>만기일 (한국 날짜).</summary>
        public string maturesDate;
        /// <summary>만기까지 남은 날 수.</summary>
        public int daysLeft;
    }

    /// <summary>POST /bank/accounts, POST /bank/accounts/{product}/cancel 의 응답.</summary>
    [Serializable]
    public sealed class TermResultDto
    {
        public string productCode;
        /// <summary>원금.</summary>
        public long amount;
        /// <summary>가입이면 만기에 받을 이자, 중도 해지면 받지 못한 이자.</summary>
        public long interest;
        public BankDto bank;
        /// <summary>같은 요청을 다시 보낸 것이라 새로 처리하지 않았는가.</summary>
        public bool duplicate;
    }

    /// <summary>POST /bank/deposit, /bank/withdraw 의 응답.</summary>
    [Serializable]
    public sealed class BankTransferDto
    {
        /// <summary>옮긴 금액.</summary>
        public long amount;
        public BankDto bank;
        /// <summary>같은 요청을 다시 보낸 것이라 새로 옮기지 않았는가.</summary>
        public bool duplicate;
    }

    /// <summary>지역 하나의 현재 상태.</summary>
    [Serializable]
    public sealed class RegionStatusDto
    {
        public string regionCode;
        /// <summary>전입한 주민 수.</summary>
        public int residents;
        public long defensePoints;
    }

    /// <summary>GET /regions 의 상태 부분. 주민도 방어 포인트도 없는 지역은 목록에 넣지 않는다.</summary>
    [Serializable]
    public sealed class RegionsDto
    {
        public List<RegionStatusDto> regions = new List<RegionStatusDto>();
    }

    /// <summary>GET /nicknames/check 의 응답.</summary>
    [Serializable]
    public sealed class NicknameCheckDto
    {
        public string nickname;
        public bool available;
        /// <summary>쓸 수 없는 이유 (NicknameRules의 이유 코드). 쓸 수 있으면 비어 있다.</summary>
        public string reason;
        public string message;
    }

    /// <summary>POST /residency 의 응답.</summary>
    [Serializable]
    public sealed class ResidencyResultDto
    {
        public string regionCode;
        public string nickname;
        /// <summary>이번에 받은 전입 축하금. 이미 받은 적이 있으면 0.</summary>
        public int bonusGc;
        public RegionStatusDto region;
        public MeDto me;
    }
}
