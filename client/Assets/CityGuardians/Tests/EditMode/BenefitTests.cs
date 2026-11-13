using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using CityGuardians.Domain;
using CityGuardians.Services;
using NUnit.Framework;

namespace CityGuardians.Tests
{
    /// <summary>현실 혜택 테스트의 공통 준비.</summary>
    internal static class BenefitKit
    {
        public static CityPassProfileDto Pass(LocalMockBackend backend)
        {
            ApiResult<CityPassProfileDto> result = null;
            backend.GetCityPass(r => result = r);
            return MockKit.Ok(result);
        }

        public static ApiResult<CityPassProfileDto> Join(LocalMockBackend backend, bool agree = true, string region = null)
        {
            ApiResult<CityPassProfileDto> result = null;
            backend.JoinCityPass(agree, region, r => result = r);
            return result;
        }

        public static ApiResult<CityPassProfileDto> SetRegion(LocalMockBackend backend, string region)
        {
            ApiResult<CityPassProfileDto> result = null;
            backend.SetCityPassRegion(region, r => result = r);
            return result;
        }

        public static ApiResult<PassOffersDto> Offers(LocalMockBackend backend, string region = null, string category = null)
        {
            ApiResult<PassOffersDto> result = null;
            backend.GetCityPassOffers(region, category, r => result = r);
            return result;
        }

        public static ApiResult<PassMerchantsDto> Merchants(LocalMockBackend backend, string region = null, string category = null)
        {
            ApiResult<PassMerchantsDto> result = null;
            backend.GetCityPassMerchants(region, category, r => result = r);
            return result;
        }

        public static PassCouponsDto Coupons(LocalMockBackend backend)
        {
            ApiResult<PassCouponsDto> result = null;
            backend.GetCityPassCoupons(r => result = r);
            return MockKit.Ok(result);
        }

        public static ApiResult<PassClaimDto> Claim(LocalMockBackend backend, string offerId, string requestId)
        {
            ApiResult<PassClaimDto> result = null;
            backend.ClaimCityPassCoupon(offerId, requestId, r => result = r);
            return result;
        }

        public static ApiResult<PassPresentDto> Present(LocalMockBackend backend, string couponId)
        {
            ApiResult<PassPresentDto> result = null;
            backend.PresentCityPassCoupon(couponId, r => result = r);
            return result;
        }

        public static PassHistoryDto History(LocalMockBackend backend)
        {
            ApiResult<PassHistoryDto> result = null;
            backend.GetCityPassHistory(r => result = r);
            return MockKit.Ok(result);
        }

        public static ApiResult<WebLoginCodeDto> WebCode(LocalMockBackend backend)
        {
            ApiResult<WebLoginCodeDto> result = null;
            backend.IssueWebLoginCode(r => result = r);
            return result;
        }

        public static ScholarshipListDto Scholarships(LocalMockBackend backend)
        {
            ApiResult<ScholarshipListDto> result = null;
            backend.GetScholarships(r => result = r);
            return MockKit.Ok(result);
        }

        public static ApiResult<ScholarshipProgramDto> Scholarship(LocalMockBackend backend, string id)
        {
            ApiResult<ScholarshipProgramDto> result = null;
            backend.GetScholarship(id, r => result = r);
            return result;
        }

        public static ScholarshipApplicationsDto Applications(LocalMockBackend backend)
        {
            ApiResult<ScholarshipApplicationsDto> result = null;
            backend.GetMyScholarshipApplications(r => result = r);
            return MockKit.Ok(result);
        }

        public static MeDto Resident(LocalMockBackend backend, string region = RegionCodes.Haeundae, string nickname = "혜택가디언")
        {
            ApiResult<ResidencyResultDto> result = null;
            backend.RegisterResidency(region, nickname, r => result = r);
            MockKit.Ok(result);
            return MockKit.Me(backend);
        }

        public static string Id() => Guid.NewGuid().ToString("N");
    }

    /// <summary>현실 혜택을 화면에 보여 줄 때의 규칙 (docs/REAL_WORLD_REWARDS_POLICY.md).</summary>
    public sealed class BenefitRulesTests
    {
        private static readonly DateTime Now = new DateTime(2026, 11, 13, 3, 0, 0, DateTimeKind.Utc);

        [Test]
        public void 받은_쿠폰은_유효기간이_지나면_만료다()
        {
            Assert.AreEqual(CouponStatus.Issued, BenefitRules.EffectiveStatus(CouponStatus.Issued, Now.AddSeconds(1), Now));
            Assert.AreEqual(CouponStatus.Expired, BenefitRules.EffectiveStatus(CouponStatus.Issued, Now, Now));
            Assert.AreEqual(CouponStatus.Expired, BenefitRules.EffectiveStatus(CouponStatus.Issued, Now.AddDays(-1), Now));
            // 이미 쓴 쿠폰은 기간이 지나도 쓴 쿠폰이다.
            Assert.AreEqual(CouponStatus.Redeemed, BenefitRules.EffectiveStatus(CouponStatus.Redeemed, Now.AddDays(-1), Now));
            Assert.AreEqual(CouponStatus.Cancelled, BenefitRules.EffectiveStatus(CouponStatus.Cancelled, Now.AddDays(1), Now));
        }

        [Test]
        public void 쿠폰을_받을_수_있는지는_가입과_수량만_따진다()
        {
            Assert.IsNull(BenefitRules.ClaimBlock(true, 0, 1, -1));
            Assert.IsNull(BenefitRules.ClaimBlock(true, 0, 1, 5));
            Assert.IsNull(BenefitRules.ClaimBlock(true, 1, 2, 1));
            Assert.AreEqual(ClaimBlocks.NotMember, BenefitRules.ClaimBlock(false, 0, 1, 5));
            Assert.AreEqual(ClaimBlocks.LimitReached, BenefitRules.ClaimBlock(true, 1, 1, 5));
            Assert.AreEqual(ClaimBlocks.SoldOut, BenefitRules.ClaimBlock(true, 0, 1, 0));
            // 여러 이유가 겹치면 가입, 내 한도, 남은 수의 순서로 알린다.
            Assert.AreEqual(ClaimBlocks.NotMember, BenefitRules.ClaimBlock(false, 3, 1, 0));
            Assert.AreEqual(ClaimBlocks.LimitReached, BenefitRules.ClaimBlock(true, 1, 1, 0));

            foreach (string block in new[] { ClaimBlocks.NotMember, ClaimBlocks.LimitReached, ClaimBlocks.SoldOut, "something_new" })
                Assert.IsNotEmpty(BenefitRules.ClaimBlockText(block));
            Assert.IsEmpty(BenefitRules.ClaimBlockText(null));
        }

        [Test]
        public void 규칙에는_게임_경제의_값을_받는_곳이_없다()
        {
            // 쿠폰과 장학은 GC, 전투 성적, 랭킹, 레벨과 이어지지 않는다. 그런 인자를 받는 함수가 생기면 여기서 걸린다.
            var banned = new Regex("^gc|Gc$|^xp$|balance|wallet|credit|loan|stock|level|rank|kill|battle|purchase|paid", RegexOptions.IgnoreCase);
            foreach (MethodInfo method in typeof(BenefitRules).GetMethods(BindingFlags.Public | BindingFlags.Static))
                foreach (ParameterInfo parameter in method.GetParameters())
                    Assert.IsFalse(banned.IsMatch(parameter.Name), $"BenefitRules.{method.Name}({parameter.Name})");
        }

        [Test]
        public void 할인_내용을_읽기_좋게_적는다()
        {
            Assert.AreEqual("10% 할인", BenefitRules.DiscountText("percent", 10));
            Assert.AreEqual("3,000원 할인", BenefitRules.DiscountText("amount", 3000));
            Assert.AreEqual("무료 체험", BenefitRules.DiscountText("free_trial", 0));
            Assert.AreEqual("사은품", BenefitRules.DiscountText("gift", 0));
            Assert.AreEqual("특별 혜택", BenefitRules.DiscountText("special", 0));
            Assert.AreEqual("특별 혜택", BenefitRules.DiscountText("unknown", 7));
        }

        [Test]
        public void 일회용_코드의_모양을_알아본다()
        {
            Assert.IsTrue(BenefitRules.LooksLikePresentCode("AB2CD-EF3GH"));
            Assert.IsFalse(BenefitRules.LooksLikePresentCode("AB2CDEF3GH"));
            Assert.IsFalse(BenefitRules.LooksLikePresentCode("ab2cd-ef3gh"));
            Assert.IsFalse(BenefitRules.LooksLikePresentCode("AB2CD-EF3G"));
            Assert.IsFalse(BenefitRules.LooksLikePresentCode("AB2CD-EF3G!"));
            Assert.IsFalse(BenefitRules.LooksLikePresentCode("AB2C-DEF3GH"));
            Assert.IsFalse(BenefitRules.LooksLikePresentCode(null));
            Assert.IsFalse(BenefitRules.LooksLikePresentCode(""));

            Assert.IsTrue(BenefitRules.LooksLikeWebCode("K7M2-P9XW"));
            Assert.IsFalse(BenefitRules.LooksLikeWebCode("K7M2P9XW"));
            Assert.IsFalse(BenefitRules.LooksLikeWebCode("K7M2-P9X"));
            Assert.IsFalse(BenefitRules.LooksLikeWebCode("k7m2-p9xw"));
            Assert.IsFalse(BenefitRules.LooksLikeWebCode(null));
        }

        [Test]
        public void QR에는_일회용_코드만_들어간다()
        {
            const string code = "AB2CD-EF3GH";
            const string plain = "CGPASS:AB2CD-EF3GH";
            const string memberNo = "CP-7KQ2-M9XA";
            const string nickname = "혜택가디언";

            // 서버가 보낸 글이 바르면 그대로 쓴다.
            Assert.AreEqual(plain, BenefitRules.SafeQrText(plain, code, memberNo, nickname));
            string address = "https://www.dalfin.net/cityguardians/merchant/redeem?code=" + code;
            Assert.AreEqual(address, BenefitRules.SafeQrText(address, code, memberNo, nickname));
            Assert.AreEqual("https://cg.test/merchant/redeem?code=" + code,
                BenefitRules.SafeQrText("https://cg.test/merchant/redeem?code=" + code, code, memberNo, "city"));
            // 비어 있으면 코드만 그린다.
            Assert.AreEqual(plain, BenefitRules.SafeQrText(null, code, memberNo, nickname));
            Assert.AreEqual(plain, BenefitRules.SafeQrText("", code, memberNo, nickname));

            // 어긋난 글은 버리고 코드만 그린다.
            string[] bad =
            {
                "https://cg.test/merchant/redeem?code=" + code + "&member=" + memberNo,      // 회원번호가 붙어 있다
                "https://cg.test/merchant/redeem?member=" + memberNo + "&code=" + code,
                "https://cg.test/" + memberNo + "/merchant/redeem?code=" + code,
                "https://cg.test/merchant/redeem?nick=x&code=" + code,                        // 다른 값이 붙어 있다
                "https://cg.test/merchant/redeem?code=" + code + "#" + memberNo,
                "https://cg.test/merchant/redeem?code=ZZZZZ-ZZZZZ",                           // 다른 코드
                "http://cg.test/merchant/redeem?code=" + code,                                // https 가 아니다
                "https://user@cg.test/merchant/redeem?code=" + code,
                "https://cg.test/혜택가디언/merchant/redeem?code=" + code,                    // 한글 (닉네임)
                "https://cg.test/merchant redeem?code=" + code,
                "CGPASS:" + code + ":" + memberNo,
                memberNo,
                "https://cg.test/" + new string('a', 220) + "?code=" + code,                  // QR 한 장에 들어가지 않는다
            };
            foreach (string payload in bad)
                Assert.AreEqual(plain, BenefitRules.SafeQrText(payload, code, memberNo, nickname), payload);

            // 영문 닉네임이 주소의 한 마디로 들어 있으면 쓰지 않는다.
            Assert.AreEqual(plain, BenefitRules.SafeQrText("https://cg.test/Guardian7/merchant/redeem?code=" + code, code, memberNo, "guardian7"));

            // 코드의 모양이 틀리면 그릴 것이 없다.
            Assert.IsNull(BenefitRules.SafeQrText(plain, "AB2CDEF3GH", memberNo, nickname));
            Assert.IsNull(BenefitRules.SafeQrText(plain, null, memberNo, nickname));

            // 정한 글은 언제나 QR 로 만들 수 있고, 회원번호와 닉네임이 없다.
            foreach (string payload in new[] { plain, address, bad[0], bad[8], null })
            {
                string text = BenefitRules.SafeQrText(payload, code, memberNo, nickname);
                Assert.IsTrue(QrCode.TryEncode(text, out QrCode qr));
                Assert.IsNotNull(qr);
                StringAssert.Contains(code, text);
                StringAssert.DoesNotContain(memberNo, text);
                StringAssert.DoesNotContain(nickname, text);
            }
        }

        [Test]
        public void 모집_단계는_공고의_상태와_날짜로만_정한다()
        {
            DateTime start = Now.AddDays(-1);
            DateTime end = Now.AddDays(10);
            Assert.AreEqual(ScholarshipPhases.Open, BenefitRules.Phase("published", start, end, Now));
            Assert.AreEqual(ScholarshipPhases.Upcoming, BenefitRules.Phase("published", Now.AddDays(1), end, Now));
            Assert.AreEqual(ScholarshipPhases.Closed, BenefitRules.Phase("published", start, Now, Now));
            Assert.AreEqual(ScholarshipPhases.Closed, BenefitRules.Phase("closed", start, end, Now));
            Assert.AreEqual(ScholarshipPhases.Announced, BenefitRules.Phase("announced", start, end, Now));
            foreach (string phase in new[] { ScholarshipPhases.Upcoming, ScholarshipPhases.Open, ScholarshipPhases.Closed, ScholarshipPhases.Announced })
                Assert.AreNotEqual("확인 중", BenefitRules.PhaseText(phase));
        }

        [Test]
        public void 날짜는_한국_시간으로_적는다()
        {
            // 협정 세계시 15:00 은 한국의 다음 날 0시다.
            Assert.AreEqual("2026-11-13", BenefitRules.DateText("2026-11-13T14:59:59Z"));
            Assert.AreEqual("2026-11-14", BenefitRules.DateText("2026-11-13T15:00:00Z"));
            Assert.AreEqual("2026-11-14 00:05", BenefitRules.DateTimeText("2026-11-13T15:05:00Z"));
            Assert.AreEqual("-", BenefitRules.DateText(null));
            Assert.AreEqual("-", BenefitRules.DateText(""));
            Assert.AreEqual("-", BenefitRules.DateText("내일"));

            Assert.IsTrue(BenefitRules.TryParseUtc("2026-11-13T03:00:00Z", out DateTime utc));
            Assert.AreEqual(Now, utc);
            Assert.AreEqual(DateTimeKind.Utc, utc.Kind);
            Assert.AreEqual("2026-11-13T03:00:00Z", BenefitRules.ToIso(Now));
            Assert.IsTrue(BenefitRules.TryParseUtc(BenefitRules.ToIso(Now), out DateTime again));
            Assert.AreEqual(Now, again);
            // 시간대가 붙어 온 글도 같은 순간으로 읽는다.
            Assert.IsTrue(BenefitRules.TryParseUtc("2026-11-13T12:00:00+09:00", out DateTime korea));
            Assert.AreEqual(Now, korea);
        }

        [Test]
        public void 남은_시간과_남은_날을_적는다()
        {
            Assert.AreEqual("5분 0초", BenefitRules.RemainingText(300));
            Assert.AreEqual("4분 59초", BenefitRules.RemainingText(299));
            Assert.AreEqual("59초", BenefitRules.RemainingText(59));
            Assert.AreEqual("0초", BenefitRules.RemainingText(0));
            Assert.AreEqual("0초", BenefitRules.RemainingText(-5));

            // 지금은 한국 시간 11월 13일 낮 12시다.
            Assert.AreEqual("오늘까지", BenefitRules.DaysLeftText(Now.AddHours(11), Now));
            Assert.AreEqual("1일 남음", BenefitRules.DaysLeftText(Now.AddHours(13), Now));
            Assert.AreEqual("30일 남음", BenefitRules.DaysLeftText(Now.AddDays(30), Now));
            Assert.AreEqual("기간 지남", BenefitRules.DaysLeftText(Now, Now));
            Assert.AreEqual("기간 지남", BenefitRules.DaysLeftText(Now.AddDays(-2), Now));
        }
    }

    /// <summary>
    /// 모의 서버의 현실 혜택: 닫혀 있는 것이 기본이고, 게임 경제와 이어지지 않는다
    /// (docs/REAL_WORLD_REWARDS_POLICY.md, docs/COUPON_SYSTEM.md, docs/SCHOLARSHIP_SYSTEM.md).
    /// </summary>
    public sealed class LocalMockBackendBenefitTests
    {
        private MockKit _kit;
        private LocalMockBackend _backend;

        [SetUp]
        public void SetUp()
        {
            _kit = new MockKit();
            _backend = _kit.NewBackend();
            MockKit.Boot(_backend);
        }

        // ── 게임이 할 수 없는 일 ────────────────────────────────

        [Test]
        public void 게임에는_쿠폰을_사거나_바꾸거나_장학금을_받는_요청이_없다()
        {
            var benefit = new Regex("CityPass|Scholarship|WebLogin");
            // 값을 치르거나, 사용 처리를 하거나, 신청·심사·지급을 하는 이름
            var bannedName = new Regex("Buy|Purchase|Exchange|Convert|Sell|Redeem|Use|Apply|Submit|Review|Award|Pay|Disburse|Grant|Transfer",
                RegexOptions.IgnoreCase);
            var bannedParameter = new Regex("gc|amount|price|balance|score|rank|level|kill", RegexOptions.IgnoreCase);
            int found = 0;
            foreach (MethodInfo method in typeof(IGameBackend).GetMethods())
            {
                if (!benefit.IsMatch(method.Name)) continue;
                found++;
                Assert.IsFalse(bannedName.IsMatch(method.Name), method.Name + ": 게임에 있으면 안 되는 요청");
                foreach (ParameterInfo parameter in method.GetParameters())
                    Assert.IsFalse(bannedParameter.IsMatch(parameter.Name), $"{method.Name}({parameter.Name})");
            }
            Assert.AreEqual(13, found, "현실 혜택 요청의 수가 바뀌었다. 새 요청이 위의 규칙을 지키는지 보고 이 수를 고친다.");
        }

        [Test]
        public void 응답에는_게임_경제의_값이_없다()
        {
            var banned = new Regex("^(gc|balance|wallet|xp|level|rank|kills?)$|Gc$|credit|loan|stock|battle", RegexOptions.IgnoreCase);
            Type[] dtos =
            {
                typeof(WebLoginCodeDto), typeof(CityPassProfileDto), typeof(CouponCountsDto), typeof(BenefitRegionDto),
                typeof(PassBranchDto), typeof(PassMerchantDto), typeof(PassMerchantsDto), typeof(PassOfferDto), typeof(PassOffersDto),
                typeof(PassCouponOfferDto), typeof(PassCouponDto), typeof(PassCouponsDto), typeof(PassClaimDto), typeof(PassPresentDto),
                typeof(PassHistoryEntryDto), typeof(PassHistoryDto), typeof(ScholarshipRuleDto), typeof(ScholarshipCriterionDto),
                typeof(ScholarshipProgramDto), typeof(ScholarshipListDto), typeof(ScholarshipApplicationDto), typeof(ScholarshipApplicationsDto),
            };
            foreach (Type dto in dtos)
                foreach (FieldInfo field in dto.GetFields())
                    Assert.IsFalse(banned.IsMatch(field.Name), $"{dto.Name}.{field.Name}");
            // 내 CITY PASS 에는 실제 이름·연락처·주소를 담을 칸이 없다.
            var personal = new Regex("realName|phone|email|address|birth", RegexOptions.IgnoreCase);
            foreach (FieldInfo field in typeof(CityPassProfileDto).GetFields())
                Assert.IsFalse(personal.IsMatch(field.Name), "CityPassProfileDto." + field.Name);
            // 신청 상태에는 신청서의 내용이 없다.
            var application = new Regex("^name$|applicant|realName|guardianName|school|phone|email|motivation|plan|document|birth", RegexOptions.IgnoreCase);
            foreach (FieldInfo field in typeof(ScholarshipApplicationDto).GetFields())
                Assert.IsFalse(application.IsMatch(field.Name), "ScholarshipApplicationDto." + field.Name);
        }

        // ── 닫혀 있는 것이 기본 ─────────────────────────────────

        [Test]
        public void 처음에는_모두_닫혀_있고_아무것도_보이지_않는다()
        {
            CityPassProfileDto pass = BenefitKit.Pass(_backend);
            Assert.IsFalse(pass.membershipEnabled);
            Assert.IsFalse(pass.couponsEnabled);
            Assert.IsFalse(pass.isMember);
            Assert.IsNull(pass.memberNo);
            Assert.IsNotEmpty(pass.notice);

            PassOffersDto offers = MockKit.Ok(BenefitKit.Offers(_backend));
            Assert.IsFalse(offers.couponsEnabled);
            Assert.IsEmpty(offers.offers);
            Assert.IsNotEmpty(offers.notice);
            Assert.IsEmpty(MockKit.Ok(BenefitKit.Merchants(_backend)).merchants);
            PassCouponsDto coupons = BenefitKit.Coupons(_backend);
            Assert.IsEmpty(coupons.coupons);
            Assert.IsNotEmpty(coupons.notice);
            Assert.IsEmpty(BenefitKit.History(_backend).history);

            ScholarshipListDto list = BenefitKit.Scholarships(_backend);
            Assert.IsFalse(list.applicationsEnabled);
            Assert.IsEmpty(list.programs);
            Assert.IsNotEmpty(list.notice);
            Assert.IsEmpty(BenefitKit.Applications(_backend).applications);

            Assert.AreEqual(ApiErrors.FeatureDisabled, MockKit.Error(BenefitKit.Join(_backend)));
            Assert.AreEqual(ApiErrors.FeatureDisabled, MockKit.Error(BenefitKit.Claim(_backend, "o_000001_aaaaaa", BenefitKit.Id())));
            Assert.AreEqual(ApiErrors.FeatureDisabled, MockKit.Error(BenefitKit.Present(_backend, "c_000001_aaaaaa")));
            Assert.AreEqual(ApiErrors.FeatureDisabled, MockKit.Error(BenefitKit.Scholarship(_backend, "p_000001_aaaaaa")));
            Assert.IsFalse(BenefitKit.Pass(_backend).isMember);
        }

        [Test]
        public void 견본이_있어도_기능을_닫으면_보이지_않는다()
        {
            string[] ids = _backend.DevEnableBenefitSamples();
            MockKit.Ok(BenefitKit.Join(_backend));
            Assert.IsNotEmpty(MockKit.Ok(BenefitKit.Offers(_backend)).offers);
            PassCouponDto coupon = MockKit.Ok(BenefitKit.Claim(_backend, ids[0], BenefitKit.Id())).coupon;
            Assert.IsTrue(coupon.usable);

            _backend.DevSetBenefitFeatures(false, false, false);
            Assert.IsEmpty(MockKit.Ok(BenefitKit.Offers(_backend)).offers);
            Assert.IsEmpty(MockKit.Ok(BenefitKit.Merchants(_backend)).merchants);
            Assert.IsEmpty(BenefitKit.Scholarships(_backend).programs);
            Assert.AreEqual(ApiErrors.FeatureDisabled, MockKit.Error(BenefitKit.Scholarship(_backend, ids[2])));
            Assert.AreEqual(ApiErrors.FeatureDisabled, MockKit.Error(BenefitKit.Claim(_backend, ids[1], BenefitKit.Id())));
            // 이미 받은 쿠폰은 목록에 남지만 쓸 수 없다.
            PassCouponsDto coupons = BenefitKit.Coupons(_backend);
            Assert.AreEqual(1, coupons.coupons.Count);
            Assert.IsFalse(coupons.coupons[0].usable);
            Assert.IsNotEmpty(coupons.notice);
            Assert.AreEqual(ApiErrors.FeatureDisabled, MockKit.Error(BenefitKit.Present(_backend, coupon.id)));
            Assert.IsFalse(_backend.DevRedeemAtMerchant("AAAAA-AAAAA", out string reason));
            Assert.AreEqual("feature_disabled", reason);
        }

        [Test]
        public void 견본_자료에는_시험용_표시가_붙어_있다()
        {
            _backend.DevEnableBenefitSamples();
            MockKit.Ok(BenefitKit.Join(_backend));
            foreach (PassMerchantDto merchant in MockKit.Ok(BenefitKit.Merchants(_backend)).merchants)
                StringAssert.StartsWith(LocalMockBackend.SampleMark, merchant.name);
            foreach (PassOfferDto offer in MockKit.Ok(BenefitKit.Offers(_backend)).offers)
            {
                StringAssert.StartsWith(LocalMockBackend.SampleMark, offer.title);
                StringAssert.StartsWith(LocalMockBackend.SampleMark, offer.merchant.name);
            }
            foreach (ScholarshipProgramDto program in BenefitKit.Scholarships(_backend).programs)
                StringAssert.StartsWith(LocalMockBackend.SampleMark, program.title);
            // 다시 불러도 견본이 두 번 들어가지 않는다.
            _backend.DevEnableBenefitSamples();
            Assert.AreEqual(2, MockKit.Ok(BenefitKit.Merchants(_backend)).merchants.Count);
            Assert.AreEqual(2, BenefitKit.Scholarships(_backend).programs.Count);
        }

        // ── 멤버십 ──────────────────────────────────────────────

        [Test]
        public void 가입은_약관에_동의해야_하고_무료다()
        {
            _backend.DevEnableBenefitSamples();
            _kit.Earn(_backend, 500);
            MeDto before = BenefitKit.Resident(_backend);
            int ledger = _backend.CurrentLedger.Count;

            Assert.AreEqual(ApiErrors.ValidationFailed, MockKit.Error(BenefitKit.Join(_backend, false)));
            Assert.IsFalse(BenefitKit.Pass(_backend).isMember);

            CityPassProfileDto pass = MockKit.Ok(BenefitKit.Join(_backend));
            Assert.IsTrue(pass.isMember);
            Assert.IsTrue(Regex.IsMatch(pass.memberNo, "^CP-[A-Z0-9]{4}-[A-Z0-9]{4}$"), pass.memberNo);
            Assert.AreEqual("active", pass.status);
            // 화면에는 게임 닉네임만 나온다.
            Assert.AreEqual("혜택가디언", pass.nickname);
            // 관심 지역의 기본값은 전입신고한 지역이다.
            Assert.AreEqual(RegionCodes.Haeundae, pass.interestRegion.code);
            Assert.AreEqual("해운대구", pass.interestRegion.name);
            Assert.AreEqual("부산광역시", pass.interestRegion.sidoName);
            Assert.AreEqual(0, pass.coupons.available);

            // 다시 가입해도 회원번호가 바뀌지 않는다.
            Assert.AreEqual(pass.memberNo, MockKit.Ok(BenefitKit.Join(_backend)).memberNo);

            // 가입은 지갑·통장·원장·경험치를 건드리지 않는다.
            MeDto after = MockKit.Me(_backend);
            Assert.AreEqual(before.gc, after.gc);
            Assert.AreEqual(before.bankGc, after.bankGc);
            Assert.AreEqual(before.xp, after.xp);
            Assert.AreEqual(before.level, after.level);
            Assert.AreEqual(ledger, _backend.CurrentLedger.Count);
            Assert.IsNull(_backend.FindLedgerInconsistency());
        }

        [Test]
        public void 관심_지역은_사는_곳과_달라도_된다()
        {
            _backend.DevEnableBenefitSamples();
            // 가입하기 전에는 바꿀 수 없다.
            Assert.AreEqual(ApiErrors.Forbidden, MockKit.Error(BenefitKit.SetRegion(_backend, "11110")));

            // 이방인(전입신고를 하지 않은 사람)도 가입할 수 있다. 관심 지역은 비어 있다.
            CityPassProfileDto pass = MockKit.Ok(BenefitKit.Join(_backend));
            Assert.IsTrue(pass.isMember);
            Assert.IsNull(pass.interestRegion);

            Assert.AreEqual("11110", MockKit.Ok(BenefitKit.SetRegion(_backend, "11110")).interestRegion.code);
            // 전입신고를 다른 곳에 해도 관심 지역은 그대로다 (거주지를 확인하지 않는다).
            BenefitKit.Resident(_backend, RegionCodes.Haeundae);
            Assert.AreEqual("11110", BenefitKit.Pass(_backend).interestRegion.code);

            Assert.AreEqual(ApiErrors.NotFound, MockKit.Error(BenefitKit.SetRegion(_backend, "99999")));
            Assert.AreEqual(ApiErrors.InvalidRequest, MockKit.Error(BenefitKit.SetRegion(_backend, "1' OR 1")));
            Assert.AreEqual(ApiErrors.InvalidRequest, MockKit.Error(BenefitKit.SetRegion(_backend, "")));
            Assert.AreEqual("11110", BenefitKit.Pass(_backend).interestRegion.code);
        }

        [Test]
        public void 가입할_때_관심_지역을_고를_수_있다()
        {
            _backend.DevEnableBenefitSamples();
            BenefitKit.Resident(_backend, RegionCodes.Haeundae);
            Assert.AreEqual(ApiErrors.NotFound, MockKit.Error(BenefitKit.Join(_backend, true, "99999")));
            Assert.IsFalse(BenefitKit.Pass(_backend).isMember);
            Assert.AreEqual("11110", MockKit.Ok(BenefitKit.Join(_backend, true, "11110")).interestRegion.code);
        }

        // ── 제휴 상점과 혜택 ────────────────────────────────────

        [Test]
        public void 승인된_상점과_혜택만_보인다()
        {
            _backend.DevEnableBenefitSamples();
            PassMerchantsDto merchants = MockKit.Ok(BenefitKit.Merchants(_backend));
            Assert.IsTrue(merchants.couponsEnabled);
            Assert.AreEqual(2, merchants.merchants.Count);
            foreach (PassMerchantDto merchant in merchants.merchants)
            {
                StringAssert.DoesNotContain("심사 중", merchant.name);
                Assert.IsNotEmpty(merchant.branches);
                Assert.IsNotEmpty(merchant.categoryName);
            }
            PassOffersDto offers = MockKit.Ok(BenefitKit.Offers(_backend));
            Assert.AreEqual(2, offers.offers.Count);
            foreach (PassOfferDto offer in offers.offers) StringAssert.DoesNotContain("승인 전", offer.title);

            // 지역과 업종으로 고른다. 다른 지역의 것도 볼 수 있다.
            Assert.AreEqual(2, MockKit.Ok(BenefitKit.Merchants(_backend, RegionCodes.Haeundae)).merchants.Count);
            PassMerchantsDto jongno = MockKit.Ok(BenefitKit.Merchants(_backend, "11110"));
            Assert.AreEqual(1, jongno.merchants.Count);
            Assert.AreEqual(1, jongno.merchants[0].branches.Count);
            Assert.AreEqual("종로점", jongno.merchants[0].branches[0].name);
            Assert.AreEqual("서울특별시 종로구", jongno.merchants[0].branches[0].regionName);
            Assert.IsEmpty(MockKit.Ok(BenefitKit.Merchants(_backend, "11140")).merchants);
            Assert.AreEqual(1, MockKit.Ok(BenefitKit.Merchants(_backend, null, "fitness")).merchants.Count);
            Assert.IsEmpty(MockKit.Ok(BenefitKit.Merchants(_backend, null, "culture")).merchants);
            Assert.AreEqual(1, MockKit.Ok(BenefitKit.Offers(_backend, "11110")).offers.Count);
            Assert.AreEqual(1, MockKit.Ok(BenefitKit.Offers(_backend, null, "cafe")).offers.Count);

            Assert.AreEqual(ApiErrors.InvalidRequest, MockKit.Error(BenefitKit.Offers(_backend, null, "casino")));
            Assert.AreEqual(ApiErrors.InvalidRequest, MockKit.Error(BenefitKit.Merchants(_backend, null, "casino")));
            Assert.AreEqual(ApiErrors.NotFound, MockKit.Error(BenefitKit.Offers(_backend, "99999")));
            Assert.AreEqual(ApiErrors.InvalidRequest, MockKit.Error(BenefitKit.Merchants(_backend, "abc")));
        }

        [Test]
        public void 가입하지_않으면_혜택을_볼_수는_있지만_받을_수는_없다()
        {
            string[] ids = _backend.DevEnableBenefitSamples();
            PassOffersDto offers = MockKit.Ok(BenefitKit.Offers(_backend));
            foreach (PassOfferDto offer in offers.offers)
            {
                Assert.IsFalse(offer.claimable);
                Assert.AreEqual(ClaimBlocks.NotMember, offer.claimBlock);
            }
            Assert.AreEqual(ApiErrors.Forbidden, MockKit.Error(BenefitKit.Claim(_backend, ids[0], BenefitKit.Id())));
            Assert.IsEmpty(BenefitKit.Coupons(_backend).coupons);
        }

        // ── 쿠폰 받기 ───────────────────────────────────────────

        [Test]
        public void 쿠폰은_GC와_상관없이_받는다()
        {
            string[] ids = _backend.DevEnableBenefitSamples();
            MockKit.Ok(BenefitKit.Join(_backend));
            // 지갑이 비어 있어도 받을 수 있다.
            Assert.AreEqual(0, MockKit.Me(_backend).gc);
            PassCouponDto empty = MockKit.Ok(BenefitKit.Claim(_backend, ids[0], BenefitKit.Id())).coupon;
            Assert.AreEqual(CouponStatus.Issued, empty.status);
            Assert.AreEqual(0, MockKit.Me(_backend).gc);
            Assert.AreEqual(0, _backend.CurrentLedger.Count);

            // GC 가 많아도 달라지는 것이 없다: 지갑도 원장도 그대로다.
            _kit.Earn(_backend, 1000);
            MeDto before = MockKit.Me(_backend);
            int ledger = _backend.CurrentLedger.Count;
            PassCouponDto second = MockKit.Ok(BenefitKit.Claim(_backend, ids[1], BenefitKit.Id())).coupon;
            Assert.AreEqual(CouponStatus.Issued, second.status);
            MeDto after = MockKit.Me(_backend);
            Assert.AreEqual(before.gc, after.gc);
            Assert.AreEqual(before.xp, after.xp);
            Assert.AreEqual(before.inventory.Count, after.inventory.Count);
            Assert.AreEqual(ledger, _backend.CurrentLedger.Count);
            Assert.IsNull(_backend.FindLedgerInconsistency());

            // 게임 경제의 저장 파일에는 혜택 자료가 섞이지 않는다.
            string server = _kit.Store.Read(LocalMockBackend.ServerFile);
            StringAssert.DoesNotContain(empty.id, server);
            StringAssert.DoesNotContain("memberNo", server);
            StringAssert.Contains(empty.id, _kit.Store.Read(LocalMockBackend.BenefitFile));
        }

        [Test]
        public void 받은_쿠폰에는_혜택과_상점과_유효기간이_있다()
        {
            string[] ids = _backend.DevEnableBenefitSamples();
            MockKit.Ok(BenefitKit.Join(_backend));
            PassCouponDto coupon = MockKit.Ok(BenefitKit.Claim(_backend, ids[0], BenefitKit.Id())).coupon;

            Assert.IsTrue(coupon.usable);
            Assert.IsNull(coupon.redeemedAt);
            StringAssert.Contains("10% 할인", coupon.offer.discountText);
            Assert.AreEqual("1회 1잔", coupon.offer.conditions);
            StringAssert.Contains("견본 카페", coupon.merchant.name);
            Assert.AreEqual(2, coupon.branches.Count);
            // 받은 날부터 30일.
            Assert.IsTrue(BenefitRules.TryParseUtc(coupon.expiresAt, out DateTime expires));
            Assert.AreEqual(_kit.Clock.UtcNow.AddDays(30), expires);

            CityPassProfileDto pass = BenefitKit.Pass(_backend);
            Assert.AreEqual(1, pass.coupons.available);
            Assert.AreEqual(0, pass.coupons.used);
            PassCouponsDto list = BenefitKit.Coupons(_backend);
            Assert.AreEqual(1, list.coupons.Count);
            Assert.AreEqual(coupon.id, list.coupons[0].id);

            // 혜택 목록에는 내가 받은 수가 나오고, 더 받을 수 없다고 나온다.
            PassOfferDto offer = MockKit.Ok(BenefitKit.Offers(_backend)).offers.Find(o => o.id == ids[0]);
            Assert.AreEqual(1, offer.myCoupons);
            Assert.IsFalse(offer.claimable);
            Assert.AreEqual(ClaimBlocks.LimitReached, offer.claimBlock);
            Assert.AreEqual(-1, offer.remaining);
        }

        [Test]
        public void 같은_요청을_다시_보내도_쿠폰은_한_장이다()
        {
            string[] ids = _backend.DevEnableBenefitSamples();
            MockKit.Ok(BenefitKit.Join(_backend));
            string request = BenefitKit.Id();
            PassCouponDto first = MockKit.Ok(BenefitKit.Claim(_backend, ids[0], request)).coupon;
            PassCouponDto again = MockKit.Ok(BenefitKit.Claim(_backend, ids[0], request)).coupon;
            Assert.AreEqual(first.id, again.id);
            Assert.AreEqual(1, BenefitKit.Coupons(_backend).coupons.Count);

            // 같은 요청 번호를 다른 혜택에 쓸 수 없다.
            Assert.AreEqual(ApiErrors.Conflict, MockKit.Error(BenefitKit.Claim(_backend, ids[1], request)));
            // 새 요청으로 같은 혜택을 또 받을 수 없다 (한 사람 한 장).
            Assert.AreEqual(ApiErrors.LimitReached, MockKit.Error(BenefitKit.Claim(_backend, ids[0], BenefitKit.Id())));
            Assert.AreEqual(1, BenefitKit.Coupons(_backend).coupons.Count);

            Assert.AreEqual(ApiErrors.InvalidRequest, MockKit.Error(BenefitKit.Claim(_backend, ids[1], "")));
            Assert.AreEqual(ApiErrors.InvalidRequest, MockKit.Error(BenefitKit.Claim(_backend, ids[1], null)));
            Assert.AreEqual(ApiErrors.InvalidRequest, MockKit.Error(BenefitKit.Claim(_backend, null, BenefitKit.Id())));
            Assert.AreEqual(ApiErrors.NotFound, MockKit.Error(BenefitKit.Claim(_backend, "o_999999_zzzzzz", BenefitKit.Id())));
        }

        [Test]
        public void 준비한_수가_다_나가면_받을_수_없다()
        {
            string[] ids = _backend.DevEnableBenefitSamples();
            MockKit.Ok(BenefitKit.Join(_backend));
            PassOfferDto limited = MockKit.Ok(BenefitKit.Offers(_backend)).offers.Find(o => o.id == ids[1]);
            Assert.AreEqual(1, limited.remaining);
            MockKit.Ok(BenefitKit.Claim(_backend, ids[1], BenefitKit.Id()));

            // 다른 플레이어
            ApiResult<MeDto> other = null;
            _backend.StartOverAsNewGuest(r => other = r);
            MockKit.Ok(other);
            MockKit.Ok(BenefitKit.Join(_backend));
            limited = MockKit.Ok(BenefitKit.Offers(_backend)).offers.Find(o => o.id == ids[1]);
            Assert.AreEqual(0, limited.remaining);
            Assert.IsFalse(limited.claimable);
            Assert.AreEqual(ClaimBlocks.SoldOut, limited.claimBlock);
            Assert.AreEqual(ApiErrors.LimitReached, MockKit.Error(BenefitKit.Claim(_backend, ids[1], BenefitKit.Id())));
            Assert.IsEmpty(BenefitKit.Coupons(_backend).coupons);
            // 수량 제한이 없는 혜택은 받을 수 있다.
            MockKit.Ok(BenefitKit.Claim(_backend, ids[0], BenefitKit.Id()));
        }

        // ── 쿠폰 보여 주기와 사용 ───────────────────────────────

        private PassCouponDto MemberWithCoupon(out string[] ids, int offer = 0)
        {
            ids = _backend.DevEnableBenefitSamples();
            BenefitKit.Resident(_backend);
            MockKit.Ok(BenefitKit.Join(_backend));
            return MockKit.Ok(BenefitKit.Claim(_backend, ids[offer], BenefitKit.Id())).coupon;
        }

        [Test]
        public void 보여_주기는_일회용_코드만_내주고_쿠폰을_쓰지_않는다()
        {
            PassCouponDto coupon = MemberWithCoupon(out _);
            CityPassProfileDto pass = BenefitKit.Pass(_backend);

            PassPresentDto present = MockKit.Ok(BenefitKit.Present(_backend, coupon.id));
            Assert.AreEqual(coupon.id, present.couponId);
            Assert.IsTrue(BenefitRules.LooksLikePresentCode(present.code), present.code);
            Assert.AreEqual(BenefitRules.PresentSeconds, present.expiresInSeconds);
            Assert.IsTrue(BenefitRules.TryParseUtc(present.expiresAt, out DateTime expires));
            Assert.AreEqual(_kit.Clock.UtcNow.AddSeconds(BenefitRules.PresentSeconds), expires);

            // QR 에 들어가는 글에는 일회용 코드뿐이다: 회원번호, 닉네임, 쿠폰 번호, 플레이어 번호가 없다.
            Assert.AreEqual(BenefitRules.QrPrefix + present.code, present.qrPayload);
            StringAssert.DoesNotContain(pass.memberNo, present.qrPayload);
            StringAssert.DoesNotContain(pass.nickname, present.qrPayload);
            StringAssert.DoesNotContain(coupon.id, present.qrPayload);
            StringAssert.DoesNotContain(MockKit.Me(_backend).playerId, present.qrPayload);
            Assert.AreEqual(present.qrPayload, BenefitRules.SafeQrText(present.qrPayload, present.code, pass.memberNo, pass.nickname));
            Assert.IsTrue(QrCode.TryEncode(present.qrPayload, out QrCode qr));
            Assert.AreEqual(2, qr.Version);

            // 코드를 받았다고 쿠폰이 쓰인 것은 아니다.
            PassCouponDto same = BenefitKit.Coupons(_backend).coupons[0];
            Assert.AreEqual(CouponStatus.Issued, same.status);
            Assert.IsTrue(same.usable);
            Assert.IsEmpty(BenefitKit.History(_backend).history);
            // 저장되는 것은 해시뿐이다.
            StringAssert.DoesNotContain(present.code, _kit.Store.Read(LocalMockBackend.BenefitFile));
            StringAssert.DoesNotContain(present.code.Replace("-", ""), _kit.Store.Read(LocalMockBackend.BenefitFile));
        }

        [Test]
        public void 상점이_확인하면_쿠폰은_한_번만_쓰인다()
        {
            PassCouponDto coupon = MemberWithCoupon(out _);
            _kit.Earn(_backend, 300);
            MeDto before = MockKit.Me(_backend);
            int ledger = _backend.CurrentLedger.Count;
            PassPresentDto present = MockKit.Ok(BenefitKit.Present(_backend, coupon.id));

            // 직원은 붙임표 없이, 소문자로 넣어도 된다.
            Assert.IsTrue(_backend.DevRedeemAtMerchant(present.code.Replace("-", "").ToLowerInvariant(), out string reason));
            Assert.IsNull(reason);
            // 같은 코드를 다시 넣으면 거절된다.
            Assert.IsFalse(_backend.DevRedeemAtMerchant(present.code, out reason));
            Assert.AreEqual("invalid_code", reason);

            PassCouponDto used = BenefitKit.Coupons(_backend).coupons[0];
            Assert.AreEqual(CouponStatus.Redeemed, used.status);
            Assert.IsFalse(used.usable);
            Assert.IsNotNull(used.redeemedAt);
            CityPassProfileDto pass = BenefitKit.Pass(_backend);
            Assert.AreEqual(0, pass.coupons.available);
            Assert.AreEqual(1, pass.coupons.used);

            PassHistoryDto history = BenefitKit.History(_backend);
            Assert.AreEqual(1, history.history.Count);
            Assert.AreEqual(coupon.id, history.history[0].couponId);
            StringAssert.Contains("견본 카페", history.history[0].merchantName);
            Assert.IsNotEmpty(history.history[0].branchName);
            Assert.AreEqual("10% 할인", history.history[0].discountText);

            // 쓴 쿠폰은 다시 보여 줄 수 없다.
            Assert.AreEqual(ApiErrors.Conflict, MockKit.Error(BenefitKit.Present(_backend, coupon.id)));
            // 쿠폰을 써도 GC 는 들어오지도 나가지도 않는다.
            MeDto after = MockKit.Me(_backend);
            Assert.AreEqual(before.gc, after.gc);
            Assert.AreEqual(before.xp, after.xp);
            Assert.AreEqual(ledger, _backend.CurrentLedger.Count);
        }

        [Test]
        public void 코드를_다시_받으면_앞의_코드는_못_쓴다()
        {
            PassCouponDto coupon = MemberWithCoupon(out _);
            PassPresentDto first = MockKit.Ok(BenefitKit.Present(_backend, coupon.id));
            _kit.Clock.Advance(10);
            PassPresentDto second = MockKit.Ok(BenefitKit.Present(_backend, coupon.id));
            Assert.AreNotEqual(first.code, second.code);

            Assert.IsFalse(_backend.DevRedeemAtMerchant(first.code, out string reason));
            Assert.AreEqual("invalid_code", reason);
            Assert.AreEqual(CouponStatus.Issued, BenefitKit.Coupons(_backend).coupons[0].status);
            Assert.IsTrue(_backend.DevRedeemAtMerchant(second.code, out _));
        }

        [Test]
        public void 코드는_5분이_지나면_못_쓰고_쿠폰은_그대로다()
        {
            PassCouponDto coupon = MemberWithCoupon(out _);
            PassPresentDto present = MockKit.Ok(BenefitKit.Present(_backend, coupon.id));

            _kit.Clock.Advance(BenefitRules.PresentSeconds);
            Assert.IsFalse(_backend.DevRedeemAtMerchant(present.code, out string reason));
            Assert.AreEqual("code_expired", reason);
            PassCouponDto same = BenefitKit.Coupons(_backend).coupons[0];
            Assert.AreEqual(CouponStatus.Issued, same.status);
            Assert.IsTrue(same.usable);

            // 새 코드를 받으면 쓸 수 있다.
            PassPresentDto fresh = MockKit.Ok(BenefitKit.Present(_backend, coupon.id));
            _kit.Clock.Advance(BenefitRules.PresentSeconds - 1);
            Assert.IsTrue(_backend.DevRedeemAtMerchant(fresh.code, out _));
        }

        [Test]
        public void 틀린_코드는_받아들이지_않는다()
        {
            PassCouponDto coupon = MemberWithCoupon(out _);
            MockKit.Ok(BenefitKit.Present(_backend, coupon.id));
            foreach (string code in new[] { null, "", "AAAAA-AAAAA", "AAAAA", coupon.id, "CGPASS:AAAAA-AAAAA" })
            {
                Assert.IsFalse(_backend.DevRedeemAtMerchant(code, out string reason), code);
                Assert.AreEqual("invalid_code", reason);
            }
            Assert.AreEqual(CouponStatus.Issued, BenefitKit.Coupons(_backend).coupons[0].status);
        }

        [Test]
        public void 유효기간이_지난_쿠폰은_보여_줄_수_없다()
        {
            // 두 번째 견본 혜택의 쿠폰은 7일 동안 쓸 수 있다.
            PassCouponDto coupon = MemberWithCoupon(out _, 1);
            PassPresentDto present = MockKit.Ok(BenefitKit.Present(_backend, coupon.id));

            _kit.Clock.AddDays(7);
            PassCouponDto late = BenefitKit.Coupons(_backend).coupons[0];
            Assert.AreEqual(CouponStatus.Expired, late.status);
            Assert.IsFalse(late.usable);
            Assert.AreEqual(ApiErrors.ValidationFailed, MockKit.Error(BenefitKit.Present(_backend, coupon.id)));
            Assert.IsFalse(_backend.DevRedeemAtMerchant(present.code, out string reason));
            Assert.AreEqual("expired", reason);
            CityPassProfileDto pass = BenefitKit.Pass(_backend);
            Assert.AreEqual(0, pass.coupons.available);
            Assert.AreEqual(1, pass.coupons.expired);
            Assert.IsEmpty(BenefitKit.History(_backend).history);
        }

        [Test]
        public void 코드는_쿠폰의_유효기간보다_오래_살지_않는다()
        {
            PassCouponDto coupon = MemberWithCoupon(out _, 1);
            _kit.Clock.AddDays(7);
            _kit.Clock.Advance(-100);   // 유효기간이 100초 남았다
            PassPresentDto present = MockKit.Ok(BenefitKit.Present(_backend, coupon.id));
            Assert.AreEqual(100, present.expiresInSeconds);
            Assert.AreEqual(coupon.expiresAt, present.expiresAt);
        }

        [Test]
        public void 남의_쿠폰은_없는_쿠폰이다()
        {
            PassCouponDto coupon = MemberWithCoupon(out _);
            ApiResult<MeDto> other = null;
            _backend.StartOverAsNewGuest(r => other = r);
            MockKit.Ok(other);
            MockKit.Ok(BenefitKit.Join(_backend));

            Assert.IsEmpty(BenefitKit.Coupons(_backend).coupons);
            Assert.AreEqual(ApiErrors.NotFound, MockKit.Error(BenefitKit.Present(_backend, coupon.id)));
            Assert.AreEqual(ApiErrors.NotFound, MockKit.Error(BenefitKit.Present(_backend, "c_999999_zzzzzz")));
            Assert.AreEqual(ApiErrors.InvalidRequest, MockKit.Error(BenefitKit.Present(_backend, "")));
            Assert.IsEmpty(BenefitKit.History(_backend).history);
        }

        [Test]
        public void 제휴가_끝난_상점의_쿠폰은_쓸_수_없다()
        {
            PassCouponDto coupon = MemberWithCoupon(out string[] ids);
            _backend.DevSetMerchantApproved(ids[0], false);

            PassCouponDto same = BenefitKit.Coupons(_backend).coupons[0];
            Assert.AreEqual(CouponStatus.Issued, same.status);
            Assert.IsFalse(same.usable);
            Assert.AreEqual(ApiErrors.ValidationFailed, MockKit.Error(BenefitKit.Present(_backend, coupon.id)));
            // 그 상점과 혜택은 목록에서도 사라진다.
            Assert.IsNull(MockKit.Ok(BenefitKit.Offers(_backend)).offers.Find(o => o.id == ids[0]));
            Assert.AreEqual(1, MockKit.Ok(BenefitKit.Merchants(_backend)).merchants.Count);
        }

        [Test]
        public void 코드를_너무_자주_받으면_잠시_막힌다()
        {
            PassCouponDto coupon = MemberWithCoupon(out _);
            for (int i = 0; i < 30; i++) MockKit.Ok(BenefitKit.Present(_backend, coupon.id));
            Assert.AreEqual(ApiErrors.RateLimited, MockKit.Error(BenefitKit.Present(_backend, coupon.id)));
            _kit.Clock.Advance(600);
            MockKit.Ok(BenefitKit.Present(_backend, coupon.id));
        }

        [Test]
        public void 껐다_켜도_멤버십과_쿠폰이_남아_있다()
        {
            PassCouponDto coupon = MemberWithCoupon(out _);
            string memberNo = BenefitKit.Pass(_backend).memberNo;
            PassPresentDto present = MockKit.Ok(BenefitKit.Present(_backend, coupon.id));

            LocalMockBackend restarted = _kit.NewBackend();
            MockKit.Boot(restarted);
            CityPassProfileDto pass = BenefitKit.Pass(restarted);
            Assert.IsTrue(pass.isMember);
            Assert.AreEqual(memberNo, pass.memberNo);
            Assert.AreEqual(RegionCodes.Haeundae, pass.interestRegion.code);
            PassCouponDto kept = BenefitKit.Coupons(restarted).coupons[0];
            Assert.AreEqual(coupon.id, kept.id);
            Assert.AreEqual(CouponStatus.Issued, kept.status);
            Assert.IsNull(kept.redeemedAt);
            Assert.IsTrue(kept.usable);
            // 껐다 켜기 전에 받은 코드도 5분 안이면 쓸 수 있다.
            Assert.IsTrue(restarted.DevRedeemAtMerchant(present.code, out _));
            Assert.AreEqual(CouponStatus.Redeemed, BenefitKit.Coupons(restarted).coupons[0].status);
        }

        // ── 홈페이지 로그인 코드 ────────────────────────────────

        [Test]
        public void 로그인_코드는_5분_동안_하나만_살아_있다()
        {
            WebLoginCodeDto first = MockKit.Ok(BenefitKit.WebCode(_backend));
            Assert.IsTrue(BenefitRules.LooksLikeWebCode(first.code), first.code);
            Assert.AreEqual(BenefitRules.WebCodeSeconds, first.expiresInSeconds);
            Assert.IsTrue(_backend.DevWebCodeIsLive(first.code));
            Assert.IsFalse(_backend.DevWebCodeIsLive("AAAA-AAAA"));
            // 저장되는 것은 해시뿐이다.
            StringAssert.DoesNotContain(first.code.Replace("-", ""), _kit.Store.Read(LocalMockBackend.BenefitFile));

            // 새로 받으면 앞의 코드는 죽는다.
            WebLoginCodeDto second = MockKit.Ok(BenefitKit.WebCode(_backend));
            Assert.AreNotEqual(first.code, second.code);
            Assert.IsFalse(_backend.DevWebCodeIsLive(first.code));
            Assert.IsTrue(_backend.DevWebCodeIsLive(second.code));

            _kit.Clock.Advance(BenefitRules.WebCodeSeconds - 1);
            Assert.IsTrue(_backend.DevWebCodeIsLive(second.code));
            _kit.Clock.Advance(1);
            Assert.IsFalse(_backend.DevWebCodeIsLive(second.code));
        }

        [Test]
        public void 로그인_코드를_너무_자주_받으면_잠시_막힌다()
        {
            for (int i = 0; i < 6; i++) MockKit.Ok(BenefitKit.WebCode(_backend));
            Assert.AreEqual(ApiErrors.RateLimited, MockKit.Error(BenefitKit.WebCode(_backend)));
            _kit.Clock.Advance(600);
            MockKit.Ok(BenefitKit.WebCode(_backend));
        }

        // ── 장학 안내 ───────────────────────────────────────────

        [Test]
        public void 장학_공고는_안내만_한다()
        {
            string[] ids = _backend.DevEnableBenefitSamples();
            ScholarshipListDto list = BenefitKit.Scholarships(_backend);
            Assert.IsTrue(list.applicationsEnabled);
            Assert.IsEmpty(list.notice);
            Assert.AreEqual(2, list.programs.Count);

            ScholarshipProgramDto open = list.programs.Find(p => p.id == ids[2]);
            ScholarshipProgramDto upcoming = list.programs.Find(p => p.id == ids[3]);
            Assert.AreEqual(ScholarshipPhases.Open, open.phase);
            Assert.AreEqual(ScholarshipPhases.Upcoming, upcoming.phase);
            Assert.AreEqual("금융 역량 장학", open.categoryName);
            Assert.AreEqual("도서·교재 지원", open.supportTypeName);
            // 목록에는 요약만 온다.
            Assert.IsFalse(open.detailed);
            Assert.IsEmpty(open.eligibility);

            ScholarshipProgramDto detail = MockKit.Ok(BenefitKit.Scholarship(_backend, ids[2]));
            Assert.IsTrue(detail.detailed);
            Assert.AreEqual(2, detail.eligibility.Count);
            Assert.AreEqual(2, detail.criteria.Count);
            Assert.AreEqual(19, detail.guardianConsentAge);
            Assert.AreEqual(7, detail.appealDays);
            // 심사에 쓰지 않는 것을 알린다.
            StringAssert.Contains("게임", detail.notUsedInReview);
            StringAssert.Contains("게임머니", detail.notUsedInReview);
            StringAssert.Contains("쓰이지 않아요", detail.notUsedInReview);
            // 지원 자격과 심사 기준에 게임 성적이 없다.
            var game = new Regex("GC|전투|랭킹|레벨|처치|결제");
            foreach (ScholarshipRuleDto rule in detail.eligibility) Assert.IsFalse(game.IsMatch(rule.description), rule.description);
            foreach (ScholarshipCriterionDto criterion in detail.criteria)
                Assert.IsFalse(game.IsMatch(criterion.name + criterion.description), criterion.name);

            Assert.AreEqual(ApiErrors.NotFound, MockKit.Error(BenefitKit.Scholarship(_backend, "p_999999_zzzzzz")));
            Assert.AreEqual(ApiErrors.NotFound, MockKit.Error(BenefitKit.Scholarship(_backend, null)));

            // 기간이 지나면 마감으로 보인다.
            _kit.Clock.AddDays(21);
            Assert.AreEqual(ScholarshipPhases.Closed, BenefitKit.Scholarships(_backend).programs.Find(p => p.id == ids[2]).phase);
        }

        [Test]
        public void 신청_상태는_볼_수만_있고_결과는_발표_뒤에_보인다()
        {
            string[] ids = _backend.DevEnableBenefitSamples();
            _kit.Earn(_backend, 1000);
            MeDto before = MockKit.Me(_backend);
            // 게임에서는 신청할 수 없다. 낸 신청이 없으면 비어 있다.
            Assert.IsEmpty(BenefitKit.Applications(_backend).applications);

            // 홈페이지에서 신청한 것을 흉내 낸다.
            _backend.DevAddScholarshipApplication(ids[2], "in_review", "selected");
            ScholarshipApplicationDto row = BenefitKit.Applications(_backend).applications[0];
            Assert.AreEqual(ids[2], row.programId);
            StringAssert.Contains("금융 역량 장학", row.programTitle);
            Assert.AreEqual("in_review", row.status);
            Assert.AreEqual("심사 중", row.statusText);
            // 발표 전에는 결과가 보이지 않는다 (정해져 있어도).
            Assert.IsTrue(string.IsNullOrEmpty(row.result));
            Assert.IsEmpty(row.resultText);
            Assert.IsTrue(string.IsNullOrEmpty(row.disbursementStatus));

            _backend.DevAddScholarshipApplication(ids[3], "announced", "selected");
            ScholarshipApplicationDto announced = BenefitKit.Applications(_backend).applications[0];
            Assert.AreEqual("결과 발표", announced.statusText);
            Assert.AreEqual("selected", announced.result);
            Assert.AreEqual("선정", announced.resultText);
            // 지급은 상태로만 보인다. 게임의 지갑에는 아무 일도 일어나지 않는다.
            Assert.AreEqual("pending", announced.disbursementStatus);
            MeDto after = MockKit.Me(_backend);
            Assert.AreEqual(before.gc, after.gc);
            Assert.AreEqual(before.bankGc, after.bankGc);
            Assert.AreEqual(before.xp, after.xp);
            Assert.IsNull(_backend.FindLedgerInconsistency());

            // 남의 신청은 보이지 않는다.
            ApiResult<MeDto> other = null;
            _backend.StartOverAsNewGuest(r => other = r);
            MockKit.Ok(other);
            Assert.IsEmpty(BenefitKit.Applications(_backend).applications);
        }

        [Test]
        public void 게임_성적이_달라도_혜택은_같다()
        {
            string[] ids = _backend.DevEnableBenefitSamples();
            MockKit.Ok(BenefitKit.Join(_backend));
            List<PassOfferDto> poor = MockKit.Ok(BenefitKit.Offers(_backend)).offers;
            List<ScholarshipProgramDto> programsBefore = BenefitKit.Scholarships(_backend).programs;

            // 사냥으로 GC 와 경험치를 많이 번 뒤에도 혜택과 공고는 똑같다.
            _kit.Earn(_backend, 5000);
            Assert.Greater(MockKit.Me(_backend).gc, 0);
            List<PassOfferDto> rich = MockKit.Ok(BenefitKit.Offers(_backend)).offers;
            Assert.AreEqual(poor.Count, rich.Count);
            for (int i = 0; i < poor.Count; i++)
            {
                Assert.AreEqual(poor[i].id, rich[i].id);
                Assert.AreEqual(poor[i].discountValue, rich[i].discountValue);
                Assert.AreEqual(poor[i].claimable, rich[i].claimable);
                Assert.AreEqual(poor[i].remaining, rich[i].remaining);
            }
            List<ScholarshipProgramDto> programsAfter = BenefitKit.Scholarships(_backend).programs;
            Assert.AreEqual(programsBefore.Count, programsAfter.Count);
            for (int i = 0; i < programsBefore.Count; i++) Assert.AreEqual(programsBefore[i].id, programsAfter[i].id);
            Assert.AreEqual(ids[0], rich.Find(o => o.id == ids[0]).id);
        }
    }
}
