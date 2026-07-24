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
        public const string PluginVersion = "1.0.1";

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

        private readonly HashSet<int> scheduledItemInstanceIds =
            new HashSet<int>();

        private Harmony harmony;
        private bool blockGameplaySpawns;
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
                "Craft PEAK Delete 1.0.1 loaded. " +
                "Original gameplay item spawns will be blocked.");
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= HandleSceneLoaded;

            if (harmony != null)
            {
                harmony.UnpatchSelf();
                harmony = null;
            }

            scheduledItemInstanceIds.Clear();

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
            scheduledItemInstanceIds.Clear();

            loadedSceneHandle = scene.handle;
            blockGameplaySpawns = IsGameplayScene(scene);

            if (!blockGameplaySpawns)
            {
                Logger.LogInfo(
                    "Item deletion disabled in scene: " +
                    scene.name);

                return;
            }

            Logger.LogInfo(
                "Gameplay scene detected. Blocking original item spawns: " +
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
            return blockGameplaySpawns &&
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
        /// Item.Awake 직후에는 네트워크와 물리 초기화가 끝나지 않았을 수 있으므로
        /// 다음 프레임에 다시 확인하고 제거합니다. 손이나 배낭으로 이동한 아이템은
        /// 맵 스폰 아이템이 아니므로 제거하지 않습니다.
        /// </summary>
        internal void ScheduleBlockedItemDeletion(Item item)
        {
            if (item == null ||
                !ShouldBlockOriginalSpawner() ||
                !IsBlockedItem(item))
            {
                return;
            }

            int instanceId = item.GetInstanceID();

            if (!scheduledItemInstanceIds.Add(instanceId))
            {
                return;
            }

            StartCoroutine(
                DeleteGroundItemNextFrame(
                    item,
                    instanceId));
        }

        private IEnumerator DeleteGroundItemNextFrame(
            Item item,
            int instanceId)
        {
            yield return null;

            if (item == null)
            {
                scheduledItemInstanceIds.Remove(instanceId);
                yield break;
            }

            if (!ShouldBlockOriginalSpawner() ||
                !IsBlockedItem(item) ||
                item.itemState != ItemState.Ground)
            {
                scheduledItemInstanceIds.Remove(instanceId);
                yield break;
            }

            RemoveGroundItem(item);
        }

        /// <summary>
        /// 씬 초기화 시점 차이로 남는 오브젝트를 여러 차례 정리합니다.
        /// 원본 Spawner는 Harmony Prefix에서 별도로 계속 차단됩니다.
        /// </summary>
        private IEnumerator CleanupGameplaySceneRoutine(
            int sceneHandle)
        {
            yield return null;
            CleanupScenePass(sceneHandle, "next-frame");

            yield return new WaitForSecondsRealtime(0.5f);
            CleanupScenePass(sceneHandle, "0.5-second");

            yield return new WaitForSecondsRealtime(1.5f);
            CleanupScenePass(sceneHandle, "2-second");

            yield return new WaitForSecondsRealtime(3f);
            CleanupScenePass(sceneHandle, "5-second");

            yield return new WaitForSecondsRealtime(5f);
            CleanupScenePass(sceneHandle, "10-second");
        }

        private void CleanupScenePass(
            int sceneHandle,
            string passName)
        {
            if (!ShouldBlockOriginalSpawner() ||
                SceneManager.GetActiveScene().handle != sceneHandle)
            {
                return;
            }

            int removedItems = CleanupExistingGroundItems();
            int disabledLuggage = DisableExistingLuggage();

            if (removedItems > 0 || disabledLuggage > 0)
            {
                Logger.LogInfo(
                    "Cleanup pass=" + passName +
                    " | Items=" + removedItems +
                    " | Luggage=" + disabledLuggage);
            }
        }

        private int CleanupExistingGroundItems()
        {
            int removedCount = 0;

            Item[] items =
                UnityEngine.Object.FindObjectsOfType<Item>(true);

            for (int i = 0; i < items.Length; i++)
            {
                Item item = items[i];

                if (item == null ||
                    !IsBlockedItem(item) ||
                    item.itemState != ItemState.Ground)
                {
                    continue;
                }

                RemoveGroundItem(item);
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

            int instanceId = item.GetInstanceID();
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

            scheduledItemInstanceIds.Remove(instanceId);
        }

        /// <summary>
        /// MapHandler가 호출하는 모든 원본 Spawner를 가장 앞에서 차단합니다.
        /// BerryBush, BerryVine, GroundPlaceSpawner, Luggage 등은
        /// Spawner.TrySpawnItems를 통해 진입하므로 여기서 생성되지 않습니다.
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
        /// Luggage.OpenLuggageRPC는 TrySpawnItems를 거치지 않고
        /// SpawnItems를 직접 호출하므로 이 경로도 차단합니다.
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

        /// <summary>
        /// Spawner가 아닌 경로 또는 다른 모드에서 뒤늦게 생성된 지상 아이템도
        /// 확인된 itemID라면 보조 차단합니다. 이후 Craft 전용 자원을 추가할 때는
        /// Spawn.cs에 등록된 판매용 자원 itemID는 자동으로 제외됩니다.
        /// </summary>
        [HarmonyPatch(
            typeof(Item),
            "Awake")]
        private static class ItemAwakePatch
        {
            [HarmonyPostfix]
            private static void Postfix(Item __instance)
            {
                if (Instance == null)
                {
                    return;
                }

                Instance.ScheduleBlockedItemDeletion(__instance);
            }
        }
    }
}
