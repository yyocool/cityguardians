<?php
namespace CG\Infra;

/**
 * 주고받는 JSON 의 이름 규칙.
 * 서버 안에서는 클라이언트의 규칙 코드와 같은 이름(camelCase)을 쓰고, 밖으로는 API 명세의 이름(snake_case)으로 내보낸다.
 * 값은 건드리지 않는다. 이름(키)만 바꾼다.
 */
final class Wire
{
    public static function isList(array $value)
    {
        $i = 0;
        foreach ($value as $key => $unused) {
            if ($key !== $i++) {
                return false;
            }
        }
        return true;
    }

    public static function snakeKey($key)
    {
        return strtolower(preg_replace('/([a-z0-9])([A-Z])/', '$1_$2', (string)$key));
    }

    public static function camelKey($key)
    {
        return preg_replace_callback('/_([a-z0-9])/', function ($m) {
            return strtoupper($m[1]);
        }, (string)$key);
    }

    /** 내보낼 때: 키를 snake_case 로. */
    public static function snake($value)
    {
        if (!is_array($value)) {
            return $value;
        }
        if (self::isList($value)) {
            return array_map([self::class, 'snake'], $value);
        }
        $out = [];
        foreach ($value as $key => $item) {
            $out[self::snakeKey($key)] = self::snake($item);
        }
        return $out;
    }

    /** 받을 때: 키를 camelCase 로. */
    public static function camel($value)
    {
        if (!is_array($value)) {
            return $value;
        }
        if (self::isList($value)) {
            return array_map([self::class, 'camel'], $value);
        }
        $out = [];
        foreach ($value as $key => $item) {
            $out[self::camelKey($key)] = self::camel($item);
        }
        return $out;
    }
}
