<?php
namespace CG\Infra;

/**
 * 환경 설정. 비밀 값은 서버의 .env 파일이나 환경변수에만 둔다 (저장소에 넣지 않는다).
 * 환경변수가 .env 보다 우선한다.
 */
final class Env
{
    /** @var array<string,string> */
    private $values = [];

    public static function load($rootDir)
    {
        $env = new self();
        $file = rtrim($rootDir, '/\\') . '/.env';
        if (is_file($file)) {
            foreach (file($file, FILE_IGNORE_NEW_LINES | FILE_SKIP_EMPTY_LINES) as $line) {
                $line = trim($line);
                if ($line === '' || $line[0] === '#') {
                    continue;
                }
                $at = strpos($line, '=');
                if ($at === false) {
                    continue;
                }
                $key = trim(substr($line, 0, $at));
                $value = trim(substr($line, $at + 1));
                if (strlen($value) >= 2 && ($value[0] === '"' || $value[0] === "'") && substr($value, -1) === $value[0]) {
                    $value = substr($value, 1, -1);
                }
                $env->values[$key] = $value;
            }
        }
        return $env;
    }

    public static function fromArray(array $values)
    {
        $env = new self();
        $env->values = $values;
        return $env;
    }

    public function get($key, $default = '')
    {
        $fromProcess = getenv($key);
        if ($fromProcess !== false && $fromProcess !== '') {
            return $fromProcess;
        }
        return isset($this->values[$key]) && $this->values[$key] !== '' ? $this->values[$key] : $default;
    }

    public function int($key, $default = 0)
    {
        $value = $this->get($key, '');
        return $value === '' ? $default : (int)$value;
    }

    public function bool($key, $default = false)
    {
        $value = strtolower($this->get($key, ''));
        if ($value === '') {
            return $default;
        }
        return in_array($value, ['1', 'true', 'yes', 'on'], true);
    }
}
