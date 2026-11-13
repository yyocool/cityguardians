<?php
namespace CG\Infra;

/** 추측하기 어려운 식별자. 모두 암호학적 난수(random_int)로 만든다. */
final class Ids
{
    const ALPHABET = 'abcdefghjkmnpqrstuvwxyz23456789';   // 헷갈리는 글자(i, l, o, 0, 1)를 뺐다

    public static function token($length)
    {
        $out = '';
        $max = strlen(self::ALPHABET) - 1;
        for ($i = 0; $i < $length; $i++) {
            $out .= self::ALPHABET[random_int(0, $max)];
        }
        return $out;
    }

    /** 밖으로 내보내는 ID (16자). 예: p_8f3k2m9x7q2zab */
    public static function publicId($prefix)
    {
        return $prefix . self::token(16 - strlen($prefix));
    }

    /** 접속 토큰, 게스트 비밀키처럼 길고 비밀인 값 (16진수 64자). */
    public static function secret()
    {
        return bin2hex(random_bytes(32));
    }

    /** 한 거래에 속한 원장 행을 묶는 값 (26자). 앞부분이 시각이라 시간순으로 정렬된다. */
    public static function txn()
    {
        return str_pad(base_convert((string)(int)(microtime(true) * 1000), 10, 32), 10, '0', STR_PAD_LEFT)
            . substr(bin2hex(random_bytes(8)), 0, 16);
    }

    public static function sha256($value)
    {
        return hash('sha256', $value);
    }
}
