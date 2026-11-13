using System;
using System.Collections.Generic;

namespace CityGuardians.Domain
{
    // 협동 타워디펜스의 규칙에 쓰는 값들 (docs/COOP_DEFENSE_SYSTEM.md).
    //
    // 이 폴더(Domain/Coop)의 코드는 Unity 를 쓰지 않는다. 같은 파일을 실시간 서버(realtime/)가 그대로 컴파일해서
    // 전투의 권한을 갖고 돌린다. 클라이언트는 같은 규칙으로 화면을 그리고, 서버 없이 해 보는 연습전을 돌린다.

    /// <summary>팀 역할. 고르지 않아도 된다. 고르면 작은 보정이 붙는다.</summary>
    public static class CoopRoleCodes
    {
        public const string Commander = "commander";
        public const string Builder = "builder";
        public const string Support = "support";
        public const string Fighter = "fighter";
    }

    [Serializable]
    public sealed class CoopRoleDef
    {
        public string code;
        public string name;
        public string description;
        /// <summary>타워 설치·강화 비용 할인 (%).</summary>
        public int buildDiscountPercent;
        /// <summary>수리량 증가 (%).</summary>
        public int repairBonusPercent;
        /// <summary>직접 공격 피해 증가 (%).</summary>
        public int attackBonusPercent;
        /// <summary>전술 핑이 다시 차는 시간 단축 (%).</summary>
        public int pingCooldownPercent;
        /// <summary>한 판에 이 역할을 고를 수 있는 사람 수. 0이면 제한이 없다.</summary>
        public int maxPerSession;
    }

    /// <summary>협동 방어전의 운영 설정. 서버의 설정(coop)이 원본이다.</summary>
    [Serializable]
    public sealed class CoopRules
    {
        public int defaultPlayers = 4;
        public int maxPlayers = 8;
        /// <summary>사람이 한 명 늘 때마다 적 체력이 느는 비율 (%).</summary>
        public int hpScalePercentPerPlayer = 60;
        /// <summary>사람이 한 명 늘 때마다 공동 예산이 느는 비율 (%).</summary>
        public int energyScalePercentPerPlayer = 50;
        public int minParticipationPercent = 30;
        public int allowLateJoin = 1;
        public int minPlayersToStart = 2;
        public int playerDamage = 12;
        public int playerDamagePerLevelPercent = 5;
        public int playerRangeM = 14;
        public int playerShotsPer10s = 30;
        public int repairHp = 30;
        public int repairEnergy = 10;
    }

    /// <summary>전술 핑의 운영 설정. 서버의 설정(ping)이 원본이다.</summary>
    [Serializable]
    public sealed class PingRules
    {
        public int enabled = 1;
        /// <summary>연달아 보낼 수 있는 핑의 수.</summary>
        public int burst = 3;
        /// <summary>핑 하나가 다시 차는 시간 (초).</summary>
        public int refillSeconds = 4;
        /// <summary>핑이 지도에 남는 시간 (초).</summary>
        public int lifetimeSeconds = 8;
        public int maxActivePerPlayer = 3;
        /// <summary>제한을 이만큼 어기면 잠시 핑을 막는다.</summary>
        public int autoMuteStrikes = 5;
        public int autoMuteSeconds = 30;
        public int routeOverlay = 1;
        public int rangeOverlay = 1;
    }

    public static class PingKinds
    {
        public const string FocusFire = "focus_fire";
        public const string DefendHere = "defend_here";
        public const string BuildHere = "build_here";
        public const string RepairHere = "repair_here";
        public const string Danger = "danger";
        public const string Rally = "rally";

        public static readonly string[] All = { FocusFire, DefendHere, BuildHere, RepairHere, Danger, Rally };

        public static bool IsKnown(string kind) => Array.IndexOf(All, kind) >= 0;

        public static string DisplayName(string kind)
        {
            switch (kind)
            {
                case FocusFire: return "공격 집중";
                case DefendHere: return "방어 요청";
                case BuildHere: return "타워 설치 제안";
                case RepairHere: return "수리 요청";
                case Danger: return "위험 경고";
                case Rally: return "집결 위치";
                default: return kind;
            }
        }

        /// <summary>미니맵에서 "긴급 지원 요청"으로 눈에 띄게 보여 줄 핑인가.</summary>
        public static bool IsSupportRequest(string kind) => kind == DefendHere || kind == RepairHere;
    }

    /// <summary>공동 예산 장부의 줄 종류. 서버(Coop::budgetKinds)와 같다.</summary>
    public static class CoopBudgetKinds
    {
        public const string GrantStart = "grant_start";
        public const string GrantWave = "grant_wave";
        public const string GrantKill = "grant_kill";
        public const string Build = "build";
        public const string Upgrade = "upgrade";
        public const string Repair = "repair";
        /// <summary>타워를 잃었다. 금액은 변하지 않고 자리만 빈다.</summary>
        public const string Lost = "lost";

        /// <summary>부호: +1 들어옴, -1 나감, 0 변화 없음. 모르는 종류면 2.</summary>
        public static int Sign(string kind)
        {
            switch (kind)
            {
                case GrantStart:
                case GrantWave:
                case GrantKill: return 1;
                case Build:
                case Upgrade:
                case Repair: return -1;
                case Lost: return 0;
                default: return 2;
            }
        }
    }

    /// <summary>요청을 받아들이지 않은 이유. 클라이언트에 그대로 간다.</summary>
    public static class CoopRejects
    {
        public const string NotMember = "not_member";
        public const string NotRunning = "not_running";
        public const string NotLobby = "not_lobby";
        public const string NotHost = "not_host";
        public const string NotEnoughPlayers = "not_enough_players";
        public const string SessionFull = "session_full";
        public const string LateJoinClosed = "late_join_closed";
        public const string BadSlot = "bad_slot";
        public const string SlotTaken = "slot_taken";
        public const string SlotEmpty = "slot_empty";
        public const string BadStructure = "bad_structure";
        public const string NotUnlocked = "not_unlocked";
        public const string TooMany = "too_many";
        public const string MaxLevel = "max_level";
        public const string NotDamaged = "not_damaged";
        public const string TooFar = "too_far";
        public const string NoBudget = "insufficient_budget";
        public const string BadRole = "bad_role";
        public const string RoleTaken = "role_taken";
        public const string BadRequest = "bad_request";
        public const string PingDisabled = "ping_disabled";
        public const string PingBlocked = "ping_blocked";
        public const string PingMuted = "ping_muted";
        public const string PingRate = "ping_rate";
        public const string PingKind = "ping_kind";
    }

    public static class CoopCloseReasons
    {
        public const string Completed = "completed";
        public const string GateDestroyed = "gate_destroyed";
        public const string AllLeft = "all_left";
        public const string Timeout = "timeout";
        public const string Shutdown = "shutdown";
    }

    /// <summary>지도 위의 한 점 (미터). x 는 동서, z 는 남북.</summary>
    public struct CoopVec
    {
        public float x;
        public float z;

        public CoopVec(float x, float z)
        {
            this.x = x;
            this.z = z;
        }

        public static float Distance(CoopVec a, CoopVec b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return (float)Math.Sqrt(dx * dx + dz * dz);
        }

        public static float DistanceSquared(CoopVec a, CoopVec b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return dx * dx + dz * dz;
        }

        /// <summary>a 에서 b 쪽으로 maxStep 만큼 (넘지 않게) 옮긴 점.</summary>
        public static CoopVec MoveTowards(CoopVec a, CoopVec b, float maxStep)
        {
            float distance = Distance(a, b);
            if (distance <= maxStep || distance <= 0.0001f) return b;
            float t = maxStep / distance;
            return new CoopVec(a.x + (b.x - a.x) * t, a.z + (b.z - a.z) * t);
        }

        public override string ToString() => "(" + x.ToString("0.##") + ", " + z.ToString("0.##") + ")";
    }

    /// <summary>협동 방어전의 기본 역할. 서버의 defense_team_roles 가 원본이다.</summary>
    public static class CoopDefaults
    {
        public static List<CoopRoleDef> Roles()
        {
            return new List<CoopRoleDef>
            {
                new CoopRoleDef { code = CoopRoleCodes.Commander, name = "지휘관", description = "전술 핑을 더 자주 보낼 수 있어요. 한 판에 한 명만 맡아요.",
                    pingCooldownPercent = 50, maxPerSession = 1 },
                new CoopRoleDef { code = CoopRoleCodes.Builder, name = "타워 설치 담당", description = "타워를 조금 싸게 세우고 강화해요.",
                    buildDiscountPercent = 10 },
                new CoopRoleDef { code = CoopRoleCodes.Support, name = "수리·지원 담당", description = "타워를 더 많이 고쳐요.",
                    repairBonusPercent = 50 },
                new CoopRoleDef { code = CoopRoleCodes.Fighter, name = "직접 전투 담당", description = "직접 쏘는 공격이 더 세요.",
                    attackBonusPercent = 20 },
            };
        }

        public static CoopRoleDef FindRole(List<CoopRoleDef> roles, string code)
        {
            if (roles == null || string.IsNullOrEmpty(code)) return null;
            for (int i = 0; i < roles.Count; i++)
                if (roles[i].code == code) return roles[i];
            return null;
        }
    }
}
