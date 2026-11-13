using System;
using System.Globalization;
using System.Text;

namespace CityGuardians.Domain
{
    /// <summary>쿠폰의 상태 (서버의 값과 같다).</summary>
    public static class CouponStatus
    {
        public const string Issued = "issued";
        public const string Redeemed = "redeemed";
        public const string Expired = "expired";
        public const string Cancelled = "cancelled";
    }

    /// <summary>혜택의 쿠폰을 받을 수 없는 이유.</summary>
    public static class ClaimBlocks
    {
        public const string NotMember = "not_member";
        public const string LimitReached = "limit_reached";
        public const string SoldOut = "sold_out";
    }

    /// <summary>장학 프로그램의 모집 단계.</summary>
    public static class ScholarshipPhases
    {
        public const string Upcoming = "upcoming";
        public const string Open = "open";
        public const string Closed = "closed";
        public const string Announced = "announced";
    }

    /// <summary>
    /// 현실 혜택(CITY PASS, GUARDIANS SCHOLARSHIP)을 게임 화면에 보여 줄 때 쓰는 규칙
    /// (docs/REAL_WORLD_REWARDS_POLICY.md, docs/COUPON_SYSTEM.md, docs/SCHOLARSHIP_SYSTEM.md).
    ///
    /// 여기에는 GC, 전투 성적, 랭킹, 레벨을 받는 함수가 하나도 없다. 현실 혜택은 게임 경제와 이어지지 않는다:
    /// 쿠폰은 값을 치르지 않고 받고, 장학 프로그램은 게임 안에서 안내만 한다 (신청과 심사는 홈페이지와 운영팀의 일).
    /// 발급, 사용 처리, 심사는 모두 서버가 하고, 이 규칙은 받은 값을 읽기 좋게 바꾸거나 화면에 내기 전에 한 번 더 살피는 데 쓴다.
    /// </summary>
    public static class BenefitRules
    {
        /// <summary>상점에 보여 주는 일회용 코드가 살아 있는 시간(초).</summary>
        public const int PresentSeconds = 300;
        /// <summary>홈페이지 로그인 코드가 살아 있는 시간(초).</summary>
        public const int WebCodeSeconds = 300;
        /// <summary>QR 대신 쓸 때의 머리말. 서버에 공개 주소가 정해지지 않았으면 QR 에는 이것과 코드만 들어간다.</summary>
        public const string QrPrefix = "CGPASS:";

        private static readonly TimeSpan Korea = TimeSpan.FromHours(9);

        // ── 쿠폰 ────────────────────────────────────────────────

        /// <summary>저장된 상태에 유효기간을 반영한 상태. 받은 채로 기간이 지났으면 만료다.</summary>
        public static string EffectiveStatus(string status, DateTime expiresUtc, DateTime nowUtc)
        {
            return status == CouponStatus.Issued && expiresUtc <= nowUtc ? CouponStatus.Expired : status;
        }

        public static string StatusText(string status)
        {
            switch (status)
            {
                case CouponStatus.Issued: return "사용 가능";
                case CouponStatus.Redeemed: return "사용함";
                case CouponStatus.Expired: return "기간 만료";
                case CouponStatus.Cancelled: return "취소됨";
                default: return "확인 중";
            }
        }

        /// <summary>
        /// 이 혜택의 쿠폰을 지금 받을 수 없는 이유. 받을 수 있으면 null.
        /// 따지는 것은 가입 여부, 한 사람이 받을 수 있는 수, 남은 수뿐이다.
        /// </summary>
        /// <param name="remaining">남은 수. 정해진 수가 없으면 음수.</param>
        public static string ClaimBlock(bool isMember, int myCoupons, int perMemberLimit, int remaining)
        {
            if (!isMember) return ClaimBlocks.NotMember;
            if (myCoupons >= perMemberLimit) return ClaimBlocks.LimitReached;
            if (remaining == 0) return ClaimBlocks.SoldOut;
            return null;
        }

        public static string ClaimBlockText(string block)
        {
            switch (block)
            {
                case null:
                case "": return "";
                case ClaimBlocks.NotMember: return "CITY PASS 에 가입하면 받을 수 있어요.";
                case ClaimBlocks.LimitReached: return "이 혜택의 쿠폰은 이미 받았어요.";
                case ClaimBlocks.SoldOut: return "준비한 쿠폰이 모두 나갔어요.";
                default: return "지금은 받을 수 없어요.";
            }
        }

        public static string DiscountText(string type, int value)
        {
            switch (type)
            {
                case "percent": return value + "% 할인";
                case "amount": return value.ToString("N0", CultureInfo.InvariantCulture) + "원 할인";
                case "free_trial": return "무료 체험";
                case "gift": return "사은품";
                default: return "특별 혜택";
            }
        }

        /// <summary>상점에 보여 주는 일회용 코드의 모양인가: 영문 대문자와 숫자 다섯 자, 붙임표, 다섯 자.</summary>
        public static bool LooksLikePresentCode(string code)
        {
            if (code == null || code.Length != 11 || code[5] != '-') return false;
            for (int i = 0; i < code.Length; i++)
            {
                if (i == 5) continue;
                char c = code[i];
                if (!((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))) return false;
            }
            return true;
        }

        /// <summary>홈페이지 로그인 코드의 모양인가: 네 자, 붙임표, 네 자.</summary>
        public static bool LooksLikeWebCode(string code)
        {
            if (code == null || code.Length != 9 || code[4] != '-') return false;
            for (int i = 0; i < code.Length; i++)
            {
                if (i == 4) continue;
                char c = code[i];
                if (!((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))) return false;
            }
            return true;
        }

        /// <summary>
        /// 화면에 그릴 QR 의 글을 정한다. QR 에는 일회용 코드만 들어가야 하고 개인정보가 들어가면 안 된다.
        ///
        /// 글은 서버가 정해서 보내지만, 그리기 전에 한 번 더 살핀다. 다음 가운데 하나라도 어긋나면
        /// 서버가 보낸 글을 버리고 "CGPASS:코드" 만 그린다 (상점 직원은 코드를 손으로 넣어도 된다).
        ///  - 코드가 그 안에 들어 있다
        ///  - "CGPASS:코드" 이거나, "https://…?code=코드" 로 끝나는 주소다 (다른 값이 붙어 있지 않다)
        ///  - 회원번호가 어디에도 들어 있지 않고, 닉네임이 주소의 한 마디로 들어 있지 않다
        ///  - 영문·숫자·기호만 있고 (한글 닉네임이 섞일 수 없다), QR 한 장에 들어가는 길이다
        /// 코드의 모양이 틀리면 그릴 것이 없다 (null).
        /// </summary>
        public static string SafeQrText(string serverPayload, string code, string memberNo, string nickname)
        {
            if (!LooksLikePresentCode(code)) return null;
            string fallback = QrPrefix + code;
            if (string.IsNullOrEmpty(serverPayload) || serverPayload == fallback) return fallback;

            const string marker = "?code=";
            bool address = serverPayload.StartsWith("https://", StringComparison.Ordinal)
                && serverPayload.EndsWith(marker + code, StringComparison.Ordinal)
                && serverPayload.IndexOf('?') == serverPayload.Length - code.Length - marker.Length
                && serverPayload.IndexOf('#') < 0
                && serverPayload.IndexOf('@') < 0;
            if (!address) return fallback;
            // 주소의 앞부분(코드를 뺀 곳)에 개인을 가리키는 글이 섞여 있으면 쓰지 않는다.
            string head = serverPayload.Substring(0, serverPayload.Length - code.Length);
            if (!string.IsNullOrEmpty(memberNo) && head.IndexOf(memberNo, StringComparison.OrdinalIgnoreCase) >= 0) return fallback;
            if (!string.IsNullOrEmpty(nickname))
            {
                // 닉네임은 짧은 낱말일 수 있어서 ("city") 글자가 겹치는 것만으로는 막지 않는다. 주소의 한 마디와 같을 때만 막는다.
                string[] parts = head.Split('/', '?', '=', '&');
                for (int i = 0; i < parts.Length; i++)
                    if (string.Equals(parts[i], nickname, StringComparison.OrdinalIgnoreCase)) return fallback;
            }
            if (Encoding.UTF8.GetByteCount(serverPayload) > QrCode.MaxBytes) return fallback;
            for (int i = 0; i < serverPayload.Length; i++)
                if (serverPayload[i] <= ' ' || serverPayload[i] > '~') return fallback;
            return serverPayload;
        }

        // ── 장학 ────────────────────────────────────────────────

        /// <summary>모집 단계. 따지는 것은 공고의 상태와 날짜뿐이다.</summary>
        public static string Phase(string programStatus, DateTime applyStartsUtc, DateTime applyEndsUtc, DateTime nowUtc)
        {
            if (programStatus == "announced") return ScholarshipPhases.Announced;
            if (programStatus == "closed" || applyEndsUtc <= nowUtc) return ScholarshipPhases.Closed;
            return applyStartsUtc > nowUtc ? ScholarshipPhases.Upcoming : ScholarshipPhases.Open;
        }

        public static string PhaseText(string phase)
        {
            switch (phase)
            {
                case ScholarshipPhases.Upcoming: return "모집 예정";
                case ScholarshipPhases.Open: return "모집 중";
                case ScholarshipPhases.Closed: return "모집 마감";
                case ScholarshipPhases.Announced: return "결과 발표";
                default: return "확인 중";
            }
        }

        // ── 날짜와 시간 ─────────────────────────────────────────

        public static bool TryParseUtc(string iso, out DateTime utc)
        {
            utc = default;
            if (string.IsNullOrEmpty(iso)) return false;
            if (!DateTime.TryParse(iso, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime parsed)) return false;
            utc = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
            return true;
        }

        public static string ToIso(DateTime utc) =>
            utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

        /// <summary>한국 날짜 ("2026-11-13"). 읽을 수 없으면 "-".</summary>
        public static string DateText(string iso)
        {
            return TryParseUtc(iso, out DateTime utc)
                ? (utc + Korea).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : "-";
        }

        /// <summary>한국 날짜와 시각 ("2026-11-13 14:05"). 읽을 수 없으면 "-".</summary>
        public static string DateTimeText(string iso)
        {
            return TryParseUtc(iso, out DateTime utc)
                ? (utc + Korea).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                : "-";
        }

        /// <summary>남은 시간 ("4분 59초", "12초"). 0 이하면 "0초".</summary>
        public static string RemainingText(int seconds)
        {
            if (seconds <= 0) return "0초";
            return seconds >= 60 ? $"{seconds / 60}분 {seconds % 60}초" : $"{seconds}초";
        }

        /// <summary>유효기간까지 남은 날 ("오늘까지", "3일 남음", "기간 지남").</summary>
        public static string DaysLeftText(DateTime expiresUtc, DateTime nowUtc)
        {
            if (expiresUtc <= nowUtc) return "기간 지남";
            int days = ((expiresUtc + Korea).Date - (nowUtc + Korea).Date).Days;
            return days <= 0 ? "오늘까지" : days + "일 남음";
        }
    }
}
