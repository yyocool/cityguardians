using System.Collections;
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
    /// 입는 것과 방 꾸미기를 처음부터 끝까지 해 본다 (docs/ITEM_SYSTEM.md, docs/ROOM_SYSTEM.md 4장):
    /// 방어구와 옷을 사서 입고, 되팔고, 옷장에서 갈아입는다. 가구를 사서 방에 놓고, 옮기고, 넣어 두고, 판다.
    /// 사람이 하듯 걸어가서 E · 숫자 키 · I · F · R 키를 쓴다. 쓸 돈만 모의 서버에 사냥 보고를 보내 미리 마련한다.
    /// </summary>
    public sealed class DecorTests : PlayTestBase
    {
        private static readonly Vector3 MisoSpot = new Vector3(24.6f, 0f, 8f);
        private static readonly Vector3 AreumSpot = new Vector3(20f, 0f, -15.9f);
        private static readonly Vector3 South = Vector3.back;
        private static readonly Vector3 North = Vector3.forward;
        private static readonly Vector3 East = Vector3.right;

        private ServerClock _clock;

        [UnityTest]
        [Timeout(600000)]
        public IEnumerator Wear_BuyArmorAndClothes_PutOn_TakeLessDamage_SellBack_ChangeAtTheWardrobe_AndKeepAfterRestart()
        {
            _clock = new ServerClock();
            GameRoot.ClockOverride = _clock;
            // 돈을 마련하는 사냥 보고에서 수리 키트가 떨어지면 가방의 줄 수가 달라진다. 드롭은 ShopTests가 확인한다.
            GameDefs defs = DefaultDefs.Create();
            defs.FindEnemy(EnemyCodes.MalfunctionRobot).dropChance = 0f;
            GameRoot.DefsOverride = defs;
            yield return BootToRoom();
            GameSession session = GameSession.Current;
            var backend = (LocalMockBackend)session.Backend;
            session.AdvanceTutorial(6);
            yield return null;
            yield return null;

            // 입을 것이 없을 때의 옷장: 메뉴가 뜨지 않는다 (보리가 어디서 사는지 알려 준다)
            yield return WalkTo(new Vector3(2.0f, 0f, 0.7f), 0.3f, 10f, false);
            yield return Until(() => Hud.Instance.PromptText != null && Hud.Instance.PromptText.Contains("옷장 열기"), 2f, "옷장 안내");
            yield return PressInteract();
            Assert.IsFalse(ChoiceMenu.Instance.IsOpen, "입을 것이 없으면 옷장 메뉴가 뜨지 않는다");
            Assert.IsNull(Player.Character.HeadLook);
            Assert.IsNull(Player.Character.BodyLook);
            Assert.AreEqual(0, Player.Health.defensePercent);

            EarnGc(_clock, 720);
            GameRoot.Instance.LoadScene(SceneNames.City, SpawnIds.Home);
            yield return UntilInCity();

            // 미소 상점: 방어구 두 가지. 사면 바로 입을지 묻는다.
            yield return TalkToMiso();
            Assert.AreEqual("미소 상점", Hud.Instance.Choice.Title);
            Assert.AreEqual("가디언 헬멧", ChoiceMenu.Instance.Options[2].label);
            Assert.AreEqual("150 GC", ChoiceMenu.Instance.Options[2].cost);
            StringAssert.Contains("받는 피해 -10%", ChoiceMenu.Instance.Options[2].detail);
            Assert.AreEqual("가디언 조끼", ChoiceMenu.Instance.Options[3].label);
            Assert.AreEqual("250 GC", ChoiceMenu.Instance.Options[3].cost);
            yield return Shot("130_miso_armor");

            yield return PressChoice(3);
            Assert.AreEqual(570, session.Me.gc);
            Assert.IsTrue(session.Owns(ItemCodes.GuardHelmet));
            Assert.AreEqual("새 방어구: 가디언 헬멧", Hud.Instance.Choice.Title);
            Assert.AreEqual("바로 쓰기", ChoiceMenu.Instance.Options[0].label);
            Assert.IsNull(Player.Character.HeadLook, "묻기 전에는 쓰지 않는다");
            yield return PressChoice(1);
            Assert.IsFalse(ChoiceMenu.Instance.IsOpen);
            Assert.AreEqual(ItemCodes.GuardHelmet, session.Me.equippedHead);
            Assert.AreEqual("guard_helmet", Player.Character.HeadLook, "쓴 것이 바로 모습에 보인다");
            Assert.AreEqual(10, Player.Health.defensePercent);
            Assert.AreEqual("가디언 헬멧을 썼어요!  받는 피해 -10%", Hud.Instance.LastToast);

            yield return TalkToMiso();
            Assert.IsFalse(ChoiceMenu.Instance.Options[2].enabled, "이미 가진 방어구는 다시 살 수 없다");
            yield return PressChoice(4);
            Assert.AreEqual(320, session.Me.gc);
            Assert.AreEqual("새 방어구: 가디언 조끼", Hud.Instance.Choice.Title);
            Assert.AreEqual("바로 입기", ChoiceMenu.Instance.Options[0].label);
            yield return PressChoice(1);
            Assert.AreEqual(ItemCodes.GuardVest, session.Me.equippedBody);
            Assert.AreEqual("guard_vest", Player.Character.BodyLook);
            Assert.AreEqual(30, session.Defense);
            Assert.AreEqual(30, Player.Health.defensePercent);
            Assert.AreEqual("가디언 조끼를 입었어요!  받는 피해 -30%", Hud.Instance.LastToast);
            yield return ShotFromFront("131_city_helmet_and_vest");

            // 받는 피해가 30% 줄어든다 (20 → 14)
            int before = Player.Health.Current;
            Assert.IsTrue(Player.Health.TakeDamage(20, null));
            Assert.AreEqual(before - 14, Player.Health.Current, "방어 30%면 20의 피해가 14가 된다");

            // 가방: 지금의 방어가 보이고, 여기서도 벗고 쓴다
            yield return PressInventory();
            Assert.AreEqual("가방", Hud.Instance.Choice.Title);
            Assert.AreEqual(3, Hud.Instance.Choice.OptionCount);
            StringAssert.Contains("받는 피해 -30%", ChoiceMenu.Instance.Body);
            Assert.AreEqual("쓰는 중", ChoiceMenu.Instance.Options[1].cost);
            Assert.AreEqual("입는 중", ChoiceMenu.Instance.Options[2].cost);
            yield return Shot("132_bag_with_armor");
            yield return PressChoice(2);
            Assert.IsTrue(string.IsNullOrEmpty(session.Me.equippedHead), "가방에서 벗을 수 있다");
            Assert.IsNull(Player.Character.HeadLook);
            Assert.AreEqual(20, Player.Health.defensePercent);
            Assert.AreEqual("벗었어요.", Hud.Instance.LastToast);
            Assert.IsTrue(ChoiceMenu.Instance.IsOpen, "바꾼 뒤에도 가방이 열려 있다");
            Assert.AreEqual("쓰기", ChoiceMenu.Instance.Options[1].cost);
            StringAssert.Contains("받는 피해 -20%", ChoiceMenu.Instance.Body);
            yield return PressChoice(2);
            Assert.AreEqual("guard_helmet", Player.Character.HeadLook);
            Assert.AreEqual(30, Player.Health.defensePercent);
            yield return PressInventory();
            Assert.IsFalse(ChoiceMenu.Instance.IsOpen);

            // 알록달록 가게: 물건이 많아서 분류부터 고른다
            yield return TalkToAreum();
            Assert.AreEqual("알록달록 가게", Hud.Instance.Choice.Title);
            Assert.AreEqual(ItemActions.ShopTag, ChoiceMenu.Instance.Tag);
            Assert.AreEqual(3, Hud.Instance.Choice.OptionCount);
            Assert.AreEqual("옷 사기", ChoiceMenu.Instance.Options[0].label);
            Assert.AreEqual("4가지", ChoiceMenu.Instance.Options[0].cost);
            Assert.AreEqual("가구 사기", ChoiceMenu.Instance.Options[1].label);
            Assert.AreEqual("5가지", ChoiceMenu.Instance.Options[1].cost);
            Assert.AreEqual(ItemActions.SellLabel, ChoiceMenu.Instance.Options[2].label);
            StringAssert.Contains("320 GC", ChoiceMenu.Instance.Body);
            yield return Shot("133_colorshop_groups");

            yield return PressChoice(1);
            Assert.AreEqual("알록달록 가게 · 옷", Hud.Instance.Choice.Title);
            Assert.AreEqual(4, Hud.Instance.Choice.OptionCount);
            Assert.AreEqual("토끼 머리띠", ChoiceMenu.Instance.Options[1].label);
            Assert.AreEqual("100 GC", ChoiceMenu.Instance.Options[1].cost);
            StringAssert.Contains("모습만 바뀌어요", ChoiceMenu.Instance.Options[1].detail);
            yield return Shot("134_colorshop_clothes");

            // E는 가게를 닫지 않고 분류 화면으로 돌아간다
            yield return PressInteract();
            Assert.IsTrue(ChoiceMenu.Instance.IsOpen);
            Assert.AreEqual("알록달록 가게", Hud.Instance.Choice.Title);
            yield return PressChoice(1);

            // 토끼 머리띠: 헬멧 대신 쓴다. 옷은 모습만 바뀌므로 헬멧의 방어가 빠진다.
            yield return PressChoice(2);
            Assert.AreEqual(220, session.Me.gc);
            Assert.AreEqual("새 옷: 토끼 머리띠", Hud.Instance.Choice.Title);
            yield return PressChoice(1);
            Assert.AreEqual(ItemCodes.BunnyBand, session.Me.equippedHead);
            Assert.AreEqual("bunny_band", Player.Character.HeadLook);
            Assert.AreEqual("guard_vest", Player.Character.BodyLook);
            Assert.AreEqual(20, Player.Health.defensePercent, "머리에는 하나만 쓴다");
            Assert.AreEqual("토끼 머리띠를 썼어요!", Hud.Instance.LastToast);
            Assert.IsTrue(session.Owns(ItemCodes.GuardHelmet), "벗은 헬멧은 가방에 남는다");
            yield return ShotFromFront("135_city_bunny_band");

            // 되팔기: 쓰고 있는 것을 팔면 벗겨진다. 실수로 팔지 않게 한 번 더 묻는다.
            yield return TalkToAreum();
            yield return PressChoice(3);
            Assert.AreEqual("알록달록 가게 · 팔기", Hud.Instance.Choice.Title);
            Assert.AreEqual(1, Hud.Instance.Choice.OptionCount, "이 가게에서 산 것만 판다 (헬멧과 조끼는 미소 상점 물건)");
            Assert.AreEqual("토끼 머리띠", ChoiceMenu.Instance.Options[0].label);
            Assert.AreEqual("+30 GC", ChoiceMenu.Instance.Options[0].cost);
            StringAssert.Contains("지금 쓰고 있어요", ChoiceMenu.Instance.Options[0].detail);
            yield return PressChoice(1);
            Assert.AreEqual("토끼 머리띠 팔기", Hud.Instance.Choice.Title);
            StringAssert.Contains("30 GC를 받아요", ChoiceMenu.Instance.Body);
            StringAssert.Contains("팔면 벗겨져요", ChoiceMenu.Instance.Body);
            Assert.AreEqual("팔기", ChoiceMenu.Instance.Options[0].label);
            Assert.AreEqual("그만두기", ChoiceMenu.Instance.Options[1].label);
            yield return Shot("136_colorshop_confirm_sell");
            yield return PressChoice(2);
            Assert.AreEqual("알록달록 가게 · 팔기", Hud.Instance.Choice.Title, "그만두면 팔기 화면으로 돌아간다");
            Assert.IsTrue(session.Owns(ItemCodes.BunnyBand));
            Assert.AreEqual(220, session.Me.gc);

            yield return PressChoice(1);
            yield return PressChoice(1);
            Assert.AreEqual(250, session.Me.gc, "산 값 100 GC의 30%를 받는다");
            Assert.IsFalse(session.Owns(ItemCodes.BunnyBand));
            Assert.IsTrue(string.IsNullOrEmpty(session.Me.equippedHead));
            Assert.IsNull(Player.Character.HeadLook, "판 것은 벗겨진다");
            Assert.AreEqual("토끼 머리띠를 팔았어요!  +30 GC", Hud.Instance.LastToast);
            Assert.AreEqual(LedgerReasons.ShopSale, backend.CurrentLedger[backend.CurrentLedger.Count - 1].reason);
            Assert.IsNull(backend.FindLedgerInconsistency(), "잔액과 원장이 맞아야 한다");
            Assert.AreEqual("알록달록 가게 · 팔기", Hud.Instance.Choice.Title);
            Assert.AreEqual(1, Hud.Instance.Choice.OptionCount);
            Assert.IsFalse(ChoiceMenu.Instance.Options[0].enabled, "더 팔 것이 없다");
            yield return PressInteract(); // 분류 화면으로
            Assert.AreEqual("알록달록 가게", Hud.Instance.Choice.Title);
            yield return PressInteract(); // 닫기
            Assert.IsFalse(ChoiceMenu.Instance.IsOpen);
            Assert.IsFalse(GameState.IsControlLocked);

            // 방으로 돌아와도 입은 그대로다. 옷장에서 갈아입는다.
            GameRoot.Instance.LoadScene(SceneNames.Room, SpawnIds.RoomDoor);
            yield return UntilInRoom();
            yield return null;
            Assert.AreEqual("guard_vest", Player.Character.BodyLook, "장면이 바뀌어도 입은 모습이 그대로다");
            Assert.IsNull(Player.Character.HeadLook);
            Assert.AreEqual(20, Player.Health.defensePercent);

            yield return WalkTo(new Vector3(2.0f, 0f, 0.7f), 0.3f, 10f, false);
            yield return Until(() => Hud.Instance.PromptText != null && Hud.Instance.PromptText.Contains("옷장 열기"), 2f, "옷장 안내");
            yield return PressInteract();
            Assert.IsTrue(ChoiceMenu.Instance.IsOpen);
            Assert.AreEqual("옷장", Hud.Instance.Choice.Title);
            Assert.AreEqual(ItemActions.WardrobeTag, ChoiceMenu.Instance.Tag);
            Assert.AreEqual(2, Hud.Instance.Choice.OptionCount, "옷장에는 입는 것만 보인다 (배턴은 없다)");
            StringAssert.Contains("받는 피해 -20%", ChoiceMenu.Instance.Body);
            Assert.AreEqual("가디언 헬멧", ChoiceMenu.Instance.Options[0].label);
            Assert.AreEqual("쓰기", ChoiceMenu.Instance.Options[0].cost);
            Assert.AreEqual("입는 중", ChoiceMenu.Instance.Options[1].cost);
            yield return PressChoice(1);
            Assert.AreEqual("guard_helmet", Player.Character.HeadLook);
            Assert.AreEqual(30, Player.Health.defensePercent);
            Assert.IsTrue(ChoiceMenu.Instance.IsOpen, "갈아입은 뒤에도 옷장이 열려 있다");
            Assert.AreEqual("쓰는 중", ChoiceMenu.Instance.Options[0].cost);
            yield return Shot("137_room_wardrobe");
            yield return PressInteract();
            Assert.IsFalse(ChoiceMenu.Instance.IsOpen);

            // 껐다 켜도 입은 그대로다
            GameRoot.Instance.Shutdown();
            yield return BootToRoom();
            GameSession restored = GameSession.Current;
            Assert.AreNotSame(session, restored);
            yield return null;
            yield return null;
            Assert.AreEqual(250, restored.Me.gc);
            Assert.AreEqual(ItemCodes.GuardHelmet, restored.Me.equippedHead);
            Assert.AreEqual(ItemCodes.GuardVest, restored.Me.equippedBody);
            Assert.AreEqual("guard_helmet", Player.Character.HeadLook);
            Assert.AreEqual("guard_vest", Player.Character.BodyLook);
            Assert.AreEqual(30, Player.Health.defensePercent);
            yield return ShotFromFront("138_room_after_restart_wearing");
        }

        [UnityTest]
        [Timeout(600000)]
        public IEnumerator Decorate_BuyFurniture_Place_Rotate_Move_Store_SellStored_AndKeepAfterRestart()
        {
            _clock = new ServerClock();
            GameRoot.ClockOverride = _clock;
            yield return BootToRoom();
            GameSession session = GameSession.Current;
            var backend = (LocalMockBackend)session.Backend;
            RoomLayout layout = RoomLayout.Default;

            // 튜토리얼을 마치기 전에는 F 키가 아무 일도 하지 않는다
            yield return PressDecorate();
            Assert.IsFalse(GameState.Decorating);

            session.AdvanceTutorial(6);
            yield return null;
            yield return null;
            Assert.AreEqual(RoomBuilder.KeyHintFor(6), Hud.Instance.KeyHintText);
            StringAssert.Contains("F  방 꾸미기", Hud.Instance.KeyHintText);

            // 가구가 하나도 없으면 꾸미기에 들어가지 않고, 어디서 사는지 알려 준다
            yield return PressDecorate();
            Assert.IsFalse(GameState.Decorating);
            StringAssert.Contains("알록달록 가게", Hud.Instance.LastToast);

            // 알록달록 가게에서 소파, 화분, 조명을 산다. 가구는 가방이 아니라 가구 목록에 들어간다.
            EarnGc(_clock, 720);
            GameRoot.Instance.LoadScene(SceneNames.City, SpawnIds.Home);
            yield return UntilInCity();
            yield return ShotOfColorShop();
            yield return TalkToAreum();
            yield return PressChoice(2);
            Assert.AreEqual("알록달록 가게 · 가구", Hud.Instance.Choice.Title);
            Assert.AreEqual(5, Hud.Instance.Choice.OptionCount);
            Assert.AreEqual("초록 화분", ChoiceMenu.Instance.Options[0].label);
            Assert.AreEqual("80 GC", ChoiceMenu.Instance.Options[0].cost);
            Assert.AreEqual("소파", ChoiceMenu.Instance.Options[4].label);
            Assert.AreEqual("400 GC", ChoiceMenu.Instance.Options[4].cost);
            yield return Shot("140_colorshop_furniture");

            int bagBefore = session.Me.inventory.Count;
            yield return PressChoice(5);
            Assert.AreEqual(320, session.Me.gc);
            StringAssert.Contains("소파를 샀어요", Hud.Instance.LastToast);
            StringAssert.Contains("F 키", Hud.Instance.LastToast);
            Assert.AreEqual("알록달록 가게 · 가구", Hud.Instance.Choice.Title, "가구를 사면 진열이 다시 열린다");
            yield return PressChoice(1);
            yield return PressChoice(3);
            Assert.AreEqual(90, session.Me.gc);
            Assert.AreEqual(3, session.Me.furniture.Count);
            Assert.AreEqual(bagBefore, session.Me.inventory.Count, "가구는 가방 칸을 쓰지 않는다");
            Assert.AreEqual(1, session.StoredFurniture(ItemCodes.Sofa));
            Assert.AreEqual(1, session.StoredFurniture(ItemCodes.Plant));
            Assert.AreEqual(1, session.StoredFurniture(ItemCodes.Lamp));
            Assert.IsFalse(ChoiceMenu.Instance.Options[3].enabled, "GC가 모자란 가구는 고를 수 없다 (책장 250 GC)");
            Assert.IsNull(backend.FindLedgerInconsistency(), "잔액과 원장이 맞아야 한다");
            yield return PressInteract();
            yield return PressInteract();
            Assert.IsFalse(ChoiceMenu.Instance.IsOpen);

            string sofaId = session.Me.furniture[0].id;
            string plantId = session.Me.furniture[1].id;
            string lampId = session.Me.furniture[2].id;
            Assert.AreEqual(ItemCodes.Sofa, session.Me.furniture[0].itemCode);
            Assert.AreEqual(ItemCodes.Plant, session.Me.furniture[1].itemCode);
            Assert.AreEqual(ItemCodes.Lamp, session.Me.furniture[2].itemCode);

            // 방: 산 가구는 넣어 둔 채다. F로 꾸미기를 시작한다.
            GameRoot.Instance.LoadScene(SceneNames.Room, SpawnIds.RoomDoor);
            yield return UntilInRoom();
            RoomDecorator decorator = RoomDecorator.Instance;
            Assert.IsNotNull(decorator);
            Assert.AreEqual(0, decorator.BuiltCount);

            yield return PressDecorate();
            Assert.IsTrue(GameState.Decorating);
            Assert.IsFalse(GameState.IsControlLocked, "꾸미는 동안에도 걸어 다닌다");
            Assert.AreEqual(RoomDecorator.BrowseHint, Hud.Instance.KeyHintText);
            yield return Until(() => Hud.Instance.PromptText == RoomDecorator.TakeOutPrompt, 2f, "가구 꺼내기 안내");
            StringAssert.Contains("방 꾸미기", Hud.Instance.LastToast);
            yield return new WaitForSecondsRealtime(0.8f); // 장면이 밝아질 때까지
            yield return Shot("141_room_decorate_start");

            // 꾸미는 동안에는 가방이 열리지 않는다
            yield return PressInventory();
            Assert.IsFalse(ChoiceMenu.Instance.IsOpen);

            // 소파를 꺼내 든다
            yield return PressInteract();
            Assert.AreEqual(RoomDecorator.TakeOutTitle, Hud.Instance.Choice.Title);
            Assert.AreEqual(3, Hud.Instance.Choice.OptionCount);
            Assert.AreEqual("소파", ChoiceMenu.Instance.Options[0].label);
            Assert.AreEqual("× 1", ChoiceMenu.Instance.Options[0].cost);
            yield return Shot("142_room_take_out_menu");
            yield return PressChoice(1);
            Assert.IsTrue(decorator.IsCarrying);
            Assert.AreEqual(ItemCodes.Sofa, decorator.CarryItemCode);
            Assert.AreEqual(RoomDecorator.CarryHint, Hud.Instance.KeyHintText);

            // 남쪽 벽 앞에 놓는다. 캐릭터가 보는 쪽의 칸에 미리 보이고, 놓을 수 있으면 초록이다.
            // 든 가구는 앞면이 나를 본다: 벽을 보고 서면 등받이가 벽 쪽으로 간다 (돌릴 필요가 없다).
            yield return WalkTo(new Vector3(-0.25f, 0f, -0.2f), 0.2f, 10f, false);
            yield return WalkTo(new Vector3(-0.25f, 0f, -0.8f), 0.15f, 10f, false);
            yield return Face(South);
            Assert.AreEqual(2, decorator.CarryRot, "남쪽을 보고 서면 소파의 앞면이 북쪽(방 안쪽)을 본다");
            Assert.IsNull(decorator.CarryProblem, "남쪽 벽 앞에는 소파를 놓을 수 있다. " + Where(decorator));
            Assert.AreEqual(RoomDecorator.PlacePrompt, Hud.Instance.PromptText);

            // R로 더 돌린다: 네 번 누르면 제자리
            yield return PressRotate();
            Assert.AreEqual(3, decorator.CarryRot);
            yield return PressRotate();
            yield return PressRotate();
            yield return PressRotate();
            Assert.AreEqual(2, decorator.CarryRot);
            Assert.IsNull(decorator.CarryProblem, "한 바퀴 돌리면 그대로다. " + Where(decorator));
            yield return Shot("143_room_carry_sofa_ok");

            int sofaX = decorator.CarryX;
            int sofaZ = decorator.CarryZ;
            yield return PressInteract();
            Assert.IsFalse(decorator.IsCarrying);
            Assert.AreEqual("가구를 놓았어요!", Hud.Instance.LastToast);
            FurniturePiece sofa = RoomRules.Find(session.Me.furniture, sofaId);
            Assert.IsTrue(sofa.placed);
            Assert.AreEqual(sofaX, sofa.x);
            Assert.AreEqual(sofaZ, sofa.z);
            Assert.AreEqual(2, sofa.rot);
            Assert.LessOrEqual(sofa.z, 1, "남쪽 벽 가까이에 놓였다");
            Assert.AreEqual(1, decorator.BuiltCount);
            GameObject sofaObject = GameObject.Find("Furniture_" + sofaId);
            Assert.IsNotNull(sofaObject, "놓은 가구가 방에 서 있어야 한다");
            Assert.IsNotNull(sofaObject.GetComponent<BoxCollider>(), "놓은 가구는 지나갈 수 없다");
            Assert.AreEqual(RoomDecorator.BrowseHint, Hud.Instance.KeyHintText);
            Assert.IsTrue(GameState.Decorating, "놓은 뒤에도 꾸미기가 이어진다");
            yield return Shot("144_room_sofa_placed");

            // 놓인 가구 곁에서는 그 가구를 옮기거나 넣어 둘 수 있고, 다른 가구도 꺼낼 수 있다
            yield return Until(() => Hud.Instance.PromptText == "[E] 소파 옮기기", 2f, "소파 옮기기 안내");
            yield return PressInteract();
            Assert.AreEqual("소파", Hud.Instance.Choice.Title);
            Assert.AreEqual(3, Hud.Instance.Choice.OptionCount);
            Assert.AreEqual(RoomDecorator.MoveLabel, ChoiceMenu.Instance.Options[0].label);
            Assert.AreEqual(RoomDecorator.StoreLabel, ChoiceMenu.Instance.Options[1].label);
            Assert.AreEqual(RoomDecorator.TakeOutLabel, ChoiceMenu.Instance.Options[2].label);
            yield return PressChoice(3);
            Assert.AreEqual(RoomDecorator.TakeOutTitle, Hud.Instance.Choice.Title);
            Assert.AreEqual(2, Hud.Instance.Choice.OptionCount, "놓은 소파는 꺼낼 목록에 없다");
            Assert.AreEqual("스탠드 조명", ChoiceMenu.Instance.Options[1].label);
            yield return PressChoice(2);
            Assert.AreEqual(ItemCodes.Lamp, decorator.CarryItemCode);

            // 문 앞은 비워 둬야 한다: 빨갛게 보이고, E를 눌러도 놓이지 않는다
            yield return WalkTo(new Vector3(0.2f, 0f, -1.1f), 0.2f, 10f, false);
            yield return WalkTo(new Vector3(1.2f, 0f, -1.1f), 0.15f, 10f, false);
            yield return Face(East);
            string keepClear = RoomReasons.Message(RoomReasons.KeepClear);
            Assert.AreEqual(keepClear, decorator.CarryProblem, Where(decorator));
            Assert.AreEqual(keepClear, Hud.Instance.PromptText, "놓을 수 없는 이유를 안내에 보여 준다");
            yield return Shot("145_room_carry_lamp_blocked");
            yield return PressInteract();
            Assert.IsTrue(decorator.IsCarrying, "놓을 수 없는 자리에서는 계속 들고 있다");
            Assert.AreEqual(keepClear, Hud.Instance.LastToast);
            Assert.IsFalse(RoomRules.Find(session.Me.furniture, lampId).placed);

            // 침대와 책상 사이 북쪽 벽 앞에 놓는다
            yield return WalkTo(new Vector3(-0.75f, 0f, 0.3f), 0.2f, 10f, false);
            yield return WalkTo(new Vector3(-0.75f, 0f, 1.3f), 0.15f, 10f, false);
            yield return Face(North);
            Assert.IsNull(decorator.CarryProblem, "북쪽 벽 앞에는 조명을 놓을 수 있다. " + Where(decorator));
            yield return PressInteract();
            FurniturePiece lamp = RoomRules.Find(session.Me.furniture, lampId);
            Assert.IsTrue(lamp.placed);
            Assert.GreaterOrEqual(lamp.z, 8, "북쪽 벽 가까이에 놓였다");
            Assert.AreEqual(2, decorator.BuiltCount);
            Assert.IsNotNull(GameObject.Find("Furniture_" + lampId).GetComponentInChildren<Light>(), "조명은 불이 켜져 있다");

            // 화분은 동쪽 벽 앞에
            yield return Until(() => Hud.Instance.PromptText == "[E] 스탠드 조명 옮기기", 2f, "조명 옮기기 안내");
            yield return PressInteract();
            yield return PressChoice(3);
            Assert.AreEqual(1, Hud.Instance.Choice.OptionCount);
            Assert.AreEqual("초록 화분", ChoiceMenu.Instance.Options[0].label);
            yield return PressChoice(1);
            Assert.AreEqual(ItemCodes.Plant, decorator.CarryItemCode);
            yield return WalkTo(new Vector3(0.9f, 0f, 0.9f), 0.25f, 10f, false);
            yield return WalkTo(new Vector3(1.2f, 0f, 0.0f), 0.2f, 10f, false);
            yield return WalkTo(new Vector3(1.95f, 0f, 0.0f), 0.15f, 10f, false);
            yield return Face(East);
            Assert.IsNull(decorator.CarryProblem, "동쪽 벽 앞에는 화분을 놓을 수 있다. " + Where(decorator));
            yield return PressInteract();
            FurniturePiece plant = RoomRules.Find(session.Me.furniture, plantId);
            Assert.IsTrue(plant.placed);
            Assert.AreEqual(3, decorator.BuiltCount);
            int plantX = plant.x;
            int plantZ = plant.z;
            yield return Shot("146_room_three_placed");

            // 옮기기: 들었다가 F로 그만두면 제자리에 그대로 있다
            yield return Until(() => Hud.Instance.PromptText == "[E] 초록 화분 옮기기", 2f, "화분 옮기기 안내");
            yield return PressInteract();
            Assert.AreEqual("초록 화분", Hud.Instance.Choice.Title);
            Assert.AreEqual(2, Hud.Instance.Choice.OptionCount, "넣어 둔 가구가 없으면 꺼내기 줄이 없다");
            yield return PressChoice(1);
            Assert.IsTrue(decorator.IsCarrying);
            Assert.AreEqual(plant.rot, decorator.CarryRot, "옮기는 가구는 놓여 있던 방향 그대로 든다");
            GameObject plantObject = GameObject.Find("Furniture_" + plantId);
            Assert.IsNull(plantObject, "옮기려고 든 가구는 제자리의 모습을 숨긴다");
            yield return PressDecorate();
            Assert.IsFalse(decorator.IsCarrying, "F는 들고 있던 것을 내려놓는다");
            Assert.IsTrue(GameState.Decorating, "들고 있을 때의 F는 꾸미기를 끝내지 않는다");
            plant = RoomRules.Find(session.Me.furniture, plantId);
            Assert.IsTrue(plant.placed);
            Assert.AreEqual(plantX, plant.x);
            Assert.AreEqual(plantZ, plant.z);
            Assert.IsNotNull(GameObject.Find("Furniture_" + plantId), "그만두면 제자리에 다시 보인다");

            // 이번에는 남쪽 벽 앞, 소파 옆으로 옮긴다
            yield return Until(() => Hud.Instance.PromptText == "[E] 초록 화분 옮기기", 2f, "화분 옮기기 안내");
            yield return PressInteract();
            yield return PressChoice(1);
            Assert.IsTrue(decorator.IsCarrying);
            yield return WalkTo(new Vector3(1.25f, 0f, -0.5f), 0.2f, 10f, false);
            yield return WalkTo(new Vector3(1.25f, 0f, -1.3f), 0.15f, 10f, false);
            yield return Face(South);
            Assert.IsNull(decorator.CarryProblem, "소파 옆에는 화분을 놓을 수 있다. " + Where(decorator));
            int movedX = decorator.CarryX;
            int movedZ = decorator.CarryZ;
            yield return PressInteract();
            Assert.AreEqual("가구를 옮겼어요!", Hud.Instance.LastToast);
            plant = RoomRules.Find(session.Me.furniture, plantId);
            Assert.IsTrue(plant.placed);
            Assert.AreEqual(movedX, plant.x);
            Assert.AreEqual(movedZ, plant.z);
            Assert.IsFalse(plant.x == plantX && plant.z == plantZ, "자리가 바뀌어야 한다");
            Assert.AreEqual(3, decorator.BuiltCount);
            yield return Shot("147_room_plant_moved");

            // 넣어 두기: 조명을 방에서 치운다
            yield return WalkTo(new Vector3(-0.6f, 0f, -0.6f), 0.25f, 10f, false);
            yield return WalkTo(new Vector3(-0.75f, 0f, 1.2f), 0.2f, 10f, false);
            yield return Until(() => Hud.Instance.PromptText == "[E] 스탠드 조명 옮기기", 2f, "조명 옮기기 안내");
            yield return PressInteract();
            yield return PressChoice(2);
            Assert.AreEqual("가구를 넣어 두었어요.", Hud.Instance.LastToast);
            Assert.IsFalse(RoomRules.Find(session.Me.furniture, lampId).placed);
            Assert.AreEqual(1, session.StoredFurniture(ItemCodes.Lamp));
            Assert.AreEqual(2, decorator.BuiltCount);
            yield return null;
            Assert.IsNull(GameObject.Find("Furniture_" + lampId), "넣어 둔 가구는 방에서 사라진다");
            yield return Until(() => Hud.Instance.PromptText == RoomDecorator.TakeOutPrompt, 2f, "가구 꺼내기 안내");

            // F로 꾸미기를 끝낸다
            yield return PressDecorate();
            Assert.IsFalse(GameState.Decorating);
            Assert.AreEqual(RoomBuilder.KeyHintFor(6), Hud.Instance.KeyHintText);
            yield return null;
            yield return null;
            Assert.AreNotEqual(RoomDecorator.TakeOutPrompt, Hud.Instance.PromptText, "꾸미기 안내가 남아 있으면 안 된다");

            // 놓은 소파는 지나갈 수 없다: 소파 쪽으로 1초 걸어도 그 앞에서 멈춘다
            sofa = RoomRules.Find(session.Me.furniture, sofaId);
            float sofaNorthEdge = layout.OriginZ + (sofa.z + 2) * RoomLayout.CellSize;
            float sofaMiddleX = layout.OriginX + (sofa.x + 1.5f) * RoomLayout.CellSize;
            yield return WalkTo(new Vector3(sofaMiddleX, 0f, sofaNorthEdge + 0.9f), 0.2f, 10f, false);
            float until = Time.time + 1f;
            while (Time.time < until)
            {
                SteerTowards(South);
                yield return null;
            }
            _input.move = Vector2.zero;
            yield return null;
            Assert.Greater(Player.transform.position.z, sofaNorthEdge + 0.2f, "소파를 뚫고 지나가면 안 된다");
            yield return Shot("148_room_decorated");

            // 가게에 되팔기: 넣어 둔 가구만 팔 수 있다
            GameRoot.Instance.LoadScene(SceneNames.City, SpawnIds.Home);
            yield return UntilInCity();
            yield return TalkToAreum();
            yield return PressChoice(3);
            Assert.AreEqual("알록달록 가게 · 팔기", Hud.Instance.Choice.Title);
            Assert.AreEqual(3, Hud.Instance.Choice.OptionCount);
            Assert.AreEqual("초록 화분", ChoiceMenu.Instance.Options[0].label);
            Assert.AreEqual("+24 GC", ChoiceMenu.Instance.Options[0].cost);
            Assert.IsFalse(ChoiceMenu.Instance.Options[0].enabled, "방에 놓인 가구는 팔 수 없다");
            StringAssert.Contains("방에 놓여 있어요", ChoiceMenu.Instance.Options[0].detail);
            Assert.AreEqual("스탠드 조명", ChoiceMenu.Instance.Options[1].label);
            Assert.AreEqual("+45 GC", ChoiceMenu.Instance.Options[1].cost);
            Assert.IsTrue(ChoiceMenu.Instance.Options[1].enabled);
            Assert.AreEqual("소파", ChoiceMenu.Instance.Options[2].label);
            Assert.IsFalse(ChoiceMenu.Instance.Options[2].enabled);
            yield return Shot("149_colorshop_sell_furniture");
            yield return PressChoice(2);
            Assert.AreEqual("스탠드 조명 팔기", Hud.Instance.Choice.Title);
            yield return PressChoice(1);
            Assert.AreEqual(135, session.Me.gc, "산 값 150 GC의 30%를 받는다");
            Assert.AreEqual(2, session.Me.furniture.Count);
            Assert.IsNull(RoomRules.Find(session.Me.furniture, lampId));
            Assert.AreEqual("스탠드 조명을 팔았어요!  +45 GC", Hud.Instance.LastToast);
            Assert.AreEqual(LedgerReasons.ShopSale, backend.CurrentLedger[backend.CurrentLedger.Count - 1].reason);
            Assert.IsNull(backend.FindLedgerInconsistency(), "잔액과 원장이 맞아야 한다");
            yield return PressInteract();
            yield return PressInteract();
            Assert.IsFalse(ChoiceMenu.Instance.IsOpen);

            // 방에 다시 들어와도, 껐다 켜도 놓은 그대로다
            GameRoot.Instance.LoadScene(SceneNames.Room, SpawnIds.RoomDoor);
            yield return UntilInRoom();
            Assert.AreEqual(2, RoomDecorator.Instance.BuiltCount);
            Assert.IsNotNull(GameObject.Find("Furniture_" + sofaId));
            Assert.IsNotNull(GameObject.Find("Furniture_" + plantId));

            GameRoot.Instance.Shutdown();
            yield return BootToRoom();
            GameSession restored = GameSession.Current;
            Assert.AreNotSame(session, restored);
            Assert.AreEqual(135, restored.Me.gc);
            Assert.AreEqual(2, restored.Me.furniture.Count);
            FurniturePiece restoredSofa = RoomRules.Find(restored.Me.furniture, sofaId);
            Assert.IsTrue(restoredSofa.placed);
            Assert.AreEqual(sofaX, restoredSofa.x);
            Assert.AreEqual(sofaZ, restoredSofa.z);
            Assert.AreEqual(2, restoredSofa.rot);
            FurniturePiece restoredPlant = RoomRules.Find(restored.Me.furniture, plantId);
            Assert.IsTrue(restoredPlant.placed);
            Assert.AreEqual(movedX, restoredPlant.x);
            Assert.AreEqual(movedZ, restoredPlant.z);
            Assert.AreEqual(2, RoomDecorator.Instance.BuiltCount);
            // 소파와 화분이 있는 남쪽을 보고 한 장 남긴다 (눈으로 확인하는 용도)
            OrbitCamera.Instance.SnapBehind(180f);
            yield return new WaitForSecondsRealtime(0.8f); // 장면이 밝아질 때까지
            yield return Shot("150_room_after_restart_decorated");
        }

        // ── 도우미 ──────────────────────────────────────────────

        private IEnumerator TalkToMiso()
        {
            yield return TalkTo(MisoSpot, new Vector3(-6f, 0f, 0f), 90f);
            yield return Until(() => Hud.Instance.Choice.Visible, 2f, "상점 메뉴");
        }

        private IEnumerator TalkToAreum()
        {
            yield return TalkTo(AreumSpot, new Vector3(0f, 0f, 6f), 180f);
            yield return Until(() => Hud.Instance.Choice.Visible, 2f, "알록달록 가게 메뉴");
        }

        /// <summary>그 방향을 보게 한다: 사람이 하듯 그쪽으로 아주 잠깐 걷는다.</summary>
        private IEnumerator Face(Vector3 direction)
        {
            for (int i = 0; i < 60; i++)
            {
                Vector3 forward = Player.transform.forward;
                forward.y = 0f;
                if (Vector3.Angle(forward, direction) < 1.5f) break;
                SteerTowards(direction);
                yield return null;
            }
            _input.move = Vector2.zero;
            yield return null;
            yield return null;
            yield return null;
        }

        private static string Where(RoomDecorator decorator)
        {
            return $"문제: {decorator.CarryProblem ?? "없음"}, 칸 ({decorator.CarryX}, {decorator.CarryZ}) 방향 {decorator.CarryRot}, " +
                   $"위치 {Player.transform.position}, 보는 쪽 {Player.transform.eulerAngles.y:0}";
        }

        /// <summary>
        /// 캐릭터를 앞에서 본 모습을 남긴다 (입은 것을 눈으로 확인하는 용도).
        /// 건물 곁에서는 카메라가 벽에 막히므로 탁 트인 광장에서 찍는다.
        /// </summary>
        private static IEnumerator ShotFromFront(string name)
        {
            if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == SceneNames.City)
                Player.Teleport(new Vector3(3f, 0f, -6f), 0f);
            yield return null;
            float yaw = Player.transform.eulerAngles.y;
            OrbitCamera.Instance.SnapBehind(yaw - 150f);
            yield return new WaitForSecondsRealtime(0.8f); // 장면이 밝아질 때까지
            yield return Shot(name);
            OrbitCamera.Instance.SnapBehind(yaw);
            yield return null;
        }

        /// <summary>알록달록 가게의 겉모습을 남긴다 (눈으로 확인하는 용도).</summary>
        private IEnumerator ShotOfColorShop()
        {
            Player.Teleport(AreumSpot + new Vector3(-3f, 0f, 9f), 170f);
            yield return new WaitForSecondsRealtime(0.8f); // 장면이 밝아질 때까지
            yield return Shot("139_city_colorshop");
        }

        /// <summary>
        /// 위의 두 테스트가 지나가지 않는 모습을 화면에 남긴다 (눈으로 확인하는 용도): 파도 모자, 별 티셔츠, 해님 후드티,
        /// 폭신 쿠션, 책장. 사고 놓는 과정은 위에서 확인했으므로 여기서는 모의 서버에 바로 요청한다.
        /// </summary>
        [UnityTest]
        [Timeout(300000)]
        public IEnumerator Lookbook_TheRestOfTheClothesAndFurniture_ShowUp()
        {
            _clock = new ServerClock();
            GameRoot.ClockOverride = _clock;
            yield return BootToRoom();
            GameSession session = GameSession.Current;
            IGameBackend backend = session.Backend;
            session.AdvanceTutorial(6);
            EarnGc(_clock, 720);

            foreach (string code in new[] { ItemCodes.WaveCap, ItemCodes.StarTee, ItemCodes.SunnyHoodie, ItemCodes.Cushion, ItemCodes.Bookshelf })
            {
                ApiResult<PurchaseResultDto> bought = null;
                backend.BuyItem(ShopCodes.Color, code, System.Guid.NewGuid().ToString("N"), r => bought = r);
                Assert.IsTrue(bought.ok, $"{code}: {bought.message}");
                session.ApplyPurchase(bought.data);
            }
            Assert.AreEqual(2, session.Me.furniture.Count);

            // 쿠션은 남쪽 벽 앞, 책장은 침대와 책상 사이의 북쪽 벽 앞 (앞면이 방 안쪽을 본다)
            ApiResult<RoomDto> room = null;
            backend.PlaceFurniture(session.Me.furniture[0].id, 4, 0, 0, r => room = r);
            Assert.IsTrue(room.ok, room.message);
            backend.PlaceFurniture(session.Me.furniture[1].id, 3, 9, 0, r => room = r);
            Assert.IsTrue(room.ok, room.message);
            session.ApplyRoom(room.data);
            yield return null;
            Assert.AreEqual(2, RoomDecorator.Instance.BuiltCount);

            yield return Equip(session, EquipSlots.Head, ItemCodes.WaveCap);
            yield return Equip(session, EquipSlots.Body, ItemCodes.StarTee);
            Assert.AreEqual("wave_cap", Player.Character.HeadLook);
            Assert.AreEqual("star_tee", Player.Character.BodyLook);
            Assert.AreEqual(0, Player.Health.defensePercent, "옷에는 방어가 없다");

            // 껐다 켠다: 튜토리얼을 건너뛴 탓에 남아 있던 보리의 첫 말풍선이 사라지고, 입은 것과 놓은 것은 그대로다.
            GameRoot.Instance.Shutdown();
            yield return BootToRoom();
            session = GameSession.Current;
            yield return null;
            yield return null;
            Assert.AreEqual("wave_cap", Player.Character.HeadLook);
            Assert.AreEqual("star_tee", Player.Character.BodyLook);
            Assert.AreEqual(2, RoomDecorator.Instance.BuiltCount);

            // 책장을 등지고 서서 앞모습을 남긴다
            yield return WalkTo(new Vector3(-0.9f, 0f, 1.2f), 0.2f, 10f, false);
            yield return Face(South);
            yield return ShotFromFront("152_look_wave_cap_star_tee_bookshelf");

            yield return Equip(session, EquipSlots.Head, null);
            yield return Equip(session, EquipSlots.Body, ItemCodes.SunnyHoodie);
            Assert.IsNull(Player.Character.HeadLook);
            Assert.AreEqual("sunny_hoodie", Player.Character.BodyLook);
            // 쿠션을 등지고 서서 앞모습을 남긴다
            yield return WalkTo(new Vector3(-0.5f, 0f, -0.8f), 0.2f, 10f, false);
            yield return Face(North);
            yield return ShotFromFront("153_look_sunny_hoodie_cushion");
        }

        private static IEnumerator Equip(GameSession session, string slot, string itemCode)
        {
            ApiResult<EquipResultDto> equipped = null;
            session.Backend.EquipItem(slot, itemCode, r => equipped = r);
            Assert.IsTrue(equipped.ok, equipped.message);
            session.ApplyEquip(equipped.data);
            yield return null;
        }

        /// <summary>선택 메뉴는 여섯 줄까지 한 화면에 보이고, 숫자 6으로 여섯째 줄을 고른다.</summary>
        [UnityTest]
        [Timeout(120000)]
        public IEnumerator ChoiceMenu_SixRows_FitOnOneScreen_AndKeySixPicksTheLast()
        {
            yield return BootToRoom();
            int chosen = 0;
            var options = new ChoiceOption[ChoiceMenu.MaxOptions];
            for (int i = 0; i < options.Length; i++)
            {
                int number = i + 1;
                options[i] = new ChoiceOption
                {
                    label = $"{number}번째 줄", cost = $"{number * 100:N0} GC", detail = "설명이 한 줄 들어가요.",
                    onChosen = () => chosen = number,
                };
            }
            ChoiceMenu.Instance.Open("여섯 줄 메뉴", "첫째 줄\n둘째 줄", options);
            yield return null;
            yield return null;
            Assert.AreEqual(6, Hud.Instance.Choice.OptionCount);
            yield return Shot("151_menu_six_rows");

            // 메뉴 전체가 화면 안에 있어야 한다
            var corners = new Vector3[4];
            ((RectTransform)Hud.Instance.Choice.transform).GetWorldCorners(corners);
            Rect screen = Hud.Instance.Canvas.pixelRect;
            Assert.GreaterOrEqual(corners[0].y, screen.yMin - 0.5f, "메뉴의 아래쪽이 화면 밖으로 나가면 안 된다");
            Assert.LessOrEqual(corners[1].y, screen.yMax + 0.5f, "메뉴의 위쪽이 화면 밖으로 나가면 안 된다");

            yield return PressChoice(6);
            Assert.AreEqual(6, chosen);
            Assert.IsFalse(ChoiceMenu.Instance.IsOpen);
        }
    }
}
