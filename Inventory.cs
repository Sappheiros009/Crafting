// INVENTORY STACK + MODCONFIG BUILD 1.1.0
//
// 기능
// - 같은 판매/제작 자원의 슬롯당 최대 수량을 ModConfig에서 조절합니다.
// - 기존 슬롯에 같은 자원이 있고 현재 최대 수량 미만이면 빈 슬롯 대신 기존 슬롯에 합칩니다.
// - 판매, 모닥불 재료 소비, 한 개 드롭 시 스택 전체가 아니라 1개만 감소합니다.
// - 스택이 1개일 때 제거하면 기존 PEAK 방식대로 슬롯이 비워집니다.
// - 슬롯 우측 아래에 현재 스택 수량을 표시합니다.
// - 수량과 최대 적재량은 Master Client가 관리하고 Photon 이벤트로 전원에게 동기화합니다.
// - 새 플레이어가 들어오면 호스트가 현재 모든 스택 수량을 전송합니다.
// - Airport, Title, Pretitle에서는 작동하지 않습니다.
//
// 스택 대상
// - 판매용 자원 11종: Spawn.IsSaleResourceId(itemID)
// - 횃불 ItemID 109
//
// 음식, 회복 아이템, 장비는 ItemInstanceData 안에 조리도, 내구도,
// 사용 횟수 등 개별 상태가 있으므로 이번 버전에서는 의도적으로 제외합니다.
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
using System.Collections.Generic;
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
            "1.1.0";

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

        private static ConfigEntry<int>
            maximumStackCountConfig;

        private const byte StackCountEventCode = 215;
        private const byte StackSnapshotEventCode = 216;
        private const byte StackConfigEventCode = 217;

        private const float PlayerRegistrationRefreshInterval =
            1f;

        private const float IgnoreDuplicateRemoveSeconds =
            1.5f;

        private readonly Dictionary<SlotKey, int> stackCounts =
            new Dictionary<SlotKey, int>();

        private readonly Dictionary<SlotKey, float>
            ignoreNextRemoteRemoveUntil =
                new Dictionary<SlotKey, float>();

        private float nextPlayerRegistrationRefreshAt;

        private int lastBroadcastMaximumStackCount = -1;

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

        private void Awake()
        {
            Instance =
                this;

            ModLogger =
                Logger;

            BindInventoryConfig();

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
                ". PEAKLib.ModConfig can display this setting when installed.");
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
            ignoreNextRemoteRemoveUntil.Clear();

            if (Instance == this)
            {
                Instance = null;
            }

            ModLogger = null;
        }

        private void Update()
        {
            if (!Enabled)
            {
                return;
            }

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

            if (!PhotonNetwork.IsMasterClient ||
                !IsGameplayActive())
            {
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
            ignoreNextRemoteRemoveUntil.Clear();

            nextPlayerRegistrationRefreshAt =
                0f;

            lastBroadcastMaximumStackCount =
                -1;

            if (!PhotonNetwork.InRoom ||
                PhotonNetwork.IsMasterClient)
            {
                ApplyLocalConfiguredMaximum(
                    "Scene loaded");
            }

            if (IsExcludedScene(
                    scene))
            {
                Logger.LogInfo(
                    "Inventory stacking disabled in scene: " +
                    scene.name);

                return;
            }

            Logger.LogInfo(
                "Inventory stacking enabled in scene: " +
                scene.name);
        }

        internal static bool IsGameplayActive()
        {
            if (!Enabled)
            {
                return false;
            }

            Scene scene =
                SceneManager.GetActiveScene();

            if (IsExcludedScene(
                    scene))
            {
                return false;
            }

            return UnityEngine.Object
                       .FindAnyObjectByType<MapHandler>() !=
                   null;
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

        public static bool IsStackableItemId(
            ushort itemId)
        {
            return
                Spawn.IsSaleResourceId(
                    itemId) ||
                itemId ==
                    TorchItemId;
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

        public static int GetStackCount(
            ItemSlot slot)
        {
            if (Instance == null ||
                slot == null ||
                slot.IsEmpty())
            {
                return 0;
            }

            global::Player player;
            byte slotId;

            if (!Instance.TryResolvePlayerSlot(
                    slot,
                    out player,
                    out slotId))
            {
                return 1;
            }

            return Instance.GetCountInternal(
                player,
                slotId);
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

            Logger.LogInfo(
                "Stack snapshot sent. TargetActor=" +
                targetActorNumber +
                " | Entries=" +
                stackCounts.Count);
        }

        public void OnEvent(
            EventData photonEvent)
        {
            if (photonEvent == null)
            {
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
            }
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
            }
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

            List<SlotKey> expired =
                new List<SlotKey>();

            foreach (
                KeyValuePair<SlotKey, float> pair
                in ignoreNextRemoteRemoveUntil)
            {
                if (Time.unscaledTime >
                    pair.Value)
                {
                    expired.Add(
                        pair.Key);
                }
            }

            for (int i = 0;
                 i < expired.Count;
                 i++)
            {
                ignoreNextRemoteRemoveUntil.Remove(
                    expired[i]);
            }
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
        private static bool Prefix(
            global::Player __instance,
            ushort itemID,
            ItemInstanceData instanceData,
            ref ItemSlot slot,
            ref bool __result)
        {
            if (InventoryStack.Instance == null ||
                !InventoryStack.IsGameplayActive() ||
                !PhotonNetwork.IsMasterClient ||
                !InventoryStack
                    .IsStackableItemId(
                        itemID))
            {
                return true;
            }

            ItemSlot mergedSlot;

            bool merged =
                InventoryStack.Instance
                    .HostTryAddToExistingStack(
                        __instance,
                        itemID,
                        out mergedSlot);

            if (!merged)
            {
                return true;
            }

            slot =
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
            ItemSlot slot,
            bool __result)
        {
            if (!__result ||
                InventoryStack.Instance == null ||
                !PhotonNetwork.IsMasterClient ||
                !InventoryStack
                    .IsStackableItemId(
                        itemID) ||
                slot == null)
            {
                return;
            }

            // Prefix에서 기존 스택을 사용한 경우에는 이미 수량을 올렸습니다.
            int count =
                InventoryStack.GetStackCount(
                    __instance,
                    slot.itemSlotID);

            if (count > 1)
            {
                return;
            }

            InventoryStack.Instance
                .HostRegisterNewSlot(
                    __instance,
                    slot,
                    itemID);
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
