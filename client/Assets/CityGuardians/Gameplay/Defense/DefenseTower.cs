using CityGuardians.Domain;
using CityGuardians.Services;
using UnityEngine;
using UnityEngine.AI;

namespace CityGuardians.Gameplay
{
    /// <summary>
    /// 방어 타워 (docs/TOWER_DEFENSE.md 5장). 사거리 안에서 방어 거점에 가장 가까이 간 적을 쏜다.
    /// 수치는 모두 TowerDef에서 오고, 타워 종류마다 다른 것은 겉모습과 쏘는 방식(한 발/범위)뿐이다.
    /// </summary>
    public sealed class DefenseTower : MonoBehaviour, IStructure
    {
        private const float BodyRadius = 0.9f;
        private const float HeadHeight = 1.55f;
        private static float _lastShotSfx = -999f;

        public TowerDef Def { get; private set; }
        /// <summary>전투 중 강화 단계. 0부터 시작한다.</summary>
        public int Level { get; private set; }
        public int Hp { get; private set; }
        public int MaxHp => Def.maxHp;
        public bool CanUpgrade => Level < Def.maxUpgradeLevel;
        public int ShotsFired { get; private set; }

        public Vector3 Position => transform.position;
        public float Radius => BodyRadius;
        public bool IsAlive => Hp > 0;

        private DefenseBattle _battle;
        private Transform _visual;
        private Transform _head;
        private Transform _muzzle;
        private HitFlash _flash;
        private EnemyHealthBar _bar;
        private float _cooldown;
        private Color[] _debris;

        public static DefenseTower Build(Transform parent, TowerDef def, Vector3 position, DefenseBattle battle)
        {
            var go = new GameObject("Tower_" + def.code);
            go.transform.SetParent(parent, false);
            go.transform.position = position;

            var tower = go.AddComponent<DefenseTower>();
            tower.Def = def;
            tower._battle = battle;
            tower.Hp = def.maxHp;
            tower.BuildVisual();

            // 적이 타워를 뚫고 지나가지 않게 길 찾기 지도에서 이 자리를 뺀다.
            var obstacle = go.AddComponent<NavMeshObstacle>();
            obstacle.shape = NavMeshObstacleShape.Box;
            obstacle.center = new Vector3(0f, 1f, 0f);
            obstacle.size = new Vector3(1.6f, 2f, 1.6f);
            obstacle.carving = true;

            tower._flash = tower._visual.gameObject.AddComponent<HitFlash>();
            tower._bar = EnemyHealthBar.Create(go.transform, 2.6f, new Color(0.36f, 0.86f, 0.46f), 1.4f);
            // 세워질 때 바닥에서 솟아오른다.
            tower._visual.gameObject.AddComponent<PopIn>();
            return tower;
        }

        private void BuildVisual()
        {
            bool blast = Def.splashRadius > 0f;
            Color baseColor = new Color(0.30f, 0.33f, 0.38f);
            Color body = blast ? new Color(0.90f, 0.42f, 0.22f) : new Color(0.16f, 0.62f, 0.72f);
            Color trim = blast ? new Color(1f, 0.82f, 0.3f) : new Color(0.75f, 0.95f, 1f);
            _debris = new[] { baseColor, body, trim };

            _visual = Blocks.Pivot(transform, "Visual", Vector3.zero);
            Blocks.Box(_visual, "Base", new Vector3(0f, 0.2f, 0f), new Vector3(1.5f, 0.4f, 1.5f), baseColor, true);
            Blocks.Cylinder(_visual, "Column", new Vector3(0f, 0.85f, 0f), new Vector3(0.7f, 0.9f, 0.7f), baseColor, true);
            Blocks.Box(_visual, "Band", new Vector3(0f, 0.45f, 0f), new Vector3(1.52f, 0.08f, 1.52f), trim, false, true);

            _head = Blocks.Pivot(_visual, "Head", new Vector3(0f, HeadHeight, 0f));
            if (blast)
            {
                Blocks.Box(_head, "Body", Vector3.zero, new Vector3(0.95f, 0.55f, 0.95f), body);
                GameObject barrel = Blocks.Cylinder(_head, "Barrel", new Vector3(0f, 0.22f, 0.42f), new Vector3(0.42f, 0.8f, 0.42f), baseColor);
                barrel.transform.localRotation = Quaternion.Euler(55f, 0f, 0f);
                Blocks.Box(_head, "Light", new Vector3(0f, 0.32f, -0.3f), new Vector3(0.18f, 0.12f, 0.18f), trim, false, true);
                _muzzle = Blocks.Pivot(_head, "Muzzle", new Vector3(0f, 0.5f, 0.78f));
            }
            else
            {
                Blocks.Box(_head, "Body", Vector3.zero, new Vector3(0.7f, 0.45f, 0.8f), body);
                Blocks.Box(_head, "BarrelL", new Vector3(-0.16f, 0f, 0.65f), new Vector3(0.1f, 0.1f, 0.7f), baseColor);
                Blocks.Box(_head, "BarrelR", new Vector3(0.16f, 0f, 0.65f), new Vector3(0.1f, 0.1f, 0.7f), baseColor);
                Blocks.Box(_head, "Sight", new Vector3(0f, 0.28f, 0.1f), new Vector3(0.14f, 0.12f, 0.3f), trim, false, true);
                _muzzle = Blocks.Pivot(_head, "Muzzle", new Vector3(0f, 0f, 1.02f));
            }
        }

        private void Update()
        {
            if (!IsAlive || _battle == null) return;
            float dt = Time.deltaTime;
            if (dt <= 0f) return;
            _cooldown -= dt;

            if (_battle.Phase != DefensePhase.Wave) return;
            EnemyAgent target = _battle.FindTarget(transform.position, TowerMath.Range(Def, Level));
            if (target == null) return;

            Vector3 aim = target.transform.position + Vector3.up * (target.Height * 0.5f);
            Vector3 flat = aim - _head.position;
            flat.y = 0f;
            if (flat.sqrMagnitude > 0.01f)
                _head.rotation = Quaternion.RotateTowards(_head.rotation, Quaternion.LookRotation(flat, Vector3.up), 720f * dt);

            if (_cooldown > 0f) return;
            _cooldown = 1f / Mathf.Max(0.05f, Def.shotsPerSecond);
            ShotsFired++;

            int damage = TowerMath.Damage(Def, Level);
            if (Def.splashRadius > 0f)
            {
                BlastShell.Launch(_muzzle.position, target.transform.position, Def.splashRadius, damage, _battle, gameObject);
                Sfx.Play(Sfx.Id.Swing, 0.8f);
            }
            else
            {
                Fx.Tracer(_muzzle.position, aim, new Color(1f, 0.95f, 0.55f));
                target.Health.TakeDamage(damage, gameObject);
                // 여러 타워가 함께 쏠 때 소리가 뭉개지지 않게 간격을 둔다.
                if (Time.time - _lastShotSfx > 0.07f)
                {
                    _lastShotSfx = Time.time;
                    Sfx.Play(Sfx.Id.TowerShot, 0.5f);
                }
            }
        }

        /// <summary>한 단계 강화한다. 피해와 사거리가 늘고, 부서진 곳도 고쳐진다.</summary>
        public void Upgrade()
        {
            if (!CanUpgrade) return;
            Level++;
            Hp = MaxHp;
            if (_bar != null) _bar.Hide();
            // 강화 단계를 받침대의 노란 표시로 보여 준다.
            float x = -0.4f + (Level - 1) * 0.4f;
            Blocks.Box(_visual, "Pip", new Vector3(x, 0.22f, -0.77f), new Vector3(0.22f, 0.22f, 0.04f),
                new Color(1f, 0.86f, 0.2f), false, true);
            Blocks.Box(_visual, "Pip", new Vector3(x, 0.22f, 0.77f), new Vector3(0.22f, 0.22f, 0.04f),
                new Color(1f, 0.86f, 0.2f), false, true);
            Blocks.Box(_visual, "Pip", new Vector3(-0.77f, 0.22f, x), new Vector3(0.04f, 0.22f, 0.22f),
                new Color(1f, 0.86f, 0.2f), false, true);
            Blocks.Box(_visual, "Pip", new Vector3(0.77f, 0.22f, x), new Vector3(0.04f, 0.22f, 0.22f),
                new Color(1f, 0.86f, 0.2f), false, true);
            if (_flash != null) _flash.Capture();
            Fx.FloatingText(transform.position + Vector3.up * 2.8f, $"강화 {Level}단계!", new Color(1f, 0.86f, 0.2f), 0.4f);
        }

        public void TakeDamage(int amount)
        {
            if (!IsAlive || amount <= 0) return;
            Hp = Mathf.Max(0, Hp - amount);
            if (_flash != null) _flash.Flash(0.12f);
            if (_bar != null) _bar.Show(Hp / (float)MaxHp);
            if (Hp > 0) return;

            Fx.Shatter(transform.position + Vector3.up * 1.0f, _debris, 18, 0.28f);
            Sfx.Play(Sfx.Id.Explosion, 0.7f);
            if (_battle != null) _battle.OnTowerDestroyed(this);
            Destroy(gameObject);
        }
    }

    /// <summary>폭발 타워의 포탄. 포물선으로 날아가 떨어진 자리에서 터진다.</summary>
    public sealed class BlastShell : MonoBehaviour
    {
        private Vector3 _from;
        private Vector3 _to;
        private float _duration;
        private float _age;
        private float _radius;
        private int _damage;
        private DefenseBattle _battle;
        private GameObject _source;

        public static void Launch(Vector3 from, Vector3 target, float radius, int damage, DefenseBattle battle, GameObject source)
        {
            GameObject go = Blocks.Sphere(null, "BlastShell", from, new Vector3(0.36f, 0.36f, 0.36f),
                new Color(1f, 0.75f, 0.25f), false, true);
            var shell = go.AddComponent<BlastShell>();
            shell._from = from;
            shell._to = new Vector3(target.x, 0.2f, target.z);
            shell._duration = Mathf.Clamp(Vector3.Distance(from, shell._to) / 16f, 0.3f, 0.75f);
            shell._radius = radius;
            shell._damage = damage;
            shell._battle = battle;
            shell._source = source;
        }

        private void Update()
        {
            _age += Time.deltaTime;
            float t = Mathf.Clamp01(_age / _duration);
            Vector3 position = Vector3.Lerp(_from, _to, t);
            position.y += Mathf.Sin(t * Mathf.PI) * 2.2f;
            transform.position = position;
            if (t < 1f) return;

            if (_battle != null) _battle.Explode(_to, _radius, _damage, _source);
            Destroy(gameObject);
        }
    }

    /// <summary>세워지는 순간 바닥에서 솟아오르듯 커진다.</summary>
    public sealed class PopIn : MonoBehaviour
    {
        private const float Duration = 0.25f;
        private float _age;

        private void Awake() => transform.localScale = new Vector3(1f, 0.05f, 1f);

        private void Update()
        {
            _age += Time.deltaTime;
            float t = Mathf.Clamp01(_age / Duration);
            // 살짝 넘쳤다가 제자리로
            float y = t < 0.7f ? Mathf.Lerp(0.05f, 1.15f, t / 0.7f) : Mathf.Lerp(1.15f, 1f, (t - 0.7f) / 0.3f);
            transform.localScale = new Vector3(1f, y, 1f);
            if (t >= 1f)
            {
                transform.localScale = Vector3.one;
                Destroy(this);
            }
        }
    }

    /// <summary>
    /// 타워를 세울 수 있는 자리 (docs/TOWER_DEFENSE.md 4장). 비어 있으면 E로 타워를 고르고,
    /// 타워가 있으면 E로 강화한다.
    /// </summary>
    public sealed class BuildSlot : MonoBehaviour
    {
        private static readonly Color EmptyColor = new Color(0.45f, 0.95f, 0.85f);
        private static readonly Color UsedColor = new Color(0.24f, 0.36f, 0.40f);

        public int Index { get; private set; }
        public DefenseTower Tower { get; private set; }

        private DefenseBattle _battle;
        private Interactable _interact;
        private GameObject _pad;
        private Transform _plus;

        public static BuildSlot Create(Transform parent, int index, Vector3 position, DefenseBattle battle)
        {
            var go = new GameObject("BuildSlot_" + index);
            go.transform.SetParent(parent, false);
            go.transform.position = position;
            var slot = go.AddComponent<BuildSlot>();
            slot.Index = index;
            slot._battle = battle;

            slot._pad = Blocks.Cylinder(go.transform, "Pad", new Vector3(0f, 0.05f, 0f), new Vector3(2.4f, 0.06f, 2.4f), EmptyColor, false, true);
            Blocks.Cylinder(go.transform, "PadRim", new Vector3(0f, 0.04f, 0f), new Vector3(2.8f, 0.04f, 2.8f), new Color(0.16f, 0.22f, 0.26f));
            // 빈 자리 위에 떠 있는 더하기 표시
            slot._plus = Blocks.Pivot(go.transform, "Plus", new Vector3(0f, 1.2f, 0f));
            Blocks.Box(slot._plus, "H", Vector3.zero, new Vector3(0.7f, 0.2f, 0.12f), Color.white, false, true);
            Blocks.Box(slot._plus, "V", Vector3.zero, new Vector3(0.2f, 0.7f, 0.12f), Color.white, false, true);

            slot._interact = Interactable.Add(go, "타워 세우기", slot.OnInteract, 2.6f, slot.IsUsable);
            return slot;
        }

        private bool IsUsable()
        {
            if (_battle == null || !_battle.CanBuild) return false;
            return Tower == null || Tower.CanUpgrade;
        }

        public void Place(DefenseTower tower)
        {
            Tower = tower;
            Blocks.SetColor(_pad, UsedColor, true);
            _plus.gameObject.SetActive(false);
        }

        private void Update()
        {
            // 타워가 부서지면 다시 빈 자리가 된다.
            if (Tower == null && !_plus.gameObject.activeSelf)
            {
                Blocks.SetColor(_pad, EmptyColor, true);
                _plus.gameObject.SetActive(true);
            }

            if (Tower == null)
            {
                _interact.verb = "타워 세우기";
                _plus.localPosition = new Vector3(0f, 1.2f + Mathf.Sin(Time.time * 2.4f + Index) * 0.12f, 0f);
                _plus.Rotate(0f, 90f * Time.deltaTime, 0f, Space.World);
            }
            else if (Tower.CanUpgrade)
            {
                _interact.verb = $"타워 강화 (에너지 {Tower.Def.upgradeEnergyCost})";
            }
        }

        private void OnInteract()
        {
            if (_battle == null || !_battle.CanBuild) return;
            if (Tower != null)
            {
                _battle.TryUpgrade(Tower);
                return;
            }

            GameSession session = GameSession.Current;
            if (session == null) return;
            var options = new System.Collections.Generic.List<ChoiceOption>();
            foreach (TowerDef def in session.Defs.towers)
            {
                if (options.Count >= ChoiceMenu.MaxOptions) break;
                TowerDef captured = def;
                bool unlocked = session.IsTowerUnlocked(def.code);
                bool affordable = _battle.Energy >= def.energyCost;
                options.Add(new ChoiceOption
                {
                    label = def.name,
                    cost = unlocked ? $"에너지 {def.energyCost}" : "잠김",
                    detail = !unlocked ? $"도시의 방어기지에서 {def.unlockGc} GC로 해금할 수 있어요."
                        : affordable ? def.description
                        : "에너지가 모자라요. 로봇을 물리치면 에너지를 얻어요.",
                    enabled = unlocked && affordable,
                    onChosen = () => _battle.TryBuild(this, captured),
                });
            }
            ChoiceMenu.Instance.Open("타워 세우기", $"가진 에너지  {_battle.Energy}", options.ToArray());
        }
    }
}
