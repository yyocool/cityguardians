using System;
using System.Collections.Generic;

namespace CityGuardians.Services
{
    // 현실 혜택 (CITY PASS, GUARDIANS SCHOLARSHIP) 의 응답 (docs/CITY_PASS_API.md, docs/SCHOLARSHIP_API.md).
    // 게임과 홈페이지가 같은 서버 자료를 본다. 어느 응답에도 GC·전투 성적·랭킹·레벨은 없다.
    // 서버가 값을 비워 보낼 수 있는 숫자(남은 수, 지도 좌표)는 여기서 "없음"을 따로 적는다.

    /// <summary>홈페이지 로그인에 한 번 쓰는 코드. 5분 동안 살아 있다.</summary>
    [Serializable]
    public sealed class WebLoginCodeDto
    {
        /// <summary>"XXXX-XXXX"</summary>
        public string code;
        public string expiresAt;
        public int expiresInSeconds;
        /// <summary>코드를 넣는 주소. 서버에 공개 주소가 정해지지 않았으면 비어 있다.</summary>
        public string loginUrl;
    }

    [Serializable]
    public sealed class BenefitRegionDto
    {
        public string code;
        public string name;
        public string sidoName;
    }

    [Serializable]
    public sealed class CouponCountsDto
    {
        public int available;
        public int used;
        public int expired;
    }

    /// <summary>내 CITY PASS. 실제 이름이나 연락처는 없다: 닉네임과 회원번호뿐이다.</summary>
    [Serializable]
    public sealed class CityPassProfileDto
    {
        /// <summary>운영 승인이 끝나 가입을 받고 있는가.</summary>
        public bool membershipEnabled;
        public bool couponsEnabled;
        public bool isMember;
        public string memberNo;
        public string status;
        public string nickname;
        /// <summary>관심 지역. 가입하지 않았거나 고르지 않았으면 null. 실제 거주지가 아니어도 된다.</summary>
        public BenefitRegionDto interestRegion;
        public string joinedAt;
        public string termsVersion;
        public CouponCountsDto coupons = new CouponCountsDto();
        /// <summary>준비 중일 때의 안내. 열려 있으면 비어 있다.</summary>
        public string notice;
    }

    [Serializable]
    public sealed class PassBranchDto
    {
        public string id;
        public string name;
        public string regionCode;
        public string regionName;
        public string address;
        public string phone;
        /// <summary>지도 위치가 등록되어 있는가. 없으면 lat, lng 는 뜻이 없다.</summary>
        public bool hasLocation;
        public double lat;
        public double lng;
        public string imagePath;
    }

    [Serializable]
    public sealed class PassMerchantDto
    {
        public string id;
        public string name;
        public string category;
        public string categoryName;
        public List<PassBranchDto> branches = new List<PassBranchDto>();
    }

    [Serializable]
    public sealed class PassMerchantsDto
    {
        public bool couponsEnabled;
        public List<PassMerchantDto> merchants = new List<PassMerchantDto>();
    }

    [Serializable]
    public sealed class PassOfferDto
    {
        public string id;
        public string title;
        public string description;
        public string discountType;
        public int discountValue;
        public string discountText;
        public string conditions;
        /// <summary>쿠폰을 주는 근거: membership, promotion, partner_condition. 게임 성적이나 GC 는 근거가 될 수 없다.</summary>
        public string issuePolicy;
        public string startsAt;
        public string endsAt;
        public int couponValidDays;
        public int perMemberLimit;
        /// <summary>남은 수. 정해진 수가 없으면 -1.</summary>
        public int remaining = -1;
        public PassMerchantDto merchant;
        public List<PassBranchDto> branches = new List<PassBranchDto>();
        public int myCoupons;
        public bool claimable;
        /// <summary>받을 수 없는 이유 (ClaimBlocks). 받을 수 있으면 비어 있다.</summary>
        public string claimBlock;
    }

    [Serializable]
    public sealed class PassOffersDto
    {
        public bool couponsEnabled;
        public string regionCode;
        public List<PassOfferDto> offers = new List<PassOfferDto>();
        public string notice;
    }

    [Serializable]
    public sealed class PassCouponOfferDto
    {
        public string id;
        public string title;
        public string description;
        public string discountText;
        public string conditions;
    }

    [Serializable]
    public sealed class PassCouponDto
    {
        public string id;
        /// <summary>CouponStatus 의 값. 유효기간이 반영되어 있다.</summary>
        public string status;
        public string issuedAt;
        public string expiresAt;
        public string redeemedAt;
        public PassCouponOfferDto offer;
        public PassMerchantDto merchant;
        public List<PassBranchDto> branches = new List<PassBranchDto>();
        /// <summary>지금 상점에 보여 줄 수 있는가.</summary>
        public bool usable;
    }

    [Serializable]
    public sealed class PassCouponsDto
    {
        public bool couponsEnabled;
        public List<PassCouponDto> coupons = new List<PassCouponDto>();
        public string notice;
    }

    [Serializable]
    public sealed class PassClaimDto
    {
        public PassCouponDto coupon;
    }

    /// <summary>
    /// 상점에 보여 줄 일회용 코드. 이것을 받았다고 쿠폰이 쓰인 것은 아니다:
    /// 쓰였는지는 상점이 확인 화면에서 처리했을 때 서버가 정한다.
    /// </summary>
    [Serializable]
    public sealed class PassPresentDto
    {
        public string couponId;
        /// <summary>"XXXXX-XXXXX"</summary>
        public string code;
        /// <summary>QR 에 넣을 글. 일회용 코드만 들어 있다 (회원번호·닉네임·쿠폰 번호 없음).</summary>
        public string qrPayload;
        public string expiresAt;
        public int expiresInSeconds;
    }

    [Serializable]
    public sealed class PassHistoryEntryDto
    {
        public string couponId;
        public string offerTitle;
        public string discountText;
        public string merchantName;
        public string branchName;
        public string redeemedAt;
    }

    [Serializable]
    public sealed class PassHistoryDto
    {
        public List<PassHistoryEntryDto> history = new List<PassHistoryEntryDto>();
    }

    // ── GUARDIANS SCHOLARSHIP ───────────────────────────────

    [Serializable]
    public sealed class ScholarshipRuleDto
    {
        public string id;
        public string type;
        public string description;
    }

    [Serializable]
    public sealed class ScholarshipCriterionDto
    {
        public string name;
        public string description;
        public int maxScore;
        public int weight;
    }

    /// <summary>
    /// 장학 프로그램. 목록에서는 요약만 채워져 오고, 하나를 열면 나머지도 온다 (detailed = true).
    /// 게임은 이것을 보여 주기만 한다. 신청은 홈페이지에서 한다.
    /// </summary>
    [Serializable]
    public sealed class ScholarshipProgramDto
    {
        public string id;
        public string category;
        public string categoryName;
        public string title;
        public string summary;
        public string supportType;
        public string supportTypeName;
        public string supportDetail;
        public int capacity;
        public string applyStartsAt;
        public string applyEndsAt;
        /// <summary>결과를 알리는 날 ("2027-01-15").</summary>
        public string resultOn;
        /// <summary>ScholarshipPhases 의 값.</summary>
        public string phase;

        public bool detailed;
        public string description;
        public List<ScholarshipRuleDto> eligibility = new List<ScholarshipRuleDto>();
        public List<ScholarshipCriterionDto> criteria = new List<ScholarshipCriterionDto>();
        public int guardianConsentAge;
        public int appealDays;
        /// <summary>신청하는 홈페이지 주소. 정해지지 않았으면 비어 있다.</summary>
        public string applyUrl;
        /// <summary>심사에 쓰지 않는 것의 안내 (게임 기록, 랭킹, 게임머니, 결제 내역).</summary>
        public string notUsedInReview;
    }

    [Serializable]
    public sealed class ScholarshipListDto
    {
        /// <summary>운영 승인이 끝나 신청을 받고 있는가. 아니면 programs 는 비어 있다.</summary>
        public bool applicationsEnabled;
        public List<ScholarshipProgramDto> programs = new List<ScholarshipProgramDto>();
        public string notice;
        public string siteUrl;
    }

    /// <summary>내 신청 한 건의 진행 상태. 신청서의 내용(개인정보)은 게임으로 오지 않는다.</summary>
    [Serializable]
    public sealed class ScholarshipApplicationDto
    {
        public string id;
        public string programId;
        public string programTitle;
        public string categoryName;
        public string status;
        public string statusText;
        public string submittedAt;
        public string eligibilityStatus;
        public bool isMinor;
        public string consentStatus;
        public string consentText;
        /// <summary>발표 전에는 비어 있다.</summary>
        public string result;
        public string resultText;
        public string resultNotifiedAt;
        public string disbursementStatus;
    }

    [Serializable]
    public sealed class ScholarshipApplicationsDto
    {
        public List<ScholarshipApplicationDto> applications = new List<ScholarshipApplicationDto>();
    }
}
