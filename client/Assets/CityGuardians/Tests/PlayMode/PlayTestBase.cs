using System;
using System.Collections;
using System.IO;
using CityGuardians.Gameplay;
using CityGuardians.Inputs;
using CityGuardians.Services;
using CityGuardians.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace CityGuardians.Tests
{
    /// <summary>
    /// 실제 장면을 띄워 놓고 사람이 하듯 입력을 넣는 테스트의 공통 부분.
    /// 키 입력 대신 ScriptedInput을 쓰는 것 말고는 게임 코드를 그대로 지난다.
    /// 저장 데이터는 임시 폴더에 두어 실제 저장 데이터를 건드리지 않는다.
    /// </summary>
    public abstract class PlayTestBase
    {
        protected ScriptedInput _input;
        protected string _storageDir;

        [SetUp]
        public void SetUp()
        {
            _storageDir = Path.Combine(Application.temporaryCachePath, "cg_playmode_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_storageDir);
            GameRoot.StorageDirOverride = _storageDir;
            GameRoot.ClockOverride = null;
            GameRoot.DefsOverride = null;
            _input = new ScriptedInput();
            PlayerInputHub.Current = _input;
            TimeFx.Enabled = false;
            GameRoot.Instance.Shutdown();
        }

        [TearDown]
        public void TearDown()
        {
            GameRoot.Instance.Shutdown();
            Time.timeScale = 1f;
            GameRoot.StorageDirOverride = null;
            GameRoot.ClockOverride = null;
            GameRoot.DefsOverride = null;
            PlayerInputHub.ResetToDefault();
            TimeFx.Enabled = true;
            try { Directory.Delete(_storageDir, true); }
            catch (IOException) { }
        }

        protected static PlayerController Player => PlayerController.Instance;

        protected IEnumerator BootToRoom()
        {
            SceneManager.LoadScene(SceneNames.Boot);
            yield return Until(() => GameSession.Current != null
                                     && SceneManager.GetActiveScene().name == SceneNames.Room
                                     && Player != null && !GameRoot.Instance.IsBusy,
                40f, "접속 후 방 장면 시작");
            yield return null;
        }

        /// <summary>
        /// 튜토리얼을 마친 상태로 도시에 선다. 방에서 도시까지의 흐름은 FirstPlayTests가 확인하므로 다른 테스트는 건너뛴다.
        /// </summary>
        protected IEnumerator BootToCityAfterTutorial()
        {
            yield return BootToRoom();
            GameSession.Current.AdvanceTutorial(6);
            Assert.AreEqual(6, GameSession.Current.TutorialStep);
            GameRoot.Instance.LoadScene(SceneNames.City, SpawnIds.Home);
            yield return UntilInCity();
        }

        protected IEnumerator UntilInCity()
        {
            yield return Until(() => SceneManager.GetActiveScene().name == SceneNames.City && Player != null && !GameRoot.Instance.IsBusy,
                40f, "도시 장면으로 이동");
            yield return null;
        }

        protected static float PlanarDistance(Vector3 a, Vector3 b)
        {
            float x = a.x - b.x;
            float z = a.z - b.z;
            return Mathf.Sqrt(x * x + z * z);
        }

        /// <summary>조건이 될 때까지 기다린다. 제한 시간은 실제 시간(초)이다.</summary>
        protected static IEnumerator Until(Func<bool> condition, float seconds, string what)
        {
            float deadline = Time.realtimeSinceStartup + seconds;
            while (!condition())
            {
                if (Time.realtimeSinceStartup > deadline) Assert.Fail($"시간 초과 ({seconds}초): {what}");
                yield return null;
            }
        }

        protected IEnumerator PressInteract()
        {
            _input.PressInteract();
            yield return null;
            yield return null;
            yield return null;
        }

        protected IEnumerator PressChoice(int number)
        {
            _input.PressChoice(number);
            yield return null;
            yield return null;
            yield return null;
        }

        protected IEnumerator PressInventory()
        {
            _input.PressInventory();
            yield return null;
            yield return null;
            yield return null;
        }

        /// <summary>숫자 1·2: 무기 칸 고르기.</summary>
        protected IEnumerator PressWeaponSlot(int slot)
        {
            _input.PressWeaponSlot(slot);
            yield return null;
            yield return null;
            yield return null;
        }

        /// <summary>숫자 3: 수리 키트 빠른 사용.</summary>
        protected IEnumerator PressQuickUse()
        {
            _input.PressQuickUse();
            yield return null;
            yield return null;
            yield return null;
        }

        /// <summary>마우스를 움직여 화면이 그 지점을 향하게 한다 (카메라를 직접 옮기지 않는다).</summary>
        protected IEnumerator LookTowards(Vector3 worldPoint)
        {
            for (int i = 0; i < 180; i++)
            {
                if (!TurnCameraTowards(worldPoint)) break;
                yield return null;
            }
            _input.lookDelta = Vector2.zero;
            yield return null;
        }

        /// <summary>한 프레임 분량의 마우스 움직임을 넣는다. 이미 그쪽을 보고 있으면 false.</summary>
        protected bool TurnCameraTowards(Vector3 worldPoint)
        {
            OrbitCamera orbit = OrbitCamera.Instance;
            // 카메라는 캐릭터 둘레를 돈다. 캐릭터에서 그 지점을 보는 방향이 화면의 정면이 되게 한다.
            Vector3 to = worldPoint - Player.transform.position;
            float wanted = Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg;
            float delta = Mathf.DeltaAngle(orbit.Yaw, wanted);
            if (Mathf.Abs(delta) < 1.5f)
            {
                _input.lookDelta = Vector2.zero;
                return false;
            }
            _input.lookDelta = new Vector2(Mathf.Clamp(delta, -10f, 10f), 0f);
            return true;
        }

        protected IEnumerator AdvanceDialogue()
        {
            for (int i = 0; i < 12 && Hud.Instance.DialogueVisible; i++) yield return PressInteract();
            Assert.IsFalse(Hud.Instance.DialogueVisible, "대화가 끝나지 않았다");
        }

        /// <summary>카메라 기준 입력으로 목표 지점까지 걸어간다 (순간이동하지 않는다). 제한 시간은 게임 시간(초)이다.</summary>
        protected IEnumerator WalkTo(Vector3 target, float stopDistance, float seconds, bool run)
        {
            float deadline = Time.time + seconds;
            float closest = float.MaxValue;
            float lastProgress = Time.time;
            float sidestepUntil = -1f;
            float sidestepSign = 1f;
            while (true)
            {
                Assert.IsNotNull(Player, "플레이어가 없다");
                Vector3 to = target - Player.transform.position;
                to.y = 0f;
                float distance = to.magnitude;
                if (distance <= stopDistance) break;
                if (Time.time > deadline)
                    Assert.Fail($"시간 초과: {target} 까지 걸어가지 못했다. 현재 위치 {Player.transform.position}");

                // 사람이나 로봇에 막혀 나아가지 못하면, 사람이 하듯 옆으로 돌아간다.
                if (distance < closest - 0.05f)
                {
                    closest = distance;
                    lastProgress = Time.time;
                }
                else if (Time.time - lastProgress > 0.6f && Time.time >= sidestepUntil)
                {
                    sidestepUntil = Time.time + 0.5f;
                    sidestepSign = -sidestepSign;
                    lastProgress = Time.time;
                    closest = float.MaxValue;
                }

                Vector3 direction = to.normalized;
                if (Time.time < sidestepUntil)
                    direction = (new Vector3(direction.z, 0f, -direction.x) * sidestepSign + direction * 0.3f).normalized;
                SteerTowards(direction);
                _input.run = run;
                yield return null;
            }
            _input.move = Vector2.zero;
            _input.run = false;
            yield return null;
        }

        protected void SteerTowards(Vector3 worldDirection)
        {
            Transform cam = Camera.main.transform;
            Vector3 forward = cam.forward;
            forward.y = 0f;
            forward.Normalize();
            var right = new Vector3(forward.z, 0f, -forward.x);
            _input.move = new Vector2(Vector3.Dot(worldDirection, right), Vector3.Dot(worldDirection, forward));
        }

        /// <summary>게임 시간과 함께 흐르고, 앞으로 감을 수도 있는 서버 시계. 한국 시간 2026-11-13 12:00에 시작한다.</summary>
        protected sealed class ServerClock : IClock
        {
            private readonly DateTime _start = new DateTime(2026, 11, 13, 3, 0, 0, DateTimeKind.Utc);
            public double offset;
            public DateTime UtcNow => _start.AddSeconds(Time.timeAsDouble + offset);
        }

        /// <summary>
        /// 쓸 돈을 마련한다: 모의 서버에 사냥 보고를 보낸다 (로봇 한 대 10 GC, 1분에 24대까지 인정).
        /// 서버 시계를 1분씩 앞으로 감아 "너무 빨리 잡았다"는 검증에 걸리지 않게 한다.
        /// 사냥으로 돈을 버는 과정은 FirstPlayTests가 확인한다.
        /// </summary>
        protected static void EarnGc(ServerClock clock, int gc)
        {
            GameSession session = GameSession.Current;
            var backend = (LocalMockBackend)session.Backend;
            while (gc > 0)
            {
                int chunk = Math.Min(gc, 240);
                ApiResult<HuntSessionDto> started = null;
                backend.StartHunt(CityGuardians.Domain.ZoneCodes.NorthEast, r => started = r);
                Assert.IsTrue(started.ok, started.message);
                clock.offset += 60;
                ApiResult<HuntReportDto> report = null;
                backend.ReportHunt(started.data.sessionId, 1,
                    new System.Collections.Generic.List<CityGuardians.Domain.KillCount>
                    {
                        new CityGuardians.Domain.KillCount(CityGuardians.Domain.EnemyCodes.MalfunctionRobot, chunk / 10),
                    }, r => report = r);
                Assert.IsTrue(report.ok, report.message);
                Assert.AreEqual(chunk, report.data.rewardGc, "테스트 준비: 사냥 보상");
                gc -= chunk;
            }
            ApiResult<MeDto> me = null;
            backend.GetMe(r => me = r);
            Assert.IsTrue(me.ok, me.message);
            session.ApplyMe(me.data);
        }

        /// <summary>그 NPC에게 걸어가 말을 걸고 대화를 끝까지 넘긴다. 멀리 있으면 먼저 근처로 옮겨 놓는다.</summary>
        /// <param name="from">NPC에서 본, 걸어가기 시작할 자리의 방향과 거리.</param>
        protected IEnumerator TalkTo(Vector3 npcSpot, Vector3 from, float yaw)
        {
            if (PlanarDistance(Player.transform.position, npcSpot) > 8f)
            {
                Player.Teleport(npcSpot + from, yaw);
                OrbitCamera.Instance.SnapBehind(yaw);
            }
            yield return null;
            yield return WalkTo(npcSpot, 1.6f, 15f, false);
            yield return Until(() => Hud.Instance.PromptText != null && Hud.Instance.PromptText.Contains("말하기"), 2f, "말하기 안내");
            yield return PressInteract();
            Assert.IsTrue(Hud.Instance.DialogueVisible, "대화가 시작되어야 한다");
            yield return AdvanceDialogue();
        }

        protected IEnumerator UntilInRoom()
        {
            yield return Until(() => SceneManager.GetActiveScene().name == SceneNames.Room && Player != null && !GameRoot.Instance.IsBusy,
                40f, "방 장면으로 이동");
            yield return null;
        }

        protected IEnumerator PressDecorate()
        {
            _input.PressDecorate();
            yield return null;
            yield return null;
            yield return null;
        }

        protected IEnumerator PressRotate()
        {
            _input.PressRotate();
            yield return null;
            yield return null;
            yield return null;
        }

        protected static NpcAgent Npc(string code)
        {
            foreach (NpcAgent npc in UnityEngine.Object.FindObjectsByType<NpcAgent>(FindObjectsSortMode.None))
                if (npc.Code == code) return npc;
            Assert.Fail("NPC를 찾을 수 없다: " + code);
            return null;
        }

        /// <summary>
        /// 화면을 PNG로 남긴다 (client/Logs/shots). 그래픽 장치 없이 돌 때는 아무것도 하지 않는다.
        /// HUD까지 한 장에 담기 위해 잠깐 캔버스를 카메라에 붙였다가 되돌린다.
        /// </summary>
        protected static IEnumerator Shot(string name)
        {
            yield return null;
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) yield break;
            Camera cam = Camera.main;
            GameObject temporaryCamera = null;
            if (cam == null)
            {
                // 시작 화면처럼 카메라가 없는 장면은 빈 배경 위에 HUD만 찍는다.
                temporaryCamera = new GameObject("ShotCamera");
                cam = temporaryCamera.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Color.black;
            }
            OrbitCamera orbit = OrbitCamera.Instance;
            if (orbit != null)
                Debug.Log($"[Shot] {name}: cameraDistance={orbit.CurrentDistance:0.00} targetHidden={orbit.TargetHidden}");

            Canvas canvas = Hud.Instance.Canvas;
            var target = new RenderTexture(1280, 720, 24);
            // 그림의 크기를 먼저 정해 둔다. 그래야 HUD가 (테스트 창의 크기가 아니라) 16:9 화면에 맞춰 배치된다.
            RenderTexture previous = cam.targetTexture;
            cam.targetTexture = target;
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = cam;
            canvas.planeDistance = 0.35f;
            Canvas.ForceUpdateCanvases();
            yield return null;
            yield return null;

            cam.Render();
            cam.targetTexture = previous;

            RenderTexture.active = target;
            var image = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
            image.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
            image.Apply();
            RenderTexture.active = null;

            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.worldCamera = null;
            if (temporaryCamera != null) UnityEngine.Object.Destroy(temporaryCamera);

            string dir = Path.GetFullPath(Path.Combine(Application.dataPath, "../Logs/shots"));
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, name + ".png"), image.EncodeToPNG());
            UnityEngine.Object.Destroy(image);
            target.Release();
            UnityEngine.Object.Destroy(target);

            // 캔버스가 화면 기준으로 돌아온 뒤의 좌표를 테스트가 읽을 수 있게 한 프레임 기다린다.
            Canvas.ForceUpdateCanvases();
            yield return null;
            yield return null;
        }
    }
}
