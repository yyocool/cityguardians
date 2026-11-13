using System.Collections.Generic;

namespace CityGuardians.Services
{
    /// <summary>
    /// 대시보드의 알림 (docs/LIVE_DASHBOARD.md 6장).
    ///
    /// 서버가 요약에 실어 보낸 알림을 받아, 처음 보는 것만 "새 알림"으로 돌려준다. 같은 일에는 같은 id 가 오므로
    /// 요약을 받을 때마다 같은 알림이 다시 뜨지 않는다. 어떤 일이 알림감인지는 서버가 정한다 (관리자의 경고 기준과 정책).
    /// </summary>
    public sealed class DashboardNotificationService
    {
        /// <summary>기억해 두는 id 의 수. 넘으면 오래된 것부터 잊는다.</summary>
        public const int SeenLimit = 64;

        private readonly List<string> _seen = new List<string>();
        private readonly List<DashboardNoticeDto> _active = new List<DashboardNoticeDto>();
        private readonly List<DashboardNoticeDto> _fresh = new List<DashboardNoticeDto>();

        /// <summary>지금 살아 있는 알림 (마지막 요약에 들어 있던 것).</summary>
        public IReadOnlyList<DashboardNoticeDto> Active => _active;

        /// <summary>우리 도시의 알림 가운데 가장 급한 것. 없으면 null. 전투 중에도 카드 머리에 보인다.</summary>
        public DashboardNoticeDto CityAlert { get; private set; }

        /// <summary>다른 도시의 알림 수.</summary>
        public int NationalCount { get; private set; }

        /// <summary>요약을 받았다. 처음 보는 알림만 돌려준다 (돌려준 목록은 다음에 부를 때 다시 쓰인다).</summary>
        public IReadOnlyList<DashboardNoticeDto> Accept(DashboardSummaryDto summary)
        {
            _active.Clear();
            _fresh.Clear();
            CityAlert = null;
            NationalCount = 0;
            if (summary == null || summary.notifications == null) return _fresh;

            for (int i = 0; i < summary.notifications.Count; i++)
            {
                DashboardNoticeDto notice = summary.notifications[i];
                if (notice == null || string.IsNullOrEmpty(notice.id)) continue;
                _active.Add(notice);
                if (notice.IsCity)
                {
                    if (CityAlert == null || DashboardNoticeLevels.Order(notice.level) > DashboardNoticeLevels.Order(CityAlert.level))
                        CityAlert = notice;
                }
                else
                {
                    NationalCount++;
                }
                if (_seen.Contains(notice.id)) continue;
                _seen.Add(notice.id);
                _fresh.Add(notice);
            }
            while (_seen.Count > SeenLimit) _seen.RemoveAt(0);
            return _fresh;
        }

        /// <summary>
        /// 이 알림을 화면 위의 한 줄 알림으로 띄울 것인가.
        /// 우리 도시의 경고·긴급 알림은 전투 중에도 띄운다. 다른 도시의 알림은 전투 중에는 띄우지 않는다 (카드에는 남는다).
        /// </summary>
        public static bool ShouldToast(DashboardNoticeDto notice, bool inBattle)
        {
            if (notice == null || DashboardNoticeLevels.Order(notice.level) < DashboardNoticeLevels.Order(DashboardNoticeLevels.Warn)) return false;
            return notice.IsCity || !inBattle;
        }
    }
}
