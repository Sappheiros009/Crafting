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
    /// 2. MapHandler가 존재하는 실제 게임 맵에서 Spawner.TrySpawnItems를 차단합니다.
    /// 3. Luggage가 직접 호출하는 Spawner.SpawnItems도 차단합니다.
    /// 4. 씬에 미리 배치되어 있거나 다른 경로로 먼저 생성된 지상 아이템을 정리합니다.
    /// 5. RespawnChest는 부활 기능을 위해 삭제하거나 상호작용을 막지 않습니다.
    ///
    /// 이 파일 하나만 프로젝트에 추가해도 BepInEx가 별도 플러그인으로 자동 로드합니다.
    /// Reflection은 사용하지 않습니다.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class Delete : BaseUnityPlugin
    {
        public const string PluginGuid = "com.sappheiros.crafting.delete";
        public const string PluginName = "Craft PEAK Delete";
        public const string PluginVersion = "1.1.0";

        /// <summary>
        /// 사용자가 인게임에서 직접 확인한 모든 World.itemID입니다.
        /// Airport에서는 이 목록을 사용하지 않으므로 여권과 배낭도 로비에서 유지됩니다.
        /// </summary>
        private static readonly HashSet<ushort> BlockedItemIds =
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

        private readonly HashSet<int> initialFieldItemInstanceIds =
            new HashSet<int>();

        private Harmony harmony;

        // true인 동안에만 원본 필드 Spawner를 차단합니다.
        // 초기 정리 완료 뒤에는 false가 되어 제작/모드 스폰 아이템을 유지합니다.
        private bool initialCleanupActive;

        private int loadedSceneHandle = -1;

        internal static Delete Instance { get; private set; }
        internal static ManualLogSource ModLogger { get; private set; }

        private void Awake()
        {
            Instance = this;
            ModLogger = Logger;

            harmony = new Harmony(PluginGuid);
            harmony.PatchAll(typeof(Delete).Assembly);

            SceneManager.sceneLoaded += HandleSceneLoaded;

            HandleSceneLoaded(
                SceneManager.GetActiveScene(),
                LoadSceneMode.Single);

            Logger.LogInfo(
                "Craft PEAK Delete 1.1.0 loaded. " +
                "Only the initial field items will be removed. " +
                "Items crafted or spawned after initial cleanup will be preserved.");
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= HandleSceneLoaded;

            if (harmony != null)
            {
                harmony.UnpatchSelf();
                harmony = null;
            }

            initialFieldItemInstanceIds.Clear();

            if (Instance == this)
            {
                Instance = null;
            }
        }

        private void HandleSceneLoaded(
            Scene scene,
            LoadSceneMode loadSceneMode)
        {
            StopAllCoroutines();
            initialFieldItemInstanceIds.Clear();

            loadedSceneHandle = scene.handle;
            initialCleanupActive = IsGameplayScene(scene);

            if (!initialCleanupActive)
            {
                Logger.LogInfo(
                    "Item deletion disabled in scene: " +
                    scene.name);

                return;
            }

            Logger.LogInfo(
                "Gameplay scene detected. Starting one-time initial field cleanup: " +
                scene.name);

            StartCoroutine(
                CleanupGameplaySceneRoutine(
                    scene.handle));
        }

        private static bool IsGameplayScene(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded)
            {
                return false;
            }

            if (string.Equals(
                    scene.name,
                    "Airport",
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            MapHandler mapHandler =
                UnityEngine.Object.FindObjectOfType<MapHandler>();

            return mapHandler != null;
        }

        internal bool ShouldBlockOriginalSpawner()
        {
            return initialCleanupActive &&
                   loadedSceneHandle ==
                   SceneManager.GetActiveScene().handle;
        }

        internal static bool IsBlockedItem(Item item)
        {
            return item != null &&
                   BlockedItemIds.Contains(item.itemID) &&
                   !Spawn.IsSaleResourceId(item.itemID);
        }

        /// <summary>
        /// 씬 초기화 시점 차이로 남는 오브젝트를 여러 차례 정리합니다.
        /// 원본 Spawner는 Harmony Prefix에서 별도로 계속 차단됩니다.
        /// </summary>
        private IEnumerator CleanupGameplaySceneRoutine(
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
                    2f);

            if (!IsCurrentCleanupScene(
                    sceneHandle))
            {
                yield break;
            }

            // 첫 스냅샷에 포함된 비활성 오브젝트가 뒤늦게 활성화된 경우만 재정리합니다.
            int lateRemoved =
                RemoveCapturedInitialFieldItems();

            initialCleanupActive =
                false;

            initialFieldItemInstanceIds.Clear();

            Logger.LogInfo(
                "Initial field cleanup finished. " +
                "LateRemoved=" +
                lateRemoved +
                " | Original spawner block released=True. " +
                "All subsequently crafted or spawned items will be preserved.");
        }

        private bool IsCurrentCleanupScene(
            int sceneHandle)
        {
            return initialCleanupActive &&
                   loadedSceneHandle ==
                       sceneHandle &&
                   SceneManager.GetActiveScene()
                       .handle ==
                       sceneHandle;
        }

        private void CaptureInitialFieldItems()
        {
            Item[] items =
                UnityEngine.Object.FindObjectsByType<Item>(
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
                UnityEngine.Object.FindObjectsByType<Item>(
                    FindObjectsInactive.Include,
                    FindObjectsSortMode.None);

            for (int i = 0;
                 i < items.Length;
                 i++)
            {
                Item item =
                    items[i];

                if (item == null ||
                    !initialFieldItemInstanceIds.Contains(
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

            return removedCount;
        }

        private int DisableExistingLuggage()
        {
            int disabledCount = 0;

            Luggage[] luggageObjects =
                UnityEngine.Object.FindObjectsOfType<Luggage>(true);

            for (int i = 0; i < luggageObjects.Length; i++)
            {
                Luggage luggage = luggageObjects[i];

                if (luggage == null ||
                    luggage is RespawnChest ||
                    !luggage.gameObject.activeSelf)
                {
                    continue;
                }

                luggage.gameObject.SetActive(false);
                disabledCount++;
            }

            return disabledCount;
        }

        private void RemoveGroundItem(Item item)
        {
            if (item == null ||
                item.itemState != ItemState.Ground)
            {
                return;
            }

            string objectName = item.gameObject.name;
            ushort itemId = item.itemID;

            // 모든 클라이언트에서 즉시 보이지 않고 상호작용되지 않게 합니다.
            item.gameObject.SetActive(false);

            PhotonView photonView = item.photonView;

            if (PhotonNetwork.InRoom &&
                PhotonNetwork.IsMasterClient &&
                photonView != null &&
                photonView.ViewID != 0)
            {
                try
                {
                    PhotonNetwork.Destroy(item.gameObject);
                }
                catch (Exception exception)
                {
                    Logger.LogWarning(
                        "Photon item destroy failed. Falling back to local destroy. " +
                        "Object=" + objectName +
                        " | ItemID=" + itemId +
                        " | Error=" + exception.Message);

                    UnityEngine.Object.Destroy(item.gameObject);
                }
            }
            else if (!PhotonNetwork.InRoom ||
                     photonView == null ||
                     photonView.ViewID == 0)
            {
                UnityEngine.Object.Destroy(item.gameObject);
            }

        }

        /// <summary>
        /// 초기 필드 정리 중에만 원본 Spawner를 차단합니다.
        /// 초기 정리가 끝나면 Prefix가 원본 실행을 허용하므로
        /// 이후 제작·모드 스폰·게임 내 신규 스폰 아이템은 유지됩니다.
        /// </summary>
        [HarmonyPatch(
            typeof(Spawner),
            nameof(Spawner.TrySpawnItems))]
        private static class SpawnerTrySpawnItemsPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(
                ref List<PhotonView> __result)
            {
                if (Instance == null ||
                    !Instance.ShouldBlockOriginalSpawner())
                {
                    return true;
                }

                __result = new List<PhotonView>();
                return false;
            }
        }

        /// <summary>
        /// 초기 필드 정리 중 Luggage.OpenLuggageRPC가 직접 호출하는
        /// SpawnItems 경로만 잠시 차단합니다.
        /// RespawnChest의 부활 로직은 RespawnChest.SpawnItems 오버라이드에서
        /// 먼저 처리되므로 유지됩니다. 아이템을 꺼내는 base.SpawnItems만 막힙니다.
        /// </summary>
        [HarmonyPatch(
            typeof(Spawner),
            nameof(Spawner.SpawnItems))]
        private static class SpawnerSpawnItemsPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(
                ref List<PhotonView> __result)
            {
                if (Instance == null ||
                    !Instance.ShouldBlockOriginalSpawner())
                {
                    return true;
                }

                __result = new List<PhotonView>();
                return false;
            }
        }

        /// <summary>
        /// 혹시 씬에 고정 배치된 일반 Luggage가 정리되기 전에 상호작용되어도
        /// 열리지 않게 합니다. RespawnChest는 제외합니다.
        /// </summary>
        [HarmonyPatch(
            typeof(Luggage),
            nameof(Luggage.Interact_CastFinished))]
        private static class LuggageInteractFinishedPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(Luggage __instance)
            {
                if (Instance == null ||
                    !Instance.ShouldBlockOriginalSpawner())
                {
                    return true;
                }

                return __instance is RespawnChest;
            }
        }

    }
}
