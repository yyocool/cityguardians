<?php
namespace CG\Web;

use CG\Admin\Html;
use CG\Infra\Ids;
use CG\Infra\Request;
use CG\Service\CityPass;
use CG\Service\Features;

/**
 * 홈페이지: CITY PASS (docs/CITY_PASS_SYSTEM.md 5장).
 *
 * 제휴 상점과 혜택은 운영자가 승인한 것만 나온다. 없으면 없다고 말한다: 본보기 상점이나 가짜 혜택을 채워 넣지 않는다.
 */
trait SitePassPages
{
    private function routePass($get, $path, Request $request)
    {
        $pass = $this->app->cityPass();
        $form = $request->form;
        if ($get) {
            switch ($path) {
                case '/city-pass': return $this->passIntro();
                case '/city-pass/merchants': return $this->passMerchants($request);
                case '/city-pass/offers': return $this->passOffers($request, []);
                case '/city-pass/coupons': return $this->player === null ? $this->needLogin($path) : $this->passCoupons([], null, $request);
                case '/city-pass/partnership': return $this->passPartnership();
            }
            return null;
        }
        // 여기부터는 로그인과 CSRF 확인을 마친 POST 다.
        switch ($path) {
            case '/city-pass/join':
                $pass->join($this->player, self::field($form, 'agree') === '1', self::field($form, 'region') !== '' ? self::field($form, 'region') : null);
                return $this->redirect('/city-pass/coupons?done=joined');
            case '/city-pass/region':
                $pass->setInterestRegion($this->player, self::field($form, 'region'));
                return $this->redirect('/city-pass/coupons?done=region');
            case '/city-pass/claim':
                $pass->claim($this->player, self::field($form, 'offer'), self::field($form, 'key'));
                return $this->redirect('/city-pass/coupons?done=claimed');
            case '/city-pass/present':
                return $this->passCoupons([], $pass->present($this->player, self::field($form, 'coupon')), $request);
        }
        return null;
    }

    private function passState()
    {
        $features = $this->app->features();
        return [$features->isEnabled(Features::PASS_MEMBERSHIP), $features->isEnabled(Features::PASS_COUPONS)];
    }

    // ── 서비스 소개 ─────────────────────────────────────────

    private function passIntro()
    {
        list($membership, $coupons) = $this->passState();
        $content = '<p class="lead">CITY PASS 는 시티가디언즈 이용자가 제휴 상점에서 혜택을 받을 수 있는 지역 제휴 멤버십입니다.</p>';
        if (!$membership) {
            $content .= self::soon('CITY PASS 는 아직 열리지 않았습니다. 지금은 가입할 수 없고, 제휴 상점과 혜택도 아직 없습니다. '
                . '제휴와 운영 준비가 끝나면 이 페이지에서 알려 드리겠습니다.');
        } elseif (!$coupons) {
            $content .= self::soon('멤버십 가입은 열려 있습니다. 제휴 상점과 쿠폰은 준비 중입니다.');
        }
        $content .= '<h2>어떤 서비스인가요</h2><ul>'
            . '<li>게임 안에서 전입신고한 지역이 <strong>기본 관심 지역</strong>이 됩니다. 다른 지역의 혜택도 볼 수 있습니다.</li>'
            . '<li>실제로 그 지역에 사는지는 확인하지 않습니다. 주소나 신분증을 받지 않습니다.</li>'
            . '<li>제휴 상점은 운영팀과 제휴 계약을 맺은 곳만 나옵니다.</li>'
            . '<li>쿠폰은 멤버십 가입, 무료 프로모션, 제휴 상점이 공개한 조건으로 받습니다.</li></ul>'
            . '<h2>게임 보상과는 다릅니다</h2><ul>'
            . '<li>쿠폰은 게임머니(GC)로 살 수 없고, GC 로 바꿀 수도 없습니다.</li>'
            . '<li>전투 성적, 랭킹, 아이템, 플레이 시간, 결제 금액은 쿠폰 발급과 관계가 없습니다.</li>'
            . '<li>게임을 많이 하거나 잘한다고 혜택이 늘어나지 않습니다.</li></ul>'
            . '<h2>이용 방법</h2><ol>'
            . '<li>게임의 휴대전화(P 키)에서 <strong>홈페이지 로그인 코드</strong>를 받아 ' . $this->a('/login', '로그인') . '합니다. 게임 안의 CITY PASS 안내센터에서도 같은 내용을 볼 수 있습니다.</li>'
            . '<li>' . $this->a('/city-pass/coupons', '쿠폰') . ' 화면에서 CITY PASS 에 가입합니다 (무료).</li>'
            . '<li>' . $this->a('/city-pass/offers', '지역별 혜택') . '에서 쿠폰을 받습니다.</li>'
            . '<li>매장에서 쿠폰의 <strong>일회용 코드</strong>(게임에서는 QR)를 직원에게 보여 줍니다. 직원이 확인하면 쿠폰이 사용 처리됩니다.</li></ol>'
            . '<h2>개인정보</h2><p>CITY PASS 는 게임 닉네임과 회원번호만 씁니다. 이름, 전화번호, 주소를 받지 않습니다. '
            . '매장에 보여 주는 코드와 QR 에는 개인정보가 들어 있지 않고, 매장 직원에게 닉네임이나 회원번호가 보이지 않습니다.</p>';
        return $this->page('CITY PASS', $content, 200, '시티가디언즈 이용자를 위한 지역 제휴 멤버십 CITY PASS 소개');
    }

    // ── 제휴 상점 ───────────────────────────────────────────

    /** 목록에 나온 지점에서 지역 고르기 목록을 만든다 (제휴 상점이 있는 지역만 고를 수 있다). */
    private static function regionsOf(array $items)
    {
        $regions = [];
        foreach ($items as $item) {
            foreach ($item['branches'] as $branch) {
                $regions[$branch['regionCode']] = $branch['regionName'];
            }
        }
        asort($regions);
        return $regions;
    }

    private function passFilter($path, array $regions, $region, $category)
    {
        return '<form method="get" action="' . Html::e($this->url($path)) . '" class="filter">'
            . Html::select('region', '지역', ['' => '전체 지역'] + $regions, $region)
            . Html::select('category', '업종', ['' => '전체 업종'] + CityPass::categories(), $category)
            . '<p><button type="submit">보기</button></p></form>';
    }

    private function branchHtml(array $branch)
    {
        $out = '<li><strong>' . Html::e($branch['name']) . '</strong> <span class="tag">' . Html::e($branch['regionName']) . '</span><br>'
            . Html::e($branch['address']) . ($branch['phone'] !== null && $branch['phone'] !== '' ? ' · ' . Html::e($branch['phone']) : '');
        if ($branch['lat'] !== null) {
            $out .= '<br><small>지도 위치: 위도 ' . Html::e(number_format($branch['lat'], 5)) . ', 경도 ' . Html::e(number_format($branch['lng'], 5)) . '</small>';
        }
        return $out . '</li>';
    }

    private function passMerchants(Request $request)
    {
        list(, $coupons) = $this->passState();
        if (!$coupons) {
            return $this->page('제휴 상점', self::soon('제휴 상점은 준비 중입니다. 제휴 계약이 끝난 상점만 이 페이지에 나옵니다. 지금은 보여 드릴 상점이 없습니다.')
                . '<p>상점을 운영하고 계신가요? ' . $this->a('/city-pass/partnership', '제휴 문의') . '를 봐 주세요.</p>');
        }
        $all = $this->app->cityPass()->merchants()['merchants'];
        $regions = self::regionsOf($all);
        $region = self::field($request->query, 'region');
        $region = isset($regions[$region]) ? $region : '';
        $category = self::field($request->query, 'category');
        $category = isset(CityPass::categories()[$category]) ? $category : '';
        $content = $this->passFilter('/city-pass/merchants', $regions, $region, $category);
        $shown = 0;
        foreach ($all as $merchant) {
            $branches = array_filter($merchant['branches'], function ($branch) use ($region) {
                return $region === '' || $branch['regionCode'] === $region;
            });
            if (!$branches || ($category !== '' && $merchant['category'] !== $category)) {
                continue;
            }
            $shown++;
            $content .= '<article class="card"><h2>' . Html::e($merchant['name']) . ' <span class="tag">' . Html::e($merchant['categoryName']) . '</span></h2>';
            $first = reset($branches);
            if ($first['imagePath'] !== null) {
                $content .= '<img src="' . Html::e($this->url($first['imagePath'])) . '" alt="' . Html::e($merchant['name']) . ' 대표 이미지" loading="lazy">';
            }
            $content .= '<ul class="branches">' . implode('', array_map([$this, 'branchHtml'], $branches)) . '</ul></article>';
        }
        if ($shown === 0) {
            $content .= '<p class="empty">' . ($all ? '고른 조건에 맞는 제휴 상점이 없습니다.' : '아직 제휴 상점이 없습니다. 제휴가 맺어지면 이 페이지에 나옵니다.') . '</p>';
        }
        return $this->page('제휴 상점', '<p>운영팀과 제휴 계약을 맺은 상점입니다. 혜택은 ' . $this->a('/city-pass/offers', '지역별 혜택') . '에서 볼 수 있습니다.</p>' . $content);
    }

    // ── 지역별 혜택 ─────────────────────────────────────────

    private function passOffers(Request $request, array $problems)
    {
        list(, $coupons) = $this->passState();
        if (!$coupons) {
            return $this->page('지역별 혜택', self::soon('제휴 혜택은 준비 중입니다. 지금은 받을 수 있는 쿠폰이 없습니다.'));
        }
        $data = $this->app->cityPass()->offers($this->player);
        $regions = self::regionsOf($data['offers']);
        $region = self::field($request->query, 'region');
        $region = isset($regions[$region]) ? $region : '';
        $category = self::field($request->query, 'category');
        $category = isset(CityPass::categories()[$category]) ? $category : '';
        $profile = $this->player !== null ? $this->app->cityPass()->profile($this->player) : null;
        $interest = $profile !== null && $profile['interestRegion'] !== null ? $profile['interestRegion']['code'] : null;
        $blocks = ['not_member' => 'CITY PASS 에 가입하면 받을 수 있습니다.', 'limit_reached' => '이미 받은 쿠폰입니다.', 'sold_out' => '준비한 쿠폰이 모두 나갔습니다.'];
        $content = self::problems($problems) . $this->passFilter('/city-pass/offers', $regions, $region, $category);
        if ($this->player === null) {
            $content .= '<p>쿠폰을 받으려면 ' . $this->a('/login?next=/city-pass/offers', '로그인') . '해 주세요.</p>';
        }
        $shown = 0;
        foreach ($data['offers'] as $offer) {
            $branches = array_filter($offer['branches'], function ($branch) use ($region) {
                return $region === '' || $branch['regionCode'] === $region;
            });
            if (!$branches || ($category !== '' && $offer['merchant']['category'] !== $category)) {
                continue;
            }
            $shown++;
            $mine = $interest !== null && array_filter($branches, function ($branch) use ($interest) {
                return $branch['regionCode'] === $interest;
            });
            $content .= '<article class="card"><h2>' . Html::e($offer['title']) . ' <span class="tag strong">' . Html::e($offer['discountText']) . '</span>'
                . ($mine ? ' <span class="tag">내 관심 지역</span>' : '') . '</h2>'
                . '<p>' . Html::e($offer['merchant']['name']) . ' · ' . Html::e($offer['merchant']['categoryName']) . '</p>'
                . ($offer['description'] !== '' ? self::prose($offer['description']) : '')
                . '<dl><dt>이용 조건</dt><dd>' . nl2br(Html::e($offer['conditions'])) . '</dd>'
                . '<dt>이용 기간</dt><dd>' . Html::e(self::kst($offer['startsAt'])) . ' ~ ' . Html::e(self::kst($offer['endsAt'])) . ' (한국 시간)</dd>'
                . '<dt>쿠폰 유효기간</dt><dd>받은 날부터 ' . (int)$offer['couponValidDays'] . '일 (이용 기간을 넘지 않습니다)</dd>'
                . '<dt>받을 수 있는 수</dt><dd>한 사람 ' . (int)$offer['perMemberLimit'] . '장'
                . ($offer['remaining'] !== null ? ' · 남은 쿠폰 ' . (int)$offer['remaining'] . '장' : '') . '</dd>'
                . '<dt>발급 조건</dt><dd>' . Html::e(CityPass::issuePolicies()[$offer['issuePolicy']]) . '</dd></dl>'
                . '<ul class="branches">' . implode('', array_map([$this, 'branchHtml'], $branches)) . '</ul>';
            if ($this->player !== null) {
                $content .= $offer['claimable']
                    // 요청 번호를 폼에 넣어 둔다: 두 번 눌러도 쿠폰은 한 장만 생긴다.
                    ? $this->form('/city-pass/claim', '<input type="hidden" name="offer" value="' . Html::e($offer['id']) . '">'
                        . '<input type="hidden" name="key" value="' . Html::e('web-' . Ids::token(24)) . '">', '쿠폰 받기 (무료)')
                    : '<p class="muted">' . Html::e($blocks[$offer['claimBlock']]) . '</p>';
            }
            $content .= '</article>';
        }
        if ($shown === 0) {
            $content .= '<p class="empty">' . ($data['offers'] ? '고른 조건에 맞는 혜택이 없습니다.' : '지금 받을 수 있는 혜택이 없습니다. 제휴 상점이 혜택을 등록하면 이 페이지에 나옵니다.') . '</p>';
        }
        return $this->page('지역별 혜택', '<p>승인된 제휴 상점의 혜택입니다. 쿠폰은 무료이고, 게임머니나 게임 성적과 관계가 없습니다.</p>' . $content, $problems ? 422 : 200);
    }

    // ── 쿠폰 (내 CITY PASS) ─────────────────────────────────

    /** @param array|null $shown 방금 만든 일회용 코드 (present 의 응답). 이 응답에서만 보인다. */
    private function passCoupons(array $problems, $shown, Request $request)
    {
        $pass = $this->app->cityPass();
        $profile = $pass->profile($this->player);
        $done = ['joined' => 'CITY PASS 에 가입했습니다.', 'region' => '관심 지역을 바꿨습니다.', 'claimed' => '쿠폰을 받았습니다. 아래 쿠폰 목록에 있습니다.'];
        $key = self::field($request->query, 'done');
        $content = Html::notice(isset($done[$key]) ? $done[$key] : '') . self::problems($problems);
        if ($shown !== null) {
            $content .= '<section class="present" role="status"><h2>매장 직원에게 보여 주세요</h2><p class="code-big">' . Html::e($shown['code']) . '</p>'
                . '<p>' . Html::e(self::kst($shown['expiresAt'])) . ' 까지 쓸 수 있습니다 (한국 시간, 약 ' . (int)ceil($shown['expiresInSeconds'] / 60) . '분). '
                . '시간이 지나면 다시 만들어 주세요.</p>'
                . '<p><small>이 코드에는 개인정보가 없습니다. 직원이 코드를 확인하고 "사용 처리"를 눌러야 쿠폰이 쓰입니다. '
                . 'QR 코드는 게임의 쿠폰 화면에서 볼 수 있습니다.</small></p></section>';
        }
        if (!$profile['isMember']) {
            if (!$profile['membershipEnabled']) {
                return $this->page('내 CITY PASS', $content . self::soon('CITY PASS 는 준비 중입니다. 지금은 가입할 수 없습니다.'));
            }
            $content .= '<h2>CITY PASS 가입</h2><p>가입은 무료입니다. 게임 닉네임과 회원번호만 쓰고, 이름·전화번호·주소는 받지 않습니다.</p>'
                . $this->form('/city-pass/join',
                    $this->regionSelect('region', '관심 지역', $this->player['regionCode'], $this->player['regionCode'] === null ? '고르기' : null)
                    . '<p><small>전입신고한 지역이 기본으로 골라져 있습니다. 다른 지역을 골라도 됩니다 (실제 거주지를 확인하지 않습니다).</small></p>'
                    . Html::checkbox('agree', 'CITY PASS 이용 약관(' . $profile['termsVersion'] . ')에 동의합니다',
                        false, '쿠폰은 현금이나 게임머니로 바꿀 수 없고, 다른 사람에게 넘길 수 없습니다'), '가입하기');
            return $this->page('내 CITY PASS', $content);
        }
        $region = $profile['interestRegion'];
        $content .= '<section class="pass-card"><p class="label">CITY PASS</p><p class="name">' . Html::e($profile['nickname']) . '</p>'
            . '<p class="no">' . Html::e($profile['memberNo']) . '</p>'
            . '<p>관심 지역: ' . Html::e($region !== null ? $region['sidoName'] . ' ' . $region['name'] : '정하지 않음') . '</p>'
            . '<p>쓸 수 있는 쿠폰 ' . (int)$profile['coupons']['available'] . '장 · 쓴 쿠폰 ' . (int)$profile['coupons']['used'] . '장</p></section>';
        $coupons = $pass->coupons($this->player);
        if (!$coupons['couponsEnabled']) {
            $content .= self::soon('쿠폰은 준비 중입니다. 지금은 쿠폰을 받거나 쓸 수 없습니다.');
        }
        $names = ['issued' => '쓸 수 있음', 'redeemed' => '사용함', 'expired' => '기간 만료'];
        $content .= '<h2>내 쿠폰</h2>';
        if (!$coupons['coupons']) {
            $content .= '<p class="empty">받은 쿠폰이 없습니다. ' . $this->a('/city-pass/offers', '지역별 혜택') . '에서 받을 수 있습니다.</p>';
        }
        foreach ($coupons['coupons'] as $coupon) {
            $content .= '<article class="card"><h3>' . Html::e($coupon['offer']['title']) . ' <span class="tag strong">' . Html::e($coupon['offer']['discountText']) . '</span>'
                . ' <span class="tag">' . Html::e(isset($names[$coupon['status']]) ? $names[$coupon['status']] : $coupon['status']) . '</span></h3>'
                . '<p>' . Html::e($coupon['merchant']['name']) . ' · ' . Html::e($coupon['merchant']['categoryName']) . '</p>'
                . '<dl><dt>이용 조건</dt><dd>' . nl2br(Html::e($coupon['offer']['conditions'])) . '</dd>'
                . '<dt>유효기간</dt><dd>' . Html::e(self::kst($coupon['expiresAt'])) . ' 까지 (한국 시간)</dd>'
                . ($coupon['redeemedAt'] !== null ? '<dt>사용한 때</dt><dd>' . Html::e(self::kst($coupon['redeemedAt'])) . '</dd>' : '') . '</dl>'
                . '<ul class="branches">' . implode('', array_map([$this, 'branchHtml'], $coupon['branches'])) . '</ul>';
            if ($coupon['usable']) {
                $content .= $this->form('/city-pass/present', '<input type="hidden" name="coupon" value="' . Html::e($coupon['id']) . '">', '매장에 보여 줄 코드 만들기');
            }
            $content .= '</article>';
        }
        $rows = [];
        foreach ($pass->history($this->player)['history'] as $row) {
            $rows[] = [self::kst($row['redeemedAt']), $row['merchantName'], $row['branchName'], $row['offerTitle'], $row['discountText']];
        }
        $content .= '<h2>이용 내역</h2><div class="scroll">' . Html::table(['사용한 때 (한국 시간)', '상점', '지점', '혜택', '할인'], $rows, '아직 쓴 쿠폰이 없습니다.') . '</div>'
            . '<h2>관심 지역 바꾸기</h2>' . $this->form('/city-pass/region',
                $this->regionSelect('region', '관심 지역', $region !== null ? $region['code'] : '', $region === null ? '고르기' : null), '바꾸기');
        return $this->page('내 CITY PASS', $content, $problems ? 422 : 200);
    }

    // ── 제휴 문의 ───────────────────────────────────────────

    private function passPartnership()
    {
        $email = $this->app->env->get('PARTNER_CONTACT_EMAIL', '');
        $email = filter_var($email, FILTER_VALIDATE_EMAIL) !== false ? $email : '';
        $content = '<p class="lead">지역 상점과 함께 이용자에게 혜택을 드리는 제휴를 준비하고 있습니다.</p>'
            . '<h2>제휴가 진행되는 순서</h2><ol>'
            . '<li>문의: 상점 이름, 업종, 지역, 제안하시는 혜택을 알려 주세요.</li>'
            . '<li>협의와 계약: 혜택의 내용, 기간, 이용 조건을 정하고 제휴 계약을 맺습니다.</li>'
            . '<li>등록과 승인: 운영팀이 상점과 혜택을 등록하고, 등록한 사람이 아닌 다른 담당자가 계약 내용과 맞는지 확인해 승인합니다.</li>'
            . '<li>상점 계정: 쿠폰을 확인하는 화면에 로그인할 계정을 드립니다. 휴대전화나 PC 의 웹 브라우저로 씁니다.</li></ol>'
            . '<h2>제휴 상점에 대해</h2><ul>'
            . '<li>이용자 가운데에는 청소년이 있습니다. 주류, 담배, 유흥, 사행성 업종과 상품은 제휴할 수 없습니다.</li>'
            . '<li>상점에는 손님의 개인정보가 전달되지 않습니다. 쿠폰 코드가 유효한지와 혜택의 내용만 보입니다.</li>'
            . '<li>상점 계정은 자기 매장의 쿠폰과 이용 내역만 볼 수 있습니다.</li>'
            . '<li>계약이 끝나거나 중지되면 상점과 혜택은 바로 목록에서 내려갑니다.</li></ul><h2>문의처</h2>';
        $content .= $email !== '' ? '<p>이메일: ' . Html::e($email) . '</p>'
            : self::soon('제휴 문의 창구는 준비 중입니다. 창구가 열리면 이 페이지에 안내하겠습니다.');
        return $this->page('제휴 문의', $content, 200, 'CITY PASS 제휴 상점 안내');
    }
}
