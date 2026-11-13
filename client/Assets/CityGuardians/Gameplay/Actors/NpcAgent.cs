using System;
using CityGuardians.Services;
using UnityEngine;

namespace CityGuardians.Gameplay
{
    /// <summary>
    /// 대화할 수 있는 NPC (docs/NPC_SYSTEM.md). 이름표를 달고 있어 실제 플레이어와 구별된다.
    /// 무슨 말을 할지는 튜토리얼 단계에 따라 대화 데이터에서 고른다.
    /// </summary>
    public sealed class NpcAgent : MonoBehaviour
    {
        public string Code { get; private set; }
        public string DisplayName { get; private set; }
        public BlockCharacterAnimator Animator { get; private set; }
        /// <summary>대화 데이터의 action 이름을 실행하는 곳. 성공하면 넘겨받은 콜백을 부른다.</summary>
        public Action<string, Action> actionHandler;

        private WorldLabel _bubble;
        private float _bubbleUntil;
        private float _height;
        private Quaternion _homeRotation;

        /// <summary>사람 모습의 NPC.</summary>
        public static NpcAgent CreateHuman(Transform parent, string code, Vector3 position, float yaw,
            Appearance appearance, float scale = 1f)
        {
            NpcAgent npc = CreateBase(parent, code, position, yaw, BlockCharacter.Height * scale, 0.32f * scale);
            BlockCharacter character = BlockCharacter.Create(npc.transform, appearance);
            character.transform.localScale = Vector3.one * scale;
            npc.Animator = character.gameObject.AddComponent<BlockCharacterAnimator>();
            return npc;
        }

        /// <summary>겉모습을 따로 만들어 붙이는 NPC (안내 로봇 보리).</summary>
        public static NpcAgent CreateCustom(Transform parent, string code, Vector3 position, float yaw,
            float height, float radius)
        {
            return CreateBase(parent, code, position, yaw, height, radius);
        }

        private static NpcAgent CreateBase(Transform parent, string code, Vector3 position, float yaw,
            float height, float radius)
        {
            var go = new GameObject("Npc_" + code);
            go.layer = 2;
            go.transform.SetParent(parent, false);
            go.transform.SetPositionAndRotation(position, Quaternion.Euler(0f, yaw, 0f));

            var npc = go.AddComponent<NpcAgent>();
            npc.Code = code;
            npc.DisplayName = DialogueDb.NameOf(code);
            npc._height = height;
            npc._homeRotation = go.transform.rotation;

            var capsule = go.AddComponent<CapsuleCollider>();
            capsule.radius = radius;
            capsule.height = Mathf.Max(height, radius * 2f);
            capsule.center = new Vector3(0f, capsule.height * 0.5f, 0f);

            WorldLabel.Create(go.transform, new Vector3(0f, height + 0.28f, 0f), npc.DisplayName, 0.22f,
                new Color(0.75f, 1f, 0.8f));

            Interactable.Add(go, "말하기", npc.Talk, 2.4f, () => npc.CurrentEntry() != null);
            return npc;
        }

        /// <summary>지금 이 플레이어에게 할 말. 없으면 null.</summary>
        private DialogueEntry CurrentEntry()
        {
            GameSession session = GameSession.Current;
            return session == null ? null : DialogueDb.Find(Code, session.TutorialStep, session.Me.residency);
        }

        public void Talk()
        {
            GameSession session = GameSession.Current;
            if (session == null) return;
            DialogueEntry entry = CurrentEntry();
            if (entry == null) return;

            HideBubble();
            FacePlayerNow();
            DialogueRunner.Instance.Show(DisplayName, entry.lines, () => OnDialogueDone(entry), Animator);
        }

        private void OnDialogueDone(DialogueEntry entry)
        {
            Action advance = () =>
            {
                if (entry.setStep > 0 && GameSession.Current != null) GameSession.Current.AdvanceTutorial(entry.setStep);
            };

            if (string.IsNullOrEmpty(entry.action)) advance();
            else if (actionHandler != null) actionHandler(entry.action, advance);
            else Debug.LogWarning($"대화의 action '{entry.action}' 을 처리할 곳이 없습니다 ({Code}).");
        }

        /// <summary>머리 위에 말풍선을 잠깐 띄운다.</summary>
        public void Say(string text, float seconds = 4f)
        {
            if (_bubble == null)
            {
                _bubble = WorldLabel.Create(transform, new Vector3(0f, _height + 0.8f, 0f), text, 0.17f,
                    new Color(0.12f, 0.12f, 0.16f), new Color(1f, 1f, 1f, 0.95f));
            }
            _bubble.SetText(WorldLabel.WrapInTwo(text, 12));
            _bubble.FitWidthToText();
            _bubble.gameObject.SetActive(true);
            _bubbleUntil = Time.time + seconds;
        }

        public void HideBubble()
        {
            if (_bubble != null) _bubble.gameObject.SetActive(false);
        }

        private void Update()
        {
            if (_bubble != null && _bubble.gameObject.activeSelf && Time.time >= _bubbleUntil) HideBubble();

            // 플레이어가 가까이 오면 그쪽을 바라보고, 멀어지면 원래 방향으로 돌아간다.
            PlayerController player = PlayerController.Instance;
            Quaternion goal = _homeRotation;
            if (player != null)
            {
                Vector3 to = player.transform.position - transform.position;
                to.y = 0f;
                if (to.sqrMagnitude < 25f && to.sqrMagnitude > 0.01f) goal = Quaternion.LookRotation(to, Vector3.up);
            }
            transform.rotation = Quaternion.RotateTowards(transform.rotation, goal, 240f * Time.deltaTime);
        }

        private void FacePlayerNow()
        {
            PlayerController player = PlayerController.Instance;
            if (player == null) return;
            Vector3 to = player.transform.position - transform.position;
            to.y = 0f;
            if (to.sqrMagnitude > 0.01f) transform.rotation = Quaternion.LookRotation(to, Vector3.up);
        }
    }

    /// <summary>
    /// 안내 로봇 보리의 겉모습. 둥글고 작은 구형 로봇이며 바닥에서 살짝 떠 있다.
    /// 적 로봇과 헷갈리지 않도록 밝은 색과 웃는 얼굴을 쓴다.
    /// </summary>
    public sealed class BoriVisual : MonoBehaviour
    {
        public const float Height = 0.95f;
        private Transform _body;
        private Transform _armL;
        private Transform _armR;

        /// <summary>손짓하는 중인가.</summary>
        public bool waving;

        public static BoriVisual Create(Transform parent)
        {
            var root = new GameObject("Visual");
            root.transform.SetParent(parent, false);
            var visual = root.AddComponent<BoriVisual>();
            visual.Build();
            return visual;
        }

        private void Build()
        {
            Color shell = new Color(0.96f, 0.97f, 0.94f);
            Color accent = new Color(0.35f, 0.82f, 0.72f);
            Color face = new Color(0.14f, 0.18f, 0.24f);
            Color light = new Color(0.55f, 1f, 0.9f);

            _body = Blocks.Pivot(transform, "Body", new Vector3(0f, 0.25f, 0f));
            Blocks.Sphere(_body, "Base", new Vector3(0f, 0.20f, 0f), new Vector3(0.50f, 0.42f, 0.50f), shell);
            Blocks.Box(_body, "Belt", new Vector3(0f, 0.20f, 0f), new Vector3(0.53f, 0.06f, 0.53f), accent);
            Blocks.Box(_body, "Head", new Vector3(0f, 0.52f, 0f), new Vector3(0.46f, 0.32f, 0.38f), shell);
            Blocks.Box(_body, "Screen", new Vector3(0f, 0.52f, 0.192f), new Vector3(0.36f, 0.22f, 0.02f), face);
            Blocks.Box(_body, "EyeL", new Vector3(-0.09f, 0.55f, 0.205f), new Vector3(0.07f, 0.08f, 0.02f), light, false, true);
            Blocks.Box(_body, "EyeR", new Vector3(0.09f, 0.55f, 0.205f), new Vector3(0.07f, 0.08f, 0.02f), light, false, true);
            Blocks.Box(_body, "Smile", new Vector3(0f, 0.465f, 0.205f), new Vector3(0.12f, 0.025f, 0.02f), light, false, true);
            Blocks.Box(_body, "Antenna", new Vector3(0f, 0.73f, 0f), new Vector3(0.03f, 0.12f, 0.03f), accent);
            Blocks.Sphere(_body, "AntennaTip", new Vector3(0f, 0.81f, 0f), new Vector3(0.08f, 0.08f, 0.08f), light, false, true);

            _armL = Blocks.Pivot(_body, "ArmL", new Vector3(-0.27f, 0.30f, 0f));
            _armR = Blocks.Pivot(_body, "ArmR", new Vector3(0.27f, 0.30f, 0f));
            Blocks.Box(_armL, "Arm", new Vector3(0f, -0.10f, 0f), new Vector3(0.07f, 0.22f, 0.07f), accent);
            Blocks.Box(_armR, "Arm", new Vector3(0f, -0.10f, 0f), new Vector3(0.07f, 0.22f, 0.07f), accent);
            // 떠 있는 느낌을 주는 아래쪽 불빛
            Blocks.Cylinder(transform, "Glow", new Vector3(0f, 0.03f, 0f), new Vector3(0.34f, 0.02f, 0.34f),
                new Color(0.55f, 1f, 0.9f, 1f), false, true);
        }

        private void Update()
        {
            float t = Time.time;
            _body.localPosition = new Vector3(0f, 0.25f + Mathf.Sin(t * 2.2f) * 0.04f, 0f);
            _body.localRotation = Quaternion.Euler(0f, 0f, Mathf.Sin(t * 1.3f) * 3f);
            float sway = Mathf.Sin(t * 2.2f) * 6f;
            // 손짓: 오른팔을 옆으로 들어 올려 흔든다.
            _armR.localRotation = waving
                ? Quaternion.Euler(0f, 0f, 150f + Mathf.Sin(t * 9f) * 25f)
                : Quaternion.Euler(sway, 0f, 0f);
            _armL.localRotation = Quaternion.Euler(-sway, 0f, 0f);
        }
    }

    /// <summary>지금 가야 할 곳 위에 떠서 까딱거리는 노란 표시.</summary>
    public sealed class ObjectiveMarker : MonoBehaviour
    {
        private Transform _target;
        private bool _followsTarget;
        private Vector3 _offset;
        private Vector3 _fixedPoint;

        public static ObjectiveMarker Create()
        {
            var go = new GameObject("ObjectiveMarker");
            var marker = go.AddComponent<ObjectiveMarker>();
            Color yellow = new Color(1f, 0.86f, 0.2f);
            // 아래를 가리키는 화살 모양
            Blocks.Box(go.transform, "Stem", new Vector3(0f, 0.34f, 0f), new Vector3(0.14f, 0.36f, 0.14f), yellow, false, true);
            Blocks.Box(go.transform, "Tip", new Vector3(0f, 0.06f, 0f), new Vector3(0.26f, 0.26f, 0.26f), yellow, false, true)
                .transform.localRotation = Quaternion.Euler(45f, 0f, 45f);
            go.SetActive(false);
            return marker;
        }

        public void Point(Transform target, float height)
        {
            _target = target;
            _followsTarget = target != null;
            _offset = Vector3.up * height;
            gameObject.SetActive(target != null);
        }

        public void PointAt(Vector3 worldPoint)
        {
            _target = null;
            _followsTarget = false;
            _fixedPoint = worldPoint;
            gameObject.SetActive(true);
        }

        public void Hide()
        {
            _target = null;
            _followsTarget = false;
            gameObject.SetActive(false);
        }

        private void LateUpdate()
        {
            // 따라가던 대상이 사라졌으면 (쓰러진 로봇 등) 표시도 숨긴다.
            if (_followsTarget && _target == null) { Hide(); return; }
            Vector3 basePoint = _followsTarget ? _target.position + _offset : _fixedPoint;
            transform.position = basePoint + Vector3.up * (Mathf.Abs(Mathf.Sin(Time.time * 3.2f)) * 0.25f);
            transform.Rotate(0f, 120f * Time.deltaTime, 0f, Space.World);
        }
    }
}
