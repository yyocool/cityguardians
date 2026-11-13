using System;
using CityGuardians.Domain;
using UnityEngine;

namespace CityGuardians.Services
{
    /// <summary>
    /// 이용자가 정한 대시보드 설정을 기기에 저장한다 (docs/HUD_LAYOUT_SYSTEM.md 6장).
    /// 카드별 상태, 전체 표시 여부, 글자·카드 크기, 타워디펜스에서의 자리.
    ///
    /// 화면 설정일 뿐이라 서버에 보내지 않는다. 파일이 없거나 읽을 수 없으면 기본값으로 시작한다 (게임을 막지 않는다).
    /// </summary>
    public sealed class DashboardPreferenceService
    {
        public const string FileName = "dashboard_prefs.json";

        private readonly ITextStore _store;

        public DashboardPreferenceService(ITextStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        public DashboardPrefs Load()
        {
            DashboardPrefs prefs = null;
            try
            {
                string text = _store.Read(FileName);
                if (!string.IsNullOrEmpty(text)) prefs = JsonUtility.FromJson<DashboardPrefs>(text);
            }
            catch (Exception)
            {
                // 깨진 설정은 버린다. 다음에 저장할 때 새로 쓰인다.
                prefs = null;
            }
            if (prefs == null) prefs = new DashboardPrefs();
            prefs.Normalize();
            return prefs;
        }

        /// <returns>저장했는가. 저장하지 못해도 게임은 계속된다 (이번 실행 동안에는 설정이 지켜진다).</returns>
        public bool Save(DashboardPrefs prefs)
        {
            if (prefs == null) return false;
            try
            {
                _store.Write(FileName, JsonUtility.ToJson(prefs));
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
