using CityGuardians.Domain;
using CityGuardians.Gameplay;
using CityGuardians.Services;
using UnityEngine;
using UnityEngine.UI;

namespace CityGuardians.UI
{
    /// <summary>
    /// 오른쪽 아래의 빠른 칸: 1 근접 무기, 2 원거리 무기, 3 수리 키트 (docs/ITEM_SYSTEM.md 4~5장).
    /// 어떤 숫자 키가 무엇인지 늘 보여 준다. 원거리 무기나 수리 키트가 생기기 전에는 보이지 않는다
    /// (배턴 하나뿐일 때는 고를 것이 없다).
    /// </summary>
    public sealed class Hotbar : MonoBehaviour
    {
        private const int Rows = 3;
        private const float RowWidth = 330f;
        private const float RowHeight = 56f;
        private const float RowGap = 8f;

        private static readonly Color ActiveBack = new Color(0.34f, 0.28f, 0.08f, 0.92f);
        private static readonly Color Dim = new Color(0.72f, 0.74f, 0.78f);

        public bool Visible => _root.activeSelf;
        /// <summary>지금 손에 든 무기 칸의 이름. 보이지 않으면 null.</summary>
        public string ActiveName { get; private set; }
        /// <summary>수리 키트 칸의 글자 ("수리 키트  × 2"). 칸이 없으면 null.</summary>
        public string KitText => Visible && _rows[2].activeSelf ? _names[2].text + "  " + _counts[2].text : null;
        public bool RangedRowVisible => Visible && _rows[1].activeSelf;

        private GameObject _root;
        private readonly GameObject[] _rows = new GameObject[Rows];
        private readonly Image[] _backs = new Image[Rows];
        private readonly Image[] _keys = new Image[Rows];
        private readonly Text[] _names = new Text[Rows];
        private readonly Text[] _counts = new Text[Rows];

        public static Hotbar Create(Transform root)
        {
            RectTransform rect = UiFactory.Rect(root, "Hotbar", new Vector2(1f, 0f), new Vector2(-32f, 28f),
                new Vector2(RowWidth, Rows * (RowHeight + RowGap)));
            var bar = rect.gameObject.AddComponent<Hotbar>();
            bar._root = rect.gameObject;
            bar.Build(rect);
            rect.gameObject.SetActive(false);
            return bar;
        }

        private void Build(Transform root)
        {
            for (int i = 0; i < Rows; i++)
            {
                Image row = UiFactory.Panel(root, "Slot" + (i + 1), new Vector2(1f, 0f), Vector2.zero,
                    new Vector2(RowWidth, RowHeight), UiFactory.PanelColor);
                _rows[i] = row.gameObject;
                _backs[i] = row;
                Image key = UiFactory.Panel(row.transform, "Key", new Vector2(0f, 0.5f), new Vector2(8f, 0f),
                    new Vector2(40f, 40f), UiFactory.Accent);
                _keys[i] = key;
                UiFactory.Label(key.transform, "Number", 26, TextAnchor.MiddleCenter, new Color(0.1f, 0.1f, 0.14f)).text =
                    (i + 1).ToString();
                _names[i] = UiFactory.LabelAt(row.transform, "Name", new Vector2(0f, 0.5f), new Vector2(60f, 0f),
                    new Vector2(190f, 44f), 24, TextAnchor.MiddleLeft, UiFactory.TextColor);
                _names[i].horizontalOverflow = HorizontalWrapMode.Overflow;
                _counts[i] = UiFactory.LabelAt(row.transform, "Count", new Vector2(1f, 0.5f), new Vector2(-14f, 0f),
                    new Vector2(90f, 44f), 24, TextAnchor.MiddleRight, UiFactory.Accent);
            }
        }

        /// <summary>Hud가 매 프레임 부른다.</summary>
        public void Refresh()
        {
            GameSession session = GameSession.Current;
            bool show = false;
            WeaponDef ranged = null;
            int kits = 0;
            // FPS 모드에서는 그 모드의 장비 칸(CombatHud)이 이 자리를 쓴다.
            if (session != null && session.TutorialStep >= 3 && !GameState.IsControlLocked && !CombatService.IsFps)
            {
                ranged = session.EquippedRanged;
                kits = session.CountOf(ItemCodes.RepairKit);
                show = ranged != null || kits > 0;
            }

            if (_root.activeSelf != show) _root.SetActive(show);
            if (!show)
            {
                ActiveName = null;
                return;
            }

            bool rangedActive = session.ActiveSlot == EquipSlots.Ranged;
            WeaponDef melee = session.EquippedMelee;
            ItemDef kit = session.Defs.FindItem(ItemCodes.RepairKit);
            ActiveName = rangedActive ? ranged.name : melee != null ? melee.name : null;

            // 아래에서부터 쌓는다: 맨 아래가 가장 큰 번호.
            float y = 0f;
            y = Place(2, kits > 0, kit != null ? kit.name : "", $"× {kits}", false, y);
            y = Place(1, ranged != null, ranged != null ? ranged.name : "", "", rangedActive, y);
            Place(0, true, melee != null ? melee.name : "", "", !rangedActive, y);
        }

        private float Place(int index, bool used, string name, string count, bool active, float y)
        {
            if (_rows[index].activeSelf != used) _rows[index].SetActive(used);
            if (!used) return y;

            ((RectTransform)_rows[index].transform).anchoredPosition = new Vector2(0f, y);
            _backs[index].color = active ? ActiveBack : UiFactory.PanelColor;
            _keys[index].color = active || index == 2 ? UiFactory.Accent : Dim;
            _names[index].text = name;
            _names[index].color = active ? UiFactory.Accent : UiFactory.TextColor;
            _counts[index].text = count;
            return y + RowHeight + RowGap;
        }
    }
}
