// LONG E PICKUP + MODCONFIG BUILD 1.1.0
//
// 기능
// - 판매용 자원 아이템을 한 번 눌러 즉시 줍지 못하게 합니다.
// - E키 유지 시간은 ModConfig에서 초 단위로 조절할 수 있습니다.
// - E키를 놓거나, 다른 곳을 보거나, 대상이 사라지면 진행도가 초기화됩니다.
// - PEAK 원본 UI_UseItemProgress 원형 게이지를 그대로 사용합니다.
// - Airport, Title, Pretitle에서는 작동하지 않습니다.
// - 판매용 자원 판정은 Spawn.IsSaleResourceId를 그대로 사용합니다.
//
// 중요
// - Delete.cs, Spawn.cs, LongE.cs는 같은 Craft PEAK.dll로 빌드해야 합니다.
// - Delete.cs가 PatchAll(typeof(Delete).Assembly)을 실행하므로,
//   이 파일에서는 Harmony.PatchAll을 다시 실행하지 않습니다.
//   다시 실행하면 같은 Prefix가 중복으로 적용될 수 있습니다.

using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;
using UnityEngine;
using UnityEngine.SceneManagement;

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
    public sealed class LongE : BaseUnityPlugin
    {
        public const string PluginGuid =
            "com.sappheiros.crafting.longe";

        public const string PluginName =
            "Craft PEAK Long E Pickup";

        public const string PluginVersion =
            "1.1.0";

        private const float DefaultPickupHoldSeconds =
            10f;

        private const float MinimumPickupHoldSeconds =
            0.1f;

        private const float MaximumPickupHoldSeconds =
            60f;

        internal static bool Enabled
        {
            get;
            private set;
        }

        internal static ManualLogSource ModLogger
        {
            get;
            private set;
        }

        public static float PickupHoldSeconds
        {
            get;
            private set;
        } =
            DefaultPickupHoldSeconds;

        private static ConfigEntry<float>
            pickupHoldSecondsConfig;

        private void Awake()
        {
            ModLogger =
                Logger;

            BindLongEConfig();

            Enabled =
                true;

            SceneManager.sceneLoaded +=
                HandleSceneLoaded;

            Logger.LogInfo(
                PluginName +
                " " +
                PluginVersion +
                " loaded. " +
                "Sale-resource pickup hold time=" +
                PickupHoldSeconds +
                " seconds. " +
                "PEAKLib.ModConfig can display this setting when installed.");
        }

        private void BindLongEConfig()
        {
            pickupHoldSecondsConfig =
                Config.Bind(
                    "01. LongE 채집 설정",
                    "채집 대기시간 (초)",
                    DefaultPickupHoldSeconds,
                    new ConfigDescription(
                        "판매용 자원을 줍기 위해 E키를 계속 눌러야 하는 시간입니다. " +
                        "값을 변경하면 진행 중인 채집은 취소되고 다음 채집부터 적용됩니다.",
                        new AcceptableValueRange<float>(
                            MinimumPickupHoldSeconds,
                            MaximumPickupHoldSeconds)));

            pickupHoldSecondsConfig.SettingChanged +=
                HandlePickupHoldConfigChanged;

            ApplyPickupHoldSeconds(
                pickupHoldSecondsConfig.Value,
                false);
        }

        private static void HandlePickupHoldConfigChanged(
            object sender,
            EventArgs eventArgs)
        {
            if (pickupHoldSecondsConfig == null)
            {
                return;
            }

            ApplyPickupHoldSeconds(
                pickupHoldSecondsConfig.Value,
                true);
        }

        private void OnDestroy()
        {
            Enabled =
                false;

            SceneManager.sceneLoaded -=
                HandleSceneLoaded;

            if (pickupHoldSecondsConfig != null)
            {
                pickupHoldSecondsConfig.SettingChanged -=
                    HandlePickupHoldConfigChanged;

                pickupHoldSecondsConfig =
                    null;
            }

            LongERuntime.ResetWithoutInteraction();

            ModLogger =
                null;
        }

        private static void HandleSceneLoaded(
            Scene scene,
            LoadSceneMode loadSceneMode)
        {
            LongERuntime.ResetWithoutInteraction();

            if (IsExcludedScene(
                    scene))
            {
                if (ModLogger != null)
                {
                    ModLogger.LogInfo(
                        "Long-E pickup disabled in scene: " +
                        scene.name);
                }

                return;
            }

            if (ModLogger != null)
            {
                ModLogger.LogInfo(
                    "Long-E pickup waiting for gameplay scene: " +
                    scene.name);
            }
        }

        /// <summary>
        /// 상점 업그레이드나 다른 시스템에서 채집 시간을 변경할 때 사용합니다.
        /// ConfigEntry가 준비된 상태라면 설정 파일과 ModConfig에도 같은 값이 반영됩니다.
        /// </summary>
        public static void SetPickupHoldSeconds(
            float seconds)
        {
            float safeSeconds =
                Mathf.Clamp(
                    seconds,
                    MinimumPickupHoldSeconds,
                    MaximumPickupHoldSeconds);

            if (pickupHoldSecondsConfig != null)
            {
                if (!Mathf.Approximately(
                        pickupHoldSecondsConfig.Value,
                        safeSeconds))
                {
                    pickupHoldSecondsConfig.Value =
                        safeSeconds;

                    return;
                }
            }

            ApplyPickupHoldSeconds(
                safeSeconds,
                true);
        }

        public static void ResetPickupHoldSeconds()
        {
            SetPickupHoldSeconds(
                DefaultPickupHoldSeconds);
        }

        private static void ApplyPickupHoldSeconds(
            float seconds,
            bool resetActiveInteraction)
        {
            PickupHoldSeconds =
                Mathf.Clamp(
                    seconds,
                    MinimumPickupHoldSeconds,
                    MaximumPickupHoldSeconds);

            if (resetActiveInteraction)
            {
                LongERuntime.ResetActiveInteraction();
            }

            if (ModLogger != null)
            {
                ModLogger.LogInfo(
                    "Sale-resource pickup hold time applied: " +
                    PickupHoldSeconds.ToString("0.00") +
                    " seconds.");
            }
        }

        internal static bool IsLongEPickupTarget(
            Item item)
        {
            if (!Enabled ||
                item == null ||
                item.gameObject == null)
            {
                return false;
            }

            return Spawn.IsSaleResourceId(
                item.itemID);
        }

        internal static bool IsGameplayActive()
        {
            if (!Enabled)
            {
                return false;
            }

            Scene activeScene =
                SceneManager.GetActiveScene();

            if (IsExcludedScene(
                    activeScene))
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
    }

    /// <summary>
    /// Interaction.currentHeldInteractible에 넣기 위한 대리 객체입니다.
    ///
    /// Item은 IInteractible만 구현하고 IInteractibleConstant는 구현하지 않으므로,
    /// PEAK 기본 코드만으로는 아이템 줍기에 홀드 게이지를 사용할 수 없습니다.
    ///
    /// 이 대리 객체를 currentHeldInteractible에 넣으면
    /// UI_UseItemProgress가 Interaction.constantInteractableProgress를 읽어
    /// PEAK 원본 원형 진행 게이지를 자동으로 표시합니다.
    /// </summary>
    internal sealed class LongEItemInteractible :
        IInteractibleConstant
    {
        internal Item TargetItem
        {
            get;
            private set;
        }

        internal int TargetInstanceId
        {
            get;
            private set;
        }

        internal LongEItemInteractible(
            Item targetItem)
        {
            TargetItem =
                targetItem;

            TargetInstanceId =
                targetItem != null
                    ? targetItem.GetInstanceID()
                    : 0;
        }

        public bool IsInteractible(
            Character interactor)
        {
            return
                LongERuntime.CanContinueHolding(
                    TargetItem,
                    interactor);
        }

        public bool IsConstantlyInteractable(
            Character interactor)
        {
            return IsInteractible(
                interactor);
        }

        public void Interact(
            Character interactor)
        {
            // 실제 줍기는 Interact_CastFinished에서 한 번만 실행합니다.
        }

        public float GetInteractTime(
            Character interactor)
        {
            return LongE.PickupHoldSeconds;
        }

        public void Interact_CastFinished(
            Character interactor)
        {
            if (!LongERuntime.CanFinishPickup(
                    TargetItem,
                    interactor))
            {
                return;
            }

            TargetItem.Interact(
                interactor);
        }

        public void CancelCast(
            Character interactor)
        {
            // 별도 취소 동작은 필요하지 않습니다.
            // Interaction 진행 시간은 LongERuntime에서 0으로 초기화합니다.
        }

        public void ReleaseInteract(
            Character interactor)
        {
            // 완료 후 E키를 놓을 때 실행할 별도 동작은 없습니다.
        }

        public bool holdOnFinish
        {
            get
            {
                return false;
            }
        }

        public void HoverEnter()
        {
            if (TargetItem != null)
            {
                TargetItem.HoverEnter();
            }
        }

        public void HoverExit()
        {
            if (TargetItem != null)
            {
                TargetItem.HoverExit();
            }
        }

        public Vector3 Center()
        {
            return TargetItem != null
                ? TargetItem.Center()
                : Vector3.zero;
        }

        public Transform GetTransform()
        {
            return TargetItem != null
                ? TargetItem.GetTransform()
                : null;
        }

        public string GetInteractionText()
        {
            return TargetItem != null
                ? TargetItem.GetInteractionText()
                : string.Empty;
        }

        public string GetName()
        {
            return TargetItem != null
                ? TargetItem.GetName()
                : string.Empty;
        }
    }

    internal static class LongERuntime
    {
        private static LongEItemInteractible activeInteractible;

        private static int activeItemInstanceId;

        private static bool completionInProgress;

        internal static bool HasActiveInteraction
        {
            get
            {
                return activeInteractible != null;
            }
        }

        internal static bool ShouldIntercept(
            IInteractible interactable)
        {
            if (HasActiveInteraction)
            {
                return true;
            }

            Item item =
                interactable as Item;

            return LongE.IsLongEPickupTarget(
                item);
        }

        internal static void ProcessInteraction(
            Interaction interaction,
            IInteractible hoveredInteractable)
        {
            if (interaction == null)
            {
                ResetWithoutInteraction();
                return;
            }

            Character character =
                Character.localCharacter;

            if (character == null ||
                character.input == null)
            {
                Cancel(
                    interaction,
                    character,
                    false,
                    "NoLocalCharacter");

                return;
            }

            Item hoveredItem =
                hoveredInteractable as Item;

            bool interactPressed =
                character.input.interactIsPressed;

            if (!interactPressed)
            {
                bool hadProgress =
                    interaction.currentInteractableHeldTime >
                    0f;

                Cancel(
                    interaction,
                    character,
                    hadProgress,
                    "Released");

                interaction.readyToInteract =
                    true;

                interaction.readyToReleaseInteract =
                    true;

                return;
            }

            if (!HasActiveInteraction)
            {
                if (!LongE.IsLongEPickupTarget(
                        hoveredItem))
                {
                    return;
                }

                if (!interaction.readyToInteract)
                {
                    return;
                }

                if (!CanStartHolding(
                        hoveredItem,
                        character))
                {
                    interaction.readyToInteract =
                        false;

                    ClearInteractionFields(
                        interaction);

                    return;
                }

                Begin(
                    interaction,
                    hoveredItem,
                    character);

                return;
            }

            Item activeItem =
                activeInteractible.TargetItem;

            if (hoveredItem == null ||
                activeItem == null ||
                hoveredItem.GetInstanceID() !=
                    activeItemInstanceId ||
                hoveredItem !=
                    activeItem)
            {
                Cancel(
                    interaction,
                    character,
                    true,
                    "TargetChanged");

                interaction.readyToInteract =
                    false;

                return;
            }

            if (!CanContinueHolding(
                    activeItem,
                    character))
            {
                Cancel(
                    interaction,
                    character,
                    true,
                    "TargetUnavailable");

                interaction.readyToInteract =
                    false;

                return;
            }

            interaction.currentHeldInteractible =
                activeInteractible;

            interaction.currentConstantInteractableTime =
                LongE.PickupHoldSeconds;

            interaction.currentInteractableHeldTime =
                Mathf.Min(
                    LongE.PickupHoldSeconds,
                    interaction.currentInteractableHeldTime +
                    Time.deltaTime);

            if (interaction.currentInteractableHeldTime <
                LongE.PickupHoldSeconds)
            {
                return;
            }

            Complete(
                interaction,
                character);
        }

        private static void Begin(
            Interaction interaction,
            Item item,
            Character character)
        {
            activeInteractible =
                new LongEItemInteractible(
                    item);

            activeItemInstanceId =
                item.GetInstanceID();

            completionInProgress =
                false;

            interaction.currentHeldInteractible =
                activeInteractible;

            interaction.currentConstantInteractableTime =
                LongE.PickupHoldSeconds;

            interaction.currentInteractableHeldTime =
                0f;

            interaction.readyToInteract =
                false;

            interaction.readyToReleaseInteract =
                true;

            if (LongE.ModLogger != null)
            {
                LongE.ModLogger.LogInfo(
                    "Long-E pickup started. " +
                    "ItemID=" +
                    item.itemID +
                    " | Name=" +
                    GetItemName(
                        item) +
                    " | HoldSeconds=" +
                    LongE.PickupHoldSeconds +
                    " | Position=" +
                    FormatVector(
                        item.transform.position));
            }
        }

        private static void Complete(
            Interaction interaction,
            Character character)
        {
            if (completionInProgress ||
                activeInteractible == null)
            {
                return;
            }

            completionInProgress =
                true;

            Item completedItem =
                activeInteractible.TargetItem;

            ushort completedItemId =
                completedItem != null
                    ? completedItem.itemID
                    : (ushort)0;

            string completedItemName =
                GetItemName(
                    completedItem);

            try
            {
                activeInteractible
                    .Interact_CastFinished(
                        character);
            }
            finally
            {
                interaction.readyToReleaseInteract =
                    false;

                interaction.readyToInteract =
                    false;

                ClearInteractionFields(
                    interaction);

                activeInteractible =
                    null;

                activeItemInstanceId =
                    0;

                completionInProgress =
                    false;
            }

            if (LongE.ModLogger != null)
            {
                LongE.ModLogger.LogInfo(
                    "Long-E pickup completed. " +
                    "ItemID=" +
                    completedItemId +
                    " | Name=" +
                    completedItemName +
                    " | HeldSeconds=" +
                    LongE.PickupHoldSeconds);
            }
        }

        private static void Cancel(
            Interaction interaction,
            Character character,
            bool logCancellation,
            string reason)
        {
            if (activeInteractible != null)
            {
                activeInteractible.CancelCast(
                    character);
            }

            float cancelledProgress =
                interaction != null
                    ? interaction.currentInteractableHeldTime
                    : 0f;

            Item cancelledItem =
                activeInteractible != null
                    ? activeInteractible.TargetItem
                    : null;

            ClearInteractionFields(
                interaction);

            activeInteractible =
                null;

            activeItemInstanceId =
                0;

            completionInProgress =
                false;

            if (logCancellation &&
                cancelledProgress >
                    0.05f &&
                LongE.ModLogger != null)
            {
                LongE.ModLogger.LogInfo(
                    "Long-E pickup cancelled. " +
                    "Reason=" +
                    reason +
                    " | ItemID=" +
                    (
                        cancelledItem != null
                            ? cancelledItem.itemID
                            : 0
                    ) +
                    " | Progress=" +
                    cancelledProgress.ToString("0.00") +
                    "/" +
                    LongE.PickupHoldSeconds.ToString("0.00") +
                    " seconds.");
            }
        }

        internal static void ResetActiveInteraction()
        {
            Interaction interaction =
                Interaction.instance;

            Character character =
                Character.localCharacter;

            Cancel(
                interaction,
                character,
                false,
                "Reset");
        }

        internal static void ResetWithoutInteraction()
        {
            activeInteractible =
                null;

            activeItemInstanceId =
                0;

            completionInProgress =
                false;
        }

        private static void ClearInteractionFields(
            Interaction interaction)
        {
            if (interaction == null)
            {
                return;
            }

            interaction.currentInteractableHeldTime =
                0f;

            interaction.currentConstantInteractableTime =
                0f;

            interaction.currentHeldInteractible =
                null;
        }

        internal static bool CanStartHolding(
            Item item,
            Character character)
        {
            if (!CanContinueHolding(
                    item,
                    character))
            {
                return false;
            }

            if (character.player == null)
            {
                return false;
            }

            return character.player.HasEmptySlot(
                item.itemID);
        }

        internal static bool CanContinueHolding(
            Item item,
            Character character)
        {
            if (!LongE.IsGameplayActive() ||
                !LongE.IsLongEPickupTarget(
                    item) ||
                character == null ||
                !character.IsLocal ||
                item.gameObject == null ||
                !item.gameObject.activeInHierarchy)
            {
                return false;
            }

            if (item.itemState ==
                    ItemState.Held ||
                item.itemState ==
                    ItemState.InBackpack)
            {
                return false;
            }

            return item.IsInteractible(
                character);
        }

        internal static bool CanFinishPickup(
            Item item,
            Character character)
        {
            if (!CanContinueHolding(
                    item,
                    character))
            {
                return false;
            }

            if (character.player == null)
            {
                return false;
            }

            return character.player.HasEmptySlot(
                item.itemID);
        }

        private static string GetItemName(
            Item item)
        {
            if (item == null)
            {
                return "<null>";
            }

            if (item.UIData != null &&
                !string.IsNullOrEmpty(
                    item.UIData.itemName))
            {
                return item.UIData.itemName;
            }

            return item.gameObject != null
                ? item.gameObject.name
                : "<unnamed>";
        }

        private static string FormatVector(
            Vector3 value)
        {
            return "(" +
                   value.x.ToString("0.00") +
                   ", " +
                   value.y.ToString("0.00") +
                   ", " +
                   value.z.ToString("0.00") +
                   ")";
        }
    }

    /// <summary>
    /// PEAK의 Item은 IInteractibleConstant가 아니어서 기본적으로 즉시 줍습니다.
    ///
    /// Interaction.DoInteraction만 판매용 자원에 한해 가로채고,
    /// LongEItemInteractible을 currentHeldInteractible에 넣어
    /// 원본 홀드 처리 필드와 UI_UseItemProgress를 재사용합니다.
    ///
    /// 이 패치는 Delete.cs의 PatchAll 호출로 한 번만 적용됩니다.
    /// </summary>
    [HarmonyPatch(
        typeof(Interaction),
        "DoInteraction")]
    internal static class LongEInteractionDoInteractionPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(
            Interaction __instance,
            IInteractible interactable)
        {
            if (!LongE.Enabled)
            {
                return true;
            }

            if (!LongE.IsGameplayActive())
            {
                if (LongERuntime.HasActiveInteraction)
                {
                    LongERuntime.ResetActiveInteraction();
                }

                return true;
            }

            if (!LongERuntime.ShouldIntercept(
                    interactable))
            {
                return true;
            }

            LongERuntime.ProcessInteraction(
                __instance,
                interactable);

            return false;
        }
    }

}
