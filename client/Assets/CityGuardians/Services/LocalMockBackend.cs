using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using CityGuardians.Domain;
using UnityEngine;

namespace CityGuardians.Services
{
    /// <summary>
    /// 서버 없이 같은 규칙으로 동작하는 개발용 모의 서버 (docs/DEVELOPMENT_PLAN.md 4장).
    /// 실제 PHP API가 준비되면 HTTP 구현으로 바꾼다. 공개 빌드에는 넣지 않는다.
    ///
    /// 저장소에는 두 가지를 둔다.
    ///  - guest.json        : 기기가 보관하는 게스트 ID와 비밀키 (클라이언트 쪽 데이터)
    ///  - mock_server.json  : 서버 DB에 해당하는 데이터 (플레이어, 잔액, 원장)
    /// 실제 서버 연결 때 guest.json의 비밀키는 Windows 사용자 계정 암호화(DPAPI)로 보관해야 한다.
    /// 이 모의 구현은 개발용이라 평문으로 둔다.
    /// </summary>
    public sealed partial class LocalMockBackend : IGameBackend
    {
        public const string GuestFile = "guest.json";
        public const string ServerFile = "mock_server.json";
        private const int LedgerKeep = 500;
        private const int RequestKeep = 50;
        private const int RequestIdMaxLength = 64;
        private const string RequestBuy = "buy";
        private const string RequestUse = "use";
        private const string RequestDeposit = "deposit";
        private const string RequestWithdraw = "withdraw";
        private const string RequestSell = "sell";
        private const string RequestTermOpen = "term_open";
        private const string RequestTermCancel = "term_cancel";
        /// <summary>거래 내역 화면 한 장에 들어가는 줄 수.</summary>
        private const int BankRecentCount = 5;

        [Serializable]
        private sealed class GuestCredential
        {
            public string playerId;
            public string guestSecret;
        }

        [Serializable]
        private sealed class PlayerRow
        {
            public string playerId;
            public string secretHash;
            public string displayName;
            public string createdAt;
            public int level = 1;
            public int xp;
            public long balance;
            public long ledgerNextSeq = 1;
            /// <summary>보관 중인 원장의 첫 행 이전 잔액. 오래된 행을 잘라낼 때 갱신한다.</summary>
            public long ledgerOpeningBalance;
            public List<LedgerEntry> ledger = new List<LedgerEntry>();
            public List<FlagDto> flags = new List<FlagDto>();
            public List<string> questsClaimed = new List<string>();
            public string dailyDate;
            public int dailyHuntGc;
            public int confirmedKills;
            public string equippedMelee;
            /// <summary>장착한 원거리 무기. 없으면 비어 있다.</summary>
            public string equippedRanged;
            /// <summary>머리와 몸에 입은 방어구나 의상. 없으면 비어 있다.</summary>
            public string equippedHead;
            public string equippedBody;
            /// <summary>가진 아이템. 장착한 장비도 여기에 남는다.</summary>
            public List<ItemStack> inventory = new List<ItemStack>();
            /// <summary>가진 가구 (방에 놓은 것과 넣어 둔 것). 가방과 따로 센다.</summary>
            public List<FurniturePiece> furniture = new List<FurniturePiece>();
            public long furnitureNextId = 1;
            /// <summary>최근에 처리한 구매·사용 요청. 같은 요청이 다시 오면 두 번 처리하지 않는다.</summary>
            public List<RequestRow> recentRequests = new List<RequestRow>();
            /// <summary>GC로 해금한 타워. 처음부터 쓸 수 있는 타워는 여기에 넣지 않는다.</summary>
            public List<string> unlockedTowers = new List<string>();
            public List<string> clearedStages = new List<string>();
            /// <summary>전입한 지역. 이방인이면 비어 있다.</summary>
            public string regionCode;
            public string registeredAt;
            /// <summary>전입 축하금을 받았는가. 계정당 한 번만 준다.</summary>
            public bool residencyBonusPaid;

            // 은행 (보통예금). 통장도 지갑처럼 잔액과 원장을 가진다.
            public long bankBalance;
            /// <summary>오늘의 최저 잔액. 이자는 이 값에 붙는다.</summary>
            public long bankDayLow;
            /// <summary>지금 이자를 세고 있는 날 (한국 날짜). 비어 있으면 아직 은행을 쓴 적이 없다.</summary>
            public string bankInterestDate;
            public long bankInterestTotal;
            public long bankLedgerNextSeq = 1;
            public long bankLedgerOpeningBalance;
            public List<LedgerEntry> bankLedger = new List<LedgerEntry>();
            /// <summary>가입한 정기예금. 이 돈은 지갑에도 통장에도 없고 여기에 있다.</summary>
            public List<TermDeposit> termDeposits = new List<TermDeposit>();
            /// <summary>지금까지 만기가 된 정기예금의 수.</summary>
            public long termMaturedCount;

            // ── v8.0 공통 전투 (모든 전투 모드가 함께 쓴다. 모드별로 따로 저장하지 않는다) ──
            /// <summary>GC로 산 전투 장비와 강화 단계.</summary>
            public List<OwnedGear> gear = new List<OwnedGear>();
            public List<LoadoutEntry> loadouts = new List<LoadoutEntry>();
            public List<string> clearedFps = new List<string>();
            /// <summary>오늘 GC 보상을 받은 FPS 임무의 수.</summary>
            public int dailyFpsRewards;
            /// <summary>오늘 이 플레이어가 낮춘 도시 위험도 (1/100 단위).</summary>
            public int dailyRiskReduced;
            public int dailyRepairs;
            public int dailyFundGc;
            /// <summary>아직 위험도에 반영되지 않은 사냥 처치 수.</summary>
            public int huntKillCarry;
            /// <summary>소속 도시에 쌓은 기여도.</summary>
            public long cityContribution;
            /// <summary>다른 도시를 도와 쌓은 기여도.</summary>
            public long supportPoints;
            /// <summary>지금 나가 있는 지원 요청. 없으면 비어 있다.</summary>
            public string supportRequestId;
            /// <summary>마지막으로 전투에 참여했거나 전입한 시각. 활성 가디언을 셀 때 쓴다.</summary>
            public string lastActiveAt;
        }

        /// <summary>처리한 요청 한 건 (중복 방지 키와 그때의 결과).</summary>
        [Serializable]
        private sealed class RequestRow
        {
            public string id;
            public string kind;
            /// <summary>아이템 코드. 정기예금 요청이면 상품 코드.</summary>
            public string itemCode;
            /// <summary>구매면 낸 값, 판매면 받은 값, 사용이면 회복량.</summary>
            public int amount;
            /// <summary>은행 거래면 옮긴 금액 (정기예금이면 원금).</summary>
            public long transfer;
            /// <summary>정기예금이면 이자 (가입: 받을 이자, 중도 해지: 받지 못한 이자).</summary>
            public long interest;
        }

        [Serializable]
        private sealed class RegionRow
        {
            public string code;
            public int residents;
            public long defensePoints;
        }

        [Serializable]
        private sealed class ServerDb
        {
            public int version = 1;
            public List<PlayerRow> players = new List<PlayerRow>();
            /// <summary>주민이나 방어 포인트가 생긴 지역만 행이 있다.</summary>
            public List<RegionRow> regions = new List<RegionRow>();
        }

        private sealed class HuntSession
        {
            public string id;
            public string playerId;
            public HuntZoneDef zone;
            public DateTime lastReportUtc;
            public float allowance;
            public int nextSeq = 1;
            public HuntReportDto lastResponse;
        }

        private sealed class BattleSession
        {
            public string id;
            public string playerId;
            public StageDef stage;
            public DateTime startedUtc;
            public BattleResultDto response;
        }

        private readonly ITextStore _store;
        private readonly IClock _clock;
        private readonly System.Random _random;
        private readonly Dictionary<string, HuntSession> _sessions = new Dictionary<string, HuntSession>();
        private readonly Dictionary<string, BattleSession> _battles = new Dictionary<string, BattleSession>();
        private ServerDb _db;
        private PlayerRow _player;

        public GameDefs Defs { get; }
        public RegionCatalog Regions { get; }

        public LocalMockBackend(ITextStore store, IClock clock, GameDefs defs = null, int? randomSeed = null,
            RegionCatalog regions = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            Defs = defs ?? DefaultDefs.Create();
            Regions = regions ?? RegionCatalog.Default;
            _random = randomSeed.HasValue ? new System.Random(randomSeed.Value) : new System.Random();
        }

        // ── 접속 ────────────────────────────────────────────────

        public void Bootstrap(Action<ApiResult<MeDto>> done)
        {
            try
            {
                LoadDb();

                string guestText = _store.Read(GuestFile);
                if (string.IsNullOrEmpty(guestText))
                {
                    done(ApiResult<MeDto>.Success(CreateGuest()));
                    return;
                }

                GuestCredential credential = TryParse<GuestCredential>(guestText);
                if (credential == null || string.IsNullOrEmpty(credential.playerId)
                    || string.IsNullOrEmpty(credential.guestSecret))
                {
                    done(ApiResult<MeDto>.Fail(ApiErrors.GuestDataCorrupt,
                        "저장된 게스트 정보를 읽을 수 없어요."));
                    return;
                }

                PlayerRow row = FindPlayer(credential.playerId);
                if (row == null || row.secretHash != Hash(credential.guestSecret))
                {
                    done(ApiResult<MeDto>.Fail(ApiErrors.Unauthorized,
                        "게스트 정보가 서버 기록과 맞지 않아요."));
                    return;
                }

                _player = row;
                RollDailyIfNeeded(_player);
                if (SettleInterest(_player)) SaveDb();
                done(ApiResult<MeDto>.Success(ToMe(_player)));
            }
            catch (Exception e)
            {
                done(ApiResult<MeDto>.Fail(ApiErrors.StorageFailed, e.Message));
            }
        }

        public void StartOverAsNewGuest(Action<ApiResult<MeDto>> done)
        {
            try
            {
                LoadDb();
                // 기존 파일은 지우지 않고 이름을 바꿔 남겨 둔다.
                string stamp = _clock.UtcNow.ToString("yyyyMMdd-HHmmss");
                _store.Move(GuestFile, $"guest.unreadable-{stamp}.json");
                done(ApiResult<MeDto>.Success(CreateGuest()));
            }
            catch (Exception e)
            {
                done(ApiResult<MeDto>.Fail(ApiErrors.StorageFailed, e.Message));
            }
        }

        public void GetMe(Action<ApiResult<MeDto>> done)
        {
            if (!RequirePlayer(done)) return;
            RollDailyIfNeeded(_player);
            if (SettleInterest(_player)) SaveDb();
            done(ApiResult<MeDto>.Success(ToMe(_player)));
        }

        public void SetFlag(string key, string value, Action<ApiResult<MeDto>> done)
        {
            if (!RequirePlayer(done)) return;
            if (string.IsNullOrEmpty(key) || key.Length > 40 || (value != null && value.Length > 200))
            {
                done(ApiResult<MeDto>.Fail(ApiErrors.InvalidRequest, "진행 표시 값이 올바르지 않아요."));
                return;
            }

            FlagDto flag = _player.flags.Find(f => f.key == key);
            if (flag == null) _player.flags.Add(new FlagDto { key = key, value = value });
            else flag.value = value;
            SaveDb();
            done(ApiResult<MeDto>.Success(ToMe(_player)));
        }

        // ── 사냥 ────────────────────────────────────────────────

        public void StartHunt(string zoneCode, Action<ApiResult<HuntSessionDto>> done)
        {
            if (!RequirePlayer(done)) return;
            HuntZoneDef zone = Defs.FindZone(zoneCode);
            if (zone == null)
            {
                done(ApiResult<HuntSessionDto>.Fail(ApiErrors.NotFound, "없는 사냥 구역이에요."));
                return;
            }

            var session = new HuntSession
            {
                id = "h_" + RandomToken(10),
                playerId = _player.playerId,
                zone = zone,
                lastReportUtc = _clock.UtcNow,
                allowance = zone.burstAllowance,
            };
            _sessions[session.id] = session;
            done(ApiResult<HuntSessionDto>.Success(new HuntSessionDto { sessionId = session.id, zoneCode = zone.code }));
        }

        public void ReportHunt(string sessionId, int seq, List<KillCount> kills, Action<ApiResult<HuntReportDto>> done)
        {
            if (!RequirePlayer(done)) return;
            if (sessionId == null || !_sessions.TryGetValue(sessionId, out HuntSession session)
                || session.playerId != _player.playerId)
            {
                done(ApiResult<HuntReportDto>.Fail(ApiErrors.NotFound, "사냥 기록을 찾을 수 없어요."));
                return;
            }

            // 같은 순번의 재전송: 처음 결과를 그대로 돌려준다 (두 번 지급하지 않는다).
            if (seq == session.nextSeq - 1 && session.lastResponse != null)
            {
                done(ApiResult<HuntReportDto>.Success(session.lastResponse));
                return;
            }
            if (seq != session.nextSeq)
            {
                done(ApiResult<HuntReportDto>.Fail(ApiErrors.Conflict, "보고 순서가 맞지 않아요."));
                return;
            }

            RollDailyIfNeeded(_player);
            DateTime now = _clock.UtcNow;
            float elapsed = (float)(now - session.lastReportUtc).TotalSeconds;
            HuntVerdict verdict = HuntRules.Evaluate(
                Defs, session.zone, kills, elapsed, session.allowance, _player.dailyHuntGc, out float newAllowance);

            session.allowance = newAllowance;
            session.lastReportUtc = now;
            session.nextSeq++;

            int acceptedCount = 0;
            for (int i = 0; i < verdict.accepted.Count; i++) acceptedCount += verdict.accepted[i].count;

            int levelsGained = 0;
            var drops = new List<ItemStack>();
            if (acceptedCount > 0)
            {
                _player.confirmedKills += acceptedCount;
                if (verdict.rewardGc > 0)
                {
                    ApplyLedger(_player, verdict.rewardGc, LedgerReasons.HuntReward, session.id + "#" + seq, now);
                    _player.dailyHuntGc += verdict.rewardGc;
                }
                levelsGained = Progression.AddXp(ref _player.level, ref _player.xp, verdict.rewardXp, Defs.progression);
                RollDrops(_player, verdict.accepted, drops);
            }
            // 사냥은 소속 도시의 위험도를 조금 낮춘다. 서버가 인정한 처치만 센다.
            CityEffectDto cityEffect = acceptedCount > 0
                ? ApplyHuntToCity(_player, acceptedCount, session.id + "#" + seq, now)
                : null;

            var response = new HuntReportDto
            {
                accepted = verdict.accepted,
                rewardGc = verdict.rewardGc,
                rewardXp = verdict.rewardXp,
                gc = _player.balance,
                level = _player.level,
                xp = _player.xp,
                xpNext = Progression.XpForNextLevel(_player.level, Defs.progression),
                hpMax = Progression.MaxHp(_player.level, Defs.progression),
                levelsGained = levelsGained,
                dailyHuntGc = _player.dailyHuntGc,
                dailyHuntGcLimit = Defs.economy.dailyHuntGcLimit,
                rejectReason = verdict.rejectReason,
                drops = drops,
                inventory = drops.Count > 0 ? CopyInventory(_player) : null,
                city = cityEffect,
            };
            session.lastResponse = response;
            SaveDb();
            done(ApiResult<HuntReportDto>.Success(response));
        }

        /// <summary>
        /// 인정한 처치마다 드롭을 판정한다 (docs/ITEM_SYSTEM.md 8장). 판정은 서버가 하고,
        /// 가방에 넣을 수 없으면 떨어뜨리지 않는다.
        /// </summary>
        private void RollDrops(PlayerRow row, List<KillCount> accepted, List<ItemStack> drops)
        {
            for (int i = 0; i < accepted.Count; i++)
            {
                EnemyDef enemy = Defs.FindEnemy(accepted[i].enemyCode);
                if (enemy == null || enemy.dropChance <= 0f) continue;
                ItemDef item = Defs.FindItem(enemy.dropItemCode);
                if (item == null) continue;

                for (int n = 0; n < accepted[i].count; n++)
                {
                    if (_random.NextDouble() >= enemy.dropChance) continue;
                    if (InventoryRules.CheckAdd(Defs, row.inventory, item, 1) != null) continue;
                    InventoryRules.Add(row.inventory, item.code, 1);
                    InventoryRules.Add(drops, item.code, 1);
                }
            }
        }

        // ── 임무 ────────────────────────────────────────────────

        public void ClaimQuest(string questCode, Action<ApiResult<QuestRewardDto>> done)
        {
            if (!RequirePlayer(done)) return;
            if (questCode != QuestCodes.FirstRescue)
            {
                done(ApiResult<QuestRewardDto>.Fail(ApiErrors.NotFound, "없는 임무예요."));
                return;
            }
            if (_player.questsClaimed.Contains(questCode))
            {
                done(ApiResult<QuestRewardDto>.Fail(ApiErrors.Conflict, "이미 받은 보상이에요."));
                return;
            }
            if (_player.confirmedKills < 1)
            {
                done(ApiResult<QuestRewardDto>.Fail(ApiErrors.ValidationFailed, "아직 로봇을 물리치지 않았어요."));
                return;
            }

            int reward = Defs.economy.firstRescueRewardGc;
            ApplyLedger(_player, reward, LedgerReasons.QuestReward, questCode, _clock.UtcNow);
            _player.questsClaimed.Add(questCode);
            SaveDb();
            done(ApiResult<QuestRewardDto>.Success(new QuestRewardDto
            {
                questCode = questCode, rewardGc = reward, gc = _player.balance,
            }));
        }

        // ── 방어전 ──────────────────────────────────────────────

        public void StartBattle(string stageCode, Action<ApiResult<BattleSessionDto>> done)
        {
            if (!RequirePlayer(done)) return;
            StageDef stage = Defs.FindStage(stageCode);
            if (stage == null)
            {
                done(ApiResult<BattleSessionDto>.Fail(ApiErrors.NotFound, "없는 방어전이에요."));
                return;
            }

            var session = new BattleSession
            {
                id = "b_" + RandomToken(10),
                playerId = _player.playerId,
                stage = stage,
                startedUtc = _clock.UtcNow,
            };
            _battles[session.id] = session;
            done(ApiResult<BattleSessionDto>.Success(new BattleSessionDto { sessionId = session.id, stageCode = stage.code }));
        }

        public void FinishBattle(string sessionId, BattleReport report, Action<ApiResult<BattleResultDto>> done)
        {
            if (!RequirePlayer(done)) return;
            if (sessionId == null || !_battles.TryGetValue(sessionId, out BattleSession session)
                || session.playerId != _player.playerId)
            {
                done(ApiResult<BattleResultDto>.Fail(ApiErrors.NotFound, "방어전 기록을 찾을 수 없어요."));
                return;
            }

            // 이미 끝난 전투의 재보고: 처음 결과를 그대로 돌려준다 (두 번 지급하지 않는다).
            if (session.response != null)
            {
                done(ApiResult<BattleResultDto>.Success(session.response));
                return;
            }

            DateTime now = _clock.UtcNow;
            float elapsed = (float)(now - session.startedUtc).TotalSeconds;
            bool alreadyCleared = _player.clearedStages.Contains(session.stage.code);
            BattleVerdict verdict = BattleRules.Evaluate(session.stage, report, elapsed, alreadyCleared);

            int levelsGained = 0;
            int defensePoints = 0;
            string defenseRegion = null;
            if (verdict.accepted)
            {
                if (verdict.rewardGc > 0)
                    ApplyLedger(_player, verdict.rewardGc, LedgerReasons.BattleReward, session.id, now);
                if (verdict.rewardXp > 0)
                    levelsGained = Progression.AddXp(ref _player.level, ref _player.xp, verdict.rewardXp, Defs.progression);
                if (verdict.result == BattleResults.Win && !alreadyCleared)
                    _player.clearedStages.Add(session.stage.code);
                // 주민이 이기면 소속 도시에 방어 포인트가 쌓인다. 이방인은 쌓을 도시가 없다.
                if (verdict.result == BattleResults.Win && IsResident(_player) && session.stage.winDefensePoints > 0)
                {
                    defensePoints = session.stage.winDefensePoints;
                    defenseRegion = CombatRegionOf(_player);
                    RegionRowFor(defenseRegion).defensePoints += defensePoints;
                }
            }
            // 도시 위험도: 인정된 결과만 반영한다. 이기면 내려가고, 져도 올라가지 않는다.
            CityEffectDto cityEffect = verdict.accepted
                ? ApplyCombatToCity(_player, CombatModeCodes.Defense, verdict.result == BattleResults.Win, session.id, now)
                : null;

            session.response = new BattleResultDto
            {
                accepted = verdict.accepted,
                result = verdict.result,
                wavesCleared = verdict.wavesCleared,
                rewardGc = verdict.rewardGc,
                rewardXp = verdict.rewardXp,
                firstWin = verdict.firstWin,
                gc = _player.balance,
                level = _player.level,
                xp = _player.xp,
                xpNext = Progression.XpForNextLevel(_player.level, Defs.progression),
                hpMax = Progression.MaxHp(_player.level, Defs.progression),
                levelsGained = levelsGained,
                rejectReason = verdict.rejectReason,
                clearedStages = new List<string>(_player.clearedStages),
                defensePoints = defensePoints,
                regionCode = defensePoints > 0 ? defenseRegion : null,
                city = cityEffect,
            };
            SaveDb();
            done(ApiResult<BattleResultDto>.Success(session.response));
        }

        public void UnlockTower(string towerCode, Action<ApiResult<TowerUnlockDto>> done)
        {
            if (!RequirePlayer(done)) return;
            TowerDef tower = Defs.FindTower(towerCode);
            if (tower == null)
            {
                done(ApiResult<TowerUnlockDto>.Fail(ApiErrors.NotFound, "없는 타워예요."));
                return;
            }
            if (!tower.IsBuildable)
            {
                done(ApiResult<TowerUnlockDto>.Fail(ApiErrors.Locked,
                    tower.IsCityOwned ? "도시가 함께 세우는 시설이에요." : "아직 준비 중인 시설이에요."));
                return;
            }
            if (tower.unlockGc <= 0 || _player.unlockedTowers.Contains(towerCode))
            {
                done(ApiResult<TowerUnlockDto>.Fail(ApiErrors.Conflict, "이미 쓸 수 있는 타워예요."));
                return;
            }
            if (_player.level < tower.unlockLevel)
            {
                done(ApiResult<TowerUnlockDto>.Fail(ApiErrors.Locked, $"레벨 {tower.unlockLevel}부터 해금할 수 있어요."));
                return;
            }
            if (_player.balance < tower.unlockGc)
            {
                done(ApiResult<TowerUnlockDto>.Fail(ApiErrors.InsufficientFunds, "GC가 모자라요."));
                return;
            }

            ApplyLedger(_player, -tower.unlockGc, LedgerReasons.TowerUnlock, towerCode, _clock.UtcNow);
            _player.unlockedTowers.Add(towerCode);
            SaveDb();
            done(ApiResult<TowerUnlockDto>.Success(new TowerUnlockDto
            {
                towerCode = towerCode, costGc = tower.unlockGc, gc = _player.balance,
                unlockedTowers = UsableTowers(_player),
            }));
        }

        private List<string> UsableTowers(PlayerRow row)
        {
            var usable = new List<string>();
            for (int i = 0; i < Defs.towers.Count; i++)
            {
                TowerDef tower = Defs.towers[i];
                // 준비 중인 시설과 도시 공용 시설은 개인이 전투에서 세우는 것이 아니다.
                if (!tower.IsBuildable) continue;
                if (tower.unlockGc <= 0 || row.unlockedTowers.Contains(tower.code)) usable.Add(tower.code);
            }
            return usable;
        }

        // ── 상점·인벤토리·장착 ──────────────────────────────────

        public void GetShop(string shopCode, Action<ApiResult<ShopDto>> done)
        {
            if (!RequirePlayer(done)) return;
            ShopDef shop = Defs.FindShop(shopCode);
            if (shop == null)
            {
                done(ApiResult<ShopDto>.Fail(ApiErrors.NotFound, "없는 상점이에요."));
                return;
            }

            var dto = new ShopDto { code = shop.code, name = shop.name };
            for (int i = 0; i < shop.itemCodes.Length; i++)
            {
                ItemDef item = Defs.FindItem(shop.itemCodes[i]);
                if (item == null || item.priceGc <= 0) continue;
                dto.items.Add(new ShopItemDto
                {
                    itemCode = item.code, priceGc = item.priceGc, sellPriceGc = Math.Max(0, item.sellPriceGc),
                });
            }
            done(ApiResult<ShopDto>.Success(dto));
        }

        public void BuyItem(string shopCode, string itemCode, string requestId, Action<ApiResult<PurchaseResultDto>> done)
        {
            if (!RequirePlayer(done)) return;
            if (!IsValidRequestId(requestId))
            {
                done(ApiResult<PurchaseResultDto>.Fail(ApiErrors.InvalidRequest, "요청 번호가 올바르지 않아요."));
                return;
            }

            // 같은 요청의 재전송: 새로 사지 않고 처음 결과를 돌려준다 (두 번 차감하지 않는다).
            RequestRow seen = FindRequest(_player, requestId);
            if (seen != null)
            {
                if (seen.kind != RequestBuy || seen.itemCode != itemCode)
                {
                    done(ApiResult<PurchaseResultDto>.Fail(ApiErrors.Conflict, "이미 다른 일에 쓴 요청 번호예요."));
                    return;
                }
                done(ApiResult<PurchaseResultDto>.Success(new PurchaseResultDto
                {
                    itemCode = itemCode, priceGc = seen.amount, gc = _player.balance,
                    inventory = CopyInventory(_player), furniture = CopyFurniture(_player), duplicate = true,
                }));
                return;
            }

            // 먼저 모두 확인하고, 그 다음에 한꺼번에 바꾼다 (실제 서버에서는 하나의 DB 트랜잭션).
            string reason = InventoryRules.CheckPurchase(Defs, shopCode, itemCode, _player.inventory, _player.balance,
                _player.furniture.Count);
            if (reason != null)
            {
                done(ApiResult<PurchaseResultDto>.Fail(ApiErrors.FromItemReason(reason), PurchaseMessage(reason)));
                return;
            }

            ItemDef item = Defs.FindItem(itemCode);
            ApplyLedger(_player, -item.priceGc, LedgerReasons.ShopPurchase,
                shopCode + ":" + itemCode + ":" + requestId, _clock.UtcNow);
            if (item.IsFurniture)
            {
                // 가구는 가방이 아니라 가구 목록에, 넣어 둔 상태로 들어간다.
                _player.furniture.Add(new FurniturePiece { id = "f" + _player.furnitureNextId, itemCode = itemCode });
                _player.furnitureNextId++;
            }
            else
            {
                InventoryRules.Add(_player.inventory, itemCode, 1);
            }
            RememberRequest(_player, requestId, RequestBuy, itemCode, item.priceGc);
            SaveDb();

            done(ApiResult<PurchaseResultDto>.Success(new PurchaseResultDto
            {
                itemCode = itemCode, priceGc = item.priceGc, gc = _player.balance,
                inventory = CopyInventory(_player), furniture = CopyFurniture(_player),
            }));
        }

        public void SellItem(string shopCode, string itemCode, string requestId, Action<ApiResult<SellResultDto>> done)
        {
            if (!RequirePlayer(done)) return;
            if (!IsValidRequestId(requestId))
            {
                done(ApiResult<SellResultDto>.Fail(ApiErrors.InvalidRequest, "요청 번호가 올바르지 않아요."));
                return;
            }

            // 같은 요청의 재전송: 한 번만 판다.
            RequestRow seen = FindRequest(_player, requestId);
            if (seen != null)
            {
                if (seen.kind != RequestSell || seen.itemCode != itemCode)
                {
                    done(ApiResult<SellResultDto>.Fail(ApiErrors.Conflict, "이미 다른 일에 쓴 요청 번호예요."));
                    return;
                }
                done(ApiResult<SellResultDto>.Success(ToSellResult(_player, itemCode, seen.amount, true)));
                return;
            }

            ItemDef item = Defs.FindItem(itemCode);
            bool furniture = item != null && item.IsFurniture;
            string reason = furniture
                ? RoomRules.CheckSell(Defs, shopCode, itemCode, _player.furniture)
                : InventoryRules.CheckSell(Defs, shopCode, itemCode, _player.inventory, _player.equippedMelee);
            if (reason != null)
            {
                done(ApiResult<SellResultDto>.Fail(ApiErrors.FromItemReason(reason), SellMessage(reason)));
                return;
            }

            // 물건을 빼고, 입고 있었다면 벗기고, 값을 준다 (실제 서버에서는 하나의 DB 트랜잭션).
            if (furniture)
            {
                _player.furniture.Remove(RoomRules.FindStored(_player.furniture, itemCode));
            }
            else
            {
                InventoryRules.Remove(_player.inventory, itemCode, 1);
                if (_player.equippedRanged == itemCode) _player.equippedRanged = null;
                if (_player.equippedHead == itemCode) _player.equippedHead = null;
                if (_player.equippedBody == itemCode) _player.equippedBody = null;
            }
            ApplyLedger(_player, item.sellPriceGc, LedgerReasons.ShopSale,
                shopCode + ":" + itemCode + ":" + requestId, _clock.UtcNow);
            RememberRequest(_player, requestId, RequestSell, itemCode, item.sellPriceGc);
            SaveDb();

            done(ApiResult<SellResultDto>.Success(ToSellResult(_player, itemCode, item.sellPriceGc, false)));
        }

        private SellResultDto ToSellResult(PlayerRow row, string itemCode, int priceGc, bool duplicate) => new SellResultDto
        {
            itemCode = itemCode, priceGc = priceGc, gc = row.balance,
            inventory = CopyInventory(row), furniture = CopyFurniture(row),
            equippedMelee = row.equippedMelee,
            equippedRanged = EquippedRangedOf(row),
            equippedHead = EquippedWearOf(row, row.equippedHead),
            equippedBody = EquippedWearOf(row, row.equippedBody),
            duplicate = duplicate,
        };

        private static string SellMessage(string reason)
        {
            switch (reason)
            {
                case ItemReasons.UnknownShop: return "없는 상점이에요.";
                case ItemReasons.UnknownItem: return "없는 물건이에요.";
                case ItemReasons.NotSellable: return "이 상점에서 사 주지 않는 물건이에요.";
                case ItemReasons.NotOwned: return "가지고 있지 않아요.";
                case ItemReasons.CannotUnequip: return "지금 든 근접 무기는 팔 수 없어요.";
                case RoomReasons.StillPlaced: return RoomReasons.Message(reason);
                default: return "팔 수 없어요.";
            }
        }

        private static string PurchaseMessage(string reason)
        {
            switch (reason)
            {
                case ItemReasons.UnknownShop: return "없는 상점이에요.";
                case ItemReasons.UnknownItem: return "없는 물건이에요.";
                case ItemReasons.NotForSale: return "이 상점에서 팔지 않는 물건이에요.";
                case ItemReasons.AlreadyOwned: return "이미 가지고 있어요.";
                case ItemReasons.StackFull: return "더 가질 수 없어요.";
                case ItemReasons.BagFull: return "가방이 가득 찼어요.";
                case ItemReasons.FurnitureFull: return "가구를 더 가질 수 없어요.";
                case ItemReasons.InsufficientFunds: return "GC가 모자라요.";
                default: return "살 수 없어요.";
            }
        }

        public void EquipItem(string slot, string itemCode, Action<ApiResult<EquipResultDto>> done)
        {
            if (!RequirePlayer(done)) return;
            string reason = InventoryRules.CheckEquip(Defs, _player.inventory, slot, itemCode);
            if (reason != null)
            {
                string message = reason == ItemReasons.NotOwned ? "가지고 있지 않은 장비예요."
                    : reason == ItemReasons.CannotUnequip ? "근접 무기는 비워 둘 수 없어요."
                    : reason == ItemReasons.UnknownItem ? "없는 장비예요."
                    : "그 칸에는 장착할 수 없어요.";
                done(ApiResult<EquipResultDto>.Fail(ApiErrors.FromItemReason(reason), message));
                return;
            }

            string worn = string.IsNullOrEmpty(itemCode) ? null : itemCode;
            switch (slot)
            {
                case EquipSlots.Melee: _player.equippedMelee = itemCode; break;
                case EquipSlots.Ranged: _player.equippedRanged = worn; break;
                case EquipSlots.Head: _player.equippedHead = worn; break;
                case EquipSlots.Body: _player.equippedBody = worn; break;
            }
            SaveDb();
            done(ApiResult<EquipResultDto>.Success(new EquipResultDto
            {
                equippedMelee = _player.equippedMelee,
                equippedRanged = EquippedRangedOf(_player),
                equippedHead = EquippedWearOf(_player, _player.equippedHead),
                equippedBody = EquippedWearOf(_player, _player.equippedBody),
            }));
        }

        // ── 방과 가구 ───────────────────────────────────────────

        public void PlaceFurniture(string furnitureId, int x, int z, int rot, Action<ApiResult<RoomDto>> done)
        {
            if (!RequirePlayer(done)) return;
            string reason = RoomRules.CheckPlace(Defs, RoomLayout.Default, _player.furniture, furnitureId, x, z, rot);
            if (reason != null)
            {
                done(ApiResult<RoomDto>.Fail(
                    reason == RoomReasons.UnknownFurniture ? ApiErrors.NotFound : ApiErrors.ValidationFailed,
                    RoomReasons.Message(reason)));
                return;
            }

            FurniturePiece piece = RoomRules.Find(_player.furniture, furnitureId);
            piece.placed = true;
            piece.x = x;
            piece.z = z;
            piece.rot = rot;
            SaveDb();
            done(ApiResult<RoomDto>.Success(new RoomDto { furniture = CopyFurniture(_player) }));
        }

        public void StoreFurniture(string furnitureId, Action<ApiResult<RoomDto>> done)
        {
            if (!RequirePlayer(done)) return;
            FurniturePiece piece = RoomRules.Find(_player.furniture, furnitureId);
            if (piece == null)
            {
                done(ApiResult<RoomDto>.Fail(ApiErrors.NotFound, RoomReasons.Message(RoomReasons.UnknownFurniture)));
                return;
            }

            if (piece.placed)
            {
                piece.placed = false;
                piece.x = 0;
                piece.z = 0;
                piece.rot = 0;
                SaveDb();
            }
            done(ApiResult<RoomDto>.Success(new RoomDto { furniture = CopyFurniture(_player) }));
        }

        private static List<FurniturePiece> CopyFurniture(PlayerRow row)
        {
            var copy = new List<FurniturePiece>(row.furniture.Count);
            for (int i = 0; i < row.furniture.Count; i++) copy.Add(row.furniture[i].Clone());
            return copy;
        }

        /// <summary>입은 물건. 더는 가지고 있지 않으면 입지 않은 것으로 본다.</summary>
        private static string EquippedWearOf(PlayerRow row, string itemCode) =>
            !string.IsNullOrEmpty(itemCode) && InventoryRules.Owns(row.inventory, itemCode) ? itemCode : null;

        public void UseItem(string itemCode, string requestId, Action<ApiResult<ItemUseResultDto>> done)
        {
            if (!RequirePlayer(done)) return;
            if (!IsValidRequestId(requestId))
            {
                done(ApiResult<ItemUseResultDto>.Fail(ApiErrors.InvalidRequest, "요청 번호가 올바르지 않아요."));
                return;
            }

            // 같은 요청의 재전송: 한 번만 쓴다.
            RequestRow seen = FindRequest(_player, requestId);
            if (seen != null)
            {
                if (seen.kind != RequestUse || seen.itemCode != itemCode)
                {
                    done(ApiResult<ItemUseResultDto>.Fail(ApiErrors.Conflict, "이미 다른 일에 쓴 요청 번호예요."));
                    return;
                }
                done(ApiResult<ItemUseResultDto>.Success(new ItemUseResultDto
                {
                    itemCode = itemCode, healHp = seen.amount,
                    remaining = InventoryRules.CountOf(_player.inventory, itemCode),
                    inventory = CopyInventory(_player), duplicate = true,
                }));
                return;
            }

            string reason = InventoryRules.CheckUse(Defs, _player.inventory, itemCode);
            if (reason != null)
            {
                string message = reason == ItemReasons.NotOwned ? "가지고 있지 않아요."
                    : reason == ItemReasons.UnknownItem ? "없는 물건이에요."
                    : "쓸 수 없는 물건이에요.";
                done(ApiResult<ItemUseResultDto>.Fail(ApiErrors.FromItemReason(reason), message));
                return;
            }

            ItemDef item = Defs.FindItem(itemCode);
            InventoryRules.Remove(_player.inventory, itemCode, 1);
            RememberRequest(_player, requestId, RequestUse, itemCode, item.healHp);
            SaveDb();
            done(ApiResult<ItemUseResultDto>.Success(new ItemUseResultDto
            {
                itemCode = itemCode, healHp = item.healHp,
                remaining = InventoryRules.CountOf(_player.inventory, itemCode),
                inventory = CopyInventory(_player),
            }));
        }

        private static bool IsValidRequestId(string requestId) =>
            !string.IsNullOrEmpty(requestId) && requestId.Length <= RequestIdMaxLength;

        private static RequestRow FindRequest(PlayerRow row, string requestId) =>
            row.recentRequests.Find(r => r.id == requestId);

        private static void RememberRequest(PlayerRow row, string requestId, string kind, string itemCode, int amount)
        {
            row.recentRequests.Add(new RequestRow { id = requestId, kind = kind, itemCode = itemCode, amount = amount });
            while (row.recentRequests.Count > RequestKeep) row.recentRequests.RemoveAt(0);
        }

        private static List<ItemStack> CopyInventory(PlayerRow row)
        {
            var copy = new List<ItemStack>(row.inventory.Count);
            for (int i = 0; i < row.inventory.Count; i++)
                copy.Add(new ItemStack(row.inventory[i].itemCode, row.inventory[i].count));
            return copy;
        }

        /// <summary>장착한 원거리 무기. 더는 가지고 있지 않으면 장착하지 않은 것으로 본다.</summary>
        private static string EquippedRangedOf(PlayerRow row) =>
            !string.IsNullOrEmpty(row.equippedRanged) && InventoryRules.Owns(row.inventory, row.equippedRanged)
                ? row.equippedRanged
                : null;

        // ── 은행 ────────────────────────────────────────────────

        public void GetBank(Action<ApiResult<BankDto>> done)
        {
            if (!RequirePlayer(done)) return;
            if (SettleInterest(_player)) SaveDb();
            done(ApiResult<BankDto>.Success(ToBank(_player)));
        }

        public void BankDeposit(long amount, string requestId, Action<ApiResult<BankTransferDto>> done) =>
            BankTransfer(RequestDeposit, amount, requestId, done);

        public void BankWithdraw(long amount, string requestId, Action<ApiResult<BankTransferDto>> done) =>
            BankTransfer(RequestWithdraw, amount, requestId, done);

        private void BankTransfer(string kind, long amount, string requestId, Action<ApiResult<BankTransferDto>> done)
        {
            if (!RequirePlayer(done)) return;
            if (!IsValidRequestId(requestId))
            {
                done(ApiResult<BankTransferDto>.Fail(ApiErrors.InvalidRequest, "요청 번호가 올바르지 않아요."));
                return;
            }

            // 밀린 이자를 먼저 정산한다. 그래야 자정을 넘긴 뒤의 첫 거래가 어제 치 이자를 깎지 않는다.
            bool settled = SettleInterest(_player);

            // 같은 요청의 재전송: 한 번만 옮긴다.
            RequestRow seen = FindRequest(_player, requestId);
            if (seen != null)
            {
                if (settled) SaveDb();
                if (seen.kind != kind || seen.transfer != amount)
                {
                    done(ApiResult<BankTransferDto>.Fail(ApiErrors.Conflict, "이미 다른 일에 쓴 요청 번호예요."));
                    return;
                }
                done(ApiResult<BankTransferDto>.Success(new BankTransferDto
                {
                    amount = seen.transfer, bank = ToBank(_player), duplicate = true,
                }));
                return;
            }

            bool deposit = kind == RequestDeposit;
            string reason = deposit
                ? BankRules.CheckDeposit(Defs.bank, _player.level, amount, _player.balance)
                : BankRules.CheckWithdraw(Defs.bank, _player.level, amount, _player.bankBalance);
            if (reason != null)
            {
                if (settled) SaveDb();
                done(ApiResult<BankTransferDto>.Fail(BankError(reason), BankMessage(reason)));
                return;
            }

            // 지갑과 통장에 한 줄씩: 돈이 어디서 어디로 갔는지 남는다 (실제 서버에서는 하나의 DB 트랜잭션).
            DateTime now = _clock.UtcNow;
            if (string.IsNullOrEmpty(_player.bankInterestDate))
            {
                // 첫 거래: 오늘부터 이자를 센다. 오늘의 최저 잔액은 거래 전의 잔액이다.
                _player.bankInterestDate = KoreaDate(now);
                _player.bankDayLow = _player.bankBalance;
            }
            BankAccountState account = AccountOf(_player);
            if (deposit)
            {
                ApplyLedger(_player, -amount, LedgerReasons.BankDeposit, requestId, now);
                ApplyBankLedger(_player, amount, LedgerReasons.BankDeposit, requestId, now);
                BankRules.Deposit(account, amount);
            }
            else
            {
                ApplyBankLedger(_player, -amount, LedgerReasons.BankWithdraw, requestId, now);
                ApplyLedger(_player, amount, LedgerReasons.BankWithdraw, requestId, now);
                BankRules.Withdraw(account, amount);
            }
            _player.bankDayLow = account.dayLow;
            _player.recentRequests.Add(new RequestRow { id = requestId, kind = kind, transfer = amount });
            while (_player.recentRequests.Count > RequestKeep) _player.recentRequests.RemoveAt(0);
            SaveDb();

            done(ApiResult<BankTransferDto>.Success(new BankTransferDto { amount = amount, bank = ToBank(_player) }));
        }

        // ── 정기예금 ────────────────────────────────────────────

        public void OpenTermDeposit(string productCode, long amount, string requestId, Action<ApiResult<TermResultDto>> done)
        {
            if (!RequirePlayer(done)) return;
            if (!IsValidRequestId(requestId))
            {
                done(ApiResult<TermResultDto>.Fail(ApiErrors.InvalidRequest, "요청 번호가 올바르지 않아요."));
                return;
            }

            bool settled = SettleInterest(_player);

            // 같은 요청의 재전송: 한 번만 가입한다.
            RequestRow seen = FindRequest(_player, requestId);
            if (seen != null)
            {
                if (settled) SaveDb();
                if (seen.kind != RequestTermOpen || seen.itemCode != productCode || seen.transfer != amount)
                {
                    done(ApiResult<TermResultDto>.Fail(ApiErrors.Conflict, "이미 다른 일에 쓴 요청 번호예요."));
                    return;
                }
                done(ApiResult<TermResultDto>.Success(new TermResultDto
                {
                    productCode = productCode, amount = seen.transfer, interest = seen.interest,
                    bank = ToBank(_player), duplicate = true,
                }));
                return;
            }

            string reason = BankRules.CheckOpenTerm(Defs.bank, _player.level, productCode, amount, _player.balance,
                _player.termDeposits);
            if (reason != null)
            {
                if (settled) SaveDb();
                done(ApiResult<TermResultDto>.Fail(BankError(reason), BankMessage(reason, productCode)));
                return;
            }

            // 지갑에서 돈이 나가고 정기예금이 생긴다 (실제 서버에서는 하나의 DB 트랜잭션).
            DateTime now = _clock.UtcNow;
            string today = KoreaDate(now);
            if (string.IsNullOrEmpty(_player.bankInterestDate))
            {
                // 은행을 처음 쓴다: 오늘부터 날짜를 센다. 그래야 만기일에 정산이 돈다.
                _player.bankInterestDate = today;
                _player.bankDayLow = _player.bankBalance;
            }
            TermDeposit term = BankRules.OpenTerm(Defs.bank.FindTerm(productCode), amount, today);
            ApplyLedger(_player, -amount, LedgerReasons.TermOpen, productCode + ":" + requestId, now);
            _player.termDeposits.Add(term);
            RememberBankRequest(_player, requestId, RequestTermOpen, productCode, amount, term.interest);
            SaveDb();

            done(ApiResult<TermResultDto>.Success(new TermResultDto
            {
                productCode = productCode, amount = amount, interest = term.interest, bank = ToBank(_player),
            }));
        }

        public void CancelTermDeposit(string productCode, string requestId, Action<ApiResult<TermResultDto>> done)
        {
            if (!RequirePlayer(done)) return;
            if (!IsValidRequestId(requestId))
            {
                done(ApiResult<TermResultDto>.Fail(ApiErrors.InvalidRequest, "요청 번호가 올바르지 않아요."));
                return;
            }

            // 먼저 정산한다. 오늘이 만기일이면 이 요청보다 만기가 먼저다 (이자를 잃지 않는다).
            bool settled = SettleInterest(_player);

            RequestRow seen = FindRequest(_player, requestId);
            if (seen != null)
            {
                if (settled) SaveDb();
                if (seen.kind != RequestTermCancel || seen.itemCode != productCode)
                {
                    done(ApiResult<TermResultDto>.Fail(ApiErrors.Conflict, "이미 다른 일에 쓴 요청 번호예요."));
                    return;
                }
                done(ApiResult<TermResultDto>.Success(new TermResultDto
                {
                    productCode = productCode, amount = seen.transfer, interest = seen.interest,
                    bank = ToBank(_player), duplicate = true,
                }));
                return;
            }

            string reason = BankRules.CheckCancelTerm(Defs.bank, productCode, _player.termDeposits);
            if (reason != null)
            {
                if (settled) SaveDb();
                done(ApiResult<TermResultDto>.Fail(BankError(reason), BankMessage(reason, productCode)));
                return;
            }

            // 원금만 통장으로 돌아온다. 오늘 들어온 돈이라 오늘 치 보통예금 이자는 붙지 않는다.
            DateTime now = _clock.UtcNow;
            TermDeposit term = BankRules.FindTerm(_player.termDeposits, productCode);
            _player.termDeposits.Remove(term);
            ApplyBankLedger(_player, term.principal, LedgerReasons.TermCancel, KoreaDate(now) + ":" + productCode, now);
            RememberBankRequest(_player, requestId, RequestTermCancel, productCode, term.principal, term.interest);
            SaveDb();

            done(ApiResult<TermResultDto>.Success(new TermResultDto
            {
                productCode = productCode, amount = term.principal, interest = term.interest, bank = ToBank(_player),
            }));
        }

        private static void RememberBankRequest(PlayerRow row, string requestId, string kind, string productCode,
            long transfer, long interest)
        {
            row.recentRequests.Add(new RequestRow
            {
                id = requestId, kind = kind, itemCode = productCode, transfer = transfer, interest = interest,
            });
            while (row.recentRequests.Count > RequestKeep) row.recentRequests.RemoveAt(0);
        }

        private static string BankError(string reason)
        {
            switch (reason)
            {
                case BankReasons.Locked: return ApiErrors.Locked;
                case BankReasons.InsufficientWallet:
                case BankReasons.InsufficientAccount:
                    return ApiErrors.InsufficientFunds;
                case BankReasons.UnknownProduct:
                case BankReasons.NotOpen:
                    return ApiErrors.NotFound;
                case BankReasons.AlreadyOpen: return ApiErrors.Conflict;
                default: return ApiErrors.InvalidRequest;
            }
        }

        private string BankMessage(string reason, string productCode = null)
        {
            TermProductDef product = productCode != null ? Defs.bank.FindTerm(productCode) : null;
            switch (reason)
            {
                case BankReasons.Locked: return $"은행은 레벨 {Defs.bank.unlockLevel}부터 이용할 수 있어요.";
                case BankReasons.InsufficientWallet: return "지갑에 그만큼의 GC가 없어요.";
                case BankReasons.InsufficientAccount: return "통장에 그만큼의 GC가 없어요.";
                case BankReasons.UnknownProduct: return "없는 정기예금이에요.";
                case BankReasons.AlreadyOpen: return "이미 가입한 정기예금이에요.";
                case BankReasons.NotOpen: return "가입한 정기예금이 없어요. 만기가 되어 통장에 들어왔을 수 있어요.";
                case BankReasons.BelowMinimum:
                    return product != null ? $"{product.minAmount:N0} GC부터 넣을 수 있어요." : "금액이 너무 적어요.";
                case BankReasons.AboveMaximum:
                    return product != null ? $"{product.maxAmount:N0} GC까지 넣을 수 있어요." : "금액이 너무 많아요.";
                default: return "금액이 올바르지 않아요.";
            }
        }

        private static BankAccountState AccountOf(PlayerRow row) => new BankAccountState
        {
            balance = row.bankBalance, dayLow = row.bankDayLow, interestDate = row.bankInterestDate,
        };

        /// <summary>
        /// 밀린 이자를 오늘(한국 날짜)까지 하루씩 지급한다. 실제 서버에서는 자정 뒤의 예약 작업이 하는 일이고,
        /// 여기서는 플레이어의 데이터를 읽을 때마다 확인한다. 같은 날에는 몇 번을 불러도 한 번만 지급된다.
        /// </summary>
        /// <returns>저장할 변화가 있었는가.</returns>
        private bool SettleInterest(PlayerRow row)
        {
            // 은행을 쓴 적이 없는 계좌는 건드리지 않는다.
            if (string.IsNullOrEmpty(row.bankInterestDate) && row.bankBalance == 0 && row.termDeposits.Count == 0) return false;

            DateTime now = _clock.UtcNow;
            string today = KoreaDate(now);
            if (row.bankInterestDate == today) return false;

            BankAccountState account = AccountOf(row);
            List<BankEvent> events = BankRules.SettleAll(account, row.termDeposits, today, Defs.bank);
            for (int i = 0; i < events.Count; i++)
            {
                BankEvent e = events[i];
                if (e.kind == BankEvent.TermMatured)
                {
                    // 만기: 원금과 약속한 이자가 통장으로 들어온다. 원장에는 따로 적는다.
                    string reference = e.date + ":" + e.productCode;
                    ApplyBankLedger(row, e.amount, LedgerReasons.TermMatured, reference, now);
                    if (e.termInterest > 0)
                    {
                        ApplyBankLedger(row, e.termInterest, LedgerReasons.TermInterest, reference, now);
                        row.bankInterestTotal += e.termInterest;
                    }
                    row.termMaturedCount++;
                }
                else
                {
                    ApplyBankLedger(row, e.amount, LedgerReasons.BankInterest, e.date, now);
                    row.bankInterestTotal += e.amount;
                }
            }
            bool changed = events.Count > 0 || row.bankInterestDate != account.interestDate || row.bankDayLow != account.dayLow;
            row.bankDayLow = account.dayLow;
            row.bankInterestDate = account.interestDate;
            return changed;
        }

        private void ApplyBankLedger(PlayerRow row, long delta, string reason, string reference, DateTime now)
        {
            var account = new Wallet(row.bankBalance, row.bankLedger, row.bankLedgerNextSeq);
            account.Apply(delta, reason, reference, now);
            row.bankBalance = account.Balance;
            row.bankLedgerNextSeq = account.NextSeq;

            while (row.bankLedger.Count > LedgerKeep)
            {
                row.bankLedgerOpeningBalance = row.bankLedger[0].balanceAfter;
                row.bankLedger.RemoveAt(0);
            }
        }

        private BankDto ToBank(PlayerRow row)
        {
            BankAccountState account = AccountOf(row);
            var dto = new BankDto
            {
                unlocked = row.level >= Defs.bank.unlockLevel,
                unlockLevel = Defs.bank.unlockLevel,
                gc = row.balance,
                bankGc = row.bankBalance,
                interestTomorrow = BankRules.InterestTomorrow(account, Defs.bank),
                interestWhenSettled = BankRules.InterestWhenSettled(account, Defs.bank),
                dailyInterestBp = Defs.bank.dailyInterestBp,
                interestCap = Defs.bank.interestCap,
                balanceForOneGc = BankRules.BalanceForOneGc(Defs.bank),
                interestTotal = row.bankInterestTotal,
            };
            for (int i = row.bankLedger.Count - 1; i >= 0 && dto.recent.Count < BankRecentCount; i--)
            {
                LedgerEntry entry = row.bankLedger[i];
                dto.recent.Add(new BankEntryDto
                {
                    reason = entry.reason, delta = entry.delta, balanceAfter = entry.balanceAfter,
                    date = EntryDate(entry),
                });
            }

            string today = KoreaDate(_clock.UtcNow);
            for (int i = 0; i < Defs.bank.termProducts.Count; i++)
            {
                TermProductDef product = Defs.bank.termProducts[i];
                TermDeposit term = BankRules.FindTerm(row.termDeposits, product.code);
                dto.terms.Add(new TermProductDto
                {
                    code = product.code, name = product.name, days = product.days, rateBp = product.rateBp,
                    minAmount = product.minAmount, maxAmount = product.maxAmount,
                    open = term != null,
                    principal = term != null ? term.principal : 0,
                    interest = term != null ? term.interest : 0,
                    maturesDate = term != null ? term.maturesDate : null,
                    daysLeft = term != null ? BankRules.DaysLeft(term, today) : 0,
                });
            }
            dto.termGc = TermTotal(row);
            dto.termMaturedCount = row.termMaturedCount;
            return dto;
        }

        private static long TermTotal(PlayerRow row)
        {
            long total = 0;
            for (int i = 0; i < row.termDeposits.Count; i++) total += row.termDeposits[i].principal;
            return total;
        }

        /// <summary>
        /// 거래 내역에 보여 줄 날짜. 이자는 "어느 날의 이자"인지를, 정기예금은 만기일이나 찾은 날을,
        /// 나머지는 거래한 날(한국 날짜)을 쓴다.
        /// </summary>
        private static string EntryDate(LedgerEntry entry)
        {
            if (entry.reason == LedgerReasons.BankInterest && !string.IsNullOrEmpty(entry.reference)) return entry.reference;
            bool term = entry.reason == LedgerReasons.TermMatured || entry.reason == LedgerReasons.TermInterest
                || entry.reason == LedgerReasons.TermCancel;
            if (term && entry.reference != null && entry.reference.Length >= 10) return entry.reference.Substring(0, 10);
            return DateTime.TryParse(entry.createdAt, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out DateTime at)
                ? KoreaDate(at.ToUniversalTime())
                : "";
        }

        // ── 지역과 전입신고 ─────────────────────────────────────

        public void GetRegions(Action<ApiResult<RegionsDto>> done)
        {
            if (!RequirePlayer(done)) return;
            var dto = new RegionsDto();
            for (int i = 0; i < _db.regions.Count; i++)
            {
                RegionRow row = _db.regions[i];
                if (row.residents > 0 || row.defensePoints > 0) dto.regions.Add(ToStatus(row));
            }
            done(ApiResult<RegionsDto>.Success(dto));
        }

        public void CheckNickname(string nickname, Action<ApiResult<NicknameCheckDto>> done)
        {
            if (!RequirePlayer(done)) return;
            string reason = NicknameProblem(nickname);
            done(ApiResult<NicknameCheckDto>.Success(new NicknameCheckDto
            {
                nickname = NicknameRules.Normalize(nickname),
                available = reason == null,
                reason = reason,
                message = NicknameRules.Message(reason),
            }));
        }

        public void RegisterResidency(string regionCode, string nickname, Action<ApiResult<ResidencyResultDto>> done)
        {
            if (!RequirePlayer(done)) return;

            // 먼저 모두 확인하고, 그 다음에 한꺼번에 바꾼다 (실제 서버에서는 하나의 DB 트랜잭션).
            if (IsResident(_player))
            {
                done(ApiResult<ResidencyResultDto>.Fail(ApiErrors.AlreadyResident, "이미 전입신고를 했어요."));
                return;
            }
            if (Regions.FindRegion(regionCode) == null)
            {
                done(ApiResult<ResidencyResultDto>.Fail(ApiErrors.NotFound, "없는 지역이에요."));
                return;
            }
            string reason = NicknameProblem(nickname);
            if (reason != null)
            {
                done(ApiResult<ResidencyResultDto>.Fail(
                    reason == NicknameRules.Taken ? ApiErrors.NicknameTaken : ApiErrors.NicknameInvalid,
                    NicknameRules.Message(reason)));
                return;
            }

            DateTime now = _clock.UtcNow;
            int bonus = 0;
            if (!_player.residencyBonusPaid && Defs.economy.residencyBonusGc > 0)
            {
                bonus = Defs.economy.residencyBonusGc;
                ApplyLedger(_player, bonus, LedgerReasons.ResidencyBonus, regionCode, now);
                _player.residencyBonusPaid = true;
            }
            _player.regionCode = regionCode;
            _player.displayName = NicknameRules.Normalize(nickname);
            _player.registeredAt = now.ToString("o");
            _player.lastActiveAt = _player.registeredAt;
            RegionRow region = RegionRowFor(regionCode);
            region.residents++;
            SaveDb();

            done(ApiResult<ResidencyResultDto>.Success(new ResidencyResultDto
            {
                regionCode = regionCode,
                nickname = _player.displayName,
                bonusGc = bonus,
                region = ToStatus(region),
                me = ToMe(_player),
            }));
        }

        private static bool IsResident(PlayerRow row) => !string.IsNullOrEmpty(row.regionCode);

        /// <summary>닉네임을 쓸 수 없는 이유. 쓸 수 있으면 null.</summary>
        private string NicknameProblem(string nickname)
        {
            string reason = NicknameRules.Check(nickname);
            if (reason != null) return reason;
            string key = NicknameRules.Key(nickname);
            for (int i = 0; i < _db.players.Count; i++)
            {
                PlayerRow other = _db.players[i];
                if (other != _player && NicknameRules.Key(other.displayName) == key) return NicknameRules.Taken;
            }
            return null;
        }

        /// <summary>그 지역의 행. 없으면 만든다.</summary>
        private RegionRow RegionRowFor(string regionCode)
        {
            RegionRow row = _db.regions.Find(r => r.code == regionCode);
            if (row == null)
            {
                row = new RegionRow { code = regionCode };
                _db.regions.Add(row);
            }
            return row;
        }

        private static RegionStatusDto ToStatus(RegionRow row) => new RegionStatusDto
        {
            regionCode = row.code, residents = row.residents, defensePoints = row.defensePoints,
        };

        // ── 점검용 ──────────────────────────────────────────────

        /// <summary>현재 플레이어의 원장과 잔액이 맞는지 검사한다. 맞으면 null.</summary>
        public string FindLedgerInconsistency()
        {
            if (_player == null) return "접속한 플레이어가 없음";
            var wallet = new Wallet(_player.balance, _player.ledger, _player.ledgerNextSeq);
            string problem = wallet.FindInconsistency(_player.ledgerOpeningBalance);
            if (problem != null) return "지갑: " + problem;

            var account = new Wallet(_player.bankBalance, _player.bankLedger, _player.bankLedgerNextSeq);
            problem = account.FindInconsistency(_player.bankLedgerOpeningBalance);
            if (problem != null) return "통장: " + problem;
            if (_player.bankDayLow < 0 || _player.bankDayLow > _player.bankBalance) return "통장: 오늘의 최저 잔액이 잔액보다 큼";
            for (int i = 0; i < _player.termDeposits.Count; i++)
            {
                TermDeposit term = _player.termDeposits[i];
                if (term.principal <= 0 || term.interest < 0) return "정기예금: 원금이나 이자가 올바르지 않음";
                if (BankRules.FindTerm(_player.termDeposits, term.productCode) != term) return "정기예금: 같은 상품에 두 번 가입함";
            }
            return null;
        }

        /// <summary>현재 플레이어가 가진 돈의 합: 지갑 + 통장 + 정기예금 원금. 돈이 새거나 생기지 않는지 볼 때 쓴다.</summary>
        public long CurrentTotalGc
        {
            get
            {
                return _player == null ? 0 : _player.balance + _player.bankBalance + TermTotal(_player);
            }
        }

        public IReadOnlyList<LedgerEntry> CurrentLedger =>
            _player != null ? _player.ledger : (IReadOnlyList<LedgerEntry>)Array.Empty<LedgerEntry>();

        /// <summary>현재 플레이어의 통장 원장.</summary>
        public IReadOnlyList<LedgerEntry> CurrentBankLedger =>
            _player != null ? _player.bankLedger : (IReadOnlyList<LedgerEntry>)Array.Empty<LedgerEntry>();

        // ── 내부 ────────────────────────────────────────────────

        private bool RequirePlayer<T>(Action<ApiResult<T>> done)
        {
            if (_player != null) return true;
            done(ApiResult<T>.Fail(ApiErrors.Unauthorized, "먼저 접속해야 해요."));
            return false;
        }

        private MeDto CreateGuest()
        {
            string playerId = "p_" + RandomToken(12);
            string secret = RandomSecret();
            var row = new PlayerRow
            {
                playerId = playerId,
                secretHash = Hash(secret),
                displayName = "이방인-" + _random.Next(1000, 10000),
                createdAt = _clock.UtcNow.ToString("o"),
                equippedMelee = WeaponCodes.SparkBaton,
                dailyDate = KoreaDate(_clock.UtcNow),
            };
            row.flags.Add(new FlagDto { key = FlagKeys.TutorialStep, value = "0" });
            row.inventory.Add(new ItemStack(ItemCodes.SparkBaton, 1));
            _db.players.Add(row);
            _player = row;

            // 서버 기록을 먼저 저장한다. 그 뒤에 기기 쪽 게스트 정보를 쓴다.
            SaveDb();
            _store.Write(GuestFile, JsonUtility.ToJson(new GuestCredential { playerId = playerId, guestSecret = secret }));
            return ToMe(row);
        }

        private void ApplyLedger(PlayerRow row, long delta, string reason, string reference, DateTime now)
        {
            var wallet = new Wallet(row.balance, row.ledger, row.ledgerNextSeq);
            wallet.Apply(delta, reason, reference, now);
            row.balance = wallet.Balance;
            row.ledgerNextSeq = wallet.NextSeq;

            while (row.ledger.Count > LedgerKeep)
            {
                row.ledgerOpeningBalance = row.ledger[0].balanceAfter;
                row.ledger.RemoveAt(0);
            }
        }

        private void RollDailyIfNeeded(PlayerRow row)
        {
            string today = KoreaDate(_clock.UtcNow);
            if (row.dailyDate == today) return;
            row.dailyDate = today;
            row.dailyHuntGc = 0;
            row.dailyFpsRewards = 0;
            row.dailyRiskReduced = 0;
            row.dailyRepairs = 0;
            row.dailyFundGc = 0;
            SaveDb();
        }

        /// <summary>일일 한도의 날짜 경계는 한국 시간 자정이다.</summary>
        private static string KoreaDate(DateTime utc) => utc.AddHours(9).ToString("yyyy-MM-dd");

        private MeDto ToMe(PlayerRow row)
        {
            var me = new MeDto
            {
                playerId = row.playerId,
                displayName = row.displayName,
                residency = IsResident(row) ? ResidencyStatus.Resident : ResidencyStatus.Stranger,
                regionCode = IsResident(row) ? row.regionCode : null,
                level = row.level,
                xp = row.xp,
                xpNext = Progression.XpForNextLevel(row.level, Defs.progression),
                hpMax = Progression.MaxHp(row.level, Defs.progression),
                gc = row.balance,
                bankGc = row.bankBalance,
                bankInterestTotal = row.bankInterestTotal,
                equippedMelee = row.equippedMelee,
                equippedRanged = EquippedRangedOf(row),
                equippedHead = EquippedWearOf(row, row.equippedHead),
                equippedBody = EquippedWearOf(row, row.equippedBody),
                inventory = CopyInventory(row),
                furniture = CopyFurniture(row),
                termGc = TermTotal(row),
                termMaturedCount = row.termMaturedCount,
                dailyHuntGc = row.dailyHuntGc,
                dailyHuntGcLimit = Defs.economy.dailyHuntGcLimit,
                unlockedTowers = UsableTowers(row),
                clearedStages = new List<string>(row.clearedStages),
                gear = CopyGear(row),
                loadouts = CopyLoadouts(row),
                clearedFpsMissions = new List<string>(row.clearedFps),
                cityContribution = row.cityContribution,
                supportRegionCode = SupportRegionOf(row),
            };
            for (int i = 0; i < row.flags.Count; i++)
                me.flags.Add(new FlagDto { key = row.flags[i].key, value = row.flags[i].value });
            return me;
        }

        private PlayerRow FindPlayer(string playerId) => _db.players.Find(p => p.playerId == playerId);

        private void LoadDb()
        {
            if (_db != null) return;
            string text = _store.Read(ServerFile);
            if (string.IsNullOrEmpty(text))
            {
                _db = new ServerDb();
                return;
            }
            ServerDb parsed = TryParse<ServerDb>(text);
            if (parsed == null || parsed.players == null)
                throw new InvalidOperationException("모의 서버 데이터를 읽을 수 없어요: " + ServerFile);
            // 지역 기능이 생기기 전에 저장된 데이터에는 지역 목록이 없다.
            if (parsed.regions == null) parsed.regions = new List<RegionRow>();
            for (int i = 0; i < parsed.players.Count; i++) UpgradeRow(parsed.players[i]);
            _db = parsed;
        }

        /// <summary>
        /// 아이템 기능이 생기기 전에 저장된 플레이어를 지금 형식에 맞춘다.
        /// 그때의 플레이어는 가방 없이 스파크 배턴만 들고 있었다. 있는 데이터는 건드리지 않는다.
        /// </summary>
        private static void UpgradeRow(PlayerRow row)
        {
            if (row.inventory == null) row.inventory = new List<ItemStack>();
            if (row.recentRequests == null) row.recentRequests = new List<RequestRow>();
            if (string.IsNullOrEmpty(row.equippedMelee)) row.equippedMelee = WeaponCodes.SparkBaton;
            if (!InventoryRules.Owns(row.inventory, row.equippedMelee))
                row.inventory.Insert(0, new ItemStack(row.equippedMelee, 1));
            // 은행 기능이 생기기 전의 데이터: 빈 통장.
            if (row.bankLedger == null) row.bankLedger = new List<LedgerEntry>();
            if (row.bankLedgerNextSeq < 1) row.bankLedgerNextSeq = 1;
            // 가구와 정기예금이 생기기 전의 데이터: 가구도 정기예금도 없다.
            if (row.furniture == null) row.furniture = new List<FurniturePiece>();
            if (row.furnitureNextId < 1) row.furnitureNextId = 1;
            if (row.termDeposits == null) row.termDeposits = new List<TermDeposit>();
            // 전투 장비가 생기기 전의 데이터: 산 장비가 없다. 무료 기본 장비는 누구나 가진 것으로 친다.
            if (row.gear == null) row.gear = new List<OwnedGear>();
            if (row.loadouts == null) row.loadouts = new List<LoadoutEntry>();
            if (row.clearedFps == null) row.clearedFps = new List<string>();
        }

        private void SaveDb() => _store.Write(ServerFile, JsonUtility.ToJson(_db));

        private static T TryParse<T>(string json) where T : class
        {
            try { return JsonUtility.FromJson<T>(json); }
            catch (ArgumentException) { return null; }
        }

        private static string Hash(string text)
        {
            using (var sha = SHA256.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
                var builder = new StringBuilder(bytes.Length * 2);
                for (int i = 0; i < bytes.Length; i++) builder.Append(bytes[i].ToString("x2"));
                return builder.ToString();
            }
        }

        private static string RandomSecret()
        {
            byte[] bytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return Convert.ToBase64String(bytes);
        }

        private static string RandomToken(int length)
        {
            const string alphabet = "abcdefghjkmnpqrstuvwxyz23456789";
            byte[] bytes = new byte[length];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            var builder = new StringBuilder(length);
            for (int i = 0; i < length; i++) builder.Append(alphabet[bytes[i] % alphabet.Length]);
            return builder.ToString();
        }
    }
}
