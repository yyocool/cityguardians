using System;
using System.Collections.Generic;
using CityGuardians.Domain;
using CityGuardians.Inputs;
using CityGuardians.Services;
using UnityEngine;

namespace CityGuardians.Gameplay
{
    // 협동 방어전의 화면 쪽 (docs/COOP_DEFENSE_SYSTEM.md 7장, docs/TACTICAL_MINIMAP.md).
    //
    // 전투의 권한은 서버(온라인) 또는 같은 규칙을 돌리는 연습전(LocalCoopLink)에 있다.
    // 여기서는 ICoopLink 가 알려 준 상태를 그리고, "무엇을 하겠다"는 요청만 보낸다:
    // 적의 위치, 타워, 웨이브, 관문 내구도, 공동 예산을 스스로 정하지 않는다.

    /// <summary>다음 협동 방어전 장면이 쓸 연결. 도시에서 정해 두고 장면이 가져간다.</summary>
    public static class CoopLaunch
    {
        private static ICoopLink _link;
        private static string _sessionId;

        public static bool HasPending => _link != null;

        public static void Set(ICoopLink link, string sessionId)
        {
            Discard();
            _link = link;
            _sessionId = sessionId;
        }

        public static ICoopLink Take(out string sessionId)
        {
            ICoopLink link = _link;
            sessionId = _sessionId;
            _link = null;
            _sessionId = null;
            return link;
        }

        /// <summary>쓰지 않게 된 연결을 닫는다.</summary>
        public static void Discard()
        {
            if (_link != null && _link.State != CoopLinkStates.Closed) _link.Leave();
            _link = null;
            _sessionId = null;
        }

        /// <summary>연습전. NPC 대원과 함께 같은 규칙을 기기 안에서 돌린다. 보상도 기록도 없다.</summary>
        public static ICoopLink NewPractice(GameSession session, int helpers = 3)
        {
            GameDefs defs = session.Defs;
            var unlocked = new List<string>();
            for (int i = 0; i < defs.towers.Count; i++)
                if (defs.towers[i].IsBuildable && session.IsTowerUnlocked(defs.towers[i].code)) unlocked.Add(defs.towers[i].code);
            return new LocalCoopLink(CoopPractice.Config(defs, 4), session.Me.displayName, session.Me.level, unlocked, helpers);
        }
    }

    /// <summary>여섯 개가 넘는 선택지를 쪽으로 나눠 보여 준다.</summary>
    public static class PagedMenu
    {
        public static void Open(string title, string body, List<ChoiceOption> options, Action onBack, string tag, int page = 0)
        {
            if (options.Count == 0)
            {
                onBack?.Invoke();
                return;
            }
            int perPage = options.Count <= ChoiceMenu.MaxOptions ? ChoiceMenu.MaxOptions : ChoiceMenu.MaxOptions - 1;
            int pages = (options.Count + perPage - 1) / perPage;
            page = Mathf.Clamp(page, 0, pages - 1);
            var shown = new List<ChoiceOption>();
            for (int i = page * perPage; i < options.Count && shown.Count < perPage; i++) shown.Add(options[i]);
            if (pages > 1)
            {
                int next = (page + 1) % pages;
                shown.Add(new ChoiceOption
                {
                    label = "다음 쪽", cost = $"{page + 1} / {pages}", detail = "더 있어요.",
                    onChosen = () => Open(title, body, options, onBack, tag, next),
                });
            }
            ChoiceMenu.Instance.Open(title, body, shown.ToArray());
            ChoiceMenu.Instance.Tag = tag;
            ChoiceMenu.Instance.OnCancelled = onBack;
        }
    }

    /// <summary>
    /// 협동 방어전 한 판. 연결(ICoopLink)을 쥐고, 서버가 알려 준 상태에 맞춰 대원·적·타워·핑을 그린다.
    /// 입력은 요청으로만 나간다: 타워 설치·강화·수리, 전술 핑, 역할, 시작, 나가기.
    /// </summary>
    public sealed class CoopBattle : MonoBehaviour
    {
        public const string MenuTag = "coop";
        private const float ResultInputDelay = 0.8f;
        private const float CloseReturnDelay = 2.5f;
        private const float RecordRetrySeconds = 2f;
        private const int RecordMaxTries = 8;

        private static readonly Color EnemyBar = new Color(0.95f, 0.36f, 0.30f);
        private static readonly Color TracerColor = new Color(1f, 0.92f, 0.5f);

        public static CoopBattle Current { get; private set; }

        public ICoopLink Link { get; private set; }
        public CoopMap Map => Link != null ? Link.Map : null;
        /// <summary>온라인일 때 게임 서버의 방어전 번호. 연습전은 null.</summary>
        public string SessionId { get; private set; }
        /// <summary>전술 지도를 펼쳤는가.</summary>
        public bool TacticalOpen { get; private set; }
        public bool ShowRoute { get; private set; } = true;
        public bool ShowRanges { get; private set; } = true;
        /// <summary>게임 서버가 확정한 결과와 보상 (온라인). 아직 받지 못했으면 null.</summary>
        public CoopResultDto Record { get; private set; }
        /// <summary>결과 화면이 뜬 때 (Time.unscaledTime). 아직이면 음수.</summary>
        public float FinishedAt { get; private set; } = -1f;
        public bool Leaving { get; private set; }
        public int FocusEnemyId { get; private set; }

        private sealed class MateView
        {
            public GameObject root;
            public BlockCharacterAnimator animator;
            public WorldLabel label;
            public Vector3 target;
            public string shown;
        }

        private sealed class FoeView
        {
            public GameObject root;
            public EnemyVisual visual;
            public Transform bar;
            public Vector3 target;
            public Vector3 facing = Vector3.back;
            public Color[] debris;
            public float height = 1.2f;
        }

        private sealed class TowerView
        {
            public GameObject root;
            public Transform head;
            public Transform muzzle;
            public WorldLabel label;
            public string code;
            public int level = -1;
            public int hp = -1;
            public float range;
            public float fireEvery;
            public float cooldown;
            public Color trim;
            public Color[] debris;
        }

        private readonly Dictionary<string, MateView> _mates = new Dictionary<string, MateView>();
        private readonly Dictionary<int, FoeView> _foes = new Dictionary<int, FoeView>();
        private readonly Dictionary<int, TowerView> _towers = new Dictionary<int, TowerView>();
        private readonly Dictionary<int, GameObject> _pings = new Dictionary<int, GameObject>();
        private readonly List<int> _gone = new List<int>();
        private readonly List<string> _goneMates = new List<string>();
        private readonly MinimapOptions _mapOptions = new MinimapOptions();
        private MinimapSource _lastSource;

        private Transform _actors;
        private PlayerController _player;
        private ObjectiveMarker _focusMarker;
        private long _shownTick = -1;
        private string _shownObjective;
        private float _shotCooldown;
        private float _closedAt = -1f;
        private bool _attackWasHeld;
        private int _recordTries;
        private float _recordWait;
        private bool _recordAsked;

        public static CoopBattle Create(Transform actors, ICoopLink link, string sessionId, PlayerController player,
            Vector3 signalPoint, Vector3 exitPoint)
        {
            var battle = actors.gameObject.AddComponent<CoopBattle>();
            battle._actors = actors;
            battle.Link = link;
            battle.SessionId = sessionId;
            battle._player = player;
            battle._focusMarker = ObjectiveMarker.Create();
            battle._focusMarker.transform.SetParent(actors, true);

            CoopMap map = link.Map;
            for (int i = 0; i < map.slots.Length; i++)
                CoopSlot.Create(actors, i, new Vector3(map.slots[i].x, 0f, map.slots[i].z), battle);
            battle.BuildSignal(signalPoint);
            battle.BuildExit(exitPoint);
            return battle;
        }

        private void OnEnable() => Current = this;

        private void OnDestroy()
        {
            if (Current == this) Current = null;
            GameState.Unlock(GameState.LockTacticalMap);
            GameState.Unlock(GameState.LockResult);
            GameEvents.SetObjective(null);
            // 장면을 떠나면 방어전에서도 나간다. 온라인 전투는 남은 대원들과 계속된다.
            if (Link != null && Link.State != CoopLinkStates.Closed) Link.Leave();
        }

        // ── 상태 ────────────────────────────────────────────────

        public CoopSnapshot Snapshot => Link != null ? Link.Snapshot : null;
        public bool IsHost => Link != null && Link.MyPid != null && Link.MyPid == Link.HostPid;
        public bool InLobby => Link != null && Link.State == CoopLinkStates.Lobby;
        public bool InBattle => Link != null && Link.State == CoopLinkStates.Battle;
        /// <summary>타워를 세우거나 고칠 수 있는 때인가.</summary>
        public bool CanAct => InBattle && Snapshot != null && Snapshot.IsRunning;

        public CoopPlayerView Me => Snapshot != null ? Snapshot.FindPlayer(Link.MyPid) : null;

        public string NameOf(string pid)
        {
            CoopPlayerView player = Snapshot != null ? Snapshot.FindPlayer(pid) : null;
            return player != null ? player.name : "대원";
        }

        public TowerDef StructureOf(string code)
        {
            IReadOnlyList<CoopStructureInfo> all = Link.Structures;
            for (int i = 0; i < all.Count; i++)
                if (all[i].def.code == code) return all[i].def;
            return null;
        }

        /// <summary>내 역할의 할인을 반영한 값. 실제 차감은 서버가 한다. 여기서는 보여 주기만 한다.</summary>
        public int ShownCost(int cost)
        {
            CoopPlayerView me = Me;
            CoopRoleDef role = null;
            if (me != null && !string.IsNullOrEmpty(me.role))
                for (int i = 0; i < Link.Roles.Count; i++)
                    if (Link.Roles[i].code == me.role) role = Link.Roles[i];
            int discount = role != null ? Mathf.Clamp(role.buildDiscountPercent, 0, 90) : 0;
            return cost <= 0 ? 0 : (cost * (100 - discount) + 99) / 100;
        }

        /// <summary>미니맵에 그릴 내용. UI 가 매 프레임 부른다.</summary>
        public MinimapView BuildMinimap()
        {
            CoopSnapshot snapshot = Snapshot;
            if (snapshot == null || Map == null) return null;
            MinimapSource source = snapshot.ToMinimap(Map, Link.MyPid);
            // 내 위치는 화면의 캐릭터를 따른다 (서버가 알려 주는 위치는 한 박자 늦다).
            if (_player != null)
                for (int i = 0; i < source.players.Count; i++)
                    if (source.players[i].pid == Link.MyPid)
                        source.players[i].pos = new CoopVec(_player.transform.position.x, _player.transform.position.z);
            // 가려 둔 대원의 핑은 보이지 않는다.
            for (int i = source.pings.Count - 1; i >= 0; i--)
                if (Link.IsHidden(source.pings[i].pid)) source.pings.RemoveAt(i);
            _mapOptions.expanded = TacticalOpen;
            _mapOptions.showRoute = ShowRoute;
            _mapOptions.showRanges = ShowRanges;
            _lastSource = source;
            return Minimap.Build(source, _mapOptions, Link.PingRules);
        }

        /// <summary>전술 지도 위의 한 곳(0~1)을 눌렀다: 그곳에 보낼 핑을 고른다.</summary>
        public void PingOnMap(float u, float v)
        {
            if (_lastSource == null || !InBattle) return;
            CoopVec at = Minimap.ToWorld(_lastSource, _mapOptions, u, v);
            OpenPingMenu(at.x, at.z, null);
        }

        // ── 매 프레임 ───────────────────────────────────────────

        private void Update()
        {
            if (Link == null) return;

            // 연습전은 게임을 멈추면 함께 멈춘다. 온라인 전투는 서버에서 계속 흐르므로 연결도 계속 돌린다.
            float dt = Link.Practice ? Time.deltaTime : Time.unscaledDeltaTime;
            if (_player != null && (InLobby || InBattle))
                Link.Move(_player.transform.position.x, _player.transform.position.z);
            Link.Update(dt);

            HandleEvents();
            HandleResults();

            CoopSnapshot snapshot = Link.Snapshot;
            if (snapshot != null && snapshot.tick != _shownTick)
            {
                _shownTick = snapshot.tick;
                Sync(snapshot);
            }
            Animate(Time.deltaTime);
            UpdateObjective();

            if (Link.State == CoopLinkStates.Finished) UpdateFinished();
            else if (Link.State == CoopLinkStates.Closed) UpdateClosed();
            else HandleInput();
        }

        private void HandleEvents()
        {
            List<CoopEvent> events = Link.DrainEvents();
            for (int i = 0; i < events.Count; i++)
            {
                CoopEvent e = events[i];
                bool mine = e.pid != null && e.pid == Link.MyPid;
                switch (e.type)
                {
                    case CoopEventTypes.Started:
                        Sfx.Play(Sfx.Id.Alarm, 0.5f);
                        GameEvents.ShowToast(Link.Practice
                            ? "연습전 시작! NPC 대원과 함께 관문을 지켜요. 연습전에는 보상이 없어요."
                            : $"방어전 시작! 대원 {e.value}명이 함께 관문을 지켜요.");
                        break;
                    case CoopEventTypes.WaveStarted:
                        Sfx.Play(Sfx.Id.Alarm, 0.4f);
                        GameEvents.ShowToast($"웨이브 {e.value} 시작!");
                        break;
                    case CoopEventTypes.WaveCleared:
                        Sfx.Play(Sfx.Id.Coin);
                        GameEvents.ShowToast($"웨이브 {e.value}을(를) 막아냈어요! 공동 예산이 들어왔어요.");
                        break;
                    case CoopEventTypes.TowerBuilt:
                        Sfx.Play(Sfx.Id.Interact);
                        if (!mine) GameEvents.ShowToast($"{NameOf(e.pid)}: {StructureName(e.code)} 설치");
                        break;
                    case CoopEventTypes.TowerUpgraded:
                        if (!mine) GameEvents.ShowToast($"{NameOf(e.pid)}: {StructureName(e.code)} 강화");
                        break;
                    case CoopEventTypes.TowerLost:
                        Sfx.Play(Sfx.Id.Explosion, 0.6f);
                        GameEvents.ShowToast($"{StructureName(e.code)}이(가) 부서졌어요!");
                        break;
                    case CoopEventTypes.GateHit:
                        Sfx.Play(Sfx.Id.Hurt, 0.7f);
                        GameEvents.RaisePlayerHurt();
                        break;
                    case CoopEventTypes.Ping:
                        if (mine || Link.IsHidden(e.pid)) break;
                        Sfx.Play(Sfx.Id.Talk, 0.7f);
                        GameEvents.ShowToast($"{NameOf(e.pid)}: {PingKinds.DisplayName(e.code)}");
                        break;
                    case "player_left":
                        if (!mine) GameEvents.ShowToast($"{NameOf(e.pid)} 대원의 연결이 끊겼어요. 전투는 계속돼요.");
                        break;
                    case "player_joined":
                        if (!mine) GameEvents.ShowToast($"{NameOf(e.pid)} 대원이 들어왔어요.");
                        break;
                }
            }
        }

        private void HandleResults()
        {
            List<CoopActionResult> results = Link.DrainResults();
            for (int i = 0; i < results.Count; i++)
            {
                CoopActionResult result = results[i];
                if (result.ok || result.duplicate) continue;
                GameEvents.ShowToast(CoopTexts.Reject(result.reject));
            }
        }

        private string StructureName(string code)
        {
            TowerDef def = StructureOf(code);
            return def != null ? def.name : "타워";
        }

        private void UpdateObjective()
        {
            string text = null;
            CoopSnapshot snapshot = Snapshot;
            if (InLobby && snapshot != null)
            {
                int here = snapshot.ConnectedCount;
                text = !IsHost ? $"방장이 시작하기를 기다리고 있어요  (대원 {here}명)"
                    : here < Link.MinPlayersToStart ? $"대원이 더 모이기를 기다려요  ({here} / {Link.MinPlayersToStart}명)"
                    : $"신호기에서 방어전을 시작하세요  (대원 {here}명)";
            }
            if (text == _shownObjective) return;
            _shownObjective = text;
            GameEvents.SetObjective(text);
        }

        // ── 서버 상태에 맞추기 ──────────────────────────────────

        private void Sync(CoopSnapshot snapshot)
        {
            GameSession session = GameSession.Current;

            // 대원 (나는 내 캐릭터가 따로 있다)
            _goneMates.Clear();
            foreach (KeyValuePair<string, MateView> pair in _mates)
                if (snapshot.FindPlayer(pair.Key) == null) _goneMates.Add(pair.Key);
            for (int i = 0; i < _goneMates.Count; i++)
            {
                Destroy(_mates[_goneMates[i]].root);
                _mates.Remove(_goneMates[i]);
            }
            for (int i = 0; i < snapshot.players.Count; i++)
            {
                CoopPlayerView player = snapshot.players[i];
                if (player.pid == Link.MyPid) continue;
                if (!_mates.TryGetValue(player.pid, out MateView mate))
                {
                    mate = CreateMate(player);
                    _mates[player.pid] = mate;
                }
                mate.target = new Vector3(player.x, 0f, player.z);
                string role = string.IsNullOrEmpty(player.role) ? "" : "  · " + CoopTexts.RoleName(Link.Roles, player.role);
                string shown = player.connected ? player.name + role : player.name + "  (연결 끊김)";
                if (shown != mate.shown)
                {
                    mate.shown = shown;
                    mate.label.SetText(shown);
                    mate.label.Alpha = player.connected ? 1f : 0.5f;
                }
            }

            // 적
            _gone.Clear();
            foreach (KeyValuePair<int, FoeView> pair in _foes)
                if (snapshot.FindEnemy(pair.Key) == null) _gone.Add(pair.Key);
            for (int i = 0; i < _gone.Count; i++)
            {
                FoeView foe = _foes[_gone[i]];
                // 쓰러졌거나 관문에 닿았다. 어느 쪽이든 조각으로 흩어진다.
                Fx.Shatter(foe.root.transform.position + Vector3.up * foe.height * 0.5f, foe.debris, 6);
                Destroy(foe.root);
                _foes.Remove(_gone[i]);
                if (FocusEnemyId == _gone[i]) FocusEnemyId = 0;
            }
            for (int i = 0; i < snapshot.enemies.Count; i++)
            {
                CoopEnemyView enemy = snapshot.enemies[i];
                if (!_foes.TryGetValue(enemy.id, out FoeView foe))
                {
                    foe = CreateFoe(enemy, session);
                    _foes[enemy.id] = foe;
                }
                foe.target = new Vector3(enemy.x, 0f, enemy.z);
                if (Mathf.Abs(enemy.dx) + Mathf.Abs(enemy.dz) > 0.001f) foe.facing = new Vector3(enemy.dx, 0f, enemy.dz);
                float ratio = enemy.maxHp > 0 ? Mathf.Clamp01(enemy.hp / (float)enemy.maxHp) : 0f;
                foe.bar.localScale = new Vector3(Mathf.Max(0.02f, ratio), 1f, 1f);
                foe.bar.localPosition = new Vector3(-(1f - ratio) * 0.5f, 0f, -0.01f);
            }

            // 타워
            _gone.Clear();
            foreach (KeyValuePair<int, TowerView> pair in _towers)
                if (snapshot.FindTower(pair.Key) == null) _gone.Add(pair.Key);
            for (int i = 0; i < _gone.Count; i++)
            {
                TowerView tower = _towers[_gone[i]];
                Fx.Shatter(tower.root.transform.position + Vector3.up, tower.debris, 10, 0.26f);
                Destroy(tower.root);
                _towers.Remove(_gone[i]);
            }
            for (int i = 0; i < snapshot.towers.Count; i++)
            {
                CoopTowerView state = snapshot.towers[i];
                if (!Map.IsSlot(state.slot)) continue;
                if (_towers.TryGetValue(state.slot, out TowerView tower) && tower.code != state.code)
                {
                    // 부서진 자리에 다른 타워가 섰다.
                    Destroy(tower.root);
                    _towers.Remove(state.slot);
                    tower = null;
                }
                if (tower == null)
                {
                    tower = CreateTower(state);
                    _towers[state.slot] = tower;
                }
                tower.range = state.range;
                if (tower.level != state.level || tower.hp != state.hp)
                {
                    tower.level = state.level;
                    tower.hp = state.hp;
                    string stars = state.level > 0 ? "  " + new string('★', Mathf.Min(5, state.level)) : "";
                    string hurt = state.hp < state.maxHp ? $"\n내구도 {state.hp} / {state.maxHp}" : "";
                    tower.label.SetText(StructureName(state.code) + stars + hurt);
                }
            }

            // 핑
            _gone.Clear();
            foreach (KeyValuePair<int, GameObject> pair in _pings)
            {
                bool alive = false;
                for (int i = 0; i < snapshot.pings.Count; i++)
                    if (snapshot.pings[i].id == pair.Key && !Link.IsHidden(snapshot.pings[i].pid)) alive = true;
                if (!alive) _gone.Add(pair.Key);
            }
            for (int i = 0; i < _gone.Count; i++)
            {
                Destroy(_pings[_gone[i]]);
                _pings.Remove(_gone[i]);
            }
            for (int i = 0; i < snapshot.pings.Count; i++)
            {
                CoopPingView ping = snapshot.pings[i];
                if (_pings.ContainsKey(ping.id) || Link.IsHidden(ping.pid)) continue;
                _pings[ping.id] = CreatePing(ping);
            }
        }

        private MateView CreateMate(CoopPlayerView player)
        {
            var root = new GameObject("Mate_" + player.pid);
            root.transform.SetParent(_actors, false);
            root.transform.position = new Vector3(player.x, 0f, player.z);
            BlockCharacter character = BlockCharacter.Create(root.transform, Appearance.FromSeed(player.pid));
            var animator = character.gameObject.AddComponent<BlockCharacterAnimator>();
            character.EquipBlaster();
            // NPC 대원은 회색 완장이다. 이름에도 [NPC] 가 붙어 온다.
            character.SetArmbandColor(player.npc ? Appearance.StrangerArmband : Appearance.HaeundaeColor);
            WorldLabel label = WorldLabel.Create(root.transform, new Vector3(0f, BlockCharacter.Height + 0.45f, 0f), player.name, 0.26f,
                player.npc ? new Color(0.80f, 0.84f, 0.88f) : Color.white, new Color(0f, 0f, 0f, 0.45f), 4.6f);
            return new MateView { root = root, animator = animator, label = label, target = root.transform.position };
        }

        private FoeView CreateFoe(CoopEnemyView enemy, GameSession session)
        {
            var root = new GameObject("Foe_" + enemy.id);
            root.transform.SetParent(_actors, false);
            root.transform.position = new Vector3(enemy.x, 0f, enemy.z);
            var view = new FoeView { root = root, target = root.transform.position };

            EnemyDef def = session != null ? session.Defs.FindEnemy(enemy.code) : null;
            if (def != null)
            {
                view.visual = EnemyVisual.Create(root.transform, def);
                view.height = view.visual.Height;
                view.debris = view.visual.DebrisColors;
            }
            else
            {
                // 이 클라이언트가 모르는 새 적: 이름 없는 상자로라도 보여 준다.
                Blocks.Box(root.transform, "Body", new Vector3(0f, 0.6f, 0f), new Vector3(0.9f, 1.2f, 0.9f), new Color(0.5f, 0.5f, 0.56f));
            }
            if (view.debris == null || view.debris.Length == 0) view.debris = new[] { new Color(0.5f, 0.5f, 0.56f), new Color(0.3f, 0.3f, 0.34f) };

            // 머리 위의 체력 막대
            Transform holder = Blocks.Pivot(root.transform, "HpBar", new Vector3(0f, view.height + 0.35f, 0f));
            holder.gameObject.AddComponent<FaceCamera>();
            float width = enemy.boss ? 1.8f : 1.0f;
            holder.localScale = new Vector3(width, 1f, 1f);
            Blocks.Box(holder, "Back", Vector3.zero, new Vector3(1.04f, 0.14f, 0.02f), new Color(0.08f, 0.08f, 0.10f), false, true);
            // 막대의 길이는 이 빈 오브젝트의 가로 배율로 바꾼다.
            view.bar = Blocks.Pivot(holder, "Fill", new Vector3(0f, 0f, -0.01f));
            Blocks.Box(view.bar, "Bar", Vector3.zero, new Vector3(1f, 0.10f, 0.02f), EnemyBar, false, true);
            return view;
        }

        private TowerView CreateTower(CoopTowerView state)
        {
            TowerDef def = StructureOf(state.code);
            CoopVec at = Map.slots[state.slot];
            var root = new GameObject("CoopTower_" + state.slot);
            root.transform.SetParent(_actors, false);
            root.transform.position = new Vector3(at.x, 0f, at.z);
            var view = new TowerView { root = root, code = state.code, range = state.range };

            string kind = def != null ? def.kind : StructureKinds.Gun;
            bool blast = def != null && def.splashRadius > 0f;
            bool laser = kind == StructureKinds.Laser;
            bool slow = kind == StructureKinds.Slow;
            bool emp = kind == StructureKinds.Emp;
            Color baseColor = new Color(0.30f, 0.33f, 0.38f);
            Color body = blast ? new Color(0.90f, 0.42f, 0.22f)
                : laser ? new Color(0.62f, 0.22f, 0.52f)
                : slow ? new Color(0.24f, 0.42f, 0.78f)
                : emp ? new Color(0.46f, 0.30f, 0.76f)
                : new Color(0.16f, 0.62f, 0.72f);
            view.trim = blast ? new Color(1f, 0.82f, 0.3f)
                : laser ? new Color(1f, 0.4f, 0.9f)
                : slow ? new Color(0.5f, 0.8f, 1f)
                : emp ? new Color(0.8f, 0.6f, 1f)
                : new Color(0.75f, 0.95f, 1f);
            view.debris = new[] { baseColor, body, view.trim };

            Blocks.Box(root.transform, "Base", new Vector3(0f, 0.2f, 0f), new Vector3(1.5f, 0.4f, 1.5f), baseColor, true);
            Blocks.Cylinder(root.transform, "Column", new Vector3(0f, 0.85f, 0f), new Vector3(0.7f, 0.9f, 0.7f), baseColor, true);
            Blocks.Box(root.transform, "Band", new Vector3(0f, 0.45f, 0f), new Vector3(1.52f, 0.08f, 1.52f), view.trim, false, true);
            view.head = Blocks.Pivot(root.transform, "Head", new Vector3(0f, 1.55f, 0f));
            if (slow || emp)
            {
                // 쏘지 않는 장치: 포신 대신 빛나는 구슬과 고리.
                Blocks.Cylinder(view.head, "Ring", new Vector3(0f, -0.1f, 0f), new Vector3(1.1f, 0.08f, 1.1f), body);
                Blocks.Sphere(view.head, "Orb", new Vector3(0f, 0.3f, 0f), new Vector3(0.6f, 0.6f, 0.6f), view.trim, false, true);
            }
            else
            {
                Blocks.Box(view.head, "Body", Vector3.zero, new Vector3(blast ? 0.95f : 0.7f, 0.5f, 0.85f), body);
                Blocks.Box(view.head, "Barrel", new Vector3(0f, 0f, 0.7f), new Vector3(blast ? 0.36f : 0.14f, blast ? 0.36f : 0.14f, 0.8f), baseColor);
                Blocks.Box(view.head, "Sight", new Vector3(0f, 0.3f, 0.1f), new Vector3(0.14f, 0.12f, 0.3f), view.trim, false, true);
                view.muzzle = Blocks.Pivot(view.head, "Muzzle", new Vector3(0f, 0f, 1.12f));
                // 쏘는 모습은 보여 주기일 뿐이다. 맞았는지는 서버가 정한다.
                view.fireEvery = def != null && def.shotsPerSecond > 0f ? 1f / def.shotsPerSecond : 0.5f;
            }
            view.label = WorldLabel.Create(root.transform, new Vector3(0f, 2.7f, 0f), StructureName(state.code), 0.24f, Color.white,
                new Color(0f, 0f, 0f, 0.4f), 3.6f);
            root.AddComponent<PopIn>();
            return view;
        }

        private GameObject CreatePing(CoopPingView ping)
        {
            Color color = PingColor(ping.kind);
            var root = new GameObject("Ping_" + ping.id);
            root.transform.SetParent(_actors, false);
            root.transform.position = new Vector3(ping.x, 0f, ping.z);
            Blocks.Cylinder(root.transform, "Ring", new Vector3(0f, 0.04f, 0f), new Vector3(2.6f, 0.05f, 2.6f), color, false, true);
            Blocks.Box(root.transform, "Beam", new Vector3(0f, 2.2f, 0f), new Vector3(0.12f, 4.4f, 0.12f), color, false, true);
            WorldLabel.Create(root.transform, new Vector3(0f, 4.9f, 0f), $"{PingKinds.DisplayName(ping.kind)}\n{NameOf(ping.pid)}", 0.34f, color,
                new Color(0f, 0f, 0f, 0.5f), 4.4f);
            Fx.Shockwave(root.transform.position, 2.2f, color);
            return root;
        }

        /// <summary>핑의 색. 미니맵도 같은 색을 쓴다.</summary>
        public static Color PingColor(string kind)
        {
            switch (kind)
            {
                case PingKinds.FocusFire: return new Color(1f, 0.38f, 0.30f);
                case PingKinds.DefendHere: return new Color(0.40f, 0.72f, 1f);
                case PingKinds.BuildHere: return new Color(0.45f, 0.95f, 0.85f);
                case PingKinds.RepairHere: return new Color(1f, 0.74f, 0.28f);
                case PingKinds.Danger: return new Color(1f, 0.90f, 0.25f);
                default: return new Color(0.70f, 1f, 0.50f);
            }
        }

        private void Animate(float dt)
        {
            float blend = 1f - Mathf.Exp(-10f * dt);
            foreach (MateView mate in _mates.Values)
            {
                Vector3 before = mate.root.transform.position;
                Vector3 after = Vector3.Lerp(before, mate.target, blend);
                mate.root.transform.position = after;
                Vector3 step = after - before;
                float speed = dt > 0f ? step.magnitude / dt : 0f;
                mate.animator.speed = speed;
                if (step.sqrMagnitude > 0.0001f)
                    mate.root.transform.rotation = Quaternion.RotateTowards(mate.root.transform.rotation,
                        Quaternion.LookRotation(step.normalized, Vector3.up), 720f * dt);
            }
            foreach (FoeView foe in _foes.Values)
            {
                Vector3 before = foe.root.transform.position;
                Vector3 after = Vector3.Lerp(before, foe.target, blend);
                foe.root.transform.position = after;
                if (foe.visual != null) foe.visual.speed = dt > 0f ? (after - before).magnitude / dt : 0f;
                foe.root.transform.rotation = Quaternion.RotateTowards(foe.root.transform.rotation,
                    Quaternion.LookRotation(foe.facing, Vector3.up), 540f * dt);
            }

            bool fighting = Snapshot != null && Snapshot.phase == CoopPhaseNames.Wave;
            foreach (TowerView tower in _towers.Values)
            {
                if (tower.muzzle == null || !fighting) continue;
                FoeView target = Nearest(tower.root.transform.position, tower.range, out _);
                if (target == null) continue;
                Vector3 aim = target.root.transform.position - tower.root.transform.position;
                aim.y = 0f;
                if (aim.sqrMagnitude > 0.01f)
                    tower.head.rotation = Quaternion.RotateTowards(tower.head.rotation, Quaternion.LookRotation(aim.normalized, Vector3.up), 360f * dt);
                tower.cooldown -= dt;
                if (tower.cooldown > 0f) continue;
                tower.cooldown = tower.fireEvery;
                Fx.Tracer(tower.muzzle.position, target.root.transform.position + Vector3.up * target.height * 0.5f, tower.trim);
            }

            // 내 사격: 서버는 사거리 안의 적(겨눈 적이 있으면 그 적)을 쏜다. 화면은 같은 적을 향해 쏘는 모습을 그린다.
            if (_player != null && fighting && InBattle)
            {
                _shotCooldown -= dt;
                if (_shotCooldown <= 0f)
                {
                    FoeView target = null;
                    Vector3 from = _player.transform.position;
                    if (FocusEnemyId != 0 && _foes.TryGetValue(FocusEnemyId, out FoeView focused)
                        && Flat(focused.root.transform.position - from).magnitude <= Link.PlayerRangeM) target = focused;
                    if (target == null) target = Nearest(from, Link.PlayerRangeM, out _);
                    if (target != null)
                    {
                        _shotCooldown = 0.34f;
                        if (_player.Animator != null) _player.Animator.TriggerShoot();
                        Fx.Tracer(from + Vector3.up * 1.0f, target.root.transform.position + Vector3.up * target.height * 0.5f, TracerColor);
                    }
                }
            }

            if (FocusEnemyId != 0 && _foes.TryGetValue(FocusEnemyId, out FoeView marked))
                _focusMarker.PointAt(marked.root.transform.position + Vector3.up * (marked.height + 0.9f));
            else _focusMarker.Hide();
        }

        private static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);

        private FoeView Nearest(Vector3 from, float range, out int id)
        {
            FoeView best = null;
            id = 0;
            float bestDistance = range * range;
            foreach (KeyValuePair<int, FoeView> pair in _foes)
            {
                float distance = Flat(pair.Value.root.transform.position - from).sqrMagnitude;
                if (distance > bestDistance) continue;
                bestDistance = distance;
                best = pair.Value;
                id = pair.Key;
            }
            return best;
        }

        // ── 입력 ────────────────────────────────────────────────

        private void HandleInput()
        {
            if (GameState.IsPaused || Leaving || Time.frameCount <= GameState.LastUnlockFrame) return;
            IPlayerInput input = PlayerInputHub.Current;

            if (TacticalOpen)
            {
                // 전술 지도가 맨 위에 있을 때만 닫는다 (핑을 고르는 메뉴가 떠 있으면 그 메뉴가 E 를 받는다).
                if (GameState.IsOnlyLockedBy(GameState.LockTacticalMap) && (input.MapPressed || input.InteractPressed))
                    SetTactical(false);
                return;
            }
            if (GameState.IsControlLocked) return;

            if (input.MapPressed)
            {
                SetTactical(true);
                return;
            }
            if (input.GadgetPressed && InBattle && _player != null)
            {
                OpenPingMenu(_player.transform.position.x, _player.transform.position.z, null);
                return;
            }
            if (input.SupportPressed)
            {
                OpenTeamMenu();
                return;
            }

            // 마우스 왼쪽: 보고 있는 쪽의 적을 겨눈다. 겨눈 적을 사거리 안에서 먼저 쏜다 (쏘는 것은 서버가 한다).
            bool held = input.AttackHeld;
            if (held && !_attackWasHeld && InBattle) FocusInView();
            _attackWasHeld = held;
        }

        private void FocusInView()
        {
            if (_player == null) return;
            Camera cam = Camera.main;
            Vector3 from = _player.transform.position;
            Vector3 look = cam != null ? Flat(cam.transform.forward) : _player.transform.forward;
            if (look.sqrMagnitude < 0.001f) look = _player.transform.forward;
            look.Normalize();

            int best = 0;
            float bestScore = float.MaxValue;
            float range = Link.PlayerRangeM;
            foreach (KeyValuePair<int, FoeView> pair in _foes)
            {
                Vector3 to = Flat(pair.Value.root.transform.position - from);
                float distance = to.magnitude;
                if (distance > range || distance < 0.01f) continue;
                float angle = Vector3.Angle(look, to);
                if (angle > 60f) continue;
                // 화면 가운데에 가깝고 가까운 적을 고른다.
                float score = angle + distance * 1.5f;
                if (score >= bestScore) continue;
                bestScore = score;
                best = pair.Key;
            }
            if (best == 0 || best == FocusEnemyId) return;
            FocusEnemyId = best;
            Link.Focus(best);
            Sfx.Play(Sfx.Id.Interact, 0.6f);
        }

        public void SetTactical(bool open)
        {
            if (TacticalOpen == open) return;
            TacticalOpen = open;
            if (open) GameState.Lock(GameState.LockTacticalMap);
            else GameState.Unlock(GameState.LockTacticalMap);
            Sfx.Play(Sfx.Id.Interact);
        }

        // ── 메뉴: 타워 자리 ─────────────────────────────────────

        public void OpenSlotMenu(int slot)
        {
            CoopSnapshot snapshot = Snapshot;
            if (snapshot == null || !CanAct) return;
            CoopTowerView tower = snapshot.FindTower(slot);
            if (tower == null) OpenBuildMenu(slot, snapshot);
            else OpenTowerMenu(slot, tower, snapshot);
        }

        private void OpenBuildMenu(int slot, CoopSnapshot snapshot)
        {
            var options = new List<ChoiceOption>();
            IReadOnlyList<CoopStructureInfo> all = Link.Structures;
            for (int i = 0; i < all.Count; i++)
            {
                CoopStructureInfo info = all[i];
                TowerDef def = info.def;
                int cost = ShownCost(def.energyCost);
                bool affordable = snapshot.energy >= cost;
                options.Add(new ChoiceOption
                {
                    label = def.name,
                    cost = $"예산 {cost}",
                    detail = !info.usable ? "아직 해금하지 않았어요. 작전 본부의 방어 시설 연구에서 해금해요."
                        : !affordable ? $"공동 예산이 모자라요. (지금 {snapshot.energy})"
                        : string.IsNullOrEmpty(def.description) ? $"사거리 {def.range:0.#} m" : def.description,
                    enabled = info.usable && affordable,
                    onChosen = () => Link.Build(slot, def.code),
                });
            }
            PagedMenu.Open("타워 세우기", $"공동 예산 {snapshot.energy}\n팀이 함께 쓰는 예산이에요. 같은 자리에는 먼저 요청한 대원의 타워만 서요.",
                options, null, MenuTag);
        }

        private void OpenTowerMenu(int slot, CoopTowerView tower, CoopSnapshot snapshot)
        {
            TowerDef def = StructureOf(tower.code);
            int upgradeCost = def != null ? ShownCost(def.upgradeEnergyCost) : 0;
            int maxLevel = def != null ? def.maxUpgradeLevel : 0;
            bool canUpgrade = def != null && tower.level < maxLevel;
            bool hurt = tower.hp < tower.maxHp;
            string name = def != null ? def.name : "타워";
            ChoiceMenu.Instance.Open(name,
                $"강화 {tower.level} / {maxLevel}   내구도 {tower.hp} / {tower.maxHp}   세운 대원 {NameOf(tower.owner)}\n공동 예산 {snapshot.energy}",
                new ChoiceOption
                {
                    label = "강화",
                    cost = canUpgrade ? $"예산 {upgradeCost}" : "최고 단계",
                    detail = !canUpgrade ? "더 강화할 수 없어요."
                        : snapshot.energy < upgradeCost ? $"공동 예산이 모자라요. (지금 {snapshot.energy})"
                        : "공격력과 사거리가 올라요.",
                    enabled = canUpgrade && snapshot.energy >= upgradeCost,
                    onChosen = () => Link.Upgrade(slot),
                },
                new ChoiceOption
                {
                    label = "수리",
                    cost = hurt ? $"예산 {Link.RepairEnergy}" : "정상",
                    detail = !hurt ? "고칠 곳이 없어요."
                        : snapshot.energy < Link.RepairEnergy ? $"공동 예산이 모자라요. (지금 {snapshot.energy})"
                        : $"타워 가까이({Link.RepairRangeM:0} m 안)에서 내구도를 되돌려요.",
                    enabled = hurt && snapshot.energy >= Link.RepairEnergy,
                    onChosen = () => Link.Repair(slot),
                });
            ChoiceMenu.Instance.Tag = MenuTag;
        }

        // ── 메뉴: 전술 핑 ───────────────────────────────────────

        public void OpenPingMenu(float x, float z, Action onBack)
        {
            if (!InBattle) return;
            PingRules rules = Link.PingRules;
            if (Link.PingBlocked)
            {
                GameEvents.ShowToast(CoopTexts.Reject(CoopRejects.PingBlocked));
                return;
            }
            if (rules != null && rules.enabled != 1)
            {
                GameEvents.ShowToast(CoopTexts.Reject(CoopRejects.PingDisabled));
                return;
            }
            var options = new ChoiceOption[PingKinds.All.Length];
            for (int i = 0; i < options.Length; i++)
            {
                string kind = PingKinds.All[i];
                options[i] = new ChoiceOption
                {
                    label = PingKinds.DisplayName(kind),
                    detail = PingHelp(kind),
                    onChosen = () => Link.Ping(kind, x, z),
                };
            }
            ChoiceMenu.Instance.Open("전술 핑", TacticalOpen
                ? "고른 곳에 표시를 남겨요. 모든 대원의 화면과 미니맵에 보여요."
                : "지금 서 있는 곳에 표시를 남겨요. 전술 지도(M)에서는 원하는 곳을 눌러 보낼 수 있어요.", options);
            ChoiceMenu.Instance.Tag = MenuTag;
            ChoiceMenu.Instance.OnCancelled = onBack;
        }

        private static string PingHelp(string kind)
        {
            switch (kind)
            {
                case PingKinds.FocusFire: return "여기의 적을 함께 공격해요.";
                case PingKinds.DefendHere: return "여기를 지켜 주세요. 긴급 지원 요청으로 표시돼요.";
                case PingKinds.BuildHere: return "여기에 타워를 세우면 좋겠어요.";
                case PingKinds.RepairHere: return "여기의 타워를 고쳐 주세요. 긴급 지원 요청으로 표시돼요.";
                case PingKinds.Danger: return "여기가 위험해요.";
                default: return "여기로 모여요.";
            }
        }

        // ── 메뉴: 팀 ────────────────────────────────────────────

        public void OpenTeamMenu()
        {
            CoopSnapshot snapshot = Snapshot;
            if (snapshot == null || !(InLobby || InBattle)) return;
            CoopPlayerView me = Me;
            PingRules rules = Link.PingRules;
            bool routeAllowed = rules == null || rules.routeOverlay == 1;
            bool rangesAllowed = rules == null || rules.rangeOverlay == 1;
            ChoiceMenu.Instance.Open("팀 메뉴", $"{Link.ZoneName}   대원 {snapshot.ConnectedCount} / {Link.MaxPlayers}명",
                new ChoiceOption
                {
                    label = "역할 고르기",
                    cost = CoopTexts.RoleName(Link.Roles, me != null ? me.role : null),
                    detail = "역할은 고르지 않아도 돼요. 언제든 바꿀 수 있어요.",
                    onChosen = OpenRoleMenu,
                },
                new ChoiceOption
                {
                    label = "적 이동 경로 표시",
                    cost = !routeAllowed ? "사용 안 함" : ShowRoute ? "켜짐" : "꺼짐",
                    detail = routeAllowed ? "전술 지도에 적이 지나가는 길을 그려요." : "운영 설정으로 꺼져 있어요.",
                    enabled = routeAllowed,
                    onChosen = () => { ShowRoute = !ShowRoute; OpenTeamMenu(); },
                },
                new ChoiceOption
                {
                    label = "타워 사거리 표시",
                    cost = !rangesAllowed ? "사용 안 함" : ShowRanges ? "켜짐" : "꺼짐",
                    detail = rangesAllowed ? "전술 지도에 타워가 닿는 범위를 그려요." : "운영 설정으로 꺼져 있어요.",
                    enabled = rangesAllowed,
                    onChosen = () => { ShowRanges = !ShowRanges; OpenTeamMenu(); },
                },
                new ChoiceOption
                {
                    label = "대원의 핑 가리기",
                    cost = "",
                    detail = "핑을 너무 자주 보내는 대원의 핑을 내 화면에서 가려요.",
                    enabled = snapshot.players.Count > 1,
                    onChosen = () => OpenHideMenu(0),
                },
                new ChoiceOption
                {
                    label = "방어전에서 나가기",
                    cost = "",
                    detail = Link.Practice ? "연습전을 그만두고 도시로 가요." : "내가 나가도 남은 대원들의 전투는 계속돼요.",
                    onChosen = AskLeave,
                });
            ChoiceMenu.Instance.Tag = MenuTag;
        }

        private void OpenRoleMenu()
        {
            CoopPlayerView me = Me;
            string mine = me != null ? me.role : null;
            var options = new List<ChoiceOption>();
            for (int i = 0; i < Link.Roles.Count; i++)
            {
                CoopRoleDef role = Link.Roles[i];
                options.Add(new ChoiceOption
                {
                    label = role.name,
                    cost = role.code == mine ? "맡는 중" : "",
                    detail = role.description,
                    enabled = role.code != mine,
                    onChosen = () => Link.SetRole(role.code),
                });
            }
            options.Add(new ChoiceOption
            {
                label = "역할 없음",
                cost = string.IsNullOrEmpty(mine) ? "지금" : "",
                detail = "역할 없이 자유롭게 움직여요.",
                enabled = !string.IsNullOrEmpty(mine),
                onChosen = () => Link.SetRole(null),
            });
            PagedMenu.Open("역할 고르기", "역할은 강제가 아니에요. 팀에 필요한 일을 골라 맡아 보세요.", options, OpenTeamMenu, MenuTag);
        }

        private void OpenHideMenu(int page)
        {
            CoopSnapshot snapshot = Snapshot;
            if (snapshot == null) return;
            var options = new List<ChoiceOption>();
            for (int i = 0; i < snapshot.players.Count; i++)
            {
                CoopPlayerView player = snapshot.players[i];
                if (player.pid == Link.MyPid) continue;
                string pid = player.pid;
                bool hidden = Link.IsHidden(pid);
                options.Add(new ChoiceOption
                {
                    label = player.name,
                    cost = hidden ? "가리는 중" : "보는 중",
                    detail = hidden ? "다시 보이게 해요." : "이 대원의 핑을 내 화면에서 가려요.",
                    onChosen = () =>
                    {
                        Link.HidePings(pid, !hidden);
                        GameEvents.ShowToast(hidden ? $"{player.name} 대원의 핑을 다시 봐요." : $"{player.name} 대원의 핑을 가렸어요.");
                    },
                });
            }
            PagedMenu.Open("대원의 핑 가리기", "가려도 그 대원에게는 알려지지 않아요.", options, OpenTeamMenu, MenuTag, page);
        }

        private void AskLeave()
        {
            bool running = InBattle;
            ChoiceMenu.Instance.Open("방어전에서 나갈까요?",
                Link.Practice ? "연습전을 그만두고 도시로 돌아가요."
                : running ? "지금 나가면 이번 방어전의 보상을 받지 못할 수 있어요.\n남은 대원들의 전투는 계속돼요."
                : "대기실에서 나가 도시로 돌아가요.",
                new ChoiceOption { label = "나가기", detail = "도시로 가요.", onChosen = Exit },
                new ChoiceOption { label = "계속하기", detail = "여기에 남아요.", onChosen = () => { } });
            ChoiceMenu.Instance.Tag = MenuTag;
        }

        // ── 신호기와 나가는 문 ──────────────────────────────────

        private void BuildSignal(Vector3 at)
        {
            Color dark = new Color(0.22f, 0.25f, 0.28f);
            Transform signal = Blocks.Pivot(_actors, "CoopSignal", at);
            Blocks.Box(signal, "Pole", new Vector3(0f, 1.2f, 0f), new Vector3(0.22f, 2.4f, 0.22f), dark, true);
            Blocks.Box(signal, "Box", new Vector3(0f, 1.3f, 0.2f), new Vector3(0.6f, 0.5f, 0.2f), new Color(0.95f, 0.75f, 0.2f));
            Blocks.Box(signal, "Button", new Vector3(0f, 1.3f, 0.32f), new Vector3(0.26f, 0.26f, 0.06f), new Color(1f, 0.3f, 0.25f), false, true);
            Blocks.Sphere(signal, "Lamp", new Vector3(0f, 2.6f, 0f), new Vector3(0.5f, 0.5f, 0.5f), new Color(1f, 0.4f, 0.3f), false, true);
            WorldLabel.Create(signal, new Vector3(0f, 3.2f, 0f), "신호기", 0.3f, new Color(1f, 0.86f, 0.3f));
            Interactable.Add(signal.gameObject, "방어전 시작", () => Link.Start(), 2.4f, () => InLobby && IsHost);
        }

        private void BuildExit(Vector3 at)
        {
            Transform gate = Blocks.Pivot(_actors, "CoopExit", at);
            Blocks.Box(gate, "Door", new Vector3(0f, 1.1f, 0f), new Vector3(1.6f, 2.2f, 0.12f), new Color(0.30f, 0.44f, 0.40f), true);
            Blocks.Box(gate, "Sign", new Vector3(0f, 2.6f, 0f), new Vector3(2.2f, 0.5f, 0.12f), new Color(0.16f, 0.2f, 0.3f));
            WorldLabel.Create(gate, new Vector3(0f, 3.3f, 0f), "도시로", 0.32f, Color.white);
            Interactable.Add(gate.gameObject, "도시로 돌아가기", AskLeave, 2.2f, () => InLobby || InBattle);
        }

        // ── 끝 ──────────────────────────────────────────────────

        private void UpdateFinished()
        {
            if (FinishedAt < 0f)
            {
                FinishedAt = Time.unscaledTime;
                if (TacticalOpen) SetTactical(false);
                ChoiceMenu.Instance.Close();
                GameState.Lock(GameState.LockResult);
                Sfx.Play(Link.Finish != null && Link.Finish.Won ? Sfx.Id.LevelUp : Sfx.Id.Hurt);
            }

            FetchRecord();

            if (Leaving || GameState.IsPaused || Time.unscaledTime - FinishedAt < ResultInputDelay) return;
            if (PlayerInputHub.Current.InteractPressed) Exit();
        }

        /// <summary>
        /// 온라인: 게임 서버가 결과를 받았다고 하면, 서버가 확정한 보상을 가져온다.
        /// 보상은 실시간 서버도 이 화면도 정하지 않는다.
        /// </summary>
        private void FetchRecord()
        {
            CoopFinish finish = Link.Finish;
            GameSession session = GameSession.Current;
            if (finish == null || finish.practice || session == null || string.IsNullOrEmpty(SessionId)) return;
            if (Record != null || !finish.recordKnown || !finish.recorded || _recordTries >= RecordMaxTries) return;
            if (_recordAsked) return;
            _recordWait -= Time.unscaledDeltaTime;
            if (_recordWait > 0f) return;

            _recordAsked = true;
            _recordTries++;
            session.Backend.GetCoopResult(SessionId, result =>
            {
                _recordAsked = false;
                _recordWait = RecordRetrySeconds;
                if (this == null || GameSession.Current != session) return;
                if (!result.ok || result.data == null || !result.data.finished) return;
                Record = result.data;
                // 방어전의 결과로 도시의 위험도가 바뀌었을 수 있다. 서버의 값으로 다시 맞춘다.
                CityRiskService.Refresh();
                // 보상으로 바뀐 GC·경험치·레벨을 서버 값으로 맞춘다.
                session.Backend.GetMe(me => { if (me.ok && GameSession.Current == session) session.ApplyMe(me.data); });
            });
        }

        private void UpdateClosed()
        {
            if (Leaving) return;
            if (_closedAt < 0f)
            {
                _closedAt = Time.unscaledTime;
                if (TacticalOpen) SetTactical(false);
                ChoiceMenu.Instance.Close();
                GameEvents.ShowToast(CoopTexts.Closed(Link.ClosedReason));
            }
            if (Time.unscaledTime - _closedAt >= CloseReturnDelay) Exit();
        }

        /// <summary>도시로 돌아간다.</summary>
        public void Exit()
        {
            if (Leaving) return;
            Leaving = true;
            if (TacticalOpen) SetTactical(false);
            GameState.Unlock(GameState.LockResult);
            if (Link.State != CoopLinkStates.Closed) Link.Leave();
            GameRoot.Instance.LoadScene(SceneNames.City, SpawnIds.DefenseGate);
        }
    }

    /// <summary>타워를 세울 수 있는 자리. 비어 있으면 세우고, 타워가 있으면 강화하거나 고친다.</summary>
    public sealed class CoopSlot : MonoBehaviour
    {
        private static readonly Color EmptyColor = new Color(0.45f, 0.95f, 0.85f);
        private static readonly Color UsedColor = new Color(0.24f, 0.36f, 0.40f);

        public int Index { get; private set; }

        private CoopBattle _battle;
        private Interactable _interact;
        private GameObject _pad;
        private Transform _plus;
        private bool _shownUsed;

        public static CoopSlot Create(Transform parent, int index, Vector3 position, CoopBattle battle)
        {
            var go = new GameObject("CoopSlot_" + index);
            go.transform.SetParent(parent, false);
            go.transform.position = position;
            var slot = go.AddComponent<CoopSlot>();
            slot.Index = index;
            slot._battle = battle;
            slot._pad = Blocks.Cylinder(go.transform, "Pad", new Vector3(0f, 0.05f, 0f), new Vector3(2.4f, 0.06f, 2.4f), EmptyColor, false, true);
            Blocks.Cylinder(go.transform, "PadRim", new Vector3(0f, 0.04f, 0f), new Vector3(2.8f, 0.04f, 2.8f), new Color(0.16f, 0.22f, 0.26f));
            slot._plus = Blocks.Pivot(go.transform, "Plus", new Vector3(0f, 1.2f, 0f));
            Blocks.Box(slot._plus, "H", Vector3.zero, new Vector3(0.7f, 0.2f, 0.12f), Color.white, false, true);
            Blocks.Box(slot._plus, "V", Vector3.zero, new Vector3(0.2f, 0.7f, 0.12f), Color.white, false, true);
            slot._interact = Interactable.Add(go, "타워 세우기", () => battle.OpenSlotMenu(index), 2.6f, () => battle.CanAct);
            return slot;
        }

        private void Update()
        {
            CoopSnapshot snapshot = _battle != null ? _battle.Snapshot : null;
            bool used = snapshot != null && snapshot.FindTower(Index) != null;
            if (used != _shownUsed)
            {
                _shownUsed = used;
                Blocks.SetColor(_pad, used ? UsedColor : EmptyColor, true);
                _plus.gameObject.SetActive(!used);
            }
            _interact.verb = used ? "타워 강화·수리" : "타워 세우기";
            if (!used) _plus.localPosition = new Vector3(0f, 1.2f + Mathf.Sin(Time.time * 2.4f + Index) * 0.12f, 0f);
        }
    }

    /// <summary>늘 카메라를 마주 본다 (적 머리 위의 체력 막대).</summary>
    public sealed class FaceCamera : MonoBehaviour
    {
        private void LateUpdate()
        {
            Camera cam = Camera.main;
            if (cam != null) transform.rotation = cam.transform.rotation;
        }
    }

    /// <summary>
    /// 협동 방어전의 모드 컨트롤러. 디펜스 모드와 같은 카메라(3인칭 ↔ 전략 시점)를 쓰고, 조작 안내를 맡는다.
    /// </summary>
    public sealed class CoopModeController : CombatModeController
    {
        public override string ModeCode => CombatModeCodes.Defense;

        public bool StrategyView => CameraKind == CameraKinds.Strategy;

        private void Start()
        {
            UseCamera(DefaultCamera);
            ShowHint();
        }

        private void OnDestroy()
        {
            GameEvents.SetKeyHint(null);
        }

        private void Update()
        {
            if (GameState.IsControlLocked || Time.frameCount <= GameState.LastUnlockFrame) return;
            if (!PlayerInputHub.Current.CameraTogglePressed) return;
            if (!UseCamera(StrategyView ? CameraKinds.ThirdPerson : CameraKinds.Strategy)) return;
            Sfx.Play(Sfx.Id.Interact);
            ShowHint();
        }

        private void ShowHint()
        {
            GameEvents.SetKeyHint(StrategyView
                ? "E  타워      Q  전술 핑      M  전술 지도      F  팀 메뉴      V  3인칭 시점"
                : "E  타워      Q  전술 핑      M  전술 지도      F  팀 메뉴      V  전략 시점      마우스 왼쪽  적 겨누기");
        }
    }
}
