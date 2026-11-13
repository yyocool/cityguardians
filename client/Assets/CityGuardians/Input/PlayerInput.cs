using UnityEngine;
using UnityEngine.InputSystem;

namespace CityGuardians.Inputs
{
    /// <summary>
    /// 게임 코드가 읽는 "의도". 키 이름이 아니라 하려는 일을 나타낸다 (docs/PLATFORM_STRATEGY.md 3.2).
    /// Windows에서는 키보드·마우스, 모바일에서는 터치 구현체가 이 인터페이스를 채운다.
    /// </summary>
    public interface IPlayerInput
    {
        /// <summary>이동. x는 좌우, y는 앞뒤. 크기 0~1.</summary>
        Vector2 Move { get; }
        /// <summary>시점 회전량 (이번 프레임).</summary>
        Vector2 LookDelta { get; }
        /// <summary>카메라 거리 조절량 (이번 프레임). 양수면 가까이.</summary>
        float ZoomDelta { get; }
        bool Run { get; }
        bool AttackHeld { get; }
        bool JumpPressed { get; }
        bool InteractPressed { get; }
        bool PausePressed { get; }
        bool QuitPressed { get; }
        bool RetryPressed { get; }
        bool NewGamePressed { get; }
        /// <summary>선택 메뉴에서 고른 번호 (1~6). 이번 프레임에 고르지 않았으면 0.</summary>
        int ChoicePressed { get; }
        /// <summary>지도를 열거나 닫는다.</summary>
        bool MapPressed { get; }
        /// <summary>가방을 열거나 닫는다.</summary>
        bool InventoryPressed { get; }
        /// <summary>고른 무기 칸: 1 근접, 2 원거리. 이번 프레임에 고르지 않았으면 0.</summary>
        int WeaponSlotPressed { get; }
        /// <summary>회복 아이템 빠른 사용.</summary>
        bool QuickUsePressed { get; }
        /// <summary>방 꾸미기를 시작하거나 그만둔다.</summary>
        bool DecoratePressed { get; }
        /// <summary>들고 있는 가구를 돌린다.</summary>
        bool RotatePressed { get; }

        // 화면을 직접 가리키는 입력. 지도처럼 커서가 풀려 있는 화면에서 쓴다 (마우스, 추후 터치).
        /// <summary>가리키는 곳의 화면 좌표 (픽셀, 왼쪽 아래가 0,0).</summary>
        Vector2 PointerPosition { get; }
        /// <summary>이번 프레임에 누르기 시작했는가.</summary>
        bool PointerPressed { get; }
        bool PointerHeld { get; }
        /// <summary>이번 프레임에 뗐는가.</summary>
        bool PointerReleased { get; }
        /// <summary>"뒤로"에 해당하는 누름 (마우스 오른쪽 버튼).</summary>
        bool PointerBackPressed { get; }
    }

    /// <summary>현재 쓰이는 입력. 테스트에서는 ScriptedInput으로 바꿔 끼운다.</summary>
    public static class PlayerInputHub
    {
        private static IPlayerInput _current;

        public static IPlayerInput Current
        {
            get => _current ?? (_current = new KeyboardMouseInput());
            set => _current = value;
        }

        public static void ResetToDefault() => _current = null;
    }

    /// <summary>Windows 키보드·마우스 (docs/COMBAT_SYSTEM.md 2장).</summary>
    public sealed class KeyboardMouseInput : IPlayerInput
    {
        private const float MouseSensitivity = 0.12f;

        public Vector2 Move
        {
            get
            {
                Keyboard keyboard = Keyboard.current;
                if (keyboard == null) return Vector2.zero;
                float x = (keyboard.dKey.isPressed ? 1f : 0f) - (keyboard.aKey.isPressed ? 1f : 0f);
                float y = (keyboard.wKey.isPressed ? 1f : 0f) - (keyboard.sKey.isPressed ? 1f : 0f);
                var move = new Vector2(x, y);
                return move.sqrMagnitude > 1f ? move.normalized : move;
            }
        }

        public Vector2 LookDelta
        {
            get
            {
                Mouse mouse = Mouse.current;
                // 커서가 풀려 있을 때(멈춤, 창 밖)는 시점을 돌리지 않는다.
                if (mouse == null || Cursor.lockState != CursorLockMode.Locked) return Vector2.zero;
                return mouse.delta.ReadValue() * MouseSensitivity;
            }
        }

        public float ZoomDelta
        {
            get
            {
                Mouse mouse = Mouse.current;
                if (mouse == null) return 0f;
                float scroll = mouse.scroll.ReadValue().y;
                return Mathf.Clamp(scroll, -1f, 1f);
            }
        }

        public bool Run => Keyboard.current != null && Keyboard.current.leftShiftKey.isPressed;

        public bool AttackHeld =>
            Mouse.current != null && Mouse.current.leftButton.isPressed && Cursor.lockState == CursorLockMode.Locked;

        public bool JumpPressed => Pressed(Key.Space);
        public bool InteractPressed => Pressed(Key.E);
        public bool PausePressed => Pressed(Key.Escape);
        public bool QuitPressed => Pressed(Key.Q);
        public bool RetryPressed => Pressed(Key.R);
        public bool NewGamePressed => Pressed(Key.N);

        public int ChoicePressed
        {
            get
            {
                if (Pressed(Key.Digit1) || Pressed(Key.Numpad1)) return 1;
                if (Pressed(Key.Digit2) || Pressed(Key.Numpad2)) return 2;
                if (Pressed(Key.Digit3) || Pressed(Key.Numpad3)) return 3;
                if (Pressed(Key.Digit4) || Pressed(Key.Numpad4)) return 4;
                if (Pressed(Key.Digit5) || Pressed(Key.Numpad5)) return 5;
                if (Pressed(Key.Digit6) || Pressed(Key.Numpad6)) return 6;
                return 0;
            }
        }

        public bool MapPressed => Pressed(Key.M);
        public bool InventoryPressed => Pressed(Key.I);

        // 숫자 키는 선택 메뉴가 떠 있을 때는 선택지 번호이고, 걸어 다닐 때는 무기 칸과 빠른 사용이다.
        // 어느 쪽으로 읽을지는 게임 코드가 상태를 보고 정한다.
        public int WeaponSlotPressed
        {
            get
            {
                if (Pressed(Key.Digit1) || Pressed(Key.Numpad1)) return 1;
                if (Pressed(Key.Digit2) || Pressed(Key.Numpad2)) return 2;
                return 0;
            }
        }

        public bool QuickUsePressed => Pressed(Key.Digit3) || Pressed(Key.Numpad3);
        public bool DecoratePressed => Pressed(Key.F);
        public bool RotatePressed => Pressed(Key.R);

        public Vector2 PointerPosition => Mouse.current != null ? Mouse.current.position.ReadValue() : Vector2.zero;

        // 커서가 잠겨 있을 때(걸어 다니는 중)의 클릭은 공격이지 화면을 가리키는 것이 아니다.
        private static bool PointerActive => Mouse.current != null && Cursor.lockState != CursorLockMode.Locked;

        public bool PointerPressed => PointerActive && Mouse.current.leftButton.wasPressedThisFrame;
        public bool PointerHeld => PointerActive && Mouse.current.leftButton.isPressed;
        public bool PointerReleased => PointerActive && Mouse.current.leftButton.wasReleasedThisFrame;
        public bool PointerBackPressed => PointerActive && Mouse.current.rightButton.wasPressedThisFrame;

        private static bool Pressed(Key key)
        {
            Keyboard keyboard = Keyboard.current;
            return keyboard != null && keyboard[key].wasPressedThisFrame;
        }
    }

    /// <summary>
    /// 코드로 조작하는 입력. 자동 테스트에서 쓴다.
    /// "누름" 계열은 Press…()를 부른 다음 프레임 한 번만 true가 된다.
    /// </summary>
    public sealed class ScriptedInput : IPlayerInput
    {
        public Vector2 move;
        public Vector2 lookDelta;
        public float zoomDelta;
        public bool run;
        public bool attackHeld;
        /// <summary>가리키는 곳의 화면 좌표.</summary>
        public Vector2 pointerPosition;
        /// <summary>끌기를 흉내 낼 때 누른 채로 둔다.</summary>
        public bool pointerHeld;

        private int _mapFrame = -1;
        private int _pointerDownFrame = -1;
        private int _pointerUpFrame = -1;
        private int _pointerBackFrame = -1;
        private int _jumpFrame = -1;
        private int _interactFrame = -1;
        private int _pauseFrame = -1;
        private int _quitFrame = -1;
        private int _retryFrame = -1;
        private int _newGameFrame = -1;
        private int _choiceFrame = -1;
        private int _choice;
        private int _inventoryFrame = -1;
        private int _weaponSlotFrame = -1;
        private int _weaponSlot;
        private int _quickUseFrame = -1;
        private int _decorateFrame = -1;
        private int _rotateFrame = -1;

        public bool InventoryPressed => Time.frameCount == _inventoryFrame;
        public int WeaponSlotPressed => Time.frameCount == _weaponSlotFrame ? _weaponSlot : 0;
        public bool QuickUsePressed => Time.frameCount == _quickUseFrame;

        public bool DecoratePressed => Time.frameCount == _decorateFrame;
        public bool RotatePressed => Time.frameCount == _rotateFrame;

        public void PressInventory() => _inventoryFrame = Time.frameCount + 1;
        public void PressDecorate() => _decorateFrame = Time.frameCount + 1;
        public void PressRotate() => _rotateFrame = Time.frameCount + 1;

        /// <summary>
        /// 숫자 키 하나를 누른다. 실제 키보드에서는 같은 키가 선택지 번호이자 무기 칸·빠른 사용이므로
        /// 여기서도 함께 눌린 것으로 만든다. 그래야 "메뉴를 고른 키가 무기도 바꾸는" 실수를 테스트가 잡는다.
        /// </summary>
        public void PressNumber(int number)
        {
            int frame = Time.frameCount + 1;
            _choice = number;
            _choiceFrame = frame;
            _weaponSlot = number == 1 || number == 2 ? number : 0;
            _weaponSlotFrame = frame;
            if (number == 3) _quickUseFrame = frame;
        }

        public void PressWeaponSlot(int slot) => PressNumber(slot);
        public void PressQuickUse() => PressNumber(3);

        public Vector2 Move => move;
        public Vector2 LookDelta => lookDelta;
        public float ZoomDelta => zoomDelta;
        public bool Run => run;
        public bool AttackHeld => attackHeld;
        public bool JumpPressed => Time.frameCount == _jumpFrame;
        public bool InteractPressed => Time.frameCount == _interactFrame;
        public bool PausePressed => Time.frameCount == _pauseFrame;
        public bool QuitPressed => Time.frameCount == _quitFrame;
        public bool RetryPressed => Time.frameCount == _retryFrame;
        public bool NewGamePressed => Time.frameCount == _newGameFrame;
        public int ChoicePressed => Time.frameCount == _choiceFrame ? _choice : 0;
        public bool MapPressed => Time.frameCount == _mapFrame;
        public Vector2 PointerPosition => pointerPosition;
        public bool PointerPressed => Time.frameCount == _pointerDownFrame;
        public bool PointerHeld => pointerHeld || Time.frameCount == _pointerDownFrame;
        public bool PointerReleased => Time.frameCount == _pointerUpFrame;
        public bool PointerBackPressed => Time.frameCount == _pointerBackFrame;

        public void PressMap() => _mapFrame = Time.frameCount + 1;

        /// <summary>그 자리를 한 번 누른다: 다음 프레임에 누르고 그 다음 프레임에 뗀다.</summary>
        public void Click(Vector2 screenPosition)
        {
            pointerPosition = screenPosition;
            _pointerDownFrame = Time.frameCount + 1;
            _pointerUpFrame = Time.frameCount + 2;
        }

        /// <summary>끌기 시작: 다음 프레임에 누르고 누른 채로 둔다. pointerPosition을 옮긴 뒤 EndDrag로 뗀다.</summary>
        public void BeginDrag(Vector2 screenPosition)
        {
            pointerPosition = screenPosition;
            _pointerDownFrame = Time.frameCount + 1;
            pointerHeld = true;
        }

        public void EndDrag()
        {
            pointerHeld = false;
            _pointerUpFrame = Time.frameCount + 1;
        }

        public void PressPointerBack() => _pointerBackFrame = Time.frameCount + 1;

        public void PressChoice(int number) => PressNumber(number);

        public void PressJump() => _jumpFrame = Time.frameCount + 1;
        public void PressInteract() => _interactFrame = Time.frameCount + 1;
        public void PressPause() => _pauseFrame = Time.frameCount + 1;
        public void PressQuit() => _quitFrame = Time.frameCount + 1;
        public void PressRetry() => _retryFrame = Time.frameCount + 1;
        public void PressNewGame() => _newGameFrame = Time.frameCount + 1;
    }
}
