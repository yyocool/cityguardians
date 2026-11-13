using UnityEngine;

namespace CityGuardians.Gameplay
{
    /// <summary>
    /// 관절 회전을 코드로 움직이는 절차적 애니메이션 (docs/CHARACTER_SYSTEM.md 3장).
    /// 부위가 적어 이것으로 충분하고, 모델을 바꿔도 다시 만들 필요가 없다.
    /// </summary>
    [RequireComponent(typeof(BlockCharacter))]
    public sealed class BlockCharacterAnimator : MonoBehaviour
    {
        private const float AttackDuration = 0.28f;
        private const float HitDuration = 0.22f;
        private const float AimHold = 0.45f;

        /// <summary>이동 속도 (m/s). 걷기·달리기의 폭과 빠르기를 정한다.</summary>
        public float speed;
        public bool grounded = true;
        public float verticalVelocity;
        /// <summary>대화 중처럼 가만히 서 있을 때 말하는 흉내.</summary>
        public bool talking;

        private BlockCharacter _character;
        private float _phase;
        private float _attackTime = -1f;
        private float _hitTime = -1f;
        private float _aimTime;
        private float _aimBlend;
        private float _recoil;
        private float _landSquash;
        private bool _wasGrounded = true;
        private float _blend;

        public bool IsAttacking => _attackTime >= 0f;

        private void Awake()
        {
            _character = GetComponent<BlockCharacter>();
        }

        public void TriggerAttack() => _attackTime = 0f;

        /// <summary>한 발 쏜다: 오른팔을 앞으로 뻗어 겨누고, 쏠 때마다 살짝 튄다. 잠시 뒤 팔을 내린다.</summary>
        public void TriggerShoot()
        {
            _aimTime = AimHold;
            _recoil = 1f;
        }

        public void TriggerHit() => _hitTime = 0f;

        private void LateUpdate()
        {
            if (_character == null || _character.Body == null) return;
            float dt = Time.deltaTime;

            // 걷기·달리기: 팔다리를 엇갈려 흔든다
            float target = Mathf.Clamp01(speed / 4f);
            _blend = Mathf.MoveTowards(_blend, target, dt * 6f);
            bool running = speed > 5f;
            _phase += dt * Mathf.Lerp(6f, 13f, Mathf.Clamp01(speed / 6.5f));
            float swing = Mathf.Sin(_phase) * (running ? 52f : 34f) * _blend;

            float legL = swing;
            float legR = -swing;
            float armL = -swing * 0.9f;
            float armR = swing * 0.9f;
            float lean = running ? 9f * _blend : 3f * _blend;
            float bob = Mathf.Abs(Mathf.Sin(_phase)) * 0.035f * _blend;

            // 가만히 있을 때의 숨쉬기
            float idle = Mathf.Sin(Time.time * 2f) * (1f - _blend);
            bob += idle * 0.008f;
            armL += idle * 2f;
            armR -= idle * 2f;

            // 공중: 팔을 들고 다리를 모은다
            if (!grounded)
            {
                float rise = Mathf.Clamp(verticalVelocity / 6f, -1f, 1f);
                legL = Mathf.Lerp(legL, 18f, 0.8f);
                legR = Mathf.Lerp(legR, -12f, 0.8f);
                armL = Mathf.Lerp(armL, -70f - rise * 40f, 0.8f);
                armR = Mathf.Lerp(armR, -70f - rise * 40f, 0.8f);
            }

            // 착지: 살짝 눌린다
            if (grounded && !_wasGrounded) _landSquash = 1f;
            _wasGrounded = grounded;
            _landSquash = Mathf.MoveTowards(_landSquash, 0f, dt * 6f);
            float squash = 1f - _landSquash * 0.14f;

            // 공격: 오른팔을 뒤로 들었다가 앞으로 휘두른다
            float twist = 0f;
            if (_attackTime >= 0f)
            {
                _attackTime += dt;
                float p = Mathf.Clamp01(_attackTime / AttackDuration);
                float arm = p < 0.3f
                    ? Mathf.Lerp(0f, -150f, p / 0.3f)
                    : Mathf.Lerp(-150f, 35f, Mathf.SmoothStep(0f, 1f, (p - 0.3f) / 0.45f));
                if (p > 0.75f) arm = Mathf.Lerp(35f, 0f, (p - 0.75f) / 0.25f);
                armR = arm;
                twist = Mathf.Sin(p * Mathf.PI) * -22f;
                lean += Mathf.Sin(p * Mathf.PI) * 10f;
                if (_attackTime >= AttackDuration) _attackTime = -1f;
            }

            // 사격: 오른팔을 앞으로 뻗는다. 걷거나 뛰는 중에도 겨눈 팔은 흔들리지 않는다.
            _aimTime = Mathf.Max(0f, _aimTime - dt);
            _aimBlend = Mathf.MoveTowards(_aimBlend, _aimTime > 0f ? 1f : 0f, dt * (_aimTime > 0f ? 14f : 5f));
            _recoil = Mathf.MoveTowards(_recoil, 0f, dt * 9f);
            if (_aimBlend > 0f && _attackTime < 0f)
            {
                armR = Mathf.Lerp(armR, -88f - _recoil * 16f, _aimBlend);
                lean = Mathf.Lerp(lean, lean * 0.4f, _aimBlend);
            }

            // 피격: 뒤로 젖혀진다
            if (_hitTime >= 0f)
            {
                _hitTime += dt;
                float p = Mathf.Clamp01(_hitTime / HitDuration);
                lean -= Mathf.Sin(p * Mathf.PI) * 22f;
                if (_hitTime >= HitDuration) _hitTime = -1f;
            }

            float headNod = 0f;
            if (talking)
            {
                headNod = Mathf.Sin(Time.time * 9f) * 4f;
                _character.SetMouthOpen(Mathf.Sin(Time.time * 14f) > 0f);
            }

            _character.HipL.localRotation = Quaternion.Euler(legL, 0f, 0f);
            _character.HipR.localRotation = Quaternion.Euler(legR, 0f, 0f);
            _character.ShoulderL.localRotation = Quaternion.Euler(armL, 0f, 4f);
            _character.ShoulderR.localRotation = Quaternion.Euler(armR, 0f, -4f);
            _character.Head.localRotation = Quaternion.Euler(headNod - lean * 0.4f, 0f, 0f);
            _character.Body.localRotation = Quaternion.Euler(lean, twist, 0f);
            _character.Body.localPosition = new Vector3(0f, bob, 0f);
            _character.Body.localScale = new Vector3(1f + (1f - squash) * 0.5f, squash, 1f + (1f - squash) * 0.5f);
        }

        public void StopTalking()
        {
            talking = false;
            if (_character != null) _character.SetMouthOpen(false);
        }
    }
}
