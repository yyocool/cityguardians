<?php
namespace CG\Admin;

/**
 * 관리자 화면의 HTML 조각.
 *
 * 화면에 나가는 값은 모두 e() 로 이스케이프한다. 플레이어가 정한 닉네임이나 관리자가 적은 사유가
 * 관리자 화면에서 스크립트로 실행되어서는 안 된다. 표의 칸에 값을 넣으면 자동으로 이스케이프되고,
 * 이미 만든 HTML 을 넣을 때에만 Html::raw() 로 감싼다.
 * 외부 스크립트·글꼴·이미지는 쓰지 않는다. 스크립트는 아예 없다.
 */
final class Html
{
    /** @var string */
    public $html;

    private function __construct($html)
    {
        $this->html = $html;
    }

    /** 이미 이스케이프를 마친 HTML. */
    public static function raw($html)
    {
        return new self((string)$html);
    }

    public static function e($value)
    {
        if ($value instanceof self) {
            return $value->html;
        }
        if ($value === null) {
            return '';
        }
        if (is_bool($value)) {
            return $value ? '예' : '아니오';
        }
        return htmlspecialchars((string)$value, ENT_QUOTES | ENT_SUBSTITUTE, 'UTF-8');
    }

    public static function link($href, $text)
    {
        return self::raw('<a href="' . self::e($href) . '">' . self::e($text) . '</a>');
    }

    /**
     * @param string[] $headers
     * @param array[] $rows 칸마다 문자열(이스케이프된다) 또는 Html::raw()
     */
    public static function table(array $headers, array $rows, $empty = '내용이 없습니다.')
    {
        $out = '<table><thead><tr>';
        foreach ($headers as $header) {
            $out .= '<th>' . self::e($header) . '</th>';
        }
        $out .= '</tr></thead><tbody>';
        if (!$rows) {
            $out .= '<tr><td class="empty" colspan="' . count($headers) . '">' . self::e($empty) . '</td></tr>';
        }
        foreach ($rows as $row) {
            $out .= '<tr>';
            foreach ($row as $cell) {
                $out .= '<td>' . self::e($cell) . '</td>';
            }
            $out .= '</tr>';
        }
        return $out . '</tbody></table>';
    }

    /** 폼. CSRF 토큰이 들어간다. $inner 는 이미 만든 HTML 이다. */
    public static function form($action, $csrf, $inner, $submit = '저장')
    {
        return '<form method="post" action="' . self::e($action) . '">'
            . '<input type="hidden" name="_csrf" value="' . self::e($csrf) . '">'
            . $inner
            . '<p><button type="submit">' . self::e($submit) . '</button></p></form>';
    }

    public static function input($name, $label, $value, $help = '', $type = 'text')
    {
        return '<p class="field"><label>' . self::e($label)
            . ' <input type="' . self::e($type) . '" name="' . self::e($name) . '" value="' . self::e($value) . '"'
            . ($type === 'password' ? ' autocomplete="off"' : '') . '></label>'
            . ($help !== '' ? ' <small>' . self::e($help) . '</small>' : '') . '</p>';
    }

    public static function textarea($name, $label, $value, $help = '')
    {
        return '<p class="field"><label>' . self::e($label) . '<br><textarea name="' . self::e($name) . '" rows="8" cols="100">'
            . self::e($value) . '</textarea></label>' . ($help !== '' ? '<br><small>' . self::e($help) . '</small>' : '') . '</p>';
    }

    public static function checkbox($name, $label, $checked, $help = '')
    {
        return '<p class="field"><label><input type="checkbox" name="' . self::e($name) . '" value="1"' . ($checked ? ' checked' : '')
            . '> ' . self::e($label) . '</label>' . ($help !== '' ? ' <small>' . self::e($help) . '</small>' : '') . '</p>';
    }

    public static function select($name, $label, array $options, $selected)
    {
        $out = '<p class="field"><label>' . self::e($label) . ' <select name="' . self::e($name) . '">';
        foreach ($options as $value => $text) {
            $out .= '<option value="' . self::e($value) . '"' . ((string)$value === (string)$selected ? ' selected' : '') . '>'
                . self::e($text) . '</option>';
        }
        return $out . '</select></label></p>';
    }

    public static function reason()
    {
        return self::input('reason', '변경 사유 (필수)', '', '누가 왜 바꿨는지 이력에 남습니다');
    }

    /** @param string[] $problems */
    public static function problems(array $problems)
    {
        if (!$problems) {
            return '';
        }
        $out = '<div class="problems" role="alert"><strong>저장하지 않았습니다.</strong><ul>';
        foreach ($problems as $problem) {
            $out .= '<li>' . self::e($problem) . '</li>';
        }
        return $out . '</ul></div>';
    }

    public static function notice($text)
    {
        return $text === '' ? '' : '<div class="notice" role="status">' . self::e($text) . '</div>';
    }
}
