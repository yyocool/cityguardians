using System.Collections.Generic;
using CityGuardians.Domain;
using UnityEngine;
using UnityEngine.AI;

namespace CityGuardians.Gameplay
{
    /// <summary>한 목표를 동시에 공격할 수 있는 근접 적의 수를 제한한다 (docs/ENEMY_AI.md).</summary>
    public static class AttackSlots
    {
        public const int Max = 3;
        private static readonly List<EnemyAgent> Holders = new List<EnemyAgent>();

        public static bool TryAcquire(EnemyAgent agent)
        {
            Holders.RemoveAll(h => h == null);
            if (Holders.Contains(agent)) return true;
            if (Holders.Count >= Max) return false;
            Holders.Add(agent);
            return true;
        }

        public static void Release(EnemyAgent agent) => Holders.Remove(agent);
        public static void Clear() => Holders.Clear();
    }

    /// <summary>적을 출현시킨 쪽. 사냥 구역과 방어전이 구현한다.</summary>
    public interface IEnemyOwner
    {
        /// <summary>적이 목표를 쫓아갈 수 있는 범위 안인가.</summary>
        bool ContainsLeash(Vector3 point);
        /// <param name="byPlayer">플레이어가 한 번이라도 피해를 준 적인가.</param>
        void OnEnemyDied(EnemyAgent enemy, bool byPlayer, Vector3 center);
    }

    /// <summary>적이 부술 수 있는 구조물 (타워).</summary>
    public interface IStructure
    {
        Vector3 Position { get; }
        float Radius { get; }
        bool IsAlive { get; }
        void TakeDamage(int amount);
    }

    /// <summary>방어전에서 적이 따라가는 길 (docs/ENEMY_AI.md 5장).</summary>
    public interface IEnemyRoute
    {
        /// <summary>출현 지점에서 방어 거점까지의 경유지. 마지막이 방어 거점이다.</summary>
        IReadOnlyList<Vector3> Waypoints { get; }
        void OnEnemyReachedGoal(EnemyAgent enemy);
        /// <summary>center에서 radius 안에 있는 구조물을 results에 더한다.</summary>
        void CollectStructures(Vector3 center, float radius, List<IStructure> results);
    }

    /// <summary>
    /// 적 한 마리의 실행 계층. 판단은 EnemyBrain(순수 C#)이 하고,
    /// 여기서는 감각을 채워 넘기고 두뇌가 요청한 이동·공격을 Unity에서 수행한다.
    /// 사냥 구역에서는 제자리를 지키다 플레이어를 쫓고, 방어전에서는 길을 따라 방어 거점으로 간다.
    /// 두 방식은 "누구를 목표로 삼고 어디로 걷는가"만 다르고 두뇌와 공격은 같다.
    /// </summary>
    public sealed class EnemyAgent : MonoBehaviour
    {
        private const float HomeRadius = 1.2f;
        private const float RepathInterval = 0.2f;
        private const float WanderRadius = 4f;
        private const float WaypointRadius = 1.5f;
        private const float GoalRadius = 1.8f;
        private const float KnockDecay = 22f;
        /// <summary>사냥 구역의 적이 플레이어에게 맞은 뒤, 구역 밖까지 쫓아가는 시간(초).</summary>
        private const float ProvokedSeconds = 6f;
        private static readonly List<IStructure> StructureBuffer = new List<IStructure>();
        private static readonly Color StrikeColor = new Color(1f, 0.45f, 0.15f);

        private enum RouteMove { None, March, Seek }

        public EnemyDef Def { get; private set; }
        public IEnemyOwner Owner { get; private set; }
        public Health Health { get; private set; }
        public EnemyState State => _brain.State;
        public float Radius => _visual.Radius;
        public float Height => _visual.Height;
        public bool OnRoute => _route != null;
        /// <summary>길을 얼마나 나아갔는가. 클수록 방어 거점에 가깝다. 타워가 앞선 적부터 노리는 데 쓴다.</summary>
        public float RouteProgress { get; private set; }
        /// <summary>쓰러졌거나 사라지는 중인가.</summary>
        public bool IsGone => _gone;

        private EnemyBrain _brain;
        private NavMeshAgent _agent;
        private EnemyVisual _visual;
        private HitFlash _flash;
        private EnemyHealthBar _bar;
        private Vector3 _home;
        private Vector3 _wanderGoal;
        private bool _hasWanderGoal;
        private float _nextRepath;
        private Vector3 _knock;
        private bool _damagedByPlayer;
        private float _provokedUntil = -1f;
        private bool _gone;
        private EnemyMove _lastMove = EnemyMove.None;

        // 방어전
        private IEnemyRoute _route;
        private int _routeIndex;
        private Vector3 _routeOffset;
        private RouteMove _routeMove = RouteMove.None;
        private int _marchIndex = -1;
        private float _aggroUntil;
        private IStructure _structureTarget;

        public static EnemyAgent Spawn(EnemyDef def, Vector3 position, IEnemyOwner owner, System.Random rng)
        {
            var go = new GameObject("Enemy_" + def.code);
            go.layer = 2; // Ignore Raycast: 카메라 충돌 검사에서 빠진다
            go.transform.position = position;
            go.transform.rotation = Quaternion.Euler(0f, (float)rng.NextDouble() * 360f, 0f);

            var agent = go.AddComponent<EnemyAgent>();
            agent.Def = def;
            agent.Owner = owner;
            agent._home = position;
            agent._brain = new EnemyBrain(def, rng);

            agent._visual = EnemyVisual.Create(go.transform, def);
            agent._flash = agent._visual.gameObject.AddComponent<HitFlash>();

            var capsule = go.AddComponent<CapsuleCollider>();
            capsule.radius = agent._visual.Radius;
            capsule.height = Mathf.Max(agent._visual.Height, agent._visual.Radius * 2f);
            capsule.center = new Vector3(0f, capsule.height * 0.5f, 0f);
            var body = go.AddComponent<Rigidbody>();
            body.isKinematic = true;

            agent.Health = go.AddComponent<Health>();
            agent.Health.team = Team.Enemy;
            agent.Health.Init(def.maxHp);
            agent.Health.Damaged += agent.OnDamaged;
            agent.Health.Died += agent.OnDied;

            float barScale = Mathf.Clamp(agent._visual.Height / 1.35f, 1f, 2.5f);
            agent._bar = EnemyHealthBar.Create(go.transform, agent._visual.Height + 0.25f * barScale,
                new Color(1f, 0.35f, 0.3f), barScale);

            agent._agent = go.AddComponent<NavMeshAgent>();
            agent._agent.radius = agent._visual.Radius;
            agent._agent.height = capsule.height;
            agent._agent.speed = def.moveSpeed;
            agent._agent.acceleration = 30f;
            agent._agent.angularSpeed = 540f;
            agent._agent.stoppingDistance = 0.1f;
            agent._agent.autoBraking = true;
            return agent;
        }

        /// <summary>
        /// 방어전 방식으로 바꾼다. 길을 따라 방어 거점으로 가고, 플레이어에게 맞으면 잠깐 플레이어를 쫓는다.
        /// </summary>
        /// <param name="lateralOffset">여럿이 한 줄로 서지 않도록 경유지에서 비켜 서는 정도.</param>
        public void FollowRoute(IEnemyRoute route, Vector3 lateralOffset)
        {
            _route = route;
            _routeIndex = 0;
            _routeOffset = new Vector3(lateralOffset.x, 0f, lateralOffset.z);
            _agent.autoBraking = false;
            // 길 위에서 서로 밀치느라 멈추지 않게 한다.
            _agent.obstacleAvoidanceType = ObstacleAvoidanceType.LowQualityObstacleAvoidance;
            if (route.Waypoints.Count > 0) Face(route.Waypoints[0], 360f);
        }

        private void Update()
        {
            float dt = Time.deltaTime;
            if (dt <= 0f || _gone || _brain.State == EnemyState.Dead) return;

            if (_route != null) UpdateOnRoute(dt);
            else UpdateInZone(dt);

            _visual.speed = _agent.enabled ? _agent.velocity.magnitude : 0f;
        }

        // ── 사냥 구역: 제자리를 지키다 플레이어를 쫓는다 ─────────

        private void UpdateInZone(float dt)
        {
            PlayerController player = PlayerController.Instance;
            bool hasTarget = player != null && !player.Health.IsDead;
            Vector3 targetPosition = hasTarget ? player.transform.position : _home;
            float distance = hasTarget ? PlanarDistance(transform.position, targetPosition) : float.MaxValue;

            // 공격 자리: 가까이 있거나 공격 중일 때만 차지한다.
            bool wantsSlot = hasTarget && (_brain.State == EnemyState.Attack || distance <= Def.attackRange * 2f);
            bool canEngage = wantsSlot && AttackSlots.TryAcquire(this);
            if (!wantsSlot) AttackSlots.Release(this);

            var senses = new EnemySenses
            {
                hasTarget = hasTarget,
                targetDistance = distance,
                // 구역 밖에서 쏜 플레이어도 잠시 동안은 쫓아간다. 안전한 곳에서 맞기만 하지는 않는다.
                targetInsideLeash = hasTarget && (Owner == null || Owner.ContainsLeash(targetPosition)
                                                  || Time.time < _provokedUntil),
                atHome = PlanarDistance(transform.position, _home) <= HomeRadius,
                canEngage = canEngage,
            };

            EnemyIntent intent = Think(dt, senses);
            if (intent.strike) Strike(player);
            if (intent.healToFull) Health.Init(Def.maxHp);

            Move(intent.move, targetPosition, dt);

            if (_brain.State == EnemyState.Attack && hasTarget) Face(targetPosition, 480f * dt);
        }

        private EnemyIntent Think(float dt, in EnemySenses senses)
        {
            EnemyState before = _brain.State;
            EnemyIntent intent = _brain.Tick(dt, senses);
            if (before == EnemyState.Attack && _brain.State != EnemyState.Attack) _visual.CancelWindup();
            if (intent.windupStarted)
            {
                _visual.BeginWindup(Def.attackWindup);
                // 넓은 공격은 어디에 떨어질지 바닥에 미리 보여 준다. 피할 시간을 주기 위해서다.
                if (Def.strikeRadius > 0f)
                    Fx.Telegraph(transform, new Vector3(0f, 0.07f, Def.attackRange * 0.5f), Def.strikeRadius,
                        Def.attackWindup, StrikeColor);
            }
            return intent;
        }

        private void Move(EnemyMove move, Vector3 targetPosition, float dt)
        {
            if (!_agent.enabled || !_agent.isOnNavMesh) return;
            ApplyKnock(dt);

            if (move != EnemyMove.Wander) _hasWanderGoal = false;

            switch (move)
            {
                case EnemyMove.None:
                    if (_lastMove != EnemyMove.None || _agent.hasPath) Halt();
                    break;

                case EnemyMove.ToTarget:
                    _agent.speed = Def.moveSpeed;
                    if (Time.time >= _nextRepath || _lastMove != EnemyMove.ToTarget)
                    {
                        _nextRepath = Time.time + RepathInterval;
                        _agent.SetDestination(targetPosition);
                    }
                    break;

                case EnemyMove.ToHome:
                    _agent.speed = Def.moveSpeed;
                    if (_lastMove != EnemyMove.ToHome) _agent.SetDestination(_home);
                    break;

                case EnemyMove.Wander:
                    _agent.speed = Def.moveSpeed * 0.45f;
                    if (!_hasWanderGoal)
                    {
                        Vector2 offset = Random.insideUnitCircle * WanderRadius;
                        Vector3 candidate = _home + new Vector3(offset.x, 0f, offset.y);
                        if (NavMesh.SamplePosition(candidate, out NavMeshHit hit, 2f, NavMesh.AllAreas))
                        {
                            _wanderGoal = hit.position;
                            _hasWanderGoal = true;
                            _agent.SetDestination(_wanderGoal);
                        }
                    }
                    break;
            }
            _lastMove = move;
        }

        // ── 방어전: 길을 따라 방어 거점으로 간다 ─────────────────

        private void UpdateOnRoute(float dt)
        {
            PlayerController player = PlayerController.Instance;
            bool playerAlive = player != null && !player.Health.IsDead;

            // 목표: 방금 나를 때린 플레이어 > 길 가까이의 타워(구조물을 부수는 적만) > 없음(길을 따라간다)
            bool hasTarget = false;
            bool targetIsPlayer = false;
            Vector3 targetPosition = transform.position;
            float distance = float.MaxValue;
            _structureTarget = null;

            if (playerAlive && Time.time < _aggroUntil)
            {
                float toPlayer = PlanarDistance(transform.position, player.transform.position);
                if (toPlayer <= Def.loseRange)
                {
                    hasTarget = true;
                    targetIsPlayer = true;
                    targetPosition = player.transform.position;
                    distance = toPlayer;
                }
                else
                {
                    _aggroUntil = 0f;
                }
            }

            if (!hasTarget && Def.attacksStructures)
            {
                _structureTarget = NearestStructure(Def.detectRange);
                if (_structureTarget != null)
                {
                    hasTarget = true;
                    targetPosition = _structureTarget.Position;
                    distance = Mathf.Max(0f, PlanarDistance(transform.position, targetPosition) - _structureTarget.Radius);
                }
            }

            bool canEngage = hasTarget;
            if (targetIsPlayer)
            {
                bool wantsSlot = _brain.State == EnemyState.Attack || distance <= Def.attackRange * 2f;
                canEngage = wantsSlot && AttackSlots.TryAcquire(this);
                if (!wantsSlot) AttackSlots.Release(this);
            }
            else
            {
                AttackSlots.Release(this);
            }

            var senses = new EnemySenses
            {
                hasTarget = hasTarget,
                targetDistance = distance,
                targetInsideLeash = true,
                // 돌아갈 제자리가 없다: 목표를 놓치면 곧바로 다시 길을 따라간다.
                atHome = true,
                canEngage = canEngage,
            };

            EnemyIntent intent = Think(dt, senses);
            if (intent.strike) Strike(targetIsPlayer || Def.strikeRadius > 0f ? player : null);

            if (!_agent.enabled || !_agent.isOnNavMesh) return;
            ApplyKnock(dt);

            switch (_brain.State)
            {
                case EnemyState.Spawn:
                case EnemyState.Stagger:
                case EnemyState.Attack:
                    StopOnRoute();
                    if (_brain.State == EnemyState.Attack && hasTarget) Face(targetPosition, 480f * dt);
                    break;

                case EnemyState.Chase:
                    if (intent.move == EnemyMove.ToTarget && hasTarget) Seek(targetPosition);
                    else StopOnRoute();
                    break;

                default:
                    March();
                    break;
            }
        }

        private void March()
        {
            IReadOnlyList<Vector3> points = _route.Waypoints;
            if (points.Count == 0) return;

            // 닿았거나 이미 지나친 경유지는 넘어간다. 서로 밀려 정확히 밟지 못해도 막히지 않는다.
            while (true)
            {
                bool last = _routeIndex >= points.Count - 1;
                Vector3 goal = RoutePoint(_routeIndex);
                Vector3 from = _routeIndex > 0 ? RoutePoint(_routeIndex - 1) : _home;
                Vector3 along = goal - from;
                along.y = 0f;
                Vector3 toGoal = goal - transform.position;
                toGoal.y = 0f;
                bool reached = toGoal.magnitude <= (last ? GoalRadius : WaypointRadius);
                bool passed = along.sqrMagnitude > 0.01f && Vector3.Dot(toGoal, along) <= 0f;
                if (!reached && !passed) break;

                if (last)
                {
                    _route.OnEnemyReachedGoal(this);
                    return;
                }
                _routeIndex++;
            }

            Vector3 target = RoutePoint(_routeIndex);
            _agent.speed = Def.moveSpeed;
            if (_routeMove != RouteMove.March || _marchIndex != _routeIndex)
            {
                _routeMove = RouteMove.March;
                _marchIndex = _routeIndex;
                _agent.SetDestination(target);
            }

            Vector3 previous = _routeIndex > 0 ? RoutePoint(_routeIndex - 1) : _home;
            float segment = Mathf.Max(0.5f, PlanarDistance(previous, target));
            float left = PlanarDistance(transform.position, target);
            RouteProgress = _routeIndex + 1f - Mathf.Clamp01(left / segment);
        }

        private Vector3 RoutePoint(int index)
        {
            IReadOnlyList<Vector3> points = _route.Waypoints;
            // 마지막 지점(방어 거점)은 비켜 서지 않고 그대로 향한다.
            return index >= points.Count - 1 ? points[points.Count - 1] : points[index] + _routeOffset;
        }

        private void Seek(Vector3 targetPosition)
        {
            _agent.speed = Def.moveSpeed;
            if (_routeMove != RouteMove.Seek || Time.time >= _nextRepath)
            {
                _routeMove = RouteMove.Seek;
                _nextRepath = Time.time + RepathInterval;
                _agent.SetDestination(targetPosition);
            }
        }

        private void StopOnRoute()
        {
            if (_routeMove == RouteMove.None && !_agent.hasPath) return;
            _routeMove = RouteMove.None;
            Halt();
        }

        private IStructure NearestStructure(float range)
        {
            StructureBuffer.Clear();
            _route.CollectStructures(transform.position, range, StructureBuffer);
            IStructure best = null;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < StructureBuffer.Count; i++)
            {
                IStructure candidate = StructureBuffer[i];
                if (candidate == null || !candidate.IsAlive) continue;
                float d = PlanarDistance(transform.position, candidate.Position);
                if (d < bestDistance) { bestDistance = d; best = candidate; }
            }
            StructureBuffer.Clear();
            return best;
        }

        // ── 공통 ────────────────────────────────────────────────

        private void Halt()
        {
            _agent.ResetPath();
            _agent.velocity = Vector3.zero;
        }

        private void ApplyKnock(float dt)
        {
            if (_knock.sqrMagnitude <= 0.01f) return;
            _agent.Move(_knock * dt);
            _knock = Vector3.MoveTowards(_knock, Vector3.zero, KnockDecay * dt);
        }

        private void Strike(PlayerController player)
        {
            _visual.Strike();
            if (Def.strikeRadius > 0f)
            {
                StrikeArea(player);
                return;
            }

            if (_structureTarget != null && _structureTarget.IsAlive)
            {
                _structureTarget.TakeDamage(Def.structureDamage > 0 ? Def.structureDamage : Def.damage);
                return;
            }
            if (player == null || player.Health.IsDead) return;

            Vector3 to = player.transform.position - transform.position;
            to.y = 0f;
            // 예고 동작 동안 옆이나 뒤로 피하면 맞지 않는다.
            if (to.magnitude > Def.attackRange + 0.5f) return;
            if (to.sqrMagnitude > 0.04f && Vector3.Angle(transform.forward, to) > 70f) return;

            int damage = Def.damage;
            if (Def.code == EnemyCodes.TutorialRobot)
            {
                // 첫 전투는 지지 않는다: 체력을 20% 아래로 깎지 않는다.
                int safe = Mathf.CeilToInt(player.Health.Max * 0.2f);
                damage = Mathf.Min(damage, player.Health.Current - safe);
            }
            if (damage > 0) player.Health.TakeDamage(damage, gameObject);
        }

        /// <summary>넓은 타격: 바닥에 미리 보여 준 원 안의 플레이어와 구조물이 모두 맞는다.</summary>
        private void StrikeArea(PlayerController player)
        {
            Vector3 center = transform.position + transform.forward * (Def.attackRange * 0.5f);
            Fx.Shockwave(center, Def.strikeRadius, StrikeColor);
            Sfx.Play(Sfx.Id.Explosion, 0.8f);

            if (player != null && !player.Health.IsDead)
            {
                float toPlayer = PlanarDistance(player.transform.position, center);
                if (toPlayer <= Def.strikeRadius) player.Health.TakeDamage(Def.damage, gameObject);
                if (toPlayer <= 14f && OrbitCamera.Instance != null) OrbitCamera.Instance.Shake(0.14f);
            }

            if (_route == null) return;
            StructureBuffer.Clear();
            _route.CollectStructures(center, Def.strikeRadius, StructureBuffer);
            int structureDamage = Def.structureDamage > 0 ? Def.structureDamage : Def.damage;
            // 맞은 구조물이 부서지며 목록이 바뀔 수 있어 복사해서 돈다.
            IStructure[] hit = StructureBuffer.ToArray();
            StructureBuffer.Clear();
            for (int i = 0; i < hit.Length; i++)
                if (hit[i] != null && hit[i].IsAlive) hit[i].TakeDamage(structureDamage);
        }

        /// <summary>맞았을 때 뒤로 밀린다.</summary>
        public void Knockback(Vector3 direction, float distance)
        {
            if (Def.staggerImmune) return;
            direction.y = 0f;
            if (direction.sqrMagnitude < 0.0001f) return;
            // 밀리는 속도가 일정하게 줄어드는 동안 대략 distance만큼 움직인다.
            _knock = direction.normalized * Mathf.Sqrt(2f * KnockDecay * distance);
        }

        private void OnDamaged(int amount, GameObject source)
        {
            bool byPlayer = source != null && source.GetComponent<PlayerController>() != null;
            if (byPlayer)
            {
                _damagedByPlayer = true;
                _provokedUntil = Time.time + ProvokedSeconds;
                if (_route != null && Def.aggroSeconds > 0f) _aggroUntil = Time.time + Def.aggroSeconds;
            }

            // 타워의 연사는 적을 멈춰 세우지 못한다. 멈칫하게 하는 것은 가까이에서 때리는 공격뿐이다.
            // 플레이어의 탄환도 마찬가지다: 맞은 적이 쏜 쪽을 쫓아오지만 멈칫하지는 않는다.
            // (초당 여러 발을 맞는 동안 계속 멈칫하면 적이 한 걸음도 다가오지 못한다.)
            bool fromStructure = source != null && source.GetComponent<IStructure>() != null;
            if (!fromStructure)
            {
                bool stagger = !Def.staggerImmune && !Health.LastHitWasLight;
                if (stagger) _visual.CancelWindup();
                _brain.OnDamaged(stagger);
            }

            if (_flash != null) _flash.Flash();
            if (_bar != null) _bar.Show(Health.Current / (float)Health.Max);
        }

        private void OnDied()
        {
            if (_gone) return;
            _gone = true;
            _brain.OnDied();
            AttackSlots.Release(this);
            Vector3 center = transform.position + Vector3.up * (_visual.Height * 0.5f);
            ShatterAt(center);
            Sfx.Play(Sfx.Id.EnemyDown);
            if (Owner != null) Owner.OnEnemyDied(this, _damagedByPlayer, center);
            Destroy(gameObject);
        }

        /// <summary>보상 없이 사라진다. 방어 거점에 닿았거나 전투가 끝났을 때 쓴다.</summary>
        public void Despawn(bool shatter)
        {
            if (_gone) return;
            _gone = true;
            _brain.OnDied();
            AttackSlots.Release(this);
            if (shatter) ShatterAt(transform.position + Vector3.up * (_visual.Height * 0.5f));
            Destroy(gameObject);
        }

        private void ShatterAt(Vector3 center)
        {
            bool big = _visual.Height > 2f;
            int count = Def.code == EnemyCodes.MechInsect ? 7 : big ? 26 : 12;
            Fx.Shatter(center, _visual.DebrisColors, count, big ? 0.32f : 0.18f);
        }

        private void OnDestroy()
        {
            AttackSlots.Release(this);
        }

        private void Face(Vector3 point, float maxDegrees)
        {
            Vector3 direction = point - transform.position;
            direction.y = 0f;
            if (direction.sqrMagnitude < 0.0001f) return;
            transform.rotation = Quaternion.RotateTowards(transform.rotation,
                Quaternion.LookRotation(direction, Vector3.up), maxDegrees);
        }

        private static float PlanarDistance(Vector3 a, Vector3 b)
        {
            float x = a.x - b.x;
            float z = a.z - b.z;
            return Mathf.Sqrt(x * x + z * z);
        }
    }

    /// <summary>머리 위의 작은 체력 막대. 처음 맞았을 때부터 보인다. 적과 타워가 함께 쓴다.</summary>
    public sealed class EnemyHealthBar : MonoBehaviour
    {
        private const float Width = 0.8f;
        private Transform _fill;

        public static EnemyHealthBar Create(Transform parent, float height, Color fillColor, float scale = 1f)
        {
            var go = new GameObject("HealthBar");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(0f, height, 0f);
            go.transform.localScale = Vector3.one * scale;
            var bar = go.AddComponent<EnemyHealthBar>();
            Blocks.Box(go.transform, "Back", Vector3.zero, new Vector3(Width + 0.06f, 0.12f, 0.02f),
                new Color(0.1f, 0.1f, 0.12f), false, true);
            bar._fill = Blocks.Pivot(go.transform, "FillPivot", new Vector3(-Width * 0.5f, 0f, -0.015f));
            Blocks.Box(bar._fill, "Fill", new Vector3(Width * 0.5f, 0f, 0f), new Vector3(Width, 0.07f, 0.02f),
                fillColor, false, true);
            go.SetActive(false);
            return bar;
        }

        public void Show(float ratio)
        {
            gameObject.SetActive(true);
            _fill.localScale = new Vector3(Mathf.Clamp01(ratio), 1f, 1f);
        }

        public void Hide() => gameObject.SetActive(false);

        private void LateUpdate()
        {
            Camera cam = Camera.main;
            if (cam != null) transform.rotation = Quaternion.LookRotation(cam.transform.forward, Vector3.up);
        }
    }
}
