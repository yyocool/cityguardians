using CityGuardians.Domain;
using CityGuardians.Inputs;
using CityGuardians.Services;
using UnityEngine;

namespace CityGuardians.Gameplay
{
    /// <summary>
    /// 플레이어의 공격 (docs/COMBAT_SYSTEM.md 2~4장).
    /// 근접 무기는 정면 부채꼴 안의 적을 모두 맞히고, 원거리 무기는 화면 가운데 가까이의 적에게 탄환을 쏜다.
    /// 숫자 1·2로 무기를 바꾸고 3으로 수리 키트를 쓴다.
    /// 피해 수치는 화면 표시와 적의 체력 처리를 위한 것이고, 보상은 서버가 정한다.
    /// </summary>
    public sealed class PlayerCombat : MonoBehaviour
    {
        private const float HitDelay = 0.11f;
        private const float AimAssistExtraRange = 1.2f;
        /// <summary>화면 가운데에서 이 각도 안의 적을 겨눈다 (카메라에서 본 좌우 각도).</summary>
        private const float RangedAimConeDegrees = 26f;
        /// <summary>이 거리 안의 적은 화면 어디에 있든 겨눈다. 바짝 붙은 적을 놓치지 않게 한다.</summary>
        private const float RangedCloseRange = 6f;
        private const float AimHoldSeconds = 0.4f;
        private const float DefaultShoulderHeight = 0.8f;
        private const float MuzzleForward = 0.55f;
        private const float AimScanInterval = 0.1f;
        private const int IgnoreRaycastLayerMask = 1 << 2;
        private static readonly Collider[] Buffer = new Collider[32];
        private static readonly Collider[] RangedBuffer = new Collider[160];

        /// <summary>무기를 손에 들었는가. 방에서 배턴을 챙기기 전에는 공격할 수 없다.</summary>
        public bool armed;

        /// <summary>지금 겨누고 있는 적 (원거리). 자동 테스트와 조준 표시가 읽는다.</summary>
        public Health RangedTarget => _rangedTarget != null && !_rangedTarget.IsDead ? _rangedTarget : null;

        private PlayerController _player;
        private float _nextAttackTime;
        private float _hitAt = -1f;
        private WeaponDef _swingWeapon;
        private string _shownWeaponCode;
        private Health _rangedTarget;
        private float _nextAimScan;
        private Transform _aimMark;

        /// <summary>겨눈 적의 발밑에 조준 표시가 보이는가. 자동 테스트가 읽는다.</summary>
        public bool AimMarkVisible => _aimMark != null && _aimMark.gameObject.activeSelf;

        private void Awake()
        {
            _player = GetComponent<PlayerController>();
        }

        private void Update()
        {
            if (_hitAt >= 0f && Time.time >= _hitAt)
            {
                _hitAt = -1f;
                ResolveHit(_swingWeapon);
            }

            GameSession session = GameSession.Current;
            if (session == null || !armed) return;

            // 가방이나 상점에서 장착을 바꿨으면 손에 든 모습도 바꾼다.
            WeaponDef weapon = session.ActiveWeapon;
            if (weapon == null) return;
            if (weapon.code != _shownWeaponCode) ShowWeapon(weapon);

            // 방을 꾸미는 동안에는 숫자 키와 마우스가 무기에 닿지 않는다.
            if (GameState.IsControlLocked || GameState.Decorating || _player.Health.IsDead) return;

            // 메뉴를 고른 숫자 키가 같은 프레임에 무기까지 바꾸지 않게 한다.
            IPlayerInput input = PlayerInputHub.Current;
            if (Time.frameCount > GameState.LastUnlockFrame)
            {
                int slot = input.WeaponSlotPressed;
                if (slot == 1) ItemActions.SelectWeapon(EquipSlots.Melee);
                else if (slot == 2) ItemActions.SelectWeapon(EquipSlots.Ranged);
                else if (input.QuickUsePressed) ItemActions.UseRepairKit();

                weapon = session.ActiveWeapon;
                if (weapon == null) return;
                if (weapon.code != _shownWeaponCode) ShowWeapon(weapon);
            }

            // 원거리 무기를 들고 있으면 쏘기 전에도 누구를 겨누는지 찾아 둔다 (바닥의 조준 표시가 따라간다).
            if (weapon.kind == WeaponKind.Ranged && Time.time >= _nextAimScan)
            {
                _nextAimScan = Time.time + AimScanInterval;
                _rangedTarget = FindRangedTarget(weapon.range, transform.position + Vector3.up * ShoulderHeight());
            }

            if (!input.AttackHeld || Time.time < _nextAttackTime) return;
            _nextAttackTime = Time.time + 1f / Mathf.Max(0.1f, weapon.attacksPerSecond);
            if (weapon.kind == WeaponKind.Ranged) Fire(weapon);
            else Swing(weapon);
        }

        private void LateUpdate()
        {
            Health target = AimMarkTarget();
            if (target == null)
            {
                if (_aimMark != null && _aimMark.gameObject.activeSelf) _aimMark.gameObject.SetActive(false);
                return;
            }

            if (_aimMark == null) _aimMark = BuildAimMark();
            if (!_aimMark.gameObject.activeSelf) _aimMark.gameObject.SetActive(true);

            var body = target.GetComponent<Collider>();
            float radius = body != null ? Mathf.Max(body.bounds.extents.x, body.bounds.extents.z) : 0.5f;
            Vector3 feet = target.transform.position;
            feet.y += 0.07f;
            _aimMark.position = feet;
            _aimMark.localScale = Vector3.one * ((radius + 0.3f) * 2f);
            _aimMark.Rotate(0f, 80f * Time.deltaTime, 0f, Space.World);
        }

        private void OnDestroy()
        {
            if (_aimMark != null) Destroy(_aimMark.gameObject);
        }

        /// <summary>조준 표시를 보여 줄 적. 원거리 무기를 들고 조작할 수 있을 때만 있다.</summary>
        private Health AimMarkTarget()
        {
            GameSession session = GameSession.Current;
            if (session == null || !armed || GameState.IsControlLocked || _player.Health.IsDead) return null;
            WeaponDef weapon = session.ActiveWeapon;
            if (weapon == null || weapon.kind != WeaponKind.Ranged) return null;
            return RangedTarget;
        }

        /// <summary>겨눈 적의 발밑에 놓이는 네모 테두리. 한 변이 1인 크기로 만들고 적의 크기에 맞춰 늘린다.</summary>
        private static Transform BuildAimMark()
        {
            var go = new GameObject("AimMark");
            Color color = new Color(0.45f, 0.95f, 1f);
            const float thick = 0.07f;
            Blocks.Box(go.transform, "N", new Vector3(0f, 0f, 0.5f), new Vector3(1f + thick, 0.03f, thick), color, false, true);
            Blocks.Box(go.transform, "S", new Vector3(0f, 0f, -0.5f), new Vector3(1f + thick, 0.03f, thick), color, false, true);
            Blocks.Box(go.transform, "E", new Vector3(0.5f, 0f, 0f), new Vector3(thick, 0.03f, 1f + thick), color, false, true);
            Blocks.Box(go.transform, "W", new Vector3(-0.5f, 0f, 0f), new Vector3(thick, 0.03f, 1f + thick), color, false, true);
            return go.transform;
        }

        /// <summary>손에 든 무기의 모습을 지금 장착에 맞춘다.</summary>
        public void ShowWeapon(WeaponDef weapon)
        {
            _shownWeaponCode = weapon.code;
            if (weapon.kind == WeaponKind.Ranged) _player.Character.EquipBlaster();
            else _player.Character.EquipBaton();
            // 부품이 바뀌었으니 피격 깜빡임이 새 부품도 알게 한다.
            var flash = _player.Character.GetComponent<HitFlash>();
            if (flash != null) flash.Capture();
            // 카메라가 바짝 붙어 캐릭터를 숨긴 동안에 바꿨다면 새 무기도 숨겨야 한다.
            if (OrbitCamera.Instance != null) OrbitCamera.Instance.RefreshTargetVisibility();
        }

        // ── 근접 ────────────────────────────────────────────────

        private void Swing(WeaponDef weapon)
        {
            _swingWeapon = weapon;
            _hitAt = Time.time + HitDelay;

            // 조준 보조: 가까운 적이 있으면 그쪽을 바라본다.
            Health nearest = FindNearestEnemy(weapon.range + AimAssistExtraRange);
            if (nearest != null) _player.FaceTowards(nearest.transform.position);

            if (_player.Animator != null) _player.Animator.TriggerAttack();
            Sfx.Play(Sfx.Id.Swing);
        }

        private void ResolveHit(WeaponDef weapon)
        {
            Vector3 origin = transform.position;
            Vector3 forward = transform.forward;
            float reach = weapon.range;
            int count = Physics.OverlapSphereNonAlloc(origin + Vector3.up * 0.7f, reach + 0.6f, Buffer,
                ~0, QueryTriggerInteraction.Ignore);

            int hits = 0;
            for (int i = 0; i < count; i++)
            {
                var health = Buffer[i].GetComponentInParent<Health>();
                if (health == null || health.team != Team.Enemy || health.IsDead) continue;

                Vector3 to = health.transform.position - origin;
                to.y = 0f;
                // 적의 몸 크기만큼 닿는 거리를 넉넉히 본다.
                float bodyRadius = Buffer[i].bounds.extents.x;
                if (to.magnitude - bodyRadius > reach) continue;
                if (to.sqrMagnitude > 0.04f && Vector3.Angle(forward, to) > weapon.arcDegrees * 0.5f) continue;

                int damage = DamageFormula.Compute(weapon.damage, 1f, 0);
                if (!health.TakeDamage(damage, gameObject)) continue;
                hits++;

                Vector3 pushDirection = to.sqrMagnitude > 0.0001f ? to.normalized : forward;
                var enemy = health.GetComponent<EnemyAgent>();
                if (enemy != null) enemy.Knockback(pushDirection, 1.1f);

                Fx.FloatingText(health.transform.position + Vector3.up * 1.5f, damage.ToString(),
                    new Color(1f, 0.95f, 0.5f));
            }

            if (hits > 0)
            {
                Sfx.Play(Sfx.Id.Hit);
                TimeFx.HitStop(0.045f);
                if (OrbitCamera.Instance != null) OrbitCamera.Instance.Shake(0.06f);
            }
        }

        private Health FindNearestEnemy(float radius)
        {
            int count = Physics.OverlapSphereNonAlloc(transform.position + Vector3.up * 0.7f, radius, Buffer,
                ~0, QueryTriggerInteraction.Ignore);
            Health best = null;
            float bestScore = float.MaxValue;
            for (int i = 0; i < count; i++)
            {
                var health = Buffer[i].GetComponentInParent<Health>();
                if (health == null || health.team != Team.Enemy || health.IsDead) continue;
                Vector3 to = health.transform.position - transform.position;
                to.y = 0f;
                // 이미 바라보는 쪽의 적을 조금 더 우선한다.
                float score = to.magnitude + Vector3.Angle(transform.forward, to) / 90f;
                if (score < bestScore) { bestScore = score; best = health; }
            }
            return best;
        }

        // ── 원거리 ──────────────────────────────────────────────

        private void Fire(WeaponDef weapon)
        {
            // 팔을 어깨 높이로 들고 쏜다. 그 높이에서 겨누고 그 높이에서 탄환이 나간다.
            Vector3 shoulder = transform.position + Vector3.up * ShoulderHeight();
            _rangedTarget = FindRangedTarget(weapon.range, shoulder);

            // 겨눈 적이 있으면 그쪽을, 없으면 화면이 보는 쪽을 향해 쏜다.
            Vector3 aimDirection = _rangedTarget != null
                ? BoltProjectile.AimPointOf(_rangedTarget) - shoulder
                : CameraForward();
            _player.AimAlong(aimDirection, AimHoldSeconds);

            // 몸이 다 돌기 전에 쏘더라도 탄환은 겨눈 쪽으로 나간다.
            Vector3 flat = aimDirection;
            flat.y = 0f;
            flat = flat.sqrMagnitude > 0.0001f ? flat.normalized : transform.forward;
            Vector3 muzzle = shoulder + flat * MuzzleForward;
            // 벽에 바짝 붙어 있으면 총구가 벽 너머로 나가지 않게 한다.
            if (Physics.Linecast(shoulder, muzzle, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                muzzle = shoulder;

            Vector3 direction = _rangedTarget != null
                ? (BoltProjectile.AimPointOf(_rangedTarget) - muzzle).normalized
                : flat;
            BoltProjectile.Launch(muzzle, direction, weapon, gameObject, _rangedTarget);

            if (_player.Animator != null) _player.Animator.TriggerShoot();
            Sfx.Play(Sfx.Id.Blaster, 0.8f);
        }

        private float ShoulderHeight()
        {
            BlockCharacter character = _player.Character;
            if (character == null || character.ShoulderR == null) return DefaultShoulderHeight;
            return Mathf.Max(0.5f, character.ShoulderR.position.y - transform.position.y);
        }

        private static Vector3 CameraForward()
        {
            Camera cam = Camera.main;
            Vector3 forward = cam != null ? cam.transform.forward : Vector3.forward;
            forward.y = 0f;
            return forward.sqrMagnitude > 0.0001f ? forward.normalized : Vector3.forward;
        }

        /// <summary>
        /// 원거리 조준 보조: 화면 가운데에 가까운 적을 고른다. 바짝 붙은 적은 화면 어디에 있든 고른다.
        /// 벽 뒤의 적은 고르지 않는다. 방금까지 겨누던 적을 조금 더 우선해 목표가 왔다 갔다 하지 않게 한다.
        /// </summary>
        private Health FindRangedTarget(float range, Vector3 shoulder)
        {
            Camera cam = Camera.main;
            Vector3 eye = cam != null ? cam.transform.position : shoulder;
            Vector3 look = CameraForward();

            // 적은 모두 Ignore Raycast 레이어에 있다. 그 레이어만 찾아 건물 충돌체로 버퍼가 차지 않게 한다.
            int count = Physics.OverlapSphereNonAlloc(shoulder, range, RangedBuffer, IgnoreRaycastLayerMask,
                QueryTriggerInteraction.Ignore);

            Health best = null;
            float bestScore = float.MaxValue;
            for (int i = 0; i < count; i++)
            {
                var health = RangedBuffer[i].GetComponentInParent<Health>();
                if (health == null || health.team != Team.Enemy || health.IsDead) continue;

                Vector3 center = RangedBuffer[i].bounds.center;
                Vector3 fromPlayer = center - shoulder;
                float distance = new Vector2(fromPlayer.x, fromPlayer.z).magnitude;
                if (distance > range) continue;

                Vector3 fromEye = center - eye;
                fromEye.y = 0f;
                float angle = Vector3.Angle(look, fromEye);

                float score;
                if (angle <= RangedAimConeDegrees) score = angle / RangedAimConeDegrees + distance / range * 0.6f;
                else if (distance <= RangedCloseRange) score = 2f + distance / RangedCloseRange;
                else continue;
                if (health == _rangedTarget) score -= 0.35f;
                if (score >= bestScore) continue;

                // 사이에 벽이 있으면 겨누지 않는다 (캐릭터들은 이 검사에 걸리지 않는다).
                // 탄환이 날아갈 길과 같은 선으로 본다: 어깨 높이에서 적의 가슴께까지.
                if (Physics.Linecast(shoulder, BoltProjectile.AimPointOf(health), Physics.DefaultRaycastLayers,
                        QueryTriggerInteraction.Ignore)) continue;

                bestScore = score;
                best = health;
            }
            return best;
        }
    }
}
