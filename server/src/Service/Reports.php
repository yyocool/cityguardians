<?php
namespace CG\Service;

use CG\Infra\ApiError;
use CG\Infra\Wire;

/**
 * 클라이언트가 보낸 전투 보고의 형식 검사. 값의 옳고 그름은 규칙(Domain)이 판정하고,
 * 여기서는 형식이 맞지 않는 것(문자열로 온 숫자, 소수, 너무 긴 목록)을 걸러 낸다.
 */
final class Reports
{
    /** @return array[] [[enemyCode, count]] */
    public static function kills($value)
    {
        if ($value === null) {
            return [];
        }
        if (!is_array($value) || !Wire::isList($value) || count($value) > 32) {
            throw new ApiError(ApiError::INVALID_REQUEST, '처치 보고의 형식이 올바르지 않아요.');
        }
        $kills = [];
        foreach ($value as $row) {
            if (!is_array($row) || !isset($row['enemyCode'], $row['count']) || !is_string($row['enemyCode'])
                || strlen($row['enemyCode']) > 40 || !is_int($row['count'])) {
                throw new ApiError(ApiError::INVALID_REQUEST, '처치 보고의 형식이 올바르지 않아요.');
            }
            $kills[] = ['enemyCode' => $row['enemyCode'], 'count' => $row['count']];
        }
        return $kills;
    }

    public static function int(array $report, $key)
    {
        $value = isset($report[$key]) ? $report[$key] : 0;
        if (!is_int($value)) {
            throw new ApiError(ApiError::INVALID_REQUEST, '보고의 형식이 올바르지 않아요.');
        }
        return $value;
    }

    public static function result(array $report)
    {
        $value = isset($report['result']) ? $report['result'] : null;
        if (!is_string($value) || strlen($value) > 8) {
            throw new ApiError(ApiError::INVALID_REQUEST, '보고의 형식이 올바르지 않아요.');
        }
        return $value;
    }

    /** 기록 번호의 형식: 머리글자 + 영문 소문자와 숫자. */
    public static function sessionId($value, $prefix)
    {
        return is_string($value) && preg_match('/^' . $prefix . '[a-z0-9]{6,14}$/', $value) === 1;
    }
}
