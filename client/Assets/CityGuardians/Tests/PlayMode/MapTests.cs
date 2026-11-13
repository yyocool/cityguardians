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
    /// 대한민국 지도와 전입신고를 사람이 하듯 해 본다 (docs/MAP_SYSTEM.md 3장, docs/RESIDENCY_SYSTEM.md 4장).
    /// 마우스는 ScriptedInput의 화면 좌표로 흉내 낸다. 누를 자리는 지도 화면에 물어서 얻는다.
    /// </summary>
    public sealed class MapTests : PlayTestBase
    {
        private const string Busan = "26";
        private static readonly Vector3 NaraeSpot = new Vector3(-24.6f, 0f, 8f);

        private static MapController Map => MapController.Instance;
        private static MapPanel Panel => Hud.Instance.Map;

        [UnityTest]
        [Timeout(300000)]
        public IEnumerator Map_OpensWithM_ZoomsIntoASido_ShowsARegion_AndBacksOutStepByStep()
        {
            // 튜토리얼을 마치기 전에는 지도가 열리지 않는다 (배울 것을 한꺼번에 늘리지 않는다)
            yield return BootToRoom();
            yield return PressMap();
            Assert.IsFalse(Map.IsOpen, "방어대장을 만나기 전에는 지도를 열 수 없다");
            Assert.IsFalse(Panel.Visible);

            GameSession.Current.AdvanceTutorial(6);
            GameRoot.Instance.LoadScene(SceneNames.City, SpawnIds.Home);
            yield return UntilInCity();
            GameSession session = GameSession.Current;
            Vector3 standing = Player.transform.position;

            // M: 전국 지도
            yield return PressMap();
            Assert.IsTrue(Map.IsOpen);
            Assert.IsTrue(Panel.Visible);
            Assert.AreEqual(MapMode.Browse, Map.Mode);
            Assert.IsNull(Map.FocusedSido);
            Assert.IsTrue(GameState.IsControlLocked, "지도를 보는 동안에는 걸어 다니지 않는다");
            Assert.AreEqual("대한민국 지도", Panel.HeaderTitle);
            Assert.AreEqual("대한민국", Panel.InfoTitle);
            Assert.AreEqual("0명", Panel.InfoResidents, "아직 아무도 전입하지 않았다. 꾸며낸 수치를 보여 주면 안 된다");
            Assert.AreEqual("0", Panel.InfoDefensePoints);
            Assert.IsFalse(Panel.ResidencyButtonVisible, "둘러보기에서는 전입신고 버튼이 없다");
            Assert.IsFalse(Panel.MyCityPinVisible, "이방인에게는 내 도시가 없다");
            // 경계 데이터의 이용 조건: 출처가 화면에 보여야 한다
            StringAssert.Contains("통계청", Panel.AttributionText);
            StringAssert.Contains("CC BY 4.0", Panel.AttributionText);
            yield return Settle();
            yield return new WaitForSecondsRealtime(0.8f);   // 장면이 밝아지는 동안은 찍지 않는다
            yield return Shot("50_map_korea");

            // 지도가 열려 있는 동안 이동 키는 먹지 않는다
            _input.move = new Vector2(0f, 1f);
            yield return new WaitForSeconds(0.4f);
            _input.move = Vector2.zero;
            Assert.Less(PlanarDistance(standing, Player.transform.position), 0.05f);

            // 바다를 누르면 아무 일도 없다
            yield return Click(Panel.ScreenPointOfOpenSea());
            Assert.IsNull(Map.FocusedSido);

            // 부산을 가리키면 이름이 뜨고, 누르면 확대된다
            _input.pointerPosition = Panel.ScreenPointOfSido(Busan);
            yield return null;
            yield return null;
            Assert.AreEqual("부산광역시", Panel.HoverText);
            Assert.IsTrue(Panel.TooltipVisible, "지도에는 '부산'이라고만 적혀 있으므로 온전한 이름을 말풍선으로 보여 준다");
            float nationalScale = Panel.Scale;
            yield return Click(Panel.ScreenPointOfSido(Busan));
            Assert.AreEqual(Busan, Map.FocusedSido);
            Assert.IsNull(Map.SelectedRegion);
            yield return Settle();
            Assert.Greater(Panel.Scale, nationalScale * 4f, "부산이 화면에 차도록 확대되어야 한다");
            Assert.AreEqual("부산광역시", Panel.InfoTitle);
            StringAssert.Contains("부산광역시", Panel.Breadcrumb);
            yield return Shot("51_map_busan");

            // 해운대구를 누르면 정보가 보인다
            _input.pointerPosition = Panel.ScreenPointOfRegion(RegionCodes.Haeundae);
            yield return null;
            yield return null;
            Assert.AreEqual("해운대구", Panel.HoverText);
            yield return Click(Panel.ScreenPointOfRegion(RegionCodes.Haeundae));
            Assert.AreEqual(RegionCodes.Haeundae, Map.SelectedRegion);
            Assert.AreEqual("부산광역시 해운대구", Panel.InfoTitle);
            StringAssert.Contains("해운대구", Panel.Breadcrumb);
            StringAssert.Contains("정나래", Panel.InfoNote, "이방인에게는 전입신고하는 곳을 알려 준다");
            Assert.IsFalse(ChoiceMenu.Instance.IsOpen);
            yield return Shot("52_map_haeundae");

            // E는 둘러보기에서는 아무 일도 하지 않는다 (전입신고는 주민센터에서 시작한다)
            yield return PressInteract();
            Assert.IsFalse(ChoiceMenu.Instance.IsOpen);
            Assert.AreEqual(ResidencyStatus.Stranger, session.Me.residency);

            // 휠로 확대하고, 끌어서 옮긴다. 끌기는 누르기가 아니므로 고른 지역이 바뀌지 않는다.
            float before = Panel.Scale;
            _input.zoomDelta = 1f;
            yield return null;
            yield return null;
            _input.zoomDelta = 0f;
            yield return Settle();
            Assert.Greater(Panel.Scale, before * 1.2f);

            Vector2 grab = Panel.ScreenPointOfRegion(RegionCodes.Haeundae);
            _input.BeginDrag(grab);
            yield return null;
            yield return null;
            for (int i = 1; i <= 10; i++)
            {
                _input.pointerPosition = grab + new Vector2(-12f * i, 6f * i);
                yield return null;
            }
            _input.EndDrag();
            yield return null;
            yield return null;
            yield return Settle();
            Vector2 moved = Panel.ScreenPointOfRegion(RegionCodes.Haeundae);
            Assert.Less(moved.x, grab.x - 60f, "끈 만큼 지도가 따라와야 한다");
            Assert.AreEqual(RegionCodes.Haeundae, Map.SelectedRegion);
            Assert.AreEqual(Busan, Map.FocusedSido);

            // 뒤로: 지역 → 시·도 → 전국 → 닫기. Esc는 지도가 열려 있는 동안 멈춤이 아니라 뒤로다.
            _input.PressPointerBack();
            yield return null;
            yield return null;
            Assert.IsNull(Map.SelectedRegion);
            Assert.AreEqual(Busan, Map.FocusedSido);

            yield return Click(Panel.ScreenPointOfBackButton());
            Assert.IsNull(Map.FocusedSido);
            Assert.IsTrue(Map.IsOpen);
            yield return Settle();
            Assert.Less(Panel.Scale, nationalScale * 1.05f, "전국 보기로 돌아와야 한다");

            _input.PressPause();
            yield return null;
            yield return null;
            Assert.IsFalse(Map.IsOpen);
            Assert.IsFalse(Panel.Visible);
            Assert.IsFalse(GameState.IsPaused, "지도에서 누른 Esc가 게임을 멈추면 안 된다");
            Assert.IsFalse(GameState.IsControlLocked);

            // 닫은 뒤에는 다시 걸을 수 있다
            yield return WalkTo(standing + new Vector3(0f, 0f, -3f), 0.5f, 10f, false);

            // M으로 열고 M으로 닫는다
            yield return PressMap();
            Assert.IsTrue(Map.IsOpen);
            yield return PressMap();
            Assert.IsFalse(Map.IsOpen);

            // 멈춘 동안에는 열리지 않는다
            _input.PressPause();
            yield return null;
            yield return null;
            Assert.IsTrue(GameState.IsPaused);
            yield return PressMap();
            Assert.IsFalse(Map.IsOpen);
            _input.PressPause();
            yield return null;
            yield return null;
            Assert.IsFalse(GameState.IsPaused);
        }

        [UnityTest]
        [Timeout(300000)]
        public IEnumerator Residency_TalkToTheOfficer_PickHaeundaeOnTheMap_BecomeAGuardian()
        {
            yield return BootToCityAfterTutorial();
            GameSession session = GameSession.Current;
            var backend = (LocalMockBackend)session.Backend;
            string guestName = session.Me.displayName;
            StringAssert.StartsWith("이방인", guestName);
            Assert.AreEqual(ResidencyStatus.Stranger, session.Me.residency);
            Assert.AreEqual(0, session.Me.gc);
            Assert.IsNull(Hud.Instance.CityText, "이방인에게는 소속 도시가 없다");

            // 주민센터의 정나래: 권하기만 하고, "나중에"를 고르면 그대로 보내 준다
            yield return TalkToNarae();
            Assert.AreEqual("전입신고", Hud.Instance.Choice.Title);
            Assert.AreEqual(2, Hud.Instance.Choice.OptionCount);
            yield return Shot("53_residency_offer");
            yield return PressChoice(2);
            Assert.IsFalse(Map.IsOpen);
            Assert.IsFalse(GameState.IsControlLocked, "거절해도 아무것도 막히지 않는다");
            Assert.AreEqual("1", session.Me.GetFlag(FlagKeys.ResidencyDeclined));
            Assert.AreEqual(ResidencyStatus.Stranger, session.Me.residency);

            // 다시 말을 걸면 언제든 할 수 있다
            yield return TalkToNarae();
            yield return PressChoice(1);
            Assert.IsTrue(Map.IsOpen);
            Assert.AreEqual(MapMode.Residency, Map.Mode);
            StringAssert.Contains("전입신고", Panel.HeaderTitle);
            StringAssert.Contains("실제로 사는 곳과 달라도 괜찮아요", Panel.GuideText);
            Assert.IsTrue(Panel.ResidencyButtonVisible);
            Assert.IsFalse(Map.CanRequestResidency, "도시를 고르기 전에는 신고할 수 없다");
            yield return Settle();

            // 도시를 고르지 않고 E를 눌러도 아무 일도 없다
            yield return PressInteract();
            Assert.IsFalse(ChoiceMenu.Instance.IsOpen);
            Assert.IsTrue(Map.IsOpen);

            // 지도에서 부산 → 해운대구
            yield return Click(Panel.ScreenPointOfSido(Busan));
            Assert.AreEqual(Busan, Map.FocusedSido);
            yield return Settle();
            yield return Click(Panel.ScreenPointOfRegion(RegionCodes.Haeundae));
            Assert.AreEqual(RegionCodes.Haeundae, Map.SelectedRegion);
            Assert.IsTrue(Map.CanRequestResidency);
            StringAssert.Contains("여기로 전입신고", Panel.ResidencyButtonText);
            yield return Shot("54_residency_pick_city");

            // 버튼을 마우스로 누른다 (E 키도 같은 일을 한다). 이름은 추천 이름 가운데에서 고른다.
            yield return Click(Panel.ScreenPointOfResidencyButton());
            yield return Until(() => Hud.Instance.Choice.Visible, 2f, "이름 고르기 메뉴");
            Assert.AreEqual("가디언 이름 고르기", Hud.Instance.Choice.Title);
            Assert.AreEqual(4, Hud.Instance.Choice.OptionCount, "추천 이름 셋과 '다른 이름 보기'");
            string firstOffer = ChoiceMenu.Instance.Options[0].label;
            Assert.IsNull(NicknameRules.Check(firstOffer), "추천 이름은 규칙에 맞아야 한다");
            yield return Shot("55_residency_pick_name");

            // 다른 이름 보기: 방금 본 이름은 다시 나오지 않는다
            yield return PressChoice(4);
            Assert.IsTrue(ChoiceMenu.Instance.IsOpen);
            Assert.AreEqual("가디언 이름 고르기", Hud.Instance.Choice.Title);
            for (int i = 0; i < 3; i++) Assert.AreNotEqual(firstOffer, ChoiceMenu.Instance.Options[i].label);
            string nickname = ChoiceMenu.Instance.Options[1].label;

            // 이름을 고르면 한 번 더 확인한다. "도시를 다시 고를래요"는 지도로 돌아간다.
            yield return PressChoice(2);
            Assert.AreEqual("전입신고", Hud.Instance.Choice.Title);
            StringAssert.Contains("부산광역시 해운대구", ChoiceMenu.Instance.Body);
            StringAssert.Contains(nickname, ChoiceMenu.Instance.Body);
            yield return Shot("56_residency_confirm");
            yield return PressChoice(3);
            Assert.IsFalse(ChoiceMenu.Instance.IsOpen);
            Assert.IsTrue(Map.IsOpen, "지도로 돌아와야 한다");
            Assert.AreEqual(ResidencyStatus.Stranger, session.Me.residency, "확인하기 전에는 등록되지 않는다");

            // 이번에는 E 키로 시작해서 끝까지 간다
            yield return PressInteract();
            yield return Until(() => Hud.Instance.Choice.Visible, 2f, "이름 고르기 메뉴");
            nickname = ChoiceMenu.Instance.Options[0].label;
            yield return PressChoice(1);
            Assert.AreEqual("전입신고", Hud.Instance.Choice.Title);
            yield return PressChoice(1);

            // 가디언 등록: 서버가 지역과 이름을 등록하고 축하금을 준다
            int bonus = session.Defs.economy.residencyBonusGc;
            Assert.AreEqual(ResidencyStatus.Resident, session.Me.residency);
            Assert.IsTrue(session.IsResident);
            Assert.AreEqual(RegionCodes.Haeundae, session.Me.regionCode);
            Assert.AreEqual(nickname, session.Me.displayName);
            Assert.AreEqual(bonus, session.Me.gc);
            Assert.IsNull(backend.FindLedgerInconsistency(), "잔액과 원장이 맞아야 한다");
            Assert.AreEqual(LedgerReasons.ResidencyBonus, backend.CurrentLedger[backend.CurrentLedger.Count - 1].reason);

            Assert.IsFalse(Map.IsOpen, "등록이 끝나면 지도가 닫힌다");
            yield return null;
            Assert.IsTrue(Hud.Instance.PanelVisible, "가디언 등록 안내판");
            yield return new WaitForSecondsRealtime(0.4f);
            StringAssert.Contains(nickname, Hud.Instance.NameText);
            Assert.AreEqual("부산 해운대구 가디언", Hud.Instance.CityText);
            yield return Shot("57_residency_done");
            yield return PressInteract();
            Assert.IsFalse(Hud.Instance.PanelVisible);
            Assert.IsFalse(GameState.IsControlLocked);
            Assert.IsTrue(Hud.Instance.WalletVisible);
            yield return Shot("59_city_as_guardian");

            // 주민이 된 뒤의 정나래는 다시 권하지 않는다
            yield return TalkToNarae(expectMenu: false);
            Assert.IsFalse(ChoiceMenu.Instance.IsOpen);

            // 지도는 내 도시부터 보여 주고, 주민 수가 한 명이다
            yield return PressMap();
            Assert.IsTrue(Map.IsOpen);
            Assert.AreEqual(MapMode.Browse, Map.Mode);
            Assert.AreEqual(Busan, Map.FocusedSido);
            Assert.AreEqual(RegionCodes.Haeundae, Map.SelectedRegion);
            Assert.IsTrue(Panel.MyCityPinVisible);
            Assert.AreEqual("부산광역시 해운대구", Panel.InfoTitle);
            Assert.AreEqual("1명", Panel.InfoResidents);
            Assert.AreEqual("0", Panel.InfoDefensePoints);
            Assert.IsFalse(Panel.ResidencyButtonVisible);
            yield return Settle();
            yield return Shot("58_map_my_city");
            yield return PressMap();
            Assert.IsFalse(Map.IsOpen);

            // 게임을 껐다 켜도 가디언이다
            GameRoot.Instance.Shutdown();
            yield return BootToRoom();
            MeDto me = GameSession.Current.Me;
            Assert.AreEqual(ResidencyStatus.Resident, me.residency);
            Assert.AreEqual(RegionCodes.Haeundae, me.regionCode);
            Assert.AreEqual(nickname, me.displayName);
            Assert.AreEqual(bonus, me.gc);
        }

        // ── 도우미 ──────────────────────────────────────────────

        private IEnumerator PressMap()
        {
            _input.PressMap();
            yield return null;
            yield return null;
            yield return null;
        }

        /// <summary>화면의 한 곳을 마우스로 한 번 누른다.</summary>
        private IEnumerator Click(Vector2 screenPoint)
        {
            _input.Click(screenPoint);
            yield return null;
            yield return null;
            yield return null;
            yield return null;
        }

        /// <summary>지도의 확대·이동이 끝날 때까지 기다린다. 그 뒤에 읽은 화면 좌표가 맞다.</summary>
        private IEnumerator Settle()
        {
            yield return null;
            yield return Until(() => Panel.IsSettled, 5f, "지도 확대·이동이 끝남");
            yield return null;
        }

        /// <summary>주민센터의 정나래에게 걸어가 말을 걸고 대화를 끝까지 넘긴다.</summary>
        private IEnumerator TalkToNarae(bool expectMenu = true)
        {
            if (PlanarDistance(Player.transform.position, NaraeSpot) > 8f)
                Player.Teleport(NaraeSpot + new Vector3(6f, 0f, 0f), -90f);
            yield return null;
            yield return WalkTo(NaraeSpot, 1.6f, 15f, false);
            yield return Until(() => Hud.Instance.PromptText != null && Hud.Instance.PromptText.Contains("말하기"), 2f, "정나래에게 말하기 안내");
            yield return PressInteract();
            Assert.IsTrue(Hud.Instance.DialogueVisible, "대화가 시작되어야 한다");
            yield return AdvanceDialogue();
            if (!expectMenu) yield break;
            yield return Until(() => Hud.Instance.Choice.Visible, 2f, "전입신고 메뉴");
        }
    }
}
