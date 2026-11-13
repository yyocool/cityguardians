<?php
namespace CG\Api;

use CG\App;
use CG\Infra\ApiError;
use CG\Infra\Clock;
use CG\Infra\Request;
use CG\Infra\Wire;

/**
 * /api/v1 의 요청 하나를 처리한다 (docs/API_SPEC.md).
 *
 * 응답 형식: { ok, data, server_time } / { ok:false, error:{code,message}, server_time }
 * 이름은 snake_case 로 내보내고, 받은 본문의 이름은 서버 안의 이름(camelCase)으로 바꿔 쓴다.
 */
final class Kernel
{
    const VERSION = '10.0.0';

    /** @var App */
    private $app;
    /** @var array[] */
    private $routes;

    public function __construct(App $app)
    {
        $this->app = $app;
        $this->routes = Routes::all($app);
    }

    /** @return array [HTTP 상태, 본문 배열] */
    public function handle(Request $request)
    {
        try {
            $data = $this->dispatch($request);
            return [200, ['ok' => true, 'data' => Wire::snake($data), 'server_time' => Clock::iso()]];
        } catch (ApiError $error) {
            $body = ['code' => $error->errorCode, 'message' => $error->getMessage()];
            if ($error->reason !== null) {
                $body['reason'] = $error->reason;
            }
            if ($error->problems) {
                $body['problems'] = array_values($error->problems);
            }
            return [$error->httpStatus(), ['ok' => false, 'error' => $body, 'server_time' => Clock::iso()]];
        } catch (\Throwable $error) {
            // 내부 정보(SQL, 경로, 스택)는 응답에 넣지 않는다. 서버 로그에만 남긴다.
            $this->app->log('error', 'api', get_class($error) . ': ' . $error->getMessage(),
                $request->method . ' ' . $request->path . "\n" . $error->getFile() . ':' . $error->getLine());
            return [500, ['ok' => false, 'error' => ['code' => ApiError::INTERNAL, 'message' => '잠시 뒤에 다시 시도해 주세요.'],
                'server_time' => Clock::iso()]];
        }
    }

    private function dispatch(Request $request)
    {
        $env = $this->app->env;
        if ($request->badJson) {
            throw new ApiError(ApiError::INVALID_REQUEST, '요청의 형식이 올바르지 않아요.');
        }
        $route = $this->match($request);
        if ($env->bool('MAINTENANCE', false) && $route['name'] !== 'health') {
            throw new ApiError(ApiError::MAINTENANCE, '점검 중이에요. 잠시 뒤에 다시 와 주세요.');
        }
        $minVersion = $env->get('MIN_CLIENT_VERSION', '');
        $clientVersion = $request->header('x-client-version');
        if ($minVersion !== '' && $clientVersion !== '' && $route['name'] !== 'health'
            && version_compare($clientVersion, $minVersion, '<')) {
            throw new ApiError(ApiError::UPDATE_REQUIRED, '새 버전으로 업데이트해 주세요.');
        }
        if ($route['auth']) {
            $header = $request->header('authorization');
            if (stripos($header, 'Bearer ') !== 0) {
                throw new ApiError(ApiError::UNAUTHORIZED, '다시 접속해 주세요.');
            }
            $request->player = $this->app->players()->authenticate(trim(substr($header, 7)));
        }
        return call_user_func($route['handler'], $request);
    }

    private function match(Request $request)
    {
        $pathMatched = false;
        foreach ($this->routes as $route) {
            if (!preg_match($route['regex'], $request->path, $m)) {
                continue;
            }
            $pathMatched = true;
            if ($route['method'] !== $request->method) {
                continue;
            }
            foreach ($m as $key => $value) {
                if (is_string($key)) {
                    $request->params[$key] = $value;
                }
            }
            return $route;
        }
        if ($pathMatched) {
            throw new ApiError(ApiError::INVALID_REQUEST, '쓸 수 없는 요청 방식이에요.');
        }
        throw new ApiError(ApiError::NOT_FOUND, '없는 주소예요.');
    }

    /** 응답을 내보낸다. */
    public static function send($status, array $body)
    {
        http_response_code($status);
        header('Content-Type: application/json; charset=utf-8');
        header('Cache-Control: no-store');
        header('X-Content-Type-Options: nosniff');
        echo json_encode($body, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES);
    }
}
