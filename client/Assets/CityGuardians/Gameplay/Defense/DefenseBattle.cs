using System;
using System.Collections.Generic;
using CityGuardians.Domain;
using CityGuardians.Inputs;
using CityGuardians.Services;
using UnityEngine;
using UnityEngine.AI;

namespace CityGuardians.Gameplay
{
    public enum DefensePhase
    {
        /// <summary>서버에 전투 시작을 알리는 중.</summary>
        Starting,
        /// <summary>첫 웨이브 전. 시간 제한 없이 타워를 세운다.</summary>
        Prep,
        Wave,
        /// <summary>웨이브 사이의 쉬는 시간.</summary>
        Rest,
        /// <summary>결과를 서버에 보내는 중.</summary>
        Reporting,
        /// <summary>결과를 보내지 못했다. 다시 보낼 수 있다.</summary>
        ReportFailed,
        Won,
        Lost,
    }

    /// <summary>
    /// 방어전 한 판 (docs/TOWER_DEFENSE.md). 웨이브를 내보내고, 에너지와 도시 방어 체력을 관리하며,
    /// 끝나면 결과를 서버에 보고한다. 보상은 서버가 스테이지 정의에서 계산한 값만 쓴다.
    /// </summary>
    public sealed class DefenseBattle : MonoBehaviour, IEnemyOwner, IEnemyRoute
    {
        private const float ResultInputDelay = 1.0f;
        private static readonly Color EnergyColor = new Color(0.45f, 0.95f, 1f);

        private struct SpawnEvent
        {
            public float time;
            public string enemyCode;
        }

        public static DefenseBattle Current { get; private set; }

        public StageDef Stage { get; private set; }
        public DefensePhase Phase { get; private set; } = DefensePhase.Starting;
        public int Energy { get; private set; }
        public int BaseHp { get; private set; }
        /// <summary>지금 진행 중이거나 마지막으로 시작한 웨이브 (1부터). 아직 시작 전이면 0.</summary>
        public int WaveNumber { get; private set; }
        public int WavesCleared { get; private set; }
        public float RestLeft { get; private set; }
        public int TowersBuilt { get; private set; }
        public int TotalKills { get; private set; }
        public int AliveCount => _alive.Count;
        public IReadOnlyList<EnemyAgent> Alive => _alive;
        /// <summary>이번 웨이브에서 아직 나오지 않은 적의 수.</summary>
        public int PendingSpawns => _schedule.Count - _scheduleIndex;
        /// <summary>도시 방어 체력이 마지막으로 깎인 때 (Time.time). 화면 표시가 깜빡이는 데 쓴다.</summary>
        public float LastBaseHitTime { get; private set; } = -999f;
        public bool Retreated { get; private set; }

        /// <summary>서버가 돌려준 결과. 결과 화면이 이것을 그린다.</summary>
        public BattleResultDto Result { get; private set; }
        public string ResultError { get; private set; }

        public bool CanBuild => Phase == DefensePhase.Prep || Phase == DefensePhase.Wave || Phase == DefensePhase.Rest;
        public bool IsOver => Phase == DefensePhase.Won || Phase == DefensePhase.Lost;

        public IReadOnlyList<Vector3> Waypoints => _waypoints;

        private readonly List<Vector3> _waypoints = new List<Vector3>();
        private readonly List<EnemyAgent> _alive = new List<EnemyAgent>();
        private readonly List<DefenseTower> _towers = new List<DefenseTower>();
        private readonly Dictionary<string, int> _kills = new Dictionary<string, int>();
        private readonly List<SpawnEvent> _schedule = new List<SpawnEvent>();
        private readonly System.Random _rng = new System.Random();
        private int _scheduleIndex;
        private float _waveTime;
        private Vector3 _spawnPoint;
        private string _sessionId;
        private BattleReport _report;
        private float _resultShownAt;
        private bool _leaving;
        private bool _leaveRequested;
        private bool _leakToastShown;
        private float _lastAlarm = -999f;

        public static DefenseBattle Create(Transform parent, string stageCode, Vector3 spawnPoint, IList<Vector3> waypoints)
        {
            StageDef stage = GameSession.Current.Defs.FindStage(stageCode)
                             ?? throw new InvalidOperationException("정의에 없는 방어전: " + stageCode);
            var go = new GameObject("DefenseBattle_" + stageCode);
            go.transform.SetParent(parent, false);
            var battle = go.AddComponent<DefenseBattle>();
            battle.Stage = stage;
            battle._spawnPoint = spawnPoint;
            battle._waypoints.AddRange(waypoints);
            battle.Energy = stage.startEnergy;
            battle.BaseHp = stage.baseHp;
            Current = battle;
            battle.Begin();
            return battle;
        }

        private void OnDestroy()
        {
            GameState.Unlock(GameState.LockResult);
            if (Current == this) Current = null;
        }

        private void Begin()
        {
            GameSession session = GameSession.Current;
            session.Backend.StartBattle(Stage.code, result =>
            {
                if (this == null) return;
                if (!result.ok)
                {
                    Debug.LogWarning($"방어전 시작 실패 ({Stage.code}): {result.errorCode} {result.message}");
                    GameEvents.ShowToast("방어전을 시작하지 못했어요. 잠시 뒤에 다시 시도해 주세요.");
                    Leave();
                    return;
                }
                _sessionId = result.data.sessionId;
                Phase = DefensePhase.Prep;
            });
        }

        // ── 매 프레임 ───────────────────────────────────────────

        private void Update()
        {
            if (_leaving) return;
            switch (Phase)
            {
                case DefensePhase.Wave:
                    UpdateWave(Time.deltaTime);
                    break;

                case DefensePhase.Rest:
                    RestLeft -= Time.deltaTime;
                    if (RestLeft <= 0f) StartNextWave();
                    break;

                case DefensePhase.ReportFailed:
                    if (PlayerInputHub.Current.RetryPressed) SendResult();
                    break;

                case DefensePhase.Won:
                case DefensePhase.Lost:
                    // 타워 앞에서 E를 누르던 손이 결과 화면을 바로 닫지 않게 잠깐 기다린다.
                    if (!_leaving && !GameState.IsPaused && Time.unscaledTime - _resultShownAt >= ResultInputDelay
                        && PlayerInputHub.Current.InteractPressed)
                        Leave();
                    break;
            }
            UpdateGuide();
        }

        private void UpdateWave(float dt)
        {
            _waveTime += dt;
            while (_scheduleIndex < _schedule.Count && _schedule[_scheduleIndex].time <= _waveTime)
            {
                Spawn(_schedule[_scheduleIndex].enemyCode);
                _scheduleIndex++;
            }

            if (Phase != DefensePhase.Wave) return; // 방어 체력이 다해 이미 끝났다
            if (_scheduleIndex < _schedule.Count || _alive.Count > 0) return;

            // 웨이브를 막아냈다
            WavesCleared = WaveNumber;
            if (WavesCleared >= Stage.waves.Count)
            {
                Finish(true);
                return;
            }
            Energy += Stage.waveClearEnergy;
            RestLeft = Stage.restSeconds;
            Phase = DefensePhase.Rest;
            Sfx.Play(Sfx.Id.LevelUp, 0.7f);
            GameEvents.ShowToast($"웨이브 {WavesCleared} 방어 성공! 에너지 +{Stage.waveClearEnergy}");
        }

        private void UpdateGuide()
        {
            switch (Phase)
            {
                case DefensePhase.Prep:
                    GameEvents.SetObjective(TowersBuilt == 0
                        ? "빛나는 자리에서 E 키로 타워를 세우세요"
                        : "준비되면 신호기에서 E 키로 웨이브를 시작하세요");
                    break;
                case DefensePhase.Wave:
                    GameEvents.SetObjective($"웨이브 {WaveNumber}/{Stage.waves.Count}: 로봇이 방어선에 닿기 전에 막으세요");
                    break;
                case DefensePhase.Rest:
                    GameEvents.SetObjective($"다음 웨이브까지 {Mathf.CeilToInt(Mathf.Max(0f, RestLeft))}초 · 타워를 세우거나 강화하세요");
                    break;
                default:
                    GameEvents.SetObjective(null);
                    break;
            }
        }

        // ── 웨이브 ──────────────────────────────────────────────

        /// <summary>첫 웨이브는 플레이어가 시작한다. 쉬는 시간에 부르면 기다리지 않고 바로 시작한다.</summary>
        public void StartNextWave()
        {
            if (Phase != DefensePhase.Prep && Phase != DefensePhase.Rest) return;
            if (WaveNumber >= Stage.waves.Count) return;

            WaveNumber++;
            WaveDef wave = Stage.waves[WaveNumber - 1];
            _schedule.Clear();
            _scheduleIndex = 0;
            for (int g = 0; g < wave.groups.Count; g++)
            {
                WaveGroupDef group = wave.groups[g];
                for (int i = 0; i < group.count; i++)
                    _schedule.Add(new SpawnEvent { time = group.delay + i * group.interval, enemyCode = group.enemyCode });
            }
            _schedule.Sort((a, b) => a.time.CompareTo(b.time));
            _waveTime = 0f;
            Phase = DefensePhase.Wave;
            Sfx.Play(Sfx.Id.Alarm, 0.8f);
            GameEvents.ShowToast(WaveNumber >= Stage.waves.Count
                ? "마지막 웨이브! 끝까지 막아내세요!"
                : $"웨이브 {WaveNumber} 시작!");
        }

        private void Spawn(string enemyCode)
        {
            EnemyDef def = GameSession.Current.Defs.FindEnemy(enemyCode);
            if (def == null)
            {
                Debug.LogError("정의에 없는 적: " + enemyCode);
                return;
            }

            bool big = def.staggerImmune;
            Vector2 jitter = big ? Vector2.zero : UnityEngine.Random.insideUnitCircle * 1.5f;
            Vector3 candidate = _spawnPoint + new Vector3(jitter.x, 0f, jitter.y);
            if (!NavMesh.SamplePosition(candidate, out NavMeshHit hit, 3f, NavMesh.AllAreas))
            {
                Debug.LogError("적이 나올 자리를 찾지 못했습니다 (길 찾기 지도를 확인하세요).");
                return;
            }

            EnemyAgent enemy = EnemyAgent.Spawn(def, hit.position, this, _rng);
            enemy.transform.SetParent(transform, true);
            Vector2 offset = big ? Vector2.zero : UnityEngine.Random.insideUnitCircle * 1.3f;
            enemy.FollowRoute(this, new Vector3(offset.x, 0f, offset.y));
            _alive.Add(enemy);

            if (def.attacksStructures)
                GameEvents.ShowToast($"{def.name} 등장! 타워를 노려요. 바닥의 붉은 원을 피하세요!");
        }

        // ── IEnemyOwner / IEnemyRoute ───────────────────────────

        public bool ContainsLeash(Vector3 point) => true;

        /// <summary>적이 쓰러졌다. 타워나 방어대가 쓰러뜨린 적도 모두 센다.</summary>
        public void OnEnemyDied(EnemyAgent enemy, bool byPlayer, Vector3 center)
        {
            _alive.Remove(enemy);
            _kills.TryGetValue(enemy.Def.code, out int count);
            _kills[enemy.Def.code] = count + 1;
            TotalKills++;

            if (enemy.Def.rewardEnergy > 0 && CanBuild)
            {
                Energy += enemy.Def.rewardEnergy;
                Fx.FloatingText(center + Vector3.up * 0.9f, $"+{enemy.Def.rewardEnergy}", EnergyColor, 0.3f);
            }
        }

        /// <summary>적이 방어 거점에 닿았다. 도시 방어 체력이 깎이고 그 적은 사라진다 (보상 없음).</summary>
        public void OnEnemyReachedGoal(EnemyAgent enemy)
        {
            if (!_alive.Remove(enemy)) return;
            enemy.Despawn(true);
            if (!CanBuild) return;

            BaseHp = Mathf.Max(0, BaseHp - Mathf.Max(1, enemy.Def.baseDamage));
            LastBaseHitTime = Time.time;
            GameEvents.RaisePlayerHurt();
            if (OrbitCamera.Instance != null) OrbitCamera.Instance.Shake(0.1f);
            if (Time.time - _lastAlarm > 1.2f)
            {
                _lastAlarm = Time.time;
                Sfx.Play(Sfx.Id.Hurt, 0.8f);
            }
            if (!_leakToastShown)
            {
                _leakToastShown = true;
                GameEvents.ShowToast("로봇이 방어선에 닿았어요! 도시 방어 체력이 줄었어요.");
            }

            if (BaseHp <= 0) Finish(false);
        }

        public void CollectStructures(Vector3 center, float radius, List<IStructure> results)
        {
            for (int i = 0; i < _towers.Count; i++)
            {
                DefenseTower tower = _towers[i];
                if (tower == null || !tower.IsAlive) continue;
                Vector3 to = tower.Position - center;
                to.y = 0f;
                if (to.magnitude - tower.Radius <= radius) results.Add(tower);
            }
        }

        // ── 타워 ────────────────────────────────────────────────

        /// <summary>자리에 타워를 세운다. 에너지가 모자라거나 잠긴 타워면 세우지 않는다.</summary>
        public bool TryBuild(BuildSlot slot, TowerDef def)
        {
            if (!CanBuild || slot == null || slot.Tower != null || def == null) return false;
            GameSession session = GameSession.Current;
            if (session == null || !def.IsBuildable || !session.IsTowerUnlocked(def.code)) return false;
            if (def.maxPerBattle > 0 && CountTowers(def.code) >= def.maxPerBattle)
            {
                // 이름에 따라 조사가 달라지므로 조사가 필요 없는 문장으로 쓴다.
                GameEvents.ShowToast($"{def.name}: 한 전투에 {def.maxPerBattle}개까지 세울 수 있어요.");
                return false;
            }
            if (Energy < def.energyCost)
            {
                GameEvents.ShowToast("에너지가 모자라요. 로봇을 물리치면 에너지를 얻어요.");
                return false;
            }

            Energy -= def.energyCost;
            DefenseTower tower = DefenseTower.Build(transform, def, slot.transform.position, this);
            _towers.Add(tower);
            slot.Place(tower);
            TowersBuilt++;
            Sfx.Play(Sfx.Id.Build);
            return true;
        }

        /// <summary>전투 중 강화. 에너지를 쓰고 이번 전투에서만 유지된다.</summary>
        public bool TryUpgrade(DefenseTower tower)
        {
            if (!CanBuild || tower == null || !tower.IsAlive || !tower.CanUpgrade) return false;
            int cost = tower.Def.upgradeEnergyCost;
            if (Energy < cost)
            {
                GameEvents.ShowToast("에너지가 모자라요. 로봇을 물리치면 에너지를 얻어요.");
                return false;
            }
            Energy -= cost;
            tower.Upgrade();
            Sfx.Play(Sfx.Id.Build);
            return true;
        }

        public void OnTowerDestroyed(DefenseTower tower)
        {
            _towers.Remove(tower);
            if (CanBuild) GameEvents.ShowToast("타워가 부서졌어요! 빈 자리에 다시 세울 수 있어요.");
        }

        /// <summary>지금 서 있는 그 종류의 타워 수. 설치 제한(maxPerBattle)에 쓴다.</summary>
        public int CountTowers(string towerCode)
        {
            int count = 0;
            for (int i = 0; i < _towers.Count; i++)
                if (_towers[i] != null && _towers[i].IsAlive && _towers[i].Def.code == towerCode) count++;
            return count;
        }

        /// <summary>반경 안의 적을 results 에 더한다 (감속 장치, EMP 타워). 돌려주는 값은 더한 수.</summary>
        public int CollectEnemies(Vector3 center, float radius, List<EnemyAgent> results)
        {
            int added = 0;
            for (int i = 0; i < _alive.Count; i++)
            {
                EnemyAgent enemy = _alive[i];
                if (enemy == null || enemy.IsGone || enemy.State == EnemyState.Spawn) continue;
                Vector3 to = enemy.transform.position - center;
                to.y = 0f;
                if (to.magnitude - enemy.Radius * 0.5f > radius) continue;
                results.Add(enemy);
                added++;
            }
            return added;
        }

        /// <summary>사거리 안에서 방어 거점에 가장 가까이 간 적. 없으면 null.</summary>
        public EnemyAgent FindTarget(Vector3 position, float range)
        {
            EnemyAgent best = null;
            float bestProgress = float.MinValue;
            for (int i = 0; i < _alive.Count; i++)
            {
                EnemyAgent enemy = _alive[i];
                if (enemy == null || enemy.IsGone || enemy.State == EnemyState.Spawn) continue;
                Vector3 to = enemy.transform.position - position;
                to.y = 0f;
                if (to.magnitude - enemy.Radius * 0.5f > range) continue;
                if (enemy.RouteProgress > bestProgress)
                {
                    bestProgress = enemy.RouteProgress;
                    best = enemy;
                }
            }
            return best;
        }

        /// <summary>폭발: 반경 안의 적이 모두 피해를 입는다.</summary>
        public void Explode(Vector3 center, float radius, int damage, GameObject source)
        {
            Fx.Shockwave(center, radius, new Color(1f, 0.7f, 0.25f));
            Sfx.Play(Sfx.Id.Explosion, 0.6f);
            PlayerController player = PlayerController.Instance;
            if (player != null && OrbitCamera.Instance != null
                && (player.transform.position - center).sqrMagnitude < 12f * 12f)
                OrbitCamera.Instance.Shake(0.05f);

            // 쓰러지며 목록이 바뀌므로 복사해서 돈다.
            EnemyAgent[] targets = _alive.ToArray();
            for (int i = 0; i < targets.Length; i++)
            {
                EnemyAgent enemy = targets[i];
                if (enemy == null || enemy.IsGone) continue;
                Vector3 to = enemy.transform.position - center;
                to.y = 0f;
                if (to.magnitude - enemy.Radius > radius) continue;
                enemy.Health.TakeDamage(damage, source);
            }
        }

        // ── 끝 ──────────────────────────────────────────────────

        /// <summary>플레이어가 스스로 물러난다. 막아낸 웨이브만큼의 참가 보상만 받는다.</summary>
        public void Retreat()
        {
            if (!CanBuild) return;
            Retreated = true;
            Finish(false);
        }

        private void Finish(bool win)
        {
            if (!CanBuild) return;
            Phase = DefensePhase.Reporting;
            _schedule.Clear();
            _scheduleIndex = 0;

            EnemyAgent[] rest = _alive.ToArray();
            _alive.Clear();
            for (int i = 0; i < rest.Length; i++)
                if (rest[i] != null) rest[i].Despawn(true);

            _report = new BattleReport
            {
                result = win ? BattleResults.Win : BattleResults.Lose,
                baseHpLeft = BaseHp,
                wavesCleared = WavesCleared,
            };
            foreach (KeyValuePair<string, int> pair in _kills) _report.kills.Add(new KillCount(pair.Key, pair.Value));
            SendResult();
        }

        private void SendResult()
        {
            Phase = DefensePhase.Reporting;
            ResultError = null;
            GameSession session = GameSession.Current;
            if (session == null) return;
            // 같은 전투를 다시 보내도 서버는 처음 결과를 그대로 돌려준다 (두 번 지급하지 않는다).
            session.Backend.FinishBattle(_sessionId, _report, result =>
            {
                if (this == null) return;
                if (!result.ok)
                {
                    Debug.LogWarning($"방어전 결과 보고 실패: {result.errorCode} {result.message}");
                    ResultError = string.IsNullOrEmpty(result.message) ? "결과를 보내지 못했어요." : result.message;
                    Phase = DefensePhase.ReportFailed;
                    return;
                }

                if (GameSession.Current == session)
                {
                    session.ApplyBattleResult(result.data);
                    // 도시 위험도는 서버가 계산해 돌려준 값이다. 받아 둔 현황에 반영만 한다.
                    CityRiskService.Apply(result.data.city);
                }
                Result = result.data;
                if (!result.data.accepted)
                    Debug.LogWarning("방어전 결과가 인정되지 않음: " + result.data.rejectReason);

                // 웨이브를 시작하기도 전에 돌아가는 것은 실패가 아니다. 결과 화면 없이 돌아간다.
                if (Retreated && WaveNumber == 0)
                {
                    Phase = DefensePhase.Lost;
                    Leave();
                    return;
                }

                bool won = result.data.accepted && result.data.result == BattleResults.Win;
                Phase = won ? DefensePhase.Won : DefensePhase.Lost;
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
}
