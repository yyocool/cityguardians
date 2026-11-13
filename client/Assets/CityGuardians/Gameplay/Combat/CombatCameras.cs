using CityGuardians.Domain;
using CityGuardians.Inputs;
using UnityEngine;

namespace CityGuardians.Gameplay
{
    // 전투 모드별 카메라 (docs/COMBAT_SYSTEM.md 13장).
    //   3인칭 (ThirdPersonCamera 역할) : 기존 OrbitCamera. 이름을 바꾸지 않은 것은 기존 코드와 테스트가 그 이름을 쓰기 때문이다.
    //   1인칭                          : FirstPersonCamera (FPS 모드)
    //   전략 시점                      : DefenseCamera (디펜스 모드, V 키로 켜고 끈다)
    // 세 카메라는 같은 카메라 오브젝트에 붙어 있고, 한 번에 하나만 켜진다. 캐릭터와 이동 코드는 그대로다:
    // PlayerController 는 "카메라가 보는 쪽"을 기준으로 걷기 때문에 카메라만 바꿔도 조작이 맞게 따라온다.

    /// <summary>주 카메라에 붙은 세 카메라 가운데 하나를 켠다.</summary>
    public static class CameraRig
    {
        private const float ThirdPersonFov = 55f;
        private const float FirstPersonFov = 70f;

        /// <summary>지금 켜진 카메라 (CameraKinds). 주 카메라가 없으면 null.</summary>
        public static string Kind
        {
            get
            {
                Camera cam = Camera.main;
                if (cam == null) return null;
                var first = cam.GetComponent<FirstPersonCamera>();
                if (first != null && first.enabled) return CameraKinds.FirstPerson;
                var strategy = cam.GetComponent<DefenseCamera>();
                if (strategy != null && strategy.enabled) return CameraKinds.Strategy;
                return CameraKinds.ThirdPerson;
            }
        }

        /// <summary>그 카메라로 바꾼다. 보던 방향은 이어 간다.</summary>
        public static bool Use(string kind)
        {
            Camera cam = Camera.main;
            PlayerController player = PlayerController.Instance;
            if (cam == null || player == null) return false;

            var orbit = cam.GetComponent<OrbitCamera>();
            var first = cam.GetComponent<FirstPersonCamera>();
            var strategy = cam.GetComponent<DefenseCamera>();

            // 지금 보고 있는 방향
            float yaw = first != null && first.enabled ? first.Yaw
                : strategy != null && strategy.enabled ? strategy.Yaw
                : orbit != null ? orbit.Yaw
                : player.transform.eulerAngles.y;

            bool wantFirst = kind == CameraKinds.FirstPerson;
            bool wantStrategy = kind == CameraKinds.Strategy;
            if (wantFirst && first == null)
            {
                first = cam.gameObject.AddComponent<FirstPersonCamera>();
                first.target = player;
            }
            if (wantStrategy && strategy == null)
            {
                strategy = cam.gameObject.AddComponent<DefenseCamera>();
                strategy.target = player.transform;
            }

            // 먼저 끄고 나서 켠다: 1인칭이 숨겨 둔 캐릭터는 꺼질 때 다시 보이게 된다.
            if (first != null && !wantFirst) first.enabled = false;
            if (strategy != null && !wantStrategy) strategy.enabled = false;
            if (orbit != null) orbit.enabled = !wantFirst && !wantStrategy;

            if (wantFirst)
            {
                first.enabled = true;
                first.SnapTo(yaw);
            }
            else if (wantStrategy)
            {
                if (orbit != null) orbit.ShowTarget();
                strategy.enabled = true;
                strategy.SnapTo(yaw);
            }
            else if (orbit != null)
            {
                orbit.ShowTarget();
                orbit.SnapBehind(yaw);
            }

            cam.fieldOfView = wantFirst ? FirstPersonFov : ThirdPersonFov;
            return true;
        }
    }

    /// <summary>
    /// 1인칭 카메라 (docs/FPS_MODE.md 3장). 눈높이에서 보고, 마우스로 둘러본다.
    /// 캐릭터의 몸은 숨기고, 몸의 방향은 보는 쪽을 따라간다 (걷는 방향과 겨누는 방향이 같아진다).
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public sealed class FirstPersonCamera : MonoBehaviour
    {
        public const float BaseFov = 70f;
        private const float EyeHeight = 1.22f;
        private const float MinPitch = -70f;
        private const float MaxPitch = 70f;
        private const float HideInterval = 0.5f;

        public static FirstPersonCamera Instance { get; private set; }

        public PlayerController target;

        public float Yaw => _yaw;
        public float Pitch => _pitch;
        /// <summary>겨눌 때 시야가 좁아지는 비율. 1이면 그대로. 무기가 정한다.</summary>
        public float zoom = 1f;

        private Camera _camera;
        private float _yaw;
        private float _pitch;
        private float _shake;
        private float _kick;
        private float _nextHide;

        private void Awake()
        {
            _camera = GetComponent<Camera>();
        }

        private void OnEnable()
        {
            Instance = this;
            _nextHide = 0f;
        }

        private void OnDisable()
        {
            if (Instance == this) Instance = null;
            SetTargetVisible(true);
            zoom = 1f;
        }

        /// <summary>그 방향을 정면으로 본다 (장면에 들어왔을 때, 다시 일어났을 때).</summary>
        public void SnapTo(float yaw)
        {
            _yaw = yaw;
            _pitch = 0f;
            _kick = 0f;
            Place(0f);
        }

        public void Shake(float amount) => _shake = Mathf.Max(_shake, amount);

        /// <summary>쏠 때 시야가 살짝 들린다.</summary>
        public void Kick(float degrees) => _kick = Mathf.Min(4f, _kick + degrees);

        /// <summary>보는 방향 (총알이 나가는 방향).</summary>
        public Vector3 Forward => Quaternion.Euler(_pitch - _kick, _yaw, 0f) * Vector3.forward;

        public Vector3 EyePosition => target != null
            ? target.transform.position + Vector3.up * EyeHeight
            : transform.position;

        private void LateUpdate()
        {
            if (target == null) return;
            float dt = Time.unscaledDeltaTime;

            bool locked = GameState.IsControlLocked || target.Health == null || target.Health.IsDead;
            if (!locked)
            {
                Vector2 look = PlayerInputHub.Current.LookDelta;
                // 겨누는 동안에는 시야가 좁아진 만큼 천천히 돈다.
                float sensitivity = Mathf.Lerp(1f, Mathf.Clamp(zoom, 0.3f, 1f), 0.7f);
                _yaw += look.x * sensitivity;
                _pitch = Mathf.Clamp(_pitch - look.y * sensitivity, MinPitch, MaxPitch);
                // 몸이 보는 쪽을 따라온다. 그래야 옆걸음과 뒷걸음이 된다.
                target.AimAlong(Quaternion.Euler(0f, _yaw, 0f) * Vector3.forward, 0.25f);
            }

            Place(dt);

            // 나중에 붙는 부품(갈아입은 옷, 바꾼 무기)도 숨긴다.
            if (Time.unscaledTime >= _nextHide)
            {
                _nextHide = Time.unscaledTime + HideInterval;
                SetTargetVisible(false);
            }
        }

        private void Place(float dt)
        {
            if (target == null) return;
            _kick = Mathf.MoveTowards(_kick, 0f, dt * 14f);
            Quaternion rotation = Quaternion.Euler(_pitch - _kick, _yaw, 0f);
            Vector3 position = EyePosition;
            if (_shake > 0.001f)
            {
                position += Random.insideUnitSphere * _shake;
                _shake = Mathf.MoveTowards(_shake, 0f, dt * 1.2f);
            }
            transform.SetPositionAndRotation(position, rotation);

            if (_camera != null && dt > 0f)
            {
                float wanted = BaseFov * Mathf.Clamp(zoom, 0.3f, 1f);
                _camera.fieldOfView = Mathf.Lerp(_camera.fieldOfView, wanted, 1f - Mathf.Exp(-14f * dt));
            }
        }

        private void SetTargetVisible(bool visible)
        {
            if (target == null) return;
            foreach (MeshRenderer renderer in target.GetComponentsInChildren<MeshRenderer>(true))
                if (renderer.enabled != visible) renderer.enabled = visible;
        }
    }

    /// <summary>
    /// 디펜스 모드의 전략 시점 (docs/DEFENSE_MODE.md 2장). 높은 곳에서 비스듬히 내려다보는 쿼터뷰이고,
    /// 마우스로 자유롭게 돌리고 휠로 당기고 민다. 캐릭터는 그대로 걸어 다니며 타워를 세운다.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public sealed class DefenseCamera : MonoBehaviour
    {
        public Transform target;
        public float distance = 26f;
        public float minDistance = 14f;
        public float maxDistance = 42f;
        public float minPitch = 38f;
        public float maxPitch = 80f;

        public float Yaw => _yaw;
        public float Pitch => _pitch;

        private float _yaw;
        private float _pitch = 56f;
        private Vector3 _focus;
        private bool _hasFocus;

        public void SnapTo(float yaw)
        {
            _yaw = yaw;
            _hasFocus = false;
            Place(0f);
        }

        private void LateUpdate()
        {
            if (target == null) return;
            float dt = Time.unscaledDeltaTime;

            if (!GameState.IsControlLocked)
            {
                IPlayerInput input = PlayerInputHub.Current;
                Vector2 look = input.LookDelta;
                _yaw += look.x;
                _pitch = Mathf.Clamp(_pitch - look.y * 0.5f, minPitch, maxPitch);
                distance = Mathf.Clamp(distance - input.ZoomDelta * 2.5f, minDistance, maxDistance);
            }
            Place(dt);
        }

        private void Place(float dt)
        {
            if (target == null) return;
            Vector3 wanted = target.position + Vector3.up * 1f;
            // 캐릭터를 부드럽게 따라가서, 걸을 때 전장이 흔들려 보이지 않게 한다.
            _focus = _hasFocus && dt > 0f ? Vector3.Lerp(_focus, wanted, 1f - Mathf.Exp(-8f * dt)) : wanted;
            _hasFocus = true;

            Quaternion rotation = Quaternion.Euler(_pitch, _yaw, 0f);
            transform.SetPositionAndRotation(_focus + rotation * Vector3.back * distance, rotation);
        }
    }
}
