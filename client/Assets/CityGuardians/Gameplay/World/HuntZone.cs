using System;
using System.Collections.Generic;
using CityGuardians.Domain;
using CityGuardians.Services;
using UnityEngine;
using UnityEngine.AI;

namespace CityGuardians.Gameplay
{
    /// <summary>
    /// 사냥 구역 (docs/COMBAT_SYSTEM.md 10.1). 적을 출현시키고, 쓰러뜨린 수를 모아 서버에 보고한다.
    /// 보상 금액은 서버 응답으로만 확정된다. 화면에는 확정 전까지 추정치를 함께 보여 준다.
    /// </summary>
    public sealed class HuntZone : MonoBehaviour, IEnemyOwner
    {
        private const float LeashMargin = 5f;
        private const float MinSpawnDistanceFromPlayer = 7f;
        private const float SessionRetrySeconds = 3f;

        public static readonly List<HuntZone> All = new List<HuntZone>();

        /// <summary>플레이어가 적을 쓰러뜨린 순간 (연출용). 보상은 아직 확정되지 않았다.</summary>
        public static event Action<HuntZone, EnemyDef> PlayerKill;
        /// <summary>서버가 보고를 처리한 뒤.</summary>
        public static event Action<HuntZone, HuntReportDto> ReportConfirmed;

        public HuntZoneDef Def { get; private set; }
        public int AliveCount => _alive.Count;
        public IReadOnlyList<EnemyAgent> Alive => _alive;

        /// <summary>보고 간격(초). 문서의 초기값 10초보다 짧게 잡아 보상이 빨리 확정되게 했다.</summary>
        public float reportInterval = 3f;

        private Vector2 _half;
        private bool _autoSpawn;
        private readonly List<EnemyAgent> _alive = new List<EnemyAgent>();
        private readonly System.Random _rng = new System.Random();
        private int _spawnedTotal;
        private float _nextSpawnAt;

        private string _sessionId;
        private bool _startingSession;
        private float _nextSessionTry;
        private int _nextSeq = 1;
        private readonly Dictionary<string, int> _pending = new Dictionary<string, int>();
        private int _pendingGc;
        private List<KillCount> _inFlight;
        private int _inFlightGc;
        private bool _waiting;
        private float _lastSendTime = -999f;
        private bool _limitToastShown;

        public static HuntZone Create(Transform parent, string zoneCode, Vector3 center, Vector2 halfExtents, bool autoSpawn)
        {
            var go = new GameObject("HuntZone_" + zoneCode);
            go.transform.SetParent(parent, false);
            go.transform.position = center;
            var zone = go.AddComponent<HuntZone>();
            zone.Def = GameSession.Current.Defs.FindZone(zoneCode)
                       ?? throw new InvalidOperationException("정의에 없는 사냥 구역: " + zoneCode);
            zone._half = halfExtents;
            zone._autoSpawn = autoSpawn && zone.Def.respawnSeconds > 0f;
            return zone;
        }

        private void OnEnable() => All.Add(this);

        private void OnDisable()
        {
            All.Remove(this);
            Flush();
        }

        public bool Contains(Vector3 point) => Inside(point, 0f);

        /// <summary>적이 목표를 쫓아갈 수 있는 범위. 구역보다 조금 넓다.</summary>
        public bool ContainsLeash(Vector3 point) => Inside(point, LeashMargin);

        private bool Inside(Vector3 point, float margin)
        {
            Vector3 local = point - transform.position;
            return Mathf.Abs(local.x) <= _half.x + margin && Mathf.Abs(local.z) <= _half.y + margin;
        }

        private void Update()
        {
            if (GameSession.Current == null) return;
            EnsureSession();

            if (_autoSpawn && _alive.Count < Def.maxAlive && Time.time >= _nextSpawnAt)
            {
                bool filling = _spawnedTotal < Def.maxAlive;
                if (TrySpawnRandom()) _nextSpawnAt = Time.time + (filling ? 0.3f : Def.respawnSeconds);
                else _nextSpawnAt = Time.time + 1f;
            }

            bool hasWork = _inFlight != null || _pending.Count > 0;
            if (hasWork && !_waiting && _sessionId != null && Time.time - _lastSendTime >= reportInterval) Send();
        }

        // ── 출현 ────────────────────────────────────────────────

        private bool TrySpawnRandom()
        {
            // 첫 번째 종류가 더 자주 나온다.
            string[] codes = Def.enemyCodes;
            int pick = _rng.Next(codes.Length + 1);
            string code = codes[pick >= codes.Length ? 0 : pick];

            PlayerController player = PlayerController.Instance;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                float x = ((float)_rng.NextDouble() * 2f - 1f) * _half.x;
                float z = ((float)_rng.NextDouble() * 2f - 1f) * _half.y;
                Vector3 candidate = transform.position + new Vector3(x, 0f, z);
                if (player != null &&
                    (player.transform.position - candidate).sqrMagnitude < MinSpawnDistanceFromPlayer * MinSpawnDistanceFromPlayer)
                    continue;
                if (SpawnAt(code, candidate) != null) return true;
            }
            return false;
        }

        /// <summary>지정한 자리 가까이에 적을 출현시킨다. 걸을 수 있는 땅이 없으면 null.</summary>
        public EnemyAgent SpawnAt(string enemyCode, Vector3 position)
        {
            EnemyDef def = GameSession.Current.Defs.FindEnemy(enemyCode);
            if (def == null) return null;
            if (!NavMesh.SamplePosition(position, out NavMeshHit hit, 3f, NavMesh.AllAreas)) return null;
            // 잔해나 지붕 위처럼 땅이 아닌 곳은 쓰지 않는다.
            if (Mathf.Abs(hit.position.y - transform.position.y) > 0.4f) return null;

            EnemyAgent enemy = EnemyAgent.Spawn(def, hit.position, this, _rng);
            enemy.transform.SetParent(transform, true);
            _alive.Add(enemy);
            _spawnedTotal++;
            return enemy;
        }

        public void OnEnemyDied(EnemyAgent enemy, bool byPlayer, Vector3 center)
        {
            _alive.Remove(enemy);
            if (!_autoSpawn || _spawnedTotal >= Def.maxAlive)
                _nextSpawnAt = Mathf.Max(_nextSpawnAt, Time.time + Def.respawnSeconds);
            // NPC나 다른 원인으로 쓰러진 적은 보상이 없다.
            if (!byPlayer || GameSession.Current == null) return;

            GameSession session = GameSession.Current;
            _pending.TryGetValue(enemy.Def.code, out int count);
            _pending[enemy.Def.code] = count + 1;

            // 화면에 먼저 보여 줄 추정치. 오늘 한도를 넘는 부분은 보여 주지 않는다.
            long remaining = (long)session.Me.dailyHuntGcLimit - session.Me.dailyHuntGc - session.PendingGc;
            int estimate = (int)Math.Max(0L, Math.Min(enemy.Def.rewardGc, remaining));
            if (estimate > 0)
            {
                _pendingGc += estimate;
                session.AddPendingGc(estimate);
                PlayerController player = PlayerController.Instance;
                Fx.Coins(center, Mathf.Clamp(estimate / 3, 2, 6), player != null ? player.transform : null);
                // 마지막 피해 숫자와 겹치지 않게 그보다 위에서 띄운다.
                Fx.FloatingText(center + Vector3.up * 1.5f, $"+{estimate} GC", new Color(1f, 0.85f, 0.25f), 0.36f);
            }
            else if (!_limitToastShown)
            {
                _limitToastShown = true;
                GameEvents.ShowToast("오늘 받을 수 있는 사냥 보상을 모두 받았어요. 내일 다시 받을 수 있어요.");
            }

            PlayerKill?.Invoke(this, enemy.Def);
        }

        // ── 서버 보고 ───────────────────────────────────────────

        private void EnsureSession()
        {
            if (_sessionId != null || _startingSession || Time.time < _nextSessionTry) return;
            _startingSession = true;
            GameSession.Current.Backend.StartHunt(Def.code, result =>
            {
                _startingSession = false;
                if (result.ok)
                {
                    _sessionId = result.data.sessionId;
                    _nextSeq = 1;
                }
                else
                {
                    _nextSessionTry = Time.time + SessionRetrySeconds;
                    Debug.LogWarning($"사냥 시작 실패 ({Def.code}): {result.errorCode} {result.message}");
                }
            });
        }

        private void Send()
        {
            if (_inFlight == null)
            {
                _inFlight = new List<KillCount>(_pending.Count);
                foreach (KeyValuePair<string, int> pair in _pending) _inFlight.Add(new KillCount(pair.Key, pair.Value));
                _inFlightGc = _pendingGc;
                _pending.Clear();
                _pendingGc = 0;
            }

            _waiting = true;
            _lastSendTime = Time.time;
            GameSession session = GameSession.Current;
            // 같은 묶음을 다시 보낼 때는 같은 순번을 쓴다. 서버는 두 번 지급하지 않는다.
            session.Backend.ReportHunt(_sessionId, _nextSeq, _inFlight, result => OnReport(session, result));
        }

        private void OnReport(GameSession session, ApiResult<HuntReportDto> result)
        {
            _waiting = false;
            if (result.ok)
            {
                _nextSeq++;
                int cleared = _inFlightGc;
                _inFlight = null;
                _inFlightGc = 0;
                if (GameSession.Current == session)
                {
                    session.ApplyHuntReport(result.data, cleared);
                    AnnounceDrops(session, result.data);
                }

                string reason = result.data.rejectReason;
                if (reason == HuntRejectReasons.DailyLimit)
                {
                    if (!_limitToastShown)
                    {
                        _limitToastShown = true;
                        GameEvents.ShowToast("오늘 받을 수 있는 사냥 보상을 모두 받았어요. 내일 다시 받을 수 있어요.");
                    }
                }
                else if (reason != null)
                {
                    Debug.LogWarning($"사냥 보고 일부가 인정되지 않음 ({Def.code}): {reason}");
                }
                ReportConfirmed?.Invoke(this, result.data);
                return;
            }

            Debug.LogWarning($"사냥 보고 실패 ({Def.code}): {result.errorCode} {result.message}");
            if (result.errorCode == ApiErrors.NotFound || result.errorCode == ApiErrors.Conflict)
            {
                // 서버가 이 사냥 기록을 모른다: 새로 시작하고 같은 묶음을 다시 보낸다.
                _sessionId = null;
            }
        }

        /// <summary>서버가 판정한 드롭을 알린다 (docs/ITEM_SYSTEM.md 8장).</summary>
        private static void AnnounceDrops(GameSession session, HuntReportDto report)
        {
            if (report.drops == null || report.drops.Count == 0) return;
            for (int i = 0; i < report.drops.Count; i++)
            {
                ItemDef item = session.Defs.FindItem(report.drops[i].itemCode);
                if (item == null) continue;
                string count = report.drops[i].count > 1 ? $" {report.drops[i].count}개" : "";
                GameEvents.ShowToast($"{Korean.WithEul(item.name)}{count} 주웠어요!");
            }
            Sfx.Play(Sfx.Id.Build, 0.7f);
        }

        /// <summary>
        /// 장면을 떠나기 전에 남은 처치를 보고한다.
        /// 지금의 모의 서버는 바로 응답하므로 여기서 끝난다. 실제 HTTP 연결에서는
        /// 장면이 바뀌어도 남아 있는 전송 대기열이 이 일을 맡아야 한다.
        /// </summary>
        public void Flush()
        {
            if (GameSession.Current == null || _sessionId == null || _waiting) return;
            if (_inFlight == null && _pending.Count == 0) return;
            Send();
        }

        public static void FlushAll()
        {
            for (int i = All.Count - 1; i >= 0; i--) All[i].Flush();
        }
    }
}
