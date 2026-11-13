using System;
using System.Collections.Generic;
using CityGuardians.Inputs;
using UnityEngine;

namespace CityGuardians.Gameplay
{
    /// <summary>E 키로 상호작용할 수 있는 대상.</summary>
    public sealed class Interactable : MonoBehaviour
    {
        public static readonly List<Interactable> All = new List<Interactable>();

        /// <summary>안내에 보일 동작 이름. 예: "말하기", "나가기".</summary>
        public string verb = "살펴보기";
        public float radius = 2.2f;
        /// <summary>지금 상호작용할 수 있는가. 비어 있으면 항상 가능.</summary>
        public Func<bool> available;
        public Action onInteract;
        /// <summary>대상의 기준 위치가 발밑이 아닐 때의 보정.</summary>
        public Vector3 offset;

        public Vector3 Point => transform.position + offset;
        public bool IsAvailable => isActiveAndEnabled && (available == null || available());

        public static Interactable Add(GameObject host, string verb, Action onInteract, float radius = 2.2f,
            Func<bool> available = null)
        {
            var interactable = host.AddComponent<Interactable>();
            interactable.verb = verb;
            interactable.onInteract = onInteract;
            interactable.radius = radius;
            interactable.available = available;
            return interactable;
        }

        private void OnEnable() => All.Add(this);
        private void OnDisable() => All.Remove(this);
    }

    /// <summary>플레이어 가까이의 상호작용 대상을 찾아 안내를 띄우고 E 입력을 전달한다.</summary>
    public sealed class PlayerInteractor : MonoBehaviour
    {
        private Interactable _current;
        private string _shownPrompt;

        public Interactable Current => _current;

        private void Update()
        {
            // 대화가 닫힌 바로 그 프레임의 E 입력이 다시 대화를 여는 일을 막는다.
            if (GameState.IsControlLocked || Time.frameCount <= GameState.LastUnlockFrame)
            {
                SetPrompt(null);
                _current = null;
                return;
            }

            // 방을 꾸미는 동안에는 E가 가구를 놓는 데 쓰인다. 안내도 꾸미기 쪽이 띄운다.
            if (GameState.Decorating)
            {
                _current = null;
                _shownPrompt = null;
                return;
            }

            _current = FindNearest();
            SetPrompt(_current != null ? $"[E] {_current.verb}" : null);

            if (_current != null && PlayerInputHub.Current.InteractPressed)
            {
                Sfx.Play(Sfx.Id.Interact);
                Action action = _current.onInteract;
                action?.Invoke();
            }
        }

        private Interactable FindNearest()
        {
            Vector3 position = transform.position;
            Interactable best = null;
            float bestDistance = float.MaxValue;
            List<Interactable> all = Interactable.All;
            for (int i = 0; i < all.Count; i++)
            {
                Interactable candidate = all[i];
                if (candidate == null || !candidate.IsAvailable) continue;
                Vector3 to = candidate.Point - position;
                to.y = 0f;
                float distance = to.magnitude;
                if (distance > candidate.radius || distance >= bestDistance) continue;
                best = candidate;
                bestDistance = distance;
            }
            return best;
        }

        private void SetPrompt(string prompt)
        {
            if (_shownPrompt == prompt) return;
            _shownPrompt = prompt;
            GameEvents.SetPrompt(prompt);
        }

        private void OnDisable() => SetPrompt(null);
    }
}
