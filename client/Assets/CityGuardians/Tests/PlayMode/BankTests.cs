using System;
using System.Collections;
using System.Collections.Generic;
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
    /// 은행을 처음부터 끝까지 해 본다 (docs/ECONOMY_SYSTEM.md 4장, docs/USER_FLOW.md 5.4):
    /// 레벨이 낮으면 잠겨 있고, 은행원 은다온에게 걸어가 맡기고, 밤이 지나면 이자가 들어오고,
    /// 내 방 컴퓨터에서 같은 화면을 열어 찾는다. 사람이 하듯 걸어가서 E·숫자 키를 쓴다.
    /// 맡길 돈은 모의 서버에 사냥 보고를 보내 마련하고, 밤은 서버 시계를 앞으로 감아 만든다.
    /// </summary>
    public sealed class BankTests : PlayTestBase
    {
        private static readonly Vector3 DaonSpot = new Vector3(24.6f, 0f, -9f);
        private static readonly Vector3 ComputerSpot = new Vector3(0.9f, 0f, 1.15f);
        private const double Day = 86400;

        /// <summary>게임 시간과 함께 흐르고, 앞으로 감을 수도 있는 서버 시계. 한국 시간 2026-11-13 12:00에 시작한다.</summary>
        private sealed class TestClock : IClock
        {
            private readonly DateTime _start = new DateTime(2026, 11, 13, 3, 0, 0, DateTimeKind.Utc);
            public double offset;
            public DateTime UtcNow => _start.AddSeconds(Time.timeAsDouble + offset);
        }

        private TestClock _clock;

        [UnityTest]
        [Timeout(600000)]
        public IEnumerator Bank_LockedAtFirst_DepositWithTheTeller_InterestOvernight_WithdrawFromTheRoomComputer_AndKeepAfterRestart()
        {
            _clock = new TestClock();
            GameRoot.ClockOverride = _clock;
            GameDefs defs = DefaultDefs.Create();
            defs.FindEnemy(EnemyCodes.MalfunctionRobot).dropChance = 0f;
            GameRoot.DefsOverride = defs;

            yield return BootToRoom();
            GameSession session = GameSession.Current;
            var backend = (LocalMockBackend)session.Backend;
            session.AdvanceTutorial(6);
            Earn(100);
            Assert.AreEqual(2, session.Me.level, "테스트 준비: 아직 레벨 2");
            GameRoot.Instance.LoadScene(SceneNames.City, SpawnIds.Home);
            yield return UntilInCity();
            yield return null;

            // 1. 레벨이 낮으면 은행원이 인사만 하고, 왜 안 되는지 알려 준다
            StringAssert.DoesNotContain("도시은행", Hud.Instance.ObjectiveText, "잠겨 있는 동안에는 은행을 권하지 않는다");
            yield return TalkToDaon();
            yield return null;
            Assert.IsFalse(ChoiceMenu.Instance.IsOpen, "잠겨 있으면 은행 화면이 열리지 않는다");
            StringAssert.Contains("레벨 3부터", Hud.Instance.LastToast);
            StringAssert.Contains("지금 레벨 2", Hud.Instance.LastToast);
            Assert.IsFalse(GameState.IsControlLocked);
            yield return new WaitForSecondsRealtime(0.4f);
            yield return Shot("80_bank_locked");

            // 2. 레벨 3이 되고 맡길 돈이 생기면 은행을 한 번 소개한다
            //    (볼트 블래스터를 살 수 있으면 상점 안내가 먼저라서, 블래스터는 미리 사 둔다. 상점은 ShopTests가 확인한다.)
            Earn(1700);
            BuyBlaster();
            Assert.GreaterOrEqual(session.Me.level, 3);
            Assert.AreEqual(1600, session.Me.gc);
            yield return null;
            yield return null;
            StringAssert.Contains("도시은행", Hud.Instance.ObjectiveText);
            yield return Shot("81_bank_guide");

            // 3. 은다온에게 E → 은행 첫 화면: 지갑과 통장, 이자가 언제부터 붙는지
            yield return TalkToDaon();
            yield return Until(() => Hud.Instance.Choice.Visible, 2f, "은행 화면");
            Assert.AreEqual(BankActions.Title, Hud.Instance.Choice.Title);
            Assert.AreEqual(BankActions.BankTag, ChoiceMenu.Instance.Tag);
            Assert.AreEqual(4, Hud.Instance.Choice.OptionCount);
            Assert.AreEqual(BankActions.TermLabel, ChoiceMenu.Instance.Options[2].label);
            StringAssert.Contains("지갑  1,600 GC", ChoiceMenu.Instance.Body);
            StringAssert.Contains("통장  0 GC", ChoiceMenu.Instance.Body);
            StringAssert.Contains("100 GC가 있으면 하루에 1 GC", ChoiceMenu.Instance.Body);
            Assert.IsTrue(ChoiceMenu.Instance.Options[0].enabled);
            Assert.IsFalse(ChoiceMenu.Instance.Options[1].enabled, "통장이 비어 있으면 찾을 수 없다");
            Assert.IsFalse(ChoiceMenu.Instance.Options[3].enabled, "거래가 없으면 내역도 없다");
            StringAssert.Contains("닫기", Hud.Instance.Choice.KeysText);
            Assert.IsTrue(GameState.IsControlLocked);
            yield return Shot("82_bank_main");
            StringAssert.DoesNotContain("도시은행", Hud.Instance.ObjectiveText, "한 번 열어 보면 은행 안내는 사라진다");

            // 4. 맡기기 → 금액 버튼. E는 은행을 닫지 않고 첫 화면으로 돌아간다.
            yield return PressChoice(1);
            Assert.AreEqual(BankActions.DepositTitle, Hud.Instance.Choice.Title);
            Assert.AreEqual(4, Hud.Instance.Choice.OptionCount);
            Assert.AreEqual("100 GC", ChoiceMenu.Instance.Options[0].label);
            Assert.AreEqual("500 GC", ChoiceMenu.Instance.Options[1].label);
            Assert.AreEqual("1,000 GC", ChoiceMenu.Instance.Options[2].label);
            Assert.AreEqual("전부", ChoiceMenu.Instance.Options[3].label);
            Assert.AreEqual("1,600 GC", ChoiceMenu.Instance.Options[3].cost);
            StringAssert.Contains("뒤로", Hud.Instance.Choice.KeysText);
            yield return Shot("83_bank_deposit_amounts");
            yield return PressInteract();
            Assert.IsTrue(ChoiceMenu.Instance.IsOpen, "E는 첫 화면으로 돌아간다");
            Assert.AreEqual(BankActions.Title, Hud.Instance.Choice.Title);
            Assert.AreEqual(1600, session.Me.gc, "뒤로 가기만 했다");

            // 5. 1,000 GC를 맡긴다
            yield return PressChoice(1);
            yield return PressChoice(3);
            Assert.AreEqual(600, session.Me.gc);
            Assert.AreEqual(1000, session.Me.bankGc);
            StringAssert.Contains("1,000 GC를 맡겼어요", Hud.Instance.LastToast);
            Assert.AreEqual(1, backend.CurrentBankLedger.Count);
            Assert.AreEqual(LedgerReasons.BankDeposit, backend.CurrentLedger[backend.CurrentLedger.Count - 1].reason);
            Assert.IsNull(backend.FindLedgerInconsistency(), "지갑·통장의 잔액과 원장이 맞아야 한다");
            Assert.IsTrue(ChoiceMenu.Instance.IsOpen, "맡긴 뒤에는 첫 화면으로 돌아온다");
            Assert.AreEqual(BankActions.Title, Hud.Instance.Choice.Title);
            StringAssert.Contains("지갑  600 GC", ChoiceMenu.Instance.Body);
            StringAssert.Contains("통장  1,000 GC", ChoiceMenu.Instance.Body);
            StringAssert.Contains("내일 받을 이자  +0 GC", ChoiceMenu.Instance.Body, "오늘 맡긴 돈에는 오늘 치 이자가 붙지 않는다");
            StringAssert.Contains("모레부터 하루  +10 GC", ChoiceMenu.Instance.Body);
            yield return Until(() => Hud.Instance.WalletText == "600 GC", 4f, "화면 위의 지갑 숫자가 600 GC로 내려간다");
            yield return Shot("84_bank_after_deposit");

            // 6. 지갑보다 큰 금액은 고를 수 없다. "전부"로 남은 돈을 다 맡긴다.
            yield return PressChoice(1);
            Assert.IsFalse(ChoiceMenu.Instance.Options[2].enabled, "지갑에 600 GC뿐이면 1,000 GC는 고를 수 없다");
            StringAssert.Contains("지갑에 그만큼 없어요", ChoiceMenu.Instance.Options[2].detail);
            yield return PressChoice(3);
            Assert.AreEqual(BankActions.DepositTitle, Hud.Instance.Choice.Title, "고를 수 없는 줄은 눌러도 반응하지 않는다");
            Assert.AreEqual(600, session.Me.gc);
            yield return PressChoice(4);
            Assert.AreEqual(0, session.Me.gc);
            Assert.AreEqual(1600, session.Me.bankGc);
            Assert.IsFalse(ChoiceMenu.Instance.Options[0].enabled, "지갑이 비면 맡기기를 고를 수 없다");
            Assert.IsTrue(ChoiceMenu.Instance.Options[1].enabled);

            // 7. 거래 내역: 읽고 닫으면 은행 첫 화면으로 돌아온다
            yield return PressChoice(4);
            Assert.IsTrue(Hud.Instance.PanelVisible, "거래 내역 화면");
            Assert.AreEqual(BankActions.HistoryTitle, Hud.Instance.PanelTitle);
            StringAssert.Contains("11월 13일", Hud.Instance.PanelBody);
            StringAssert.Contains("맡김  +600 GC", Hud.Instance.PanelBody);
            StringAssert.Contains("맡김  +1,000 GC", Hud.Instance.PanelBody);
            StringAssert.Contains("통장 1,600 GC", Hud.Instance.PanelBody);
            yield return Shot("85_bank_history");
            yield return PressInteract();
            Assert.IsFalse(Hud.Instance.PanelVisible);
            Assert.IsTrue(ChoiceMenu.Instance.IsOpen);
            Assert.AreEqual(BankActions.Title, Hud.Instance.Choice.Title);
            yield return PressInteract();
            Assert.IsFalse(ChoiceMenu.Instance.IsOpen, "첫 화면에서 E는 은행을 닫는다");
            Assert.IsFalse(GameState.IsControlLocked);

            // 8. 이틀 밤이 지나고 방에 들어가면 이자가 들어왔다고 알려 준다
            //    (맡긴 날의 이자는 없고, 그다음 날 치 16 GC: 1,600 GC의 1%)
            _clock.offset += 2 * Day;
            GameRoot.Instance.LoadScene(SceneNames.Room, SpawnIds.RoomDoor);
            yield return Until(() => SceneManager.GetActiveScene().name == SceneNames.Room && Player != null && !GameRoot.Instance.IsBusy,
                40f, "방 장면으로 이동");
            yield return null;
            Assert.AreEqual(1616, session.Me.bankGc);
            Assert.AreEqual(16, session.Me.bankInterestTotal);
            Assert.AreEqual(0, session.Me.gc, "이자는 통장에 들어온다");
            StringAssert.Contains("이자 +16 GC", Hud.Instance.LastToast);
            yield return new WaitForSecondsRealtime(0.4f);
            yield return Shot("86_room_interest_toast");

            // 9. 내 방 컴퓨터에서도 같은 은행 화면이 열린다
            yield return WalkTo(ComputerSpot, 0.25f, 10f, false);
            yield return Until(() => Hud.Instance.PromptText != null && Hud.Instance.PromptText.Contains("컴퓨터"), 2f, "컴퓨터 상호작용 안내");
            yield return PressInteract();
            Assert.IsTrue(ChoiceMenu.Instance.IsOpen);
            Assert.AreEqual("컴퓨터", Hud.Instance.Choice.Title);
            Assert.AreEqual(2, Hud.Instance.Choice.OptionCount);
            Assert.AreEqual("도시은행", ChoiceMenu.Instance.Options[1].label);
            Assert.AreEqual("통장 1,616 GC", ChoiceMenu.Instance.Options[1].cost);
            yield return Shot("87_room_computer_menu");
            yield return PressChoice(2);
            Assert.IsTrue(ChoiceMenu.Instance.IsOpen);
            Assert.AreEqual(BankActions.Title, Hud.Instance.Choice.Title);
            StringAssert.Contains("통장  1,616 GC", ChoiceMenu.Instance.Body);
            StringAssert.Contains("내일 받을 이자  +16 GC", ChoiceMenu.Instance.Body);
            StringAssert.DoesNotContain("모레부터", ChoiceMenu.Instance.Body);
            Assert.AreEqual("받은 이자 16 GC", ChoiceMenu.Instance.Options[3].cost);

            // 10. 500 GC를 찾는다
            yield return PressChoice(2);
            Assert.AreEqual(BankActions.WithdrawTitle, Hud.Instance.Choice.Title);
            Assert.AreEqual("1,616 GC", ChoiceMenu.Instance.Options[3].cost);
            yield return PressChoice(2);
            Assert.AreEqual(500, session.Me.gc);
            Assert.AreEqual(1116, session.Me.bankGc);
            StringAssert.Contains("500 GC를 찾았어요", Hud.Instance.LastToast);
            Assert.AreEqual(LedgerReasons.BankWithdraw, backend.CurrentBankLedger[backend.CurrentBankLedger.Count - 1].reason);
            Assert.IsNull(backend.FindLedgerInconsistency(), "지갑·통장의 잔액과 원장이 맞아야 한다");
            StringAssert.Contains("지갑  500 GC", ChoiceMenu.Instance.Body);
            StringAssert.Contains("통장  1,116 GC", ChoiceMenu.Instance.Body);
            yield return Shot("88_bank_from_computer_after_withdraw");

            yield return PressChoice(4);
            StringAssert.Contains("이자  +16 GC", Hud.Instance.PanelBody);
            StringAssert.Contains("찾음  −500 GC", Hud.Instance.PanelBody);
            StringAssert.Contains("지금까지 받은 이자  16 GC", Hud.Instance.PanelBody);
            yield return Shot("89_bank_history_with_interest");
            yield return PressInteract();
            yield return PressInteract();
            Assert.IsFalse(ChoiceMenu.Instance.IsOpen);
            Assert.IsFalse(Hud.Instance.PanelVisible);
            Assert.IsFalse(GameState.IsControlLocked);

            // 11. 컴퓨터의 도시 소식도 그대로 볼 수 있다
            yield return PressInteract();
            Assert.AreEqual("컴퓨터", Hud.Instance.Choice.Title);
            yield return PressChoice(1);
            Assert.IsTrue(Hud.Instance.PanelVisible);
            Assert.AreEqual("도시 소식", Hud.Instance.PanelTitle);
            yield return PressInteract();
            Assert.IsFalse(Hud.Instance.PanelVisible);
            Assert.IsFalse(ChoiceMenu.Instance.IsOpen);

            // 12. 껐다 켜도 통장이 그대로이고, 이미 알린 이자를 다시 알리지 않는다
            GameRoot.Instance.Shutdown();
            yield return BootToRoom();
            GameSession restored = GameSession.Current;
            Assert.AreNotSame(session, restored);
            Assert.AreEqual(500, restored.Me.gc);
            Assert.AreEqual(1116, restored.Me.bankGc);
            Assert.AreEqual(16, restored.Me.bankInterestTotal);
            Assert.AreEqual("16", restored.Me.GetFlag(FlagKeys.BankInterestSeen));
            Assert.AreEqual(0, restored.TakeUnseenInterest());
            Assert.IsNull(((LocalMockBackend)restored.Backend).FindLedgerInconsistency());
        }

        /// <summary>
        /// 정기예금 (docs/ECONOMY_SYSTEM.md 4.5): 은행원에게 가입하고, 중간에 찾으면 이자가 없다는 것을 보고,
        /// 만기가 되면 원금과 이자가 통장에 들어온다.
        /// </summary>
        [UnityTest]
        [Timeout(600000)]
        public IEnumerator TermDeposit_OpenWithTheTeller_CancelLosesInterest_MaturityPaysIntoTheAccount()
        {
            _clock = new TestClock();
            GameRoot.ClockOverride = _clock;
            GameDefs defs = DefaultDefs.Create();
            defs.FindEnemy(EnemyCodes.MalfunctionRobot).dropChance = 0f;
            GameRoot.DefsOverride = defs;

            yield return BootToRoom();
            GameSession session = GameSession.Current;
            var backend = (LocalMockBackend)session.Backend;
            session.AdvanceTutorial(6);
            Earn(2000);
            BuyBlaster();
            Assert.GreaterOrEqual(session.Me.level, 3);
            Assert.AreEqual(1800, session.Me.gc);
            GameRoot.Instance.LoadScene(SceneNames.City, SpawnIds.Home);
            yield return UntilInCity();
            yield return null;

            // 1. 은행 첫 화면의 세 번째 줄이 정기예금이다
            yield return TalkToDaon();
            yield return Until(() => Hud.Instance.Choice.Visible, 2f, "은행 화면");
            Assert.AreEqual(BankActions.TermLabel, ChoiceMenu.Instance.Options[2].label);
            Assert.AreEqual("", ChoiceMenu.Instance.Options[2].cost);
            yield return PressChoice(3);
            Assert.AreEqual(BankActions.TermTitle, Hud.Instance.Choice.Title);
            Assert.AreEqual(2, Hud.Instance.Choice.OptionCount);
            Assert.AreEqual("7일 정기예금", ChoiceMenu.Instance.Options[0].label);
            Assert.AreEqual("이자 10%", ChoiceMenu.Instance.Options[0].cost);
            Assert.AreEqual("30일 정기예금", ChoiceMenu.Instance.Options[1].label);
            Assert.AreEqual("이자 50%", ChoiceMenu.Instance.Options[1].cost);
            StringAssert.Contains("중간에 찾으면 이자가 없어요", ChoiceMenu.Instance.Body);
            StringAssert.Contains("뒤로", Hud.Instance.Choice.KeysText);
            yield return Shot("120_term_products");

            // 2. 7일 상품에 1,000 GC. 넣기 전에 만기에 얼마를 받는지 보여 준다.
            yield return PressChoice(1);
            Assert.AreEqual("7일 정기예금", Hud.Instance.Choice.Title);
            Assert.AreEqual(4, Hud.Instance.Choice.OptionCount);
            Assert.AreEqual("1,000 GC", ChoiceMenu.Instance.Options[2].label);
            Assert.AreEqual("만기에 +100 GC", ChoiceMenu.Instance.Options[2].cost);
            StringAssert.Contains("7일 뒤에 통장으로  1,100 GC", ChoiceMenu.Instance.Options[2].detail);
            Assert.AreEqual("전부", ChoiceMenu.Instance.Options[3].label);
            Assert.AreEqual("1,800 GC", ChoiceMenu.Instance.Options[3].cost);
            yield return Shot("121_term_amounts");
            yield return PressChoice(3);
            Assert.AreEqual(800, session.Me.gc);
            Assert.AreEqual(1000, session.Me.termGc);
            Assert.AreEqual(0, session.Me.bankGc, "정기예금은 통장과 따로다");
            StringAssert.Contains("1,000 GC를 넣었어요", Hud.Instance.LastToast);
            Assert.AreEqual(LedgerReasons.TermOpen, backend.CurrentLedger[backend.CurrentLedger.Count - 1].reason);
            Assert.IsNull(backend.FindLedgerInconsistency(), "지갑·통장·정기예금과 원장이 맞아야 한다");
            Assert.AreEqual(BankActions.TermTitle, Hud.Instance.Choice.Title, "넣은 뒤에는 정기예금 화면으로 돌아온다");
            StringAssert.Contains("가입 중", ChoiceMenu.Instance.Options[0].label);
            Assert.AreEqual("1,000 GC", ChoiceMenu.Instance.Options[0].cost);
            StringAssert.Contains("7일 뒤에 이자 +100 GC", ChoiceMenu.Instance.Options[0].detail);
            yield return Shot("122_term_opened");

            // 3. 가입한 상품을 누르면 중간에 찾을지 묻는다. 첫 줄은 "계속 넣어 두기"다.
            yield return PressChoice(1);
            StringAssert.Contains("중간에 찾기", Hud.Instance.Choice.Title);
            StringAssert.Contains("이자 100 GC를 받지 못해요", ChoiceMenu.Instance.Body);
            Assert.AreEqual("계속 넣어 두기", ChoiceMenu.Instance.Options[0].label);
            Assert.AreEqual("지금 찾기", ChoiceMenu.Instance.Options[1].label);
            yield return Shot("123_term_cancel_confirm");
            yield return PressChoice(1);
            Assert.AreEqual(1000, session.Me.termGc, "계속 넣어 두기를 골랐다");
            Assert.AreEqual(BankActions.TermTitle, Hud.Instance.Choice.Title);

            // 4. 30일 상품에 500 GC를 넣었다가 바로 찾는다: 이자 없이 원금만 통장으로 돌아온다
            yield return PressChoice(2);
            Assert.AreEqual("30일 정기예금", Hud.Instance.Choice.Title);
            Assert.AreEqual("만기에 +250 GC", ChoiceMenu.Instance.Options[1].cost);
            Assert.IsFalse(ChoiceMenu.Instance.Options[2].enabled, "지갑에 800 GC뿐이면 1,000 GC는 고를 수 없다");
            yield return PressChoice(2);
            Assert.AreEqual(300, session.Me.gc);
            Assert.AreEqual(1500, session.Me.termGc);
            yield return PressChoice(2);
            StringAssert.Contains("이자 250 GC를 받지 못해요", ChoiceMenu.Instance.Body);
            yield return PressChoice(2);
            Assert.AreEqual(300, session.Me.gc);
            Assert.AreEqual(500, session.Me.bankGc, "원금만 통장으로 돌아온다");
            Assert.AreEqual(1000, session.Me.termGc);
            Assert.AreEqual(0, session.Me.bankInterestTotal);
            StringAssert.Contains("500 GC를 통장으로 돌려받았어요", Hud.Instance.LastToast);
            Assert.IsNull(backend.FindLedgerInconsistency(), "지갑·통장·정기예금과 원장이 맞아야 한다");
            Assert.AreEqual("30일 정기예금", ChoiceMenu.Instance.Options[1].label, "다시 가입할 수 있다");

            // 5. E로 첫 화면에 돌아오면 넣어 둔 돈이 보이고, 거래 내역에 중간에 찾은 기록이 있다
            yield return PressInteract();
            Assert.AreEqual(BankActions.Title, Hud.Instance.Choice.Title);
            Assert.AreEqual("넣어 둔 돈 1,000 GC", ChoiceMenu.Instance.Options[2].cost);
            yield return PressChoice(4);
            StringAssert.Contains("정기예금 찾음  +500 GC", Hud.Instance.PanelBody);
            yield return PressInteract();
            yield return PressInteract();
            Assert.IsFalse(ChoiceMenu.Instance.IsOpen);
            Assert.IsFalse(GameState.IsControlLocked);

            // 6. 7일이 지나 방에 들어가면 만기를 알려 준다.
            //    통장의 500 GC에는 그동안 하루 5 GC씩 엿새 치 이자 30 GC가 붙었고, 만기로 1,000 + 100 GC가 들어온다.
            _clock.offset += 7 * Day;
            GameRoot.Instance.LoadScene(SceneNames.Room, SpawnIds.RoomDoor);
            yield return Until(() => SceneManager.GetActiveScene().name == SceneNames.Room && Player != null && !GameRoot.Instance.IsBusy,
                40f, "방 장면으로 이동");
            yield return null;
            Assert.AreEqual(0, session.Me.termGc);
            Assert.AreEqual(1630, session.Me.bankGc);
            Assert.AreEqual(130, session.Me.bankInterestTotal);
            Assert.AreEqual(300, session.Me.gc);
            StringAssert.Contains("정기예금이 만기가 됐어요", Hud.Instance.LastToast);
            StringAssert.Contains("+130 GC", Hud.Instance.LastToast);
            Assert.IsNull(backend.FindLedgerInconsistency(), "지갑·통장·정기예금과 원장이 맞아야 한다");
            yield return new WaitForSecondsRealtime(0.4f);
            yield return Shot("124_room_term_matured_toast");

            // 7. 컴퓨터의 은행에서 내역을 본다. 만기가 된 상품은 다시 가입할 수 있다.
            yield return WalkTo(ComputerSpot, 0.25f, 10f, false);
            yield return Until(() => Hud.Instance.PromptText != null && Hud.Instance.PromptText.Contains("컴퓨터"), 2f, "컴퓨터 상호작용 안내");
            yield return PressInteract();
            yield return PressChoice(2);
            Assert.AreEqual(BankActions.Title, Hud.Instance.Choice.Title);
            Assert.AreEqual("", ChoiceMenu.Instance.Options[2].cost);
            yield return PressChoice(4);
            StringAssert.Contains("정기예금 만기  +1,000 GC", Hud.Instance.PanelBody);
            StringAssert.Contains("정기예금 이자  +100 GC", Hud.Instance.PanelBody);
            StringAssert.Contains("지금까지 받은 이자  130 GC", Hud.Instance.PanelBody);
            yield return Shot("125_term_history");
            // 한 건이 한 줄이고, 다섯 건과 합계가 안내판 안에 들어가야 한다 (넘치면 제목과 닫기 안내를 덮는다)
            Assert.IsTrue(Hud.Instance.PanelBodyFits, $"거래 내역이 안내판을 넘친다 (글자 크기 {Hud.Instance.PanelBodyFontSize})");
            yield return PressInteract();
            yield return PressChoice(3);
            Assert.AreEqual("7일 정기예금", ChoiceMenu.Instance.Options[0].label);
            Assert.IsTrue(ChoiceMenu.Instance.Options[0].enabled);
            yield return PressInteract();
            yield return PressInteract();
            Assert.IsFalse(ChoiceMenu.Instance.IsOpen);

            // 8. 껐다 켜도 그대로이고, 이미 알린 만기를 다시 알리지 않는다
            GameRoot.Instance.Shutdown();
            yield return BootToRoom();
            GameSession restored = GameSession.Current;
            Assert.AreEqual(1630, restored.Me.bankGc);
            Assert.AreEqual(0, restored.Me.termGc);
            Assert.AreEqual(0, restored.TakeUnseenTermMaturities());
            Assert.AreEqual(0, restored.TakeUnseenInterest());
            Assert.IsNull(((LocalMockBackend)restored.Backend).FindLedgerInconsistency());
        }

        // ── 도우미 ──────────────────────────────────────────────

        /// <summary>
        /// 맡길 돈을 마련한다: 모의 서버에 사냥 보고를 보낸다 (로봇 한 대 10 GC, 1분에 24대까지 인정).
        /// 서버 시계를 1분씩 앞으로 감아 "너무 빨리 잡았다"는 검증에 걸리지 않게 한다.
        /// </summary>
        private void Earn(int gc)
        {
            GameSession session = GameSession.Current;
            var backend = (LocalMockBackend)session.Backend;
            while (gc > 0)
            {
                int chunk = Math.Min(gc, 240);
                ApiResult<HuntSessionDto> started = null;
                backend.StartHunt(ZoneCodes.NorthEast, r => started = r);
                Assert.IsTrue(started.ok, started.message);
                _clock.offset += 60;
                ApiResult<HuntReportDto> report = null;
                backend.ReportHunt(started.data.sessionId, 1,
                    new List<KillCount> { new KillCount(EnemyCodes.MalfunctionRobot, chunk / 10) }, r => report = r);
                Assert.IsTrue(report.ok, report.message);
                Assert.AreEqual(chunk, report.data.rewardGc, "테스트 준비: 사냥 보상");
                gc -= chunk;
            }
            ApiResult<MeDto> me = null;
            backend.GetMe(r => me = r);
            Assert.IsTrue(me.ok, me.message);
            session.ApplyMe(me.data);
        }

        /// <summary>볼트 블래스터를 사서 든다 (테스트 준비. 상점에서 사는 과정은 ShopTests가 확인한다).</summary>
        private static void BuyBlaster()
        {
            GameSession session = GameSession.Current;
            ApiResult<PurchaseResultDto> bought = null;
            session.Backend.BuyItem(ShopCodes.Miso, ItemCodes.BoltBlaster, "test-blaster", r => bought = r);
            Assert.IsTrue(bought.ok, bought.message);
            session.ApplyPurchase(bought.data);
            ApiResult<EquipResultDto> equipped = null;
            session.Backend.EquipItem(EquipSlots.Ranged, ItemCodes.BoltBlaster, r => equipped = r);
            Assert.IsTrue(equipped.ok, equipped.message);
            session.ApplyEquip(equipped.data);
            Assert.IsTrue(session.SelectSlot(EquipSlots.Ranged));
        }

        /// <summary>도시은행의 은다온에게 걸어가 말을 걸고 대화를 넘긴다.</summary>
        private IEnumerator TalkToDaon()
        {
            if (PlanarDistance(Player.transform.position, DaonSpot) > 8f)
                Player.Teleport(DaonSpot + new Vector3(-6f, 0f, 0f), 90f);
            yield return null;
            yield return WalkTo(DaonSpot, 1.6f, 15f, false);
            yield return Until(() => Hud.Instance.PromptText != null && Hud.Instance.PromptText.Contains("말하기"), 2f, "은다온에게 말하기 안내");
            yield return PressInteract();
            Assert.IsTrue(Hud.Instance.DialogueVisible, "대화가 시작되어야 한다");
            StringAssert.Contains("매일 조금씩 불어나요", Hud.Instance.DialogueBody);
            yield return AdvanceDialogue();
        }
    }
}
