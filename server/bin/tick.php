<?php
// 예약 작업: 침공 판정과 정리 (docs/CITY_RISK_SYSTEM.md 5장), 쿠폰 만료와 장학 신청 개인정보 삭제 (docs/PRIVACY_DATA_FLOW.md 5장).
//
//   php bin/tick.php
//
// 5~10분에 한 번 돌리면 된다. 여러 번 돌아도, 겹쳐 돌아도 같은 판정이 두 번 처리되지 않는다
// (판정 순번을 DB 의 한 행에 적어 두고 그 행을 잠근 채 처리한다).
// WORLD_LAZY_TICK=1 이면 이 작업이 없어도 요청이 올 때 밀린 판정을 따라잡는다. 그래도 이 작업을 걸어 두면
// 아무도 접속하지 않는 동안에도 정리가 되고, 첫 요청이 느려지지 않는다.
// 운영 서버에 예약 작업을 등록하는 것은 사전 승인이 필요하다 (docs/SERVER_DEPLOYMENT.md 7장).

if (PHP_SAPI !== 'cli') {
    http_response_code(404);
    exit;
}
if (PHP_INT_SIZE < 8) {
    fwrite(STDERR, "64비트 PHP 가 필요합니다.\n");
    exit(2);
}
mb_internal_encoding('UTF-8');
require __DIR__ . '/../src/autoload.php';

use CG\Infra\Db;
use CG\Infra\Env;

$env = Env::load(__DIR__ . '/..');
try {
    $db = Db::connect($env);
} catch (\Throwable $error) {
    fwrite(STDERR, "DB 에 연결하지 못했습니다.\n");
    exit(1);
}
$app = new CG\App($env, $db);

try {
    $ticks = $app->city()->tick();
    $keys = $app->idempotency()->purge();
    $limits = $app->rateLimit()->purge();
    $sessions = (new CG\Admin\Auth($app))->purge();
    $problem = $app->city()->findInconsistency();
    if ($problem !== null) {
        $app->log('error', 'tick', '도시 기록 점검 실패', $problem);
        fwrite(STDERR, "도시 기록 점검 실패: $problem\n");
        exit(1);
    }
    // v7.0: 기간이 지난 쿠폰을 만료로 적고, 보관 기간이 끝난 장학 신청의 개인정보를 지우고, 끝난 웹·상점 접속을 치운다.
    // 기능이 꺼져 있어도 돈다 (꺼진 뒤에도 보관 기간은 흐른다).
    $expired = $app->cityPass()->expireDue();
    $purged = $app->scholarshipOffice()->purgeDue();
    $webSessions = $app->webAuth()->purge() + $app->merchantPortal()->purge();
    // v9.0: 전입자 현황, 재산세(정책이 켜져 있을 때만), 참여예산 마감, 협동 방어전 정리, 기록 점검.
    $v9 = CG\Service\CityOpsTick::run($app);
    if ($v9['problems']) {
        $detail = implode("\n", $v9['problems']);
        $app->log('error', 'tick', 'v9.0 기록 점검 실패', $detail);
        fwrite(STDERR, "v9.0 기록 점검 실패:\n$detail\n");
        exit(1);
    }
    $summary = "판정 $ticks, 정리: 중복 방지 키 $keys, 요청 제한 $limits, 관리자 접속 $sessions, 웹·상점 접속 $webSessions,"
        . " 만료 쿠폰 $expired, 개인정보를 지운 장학 신청 $purged,"
        . " 전입자 현황을 고친 도시 {$v9['population']}, 재산세 {$v9['propertyTaxPayers']}명 {$v9['propertyTaxGc']} GC,"
        . " 확정한 참여예산 {$v9['budgetClosed']}, 닫은 협동 방어전 {$v9['coopClosed']}";
    if ($ticks > 0 || $expired > 0 || $purged > 0 || $v9['propertyTaxPayers'] > 0 || $v9['budgetClosed'] > 0 || $v9['coopClosed'] > 0) {
        $app->log('info', 'tick', $summary);
    }
    echo $summary . "\n";
} catch (\Throwable $error) {
    $app->log('error', 'tick', get_class($error) . ': ' . $error->getMessage(), $error->getFile() . ':' . $error->getLine());
    fwrite(STDERR, "예약 작업이 실패했습니다. 시스템 로그를 확인해 주세요.\n");
    exit(1);
}
