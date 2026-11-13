using System;
using System.Collections.Generic;

namespace CityGuardians.Domain
{
    /// <summary>지도에 떠 있는 전술 핑.</summary>
    public sealed class TacticalPing
    {
        public int id;
        public string pid;
        public string kind;
        public CoopVec pos;
        /// <summary>보낸 때와 사라지는 때 (전투 시계, 초).</summary>
        public float at;
        public float expiresAt;
    }

    /// <summary>핑 기록의 한 줄. 받아들이지 않은 핑도 남긴다 (남용을 살피는 데 쓴다).</summary>
    [Serializable]
    public sealed class PingRecord
    {
        public string pid;
        public string kind;
        /// <summary>위치 (cm).</summary>
        public int x;
        public int z;
        public int atMs;
        public bool accepted;
    }

    /// <summary>
    /// 전술 핑: 빈도 제한, 자동 일시 차단, 관리자 차단, 팀원의 "이 사람 핑 숨기기" (docs/TACTICAL_MINIMAP.md 4장).
    ///
    /// 빈도 제한은 토큰 버킷이다: 연달아 burst 번까지 보낼 수 있고, refillSeconds 마다 하나씩 다시 찬다.
    /// 제한을 어겨 거절된 횟수가 autoMuteStrikes 에 닿으면 autoMuteSeconds 동안 그 사람의 핑을 받지 않는다.
    /// </summary>
    public sealed class PingBoard
    {
        public const int MaxRecords = 3000;

        private sealed class Sender
        {
            public float tokens;
            public float lastRefill;
            public int strikes;
            public float mutedUntil;
            public bool blocked;
            /// <summary>다시 차는 시간의 단축 (%). 지휘관 역할.</summary>
            public int cooldownPercent;
        }

        private readonly PingRules rules;
        private readonly CoopMap map;
        private readonly Dictionary<string, Sender> senders = new Dictionary<string, Sender>();
        private readonly Dictionary<string, HashSet<string>> hidden = new Dictionary<string, HashSet<string>>();
        private readonly List<TacticalPing> active = new List<TacticalPing>();
        private readonly List<PingRecord> records = new List<PingRecord>();
        private int nextId = 1;

        public PingBoard(PingRules rules, CoopMap map)
        {
            this.rules = rules ?? new PingRules();
            this.map = map;
        }

        public IReadOnlyList<TacticalPing> Active => active;
        public IReadOnlyList<PingRecord> Records => records;

        private Sender Of(string pid, float now)
        {
            if (!senders.TryGetValue(pid, out var sender))
            {
                sender = new Sender { tokens = Math.Max(1, rules.burst), lastRefill = now };
                senders[pid] = sender;
            }
            return sender;
        }

        /// <summary>관리자가 막은 계정인가 (입장권에 실려 온다).</summary>
        public void SetBlocked(string pid, bool blocked, float now) => Of(pid, now).blocked = blocked;

        /// <summary>역할에 따른 재사용 시간 단축.</summary>
        public void SetCooldownPercent(string pid, int percent, float now) => Of(pid, now).cooldownPercent = Math.Max(0, Math.Min(90, percent));

        public bool IsMuted(string pid, float now) => senders.TryGetValue(pid, out var sender) && (sender.blocked || now < sender.mutedUntil);

        private void Refill(Sender sender, float now)
        {
            float seconds = Math.Max(0.1f, rules.refillSeconds * (100 - sender.cooldownPercent) / 100f);
            float elapsed = Math.Max(0f, now - sender.lastRefill);
            sender.tokens = Math.Min(Math.Max(1, rules.burst), sender.tokens + elapsed / seconds);
            sender.lastRefill = now;
        }

        private void Record(string pid, string kind, CoopVec pos, int atMs, bool accepted)
        {
            if (records.Count >= MaxRecords) return;
            records.Add(new PingRecord
            {
                pid = pid, kind = kind, x = (int)Math.Round(pos.x * 100f), z = (int)Math.Round(pos.z * 100f),
                atMs = Math.Max(0, atMs), accepted = accepted,
            });
        }

        /// <summary>
        /// 핑을 보낸다. 받아들이면 null 을, 아니면 이유(CoopRejects)를 돌려준다. 받아들인 핑은 ping 으로 나온다.
        /// now 는 방의 시계(초)이고, atMs 는 기록에 남길 전투 시간이다 (대기실에서는 0).
        /// </summary>
        public string Send(string pid, string kind, CoopVec pos, float now, out TacticalPing ping, int atMs = -1)
        {
            if (atMs < 0) atMs = (int)Math.Round(now * 1000f);
            ping = null;
            if (string.IsNullOrEmpty(pid)) return CoopRejects.NotMember;
            if (!PingKinds.IsKnown(kind)) return CoopRejects.PingKind;      // 모르는 종류는 기록도 하지 않는다
            pos = map != null ? map.Clamp(pos) : pos;
            var sender = Of(pid, now);
            string reject = null;
            if (rules.enabled != 1) reject = CoopRejects.PingDisabled;
            else if (sender.blocked) reject = CoopRejects.PingBlocked;
            else if (now < sender.mutedUntil) reject = CoopRejects.PingMuted;
            else
            {
                Refill(sender, now);
                if (sender.tokens < 1f)
                {
                    reject = CoopRejects.PingRate;
                    sender.strikes++;
                    if (sender.strikes >= Math.Max(1, rules.autoMuteStrikes))
                    {
                        sender.strikes = 0;
                        sender.mutedUntil = now + rules.autoMuteSeconds;
                    }
                }
            }
            if (reject != null)
            {
                Record(pid, kind, pos, atMs, false);
                return reject;
            }
            sender.tokens -= 1f;
            sender.strikes = 0;
            ping = new TacticalPing { id = nextId++, pid = pid, kind = kind, pos = pos, at = now, expiresAt = now + Math.Max(1, rules.lifetimeSeconds) };
            active.Add(ping);
            // 한 사람이 지도에 띄울 수 있는 수를 넘으면 그 사람의 가장 오래된 핑부터 지운다.
            int mine = 0;
            for (int i = active.Count - 1; i >= 0; i--)
            {
                if (active[i].pid != pid) continue;
                mine++;
                if (mine > Math.Max(1, rules.maxActivePerPlayer)) active.RemoveAt(i);
            }
            Record(pid, kind, pos, atMs, true);
            return null;
        }

        /// <summary>시간이 지난 핑을 지운다.</summary>
        public void Expire(float now)
        {
            for (int i = active.Count - 1; i >= 0; i--)
                if (now >= active[i].expiresAt) active.RemoveAt(i);
        }

        /// <summary>팀원이 특정 사람의 핑을 숨기거나 다시 본다. 그 판 안에서만 유효하다.</summary>
        public void Hide(string viewerPid, string senderPid, bool hide)
        {
            if (string.IsNullOrEmpty(viewerPid) || string.IsNullOrEmpty(senderPid) || viewerPid == senderPid) return;
            if (!hidden.TryGetValue(viewerPid, out var set))
            {
                if (!hide) return;
                set = new HashSet<string>();
                hidden[viewerPid] = set;
            }
            if (hide) set.Add(senderPid);
            else set.Remove(senderPid);
        }

        public bool IsHidden(string viewerPid, string senderPid) =>
            viewerPid != null && hidden.TryGetValue(viewerPid, out var set) && set.Contains(senderPid);

        /// <summary>그 사람에게 보이는 핑.</summary>
        public List<TacticalPing> VisibleTo(string viewerPid)
        {
            var visible = new List<TacticalPing>();
            for (int i = 0; i < active.Count; i++)
                if (!IsHidden(viewerPid, active[i].pid)) visible.Add(active[i]);
            return visible;
        }

        public int SentBy(string pid)
        {
            int count = 0;
            for (int i = 0; i < records.Count; i++)
                if (records[i].pid == pid) count++;
            return count;
        }

        public int RejectedOf(string pid)
        {
            int count = 0;
            for (int i = 0; i < records.Count; i++)
                if (records[i].pid == pid && !records[i].accepted) count++;
            return count;
        }
    }
}
