<?php
namespace CG\Infra;

/**
 * 개인정보 열의 암호화 (docs/PRIVACY_DATA_FLOW.md 4장).
 *
 * AES-256-GCM. 키는 서버의 환경 설정 DATA_KEY 에만 있다 (base64 로 적은 32바이트).
 * 키가 없으면 암호화가 필요한 기능은 동작하지 않는다: 평문으로 저장하는 길은 없다.
 * 저장 형식: "v1:" + base64(IV 12바이트 + 인증 태그 16바이트 + 암호문).
 * 값마다 용도($purpose)를 함께 인증한다: 한 열의 암호문을 다른 열에 옮겨 넣으면 풀리지 않는다.
 */
final class Crypto
{
    const PREFIX = 'v1:';

    /** @var string|null */
    private $key;

    public function __construct($base64Key)
    {
        $raw = is_string($base64Key) && $base64Key !== '' ? base64_decode($base64Key, true) : false;
        $this->key = $raw !== false && strlen($raw) === 32 ? $raw : null;
    }

    public static function fromEnv(Env $env)
    {
        return new self($env->get('DATA_KEY', ''));
    }

    /** 쓸 수 있는 키가 있는가. */
    public function ready()
    {
        return $this->key !== null && function_exists('openssl_encrypt')
            && in_array('aes-256-gcm', openssl_get_cipher_methods(), true);
    }

    private function requireReady()
    {
        if (!$this->ready()) {
            throw new \RuntimeException('DATA_KEY 가 설정되지 않아 개인정보를 다룰 수 없습니다.');
        }
    }

    /** @return string|null null 과 빈 글자는 null 로 저장한다. */
    public function seal($plain, $purpose)
    {
        if ($plain === null || $plain === '') {
            return null;
        }
        $this->requireReady();
        $iv = random_bytes(12);
        $tag = '';
        $cipher = openssl_encrypt((string)$plain, 'aes-256-gcm', $this->key, OPENSSL_RAW_DATA, $iv, $tag, (string)$purpose, 16);
        if ($cipher === false) {
            throw new \RuntimeException('암호화하지 못했습니다.');
        }
        return self::PREFIX . base64_encode($iv . $tag . $cipher);
    }

    /** @return string|null 저장된 값이 없으면 null. 풀리지 않으면 예외 (다른 키, 손상, 다른 용도). */
    public function open($sealed, $purpose)
    {
        if ($sealed === null || $sealed === '') {
            return null;
        }
        $this->requireReady();
        $raw = strpos($sealed, self::PREFIX) === 0 ? base64_decode(substr($sealed, strlen(self::PREFIX)), true) : false;
        if ($raw === false || strlen($raw) < 28) {
            throw new \RuntimeException('암호화된 값의 형식이 올바르지 않습니다.');
        }
        $plain = openssl_decrypt(substr($raw, 28), 'aes-256-gcm', $this->key, OPENSSL_RAW_DATA, substr($raw, 0, 12), substr($raw, 12, 16), (string)$purpose);
        if ($plain === false) {
            throw new \RuntimeException('암호화된 값을 풀지 못했습니다.');
        }
        return $plain;
    }

    /** 새 키를 만든다 (설치할 때 한 번). */
    public static function newKey()
    {
        return base64_encode(random_bytes(32));
    }
}
