using System;
using System.Collections.Generic;
using CityGuardians.Inputs;
using UnityEngine;

namespace CityGuardians.Gameplay
{
    /// <summary>선택 메뉴의 한 줄.</summary>
    public sealed class ChoiceOption
    {
        public string label;
        /// <summary>오른쪽에 보이는 짧은 값. 예: "에너지 100".</summary>
        public string cost;
        /// <summary>아래 줄의 설명.</summary>
        public string detail;
        /// <summary>고를 수 있는가. 고를 수 없으면 흐리게 보이고 눌러도 반응하지 않는다.</summary>
        public bool enabled = true;
        public Action onChosen;
    }

    /// <summary>
    /// 숫자 키로 고르는 작은 선택 메뉴. 타워 고르기, 출동 확인, 해금 확인처럼
    /// "무엇을 할지 한 번 묻는" 곳에 쓴다. 숫자 키(1~6)로 고르고 E로 닫는다.
    /// 열려 있는 동안 플레이어 조작은 잠기지만 게임은 멈추지 않는다.
    /// 화면은 UI가 이 객체의 상태를 읽어 그린다.
    /// </summary>
    public sealed class ChoiceMenu : MonoBehaviour
    {
        public const int MaxOptions = 6;

        public static ChoiceMenu Instance { get; private set; }

        public bool IsOpen { get; private set; }
        public string Title { get; private set; }
        public string Body { get; private set; }
        public IReadOnlyList<ChoiceOption> Options => _options;
        /// <summary>내용이 바뀔 때마다 1씩 오른다. 화면이 다시 그릴지 판단하는 데 쓴다.</summary>
        public int Version { get; private set; }
        /// <summary>누가 연 메뉴인지 표시해 두는 이름표 (가방, 상점). Open을 부른 쪽이 붙이고, 닫히면 지워진다.</summary>
        public string Tag { get; set; }
        /// <summary>
        /// E로 닫았을 때 할 일. 하위 메뉴에서 "뒤로"를 만드는 데 쓴다. Open을 부른 쪽이 붙이고, 닫히면 지워진다.
        /// 선택지를 골랐거나 다른 곳에서 Close를 불렀을 때는 불리지 않는다.
        /// </summary>
        public Action OnCancelled { get; set; }

        private readonly List<ChoiceOption> _options = new List<ChoiceOption>();
        private int _openedFrame;

        private void Awake() => Instance = this;

        public void Open(string title, string body, params ChoiceOption[] options)
        {
            if (options == null || options.Length == 0 || options.Length > MaxOptions)
                throw new ArgumentException($"선택지는 1~{MaxOptions}개여야 합니다.", nameof(options));

            _options.Clear();
            _options.AddRange(options);
            Title = title;
            Body = body;
            Tag = null;
            OnCancelled = null;
            IsOpen = true;
            Version++;
            _openedFrame = Time.frameCount;
            GameState.Lock(GameState.LockChoice);
        }

        public void Close()
        {
            if (!IsOpen) return;
            IsOpen = false;
            Version++;
            Tag = null;
            OnCancelled = null;
            _options.Clear();
            GameState.Unlock(GameState.LockChoice);
        }

        private void Update()
        {
            if (!IsOpen || GameState.IsPaused) return;
            // 메뉴를 연 바로 그 입력으로 닫히지 않게 한다.
            if (Time.frameCount <= _openedFrame) return;

            IPlayerInput input = PlayerInputHub.Current;
            int choice = input.ChoicePressed;
            if (choice >= 1 && choice <= _options.Count)
            {
                ChoiceOption option = _options[choice - 1];
                if (!option.enabled) return;
                Action chosen = option.onChosen;
                Close();
                Sfx.Play(Sfx.Id.Interact);
                chosen?.Invoke();
                return;
            }

            if (input.InteractPressed)
            {
                Action cancelled = OnCancelled;
                Close();
                cancelled?.Invoke();
            }
        }
    }
}
