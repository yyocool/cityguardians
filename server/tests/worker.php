<?php
// 테스트 전용: "동시에 일어나는 요청"을 만들기 위한 일꾼. CityTestCase::parallel 이 여러 개를 한꺼번에 띄운다.
// 저마다 DB 에 따로 연결해, 정해진 시각까지 기다렸다가 같은 순간에 일을 한다. 결과를 JSON 한 줄로 내보낸다.
//
//   php tests/worker.php <base64(JSON)>
//
// 테스트용 DB (이름이 _test 로 끝나는 것) 에서만 돈다.

error_reporting(E_ALL);
ini_set('display_errors', '0');
mb_internal_encoding('UTF-8');

require __DIR__ . '/../src/autoload.php';
require __DIR__ . '/lib.php';
require __DIR__ . '/DbTestCase.php';

use CG\Infra\ApiError;
use CG\Infra\Clock;
use CG\Infra\Db;

$spec = isset($argv[1]) ? json_decode((string)base64_decode($argv[1], true), true) : null;
if (!is_array($spec) || !isset($spec['job'], $spec['clock'], $spec['startAt'])) {
    fwrite(STDERR, "잘못된 작업입니다.\n");
    exit(2);
}

try {
    $env = DbTestCase::envForTests();
    if ($env === null) {
        throw new RuntimeException('CG_TEST_DB_NAME 이 없습니다.');
    }
    Clock::set((int)$spec['clock']);
    $db = Db::connect($env);
    if (!preg_match('/_test$/', (string)$db->val('SELECT DATABASE()'))) {
        throw new RuntimeException('테스트용 DB 가 아닙니다.');
    }
    $app = new \CG\App($env, $db);
    $player = isset($spec['playerId']) ? $app->players()->get((int)$spec['playerId']) : null;

    // 모두가 같은 순간에 시작하도록 기다린다.
    while (microtime(true) < (float)$spec['startAt']) {
        usleep(500);
    }

    switch ($spec['job']) {
        case 'coop.join':
            $data = $app->coop()->join($player, $spec['session'], isset($spec['role']) ? $spec['role'] : null, $spec['key']);
            break;
        case 'coop.create':
            $data = $app->coop()->create($player, $spec['zone'], null, null, $spec['key']);
            break;
        case 'coop.result':
            $data = $app->coop()->internalResult($spec['session'], $spec['body']);
            break;
        case 'budget.vote':
            $data = $app->budget()->vote($player, $spec['proposal'], $spec['choice'], $spec['key']);
            break;
        case 'budget.execute':
            $data = ['problems' => $app->budget()->execute($spec['admin'], $spec['id'], $spec['reason'])];
            break;
        case 'tax.property':
            $data = $app->taxes()->collectProperty();
            break;
        case 'treasury.spend':
            list($id, $problems) = $app->treasury()->spend($spec['admin'], $spec['region'], $spec['form']);
            $data = ['id' => $id, 'problems' => $problems];
            break;
        case 'residency.register':
            $data = $app->players()->registerResidency($player, $spec['region'], $spec['nickname']);
            $data = ['registered' => true];
            break;
        case 'residency.move':
            $data = $app->growth()->move($player, $spec['region'], $spec['key']);
            break;
        case 'city.fund':
            $data = $app->city()->fundStructure($player, $spec['structure'], (int)$spec['amount'], $spec['key']);
            $data = ['costGc' => $data['costGc'], 'duplicate' => !empty($data['duplicate'])];
            break;
        case 'defense.win':
            $session = $app->defense()->start($player, 'outskirts_1');
            Clock::advance(60);
            $data = $app->defense()->finish($player, $session['sessionId'], ['result' => 'win', 'baseHpLeft' => 20, 'wavesCleared' => 5, 'kills' => [
                ['enemyCode' => 'malfunction_robot', 'count' => 60], ['enemyCode' => 'mech_insect', 'count' => 134],
                ['enemyCode' => 'construction_mecha', 'count' => 1]]]);
            $data = ['rewardGc' => $data['rewardGc']];
            break;
        case 'gear.buy':
            $data = $app->combat()->buy($player, $spec['gear'], $spec['key']);
            break;
        default:
            throw new RuntimeException('모르는 작업: ' . $spec['job']);
    }
    echo json_encode(['ok' => true, 'data' => $data], JSON_UNESCAPED_UNICODE);
} catch (ApiError $error) {
    echo json_encode(['ok' => false, 'code' => $error->errorCode, 'reason' => $error->reason, 'message' => $error->getMessage()],
        JSON_UNESCAPED_UNICODE);
} catch (\Throwable $error) {
    echo json_encode(['ok' => false, 'code' => 'EXCEPTION', 'reason' => get_class($error), 'message' => $error->getMessage()],
        JSON_UNESCAPED_UNICODE);
}
