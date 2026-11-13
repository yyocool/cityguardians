using System;
using System.Collections.Generic;
using CityGuardians.Domain;
using CityGuardians.Gameplay;
using CityGuardians.Services;
using NUnit.Framework;
using UnityEngine;

namespace CityGuardians.Tests
{
    /// <summary>지역 목록 (docs/MAP_SYSTEM.md 2장).</summary>
    public sealed class RegionCatalogTests
    {
        private static readonly string[] ProvincesWithGeneralGu = { "41", "43", "44", "47", "48", "52" };

        [Test]
        public void Catalog_HasSixteenSidos_AndTwoHundredThirtyRegions()
        {
            RegionCatalog catalog = RegionCatalog.Default;
            Assert.AreEqual(16, catalog.Sidos.Count);
            Assert.AreEqual(230, catalog.Regions.Count);

            int total = 0;
            foreach (SidoDef sido in catalog.Sidos)
            {
                Assert.IsNotEmpty(sido.shortName, sido.name);
                int count = catalog.CountIn(sido.code);
                Assert.Greater(count, 0, $"{sido.name} 에 지역이 없다");
                total += count;
            }
            Assert.AreEqual(catalog.Regions.Count, total, "모든 지역이 어느 시·도에 속해야 한다");
        }

        [Test]
        public void Names_AreWrittenTheWayPeopleSayThem()
        {
            RegionCatalog catalog = RegionCatalog.Default;
            Assert.AreEqual("부산광역시 해운대구", catalog.FullName(RegionCodes.Haeundae));
            Assert.AreEqual("부산 해운대구", catalog.ShortName(RegionCodes.Haeundae));
            // 시·군·구가 하나뿐인 세종은 이름을 두 번 쓰지 않는다
            Assert.AreEqual("세종특별자치시", catalog.FullName("36110"));
            Assert.AreEqual("세종시", catalog.ShortName("36110"));
            Assert.IsNull(catalog.FullName("99999"));
            Assert.IsNull(catalog.FindRegion(null));
        }

        [Test]
        public void CitiesWithDistricts_AreOneRegionEach()
        {
            RegionCatalog catalog = RegionCatalog.Default;
            // 수원시의 네 구는 수원시 하나로 합쳐져 있다
            Assert.AreEqual("수원시", catalog.FindRegion("41110").name);
            Assert.IsNull(catalog.FindRegion("41111"), "수원시 장안구는 따로 고를 수 없다");

            foreach (RegionDef region in catalog.Regions)
            {
                if (Array.IndexOf(ProvincesWithGeneralGu, region.sidoCode) < 0) continue;
                Assert.IsFalse(region.name.EndsWith("구") && region.name.Contains("시"),
                    $"도에 속한 시의 구가 합쳐지지 않았다: {region.code} {region.name}");
            }

            // 특별시·광역시의 자치구는 그대로다
            Assert.AreEqual(25, catalog.CountIn("11"), "서울의 구");
            Assert.AreEqual(16, catalog.CountIn("26"), "부산의 구·군");
        }

        [Test]
        public void SameNameInDifferentSidos_HasDifferentCodes()
        {
            RegionCatalog catalog = RegionCatalog.Default;
            var seen = new HashSet<string>();
            int junggu = 0;
            foreach (RegionDef region in catalog.Regions)
            {
                Assert.IsTrue(seen.Add(region.code), "코드가 겹친다: " + region.code);
                if (region.name == "중구") junggu++;
            }
            Assert.Greater(junggu, 1, "'중구'는 여러 도시에 있다. 이름이 아니라 코드로 구별해야 한다");
        }
    }

    /// <summary>닉네임 규칙 (docs/RESIDENCY_SYSTEM.md 5장).</summary>
    public sealed class NicknameRulesTests
    {
        [TestCase("별빛가디언")]
        [TestCase("고래")]
        [TestCase("Guardian")]
        [TestCase("파도2026")]
        [TestCase("a1")]
        [TestCase("  푸른고래  ", Description = "앞뒤 공백은 떼고 본다")]
        public void GoodNames_Pass(string name)
        {
            Assert.IsNull(NicknameRules.Check(name), NicknameRules.Message(NicknameRules.Check(name)));
        }

        [TestCase(null, NicknameRules.TooShort)]
        [TestCase("", NicknameRules.TooShort)]
        [TestCase("가", NicknameRules.TooShort)]
        [TestCase("가나다라마바사아자", NicknameRules.TooLong)]
        [TestCase("푸른 고래", NicknameRules.BadCharacter, Description = "띄어쓰기")]
        [TestCase("고래!", NicknameRules.BadCharacter)]
        [TestCase("ㅋㅋㅋ", NicknameRules.BadCharacter, Description = "낱자")]
        [TestCase("고래_1", NicknameRules.BadCharacter)]
        [TestCase("こんにちは", NicknameRules.BadCharacter)]
        [TestCase("1234", NicknameRules.DigitsOnly)]
        [TestCase("010123가", NicknameRules.LooksLikeContact, Description = "숫자가 다섯 개 넘게 이어지면 연락처로 본다")]
        [TestCase("운영자님", NicknameRules.Reserved)]
        [TestCase("GM고래", NicknameRules.Reserved)]
        [TestCase("Admin1", NicknameRules.Reserved, Description = "대소문자를 가리지 않는다")]
        [TestCase("보리", NicknameRules.Reserved, Description = "게임 속 인물의 이름")]
        [TestCase("이방인77", NicknameRules.Reserved, Description = "게스트 이름처럼 보이면 안 된다")]
        [TestCase("바보고래", NicknameRules.Banned)]
        public void BadNames_AreRefused_WithTheReason(string name, string expected)
        {
            Assert.AreEqual(expected, NicknameRules.Check(name));
            Assert.IsNotEmpty(NicknameRules.Message(expected));
        }

        [Test]
        public void FourDigitsInARow_AreStillAllowed()
        {
            Assert.IsNull(NicknameRules.Check("고래2026"));
            Assert.AreEqual(NicknameRules.LooksLikeContact, NicknameRules.Check("고래20261"));
        }

        [Test]
        public void Key_IgnoresCaseAndOuterSpaces()
        {
            Assert.AreEqual(NicknameRules.Key("Blue고래"), NicknameRules.Key("  bLUE고래 "));
            Assert.AreNotEqual(NicknameRules.Key("고래"), NicknameRules.Key("고래1"));
        }

        [Test]
        public void EverySuggestedName_PassesTheRules()
        {
            int usable = 0;
            foreach (string first in NicknameSuggester.Firsts)
            foreach (string second in NicknameSuggester.Seconds)
            {
                string name = first + second;
                if (name.Length > NicknameRules.MaxLength) continue;
                Assert.IsNull(NicknameRules.Check(name), $"추천 이름 '{name}': {NicknameRules.Message(NicknameRules.Check(name))}");
                usable++;
            }
            Assert.Greater(usable, 300, "추천할 이름이 넉넉해야 한다");
        }

        [Test]
        public void Suggest_GivesDifferentNames_AndSkipsTheExcluded()
        {
            var random = new System.Random(3);
            List<string> first = NicknameSuggester.Suggest(random, 3);
            Assert.AreEqual(3, first.Count);
            CollectionAssert.AllItemsAreUnique(first);

            for (int i = 0; i < 50; i++)
            {
                List<string> next = NicknameSuggester.Suggest(random, 3, first);
                Assert.AreEqual(3, next.Count);
                foreach (string name in next) CollectionAssert.DoesNotContain(first, name);
            }
        }
    }

    /// <summary>모의 서버의 지역 상태와 전입신고 (docs/RESIDENCY_SYSTEM.md 4, 5장).</summary>
    public sealed class LocalMockBackendResidencyTests
    {
        private sealed class FakeClock : IClock
        {
            public DateTime UtcNow { get; private set; } = new DateTime(2026, 11, 13, 3, 0, 0, DateTimeKind.Utc);
            public void Advance(double seconds) => UtcNow = UtcNow.AddSeconds(seconds);
        }

        private MemoryTextStore _store;
        private FakeClock _clock;

        [SetUp]
        public void SetUp()
        {
            _store = new MemoryTextStore();
            _clock = new FakeClock();
        }

        private LocalMockBackend NewBackend() => new LocalMockBackend(_store, _clock, null, 7);

        private static T Ok<T>(ApiResult<T> result)
        {
            Assert.IsNotNull(result, "응답 콜백이 불리지 않았다");
            Assert.IsTrue(result.ok, $"실패 응답: {result.errorCode} {result.message}");
            return result.data;
        }

        private static MeDto Boot(LocalMockBackend backend)
        {
            ApiResult<MeDto> result = null;
            backend.Bootstrap(r => result = r);
            return Ok(result);
        }

        private static MeDto Me(LocalMockBackend backend)
        {
            ApiResult<MeDto> result = null;
            backend.GetMe(r => result = r);
            return Ok(result);
        }

        private static ApiResult<ResidencyResultDto> Register(LocalMockBackend backend, string region, string nickname)
        {
            ApiResult<ResidencyResultDto> result = null;
            backend.RegisterResidency(region, nickname, r => result = r);
            Assert.IsNotNull(result, "응답 콜백이 불리지 않았다");
            return result;
        }

        private static NicknameCheckDto Check(LocalMockBackend backend, string nickname)
        {
            ApiResult<NicknameCheckDto> result = null;
            backend.CheckNickname(nickname, r => result = r);
            return Ok(result);
        }

        private static List<RegionStatusDto> Regions(LocalMockBackend backend)
        {
            ApiResult<RegionsDto> result = null;
            backend.GetRegions(r => result = r);
            return Ok(result).regions;
        }

        private BattleResultDto Win(LocalMockBackend backend)
        {
            ApiResult<BattleSessionDto> started = null;
            backend.StartBattle(StageCodes.Outskirts1, r => started = r);
            string session = Ok(started).sessionId;
            _clock.Advance(300);
            StageDef stage = backend.Defs.FindStage(StageCodes.Outskirts1);
            ApiResult<BattleResultDto> finished = null;
            backend.FinishBattle(session, BattleRulesTests.WinReport(stage), r => finished = r);
            return Ok(finished);
        }

        [Test]
        public void NewGuest_IsAStranger_AndNoCityHasAnyone()
        {
            LocalMockBackend backend = NewBackend();
            MeDto me = Boot(backend);
            Assert.AreEqual(ResidencyStatus.Stranger, me.residency);
            Assert.IsTrue(string.IsNullOrEmpty(me.regionCode));
            Assert.AreEqual(0, Regions(backend).Count, "아무도 없는 지역은 목록에 넣지 않는다 (꾸며낸 수치가 없다)");
            Assert.AreSame(RegionCatalog.Default, backend.Regions);
        }

        [Test]
        public void Register_MakesAResident_PaysTheBonusThroughTheLedger_AndCountsTheResident()
        {
            LocalMockBackend backend = NewBackend();
            Boot(backend);
            int bonus = backend.Defs.economy.residencyBonusGc;
            Assert.AreEqual(500, bonus, "docs/RESIDENCY_SYSTEM.md 5장의 전입 축하금");

            ResidencyResultDto result = Ok(Register(backend, RegionCodes.Haeundae, "  푸른고래 "));
            Assert.AreEqual(RegionCodes.Haeundae, result.regionCode);
            Assert.AreEqual("푸른고래", result.nickname, "앞뒤 공백은 떼고 등록한다");
            Assert.AreEqual(bonus, result.bonusGc);
            Assert.AreEqual(1, result.region.residents);

            MeDto me = result.me;
            Assert.AreEqual(ResidencyStatus.Resident, me.residency);
            Assert.AreEqual(RegionCodes.Haeundae, me.regionCode);
            Assert.AreEqual("푸른고래", me.displayName);
            Assert.AreEqual(bonus, me.gc);

            Assert.AreEqual(1, backend.CurrentLedger.Count);
            LedgerEntry entry = backend.CurrentLedger[0];
            Assert.AreEqual(bonus, entry.delta);
            Assert.AreEqual(LedgerReasons.ResidencyBonus, entry.reason);
            Assert.AreEqual(RegionCodes.Haeundae, entry.reference);
            Assert.IsNull(backend.FindLedgerInconsistency());

            List<RegionStatusDto> regions = Regions(backend);
            Assert.AreEqual(1, regions.Count);
            Assert.AreEqual(RegionCodes.Haeundae, regions[0].regionCode);
            Assert.AreEqual(1, regions[0].residents);
            Assert.AreEqual(0, regions[0].defensePoints);
        }

        [Test]
        public void Residency_SurvivesARestart()
        {
            LocalMockBackend first = NewBackend();
            Boot(first);
            Ok(Register(first, RegionCodes.Haeundae, "푸른고래"));

            LocalMockBackend second = NewBackend();
            MeDto me = Boot(second);
            Assert.AreEqual(ResidencyStatus.Resident, me.residency);
            Assert.AreEqual(RegionCodes.Haeundae, me.regionCode);
            Assert.AreEqual("푸른고래", me.displayName);
            Assert.AreEqual(second.Defs.economy.residencyBonusGc, me.gc);
            Assert.AreEqual(1, Regions(second)[0].residents);
            Assert.IsNull(second.FindLedgerInconsistency());
        }

        [Test]
        public void RegisteringAgain_IsRefused_AndPaysNothing()
        {
            LocalMockBackend backend = NewBackend();
            Boot(backend);
            Ok(Register(backend, RegionCodes.Haeundae, "푸른고래"));
            long gc = Me(backend).gc;

            ApiResult<ResidencyResultDto> again = Register(backend, "11140", "다른이름");
            Assert.IsFalse(again.ok);
            Assert.AreEqual(ApiErrors.AlreadyResident, again.errorCode);

            MeDto me = Me(backend);
            Assert.AreEqual(gc, me.gc, "축하금은 한 번만 받는다");
            Assert.AreEqual(RegionCodes.Haeundae, me.regionCode, "도시는 이사로만 바꿀 수 있다");
            Assert.AreEqual("푸른고래", me.displayName);
            Assert.AreEqual(1, backend.CurrentLedger.Count);
            List<RegionStatusDto> regions = Regions(backend);
            Assert.AreEqual(1, regions.Count, "거절된 신고로 다른 지역의 주민 수가 늘면 안 된다");
            Assert.AreEqual(1, regions[0].residents);
        }

        [Test]
        public void UnknownRegion_IsRefused_AndNothingChanges()
        {
            LocalMockBackend backend = NewBackend();
            Boot(backend);

            foreach (string region in new[] { "99999", "41111", "", null })
            {
                ApiResult<ResidencyResultDto> result = Register(backend, region, "푸른고래");
                Assert.IsFalse(result.ok, $"지역 '{region}'");
                Assert.AreEqual(ApiErrors.NotFound, result.errorCode);
            }

            MeDto me = Me(backend);
            Assert.AreEqual(ResidencyStatus.Stranger, me.residency);
            Assert.AreEqual(0, me.gc);
            Assert.AreEqual(0, backend.CurrentLedger.Count);
            Assert.AreEqual(0, Regions(backend).Count);
        }

        [Test]
        public void BadNickname_IsRefused_AndNothingChanges()
        {
            LocalMockBackend backend = NewBackend();
            MeDto before = Boot(backend);

            foreach (string name in new[] { "가", "운영자", "01012345", "고래!!", null })
            {
                ApiResult<ResidencyResultDto> result = Register(backend, RegionCodes.Haeundae, name);
                Assert.IsFalse(result.ok, $"이름 '{name}'");
                Assert.AreEqual(ApiErrors.NicknameInvalid, result.errorCode);
                Assert.IsNotEmpty(result.message, "왜 안 되는지 알려 줘야 한다");
            }

            MeDto me = Me(backend);
            Assert.AreEqual(ResidencyStatus.Stranger, me.residency);
            Assert.AreEqual(before.displayName, me.displayName);
            Assert.AreEqual(0, me.gc);
            Assert.AreEqual(0, Regions(backend).Count);
        }

        [Test]
        public void NicknameTakenByAnotherPlayer_IsRefused_EvenInDifferentCase()
        {
            LocalMockBackend backend = NewBackend();
            MeDto firstPlayer = Boot(backend);
            Ok(Register(backend, RegionCodes.Haeundae, "Blue고래"));
            Assert.IsTrue(Check(backend, "Blue고래").available, "내가 쓰는 이름은 나에게는 겹치지 않는다");

            // 같은 서버에 두 번째 플레이어가 생긴다
            ApiResult<MeDto> startedOver = null;
            backend.StartOverAsNewGuest(r => startedOver = r);
            MeDto secondPlayer = Ok(startedOver);
            Assert.AreNotEqual(firstPlayer.playerId, secondPlayer.playerId);
            Assert.AreEqual(ResidencyStatus.Stranger, secondPlayer.residency);

            NicknameCheckDto check = Check(backend, "blue고래");
            Assert.IsFalse(check.available);
            Assert.AreEqual(NicknameRules.Taken, check.reason);
            Assert.IsNotEmpty(check.message);

            ApiResult<ResidencyResultDto> taken = Register(backend, RegionCodes.Haeundae, "BLUE고래");
            Assert.IsFalse(taken.ok);
            Assert.AreEqual(ApiErrors.NicknameTaken, taken.errorCode);
            Assert.AreEqual(ResidencyStatus.Stranger, Me(backend).residency);

            // 다른 이름으로는 같은 도시에 들어갈 수 있고, 주민 수가 둘이 된다
            ResidencyResultDto ok = Ok(Register(backend, RegionCodes.Haeundae, "빛나는수달"));
            Assert.AreEqual(2, ok.region.residents);
            Assert.AreEqual(backend.Defs.economy.residencyBonusGc, ok.me.gc, "축하금은 계정마다 한 번");
            Assert.IsNull(backend.FindLedgerInconsistency());
        }

        [Test]
        public void CheckNickname_SaysWhy()
        {
            LocalMockBackend backend = NewBackend();
            Boot(backend);

            NicknameCheckDto good = Check(backend, " 별빛가디언 ");
            Assert.IsTrue(good.available);
            Assert.AreEqual("별빛가디언", good.nickname);
            Assert.IsTrue(string.IsNullOrEmpty(good.reason));

            NicknameCheckDto bad = Check(backend, "운영자");
            Assert.IsFalse(bad.available);
            Assert.AreEqual(NicknameRules.Reserved, bad.reason);
            Assert.IsNotEmpty(bad.message);
        }

        [Test]
        public void ResidentWin_AddsDefensePointsToTheirCity_StrangerWinDoesNot()
        {
            LocalMockBackend backend = NewBackend();
            Boot(backend);
            StageDef stage = backend.Defs.FindStage(StageCodes.Outskirts1);
            Assert.Greater(stage.winDefensePoints, 0);

            // 이방인으로 이기면 쌓일 도시가 없다
            BattleResultDto asStranger = Win(backend);
            Assert.IsTrue(asStranger.accepted, asStranger.rejectReason);
            Assert.AreEqual(0, asStranger.defensePoints);
            Assert.IsTrue(string.IsNullOrEmpty(asStranger.regionCode));
            Assert.AreEqual(0, Regions(backend).Count);

            Ok(Register(backend, RegionCodes.Haeundae, "푸른고래"));
            BattleResultDto asResident = Win(backend);
            Assert.AreEqual(stage.winDefensePoints, asResident.defensePoints);
            Assert.AreEqual(RegionCodes.Haeundae, asResident.regionCode);
            Win(backend);

            List<RegionStatusDto> regions = Regions(backend);
            Assert.AreEqual(1, regions.Count);
            Assert.AreEqual(stage.winDefensePoints * 2, regions[0].defensePoints);
            Assert.AreEqual(1, regions[0].residents);

            // 진 전투로는 쌓이지 않는다
            ApiResult<BattleSessionDto> started = null;
            backend.StartBattle(StageCodes.Outskirts1, r => started = r);
            _clock.Advance(5);
            ApiResult<BattleResultDto> lost = null;
            backend.FinishBattle(Ok(started).sessionId,
                new BattleReport { result = BattleResults.Lose, wavesCleared = 0, baseHpLeft = stage.baseHp }, r => lost = r);
            Assert.AreEqual(0, Ok(lost).defensePoints);
            Assert.AreEqual(stage.winDefensePoints * 2, Regions(backend)[0].defensePoints);
            Assert.IsNull(backend.FindLedgerInconsistency());
        }

        [Test]
        public void OldSaveWithoutRegions_StillLoads()
        {
            // 지역 기능이 생기기 전의 저장 데이터: regions 목록도, 플레이어의 지역 칸도 없다.
            LocalMockBackend first = NewBackend();
            MeDto before = Boot(first);
            string saved = _store.Read(LocalMockBackend.ServerFile);
            int at = saved.IndexOf(",\"regions\":[", StringComparison.Ordinal);
            Assert.Greater(at, 0, "저장 형식이 바뀌었다면 이 테스트도 고쳐야 한다: " + saved);
            int end = saved.IndexOf(']', at);
            _store.Write(LocalMockBackend.ServerFile, saved.Remove(at, end - at + 1));

            LocalMockBackend second = NewBackend();
            MeDto me = Boot(second);
            Assert.AreEqual(before.playerId, me.playerId);
            Assert.AreEqual(ResidencyStatus.Stranger, me.residency);
            Assert.AreEqual(0, Regions(second).Count);
            Ok(Register(second, RegionCodes.Haeundae, "푸른고래"));
            Assert.AreEqual(1, Regions(second)[0].residents);
        }

        [Test]
        public void BeforeBootstrap_EverythingIsRefused()
        {
            LocalMockBackend backend = NewBackend();
            ApiResult<RegionsDto> regions = null;
            backend.GetRegions(r => regions = r);
            Assert.AreEqual(ApiErrors.Unauthorized, regions.errorCode);

            ApiResult<NicknameCheckDto> check = null;
            backend.CheckNickname("푸른고래", r => check = r);
            Assert.AreEqual(ApiErrors.Unauthorized, check.errorCode);

            Assert.AreEqual(ApiErrors.Unauthorized, Register(backend, RegionCodes.Haeundae, "푸른고래").errorCode);
        }
    }

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
