using System;
using System.Collections.Generic;
using CityGuardians.Domain;
using CityGuardians.Services;
using UnityEngine;

namespace CityGuardians.Gameplay
{
    /// <summary>
    /// 협동 방어전으로 들어가는 화면 흐름 (작전 본부 → 협동 방어전, docs/COOP_DEFENSE_SYSTEM.md 7장).
    ///
    ///  - 연습전: NPC 대원과 함께 같은 규칙으로 해 본다. 서버와 주고받는 것이 없으므로 보상도 기록도 없다.
    ///  - 온라인: 게임 서버(REST)에서 방어전을 만들거나 찾아 들어가 입장권을 받고, 실시간 서버(WebSocket)에 접속한다.
    ///    운영팀이 켜고 실시간 서버가 준비되기 전에는 "준비 중"으로 보인다.
    /// </summary>
    public static class CoopActions
    {
        public const string MenuTag = "coop";

        public static void Open(Action onBack = null)
        {
            GameSession session = GameSession.Current;
            if (session == null) return;
            bool open = CombatService.IsOpen(CombatModeCodes.Defense);
            ChoiceMenu.Instance.Open("협동 방어전",
                "도시 외곽에서 여러 대원이 함께 관문을 지켜요.\n타워 설치, 수리, 전투, 지휘를 나눠 맡고 전술 핑으로 서로 알려요.",
                new ChoiceOption
                {
                    label = "연습전",
                    cost = "NPC 대원 3명",
                    detail = open ? "NPC 대원과 함께 조작을 익혀요. 보상과 기록은 없어요." : "지금은 방어전이 잠시 닫혀 있어요.",
                    enabled = open,
                    onChosen = () => StartPractice(session),
                },
                new ChoiceOption
                {
                    label = "온라인 방어전",
                    cost = "4~8명",
                    detail = !open ? "지금은 방어전이 잠시 닫혀 있어요."
                        : session.IsResident ? "다른 가디언들과 함께 우리 도시의 관문을 지켜요."
                        : "전입신고를 하면 도시의 방어전에 참여할 수 있어요.",
                    enabled = open && session.IsResident,
                    onChosen = () => OpenOnline(session, onBack),
                });
            ChoiceMenu.Instance.Tag = MenuTag;
            ChoiceMenu.Instance.OnCancelled = onBack;
        }

        private static void StartPractice(GameSession session)
        {
            if (GameSession.Current != session || GameRoot.Instance.IsBusy) return;
            CoopLaunch.Set(CoopLaunch.NewPractice(session), null);
            GameRoot.Instance.LoadScene(SceneNames.Coop, SpawnIds.CoopBase);
        }

        // ── 온라인 ──────────────────────────────────────────────

        private static void OpenOnline(GameSession session, Action onBack)
        {
            Action reopen = () => Open(onBack);
            session.Backend.GetCoopConfig(config =>
            {
                if (GameSession.Current != session) return;
                if (!config.ok)
                {
                    BenefitUi.Failed(config, "협동 방어전 설정");
                    reopen();
                    return;
                }
                if (!config.data.available)
                {
                    // 운영팀이 아직 열지 않았거나 실시간 서버가 준비되지 않았다.
                    GameEvents.ShowToast(config.data.UnavailableMessage + " 연습전은 지금도 할 수 있어요.");
                    reopen();
                    return;
                }
                session.Backend.GetCoopSessions(list =>
                {
                    if (GameSession.Current != session) return;
                    if (!list.ok)
                    {
                        BenefitUi.Failed(list, "협동 방어전 목록");
                        reopen();
                        return;
                    }
                    ShowLobbies(session, config.data, list.data, onBack);
                });
            });
        }

        private static void ShowLobbies(GameSession session, CoopConfigDto config, CoopSessionsDto list, Action onBack)
        {
            Action reopen = () => Open(onBack);
            var options = new List<ChoiceOption>();

            // 내가 이미 들어가 있는 방어전이 있으면 그리로 돌아간다 (연결이 끊겼다가 돌아온 경우).
            CoopSessionDto mine = config.activeSession;
            if (mine != null)
            {
                options.Add(new ChoiceOption
                {
                    label = "진행 중인 방어전으로 돌아가기",
                    cost = mine.statusName,
                    detail = $"{mine.zoneName}   대원 {mine.players} / {mine.maxPlayers}명",
                    onChosen = () => Enter(session, mine.id, reopen),
                });
            }
            else
            {
                options.Add(new ChoiceOption
                {
                    label = "새 방어전 만들기",
                    cost = $"{config.defaultPlayers}명",
                    detail = $"내가 방장이 돼요. 대원이 {config.minPlayersToStart}명 넘게 모이면 시작할 수 있어요.",
                    onChosen = () => Create(session, config.defaultPlayers, reopen),
                });
                if (config.maxPlayers > config.defaultPlayers)
                    options.Add(new ChoiceOption
                    {
                        label = "큰 방어전 만들기",
                        cost = $"{config.maxPlayers}명",
                        detail = "대원이 많을수록 적도 강해지고, 공동 예산도 늘어요.",
                        onChosen = () => Create(session, config.maxPlayers, reopen),
                    });
                for (int i = 0; i < list.sessions.Count; i++)
                {
                    CoopSessionDto lobby = list.sessions[i];
                    if (lobby.mine) continue;
                    bool full = lobby.players >= lobby.maxPlayers;
                    options.Add(new ChoiceOption
                    {
                        label = HostName(lobby) + "의 방어전",
                        cost = $"{lobby.players} / {lobby.maxPlayers}명",
                        detail = full ? "자리가 없어요." : $"{lobby.zoneName} · {lobby.statusName}",
                        enabled = !full,
                        onChosen = () => Join(session, lobby.id, reopen),
                    });
                }
            }

            string rewards = config.dailyRewardLimit > 0
                ? $"오늘 받을 수 있는 보상 {config.rewardsLeftToday} / {config.dailyRewardLimit}회"
                : "보상은 팀의 결과와 내 참여에 따라 서버가 정해요.";
            PagedMenu.Open("온라인 방어전", $"{RegionService.ShortName(list.regionCode) ?? "우리 도시"} 외곽\n{rewards}", options, reopen, MenuTag);
        }

        private static string HostName(CoopSessionDto lobby)
        {
            for (int i = 0; i < lobby.members.Count; i++)
                if (lobby.members[i].isHost) return lobby.members[i].nickname;
            return "가디언";
        }

        private static void Create(GameSession session, int players, Action onFail)
        {
            session.Backend.CreateCoopSession(StageCodes.Outskirts1, players, null, CombatService.NewRequestId(), result =>
            {
                if (GameSession.Current != session) return;
                if (!result.ok || result.data == null || result.data.session == null)
                {
                    BenefitUi.Failed(result, "협동 방어전 만들기");
                    onFail?.Invoke();
                    return;
                }
                Enter(session, result.data.session.id, onFail);
            });
        }

        private static void Join(GameSession session, string sessionId, Action onFail)
        {
            session.Backend.JoinCoopSession(sessionId, null, CombatService.NewRequestId(), result =>
            {
                if (GameSession.Current != session) return;
                if (!result.ok)
                {
                    BenefitUi.Failed(result, "협동 방어전 참여");
                    onFail?.Invoke();
                    return;
                }
                Enter(session, sessionId, onFail);
            });
        }

        /// <summary>입장권을 받아 실시간 서버에 접속한다. 입장권은 짧게만, 한 번만 쓸 수 있다.</summary>
        private static void Enter(GameSession session, string sessionId, Action onFail)
        {
            session.Backend.GetCoopTicket(sessionId, result =>
            {
                if (GameSession.Current != session) return;
                if (!result.ok || result.data == null)
                {
                    BenefitUi.Failed(result, "협동 방어전 입장권");
                    onFail?.Invoke();
                    return;
                }
                Connect(session, sessionId, result.data, onFail);
            });
        }

        private static void Connect(GameSession session, string sessionId, CoopTicketDto ticket, Action onFail)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            // 웹 빌드에서는 이 소켓을 쓸 수 없다 (준비 중).
            GameEvents.ShowToast("이 기기에서는 온라인 협동 방어전이 아직 준비 중이에요.");
            onFail?.Invoke();
#else
            // 암호화된 주소(wss://)로만 접속한다. 입장권을 주소에 싣지 않는다.
            string problem = WebSocketCoopSocket.UrlProblem(ticket.url);
            if (problem != null)
            {
                Debug.LogWarning("실시간 서버 주소를 쓸 수 없음: " + problem);
                GameEvents.ShowToast("실시간 서버 주소가 올바르지 않아 접속하지 않았어요.");
                onFail?.Invoke();
                return;
            }
            WebSocketCoopSocket socket;
            try
            {
                socket = new WebSocketCoopSocket(ticket.url);
            }
            catch (Exception e)
            {
                Debug.LogWarning("실시간 서버 접속 실패: " + e.Message);
                GameEvents.ShowToast("실시간 서버에 연결하지 못했어요.");
                onFail?.Invoke();
                return;
            }
            CoopConnector.Begin(session, new RemoteCoopLink(socket, ticket.ticket), socket, sessionId, onFail);
#endif
        }
    }

    /// <summary>
    /// 실시간 서버가 맞아 줄 때까지(지도와 첫 상태가 올 때까지) 기다렸다가 전장으로 간다.
    /// 그 전에 닫히거나 너무 오래 걸리면 이유를 알리고 그만둔다.
    /// </summary>
    public sealed class CoopConnector : MonoBehaviour
    {
        public const string LockConnect = "coop_connect";
        private const float TimeoutSeconds = 15f;

        private GameSession _session;
        private ICoopLink _link;
        private IDisposable _socket;
        private string _sessionId;
        private Action _onFail;
        private float _elapsed;

        public static void Begin(GameSession session, ICoopLink link, IDisposable socket, string sessionId, Action onFail)
        {
            var connector = GameRoot.Instance.gameObject.AddComponent<CoopConnector>();
            connector._session = session;
            connector._link = link;
            connector._socket = socket;
            connector._sessionId = sessionId;
            connector._onFail = onFail;
            GameState.Lock(LockConnect);
            GameEvents.ShowToast("실시간 서버에 연결하고 있어요…");
        }

        private void Update()
        {
            if (_link == null) return;
            if (GameSession.Current != _session)
            {
                Fail(null);
                return;
            }
            _elapsed += Time.unscaledDeltaTime;
            _link.Update(Time.unscaledDeltaTime);

            string state = _link.State;
            if ((state == CoopLinkStates.Lobby || state == CoopLinkStates.Battle) && _link.Map != null && _link.Snapshot != null)
            {
                if (GameRoot.Instance.IsBusy) return;
                ICoopLink link = _link;
                _link = null;
                GameState.Unlock(LockConnect);
                CoopLaunch.Set(link, _sessionId);
                GameRoot.Instance.LoadScene(SceneNames.Coop, SpawnIds.CoopBase);
                Destroy(this);
            }
            else if (state == CoopLinkStates.Closed || state == CoopLinkStates.Finished)
            {
                Fail(CoopTexts.Closed(_link.ClosedReason));
            }
            else if (_elapsed >= TimeoutSeconds)
            {
                Fail("실시간 서버에 연결하지 못했어요. 잠시 뒤에 다시 해 주세요.");
            }
        }

        private void Fail(string message)
        {
            ICoopLink link = _link;
            _link = null;
            if (link != null && link.State != CoopLinkStates.Closed) link.Leave();
            _socket?.Dispose();
            GameState.Unlock(LockConnect);
            if (message != null) GameEvents.ShowToast(message);
            Action onFail = _onFail;
            Destroy(this);
            if (message != null) onFail?.Invoke();
        }

        private void OnDestroy()
        {
            // 접속을 끊는 등으로 도중에 사라졌을 때도 잠금과 연결을 남기지 않는다.
            if (_link == null) return;
            if (_link.State != CoopLinkStates.Closed) _link.Leave();
            _socket?.Dispose();
            GameState.Unlock(LockConnect);
        }
    }
}
