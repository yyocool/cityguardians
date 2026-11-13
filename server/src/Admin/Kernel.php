<?php
namespace CG\Admin;

use CG\App;
use CG\Infra\ApiError;
use CG\Infra\Request;

/**
 * /admin 의 요청 하나를 처리한다 (docs/ADMIN_SYSTEM.md).
 *
 * - 로그인 화면을 뺀 모든 화면은 접속(쿠키)과 역할을 확인한다.
 * - 값을 바꾸는 요청은 POST 뿐이고, 모두 CSRF 토큰과 출처(Origin)를 확인한다.
 * - 응답마다 보안 헤더를 붙인다: 스크립트 없음, 다른 사이트의 틀 안에 넣을 수 없음, 검색 엔진 색인 금지, 저장 금지.
 */
final class Kernel
{
    /** @var App */
    private $app;
    /** @var Auth */
    private $auth;
    /** 게임 서버가 놓인 경로 (예: "" 또는 "/cg"). 링크와 쿠키의 경로에 쓴다. */
    private $base;

    public function __construct(App $app, $basePath = '')
    {
        $this->app = $app;
        $this->auth = new Auth($app);
        $this->base = rtrim((string)$basePath, '/');
    }

    private function headers(array $extra = [])
    {
        return array_merge([
            'Content-Type: text/html; charset=utf-8',
            'Cache-Control: no-store',
            "Content-Security-Policy: default-src 'none'; style-src 'self'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'",
            'X-Frame-Options: DENY',
            'X-Content-Type-Options: nosniff',
            'X-Robots-Tag: noindex, nofollow',
            'Referrer-Policy: no-referrer',
        ], $extra);
    }

    private function cookie($token, $maxAge)
    {
        $secure = $this->app->env->bool('ADMIN_COOKIE_SECURE', true);
        return 'Set-Cookie: ' . Auth::COOKIE . '=' . $token . '; Path=' . $this->base . '/admin; Max-Age=' . (int)$maxAge
            . '; HttpOnly; SameSite=Strict' . ($secure ? '; Secure' : '');
    }

    private function redirect($path, array $extraHeaders = [])
    {
        return [303, $this->headers(array_merge(['Location: ' . $this->base . $path], $extraHeaders)), ''];
    }

    /** @return array [HTTP 상태, 헤더 목록, 본문] */
    public function handle(Request $request)
    {
        try {
            return $this->dispatch($request);
        } catch (ApiError $error) {
            $pages = new Pages($this->app, $this->auth, $this->base);
            return [$error->httpStatus(), $this->headers(), $pages->message('처리할 수 없습니다', $error->getMessage())];
        } catch (\Throwable $error) {
            // 내부 정보는 화면에 내보내지 않고 서버 로그에만 남긴다.
            $this->app->log('error', 'admin', get_class($error) . ': ' . $error->getMessage(),
                $request->method . ' ' . $request->path . "\n" . $error->getFile() . ':' . $error->getLine());
            $pages = new Pages($this->app, $this->auth, $this->base);
            return [500, $this->headers(), $pages->message('오류', '처리하는 중에 문제가 생겼습니다. 시스템 로그를 확인해 주세요.')];
        }
    }

    private function dispatch(Request $request)
    {
        $pages = new Pages($this->app, $this->auth, $this->base);
        $path = rtrim($request->path, '/');
        $method = $request->method;
        // 폼·쿠키·주소의 값은 글자만 받는다 (name[]=… 처럼 배열로 보낸 값은 버린다).
        $request->form = array_filter((array)$request->form, 'is_string');
        $request->cookies = array_filter((array)$request->cookies, 'is_string');
        $request->query = array_filter((array)$request->query, 'is_string');

        // 허용 목록이 있으면 그 주소에서만 관리자 화면이 보인다. 그 밖에서는 없는 주소처럼 답한다.
        $allow = array_filter(array_map('trim', explode(',', $this->app->env->get('ADMIN_ALLOW_IPS', ''))), 'strlen');
        if ($allow && !in_array($request->ip, $allow, true)) {
            return [404, $this->headers(), $pages->message('없는 주소', '없는 주소입니다.')];
        }
        if ($method !== 'GET' && $method !== 'POST') {
            return [405, $this->headers(['Allow: GET, POST']), $pages->message('쓸 수 없는 요청', '쓸 수 없는 요청 방식입니다.')];
        }
        // 다른 사이트에서 보낸 폼은 받지 않는다.
        if ($method === 'POST') {
            $origin = $request->header('origin');
            $host = $request->header('host');
            if ($origin !== '' && $host !== '' && parse_url($origin, PHP_URL_HOST) . (parse_url($origin, PHP_URL_PORT) ? ':' . parse_url($origin, PHP_URL_PORT) : '') !== $host) {
                return [403, $this->headers(), $pages->message('거절', '다른 곳에서 보낸 요청은 받지 않습니다.')];
            }
        }

        $token = isset($request->cookies[Auth::COOKIE]) ? $request->cookies[Auth::COOKIE] : '';

        // ── 로그인 ──
        if ($path === '/admin/login') {
            if ($method === 'GET') {
                return [200, $this->headers(), $pages->login('')];
            }
            $this->app->rateLimit()->hit('admin_login', $request->ip, $this->app->env->int('ADMIN_LOGIN_PER_10MIN', 10), 600);
            $form = $request->form;
            $result = $this->auth->login(
                isset($form['login']) ? $form['login'] : '', isset($form['password']) ? $form['password'] : '',
                isset($form['code']) ? $form['code'] : '', $request->ip
            );
            if ($result === null) {
                return [401, $this->headers(), $pages->login('아이디, 비밀번호, 일회용 코드를 다시 확인해 주세요.')];
            }
            return $this->redirect('/admin', [$this->cookie($result[0], Auth::MAX_HOURS * 3600)]);
        }

        $session = $this->auth->session($token);
        if ($session === null) {
            return $this->redirect('/admin/login', $token !== '' ? [$this->cookie('', 0)] : []);
        }
        $admin = $session['admin'];
        $pages->signedIn($admin, $session['csrf']);

        if ($method === 'POST') {
            $sent = isset($request->form['_csrf']) && is_string($request->form['_csrf']) ? $request->form['_csrf'] : '';
            if (!hash_equals($session['csrf'], $sent)) {
                $this->auth->audit($admin['id'], 'csrf.rejected', '', mb_substr($path, 0, 64), null, $request->ip);
                return [403, $this->headers(), $pages->message('거절', '화면을 새로 고친 뒤 다시 시도해 주세요.')];
            }
        }
        if ($path === '/admin/logout' && $method === 'POST') {
            $this->auth->logout($token);
            $this->auth->audit($admin['id'], 'logout', 'admin', $admin['login'], null, $request->ip);
            return $this->redirect('/admin/login', [$this->cookie('', 0)]);
        }
        // 화면 묶음마다 권한이 다르다 (docs/ADMIN_SYSTEM.md 11장).
        //   현실 혜택 화면: 화면과 서비스가 업무별 권한을 하나씩 확인한다.
        //   게임 운영 화면: VIEW 가 있어야 한다. 감사 기록만은 AUDIT 권한으로 본다 (준법 담당).
        //   도시 운영 화면 (v9.0): CITY_VIEW 가 있어야 보이고, 바꾸는 일은 일마다 권한이 따로 있다 (12장).
        if (BenefitPages::owns($path)) {
            $result = (new BenefitPages($this->app, $pages, $this->base, $admin, $session['csrf']))->route($method, $path, $request);
        } elseif (CityOpsPages::owns($path)) {
            $result = (new CityOpsPages($this->app, $pages, $this->base, $admin, $session['csrf']))->route($method, $path, $request);
        } elseif (DashboardPages::owns($path)) {
            // 실시간 대시보드 설정 (v10.0): 도시 운영 화면과 같은 권한을 쓴다 (13장).
            $result = (new DashboardPages($this->app, $pages, $this->base, $admin, $session['csrf']))->route($method, $path, $request);
        } elseif (Auth::can($admin, Auth::VIEW) || ($path === '/admin/audit' && Auth::can($admin, Auth::AUDIT))) {
            $result = $pages->route($method, $path, $request);
        } elseif (($path === '/admin' || $path === '') && $method === 'GET') {
            return [200, $this->headers(), $pages->workMenu()];
        } else {
            return [403, $this->headers(), $pages->message('권한 없음', '이 계정으로는 볼 수 없습니다.')];
        }
        if ($result === null) {
            return [404, $this->headers(), $pages->message('없는 주소', '없는 주소입니다.')];
        }
        if (isset($result['redirect'])) {
            return $this->redirect($result['redirect']);
        }
        return [isset($result['status']) ? $result['status'] : 200, $this->headers(), $result['html']];
    }

    /** 응답을 내보낸다. */
    public static function send($status, array $headers, $body)
    {
        http_response_code($status);
        foreach ($headers as $header) {
            header($header, false);
        }
        echo $body;
    }
}
