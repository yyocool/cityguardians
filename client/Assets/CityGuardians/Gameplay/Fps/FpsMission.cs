using System;
using System.Collections.Generic;
using CityGuardians.Domain;
using CityGuardians.Inputs;
using CityGuardians.Services;
using UnityEngine;
using UnityEngine.AI;

namespace CityGuardians.Gameplay
{
    public enum FpsPhase
    {
        /// <summary>서버에 임무 시작을 알리는 중.</summary>
        Starting,
        Active,
        /// <summary>결과를 서버에 보내는 중.</summary>
        Reporting,
        /// <summary>결과를 보내지 못했다. R 키로 다시 보낸다.</summary>
        ReportFailed,
        Won,
        Lost,
    }

    /// <summary>
    /// FPS 시내 침입 임무 한 판 (docs/FPS_MODE.md). 로봇이 침입 지점에서 나와 주요 시설로 향하고,
    /// 플레이어는 로봇을 막고 숨어 있는 시민을 대피시킨다.
    /// 여기서 정하는 것은 "무슨 일이 있었는가"뿐이다. 이겼는지, 보상이 얼마인지, 도시 위험도가
    /// 얼마나 바뀌는지는 서버가 보고를 검증한 뒤에 정한다 (FpsRules, CityRiskRules).
    /// </summary>
    public sealed class FpsMission : MonoBehaviour, IEnemyOwner
    {
        /// <summary>결과 화면이 뜬 뒤 E 입력을 받기까지의 시간(초).</summary>
        private const float ResultInputDelay = 0.6f;
        /// <summary>길을 따라가는 로봇이 이 거리 안의 플레이어를 알아채고 덤빈다 (m).</summary>
        private const float NoticeRange = 7f;
        /// <summary>시설에 닿았다고 인정하는 거리 (m). 길을 지나쳤다는 판정만으로는 시설이 깎이지 않는다.</summary>
        private const float ReachRadius = 4f;

        public static FpsMission Current { get; private set; }

        public FpsMissionDef Def { get; private set; }
        public FpsPhase Phase { get; private set; } = FpsPhase.Starting;
        /// <summary>임무가 시작된 뒤 흐른 시간(초).</summary>
        public float Elapsed { get; private set; }
        public float TimeLeft => Mathf.Max(0f, Def.timeLimitSeconds - Elapsed);
        public int Killed { get; private set; }
        /// <summary>시설에 닿아 버린 로봇의 수.</summary>
        public int Leaked { get; private set; }
        public int CiviliansSaved { get; private set; }
        public bool Retreated { get; private set; }
        /// <summary>이 임무가 막으려는 침공. 없으면 순찰 출동이다.</summary>
        public string InvasionId { get; private set; }
        /// <summary>임무가 반영될 도시. 이방인이면 비어 있다.</summary>
        public string RegionCode { get; private set; }
        public float LastFacilityHitTime { get; private set; } = -999f;

        /// <summary>서버가 돌려준 결과. 결과 화면이 이것을 그린다.</summary>
        public FpsResultDto Result { get; private set; }
        public string ResultError { get; private set; }

        public bool IsActive => Phase == FpsPhase.Active;
        public bool IsOver => Phase == FpsPhase.Won || Phase == FpsPhase.Lost;
        public IReadOnlyList<FpsFacility> Facilities => _facilities;
        public IReadOnlyList<FpsCivilian> Civilians => _civilians;
        /// <summary>지금 살아 있는 로봇.</summary>
        public IReadOnlyList<EnemyAgent> Enemies => _alive;
        /// <summary>아직 나오지 않은 로봇의 수.</summary>
        public int PendingSpawns => _schedule.Count - _scheduleIndex;
        /// <summary>남은 로봇 (나올 것 + 살아 있는 것).</summary>
        public int EnemiesLeft => PendingSpawns + _alive.Count;

        public int FacilitiesAlive
        {
            get
            {
                int alive = 0;
                for (int i = 0; i < _facilities.Count; i++)
                    if (_facilities[i].IsAlive) alive++;
                return alive;
            }
        }

        private struct SpawnEvent
        {
            public float time;
            public string enemyCode;
        }

        private readonly List<FpsFacility> _facilities = new List<FpsFacility>();
        private readonly List<FpsCivilian> _civilians = new List<FpsCivilian>();
        private readonly List<EnemyAgent> _alive = new List<EnemyAgent>();
        private readonly Dictionary<EnemyAgent, FpsFacility> _targets = new Dictionary<EnemyAgent, FpsFacility>();
        private readonly Dictionary<string, int> _kills = new Dictionary<string, int>();
        private readonly List<SpawnEvent> _schedule = new List<SpawnEvent>();
        private readonly System.Random _rng = new System.Random();
        private int _scheduleIndex;
        private int _spawned;
        private readonly List<Vector3> _spawnPoints = new List<Vector3>();
        private string _sessionId;
        private FpsReport _report;
        private float _resultShownAt;
        private bool _leaving;
        private bool _leaveRequested;
        private bool _lastMinuteWarned;

        /// <param name="spawnPoints">침입 지점. 로봇이 차례로 돌아가며 나온다.</param>
        public static FpsMission Create(Transform parent, string missionCode, IList<Vector3> spawnPoints, IList<FpsFacility> facilities)
        {
            FpsMissionDef def = GameSession.Current.Defs.combat.FindFpsMission(missionCode)
                                ?? throw new InvalidOperationException("정의에 없는 FPS 임무: " + missionCode);
            var go = new GameObject("FpsMission_" + missionCode);
            go.transform.SetParent(parent, false);
            var mission = go.AddComponent<FpsMission>();
            mission.Def = def;
            mission._spawnPoints.AddRange(spawnPoints);
            for (int i = 0; i < facilities.Count && i < def.facilityCount; i++)
            {
                facilities[i].Bind(mission, def.facilityHp);
                mission._facilities.Add(facilities[i]);
            }
            for (int g = 0; g < def.groups.Count; g++)
            {
                WaveGroupDef group = def.groups[g];
                for (int i = 0; i < group.count; i++)
                    mission._schedule.Add(new SpawnEvent { time = group.delay + i * group.interval, enemyCode = group.enemyCode });
            }
            mission._schedule.Sort((a, b) => a.time.CompareTo(b.time));
            Current = mission;
            mission.Begin();
            return mission;
        }

        public void AddCivilian(FpsCivilian civilian)
        {
            if (civilian != null && _civilians.Count < Def.civilianCount) _civilians.Add(civilian);
        }

        private void OnDestroy()
        {
            GameState.Unlock(GameState.LockResult);
            if (Current == this) Current = null;
        }

        private void Begin()
        {
            // 걸린 시간은 서버가 잰다. 서버가 시작을 받아 준 뒤에 로봇이 나오기 시작한다.
            CombatService.StartFps(Def.code, result =>
            {
                if (this == null) return;
                if (!result.ok)
                {
                    Debug.LogWarning($"FPS 임무 시작 실패 ({Def.code}): {result.errorCode} {result.message}");
                    GameEvents.ShowToast(string.IsNullOrEmpty(result.message)
                        ? "임무를 시작하지 못했어요. 잠시 뒤에 다시 시도해 주세요."
                        : result.message);
                    Leave();
                    return;
                }
                _sessionId = result.data.sessionId;
                InvasionId = result.data.invasionId;
                RegionCode = result.data.regionCode;
                Phase = FpsPhase.Active;
                Sfx.Play(Sfx.Id.Alarm, 0.8f);
                GameEvents.ShowToast(string.IsNullOrEmpty(InvasionId)
                    ? "시내 순찰 출동! 로봇이 주요 시설로 가기 전에 막으세요."
                    : "시내 침입 발생! 로봇이 주요 시설로 가기 전에 막으세요.");
            });
        }

        // ── 매 프레임 ───────────────────────────────────────────

        private void Update()
        {
            if (_leaving) return;
            switch (Phase)
            {
                case FpsPhase.Active:
                    UpdateActive(Time.deltaTime);
                    break;

                case FpsPhase.ReportFailed:
                    if (PlayerInputHub.Current.RetryPressed) SendResult();
                    break;

                case FpsPhase.Won:
                case FpsPhase.Lost:
                    // 시민 곁에서 E를 누르던 손이 결과 화면을 바로 닫지 않게 잠깐 기다린다.
                    if (!GameState.IsPaused && Time.unscaledTime - _resultShownAt >= ResultInputDelay
                        && PlayerInputHub.Current.InteractPressed)
                        Leave();
                    break;
            }
            UpdateGuide();
        }

        private void UpdateActive(float dt)
        {
            Elapsed += dt;
            while (_scheduleIndex < _schedule.Count && _schedule[_scheduleIndex].time <= Elapsed)
            {
                Spawn(_schedule[_scheduleIndex].enemyCode);
                _scheduleIndex++;
            }
            if (Phase != FpsPhase.Active) return;

            if (!_lastMinuteWarned && TimeLeft <= 60f)
            {
                _lastMinuteWarned = true;
                Sfx.Play(Sfx.Id.Alarm, 0.5f);
                GameEvents.ShowToast("남은 시간 1분! 서둘러 로봇을 정리하세요.");
            }

            if (FacilitiesAlive == 0 || Elapsed >= Def.timeLimitSeconds)
            {
                Finish(false);
                return;
            }
            if (_scheduleIndex >= _schedule.Count && _alive.Count == 0) Finish(true);
        }

        private void UpdateGuide()
        {
            if (Phase != FpsPhase.Active)
            {
                GameEvents.SetObjective(null);
                return;
            }
            int waiting = 0;
            for (int i = 0; i < _civilians.Count; i++)
                if (!_civilians[i].Saved) waiting++;
            GameEvents.SetObjective(waiting > 0
                ? $"로봇을 막고 시설을 지키세요 · 숨어 있는 시민 {waiting}명을 대피시키세요"
                : "로봇을 막고 시설을 지키세요");
        }

        // ── 로봇 ────────────────────────────────────────────────

        private void Spawn(string enemyCode)
        {
            EnemyDef def = GameSession.Current.Defs.FindEnemy(enemyCode);
            if (def == null)
            {
                Debug.LogError("정의에 없는 적: " + enemyCode);
                return;
            }
            FpsFacility target = NextTarget();
            if (target == null) return;

            Vector2 jitter = UnityEngine.Random.insideUnitCircle * 1.5f;
            Vector3 candidate = _spawnPoints[_spawned % _spawnPoints.Count] + new Vector3(jitter.x, 0f, jitter.y);
            if (!NavMesh.SamplePosition(candidate, out NavMeshHit hit, 3f, NavMesh.AllAreas))
            {
                Debug.LogError("로봇이 나올 자리를 찾지 못했습니다 (길 찾기 지도를 확인하세요).");
                return;
            }

            EnemyAgent enemy = EnemyAgent.Spawn(def, hit.position, this, _rng);
            enemy.transform.SetParent(transform, true);
            enemy.noticeRange = NoticeRange;
            _alive.Add(enemy);
            _spawned++;
            Send(enemy, target);
        }

        /// <summary>나오는 차례대로 남은 시설을 돌아가며 노린다. 한 곳만 지켜서는 막을 수 없다.</summary>
        private FpsFacility NextTarget()
        {
            int count = _facilities.Count;
            for (int i = 0; i < count; i++)
            {
                FpsFacility candidate = _facilities[(_spawned + i) % count];
                if (candidate.IsAlive) return candidate;
            }
            return null;
        }

        private void Send(EnemyAgent enemy, FpsFacility target)
        {
            _targets[enemy] = target;
            Vector2 offset = UnityEngine.Random.insideUnitCircle * 0.8f;
            enemy.FollowRoute(target.Route, new Vector3(offset.x, 0f, offset.y));
        }

        /// <summary>노리던 시설이 멈췄으면 가장 가까운 다른 시설로 향한다.</summary>
        private void Retarget(EnemyAgent enemy)
        {
            FpsFacility best = null;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < _facilities.Count; i++)
            {
                FpsFacility candidate = _facilities[i];
                if (!candidate.IsAlive) continue;
                float distance = (candidate.ApproachPoint - enemy.transform.position).sqrMagnitude;
                if (distance < bestDistance) { bestDistance = distance; best = candidate; }
            }
            if (best != null) Send(enemy, best);
        }

        /// <summary>로봇이 길의 끝(시설 앞)에 닿았다.</summary>
        internal void OnEnemyReached(EnemyAgent enemy, FpsFacility facility)
        {
            if (Phase != FpsPhase.Active || enemy == null || enemy.IsGone) return;
            if (!facility.IsAlive)
            {
                Retarget(enemy);
                return;
            }
            Vector3 to = facility.ApproachPoint - enemy.transform.position;
            to.y = 0f;
            if (to.magnitude > ReachRadius)
            {
                // 길을 지나쳤다고 본 것일 뿐 아직 닿지 않았다. 지금 자리에서 다시 길을 잡는다.
                Send(enemy, facility);
                return;
            }

            Leaked++;
            LastFacilityHitTime = Time.time;
            _alive.Remove(enemy);
            _targets.Remove(enemy);
            enemy.Despawn(true);
            facility.TakeDamage(Mathf.Max(1, enemy.Def.baseDamage));
            Sfx.Play(Sfx.Id.Alarm, 0.5f);
            if (FirstPersonCamera.Instance != null) FirstPersonCamera.Instance.Shake(0.05f);

            if (facility.IsAlive)
            {
                GameEvents.ShowToast($"{facility.DisplayName} 피해!  내구도 {facility.Hp} / {facility.MaxHp}");
                return;
            }

            GameEvents.ShowToast($"{facility.DisplayName} 가동 중단! 남은 시설을 지키세요.");
            // 그 시설로 가던 로봇들은 다른 시설로 방향을 바꾼다.
            EnemyAgent[] rest = _alive.ToArray();
            for (int i = 0; i < rest.Length; i++)
                if (rest[i] != null && !rest[i].IsGone && _targets.TryGetValue(rest[i], out FpsFacility aim) && aim == facility)
                    Retarget(rest[i]);
        }

        // ── IEnemyOwner ─────────────────────────────────────────

        public bool ContainsLeash(Vector3 point) => true;

        public void OnEnemyDied(EnemyAgent enemy, bool byPlayer, Vector3 center)
        {
            _alive.Remove(enemy);
            _targets.Remove(enemy);
            if (Phase != FpsPhase.Active) return;
            Killed++;
            _kills.TryGetValue(enemy.Def.code, out int count);
            _kills[enemy.Def.code] = count + 1;
        }

        // ── 장비가 쓰는 조회 ────────────────────────────────────

        /// <summary>반경 안의 로봇을 results 에 더한다 (EMP 장치, 충격파). 돌려주는 값은 더한 수.</summary>
        public int CollectEnemies(Vector3 center, float radius, List<EnemyAgent> results)
        {
            int added = 0;
            for (int i = 0; i < _alive.Count; i++)
            {
                EnemyAgent enemy = _alive[i];
                if (enemy == null || enemy.IsGone) continue;
                Vector3 to = enemy.transform.position - center;
                to.y = 0f;
                if (to.magnitude - enemy.Radius * 0.5f > radius) continue;
                results.Add(enemy);
                added++;
            }
            return added;
        }

        /// <summary>범위 안에서 가장 가까운 로봇 (지원 드론). 없으면 null.</summary>
        public EnemyAgent Nearest(Vector3 position, float range)
        {
            EnemyAgent best = null;
            float bestDistance = range;
            for (int i = 0; i < _alive.Count; i++)
            {
                EnemyAgent enemy = _alive[i];
                if (enemy == null || enemy.IsGone || enemy.State == EnemyState.Spawn) continue;
                Vector3 to = enemy.transform.position - position;
                to.y = 0f;
                float distance = to.magnitude;
                if (distance > bestDistance) continue;
                bestDistance = distance;
                best = enemy;
            }
            return best;
        }

        // ── 시민 ────────────────────────────────────────────────

        internal void OnCivilianSaved(FpsCivilian civilian)
        {
            if (Phase != FpsPhase.Active) return;
            CiviliansSaved++;
            Sfx.Play(Sfx.Id.LevelUp, 0.5f);
            GameEvents.ShowToast($"시민 대피 완료!  {CiviliansSaved} / {_civilians.Count}");
        }

        // ── 끝 ──────────────────────────────────────────────────

        /// <summary>플레이어가 스스로 물러난다. 실패로 기록되고 보상은 없다.</summary>
        public void Retreat()
        {
            if (Phase == FpsPhase.Starting)
            {
                Leave();
                return;
            }
            if (Phase != FpsPhase.Active) return;
            Retreated = true;
            Finish(false);
        }

        private void Finish(bool win)
        {
            if (Phase != FpsPhase.Active) return;
            Phase = FpsPhase.Reporting;
            _schedule.Clear();
            _scheduleIndex = 0;

            EnemyAgent[] rest = _alive.ToArray();
            _alive.Clear();
            _targets.Clear();
            for (int i = 0; i < rest.Length; i++)
                if (rest[i] != null) rest[i].Despawn(true);

            _report = new FpsReport
            {
                result = win ? BattleResults.Win : BattleResults.Lose,
                facilitiesSaved = FacilitiesAlive,
                civiliansSaved = CiviliansSaved,
            };
            foreach (KeyValuePair<string, int> pair in _kills) _report.kills.Add(new KillCount(pair.Key, pair.Value));
            SendResult();
        }

        private void SendResult()
        {
            Phase = FpsPhase.Reporting;
            ResultError = null;
            // 같은 임무를 다시 보내도 서버는 처음 결과를 그대로 돌려준다 (두 번 지급하지 않는다).
            CombatService.FinishFps(_sessionId, _report, result =>
            {
                if (this == null) return;
                if (!result.ok)
                {
                    Debug.LogWarning($"FPS 임무 결과 보고 실패: {result.errorCode} {result.message}");
                    ResultError = string.IsNullOrEmpty(result.message) ? "결과를 보내지 못했어요." : result.message;
                    Phase = FpsPhase.ReportFailed;
                    return;
                }

                Result = result.data;
                if (!result.data.accepted)
                    Debug.LogWarning("FPS 임무 결과가 인정되지 않음: " + result.data.rejectReason);

                bool won = result.data.accepted && result.data.result == BattleResults.Win;
                Phase = won ? FpsPhase.Won : FpsPhase.Lost;
                _resultShownAt = Time.unscaledTime;
                GameState.Lock(GameState.LockResult);
                Sfx.Play(won ? Sfx.Id.Victory : Sfx.Id.Defeat);
            });
        }

        private void Leave()
        {
            if (_leaving) return;
            _leaving = true;
            GameState.Unlock(GameState.LockResult);
            GameEvents.SetObjective(null);
        }

        private void LateUpdate()
        {
            // 장면이 아직 바뀌는 중이면 끝난 뒤에 떠난다.
            if (!_leaving || _leaveRequested || GameRoot.Instance.IsBusy) return;
            _leaveRequested = true;
            GameRoot.Instance.LoadScene(SceneNames.City, SpawnIds.DefenseGate);
        }
    }

    /// <summary>
    /// 지켜야 하는 주요 시설 (발전 설비, 통신 중계기, 급수 설비). 로봇이 닿으면 내구도가 깎이고,
    /// 다 깎이면 가동을 멈춘다. 임무 안에서의 상태일 뿐이고, 도시의 시설 피해는 서버가 결과를 보고 정한다.
    /// </summary>
    public sealed class FpsFacility : MonoBehaviour
    {
        private static readonly Color Working = new Color(0.45f, 0.95f, 1f);
        private static readonly Color Hurt = new Color(1f, 0.72f, 0.25f);
        private static readonly Color Stopped = new Color(0.30f, 0.30f, 0.34f);

        public string Code { get; private set; }
        public string DisplayName { get; private set; }
        public int Hp { get; private set; }
        public int MaxHp { get; private set; }
        public bool IsAlive => Hp > 0;
        /// <summary>로봇이 향하는 자리 (시설 바로 앞).</summary>
        public Vector3 ApproachPoint { get; private set; }
        public FpsRoute Route { get; private set; }
        public float LastHitTime { get; private set; } = -999f;

        private GameObject _light;
        private WorldLabel _label;
        private FpsMission _mission;

        /// <param name="forward">시설이 바라보는 쪽 (로봇이 다가오는 쪽).</param>
        public static FpsFacility Create(Transform parent, string code, string displayName, Vector3 position, Vector3 forward,
            Color bodyColor)
        {
            Transform root = Blocks.Pivot(parent, "Facility_" + code, position);
            root.localRotation = Quaternion.LookRotation(forward, Vector3.up);
            var facility = root.gameObject.AddComponent<FpsFacility>();
            facility.Code = code;
            facility.DisplayName = displayName;
            facility.ApproachPoint = position + forward.normalized * 2.7f;

            Color dark = new Color(0.20f, 0.22f, 0.27f);
            Blocks.Box(root, "Base", new Vector3(0f, 0.2f, 0f), new Vector3(3.2f, 0.4f, 3.2f), dark, true);
            Blocks.Box(root, "Body", new Vector3(0f, 1.5f, -0.2f), new Vector3(2.6f, 2.2f, 2.2f), bodyColor, true);
            Blocks.Box(root, "Roof", new Vector3(0f, 2.7f, -0.2f), new Vector3(2.9f, 0.2f, 2.5f), dark);
            Blocks.Cylinder(root, "Mast", new Vector3(0.8f, 3.5f, -0.6f), new Vector3(0.16f, 0.7f, 0.16f), dark);
            facility._light = Blocks.Sphere(root, "Light", new Vector3(0.8f, 4.3f, -0.6f), new Vector3(0.45f, 0.45f, 0.45f),
                Working, false, true);
            Blocks.Box(root, "Screen", new Vector3(0f, 1.6f, 0.92f), new Vector3(1.6f, 0.9f, 0.05f), Working, false, true);
            facility._label = WorldLabel.Create(root, new Vector3(0f, 5.0f, 0f), displayName, 0.45f, Color.white);

            facility.Route = new FpsRoute(facility);
            return facility;
        }

        internal void Bind(FpsMission mission, int maxHp)
        {
            _mission = mission;
            MaxHp = Mathf.Max(1, maxHp);
            Hp = MaxHp;
        }

        internal void OnEnemyReached(EnemyAgent enemy)
        {
            if (_mission != null) _mission.OnEnemyReached(enemy, this);
        }

        public void TakeDamage(int amount)
        {
            if (!IsAlive || amount <= 0) return;
            Hp = Mathf.Max(0, Hp - amount);
            LastHitTime = Time.time;
            Color color = !IsAlive ? Stopped : Hp * 2 <= MaxHp ? Hurt : Working;
            if (_light != null) Blocks.SetColor(_light, color, true);
            Transform screen = transform.Find("Screen");
            if (screen != null) Blocks.SetColor(screen.gameObject, color, true);
            Fx.Shatter(transform.position + Vector3.up * 1.6f, new[] { color, Stopped }, IsAlive ? 5 : 16, 0.14f);
            if (!IsAlive) Sfx.Play(Sfx.Id.Explosion, 0.7f);
        }
    }

    /// <summary>침입 지점에서 한 시설까지의 길. 길 찾기 지도가 건물 사이의 길을 잡아 준다.</summary>
    public sealed class FpsRoute : IEnemyRoute
    {
        public FpsFacility Facility { get; }
        public IReadOnlyList<Vector3> Waypoints => _points;

        private readonly List<Vector3> _points = new List<Vector3>();

        public FpsRoute(FpsFacility facility)
        {
            Facility = facility;
            _points.Add(facility.ApproachPoint);
        }

        public void OnEnemyReachedGoal(EnemyAgent enemy) => Facility.OnEnemyReached(enemy);

        /// <summary>FPS 임무에는 로봇이 부수는 타워가 없다.</summary>
        public void CollectStructures(Vector3 center, float radius, List<IStructure> results) { }
    }

    /// <summary>
    /// 숨어 있는 시민 NPC. 가까이 가서 E 키로 대피시킨다. 로봇은 시민을 공격하지 않는다:
    /// 대피시키지 못한 시민은 보너스를 받지 못할 뿐이다.
    /// </summary>
    public sealed class FpsCivilian : MonoBehaviour
    {
        private const float LeaveSeconds = 1.6f;

        public bool Saved { get; private set; }
        public Vector3 Position => transform.position;

        private FpsMission _mission;
        private BlockCharacter _character;
        private WorldLabel _label;
        private Vector3 _exitDirection;
        private float _leaveLeft;

        public static FpsCivilian Create(Transform parent, FpsMission mission, int index, Vector3 position, float yaw,
            Vector3 exitDirection)
        {
            Transform root = Blocks.Pivot(parent, "Civilian_" + index, position);
            root.localRotation = Quaternion.Euler(0f, yaw, 0f);
            var civilian = root.gameObject.AddComponent<FpsCivilian>();
            civilian._mission = mission;
            civilian._exitDirection = exitDirection.sqrMagnitude > 0.001f ? exitDirection.normalized : Vector3.back;
            // 실제 플레이어처럼 보이지 않게, 정해진 씨앗으로 만든 주민 모습과 "시민" 이름표를 쓴다.
            civilian._character = BlockCharacter.Create(root, Appearance.FromSeed("fps_civilian_" + index));
            // 겁을 먹고 웅크린 모습
            civilian._character.Body.localRotation = Quaternion.Euler(18f, 0f, 0f);
            civilian._character.transform.localScale = new Vector3(1f, 0.86f, 1f);
            civilian._label = WorldLabel.Create(root, new Vector3(0f, 2.1f, 0f), "시민 · 도와주세요!", 0.26f,
                new Color(1f, 0.92f, 0.5f));

            Interactable interact = Interactable.Add(root.gameObject, "시민 대피시키기", civilian.Evacuate, 2.4f,
                () => !civilian.Saved && mission != null && mission.IsActive);
            interact.offset = Vector3.zero;
            mission.AddCivilian(civilian);
            return civilian;
        }

        private void Evacuate()
        {
            if (Saved || _mission == null || !_mission.IsActive) return;
            Saved = true;
            _leaveLeft = LeaveSeconds;
            _character.Body.localRotation = Quaternion.identity;
            _character.transform.localScale = Vector3.one;
            transform.rotation = Quaternion.LookRotation(_exitDirection, Vector3.up);
            if (_label != null) Destroy(_label.gameObject);
            Fx.FloatingText(transform.position + Vector3.up * 2.2f, "고마워요!", new Color(0.6f, 1f, 0.7f), 0.36f);
            _mission.OnCivilianSaved(this);
        }

        private void Update()
        {
            if (!Saved || _leaveLeft <= 0f) return;
            float dt = Time.deltaTime;
            _leaveLeft -= dt;
            // 안전한 곳으로 달려가며 작아지다 사라진다.
            transform.position += _exitDirection * (5f * dt);
            float scale = Mathf.Clamp01(_leaveLeft / 0.5f);
            transform.localScale = Vector3.one * Mathf.Max(0.05f, scale);
            if (_leaveLeft <= 0f) gameObject.SetActive(false);
        }
    }
}
