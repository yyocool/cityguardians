using System;
using System.Collections.Generic;

namespace CityGuardians.Domain
{
    /// <summary>시·도 (특별시, 광역시, 도).</summary>
    [Serializable]
    public sealed class SidoDef
    {
        public string code;
        public string name;
        /// <summary>지도에 쓰는 짧은 이름. 예: "부산".</summary>
        public string shortName;
    }

    /// <summary>전입신고할 수 있는 지역 하나 (시·군·구). 구가 있는 시는 시 하나로 본다.</summary>
    [Serializable]
    public sealed class RegionDef
    {
        public string code;
        public string sidoCode;
        public string name;
    }

    public static class RegionCodes
    {
        /// <summary>1차 대표 도시 (docs/MAP_SYSTEM.md 5.2).</summary>
        public const string Haeundae = "26350";
    }

    public static class ResidencyStatus
    {
        public const string Stranger = "stranger";
        public const string Resident = "resident";
    }

    /// <summary>
    /// 지역 목록 (docs/MAP_SYSTEM.md 2장). 서버의 regions 테이블이 원본이 될 데이터이고,
    /// 지금은 tools/map 이 만든 목록(KrRegions)을 쓴다.
    /// </summary>
    public sealed class RegionCatalog
    {
        private static RegionCatalog _default;

        /// <summary>게임에 들어 있는 기본 목록.</summary>
        public static RegionCatalog Default =>
            _default ?? (_default = new RegionCatalog(KrRegions.CreateSidos(), KrRegions.CreateRegions()));

        private readonly List<SidoDef> _sidos;
        private readonly List<RegionDef> _regions;
        private readonly Dictionary<string, SidoDef> _sidoByCode = new Dictionary<string, SidoDef>();
        private readonly Dictionary<string, RegionDef> _regionByCode = new Dictionary<string, RegionDef>();

        public IReadOnlyList<SidoDef> Sidos => _sidos;
        public IReadOnlyList<RegionDef> Regions => _regions;

        public RegionCatalog(List<SidoDef> sidos, List<RegionDef> regions)
        {
            _sidos = sidos ?? throw new ArgumentNullException(nameof(sidos));
            _regions = regions ?? throw new ArgumentNullException(nameof(regions));
            for (int i = 0; i < _sidos.Count; i++)
            {
                if (_sidoByCode.ContainsKey(_sidos[i].code))
                    throw new ArgumentException("시·도 코드가 겹칩니다: " + _sidos[i].code);
                _sidoByCode.Add(_sidos[i].code, _sidos[i]);
            }
            for (int i = 0; i < _regions.Count; i++)
            {
                RegionDef region = _regions[i];
                if (_regionByCode.ContainsKey(region.code))
                    throw new ArgumentException("지역 코드가 겹칩니다: " + region.code);
                if (!_sidoByCode.ContainsKey(region.sidoCode))
                    throw new ArgumentException($"지역 {region.code} 의 시·도 {region.sidoCode} 가 목록에 없습니다.");
                _regionByCode.Add(region.code, region);
            }
        }

        /// <summary>없으면 null.</summary>
        public SidoDef FindSido(string code) =>
            code != null && _sidoByCode.TryGetValue(code, out SidoDef sido) ? sido : null;

        /// <summary>없으면 null.</summary>
        public RegionDef FindRegion(string code) =>
            code != null && _regionByCode.TryGetValue(code, out RegionDef region) ? region : null;

        /// <summary>"부산광역시 해운대구". 시·도와 이름이 같은 지역(세종시)은 한 번만 쓴다. 없는 지역이면 null.</summary>
        public string FullName(string regionCode)
        {
            RegionDef region = FindRegion(regionCode);
            if (region == null) return null;
            SidoDef sido = FindSido(region.sidoCode);
            return IsSameAsSido(region, sido) ? sido.name : sido.name + " " + region.name;
        }

        /// <summary>"부산 해운대구". 좁은 자리에 쓰는 이름. 없는 지역이면 null.</summary>
        public string ShortName(string regionCode)
        {
            RegionDef region = FindRegion(regionCode);
            if (region == null) return null;
            SidoDef sido = FindSido(region.sidoCode);
            return IsSameAsSido(region, sido) ? region.name : sido.shortName + " " + region.name;
        }

        public int CountIn(string sidoCode)
        {
            int count = 0;
            for (int i = 0; i < _regions.Count; i++)
                if (_regions[i].sidoCode == sidoCode) count++;
            return count;
        }

        // 시·군·구가 하나뿐인 시·도(세종특별자치시)는 지역이 곧 시·도다.
        private bool IsSameAsSido(RegionDef region, SidoDef sido) => CountIn(sido.code) == 1;
    }
}
