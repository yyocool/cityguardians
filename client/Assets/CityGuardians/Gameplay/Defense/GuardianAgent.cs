using UnityEngine;
using UnityEngine.AI;

namespace CityGuardians.Gameplay
{
    /// <summary>
    /// 방어전을 함께 치르는 NPC 방어대원 (docs/TOWER_DEFENSE.md 8장, docs/NPC_SYSTEM.md).
    /// 방어 거점 앞을 지키다가 가까이 온 적을 쫓아가 때리고 제자리로 돌아온다.
    /// 쓰러지지 않으며, 혼자서 전투를 끝낼 만큼 강하지 않다. 주인공은 플레이어다.
    /// 이름표에 NPC라고 적어 실제 플레이어와 헷갈리지 않게 한다.
    /// </summary>
    public sealed class GuardianAgent : MonoBehaviour
    {
        // 한 명이 초당 8쯤의 피해: 플레이어(초당 50)나 기관총 타워(초당 30)보다 훨씬 약하다.
        // 방어대원 둘과 타워 둘만으로 끝까지 막히던 것을 자동 플레이 테스트로 확인하고 낮췄다.
        private const int Damage = 10;
        private const float AttackInterval = 1.2f;
        private const float AttackReach = 1.5f;
        private const float GuardRadius = 10f;
        private const float MoveSpeed = 4.5f;
        private const float HitDelay = 0.11f;

        private DefenseBattle _battle;
        private NavMeshAgent _agent;
        private BlockCharacterAnimator _animator;
        private Vector3 _post;
        private Quaternion _postRotation;
        private EnemyAgent _target;
        private float _nextAttack;
        private float _hitAt = -1f;
        private float _nextScan;
        private float _nextRepath;

        public static GuardianAgent Create(Transform parent, string displayName, Vector3 post, float yaw,
            Appearance appearance, DefenseBattle battle)
        {
            var go = new GameObject("Guardian_" + displayName);
            go.layer = 2;
            go.transform.SetParent(parent, false);
            go.transform.SetPositionAndRotation(post, Quaternion.Euler(0f, yaw, 0f));

            var guardian = go.AddComponent<GuardianAgent>();
            guardian._battle = battle;
            guardian._post = post;
            guardian._postRotation = go.transform.rotation;

            BlockCharacter character = BlockCharacter.Create(go.transform, appearance);
            character.EquipBaton();
            guardian._animator = character.gameObject.AddComponent<BlockCharacterAnimator>();

            var capsule = go.AddComponent<CapsuleCollider>();
            capsule.radius = 0.3f;
            capsule.height = BlockCharacter.Height;
            capsule.center = new Vector3(0f, BlockCharacter.Height * 0.5f, 0f);

            WorldLabel.Create(go.transform, new Vector3(0f, BlockCharacter.Height + 0.28f, 0f), "NPC " + displayName, 0.22f,
                new Color(0.75f, 1f, 0.8f));

            guardian._agent = go.AddComponent<NavMeshAgent>();
            guardian._agent.radius = 0.3f;
            guardian._agent.height = BlockCharacter.Height;
            guardian._agent.speed = MoveSpeed;
            guardian._agent.acceleration = 30f;
            guardian._agent.angularSpeed = 720f;
            guardian._agent.stoppingDistance = 0.2f;
            return guardian;
        }

        private void Update()
        {
            float dt = Time.deltaTime;
            if (dt <= 0f || _battle == null) return;

            if (_hitAt >= 0f && Time.time >= _hitAt)
            {
                _hitAt = -1f;
                ResolveHit();
            }

            if (Time.time >= _nextScan || _target == null || _target.IsGone)
            {
                _nextScan = Time.time + 0.3f;
                _target = _battle.Phase == DefensePhase.Wave ? FindTarget() : null;
            }

            if (_agent.enabled && _agent.isOnNavMesh)
            {
                if (_target != null) Engage(dt);
                else ReturnToPost(dt);
            }
            _animator.speed = _agent.enabled ? _agent.velocity.magnitude : 0f;
        }

        private EnemyAgent FindTarget()
        {
            EnemyAgent best = null;
            float bestDistance = float.MaxValue;
            foreach (EnemyAgent enemy in _battle.Alive)
            {
                if (enemy == null || enemy.IsGone) continue;
                // 지키는 자리에서 너무 멀리 나가지 않는다.
                if (Planar(enemy.transform.position, _post) > GuardRadius) continue;
                float distance = Planar(enemy.transform.position, transform.position);
                if (distance < bestDistance) { bestDistance = distance; best = enemy; }
            }
            return best;
        }

        private void Engage(float dt)
        {
            Vector3 targetPosition = _target.transform.position;
            float distance = Planar(targetPosition, transform.position) - _target.Radius;
            if (distance > AttackReach)
            {
                if (Time.time >= _nextRepath)
                {
                    _nextRepath = Time.time + 0.2f;
                    _agent.SetDestination(targetPosition);
                }
                return;
            }

            if (_agent.hasPath)
            {
                _agent.ResetPath();
                _agent.velocity = Vector3.zero;
            }
            Face(targetPosition, 720f * dt);
            if (Time.time < _nextAttack) return;
            _nextAttack = Time.time + AttackInterval;
            _hitAt = Time.time + HitDelay;
            _animator.TriggerAttack();
        }

        private void ResolveHit()
        {
            if (_target == null || _target.IsGone) return;
            float distance = Planar(_target.transform.position, transform.position) - _target.Radius;
            if (distance > AttackReach + 0.8f) return;
            if (_target.Health.TakeDamage(Damage, gameObject)) Sfx.Play(Sfx.Id.Hit, 0.35f);
        }

        private void ReturnToPost(float dt)
        {
            if (Planar(transform.position, _post) > 0.5f)
            {
                if (Time.time >= _nextRepath)
                {
                    _nextRepath = Time.time + 0.5f;
                    _agent.SetDestination(_post);
                }
                return;
            }
            if (_agent.hasPath)
            {
                _agent.ResetPath();
                _agent.velocity = Vector3.zero;
            }
            transform.rotation = Quaternion.RotateTowards(transform.rotation, _postRotation, 240f * dt);
        }

        private void Face(Vector3 point, float maxDegrees)
        {
            Vector3 direction = point - transform.position;
            direction.y = 0f;
            if (direction.sqrMagnitude < 0.0001f) return;
            transform.rotation = Quaternion.RotateTowards(transform.rotation,
                Quaternion.LookRotation(direction, Vector3.up), maxDegrees);
        }

        private static float Planar(Vector3 a, Vector3 b)
        {
            float x = a.x - b.x;
            float z = a.z - b.z;
            return Mathf.Sqrt(x * x + z * z);
        }
    }
}
