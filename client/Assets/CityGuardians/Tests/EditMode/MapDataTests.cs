using System.Collections.Generic;
using CityGuardians.Domain;
using CityGuardians.Gameplay;
using NUnit.Framework;
using UnityEngine;

namespace CityGuardians.Tests
{
    /// <summary>지도 도형 데이터 (tools/map 이 만든 Resources/Data/kr_regions.json).</summary>
    public sealed class MapDataTests
    {
        [Test]
        public void MapData_MatchesTheRegionCatalog()
        {
            MapData data = MapData.Instance;
            Assert.IsFalse(data.IsEmpty, "지도 데이터가 없다. tools/map 에서 npm run build 를 실행한다");
            Assert.IsNull(data.FindMismatch(RegionCatalog.Default));
            Assert.AreEqual(KrRegions.BaseDate, data.File.baseDate, "지역 목록과 지도 데이터의 기준 날짜가 같아야 한다");
        }

        [Test]
        public void Attribution_NamesTheSourceAndBothLicences()
        {
            string text = MapData.Instance.File.attribution;
            StringAssert.Contains("통계청", text);
            StringAssert.Contains("SGIS", text);
            StringAssert.Contains("공공누리 제1유형", text);
            StringAssert.Contains("vuski/admdongkor", text);
            StringAssert.Contains("CC BY 4.0", text);
            StringAssert.Contains("단순화", text, "고쳐 썼다는 사실도 밝혀야 한다");
        }

        [Test]
        public void EveryRegion_CanBeDrawn_AndFitsInOneMesh()
        {
            MapData data = MapData.Instance;
            int vertices = 0;
            foreach (MapRegion region in data.Regions)
            {
                int count = region.v.Length / 2;
                vertices += count;
                Assert.GreaterOrEqual(count, 3, region.name);
                Assert.GreaterOrEqual(region.t.Length, 3, region.name);
                Assert.AreEqual(0, region.t.Length % 3, region.name);
                foreach (int index in region.t) Assert.IsTrue(index >= 0 && index < count, $"{region.name}: 삼각형이 없는 꼭짓점을 가리킨다");

                int inRings = 0;
                for (int i = 0; i + 1 < region.rings.Length; i += 2)
                {
                    Assert.IsTrue(region.rings[i] >= 0 && region.rings[i] + region.rings[i + 1] <= count, $"{region.name}: 고리가 꼭짓점 범위를 벗어난다");
                    inRings += region.rings[i + 1];
                }
                Assert.AreEqual(count, inRings, $"{region.name}: 모든 꼭짓점이 어느 고리에 속해야 한다");

                for (int i = 0; i < region.v.Length; i += 2)
                {
                    Assert.IsTrue(region.v[i] >= region.minX && region.v[i] <= region.maxX, region.name);
                    Assert.IsTrue(region.v[i + 1] >= region.minY && region.v[i + 1] <= region.maxY, region.name);
                    Assert.IsTrue(region.v[i] >= 0 && region.v[i] <= data.File.width, region.name);
                    Assert.IsTrue(region.v[i + 1] >= 0 && region.v[i + 1] <= data.File.height, region.name);
                }
            }
            // uGUI 메시 하나에 들어가는 꼭짓점은 65,000개가 한계다.
            Assert.Less(vertices, 60000, "지도 채우기가 메시 하나에 들어가야 한다. 넘으면 tools/map 의 단순화 비율을 낮춘다");
        }

        [Test]
        public void PointingAtARegionsLabel_FindsThatRegion()
        {
            MapData data = MapData.Instance;
            foreach (MapRegion region in data.Regions)
            {
                var label = new Vector2(region.labelX, region.labelY);
                MapRegion hit = data.RegionAt(label);
                Assert.IsNotNull(hit, $"{region.name} 의 이름표 자리가 어느 지역에도 속하지 않는다");
                Assert.AreEqual(region.code, hit.code, $"{region.name} 의 이름표 자리를 누르면 {hit.name} 이 골라진다");
            }
        }

        [Test]
        public void PointingAtASidosLabel_FindsARegionOfThatSido()
        {
            MapData data = MapData.Instance;
            foreach (MapSido sido in data.Sidos)
            {
                MapRegion hit = data.RegionAt(new Vector2(sido.labelX, sido.labelY));
                Assert.IsNotNull(hit, $"{sido.name} 의 이름표 자리가 바다다");
                Assert.AreEqual(sido.code, hit.sido, $"{sido.name} 의 이름표 자리를 누르면 다른 시·도의 {hit.name} 이 골라진다");

                Assert.IsTrue(sido.focusMinX >= sido.minX && sido.focusMaxX <= sido.maxX
                              && sido.focusMinY >= sido.minY && sido.focusMaxY <= sido.maxY, $"{sido.name} 의 확대 범위");
                Assert.Greater(sido.focusMaxX - sido.focusMinX, 0, sido.name);
                Assert.Greater(sido.focusMaxY - sido.focusMinY, 0, sido.name);
            }
        }

        [Test]
        public void OpenSea_IsNoRegion_ButNearTheCoastIsForgiving()
        {
            MapData data = MapData.Instance;
            Assert.IsNull(data.RegionAt(new Vector2(40f, 40f)));
            Assert.IsNull(data.RegionAt(new Vector2(40f, 40f), 100f));

            // 해운대구 바로 바깥(동쪽 바다)을 눌러도 여유 거리 안이면 해운대구가 골라진다
            MapRegion haeundae = data.FindRegion(RegionCodes.Haeundae);
            var justOutside = new Vector2(haeundae.maxX + 20f, haeundae.labelY);
            MapRegion near = data.RegionAt(justOutside, 120f);
            Assert.IsNotNull(near);
            Assert.AreEqual("26", near.sido);
        }

        [Test]
        public void Dokdo_IsOnTheMap()
        {
            MapData data = MapData.Instance;
            Assert.AreEqual(1, data.File.marks.Length);
            MapMark dokdo = data.File.marks[0];
            Assert.AreEqual("독도", dokdo.name);

            // 독도는 울릉군에 속하고, 울릉도보다 훨씬 동쪽에 있다
            MapRegion ulleung = data.FindRegion("47940");
            Assert.IsNotNull(ulleung);
            Assert.GreaterOrEqual(ulleung.rings.Length / 2, 2, "울릉도와 독도가 따로 그려져야 한다");
            Assert.Greater(dokdo.x, ulleung.labelX + 500, "독도 표시는 울릉도의 동쪽");
            Assert.LessOrEqual(dokdo.x, ulleung.maxX);
            Assert.IsTrue(dokdo.y >= ulleung.minY && dokdo.y <= ulleung.maxY);
        }

        [Test]
        public void EverySidoHasAColor()
        {
            var unknown = RegionColors.OfSido("없는코드");
            var seen = new HashSet<Color>();
            foreach (SidoDef sido in RegionCatalog.Default.Sidos)
            {
                Color color = RegionColors.OfSido(sido.code);
                Assert.AreNotEqual(unknown, color, $"{sido.name} 의 색이 정해지지 않았다");
                Assert.IsTrue(seen.Add(color), $"{sido.name} 의 색이 다른 시·도와 같다");
            }
            Assert.AreEqual(RegionColors.OfSido("26"), RegionColors.OfRegion(RegionCodes.Haeundae));
        }
    }
}
