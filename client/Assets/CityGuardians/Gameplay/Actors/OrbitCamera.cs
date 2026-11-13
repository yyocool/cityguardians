using CityGuardians.Inputs;
using UnityEngine;

namespace CityGuardians.Gameplay
{
    /// <summary>
    /// 3인칭 궤도 카메라. 마우스로 돌리고 휠로 거리를 조절한다.
    /// 벽에 가리면 캐릭터 쪽으로 당겨진다.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public sealed class OrbitCamera : MonoBehaviour
    {
        public static OrbitCamera Instance { get; private set; }

        public Transform target;
        public float pivotHeight = 1.15f;
        public float distance = 5.5f;
        public float minDistance = 2.5f;
        public float maxDistance = 9f;
        public float minPitch = -10f;
        public float maxPitch = 65f;

        private const float CollisionRadius = 0.25f;
        /// <summary>벽에 밀려 이보다 가까워지면 캐릭터가 화면을 가리므로 캐릭터를 그리지 않는다.</summary>
        private const float HideTargetBelow = 1.3f;
        private float _yaw;
        private float _pitch = 22f;
        private float _currentDistance;
        private float _shake;
        private bool _targetHidden;

        public float Yaw => _yaw;
        public float CurrentDistance => _currentDistance;
        public bool TargetHidden => _targetHidden;

        private void Awake()
        {
            Instance = this;
            _currentDistance = distance;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        /// <summary>캐릭터의 등 뒤로 바로 옮긴다.</summary>
        public void SnapBehind(float yaw)
        {
            _yaw = yaw;
            _currentDistance = distance;
            Place(0f);
        }

        public void Shake(float amount) => _shake = Mathf.Max(_shake, amount);

        private void LateUpdate()
        {
            if (target == null) return;
            float dt = Time.unscaledDeltaTime;

            if (!GameState.IsControlLocked)
            {
                IPlayerInput input = PlayerInputHub.Current;
                Vector2 look = input.LookDelta;
                _yaw += look.x;
                _pitch = Mathf.Clamp(_pitch - look.y, minPitch, maxPitch);
                distance = Mathf.Clamp(distance - input.ZoomDelta * 0.8f, minDistance, maxDistance);
            }

            Place(dt);
        }

        private void Place(float dt)
        {
            if (target == null) return;
            Vector3 pivot = target.position + Vector3.up * pivotHeight;
            Quaternion rotation = Quaternion.Euler(_pitch, _yaw, 0f);
            Vector3 back = rotation * Vector3.back;

            // 캐릭터는 Ignore Raycast 레이어에 있어 이 검사에 걸리지 않는다.
            float allowed = distance;
            if (Physics.SphereCast(pivot, CollisionRadius, back, out RaycastHit hit, distance,
                    Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                allowed = Mathf.Max(0.6f, hit.distance);

            // 가려지면 바로 당기고, 풀리면 천천히 물러난다.
            if (allowed < _currentDistance || dt <= 0f) _currentDistance = allowed;
            else _currentDistance = Mathf.MoveTowards(_currentDistance, allowed, dt * 6f);

            Vector3 position = pivot + back * _currentDistance;
            if (_shake > 0.001f)
            {
                position += Random.insideUnitSphere * _shake;
                _shake = Mathf.MoveTowards(_shake, 0f, dt * 1.2f);
            }
            transform.SetPositionAndRotation(position, rotation);

            // 다시 보일 때는 조금 더 물러난 뒤에 켜서, 경계에서 깜빡이지 않게 한다.
            bool hide = _targetHidden ? _currentDistance < HideTargetBelow + 0.2f : _currentDistance < HideTargetBelow;
            if (hide != _targetHidden) SetTargetVisible(!hide);
        }

        /// <summary>캐릭터의 부품이 바뀐 뒤(무기 교체)에 부른다. 숨겨 둔 동안 새로 붙은 부품도 숨긴다.</summary>
        public void RefreshTargetVisibility()
        {
            if (_targetHidden && target != null) SetTargetVisible(false);
        }

        /// <summary>숨겨 둔 캐릭터를 다시 보이게 한다. 다른 카메라(전략 시점)로 바꿀 때 쓴다.</summary>
        public void ShowTarget()
        {
            if (target != null) SetTargetVisible(true);
        }

        private void SetTargetVisible(bool visible)
        {
            _targetHidden = !visible;
            // 장비가 나중에 붙을 수 있으므로 바뀔 때마다 다시 모은다. 이름표 같은 캔버스는 건드리지 않는다.
            foreach (MeshRenderer renderer in target.GetComponentsInChildren<MeshRenderer>(true))
                renderer.enabled = visible;
        }
    }
}
