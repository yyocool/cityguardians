<?php
// CITY GUARDIANS 게임 서버의 입구.
//
//   {설치 경로}/api/v1/...   게임 API   (docs/API_SPEC.md)
//   {설치 경로}/admin/...    웹 관리자  (docs/ADMIN_SYSTEM.md)
//   {설치 경로}/city-pass/…, /scholarship/…, /login   홈페이지의 CITY PASS · SCHOLARSHIP (docs/WEBSITE_SPEC.md 11장)
//   {설치 경로}/merchant/...  제휴 상점의 쿠폰 확인 화면 (docs/COUPON_SYSTEM.md 5장)
//
// 웹 서버의 문서 루트(또는 별칭)는 이 public/ 폴더만 가리켜야 한다. src/, migrations/, .env 는 웹에서 보이면 안 된다.
// 설정과 비밀 값은 server/.env 또는 환경변수에서 읽는다 (docs/SERVER_DEPLOYMENT.md).

if (PHP_INT_SIZE < 8) {
    http_response_code(500);
    exit;
}
error_reporting(E_ALL);
ini_set('display_errors', '0');   // 오류의 내용은 화면에 내보내지 않는다
ini_set('log_errors', '1');
mb_internal_encoding('UTF-8');
header_remove('X-Powered-By');   // PHP 버전을 알리지 않는다

require __DIR__ . '/../src/autoload.php';

use CG\Infra\Db;
use CG\Infra\Env;
use CG\Infra\Request;

$base = rtrim(str_replace('\\', '/', dirname(isset($_SERVER['SCRIPT_NAME']) ? $_SERVER['SCRIPT_NAME'] : '/index.php')), '/');
$uriPath = parse_url(isset($_SERVER['REQUEST_URI']) ? $_SERVER['REQUEST_URI'] : '/', PHP_URL_PATH);
$uriPath = is_string($uriPath) ? $uriPath : '/';
$path = $base !== '' && strpos($uriPath, $base) === 0 ? substr($uriPath, strlen($base)) : $uriPath;
$isApi = $path === '/api/v1' || strpos($path, '/api/v1/') === 0;
$isAdmin = $path === '/admin' || strpos($path, '/admin/') === 0;

$isMerchant = $path === '/merchant' || strpos($path, '/merchant/') === 0;
$isSite = CG\Web\SiteKernel::owns($path);

if (!$isApi && !$isAdmin && !$isMerchant && !$isSite) {
    http_response_code(404);
    header('Content-Type: text/plain; charset=utf-8');
    header('X-Content-Type-Options: nosniff');
    echo "Not Found\n";
    exit;
}

$env = Env::load(__DIR__ . '/..');
try {
    $db = Db::connect($env);
} catch (\Throwable $error) {
    // DB 에 닿지 못했다. 접속 정보나 오류 내용은 내보내지 않는다.
    error_log('CITY GUARDIANS: DB 연결 실패 (' . get_class($error) . ')');
    http_response_code(503);
    header('Cache-Control: no-store');
    header('X-Content-Type-Options: nosniff');
    if ($isApi) {
        header('Content-Type: application/json; charset=utf-8');
        echo json_encode(['ok' => false, 'error' => ['code' => 'MAINTENANCE', 'message' => '잠시 뒤에 다시 시도해 주세요.'],
            'server_time' => gmdate('Y-m-d\TH:i:s\Z')], JSON_UNESCAPED_UNICODE);
    } else {
        header('Content-Type: text/plain; charset=utf-8');
        echo "잠시 뒤에 다시 시도해 주세요.\n";
    }
    exit;
}

$app = new CG\App($env, $db);
if ($isApi) {
    list($status, $body) = (new CG\Api\Kernel($app))->handle(Request::fromGlobals($base . '/api/v1'));
    CG\Api\Kernel::send($status, $body);
} elseif ($isAdmin) {
    list($status, $headers, $body) = (new CG\Admin\Kernel($app, $base))->handle(Request::fromGlobals($base));
    CG\Admin\Kernel::send($status, $headers, $body);
} else {
    $kernel = $isMerchant ? new CG\Web\MerchantKernel($app, $base) : new CG\Web\SiteKernel($app, $base);
    list($status, $headers, $body) = $kernel->handle(Request::fromGlobals($base));
    CG\Web\Base::send($status, $headers, $body);
}
