using System;
using System.Collections;
using System.IO;
using CityGuardians.Domain;
using CityGuardians.Gameplay;
using CityGuardians.Inputs;
using CityGuardians.Services;
using CityGuardians.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace CityGuardians.Tests
{
    /// <summary>
    /// 사람이 하듯 입력을 넣어 첫 플레이를 끝까지 진행해 본다 (docs/USER_FLOW.md).
    /// 공통 준비와 도우미는 PlayTestBase에 있다.
    /// </summary>
    public sealed class FirstPlayTests : PlayTestBase
    {
        // ── 테스트 ──────────────────────────────────────────────

        [UnityTest]
        [Timeout(300000)]
        public IEnumerator FirstPlay_RoomToCity_HuntEarnGc_AndProgressSurvivesRestart()
        {
            // 1~2. 실행하면 메뉴 없이 방에서 시작한다
            yield return BootToRoom();
            GameSession session = GameSession.Current;
            string playerId = session.Me.playerId;
            Assert.AreEqual(0, session.TutorialStep);
            Assert.AreEqual(0, session.Me.gc);
            Assert.IsFalse(Hud.Instance.StatusVisible, "체력 막대는 첫 전투 전에는 보이지 않는다");
            Assert.IsFalse(Hud.Instance.WalletVisible, "GC는 첫 보상 전에는 보이지 않는다");
            Assert.IsTrue(Hud.Instance.ObjectiveVisible);
            Assert.IsFalse(Player.GetComponent<PlayerCombat>().armed, "배턴을 챙기기 전에는 공격할 수 없다");
            yield return Shot("01_room_start");

            // 3. 걷기를 배우면 컴퓨터가 울린다
            yield return WalkTo(new Vector3(1.6f, 0f, 0.6f), 0.3f, 8f, false);
            yield return Until(() => session.TutorialStep == 1, 3f, "걸은 뒤 튜토리얼 1단계(컴퓨터 경보)");
            Assert.Greater(Player.DistanceMoved, 2.4f);

            // 4. 컴퓨터에서 침공 경고를 본다
            yield return WalkTo(new Vector3(0.9f, 0f, 1.15f), 0.25f, 8f, false);
            yield return Until(() => Hud.Instance.PromptText != null, 2f, "컴퓨터 상호작용 안내");
            StringAssert.Contains("컴퓨터", Hud.Instance.PromptText);
            yield return PressInteract();
            Assert.IsTrue(Hud.Instance.PanelVisible, "침공 경고 화면이 열려야 한다");
            Assert.IsTrue(GameState.IsControlLocked);
            yield return Shot("02_room_warning");

            // 5. 경고를 닫으면 보리가 말을 걸고 배턴을 준다
            yield return PressInteract();
            Assert.IsFalse(Hud.Instance.PanelVisible);
            Assert.IsTrue(Hud.Instance.DialogueVisible, "경고를 닫으면 보리의 대화가 이어진다");
            yield return AdvanceDialogue();
            Assert.AreEqual(2, session.TutorialStep);
            Assert.IsFalse(GameState.IsControlLocked);
            yield return null;
            Assert.IsTrue(Player.GetComponent<PlayerCombat>().armed, "대화가 끝나면 배턴을 든다");

            // 6. 문으로 나가면 도시다
            yield return WalkTo(new Vector3(2.25f, 0f, -1.3f), 0.25f, 8f, false);
            yield return Until(() => Hud.Instance.PromptText != null && Hud.Instance.PromptText.Contains("나가기"), 2f, "문 상호작용 안내");
            yield return Shot("03_room_door");
            yield return PressInteract();
            yield return Until(() => SceneManager.GetActiveScene().name == SceneNames.City && Player != null && !GameRoot.Instance.IsBusy,
                40f, "도시 장면으로 이동");
            Assert.AreEqual(3, session.TutorialStep);
            Assert.IsTrue(Hud.Instance.StatusVisible, "첫 전투부터 체력 막대가 보인다");
            yield return Shot("04_city_arrive");

            // 7. 첫 전투: 도담을 위협하는 로봇 한 대
            HuntZone home = FindZone(ZoneCodes.HomeStreet);
            yield return Until(() => home.AliveCount == 1, 5f, "첫 전투용 로봇 출현");
            EnemyAgent robot = home.Alive[0];
            Assert.AreEqual(EnemyCodes.TutorialRobot, robot.Def.code);
            yield return Fight(() => session.TutorialStep >= 4, 45f, "첫 로봇 처치와 서버 확인");

            // 8. 보상: 서버가 계산한 10 GC
            Assert.AreEqual(10, session.Me.gc);
            Assert.AreEqual(0, session.PendingGc, "확정된 뒤에는 추정치가 남지 않는다");
            Assert.IsTrue(Hud.Instance.WalletVisible, "첫 보상부터 GC가 보인다");
            Assert.Greater(Player.Health.Current, 0, "첫 전투에서는 쓰러지지 않는다");
            yield return Shot("05_city_first_reward");

            // 5(NPC 대화). 도담에게 말을 걸어 감사 선물을 받는다
            yield return WalkTo(Npc("dodam").transform.position, 1.5f, 15f, true);
            yield return Until(() => Hud.Instance.PromptText != null && Hud.Instance.PromptText.Contains("말하기"), 2f, "도담 대화 안내");
            yield return PressInteract();
            Assert.IsTrue(Hud.Instance.DialogueVisible);
            Assert.IsTrue(GameState.IsControlLocked, "대화 중에는 조작이 잠긴다");
            yield return Shot("06_city_dodam_talk");
            yield return AdvanceDialogue();
            Assert.AreEqual(110, session.Me.gc, "첫 구조 임무 보상 100 GC");
            Assert.AreEqual(5, session.TutorialStep);

            // 다시 말을 걸어도 선물을 또 주지 않는다 (서버 쪽 중복 지급 방지는 EditMode 테스트가 확인한다)
            yield return PressInteract();
            Assert.IsTrue(Hud.Instance.DialogueVisible);
            yield return AdvanceDialogue();
            Assert.AreEqual(110, session.Me.gc);

            // 9. 방어대장: 전투 구역 안내와 전입신고 권유 (강요하지 않는다)
            yield return WalkTo(new Vector3(-9f, 0f, 14f), 1.0f, 20f, true);
            yield return WalkTo(Npc("hanul").transform.position, 1.7f, 20f, true);
            yield return Until(() => Hud.Instance.PromptText != null && Hud.Instance.PromptText.Contains("말하기"), 2f, "방어대장 대화 안내");
            yield return Shot("07_city_plaza");
            yield return PressInteract();
            yield return AdvanceDialogue();
            Assert.AreEqual(6, session.TutorialStep);
            Assert.AreEqual("stranger", session.Me.residency, "전입신고를 하지 않아도 계속 놀 수 있다");

            // 자유 사냥: 북동 전투 구역에서 로봇을 물리쳐 GC를 번다
            yield return WalkTo(new Vector3(31.5f, 0f, 15.5f), 1.0f, 30f, true);
            yield return Shot("08_city_zone_entrance");
            yield return WalkTo(new Vector3(31.5f, 0f, 23f), 1.0f, 15f, true);
            HuntZone northEast = FindZone(ZoneCodes.NorthEast);
            Assert.Greater(northEast.AliveCount, 0, "전투 구역에 로봇이 있어야 한다");
            yield return Fight(() => session.Me.gc >= 140, 120f, "전투 구역에서 로봇 3대 이상 처치", northEast, "09_city_hunting");
            yield return Until(() => session.PendingGc == 0, 6f, "남은 사냥 보고 확정");
            yield return Shot("10_city_after_hunt");
            yield return CameraAgainstWall();

            var backend = (LocalMockBackend)session.Backend;
            Assert.IsNull(backend.FindLedgerInconsistency(), "잔액과 원장이 맞아야 한다");
            long gc = session.Me.gc;
            int level = session.Me.level;
            int xp = session.Me.xp;
            Assert.AreEqual((gc - 100) , xp + XpSpentOnLevels(level, session.Defs),
                "사냥으로 번 GC와 경험치는 같은 수여야 한다 (로봇 1대 = 10 GC, 10 XP)");

            // 16~17. 껐다 켜도 그대로다
            GameRoot.Instance.Shutdown();
            Assert.IsNull(GameSession.Current);
            yield return BootToRoom();
            GameSession restored = GameSession.Current;
            Assert.AreNotSame(session, restored);
            Assert.AreEqual(playerId, restored.Me.playerId);
            Assert.AreEqual(gc, restored.Me.gc);
            Assert.AreEqual(level, restored.Me.level);
            Assert.AreEqual(xp, restored.Me.xp);
            Assert.AreEqual(6, restored.TutorialStep);
            Assert.AreEqual(SceneNames.Room, SceneManager.GetActiveScene().name, "다시 켜면 항상 방에서 시작한다");
            yield return null;
            Assert.IsTrue(Player.GetComponent<PlayerCombat>().armed);
            Assert.IsTrue(Hud.Instance.WalletVisible);
            yield return Shot("11_room_after_restart");
        }

        [UnityTest]
        [Timeout(120000)]
        public IEnumerator UnreadableGuestFile_AsksBeforeStartingOver_AndKeepsTheOldFile()
        {
            File.WriteAllText(Path.Combine(_storageDir, LocalMockBackend.GuestFile), "이건 읽을 수 없는 파일");

            SceneManager.LoadScene(SceneNames.Boot);
            yield return Until(() => GameRoot.Instance.BootHasFailed, 10f, "접속 실패 감지");
            yield return null;
            Assert.IsTrue(Hud.Instance.BootFailVisible, "실패를 화면에 알려야 한다");
            Assert.IsNull(GameSession.Current, "묻지 않고 새로 시작하면 안 된다");
            Assert.AreEqual("이건 읽을 수 없는 파일", File.ReadAllText(Path.Combine(_storageDir, LocalMockBackend.GuestFile)));
            yield return Shot("20_boot_failed");

            // 다시 시도해도 같은 결과
            _input.PressRetry();
            yield return null;
            yield return null;
            Assert.IsTrue(GameRoot.Instance.BootHasFailed);

            // 사용자가 새로 시작을 고르면: 옛 파일은 이름을 바꿔 보관하고 새 게스트로 시작한다
            _input.PressNewGame();
            yield return Until(() => SceneManager.GetActiveScene().name == SceneNames.Room && Player != null && !GameRoot.Instance.IsBusy,
                30f, "새 게스트로 방에서 시작");
            Assert.IsFalse(Hud.Instance.BootFailVisible);
            string[] kept = Directory.GetFiles(_storageDir, "guest.unreadable-*.json");
            Assert.AreEqual(1, kept.Length, "읽을 수 없던 파일은 지우지 않고 보관한다");
            Assert.AreEqual("이건 읽을 수 없는 파일", File.ReadAllText(kept[0]));
        }

        // ── 도우미 ──────────────────────────────────────────────

        /// <summary>
        /// 벽을 등지면 카메라가 캐릭터 바로 뒤까지 당겨진다. 그때 캐릭터를 그리면 머리가 화면을 다 가리므로,
        /// 가까울 때는 그리지 않고 물러나면 다시 그리는지 확인한다.
        /// </summary>
        private IEnumerator CameraAgainstWall()
        {
            _input.move = Vector2.zero;
            _input.attackHeld = false;
            OrbitCamera orbit = OrbitCamera.Instance;

            var from = new Vector3(2f, 1.15f, 30f); // 집 앞 길에서 북쪽(집)을 본다
            Assert.IsTrue(Physics.Raycast(from, Vector3.forward, out RaycastHit wall, 20f,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore), "집 앞에서 북쪽으로 벽이 있어야 한다");

            Player.Teleport(new Vector3(from.x, 0f, wall.point.z - 0.9f), 180f);
            orbit.SnapBehind(180f);
            yield return null;
            yield return null;
            Debug.Log($"[CameraAgainstWall] wall={wall.collider.name} z={wall.point.z:0.00} distance={orbit.CurrentDistance:0.00}");
            Assert.Less(orbit.CurrentDistance, 1.3f, "벽을 등지면 카메라가 당겨져야 한다");
            Assert.IsTrue(orbit.TargetHidden, "카메라가 바짝 붙으면 캐릭터를 그리지 않아야 한다");
            foreach (MeshRenderer renderer in Player.GetComponentsInChildren<MeshRenderer>(true))
                Assert.IsFalse(renderer.enabled, $"가까울 때는 {renderer.name} 을 그리지 않아야 한다");
            yield return Shot("12_camera_against_wall");

            Player.Teleport(new Vector3(3f, 0f, -6f), 0f);
            orbit.SnapBehind(0f);
            yield return null;
            yield return null;
            Assert.Greater(orbit.CurrentDistance, 3f, "트인 곳에서는 카메라가 다시 물러나야 한다");
            Assert.IsFalse(orbit.TargetHidden);
            foreach (MeshRenderer renderer in Player.GetComponentsInChildren<MeshRenderer>(true))
                Assert.IsTrue(renderer.enabled, $"물러나면 {renderer.name} 을 다시 그려야 한다");
        }

        /// <summary>가장 가까운 적에게 다가가 공격 버튼을 누르고 있는다.</summary>
        private IEnumerator Fight(Func<bool> done, float seconds, string what, HuntZone stayIn = null, string shotName = null)
        {
            float deadline = Time.time + seconds;
            bool shotTaken = shotName == null;
            _input.attackHeld = true;
            while (!done())
            {
                if (Time.time > deadline)
                    Assert.Fail($"시간 초과 ({seconds}초): {what}. GC {GameSession.Current.Me.gc}, 체력 {Player.Health.Current}, 위치 {Player.transform.position}");

                EnemyAgent nearest = NearestEnemy();
                if (nearest != null)
                {
                    Vector3 to = nearest.transform.position - Player.transform.position;
                    to.y = 0f;
                    if (to.magnitude > 1.3f) SteerTowards(to.normalized);
                    else _input.move = Vector2.zero;
                    if (!shotTaken && to.magnitude < 2.5f)
                    {
                        shotTaken = true;
                        yield return Shot(shotName);
                    }
                }
                else if (stayIn != null)
                {
                    // 적이 다시 나올 때까지 구역 가운데 근처에서 기다린다.
                    Vector3 to = stayIn.transform.position - Player.transform.position;
                    to.y = 0f;
                    if (to.magnitude > 3f) SteerTowards(to.normalized);
                    else _input.move = Vector2.zero;
                }
                else
                {
                    _input.move = Vector2.zero;
                }
                yield return null;
            }
            _input.attackHeld = false;
            _input.move = Vector2.zero;
            yield return null;
        }

        private static EnemyAgent NearestEnemy()
        {
            EnemyAgent best = null;
            float bestDistance = 30f;
            foreach (HuntZone zone in HuntZone.All)
            {
                foreach (EnemyAgent enemy in zone.Alive)
                {
                    if (enemy == null || enemy.Health.IsDead) continue;
                    float distance = Vector3.Distance(enemy.transform.position, Player.transform.position);
                    if (distance < bestDistance) { bestDistance = distance; best = enemy; }
                }
            }
            return best;
        }

        private static HuntZone FindZone(string code)
        {
            foreach (HuntZone zone in HuntZone.All)
                if (zone.Def.code == code) return zone;
            Assert.Fail("사냥 구역을 찾을 수 없다: " + code);
            return null;
        }

        /// <summary>지금 레벨에 오기까지 쓴 경험치의 합.</summary>
        private static int XpSpentOnLevels(int level, GameDefs defs)
        {
            int total = 0;
            for (int l = 1; l < level; l++) total += Progression.XpForNextLevel(l, defs.progression);
            return total;
        }
    }
}
