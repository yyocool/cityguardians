using System.Collections.Generic;
using CityGuardians.Domain;
using CityGuardians.Gameplay;
using CityGuardians.Services;
using UnityEngine;
using UnityEngine.UI;

namespace CityGuardians.UI
{
    /// <summary>
    /// 전투 모드가 함께 쓰는 화면 (docs/COMBAT_SYSTEM.md 12장):
    ///   · 도시 위험도 표시 — 왼쪽 위, 모든 전투 장면에서 보인다. 색만이 아니라 모양(●▲◆■)과 글자로도 단계를 알린다.
    ///   · FPS 화면 — 조준점, 탄창, 보조 장치·지원 장비, 임무 현황(시설·시민·시간), 탐지 표시, 결과.
    /// 게임플레이의 상태(CityRiskService, FpsMission, FpsWeapon)를 읽어 그리기만 한다.
    /// </summary>
    public sealed class CombatHud : MonoBehaviour
    {
        private const int MarkerCount = 28;
        private const int FacilityRows = 3;
        private static readonly Color EnemyMark = new Color(1f, 0.38f, 0.32f);
        private static readonly Color CivilianMark = new Color(1f, 0.90f, 0.40f);
        private static readonly Color Good = new Color(0.36f, 0.78f, 1f);
        private static readonly Color Low = new Color(0.95f, 0.36f, 0.30f);
        private static readonly Color Ready = new Color(0.6f, 1f, 0.7f);

        // 점검과 자동 테스트에서 읽는 값
        public bool RiskVisible => _risk.activeSelf;
        public string RiskText => _riskText.text;
        public string RiskDetail => _riskDetail.gameObject.activeSelf ? _riskDetail.text : null;
        public bool FpsVisible => _fps.activeSelf;
        public string AmmoText => _ammoText.text;
        public string MissionText => _missionInfo.text;
        public bool ResultVisible => _result.activeSelf;
        public string ResultTitle => _resultTitle.text;
        public string ResultBody => _resultBody.text;
        public int VisibleMarkers { get; private set; }

        private Canvas _canvas;

        private GameObject _risk;
        private Image _riskChip;
        private Text _riskIcon;
        private Text _riskText;
        private Text _riskDetail;

        private GameObject _fps;
        private RectTransform _crosshair;
        private readonly Image[] _crossBars = new Image[4];
        private Image _crossDot;
        private Text _weaponName;
        private Text _ammoText;
        private RectTransform _reloadFill;
        private Text _gadgetText;
        private Text _supportText;
        private Text _missionTitle;
        private Text _missionInfo;
        private readonly Text[] _facilityNames = new Text[FacilityRows];
        private readonly Text[] _facilityHp = new Text[FacilityRows];
        private readonly RectTransform[] _facilityFill = new RectTransform[FacilityRows];
        private RectTransform _markerRoot;
        private readonly RectTransform[] _markers = new RectTransform[MarkerCount];
        private readonly Text[] _markerTexts = new Text[MarkerCount];

        private GameObject _result;
        private Image _resultBack;
        private Text _resultTitle;
        private Text _resultBody;
        private Text _resultKeys;

        /// <summary>걸어 다니는 화면의 요소들과 같은 층에 놓는 것: 위험도 표시와 FPS 화면.</summary>
        public void BuildStatus(Canvas canvas, Transform root)
        {
            _canvas = canvas;

            // 왼쪽 위, 체력·경험치·소속 도시 아래: 도시 위험도
            Image risk = UiFactory.Panel(root, "CityRisk", new Vector2(0f, 1f), new Vector2(32f, -172f), new Vector2(400f, 76f),
                UiFactory.PanelColor);
            _risk = risk.gameObject;
            _riskChip = UiFactory.Panel(risk.transform, "Chip", new Vector2(0f, 1f), new Vector2(8f, -6f), new Vector2(34f, 34f), Color.white);
            _riskIcon = UiFactory.Label(_riskChip.transform, "Icon", 22, TextAnchor.MiddleCenter, new Color(0.08f, 0.08f, 0.12f));
            _riskText = UiFactory.LabelAt(risk.transform, "Text", new Vector2(0f, 1f), new Vector2(52f, -4f), new Vector2(340f, 38f),
                24, TextAnchor.MiddleLeft, UiFactory.TextColor);
            _riskText.horizontalOverflow = HorizontalWrapMode.Overflow;
            _riskDetail = UiFactory.LabelAt(risk.transform, "Detail", new Vector2(0f, 1f), new Vector2(12f, -42f), new Vector2(380f, 30f),
                20, TextAnchor.MiddleLeft, UiFactory.Accent);
            _riskDetail.horizontalOverflow = HorizontalWrapMode.Overflow;
            _risk.SetActive(false);

            // FPS 화면 전체를 한 덩어리로 켜고 끈다.
            RectTransform fps = UiFactory.Stretch(root, "Fps");
            _fps = fps.gameObject;

            // 탐지 표시는 다른 FPS 요소 아래에 깔린다.
            _markerRoot = UiFactory.Stretch(fps, "Markers");
            for (int i = 0; i < MarkerCount; i++)
            {
                RectTransform marker = UiFactory.Rect(_markerRoot, "Marker" + i, new Vector2(0f, 0f), Vector2.zero, new Vector2(120f, 44f));
                marker.pivot = new Vector2(0.5f, 0.5f);
                _markerTexts[i] = UiFactory.Label(marker, "Text", 22, TextAnchor.MiddleCenter, EnemyMark, 0f, true);
                _markerTexts[i].horizontalOverflow = HorizontalWrapMode.Overflow;
                _markers[i] = marker;
                marker.gameObject.SetActive(false);
            }

            // 조준점
            _crosshair = UiFactory.Rect(fps, "Crosshair", new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(60f, 60f));
            Vector2[] offsets = { new Vector2(0f, 14f), new Vector2(0f, -14f), new Vector2(-14f, 0f), new Vector2(14f, 0f) };
            for (int i = 0; i < 4; i++)
            {
                bool vertical = i < 2;
                _crossBars[i] = UiFactory.Panel(_crosshair, "Bar" + i, new Vector2(0.5f, 0.5f), offsets[i],
                    vertical ? new Vector2(3f, 12f) : new Vector2(12f, 3f), Color.white);
            }
            _crossDot = UiFactory.Panel(_crosshair, "Dot", new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(4f, 4f), Color.white);

            // 오른쪽 아래: 무기와 장치
            Image gear = UiFactory.Panel(fps, "Gear", new Vector2(1f, 0f), new Vector2(-32f, 28f), new Vector2(380f, 188f), UiFactory.PanelColor);
            _weaponName = UiFactory.LabelAt(gear.transform, "Weapon", new Vector2(0f, 1f), new Vector2(18f, -8f), new Vector2(230f, 36f),
                24, TextAnchor.MiddleLeft, UiFactory.TextColor);
            _weaponName.horizontalOverflow = HorizontalWrapMode.Overflow;
            _ammoText = UiFactory.LabelAt(gear.transform, "Ammo", new Vector2(1f, 1f), new Vector2(-18f, -6f), new Vector2(150f, 42f),
                32, TextAnchor.MiddleRight, UiFactory.Accent);
            _reloadFill = UiFactory.Bar(gear.transform, "Reload", new Vector2(0.5f, 1f), new Vector2(0f, -52f), new Vector2(344f, 14f),
                new Color(0.10f, 0.10f, 0.14f, 0.9f), UiFactory.Accent);
            _gadgetText = UiFactory.LabelAt(gear.transform, "Gadget", new Vector2(0f, 1f), new Vector2(18f, -80f), new Vector2(344f, 40f),
                24, TextAnchor.MiddleLeft, UiFactory.TextColor);
            _gadgetText.horizontalOverflow = HorizontalWrapMode.Overflow;
            _supportText = UiFactory.LabelAt(gear.transform, "Support", new Vector2(0f, 1f), new Vector2(18f, -128f), new Vector2(344f, 40f),
                24, TextAnchor.MiddleLeft, UiFactory.TextColor);
            _supportText.horizontalOverflow = HorizontalWrapMode.Overflow;

            // 오른쪽 위, GC 아래: 임무 현황
            Image mission = UiFactory.Panel(fps, "Mission", new Vector2(1f, 1f), new Vector2(-32f, -100f), new Vector2(400f, 232f),
                UiFactory.PanelColor);
            _missionTitle = UiFactory.LabelAt(mission.transform, "Title", new Vector2(0f, 1f), new Vector2(18f, -8f), new Vector2(364f, 34f),
                24, TextAnchor.MiddleLeft, UiFactory.Accent);
            _missionTitle.horizontalOverflow = HorizontalWrapMode.Overflow;
            _missionInfo = UiFactory.LabelAt(mission.transform, "Info", new Vector2(0f, 1f), new Vector2(18f, -44f), new Vector2(364f, 64f),
                22, TextAnchor.UpperLeft, UiFactory.TextColor);
            _missionInfo.horizontalOverflow = HorizontalWrapMode.Overflow;
            for (int i = 0; i < FacilityRows; i++)
            {
                float y = -112f - i * 38f;
                _facilityNames[i] = UiFactory.LabelAt(mission.transform, "FacilityName" + i, new Vector2(0f, 1f), new Vector2(18f, y),
                    new Vector2(130f, 32f), 22, TextAnchor.MiddleLeft, UiFactory.TextColor);
                _facilityNames[i].horizontalOverflow = HorizontalWrapMode.Overflow;
                _facilityFill[i] = UiFactory.Bar(mission.transform, "FacilityBar" + i, new Vector2(0f, 1f), new Vector2(150f, y - 6f),
                    new Vector2(150f, 20f), new Color(0.10f, 0.10f, 0.14f, 0.9f), Good);
                _facilityHp[i] = UiFactory.LabelAt(mission.transform, "FacilityHp" + i, new Vector2(1f, 1f), new Vector2(-18f, y),
                    new Vector2(80f, 32f), 22, TextAnchor.MiddleRight, Color.white);
            }

            _fps.SetActive(false);
        }

        /// <summary>결과 화면. 선택 메뉴보다 위에 놓는다.</summary>
        public void BuildResult(Transform root)
        {
            Image result = UiFactory.Panel(root, "FpsResult", new Vector2(0.5f, 0.5f), new Vector2(0f, 20f),
                new Vector2(1000f, 600f), new Color(0.06f, 0.10f, 0.18f, 0.97f));
            _result = result.gameObject;
            _resultBack = result;
            _resultTitle = UiFactory.LabelAt(result.transform, "Title", new Vector2(0.5f, 1f), new Vector2(0f, -40f), new Vector2(920f, 110f),
                76, TextAnchor.MiddleCenter, Color.white);
            _resultBody = UiFactory.LabelAt(result.transform, "Body", new Vector2(0.5f, 0.5f), new Vector2(0f, -20f), new Vector2(880f, 340f),
                30, TextAnchor.MiddleCenter, UiFactory.TextColor);
            _resultKeys = UiFactory.LabelAt(result.transform, "Keys", new Vector2(0.5f, 0f), new Vector2(0f, 26f), new Vector2(900f, 44f),
                30, TextAnchor.MiddleCenter, UiFactory.Accent);
            _result.SetActive(false);
        }

        /// <summary>Hud가 매 프레임 부른다.</summary>
        public void Refresh(float dt)
        {
            RefreshRisk();
            RefreshFps();
        }

        // ── 도시 위험도 ─────────────────────────────────────────

        private void RefreshRisk()
        {
            GameSession session = GameSession.Current;
            // 전투가 반영되는 도시: 지원 나가 있으면 그 도시, 아니면 소속 도시. 이방인에게는 없다.
            CityStatusDto city = session != null && session.TutorialStep >= 6 && session.IsResident
                ? CityRiskService.CombatCity
                : null;
            bool show = city != null;
            if (_risk.activeSelf != show) _risk.SetActive(show);
            if (!show) return;

            bool supporting = !string.IsNullOrEmpty(session.SupportRegionCode) && city.regionCode == session.SupportRegionCode;
            string name = RegionService.ShortName(city.regionCode) ?? "도시";
            _riskChip.color = CityRiskService.StageColor(city.stage);
            _riskIcon.text = RiskStages.Icon(city.stage);
            _riskText.text = $"{RiskStages.DisplayName(city.stage)} · {(supporting ? "지원 " : "")}{name} 위험도 {city.risk}";

            bool invasion = city.HasInvasion;
            if (_riskDetail.gameObject.activeSelf != invasion) _riskDetail.gameObject.SetActive(invasion);
            if (invasion)
                _riskDetail.text = $"! {InvasionKinds.DisplayName(city.invasionKind)} 진행 중 · 격퇴 {city.invasionProgress} / {city.invasionRequired}";
            ((RectTransform)_risk.transform).sizeDelta = new Vector2(400f, invasion ? 76f : 46f);
        }

        // ── FPS ─────────────────────────────────────────────────

        private void RefreshFps()
        {
            FpsMission mission = FpsMission.Current;
            FpsWeapon weapon = FpsWeapon.Current;
            bool show = mission != null && weapon != null && mission.Phase != FpsPhase.Starting;
            if (_fps.activeSelf != show) _fps.SetActive(show);
            if (!show)
            {
                if (_result.activeSelf) _result.SetActive(false);
                VisibleMarkers = 0;
                return;
            }

            bool playing = mission.IsActive && !GameState.IsControlLocked;
            if (_crosshair.gameObject.activeSelf != playing) _crosshair.gameObject.SetActive(playing);
            if (playing) RefreshCrosshair(weapon);
            RefreshGear(weapon);
            RefreshMission(mission);
            RefreshMarkers(mission, weapon, playing);
            RefreshResult(mission);
        }

        private void RefreshCrosshair(FpsWeapon weapon)
        {
            // 방금 맞혔으면 조준점이 잠깐 노랗게 벌어진다.
            float sinceHit = Time.time - weapon.LastHitTime;
            bool hit = sinceHit >= 0f && sinceHit < 0.12f;
            Color color = hit ? UiFactory.Accent : weapon.Reloading ? new Color(1f, 1f, 1f, 0.35f) : Color.white;
            float spread = weapon.Aiming ? 8f : hit ? 18f : 14f;
            Vector2[] offsets = { new Vector2(0f, spread), new Vector2(0f, -spread), new Vector2(-spread, 0f), new Vector2(spread, 0f) };
            for (int i = 0; i < 4; i++)
            {
                _crossBars[i].color = color;
                _crossBars[i].rectTransform.anchoredPosition = offsets[i];
            }
            _crossDot.color = color;
        }

        private void RefreshGear(FpsWeapon weapon)
        {
            GearDef primary = weapon.Primary;
            _weaponName.text = primary != null ? primary.name : "무기 없음";
            _ammoText.text = primary == null ? "" : weapon.Reloading ? "재장전" : $"{weapon.Ammo} / {weapon.ClipSize}";
            _ammoText.color = weapon.Reloading || weapon.Ammo == 0 ? Low : UiFactory.Accent;
            UiFactory.SetBar(_reloadFill, weapon.Reloading
                ? weapon.ReloadProgress
                : weapon.ClipSize > 0 ? weapon.Ammo / (float)weapon.ClipSize : 0f);

            _gadgetText.text = DeviceLine("Q", weapon.Gadget, weapon.GadgetCooldownLeft,
                weapon.ShieldActive ? $"보호막 {weapon.ShieldLeft}" : null);
            _gadgetText.color = weapon.Gadget != null && weapon.GadgetCooldownLeft <= 0f ? Ready : UiFactory.TextColor;
            _supportText.text = DeviceLine("F", weapon.Support, weapon.SupportCooldownLeft,
                weapon.SensorActive ? "탐지 중" : weapon.DroneActive ? "출동 중" : null);
            _supportText.color = weapon.Support != null && weapon.SupportCooldownLeft <= 0f ? Ready : UiFactory.TextColor;
        }

        private static string DeviceLine(string key, GearDef def, float cooldownLeft, string active)
        {
            if (def == null) return $"{key}  비어 있음";
            string state = active ?? (cooldownLeft > 0f ? $"{Mathf.CeilToInt(cooldownLeft)}초" : "준비됨");
            return $"{key}  {def.name}   {state}";
        }

        private void RefreshMission(FpsMission mission)
        {
            _missionTitle.text = string.IsNullOrEmpty(mission.InvasionId)
                ? $"순찰 출동 · {mission.Def.name}"
                : $"침입 격퇴 · {mission.Def.name}";
            int seconds = Mathf.CeilToInt(mission.TimeLeft);
            _missionInfo.text = $"남은 시간  {seconds / 60}:{seconds % 60:00}     남은 로봇  {mission.EnemiesLeft}\n" +
                                $"대피시킨 시민  {mission.CiviliansSaved} / {mission.Civilians.Count}";
            _missionInfo.color = mission.TimeLeft <= 60f && mission.IsActive ? Low : UiFactory.TextColor;

            IReadOnlyList<FpsFacility> facilities = mission.Facilities;
            for (int i = 0; i < FacilityRows; i++)
            {
                bool used = i < facilities.Count;
                if (_facilityNames[i].gameObject.activeSelf != used)
                {
                    _facilityNames[i].gameObject.SetActive(used);
                    _facilityHp[i].gameObject.SetActive(used);
                    _facilityFill[i].parent.parent.gameObject.SetActive(used);
                }
                if (!used) continue;

                FpsFacility facility = facilities[i];
                float ratio = facility.MaxHp > 0 ? facility.Hp / (float)facility.MaxHp : 0f;
                bool justHit = Time.time - facility.LastHitTime < 0.4f;
                _facilityNames[i].text = facility.DisplayName;
                _facilityNames[i].color = facility.IsAlive ? UiFactory.TextColor : new Color(0.6f, 0.6f, 0.64f);
                // 색만으로 알리지 않는다: 멈춘 시설은 글자로도 "중단"이라고 쓴다.
                _facilityHp[i].text = facility.IsAlive ? $"{facility.Hp} / {facility.MaxHp}" : "중단";
                _facilityHp[i].color = facility.IsAlive ? Color.white : Low;
                UiFactory.SetBar(_facilityFill[i], ratio);
                _facilityFill[i].GetComponent<Image>().color = justHit || ratio <= 0.35f ? Low : Good;
            }
        }

        /// <summary>탐지 센서가 켜져 있는 동안 벽 너머의 로봇(◆)과 시민(●)을 화면에 표시한다.</summary>
        private void RefreshMarkers(FpsMission mission, FpsWeapon weapon, bool playing)
        {
            int used = 0;
            Camera cam = Camera.main;
            PlayerController player = PlayerController.Instance;
            if (playing && weapon.SensorActive && cam != null && player != null && _canvas != null)
            {
                float scale = Mathf.Max(0.01f, _canvas.scaleFactor);
                Vector3 origin = player.transform.position;
                float radius = weapon.SensorRadius;

                IReadOnlyList<EnemyAgent> enemies = mission.Enemies;
                for (int i = 0; i < enemies.Count && used < MarkerCount; i++)
                {
                    EnemyAgent enemy = enemies[i];
                    if (enemy == null || enemy.IsGone) continue;
                    Vector3 at = enemy.transform.position + Vector3.up * (enemy.Height * 0.6f);
                    if (PlaceMarker(used, cam, scale, origin, at, radius, "◆", EnemyMark)) used++;
                }
                IReadOnlyList<FpsCivilian> civilians = mission.Civilians;
                for (int i = 0; i < civilians.Count && used < MarkerCount; i++)
                {
                    FpsCivilian civilian = civilians[i];
                    if (civilian == null || civilian.Saved) continue;
                    if (PlaceMarker(used, cam, scale, origin, civilian.Position + Vector3.up * 1.2f, radius, "● 시민", CivilianMark)) used++;
                }
            }

            for (int i = used; i < MarkerCount; i++)
                if (_markers[i].gameObject.activeSelf) _markers[i].gameObject.SetActive(false);
            VisibleMarkers = used;
        }

        private bool PlaceMarker(int index, Camera cam, float scale, Vector3 origin, Vector3 world, float radius, string label, Color color)
        {
            Vector3 to = world - origin;
            to.y = 0f;
            float distance = to.magnitude;
            if (distance > radius) return false;
            Vector3 screen = cam.WorldToScreenPoint(world);
            // 등 뒤에 있는 것은 표시하지 않는다.
            if (screen.z <= 0.1f) return false;
            if (screen.x < 0f || screen.x > Screen.width || screen.y < 0f || screen.y > Screen.height) return false;

            RectTransform marker = _markers[index];
            if (!marker.gameObject.activeSelf) marker.gameObject.SetActive(true);
            marker.anchoredPosition = new Vector2(screen.x / scale, screen.y / scale);
            _markerTexts[index].text = $"{label} {Mathf.RoundToInt(distance)}m";
            _markerTexts[index].color = color;
            return true;
        }

        private void RefreshResult(FpsMission mission)
        {
            bool failed = mission.Phase == FpsPhase.ReportFailed;
            bool show = failed || (mission.IsOver && mission.Result != null);
            if (_result.activeSelf != show) _result.SetActive(show);
            if (!show) return;

            if (failed)
            {
                _resultBack.color = new Color(0.10f, 0.10f, 0.14f, 0.97f);
                _resultTitle.text = "결과를 보내지 못했어요";
                _resultTitle.color = Color.white;
                _resultBody.text = (mission.ResultError ?? "") + "\n\n보상은 결과를 보낸 뒤에 받을 수 있어요.";
                _resultKeys.text = "R  다시 보내기";
                return;
            }

            FpsResultDto result = mission.Result;
            bool won = mission.Phase == FpsPhase.Won;
            _resultBack.color = won ? new Color(0.05f, 0.16f, 0.22f, 0.97f) : new Color(0.14f, 0.11f, 0.16f, 0.97f);
            _resultKeys.text = "E  도시로 돌아가기";

            if (!result.accepted)
            {
                _resultTitle.text = "기록을 확인하지 못했어요";
                _resultTitle.color = Color.white;
                _resultBody.text = "임무 기록이 서버 기록과 맞지 않아\n이번에는 보상을 받지 못했어요.";
                return;
            }

            FpsMissionDef def = mission.Def;
            string tally = $"지켜낸 시설  {result.facilitiesSaved} / {def.facilityCount}      " +
                           $"대피시킨 시민  {result.civiliansSaved} / {def.civilianCount}";
            // 결과가 도시 위험도에 반영된 내용 (서버가 계산한 값).
            string risk = RewardService.CityBlock(result.city);

            if (won)
            {
                _resultTitle.text = "침입 격퇴 성공!";
                _resultTitle.color = UiFactory.Accent;
                string reward = result.rewardLimited
                    ? $"오늘의 보상 {result.fpsRewardLimit}번을 모두 받았어요. 경험치와 도시 반영은 그대로예요."
                    : result.firstWin
                        ? $"보상  +{result.rewardGc:N0} GC   (첫 승리 보너스 {def.firstWinBonusGc:N0} GC 포함)"
                        : $"보상  +{result.rewardGc:N0} GC";
                string level = result.levelsGained > 0 ? $"\n레벨 업!  레벨 {result.level}" : "";
                string cityName = result.defensePoints > 0 ? RegionService.ShortName(mission.RegionCode) : null;
                string city = cityName != null ? $"\n{cityName} 방어 포인트  +{result.defensePoints:N0}" : "";
                _resultBody.text = $"{tally}\n\n{reward}\n경험치  +{result.rewardXp}{level}{city}{risk}";
                return;
            }

            _resultTitle.text = mission.Retreated ? "후퇴했어요"
                : result.facilitiesSaved == 0 ? "시설이 모두 멈췄어요"
                : "시간이 다 됐어요";
            _resultTitle.color = Color.white;
            // 전투에 졌다고 은행 잔액이나 게임 내 신용이 줄지 않는다 (docs/COMBAT_ECONOMY.md).
            _resultBody.text = $"{tally}{risk}\n\n실패해도 은행 잔액과 신용에는 아무 영향이 없어요.\n" +
                               "장비를 강화하거나 EMP 장치·탐지 센서를 챙겨 다시 도전해 보세요!";
        }
    }
}
