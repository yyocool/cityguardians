using CityGuardians.Domain;
using UnityEngine;

namespace CityGuardians.Gameplay
{
    /// <summary>
    /// 원거리 무기의 탄환 (docs/COMBAT_SYSTEM.md 3장). 눈에 보이게 날아가고, 겨눈 적에게 조금 휘어 들어간다.
    /// 벽에 막히고, 사거리를 다 날아가면 사라진다. 탄약은 없다.
    /// 피해 수치는 화면 표시와 적의 체력 처리를 위한 것이고, 보상은 서버가 정한다.
    /// </summary>
    public sealed class BoltProjectile : MonoBehaviour
    {
        private const float Radius = 0.22f;
        /// <summary>겨눈 적 쪽으로 꺾을 수 있는 정도 (도/초). 빗나가기 어렵게 하되 벽을 돌아가지는 못한다.</summary>
        private const float HomingDegreesPerSecond = 220f;
        private const int IgnoreRaycastLayer = 2;
        private static readonly RaycastHit[] Hits = new RaycastHit[24];
        private static readonly Color BoltColor = new Color(0.45f, 0.95f, 1f);
        private static readonly Color[] SparkColors = { new Color(0.45f, 0.95f, 1f), Color.white };

        /// <summary>지금 날아가고 있는 탄환 수. 자동 테스트가 읽는다.</summary>
        public static int ActiveCount { get; private set; }
        /// <summary>적에게 맞은 탄환 수 (게임을 켠 뒤로). 자동 테스트가 읽는다.</summary>
        public static int HitCount { get; private set; }

        private Vector3 _direction;
        private float _speed;
        private float _remaining;
        private int _damage;
        private GameObject _source;
        private Health _target;

        /// <param name="target">겨눈 적. 없으면 곧게 날아간다.</param>
        public static BoltProjectile Launch(Vector3 from, Vector3 direction, WeaponDef weapon, GameObject source, Health target)
        {
            if (direction.sqrMagnitude < 0.0001f) direction = Vector3.forward;
            var go = new GameObject("Bolt");
            go.transform.SetPositionAndRotation(from, Quaternion.LookRotation(direction.normalized, Vector3.up));
            Blocks.Box(go.transform, "Core", Vector3.zero, new Vector3(0.09f, 0.09f, 0.55f), BoltColor, false, true);
            Blocks.Box(go.transform, "Glow", new Vector3(0f, 0f, 0.2f), new Vector3(0.16f, 0.16f, 0.16f), Color.white, false, true);

            var bolt = go.AddComponent<BoltProjectile>();
            bolt._direction = direction.normalized;
            bolt._speed = Mathf.Max(5f, weapon.projectileSpeed);
            bolt._remaining = Mathf.Max(1f, weapon.range);
            bolt._damage = DamageFormula.Compute(weapon.damage, 1f, 0);
            bolt._source = source;
            bolt._target = target;
            return bolt;
        }

        private void OnEnable() => ActiveCount++;
        private void OnDisable() => ActiveCount--;

        private void Update()
        {
            float dt = Time.deltaTime;
            if (dt <= 0f) return;

            // 겨눈 적이 움직이면 그쪽으로 조금 꺾는다.
            if (_target != null && !_target.IsDead)
            {
                Vector3 to = AimPointOf(_target) - transform.position;
                if (to.sqrMagnitude > 0.01f)
                    _direction = Vector3.RotateTowards(_direction, to.normalized,
                        HomingDegreesPerSecond * Mathf.Deg2Rad * dt, 0f).normalized;
            }

            float step = Mathf.Min(_speed * dt, _remaining);
            Vector3 from = transform.position;

            if (FindHit(from, step, out RaycastHit hit, out Health victim))
            {
                Vector3 at = hit.distance > 0f ? hit.point : from;
                if (victim != null) Strike(victim, at);
                else Fx.Shatter(at, SparkColors, 2, 0.06f);
                Destroy(gameObject);
                return;
            }

            transform.SetPositionAndRotation(from + _direction * step, Quaternion.LookRotation(_direction, Vector3.up));
            _remaining -= step;
            if (_remaining <= 0.001f) Destroy(gameObject);
        }

        /// <summary>
        /// 이번 걸음에서 처음 부딪히는 것: 살아 있는 적이거나 단단한 지형.
        /// 적은 넉넉한 굵기로 맞히고, 지형은 가는 선으로 본다. 화면에서는 적이 보이는데 탄환이
        /// 낮은 울타리 모서리에 걸려 사라지면 답답하기 때문이다.
        /// </summary>
        private bool FindHit(Vector3 from, float step, out RaycastHit best, out Health victim)
        {
            best = default;
            victim = null;
            float bestDistance = float.MaxValue;
            bool found = false;

            int count = Physics.SphereCastNonAlloc(from, Radius, _direction, Hits, step, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                RaycastHit hit = Hits[i];
                var health = hit.collider.GetComponentInParent<Health>();
                if (health == null || health.team != Team.Enemy || health.IsDead) continue;
                if (hit.distance >= bestDistance) continue;
                bestDistance = hit.distance;
                best = hit;
                victim = health;
                found = true;
            }

            count = Physics.RaycastNonAlloc(from, _direction, Hits, step, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                RaycastHit hit = Hits[i];
                // 캐릭터(플레이어, NPC, 방어대원, 타워)와 보이지 않는 막이는 그대로 지나간다. 적은 위에서 이미 봤다.
                if (hit.collider.gameObject.layer == IgnoreRaycastLayer) continue;
                if (hit.collider.GetComponentInParent<Health>() != null) continue;
                if (hit.distance >= bestDistance) continue;
                bestDistance = hit.distance;
                best = hit;
                victim = null;
                found = true;
            }
            return found;
        }

        private void Strike(Health victim, Vector3 at)
        {
            if (!victim.TakeDamage(_damage, _source, true)) return;
            HitCount++;

            var enemy = victim.GetComponent<EnemyAgent>();
            if (enemy != null) enemy.Knockback(_direction, 0.25f);

            Fx.FloatingText(victim.transform.position + Vector3.up * 1.5f, _damage.ToString(), BoltColor, 0.34f);
            Fx.Shatter(at, SparkColors, 3, 0.07f);
            Sfx.Play(Sfx.Id.Hit, 0.45f);
        }

        /// <summary>
        /// 겨누는 곳이자 탄환이 향하는 곳: 적의 몸 가운데보다 조금 위(가슴께).
        /// 어깨 높이에서 쏜 탄환이 허리 높이의 울타리 위로 넘어가게 한다.
        /// </summary>
        public static Vector3 AimPointOf(Health target)
        {
            var collider = target.GetComponent<Collider>();
            if (collider == null) return target.transform.position + Vector3.up * 0.8f;
            Bounds bounds = collider.bounds;
            return bounds.center + Vector3.up * (bounds.extents.y * 0.3f);
        }
    }
}
