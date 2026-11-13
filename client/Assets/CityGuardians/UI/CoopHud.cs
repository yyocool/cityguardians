using System.Text;
using CityGuardians.Domain;
using CityGuardians.Gameplay;
using CityGuardians.Services;
using UnityEngine;
using UnityEngine.UI;

namespace CityGuardians.UI
{
    /// <summary>
    /// 협동 방어전 화면 (docs/COOP_DEFENSE_SYSTEM.md 7장): 관문 내구도, 웨이브, 공동 예산, 대원 목록과 결과 화면.
    /// CoopBattle 이 서버에서 받은 상태를 읽어 그리기만 한다. 협동 방어전이 아니면 보이지 않는다.
    /// 미니맵(오른쪽 위) 바로 아래에 놓인다.
    /// </summary>
    public sealed class CoopHud : MonoBehaviour
    {
        private const int MaxRows = 8;
        private const float Width = 292f;

        private static readonly Color EnergyColor = new Color(0.45f, 0.95f, 1f);
        private static readonly Color GateColor = new Color(0.36f, 0.78f, 1f);
        private static readonly Color GateLow = new Color(0.95f, 0.36f, 0.30f);
        private static readonly Color Dim = new Color(0.62f, 0.64f, 0.68f);

        public bool StatusVisible => _status.activeSelf;
        public bool ResultVisible => _result.activeSelf;
        public string ResultTitle => _resultTitle.text;
        public string ResultBody => _resultBody.text;
        public string WaveText => _waveText.text;
        public string EnergyText => _energyText.text;

        private GameObject _status;
        private RectTransform _gateFill;
        private Text _gateText;
        private Text _waveText;
        private Text _energyText;
        private Text _modeText;
        private GameObject _team;
        private readonly Text[] _rows = new Text[MaxRows];

        private GameObject _result;
        private Image _resultBack;
        private Text _resultTitle;
        private Text _resultBody;
        private Text _resultKeys;

        public void BuildStatus(Transform root)
        {
            Image status = UiFactory.Panel(root, "CoopStatus", new Vector2(1f, 1f), new Vector2(-32f, -438f), new Vector2(Width, 156f),
                UiFactory.PanelColor);
            _status = status.gameObject;
            UiFactory.LabelAt(status.transform, "GateLabel", new Vector2(0f, 1f), new Vector2(12f, -6f), new Vector2(150f, 30f),
                22, TextAnchor.MiddleLeft, UiFactory.TextColor).text = "관문 내구도";
            _gateText = UiFactory.LabelAt(status.transform, "GateText", new Vector2(1f, 1f), new Vector2(-12f, -6f), new Vector2(120f, 30f),
                22, TextAnchor.MiddleRight, Color.white);
            _gateFill = UiFactory.Bar(status.transform, "Gate", new Vector2(0.5f, 1f), new Vector2(0f, -38f), new Vector2(Width - 24f, 22f),
                new Color(0.10f, 0.10f, 0.14f, 0.9f), GateColor);
            _waveText = UiFactory.LabelAt(status.transform, "Wave", new Vector2(0f, 1f), new Vector2(12f, -64f), new Vector2(Width - 24f, 30f),
                20, TextAnchor.MiddleLeft, UiFactory.TextColor);
            _waveText.horizontalOverflow = HorizontalWrapMode.Overflow;
            _energyText = UiFactory.LabelAt(status.transform, "Energy", new Vector2(0f, 1f), new Vector2(12f, -94f), new Vector2(Width - 24f, 32f),
                26, TextAnchor.MiddleLeft, EnergyColor);
            _modeText = UiFactory.LabelAt(status.transform, "Mode", new Vector2(0f, 1f), new Vector2(12f, -126f), new Vector2(Width - 24f, 26f),
                18, TextAnchor.MiddleLeft, UiFactory.Accent);
            _modeText.horizontalOverflow = HorizontalWrapMode.Overflow;
            _status.SetActive(false);

            Image team = UiFactory.Panel(root, "CoopTeam", new Vector2(1f, 1f), new Vector2(-32f, -606f), new Vector2(Width, 34f + MaxRows * 26f),
                UiFactory.PanelColor);
            _team = team.gameObject;
            UiFactory.LabelAt(team.transform, "Head", new Vector2(0f, 1f), new Vector2(12f, -4f), new Vector2(Width - 24f, 28f),
                20, TextAnchor.MiddleLeft, UiFactory.Accent).text = "대원   F  팀 메뉴";
            for (int i = 0; i < MaxRows; i++)
            {
                _rows[i] = UiFactory.LabelAt(team.transform, "Row" + i, new Vector2(0f, 1f), new Vector2(12f, -32f - i * 26f),
                    new Vector2(Width - 24f, 26f), 18, TextAnchor.MiddleLeft, UiFactory.TextColor);
                _rows[i].horizontalOverflow = HorizontalWrapMode.Overflow;
            }
            _team.SetActive(false);
        }

        /// <summary>결과 화면. 선택 메뉴보다 위에 놓는다.</summary>
        public void BuildResult(Transform root)
        {
            Image result = UiFactory.Panel(root, "CoopResult", new Vector2(0.5f, 0.5f), new Vector2(0f, 20f),
                new Vector2(1100f, 700f), new Color(0.06f, 0.10f, 0.18f, 0.97f));
            _result = result.gameObject;
            _resultBack = result;
            _resultTitle = UiFactory.LabelAt(result.transform, "Title", new Vector2(0.5f, 1f), new Vector2(0f, -30f), new Vector2(1000f, 96f),
                68, TextAnchor.MiddleCenter, Color.white);
            _resultBody = UiFactory.LabelAt(result.transform, "Body", new Vector2(0.5f, 0.5f), new Vector2(0f, -24f), new Vector2(1000f, 460f),
                26, TextAnchor.MiddleCenter, UiFactory.TextColor);
            _resultBody.horizontalOverflow = HorizontalWrapMode.Overflow;
            _resultKeys = UiFactory.LabelAt(result.transform, "Keys", new Vector2(0.5f, 0f), new Vector2(0f, 22f), new Vector2(1000f, 44f),
                30, TextAnchor.MiddleCenter, UiFactory.Accent);
            _result.SetActive(false);
        }

        /// <summary>Hud 가 매 프레임 부른다.</summary>
        public void Refresh(float dt)
        {
            CoopBattle battle = CoopBattle.Current;
            CoopSnapshot snapshot = battle != null ? battle.Snapshot : null;
            bool show = snapshot != null;
            if (_status.activeSelf != show) _status.SetActive(show);
            if (_team.activeSelf != show) _team.SetActive(show);
            if (!show)
            {
                if (_result.activeSelf) _result.SetActive(false);
                return;
            }

            ICoopLink link = battle.Link;
            float ratio = snapshot.gateMaxHp > 0 ? snapshot.gateHp / (float)snapshot.gateMaxHp : 0f;
            UiFactory.SetBar(_gateFill, ratio);
            _gateFill.GetComponent<Image>().color = ratio <= 0.35f ? GateLow : GateColor;
            _gateText.text = $"{snapshot.gateHp} / {snapshot.gateMaxHp}";

            _waveText.text = snapshot.IsLobby ? $"대기실   대원 {snapshot.ConnectedCount} / {link.MaxPlayers}명"
                : snapshot.phase == CoopPhaseNames.Wave ? $"웨이브 {snapshot.wave} / {snapshot.waveCount}   적 {snapshot.enemies.Count}"
                : snapshot.phase == CoopPhaseNames.Rest ? $"다음 웨이브까지 {Mathf.CeilToInt(snapshot.restMs / 1000f)}초   ({snapshot.wavesCleared} / {snapshot.waveCount})"
                : $"웨이브 {snapshot.wavesCleared} / {snapshot.waveCount} 막아냄";
            _energyText.text = $"공동 예산  {snapshot.energy}";
            _modeText.text = link.Practice ? "연습전 · 보상과 기록이 없어요" : $"{link.RegionName} · {link.ZoneName}";

            for (int i = 0; i < MaxRows; i++)
            {
                bool used = i < snapshot.players.Count;
                if (_rows[i].gameObject.activeSelf != used) _rows[i].gameObject.SetActive(used);
                if (!used) continue;
                CoopPlayerView player = snapshot.players[i];
                string mark = player.pid == link.MyPid ? "▶ " : player.host ? "★ " : "   ";
                string role = string.IsNullOrEmpty(player.role) ? "" : "  " + CoopTexts.RoleName(link.Roles, player.role);
                _rows[i].text = $"{mark}{Short(player.name, 10)}{role}   처치 {player.kills}";
                _rows[i].color = !player.connected ? Dim : player.pid == link.MyPid ? Color.white : UiFactory.TextColor;
            }

            RefreshResult(battle, snapshot);
        }

        private static string Short(string name, int max)
        {
            if (string.IsNullOrEmpty(name)) return "대원";
            return name.Length <= max ? name : name.Substring(0, max - 1) + "…";
        }

        private void RefreshResult(CoopBattle battle, CoopSnapshot snapshot)
        {
            CoopFinish finish = battle.Link.Finish;
            bool show = finish != null && battle.Link.State == CoopLinkStates.Finished;
            if (_result.activeSelf != show) _result.SetActive(show);
            if (!show) return;

            bool won = finish.Won;
            _resultBack.color = won ? new Color(0.05f, 0.16f, 0.22f, 0.97f) : new Color(0.14f, 0.11f, 0.16f, 0.97f);
            _resultTitle.color = won ? UiFactory.Accent : Color.white;
            _resultTitle.text = won ? "방어 성공!"
                : finish.closeReason == CoopCloseReasons.GateDestroyed ? "관문이 뚫렸어요"
                : finish.closeReason == CoopCloseReasons.Timeout ? "시간이 다 됐어요"
                : "방어전이 끝났어요";
            _resultKeys.text = "E  도시로 돌아가기";

            var body = new StringBuilder();
            body.Append($"막아낸 웨이브 {finish.wavesCleared} / {snapshot.waveCount}     남은 관문 내구도 {finish.gateHpLeft}     ")
                .Append($"걸린 시간 {finish.durationSeconds / 60}분 {finish.durationSeconds % 60:00}초\n\n");

            // 대원별 활약 (서버가 센 값)
            for (int i = 0; i < snapshot.players.Count && i < MaxRows; i++)
            {
                CoopPlayerView player = snapshot.players[i];
                string role = string.IsNullOrEmpty(player.role) ? "" : " (" + CoopTexts.RoleName(battle.Link.Roles, player.role) + ")";
                body.Append($"{Short(player.name, 12)}{role}   처치 {player.kills} · 설치 {player.towersBuilt} · 강화 {player.towersUpgraded} · 수리 {player.repairs}\n");
            }
            body.Append('\n').Append(RewardText(battle, finish));
            _resultBody.text = body.ToString();
        }

        /// <summary>보상 안내. 보상은 게임 서버가 정한다. 여기서는 받은 값을 보여 주기만 한다.</summary>
        private static string RewardText(CoopBattle battle, CoopFinish finish)
        {
            if (finish.practice) return "연습전이라 보상과 기록은 없어요.\n온라인 방어전에서는 팀의 결과와 내 참여에 따라 보상을 받아요.";

            CoopResultDto record = battle.Record;
            if (record != null && record.result != null)
            {
                CoopMyResultDto mine = record.result;
                // 받지 못했으면 서버가 이유를 알려 준다 (참여 시간이 짧음, 오늘의 횟수를 다 씀, 결과가 인정되지 않음).
                if (!string.IsNullOrEmpty(mine.rewardNote))
                    return string.IsNullOrEmpty(mine.rewardMessage) ? "이번에는 보상을 받지 못했어요." : mine.rewardMessage;
                return $"보상  +{mine.rewardGc:N0} GC     경험치  +{mine.rewardXp}";
            }
            if (!finish.recordKnown) return "결과를 기록하고 있어요…";
            if (finish.pending) return "결과를 게임 서버에 보내지 못했어요. 다시 보내는 중이에요.\n보상은 기록이 도착하면 자동으로 들어와요.";
            if (!finish.recorded || !finish.accepted) return "전투 기록이 확인되지 않아 이번에는 보상이 없어요.";
            return "보상을 확인하고 있어요…";
        }
    }
}
