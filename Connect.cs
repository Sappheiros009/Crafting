// CRAFT PEAK HOST MIGRATION CONNECT BUILD 1.0.0
//
// 목적
// - 기존 호스트가 게임 도중 나가도 Photon이 지정한 다음 Master Client가
//   Craft PEAK 모드의 진행 상태를 이어서 관리합니다.
// - RunManager, Spawn 설정/업그레이드, 자원 슬롯/재생성 타이머,
//   Campfire 조건, Inventory 최대 스택, 공유 잔액 상태를 연결합니다.
//
// 핵심 방식
// 1. RunManager 상태
//    - CraftRunManager의 Photon Room Custom Properties를 그대로 승계합니다.
//
// 2. 호스트 설정
//    - Spawn 확률/아이템 비중/업그레이드 단계
//    - Campfire 재료와 집결 조건
//    - Inventory 최대 적재량
//    위 값을 작은 Photon Room Custom Properties로 지속 보존합니다.
//    새 호스트는 이전 호스트 값을 자신의 ConfigEntry에 적용합니다.
//
// 3. 자원 슬롯과 재생성 시간
//    - 모든 클라이언트가 지상에 생성된 고정 자원 위치를 로컬 미러에 기록합니다.
//    - 기존 호스트는 자원이 수집될 때 슬롯 위치와 PhotonNetwork.Time 기반
//      5분 재생성 시각을 모든 클라이언트에 전송합니다.
//    - 새 호스트는 자신의 로컬 미러와 다른 클라이언트의 보조 스냅샷을 합칩니다.
//    - 이전 호스트의 Spawn.cs 루프가 사라진 뒤에는 Connect.cs가 만료된 슬롯을 재생성합니다.
//
// 4. Inventory 스택
//    - Inventory.cs가 StackCount 이벤트를 ReceiverGroup.All로 보내므로
//      모든 클라이언트가 이미 같은 스택 미러를 갖습니다.
//    - 새 호스트의 Inventory.OnMasterClientSwitched가 그 미러를 이어서 사용합니다.
//
// 5. 공유 돈
//    - Open.cs가 Photon Room Custom Property에 공유 잔액을 저장하므로
//      방이 유지되는 동안 자동 승계됩니다.
//
// 제한
// - 호스트가 아이템 생성 직후 모든 클라이언트가 해당 슬롯을 보기 전에
//   즉시 나가는 극단적인 경우에는 아직 학습되지 않은 슬롯 하나가 누락될 수 있습니다.
// - 정상 플레이 중에는 각 클라이언트가 지속적으로 슬롯을 복제하므로
//   다음 호스트와 그 다음 호스트까지 반복 승계할 수 있습니다.
//
// 중요
// - 리플렉션을 사용하지 않습니다.
// - Harmony 패치를 사용하지 않습니다.
// - Delete.cs, Spawn.cs, LongE.cs, Open.cs, Campfire.cs,
//   Inventory.cs, RunManager.cs, ResourceStreaming.cs와 같은 DLL에 포함합니다.

using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using ExitGames.Client.Photon;
using Photon.Pun;
using Photon.Realtime;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CraftPeak
{
    [BepInPlugin(
        PluginGuid,
        PluginName,
        PluginVersion)]
    [BepInDependency(
        Spawn.PluginGuid,
        BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency(
        CampfireGate.PluginGuid,
        BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency(
        InventoryStack.PluginGuid,
        BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency(
        Shop.PluginGuid,
        BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency(
        CraftRunManager.PluginGuid,
        BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency(
        ResourceStreaming.PluginGuid,
        BepInDependency.DependencyFlags.HardDependency)]
    public sealed class Connect :
        BaseUnityPlugin,
        IOnEventCallback,
        IInRoomCallbacks
    {
        public const string PluginGuid =
            "com.sappheiros.crafting.connect";

        public const string PluginName =
            "Craft PEAK Host Migration Connect";

        public const string PluginVersion =
            "1.0.1";

        public const int ProtocolVersion = 1;

        private const double ResourceRespawnDelaySeconds =
            300d;

        private const float RoomPollIntervalSeconds =
            0.50f;

        private const float ConfigPublishPollSeconds =
            1f;

        private const float ResourceDiscoveryIntervalSeconds =
            0.25f;

        private const float MissingItemConfirmationSeconds =
            0.75f;

        private const float TakeoverSnapshotWaitSeconds =
            2.50f;

        private const float TakeoverPhotonSettleSeconds =
            0.75f;

        private const int RecoveryRespawnsPerFrame =
            10;

        private const int SnapshotRecordsPerChunk =
            20;

        private const float ResourcePositionQuantization =
            0.25f;

        private const byte ResourceDeltaEventCode = 218;
        private const byte ResourceSnapshotRequestEventCode = 219;
        private const byte ResourceSnapshotChunkEventCode = 220;
        private const byte TakeoverCompletedEventCode = 221;

        private const int ResourceSnapshotRecordFieldCount = 12;

        private const string PropertyProtocol =
            "CraftPeak.Connect.Protocol";

        private const string PropertyRevision =
            "CraftPeak.Connect.Revision";

        private const string PropertyOwnerActor =
            "CraftPeak.Connect.OwnerActor";

        private const string PropertyHostEpoch =
            "CraftPeak.Connect.HostEpoch";

        private const string PropertyRunId =
            "CraftPeak.Connect.RunId";

        private const string PropertySpawnGrade =
            "CraftPeak.Connect.SpawnGrade";

        private const string PropertySpawnRarityWeights =
            "CraftPeak.Connect.SpawnRarityWeights";

        private const string PropertySpawnItemWeights =
            "CraftPeak.Connect.SpawnItemWeights";

        private const string PropertyCampfireMaterials =
            "CraftPeak.Connect.CampfireMaterials";

        private const string PropertyCampfireRequireEveryone =
            "CraftPeak.Connect.CampfireRequireEveryone";

        private const string PropertyCampfireDistances =
            "CraftPeak.Connect.CampfireDistances";

        private const string PropertyInventoryMaximum =
            "CraftPeak.Connect.InventoryMaximum";

        private const int ExpectedDefaultResourceSlots = 3000;

        private static readonly ushort[] ResourceItemIds =
        {
            28,
            72,
            69,
            14,
            13,
            15,
            99,
            34,
            49,
            51,
            112
        };

        private static readonly int[] ResourceItemRarities =
        {
            0,
            0,
            0,
            1,
            1,
            1,
            1,
            2,
            2,
            3,
            4
        };

        // 행별 활성 등급만 삼각형 형태로 저장합니다.
        // Common: 1개
        // Normal: 2개
        // Rare: 3개
        // Unique: 4개
        // Legendary: 5개
        private static readonly float[]
            DefaultFlattenedRarityWeights =
            {
                100f,

                80f,
                20f,

                65f,
                25f,
                10f,

                55f,
                25f,
                15f,
                5f,

                50f,
                25f,
                15f,
                8f,
                2f
            };

        private static readonly float[]
            DefaultItemWeights =
            {
                1f,
                1f,
                1f,
                1f,
                1f,
                1f,
                1f,
                1f,
                1f,
                1f,
                1f
            };

        private static readonly ConfigDefinition[]
            SpawnRarityDefinitions =
            {
                new ConfigDefinition(
                    "01. Common 업그레이드 확률",
                    "Common 등급 비중"),

                new ConfigDefinition(
                    "02. Normal 업그레이드 확률",
                    "Common 등급 비중"),

                new ConfigDefinition(
                    "02. Normal 업그레이드 확률",
                    "Normal 등급 비중"),

                new ConfigDefinition(
                    "03. Rare 업그레이드 확률",
                    "Common 등급 비중"),

                new ConfigDefinition(
                    "03. Rare 업그레이드 확률",
                    "Normal 등급 비중"),

                new ConfigDefinition(
                    "03. Rare 업그레이드 확률",
                    "Rare 등급 비중"),

                new ConfigDefinition(
                    "04. Unique 업그레이드 확률",
                    "Common 등급 비중"),

                new ConfigDefinition(
                    "04. Unique 업그레이드 확률",
                    "Normal 등급 비중"),

                new ConfigDefinition(
                    "04. Unique 업그레이드 확률",
                    "Rare 등급 비중"),

                new ConfigDefinition(
                    "04. Unique 업그레이드 확률",
                    "Unique 등급 비중"),

                new ConfigDefinition(
                    "05. Legendary 업그레이드 확률",
                    "Common 등급 비중"),

                new ConfigDefinition(
                    "05. Legendary 업그레이드 확률",
                    "Normal 등급 비중"),

                new ConfigDefinition(
                    "05. Legendary 업그레이드 확률",
                    "Rare 등급 비중"),

                new ConfigDefinition(
                    "05. Legendary 업그레이드 확률",
                    "Unique 등급 비중"),

                new ConfigDefinition(
                    "05. Legendary 업그레이드 확률",
                    "Legendary 등급 비중")
            };

        private static readonly ConfigDefinition[]
            SpawnItemDefinitions =
            {
                new ConfigDefinition(
                    "06. 등급 내부 아이템 비중",
                    "Common - 나뭇가지"),

                new ConfigDefinition(
                    "06. 등급 내부 아이템 비중",
                    "Common - 돌"),

                new ConfigDefinition(
                    "06. 등급 내부 아이템 비중",
                    "Common - 소라고동"),

                new ConfigDefinition(
                    "06. 등급 내부 아이템 비중",
                    "Normal - 망원경"),

                new ConfigDefinition(
                    "06. 등급 내부 아이템 비중",
                    "Normal - 빙봉"),

                new ConfigDefinition(
                    "06. 등급 내부 아이템 비중",
                    "Normal - 나팔"),

                new ConfigDefinition(
                    "06. 등급 내부 아이템 비중",
                    "Normal - 플라잉 디스크"),

                new ConfigDefinition(
                    "06. 등급 내부 아이템 비중",
                    "Rare - 가이드북"),

                new ConfigDefinition(
                    "06. 등급 내부 아이템 비중",
                    "Rare - 스크롤"),

                new ConfigDefinition(
                    "06. 등급 내부 아이템 비중",
                    "Unique - 괴상 버섯"),

                new ConfigDefinition(
                    "06. 등급 내부 아이템 비중",
                    "Legendary - 이상한 보석")
            };

        private static readonly ConfigDefinition
            CampfireWoodDefinition =
                new ConfigDefinition(
                    "01. 캠프파이어 재료 조건",
                    "나뭇가지 요구 수량");

        private static readonly ConfigDefinition
            CampfireStoneDefinition =
                new ConfigDefinition(
                    "01. 캠프파이어 재료 조건",
                    "돌 요구 수량");

        private static readonly ConfigDefinition
            CampfireTorchDefinition =
                new ConfigDefinition(
                    "01. 캠프파이어 재료 조건",
                    "횃불 요구 수량");

        private static readonly ConfigDefinition
            CampfireRequireEveryoneDefinition =
                new ConfigDefinition(
                    "02. 캠프파이어 집결 조건",
                    "모든 생존 플레이어 집결 필요");

        private static readonly ConfigDefinition
            CampfireEveryoneDistanceDefinition =
                new ConfigDefinition(
                    "02. 캠프파이어 집결 조건",
                    "집결 판정 거리");

        private static readonly ConfigDefinition
            CampfireRequesterDistanceDefinition =
                new ConfigDefinition(
                    "02. 캠프파이어 집결 조건",
                    "점화 요청 허용 거리");

        private static readonly ConfigDefinition
            InventoryMaximumDefinition =
                new ConfigDefinition(
                    "01. 인벤토리 적재 설정",
                    "슬롯당 최대 적재 수량");

        private readonly Dictionary<ResourceSlotKey, ResourceSlotMirror>
            resourceSlots =
                new Dictionary<ResourceSlotKey, ResourceSlotMirror>();

        private readonly Dictionary<int, ResourceSlotMirror>
            resourceSlotsByViewId =
                new Dictionary<int, ResourceSlotMirror>();

        private readonly List<ResourceSlotMirror>
            resourceSlotList =
                new List<ResourceSlotMirror>();

        private Room observedRoom;

        private HostConfigurationSnapshot
            preservedConfiguration =
                HostConfigurationSnapshot.CreateDefault();

        private float nextRoomPollAt;
        private float nextConfigPublishAt;
        private float nextResourceDiscoveryAt;

        private int configurationRevision;
        private int hostEpoch;
        private int resourceRevision;

        private int recoveryCursor;

        private bool recoveryAuthorityActive;
        private bool takeoverInProgress;
        private bool roomConfigurationLoaded;
        private bool sceneReady;

        private string activeRunId =
            string.Empty;

        private int activeSnapshotRequestId;
        private int expectedSnapshotChunks;
        private int receivedSnapshotChunks;

        private Coroutine takeoverRoutine;

        internal static Connect Instance
        {
            get;
            private set;
        }

        internal static ManualLogSource ModLogger
        {
            get;
            private set;
        }

        public static bool RecoveryAuthorityActive
        {
            get
            {
                return
                    Instance != null &&
                    Instance.recoveryAuthorityActive;
            }
        }

        public static bool TakeoverInProgress
        {
            get
            {
                return
                    Instance != null &&
                    Instance.takeoverInProgress;
            }
        }

        public static int MirroredResourceSlotCount
        {
            get
            {
                return
                    Instance != null
                        ? Instance.resourceSlotList.Count
                        : 0;
            }
        }

        private struct ResourceSlotKey :
            IEquatable<ResourceSlotKey>
        {
            public int X;
            public int Y;
            public int Z;

            public static ResourceSlotKey FromPosition(
                Vector3 position)
            {
                return
                    new ResourceSlotKey
                    {
                        X =
                            Mathf.RoundToInt(
                                position.x /
                                ResourcePositionQuantization),

                        Y =
                            Mathf.RoundToInt(
                                position.y /
                                ResourcePositionQuantization),

                        Z =
                            Mathf.RoundToInt(
                                position.z /
                                ResourcePositionQuantization)
                    };
            }

            public bool Equals(
                ResourceSlotKey other)
            {
                return
                    X == other.X &&
                    Y == other.Y &&
                    Z == other.Z;
            }

            public override bool Equals(
                object obj)
            {
                return
                    obj is ResourceSlotKey &&
                    Equals(
                        (ResourceSlotKey)obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = 17;

                    hash =
                        hash * 31 +
                        X;

                    hash =
                        hash * 31 +
                        Y;

                    hash =
                        hash * 31 +
                        Z;

                    return hash;
                }
            }

            public override string ToString()
            {
                return
                    X +
                    ":" +
                    Y +
                    ":" +
                    Z;
            }
        }

        private sealed class ResourceSlotMirror
        {
            public ResourceSlotKey Key;

            public Vector3 Position;
            public Quaternion Rotation;

            public ushort LastItemId;

            public bool Occupied;
            public double RespawnAtNetworkTime;

            public int Revision;
            public double UpdatedAtNetworkTime;

            public Item CurrentItem;
            public int CurrentViewId;

            public float MissingObservedAt = -1f;

            public Action<ItemState>
                StateChangedHandler;
        }

        private sealed class HostConfigurationSnapshot
        {
            public int Protocol;
            public int Revision;
            public int OwnerActor;
            public int HostEpoch;

            public string RunId;

            public int SpawnGrade;
            public float[] SpawnRarityWeights;
            public float[] SpawnItemWeights;

            public int[] CampfireMaterials;
            public bool CampfireRequireEveryone;
            public float[] CampfireDistances;

            public int InventoryMaximum;

            public static HostConfigurationSnapshot
                CreateDefault()
            {
                return
                    new HostConfigurationSnapshot
                    {
                        Protocol =
                            ProtocolVersion,

                        Revision =
                            0,

                        OwnerActor =
                            0,

                        HostEpoch =
                            0,

                        RunId =
                            string.Empty,

                        SpawnGrade =
                            0,

                        SpawnRarityWeights =
                            CloneFloatArray(
                                DefaultFlattenedRarityWeights),

                        SpawnItemWeights =
                            CloneFloatArray(
                                DefaultItemWeights),

                        CampfireMaterials =
                            new[]
                            {
                                1,
                                1,
                                1
                            },

                        CampfireRequireEveryone =
                            true,

                        CampfireDistances =
                            new[]
                            {
                                15f,
                                4f
                            },

                        InventoryMaximum =
                            10
                    };
            }

            public HostConfigurationSnapshot Clone()
            {
                return
                    new HostConfigurationSnapshot
                    {
                        Protocol =
                            Protocol,

                        Revision =
                            Revision,

                        OwnerActor =
                            OwnerActor,

                        HostEpoch =
                            HostEpoch,

                        RunId =
                            RunId ??
                            string.Empty,

                        SpawnGrade =
                            SpawnGrade,

                        SpawnRarityWeights =
                            CloneFloatArray(
                                SpawnRarityWeights),

                        SpawnItemWeights =
                            CloneFloatArray(
                                SpawnItemWeights),

                        CampfireMaterials =
                            CloneIntArray(
                                CampfireMaterials),

                        CampfireRequireEveryone =
                            CampfireRequireEveryone,

                        CampfireDistances =
                            CloneFloatArray(
                                CampfireDistances),

                        InventoryMaximum =
                            InventoryMaximum
                    };
            }

            public bool ContentEquals(
                HostConfigurationSnapshot other)
            {
                if (other == null)
                {
                    return false;
                }

                return
                    SpawnGrade ==
                        other.SpawnGrade &&
                    FloatArraysEqual(
                        SpawnRarityWeights,
                        other.SpawnRarityWeights) &&
                    FloatArraysEqual(
                        SpawnItemWeights,
                        other.SpawnItemWeights) &&
                    IntArraysEqual(
                        CampfireMaterials,
                        other.CampfireMaterials) &&
                    CampfireRequireEveryone ==
                        other.CampfireRequireEveryone &&
                    FloatArraysEqual(
                        CampfireDistances,
                        other.CampfireDistances) &&
                    InventoryMaximum ==
                        other.InventoryMaximum &&
                    string.Equals(
                        RunId,
                        other.RunId,
                        StringComparison.Ordinal);
            }
        }

        private void Awake()
        {
            Instance =
                this;

            ModLogger =
                Logger;

            SceneManager.sceneLoaded +=
                HandleSceneLoaded;

            SceneManager.sceneUnloaded +=
                HandleSceneUnloaded;

            CraftRunManager.RunStarted +=
                HandleRunStarted;

            CraftRunManager.RunFinished +=
                HandleRunFinished;

            CraftRunManager.SnapshotChanged +=
                HandleRunSnapshotChanged;

            Scene activeScene =
                SceneManager.GetActiveScene();

            if (activeScene.IsValid() &&
                activeScene.isLoaded)
            {
                HandleSceneLoaded(
                    activeScene,
                    LoadSceneMode.Single);
            }

            Logger.LogInfo(
                PluginName +
                " " +
                PluginVersion +
                " loaded. Protocol=" +
                ProtocolVersion +
                ".");
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
            SceneManager.sceneLoaded -=
                HandleSceneLoaded;

            SceneManager.sceneUnloaded -=
                HandleSceneUnloaded;

            CraftRunManager.RunStarted -=
                HandleRunStarted;

            CraftRunManager.RunFinished -=
                HandleRunFinished;

            CraftRunManager.SnapshotChanged -=
                HandleRunSnapshotChanged;

            StopTakeoverRoutine();

            ClearResourceMirror(
                true);

            if (Instance == this)
            {
                Instance = null;
            }

            ModLogger =
                null;
        }

        private void Update()
        {
            if (Time.unscaledTime >=
                nextRoomPollAt)
            {
                nextRoomPollAt =
                    Time.unscaledTime +
                    RoomPollIntervalSeconds;

                DetectRoomChange();
            }

            if (!PhotonNetwork.InRoom ||
                PhotonNetwork.CurrentRoom == null)
            {
                return;
            }

            if (Time.unscaledTime >=
                nextResourceDiscoveryAt)
            {
                nextResourceDiscoveryAt =
                    Time.unscaledTime +
                    ResourceDiscoveryIntervalSeconds;

                DiscoverAndReconcileResources();
            }

            ConfirmMissingItems();

            if (PhotonNetwork.IsMasterClient &&
                Time.unscaledTime >=
                    nextConfigPublishAt)
            {
                nextConfigPublishAt =
                    Time.unscaledTime +
                    ConfigPublishPollSeconds;

                PublishConfigurationIfChanged();
            }

            if (recoveryAuthorityActive &&
                PhotonNetwork.IsMasterClient &&
                !takeoverInProgress)
            {
                ProcessRecoveryRespawns();
            }
        }

        private void DetectRoomChange()
        {
            Room currentRoom =
                PhotonNetwork.InRoom
                    ? PhotonNetwork.CurrentRoom
                    : null;

            if (ReferenceEquals(
                    observedRoom,
                    currentRoom))
            {
                return;
            }

            observedRoom =
                currentRoom;

            roomConfigurationLoaded =
                false;

            recoveryAuthorityActive =
                false;

            takeoverInProgress =
                false;

            StopTakeoverRoutine();

            if (currentRoom == null)
            {
                Logger.LogInfo(
                    "Connect left Photon room. Local resource mirror is retained until the scene changes.");

                return;
            }

            Logger.LogInfo(
                "Connect entered Photon room. Room=" +
                currentRoom.Name +
                " | LocalActor=" +
                GetLocalActorNumber() +
                " | IsMaster=" +
                PhotonNetwork.IsMasterClient +
                ".");

            ReadPreservedConfigurationFromRoom();

            if (!PhotonNetwork.IsMasterClient)
            {
                return;
            }

            if (!roomConfigurationLoaded)
            {
                hostEpoch =
                    1;

                configurationRevision =
                    0;

                preservedConfiguration =
                    CaptureLocalAuthoritativeConfiguration();

                PublishConfiguration(
                    preservedConfiguration,
                    "Initial host configuration");
            }
            else if (preservedConfiguration.OwnerActor !=
                     GetLocalActorNumber())
            {
                BeginHostTakeover(
                    "Entered room as a replacement master");
            }
        }

        private void HandleSceneLoaded(
            Scene scene,
            LoadSceneMode loadSceneMode)
        {
            StopTakeoverRoutine();

            recoveryAuthorityActive =
                false;

            takeoverInProgress =
                false;

            sceneReady =
                !IsExcludedScene(
                    scene);

            activeRunId =
                CraftRunManager.CurrentRunId;

            ClearResourceMirror(
                true);

            resourceRevision =
                0;

            recoveryCursor =
                0;

            nextResourceDiscoveryAt =
                0f;

            if (!sceneReady)
            {
                Logger.LogInfo(
                    "Connect resource mirroring disabled in scene: " +
                    scene.name);

                return;
            }

            Logger.LogInfo(
                "Connect resource mirroring started. Scene=" +
                scene.name +
                " | RunId=" +
                (
                    string.IsNullOrEmpty(
                        activeRunId)
                        ? "<pending>"
                        : activeRunId
                ) +
                ".");
        }

        private void HandleSceneUnloaded(
            Scene scene)
        {
            if (!sceneReady)
            {
                return;
            }

            ClearResourceMirror(
                true);

            sceneReady =
                false;
        }

        private void HandleRunStarted(
            CraftRunSnapshot snapshot)
        {
            string newRunId =
                snapshot != null
                    ? snapshot.RunId
                    : string.Empty;

            if (string.Equals(
                    activeRunId,
                    newRunId,
                    StringComparison.Ordinal))
            {
                return;
            }

            activeRunId =
                newRunId;

            recoveryAuthorityActive =
                false;

            takeoverInProgress =
                false;

            StopTakeoverRoutine();

            ClearResourceMirror(
                true);

            resourceRevision =
                0;

            nextResourceDiscoveryAt =
                0f;

            Logger.LogInfo(
                "Connect started a new run mirror. RunId=" +
                (
                    string.IsNullOrEmpty(
                        activeRunId)
                        ? "<none>"
                        : activeRunId
                ) +
                ".");
        }

        private void HandleRunFinished(
            CraftRunSnapshot snapshot)
        {
            recoveryAuthorityActive =
                false;

            takeoverInProgress =
                false;

            StopTakeoverRoutine();

            Logger.LogInfo(
                "Connect recovery authority stopped because the run finished.");
        }

        private void HandleRunSnapshotChanged(
            CraftRunSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return;
            }

            activeRunId =
                snapshot.RunId ??
                string.Empty;
        }

        // -----------------------------------------------------------------
        // Photon callbacks
        // -----------------------------------------------------------------

        public void OnEvent(
            EventData photonEvent)
        {
            if (photonEvent == null)
            {
                return;
            }

            if (photonEvent.Code ==
                ResourceDeltaEventCode)
            {
                ApplyResourceDelta(
                    photonEvent.CustomData as
                        object[]);

                return;
            }

            if (photonEvent.Code ==
                ResourceSnapshotRequestEventCode)
            {
                HandleResourceSnapshotRequest(
                    photonEvent.Sender,
                    photonEvent.CustomData as
                        object[]);

                return;
            }

            if (photonEvent.Code ==
                ResourceSnapshotChunkEventCode)
            {
                ApplyResourceSnapshotChunk(
                    photonEvent.Sender,
                    photonEvent.CustomData as
                        object[]);

                return;
            }

            if (photonEvent.Code ==
                TakeoverCompletedEventCode)
            {
                HandleTakeoverCompleted(
                    photonEvent.CustomData as
                        object[]);
            }
        }

        public void OnPlayerEnteredRoom(
            Photon.Realtime.Player newPlayer)
        {
            if (!PhotonNetwork.IsMasterClient ||
                newPlayer == null)
            {
                return;
            }

            StartCoroutine(
                SendSnapshotAfterDelay(
                    newPlayer.ActorNumber,
                    1f,
                    0));
        }

        public void OnPlayerLeftRoom(
            Photon.Realtime.Player otherPlayer)
        {
            if (otherPlayer == null)
            {
                return;
            }

            Logger.LogInfo(
                "Connect observed player leave. Actor=" +
                otherPlayer.ActorNumber +
                " | Name=" +
                otherPlayer.NickName +
                ".");
        }

        public void OnRoomPropertiesUpdate(
            ExitGames.Client.Photon.Hashtable
                propertiesThatChanged)
        {
            if (propertiesThatChanged == null ||
                !ContainsConnectProperty(
                    propertiesThatChanged))
            {
                return;
            }

            ReadPreservedConfigurationFromRoom();
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
            if (newMasterClient == null)
            {
                return;
            }

            Logger.LogWarning(
                "Connect detected Master Client switch. " +
                "NewHostActor=" +
                newMasterClient.ActorNumber +
                " | NewHostName=" +
                newMasterClient.NickName +
                ".");

            ReadPreservedConfigurationFromRoom();

            if (PhotonNetwork.LocalPlayer == null ||
                newMasterClient.ActorNumber !=
                    PhotonNetwork.LocalPlayer.ActorNumber)
            {
                recoveryAuthorityActive =
                    false;

                takeoverInProgress =
                    false;

                return;
            }

            BeginHostTakeover(
                "Photon OnMasterClientSwitched");
        }

        // -----------------------------------------------------------------
        // Host configuration preservation
        // -----------------------------------------------------------------

        private void PublishConfigurationIfChanged()
        {
            if (!PhotonNetwork.IsMasterClient ||
                PhotonNetwork.CurrentRoom == null)
            {
                return;
            }

            HostConfigurationSnapshot current =
                CaptureLocalAuthoritativeConfiguration();

            current.HostEpoch =
                Mathf.Max(
                    1,
                    hostEpoch);

            current.OwnerActor =
                GetLocalActorNumber();

            current.RunId =
                CraftRunManager.CurrentRunId;

            if (roomConfigurationLoaded &&
                preservedConfiguration
                    .ContentEquals(
                        current) &&
                preservedConfiguration
                    .OwnerActor ==
                    current.OwnerActor &&
                preservedConfiguration
                    .HostEpoch ==
                    current.HostEpoch)
            {
                return;
            }

            PublishConfiguration(
                current,
                "Host configuration changed");
        }

        private void PublishConfiguration(
            HostConfigurationSnapshot snapshot,
            string reason)
        {
            if (!PhotonNetwork.InRoom ||
                !PhotonNetwork.IsMasterClient ||
                PhotonNetwork.CurrentRoom == null ||
                snapshot == null)
            {
                return;
            }

            HostConfigurationSnapshot safe =
                NormalizeConfigurationSnapshot(
                    snapshot.Clone());

            safe.Protocol =
                ProtocolVersion;

            safe.Revision =
                Mathf.Max(
                    configurationRevision,
                    safe.Revision) +
                1;

            safe.OwnerActor =
                GetLocalActorNumber();

            safe.HostEpoch =
                Mathf.Max(
                    1,
                    hostEpoch);

            safe.RunId =
                CraftRunManager.CurrentRunId;

            ExitGames.Client.Photon.Hashtable
                properties =
                    new ExitGames.Client.Photon.Hashtable
                    {
                        {
                            PropertyProtocol,
                            safe.Protocol
                        },
                        {
                            PropertyRevision,
                            safe.Revision
                        },
                        {
                            PropertyOwnerActor,
                            safe.OwnerActor
                        },
                        {
                            PropertyHostEpoch,
                            safe.HostEpoch
                        },
                        {
                            PropertyRunId,
                            safe.RunId ??
                            string.Empty
                        },
                        {
                            PropertySpawnGrade,
                            safe.SpawnGrade
                        },
                        {
                            PropertySpawnRarityWeights,
                            CloneFloatArray(
                                safe.SpawnRarityWeights)
                        },
                        {
                            PropertySpawnItemWeights,
                            CloneFloatArray(
                                safe.SpawnItemWeights)
                        },
                        {
                            PropertyCampfireMaterials,
                            CloneIntArray(
                                safe.CampfireMaterials)
                        },
                        {
                            PropertyCampfireRequireEveryone,
                            safe.CampfireRequireEveryone
                        },
                        {
                            PropertyCampfireDistances,
                            CloneFloatArray(
                                safe.CampfireDistances)
                        },
                        {
                            PropertyInventoryMaximum,
                            safe.InventoryMaximum
                        }
                    };

            bool queued =
                PhotonNetwork.CurrentRoom
                    .SetCustomProperties(
                        properties);

            if (!queued)
            {
                Logger.LogError(
                    "Failed to publish Connect configuration. Reason=" +
                    reason);

                return;
            }

            configurationRevision =
                safe.Revision;

            preservedConfiguration =
                safe;

            roomConfigurationLoaded =
                true;

            Logger.LogInfo(
                "Connect configuration published. " +
                "Reason=" +
                reason +
                " | Revision=" +
                safe.Revision +
                " | HostEpoch=" +
                safe.HostEpoch +
                " | OwnerActor=" +
                safe.OwnerActor +
                " | SpawnGrade=" +
                safe.SpawnGrade +
                " | InventoryMax=" +
                safe.InventoryMaximum +
                " | Campfire=[" +
                safe.CampfireMaterials[0] +
                ", " +
                safe.CampfireMaterials[1] +
                ", " +
                safe.CampfireMaterials[2] +
                "].");
        }

        private bool ReadPreservedConfigurationFromRoom()
        {
            if (!PhotonNetwork.InRoom ||
                PhotonNetwork.CurrentRoom == null)
            {
                return false;
            }

            ExitGames.Client.Photon.Hashtable
                properties =
                    PhotonNetwork.CurrentRoom
                        .CustomProperties;

            object protocolValue;
            object revisionValue;

            if (!properties.TryGetValue(
                    PropertyProtocol,
                    out protocolValue) ||
                !properties.TryGetValue(
                    PropertyRevision,
                    out revisionValue))
            {
                return false;
            }

            try
            {
                int protocol =
                    Convert.ToInt32(
                        protocolValue);

                if (protocol !=
                    ProtocolVersion)
                {
                    Logger.LogError(
                        "Connect protocol mismatch. Room=" +
                        protocol +
                        " | Local=" +
                        ProtocolVersion +
                        ".");

                    return false;
                }

                HostConfigurationSnapshot snapshot =
                    HostConfigurationSnapshot
                        .CreateDefault();

                snapshot.Protocol =
                    protocol;

                snapshot.Revision =
                    Convert.ToInt32(
                        revisionValue);

                snapshot.OwnerActor =
                    ReadIntProperty(
                        properties,
                        PropertyOwnerActor,
                        0);

                snapshot.HostEpoch =
                    ReadIntProperty(
                        properties,
                        PropertyHostEpoch,
                        0);

                snapshot.RunId =
                    ReadStringProperty(
                        properties,
                        PropertyRunId);

                snapshot.SpawnGrade =
                    Mathf.Clamp(
                        ReadIntProperty(
                            properties,
                            PropertySpawnGrade,
                            0),
                        0,
                        4);

                snapshot.SpawnRarityWeights =
                    ReadFloatArrayProperty(
                        properties,
                        PropertySpawnRarityWeights,
                        DefaultFlattenedRarityWeights);

                snapshot.SpawnItemWeights =
                    ReadFloatArrayProperty(
                        properties,
                        PropertySpawnItemWeights,
                        DefaultItemWeights);

                snapshot.CampfireMaterials =
                    ReadIntArrayProperty(
                        properties,
                        PropertyCampfireMaterials,
                        new[]
                        {
                            1,
                            1,
                            1
                        });

                snapshot.CampfireRequireEveryone =
                    ReadBoolProperty(
                        properties,
                        PropertyCampfireRequireEveryone,
                        true);

                snapshot.CampfireDistances =
                    ReadFloatArrayProperty(
                        properties,
                        PropertyCampfireDistances,
                        new[]
                        {
                            15f,
                            4f
                        });

                snapshot.InventoryMaximum =
                    Mathf.Clamp(
                        ReadIntProperty(
                            properties,
                            PropertyInventoryMaximum,
                            10),
                        1,
                        100);

                if (snapshot.Revision <
                    configurationRevision)
                {
                    return false;
                }

                snapshot =
                    NormalizeConfigurationSnapshot(
                        snapshot);

                preservedConfiguration =
                    snapshot;

                configurationRevision =
                    snapshot.Revision;

                hostEpoch =
                    Mathf.Max(
                        hostEpoch,
                        snapshot.HostEpoch);

                roomConfigurationLoaded =
                    true;

                return true;
            }
            catch (Exception exception)
            {
                Logger.LogError(
                    "Failed to read Connect configuration from room properties. Error=" +
                    exception);

                return false;
            }
        }

        private HostConfigurationSnapshot
            CaptureLocalAuthoritativeConfiguration()
        {
            HostConfigurationSnapshot snapshot =
                HostConfigurationSnapshot
                    .CreateDefault();

            snapshot.Protocol =
                ProtocolVersion;

            snapshot.Revision =
                configurationRevision;

            snapshot.OwnerActor =
                GetLocalActorNumber();

            snapshot.HostEpoch =
                Mathf.Max(
                    1,
                    hostEpoch);

            snapshot.RunId =
                CraftRunManager.CurrentRunId;

            snapshot.SpawnGrade =
                Mathf.Clamp(
                    (int)Spawn.CurrentUpgradeGrade,
                    0,
                    4);

            snapshot.SpawnRarityWeights =
                ReadConfigFloatArray(
                    Spawn.Instance != null
                        ? Spawn.Instance.Config
                        : null,
                    SpawnRarityDefinitions,
                    DefaultFlattenedRarityWeights);

            snapshot.SpawnItemWeights =
                ReadConfigFloatArray(
                    Spawn.Instance != null
                        ? Spawn.Instance.Config
                        : null,
                    SpawnItemDefinitions,
                    DefaultItemWeights);

            snapshot.CampfireMaterials =
                new[]
                {
                    Mathf.Max(
                        0,
                        CampfireGate.RequiredFireWoodCount),

                    Mathf.Max(
                        0,
                        CampfireGate.RequiredStoneCount),

                    Mathf.Max(
                        0,
                        CampfireGate.RequiredTorchCount)
                };

            snapshot.CampfireRequireEveryone =
                CampfireGate.RequireEveryoneInRange;

            snapshot.CampfireDistances =
                new[]
                {
                    Mathf.Max(
                        1f,
                        CampfireGate.EveryoneInRangeDistance),

                    Mathf.Max(
                        1f,
                        CampfireGate.MaximumRequesterDistance)
                };

            snapshot.InventoryMaximum =
                Mathf.Clamp(
                    InventoryStack.MaximumStackCount,
                    1,
                    100);

            return
                NormalizeConfigurationSnapshot(
                    snapshot);
        }

        private void ApplyPreservedConfigurationToNewHost()
        {
            if (!PhotonNetwork.IsMasterClient ||
                preservedConfiguration == null)
            {
                return;
            }

            HostConfigurationSnapshot snapshot =
                NormalizeConfigurationSnapshot(
                    preservedConfiguration.Clone());

            ApplyConfigFloatArray(
                Spawn.Instance != null
                    ? Spawn.Instance.Config
                    : null,
                SpawnRarityDefinitions,
                snapshot.SpawnRarityWeights);

            ApplyConfigFloatArray(
                Spawn.Instance != null
                    ? Spawn.Instance.Config
                    : null,
                SpawnItemDefinitions,
                snapshot.SpawnItemWeights);

            Spawn.SetUpgradeGrade(
                snapshot.SpawnGrade);

            ConfigFile campfireConfig =
                CampfireGate.Instance != null
                    ? CampfireGate.Instance.Config
                    : null;

            SetConfigEntryValue(
                campfireConfig,
                CampfireWoodDefinition,
                snapshot.CampfireMaterials.Length > 0
                    ? snapshot.CampfireMaterials[0]
                    : 1);

            SetConfigEntryValue(
                campfireConfig,
                CampfireStoneDefinition,
                snapshot.CampfireMaterials.Length > 1
                    ? snapshot.CampfireMaterials[1]
                    : 1);

            SetConfigEntryValue(
                campfireConfig,
                CampfireTorchDefinition,
                snapshot.CampfireMaterials.Length > 2
                    ? snapshot.CampfireMaterials[2]
                    : 1);

            SetConfigEntryValue(
                campfireConfig,
                CampfireRequireEveryoneDefinition,
                snapshot.CampfireRequireEveryone);

            SetConfigEntryValue(
                campfireConfig,
                CampfireEveryoneDistanceDefinition,
                snapshot.CampfireDistances.Length > 0
                    ? snapshot.CampfireDistances[0]
                    : 15f);

            SetConfigEntryValue(
                campfireConfig,
                CampfireRequesterDistanceDefinition,
                snapshot.CampfireDistances.Length > 1
                    ? snapshot.CampfireDistances[1]
                    : 4f);

            ConfigFile inventoryConfig =
                InventoryStack.Instance != null
                    ? InventoryStack.Instance.Config
                    : null;

            SetConfigEntryValue(
                inventoryConfig,
                InventoryMaximumDefinition,
                snapshot.InventoryMaximum);

            Logger.LogInfo(
                "Previous host configuration applied to new host. " +
                "SpawnGrade=" +
                snapshot.SpawnGrade +
                " | InventoryMax=" +
                snapshot.InventoryMaximum +
                " | Campfire=[" +
                snapshot.CampfireMaterials[0] +
                ", " +
                snapshot.CampfireMaterials[1] +
                ", " +
                snapshot.CampfireMaterials[2] +
                "].");
        }

        // -----------------------------------------------------------------
        // Host takeover
        // -----------------------------------------------------------------

        private void BeginHostTakeover(
            string reason)
        {
            if (!PhotonNetwork.IsMasterClient ||
                takeoverInProgress)
            {
                return;
            }

            StopTakeoverRoutine();

            takeoverRoutine =
                StartCoroutine(
                    HostTakeoverRoutine(
                        reason));
        }

        private IEnumerator HostTakeoverRoutine(
            string reason)
        {
            takeoverInProgress =
                true;

            recoveryAuthorityActive =
                false;

            Logger.LogWarning(
                "Connect host takeover started. Reason=" +
                reason +
                " | LocalMirroredSlots=" +
                resourceSlotList.Count +
                ".");

            ReadPreservedConfigurationFromRoom();

            ApplyPreservedConfigurationToNewHost();

            yield return
                new WaitForSecondsRealtime(
                    TakeoverPhotonSettleSeconds);

            int donorActor =
                SelectSnapshotDonorActor();

            activeSnapshotRequestId =
                UnityEngine.Random.Range(
                    1,
                    int.MaxValue);

            expectedSnapshotChunks =
                0;

            receivedSnapshotChunks =
                0;

            if (donorActor > 0)
            {
                RequestResourceSnapshotFromActor(
                    donorActor,
                    activeSnapshotRequestId);

                float waitUntil =
                    Time.unscaledTime +
                    TakeoverSnapshotWaitSeconds;

                while (Time.unscaledTime <
                       waitUntil)
                {
                    yield return null;
                }
            }

            DiscoverAndReconcileResources();

            ReconcileMissingSlotsAfterTakeover();

            hostEpoch =
                Mathf.Max(
                    hostEpoch,
                    preservedConfiguration.HostEpoch) +
                1;

            preservedConfiguration.OwnerActor =
                GetLocalActorNumber();

            preservedConfiguration.HostEpoch =
                hostEpoch;

            recoveryAuthorityActive =
                true;

            takeoverInProgress =
                false;

            CraftRunManager
                .TryReassertCurrentSnapshotAsHost(
                    "Connect host takeover completed");

            ResourceStreaming.ForceRefresh();

            PublishConfiguration(
                CaptureLocalAuthoritativeConfiguration(),
                "New host takeover completed");

            BroadcastTakeoverCompleted();

            Logger.LogWarning(
                "Connect host takeover completed. " +
                "MirroredSlots=" +
                resourceSlotList.Count +
                " | SnapshotChunks=" +
                receivedSnapshotChunks +
                "/" +
                expectedSnapshotChunks +
                " | RecoveryAuthority=True.");

            takeoverRoutine =
                null;
        }

        private void StopTakeoverRoutine()
        {
            if (takeoverRoutine != null)
            {
                StopCoroutine(
                    takeoverRoutine);

                takeoverRoutine =
                    null;
            }
        }

        private int SelectSnapshotDonorActor()
        {
            if (!PhotonNetwork.InRoom ||
                PhotonNetwork.PlayerList == null)
            {
                return 0;
            }

            int localActor =
                GetLocalActorNumber();

            int selectedActor =
                int.MaxValue;

            Photon.Realtime.Player[] players =
                PhotonNetwork.PlayerList;

            for (int i = 0;
                 i < players.Length;
                 i++)
            {
                Photon.Realtime.Player player =
                    players[i];

                if (player == null ||
                    player.ActorNumber ==
                        localActor ||
                    player.IsInactive)
                {
                    continue;
                }

                if (player.ActorNumber <
                    selectedActor)
                {
                    selectedActor =
                        player.ActorNumber;
                }
            }

            return
                selectedActor ==
                    int.MaxValue
                    ? 0
                    : selectedActor;
        }

        private void RequestResourceSnapshotFromActor(
            int actorNumber,
            int requestId)
        {
            object[] payload =
            {
                requestId,
                GetLocalActorNumber(),
                CraftRunManager.CurrentRunId
            };

            RaiseEventOptions options =
                new RaiseEventOptions
                {
                    TargetActors =
                        new[]
                        {
                            actorNumber
                        }
                };

            PhotonNetwork.RaiseEvent(
                ResourceSnapshotRequestEventCode,
                payload,
                options,
                SendOptions.SendReliable);

            Logger.LogInfo(
                "Requested resource mirror snapshot. " +
                "DonorActor=" +
                actorNumber +
                " | RequestId=" +
                requestId +
                ".");
        }

        private void BroadcastTakeoverCompleted()
        {
            object[] payload =
            {
                GetLocalActorNumber(),
                hostEpoch,
                resourceSlotList.Count,
                CraftRunManager.CurrentRunId
            };

            RaiseEventOptions options =
                new RaiseEventOptions
                {
                    Receivers =
                        ReceiverGroup.All
                };

            PhotonNetwork.RaiseEvent(
                TakeoverCompletedEventCode,
                payload,
                options,
                SendOptions.SendReliable);
        }

        private void HandleTakeoverCompleted(
            object[] payload)
        {
            if (payload == null ||
                payload.Length < 4)
            {
                return;
            }

            try
            {
                int ownerActor =
                    Convert.ToInt32(
                        payload[0]);

                int incomingEpoch =
                    Convert.ToInt32(
                        payload[1]);

                int slotCount =
                    Convert.ToInt32(
                        payload[2]);

                string runId =
                    payload[3] as string ??
                    string.Empty;

                if (!IsRunIdCompatible(
                        runId))
                {
                    return;
                }

                hostEpoch =
                    Mathf.Max(
                        hostEpoch,
                        incomingEpoch);

                Logger.LogInfo(
                    "Connect takeover completion received. " +
                    "OwnerActor=" +
                    ownerActor +
                    " | HostEpoch=" +
                    incomingEpoch +
                    " | SlotCount=" +
                    slotCount +
                    ".");
            }
            catch (Exception)
            {
            }
        }

        // -----------------------------------------------------------------
        // Resource mirror
        // -----------------------------------------------------------------

        private void DiscoverAndReconcileResources()
        {
            if (!sceneReady ||
                Item.ALL_ITEMS == null)
            {
                return;
            }

            List<Item> items =
                new List<Item>(
                    Item.ALL_ITEMS);

            for (int i = 0;
                 i < items.Count;
                 i++)
            {
                Item item =
                    items[i];

                if (!IsFixedResourceCandidate(
                        item))
                {
                    continue;
                }

                BindOrCreateResourceSlot(
                    item);
            }
        }

        private void BindOrCreateResourceSlot(
            Item item)
        {
            if (item == null)
            {
                return;
            }

            Vector3 position =
                item.transform.position;

            ResourceSlotKey key =
                ResourceSlotKey.FromPosition(
                    position);

            ResourceSlotMirror slot;
            bool createdNewSlot = false;

            if (!resourceSlots.TryGetValue(
                    key,
                    out slot))
            {
                createdNewSlot =
                    true;

                slot =
                    new ResourceSlotMirror
                    {
                        Key =
                            key,

                        Position =
                            position,

                        Rotation =
                            item.transform.rotation,

                        LastItemId =
                            item.itemID,

                        Occupied =
                            true,

                        RespawnAtNetworkTime =
                            0d,

                        Revision =
                            0,

                        UpdatedAtNetworkTime =
                            PhotonNetwork.InRoom
                                ? PhotonNetwork.Time
                                : 0d
                    };

                resourceSlots.Add(
                    key,
                    slot);

                resourceSlotList.Add(
                    slot);

                if (resourceSlotList.Count ==
                    ExpectedDefaultResourceSlots)
                {
                    Logger.LogInfo(
                        "Connect learned the expected 3000 resource slots.");
                }
            }

            bool shouldBroadcastOccupiedDelta =
                PhotonNetwork.IsMasterClient &&
                !createdNewSlot &&
                !slot.Occupied;

            BindItemToSlot(
                slot,
                item,
                shouldBroadcastOccupiedDelta);
        }

        private void BindItemToSlot(
            ResourceSlotMirror slot,
            Item item,
            bool broadcastIfHost)
        {
            if (slot == null ||
                item == null)
            {
                return;
            }

            PhotonView photonView =
                item.GetComponent<PhotonView>();

            int viewId =
                photonView != null
                    ? photonView.ViewID
                    : 0;

            if (slot.CurrentItem ==
                    item &&
                slot.Occupied)
            {
                slot.MissingObservedAt =
                    -1f;

                return;
            }

            DetachSlotItemHandler(
                slot);

            if (slot.CurrentViewId > 0)
            {
                resourceSlotsByViewId.Remove(
                    slot.CurrentViewId);
            }

            slot.CurrentItem =
                item;

            slot.CurrentViewId =
                viewId;

            slot.Position =
                item.transform.position;

            slot.Rotation =
                item.transform.rotation;

            slot.LastItemId =
                item.itemID;

            slot.Occupied =
                true;

            slot.RespawnAtNetworkTime =
                0d;

            slot.MissingObservedAt =
                -1f;

            if (viewId > 0)
            {
                resourceSlotsByViewId[
                    viewId] =
                        slot;
            }

            Item capturedItem =
                item;

            ResourceSlotMirror capturedSlot =
                slot;

            slot.StateChangedHandler =
                delegate (
                    ItemState state)
                {
                    if (state !=
                        ItemState.Ground)
                    {
                        HandleSlotItemRemoved(
                            capturedSlot,
                            capturedItem,
                            "OnStateChange=" +
                            state);
                    }
                };

            item.OnStateChange +=
                slot.StateChangedHandler;

            if (broadcastIfHost &&
                PhotonNetwork.IsMasterClient)
            {
                MarkSlotAuthoritative(
                    slot,
                    true,
                    0d,
                    "Resource item bound",
                    true);
            }
        }

        private void HandleSlotItemRemoved(
            ResourceSlotMirror slot,
            Item expectedItem,
            string reason)
        {
            if (slot == null ||
                slot.CurrentItem !=
                    expectedItem)
            {
                return;
            }

            double respawnAt =
                PhotonNetwork.InRoom
                    ? PhotonNetwork.Time +
                      ResourceRespawnDelaySeconds
                    : ResourceRespawnDelaySeconds;

            DetachSlotItemHandler(
                slot);

            if (slot.CurrentViewId > 0)
            {
                resourceSlotsByViewId.Remove(
                    slot.CurrentViewId);
            }

            slot.CurrentItem =
                null;

            slot.CurrentViewId =
                0;

            slot.Occupied =
                false;

            slot.RespawnAtNetworkTime =
                respawnAt;

            slot.MissingObservedAt =
                -1f;

            if (PhotonNetwork.IsMasterClient)
            {
                MarkSlotAuthoritative(
                    slot,
                    false,
                    respawnAt,
                    reason,
                    true);
            }
            else
            {
                slot.UpdatedAtNetworkTime =
                    PhotonNetwork.InRoom
                        ? PhotonNetwork.Time
                        : 0d;
            }
        }

        private void ConfirmMissingItems()
        {
            for (int i = 0;
                 i < resourceSlotList.Count;
                 i++)
            {
                ResourceSlotMirror slot =
                    resourceSlotList[i];

                if (slot == null ||
                    !slot.Occupied)
                {
                    continue;
                }

                Item item =
                    slot.CurrentItem;

                bool missing =
                    item == null ||
                    item.gameObject == null;

                if (!missing &&
                    item.itemState !=
                        ItemState.Ground)
                {
                    HandleSlotItemRemoved(
                        slot,
                        item,
                        "Periodic state=" +
                        item.itemState);

                    continue;
                }

                if (!missing)
                {
                    slot.MissingObservedAt =
                        -1f;

                    continue;
                }

                if (slot.MissingObservedAt <
                    0f)
                {
                    slot.MissingObservedAt =
                        Time.unscaledTime;

                    continue;
                }

                if (Time.unscaledTime -
                    slot.MissingObservedAt <
                    MissingItemConfirmationSeconds)
                {
                    continue;
                }

                double respawnAt =
                    PhotonNetwork.InRoom
                        ? PhotonNetwork.Time +
                          ResourceRespawnDelaySeconds
                        : ResourceRespawnDelaySeconds;

                DetachSlotItemHandler(
                    slot);

                if (slot.CurrentViewId > 0)
                {
                    resourceSlotsByViewId.Remove(
                        slot.CurrentViewId);
                }

                slot.CurrentItem =
                    null;

                slot.CurrentViewId =
                    0;

                slot.Occupied =
                    false;

                slot.RespawnAtNetworkTime =
                    respawnAt;

                slot.MissingObservedAt =
                    -1f;

                if (PhotonNetwork.IsMasterClient)
                {
                    MarkSlotAuthoritative(
                        slot,
                        false,
                        respawnAt,
                        "Item destroyed or missing",
                        true);
                }
            }
        }

        private void MarkSlotAuthoritative(
            ResourceSlotMirror slot,
            bool occupied,
            double respawnAt,
            string reason,
            bool broadcast)
        {
            if (slot == null ||
                !PhotonNetwork.IsMasterClient)
            {
                return;
            }

            resourceRevision++;

            slot.Occupied =
                occupied;

            slot.RespawnAtNetworkTime =
                occupied
                    ? 0d
                    : Math.Max(
                        PhotonNetwork.Time,
                        respawnAt);

            slot.Revision =
                resourceRevision;

            slot.UpdatedAtNetworkTime =
                PhotonNetwork.Time;

            if (broadcast)
            {
                BroadcastResourceDelta(
                    slot);
            }

            Logger.LogDebug(
                "Resource slot authoritative update. " +
                "Key=" +
                slot.Key +
                " | Occupied=" +
                slot.Occupied +
                " | ItemID=" +
                slot.LastItemId +
                " | RespawnAt=" +
                slot.RespawnAtNetworkTime.ToString("0.00") +
                " | Revision=" +
                slot.Revision +
                " | Reason=" +
                reason);
        }

        private void BroadcastResourceDelta(
            ResourceSlotMirror slot)
        {
            if (!PhotonNetwork.IsMasterClient ||
                slot == null)
            {
                return;
            }

            object[] payload =
                BuildResourceRecordPayload(
                    slot,
                    true);

            RaiseEventOptions options =
                new RaiseEventOptions
                {
                    Receivers =
                        ReceiverGroup.All
                };

            PhotonNetwork.RaiseEvent(
                ResourceDeltaEventCode,
                payload,
                options,
                SendOptions.SendReliable);
        }

        private void ApplyResourceDelta(
            object[] payload)
        {
            if (payload == null ||
                payload.Length <
                    ResourceSnapshotRecordFieldCount +
                    1)
            {
                return;
            }

            string runId =
                payload[0] as string ??
                string.Empty;

            if (!IsRunIdCompatible(
                    runId))
            {
                return;
            }

            ApplyResourceRecordPayload(
                payload,
                1);
        }

        private void ProcessRecoveryRespawns()
        {
            if (resourceSlotList.Count == 0)
            {
                return;
            }

            int processed = 0;
            int checkedCount = 0;

            while (processed <
                       RecoveryRespawnsPerFrame &&
                   checkedCount <
                       resourceSlotList.Count)
            {
                if (recoveryCursor >=
                    resourceSlotList.Count)
                {
                    recoveryCursor =
                        0;
                }

                ResourceSlotMirror slot =
                    resourceSlotList[
                        recoveryCursor];

                recoveryCursor++;
                checkedCount++;

                if (slot == null ||
                    slot.Occupied ||
                    slot.RespawnAtNetworkTime <=
                        0d ||
                    PhotonNetwork.Time <
                        slot.RespawnAtNetworkTime)
                {
                    continue;
                }

                bool spawned =
                    SpawnRecoveryItem(
                        slot);

                if (spawned)
                {
                    processed++;
                }
                else
                {
                    slot.RespawnAtNetworkTime =
                        PhotonNetwork.Time +
                        10d;

                    MarkSlotAuthoritative(
                        slot,
                        false,
                        slot.RespawnAtNetworkTime,
                        "Recovery spawn failed; retry scheduled",
                        true);
                }
            }
        }

        private bool SpawnRecoveryItem(
            ResourceSlotMirror slot)
        {
            if (slot == null ||
                !PhotonNetwork.InRoom ||
                !PhotonNetwork.IsMasterClient)
            {
                return false;
            }

            ushort itemId =
                RollRecoveryItemId();

            Item prefab;

            if (!ItemDatabase.TryGetItem(
                    itemId,
                    out prefab) ||
                prefab == null ||
                prefab.gameObject == null)
            {
                Logger.LogError(
                    "Recovery spawn prefab lookup failed. ItemID=" +
                    itemId +
                    " | Slot=" +
                    slot.Key);

                return false;
            }

            GameObject spawnedObject;

            try
            {
                spawnedObject =
                    PhotonNetwork
                        .InstantiateItemRoom(
                            prefab.gameObject.name,
                            slot.Position,
                            slot.Rotation);
            }
            catch (Exception exception)
            {
                Logger.LogError(
                    "Recovery InstantiateItemRoom failed. " +
                    "Slot=" +
                    slot.Key +
                    " | ItemID=" +
                    itemId +
                    " | Error=" +
                    exception);

                return false;
            }

            if (spawnedObject == null)
            {
                return false;
            }

            Item item =
                spawnedObject.GetComponent<Item>();

            PhotonView view =
                spawnedObject.GetComponent<PhotonView>();

            if (item == null ||
                view == null)
            {
                Logger.LogError(
                    "Recovery spawned object is missing Item or PhotonView. Object=" +
                    spawnedObject.name);

                return false;
            }

            item.SetKinematicNetworked(
                true,
                slot.Position,
                slot.Rotation);

            slot.LastItemId =
                itemId;

            BindItemToSlot(
                slot,
                item,
                false);

            MarkSlotAuthoritative(
                slot,
                true,
                0d,
                "Recovery respawn",
                true);

            Logger.LogInfo(
                "Recovered resource slot respawned. " +
                "Slot=" +
                slot.Key +
                " | ItemID=" +
                itemId +
                " | ViewID=" +
                view.ViewID +
                " | HostEpoch=" +
                hostEpoch +
                ".");

            return true;
        }

        private void ReconcileMissingSlotsAfterTakeover()
        {
            double now =
                PhotonNetwork.Time;

            int staleOccupied = 0;

            for (int i = 0;
                 i < resourceSlotList.Count;
                 i++)
            {
                ResourceSlotMirror slot =
                    resourceSlotList[i];

                if (slot == null ||
                    !slot.Occupied)
                {
                    continue;
                }

                if (slot.CurrentItem != null &&
                    slot.CurrentItem.itemState ==
                        ItemState.Ground)
                {
                    continue;
                }

                slot.Occupied =
                    false;

                slot.CurrentItem =
                    null;

                slot.CurrentViewId =
                    0;

                slot.RespawnAtNetworkTime =
                    slot.RespawnAtNetworkTime >
                        now
                        ? slot.RespawnAtNetworkTime
                        : now +
                          ResourceRespawnDelaySeconds;

                slot.UpdatedAtNetworkTime =
                    now;

                staleOccupied++;
            }

            Logger.LogInfo(
                "Connect takeover resource reconciliation. " +
                "Slots=" +
                resourceSlotList.Count +
                " | StaleOccupiedConvertedToTimer=" +
                staleOccupied +
                ".");
        }

        private void DetachSlotItemHandler(
            ResourceSlotMirror slot)
        {
            if (slot == null)
            {
                return;
            }

            if (slot.CurrentItem != null &&
                slot.StateChangedHandler != null)
            {
                slot.CurrentItem.OnStateChange -=
                    slot.StateChangedHandler;
            }

            slot.StateChangedHandler =
                null;
        }

        private void ClearResourceMirror(
            bool detachHandlers)
        {
            if (detachHandlers)
            {
                for (int i = 0;
                     i < resourceSlotList.Count;
                     i++)
                {
                    DetachSlotItemHandler(
                        resourceSlotList[i]);
                }
            }

            resourceSlots.Clear();
            resourceSlotsByViewId.Clear();
            resourceSlotList.Clear();

            recoveryCursor =
                0;
        }

        private static bool IsFixedResourceCandidate(
            Item item)
        {
            if (item == null ||
                item.gameObject == null ||
                !item.gameObject.activeInHierarchy ||
                item.itemState !=
                    ItemState.Ground ||
                !Spawn.IsSaleResourceId(
                    item.itemID))
            {
                return false;
            }

            PhotonView view =
                item.GetComponent<PhotonView>();

            if (view == null ||
                view.ViewID <= 0)
            {
                return false;
            }

            Rigidbody rigidbody =
                item.GetComponent<Rigidbody>();

            if (rigidbody == null)
            {
                rigidbody =
                    item.GetComponentInChildren<
                        Rigidbody>();
            }

            if (rigidbody != null &&
                !rigidbody.isKinematic)
            {
                return false;
            }

            Vector3 position =
                item.transform.position;

            return
                IsFinite(
                    position.x) &&
                IsFinite(
                    position.y) &&
                IsFinite(
                    position.z);
        }

        // -----------------------------------------------------------------
        // Resource snapshot transfer
        // -----------------------------------------------------------------

        private void HandleResourceSnapshotRequest(
            int senderActor,
            object[] payload)
        {
            if (payload == null ||
                payload.Length < 3)
            {
                return;
            }

            try
            {
                int requestId =
                    Convert.ToInt32(
                        payload[0]);

                int targetActor =
                    Convert.ToInt32(
                        payload[1]);

                string runId =
                    payload[2] as string ??
                    string.Empty;

                if (targetActor <= 0 ||
                    senderActor !=
                        targetActor ||
                    !IsRunIdCompatible(
                        runId))
                {
                    return;
                }

                StartCoroutine(
                    SendResourceSnapshotChunks(
                        targetActor,
                        requestId));
            }
            catch (Exception)
            {
            }
        }

        private IEnumerator SendSnapshotAfterDelay(
            int targetActor,
            float delaySeconds,
            int requestId)
        {
            yield return
                new WaitForSecondsRealtime(
                    delaySeconds);

            if (!PhotonNetwork.InRoom ||
                !PhotonNetwork.IsMasterClient)
            {
                yield break;
            }

            int effectiveRequestId =
                requestId > 0
                    ? requestId
                    : UnityEngine.Random.Range(
                        1,
                        int.MaxValue);

            yield return
                StartCoroutine(
                    SendResourceSnapshotChunks(
                        targetActor,
                        effectiveRequestId));
        }

        private IEnumerator SendResourceSnapshotChunks(
            int targetActor,
            int requestId)
        {
            List<ResourceSlotMirror> snapshot =
                new List<ResourceSlotMirror>(
                    resourceSlotList);

            int totalChunks =
                snapshot.Count == 0
                    ? 1
                    : Mathf.CeilToInt(
                        (float)snapshot.Count /
                        SnapshotRecordsPerChunk);

            for (int chunkIndex = 0;
                 chunkIndex < totalChunks;
                 chunkIndex++)
            {
                int startIndex =
                    chunkIndex *
                    SnapshotRecordsPerChunk;

                int recordCount =
                    Mathf.Min(
                        SnapshotRecordsPerChunk,
                        snapshot.Count -
                        startIndex);

                if (snapshot.Count == 0)
                {
                    recordCount =
                        0;
                }

                object[] payload =
                    new object[
                        5 +
                        recordCount *
                        ResourceSnapshotRecordFieldCount];

                payload[0] =
                    requestId;

                payload[1] =
                    chunkIndex;

                payload[2] =
                    totalChunks;

                payload[3] =
                    recordCount;

                payload[4] =
                    CraftRunManager.CurrentRunId;

                int writeIndex = 5;

                for (int i = 0;
                     i < recordCount;
                     i++)
                {
                    ResourceSlotMirror slot =
                        snapshot[
                            startIndex +
                            i];

                    WriteResourceRecordPayload(
                        payload,
                        ref writeIndex,
                        slot);
                }

                RaiseEventOptions options =
                    new RaiseEventOptions
                    {
                        TargetActors =
                            new[]
                            {
                                targetActor
                            }
                    };

                PhotonNetwork.RaiseEvent(
                    ResourceSnapshotChunkEventCode,
                    payload,
                    options,
                    SendOptions.SendReliable);

                yield return null;
            }

            Logger.LogInfo(
                "Resource mirror snapshot sent. " +
                "TargetActor=" +
                targetActor +
                " | RequestId=" +
                requestId +
                " | Records=" +
                snapshot.Count +
                " | Chunks=" +
                totalChunks +
                ".");
        }

        private void ApplyResourceSnapshotChunk(
            int senderActor,
            object[] payload)
        {
            if (payload == null ||
                payload.Length < 5)
            {
                return;
            }

            try
            {
                int requestId =
                    Convert.ToInt32(
                        payload[0]);

                int chunkIndex =
                    Convert.ToInt32(
                        payload[1]);

                int totalChunks =
                    Convert.ToInt32(
                        payload[2]);

                int recordCount =
                    Convert.ToInt32(
                        payload[3]);

                string runId =
                    payload[4] as string ??
                    string.Empty;

                if (!IsRunIdCompatible(
                        runId) ||
                    recordCount < 0 ||
                    totalChunks <= 0)
                {
                    return;
                }

                if (takeoverInProgress &&
                    requestId ==
                        activeSnapshotRequestId)
                {
                    expectedSnapshotChunks =
                        Mathf.Max(
                            expectedSnapshotChunks,
                            totalChunks);

                    receivedSnapshotChunks =
                        Mathf.Min(
                            totalChunks,
                            receivedSnapshotChunks +
                            1);
                }

                int readIndex = 5;

                for (int i = 0;
                     i < recordCount;
                     i++)
                {
                    if (readIndex +
                            ResourceSnapshotRecordFieldCount >
                        payload.Length)
                    {
                        break;
                    }

                    ApplyResourceRecordPayload(
                        payload,
                        readIndex);

                    readIndex +=
                        ResourceSnapshotRecordFieldCount;
                }

                Logger.LogDebug(
                    "Resource snapshot chunk applied. " +
                    "Sender=" +
                    senderActor +
                    " | RequestId=" +
                    requestId +
                    " | Chunk=" +
                    chunkIndex +
                    "/" +
                    totalChunks +
                    " | Records=" +
                    recordCount +
                    ".");
            }
            catch (Exception exception)
            {
                Logger.LogWarning(
                    "Failed to apply resource snapshot chunk. Error=" +
                    exception);
            }
        }

        private object[] BuildResourceRecordPayload(
            ResourceSlotMirror slot,
            bool includeRunId)
        {
            object[] payload =
                new object[
                    ResourceSnapshotRecordFieldCount +
                    (
                        includeRunId
                            ? 1
                            : 0
                    )];

            int index = 0;

            if (includeRunId)
            {
                payload[index++] =
                    CraftRunManager.CurrentRunId;
            }

            WriteResourceRecordPayload(
                payload,
                ref index,
                slot);

            return payload;
        }

        private static void WriteResourceRecordPayload(
            object[] payload,
            ref int index,
            ResourceSlotMirror slot)
        {
            payload[index++] =
                slot.Position.x;

            payload[index++] =
                slot.Position.y;

            payload[index++] =
                slot.Position.z;

            payload[index++] =
                slot.Rotation.x;

            payload[index++] =
                slot.Rotation.y;

            payload[index++] =
                slot.Rotation.z;

            payload[index++] =
                slot.Rotation.w;

            payload[index++] =
                (int)slot.LastItemId;

            payload[index++] =
                slot.Occupied;

            payload[index++] =
                slot.RespawnAtNetworkTime;

            payload[index++] =
                slot.Revision;

            payload[index++] =
                slot.UpdatedAtNetworkTime;
        }

        private void ApplyResourceRecordPayload(
            object[] payload,
            int startIndex)
        {
            try
            {
                Vector3 position =
                    new Vector3(
                        Convert.ToSingle(
                            payload[
                                startIndex]),

                        Convert.ToSingle(
                            payload[
                                startIndex +
                                1]),

                        Convert.ToSingle(
                            payload[
                                startIndex +
                                2]));

                Quaternion rotation =
                    new Quaternion(
                        Convert.ToSingle(
                            payload[
                                startIndex +
                                3]),

                        Convert.ToSingle(
                            payload[
                                startIndex +
                                4]),

                        Convert.ToSingle(
                            payload[
                                startIndex +
                                5]),

                        Convert.ToSingle(
                            payload[
                                startIndex +
                                6]));

                int itemIdValue =
                    Convert.ToInt32(
                        payload[
                            startIndex +
                            7]);

                bool occupied =
                    Convert.ToBoolean(
                        payload[
                            startIndex +
                            8]);

                double respawnAt =
                    Convert.ToDouble(
                        payload[
                            startIndex +
                            9]);

                int revision =
                    Convert.ToInt32(
                        payload[
                            startIndex +
                            10]);

                double updatedAt =
                    Convert.ToDouble(
                        payload[
                            startIndex +
                            11]);

                if (itemIdValue < 0 ||
                    itemIdValue >
                        ushort.MaxValue)
                {
                    return;
                }

                ResourceSlotKey key =
                    ResourceSlotKey.FromPosition(
                        position);

                ResourceSlotMirror slot;

                if (!resourceSlots.TryGetValue(
                        key,
                        out slot))
                {
                    slot =
                        new ResourceSlotMirror
                        {
                            Key =
                                key,

                            Position =
                                position,

                            Rotation =
                                rotation,

                            LastItemId =
                                (ushort)itemIdValue,

                            Occupied =
                                occupied,

                            RespawnAtNetworkTime =
                                respawnAt,

                            Revision =
                                revision,

                            UpdatedAtNetworkTime =
                                updatedAt
                        };

                    resourceSlots.Add(
                        key,
                        slot);

                    resourceSlotList.Add(
                        slot);

                    resourceRevision =
                        Mathf.Max(
                            resourceRevision,
                            revision);

                    return;
                }

                bool incomingIsNewer =
                    revision >
                        slot.Revision ||
                    (
                        revision ==
                            slot.Revision &&
                        updatedAt >
                            slot.UpdatedAtNetworkTime
                    );

                if (!incomingIsNewer)
                {
                    return;
                }

                slot.Position =
                    position;

                slot.Rotation =
                    rotation;

                slot.LastItemId =
                    (ushort)itemIdValue;

                slot.Occupied =
                    occupied;

                slot.RespawnAtNetworkTime =
                    respawnAt;

                slot.Revision =
                    revision;

                slot.UpdatedAtNetworkTime =
                    updatedAt;

                if (!occupied)
                {
                    DetachSlotItemHandler(
                        slot);

                    if (slot.CurrentViewId > 0)
                    {
                        resourceSlotsByViewId.Remove(
                            slot.CurrentViewId);
                    }

                    slot.CurrentItem =
                        null;

                    slot.CurrentViewId =
                        0;
                }

                resourceRevision =
                    Mathf.Max(
                        resourceRevision,
                        revision);
            }
            catch (Exception)
            {
            }
        }

        // -----------------------------------------------------------------
        // Recovery probability roll
        // -----------------------------------------------------------------

        private ushort RollRecoveryItemId()
        {
            HostConfigurationSnapshot config =
                preservedConfiguration ??
                HostConfigurationSnapshot
                    .CreateDefault();

            int grade =
                Mathf.Clamp(
                    config.SpawnGrade,
                    0,
                    4);

            float[] rarityWeights =
                config.SpawnRarityWeights ??
                DefaultFlattenedRarityWeights;

            float[] itemWeights =
                config.SpawnItemWeights ??
                DefaultItemWeights;

            int rarityStart =
                grade *
                (
                    grade +
                    1
                ) /
                2;

            float[] activeRarityWeights =
                new float[
                    grade +
                    1];

            float rarityTotal = 0f;

            for (int rarity = 0;
                 rarity <= grade;
                 rarity++)
            {
                int flattenedIndex =
                    rarityStart +
                    rarity;

                float configuredWeight =
                    flattenedIndex <
                        rarityWeights.Length
                        ? Mathf.Max(
                            0f,
                            rarityWeights[
                                flattenedIndex])
                        : 0f;

                bool hasSelectableItem =
                    HasSelectableResourceItem(
                        rarity,
                        itemWeights);

                activeRarityWeights[
                    rarity] =
                        hasSelectableItem
                            ? configuredWeight
                            : 0f;

                rarityTotal +=
                    activeRarityWeights[
                        rarity];
            }

            if (rarityTotal <=
                0.0001f)
            {
                return RollUniformCommon();
            }

            float rarityRoll =
                UnityEngine.Random.Range(
                    0f,
                    rarityTotal);

            int selectedRarity = 0;

            for (int rarity = 0;
                 rarity <
                     activeRarityWeights.Length;
                 rarity++)
            {
                float weight =
                    activeRarityWeights[
                        rarity];

                if (weight <= 0f)
                {
                    continue;
                }

                if (rarityRoll <
                    weight)
                {
                    selectedRarity =
                        rarity;

                    break;
                }

                rarityRoll -=
                    weight;
            }

            float itemTotal = 0f;

            for (int i = 0;
                 i < ResourceItemIds.Length;
                 i++)
            {
                if (ResourceItemRarities[i] !=
                    selectedRarity)
                {
                    continue;
                }

                itemTotal +=
                    GetItemWeight(
                        itemWeights,
                        i);
            }

            if (itemTotal <=
                0.0001f)
            {
                return RollUniformItemFromRarity(
                    selectedRarity);
            }

            float itemRoll =
                UnityEngine.Random.Range(
                    0f,
                    itemTotal);

            for (int i = 0;
                 i < ResourceItemIds.Length;
                 i++)
            {
                if (ResourceItemRarities[i] !=
                    selectedRarity)
                {
                    continue;
                }

                float weight =
                    GetItemWeight(
                        itemWeights,
                        i);

                if (weight <= 0f)
                {
                    continue;
                }

                if (itemRoll <
                    weight)
                {
                    return ResourceItemIds[i];
                }

                itemRoll -=
                    weight;
            }

            return RollUniformItemFromRarity(
                selectedRarity);
        }

        private static bool HasSelectableResourceItem(
            int rarity,
            float[] itemWeights)
        {
            for (int i = 0;
                 i < ResourceItemIds.Length;
                 i++)
            {
                if (ResourceItemRarities[i] ==
                        rarity &&
                    GetItemWeight(
                        itemWeights,
                        i) >
                    0f)
                {
                    return true;
                }
            }

            return false;
        }

        private static float GetItemWeight(
            float[] itemWeights,
            int index)
        {
            if (itemWeights == null ||
                index < 0 ||
                index >=
                    itemWeights.Length)
            {
                return 1f;
            }

            return
                Mathf.Max(
                    0f,
                    itemWeights[index]);
        }

        private static ushort RollUniformCommon()
        {
            int[] commonIndexes =
            {
                0,
                1,
                2
            };

            return
                ResourceItemIds[
                    commonIndexes[
                        UnityEngine.Random.Range(
                            0,
                            commonIndexes.Length)]];
        }

        private static ushort RollUniformItemFromRarity(
            int rarity)
        {
            List<ushort> candidates =
                new List<ushort>();

            for (int i = 0;
                 i < ResourceItemIds.Length;
                 i++)
            {
                if (ResourceItemRarities[i] ==
                    rarity)
                {
                    candidates.Add(
                        ResourceItemIds[i]);
                }
            }

            if (candidates.Count == 0)
            {
                return RollUniformCommon();
            }

            return
                candidates[
                    UnityEngine.Random.Range(
                        0,
                        candidates.Count)];
        }

        // -----------------------------------------------------------------
        // Config helpers
        // -----------------------------------------------------------------

        private static float[] ReadConfigFloatArray(
            ConfigFile config,
            ConfigDefinition[] definitions,
            float[] defaults)
        {
            float[] values =
                new float[
                    definitions.Length];

            for (int i = 0;
                 i < definitions.Length;
                 i++)
            {
                float fallback =
                    defaults != null &&
                    i < defaults.Length
                        ? defaults[i]
                        : 0f;

                values[i] =
                    ReadConfigFloat(
                        config,
                        definitions[i],
                        fallback);
            }

            return values;
        }

        private static float ReadConfigFloat(
            ConfigFile config,
            ConfigDefinition definition,
            float fallback)
        {
            if (config == null ||
                definition == null)
            {
                return fallback;
            }

            if (!config.ContainsKey(
                    definition))
            {
                return fallback;
            }

            ConfigEntryBase entry =
                config[
                    definition];

            if (entry == null ||
                entry.BoxedValue == null)
            {
                return fallback;
            }

            try
            {
                return
                    Convert.ToSingle(
                        entry.BoxedValue);
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        private static void ApplyConfigFloatArray(
            ConfigFile config,
            ConfigDefinition[] definitions,
            float[] values)
        {
            if (config == null ||
                definitions == null ||
                values == null)
            {
                return;
            }

            int count =
                Mathf.Min(
                    definitions.Length,
                    values.Length);

            for (int i = 0;
                 i < count;
                 i++)
            {
                SetConfigEntryValue(
                    config,
                    definitions[i],
                    Mathf.Max(
                        0f,
                        values[i]));
            }
        }

        private static void SetConfigEntryValue(
            ConfigFile config,
            ConfigDefinition definition,
            object value)
        {
            if (config == null ||
                definition == null)
            {
                return;
            }

            if (!config.ContainsKey(
                    definition))
            {
                return;
            }

            ConfigEntryBase entry =
                config[
                    definition];

            if (entry == null)
            {
                return;
            }

            try
            {
                object converted =
                    ConvertValueForType(
                        value,
                        entry.SettingType);

                if (converted == null)
                {
                    return;
                }

                if (Equals(
                        entry.BoxedValue,
                        converted))
                {
                    return;
                }

                entry.BoxedValue =
                    converted;
            }
            catch (Exception exception)
            {
                if (ModLogger != null)
                {
                    ModLogger.LogWarning(
                        "Failed to apply preserved config entry. " +
                        "Definition=" +
                        definition +
                        " | Error=" +
                        exception.Message);
                }
            }
        }

        private static object ConvertValueForType(
            object value,
            Type targetType)
        {
            if (value == null ||
                targetType == null)
            {
                return null;
            }

            if (targetType.IsInstanceOfType(
                    value))
            {
                return value;
            }

            if (targetType ==
                typeof(int))
            {
                return
                    Convert.ToInt32(
                        value);
            }

            if (targetType ==
                typeof(float))
            {
                return
                    Convert.ToSingle(
                        value);
            }

            if (targetType ==
                typeof(bool))
            {
                return
                    Convert.ToBoolean(
                        value);
            }

            if (targetType ==
                typeof(string))
            {
                return
                    Convert.ToString(
                        value);
            }

            return null;
        }

        // -----------------------------------------------------------------
        // Room property helpers
        // -----------------------------------------------------------------

        private static bool ContainsConnectProperty(
            ExitGames.Client.Photon.Hashtable properties)
        {
            return
                properties.ContainsKey(
                    PropertyProtocol) ||
                properties.ContainsKey(
                    PropertyRevision) ||
                properties.ContainsKey(
                    PropertyOwnerActor) ||
                properties.ContainsKey(
                    PropertyHostEpoch) ||
                properties.ContainsKey(
                    PropertyRunId) ||
                properties.ContainsKey(
                    PropertySpawnGrade) ||
                properties.ContainsKey(
                    PropertySpawnRarityWeights) ||
                properties.ContainsKey(
                    PropertySpawnItemWeights) ||
                properties.ContainsKey(
                    PropertyCampfireMaterials) ||
                properties.ContainsKey(
                    PropertyCampfireRequireEveryone) ||
                properties.ContainsKey(
                    PropertyCampfireDistances) ||
                properties.ContainsKey(
                    PropertyInventoryMaximum);
        }

        private static int ReadIntProperty(
            ExitGames.Client.Photon.Hashtable properties,
            string key,
            int fallback)
        {
            object value;

            if (!properties.TryGetValue(
                    key,
                    out value) ||
                value == null)
            {
                return fallback;
            }

            try
            {
                return
                    Convert.ToInt32(
                        value);
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        private static bool ReadBoolProperty(
            ExitGames.Client.Photon.Hashtable properties,
            string key,
            bool fallback)
        {
            object value;

            if (!properties.TryGetValue(
                    key,
                    out value) ||
                value == null)
            {
                return fallback;
            }

            try
            {
                return
                    Convert.ToBoolean(
                        value);
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        private static string ReadStringProperty(
            ExitGames.Client.Photon.Hashtable properties,
            string key)
        {
            object value;

            if (!properties.TryGetValue(
                    key,
                    out value) ||
                value == null)
            {
                return string.Empty;
            }

            return
                value as string ??
                Convert.ToString(
                    value);
        }

        private static float[] ReadFloatArrayProperty(
            ExitGames.Client.Photon.Hashtable properties,
            string key,
            float[] fallback)
        {
            object value;

            if (!properties.TryGetValue(
                    key,
                    out value) ||
                value == null)
            {
                return
                    CloneFloatArray(
                        fallback);
            }

            float[] direct =
                value as float[];

            if (direct != null)
            {
                return
                    CloneFloatArray(
                        direct);
            }

            object[] boxed =
                value as object[];

            if (boxed == null)
            {
                return
                    CloneFloatArray(
                        fallback);
            }

            float[] result =
                new float[
                    boxed.Length];

            try
            {
                for (int i = 0;
                     i < boxed.Length;
                     i++)
                {
                    result[i] =
                        Convert.ToSingle(
                            boxed[i]);
                }

                return result;
            }
            catch (Exception)
            {
                return
                    CloneFloatArray(
                        fallback);
            }
        }

        private static int[] ReadIntArrayProperty(
            ExitGames.Client.Photon.Hashtable properties,
            string key,
            int[] fallback)
        {
            object value;

            if (!properties.TryGetValue(
                    key,
                    out value) ||
                value == null)
            {
                return
                    CloneIntArray(
                        fallback);
            }

            int[] direct =
                value as int[];

            if (direct != null)
            {
                return
                    CloneIntArray(
                        direct);
            }

            object[] boxed =
                value as object[];

            if (boxed == null)
            {
                return
                    CloneIntArray(
                        fallback);
            }

            int[] result =
                new int[
                    boxed.Length];

            try
            {
                for (int i = 0;
                     i < boxed.Length;
                     i++)
                {
                    result[i] =
                        Convert.ToInt32(
                            boxed[i]);
                }

                return result;
            }
            catch (Exception)
            {
                return
                    CloneIntArray(
                        fallback);
            }
        }

        private static HostConfigurationSnapshot
            NormalizeConfigurationSnapshot(
                HostConfigurationSnapshot snapshot)
        {
            HostConfigurationSnapshot safe =
                snapshot ??
                HostConfigurationSnapshot
                    .CreateDefault();

            safe.Protocol =
                ProtocolVersion;

            safe.Revision =
                Mathf.Max(
                    0,
                    safe.Revision);

            safe.OwnerActor =
                Mathf.Max(
                    0,
                    safe.OwnerActor);

            safe.HostEpoch =
                Mathf.Max(
                    0,
                    safe.HostEpoch);

            safe.RunId =
                safe.RunId ??
                string.Empty;

            safe.SpawnGrade =
                Mathf.Clamp(
                    safe.SpawnGrade,
                    0,
                    4);

            safe.SpawnRarityWeights =
                EnsureFloatArrayLength(
                    safe.SpawnRarityWeights,
                    DefaultFlattenedRarityWeights);

            safe.SpawnItemWeights =
                EnsureFloatArrayLength(
                    safe.SpawnItemWeights,
                    DefaultItemWeights);

            safe.CampfireMaterials =
                EnsureIntArrayLength(
                    safe.CampfireMaterials,
                    new[]
                    {
                        1,
                        1,
                        1
                    });

            for (int i = 0;
                 i < safe.CampfireMaterials.Length;
                 i++)
            {
                safe.CampfireMaterials[i] =
                    Mathf.Max(
                        0,
                        safe.CampfireMaterials[i]);
            }

            safe.CampfireDistances =
                EnsureFloatArrayLength(
                    safe.CampfireDistances,
                    new[]
                    {
                        15f,
                        4f
                    });

            safe.CampfireDistances[0] =
                Mathf.Max(
                    1f,
                    safe.CampfireDistances[0]);

            safe.CampfireDistances[1] =
                Mathf.Max(
                    1f,
                    safe.CampfireDistances[1]);

            safe.InventoryMaximum =
                Mathf.Clamp(
                    safe.InventoryMaximum,
                    1,
                    100);

            return safe;
        }

        private static float[] EnsureFloatArrayLength(
            float[] source,
            float[] fallback)
        {
            int requiredLength =
                fallback != null
                    ? fallback.Length
                    : 0;

            float[] result =
                new float[
                    requiredLength];

            for (int i = 0;
                 i < requiredLength;
                 i++)
            {
                float value =
                    source != null &&
                    i < source.Length
                        ? source[i]
                        : fallback[i];

                result[i] =
                    Mathf.Max(
                        0f,
                        value);
            }

            return result;
        }

        private static int[] EnsureIntArrayLength(
            int[] source,
            int[] fallback)
        {
            int requiredLength =
                fallback != null
                    ? fallback.Length
                    : 0;

            int[] result =
                new int[
                    requiredLength];

            for (int i = 0;
                 i < requiredLength;
                 i++)
            {
                result[i] =
                    source != null &&
                    i < source.Length
                        ? source[i]
                        : fallback[i];
            }

            return result;
        }

        // -----------------------------------------------------------------
        // General helpers
        // -----------------------------------------------------------------

        private bool IsRunIdCompatible(
            string runId)
        {
            string currentRunId =
                CraftRunManager.CurrentRunId;

            if (string.IsNullOrEmpty(
                    currentRunId) ||
                string.IsNullOrEmpty(
                    runId))
            {
                return true;
            }

            return
                string.Equals(
                    currentRunId,
                    runId,
                    StringComparison.Ordinal);
        }

        private static int GetLocalActorNumber()
        {
            return
                PhotonNetwork.LocalPlayer != null
                    ? PhotonNetwork.LocalPlayer.ActorNumber
                    : 0;
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

        private static bool IsFinite(
            float value)
        {
            return
                !float.IsNaN(
                    value) &&
                !float.IsInfinity(
                    value);
        }

        private static float[] CloneFloatArray(
            float[] source)
        {
            if (source == null)
            {
                return
                    Array.Empty<float>();
            }

            float[] clone =
                new float[
                    source.Length];

            Array.Copy(
                source,
                clone,
                source.Length);

            return clone;
        }

        private static int[] CloneIntArray(
            int[] source)
        {
            if (source == null)
            {
                return
                    Array.Empty<int>();
            }

            int[] clone =
                new int[
                    source.Length];

            Array.Copy(
                source,
                clone,
                source.Length);

            return clone;
        }

        private static bool FloatArraysEqual(
            float[] left,
            float[] right)
        {
            if (ReferenceEquals(
                    left,
                    right))
            {
                return true;
            }

            if (left == null ||
                right == null ||
                left.Length !=
                    right.Length)
            {
                return false;
            }

            for (int i = 0;
                 i < left.Length;
                 i++)
            {
                if (!Mathf.Approximately(
                        left[i],
                        right[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IntArraysEqual(
            int[] left,
            int[] right)
        {
            if (ReferenceEquals(
                    left,
                    right))
            {
                return true;
            }

            if (left == null ||
                right == null ||
                left.Length !=
                    right.Length)
            {
                return false;
            }

            for (int i = 0;
                 i < left.Length;
                 i++)
            {
                if (left[i] !=
                    right[i])
                {
                    return false;
                }
            }

            return true;
        }
    }
}
