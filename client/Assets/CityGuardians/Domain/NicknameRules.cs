using System;
using System.Collections.Generic;

namespace CityGuardians.Domain
{
    /// <summary>
    /// 닉네임 규칙 (docs/RESIDENCY_SYSTEM.md 5장). 서버가 최종 판단하고, 화면은 같은 규칙으로 미리 알려 준다.
    /// 실명인지는 기계가 알 수 없으므로 화면 안내("진짜 이름이나 전화번호는 쓰지 마세요")로 막는다.
    /// </summary>
    public static class NicknameRules
    {
        public const int MinLength = 2;
        public const int MaxLength = 8;
        /// <summary>이어진 숫자가 이보다 길면 전화번호 같은 연락처로 본다.</summary>
        public const int MaxDigitRun = 4;

        public const string TooShort = "too_short";
        public const string TooLong = "too_long";
        public const string BadCharacter = "bad_character";
        public const string DigitsOnly = "digits_only";
        public const string LooksLikeContact = "looks_like_contact";
        public const string Reserved = "reserved";
        public const string Banned = "banned";
        public const string Taken = "taken";

        // 운영자·시스템 사칭, 게임 속 인물 이름. 실제 플레이어가 NPC로 보이거나 NPC가 플레이어로 보이면 안 된다.
        private static readonly string[] ReservedWords =
        {
            "운영자", "운영진", "관리자", "매니저", "개발자", "공식", "시스템", "고객센터", "시티가디언",
            "admin", "gm", "master", "manager", "official", "system", "cityguardian", "zero",
            "이방인", "보리", "도담", "강한울", "오미소", "은다온", "정나래", "방어대장", "방어대원", "행정관",
        };

        // 최소한의 금칙어. 실제 서비스에서는 서버가 관리하는 목록으로 바꾼다 (docs/SECURITY_POLICY.md).
        private static readonly string[] BannedWords =
        {
            "시발", "씨발", "씨바", "시바", "병신", "지랄", "좆", "존나", "개새", "새끼", "꺼져", "닥쳐",
            "미친", "죽어", "바보", "멍청", "섹스", "야동", "fuck", "shit", "bitch", "sex",
        };

        /// <summary>앞뒤 공백을 뗀다. null이면 빈 글.</summary>
        public static string Normalize(string name) => name == null ? string.Empty : name.Trim();

        /// <summary>중복 검사에 쓰는 비교용 키. 영문 대소문자를 구별하지 않는다.</summary>
        public static string Key(string name) => Normalize(name).ToLowerInvariant();

        /// <summary>규칙에 맞으면 null, 아니면 위의 이유 코드. 중복 여부는 서버가 따로 본다.</summary>
        public static string Check(string name)
        {
            string text = Normalize(name);
            if (text.Length < MinLength) return TooShort;
            if (text.Length > MaxLength) return TooLong;

            int digits = 0, digitRun = 0, longestDigitRun = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                bool digit = c >= '0' && c <= '9';
                bool latin = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
                bool hangul = c >= '\uAC00' && c <= '\uD7A3';   // 완성된 한글 글자만. 낱자(ㄱ, ㅏ)는 받지 않는다.
                if (!digit && !latin && !hangul) return BadCharacter;

                if (digit)
                {
                    digits++;
                    digitRun++;
                    if (digitRun > longestDigitRun) longestDigitRun = digitRun;
                }
                else
                {
                    digitRun = 0;
                }
            }
            if (digits == text.Length) return DigitsOnly;
            if (longestDigitRun > MaxDigitRun) return LooksLikeContact;

            string key = text.ToLowerInvariant();
            if (ContainsAny(key, ReservedWords)) return Reserved;
            if (ContainsAny(key, BannedWords)) return Banned;
            return null;
        }

        /// <summary>이유 코드를 화면에 보일 말로 바꾼다.</summary>
        public static string Message(string reason)
        {
            switch (reason)
            {
                case null: return "쓸 수 있는 이름이에요.";
                case TooShort: return $"이름은 {MinLength}글자보다 짧을 수 없어요.";
                case TooLong: return $"이름은 {MaxLength}글자보다 길 수 없어요.";
                case BadCharacter: return "한글, 영어, 숫자만 쓸 수 있어요.";
                case DigitsOnly: return "숫자만으로는 이름을 지을 수 없어요.";
                case LooksLikeContact: return "전화번호처럼 보이는 이름은 쓸 수 없어요.";
                case Reserved: return "쓸 수 없는 낱말이 들어 있어요.";
                case Banned: return "쓸 수 없는 낱말이 들어 있어요.";
                case Taken: return "다른 가디언이 이미 쓰고 있는 이름이에요.";
                default: return "쓸 수 없는 이름이에요.";
            }
        }

        private static bool ContainsAny(string key, string[] words)
        {
            for (int i = 0; i < words.Length; i++)
                if (key.IndexOf(words[i], StringComparison.Ordinal) >= 0) return true;
            return false;
        }
    }

    /// <summary>
    /// 추천 이름을 만든다. 꾸미는 말과 이름 낱말을 붙인 것이라 개인정보가 들어갈 수 없다.
    /// 만든 이름은 모두 NicknameRules를 통과한다 (단위 테스트가 모든 조합을 확인한다).
    /// </summary>
    public static class NicknameSuggester
    {
        public static readonly string[] Firsts =
        {
            "용감한", "씩씩한", "푸른", "빛나는", "날쌘", "든든한", "반짝", "새벽", "별빛", "무지개",
            "튼튼한", "웃는", "재빠른", "따뜻한", "힘찬", "달빛", "하늘", "초록", "노을", "번개",
        };

        public static readonly string[] Seconds =
        {
            "고래", "파도", "갈매기", "수달", "돌고래", "호랑이", "여우", "다람쥐", "참새", "거북",
            "등대", "구름", "바람", "햇살", "소나무", "방패", "가디언", "지킴이", "별", "나침반",
        };

        /// <summary>서로 다른 추천 이름 count개. 조합의 수보다 많이 달라고 하면 있는 만큼만 준다.</summary>
        public static List<string> Suggest(Random random, int count, ICollection<string> exclude = null)
        {
            if (random == null) throw new ArgumentNullException(nameof(random));
            var result = new List<string>();
            int combinations = Firsts.Length * Seconds.Length;
            for (int attempt = 0; attempt < combinations * 4 && result.Count < count; attempt++)
            {
                string name = Firsts[random.Next(Firsts.Length)] + Seconds[random.Next(Seconds.Length)];
                if (name.Length > NicknameRules.MaxLength) continue;
                if (result.Contains(name) || (exclude != null && exclude.Contains(name))) continue;
                result.Add(name);
            }
            return result;
        }
    }
}
