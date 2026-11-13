using CityGuardians.Domain;
using CityGuardians.Gameplay;
using CityGuardians.Services;
using UnityEngine;
using UnityEngine.UI;

namespace CityGuardians.UI
{
    /// <summary>
    /// 방어전 화면 (docs/TOWER_DEFENSE.md 9장): 도시 방어 체력, 웨이브, 에너지와 결과 화면.
    /// DefenseBattle의 상태를 읽어 그리기만 한다. 방어전이 없으면 보이지 않는다.
    /// </summary>
    public sealed class DefenseHud : MonoBehaviour
    {
        private static readonly Color EnergyColor = new Color(0.45f, 0.95f, 1f);
        private static readonly Color BaseColor = new Color(0.36f, 0.78f, 1f);
        private static readonly Color BaseLow = new Color(0.95f, 0.36f, 0.30f);

        public bool StatusVisible => _status.activeSelf;
        public bool ResultVisible => _result.activeSelf;
        public string ResultTitle => _resultTitle.text;
        public string ResultBody => _resultBody.text;
        public string EnergyText => _energyText.text;
        public string WaveText => _waveText.text;

        private GameObject _status;
        private RectTransform _baseFill;
        private Text _baseText;
        private Text _waveText;
        private Text _energyText;
        private float _energyPulse;
        private int _shownEnergy = -1;

        private GameObject _result;
        private Image _resultBack;
        private Text _resultTitle;
        private Text _resultBody;
        private Text _resultKeys;

        /// <summary>상태 표시. 다른 화면 요소와 같은 층에 놓는다 (오른쪽 위, GC 표시 바로 아래).</summary>
        public void BuildStatus(Transform root)
        {
            Image status = UiFactory.Panel(root, "DefenseStatus", new Vector2(1f, 1f), new Vector2(-32f, -100f),
                new Vector2(400f, 178f), UiFactory.PanelColor);
            _status = status.gameObject;

            UiFactory.LabelAt(status.transform, "BaseLabel", new Vector2(0f, 1f), new Vector2(18f, -10f), new Vector2(220f, 34f),
                24, TextAnchor.MiddleLeft, UiFactory.TextColor).text = "도시 방어 체력";
            _baseText = UiFactory.LabelAt(status.transform, "BaseText", new Vector2(1f, 1f), new Vector2(-18f, -10f), new Vector2(140f, 34f),
                26, TextAnchor.MiddleRight, Color.white);
            _baseFill = UiFactory.Bar(status.transform, "Base", new Vector2(0.5f, 1f), new Vector2(0f, -48f), new Vector2(364f, 26f),
                new Color(0.10f, 0.10f, 0.14f, 0.9f), BaseColor);
            _waveText = UiFactory.LabelAt(status.transform, "Wave", new Vector2(0f, 1f), new Vector2(18f, -86f), new Vector2(364f, 36f),
                24, TextAnchor.MiddleLeft, UiFactory.TextColor);
            _waveText.horizontalOverflow = HorizontalWrapMode.Overflow;
            UiFactory.Panel(status.transform, "EnergyMark", new Vector2(0f, 0f), new Vector2(18f, 16f), new Vector2(26f, 26f), EnergyColor);
            _energyText = UiFactory.LabelAt(status.transform, "Energy", new Vector2(0f, 0f), new Vector2(54f, 8f), new Vector2(320f, 42f),
                30, TextAnchor.MiddleLeft, EnergyColor);

            _status.SetActive(false);
        }

        /// <summary>결과 화면. 선택 메뉴보다 위에 놓는다.</summary>
        public void BuildResult(Transform root)
        {
            Image result = UiFactory.Panel(root, "DefenseResult", new Vector2(0.5f, 0.5f), new Vector2(0f, 20f),
                new Vector2(1000f, 600f), new Color(0.06f, 0.10f, 0.18f, 0.97f));
            _result = result.gameObject;
            _resultBack = result;
            _resultTitle = UiFactory.LabelAt(result.transform, "Title", new Vector2(0.5f, 1f), new Vector2(0f, -40f), new Vector2(920f, 110f),
                76, TextAnchor.MiddleCenter, Color.white);
            _resultBody = UiFactory.LabelAt(result.transform, "Body", new Vector2(0.5f, 0.5f), new Vector2(0f, -20f), new Vector2(880f, 320f),
                36, TextAnchor.MiddleCenter, UiFactory.TextColor);
            _resultKeys = UiFactory.LabelAt(result.transform, "Keys", new Vector2(0.5f, 0f), new Vector2(0f, 26f), new Vector2(900f, 44f),
                30, TextAnchor.MiddleCenter, UiFactory.Accent);
            _result.SetActive(false);
        }

        /// <summary>Hud가 매 프레임 부른다.</summary>
        public void Refresh(float dt)
        {
            DefenseBattle battle = DefenseBattle.Current;
            bool showStatus = battle != null && battle.Phase != DefensePhase.Starting;
            if (_status.activeSelf != showStatus) _status.SetActive(showStatus);
            if (!showStatus)
            {
                if (_result.activeSelf) _result.SetActive(false);
                _shownEnergy = -1;
                return;
            }

            StageDef stage = battle.Stage;
            float ratio = stage.baseHp > 0 ? battle.BaseHp / (float)stage.baseHp : 0f;
            UiFactory.SetBar(_baseFill, ratio);
            // 방금 맞았으면 잠깐 붉게 깜빡인다.
            bool justHit = Time.time - battle.LastBaseHitTime < 0.35f;
            _baseFill.GetComponent<Image>().color = justHit || ratio <= 0.35f ? BaseLow : BaseColor;
            _baseText.text = $"{battle.BaseHp} / {stage.baseHp}";

            int waveCount = stage.waves.Count;
            _waveText.text = battle.Phase == DefensePhase.Wave
                ? $"웨이브 {battle.WaveNumber} / {waveCount}   남은 적 {battle.AliveCount + battle.PendingSpawns}"
                : battle.WaveNumber == 0 ? $"웨이브 준비 중   (모두 {waveCount}번)"
                : $"웨이브 {battle.WavesCleared} / {waveCount} 막아냄";

            if (_shownEnergy >= 0 && battle.Energy > _shownEnergy) _energyPulse = 1f;
            _shownEnergy = battle.Energy;
            _energyText.text = $"에너지  {battle.Energy}";
            _energyPulse = Mathf.MoveTowards(_energyPulse, 0f, dt * 4f);
            _energyText.transform.localScale = Vector3.one * (1f + _energyPulse * 0.12f);

            RefreshResult(battle);
        }

        private void RefreshResult(DefenseBattle battle)
        {
            bool failed = battle.Phase == DefensePhase.ReportFailed;
            // 웨이브를 시작하기 전에 돌아가는 경우에는 결과 화면이 없다.
            bool show = failed || (battle.IsOver && battle.Result != null && !(battle.Retreated && battle.WaveNumber == 0));
            if (_result.activeSelf != show) _result.SetActive(show);
            if (!show) return;

            if (failed)
            {
                _resultBack.color = new Color(0.10f, 0.10f, 0.14f, 0.97f);
                _resultTitle.text = "결과를 보내지 못했어요";
                _resultTitle.color = Color.white;
                _resultBody.text = (battle.ResultError ?? "") + "\n\n보상은 결과를 보낸 뒤에 받을 수 있어요.";
                _resultKeys.text = "R  다시 보내기";
                return;
            }

            BattleResultDto result = battle.Result;
            int waveCount = battle.Stage.waves.Count;
            bool won = battle.Phase == DefensePhase.Won;
            _resultBack.color = won ? new Color(0.05f, 0.16f, 0.22f, 0.97f) : new Color(0.14f, 0.11f, 0.16f, 0.97f);
            _resultKeys.text = "E  도시로 돌아가기";

            if (!result.accepted)
            {
                _resultTitle.text = "기록을 확인하지 못했어요";
                _resultTitle.color = Color.white;
                _resultBody.text = "전투 기록이 서버 기록과 맞지 않아\n이번에는 보상을 받지 못했어요.";
                return;
            }

            if (won)
            {
                _resultTitle.text = "방어 성공!";
                _resultTitle.color = UiFactory.Accent;
                string reward = result.firstWin
                    ? $"보상  +{result.rewardGc:N0} GC   (첫 승리 보너스 {battle.Stage.firstWinBonusGc:N0} GC 포함)"
                    : $"보상  +{result.rewardGc:N0} GC";
                string level = result.levelsGained > 0 ? $"\n레벨 업!  레벨 {result.level}" : "";
                // 주민이 이기면 소속 도시에 방어 포인트가 쌓인다.
                string cityName = result.defensePoints > 0 ? RegionCatalog.Default.ShortName(result.regionCode) : null;
                string city = cityName != null ? $"\n{cityName} 방어 포인트  +{result.defensePoints:N0}" : "";
                _resultBody.text = $"웨이브 {waveCount}번을 모두 막아냈어요!\n남은 도시 방어 체력  {battle.BaseHp} / {battle.Stage.baseHp}\n\n" +
                                   $"{reward}\n경험치  +{result.rewardXp}{level}{city}";
                return;
            }

            _resultTitle.text = battle.Retreated ? "후퇴했어요" : "방어선이 뚫렸어요";
            _resultTitle.color = Color.white;
            string consolation = result.rewardGc > 0
                ? $"참가 보상  +{result.rewardGc:N0} GC"
                : "웨이브를 하나라도 막아내면 참가 보상을 받아요.";
            _resultBody.text = $"막아낸 웨이브  {result.wavesCleared} / {waveCount}\n{consolation}\n\n" +
                               "타워를 더 세우거나 강화해서 다시 도전해 보세요!";
        }
    }
}
