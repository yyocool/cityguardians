<?php
namespace CG\Infra;

/**
 * 관리자·상점·홈페이지 폼에서 받은 값의 확인.
 *
 * 값을 읽으면서 문제를 $problems 에 모은다. 문제가 있으면 저장하지 않고 폼을 다시 보여 준다.
 * 화면의 날짜와 시각은 한국 시간이고, DB 에는 UTC 로 넣는다.
 */
final class Form
{
    /** @var array<string,mixed> */
    private $input;
    /** @var string[] */
    public $problems = [];

    public function __construct(array $input)
    {
        $this->input = $input;
    }

    public function raw($key)
    {
        return isset($this->input[$key]) && is_string($this->input[$key]) ? trim($this->input[$key]) : '';
    }

    /** 글자. 길이가 맞지 않으면 문제. 제어 문자는 받지 않는다. */
    public function text($key, $label, $min, $max, $multiline = false)
    {
        $value = $this->raw($key);
        if ($multiline) {
            $value = str_replace(["\r\n", "\r"], "\n", $value);
        }
        $length = mb_strlen($value);
        if ($length < $min) {
            $this->problems[] = $min > 0 && $length === 0 ? "$label: 적어 주세요." : "$label: {$min}자 이상 적어 주세요.";
        } elseif ($length > $max) {
            $this->problems[] = "$label: {$max}자를 넘을 수 없습니다.";
        } elseif (preg_match($multiline ? '/[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]/' : '/[\x00-\x1F\x7F]/', $value)) {
            $this->problems[] = "$label: 쓸 수 없는 글자가 들어 있습니다.";
        } elseif (!mb_check_encoding($value, 'UTF-8')) {
            $this->problems[] = "$label: 글자를 읽을 수 없습니다.";
        }
        return $value;
    }

    /** 글자. 비어 있으면 null. */
    public function optional($key, $label, $max, $multiline = false)
    {
        $value = $this->text($key, $label, 0, $max, $multiline);
        return $value === '' ? null : $value;
    }

    public function choice($key, $label, array $allowed)
    {
        $value = $this->raw($key);
        if (!isset($allowed[$value])) {
            $this->problems[] = "$label: 목록에서 골라 주세요.";
        }
        return $value;
    }

    public function int($key, $label, $min, $max)
    {
        $value = $this->raw($key);
        if (!preg_match('/^-?\d{1,12}$/', $value) || (int)$value < $min || (int)$value > $max) {
            $this->problems[] = "$label: $min ~ $max 사이의 정수로 적어 주세요.";
            return $min;
        }
        return (int)$value;
    }

    /** 정수. 비어 있으면 null. */
    public function optionalInt($key, $label, $min, $max)
    {
        return $this->raw($key) === '' ? null : $this->int($key, $label, $min, $max);
    }

    public function checked($key)
    {
        return $this->raw($key) === '1';
    }

    /** 날짜 (YYYY-MM-DD). 비어 있으면 null. */
    public function date($key, $label, $required = false)
    {
        $value = $this->raw($key);
        if ($value === '') {
            if ($required) {
                $this->problems[] = "$label: 날짜를 적어 주세요.";
            }
            return null;
        }
        if (!self::isDate($value)) {
            $this->problems[] = "$label: 날짜는 2026-11-13 처럼 적어 주세요.";
            return null;
        }
        return $value;
    }

    public static function isDate($value)
    {
        $date = is_string($value) ? \DateTime::createFromFormat('!Y-m-d', $value, new \DateTimeZone('UTC')) : false;
        return $date !== false && $date->format('Y-m-d') === $value;
    }

    /** 한국 시간으로 적은 날짜와 시각 (YYYY-MM-DD HH:MM) 을 유닉스 시각으로. 틀리면 null 이고 문제를 남긴다. */
    public function koreaTime($key, $label)
    {
        $value = $this->raw($key);
        $unix = self::parseKoreaTime($value);
        if ($unix === null) {
            $this->problems[] = "$label: 날짜와 시각은 2026-11-13 09:00 처럼 적어 주세요 (한국 시간).";
        }
        return $unix;
    }

    public static function parseKoreaTime($value)
    {
        if (!is_string($value) || !preg_match('/^(\d{4}-\d{2}-\d{2})[ T](\d{2}):(\d{2})$/', $value, $m) || !self::isDate($m[1])
            || (int)$m[2] > 23 || (int)$m[3] > 59) {
            return null;
        }
        return (int)strtotime($m[1] . ' ' . $m[2] . ':' . $m[3] . ':00 UTC') - 9 * 3600;
    }

    /** 유닉스 시각 또는 DB 의 UTC 시각을 한국 시간 글자로 (폼에 다시 채울 때와 화면에 보일 때). */
    public static function koreaText($value)
    {
        if ($value === null || $value === '') {
            return '';
        }
        return gmdate('Y-m-d H:i', (is_int($value) ? $value : Clock::parse($value)) + 9 * 3600);
    }

    public function email($key, $label, $required = false)
    {
        $value = $this->raw($key);
        if ($value === '') {
            if ($required) {
                $this->problems[] = "$label: 적어 주세요.";
            }
            return null;
        }
        if (strlen($value) > 120 || filter_var($value, FILTER_VALIDATE_EMAIL) === false) {
            $this->problems[] = "$label: 이메일 주소를 다시 확인해 주세요.";
            return null;
        }
        return $value;
    }

    /** 전화번호: 숫자, 붙임표, 빈칸, +, 괄호만. */
    public function phone($key, $label, $required = false)
    {
        $value = $this->raw($key);
        if ($value === '') {
            if ($required) {
                $this->problems[] = "$label: 적어 주세요.";
            }
            return null;
        }
        if (!preg_match('/^[0-9+\-() ]{7,30}$/', $value)) {
            $this->problems[] = "$label: 전화번호를 다시 확인해 주세요.";
            return null;
        }
        return $value;
    }

    /** https 주소. 비어 있으면 null. */
    public function url($key, $label)
    {
        $value = $this->raw($key);
        if ($value === '') {
            return null;
        }
        if (strlen($value) > 300 || !preg_match('#^https://[A-Za-z0-9.\-]+(:\d+)?(/[^\s<>"\'\\\\]*)?$#', $value)) {
            $this->problems[] = "$label: https:// 로 시작하는 주소만 받을 수 있습니다.";
            return null;
        }
        return $value;
    }

    /** 변경 사유 (2~255자). */
    public function reason($key = 'reason')
    {
        $value = $this->raw($key);
        $length = mb_strlen($value);
        if ($length < 2 || $length > 255) {
            $this->problems[] = '사유를 2~255자로 적어 주세요.';
        }
        return $value;
    }

    public function fail($problem)
    {
        $this->problems[] = $problem;
    }

    public function ok()
    {
        return !$this->problems;
    }
}
