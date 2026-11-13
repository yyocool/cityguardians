<?php
namespace CG\Infra;

/** 들어온 요청 하나. 테스트에서는 직접 만들어 넣는다. */
final class Request
{
    public $method = 'GET';
    public $path = '/';
    /** @var array<string,string> 소문자 이름의 헤더. */
    public $headers = [];
    /** @var array<string,string> */
    public $query = [];
    /** @var array JSON 본문 (키는 camelCase 로 바꿔 둔다). */
    public $body = [];
    /** @var array<string,string> 폼 본문 (관리자 화면). */
    public $form = [];
    /** @var array<string,string> */
    public $cookies = [];
    public $ip = '';
    /** @var array<string,string> 경로에서 뽑은 값. */
    public $params = [];
    /** @var array|null 인증된 플레이어. */
    public $player = null;
    /** 본문이 JSON 으로 읽히지 않았는가. */
    public $badJson = false;
    /** 받은 그대로의 본문. 서명을 확인하는 내부 요청(/internal/...)이 쓴다. */
    public $raw = '';

    public static function make($method, $path, array $body = [], array $headers = [], array $query = [])
    {
        $request = new self();
        $request->method = strtoupper($method);
        $request->path = $path;
        $request->body = Wire::camel($body);
        $request->raw = $body ? json_encode($body, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES) : '';
        foreach ($headers as $name => $value) {
            $request->headers[strtolower($name)] = (string)$value;
        }
        $request->query = $query;
        $request->ip = '127.0.0.1';
        return $request;
    }

    public static function fromGlobals($basePath = '')
    {
        $request = new self();
        $request->method = strtoupper(isset($_SERVER['REQUEST_METHOD']) ? $_SERVER['REQUEST_METHOD'] : 'GET');
        $uri = isset($_SERVER['REQUEST_URI']) ? $_SERVER['REQUEST_URI'] : '/';
        $path = parse_url($uri, PHP_URL_PATH);
        $path = is_string($path) ? $path : '/';
        if ($basePath !== '' && strpos($path, $basePath) === 0) {
            $path = substr($path, strlen($basePath));
        }
        $request->path = '/' . ltrim($path, '/');
        foreach ($_SERVER as $name => $value) {
            if (strpos($name, 'HTTP_') === 0) {
                $request->headers[strtolower(str_replace('_', '-', substr($name, 5)))] = (string)$value;
            }
        }
        if (isset($_SERVER['CONTENT_TYPE'])) {
            $request->headers['content-type'] = (string)$_SERVER['CONTENT_TYPE'];
        }
        // 일부 서버 구성(CGI)은 Authorization 헤더를 이 이름으로 넘긴다.
        if (!isset($request->headers['authorization']) && isset($_SERVER['REDIRECT_HTTP_AUTHORIZATION'])) {
            $request->headers['authorization'] = (string)$_SERVER['REDIRECT_HTTP_AUTHORIZATION'];
        }
        $request->query = $_GET;
        $request->cookies = $_COOKIE;
        $request->form = $_POST;
        $request->ip = isset($_SERVER['REMOTE_ADDR']) ? (string)$_SERVER['REMOTE_ADDR'] : '';

        $type = isset($request->headers['content-type']) ? strtolower($request->headers['content-type']) : '';
        if (strpos($type, 'application/json') === 0) {
            $raw = file_get_contents('php://input');
            if ($raw !== false && $raw !== '') {
                $request->raw = $raw;
                // 실시간 서버가 보내는 전투 기록(공동 예산 장부, 핑)은 더 길 수 있다. 그 요청은 서명이 맞아야만 처리된다.
                if (strlen($raw) > (strpos($request->path, '/internal/') === 0 ? 2097152 : 65536)) {
                    $request->badJson = true;
                } else {
                    $decoded = json_decode($raw, true);
                    if (is_array($decoded)) {
                        $request->body = Wire::camel($decoded);
                    } else {
                        $request->badJson = true;
                    }
                }
            }
        }
        return $request;
    }

    public function header($name, $default = '')
    {
        $name = strtolower($name);
        return isset($this->headers[$name]) ? $this->headers[$name] : $default;
    }

    /** 본문의 문자열 값. 없거나 문자열이 아니면 오류. */
    public function str($key, $maxLength = 64, $required = true)
    {
        if (!isset($this->body[$key]) || $this->body[$key] === '') {
            if ($required) {
                throw new ApiError(ApiError::INVALID_REQUEST, '필요한 값이 빠졌어요.');
            }
            return null;
        }
        $value = $this->body[$key];
        if (!is_string($value) || strlen($value) > $maxLength * 4) {
            throw new ApiError(ApiError::INVALID_REQUEST, '값의 형식이 올바르지 않아요.');
        }
        return $value;
    }

    /** 본문의 정수 값. 소수나 문자열은 받지 않는다. */
    public function int($key, $required = true, $default = 0)
    {
        if (!array_key_exists($key, $this->body) || $this->body[$key] === null) {
            if ($required) {
                throw new ApiError(ApiError::INVALID_REQUEST, '필요한 값이 빠졌어요.');
            }
            return $default;
        }
        $value = $this->body[$key];
        if (!is_int($value)) {
            throw new ApiError(ApiError::INVALID_REQUEST, '값의 형식이 올바르지 않아요.');
        }
        return $value;
    }

    /**
     * 처치 보고 목록 [[enemyCode, count]]. 형식이 맞지 않으면 오류.
     * @return array[]
     */
    public function kills($key = 'kills')
    {
        $value = isset($this->body[$key]) ? $this->body[$key] : [];
        if (!is_array($value) || !Wire::isList($value) || count($value) > 32) {
            throw new ApiError(ApiError::INVALID_REQUEST, '처치 보고의 형식이 올바르지 않아요.');
        }
        $kills = [];
        foreach ($value as $row) {
            if (!is_array($row) || !isset($row['enemyCode'], $row['count']) || !is_string($row['enemyCode'])
                || strlen($row['enemyCode']) > 40 || !is_int($row['count'])) {
                throw new ApiError(ApiError::INVALID_REQUEST, '처치 보고의 형식이 올바르지 않아요.');
            }
            $kills[] = ['enemyCode' => $row['enemyCode'], 'count' => $row['count']];
        }
        return $kills;
    }
}
