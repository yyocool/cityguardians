using System;
using System.Collections.Generic;
using CityGuardians.Inputs;
using UnityEngine;

namespace CityGuardians.Gameplay
{
    [Serializable]
    public sealed class NpcInfo
    {
        public string code;
        public string name;
    }

    /// <summary>
    /// 대화 한 묶음. 튜토리얼 단계(minStep~maxStep)에 따라 같은 NPC가 다른 말을 한다.
    /// </summary>
    [Serializable]
    public sealed class DialogueEntry
    {
        public string npc;
        public int minStep;
        public int maxStep;
        /// <summary>누구에게 하는 말인가: "stranger"(이방인), "resident"(주민). 비어 있으면 누구에게나.</summary>
        public string when;
        public string[] lines;
        /// <summary>대화가 끝나면 옮겨 갈 튜토리얼 단계. 0이면 옮기지 않는다.</summary>
        public int setStep;
        /// <summary>대화가 끝나면 실행할 일의 이름. 비어 있으면 없음.</summary>
        public string action;
    }

    /// <summary>화면 가운데에 띄우는 안내판 한 장 (컴퓨터 화면).</summary>
    [Serializable]
    public sealed class PanelInfo
    {
        public string code;
        public string title;
        public string body;
        public bool alert;
    }

    [Serializable]
    public sealed class DialogueFile
    {
        public NpcInfo[] npcs = new NpcInfo[0];
        public PanelInfo[] panels = new PanelInfo[0];
        public DialogueEntry[] entries = new DialogueEntry[0];
    }

    /// <summary>대화 데이터 (Resources/Data/dialogues.json). 글을 코드에서 떼어 놓아 고치기 쉽게 한다.</summary>
    public static class DialogueDb
    {
        private static DialogueFile _file;

        private static DialogueFile File
        {
            get
            {
                if (_file != null) return _file;
                var asset = Resources.Load<TextAsset>("Data/dialogues");
                if (asset == null)
                {
                    Debug.LogError("대화 데이터 Resources/Data/dialogues.json 을 찾을 수 없습니다.");
                    _file = new DialogueFile();
                }
                else
                {
                    _file = JsonUtility.FromJson<DialogueFile>(asset.text);
                }
                return _file;
            }
        }

        public static string NameOf(string npcCode)
        {
            foreach (NpcInfo info in File.npcs)
                if (info.code == npcCode) return info.name;
            return npcCode;
        }

        /// <summary>그 NPC가 지금 할 말. 위에서부터 처음 맞는 것을 고른다. 없으면 null.</summary>
        /// <param name="residency">플레이어의 소속 상태 (ResidencyStatus). null이면 조건이 붙은 대화는 고르지 않는다.</param>
        public static DialogueEntry Find(string npcCode, int step, string residency = null)
        {
            foreach (DialogueEntry entry in File.entries)
            {
                if (entry.npc != npcCode || step < entry.minStep || step > entry.maxStep) continue;
                if (!string.IsNullOrEmpty(entry.when) && entry.when != residency) continue;
                return entry;
            }
            return null;
        }

        public static PanelInfo Panel(string code)
        {
            foreach (PanelInfo panel in File.panels)
                if (panel.code == code) return panel;
            return null;
        }

        public static IReadOnlyList<DialogueEntry> AllEntries => File.entries;
        public static IReadOnlyList<NpcInfo> AllNpcs => File.npcs;
    }

    /// <summary>
    /// 대화창과 안내판을 진행한다. 열려 있는 동안 플레이어 조작을 잠그고, E(또는 Space)로 넘긴다.
    /// </summary>
    public sealed class DialogueRunner : MonoBehaviour
    {
        public static DialogueRunner Instance { get; private set; }

        public bool IsOpen => _lines != null || _panelOpen;

        private string _speaker;
        private IList<string> _lines;
        private int _index;
        private Action _onDone;
        private BlockCharacterAnimator _talker;
        private bool _panelOpen;
        private int _openedFrame;

        private void Awake() => Instance = this;

        public void Show(string speaker, IList<string> lines, Action onDone = null, BlockCharacterAnimator talker = null)
        {
            if (lines == null || lines.Count == 0) { onDone?.Invoke(); return; }
            CloseSilently();
            _speaker = speaker;
            _lines = lines;
            _index = 0;
            _onDone = onDone;
            _talker = talker;
            _openedFrame = Time.frameCount;
            GameState.Lock(GameState.LockDialogue);
            if (_talker != null) _talker.talking = true;
            ShowCurrent();
        }

        public void ShowPanel(string title, string body, bool alert, Action onDone = null)
        {
            CloseSilently();
            _panelOpen = true;
            _onDone = onDone;
            _openedFrame = Time.frameCount;
            GameState.Lock(GameState.LockPanel);
            GameEvents.OpenPanel(title, body, alert);
        }

        /// <summary>장면이 바뀔 때처럼, 끝났다는 알림 없이 닫는다.</summary>
        public void CloseSilently()
        {
            if (_lines != null)
            {
                _lines = null;
                if (_talker != null) _talker.StopTalking();
                _talker = null;
                GameEvents.CloseDialogue();
                GameState.Unlock(GameState.LockDialogue);
            }
            if (_panelOpen)
            {
                _panelOpen = false;
                GameEvents.ClosePanel();
                GameState.Unlock(GameState.LockPanel);
            }
            _onDone = null;
        }

        private void Update()
        {
            if (!IsOpen || GameState.IsPaused) return;
            // 대화를 연 바로 그 입력으로 첫 줄을 넘기지 않는다.
            if (Time.frameCount <= _openedFrame) return;

            IPlayerInput input = PlayerInputHub.Current;
            if (!input.InteractPressed && !input.JumpPressed) return;

            if (_panelOpen)
            {
                Finish();
                return;
            }

            _index++;
            if (_index >= _lines.Count) Finish();
            else ShowCurrent();
        }

        private void ShowCurrent()
        {
            Sfx.Play(Sfx.Id.Talk);
            GameEvents.ShowDialogueLine(_speaker, _lines[_index]);
        }

        private void Finish()
        {
            Action done = _onDone;
            CloseSilently();
            done?.Invoke();
        }
    }
}
