using System;
using CityGuardians.Domain;

namespace CityGuardians.Services
{
    /// <summary>
    /// 실시간 대시보드의 데이터 창구 (docs/LIVE_DASHBOARD.md 5장). 화면(UI/Dashboard)은 여기서 받은 요약을 그리기만 한다.
    ///
    /// - 서버에 묻는 것은 GET /dashboard/summary 하나다. 값을 스스로 계산하거나 고치지 않는다.
    /// - 언제 물을지는 DashboardRefreshPolicy 가 정한다: 관리자가 정한 간격마다, 값이 바뀌는 일이 있으면 조금 앞당겨서,
    ///   실패하면 간격을 늘려서. Tick 은 매 프레임 불러도 되지만 요청은 그때에만 나간다.
    /// - 연결이 끊기면 마지막으로 받은 요약을 그대로 들고 있고, 언제 받은 것인지를 알려 준다.
    /// </summary>
    public sealed class DashboardDataService
    {
        private readonly IGameBackend _backend;
        private readonly IClock _clock;
        private double _now;
        private int _ticket;
        private bool _stopped;

        public DashboardRefreshPolicy Policy { get; } = new DashboardRefreshPolicy();

        /// <summary>마지막으로 받은 요약. 받은 적이 없으면 null.</summary>
        public DashboardSummaryDto Summary { get; private set; }
        /// <summary>요약이나 연결 상태가 바뀌면 오른다.</summary>
        public int Version { get; private set; }
        /// <summary>마지막으로 받은 때 (UTC). 받은 적이 없으면 null.</summary>
        public DateTime? LastSuccessUtc { get; private set; }
        public string LastErrorCode { get; private set; }
        /// <summary>지금까지 서버에 보낸 요청의 수 (점검용).</summary>
        public int RequestCount { get; private set; }

        public event Action<DashboardSummaryDto> Updated;

        public DashboardDataService(IGameBackend backend, IClock clock = null)
        {
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
            _clock = clock ?? new SystemClock();
        }

        public string Connection => DashboardConnection.Of(Summary != null, Policy.Failures);

        /// <summary>마지막으로 받은 뒤로 지난 시간(초). 받은 적이 없으면 0.</summary>
        public double SecondsSinceSuccess =>
            LastSuccessUtc.HasValue ? Math.Max(0.0, (_clock.UtcNow - LastSuccessUtc.Value).TotalSeconds) : 0.0;

        /// <summary>카드 아래에 쓰는 연결 상태 줄.</summary>
        public string StatusLine() =>
            DashboardFormat.Status(Connection, LastSuccessUtc.HasValue ? LastSuccessUtc.Value.ToLocalTime() : (DateTime?)null, SecondsSinceSuccess);

        /// <param name="now">흐르는 시간(초). 게임이 멈춰 있어도 흐르는 시계를 쓴다.</param>
        public void Tick(double now)
        {
            _now = now;
            if (_stopped || !Policy.ShouldRequest(now)) return;
            Request();
        }

        /// <summary>값이 바뀌는 일이 있었다 (금융 거래, 전투 결과, 전투 종료). 곧 다시 받는다.</summary>
        public void Nudge() => Policy.Nudge(_now);

        /// <summary>세션이 끝났다. 나가 있는 요청의 답은 버린다.</summary>
        public void Stop()
        {
            _stopped = true;
            _ticket++;
        }

        private void Request()
        {
            Policy.Started(_now);
            RequestCount++;
            int ticket = ++_ticket;
            try
            {
                _backend.GetDashboardSummary(result => OnResult(ticket, result));
            }
            catch (Exception e)
            {
                OnResult(ticket, ApiResult<DashboardSummaryDto>.Fail(ApiErrors.StorageFailed, e.Message));
            }
        }

        private void OnResult(int ticket, ApiResult<DashboardSummaryDto> result)
        {
            if (ticket != _ticket || _stopped) return;
            if (result != null && result.ok && result.data != null)
            {
                Summary = result.data;
                if (Summary.settings != null) Policy.Configure(Summary.settings.SafeRefreshSeconds);
                Policy.Succeeded(_now);
                LastSuccessUtc = _clock.UtcNow;
                LastErrorCode = null;
                Version++;
                Updated?.Invoke(Summary);
                return;
            }
            Policy.Failed(_now);
            LastErrorCode = result != null ? result.errorCode : ApiErrors.InvalidRequest;
            Version++;
        }
    }
}
