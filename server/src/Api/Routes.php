<?php
namespace CG\Api;

use CG\App;
use CG\Domain\Nickname;
use CG\Infra\ApiError;
use CG\Infra\Request;

/**
 * /api/v1 의 경로 목록 (docs/API_SPEC.md 2~3장, 14~17장).
 *
 * 여기에는 "위험도를 얼마로 바꿔라"를 받는 경로가 없다. 위험도는 서버가 검증한 전투 결과와 시간의 흐름으로만 바뀐다.
 * 가격·보상 금액을 받는 경로도 없다. 금액은 서버의 정의 데이터에서 읽는다.
 */
final class Routes
{
    /** @return array[] */
    public static function all(App $app)
    {
        $routes = [];
        $add = function ($method, $path, $handler, $auth = true, $name = '') use (&$routes) {
            // {code} 같은 자리는 영문·숫자·밑줄·붙임표만 받는다.
            $regex = preg_replace('/\{(\w+)\}/', '(?P<$1>[A-Za-z0-9_\-]{1,64})', $path);
            $routes[] = ['method' => $method, 'regex' => '#^' . $regex . '$#', 'handler' => $handler, 'auth' => $auth, 'name' => $name];
        };
        $idem = function (Request $r) {
            return $r->header('idempotency-key');
        };

        // ── 상태, 인증 ──
        $add('GET', '/health', function () use ($app) {
            $app->db->val('SELECT 1');
            return ['status' => 'ok', 'version' => Kernel::VERSION, 'environment' => $app->env->get('APP_ENV', 'production'),
                'maintenance' => $app->env->bool('MAINTENANCE', false), 'minClientVersion' => $app->env->get('MIN_CLIENT_VERSION', '')];
        }, false, 'health');

        $add('POST', '/auth/guest', function (Request $r) use ($app) {
            $app->rateLimit()->hit('guest', $r->ip, $app->env->int('GUEST_PER_HOUR', 20), 3600);
            return $app->players()->createGuest($r->header('x-client-platform'), $r->header('x-client-version'));
        }, false);

        $add('POST', '/auth/token', function (Request $r) use ($app) {
            $app->rateLimit()->hit('token', $r->ip, $app->env->int('TOKEN_PER_10MIN', 30), 600);
            return $app->players()->login($r->str('playerId', 16), $r->str('guestSecret', 64),
                $r->header('x-client-platform'), $r->header('x-client-version'));
        }, false);

        $add('POST', '/auth/logout', function (Request $r) use ($app) {
            $app->players()->logout(trim(substr($r->header('authorization'), 7)));
            return ['loggedOut' => true];
        });

        // ── 시작 데이터 ──
        $add('GET', '/me', function (Request $r) use ($app) {
            return $app->players()->me($r->player);
        });

        $add('GET', '/defs', function (Request $r) use ($app) {
            $defs = $app->defs();
            $version = $defs->version();
            if (isset($r->query['version']) && $r->query['version'] === $version) {
                return ['version' => $version, 'unchanged' => true];
            }
            $strip = function (array $list) {
                return array_map(function ($def) {
                    unset($def['id']);
                    return $def;
                }, array_values($list));
            };
            $policy = $defs->policy();
            $weights = [];
            foreach ($policy['weights'] as $reason => $delta) {
                $weights[] = ['reason' => $reason, 'deltaCenti' => $delta];
            }
            $policy['weights'] = $weights;
            return [
                'version' => $version,
                'unchanged' => false,
                'economy' => $defs->economy(),
                'progression' => $defs->progression(),
                'enemies' => $strip($defs->enemies()),
                'zones' => $strip($defs->huntZones()),
                'towers' => $strip($defs->structures()),
                'stages' => $strip($defs->zones()),
                'combat' => [
                    'modes' => $defs->modes(),
                    'gear' => $strip($defs->gear()),
                    'fpsMissions' => $strip($defs->fpsMissions()),
                    'facilities' => $strip($defs->facilities()),
                    'risk' => $policy,
                    'ranking' => $defs->rankingWeights(),
                    'dailyFpsRewardLimit' => $defs->combatConfig()['dailyFpsRewardLimit'],
                ],
            ];
        });

        $add('GET', '/regions', function () use ($app) {
            $sidos = $app->db->all('SELECT code, name, short_name FROM sidos ORDER BY id');
            $regions = [];
            foreach ($app->db->all(
                'SELECT r.code, r.name, s.code AS sido_code, r.resident_count, r.defense_points'
                . ' FROM regions r JOIN sidos s ON s.id = r.sido_id ORDER BY r.id'
            ) as $row) {
                $regions[] = ['regionCode' => $row['code'], 'name' => $row['name'], 'sidoCode' => $row['sido_code'],
                    'residents' => (int)$row['resident_count'], 'defensePoints' => (int)$row['defense_points']];
            }
            return ['sidos' => array_map(function ($s) {
                return ['code' => $s['code'], 'name' => $s['name'], 'shortName' => $s['short_name']];
            }, $sidos), 'regions' => $regions];
        });

        $add('POST', '/residency/check-nickname', function (Request $r) use ($app) {
            $nickname = $r->str('nickname', 32);
            $reason = $app->players()->nicknameProblem($r->player['id'], $nickname);
            return ['nickname' => Nickname::normalize($nickname), 'available' => $reason === null, 'reason' => $reason,
                'message' => Nickname::message($reason)];
        });

        $add('POST', '/residency', function (Request $r) use ($app) {
            return $app->players()->registerResidency($r->player, $r->str('regionCode', 8), $r->str('nickname', 32));
        });

        // ── 공통 전투 ──
        $add('GET', '/combat/modes', function (Request $r) use ($app) {
            return $app->combat()->modes($r->player);
        });
        $add('GET', '/combat/equipment', function (Request $r) use ($app) {
            return $app->combat()->equipment($r->player);
        });
        $add('GET', '/combat/overview', function (Request $r) use ($app) {
            return $app->combat()->overview($r->player);
        });
        $add('GET', '/combat/rewards', function (Request $r) use ($app) {
            return $app->combat()->rewards($r->player);
        });
        $add('POST', '/combat/equipment/{code}/purchase', function (Request $r) use ($app, $idem) {
            return $app->combat()->buy($r->player, $r->params['code'], $idem($r));
        });
        $add('POST', '/combat/equipment/{code}/upgrade', function (Request $r) use ($app, $idem) {
            return $app->combat()->upgrade($r->player, $r->params['code'], $idem($r));
        });
        $add('PUT', '/combat/loadouts', function (Request $r) use ($app) {
            return $app->combat()->equip($r->player, $r->str('mode', 16), $r->str('slot', 16), $r->str('gearCode', 40, false));
        });
        $add('POST', '/combat/sessions', function (Request $r) use ($app) {
            return $app->combat()->start($r->player, $r->str('mode', 16), $r->str('code', 40));
        });
        $add('POST', '/combat/sessions/{id}/result', function (Request $r) use ($app) {
            return $app->combat()->result($r->player, $r->params['id'], $r->body);
        });

        // ── 디펜스 ──
        $add('GET', '/defense/zones', function (Request $r) use ($app) {
            return $app->defense()->zones($r->player);
        });
        $add('GET', '/defense/structures', function (Request $r) use ($app) {
            return $app->defense()->structures($r->player);
        });
        $add('POST', '/defense/structures/{code}/purchase', function (Request $r) use ($app, $idem) {
            return $app->defense()->purchase($r->player, $r->params['code'], $idem($r));
        });
        $add('POST', '/defense/structures/{code}/upgrade', function (Request $r) use ($app, $idem) {
            return $app->defense()->upgrade($r->player, $r->params['code'], $r->int('amountGc', false, 0), $idem($r));
        });
        $add('POST', '/defense/battles', function (Request $r) use ($app) {
            return $app->defense()->start($r->player, $r->str('zoneCode', 40));
        });
        $add('POST', '/defense/battles/{id}/finish', function (Request $r) use ($app) {
            return $app->defense()->finish($r->player, $r->params['id'], $r->body);
        });

        // ── 헌트 ──
        $add('GET', '/hunt/zones', function (Request $r) use ($app) {
            return $app->hunt()->zones($r->player);
        });
        $add('POST', '/hunt/sessions', function (Request $r) use ($app) {
            return $app->hunt()->start($r->player, $r->str('zoneCode', 40));
        });
        $add('POST', '/hunt/sessions/{id}/report', function (Request $r) use ($app) {
            return $app->hunt()->report($r->player, $r->params['id'], $r->int('seq'), isset($r->body['kills']) ? $r->body['kills'] : null);
        });

        // ── FPS ──
        $add('GET', '/fps/invasions', function (Request $r) use ($app) {
            return $app->fps()->invasions($r->player);
        });
        $add('GET', '/fps/missions', function (Request $r) use ($app) {
            return $app->fps()->missions($r->player);
        });
        $add('POST', '/fps/missions/{code}/start', function (Request $r) use ($app) {
            return $app->fps()->start($r->player, $r->params['code']);
        });
        $add('POST', '/fps/sessions/{id}/finish', function (Request $r) use ($app) {
            return $app->fps()->finish($r->player, $r->params['id'], $r->body);
        });

        // ── v10.0: 실시간 대시보드 (docs/API_SPEC.md 23장) ──
        // 카드 세 장이 한 번에 받아 가는 요약. 받는 인자가 없다: 언제나 접속한 사람 자신의 값이다.
        $add('GET', '/dashboard/summary', function (Request $r) use ($app) {
            return $app->dashboard()->summary($r->player);
        });

        // ── v9.0: 도시 성장, 이사, 지방세, 재정, 참여예산, 협동 방어전 ── (/cities/{code} 보다 먼저 걸리도록 여기서 등록한다)
        RoutesV9::register($app, $add, $idem);

        // ── 도시: 위험도, 침공, 랭킹, 지원 ── (고정된 경로를 {code} 보다 먼저 둔다)
        $add('GET', '/cities/risk', function (Request $r) use ($app) {
            return $app->city()->risks($r->player);
        });
        $add('GET', '/cities/invasions', function (Request $r) use ($app) {
            return $app->city()->invasions($r->player);
        });
        $add('GET', '/cities/rankings', function (Request $r) use ($app) {
            return $app->city()->rankings($r->player);
        });
        $add('GET', '/cities/support', function (Request $r) use ($app) {
            return $app->city()->supportList($r->player);
        });
        $add('GET', '/cities/support/contributions', function (Request $r) use ($app) {
            return $app->city()->supportContributions($r->player);
        });
        $add('POST', '/cities/support/leave', function (Request $r) use ($app) {
            return $app->city()->leaveSupport($r->player);
        });
        $add('POST', '/cities/support/{id}/join', function (Request $r) use ($app) {
            return $app->city()->joinSupport($r->player, $r->params['id']);
        });
        $add('POST', '/cities/facilities/{code}/repair', function (Request $r) use ($app, $idem) {
            return $app->city()->repairFacility($r->player, $r->params['code'], $idem($r));
        });
        $add('POST', '/cities/structures/{code}/fund', function (Request $r) use ($app, $idem) {
            return $app->city()->fundStructure($r->player, $r->params['code'], $r->int('amountGc'), $idem($r));
        });
        $add('GET', '/cities/{code}/history', function (Request $r) use ($app) {
            $limit = isset($r->query['limit']) ? (int)$r->query['limit'] : 50;
            $before = isset($r->query['before']) ? (int)$r->query['before'] : 0;
            return $app->city()->history($r->params['code'], $limit, $before);
        });
        $add('GET', '/cities/{code}', function (Request $r) use ($app) {
            return $app->city()->detail($r->player, $r->params['code']);
        });

        // ── v7.0 현실 혜택 ── (docs/CITY_PASS_API.md, docs/SCHOLARSHIP_API.md)
        // 아래 경로는 모두 접속 토큰이 있어야 한다. GC·전투 결과·랭킹을 받거나 바꾸는 경로는 없다.
        // 기능이 꺼져 있으면 (법률 검토와 운영 승인 전) 발급·제시·신청은 FEATURE_DISABLED 로 거절된다.

        // 홈페이지에 넣을 일회용 로그인 코드
        $add('POST', '/auth/web-code', function (Request $r) use ($app) {
            return $app->webAuth()->issueCode($r->player);
        });

        // CITY PASS
        $add('GET', '/city-pass/profile', function (Request $r) use ($app) {
            return $app->cityPass()->profile($r->player);
        });
        $add('PUT', '/city-pass/profile', function (Request $r) use ($app) {
            return $app->cityPass()->setInterestRegion($r->player, $r->str('interestRegionCode', 8));
        });
        $add('POST', '/city-pass/membership', function (Request $r) use ($app) {
            return $app->cityPass()->join($r->player, isset($r->body['agreeTerms']) && $r->body['agreeTerms'] === true,
                $r->str('interestRegionCode', 8, false));
        });
        $add('GET', '/city-pass/offers', function (Request $r) use ($app) {
            $query = function ($key) use ($r) {
                return isset($r->query[$key]) && is_string($r->query[$key]) && $r->query[$key] !== '' ? $r->query[$key] : null;
            };
            return $app->cityPass()->offers($r->player, $query('region'), $query('category'));
        });
        // 제휴 상점 목록 (게임의 "주변 제휴 상점"). 홈페이지와 같은 조건: 승인되고 계약 기간 안인 업체의 지점만.
        $add('GET', '/city-pass/merchants', function (Request $r) use ($app) {
            $query = function ($key) use ($r) {
                return isset($r->query[$key]) && is_string($r->query[$key]) && $r->query[$key] !== '' ? $r->query[$key] : null;
            };
            return $app->cityPass()->merchants($query('region'), $query('category'));
        });
        $add('GET', '/city-pass/coupons', function (Request $r) use ($app) {
            return $app->cityPass()->coupons($r->player);
        });
        $add('POST', '/city-pass/coupons/claim', function (Request $r) use ($app, $idem) {
            return $app->cityPass()->claim($r->player, $r->str('offerId', 16), $idem($r));
        });
        // 이름은 지시서의 것(redeem)이지만, 이용자가 부르는 이 경로는 쿠폰을 "사용됨"으로 바꾸지 않는다.
        // 상점에 보여 줄 5분짜리 일회용 코드를 내줄 뿐이다. 사용 처리는 상점 확인 화면(/merchant)이 서버에서 한다.
        $add('POST', '/city-pass/coupons/redeem', function (Request $r) use ($app) {
            return $app->cityPass()->present($r->player, $r->str('couponId', 16));
        });
        $add('GET', '/city-pass/history', function (Request $r) use ($app) {
            return $app->cityPass()->history($r->player);
        });

        // GUARDIANS SCHOLARSHIP (고정된 경로를 {id} 보다 먼저 둔다)
        $add('GET', '/scholarships', function () use ($app) {
            return $app->scholarships()->programs();
        });
        $add('GET', '/scholarships/my-applications', function (Request $r) use ($app) {
            return $app->scholarships()->myApplications($r->player);
        });
        $add('GET', '/scholarships/applications/{id}', function (Request $r) use ($app) {
            return $app->scholarships()->application($r->player, $r->params['id'], $r->ip);
        });
        $add('POST', '/scholarships/applications/{id}/withdraw', function (Request $r) use ($app) {
            return $app->scholarships()->withdraw($r->player, $r->params['id'], $r->ip);
        });
        $add('POST', '/scholarships/applications/{id}/appeal', function (Request $r) use ($app) {
            return $app->scholarships()->appeal($r->player, $r->params['id'], isset($r->body['reason']) ? $r->body['reason'] : null, $r->ip);
        });
        $add('POST', '/scholarships/applications/{id}/guardian-link', function (Request $r) use ($app) {
            return $app->scholarships()->reissueGuardianLink($r->player, $r->params['id'], $r->ip);
        });
        $add('GET', '/scholarships/{id}', function (Request $r) use ($app) {
            return $app->scholarships()->program($r->params['id']);
        });
        $add('POST', '/scholarships/{id}/applications', function (Request $r) use ($app) {
            return $app->scholarships()->apply($r->player, $r->params['id'], $r->body, $r->ip);
        });

        return $routes;
    }
}
