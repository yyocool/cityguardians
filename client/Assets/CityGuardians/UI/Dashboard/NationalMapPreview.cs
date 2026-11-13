using System.Collections.Generic;
using CityGuardians.Domain;
using CityGuardians.Gameplay;
using CityGuardians.Services;
using UnityEngine;
using UnityEngine.UI;

namespace CityGuardians.UI
{
    /// <summary>
    /// 전국 카드 안의 축약 지도 (docs/NATIONAL_MAP_DASHBOARD.md 3장).
    ///
    /// 큰 지도와 같은 도형 데이터(MapData: 대한민국 시·군·구 경계)를 작게 그리고, 도시 위험도 단계의 색으로 칠한다.
    /// 색은 기존 도시 위험도 서비스(CityRiskService)가 받아 둔 값이다. 여기서 서버에 묻지 않는다.
    /// 우리 도시는 흰 점, 급한 도시는 붉은 네모로 표시한다. 확대·축소는 없다 (전체 지도는 상세 보기에서).
    /// </summary>
    public sealed class NationalMapPreview : MonoBehaviour
    {
        /// <summary>
        /// 축약 지도 아래의 출처 표기. 경계 데이터의 조건(공공누리 제1유형, CC BY 4.0)이 출처 표시를 요구한다.
        /// 전체 문구는 상세 보기의 지도 화면과 docs/MAP_SYSTEM.md 2장에 있다.
        /// </summary>
        public const string Credit = "지도 경계: 통계청 SGIS · vuski/admdongkor (CC BY 4.0)";

        private const int MarkerCount = 5;
        private static readonly Color Unknown = new Color(0.27f, 0.32f, 0.41f, 1f);

        /// <summary>지도 도형 데이터가 있는가.</summary>
        public bool HasMap { get; private set; }
        /// <summary>위험도 단계의 색으로 칠한 시·군·구의 수 (마지막으로 그렸을 때).</summary>
        public int ColoredRegions { get; private set; }
        public bool HomeMarked => _home != null && _home.gameObject.activeSelf;
        public int UrgentMarked { get; private set; }

        private MapMeshGraphic _mesh;
        private RectTransform _home;
        private readonly RectTransform[] _markers = new RectTransform[MarkerCount];
        private Vector2 _offset;
        private float _scale = 1f;
        private int _drawnVersion = -1;
        private string _drawnHome;

        public static NationalMapPreview Create(Transform parent, Vector2 position, Vector2 size)
        {
            RectTransform rect = UiFactory.Rect(parent, "MapPreview", new Vector2(0f, 1f), position, size);
            var preview = rect.gameObject.AddComponent<NationalMapPreview>();
            preview.Build(rect, size);
            return preview;
        }

        private void Build(RectTransform rect, Vector2 size)
        {
            // 지도는 꼭짓점이 많다. 제 캔버스에 따로 두어서, 다른 HUD 글자가 바뀔 때마다 지도까지 다시 묶이지 않게 한다.
            gameObject.AddComponent<Canvas>();

            MapData data = MapData.Instance;
            HasMap = data != null && !data.IsEmpty && data.File.width > 0 && data.File.height > 0;
            if (!HasMap)
            {
                DashboardStyle.Line(rect, "NoMap", new Vector2(0.5f, 0.5f), Vector2.zero, size, 13, TextAnchor.MiddleCenter, DashboardStyle.Dim)
                    .text = "지도 데이터 없음";
                return;
            }

            _scale = Mathf.Min(size.x / data.File.width, size.y / data.File.height);
            _offset = new Vector2((size.x - data.File.width * _scale) * 0.5f, (size.y - data.File.height * _scale) * 0.5f);
            _mesh = MapMeshGraphic.Create(rect, "Regions", new Vector2(data.File.width, data.File.height), Populate);
            _mesh.rectTransform.anchoredPosition = _offset;
            _mesh.rectTransform.localScale = new Vector3(_scale, _scale, 1f);

            for (int i = 0; i < MarkerCount; i++)
            {
                Image marker = UiFactory.Panel(rect, "Urgent" + i, new Vector2(0f, 0f), Vector2.zero, new Vector2(6f, 6f),
                    CityRiskService.StageColor(RiskStages.Emergency));
                marker.rectTransform.pivot = new Vector2(0.5f, 0.5f);
                _markers[i] = marker.rectTransform;
                marker.gameObject.SetActive(false);
            }
            Image home = DashboardStyle.RoundedPanel(rect, "Home", new Vector2(0f, 0f), Vector2.zero, new Vector2(9f, 9f), Color.white, 3f);
            home.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            _home = home.rectTransform;
            home.gameObject.SetActive(false);
        }

        /// <summary>지도를 지금의 위험도로 칠하고 표시를 옮긴다. 달라진 것이 없으면 다시 그리지 않는다.</summary>
        public void Show(string homeCode, IReadOnlyList<string> urgentCodes)
        {
            if (!HasMap) return;
            int version = CityRiskService.Loaded ? CityRiskService.Version : -1;
            if (version != _drawnVersion)
            {
                _drawnVersion = version;
                _mesh.SetVerticesDirty();
            }

            MapData data = MapData.Instance;
            if (homeCode != _drawnHome || !HomeMarked)
            {
                _drawnHome = homeCode;
                MapRegion region = homeCode != null ? data.FindRegion(homeCode) : null;
                _home.gameObject.SetActive(region != null);
                if (region != null) _home.anchoredPosition = PointOf(region);
            }

            int marked = 0;
            for (int i = 0; urgentCodes != null && i < urgentCodes.Count && marked < MarkerCount; i++)
            {
                // 우리 도시가 급하면 흰 점이 그 자리를 이미 알린다.
                if (urgentCodes[i] == homeCode) continue;
                MapRegion region = data.FindRegion(urgentCodes[i]);
                if (region == null) continue;
                _markers[marked].anchoredPosition = PointOf(region);
                _markers[marked].gameObject.SetActive(true);
                marked++;
            }
            for (int i = marked; i < MarkerCount; i++)
                if (_markers[i].gameObject.activeSelf) _markers[i].gameObject.SetActive(false);
            UrgentMarked = marked;
        }

        private Vector2 PointOf(MapRegion region) => _offset + new Vector2(region.labelX, region.labelY) * _scale;

        private void Populate(VertexHelper vh)
        {
            MapData data = MapData.Instance;
            bool loaded = CityRiskService.Loaded;
            int colored = 0;
            for (int r = 0; r < data.Regions.Count; r++)
            {
                MapRegion region = data.Regions[r];
                CityStatusDto city = loaded ? CityRiskService.Of(region.code) : null;
                Color color = Unknown;
                if (city != null)
                {
                    Color stage = CityRiskService.StageColor(city.stage);
                    color = new Color(stage.r * 0.86f, stage.g * 0.86f, stage.b * 0.86f, 1f);
                    colored++;
                }
                // 이웃한 지역의 밝기를 조금씩 달리해서 경계가 보이게 한다 (큰 지도와 같은 방법).
                float shade = 0.88f + 0.12f * ((r * 7) % 5) / 4f;
                MapPanel.AddRegion(vh, region, new Color(color.r * shade, color.g * shade, color.b * shade, 1f));
            }
            ColoredRegions = colored;
        }
    }
}
