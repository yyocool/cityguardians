<?php
namespace CG\Api;

use CG\App;
use CG\Infra\ApiError;
use CG\Infra\Request;

/**
 * v9.0 의 경로 (docs/API_SPEC.md 19~22장): 도시 성장, 이사, 지역 콘텐츠, 지방세, 도시 재정, 참여예산, 협동 방어전.
 *
 * - 세율·세액·보상 금액·전입자 수를 받는 경로는 없다. 모두 서버가 계산한다.
 * - 전투 중의 상태(적의 위치, 타워 설치, 공동 예산, 전술 핑)를 주고받는 경로도 없다. 그것은 실시간 서버(WebSocket)가 맡는다.
 *   여기에는 판에 들어가고 나가는 것, 입장권, 끝난 뒤의 결과와 기록만 있다.
 * - /internal/... 은 실시간 서버만 부른다. 접속 토큰 대신 서명(X-CG-Signature)을 확인한다. 서명이 맞지 않으면 없는 주소처럼 답한다.
 */
final class RoutesV9
{
    /** Routes::all 이 /cities/{code} 보다 먼저 부른다 (고정된 경로가 먼저 걸리도록). */
    public static function register(App $app, callable $add, callable $idem)
    {
        $query = function (Request $r, $key, $default = '') {
            return isset($r->query[$key]) && is_string($r->query[$key]) ? $r->query[$key] : $default;
        };

        // ── 도시 성장, 지역 콘텐츠 ──
        $add('GET', '/cities/growth', function () use ($app) {
            return $app->growth()->cities();
        });
        $add('GET', '/cities/content', function (Request $r) use ($app) {
            return $app->modeling()->catalog($r->player, $r->header('x-client-version'));
        });
        $add('GET', '/cities/{code}/growth', function (Request $r) use ($app) {
            return $app->growth()->city($r->params['code']);
        });

        // ── 주거 도시와 이사 ──
        $add('GET', '/residency', function (Request $r) use ($app) {
            return $app->growth()->residency($r->player);
        });
        $add('POST', '/residency/move', function (Request $r) use ($app, $idem) {
            return $app->growth()->move($r->player, $r->str('regionCode', 8), $idem($r));
        });

        // ── 가상 지방세 ──
        $add('GET', '/tax/policies', function (Request $r) use ($app) {
            return $app->taxes()->policiesFor($r->player);
        });
        $add('GET', '/tax/transactions', function (Request $r) use ($app, $query) {
            return $app->taxes()->transactionsOf($r->player, (int)$query($r, 'limit', '50'), $query($r, 'before'));
        });

        // ── 도시 재정 ──
        $add('GET', '/cities/{code}/treasury', function (Request $r) use ($app) {
            return $app->treasury()->summary($r->params['code']);
        });
        $add('GET', '/cities/{code}/treasury/expenditures', function (Request $r) use ($app, $query) {
            return $app->treasury()->expenditures($r->params['code'], (int)$query($r, 'limit', '50'), $query($r, 'before'));
        });

        // ── 참여예산 ──
        $add('GET', '/cities/budget/options', function (Request $r) use ($app) {
            return $app->budget()->options($r->player);
        });
        $add('POST', '/cities/budget/proposals', function (Request $r) use ($app, $idem) {
            return $app->budget()->propose($r->player, $r->body, $idem($r));
        });
        $add('GET', '/cities/budget/proposals/{id}', function (Request $r) use ($app) {
            return $app->budget()->detail($r->player, $r->params['id']);
        });
        $add('POST', '/cities/budget/proposals/{id}/vote', function (Request $r) use ($app, $idem) {
            return $app->budget()->vote($r->player, $r->params['id'], $r->str('choice', 8), $idem($r));
        });
        $add('POST', '/cities/budget/proposals/{id}/withdraw', function (Request $r) use ($app) {
            return $app->budget()->withdraw($r->player, $r->params['id']);
        });
        $add('GET', '/cities/{code}/budget/proposals', function (Request $r) use ($app, $query) {
            return $app->budget()->listFor($r->player, $r->params['code'], $query($r, 'status'));
        });

        // ── 협동 타워디펜스: 판에 들어가고 나가기, 입장권, 결과 ──
        $add('GET', '/coop/config', function (Request $r) use ($app) {
            return $app->coop()->config($r->player);
        });
        $add('GET', '/coop/history', function (Request $r) use ($app) {
            return $app->coop()->history($r->player);
        });
        $add('GET', '/coop/sessions', function (Request $r) use ($app) {
            return $app->coop()->sessions($r->player);
        });
        $add('POST', '/coop/sessions', function (Request $r) use ($app, $idem) {
            return $app->coop()->create($r->player, $r->str('zoneCode', 40), $r->int('maxPlayers', false, null),
                $r->str('roleCode', 24, false), $idem($r));
        });
        $add('GET', '/coop/sessions/{id}', function (Request $r) use ($app) {
            return $app->coop()->session($r->player, $r->params['id']);
        });
        $add('POST', '/coop/sessions/{id}/join', function (Request $r) use ($app, $idem) {
            return $app->coop()->join($r->player, $r->params['id'], $r->str('roleCode', 24, false), $idem($r));
        });
        $add('POST', '/coop/sessions/{id}/leave', function (Request $r) use ($app) {
            return $app->coop()->leave($r->player, $r->params['id']);
        });
        $add('PUT', '/coop/sessions/{id}/role', function (Request $r) use ($app) {
            return $app->coop()->setRole($r->player, $r->params['id'], $r->str('roleCode', 24, false));
        });
        $add('POST', '/coop/sessions/{id}/ticket', function (Request $r) use ($app) {
            return $app->coop()->ticket($r->player, $r->params['id']);
        });
        $add('GET', '/coop/sessions/{id}/budget', function (Request $r) use ($app) {
            return $app->coop()->budget($r->player, $r->params['id']);
        });
        $add('GET', '/coop/sessions/{id}/result', function (Request $r) use ($app) {
            return $app->coop()->result($r->player, $r->params['id']);
        });

        // ── 실시간 서버가 부르는 내부 경로 ── (접속 토큰이 아니라 서명으로 확인한다)
        $signed = function (Request $r) use ($app) {
            $signer = $app->coop()->signer();
            if (!$signer->ready() || !$signer->verifyRequest($r->method, $r->path, $r->raw, $r->header('x-cg-signature'))) {
                throw new ApiError(ApiError::NOT_FOUND, '없는 주소예요.');
            }
        };
        $add('GET', '/internal/coop/sessions/{id}/config', function (Request $r) use ($app, $signed) {
            $signed($r);
            return $app->coop()->internalConfig($r->params['id']);
        }, false, 'internal');
        $add('POST', '/internal/coop/sessions/{id}/start', function (Request $r) use ($app, $signed) {
            $signed($r);
            return $app->coop()->internalStart($r->params['id'], $r->body);
        }, false, 'internal');
        $add('POST', '/internal/coop/sessions/{id}/result', function (Request $r) use ($app, $signed) {
            $signed($r);
            return $app->coop()->internalResult($r->params['id'], $r->body);
        }, false, 'internal');
    }
}
