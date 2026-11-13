using CityGuardians.Domain;
using UnityEngine;

namespace CityGuardians.Gameplay
{
    /// <summary>
    /// 적의 겉모습과 움직임. 블록으로 조립하며 종류에 따라 모양이 다르다.
    /// 피나 잔혹한 표현 없이, 고장 난 기계라는 점을 색과 불빛으로 보여 준다.
    /// </summary>
    public sealed class EnemyVisual : MonoBehaviour
    {
        private static readonly Color EyeCalm = new Color(1f, 0.25f, 0.2f);
        private static readonly Color EyeWarn = new Color(1f, 0.95f, 0.3f);

        public float Height { get; private set; }
        public float Radius { get; private set; }
        public Color[] DebrisColors { get; private set; }

        /// <summary>이동 속도 (m/s).</summary>
        public float speed;

        private Transform _body;
        private Transform _armL;
        private Transform _armR;
        private Transform[] _legs;
        private GameObject _eye;
        private WorldLabel _alert;
        private bool _isInsect;
        private float _phase;
        private float _spawnTime;
        private float _windupLeft;
        private float _windupTotal;
        private float _lunge;
        /// <summary>다 자랐을 때의 크기 배율. 건설 메카처럼 큰 적은 같은 뼈대를 키워 쓴다.</summary>
        private float _baseScale = 1f;

        private enum RobotLook { Tutorial, Normal, Mecha }

        public static EnemyVisual Create(Transform parent, EnemyDef def)
        {
            var root = new GameObject("Visual");
            root.transform.SetParent(parent, false);
            var visual = root.AddComponent<EnemyVisual>();
            float alertHeight;
            if (def.code == EnemyCodes.MechInsect)
            {
                visual.BuildInsect();
                alertHeight = visual.Height + 0.45f;
            }
            else if (def.code == EnemyCodes.ConstructionMecha)
            {
                visual.BuildRobot(RobotLook.Mecha);
                alertHeight = visual.Height + 0.55f;
                // 겉모습 안의 좌표는 키우기 전 기준이다. 바깥에서 쓰는 크기는 키운 값으로 알려 준다.
                visual._baseScale = 2f;
                visual.Height *= visual._baseScale;
                visual.Radius *= visual._baseScale;
            }
            else
            {
                visual.BuildRobot(def.code == EnemyCodes.TutorialRobot ? RobotLook.Tutorial : RobotLook.Normal);
                alertHeight = visual.Height + 0.45f;
            }
            visual._alert = WorldLabel.Create(root.transform, new Vector3(0f, alertHeight, 0f), "!", 0.55f, EyeWarn);
            visual._alert.gameObject.SetActive(false);
            root.transform.localScale = Vector3.zero;
            return visual;
        }

        private void BuildRobot(RobotLook look)
        {
            Color shell = look == RobotLook.Tutorial ? new Color(0.62f, 0.66f, 0.70f)
                : look == RobotLook.Mecha ? new Color(0.96f, 0.72f, 0.14f)
                : new Color(0.45f, 0.50f, 0.58f);
            Color dark = new Color(0.22f, 0.24f, 0.28f);
            Color stripe = look == RobotLook.Mecha ? new Color(0.92f, 0.30f, 0.16f) : new Color(1f, 0.60f, 0.12f);
            DebrisColors = new[] { shell, dark, stripe };
            Height = 1.35f;
            Radius = 0.42f;

            _body = Blocks.Pivot(transform, "Body", Vector3.zero);
            _legs = new Transform[2];
            _legs[0] = Blocks.Pivot(_body, "LegL", new Vector3(-0.17f, 0.36f, 0f));
            _legs[1] = Blocks.Pivot(_body, "LegR", new Vector3(0.17f, 0.36f, 0f));
            Blocks.Box(_legs[0], "Leg", new Vector3(0f, -0.15f, 0f), new Vector3(0.16f, 0.30f, 0.18f), dark);
            Blocks.Box(_legs[1], "Leg", new Vector3(0f, -0.15f, 0f), new Vector3(0.16f, 0.30f, 0.18f), dark);
            Blocks.Box(_legs[0], "Foot", new Vector3(0f, -0.32f, 0.04f), new Vector3(0.22f, 0.08f, 0.30f), shell);
            Blocks.Box(_legs[1], "Foot", new Vector3(0f, -0.32f, 0.04f), new Vector3(0.22f, 0.08f, 0.30f), shell);

            Blocks.Box(_body, "Torso", new Vector3(0f, 0.68f, 0f), new Vector3(0.66f, 0.60f, 0.46f), shell);
            Blocks.Box(_body, "Stripe", new Vector3(0f, 0.60f, 0.232f), new Vector3(0.50f, 0.10f, 0.02f), stripe, false, true);
            Blocks.Box(_body, "Panel", new Vector3(0.14f, 0.80f, 0.232f), new Vector3(0.16f, 0.12f, 0.02f), dark);

            Blocks.Box(_body, "Head", new Vector3(0f, 1.14f, 0f), new Vector3(0.50f, 0.34f, 0.40f), shell);
            Blocks.Box(_body, "Visor", new Vector3(0f, 1.15f, 0.201f), new Vector3(0.40f, 0.14f, 0.02f), dark);
            _eye = Blocks.Box(_body, "Eye", new Vector3(0f, 1.15f, 0.212f), new Vector3(0.16f, 0.08f, 0.02f), EyeCalm, false, true);
            if (look == RobotLook.Mecha)
            {
                // 공사장 안전모와 경고등: 원래는 도시를 짓던 기계다
                Blocks.Box(_body, "Helmet", new Vector3(0f, 1.35f, 0f), new Vector3(0.58f, 0.10f, 0.48f), stripe);
                Blocks.Box(_body, "HelmetBrim", new Vector3(0f, 1.31f, 0.26f), new Vector3(0.50f, 0.04f, 0.12f), stripe);
                Blocks.Box(_body, "Beacon", new Vector3(0f, 1.45f, 0f), new Vector3(0.14f, 0.10f, 0.14f),
                    new Color(1f, 0.85f, 0.2f), false, true);
                // 검은 빗금 띠
                for (int i = -1; i <= 1; i++)
                    Blocks.Box(_body, "Hazard", new Vector3(i * 0.2f, 0.46f, 0.232f), new Vector3(0.08f, 0.10f, 0.02f), dark, false, true);
            }
            else
            {
                // 휘어진 안테나: 고장 난 느낌
                Blocks.Box(_body, "Antenna", new Vector3(0.14f, 1.40f, 0f), new Vector3(0.03f, 0.20f, 0.03f), dark)
                    .transform.localRotation = Quaternion.Euler(0f, 0f, -24f);
                Blocks.Box(_body, "AntennaTip", new Vector3(0.19f, 1.50f, 0f), new Vector3(0.07f, 0.07f, 0.07f), stripe, false, true);
            }

            _armL = Blocks.Pivot(_body, "ArmL", new Vector3(-0.40f, 0.90f, 0f));
            _armR = Blocks.Pivot(_body, "ArmR", new Vector3(0.40f, 0.90f, 0f));
            Blocks.Box(_armL, "Arm", new Vector3(0f, -0.22f, 0f), new Vector3(0.14f, 0.44f, 0.16f), dark);
            Blocks.Box(_armR, "Arm", new Vector3(0f, -0.22f, 0f), new Vector3(0.14f, 0.44f, 0.16f), dark);
            if (look == RobotLook.Mecha)
            {
                // 넓은 어깨와 커다란 쇠주먹
                Blocks.Box(_armL, "Shoulder", new Vector3(-0.02f, 0.04f, 0f), new Vector3(0.26f, 0.18f, 0.30f), shell);
                Blocks.Box(_armR, "Shoulder", new Vector3(0.02f, 0.04f, 0f), new Vector3(0.26f, 0.18f, 0.30f), shell);
                Blocks.Box(_armL, "Fist", new Vector3(0f, -0.52f, 0f), new Vector3(0.30f, 0.24f, 0.32f), stripe);
                Blocks.Box(_armR, "Fist", new Vector3(0f, -0.52f, 0f), new Vector3(0.30f, 0.24f, 0.32f), stripe);
            }
            else
            {
                Blocks.Box(_armL, "Claw", new Vector3(0f, -0.49f, 0f), new Vector3(0.20f, 0.14f, 0.22f), stripe);
                Blocks.Box(_armR, "Claw", new Vector3(0f, -0.49f, 0f), new Vector3(0.20f, 0.14f, 0.22f), stripe);
            }
        }

        private void BuildInsect()
        {
            Color shell = new Color(0.36f, 0.30f, 0.52f);
            Color dark = new Color(0.16f, 0.15f, 0.22f);
            Color glow = new Color(0.45f, 1f, 0.55f);
            DebrisColors = new[] { shell, dark, glow };
            Height = 0.55f;
            Radius = 0.36f;
            _isInsect = true;

            _body = Blocks.Pivot(transform, "Body", Vector3.zero);
            Blocks.Box(_body, "Abdomen", new Vector3(0f, 0.30f, -0.18f), new Vector3(0.44f, 0.26f, 0.50f), shell);
            Blocks.Box(_body, "Thorax", new Vector3(0f, 0.30f, 0.16f), new Vector3(0.34f, 0.22f, 0.26f), dark);
            Blocks.Box(_body, "Head", new Vector3(0f, 0.32f, 0.36f), new Vector3(0.26f, 0.20f, 0.18f), shell);
            Blocks.Box(_body, "Back", new Vector3(0f, 0.44f, -0.18f), new Vector3(0.30f, 0.03f, 0.36f), glow, false, true);
            _eye = Blocks.Box(_body, "Eye", new Vector3(0f, 0.34f, 0.452f), new Vector3(0.20f, 0.07f, 0.02f), EyeCalm, false, true);
            Blocks.Box(_body, "JawL", new Vector3(-0.08f, 0.24f, 0.48f), new Vector3(0.04f, 0.04f, 0.12f), dark);
            Blocks.Box(_body, "JawR", new Vector3(0.08f, 0.24f, 0.48f), new Vector3(0.04f, 0.04f, 0.12f), dark);

            _legs = new Transform[6];
            for (int i = 0; i < 6; i++)
            {
                bool left = i % 2 == 0;
                float z = 0.22f - (i / 2) * 0.22f;
                Transform pivot = Blocks.Pivot(_body, "Leg" + i, new Vector3(left ? -0.18f : 0.18f, 0.26f, z));
                Blocks.Box(pivot, "Upper", new Vector3(left ? -0.14f : 0.14f, 0.02f, 0f), new Vector3(0.28f, 0.04f, 0.05f), dark);
                Blocks.Box(pivot, "Lower", new Vector3(left ? -0.28f : 0.28f, -0.12f, 0f), new Vector3(0.04f, 0.28f, 0.05f), dark);
                _legs[i] = pivot;
            }
        }

        /// <summary>공격 예고. 눈이 노랗게 변하고 머리 위에 느낌표가 뜬다.</summary>
        public void BeginWindup(float seconds)
        {
            _windupTotal = Mathf.Max(0.05f, seconds);
            _windupLeft = _windupTotal;
            if (_eye != null) Blocks.SetColor(_eye, EyeWarn, true);
            if (_alert != null) _alert.gameObject.SetActive(true);
        }

        public void CancelWindup()
        {
            if (_windupLeft <= 0f && (_alert == null || !_alert.gameObject.activeSelf)) return;
            _windupLeft = 0f;
            if (_eye != null) Blocks.SetColor(_eye, EyeCalm, true);
            if (_alert != null) _alert.gameObject.SetActive(false);
        }

        /// <summary>타격 순간. 앞으로 짧게 내지른다.</summary>
        public void Strike()
        {
            CancelWindup();
            _lunge = 1f;
        }

        private void Update()
        {
            float dt = Time.deltaTime;

            // 등장: 바닥에서 솟아오르듯 커진다
            _spawnTime += dt;
            float grow = Mathf.Clamp01(_spawnTime / EnemyBrain.SpawnDuration);
            transform.localScale = Vector3.one * (_baseScale * Mathf.SmoothStep(0f, 1f, grow));

            float moving = Mathf.Clamp01(speed / 2f);
            _phase += dt * (_isInsect ? 22f : 9f) * Mathf.Max(0.25f, moving);
            float swing = Mathf.Sin(_phase);

            float windup = 0f;
            if (_windupLeft > 0f)
            {
                _windupLeft -= dt;
                windup = 1f - Mathf.Clamp01(_windupLeft / _windupTotal);
            }
            _lunge = Mathf.MoveTowards(_lunge, 0f, dt * 5f);

            if (_isInsect)
            {
                for (int i = 0; i < _legs.Length; i++)
                {
                    float offset = (i % 2 == 0 ? 0f : Mathf.PI) + (i / 2) * 1.3f;
                    _legs[i].localRotation = Quaternion.Euler(0f, Mathf.Sin(_phase + offset) * 24f * moving, 0f);
                }
                // 예고: 몸을 뒤로 웅크렸다가, 타격: 앞으로 튀어 오른다
                float crouch = windup * -0.12f;
                _body.localPosition = new Vector3(0f, _lunge * 0.25f + Mathf.Abs(swing) * 0.02f * moving,
                    crouch + _lunge * 0.35f);
                _body.localRotation = Quaternion.Euler(windup * -14f + _lunge * 18f, 0f, 0f);
            }
            else
            {
                _legs[0].localRotation = Quaternion.Euler(swing * 26f * moving, 0f, 0f);
                _legs[1].localRotation = Quaternion.Euler(-swing * 26f * moving, 0f, 0f);
                // 고장 난 듯 가끔 움찔거린다
                float glitch = Mathf.PerlinNoise(Time.time * 3f, GetInstanceID() * 0.01f) > 0.8f ? 4f : 0f;
                float raise = windup * -150f + _lunge * 70f;
                _armL.localRotation = Quaternion.Euler(-swing * 18f * moving + raise, 0f, 6f + glitch);
                _armR.localRotation = Quaternion.Euler(swing * 18f * moving + raise, 0f, -6f - glitch);
                _body.localPosition = new Vector3(0f, Mathf.Abs(swing) * 0.03f * moving, _lunge * 0.3f);
                _body.localRotation = Quaternion.Euler(windup * -8f + _lunge * 14f, 0f, glitch * 0.5f);
            }
        }
    }
}
