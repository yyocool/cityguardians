using System;
using System.Collections.Generic;

namespace CityGuardians.Domain
{
    // 도시 위험도 (docs/CITY_RISK_SYSTEM.md).
    //
    // - 위험도는 서버가 계산한다. 클라이언트는 전투 결과만 보내고, 위험도 숫자를 보내는 요청은 없다.
    // - 서버 안에서는 1/100 단위(0~10000)로 저장하고 화면에는 0~100으로 보여 준다.
    //   여러 사람의 작은 기여가 반올림으로 사라지지 않게 하기 위해서다.
    // - 위험도를 올리는 것은 시스템 사건(침공, 막지 못한 침공, 시설 피해)뿐이다.
    //   플레이어의 활동은 위험도를 내리기만 한다. 일부러 져서 남의 도시를 위험하게 만들 수 없다.
    // - 위험도는 은행 잔액이나 게임 내 신용점수와 연결되어 있지 않다. 이 파일은 지갑을 참조하지 않는다.
    // 실제 서버(PHP)의 계산이 이 규칙과 같아야 한다.

    public static class RiskStages
    {
        public const string Safe = "safe";
        public const string Caution = "caution";
        public const string Danger = "danger";
        public const string Emergency = "emergency";

        public static readonly string[] All = { Safe, Caution, Danger, Emergency };

        public static string DisplayName(string stage)
        {
            switch (stage)
            {
                case Caution: return "주의";
                case Danger: return "위험";
                case Emergency: return "긴급";
                default: return "안전";
            }
        }

        /// <summary>색을 보지 않아도 구분되는 모양 표시. 지도와 목록에서 이름과 함께 쓴다.</summary>
        public static string Icon(string stage)
        {
            switch (stage)
            {
                case Caution: return "▲";
                case Danger: return "◆";
                case Emergency: return "■";
                default: return "●";
            }
        }

        /// <summary>단계의 순서. 안전 0, 긴급 3.</summary>
        public static int Order(string stage)
        {
            switch (stage)
            {
                case Caution: return 1;
                case Danger: return 2;
                case Emergency: return 3;
                default: return 0;
            }
        }

        /// <summary>단계가 바뀌면 달라지는 도시의 분위기 한 줄.</summary>
        public static string Mood(string stage)
        {
            switch (stage)
            {
                case Caution: return "순찰이 늘었어요. 사냥 임무가 더 많이 나와요.";
                case Danger: return "경보가 울려요. 방어전과 긴급 출동이 필요해요.";
                case Emergency: return "도시가 위험해요! 주변 도시에 지원을 요청했어요.";
                default: return "도시가 평화로워요.";
            }
        }
    }

    public static class RiskReasons
    {
        // 올리는 사건 (시스템)
        public const string InvasionStarted = "invasion_started";
        /// <summary>외곽 방어 실패: 침공을 제한 시간 안에 아무도 막지 못했다.</summary>
        public const string DefenseFailed = "defense_failed";
        /// <summary>시내 침입을 막지 못했다.</summary>
        public const string IntrusionFailed = "intrusion_failed";
        public const string FacilityDamaged = "facility_damaged";
        /// <summary>지원 요청이 아무 도움 없이 끝났다.</summary>
        public const string EmergencyUnresolved = "emergency_unresolved";
        public const string ZeroEvent = "zero_event";

        // 내리는 활동
        public const string DefenseWon = "defense_won";
        public const string HuntCompleted = "hunt_completed";
        public const string FpsRepelled = "fps_repelled";
        public const string FacilityRepaired = "facility_repaired";
        /// <summary>도시 공동 방어 활동: 공용 방어시설 기금, 다른 도시 지원.</summary>
        public const string JointDefense = "joint_defense";
        /// <summary>특별 위험 제거 임무: ZERO 이벤트를 막아 냈다.</summary>
        public const string SpecialMission = "special_mission";
        /// <summary>플레이어들이 침공을 끝까지 막아 냈다.</summary>
        public const string InvasionRepelled = "invasion_repelled";
        /// <summary>NPC 수비대가 침공을 막았다. 플레이어의 기여가 아니다.</summary>
        public const string NpcHeld = "npc_held";
        /// <summary>하루 한 번, NPC 순찰대가 위험도를 조금 낮춘다.</summary>
        public const string DailyStabilize = "daily_stabilize";
        public const string AdminAdjust = "admin_adjust";

        public static string DisplayName(string reason)
        {
            switch (reason)
            {
                case InvasionStarted: return "AI 침공 발생";
                case DefenseFailed: return "외곽 방어 실패";
                case IntrusionFailed: return "시내 침입";
                case FacilityDamaged: return "주요 시설 피해";
                case EmergencyUnresolved: return "미해결 긴급 임무";
                case ZeroEvent: return "ZERO 이벤트";
                case DefenseWon: return "타워디펜스 성공";
                case HuntCompleted: return "사냥 임무 완료";
                case FpsRepelled: return "시내 침입 격퇴";
                case FacilityRepaired: return "방어시설 복구";
                case JointDefense: return "도시 공동 방어";
                case SpecialMission: return "특별 위험 제거";
                case InvasionRepelled: return "침공 격퇴 완료";
                case NpcHeld: return "NPC 수비대 방어";
                case DailyStabilize: return "NPC 순찰";
                case AdminAdjust: return "관리자 조정";
                default: return "기타";
            }
        }
    }

    /// <summary>위험도를 바꾼 주체. NPC의 방어와 실제 플레이어의 기여를 구분한다.</summary>
    public static class RiskActors
    {
        /// <summary>그 도시의 주민 플레이어.</summary>
        public const string Player = "player";
        /// <summary>다른 도시에서 지원 온 플레이어.</summary>
        public const string Supporter = "supporter";
        public const string Npc = "npc";
        public const string System = "system";
        public const string Admin = "admin";

        public static string DisplayName(string actor)
        {
            switch (actor)
            {
                case Player: return "가디언";
                case Supporter: return "지원 가디언";
                case Npc: return "NPC 수비대";
                case Admin: return "관리자";
                default: return "시스템";
            }
        }

        public static bool IsRealPlayer(string actor) => actor == Player || actor == Supporter;
    }

    [Serializable]
    public sealed class RiskWeight
    {
        public string reason;
        /// <summary>한 번에 바뀌는 양 (1/100 단위). 양수면 올리고 음수면 내린다.</summary>
        public int deltaCenti;

        public RiskWeight() { }

        public RiskWeight(string reason, int deltaCenti)
        {
            this.reason = reason;
            this.deltaCenti = deltaCenti;
        }
    }

    /// <summary>
    /// 위험도 계산 정책. 관리자가 조정한다 (서버의 city_risk_policy). 수치는 초기값이다.
    /// </summary>
    [Serializable]
    public sealed class RiskPolicy
    {
        public int version = 1;
        public List<RiskWeight> weights = new List<RiskWeight>();

        // 단계 경계 (0~100)
        public int cautionAt = 25;
        public int dangerAt = 50;
        public int emergencyAt = 75;

        /// <summary>새 도시의 처음 위험도 (0~100).</summary>
        public int initialRisk = 20;
        /// <summary>처음 위험도가 도시마다 달라지는 폭 (±). 0이면 모든 도시가 같은 값에서 시작한다.</summary>
        public int initialSpread = 12;
        /// <summary>
        /// 활성 가디언이 이 수를 넘는 도시에서는 한 사람의 활동이 그만큼 나뉘어 반영된다.
        /// 사람이 많은 도시가 자동으로 유리해지지 않게 하는 보정이다.
        /// </summary>
        public int activePlayerNorm = 20;
        /// <summary>최근 며칠 안에 접속한 주민을 활성 가디언으로 세는가.</summary>
        public int activeDays = 7;
        /// <summary>사냥은 이만큼 쓰러뜨릴 때마다 한 번 반영된다.</summary>
        public int huntKillsPerStep = 10;
        /// <summary>위험 단계 이상의 도시에서는 사냥이 특별 사냥 임무로 쳐져 이 비율(%)로 반영된다.</summary>
        public int huntHighRiskPercent = 200;
        /// <summary>한 사람이 하루에 낮출 수 있는 위험도의 한도 (0~100).</summary>
        public int dailyPlayerReduceCap = 20;
        /// <summary>침공이 없을 때의 방어전·FPS 임무(순찰)가 반영되는 비율 (%).</summary>
        public int patrolPercent = 50;

        // 침공
        /// <summary>침공을 판정하는 간격(분). 서버의 예약 작업이 이 간격으로 돈다.</summary>
        public int tickMinutes = 60;
        /// <summary>한 번의 판정에서 도시 하나에 침공이 일어날 확률 (천분율). 침공 빈도.</summary>
        public int invasionChancePermille = 40;
        /// <summary>침공이 이어지는 시간(분). 이 안에 막지 못하면 NPC 수비대가 판정한다.</summary>
        public int invasionMinutes = 180;
        /// <summary>침공 가운데 시내 침입(FPS)의 비율 (천분율).</summary>
        public int intrusionPermille = 350;
        /// <summary>침공 가운데 ZERO 이벤트의 비율 (천분율).</summary>
        public int zeroEventPermille = 20;

        // NPC 수비대
        /// <summary>제한 시간 안에 플레이어가 막지 못한 침공을 NPC 수비대가 막을 기본 확률 (%).</summary>
        public int npcHoldBasePercent = 80;
        /// <summary>방어력이 이만큼 오를 때마다 NPC가 막을 확률 +1%.</summary>
        public int defensePowerPerPercent = 20;
        public int npcHoldMaxBonusPercent = 20;
        /// <summary>침공 규모가 한 단계 클 때마다 NPC가 막을 확률이 이만큼 준다.</summary>
        public int npcHoldSeverityPenaltyPercent = 10;
        /// <summary>하루 한 번 NPC 순찰대가 낮추는 비율 (지금 위험도의 %).</summary>
        public int dailyStabilizePercent = 20;
        public int baseGarrisonPower = 100;
        /// <summary>방어 포인트가 방어력에 더해지는 한도.</summary>
        public int defensePointsPowerCap = 300;

        // 도시 간 지원
        /// <summary>위험도가 이 값 이상이면 지원 요청이 열린다 (0~100).</summary>
        public int supportOpenAt = 75;
        /// <summary>위험도가 이 값 아래로 내려가면 지원 요청이 해결된 것으로 닫힌다.</summary>
        public int supportCloseBelow = 50;
        public int supportMinutes = 360;

        // 도시 기여도 (점수)
        public int contribDefenseWin = 10;
        public int contribFpsWin = 12;
        public int contribHuntStep = 1;
        public int contribRepair = 5;
        /// <summary>도시 공용 방어시설 기금에 100 GC를 낼 때마다 받는 기여도.</summary>
        public int contribPerFund100Gc = 2;
        /// <summary>침공 중의 승리는 규모 한 단계마다 기여도가 이만큼(%) 더해진다. 난이도 보정이다.</summary>
        public int contribSeverityBonusPercent = 50;
        /// <summary>한 사람이 하루에 고칠 수 있는 시설 피해 단계 수.</summary>
        public int dailyRepairLimit = 3;
        /// <summary>한 사람이 하루에 도시 기금에 낼 수 있는 GC.</summary>
        public int dailyFundLimitGc = 2000;

        public int Delta(string reason)
        {
            for (int i = 0; i < weights.Count; i++)
                if (weights[i] != null && weights[i].reason == reason) return weights[i].deltaCenti;
            return 0;
        }

        public void SetDelta(string reason, int deltaCenti)
        {
            for (int i = 0; i < weights.Count; i++)
            {
                if (weights[i] == null || weights[i].reason != reason) continue;
                weights[i].deltaCenti = deltaCenti;
                return;
            }
            weights.Add(new RiskWeight(reason, deltaCenti));
        }

        public static RiskPolicy CreateDefault()
        {
            var policy = new RiskPolicy();
            // 올리는 사건. 침공은 규모(1~3)를 곱한다.
            policy.weights.Add(new RiskWeight(RiskReasons.InvasionStarted, 800));
            policy.weights.Add(new RiskWeight(RiskReasons.DefenseFailed, 300));
            policy.weights.Add(new RiskWeight(RiskReasons.IntrusionFailed, 300));
            policy.weights.Add(new RiskWeight(RiskReasons.FacilityDamaged, 200));
            policy.weights.Add(new RiskWeight(RiskReasons.EmergencyUnresolved, 400));
            policy.weights.Add(new RiskWeight(RiskReasons.ZeroEvent, 1000));
            // 내리는 활동
            policy.weights.Add(new RiskWeight(RiskReasons.DefenseWon, -600));
            policy.weights.Add(new RiskWeight(RiskReasons.HuntCompleted, -100));
            policy.weights.Add(new RiskWeight(RiskReasons.FpsRepelled, -800));
            policy.weights.Add(new RiskWeight(RiskReasons.FacilityRepaired, -200));
            policy.weights.Add(new RiskWeight(RiskReasons.JointDefense, -300));
            policy.weights.Add(new RiskWeight(RiskReasons.SpecialMission, -1000));
            policy.weights.Add(new RiskWeight(RiskReasons.InvasionRepelled, -300));
            // NPC 수비대가 막아도 침공의 흔적은 남는다. 플레이어가 직접 막는 쪽(승리 + 격퇴)이 언제나 더 많이 내린다.
            policy.weights.Add(new RiskWeight(RiskReasons.NpcHeld, -700));
            return policy;
        }
    }

    /// <summary>위험도가 한 번 바뀐 결과.</summary>
    public sealed class RiskChange
    {
        public string reason;
        /// <summary>바뀌기 전 (1/100 단위).</summary>
        public int before;
        public int after;
        public string stageBefore;
        public string stageAfter;

        public int Delta => after - before;
        public bool Changed => after != before;
        public bool StageChanged => stageBefore != stageAfter;
    }

    /// <summary>위험도 변경 이력의 한 줄. 서버의 city_risk_history 한 행.</summary>
    [Serializable]
    public sealed class RiskHistoryEntry
    {
        public long seq;
        public string regionCode;
        public string at;
        public string reason;
        /// <summary>RiskActors.</summary>
        public string actor;
        public int deltaCenti;
        public int afterCenti;
        public string stage;
        /// <summary>관련된 것 (침공 ID, 전투 기록 ID 등).</summary>
        public string reference;
    }

    public static class CityRiskRules
    {
        public const int MaxCenti = 10000;

        public static int Clamp(int centi) => centi < 0 ? 0 : centi > MaxCenti ? MaxCenti : centi;

        /// <summary>화면에 보이는 값 (0~100).</summary>
        public static int ToPoints(int centi) => Clamp(centi) / 100;

        public static string StageOf(int centi, RiskPolicy policy)
        {
            int points = ToPoints(centi);
            int caution = policy != null ? policy.cautionAt : 25;
            int danger = policy != null ? policy.dangerAt : 50;
            int emergency = policy != null ? policy.emergencyAt : 75;
            if (points >= emergency) return RiskStages.Emergency;
            if (points >= danger) return RiskStages.Danger;
            if (points >= caution) return RiskStages.Caution;
            return RiskStages.Safe;
        }

        /// <summary>
        /// 한 사람의 활동이 반영되는 비율(%). 활성 가디언이 기준 수 이하면 100, 넘으면 그만큼 나뉜다.
        /// 기준 수의 열 배인 도시에서는 한 사람의 활동이 10%만 반영된다. 대신 활동하는 사람이 열 배다.
        /// </summary>
        public static int ScalePercent(RiskPolicy policy, int activePlayers)
        {
            int norm = Math.Max(1, policy.activePlayerNorm);
            if (activePlayers <= norm) return 100;
            return Math.Max(1, 100 * norm / activePlayers);
        }

        /// <summary>시스템 사건을 반영한다. quantity 는 침공 규모, 피해 입은 시설 수 같은 배수다.</summary>
        public static RiskChange ApplySystem(int current, string reason, int quantity, RiskPolicy policy)
        {
            long delta = (long)policy.Delta(reason) * Math.Max(0, quantity);
            return Finish(current, current + delta, reason, policy);
        }

        /// <summary>정해진 양만큼 바꾼다 (관리자 조정, 하루 한 번의 순찰).</summary>
        public static RiskChange ApplyExact(int current, string reason, int deltaCenti, RiskPolicy policy) =>
            Finish(current, (long)current + deltaCenti, reason, policy);

        /// <summary>
        /// 플레이어의 활동을 반영한다. 내리는 방향만 반영되고, 활성 가디언 수로 나뉘며,
        /// 한 사람의 하루 한도를 넘지 않는다.
        /// </summary>
        /// <param name="unitsPercent">활동의 크기 (%). 100이 한 번이다. 순찰은 patrolPercent.</param>
        /// <param name="reducedToday">이 플레이어가 오늘 이미 낮춘 양 (1/100 단위).</param>
        public static RiskChange ApplyPlayer(int current, string reason, int unitsPercent, RiskPolicy policy,
            int activePlayers, int reducedToday, out int newReducedToday)
        {
            newReducedToday = Math.Max(0, reducedToday);
            int perUnit = policy.Delta(reason);
            // 정책을 잘못 고쳐 양수가 되더라도 플레이어의 활동이 위험도를 올리지는 않는다.
            if (perUnit >= 0 || unitsPercent <= 0) return Finish(current, current, reason, policy);

            long wanted = -(long)perUnit * unitsPercent / 100;
            wanted = wanted * ScalePercent(policy, activePlayers) / 100;
            long room = Math.Max(0L, (long)policy.dailyPlayerReduceCap * 100 - newReducedToday);
            long applied = Math.Min(Math.Min(wanted, room), Clamp(current));
            if (applied < 0) applied = 0;
            newReducedToday += (int)applied;
            return Finish(current, current - applied, reason, policy);
        }

        /// <summary>하루 한 번의 NPC 순찰이 낮추는 양 (음수).</summary>
        public static int DailyStabilizeDelta(int current, RiskPolicy policy) =>
            -(int)((long)Clamp(current) * Math.Max(0, policy.dailyStabilizePercent) / 100);

        /// <summary>도시의 방어력: NPC 수비대 + 주민이 쌓은 방어 포인트 + 도시 공용 방어시설.</summary>
        public static int DefensePower(RiskPolicy policy, long defensePoints, int structurePower)
        {
            long fromPoints = Math.Min(Math.Max(0, policy.defensePointsPowerCap), Math.Max(0, defensePoints) / 10);
            return policy.baseGarrisonPower + (int)fromPoints + Math.Max(0, structurePower);
        }

        private static RiskChange Finish(int before, long after, string reason, RiskPolicy policy)
        {
            int b = Clamp(before);
            int a = after < 0 ? 0 : after > MaxCenti ? MaxCenti : (int)after;
            return new RiskChange
            {
                reason = reason, before = b, after = a,
                stageBefore = StageOf(b, policy), stageAfter = StageOf(a, policy),
            };
        }
    }

    // ── 침공 이벤트 ─────────────────────────────────────────────

    public static class InvasionKinds
    {
        /// <summary>외곽 침공. 타워디펜스로 막는다.</summary>
        public const string Outskirts = "outskirts";
        /// <summary>시내 침입. FPS 임무로 격퇴한다.</summary>
        public const string Intrusion = "intrusion";
        /// <summary>ZERO 이벤트. 어느 모드로든 막을 수 있는 큰 침공.</summary>
        public const string Zero = "zero";

        public static string DisplayName(string kind)
        {
            switch (kind)
            {
                case Intrusion: return "시내 침입";
                case Zero: return "ZERO 이벤트";
                default: return "외곽 침공";
            }
        }
    }

    public static class InvasionStatus
    {
        public const string Active = "active";
        /// <summary>플레이어들이 막았다.</summary>
        public const string Repelled = "repelled";
        /// <summary>NPC 수비대가 막았다.</summary>
        public const string NpcHeld = "npc_held";
        public const string Failed = "failed";

        public static string DisplayName(string status)
        {
            switch (status)
            {
                case Repelled: return "가디언이 격퇴";
                case NpcHeld: return "NPC 수비대가 방어";
                case Failed: return "방어 실패";
                default: return "진행 중";
            }
        }
    }

    /// <summary>침공 이벤트. 서버의 city_invasion_events 한 행.</summary>
    [Serializable]
    public sealed class InvasionEvent
    {
        public string id;
        public string regionCode;
        public string kind;
        /// <summary>규모 1~3.</summary>
        public int severity;
        public string status;
        public string startedAt;
        public string endsAt;
        public string resolvedAt;
        /// <summary>막아 내는 데 필요한 플레이어의 승리 수.</summary>
        public int required;
        public int progress;

        public bool IsActive => status == InvasionStatus.Active;
    }

    public static class InvasionRules
    {
        /// <summary>
        /// 같은 입력에 늘 같은 값을 내는 판정 (0 이상 modulo 미만).
        /// 서버가 같은 시각의 판정을 두 번 돌려도 결과가 같아, 예약 작업이 겹쳐도 침공이 두 번 생기지 않는다.
        /// </summary>
        public static int Roll(int seed, string regionCode, long tick, int salt, int modulo)
        {
            if (modulo <= 0) return 0;
            unchecked
            {
                uint h = 2166136261;
                h = (h ^ (uint)seed) * 16777619;
                if (regionCode != null)
                    for (int i = 0; i < regionCode.Length; i++) h = (h ^ regionCode[i]) * 16777619;
                h = (h ^ (uint)(tick & 0xFFFFFFFF)) * 16777619;
                h = (h ^ (uint)((tick >> 32) & 0xFFFFFFFF)) * 16777619;
                h = (h ^ (uint)salt) * 16777619;
                h ^= h >> 16;
                h *= 0x7feb352d;
                h ^= h >> 15;
                h *= 0x846ca68b;
                h ^= h >> 16;
                return (int)(h % (uint)modulo);
            }
        }

        /// <summary>시각을 판정 순번으로 바꾼다.</summary>
        public static long TickOf(DateTime utc, RiskPolicy policy)
        {
            long minutes = (long)(utc - new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMinutes;
            return minutes / Math.Max(1, policy.tickMinutes);
        }

        public static DateTime TickStart(long tick, RiskPolicy policy) =>
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(tick * (long)Math.Max(1, policy.tickMinutes));

        /// <summary>이번 판정에서 그 도시에 침공이 시작되는가. 시작되면 이벤트를 돌려준다 (required 는 호출한 쪽이 채운다).</summary>
        public static InvasionEvent TryStart(RiskPolicy policy, string regionCode, long tick, int seed)
        {
            if (Roll(seed, regionCode, tick, 1, 1000) >= policy.invasionChancePermille) return null;

            int kindRoll = Roll(seed, regionCode, tick, 2, 1000);
            string kind = kindRoll < policy.zeroEventPermille ? InvasionKinds.Zero
                : kindRoll < policy.zeroEventPermille + policy.intrusionPermille ? InvasionKinds.Intrusion
                : InvasionKinds.Outskirts;
            int severityRoll = Roll(seed, regionCode, tick, 3, 100);
            int severity = kind == InvasionKinds.Zero ? 3 : severityRoll < 60 ? 1 : severityRoll < 90 ? 2 : 3;

            DateTime start = TickStart(tick, policy);
            return new InvasionEvent
            {
                id = "inv_" + regionCode + "_" + tick,
                regionCode = regionCode,
                kind = kind,
                severity = severity,
                status = InvasionStatus.Active,
                startedAt = start.ToString("o"),
                endsAt = start.AddMinutes(Math.Max(1, policy.invasionMinutes)).ToString("o"),
            };
        }

        /// <summary>
        /// 막아 내는 데 필요한 승리 수. 활성 가디언이 많은 도시는 그만큼 더 필요하다 (도시 크기 보정).
        /// </summary>
        public static int RequiredWins(RiskPolicy policy, int severity, int activePlayers)
        {
            int norm = Math.Max(1, policy.activePlayerNorm);
            int groups = Math.Max(1, (activePlayers + norm - 1) / norm);
            return Math.Max(1, severity) * groups;
        }

        /// <summary>침공이 시작될 때 위험도를 올리는 사유.</summary>
        public static string StartReason(string kind) =>
            kind == InvasionKinds.Zero ? RiskReasons.ZeroEvent : RiskReasons.InvasionStarted;

        /// <summary>그 전투 모드의 승리가 이 침공을 막는 데 쓰이는가.</summary>
        public static bool Counts(InvasionEvent invasion, string modeCode)
        {
            if (invasion == null || !invasion.IsActive) return false;
            switch (invasion.kind)
            {
                case InvasionKinds.Outskirts: return modeCode == CombatModeCodes.Defense;
                case InvasionKinds.Intrusion: return modeCode == CombatModeCodes.Fps;
                case InvasionKinds.Zero: return modeCode == CombatModeCodes.Defense || modeCode == CombatModeCodes.Fps;
                default: return false;
            }
        }

        /// <summary>승리 한 번이 위험도를 내리는 사유.</summary>
        public static string WinReason(string modeCode) =>
            modeCode == CombatModeCodes.Fps ? RiskReasons.FpsRepelled : RiskReasons.DefenseWon;

        /// <summary>플레이어들이 끝까지 막았을 때 한 번 더 내리는 사유.</summary>
        public static string RepelReason(string kind) =>
            kind == InvasionKinds.Zero ? RiskReasons.SpecialMission : RiskReasons.InvasionRepelled;

        /// <summary>막지 못했을 때 올리는 사유.</summary>
        public static string FailReason(string kind) =>
            kind == InvasionKinds.Outskirts ? RiskReasons.DefenseFailed : RiskReasons.IntrusionFailed;

        /// <summary>제한 시간이 끝난 침공을 NPC 수비대가 막을 확률 (%).</summary>
        public static int NpcHoldPercent(RiskPolicy policy, int defensePower, InvasionEvent invasion)
        {
            int bonus = Math.Min(Math.Max(0, policy.npcHoldMaxBonusPercent),
                Math.Max(0, defensePower - policy.baseGarrisonPower) / Math.Max(1, policy.defensePowerPerPercent));
            int penalty = policy.npcHoldSeverityPenaltyPercent * (Math.Max(1, invasion.severity) - 1);
            // 플레이어가 조금이라도 싸웠다면 그만큼 NPC가 막기 쉬워진다.
            int helped = invasion.required > 0 ? 20 * Math.Min(invasion.progress, invasion.required) / invasion.required : 0;
            int percent = policy.npcHoldBasePercent + bonus - penalty + helped;
            return percent < 5 ? 5 : percent > 95 ? 95 : percent;
        }

        /// <summary>제한 시간이 끝난 침공의 결과: NPC 수비대가 막았거나(NpcHeld) 실패했다(Failed).</summary>
        public static string ResolveExpired(RiskPolicy policy, InvasionEvent invasion, int defensePower, int seed, long tick)
        {
            int roll = Roll(seed, invasion.regionCode, tick, 4, 100);
            return roll < NpcHoldPercent(policy, defensePower, invasion) ? InvasionStatus.NpcHeld : InvasionStatus.Failed;
        }
    }

    // ── 도시 간 지원 ────────────────────────────────────────────

    public static class SupportStatus
    {
        public const string Open = "open";
        /// <summary>위험도가 내려가 해결되었다.</summary>
        public const string Resolved = "resolved";
        public const string Expired = "expired";
    }

    /// <summary>지원 요청. 서버의 city_support_requests 한 행.</summary>
    [Serializable]
    public sealed class SupportRequest
    {
        public string id;
        public string regionCode;
        public string status;
        public string openedAt;
        public string closesAt;
        /// <summary>지원 온 가디언의 승리 수.</summary>
        public int wins;
        /// <summary>지원으로 쌓인 기여도.</summary>
        public long points;

        public bool IsOpen => status == SupportStatus.Open;
    }

    public static class SupportReasons
    {
        public const string NotResident = "not_resident";
        public const string OwnCity = "own_city";
        public const string NotOpen = "not_open";
        public const string TooFar = "too_far";

        public static string Message(string reason)
        {
            switch (reason)
            {
                case NotResident: return "전입신고를 한 가디언만 다른 도시를 지원할 수 있어요.";
                case OwnCity: return "우리 도시는 출동 게이트에서 바로 지킬 수 있어요.";
                case NotOpen: return "이미 끝난 지원 요청이에요.";
                case TooFar: return "같은 시·도의 도시만 지원할 수 있어요.";
                default: return "지원할 수 없어요.";
            }
        }
    }

    public static class SupportRules
    {
        /// <summary>지원 요청을 새로 열어야 하는가.</summary>
        public static bool ShouldOpen(RiskPolicy policy, int riskCenti, bool hasOpen) =>
            !hasOpen && CityRiskRules.ToPoints(riskCenti) >= policy.supportOpenAt;

        /// <summary>위험도가 충분히 내려가 요청이 해결되었는가.</summary>
        public static bool IsResolved(RiskPolicy policy, int riskCenti) =>
            CityRiskRules.ToPoints(riskCenti) < policy.supportCloseBelow;

        /// <summary>
        /// 지원에 나설 수 없는 이유. 나설 수 있으면 null.
        /// "주변 도시"는 지금은 같은 시·도다. 소속 도시는 바뀌지 않는다.
        /// </summary>
        public static string CheckJoin(SupportRequest request, string homeRegionCode, RegionCatalog catalog)
        {
            if (string.IsNullOrEmpty(homeRegionCode)) return SupportReasons.NotResident;
            if (request == null || !request.IsOpen) return SupportReasons.NotOpen;
            if (request.regionCode == homeRegionCode) return SupportReasons.OwnCity;
            RegionDef home = catalog.FindRegion(homeRegionCode);
            RegionDef target = catalog.FindRegion(request.regionCode);
            if (home == null || target == null || home.sidoCode != target.sidoCode) return SupportReasons.TooFar;
            return null;
        }
    }

    // ── 지역 랭킹 ───────────────────────────────────────────────

    /// <summary>
    /// 지역 랭킹의 가중치. 관리자가 조정한다 (docs/REGIONAL_COMPETITION.md).
    /// 현금 결제와 관계된 항목은 없다.
    /// </summary>
    [Serializable]
    public sealed class RankingWeights
    {
        public int safety = 30;
        public int successRate = 20;
        public int contribution = 20;
        public int response = 20;
        public int cooperation = 10;
        /// <summary>활성 가디언 한 사람이 이만큼 기여하면 기여도 항목이 만점이다.</summary>
        public int contributionTarget = 100;
        /// <summary>활성 가디언 한 사람이 다른 도시를 이만큼 도우면 협동 항목이 만점이다.</summary>
        public int cooperationTarget = 30;
    }

    /// <summary>랭킹 계산에 쓰는 도시의 집계.</summary>
    [Serializable]
    public sealed class RegionStats
    {
        public string regionCode;
        public int residents;
        public int activePlayers;
        public int riskCenti;
        /// <summary>플레이어의 방어전·FPS 승리 수 (NPC의 방어는 세지 않는다).</summary>
        public int playerWins;
        public int playerAttempts;
        /// <summary>지금까지 끝난 침공의 규모 합.</summary>
        public int invasionSeverityTotal;
        /// <summary>그 가운데 플레이어가 막은 침공의 규모 합.</summary>
        public int repelledSeverity;
        public int npcHeldCount;
        public int failedCount;
        /// <summary>플레이어가 쌓은 도시 기여도.</summary>
        public long contribution;
        /// <summary>이 도시의 주민이 다른 도시를 도와 쌓은 기여도.</summary>
        public long supportGiven;
        public long supportReceived;
    }

    [Serializable]
    public sealed class RankingScore
    {
        public string regionCode;
        public int rank;
        /// <summary>종합 점수 (0~10000, 화면에는 100으로 나눠 보여 준다).</summary>
        public int total;
        public int safety;
        public int successRate;
        public int contribution;
        public int response;
        public int cooperation;
        /// <summary>활성 가디언이 있는 도시인가. 없으면 NPC 수비대만 지키는 도시다.</summary>
        public bool playerDefended;
    }

    public static class RankingRules
    {
        public static RankingScore Score(RegionStats stats, RankingWeights weights)
        {
            int active = Math.Max(1, stats.activePlayers);
            var score = new RankingScore
            {
                regionCode = stats.regionCode,
                playerDefended = stats.activePlayers > 0,
                safety = 100 - CityRiskRules.ToPoints(stats.riskCenti),
                // 시도가 적은 도시가 한두 번의 결과로 0점이나 100점이 되지 않게 한다.
                successRate = (Math.Max(0, stats.playerWins) + 1) * 100 / (Math.Max(0, stats.playerAttempts) + 2),
                // 도시의 총합이 아니라 활성 가디언 한 사람당 기여로 본다 (인구 보정).
                contribution = Percent(stats.contribution, (long)active * Math.Max(1, weights.contributionTarget)),
                // 규모가 큰 침공을 막을수록 많이 반영된다 (난이도 보정). 침공이 없었다면 중간값.
                response = stats.invasionSeverityTotal <= 0
                    ? 50
                    : Percent(stats.repelledSeverity, stats.invasionSeverityTotal),
                cooperation = Percent(stats.supportGiven, (long)active * Math.Max(1, weights.cooperationTarget)),
            };

            long weightSum = (long)weights.safety + weights.successRate + weights.contribution + weights.response + weights.cooperation;
            if (weightSum <= 0) return score;
            long sum = (long)score.safety * weights.safety + (long)score.successRate * weights.successRate
                       + (long)score.contribution * weights.contribution + (long)score.response * weights.response
                       + (long)score.cooperation * weights.cooperation;
            score.total = (int)(sum * 100 / weightSum);
            return score;
        }

        /// <summary>
        /// 순위를 매긴다. 활성 가디언이 있는 도시끼리 먼저 겨루고, NPC 수비대만 있는 도시는 그 뒤에 온다.
        /// NPC가 지킨 결과로 실제 플레이어의 도시를 앞지르지 않게 하기 위해서다.
        /// </summary>
        public static List<RankingScore> Rank(List<RegionStats> all, RankingWeights weights)
        {
            var scores = new List<RankingScore>(all.Count);
            for (int i = 0; i < all.Count; i++) scores.Add(Score(all[i], weights));
            scores.Sort((a, b) =>
            {
                if (a.playerDefended != b.playerDefended) return a.playerDefended ? -1 : 1;
                if (a.total != b.total) return b.total.CompareTo(a.total);
                return string.CompareOrdinal(a.regionCode, b.regionCode);
            });
            for (int i = 0; i < scores.Count; i++) scores[i].rank = i + 1;
            return scores;
        }

        private static int Percent(long value, long full)
        {
            if (full <= 0 || value <= 0) return 0;
            long percent = value * 100 / full;
            return percent > 100 ? 100 : (int)percent;
        }
    }
}
