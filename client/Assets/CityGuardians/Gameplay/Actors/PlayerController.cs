using System.Collections;
using CityGuardians.Inputs;
using CityGuardians.Services;
using UnityEngine;

namespace CityGuardians.Gameplay
{
    /// <summary>
    /// 플레이어 이동 (docs/CHARACTER_SYSTEM.md 5장). 카메라가 보는 방향을 기준으로 움직인다.
    /// 입력은 IPlayerInput으로만 읽는다.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public sealed class PlayerController : MonoBehaviour
    {
        public const float WalkSpeed = 4.0f;
        public const float RunSpeed = 6.5f;
        public const float JumpHeight = 1.2f;
        private const float Gravity = -20f;
        private const float TurnSpeed = 720f;
        private const float RegenDelay = 5f;
        private const float RegenPerSecond = 5f;
        private const float RespawnDelay = 3f;

        public static PlayerController Instance { get; private set; }

        /// <summary>쓰러졌을 때 다시 일어나는 자리. 장면을 만드는 쪽이 정한다.</summary>
        public Vector3 respawnPoint;
        public float respawnYaw;

        public Health Health { get; private set; }
        public BlockCharacter Character { get; private set; }
        public BlockCharacterAnimator Animator { get; private set; }
        public Vector3 PlanarVelocity { get; private set; }
        /// <summary>이번 장면에서 걸은 거리 (m). 이동을 배웠는지 판단하는 데 쓴다.</summary>
        public float DistanceMoved { get; private set; }

        private CharacterController _controller;
        private HitFlash _flash;
        private float _verticalVelocity;
        private float _regenBank;
        private bool _respawning;
        private Vector3 _aimDirection = Vector3.forward;
        private float _aimUntil = -1f;

        private void Awake()
        {
            Instance = this;
            _controller = GetComponent<CharacterController>();
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (GameSession.Current != null)
            {
                GameSession.Current.Changed -= OnSessionChanged;
                GameSession.Current.LeveledUp -= OnLeveledUp;
            }
        }

        public void Setup(BlockCharacter character, Health health)
        {
            Character = character;
            Animator = character.GetComponent<BlockCharacterAnimator>();
            _flash = character.GetComponent<HitFlash>();
            Health = health;
            Health.Damaged += OnDamaged;
            Health.Died += OnDied;
            Health.Changed += () => GameEvents.RaisePlayerHealth(Health.Current, Health.Max);
            if (GameSession.Current != null)
            {
                GameSession.Current.Changed += OnSessionChanged;
                GameSession.Current.LeveledUp += OnLeveledUp;
            }
            GameEvents.RaisePlayerHealth(Health.Current, Health.Max);
        }

        private void OnLeveledUp(int level)
        {
            // 레벨이 오르면 체력이 가득 찬다.
            if (Health != null && !Health.IsDead) Health.Heal(Health.Max);
            Sfx.Play(Sfx.Id.LevelUp);
            // 숫자에 따라 조사(이/가)가 달라지므로 조사가 필요 없는 문장으로 쓴다.
            GameEvents.ShowToast($"레벨 업!  레벨 {level}");
            Fx.FloatingText(transform.position + Vector3.up * 2f, "LEVEL UP!", new Color(0.6f, 1f, 0.7f), 0.5f);
        }

        private void OnSessionChanged()
        {
            // 레벨이 올라 최대 체력이 늘었으면 반영한다.
            int max = GameSession.Current.Me.hpMax;
            if (Health != null && max != Health.Max) Health.SetMax(max, false);
        }

        private void Update()
        {
            float dt = Time.deltaTime;
            if (dt <= 0f) return;

            IPlayerInput input = PlayerInputHub.Current;
            bool locked = GameState.IsControlLocked || Health.IsDead;

            Vector2 move = locked ? Vector2.zero : input.Move;
            Vector3 direction = CameraRelative(move);
            float speed = (!locked && input.Run) ? RunSpeed : WalkSpeed;
            Vector3 planar = direction * speed;

            if (!locked && Time.time < _aimUntil)
            {
                // 겨누는 동안에는 걷는 방향과 상관없이 겨눈 쪽을 본다 (옆걸음, 뒷걸음).
                Quaternion look = Quaternion.LookRotation(_aimDirection, Vector3.up);
                transform.rotation = Quaternion.RotateTowards(transform.rotation, look, TurnSpeed * 2f * dt);
            }
            else if (direction.sqrMagnitude > 0.001f)
            {
                Quaternion look = Quaternion.LookRotation(direction, Vector3.up);
                transform.rotation = Quaternion.RotateTowards(transform.rotation, look, TurnSpeed * dt);
            }

            bool grounded = _controller.isGrounded;
            if (grounded && _verticalVelocity < 0f) _verticalVelocity = -2f;
            if (grounded && !locked && input.JumpPressed)
                _verticalVelocity = Mathf.Sqrt(2f * -Gravity * JumpHeight);
            _verticalVelocity += Gravity * dt;

            Vector3 before = transform.position;
            _controller.Move((planar + Vector3.up * _verticalVelocity) * dt);
            Vector3 moved = transform.position - before;
            moved.y = 0f;
            DistanceMoved += moved.magnitude;
            PlanarVelocity = moved / dt;

            if (Animator != null)
            {
                Animator.speed = PlanarVelocity.magnitude;
                Animator.grounded = _controller.isGrounded;
                Animator.verticalVelocity = _verticalVelocity;
            }

            Regenerate(dt);

            // 세상 밖으로 떨어졌을 때의 안전망
            if (transform.position.y < -20f) Teleport(respawnPoint, respawnYaw);
        }

        private void Regenerate(float dt)
        {
            if (Health.IsDead || Health.Current >= Health.Max) { _regenBank = 0f; return; }
            if (Time.time - Health.LastDamageTime < RegenDelay) return;
            _regenBank += RegenPerSecond * dt;
            if (_regenBank >= 1f)
            {
                int whole = Mathf.FloorToInt(_regenBank);
                _regenBank -= whole;
                Health.Heal(whole);
            }
        }

        private static Vector3 CameraRelative(Vector2 move)
        {
            if (move.sqrMagnitude < 0.0001f) return Vector3.zero;
            Camera cam = Camera.main;
            Vector3 forward = cam != null ? cam.transform.forward : Vector3.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f) forward = Vector3.forward;
            forward.Normalize();
            Vector3 right = new Vector3(forward.z, 0f, -forward.x);
            Vector3 direction = forward * move.y + right * move.x;
            return direction.sqrMagnitude > 1f ? direction.normalized : direction;
        }

        public void Teleport(Vector3 position, float yaw)
        {
            _controller.enabled = false;
            transform.SetPositionAndRotation(position, Quaternion.Euler(0f, yaw, 0f));
            _controller.enabled = true;
            _verticalVelocity = 0f;
            // 켜져 있는 카메라만 옮긴다 (1인칭·전략 시점일 때 3인칭 카메라가 화면을 가져가지 않게).
            if (OrbitCamera.Instance != null && OrbitCamera.Instance.enabled) OrbitCamera.Instance.SnapBehind(yaw);
            if (FirstPersonCamera.Instance != null) FirstPersonCamera.Instance.SnapTo(yaw);
        }

        /// <summary>바라보는 방향을 바로 돌린다 (조준 보조).</summary>
        public void FaceTowards(Vector3 worldPoint)
        {
            Vector3 direction = worldPoint - transform.position;
            direction.y = 0f;
            if (direction.sqrMagnitude > 0.0001f) transform.rotation = Quaternion.LookRotation(direction, Vector3.up);
        }

        /// <summary>
        /// 그 방향을 바로 보고, holdSeconds 동안 걷는 방향과 상관없이 계속 그쪽을 본다.
        /// 원거리 무기를 쏘면서 움직일 때 쓴다.
        /// </summary>
        public void AimAlong(Vector3 worldDirection, float holdSeconds)
        {
            worldDirection.y = 0f;
            if (worldDirection.sqrMagnitude < 0.0001f) return;
            _aimDirection = worldDirection.normalized;
            _aimUntil = Time.time + holdSeconds;
            transform.rotation = Quaternion.LookRotation(_aimDirection, Vector3.up);
        }

        private void OnDamaged(int amount, GameObject source)
        {
            if (Animator != null) Animator.TriggerHit();
            if (_flash != null) _flash.Flash();
            Sfx.Play(Sfx.Id.Hurt);
            GameEvents.RaisePlayerHurt();
        }

        private void OnDied()
        {
            if (_respawning) return;
            StartCoroutine(Respawn());
        }

        private IEnumerator Respawn()
        {
            _respawning = true;
            GameState.Lock(GameState.LockDead);
            GameEvents.ShowToast("쓰러졌어요. 잠시 뒤 안전한 곳에서 다시 일어나요.");
            if (Character != null) Character.Body.localRotation = Quaternion.Euler(-80f, 0f, 0f);
            if (Animator != null) Animator.enabled = false;

            yield return new WaitForSeconds(RespawnDelay - 0.4f);
            GameEvents.RequestFade(true, 0.4f);
            yield return new WaitForSeconds(0.4f);

            Teleport(respawnPoint, respawnYaw);
            Health.Revive(Mathf.CeilToInt(Health.Max * 0.5f));
            if (Animator != null) Animator.enabled = true;
            GameEvents.RequestFade(false, 0.4f);
            GameState.Unlock(GameState.LockDead);
            _respawning = false;
        }
    }
}
