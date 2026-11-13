using System.Collections;
using UnityEngine;

namespace CityGuardians.Gameplay
{
    /// <summary>맞았을 때 잠깐 하얗게 깜빡인다.</summary>
    public sealed class HitFlash : MonoBehaviour
    {
        private MeshRenderer[] _renderers;
        private Material[] _originals;
        private float _until;
        private bool _flashing;

        public void Flash(float seconds = 0.08f)
        {
            if (_renderers == null) Capture();
            if (!_flashing)
            {
                // 깜빡이기 직전의 재질을 기억한다 (색이 바뀌는 부품이 있을 수 있다).
                for (int i = 0; i < _renderers.Length; i++)
                    if (_renderers[i] != null) _originals[i] = _renderers[i].sharedMaterial;
                Material white = MaterialLibrary.Unlit(Color.white);
                for (int i = 0; i < _renderers.Length; i++)
                    if (_renderers[i] != null) _renderers[i].sharedMaterial = white;
                _flashing = true;
            }
            _until = Time.time + seconds;
        }

        /// <summary>부품이 바뀐 뒤(무기 장착 등)에는 다시 수집한다.</summary>
        public void Capture()
        {
            if (_flashing) Restore();
            _renderers = GetComponentsInChildren<MeshRenderer>(true);
            _originals = new Material[_renderers.Length];
        }

        private void Update()
        {
            if (_flashing && Time.time >= _until) Restore();
        }

        private void OnDisable()
        {
            if (_flashing) Restore();
        }

        private void Restore()
        {
            for (int i = 0; i < _renderers.Length; i++)
                if (_renderers[i] != null && _originals[i] != null) _renderers[i].sharedMaterial = _originals[i];
            _flashing = false;
        }
    }

    /// <summary>흩어지는 블록 조각. 물리 엔진 없이 직접 움직인다.</summary>
    public sealed class Debris : MonoBehaviour
    {
        public Vector3 velocity;
        public Vector3 spin;
        public float life = 1.1f;
        private float _age;
        private Vector3 _startScale;

        private void Start() => _startScale = transform.localScale;

        private void Update()
        {
            float dt = Time.deltaTime;
            _age += dt;
            velocity += Vector3.down * 18f * dt;
            Vector3 position = transform.position + velocity * dt;
            if (position.y < 0.06f)
            {
                position.y = 0.06f;
                velocity = new Vector3(velocity.x * 0.6f, -velocity.y * 0.35f, velocity.z * 0.6f);
            }
            transform.position = position;
            transform.Rotate(spin * dt, Space.Self);

            float remaining = 1f - _age / life;
            transform.localScale = _startScale * Mathf.Clamp01(remaining * 2f);
            if (_age >= life) Destroy(gameObject);
        }
    }

    /// <summary>적이 떨어뜨린 GC 조각. 튀어 오른 뒤 플레이어에게 빨려 들어온다.</summary>
    public sealed class CoinFly : MonoBehaviour
    {
        public Transform target;
        public Vector3 velocity;
        private float _age;

        private void Update()
        {
            float dt = Time.deltaTime;
            _age += dt;
            transform.Rotate(0f, 540f * dt, 0f, Space.World);

            if (_age < 0.35f || target == null)
            {
                velocity += Vector3.down * 14f * dt;
                Vector3 position = transform.position + velocity * dt;
                if (position.y < 0.15f) { position.y = 0.15f; velocity.y = Mathf.Abs(velocity.y) * 0.4f; }
                transform.position = position;
                if (target == null && _age > 1.5f) Destroy(gameObject);
                return;
            }

            Vector3 goal = target.position + Vector3.up * 0.8f;
            float speed = 6f + (_age - 0.35f) * 30f;
            transform.position = Vector3.MoveTowards(transform.position, goal, speed * dt);
            if ((transform.position - goal).sqrMagnitude < 0.09f)
            {
                Sfx.Play(Sfx.Id.Coin, 0.6f);
                Destroy(gameObject);
            }
        }
    }

    public static class Fx
    {
        private static readonly Color CoinColor = new Color(1f, 0.82f, 0.2f);

        /// <summary>블록 조각으로 흩어진다. 적이 정지할 때 쓴다 (피나 잔혹한 표현 없음).</summary>
        public static void Shatter(Vector3 center, Color[] colors, int count, float size = 0.18f)
        {
            for (int i = 0; i < count; i++)
            {
                Color color = colors[i % colors.Length];
                float s = size * Random.Range(0.6f, 1.2f);
                GameObject piece = Blocks.Box(null, "Debris", center + Random.insideUnitSphere * 0.3f,
                    new Vector3(s, s, s), color);
                var debris = piece.AddComponent<Debris>();
                Vector3 dir = Random.insideUnitSphere;
                dir.y = Mathf.Abs(dir.y) + 0.6f;
                debris.velocity = dir.normalized * Random.Range(2.5f, 5.5f);
                debris.spin = Random.insideUnitSphere * 540f;
                debris.life = Random.Range(0.8f, 1.3f);
            }
        }

        public static void Coins(Vector3 center, int count, Transform target)
        {
            for (int i = 0; i < count; i++)
            {
                GameObject coin = Blocks.Box(null, "Coin", center + Vector3.up * 0.4f,
                    new Vector3(0.18f, 0.18f, 0.06f), CoinColor, false, true);
                var fly = coin.AddComponent<CoinFly>();
                fly.target = target;
                Vector2 side = Random.insideUnitCircle * 2.2f;
                fly.velocity = new Vector3(side.x, Random.Range(3.5f, 5.5f), side.y);
            }
        }

        /// <summary>
        /// 곧 넓은 공격이 떨어질 자리를 바닥에 보여 준다. seconds 동안 커지다가 사라진다.
        /// parent에 붙어 함께 움직이므로, 공격하는 쪽이 방향을 틀면 표시도 따라간다.
        /// </summary>
        public static void Telegraph(Transform parent, Vector3 localPos, float radius, float seconds, Color color)
        {
            GameObject disc = Blocks.Cylinder(parent, "Telegraph", localPos, new Vector3(0.1f, 0.02f, 0.1f), color, false, true);
            var grow = disc.AddComponent<GrowDisc>();
            grow.fromDiameter = radius * 0.5f;
            grow.toDiameter = radius * 2f;
            grow.seconds = Mathf.Max(0.05f, seconds);
        }

        /// <summary>바닥을 따라 퍼지는 충격파. 넓은 타격과 폭발에 쓴다.</summary>
        public static void Shockwave(Vector3 center, float radius, Color color)
        {
            GameObject disc = Blocks.Cylinder(null, "Shockwave", new Vector3(center.x, 0.09f, center.z),
                new Vector3(0.1f, 0.03f, 0.1f), color, false, true);
            var grow = disc.AddComponent<GrowDisc>();
            grow.fromDiameter = radius * 0.6f;
            grow.toDiameter = radius * 2f;
            grow.seconds = 0.22f;
        }

        /// <summary>총알이 지나간 자리의 짧은 빛줄기.</summary>
        public static void Tracer(Vector3 from, Vector3 to, Color color)
        {
            Vector3 delta = to - from;
            float length = delta.magnitude;
            if (length < 0.05f) return;
            GameObject line = Blocks.Box(null, "Tracer", (from + to) * 0.5f, new Vector3(0.05f, 0.05f, length), color, false, true);
            line.transform.rotation = Quaternion.LookRotation(delta / length, Vector3.up);
            Object.Destroy(line, 0.05f);
        }

        /// <summary>떠오르며 사라지는 숫자·글자.</summary>
        public static void FloatingText(Vector3 position, string text, Color color, float height = 0.42f)
        {
            WorldLabel label = WorldLabel.Create(null, position, text, height, color);
            label.gameObject.AddComponent<FloatingTextMotion>();
        }
    }

    /// <summary>바닥에서 커지다가 사라지는 원판. 공격 예고와 충격파가 함께 쓴다.</summary>
    public sealed class GrowDisc : MonoBehaviour
    {
        public float fromDiameter = 0.5f;
        public float toDiameter = 2f;
        public float seconds = 0.3f;
        private float _age;
        private float _height;

        private void Start()
        {
            _height = transform.localScale.y;
            Apply(0f);
        }

        private void Update()
        {
            _age += Time.deltaTime;
            Apply(Mathf.Clamp01(_age / seconds));
            if (_age >= seconds) Destroy(gameObject);
        }

        private void Apply(float t)
        {
            float diameter = Mathf.Lerp(fromDiameter, toDiameter, Mathf.Sqrt(t));
            transform.localScale = new Vector3(diameter, _height, diameter);
        }
    }

    public sealed class FloatingTextMotion : MonoBehaviour
    {
        private const float Life = 0.75f;
        private float _age;
        private WorldLabel _label;
        private Vector3 _drift;

        private void Start()
        {
            _label = GetComponent<WorldLabel>();
            _drift = new Vector3(Random.Range(-0.4f, 0.4f), 1.6f, 0f);
        }

        private void Update()
        {
            _age += Time.deltaTime;
            transform.position += _drift * Time.deltaTime;
            if (_label != null) _label.Alpha = Mathf.Clamp01((Life - _age) / 0.3f);
            if (_age >= Life) Destroy(gameObject);
        }
    }

    /// <summary>타격 순간의 아주 짧은 멈춤. 장면이 바뀌어도 시간 배율이 되돌아오도록 한곳에서 관리한다.</summary>
    public sealed class TimeFx : MonoBehaviour
    {
        public static bool Enabled = true;
        private static TimeFx _instance;
        private Coroutine _routine;

        public static void Attach(GameObject host)
        {
            _instance = host.GetComponent<TimeFx>();
            if (_instance == null) _instance = host.AddComponent<TimeFx>();
        }

        public static void HitStop(float realSeconds = 0.05f)
        {
            if (!Enabled || _instance == null || GameState.IsPaused) return;
            if (_instance._routine != null) _instance.StopCoroutine(_instance._routine);
            _instance._routine = _instance.StartCoroutine(_instance.Run(realSeconds));
        }

        private IEnumerator Run(float realSeconds)
        {
            Time.timeScale = 0.05f;
            yield return new WaitForSecondsRealtime(realSeconds);
            if (!GameState.IsPaused) Time.timeScale = 1f;
            _routine = null;
        }
    }
}
