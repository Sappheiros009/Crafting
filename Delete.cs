using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Photon.Pun;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CraftPeak
{
    /// <summary>
    /// PEAK 원본 맵의 아이템 스폰을 차단합니다.
    ///
    /// 동작 범위
    /// 1. Airport에서는 아무것도 차단하지 않습니다.
    /// 2. 실제 게임 맵 최초 진입 시 원본 Spawner를 2초 동안 차단합니다.
    /// 3. MapHandler.GoToSegment 호출 시에는 전환 감시만 시작하고 차단하지 않습니다.
    /// 4. 실제 세그먼트가 변경된 뒤 새 세그먼트 원본 Spawner의 첫 호출부터 2초간 차단합니다.
    /// 5. 평상시 제작·ItemSpawner·다른 모드가 생성한 아이템은 유지됩니다.
    /// 6. Luggage가 직접 호출하는 Spawner.SpawnItems도 차단 시간에만 막습니다.
    /// 7. 씬에 미리 배치되어 있거나 먼저 생성된 초기 지상 아이템을 정리합니다.
    /// 8. RespawnChest는 부활 기능을 위해 삭제하거나 상호작용을 막지 않습니다.
    ///
    /// Delete.cs가 PatchAll(typeof(Delete).Assembly)을 실행하므로
    /// 같은 Craft PEAK.dll 안의 다른 Harmony 패치도 한 번만 적용됩니다.
    /// Reflection은 사용하지 않습니다.
    /// </summary>
    [BepInPlugin(
        PluginGuid,
        PluginName,
        PluginVersion)]
    public sealed class Delete :
        BaseUnityPlugin
    {
        public const string PluginGuid =
            "com.sappheiros.crafting.delete";

        public const string PluginName =
            "Craft PEAK Delete";

        public const string PluginVersion =
            "1.1.2";

        private const float OriginalSpawnerBlockSeconds =
            2f;

        private const float SegmentTransitionTimeoutSeconds =
            60f;

        /// <summary>
        /// 사용자가 인게임에서 직접 확인한 모든 World.itemID입니다.
        /// Airport에서는 이 목록을 사용하지 않으므로 여권과 배낭도 로비에서 유지됩니다.
        /// </summary>
        private static readonly HashSet<ushort>
            BlockedItemIds =
                new HashSet<ushort>
                {
                    0, 1, 2, 3, 4, 5, 6, 7, 8, 9,
                    10, 11, 12, 13, 14, 15, 16, 17, 18, 19,
                    20, 21, 22, 23, 24, 25, 26, 27, 28, 29,
                    30, 31, 32, 33, 34, 35, 36, 37, 38,
                    40, 41, 42, 43, 44, 45, 46, 47, 48, 49,
                    51, 55, 56, 57, 58, 59, 60, 61, 62, 63,
                    64, 65, 66, 67, 68, 69, 70, 71, 72, 73,
                    74, 75, 76, 77, 79, 81, 83, 84, 90, 93,
                    95, 98, 99, 100, 101, 102, 103, 104, 105, 106,
                    107, 108, 109, 110, 111, 112, 113, 114, 115, 117,
                    152, 153, 154, 155, 156, 158, 159, 160, 161, 162,
                    165
                };

        private readonly HashSet<int>
            initialFieldItemInstanceIds =
                new HashSet<int>();

        private Harmony harmony;

        // 최초 게임플레이 씬 정리 중 활성화됩니다.
        private bool initialCleanupActive;

        // GoToSegment가 호출된 뒤 실제 세그먼트 변경을 기다리는 상태입니다.
        // 이 상태만으로는 Spawner를 차단하지 않습니다.
        private bool segmentTransitionPending;

        // 실제 세그먼트 변경 후 새 세그먼트의 첫 원본 Spawner가
        // 진입하는 순간부터 2초 동안만 활성화됩니다.
        private bool segmentTransitionBlockActive;

        private int segmentTransitionGeneration;

        private Coroutine segmentTransitionRoutine;

        private MapHandler pendingMapHandler;

        private int pendingPreviousSegment =
            -1;

        private int pendingRequestedSegment =
            -1;

        private int pendingSceneHandle =
            -1;

        private int loadedSceneHandle =
            -1;

        internal static Delete Instance
        {
            get;
            private set;
        }

        internal static ManualLogSource ModLogger
        {
            get;
            private set;
        }

        private void Awake()
        {
            Instance =
                this;

            ModLogger =
                Logger;

            harmony =
                new Harmony(
                    PluginGuid);

            harmony.PatchAll(
                typeof(Delete).Assembly);

            SceneManager.sceneLoaded +=
                HandleSceneLoaded;

            HandleSceneLoaded(
                SceneManager.GetActiveScene(),
                LoadSceneMode.Single);

            Logger.LogInfo(
                PluginName +
                " " +
                PluginVersion +
                " loaded. " +
                "Original spawners are blocked for " +
                OriginalSpawnerBlockSeconds.ToString("0.00") +
                " seconds on initial gameplay load and from the first original-spawner call after every completed segment transition. " +
                "Items crafted or spawned outside those transition windows are preserved.");
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -=
                HandleSceneLoaded;

            if (segmentTransitionRoutine !=
                null)
            {
                StopCoroutine(
                    segmentTransitionRoutine);

                segmentTransitionRoutine =
                    null;
            }

            if (harmony != null)
            {
                harmony.UnpatchSelf();
                harmony =
                    null;
            }

            initialCleanupActive =
                false;

            segmentTransitionPending =
                false;

            segmentTransitionBlockActive =
                false;

            pendingMapHandler =
                null;

            pendingPreviousSegment =
                -1;

            pendingRequestedSegment =
                -1;

            pendingSceneHandle =
                -1;

            segmentTransitionGeneration++;

            initialFieldItemInstanceIds.Clear();

            if (Instance ==
                this)
            {
                Instance =
                    null;
            }

            ModLogger =
                null;
        }

        private void HandleSceneLoaded(
            Scene scene,
            LoadSceneMode loadSceneMode)
        {
            StopAllCoroutines();

            segmentTransitionRoutine =
                null;

            segmentTransitionGeneration++;

            segmentTransitionPending =
                false;

            segmentTransitionBlockActive =
                false;

            pendingMapHandler =
                null;

            pendingPreviousSegment =
                -1;

            pendingRequestedSegment =
                -1;

            pendingSceneHandle =
                -1;

            initialFieldItemInstanceIds.Clear();

            loadedSceneHandle =
                scene.handle;

            initialCleanupActive =
                IsGameplayScene(
                    scene);

            if (!initialCleanupActive)
            {
                Logger.LogInfo(
                    "Item deletion disabled in scene: " +
                    scene.name);

                return;
            }

            Logger.LogInfo(
                "Gameplay scene detected. Starting initial field cleanup and " +
                OriginalSpawnerBlockSeconds.ToString("0.00") +
                "-second original-spawner block: " +
                scene.name);

            StartCoroutine(
                CleanupGameplaySceneRoutine(
                    scene.handle));
        }

        private static bool IsGameplayScene(
            Scene scene)
        {
            if (!scene.IsValid() ||
                !scene.isLoaded)
            {
                return false;
            }

            if (string.Equals(
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
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            MapHandler mapHandler =
                UnityEngine.Object
                    .FindAnyObjectByType<
                        MapHandler>();

            return
                mapHandler != null;
        }

        private bool IsCurrentGameplayScene(
            int sceneHandle)
        {
            Scene activeScene =
                SceneManager.GetActiveScene();

            return
                activeScene.IsValid() &&
                activeScene.isLoaded &&
                activeScene.handle ==
                    sceneHandle &&
                loadedSceneHandle ==
                    sceneHandle &&
                IsGameplayScene(
                    activeScene);
        }

        /// <summary>
        /// Spawner Harmony Prefix에서 호출됩니다.
        ///
        /// 최초 게임플레이 진입 정리 중에는 기존과 동일하게 차단합니다.
        /// 일반 세그먼트 전환 중에는 차단하지 않습니다.
        ///
        /// GoToSegment로 등록된 전환에서 실제 GetCurrentSegment 값이 변경되고,
        /// 새 세그먼트에 배치된 원본 Spawner가 처음 진입하는 순간
        /// 2초 차단을 시작합니다. 따라서 전환 연출 시간은 차단 시간에
        /// 포함되지 않으며 첫 원본 스폰 시도도 실행 전에 차단됩니다.
        /// </summary>
        internal bool ShouldBlockOriginalSpawner(
            Spawner spawner)
        {
            if (loadedSceneHandle !=
                SceneManager
                    .GetActiveScene()
                    .handle)
            {
                return false;
            }

            if (initialCleanupActive ||
                segmentTransitionBlockActive)
            {
                return true;
            }

            if (!segmentTransitionPending ||
                spawner == null)
            {
                return false;
            }

            if (!TryActivatePostSegmentBlock(
                    spawner))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Luggage 상호작용처럼 Spawner 인스턴스가 없는 검사에서 사용합니다.
        /// 전환 감시 중에는 막지 않고 실제 2초 차단이 시작된 뒤에만 막습니다.
        /// </summary>
        internal bool IsSpawnerBlockCurrentlyActive()
        {
            return
                loadedSceneHandle ==
                    SceneManager
                        .GetActiveScene()
                        .handle &&
                (
                    initialCleanupActive ||
                    segmentTransitionBlockActive
                );
        }

        /// <summary>
        /// MapHandler.GoToSegment 호출 시에는 차단하지 않고
        /// 실제 세그먼트 변경만 감시하도록 예약합니다.
        /// </summary>
        internal void BeginSegmentTransitionWatch(
            MapHandler mapHandler,
            Segment targetSegment)
        {
            if (mapHandler == null)
            {
                return;
            }

            Scene activeScene =
                SceneManager.GetActiveScene();

            if (!IsGameplayScene(
                    activeScene) ||
                activeScene.handle !=
                    loadedSceneHandle)
            {
                return;
            }

            int currentSegment =
                (int)mapHandler
                    .GetCurrentSegment();

            int requestedSegment =
                (int)targetSegment;

            if (requestedSegment <=
                currentSegment)
            {
                return;
            }

            segmentTransitionGeneration++;

            int generation =
                segmentTransitionGeneration;

            if (segmentTransitionRoutine !=
                null)
            {
                StopCoroutine(
                    segmentTransitionRoutine);

                segmentTransitionRoutine =
                    null;
            }

            // 요청 시점에는 차단하지 않습니다.
            segmentTransitionPending =
                true;

            segmentTransitionBlockActive =
                false;

            pendingMapHandler =
                mapHandler;

            pendingPreviousSegment =
                currentSegment;

            pendingRequestedSegment =
                requestedSegment;

            pendingSceneHandle =
                activeScene.handle;

            Logger.LogInfo(
                "Segment transition watch started without blocking. " +
                "From=" +
                currentSegment +
                " | Target=" +
                requestedSegment +
                " | Scene=" +
                activeScene.name +
                " | BlockStarts=FirstNewSegmentSpawnerCall");

            segmentTransitionRoutine =
                StartCoroutine(
                    SegmentTransitionPendingTimeoutRoutine(
                        generation));
        }

        /// <summary>
        /// 새 세그먼트의 첫 Spawner Prefix에서 호출됩니다.
        ///
        /// currentSegment가 이전 값과 달라졌고 해당 Spawner가 새 세그먼트
        /// 또는 그 세그먼트의 캠프파이어 아래에 있을 때만 차단을 시작합니다.
        /// ItemSpawner 등 맵 세그먼트 밖의 런타임 Spawner는 차단 시작
        /// 트리거로 사용하지 않습니다.
        /// </summary>
        private bool TryActivatePostSegmentBlock(
            Spawner spawner)
        {
            if (!segmentTransitionPending ||
                pendingMapHandler == null ||
                spawner == null ||
                pendingSceneHandle !=
                    loadedSceneHandle)
            {
                return false;
            }

            int currentSegment =
                (int)pendingMapHandler
                    .GetCurrentSegment();

            if (currentSegment ==
                pendingPreviousSegment)
            {
                return false;
            }

            if (!IsSpawnerUnderCurrentSegment(
                    spawner,
                    pendingMapHandler,
                    currentSegment))
            {
                return false;
            }

            int generation =
                segmentTransitionGeneration;

            segmentTransitionPending =
                false;

            segmentTransitionBlockActive =
                true;

            if (segmentTransitionRoutine !=
                null)
            {
                StopCoroutine(
                    segmentTransitionRoutine);

                segmentTransitionRoutine =
                    null;
            }

            Logger.LogInfo(
                "Post-segment original-spawner block started. " +
                "Previous=" +
                pendingPreviousSegment +
                " | Current=" +
                currentSegment +
                " | Requested=" +
                pendingRequestedSegment +
                " | Duration=" +
                OriginalSpawnerBlockSeconds.ToString("0.00") +
                "s | TriggerSpawner=" +
                spawner.gameObject.name);

            segmentTransitionRoutine =
                StartCoroutine(
                    ReleasePostSegmentBlockRoutine(
                        currentSegment,
                        generation));

            return true;
        }

        private static bool IsSpawnerUnderCurrentSegment(
            Spawner spawner,
            MapHandler mapHandler,
            int currentSegment)
        {
            if (spawner == null ||
                mapHandler == null ||
                mapHandler.segments == null ||
                spawner.transform == null)
            {
                return false;
            }

            int mapSegmentIndex =
                currentSegment;

            // 원본 JumpToSegmentLogic는 Peak에서 직전 맵 세그먼트를 사용합니다.
            if (currentSegment ==
                (int)Segment.Peak)
            {
                mapSegmentIndex--;
            }

            if (mapSegmentIndex < 0 ||
                mapSegmentIndex >=
                    mapHandler.segments.Length)
            {
                return false;
            }

            MapHandler.MapSegment segment =
                mapHandler.segments[
                    mapSegmentIndex];

            if (segment == null)
            {
                return false;
            }

            if (IsTransformInside(
                    spawner.transform,
                    segment.segmentParent))
            {
                return true;
            }

            if (IsTransformInside(
                    spawner.transform,
                    segment.segmentCampfire))
            {
                return true;
            }

            return false;
        }

        private static bool IsTransformInside(
            Transform target,
            GameObject root)
        {
            if (target == null ||
                root == null)
            {
                return false;
            }

            Transform rootTransform =
                root.transform;

            return
                target ==
                    rootTransform ||
                target.IsChildOf(
                    rootTransform);
        }

        private IEnumerator
            ReleasePostSegmentBlockRoutine(
                int currentSegment,
                int generation)
        {
            yield return
                new WaitForSecondsRealtime(
                    OriginalSpawnerBlockSeconds);

            if (generation !=
                segmentTransitionGeneration)
            {
                yield break;
            }

            segmentTransitionBlockActive =
                false;

            segmentTransitionRoutine =
                null;

            pendingMapHandler =
                null;

            pendingPreviousSegment =
                -1;

            pendingRequestedSegment =
                -1;

            pendingSceneHandle =
                -1;

            Logger.LogInfo(
                "Post-segment original-spawner block released. " +
                "CurrentSegment=" +
                currentSegment +
                " | Scene=" +
                SceneManager
                    .GetActiveScene()
                    .name +
                " | Crafted and ItemSpawner items are allowed again.");
        }

        private IEnumerator
            SegmentTransitionPendingTimeoutRoutine(
                int generation)
        {
            yield return
                new WaitForSecondsRealtime(
                    SegmentTransitionTimeoutSeconds);

            if (generation !=
                    segmentTransitionGeneration ||
                !segmentTransitionPending)
            {
                yield break;
            }

            segmentTransitionPending =
                false;

            segmentTransitionBlockActive =
                false;

            segmentTransitionRoutine =
                null;

            Logger.LogWarning(
                "Segment transition watch timed out without starting a block. " +
                "Previous=" +
                pendingPreviousSegment +
                " | Requested=" +
                pendingRequestedSegment +
                " | Scene=" +
                SceneManager
                    .GetActiveScene()
                    .name);

            pendingMapHandler =
                null;

            pendingPreviousSegment =
                -1;

            pendingRequestedSegment =
                -1;

            pendingSceneHandle =
                -1;
        }

        internal static bool IsBlockedItem(
            Item item)
        {
            return
                item != null &&
                BlockedItemIds.Contains(
                    item.itemID) &&
                !Spawn.IsSaleResourceId(
                    item.itemID);
        }

        /// <summary>
        /// 최초 게임플레이 씬 초기화 시점 차이로 남는 오브젝트를 정리합니다.
        /// 원본 Spawner는 최초 2초 동안 Harmony Prefix에서 별도로 차단됩니다.
        /// </summary>
        private IEnumerator
            CleanupGameplaySceneRoutine(
                int sceneHandle)
        {
            // 씬과 Photon 오브젝트가 배치될 시간을 한 프레임 기다립니다.
            yield return null;

            if (!IsCurrentCleanupScene(
                    sceneHandle))
            {
                yield break;
            }

            CaptureInitialFieldItems();

            int removedItems =
                RemoveCapturedInitialFieldItems();

            int disabledLuggage =
                DisableExistingLuggage();

            Logger.LogInfo(
                "Initial cleanup pass completed. " +
                "Items=" +
                removedItems +
                " | Luggage=" +
                disabledLuggage +
                " | CapturedItemIds=" +
                initialFieldItemInstanceIds.Count);

            // 늦게 초기화되는 원본 Spawner 호출만 잠시 차단합니다.
            // 이 시간 동안 Item.Awake 기반 삭제는 하지 않으므로
            // 다른 모드가 직접 만든 아이템은 삭제하지 않습니다.
            yield return
                new WaitForSecondsRealtime(
                    OriginalSpawnerBlockSeconds);

            if (!IsCurrentCleanupScene(
                    sceneHandle))
            {
                yield break;
            }

            // 첫 스냅샷에 포함된 비활성 오브젝트가
            // 뒤늦게 활성화된 경우만 재정리합니다.
            int lateRemoved =
                RemoveCapturedInitialFieldItems();

            initialCleanupActive =
                false;

            initialFieldItemInstanceIds.Clear();

            Logger.LogInfo(
                "Initial field cleanup finished. " +
                "LateRemoved=" +
                lateRemoved +
                " | Initial original-spawner block released=True. " +
                "The block will be reactivated for every MapHandler.GoToSegment transition.");
        }

        private bool IsCurrentCleanupScene(
            int sceneHandle)
        {
            return
                initialCleanupActive &&
                loadedSceneHandle ==
                    sceneHandle &&
                SceneManager
                    .GetActiveScene()
                    .handle ==
                    sceneHandle;
        }

        private void CaptureInitialFieldItems()
        {
            Item[] items =
                UnityEngine.Object
                    .FindObjectsByType<Item>(
                        FindObjectsInactive.Include,
                        FindObjectsSortMode.None);

            for (int i = 0;
                 i < items.Length;
                 i++)
            {
                Item item =
                    items[i];

                if (item == null ||
                    !IsBlockedItem(
                        item) ||
                    item.itemState !=
                        ItemState.Ground)
                {
                    continue;
                }

                initialFieldItemInstanceIds.Add(
                    item.GetInstanceID());
            }

            Logger.LogInfo(
                "Initial field item snapshot captured. Count=" +
                initialFieldItemInstanceIds.Count);
        }

        private int RemoveCapturedInitialFieldItems()
        {
            int removedCount =
                0;

            Item[] items =
                UnityEngine.Object
                    .FindObjectsByType<Item>(
                        FindObjectsInactive.Include,
                        FindObjectsSortMode.None);

            for (int i = 0;
                 i < items.Length;
                 i++)
            {
                Item item =
                    items[i];

                if (item == null ||
                    !initialFieldItemInstanceIds
                        .Contains(
                            item.GetInstanceID()) ||
                    !IsBlockedItem(
                        item) ||
                    item.itemState !=
                        ItemState.Ground)
                {
                    continue;
                }

                RemoveGroundItem(
                    item);

                removedCount++;
            }

            return
                removedCount;
        }

        private int DisableExistingLuggage()
        {
            int disabledCount =
                0;

            Luggage[] luggageObjects =
                UnityEngine.Object
                    .FindObjectsByType<Luggage>(
                        FindObjectsInactive.Include,
                        FindObjectsSortMode.None);

            for (int i = 0;
                 i <
                     luggageObjects.Length;
                 i++)
            {
                Luggage luggage =
                    luggageObjects[i];

                if (luggage == null ||
                    luggage is RespawnChest ||
                    !luggage.gameObject
                        .activeSelf)
                {
                    continue;
                }

                luggage.gameObject
                    .SetActive(
                        false);

                disabledCount++;
            }

            return
                disabledCount;
        }

        private void RemoveGroundItem(
            Item item)
        {
            if (item == null ||
                item.itemState !=
                    ItemState.Ground)
            {
                return;
            }

            string objectName =
                item.gameObject.name;

            ushort itemId =
                item.itemID;

            // 모든 클라이언트에서 즉시 보이지 않고
            // 상호작용되지 않게 합니다.
            item.gameObject
                .SetActive(
                    false);

            PhotonView photonView =
                item.photonView;

            if (PhotonNetwork.InRoom &&
                PhotonNetwork.IsMasterClient &&
                photonView != null &&
                photonView.ViewID !=
                    0)
            {
                try
                {
                    PhotonNetwork.Destroy(
                        item.gameObject);
                }
                catch (Exception exception)
                {
                    Logger.LogWarning(
                        "Photon item destroy failed. Falling back to local destroy. " +
                        "Object=" +
                        objectName +
                        " | ItemID=" +
                        itemId +
                        " | Error=" +
                        exception.Message);

                    UnityEngine.Object.Destroy(
                        item.gameObject);
                }
            }
            else if (!PhotonNetwork.InRoom ||
                     photonView == null ||
                     photonView.ViewID ==
                         0)
            {
                UnityEngine.Object.Destroy(
                    item.gameObject);
            }
        }

        /// <summary>
        /// 최초 게임플레이 씬 로드 직후와 세그먼트 전환 구간에만
        /// 원본 Spawner.TrySpawnItems 실행을 차단합니다.
        ///
        /// 평상시에는 Prefix가 원본 실행을 허용하므로,
        /// 제작·ItemSpawner·다른 모드가 나중에 생성한 아이템은 유지됩니다.
        /// </summary>
        [HarmonyPatch(
            typeof(Spawner),
            nameof(Spawner.TrySpawnItems))]
        private static class
            SpawnerTrySpawnItemsPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(
                Spawner __instance,
                ref List<PhotonView> __result)
            {
                if (Instance == null ||
                    !Instance
                        .ShouldBlockOriginalSpawner(
                            __instance))
                {
                    return true;
                }

                __result =
                    new List<PhotonView>();

                return false;
            }
        }

        /// <summary>
        /// 최초 정리 또는 세그먼트 전환 차단 중
        /// Luggage.OpenLuggageRPC가 직접 호출하는 SpawnItems 경로를 차단합니다.
        ///
        /// RespawnChest의 부활 로직은 RespawnChest.SpawnItems 오버라이드에서
        /// 먼저 처리되므로 유지됩니다. 아이템을 꺼내는 base.SpawnItems만 막힙니다.
        /// </summary>
        [HarmonyPatch(
            typeof(Spawner),
            nameof(Spawner.SpawnItems))]
        private static class
            SpawnerSpawnItemsPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(
                Spawner __instance,
                ref List<PhotonView> __result)
            {
                if (Instance == null ||
                    !Instance
                        .ShouldBlockOriginalSpawner(
                            __instance))
                {
                    return true;
                }

                __result =
                    new List<PhotonView>();

                return false;
            }
        }

        /// <summary>
        /// 정상 모닥불 진행은 MapHandler.GoToSegment를 호출합니다.
        ///
        /// 원본 DLL의 GoToSegment는 전환 코루틴을 시작하고,
        /// 그 내부에서 다음 세그먼트를 활성화한 뒤 ISpawner.TrySpawnItems를 호출합니다.
        /// Prefix에서는 전환 사실만 등록하며 아직 Spawner를 차단하지 않습니다.
        /// </summary>
        [HarmonyPatch(
            typeof(MapHandler),
            nameof(MapHandler.GoToSegment))]
        private static class
            MapHandlerGoToSegmentPatch
        {
            [HarmonyPrefix]
            private static void Prefix(
                MapHandler __instance,
                Segment s)
            {
                if (Instance == null)
                {
                    return;
                }

                Instance
                    .BeginSegmentTransitionWatch(
                        __instance,
                        s);
            }
        }

        /// <summary>
        /// 혹시 씬에 고정 배치된 일반 Luggage가 정리되기 전에 상호작용되어도
        /// 차단 시간에는 열리지 않게 합니다. RespawnChest는 제외합니다.
        /// </summary>
        [HarmonyPatch(
            typeof(Luggage),
            nameof(Luggage.Interact_CastFinished))]
        private static class
            LuggageInteractFinishedPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(
                Luggage __instance)
            {
                if (Instance == null ||
                    !Instance
                        .IsSpawnerBlockCurrentlyActive())
                {
                    return true;
                }

                return
                    __instance is
                        RespawnChest;
            }
        }
    }
}
