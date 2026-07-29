// INVENTORY STACK + CLIENT MERGE SYNC + PERFORMANCE BUILD 1.3.0
//
// 기능
// - 같은 판매/제작 자원의 슬롯당 최대 수량을 ModConfig에서 조절합니다.
// - 기존 슬롯에 같은 자원이 있고 현재 최대 수량 미만이면 빈 슬롯 대신 기존 슬롯에 합칩니다.
// - 판매, 모닥불 재료 소비, 한 개 드롭 시 스택 전체가 아니라 1개만 감소합니다.
// - 스택이 1개일 때 제거하면 기존 PEAK 방식대로 슬롯이 비워집니다.
// - 슬롯 우측 아래에 현재 스택 수량을 표시합니다.
// - 수량과 최대 적재량은 Master Client가 관리하고 Photon 이벤트로 전원에게 동기화합니다.
// - 일반 클라이언트가 기존 스택에 아이템을 주우면 호스트에 신뢰성 병합 요청을 보냅니다.
// - 빠른 연속 수집은 로컬 예약 수량으로 최대 적재량을 초과하지 않도록 제한합니다.
// - 새 플레이어가 들어오면 호스트가 현재 모든 스택 수량을 전송합니다.
// - Airport, Title, Pretitle에서는 작동하지 않습니다.
// - MapHandler 씬 검색 결과를 캐시하여 Update와 Harmony 패치에서 매 프레임 검색하지 않습니다.
// - 중복 제거 가드 정리 시 임시 List 생성을 재사용하여 GC 할당을 줄입니다.
// - Photon 예약 이벤트 코드 충돌을 제거하여 일반 클라이언트도 스택 수량을 수신합니다.
// - 스택 수량/스냅샷 수신 직후 로컬 인벤토리 UI를 강제로 다시 갱신합니다.
// - Q로 버린 자원은 다시 주워도 수집량 배율을 받지 않고 정확히 1개로 처리합니다.
// - 일반 인벤토리의 자원 스택을 배낭에 넣을 때 스택 전체 수량을 한 칸에 그대로 이전합니다.
// - 배낭 내부 슬롯도 별도 스택 수량으로 동기화하고 배낭 UI에 xN을 표시합니다.
// - CraftHub 제작 목록의 등산용, 음식용, 회복용, 기타, 부활 아이템도 중첩합니다.
// - 붕대처럼 같은 제작품을 여러 번 구매하면 별도 슬롯이 아니라 x2, x3으로 합쳐집니다.
// - x3, x4 스택을 배낭에 넣으면 대표 ItemInstanceData와 전체 수량을 함께 보존합니다.
// - 원본 배낭 RPC가 실제 이동에 성공한 뒤에만 배낭 스택 수량을 확정합니다.
// - 배낭의 원래 4칸은 유지하되 같은 ItemID는 하나의 배낭 슬롯에 x2, x3으로 합칩니다.
// - 예: 소라고둥 3개를 가진 상태에서 하나를 넣으면 배낭 한 칸에 소라고둥 x3으로 보관합니다.
// - 일반 슬롯 1~3과 tempFullSlot에 있는 동일 아이템 전체 수량을 한 번에 이동합니다.
// - 배낭 RPC가 실제로 수정한 BackpackData를 직접 검증해 바닥 배낭/착용 배낭을 구분합니다.
// - Player.EmptySlot 실행 시점까지 전체 스택 예약을 유지해 x2가 1개만 차감되는 문제를 막습니다.
// - 배낭에서 꺼낼 때도 배낭 슬롯의 xN을 새 인벤토리 슬롯에 그대로 이전합니다.
// - 바닥 배낭 회수 시 착용 배낭을 재조회하지 않고 실제 저장된 슬롯 수량을 직접 사용합니다.
// - 이미 x9가 든 배낭 슬롯에 x1을 추가하면 기존 수량을 덮지 않고 x10으로 합산합니다.
// - 배낭 UI가 임시로 꺼낸 대표 아이템 1개는 추가 수량에서 제외해 x11 중복을 방지합니다.
// - 서로 다른 배낭의 같은 슬롯 번호가 수량을 공유하지 않도록 대표 GUID별 배낭 수량을 보존합니다.
// - 배낭 회수 시 기존 인벤토리 수량 + 배낭 수량을 정확히 합산합니다.
// - 같은 AddItem Harmony Prefix가 중복 등록되어도 한 호출에서 수량을 한 번만 증가시킵니다.
// - Q 드롭은 기존대로 x3→x2처럼 정확히 1개만 감소합니다.
// - 배낭 수량은 실제 BackpackData 객체와 내부 슬롯 번호로도 보존해 회수 시 xN을 확실히 복원합니다.
// - 기존 인벤토리 x1 + 배낭 x2는 회수 후 x3, 기존 x1 + 배낭 x50은 x51이 됩니다.
// - 배낭 아이템을 기존 스택에 회수할 때 Player.AddItem의 null 반환 슬롯을 사용하지 않고
//   RequestPickup 단계에서 기존 수량과 배낭 전체 수량을 직접 합산합니다.
//
// 스택 대상
// - 판매용 자원 11종: Spawn.IsSaleResourceId(itemID)
// - 횃불 ItemID 109
// - CraftHub 제작 목록에 등록되는 등산, 음식, 회복, 기타, 부활, 최종 아이템
//
// 제작 아이템은 동일 ItemID 기준으로 합칩니다.
// 스택 내 개별 인스턴스 데이터는 첫 슬롯의 데이터를 대표값으로 사용합니다.
//
// 중요
// - Delete.cs가 PatchAll(typeof(Delete).Assembly)을 실행하므로
//   Inventory.cs에서 Harmony.PatchAll을 다시 실행하지 않습니다.
// - Delete.cs, Spawn.cs, LongE.cs, Open.cs, Campfire.cs, Inventory.cs를
//   하나의 Craft PEAK.dll로 빌드하세요.
// - 리플렉션을 사용하지 않습니다.

using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using ExitGames.Client.Photon;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using Zorro.Core;
using Zorro.Core.Serizalization;

namespace CraftPeak
{
    [BepInPlugin(
        PluginGuid,
        PluginName,
        PluginVersion)]
    [BepInDependency(
        Delete.PluginGuid,
        BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency(
        Spawn.PluginGuid,
        BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency(
        "com.github.PEAKModding.PEAKLib.ModConfig",
        BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class InventoryStack :
        BaseUnityPlugin,
        IOnEventCallback,
        IInRoomCallbacks
    {
        public const string PluginGuid =
            "com.sappheiros.crafting.inventory";

        public const string PluginName =
            "Craft PEAK Inventory Stack";

        public const string PluginVersion =
            "1.6.9";

        private const int DefaultMaximumStackCount = 10;
        private const int MinimumConfigStackCount = 1;
        private const int MaximumConfigStackCount = 100;

        public static int MaximumStackCount
        {
            get;
            private set;
        } =
            DefaultMaximumStackCount;

        public const ushort TorchItemId = 109;

        private static readonly HashSet<ushort>
            craftStackableItemIds =
                new HashSet<ushort>();

        private static bool craftStackableItemsRegistered;

        private static float nextCraftStackableRegistrationAt;

        // CraftHub_RESTORED_v2.11.2의 제작 출력 별칭과 동일한 목록입니다.
        // ItemDatabase의 표시 이름 또는 GameObject 이름과 대조해 ItemID를 등록합니다.
        private static readonly string[]
            CraftStackableAliases =
            {
                "Red Crispberry", "Red Crisp Berry", "빨간색 아삭 열매",
                "Coconut Half", "Half Coconut", "코코넛 반쪽",
                "Trail Mix", "TrailMix", "트레일 믹스",
                "Yellow Berrynana", "Yellow Banana", "노란색 열매나나",
                "Blue Mushroom Berry", "Blue MushroomBerry", "파란색 버섯열매",
                "Sports Drink", "SportsDrink", "스포츠 드링크",

                "Backpack", "배낭",
                "Piton", "피톤",
                "Energy Drink", "EnergyDrink", "에너지 드링크",
                "Balloon", "풍선",
                "Portable Stove", "PortableStove", "휴대용 스토브",

                "Pirate Compass", "PirateCompass", "해적 나침반",
                "Snow", "눈",
                "Aloe Vera", "AloeVera", "알로에 베라",
                "Heat Pack", "HeatPack", "핫팩",
                "Torch", "횃불",
                "Bandage", "붕대",

                "Tick", "진드기",
                "Red Clusterberry", "Red Cluster Berry", "빨간 송송열매",
                "Kingberry Green", "KingberryGreen", "녹색 대왕열매",
                "FortifiedMilk", "Fortified Milk", "강화 우유", "강화우유",
                "Marshmallow", "마시멜로우",
                "Granola Bar", "GranolaBar", "그래놀라바",
                "Puff Mushroom", "PuffMushroom", "통통버섯",
                "Trumpet Mushroom", "TrumpetMushroom", "나팔버섯",
                "Bundle Mushroom", "BundleMushroom", "다발버섯",
                "Button Mushroom", "ButtonMushroom", "단추버섯",
                "Orange Winterberry", "Orange Winter Berry", "주황 겨울열매",
                "Red Thornberry", "Red Thorn Berry", "빨간 가시열매",
                "Purple Mushroom Berry", "Purple MushroomBerry", "보라색 버섯열매",

                "Shelf Fungus", "ShelfFungus", "선반 균류",
                "Cloud Fungus", "CloudFungus", "구름균류",
                "Rope Spool", "RopeSpool", "밧줄타래",
                "Bounce Fungus", "BounceFungus", "방방 균류",
                "Checkpoint Flag", "CheckpointFlag", "체크포인트 깃발",

                "Lantern", "랜턴",
                "Antidote", "해독제",
                "Rainbow Candy", "RainbowCandy", "무지개사탕",
                "Parasol", "파라솔",
                "Sunscreen", "선크림",
                "First Aid Kit", "First-Aid Kit", "Medkit", "구급상자",

                "Hot Dog", "Hotdog", "핫도그",
                "Cooked Bird", "CookedBird", "요리된 새",
                "Airline Food", "Airline Meal", "기내식",
                "Honeycomb Honey", "Honey", "벌집꿀",
                "Scout Cookie", "Scout Snack", "스카우트 과자",
                "Red Mushroom Berry", "Red MushroomBerry", "빨간색 버섯 열매",

                "Cactus", "선인장",
                "Dynamite", "다이너마이트",
                "Scout Cannon", "ScoutCannon", "스카우트 캐논",

                "ScoutEffigy", "Scout Effigy", "스카우트 인형",
                "Scout Statue", "Scout Statue Item",
                "Scoutmaster Statue", "Scout Effigy Item",
                "Scoutmaster Effigy", "Effigy",
                "Revive Statue", "Resurrection Statue",
                "스카우트 석상", "스카우트석상",
                "스카우트 조각상", "부활 석상", "부활석상",

                "Pandora's Box", "Pandora Box", "판도라의 상자",
                "Sleep Berry", "SleepBerry", "수면 열매",
                "Pop Pop", "Bubble Wrap", "뾱뾱이",

                "Balloon Bunch", "Bunch of Balloons", "풍선 다발",
                "Rescue Hook", "RescueHook", "구조갈고리",
                "Chain Launcher", "ChainLauncher", "사슬발사기",
                "Magic Bean", "MagicBean", "마법의 콩",
                "Rope Cannon", "RopeCannon", "밧줄총",

                "Scoutmaster Bugle", "Scoutmaster Horn", "스카우트지도자의 나팔",
                "Cursed Skull", "CursedSkull", "저주받은 해골",
                "Fairy Lantern", "FairyLantern", "요정랜턴",

                "Book of Bones", "Bone Book", "뼈의서", "뼈의 서",
                "Anti-Rope Cannon", "Reverse Rope Cannon", "반전 밧줄총",
                "Anti-Rope Spool", "Reverse Rope Spool", "반전 밧줄타래",
                "Friendship Bugle", "Friendship Horn", "우정 나팔",

                "Golden Bing Bong", "GoldenBingBong", "황금 빙봉",
                "Cure-All", "Cure All", "Panacea", "만병통치약",
                "Flare", "Flare Gun", "조명탄"
            };

        private static ConfigEntry<int>
            maximumStackCountConfig;

        // Photon custom event codes must remain below 200.
        // 200~255는 Photon 내부 예약 범위이므로 클라이언트 수신/전송이
        // 실패하거나 내부 이벤트와 충돌할 수 있습니다.
        private const byte StackCountEventCode = 160;
        private const byte StackSnapshotEventCode = 161;
        private const byte StackConfigEventCode = 162;

        private const byte ClientMergeRequestEventCode = 163;
        private const byte ClientMergeResultEventCode = 164;
        private const byte ClientSnapshotRequestEventCode = 165;

        private const byte BackpackStackCountEventCode = 166;
        private const byte BackpackStackSnapshotEventCode = 167;

        private const float PlayerRegistrationRefreshInterval =
            1f;

        private const float GameplayStateRefreshInterval =
            0.5f;

        private const float IgnoreDuplicateRemoveSeconds =
            1.5f;

        private readonly Dictionary<SlotKey, int> stackCounts =
            new Dictionary<SlotKey, int>();

        private readonly Dictionary<BackpackSlotKey, int>
            backpackStackCounts =
                new Dictionary<BackpackSlotKey, int>();

        // Actor+slot 키는 서로 다른 바닥 배낭의 같은 슬롯 번호가 충돌할 수 있습니다.
        // 실제 배낭 대표 ItemInstanceData GUID를 보조 키로 사용해 회수 수량을 구분합니다.
        private readonly Dictionary<string, int>
            backpackStackCountsByGuid =
                new Dictionary<string, int>(
                    StringComparer.Ordinal);

        // 가장 신뢰도 높은 런타임 키입니다.
        // 서로 다른 배낭이 같은 Actor/슬롯 번호를 사용하거나 GUID 조회가
        // 실패해도 실제 BackpackData 인스턴스별 수량을 구분합니다.
        private readonly Dictionary<BackpackData, int[]>
            backpackStackCountsByActualData =
                new Dictionary<BackpackData, int[]>();

        [ThreadStatic]
        internal static bool AddItemMergePrefixInProgress;

        [ThreadStatic]
        internal static bool AddItemMergePrefixSkipsOriginal;

        private struct PendingBackpackTransfer
        {
            public SlotKey SourceKey;
            public byte BackpackSlotIndex;
            public ushort ItemId;
            public int Count;
            public string Guid;
            public bool SourceRemovalConsumed;
            public int DestinationCountAfterMerge;
        }

        internal struct BackpackWithdrawalState
        {
            public bool IsValid;
            public int ActorNumber;
            public byte BackpackSlotIndex;
            public ushort ItemId;
            public int Count;
            public string Guid;
            public BackpackData ActualBackpackData;
        }

        // 배낭 RPC 실행 전 전체 수량과 대표 ItemInstanceData 식별값을 예약합니다.
        // 실제 배낭 슬롯 이동 성공은 RPC Postfix에서 검증한 뒤 확정합니다.
        private readonly Dictionary<SlotKey, PendingBackpackTransfer>
            pendingWholeStackBackpackTransfers =
                new Dictionary<SlotKey, PendingBackpackTransfer>();

        private readonly Dictionary<SlotKey, float>
            ignoreNextRemoteRemoveUntil =
                new Dictionary<SlotKey, float>();

        private readonly Dictionary<SlotKey, int>
            pendingClientMergeCounts =
                new Dictionary<SlotKey, int>();

        private readonly List<SlotKey>
            expiredDuplicateGuardKeys =
                new List<SlotKey>();

        private int nextClientMergeRequestId;

        private float nextPlayerRegistrationRefreshAt;

        private static bool gameplaySceneCached;

        private static bool gameplayActiveCached;

        private static float nextGameplayStateRefreshAt;

        private int lastBroadcastMaximumStackCount = -1;

        private bool clientInitialSyncRequested;

        // ------------------------------------------------------------
        // 손 슬롯/드롭 진단 전용 상태
        // 실제 게임 로직은 변경하지 않고 상태와 호출 순서만 기록합니다.
        // 로그: BepInEx/InventoryHandSlotDiagnostic.log
        // ------------------------------------------------------------
        private StreamWriter handDiagnosticWriter;
        private string handDiagnosticLogPath;
        private int handDiagnosticSequence;
        private float nextHandInvariantCheckAt;
        private string lastHandInvariantSignature = string.Empty;

        // Q 드롭 중 PhotonNetwork.InstantiateItemRoom으로 생성된 월드 아이템을
        // 정확히 식별하기 위한 호스트 전용 추적 상태입니다.
        private static bool capturingQDropSpawn;

        private static readonly HashSet<int>
            singleUnitDroppedItemViewIds =
                new HashSet<int>();

        // Item.RequestPickup 실행 동안 CraftHub 수집 배율 적용을 차단합니다.
        private static int activeSingleUnitPickupViewId =
            -1;

        internal static InventoryStack Instance
        {
            get;
            private set;
        }

        internal static ManualLogSource ModLogger
        {
            get;
            private set;
        }

        internal static bool Enabled
        {
            get;
            private set;
        }

        private struct SlotKey :
            IEquatable<SlotKey>
        {
            public int ActorNumber;
            public byte SlotId;

            public SlotKey(
                int actorNumber,
                byte slotId)
            {
                ActorNumber =
                    actorNumber;

                SlotId =
                    slotId;
            }

            public bool Equals(
                SlotKey other)
            {
                return
                    ActorNumber ==
                        other.ActorNumber &&
                    SlotId ==
                        other.SlotId;
            }

            public override bool Equals(
                object obj)
            {
                return
                    obj is SlotKey &&
                    Equals(
                        (SlotKey)obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return
                        ActorNumber * 397 ^
                        SlotId;
                }
            }

            public override string ToString()
            {
                return
                    "Actor=" +
                    ActorNumber +
                    ", Slot=" +
                    SlotId;
            }
        }

        private struct BackpackSlotKey :
            IEquatable<BackpackSlotKey>
        {
            public int ActorNumber;
            public byte BackpackSlotIndex;

            public BackpackSlotKey(
                int actorNumber,
                byte backpackSlotIndex)
            {
                ActorNumber =
                    actorNumber;

                BackpackSlotIndex =
                    backpackSlotIndex;
            }

            public bool Equals(
                BackpackSlotKey other)
            {
                return
                    ActorNumber ==
                        other.ActorNumber &&
                    BackpackSlotIndex ==
                        other.BackpackSlotIndex;
            }

            public override bool Equals(
                object obj)
            {
                return
                    obj is BackpackSlotKey &&
                    Equals(
                        (BackpackSlotKey)obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return
                        ActorNumber * 397 ^
                        BackpackSlotIndex;
                }
            }

            public override string ToString()
            {
                return
                    "Actor=" +
                    ActorNumber +
                    ", BackpackSlot=" +
                    BackpackSlotIndex;
            }
        }

        private void Awake()
        {
            Instance =
                this;

            ModLogger =
                Logger;

            BindInventoryConfig();

            RegisterKnownCraftStackableIds();

            InitializeHandDiagnosticLog();

            Enabled =
                true;

            SceneManager.sceneLoaded +=
                HandleSceneLoaded;

            Logger.LogInfo(
                PluginName +
                " " +
                PluginVersion +
                " loaded. Maximum stack=" +
                MaximumStackCount +
                ". PEAKLib.ModConfig can display this setting when installed." +
                " CustomEventCodes=" +
                StackCountEventCode +
                "-" +
                BackpackStackSnapshotEventCode +
                " (Photon-safe range). HandDiagnosticLog=" +
                handDiagnosticLogPath);
        }

        private void InitializeHandDiagnosticLog()
        {
            handDiagnosticLogPath =
                Path.Combine(
                    Paths.BepInExRootPath,
                    "InventoryHandSlotDiagnostic.log");

            try
            {
                handDiagnosticWriter =
                    new StreamWriter(
                        handDiagnosticLogPath,
                        true,
                        new UTF8Encoding(false));

                handDiagnosticWriter.AutoFlush =
                    true;

                handDiagnosticWriter.WriteLine(
                    "================================================================================================================");

                WriteHandDiagnostic(
                    "START",
                    "InventoryVersion=" +
                    PluginVersion +
                    " | Unity=" +
                    Application.unityVersion +
                    " | LogPath=" +
                    handDiagnosticLogPath);
            }
            catch (Exception exception)
            {
                Logger.LogError(
                    "Inventory hand diagnostic log initialization failed: " +
                    exception);
            }
        }

        internal void WriteHandDiagnostic(
            string tag,
            string message)
        {
            string line =
                DateTime.Now.ToString(
                    "yyyy-MM-dd HH:mm:ss.fff") +
                " | #" +
                (++handDiagnosticSequence).ToString(
                    "000000") +
                " | " +
                tag +
                " | " +
                (
                    message ??
                    string.Empty
                );

            try
            {
                if (handDiagnosticWriter != null)
                {
                    handDiagnosticWriter.WriteLine(
                        line);
                }
            }
            catch (Exception)
            {
            }

            if (ModLogger != null)
            {
                ModLogger.LogInfo(
                    "[HandDiag] " +
                    tag +
                    " | " +
                    message);
            }
        }

        private void CheckLocalHandInvariantIfNeeded()
        {
            if (Time.unscaledTime <
                nextHandInvariantCheckAt)
            {
                return;
            }

            nextHandInvariantCheckAt =
                Time.unscaledTime +
                0.25f;

            global::Player player =
                global::Player.localPlayer;

            Character character =
                Character.localCharacter;

            string signature =
                BuildHandStateSummary(
                    player,
                    character);

            if (string.Equals(
                    signature,
                    lastHandInvariantSignature,
                    StringComparison.Ordinal))
            {
                return;
            }

            lastHandInvariantSignature =
                signature;

            WriteHandDiagnostic(
                "STATE-CHANGED",
                signature);

            if (player == null ||
                player.tempFullSlot == null ||
                player.tempFullSlot.IsEmpty() ||
                player.tempFullSlot.prefab == null)
            {
                return;
            }

            bool tempSelected =
                character != null &&
                character.refs != null &&
                character.refs.items != null &&
                character.refs.items.currentSelectedSlot.IsSome &&
                character.refs.items.currentSelectedSlot.Value ==
                    player.tempFullSlot.itemSlotID;

            if (!tempSelected)
            {
                WriteHandDiagnostic(
                    "GHOST-HAND-DETECTED",
                    "tempFullSlot contains an item but currentSelectedSlot does not point to tempFullSlot. " +
                    signature);
            }
        }

        internal static string BuildHandStateSummary(
            global::Player player,
            Character character)
        {
            StringBuilder builder =
                new StringBuilder(1024);

            builder.Append(
                "Network[InRoom=");

            builder.Append(
                PhotonNetwork.InRoom);

            builder.Append(
                ",IsMaster=");

            builder.Append(
                PhotonNetwork.IsMasterClient);

            builder.Append(
                ",Actor=");

            builder.Append(
                PhotonNetwork.LocalPlayer != null
                    ? PhotonNetwork.LocalPlayer.ActorNumber
                    : -1);

            builder.Append(
                "] | ");

            if (player == null)
            {
                builder.Append(
                    "Player=<null>");

                return
                    builder.ToString();
            }

            builder.Append(
                "PlayerView=");

            builder.Append(
                player.photonView != null
                    ? player.photonView.ViewID
                    : -1);

            builder.Append(
                " | Slots=[");

            if (player.itemSlots != null)
            {
                for (int i = 0;
                     i < player.itemSlots.Length;
                     i++)
                {
                    if (i > 0)
                    {
                        builder.Append(
                            " | ");
                    }

                    builder.Append(
                        i);

                    builder.Append(
                        ":");

                    builder.Append(
                        BuildItemSlotDiagnostic(
                            player.itemSlots[i]));
                }
            }

            builder.Append(
                "] | Temp=");

            builder.Append(
                BuildItemSlotDiagnostic(
                    player.tempFullSlot));

            builder.Append(
                " | Selected=");

            if (character == null ||
                character.refs == null ||
                character.refs.items == null)
            {
                builder.Append(
                    "<items-null>");
            }
            else if (character.refs.items
                .currentSelectedSlot.IsNone)
            {
                builder.Append(
                    "None");
            }
            else
            {
                byte selectedSlot =
                    character.refs.items
                        .currentSelectedSlot.Value;

                builder.Append(
                    selectedSlot);

                builder.Append(
                    "(");

                builder.Append(
                    BuildItemSlotDiagnostic(
                        player.GetItemSlot(
                            selectedSlot)));

                builder.Append(
                    ")");
            }

            return
                builder.ToString();
        }

        internal static string BuildItemSlotDiagnostic(
            ItemSlot slot)
        {
            if (slot == null)
            {
                return "<null>";
            }

            if (slot.IsEmpty() ||
                slot.prefab == null)
            {
                return
                    "empty(slotId=" +
                    slot.itemSlotID +
                    ")";
            }

            string guid =
                slot.data != null
                    ? slot.data.guid.ToString()
                    : "<null>";

            int count =
                1;

            try
            {
                count =
                    Mathf.Max(
                        1,
                        GetStackCount(
                            slot));
            }
            catch (Exception)
            {
            }

            return
                "slotId=" +
                slot.itemSlotID +
                ",itemId=" +
                slot.prefab.itemID +
                ",name=" +
                SafeDiagnosticItemName(
                    slot.prefab) +
                ",guid=" +
                guid +
                ",stack=" +
                count;
        }

        private static string SafeDiagnosticItemName(
            Item item)
        {
            if (item == null)
            {
                return "<null>";
            }

            try
            {
                string itemName =
                    item.GetName();

                if (!string.IsNullOrEmpty(
                        itemName))
                {
                    return itemName;
                }
            }
            catch (Exception)
            {
            }

            return
                item.gameObject != null
                    ? item.gameObject.name
                    : "<unnamed>";
        }

        internal static string BuildDiagnosticArguments(
            object[] arguments)
        {
            if (arguments == null)
            {
                return "<null>";
            }

            StringBuilder builder =
                new StringBuilder();

            builder.Append(
                "[");

            for (int i = 0;
                 i < arguments.Length;
                 i++)
            {
                if (i > 0)
                {
                    builder.Append(
                        ", ");
                }

                object value =
                    arguments[i];

                builder.Append(
                    i);

                builder.Append(
                    ":");

                if (value == null)
                {
                    builder.Append(
                        "<null>");

                    continue;
                }

                builder.Append(
                    value.GetType().Name);

                builder.Append(
                    "=");

                if (value is ItemSlot)
                {
                    builder.Append(
                        BuildItemSlotDiagnostic(
                            (ItemSlot)value));
                }
                else
                {
                    builder.Append(
                        value);
                }
            }

            builder.Append(
                "]");

            return
                builder.ToString();
        }

        private void BindInventoryConfig()
        {
            maximumStackCountConfig =
                Config.Bind(
                    "01. 인벤토리 적재 설정",
                    "슬롯당 최대 적재 수량",
                    DefaultMaximumStackCount,
                    new ConfigDescription(
                        "판매용 자원과 횃불을 한 슬롯에 쌓을 수 있는 최대 수량입니다. " +
                        "멀티플레이에서는 호스트 설정이 실제 게임 규칙으로 적용됩니다. " +
                        "게임 중 값을 낮춰도 기존 초과 스택은 삭제되지 않으며 추가 합치기만 제한됩니다.",
                        new AcceptableValueRange<int>(
                            MinimumConfigStackCount,
                            MaximumConfigStackCount)));

            maximumStackCountConfig.SettingChanged +=
                HandleMaximumStackConfigChanged;

            ApplyLocalConfiguredMaximum(
                "Initial config");
        }

        private static void HandleMaximumStackConfigChanged(
            object sender,
            EventArgs eventArgs)
        {
            if (Instance == null)
            {
                return;
            }

            if (PhotonNetwork.InRoom &&
                !PhotonNetwork.IsMasterClient)
            {
                if (ModLogger != null)
                {
                    ModLogger.LogInfo(
                        "Local maximum-stack config was changed, but the current room uses the host value " +
                        MaximumStackCount +
                        ". The local value will apply when hosting or playing outside a room.");
                }

                return;
            }

            Instance.ApplyLocalConfiguredMaximum(
                "Config changed");
        }

        private void ApplyLocalConfiguredMaximum(
            string reason)
        {
            int configuredValue =
                maximumStackCountConfig != null
                    ? maximumStackCountConfig.Value
                    : DefaultMaximumStackCount;

            ApplyEffectiveMaximumStackCount(
                configuredValue,
                reason);

            if (PhotonNetwork.InRoom &&
                PhotonNetwork.IsMasterClient)
            {
                BroadcastMaximumStackCount(
                    null);
            }
        }

        private void ApplyEffectiveMaximumStackCount(
            int value,
            string reason)
        {
            int safeValue =
                Mathf.Clamp(
                    value,
                    MinimumConfigStackCount,
                    MaximumConfigStackCount);

            bool changed =
                MaximumStackCount !=
                safeValue;

            MaximumStackCount =
                safeValue;

            int overLimitStackCount =
                CountStacksAboveCurrentMaximum();

            RefreshAllInventoryPresentations();

            if (ModLogger != null)
            {
                ModLogger.LogInfo(
                    "Maximum stack count applied. Value=" +
                    MaximumStackCount +
                    " | Reason=" +
                    reason +
                    " | Existing stacks above new maximum=" +
                    overLimitStackCount +
                    ". Existing quantities are preserved.");
            }

            if (changed &&
                PhotonNetwork.InRoom &&
                PhotonNetwork.IsMasterClient)
            {
                lastBroadcastMaximumStackCount =
                    -1;
            }
        }

        private int CountStacksAboveCurrentMaximum()
        {
            int count = 0;

            foreach (
                KeyValuePair<SlotKey, int> pair
                in stackCounts)
            {
                if (pair.Value >
                    MaximumStackCount)
                {
                    count++;
                }
            }

            return count;
        }

        private static void RefreshAllInventoryPresentations()
        {
            foreach (global::Player player in
                     PlayerHandler.GetAllPlayers())
            {
                NotifyInventoryChanged(
                    player);
            }
        }

        private void RequestInitialClientSyncIfNeeded()
        {
            if (clientInitialSyncRequested ||
                !PhotonNetwork.InRoom ||
                PhotonNetwork.IsMasterClient ||
                PhotonNetwork.MasterClient == null ||
                PhotonNetwork.LocalPlayer == null)
            {
                return;
            }

            RaiseEventOptions options =
                new RaiseEventOptions
                {
                    TargetActors =
                        new[]
                        {
                            PhotonNetwork
                                .MasterClient
                                .ActorNumber
                        }
                };

            bool sent =
                PhotonNetwork.RaiseEvent(
                    ClientSnapshotRequestEventCode,
                    new object[]
                    {
                        PhotonNetwork
                            .LocalPlayer
                            .ActorNumber
                    },
                    options,
                    SendOptions.SendReliable);

            if (!sent)
            {
                return;
            }

            clientInitialSyncRequested =
                true;

            if (ModLogger != null)
            {
                ModLogger.LogInfo(
                    "Initial stack snapshot requested from host. Actor=" +
                    PhotonNetwork.LocalPlayer.ActorNumber);
            }
        }

        private void BroadcastMaximumStackCount(
            int? targetActorNumber)
        {
            if (!PhotonNetwork.InRoom ||
                !PhotonNetwork.IsMasterClient)
            {
                return;
            }

            object[] payload =
            {
                MaximumStackCount
            };

            RaiseEventOptions options =
                new RaiseEventOptions();

            if (targetActorNumber.HasValue)
            {
                options.TargetActors =
                    new[]
                    {
                        targetActorNumber.Value
                    };
            }
            else
            {
                options.Receivers =
                    ReceiverGroup.All;
            }

            PhotonNetwork.RaiseEvent(
                StackConfigEventCode,
                payload,
                options,
                SendOptions.SendReliable);

            lastBroadcastMaximumStackCount =
                MaximumStackCount;

            if (ModLogger != null)
            {
                ModLogger.LogInfo(
                    "Maximum stack count synchronized. Value=" +
                    MaximumStackCount +
                    (
                        targetActorNumber.HasValue
                            ? " | TargetActor=" +
                              targetActorNumber.Value
                            : " | Target=All"
                    ));
            }
        }

        private void ApplyMaximumStackCountEvent(
            EventData photonEvent)
        {
            if (photonEvent == null ||
                photonEvent.CustomData == null)
            {
                return;
            }

            if (PhotonNetwork.MasterClient != null &&
                photonEvent.Sender !=
                    PhotonNetwork.MasterClient.ActorNumber)
            {
                if (ModLogger != null)
                {
                    ModLogger.LogWarning(
                        "Ignored maximum-stack event from non-master actor " +
                        photonEvent.Sender +
                        ".");
                }

                return;
            }

            object[] payload =
                photonEvent.CustomData as
                    object[];

            if (payload == null ||
                payload.Length < 1)
            {
                return;
            }

            int hostMaximum;

            try
            {
                hostMaximum =
                    Convert.ToInt32(
                        payload[0]);
            }
            catch (Exception)
            {
                return;
            }

            ApplyEffectiveMaximumStackCount(
                hostMaximum,
                "Host synchronization");
        }

        private void OnEnable()
        {
            PhotonNetwork.AddCallbackTarget(
                this);
        }

        private void OnDisable()
        {
            PhotonNetwork.RemoveCallbackTarget(
                this);
        }

        private void OnDestroy()
        {
            Enabled =
                false;

            gameplaySceneCached =
                false;

            gameplayActiveCached =
                false;

            nextGameplayStateRefreshAt =
                0f;

            SceneManager.sceneLoaded -=
                HandleSceneLoaded;

            if (maximumStackCountConfig != null)
            {
                maximumStackCountConfig.SettingChanged -=
                    HandleMaximumStackConfigChanged;

                maximumStackCountConfig =
                    null;
            }

            stackCounts.Clear();
            backpackStackCounts.Clear();
            backpackStackCountsByGuid.Clear();
            backpackStackCountsByActualData.Clear();
            pendingWholeStackBackpackTransfers.Clear();
            ignoreNextRemoteRemoveUntil.Clear();
            pendingClientMergeCounts.Clear();

            singleUnitDroppedItemViewIds.Clear();

            capturingQDropSpawn =
                false;

            activeSingleUnitPickupViewId =
                -1;

            if (Instance == this)
            {
                Instance = null;
            }

            WriteHandDiagnostic(
                "STOP",
                "Inventory diagnostic shutting down.");

            if (handDiagnosticWriter != null)
            {
                handDiagnosticWriter.Flush();
                handDiagnosticWriter.Dispose();
                handDiagnosticWriter =
                    null;
            }

            ModLogger = null;
        }

        private void Update()
        {
            if (!Enabled)
            {
                return;
            }

            RefreshGameplayStateIfNeeded();

            EnsureCraftStackableItemsRegistered();

            CheckLocalHandInvariantIfNeeded();

            if (!PhotonNetwork.InRoom)
            {
                int localConfiguredMaximum =
                    maximumStackCountConfig != null
                        ? maximumStackCountConfig.Value
                        : DefaultMaximumStackCount;

                if (MaximumStackCount !=
                    localConfiguredMaximum)
                {
                    ApplyEffectiveMaximumStackCount(
                        localConfiguredMaximum,
                        "Outside room local config");
                }

                return;
            }

            if (!gameplayActiveCached)
            {
                return;
            }

            if (!PhotonNetwork.IsMasterClient)
            {
                RequestInitialClientSyncIfNeeded();
                return;
            }

            if (lastBroadcastMaximumStackCount !=
                MaximumStackCount)
            {
                BroadcastMaximumStackCount(
                    null);
            }

            if (Time.unscaledTime <
                nextPlayerRegistrationRefreshAt)
            {
                return;
            }

            nextPlayerRegistrationRefreshAt =
                Time.unscaledTime +
                PlayerRegistrationRefreshInterval;

            EnsureAllPlayerStackEntries();
            RemoveExpiredDuplicateGuards();
        }

        private void HandleSceneLoaded(
            Scene scene,
            LoadSceneMode loadSceneMode)
        {
            stackCounts.Clear();
            backpackStackCounts.Clear();
            backpackStackCountsByGuid.Clear();
            backpackStackCountsByActualData.Clear();
            pendingWholeStackBackpackTransfers.Clear();
            ignoreNextRemoteRemoveUntil.Clear();
            pendingClientMergeCounts.Clear();

            singleUnitDroppedItemViewIds.Clear();

            capturingQDropSpawn =
                false;

            activeSingleUnitPickupViewId =
                -1;

            nextClientMergeRequestId =
                0;

            nextPlayerRegistrationRefreshAt =
                0f;

            nextGameplayStateRefreshAt =
                0f;

            gameplaySceneCached =
                !IsExcludedScene(
                    scene);

            gameplayActiveCached =
                false;

            lastBroadcastMaximumStackCount =
                -1;

            clientInitialSyncRequested =
                false;

            craftStackableItemsRegistered =
                false;

            nextCraftStackableRegistrationAt =
                0f;

            RegisterKnownCraftStackableIds();

            if (!PhotonNetwork.InRoom ||
                PhotonNetwork.IsMasterClient)
            {
                ApplyLocalConfiguredMaximum(
                    "Scene loaded");
            }

            WriteHandDiagnostic(
                "SCENE",
                "Name=" +
                scene.name +
                " | Handle=" +
                scene.handle +
                " | Excluded=" +
                IsExcludedScene(
                    scene));

            if (IsExcludedScene(
                    scene))
            {
                Logger.LogInfo(
                    "Inventory stacking disabled in scene: " +
                    scene.name);

                return;
            }

            RefreshGameplayState(
                true);

            Logger.LogInfo(
                "Inventory stacking enabled in scene: " +
                scene.name);
        }

        private static void RefreshGameplayStateIfNeeded()
        {
            if (!Enabled ||
                !gameplaySceneCached ||
                gameplayActiveCached ||
                Time.unscaledTime <
                    nextGameplayStateRefreshAt)
            {
                return;
            }

            RefreshGameplayState(
                false);
        }

        private static void RefreshGameplayState(
            bool force)
        {
            if (!Enabled ||
                !gameplaySceneCached)
            {
                gameplayActiveCached =
                    false;

                return;
            }

            if (!force &&
                Time.unscaledTime <
                    nextGameplayStateRefreshAt)
            {
                return;
            }

            nextGameplayStateRefreshAt =
                Time.unscaledTime +
                GameplayStateRefreshInterval;

            gameplayActiveCached =
                UnityEngine.Object
                    .FindAnyObjectByType<
                        MapHandler>() !=
                null;
        }

        internal static bool IsGameplayActive()
        {
            if (!Enabled ||
                !gameplaySceneCached)
            {
                return false;
            }

            RefreshGameplayStateIfNeeded();

            return
                gameplayActiveCached;
        }

        private static bool IsExcludedScene(
            Scene scene)
        {
            if (!scene.IsValid() ||
                !scene.isLoaded)
            {
                return true;
            }

            return
                string.Equals(
                    scene.name,
                    "Airport",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    scene.name,
                    "Title",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    scene.name,
                    "Pretitle",
                    StringComparison.OrdinalIgnoreCase);
        }

        private static void RegisterKnownCraftStackableIds()
        {
            // CraftHub에서 로그로 검증된 고정 ItemID입니다.
            craftStackableItemIds.Add(
                32);  // Flare

            craftStackableItemIds.Add(
                56);  // Kingberry Green

            craftStackableItemIds.Add(
                67);  // ScoutEffigy

            craftStackableItemIds.Add(
                109); // Torch

            craftStackableItemIds.Add(
                152); // FortifiedMilk
        }

        private static void EnsureCraftStackableItemsRegistered()
        {
            if (craftStackableItemsRegistered ||
                Time.unscaledTime <
                    nextCraftStackableRegistrationAt)
            {
                return;
            }

            nextCraftStackableRegistrationAt =
                Time.unscaledTime +
                1f;

            ItemDatabase database =
                SingletonAsset<ItemDatabase>.Instance;

            if (database == null ||
                database.itemLookup == null ||
                database.itemLookup.Count == 0)
            {
                return;
            }

            HashSet<string> normalizedAliases =
                new HashSet<string>(
                    StringComparer.Ordinal);

            for (int i = 0;
                 i < CraftStackableAliases.Length;
                 i++)
            {
                string normalized =
                    NormalizeCraftStackableName(
                        CraftStackableAliases[i]);

                if (!string.IsNullOrEmpty(
                        normalized))
                {
                    normalizedAliases.Add(
                        normalized);
                }
            }

            int addedCount =
                0;

            foreach (
                KeyValuePair<ushort, Item> pair
                in database.itemLookup)
            {
                Item item =
                    pair.Value;

                if (item == null)
                {
                    continue;
                }

                string displayName =
                    string.Empty;

                try
                {
                    displayName =
                        item.GetName();
                }
                catch (Exception)
                {
                }

                string objectName =
                    item.gameObject != null
                        ? item.gameObject.name
                        : string.Empty;

                string normalizedDisplay =
                    NormalizeCraftStackableName(
                        displayName);

                string normalizedObject =
                    NormalizeCraftStackableName(
                        objectName);

                if (!normalizedAliases.Contains(
                        normalizedDisplay) &&
                    !normalizedAliases.Contains(
                        normalizedObject))
                {
                    continue;
                }

                if (craftStackableItemIds.Add(
                        pair.Key))
                {
                    addedCount++;
                }
            }

            craftStackableItemsRegistered =
                true;

            if (ModLogger != null)
            {
                ModLogger.LogInfo(
                    "CraftHub output stack registration completed. " +
                    "RegisteredItemIDs=" +
                    craftStackableItemIds.Count +
                    " | NewlyResolved=" +
                    addedCount +
                    ". Crafted climbing, food, healing, utility and revive items now share the normal stack system.");
            }
        }

        private static string NormalizeCraftStackableName(
            string value)
        {
            if (string.IsNullOrEmpty(
                    value))
            {
                return string.Empty;
            }

            char[] buffer =
                new char[value.Length];

            int length =
                0;

            for (int i = 0;
                 i < value.Length;
                 i++)
            {
                char character =
                    value[i];

                if (char.IsLetterOrDigit(
                        character))
                {
                    buffer[length++] =
                        char.ToLowerInvariant(
                            character);
                }
            }

            return
                length > 0
                    ? new string(
                        buffer,
                        0,
                        length)
                    : string.Empty;
        }

        public static bool IsStackableItemId(
            ushort itemId)
        {
            EnsureCraftStackableItemsRegistered();

            return
                Spawn.IsSaleResourceId(
                    itemId) ||
                itemId ==
                    TorchItemId ||
                craftStackableItemIds.Contains(
                    itemId);
        }

        public static int GetStackCount(
            global::Player player,
            byte slotId)
        {
            if (Instance == null ||
                player == null)
            {
                return 1;
            }

            return Instance.GetCountInternal(
                player,
                slotId);
        }

        private int GetKnownActualBackpackSlotCount(
            ItemSlot slot)
        {
            if (slot == null ||
                slot.IsEmpty())
            {
                return 0;
            }

            foreach (
                KeyValuePair<BackpackData, int[]> pair
                in backpackStackCountsByActualData)
            {
                BackpackData data =
                    pair.Key;

                int[] counts =
                    pair.Value;

                if (data == null ||
                    data.itemSlots == null ||
                    counts == null)
                {
                    continue;
                }

                for (int i = 0;
                     i < data.itemSlots.Length &&
                     i < counts.Length;
                     i++)
                {
                    if (ReferenceEquals(
                            data.itemSlots[i],
                            slot) &&
                        counts[i] > 0)
                    {
                        return
                            Mathf.Clamp(
                                counts[i],
                                1,
                                MaximumConfigStackCount);
                    }
                }
            }

            return 0;
        }

        public static int GetStackCount(
            ItemSlot slot)
        {
            if (Instance == null ||
                slot == null ||
                slot.IsEmpty())
            {
                return 0;
            }

            int actualBackpackCount =
                Instance.GetKnownActualBackpackSlotCount(
                    slot);

            if (actualBackpackCount > 0)
            {
                return actualBackpackCount;
            }

            global::Player player;
            byte slotId;

            if (Instance.TryResolvePlayerSlot(
                    slot,
                    out player,
                    out slotId))
            {
                return Instance.GetCountInternal(
                    player,
                    slotId);
            }

            byte backpackSlotIndex;

            if (Instance.TryResolveBackpackSlot(
                    slot,
                    out player,
                    out backpackSlotIndex))
            {
                return GetBackpackStackCount(
                    player,
                    backpackSlotIndex);
            }

            return 1;
        }

        public static int GetBackpackStackCount(
            global::Player player,
            byte backpackSlotIndex)
        {
            if (Instance == null ||
                player == null ||
                player.photonView == null ||
                player.photonView.Owner == null)
            {
                return 1;
            }

            BackpackData backpackData;

            if (!TryGetBackpackData(
                    player,
                    out backpackData) ||
                backpackData.itemSlots == null ||
                backpackSlotIndex >=
                    backpackData.itemSlots.Length)
            {
                return 0;
            }

            ItemSlot slot =
                backpackData.itemSlots[
                    backpackSlotIndex];

            if (slot == null ||
                slot.IsEmpty() ||
                slot.prefab == null)
            {
                return 0;
            }

            BackpackSlotKey key =
                new BackpackSlotKey(
                    player.photonView.Owner.ActorNumber,
                    backpackSlotIndex);

            int count;

            if (!Instance.backpackStackCounts.TryGetValue(
                    key,
                    out count))
            {
                return 1;
            }

            return
                Mathf.Clamp(
                    count,
                    1,
                    MaximumConfigStackCount);
        }

        private static bool TryGetBackpackData(
            global::Player player,
            out BackpackData backpackData)
        {
            backpackData =
                default(BackpackData);

            return
                player != null &&
                player.backpackSlot != null &&
                !player.backpackSlot.IsEmpty() &&
                player.backpackSlot.data != null &&
                player.backpackSlot.data
                    .TryGetDataEntry<BackpackData>(
                        DataEntryKey.BackpackData,
                        out backpackData) &&
                backpackData != null;
        }

        public static bool HasStackSpace(
            global::Player player,
            ushort itemId)
        {
            if (Instance == null ||
                player == null ||
                !IsStackableItemId(
                    itemId))
            {
                return false;
            }

            ItemSlot slot;

            if (!PhotonNetwork.IsMasterClient &&
                player.photonView != null &&
                player.photonView.IsMine)
            {
                return Instance
                    .TryFindClientStackWithPendingSpace(
                        player,
                        itemId,
                        out slot);
            }

            return Instance.TryFindStackWithSpace(
                player,
                itemId,
                out slot);
        }

        private int GetCountInternal(
            global::Player player,
            byte slotId)
        {
            if (player == null ||
                player.photonView == null ||
                player.photonView.Owner == null)
            {
                return 1;
            }

            ItemSlot slot =
                player.GetItemSlot(
                    slotId);

            if (slot == null ||
                slot.IsEmpty() ||
                slot.prefab == null ||
                !IsStackableItemId(
                    slot.prefab.itemID))
            {
                return 0;
            }

            SlotKey key =
                new SlotKey(
                    player.photonView
                        .Owner
                        .ActorNumber,
                    slotId);

            int count;

            if (!stackCounts.TryGetValue(
                    key,
                    out count))
            {
                return 1;
            }

            // 현재 설정값보다 큰 기존 스택도 수량을 잃지 않도록
            // 실제 저장 수량은 설정 가능한 절대 상한까지만 제한합니다.
            return Mathf.Clamp(
                count,
                1,
                MaximumConfigStackCount);
        }

        private bool TryFindStackWithSpace(
            global::Player player,
            ushort itemId,
            out ItemSlot stackSlot)
        {
            stackSlot =
                null;

            if (player == null ||
                !IsStackableItemId(
                    itemId))
            {
                return false;
            }

            for (int i = 0;
                 i < player.itemSlots.Length;
                 i++)
            {
                ItemSlot slot =
                    player.itemSlots[i];

                if (!IsMatchingStack(
                        slot,
                        itemId))
                {
                    continue;
                }

                int count =
                    GetCountInternal(
                        player,
                        slot.itemSlotID);

                if (count >=
                    MaximumStackCount)
                {
                    continue;
                }

                stackSlot =
                    slot;

                return true;
            }

            ItemSlot tempSlot =
                player.tempFullSlot;

            if (IsMatchingStack(
                    tempSlot,
                    itemId))
            {
                int tempCount =
                    GetCountInternal(
                        player,
                        tempSlot.itemSlotID);

                if (tempCount <
                    MaximumStackCount)
                {
                    stackSlot =
                        tempSlot;

                    return true;
                }
            }

            return false;
        }

        private static bool IsMatchingStack(
            ItemSlot slot,
            ushort itemId)
        {
            return
                slot != null &&
                !slot.IsEmpty() &&
                slot.prefab != null &&
                slot.prefab.itemID ==
                    itemId;
        }

        internal bool ClientTryRequestStackMerge(
            global::Player player,
            ushort itemId,
            out ItemSlot resultSlot)
        {
            resultSlot =
                null;

            if (!Enabled ||
                PhotonNetwork.IsMasterClient ||
                !PhotonNetwork.InRoom ||
                PhotonNetwork.MasterClient == null ||
                !IsGameplayActive() ||
                player == null ||
                player.photonView == null ||
                !player.photonView.IsMine ||
                player.photonView.Owner == null ||
                !IsStackableItemId(
                    itemId))
            {
                return false;
            }

            ItemSlot stackSlot;

            if (!TryFindClientStackWithPendingSpace(
                    player,
                    itemId,
                    out stackSlot))
            {
                return false;
            }

            int requestId =
                ++nextClientMergeRequestId;

            SlotKey key =
                new SlotKey(
                    player.photonView.Owner.ActorNumber,
                    stackSlot.itemSlotID);

            int pendingCount;

            pendingClientMergeCounts.TryGetValue(
                key,
                out pendingCount);

            pendingClientMergeCounts[key] =
                pendingCount +
                1;

            RaiseEventOptions options =
                new RaiseEventOptions
                {
                    TargetActors =
                        new[]
                        {
                            PhotonNetwork
                                .MasterClient
                                .ActorNumber
                        }
                };

            object[] payload =
            {
                requestId,
                (int)itemId,
                (int)stackSlot.itemSlotID
            };

            bool sent =
                PhotonNetwork.RaiseEvent(
                    ClientMergeRequestEventCode,
                    payload,
                    options,
                    SendOptions.SendReliable);

            if (!sent)
            {
                RemoveOnePendingClientMerge(
                    key);

                if (ModLogger != null)
                {
                    ModLogger.LogWarning(
                        "Client stack merge request failed to send. " +
                        key +
                        " | ItemID=" +
                        itemId);
                }

                return false;
            }

            resultSlot =
                stackSlot;

            if (ModLogger != null)
            {
                ModLogger.LogDebug(
                    "Client stack merge requested. " +
                    key +
                    " | RequestID=" +
                    requestId +
                    " | ItemID=" +
                    itemId +
                    " | Pending=" +
                    pendingClientMergeCounts[key]);
            }

            return true;
        }

        private bool TryFindClientStackWithPendingSpace(
            global::Player player,
            ushort itemId,
            out ItemSlot stackSlot)
        {
            stackSlot =
                null;

            if (player == null ||
                player.photonView == null ||
                player.photonView.Owner == null ||
                !IsStackableItemId(
                    itemId))
            {
                return false;
            }

            int actorNumber =
                player.photonView.Owner.ActorNumber;

            for (int i = 0;
                 i < player.itemSlots.Length;
                 i++)
            {
                ItemSlot slot =
                    player.itemSlots[i];

                if (!IsMatchingStack(
                        slot,
                        itemId))
                {
                    continue;
                }

                SlotKey key =
                    new SlotKey(
                        actorNumber,
                        slot.itemSlotID);

                int pendingCount;

                pendingClientMergeCounts.TryGetValue(
                    key,
                    out pendingCount);

                int effectiveCount =
                    GetCountInternal(
                        player,
                        slot.itemSlotID) +
                    pendingCount;

                if (effectiveCount >=
                    MaximumStackCount)
                {
                    continue;
                }

                stackSlot =
                    slot;

                return true;
            }

            ItemSlot tempSlot =
                player.tempFullSlot;

            if (IsMatchingStack(
                    tempSlot,
                    itemId))
            {
                SlotKey tempKey =
                    new SlotKey(
                        actorNumber,
                        tempSlot.itemSlotID);

                int pendingCount;

                pendingClientMergeCounts.TryGetValue(
                    tempKey,
                    out pendingCount);

                int effectiveCount =
                    GetCountInternal(
                        player,
                        tempSlot.itemSlotID) +
                    pendingCount;

                if (effectiveCount <
                    MaximumStackCount)
                {
                    stackSlot =
                        tempSlot;

                    return true;
                }
            }

            return false;
        }

        private void ProcessClientMergeRequestOnHost(
            int senderActorNumber,
            object[] payload)
        {
            if (!PhotonNetwork.IsMasterClient ||
                payload == null ||
                payload.Length < 3)
            {
                return;
            }

            int requestId;
            int itemValue;
            int slotValue;

            try
            {
                requestId =
                    Convert.ToInt32(
                        payload[0]);

                itemValue =
                    Convert.ToInt32(
                        payload[1]);

                slotValue =
                    Convert.ToInt32(
                        payload[2]);
            }
            catch (Exception)
            {
                SendClientMergeResult(
                    senderActorNumber,
                    0,
                    false,
                    0,
                    0,
                    "요청 데이터 변환 실패");

                return;
            }

            if (itemValue < 0 ||
                itemValue > ushort.MaxValue ||
                slotValue < 0 ||
                slotValue > byte.MaxValue)
            {
                SendClientMergeResult(
                    senderActorNumber,
                    requestId,
                    false,
                    itemValue,
                    slotValue,
                    "요청 범위 오류");

                return;
            }

            ushort itemId =
                (ushort)itemValue;

            byte requestedSlotId =
                (byte)slotValue;

            global::Player player =
                PlayerHandler.GetPlayer(
                    senderActorNumber);

            if (player == null ||
                player.photonView == null ||
                player.photonView.Owner == null ||
                player.photonView.Owner.ActorNumber !=
                    senderActorNumber)
            {
                SendClientMergeResult(
                    senderActorNumber,
                    requestId,
                    false,
                    itemId,
                    requestedSlotId,
                    "호스트에서 요청 플레이어를 찾지 못함");

                return;
            }

            ItemSlot requestedSlot =
                player.GetItemSlot(
                    requestedSlotId);

            if (requestedSlot == null ||
                requestedSlot.IsEmpty() ||
                requestedSlot.prefab == null ||
                requestedSlot.prefab.itemID !=
                    itemId ||
                !IsStackableItemId(
                    itemId))
            {
                SendClientMergeResult(
                    senderActorNumber,
                    requestId,
                    false,
                    itemId,
                    requestedSlotId,
                    "호스트 슬롯 상태 불일치");

                return;
            }

            int oldCount =
                GetCountInternal(
                    player,
                    requestedSlotId);

            if (oldCount >=
                MaximumStackCount)
            {
                SendClientMergeResult(
                    senderActorNumber,
                    requestId,
                    false,
                    itemId,
                    requestedSlotId,
                    "최대 중첩 수량 도달");

                return;
            }

            int newCount =
                Mathf.Clamp(
                    oldCount + 1,
                    1,
                    MaximumStackCount);

            SetCountOnHost(
                player,
                requestedSlotId,
                newCount,
                "ClientPickupMerge");

            NotifyInventoryChanged(
                player);

            SendClientMergeResult(
                senderActorNumber,
                requestId,
                true,
                itemId,
                requestedSlotId,
                string.Empty);

            if (ModLogger != null)
            {
                ModLogger.LogInfo(
                    "Client pickup merged on host. " +
                    GetPlayerLogName(
                        player) +
                    " | Slot=" +
                    requestedSlotId +
                    " | ItemID=" +
                    itemId +
                    " | Count=" +
                    oldCount +
                    "->" +
                    newCount +
                    " | RequestID=" +
                    requestId);
            }
        }

        private static void SendClientMergeResult(
            int targetActorNumber,
            int requestId,
            bool success,
            int itemId,
            int slotId,
            string reason)
        {
            if (!PhotonNetwork.IsMasterClient ||
                targetActorNumber <= 0)
            {
                return;
            }

            RaiseEventOptions options =
                new RaiseEventOptions
                {
                    TargetActors =
                        new[]
                        {
                            targetActorNumber
                        }
                };

            object[] payload =
            {
                requestId,
                success,
                itemId,
                slotId,
                reason ?? string.Empty
            };

            PhotonNetwork.RaiseEvent(
                ClientMergeResultEventCode,
                payload,
                options,
                SendOptions.SendReliable);
        }

        private void ApplyClientMergeResult(
            EventData photonEvent)
        {
            if (photonEvent == null ||
                photonEvent.CustomData == null)
            {
                return;
            }

            if (PhotonNetwork.MasterClient != null &&
                photonEvent.Sender !=
                    PhotonNetwork.MasterClient.ActorNumber)
            {
                return;
            }

            object[] payload =
                photonEvent.CustomData as
                    object[];

            if (payload == null ||
                payload.Length < 5)
            {
                return;
            }

            int requestId;
            bool success;
            int itemValue;
            int slotValue;
            string reason;

            try
            {
                requestId =
                    Convert.ToInt32(
                        payload[0]);

                success =
                    Convert.ToBoolean(
                        payload[1]);

                itemValue =
                    Convert.ToInt32(
                        payload[2]);

                slotValue =
                    Convert.ToInt32(
                        payload[3]);

                reason =
                    payload[4] as string ??
                    string.Empty;
            }
            catch (Exception)
            {
                return;
            }

            global::Player localPlayer =
                global::Player.localPlayer;

            if (localPlayer != null &&
                localPlayer.photonView != null &&
                localPlayer.photonView.Owner != null &&
                slotValue >= 0 &&
                slotValue <= byte.MaxValue)
            {
                SlotKey key =
                    new SlotKey(
                        localPlayer.photonView.Owner.ActorNumber,
                        (byte)slotValue);

                RemoveOnePendingClientMerge(
                    key);

                // StackCount 이벤트가 먼저 또는 나중에 도착하더라도
                // 병합 결과 수신 시점에 로컬 슬롯 UI를 다시 계산합니다.
                NotifyInventoryChanged(
                    localPlayer);
            }

            if (!success &&
                ModLogger != null)
            {
                ModLogger.LogWarning(
                    "Client stack merge rejected. " +
                    "RequestID=" +
                    requestId +
                    " | ItemID=" +
                    itemValue +
                    " | Slot=" +
                    slotValue +
                    " | Reason=" +
                    reason);
            }
        }

        private void RemoveOnePendingClientMerge(
            SlotKey key)
        {
            int pendingCount;

            if (!pendingClientMergeCounts.TryGetValue(
                    key,
                    out pendingCount))
            {
                return;
            }

            pendingCount--;

            if (pendingCount <= 0)
            {
                pendingClientMergeCounts.Remove(
                    key);
            }
            else
            {
                pendingClientMergeCounts[key] =
                    pendingCount;
            }
        }

        internal static void BeginQDropSpawnCapture()
        {
            if (!Enabled ||
                !PhotonNetwork.IsMasterClient)
            {
                return;
            }

            capturingQDropSpawn =
                true;
        }

        internal static void EndQDropSpawnCapture()
        {
            capturingQDropSpawn =
                false;
        }

        internal static void RegisterQDroppedWorldItem(
            GameObject spawnedObject)
        {
            if (!capturingQDropSpawn ||
                !PhotonNetwork.IsMasterClient ||
                spawnedObject == null)
            {
                return;
            }

            PhotonView view =
                spawnedObject.GetComponent<
                    PhotonView>();

            Item item =
                spawnedObject.GetComponent<
                    Item>();

            if (view == null ||
                view.ViewID <= 0 ||
                item == null ||
                !Spawn.IsSaleResourceId(
                    item.itemID))
            {
                return;
            }

            singleUnitDroppedItemViewIds.Add(
                view.ViewID);

            if (ModLogger != null)
            {
                ModLogger.LogDebug(
                    "Q-dropped resource registered as single unit. " +
                    "ViewID=" +
                    view.ViewID +
                    " | ItemID=" +
                    item.itemID);
            }
        }

        internal static bool BeginSingleUnitPickup(
            Item item)
        {
            activeSingleUnitPickupViewId =
                -1;

            if (!Enabled ||
                !PhotonNetwork.IsMasterClient ||
                item == null ||
                item.photonView == null ||
                item.photonView.ViewID <= 0)
            {
                return false;
            }

            int viewId =
                item.photonView.ViewID;

            if (!singleUnitDroppedItemViewIds.Contains(
                    viewId))
            {
                return false;
            }

            activeSingleUnitPickupViewId =
                viewId;

            return true;
        }

        internal static void EndSingleUnitPickup()
        {
            activeSingleUnitPickupViewId =
                -1;
        }

        internal static bool ConsumeSingleUnitPickupBonusBlock()
        {
            int viewId =
                activeSingleUnitPickupViewId;

            if (viewId <= 0 ||
                !singleUnitDroppedItemViewIds.Remove(
                    viewId))
            {
                return false;
            }

            if (ModLogger != null)
            {
                ModLogger.LogInfo(
                    "Q-dropped resource picked up as exactly one unit. " +
                    "ViewID=" +
                    viewId);
            }

            return true;
        }

        internal bool HostTryAddToExistingStack(
            global::Player player,
            ushort itemId,
            out ItemSlot resultSlot)
        {
            resultSlot =
                null;

            if (!Enabled ||
                !PhotonNetwork.IsMasterClient ||
                !IsGameplayActive() ||
                player == null ||
                !IsStackableItemId(
                    itemId))
            {
                return false;
            }

            ItemSlot stackSlot;

            if (!TryFindStackWithSpace(
                    player,
                    itemId,
                    out stackSlot))
            {
                return false;
            }

            int oldCount =
                GetCountInternal(
                    player,
                    stackSlot.itemSlotID);

            int newCount =
                Mathf.Clamp(
                    oldCount + 1,
                    1,
                    MaximumStackCount);

            SetCountOnHost(
                player,
                stackSlot.itemSlotID,
                newCount,
                "PickupMerge");

            resultSlot =
                stackSlot;

            NotifyInventoryChanged(
                player);

            Logger.LogInfo(
                "Item merged into stack. " +
                GetPlayerLogName(
                    player) +
                " | Slot=" +
                stackSlot.itemSlotID +
                " | ItemID=" +
                itemId +
                " | Count=" +
                oldCount +
                "->" +
                newCount);

            return true;
        }

        internal void HostRegisterNewSlot(
            global::Player player,
            ItemSlot slot,
            ushort itemId)
        {
            if (!Enabled ||
                !PhotonNetwork.IsMasterClient ||
                player == null ||
                slot == null ||
                !IsStackableItemId(
                    itemId))
            {
                return;
            }

            SetCountOnHost(
                player,
                slot.itemSlotID,
                1,
                "NewStack");

            NotifyInventoryChanged(
                player);
        }

        internal bool HostConsumeOneFromSlot(
            global::Player player,
            byte slotId,
            string reason,
            bool synchronizeInventory)
        {
            if (!Enabled ||
                !PhotonNetwork.IsMasterClient ||
                player == null)
            {
                return false;
            }

            ItemSlot slot =
                player.GetItemSlot(
                    slotId);

            if (slot == null ||
                slot.IsEmpty() ||
                slot.prefab == null ||
                !IsStackableItemId(
                    slot.prefab.itemID))
            {
                return false;
            }

            int oldCount =
                GetCountInternal(
                    player,
                    slotId);

            if (oldCount <= 1)
            {
                RemoveCountOnHost(
                    player,
                    slotId,
                    reason +
                    ":FinalItem");

                return false;
            }

            int newCount =
                oldCount - 1;

            SetCountOnHost(
                player,
                slotId,
                newCount,
                reason);

            NotifyInventoryChanged(
                player);

            if (synchronizeInventory)
            {
                SyncPlayerInventoryFromHost(
                    player);
            }

            Logger.LogInfo(
                "One item consumed from stack. " +
                GetPlayerLogName(
                    player) +
                " | Slot=" +
                slotId +
                " | ItemID=" +
                slot.prefab.itemID +
                " | Count=" +
                oldCount +
                "->" +
                newCount +
                " | Reason=" +
                reason);

            return true;
        }

        internal void HostRemoveFinalStackEntry(
            global::Player player,
            byte slotId,
            string reason)
        {
            if (!Enabled ||
                !PhotonNetwork.IsMasterClient ||
                player == null)
            {
                return;
            }

            RemoveCountOnHost(
                player,
                slotId,
                reason);
        }

        private void SetCountOnHost(
            global::Player player,
            byte slotId,
            int count,
            string reason)
        {
            if (!PhotonNetwork.IsMasterClient ||
                player == null ||
                player.photonView == null ||
                player.photonView.Owner == null)
            {
                return;
            }

            int actorNumber =
                player.photonView
                    .Owner
                    .ActorNumber;

            SlotKey key =
                new SlotKey(
                    actorNumber,
                    slotId);

            int safeCount =
                Mathf.Clamp(
                    count,
                    1,
                    MaximumConfigStackCount);

            stackCounts[key] =
                safeCount;

            BroadcastStackCount(
                key,
                safeCount);

            if (ModLogger != null)
            {
                ModLogger.LogDebug(
                    "Stack count set. " +
                    key +
                    " | Count=" +
                    safeCount +
                    " | Reason=" +
                    reason);
            }
        }

        private void RemoveCountOnHost(
            global::Player player,
            byte slotId,
            string reason)
        {
            if (!PhotonNetwork.IsMasterClient ||
                player == null ||
                player.photonView == null ||
                player.photonView.Owner == null)
            {
                return;
            }

            SlotKey key =
                new SlotKey(
                    player.photonView
                        .Owner
                        .ActorNumber,
                    slotId);

            stackCounts.Remove(
                key);

            BroadcastStackCount(
                key,
                0);

            if (ModLogger != null)
            {
                ModLogger.LogDebug(
                    "Stack entry removed. " +
                    key +
                    " | Reason=" +
                    reason);
            }
        }

        private void BroadcastStackCount(
            SlotKey key,
            int count)
        {
            if (!PhotonNetwork.InRoom ||
                !PhotonNetwork.IsMasterClient)
            {
                return;
            }

            object[] payload =
            {
                key.ActorNumber,
                (int)key.SlotId,
                count
            };

            RaiseEventOptions options =
                new RaiseEventOptions
                {
                    Receivers =
                        ReceiverGroup.All
                };

            PhotonNetwork.RaiseEvent(
                StackCountEventCode,
                payload,
                options,
                SendOptions.SendReliable);
        }

        private void SendSnapshotToPlayer(
            int targetActorNumber)
        {
            if (!PhotonNetwork.InRoom ||
                !PhotonNetwork.IsMasterClient ||
                targetActorNumber <= 0)
            {
                return;
            }

            List<object> payload =
                new List<object>();

            payload.Add(
                stackCounts.Count);

            foreach (
                KeyValuePair<SlotKey, int> pair
                in stackCounts)
            {
                payload.Add(
                    pair.Key.ActorNumber);

                payload.Add(
                    (int)pair.Key.SlotId);

                payload.Add(
                    pair.Value);
            }

            RaiseEventOptions options =
                new RaiseEventOptions
                {
                    TargetActors =
                        new[]
                        {
                            targetActorNumber
                        }
                };

            PhotonNetwork.RaiseEvent(
                StackSnapshotEventCode,
                payload.ToArray(),
                options,
                SendOptions.SendReliable);

            List<object> backpackPayload =
                new List<object>();

            backpackPayload.Add(
                backpackStackCounts.Count);

            foreach (
                KeyValuePair<BackpackSlotKey, int> pair
                in backpackStackCounts)
            {
                backpackPayload.Add(
                    pair.Key.ActorNumber);

                backpackPayload.Add(
                    (int)pair.Key.BackpackSlotIndex);

                backpackPayload.Add(
                    pair.Value);
            }

            PhotonNetwork.RaiseEvent(
                BackpackStackSnapshotEventCode,
                backpackPayload.ToArray(),
                options,
                SendOptions.SendReliable);

            Logger.LogInfo(
                "Stack snapshot sent. TargetActor=" +
                targetActorNumber +
                " | InventoryEntries=" +
                stackCounts.Count +
                " | BackpackEntries=" +
                backpackStackCounts.Count);
        }

        public void OnEvent(
            EventData photonEvent)
        {
            if (photonEvent == null)
            {
                return;
            }

            if (photonEvent.Code ==
                ClientSnapshotRequestEventCode)
            {
                if (PhotonNetwork.IsMasterClient)
                {
                    SendSnapshotToPlayer(
                        photonEvent.Sender);

                    BroadcastMaximumStackCount(
                        photonEvent.Sender);
                }

                return;
            }

            if (photonEvent.Code ==
                ClientMergeRequestEventCode)
            {
                if (PhotonNetwork.IsMasterClient)
                {
                    ProcessClientMergeRequestOnHost(
                        photonEvent.Sender,
                        photonEvent.CustomData as
                            object[]);
                }

                return;
            }

            if (photonEvent.Code ==
                ClientMergeResultEventCode)
            {
                ApplyClientMergeResult(
                    photonEvent);

                return;
            }

            if (photonEvent.Code ==
                StackCountEventCode)
            {
                ApplyStackCountEvent(
                    photonEvent.CustomData as
                        object[]);

                return;
            }

            if (photonEvent.Code ==
                StackSnapshotEventCode)
            {
                ApplyStackSnapshot(
                    photonEvent.CustomData as
                        object[]);

                return;
            }

            if (photonEvent.Code ==
                StackConfigEventCode)
            {
                ApplyMaximumStackCountEvent(
                    photonEvent);

                return;
            }

            if (photonEvent.Code ==
                BackpackStackCountEventCode)
            {
                ApplyBackpackStackCountEvent(
                    photonEvent.CustomData as
                        object[]);

                return;
            }

            if (photonEvent.Code ==
                BackpackStackSnapshotEventCode)
            {
                ApplyBackpackStackSnapshot(
                    photonEvent.CustomData as
                        object[]);
            }
        }

        private void ApplyBackpackStackCountEvent(
            object[] payload)
        {
            if (payload == null ||
                payload.Length < 3)
            {
                return;
            }

            int actorNumber;
            int slotValue;
            int count;

            try
            {
                actorNumber =
                    Convert.ToInt32(
                        payload[0]);

                slotValue =
                    Convert.ToInt32(
                        payload[1]);

                count =
                    Convert.ToInt32(
                        payload[2]);
            }
            catch (Exception)
            {
                return;
            }

            if (slotValue < 0 ||
                slotValue > byte.MaxValue)
            {
                return;
            }

            BackpackSlotKey key =
                new BackpackSlotKey(
                    actorNumber,
                    (byte)slotValue);

            if (count <= 0)
            {
                backpackStackCounts.Remove(
                    key);
            }
            else
            {
                backpackStackCounts[key] =
                    Mathf.Clamp(
                        count,
                        1,
                        MaximumConfigStackCount);
            }

            RefreshPlayerInventoryPresentation(
                actorNumber);

            global::Player localPlayer =
                global::Player.localPlayer;

            if (localPlayer != null &&
                localPlayer.photonView != null &&
                localPlayer.photonView.Owner != null &&
                localPlayer.photonView.Owner.ActorNumber ==
                    actorNumber)
            {
                NotifyInventoryChanged(
                    localPlayer);
            }
        }

        private void ApplyBackpackStackSnapshot(
            object[] payload)
        {
            if (payload == null ||
                payload.Length < 1)
            {
                return;
            }

            int entryCount;

            try
            {
                entryCount =
                    Convert.ToInt32(
                        payload[0]);
            }
            catch (Exception)
            {
                return;
            }

            backpackStackCounts.Clear();

            for (int i = 0;
                 i < entryCount;
                 i++)
            {
                int baseIndex =
                    1 +
                    i * 3;

                if (baseIndex + 2 >=
                    payload.Length)
                {
                    break;
                }

                int actorNumber;
                int slotValue;
                int count;

                try
                {
                    actorNumber =
                        Convert.ToInt32(
                            payload[baseIndex]);

                    slotValue =
                        Convert.ToInt32(
                            payload[baseIndex + 1]);

                    count =
                        Convert.ToInt32(
                            payload[baseIndex + 2]);
                }
                catch (Exception)
                {
                    continue;
                }

                if (slotValue < 0 ||
                    slotValue > byte.MaxValue ||
                    count <= 0)
                {
                    continue;
                }

                backpackStackCounts[
                    new BackpackSlotKey(
                        actorNumber,
                        (byte)slotValue)] =
                            Mathf.Clamp(
                                count,
                                1,
                                MaximumConfigStackCount);
            }

            RefreshAllInventoryPresentations();
        }

        private void ApplyStackCountEvent(
            object[] payload)
        {
            if (payload == null ||
                payload.Length < 3)
            {
                return;
            }

            int actorNumber;
            int slotValue;
            int count;

            try
            {
                actorNumber =
                    Convert.ToInt32(
                        payload[0]);

                slotValue =
                    Convert.ToInt32(
                        payload[1]);

                count =
                    Convert.ToInt32(
                        payload[2]);
            }
            catch (Exception)
            {
                return;
            }

            if (slotValue < 0 ||
                slotValue > byte.MaxValue)
            {
                return;
            }

            SlotKey key =
                new SlotKey(
                    actorNumber,
                    (byte)slotValue);

            if (count <= 0)
            {
                stackCounts.Remove(
                    key);
            }
            else
            {
                stackCounts[key] =
                    Mathf.Clamp(
                        count,
                        1,
                        MaximumConfigStackCount);
            }

            RefreshPlayerInventoryPresentation(
                actorNumber);
        }

        private void ApplyStackSnapshot(
            object[] payload)
        {
            if (payload == null ||
                payload.Length < 1)
            {
                return;
            }

            int entryCount;

            try
            {
                entryCount =
                    Convert.ToInt32(
                        payload[0]);
            }
            catch (Exception)
            {
                return;
            }

            stackCounts.Clear();

            for (int i = 0;
                 i < entryCount;
                 i++)
            {
                int actorIndex =
                    1 +
                    i * 3;

                int slotIndex =
                    actorIndex + 1;

                int countIndex =
                    actorIndex + 2;

                if (countIndex >=
                    payload.Length)
                {
                    break;
                }

                int actorNumber;
                int slotValue;
                int count;

                try
                {
                    actorNumber =
                        Convert.ToInt32(
                            payload[
                                actorIndex]);

                    slotValue =
                        Convert.ToInt32(
                            payload[
                                slotIndex]);

                    count =
                        Convert.ToInt32(
                            payload[
                                countIndex]);
                }
                catch (Exception)
                {
                    continue;
                }

                if (slotValue < 0 ||
                    slotValue > byte.MaxValue ||
                    count <= 0)
                {
                    continue;
                }

                SlotKey key =
                    new SlotKey(
                        actorNumber,
                        (byte)slotValue);

                stackCounts[key] =
                    Mathf.Clamp(
                        count,
                        1,
                        MaximumConfigStackCount);
            }

            foreach (global::Player player in
                     PlayerHandler.GetAllPlayers())
            {
                NotifyInventoryChanged(
                    player);
            }

            global::Player localPlayer =
                global::Player.localPlayer;

            if (localPlayer != null)
            {
                NotifyInventoryChanged(
                    localPlayer);
            }

            Logger.LogInfo(
                "Stack snapshot applied. Entries=" +
                stackCounts.Count);
        }

        private void EnsureAllPlayerStackEntries()
        {
            foreach (global::Player player in
                     PlayerHandler.GetAllPlayers())
            {
                if (player == null)
                {
                    continue;
                }

                for (int i = 0;
                     i < player.itemSlots.Length;
                     i++)
                {
                    EnsureSlotEntry(
                        player,
                        player.itemSlots[i]);
                }

                EnsureSlotEntry(
                    player,
                    player.tempFullSlot);

                EnsureBackpackStackEntries(
                    player);
            }
        }

        private void EnsureBackpackStackEntries(
            global::Player player)
        {
            BackpackData backpackData;

            if (!TryGetBackpackData(
                    player,
                    out backpackData) ||
                backpackData.itemSlots == null ||
                player.photonView == null ||
                player.photonView.Owner == null)
            {
                return;
            }

            for (byte i = 0;
                 i < backpackData.itemSlots.Length;
                 i++)
            {
                ItemSlot slot =
                    backpackData.itemSlots[i];

                if (slot == null ||
                    slot.IsEmpty() ||
                    slot.prefab == null ||
                    !IsStackableItemId(
                        slot.prefab.itemID))
                {
                    continue;
                }

                BackpackSlotKey key =
                    new BackpackSlotKey(
                        player.photonView.Owner.ActorNumber,
                        i);

                if (backpackStackCounts.ContainsKey(
                        key))
                {
                    continue;
                }

                SetBackpackCountOnHost(
                    player,
                    i,
                    1,
                    "EnsureExistingBackpackSlot");
            }
        }

        private void SetBackpackCountOnHost(
            global::Player player,
            byte backpackSlotIndex,
            int count,
            string reason)
        {
            if (!PhotonNetwork.IsMasterClient ||
                player == null ||
                player.photonView == null ||
                player.photonView.Owner == null)
            {
                return;
            }

            BackpackSlotKey key =
                new BackpackSlotKey(
                    player.photonView.Owner.ActorNumber,
                    backpackSlotIndex);

            int safeCount =
                Mathf.Clamp(
                    count,
                    1,
                    MaximumConfigStackCount);

            backpackStackCounts[key] =
                safeCount;

            BroadcastBackpackStackCount(
                key,
                safeCount);

            if (ModLogger != null)
            {
                ModLogger.LogInfo(
                    "Backpack stack count set. " +
                    key +
                    " | Count=" +
                    safeCount +
                    " | Reason=" +
                    reason);
            }
        }

        private void RemoveBackpackCountOnHost(
            global::Player player,
            byte backpackSlotIndex,
            string reason)
        {
            if (!PhotonNetwork.IsMasterClient ||
                player == null ||
                player.photonView == null ||
                player.photonView.Owner == null)
            {
                return;
            }

            BackpackSlotKey key =
                new BackpackSlotKey(
                    player.photonView.Owner.ActorNumber,
                    backpackSlotIndex);

            backpackStackCounts.Remove(
                key);

            BroadcastBackpackStackCount(
                key,
                0);

            if (ModLogger != null)
            {
                ModLogger.LogInfo(
                    "Backpack stack entry removed. " +
                    key +
                    " | Reason=" +
                    reason);
            }
        }

        private void BroadcastBackpackStackCount(
            BackpackSlotKey key,
            int count)
        {
            if (!PhotonNetwork.InRoom ||
                !PhotonNetwork.IsMasterClient)
            {
                return;
            }

            PhotonNetwork.RaiseEvent(
                BackpackStackCountEventCode,
                new object[]
                {
                    key.ActorNumber,
                    (int)key.BackpackSlotIndex,
                    count
                },
                new RaiseEventOptions
                {
                    Receivers =
                        ReceiverGroup.All
                },
                SendOptions.SendReliable);
        }

        private int GetActualBackpackDataCount(
            BackpackData backpackData,
            byte backpackSlotIndex,
            ItemSlot actualBackpackSlot)
        {
            if (backpackData == null ||
                backpackData.itemSlots == null ||
                backpackSlotIndex >=
                    backpackData.itemSlots.Length ||
                actualBackpackSlot == null ||
                actualBackpackSlot.IsEmpty() ||
                actualBackpackSlot.prefab == null)
            {
                return 0;
            }

            int[] counts;

            if (!backpackStackCountsByActualData.TryGetValue(
                    backpackData,
                    out counts) ||
                counts == null ||
                backpackSlotIndex >=
                    counts.Length)
            {
                return 0;
            }

            int count =
                counts[
                    backpackSlotIndex];

            return
                count > 0
                    ? Mathf.Clamp(
                        count,
                        1,
                        MaximumConfigStackCount)
                    : 0;
        }

        private void SetActualBackpackDataCount(
            BackpackData backpackData,
            byte backpackSlotIndex,
            ItemSlot actualBackpackSlot,
            int count)
        {
            if (backpackData == null ||
                backpackData.itemSlots == null ||
                backpackSlotIndex >=
                    backpackData.itemSlots.Length ||
                actualBackpackSlot == null ||
                actualBackpackSlot.IsEmpty() ||
                actualBackpackSlot.prefab == null)
            {
                return;
            }

            int[] counts;

            if (!backpackStackCountsByActualData.TryGetValue(
                    backpackData,
                    out counts) ||
                counts == null ||
                counts.Length !=
                    backpackData.itemSlots.Length)
            {
                counts =
                    new int[
                        backpackData.itemSlots.Length];

                backpackStackCountsByActualData[
                    backpackData] =
                        counts;
            }

            counts[
                backpackSlotIndex] =
                    Mathf.Clamp(
                        count,
                        1,
                        MaximumConfigStackCount);

            SetBackpackGuidCount(
                actualBackpackSlot,
                count);
        }

        private void RemoveActualBackpackDataCount(
            BackpackData backpackData,
            byte backpackSlotIndex,
            string guid)
        {
            if (backpackData != null)
            {
                int[] counts;

                if (backpackStackCountsByActualData.TryGetValue(
                        backpackData,
                        out counts) &&
                    counts != null &&
                    backpackSlotIndex <
                        counts.Length)
                {
                    counts[
                        backpackSlotIndex] =
                            0;
                }
            }

            RemoveBackpackGuidCount(
                guid);
        }

        private int GetStoredBackpackStackCountForActualData(
            BackpackData backpackData,
            int actorNumber,
            byte backpackSlotIndex,
            ItemSlot actualBackpackSlot)
        {
            int actualDataCount =
                GetActualBackpackDataCount(
                    backpackData,
                    backpackSlotIndex,
                    actualBackpackSlot);

            if (actualDataCount > 0)
            {
                return actualDataCount;
            }

            return
                GetStoredBackpackStackCountForActualSlot(
                    actorNumber,
                    backpackSlotIndex,
                    actualBackpackSlot);
        }

        private int GetStoredBackpackStackCountForActualSlot(
            int actorNumber,
            byte backpackSlotIndex,
            ItemSlot actualBackpackSlot)
        {
            if (actualBackpackSlot == null ||
                actualBackpackSlot.IsEmpty() ||
                actualBackpackSlot.prefab == null)
            {
                return 0;
            }

            string guid =
                actualBackpackSlot.data != null
                    ? actualBackpackSlot.data.guid.ToString()
                    : string.Empty;

            int guidCount;

            if (!string.IsNullOrEmpty(
                    guid) &&
                backpackStackCountsByGuid.TryGetValue(
                    guid,
                    out guidCount))
            {
                return
                    Mathf.Clamp(
                        guidCount,
                        1,
                        MaximumConfigStackCount);
            }

            return
                GetStoredBackpackStackCountDirect(
                    actorNumber,
                    backpackSlotIndex,
                    actualBackpackSlot);
        }

        private void SetBackpackGuidCount(
            ItemSlot actualBackpackSlot,
            int count)
        {
            if (actualBackpackSlot == null ||
                actualBackpackSlot.IsEmpty() ||
                actualBackpackSlot.data == null)
            {
                return;
            }

            string guid =
                actualBackpackSlot.data.guid.ToString();

            if (string.IsNullOrEmpty(
                    guid))
            {
                return;
            }

            backpackStackCountsByGuid[
                guid] =
                    Mathf.Clamp(
                        count,
                        1,
                        MaximumConfigStackCount);
        }

        private void RemoveBackpackGuidCount(
            string guid)
        {
            if (string.IsNullOrEmpty(
                    guid))
            {
                return;
            }

            backpackStackCountsByGuid.Remove(
                guid);
        }

        private int GetStoredBackpackStackCountDirect(
            int actorNumber,
            byte backpackSlotIndex,
            ItemSlot actualBackpackSlot)
        {
            if (actualBackpackSlot == null ||
                actualBackpackSlot.IsEmpty() ||
                actualBackpackSlot.prefab == null)
            {
                return 0;
            }

            int count;

            if (!backpackStackCounts.TryGetValue(
                    new BackpackSlotKey(
                        actorNumber,
                        backpackSlotIndex),
                    out count))
            {
                return 1;
            }

            return
                Mathf.Clamp(
                    count,
                    1,
                    MaximumConfigStackCount);
        }

        internal BackpackWithdrawalState CaptureBackpackWithdrawal(
            Item backpackItem,
            PhotonView characterView)
        {
            BackpackWithdrawalState state =
                default(BackpackWithdrawalState);

            if (!PhotonNetwork.IsMasterClient ||
                backpackItem == null ||
                characterView == null ||
                backpackItem.itemState !=
                    ItemState.InBackpack ||
                backpackItem.backpackReference.IsNone)
            {
                return state;
            }

            Character character =
                characterView.GetComponent<Character>();

            global::Player player =
                character != null
                    ? character.player
                    : null;

            if (player == null ||
                player.photonView == null ||
                player.photonView.Owner == null)
            {
                return state;
            }

            ValueTuple<byte, BackpackReference> reference =
                backpackItem.backpackReference.Value;

            byte backpackSlotIndex =
                reference.Item1;

            BackpackData backpackData =
                reference.Item2.GetData();

            if (backpackData == null ||
                backpackData.itemSlots == null ||
                backpackSlotIndex >=
                    backpackData.itemSlots.Length)
            {
                return state;
            }

            ItemSlot backpackSlot =
                backpackData.itemSlots[
                    backpackSlotIndex];

            if (backpackSlot == null ||
                backpackSlot.IsEmpty() ||
                backpackSlot.prefab == null)
            {
                return state;
            }

            int count =
                GetStoredBackpackStackCountForActualData(
                    backpackData,
                    player.photonView.Owner.ActorNumber,
                    backpackSlotIndex,
                    backpackSlot);

            if (ModLogger != null)
            {
                ModLogger.LogInfo(
                    "Backpack withdrawal direct count lookup. " +
                    GetPlayerLogName(
                        player) +
                    " | BackpackType=" +
                    reference.Item2.type +
                    " | BackpackSlot=" +
                    backpackSlotIndex +
                    " | ItemID=" +
                    backpackSlot.prefab.itemID +
                    " | Count=" +
                    count);
            }

            if (count <= 1)
            {
                return state;
            }

            state.IsValid =
                true;

            state.ActorNumber =
                player.photonView.Owner.ActorNumber;

            state.BackpackSlotIndex =
                backpackSlotIndex;

            state.ItemId =
                backpackSlot.prefab.itemID;

            state.Count =
                count;

            state.Guid =
                backpackSlot.data != null
                    ? backpackSlot.data.guid.ToString()
                    : string.Empty;

            state.ActualBackpackData =
                backpackData;

            if (ModLogger != null)
            {
                ModLogger.LogInfo(
                    "Backpack withdrawal stack captured. " +
                    GetPlayerLogName(
                        player) +
                    " | BackpackSlot=" +
                    backpackSlotIndex +
                    " | ItemID=" +
                    state.ItemId +
                    " | Count=" +
                    state.Count +
                    " | Guid=" +
                    state.Guid);
            }

            return state;
        }

        internal bool TryHandleBackpackWithdrawalIntoExistingStack(
            Item backpackItem,
            PhotonView characterView,
            BackpackWithdrawalState state)
        {
            if (!PhotonNetwork.IsMasterClient ||
                !state.IsValid ||
                backpackItem == null ||
                characterView == null)
            {
                return false;
            }

            Character character =
                characterView.GetComponent<Character>();

            global::Player player =
                character != null
                    ? character.player
                    : null;

            if (player == null ||
                player.photonView == null ||
                player.photonView.Owner == null ||
                player.photonView.Owner.ActorNumber !=
                    state.ActorNumber ||
                character.refs == null ||
                character.refs.view == null)
            {
                return false;
            }

            ItemSlot targetSlot;

            if (!TryFindStackWithSpace(
                    player,
                    state.ItemId,
                    out targetSlot) ||
                targetSlot == null ||
                targetSlot.IsEmpty() ||
                targetSlot.prefab == null ||
                targetSlot.prefab.itemID !=
                    state.ItemId)
            {
                return false;
            }

            int currentCount =
                Mathf.Max(
                    1,
                    GetCountInternal(
                        player,
                        targetSlot.itemSlotID));

            int combinedCount =
                currentCount +
                state.Count;

            // 기존 스택과 배낭 전체 수량이 현재 최대 적재량 안에 들어가는
            // 경우에만 이 직접 병합 경로를 사용합니다.
            // 들어가지 않으면 원본 RequestPickup 경로가 새 슬롯을 선택할
            // 가능성을 유지합니다.
            if (combinedCount >
                MaximumStackCount)
            {
                return false;
            }

            SetCountOnHost(
                player,
                targetSlot.itemSlotID,
                combinedCount,
                "BackpackWithdrawalDirectExistingStackMerge");

            // Player.AddItem을 호출하지 않고 원본 RequestPickup의
            // 배낭 정리 및 획득 승인 부분만 그대로 수행합니다.
            // 이렇게 하면 HarmonyX가 out ItemSlot을 null로 남기는 경우에도
            // itemSlot.itemSlotID NullReferenceException이 발생하지 않습니다.
            backpackItem.ClearDataFromBackpack();

            RemoveBackpackCountOnHost(
                player,
                state.BackpackSlotIndex,
                "BackpackWithdrawalDirectExistingStackCompleted");

            RemoveActualBackpackDataCount(
                state.ActualBackpackData,
                state.BackpackSlotIndex,
                state.Guid);

            NotifyInventoryChanged(
                player);

            SyncPlayerInventoryFromHost(
                player);

            character.refs.view.RPC(
                "OnPickupAccepted",
                player.photonView.Owner,
                new object[]
                {
                    targetSlot.itemSlotID
                });

            if (ModLogger != null)
            {
                ModLogger.LogInfo(
                    "Backpack withdrawal merged directly into existing stack. " +
                    GetPlayerLogName(
                        player) +
                    " | BackpackSlot=" +
                    state.BackpackSlotIndex +
                    " | InventorySlot=" +
                    targetSlot.itemSlotID +
                    " | ItemID=" +
                    state.ItemId +
                    " | ExistingCount=" +
                    currentCount +
                    " | BackpackCount=" +
                    state.Count +
                    " | Formula=" +
                    currentCount +
                    "+" +
                    state.Count +
                    " | Count=" +
                    combinedCount +
                    " | Guid=" +
                    state.Guid);
            }

            return true;
        }

        internal void CompleteBackpackWithdrawal(
            PhotonView characterView,
            BackpackWithdrawalState state)
        {
            if (!PhotonNetwork.IsMasterClient ||
                !state.IsValid ||
                characterView == null)
            {
                return;
            }

            Character character =
                characterView.GetComponent<Character>();

            global::Player player =
                character != null
                    ? character.player
                    : null;

            if (player == null ||
                player.photonView == null ||
                player.photonView.Owner == null)
            {
                return;
            }

            ItemSlot targetSlot =
                FindInventorySlotForWithdrawnItem(
                    player,
                    state.ItemId,
                    state.Guid);

            if (targetSlot == null ||
                targetSlot.IsEmpty() ||
                targetSlot.prefab == null ||
                targetSlot.prefab.itemID !=
                    state.ItemId)
            {
                if (ModLogger != null)
                {
                    ModLogger.LogError(
                        "Backpack withdrawal stack destination not found. " +
                        GetPlayerLogName(
                            player) +
                        " | BackpackSlot=" +
                        state.BackpackSlotIndex +
                        " | ItemID=" +
                        state.ItemId +
                        " | ExpectedCount=" +
                        state.Count +
                        " | Guid=" +
                        state.Guid);
                }

                return;
            }

            int currentCount =
                Mathf.Max(
                    1,
                    GetCountInternal(
                        player,
                        targetSlot.itemSlotID));

            // 원본 RequestPickup의 Player.AddItem이 대표 아이템 1개를
            // 이미 대상 슬롯에 추가했습니다.
            // 따라서 기존 슬롯 현재 수량에 배낭 수량-1을 더해야 합니다.
            int restoredCount =
                Mathf.Clamp(
                    currentCount +
                    Mathf.Max(
                        0,
                        state.Count -
                        1),
                    1,
                    MaximumStackCount);

            SetCountOnHost(
                player,
                targetSlot.itemSlotID,
                restoredCount,
                "BackpackWithdrawalFullStackRestore");

            RemoveBackpackCountOnHost(
                player,
                state.BackpackSlotIndex,
                "BackpackWithdrawalCompleted");

            RemoveActualBackpackDataCount(
                state.ActualBackpackData,
                state.BackpackSlotIndex,
                state.Guid);

            NotifyInventoryChanged(
                player);

            SyncPlayerInventoryFromHost(
                player);

            if (ModLogger != null)
            {
                ModLogger.LogInfo(
                    "Backpack withdrawal full stack restored. " +
                    GetPlayerLogName(
                        player) +
                    " | BackpackSlot=" +
                    state.BackpackSlotIndex +
                    " | InventorySlot=" +
                    targetSlot.itemSlotID +
                    " | ItemID=" +
                    state.ItemId +
                    " | CurrentAfterRepresentativeAdd=" +
                    currentCount +
                    " | BackpackCount=" +
                    state.Count +
                    " | Formula=" +
                    currentCount +
                    "+" +
                    Mathf.Max(
                        0,
                        state.Count -
                        1) +
                    " | Count=" +
                    restoredCount +
                    " | Guid=" +
                    state.Guid);
            }
        }

        private ItemSlot FindInventorySlotForWithdrawnItem(
            global::Player player,
            ushort itemId,
            string expectedGuid)
        {
            if (player == null)
            {
                return null;
            }

            ItemSlot guidMatch =
                null;

            ItemSlot itemMatch =
                null;

            if (player.itemSlots != null)
            {
                for (int i = 0;
                     i < player.itemSlots.Length;
                     i++)
                {
                    ItemSlot slot =
                        player.itemSlots[i];

                    if (slot == null ||
                        slot.IsEmpty() ||
                        slot.prefab == null ||
                        slot.prefab.itemID !=
                            itemId)
                    {
                        continue;
                    }

                    if (itemMatch == null)
                    {
                        itemMatch =
                            slot;
                    }

                    if (!string.IsNullOrEmpty(
                            expectedGuid) &&
                        slot.data != null &&
                        string.Equals(
                            slot.data.guid.ToString(),
                            expectedGuid,
                            StringComparison.Ordinal))
                    {
                        guidMatch =
                            slot;

                        break;
                    }
                }
            }

            if (guidMatch != null)
            {
                return guidMatch;
            }

            ItemSlot tempSlot =
                player.tempFullSlot;

            if (tempSlot != null &&
                !tempSlot.IsEmpty() &&
                tempSlot.prefab != null &&
                tempSlot.prefab.itemID ==
                    itemId)
            {
                if (!string.IsNullOrEmpty(
                        expectedGuid) &&
                    tempSlot.data != null &&
                    string.Equals(
                        tempSlot.data.guid.ToString(),
                        expectedGuid,
                        StringComparison.Ordinal))
                {
                    return tempSlot;
                }

                if (itemMatch == null)
                {
                    itemMatch =
                        tempSlot;
                }
            }

            return
                itemMatch;
        }

        internal void HostPrepareWholeStackToBackpack(
            global::Player player,
            byte sourceSlotId,
            byte backpackSlotIndex)
        {
            if (!PhotonNetwork.IsMasterClient ||
                player == null ||
                player.photonView == null ||
                player.photonView.Owner == null)
            {
                return;
            }

            ItemSlot sourceSlot =
                player.GetItemSlot(
                    sourceSlotId);

            if (sourceSlot == null ||
                sourceSlot.IsEmpty() ||
                sourceSlot.prefab == null)
            {
                return;
            }

            int sourceCount =
                Mathf.Max(
                    1,
                    GetCountInternal(
                        player,
                        sourceSlotId));

            SlotKey sourceKey =
                new SlotKey(
                    player.photonView.Owner.ActorNumber,
                    sourceSlotId);

            PendingBackpackTransfer transfer =
                new PendingBackpackTransfer
                {
                    SourceKey =
                        sourceKey,

                    BackpackSlotIndex =
                        backpackSlotIndex,

                    ItemId =
                        sourceSlot.prefab.itemID,

                    Count =
                        sourceCount,

                    Guid =
                        sourceSlot.data != null
                            ? sourceSlot.data.guid.ToString()
                            : string.Empty,

                    SourceRemovalConsumed =
                        false,

                    DestinationCountAfterMerge =
                        0
                };

            pendingWholeStackBackpackTransfers[
                sourceKey] =
                    transfer;

            if (ModLogger != null)
            {
                ModLogger.LogInfo(
                    "Whole stack reserved for backpack transfer. " +
                    GetPlayerLogName(
                        player) +
                    " | SourceSlot=" +
                    sourceSlotId +
                    " | BackpackSlot=" +
                    backpackSlotIndex +
                    " | ItemID=" +
                    transfer.ItemId +
                    " | Count=" +
                    transfer.Count +
                    " | Guid=" +
                    transfer.Guid);
            }
        }

        internal bool ConsumeWholeStackBackpackTransfer(
            global::Player player,
            byte sourceSlotId)
        {
            if (!PhotonNetwork.IsMasterClient ||
                player == null ||
                player.photonView == null ||
                player.photonView.Owner == null)
            {
                return false;
            }

            SlotKey key =
                new SlotKey(
                    player.photonView.Owner.ActorNumber,
                    sourceSlotId);

            PendingBackpackTransfer transfer;

            if (!pendingWholeStackBackpackTransfers.TryGetValue(
                    key,
                    out transfer))
            {
                return false;
            }

            pendingWholeStackBackpackTransfers.Remove(
                key);

            RemoveCountOnHost(
                player,
                sourceSlotId,
                "WholeStackMovedToBackpackConfirmed");

            if (ModLogger != null)
            {
                ModLogger.LogInfo(
                    "Whole source stack removal consumed after backpack RPC. " +
                    GetPlayerLogName(
                        player) +
                    " | SourceSlot=" +
                    sourceSlotId +
                    " | BackpackSlot=" +
                    transfer.BackpackSlotIndex +
                    " | ItemID=" +
                    transfer.ItemId +
                    " | Count=" +
                    transfer.Count);
            }

            return true;
        }

        internal int HostConfirmWholeStackInActualBackpackData(
            global::Player player,
            BackpackData actualBackpackData,
            byte sourceSlotId,
            byte backpackSlotIndex,
            string transferPath)
        {
            if (!PhotonNetwork.IsMasterClient ||
                player == null ||
                player.photonView == null ||
                player.photonView.Owner == null ||
                actualBackpackData == null ||
                actualBackpackData.itemSlots == null ||
                backpackSlotIndex >=
                    actualBackpackData.itemSlots.Length)
            {
                return 0;
            }

            SlotKey key =
                new SlotKey(
                    player.photonView.Owner.ActorNumber,
                    sourceSlotId);

            PendingBackpackTransfer transfer;

            if (!pendingWholeStackBackpackTransfers.TryGetValue(
                    key,
                    out transfer))
            {
                return 0;
            }

            ItemSlot destinationSlot =
                actualBackpackData.itemSlots[
                    backpackSlotIndex];

            bool itemMatches =
                destinationSlot != null &&
                !destinationSlot.IsEmpty() &&
                destinationSlot.prefab != null &&
                destinationSlot.prefab.itemID ==
                    transfer.ItemId;

            bool guidMatches =
                string.IsNullOrEmpty(
                    transfer.Guid) ||
                (
                    destinationSlot != null &&
                    destinationSlot.data != null &&
                    string.Equals(
                        destinationSlot.data.guid.ToString(),
                        transfer.Guid,
                        StringComparison.Ordinal)
                );

            if (!itemMatches)
            {
                if (ModLogger != null)
                {
                    ModLogger.LogError(
                        "Actual backpack RPC destination validation failed. " +
                        GetPlayerLogName(
                            player) +
                        " | SourceSlot=" +
                        sourceSlotId +
                        " | BackpackSlot=" +
                        backpackSlotIndex +
                        " | ExpectedItemID=" +
                        transfer.ItemId +
                        " | ExpectedCount=" +
                        transfer.Count +
                        " | Destination=" +
                        BuildItemSlotDiagnostic(
                            destinationSlot) +
                        " | Path=" +
                        transferPath);
                }

                return 0;
            }

            BackpackSlotKey backpackKey =
                new BackpackSlotKey(
                    player.photonView.Owner.ActorNumber,
                    backpackSlotIndex);

            int existingDestinationCount =
                GetStoredBackpackStackCountForActualData(
                    actualBackpackData,
                    player.photonView.Owner.ActorNumber,
                    backpackSlotIndex,
                    destinationSlot);

            bool hadExistingStackCount =
                existingDestinationCount > 0;

            string destinationGuid =
                destinationSlot.data != null
                    ? destinationSlot.data.guid.ToString()
                    : string.Empty;

            bool sourceContainsTemporaryBackpackRepresentative =
                hadExistingStackCount &&
                !string.IsNullOrEmpty(
                    transfer.Guid) &&
                !string.IsNullOrEmpty(
                    destinationGuid) &&
                string.Equals(
                    transfer.Guid,
                    destinationGuid,
                    StringComparison.Ordinal);

            int effectiveAddedCount =
                sourceContainsTemporaryBackpackRepresentative
                    ? Mathf.Max(
                        0,
                        transfer.Count -
                        1)
                    : transfer.Count;

            int mergedDestinationCount =
                hadExistingStackCount
                    ? Mathf.Clamp(
                        existingDestinationCount +
                        effectiveAddedCount,
                        1,
                        MaximumConfigStackCount)
                    : Mathf.Clamp(
                        transfer.Count,
                        1,
                        MaximumConfigStackCount);

            transfer.DestinationCountAfterMerge =
                mergedDestinationCount;

            pendingWholeStackBackpackTransfers[
                key] =
                    transfer;

            SetBackpackCountOnHost(
                player,
                backpackSlotIndex,
                mergedDestinationCount,
                hadExistingStackCount
                    ? "ExistingBackpackStackMerged:" +
                      transferPath
                    : "ActualBackpackRpcConfirmed:" +
                      transferPath);

            SetActualBackpackDataCount(
                actualBackpackData,
                backpackSlotIndex,
                destinationSlot,
                mergedDestinationCount);

            if (ModLogger != null)
            {
                ModLogger.LogInfo(
                    "Whole stack confirmed in actual backpack data. " +
                    GetPlayerLogName(
                        player) +
                    " | SourceSlot=" +
                    sourceSlotId +
                    " | BackpackSlot=" +
                    backpackSlotIndex +
                    " | ItemID=" +
                    transfer.ItemId +
                    " | ExistingCount=" +
                    (
                        hadExistingStackCount
                            ? existingDestinationCount
                            : 0
                    ) +
                    " | RawSourceCount=" +
                    transfer.Count +
                    " | TemporaryRepresentativeExcluded=" +
                    sourceContainsTemporaryBackpackRepresentative +
                    " | EffectiveAddedCount=" +
                    effectiveAddedCount +
                    " | MergedCount=" +
                    mergedDestinationCount +
                    " | GuidMatches=" +
                    guidMatches +
                    " | Path=" +
                    transferPath);
            }

            // 예약은 여기서 제거하지 않습니다.
            // Backpack.Stash가 뒤이어 Player.EmptySlot을 호출할 때 전체 스택을
            // 물리 슬롯과 수량 메타데이터에서 함께 제거해야 합니다.
            return
                transfer.DestinationCountAfterMerge;
        }

        private int GetConfirmedBackpackTransferCount(
            global::Player player,
            byte sourceSlotId,
            byte backpackSlotIndex)
        {
            if (player == null ||
                player.photonView == null ||
                player.photonView.Owner == null)
            {
                return 0;
            }

            PendingBackpackTransfer transfer;

            if (!pendingWholeStackBackpackTransfers.TryGetValue(
                    new SlotKey(
                        player.photonView.Owner.ActorNumber,
                        sourceSlotId),
                    out transfer) ||
                transfer.BackpackSlotIndex !=
                    backpackSlotIndex)
            {
                return 0;
            }

            return
                Mathf.Clamp(
                    transfer.DestinationCountAfterMerge > 0
                        ? transfer.DestinationCountAfterMerge
                        : transfer.Count,
                    1,
                    MaximumStackCount);
        }

        internal int HostConsolidateMatchingItemsIntoBackpackSlot(
            global::Player player,
            BackpackData backpackData,
            byte originalSourceSlotId,
            byte destinationBackpackSlotIndex,
            string transferPath)
        {
            if (!PhotonNetwork.IsMasterClient ||
                player == null ||
                player.itemSlots == null ||
                backpackData == null ||
                backpackData.itemSlots == null ||
                destinationBackpackSlotIndex >=
                    backpackData.itemSlots.Length)
            {
                return 0;
            }

            ItemSlot destinationSlot =
                backpackData.itemSlots[
                    destinationBackpackSlotIndex];

            if (destinationSlot == null ||
                destinationSlot.IsEmpty() ||
                destinationSlot.prefab == null)
            {
                return 0;
            }

            ushort targetItemId =
                destinationSlot.prefab.itemID;

            int totalCount =
                GetConfirmedBackpackTransferCount(
                    player,
                    originalSourceSlotId,
                    destinationBackpackSlotIndex);

            if (totalCount <= 0)
            {
                totalCount =
                    Mathf.Max(
                        1,
                        GetBackpackStackCount(
                            player,
                            destinationBackpackSlotIndex));
            }

            int movedFromInventory =
                0;

            for (int sourceIndex = 0;
                 sourceIndex < player.itemSlots.Length;
                 sourceIndex++)
            {
                ItemSlot sourceSlot =
                    player.itemSlots[
                        sourceIndex];

                if (sourceSlot == null ||
                    sourceSlot.IsEmpty() ||
                    sourceSlot.prefab == null ||
                    sourceSlot.prefab.itemID !=
                        targetItemId ||
                    sourceSlot.itemSlotID ==
                        originalSourceSlotId)
                {
                    continue;
                }

                int moved =
                    MoveMatchingSlotQuantityIntoBackpack(
                        player,
                        sourceSlot,
                        destinationBackpackSlotIndex,
                        targetItemId,
                        ref totalCount,
                        transferPath);

                movedFromInventory +=
                    moved;

                if (totalCount >=
                    MaximumStackCount)
                {
                    break;
                }
            }

            // 일반 슬롯이 아닌 실제 추가 손 슬롯에 같은 아이템이 남아 있어도
            // 현재 보유량에 포함하여 같은 배낭 슬롯 한 칸으로 합칩니다.
            ItemSlot tempSlot =
                player.tempFullSlot;

            if (totalCount <
                    MaximumStackCount &&
                tempSlot != null &&
                !tempSlot.IsEmpty() &&
                tempSlot.prefab != null &&
                tempSlot.prefab.itemID ==
                    targetItemId &&
                tempSlot.itemSlotID !=
                    originalSourceSlotId)
            {
                movedFromInventory +=
                    MoveMatchingSlotQuantityIntoBackpack(
                        player,
                        tempSlot,
                        destinationBackpackSlotIndex,
                        targetItemId,
                        ref totalCount,
                        transferPath +
                        ":TempFullSlot");
            }

            SetBackpackCountOnHost(
                player,
                destinationBackpackSlotIndex,
                totalCount,
                "MoveAllOwnedQuantityAtOnce:" +
                transferPath);

            SetActualBackpackDataCount(
                backpackData,
                destinationBackpackSlotIndex,
                destinationSlot,
                totalCount);

            NotifyInventoryChanged(
                player);

            SyncPlayerInventoryFromHost(
                player);

            if (ModLogger != null)
            {
                ModLogger.LogInfo(
                    "All currently held matching quantity moved into one backpack slot. " +
                    GetPlayerLogName(
                        player) +
                    " | BackpackSlot=" +
                    destinationBackpackSlotIndex +
                    " | ItemID=" +
                    targetItemId +
                    " | FinalCount=" +
                    totalCount +
                    " | AdditionalMoved=" +
                    movedFromInventory +
                    " | Path=" +
                    transferPath);
            }

            return
                totalCount;
        }

        private int MoveMatchingSlotQuantityIntoBackpack(
            global::Player player,
            ItemSlot sourceSlot,
            byte destinationBackpackSlotIndex,
            ushort targetItemId,
            ref int backpackTotalCount,
            string transferPath)
        {
            if (player == null ||
                sourceSlot == null ||
                sourceSlot.IsEmpty() ||
                sourceSlot.prefab == null ||
                sourceSlot.prefab.itemID !=
                    targetItemId)
            {
                return 0;
            }

            int remainingCapacity =
                MaximumStackCount -
                backpackTotalCount;

            if (remainingCapacity <= 0)
            {
                return 0;
            }

            int sourceCount =
                Mathf.Max(
                    1,
                    GetCountInternal(
                        player,
                        sourceSlot.itemSlotID));

            int amountToMove =
                Mathf.Min(
                    sourceCount,
                    remainingCapacity);

            backpackTotalCount +=
                amountToMove;

            if (amountToMove >=
                sourceCount)
            {
                RemoveCountOnHost(
                    player,
                    sourceSlot.itemSlotID,
                    "BackpackMoveAllQuantity");

                sourceSlot.EmptyOut();
            }
            else
            {
                SetCountOnHost(
                    player,
                    sourceSlot.itemSlotID,
                    sourceCount -
                    amountToMove,
                    "BackpackMoveAllQuantityPartial");
            }

            if (ModLogger != null)
            {
                ModLogger.LogInfo(
                    "Inventory quantity merged into backpack slot. " +
                    GetPlayerLogName(
                        player) +
                    " | SourceSlot=" +
                    sourceSlot.itemSlotID +
                    " | BackpackSlot=" +
                    destinationBackpackSlotIndex +
                    " | ItemID=" +
                    targetItemId +
                    " | Moved=" +
                    amountToMove +
                    " | SourceCount=" +
                    sourceCount +
                    " | BackpackCount=" +
                    backpackTotalCount +
                    " | Path=" +
                    transferPath);
            }

            return
                amountToMove;
        }

        private void EnsureSlotEntry(
            global::Player player,
            ItemSlot slot)
        {
            if (player == null ||
                slot == null ||
                slot.IsEmpty() ||
                slot.prefab == null ||
                !IsStackableItemId(
                    slot.prefab.itemID))
            {
                return;
            }

            SlotKey key =
                new SlotKey(
                    player.photonView
                        .Owner
                        .ActorNumber,
                    slot.itemSlotID);

            if (stackCounts.ContainsKey(
                    key))
            {
                return;
            }

            SetCountOnHost(
                player,
                slot.itemSlotID,
                1,
                "EnsureExistingSlot");
        }

        private bool TryResolvePlayerSlot(
            ItemSlot targetSlot,
            out global::Player player,
            out byte slotId)
        {
            player =
                null;

            slotId =
                0;

            if (targetSlot == null)
            {
                return false;
            }

            foreach (global::Player candidate in
                     PlayerHandler.GetAllPlayers())
            {
                if (candidate == null)
                {
                    continue;
                }

                for (int i = 0;
                     i < candidate.itemSlots.Length;
                     i++)
                {
                    if (ReferenceEquals(
                            candidate.itemSlots[i],
                            targetSlot))
                    {
                        player =
                            candidate;

                        slotId =
                            candidate.itemSlots[i]
                                .itemSlotID;

                        return true;
                    }
                }

                if (ReferenceEquals(
                        candidate.tempFullSlot,
                        targetSlot))
                {
                    player =
                        candidate;

                    slotId =
                        candidate.tempFullSlot
                            .itemSlotID;

                    return true;
                }
            }

            return false;
        }

        private bool TryResolveBackpackSlot(
            ItemSlot targetSlot,
            out global::Player player,
            out byte backpackSlotIndex)
        {
            player =
                null;

            backpackSlotIndex =
                0;

            if (targetSlot == null)
            {
                return false;
            }

            foreach (global::Player candidate in
                     PlayerHandler.GetAllPlayers())
            {
                BackpackData backpackData;

                if (!TryGetBackpackData(
                        candidate,
                        out backpackData) ||
                    backpackData.itemSlots == null)
                {
                    continue;
                }

                for (byte i = 0;
                     i < backpackData.itemSlots.Length;
                     i++)
                {
                    if (!ReferenceEquals(
                            backpackData.itemSlots[i],
                            targetSlot))
                    {
                        continue;
                    }

                    player =
                        candidate;

                    backpackSlotIndex =
                        i;

                    return true;
                }
            }

            return false;
        }

        internal void MarkExpectedRemoteRemove(
            global::Player player,
            byte slotId)
        {
            if (player == null ||
                player.photonView == null ||
                player.photonView.Owner == null ||
                player.photonView.IsMine)
            {
                return;
            }

            SlotKey key =
                new SlotKey(
                    player.photonView
                        .Owner
                        .ActorNumber,
                    slotId);

            ignoreNextRemoteRemoveUntil[key] =
                Time.unscaledTime +
                IgnoreDuplicateRemoveSeconds;
        }

        internal bool ConsumeExpectedRemoteRemove(
            global::Player player,
            byte slotId)
        {
            if (player == null ||
                player.photonView == null ||
                player.photonView.Owner == null)
            {
                return false;
            }

            SlotKey key =
                new SlotKey(
                    player.photonView
                        .Owner
                        .ActorNumber,
                    slotId);

            float expiresAt;

            if (!ignoreNextRemoteRemoveUntil
                    .TryGetValue(
                        key,
                        out expiresAt))
            {
                return false;
            }

            ignoreNextRemoteRemoveUntil.Remove(
                key);

            return
                Time.unscaledTime <=
                expiresAt;
        }

        private void RemoveExpiredDuplicateGuards()
        {
            if (ignoreNextRemoteRemoveUntil.Count ==
                0)
            {
                return;
            }

            expiredDuplicateGuardKeys.Clear();

            float now =
                Time.unscaledTime;

            foreach (
                KeyValuePair<SlotKey, float> pair
                in ignoreNextRemoteRemoveUntil)
            {
                if (now >
                    pair.Value)
                {
                    expiredDuplicateGuardKeys.Add(
                        pair.Key);
                }
            }

            for (int i = 0;
                 i < expiredDuplicateGuardKeys.Count;
                 i++)
            {
                ignoreNextRemoteRemoveUntil.Remove(
                    expiredDuplicateGuardKeys[i]);
            }

            expiredDuplicateGuardKeys.Clear();
        }

        private static void SyncPlayerInventoryFromHost(
            global::Player player)
        {
            if (player == null ||
                !PhotonNetwork.IsMasterClient)
            {
                return;
            }

            PhotonView playerView =
                player.GetComponent<PhotonView>();

            if (playerView == null)
            {
                return;
            }

            InventorySyncData syncData =
                new InventorySyncData(
                    player.itemSlots,
                    player.backpackSlot,
                    player.tempFullSlot);

            playerView.RPC(
                "SyncInventoryRPC",
                RpcTarget.Others,
                new object[]
                {
                    IBinarySerializable
                        .ToManagedArray<
                            InventorySyncData>(
                            syncData),

                    false
                });
        }

        private static void NotifyInventoryChanged(
            global::Player player)
        {
            if (player == null)
            {
                return;
            }

            if (player.itemsChangedAction != null)
            {
                player.itemsChangedAction(
                    player.itemSlots);
            }
        }

        private static void RefreshPlayerInventoryPresentation(
            int actorNumber)
        {
            global::Player player =
                PlayerHandler.GetPlayer(
                    actorNumber);

            NotifyInventoryChanged(
                player);

            // 일반 클라이언트에서는 PlayerHandler 등록 시점과 Photon 이벤트
            // 수신 시점이 어긋날 수 있습니다. 대상이 로컬 Actor라면
            // localPlayer도 직접 갱신해 xN 텍스트가 즉시 다시 계산되게 합니다.
            global::Player localPlayer =
                global::Player.localPlayer;

            if (localPlayer == null ||
                localPlayer.photonView == null ||
                localPlayer.photonView.Owner == null ||
                localPlayer.photonView.Owner.ActorNumber !=
                    actorNumber ||
                ReferenceEquals(
                    localPlayer,
                    player))
            {
                return;
            }

            NotifyInventoryChanged(
                localPlayer);
        }

        private static string GetPlayerLogName(
            global::Player player)
        {
            if (player == null ||
                player.photonView == null ||
                player.photonView.Owner == null)
            {
                return "Player=<unknown>";
            }

            return
                "Player=" +
                player.photonView
                    .Owner
                    .NickName +
                "(" +
                player.photonView
                    .Owner
                    .ActorNumber +
                ")";
        }

        public void OnPlayerEnteredRoom(
            Photon.Realtime.Player newPlayer)
        {
            if (!PhotonNetwork.IsMasterClient ||
                newPlayer == null)
            {
                return;
            }

            EnsureAllPlayerStackEntries();

            SendSnapshotToPlayer(
                newPlayer.ActorNumber);

            BroadcastMaximumStackCount(
                newPlayer.ActorNumber);
        }

        public void OnPlayerLeftRoom(
            Photon.Realtime.Player otherPlayer)
        {
            if (otherPlayer == null)
            {
                return;
            }

            int actorNumber =
                otherPlayer.ActorNumber;

            List<SlotKey> removeKeys =
                new List<SlotKey>();

            foreach (
                KeyValuePair<SlotKey, int> pair
                in stackCounts)
            {
                if (pair.Key.ActorNumber ==
                    actorNumber)
                {
                    removeKeys.Add(
                        pair.Key);
                }
            }

            for (int i = 0;
                 i < removeKeys.Count;
                 i++)
            {
                stackCounts.Remove(
                    removeKeys[i]);

                ignoreNextRemoteRemoveUntil.Remove(
                    removeKeys[i]);

                pendingClientMergeCounts.Remove(
                    removeKeys[i]);
            }

            List<BackpackSlotKey> backpackRemoveKeys =
                new List<BackpackSlotKey>();

            foreach (
                KeyValuePair<BackpackSlotKey, int> pair
                in backpackStackCounts)
            {
                if (pair.Key.ActorNumber ==
                    actorNumber)
                {
                    backpackRemoveKeys.Add(
                        pair.Key);
                }
            }

            for (int i = 0;
                 i < backpackRemoveKeys.Count;
                 i++)
            {
                backpackStackCounts.Remove(
                    backpackRemoveKeys[i]);
            }
        }

        public void OnRoomPropertiesUpdate(
            ExitGames.Client.Photon.Hashtable
                propertiesThatChanged)
        {
        }

        public void OnPlayerPropertiesUpdate(
            Photon.Realtime.Player targetPlayer,
            ExitGames.Client.Photon.Hashtable
                changedProps)
        {
        }

        public void OnMasterClientSwitched(
            Photon.Realtime.Player newMasterClient)
        {
            clientInitialSyncRequested =
                false;

            if (newMasterClient == null ||
                PhotonNetwork.LocalPlayer == null ||
                newMasterClient.ActorNumber !=
                    PhotonNetwork.LocalPlayer.ActorNumber)
            {
                return;
            }

            ApplyLocalConfiguredMaximum(
                "Local client became Master Client");

            EnsureAllPlayerStackEntries();

            BroadcastMaximumStackCount(
                null);

            Logger.LogInfo(
                "Local client became Master Client. " +
                "Existing stack state entries=" +
                stackCounts.Count +
                " | Host maximum stack=" +
                MaximumStackCount);
        }
    }

    /// <summary>
    /// 같은 자원의 기존 스택에 빈 공간이 있으면
    /// PEAK의 빈 슬롯 탐색 전에 기존 스택을 사용합니다.
    /// </summary>
    [HarmonyPatch(
        typeof(global::Player),
        "AddItem")]
    internal static class
        InventoryPlayerAddItemPatch
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(
            global::Player __instance,
            ushort itemID,
            ItemInstanceData instanceData,
            ref ItemSlot __2,
            ref bool __result)
        {
            // Player.AddItem의 세 번째 out ItemSlot 인수는 원본 메서드의
            // 실제 매개변수 이름과 무관하게 Harmony 위치 인수 __2로 연결합니다.
            // 기존 스택 병합 시 반환 슬롯이 null로 남으면 RequestPickup Postfix가
            // 중단되어 배낭의 나머지 수량을 복원하지 못하므로 반드시 __2를 사용합니다.

            // 같은 어셈블리에 진단 플러그인이 PatchAll을 다시 실행해
            // 이 Prefix가 중복 등록된 경우에도 한 AddItem 호출을 한 번만 처리합니다.
            if (InventoryStack.AddItemMergePrefixInProgress)
            {
                return
                    !InventoryStack.AddItemMergePrefixSkipsOriginal;
            }

            InventoryStack.AddItemMergePrefixInProgress =
                true;

            InventoryStack.AddItemMergePrefixSkipsOriginal =
                false;

            if (InventoryStack.Instance == null ||
                !InventoryStack.IsGameplayActive() ||
                !InventoryStack
                    .IsStackableItemId(
                        itemID))
            {
                return true;
            }

            ItemSlot mergedSlot;

            if (PhotonNetwork.IsMasterClient)
            {
                bool hostMerged =
                    InventoryStack.Instance
                        .HostTryAddToExistingStack(
                            __instance,
                            itemID,
                            out mergedSlot);

                if (!hostMerged)
                {
                    return true;
                }

                __2 =
                    mergedSlot;

                __result =
                    true;

                InventoryStack.AddItemMergePrefixSkipsOriginal =
                    true;

                return false;
            }

            if (__instance == null ||
                __instance.photonView == null ||
                !__instance.photonView.IsMine)
            {
                return true;
            }

            bool requestSent =
                InventoryStack.Instance
                    .ClientTryRequestStackMerge(
                        __instance,
                        itemID,
                        out mergedSlot);

            if (!requestSent)
            {
                return true;
            }

            // 로컬 수집 코드는 즉시 성공으로 끝내고,
            // 실제 권한 수량은 호스트가 요청을 받아 증가시킵니다.
            __2 =
                mergedSlot;

            __result =
                true;

            return false;
        }

        [HarmonyPostfix]
        private static void Postfix(
            global::Player __instance,
            ushort itemID,
            ItemInstanceData instanceData,
            ItemSlot __2,
            bool __result)
        {
            InventoryStack.AddItemMergePrefixInProgress =
                false;

            InventoryStack.AddItemMergePrefixSkipsOriginal =
                false;

            if (!__result ||
                InventoryStack.Instance == null ||
                !PhotonNetwork.IsMasterClient ||
                !InventoryStack
                    .IsStackableItemId(
                        itemID) ||
                __2 == null)
            {
                return;
            }

            // 호스트 Prefix에서 기존 스택을 사용한 경우에는 이미 수량을 올렸습니다.
            // 클라이언트 병합은 Prefix가 원본 메서드를 건너뛰므로 이 Postfix에 들어오지 않습니다.
            int count =
                InventoryStack.GetStackCount(
                    __instance,
                    __2.itemSlotID);

            if (count > 1)
            {
                return;
            }

            InventoryStack.Instance
                .HostRegisterNewSlot(
                    __instance,
                    __2,
                    itemID);

        }

        [HarmonyFinalizer]
        private static Exception Finalizer(
            Exception __exception)
        {
            InventoryStack.AddItemMergePrefixInProgress =
                false;

            InventoryStack.AddItemMergePrefixSkipsOriginal =
                false;

            return __exception;
        }
    }

    /// <summary>
    /// 빈 슬롯이 없어도 같은 자원 스택이 현재 최대 적재량 미만이면
    /// 월드 아이템을 수집할 수 있도록 합니다.
    /// LongE의 수집 시작 검사도 이 메서드를 사용합니다.
    /// </summary>
    [HarmonyPatch(
        typeof(global::Player),
        "HasEmptySlot")]
    internal static class
        InventoryPlayerHasEmptySlotPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(
            global::Player __instance,
            ushort itemID,
            ref bool __result)
        {
            if (!InventoryStack.IsGameplayActive() ||
                !InventoryStack
                    .IsStackableItemId(
                        itemID))
            {
                return true;
            }

            if (!InventoryStack.HasStackSpace(
                    __instance,
                    itemID))
            {
                return true;
            }

            __result =
                true;

            return false;
        }
    }

    /// <summary>
    /// AddItem 전후 일반 슬롯과 tempFullSlot 배치 결과를 기록합니다.
    /// 로직은 변경하지 않습니다.
    /// </summary>
    [HarmonyPatch(
        typeof(global::Player),
        "AddItem")]
    internal static class
        InventoryPlayerAddItemHandDiagnosticPatch
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        private static void Prefix(
            global::Player __instance,
            ushort itemID,
            object[] __args)
        {
            if (InventoryStack.Instance == null)
            {
                return;
            }

            InventoryStack.Instance.WriteHandDiagnostic(
                "PLAYER-ADD-ITEM-PREFIX",
                "ItemID=" +
                itemID +
                " | Args=" +
                InventoryStack.BuildDiagnosticArguments(
                    __args) +
                " | " +
                InventoryStack.BuildHandStateSummary(
                    __instance,
                    Character.localCharacter));
        }

        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(
            global::Player __instance,
            ushort itemID,
            ItemSlot __2,
            bool __result)
        {
            if (InventoryStack.Instance == null)
            {
                return;
            }

            InventoryStack.Instance.WriteHandDiagnostic(
                "PLAYER-ADD-ITEM-POSTFIX",
                "ItemID=" +
                itemID +
                " | Result=" +
                __result +
                " | ReturnedSlot=" +
                InventoryStack.BuildItemSlotDiagnostic(
                    __2) +
                " | " +
                InventoryStack.BuildHandStateSummary(
                    __instance,
                    Character.localCharacter));
        }
    }

    /// <summary>
    /// 1·2·3 슬롯 버튼 및 tempFullSlot 선택 전후 상태를 기록합니다.
    /// 선택 요청이 들어왔는데 currentSelectedSlot이 바뀌지 않는지 확인합니다.
    /// </summary>
    [HarmonyPatch(
        typeof(CharacterItems),
        "EquipSlot")]
    internal static class
        InventoryEquipSlotHandDiagnosticPatch
    {
        [HarmonyPrefix]
        private static void Prefix(
            CharacterItems __instance,
            object[] __args)
        {
            if (InventoryStack.Instance == null)
            {
                return;
            }

            InventoryStack.Instance.WriteHandDiagnostic(
                "EQUIP-SLOT-PREFIX",
                "Args=" +
                InventoryStack.BuildDiagnosticArguments(
                    __args) +
                " | " +
                InventoryStack.BuildHandStateSummary(
                    global::Player.localPlayer,
                    Character.localCharacter));
        }

        [HarmonyPostfix]
        private static void Postfix(
            CharacterItems __instance,
            object[] __args)
        {
            if (InventoryStack.Instance == null)
            {
                return;
            }

            InventoryStack.Instance.WriteHandDiagnostic(
                "EQUIP-SLOT-POSTFIX",
                "Args=" +
                InventoryStack.BuildDiagnosticArguments(
                    __args) +
                " | " +
                InventoryStack.BuildHandStateSummary(
                    global::Player.localPlayer,
                    Character.localCharacter));

            InventoryStack.Instance.StartCoroutine(
                DelayedSelectionSnapshot());
        }

        private static IEnumerator
            DelayedSelectionSnapshot()
        {
            yield return null;

            if (InventoryStack.Instance != null)
            {
                InventoryStack.Instance.WriteHandDiagnostic(
                    "EQUIP-SLOT-NEXT-FRAME",
                    InventoryStack.BuildHandStateSummary(
                        global::Player.localPlayer,
                        Character.localCharacter));
            }

            yield return
                new WaitForSecondsRealtime(
                    0.10f);

            if (InventoryStack.Instance != null)
            {
                InventoryStack.Instance.WriteHandDiagnostic(
                    "EQUIP-SLOT-PLUS-100MS",
                    InventoryStack.BuildHandStateSummary(
                        global::Player.localPlayer,
                        Character.localCharacter));
            }
        }

        [HarmonyFinalizer]
        private static Exception Finalizer(
            Exception __exception)
        {
            if (__exception != null &&
                InventoryStack.Instance != null)
            {
                InventoryStack.Instance.WriteHandDiagnostic(
                    "EQUIP-SLOT-EXCEPTION",
                    __exception.ToString() +
                    " | " +
                    InventoryStack.BuildHandStateSummary(
                        global::Player.localPlayer,
                        Character.localCharacter));
            }

            return __exception;
        }
    }

    /// <summary>
    /// 드롭 등 Player.EmptySlot 경로에서는 한 개만 감소시킵니다.
    ///
    /// 원격 플레이어가 DropItemRpc를 실행한 뒤 Master Client에
    /// RPCRemoveItemFromSlot을 한 번 더 보낼 수 있으므로,
    /// 해당 중복 제거 요청을 짧은 시간 동안 한 번 무시합니다.
    /// </summary>
    [HarmonyPatch(
        typeof(global::Player),
        "EmptySlot")]
    internal static class
        InventoryPlayerEmptySlotPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(
            global::Player __instance,
            Optionable<byte> slot)
        {
            if (InventoryStack.Instance != null)
            {
                InventoryStack.Instance.WriteHandDiagnostic(
                    "PLAYER-EMPTY-SLOT-PREFIX",
                    "Requested=" +
                    (
                        slot.IsSome
                            ? slot.Value.ToString()
                            : "None"
                    ) +
                    " | " +
                    InventoryStack.BuildHandStateSummary(
                        __instance,
                        Character.localCharacter));
            }

            if (InventoryStack.Instance == null ||
                !InventoryStack.IsGameplayActive() ||
                !PhotonNetwork.IsMasterClient ||
                slot.IsNone)
            {
                return true;
            }

            byte slotId =
                slot.Value;

            ItemSlot itemSlot =
                __instance.GetItemSlot(
                    slotId);

            if (itemSlot == null ||
                itemSlot.IsEmpty() ||
                itemSlot.prefab == null ||
                !InventoryStack
                    .IsStackableItemId(
                        itemSlot.prefab.itemID))
            {
                return true;
            }

            if (InventoryStack.Instance
                    .ConsumeWholeStackBackpackTransfer(
                        __instance,
                        slotId))
            {
                return true;
            }

            int count =
                InventoryStack.GetStackCount(
                    __instance,
                    slotId);

            if (count <= 1)
            {
                InventoryStack.Instance
                    .HostRemoveFinalStackEntry(
                        __instance,
                        slotId,
                        "Player.EmptySlot");

                return true;
            }

            bool consumed =
                InventoryStack.Instance
                    .HostConsumeOneFromSlot(
                        __instance,
                        slotId,
                        "Player.EmptySlot",
                        true);

            if (!consumed)
            {
                return true;
            }

            InventoryStack.Instance
                .MarkExpectedRemoteRemove(
                    __instance,
                    slotId);

            return false;
        }
    }

    /// <summary>
    /// Shop.cs가 사용하는 RPCRemoveItemFromSlot도
    /// 스택 전체가 아니라 한 개만 제거합니다.
    /// </summary>
    [HarmonyPatch(
        typeof(global::Player),
        "RPCRemoveItemFromSlot")]
    internal static class
        InventoryPlayerRpcRemoveItemPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(
            global::Player __instance,
            byte slotID)
        {
            if (InventoryStack.Instance != null)
            {
                InventoryStack.Instance.WriteHandDiagnostic(
                    "RPC-REMOVE-SLOT-PREFIX",
                    "Slot=" +
                    slotID +
                    " | " +
                    InventoryStack.BuildHandStateSummary(
                        __instance,
                        Character.localCharacter));
            }

            if (InventoryStack.Instance == null ||
                !InventoryStack.IsGameplayActive() ||
                !PhotonNetwork.IsMasterClient)
            {
                return true;
            }

            if (InventoryStack.Instance
                    .ConsumeExpectedRemoteRemove(
                        __instance,
                        slotID))
            {
                return false;
            }

            ItemSlot slot =
                __instance.GetItemSlot(
                    slotID);

            if (slot == null ||
                slot.IsEmpty() ||
                slot.prefab == null ||
                !InventoryStack
                    .IsStackableItemId(
                        slot.prefab.itemID))
            {
                return true;
            }

            if (InventoryStack.Instance
                    .ConsumeWholeStackBackpackTransfer(
                        __instance,
                        slotID))
            {
                return true;
            }

            int count =
                InventoryStack.GetStackCount(
                    __instance,
                    slotID);

            if (count <= 1)
            {
                InventoryStack.Instance
                    .HostRemoveFinalStackEntry(
                        __instance,
                        slotID,
                        "RPCRemoveItemFromSlot");

                return true;
            }

            bool consumed =
                InventoryStack.Instance
                    .HostConsumeOneFromSlot(
                        __instance,
                        slotID,
                        "RPCRemoveItemFromSlot",
                        true);

            return !consumed;
        }
    }

    /// <summary>
    /// Campfire.cs처럼 ItemSlot.EmptyOut을 직접 호출하는 코드도
    /// 자원 스택에서 한 개만 감소하도록 처리합니다.
    ///
    /// Player.EmptySlot과 RPCRemoveItemFromSlot에서 수량 2 이상인 경우
    /// 원본 메서드 자체를 건너뛰므로 이 패치와 중복 감소하지 않습니다.
    /// </summary>
    [HarmonyPatch(
        typeof(ItemSlot),
        "EmptyOut")]
    internal static class
        InventoryItemSlotEmptyOutPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(
            ItemSlot __instance)
        {
            if (InventoryStack.Instance == null ||
                !InventoryStack.IsGameplayActive() ||
                !PhotonNetwork.IsMasterClient ||
                __instance == null ||
                __instance.IsEmpty() ||
                __instance.prefab == null ||
                !InventoryStack
                    .IsStackableItemId(
                        __instance.prefab.itemID))
            {
                return true;
            }

            global::Player player;
            byte slotId;

            if (!TryResolve(
                    __instance,
                    out player,
                    out slotId))
            {
                return true;
            }

            int count =
                InventoryStack.GetStackCount(
                    player,
                    slotId);

            if (count <= 1)
            {
                InventoryStack.Instance
                    .HostRemoveFinalStackEntry(
                        player,
                        slotId,
                        "ItemSlot.EmptyOut");

                return true;
            }

            bool consumed =
                InventoryStack.Instance
                    .HostConsumeOneFromSlot(
                        player,
                        slotId,
                        "ItemSlot.EmptyOut",
                        true);

            return !consumed;
        }

        private static bool TryResolve(
            ItemSlot target,
            out global::Player player,
            out byte slotId)
        {
            player =
                null;

            slotId =
                0;

            foreach (global::Player candidate in
                     PlayerHandler.GetAllPlayers())
            {
                if (candidate == null)
                {
                    continue;
                }

                for (int i = 0;
                     i < candidate.itemSlots.Length;
                     i++)
                {
                    if (ReferenceEquals(
                            candidate.itemSlots[i],
                            target))
                    {
                        player =
                            candidate;

                        slotId =
                            candidate.itemSlots[i]
                                .itemSlotID;

                        return true;
                    }
                }

                if (ReferenceEquals(
                        candidate.tempFullSlot,
                        target))
                {
                    player =
                        candidate;

                    slotId =
                        candidate.tempFullSlot
                            .itemSlotID;

                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Q 입력으로 실행되는 CharacterItems.DropItemRpc 동안만
    /// 새로 생성되는 월드 아이템을 1개짜리 재획득 대상으로 표시합니다.
    /// 사망 시 전체 드롭인 DropItemFromSlotRPC에는 적용하지 않습니다.
    /// </summary>
    [HarmonyPatch(
        typeof(CharacterItems),
        "DropItemRpc",
        new Type[]
        {
            typeof(float),
            typeof(byte),
            typeof(Vector3),
            typeof(Vector3),
            typeof(Quaternion),
            typeof(ItemInstanceData),
            typeof(bool)
        })]
    internal static class
        InventoryCharacterItemsDropItemRpcPatch
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static void Prefix(
            object[] __args)
        {
            if (InventoryStack.Instance != null)
            {
                InventoryStack.Instance.WriteHandDiagnostic(
                    "DROP-ITEM-RPC-PREFIX",
                    "Args=" +
                    InventoryStack.BuildDiagnosticArguments(
                        __args) +
                    " | " +
                    InventoryStack.BuildHandStateSummary(
                        global::Player.localPlayer,
                        Character.localCharacter));
            }

            InventoryStack
                .BeginQDropSpawnCapture();
        }

        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void Postfix()
        {
            InventoryStack
                .EndQDropSpawnCapture();

            if (InventoryStack.Instance != null)
            {
                InventoryStack.Instance.WriteHandDiagnostic(
                    "DROP-ITEM-RPC-POSTFIX",
                    InventoryStack.BuildHandStateSummary(
                        global::Player.localPlayer,
                        Character.localCharacter));
            }
        }

        [HarmonyFinalizer]
        private static Exception Finalizer(
            Exception __exception)
        {
            InventoryStack
                .EndQDropSpawnCapture();

            if (__exception != null &&
                InventoryStack.Instance != null)
            {
                InventoryStack.Instance.WriteHandDiagnostic(
                    "DROP-ITEM-RPC-EXCEPTION",
                    __exception.ToString() +
                    " | " +
                    InventoryStack.BuildHandStateSummary(
                        global::Player.localPlayer,
                        Character.localCharacter));
            }

            return __exception;
        }
    }

    /// <summary>
    /// DropItemRpc 내부에서 생성된 실제 Photon 월드 아이템의 ViewID를 기록합니다.
    /// </summary>
    [HarmonyPatch(
        typeof(PhotonNetwork),
        "InstantiateItemRoom",
        new Type[]
        {
            typeof(string),
            typeof(Vector3),
            typeof(Quaternion)
        })]
    internal static class
        InventoryPhotonInstantiateDroppedItemPatch
    {
        [HarmonyPostfix]
        private static void Postfix(
            GameObject __result)
        {
            InventoryStack
                .RegisterQDroppedWorldItem(
                    __result);
        }
    }

    /// <summary>
    /// 기록된 Q 드롭 아이템을 줍는 동안에만 수집량 배율 차단 상태를 유지합니다.
    /// </summary>
    [HarmonyPatch(
        typeof(Item),
        "RequestPickup")]
    internal static class
        InventorySingleUnitDroppedPickupPatch
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static void Prefix(
            Item __instance)
        {
            InventoryStack
                .BeginSingleUnitPickup(
                    __instance);
        }

        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void Postfix()
        {
            InventoryStack
                .EndSingleUnitPickup();
        }

        [HarmonyFinalizer]
        private static Exception Finalizer(
            Exception __exception)
        {
            InventoryStack
                .EndSingleUnitPickup();

            return __exception;
        }
    }

    /// <summary>
    /// CraftHub의 자연 자원 수집 배율은 그대로 유지하되,
    /// Q로 버렸다가 다시 줍는 아이템에 대해서만 추가 지급을 차단합니다.
    /// </summary>
    [HarmonyPatch(
        typeof(CraftHub),
        "GrantPickupBonus")]
    internal static class
        InventoryQDropYieldBonusBlockPatch
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool Prefix()
        {
            return
                !InventoryStack
                    .ConsumeSingleUnitPickupBonusBlock();
        }
    }

    /// <summary>
    /// 손에 든 일반 인벤토리 스택을 바닥에 놓인 배낭으로 옮길 때
    /// 원본 슬롯의 전체 수량을 배낭 내부 한 칸으로 이전합니다.
    /// </summary>
    /// <summary>
    /// 배낭 슬롯에서 아이템을 꺼낼 때 원본 RequestPickup은 대표 아이템 1개만
    /// Player.AddItem으로 복원합니다. 배낭의 xN 수량을 회수된 인벤토리 슬롯에
    /// 그대로 이전합니다.
    /// </summary>
    [HarmonyPatch(
        typeof(Item),
        "RequestPickup")]
    internal static class
        InventoryBackpackWithdrawalFullStackPatch
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(
            Item __instance,
            PhotonView characterView,
            out InventoryStack.BackpackWithdrawalState __state)
        {
            __state =
                InventoryStack.Instance != null
                    ? InventoryStack.Instance
                        .CaptureBackpackWithdrawal(
                            __instance,
                            characterView)
                    : default(
                        InventoryStack.BackpackWithdrawalState);

            if (InventoryStack.Instance == null ||
                !__state.IsValid)
            {
                return true;
            }

            bool handledDirectly =
                InventoryStack.Instance
                    .TryHandleBackpackWithdrawalIntoExistingStack(
                        __instance,
                        characterView,
                        __state);

            if (!handledDirectly)
            {
                return true;
            }

            // 원본 RequestPickup을 건너뛰었으므로 Postfix의 일반 복원 경로가
            // 다시 실행되지 않도록 상태를 비웁니다.
            __state =
                default(
                    InventoryStack.BackpackWithdrawalState);

            return false;
        }

        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(
            PhotonView characterView,
            InventoryStack.BackpackWithdrawalState __state)
        {
            if (InventoryStack.Instance == null ||
                !__state.IsValid)
            {
                return;
            }

            InventoryStack.Instance
                .CompleteBackpackWithdrawal(
                    characterView,
                    __state);
        }
    }

    [HarmonyPatch(
        typeof(Backpack),
        "RPCAddItemToBackpack")]
    internal static class
        InventoryBackpackWholeStackTransferPatch
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static void Prefix(
            PhotonView playerView,
            byte slotID,
            byte backpackSlotID)
        {
            if (InventoryStack.Instance == null ||
                !PhotonNetwork.IsMasterClient ||
                playerView == null)
            {
                return;
            }

            global::Player player =
                playerView.GetComponent<
                    global::Player>();

            InventoryStack.Instance
                .HostPrepareWholeStackToBackpack(
                    player,
                    slotID,
                    backpackSlotID);
        }

        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(
            Backpack __instance,
            PhotonView playerView,
            byte slotID,
            byte backpackSlotID)
        {
            if (InventoryStack.Instance == null ||
                !PhotonNetwork.IsMasterClient ||
                playerView == null)
            {
                return;
            }

            global::Player player =
                playerView.GetComponent<
                    global::Player>();

            BackpackData backpackData =
                __instance.GetData<BackpackData>(
                    DataEntryKey.BackpackData);

            int confirmedCount =
                InventoryStack.Instance
                    .HostConfirmWholeStackInActualBackpackData(
                        player,
                        backpackData,
                        slotID,
                        backpackSlotID,
                        "Backpack.RPCAddItemToBackpack");

            if (confirmedCount > 0)
            {
                InventoryStack.Instance
                    .HostConsolidateMatchingItemsIntoBackpackSlot(
                        player,
                        backpackData,
                        slotID,
                        backpackSlotID,
                        "Backpack.RPCAddItemToBackpack");
            }

            BackpackVisuals visuals =
                __instance.GetComponent<
                    BackpackVisuals>();

            if (visuals != null)
            {
                visuals.RefreshVisuals();
            }
        }
    }

    /// <summary>
    /// 착용 중인 배낭으로 옮기는 경우에도 전체 스택을 한 칸에 보존합니다.
    /// </summary>
    [HarmonyPatch(
        typeof(CharacterBackpackHandler),
        "RPCAddItemToCharacterBackpack")]
    internal static class
        InventoryCharacterBackpackWholeStackTransferPatch
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static void Prefix(
            PhotonView playerView,
            byte inventorySlotID,
            byte backpackSlotID)
        {
            if (InventoryStack.Instance == null ||
                !PhotonNetwork.IsMasterClient ||
                playerView == null)
            {
                return;
            }

            global::Player player =
                playerView.GetComponent<
                    global::Player>();

            InventoryStack.Instance
                .HostPrepareWholeStackToBackpack(
                    player,
                    inventorySlotID,
                    backpackSlotID);
        }

        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(
            CharacterBackpackHandler __instance,
            PhotonView playerView,
            byte inventorySlotID,
            byte backpackSlotID)
        {
            if (InventoryStack.Instance == null ||
                !PhotonNetwork.IsMasterClient ||
                playerView == null)
            {
                return;
            }

            global::Player player =
                playerView.GetComponent<
                    global::Player>();

            BackpackData backpackData =
                default(BackpackData);

            bool hasBackpackData =
                player != null &&
                player.backpackSlot != null &&
                !player.backpackSlot.IsEmpty() &&
                player.backpackSlot.data != null &&
                player.backpackSlot.data
                    .TryGetDataEntry<
                        BackpackData>(
                        DataEntryKey.BackpackData,
                        out backpackData);

            int confirmedCount =
                hasBackpackData
                    ? InventoryStack.Instance
                        .HostConfirmWholeStackInActualBackpackData(
                            player,
                            backpackData,
                            inventorySlotID,
                            backpackSlotID,
                            "CharacterBackpackHandler.RPCAddItemToCharacterBackpack")
                    : 0;

            if (confirmedCount > 0)
            {
                InventoryStack.Instance
                    .HostConsolidateMatchingItemsIntoBackpackSlot(
                        player,
                        backpackData,
                        inventorySlotID,
                        backpackSlotID,
                        "CharacterBackpackHandler.RPCAddItemToCharacterBackpack");
            }

            if (__instance.backpackVisuals !=
                null)
            {
                __instance.backpackVisuals
                    .RefreshVisuals();
            }
        }
    }

    /// <summary>
    /// 배낭 원형 UI의 각 내부 슬롯에도 xN 수량을 표시합니다.
    /// </summary>
    [HarmonyPatch(
        typeof(BackpackWheel),
        "InitWheel")]
    internal static class
        InventoryBackpackWheelQuantityPatch
    {
        private const string QuantityName =
            "CraftPeak_BackpackStackQuantity";

        [HarmonyPostfix]
        private static void Postfix(
            BackpackWheel __instance,
            BackpackReference bp)
        {
            if (__instance == null ||
                __instance.slices == null)
            {
                return;
            }

            BackpackData data =
                bp.GetData();

            global::Player player =
                global::Player.localPlayer;

            if (data == null ||
                data.itemSlots == null ||
                player == null)
            {
                return;
            }

            for (byte i = 0;
                 i < data.itemSlots.Length &&
                 i + 1 < __instance.slices.Length;
                 i++)
            {
                BackpackWheelSlice slice =
                    __instance.slices[i + 1];

                if (slice == null)
                {
                    continue;
                }

                Transform existing =
                    slice.transform.Find(
                        QuantityName);

                TextMeshProUGUI quantity =
                    existing != null
                        ? existing.GetComponent<
                            TextMeshProUGUI>()
                        : null;

                if (quantity == null)
                {
                    GameObject quantityObject =
                        new GameObject(
                            QuantityName,
                            typeof(RectTransform),
                            typeof(CanvasRenderer),
                            typeof(TextMeshProUGUI));

                    quantityObject.transform.SetParent(
                        slice.transform,
                        false);

                    RectTransform rect =
                        quantityObject.GetComponent<
                            RectTransform>();

                    rect.anchorMin =
                        new Vector2(
                            1f,
                            0f);

                    rect.anchorMax =
                        new Vector2(
                            1f,
                            0f);

                    rect.pivot =
                        new Vector2(
                            1f,
                            0f);

                    rect.anchoredPosition =
                        new Vector2(
                            -5f,
                            5f);

                    rect.sizeDelta =
                        new Vector2(
                            80f,
                            36f);

                    quantity =
                        quantityObject.GetComponent<
                            TextMeshProUGUI>();

                    quantity.fontSize =
                        24f;

                    quantity.fontStyle =
                        FontStyles.Bold;

                    quantity.alignment =
                        TextAlignmentOptions.BottomRight;

                    quantity.color =
                        Color.white;

                    quantity.raycastTarget =
                        false;

                    quantity.outlineWidth =
                        0.2f;

                    quantity.outlineColor =
                        Color.black;
                }

                ItemSlot slot =
                    data.itemSlots[i];

                int count =
                    InventoryStack.GetBackpackStackCount(
                        player,
                        i);

                bool visible =
                    slot != null &&
                    !slot.IsEmpty() &&
                    slot.prefab != null &&
                    count > 1;

                quantity.text =
                    visible
                        ? "x" +
                          count
                        : string.Empty;

                quantity.gameObject.SetActive(
                    visible);
            }
        }
    }

    /// <summary>
    /// 인벤토리 슬롯 아이콘 우측 아래에 x2~x10 수량을 표시합니다.
    /// 기존 PEAK UI 프리팹을 수정하지 않고 런타임에 TMP 텍스트를 붙입니다.
    /// </summary>
    [HarmonyPatch(
        typeof(InventoryItemUI),
        "SetItem")]
    internal static class
        InventoryItemUiSetItemPatch
    {
        private const string QuantityObjectName =
            "CraftPeak_StackQuantity";

        [HarmonyPostfix]
        private static void Postfix(
            InventoryItemUI __instance,
            ItemSlot slot)
        {
            if (__instance == null)
            {
                return;
            }

            TextMeshProUGUI quantityText =
                GetOrCreateQuantityText(
                    __instance);

            if (quantityText == null)
            {
                return;
            }

            if (slot == null ||
                slot.IsEmpty() ||
                slot.prefab == null ||
                !InventoryStack
                    .IsStackableItemId(
                        slot.prefab.itemID))
            {
                quantityText.gameObject
                    .SetActive(
                        false);

                return;
            }

            int count =
                InventoryStack.GetStackCount(
                    slot);

            if (count <= 1)
            {
                quantityText.gameObject
                    .SetActive(
                        false);

                return;
            }

            quantityText.text =
                "x" +
                count;

            quantityText.gameObject
                .SetActive(
                    true);
        }

        private static TextMeshProUGUI
            GetOrCreateQuantityText(
                InventoryItemUI inventoryUi)
        {
            Transform existing =
                inventoryUi.transform.Find(
                    QuantityObjectName);

            if (existing != null)
            {
                return existing.GetComponent<
                    TextMeshProUGUI>();
            }

            GameObject quantityObject =
                new GameObject(
                    QuantityObjectName,
                    typeof(RectTransform),
                    typeof(CanvasRenderer),
                    typeof(TextMeshProUGUI));

            quantityObject.transform.SetParent(
                inventoryUi.transform,
                false);

            RectTransform rectTransform =
                quantityObject.GetComponent<
                    RectTransform>();

            rectTransform.anchorMin =
                new Vector2(
                    1f,
                    0f);

            rectTransform.anchorMax =
                new Vector2(
                    1f,
                    0f);

            rectTransform.pivot =
                new Vector2(
                    1f,
                    0f);

            rectTransform.anchoredPosition =
                new Vector2(
                    -5f,
                    5f);

            rectTransform.sizeDelta =
                new Vector2(
                    90f,
                    42f);

            TextMeshProUGUI text =
                quantityObject.GetComponent<
                    TextMeshProUGUI>();

            if (inventoryUi.nameText != null)
            {
                text.font =
                    inventoryUi.nameText.font;
            }

            text.fontSize =
                27f;

            text.fontStyle =
                FontStyles.Bold;

            text.alignment =
                TextAlignmentOptions.BottomRight;

            text.color =
                Color.white;

            text.enableWordWrapping =
                false;

            text.raycastTarget =
                false;

            text.outlineWidth =
                0.22f;

            text.outlineColor =
                new Color(
                    0f,
                    0f,
                    0f,
                    0.95f);

            quantityObject.SetActive(
                false);

            return text;
        }
    }

    [HarmonyPatch(
        typeof(InventoryItemUI),
        "Clear")]
    internal static class
        InventoryItemUiClearPatch
    {
        [HarmonyPostfix]
        private static void Postfix(
            InventoryItemUI __instance)
        {
            if (__instance == null)
            {
                return;
            }

            Transform quantity =
                __instance.transform.Find(
                    "CraftPeak_StackQuantity");

            if (quantity != null)
            {
                quantity.gameObject
                    .SetActive(
                        false);
            }
        }
    }

    /// <summary>
    /// 원본 인벤토리 동기화가 적용된 직후 스택 수량 UI를 다시 갱신합니다.
    /// 실제 수량은 별도의 Photon 이벤트가 최종 확정합니다.
    /// </summary>
    [HarmonyPatch(
        typeof(global::Player),
        "SyncInventoryRPC")]
    internal static class
        InventoryPlayerSyncInventoryPatch
    {
        [HarmonyPostfix]
        private static void Postfix(
            global::Player __instance)
        {
            if (InventoryStack.Instance != null)
            {
                InventoryStack.Instance.WriteHandDiagnostic(
                    "SYNC-INVENTORY-POSTFIX",
                    InventoryStack.BuildHandStateSummary(
                        __instance,
                        Character.localCharacter));
            }

            if (__instance == null ||
                __instance.itemsChangedAction ==
                    null)
            {
                return;
            }

            __instance.itemsChangedAction(
                __instance.itemSlots);
        }
    }
}
