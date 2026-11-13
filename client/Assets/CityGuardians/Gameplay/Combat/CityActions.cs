using System;
using System.Collections.Generic;
using CityGuardians.Domain;
using CityGuardians.Services;
using UnityEngine;

namespace CityGuardians.Gameplay
{
    /// <summary>
    /// 도시를 위해 하는 일들의 화면 흐름 (숫자 키로 고르는 선택 메뉴):
    /// 시설 복구, 도시 공용 방어시설 기금, 도시 간 지원, 지역 랭킹
    /// (docs/CITY_RISK_SYSTEM.md, docs/REGIONAL_COMPETITION.md).
    /// 여기서는 서버에 "무엇을 하겠다"만 알린다. 위험도와 기여도, 순위는 서버가 계산해 돌려준다.
    /// </summary>
    public static class CityActions
    {
        public const string MenuTag = "city";
        private static readonly int[] FundAmounts = { 100, 500, 1000 };

        /// <summary>도시 방어 현황 메뉴. 주민만 쓸 수 있다.</summary>
        public static void Open(Action onBack = null)
        {
            GameSession session = GameSession.Current;
            if (session == null) return;
            if (!session.IsResident)
            {
                GameEvents.ShowToast("전입신고를 하면 도시를 함께 지킬 수 있어요.");
                onBack?.Invoke();
                return;
            }

            string home = session.Me.regionCode;
            CityRiskService.GetDetail(home, result =>
            {
                if (GameSession.Current != session) return;
                if (!result.ok)
                {
                    GameEvents.ShowToast(string.IsNullOrEmpty(result.message) ? "도시 현황을 불러오지 못했어요." : result.message);
                    onBack?.Invoke();
                    return;
                }
                ShowMain(result.data, onBack);
            });
        }

        private static void ShowMain(CityDetailDto detail, Action onBack)
        {
            GameSession session = GameSession.Current;
            if (session == null || detail == null || detail.status == null) return;
            CityStatusDto city = detail.status;
            string name = RegionService.ShortName(city.regionCode) ?? "우리 도시";

            int damaged = 0;
            for (int i = 0; i < detail.facilities.Count; i++)
                if (detail.facilities[i].damage > 0) damaged++;

            string invasion = city.HasInvasion
                ? $"\n{InvasionKinds.DisplayName(city.invasionKind)} 진행 중 · 격퇴 {city.invasionProgress} / {city.invasionRequired}"
                : "";
            string body = $"{CityRiskService.StageBadge(city.stage)} · 위험도 {city.risk}   방어력 {city.defensePower:N0}   {RankText(city)}\n" +
                          $"내 기여도 {detail.myContribution:N0}{invasion}";

            Action reopen = () => Open(onBack);
            var options = new List<ChoiceOption>
            {
                new ChoiceOption
                {
                    label = "주요 시설 복구",
                    cost = damaged > 0 ? $"피해 {damaged}곳" : "피해 없음",
                    detail = damaged > 0 ? "GC를 내어 시설을 고치면 위험도가 내려가요." : "지금은 고칠 시설이 없어요.",
                    enabled = damaged > 0,
                    onChosen = () => ShowFacilities(detail, reopen),
                },
                new ChoiceOption
                {
                    label = "도시 공용 방어시설",
                    cost = $"{detail.structures.Count}종",
                    detail = "주민이 함께 기금을 모아 세우는 시설이에요. 내 것이 아니라 도시의 것이 돼요.",
                    enabled = detail.structures.Count > 0,
                    onChosen = () => ShowStructures(detail, reopen),
                },
                new ChoiceOption
                {
                    label = "다른 도시 지원",
                    cost = string.IsNullOrEmpty(session.SupportRegionCode) ? "" : $"{RegionService.ShortName(session.SupportRegionCode)} 지원 중",
                    detail = "긴급 단계인 가까운 도시의 공동 방어에 참여해요. 소속 도시는 바뀌지 않아요.",
                    onChosen = () => OpenSupport(reopen),
                },
                new ChoiceOption
                {
                    label = "지역 랭킹",
                    cost = RankText(city),
                    detail = "안전도, 방어 성공률, 기여도, 침공 대응, 협동 활동으로 매긴 순위예요.",
                    onChosen = () => OpenRankings(reopen),
                },
                new ChoiceOption
                {
                    label = "위험도 변경 이력",
                    cost = $"{detail.history.Count}건",
                    detail = "위험도가 언제, 왜, 누구 때문에 바뀌었는지 볼 수 있어요.",
                    enabled = detail.history.Count > 0,
                    onChosen = () => ShowHistory(detail, reopen),
                },
            };

            ChoiceMenu.Instance.Open($"{name} 방어 현황", body, options.ToArray());
            ChoiceMenu.Instance.Tag = MenuTag;
            ChoiceMenu.Instance.OnCancelled = onBack;
        }

        private static string RankText(CityStatusDto city) => city.rank > 0 ? $"전국 {city.rank}위" : "순위 없음";

        // ── 시설 복구 ───────────────────────────────────────────

        private static void ShowFacilities(CityDetailDto detail, Action onBack)
        {
            GameSession session = GameSession.Current;
            if (session == null) return;
            var options = new List<ChoiceOption>();
            for (int i = 0; i < detail.facilities.Count && options.Count < ChoiceMenu.MaxOptions; i++)
            {
                FacilityStatusDto facility = detail.facilities[i];
                bool hurt = facility.damage > 0;
                bool affordable = session.Me.gc >= facility.repairGc;
                options.Add(new ChoiceOption
                {
                    label = facility.name,
                    cost = hurt ? $"{facility.repairGc:N0} GC" : "정상",
                    detail = !hurt ? "피해가 없어요."
                        : !affordable ? $"GC가 모자라요. (지금 {session.Me.gc:N0} GC)"
                        : $"피해 {facility.damage} / {facility.maxDamage}단계. 한 단계를 고쳐요.",
                    enabled = hurt && affordable,
                    onChosen = () => Repair(facility, onBack),
                });
            }
            ChoiceMenu.Instance.Open("주요 시설 복구",
                "침입을 막지 못하면 시설이 피해를 입어요.\n고치는 데 쓴 GC는 돌려받지 않고, 도시 기여도로 남아요.", options.ToArray());
            ChoiceMenu.Instance.Tag = MenuTag;
            ChoiceMenu.Instance.OnCancelled = onBack;
        }

        private static void Repair(FacilityStatusDto facility, Action onDone)
        {
            GameSession session = GameSession.Current;
            if (session == null) return;
            CityRiskService.Repair(facility.code, result =>
            {
                if (GameSession.Current != session) return;
                if (result.ok)
                {
                    Sfx.Play(Sfx.Id.Build);
                    string line = RewardService.CityLine(result.data.city);
                    GameEvents.ShowToast($"{facility.name} 복구!  -{result.data.costGc:N0} GC" + (line != null ? "  ·  " + line : ""));
                }
                else
                {
                    ShowError(result.errorCode, result.message);
                }
                onDone?.Invoke();
            });
        }

        // ── 도시 공용 방어시설 ──────────────────────────────────

        private static void ShowStructures(CityDetailDto detail, Action onBack)
        {
            GameSession session = GameSession.Current;
            if (session == null) return;
            var options = new List<ChoiceOption>();
            for (int i = 0; i < detail.structures.Count && options.Count < ChoiceMenu.MaxOptions; i++)
            {
                CityStructureDto structure = detail.structures[i];
                bool max = structure.level >= structure.maxLevel;
                options.Add(new ChoiceOption
                {
                    label = structure.name,
                    cost = max ? $"{structure.level}단계 (완성)" : $"{structure.level}단계 · {structure.fundedGc:N0} / {structure.goalGc:N0} GC",
                    detail = max ? "더 올릴 수 없어요. 도시 방어력에 보태고 있어요."
                        : $"기금이 다 모이면 한 단계 올라가고 도시 방어력이 {structure.power:N0} 늘어요.",
                    enabled = !max,
                    onChosen = () => ShowFund(structure, onBack),
                });
            }
            ChoiceMenu.Instance.Open("도시 공용 방어시설",
                "주민이 낸 기금으로 세우는 도시의 시설이에요.\n개인 자산이 아니라서 팔거나 가져갈 수 없어요.", options.ToArray());
            ChoiceMenu.Instance.Tag = MenuTag;
            ChoiceMenu.Instance.OnCancelled = onBack;
        }

        private static void ShowFund(CityStructureDto structure, Action onBack)
        {
            GameSession session = GameSession.Current;
            if (session == null) return;
            long left = Math.Max(0, structure.goalGc - structure.fundedGc);
            var options = new List<ChoiceOption>();
            for (int i = 0; i < FundAmounts.Length; i++)
            {
                int amount = FundAmounts[i];
                bool affordable = session.Me.gc >= amount;
                options.Add(new ChoiceOption
                {
                    label = $"{amount:N0} GC",
                    detail = affordable ? "지갑에서 도시 기금으로 내요. 돌려받지 않아요." : $"지갑에 그만큼 없어요. (지금 {session.Me.gc:N0} GC)",
                    enabled = affordable,
                    onChosen = () => Fund(structure, amount, onBack),
                });
            }
            options.Add(new ChoiceOption { label = "그만두기", detail = "기금을 내지 않아요.", onChosen = () => onBack?.Invoke() });
            ChoiceMenu.Instance.Open($"{structure.name} 기금",
                $"다음 단계까지 {left:N0} GC 남았어요.\n기금은 선택이에요. 내지 않아도 모든 전투에 나설 수 있어요.", options.ToArray());
            ChoiceMenu.Instance.Tag = MenuTag;
            ChoiceMenu.Instance.OnCancelled = onBack;
        }

        private static void Fund(CityStructureDto structure, int amount, Action onDone)
        {
            GameSession session = GameSession.Current;
            if (session == null) return;
            CityRiskService.Fund(structure.code, amount, result =>
            {
                if (GameSession.Current != session) return;
                if (result.ok)
                {
                    Sfx.Play(Sfx.Id.Coin);
                    GameEvents.ShowToast($"{structure.name} 기금  -{result.data.costGc:N0} GC  ·  도시 기여도 +{result.data.contribution:N0}");
                }
                else
                {
                    ShowError(result.errorCode, result.message);
                }
                onDone?.Invoke();
            });
        }

        // ── 도시 간 지원 ────────────────────────────────────────

        public static void OpenSupport(Action onBack = null)
        {
            GameSession session = GameSession.Current;
            if (session == null) return;
            RegionService.RefreshSupport(() =>
            {
                if (GameSession.Current != session) return;
                SupportListDto list = RegionService.Support;
                if (list == null)
                {
                    GameEvents.ShowToast("지원 요청을 불러오지 못했어요.");
                    onBack?.Invoke();
                    return;
                }
                ShowSupport(list, onBack);
            });
        }

        private static void ShowSupport(SupportListDto list, Action onBack)
        {
            Action reopen = () => OpenSupport(onBack);
            var options = new List<ChoiceOption>();
            bool joined = !string.IsNullOrEmpty(list.joinedRequestId);
            if (joined)
            {
                options.Add(new ChoiceOption
                {
                    label = "지원 마치기",
                    cost = RegionService.ShortName(list.joinedRegionCode) ?? "",
                    detail = "우리 도시로 돌아와요. 지금까지 쌓은 지원 기여도는 그대로 남아요.",
                    onChosen = () => RegionService.LeaveSupport(result =>
                    {
                        if (result.ok) GameEvents.ShowToast("지원을 마치고 우리 도시로 돌아왔어요.");
                        else ShowError(result.errorCode, result.message);
                        reopen();
                    }),
                });
            }
            for (int i = 0; i < list.requests.Count && options.Count < ChoiceMenu.MaxOptions; i++)
            {
                SupportRowDto row = list.requests[i];
                if (row.requestId == list.joinedRequestId) continue;
                string name = RegionService.ShortName(row.regionCode) ?? row.regionCode;
                options.Add(new ChoiceOption
                {
                    label = $"{name} 지원",
                    cost = $"{CityRiskService.StageBadge(row.stage)} {row.risk}",
                    detail = row.canJoin ? "이 도시의 공동 방어에 참여해요. 이긴 전투가 그 도시에 반영돼요."
                        : SupportReasons.Message(row.reason),
                    enabled = row.canJoin,
                    onChosen = () => RegionService.JoinSupport(row.requestId, result =>
                    {
                        if (result.ok)
                        {
                            Sfx.Play(Sfx.Id.Alarm, 0.5f);
                            GameEvents.ShowToast($"{name} 지원 출동! 방어전과 긴급 출동에서 이기면 {name}에 반영돼요.");
                        }
                        else ShowError(result.errorCode, result.message);
                        reopen();
                    }),
                });
            }

            if (options.Count == 0)
            {
                GameEvents.ShowToast("지금은 지원을 요청한 가까운 도시가 없어요.");
                onBack?.Invoke();
                return;
            }
            ChoiceMenu.Instance.Open("다른 도시 지원",
                "위험도가 긴급 단계인 가까운 도시가 도움을 요청했어요.\n지원 중에도 소속 도시는 그대로예요." +
                (list.mySupportPoints > 0 ? $"   내 지원 기여도 {list.mySupportPoints:N0}" : ""),
                options.ToArray());
            ChoiceMenu.Instance.Tag = MenuTag;
            ChoiceMenu.Instance.OnCancelled = onBack;
        }

        // ── 지역 랭킹과 이력 (안내판) ───────────────────────────

        public static void OpenRankings(Action onDone = null)
        {
            GameSession session = GameSession.Current;
            if (session == null) return;
            RegionService.RefreshRankings(() =>
            {
                if (GameSession.Current != session) return;
                RankingsDto rankings = RegionService.Rankings;
                if (rankings == null)
                {
                    GameEvents.ShowToast("랭킹을 불러오지 못했어요.");
                    onDone?.Invoke();
                    return;
                }
                // 안내판은 E로 닫는다. 닫으면 앞의 메뉴로 돌아간다.
                DialogueRunner.Instance.ShowPanel("지역 랭킹", RankingText(rankings, 8), false, onDone);
            });
        }

        /// <summary>랭킹 표를 글로 만든다. 가디언이 지키는 도시만 순위에 든다 (NPC만 지키는 도시는 빠진다).</summary>
        public static string RankingText(RankingsDto rankings, int rows)
        {
            var lines = new List<string>
            {
                $"가디언이 지키는 도시 {rankings.playerCities:N0}곳 · 전국 {rankings.totalCities:N0}곳",
                "순위   도시   종합   안전도 · 방어 · 기여 · 대응 · 협동",
            };
            bool homeShown = false;
            for (int i = 0; i < rankings.rows.Count && i < rows; i++)
            {
                RankingRowDto row = rankings.rows[i];
                bool home = rankings.home != null && row.regionCode == rankings.home.regionCode;
                homeShown |= home;
                lines.Add(RankLine(row, home));
            }
            if (!homeShown && rankings.home != null)
            {
                lines.Add("···");
                lines.Add(RankLine(rankings.home, true));
            }
            if (rankings.rows.Count == 0) lines.Add("아직 순위에 든 도시가 없어요.");
            lines.Add("점수는 활성 가디언 수와 침공 난이도로 보정해요. 큰 도시라고 유리하지 않아요.");
            return string.Join("\n", lines);
        }

        private static string RankLine(RankingRowDto row, bool home)
        {
            string name = RegionService.ShortName(row.regionCode) ?? row.regionCode;
            string rank = row.rank > 0 ? $"{row.rank}위" : "—";
            return $"{(home ? "▶ " : "")}{rank}  {name}  {row.score / 100f:0.0}점   " +
                   $"{row.safety / 100f:0} · {row.successRate / 100f:0} · {row.contribution / 100f:0} · {row.response / 100f:0} · {row.cooperation / 100f:0}" +
                   $"   {RiskStages.Icon(row.stage)}{RiskStages.DisplayName(row.stage)}";
        }

        private static void ShowHistory(CityDetailDto detail, Action onDone)
        {
            DialogueRunner.Instance.ShowPanel("위험도 변경 이력", HistoryText(detail, 9), false, onDone);
        }

        /// <summary>최근 이력을 글로 만든다. 누가 바꿨는지(가디언·NPC 수비대·시스템)를 함께 적는다.</summary>
        public static string HistoryText(CityDetailDto detail, int rows)
        {
            var lines = new List<string>();
            for (int i = 0; i < detail.history.Count && i < rows; i++)
            {
                RiskHistoryEntry entry = detail.history[i];
                string sign = entry.deltaCenti > 0 ? "+" : "";
                lines.Add($"{ShortTime(entry.at)}  {RiskReasons.DisplayName(entry.reason)} ({RiskActors.DisplayName(entry.actor)})  " +
                          $"{sign}{entry.deltaCenti / 100f:0.##}  →  {CityRiskRules.ToPoints(entry.afterCenti)}");
            }
            if (lines.Count == 0) lines.Add("아직 기록이 없어요.");
            return string.Join("\n", lines);
        }

        /// <summary>"2026-11-13T09:30:00Z" → "11-13 09:30". 형식이 다르면 그대로 돌려준다.</summary>
        private static string ShortTime(string iso)
        {
            if (string.IsNullOrEmpty(iso) || iso.Length < 16 || iso[10] != 'T') return iso ?? "";
            return iso.Substring(5, 5) + " " + iso.Substring(11, 5);
        }

        private static void ShowError(string code, string message)
        {
            if (code == ApiErrors.InsufficientFunds) GameEvents.ShowToast("GC가 모자라요.");
            else if (!string.IsNullOrEmpty(message)) GameEvents.ShowToast(message);
            else GameEvents.ShowToast("처리하지 못했어요. 잠시 뒤에 다시 시도해 주세요.");
        }
    }
}
