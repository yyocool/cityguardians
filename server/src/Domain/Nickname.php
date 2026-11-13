<?php
namespace CG\Domain;

/**
 * 닉네임 규칙 (docs/RESIDENCY_SYSTEM.md). 클라이언트의 NicknameRules 와 같다.
 * 중복 여부는 DB 가 따로 본다 (players.nickname 유일).
 */
final class Nickname
{
    const MIN_LENGTH = 2;
    const MAX_LENGTH = 8;
    const MAX_DIGIT_RUN = 4;

    const TOO_SHORT = 'too_short';
    const TOO_LONG = 'too_long';
    const BAD_CHARACTER = 'bad_character';
    const DIGITS_ONLY = 'digits_only';
    const LOOKS_LIKE_CONTACT = 'looks_like_contact';
    const RESERVED = 'reserved';
    const BANNED = 'banned';
    const TAKEN = 'taken';

    // 운영자·시스템 사칭, 게임 속 인물 이름. 실제 플레이어가 NPC로 보이거나 NPC가 플레이어로 보이면 안 된다.
    private static $reserved = [
        '운영자', '운영진', '관리자', '매니저', '개발자', '공식', '시스템', '고객센터', '시티가디언',
        'admin', 'gm', 'master', 'manager', 'official', 'system', 'cityguardian', 'zero',
        '이방인', '보리', '도담', '강한울', '오미소', '은다온', '정나래', '방어대장', '방어대원', '행정관',
    ];

    private static $banned = [
        '시발', '씨발', '씨바', '시바', '병신', '지랄', '좆', '존나', '개새', '새끼', '꺼져', '닥쳐',
        '미친', '죽어', '바보', '멍청', '섹스', '야동', 'fuck', 'shit', 'bitch', 'sex',
    ];

    /** 앞뒤 공백을 뗀다. null 이면 빈 글. */
    public static function normalize($name)
    {
        if ($name === null) {
            return '';
        }
        $trimmed = preg_replace('/^[\s\p{Z}]+|[\s\p{Z}]+$/u', '', (string)$name);
        return $trimmed === null ? '' : $trimmed;
    }

    /** 중복 검사에 쓰는 비교용 키. 영문 대소문자를 구별하지 않는다. */
    public static function key($name)
    {
        return mb_strtolower(self::normalize($name), 'UTF-8');
    }

    /** 규칙에 맞으면 null, 아니면 이유 코드. */
    public static function check($name)
    {
        $text = self::normalize($name);
        if (!mb_check_encoding($text, 'UTF-8')) {
            return self::BAD_CHARACTER;
        }
        // 길이는 클라이언트(C#)와 같게 UTF-16 단위로 센다.
        $length = intdiv(strlen(mb_convert_encoding($text, 'UTF-16LE', 'UTF-8')), 2);
        if ($length < self::MIN_LENGTH) {
            return self::TOO_SHORT;
        }
        if ($length > self::MAX_LENGTH) {
            return self::TOO_LONG;
        }

        $chars = preg_split('//u', $text, -1, PREG_SPLIT_NO_EMPTY);
        $digits = 0;
        $digitRun = 0;
        $longest = 0;
        foreach ($chars as $c) {
            $digit = strlen($c) === 1 && $c >= '0' && $c <= '9';
            $latin = strlen($c) === 1 && (($c >= 'a' && $c <= 'z') || ($c >= 'A' && $c <= 'Z'));
            // 완성된 한글 글자만. 낱자(ㄱ, ㅏ)는 받지 않는다.
            $hangul = strlen($c) === 3 && strcmp($c, "\u{AC00}") >= 0 && strcmp($c, "\u{D7A3}") <= 0;
            if (!$digit && !$latin && !$hangul) {
                return self::BAD_CHARACTER;
            }
            if ($digit) {
                $digits++;
                $digitRun++;
                if ($digitRun > $longest) {
                    $longest = $digitRun;
                }
            } else {
                $digitRun = 0;
            }
        }
        if ($digits === count($chars)) {
            return self::DIGITS_ONLY;
        }
        if ($longest > self::MAX_DIGIT_RUN) {
            return self::LOOKS_LIKE_CONTACT;
        }

        $key = mb_strtolower($text, 'UTF-8');
        foreach (self::$reserved as $word) {
            if (strpos($key, $word) !== false) {
                return self::RESERVED;
            }
        }
        foreach (self::$banned as $word) {
            if (strpos($key, $word) !== false) {
                return self::BANNED;
            }
        }
        return null;
    }

    public static function message($reason)
    {
        switch ($reason) {
            case self::TOO_SHORT: return '이름이 너무 짧아요. 2글자 이상으로 지어 주세요.';
            case self::TOO_LONG: return '이름이 너무 길어요. 8글자까지 쓸 수 있어요.';
            case self::BAD_CHARACTER: return '한글, 영어, 숫자만 쓸 수 있어요.';
            case self::DIGITS_ONLY: return '숫자만으로는 지을 수 없어요.';
            case self::LOOKS_LIKE_CONTACT: return '전화번호처럼 보이는 이름은 쓸 수 없어요.';
            case self::RESERVED: return '쓸 수 없는 이름이에요.';
            case self::BANNED: return '쓸 수 없는 말이 들어 있어요.';
            case self::TAKEN: return '이미 다른 가디언이 쓰는 이름이에요.';
            default: return '';
        }
    }
}
