<?php
use CG\Admin\Auth;
use CG\Infra\ApiError;
use CG\Infra\Clock;
use CG\Service\Modeling;
use CG\Service\Settings;
use CG\Service\Wallets;

/**
 * v9.0 도시 성장: 유효 전입자 집계, 이사, 확장 기준과 알림, 모델링 제작·검수·배포.
 * (지시서 11장의 테스트 항목: 전입자 중복 집계 방지, 도시 이전 시 인원 갱신, 기준 도달 알림 중복 방지, 모델링 상태 변경)
 */
final class GrowthTest extends CityTestCase
{
    private function milestone($code, $threshold)
    {
        $this->db->exec('UPDATE city_growth_milestones SET threshold = ? WHERE code = ?', [$threshold, $code]);
    }

    /** 기준을 작게 줄여 둔다: 2명(상징물 검토), 3명(상징거리 후보), 4명(상징거리 개방), 6명(특화 구역). */
    private function smallMilestones()
    {
        $this->milestone('landmark_review', 2);
        $this->milestone('street_candidate', 3);
        $this->milestone('street_open', 4);
        $this->milestone('special_zone_review', 6);
    }

    // ── 전입자 집계 ─────────────────────────────────────────

    public function testGuestAccountsDoNotIncreaseTheValidResidentCount()
    {
        // 게스트 계정을 여러 개 만들어 전입해도 유효 전입자는 늘지 않는다.
        for ($i = 0; $i < 5; $i++) {
            $this->resident('11110');
        }
        $stats = $this->stats('11110');
        T::eq(0, (int)$stats['valid_residents']);
        T::eq(5, (int)$stats['registered_residents']);
        T::eq(0, (int)$stats['cumulative_residents']);
        T::eq(0, (int)$stats['stage']);
        // 기존의 "전입신고를 한 계정 수"는 그대로 센다 (v8.0 과 같다).
        T::eq(5, (int)$this->db->val("SELECT resident_count FROM regions WHERE code = '11110'"));
        T::eq(0, (int)$this->db->val('SELECT COUNT(*) FROM city_growth_notifications'));

        // 연결된 계정만 유효 전입자다.
        $linked = $this->linkedResident('11110');
        $stats = $this->stats('11110');
        T::eq(1, (int)$stats['valid_residents']);
        T::eq(6, (int)$stats['registered_residents']);
        T::eq(1, (int)$stats['cumulative_residents']);

        $mine = $this->app->growth()->residency($linked);
        T::eq(true, $mine['counted']);
        T::eq('linked', $mine['countRule']);
        $guest = $this->app->growth()->residency($this->resident('11110'));
        T::eq(false, $guest['counted']);
        T::eq('guest_account', $guest['notCountedReason']);
        T::eq(null, $this->app->growth()->findInconsistency());
    }

    public function testProvenGuestsCountOnlyWhenTheAdminAllowsItAndTheyMeetEveryCondition()
    {
        $new = $this->resident('11110');
        $veteran = $this->resident('11110');
        $this->setLevel($veteran, 5);
        // 사흘 동안 플레이한 기록
        foreach (['2026-11-10', '2026-11-11', '2026-11-12'] as $date) {
            $this->db->exec('INSERT INTO daily_counters (player_id, date, counter, value) VALUES (?, ?, ?, 1)', [$veteran['id'], $date, 'hunt_gc']);
        }
        $this->db->exec('UPDATE players SET created_at = ? WHERE id = ?', [Clock::sql(Clock::now() - 10 * 86400), $veteran['id']]);

        $this->app->growth()->refreshRegion($this->regionId('11110'));
        T::eq(0, (int)$this->stats('11110')['valid_residents'], '기본 기준에서는 게스트를 세지 않는다');

        $this->setting(Settings::GROWTH, ['populationMode' => Settings::MODE_LINKED_OR_PROVEN]);
        $this->app->growth()->refreshRegion($this->regionId('11110'));
        T::eq(1, (int)$this->stats('11110')['valid_residents'], '조건을 모두 채운 게스트만 센다');
        T::eq(true, $this->app->growth()->isCounted($veteran['id']));
        T::eq(false, $this->app->growth()->isCounted($new['id']));
        T::eq('not_proven', $this->app->growth()->residency($new)['notCountedReason']);

        // 레벨만 높고 플레이한 날이 모자라면 세지 않는다 (계정을 찍어 내는 것으로는 채울 수 없다).
        $this->setLevel($new, 9);
        $this->app->growth()->refreshRegion($this->regionId('11110'));
        T::eq(1, (int)$this->stats('11110')['valid_residents']);
    }

    public function testOneAccountHoldsOneCityAndCannotRegisterTwice()
    {
        $player = $this->linkedResident('11110');
        T::throwsApi(ApiError::CONFLICT, function () use ($player) {
            $this->app->players()->registerResidency($player, '26110', '다른이름');
        });
        T::eq(1, (int)$this->stats('11110')['valid_residents']);
        T::eq(null, $this->stats('26110'));
        T::eq(1, (int)$this->db->val("SELECT COUNT(*) FROM city_residency_events WHERE player_id = ? AND kind = 'register'", [$player['id']]));
    }

    public function testSuspendedAccountsAreNotCounted()
    {
        $player = $this->linkedResident('11110');
        $this->linkedResident('11110');
        T::eq(2, (int)$this->stats('11110')['valid_residents']);
        $this->db->exec("UPDATE players SET status = 'suspended' WHERE id = ?", [$player['id']]);
        $result = $this->app->growth()->refreshAll();
        T::eq(1, $result['refreshed'] >= 1 ? 1 : 0);
        $stats = $this->stats('11110');
        T::eq(1, (int)$stats['valid_residents']);
        T::eq(2, (int)$stats['cumulative_residents'], '누적 전입자는 줄지 않는다');
    }

    // ── 이사 ────────────────────────────────────────────────

    public function testMovingUpdatesBothCitiesExactly()
    {
        $this->quiet();
        $mover = $this->linkedResident('11110');
        $this->linkedResident('11110');
        $this->linkedResident('26110');
        $this->give($mover, 5000);
        $before = $this->gc($mover);

        // 전입한 지 얼마 되지 않으면 이사할 수 없다.
        $error = T::throwsApi(ApiError::LOCKED, function () use ($mover) {
            $this->app->growth()->move($mover, '26110', $this->key());
        });
        T::eq('move_cooldown', $error->reason);
        Clock::advance(8 * 86400);

        $key = $this->key();
        $result = $this->app->growth()->move($mover, '26110', $key);
        T::eq('26110', $result['regionCode']);
        T::eq('11110', $result['previousRegionCode']);
        T::eq(1000, $result['feeGc']);
        T::eq($before - 1000, $this->gc($mover));

        T::eq(1, (int)$this->stats('11110')['valid_residents']);
        T::eq(2, (int)$this->stats('26110')['valid_residents']);
        T::eq(2, (int)$this->stats('11110')['cumulative_residents'], '떠난 도시의 누적 전입자는 그대로다');
        T::eq(1, (int)$this->db->val("SELECT resident_count FROM regions WHERE code = '11110'"));
        T::eq(2, (int)$this->db->val("SELECT resident_count FROM regions WHERE code = '26110'"));
        T::eq('26110', $this->fresh($mover)['regionCode']);
        T::eq(null, $this->app->growth()->findInconsistency());

        // 같은 요청이 다시 와도 한 번만 처리된다.
        $again = $this->app->growth()->move($mover, '26110', $key);
        T::eq(true, $again['duplicate']);
        T::eq($before - 1000, $this->gc($mover));
        T::eq(2, (int)$this->stats('26110')['valid_residents']);
        T::eq(1, (int)$this->db->val("SELECT COUNT(*) FROM ledger_entries WHERE player_id = ? AND reason = ?", [$mover['id'], Wallets::RESIDENCY_MOVE]));
        T::eq(null, $this->app->wallets()->findInconsistency($mover['id']));

        // 이사한 직후에는 다시 이사할 수 없다 (도시를 오가며 수를 흔들 수 없다).
        T::throwsApi(ApiError::LOCKED, function () use ($mover) {
            $this->app->growth()->move($mover, '11110', $this->key());
        });
        // 같은 도시로는 이사할 수 없다.
        Clock::advance(8 * 86400);
        T::throwsApi(ApiError::CONFLICT, function () use ($mover) {
            $this->app->growth()->move($mover, '26110', $this->key());
        });
        $events = array_column($this->db->all('SELECT kind FROM city_residency_events WHERE player_id = ? ORDER BY id', [$mover['id']]), 'kind');
        T::eq(['register', 'move_out', 'move_in'], $events);
    }

    public function testMoveNeedsTheFeeAndCanBeDisabled()
    {
        $this->quiet();
        $player = $this->linkedResident('11110');
        Clock::advance(8 * 86400);
        // 정착 지원금 500 GC 로는 수수료 1,000 GC 를 낼 수 없다.
        T::throwsApi(ApiError::INSUFFICIENT_GC, function () use ($player) {
            $this->app->growth()->move($player, '26110', $this->key());
        });
        T::eq('11110', $this->fresh($player)['regionCode']);
        T::eq(1, (int)$this->stats('11110')['valid_residents']);

        $this->setting(Settings::GROWTH, ['moveEnabled' => 0]);
        $this->give($player, 5000);
        $error = T::throwsApi(ApiError::LOCKED, function () use ($player) {
            $this->app->growth()->move($player, '26110', $this->key());
        });
        T::eq('move_disabled', $error->reason);

        // 이방인은 이사할 도시가 없다.
        $this->setting(Settings::GROWTH, ['moveEnabled' => 1]);
        $stranger = $this->guest();
        T::throwsApi(ApiError::CONFLICT, function () use ($stranger) {
            $this->app->growth()->move($stranger, '26110', $this->key());
        });
    }

    // ── 확장 기준과 알림 ────────────────────────────────────

    public function testReachingAMilestoneCreatesExactlyOneNotification()
    {
        $this->smallMilestones();
        $this->linkedResident('11110');
        T::eq(0, (int)$this->db->val('SELECT COUNT(*) FROM city_growth_notifications'));
        $this->linkedResident('11110');
        T::eq(1, (int)$this->db->val("SELECT COUNT(*) FROM city_growth_notifications WHERE kind = 'milestone'"));
        T::eq(1, (int)$this->stats('11110')['stage']);

        // 다시 세어도, 예약 작업이 돌아도, 같은 기준의 알림은 하나뿐이다.
        $this->app->growth()->refreshRegion($this->regionId('11110'));
        $this->app->growth()->refreshAll();
        \CG\Service\CityOpsTick::run($this->app);
        T::eq(1, (int)$this->db->val('SELECT COUNT(*) FROM city_growth_notifications'));

        // 주민이 줄었다가 다시 늘어도 같은 기준의 알림은 다시 생기지 않는다. 단계도 내려가지 않는다.
        $leaver = $this->linkedResident('11110');
        T::eq(2, (int)$this->db->val('SELECT COUNT(*) FROM city_growth_notifications'), '3명: 상징거리 후보');
        $this->db->exec("UPDATE players SET status = 'suspended' WHERE id = ?", [$leaver['id']]);
        $this->app->growth()->refreshRegion($this->regionId('11110'));
        T::eq(2, (int)$this->stats('11110')['valid_residents']);
        T::eq(2, (int)$this->stats('11110')['stage']);
        $this->db->exec("UPDATE players SET status = 'active' WHERE id = ?", [$leaver['id']]);
        $this->app->growth()->refreshRegion($this->regionId('11110'));
        T::eq(2, (int)$this->db->val('SELECT COUNT(*) FROM city_growth_notifications'));

        // 상징거리 기준(4명)에 닿으면 기준 달성으로 표시된다.
        T::eq(0, (int)$this->stats('11110')['threshold_reached']);
        $this->linkedResident('11110');
        $stats = $this->stats('11110');
        T::eq(1, (int)$stats['threshold_reached']);
        T::ok($stats['threshold_reached_at'] !== null);
        T::eq(3, (int)$stats['stage']);
        T::eq(3, (int)$this->db->val('SELECT COUNT(*) FROM city_growth_notifications'));
        // 다른 도시의 알림은 따로 생긴다.
        $this->linkedResident('26110');
        $this->linkedResident('26110');
        T::eq(4, (int)$this->db->val('SELECT COUNT(*) FROM city_growth_notifications'));
    }

    public function testAdminCanChangeMilestonesAndPerCityThresholdsWithHistory()
    {
        $this->linkedResident('11110');
        $this->linkedResident('11110');
        $this->linkedResident('11110');
        $growth = $this->app->growth();
        T::eq(0, (int)$this->stats('11110')['stage']);

        // 조회 전용 관리자와 운영자는 기준을 고칠 수 없다 (밸런스 담당과 최고 관리자만).
        $form = ['threshold' => '3', 'name' => '상징물 제작 검토', 'description' => '설명', 'is_active' => '1', 'reason' => '시범 운영에 맞게 낮춤'];
        T::eq(['권한이 없습니다.'], $growth->saveMilestone($this->operator(), 'landmark_review', $form));
        // 범위와 사유를 확인한다.
        T::ok(count($growth->saveMilestone($this->balancer(), 'landmark_review', ['threshold' => '0'] + $form)) > 0);
        T::ok(count($growth->saveMilestone($this->balancer(), 'landmark_review', ['reason' => ''] + $form)) > 0);
        T::eq(100, (int)$this->db->val("SELECT threshold FROM city_growth_milestones WHERE code = 'landmark_review'"));

        T::eq([], $growth->saveMilestone($this->balancer(), 'landmark_review', $form, '10.0.0.1'));
        T::eq(3, (int)$this->db->val("SELECT threshold FROM city_growth_milestones WHERE code = 'landmark_review'"));
        // 기준이 낮아지자 이미 그 인원을 넘은 도시에 알림이 생긴다.
        T::eq(1, (int)$this->db->val('SELECT COUNT(*) FROM city_growth_notifications'));
        T::eq(1, (int)$this->stats('11110')['stage']);
        T::eq(1, (int)$this->db->val("SELECT COUNT(*) FROM config_history WHERE target_type = 'city_growth_milestones'"));
        T::eq(1, (int)$this->db->val("SELECT COUNT(*) FROM admin_audit_logs WHERE action = 'growth.milestone.update'"));

        // 한 도시의 상징거리 기준 인원만 따로 정할 수 있다.
        T::eq(0, (int)$this->stats('11110')['threshold_reached']);
        T::eq([], $growth->setCityThreshold($this->balancer(), '11110', '3', '작은 도시라 기준을 낮춤'));
        T::eq(1, (int)$this->stats('11110')['threshold_reached']);
        $city = $growth->city('11110');
        T::eq(3, $city['streetThreshold']);
        T::eq(true, $city['thresholdReached']);
        T::eq(1, (int)$this->db->val("SELECT COUNT(*) FROM admin_audit_logs WHERE action = 'growth.threshold.update'"));
    }

    public function testNotificationIsAcknowledgedThenAssignedAndKindsAreSeparate()
    {
        $this->smallMilestones();
        $this->linkedResident('11110');
        $this->linkedResident('11110');
        $growth = $this->app->growth();
        $notice = $growth->notifications('milestone', 'new')[0];
        $operator = $this->operator();
        $maker = $this->contentMaker();

        // 권한: 제작 담당은 알림을 처리하지 못한다.
        T::eq(['권한이 없습니다.'], $growth->handleNotification($maker, $notice['id'], 'acknowledge'));
        // 확인하기 전에는 담당자를 배정할 수 없고, 제작 작업도 등록할 수 없다.
        T::eq(['먼저 알림을 확인해 주세요.'], $growth->handleNotification($operator, $notice['id'], 'assign', $maker['id']));
        list($id, $problems) = $this->app->modeling()->createProject($operator,
            ['region_code' => '11110', 'kind' => 'landmark', 'title' => '종로 상징물', 'notification_id' => (string)$notice['id']]);
        T::eq(null, $id);
        T::eq(['먼저 알림을 확인해 주세요.'], $problems);

        T::eq([], $growth->handleNotification($operator, $notice['id'], 'acknowledge'));
        T::eq(['이미 확인한 알림입니다.'], $growth->handleNotification($operator, $notice['id'], 'acknowledge'));
        T::eq([], $growth->handleNotification($operator, $notice['id'], 'assign', $maker['id']));
        $row = $growth->notification($notice['id']);
        T::eq('assigned', $row['status']);
        T::eq((int)$maker['id'], (int)$row['assigned_admin_id']);
        T::eq((int)$operator['id'], (int)$row['acknowledged_by']);

        // 확인한 알림에서 제작 작업을 등록한다. 모델링 진행 알림은 기준 도달 알림과 종류가 다르다.
        list($id, $problems) = $this->app->modeling()->createProject($operator,
            ['region_code' => '11110', 'kind' => 'landmark', 'title' => '종로 상징물', 'notification_id' => (string)$notice['id'],
                'assignee_admin_id' => (string)$maker['id'], 'due_on' => '2026-12-31', 'memo' => '보신각을 본뜬 상징물']);
        T::eq([], $problems);
        T::ok($id > 0);
        T::eq(1, count($growth->notifications('milestone')));
        T::eq(1, count($growth->notifications('modeling')));
        T::eq((int)$notice['id'], (int)$this->app->modeling()->project($id)['notification_id']);
        T::ok((int)$this->db->val("SELECT COUNT(*) FROM admin_audit_logs WHERE action LIKE 'growth.notification.%'") >= 2);
    }

    // ── 모델링 제작·검수·배포 ───────────────────────────────

    private function project($kind = 'street')
    {
        list($id, $problems) = $this->app->modeling()->createProject($this->operator(),
            ['region_code' => '11110', 'kind' => $kind, 'title' => '종로 상징거리', 'assignee_admin_id' => (string)$this->contentMaker()['id']]);
        T::eq([], $problems);
        return $id;
    }

    private static function assetForm($version = '1.0.0')
    {
        return ['version' => $version, 'address_key' => 'city/11110/street', 'catalog_path' => 'city-11110/' . $version . '/catalog.json',
            'content_hash' => str_repeat('ab', 32), 'size_bytes' => '1048576', 'min_client_version' => '9.0.0', 'note' => '첫 판'];
    }

    public function testModelingStatusMovesOnlyAlongTheAllowedPath()
    {
        $modeling = $this->app->modeling();
        $maker = $this->contentMaker();
        $operator = $this->operator();
        $id = $this->project();
        T::eq(Modeling::NOT_STARTED, $modeling->project($id)['status']);
        T::eq(Modeling::NOT_STARTED, $this->stats('11110')['modeling_status']);

        // 건너뛸 수 없다.
        T::ok(count($modeling->transition($maker, $id, Modeling::MODELING, '바로 제작')) > 0);
        T::eq(['배포 준비 상태는 검수를 통과해야 됩니다.'], $modeling->transition($maker, $id, Modeling::READY, '검수 없이'));
        T::eq(['적용 완료 상태는 결과물을 배포해야 됩니다.'], $modeling->transition($maker, $id, Modeling::DEPLOYED, '배포 없이'));
        // 사유가 있어야 한다.
        T::ok(count($modeling->transition($maker, $id, Modeling::PLANNING, '')) > 0);
        // 조회 전용 관리자는 바꿀 수 없다.
        T::eq(['권한이 없습니다.'], $modeling->transition($this->admin('viewer', Auth::ROLE_VIEWER), $id, Modeling::PLANNING, '기획 시작'));

        T::eq([], $modeling->transition($maker, $id, Modeling::PLANNING, '기획 시작'));
        T::eq([], $modeling->transition($maker, $id, Modeling::MODELING, '제작 시작'));
        T::eq(Modeling::MODELING, $this->stats('11110')['modeling_status']);
        // 파일 버전이 없으면 검수에 넘길 수 없다.
        T::eq(['검수에 넘기려면 파일 버전을 먼저 적어 주세요.'], $modeling->transition($maker, $id, Modeling::REVIEW, '검수 요청'));
        T::eq([], $modeling->addAsset($maker, $id, self::assetForm()));
        T::eq('1.0.0', $modeling->project($id)['file_version']);
        T::eq([], $modeling->transition($maker, $id, Modeling::REVIEW, '검수 요청'));

        // 담당자 본인은 검수할 수 없다. 제작 담당 역할에는 검수 권한이 없다.
        T::eq(['권한이 없습니다.'], $modeling->review($maker, $id, 'pass', '좋습니다'));
        T::eq([], $modeling->updateProject($operator, $id, ['title' => '종로 상징거리', 'file_version' => '1.0.0', 'assignee_admin_id' => (string)$operator['id']]));
        T::eq(['담당자 본인은 검수할 수 없습니다.'], $modeling->review($operator, $id, 'pass', '좋습니다'));
        $reviewer = $this->super();
        // 반려하면 제작으로 돌아간다.
        T::eq([], $modeling->review($reviewer, $id, 'fail', '간판의 글자가 깨져 보입니다'));
        $project = $modeling->project($id);
        T::eq(Modeling::MODELING, $project['status']);
        T::eq('fail', $project['review_result']);
        T::eq([], $modeling->transition($maker, $id, Modeling::REVIEW, '고쳐서 다시 요청'));
        T::eq('none', $modeling->project($id)['review_result'], '다시 넘기면 이전 검수 결과는 지워진다');
        T::eq([], $modeling->review($reviewer, $id, 'pass', '확인했습니다'));
        $project = $modeling->project($id);
        T::eq(Modeling::READY, $project['status']);
        T::eq('pass', $project['review_result']);
        T::eq((int)$reviewer['id'], (int)$project['reviewed_by']);
        T::eq(Modeling::READY, $this->stats('11110')['modeling_status']);

        // 상태가 바뀔 때마다 모델링 진행 알림과 감사 기록이 남는다.
        T::ok(count($this->app->growth()->notifications('modeling')) >= 7);
        T::eq(0, count($this->app->growth()->notifications('milestone')));
        T::ok((int)$this->db->val("SELECT COUNT(*) FROM admin_audit_logs WHERE action = 'modeling.project.status'") >= 4);
        T::eq(2, (int)$this->db->val("SELECT COUNT(*) FROM admin_audit_logs WHERE action = 'modeling.project.review'"));
    }

    /** 검수를 통과한 작업과 승인된 결과물을 만든다. @return array [작업 id, 결과물 id] */
    private function readyAsset()
    {
        $modeling = $this->app->modeling();
        $maker = $this->contentMaker();
        $id = $this->project();
        T::eq([], $modeling->transition($maker, $id, Modeling::PLANNING, '기획 시작'));
        T::eq([], $modeling->transition($maker, $id, Modeling::MODELING, '제작 시작'));
        T::eq([], $modeling->addAsset($maker, $id, self::assetForm()));
        T::eq([], $modeling->transition($maker, $id, Modeling::REVIEW, '검수 요청'));
        T::eq([], $modeling->review($this->super(), $id, 'pass', '확인했습니다'));
        return [$id, (int)$modeling->assets($id)[0]['id']];
    }

    public function testContentIsReleasedOnlyAfterReviewAndCanBeRolledOutGraduallyAndWithdrawn()
    {
        $modeling = $this->app->modeling();
        $maker = $this->contentMaker();
        $super = $this->super();
        $operator = $this->operator();
        $player = $this->resident('11110');
        list($projectId, $assetId) = $this->readyAsset();

        // 값 검사: 저장소 밖을 가리키는 경로, 잘못된 해시는 받지 않는다.
        T::ok(count($modeling->addAsset($maker, $projectId, ['catalog_path' => '../secret/catalog.json'] + self::assetForm('1.0.1'))) > 0);
        T::ok(count($modeling->addAsset($maker, $projectId, ['catalog_path' => 'https://evil.example/catalog.json'] + self::assetForm('1.0.1'))) > 0);
        T::ok(count($modeling->addAsset($maker, $projectId, ['content_hash' => 'zz'] + self::assetForm('1.0.1'))) > 0);
        T::eq(['이미 있는 버전입니다. 내용이 바뀌었으면 버전을 올려 주세요.'], $modeling->addAsset($maker, $projectId, self::assetForm('1.0.0')));

        // 승인되지 않은 결과물은 배포할 수 없다. 올린 사람은 자기 결과물을 승인할 수 없다.
        T::eq(['승인된 결과물만 배포할 수 있습니다.'], $modeling->release($super, $assetId, '100', '정식 개방'));
        T::eq(['권한이 없습니다.'], $modeling->reviewAsset($maker, $assetId, true, '문제 없음'));
        T::eq([], $modeling->reviewAsset($operator, $assetId, true, '해시와 크기를 확인함'));
        // 배포는 최고 관리자만 한다.
        T::eq(['권한이 없습니다.'], $modeling->release($operator, $assetId, '100', '정식 개방'));
        T::eq([], $modeling->catalog($player, '9.0.0')['items'], '배포 전에는 아무것도 내려가지 않는다');

        // 10% 에게만 먼저 내보낸다.
        T::eq([], $modeling->release($super, $assetId, '10', '일부에게 먼저 배포'));
        T::eq(Modeling::DEPLOYED, $modeling->project($projectId)['status']);
        $stats = $this->stats('11110');
        T::eq(Modeling::DEPLOYED, $stats['modeling_status']);
        T::eq('1.0.0', $stats['content_version']);
        T::eq(null, $stats['opened_at'], '일부 배포는 정식 개방이 아니다');
        $assetPublicId = $modeling->assets($projectId)[0]['public_id'];
        $inside = 0;
        for ($i = 0; $i < 400; $i++) {
            $inside += Modeling::inRollout('p_test' . $i, $assetPublicId, 10) ? 1 : 0;
        }
        T::ok($inside > 10 && $inside < 90, "10% 배포인데 400명 가운데 {$inside}명이 받음");
        // 비율을 올려도 이미 받던 사람은 계속 받는다.
        for ($i = 0; $i < 400; $i++) {
            if (Modeling::inRollout('p_test' . $i, $assetPublicId, 10)) {
                T::ok(Modeling::inRollout('p_test' . $i, $assetPublicId, 50));
            }
        }

        // 모두에게 배포하면 정식 개방 일시가 적힌다.
        T::eq([], $modeling->release($super, $assetId, '100', '정식 개방'));
        T::ok($this->stats('11110')['opened_at'] !== null);
        $catalog = $modeling->catalog($player, '9.0.0');
        T::eq(true, $catalog['available']);
        T::eq(1, count($catalog['items']));
        $item = $catalog['items'][0];
        T::eq('11110', $item['regionCode']);
        T::eq('street', $item['kind']);
        T::eq('https://cdn.cg.test/content/city-11110/1.0.0/catalog.json', $item['catalogUrl']);
        T::eq(str_repeat('ab', 32), $item['contentHash']);
        // 오래된 클라이언트에는 내려가지 않는다 (기본 거리를 쓴다).
        T::eq([], $modeling->catalog($player, '8.1.0')['items']);
        T::eq([], $modeling->catalog($player, '')['items']);
        $city = $this->app->growth()->city('11110');
        T::eq(true, $city['streetOpen']);
        T::eq('1.0.0', $city['contentVersion']);

        // 문제가 생기면 운영자가 바로 내릴 수 있다.
        T::eq([], $modeling->withdraw($operator, $assetId, '일부 기기에서 충돌이 보고됨'));
        T::eq([], $modeling->catalog($player, '9.0.0')['items']);
        T::eq(Modeling::READY, $modeling->project($projectId)['status']);
        T::eq(null, $this->stats('11110')['content_version']);
        T::eq(false, $this->app->growth()->city('11110')['streetOpen']);
        T::eq(2, (int)$this->db->val("SELECT COUNT(*) FROM admin_audit_logs WHERE action = 'modeling.asset.release'"));
        T::eq(1, (int)$this->db->val("SELECT COUNT(*) FROM admin_audit_logs WHERE action = 'modeling.asset.withdraw'"));
    }

    public function testContentCannotBeReleasedWithoutAContentStoreAddress()
    {
        list(, $assetId) = $this->readyAsset();
        T::eq([], $this->app->modeling()->reviewAsset($this->operator(), $assetId, true, '확인함'));
        // 콘텐츠 저장소 주소가 없거나 https 가 아닌 서버.
        foreach (['', 'http://cdn.cg.test/content'] as $base) {
            $env = \CG\Infra\Env::fromArray(['APP_ENV' => 'test', 'CONTENT_BASE_URL' => $base]);
            $app = new \CG\App($env, $this->db);
            $problems = $app->modeling()->release($this->super(), $assetId, '100', '정식 개방');
            T::eq(1, count($problems));
            T::ok(strpos($problems[0], 'CONTENT_BASE_URL') !== false);
            $catalog = $app->modeling()->catalog($this->resident('11110'), '9.0.0');
            T::eq(false, $catalog['available']);
            T::eq([], $catalog['items']);
        }
        T::eq('approved', $this->app->modeling()->assets()[0]['status']);
    }

    // ── 조회 API ────────────────────────────────────────────

    public function testGrowthApiShowsCountsButNoAccountDetails()
    {
        $this->smallMilestones();
        $player = $this->linkedResident('11110');
        $this->linkedResident('11110');
        $kernel = new \CG\Api\Kernel($this->app);
        $guest = $this->app->players()->createGuest('windows', '9.0.0');
        $headers = ['Authorization' => 'Bearer ' . $guest['accessToken']];

        list($status, $body) = $kernel->handle(\CG\Infra\Request::make('GET', '/cities/growth', [], $headers));
        T::eq(200, $status);
        T::eq(230, count($body['data']['cities']));
        $jongno = null;
        foreach ($body['data']['cities'] as $city) {
            if ($city['region_code'] === '11110') {
                $jongno = $city;
            }
        }
        T::eq(2, $jongno['valid_residents']);
        T::eq(1, $jongno['stage']);
        T::eq(3, $jongno['next_threshold']);
        T::eq('NOT_STARTED', $jongno['modeling_status']);
        T::eq(4, count($body['data']['milestones']));

        list($status, $body) = $kernel->handle(\CG\Infra\Request::make('GET', '/cities/11110/growth', [], $headers));
        T::eq(200, $status);
        T::eq(2, $body['data']['valid_residents']);
        // 응답에 계정을 가리키는 값이 없다.
        T::ok(strpos(json_encode($body), $player['publicId']) === false);
        list($status) = $kernel->handle(\CG\Infra\Request::make('GET', '/cities/99999/growth', [], $headers));
        T::eq(404, $status);

        list($status, $body) = $kernel->handle(\CG\Infra\Request::make('GET', '/residency', [], $headers));
        T::eq(200, $status);
        T::eq('stranger', $body['data']['residency']);
        // 접속 토큰이 없으면 볼 수 없다.
        list($status) = $kernel->handle(\CG\Infra\Request::make('GET', '/cities/growth'));
        T::eq(401, $status);
        // 기존 경로는 그대로다.
        list($status, $body) = $kernel->handle(\CG\Infra\Request::make('GET', '/cities/11110', [], $headers));
        T::eq(200, $status);
    }
}
