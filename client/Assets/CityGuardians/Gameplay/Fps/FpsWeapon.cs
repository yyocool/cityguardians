using System.Collections.Generic;
using CityGuardians.Domain;
using CityGuardians.Inputs;
using CityGuardians.Services;
using UnityEngine;

namespace CityGuardians.Gameplay
{
    /// <summary>
    /// FPS 모드의 무기와 장치 (docs/FPS_MODE.md 4장, docs/EQUIPMENT_SYSTEM.md).
    /// 무엇을 들고 있는지는 공통 장비 시스템(EquipmentSystem)의 FPS 장착 칸에서 읽는다:
    ///   주 무기 (마우스 왼쪽, 오른쪽으로 겨누기, R 재장전), 보조 장치 (Q), 지원 장비 (F).
    /// 수치는 모두 장비 정의와 강화 단계에서 나온다. 여기서 정하는 수치는 없다.
    /// </summary>
    public sealed class FpsWeapon : MonoBehaviour
    {
        private const int IgnoreRaycastLayer = 2;
        /// <summary>적은 이만큼 넉넉한 굵기로 맞힌다. 조준이 조금 빗나가도 맞는다.</summary>
        private const float HitRadius = 0.16f;
        /// <summary>겨누지 않고 쏠 때 탄이 퍼지는 각도.</summary>
        private const float HipSpreadDegrees = 1.4f;
        private static readonly RaycastHit[] Hits = new RaycastHit[24];
        private static readonly List<EnemyAgent> Buffer = new List<EnemyAgent>();
        private static readonly Color BlasterColor = new Color(0.45f, 0.95f, 1f);
        private static readonly Color RifleColor = new Color(1f, 0.9f, 0.45f);
        private static readonly Color ShockColor = new Color(0.75f, 0.6f, 1f);
        private static readonly Color[] Sparks = { new Color(1f, 0.95f, 0.6f), Color.white };

        public static FpsWeapon Current { get; private set; }

        public GearDef Primary { get; private set; }
        public GearDef Gadget { get; private set; }
        public GearDef Support { get; private set; }

        public int Ammo { get; private set; }
        public int ClipSize => Primary != null ? Mathf.Max(1, Primary.clipSize) : 0;
        public bool Reloading => Time.time < _reloadUntil;
        /// <summary>재장전이 얼마나 되었는가 (0~1). 재장전 중이 아니면 1.</summary>
        public float ReloadProgress => Reloading && _reloadSeconds > 0f
            ? 1f - Mathf.Clamp01((_reloadUntil - Time.time) / _reloadSeconds)
            : 1f;
        public bool Aiming { get; private set; }
        public float GadgetCooldownLeft => Mathf.Max(0f, _gadgetReadyAt - Time.time);
        public float SupportCooldownLeft => Mathf.Max(0f, _supportReadyAt - Time.time);
        /// <summary>탐지 센서가 켜져 있는가. 켜져 있으면 화면이 벽 너머의 로봇과 시민을 표시한다.</summary>
        public bool SensorActive => Time.time < _sensorUntil;
        public float SensorRadius { get; private set; }
        public bool ShieldActive => Time.time < _shieldUntil && _player != null && _player.Health.barrier > 0;
        public int ShieldLeft => ShieldActive ? _player.Health.barrier : 0;
        public bool DroneActive => _drone != null;
        /// <summary>마지막으로 로봇을 맞힌 때. 화면의 명중 표시에 쓴다.</summary>
        public float LastHitTime { get; private set; } = -999f;
        public int ShotsFired { get; private set; }
        public int ShotsHit { get; private set; }

        private PlayerController _player;
        private FpsMission _mission;
        private float _nextShotAt;
        private float _reloadUntil = -1f;
        private float _reloadSeconds;
        private bool _reloadPending;
        private float _gadgetReadyAt;
        private float _supportReadyAt;
        private float _sensorUntil = -1f;
        private float _shieldUntil = -1f;
        private FpsDrone _drone;
        private Transform _viewModel;
        private Transform _muzzle;
        private float _viewKick;

        public static FpsWeapon Attach(PlayerController player, FpsMission mission)
        {
            var weapon = player.gameObject.AddComponent<FpsWeapon>();
            weapon._player = player;
            weapon._mission = mission;
            weapon.LoadFromLoadout();
            Current = weapon;
            return weapon;
        }

        private void OnDestroy()
        {
            if (Current == this) Current = null;
            if (_player != null && _player.Health != null) _player.Health.barrier = 0;
            if (_viewModel != null) Destroy(_viewModel.gameObject);
        }

        /// <summary>장착 칸에서 장비를 읽는다. 아무것도 사지 않았어도 무료 기본 장비가 들어 있다.</summary>
        public void LoadFromLoadout()
        {
            Primary = EquipmentSystem.Equipped(CombatModeCodes.Fps, GearSlots.Primary);
            Gadget = EquipmentSystem.Equipped(CombatModeCodes.Fps, GearSlots.Gadget);
            Support = EquipmentSystem.Equipped(CombatModeCodes.Fps, GearSlots.Support);
            Ammo = ClipSize;
            _reloadUntil = -1f;
            _reloadPending = false;
            BuildViewModel();
        }

        // ── 매 프레임 ───────────────────────────────────────────

        private void Update()
        {
            FirstPersonCamera cam = FirstPersonCamera.Instance;
            bool canAct = _mission != null && _mission.IsActive && cam != null && _player != null
                          && !GameState.IsControlLocked && !_player.Health.IsDead
                          && Time.frameCount > GameState.LastUnlockFrame;

            ExpireEffects();
            FinishReload();

            IPlayerInput input = PlayerInputHub.Current;
            Aiming = canAct && Primary != null && input.AimHeld && !Reloading;
            if (cam != null) cam.zoom = Aiming ? Mathf.Clamp(Primary.aimZoom, 0.3f, 1f) : 1f;
            UpdateViewModel(cam);
            if (!canAct || Primary == null) return;

            if (input.ReloadPressed) StartReload();
            if (input.AttackHeld) TryFire(cam);
            if (input.GadgetPressed) UseGadget();
            if (input.SupportPressed) UseSupport();
        }

        // ── 주 무기 ─────────────────────────────────────────────

        private void TryFire(FirstPersonCamera cam)
        {
            if (Reloading || Time.time < _nextShotAt) return;
            if (Ammo <= 0)
            {
                StartReload();
                return;
            }

            Ammo--;
            ShotsFired++;
            _nextShotAt = Time.time + 1f / Mathf.Max(0.2f, Primary.attacksPerSecond);
            int level = EquipmentSystem.Level(Primary);
            int damage = GearRules.Damage(Primary, level);

            Vector3 origin = cam.EyePosition;
            Vector3 direction = cam.Forward;
            bool hit;
            if (Primary.coneDegrees > 0f)
            {
                hit = FireCone(origin, direction, damage);
                Sfx.Play(Sfx.Id.Explosion, 0.45f);
                cam.Kick(2.2f);
                cam.Shake(0.03f);
            }
            else
            {
                if (!Aiming) direction = Spread(direction, HipSpreadDegrees);
                // 정밀 라이플처럼 한 발이 무거운 무기는 맞은 로봇이 멈칫한다. 연사 무기는 멈칫하게 하지 못한다.
                bool heavy = Primary.attacksPerSecond < 2f;
                hit = FireRay(origin, direction, damage, heavy);
                Sfx.Play(Sfx.Id.Blaster, heavy ? 0.9f : 0.55f);
                cam.Kick(heavy ? 1.8f : 0.35f);
            }
            _viewKick = 1f;
            if (hit)
            {
                ShotsHit++;
                LastHitTime = Time.time;
            }
            if (Ammo <= 0) StartReload();
        }

        /// <summary>탄환 하나: 보는 방향으로 사거리까지. 처음 닿는 로봇이나 벽에서 멈춘다.</summary>
        private bool FireRay(Vector3 origin, Vector3 direction, int damage, bool heavy)
        {
            float range = Mathf.Max(1f, Primary.range);
            Health victim = null;
            float best = range;

            int count = Physics.SphereCastNonAlloc(origin, HitRadius, direction, Hits, range, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                RaycastHit hit = Hits[i];
                var health = hit.collider.GetComponentInParent<Health>();
                if (health == null || health.team != Team.Enemy || health.IsDead) continue;
                if (hit.distance >= best) continue;
                best = hit.distance;
                victim = health;
            }

            count = Physics.RaycastNonAlloc(origin, direction, Hits, range, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                RaycastHit hit = Hits[i];
                // 캐릭터(플레이어, 시민)와 보이지 않는 막이는 그대로 지나간다. 로봇은 위에서 이미 봤다.
                if (hit.collider.gameObject.layer == IgnoreRaycastLayer) continue;
                if (hit.collider.GetComponentInParent<Health>() != null) continue;
                if (hit.distance >= best) continue;
                best = hit.distance;
                victim = null;
            }

            Vector3 end = origin + direction * best;
            Vector3 from = _muzzle != null ? _muzzle.position : origin + direction * 0.4f;
            Fx.Tracer(from, end, heavy ? RifleColor : BlasterColor);
            if (victim == null)
            {
                if (best < range) Fx.Shatter(end, Sparks, 2, 0.06f);
                return false;
            }
            Fx.Shatter(end, Sparks, heavy ? 5 : 3, 0.07f);
            victim.TakeDamage(damage, gameObject, !heavy);
            return true;
        }

        /// <summary>충격파: 정면 부채꼴 안의 로봇이 모두 맞고 뒤로 밀린다.</summary>
        private bool FireCone(Vector3 origin, Vector3 direction, int damage)
        {
            Vector3 flat = direction;
            flat.y = 0f;
            if (flat.sqrMagnitude < 0.0001f) flat = transform.forward;
            flat.Normalize();
            Vector3 center = transform.position;

            Fx.Shockwave(center + flat * (Primary.range * 0.5f), Primary.range * 0.5f, ShockColor);
            Buffer.Clear();
            _mission.CollectEnemies(center, Primary.range, Buffer);
            // 맞아 쓰러지며 목록이 바뀔 수 있어 복사해서 돈다.
            EnemyAgent[] targets = Buffer.ToArray();
            Buffer.Clear();
            bool any = false;
            float half = Primary.coneDegrees * 0.5f;
            for (int i = 0; i < targets.Length; i++)
            {
                EnemyAgent enemy = targets[i];
                if (enemy == null || enemy.IsGone) continue;
                Vector3 to = enemy.transform.position - center;
                to.y = 0f;
                // 바로 앞에 붙은 로봇은 각도와 상관없이 맞는다.
                if (to.magnitude > 1.2f && Vector3.Angle(flat, to) > half) continue;
                any = true;
                if (Primary.knockback > 0f) enemy.Knockback(to.sqrMagnitude > 0.01f ? to : flat, Primary.knockback);
                enemy.Health.TakeDamage(damage, gameObject);
            }
            return any;
        }

        private static Vector3 Spread(Vector3 direction, float degrees)
        {
            Vector2 offset = Random.insideUnitCircle * degrees;
            return Quaternion.LookRotation(direction, Vector3.up) * Quaternion.Euler(offset.y, offset.x, 0f) * Vector3.forward;
        }

        public void StartReload()
        {
            if (Primary == null || Reloading || Ammo >= ClipSize) return;
            _reloadSeconds = Mathf.Max(0.2f, Primary.reloadSeconds);
            _reloadUntil = Time.time + _reloadSeconds;
            _reloadPending = true;
            Sfx.Play(Sfx.Id.Interact, 0.6f);
        }

        private void FinishReload()
        {
            if (!_reloadPending || Reloading) return;
            _reloadPending = false;
            Ammo = ClipSize;
            Sfx.Play(Sfx.Id.Build, 0.4f);
        }

        // ── 보조 장치 (Q) ───────────────────────────────────────

        private void UseGadget()
        {
            if (Gadget == null) return;
            if (GadgetCooldownLeft > 0f)
            {
                GameEvents.ShowToast($"{Gadget.name}: {Mathf.CeilToInt(GadgetCooldownLeft)}초 뒤에 다시 쓸 수 있어요.");
                return;
            }
            int level = EquipmentSystem.Level(Gadget);
            int value = GearRules.EffectValue(Gadget, level);

            switch (Gadget.kind)
            {
                case GearKinds.Heal:
                    if (_player.Health.Current >= _player.Health.Max)
                    {
                        GameEvents.ShowToast("체력이 이미 가득해요.");
                        return;
                    }
                    _player.Health.Heal(value);
                    Sfx.Play(Sfx.Id.Heal);
                    GameEvents.ShowToast($"{Gadget.name}: 체력 +{value}");
                    break;

                case GearKinds.Emp:
                    Buffer.Clear();
                    int count = _mission.CollectEnemies(transform.position, Gadget.effectRadius, Buffer);
                    for (int i = 0; i < count; i++) Buffer[i].Stun(Gadget.effectSeconds);
                    Buffer.Clear();
                    Fx.Shockwave(transform.position, Gadget.effectRadius, new Color(0.75f, 0.55f, 1f));
                    Sfx.Play(Sfx.Id.Alarm, 0.4f);
                    GameEvents.ShowToast(count > 0
                        ? $"{Gadget.name}: 로봇 {count}대가 잠깐 멈췄어요!"
                        : $"{Gadget.name}: 범위 안에 로봇이 없었어요.");
                    break;

                case GearKinds.Shield:
                    _player.Health.barrier = value;
                    _shieldUntil = Time.time + Gadget.effectSeconds;
                    Sfx.Play(Sfx.Id.Build, 0.6f);
                    GameEvents.ShowToast($"{Gadget.name}: 피해 {value}까지 막아 줘요.");
                    break;

                default:
                    return;
            }
            _gadgetReadyAt = Time.time + GearRules.Cooldown(Gadget, level);
        }

        // ── 지원 장비 (F) ───────────────────────────────────────

        private void UseSupport()
        {
            if (Support == null)
            {
                GameEvents.ShowToast("지원 장비가 없어요. 방어기지의 전투 장비에서 구할 수 있어요.");
                return;
            }
            if (SupportCooldownLeft > 0f)
            {
                GameEvents.ShowToast($"{Support.name}: {Mathf.CeilToInt(SupportCooldownLeft)}초 뒤에 다시 쓸 수 있어요.");
                return;
            }
            int level = EquipmentSystem.Level(Support);

            switch (Support.kind)
            {
                case GearKinds.Sensor:
                    _sensorUntil = Time.time + Support.effectSeconds;
                    SensorRadius = Support.effectRadius;
                    Sfx.Play(Sfx.Id.Interact);
                    GameEvents.ShowToast($"{Support.name}: 벽 너머의 로봇과 시민이 보여요.");
                    break;

                case GearKinds.Drone:
                    if (_drone != null) Destroy(_drone.gameObject);
                    _drone = FpsDrone.Launch(_player, _mission, Support, GearRules.EffectValue(Support, level));
                    Sfx.Play(Sfx.Id.Build, 0.6f);
                    GameEvents.ShowToast($"{Support.name} 출동! {Mathf.RoundToInt(Support.effectSeconds)}초 동안 함께 싸워요.");
                    break;

                default:
                    return;
            }
            _supportReadyAt = Time.time + GearRules.Cooldown(Support, level);
        }

        private void ExpireEffects()
        {
            if (_player != null && _player.Health.barrier > 0 && Time.time >= _shieldUntil) _player.Health.barrier = 0;
        }

        // ── 손에 든 무기 (1인칭에서 보이는 모습) ─────────────────

        private void BuildViewModel()
        {
            if (_viewModel != null) Destroy(_viewModel.gameObject);
            _viewModel = null;
            _muzzle = null;
            Camera cam = Camera.main;
            if (cam == null || Primary == null) return;

            _viewModel = Blocks.Pivot(cam.transform, "FpsViewModel", new Vector3(0.24f, -0.22f, 0.5f));
            Color body = new Color(0.22f, 0.25f, 0.30f);
            bool rifle = Primary.kind == GearKinds.Rifle;
            bool shock = Primary.kind == GearKinds.Shockwave;
            Color glow = rifle ? RifleColor : shock ? ShockColor : BlasterColor;
            float length = rifle ? 0.62f : shock ? 0.36f : 0.42f;
            float width = shock ? 0.16f : 0.08f;
            Blocks.Box(_viewModel, "Body", new Vector3(0f, 0f, 0f), new Vector3(width, 0.1f, length), body);
            Blocks.Box(_viewModel, "Grip", new Vector3(0f, -0.09f, -length * 0.3f), new Vector3(0.06f, 0.14f, 0.07f), body);
            Blocks.Box(_viewModel, "Glow", new Vector3(0f, 0.055f, 0f), new Vector3(width * 0.5f, 0.02f, length * 0.7f), glow, false, true);
            if (rifle)
                Blocks.Box(_viewModel, "Scope", new Vector3(0f, 0.1f, -0.05f), new Vector3(0.05f, 0.05f, 0.2f), body);
            _muzzle = Blocks.Pivot(_viewModel, "Muzzle", new Vector3(0f, 0f, length * 0.5f + 0.04f));
        }

        private void UpdateViewModel(FirstPersonCamera cam)
        {
            if (_viewModel == null) return;
            bool visible = cam != null && cam.enabled && _player != null && !_player.Health.IsDead;
            if (_viewModel.gameObject.activeSelf != visible) _viewModel.gameObject.SetActive(visible);
            if (!visible) return;

            float dt = Time.deltaTime;
            _viewKick = Mathf.MoveTowards(_viewKick, 0f, dt * 9f);
            // 겨누면 가운데로 모이고, 재장전 중에는 아래로 내려간다.
            Vector3 rest = Aiming ? new Vector3(0f, -0.13f, 0.42f) : new Vector3(0.24f, -0.22f, 0.5f);
            if (Reloading) rest += new Vector3(0f, -0.16f, -0.05f);
            rest += new Vector3(0f, 0f, -0.06f * _viewKick);
            _viewModel.localPosition = Vector3.Lerp(_viewModel.localPosition, rest, 1f - Mathf.Exp(-16f * dt));
            _viewModel.localRotation = Quaternion.Euler(-6f * _viewKick + (Reloading ? 24f : 0f), 0f, 0f);
        }
    }

    /// <summary>
    /// 지원 드론 (지원 장비). 정해진 시간 동안 플레이어 곁을 떠다니며 가까운 로봇을 쏜다.
    /// 드론이 쓰러뜨린 로봇도 임무의 처치 수에 들어간다 (방어전의 타워와 같다).
    /// </summary>
    public sealed class FpsDrone : MonoBehaviour
    {
        private static readonly Color DroneColor = new Color(0.5f, 1f, 0.7f);

        private PlayerController _player;
        private FpsMission _mission;
        private GearDef _def;
        private int _damage;
        private float _life;
        private float _cooldown;

        public float TimeLeft => Mathf.Max(0f, _life);

        public static FpsDrone Launch(PlayerController player, FpsMission mission, GearDef def, int damage)
        {
            Transform root = Blocks.Pivot(null, "FpsDrone", player.transform.position + Vector3.up * 2.2f);
            Blocks.Box(root, "Body", Vector3.zero, new Vector3(0.5f, 0.16f, 0.5f), new Color(0.24f, 0.28f, 0.34f));
            Blocks.Sphere(root, "Eye", new Vector3(0f, -0.08f, 0.2f), new Vector3(0.16f, 0.16f, 0.16f), DroneColor, false, true);
            foreach (Vector3 arm in new[] { new Vector3(0.3f, 0.06f, 0.3f), new Vector3(-0.3f, 0.06f, 0.3f),
                         new Vector3(0.3f, 0.06f, -0.3f), new Vector3(-0.3f, 0.06f, -0.3f) })
                Blocks.Cylinder(root, "Rotor", arm, new Vector3(0.26f, 0.02f, 0.26f), DroneColor, false, true);
            var drone = root.gameObject.AddComponent<FpsDrone>();
            drone._player = player;
            drone._mission = mission;
            drone._def = def;
            drone._damage = Mathf.Max(1, damage);
            drone._life = Mathf.Max(1f, def.effectSeconds);
            return drone;
        }

        private void Update()
        {
            float dt = Time.deltaTime;
            _life -= dt;
            if (_life <= 0f || _player == null || _mission == null || !_mission.IsActive)
            {
                Fx.Shatter(transform.position, new[] { DroneColor, Color.white }, 6, 0.08f);
                Destroy(gameObject);
                return;
            }

            // 플레이어의 오른쪽 어깨 위를 따라다닌다.
            Vector3 anchor = _player.transform.position + _player.transform.right * 0.9f
                             + Vector3.up * (2.2f + Mathf.Sin(Time.time * 2.4f) * 0.12f);
            transform.position = Vector3.Lerp(transform.position, anchor, 1f - Mathf.Exp(-6f * dt));

            _cooldown -= dt;
            EnemyAgent target = _mission.Nearest(_player.transform.position, Mathf.Max(2f, _def.range));
            if (target == null) return;

            Vector3 aim = target.transform.position + Vector3.up * (target.Height * 0.6f);
            Vector3 to = aim - transform.position;
            if (to.sqrMagnitude > 0.01f)
                transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(to, Vector3.up), 1f - Mathf.Exp(-10f * dt));
            if (_cooldown > 0f) return;

            _cooldown = 1f / Mathf.Max(0.2f, _def.attacksPerSecond);
            Fx.Tracer(transform.position, aim, DroneColor);
            Sfx.Play(Sfx.Id.TowerShot, 0.3f);
            // 드론의 사격은 가벼운 타격이다: 로봇이 멈칫하지 않고, 드론을 쫓아오지도 않는다.
            target.Health.TakeDamage(_damage, gameObject, true);
        }
    }
}
