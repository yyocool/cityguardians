using System.Collections;
using CityGuardians.Domain;
using CityGuardians.Gameplay;
using CityGuardians.Services;
using CityGuardians.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace CityGuardians.Tests
{
    /// <summary>
    /// 현실 혜택 안내를 사람이 하듯 해 본다: 휴대전화, CITY PASS 안내센터, 쿠폰과 QR, 장학센터
    /// (docs/CITY_PASS_SYSTEM.md 4장, docs/COUPON_SYSTEM.md 4장, docs/SCHOLARSHIP_SYSTEM.md 4장).
    /// 견본 자료는 모의 서버의 개발용 함수로만 넣는다. 기본 상태에서는 모든 기능이 "준비 중"이다.
    /// </summary>
    public sealed class BenefitPlayTests : PlayTestBase
    {
        private static readonly Vector3 SeoyunSpot = new Vector3(CityBuilder.CityPassCenter.x - 5.3f, 0f, CityBuilder.CityPassCenter.z);
        private static readonly Vector3 GaramSpot = new Vector3(CityBuilder.CityPassCenter.x - 5.1f, 0f, CityBuilder.CityPassCenter.z + 2.6f);
        private static readonly Vector3 SebinSpot = new Vector3(CityBuilder.ScholarshipCenter.x + 5.3f, 0f, CityBuilder.ScholarshipCenter.z);
        private static readonly Vector3 RuriSpot = new Vector3(CityBuilder.ScholarshipCenter.x + 5.1f, 0f, CityBuilder.ScholarshipCenter.z + 2.6f);

        private static ChoiceMenu Menu => ChoiceMenu.Instance;

        private IEnumerator PressPhone()
        {
            _input.PressPhone();
            yield return null;
            yield return null;
            yield return null;
        }

        /// <summary>오른쪽 글(값)이 그것인 줄을 고른다.</summary>
        private IEnumerator ChooseByCost(string cost)
        {
            for (int i = 0; i < Menu.Options.Count; i++)
            {
                if (Menu.Options[i].cost != cost) continue;
                yield return PressChoice(i + 1);
                yield break;
            }
            Assert.Fail($"'{Menu.Title}'에 '{cost}'인 줄이 없다");
        }

        private static int Option(string label)
        {
            for (int i = 0; i < Menu.Options.Count; i++)
                if (Menu.Options[i].label == label) return i;
            Assert.Fail($"'{Menu.Title}'에 '{label}'이(가) 없다");
            return -1;
        }

        [UnityTest]
        [Timeout(300000)]
        public IEnumerator Phone_OpensWithP_AndEverythingIsComingSoonUntilOperatorsApprove()
        {
            // 튜토리얼을 마치기 전에는 휴대전화가 없다
            yield return BootToRoom();
            yield return PressPhone();
            Assert.IsFalse(Menu.IsOpen, "방어대장을 만나기 전에는 휴대전화를 꺼낼 수 없다");

            GameSession.Current.AdvanceTutorial(6);
            GameRoot.Instance.LoadScene(SceneNames.City, SpawnIds.Home);
            yield return UntilInCity();
            yield return null;
            GameSession session = GameSession.Current;
            StringAssert.Contains("P  휴대전화", Hud.Instance.KeyHintText);
            long gc = session.Me.gc;

            // P: 휴대전화
            yield return PressPhone();
            Assert.IsTrue(Menu.IsOpen);
            Assert.AreEqual(PhoneActions.Title, Hud.Instance.Choice.Title);
            Assert.AreEqual(PhoneActions.PhoneTag, Menu.Tag);
            Assert.AreEqual(3, Menu.Options.Count);
            Assert.AreEqual("CITY PASS", Menu.Options[0].label);
            Assert.AreEqual("장학 안내", Menu.Options[1].label);
            Assert.AreEqual(PhoneActions.WebCodeTitle, Menu.Options[2].label);
            Assert.IsTrue(GameState.IsControlLocked);
            yield return Shot("70_phone");

            // CITY PASS: 운영 승인 전에는 가입도, 혜택도, 쿠폰도 "준비 중"이다
            yield return PressChoice(1);
            Assert.AreEqual(CityPassActions.Title, Menu.Title);
            Assert.AreEqual(5, Menu.Options.Count);
            Assert.AreEqual(CityPassActions.MyPassTitle, Menu.Options[0].label);
            Assert.AreEqual(CityPassActions.MerchantsTitle, Menu.Options[1].label);
            Assert.AreEqual(CityPassActions.OffersTitle, Menu.Options[2].label);
            Assert.AreEqual(CityPassActions.CouponsTitle, Menu.Options[3].label);
            Assert.AreEqual(CityPassActions.HistoryTitle, Menu.Options[4].label);
            for (int i = 0; i < 5; i++) Assert.IsFalse(Menu.Options[i].enabled, Menu.Options[i].label + ": 준비 중에는 고를 수 없다");
            Assert.AreEqual("준비 중", Menu.Options[0].cost);
            StringAssert.Contains("준비 중", Menu.Body);
            yield return Shot("71_citypass_coming_soon");
            // 고를 수 없는 줄을 눌러도 아무 일도 없다
            yield return PressChoice(3);
            Assert.AreEqual(CityPassActions.Title, Menu.Title);
            // E: 휴대전화의 첫 화면으로
            yield return PressInteract();
            Assert.AreEqual(PhoneActions.Title, Menu.Title);

            // 장학 안내: 소개와 안내는 볼 수 있고, 공고는 없다
            yield return PressChoice(2);
            Assert.AreEqual(ScholarshipActions.Title, Menu.Title);
            Assert.AreEqual(6, Menu.Options.Count);
            Assert.AreEqual(ScholarshipActions.IntroTitle, Menu.Options[0].label);
            Assert.AreEqual(ScholarshipActions.EligibilityTitle, Menu.Options[1].label);
            Assert.AreEqual(ScholarshipActions.ScheduleTitle, Menu.Options[2].label);
            Assert.AreEqual(ScholarshipActions.EducationTitle, Menu.Options[3].label);
            Assert.AreEqual(ScholarshipActions.HowToTitle, Menu.Options[4].label);
            Assert.AreEqual(ScholarshipActions.StatusTitle, Menu.Options[5].label);
            Assert.IsFalse(Menu.Options[1].enabled, "공고가 없으면 지원 자격을 고를 수 없다");
            StringAssert.Contains("준비 중", Menu.Body);

            yield return PressChoice(3);
            Assert.IsTrue(Hud.Instance.PanelVisible);
            Assert.AreEqual(ScholarshipActions.ScheduleTitle, Hud.Instance.PanelTitle);
            StringAssert.Contains("공고가 올라와요", Hud.Instance.PanelBody);
            Assert.IsTrue(Hud.Instance.PanelBodyFits);
            yield return PressInteract();
            Assert.AreEqual(ScholarshipActions.Title, Menu.Title);

            yield return PressChoice(1);
            Assert.AreEqual(ScholarshipActions.IntroTitle, Hud.Instance.PanelTitle);
            StringAssert.Contains(ScholarshipActions.NotUsed, Hud.Instance.PanelBody);
            Assert.IsTrue(Hud.Instance.PanelBodyFits);
            yield return Shot("72_scholarship_intro");
            yield return PressInteract();
            yield return PressInteract();
            Assert.AreEqual(PhoneActions.Title, Menu.Title);

            // 홈페이지 로그인 코드
            yield return PressChoice(3);
            Assert.IsTrue(Hud.Instance.PanelVisible);
            Assert.AreEqual(PhoneActions.WebCodeTitle, Hud.Instance.PanelTitle);
            string code = Hud.Instance.PanelBody.Split('\n')[0];
            Assert.IsTrue(BenefitRules.LooksLikeWebCode(code), code);
            Assert.IsTrue(((LocalMockBackend)session.Backend).DevWebCodeIsLive(code));
            yield return PressInteract();
            Assert.AreEqual(PhoneActions.Title, Menu.Title);

            // P: 넣기
            yield return PressPhone();
            Assert.IsFalse(Menu.IsOpen);
            Assert.IsFalse(GameState.IsControlLocked);
            Assert.AreEqual(gc, session.Me.gc);

            // 방에서도 꺼낼 수 있다
            GameRoot.Instance.LoadScene(SceneNames.Room, SpawnIds.Home);
            yield return UntilInRoom();
            StringAssert.Contains("P  휴대전화", Hud.Instance.KeyHintText);
            yield return PressPhone();
            Assert.AreEqual(PhoneActions.Title, Menu.Title);
            yield return PressPhone();
            Assert.IsFalse(Menu.IsOpen);
        }

        [UnityTest]
        [Timeout(600000)]
        public IEnumerator CityPass_JoinAtTheCenter_ClaimACoupon_ShowTheQr_AndTheWalletNeverMoves()
        {
            var clock = new ServerClock();
            GameRoot.ClockOverride = clock;
            yield return BootToRoom();
            GameSession session = GameSession.Current;
            var backend = (LocalMockBackend)session.Backend;
            session.AdvanceTutorial(6);
            EarnGc(clock, 300);
            backend.DevEnableBenefitSamples();
            GameRoot.Instance.LoadScene(SceneNames.City, SpawnIds.Home);
            yield return UntilInCity();
            yield return null;
            long gc = session.Me.gc;
            int xp = session.Me.xp;
            int ledger = backend.CurrentLedger.Count;

            // 안내센터가 서 있고, 두 안내원이 길 쪽을 보고 있다
            Assert.IsNotNull(GameObject.Find("CityPassCenter"));
            Assert.IsNotNull(GameObject.Find("ScholarshipCenter"));
            Assert.Less(PlanarDistance(Npc("seoyun").transform.position, SeoyunSpot), 0.5f);
            Assert.Less(PlanarDistance(Npc("garam").transform.position, GaramSpot), 0.5f);

            // 시티패스 매니저에게 말을 건다
            yield return TalkTo(SeoyunSpot, new Vector3(-2.6f, 0f, 0f), 90f);
            yield return null;
            Assert.IsTrue(Menu.IsOpen, "대화가 끝나면 안내센터 화면이 열린다");
            Assert.AreEqual(CityPassActions.Title, Menu.Title);
            Assert.AreEqual("무료 가입", Menu.Options[0].cost);
            Assert.IsTrue(Menu.Options[1].enabled);
            Assert.IsTrue(Menu.Options[2].enabled);
            Assert.IsFalse(Menu.Options[3].enabled, "가입하기 전에는 쿠폰함이 없다");
            Assert.IsFalse(Menu.Options[4].enabled);
            yield return Shot("73_citypass_center");

            // 가입: 약관을 보고, 동의하고 가입한다. 값은 없다.
            yield return PressChoice(1);
            Assert.AreEqual(CityPassActions.JoinTitle, Menu.Title);
            Assert.AreEqual("무료", Menu.Options[0].cost);
            StringAssert.Contains("실제 이름과 주소는 받지 않아요", Menu.Body);
            yield return PressChoice(2);
            Assert.IsTrue(Hud.Instance.PanelVisible);
            StringAssert.Contains("현금이나 GC, 아이템으로 바꿀 수 없어요", Hud.Instance.PanelBody);
            Assert.IsTrue(Hud.Instance.PanelBodyFits);
            yield return PressInteract();
            Assert.AreEqual(CityPassActions.JoinTitle, Menu.Title);
            yield return PressChoice(1);
            StringAssert.Contains("CITY PASS 에 가입했어요", Hud.Instance.LastToast);
            Assert.AreEqual(CityPassActions.Title, Menu.Title);
            StringAssert.Contains("회원번호 CP-", Menu.Body);
            Assert.IsTrue(Menu.Options[3].enabled);
            Assert.AreEqual("사용 가능 0장", Menu.Options[3].cost);

            // 주변 제휴 상점: 승인된 상점만, 견본 표시와 함께
            yield return PressChoice(2);
            Assert.AreEqual(CityPassActions.MerchantsTitle, Menu.Title);
            Assert.AreEqual(3, Menu.Options.Count, "지역 줄과 견본 상점 둘");
            Assert.AreEqual("지역: 전국", Menu.Options[0].label);
            for (int i = 1; i < 3; i++)
            {
                StringAssert.StartsWith(LocalMockBackend.SampleMark, Menu.Options[i].label);
                StringAssert.DoesNotContain("심사 중", Menu.Options[i].label);
            }
            yield return Shot("74_citypass_merchants");
            yield return PressInteract();
            Assert.AreEqual(CityPassActions.Title, Menu.Title);

            // 할인 혜택 → 쿠폰 받기
            yield return PressChoice(3);
            Assert.AreEqual(CityPassActions.OffersTitle, Menu.Title);
            StringAssert.Contains("GC 는 들지 않아요", Menu.Body);
            yield return ChooseByCost("10% 할인");
            Assert.AreEqual("쿠폰 받기", Menu.Options[0].label);
            Assert.AreEqual("무료", Menu.Options[0].cost);
            Assert.IsTrue(Menu.Options[0].enabled);
            yield return PressChoice(1);
            StringAssert.Contains("쿠폰을 받았어요", Hud.Instance.LastToast);
            Assert.AreEqual(CityPassActions.OffersTitle, Menu.Title);
            // 한 사람 한 장: 다시 받을 수 없다
            yield return ChooseByCost("10% 할인");
            Assert.IsFalse(Menu.Options[0].enabled);
            yield return PressInteract();
            yield return PressInteract();
            Assert.AreEqual(CityPassActions.Title, Menu.Title);
            Assert.AreEqual("사용 가능 1장", Menu.Options[3].cost);

            // 내 쿠폰 → 상점에 보여 주기
            yield return PressChoice(4);
            Assert.AreEqual(CityPassActions.CouponsTitle, Menu.Title);
            Assert.AreEqual(1, Menu.Options.Count);
            yield return PressChoice(1);
            Assert.AreEqual("상점에 보여 주기", Menu.Options[0].label);
            Assert.IsTrue(Menu.Options[0].enabled);
            StringAssert.Contains("유효기간", Menu.Body);
            yield return PressChoice(1);

            CouponPresenter presenter = CouponPresenter.Instance;
            QrPanel panel = Hud.Instance.Qr;
            Assert.IsTrue(presenter.IsOpen);
            Assert.IsFalse(Menu.IsOpen);
            Assert.IsTrue(panel.Visible);
            Assert.IsTrue(GameState.IsControlLocked, "쿠폰을 보여 주는 동안에는 걸어 다니지 않는다");
            string code = presenter.Code;
            Assert.IsTrue(BenefitRules.LooksLikePresentCode(code), code);
            Assert.AreEqual(code, panel.CodeText);
            StringAssert.Contains("남은 시간", panel.CountdownText);
            StringAssert.Contains("10% 할인", panel.SubtitleText);
            // QR 에는 일회용 코드만 들어 있다
            Assert.AreEqual(BenefitRules.QrPrefix + code, presenter.QrText);
            ApiResult<CityPassProfileDto> mine = null;
            backend.GetCityPass(r => mine = r);
            StringAssert.DoesNotContain(mine.data.memberNo, presenter.QrText);
            // 화면의 그림은 QR 그대로다 (둘레의 빈 칸 4칸 포함)
            QrCode qr = presenter.Qr;
            Assert.AreEqual(qr.Size + QrCode.QuietZone * 2, panel.TextureSize);
            for (int y = 0; y < panel.TextureSize; y++)
                for (int x = 0; x < panel.TextureSize; x++)
                {
                    int cx = x - QrCode.QuietZone;
                    int cy = y - QrCode.QuietZone;
                    bool inside = cx >= 0 && cy >= 0 && cx < qr.Size && cy < qr.Size;
                    Assert.AreEqual(inside && qr.IsDark(cx, cy), panel.IsDarkPixel(x, y), $"QR 칸 ({cx}, {cy})");
                }
            yield return new WaitForSecondsRealtime(0.3f);
            yield return Shot("75_coupon_qr");

            // 보여 주기만 해서는 쓰이지 않는다. 상점이 확인해야 쓰인다 (모의 서버의 상점 창구).
            Assert.AreEqual(ledger, backend.CurrentLedger.Count);
            Assert.IsTrue(backend.DevRedeemAtMerchant(code, out string reason), reason);
            Assert.IsFalse(backend.DevRedeemAtMerchant(code, out reason), "같은 코드는 두 번 쓸 수 없다");

            // E: 닫으면 그림과 코드가 화면에 남지 않고, 쿠폰 목록이 새로 온다
            yield return PressInteract();
            Assert.IsFalse(presenter.IsOpen);
            Assert.IsNull(presenter.Code);
            Assert.IsFalse(panel.Visible);
            Assert.AreEqual("", panel.CodeText);
            Assert.AreEqual(0, panel.TextureSize);
            Assert.AreEqual(CityPassActions.CouponsTitle, Menu.Title);
            Assert.AreEqual(BenefitRules.StatusText(CouponStatus.Redeemed), Menu.Options[0].cost);
            yield return PressChoice(1);
            Assert.IsFalse(Menu.Options[0].enabled, "쓴 쿠폰은 다시 보여 줄 수 없다");
            yield return PressInteract();
            yield return PressInteract();
            Assert.AreEqual(CityPassActions.Title, Menu.Title);
            Assert.AreEqual("사용 가능 0장", Menu.Options[3].cost);
            Assert.AreEqual("1건", Menu.Options[4].cost);

            // 이용 내역
            yield return PressChoice(5);
            Assert.AreEqual(CityPassActions.HistoryTitle, Hud.Instance.PanelTitle);
            StringAssert.Contains("10% 할인", Hud.Instance.PanelBody);
            Assert.IsTrue(Hud.Instance.PanelBodyFits);
            yield return Shot("76_citypass_history");
            yield return PressInteract();
            yield return PressInteract();
            Assert.IsFalse(Menu.IsOpen);
            Assert.IsFalse(GameState.IsControlLocked);

            // 가입, 쿠폰 받기, 쿠폰 쓰기 어느 것도 지갑·경험치·원장을 건드리지 않았다
            ApiResult<MeDto> me = null;
            backend.GetMe(r => me = r);
            Assert.AreEqual(gc, me.data.gc);
            Assert.AreEqual(xp, me.data.xp);
            Assert.AreEqual(ledger, backend.CurrentLedger.Count);
            Assert.IsNull(backend.FindLedgerInconsistency());

            // 지역 혜택 안내원은 혜택 목록부터 보여 준다
            yield return TalkTo(GaramSpot, new Vector3(-2.6f, 0f, 0f), 90f);
            yield return null;
            Assert.AreEqual(CityPassActions.OffersTitle, Menu.Title);
            yield return PressInteract();
            Assert.AreEqual(CityPassActions.Title, Menu.Title);
            yield return PressInteract();
            Assert.IsFalse(Menu.IsOpen);
        }

        [UnityTest]
        [Timeout(300000)]
        public IEnumerator CouponCode_ExpiresOnItsOwn_AndTheCouponStays()
        {
            var clock = new ServerClock();
            GameRoot.ClockOverride = clock;
            yield return BootToCityAfterTutorial();
            GameSession session = GameSession.Current;
            var backend = (LocalMockBackend)session.Backend;
            string[] ids = backend.DevEnableBenefitSamples();
            ApiResult<CityPassProfileDto> joined = null;
            backend.JoinCityPass(true, null, r => joined = r);
            Assert.IsTrue(joined.ok, joined.message);
            ApiResult<PassClaimDto> claimed = null;
            backend.ClaimCityPassCoupon(ids[0], "play-test-claim", r => claimed = r);
            Assert.IsTrue(claimed.ok, claimed.message);

            // 2초짜리 코드를 띄운다 (화면의 동작만 본다)
            bool closed = false;
            Assert.IsTrue(CouponPresenter.Instance.Show("쿠폰", "견본", "AB2CD-EF3GH", "CGPASS:AB2CD-EF3GH", 2, () => closed = true));
            yield return null;
            Assert.IsTrue(Hud.Instance.Qr.Visible);
            Assert.AreEqual("AB2CD-EF3GH", Hud.Instance.Qr.CodeText);
            yield return Until(() => closed, 5f, "코드의 시간이 지나 화면이 닫힘");
            yield return null;
            Assert.IsFalse(Hud.Instance.Qr.Visible);
            Assert.AreEqual(CouponPresenter.ExpiredToast, Hud.Instance.LastToast);
            Assert.IsFalse(GameState.IsControlLocked);

            // 쿠폰은 그대로다
            ApiResult<PassCouponsDto> coupons = null;
            backend.GetCityPassCoupons(r => coupons = r);
            Assert.AreEqual(CouponStatus.Issued, coupons.data.coupons[0].status);
            Assert.IsTrue(coupons.data.coupons[0].usable);

            // 그릴 수 없는 글은 띄우지 않는다
            Assert.IsFalse(CouponPresenter.Instance.Show("쿠폰", "견본", "잘못된 코드", "CGPASS:AB2CD-EF3GH", 60, null));
            Assert.IsFalse(CouponPresenter.Instance.Show("쿠폰", "견본", "AB2CD-EF3GH", new string('A', 400), 60, null));
            Assert.IsFalse(CouponPresenter.Instance.IsOpen);

            // 장면이 바뀌면 띄워 둔 코드는 조용히 사라진다
            Assert.IsTrue(CouponPresenter.Instance.Show("쿠폰", "견본", "AB2CD-EF3GH", "CGPASS:AB2CD-EF3GH", 60, null));
            yield return null;
            Assert.IsTrue(Hud.Instance.Qr.Visible);
            GameRoot.Instance.LoadScene(SceneNames.Room, SpawnIds.Home);
            yield return UntilInRoom();
            Assert.IsFalse(CouponPresenter.Instance.IsOpen);
            Assert.IsFalse(Hud.Instance.Qr.Visible);
            Assert.IsFalse(GameState.IsControlLocked);
        }

        [UnityTest]
        [Timeout(600000)]
        public IEnumerator ScholarshipCenter_OnlyGuides_ProgramsScheduleHowToAndStatus()
        {
            var clock = new ServerClock();
            GameRoot.ClockOverride = clock;
            yield return BootToRoom();
            GameSession session = GameSession.Current;
            var backend = (LocalMockBackend)session.Backend;
            session.AdvanceTutorial(6);
            EarnGc(clock, 500);
            string[] ids = backend.DevEnableBenefitSamples();
            GameRoot.Instance.LoadScene(SceneNames.City, SpawnIds.Home);
            yield return UntilInCity();
            yield return null;
            long gc = session.Me.gc;
            int ledger = backend.CurrentLedger.Count;

            Assert.Less(PlanarDistance(Npc("sebin").transform.position, SebinSpot), 0.5f);
            Assert.Less(PlanarDistance(Npc("ruri").transform.position, RuriSpot), 0.5f);

            // 장학센터 선생님에게 말을 건다
            yield return TalkTo(SebinSpot, new Vector3(2.6f, 0f, 0f), -90f);
            yield return null;
            Assert.IsTrue(Menu.IsOpen);
            Assert.AreEqual(ScholarshipActions.Title, Menu.Title);
            Assert.AreEqual(6, Menu.Options.Count);
            StringAssert.Contains("신청은 홈페이지에서", Menu.Body);
            // 신청, 지급, 수령 같은 줄은 없다
            foreach (ChoiceOption option in Menu.Options)
            {
                StringAssert.DoesNotContain("신청하기", option.label);
                StringAssert.DoesNotContain("받기", option.label);
                StringAssert.DoesNotContain("GC", option.cost ?? "");
            }
            Assert.AreEqual("모집 중 1건", Menu.Options[Option(ScholarshipActions.ScheduleTitle)].cost);
            yield return Shot("77_scholarship_center");

            // 지원 자격: 공고를 고르면 자격과 심사 기준이 나온다
            yield return PressChoice(2);
            Assert.AreEqual(ScholarshipActions.EligibilityTitle, Menu.Title);
            Assert.AreEqual(2, Menu.Options.Count);
            yield return ChooseByCost(BenefitRules.PhaseText(ScholarshipPhases.Open));
            Assert.IsTrue(Hud.Instance.PanelVisible);
            StringAssert.Contains("지원 자격", Hud.Instance.PanelBody);
            StringAssert.Contains("심사 기준", Hud.Instance.PanelBody);
            StringAssert.Contains("보호자 동의", Hud.Instance.PanelBody);
            StringAssert.Contains(ScholarshipActions.NotUsed, Hud.Instance.PanelBody);
            Assert.IsTrue(Hud.Instance.PanelBodyFits);
            yield return Shot("78_scholarship_program");
            yield return PressInteract();
            yield return PressInteract();
            Assert.AreEqual(ScholarshipActions.Title, Menu.Title);

            // 모집 일정
            yield return PressChoice(3);
            Assert.AreEqual(ScholarshipActions.ScheduleTitle, Hud.Instance.PanelTitle);
            StringAssert.Contains("[" + BenefitRules.PhaseText(ScholarshipPhases.Open) + "]", Hud.Instance.PanelBody);
            StringAssert.Contains("[" + BenefitRules.PhaseText(ScholarshipPhases.Upcoming) + "]", Hud.Instance.PanelBody);
            Assert.IsTrue(Hud.Instance.PanelBodyFits);
            yield return PressInteract();

            // 금융교육
            yield return PressChoice(4);
            Assert.AreEqual(ScholarshipActions.EducationTitle, Hud.Instance.PanelTitle);
            StringAssert.Contains("연습용", Hud.Instance.PanelBody);
            Assert.IsTrue(Hud.Instance.PanelBodyFits);
            yield return PressInteract();

            // 지원 방법: 순서와 로그인 코드. 게임 안에서는 신청하지 않는다.
            yield return PressChoice(5);
            Assert.AreEqual(ScholarshipActions.HowToTitle, Menu.Title);
            StringAssert.Contains("게임 안에서는 신청도, 지급도 하지 않아요", Menu.Body);
            Assert.AreEqual(2, Menu.Options.Count);
            yield return PressChoice(1);
            StringAssert.Contains("주민등록번호는 받지 않아요", Hud.Instance.PanelBody);
            StringAssert.Contains("보호자의 동의", Hud.Instance.PanelBody);
            Assert.IsTrue(Hud.Instance.PanelBodyFits);
            yield return PressInteract();
            yield return PressChoice(2);
            Assert.AreEqual(PhoneActions.WebCodeTitle, Hud.Instance.PanelTitle);
            Assert.IsTrue(BenefitRules.LooksLikeWebCode(Hud.Instance.PanelBody.Split('\n')[0]));
            yield return PressInteract();
            yield return PressInteract();
            Assert.AreEqual(ScholarshipActions.Title, Menu.Title);

            // 심사 진행 상태: 낸 신청이 없다
            yield return PressChoice(6);
            Assert.AreEqual(ScholarshipActions.StatusTitle, Hud.Instance.PanelTitle);
            StringAssert.Contains("낸 신청이 없어요", Hud.Instance.PanelBody);
            yield return PressInteract();

            // 홈페이지에서 신청한 뒤에는 상태가 보인다 (발표 전에는 결과가 나오지 않는다)
            backend.DevAddScholarshipApplication(ids[2], "in_review", "selected");
            yield return PressChoice(6);
            StringAssert.Contains("심사 중", Hud.Instance.PanelBody);
            StringAssert.DoesNotContain("선정", Hud.Instance.PanelBody);
            Assert.IsTrue(Hud.Instance.PanelBodyFits);
            yield return Shot("79_scholarship_status");
            yield return PressInteract();
            yield return PressInteract();
            Assert.IsFalse(Menu.IsOpen);

            // 교육 프로그램 안내원은 모집 일정부터 보여 준다
            yield return TalkTo(RuriSpot, new Vector3(2.6f, 0f, 0f), -90f);
            yield return null;
            Assert.IsTrue(Hud.Instance.PanelVisible);
            Assert.AreEqual(ScholarshipActions.ScheduleTitle, Hud.Instance.PanelTitle);
            yield return PressInteract();
            Assert.AreEqual(ScholarshipActions.Title, Menu.Title);
            yield return PressInteract();
            Assert.IsFalse(Menu.IsOpen);
            Assert.IsFalse(GameState.IsControlLocked);

            // 장학 안내를 다 둘러봐도 지갑과 원장은 그대로다
            ApiResult<MeDto> me = null;
            backend.GetMe(r => me = r);
            Assert.AreEqual(gc, me.data.gc);
            Assert.AreEqual(ledger, backend.CurrentLedger.Count);
        }
    }
}
