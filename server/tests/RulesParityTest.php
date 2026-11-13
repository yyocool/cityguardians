<?php
use CG\Domain\Battle;
use CG\Domain\Fps;
use CG\Domain\Gear;
use CG\Domain\Hunt;
use CG\Domain\Invasion;
use CG\Domain\Nickname;
use CG\Domain\Progression;
use CG\Domain\Ranking;
use CG\Domain\Risk;
use CG\Domain\Support;

/**
 * 서버(PHP)의 규칙이 클라이언트(C#)의 규칙과 같은 결과를 내는지 확인한다.
 *
 * tests/fixtures/vectors.json 은 tools/check/vectors 가 클라이언트의 규칙 코드를 실제로 돌려 뽑은
 * "입력 → 결과" 표본이다. 여기서는 같은 입력을 PHP 규칙에 넣어 결과를 비교한다.
 * DB 가 필요 없다.
 */
final class RulesParityTest
{
    private static $data;
    private static $defs;

    private static function load()
    {
        if (self::$data === null) {
            self::$data = json_decode(file_get_contents(__DIR__ . '/fixtures/vectors.json'), true);
            self::$defs = json_decode(file_get_contents(__DIR__ . '/fixtures/defs.json'), true);
            T::ok(is_array(self::$data) && is_array(self::$defs), '표본 파일을 읽을 수 없습니다.');
        }
    }

    /** @return array[] 그 함수의 표본. */
    private static function cases($fn)
    {
        self::load();
        $found = [];
        foreach (self::$data['cases'] as $case) {
            if ($case['fn'] === $fn) {
                $found[] = $case;
            }
        }
        T::ok(count($found) > 0, "표본이 없습니다: $fn");
        return $found;
    }

    /** 표본의 정책은 기본값과 다른 값만 들어 있다. */
    private static function policy($diff)
    {
        return Risk::fillPolicy(is_array($diff) ? $diff : []);
    }

    private static function gearList()
    {
        self::load();
        return array_merge(self::$defs['combat']['gear'], self::$data['extraGear']);
    }

    private static function findGear($code)
    {
        if ($code === null) {
            return null;
        }
        foreach (self::gearList() as $def) {
            if ($def['code'] === $code) {
                return $def;
            }
        }
        throw new TestFailure("표본의 장비가 정의에 없습니다: $code");
    }

    public function testDefaultPolicyMatchesClient()
    {
        self::load();
        $client = self::$defs['combat']['risk'];
        $weights = [];
        foreach ($client['weights'] as $row) {
            $weights[$row['reason']] = $row['deltaCenti'];
        }
        $client['weights'] = $weights;
        T::same($client, Risk::defaultPolicy(), '기본 위험도 정책');
        T::same(self::$defs['combat']['ranking'], Ranking::defaultWeights(), '기본 랭킹 가중치');
        T::eq([], Risk::validatePolicy(Risk::defaultPolicy()), '기본 정책은 범위 검사를 통과해야 한다');
        T::eq([], Ranking::validateWeights(Ranking::defaultWeights()));
        T::same(self::$defs['progression'], Progression::defaultConfig(), '기본 레벨 설정');
    }

    public function testRoll()
    {
        foreach (self::cases('roll') as $case) {
            $in = $case['in'];
            T::eq($case['out'], Invasion::roll($in['seed'], $in['code'], $in['tick'], $in['salt'], $in['modulo']),
                'roll ' . json_encode($in));
        }
    }

    public function testInitialRisk()
    {
        self::load();
        foreach (self::cases('initialRisk') as $case) {
            $policy = self::policy($case['in']['policy']);
            T::eq(230, count($case['out']));
            foreach ($case['out'] as $row) {
                $spread = $policy['initialSpread'];
                $points = $policy['initialRisk'] + Invasion::roll(self::$data['seed'], $row['code'], 0, 9, $spread * 2 + 1) - $spread;
                T::eq($row['centi'], Risk::clamp($points * 100), '처음 위험도 ' . $row['code']);
            }
        }
    }

    public function testTicks()
    {
        foreach (self::cases('tickOf') as $case) {
            $policy = self::policy(['tickMinutes' => $case['in']['tickMinutes']]);
            $tick = Invasion::tickOf($case['in']['unix'], $policy);
            T::eq($case['out']['tick'], $tick);
            T::eq($case['out']['startUnix'], Invasion::tickStart($tick, $policy));
        }
    }

    public function testTryStart()
    {
        $started = 0;
        foreach (self::cases('tryStart') as $case) {
            $in = $case['in'];
            $event = Invasion::tryStart(self::policy($in['policy']), $in['code'], $in['tick'], $in['seed']);
            if ($case['out'] === null) {
                T::eq(null, $event, 'tryStart ' . $in['code'] . ' ' . $in['tick']);
                continue;
            }
            $started++;
            T::ok($event !== null, '침공이 시작되어야 한다 ' . $in['code'] . ' ' . $in['tick']);
            T::eq($case['out']['id'], $event['id']);
            T::eq($case['out']['kind'], $event['kind']);
            T::eq($case['out']['severity'], $event['severity']);
            T::eq($case['out']['startUnix'], $event['startUnix']);
            T::eq($case['out']['endUnix'], $event['endUnix']);
        }
        T::ok($started > 50, '시작된 침공의 표본이 충분해야 한다');
    }

    public function testRequiredWinsAndNpcHold()
    {
        foreach (self::cases('requiredWins') as $case) {
            $in = $case['in'];
            T::eq($case['out'], Invasion::requiredWins(self::policy($in['policy']), $in['severity'], $in['active']));
        }
        foreach (self::cases('npcHold') as $case) {
            $in = $case['in'];
            $policy = self::policy($in['policy']);
            T::eq($case['out']['percent'], Invasion::npcHoldPercent($policy, $in['power'], $in['invasion']));
            T::eq($case['out']['status'], Invasion::resolveExpired($policy, $in['invasion'], $in['power'], $in['seed'], $in['tick']));
        }
        foreach (self::cases('invasionCounts') as $case) {
            $in = $case['in'];
            $invasion = ['kind' => $in['kind'], 'status' => $in['status']];
            T::eq($case['out']['counts'], Invasion::counts($invasion, $in['mode']), 'counts ' . json_encode($in));
            T::eq($case['out']['startReason'], Invasion::startReason($in['kind']));
            T::eq($case['out']['winReason'], Invasion::winReason($in['mode']));
            T::eq($case['out']['repelReason'], Invasion::repelReason($in['kind']));
            T::eq($case['out']['failReason'], Invasion::failReason($in['kind']));
        }
    }

    public function testRiskStages()
    {
        foreach (self::cases('stage') as $case) {
            $policy = self::policy($case['in']['policy']);
            $centi = $case['in']['centi'];
            $stage = Risk::stageOf($centi, $policy);
            T::eq($case['out']['clamp'], Risk::clamp($centi));
            T::eq($case['out']['points'], Risk::toPoints($centi));
            T::eq($case['out']['stage'], $stage);
            T::eq($case['out']['name'], Risk::stageName($stage));
            T::eq($case['out']['icon'], Risk::stageIcon($stage));
            T::eq($case['out']['order'], Risk::stageOrder($stage));
        }
        foreach (self::cases('reasonName') as $case) {
            T::eq($case['out'], Risk::reasonName($case['in']['reason']));
        }
    }

    public function testRiskChanges()
    {
        foreach (self::cases('applySystem') as $case) {
            $in = $case['in'];
            T::same($case['out'], Risk::applySystem($in['current'], $in['reason'], $in['quantity'], self::policy($in['policy'])),
                'applySystem ' . json_encode($in));
        }
        foreach (self::cases('applyExact') as $case) {
            $in = $case['in'];
            T::same($case['out'], Risk::applyExact($in['current'], Risk::ADMIN_ADJUST, $in['delta'], self::policy($in['policy'])));
        }
        $raised = 0;
        foreach (self::cases('applyPlayer') as $case) {
            $in = $case['in'];
            $policy = self::policy($in['policy']);
            list($change, $reduced) = Risk::applyPlayer($in['current'], $in['reason'], $in['units'], $policy, $in['active'], $in['reducedToday']);
            T::same($case['out']['change'], $change, 'applyPlayer ' . json_encode($in));
            T::eq($case['out']['reducedToday'], $reduced);
            T::eq($case['out']['scale'], Risk::scalePercent($policy, $in['active']));
            if ($change['after'] > $change['before']) {
                $raised++;
            }
        }
        T::eq(0, $raised, '플레이어의 활동이 위험도를 올린 표본이 없어야 한다');
        foreach (self::cases('stabilizeAndPower') as $case) {
            $in = $case['in'];
            $policy = self::policy($in['policy']);
            T::eq($case['out']['delta'], Risk::dailyStabilizeDelta($in['current'], $policy));
            T::eq($case['out']['power'], Risk::defensePower($policy, $in['defensePoints'], $in['structurePower']));
        }
    }

    public function testRanking()
    {
        foreach (self::cases('rank') as $case) {
            $ranked = Ranking::rank($case['in']['stats'], $case['in']['weights']);
            T::same($case['out'], $ranked, '랭킹');
        }
    }

    public function testGear()
    {
        foreach (self::cases('gear') as $case) {
            $in = $case['in'];
            $def = self::findGear($in['def']);
            $owned = [];
            foreach ($in['owned'] as $row) {
                $owned[$row['code']] = $row['level'];
            }
            $label = 'gear ' . json_encode($in, JSON_UNESCAPED_UNICODE);
            $level = Gear::levelOf($owned, $def);
            T::eq($case['out']['level'], $level, $label);
            T::eq($case['out']['buy'], Gear::checkBuy($def, $owned, $in['playerLevel'], $in['balance']), $label);
            T::eq($case['out']['upgrade'], Gear::checkUpgrade($def, $owned, $in['balance']), $label);
            T::eq($case['out']['upgradeCost'], Gear::upgradeCost($def, max(1, $level)), $label);
            T::eq($case['out']['equip'], Gear::checkEquip($def, $owned, $in['mode'], $in['slot']), $label);
            T::eq($case['out']['damage'], Gear::damage($def, $in['playerLevel']), $label);
            T::eq($case['out']['effect'], Gear::effectValue($def, $in['playerLevel']), $label);
        }
        self::load();
        foreach (self::cases('equipped') as $case) {
            $in = $case['in'];
            $owned = [];
            foreach ($in['owned'] as $row) {
                $owned[$row['code']] = $row['level'];
            }
            $loadouts = [];
            foreach ($in['loadouts'] as $row) {
                $loadouts[$row['mode'] . ':' . $row['slot']] = $row['gearCode'];
            }
            $def = Gear::equipped(self::$defs['combat']['gear'], $owned, $loadouts, $in['mode'], $in['slot']);
            T::eq($case['out'], $def ? $def['code'] : null, 'equipped ' . json_encode($in));
        }
    }

    public function testFreeGearCoversEverySlotOfFps()
    {
        // 무료 기본 장비만으로 핵심 전투에 참여할 수 있어야 한다: 주 무기가 반드시 있다.
        self::load();
        $def = Gear::equipped(self::$defs['combat']['gear'], [], [], Invasion::MODE_FPS, Gear::SLOT_PRIMARY);
        T::ok($def !== null && $def['priceGc'] === 0, 'FPS 의 무료 주 무기');
    }

    private static function stage($code)
    {
        self::load();
        foreach (self::$defs['stages'] as $stage) {
            if ($stage['code'] === $code) {
                return $stage;
            }
        }
        throw new TestFailure("구역이 없습니다: $code");
    }

    public function testBattle()
    {
        $accepted = 0;
        foreach (self::cases('battle') as $case) {
            $in = $case['in'];
            $verdict = Battle::evaluate(self::stage($in['stage']), $in['report'], $in['elapsed'], $in['alreadyCleared']);
            T::same($case['out'], $verdict, 'battle ' . json_encode($in));
            $accepted += $verdict['accepted'] ? 1 : 0;
        }
        T::ok($accepted > 30, '인정된 방어전 표본이 충분해야 한다');
    }

    public function testFps()
    {
        self::load();
        $mission = self::$defs['combat']['fpsMissions'][0];
        $accepted = 0;
        foreach (self::cases('fps') as $case) {
            $in = $case['in'];
            T::eq($mission['code'], $in['mission']);
            $verdict = Fps::evaluate($mission, $in['report'], $in['elapsed'], $in['alreadyCleared'], $in['rewardAllowed']);
            T::same($case['out'], $verdict, 'fps ' . json_encode($in));
            $accepted += $verdict['accepted'] ? 1 : 0;
        }
        T::ok($accepted > 30, '인정된 FPS 표본이 충분해야 한다');
    }

    public function testHunt()
    {
        self::load();
        $enemies = [];
        foreach (self::$defs['enemies'] as $enemy) {
            $enemies[$enemy['code']] = $enemy;
        }
        $zones = [];
        foreach (self::$defs['zones'] as $zone) {
            $zones[$zone['code']] = $zone;
        }
        $limit = self::$defs['economy']['dailyHuntGcLimit'];
        foreach (self::cases('hunt') as $case) {
            $in = $case['in'];
            list($verdict, $allowance) = Hunt::evaluate($enemies, $zones[$in['zone']], $in['kills'], $in['elapsed'],
                $in['carried'], $in['dailySoFar'], $limit);
            T::same($case['out']['verdict'], $verdict, 'hunt ' . json_encode($in));
            T::near($case['out']['newAllowance'], $allowance, 0.002, 'hunt allowance ' . json_encode($in));
        }
    }

    public function testLevels()
    {
        self::load();
        $config = self::$defs['progression'];
        foreach (self::cases('levelTable') as $case) {
            T::eq($config['maxLevel'], count($case['out']));
            foreach ($case['out'] as $row) {
                T::eq($row['xpNext'], Progression::xpForNextLevel($row['level'], $config), '레벨 ' . $row['level']);
                T::eq($row['hpMax'], Progression::maxHp($row['level'], $config));
            }
        }
        foreach (self::cases('addXp') as $case) {
            $in = $case['in'];
            list($level, $xp, $levels) = Progression::addXp($in['level'], $in['xp'], $in['gained'], $config);
            T::eq($case['out'], ['level' => $level, 'xp' => $xp, 'levels' => $levels], 'addXp ' . json_encode($in));
        }
    }

    public function testNicknames()
    {
        foreach (self::cases('nickname') as $case) {
            $name = $case['in']['name'];
            T::eq($case['out']['reason'], Nickname::check($name), '닉네임 ' . json_encode($name, JSON_UNESCAPED_UNICODE));
            T::eq($case['out']['normalized'], Nickname::normalize($name));
            T::eq($case['out']['key'], Nickname::key($name));
        }
    }

    public function testSupport()
    {
        foreach (self::cases('supportJoin') as $case) {
            $in = $case['in'];
            $request = $in['target'] === null ? null : ['regionCode' => $in['target'], 'status' => $in['status']];
            T::eq($case['out'], Support::checkJoin($request, $in['home'], $in['homeSido'], $in['targetSido']),
                'supportJoin ' . json_encode($in));
        }
        foreach (self::cases('supportOpen') as $case) {
            $in = $case['in'];
            $policy = self::policy($in['policy']);
            T::eq($case['out']['open'], Support::shouldOpen($policy, $in['centi'], $in['hasOpen']));
            T::eq($case['out']['resolved'], Support::isResolved($policy, $in['centi']));
        }
    }

    public function testPolicyValidationRejectsBrokenPolicies()
    {
        $policy = Risk::defaultPolicy();
        $policy['weights'][Risk::DEFENSE_WON] = 100;   // 플레이어의 승리가 위험도를 올리게 하는 값
        T::ok(count(Risk::validatePolicy($policy)) > 0, '내리는 활동의 가중치는 양수가 될 수 없다');

        $policy = Risk::defaultPolicy();
        $policy['cautionAt'] = 60;
        T::ok(count(Risk::validatePolicy($policy)) > 0, '단계 경계의 순서');

        $policy = Risk::defaultPolicy();
        $policy['invasionChancePermille'] = 1001;
        T::ok(count(Risk::validatePolicy($policy)) > 0, '침공 확률의 범위');

        $policy = Risk::defaultPolicy();
        $policy['weights'][Risk::INVASION_STARTED] = -1;
        T::ok(count(Risk::validatePolicy($policy)) > 0, '올리는 사건의 가중치는 음수가 될 수 없다');
    }
}
