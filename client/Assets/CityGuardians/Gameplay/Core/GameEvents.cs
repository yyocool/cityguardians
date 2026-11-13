using System;
using System.Collections.Generic;
using UnityEngine;

namespace CityGuardians.Gameplay
{
    /// <summary>
    /// 게임플레이가 화면(UI)에 알리는 일들. 게임플레이는 UI를 모르고, UI가 이 이벤트를 듣는다.
    /// </summary>
    public static class GameEvents
    {
        /// <summary>현재 목표 한 줄. null 또는 빈 글이면 숨긴다.</summary>
        public static event Action<string> ObjectiveChanged;
        /// <summary>상호작용 안내 ("[E] 말하기"). null이면 숨긴다.</summary>
        public static event Action<string> PromptChanged;
        /// <summary>잠깐 떴다 사라지는 알림.</summary>
        public static event Action<string> Toast;
        /// <summary>조작 키 안내. null이면 숨긴다.</summary>
        public static event Action<string> KeyHintChanged;
        /// <summary>대화 한 줄 표시 (말하는 이, 내용).</summary>
        public static event Action<string, string> DialogueLine;
        public static event Action DialogueClosed;
        /// <summary>화면 가운데의 안내판 한 장 (제목, 내용, 경고 여부). 컴퓨터 화면에 쓴다.</summary>
        public static event Action<string, string, bool> PanelOpened;
        public static event Action PanelClosed;
        /// <summary>화면 어둡게(true) / 밝게(false), 걸리는 시간.</summary>
        public static event Action<bool, float> Fade;
        public static event Action<bool> PauseChanged;
        /// <summary>플레이어 체력 (현재, 최대).</summary>
        public static event Action<int, int> PlayerHealthChanged;
        public static event Action PlayerHurt;
        /// <summary>접속 실패 (오류 코드, 안내 문구). 코드가 null이면 안내를 닫는다.</summary>
        public static event Action<string, string> BootFailed;

        private static string _objective;

        public static string CurrentObjective => _objective;

        public static void SetObjective(string text)
        {
            if (_objective == text) return;
            _objective = text;
            ObjectiveChanged?.Invoke(text);
        }

        public static void SetPrompt(string text) => PromptChanged?.Invoke(text);
        public static void ShowToast(string text) => Toast?.Invoke(text);
        public static void SetKeyHint(string text) => KeyHintChanged?.Invoke(text);
        public static void ShowDialogueLine(string speaker, string text) => DialogueLine?.Invoke(speaker, text);
        public static void CloseDialogue() => DialogueClosed?.Invoke();
        public static void OpenPanel(string title, string body, bool alert) => PanelOpened?.Invoke(title, body, alert);
        public static void ClosePanel() => PanelClosed?.Invoke();
        public static void RequestFade(bool toBlack, float seconds) => Fade?.Invoke(toBlack, seconds);
        public static void RaisePauseChanged(bool paused) => PauseChanged?.Invoke(paused);
        public static void RaisePlayerHealth(int current, int max) => PlayerHealthChanged?.Invoke(current, max);
        public static void RaisePlayerHurt() => PlayerHurt?.Invoke();
        public static void RaiseBootFailed(string code, string message) => BootFailed?.Invoke(code, message);
    }

    /// <summary>플레이어 조작을 잠그는 이유들을 모아 둔다. 하나라도 있으면 조작할 수 없다.</summary>
    public static class GameState
    {
        public const string LockDialogue = "dialogue";
        public const string LockPause = "pause";
        public const string LockTransition = "transition";
        public const string LockDead = "dead";
        public const string LockPanel = "panel";
        public const string LockBoot = "boot";
        public const string LockChoice = "choice";
        /// <summary>방어전 결과 화면이 떠 있는 동안.</summary>
        public const string LockResult = "result";
        /// <summary>지도가 열려 있는 동안.</summary>
        public const string LockMap = "map";
        /// <summary>쿠폰을 상점에 보여 주는 화면이 떠 있는 동안.</summary>
        public const string LockCoupon = "coupon";
        /// <summary>협동 방어전의 전술 지도를 펼쳐 둔 동안.</summary>
        public const string LockTacticalMap = "tactical_map";

        private static readonly HashSet<string> Locks = new HashSet<string>();

        public static bool IsControlLocked => Locks.Count > 0;
        public static bool IsPaused => Locks.Contains(LockPause);

        /// <summary>
        /// 방 꾸미기 중인가 (docs/ROOM_SYSTEM.md 4장). 잠금이 아니라서 걸어 다닐 수 있지만,
        /// 그동안에는 공격, 가방, 지도, 다른 상호작용을 하지 않는다 (E와 숫자 키를 꾸미기가 쓴다).
        /// </summary>
        public static bool Decorating { get; set; }

        /// <summary>잠금이 마지막으로 풀린 프레임. 잠금을 푼 입력이 같은 프레임에 다른 일을 하지 않게 하는 데 쓴다.</summary>
        public static int LastUnlockFrame { get; private set; } = -1;

        public static void Lock(string reason) => Locks.Add(reason);

        public static void Unlock(string reason)
        {
            if (Locks.Remove(reason)) LastUnlockFrame = Time.frameCount;
        }

        public static bool IsLockedBy(string reason) => Locks.Contains(reason);

        /// <summary>잠근 이유가 그것 하나뿐인가. 겹쳐 뜬 화면 가운데 맨 위의 것만 입력을 받게 하는 데 쓴다.</summary>
        public static bool IsOnlyLockedBy(string reason) => Locks.Count == 1 && Locks.Contains(reason);

        public static void ClearAll()
        {
            Locks.Clear();
            Decorating = false;
            LastUnlockFrame = -1;
            Time.timeScale = 1f;
        }
    }
}
