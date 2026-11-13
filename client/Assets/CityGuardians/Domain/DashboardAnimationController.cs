namespace CityGuardians.Domain
{
    /// <summary>
    /// 카드 한 장의 움직임: 자리, 크기, 투명도 (docs/HUD_LAYOUT_SYSTEM.md 4장).
    ///
    /// 목표가 바뀐 동안에만 값을 옮긴다. 도착하면 Step 이 false 를 돌려주고, 그 뒤로는 아무것도 다시 그리지 않는다.
    /// 시간은 게임 시간이 아니라 실제 시간으로 흐른다 (잠시 멈춤 중에도 접고 펼 수 있다).
    /// </summary>
    public sealed class DashboardAnimationController
    {
        /// <summary>투명도는 0~100 으로 다룬다 (자리와 같은 걸음 규칙을 쓰려고).</summary>
        private const float AlphaUnits = 100f;

        public float X { get; private set; }
        public float Y { get; private set; }
        public float Width { get; private set; }
        public float Height { get; private set; }
        public float Alpha { get; private set; }
        /// <summary>움직이는 중인가.</summary>
        public bool Moving { get; private set; }
        /// <summary>완전히 사라졌는가 (그리지 않아도 된다).</summary>
        public bool Gone => !_visible && Alpha <= 0f;

        private float _x, _y, _width, _height, _alpha;
        private bool _visible;
        private bool _placed;

        /// <summary>새 자리를 정한다. 처음 놓일 때와, 사라졌다가 다시 나타날 때는 그 자리에서 바로 시작한다.</summary>
        public void SetTarget(DashboardBox box, bool instant = false)
        {
            bool visible = box.Shown;
            if (visible)
            {
                _x = box.x;
                _y = box.y;
                _width = box.width;
                _height = box.height;
            }
            _alpha = visible ? AlphaUnits : 0f;

            if (!_placed || instant || (visible && Gone))
            {
                // 날아오지 않고 제자리에서 나타난다.
                X = _x;
                Y = _y;
                Width = _width;
                Height = _height;
                if (!_placed || instant) Alpha = _alpha / AlphaUnits;
                _placed = true;
            }
            _visible = visible;
            Moving = true;
        }

        /// <summary>한 프레임만큼 움직인다. 값이 달라졌으면 true.</summary>
        public bool Step(float deltaSeconds)
        {
            if (!Moving) return false;
            float x = DashboardEase.Step(X, _x, deltaSeconds);
            float y = DashboardEase.Step(Y, _y, deltaSeconds);
            float width = DashboardEase.Step(Width, _width, deltaSeconds);
            float height = DashboardEase.Step(Height, _height, deltaSeconds);
            float alpha = DashboardEase.Step(Alpha * AlphaUnits, _alpha, deltaSeconds) / AlphaUnits;
            X = x;
            Y = y;
            Width = width;
            Height = height;
            Alpha = alpha;
            Moving = x != _x || y != _y || width != _width || height != _height || alpha * AlphaUnits != _alpha;
            return true;
        }

        /// <summary>움직임을 건너뛰고 목표에 놓는다.</summary>
        public void Snap()
        {
            X = _x;
            Y = _y;
            Width = _width;
            Height = _height;
            Alpha = _alpha / AlphaUnits;
            Moving = true;
        }
    }
}
