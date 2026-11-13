using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using CityGuardians.Domain;
using UnityEngine;

namespace CityGuardians.Services
{
    // 현실 혜택 (v7.0): CITY PASS 와 GUARDIANS SCHOLARSHIP 의 모의 서버
    // (docs/REAL_WORLD_REWARDS_POLICY.md, docs/COUPON_SYSTEM.md, docs/SCHOLARSHIP_SYSTEM.md).
    //
    // 실제 서버(server/src/Service/CityPass.php, Scholarships.php)와 같은 규칙으로 답한다.
    //  - 처음에는 세 기능이 모두 닫혀 있다. 실제 서버에서 법률 검토와 운영 승인이 끝나기 전의 상태와 같다:
    //    조회는 "준비 중"으로, 가입·발급·제시는 FEATURE_DISABLED 로 돌아온다. 제휴 상점도 공고도 없다.
    //  - 이 파일은 플레이어의 지갑, 통장, 원장, 경험치, 장비를 읽지도 쓰지도 않는다 (PlayerRow 에서 쓰는 것은
    //    playerId, displayName, regionCode 뿐이다). 쿠폰은 값을 치르지 않고 받는다.
    //  - 쿠폰을 "사용됨"으로 바꾸는 요청은 IGameBackend 에 없다. 상점 확인 화면이 하는 일이고,
    //    여기서는 시험용 함수(DevRedeemAtMerchant)가 그 화면을 흉내 낸다.
    //  - 장학 신청·심사·지급은 게임에 없다. 공고와 내 신청의 진행 상태만 보여 준다.
    //
    // 자료는 mock_benefits.json 에 따로 둔다. 게임 경제의 자료(mock_server.json)와 섞지 않는다.
    public sealed partial class LocalMockBackend
    {
        public const string BenefitFile = "mock_benefits.json";
        /// <summary>시험용 견본 자료의 이름 앞에 붙는 표시. 실제 제휴 상점이나 실제 공고로 보이지 않게 한다.</summary>
        public const string SampleMark = "(시험용)";

        private const string PassTermsVersion = "2026-11-draft";
        private const int WebCodeLength = 8;
        private const int WebCodePer10Min = 6;
        private const int PresentCodeLength = 10;
        private const int PresentPer10Min = 30;
        private const string CodeAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

        private static readonly string[][] PassCategories =
        {
            new[] { "cafe", "카페" }, new[] { "restaurant", "음식점" }, new[] { "fitness", "헬스장" },
            new[] { "culture", "문화시설" }, new[] { "education", "교육시설" }, new[] { "retail", "판매점" },
            new[] { "service", "생활 서비스" },
        };

        private static readonly string[][] ScholarshipCategories =
        {
            new[] { "finance", "금융 역량 장학" }, new[] { "creativity", "창의력 장학" },
            new[] { "leadership", "협력·리더십 장학" }, new[] { "growth", "성장 지원 장학" },
        };

        private static readonly string[][] SupportTypes =
        {
            new[] { "education_fee", "교육비 지원" }, new[] { "books", "도서·교재 지원" },
            new[] { "online_pass", "온라인 교육 이용권" }, new[] { "project", "학습 프로젝트 지원" },
            new[] { "grant", "별도 장학금" },
        };

        private const string NotUsedInReview =
            "게임 플레이 기록, 전투 성적, 도시 랭킹, 게임머니, 유료 결제 내역은 자격 확인과 심사에 쓰이지 않아요.";

        [Serializable]
        private sealed class PassMemberRow
        {
            public string playerId;
            public string memberNo;
            public string interestRegionCode;
            public string status = "active";
            public string joinedAt;
        }

        [Serializable]
        private sealed class PassBranchRow
        {
            public string id;
            public string name;
            public string regionCode;
            public string address;
            public string phone;
            public bool hasLocation;
            public double lat;
            public double lng;
        }

        [Serializable]
        private sealed class PassMerchantRow
        {
            public string id;
            public string name;
            public string category;
            /// <summary>운영팀이 승인했는가. 승인되지 않은 상점은 어디에도 나오지 않는다.</summary>
            public bool approved;
            public List<PassBranchRow> branches = new List<PassBranchRow>();
        }

        [Serializable]
        private sealed class PassOfferRow
        {
            public string id;
            public string merchantId;
            /// <summary>이 지점에서만 쓸 수 있다. 비어 있으면 모든 지점.</summary>
            public string branchId;
            public string title;
            public string description;
            public string discountType;
            public int discountValue;
            public string conditions;
            public string issuePolicy = "membership";
            public string startsAt;
            public string endsAt;
            public int couponValidDays = 30;
            public int perMemberLimit = 1;
            /// <summary>준비한 수. 정해진 수가 없으면 -1.</summary>
            public int totalLimit = -1;
            public int issuedCount;
            public bool approved;
        }

        [Serializable]
        private sealed class PassCouponRow
        {
            public string id;
            public string offerId;
            public string merchantId;
            public string playerId;
            public string status;
            public string issuedAt;
            public string expiresAt;
            public string redeemedAt;
            public string redeemedBranchId;
            /// <summary>제시용 일회용 코드의 해시. 코드 자체는 남기지 않는다.</summary>
            public string presentHash;
            public string presentExpiresAt;
        }

        /// <summary>쿠폰 받기 요청 한 건 (중복 방지).</summary>
        [Serializable]
        private sealed class PassClaimRow
        {
            public string playerId;
            public string requestId;
            public string offerId;
            public string couponId;
        }

        [Serializable]
        private sealed class WebCodeRow
        {
            public string playerId;
            public string hash;
            public string expiresAt;
            public bool used;
        }

        [Serializable]
        private sealed class ProgramRow
        {
            public string id;
            public string category;
            public string title;
            public string summary;
            public string description;
            public string supportType;
            public string supportDetail;
            public int capacity;
            public string applyStartsAt;
            public string applyEndsAt;
            public string resultOn;
            /// <summary>published, closed, announced. 승인되지 않은 공고(draft)는 만들지 않는다.</summary>
            public string status = "published";
            public int guardianConsentAge = 19;
            public int appealDays = 7;
            public List<ScholarshipRuleDto> eligibility = new List<ScholarshipRuleDto>();
            public List<ScholarshipCriterionDto> criteria = new List<ScholarshipCriterionDto>();
        }

        [Serializable]
        private sealed class ApplicationRow
        {
            public string id;
            public string programId;
            public string playerId;
            public string status;
            public string submittedAt;
            public string eligibilityStatus = "pending";
            public bool isMinor;
            public string consentStatus = "not_required";
            public string result;
            public string resultNotifiedAt;
            public string disbursementStatus;
        }

        [Serializable]
        private sealed class BenefitDb
        {
            public int version = 1;
            // 운영 승인 스위치. 처음에는 모두 꺼져 있다.
            public bool membershipOn;
            public bool couponsOn;
            public bool scholarshipOn;
            public long seq = 1;
            public List<PassMemberRow> members = new List<PassMemberRow>();
            public List<PassMerchantRow> merchants = new List<PassMerchantRow>();
            public List<PassOfferRow> offers = new List<PassOfferRow>();
            public List<PassCouponRow> coupons = new List<PassCouponRow>();
            public List<PassClaimRow> claims = new List<PassClaimRow>();
            public List<WebCodeRow> webCodes = new List<WebCodeRow>();
            public List<ProgramRow> programs = new List<ProgramRow>();
            public List<ApplicationRow> applications = new List<ApplicationRow>();
        }

        private BenefitDb _benefits;
        private readonly Dictionary<string, List<DateTime>> _benefitHits = new Dictionary<string, List<DateTime>>();

        // ── 저장 ────────────────────────────────────────────────

        private BenefitDb Benefits
        {
            get
            {
                if (_benefits != null) return _benefits;
                string text = _store.Read(BenefitFile);
                BenefitDb parsed = string.IsNullOrEmpty(text) ? null : TryParse<BenefitDb>(text);
                if (!string.IsNullOrEmpty(text) && parsed == null)
                    throw new InvalidOperationException("모의 서버 데이터를 읽을 수 없어요: " + BenefitFile);
                _benefits = parsed ?? new BenefitDb();
                if (_benefits.members == null) _benefits.members = new List<PassMemberRow>();
                if (_benefits.merchants == null) _benefits.merchants = new List<PassMerchantRow>();
                if (_benefits.offers == null) _benefits.offers = new List<PassOfferRow>();
                if (_benefits.coupons == null) _benefits.coupons = new List<PassCouponRow>();
                if (_benefits.claims == null) _benefits.claims = new List<PassClaimRow>();
                if (_benefits.webCodes == null) _benefits.webCodes = new List<WebCodeRow>();
                if (_benefits.programs == null) _benefits.programs = new List<ProgramRow>();
                if (_benefits.applications == null) _benefits.applications = new List<ApplicationRow>();
                return _benefits;
            }
        }

        private void SaveBenefits() => _store.Write(BenefitFile, JsonUtility.ToJson(Benefits));

        private string NextBenefitId(string prefix) => prefix + (Benefits.seq++).ToString("D6") + "_" + RandomToken(6);

        private static string NameIn(string[][] table, string code)
        {
            for (int i = 0; i < table.Length; i++)
                if (table[i][0] == code) return table[i][1];
            return null;
        }

        private static DateTime UtcOf(string iso) =>
            BenefitRules.TryParseUtc(iso, out DateTime utc) ? utc : DateTime.MinValue;

        private static string CodeToken(int length)
        {
            byte[] bytes = new byte[length];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            var builder = new StringBuilder(length);
            for (int i = 0; i < length; i++) builder.Append(CodeAlphabet[bytes[i] % CodeAlphabet.Length]);
            return builder.ToString();
        }

        /// <summary>붙임표와 빈칸을 떼고 대문자로 맞춘다.</summary>
        private static string NormalizeCode(string code)
        {
            if (code == null) return "";
            var builder = new StringBuilder(code.Length);
            for (int i = 0; i < code.Length; i++)
            {
                char c = char.ToUpperInvariant(code[i]);
                if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) builder.Append(c);
            }
            return builder.ToString();
        }

        /// <summary>정해진 시간 안의 요청 수를 센다. 넘으면 false.</summary>
        private bool HitLimit(string bucket, int limit, int windowSeconds)
        {
            string key = bucket + ":" + _player.playerId;
            if (!_benefitHits.TryGetValue(key, out List<DateTime> hits))
            {
                hits = new List<DateTime>();
                _benefitHits[key] = hits;
            }
            DateTime now = _clock.UtcNow;
            hits.RemoveAll(t => (now - t).TotalSeconds >= windowSeconds);
            if (hits.Count >= limit) return false;
            hits.Add(now);
            return true;
        }

        // ── 홈페이지 로그인 코드 ────────────────────────────────

        public void IssueWebLoginCode(Action<ApiResult<WebLoginCodeDto>> done)
        {
            if (!RequirePlayer(done)) return;
            if (!HitLimit("web_code", WebCodePer10Min, 600))
            {
                done(ApiResult<WebLoginCodeDto>.Fail(ApiErrors.RateLimited, "코드를 너무 자주 받았어요. 잠시 뒤에 다시 받아 주세요."));
                return;
            }
            BenefitDb db = Benefits;
            DateTime now = _clock.UtcNow;
            // 살아 있는 코드는 하나뿐이다. 지난 코드는 지운다.
            db.webCodes.RemoveAll(c => c.playerId == _player.playerId || UtcOf(c.expiresAt) <= now);
            string code = CodeToken(WebCodeLength);
            DateTime expires = now.AddSeconds(BenefitRules.WebCodeSeconds);
            db.webCodes.Add(new WebCodeRow { playerId = _player.playerId, hash = Hash("cgweb:" + code), expiresAt = BenefitRules.ToIso(expires) });
            SaveBenefits();
            done(ApiResult<WebLoginCodeDto>.Success(new WebLoginCodeDto
            {
                code = code.Substring(0, 4) + "-" + code.Substring(4),
                expiresAt = BenefitRules.ToIso(expires),
                expiresInSeconds = BenefitRules.WebCodeSeconds,
                loginUrl = null,   // 모의 서버에는 홈페이지가 없다
            }));
        }

        // ── CITY PASS: 멤버십 ───────────────────────────────────

        private PassMemberRow MemberOf(string playerId) =>
            Benefits.members.Find(m => m.playerId == playerId && m.status == "active");

        private BenefitRegionDto RegionDtoOf(string regionCode)
        {
            RegionDef region = Regions.FindRegion(regionCode);
            if (region == null) return null;
            SidoDef sido = Regions.FindSido(region.sidoCode);
            return new BenefitRegionDto { code = region.code, name = region.name, sidoName = sido != null ? sido.name : "" };
        }

        /// <summary>지역 코드를 살핀다. 문제가 있으면 오류 응답을 보내고 false.</summary>
        private bool CheckRegion<T>(string regionCode, Action<ApiResult<T>> done)
        {
            bool digits = !string.IsNullOrEmpty(regionCode) && regionCode.Length <= 8;
            for (int i = 0; digits && i < regionCode.Length; i++) digits = regionCode[i] >= '0' && regionCode[i] <= '9';
            if (!digits)
            {
                done(ApiResult<T>.Fail(ApiErrors.InvalidRequest, "지역을 다시 골라 주세요."));
                return false;
            }
            if (Regions.FindRegion(regionCode) == null)
            {
                done(ApiResult<T>.Fail(ApiErrors.NotFound, "없는 지역이에요."));
                return false;
            }
            return true;
        }

        private CityPassProfileDto ToPassProfile(PlayerRow row)
        {
            BenefitDb db = Benefits;
            PassMemberRow member = MemberOf(row.playerId);
            var dto = new CityPassProfileDto
            {
                membershipEnabled = db.membershipOn,
                couponsEnabled = db.couponsOn,
                isMember = member != null,
                // 화면에는 게임 닉네임만 보인다. 실제 이름은 받지도, 저장하지도 않는다.
                nickname = row.displayName,
                termsVersion = PassTermsVersion,
                notice = db.membershipOn ? "" : "CITY PASS 는 준비 중이에요. 제휴와 운영 준비가 끝나면 열려요.",
            };
            if (member == null) return dto;

            dto.memberNo = member.memberNo;
            dto.status = member.status;
            dto.interestRegion = RegionDtoOf(member.interestRegionCode);
            dto.joinedAt = member.joinedAt;
            DateTime now = _clock.UtcNow;
            for (int i = 0; i < db.coupons.Count; i++)
            {
                PassCouponRow coupon = db.coupons[i];
                if (coupon.playerId != row.playerId) continue;
                string status = BenefitRules.EffectiveStatus(coupon.status, UtcOf(coupon.expiresAt), now);
                if (status == CouponStatus.Issued) dto.coupons.available++;
                else if (status == CouponStatus.Redeemed) dto.coupons.used++;
                else if (status == CouponStatus.Expired) dto.coupons.expired++;
            }
            return dto;
        }

        public void GetCityPass(Action<ApiResult<CityPassProfileDto>> done)
        {
            if (!RequirePlayer(done)) return;
            done(ApiResult<CityPassProfileDto>.Success(ToPassProfile(_player)));
        }

        public void JoinCityPass(bool agreeTerms, string interestRegionCode, Action<ApiResult<CityPassProfileDto>> done)
        {
            if (!RequirePlayer(done)) return;
            BenefitDb db = Benefits;
            if (!db.membershipOn)
            {
                done(ApiResult<CityPassProfileDto>.Fail(ApiErrors.FeatureDisabled, "CITY PASS 는 준비 중이에요."));
                return;
            }
            if (!agreeTerms)
            {
                done(ApiResult<CityPassProfileDto>.Fail(ApiErrors.ValidationFailed, "이용 약관에 동의해야 가입할 수 있어요."));
                return;
            }
            // 관심 지역의 기본값은 전입신고한 지역이다. 다른 지역을 골라도 된다 (실제 거주지를 확인하지 않는다).
            string regionCode = string.IsNullOrEmpty(interestRegionCode) ? _player.regionCode : interestRegionCode;
            if (!string.IsNullOrEmpty(interestRegionCode) && !CheckRegion(interestRegionCode, done)) return;

            if (MemberOf(_player.playerId) == null)
            {
                string token = CodeToken(8);
                db.members.Add(new PassMemberRow
                {
                    playerId = _player.playerId,
                    memberNo = "CP-" + token.Substring(0, 4) + "-" + token.Substring(4),
                    interestRegionCode = regionCode,
                    joinedAt = BenefitRules.ToIso(_clock.UtcNow),
                });
                SaveBenefits();
            }
            done(ApiResult<CityPassProfileDto>.Success(ToPassProfile(_player)));
        }

        public void SetCityPassRegion(string regionCode, Action<ApiResult<CityPassProfileDto>> done)
        {
            if (!RequirePlayer(done)) return;
            if (!CheckRegion(regionCode, done)) return;
            PassMemberRow member = MemberOf(_player.playerId);
            if (member == null)
            {
                done(ApiResult<CityPassProfileDto>.Fail(ApiErrors.Forbidden, "먼저 CITY PASS 에 가입해 주세요."));
                return;
            }
            member.interestRegionCode = regionCode;
            SaveBenefits();
            done(ApiResult<CityPassProfileDto>.Success(ToPassProfile(_player)));
        }

        // ── CITY PASS: 제휴 상점과 혜택 ─────────────────────────

        private PassBranchDto ToBranch(PassBranchRow row) => new PassBranchDto
        {
            id = row.id,
            name = row.name,
            regionCode = row.regionCode,
            regionName = Regions.FullName(row.regionCode) ?? "",
            address = row.address,
            phone = row.phone,
            hasLocation = row.hasLocation,
            lat = row.lat,
            lng = row.lng,
        };

        /// <summary>그 상점의 지점 가운데 조건에 맞는 것.</summary>
        private List<PassBranchDto> BranchesOf(PassMerchantRow merchant, string onlyBranchId, string regionCode)
        {
            var list = new List<PassBranchDto>();
            for (int i = 0; i < merchant.branches.Count; i++)
            {
                PassBranchRow branch = merchant.branches[i];
                if (!string.IsNullOrEmpty(onlyBranchId) && branch.id != onlyBranchId) continue;
                if (!string.IsNullOrEmpty(regionCode) && branch.regionCode != regionCode) continue;
                list.Add(ToBranch(branch));
            }
            return list;
        }

        private PassMerchantDto ToMerchant(PassMerchantRow row, List<PassBranchDto> branches) => new PassMerchantDto
        {
            id = row.id,
            name = row.name,
            category = row.category,
            categoryName = NameIn(PassCategories, row.category) ?? "",
            branches = branches ?? new List<PassBranchDto>(),
        };

        /// <summary>밖에 보여도 되는 혜택인가: 승인된 상점의 승인된 혜택이고, 기간 안이다.</summary>
        private bool OfferVisible(PassOfferRow offer, out PassMerchantRow merchant)
        {
            merchant = Benefits.merchants.Find(m => m.id == offer.merchantId);
            DateTime now = _clock.UtcNow;
            return merchant != null && merchant.approved && offer.approved
                && UtcOf(offer.startsAt) <= now && UtcOf(offer.endsAt) > now;
        }

        private int MyCouponsOf(string playerId, string offerId)
        {
            int count = 0;
            List<PassCouponRow> coupons = Benefits.coupons;
            for (int i = 0; i < coupons.Count; i++)
                if (coupons[i].playerId == playerId && coupons[i].offerId == offerId && coupons[i].status != CouponStatus.Cancelled) count++;
            return count;
        }

        private static int RemainingOf(PassOfferRow offer) =>
            offer.totalLimit < 0 ? -1 : Math.Max(0, offer.totalLimit - offer.issuedCount);

        private bool CheckCategory<T>(string category, Action<ApiResult<T>> done)
        {
            if (string.IsNullOrEmpty(category) || NameIn(PassCategories, category) != null) return true;
            done(ApiResult<T>.Fail(ApiErrors.InvalidRequest, "업종을 다시 골라 주세요."));
            return false;
        }

        public void GetCityPassOffers(string regionCode, string category, Action<ApiResult<PassOffersDto>> done)
        {
            if (!RequirePlayer(done)) return;
            BenefitDb db = Benefits;
            var dto = new PassOffersDto
            {
                couponsEnabled = db.couponsOn,
                regionCode = string.IsNullOrEmpty(regionCode) ? null : regionCode,
                notice = db.couponsOn ? "" : "제휴 혜택은 준비 중이에요.",
            };
            // 닫혀 있으면 아무것도 보이지 않는다 (준비되지 않은 혜택을 제공 중인 것처럼 보이지 않는다).
            if (!db.couponsOn)
            {
                done(ApiResult<PassOffersDto>.Success(dto));
                return;
            }
            if (!string.IsNullOrEmpty(regionCode) && !CheckRegion(regionCode, done)) return;
            if (!CheckCategory(category, done)) return;

            bool member = MemberOf(_player.playerId) != null;
            for (int i = 0; i < db.offers.Count; i++)
            {
                PassOfferRow offer = db.offers[i];
                if (!OfferVisible(offer, out PassMerchantRow merchant)) continue;
                if (!string.IsNullOrEmpty(category) && merchant.category != category) continue;
                List<PassBranchDto> where = BranchesOf(merchant, offer.branchId, regionCode);
                if (where.Count == 0) continue;   // 쓸 수 있는 지점이 없는 혜택은 보이지 않는다

                int mine = member ? MyCouponsOf(_player.playerId, offer.id) : 0;
                int remaining = RemainingOf(offer);
                string block = BenefitRules.ClaimBlock(member, mine, offer.perMemberLimit, remaining);
                dto.offers.Add(new PassOfferDto
                {
                    id = offer.id,
                    title = offer.title,
                    description = offer.description,
                    discountType = offer.discountType,
                    discountValue = offer.discountValue,
                    discountText = BenefitRules.DiscountText(offer.discountType, offer.discountValue),
                    conditions = offer.conditions,
                    issuePolicy = offer.issuePolicy,
                    startsAt = offer.startsAt,
                    endsAt = offer.endsAt,
                    couponValidDays = offer.couponValidDays,
                    perMemberLimit = offer.perMemberLimit,
                    remaining = remaining,
                    merchant = ToMerchant(merchant, null),
                    branches = where,
                    myCoupons = mine,
                    claimable = block == null,
                    claimBlock = block,
                });
            }
            done(ApiResult<PassOffersDto>.Success(dto));
        }

        public void GetCityPassMerchants(string regionCode, string category, Action<ApiResult<PassMerchantsDto>> done)
        {
            if (!RequirePlayer(done)) return;
            BenefitDb db = Benefits;
            var dto = new PassMerchantsDto { couponsEnabled = db.couponsOn };
            if (!db.couponsOn)
            {
                done(ApiResult<PassMerchantsDto>.Success(dto));
                return;
            }
            if (!string.IsNullOrEmpty(regionCode) && !CheckRegion(regionCode, done)) return;
            if (!CheckCategory(category, done)) return;

            for (int i = 0; i < db.merchants.Count; i++)
            {
                PassMerchantRow merchant = db.merchants[i];
                // 제휴(승인)하지 않은 상점은 제휴 상점으로 보이지 않는다.
                if (!merchant.approved) continue;
                if (!string.IsNullOrEmpty(category) && merchant.category != category) continue;
                List<PassBranchDto> where = BranchesOf(merchant, null, regionCode);
                if (where.Count > 0) dto.merchants.Add(ToMerchant(merchant, where));
            }
            done(ApiResult<PassMerchantsDto>.Success(dto));
        }

        // ── CITY PASS: 쿠폰 ─────────────────────────────────────

        private PassCouponDto ToCoupon(PassCouponRow row)
        {
            BenefitDb db = Benefits;
            PassOfferRow offer = db.offers.Find(o => o.id == row.offerId);
            PassMerchantRow merchant = db.merchants.Find(m => m.id == row.merchantId);
            string status = BenefitRules.EffectiveStatus(row.status, UtcOf(row.expiresAt), _clock.UtcNow);
            var dto = new PassCouponDto
            {
                id = row.id,
                status = status,
                issuedAt = row.issuedAt,
                expiresAt = row.expiresAt,
                redeemedAt = string.IsNullOrEmpty(row.redeemedAt) ? null : row.redeemedAt,
                // 지금 상점에 보여 줄 수 있는가
                usable = status == CouponStatus.Issued && merchant != null && merchant.approved && db.couponsOn,
            };
            if (offer != null)
            {
                dto.offer = new PassCouponOfferDto
                {
                    id = offer.id,
                    title = offer.title,
                    description = offer.description,
                    discountText = BenefitRules.DiscountText(offer.discountType, offer.discountValue),
                    conditions = offer.conditions,
                };
            }
            if (merchant != null)
            {
                dto.merchant = ToMerchant(merchant, null);
                dto.branches = BranchesOf(merchant, offer != null ? offer.branchId : null, null);
            }
            return dto;
        }

        /// <summary>내 쿠폰. 기능이 닫혀 있어도 이미 받은 쿠폰의 목록은 볼 수 있다 (쓸 수는 없다).</summary>
        public void GetCityPassCoupons(Action<ApiResult<PassCouponsDto>> done)
        {
            if (!RequirePlayer(done)) return;
            BenefitDb db = Benefits;
            var dto = new PassCouponsDto
            {
                couponsEnabled = db.couponsOn,
                notice = db.couponsOn ? "" : "쿠폰은 준비 중이에요. 지금은 쓸 수 없어요.",
            };
            for (int i = db.coupons.Count - 1; i >= 0; i--)
            {
                PassCouponRow coupon = db.coupons[i];
                if (coupon.playerId == _player.playerId && coupon.status != CouponStatus.Cancelled) dto.coupons.Add(ToCoupon(coupon));
            }
            done(ApiResult<PassCouponsDto>.Success(dto));
        }

        /// <summary>
        /// 혜택의 쿠폰을 받는다. 값은 치르지 않는다: 가입한 회원이면 받을 수 있다.
        /// 지갑과 원장은 읽지도 쓰지도 않는다.
        /// </summary>
        public void ClaimCityPassCoupon(string offerId, string requestId, Action<ApiResult<PassClaimDto>> done)
        {
            if (!RequirePlayer(done)) return;
            BenefitDb db = Benefits;
            if (!db.couponsOn)
            {
                done(ApiResult<PassClaimDto>.Fail(ApiErrors.FeatureDisabled, "제휴 혜택은 준비 중이에요."));
                return;
            }
            if (!IsValidRequestId(requestId) || string.IsNullOrEmpty(offerId))
            {
                done(ApiResult<PassClaimDto>.Fail(ApiErrors.InvalidRequest, "요청이 올바르지 않아요."));
                return;
            }
            // 같은 요청이 다시 오면 처음 응답을 돌려준다 (쿠폰이 두 장 생기지 않는다).
            PassClaimRow seen = db.claims.Find(c => c.playerId == _player.playerId && c.requestId == requestId);
            if (seen != null)
            {
                PassCouponRow again = db.coupons.Find(c => c.id == seen.couponId);
                if (seen.offerId != offerId || again == null)
                {
                    done(ApiResult<PassClaimDto>.Fail(ApiErrors.Conflict, "같은 요청 번호가 다른 요청에 쓰였어요."));
                    return;
                }
                done(ApiResult<PassClaimDto>.Success(new PassClaimDto { coupon = ToCoupon(again) }));
                return;
            }

            if (MemberOf(_player.playerId) == null)
            {
                done(ApiResult<PassClaimDto>.Fail(ApiErrors.Forbidden, "먼저 CITY PASS 에 가입해 주세요."));
                return;
            }
            PassOfferRow offer = db.offers.Find(o => o.id == offerId);
            if (offer == null || !OfferVisible(offer, out PassMerchantRow _))
            {
                done(ApiResult<PassClaimDto>.Fail(ApiErrors.NotFound, "지금은 받을 수 없는 혜택이에요."));
                return;
            }
            string block = BenefitRules.ClaimBlock(true, MyCouponsOf(_player.playerId, offer.id), offer.perMemberLimit, RemainingOf(offer));
            if (block != null)
            {
                done(ApiResult<PassClaimDto>.Fail(ApiErrors.LimitReached, BenefitRules.ClaimBlockText(block)));
                return;
            }

            DateTime now = _clock.UtcNow;
            DateTime expires = now.AddDays(offer.couponValidDays);
            DateTime offerEnds = UtcOf(offer.endsAt);
            if (offerEnds < expires) expires = offerEnds;
            var coupon = new PassCouponRow
            {
                id = NextBenefitId("c_"),
                offerId = offer.id,
                merchantId = offer.merchantId,
                playerId = _player.playerId,
                status = CouponStatus.Issued,
                issuedAt = BenefitRules.ToIso(now),
                expiresAt = BenefitRules.ToIso(expires),
            };
            db.coupons.Add(coupon);
            offer.issuedCount++;
            db.claims.Add(new PassClaimRow { playerId = _player.playerId, requestId = requestId, offerId = offer.id, couponId = coupon.id });
            SaveBenefits();
            done(ApiResult<PassClaimDto>.Success(new PassClaimDto { coupon = ToCoupon(coupon) }));
        }

        /// <summary>
        /// 쿠폰을 상점에 보여 줄 일회용 코드를 만든다. 쿠폰이 "사용됨"으로 바뀌는 것은 상점이 확인했을 때다.
        /// 코드는 5분 동안만 살아 있고, 다시 만들면 앞의 코드는 못 쓴다. 저장되는 것은 해시뿐이다.
        /// QR 에 들어가는 글에는 회원번호·닉네임·쿠폰 번호가 없다: 그 일회용 코드뿐이다.
        /// </summary>
        public void PresentCityPassCoupon(string couponId, Action<ApiResult<PassPresentDto>> done)
        {
            if (!RequirePlayer(done)) return;
            BenefitDb db = Benefits;
            if (!db.couponsOn)
            {
                done(ApiResult<PassPresentDto>.Fail(ApiErrors.FeatureDisabled, "쿠폰은 준비 중이에요. 지금은 쓸 수 없어요."));
                return;
            }
            if (string.IsNullOrEmpty(couponId))
            {
                done(ApiResult<PassPresentDto>.Fail(ApiErrors.InvalidRequest, "쿠폰을 다시 골라 주세요."));
                return;
            }
            if (!HitLimit("pass_present", PresentPer10Min, 600))
            {
                done(ApiResult<PassPresentDto>.Fail(ApiErrors.RateLimited, "코드를 너무 자주 받았어요. 잠시 뒤에 다시 시도해 주세요."));
                return;
            }
            // 남의 쿠폰은 없는 쿠폰과 똑같이 답한다.
            PassCouponRow coupon = db.coupons.Find(c => c.id == couponId && c.playerId == _player.playerId);
            if (coupon == null || coupon.status == CouponStatus.Cancelled)
            {
                done(ApiResult<PassPresentDto>.Fail(ApiErrors.NotFound, "없는 쿠폰이에요."));
                return;
            }
            DateTime now = _clock.UtcNow;
            DateTime couponEnds = UtcOf(coupon.expiresAt);
            string status = BenefitRules.EffectiveStatus(coupon.status, couponEnds, now);
            if (status == CouponStatus.Redeemed)
            {
                done(ApiResult<PassPresentDto>.Fail(ApiErrors.Conflict, "이미 쓴 쿠폰이에요."));
                return;
            }
            if (status == CouponStatus.Expired)
            {
                done(ApiResult<PassPresentDto>.Fail(ApiErrors.ValidationFailed, "유효기간이 지난 쿠폰이에요."));
                return;
            }
            PassMerchantRow merchant = db.merchants.Find(m => m.id == coupon.merchantId);
            if (merchant == null || !merchant.approved)
            {
                done(ApiResult<PassPresentDto>.Fail(ApiErrors.ValidationFailed, "지금은 이 상점에서 쓸 수 없어요."));
                return;
            }

            string code = CodeToken(PresentCodeLength);
            // 쿠폰의 유효기간보다 오래 살아 있지 않는다.
            DateTime expires = now.AddSeconds(BenefitRules.PresentSeconds);
            if (couponEnds < expires) expires = couponEnds;
            coupon.presentHash = Hash("cgpass:" + code);
            coupon.presentExpiresAt = BenefitRules.ToIso(expires);
            SaveBenefits();

            string shown = code.Substring(0, 5) + "-" + code.Substring(5);
            done(ApiResult<PassPresentDto>.Success(new PassPresentDto
            {
                couponId = coupon.id,
                code = shown,
                qrPayload = BenefitRules.QrPrefix + shown,   // 모의 서버에는 상점 확인 화면의 주소가 없다
                expiresAt = BenefitRules.ToIso(expires),
                expiresInSeconds = (int)Math.Round((expires - now).TotalSeconds),
            }));
        }

        public void GetCityPassHistory(Action<ApiResult<PassHistoryDto>> done)
        {
            if (!RequirePlayer(done)) return;
            BenefitDb db = Benefits;
            var dto = new PassHistoryDto();
            for (int i = db.coupons.Count - 1; i >= 0; i--)
            {
                PassCouponRow coupon = db.coupons[i];
                if (coupon.playerId != _player.playerId || coupon.status != CouponStatus.Redeemed) continue;
                PassOfferRow offer = db.offers.Find(o => o.id == coupon.offerId);
                PassMerchantRow merchant = db.merchants.Find(m => m.id == coupon.merchantId);
                PassBranchRow branch = merchant != null ? merchant.branches.Find(b => b.id == coupon.redeemedBranchId) : null;
                dto.history.Add(new PassHistoryEntryDto
                {
                    couponId = coupon.id,
                    offerTitle = offer != null ? offer.title : "",
                    discountText = offer != null ? BenefitRules.DiscountText(offer.discountType, offer.discountValue) : "",
                    merchantName = merchant != null ? merchant.name : "",
                    branchName = branch != null ? branch.name : "",
                    redeemedAt = coupon.redeemedAt,
                });
            }
            // 가장 최근에 쓴 것부터
            dto.history.Sort((a, b) => string.CompareOrdinal(b.redeemedAt, a.redeemedAt));
            done(ApiResult<PassHistoryDto>.Success(dto));
        }

        // ── GUARDIANS SCHOLARSHIP: 안내 ─────────────────────────

        private ScholarshipProgramDto ToProgram(ProgramRow row, bool detailed)
        {
            var dto = new ScholarshipProgramDto
            {
                id = row.id,
                category = row.category,
                categoryName = NameIn(ScholarshipCategories, row.category) ?? "",
                title = row.title,
                summary = row.summary,
                supportType = row.supportType,
                supportTypeName = NameIn(SupportTypes, row.supportType) ?? "",
                supportDetail = row.supportDetail,
                capacity = row.capacity,
                applyStartsAt = row.applyStartsAt,
                applyEndsAt = row.applyEndsAt,
                resultOn = row.resultOn,
                phase = BenefitRules.Phase(row.status, UtcOf(row.applyStartsAt), UtcOf(row.applyEndsAt), _clock.UtcNow),
            };
            if (!detailed) return dto;

            dto.detailed = true;
            dto.description = row.description;
            dto.eligibility = new List<ScholarshipRuleDto>(row.eligibility);
            dto.criteria = new List<ScholarshipCriterionDto>(row.criteria);
            dto.guardianConsentAge = row.guardianConsentAge;
            dto.appealDays = row.appealDays;
            dto.applyUrl = null;   // 모의 서버에는 홈페이지가 없다
            dto.notUsedInReview = NotUsedInReview;
            return dto;
        }

        public void GetScholarships(Action<ApiResult<ScholarshipListDto>> done)
        {
            if (!RequirePlayer(done)) return;
            BenefitDb db = Benefits;
            var dto = new ScholarshipListDto
            {
                applicationsEnabled = db.scholarshipOn,
                notice = db.scholarshipOn ? "" : "장학 프로그램은 준비 중이에요. 모집이 시작되면 여기에 공고가 올라와요.",
                siteUrl = null,
            };
            // 닫혀 있으면 공고가 있어도 보이지 않는다.
            if (db.scholarshipOn)
            {
                for (int i = db.programs.Count - 1; i >= 0; i--) dto.programs.Add(ToProgram(db.programs[i], false));
                dto.programs.Sort((a, b) => string.CompareOrdinal(b.applyEndsAt, a.applyEndsAt));
            }
            done(ApiResult<ScholarshipListDto>.Success(dto));
        }

        public void GetScholarship(string programId, Action<ApiResult<ScholarshipProgramDto>> done)
        {
            if (!RequirePlayer(done)) return;
            BenefitDb db = Benefits;
            if (!db.scholarshipOn)
            {
                done(ApiResult<ScholarshipProgramDto>.Fail(ApiErrors.FeatureDisabled, "장학 프로그램은 준비 중이에요."));
                return;
            }
            ProgramRow row = string.IsNullOrEmpty(programId) ? null : db.programs.Find(p => p.id == programId);
            if (row == null)
            {
                done(ApiResult<ScholarshipProgramDto>.Fail(ApiErrors.NotFound, "없는 장학 프로그램이에요."));
                return;
            }
            done(ApiResult<ScholarshipProgramDto>.Success(ToProgram(row, true)));
        }

        private static string ApplicationStatusText(string status)
        {
            switch (status)
            {
                case "submitted": return "접수됨";
                case "ineligible": return "지원 자격에 해당하지 않음";
                case "in_review": return "심사 중";
                case "decided": return "심사 완료 (발표 전)";
                case "announced": return "결과 발표";
                case "withdrawn": return "신청 철회";
                default: return status ?? "";
            }
        }

        private static string ConsentText(string status)
        {
            switch (status)
            {
                case "pending": return "보호자 동의를 기다리는 중";
                case "guardian_confirmed": return "보호자가 동의함 (담당자 확인 전)";
                case "verified": return "보호자 동의 확인됨";
                default: return "필요 없음";
            }
        }

        private static string ResultText(string result)
        {
            switch (result)
            {
                case "selected": return "선정";
                case "waitlisted": return "예비 선정";
                case "not_selected": return "선정되지 않음";
                default: return "";
            }
        }

        /// <summary>내 신청의 진행 상태. 신청은 홈페이지에서 하므로, 모의 서버에서는 시험용 함수로 만든 것만 있다.</summary>
        public void GetMyScholarshipApplications(Action<ApiResult<ScholarshipApplicationsDto>> done)
        {
            if (!RequirePlayer(done)) return;
            BenefitDb db = Benefits;
            var dto = new ScholarshipApplicationsDto();
            for (int i = db.applications.Count - 1; i >= 0; i--)
            {
                ApplicationRow row = db.applications[i];
                if (row.playerId != _player.playerId) continue;
                ProgramRow program = db.programs.Find(p => p.id == row.programId);
                // 결과는 발표한 뒤에만 보인다.
                bool announced = row.status == "announced";
                dto.applications.Add(new ScholarshipApplicationDto
                {
                    id = row.id,
                    programId = row.programId,
                    programTitle = program != null ? program.title : "",
                    categoryName = program != null ? NameIn(ScholarshipCategories, program.category) ?? "" : "",
                    status = row.status,
                    statusText = ApplicationStatusText(row.status),
                    submittedAt = row.submittedAt,
                    eligibilityStatus = row.eligibilityStatus,
                    isMinor = row.isMinor,
                    consentStatus = row.consentStatus,
                    consentText = ConsentText(row.consentStatus),
                    result = announced ? row.result : null,
                    resultText = announced ? ResultText(row.result) : "",
                    resultNotifiedAt = announced ? row.resultNotifiedAt : null,
                    disbursementStatus = announced ? row.disbursementStatus : null,
                });
            }
            done(ApiResult<ScholarshipApplicationsDto>.Success(dto));
        }

        // ── 시험용 ──────────────────────────────────────────────
        //
        // 아래는 IGameBackend 에 없다. 게임 화면에서는 부를 수 없고, 자동 테스트와 개발 중의 화면 확인에만 쓴다.
        // 실제 서버에서는 관리자 화면(운영 승인, 제휴 상점·혜택·공고 등록)과 상점 확인 화면이 하는 일이다.

        /// <summary>운영 승인 스위치를 바꾼다 (실제 서버에서는 법률 검토 기록과 최고 관리자의 승인이 있어야 켜진다).</summary>
        public void DevSetBenefitFeatures(bool membership, bool coupons, bool scholarship)
        {
            BenefitDb db = Benefits;
            db.membershipOn = membership;
            db.couponsOn = coupons;
            db.scholarshipOn = scholarship;
            SaveBenefits();
        }

        /// <summary>
        /// 세 기능을 열고 시험용 견본 자료를 넣는다. 견본의 이름에는 모두 "(시험용)"이 붙는다:
        /// 실제 제휴 상점이나 실제 장학 공고가 아니다. 이미 넣었으면 다시 넣지 않는다.
        /// </summary>
        /// <returns>견본 혜택과 공고의 번호: [0] 수량 제한이 없는 혜택, [1] 한 장만 준비한 혜택, [2] 모집 중인 공고, [3] 모집 예정인 공고.</returns>
        public string[] DevEnableBenefitSamples()
        {
            BenefitDb db = Benefits;
            db.membershipOn = true;
            db.couponsOn = true;
            db.scholarshipOn = true;
            if (db.merchants.Count == 0 && db.programs.Count == 0)
            {
                DateTime now = _clock.UtcNow;
                string from = BenefitRules.ToIso(now.AddDays(-1));
                string until = BenefitRules.ToIso(now.AddDays(60));

                var cafe = new PassMerchantRow { id = NextBenefitId("m_"), name = SampleMark + " 견본 카페", category = "cafe", approved = true };
                cafe.branches.Add(new PassBranchRow
                {
                    id = NextBenefitId("b_"), name = "해운대점", regionCode = RegionCodes.Haeundae,
                    address = SampleMark + " 주소 없음", phone = "", hasLocation = false,
                });
                cafe.branches.Add(new PassBranchRow
                {
                    id = NextBenefitId("b_"), name = "종로점", regionCode = "11110",
                    address = SampleMark + " 주소 없음", phone = "", hasLocation = false,
                });
                var gym = new PassMerchantRow { id = NextBenefitId("m_"), name = SampleMark + " 견본 체육관", category = "fitness", approved = true };
                gym.branches.Add(new PassBranchRow
                {
                    id = NextBenefitId("b_"), name = "해운대점", regionCode = RegionCodes.Haeundae,
                    address = SampleMark + " 주소 없음", phone = "", hasLocation = false,
                });
                // 승인되지 않은 상점: 어디에도 나오면 안 된다.
                var pending = new PassMerchantRow { id = NextBenefitId("m_"), name = SampleMark + " 심사 중인 상점", category = "retail", approved = false };
                pending.branches.Add(new PassBranchRow { id = NextBenefitId("b_"), name = "본점", regionCode = RegionCodes.Haeundae, address = "", phone = "" });
                db.merchants.Add(cafe);
                db.merchants.Add(gym);
                db.merchants.Add(pending);

                db.offers.Add(new PassOfferRow
                {
                    id = NextBenefitId("o_"), merchantId = cafe.id, title = SampleMark + " 음료 10% 할인",
                    description = "화면을 확인하기 위한 견본 혜택이에요. 실제로 쓸 수 없어요.", discountType = "percent", discountValue = 10,
                    conditions = "1회 1잔", issuePolicy = "membership", startsAt = from, endsAt = until, approved = true,
                });
                db.offers.Add(new PassOfferRow
                {
                    id = NextBenefitId("o_"), merchantId = gym.id, title = SampleMark + " 1일 무료 체험",
                    description = "화면을 확인하기 위한 견본 혜택이에요. 실제로 쓸 수 없어요.", discountType = "free_trial",
                    conditions = "첫 방문 1회", issuePolicy = "promotion", startsAt = from, endsAt = until, couponValidDays = 7,
                    totalLimit = 1, approved = true,
                });
                db.offers.Add(new PassOfferRow
                {
                    id = NextBenefitId("o_"), merchantId = pending.id, title = SampleMark + " 승인 전 혜택",
                    discountType = "gift", startsAt = from, endsAt = until, approved = true,
                });

                var open = new ProgramRow
                {
                    id = NextBenefitId("p_"), category = "finance", title = SampleMark + " 금융 역량 장학 견본 공고",
                    summary = "화면을 확인하기 위한 견본 공고예요. 실제 모집이 아니에요.",
                    description = "금융교육 과정을 마치고 학습 계획서를 낸 학생을 지원하는 프로그램의 견본이에요.",
                    supportType = "books", supportDetail = "도서·교재 지원", capacity = 10,
                    applyStartsAt = from, applyEndsAt = BenefitRules.ToIso(now.AddDays(20)),
                    resultOn = BenefitRules.DateText(BenefitRules.ToIso(now.AddDays(40))),
                };
                open.eligibility.Add(new ScholarshipRuleDto { id = "r1", type = "school", description = "초·중·고등학생 또는 학교 밖 청소년" });
                open.eligibility.Add(new ScholarshipRuleDto { id = "r2", type = "education", description = "금융교육 과정 수료" });
                open.criteria.Add(new ScholarshipCriterionDto { name = "학습 계획", description = "계획의 구체성과 실현 가능성", maxScore = 10, weight = 2 });
                open.criteria.Add(new ScholarshipCriterionDto { name = "지원 동기", description = "배우고 싶은 것과 이유", maxScore = 10, weight = 1 });
                var upcoming = new ProgramRow
                {
                    id = NextBenefitId("p_"), category = "creativity", title = SampleMark + " 창의력 장학 견본 공고",
                    summary = "화면을 확인하기 위한 견본 공고예요. 실제 모집이 아니에요.",
                    description = "창작 활동 계획을 낸 학생을 지원하는 프로그램의 견본이에요.",
                    supportType = "project", supportDetail = "학습 프로젝트 지원", capacity = 5,
                    applyStartsAt = BenefitRules.ToIso(now.AddDays(30)), applyEndsAt = BenefitRules.ToIso(now.AddDays(50)),
                    resultOn = BenefitRules.DateText(BenefitRules.ToIso(now.AddDays(70))),
                };
                upcoming.eligibility.Add(new ScholarshipRuleDto { id = "r1", type = "school", description = "초·중·고등학생 또는 학교 밖 청소년" });
                upcoming.criteria.Add(new ScholarshipCriterionDto { name = "창작 계획", description = "아이디어와 계획", maxScore = 10, weight = 1 });
                db.programs.Add(open);
                db.programs.Add(upcoming);
            }
            SaveBenefits();
            return new[] { db.offers[0].id, db.offers[1].id, db.programs[0].id, db.programs[1].id };
        }

        /// <summary>
        /// 상점 확인 화면을 흉내 낸다: 직원이 코드를 넣고 사용 처리를 누른 것과 같다.
        /// 살아 있는 코드이고 쿠폰이 아직 쓰이지 않았을 때만, 한 번만 받아들인다.
        /// </summary>
        /// <param name="reason">거절한 이유: invalid_code, code_expired, already_redeemed, expired, feature_disabled.</param>
        public bool DevRedeemAtMerchant(string code, out string reason)
        {
            BenefitDb db = Benefits;
            reason = null;
            if (!db.couponsOn)
            {
                reason = "feature_disabled";
                return false;
            }
            string normalized = NormalizeCode(code);
            string hash = normalized.Length == PresentCodeLength ? Hash("cgpass:" + normalized) : null;
            PassCouponRow coupon = hash == null ? null : db.coupons.Find(c => c.presentHash == hash);
            if (coupon == null)
            {
                reason = "invalid_code";
                return false;
            }
            DateTime now = _clock.UtcNow;
            string status = BenefitRules.EffectiveStatus(coupon.status, UtcOf(coupon.expiresAt), now);
            if (status == CouponStatus.Redeemed) reason = "already_redeemed";
            else if (status != CouponStatus.Issued) reason = "expired";
            else if (UtcOf(coupon.presentExpiresAt) <= now) reason = "code_expired";
            if (reason != null) return false;

            PassMerchantRow merchant = db.merchants.Find(m => m.id == coupon.merchantId);
            coupon.status = CouponStatus.Redeemed;
            coupon.redeemedAt = BenefitRules.ToIso(now);
            coupon.redeemedBranchId = merchant != null && merchant.branches.Count > 0 ? merchant.branches[0].id : null;
            // 쓴 코드는 지운다. 같은 코드로 다시 올 수 없다.
            coupon.presentHash = null;
            coupon.presentExpiresAt = null;
            SaveBenefits();
            return true;
        }

        /// <summary>그 로그인 코드가 지금 살아 있는가 (홈페이지가 코드를 확인하는 것을 흉내 낸다. 쓰지는 않는다).</summary>
        public bool DevWebCodeIsLive(string code)
        {
            string hash = Hash("cgweb:" + NormalizeCode(code));
            DateTime now = _clock.UtcNow;
            return Benefits.webCodes.Exists(c => c.hash == hash && !c.used && UtcOf(c.expiresAt) > now);
        }

        /// <summary>홈페이지에서 신청서를 낸 것을 흉내 낸다 (진행 상태 화면을 확인하는 데 쓴다).</summary>
        public string DevAddScholarshipApplication(string programId, string status, string result = null)
        {
            if (_player == null) throw new InvalidOperationException("먼저 접속해야 합니다.");
            BenefitDb db = Benefits;
            if (!db.programs.Exists(p => p.id == programId)) throw new ArgumentException("없는 장학 프로그램입니다.", nameof(programId));
            var row = new ApplicationRow
            {
                id = NextBenefitId("a_"),
                programId = programId,
                playerId = _player.playerId,
                status = status,
                submittedAt = BenefitRules.ToIso(_clock.UtcNow),
                result = result,
                resultNotifiedAt = status == "announced" ? BenefitRules.ToIso(_clock.UtcNow) : null,
                disbursementStatus = status == "announced" && result == "selected" ? "pending" : null,
            };
            db.applications.Add(row);
            SaveBenefits();
            return row.id;
        }

        /// <summary>견본 혜택의 승인을 거둔다 (제휴가 끝난 상점을 흉내 낸다).</summary>
        public void DevSetMerchantApproved(string offerId, bool approved)
        {
            BenefitDb db = Benefits;
            PassOfferRow offer = db.offers.Find(o => o.id == offerId);
            PassMerchantRow merchant = offer != null ? db.merchants.Find(m => m.id == offer.merchantId) : null;
            if (merchant == null) throw new ArgumentException("없는 혜택입니다.", nameof(offerId));
            merchant.approved = approved;
            SaveBenefits();
        }
    }
}
