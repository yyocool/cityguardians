using CityGuardians.Domain;
using CityGuardians.Gameplay;
using UnityEngine;
using UnityEngine.UI;

namespace CityGuardians.UI
{
    /// <summary>
    /// 쿠폰을 상점에 보여 주는 화면: QR, 일회용 코드, 남은 시간 (docs/COUPON_SYSTEM.md 4장).
    /// CouponPresenter(게임플레이)의 상태를 읽어 그리기만 한다. 닉네임이나 회원번호는 이 화면에 없다.
    /// </summary>
    public sealed class QrPanel : MonoBehaviour
    {
        private const float Width = 760f;
        private const float Height = 900f;
        private const float QrSize = 440f;
        public const string NoteText = "직원에게 이 화면을 보여 주세요. 사용 처리는 상점에서 해요.";
        public const string KeysText = "E  닫기";

        public bool Visible => _root.activeSelf;
        public string TitleText => _title.text;
        public string SubtitleText => _subtitle.text;
        public string CodeText => _code.text;
        public string CountdownText => _countdown.text;
        /// <summary>그림 한 변의 칸 수 (조용한 영역 포함). 닫혀 있으면 0.</summary>
        public int TextureSize => _texture != null ? _texture.width : 0;

        private GameObject _root;
        private Text _title;
        private Text _subtitle;
        private Text _code;
        private Text _countdown;
        private RawImage _image;
        private Texture2D _texture;
        private QrCode _shownQr;
        private int _shownVersion = -1;

        public static QrPanel Create(Transform root)
        {
            Image back = UiFactory.Panel(root, "QrPanel", new Vector2(0.5f, 0.5f), new Vector2(0f, 10f),
                new Vector2(Width, Height), new Color(0.06f, 0.08f, 0.14f, 0.97f));
            var panel = back.gameObject.AddComponent<QrPanel>();
            panel._root = back.gameObject;
            panel.Build(back.transform);
            back.gameObject.SetActive(false);
            return panel;
        }

        private void Build(Transform root)
        {
            UiFactory.Panel(root, "TopLine", new Vector2(0.5f, 1f), Vector2.zero, new Vector2(Width, 8f), UiFactory.Accent);
            _title = UiFactory.LabelAt(root, "Title", new Vector2(0.5f, 1f), new Vector2(0f, -24f), new Vector2(Width - 60f, 60f),
                42, TextAnchor.MiddleCenter, Color.white);
            _title.horizontalOverflow = HorizontalWrapMode.Overflow;
            _subtitle = UiFactory.LabelAt(root, "Subtitle", new Vector2(0.5f, 1f), new Vector2(0f, -88f), new Vector2(Width - 60f, 40f),
                26, TextAnchor.MiddleCenter, UiFactory.TextColor);
            _subtitle.horizontalOverflow = HorizontalWrapMode.Overflow;

            // QR 은 흰 바탕에 검은 칸이어야 읽힌다. 바탕은 그림보다 조금 크게 둔다.
            Image paper = UiFactory.Panel(root, "Paper", new Vector2(0.5f, 1f), new Vector2(0f, -144f),
                new Vector2(QrSize + 24f, QrSize + 24f), Color.white);
            RectTransform imageRect = UiFactory.Rect(paper.transform, "Qr", new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(QrSize, QrSize));
            _image = imageRect.gameObject.AddComponent<RawImage>();
            _image.raycastTarget = false;
            _image.color = Color.white;

            _code = UiFactory.LabelAt(root, "Code", new Vector2(0.5f, 0f), new Vector2(0f, 196f), new Vector2(Width - 60f, 80f),
                64, TextAnchor.MiddleCenter, UiFactory.Accent);
            _countdown = UiFactory.LabelAt(root, "Countdown", new Vector2(0.5f, 0f), new Vector2(0f, 146f), new Vector2(Width - 60f, 44f),
                30, TextAnchor.MiddleCenter, Color.white);
            UiFactory.LabelAt(root, "Note", new Vector2(0.5f, 0f), new Vector2(0f, 98f), new Vector2(Width - 60f, 40f),
                24, TextAnchor.MiddleCenter, UiFactory.TextColor).text = NoteText;
            UiFactory.LabelAt(root, "Keys", new Vector2(0.5f, 0f), new Vector2(0f, 26f), new Vector2(Width - 60f, 40f),
                26, TextAnchor.MiddleCenter, UiFactory.Accent).text = KeysText;
        }

        /// <summary>Hud가 매 프레임 부른다.</summary>
        public void Refresh()
        {
            CouponPresenter presenter = CouponPresenter.Instance;
            bool open = presenter != null && presenter.IsOpen;
            if (_root.activeSelf != open) _root.SetActive(open);
            if (!open)
            {
                // 닫히면 그림과 코드를 남겨 두지 않는다.
                if (_texture != null) Clear();
                return;
            }
            if (presenter.Version == _shownVersion) return;
            _shownVersion = presenter.Version;

            _title.text = presenter.Title ?? "";
            _subtitle.text = presenter.Subtitle ?? "";
            _code.text = presenter.Code ?? "";
            _countdown.text = "남은 시간  " + BenefitRules.RemainingText(presenter.SecondsLeft);
            if (!ReferenceEquals(presenter.Qr, _shownQr)) Draw(presenter.Qr);
        }

        private void Draw(QrCode qr)
        {
            _shownQr = qr;
            if (_texture != null) Destroy(_texture);
            _texture = null;
            if (qr == null) return;

            int quiet = QrCode.QuietZone;
            int size = qr.Size + quiet * 2;
            // 칸이 번지지 않게 한 칸을 한 점으로 그리고, 늘릴 때 섞지 않는다.
            _texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            var pixels = new Color32[size * size];
            var dark = new Color32(0, 0, 0, 255);
            var light = new Color32(255, 255, 255, 255);
            for (int y = 0; y < size; y++)
            {
                // 그림의 첫 줄은 위쪽이고, 텍스처의 첫 줄은 아래쪽이다.
                int row = size - 1 - y - quiet;
                for (int x = 0; x < size; x++) pixels[y * size + x] = qr.IsDark(x - quiet, row) ? dark : light;
            }
            _texture.SetPixels32(pixels);
            _texture.Apply(false, false);
            _image.texture = _texture;
        }

        private void Clear()
        {
            Destroy(_texture);
            _texture = null;
            _shownQr = null;
            _image.texture = null;
            _code.text = "";
            _shownVersion = -1;
        }

        /// <summary>그림의 그 칸이 어두운가 (왼쪽 위가 0,0, 조용한 영역 포함). 자동 테스트에서 쓴다.</summary>
        public bool IsDarkPixel(int x, int y)
        {
            if (_texture == null) return false;
            Color32 pixel = _texture.GetPixel(x, _texture.height - 1 - y);
            return pixel.r < 128;
        }

        private void OnDestroy()
        {
            if (_texture != null) Destroy(_texture);
        }
    }
}
