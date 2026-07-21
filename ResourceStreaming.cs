// CRAFT PEAK RESOURCE STREAMING BUILD 1.0.0
//
// 목적
// - Spawn.cs가 만든 약 3000개의 판매용 자원 중
//   기본적으로 현재 Segment의 자원만 실제로 렌더링/충돌 처리합니다.
//   ModConfig에서 앞뒤 인접 Segment 활성 범위를 늘릴 수 있습니다.
// - 현재 구간에서 멀리 떨어진 자원은 Item과 PhotonView를 유지한 채
//   Renderer, Collider, Light, AudioSource만 로컬에서 비활성화합니다.
// - 플레이어가 가까이 있는 자원은 Segment와 관계없이 항상 활성화합니다.
// - Segment가 바뀌면 새 구간을 즉시 활성화하고 멀어진 구간을 순차적으로 비활성화합니다.
//
// 이 방식의 이유
// - 현재 Spawn.cs의 슬롯과 5분 재생성 타이머는 private 상태입니다.
// - 외부 파일에서 PhotonNetwork.Destroy를 사용하면 Spawn.cs가 이를 수집으로 판단하여
//   재생성 타이머를 잘못 시작할 수 있습니다.
// - GameObject.SetActive(false)도 Spawn.cs의 InactivePickupDetected 판정과 충돌합니다.
// - 따라서 Item/GameObject/PhotonView는 살아 있게 두고 무거운 표시·충돌 요소만 스트리밍합니다.
//
// 성능 효과
// - 원거리 자원의 렌더링, Collider broadphase, Light, Audio 비용 감소
// - Photon Room Object 개수와 초기 3000개 네트워크 생성 비용은 그대로 유지
// - 향후 Spawn.cs에 공개 슬롯 API를 추가하면 실제 네트워크 오브젝트 스트리밍으로 확장 가능
//
// 호스트 승계
// - 이 파일은 권한 상태를 저장하지 않습니다.
// - 활성 구간은 CraftRunManager의 Photon Room Property 상태에서 파생됩니다.
// - 호스트가 바뀌어도 모든 클라이언트가 새 Segment 상태로 자동 재계산합니다.
// - 따라서 Connect.cs에서 별도 ResourceStreaming 스냅샷을 전송할 필요가 없습니다.
//
// 중요
// - 리플렉션을 사용하지 않습니다.
// - Harmony 패치를 사용하지 않습니다.
// - Delete.cs, Spawn.cs, RunManager.cs와 같은 Craft PEAK.dll에 포함합니다.

using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using System;
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
        CraftRunManager.PluginGuid,
        BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency(
        "com.github.PEAKModding.PEAKLib.ModConfig",
        BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class ResourceStreaming :
        BaseUnityPlugin
    {
        public const string PluginGuid =
            "com.sappheiros.crafting.resourcestreaming";

        public const string PluginName =
            "Craft PEAK Resource Streaming";

        public const string PluginVersion =
            "1.0.0";

        private const bool DefaultStreamingEnabled = true;
        private const int DefaultAdjacentSegmentRadius = 0;
        private const float DefaultPlayerActivationDistance = 55f;
        private const float DefaultDiscoveryIntervalSeconds = 1f;
        private const float DefaultPlayerPositionRefreshSeconds = 0.20f;
        private const int DefaultItemsProcessedPerFrame = 300;
        private const int DefaultNewRecordsPerFrame = 60;

        private const int MinimumAdjacentSegmentRadius = 0;
        private const int MaximumAdjacentSegmentRadius = 5;

        private const float MinimumPlayerActivationDistance = 0f;
        private const float MaximumPlayerActivationDistance = 250f;

        private const float MinimumDiscoveryIntervalSeconds = 0.20f;
        private const float MaximumDiscoveryIntervalSeconds = 10f;

        private const int MinimumItemsProcessedPerFrame = 25;
        private const int MaximumItemsProcessedPerFrame = 2000;

        private const int MinimumNewRecordsPerFrame = 5;
        private const int MaximumNewRecordsPerFrame = 500;

        private const float SegmentBoundsExpansion = 12f;
        private const float RemapDistance = 8f;
        private const float StatisticsLogIntervalSeconds = 15f;

        private readonly Dictionary<int, ResourceRecord>
            recordsByInstanceId =
                new Dictionary<int, ResourceRecord>();

        private readonly List<ResourceRecord>
            records =
                new List<ResourceRecord>();

        private readonly Queue<Item>
            pendingItems =
                new Queue<Item>();

        private readonly HashSet<int>
            pendingInstanceIds =
                new HashSet<int>();

        private readonly List<SegmentBoundsInfo>
            segmentBounds =
                new List<SegmentBoundsInfo>();

        private readonly List<Vector3>
            activePlayerPositions =
                new List<Vector3>();

        private ConfigEntry<bool>
            streamingEnabledConfig;

        private ConfigEntry<int>
            adjacentSegmentRadiusConfig;

        private ConfigEntry<float>
            playerActivationDistanceConfig;

        private ConfigEntry<float>
            discoveryIntervalConfig;

        private ConfigEntry<int>
            itemsProcessedPerFrameConfig;

        private ConfigEntry<int>
            newRecordsPerFrameConfig;

        private ConfigEntry<bool>
            streamCollidersConfig;

        private ConfigEntry<bool>
            streamLightsConfig;

        private ConfigEntry<bool>
            streamAudioConfig;

        private float nextDiscoveryAt;
        private float nextPlayerPositionRefreshAt;
        private float nextStatisticsLogAt;

        private int processingCursor;
        private int cleanupCursor;

        private int currentSegment = -1;
        private int segmentBoundsSceneHandle = -1;

        private bool forceFullEvaluation = true;
        private bool sceneReady;

        internal static ResourceStreaming Instance
        {
            get;
            private set;
        }

        internal static ManualLogSource ModLogger
        {
            get;
            private set;
        }

        public static bool StreamingEnabled
        {
            get
            {
                return
                    Instance != null &&
                    Instance.GetStreamingEnabled();
            }
        }

        public static int CurrentStreamingSegment
        {
            get
            {
                return
                    Instance != null
                        ? Instance.currentSegment
                        : -1;
            }
        }

        public static int TrackedResourceCount
        {
            get
            {
                return
                    Instance != null
                        ? Instance.records.Count
                        : 0;
            }
        }

        private sealed class SegmentBoundsInfo
        {
            public int SegmentIndex;
            public Bounds Bounds;
            public bool HasBounds;
            public string SegmentName;
        }

        private sealed class ResourceRecord
        {
            public int InstanceId;
            public Item Item;

            public int SegmentIndex = -1;
            public Vector3 MappedPosition;

            public Renderer[] Renderers;
            public bool[] RendererEnabledStates;

            public Collider[] Colliders;
            public bool[] ColliderEnabledStates;

            public Light[] Lights;
            public bool[] LightEnabledStates;

            public AudioSource[] AudioSources;
            public bool[] AudioEnabledStates;

            public bool IsStreamedOut;
            public bool SeenInLatestDiscovery;
        }

        public sealed class ResourceStreamingStatistics
        {
            public int Tracked;
            public int Active;
            public int StreamedOut;
            public int UnknownSegment;
            public int PendingRegistration;
            public int CurrentSegment;

            public override string ToString()
            {
                return
                    "Tracked=" +
                    Tracked +
                    " | Active=" +
                    Active +
                    " | StreamedOut=" +
                    StreamedOut +
                    " | UnknownSegment=" +
                    UnknownSegment +
                    " | Pending=" +
                    PendingRegistration +
                    " | CurrentSegment=" +
                    CurrentSegment;
            }
        }

        private void Awake()
        {
            Instance =
                this;

            ModLogger =
                Logger;

            BindConfig();

            SceneManager.sceneLoaded +=
                HandleSceneLoaded;

            SceneManager.sceneUnloaded +=
                HandleSceneUnloaded;

            CraftRunManager.SnapshotChanged +=
                HandleRunSnapshotChanged;

            CraftRunManager.SegmentChanged +=
                HandleRunSegmentChanged;

            CraftRunManager.RunStarted +=
                HandleRunStarted;

            CraftRunManager.RunFinished +=
                HandleRunFinished;

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
                " loaded. " +
                "StreamingEnabled=" +
                GetStreamingEnabled() +
                " | AdjacentSegmentRadius=" +
                GetAdjacentSegmentRadius() +
                " | PlayerActivationDistance=" +
                GetPlayerActivationDistance() +
                "m.");
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -=
                HandleSceneLoaded;

            SceneManager.sceneUnloaded -=
                HandleSceneUnloaded;

            CraftRunManager.SnapshotChanged -=
                HandleRunSnapshotChanged;

            CraftRunManager.SegmentChanged -=
                HandleRunSegmentChanged;

            CraftRunManager.RunStarted -=
                HandleRunStarted;

            CraftRunManager.RunFinished -=
                HandleRunFinished;

            UnbindConfigEvents();

            RestoreAllRecords(
                "Plugin destroyed");

            ClearTracking();

            if (Instance == this)
            {
                Instance = null;
            }

            ModLogger =
                null;
        }

        private void Update()
        {
            if (!sceneReady)
            {
                TryPrepareScene();
            }

            if (!sceneReady)
            {
                return;
            }

            if (!GetStreamingEnabled())
            {
                if (HasAnyStreamedOutRecord())
                {
                    RestoreAllRecords(
                        "Streaming disabled");
                }

                return;
            }

            RefreshCurrentSegment();

            if (Time.unscaledTime >=
                nextPlayerPositionRefreshAt)
            {
                nextPlayerPositionRefreshAt =
                    Time.unscaledTime +
                    DefaultPlayerPositionRefreshSeconds;

                RefreshActivePlayerPositions();
            }

            if (Time.unscaledTime >=
                nextDiscoveryAt)
            {
                nextDiscoveryAt =
                    Time.unscaledTime +
                    GetDiscoveryIntervalSeconds();

                DiscoverResourceItems();
            }

            RegisterPendingItems();
            ProcessStreamingBatch();
            CleanupStaleRecordsBatch();

            if (Time.unscaledTime >=
                nextStatisticsLogAt)
            {
                nextStatisticsLogAt =
                    Time.unscaledTime +
                    StatisticsLogIntervalSeconds;

                Logger.LogInfo(
                    "Resource streaming statistics. " +
                    GetStatistics());
            }
        }

        private void BindConfig()
        {
            streamingEnabledConfig =
                Config.Bind(
                    "01. 자원 스트리밍",
                    "자원 스트리밍 활성화",
                    DefaultStreamingEnabled,
                    "멀리 있는 판매용 자원의 렌더러와 충돌을 비활성화합니다.");

            adjacentSegmentRadiusConfig =
                Config.Bind(
                    "01. 자원 스트리밍",
                    "활성 인접 구간 수",
                    DefaultAdjacentSegmentRadius,
                    new ConfigDescription(
                        "현재 Segment를 기준으로 앞뒤 몇 개 Segment까지 항상 활성화할지 설정합니다. " +
                        "1이면 이전/현재/다음 구간이 활성화됩니다.",
                        new AcceptableValueRange<int>(
                            MinimumAdjacentSegmentRadius,
                            MaximumAdjacentSegmentRadius)));

            playerActivationDistanceConfig =
                Config.Bind(
                    "01. 자원 스트리밍",
                    "플레이어 주변 활성 거리",
                    DefaultPlayerActivationDistance,
                    new ConfigDescription(
                        "구간 판정과 관계없이 살아 있는 플레이어 주변에서 자원을 활성화할 거리입니다. " +
                        "멀티플레이에서 플레이어가 서로 다른 구간에 남아 있을 때 자원이 사라지는 것을 방지합니다.",
                        new AcceptableValueRange<float>(
                            MinimumPlayerActivationDistance,
                            MaximumPlayerActivationDistance)));

            discoveryIntervalConfig =
                Config.Bind(
                    "02. 자원 스트리밍 성능",
                    "새 자원 검색 간격 (초)",
                    DefaultDiscoveryIntervalSeconds,
                    new ConfigDescription(
                        "Spawn.cs가 새로 생성하거나 재생성한 자원을 검색하는 간격입니다.",
                        new AcceptableValueRange<float>(
                            MinimumDiscoveryIntervalSeconds,
                            MaximumDiscoveryIntervalSeconds)));

            itemsProcessedPerFrameConfig =
                Config.Bind(
                    "02. 자원 스트리밍 성능",
                    "프레임당 상태 처리 수",
                    DefaultItemsProcessedPerFrame,
                    new ConfigDescription(
                        "한 프레임에 활성/비활성 상태를 점검할 최대 자원 수입니다. " +
                        "값이 높으면 구간 전환 반응은 빨라지지만 순간 CPU 사용량이 증가합니다.",
                        new AcceptableValueRange<int>(
                            MinimumItemsProcessedPerFrame,
                            MaximumItemsProcessedPerFrame)));

            newRecordsPerFrameConfig =
                Config.Bind(
                    "02. 자원 스트리밍 성능",
                    "프레임당 신규 자원 등록 수",
                    DefaultNewRecordsPerFrame,
                    new ConfigDescription(
                        "Renderer와 Collider 정보를 처음 수집할 자원 수입니다. " +
                        "초기 3000개 등록으로 인한 한 프레임 정지를 줄이기 위해 나누어 처리합니다.",
                        new AcceptableValueRange<int>(
                            MinimumNewRecordsPerFrame,
                            MaximumNewRecordsPerFrame)));

            streamCollidersConfig =
                Config.Bind(
                    "03. 스트리밍 대상 구성요소",
                    "원거리 Collider 비활성화",
                    true,
                    "활성화하면 원거리 자원의 Collider를 끕니다. 렌더링보다 물리 성능 개선에 유리합니다.");

            streamLightsConfig =
                Config.Bind(
                    "03. 스트리밍 대상 구성요소",
                    "원거리 Light 비활성화",
                    true,
                    "활성화하면 원거리 자원에 포함된 Light를 끕니다.");

            streamAudioConfig =
                Config.Bind(
                    "03. 스트리밍 대상 구성요소",
                    "원거리 AudioSource 비활성화",
                    true,
                    "활성화하면 원거리 자원에 포함된 AudioSource를 끕니다.");

            streamingEnabledConfig.SettingChanged +=
                HandleStreamingConfigChanged;

            adjacentSegmentRadiusConfig.SettingChanged +=
                HandleStreamingConfigChanged;

            playerActivationDistanceConfig.SettingChanged +=
                HandleStreamingConfigChanged;

            discoveryIntervalConfig.SettingChanged +=
                HandleStreamingConfigChanged;

            itemsProcessedPerFrameConfig.SettingChanged +=
                HandleStreamingConfigChanged;

            newRecordsPerFrameConfig.SettingChanged +=
                HandleStreamingConfigChanged;

            streamCollidersConfig.SettingChanged +=
                HandleComponentStreamingConfigChanged;

            streamLightsConfig.SettingChanged +=
                HandleComponentStreamingConfigChanged;

            streamAudioConfig.SettingChanged +=
                HandleComponentStreamingConfigChanged;
        }

        private void UnbindConfigEvents()
        {
            if (streamingEnabledConfig != null)
            {
                streamingEnabledConfig.SettingChanged -=
                    HandleStreamingConfigChanged;
            }

            if (adjacentSegmentRadiusConfig != null)
            {
                adjacentSegmentRadiusConfig.SettingChanged -=
                    HandleStreamingConfigChanged;
            }

            if (playerActivationDistanceConfig != null)
            {
                playerActivationDistanceConfig.SettingChanged -=
                    HandleStreamingConfigChanged;
            }

            if (discoveryIntervalConfig != null)
            {
                discoveryIntervalConfig.SettingChanged -=
                    HandleStreamingConfigChanged;
            }

            if (itemsProcessedPerFrameConfig != null)
            {
                itemsProcessedPerFrameConfig.SettingChanged -=
                    HandleStreamingConfigChanged;
            }

            if (newRecordsPerFrameConfig != null)
            {
                newRecordsPerFrameConfig.SettingChanged -=
                    HandleStreamingConfigChanged;
            }

            if (streamCollidersConfig != null)
            {
                streamCollidersConfig.SettingChanged -=
                    HandleComponentStreamingConfigChanged;
            }

            if (streamLightsConfig != null)
            {
                streamLightsConfig.SettingChanged -=
                    HandleComponentStreamingConfigChanged;
            }

            if (streamAudioConfig != null)
            {
                streamAudioConfig.SettingChanged -=
                    HandleComponentStreamingConfigChanged;
            }
        }

        private void HandleStreamingConfigChanged(
            object sender,
            EventArgs eventArgs)
        {
            forceFullEvaluation =
                true;

            nextDiscoveryAt =
                0f;

            if (!GetStreamingEnabled())
            {
                RestoreAllRecords(
                    "Config disabled streaming");
            }

            Logger.LogInfo(
                "Resource streaming config changed. " +
                "Enabled=" +
                GetStreamingEnabled() +
                " | AdjacentSegmentRadius=" +
                GetAdjacentSegmentRadius() +
                " | PlayerActivationDistance=" +
                GetPlayerActivationDistance() +
                "m.");
        }

        private void HandleComponentStreamingConfigChanged(
            object sender,
            EventArgs eventArgs)
        {
            RestoreAllRecords(
                "Component streaming config changed");

            RebuildAllComponentStateSnapshots();

            forceFullEvaluation =
                true;
        }

        private void HandleSceneLoaded(
            Scene scene,
            LoadSceneMode loadSceneMode)
        {
            RestoreAllRecords(
                "Scene changed");

            ClearTracking();

            sceneReady =
                false;

            segmentBoundsSceneHandle =
                -1;

            currentSegment =
                -1;

            nextDiscoveryAt =
                0f;

            nextPlayerPositionRefreshAt =
                0f;

            nextStatisticsLogAt =
                Time.unscaledTime +
                StatisticsLogIntervalSeconds;

            if (IsExcludedScene(
                    scene))
            {
                Logger.LogInfo(
                    "Resource streaming disabled in scene: " +
                    scene.name);

                return;
            }

            Logger.LogInfo(
                "Resource streaming waiting for MapHandler. Scene=" +
                scene.name +
                ".");
        }

        private void HandleSceneUnloaded(
            Scene scene)
        {
            if (scene.handle ==
                segmentBoundsSceneHandle)
            {
                RestoreAllRecords(
                    "Streaming scene unloaded");

                ClearTracking();

                sceneReady =
                    false;

                segmentBoundsSceneHandle =
                    -1;
            }
        }

        private void HandleRunSnapshotChanged(
            CraftRunSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return;
            }

            int snapshotSegment =
                snapshot.CurrentSegment;

            if (snapshotSegment !=
                currentSegment)
            {
                currentSegment =
                    snapshotSegment;

                forceFullEvaluation =
                    true;
            }

            if (!snapshot.IsActive)
            {
                RestoreAllRecords(
                    "Run is not active");
            }
        }

        private void HandleRunSegmentChanged(
            int previousSegment,
            int newSegment)
        {
            currentSegment =
                newSegment;

            forceFullEvaluation =
                true;

            processingCursor =
                0;

            Logger.LogInfo(
                "Resource streaming segment changed. " +
                "Previous=" +
                previousSegment +
                " | Current=" +
                newSegment +
                ".");
        }

        private void HandleRunStarted(
            CraftRunSnapshot snapshot)
        {
            currentSegment =
                snapshot != null
                    ? snapshot.CurrentSegment
                    : -1;

            forceFullEvaluation =
                true;

            nextDiscoveryAt =
                0f;
        }

        private void HandleRunFinished(
            CraftRunSnapshot snapshot)
        {
            RestoreAllRecords(
                "Run finished");
        }

        private void TryPrepareScene()
        {
            Scene scene =
                SceneManager.GetActiveScene();

            if (IsExcludedScene(
                    scene))
            {
                return;
            }

            MapHandler mapHandler =
                UnityEngine.Object
                    .FindAnyObjectByType<MapHandler>();

            if (mapHandler == null ||
                mapHandler.segments == null ||
                mapHandler.segments.Length == 0)
            {
                return;
            }

            if (!BuildSegmentBounds(
                    mapHandler,
                    scene))
            {
                return;
            }

            sceneReady =
                true;

            currentSegment =
                ResolveCurrentSegment();

            forceFullEvaluation =
                true;

            nextDiscoveryAt =
                0f;

            nextPlayerPositionRefreshAt =
                0f;

            Logger.LogInfo(
                "Resource streaming scene prepared. " +
                "Scene=" +
                scene.name +
                " | SegmentBounds=" +
                segmentBounds.Count +
                " | CurrentSegment=" +
                currentSegment +
                ".");
        }

        private bool BuildSegmentBounds(
            MapHandler mapHandler,
            Scene scene)
        {
            segmentBounds.Clear();

            if (mapHandler == null ||
                mapHandler.segments == null)
            {
                return false;
            }

            for (int segmentIndex = 0;
                 segmentIndex <
                     mapHandler.segments.Length;
                 segmentIndex++)
            {
                GameObject segmentParent =
                    mapHandler.segments[
                        segmentIndex]
                        .segmentParent;

                GameObject segmentCampfire =
                    mapHandler.segments[
                        segmentIndex]
                        .segmentCampfire;

                SegmentBoundsInfo info =
                    new SegmentBoundsInfo
                    {
                        SegmentIndex =
                            segmentIndex,

                        SegmentName =
                            segmentParent != null
                                ? segmentParent.name
                                : "Segment_" +
                                  segmentIndex,

                        HasBounds =
                            false
                    };

                if (segmentParent != null)
                {
                    EncapsulateHierarchyBounds(
                        info,
                        segmentParent);
                }

                if (segmentCampfire != null)
                {
                    EncapsulateHierarchyBounds(
                        info,
                        segmentCampfire);
                }

                if (!info.HasBounds)
                {
                    Vector3 fallbackPosition =
                        segmentParent != null
                            ? segmentParent.transform.position
                            : (
                                segmentCampfire != null
                                    ? segmentCampfire
                                        .transform
                                        .position
                                    : mapHandler
                                        .transform
                                        .position
                            );

                    info.Bounds =
                        new Bounds(
                            fallbackPosition,
                            Vector3.one *
                            20f);

                    info.HasBounds =
                        true;
                }

                info.Bounds.Expand(
                    SegmentBoundsExpansion);

                segmentBounds.Add(
                    info);

                Logger.LogInfo(
                    "Resource streaming segment bounds. " +
                    "Segment=" +
                    segmentIndex +
                    " | Name=" +
                    info.SegmentName +
                    " | Center=" +
                    FormatVector(
                        info.Bounds.center) +
                    " | Size=" +
                    FormatVector(
                        info.Bounds.size) +
                    ".");
            }

            segmentBoundsSceneHandle =
                scene.handle;

            return
                segmentBounds.Count > 0;
        }

        private static void EncapsulateHierarchyBounds(
            SegmentBoundsInfo info,
            GameObject root)
        {
            if (info == null ||
                root == null)
            {
                return;
            }

            Renderer[] renderers =
                root.GetComponentsInChildren<
                    Renderer>(
                    true);

            for (int i = 0;
                 i < renderers.Length;
                 i++)
            {
                Renderer renderer =
                    renderers[i];

                if (renderer == null)
                {
                    continue;
                }

                EncapsulateBounds(
                    info,
                    renderer.bounds);
            }

            Collider[] colliders =
                root.GetComponentsInChildren<
                    Collider>(
                    true);

            for (int i = 0;
                 i < colliders.Length;
                 i++)
            {
                Collider collider =
                    colliders[i];

                if (collider == null)
                {
                    continue;
                }

                EncapsulateBounds(
                    info,
                    collider.bounds);
            }

            Spawner[] spawners =
                root.GetComponentsInChildren<
                    Spawner>(
                    true);

            for (int i = 0;
                 i < spawners.Length;
                 i++)
            {
                Spawner spawner =
                    spawners[i];

                if (spawner == null)
                {
                    continue;
                }

                Bounds pointBounds =
                    new Bounds(
                        spawner.transform.position,
                        Vector3.one *
                        2f);

                EncapsulateBounds(
                    info,
                    pointBounds);
            }

            if (!info.HasBounds)
            {
                Bounds fallback =
                    new Bounds(
                        root.transform.position,
                        Vector3.one *
                        2f);

                EncapsulateBounds(
                    info,
                    fallback);
            }
        }

        private static void EncapsulateBounds(
            SegmentBoundsInfo info,
            Bounds value)
        {
            if (!info.HasBounds)
            {
                info.Bounds =
                    value;

                info.HasBounds =
                    true;

                return;
            }

            info.Bounds.Encapsulate(
                value);
        }

        private void RefreshCurrentSegment()
        {
            int resolved =
                ResolveCurrentSegment();

            if (resolved ==
                currentSegment)
            {
                return;
            }

            int previous =
                currentSegment;

            currentSegment =
                resolved;

            forceFullEvaluation =
                true;

            processingCursor =
                0;

            Logger.LogInfo(
                "Resource streaming detected segment change. " +
                "Previous=" +
                previous +
                " | Current=" +
                currentSegment +
                ".");
        }

        private static int ResolveCurrentSegment()
        {
            int runSegment =
                CraftRunManager.CurrentSegment;

            if (runSegment >= 0)
            {
                return runSegment;
            }

            MapHandler mapHandler =
                UnityEngine.Object
                    .FindAnyObjectByType<MapHandler>();

            if (mapHandler == null)
            {
                return -1;
            }

            return
                Mathf.Max(
                    0,
                    (int)MapHandler.CurrentSegmentNumber);
        }

        private void RefreshActivePlayerPositions()
        {
            activePlayerPositions.Clear();

            List<Character> characters =
                PlayerHandler.GetAllPlayerCharacters();

            for (int i = 0;
                 i < characters.Count;
                 i++)
            {
                Character character =
                    characters[i];

                if (character == null ||
                    character.gameObject == null ||
                    !character.gameObject
                        .activeInHierarchy)
                {
                    continue;
                }

                activePlayerPositions.Add(
                    character.transform.position);
            }
        }

        private void DiscoverResourceItems()
        {
            for (int i = 0;
                 i < records.Count;
                 i++)
            {
                records[i]
                    .SeenInLatestDiscovery =
                    false;
            }

            if (Item.ALL_ITEMS == null)
            {
                return;
            }

            List<Item> snapshot =
                new List<Item>(
                    Item.ALL_ITEMS);

            for (int i = 0;
                 i < snapshot.Count;
                 i++)
            {
                Item item =
                    snapshot[i];

                if (!IsTrackableResource(
                        item))
                {
                    continue;
                }

                int instanceId =
                    item.GetInstanceID();

                ResourceRecord existing;

                if (recordsByInstanceId.TryGetValue(
                        instanceId,
                        out existing))
                {
                    existing.SeenInLatestDiscovery =
                        true;

                    continue;
                }

                if (pendingInstanceIds.Add(
                        instanceId))
                {
                    pendingItems.Enqueue(
                        item);
                }
            }
        }

        private void RegisterPendingItems()
        {
            int maximum =
                GetNewRecordsPerFrame();

            int registered = 0;

            while (registered <
                       maximum &&
                   pendingItems.Count > 0)
            {
                Item item =
                    pendingItems.Dequeue();

                if (item == null)
                {
                    registered++;
                    continue;
                }

                int instanceId =
                    item.GetInstanceID();

                pendingInstanceIds.Remove(
                    instanceId);

                if (recordsByInstanceId.ContainsKey(
                        instanceId) ||
                    !IsTrackableResource(
                        item))
                {
                    registered++;
                    continue;
                }

                ResourceRecord record =
                    CreateRecord(
                        item);

                if (record != null)
                {
                    recordsByInstanceId.Add(
                        instanceId,
                        record);

                    records.Add(
                        record);
                }

                registered++;
            }
        }

        private ResourceRecord CreateRecord(
            Item item)
        {
            if (!IsTrackableResource(
                    item))
            {
                return null;
            }

            ResourceRecord record =
                new ResourceRecord
                {
                    InstanceId =
                        item.GetInstanceID(),

                    Item =
                        item,

                    SegmentIndex =
                        FindSegmentForPosition(
                            item.transform.position),

                    MappedPosition =
                        item.transform.position,

                    Renderers =
                        item.GetComponentsInChildren<
                            Renderer>(
                            true),

                    Colliders =
                        item.GetComponentsInChildren<
                            Collider>(
                            true),

                    Lights =
                        item.GetComponentsInChildren<
                            Light>(
                            true),

                    AudioSources =
                        item.GetComponentsInChildren<
                            AudioSource>(
                            true),

                    IsStreamedOut =
                        false,

                    SeenInLatestDiscovery =
                        true
                };

            record.RendererEnabledStates =
                CaptureEnabledStates(
                    record.Renderers);

            record.ColliderEnabledStates =
                CaptureEnabledStates(
                    record.Colliders);

            record.LightEnabledStates =
                CaptureEnabledStates(
                    record.Lights);

            record.AudioEnabledStates =
                CaptureEnabledStates(
                    record.AudioSources);

            return record;
        }

        private static bool[] CaptureEnabledStates(
            Renderer[] components)
        {
            if (components == null)
            {
                return
                    Array.Empty<bool>();
            }

            bool[] states =
                new bool[
                    components.Length];

            for (int i = 0;
                 i < components.Length;
                 i++)
            {
                Renderer component =
                    components[i];

                states[i] =
                    component != null &&
                    component.enabled;
            }

            return states;
        }

        private static bool[] CaptureEnabledStates<T>(
            T[] components)
            where T : Behaviour
        {
            if (components == null)
            {
                return
                    Array.Empty<bool>();
            }

            bool[] states =
                new bool[
                    components.Length];

            for (int i = 0;
                 i < components.Length;
                 i++)
            {
                T component =
                    components[i];

                states[i] =
                    component != null &&
                    component.enabled;
            }

            return states;
        }

        private static bool[] CaptureEnabledStates(
            Collider[] components)
        {
            if (components == null)
            {
                return
                    Array.Empty<bool>();
            }

            bool[] states =
                new bool[
                    components.Length];

            for (int i = 0;
                 i < components.Length;
                 i++)
            {
                Collider component =
                    components[i];

                states[i] =
                    component != null &&
                    component.enabled;
            }

            return states;
        }

        private void ProcessStreamingBatch()
        {
            if (records.Count == 0)
            {
                return;
            }

            int maximum =
                forceFullEvaluation
                    ? Mathf.Max(
                        GetItemsProcessedPerFrame(),
                        DefaultItemsProcessedPerFrame)
                    : GetItemsProcessedPerFrame();

            int processed = 0;

            while (processed <
                       maximum &&
                   records.Count > 0)
            {
                if (processingCursor >=
                    records.Count)
                {
                    processingCursor =
                        0;

                    forceFullEvaluation =
                        false;
                }

                ResourceRecord record =
                    records[
                        processingCursor];

                processingCursor++;
                processed++;

                ProcessRecord(
                    record);
            }
        }

        private void ProcessRecord(
            ResourceRecord record)
        {
            if (record == null ||
                record.Item == null)
            {
                return;
            }

            Item item =
                record.Item;

            if (!IsTrackableResource(
                    item))
            {
                SetRecordStreamedOut(
                    record,
                    false);

                return;
            }

            Vector3 currentPosition =
                item.transform.position;

            if ((currentPosition -
                 record.MappedPosition)
                .sqrMagnitude >=
                RemapDistance *
                RemapDistance)
            {
                record.SegmentIndex =
                    FindSegmentForPosition(
                        currentPosition);

                record.MappedPosition =
                    currentPosition;
            }

            // 손에 들렸거나 인벤토리 상태가 된 아이템은 반드시 원상복구합니다.
            if (item.itemState !=
                ItemState.Ground)
            {
                SetRecordStreamedOut(
                    record,
                    false);

                return;
            }

            bool shouldBeActive =
                ShouldResourceBeActive(
                    record,
                    currentPosition);

            SetRecordStreamedOut(
                record,
                !shouldBeActive);
        }

        private bool ShouldResourceBeActive(
            ResourceRecord record,
            Vector3 position)
        {
            if (record == null)
            {
                return true;
            }

            if (!CraftRunManager.IsRunActive)
            {
                return true;
            }

            if (IsNearAnyPlayer(
                    position))
            {
                return true;
            }

            if (currentSegment < 0 ||
                record.SegmentIndex < 0)
            {
                // 구간을 판별하지 못한 아이템은 안전을 위해 숨기지 않습니다.
                return true;
            }

            int activeRadius =
                GetAdjacentSegmentRadius();

            if (CraftRunManager.CurrentState ==
                CraftRunState.Transitioning)
            {
                activeRadius++;
            }

            return
                Mathf.Abs(
                    record.SegmentIndex -
                    currentSegment) <=
                activeRadius;
        }

        private bool IsNearAnyPlayer(
            Vector3 position)
        {
            float activationDistance =
                GetPlayerActivationDistance();

            if (activationDistance <= 0f ||
                activePlayerPositions.Count ==
                    0)
            {
                return false;
            }

            float distanceSquared =
                activationDistance *
                activationDistance;

            for (int i = 0;
                 i <
                     activePlayerPositions.Count;
                 i++)
            {
                if ((
                        position -
                        activePlayerPositions[i]
                    ).sqrMagnitude <=
                    distanceSquared)
                {
                    return true;
                }
            }

            return false;
        }

        private int FindSegmentForPosition(
            Vector3 position)
        {
            if (segmentBounds.Count == 0)
            {
                return -1;
            }

            int containingSegment =
                -1;

            float smallestContainingVolume =
                float.MaxValue;

            for (int i = 0;
                 i < segmentBounds.Count;
                 i++)
            {
                SegmentBoundsInfo info =
                    segmentBounds[i];

                if (info == null ||
                    !info.HasBounds ||
                    !info.Bounds.Contains(
                        position))
                {
                    continue;
                }

                Vector3 size =
                    info.Bounds.size;

                float volume =
                    Mathf.Max(
                        0.001f,
                        size.x *
                        size.y *
                        size.z);

                if (volume <
                    smallestContainingVolume)
                {
                    containingSegment =
                        info.SegmentIndex;

                    smallestContainingVolume =
                        volume;
                }
            }

            if (containingSegment >= 0)
            {
                return containingSegment;
            }

            int nearestSegment =
                -1;

            float nearestDistanceSquared =
                float.MaxValue;

            for (int i = 0;
                 i < segmentBounds.Count;
                 i++)
            {
                SegmentBoundsInfo info =
                    segmentBounds[i];

                if (info == null ||
                    !info.HasBounds)
                {
                    continue;
                }

                float distanceSquared =
                    info.Bounds.SqrDistance(
                        position);

                if (distanceSquared <
                    nearestDistanceSquared)
                {
                    nearestDistanceSquared =
                        distanceSquared;

                    nearestSegment =
                        info.SegmentIndex;
                }
            }

            return nearestSegment;
        }

        private void SetRecordStreamedOut(
            ResourceRecord record,
            bool streamedOut)
        {
            if (record == null ||
                record.Item == null ||
                record.IsStreamedOut ==
                    streamedOut)
            {
                return;
            }

            if (streamedOut)
            {
                ApplyRendererState(
                    record.Renderers,
                    false);

                if (GetStreamColliders())
                {
                    ApplyColliderState(
                        record.Colliders,
                        false);
                }

                if (GetStreamLights())
                {
                    ApplyBehaviourState(
                        record.Lights,
                        false);
                }

                if (GetStreamAudio())
                {
                    ApplyBehaviourState(
                        record.AudioSources,
                        false);
                }

                record.IsStreamedOut =
                    true;

                return;
            }

            RestoreComponentStates(
                record);

            record.IsStreamedOut =
                false;
        }

        private void RestoreComponentStates(
            ResourceRecord record)
        {
            if (record == null)
            {
                return;
            }

            RestoreRendererStates(
                record.Renderers,
                record.RendererEnabledStates);

            RestoreColliderStates(
                record.Colliders,
                record.ColliderEnabledStates);

            RestoreBehaviourStates(
                record.Lights,
                record.LightEnabledStates);

            RestoreBehaviourStates(
                record.AudioSources,
                record.AudioEnabledStates);
        }

        private static void ApplyRendererState(
            Renderer[] components,
            bool enabled)
        {
            if (components == null)
            {
                return;
            }

            for (int i = 0;
                 i < components.Length;
                 i++)
            {
                Renderer component =
                    components[i];

                if (component != null)
                {
                    component.enabled =
                        enabled;
                }
            }
        }

        private static void ApplyColliderState(
            Collider[] components,
            bool enabled)
        {
            if (components == null)
            {
                return;
            }

            for (int i = 0;
                 i < components.Length;
                 i++)
            {
                Collider component =
                    components[i];

                if (component != null)
                {
                    component.enabled =
                        enabled;
                }
            }
        }

        private static void ApplyBehaviourState<T>(
            T[] components,
            bool enabled)
            where T : Behaviour
        {
            if (components == null)
            {
                return;
            }

            for (int i = 0;
                 i < components.Length;
                 i++)
            {
                T component =
                    components[i];

                if (component != null)
                {
                    component.enabled =
                        enabled;
                }
            }
        }

        private static void RestoreRendererStates(
            Renderer[] components,
            bool[] states)
        {
            if (components == null ||
                states == null)
            {
                return;
            }

            int count =
                Mathf.Min(
                    components.Length,
                    states.Length);

            for (int i = 0;
                 i < count;
                 i++)
            {
                Renderer component =
                    components[i];

                if (component != null)
                {
                    component.enabled =
                        states[i];
                }
            }
        }

        private static void RestoreColliderStates(
            Collider[] components,
            bool[] states)
        {
            if (components == null ||
                states == null)
            {
                return;
            }

            int count =
                Mathf.Min(
                    components.Length,
                    states.Length);

            for (int i = 0;
                 i < count;
                 i++)
            {
                Collider component =
                    components[i];

                if (component != null)
                {
                    component.enabled =
                        states[i];
                }
            }
        }

        private static void RestoreBehaviourStates<T>(
            T[] components,
            bool[] states)
            where T : Behaviour
        {
            if (components == null ||
                states == null)
            {
                return;
            }

            int count =
                Mathf.Min(
                    components.Length,
                    states.Length);

            for (int i = 0;
                 i < count;
                 i++)
            {
                T component =
                    components[i];

                if (component != null)
                {
                    component.enabled =
                        states[i];
                }
            }
        }

        private void CleanupStaleRecordsBatch()
        {
            if (records.Count == 0)
            {
                return;
            }

            int cleanupCount =
                Mathf.Min(
                    50,
                    records.Count);

            for (int i = 0;
                 i < cleanupCount &&
                 records.Count > 0;
                 i++)
            {
                if (cleanupCursor >=
                    records.Count)
                {
                    cleanupCursor =
                        0;
                }

                ResourceRecord record =
                    records[
                        cleanupCursor];

                bool remove =
                    record == null ||
                    record.Item == null;

                if (!remove &&
                    !record.SeenInLatestDiscovery &&
                    !IsTrackableResource(
                        record.Item))
                {
                    remove =
                        true;
                }

                if (!remove)
                {
                    cleanupCursor++;
                    continue;
                }

                if (record != null)
                {
                    if (record.Item != null)
                    {
                        SetRecordStreamedOut(
                            record,
                            false);
                    }

                    recordsByInstanceId.Remove(
                        record.InstanceId);

                    pendingInstanceIds.Remove(
                        record.InstanceId);
                }

                records.RemoveAt(
                    cleanupCursor);

                if (processingCursor >
                    cleanupCursor)
                {
                    processingCursor--;
                }
            }
        }

        private void RebuildAllComponentStateSnapshots()
        {
            for (int i = 0;
                 i < records.Count;
                 i++)
            {
                ResourceRecord record =
                    records[i];

                if (record == null ||
                    record.Item == null)
                {
                    continue;
                }

                record.Renderers =
                    record.Item
                        .GetComponentsInChildren<
                            Renderer>(
                            true);

                record.Colliders =
                    record.Item
                        .GetComponentsInChildren<
                            Collider>(
                            true);

                record.Lights =
                    record.Item
                        .GetComponentsInChildren<
                            Light>(
                            true);

                record.AudioSources =
                    record.Item
                        .GetComponentsInChildren<
                            AudioSource>(
                            true);

                record.RendererEnabledStates =
                    CaptureEnabledStates(
                        record.Renderers);

                record.ColliderEnabledStates =
                    CaptureEnabledStates(
                        record.Colliders);

                record.LightEnabledStates =
                    CaptureEnabledStates(
                        record.Lights);

                record.AudioEnabledStates =
                    CaptureEnabledStates(
                        record.AudioSources);

                record.IsStreamedOut =
                    false;
            }
        }

        private void RestoreAllRecords(
            string reason)
        {
            int restored = 0;

            for (int i = 0;
                 i < records.Count;
                 i++)
            {
                ResourceRecord record =
                    records[i];

                if (record == null ||
                    !record.IsStreamedOut)
                {
                    continue;
                }

                SetRecordStreamedOut(
                    record,
                    false);

                restored++;
            }

            if (restored > 0 &&
                Logger != null)
            {
                Logger.LogInfo(
                    "Resource streaming restored all records. " +
                    "Reason=" +
                    reason +
                    " | Restored=" +
                    restored +
                    ".");
            }
        }

        private bool HasAnyStreamedOutRecord()
        {
            for (int i = 0;
                 i < records.Count;
                 i++)
            {
                ResourceRecord record =
                    records[i];

                if (record != null &&
                    record.IsStreamedOut)
                {
                    return true;
                }
            }

            return false;
        }

        private void ClearTracking()
        {
            recordsByInstanceId.Clear();
            records.Clear();

            pendingItems.Clear();
            pendingInstanceIds.Clear();

            segmentBounds.Clear();
            activePlayerPositions.Clear();

            processingCursor =
                0;

            cleanupCursor =
                0;

            forceFullEvaluation =
                true;
        }

        private static bool IsTrackableResource(
            Item item)
        {
            return
                item != null &&
                item.gameObject != null &&
                Spawn.IsSaleResourceId(
                    item.itemID);
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

        private bool GetStreamingEnabled()
        {
            return
                streamingEnabledConfig == null
                    ? DefaultStreamingEnabled
                    : streamingEnabledConfig.Value;
        }

        private int GetAdjacentSegmentRadius()
        {
            return
                adjacentSegmentRadiusConfig == null
                    ? DefaultAdjacentSegmentRadius
                    : Mathf.Clamp(
                        adjacentSegmentRadiusConfig.Value,
                        MinimumAdjacentSegmentRadius,
                        MaximumAdjacentSegmentRadius);
        }

        private float GetPlayerActivationDistance()
        {
            return
                playerActivationDistanceConfig == null
                    ? DefaultPlayerActivationDistance
                    : Mathf.Clamp(
                        playerActivationDistanceConfig.Value,
                        MinimumPlayerActivationDistance,
                        MaximumPlayerActivationDistance);
        }

        private float GetDiscoveryIntervalSeconds()
        {
            return
                discoveryIntervalConfig == null
                    ? DefaultDiscoveryIntervalSeconds
                    : Mathf.Clamp(
                        discoveryIntervalConfig.Value,
                        MinimumDiscoveryIntervalSeconds,
                        MaximumDiscoveryIntervalSeconds);
        }

        private int GetItemsProcessedPerFrame()
        {
            return
                itemsProcessedPerFrameConfig == null
                    ? DefaultItemsProcessedPerFrame
                    : Mathf.Clamp(
                        itemsProcessedPerFrameConfig.Value,
                        MinimumItemsProcessedPerFrame,
                        MaximumItemsProcessedPerFrame);
        }

        private int GetNewRecordsPerFrame()
        {
            return
                newRecordsPerFrameConfig == null
                    ? DefaultNewRecordsPerFrame
                    : Mathf.Clamp(
                        newRecordsPerFrameConfig.Value,
                        MinimumNewRecordsPerFrame,
                        MaximumNewRecordsPerFrame);
        }

        private bool GetStreamColliders()
        {
            return
                streamCollidersConfig == null ||
                streamCollidersConfig.Value;
        }

        private bool GetStreamLights()
        {
            return
                streamLightsConfig == null ||
                streamLightsConfig.Value;
        }

        private bool GetStreamAudio()
        {
            return
                streamAudioConfig == null ||
                streamAudioConfig.Value;
        }

        public static ResourceStreamingStatistics
            GetStatistics()
        {
            ResourceStreamingStatistics statistics =
                new ResourceStreamingStatistics
                {
                    CurrentSegment =
                        CurrentStreamingSegment
                };

            if (Instance == null)
            {
                return statistics;
            }

            statistics.Tracked =
                Instance.records.Count;

            statistics.PendingRegistration =
                Instance.pendingItems.Count;

            for (int i = 0;
                 i <
                     Instance.records.Count;
                 i++)
            {
                ResourceRecord record =
                    Instance.records[i];

                if (record == null)
                {
                    continue;
                }

                if (record.SegmentIndex < 0)
                {
                    statistics.UnknownSegment++;
                }

                if (record.IsStreamedOut)
                {
                    statistics.StreamedOut++;
                }
                else
                {
                    statistics.Active++;
                }
            }

            return statistics;
        }

        public static void ForceRefresh()
        {
            if (Instance == null)
            {
                return;
            }

            Instance.forceFullEvaluation =
                true;

            Instance.processingCursor =
                0;

            Instance.nextDiscoveryAt =
                0f;

            Instance.nextPlayerPositionRefreshAt =
                0f;
        }

        public static void RestoreEverythingNow()
        {
            if (Instance == null)
            {
                return;
            }

            Instance.RestoreAllRecords(
                "External restore request");
        }

        private static string FormatVector(
            Vector3 value)
        {
            return
                "(" +
                value.x.ToString("0.0") +
                ", " +
                value.y.ToString("0.0") +
                ", " +
                value.z.ToString("0.0") +
                ")";
        }
    }
}
