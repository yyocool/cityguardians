using System.Collections;
using System.IO;
using CityGuardians.Domain;
using CityGuardians.Gameplay;
using CityGuardians.Services;
using CityGuardians.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CityGuardians.Tests
{
    /// <summary>
    /// 실시간 대시보드를 사람이 하듯 써 본다 (docs/LIVE_DASHBOARD.md, docs/TEST_PLAN.md 16장).
    /// 카드의 상태 규칙과 자리 계산은 EditMode 의 DashboardTests 가 확인한다. 여기서는 그것들이 화면과 키에 이어져 있는지를 본다.
    /// </summary>
    public sealed class DashboardPlayTests : PlayTestBase
    {
        private static LiveDashboardController Dashboard => LiveDashboardController.Instance;
        private static DashboardLayoutManager Cards => Hud.Instance.Dashboard;

        private IEnumerator Frames(int count = 3)
        {
            for (int i = 0; i < count; i++) yield return null;
        }

        private IEnumerator UntilSettled()
        {
            yield return Until(() => !Cards.Finance.Animating && !Cards.City.Animating && !Cards.National.Animating, 3f, "카드의 움직임이 끝남");
            yield return null;
        }

        private IEnumerator PressCard(int card)
        {
            _input.PressDashboardCard(card);
            yield return Frames();
        }

        [UnityTest]
        [Timeout(300000)]
        public IEnumerator Dashboard_ShowsThreeCards_AndKeysCollapseHideAndRestoreThem()
        {
            yield return BootToCityAfterTutorial();
            yield return Until(() => Dashboard.Available && Dashboard.Summary != null && Cards.Visible, 10f, "대시보드가 요약을 받아 나타남");
            yield return UntilSettled();
            GameSession session = GameSession.Current;

            // 기본: 계좌와 우리 도시는 보이고, 전국은 접혀 있다. 모두 오른쪽에 선다.
            Assert.IsTrue(Cards.Finance.Visible);
            Assert.IsTrue(Cards.City.Visible);
            Assert.IsTrue(Cards.National.Visible);
            Assert.AreEqual(DashboardCardStates.Collapsed, Cards.National.ShownState);
            float canvasWidth = ((RectTransform)Hud.Instance.Canvas.transform).rect.width;
            for (int i = 0; i < DashboardCards.Count; i++)
            {
                DashboardBox box = Cards.Layout.boxes[i];
                Assert.Greater(box.x, canvasWidth * 0.7f, "카드는 화면 오른쪽에 있다");
                Assert.GreaterOrEqual(box.y, Cards.TopReserve, "GC 표시와 겹치지 않는다");
            }

            // 계좌 카드의 값은 서버가 준 요약 그대로다.
            if (Cards.Finance.ShownState == DashboardCardStates.Expanded)
            {
                Assert.AreEqual("보유 GC", Cards.Finance.Rows[0].LabelText);
                Assert.AreEqual(DashboardFormat.Gc(Dashboard.Summary.finance.gc), Cards.Finance.Rows[0].ValueText);
                // 모의 서버에는 가상 주식이 없다: 샘플이라는 꼬리표가 붙는다.
                Assert.AreEqual(DashboardSamples.Tag, Cards.Finance.Rows[2].TagText);
                StringAssert.StartsWith("● 실시간", Cards.Finance.FooterText);
            }
            // 이방인: 우리 도시 카드는 값을 꾸며내지 않는다.
            Assert.IsFalse(session.IsResident);
            Assert.IsNull(Cards.City.AlertText);
            yield return Shot("90_dashboard_default");

            // F9: 전국 카드를 편다. 축약 지도에 출처가 적혀 있다.
            yield return PressCard(3);
            Assert.AreEqual(DashboardCardStates.Expanded, Dashboard.State.StateOf(DashboardCards.National));
            yield return UntilSettled();
            Assert.IsTrue(Cards.National.Preview.HasMap);
            StringAssert.Contains("CC BY 4.0", NationalMapPreview.Credit);
            yield return Shot("91_dashboard_national");

            // F7: 계좌 카드를 접는다. 접혀도 핵심 수치 하나는 남는다.
            yield return PressCard(1);
            yield return UntilSettled();
            Assert.AreEqual(DashboardCardStates.Collapsed, Cards.Finance.ShownState);
            StringAssert.Contains("GC", Cards.Finance.KeyText);

            // Ctrl + F8: 우리 도시 카드만 숨긴다. F8 로 다시 나온다.
            _input.dashboardHideHeld = true;
            yield return PressCard(2);
            _input.dashboardHideHeld = false;
            yield return Until(() => !Cards.City.Visible, 3f, "우리 도시 카드가 사라짐");
            Assert.IsTrue(Cards.Finance.Visible);
            yield return PressCard(2);
            yield return Until(() => Cards.City.Visible, 3f, "우리 도시 카드가 다시 나타남");

            // F6: 전체를 숨기고 다시 보인다. 카드마다의 상태는 그대로다.
            _input.PressDashboardToggle();
            yield return Until(() => !Cards.Finance.Visible && !Cards.City.Visible && !Cards.National.Visible, 3f, "대시보드 전체가 사라짐");
            _input.PressDashboardToggle();
            yield return Until(() => Cards.Finance.Visible && Cards.National.Visible, 3f, "대시보드가 다시 나타남");
            Assert.AreEqual(DashboardCardStates.Collapsed, Dashboard.State.StateOf(DashboardCards.Finance));
            Assert.AreEqual(DashboardCardStates.Expanded, Dashboard.State.StateOf(DashboardCards.National));

            // 고친 상태는 저장된다.
            yield return new WaitForSecondsRealtime(1.2f);
            Assert.IsTrue(File.Exists(Path.Combine(_storageDir, DashboardPreferenceService.FileName)));
            DashboardPrefs saved = new DashboardPreferenceService(new FileTextStore(_storageDir)).Load();
            Assert.AreEqual(DashboardCardStates.Collapsed, saved.StateOf(DashboardCards.Finance));
            Assert.AreEqual(DashboardCardStates.Expanded, saved.StateOf(DashboardCards.National));
        }

        [UnityTest]
        [Timeout(300000)]
        public IEnumerator Dashboard_DetailOpensTheExistingScreens_AndDoesNotOpenOverOtherScreens()
        {
            yield return BootToCityAfterTutorial();
            yield return Until(() => Dashboard.Available && Dashboard.Summary != null && Cards.Visible, 10f, "대시보드가 요약을 받아 나타남");

            // Shift + F7: 계좌 상세. 항목 여섯 개가 있는 메뉴가 열린다.
            _input.dashboardDetailHeld = true;
            yield return PressCard(1);
            _input.dashboardDetailHeld = false;
            ChoiceMenu menu = ChoiceMenu.Instance;
            Assert.IsTrue(menu.IsOpen);
            Assert.AreEqual(DashboardActions.MenuTag, menu.Tag);
            Assert.AreEqual(DashboardCards.FullTitle(DashboardCards.Finance), menu.Title);
            Assert.AreEqual(6, menu.Options.Count);
            Assert.AreEqual(DashboardSamples.Tag, menu.Options[2].cost, "서버에 없는 항목은 샘플이라고 적혀 있다");

            // 메뉴가 떠 있는 동안에는 다른 상세가 그 위에 열리지 않는다. 접고 펴는 것은 된다.
            _input.dashboardDetailHeld = true;
            yield return PressCard(3);
            _input.dashboardDetailHeld = false;
            Assert.AreEqual(DashboardCards.FullTitle(DashboardCards.Finance), menu.Title);
            Assert.IsFalse(MapController.Instance.IsOpen);

            // 6: 자산 변동. 안내판이 뜨고, 닫으면 메뉴로 돌아온다.
            yield return PressChoice(6);
            Assert.IsTrue(Hud.Instance.PanelVisible);
            Assert.AreEqual("자산 변동 (7일)", Hud.Instance.PanelTitle);
            Assert.IsTrue(Hud.Instance.PanelBodyFits);
            yield return PressInteract();
            Assert.IsTrue(menu.IsOpen);
            yield return PressInteract();
            Assert.IsFalse(menu.IsOpen);
            yield return Frames();

            // Shift + F9: 전국 상세는 기존 지도 화면이다 (확대·축소, 위험도 보기).
            _input.dashboardDetailHeld = true;
            yield return PressCard(3);
            _input.dashboardDetailHeld = false;
            Assert.IsTrue(MapController.Instance.IsOpen);
            Assert.IsTrue(MapController.Instance.RiskView);
            MapController.Instance.Close();
            yield return Frames();

            // Shift + F6: 대시보드 설정. 크기를 바꾸면 카드가 커진다.
            float before = Cards.Layout.scale;
            _input.dashboardDetailHeld = true;
            _input.PressDashboardToggle();
            yield return Frames();
            _input.dashboardDetailHeld = false;
            Assert.AreEqual("대시보드 설정", menu.Title);
            yield return PressChoice(1);
            Assert.AreEqual(120, Dashboard.State.Prefs.uiScalePercent);
            yield return Frames();
            Assert.Greater(Cards.Layout.scale, before);
            yield return PressInteract();
            Assert.IsFalse(menu.IsOpen);
        }
    }
}
