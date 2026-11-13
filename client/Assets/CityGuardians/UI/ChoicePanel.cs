using CityGuardians.Gameplay;
using UnityEngine;
using UnityEngine.UI;

namespace CityGuardians.UI
{
    /// <summary>
    /// 선택 메뉴 화면. ChoiceMenu(게임플레이)의 상태를 읽어 그리기만 한다.
    /// 숫자 키로 고르므로 누를 수 있는 버튼은 없다.
    /// </summary>
    public sealed class ChoicePanel : MonoBehaviour
    {
        private const float RowHeight = 104f;
        private const float RowGap = 12f;
        /// <summary>줄이 이보다 많으면 낮은 줄을 쓴다.</summary>
        private const int CompactAbove = 4;
        private const float CompactRowHeight = 90f;
        private const float CompactRowGap = 8f;
        private const float Width = 1000f;
        private const string KeysClose = "숫자 키로 고르기          E  닫기";
        private const string KeysBack = "숫자 키로 고르기          E  뒤로";

        private static readonly Color RowColor = new Color(0.14f, 0.18f, 0.28f, 0.95f);
        private static readonly Color RowDisabled = new Color(0.12f, 0.13f, 0.17f, 0.9f);
        private static readonly Color Dim = new Color(0.62f, 0.64f, 0.68f);

        public bool Visible => _root.activeSelf;
        public string Title => _title.text;
        public int OptionCount { get; private set; }
        public string KeysText => _keys.text;

        private GameObject _root;
        private RectTransform _rect;
        private Text _title;
        private Text _body;
        private Text _keys;
        private readonly GameObject[] _rows = new GameObject[ChoiceMenu.MaxOptions];
        private readonly Image[] _rowBacks = new Image[ChoiceMenu.MaxOptions];
        private readonly Image[] _keyBacks = new Image[ChoiceMenu.MaxOptions];
        private readonly Text[] _labels = new Text[ChoiceMenu.MaxOptions];
        private readonly Text[] _costs = new Text[ChoiceMenu.MaxOptions];
        private readonly Text[] _details = new Text[ChoiceMenu.MaxOptions];
        private int _shownVersion = -1;

        public static ChoicePanel Create(Transform root)
        {
            Image back = UiFactory.Panel(root, "Choice", new Vector2(0.5f, 0.5f), new Vector2(0f, 10f),
                new Vector2(Width, 600f), new Color(0.06f, 0.08f, 0.14f, 0.96f));
            var panel = back.gameObject.AddComponent<ChoicePanel>();
            panel._root = back.gameObject;
            panel._rect = (RectTransform)back.transform;
            panel.Build(back.transform);
            back.gameObject.SetActive(false);
            return panel;
        }

        private void Build(Transform root)
        {
            UiFactory.Panel(root, "TopLine", new Vector2(0.5f, 1f), Vector2.zero, new Vector2(Width, 8f), UiFactory.Accent);
            _title = UiFactory.LabelAt(root, "Title", new Vector2(0.5f, 1f), new Vector2(0f, -22f), new Vector2(Width - 80f, 64f),
                46, TextAnchor.MiddleCenter, Color.white);
            _body = UiFactory.LabelAt(root, "Body", new Vector2(0.5f, 1f), new Vector2(0f, -92f), new Vector2(Width - 100f, 84f),
                28, TextAnchor.MiddleCenter, UiFactory.TextColor);

            for (int i = 0; i < _rows.Length; i++)
            {
                Image row = UiFactory.Panel(root, "Row" + (i + 1), new Vector2(0.5f, 1f), Vector2.zero,
                    new Vector2(Width - 80f, RowHeight), RowColor);
                _rows[i] = row.gameObject;
                _rowBacks[i] = row;
                Image key = UiFactory.Panel(row.transform, "Key", new Vector2(0f, 0.5f), new Vector2(18f, 0f), new Vector2(64f, 64f), UiFactory.Accent);
                _keyBacks[i] = key;
                UiFactory.Label(key.transform, "Number", 38, TextAnchor.MiddleCenter, new Color(0.1f, 0.1f, 0.14f)).text = (i + 1).ToString();
                _labels[i] = UiFactory.LabelAt(row.transform, "Label", new Vector2(0f, 1f), new Vector2(104f, -10f), new Vector2(520f, 46f),
                    34, TextAnchor.MiddleLeft, Color.white);
                _costs[i] = UiFactory.LabelAt(row.transform, "Cost", new Vector2(1f, 1f), new Vector2(-24f, -10f), new Vector2(300f, 46f),
                    30, TextAnchor.MiddleRight, UiFactory.Accent);
                _details[i] = UiFactory.LabelAt(row.transform, "Detail", new Vector2(0f, 0f), new Vector2(104f, 10f), new Vector2(790f, 38f),
                    24, TextAnchor.MiddleLeft, UiFactory.TextColor);
                _details[i].horizontalOverflow = HorizontalWrapMode.Overflow;
            }

            _keys = UiFactory.LabelAt(root, "Keys", new Vector2(0.5f, 0f), new Vector2(0f, 18f), new Vector2(Width - 80f, 40f),
                26, TextAnchor.MiddleCenter, UiFactory.Accent);
            _keys.text = KeysClose;
        }

        /// <summary>
        /// Hud가 매 프레임 부른다. 이 화면이 꺼져 있는 동안에는 자신의 Update가 돌지 않으므로
        /// 켜고 끄는 일을 바깥에서 맡는다.
        /// </summary>
        public void Refresh()
        {
            ChoiceMenu menu = ChoiceMenu.Instance;
            bool open = menu != null && menu.IsOpen;
            if (_root.activeSelf != open) _root.SetActive(open);
            if (!open || menu.Version == _shownVersion) return;
            _shownVersion = menu.Version;

            _title.text = menu.Title ?? "";
            _body.text = menu.Body ?? "";
            bool hasBody = !string.IsNullOrEmpty(menu.Body);
            _body.gameObject.SetActive(hasBody);
            // E가 "닫기"인지 "뒤로"인지 그대로 알려 준다.
            _keys.text = menu.OnCancelled != null ? KeysBack : KeysClose;

            OptionCount = menu.Options.Count;
            // 줄이 많으면 줄을 낮춰 화면 안에 들어가게 한다.
            bool compact = OptionCount > CompactAbove;
            float rowHeight = compact ? CompactRowHeight : RowHeight;
            float rowGap = compact ? CompactRowGap : RowGap;
            float inset = compact ? 4f : 10f;
            float y = hasBody ? -190f : -104f;
            for (int i = 0; i < _rows.Length; i++)
            {
                bool used = i < OptionCount;
                _rows[i].SetActive(used);
                if (!used) continue;

                ChoiceOption option = menu.Options[i];
                var rowRect = (RectTransform)_rows[i].transform;
                rowRect.anchoredPosition = new Vector2(0f, y);
                rowRect.sizeDelta = new Vector2(Width - 80f, rowHeight);
                _labels[i].rectTransform.anchoredPosition = new Vector2(104f, -inset);
                _costs[i].rectTransform.anchoredPosition = new Vector2(-24f, -inset);
                _details[i].rectTransform.anchoredPosition = new Vector2(104f, inset);
                y -= rowHeight + rowGap;

                _rowBacks[i].color = option.enabled ? RowColor : RowDisabled;
                _keyBacks[i].color = option.enabled ? UiFactory.Accent : Dim;
                _labels[i].text = option.label ?? "";
                _labels[i].color = option.enabled ? Color.white : Dim;
                _costs[i].text = option.cost ?? "";
                _costs[i].color = option.enabled ? UiFactory.Accent : Dim;
                _details[i].text = option.detail ?? "";
                _details[i].color = option.enabled ? UiFactory.TextColor : Dim;
            }
            _rect.sizeDelta = new Vector2(Width, -y + 62f);
        }
    }
}
