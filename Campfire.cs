// CampfireGate는 MapHandler.CurrentCampfire로 지정된 꺼진 모닥불의 점화 요청을
// 마스터 클라이언트에서 검증하고, 설정된 재료를 파티 인벤토리에서 소비한 뒤 점화합니다.
//
// 플러그인 및 설정
// - 플러그인 버전은 1.1.5입니다.
// - Delete와 InventoryStack에 HardDependency를, PEAKLib.ModConfig에 SoftDependency를 선언합니다.
// - 나뭇가지(ItemID 28), 돌(ItemID 72), 횃불(ItemID 109)의 요구 수량을 각각 0~20으로 설정합니다.
// - 모든 플레이어 집결 여부, 집결 거리 1~50, 요청자 허용 거리 1~15를 설정합니다.
// - 설정 변경 시 값을 다시 제한 범위에 맞춰 적용하고 현재 조건을 로그에 기록합니다.
// - AccessTools.Method로 Campfire.Light_Rpc(bool, float)의 MethodInfo를 조회해 보관합니다.
//
// 적용 대상
// - 플러그인이 활성화되어 있고, 활성 씬이 Airport, Title, Pretitle이 아니며,
//   씬에 MapHandler가 존재할 때만 게임플레이 기능을 활성 상태로 판단합니다.
// - MapHandler.CurrentCampfire와 동일한 Campfire만 관리 대상으로 처리합니다.
// - 관리 대상이 아니거나 이미 켜졌거나 FireState.Off가 아닌 모닥불은 점화 요청을 처리하지 않습니다.
//
// 점화 요청과 호스트 검증
// - 로컬 상호작용자의 Photon ActorNumber와 모닥불 PhotonView ID를 확인합니다.
// - 마스터 클라이언트는 요청을 직접 처리하고, 그 외 클라이언트는 이벤트 170으로 마스터에게 전송합니다.
// - 마스터는 씬 상태, 요청 데이터, PhotonView, 중복 처리 잠금, 모닥불 상태,
//   요청자의 생존 상태와 거리, 선택적으로 Campfire.EveryoneInRange 결과를 검증합니다.
// - 점화 처리 중인 모닥불 View ID는 HashSet에 등록하며, 점화 실패 시 잠금을 제거합니다.
//
// 재료 검색과 소비
// - PlayerHandler.GetAllPlayerCharacters()에서 비활성 Photon 소유자를 제외한 캐릭터를 순회합니다.
// - 각 플레이어의 일반 itemSlots, tempFullSlot, 착용 배낭의 BackpackData.itemSlots를 검색합니다.
// - 일반 슬롯과 임시 슬롯의 수량은 InventoryStack.GetStackCount를 사용하고,
//   배낭 내부 슬롯은 비어 있지 않은 슬롯 하나를 1개로 계산합니다.
// - 소비 위치는 배낭 내부, 현재 선택되지 않은 외부 슬롯, 현재 선택된 외부 슬롯 순으로 정렬하고,
//   같은 우선순위에서는 ActorNumber와 슬롯 번호 순으로 재료 계획을 구성합니다.
// - 계획 수량과 현재 슬롯 상태를 다시 검증한 뒤 ItemSlot.EmptyOut으로 소비합니다.
// - 소비 후 호스트 인벤토리를 다른 클라이언트에 동기화하고, 관련 변경 콜백과 시각·무게 갱신을 호출합니다.
// - 소비된 슬롯이 로컬 캐릭터의 현재 선택 슬롯이면 이벤트 172를 통해 장착을 해제합니다.
//
// 점화 실행과 결과
// - 재료 소비가 끝나면 Campfire PhotonView에 Light_Rpc(true, 0f)를 RpcTarget.All로 호출합니다.
// - 0.25초 뒤 Lit 또는 FireState 변경 여부를 확인해 성공 여부를 판정합니다.
// - 요청자에게 직접 또는 이벤트 171로 결과를 보내고 UI_Notifications에 메시지를 표시합니다.
// - 점화 단계, 재료 현황, RPC 호출 및 검증 결과를 로그에 기록합니다.
//
// Harmony 패치
// - Campfire.Interact_CastFinished Prefix는 관리 대상의 꺼진 모닥불만 원본 호출을 차단하고
//   CampfireGate.RequestIgnition으로 점화 요청을 전달합니다.
// - 이미 켜졌거나 Off 상태가 아닌 모닥불과 관리 대상이 아닌 모닥불은 원본 메서드를 실행합니다.
// - Campfire.GetInteractionText Postfix는 꺼진 관리 대상 모닥불의 상호작용 문구에
//   파티 전체 재료 보유량과 요구량을 색상과 함께 추가합니다.

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
            "1.1.5";

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

        private static readonly System.Reflection.MethodInfo
            OriginalLightRpcMethod =
                AccessTools.Method(
                    typeof(global::Campfire),
                    "Light_Rpc",
                    new Type[]
                    {
                        typeof(bool),
                        typeof(float)
                    });

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

        // 점화 요청, 점화 결과, 소비된 선택 슬롯 알림에 사용하는 Photon 이벤트 코드입니다.
        private const byte IgniteRequestEventCode = 170;
        private const byte IgniteResultEventCode = 171;
        private const byte ConsumedSelectedSlotEventCode = 172;

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

            Logger.LogInfo(
                "Campfire Photon event codes=" +
                IgniteRequestEventCode +
                "-" +
                ConsumedSelectedSlotEventCode +
                " (Photon-safe range)");
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
            if (!IsGameplayActive() ||
                campfire == null)
            {
                return false;
            }

            try
            {
                // MapHandler가 현재 모닥불로 참조하는 인스턴스만 관리 대상으로 지정합니다.
                return
                    MapHandler.CurrentCampfire ==
                    campfire;
            }
            catch (Exception)
            {
                return false;
            }
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

            Logger.LogInfo(
                "[CampfireIgnitionDiag] Request prepared. " +
                "Actor=" +
                requesterActorNumber +
                " | CampfireViewID=" +
                campfireView.ViewID +
                " | IsMaster=" +
                PhotonNetwork.IsMasterClient +
                " | EventCode=" +
                IgniteRequestEventCode);

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

                return;
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

            Logger.LogInfo(
                "[CampfireIgnitionDiag] Host received ignition request. " +
                "Actor=" +
                requesterActorNumber +
                " | PayloadLength=" +
                (
                    requestData != null
                        ? requestData.Length
                        : 0
                ));

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

            Logger.LogInfo(
                "[CampfireIgnitionDiag] Campfire target validated. " +
                "ViewID=" +
                campfireViewId +
                " | Lit=" +
                campfire.Lit +
                " | State=" +
                campfire.state);

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

            Logger.LogInfo(
                "[CampfireIgnitionDiag] Requester distance validated. " +
                "Actor=" +
                requesterActorNumber +
                " | Distance=" +
                requesterDistance.ToString("0.00") +
                " | Maximum=" +
                MaximumRequesterDistance.ToString("0.00"));

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

            Logger.LogInfo(
                "[CampfireIgnitionDiag] Player range condition passed. " +
                "RequireEveryone=" +
                RequireEveryoneInRange +
                " | EveryoneRange=" +
                EveryoneInRangeDistance.ToString("0.00"));

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

            Logger.LogInfo(
                "[CampfireIgnitionDiag] Material condition passed. " +
                BuildMaterialCountLog());

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

            Logger.LogInfo(
                "[CampfireIgnitionDiag] All conditions passed. " +
                "Calling Light_Rpc(true, 0f). " +
                "Actor=" +
                requesterActorNumber +
                " | CampfireViewID=" +
                campfireViewId +
                " | BeforeLit=" +
                campfire.Lit +
                " | BeforeState=" +
                campfire.state);

            try
            {
                campfireView.RPC(
                    "Light_Rpc",
                    RpcTarget.All,
                    true,
                    0f);
            }
            catch (Exception exception)
            {
                committedCampfireViewIds.Remove(
                    campfireViewId);

                Logger.LogError(
                    "[CampfireIgnitionDiag] Photon Light_Rpc(true, 0f) threw an exception. " +
                    "CampfireViewID=" +
                    campfireViewId +
                    " | Exception=" +
                    exception);

                SendIgniteResult(
                    requesterActorNumber,
                    false,
                    "모닥불 점화 RPC 호출 중 예외가 발생했습니다. " +
                    "점화 잠금을 해제했습니다.");

                return;
            }

            StartCoroutine(
                VerifyCorrectArgumentRpcResult(
                    requesterActorNumber,
                    campfireViewId,
                    campfire));
        }

        private System.Collections.IEnumerator
            VerifyCorrectArgumentRpcResult(
                int requesterActorNumber,
                int campfireViewId,
                global::Campfire campfire)
        {
            yield return
                new WaitForSecondsRealtime(
                    0.25f);

            bool succeeded =
                IsCampfireActuallyLit(
                    campfire);

            Logger.LogInfo(
                "[CampfireIgnitionDiag] Light_Rpc(true, 0f) verification. " +
                "Actor=" +
                requesterActorNumber +
                " | CampfireViewID=" +
                campfireViewId +
                " | Lit=" +
                (
                    campfire != null &&
                    campfire.Lit
                ) +
                " | State=" +
                (
                    campfire != null
                        ? campfire.state.ToString()
                        : "<destroyed>"
                ) +
                " | Success=" +
                succeeded);

            if (succeeded)
            {
                SendIgniteResult(
                    requesterActorNumber,
                    true,
                    "모닥불 점화 성공\n" +
                    BuildConsumedMaterialMessage());

                Logger.LogInfo(
                    "Campfire ignition verified with correct RPC arguments. " +
                    "Actor=" +
                    requesterActorNumber +
                    " | CampfireViewID=" +
                    campfireViewId +
                    " | updateSegment=True" +
                    " | burningFor=0" +
                    " | Consumed: FireWood=" +
                    RequiredFireWoodCount +
                    ", Stone=" +
                    RequiredStoneCount +
                    ", Torch=" +
                    RequiredTorchCount +
                    ".");

                yield break;
            }

            committedCampfireViewIds.Remove(
                campfireViewId);

            SendIgniteResult(
                requesterActorNumber,
                false,
                "Light_Rpc(true, 0f)를 호출했지만 모닥불 상태가 변경되지 않았습니다. " +
                "점화 잠금을 해제했습니다.");

            Logger.LogError(
                "[CampfireIgnitionDiag] Correct-argument Light_Rpc did not activate campfire. " +
                "Actor=" +
                requesterActorNumber +
                " | CampfireViewID=" +
                campfireViewId +
                " | LockReleased=True");
        }

        private static bool IsCampfireActuallyLit(
            global::Campfire campfire)
        {
            if (campfire == null)
            {
                return false;
            }

            return
                campfire.Lit ||
                campfire.state !=
                    global::Campfire.FireState.Off;
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

            // 배낭 내부 슬롯에 있는 재료를 가장 먼저 소비하도록 정렬합니다.
            if (location.IsBackpackInternal)
            {
                return 0;
            }

            // 현재 선택되지 않은 외부 슬롯의 재료를 다음 순서로 소비합니다.
            if (!IsCurrentlySelected(
                    location))
            {
                return 1;
            }

            // 현재 선택된 외부 슬롯의 재료를 마지막 순서로 소비합니다.
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
                ModLogger.LogInfo(
                    "[CampfireIgnitionDiag] Result received. Success=" +
                    success +
                    " | Message=" +
                    message.Replace(
                        "\n",
                        " | "));
            }

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
    /// 관리 대상 모닥불이 꺼져 있을 때 원본 Interact_CastFinished 실행을 막고
    /// CampfireGate의 호스트 검증 점화 요청으로 대체합니다.
    /// 그 외 모닥불 상태에서는 원본 메서드를 실행합니다.
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

            // 켜져 있거나 Off가 아닌 상태에서는 원본 상호작용 완료 로직을 실행합니다.
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

            // 꺼진 관리 대상 모닥불의 원본 호출을 차단합니다.
            return false;
        }
    }

    /// <summary>
    /// 꺼진 관리 대상 모닥불의 상호작용 문구에
    /// 파티 인벤토리의 재료 보유량과 설정된 요구량을 추가합니다.
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
