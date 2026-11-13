using System;
using System.Collections.Generic;
using CityGuardians.Domain;
using CityGuardians.Services;
using NUnit.Framework;

namespace CityGuardians.Tests
{
    /// <summary>
    /// v10.0 실시간 대시보드의 규칙 (docs/LIVE_DASHBOARD.md, docs/HUD_LAYOUT_SYSTEM.md, docs/TEST_PLAN.md 16장).
    /// 카드의 상태, 전투 모드별 자동 접기와 되돌리기, 해상도별 자리 잡기, 설정 저장, 갱신 간격, 알림, 카드에 쓰는 글자,
    /// 그리고 모의 서버가 주는 요약이 기존 API 의 값과 같은지.
    /// </summary>
    public class DashboardTests
    {
        private const string F = DashboardCards.Finance;
        private const string C = DashboardCards.City;
        private const string N = DashboardCards.National;
        private const string Expanded = DashboardCardStates.Expanded;
        private const string Collapsed = DashboardCardStates.Collapsed;
        private const string Hidden = DashboardCardStates.Hidden;

        private MockKit _kit;

        [SetUp]
        public void SetUp() => _kit = new MockKit();

        private static string[] States(DashboardState state) => new[] { state.StateOf(F), state.StateOf(C), state.StateOf(N) };

        private static DashboardSummaryDto Summary(LocalMockBackend backend)
        {
            ApiResult<DashboardSummaryDto> result = null;
            backend.GetDashboardSummary(r => result = r);
            return MockKit.Ok(result);
        }

        // ── 카드의 상태 ─────────────────────────────────────

        [Test]
        public void Cards_StartFromTheAdminDefaults_AndTheUsersChoiceWins()
        {
            var state = new DashboardState();
            // 기본: 계좌와 도시는 펼쳐 있고, 전국은 접혀 있다.
            CollectionAssert.AreEqual(new[] { Expanded, Expanded, Collapsed }, States(state));
            Assert.IsFalse(state.PrefsDirty);

            // 관리자가 기본값을 바꾸면, 이용자가 손대지 않은 카드는 따라간다.
            DashboardSettings settings = DashboardSettings.Default();
            settings.Find(F).visible = false;
            settings.Find(N).expanded = true;
            state.ApplySettings(settings);
            CollectionAssert.AreEqual(new[] { Hidden, Expanded, Expanded }, States(state));

            // 이용자가 손댄 카드는 관리자의 기본값이 바뀌어도 그대로다.
            state.Toggle(C);
            Assert.AreEqual(Collapsed, state.StateOf(C));
            settings.Find(C).expanded = true;
            state.ApplySettings(settings);
            Assert.AreEqual(Collapsed, state.StateOf(C));
            Assert.IsTrue(state.PrefsDirty);

            // 모르는 카드는 없는 것으로 친다.
            int version = state.Version;
            state.Toggle("weather");
            state.ToggleHidden("weather");
            Assert.AreEqual(version, state.Version);
        }

        [Test]
        public void EachCard_IsCollapsedExpandedAndHiddenOnItsOwn()
        {
            var state = new DashboardState();
            state.Toggle(F);
            CollectionAssert.AreEqual(new[] { Collapsed, Expanded, Collapsed }, States(state));
            state.Toggle(N);
            CollectionAssert.AreEqual(new[] { Collapsed, Expanded, Expanded }, States(state));
            state.ToggleHidden(C);
            CollectionAssert.AreEqual(new[] { Collapsed, Hidden, Expanded }, States(state));
            // 숨긴 카드는 접기·펴기 키로도, 숨기기 키로도 다시 나온다.
            state.Toggle(C);
            Assert.AreEqual(Expanded, state.StateOf(C));
            state.ToggleHidden(C);
            state.ToggleHidden(C);
            Assert.AreEqual(Expanded, state.StateOf(C));
            state.Toggle(F);
            Assert.AreEqual(Expanded, state.StateOf(F));
        }

        [Test]
        public void WholeDashboard_IsHiddenAndShownAtOnce_AndCardsRememberTheirStates()
        {
            var state = new DashboardState();
            state.Toggle(F);
            state.ToggleAll();
            Assert.IsFalse(state.Visible);
            CollectionAssert.AreEqual(new[] { Hidden, Hidden, Hidden }, States(state));
            // 카드마다의 상태는 지워지지 않는다.
            Assert.AreEqual(Collapsed, state.UserState(F));
            state.ToggleAll();
            CollectionAssert.AreEqual(new[] { Collapsed, Expanded, Collapsed }, States(state));

            // 대시보드가 숨겨진 동안 카드 키를 누르면 대시보드가 다시 보이고 그 카드가 펴진다.
            state.ToggleAll();
            state.Toggle(N);
            Assert.IsTrue(state.Visible);
            CollectionAssert.AreEqual(new[] { Collapsed, Expanded, Expanded }, States(state));
        }

        // ── 전투 모드 ───────────────────────────────────────

        [Test]
        public void HuntKeepsTheDashboard_FpsCollapsesIt_AndTheEndOfBattleRestoresIt()
        {
            Assert.AreEqual(DashboardModes.Normal, DashboardModes.Of(null));
            Assert.AreEqual(DashboardModes.Normal, DashboardModes.Of(CombatModeCodes.Hunt));
            Assert.AreEqual(DashboardModes.Fps, DashboardModes.Of(CombatModeCodes.Fps));
            Assert.AreEqual(DashboardModes.Defense, DashboardModes.Of(CombatModeCodes.Defense));

            var state = new DashboardState();
            state.Toggle(N);
            state.ToggleHidden(F);
            state.MarkSaved();
            string[] before = States(state);
            CollectionAssert.AreEqual(new[] { Hidden, Expanded, Expanded }, before);

            // 사냥: 그대로.
            Assert.IsFalse(state.SetMode(DashboardModes.Of(CombatModeCodes.Hunt)));
            CollectionAssert.AreEqual(before, States(state));

            // FPS: 보이던 카드는 모두 접힌다. 숨긴 카드는 숨긴 채다.
            Assert.IsTrue(state.SetMode(DashboardModes.Fps));
            Assert.IsTrue(state.InBattle);
            CollectionAssert.AreEqual(new[] { Hidden, Collapsed, Collapsed }, States(state));

            // 전투 중에 편 카드, 숨긴 카드는 그 전투 동안만 지켜진다.
            state.Toggle(C);
            state.ToggleHidden(N);
            state.ToggleHidden(F);
            CollectionAssert.AreEqual(new[] { Collapsed, Expanded, Hidden }, States(state));
            Assert.IsFalse(state.PrefsDirty, "전투 중의 선택은 저장되지 않는다");

            // 전투가 끝나면 전투 전의 상태로 돌아간다.
            Assert.IsTrue(state.SetMode(DashboardModes.Normal));
            CollectionAssert.AreEqual(before, States(state));
            Assert.IsFalse(state.InBattle);

            // 다음 전투는 다시 접힌 채로 시작한다.
            state.SetMode(DashboardModes.Fps);
            CollectionAssert.AreEqual(new[] { Hidden, Collapsed, Collapsed }, States(state));
            // 모르는 모드는 평소와 같다.
            state.SetMode("racing");
            Assert.AreEqual(DashboardModes.Normal, state.Mode);
        }

        [Test]
        public void TowerDefense_CollapsesByDefault_OrShowsCardsWhereTheUserChose()
        {
            var state = new DashboardState();
            state.SetMode(DashboardModes.Defense);
            CollectionAssert.AreEqual(new[] { Collapsed, Collapsed, Collapsed }, States(state));
            Assert.IsFalse(state.DockLeft);

            // 오른쪽에 그대로: 평소에 정한 상태를 쓴다.
            state.CyclePlacement();
            Assert.AreEqual(DashboardPlacements.Right, state.Prefs.defensePlacement);
            CollectionAssert.AreEqual(new[] { Expanded, Expanded, Collapsed }, States(state));
            Assert.IsFalse(state.DockLeft);

            // 왼쪽으로 옮기기.
            state.CyclePlacement();
            Assert.AreEqual(DashboardPlacements.Left, state.Prefs.defensePlacement);
            Assert.IsTrue(state.DockLeft);
            CollectionAssert.AreEqual(new[] { Expanded, Expanded, Collapsed }, States(state));
            Assert.IsTrue(state.PrefsDirty, "자리는 이용자의 설정으로 저장된다");

            // 자리 설정은 타워디펜스에서만 쓰인다.
            state.SetMode(DashboardModes.Normal);
            Assert.IsFalse(state.DockLeft);
            state.SetMode(DashboardModes.Fps);
            Assert.IsFalse(state.DockLeft);
            CollectionAssert.AreEqual(new[] { Collapsed, Collapsed, Collapsed }, States(state));

            state.SetMode(DashboardModes.Defense);
            state.CyclePlacement();
            Assert.AreEqual(DashboardPlacements.Collapsed, state.Prefs.defensePlacement);
            foreach (string placement in DashboardPlacements.All) Assert.IsNotEmpty(DashboardPlacements.DisplayName(placement));
        }

        [Test]
        public void HidingTheWholeDashboardInBattle_StaysTheUsersSetting()
        {
            var state = new DashboardState();
            state.SetMode(DashboardModes.Fps);
            state.ToggleAll();
            Assert.IsTrue(state.PrefsDirty);
            state.SetMode(DashboardModes.Normal);
            Assert.IsFalse(state.Visible);
            CollectionAssert.AreEqual(new[] { Hidden, Hidden, Hidden }, States(state));
        }

        // ── 설정 저장 ───────────────────────────────────────

        [Test]
        public void Preferences_AreSavedAndLoaded_AndBrokenFilesFallBackToDefaults()
        {
            var store = new MemoryTextStore();
            var service = new DashboardPreferenceService(store);

            // 저장된 것이 없으면 기본값.
            DashboardPrefs loaded = service.Load();
            Assert.IsTrue(loaded.visible);
            Assert.AreEqual(100, loaded.uiScalePercent);
            Assert.AreEqual(0, loaded.cards.Count);

            var state = new DashboardState(loaded);
            state.Toggle(F);
            state.ToggleHidden(N);
            state.CycleScale();
            state.CyclePlacement();
            state.ToggleAll();
            Assert.IsTrue(service.Save(state.Prefs));

            var again = new DashboardState(new DashboardPreferenceService(store).Load());
            Assert.IsFalse(again.Visible);
            Assert.AreEqual(120, again.Prefs.uiScalePercent);
            Assert.AreEqual(DashboardPlacements.Right, again.Prefs.defensePlacement);
            again.ToggleAll();
            CollectionAssert.AreEqual(new[] { Collapsed, Expanded, Hidden }, States(again));

            // 깨진 파일, 범위를 벗어난 값, 모르는 카드와 상태.
            store.Write(DashboardPreferenceService.FileName, "{ this is not json");
            Assert.AreEqual(100, service.Load().uiScalePercent);
            store.Write(DashboardPreferenceService.FileName,
                "{\"version\":99,\"visible\":true,\"uiScalePercent\":900,\"defensePlacement\":\"moon\",\"cards\":["
                + "{\"id\":\"finance\",\"state\":\"collapsed\"},{\"id\":\"finance\",\"state\":\"hidden\"},"
                + "{\"id\":\"weather\",\"state\":\"expanded\"},{\"id\":\"city\",\"state\":\"sideways\"}]}");
            DashboardPrefs odd = service.Load();
            Assert.AreEqual(DashboardPrefs.MaxScalePercent, odd.uiScalePercent);
            Assert.AreEqual(DashboardPlacements.Collapsed, odd.defensePlacement);
            Assert.AreEqual(DashboardPrefs.CurrentVersion, odd.version);
            Assert.AreEqual(1, odd.cards.Count);
            Assert.AreEqual(F, odd.cards[0].id);
            Assert.IsNull(odd.StateOf(C));

            // 저장하지 못해도 게임은 계속된다.
            Assert.IsFalse(new DashboardPreferenceService(new BrokenStore()).Save(state.Prefs));
            Assert.AreEqual(100, new DashboardPreferenceService(new BrokenStore()).Load().uiScalePercent);
        }

        private sealed class BrokenStore : ITextStore
        {
            public string Read(string name) => throw new InvalidOperationException("읽을 수 없음");
            public void Write(string name, string text) => throw new InvalidOperationException("쓸 수 없음");
            public void Move(string name, string newName) { }
        }

        [Test]
        public void UiScale_StepsThroughItsRange_AndResetReturnsToDefaults()
        {
            var state = new DashboardState();
            var seen = new List<int>();
            for (int i = 0; i < 5; i++)
            {
                state.CycleScale();
                seen.Add(state.Prefs.uiScalePercent);
            }
            CollectionAssert.AreEqual(new[] { 120, 140, 80, 100, 120 }, seen);
            state.SetScale(5);
            Assert.AreEqual(80, state.Prefs.uiScalePercent);
            state.SetScale(500);
            Assert.AreEqual(140, state.Prefs.uiScalePercent);

            state.Toggle(C);
            state.ResetToDefaults();
            Assert.AreEqual(100, state.Prefs.uiScalePercent);
            CollectionAssert.AreEqual(new[] { Expanded, Expanded, Collapsed }, States(state));
        }

        // ── 자리 잡기 ───────────────────────────────────────

        private static DashboardLayoutInput Input(float top, float bottom, params string[] states) => new DashboardLayoutInput
        {
            topReserve = top, bottomReserve = bottom,
            states = states.Length == 3 ? states : new[] { Expanded, Expanded, Expanded },
        };

        [Test]
        public void Layout_At1080p_StacksCardsOnTheRightBelowTheWallet()
        {
            // 평소: 위에는 GC 표시(88 까지), 아래는 비어 있다.
            DashboardLayoutResult layout = DashboardLayout.Compute(Input(88f, 0f));
            Assert.IsFalse(layout.compactRow);
            Assert.AreEqual(1f, layout.scale, 0.0001f);
            float y = 96f;
            for (int i = 0; i < 3; i++)
            {
                DashboardBox box = layout.boxes[i];
                Assert.AreEqual(Expanded, box.state);
                Assert.IsFalse(box.autoCollapsed);
                Assert.AreEqual(1920f - 32f - 340f, box.x, 0.01f, "오른쪽에 붙는다");
                Assert.AreEqual(340f, box.width, 0.01f);
                Assert.AreEqual(y, box.y, 0.01f);
                y += box.height + DashboardLayout.Gap;
            }
            Assert.LessOrEqual(layout.boxes[2].y + layout.boxes[2].height, 1080f - 28f);
            // 카드가 가리는 폭은 화면의 5분의 1이 안 된다.
            Assert.Less((340f + 32f) / 1920f, 0.2f);
        }

        [Test]
        public void Layout_CollapsesCardsInOrder_WhenOtherHudNeedsTheSpace()
        {
            // 사냥: 아래에 단축 칸이 있다. 전국 카드부터 접힌다.
            DashboardLayoutResult layout = DashboardLayout.Compute(Input(88f, 300f));
            CollectionAssert.AreEqual(new[] { Expanded, Expanded, Collapsed }, new[] { layout.boxes[0].state, layout.boxes[1].state, layout.boxes[2].state });
            Assert.IsTrue(layout.boxes[2].autoCollapsed);
            Assert.IsFalse(layout.boxes[0].autoCollapsed);
            Assert.LessOrEqual(layout.boxes[2].y + layout.boxes[2].height, 1080f - 300f - DashboardLayout.Gap + 0.01f);

            // 더 좁으면 계좌, 그다음에 도시. 도시 카드는 긴급 알림을 싣고 있어서 맨 나중이다.
            layout = DashboardLayout.Compute(Input(88f, 520f));
            CollectionAssert.AreEqual(new[] { Collapsed, Expanded, Collapsed }, new[] { layout.boxes[0].state, layout.boxes[1].state, layout.boxes[2].state });
            layout = DashboardLayout.Compute(Input(88f, 760f));
            CollectionAssert.AreEqual(new[] { Collapsed, Collapsed, Collapsed }, new[] { layout.boxes[0].state, layout.boxes[1].state, layout.boxes[2].state });
            Assert.IsFalse(layout.compactRow);
            Assert.IsTrue(layout.boxes[1].autoCollapsed);

            // 이용자가 접어 둔 카드는 "자리가 모자라서 접힌 것"이 아니다.
            layout = DashboardLayout.Compute(Input(88f, 300f, Expanded, Expanded, Collapsed));
            Assert.IsFalse(layout.boxes[2].autoCollapsed);
        }

        [Test]
        public void Layout_PutsThreeCardsInOneRow_WhenEvenCollapsedCardsDoNotFit()
        {
            // 협동 방어전: 미니맵, 현황, 팀 목록이 오른쪽을 거의 다 쓴다.
            DashboardLayoutResult layout = DashboardLayout.Compute(Input(920f, 0f, Collapsed, Collapsed, Collapsed));
            Assert.IsTrue(layout.compactRow);
            float right = 0f;
            for (int i = 0; i < 3; i++)
            {
                DashboardBox box = layout.boxes[i];
                Assert.IsTrue(box.compact);
                Assert.AreEqual(Collapsed, box.state);
                Assert.AreEqual(layout.boxes[0].y, box.y, 0.01f, "한 줄이다");
                Assert.AreEqual(DashboardLayout.HeaderHeight, box.height, 0.01f);
                if (i > 0) Assert.GreaterOrEqual(box.x, right, "겹치지 않는다");
                right = box.x + box.width;
            }
            Assert.AreEqual(1920f - 32f, right, 0.01f);
            Assert.AreEqual(1920f - 32f - 340f, layout.boxes[0].x, 0.01f);
            Assert.GreaterOrEqual(layout.boxes[0].y, 928f - 0.01f, "다른 HUD 아래에 놓인다");
            Assert.LessOrEqual(layout.boxes[0].y + DashboardLayout.HeaderHeight, 1080f - 28f + 0.01f);

            // 한 장만 보이면 줄을 나눌 까닭이 없다.
            layout = DashboardLayout.Compute(Input(920f, 0f, Hidden, Collapsed, Hidden));
            Assert.IsFalse(layout.compactRow);
            Assert.AreEqual(340f, layout.boxes[1].width, 0.01f);
        }

        [Test]
        public void Layout_HiddenCardsTakeNoSpace_AndLeftDockMirrorsTheColumn()
        {
            DashboardLayoutResult layout = DashboardLayout.Compute(Input(88f, 0f, Hidden, Expanded, Collapsed));
            Assert.IsFalse(layout.boxes[0].Shown);
            Assert.AreEqual(0f, layout.boxes[0].height);
            Assert.AreEqual(96f, layout.boxes[1].y, 0.01f, "숨긴 카드의 자리를 다음 카드가 쓴다");
            Assert.AreEqual(96f + layout.boxes[1].height + DashboardLayout.Gap, layout.boxes[2].y, 0.01f);

            DashboardLayoutInput input = Input(220f, 86f, Expanded, Collapsed, Collapsed);
            input.dockLeft = true;
            layout = DashboardLayout.Compute(input);
            for (int i = 0; i < 3; i++) Assert.AreEqual(32f, layout.boxes[i].x, 0.01f);
            Assert.AreEqual(228f, layout.boxes[0].y, 0.01f);

            // 모두 숨기면 아무것도 놓이지 않는다.
            layout = DashboardLayout.Compute(Input(88f, 0f, Hidden, Hidden, Hidden));
            for (int i = 0; i < 3; i++) Assert.IsFalse(layout.boxes[i].Shown);
        }

        private static readonly int[][] Resolutions =
        {
            new[] { 1280, 720 }, new[] { 1366, 768 }, new[] { 1600, 900 }, new[] { 1920, 1080 }, new[] { 2560, 1440 }, new[] { 3840, 2160 },
            new[] { 2560, 1080 }, new[] { 3440, 1440 }, new[] { 1920, 1200 }, new[] { 1280, 1024 }, new[] { 1024, 768 },
        };

        /// <summary>[위에서 쓰는 높이, 아래에서 쓰는 높이]: 평소, 사냥, FPS, 혼자 하는 방어전, 협동 방어전.</summary>
        private static readonly float[][] Reserves =
        {
            new[] { 88f, 0f }, new[] { 88f, 300f }, new[] { 332f, 216f }, new[] { 300f, 0f }, new[] { 780f, 0f }, new[] { 940f, 60f },
        };

        [Test]
        public void Layout_KeepsItsPromises_AtEveryResolutionScaleAndHudSituation()
        {
            string[][] stateSets =
            {
                new[] { Expanded, Expanded, Expanded }, new[] { Expanded, Expanded, Collapsed }, new[] { Collapsed, Collapsed, Collapsed },
                new[] { Hidden, Expanded, Expanded }, new[] { Expanded, Hidden, Collapsed },
            };
            int checkedCount = 0;
            foreach (int[] resolution in Resolutions)
            {
                DashboardLayout.CanvasSize(resolution[0], resolution[1], out float width, out float height);
                foreach (int scale in new[] { 80, 100, 120, 140 })
                foreach (float[] reserve in Reserves)
                foreach (string[] states in stateSets)
                foreach (bool left in new[] { false, true })
                {
                    string where = $"{resolution[0]}×{resolution[1]} 배율 {scale} 위 {reserve[0]} 아래 {reserve[1]} [{string.Join(",", states)}] {(left ? "왼쪽" : "오른쪽")}";
                    var input = new DashboardLayoutInput
                    {
                        canvasWidth = width, canvasHeight = height, uiScalePercent = scale, dockLeft = left,
                        topReserve = reserve[0], bottomReserve = reserve[1], states = (string[])states.Clone(),
                    };
                    DashboardLayoutResult layout = DashboardLayout.Compute(input);
                    Assert.LessOrEqual(DashboardLayout.CardWidth * layout.scale, width * DashboardLayout.MaxWidthRatio + 0.01f, where + ": 폭이 화면의 24% 를 넘는다");
                    Assert.LessOrEqual(layout.scale, scale / 100f + 0.0001f, where);

                    float previousBottom = float.NegativeInfinity;
                    float previousRight = float.NegativeInfinity;
                    for (int i = 0; i < 3; i++)
                    {
                        DashboardBox box = layout.boxes[i];
                        // 이용자가 숨긴 카드는 어떤 경우에도 나타나지 않고, 숨기지 않은 카드는 사라지지 않는다.
                        Assert.AreEqual(states[i] == Hidden, !box.Shown, where + ": 카드 " + i);
                        if (!box.Shown) continue;
                        // 펼친 적이 없는 카드가 저절로 펼쳐지지 않는다.
                        if (states[i] == Collapsed) Assert.AreEqual(Collapsed, box.state, where);
                        Assert.GreaterOrEqual(box.x, DashboardLayout.Margin - 0.01f, where + ": 왼쪽으로 넘친다");
                        Assert.LessOrEqual(box.x + box.width, width - DashboardLayout.Margin + 0.01f, where + ": 오른쪽으로 넘친다");
                        Assert.GreaterOrEqual(box.y, DashboardLayout.EdgeMargin - 0.01f, where + ": 위로 넘친다");
                        Assert.LessOrEqual(box.y + box.height, height - DashboardLayout.EdgeMargin + 0.01f, where + ": 아래로 넘친다");
                        if (layout.compactRow)
                        {
                            Assert.GreaterOrEqual(box.x, previousRight - 0.01f, where + ": 옆 카드와 겹친다");
                            previousRight = box.x + box.width;
                        }
                        else
                        {
                            Assert.GreaterOrEqual(box.y, previousBottom - 0.01f, where + ": 위 카드와 겹친다");
                            previousBottom = box.y + box.height;
                            // 다른 HUD 가 쓰는 자리를 침범하지 않는다.
                            Assert.GreaterOrEqual(box.y, reserve[0] + DashboardLayout.Gap - 0.01f, where + ": 위쪽 HUD 와 겹친다");
                            Assert.LessOrEqual(box.y + box.height, height - reserve[1] - DashboardLayout.Gap + 0.01f, where + ": 아래쪽 HUD 와 겹친다");
                        }
                    }
                    checkedCount++;
                }
            }
            Assert.AreEqual(Resolutions.Length * 4 * Reserves.Length * 5 * 2, checkedCount);

            // 16:9 는 해상도가 달라도 캔버스가 같다 (같은 자리에 같은 크기로 보인다).
            DashboardLayout.CanvasSize(1280, 720, out float w720, out float h720);
            DashboardLayout.CanvasSize(3840, 2160, out float w4k, out float h4k);
            Assert.AreEqual(1920f, w720, 0.01f);
            Assert.AreEqual(1080f, h720, 0.01f);
            Assert.AreEqual(1920f, w4k, 0.01f);
            Assert.AreEqual(1080f, h4k, 0.01f);
            // 4:3 처럼 좁은 화면에서는 큰 배율이 화면의 24% 에 맞춰 줄어든다.
            DashboardLayout.CanvasSize(1024, 768, out float w43, out float h43);
            var narrow = new DashboardLayoutInput { canvasWidth = w43, canvasHeight = h43, uiScalePercent = 140, topReserve = 88f };
            Assert.Less(DashboardLayout.Compute(narrow).scale, 1.4f);
        }

        [Test]
        public void Animation_MovesTowardTheTarget_WithoutOvershooting_AndStops()
        {
            float value = 0f;
            float previous = 0f;
            int frames = 0;
            while (value != 240f && frames < 200)
            {
                value = DashboardEase.Step(value, 240f, 1f / 60f);
                Assert.GreaterOrEqual(value, previous);
                Assert.LessOrEqual(value, 240f);
                previous = value;
                frames++;
            }
            Assert.AreEqual(240f, value, "끝에 닿으면 정확히 그 값이 된다 (그 뒤로는 다시 그리지 않는다)");
            Assert.Less(frames, 60, "1초 안에 끝난다");
            Assert.Greater(frames, 5, "한 번에 건너뛰지 않는다");
            // 프레임이 크게 밀리면 곧바로 끝낸다. 시간이 흐르지 않으면 움직이지 않는다.
            Assert.AreEqual(44f, DashboardEase.Step(240f, 44f, 2f));
            Assert.AreEqual(100f, DashboardEase.Step(100f, 44f, 0f));
            Assert.AreEqual(44f, DashboardEase.Step(44.1f, 44f, 0.016f));
        }

        [Test]
        public void CardAnimation_AppearsInPlace_GlidesToNewPlaces_AndFadesOutWhenHidden()
        {
            var animation = new DashboardAnimationController();
            var box = new DashboardBox { x = 1548f, y = 96f, width = 340f, height = 240f, state = Expanded };

            // 처음에는 날아오지 않고 제자리에 나타난다.
            animation.SetTarget(box);
            Assert.AreEqual(96f, animation.Y);
            Assert.AreEqual(1f, animation.Alpha);
            Assert.IsTrue(animation.Step(1f / 60f));
            Assert.IsFalse(animation.Moving);
            Assert.IsFalse(animation.Step(1f / 60f), "멈춘 뒤에는 아무것도 다시 그리지 않는다");

            // 접으면 높이가 줄어들며 따라간다.
            box.height = 44f;
            box.state = Collapsed;
            animation.SetTarget(box);
            animation.Step(1f / 60f);
            Assert.That(animation.Height, Is.InRange(44.01f, 239.99f));
            int frames = 1;
            while (animation.Moving && frames < 120)
            {
                animation.Step(1f / 60f);
                frames++;
            }
            Assert.AreEqual(44f, animation.Height);
            Assert.Less(frames, 60);

            // 숨기면 그 자리에서 옅어지고, 다 옅어지면 "사라졌다"고 알린다.
            box.state = Hidden;
            animation.SetTarget(box);
            Assert.IsFalse(animation.Gone);
            while (animation.Moving) animation.Step(1f / 60f);
            Assert.IsTrue(animation.Gone);
            Assert.AreEqual(0f, animation.Alpha);
            Assert.AreEqual(44f, animation.Height, "사라지는 동안 크기는 그대로다");

            // 다른 자리에 다시 나타날 때는 그 자리에서 떠오른다.
            box = new DashboardBox { x = 32f, y = 300f, width = 340f, height = 44f, state = Collapsed };
            animation.SetTarget(box);
            Assert.AreEqual(32f, animation.X);
            Assert.AreEqual(300f, animation.Y);
            Assert.AreEqual(0f, animation.Alpha);
            while (animation.Moving) animation.Step(1f / 60f);
            Assert.AreEqual(1f, animation.Alpha);

            // 해상도가 바뀌었을 때처럼 곧바로 옮겨야 하면 건너뛴다.
            box.y = 500f;
            animation.SetTarget(box, true);
            Assert.AreEqual(500f, animation.Y);
        }

        [Test]
        public void Layout_ColumnIsTheSameOneTheCardsUse()
        {
            foreach (bool left in new[] { false, true })
            foreach (int scale in new[] { 80, 100, 140 })
            {
                var input = new DashboardLayoutInput { uiScalePercent = scale, dockLeft = left, topReserve = 88f };
                DashboardLayout.Column(input, out float x, out float width);
                DashboardLayoutResult layout = DashboardLayout.Compute(input);
                Assert.AreEqual(layout.boxes[0].x, x, 0.001f);
                Assert.AreEqual(layout.boxes[0].width, width, 0.001f);
                Assert.AreEqual(layout.scale, DashboardLayout.ScaleOf(input), 0.0001f);
            }
        }

        // ── 데이터 갱신 ─────────────────────────────────────

        [Test]
        public void Refresh_FollowsTheInterval_BringsEventsForward_AndBacksOffOnFailure()
        {
            var policy = new DashboardRefreshPolicy();
            policy.Configure(20);
            Assert.IsTrue(policy.ShouldRequest(0), "처음에는 곧바로 받는다");
            policy.Started(0);
            Assert.IsFalse(policy.ShouldRequest(0.5), "답을 기다리는 동안에는 또 보내지 않는다");
            policy.Succeeded(0.5);
            Assert.IsFalse(policy.ShouldRequest(20.4));
            Assert.IsTrue(policy.ShouldRequest(20.5));

            // 값이 바뀌는 일: 곧 다시 받는다. 다만 지난 요청에서 2초는 떨어뜨린다.
            policy.Nudge(0.6);
            Assert.IsFalse(policy.ShouldRequest(1.9));
            Assert.IsTrue(policy.ShouldRequest(2.0));
            policy.Started(2.0);
            policy.Succeeded(2.1);
            // 일이 잇달아 일어나도 요청은 한 번이다.
            for (int i = 0; i < 100; i++) policy.Nudge(10.0 + i * 0.001);
            Assert.IsFalse(policy.ShouldRequest(10.2));
            Assert.IsTrue(policy.ShouldRequest(10.4));
            // 앞당기기만 한다. 이미 가까운 요청을 뒤로 미루지 않는다.
            policy.Started(10.4);
            policy.Succeeded(10.5);
            double next = policy.NextAt;
            policy.Nudge(30.4);
            Assert.AreEqual(next, policy.NextAt);

            // 실패하면 간격이 두 배씩 늘고, 2분을 넘지 않는다. 성공하면 원래대로.
            double now = 100;
            var delays = new List<double>();
            for (int i = 0; i < 6; i++)
            {
                policy.Started(now);
                policy.Failed(now);
                delays.Add(policy.NextAt - now);
                now = policy.NextAt;
            }
            CollectionAssert.AreEqual(new[] { 20.0, 40.0, 80.0, 120.0, 120.0, 120.0 }, delays);
            Assert.AreEqual(6, policy.Failures);
            policy.Started(now);
            policy.Succeeded(now);
            Assert.AreEqual(0, policy.Failures);
            Assert.AreEqual(now + 20, policy.NextAt);

            // 간격은 5초 ~ 300초 안으로 당겨진다.
            policy.Configure(0);
            Assert.AreEqual(5, policy.IntervalSeconds);
            policy.Configure(99999);
            Assert.AreEqual(300, policy.IntervalSeconds);
        }

        [Test]
        public void DataService_AsksOnlyAtTheInterval_NeverEveryFrame()
        {
            LocalMockBackend backend = _kit.NewBackend(CityKit.QuietDefs());
            MockKit.Boot(backend);
            var data = new DashboardDataService(backend, _kit.Clock);
            int updates = 0;
            data.Updated += _ => updates++;

            // 10분을 초당 60프레임으로 돈다.
            const int frames = 600 * 60;
            for (int frame = 0; frame < frames; frame++) data.Tick(frame / 60.0);
            Assert.AreEqual(30, data.RequestCount, "20초에 한 번");
            Assert.AreEqual(30, updates);
            Assert.AreEqual(DashboardConnection.Live, data.Connection);

            // 관리자가 간격을 바꾸면 다음 요약부터 따른다.
            backend.DashboardConfig.refreshSeconds = 60;
            int before = data.RequestCount;
            for (int frame = frames; frame < frames * 2; frame++) data.Tick(frame / 60.0);
            Assert.That(data.RequestCount - before, Is.InRange(10, 11));

            // 멈춘 뒤에는 묻지 않는다.
            data.Stop();
            before = data.RequestCount;
            data.Tick(99999);
            Assert.AreEqual(before, data.RequestCount);
        }

        [Test]
        public void DataService_RefreshesSoonAfterFinanceChanges()
        {
            LocalMockBackend backend = _kit.NewBackend(CityKit.QuietDefs());
            MockKit.Boot(backend);
            var data = new DashboardDataService(backend, _kit.Clock);
            data.Tick(0);
            long before = data.Summary.finance.gc;

            _kit.Earn(backend, 500);
            MockKit.Ok(MockKit.Deposit(backend, 200, "req-dash-0001"));
            // 아직 다시 받지 않았다: 카드의 값은 서버가 준 그대로다 (클라이언트가 고쳐 쓰지 않는다).
            Assert.AreEqual(before, data.Summary.finance.gc);

            data.Tick(1.0);
            data.Nudge();
            data.Tick(1.5);
            Assert.AreEqual(1, data.RequestCount);
            data.Tick(2.0);
            Assert.AreEqual(2, data.RequestCount);
            DashboardFinanceDto finance = data.Summary.finance;
            Assert.AreEqual(before + 300, finance.gc);
            Assert.AreEqual(200, finance.bankGc);
            Assert.AreEqual(before + 500, finance.netWorthGc);
        }

        [Test]
        public void DataService_KeepsTheLastSummaryWhenOffline_AndSaysSince()
        {
            LocalMockBackend backend = _kit.NewBackend(CityKit.QuietDefs());
            MockKit.Boot(backend);
            var data = new DashboardDataService(backend, _kit.Clock);
            Assert.AreEqual(DashboardConnection.Loading, data.Connection);
            StringAssert.Contains("불러오는 중", data.StatusLine());

            data.Tick(0);
            Assert.AreEqual(DashboardConnection.Live, data.Connection);
            StringAssert.StartsWith("● 실시간", data.StatusLine());
            DashboardSummaryDto last = data.Summary;
            int version = data.Version;

            backend.DashboardOffline = true;
            _kit.Clock.Advance(150);
            data.Tick(20);
            Assert.AreEqual(DashboardConnection.Offline, data.Connection);
            Assert.AreSame(last, data.Summary, "마지막으로 받은 값을 그대로 보여 준다");
            Assert.Greater(data.Version, version, "연결 상태가 바뀐 것을 화면이 안다");
            StringAssert.StartsWith("▲ 연결 끊김 · 마지막 갱신", data.StatusLine());
            StringAssert.Contains("2분 전", data.StatusLine());
            Assert.AreEqual(ApiErrors.StorageFailed, data.LastErrorCode);

            // 끊긴 동안에는 간격을 늘려 가며 다시 묻는다.
            int requests = data.RequestCount;
            for (int t = 21; t < 200; t++) data.Tick(t);
            Assert.That(data.RequestCount - requests, Is.InRange(2, 3));

            // 다시 이어지면 원래대로.
            backend.DashboardOffline = false;
            for (int t = 200; t < 400; t++) data.Tick(t);
            Assert.AreEqual(DashboardConnection.Live, data.Connection);
            Assert.IsNull(data.LastErrorCode);
            Assert.AreNotSame(last, data.Summary);

            // 한 번도 받지 못한 채 끊긴 경우.
            backend.DashboardOffline = true;
            var cold = new DashboardDataService(backend, _kit.Clock);
            cold.Tick(0);
            Assert.AreEqual(DashboardConnection.Offline, cold.Connection);
            Assert.IsNull(cold.Summary);
            StringAssert.Contains("아직 받지 못했어요", cold.StatusLine());
        }

        // ── 모의 서버의 요약 ────────────────────────────────

        [Test]
        public void Summary_ForAStranger_HasFinanceAndNationalButNoCity()
        {
            LocalMockBackend backend = _kit.NewBackend(CityKit.QuietDefs());
            MeDto me = MockKit.Boot(backend);
            DashboardSummaryDto summary = Summary(backend);

            Assert.AreEqual(me.gc, summary.finance.gc);
            Assert.IsFalse(summary.myCity.resident);
            Assert.IsNull(summary.myCity.regionCode);
            Assert.IsFalse(summary.myCity.alert);
            Assert.AreEqual(0, summary.national.homeRank);
            Assert.AreEqual(CityKit.Risks(backend).cities.Count, summary.national.totalCities);
            Assert.AreEqual(5, summary.national.top.Count);
            Assert.IsNotEmpty(summary.updatedAt);
            Assert.AreEqual(0, summary.notifications.Count);

            // 접속하지 않았으면 받지 못한다.
            ApiResult<DashboardSummaryDto> result = null;
            _kit.NewBackend().GetDashboardSummary(r => result = r);
            Assert.AreEqual(ApiErrors.Unauthorized, MockKit.Error(result));
        }

        [Test]
        public void Summary_Finance_ComesFromTheWalletAndTheBank_AndInventsNothing()
        {
            LocalMockBackend backend = _kit.NewBackend(CityKit.QuietDefs());
            MockKit.Boot(backend);
            _kit.Earn(backend, 1000);
            _kit.Clock.AddDays(2);
            _kit.Earn(backend, 500);
            MockKit.Ok(MockKit.Deposit(backend, 400, "req-dash-0002"));
            _kit.Clock.AddDays(1);

            int ledger = backend.CurrentLedger.Count;
            DashboardFinanceDto finance = Summary(backend).finance;
            BankDto bank = MockKit.Bank(backend);
            Assert.AreEqual(bank.gc, finance.gc);
            Assert.IsTrue(finance.bankAvailable);
            Assert.AreEqual(bank.bankGc, finance.bankGc);
            Assert.AreEqual(bank.termGc, finance.termGc);
            // 가상 주식과 대출은 없다. 0 이 아니라 "없다".
            Assert.IsFalse(finance.stocksAvailable);
            Assert.IsFalse(finance.loansAvailable);
            Assert.AreEqual(0, finance.stockValueGc + finance.stockProfitGc + finance.loanBalanceGc);
            Assert.AreEqual(bank.gc + bank.bankGc + bank.termGc, finance.netWorthGc);
            Assert.IsTrue(finance.netWorthPartial);

            // 자산 변동: 7일, 오래된 날이 앞, 마지막은 지금의 잔액.
            Assert.AreEqual(7, finance.trend.Count);
            for (int i = 1; i < 7; i++) Assert.Less(string.CompareOrdinal(finance.trend[i - 1].date, finance.trend[i].date), 0);
            Assert.AreEqual(finance.gc, finance.trend[6].gc);
            long start = finance.trend[2].gc;
            Assert.AreEqual(start + 1000, finance.trend[3].gc, "사흘 전에 1000 GC 를 벌었다");
            Assert.AreEqual(finance.trend[3].gc, finance.trend[4].gc);
            Assert.AreEqual(start + 1000 + 500 - 400, finance.trend[5].gc, "하루 전에 500 을 벌고 400 을 맡겼다");

            // 요약을 받아도 원장에 줄이 늘지 않는다.
            Summary(backend);
            Assert.AreEqual(ledger, backend.CurrentLedger.Count);
            Assert.IsNull(backend.FindLedgerInconsistency());
        }

        [Test]
        public void Summary_MyCityAndNational_MatchTheExistingApis()
        {
            GameDefs defs = CityKit.QuietDefs();
            defs.combat.risk.initialRisk = 40;
            LocalMockBackend backend = _kit.NewBackend(defs);
            MockKit.Boot(backend);
            CityKit.Register(backend);

            DashboardSummaryDto summary = Summary(backend);
            CityRisksDto risks = CityKit.Risks(backend);
            RankingsDto rankings = CityKit.Rankings(backend);
            CityStatusDto home = risks.Find(CityKit.Haeundae);
            CityDetailDto detail = CityKit.Detail(backend, CityKit.Haeundae);

            DashboardCityDto city = summary.myCity;
            Assert.IsTrue(city.resident);
            Assert.AreEqual(CityKit.Haeundae, city.regionCode);
            Assert.IsNotEmpty(city.name);
            Assert.AreEqual(home.risk, city.risk);
            Assert.AreEqual(home.stage, city.stage);
            Assert.AreEqual(RiskStages.Caution, city.stage);
            Assert.AreEqual(home.defensePower, city.defensePower);
            Assert.AreEqual(home.rank, city.rank);
            Assert.AreEqual(detail.myContribution, city.myContribution);
            Assert.IsNull(city.invasionKind);
            Assert.IsFalse(city.alert, "위험도 40 은 기준(70)보다 낮다");

            DashboardNationalDto national = summary.national;
            long total = 0;
            int emergency = 0;
            foreach (CityStatusDto row in risks.cities)
            {
                total += row.risk;
                if (row.stage == RiskStages.Emergency) emergency++;
            }
            Assert.AreEqual(risks.cities.Count, national.totalCities);
            Assert.AreEqual((int)Math.Round(100.0 - (double)total / risks.cities.Count, MidpointRounding.AwayFromZero), national.averageSafety);
            Assert.AreEqual(emergency, national.emergencyCities);
            Assert.AreEqual(rankings.home.rank, national.homeRank);
            for (int i = 0; i < 5; i++)
            {
                Assert.AreEqual(rankings.rows[i].regionCode, national.top[i].regionCode);
                Assert.AreEqual(rankings.rows[i].score, national.top[i].score);
                Assert.IsNotEmpty(national.top[i].name);
            }
        }

        [Test]
        public void Summary_AlertsOnInvasionEmergencyAndTheAdminThreshold()
        {
            // 관리자가 정한 위험도 기준.
            GameDefs defs = CityKit.QuietDefs();
            defs.combat.risk.initialRisk = 60;
            LocalMockBackend backend = _kit.NewBackend(defs);
            MockKit.Boot(backend);
            CityKit.Register(backend);
            Assert.IsFalse(Summary(backend).myCity.alert);
            backend.DashboardConfig.cityAlertRisk = 60;
            DashboardSummaryDto summary = Summary(backend);
            Assert.IsTrue(summary.myCity.alert);
            Assert.AreEqual(DashboardAlertReasons.Risk, summary.myCity.alertReason);
            Assert.AreEqual("city_risk", summary.notifications[0].kind);
            Assert.AreEqual(DashboardNoticeLevels.Warn, summary.notifications[0].level);
            StringAssert.Contains("위험도가 높아요", DashboardViews.CityAlertText(summary.myCity));

            // 침공.
            var invadedKit = new MockKit();
            LocalMockBackend invaded = invadedKit.NewBackend(CityKit.InvadedDefs());
            MockKit.Boot(invaded);
            CityKit.Register(invaded);
            invadedKit.Clock.Advance(3600);
            summary = Summary(invaded);
            Assert.IsTrue(summary.myCity.HasInvasion);
            Assert.AreEqual(DashboardAlertReasons.Invasion, summary.myCity.alertReason);
            DashboardNoticeDto first = summary.notifications[0];
            Assert.AreEqual("city_invasion", first.kind);
            Assert.AreEqual(DashboardNoticeLevels.Urgent, first.level);
            Assert.IsTrue(first.IsCity);
            StringAssert.StartsWith("city_invasion:", first.id);
            Assert.Greater(summary.national.invasionCities, 0);
            Assert.Greater(summary.national.urgent.Count, 0);
            StringAssert.Contains("도시를 지켜 주세요", DashboardViews.CityAlertText(summary.myCity));
            // 같은 일에는 같은 id.
            Assert.AreEqual(first.id, Summary(invaded).notifications[0].id);

            // 전국 알림 정책: 0 이면 다른 도시의 알림을 보내지 않는다. 2 이면 침공 중인 도시도 알린다.
            invaded.DashboardConfig.nationalNotice = 0;
            foreach (DashboardNoticeDto notice in Summary(invaded).notifications) Assert.IsTrue(notice.IsCity);
            invaded.DashboardConfig.nationalNotice = 2;
            int national = 0;
            foreach (DashboardNoticeDto notice in Summary(invaded).notifications)
            {
                if (notice.IsCity) continue;
                national++;
                Assert.AreNotEqual(CityKit.Haeundae, notice.regionCode, "우리 도시의 일은 전국 알림으로 다시 나오지 않는다");
            }
            Assert.That(national, Is.InRange(1, 3));
        }

        // ── 알림 ────────────────────────────────────────────

        private static DashboardNoticeDto Notice(string id, string kind, string level) =>
            new DashboardNoticeDto { id = id, kind = kind, level = level, regionCode = "26350", title = id };

        [Test]
        public void Notifications_AreAnnouncedOnce_AndCityAlertsReachThePlayerInBattle()
        {
            var service = new DashboardNotificationService();
            var summary = new DashboardSummaryDto();
            summary.notifications.Add(Notice("city_risk:26350:2026-11-14", "city_risk", DashboardNoticeLevels.Warn));
            summary.notifications.Add(Notice("city_invasion:inv_7", "city_invasion", DashboardNoticeLevels.Urgent));
            summary.notifications.Add(Notice("national_emergency:11140:2026-11-14", "national_emergency", DashboardNoticeLevels.Warn));
            summary.notifications.Add(Notice("city_support:sup_3", "city_support", DashboardNoticeLevels.Info));
            summary.notifications.Add(new DashboardNoticeDto { id = "", kind = "city_risk" });

            Assert.AreEqual(4, service.Accept(summary).Count, "처음 보는 알림");
            Assert.AreEqual(0, service.Accept(summary).Count, "다시 받아도 또 알리지 않는다");
            Assert.AreEqual(4, service.Active.Count);
            Assert.AreEqual("city_invasion:inv_7", service.CityAlert.id, "우리 도시의 알림 가운데 가장 급한 것");
            Assert.AreEqual(1, service.NationalCount);

            // 새 침공만 새 알림이다.
            summary.notifications.Add(Notice("city_invasion:inv_8", "city_invasion", DashboardNoticeLevels.Urgent));
            IReadOnlyList<DashboardNoticeDto> fresh = service.Accept(summary);
            Assert.AreEqual(1, fresh.Count);
            Assert.AreEqual("city_invasion:inv_8", fresh[0].id);

            // 끝난 일은 살아 있는 알림에서 빠진다.
            Assert.AreEqual(0, service.Accept(new DashboardSummaryDto()).Count);
            Assert.AreEqual(0, service.Active.Count);
            Assert.IsNull(service.CityAlert);
            Assert.AreEqual(0, service.Accept(null).Count);

            // 한 줄 알림: 우리 도시의 경고·긴급은 전투 중에도, 다른 도시의 것은 전투 밖에서만, 참고용은 띄우지 않는다.
            DashboardNoticeDto cityUrgent = Notice("a", "city_invasion", DashboardNoticeLevels.Urgent);
            DashboardNoticeDto cityWarn = Notice("b", "city_risk", DashboardNoticeLevels.Warn);
            DashboardNoticeDto nationalWarn = Notice("c", "national_emergency", DashboardNoticeLevels.Warn);
            DashboardNoticeDto info = Notice("d", "city_support", DashboardNoticeLevels.Info);
            Assert.IsTrue(DashboardNotificationService.ShouldToast(cityUrgent, true));
            Assert.IsTrue(DashboardNotificationService.ShouldToast(cityWarn, true));
            Assert.IsFalse(DashboardNotificationService.ShouldToast(nationalWarn, true));
            Assert.IsTrue(DashboardNotificationService.ShouldToast(nationalWarn, false));
            Assert.IsFalse(DashboardNotificationService.ShouldToast(info, false));
            Assert.IsFalse(DashboardNotificationService.ShouldToast(null, false));

            // 기억은 한없이 늘지 않는다.
            var many = new DashboardSummaryDto();
            for (int i = 0; i < 200; i++) many.notifications.Add(Notice("city_risk:" + i, "city_risk", DashboardNoticeLevels.Warn));
            Assert.AreEqual(200, service.Accept(many).Count);
            Assert.AreEqual(200 - DashboardNotificationService.SeenLimit, service.Accept(many).Count, "가장 최근의 64개만 기억한다");
        }

        // ── 카드에 쓰는 글자 ────────────────────────────────

        [Test]
        public void FinanceCard_MarksSamplesAndNeverAddsThemToNetWorth()
        {
            var finance = new DashboardFinanceDto { gc = 1200, bankAvailable = true, bankGc = 300, termGc = 500, netWorthGc = 2000, netWorthPartial = true };

            List<DashboardRow> rows = DashboardViews.Finance(finance, true);
            CollectionAssert.AreEqual(new[] { "보유 GC", "은행 예금", "가상 주식 평가액", "평가손익", "대출 잔액", "순자산 (있는 항목만)" },
                rows.ConvertAll(r => r.label));
            Assert.AreEqual("1,200 GC", rows[0].value);
            Assert.AreEqual("800 GC", rows[1].value, "통장과 정기예금");
            Assert.IsNull(rows[0].tag);
            Assert.IsNull(rows[1].tag);
            // 서버에 없는 항목은 샘플이라는 꼬리표가 붙는다.
            for (int i = 2; i <= 4; i++) Assert.AreEqual(DashboardSamples.Tag, rows[i].tag, rows[i].label);
            Assert.AreEqual(DashboardFormat.Gc(DashboardSamples.StockValue), rows[2].value);
            // 순자산은 서버가 준 값 그대로다. 샘플은 더해지지 않는다.
            Assert.AreEqual("2,000 GC", rows[5].value);
            Assert.IsTrue(rows[5].strong);
            Assert.IsNull(rows[5].tag);
            Assert.AreEqual("순자산 2,000 GC", DashboardViews.FinanceKey(finance, false));
            Assert.AreEqual("GC 2,000", DashboardViews.FinanceKey(finance, true));

            // 샘플을 허용하지 않으면 "준비 중"이고 숫자가 없다.
            rows = DashboardViews.Finance(finance, false);
            for (int i = 2; i <= 4; i++)
            {
                Assert.AreEqual(DashboardSamples.NotReadyTag, rows[i].tag);
                Assert.AreEqual("—", rows[i].value);
            }
            StringAssert.Contains("준비 중", DashboardViews.StocksText(false));
            StringAssert.Contains("준비 중", DashboardViews.LoanText(false));
            StringAssert.Contains("준비 중", DashboardViews.StockTradesText(false));
            foreach (string text in new[] { DashboardViews.StocksText(true), DashboardViews.StockTradesText(true), DashboardViews.LoanText(true) })
                StringAssert.StartsWith("※ 개발용 샘플이에요. 실제 데이터가 아니고", text);

            // 서버가 실제 값을 주기 시작하면 꼬리표가 사라지고 실제 값이 보인다.
            finance.stocksAvailable = true;
            finance.stockValueGc = 4000;
            finance.stockProfitGc = -250;
            finance.loansAvailable = true;
            finance.loanBalanceGc = 900;
            finance.netWorthPartial = false;
            rows = DashboardViews.Finance(finance, true);
            Assert.AreEqual("4,000 GC", rows[2].value);
            Assert.AreEqual("▼ −250 GC", rows[3].value);
            Assert.AreEqual(DashboardRow.ToneBad, rows[3].tone);
            Assert.AreEqual("900 GC", rows[4].value);
            Assert.AreEqual("순자산", rows[5].label);
            for (int i = 0; i < rows.Count; i++) Assert.IsNull(rows[i].tag);

            // 샘플은 누구에게나 같은 값이고, 합계가 맞는다.
            Assert.AreEqual(12 * 1150 + 5 * 2260 + 8 * 905, DashboardSamples.StockValue);
            Assert.AreEqual(12 * 150 - 5 * 140 + 8 * 155, DashboardSamples.StockProfit);
            Assert.AreEqual(3000, DashboardSamples.LoanBalance);
        }

        [Test]
        public void CityAndNationalCards_ShowRiskWithShapeAndWord_NotOnlyColor()
        {
            var city = new DashboardCityDto
            {
                resident = true, regionCode = "26350", name = "해운대구", risk = 82, stage = RiskStages.Emergency, defensePower = 1234,
                validResidents = 57, treasuryGc = 45000, invasionKind = InvasionKinds.Zero, invasionProgress = 3, invasionRequired = 10,
                myContribution = 420, alert = true, alertReason = DashboardAlertReasons.Invasion, growthStage = 2, growthStageName = "상징물 제작 검토",
                modelingStatus = ModelingStatuses.Modeling,
            };
            List<DashboardRow> rows = DashboardViews.City(city);
            CollectionAssert.AreEqual(new[] { "위험도", "방어력", "유효 전입자", "도시 재정", "침공", "내 기여도" }, rows.ConvertAll(r => r.label));
            Assert.AreEqual("82  ■ 긴급", rows[0].value);
            Assert.AreEqual(82, rows[0].bar);
            Assert.AreEqual(RiskStages.Emergency, rows[0].stage);
            Assert.AreEqual("1,234", rows[1].value);
            Assert.AreEqual("57명", rows[2].value);
            Assert.AreEqual("45,000 GC", rows[3].value);
            Assert.AreEqual("■ ZERO 이벤트  3 / 10", rows[4].value);
            Assert.AreEqual(DashboardRow.ToneBad, rows[4].tone);
            Assert.AreEqual("위험도 82  ■ 긴급", DashboardViews.CityKey(city, false));
            Assert.AreEqual("■ 82", DashboardViews.CityKey(city, true));
            StringAssert.Contains("ZERO 이벤트", DashboardViews.CityAlertText(city));
            // 접혀 있어도 침공은 머리 줄에 보인다.
            Assert.AreEqual("■ ZERO 이벤트 · 위험도 82", DashboardViews.CityHeader(city, false));
            Assert.AreEqual("■ 침공", DashboardViews.CityHeader(city, true));
            StringAssert.Contains("2단계 · 상징물 제작 검토", DashboardViews.GrowthText(city));
            StringAssert.Contains("제작 중", DashboardViews.GrowthText(city));

            // 단계마다 모양과 낱말이 다르다 (색을 보지 않아도 구분된다).
            var badges = new HashSet<string>();
            foreach (string stage in RiskStages.All) Assert.IsTrue(badges.Add(DashboardViews.StageBadge(stage)));

            city.invasionKind = null;
            city.alertReason = DashboardAlertReasons.Emergency;
            Assert.AreEqual("침공 없음", DashboardViews.City(city)[4].value);
            Assert.AreEqual(DashboardViews.CityKey(city, false), DashboardViews.CityHeader(city, false));
            StringAssert.Contains("긴급 단계", DashboardViews.CityAlertText(city));
            city.alert = false;
            Assert.IsNull(DashboardViews.CityAlertText(city));
            Assert.AreEqual("전입신고 전", DashboardViews.CityKey(new DashboardCityDto(), false));

            var national = new DashboardNationalDto { totalCities = 229, averageSafety = 81, emergencyCities = 3, dangerCities = 7, invasionCities = 4, homeRank = 12, supportRequests = 2 };
            national.top.Add(new DashboardRankDto { regionCode = "11110", name = "서울 종로구", rank = 1, score = 913, risk = 4, stage = RiskStages.Safe });
            national.urgent.Add(new DashboardUrgentDto { regionCode = "11140", name = "서울 중구", risk = 91, stage = RiskStages.Emergency, invasionKind = InvasionKinds.Outskirts });
            rows = DashboardViews.National(national);
            CollectionAssert.AreEqual(new[] { "전국 평균 안전도", "긴급 도시", "우리 도시 순위", "1위 도시", "지원 요청" }, rows.ConvertAll(r => r.label));
            CollectionAssert.AreEqual(new[] { "81", "■ 3곳", "12위 / 229", "서울 종로구  913점", "2곳" }, rows.ConvertAll(r => r.value));
            Assert.AreEqual("■ 긴급 3곳", DashboardViews.NationalKey(national, false));
            string text = DashboardViews.NationalText(national);
            StringAssert.Contains("1위   서울 종로구   913점   ● 안전 4", text);
            StringAssert.Contains("■ 긴급   서울 중구   위험도 91   외곽 침공", text);

            national.emergencyCities = 0;
            national.homeRank = 0;
            Assert.AreEqual("● 안전도 81", DashboardViews.NationalKey(national, false));
            Assert.AreEqual("● 81", DashboardViews.NationalKey(national, true));
            Assert.AreEqual("없음", DashboardViews.National(national)[1].value);
            Assert.AreEqual("—", DashboardViews.National(national)[2].value);
        }

        [Test]
        public void Format_WritesMoneyTimeAndTrendsTheSameWayEverywhere()
        {
            Assert.AreEqual("0 GC", DashboardFormat.Gc(0));
            Assert.AreEqual("1,234,567 GC", DashboardFormat.Gc(1234567));
            Assert.AreEqual("9,999", DashboardFormat.Compact(9999));
            Assert.AreEqual("1만", DashboardFormat.Compact(10000));
            Assert.AreEqual("1.2만", DashboardFormat.Compact(12340));
            Assert.AreEqual("1.9만", DashboardFormat.Compact(19999), "올림하지 않는다");
            Assert.AreEqual("1,234만", DashboardFormat.Compact(12345678));
            Assert.AreEqual("1.2억", DashboardFormat.Compact(123456789));
            Assert.AreEqual("−1.5만", DashboardFormat.Compact(-15000));
            Assert.AreEqual("▲ +1,200 GC", DashboardFormat.SignedGc(1200));
            Assert.AreEqual("▼ −300 GC", DashboardFormat.SignedGc(-300));
            Assert.AreEqual("0 GC", DashboardFormat.SignedGc(0));

            Assert.AreEqual("방금", DashboardFormat.Ago(3));
            Assert.AreEqual("42초 전", DashboardFormat.Ago(42.9));
            Assert.AreEqual("2분 전", DashboardFormat.Ago(150));
            Assert.AreEqual("3시간 전", DashboardFormat.Ago(3 * 3600 + 5));

            var at = new DateTime(2026, 11, 14, 12, 3, 15);
            Assert.AreEqual("● 실시간 · 12:03:15 갱신", DashboardFormat.Status(DashboardConnection.Live, at, 4));
            Assert.AreEqual("▲ 연결 끊김 · 마지막 갱신 12:03:15 (2분 전)", DashboardFormat.Status(DashboardConnection.Offline, at, 125));
            Assert.AreEqual("▲ 연결 끊김 · 아직 받지 못했어요", DashboardFormat.Status(DashboardConnection.Offline, null, 0));
            Assert.AreEqual("… 불러오는 중", DashboardFormat.Status(DashboardConnection.Loading, null, 0));

            CollectionAssert.AreEqual(new[] { 1, 1, 4, 7, 7 }, DashboardFormat.Bars(new long[] { 100, 100, 600, 1100, 1100 }));
            CollectionAssert.AreEqual(new[] { 4, 4, 4 }, DashboardFormat.Bars(new long[] { 50, 50, 50 }));
            Assert.AreEqual(0, DashboardFormat.Bars(new long[0]).Length);
            Assert.AreEqual("▁▁▄▇▇", DashboardFormat.Sparkline(new long[] { 100, 100, 600, 1100, 1100 }));

            var finance = new DashboardFinanceDto();
            Assert.AreEqual("아직 기록이 없어요.", DashboardViews.TrendText(finance));
            finance.trend.Add(new DashboardTrendPointDto { date = "2026-11-13", gc = 500 });
            finance.trend.Add(new DashboardTrendPointDto { date = "2026-11-14", gc = 1700 });
            string trend = DashboardViews.TrendText(finance);
            StringAssert.Contains("11-13    500 GC", trend);
            StringAssert.Contains("11-14    1,700 GC    ▲ +1,200 GC", trend);

            // 카드마다 이름과 키가 있다.
            foreach (string id in DashboardCards.All)
            {
                Assert.IsNotEmpty(DashboardCards.Title(id));
                Assert.IsNotEmpty(DashboardCards.FullTitle(id));
            }
            CollectionAssert.AreEqual(new[] { "F7", "F8", "F9" }, new[] { DashboardCards.KeyName(F), DashboardCards.KeyName(C), DashboardCards.KeyName(N) });
            Assert.AreEqual("F6", DashboardCards.ToggleAllKeyName);
            Assert.AreEqual(-1, DashboardCards.IndexOf("weather"));
        }
    }
}
