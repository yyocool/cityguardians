<?php
namespace CG\Infra;

/**
 * 일회용 코드 (TOTP, RFC 6238: HMAC-SHA1, 30초, 6자리). 관리자 2단계 인증에 쓴다.
 * 일회용 코드 앱(Google Authenticator 등)과 맞는다.
 */
final class Totp
{
    const ALPHABET = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';

    public static function newSecret($bytes = 20)
    {
        return self::base32Encode(random_bytes($bytes));
    }

    public static function code($secret, $unix, $step = 30)
    {
        $key = self::base32Decode($secret);
        $counter = intdiv($unix, $step);
        $binary = pack('N', 0) . pack('N', $counter);
        $hash = hash_hmac('sha1', $binary, $key, true);
        $offset = ord($hash[19]) & 0x0F;
        $value = ((ord($hash[$offset]) & 0x7F) << 24)
            | (ord($hash[$offset + 1]) << 16)
            | (ord($hash[$offset + 2]) << 8)
            | ord($hash[$offset + 3]);
        return str_pad((string)($value % 1000000), 6, '0', STR_PAD_LEFT);
    }

    /** 앞뒤 한 칸(30초)의 시계 오차를 허용한다. */
    public static function verify($secret, $code, $unix)
    {
        $code = preg_replace('/\s+/', '', (string)$code);
        if (!preg_match('/^\d{6}$/', $code)) {
            return false;
        }
        for ($drift = -1; $drift <= 1; $drift++) {
            if (hash_equals(self::code($secret, $unix + $drift * 30), $code)) {
                return true;
            }
        }
        return false;
    }

    /**
     * 코드가 맞으면 그 코드의 순번(30초 단위)을, 틀리면 0을 돌려준다.
     * 순번을 기록해 두면 같은 코드를 두 번 받지 않을 수 있다.
     */
    public static function matchStep($secret, $code, $unix)
    {
        $code = preg_replace('/\s+/', '', (string)$code);
        if (!preg_match('/^\d{6}$/', $code)) {
            return 0;
        }
        $found = 0;
        for ($drift = -1; $drift <= 1; $drift++) {
            if (hash_equals(self::code($secret, $unix + $drift * 30), $code)) {
                $found = intdiv($unix + $drift * 30, 30);
            }
        }
        return $found;
    }

    public static function uri($secret, $account, $issuer = 'CITY GUARDIANS Admin')
    {
        return 'otpauth://totp/' . rawurlencode($issuer . ':' . $account) . '?secret=' . $secret
            . '&issuer=' . rawurlencode($issuer) . '&algorithm=SHA1&digits=6&period=30';
    }

    public static function base32Encode($data)
    {
        $bits = '';
        for ($i = 0, $n = strlen($data); $i < $n; $i++) {
            $bits .= str_pad(decbin(ord($data[$i])), 8, '0', STR_PAD_LEFT);
        }
        $out = '';
        foreach (str_split($bits, 5) as $chunk) {
            $out .= self::ALPHABET[bindec(str_pad($chunk, 5, '0'))];
        }
        return $out;
    }

    public static function base32Decode($text)
    {
        $text = strtoupper(preg_replace('/[^A-Za-z2-7]/', '', (string)$text));
        $bits = '';
        for ($i = 0, $n = strlen($text); $i < $n; $i++) {
            $bits .= str_pad(decbin(strpos(self::ALPHABET, $text[$i])), 5, '0', STR_PAD_LEFT);
        }
        $out = '';
        foreach (str_split($bits, 8) as $chunk) {
            if (strlen($chunk) === 8) {
                $out .= chr(bindec($chunk));
            }
        }
        return $out;
    }
}
