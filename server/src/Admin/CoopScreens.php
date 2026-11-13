<?php
namespace CG\Admin;

use CG\Infra\Request;
use CG\Service\Coop;
use CG\Service\Settings;

/**
 * 관리자 화면: 협동 디펜스 모니터링(/admin/coop), 미니맵 및 전술 핑 설정(/admin/ping).
 * (docs/COOP_DEFENSE_SYSTEM.md, docs/TACTICAL_MINIMAP.md) CityOpsPages 가 쓴다.
 *
 * 전투 중의 상태(적 위치, 타워, 관문 내구도)는 실시간 서버의 메모리에만 있다. 여기에서는 판의 상태와, 끝난 판이 넘긴
 * 기록(팀원별 기여, 공동 예산 장부, 전술 핑)을 본다.
 */
trait CoopScreens
{
    private function routeCoop($get, $path, Request $request)
    {
        $form = $request->form;
        $ip = $request->ip;
        $coop = $this->app->coop();
        if ($path === '/admin/coop') {
            return $get ? $this->coopPage([], $request) : null;
        }
        if (preg_match('#^/admin/coop/(\d{1,18})$#', $path, $m)) {
            return $get ? $this->coopDetail((int)$m[1], [], $request) : null;
        }
        if (preg_match('#^/admin/coop/(\d{1,18})/close$#', $path, $m) && !$get) {
            $problems = $coop->adminClose($this->admin, (int)$m[1], self::field($form, 'reason'), $ip);
            return $problems ? $this->coopDetail((int)$m[1], $problems, $request) : ['redirect' => '/admin/coop/' . (int)$m[1] . '?done=closed'];
        }
        if (preg_match('#^/admin/coop/roles/([a-z_]{1,24})$#', $path, $m) && !$get) {
            $problems = $coop->saveRole($this->admin, $m[1], $form, $ip);
            return $problems ? $this->coopPage($problems, $request) : ['redirect' => '/admin/coop?done=saved'];
        }
        if ($path === '/admin/ping') {
            return $get ? $this->pingPage([], $request) : null;
        }
        if ($path === '/admin/ping/block' && !$get) {
            $problems = $coop->blockPing($this->admin, self::field($form, 'player_id'), self::field($form, 'hours'), self::field($form, 'reason'), $ip);
            return $problems ? $this->pingPage($problems, $request) : ['redirect' => '/admin/ping?done=blocked'];
        }
        if (preg_match('#^/admin/ping/blocks/(\d{1,18})/lift$#', $path, $m) && !$get) {
            $problems = $coop->liftPingBlock($this->admin, (int)$m[1], self::field($form, 'reason'), $ip);
            return $problems ? $this->pingPage($problems, $request) : ['redirect' => '/admin/ping?done=lifted'];
        }
        return null;
    }

    private static function unavailableText($reason)
    {
        $texts = [
            'coop_disabled' => '협동 방어전이 꺼져 있습니다 (아래 설정의 "협동 방어전 열기").',
            'realtime_not_configured' => '실시간 서버가 설정되어 있지 않습니다. 서버의 .env 에 REALTIME_SECRET (32자 이상) 과 REALTIME_URL (wss://…) 이 있어야 합니다.',
            'mode_closed' => '타워디펜스 모드가 닫혀 있습니다 (전투 모드 설정).',
        ];
        return isset($texts[$reason]) ? $texts[$reason] : (string)$reason;
    }

    private static function closeReasonName($reason)
    {
        $reason = (string)$reason;
        if (strpos($reason, 'rejected:') === 0) {
            return '결과가 인정되지 않음 (' . substr($reason, 9) . ')';
        }
        $names = ['' => '', 'completed' => '끝까지 진행', 'gate_destroyed' => '관문 파괴', 'all_left' => '모두 나감', 'timeout' => '시간 초과',
            'admin' => '관리자가 닫음', 'empty' => '모두 나가서 취소', 'lobby_timeout' => '대기 시간 초과', 'shutdown' => '실시간 서버 종료'];
        return isset($names[$reason]) ? $names[$reason] : $reason;
    }

    // ── 협동 디펜스 모니터링 ────────────────────────────────

    private function coopPage(array $problems, Request $request)
    {
        $coop = $this->app->coop();
        $statuses = Coop::statuses();
        $reason = $coop->unavailableReason();
        $content = self::done($request) . Html::problems($problems)
            . '<p>' . ($reason === null ? '○ 협동 방어전이 열려 있습니다. 실시간 서버: <code>' . Html::e($coop->realtimeUrl()) . '</code>'
                : '● 플레이어에게 열려 있지 않습니다: ' . Html::e(self::unavailableText($reason))) . '</p>';

        $counts = $coop->adminCounts();
        $rows = [];
        foreach ($statuses as $status => $name) {
            $rows[] = [$this->link('/admin/coop?status=' . $status, $name), self::n($counts[$status])];
        }
        $content .= '<h2>판 수</h2>' . Html::table(['상태', '판'], $rows);

        $status = self::field($request->query, 'status');
        if (!isset($statuses[$status])) {
            $status = '';
        }
        $rows = [];
        foreach ($coop->adminSessions($status) as $session) {
            $rows[] = [$this->link('/admin/coop/' . (int)$session['id'], $session['public_id']), $session['region_name'], $session['zone_name'],
                $statuses[$session['status']], $session['host_nickname'], (int)$session['joined'] . ' / ' . (int)$session['max_players']
                . ' (거쳐 간 사람 ' . (int)$session['members'] . ')', self::resultName($session['result']),
                (int)$session['waves_cleared'], self::kst($session['created_at']), self::kst($session['started_at']), self::kst($session['ended_at']),
                self::closeReasonName($session['close_reason'])];
        }
        $content .= '<h2>' . Html::e($status !== '' ? $statuses[$status] : '최근') . ' 방어전</h2>'
            . ($status !== '' ? '<p>' . Html::e($this->link('/admin/coop', '전체 보기')) . '</p>' : '')
            . Html::table(['판', '도시', '방어 구역', '상태', '방장', '팀원', '결과', '막은 웨이브', '만든 때', '시작', '끝', '닫힌 이유'], $rows, '방어전이 없습니다.');

        $problem = $coop->findInconsistency();
        $content .= '<h2>기록 점검</h2><p>' . ($problem === null
            ? '○ 보상의 합이 지갑 원장과 같고, 공동 예산 장부의 잔액이 이어지며, 한 계정이 동시에 두 판에 있지 않습니다.'
            : '● 맞지 않는 곳이 있습니다: ' . Html::e($problem)) . '</p>';

        $content .= '<h2 id="roles">팀 역할</h2><p>역할은 고르지 않아도 됩니다. 고르면 아래의 작은 보정이 붙습니다.</p>';
        $policy = $this->can(Auth::CITY_POLICY);
        $rows = [];
        foreach ($coop->adminRoles() as $role) {
            if ($policy) {
                $content .= '<h3>' . Html::e($role['name']) . ' <span class="sub">' . Html::e($role['code']) . '</span></h3><p>' . Html::e($role['description']) . '</p>'
                    . $this->form('/admin/coop/roles/' . $role['code'],
                        Html::input('build_discount_percent', '타워 비용 할인 (%)', (string)$role['build_discount_percent'], '0 ~ 50')
                        . Html::input('repair_bonus_percent', '수리량 증가 (%)', (string)$role['repair_bonus_percent'], '0 ~ 200')
                        . Html::input('attack_bonus_percent', '직접 공격 피해 증가 (%)', (string)$role['attack_bonus_percent'], '0 ~ 200')
                        . Html::input('ping_cooldown_percent', '핑 재사용 시간 단축 (%)', (string)$role['ping_cooldown_percent'], '0 ~ 90')
                        . Html::input('max_per_session', '한 판에 고를 수 있는 사람 수', (string)$role['max_per_session'], '0 ~ 16 (0: 제한 없음)')
                        . Html::checkbox('is_active', '사용', (int)$role['is_active'] === 1) . Html::reason());
            } else {
                $rows[] = [$role['name'], $role['description'], (int)$role['build_discount_percent'] . '%', (int)$role['repair_bonus_percent'] . '%',
                    (int)$role['attack_bonus_percent'] . '%', (int)$role['ping_cooldown_percent'] . '%',
                    (int)$role['max_per_session'] === 0 ? '제한 없음' : (int)$role['max_per_session'] . '명', (int)$role['is_active'] === 1 ? '사용' : '사용 안 함'];
            }
        }
        if (!$policy) {
            $content .= Html::table(['역할', '설명', '타워 비용 할인', '수리량 증가', '직접 공격 피해 증가', '핑 재사용 시간 단축', '한 판 인원', '사용'], $rows);
        }
        $content .= $this->settingsSection(Settings::COOP, '/admin/coop');
        return $this->page('협동 디펜스 모니터링', $content, $problems ? 422 : 200);
    }

    private static function resultName($result)
    {
        $names = ['win' => '○ 방어 성공', 'lose' => '● 방어 실패'];
        return $result === null ? '' : (isset($names[$result]) ? $names[$result] : (string)$result);
    }

    private function coopDetail($id, array $problems, Request $request)
    {
        $coop = $this->app->coop();
        $data = $coop->adminSession($id);
        if ($data === null) {
            return null;
        }
        $session = $data['session'];
        $statuses = Coop::statuses();
        $content = $this->back('/admin/coop') . self::done($request) . Html::problems($problems)
            . Html::table(['항목', '값'], [
                ['도시 / 방어 구역', $session['region_name'] . ' / ' . $session['zone_name']],
                ['상태', $statuses[$session['status']] . ($session['close_reason'] !== null ? ' (' . self::closeReasonName($session['close_reason']) . ')' : '')],
                ['정원 / 시작할 때 인원', (int)$session['max_players'] . ' / ' . (int)$session['players_at_start']],
                ['만든 때 / 시작 / 끝 (한국 시간)', self::kst($session['created_at']) . ' / ' . self::kst($session['started_at']) . ' / ' . self::kst($session['ended_at'])],
                ['정리 예정', self::kst($session['expires_at'])],
                ['결과', self::resultName($session['result'])],
                ['막은 웨이브 / 남은 관문 내구도 / 걸린 시간', (int)$session['waves_cleared'] . ' / ' . (int)$session['gate_hp_left'] . ' / ' . (int)$session['duration_seconds'] . '초'],
                ['공동 예산: 받은 합 / 쓴 합 / 남은 것', self::n($session['energy_granted']) . ' / ' . self::n($session['energy_spent']) . ' / ' . self::n($session['energy_left'])],
            ]);
        if (($session['status'] === 'lobby' || $session['status'] === 'running') && $this->can(Auth::COOP_MANAGE)) {
            $content .= '<h2>강제로 닫기</h2><p>보상은 지급되지 않습니다. 팀원은 다른 판에 들어갈 수 있게 됩니다.</p>'
                . $this->form('/admin/coop/' . $id . '/close', Html::reason(), '이 판 닫기');
        }

        $roles = [];
        foreach ($coop->adminRoles() as $role) {
            $roles[$role['code']] = $role['name'];
        }
        $rows = [];
        foreach ($data['members'] as $member) {
            $rows[] = [$member['nickname'], $member['player_public_id'], (int)$member['level'],
                $member['role_code'] !== null && isset($roles[$member['role_code']]) ? $roles[$member['role_code']] : '(고르지 않음)',
                $member['status'] === 'joined' ? '참여' : '나감 ' . self::kst($member['left_at']),
                (int)$member['connected_seconds'] . '초', (int)$member['kills'], (int)$member['towers_built'], (int)$member['towers_upgraded'],
                (int)$member['repairs'], self::n($member['damage_dealt']), (int)$member['pings_sent'] . ' (거절 ' . (int)$member['pings_rejected'] . ')',
                $member['eligible'] === null ? '' : ((int)$member['eligible'] === 1 ? '○' : '●'),
                self::n($member['reward_gc']) . ' GC / ' . self::n($member['reward_xp']) . ' XP', self::rewardNote($member['reward_note'])];
        }
        $content .= '<h2>팀원</h2>' . Html::table(['닉네임', '계정', '레벨', '역할', '상태', '접속 시간', '처치', '타워 설치', '타워 강화', '수리', '준 피해',
            '핑', '보상 대상', '보상', '비고'], $rows, '팀원이 없습니다.');

        $kinds = ['grant_start' => '시작 지급', 'grant_wave' => '웨이브 지급', 'grant_kill' => '처치 지급', 'build' => '타워 설치', 'upgrade' => '타워 강화',
            'repair' => '수리', 'lost' => '타워 잃음'];
        $rows = [];
        foreach ($data['budget'] as $line) {
            $rows[] = [(int)$line['seq'], self::clock($line['at_ms']), isset($kinds[$line['kind']]) ? $kinds[$line['kind']] : $line['kind'],
                (string)$line['nickname'], self::signed($line['delta']), self::n($line['balance_after']),
                $line['slot_index'] !== null ? (int)$line['slot_index'] : '', (string)$line['structure_code']];
        }
        $content .= '<h2>공동 예산 장부</h2><p>팀이 한 판 안에서 함께 쓰는 에너지입니다 (GC 가 아닙니다). 줄마다 "바뀐 뒤 잔액"이 이어져야 하고, '
            . '한 자리에는 타워가 하나만 놓입니다. 300줄까지 보입니다.</p>'
            . Html::table(['순번', '전투 시간', '종류', '쓴 사람', '변화', '바뀐 뒤 잔액', '자리', '시설'], $rows, '기록이 없습니다.');

        $pingKinds = Coop::pingKinds();
        $rows = [];
        foreach ($data['pings'] as $ping) {
            $rows[] = [(int)$ping['seq'], self::clock($ping['at_ms']), $ping['nickname'], isset($pingKinds[$ping['kind']]) ? $pingKinds[$ping['kind']] : $ping['kind'],
                number_format((int)$ping['x_centi'] / 100, 1) . ', ' . number_format((int)$ping['z_centi'] / 100, 1),
                (int)$ping['accepted'] === 1 ? '○ 공유됨' : '● 거절됨 (빈도 제한·차단)'];
        }
        $content .= '<h2>전술 핑</h2>' . Html::table(['순번', '전투 시간', '보낸 사람', '종류', '위치 (m)', '처리'], $rows, '기록이 없습니다.');
        return $this->page('협동 방어전 ' . $session['public_id'], $content, $problems ? 422 : 200);
    }

    private static function clock($ms)
    {
        $seconds = (int)floor((int)$ms / 1000);
        return sprintf('%d:%02d', (int)floor($seconds / 60), $seconds % 60);
    }

    private static function rewardNote($note)
    {
        $names = ['' => '', 'low_participation' => '참여 시간 부족', 'daily_limit' => '오늘의 보상 횟수를 다 씀', 'rejected' => '결과가 인정되지 않음'];
        return isset($names[(string)$note]) ? $names[(string)$note] : (string)$note;
    }

    // ── 미니맵 및 전술 핑 설정 ──────────────────────────────

    private function pingPage(array $problems, Request $request)
    {
        $coop = $this->app->coop();
        $kinds = Coop::pingKinds();
        $content = self::done($request) . Html::problems($problems)
            . '<p>전술 핑은 실시간 서버가 받아 팀원에게 나눠 줍니다. 한 사람이 보낼 수 있는 빈도는 아래 설정으로 제한되고 '
            . '(연속으로 보낼 수 있는 횟수와 한 번이 다시 차는 시간), 거절이 이어지면 잠시 자동으로 막힙니다. '
            . '팀원은 그 판 안에서 특정 사람의 핑을 숨길 수 있고, 남용이 이어지는 계정은 여기에서 막을 수 있습니다.</p>';

        $rows = [];
        $used = [];
        foreach ($coop->pingKindTotals() as $row) {
            $used[$row['kind']] = $row;
        }
        foreach ($kinds as $code => $name) {
            $rows[] = [$name, $code, isset($used[$code]) ? self::n($used[$code]['total']) : '0', isset($used[$code]) ? self::n($used[$code]['rejected']) : '0'];
        }
        $content .= '<h2>핑 종류와 최근 7일의 사용</h2>' . Html::table(['종류', '코드', '보낸 수', '거절된 수'], $rows);

        $rows = [];
        foreach ($coop->pingAbuse() as $row) {
            $rows[] = [$row['nickname'], $row['player_public_id'], self::n($row['total']), self::n($row['rejected']), self::n($row['sessions'])];
        }
        $content .= '<h2>최근 7일 동안 핑이 많이 거절된 계정</h2>'
            . Html::table(['닉네임', '계정', '보낸 수', '거절된 수', '판 수'], $rows, '거절된 핑이 없습니다.');

        $manage = $this->can(Auth::COOP_MANAGE);
        $rows = [];
        foreach ($coop->pingBlocks() as $block) {
            $active = $block['lifted_at'] === null && ($block['expires_at'] === null || $block['expires_at'] > \CG\Infra\Clock::sql());
            $rows[] = [$block['nickname'], $block['player_public_id'], $block['reason'], (string)$block['login'], self::kst($block['created_at']),
                $block['expires_at'] === null ? '풀 때까지' : self::kst($block['expires_at']),
                $block['lifted_at'] !== null ? '풀림 ' . self::kst($block['lifted_at']) : ($active ? '● 막힘' : '기간 끝남'),
                $active && $manage ? $this->inline('/admin/ping/blocks/' . (int)$block['id'] . '/lift', '차단 풀기') : ''];
        }
        $content .= '<h2>전술 핑 차단</h2>'
            . Html::table(['닉네임', '계정', '사유', '막은 관리자', '막은 때', '끝나는 때', '상태', '할 일'], $rows, '차단한 계정이 없습니다.');
        if ($manage) {
            $content .= '<h3>계정의 핑 막기</h3><p>핑만 막습니다. 방어전 참여와 보상에는 영향이 없습니다.</p>'
                . $this->form('/admin/ping/block',
                    Html::input('player_id', '계정 (공개 ID)', self::field($request->form, 'player_id'), '예: p_xxxxxxxxxxxxxx')
                    . Html::input('hours', '기간 (시간)', self::field($request->form, 'hours', '24'), '0 ~ 8,760 (0: 풀 때까지)') . Html::reason(), '핑 막기');
        }
        $content .= $this->settingsSection(Settings::PING, '/admin/ping');
        return $this->page('미니맵 및 전술 핑 설정', $content, $problems ? 422 : 200);
    }
}
