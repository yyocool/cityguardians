<?php
use CG\Infra\Clock;
use CG\Infra\Wire;
use CG\Service\Modeling;
use CG\Service\Taxes;

/**
 * v9.0 게임 서버(PHP)와 게임 클라이언트(Unity, C#) 사이의 약속 (docs/API_SPEC.md 19~22장).
 *
 * 클라이언트에는 아직 이 서버에 붙는 HTTP 구현이 없다. 그래서 양쪽의 응답 형식이 어긋나도 알아챌 방법이 없었다.
 * 이 테스트는 v9.0 의 플레이어용 응답마다 "어떤 이름의 값이 어떤 종류로 오는가"(모양)를 표본으로 남기고,
 * 클라이언트 쪽 테스트(tools/check/rules/WireContractTests.cs)가 같은 표본으로 DTO 의 필드를 대조한다.
 *
 *   fixtures/client-wire.json   응답의 모양. 값은 들어 있지 않다 (이름과 종류만).
 *
 * 응답이 바뀌면 이 테스트가 실패한다. 클라이언트의 DTO(client/.../Services/CityOpsDtos.cs)를 함께 고친 뒤
 * CG_UPDATE_FIXTURES=1 로 다시 돌려 표본을 새로 만든다.
 */
final class ClientWireTest extends CityTestCase
{
    private static function fixture()
    {
        return __DIR__ . '/fixtures/client-wire.json';
    }

    /** 값을 모양으로 줄인다: 객체는 이름 → 모양, 목록은 [원소들의 모양을 합친 것], 나머지는 종류의 이름. */
    private static function shape($value)
    {
        if (is_array($value)) {
            if ($value === [] || Wire::isList($value)) {
                $merged = null;
                foreach ($value as $item) {
                    $merged = $merged === null ? self::shape($item) : self::merge($merged, self::shape($item));
                }
                return $merged === null ? [] : [$merged];
            }
            $out = [];
            foreach ($value as $key => $item) {
                $out[$key] = self::shape($item);
            }
            ksort($out);
            return $out;
        }
        if (is_bool($value)) {
            return 'bool';
        }
        if (is_int($value)) {
            return 'int';
        }
        if (is_float($value)) {
            return 'float';
        }
        return $value === null ? 'null' : 'string';
    }

    private static function isListShape($shape)
    {
        return is_array($shape) && ($shape === [] || array_keys($shape) === [0]);
    }

    private static function merge($a, $b)
    {
        if ($a === $b) {
            return $a;
        }
        if (is_string($a) && is_string($b)) {
            $kinds = array_unique(array_merge(explode('|', $a), explode('|', $b)));
            sort($kinds);
            return implode('|', $kinds);
        }
        if (is_array($a) && is_array($b)) {
            if (self::isListShape($a) && self::isListShape($b)) {
                if ($a === []) {
                    return $b;
                }
                return $b === [] ? $a : [self::merge($a[0], $b[0])];
            }
            if (!self::isListShape($a) && !self::isListShape($b)) {
                foreach ($b as $key => $item) {
                    $a[$key] = array_key_exists($key, $a) ? self::merge($a[$key], $item) : $item;
                }
                ksort($a);
                return $a;
            }
        }
        // 값이 없을 때(null)와 있을 때(객체·목록)가 섞이면 있는 쪽의 모양을 남긴다.
        if ($a === 'null') {
            return $b;
        }
        if ($b === 'null') {
            return $a;
        }
        throw new \RuntimeException('모양이 서로 다르다: ' . json_encode($a) . ' / ' . json_encode($b));
    }

    public function testResponsesTheClientReadsKeepTheirShape()
    {
        $this->quiet();
        $app = $this->app;
        $seen = [];
        $note = function ($name, $data) use (&$seen) {
            $shape = self::shape($data);
            $seen[$name] = isset($seen[$name]) ? self::merge($seen[$name], $shape) : $shape;
            return $data;
        };

        // ── 주민들 ──
        $me = $this->setLevel($this->linkedResident('11110', '표본시민'), 10);
        $this->give($me, 99500);
        $voters = [];
        for ($i = 0; $i < 5; $i++) {
            $voters[] = $this->setLevel($this->resident('11110'), 2);
        }

        // ── 지방세 ──
        $this->enableTax(Taxes::TRADE);
        $this->enableTax(Taxes::PROPERTY, ['rate_bp' => 2000, 'cap_gc' => 0, 'exempt_amount' => 0]);
        $app->combat()->buy($me, 'gear_precision_rifle', $this->key());
        T::ok($app->taxes()->collectProperty()['totalGc'] > 0);
        $note('GET /tax/policies', $app->taxes()->policiesFor($me));
        $note('GET /tax/transactions', $app->taxes()->transactionsOf($me, 50, ''));

        // ── 참여예산 ──
        $this->enableBudget();
        Clock::advance(4 * 86400);
        $app->growth()->refreshAll();
        $note('GET /cities/budget/options', $app->budget()->options($me));
        $proposal = $note('POST /cities/budget/proposals', $app->budget()->propose($me, ['category' => 'defense_facility',
            'title' => '에너지 방어막을 함께 세워요', 'description' => '관문 앞의 방어막에 도시 예산을 보태요.', 'requestedGc' => 5000,
            'targetCode' => 'twr_energy_shield'], $this->key()))['proposal'];
        $rowId = (int)$this->db->val('SELECT id FROM city_budget_proposals WHERE public_id = ?', [$proposal['id']]);
        T::eq([], $app->budget()->review($this->operator(), $rowId, true, '규칙에 맞는 제안'));
        $note('GET /cities/{code}/budget/proposals', $app->budget()->listFor($me, '11110', ''));
        foreach ($voters as $voter) {
            $app->budget()->vote($voter, $proposal['id'], 'yes', $this->key());
        }
        $note('POST /cities/budget/proposals/{id}/vote', $app->budget()->vote($me, $proposal['id'], 'yes', $this->key()));
        $note('GET /cities/{code}/budget/proposals', $app->budget()->listFor($me, '11110', ''));
        Clock::advance(7 * 86400);
        T::eq([], $app->budget()->execute($this->operator(), $rowId, '주민 투표 결과 집행'));
        $note('GET /cities/budget/proposals/{id}', $app->budget()->detail($me, $proposal['id']));

        // ── 도시 재정 ──
        $note('GET /cities/{code}/treasury', $app->treasury()->summary('11110'));
        $note('GET /cities/{code}/treasury/expenditures', $app->treasury()->expenditures('11110', 50, ''));

        // ── 지역 콘텐츠 ──
        $maker = $this->contentMaker();
        list($projectId, $problems) = $app->modeling()->createProject($this->operator(),
            ['region_code' => '11110', 'kind' => 'street', 'title' => '종로 상징거리', 'assignee_admin_id' => (string)$maker['id']]);
        T::eq([], $problems);
        T::eq([], $app->modeling()->transition($maker, $projectId, Modeling::PLANNING, '기획 시작'));
        T::eq([], $app->modeling()->transition($maker, $projectId, Modeling::MODELING, '제작 시작'));
        T::eq([], $app->modeling()->addAsset($maker, $projectId, ['version' => '1.0.0', 'address_key' => 'city/11110/street',
            'catalog_path' => 'city-11110/1.0.0/catalog.json', 'content_hash' => str_repeat('ab', 32), 'size_bytes' => '1048576',
            'min_client_version' => '9.0.0', 'note' => '첫 판']));
        T::eq([], $app->modeling()->transition($maker, $projectId, Modeling::REVIEW, '검수 요청'));
        T::eq([], $app->modeling()->review($this->super(), $projectId, 'pass', '확인했습니다'));
        $assetId = (int)$app->modeling()->assets($projectId)[0]['id'];
        T::eq([], $app->modeling()->reviewAsset($this->operator(), $assetId, true, '해시와 크기를 확인함'));
        T::eq([], $app->modeling()->release($this->super(), $assetId, '100', '정식 개방'));
        T::eq(1, count($note('GET /cities/content', $app->modeling()->catalog($me, '9.0.0'))['items']));

        // ── 도시 성장 ──
        $note('GET /cities/growth', $app->growth()->cities());
        $note('GET /cities/{code}/growth', $app->growth()->city('11110'));
        $note('GET /residency', $app->growth()->residency($me));

        // ── 협동 방어전 ──
        $this->enableCoop();
        $partner = $voters[0];
        $note('GET /coop/config', $app->coop()->config($me));
        $id = $note('POST /coop/sessions', $app->coop()->create($me, 'outskirts_1', 4, null, $this->key()))['session']['id'];
        $note('POST /coop/sessions/{id}/join', $app->coop()->join($partner, $id, 'builder', $this->key()));
        $note('GET /coop/config', $app->coop()->config($me));
        $note('GET /coop/sessions', $app->coop()->sessions($partner));
        $note('POST /coop/sessions/{id}/ticket', $app->coop()->ticket($me, $id));
        $note('GET /coop/sessions/{id}/result', $app->coop()->result($me, $id));
        $app->coop()->internalStart($id, ['by' => $me['publicId'], 'players' => [$me['publicId'], $partner['publicId']]]);
        $raw = str_replace(['p_fixture_a', 'p_fixture_b'], [$me['publicId'], $partner['publicId']],
            file_get_contents(__DIR__ . '/fixtures/coop-report.json'));
        $report = Wire::camel(json_decode($raw, true));
        Clock::advance($report['durationSeconds'] + 1);
        T::eq(true, $app->coop()->internalResult($id, $report)['accepted']);
        $note('GET /coop/sessions/{id}/result', $app->coop()->result($me, $id));
        // 나가기: 다른 대기실을 하나 만들어 나가 본다.
        $other = $app->coop()->create($voters[1], 'outskirts_1', 4, null, $this->key())['session']['id'];
        $note('POST /coop/sessions/{id}/leave', $app->coop()->leave($voters[1], $other));

        // ── 이사 (맨 끝: 주거 도시가 바뀐다) ──
        $note('POST /residency/move', $app->growth()->move($me, '26110', $this->key()));

        // ── 실시간 대시보드 (v10.0) ── 이사한 도시에 침공이 있는 상황 (알림과 긴급 도시 목록이 비지 않도록).
        $app->city()->adminStartInvasion(null, '26110', \CG\Domain\Invasion::OUTSKIRTS, 2);
        $note('GET /dashboard/summary', $app->dashboard()->summary($this->fresh($me)));

        ksort($seen);
        $text = json_encode($seen, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES | JSON_PRETTY_PRINT) . "\n";
        // 내부 번호나 개인정보의 이름이 응답에 없다 (playerId 는 공개 ID 다).
        foreach (['"player_id"', '"email"', '"secret"', '"guestKey"', '"ip"', '"adminId"'] as $never) {
            T::ok(strpos($text, $never) === false, $never);
        }
        if (getenv('CG_UPDATE_FIXTURES') === '1') {
            file_put_contents(self::fixture(), $text);
        }
        T::ok(is_file(self::fixture()), 'fixtures/client-wire.json 이 없다. CG_UPDATE_FIXTURES=1 로 만든다.');
        T::eq(json_decode(file_get_contents(self::fixture()), true), json_decode($text, true),
            '클라이언트가 읽는 응답의 모양이 바뀌었다. client/.../Services/CityOpsDtos.cs, DashboardDtos.cs 를 확인하고 표본을 새로 만든다.');
    }
}
