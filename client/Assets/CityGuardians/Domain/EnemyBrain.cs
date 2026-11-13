using System;

namespace CityGuardians.Domain
{
    public enum EnemyState
    {
        Spawn,
        Idle,
        Patrol,
        Chase,
        Attack,
        Stagger,
        Return,
        Dead,
    }

    public enum EnemyMove
    {
        None,
        ToTarget,
        ToHome,
        Wander,
    }

    /// <summary>적이 이번 프레임에 알고 있는 것. 실행 계층(Unity)이 채워서 넘긴다.</summary>
    public struct EnemySenses
    {
        public bool hasTarget;
        public float targetDistance;
        /// <summary>목표가 이 적의 활동 구역 안에 있는가. 안전 구역으로 달아난 목표는 쫓지 않는다.</summary>
        public bool targetInsideLeash;
        public bool atHome;
        /// <summary>한 목표를 동시에 공격하는 근접 적의 수 제한에 걸리지 않았는가.</summary>
        public bool canEngage;
    }

    /// <summary>두뇌가 실행 계층에 요청하는 행동.</summary>
    public struct EnemyIntent
    {
        public EnemyMove move;
        /// <summary>이번 프레임에 타격 판정을 해야 하는가. 공격 한 번에 한 프레임만 true.</summary>
        public bool strike;
        /// <summary>공격 예고 동작이 방금 시작되었는가.</summary>
        public bool windupStarted;
        /// <summary>제자리에 돌아와 체력을 회복해야 하는가.</summary>
        public bool healToFull;
    }

    /// <summary>
    /// 적의 상태 기계 (docs/ENEMY_AI.md 3장). Unity에 의존하지 않아 단위 테스트할 수 있다.
    /// 이동 방식과 목표 선택은 실행 계층이 맡고, 두뇌는 상태와 의도만 정한다.
    /// </summary>
    public sealed class EnemyBrain
    {
        public const float SpawnDuration = 0.6f;
        public const float StaggerDuration = 0.2f;
        public const float PatrolDuration = 3.0f;
        /// <summary>
        /// 맞은 적은 평소보다 이만큼 더 멀리까지 공격자를 쫓는다.
        /// 원거리 무기(사거리 25 m)로 멀리서 쏴도 적이 가만히 맞고만 있지 않게 한다.
        /// </summary>
        public const float AggravatedRangeFactor = 1.6f;

        private readonly EnemyDef _def;
        private readonly Random _rng;
        private float _stateTime;
        private float _idleDuration;
        private bool _struck;
        private bool _aggravated;

        public EnemyState State { get; private set; }
        public float StateTime => _stateTime;

        public EnemyBrain(EnemyDef def, Random rng)
        {
            _def = def ?? throw new ArgumentNullException(nameof(def));
            _rng = rng ?? throw new ArgumentNullException(nameof(rng));
            Enter(EnemyState.Spawn);
        }

        /// <summary>피해를 입었을 때. 감지 거리 밖이어도 공격자를 쫓게 된다.</summary>
        /// <param name="stagger">멈칫하는가. 중간 보스처럼 멈추지 않는 적은 false.</param>
        public void OnDamaged(bool stagger = true)
        {
            if (State == EnemyState.Dead || State == EnemyState.Spawn) return;
            _aggravated = true;
            if (stagger) Enter(EnemyState.Stagger);
        }

        public void OnDied()
        {
            Enter(EnemyState.Dead);
        }

        public EnemyIntent Tick(float dt, in EnemySenses senses)
        {
            var intent = new EnemyIntent { move = EnemyMove.None };
            if (dt < 0f) dt = 0f;
            _stateTime += dt;

            switch (State)
            {
                case EnemyState.Spawn:
                    if (_stateTime >= SpawnDuration) Enter(EnemyState.Idle);
                    break;

                case EnemyState.Idle:
                    if (ShouldChase(senses)) { Enter(EnemyState.Chase); intent.move = EnemyMove.ToTarget; }
                    else if (_stateTime >= _idleDuration) { Enter(EnemyState.Patrol); intent.move = EnemyMove.Wander; }
                    break;

                case EnemyState.Patrol:
                    if (ShouldChase(senses)) { Enter(EnemyState.Chase); intent.move = EnemyMove.ToTarget; }
                    else if (_stateTime >= PatrolDuration) Enter(EnemyState.Idle);
                    else intent.move = EnemyMove.Wander;
                    break;

                case EnemyState.Chase:
                    if (!senses.hasTarget || !senses.targetInsideLeash || senses.targetDistance > LoseRange)
                    {
                        Enter(EnemyState.Return);
                        intent.move = EnemyMove.ToHome;
                    }
                    else if (senses.targetDistance <= _def.attackRange && senses.canEngage)
                    {
                        Enter(EnemyState.Attack);
                        intent.windupStarted = true;
                    }
                    else if (!senses.canEngage && senses.targetDistance <= _def.attackRange * 2f)
                    {
                        // 공격 자리가 없으면 가까이에서 기다린다.
                        intent.move = EnemyMove.None;
                    }
                    else
                    {
                        intent.move = EnemyMove.ToTarget;
                    }
                    break;

                case EnemyState.Attack:
                    if (!_struck && _stateTime >= _def.attackWindup)
                    {
                        _struck = true;
                        intent.strike = true;
                    }
                    if (_stateTime >= Math.Max(_def.attackInterval, _def.attackWindup))
                    {
                        bool inRange = senses.hasTarget && senses.targetInsideLeash
                                       && senses.targetDistance <= _def.attackRange && senses.canEngage;
                        if (inRange)
                        {
                            Enter(EnemyState.Attack);
                            intent.windupStarted = true;
                        }
                        else
                        {
                            Enter(EnemyState.Chase);
                        }
                    }
                    break;

                case EnemyState.Stagger:
                    if (_stateTime >= StaggerDuration)
                    {
                        if (senses.hasTarget && senses.targetInsideLeash) Enter(EnemyState.Chase);
                        else Enter(EnemyState.Return);
                    }
                    break;

                case EnemyState.Return:
                    if (senses.atHome)
                    {
                        _aggravated = false;
                        intent.healToFull = true;
                        Enter(EnemyState.Idle);
                    }
                    else
                    {
                        intent.move = EnemyMove.ToHome;
                    }
                    break;

                case EnemyState.Dead:
                    break;
            }

            return intent;
        }

        private bool ShouldChase(in EnemySenses senses)
        {
            if (!senses.hasTarget || !senses.targetInsideLeash) return false;
            if (_aggravated) return senses.targetDistance <= LoseRange;
            return senses.targetDistance <= _def.detectRange;
        }

        /// <summary>이 거리보다 멀어지면 추적을 포기한다. 맞은 뒤에는 더 멀리까지 쫓는다.</summary>
        private float LoseRange => _aggravated ? _def.loseRange * AggravatedRangeFactor : _def.loseRange;

        private void Enter(EnemyState next)
        {
            State = next;
            _stateTime = 0f;
            _struck = false;
            if (next == EnemyState.Idle)
                _idleDuration = 2f + (float)_rng.NextDouble() * 2f;
        }
    }
}
