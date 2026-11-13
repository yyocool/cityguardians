<?php
// 외부 라이브러리 없이 쓰는 작은 테스트 도구.

final class TestFailure extends Exception
{
}

final class TestSkipped extends Exception
{
}

final class T
{
    public static $checks = 0;

    public static function ok($condition, $message = '')
    {
        self::$checks++;
        if (!$condition) {
            throw new TestFailure($message !== '' ? $message : '조건이 거짓입니다.');
        }
    }

    public static function eq($expected, $actual, $message = '')
    {
        self::$checks++;
        if ($expected !== $actual) {
            throw new TestFailure(($message !== '' ? $message . ': ' : '')
                . '기대 ' . self::show($expected) . ' / 실제 ' . self::show($actual));
        }
    }

    /** 숫자의 종류(정수·실수)는 가리지 않고 값만 비교한다. 배열은 키 순서도 가리지 않는다. */
    public static function same($expected, $actual, $message = '')
    {
        self::$checks++;
        if (!self::loose($expected, $actual)) {
            throw new TestFailure(($message !== '' ? $message . ': ' : '')
                . '기대 ' . self::show($expected) . ' / 실제 ' . self::show($actual));
        }
    }

    public static function near($expected, $actual, $tolerance, $message = '')
    {
        self::$checks++;
        if (abs($expected - $actual) > $tolerance) {
            throw new TestFailure(($message !== '' ? $message . ': ' : '') . "기대 $expected / 실제 $actual");
        }
    }

    /** 그 오류 코드의 ApiError 가 나는지 확인한다. */
    public static function throwsApi($code, callable $work, $message = '')
    {
        self::$checks++;
        try {
            $work();
        } catch (\CG\Infra\ApiError $error) {
            if ($error->errorCode !== $code) {
                throw new TestFailure(($message !== '' ? $message . ': ' : '')
                    . "기대한 오류 $code / 실제 {$error->errorCode} ({$error->getMessage()})");
            }
            return $error;
        }
        throw new TestFailure(($message !== '' ? $message . ': ' : '') . "오류 $code 가 나야 하는데 성공했습니다.");
    }

    public static function skip($why)
    {
        throw new TestSkipped($why);
    }

    private static function loose($a, $b)
    {
        if (is_array($a) && is_array($b)) {
            if (count($a) !== count($b)) {
                return false;
            }
            foreach ($a as $key => $value) {
                if (!array_key_exists($key, $b) || !self::loose($value, $b[$key])) {
                    return false;
                }
            }
            return true;
        }
        if ((is_int($a) || is_float($a)) && (is_int($b) || is_float($b))) {
            return abs($a - $b) < 1e-6;
        }
        return $a === $b;
    }

    private static function show($value)
    {
        $text = json_encode($value, JSON_UNESCAPED_UNICODE);
        return strlen($text) > 600 ? substr($text, 0, 600) . '…' : $text;
    }
}
