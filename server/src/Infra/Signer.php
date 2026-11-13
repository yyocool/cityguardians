<?php
namespace CG\Infra;

/**
 * 게임 서버(PHP)와 실시간 서버 사이의 서명 (docs/COOP_DEFENSE_SYSTEM.md 4장).
 *
 * 두 서버는 같은 비밀값(REALTIME_SECRET)을 환경변수로만 나눠 가진다. 저장소와 문서에는 적지 않는다.
 *
 *   입장권      게임 서버가 서명해서 플레이어에게 준다. 플레이어는 그것을 실시간 서버에 낸다.
 *               payload(base64url) + "." + HMAC-SHA256(base64url). 수명이 짧다 (기본 60초).
 *   내부 요청   실시간 서버가 게임 서버의 /internal/... 을 부를 때 붙인다.
 *               X-CG-Signature: t=<유닉스 시각>,v1=<HMAC-SHA256(시각 \n 방식 \n 경로 \n 본문의 SHA-256)>
 *               시각이 60초 넘게 어긋나면 받지 않는다.
 *
 * 입장권과 내부 요청은 서로 다른 키로 서명한다 (비밀값에서 용도별로 끌어낸다). 한쪽 서명을 다른 쪽에 쓸 수 없다.
 */
final class Signer
{
    const MIN_SECRET_LENGTH = 32;
    const MAX_SKEW_SECONDS = 60;

    private $secret;

    public function __construct($secret)
    {
        $this->secret = is_string($secret) ? $secret : '';
    }

    public static function fromEnv(Env $env)
    {
        return new self($env->get('REALTIME_SECRET', ''));
    }

    /** 비밀값이 충분히 긴가. 아니면 서명도 검증도 하지 않는다. */
    public function ready()
    {
        return strlen($this->secret) >= self::MIN_SECRET_LENGTH;
    }

    private function key($purpose)
    {
        if (!$this->ready()) {
            throw new \LogicException('REALTIME_SECRET 이 설정되지 않았습니다.');
        }
        return hash_hmac('sha256', $purpose, $this->secret, true);
    }

    public static function base64url($bytes)
    {
        return rtrim(strtr(base64_encode($bytes), '+/', '-_'), '=');
    }

    private static function fromBase64url($text)
    {
        if (!is_string($text) || !preg_match('/^[A-Za-z0-9_-]+$/', $text)) {
            return null;
        }
        $bytes = base64_decode(strtr($text, '-_', '+/') . str_repeat('=', (4 - strlen($text) % 4) % 4), true);
        return $bytes === false ? null : $bytes;
    }

    // ── 입장권 ──────────────────────────────────────────────

    /** @param array $payload exp(만료 시각, 유닉스 초)가 들어 있어야 한다. */
    public function ticket(array $payload)
    {
        $body = self::base64url(json_encode($payload, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES));
        return $body . '.' . self::base64url(hash_hmac('sha256', $body, $this->key('cg-ticket-v1'), true));
    }

    /** @return array|null 서명이 맞고 만료되지 않은 입장권의 내용. */
    public function verifyTicket($ticket)
    {
        if (!$this->ready() || !is_string($ticket) || strlen($ticket) > 4096) {
            return null;
        }
        $parts = explode('.', $ticket);
        if (count($parts) !== 2) {
            return null;
        }
        $signature = self::fromBase64url($parts[1]);
        if ($signature === null || !hash_equals(hash_hmac('sha256', $parts[0], $this->key('cg-ticket-v1'), true), $signature)) {
            return null;
        }
        $json = self::fromBase64url($parts[0]);
        $payload = $json !== null ? json_decode($json, true) : null;
        if (!is_array($payload) || !isset($payload['exp']) || !is_int($payload['exp']) || Clock::now() >= $payload['exp']) {
            return null;
        }
        return $payload;
    }

    // ── 내부 요청 ───────────────────────────────────────────

    /** 내부 요청에 붙일 헤더 값. */
    public function signRequest($method, $path, $rawBody, $timestamp = null)
    {
        $timestamp = $timestamp === null ? Clock::now() : (int)$timestamp;
        return 't=' . $timestamp . ',v1=' . $this->requestMac($method, $path, $rawBody, $timestamp);
    }

    private function requestMac($method, $path, $rawBody, $timestamp)
    {
        $message = $timestamp . "\n" . strtoupper($method) . "\n" . $path . "\n" . hash('sha256', (string)$rawBody);
        return hash_hmac('sha256', $message, $this->key('cg-internal-v1'));
    }

    /** 내부 요청의 서명이 맞는가. */
    public function verifyRequest($method, $path, $rawBody, $header)
    {
        if (!$this->ready() || !is_string($header) || !preg_match('/^t=(\d{1,12}),v1=([0-9a-f]{64})$/', $header, $m)) {
            return false;
        }
        $timestamp = (int)$m[1];
        if (abs(Clock::now() - $timestamp) > self::MAX_SKEW_SECONDS) {
            return false;
        }
        return hash_equals($this->requestMac($method, $path, $rawBody, $timestamp), $m[2]);
    }
}
