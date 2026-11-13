<?php
namespace CG\Admin;

use CG\Infra\Request;
use CG\Service\Modeling;

/**
 * 관리자 화면: 모델링 제작 관리(/admin/modeling), 도시 콘텐츠 배포 관리(/admin/content).
 * (docs/CITY_MODELING_PIPELINE.md) CityOpsPages 가 쓴다.
 *
 * 제작(담당자) → 검수(다른 관리자) → 결과물 승인(올린 사람이 아닌 관리자) → 배포(최고 관리자, 받는 비율을 정해 단계적으로) → 회수.
 * 서버는 파일을 받지 않는다. 콘텐츠 저장소에 올린 묶음의 경로와 해시만 적는다.
 */
trait ModelingScreens
{
    private function routeModeling($get, $path, Request $request)
    {
        $form = $request->form;
        $ip = $request->ip;
        $modeling = $this->app->modeling();
        if ($path === '/admin/modeling') {
            return $get ? $this->modelingList([], $request) : null;
        }
        if ($path === '/admin/modeling/create' && !$get) {
            list($id, $problems) = $modeling->createProject($this->admin, $form, $ip);
            return $problems ? $this->modelingList($problems, $request, $form) : ['redirect' => '/admin/modeling/' . (int)$id . '?done=created'];
        }
        if (preg_match('#^/admin/modeling/(\d{1,18})$#', $path, $m)) {
            return $get ? $this->modelingDetail((int)$m[1], [], $request) : null;
        }
        if (preg_match('#^/admin/modeling/(\d{1,18})/(update|transition|review|assets)$#', $path, $m) && !$get) {
            $id = (int)$m[1];
            if ($m[2] === 'update') {
                $problems = $modeling->updateProject($this->admin, $id, $form, $ip);
                $done = 'saved';
            } elseif ($m[2] === 'transition') {
                $problems = $modeling->transition($this->admin, $id, self::field($form, 'to'), self::field($form, 'reason'), $ip);
                $done = 'moved';
            } elseif ($m[2] === 'review') {
                $problems = $modeling->review($this->admin, $id, self::field($form, 'result'), self::field($form, 'note'), $ip);
                $done = 'reviewed';
            } else {
                $problems = $modeling->addAsset($this->admin, $id, $form, $ip);
                $done = 'added';
            }
            return $problems ? $this->modelingDetail($id, $problems, $request) : ['redirect' => '/admin/modeling/' . $id . '?done=' . $done];
        }
        if (preg_match('#^/admin/modeling/assets/(\d{1,18})/(approve|reject)$#', $path, $m) && !$get) {
            $asset = $this->app->db->one('SELECT project_id FROM city_modeling_assets WHERE id = ?', [(int)$m[1]]);
            if ($asset === null) {
                return null;
            }
            $problems = $modeling->reviewAsset($this->admin, (int)$m[1], $m[2] === 'approve', self::field($form, 'note'), $ip);
            return $problems ? $this->modelingDetail((int)$asset['project_id'], $problems, $request)
                : ['redirect' => '/admin/modeling/' . (int)$asset['project_id'] . '?done=reviewed'];
        }
        if ($path === '/admin/content') {
            return $get ? $this->contentPage([], $request) : null;
        }
        if (preg_match('#^/admin/content/(\d{1,18})/(release|withdraw)$#', $path, $m) && !$get) {
            $problems = $m[2] === 'release'
                ? $modeling->release($this->admin, (int)$m[1], self::field($form, 'rollout_percent'), self::field($form, 'reason'), $ip)
                : $modeling->withdraw($this->admin, (int)$m[1], self::field($form, 'reason'), $ip);
            return $problems ? $this->contentPage($problems, $request) : ['redirect' => '/admin/content?done=' . ($m[2] === 'release' ? 'released' : 'withdrawn')];
        }
        return null;
    }

    // ── 모델링 제작 관리 ────────────────────────────────────

    private function modelingList(array $problems, Request $request, array $sent = [])
    {
        $modeling = $this->app->modeling();
        $statuses = Modeling::statuses();
        $kinds = Modeling::kinds();
        $status = self::field($request->query, 'status', 'active');
        if ($status !== 'active' && $status !== 'all' && !isset($statuses[$status])) {
            $status = 'active';
        }
        $tabs = '';
        foreach (['active' => '진행 중', 'all' => '전체'] + $statuses as $key => $name) {
            $tabs .= ($key === $status ? '<strong>' . Html::e($name) . '</strong>' : Html::e($this->link('/admin/modeling?status=' . $key, $name))) . ' · ';
        }
        $rows = [];
        foreach ($modeling->projects($status === 'all' ? null : $status) as $project) {
            $rows[] = [$this->link('/admin/modeling/' . (int)$project['id'], $project['title']),
                $this->link('/admin/growth/city/' . $project['region_code'], $project['region_name']), $kinds[$project['kind']],
                $statuses[$project['status']], (string)$project['assignee_login'], (string)$project['due_on'], $project['file_version'],
                self::reviewName($project['review_result']), self::kst($project['applied_at']), self::kst($project['updated_at'])];
        }
        $content = self::done($request) . Html::problems($problems)
            . '<p>상태는 미착수 → 기획 → 제작 → 검수 → 배포 준비 → 적용 완료 의 순서로 나아갑니다. '
            . '"배포 준비"는 검수를 통과해야만 되고, "적용 완료"는 결과물을 배포해야만 됩니다.</p><p>' . rtrim($tabs, ' ·') . '</p>'
            . Html::table(['작업', '도시', '종류', '상태', '담당자', '예정일', '파일 버전', '검수 결과', '적용 일시', '마지막 변경'], $rows, '해당하는 제작 작업이 없습니다.');

        if ($this->can(Auth::MODELING_MANAGE)) {
            $value = function ($key, $default = '') use ($sent, $request) {
                return self::field($sent, $key, $default);
            };
            $content .= '<h2 id="create">제작 작업 등록</h2><p>기준 도달 알림에서 넘어왔다면 알림 번호가 채워져 있습니다. 알림은 먼저 확인해야 합니다.</p>'
                . $this->form('/admin/modeling/create',
                    Html::input('region_code', '도시 코드', $value('region_code', self::field($request->query, 'region')), '예: 11110')
                    . Html::select('kind', '종류', $kinds, $value('kind', 'street'))
                    . Html::input('title', '작업 이름', $value('title'), '80자까지')
                    . Html::select('assignee_admin_id', '담당자', $this->adminOptions(), $value('assignee_admin_id'))
                    . Html::input('due_on', '예정일', $value('due_on'), '예: 2026-12-31 (비워도 됩니다)')
                    . Html::input('file_version', '파일 버전', $value('file_version'), '비워도 됩니다')
                    . Html::textarea('memo', '작업 메모', $value('memo'))
                    . Html::input('notification_id', '기준 도달 알림 번호', $value('notification_id', self::field($request->query, 'notification')), '없으면 비워 둡니다'),
                    '등록');
        }
        return $this->page('모델링 제작 관리', $content, $problems ? 422 : 200);
    }

    private static function reviewName($result)
    {
        $names = ['none' => '', 'pass' => '○ 통과', 'fail' => '● 반려'];
        return isset($names[$result]) ? $names[$result] : (string)$result;
    }

    private function modelingDetail($id, array $problems, Request $request)
    {
        $modeling = $this->app->modeling();
        $project = $modeling->project($id);
        if ($project === null) {
            return null;
        }
        $statuses = Modeling::statuses();
        $kinds = Modeling::kinds();
        $assetStatuses = Modeling::assetStatuses();
        $content = $this->back('/admin/modeling') . self::done($request) . Html::problems($problems)
            . Html::table(['항목', '값'], [
                ['도시', $this->link('/admin/growth/city/' . $project['region_code'], $project['region_name'])],
                ['종류', $kinds[$project['kind']]], ['상태', $statuses[$project['status']]],
                ['담당자', (string)$project['assignee_login']], ['예정일', (string)$project['due_on']], ['파일 버전', $project['file_version']],
                ['검수 결과', trim(self::reviewName($project['review_result']) . ' ' . $project['review_note'])],
                ['검수한 관리자', trim((string)$project['reviewer_login'] . ' ' . self::kst($project['reviewed_at']))],
                ['적용 일시 (한국 시간)', self::kst($project['applied_at'])],
                ['등록 / 마지막 변경', self::kst($project['created_at']) . ' / ' . self::kst($project['updated_at'])],
            ])
            . '<h2>작업 메모</h2><p>' . nl2br(Html::e($project['memo'])) . '</p>';

        if ($this->can(Auth::MODELING_MANAGE)) {
            $content .= '<h2>작업 정보 고치기</h2>' . $this->form('/admin/modeling/' . $id . '/update',
                Html::input('title', '작업 이름', $project['title'])
                . Html::select('assignee_admin_id', '담당자', $this->adminOptions(), (string)$project['assignee_admin_id'])
                . Html::input('due_on', '예정일', (string)$project['due_on'], '예: 2026-12-31')
                . Html::input('file_version', '파일 버전', $project['file_version'])
                . Html::textarea('memo', '작업 메모', $project['memo']));
            $next = Modeling::manualTransitions()[$project['status']];
            if ($next) {
                $options = [];
                foreach ($next as $to) {
                    $options[$to] = $statuses[$to];
                }
                $content .= '<h2>상태 바꾸기</h2>' . $this->form('/admin/modeling/' . $id . '/transition',
                    Html::select('to', '다음 상태', $options, $next[0]) . Html::reason(), '상태 바꾸기');
            }
        }
        if ($project['status'] === Modeling::REVIEW && $this->can(Auth::MODELING_REVIEW)) {
            $content .= '<h2>검수</h2><p>담당자 본인은 검수할 수 없습니다. 통과하면 "배포 준비", 반려하면 "제작"으로 돌아갑니다.</p>'
                . $this->form('/admin/modeling/' . $id . '/review',
                    Html::select('result', '검수 결과', ['pass' => '통과', 'fail' => '반려'], 'pass')
                    . Html::input('note', '검수 의견 (필수)', '', '500자까지'), '검수 결과 기록');
        }

        $rows = [];
        foreach ($modeling->assets($id) as $asset) {
            $actions = '';
            if ($asset['status'] === 'uploaded' && $this->can(Auth::MODELING_REVIEW)) {
                $actions = Html::e($this->inline('/admin/modeling/assets/' . (int)$asset['id'] . '/approve', '승인', [], 'note', '검수 의견')) . ' '
                    . Html::e($this->inline('/admin/modeling/assets/' . (int)$asset['id'] . '/reject', '반려', [], 'note', '검수 의견'));
            } elseif ($asset['status'] === 'approved' || $asset['status'] === 'released') {
                $actions = Html::e($this->link('/admin/content', '배포 관리로'));
            }
            $rows[] = [$asset['version'], $assetStatuses[$asset['status']], $asset['address_key'], $asset['catalog_path'],
                Html::raw('<code>' . Html::e($asset['content_hash']) . '</code>'), self::n($asset['size_bytes']), $asset['min_client_version'],
                (string)$asset['uploader_login'], trim((string)$asset['reviewer_login'] . ' ' . $asset['review_note']), $asset['note'],
                Html::raw($actions)];
        }
        $content .= '<h2>결과물 (콘텐츠 묶음)</h2>'
            . Html::table(['버전', '상태', '콘텐츠 주소', '카탈로그 경로', 'SHA-256', '크기(바이트)', '최소 클라이언트', '올린 사람', '검수', '메모', '할 일'],
                $rows, '등록된 결과물이 없습니다.');
        if ($this->can(Auth::MODELING_MANAGE) && $project['status'] !== Modeling::NOT_STARTED && $project['status'] !== Modeling::PLANNING) {
            $content .= '<h3>결과물 등록</h3><p>파일은 콘텐츠 저장소에 먼저 올리고, 여기에는 그 경로와 해시만 적습니다. '
                . '클라이언트는 받은 묶음의 해시가 여기 적힌 값과 다르면 쓰지 않습니다.</p>'
                . $this->form('/admin/modeling/' . $id . '/assets',
                    Html::input('version', '버전', '', '예: 1.0.0')
                    . Html::input('address_key', '콘텐츠 주소 (Addressables 키)', '', '예: city/11110/street')
                    . Html::input('catalog_path', '카탈로그 경로', '', '콘텐츠 저장소 안의 상대 경로. 예: city/11110/1.0.0/catalog.json')
                    . Html::input('content_hash', 'SHA-256 해시', '', '16진수 64자')
                    . Html::input('size_bytes', '크기 (바이트)', '')
                    . Html::input('min_client_version', '최소 클라이언트 버전', '', '예: 9.0.0 (비워도 됩니다)')
                    . Html::input('note', '메모', ''), '등록');
        }
        return $this->page($project['title'] . ' — 제작 작업', $content, $problems ? 422 : 200);
    }

    // ── 도시 콘텐츠 배포 관리 ───────────────────────────────

    private function contentPage(array $problems, Request $request)
    {
        $modeling = $this->app->modeling();
        $assetStatuses = Modeling::assetStatuses();
        $statuses = Modeling::statuses();
        $base = $modeling->contentBase();
        $content = self::done($request) . Html::problems($problems)
            . '<p>검수를 통과한 작업의 승인된 결과물만 배포할 수 있습니다. 받는 비율을 낮게 시작해 문제가 없으면 100% 로 올립니다. '
            . '문제가 생기면 회수합니다: 운영 중인 클라이언트는 다음에 목록을 받을 때부터 그 묶음을 쓰지 않고 기본 도시를 보여 줍니다.</p>';
        $content .= $base === ''
            ? '<div class="problems" role="alert">콘텐츠 저장소 주소(CONTENT_BASE_URL)가 서버에 설정되어 있지 않습니다. 설정하기 전에는 배포할 수 없습니다.</div>'
            : '<p>콘텐츠 저장소: <code>' . Html::e($base) . '</code> (서버 설정으로 고정됩니다. 화면에서 바꿀 수 없습니다.)</p>';

        $release = $this->can(Auth::CONTENT_RELEASE);
        $withdraw = $this->can(Auth::CONTENT_WITHDRAW);
        $sections = ['released' => '배포 중', 'approved' => '승인됨 (배포 대기)', 'withdrawn' => '회수됨'];
        foreach ($sections as $status => $title) {
            $rows = [];
            foreach ($modeling->assets(null, $status, 100) as $asset) {
                $actions = '';
                $path = '/admin/content/' . (int)$asset['id'];
                if ($status !== 'withdrawn' && $release) {
                    $actions .= '<form method="post" class="inline" action="' . Html::e($this->url($path . '/release')) . '">'
                        . '<input type="hidden" name="_csrf" value="' . Html::e($this->csrf) . '">'
                        . '<input type="text" name="rollout_percent" value="' . Html::e($status === 'released' ? (int)$asset['rollout_percent'] : 10)
                        . '" size="3" aria-label="받는 비율(%)"> % <input type="text" name="reason" placeholder="사유" aria-label="사유"> '
                        . '<button type="submit">' . ($status === 'released' ? '받는 비율 바꾸기' : '배포') . '</button></form> ';
                }
                if ($status === 'released' && $withdraw) {
                    $actions .= Html::e($this->inline($path . '/withdraw', '회수'));
                }
                $rows[] = [$this->link('/admin/growth/city/' . $asset['region_code'], $asset['region_name']),
                    $this->link('/admin/modeling/' . (int)$asset['project_id'], $asset['project_title']), $statuses[$asset['project_status']],
                    $asset['version'], $assetStatuses[$asset['status']], (int)$asset['rollout_percent'] . '%', $asset['address_key'],
                    $asset['min_client_version'], trim((string)$asset['releaser_login'] . ' ' . self::kst($asset['released_at'])),
                    $status === 'withdrawn' ? trim(self::kst($asset['withdrawn_at']) . ' ' . $asset['withdraw_reason']) : '', Html::raw($actions)];
            }
            $content .= '<h2>' . Html::e($title) . '</h2>'
                . Html::table(['도시', '작업', '작업 상태', '버전', '상태', '받는 비율', '콘텐츠 주소', '최소 클라이언트', '배포', '회수', '할 일'], $rows, '없습니다.');
        }

        // 지역별 콘텐츠 개방 현황
        $rows = [];
        foreach ($this->app->growth()->adminCities() as $city) {
            if ($city['content_version'] === null && $city['opened_at'] === null) {
                continue;
            }
            $rows[] = [$this->link('/admin/growth/city/' . $city['code'], $city['name']), (string)$city['content_version'],
                $city['content_version'] !== null && $city['opened_at'] !== null ? '● 정식 개방' : ($city['content_version'] !== null ? '일부 클라이언트에 배포 중' : '회수됨'),
                self::kst($city['opened_at']), self::n($city['valid_residents'])];
        }
        $content .= '<h2>지역별 콘텐츠 개방 현황</h2><p>정식 개방 일시는 상징거리의 결과물이 처음으로 모든 클라이언트(100%)에 배포된 때입니다.</p>'
            . Html::table(['도시', '적용 버전', '상태', '정식 개방 (한국 시간)', '유효 전입자'], $rows, '아직 지역 콘텐츠가 배포된 도시가 없습니다.');
        return $this->page('도시 콘텐츠 배포 관리', $content, $problems ? 422 : 200);
    }
}
