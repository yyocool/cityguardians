using System;
using CityGuardians.Domain;
using CityGuardians.Inputs;
using CityGuardians.Services;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CityGuardians.Gameplay
{
    /// <summary>
    /// 쿠폰을 상점에 보여 주는 화면의 상태 (docs/COUPON_SYSTEM.md 4장). QR 과 일회용 코드, 남은 시간.
    /// 화면은 UI(QrPanel)가 이 객체의 상태를 읽어 그린다. 열려 있는 동안 플레이어 조작을 잠그고, E로 닫는다.
    ///
    /// 여기서 하는 일은 보여 주기뿐이다. 쿠폰을 "사용됨"으로 바꾸는 것은 상점이 확인 화면에서 하고 서버가 정한다.
    /// 그래서 닫을 때도, 시간이 다 됐을 때도 서버에 아무것도 보내지 않는다.
    /// </summary>
    public sealed class CouponPresenter : MonoBehaviour
    {
        public const string ExpiredToast = "코드의 시간이 지났어요. 쿠폰은 그대로 있어요. 다시 보여 주기를 눌러 주세요.";

        public static CouponPresenter Instance { get; private set; }

        public bool IsOpen { get; private set; }
        /// <summary>혜택의 이름.</summary>
        public string Title { get; private set; }
        /// <summary>상점 이름과 할인 내용.</summary>
        public string Subtitle { get; private set; }
        /// <summary>직원이 손으로 넣을 수 있는 일회용 코드 ("XXXXX-XXXXX").</summary>
        public string Code { get; private set; }
        /// <summary>QR 그림. 일회용 코드만 들어 있다.</summary>
        public QrCode Qr { get; private set; }
        /// <summary>QR 에 들어간 글. 자동 테스트가 개인정보가 없는지 확인하는 데 쓴다.</summary>
        public string QrText { get; private set; }
        public int SecondsLeft { get; private set; }
        /// <summary>내용이 바뀔 때마다 1씩 오른다.</summary>
        public int Version { get; private set; }

        private float _deadline;
        private int _openedFrame;
        private Action _onClosed;

        private void Awake() => Instance = this;

        /// <param name="qrText">QR 에 넣을 글. BenefitRules.SafeQrText 를 거친 것이어야 한다.</param>
        /// <param name="seconds">코드가 살아 있는 시간.</param>
        /// <param name="onClosed">닫힌 뒤에 할 일 (쿠폰 목록으로 돌아가기).</param>
        /// <returns>그릴 수 없는 글이면 false. 그때는 아무것도 열지 않는다.</returns>
        public bool Show(string title, string subtitle, string code, string qrText, int seconds, Action onClosed)
        {
            if (seconds <= 0 || !BenefitRules.LooksLikePresentCode(code) || !QrCode.TryEncode(qrText, out QrCode qr)) return false;
            CloseSilently();
            Title = title ?? "";
            Subtitle = subtitle ?? "";
            Code = code;
            Qr = qr;
            QrText = qrText;
            SecondsLeft = seconds;
            _deadline = Time.unscaledTime + seconds;
            _openedFrame = Time.frameCount;
            _onClosed = onClosed;
            IsOpen = true;
            Version++;
            GameState.Lock(GameState.LockCoupon);
            return true;
        }

        /// <summary>장면이 바뀔 때처럼, 뒤따르는 일 없이 닫는다.</summary>
        public void CloseSilently()
        {
            if (!IsOpen) return;
            IsOpen = false;
            // 닫으면 코드와 그림을 화면 상태에 남겨 두지 않는다.
            Code = null;
            Qr = null;
            QrText = null;
            _onClosed = null;
            Version++;
            GameState.Unlock(GameState.LockCoupon);
        }

        private void Finish()
        {
            Action closed = _onClosed;
            CloseSilently();
            closed?.Invoke();
        }

        private void Update()
        {
            if (!IsOpen) return;

            // 시간은 멈춤 화면에서도 간다 (서버의 5분은 기다려 주지 않는다).
            int left = Mathf.CeilToInt(_deadline - Time.unscaledTime);
            if (left <= 0)
            {
                GameEvents.ShowToast(ExpiredToast);
                Finish();
                return;
            }
            if (left != SecondsLeft)
            {
                SecondsLeft = left;
                Version++;
            }

            if (GameState.IsPaused || Time.frameCount <= _openedFrame) return;
            IPlayerInput input = PlayerInputHub.Current;
            if (input.InteractPressed || input.JumpPressed) Finish();
        }
    }

    /// <summary>
    /// P 키로 게임 속 휴대전화를 꺼내고 넣는다. 장면이 바뀌어도 남아 있는 GameRoot 에 붙는다.
    /// 휴대전화에서는 CITY PASS 와 장학 안내, 홈페이지 로그인 코드를 볼 수 있다. 전투 중에는 꺼낼 수 없다.
    /// </summary>
    public sealed class PhoneController : MonoBehaviour
    {
        /// <summary>도시에 나가 첫 전투를 마친 뒤부터 쓸 수 있다 (지도와 같다).</summary>
        public const int MinTutorialStep = 6;

        /// <summary>지금 휴대전화를 꺼낼 수 있는가. 방과 도시에서만 된다.</summary>
        public static bool Available
        {
            get
            {
                GameSession session = GameSession.Current;
                if (session == null || session.TutorialStep < MinTutorialStep) return false;
                string scene = SceneManager.GetActiveScene().name;
                return scene == SceneNames.Room || scene == SceneNames.City;
            }
        }

        private void Update()
        {
            if (GameSession.Current == null || GameRoot.Instance.IsBusy) return;
            if (!PlayerInputHub.Current.PhonePressed) return;

            ChoiceMenu menu = ChoiceMenu.Instance;
            if (menu.IsOpen && menu.Tag == PhoneActions.PhoneTag)
            {
                if (!GameState.IsPaused) menu.Close();
                return;
            }
            if (GameState.IsControlLocked || GameState.Decorating || !Available) return;
            Sfx.Play(Sfx.Id.Interact);
            PhoneActions.Open();
        }
    }
}
