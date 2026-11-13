using System;
using UnityEngine;

namespace CityGuardians.Gameplay
{
    /// <summary>캐릭터 외형. 부품과 색의 묶음 (docs/CHARACTER_SYSTEM.md 8장).</summary>
    [Serializable]
    public struct Appearance
    {
        public Color skin;
        public Color hair;
        public Color top;
        public Color bottom;
        public Color shoes;
        /// <summary>완장 색. 이방인은 무늬 없는 회색.</summary>
        public Color armband;
        public int hairStyle;

        private static readonly Color[] Skins =
        {
            new Color(1.00f, 0.86f, 0.72f), new Color(0.96f, 0.78f, 0.62f),
            new Color(0.85f, 0.65f, 0.48f), new Color(0.62f, 0.44f, 0.32f),
        };

        private static readonly Color[] Hairs =
        {
            new Color(0.16f, 0.12f, 0.10f), new Color(0.35f, 0.22f, 0.12f),
            new Color(0.10f, 0.10f, 0.14f), new Color(0.55f, 0.35f, 0.15f),
        };

        private static readonly Color[] Tops =
        {
            new Color(0.20f, 0.55f, 0.90f), new Color(0.95f, 0.45f, 0.25f), new Color(0.30f, 0.75f, 0.45f),
            new Color(0.95f, 0.75f, 0.20f), new Color(0.65f, 0.40f, 0.85f), new Color(0.90f, 0.35f, 0.50f),
        };

        private static readonly Color[] Bottoms =
        {
            new Color(0.25f, 0.28f, 0.38f), new Color(0.35f, 0.30f, 0.25f), new Color(0.20f, 0.35f, 0.45f),
        };

        public static readonly Color StrangerArmband = new Color(0.62f, 0.64f, 0.68f);
        /// <summary>해운대구의 지역 색 (바다색).</summary>
        public static readonly Color HaeundaeColor = new Color(0.10f, 0.62f, 0.82f);

        /// <summary>
        /// 같은 씨앗에서는 항상 같은 모습이 나온다. 처음 접속한 플레이어의 자동 생성에 쓴다.
        /// 실제 서버에서는 서버가 조합을 정해 내려 준다.
        /// </summary>
        public static Appearance FromSeed(string seed)
        {
            int hash = 17;
            if (seed != null)
                for (int i = 0; i < seed.Length; i++) hash = unchecked(hash * 31 + seed[i]);
            var rng = new System.Random(hash);
            return new Appearance
            {
                skin = Skins[rng.Next(Skins.Length)],
                hair = Hairs[rng.Next(Hairs.Length)],
                top = Tops[rng.Next(Tops.Length)],
                bottom = Bottoms[rng.Next(Bottoms.Length)],
                shoes = new Color(0.20f, 0.20f, 0.22f),
                armband = StrangerArmband,
                hairStyle = rng.Next(3),
            };
        }
    }

    /// <summary>
    /// 블록형 캐릭터의 몸. 머리가 크고 몸통이 짧은 약 2.5등신이며, 부위마다 독립된 블록이다.
    /// 플레이어와 사람 NPC가 같은 뼈대를 쓴다.
    /// </summary>
    public sealed class BlockCharacter : MonoBehaviour
    {
        public const float Height = 1.4f;

        public Transform Body { get; private set; }
        public Transform Head { get; private set; }
        public Transform ShoulderL { get; private set; }
        public Transform ShoulderR { get; private set; }
        public Transform HipL { get; private set; }
        public Transform HipR { get; private set; }
        public Transform WeaponSocket { get; private set; }
        public Appearance Appearance { get; private set; }

        /// <summary>머리와 몸에 입은 것의 모습 코드 (ItemDef.look). 입지 않았으면 null.</summary>
        public string HeadLook { get; private set; }
        public string BodyLook { get; private set; }

        private GameObject _armband;
        private GameObject _weapon;
        private GameObject _mouth;
        private GameObject _torso;
        private GameObject _hem;
        private GameObject _armL;
        private GameObject _armR;
        private GameObject _headWear;
        private GameObject _bodyWear;
        private GameObject _sleeveL;
        private GameObject _sleeveR;

        public static BlockCharacter Create(Transform parent, Appearance appearance)
        {
            var root = new GameObject("Visual");
            root.transform.SetParent(parent, false);
            var character = root.AddComponent<BlockCharacter>();
            character.Build(appearance);
            return character;
        }

        private void Build(Appearance look)
        {
            Appearance = look;
            Body = Blocks.Pivot(transform, "Body", Vector3.zero);
            Color eye = new Color(0.12f, 0.12f, 0.16f);

            // 다리: 골반에서 회전
            HipL = Blocks.Pivot(Body, "HipL", new Vector3(-0.11f, 0.40f, 0f));
            HipR = Blocks.Pivot(Body, "HipR", new Vector3(0.11f, 0.40f, 0f));
            Blocks.Box(HipL, "LegL", new Vector3(0f, -0.17f, 0f), new Vector3(0.18f, 0.34f, 0.20f), look.bottom);
            Blocks.Box(HipR, "LegR", new Vector3(0f, -0.17f, 0f), new Vector3(0.18f, 0.34f, 0.20f), look.bottom);
            Blocks.Box(HipL, "ShoeL", new Vector3(0f, -0.365f, 0.02f), new Vector3(0.20f, 0.07f, 0.26f), look.shoes);
            Blocks.Box(HipR, "ShoeR", new Vector3(0f, -0.365f, 0.02f), new Vector3(0.20f, 0.07f, 0.26f), look.shoes);

            // 몸통: 아래가 살짝 넓다
            _torso = Blocks.Box(Body, "Torso", new Vector3(0f, 0.63f, 0f), new Vector3(0.44f, 0.38f, 0.26f), look.top);
            _hem = Blocks.Box(Body, "Hem", new Vector3(0f, 0.445f, 0f), new Vector3(0.50f, 0.09f, 0.30f), look.top);

            // 머리: 크게
            Head = Blocks.Pivot(Body, "Head", new Vector3(0f, 0.84f, 0f));
            Blocks.Box(Head, "Skull", new Vector3(0f, 0.27f, 0f), new Vector3(0.56f, 0.50f, 0.50f), look.skin);
            Blocks.Box(Head, "EyeL", new Vector3(-0.13f, 0.30f, 0.251f), new Vector3(0.08f, 0.11f, 0.02f), eye, false, true);
            Blocks.Box(Head, "EyeR", new Vector3(0.13f, 0.30f, 0.251f), new Vector3(0.08f, 0.11f, 0.02f), eye, false, true);
            _mouth = Blocks.Box(Head, "Mouth", new Vector3(0f, 0.14f, 0.251f), new Vector3(0.14f, 0.035f, 0.02f),
                new Color(0.55f, 0.25f, 0.25f), false, true);
            Blocks.Box(Head, "CheekL", new Vector3(-0.21f, 0.19f, 0.251f), new Vector3(0.07f, 0.04f, 0.02f),
                new Color(1f, 0.62f, 0.62f), false, true);
            Blocks.Box(Head, "CheekR", new Vector3(0.21f, 0.19f, 0.251f), new Vector3(0.07f, 0.04f, 0.02f),
                new Color(1f, 0.62f, 0.62f), false, true);
            BuildHair(look);

            // 팔: 어깨에서 회전. 손은 손가락 없는 둥근 장갑 모양
            ShoulderL = Blocks.Pivot(Body, "ShoulderL", new Vector3(-0.30f, 0.79f, 0f));
            ShoulderR = Blocks.Pivot(Body, "ShoulderR", new Vector3(0.30f, 0.79f, 0f));
            _armL = Blocks.Box(ShoulderL, "ArmL", new Vector3(0f, -0.14f, 0f), new Vector3(0.13f, 0.30f, 0.15f), look.top);
            _armR = Blocks.Box(ShoulderR, "ArmR", new Vector3(0f, -0.14f, 0f), new Vector3(0.13f, 0.30f, 0.15f), look.top);
            Blocks.Sphere(ShoulderL, "HandL", new Vector3(0f, -0.34f, 0f), new Vector3(0.17f, 0.17f, 0.17f), look.skin);
            Blocks.Sphere(ShoulderR, "HandR", new Vector3(0f, -0.34f, 0f), new Vector3(0.17f, 0.17f, 0.17f), look.skin);
            WeaponSocket = Blocks.Pivot(ShoulderR, "WeaponSocket", new Vector3(0f, -0.36f, 0.04f));

            // 가디언 표식: 왼팔의 완장
            _armband = Blocks.Box(ShoulderL, "Armband", new Vector3(0f, -0.10f, 0f),
                new Vector3(0.15f, 0.09f, 0.17f), look.armband);
        }

        private void BuildHair(Appearance look)
        {
            // 윗머리는 공통, 모양에 따라 앞머리·옆머리가 달라진다.
            Blocks.Box(Head, "HairTop", new Vector3(0f, 0.545f, -0.01f), new Vector3(0.60f, 0.10f, 0.54f), look.hair);
            Blocks.Box(Head, "HairBack", new Vector3(0f, 0.36f, -0.265f), new Vector3(0.60f, 0.36f, 0.06f), look.hair);
            switch (look.hairStyle)
            {
                case 0: // 짧은 앞머리
                    Blocks.Box(Head, "Fringe", new Vector3(0f, 0.475f, 0.26f), new Vector3(0.56f, 0.08f, 0.04f), look.hair);
                    break;
                case 1: // 옆머리
                    Blocks.Box(Head, "SideL", new Vector3(-0.29f, 0.33f, 0f), new Vector3(0.05f, 0.40f, 0.48f), look.hair);
                    Blocks.Box(Head, "SideR", new Vector3(0.29f, 0.33f, 0f), new Vector3(0.05f, 0.40f, 0.48f), look.hair);
                    Blocks.Box(Head, "Fringe", new Vector3(0f, 0.485f, 0.26f), new Vector3(0.56f, 0.06f, 0.04f), look.hair);
                    break;
                default: // 삐죽 머리
                    Blocks.Box(Head, "Spike", new Vector3(0.08f, 0.64f, 0.05f), new Vector3(0.16f, 0.12f, 0.16f), look.hair);
                    Blocks.Box(Head, "Fringe", new Vector3(-0.10f, 0.47f, 0.26f), new Vector3(0.30f, 0.09f, 0.04f), look.hair);
                    break;
            }
        }

        public void SetArmbandColor(Color color)
        {
            if (_armband != null) Blocks.SetColor(_armband, color);
        }

        // ── 입는 것 (docs/CHARACTER_SYSTEM.md 8장) ──────────────

        /// <summary>
        /// 머리와 몸에 입은 것의 모습을 맞춘다. 모습 코드는 아이템 정의의 look이고, 비어 있으면 벗은 모습이다.
        /// 모르는 코드는 벗은 모습으로 둔다. 바뀐 것이 있으면 true (부품이 바뀌었으니 피격 깜빡임 등을 새로 맞춰야 한다).
        /// </summary>
        public bool Wear(string headLook, string bodyLook)
        {
            headLook = string.IsNullOrEmpty(headLook) ? null : headLook;
            bodyLook = string.IsNullOrEmpty(bodyLook) ? null : bodyLook;
            if (headLook == HeadLook && bodyLook == BodyLook) return false;

            if (headLook != HeadLook)
            {
                HeadLook = headLook;
                // 바로 없앤다: 같은 프레임에 깜빡임이 부품을 다시 모을 때 사라질 부품이 섞이지 않게 한다.
                if (_headWear != null) DestroyImmediate(_headWear);
                _headWear = BuildHeadWear(headLook);
            }
            if (bodyLook != BodyLook)
            {
                BodyLook = bodyLook;
                if (_bodyWear != null) DestroyImmediate(_bodyWear);
                if (_sleeveL != null) DestroyImmediate(_sleeveL);
                if (_sleeveR != null) DestroyImmediate(_sleeveR);
                BuildBodyWear(bodyLook);
            }
            return true;
        }

        private GameObject BuildHeadWear(string look)
        {
            if (look == null) return null;
            Transform wear = Blocks.Pivot(Head, "HeadWear", Vector3.zero);
            switch (look)
            {
                case "guard_helmet":
                {
                    Color shell = new Color(0.30f, 0.42f, 0.62f);
                    Color trim = new Color(0.20f, 0.28f, 0.44f);
                    Blocks.Box(wear, "Band", new Vector3(0f, 0.465f, -0.01f), new Vector3(0.66f, 0.19f, 0.60f), shell);
                    Blocks.Box(wear, "Dome", new Vector3(0f, 0.63f, -0.01f), new Vector3(0.62f, 0.18f, 0.56f), shell);
                    Blocks.Box(wear, "Crest", new Vector3(0f, 0.735f, -0.01f), new Vector3(0.10f, 0.05f, 0.40f), new Color(1f, 0.82f, 0.25f));
                    Blocks.Box(wear, "Visor", new Vector3(0f, 0.43f, 0.295f), new Vector3(0.50f, 0.05f, 0.02f),
                        new Color(0.35f, 0.95f, 1f), false, true);
                    Blocks.Box(wear, "EarL", new Vector3(-0.31f, 0.28f, -0.02f), new Vector3(0.05f, 0.20f, 0.26f), trim);
                    Blocks.Box(wear, "EarR", new Vector3(0.31f, 0.28f, -0.02f), new Vector3(0.05f, 0.20f, 0.26f), trim);
                    break;
                }
                case "wave_cap":
                {
                    Color cap = new Color(0.10f, 0.62f, 0.82f);
                    Blocks.Box(wear, "Crown", new Vector3(0f, 0.61f, -0.02f), new Vector3(0.63f, 0.21f, 0.57f), cap);
                    Blocks.Box(wear, "Brim", new Vector3(0f, 0.525f, 0.36f), new Vector3(0.50f, 0.04f, 0.24f), new Color(0.06f, 0.42f, 0.60f));
                    Blocks.Box(wear, "Wave", new Vector3(0f, 0.60f, 0.267f), new Vector3(0.40f, 0.05f, 0.01f), Color.white, false, true);
                    Blocks.Box(wear, "Button", new Vector3(0f, 0.73f, -0.02f), new Vector3(0.10f, 0.03f, 0.10f), Color.white);
                    break;
                }
                case "bunny_band":
                {
                    Color pink = new Color(1f, 0.62f, 0.75f);
                    Blocks.Box(wear, "Band", new Vector3(0f, 0.615f, 0f), new Vector3(0.63f, 0.05f, 0.10f), pink);
                    foreach (float x in new[] { -0.15f, 0.15f })
                    {
                        Blocks.Box(wear, "Ear", new Vector3(x, 0.82f, 0f), new Vector3(0.11f, 0.38f, 0.07f), Color.white);
                        Blocks.Box(wear, "EarInner", new Vector3(x, 0.81f, 0.037f), new Vector3(0.05f, 0.26f, 0.01f), pink, false, true);
                    }
                    break;
                }
                default:
                    Debug.LogWarning($"모르는 머리 모습: {look}");
                    break;
            }
            return wear.gameObject;
        }

        private void BuildBodyWear(string look)
        {
            Color top = Appearance.top;
            Color arms = Appearance.top;
            _bodyWear = null;
            _sleeveL = null;
            _sleeveR = null;

            if (look != null)
            {
                Transform wear = Blocks.Pivot(Body, "BodyWear", Vector3.zero);
                _bodyWear = wear.gameObject;
                switch (look)
                {
                    case "guard_vest":
                    {
                        Color vest = new Color(0.16f, 0.22f, 0.36f);
                        Color plate = new Color(0.55f, 0.62f, 0.72f);
                        Blocks.Box(wear, "Vest", new Vector3(0f, 0.645f, 0f), new Vector3(0.48f, 0.33f, 0.31f), vest);
                        Blocks.Box(wear, "Plate", new Vector3(0f, 0.66f, 0.158f), new Vector3(0.30f, 0.20f, 0.02f), plate);
                        Blocks.Box(wear, "Emblem", new Vector3(0f, 0.67f, 0.17f), new Vector3(0.10f, 0.10f, 0.01f),
                            new Color(0.35f, 0.95f, 1f), false, true);
                        Blocks.Box(wear, "Belt", new Vector3(0f, 0.47f, 0f), new Vector3(0.52f, 0.05f, 0.32f), new Color(0.10f, 0.12f, 0.18f));
                        _sleeveL = Blocks.Box(ShoulderL, "PadL", new Vector3(-0.01f, 0.02f, 0f), new Vector3(0.18f, 0.08f, 0.20f), plate);
                        _sleeveR = Blocks.Box(ShoulderR, "PadR", new Vector3(0.01f, 0.02f, 0f), new Vector3(0.18f, 0.08f, 0.20f), plate);
                        break;
                    }
                    case "sunny_hoodie":
                    {
                        top = new Color(1f, 0.80f, 0.22f);
                        arms = top;
                        Color shade = new Color(0.90f, 0.66f, 0.14f);
                        Blocks.Box(wear, "Hood", new Vector3(0f, 0.84f, -0.17f), new Vector3(0.38f, 0.16f, 0.10f), shade);
                        Blocks.Box(wear, "Pocket", new Vector3(0f, 0.53f, 0.133f), new Vector3(0.24f, 0.10f, 0.01f), shade);
                        Blocks.Box(wear, "StringL", new Vector3(-0.05f, 0.74f, 0.133f), new Vector3(0.02f, 0.12f, 0.01f), Color.white, false, true);
                        Blocks.Box(wear, "StringR", new Vector3(0.05f, 0.74f, 0.133f), new Vector3(0.02f, 0.12f, 0.01f), Color.white, false, true);
                        break;
                    }
                    case "star_tee":
                    {
                        top = new Color(0.52f, 0.36f, 0.86f);
                        arms = Appearance.skin; // 반소매
                        Color star = new Color(1f, 0.86f, 0.2f);
                        GameObject a = Blocks.Box(wear, "StarA", new Vector3(0f, 0.65f, 0.133f), new Vector3(0.20f, 0.07f, 0.01f), star, false, true);
                        GameObject b = Blocks.Box(wear, "StarB", new Vector3(0f, 0.65f, 0.134f), new Vector3(0.20f, 0.07f, 0.01f), star, false, true);
                        GameObject c = Blocks.Box(wear, "StarC", new Vector3(0f, 0.65f, 0.135f), new Vector3(0.20f, 0.07f, 0.01f), star, false, true);
                        a.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
                        b.transform.localRotation = Quaternion.Euler(0f, 0f, 30f);
                        c.transform.localRotation = Quaternion.Euler(0f, 0f, -30f);
                        _sleeveL = Blocks.Box(ShoulderL, "SleeveL", new Vector3(0f, -0.04f, 0f), new Vector3(0.15f, 0.12f, 0.17f), top);
                        _sleeveR = Blocks.Box(ShoulderR, "SleeveR", new Vector3(0f, -0.04f, 0f), new Vector3(0.15f, 0.12f, 0.17f), top);
                        break;
                    }
                    default:
                        Debug.LogWarning($"모르는 몸 모습: {look}");
                        break;
                }
            }

            Blocks.SetColor(_torso, top);
            Blocks.SetColor(_hem, top);
            Blocks.SetColor(_armL, arms);
            Blocks.SetColor(_armR, arms);
        }

        /// <summary>입 모양으로 간단한 표정을 낸다.</summary>
        public void SetMouthOpen(bool open)
        {
            if (_mouth == null) return;
            _mouth.transform.localScale = open ? new Vector3(0.12f, 0.09f, 0.02f) : new Vector3(0.14f, 0.035f, 0.02f);
        }

        /// <summary>원거리 무기(볼트 블래스터)를 오른손에 붙인다. 팔을 앞으로 들면 총구가 앞을 향한다.</summary>
        public void EquipBlaster()
        {
            if (_weapon != null) Destroy(_weapon);
            _weapon = new GameObject("Blaster");
            _weapon.transform.SetParent(WeaponSocket, false);
            Color shell = new Color(0.92f, 0.93f, 0.95f);
            Color dark = new Color(0.20f, 0.22f, 0.28f);
            Color glow = new Color(0.35f, 0.95f, 1f);
            // 총몸은 팔이 뻗는 방향(아래쪽 = 팔을 들었을 때의 앞쪽)으로 길다.
            Blocks.Box(_weapon.transform, "Grip", new Vector3(0f, 0.03f, -0.06f), new Vector3(0.07f, 0.12f, 0.08f), dark);
            Blocks.Box(_weapon.transform, "Body", new Vector3(0f, -0.10f, 0f), new Vector3(0.10f, 0.30f, 0.12f), shell);
            Blocks.Box(_weapon.transform, "Stripe", new Vector3(0f, -0.10f, 0.062f), new Vector3(0.04f, 0.22f, 0.01f), glow, false, true);
            Blocks.Box(_weapon.transform, "Barrel", new Vector3(0f, -0.29f, 0f), new Vector3(0.06f, 0.10f, 0.06f), dark);
            Blocks.Box(_weapon.transform, "Muzzle", new Vector3(0f, -0.35f, 0f), new Vector3(0.07f, 0.03f, 0.07f), glow, false, true);
        }

        /// <summary>근접 무기(스파크 배턴)를 오른손에 붙인다.</summary>
        public void EquipBaton()
        {
            if (_weapon != null) Destroy(_weapon);
            _weapon = new GameObject("Baton");
            _weapon.transform.SetParent(WeaponSocket, false);
            // 팔을 내렸을 때 앞쪽을 향해 뻗는다.
            _weapon.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            Blocks.Box(_weapon.transform, "Grip", new Vector3(0f, 0.05f, 0f), new Vector3(0.06f, 0.18f, 0.06f),
                new Color(0.20f, 0.20f, 0.24f));
            Blocks.Box(_weapon.transform, "Rod", new Vector3(0f, 0.36f, 0f), new Vector3(0.05f, 0.46f, 0.05f),
                new Color(0.75f, 0.78f, 0.82f));
            Blocks.Box(_weapon.transform, "Tip", new Vector3(0f, 0.62f, 0f), new Vector3(0.08f, 0.10f, 0.08f),
                new Color(0.35f, 0.95f, 1f), false, true);
        }
    }
}
