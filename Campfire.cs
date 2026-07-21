// CAMPFIRE MATERIAL GATE + MODCONFIG BUILD 1.1.0
//
// 기능
// - 일반 모닥불 점화 조건은 ModConfig에서 조절할 수 있습니다.
//     나뭇가지  ItemID 28
//     돌        ItemID 72
//     횃불      ItemID 109
// - 횃불은 맵 자원으로 추가하지 않습니다. 상점에서만 지급하는 전제를 유지합니다.
// - 재료는 상호작용한 플레이어 한 명의 인벤토리에 몰려 있을 필요가 없습니다.
// - 접속 중인 모든 플레이어의 다음 저장 위치를 합산합니다.
//     일반 인벤토리 슬롯
//     임시 슬롯
//     착용한 배낭 내부 슬롯
// - 호스트가 ModConfig 요구 수량을 검증하고 필요한 개수만큼 소비합니다.
// - 재료 소비가 성공한 경우에만 PEAK 원본 Light_Rpc를 실행합니다.
// - 기존 "모든 생존 플레이어가 모닥불 근처에 있어야 한다"는 조건을 유지합니다.
// - 이미 켜진 모닥불의 요리 기능은 변경하지 않습니다.
// - 최종 Pyre는 건드리지 않습니다.
// - Airport, Title, Pretitle에서는 작동하지 않습니다.
//
// 중요
// - Delete.cs가 PatchAll(typeof(Delete).Assembly)을 실행하므로
//   Campfire.cs에서 Harmony.PatchAll을 다시 실행하지 않습니다.
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
        InventoryStack.PluginGuid,
        BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency(
        "com.github.PEAKModding.PEAKLib.ModConfig",
        BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class CampfireGate :
        BaseUnityPlugin,
        IOnEventCallback
    {
        public const string PluginGuid =
            "com.sappheiros.crafting.campfire";

        public const string PluginName =
            "Craft PEAK Campfire Materials";

        public const string PluginVersion =
            "1.1.0";

        public const ushort FireWoodItemId = 28;
        public const ushort StoneItemId = 72;
        public const ushort TorchItemId = 109;

        private const int DefaultRequiredFireWoodCount = 1;
        private const int DefaultRequiredStoneCount = 1;
        private const int DefaultRequiredTorchCount = 1;

        private const bool DefaultRequireEveryoneInRange = true;
        private const float DefaultEveryoneInRangeDistance = 15f;
        private const float DefaultMaximumRequesterDistance = 4f;

        private const int MaximumConfigMaterialCount = 20;
        private const float MinimumConfigDistance = 1f;
        private const float MaximumConfigDistance = 50f;

        public static int RequiredFireWoodCount
        {
            get;
            private set;
        } =
            DefaultRequiredFireWoodCount;

        public static int RequiredStoneCount
        {
            get;
            private set;
        } =
            DefaultRequiredStoneCount;

        public static int RequiredTorchCount
        {
            get;
            private set;
        } =
            DefaultRequiredTorchCount;

        public static bool RequireEveryoneInRange
        {
            get;
            private set;
        } =
            DefaultRequireEveryoneInRange;

        public static float EveryoneInRangeDistance
        {
            get;
            private set;
        } =
            DefaultEveryoneInRangeDistance;

        public static float MaximumRequesterDistance
        {
            get;
            private set;
        } =
            DefaultMaximumRequesterDistance;

        private static ConfigEntry<int>
            requiredFireWoodCountConfig;

        private static ConfigEntry<int>
            requiredStoneCountConfig;

        private static ConfigEntry<int>
            requiredTorchCountConfig;

        private static ConfigEntry<bool>
            requireEveryoneInRangeConfig;

        private static ConfigEntry<float>
            everyoneInRangeDistanceConfig;

        private static ConfigEntry<float>
            maximumRequesterDistanceConfig;

        private const byte IgniteRequestEventCode = 212;
        private const byte IgniteResultEventCode = 213;
        private const byte ConsumedSelectedSlotEventCode = 214;

        private readonly HashSet<int> committedCampfireViewIds =
            new HashSet<int>();

        internal static CampfireGate Instance
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

        private sealed class IngredientLocation
        {
            public global::Player Player;
            public Character Character;
            public ItemSlot Slot;

            public bool IsBackpackInternal;
            public byte ExternalSlotId;
            public int BackpackSlotIndex;

            public ushort ItemId;
            public int AvailableCount;
        }

        private sealed class IngredientPlan
        {
            public readonly List<IngredientLocation>
                FireWood =
                    new List<IngredientLocation>();

            public readonly List<IngredientLocation>
                Stone =
                    new List<IngredientLocation>();

            public readonly List<IngredientLocation>
                Torch =
                    new List<IngredientLocation>();
        }

        private struct ConsumedSelectedSlot
        {
            public int ActorNumber;
            public int SlotId;
        }

        private void Awake()
        {
            Instance =
                this;

            ModLogger =
                Logger;

            BindCampfireConfig();

            Enabled =
                true;

            SceneManager.sceneLoaded +=
                HandleSceneLoaded;

            LogCurrentCampfireConditions(
                "Loaded");
        }

        private void BindCampfireConfig()
        {
            requiredFireWoodCountConfig =
                Config.Bind(
                    "01. 캠프파이어 재료 조건",
                    "나뭇가지 요구 수량",
                    DefaultRequiredFireWoodCount,
                    new ConfigDescription(
                        "모닥불 하나를 점화할 때 소비할 나뭇가지 수량입니다. " +
                        "0으로 설정하면 나뭇가지를 요구하거나 소비하지 않습니다.",
                        new AcceptableValueRange<int>(
                            0,
                            MaximumConfigMaterialCount)));

            requiredStoneCountConfig =
                Config.Bind(
                    "01. 캠프파이어 재료 조건",
                    "돌 요구 수량",
                    DefaultRequiredStoneCount,
                    new ConfigDescription(
                        "모닥불 하나를 점화할 때 소비할 돌 수량입니다. " +
                        "0으로 설정하면 돌을 요구하거나 소비하지 않습니다.",
                        new AcceptableValueRange<int>(
                            0,
                            MaximumConfigMaterialCount)));

            requiredTorchCountConfig =
                Config.Bind(
                    "01. 캠프파이어 재료 조건",
                    "횃불 요구 수량",
                    DefaultRequiredTorchCount,
                    new ConfigDescription(
                        "모닥불 하나를 점화할 때 소비할 횃불 수량입니다. " +
                        "0으로 설정하면 횃불을 요구하거나 소비하지 않습니다.",
                        new AcceptableValueRange<int>(
                            0,
                            MaximumConfigMaterialCount)));

            requireEveryoneInRangeConfig =
                Config.Bind(
                    "02. 캠프파이어 집결 조건",
                    "모든 생존 플레이어 집결 필요",
                    DefaultRequireEveryoneInRange,
                    "활성화하면 기존 PEAK처럼 모든 생존 플레이어가 모닥불 근처에 모여야 점화할 수 있습니다.");

            everyoneInRangeDistanceConfig =
                Config.Bind(
                    "02. 캠프파이어 집결 조건",
                    "집결 판정 거리",
                    DefaultEveryoneInRangeDistance,
                    new ConfigDescription(
                        "모든 생존 플레이어 집결 조건에 사용하는 모닥불 중심 거리입니다.",
                        new AcceptableValueRange<float>(
                            MinimumConfigDistance,
                            MaximumConfigDistance)));

            maximumRequesterDistanceConfig =
                Config.Bind(
                    "02. 캠프파이어 집결 조건",
                    "점화 요청 허용 거리",
                    DefaultMaximumRequesterDistance,
                    new ConfigDescription(
                        "상호작용한 플레이어가 호스트 검증 시 모닥불에서 떨어질 수 있는 최대 거리입니다.",
                        new AcceptableValueRange<float>(
                            MinimumConfigDistance,
                            15f)));

            requiredFireWoodCountConfig.SettingChanged +=
                HandleCampfireConfigChanged;

            requiredStoneCountConfig.SettingChanged +=
                HandleCampfireConfigChanged;

            requiredTorchCountConfig.SettingChanged +=
                HandleCampfireConfigChanged;

            requireEveryoneInRangeConfig.SettingChanged +=
                HandleCampfireConfigChanged;

            everyoneInRangeDistanceConfig.SettingChanged +=
                HandleCampfireConfigChanged;

            maximumRequesterDistanceConfig.SettingChanged +=
                HandleCampfireConfigChanged;

            ApplyCampfireConfigValues();
        }

        private static void HandleCampfireConfigChanged(
            object sender,
            EventArgs eventArgs)
        {
            ApplyCampfireConfigValues();
            LogCurrentCampfireConditions(
                "Config changed");
        }

        private static void ApplyCampfireConfigValues()
        {
            RequiredFireWoodCount =
                requiredFireWoodCountConfig != null
                    ? Mathf.Clamp(
                        requiredFireWoodCountConfig.Value,
                        0,
                        MaximumConfigMaterialCount)
                    : DefaultRequiredFireWoodCount;

            RequiredStoneCount =
                requiredStoneCountConfig != null
                    ? Mathf.Clamp(
                        requiredStoneCountConfig.Value,
                        0,
                        MaximumConfigMaterialCount)
                    : DefaultRequiredStoneCount;

            RequiredTorchCount =
                requiredTorchCountConfig != null
                    ? Mathf.Clamp(
                        requiredTorchCountConfig.Value,
                        0,
                        MaximumConfigMaterialCount)
                    : DefaultRequiredTorchCount;

            RequireEveryoneInRange =
                requireEveryoneInRangeConfig != null
                    ? requireEveryoneInRangeConfig.Value
                    : DefaultRequireEveryoneInRange;

            EveryoneInRangeDistance =
                everyoneInRangeDistanceConfig != null
                    ? Mathf.Clamp(
                        everyoneInRangeDistanceConfig.Value,
                        MinimumConfigDistance,
                        MaximumConfigDistance)
                    : DefaultEveryoneInRangeDistance;

            MaximumRequesterDistance =
                maximumRequesterDistanceConfig != null
                    ? Mathf.Clamp(
                        maximumRequesterDistanceConfig.Value,
                        MinimumConfigDistance,
                        15f)
                    : DefaultMaximumRequesterDistance;
        }

        private static void LogCurrentCampfireConditions(
            string reason)
        {
            if (ModLogger == null)
            {
                return;
            }

            ModLogger.LogInfo(
                PluginName +
                " " +
                PluginVersion +
                " conditions applied. " +
                "Reason=" +
                reason +
                " | FireWood(" +
                FireWoodItemId +
                ") x" +
                RequiredFireWoodCount +
                " | Stone(" +
                StoneItemId +
                ") x" +
                RequiredStoneCount +
                " | Torch(" +
                TorchItemId +
                ") x" +
                RequiredTorchCount +
                " | RequireEveryone=" +
                RequireEveryoneInRange +
                " | EveryoneRange=" +
                EveryoneInRangeDistance +
                " | RequesterRange=" +
                MaximumRequesterDistance +
                ". Host settings are authoritative.");
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

            UnbindCampfireConfigEvents();

            committedCampfireViewIds.Clear();

            if (Instance == this)
            {
                Instance = null;
            }

            ModLogger = null;
        }

        private static void UnbindCampfireConfigEvents()
        {
            if (requiredFireWoodCountConfig != null)
            {
                requiredFireWoodCountConfig.SettingChanged -=
                    HandleCampfireConfigChanged;
            }

            if (requiredStoneCountConfig != null)
            {
                requiredStoneCountConfig.SettingChanged -=
                    HandleCampfireConfigChanged;
            }

            if (requiredTorchCountConfig != null)
            {
                requiredTorchCountConfig.SettingChanged -=
                    HandleCampfireConfigChanged;
            }

            if (requireEveryoneInRangeConfig != null)
            {
                requireEveryoneInRangeConfig.SettingChanged -=
                    HandleCampfireConfigChanged;
            }

            if (everyoneInRangeDistanceConfig != null)
            {
                everyoneInRangeDistanceConfig.SettingChanged -=
                    HandleCampfireConfigChanged;
            }

            if (maximumRequesterDistanceConfig != null)
            {
                maximumRequesterDistanceConfig.SettingChanged -=
                    HandleCampfireConfigChanged;
            }

            requiredFireWoodCountConfig = null;
            requiredStoneCountConfig = null;
            requiredTorchCountConfig = null;
            requireEveryoneInRangeConfig = null;
            everyoneInRangeDistanceConfig = null;
            maximumRequesterDistanceConfig = null;
        }

        private void HandleSceneLoaded(
            Scene scene,
            LoadSceneMode loadSceneMode)
        {
            committedCampfireViewIds.Clear();

            if (IsExcludedScene(
                    scene))
            {
                Logger.LogInfo(
                    "Campfire material gate disabled in scene: " +
                    scene.name);

                return;
            }

            Logger.LogInfo(
                "Campfire material gate enabled in scene: " +
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

        internal static bool IsManagedCampfire(
            global::Campfire campfire)
        {
            return
                IsGameplayActive() &&
                campfire != null &&
                !campfire.isPyre;
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

        internal void RequestIgnition(
            global::Campfire campfire,
            Character interactor)
        {
            if (!IsManagedCampfire(
                    campfire) ||
                campfire.Lit ||
                campfire.state !=
                    global::Campfire.FireState.Off)
            {
                return;
            }

            if (!PhotonNetwork.InRoom)
            {
                NotifyLocalPlayer(
                    "모닥불 점화 실패: 네트워크 방에 연결되어 있지 않습니다.");

                return;
            }

            if (interactor == null ||
                interactor.photonView == null ||
                !interactor.IsLocal)
            {
                NotifyLocalPlayer(
                    "모닥불 점화 실패: 상호작용 플레이어를 확인할 수 없습니다.");

                return;
            }

            PhotonView campfireView =
                campfire.GetComponent<PhotonView>();

            if (campfireView == null ||
                campfireView.ViewID <= 0)
            {
                NotifyLocalPlayer(
                    "모닥불 점화 실패: 모닥불 네트워크 정보를 찾지 못했습니다.");

                return;
            }

            int requesterActorNumber =
                interactor.photonView.Owner != null
                    ? interactor.photonView.Owner.ActorNumber
                    : -1;

            if (requesterActorNumber <= 0)
            {
                NotifyLocalPlayer(
                    "모닥불 점화 실패: 플레이어 네트워크 번호를 찾지 못했습니다.");

                return;
            }

            object[] requestData =
            {
                campfireView.ViewID
            };

            if (PhotonNetwork.IsMasterClient)
            {
                ProcessIgniteRequestOnHost(
                    requesterActorNumber,
                    requestData);

                return;
            }

            RaiseEventOptions options =
                new RaiseEventOptions
                {
                    Receivers =
                        ReceiverGroup.MasterClient
                };

            bool sent =
                PhotonNetwork.RaiseEvent(
                    IgniteRequestEventCode,
                    requestData,
                    options,
                    SendOptions.SendReliable);

            if (!sent)
            {
                NotifyLocalPlayer(
                    "모닥불 점화 요청 전송에 실패했습니다.");
            }
            else
            {
                Logger.LogInfo(
                    "Campfire ignite request sent to host. " +
                    "Actor=" +
                    requesterActorNumber +
                    " | CampfireViewID=" +
                    campfireView.ViewID);
            }
        }

        public void OnEvent(
            EventData photonEvent)
        {
            if (photonEvent == null)
            {
                return;
            }

            if (photonEvent.Code ==
                IgniteRequestEventCode)
            {
                if (!PhotonNetwork.IsMasterClient)
                {
                    return;
                }

                ProcessIgniteRequestOnHost(
                    photonEvent.Sender,
                    photonEvent.CustomData as
                        object[]);

                return;
            }

            if (photonEvent.Code ==
                IgniteResultEventCode)
            {
                HandleIgniteResult(
                    photonEvent.CustomData as
                        object[]);

                return;
            }

            if (photonEvent.Code ==
                ConsumedSelectedSlotEventCode)
            {
                HandleConsumedSelectedSlots(
                    photonEvent.CustomData as
                        object[]);
            }
        }

        private void ProcessIgniteRequestOnHost(
            int requesterActorNumber,
            object[] requestData)
        {
            if (!PhotonNetwork.IsMasterClient)
            {
                return;
            }

            if (!IsGameplayActive())
            {
                SendIgniteResult(
                    requesterActorNumber,
                    false,
                    "현재 씬에서는 모닥불 재료 기능이 작동하지 않습니다.");

                return;
            }

            if (requestData == null ||
                requestData.Length < 1)
            {
                SendIgniteResult(
                    requesterActorNumber,
                    false,
                    "잘못된 모닥불 점화 요청입니다.");

                return;
            }

            int campfireViewId;

            try
            {
                campfireViewId =
                    Convert.ToInt32(
                        requestData[0]);
            }
            catch (Exception)
            {
                SendIgniteResult(
                    requesterActorNumber,
                    false,
                    "모닥불 네트워크 정보를 해석하지 못했습니다.");

                return;
            }

            if (campfireViewId <= 0)
            {
                SendIgniteResult(
                    requesterActorNumber,
                    false,
                    "잘못된 모닥불 네트워크 번호입니다.");

                return;
            }

            if (committedCampfireViewIds.Contains(
                    campfireViewId))
            {
                SendIgniteResult(
                    requesterActorNumber,
                    false,
                    "이 모닥불은 이미 점화 처리 중이거나 켜져 있습니다.");

                return;
            }

            PhotonView campfireView =
                PhotonView.Find(
                    campfireViewId);

            if (campfireView == null)
            {
                SendIgniteResult(
                    requesterActorNumber,
                    false,
                    "모닥불을 찾을 수 없습니다.");

                return;
            }

            global::Campfire campfire =
                campfireView.GetComponent<
                    global::Campfire>();

            if (!IsManagedCampfire(
                    campfire))
            {
                SendIgniteResult(
                    requesterActorNumber,
                    false,
                    "이 모닥불은 재료 조건 적용 대상이 아닙니다.");

                return;
            }

            if (campfire.Lit ||
                campfire.state !=
                    global::Campfire.FireState.Off)
            {
                SendIgniteResult(
                    requesterActorNumber,
                    false,
                    "이 모닥불은 이미 켜졌거나 사용할 수 없습니다.");

                return;
            }

            global::Player requester =
                PlayerHandler.GetPlayer(
                    requesterActorNumber);

            Character requesterCharacter =
                requester != null
                    ? requester.character
                    : null;

            if (requester == null ||
                requesterCharacter == null ||
                requesterCharacter.data == null ||
                requesterCharacter.data.dead)
            {
                SendIgniteResult(
                    requesterActorNumber,
                    false,
                    "점화를 요청한 플레이어를 확인할 수 없습니다.");

                return;
            }

            float requesterDistance =
                Vector3.Distance(
                    campfire.Center(),
                    requesterCharacter.Center);

            if (requesterDistance >
                MaximumRequesterDistance)
            {
                SendIgniteResult(
                    requesterActorNumber,
                    false,
                    "모닥불에서 너무 멀리 떨어져 있습니다.");

                return;
            }

            if (RequireEveryoneInRange)
            {
                string rangeMessage;

                if (!campfire.EveryoneInRange(
                        out rangeMessage,
                        EveryoneInRangeDistance))
                {
                    string notification =
                        string.IsNullOrEmpty(
                            rangeMessage)
                            ? "모든 생존 플레이어가 모닥불 근처에 모여야 합니다."
                            : StripRichTextForNotification(
                                rangeMessage);

                    SendIgniteResult(
                        requesterActorNumber,
                        false,
                        notification);

                    return;
                }
            }

            IngredientPlan plan;
            string missingMessage;

            if (!TryCreateIngredientPlan(
                    out plan,
                    out missingMessage))
            {
                SendIgniteResult(
                    requesterActorNumber,
                    false,
                    missingMessage);

                Logger.LogInfo(
                    "Campfire ignite denied: missing materials. " +
                    "Actor=" +
                    requesterActorNumber +
                    " | CampfireViewID=" +
                    campfireViewId +
                    " | " +
                    BuildMaterialCountLog());

                return;
            }

            committedCampfireViewIds.Add(
                campfireViewId);

            List<ConsumedSelectedSlot>
                consumedSelectedSlots;

            bool consumed =
                TryConsumeIngredientPlan(
                    plan,
                    out consumedSelectedSlots);

            if (!consumed)
            {
                committedCampfireViewIds.Remove(
                    campfireViewId);

                SendIgniteResult(
                    requesterActorNumber,
                    false,
                    "재료 소비 중 오류가 발생했습니다. 재료는 소비되지 않았습니다.");

                return;
            }

            BroadcastConsumedSelectedSlots(
                consumedSelectedSlots);

            campfireView.RPC(
                "Light_Rpc",
                RpcTarget.All,
                Array.Empty<object>());

            SendIgniteResult(
                requesterActorNumber,
                true,
                "모닥불 점화 성공\n" +
                BuildConsumedMaterialMessage());

            Logger.LogInfo(
                "Campfire ignition approved by host. " +
                "Actor=" +
                requesterActorNumber +
                " | CampfireViewID=" +
                campfireViewId +
                " | Consumed: FireWood=" +
                RequiredFireWoodCount +
                ", Stone=" +
                RequiredStoneCount +
                ", Torch=" +
                RequiredTorchCount +
                ".");
        }

        private static bool TryCreateIngredientPlan(
            out IngredientPlan plan,
            out string missingMessage)
        {
            plan =
                new IngredientPlan();

            List<IngredientLocation> locations =
                CollectAllIngredientLocations();

            int fireWoodCount =
                CountAvailableUnits(
                    locations,
                    FireWoodItemId);

            int stoneCount =
                CountAvailableUnits(
                    locations,
                    StoneItemId);

            int torchCount =
                CountAvailableUnits(
                    locations,
                    TorchItemId);

            bool hasAll =
                fireWoodCount >=
                    RequiredFireWoodCount &&
                stoneCount >=
                    RequiredStoneCount &&
                torchCount >=
                    RequiredTorchCount;

            if (!hasAll)
            {
                missingMessage =
                    "모닥불 점화 재료가 부족합니다.\n" +
                    BuildMaterialProgressText(
                        fireWoodCount,
                        stoneCount,
                        torchCount);

                return false;
            }

            bool fireWoodPlanned =
                TryAppendIngredientUnits(
                    locations,
                    FireWoodItemId,
                    RequiredFireWoodCount,
                    plan.FireWood);

            bool stonePlanned =
                TryAppendIngredientUnits(
                    locations,
                    StoneItemId,
                    RequiredStoneCount,
                    plan.Stone);

            bool torchPlanned =
                TryAppendIngredientUnits(
                    locations,
                    TorchItemId,
                    RequiredTorchCount,
                    plan.Torch);

            if (!fireWoodPlanned ||
                !stonePlanned ||
                !torchPlanned)
            {
                missingMessage =
                    "재료 목록을 구성하는 동안 인벤토리가 변경되었습니다. 다시 시도하세요.";

                return false;
            }

            missingMessage =
                string.Empty;

            return true;
        }

        private static bool TryAppendIngredientUnits(
            List<IngredientLocation> locations,
            ushort itemId,
            int requiredCount,
            List<IngredientLocation> destination)
        {
            if (destination == null)
            {
                return false;
            }

            destination.Clear();

            if (requiredCount <= 0)
            {
                return true;
            }

            List<IngredientLocation> matching =
                new List<IngredientLocation>();

            for (int i = 0;
                 i < locations.Count;
                 i++)
            {
                IngredientLocation location =
                    locations[i];

                if (location != null &&
                    location.ItemId ==
                        itemId &&
                    location.AvailableCount >
                        0)
                {
                    matching.Add(
                        location);
                }
            }

            matching.Sort(
                CompareIngredientLocations);

            int remaining =
                requiredCount;

            for (int i = 0;
                 i < matching.Count &&
                 remaining > 0;
                 i++)
            {
                IngredientLocation location =
                    matching[i];

                int unitsFromLocation =
                    Mathf.Min(
                        location.AvailableCount,
                        remaining);

                for (int unitIndex = 0;
                     unitIndex <
                         unitsFromLocation;
                     unitIndex++)
                {
                    destination.Add(
                        location);
                }

                remaining -=
                    unitsFromLocation;
            }

            return remaining <= 0;
        }

        private static int CompareIngredientLocations(
            IngredientLocation left,
            IngredientLocation right)
        {
            int priorityComparison =
                GetConsumptionPriority(
                    left)
                .CompareTo(
                    GetConsumptionPriority(
                        right));

            if (priorityComparison != 0)
            {
                return priorityComparison;
            }

            int leftActor =
                GetActorNumber(
                    left);

            int rightActor =
                GetActorNumber(
                    right);

            int actorComparison =
                leftActor.CompareTo(
                    rightActor);

            if (actorComparison != 0)
            {
                return actorComparison;
            }

            if (left.IsBackpackInternal !=
                right.IsBackpackInternal)
            {
                return left.IsBackpackInternal
                    ? -1
                    : 1;
            }

            if (left.IsBackpackInternal)
            {
                return left.BackpackSlotIndex.CompareTo(
                    right.BackpackSlotIndex);
            }

            return left.ExternalSlotId.CompareTo(
                right.ExternalSlotId);
        }

        private static int GetActorNumber(
            IngredientLocation location)
        {
            if (location == null ||
                location.Character == null ||
                location.Character.photonView == null ||
                location.Character.photonView.Owner == null)
            {
                return int.MaxValue;
            }

            return location.Character
                .photonView
                .Owner
                .ActorNumber;
        }

        private static List<IngredientLocation>
            CollectAllIngredientLocations()
        {
            List<IngredientLocation> result =
                new List<IngredientLocation>();

            List<Character> characters =
                PlayerHandler.GetAllPlayerCharacters();

            for (int characterIndex = 0;
                 characterIndex <
                     characters.Count;
                 characterIndex++)
            {
                Character character =
                    characters[
                        characterIndex];

                if (character == null ||
                    character.player == null ||
                    character.photonView == null ||
                    character.photonView.Owner == null ||
                    character.photonView.Owner.IsInactive)
                {
                    continue;
                }

                global::Player player =
                    character.player;

                ItemSlot[] regularSlots =
                    player.itemSlots;

                if (regularSlots != null)
                {
                    for (int slotIndex = 0;
                         slotIndex <
                             regularSlots.Length;
                         slotIndex++)
                    {
                        AddIngredientLocation(
                            result,
                            player,
                            character,
                            regularSlots[
                                slotIndex],
                            false,
                            (byte)slotIndex,
                            -1);
                    }
                }

                AddIngredientLocation(
                    result,
                    player,
                    character,
                    player.tempFullSlot,
                    false,
                    250,
                    -1);

                BackpackData backpackData =
                    default(BackpackData);

                bool hasBackpackData =
                    player.backpackSlot != null &&
                    !player.backpackSlot.IsEmpty() &&
                    player.backpackSlot.data != null &&
                    player.backpackSlot.data
                        .TryGetDataEntry<
                            BackpackData>(
                            DataEntryKey.BackpackData,
                            out backpackData);

                if (!hasBackpackData ||
                    backpackData == null ||
                    backpackData.itemSlots == null)
                {
                    continue;
                }

                for (int backpackSlotIndex = 0;
                     backpackSlotIndex <
                         backpackData.itemSlots.Length;
                     backpackSlotIndex++)
                {
                    AddIngredientLocation(
                        result,
                        player,
                        character,
                        backpackData.itemSlots[
                            backpackSlotIndex],
                        true,
                        byte.MaxValue,
                        backpackSlotIndex);
                }
            }

            return result;
        }

        private static void AddIngredientLocation(
            List<IngredientLocation> locations,
            global::Player player,
            Character character,
            ItemSlot slot,
            bool isBackpackInternal,
            byte externalSlotId,
            int backpackSlotIndex)
        {
            if (locations == null ||
                player == null ||
                character == null ||
                slot == null ||
                slot.IsEmpty() ||
                slot.prefab == null)
            {
                return;
            }

            ushort itemId =
                slot.prefab.itemID;

            if (itemId !=
                    FireWoodItemId &&
                itemId !=
                    StoneItemId &&
                itemId !=
                    TorchItemId)
            {
                return;
            }

            locations.Add(
                new IngredientLocation
                {
                    Player =
                        player,

                    Character =
                        character,

                    Slot =
                        slot,

                    IsBackpackInternal =
                        isBackpackInternal,

                    ExternalSlotId =
                        externalSlotId,

                    BackpackSlotIndex =
                        backpackSlotIndex,

                    ItemId =
                        itemId,

                    AvailableCount =
                        GetLocationAvailableCount(
                            player,
                            slot,
                            isBackpackInternal,
                            externalSlotId)
                });
        }

        private static int GetLocationAvailableCount(
            global::Player player,
            ItemSlot slot,
            bool isBackpackInternal,
            byte externalSlotId)
        {
            if (slot == null ||
                slot.IsEmpty())
            {
                return 0;
            }

            if (isBackpackInternal)
            {
                return 1;
            }

            int stackCount =
                InventoryStack.GetStackCount(
                    player,
                    externalSlotId);

            return Mathf.Max(
                1,
                stackCount);
        }

        private static int GetConsumptionPriority(
            IngredientLocation location)
        {
            if (location == null)
            {
                return int.MaxValue;
            }

            // 착용한 배낭 내부 재료를 먼저 사용합니다.
            if (location.IsBackpackInternal)
            {
                return 0;
            }

            // 손에 들지 않은 일반 슬롯을 두 번째로 사용합니다.
            if (!IsCurrentlySelected(
                    location))
            {
                return 1;
            }

            // 재료가 손에 든 것밖에 없을 때만 선택 슬롯을 소비합니다.
            return 2;
        }

        private static bool IsCurrentlySelected(
            IngredientLocation location)
        {
            if (location == null ||
                location.IsBackpackInternal ||
                location.Character == null ||
                location.Character.refs == null ||
                location.Character.refs.items == null)
            {
                return false;
            }

            Optionable<byte> selectedSlot =
                location.Character.refs.items
                    .currentSelectedSlot;

            return
                selectedSlot.IsSome &&
                selectedSlot.Value ==
                    location.ExternalSlotId;
        }

        private static int CountAvailableUnits(
            List<IngredientLocation> locations,
            ushort itemId)
        {
            int count = 0;

            for (int i = 0;
                 i < locations.Count;
                 i++)
            {
                IngredientLocation location =
                    locations[i];

                if (location != null &&
                    location.ItemId ==
                        itemId)
                {
                    count +=
                        Mathf.Max(
                            0,
                            location.AvailableCount);
                }
            }

            return count;
        }

        private static bool TryConsumeIngredientPlan(
            IngredientPlan plan,
            out List<ConsumedSelectedSlot>
                consumedSelectedSlots)
        {
            consumedSelectedSlots =
                new List<ConsumedSelectedSlot>();

            if (plan == null ||
                !ValidatePlannedUnits(
                    plan.FireWood,
                    FireWoodItemId,
                    RequiredFireWoodCount) ||
                !ValidatePlannedUnits(
                    plan.Stone,
                    StoneItemId,
                    RequiredStoneCount) ||
                !ValidatePlannedUnits(
                    plan.Torch,
                    TorchItemId,
                    RequiredTorchCount))
            {
                return false;
            }

            List<IngredientLocation> all =
                new List<IngredientLocation>();

            all.AddRange(
                plan.FireWood);

            all.AddRange(
                plan.Stone);

            all.AddRange(
                plan.Torch);

            HashSet<global::Player> touchedPlayers =
                new HashSet<global::Player>();

            HashSet<Character> backpackChangedCharacters =
                new HashSet<Character>();

            HashSet<string> selectedSlotKeys =
                new HashSet<string>();

            for (int i = 0;
                 i < all.Count;
                 i++)
            {
                IngredientLocation location =
                    all[i];

                if (!IsLocationStillValid(
                        location,
                        location.ItemId))
                {
                    return false;
                }

                if (IsCurrentlySelected(
                        location) &&
                    location.Character.photonView != null &&
                    location.Character.photonView.Owner != null)
                {
                    int actorNumber =
                        location.Character
                            .photonView
                            .Owner
                            .ActorNumber;

                    string selectedKey =
                        actorNumber +
                        ":" +
                        location.ExternalSlotId;

                    if (selectedSlotKeys.Add(
                            selectedKey))
                    {
                        consumedSelectedSlots.Add(
                            new ConsumedSelectedSlot
                            {
                                ActorNumber =
                                    actorNumber,

                                SlotId =
                                    location.ExternalSlotId
                            });
                    }
                }

                location.Slot.EmptyOut();

                touchedPlayers.Add(
                    location.Player);

                if (location.IsBackpackInternal)
                {
                    backpackChangedCharacters.Add(
                        location.Character);
                }
            }

            foreach (global::Player player in
                     touchedPlayers)
            {
                SyncPlayerInventoryFromHost(
                    player);
            }

            foreach (Character character in
                     backpackChangedCharacters)
            {
                RefreshBackpackVisuals(
                    character);
            }

            RefreshAllCarryWeights(
                touchedPlayers);

            return true;
        }

        private static bool ValidatePlannedUnits(
            List<IngredientLocation> plannedUnits,
            ushort expectedItemId,
            int expectedCount)
        {
            if (expectedCount <= 0)
            {
                return
                    plannedUnits != null &&
                    plannedUnits.Count == 0;
            }

            if (plannedUnits == null ||
                plannedUnits.Count !=
                    expectedCount)
            {
                return false;
            }

            Dictionary<IngredientLocation, int>
                requiredByLocation =
                    new Dictionary<IngredientLocation, int>();

            for (int i = 0;
                 i < plannedUnits.Count;
                 i++)
            {
                IngredientLocation location =
                    plannedUnits[i];

                if (!IsLocationStillValid(
                        location,
                        expectedItemId))
                {
                    return false;
                }

                if (!requiredByLocation.ContainsKey(
                        location))
                {
                    requiredByLocation[
                        location] = 0;
                }

                requiredByLocation[
                    location]++;
            }

            foreach (
                KeyValuePair<IngredientLocation, int> pair
                in requiredByLocation)
            {
                int currentAvailable =
                    GetLocationAvailableCount(
                        pair.Key.Player,
                        pair.Key.Slot,
                        pair.Key.IsBackpackInternal,
                        pair.Key.ExternalSlotId);

                if (currentAvailable <
                    pair.Value)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsLocationStillValid(
            IngredientLocation location,
            ushort expectedItemId)
        {
            return
                location != null &&
                location.Player != null &&
                location.Character != null &&
                location.Slot != null &&
                !location.Slot.IsEmpty() &&
                location.Slot.prefab != null &&
                location.Slot.prefab.itemID ==
                    expectedItemId;
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

            if (player.itemsChangedAction != null)
            {
                player.itemsChangedAction(
                    player.itemSlots);
            }
        }

        private static void RefreshBackpackVisuals(
            Character character)
        {
            if (character == null ||
                !PhotonNetwork.IsMasterClient)
            {
                return;
            }

            CharacterBackpackHandler handler =
                character.GetComponent<
                    CharacterBackpackHandler>();

            if (handler == null ||
                handler.backpackVisuals == null)
            {
                return;
            }

            handler.backpackVisuals
                .RefreshVisuals();
        }

        private static void RefreshAllCarryWeights(
            HashSet<global::Player> touchedPlayers)
        {
            if (touchedPlayers == null)
            {
                return;
            }

            foreach (global::Player player in
                     touchedPlayers)
            {
                if (player == null ||
                    player.character == null ||
                    player.character.refs == null ||
                    player.character.refs.items == null)
                {
                    continue;
                }

                player.character.refs.items
                    .RefreshAllCharacterCarryWeight();

                break;
            }
        }

        private void BroadcastConsumedSelectedSlots(
            List<ConsumedSelectedSlot> consumedSlots)
        {
            if (consumedSlots == null ||
                consumedSlots.Count == 0)
            {
                return;
            }

            object[] payload =
                new object[
                    1 +
                    consumedSlots.Count *
                    2];

            payload[0] =
                consumedSlots.Count;

            for (int i = 0;
                 i < consumedSlots.Count;
                 i++)
            {
                payload[
                    1 +
                    i * 2] =
                        consumedSlots[i]
                            .ActorNumber;

                payload[
                    2 +
                    i * 2] =
                        consumedSlots[i]
                            .SlotId;
            }

            RaiseEventOptions options =
                new RaiseEventOptions
                {
                    Receivers =
                        ReceiverGroup.All
                };

            PhotonNetwork.RaiseEvent(
                ConsumedSelectedSlotEventCode,
                payload,
                options,
                SendOptions.SendReliable);
        }

        private static void HandleConsumedSelectedSlots(
            object[] payload)
        {
            if (payload == null ||
                payload.Length < 1 ||
                PhotonNetwork.LocalPlayer == null)
            {
                return;
            }

            int count;

            try
            {
                count =
                    Convert.ToInt32(
                        payload[0]);
            }
            catch (Exception)
            {
                return;
            }

            int localActorNumber =
                PhotonNetwork.LocalPlayer.ActorNumber;

            for (int i = 0;
                 i < count;
                 i++)
            {
                int actorIndex =
                    1 +
                    i * 2;

                int slotIndex =
                    2 +
                    i * 2;

                if (slotIndex >=
                    payload.Length)
                {
                    break;
                }

                int actorNumber;
                int consumedSlotId;

                try
                {
                    actorNumber =
                        Convert.ToInt32(
                            payload[
                                actorIndex]);

                    consumedSlotId =
                        Convert.ToInt32(
                            payload[
                                slotIndex]);
                }
                catch (Exception)
                {
                    continue;
                }

                if (actorNumber !=
                    localActorNumber)
                {
                    continue;
                }

                UnequipConsumedLocalSlot(
                    consumedSlotId);
            }
        }

        private static void UnequipConsumedLocalSlot(
            int consumedSlotId)
        {
            Character character =
                Character.localCharacter;

            if (character == null ||
                character.refs == null ||
                character.refs.items == null)
            {
                return;
            }

            Optionable<byte> selectedSlot =
                character.refs.items
                    .currentSelectedSlot;

            if (selectedSlot.IsNone ||
                selectedSlot.Value !=
                    (byte)consumedSlotId)
            {
                return;
            }

            character.refs.items.EquipSlot(
                Optionable<byte>.None);
        }

        private void SendIgniteResult(
            int targetActorNumber,
            bool success,
            string message)
        {
            object[] resultData =
            {
                success,
                message ?? string.Empty
            };

            if (PhotonNetwork.LocalPlayer != null &&
                PhotonNetwork.LocalPlayer.ActorNumber ==
                    targetActorNumber)
            {
                HandleIgniteResult(
                    resultData);

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

            PhotonNetwork.RaiseEvent(
                IgniteResultEventCode,
                resultData,
                options,
                SendOptions.SendReliable);
        }

        private static void HandleIgniteResult(
            object[] resultData)
        {
            if (resultData == null ||
                resultData.Length < 2)
            {
                return;
            }

            bool success;
            string message;

            try
            {
                success =
                    Convert.ToBoolean(
                        resultData[0]);

                message =
                    resultData[1] as
                    string;
            }
            catch (Exception)
            {
                return;
            }

            if (string.IsNullOrEmpty(
                    message))
            {
                message =
                    success
                        ? "모닥불을 점화했습니다."
                        : "모닥불을 점화하지 못했습니다.";
            }

            NotifyLocalPlayer(
                message);

            if (ModLogger != null)
            {
                if (success)
                {
                    ModLogger.LogInfo(
                        message.Replace(
                            "\n",
                            " | "));
                }
                else
                {
                    ModLogger.LogWarning(
                        message.Replace(
                            "\n",
                            " | "));
                }
            }
        }

        internal static void NotifyLocalPlayer(
            string message)
        {
            if (string.IsNullOrEmpty(
                    message))
            {
                return;
            }

            UI_Notifications notifications =
                UnityEngine.Object
                    .FindAnyObjectByType<
                        UI_Notifications>();

            if (notifications != null)
            {
                notifications.AddNotification(
                    message);
            }
            else if (ModLogger != null)
            {
                ModLogger.LogInfo(
                    "Notification UI not found. Message=" +
                    message.Replace(
                        "\n",
                        " | "));
            }
        }

        internal static string BuildRequirementPrompt()
        {
            List<IngredientLocation> locations =
                CollectAllIngredientLocations();

            int fireWoodCount =
                CountAvailableUnits(
                    locations,
                    FireWoodItemId);

            int stoneCount =
                CountAvailableUnits(
                    locations,
                    StoneItemId);

            int torchCount =
                CountAvailableUnits(
                    locations,
                    TorchItemId);

            bool ready =
                fireWoodCount >=
                    RequiredFireWoodCount &&
                stoneCount >=
                    RequiredStoneCount &&
                torchCount >=
                    RequiredTorchCount;

            string color =
                ready
                    ? "#79E081"
                    : "#FF8A80";

            return
                "\n<color=" +
                color +
                ">필요: " +
                BuildMaterialProgressText(
                    fireWoodCount,
                    stoneCount,
                    torchCount) +
                "</color>";
        }

        private static string BuildMaterialProgressText(
            int fireWoodCount,
            int stoneCount,
            int torchCount)
        {
            List<string> parts =
                new List<string>();

            if (RequiredFireWoodCount > 0)
            {
                parts.Add(
                    "나뭇가지 " +
                    Mathf.Min(
                        fireWoodCount,
                        RequiredFireWoodCount) +
                    "/" +
                    RequiredFireWoodCount);
            }

            if (RequiredStoneCount > 0)
            {
                parts.Add(
                    "돌 " +
                    Mathf.Min(
                        stoneCount,
                        RequiredStoneCount) +
                    "/" +
                    RequiredStoneCount);
            }

            if (RequiredTorchCount > 0)
            {
                parts.Add(
                    "횃불 " +
                    Mathf.Min(
                        torchCount,
                        RequiredTorchCount) +
                    "/" +
                    RequiredTorchCount);
            }

            if (parts.Count == 0)
            {
                return "재료 없음";
            }

            return string.Join(
                " | ",
                parts.ToArray());
        }

        private static string BuildConsumedMaterialMessage()
        {
            List<string> parts =
                new List<string>();

            if (RequiredFireWoodCount > 0)
            {
                parts.Add(
                    "나뭇가지 " +
                    RequiredFireWoodCount +
                    "개");
            }

            if (RequiredStoneCount > 0)
            {
                parts.Add(
                    "돌 " +
                    RequiredStoneCount +
                    "개");
            }

            if (RequiredTorchCount > 0)
            {
                parts.Add(
                    "횃불 " +
                    RequiredTorchCount +
                    "개");
            }

            if (parts.Count == 0)
            {
                return "재료를 소비하지 않았습니다.";
            }

            return
                string.Join(
                    ", ",
                    parts.ToArray()) +
                "를 소비했습니다.";
        }

        private static int CountGroupIngredient(
            ushort itemId)
        {
            List<IngredientLocation> locations =
                CollectAllIngredientLocations();

            return CountAvailableUnits(
                locations,
                itemId);
        }

        private static string BuildMaterialCountLog()
        {
            List<IngredientLocation> locations =
                CollectAllIngredientLocations();

            return
                "FireWood=" +
                CountAvailableUnits(
                    locations,
                    FireWoodItemId) +
                ", Stone=" +
                CountAvailableUnits(
                    locations,
                    StoneItemId) +
                ", Torch=" +
                CountAvailableUnits(
                    locations,
                    TorchItemId) +
                " | Required=[" +
                RequiredFireWoodCount +
                ", " +
                RequiredStoneCount +
                ", " +
                RequiredTorchCount +
                "]";
        }

        private static string DescribeLocation(
            IngredientLocation location)
        {
            if (location == null ||
                location.Character == null ||
                location.Character.photonView == null ||
                location.Character.photonView.Owner == null)
            {
                return "<unknown>";
            }

            string owner =
                location.Character
                    .photonView
                    .Owner
                    .NickName;

            if (location.IsBackpackInternal)
            {
                return
                    owner +
                    ":Backpack[" +
                    location.BackpackSlotIndex +
                    "]";
            }

            return
                owner +
                ":Inventory[" +
                location.ExternalSlotId +
                "]";
        }

        private static string StripRichTextForNotification(
            string value)
        {
            if (string.IsNullOrEmpty(
                    value))
            {
                return string.Empty;
            }

            string result =
                value;

            int safety = 0;

            while (safety < 32)
            {
                int openIndex =
                    result.IndexOf(
                        '<');

                if (openIndex < 0)
                {
                    break;
                }

                int closeIndex =
                    result.IndexOf(
                        '>',
                        openIndex);

                if (closeIndex < 0)
                {
                    break;
                }

                result =
                    result.Remove(
                        openIndex,
                        closeIndex -
                        openIndex +
                        1);

                safety++;
            }

            return result.Trim();
        }
    }

    /// <summary>
    /// 꺼진 일반 모닥불의 원본 점화 완료를 가로챕니다.
    ///
    /// 켜진 모닥불은 원본 요리 로직을 그대로 실행합니다.
    /// 꺼진 모닥불은 클라이언트가 직접 Light_Rpc를 보내지 못하게 막고,
    /// CampfireGate를 통해 호스트 검증 후 점화합니다.
    ///
    /// Delete.cs의 PatchAll 호출로 이 패치가 적용됩니다.
    /// </summary>
    [HarmonyPatch(
        typeof(global::Campfire),
        "Interact_CastFinished")]
    internal static class
        CampfireInteractCastFinishedPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(
            global::Campfire __instance,
            Character interactor)
        {
            if (!CampfireGate
                    .IsManagedCampfire(
                        __instance))
            {
                return true;
            }

            // 이미 켜진 모닥불의 요리 완료는 PEAK 원본 로직을 사용합니다.
            if (__instance.Lit ||
                __instance.state !=
                    global::Campfire.FireState.Off)
            {
                return true;
            }

            if (CampfireGate.Instance != null)
            {
                CampfireGate.Instance
                    .RequestIgnition(
                        __instance,
                        interactor);
            }

            // 원본의 무조건 Light_Rpc 호출은 차단합니다.
            return false;
        }
    }

    /// <summary>
    /// 모닥불 상호작용 문구 아래에 파티 전체 재료 보유 현황을 표시합니다.
    /// </summary>
    [HarmonyPatch(
        typeof(global::Campfire),
        "GetInteractionText")]
    internal static class
        CampfireGetInteractionTextPatch
    {
        [HarmonyPostfix]
        private static void Postfix(
            global::Campfire __instance,
            ref string __result)
        {
            if (!CampfireGate
                    .IsManagedCampfire(
                        __instance) ||
                __instance.Lit ||
                __instance.state !=
                    global::Campfire.FireState.Off)
            {
                return;
            }

            __result +=
                CampfireGate
                    .BuildRequirementPrompt();
        }
    }
}
