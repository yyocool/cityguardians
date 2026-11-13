<?php
use CG\Infra\ApiError;
use CG\Infra\Clock;

/**
 * 세 전투 모드의 흐름: 시작 → 결과 보고 → 서버 검증 → 보상 → 도시 반영.
 * 계정·레벨·GC·소속 도시는 세 모드가 함께 쓴다.
 */
final class CombatFlowTest extends DbTestCase
{
    // ── 디펜스 ──────────────────────────────────────────────

    public function testDefenseWinPaysTheDefinedRewardAndLowersCityRisk()
    {
        $this->quiet();
        $player = $this->resident('11110');
        $gcBefore = $this->gc($player);
        $riskBefore = $this->riskCenti('11110');
        $pointsBefore = (int)$this->db->val("SELECT defense_points FROM regions WHERE code = '11110'");

        $result = $this->winDefense($player);

        T::same(true, $result['accepted']);
        T::same('win', $result['result']);
        T::same(500, $result['rewardGc'], '승리 300 + 첫 승리 200');
        T::same(150, $result['rewardXp']);
        T::same(true, $result['firstWin']);
        T::same($gcBefore + 500, $result['gc']);
        T::same($gcBefore + 500, $this->gc($player));
        T::same(100, $result['defensePoints']);
        T::same('11110', $result['regionCode']);
        T::eq(['outskirts_1'], $result['clearedStages']);
        T::same($pointsBefore + 100, (int)$this->db->val("SELECT defense_points FROM regions WHERE code = '11110'"));

        // 침공이 없을 때의 승리는 순찰로 쳐서 절반만 반영된다: 600 × 50% = 300
        T::same($riskBefore - 300, $this->riskCenti('11110'));
        T::same(-300, $result['city']['riskDeltaCenti']);
        T::same('player', $result['city']['actor']);
        T::same('defense_won', $result['city']['reason']);
        T::same(10, $result['city']['contribution']);

        $history = $this->db->one('SELECT * FROM city_risk_history ORDER BY id DESC LIMIT 1');
        T::same('defense_won', $history['reason']);
        T::same('player', $history['actor']);
        T::eq($player['id'], $history['player_id']);
        T::eq(-300, $history['delta_centi']);

        T::same(null, $this->app->wallets()->findInconsistency($player['id']));
        T::same(null, $this->app->city()->findInconsistency());
    }

    public function testReportingAFinishedBattleAgainDoesNotPayTwice()
    {
        $this->quiet();
        $player = $this->resident();
        $session = $this->app->defense()->start($player, 'outskirts_1');
        Clock::advance(60);
        $first = $this->app->defense()->finish($player, $session['sessionId'], self::defenseWinReport());
        $gc = $this->gc($player);
        $risk = $this->riskCenti('11110');
        $ledger = (int)$this->db->val('SELECT COUNT(*) FROM ledger_entries');

        $second = $this->app->defense()->finish($player, $session['sessionId'], self::defenseWinReport());
        // 다른 내용으로 다시 보고해도 처음 결과가 그대로다.
        $third = $this->app->defense()->finish($player, $session['sessionId'], ['result' => 'lose', 'kills' => [], 'baseHpLeft' => 0, 'wavesCleared' => 0]);

        T::eq($first, $second);
        T::eq($first, $third);
        T::same($gc, $this->gc($player));
        T::same($risk, $this->riskCenti('11110'));
        T::same($ledger, (int)$this->db->val('SELECT COUNT(*) FROM ledger_entries'));
    }

    public function testDefenseReportThatFinishedTooFastIsRejectedWithNoRewardAndNoCityChange()
    {
        $this->quiet();
        $player = $this->resident();
        $gc = $this->gc($player);
        $risk = $this->riskCenti('11110');
        $session = $this->app->defense()->start($player, 'outskirts_1');
        Clock::advance(5);   // 다섯 웨이브에 최소 50초가 걸린다

        $result = $this->app->defense()->finish($player, $session['sessionId'], self::defenseWinReport());

        T::same(false, $result['accepted']);
        T::same('finished_too_fast', $result['rejectReason']);
        T::same(0, $result['rewardGc']);
        T::same(0, $result['rewardXp']);
        T::same(null, $result['city']);
        T::same($gc, $this->gc($player));
        T::same($risk, $this->riskCenti('11110'));
        T::same(1, $this->fresh($player)['level']);
    }

    public function testImpossibleKillCountsAreRejected()
    {
        $this->quiet();
        $player = $this->resident();
        $session = $this->app->defense()->start($player, 'outskirts_1');
        Clock::advance(60);
        $report = self::defenseWinReport();
        $report['kills'][0]['count'] = 9999;

        $result = $this->app->defense()->finish($player, $session['sessionId'], $report);

        T::same(false, $result['accepted']);
        T::same('kills_exceed_stage', $result['rejectReason']);
        T::same(0, $result['rewardGc']);
    }

    public function testLosingADefenseBattleNeverRaisesRiskOrTakesGc()
    {
        $this->quiet();
        $player = $this->resident();
        $gc = $this->gc($player);
        $risk = $this->riskCenti('11110');
        $session = $this->app->defense()->start($player, 'outskirts_1');
        Clock::advance(30);

        $result = $this->app->defense()->finish($player, $session['sessionId'], [
            'result' => 'lose', 'baseHpLeft' => 0, 'wavesCleared' => 1,
            'kills' => [['enemyCode' => 'malfunction_robot', 'count' => 6]],
        ]);

        T::same(true, $result['accepted']);
        T::same('lose', $result['result']);
        T::same(20, $result['rewardGc'], '막아낸 웨이브당 20 GC');
        T::same($gc + 20, $this->gc($player));
        T::same(null, $result['city']);
        T::same($risk, $this->riskCenti('11110'), '져도 위험도는 오르지 않는다');
        T::eq(0, $this->db->val('SELECT COUNT(*) FROM ledger_entries WHERE player_id = ? AND delta < 0', [$player['id']]));
        // 시도 횟수만 센다.
        T::eq(1, $this->db->val("SELECT s.player_attempts FROM city_risk_status s JOIN regions r ON r.id = s.region_id WHERE r.code = '11110'"));
        T::eq(0, $this->db->val("SELECT s.player_wins FROM city_risk_status s JOIN regions r ON r.id = s.region_id WHERE r.code = '11110'"));
    }

    public function testAStrangerGetsTheRewardButHasNoCityToAffect()
    {
        $this->quiet();
        $stranger = $this->guest();
        $this->app->city()->tick();
        $historyBefore = (int)$this->db->val('SELECT COUNT(*) FROM city_risk_history');

        $result = $this->winDefense($stranger);

        T::same(true, $result['accepted']);
        T::same(500, $result['rewardGc']);
        T::same(null, $result['city']);
        T::same(0, $result['defensePoints']);
        T::same($historyBefore, (int)$this->db->val('SELECT COUNT(*) FROM city_risk_history'));
    }

    public function testASessionBelongsToThePlayerWhoStartedIt()
    {
        $owner = $this->resident();
        $other = $this->resident();
        $session = $this->app->defense()->start($owner, 'outskirts_1');
        Clock::advance(60);
        T::throwsApi(ApiError::NOT_FOUND, function () use ($other, $session) {
            $this->app->defense()->finish($other, $session['sessionId'], self::defenseWinReport());
        });
    }

    public function testMalformedReportsAreRefused()
    {
        $player = $this->resident();
        $session = $this->app->defense()->start($player, 'outskirts_1');
        Clock::advance(60);
        $bad = [
            ['result' => 'win', 'kills' => 'many', 'baseHpLeft' => 20, 'wavesCleared' => 5],
            ['result' => 'win', 'kills' => [['enemyCode' => 'mech_insect', 'count' => '5']], 'baseHpLeft' => 20, 'wavesCleared' => 5],
            ['result' => 'win', 'kills' => [], 'baseHpLeft' => 20.5, 'wavesCleared' => 5],
            ['result' => ['win'], 'kills' => [], 'baseHpLeft' => 20, 'wavesCleared' => 5],
        ];
        foreach ($bad as $report) {
            T::throwsApi(ApiError::INVALID_REQUEST, function () use ($player, $session, $report) {
                $this->app->defense()->finish($player, $session['sessionId'], $report);
            });
        }
        // 형식이 틀린 보고는 전투를 끝내지 않는다. 올바른 보고를 다시 보낼 수 있다.
        $result = $this->app->defense()->finish($player, $session['sessionId'], self::defenseWinReport());
        T::same(true, $result['accepted']);
    }

    // ── 디펜스: 방어시설 ────────────────────────────────────

    public function testBasicTowerIsUsableForFreeAndPermanentTowersAreUnlockedWithGc()
    {
        $player = $this->resident();
        T::eq(['twr_machine_gun'], array_values(array_filter($this->app->defense()->usableStructures($player['id']), function ($code) {
            return $code === 'twr_machine_gun';
        })), '무료 기본 타워는 처음부터 쓸 수 있다');
        T::ok(!in_array('twr_blast', $this->app->defense()->usableStructures($player['id']), true));

        $this->give($player, 1000);
        $gc = $this->gc($player);
        $key = $this->key();
        $result = $this->app->defense()->purchase($player, 'twr_blast', $key);

        T::same(500, $result['costGc']);
        T::same($gc - 500, $result['gc']);
        T::ok(in_array('twr_blast', $result['unlockedTowers'], true));

        // 같은 요청이 다시 와도 한 번만 낸다.
        $again = $this->app->defense()->purchase($player, 'twr_blast', $key);
        T::same(true, $again['duplicate']);
        T::same($gc - 500, $this->gc($player));
        // 이미 가진 것을 새 요청으로 또 살 수는 없다.
        T::throwsApi(ApiError::CONFLICT, function () use ($player) {
            $this->app->defense()->purchase($player, 'twr_blast', $this->key());
        });
        T::same(null, $this->app->wallets()->findInconsistency($player['id']));
    }

    public function testStructurePurchaseChecksLevelAndBalanceOnTheServer()
    {
        $player = $this->resident();
        $this->give($player, 5000);
        T::throwsApi(ApiError::LOCKED, function () use ($player) {
            $this->app->defense()->purchase($player, 'twr_laser', $this->key());   // 레벨 4부터
        });
        $poor = $this->setLevel($this->resident(), 10);
        $gc = $this->gc($poor);
        T::throwsApi(ApiError::INSUFFICIENT_GC, function () use ($poor) {
            $this->app->defense()->purchase($poor, 'twr_laser', $this->key());     // 900 GC
        });
        T::same($gc, $this->gc($poor));
        T::eq(0, $this->db->val('SELECT COUNT(*) FROM player_defense_structures WHERE player_id = ?', [$poor['id']]));
    }

    public function testCityStructuresCannotBecomePersonalAssets()
    {
        $player = $this->setLevel($this->resident(), 50);
        $this->give($player, 100000);
        $cityOwned = $this->db->val("SELECT code FROM defense_structures WHERE ownership = 'city' AND is_coming_soon = 0 LIMIT 1");
        T::ok($cityOwned !== null, '도시 공용 시설이 정의되어 있어야 한다');
        T::throwsApi(ApiError::LOCKED, function () use ($player, $cityOwned) {
            $this->app->defense()->purchase($player, $cityOwned, $this->key());
        });
        T::eq(0, $this->db->val('SELECT COUNT(*) FROM player_defense_structures WHERE player_id = ?', [$player['id']]));
        // 개인이 세우는 타워는 GC 로 미리 강화하지 않는다 (전투 안에서 에너지로 올린다).
        T::throwsApi(ApiError::VALIDATION_FAILED, function () use ($player) {
            $this->app->defense()->upgrade($player, 'twr_machine_gun', 100, $this->key());
        });
    }

    // ── 헌트 ────────────────────────────────────────────────

    public function testHuntPaysPerAcceptedKillAndCapsTheKillRate()
    {
        $this->quiet();
        $player = $this->resident();
        $gc = $this->gc($player);
        $session = $this->app->hunt()->start($player, 'zone_ne');   // 분당 24마리, 한꺼번에 5마리까지
        Clock::advance(10);

        $result = $this->app->hunt()->report($player, $session['sessionId'], 1, [['enemyCode' => 'malfunction_robot', 'count' => 40]]);

        // 처음 허용량 5 + 10초 × 24/60 = 9. 나머지는 인정하지 않는다.
        T::eq([['enemyCode' => 'malfunction_robot', 'count' => 9]], $result['accepted']);
        T::same(90, $result['rewardGc']);
        T::same(90, $result['rewardXp']);
        T::same('kill_rate_exceeded', $result['rejectReason']);
        T::same($gc + 90, $this->gc($player));
        T::same(90, $result['dailyHuntGc']);
    }

    public function testHuntReportIsIdempotentBySequenceNumber()
    {
        $this->quiet();
        $player = $this->resident();
        $session = $this->app->hunt()->start($player, 'zone_ne');
        Clock::advance(30);
        $kills = [['enemyCode' => 'malfunction_robot', 'count' => 3]];
        $first = $this->app->hunt()->report($player, $session['sessionId'], 1, $kills);
        $gc = $this->gc($player);

        $replay = $this->app->hunt()->report($player, $session['sessionId'], 1, $kills);
        T::eq($first, $replay);
        T::same($gc, $this->gc($player), '같은 순번을 다시 보내도 두 번 받지 않는다');

        T::throwsApi(ApiError::CONFLICT, function () use ($player, $session, $kills) {
            $this->app->hunt()->report($player, $session['sessionId'], 5, $kills);
        });
        Clock::advance(30);
        $next = $this->app->hunt()->report($player, $session['sessionId'], 2, $kills);
        T::same(30, $next['rewardGc']);
    }

    public function testHuntRejectsEnemiesThatDoNotLiveInTheZone()
    {
        $player = $this->resident();
        $session = $this->app->hunt()->start($player, 'zone_ne');
        Clock::advance(60);
        $result = $this->app->hunt()->report($player, $session['sessionId'], 1, [['enemyCode' => 'construction_mecha', 'count' => 1]]);
        T::eq([], $result['accepted']);
        T::same(0, $result['rewardGc']);
        T::same('enemy_not_in_zone', $result['rejectReason']);
    }

    public function testHuntGcStopsAtTheDailyLimitAndResetsAtKoreanMidnight()
    {
        $this->quiet();
        $player = $this->resident();
        $this->app->daily()->set($player['id'], \CG\Service\Daily::HUNT_GC, 2990);
        $session = $this->app->hunt()->start($player, 'zone_ne');
        Clock::advance(60);
        $result = $this->app->hunt()->report($player, $session['sessionId'], 1, [['enemyCode' => 'malfunction_robot', 'count' => 5]]);
        T::same(10, $result['rewardGc'], '한도까지 남은 만큼만');
        T::same(50, $result['rewardXp'], '경험치는 그대로');
        T::same('daily_limit_reached', $result['rejectReason']);
        T::same(3000, $result['dailyHuntGc']);

        Clock::advance(24 * 3600);
        $session = $this->app->hunt()->start($player, 'zone_ne');
        Clock::advance(60);
        $result = $this->app->hunt()->report($player, $session['sessionId'], 1, [['enemyCode' => 'malfunction_robot', 'count' => 5]]);
        T::same(50, $result['rewardGc'], '날이 바뀌면 다시 받는다');
    }

    public function testEveryTenAcceptedHuntKillsLowerTheHomeCityRiskALittle()
    {
        $this->quiet();
        $player = $this->resident('11110');
        $risk = $this->riskCenti('11110');
        $session = $this->app->hunt()->start($player, 'zone_nw');   // 분당 50마리
        Clock::advance(60);
        $first = $this->app->hunt()->report($player, $session['sessionId'], 1, [['enemyCode' => 'mech_insect', 'count' => 9]]);
        T::same(null, $first['city'], '아직 10마리가 안 됐다');
        T::same($risk, $this->riskCenti('11110'));

        Clock::advance(60);
        $second = $this->app->hunt()->report($player, $session['sessionId'], 2, [['enemyCode' => 'mech_insect', 'count' => 12]]);
        // 9 + 12 = 21 → 두 번 반영, 한 마리는 다음으로 넘어간다.
        T::same(-200, $second['city']['riskDeltaCenti']);
        T::same('hunt_completed', $second['city']['reason']);
        T::same(2, $second['city']['contribution']);
        T::same($risk - 200, $this->riskCenti('11110'));
        T::eq(1, $this->db->val('SELECT hunt_kill_carry FROM player_combat_state WHERE player_id = ?', [$player['id']]));
    }

    public function testHuntInADangerousCityCountsAsASpecialMission()
    {
        $this->quiet();
        $player = $this->resident('11110');
        $this->setRisk('11110', 6000);   // 위험 단계
        $session = $this->app->hunt()->start($player, 'zone_nw');
        T::same('danger', $session['stage']);
        T::same(true, $session['special']);
        Clock::advance(60);
        $result = $this->app->hunt()->report($player, $session['sessionId'], 1, [['enemyCode' => 'mech_insect', 'count' => 10]]);
        T::same(-200, $result['city']['riskDeltaCenti'], '위험 단계에서는 두 배로 반영된다');
    }

    // ── FPS ─────────────────────────────────────────────────

    public function testFpsWinPaysTheDefinedRewardAndLowersCityRisk()
    {
        $this->quiet();
        $player = $this->resident('11110');
        $gc = $this->gc($player);
        $risk = $this->riskCenti('11110');

        $result = $this->winFps($player);

        T::same(true, $result['accepted']);
        // 승리 250 + 첫 승리 150 + 시민 4 × 10 + 시설 3 × 20
        T::same(500, $result['rewardGc']);
        T::same(120, $result['rewardXp']);
        T::same($gc + 500, $this->gc($player));
        T::same(80, $result['defensePoints']);
        T::eq(['downtown_1'], $result['clearedFpsMissions']);
        T::same(1, $result['fpsRewardsToday']);
        // 침입이 없을 때는 순찰: 800 × 50% = 400
        T::same($risk - 400, $this->riskCenti('11110'));
        T::same('fps_repelled', $result['city']['reason']);
        T::same(12, $result['city']['contribution']);

        $again = $this->winFps($player);
        T::same(350, $again['rewardGc'], '첫 승리 보너스는 한 번뿐');
        T::same(false, $again['firstWin']);
    }

    public function testFpsReportsAreValidatedAgainstServerTimeAndMissionDefinition()
    {
        $this->quiet();
        $player = $this->resident();
        $tooFast = $this->winFps($player, 10);
        T::same(false, $tooFast['accepted']);
        T::same('finished_too_fast', $tooFast['rejectReason']);
        T::same(0, $tooFast['rewardGc']);

        $tooLate = $this->winFps($player, 400);
        T::same(false, $tooLate['accepted']);
        T::same('time_limit_exceeded', $tooLate['rejectReason']);

        $session = $this->app->fps()->start($player, 'downtown_1');
        Clock::advance(90);
        $report = self::fpsWinReport();
        $report['civiliansSaved'] = 99;
        $tooMany = $this->app->fps()->finish($player, $session['sessionId'], $report);
        T::same(false, $tooMany['accepted']);
        T::same('invalid_civilians', $tooMany['rejectReason']);
        T::eq(0, $this->db->val("SELECT COUNT(*) FROM ledger_entries WHERE player_id = ? AND reason = 'fps_reward'", [$player['id']]));
    }

    public function testFpsGcRewardHasADailyCountLimitButTheMissionStaysPlayable()
    {
        $this->quiet();
        $player = $this->resident();
        for ($i = 0; $i < 5; $i++) {
            $result = $this->winFps($player);
            T::ok($result['rewardGc'] > 0);
            T::same(false, $result['rewardLimited']);
        }
        $gc = $this->gc($player);
        $level = $this->fresh($player);

        $sixth = $this->winFps($player);

        T::same(true, $sixth['accepted']);
        T::same(0, $sixth['rewardGc']);
        T::same(true, $sixth['rewardLimited']);
        T::same(120, $sixth['rewardXp'], '경험치는 그대로 받는다');
        T::same($gc, $this->gc($player));
        T::ok($sixth['level'] > $level['level'] || $sixth['xp'] > $level['xp']);
        T::same(5, $sixth['fpsRewardsToday']);
    }

    public function testFailingAnFpsMissionGivesNoRewardAndDoesNotRaiseRisk()
    {
        $this->quiet();
        $player = $this->resident();
        $gc = $this->gc($player);
        $risk = $this->riskCenti('11110');
        $session = $this->app->fps()->start($player, 'downtown_1');
        Clock::advance(100);
        $result = $this->app->fps()->finish($player, $session['sessionId'],
            ['result' => 'lose', 'kills' => [], 'facilitiesSaved' => 0, 'civiliansSaved' => 0]);
        T::same(true, $result['accepted']);
        T::same(0, $result['rewardGc']);
        T::same($gc, $this->gc($player));
        T::same($risk, $this->riskCenti('11110'));
    }

    // ── 공통: 세 모드가 같은 계정 정보를 쓴다 ───────────────

    public function testAllThreeModesShareOneWalletOneLevelAndOneCity()
    {
        $this->quiet();
        $player = $this->resident('11110');
        $start = $this->gc($player);

        $defense = $this->winDefense($player);
        $fps = $this->winFps($player);
        $session = $this->app->hunt()->start($player, 'zone_ne');
        Clock::advance(60);
        $hunt = $this->app->hunt()->report($player, $session['sessionId'], 1, [['enemyCode' => 'malfunction_robot', 'count' => 5]]);

        // GC: 지갑 하나에 세 모드의 보상이 모두 들어온다.
        T::same($start + $defense['rewardGc'] + $fps['rewardGc'] + $hunt['rewardGc'], $this->gc($player));
        T::eq(1, $this->db->val('SELECT COUNT(*) FROM wallets WHERE player_id = ?', [$player['id']]));
        T::eq(['battle_reward', 'fps_reward', 'hunt_reward'], array_column($this->db->all(
            "SELECT DISTINCT reason FROM ledger_entries WHERE player_id = ? AND reason LIKE '%_reward' ORDER BY reason", [$player['id']]), 'reason'));

        // 레벨·경험치: 캐릭터 하나에 쌓인다. 모드가 바뀌어도 이어진다.
        T::eq(1, $this->db->val('SELECT COUNT(*) FROM characters WHERE player_id = ?', [$player['id']]));
        $me = $this->app->players()->me($player);
        T::same($hunt['level'], $me['level']);
        T::same($hunt['xp'], $me['xp']);
        list($level, $xp) = \CG\Domain\Progression::addXp(1, 0, 150 + 120 + 50, $this->app->defs()->progression());
        T::same($level, $me['level']);
        T::same($xp, $me['xp']);

        // 소속 도시와 도시 기여도도 하나다.
        T::same('11110', $me['regionCode']);
        T::same(10 + 12, $me['cityContribution']);
        T::eq(['outskirts_1'], $me['clearedStages']);
        T::eq(['downtown_1'], $me['clearedFpsMissions']);
        T::same(null, $this->app->wallets()->findInconsistency($player['id']));
    }

    public function testAClosedModeCannotBeStarted()
    {
        $player = $this->resident();
        $this->db->exec("UPDATE combat_modes SET is_closed = 1 WHERE code = 'fps'");
        $this->app->defs()->forget();
        T::throwsApi(ApiError::LOCKED, function () use ($player) {
            $this->app->fps()->start($player, 'downtown_1');
        });
        // 다른 모드는 그대로 열려 있다.
        T::ok($this->app->defense()->start($player, 'outskirts_1')['sessionId'] !== '');
        T::ok($this->app->combat()->start($player, 'hunt', 'zone_ne')['sessionId'] !== '');
    }

    public function testTheCommonEntryPointDispatchesByMode()
    {
        $this->quiet();
        $player = $this->resident();
        $defense = $this->app->combat()->start($player, 'defense', 'outskirts_1');
        T::same('defense', $defense['mode']);
        Clock::advance(60);
        $result = $this->app->combat()->result($player, $defense['sessionId'], self::defenseWinReport());
        T::same('defense', $result['mode']);
        T::same(true, $result['accepted']);

        $fps = $this->app->combat()->start($player, 'fps', 'downtown_1');
        Clock::advance(70);
        $result = $this->app->combat()->result($player, $fps['sessionId'], self::fpsWinReport());
        T::same('fps', $result['mode']);
        T::same(true, $result['accepted']);

        T::throwsApi(ApiError::NOT_FOUND, function () use ($player) {
            $this->app->combat()->start($player, 'pvp', 'arena');
        });
    }

    // ── 전투 장비 ───────────────────────────────────────────

    public function testFreeBasicGearLetsANewPlayerJoinEveryMode()
    {
        $player = $this->guest();
        T::same(0, $this->gc($player));
        $modes = $this->app->combat()->modes($player)['modes'];
        T::eq(3, count($modes));
        $fps = null;
        foreach ($modes as $mode) {
            if ($mode['code'] === 'fps') {
                $fps = $mode;
            }
        }
        $primary = null;
        foreach ($fps['equipped'] as $slot) {
            if ($slot['slot'] === 'primary') {
                $primary = $slot;
            }
        }
        T::same('gear_energy_blaster', $primary['gearCode'], '사지 않아도 기본 무기를 낀 상태다');
        T::same(1, $primary['level']);
        // GC 가 0이어도 세 모드를 모두 시작할 수 있다.
        T::ok(strlen($this->app->defense()->start($player, 'outskirts_1')['sessionId']) === 16);
        T::ok(strlen($this->app->hunt()->start($player, 'home_street')['sessionId']) === 16);
        T::ok(strlen($this->app->fps()->start($player, 'downtown_1')['sessionId']) === 16);
    }

    public function testBuyingGearIsCheckedAndChargedByTheServer()
    {
        $player = $this->resident();
        $this->give($player, 5000);
        T::throwsApi(ApiError::LOCKED, function () use ($player) {
            $this->app->combat()->buy($player, 'gear_precision_rifle', $this->key());   // 레벨 4부터
        });
        T::throwsApi(ApiError::CONFLICT, function () use ($player) {
            $this->app->combat()->buy($player, 'gear_energy_blaster', $this->key());    // 무료 기본 장비는 이미 가졌다
        });
        T::throwsApi(ApiError::NOT_FOUND, function () use ($player) {
            $this->app->combat()->buy($player, 'gear_nope', $this->key());
        });

        $player = $this->setLevel($player, 4);
        $gc = $this->gc($player);
        $key = $this->key();
        $result = $this->app->combat()->buy($player, 'gear_precision_rifle', $key);
        T::same(600, $result['costGc']);
        T::same($gc - 600, $result['gc']);
        T::same(1, $result['level']);
        T::same(false, $result['duplicate']);

        $again = $this->app->combat()->buy($player, 'gear_precision_rifle', $key);
        T::same(true, $again['duplicate']);
        T::same($gc - 600, $this->gc($player));
        T::eq(1, $this->db->val("SELECT COUNT(*) FROM ledger_entries WHERE player_id = ? AND reason = 'gear_purchase'", [$player['id']]));

        // 같은 요청 번호를 다른 일에 쓰면 거절한다.
        T::throwsApi(ApiError::IDEMPOTENCY_MISMATCH, function () use ($player, $key) {
            $this->app->combat()->buy($player, 'gear_shockwave_launcher', $key);
        });
        T::throwsApi(ApiError::INVALID_REQUEST, function () use ($player) {
            $this->app->combat()->buy($player, 'gear_shockwave_launcher', 'x');
        });
    }

    public function testAFailedPurchaseChangesNothing()
    {
        $player = $this->setLevel($this->resident(), 10);
        $gc = $this->gc($player);   // 정착 지원금 500 뿐이다
        $key = $this->key();
        T::throwsApi(ApiError::INSUFFICIENT_GC, function () use ($player, $key) {
            $this->app->combat()->buy($player, 'gear_precision_rifle', $key);
        });
        T::same($gc, $this->gc($player));
        T::eq(0, $this->db->val('SELECT COUNT(*) FROM player_combat_equipment WHERE player_id = ?', [$player['id']]));
        T::eq(0, $this->db->val('SELECT COUNT(*) FROM idempotency_keys WHERE player_id = ?', [$player['id']]), '실패한 요청은 기록하지 않는다');
        // 같은 요청 번호로 GC 를 모은 뒤 다시 시도할 수 있다.
        $this->give($player, 1000);
        T::same(600, $this->app->combat()->buy($player, 'gear_precision_rifle', $key)['costGc']);
    }

    public function testUpgradingGearCostsMorePerLevelAndStopsAtTheMaximum()
    {
        $player = $this->resident();
        $this->give($player, 10000);
        $gc = $this->gc($player);
        // 무료 기본 장비도 강화할 수 있다: 150 × 지금 단계
        $first = $this->app->combat()->upgrade($player, 'gear_energy_blaster', $this->key());
        T::same(150, $first['costGc']);
        T::same(2, $first['level']);
        $second = $this->app->combat()->upgrade($player, 'gear_energy_blaster', $this->key());
        T::same(300, $second['costGc']);
        T::same(3, $second['level']);
        T::same($gc - 450, $this->gc($player));
        T::throwsApi(ApiError::CONFLICT, function () use ($player) {
            $this->app->combat()->upgrade($player, 'gear_energy_blaster', $this->key());
        });
        T::eq(2, $this->db->val('SELECT COUNT(*) FROM equipment_upgrades WHERE player_id = ?', [$player['id']]));
        T::throwsApi(ApiError::VALIDATION_FAILED, function () use ($player) {
            $this->app->combat()->upgrade($player, 'gear_precision_rifle', $this->key());   // 가지고 있지 않다
        });
    }

    public function testGearIsOwnedOnceAndEquippedPerMode()
    {
        $player = $this->setLevel($this->resident(), 10);
        $this->give($player, 5000);
        $this->app->combat()->buy($player, 'gear_precision_rifle', $this->key());

        $result = $this->app->combat()->equip($player, 'fps', 'primary', 'gear_precision_rifle');
        T::eq([['mode' => 'fps', 'slot' => 'primary', 'gearCode' => 'gear_precision_rifle']], $result['loadouts']);
        // 가진 장비는 한 점이다. 장착은 "어느 모드의 어느 칸"만 적는다.
        T::eq(1, $this->db->val('SELECT COUNT(*) FROM player_combat_equipment WHERE player_id = ?', [$player['id']]));

        T::throwsApi(ApiError::VALIDATION_FAILED, function () use ($player) {
            $this->app->combat()->equip($player, 'fps', 'gadget', 'gear_precision_rifle');   // 칸이 다르다
        });
        T::throwsApi(ApiError::VALIDATION_FAILED, function () use ($player) {
            $this->app->combat()->equip($player, 'defense', 'primary', 'gear_precision_rifle');   // 그 모드에서는 쓸 수 없다
        });
        T::throwsApi(ApiError::VALIDATION_FAILED, function () use ($player) {
            $this->app->combat()->equip($player, 'fps', 'primary', 'gear_shockwave_launcher');   // 가지고 있지 않다
        });
        T::throwsApi(ApiError::INVALID_REQUEST, function () use ($player) {
            $this->app->combat()->equip($player, 'pvp', 'primary', 'gear_precision_rifle');
        });

        // 칸을 비우면 다시 무료 기본 장비를 쓴다.
        $cleared = $this->app->combat()->equip($player, 'fps', 'primary', null);
        T::eq([], $cleared['loadouts']);
    }

    public function testExpensiveGearDoesNotChangeRewardsOrCityContribution()
    {
        $this->quiet();
        $rich = $this->setLevel($this->resident('11110'), 10);
        $this->give($rich, 50000);
        foreach (['gear_precision_rifle', 'gear_shockwave_launcher'] as $code) {
            $this->app->combat()->buy($rich, $code, $this->key());
            $this->app->combat()->upgrade($rich, $code, $this->key());
            $this->app->combat()->upgrade($rich, $code, $this->key());
        }
        $this->app->combat()->equip($rich, 'fps', 'primary', 'gear_precision_rifle');
        $plain = $this->resident('11140');

        $a = $this->winFps($rich);
        $b = $this->winFps($plain);

        // 보상과 도시 기여는 임무의 결과로만 정해진다. 가진 장비의 값은 들어가지 않는다.
        T::same($b['rewardGc'], $a['rewardGc']);
        T::same($b['rewardXp'], $a['rewardXp']);
        T::same($b['city']['contribution'], $a['city']['contribution']);
        T::same($b['city']['riskDeltaCenti'], $a['city']['riskDeltaCenti']);
    }

    public function testRewardSummaryListsAllModesFromOneLedger()
    {
        $this->quiet();
        $player = $this->resident();
        $this->winDefense($player);
        $this->winFps($player);
        $rewards = $this->app->combat()->rewards($player);
        T::same($this->gc($player), $rewards['gc']);
        $byMode = array_column($rewards['totals'], 'totalGc', 'mode');
        T::same(500, $byMode['defense']);
        T::same(500, $byMode['fps']);
        T::same(0, $byMode['hunt']);
        T::eq(2, count($rewards['recent']));
        T::same(1, $rewards['fpsRewardsToday']);
    }
}
