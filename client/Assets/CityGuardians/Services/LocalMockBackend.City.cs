using System;
using System.Collections.Generic;
using System.Globalization;
using CityGuardians.Domain;
using UnityEngine;

namespace CityGuardians.Services
{
    // v8.0: 전투 장비, FPS 임무, 도시 위험도, 침공, 지역 랭킹, 도시 간 지원의 모의 서버 구현.
    //
    // 위험도는 여기(서버 쪽)에서만 계산한다. 클라이언트가 위험도 숫자를 넣을 수 있는 요청은 없다.
    // 플레이어의 지갑과 통장은 위험도와 연결되어 있지 않다: 이 파일에서 GC가 나가는 곳은
    // 플레이어가 스스로 고른 장비 구매·강화, 시설 복구, 도시 기금뿐이다.
    //
    // 도시 기록은 mock_world.json 에 따로 둔다. 전국 230개 도시의 기록이라 크기가 있고,
    // 상점 구매나 사냥 보고처럼 자주 일어나는 저장 때마다 다시 쓰지 않게 하기 위해서다.
    public sealed partial class LocalMockBackend
    {
        public const string WorldFile = "mock_world.json";
        private const string RequestGearBuy = "gear_buy";
        private const string RequestGearUpgrade = "gear_upgrade";
        private const string RequestCityRepair = "city_repair";
        private const string RequestCityFund = "city_fund";
        /// <summary>침공 판정에 쓰는 고정 씨앗. 같은 시각의 판정은 늘 같은 결과다.</summary>
        private const int WorldSeed = 20261113;
        /// <summary>오래 꺼 두었다 켰을 때 한꺼번에 따라잡는 판정의 수.</summary>
        private const int MaxCatchUpTicks = 72;
        private const int HistoryKeepHome = 40;
        private const int HistoryKeepOther = 6;
        private const int InvasionKeepHome = 10;
        private const int InvasionKeepOther = 2;
        private const int SupportKeepClosed = 40;
        private const int DetailHistoryCount = 20;
        private const int DetailInvasionCount = 8;
        private const int RecentInvasionDays = 7;

        [Serializable]
        private sealed class FacilityRow
        {
            public string code;
            public int damage;
        }

        [Serializable]
        private sealed class CityStructureRow
        {
            public string code;
            /// <summary>지금까지 모인 기금의 합.</summary>
            public long fundedGc;
        }

        /// <summary>도시 하나의 위험도와 집계. 서버의 city_risk_status 한 행.</summary>
        [Serializable]
        private sealed class CityRow
        {
            public string code;
            /// <summary>위험도 (1/100 단위, 0~10000).</summary>
            public int riskCenti;
            /// <summary>하루 한 번의 NPC 순찰을 마지막으로 반영한 날 (한국 날짜).</summary>
            public string stabilizedDate;
            public List<FacilityRow> facilities = new List<FacilityRow>();
            public List<CityStructureRow> structures = new List<CityStructureRow>();
            /// <summary>최근 침공이 시작된 날들 (한국 날짜). 최근 7일만 남긴다.</summary>
            public List<string> invasionDates = new List<string>();
            // 랭킹 집계. 플레이어의 것과 NPC의 것을 따로 센다.
            public int playerWins;
            public int playerAttempts;
            public int invasionSeverityTotal;
            public int repelledSeverity;
            public int npcHeldCount;
            public int failedCount;
            public long contribution;
            public long supportGiven;
            public long supportReceived;
        }

        [Serializable]
        private sealed class WorldDb
        {
            public int version = 1;
            /// <summary>마지막으로 처리한 침공 판정 순번. 아직 한 번도 처리하지 않았으면 -1.</summary>
            public long tick = -1;
            public long historySeq = 1;
            public long supportSeq = 1;
            public List<CityRow> cities = new List<CityRow>();
            public List<InvasionEvent> invasions = new List<InvasionEvent>();
            public List<RiskHistoryEntry> history = new List<RiskHistoryEntry>();
            public List<SupportRequest> supports = new List<SupportRequest>();
        }

        private sealed class FpsSession
        {
            public string id;
            public string playerId;
            public FpsMissionDef mission;
            public DateTime startedUtc;
            public FpsResultDto response;
        }

        private readonly Dictionary<string, FpsSession> _fpsSessions = new Dictionary<string, FpsSession>();
        private readonly Dictionary<string, CityRow> _cityIndex = new Dictionary<string, CityRow>();
        private readonly Dictionary<string, InvasionEvent> _activeInvasions = new Dictionary<string, InvasionEvent>();
        private WorldDb _world;
        private bool _worldDirty;

        private RiskPolicy Policy => Defs.combat.risk;

        // ── 공통 전투: 모드, 장비, 장착 ─────────────────────────

        public void GetCombatOverview(Action<ApiResult<CombatOverviewDto>> done)
        {
            if (!RequirePlayer(done)) return;
            RollDailyIfNeeded(_player);
            var dto = new CombatOverviewDto
            {
                gear = CopyGear(_player),
                loadouts = CopyLoadouts(_player),
                clearedFpsMissions = new List<string>(_player.clearedFps),
                fpsRewardsToday = _player.dailyFpsRewards,
                fpsRewardLimit = Defs.combat.dailyFpsRewardLimit,
                riskReducedToday = _player.dailyRiskReduced / 100,
                riskReduceCap = Policy.dailyPlayerReduceCap,
            };
            for (int i = 0; i < Defs.combat.modes.Count; i++) dto.modes.Add(Defs.combat.modes[i]);
            done(ApiResult<CombatOverviewDto>.Success(dto));
        }

        public void BuyGear(string gearCode, string requestId, Action<ApiResult<GearResultDto>> done)
        {
            if (!RequirePlayer(done)) return;
            if (!IsValidRequestId(requestId))
            {
                done(ApiResult<GearResultDto>.Fail(ApiErrors.InvalidRequest, "요청 번호가 올바르지 않아요."));
                return;
            }
            RequestRow seen = FindRequest(_player, requestId);
            if (seen != null)
            {
                if (seen.kind != RequestGearBuy || seen.itemCode != gearCode)
                {
                    done(ApiResult<GearResultDto>.Fail(ApiErrors.Conflict, "이미 다른 일에 쓴 요청 번호예요."));
                    return;
                }
                done(ApiResult<GearResultDto>.Success(ToGearResult(_player, gearCode, seen.amount, true)));
                return;
            }

            GearDef def = Defs.combat.FindGear(gearCode);
            string reason = GearRules.CheckBuy(def, _player.gear, _player.level, _player.balance);
            if (reason != null)
            {
                done(ApiResult<GearResultDto>.Fail(ApiErrors.FromGearReason(reason), GearMessage(reason, def)));
                return;
            }

            ApplyLedger(_player, -def.priceGc, LedgerReasons.GearPurchase, gearCode + ":" + requestId, _clock.UtcNow);
            _player.gear.Add(new OwnedGear(gearCode, 1));
            RememberRequest(_player, requestId, RequestGearBuy, gearCode, def.priceGc);
            SaveDb();
            done(ApiResult<GearResultDto>.Success(ToGearResult(_player, gearCode, def.priceGc, false)));
        }

        public void UpgradeGear(string gearCode, string requestId, Action<ApiResult<GearResultDto>> done)
        {
            if (!RequirePlayer(done)) return;
            if (!IsValidRequestId(requestId))
            {
                done(ApiResult<GearResultDto>.Fail(ApiErrors.InvalidRequest, "요청 번호가 올바르지 않아요."));
                return;
            }
            RequestRow seen = FindRequest(_player, requestId);
            if (seen != null)
            {
                if (seen.kind != RequestGearUpgrade || seen.itemCode != gearCode)
                {
                    done(ApiResult<GearResultDto>.Fail(ApiErrors.Conflict, "이미 다른 일에 쓴 요청 번호예요."));
                    return;
                }
                done(ApiResult<GearResultDto>.Success(ToGearResult(_player, gearCode, seen.amount, true)));
                return;
            }

            GearDef def = Defs.combat.FindGear(gearCode);
            string reason = GearRules.CheckUpgrade(def, _player.gear, _player.balance);
            if (reason != null)
            {
                done(ApiResult<GearResultDto>.Fail(ApiErrors.FromGearReason(reason), GearMessage(reason, def)));
                return;
            }

            int level = GearRules.LevelOf(_player.gear, def);
            int cost = GearRules.UpgradeCost(def, level);
            ApplyLedger(_player, -cost, LedgerReasons.GearUpgrade, gearCode + ":" + (level + 1) + ":" + requestId, _clock.UtcNow);
            OwnedGear owned = GearRules.Find(_player.gear, gearCode);
            if (owned == null)
            {
                // 무료 기본 장비는 강화할 때 처음으로 행이 생긴다.
                owned = new OwnedGear(gearCode, level);
                _player.gear.Add(owned);
            }
            owned.level = level + 1;
            RememberRequest(_player, requestId, RequestGearUpgrade, gearCode, cost);
            SaveDb();
            done(ApiResult<GearResultDto>.Success(ToGearResult(_player, gearCode, cost, false)));
        }

        public void EquipGear(string modeCode, string slot, string gearCode, Action<ApiResult<GearResultDto>> done)
        {
            if (!RequirePlayer(done)) return;
            if (!CombatModeCodes.IsKnown(modeCode) || !GearSlots.IsKnown(slot))
            {
                done(ApiResult<GearResultDto>.Fail(ApiErrors.InvalidRequest, "없는 전투 모드나 장비 칸이에요."));
                return;
            }

            if (string.IsNullOrEmpty(gearCode))
            {
                // 칸을 비운다: 다음부터 무료 기본 장비를 쓴다.
                LoadoutEntry entry = GearRules.FindEntry(_player.loadouts, modeCode, slot);
                if (entry != null) _player.loadouts.Remove(entry);
                SaveDb();
                done(ApiResult<GearResultDto>.Success(ToGearResult(_player, null, 0, false)));
                return;
            }

            GearDef def = Defs.combat.FindGear(gearCode);
            string reason = GearRules.CheckEquip(def, _player.gear, modeCode, slot);
            if (reason != null)
            {
                done(ApiResult<GearResultDto>.Fail(ApiErrors.FromGearReason(reason), GearMessage(reason, def)));
                return;
            }
            GearRules.SetEntry(_player.loadouts, modeCode, slot, gearCode);
            SaveDb();
            done(ApiResult<GearResultDto>.Success(ToGearResult(_player, gearCode, 0, false)));
        }

        private GearResultDto ToGearResult(PlayerRow row, string gearCode, int costGc, bool duplicate) => new GearResultDto
        {
            gearCode = gearCode,
            level = gearCode != null ? GearRules.LevelOf(row.gear, Defs.combat.FindGear(gearCode)) : 0,
            costGc = costGc,
            gc = row.balance,
            gear = CopyGear(row),
            loadouts = CopyLoadouts(row),
            duplicate = duplicate,
        };

        private static string GearMessage(string reason, GearDef def) =>
            reason == GearReasons.LevelTooLow && def != null
                ? $"레벨 {def.unlockLevel}부터 살 수 있어요."
                : GearReasons.Message(reason);

        private static List<OwnedGear> CopyGear(PlayerRow row)
        {
            var copy = new List<OwnedGear>(row.gear.Count);
            for (int i = 0; i < row.gear.Count; i++) copy.Add(new OwnedGear(row.gear[i].code, row.gear[i].level));
            return copy;
        }

        private static List<LoadoutEntry> CopyLoadouts(PlayerRow row)
        {
            var copy = new List<LoadoutEntry>(row.loadouts.Count);
            for (int i = 0; i < row.loadouts.Count; i++)
                copy.Add(new LoadoutEntry { mode = row.loadouts[i].mode, slot = row.loadouts[i].slot, gearCode = row.loadouts[i].gearCode });
            return copy;
        }

        // ── FPS 임무 ────────────────────────────────────────────

        public void StartFpsMission(string missionCode, Action<ApiResult<FpsSessionDto>> done)
        {
            if (!RequirePlayer(done)) return;
            FpsMissionDef mission = Defs.combat.FindFpsMission(missionCode);
            if (mission == null)
            {
                done(ApiResult<FpsSessionDto>.Fail(ApiErrors.NotFound, "없는 임무예요."));
                return;
            }
            if (_player.level < mission.unlockLevel)
            {
                done(ApiResult<FpsSessionDto>.Fail(ApiErrors.Locked, $"레벨 {mission.unlockLevel}부터 나설 수 있어요."));
                return;
            }

            DateTime now = _clock.UtcNow;
            var session = new FpsSession
            {
                id = "f_" + RandomToken(10),
                playerId = _player.playerId,
                mission = mission,
                startedUtc = now,
            };
            _fpsSessions[session.id] = session;

            var dto = new FpsSessionDto { sessionId = session.id, missionCode = mission.code };
            string target = CombatRegionOf(_player);
            if (!string.IsNullOrEmpty(target))
            {
                AdvanceWorld(now);
                target = CombatRegionOf(_player);
                dto.regionCode = target;
                InvasionEvent invasion = ActiveInvasionOf(target);
                if (InvasionRules.Counts(invasion, CombatModeCodes.Fps)) dto.invasionId = invasion.id;
                SaveWorldIfDirty();
            }
            done(ApiResult<FpsSessionDto>.Success(dto));
        }

        public void FinishFpsMission(string sessionId, FpsReport report, Action<ApiResult<FpsResultDto>> done)
        {
            if (!RequirePlayer(done)) return;
            if (sessionId == null || !_fpsSessions.TryGetValue(sessionId, out FpsSession session)
                || session.playerId != _player.playerId)
            {
                done(ApiResult<FpsResultDto>.Fail(ApiErrors.NotFound, "임무 기록을 찾을 수 없어요."));
                return;
            }
            // 이미 끝난 임무의 재보고: 처음 결과를 그대로 돌려준다 (두 번 지급하지 않는다).
            if (session.response != null)
            {
                done(ApiResult<FpsResultDto>.Success(session.response));
                return;
            }

            RollDailyIfNeeded(_player);
            DateTime now = _clock.UtcNow;
            float elapsed = (float)(now - session.startedUtc).TotalSeconds;
            FpsMissionDef mission = session.mission;
            bool alreadyCleared = _player.clearedFps.Contains(mission.code);
            bool rewardAllowed = _player.dailyFpsRewards < Defs.combat.dailyFpsRewardLimit;
            FpsVerdict verdict = FpsRules.Evaluate(mission, report, elapsed, alreadyCleared, rewardAllowed);

            bool win = verdict.accepted && verdict.result == BattleResults.Win;
            int levelsGained = 0;
            int defensePoints = 0;
            if (win)
            {
                if (verdict.rewardGc > 0)
                {
                    ApplyLedger(_player, verdict.rewardGc, LedgerReasons.FpsReward, session.id, now);
                    _player.dailyFpsRewards++;
                }
                if (verdict.rewardXp > 0)
                    levelsGained = Progression.AddXp(ref _player.level, ref _player.xp, verdict.rewardXp, Defs.progression);
                if (!alreadyCleared) _player.clearedFps.Add(mission.code);
                if (IsResident(_player) && mission.winDefensePoints > 0)
                {
                    defensePoints = mission.winDefensePoints;
                    RegionRowFor(CombatRegionOf(_player)).defensePoints += defensePoints;
                }
            }
            CityEffectDto cityEffect = verdict.accepted
                ? ApplyCombatToCity(_player, CombatModeCodes.Fps, win, session.id, now)
                : null;

            session.response = new FpsResultDto
            {
                accepted = verdict.accepted,
                result = verdict.result,
                rewardGc = verdict.rewardGc,
                rewardXp = verdict.rewardXp,
                firstWin = verdict.firstWin,
                rewardLimited = win && !rewardAllowed,
                gc = _player.balance,
                level = _player.level,
                xp = _player.xp,
                xpNext = Progression.XpForNextLevel(_player.level, Defs.progression),
                hpMax = Progression.MaxHp(_player.level, Defs.progression),
                levelsGained = levelsGained,
                rejectReason = verdict.rejectReason,
                facilitiesSaved = verdict.facilitiesSaved,
                civiliansSaved = verdict.civiliansSaved,
                clearedFpsMissions = new List<string>(_player.clearedFps),
                defensePoints = defensePoints,
                fpsRewardsToday = _player.dailyFpsRewards,
                fpsRewardLimit = Defs.combat.dailyFpsRewardLimit,
                city = cityEffect,
            };
            SaveDb();
            done(ApiResult<FpsResultDto>.Success(session.response));
        }

        // ── 전투 결과를 도시에 반영 ─────────────────────────────

        /// <summary>
        /// 전투가 반영되는 도시: 지원 나가 있으면 그 도시, 아니면 소속 도시. 이방인이면 null.
        /// </summary>
        private string CombatRegionOf(PlayerRow row)
        {
            if (!IsResident(row)) return null;
            return SupportRegionOf(row) ?? row.regionCode;
        }

        /// <summary>지금 지원 나가 있는 도시. 요청이 닫혔으면 null.</summary>
        private string SupportRegionOf(PlayerRow row)
        {
            SupportRequest request = OpenSupportOf(row);
            return request != null ? request.regionCode : null;
        }

        private SupportRequest OpenSupportOf(PlayerRow row)
        {
            if (string.IsNullOrEmpty(row.supportRequestId)) return null;
            LoadWorld();
            SupportRequest request = _world.supports.Find(s => s.id == row.supportRequestId);
            return request != null && request.IsOpen ? request : null;
        }

        /// <summary>
        /// 방어전·FPS 결과를 도시에 반영한다. 이기면 위험도가 내려가고 기여도가 쌓인다.
        /// 져도 위험도는 올라가지 않는다 (시도 횟수만 센다). 이방인은 반영할 도시가 없다.
        /// </summary>
        private CityEffectDto ApplyCombatToCity(PlayerRow row, string mode, bool win, string reference, DateTime now)
        {
            if (!IsResident(row)) return null;
            row.lastActiveAt = now.ToString("o");
            AdvanceWorld(now);

            SupportRequest support = OpenSupportOf(row);
            if (support == null) row.supportRequestId = null;
            string target = support != null ? support.regionCode : row.regionCode;
            CityRow city = CityFor(target);
            if (city == null) return null;

            city.playerAttempts++;
            _worldDirty = true;
            if (!win)
            {
                SaveWorldIfDirty();
                return null;
            }
            city.playerWins++;

            RollDailyIfNeeded(row);
            RiskPolicy policy = Policy;
            string at = now.ToString("o");
            string actor = support != null ? RiskActors.Supporter : RiskActors.Player;
            InvasionEvent invasion = ActiveInvasionOf(target);
            bool counts = InvasionRules.Counts(invasion, mode);
            string reason = InvasionRules.WinReason(mode);

            int before = city.riskCenti;
            string stageBefore = CityRiskRules.StageOf(before, policy);
            RiskChange change = CityRiskRules.ApplyPlayer(city.riskCenti, reason, counts ? 100 : policy.patrolPercent, policy,
                ActivePlayers(target, now), row.dailyRiskReduced, out int reducedToday);
            row.dailyRiskReduced = reducedToday;
            ChangeRisk(city, change, actor, reference, at);

            int points = mode == CombatModeCodes.Fps ? policy.contribFpsWin : policy.contribDefenseWin;
            if (counts) points += points * policy.contribSeverityBonusPercent * invasion.severity / 100;

            var effect = new CityEffectDto
            {
                regionCode = target, actor = actor, reason = reason, contribution = points,
                capped = reducedToday >= policy.dailyPlayerReduceCap * 100,
            };
            if (counts)
            {
                invasion.progress++;
                effect.invasionId = invasion.id;
                effect.invasionKind = invasion.kind;
                effect.invasionRequired = invasion.required;
                effect.invasionProgress = invasion.progress;
                if (invasion.progress >= invasion.required)
                {
                    // 플레이어들이 끝까지 막아 냈다. 이 몫은 침공 하나에 한 번이라 사람 수로 나누지 않는다.
                    invasion.status = InvasionStatus.Repelled;
                    invasion.resolvedAt = at;
                    city.invasionSeverityTotal += invasion.severity;
                    city.repelledSeverity += invasion.severity;
                    int quantity = invasion.kind == InvasionKinds.Zero ? 1 : invasion.severity;
                    ChangeRisk(city, CityRiskRules.ApplySystem(city.riskCenti, InvasionRules.RepelReason(invasion.kind), quantity, policy),
                        actor, invasion.id, at);
                    effect.invasionRepelled = true;
                }
            }

            if (support != null)
            {
                // 지원 나간 도시: 소속 도시는 그대로이고, 기여는 지원 기여도로 따로 쌓인다.
                support.wins++;
                support.points += points;
                row.supportPoints += points;
                city.supportReceived += points;
                CityRow home = CityFor(row.regionCode);
                if (home != null) home.supportGiven += points;
            }
            else
            {
                city.contribution += points;
                row.cityContribution += points;
            }

            UpdateSupport(city, now);
            effect.riskBefore = CityRiskRules.ToPoints(before);
            effect.riskAfter = CityRiskRules.ToPoints(city.riskCenti);
            effect.riskDeltaCenti = city.riskCenti - before;
            effect.stageBefore = stageBefore;
            effect.stage = CityRiskRules.StageOf(city.riskCenti, policy);
            _worldDirty = true;
            SaveWorldIfDirty();
            return effect;
        }

        /// <summary>
        /// 사냥을 소속 도시에 반영한다. 서버가 인정한 처치가 일정 수 모일 때마다 위험도가 조금 내려간다.
        /// 사냥은 우리 도시의 거리에서 하는 일이라, 지원 나가 있어도 소속 도시에 반영된다.
        /// </summary>
        private CityEffectDto ApplyHuntToCity(PlayerRow row, int acceptedKills, string reference, DateTime now)
        {
            if (!IsResident(row) || acceptedKills <= 0) return null;
            RiskPolicy policy = Policy;
            row.lastActiveAt = now.ToString("o");
            row.huntKillCarry += acceptedKills;
            int perStep = Math.Max(1, policy.huntKillsPerStep);
            int steps = row.huntKillCarry / perStep;
            if (steps <= 0) return null;
            row.huntKillCarry -= steps * perStep;

            AdvanceWorld(now);
            CityRow city = CityFor(row.regionCode);
            if (city == null) return null;
            RollDailyIfNeeded(row);

            int before = city.riskCenti;
            string stageBefore = CityRiskRules.StageOf(before, policy);
            // 위험 단계 이상의 도시에서는 사냥이 특별 사냥 임무로 쳐진다.
            int units = steps * 100;
            if (RiskStages.Order(stageBefore) >= RiskStages.Order(RiskStages.Danger))
                units = units * Math.Max(100, policy.huntHighRiskPercent) / 100;
            RiskChange change = CityRiskRules.ApplyPlayer(city.riskCenti, RiskReasons.HuntCompleted, units, policy,
                ActivePlayers(row.regionCode, now), row.dailyRiskReduced, out int reducedToday);
            row.dailyRiskReduced = reducedToday;
            ChangeRisk(city, change, RiskActors.Player, reference, now.ToString("o"));

            int points = policy.contribHuntStep * steps;
            city.contribution += points;
            row.cityContribution += points;
            UpdateSupport(city, now);
            _worldDirty = true;
            SaveWorldIfDirty();

            return new CityEffectDto
            {
                regionCode = row.regionCode, actor = RiskActors.Player, reason = RiskReasons.HuntCompleted,
                riskBefore = CityRiskRules.ToPoints(before), riskAfter = CityRiskRules.ToPoints(city.riskCenti),
                riskDeltaCenti = city.riskCenti - before,
                stageBefore = stageBefore, stage = CityRiskRules.StageOf(city.riskCenti, policy),
                contribution = points,
                capped = reducedToday >= policy.dailyPlayerReduceCap * 100,
            };
        }

        // ── 도시 조회 ───────────────────────────────────────────

        public void GetCityRisks(Action<ApiResult<CityRisksDto>> done)
        {
            if (!RequirePlayer(done)) return;
            DateTime now = _clock.UtcNow;
            AdvanceWorld(now);

            Dictionary<string, RankingScore> scores = RankAll(now, out _);
            var dto = new CityRisksDto
            {
                serverTime = now.ToString("o"),
                homeRegionCode = IsResident(_player) ? _player.regionCode : null,
                supportRegionCode = SupportRegionOf(_player),
            };
            for (int i = 0; i < _world.cities.Count; i++)
            {
                CityRow city = _world.cities[i];
                scores.TryGetValue(city.code, out RankingScore score);
                dto.cities.Add(ToCityStatus(city, score, now));
            }
            SaveWorldIfDirty();
            done(ApiResult<CityRisksDto>.Success(dto));
        }

        public void GetCityDetail(string regionCode, Action<ApiResult<CityDetailDto>> done)
        {
            if (!RequirePlayer(done)) return;
            if (Regions.FindRegion(regionCode) == null)
            {
                done(ApiResult<CityDetailDto>.Fail(ApiErrors.NotFound, "없는 지역이에요."));
                return;
            }
            DateTime now = _clock.UtcNow;
            AdvanceWorld(now);
            CityDetailDto detail = ToCityDetail(CityFor(regionCode), now);
            SaveWorldIfDirty();
            done(ApiResult<CityDetailDto>.Success(detail));
        }

        public void GetRankings(Action<ApiResult<RankingsDto>> done)
        {
            if (!RequirePlayer(done)) return;
            DateTime now = _clock.UtcNow;
            AdvanceWorld(now);

            RankAll(now, out List<RankingScore> ordered);
            var dto = new RankingsDto { totalCities = ordered.Count };
            string home = IsResident(_player) ? _player.regionCode : null;
            for (int i = 0; i < ordered.Count; i++)
            {
                RankingScore score = ordered[i];
                CityRow city = CityFor(score.regionCode);
                var row = new RankingRowDto
                {
                    regionCode = score.regionCode, rank = score.rank, score = score.total,
                    safety = score.safety, successRate = score.successRate, contribution = score.contribution,
                    response = score.response, cooperation = score.cooperation, playerDefended = score.playerDefended,
                    risk = CityRiskRules.ToPoints(city.riskCenti), stage = CityRiskRules.StageOf(city.riskCenti, Policy),
                    activePlayers = ActivePlayers(score.regionCode, now),
                };
                if (score.playerDefended) dto.playerCities++;
                if (score.regionCode == home) dto.home = row;
                dto.rows.Add(row);
            }
            SaveWorldIfDirty();
            done(ApiResult<RankingsDto>.Success(dto));
        }

        // ── 도시 간 지원 ────────────────────────────────────────

        public void GetSupportRequests(Action<ApiResult<SupportListDto>> done)
        {
            if (!RequirePlayer(done)) return;
            DateTime now = _clock.UtcNow;
            AdvanceWorld(now);

            SupportRequest joined = OpenSupportOf(_player);
            var dto = new SupportListDto
            {
                joinedRequestId = joined != null ? joined.id : null,
                joinedRegionCode = joined != null ? joined.regionCode : null,
                mySupportPoints = _player.supportPoints,
            };
            string home = IsResident(_player) ? _player.regionCode : null;
            // 이방인은 지원에 나설 수 없다. 목록도 비어 있다.
            for (int i = 0; home != null && i < _world.supports.Count; i++)
            {
                SupportRequest request = _world.supports[i];
                if (!request.IsOpen) continue;
                CityRow city = CityFor(request.regionCode);
                if (city == null) continue;
                string reason = SupportRules.CheckJoin(request, home, Regions);
                // 너무 먼 도시의 요청은 목록에 넣지 않는다. 주변 도시의 가디언에게만 보인다.
                if (reason == SupportReasons.TooFar) continue;
                dto.requests.Add(new SupportRowDto
                {
                    requestId = request.id, regionCode = request.regionCode,
                    risk = CityRiskRules.ToPoints(city.riskCenti), stage = CityRiskRules.StageOf(city.riskCenti, Policy),
                    closesAt = request.closesAt, wins = request.wins, points = request.points,
                    canJoin = reason == null, reason = reason,
                });
            }
            // 가장 위험한 도시가 앞에 온다.
            dto.requests.Sort((a, b) => a.risk != b.risk ? b.risk.CompareTo(a.risk) : string.CompareOrdinal(a.regionCode, b.regionCode));
            SaveWorldIfDirty();
            done(ApiResult<SupportListDto>.Success(dto));
        }

        public void JoinSupport(string requestId, Action<ApiResult<SupportResultDto>> done)
        {
            if (!RequirePlayer(done)) return;
            DateTime now = _clock.UtcNow;
            AdvanceWorld(now);
            SupportRequest request = string.IsNullOrEmpty(requestId) ? null : _world.supports.Find(s => s.id == requestId);
            string reason = SupportRules.CheckJoin(request, IsResident(_player) ? _player.regionCode : null, Regions);
            if (reason != null)
            {
                SaveWorldIfDirty();
                string code = reason == SupportReasons.NotOpen ? (request == null ? ApiErrors.NotFound : ApiErrors.Conflict)
                    : reason == SupportReasons.NotResident ? ApiErrors.Locked
                    : ApiErrors.ValidationFailed;
                done(ApiResult<SupportResultDto>.Fail(code, SupportReasons.Message(reason)));
                return;
            }

            // 소속 도시(regionCode)는 건드리지 않는다. 어디를 돕고 있는지만 적는다.
            _player.supportRequestId = request.id;
            SaveDb();
            SaveWorldIfDirty();
            done(ApiResult<SupportResultDto>.Success(new SupportResultDto
            {
                joinedRequestId = request.id, joinedRegionCode = request.regionCode, homeRegionCode = _player.regionCode,
            }));
        }

        public void LeaveSupport(Action<ApiResult<SupportResultDto>> done)
        {
            if (!RequirePlayer(done)) return;
            _player.supportRequestId = null;
            SaveDb();
            done(ApiResult<SupportResultDto>.Success(new SupportResultDto
            {
                homeRegionCode = IsResident(_player) ? _player.regionCode : null,
            }));
        }

        // ── 도시를 위해 GC를 쓰는 일 (플레이어가 스스로 고른 것만) ──

        public void RepairFacility(string facilityCode, string requestId, Action<ApiResult<CityActionDto>> done)
        {
            if (!RequirePlayer(done)) return;
            if (!IsValidRequestId(requestId))
            {
                done(ApiResult<CityActionDto>.Fail(ApiErrors.InvalidRequest, "요청 번호가 올바르지 않아요."));
                return;
            }
            DateTime now = _clock.UtcNow;
            RequestRow seen = FindRequest(_player, requestId);
            if (seen != null)
            {
                if (seen.kind != RequestCityRepair || seen.itemCode != facilityCode)
                {
                    done(ApiResult<CityActionDto>.Fail(ApiErrors.Conflict, "이미 다른 일에 쓴 요청 번호예요."));
                    return;
                }
                AdvanceWorld(now);
                done(ApiResult<CityActionDto>.Success(new CityActionDto
                {
                    costGc = seen.amount, gc = _player.balance, duplicate = true,
                    detail = ToCityDetail(CityFor(_player.regionCode), now),
                }));
                SaveWorldIfDirty();
                return;
            }
            if (!IsResident(_player))
            {
                done(ApiResult<CityActionDto>.Fail(ApiErrors.Locked, "전입신고를 한 가디언만 도시의 시설을 고칠 수 있어요."));
                return;
            }
            FacilityDef def = Defs.combat.FindFacility(facilityCode);
            if (def == null)
            {
                done(ApiResult<CityActionDto>.Fail(ApiErrors.NotFound, "없는 시설이에요."));
                return;
            }

            RollDailyIfNeeded(_player);
            AdvanceWorld(now);
            RiskPolicy policy = Policy;
            CityRow city = CityFor(_player.regionCode);
            FacilityRow facility = FacilityOf(city, facilityCode);
            if (facility.damage <= 0)
            {
                SaveWorldIfDirty();
                done(ApiResult<CityActionDto>.Fail(ApiErrors.Conflict, "고칠 곳이 없어요."));
                return;
            }
            if (_player.dailyRepairs >= policy.dailyRepairLimit)
            {
                SaveWorldIfDirty();
                done(ApiResult<CityActionDto>.Fail(ApiErrors.LimitReached, "오늘은 더 고칠 수 없어요. 내일 다시 도와주세요."));
                return;
            }
            if (_player.balance < def.repairGc)
            {
                SaveWorldIfDirty();
                done(ApiResult<CityActionDto>.Fail(ApiErrors.InsufficientFunds, "GC가 모자라요."));
                return;
            }

            ApplyLedger(_player, -def.repairGc, LedgerReasons.CityRepair, _player.regionCode + ":" + facilityCode + ":" + requestId, now);
            facility.damage--;
            _player.dailyRepairs++;
            _player.lastActiveAt = now.ToString("o");
            CityEffectDto effect = ApplyCityAction(city, RiskReasons.FacilityRepaired, policy.contribRepair, requestId, now);
            RememberRequest(_player, requestId, RequestCityRepair, facilityCode, def.repairGc);
            SaveDb();
            SaveWorldIfDirty();
            done(ApiResult<CityActionDto>.Success(new CityActionDto
            {
                costGc = def.repairGc, gc = _player.balance, contribution = effect.contribution, city = effect,
                detail = ToCityDetail(city, now),
            }));
        }

        public void FundCityStructure(string structureCode, int amountGc, string requestId, Action<ApiResult<CityActionDto>> done)
        {
            if (!RequirePlayer(done)) return;
            if (!IsValidRequestId(requestId))
            {
                done(ApiResult<CityActionDto>.Fail(ApiErrors.InvalidRequest, "요청 번호가 올바르지 않아요."));
                return;
            }
            DateTime now = _clock.UtcNow;
            RequestRow seen = FindRequest(_player, requestId);
            if (seen != null)
            {
                if (seen.kind != RequestCityFund || seen.itemCode != structureCode || seen.amount != amountGc)
                {
                    done(ApiResult<CityActionDto>.Fail(ApiErrors.Conflict, "이미 다른 일에 쓴 요청 번호예요."));
                    return;
                }
                AdvanceWorld(now);
                done(ApiResult<CityActionDto>.Success(new CityActionDto
                {
                    costGc = seen.amount, gc = _player.balance, duplicate = true,
                    detail = ToCityDetail(CityFor(_player.regionCode), now),
                }));
                SaveWorldIfDirty();
                return;
            }
            if (!IsResident(_player))
            {
                done(ApiResult<CityActionDto>.Fail(ApiErrors.Locked, "전입신고를 한 가디언만 도시 기금에 낼 수 있어요."));
                return;
            }
            TowerDef def = Defs.FindTower(structureCode);
            if (def == null || !def.IsCityOwned || def.comingSoon || def.cityGoalGc <= 0)
            {
                done(ApiResult<CityActionDto>.Fail(ApiErrors.NotFound, "도시가 함께 세우는 시설이 아니에요."));
                return;
            }
            // 100 GC 단위로만 낸다. 금액은 플레이어가 고르지만 범위는 서버가 검사한다.
            if (amountGc < 100 || amountGc % 100 != 0)
            {
                done(ApiResult<CityActionDto>.Fail(ApiErrors.InvalidRequest, "100 GC 단위로 낼 수 있어요."));
                return;
            }

            RollDailyIfNeeded(_player);
            AdvanceWorld(now);
            RiskPolicy policy = Policy;
            CityRow city = CityFor(_player.regionCode);
            CityStructureRow structure = StructureOf(city, structureCode);
            long full = (long)def.cityGoalGc * Math.Max(1, def.cityMaxLevel);
            if (structure.fundedGc >= full)
            {
                SaveWorldIfDirty();
                done(ApiResult<CityActionDto>.Fail(ApiErrors.Conflict, "이미 다 지은 시설이에요."));
                return;
            }
            if (_player.dailyFundGc + amountGc > policy.dailyFundLimitGc)
            {
                SaveWorldIfDirty();
                done(ApiResult<CityActionDto>.Fail(ApiErrors.LimitReached,
                    $"하루에 {policy.dailyFundLimitGc:N0} GC까지 낼 수 있어요."));
                return;
            }
            // 남은 목표보다 많이 내려고 하면 남은 만큼만 받는다 (100 GC 단위로 올림).
            long room = full - structure.fundedGc;
            int accepted = (int)Math.Min(amountGc, (room + 99) / 100 * 100);
            if (_player.balance < accepted)
            {
                SaveWorldIfDirty();
                done(ApiResult<CityActionDto>.Fail(ApiErrors.InsufficientFunds, "GC가 모자라요."));
                return;
            }

            ApplyLedger(_player, -accepted, LedgerReasons.CityFund, _player.regionCode + ":" + structureCode + ":" + requestId, now);
            int levelBefore = StructureLevel(def, structure);
            structure.fundedGc = Math.Min(full, structure.fundedGc + accepted);
            _player.dailyFundGc += accepted;
            _player.lastActiveAt = now.ToString("o");
            int points = policy.contribPerFund100Gc * (accepted / 100);
            // 기금은 도시의 방어력을 올린다. 위험도는 시설이 한 단계 완성될 때만 내려간다.
            bool leveled = StructureLevel(def, structure) > levelBefore;
            CityEffectDto effect = ApplyCityAction(city, leveled ? RiskReasons.JointDefense : null, points, requestId, now);
            RememberRequest(_player, requestId, RequestCityFund, structureCode, amountGc);
            SaveDb();
            SaveWorldIfDirty();
            done(ApiResult<CityActionDto>.Success(new CityActionDto
            {
                costGc = accepted, gc = _player.balance, contribution = points, city = effect,
                detail = ToCityDetail(city, now),
            }));
        }

        /// <summary>시설 복구나 도시 기금을 소속 도시에 반영한다. reason 이 없으면 기여도만 쌓인다.</summary>
        private CityEffectDto ApplyCityAction(CityRow city, string reason, int points, string reference, DateTime now)
        {
            RiskPolicy policy = Policy;
            int before = city.riskCenti;
            string stageBefore = CityRiskRules.StageOf(before, policy);
            if (reason != null)
            {
                RiskChange change = CityRiskRules.ApplyPlayer(city.riskCenti, reason, 100, policy,
                    ActivePlayers(city.code, now), _player.dailyRiskReduced, out int reducedToday);
                _player.dailyRiskReduced = reducedToday;
                ChangeRisk(city, change, RiskActors.Player, reference, now.ToString("o"));
            }
            city.contribution += points;
            _player.cityContribution += points;
            UpdateSupport(city, now);
            _worldDirty = true;
            return new CityEffectDto
            {
                regionCode = city.code, actor = RiskActors.Player, reason = reason,
                riskBefore = CityRiskRules.ToPoints(before), riskAfter = CityRiskRules.ToPoints(city.riskCenti),
                riskDeltaCenti = city.riskCenti - before,
                stageBefore = stageBefore, stage = CityRiskRules.StageOf(city.riskCenti, policy),
                contribution = points,
                capped = _player.dailyRiskReduced >= policy.dailyPlayerReduceCap * 100,
            };
        }

        // ── 도시 기록: 시간의 흐름 ──────────────────────────────

        /// <summary>
        /// 지금 시각까지의 침공 판정을 처리한다. 실제 서버에서는 예약 작업(cron)이 정해진 간격으로 하는 일이다.
        /// 모의 서버에는 예약 작업이 없어, 도시 기록을 쓰는 요청이 올 때 밀린 만큼 따라잡는다.
        /// </summary>
        private void AdvanceWorld(DateTime now)
        {
            LoadWorld();
            EnsureCities();
            RiskPolicy policy = Policy;
            long nowTick = InvasionRules.TickOf(now, policy);
            if (_world.tick < 0)
            {
                // 처음: 지금 순번부터 시작한다. 지난 일은 만들어 내지 않는다.
                ProcessTick(nowTick);
                _world.tick = nowTick;
                _worldDirty = true;
                return;
            }
            if (nowTick <= _world.tick) return;

            long from = Math.Max(_world.tick + 1, nowTick - MaxCatchUpTicks + 1);
            for (long tick = from; tick <= nowTick; tick++) ProcessTick(tick);
            _world.tick = nowTick;
            TrimWorld();
            _worldDirty = true;
        }

        private void ProcessTick(long tick)
        {
            RiskPolicy policy = Policy;
            DateTime tickStart = InvasionRules.TickStart(tick, policy);
            string at = tickStart.ToString("o");
            string date = KoreaDate(tickStart);
            string oldest = KoreaDate(tickStart.AddDays(-RecentInvasionDays));

            _activeInvasions.Clear();
            for (int i = 0; i < _world.invasions.Count; i++)
                if (_world.invasions[i].IsActive) _activeInvasions[_world.invasions[i].regionCode] = _world.invasions[i];

            for (int i = 0; i < _world.cities.Count; i++)
            {
                CityRow city = _world.cities[i];
                _activeInvasions.TryGetValue(city.code, out InvasionEvent active);

                // 1) 제한 시간이 끝난 침공: 플레이어가 막지 못했으므로 NPC 수비대가 판정한다.
                if (active != null && ParseUtc(active.endsAt) <= tickStart)
                {
                    ResolveExpired(city, active, tick, at);
                    active = null;
                }

                // 2) 하루 한 번, NPC 순찰대가 위험도를 조금 낮춘다.
                if (city.stabilizedDate != date)
                {
                    if (!string.IsNullOrEmpty(city.stabilizedDate))
                    {
                        int delta = CityRiskRules.DailyStabilizeDelta(city.riskCenti, policy);
                        if (delta != 0)
                            ChangeRisk(city, CityRiskRules.ApplyExact(city.riskCenti, RiskReasons.DailyStabilize, delta, policy),
                                RiskActors.Npc, null, at);
                    }
                    city.stabilizedDate = date;
                    city.invasionDates.RemoveAll(d => string.CompareOrdinal(d, oldest) < 0);
                }

                // 3) 새 침공. 한 도시에 침공은 한 번에 하나다.
                if (active == null)
                {
                    InvasionEvent started = InvasionRules.TryStart(policy, city.code, tick, WorldSeed);
                    if (started != null)
                    {
                        started.required = InvasionRules.RequiredWins(policy, started.severity, ActivePlayers(city.code, tickStart));
                        _world.invasions.Add(started);
                        city.invasionDates.Add(date);
                        int quantity = started.kind == InvasionKinds.Zero ? 1 : started.severity;
                        ChangeRisk(city, CityRiskRules.ApplySystem(city.riskCenti, InvasionRules.StartReason(started.kind), quantity, policy),
                            RiskActors.System, started.id, at);
                    }
                }

                // 4) 지원 요청을 열거나 닫는다.
                UpdateSupport(city, tickStart);
            }
        }

        private void ResolveExpired(CityRow city, InvasionEvent invasion, long tick, string at)
        {
            RiskPolicy policy = Policy;
            string status = InvasionRules.ResolveExpired(policy, invasion, DefensePowerOf(city), WorldSeed, tick);
            invasion.status = status;
            invasion.resolvedAt = at;
            city.invasionSeverityTotal += invasion.severity;

            if (status == InvasionStatus.NpcHeld)
            {
                // NPC가 막은 것은 플레이어의 기여로 세지 않는다.
                city.npcHeldCount++;
                ChangeRisk(city, CityRiskRules.ApplySystem(city.riskCenti, RiskReasons.NpcHeld, invasion.severity, policy),
                    RiskActors.Npc, invasion.id, at);
                return;
            }

            city.failedCount++;
            ChangeRisk(city, CityRiskRules.ApplySystem(city.riskCenti, InvasionRules.FailReason(invasion.kind), invasion.severity, policy),
                RiskActors.System, invasion.id, at);
            // 시내까지 들어온 침공을 막지 못하면 주요 시설 한 곳이 피해를 입는다.
            if (invasion.kind == InvasionKinds.Outskirts || Defs.combat.facilities.Count == 0) return;
            FacilityDef def = Defs.combat.facilities[InvasionRules.Roll(WorldSeed, city.code, tick, 5, Defs.combat.facilities.Count)];
            FacilityRow facility = FacilityOf(city, def.code);
            if (facility.damage >= def.maxDamage) return;
            facility.damage++;
            ChangeRisk(city, CityRiskRules.ApplySystem(city.riskCenti, RiskReasons.FacilityDamaged, 1, policy),
                RiskActors.System, invasion.id + ":" + def.code, at);
        }

        /// <summary>위험도를 바꾸고 이력에 남긴다. 실제로 바뀌지 않았으면 남기지 않는다.</summary>
        private void ChangeRisk(CityRow city, RiskChange change, string actor, string reference, string at)
        {
            city.riskCenti = change.after;
            _worldDirty = true;
            if (!change.Changed) return;
            _world.history.Add(new RiskHistoryEntry
            {
                seq = _world.historySeq++,
                regionCode = city.code,
                at = at,
                reason = change.reason,
                actor = actor,
                deltaCenti = change.Delta,
                afterCenti = change.after,
                stage = change.stageAfter,
                reference = reference,
            });
        }

        /// <summary>
        /// 지원 요청을 열거나 닫는다. 긴급 단계가 되면 열리고, 위험도가 충분히 내려가면 해결로 닫힌다.
        /// 아무 도움 없이 시간이 다 되면 "미해결 긴급 임무"로 위험도가 오른다.
        /// </summary>
        private void UpdateSupport(CityRow city, DateTime now)
        {
            RiskPolicy policy = Policy;
            SupportRequest open = null;
            for (int i = 0; i < _world.supports.Count; i++)
            {
                if (_world.supports[i].regionCode != city.code || !_world.supports[i].IsOpen) continue;
                open = _world.supports[i];
                break;
            }

            if (open != null)
            {
                if (SupportRules.IsResolved(policy, city.riskCenti))
                {
                    open.status = SupportStatus.Resolved;
                    open = null;
                    _worldDirty = true;
                }
                else if (ParseUtc(open.closesAt) <= now)
                {
                    open.status = SupportStatus.Expired;
                    if (open.wins <= 0)
                        ChangeRisk(city, CityRiskRules.ApplySystem(city.riskCenti, RiskReasons.EmergencyUnresolved, 1, policy),
                            RiskActors.System, open.id, now.ToString("o"));
                    open = null;
                    _worldDirty = true;
                }
            }

            if (!SupportRules.ShouldOpen(policy, city.riskCenti, open != null)) return;
            _world.supports.Add(new SupportRequest
            {
                id = "sup_" + _world.supportSeq++,
                regionCode = city.code,
                status = SupportStatus.Open,
                openedAt = now.ToString("o"),
                closesAt = now.AddMinutes(Math.Max(1, policy.supportMinutes)).ToString("o"),
            });
            _worldDirty = true;
        }

        /// <summary>오래된 이력과 끝난 침공을 덜어 낸다. 주민이 있는 도시의 기록을 더 많이 남긴다.</summary>
        private void TrimWorld()
        {
            var counts = new Dictionary<string, int>();
            for (int i = _world.history.Count - 1; i >= 0; i--)
            {
                string code = _world.history[i].regionCode;
                counts.TryGetValue(code, out int kept);
                if (kept >= (HasResidents(code) ? HistoryKeepHome : HistoryKeepOther)) _world.history.RemoveAt(i);
                else counts[code] = kept + 1;
            }

            counts.Clear();
            for (int i = _world.invasions.Count - 1; i >= 0; i--)
            {
                InvasionEvent invasion = _world.invasions[i];
                if (invasion.IsActive) continue;
                counts.TryGetValue(invasion.regionCode, out int kept);
                if (kept >= (HasResidents(invasion.regionCode) ? InvasionKeepHome : InvasionKeepOther)) _world.invasions.RemoveAt(i);
                else counts[invasion.regionCode] = kept + 1;
            }

            int closed = 0;
            for (int i = _world.supports.Count - 1; i >= 0; i--)
            {
                if (_world.supports[i].IsOpen) continue;
                if (++closed > SupportKeepClosed) _world.supports.RemoveAt(i);
            }
        }

        // ── 도시 기록: 찾기와 계산 ──────────────────────────────

        private CityRow CityFor(string regionCode)
        {
            if (string.IsNullOrEmpty(regionCode)) return null;
            LoadWorld();
            EnsureCities();
            return _cityIndex.TryGetValue(regionCode, out CityRow city) ? city : null;
        }

        /// <summary>지역 목록의 모든 도시에 기록을 만든다. 처음 위험도는 도시마다 조금씩 다르다.</summary>
        private void EnsureCities()
        {
            IReadOnlyList<RegionDef> regions = Regions.Regions;
            if (_cityIndex.Count == _world.cities.Count && _world.cities.Count >= regions.Count) return;

            _cityIndex.Clear();
            for (int i = 0; i < _world.cities.Count; i++) _cityIndex[_world.cities[i].code] = _world.cities[i];

            RiskPolicy policy = Policy;
            for (int i = 0; i < regions.Count; i++)
            {
                string code = regions[i].code;
                if (_cityIndex.ContainsKey(code)) continue;
                int spread = Math.Max(0, policy.initialSpread);
                int points = policy.initialRisk + (spread > 0 ? InvasionRules.Roll(WorldSeed, code, 0, 9, spread * 2 + 1) - spread : 0);
                var city = new CityRow { code = code, riskCenti = CityRiskRules.Clamp(points * 100) };
                _world.cities.Add(city);
                _cityIndex[code] = city;
                _worldDirty = true;
            }
        }

        private InvasionEvent ActiveInvasionOf(string regionCode)
        {
            for (int i = _world.invasions.Count - 1; i >= 0; i--)
                if (_world.invasions[i].regionCode == regionCode && _world.invasions[i].IsActive) return _world.invasions[i];
            return null;
        }

        /// <summary>최근에 전투에 참여했거나 전입한 주민의 수.</summary>
        private int ActivePlayers(string regionCode, DateTime now)
        {
            DateTime since = now.AddDays(-Math.Max(1, Policy.activeDays));
            int count = 0;
            for (int i = 0; i < _db.players.Count; i++)
            {
                PlayerRow player = _db.players[i];
                if (player.regionCode != regionCode || string.IsNullOrEmpty(player.lastActiveAt)) continue;
                if (ParseUtc(player.lastActiveAt) >= since) count++;
            }
            return count;
        }

        private bool HasResidents(string regionCode)
        {
            RegionRow region = _db.regions.Find(r => r.code == regionCode);
            return region != null && region.residents > 0;
        }

        private static FacilityRow FacilityOf(CityRow city, string code)
        {
            FacilityRow row = city.facilities.Find(f => f.code == code);
            if (row == null)
            {
                row = new FacilityRow { code = code };
                city.facilities.Add(row);
            }
            return row;
        }

        private static CityStructureRow StructureOf(CityRow city, string code)
        {
            CityStructureRow row = city.structures.Find(s => s.code == code);
            if (row == null)
            {
                row = new CityStructureRow { code = code };
                city.structures.Add(row);
            }
            return row;
        }

        private static int StructureLevel(TowerDef def, CityStructureRow row)
        {
            if (def == null || row == null || def.cityGoalGc <= 0) return 0;
            return (int)Math.Min(Math.Max(1, def.cityMaxLevel), row.fundedGc / def.cityGoalGc);
        }

        private int DefensePowerOf(CityRow city)
        {
            int structurePower = 0;
            for (int i = 0; i < city.structures.Count; i++)
            {
                TowerDef def = Defs.FindTower(city.structures[i].code);
                if (def != null && def.IsCityOwned) structurePower += StructureLevel(def, city.structures[i]) * def.cityPower;
            }
            RegionRow region = _db.regions.Find(r => r.code == city.code);
            return CityRiskRules.DefensePower(Policy, region != null ? region.defensePoints : 0, structurePower);
        }

        private RegionStats ToStats(CityRow city, DateTime now)
        {
            RegionRow region = _db.regions.Find(r => r.code == city.code);
            return new RegionStats
            {
                regionCode = city.code,
                residents = region != null ? region.residents : 0,
                activePlayers = ActivePlayers(city.code, now),
                riskCenti = city.riskCenti,
                playerWins = city.playerWins,
                playerAttempts = city.playerAttempts,
                invasionSeverityTotal = city.invasionSeverityTotal,
                repelledSeverity = city.repelledSeverity,
                npcHeldCount = city.npcHeldCount,
                failedCount = city.failedCount,
                contribution = city.contribution,
                supportGiven = city.supportGiven,
                supportReceived = city.supportReceived,
            };
        }

        private Dictionary<string, RankingScore> RankAll(DateTime now, out List<RankingScore> ordered)
        {
            var stats = new List<RegionStats>(_world.cities.Count);
            for (int i = 0; i < _world.cities.Count; i++) stats.Add(ToStats(_world.cities[i], now));
            ordered = RankingRules.Rank(stats, Defs.combat.ranking);
            var byCode = new Dictionary<string, RankingScore>(ordered.Count);
            for (int i = 0; i < ordered.Count; i++) byCode[ordered[i].regionCode] = ordered[i];
            return byCode;
        }

        private CityStatusDto ToCityStatus(CityRow city, RankingScore score, DateTime now)
        {
            RegionRow region = _db.regions.Find(r => r.code == city.code);
            var dto = new CityStatusDto
            {
                regionCode = city.code,
                risk = CityRiskRules.ToPoints(city.riskCenti),
                stage = CityRiskRules.StageOf(city.riskCenti, Policy),
                residents = region != null ? region.residents : 0,
                activePlayers = ActivePlayers(city.code, now),
                defensePoints = region != null ? region.defensePoints : 0,
                defensePower = DefensePowerOf(city),
                recentInvasions = city.invasionDates.Count,
                contribution = city.contribution,
            };
            if (score != null)
            {
                dto.rank = score.rank;
                dto.score = score.total;
                dto.playerDefended = score.playerDefended;
            }
            InvasionEvent invasion = ActiveInvasionOf(city.code);
            if (invasion != null)
            {
                dto.invasionKind = invasion.kind;
                dto.invasionSeverity = invasion.severity;
                dto.invasionProgress = invasion.progress;
                dto.invasionRequired = invasion.required;
                dto.invasionEndsAt = invasion.endsAt;
            }
            for (int i = 0; i < _world.supports.Count; i++)
            {
                if (_world.supports[i].regionCode != city.code || !_world.supports[i].IsOpen) continue;
                dto.supportRequestId = _world.supports[i].id;
                break;
            }
            return dto;
        }

        private CityDetailDto ToCityDetail(CityRow city, DateTime now)
        {
            Dictionary<string, RankingScore> scores = RankAll(now, out _);
            scores.TryGetValue(city.code, out RankingScore score);
            bool isHome = IsResident(_player) && _player.regionCode == city.code;
            var detail = new CityDetailDto
            {
                status = ToCityStatus(city, score, now),
                score = score,
                isHome = isHome,
                myContribution = isHome ? _player.cityContribution : 0,
            };
            detail.mood = RiskStages.Mood(detail.status.stage);

            for (int i = _world.history.Count - 1; i >= 0 && detail.history.Count < DetailHistoryCount; i--)
                if (_world.history[i].regionCode == city.code) detail.history.Add(_world.history[i]);
            for (int i = _world.invasions.Count - 1; i >= 0 && detail.invasions.Count < DetailInvasionCount; i--)
                if (_world.invasions[i].regionCode == city.code) detail.invasions.Add(_world.invasions[i]);

            for (int i = 0; i < Defs.combat.facilities.Count; i++)
            {
                FacilityDef def = Defs.combat.facilities[i];
                FacilityRow row = city.facilities.Find(f => f.code == def.code);
                detail.facilities.Add(new FacilityStatusDto
                {
                    code = def.code, name = def.name, damage = row != null ? row.damage : 0,
                    maxDamage = def.maxDamage, repairGc = def.repairGc,
                });
            }
            for (int i = 0; i < Defs.towers.Count; i++)
            {
                TowerDef def = Defs.towers[i];
                if (!def.IsCityOwned || def.comingSoon) continue;
                CityStructureRow row = city.structures.Find(s => s.code == def.code);
                int level = StructureLevel(def, row);
                detail.structures.Add(new CityStructureDto
                {
                    code = def.code, name = def.name, level = level, maxLevel = Math.Max(1, def.cityMaxLevel),
                    fundedGc = row != null ? row.fundedGc - (long)level * def.cityGoalGc : 0,
                    goalGc = def.cityGoalGc, power = level * def.cityPower,
                });
            }
            for (int i = 0; i < _world.supports.Count; i++)
            {
                if (_world.supports[i].regionCode != city.code || !_world.supports[i].IsOpen) continue;
                detail.support = _world.supports[i];
                break;
            }
            return detail;
        }

        // ── 도시 기록: 저장 ─────────────────────────────────────

        private void LoadWorld()
        {
            if (_world != null) return;
            string text = _store.Read(WorldFile);
            if (!string.IsNullOrEmpty(text))
            {
                WorldDb parsed = TryParse<WorldDb>(text);
                if (parsed != null && parsed.cities != null)
                {
                    if (parsed.invasions == null) parsed.invasions = new List<InvasionEvent>();
                    if (parsed.history == null) parsed.history = new List<RiskHistoryEntry>();
                    if (parsed.supports == null) parsed.supports = new List<SupportRequest>();
                    if (parsed.historySeq < 1) parsed.historySeq = 1;
                    if (parsed.supportSeq < 1) parsed.supportSeq = 1;
                    for (int i = 0; i < parsed.cities.Count; i++)
                    {
                        CityRow city = parsed.cities[i];
                        if (city.facilities == null) city.facilities = new List<FacilityRow>();
                        if (city.structures == null) city.structures = new List<CityStructureRow>();
                        if (city.invasionDates == null) city.invasionDates = new List<string>();
                    }
                    _world = parsed;
                    _cityIndex.Clear();
                    return;
                }
                // 읽을 수 없는 도시 기록은 지우지 않고 이름을 바꿔 남겨 둔다. 도시 기록은 새로 시작한다.
                _store.Move(WorldFile, $"mock_world.unreadable-{_clock.UtcNow:yyyyMMdd-HHmmss}.json");
            }
            _world = new WorldDb();
            _cityIndex.Clear();
        }

        private void SaveWorldIfDirty()
        {
            if (!_worldDirty || _world == null) return;
            _store.Write(WorldFile, JsonUtility.ToJson(_world));
            _worldDirty = false;
        }

        private static DateTime ParseUtc(string iso) =>
            DateTime.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();

        // ── 점검용 ──────────────────────────────────────────────

        /// <summary>도시 기록이 규칙에 맞는지 검사한다. 맞으면 null. 자동 테스트가 쓴다.</summary>
        public string FindWorldInconsistency()
        {
            LoadWorld();
            var active = new HashSet<string>();
            for (int i = 0; i < _world.invasions.Count; i++)
            {
                InvasionEvent invasion = _world.invasions[i];
                if (invasion.severity < 1 || invasion.severity > 3) return "침공 규모가 범위를 벗어남: " + invasion.id;
                if (invasion.IsActive && !active.Add(invasion.regionCode)) return "한 도시에 진행 중인 침공이 둘: " + invasion.regionCode;
            }
            var open = new HashSet<string>();
            for (int i = 0; i < _world.supports.Count; i++)
                if (_world.supports[i].IsOpen && !open.Add(_world.supports[i].regionCode))
                    return "한 도시에 열린 지원 요청이 둘: " + _world.supports[i].regionCode;
            for (int i = 0; i < _world.cities.Count; i++)
            {
                CityRow city = _world.cities[i];
                if (city.riskCenti < 0 || city.riskCenti > CityRiskRules.MaxCenti) return "위험도가 범위를 벗어남: " + city.code;
                if (city.playerWins > city.playerAttempts) return "승리가 시도보다 많음: " + city.code;
                if (city.repelledSeverity > city.invasionSeverityTotal) return "막은 침공이 전체보다 많음: " + city.code;
            }
            // 이력의 마지막 값은 지금 위험도와 같아야 한다.
            var last = new Dictionary<string, int>();
            for (int i = 0; i < _world.history.Count; i++) last[_world.history[i].regionCode] = _world.history[i].afterCenti;
            foreach (KeyValuePair<string, int> pair in last)
            {
                CityRow city = _cityIndex.TryGetValue(pair.Key, out CityRow found) ? found : null;
                if (city != null && city.riskCenti != pair.Value) return "이력의 마지막 값과 위험도가 다름: " + pair.Key;
            }
            return null;
        }
    }
}
