using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using CityGuardians.Domain;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace CityGuardians.Vectors
{
    /// <summary>
    /// 클라이언트의 규칙(C#)에서 표본을 뽑는다. 서버(PHP)의 규칙이 같은 결과를 내는지 확인하는 기준이다.
    /// 결과는 늘 같다 (씨앗이 고정된 난수). 규칙을 고치면 다시 실행해 표본을 갱신한다.
    /// </summary>
    internal static class Program
    {
        private const int WorldSeed = 20261113;
        private static readonly List<object> Cases = new List<object>();
        private static readonly List<GearDef> ExtraGear = new List<GearDef>();
        private static Random _random = new Random(8001);

        private static int Main(string[] args)
        {
            string outDir = args.Length > 0
                ? args[0]
                : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "server", "tests", "fixtures"));
            Directory.CreateDirectory(outDir);

            GameDefs defs = DefaultDefs.Create();
            RegionCatalog catalog = RegionCatalog.Default;

            Write(outDir, "defs.json", defs);
            Write(outDir, "regions.json", new { baseDate = KrRegions.BaseDate, sidos = catalog.Sidos, regions = catalog.Regions });

            Roll();
            Invasions(defs, catalog);
            Risk(defs);
            Ranking(defs);
            Gear(defs);
            Battle(defs);
            Fps(defs);
            Hunt(defs);
            Levels(defs);
            Nicknames();
            Support(defs, catalog);

            Write(outDir, "vectors.json", new { seed = WorldSeed, count = Cases.Count, extraGear = ExtraGear, cases = Cases });
            Console.WriteLine($"표본 {Cases.Count}개를 {outDir} 에 썼습니다.");
            return 0;
        }

        private static void Add(string fn, object input, object output) => Cases.Add(new { fn, @in = input, @out = output });

        private static int Next(int min, int maxInclusive) => _random.Next(min, maxInclusive + 1);

        private static T Pick<T>(IList<T> list) => list[_random.Next(list.Count)];

        // ── 판정 난수 ───────────────────────────────────────────

        private static void Roll()
        {
            string[] codes = { "26350", "11110", "50110", "36110", "41110", "", "A1", "zz9" };
            long[] ticks = { 0, 1, 2, 77, 7599, 123456, 4294967295L, 4294967296L, 9876543210L, 281474976710655L };
            int[] modulos = { 1, 2, 3, 25, 100, 1000, 65536, 1000003 };
            for (int i = 0; i < 400; i++)
            {
                int seed = i % 5 == 0 ? Next(0, int.MaxValue - 1) : WorldSeed;
                string code = Pick(codes);
                long tick = i < 80 ? Pick(ticks) : Next(0, 2000000);
                int salt = Next(0, 9);
                int modulo = Pick(modulos);
                Add("roll", new { seed, code, tick, salt, modulo }, InvasionRules.Roll(seed, code, tick, salt, modulo));
            }
        }

        // ── 침공 ────────────────────────────────────────────────

        private static void Invasions(GameDefs defs, RegionCatalog catalog)
        {
            RiskPolicy policy = defs.combat.risk;
            // 모든 도시의 처음 위험도.
            var initial = new List<object>();
            for (int i = 0; i < catalog.Regions.Count; i++)
            {
                string code = catalog.Regions[i].code;
                int spread = policy.initialSpread;
                int points = policy.initialRisk + InvasionRules.Roll(WorldSeed, code, 0, 9, spread * 2 + 1) - spread;
                initial.Add(new { code, centi = CityRiskRules.Clamp(points * 100) });
            }
            Add("initialRisk", new { policy = Diff(policy) }, initial);

            DateTime[] times =
            {
                new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 1, 1, 0, 59, 59, DateTimeKind.Utc),
                new DateTime(2026, 11, 13, 9, 30, 0, DateTimeKind.Utc), new DateTime(2027, 3, 2, 23, 0, 1, DateTimeKind.Utc),
                new DateTime(2030, 12, 31, 23, 59, 59, DateTimeKind.Utc),
            };
            int[] tickMinutes = { 60, 15, 1, 240 };
            foreach (DateTime time in times)
            foreach (int minutes in tickMinutes)
            {
                var p = RiskPolicy.CreateDefault();
                p.tickMinutes = minutes;
                long tick = InvasionRules.TickOf(time, p);
                Add("tickOf", new { unix = ((DateTimeOffset)time).ToUnixTimeSeconds(), tickMinutes = minutes },
                    new { tick, startUnix = ((DateTimeOffset)InvasionRules.TickStart(tick, p)).ToUnixTimeSeconds() });
            }

            // 침공이 시작되는가. 기본 정책(4%)과 잦은 정책(60%) 둘 다.
            var frequent = RiskPolicy.CreateDefault();
            frequent.invasionChancePermille = 600;
            frequent.zeroEventPermille = 150;
            frequent.invasionMinutes = 45;
            foreach (RiskPolicy p in new[] { policy, frequent })
            {
                for (int i = 0; i < 260; i++)
                {
                    string code = catalog.Regions[_random.Next(catalog.Regions.Count)].code;
                    long tick = Next(7000, 9000);
                    InvasionEvent started = InvasionRules.TryStart(p, code, tick, WorldSeed);
                    Add("tryStart", new { policy = Diff(p), code, tick, seed = WorldSeed }, started == null
                        ? null
                        : new
                        {
                            started.id, started.kind, started.severity,
                            startUnix = Unix(started.startedAt), endUnix = Unix(started.endsAt),
                        });
                }
            }

            for (int i = 0; i < 120; i++)
            {
                var p = RiskPolicy.CreateDefault();
                p.activePlayerNorm = Pick(new[] { 20, 1, 7, 50 });
                int severity = Next(0, 4);
                int active = Next(0, 400);
                Add("requiredWins", new { policy = Diff(p), severity, active }, InvasionRules.RequiredWins(p, severity, active));
            }

            string[] kinds = { InvasionKinds.Outskirts, InvasionKinds.Intrusion, InvasionKinds.Zero };
            for (int i = 0; i < 200; i++)
            {
                var p = RiskPolicy.CreateDefault();
                if (i % 4 == 1) { p.npcHoldBasePercent = Next(0, 100); p.npcHoldSeverityPenaltyPercent = Next(0, 40); }
                var invasion = new InvasionEvent
                {
                    id = "inv_x", regionCode = catalog.Regions[_random.Next(catalog.Regions.Count)].code,
                    kind = Pick(kinds), severity = Next(1, 3), status = InvasionStatus.Active,
                    required = Next(0, 6), progress = Next(0, 7),
                };
                int power = Next(0, 900);
                long tick = Next(1, 100000);
                Add("npcHold", new { policy = Diff(p), power, invasion, tick, seed = WorldSeed }, new
                {
                    percent = InvasionRules.NpcHoldPercent(p, power, invasion),
                    status = InvasionRules.ResolveExpired(p, invasion, power, WorldSeed, tick),
                });
            }

            string[] modes = { CombatModeCodes.Defense, CombatModeCodes.Hunt, CombatModeCodes.Fps, "x" };
            string[] statuses = { InvasionStatus.Active, InvasionStatus.Repelled, InvasionStatus.NpcHeld, InvasionStatus.Failed };
            foreach (string kind in kinds)
            foreach (string mode in modes)
            foreach (string status in statuses)
            {
                var invasion = new InvasionEvent { kind = kind, status = status, severity = 1 };
                Add("invasionCounts", new { kind, status, mode }, new
                {
                    counts = InvasionRules.Counts(invasion, mode),
                    startReason = InvasionRules.StartReason(kind),
                    winReason = InvasionRules.WinReason(mode),
                    repelReason = InvasionRules.RepelReason(kind),
                    failReason = InvasionRules.FailReason(kind),
                });
            }
        }

        private static long Unix(string iso) =>
            ((DateTimeOffset)DateTime.Parse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime()).ToUnixTimeSeconds();

        // ── 위험도 ──────────────────────────────────────────────

        private static void Risk(GameDefs defs)
        {
            string[] reasons =
            {
                RiskReasons.InvasionStarted, RiskReasons.DefenseFailed, RiskReasons.IntrusionFailed, RiskReasons.FacilityDamaged,
                RiskReasons.EmergencyUnresolved, RiskReasons.ZeroEvent, RiskReasons.DefenseWon, RiskReasons.HuntCompleted,
                RiskReasons.FpsRepelled, RiskReasons.FacilityRepaired, RiskReasons.JointDefense, RiskReasons.SpecialMission,
                RiskReasons.InvasionRepelled, RiskReasons.NpcHeld, RiskReasons.DailyStabilize, "unknown_reason",
            };

            for (int i = 0; i < 60; i++)
            {
                var p = RiskPolicy.CreateDefault();
                if (i % 3 == 0) { p.cautionAt = Next(5, 30); p.dangerAt = Next(31, 60); p.emergencyAt = Next(61, 95); }
                int centi = i < 10 ? new[] { -5, 0, 2499, 2500, 4999, 5000, 7499, 7500, 10000, 12000 }[i] : Next(-200, 10300);
                Add("stage", new { policy = Diff(p), centi }, new
                {
                    clamp = CityRiskRules.Clamp(centi), points = CityRiskRules.ToPoints(centi), stage = CityRiskRules.StageOf(centi, p),
                    name = RiskStages.DisplayName(CityRiskRules.StageOf(centi, p)), icon = RiskStages.Icon(CityRiskRules.StageOf(centi, p)),
                    order = RiskStages.Order(CityRiskRules.StageOf(centi, p)),
                });
            }

            for (int i = 0; i < 200; i++)
            {
                RiskPolicy p = RiskPolicy.CreateDefault();
                int current = Next(0, 10000);
                string reason = Pick(reasons);
                int quantity = Next(-1, 4);
                RiskChange change = CityRiskRules.ApplySystem(current, reason, quantity, p);
                Add("applySystem", new { policy = Diff(p), current, reason, quantity }, ChangeOf(change));
            }

            for (int i = 0; i < 100; i++)
            {
                RiskPolicy p = RiskPolicy.CreateDefault();
                int current = Next(0, 10000);
                int delta = Next(-12000, 12000);
                Add("applyExact", new { policy = Diff(p), current, delta },
                    ChangeOf(CityRiskRules.ApplyExact(current, RiskReasons.AdminAdjust, delta, p)));
            }

            for (int i = 0; i < 400; i++)
            {
                RiskPolicy p = RiskPolicy.CreateDefault();
                if (i % 5 == 2) { p.activePlayerNorm = Next(1, 60); p.dailyPlayerReduceCap = Next(0, 40); }
                if (i % 17 == 3) p.SetDelta(RiskReasons.DefenseWon, 500);   // 잘못 고친 정책: 플레이어의 활동이 올리지는 않는다
                int current = Next(0, 10000);
                string reason = Pick(reasons);
                int units = Pick(new[] { 100, 100, 50, 200, 300, 0, -100, 1000, 37 });
                int active = Next(0, 500);
                int reducedToday = Next(-50, 2600);
                RiskChange change = CityRiskRules.ApplyPlayer(current, reason, units, p, active, reducedToday, out int newReduced);
                Add("applyPlayer", new { policy = Diff(p), current, reason, units, active, reducedToday },
                    new { change = ChangeOf(change), reducedToday = newReduced, scale = CityRiskRules.ScalePercent(p, active) });
            }

            for (int i = 0; i < 80; i++)
            {
                RiskPolicy p = RiskPolicy.CreateDefault();
                if (i % 4 == 0) p.dailyStabilizePercent = Next(-5, 100);
                int current = Next(-100, 10100);
                long points = i % 7 == 0 ? Next(-50, 50) : Next(0, 60000);
                int structurePower = Next(-10, 400);
                Add("stabilizeAndPower", new { policy = Diff(p), current, defensePoints = points, structurePower }, new
                {
                    delta = CityRiskRules.DailyStabilizeDelta(current, p),
                    power = CityRiskRules.DefensePower(p, points, structurePower),
                });
            }

            foreach (string reason in reasons)
                Add("reasonName", new { reason }, RiskReasons.DisplayName(reason));
        }

        private static object ChangeOf(RiskChange change) => new
        {
            change.reason, change.before, change.after, change.stageBefore, change.stageAfter,
            delta = change.Delta, changed = change.Changed, stageChanged = change.StageChanged,
        };

        // ── 랭킹 ────────────────────────────────────────────────

        private static void Ranking(GameDefs defs)
        {
            for (int round = 0; round < 12; round++)
            {
                var weights = new RankingWeights();
                if (round % 3 == 1) { weights.safety = Next(0, 50); weights.successRate = Next(0, 50); weights.cooperation = Next(0, 50); }
                if (round == 5) { weights.safety = 0; weights.successRate = 0; weights.contribution = 0; weights.response = 0; weights.cooperation = 0; }
                var all = new List<RegionStats>();
                int count = Next(1, 24);
                for (int i = 0; i < count; i++)
                {
                    int attempts = Next(0, 60);
                    int total = Next(0, 30);
                    all.Add(new RegionStats
                    {
                        regionCode = (11000 + round * 100 + i * 7).ToString(),
                        residents = Next(0, 300),
                        activePlayers = i % 3 == 0 ? 0 : Next(1, 200),
                        riskCenti = Next(0, 10000),
                        playerAttempts = attempts,
                        playerWins = Next(0, attempts),
                        invasionSeverityTotal = total,
                        repelledSeverity = Next(0, total),
                        npcHeldCount = Next(0, 9),
                        failedCount = Next(0, 9),
                        contribution = Next(0, 30000),
                        supportGiven = Next(0, 5000),
                        supportReceived = Next(0, 5000),
                    });
                }
                Add("rank", new { weights, stats = all }, RankingRules.Rank(all, weights));
            }
        }

        // ── 장비 ────────────────────────────────────────────────

        private static void Gear(GameDefs defs)
        {
            CombatDefs combat = defs.combat;
            // 준비 중인 장비도 한 가지 시험한다.
            var soon = new GearDef
            {
                code = "gear_test_soon", name = "시험", slot = GearSlots.Support, kind = GearKinds.Sensor,
                modes = new[] { CombatModeCodes.Fps }, priceGc = 0, comingSoon = true,
            };
            ExtraGear.Add(soon);
            var gearList = new List<GearDef>(combat.gear) { soon, null };
            string[] modes = { CombatModeCodes.Fps, CombatModeCodes.Defense, CombatModeCodes.Hunt, "nope" };
            string[] slots = { GearSlots.Primary, GearSlots.Gadget, GearSlots.Support, "nope" };

            for (int i = 0; i < 300; i++)
            {
                var owned = new List<OwnedGear>();
                foreach (GearDef g in combat.gear)
                    if (_random.Next(3) == 0) owned.Add(new OwnedGear(g.code, Next(0, 5)));
                GearDef def = Pick(gearList);
                int level = Next(1, 8);
                long balance = Pick(new long[] { 0, 100, 299, 300, 600, 1199, 1200, 5000 });
                string mode = Pick(modes);
                string slot = Pick(slots);
                int ownedLevel = GearRules.LevelOf(owned, def);
                Add("gear", new { def = def?.code, owned, playerLevel = level, balance, mode, slot }, new
                {
                    level = ownedLevel,
                    buy = GearRules.CheckBuy(def, owned, level, balance),
                    upgrade = GearRules.CheckUpgrade(def, owned, balance),
                    upgradeCost = GearRules.UpgradeCost(def, Math.Max(1, ownedLevel)),
                    equip = GearRules.CheckEquip(def, owned, mode, slot),
                    damage = GearRules.Damage(def, level),
                    effect = GearRules.EffectValue(def, level),
                });
            }

            for (int i = 0; i < 60; i++)
            {
                var owned = new List<OwnedGear>();
                foreach (GearDef g in combat.gear)
                    if (_random.Next(2) == 0) owned.Add(new OwnedGear(g.code, 1));
                var loadouts = new List<LoadoutEntry>();
                foreach (string slot in GearSlots.All)
                    if (_random.Next(2) == 0)
                        loadouts.Add(new LoadoutEntry { mode = CombatModeCodes.Fps, slot = slot, gearCode = Pick(combat.gear).code });
                string mode = Pick(new[] { CombatModeCodes.Fps, CombatModeCodes.Defense });
                string askSlot = Pick(GearSlots.All);
                GearDef equipped = GearRules.Equipped(combat, owned, loadouts, mode, askSlot);
                Add("equipped", new { owned, loadouts, mode, slot = askSlot }, equipped?.code);
            }
        }

        // ── 방어전 ──────────────────────────────────────────────

        private static void Battle(GameDefs defs)
        {
            StageDef stage = defs.FindStage(StageCodes.Outskirts1);
            string[] enemies = { EnemyCodes.MalfunctionRobot, EnemyCodes.MechInsect, EnemyCodes.ConstructionMecha, EnemyCodes.TutorialRobot };
            for (int i = 0; i < 400; i++)
            {
                var report = new BattleReport();
                int shape = i % 8;
                bool win = shape < 4;
                report.result = shape == 7 ? "draw" : win ? BattleResults.Win : BattleResults.Lose;
                report.wavesCleared = win && shape != 3 ? stage.waves.Count : Next(-1, stage.waves.Count + 1);
                report.baseHpLeft = win ? Next(shape == 2 ? 0 : 1, stage.baseHp + (shape == 2 ? 2 : 0)) : Next(0, stage.baseHp);
                foreach (string code in enemies)
                {
                    int inStage = stage.CountOf(code);
                    if (code == EnemyCodes.TutorialRobot && _random.Next(12) != 0) continue;
                    int count = win && _random.Next(4) != 0
                        ? Math.Max(0, inStage - Next(0, 4))
                        : Next(-1, inStage + 2);
                    report.kills.Add(new KillCount(code, count));
                }
                if (_random.Next(25) == 0 && report.kills.Count > 0) report.kills.Add(report.kills[0]);
                float elapsed = Pick(new[] { 0f, 9.5f, 30f, 49.75f, 50f, 50.25f, 120f, 400f });
                bool cleared = _random.Next(2) == 0;
                Add("battle", new { stage = stage.code, report, elapsed, alreadyCleared = cleared }, BattleRules.Evaluate(stage, report, elapsed, cleared));
            }
            Add("battle", new { stage = stage.code, report = (BattleReport)null, elapsed = 100f, alreadyCleared = false },
                BattleRules.Evaluate(stage, null, 100f, false));
        }

        // ── FPS ─────────────────────────────────────────────────

        private static void Fps(GameDefs defs)
        {
            FpsMissionDef mission = defs.combat.FindFpsMission(FpsMissionCodes.Downtown1);
            string[] enemies = { EnemyCodes.MalfunctionRobot, EnemyCodes.MechInsect, EnemyCodes.ConstructionMecha };
            for (int i = 0; i < 400; i++)
            {
                var report = new FpsReport();
                int shape = i % 8;
                bool win = shape < 5;
                report.result = shape == 7 ? "" : win ? BattleResults.Win : BattleResults.Lose;
                report.facilitiesSaved = Next(win && shape != 4 ? 1 : -1, mission.facilityCount + (shape == 4 ? 1 : 0));
                report.civiliansSaved = Next(shape == 4 ? -1 : 0, mission.civilianCount + (shape == 4 ? 1 : 0));
                foreach (string code in enemies)
                {
                    int inMission = mission.CountOf(code);
                    if (code == EnemyCodes.ConstructionMecha && _random.Next(15) != 0) continue;
                    int count = win && _random.Next(5) != 0 ? Math.Max(0, inMission - Next(0, 3)) : Next(-1, inMission + 1);
                    report.kills.Add(new KillCount(code, count));
                }
                if (_random.Next(30) == 0 && report.kills.Count > 0) report.kills.Add(report.kills[0]);
                float elapsed = Pick(new[] { 0f, 59.5f, 60f, 61f, 150f, 300f, 329.5f, 330f, 330.5f, 900f });
                bool cleared = _random.Next(2) == 0;
                bool rewardAllowed = _random.Next(4) != 0;
                Add("fps", new { mission = mission.code, report, elapsed, alreadyCleared = cleared, rewardAllowed },
                    FpsRules.Evaluate(mission, report, elapsed, cleared, rewardAllowed));
            }
            Add("fps", new { mission = mission.code, report = (FpsReport)null, elapsed = 100f, alreadyCleared = false, rewardAllowed = true },
                FpsRules.Evaluate(mission, null, 100f, false, true));
        }

        // ── 사냥 ────────────────────────────────────────────────

        private static void Hunt(GameDefs defs)
        {
            string[] enemies = { EnemyCodes.TutorialRobot, EnemyCodes.MalfunctionRobot, EnemyCodes.MechInsect, EnemyCodes.ConstructionMecha, "ghost" };
            for (int i = 0; i < 400; i++)
            {
                HuntZoneDef zone = Pick(defs.zones);
                var kills = new List<KillCount>();
                int lines = Next(0, 3);
                for (int k = 0; k < lines; k++)
                {
                    string code = _random.Next(5) == 0 ? Pick(enemies) : Pick(zone.enemyCodes);
                    kills.Add(new KillCount(code, Next(-1, 14)));
                }
                // 0.25초 단위의 시간만 쓴다. 32비트·64비트 실수의 반올림 차이가 결과를 가르지 않게 하기 위해서다.
                float elapsed = Next(-4, 480) * 0.25f;
                float carried = Next(0, 40) * 0.25f;
                int soFar = Pick(new[] { 0, 0, 0, 1500, 2950, 2995, 3000, 3100 });
                HuntVerdict verdict = HuntRules.Evaluate(defs, zone, kills, elapsed, carried, soFar, out float newAllowance);
                Add("hunt", new { zone = zone.code, kills, elapsed, carried, dailySoFar = soFar },
                    new { verdict, newAllowance = Math.Round(newAllowance, 3) });
            }
        }

        // ── 레벨 ────────────────────────────────────────────────

        private static void Levels(GameDefs defs)
        {
            ProgressionConfig config = defs.progression;
            var table = new List<object>();
            for (int level = 1; level <= config.maxLevel; level++)
                table.Add(new { level, xpNext = Progression.XpForNextLevel(level, config), hpMax = Progression.MaxHp(level, config) });
            Add("levelTable", new { }, table);

            for (int i = 0; i < 200; i++)
            {
                int level = i % 9 == 0 ? Next(48, 50) : Next(1, 50);
                int xp = level >= config.maxLevel ? 0 : Next(0, Progression.XpForNextLevel(level, config) - 1);
                int gained = Pick(new[] { 0, 3, 10, 100, 120, 150, 1000, 25000, 2000000 });
                int newLevel = level, newXp = xp;
                int levels = Progression.AddXp(ref newLevel, ref newXp, gained, config);
                Add("addXp", new { level, xp, gained }, new { level = newLevel, xp = newXp, levels });
            }
        }

        // ── 닉네임 ──────────────────────────────────────────────

        private static void Nicknames()
        {
            string[] names =
            {
                "가디언", " 보리친구 ", "a", "", null, "가", "가나다라마바사아", "가나다라마바사아자", "abcDEF12", "1234", "12345a",
                "a12345", "한글1234", "ㄱㄴㄷ", "hello!", "space bar", "운영자", "GM가디언", "Admin1", "바보야", "ZERO", "시티가디언즈",
                "별빛수호대", "x1y2z3", "해운대지킴이", "😀가디언", "FUCKer", "보리", "이방인1", "ok가디언",
            };
            foreach (string name in names)
                Add("nickname", new { name }, new
                {
                    reason = NicknameRules.Check(name), normalized = NicknameRules.Normalize(name), key = NicknameRules.Key(name),
                });
        }

        // ── 지원 ────────────────────────────────────────────────

        private static void Support(GameDefs defs, RegionCatalog catalog)
        {
            string[] homes = { null, "", "26350", "26110", "11110", "99999" };
            string[] targets = { "26350", "26110", "26710", "11110", "41110", "99999" };
            string[] statuses = { SupportStatus.Open, SupportStatus.Resolved, SupportStatus.Expired };
            foreach (string home in homes)
            foreach (string target in targets)
            foreach (string status in statuses)
                Add("supportJoin", new { home, target, status, homeSido = catalog.FindRegion(home)?.sidoCode, targetSido = catalog.FindRegion(target)?.sidoCode },
                    SupportRules.CheckJoin(new SupportRequest { id = "sup_1", regionCode = target, status = status }, home, catalog));
            Add("supportJoin", new { home = "26350", target = (string)null, status = (string)null, homeSido = "26", targetSido = (string)null },
                SupportRules.CheckJoin(null, "26350", catalog));

            for (int i = 0; i < 60; i++)
            {
                RiskPolicy p = RiskPolicy.CreateDefault();
                int centi = Pick(new[] { 0, 4999, 5000, 5099, 7499, 7500, 7600, 10000, Next(0, 10000) });
                bool hasOpen = _random.Next(2) == 0;
                Add("supportOpen", new { policy = Diff(p), centi, hasOpen },
                    new { open = SupportRules.ShouldOpen(p, centi, hasOpen), resolved = SupportRules.IsResolved(p, centi) });
            }
        }

        /// <summary>정책에서 기본값과 다른 값만 뽑는다. 표본 파일이 커지지 않게 하기 위해서다.</summary>
        private static Dictionary<string, object> Diff(RiskPolicy policy)
        {
            RiskPolicy basis = RiskPolicy.CreateDefault();
            var diff = new Dictionary<string, object>();
            foreach (FieldInfo field in typeof(RiskPolicy).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (field.Name == "weights") continue;
                object value = field.GetValue(policy);
                if (!Equals(value, field.GetValue(basis))) diff[field.Name] = value;
            }
            var weights = new Dictionary<string, int>();
            for (int i = 0; i < policy.weights.Count; i++)
                if (policy.weights[i].deltaCenti != basis.Delta(policy.weights[i].reason))
                    weights[policy.weights[i].reason] = policy.weights[i].deltaCenti;
            if (weights.Count > 0) diff["weights"] = weights;
            return diff;
        }

        // ── 쓰기 ────────────────────────────────────────────────

        private static void Write(string dir, string name, object value)
        {
            var settings = new JsonSerializerSettings
            {
                ContractResolver = new FieldsOnlyResolver(),
                Formatting = Formatting.None,
                NullValueHandling = NullValueHandling.Include,
            };
            string json = JsonConvert.SerializeObject(value, settings);
            File.WriteAllText(Path.Combine(dir, name), json + "\n", new UTF8Encoding(false));
        }

        /// <summary>
        /// 게임의 정의 클래스는 공개 필드만 내보낸다 (계산해서 얻는 속성은 빼기 위해서다).
        /// 여기서 만든 이름 없는 형식은 속성을 그대로 내보낸다.
        /// </summary>
        private sealed class FieldsOnlyResolver : DefaultContractResolver
        {
            protected override List<MemberInfo> GetSerializableMembers(Type objectType)
            {
                if (objectType.Namespace != "CityGuardians.Domain") return base.GetSerializableMembers(objectType);
                var members = new List<MemberInfo>();
                foreach (FieldInfo field in objectType.GetFields(BindingFlags.Public | BindingFlags.Instance)) members.Add(field);
                return members;
            }
        }
    }
}
