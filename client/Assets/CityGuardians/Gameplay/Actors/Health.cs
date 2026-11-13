using System;
using CityGuardians.Domain;
using UnityEngine;

namespace CityGuardians.Gameplay
{
    public enum Team
    {
        Player,
        Enemy,
    }

    /// <summary>체력. 플레이어와 적이 같은 컴포넌트를 쓴다.</summary>
    public sealed class Health : MonoBehaviour
    {
        public Team team;
        /// <summary>피해를 받은 뒤 다시 피해를 받지 않는 시간(초).</summary>
        public float invulnerableAfterHit;
        /// <summary>체력이 이 값 아래로는 내려가지 않는다. 첫 전투에서 지지 않게 하는 데 쓴다.</summary>
        public int floor;
        /// <summary>
        /// 받는 피해가 줄어드는 비율(%). 플레이어가 입은 방어구의 합이다 (docs/COMBAT_SYSTEM.md 4장).
        /// 적에게는 쓰지 않는다.
        /// </summary>
        public int defensePercent;

        public int Max { get; private set; }
        public int Current { get; private set; }
        public bool IsDead => Current <= 0;
        public float LastDamageTime { get; private set; } = -999f;
        /// <summary>마지막 피해가 "가벼운 타격"(탄환)이었는가. 가벼운 타격은 상대를 멈칫하게 하지 못한다.</summary>
        public bool LastHitWasLight { get; private set; }

        /// <summary>(받은 피해, 피해를 준 쪽)</summary>
        public event Action<int, GameObject> Damaged;
        public event Action Died;
        public event Action Changed;

        private float _invulnerableUntil;

        public void Init(int max, int current = -1)
        {
            Max = Mathf.Max(1, max);
            Current = current < 0 ? Max : Mathf.Clamp(current, 0, Max);
            Changed?.Invoke();
        }

        public void SetMax(int max, bool refill)
        {
            int previousMax = Max;
            Max = Mathf.Max(1, max);
            if (refill) Current = Max;
            else Current = Mathf.Clamp(Current + Mathf.Max(0, Max - previousMax), 0, Max);
            Changed?.Invoke();
        }

        /// <param name="light">가벼운 타격인가 (연사하는 탄환). 맞은 쪽이 멈칫하지 않는다.</param>
        /// <returns>실제로 피해가 들어갔으면 true.</returns>
        public bool TakeDamage(int amount, GameObject source, bool light = false)
        {
            if (IsDead || amount <= 0) return false;
            if (Time.time < _invulnerableUntil) return false;
            if (defensePercent > 0) amount = DamageFormula.Reduce(amount, defensePercent);

            int after = Mathf.Max(Mathf.Max(0, floor), Current - amount);
            int dealt = Current - after;
            if (dealt <= 0) return false;

            LastHitWasLight = light;
            Current = after;
            LastDamageTime = Time.time;
            _invulnerableUntil = Time.time + invulnerableAfterHit;
            Damaged?.Invoke(dealt, source);
            Changed?.Invoke();
            if (Current <= 0) Died?.Invoke();
            return true;
        }

        public void Heal(int amount)
        {
            if (IsDead || amount <= 0) return;
            int after = Mathf.Min(Max, Current + amount);
            if (after == Current) return;
            Current = after;
            Changed?.Invoke();
        }

        /// <summary>
        /// 회복을 되돌린다 (서버가 아이템 사용을 거절했을 때). 피해가 아니라서 피격 연출이 없고,
        /// 이것 때문에 쓰러지지는 않는다.
        /// </summary>
        public void UndoHeal(int amount)
        {
            if (IsDead || amount <= 0) return;
            int after = Mathf.Max(1, Current - amount);
            if (after == Current) return;
            Current = after;
            Changed?.Invoke();
        }

        public void Revive(int hp)
        {
            Current = Mathf.Clamp(hp, 1, Max);
            _invulnerableUntil = Time.time + 1.5f;
            Changed?.Invoke();
        }
    }
}
