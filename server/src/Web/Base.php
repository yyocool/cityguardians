<?php
namespace CG\Web;

use CG\Admin\Html;
use CG\App;
use CG\Infra\ApiError;
use CG\Infra\Clock;
use CG\Infra\Request;

/**
 * 홈페이지(/city-pass, /scholarship)와 상점 확인 화면(/merchant)이 함께 쓰는 바탕.
 *
 * - 응답마다 보안 헤더를 붙인다. 스크립트는 없다 (CSP 가 막는다). 외부 글꼴·그림·스크립트를 부르지 않는다.
 * - 값을 바꾸는 요청은 POST 뿐이고, 모두 출처(Origin)와 CSRF 토큰을 확인한다.
 * - 화면에 나가는 값은 모두 Html::e() 를 거친다.
 * - 오류의 내부 정보는 화면에 내보내지 않고 서버 로그에만 남긴다.
 */
abstract class Base
{
    /** @var App */
    protected $app;
    /** 서버가 놓인 경로 (예: "" 또는 "/cg"). */
    protected $base;

    public function __construct(App $app, $basePath = '')
    {
        $this->app = $app;
        $this->base = rtrim((string)$basePath, '/');
    }

    /** @param bool $private 로그인한 사람의 화면인가. 그러면 저장하지 않고, 검색 엔진에도 맡기지 않는다. */
    protected function headers($private = true, array $extra = [])
    {
        return array_merge([
            'Content-Type: text/html; charset=utf-8',
            'Cache-Control: ' . ($private ? 'no-store' : 'no-cache'),
            "Content-Security-Policy: default-src 'none'; style-src 'self'; img-src 'self'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'",
            'X-Frame-Options: DENY',
            'X-Content-Type-Options: nosniff',
            'Referrer-Policy: no-referrer',
        ], $private ? ['X-Robots-Tag: noindex, nofollow'] : [], $extra);
    }

    protected function cookieHeader($name, $token, $maxAge, $path, $sameSite)
    {
        $secure = $this->app->env->bool('WEB_COOKIE_SECURE', true);
        return 'Set-Cookie: ' . $name . '=' . $token . '; Path=' . $this->base . $path . '; Max-Age=' . (int)$maxAge
            . '; HttpOnly; SameSite=' . $sameSite . ($secure ? '; Secure' : '');
    }

    protected function url($path)
    {
        return $this->base . $path;
    }

    protected function redirect($path, array $extraHeaders = [])
    {
        return [303, $this->headers(true, array_merge(['Location: ' . $this->base . $path], $extraHeaders)), ''];
    }

    /** 폼·쿠키·주소의 값은 글자만 받는다 (name[]=… 처럼 배열로 보낸 값은 버린다). */
    protected static function clean(Request $request)
    {
        $request->form = array_filter((array)$request->form, 'is_string');
        $request->cookies = array_filter((array)$request->cookies, 'is_string');
        $request->query = array_filter((array)$request->query, 'is_string');
    }

    /** 다른 사이트에서 보낸 폼인가. */
    protected static function crossOrigin(Request $request)
    {
        $origin = $request->header('origin');
        $host = $request->header('host');
        if ($origin === '' || $host === '') {
            return false;
        }
        $port = parse_url($origin, PHP_URL_PORT);
        return parse_url($origin, PHP_URL_HOST) . ($port ? ':' . $port : '') !== $host;
    }

    protected static function field(array $values, $key, $default = '')
    {
        return isset($values[$key]) && is_string($values[$key]) ? $values[$key] : $default;
    }

    protected static function e($value)
    {
        return Html::e($value);
    }

    /** 받지 못한 까닭을 화면 위에 보여 준다. */
    protected static function problems(array $problems, $title = '확인해 주세요.')
    {
        if (!$problems) {
            return '';
        }
        $out = '<div class="problems" role="alert"><strong>' . Html::e($title) . '</strong><ul>';
        foreach ($problems as $problem) {
            $out .= '<li>' . Html::e($problem) . '</li>';
        }
        return $out . '</ul></div>';
    }

    /** 한국 시간으로 보여 준다. ISO 시각(…Z)이나 DB 의 UTC 시각을 받는다. */
    protected static function kst($time, $withTime = true)
    {
        if ($time === null || $time === '') {
            return '';
        }
        $unix = is_int($time) ? $time : (strpos((string)$time, 'T') !== false ? strtotime((string)$time) : Clock::parse((string)$time));
        return gmdate($withTime ? 'Y-m-d H:i' : 'Y-m-d', $unix + 9 * 3600);
    }

    /** 요청 하나를 처리한다. @return array [HTTP 상태, 헤더 목록, 본문] */
    public function handle(Request $request)
    {
        try {
            return $this->dispatch($request);
        } catch (ApiError $error) {
            return [$error->httpStatus(), $this->headers(), $this->errorPage($error)];
        } catch (\Throwable $error) {
            $this->app->log('error', static::LOG_NAME, get_class($error) . ': ' . $error->getMessage(),
                $request->method . ' ' . $request->path . "\n" . $error->getFile() . ':' . $error->getLine());
            return [500, $this->headers(), $this->messagePage('오류', '처리하는 중에 문제가 생겼습니다. 잠시 뒤에 다시 시도해 주세요.')];
        }
    }

    abstract protected function dispatch(Request $request);

    abstract protected function messagePage($title, $text);

    protected function errorPage(ApiError $error)
    {
        return $this->messagePage($error->errorCode === ApiError::FEATURE_DISABLED ? '준비 중' : '처리할 수 없습니다', $error->getMessage());
    }

    public static function send($status, array $headers, $body)
    {
        http_response_code($status);
        foreach ($headers as $header) {
            header($header, false);
        }
        echo $body;
    }
}
