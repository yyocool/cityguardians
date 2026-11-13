using System;
using System.Collections.Generic;
using UnityEngine;

namespace CityGuardians.Gameplay
{
    /// <summary>
    /// 코드로 만든 간단한 효과음. 음원 파일 없이 시작하기 위한 임시 소리이며
    /// 나중에 실제 음원으로 바꿀 수 있다.
    /// </summary>
    public static class Sfx
    {
        public enum Id
        {
            Swing,
            Hit,
            EnemyDown,
            Coin,
            Hurt,
            Alarm,
            LevelUp,
            Interact,
            Talk,
            TowerShot,
            Explosion,
            Build,
            Victory,
            Defeat,
            Blaster,
            Heal,
        }

        private const int SampleRate = 22050;
        private static AudioSource _source;
        private static readonly Dictionary<Id, AudioClip> Clips = new Dictionary<Id, AudioClip>();
        private static readonly System.Random Noise = new System.Random(7);

        public static float Volume = 0.5f;

        public static void Attach(GameObject host)
        {
            _source = host.GetComponent<AudioSource>();
            if (_source == null) _source = host.AddComponent<AudioSource>();
            _source.playOnAwake = false;
            _source.spatialBlend = 0f;
        }

        public static void Play(Id id, float volumeScale = 1f)
        {
            if (_source == null || Application.isBatchMode) return;
            if (!Clips.TryGetValue(id, out AudioClip clip) || clip == null)
            {
                clip = Build(id);
                Clips[id] = clip;
            }
            _source.PlayOneShot(clip, Volume * volumeScale);
        }

        private static AudioClip Build(Id id)
        {
            switch (id)
            {
                case Id.Swing:
                    return Make("swing", 0.12f, (t, p) => NoiseSample() * 0.35f * (1f - p) * Mathf.Sin(p * Mathf.PI));
                case Id.Hit:
                    return Make("hit", 0.14f, (t, p) =>
                        (Square(t, 140f) * 0.45f + NoiseSample() * 0.4f) * Mathf.Pow(1f - p, 2f));
                case Id.EnemyDown:
                    return Make("down", 0.4f, (t, p) =>
                        (Square(t, Mathf.Lerp(320f, 60f, p)) * 0.35f + NoiseSample() * 0.25f * (1f - p)) * (1f - p));
                case Id.Coin:
                    return Make("coin", 0.22f, (t, p) =>
                        Mathf.Sin(2f * Mathf.PI * (p < 0.35f ? 988f : 1319f) * t) * 0.4f * Mathf.Pow(1f - p, 1.5f));
                case Id.Hurt:
                    return Make("hurt", 0.25f, (t, p) => Saw(t, Mathf.Lerp(220f, 110f, p)) * 0.4f * (1f - p));
                case Id.Alarm:
                    return Make("alarm", 0.9f, (t, p) =>
                    {
                        float freq = ((int)(t / 0.15f) % 2 == 0) ? 880f : 660f;
                        return Square(t, freq) * 0.22f * Mathf.Clamp01((1f - p) * 6f);
                    });
                case Id.LevelUp:
                    return Make("levelup", 0.6f, (t, p) =>
                    {
                        float[] notes = { 523f, 659f, 784f, 1047f };
                        int index = Mathf.Min(notes.Length - 1, (int)(p * notes.Length));
                        float local = (p * notes.Length) - index;
                        return Mathf.Sin(2f * Mathf.PI * notes[index] * t) * 0.35f * (1f - local * 0.6f);
                    });
                case Id.Interact:
                    return Make("interact", 0.08f, (t, p) => Mathf.Sin(2f * Mathf.PI * 700f * t) * 0.3f * (1f - p));
                case Id.Talk:
                    return Make("talk", 0.06f, (t, p) => Square(t, 420f) * 0.15f * (1f - p));
                case Id.TowerShot:
                    return Make("towershot", 0.05f, (t, p) =>
                        (Square(t, 260f) * 0.2f + NoiseSample() * 0.2f) * Mathf.Pow(1f - p, 2f));
                case Id.Explosion:
                    return Make("explosion", 0.5f, (t, p) =>
                        (NoiseSample() * 0.55f + Square(t, Mathf.Lerp(90f, 40f, p)) * 0.3f) * Mathf.Pow(1f - p, 1.6f));
                case Id.Build:
                    return Make("build", 0.3f, (t, p) =>
                        Square(t, p < 0.5f ? 330f : 495f) * 0.25f * (1f - p * 0.7f));
                case Id.Victory:
                    return Make("victory", 1.0f, (t, p) =>
                    {
                        float[] notes = { 523f, 659f, 784f, 659f, 784f, 1047f };
                        int index = Mathf.Min(notes.Length - 1, (int)(p * notes.Length));
                        return Mathf.Sin(2f * Mathf.PI * notes[index] * t) * 0.35f * Mathf.Clamp01((1f - p) * 5f);
                    });
                case Id.Defeat:
                    return Make("defeat", 0.9f, (t, p) =>
                        Saw(t, Mathf.Lerp(330f, 110f, p)) * 0.3f * (1f - p));
                case Id.Blaster:
                    // 높은 음에서 빠르게 떨어지는 "퓻" 소리
                    return Make("blaster", 0.11f, (t, p) =>
                        Mathf.Sin(2f * Mathf.PI * Mathf.Lerp(1500f, 420f, p) * t) * 0.3f * Mathf.Pow(1f - p, 1.5f));
                case Id.Heal:
                    return Make("heal", 0.45f, (t, p) =>
                    {
                        float[] notes = { 659f, 880f, 1175f };
                        int index = Mathf.Min(notes.Length - 1, (int)(p * notes.Length));
                        return Mathf.Sin(2f * Mathf.PI * notes[index] * t) * 0.3f * Mathf.Clamp01((1f - p) * 4f);
                    });
                default:
                    return Make("silence", 0.01f, (t, p) => 0f);
            }
        }

        private static AudioClip Make(string name, float seconds, Func<float, float, float> sample)
        {
            int count = Mathf.Max(1, Mathf.RoundToInt(seconds * SampleRate));
            var data = new float[count];
            for (int i = 0; i < count; i++)
            {
                float t = i / (float)SampleRate;
                data[i] = Mathf.Clamp(sample(t, i / (float)count), -1f, 1f);
            }
            AudioClip clip = AudioClip.Create("sfx_" + name, count, 1, SampleRate, false);
            clip.SetData(data, 0);
            return clip;
        }

        private static float Square(float t, float freq) => Mathf.Sin(2f * Mathf.PI * freq * t) >= 0f ? 1f : -1f;

        private static float Saw(float t, float freq) => 2f * (t * freq - Mathf.Floor(0.5f + t * freq));

        private static float NoiseSample() => (float)(Noise.NextDouble() * 2.0 - 1.0);
    }
}
