// SHOP INVENTORY CLICK SELECTION BUILD 1.1.0
//
// 기능
// - Shore 이후 게임 맵에서 P키를 누르면 상점 창을 엽니다.
// - 상점에 표시되는 인벤토리 슬롯을 마우스로 클릭해 판매 대상을 선택합니다.
// - 판매 판정과 아이템 제거는 Master Client가 확정합니다.
// - 돈은 Photon Room Custom Property로 모든 플레이어가 공유합니다.
// - 구매 기능, 업그레이드 UI, 아이템 목록은 아직 만들지 않습니다.
// - Airport, Title, Pretitle에서는 작동하지 않습니다.
//
// 현재 프로토타입 판매 가격
// Common
//   나뭇가지 28 = 1
//   돌 72 = 1
//   소라고동 69 = 1
// Normal
//   망원경 14 = 3
//   빙봉 13 = 3
//   나팔 15 = 3
//   플라잉 디스크 99 = 3
// Rare
//   가이드북 34 = 7
//   스크롤 49 = 7
// Unique
//   괴상 버섯 51 = 15
// Legendary
//   이상한 보석 112 = 50
//
// 중요
// - Delete.cs, Spawn.cs, LongE.cs, Open.cs, Campfire.cs, Inventory.cs를 같은 Craft PEAK.dll로 빌드하세요.
// - 이 파일에는 Harmony 패치가 없습니다.
// - TMPro.dll, UnityEngine.UI.dll, Unity.InputSystem.dll 참조가 필요합니다.

using BepInEx;
using BepInEx.Logging;
using ExitGames.Client.Photon;
using Photon.Pun;
using Photon.Realtime;
using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Zorro.Core;

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
        LongE.PluginGuid,
        BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency(
        InventoryStack.PluginGuid,
        BepInDependency.DependencyFlags.HardDependency)]
    public sealed class Shop :
        BaseUnityPlugin,
        IOnEventCallback
    {
        public const string PluginGuid =
            "com.sappheiros.crafting.shop";

        public const string PluginName =
            "Craft PEAK Shop Inventory Selection";

        public const string PluginVersion =
            "1.1.0";

        private const byte SellRequestEventCode = 210;
        private const byte SellResultEventCode = 211;

        private const string SharedMoneyPropertyKey =
            "CraftPeak.SharedMoney";

        private const float SellRequestTimeoutSeconds =
            5f;

        private ShopMenuWindow activeWindow;

        private bool sellRequestPending;
        private float sellRequestStartedAt;

        private bool waitingForNewRun;
        private int cachedSharedMoney;

        // 상점에서 마우스로 선택한 일반 인벤토리 슬롯입니다.
        // 손에 장착된 슬롯과는 별도로 관리합니다.
        private int selectedInventorySlotId = -1;

        internal static Shop Instance
        {
            get;
            private set;
        }

        internal static ManualLogSource ModLogger
        {
            get;
            private set;
        }

        public static int SharedMoney
        {
            get
            {
                return Instance != null
                    ? Instance.cachedSharedMoney
                    : 0;
            }
        }

        private void Awake()
        {
            Instance = this;
            ModLogger = Logger;

            SceneManager.sceneLoaded +=
                HandleSceneLoaded;

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
                " loaded. Press P in a gameplay scene.");
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

            CloseShop();
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -=
                HandleSceneLoaded;

            CloseShop();

            if (Instance == this)
            {
                Instance = null;
            }

            ModLogger = null;
        }

        private void Update()
        {
            RefreshSharedMoneyFromRoom();
            InitializeRunMoneyIfNeeded();

            if (sellRequestPending &&
                Time.unscaledTime -
                sellRequestStartedAt >
                SellRequestTimeoutSeconds)
            {
                sellRequestPending =
                    false;

                SetWindowStatus(
                    "판매 요청 시간이 초과되었습니다.");

                RefreshWindow();
            }

            if (activeWindow != null &&
                !activeWindow.isOpen)
            {
                DestroyShopWindowObject();
            }

            if (!WasShopKeyPressed())
            {
                return;
            }

            if (activeWindow != null)
            {
                CloseShop();
                return;
            }

            if (!CanOpenShop())
            {
                return;
            }

            OpenShop();
        }

        private static bool WasShopKeyPressed()
        {
            Keyboard keyboard =
                Keyboard.current;

            return keyboard != null &&
                   keyboard.pKey.wasPressedThisFrame;
        }

        private void HandleSceneLoaded(
            Scene scene,
            LoadSceneMode loadSceneMode)
        {
            CloseShop();

            sellRequestPending =
                false;

            if (IsAirportScene(
                    scene))
            {
                waitingForNewRun =
                    true;

                cachedSharedMoney =
                    0;

                Logger.LogInfo(
                    "Shop disabled in Airport. " +
                    "The next gameplay scene will start with 0 shared money.");

                return;
            }

            if (IsExcludedScene(
                    scene))
            {
                return;
            }

            Logger.LogInfo(
                "Shop prototype enabled in scene: " +
                scene.name);
        }

        private void InitializeRunMoneyIfNeeded()
        {
            if (!waitingForNewRun ||
                !PhotonNetwork.InRoom ||
                !IsGameplayScene())
            {
                return;
            }

            object existingValue;

            bool propertyAlreadyExists =
                PhotonNetwork.CurrentRoom != null &&
                PhotonNetwork.CurrentRoom
                    .CustomProperties.TryGetValue(
                        SharedMoneyPropertyKey,
                        out existingValue);

            if (propertyAlreadyExists)
            {
                waitingForNewRun =
                    false;

                return;
            }

            if (!PhotonNetwork.IsMasterClient)
            {
                return;
            }

            SetSharedMoneyOnHost(
                0);

            waitingForNewRun =
                false;

            Logger.LogInfo(
                "New run shared money initialized to 0.");
        }

        private static bool CanOpenShop()
        {
            if (!IsGameplayScene() ||
                LoadingScreenHandler.loading ||
                Character.localCharacter == null ||
                global::Player.localPlayer == null)
            {
                return false;
            }

            GUIManager guiManager =
                GUIManager.instance;

            if (guiManager == null ||
                GUIManager.InPauseMenu ||
                guiManager.wheelActive)
            {
                return false;
            }

            for (int i = 0;
                 i < MenuWindow.AllActiveWindows.Count;
                 i++)
            {
                MenuWindow window =
                    MenuWindow.AllActiveWindows[i];

                if (window != null)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsGameplayScene()
        {
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
                IsAirportScene(
                    scene) ||
                string.Equals(
                    scene.name,
                    "Title",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    scene.name,
                    "Pretitle",
                    StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsAirportScene(
            Scene scene)
        {
            return scene.IsValid() &&
                   string.Equals(
                       scene.name,
                       "Airport",
                       StringComparison.OrdinalIgnoreCase);
        }

        private void OpenShop()
        {
            if (activeWindow != null)
            {
                return;
            }

            selectedInventorySlotId =
                -1;

            GameObject root =
                new GameObject(
                    "CraftPeak_ShopWindow",
                    typeof(RectTransform),
                    typeof(Canvas),
                    typeof(CanvasScaler),
                    typeof(GraphicRaycaster),
                    typeof(ShopMenuWindow));

            UnityEngine.Object.DontDestroyOnLoad(
                root);

            Canvas canvas =
                root.GetComponent<Canvas>();

            canvas.renderMode =
                RenderMode.ScreenSpaceOverlay;

            canvas.sortingOrder =
                500;

            CanvasScaler scaler =
                root.GetComponent<CanvasScaler>();

            scaler.uiScaleMode =
                CanvasScaler.ScaleMode.ScaleWithScreenSize;

            scaler.referenceResolution =
                new Vector2(
                    1920f,
                    1080f);

            scaler.screenMatchMode =
                CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;

            scaler.matchWidthOrHeight =
                0.5f;

            activeWindow =
                root.GetComponent<ShopMenuWindow>();

            BuildShopVisuals(
                activeWindow);

            activeWindow.Initialize(
                this);

            Logger.LogInfo(
                "Shop opened.");

            RefreshWindow();
        }

        private void BuildShopVisuals(
            ShopMenuWindow window)
        {
            TMP_FontAsset font =
                ResolveFontAsset();

            Image backdrop =
                CreateImage(
                    "Backdrop",
                    window.transform,
                    new Color(
                        0f,
                        0f,
                        0f,
                        0.68f));

            SetStretch(
                backdrop.rectTransform);

            Image panel =
                CreateImage(
                    "Panel",
                    window.transform,
                    new Color(
                        0.10f,
                        0.11f,
                        0.13f,
                        0.98f));

            SetCenteredRect(
                panel.rectTransform,
                new Vector2(
                    900f,
                    680f),
                Vector2.zero);

            Image topLine =
                CreateImage(
                    "TopLine",
                    panel.transform,
                    new Color(
                        0.82f,
                        0.67f,
                        0.31f,
                        1f));

            SetAnchoredRect(
                topLine.rectTransform,
                new Vector2(
                    0.5f,
                    1f),
                new Vector2(
                    0.5f,
                    1f),
                new Vector2(
                    0f,
                    -5f),
                new Vector2(
                    900f,
                    10f));

            TextMeshProUGUI title =
                CreateText(
                    "Title",
                    panel.transform,
                    font,
                    "자원 상점",
                    42f,
                    TextAlignmentOptions.Center);

            SetAnchoredRect(
                title.rectTransform,
                new Vector2(
                    0.5f,
                    1f),
                new Vector2(
                    0.5f,
                    1f),
                new Vector2(
                    0f,
                    -52f),
                new Vector2(
                    760f,
                    65f));

            TextMeshProUGUI balanceText =
                CreateText(
                    "SharedBalance",
                    panel.transform,
                    font,
                    string.Empty,
                    30f,
                    TextAlignmentOptions.Center);

            SetAnchoredRect(
                balanceText.rectTransform,
                new Vector2(
                    0.5f,
                    1f),
                new Vector2(
                    0.5f,
                    1f),
                new Vector2(
                    0f,
                    -108f),
                new Vector2(
                    760f,
                    50f));

            TextMeshProUGUI inventoryLabel =
                CreateText(
                    "InventoryLabel",
                    panel.transform,
                    font,
                    "판매할 인벤토리 슬롯을 마우스로 선택하세요",
                    23f,
                    TextAlignmentOptions.Center);

            inventoryLabel.color =
                new Color(
                    0.82f,
                    0.84f,
                    0.88f,
                    1f);

            SetAnchoredRect(
                inventoryLabel.rectTransform,
                new Vector2(
                    0.5f,
                    1f),
                new Vector2(
                    0.5f,
                    1f),
                new Vector2(
                    0f,
                    -155f),
                new Vector2(
                    760f,
                    40f));

            List<ShopInventorySlotView> inventorySlotViews =
                CreateInventorySlotViews(
                    panel.transform,
                    font);

            Image itemBox =
                CreateImage(
                    "SelectedItemBox",
                    panel.transform,
                    new Color(
                        0.16f,
                        0.17f,
                        0.20f,
                        1f));

            SetAnchoredRect(
                itemBox.rectTransform,
                new Vector2(
                    0.5f,
                    0.5f),
                new Vector2(
                    0.5f,
                    0.5f),
                new Vector2(
                    0f,
                    -75f),
                new Vector2(
                    760f,
                    105f));

            TextMeshProUGUI selectedItemText =
                CreateText(
                    "SelectedItem",
                    itemBox.transform,
                    font,
                    string.Empty,
                    26f,
                    TextAlignmentOptions.Center);

            SetStretchWithMargins(
                selectedItemText.rectTransform,
                25f,
                25f,
                12f,
                12f);

            TextMeshProUGUI statusText =
                CreateText(
                    "Status",
                    panel.transform,
                    font,
                    "인벤토리 슬롯을 클릭한 뒤 판매 버튼을 누르세요.",
                    21f,
                    TextAlignmentOptions.Center);

            statusText.color =
                new Color(
                    0.80f,
                    0.82f,
                    0.86f,
                    1f);

            SetAnchoredRect(
                statusText.rectTransform,
                new Vector2(
                    0.5f,
                    0.5f),
                new Vector2(
                    0.5f,
                    0.5f),
                new Vector2(
                    0f,
                    -155f),
                new Vector2(
                    760f,
                    58f));

            Button sellButton =
                CreateButton(
                    "SellButton",
                    panel.transform,
                    font,
                    "선택 슬롯 판매",
                    new Color(
                        0.82f,
                        0.67f,
                        0.31f,
                        1f),
                    new Color(
                        0.08f,
                        0.08f,
                        0.09f,
                        1f));

            SetAnchoredRect(
                sellButton.GetComponent<RectTransform>(),
                new Vector2(
                    0.5f,
                    0f),
                new Vector2(
                    0.5f,
                    0f),
                new Vector2(
                    -115f,
                    67f),
                new Vector2(
                    290f,
                    68f));

            Button closeButton =
                CreateButton(
                    "CloseButton",
                    panel.transform,
                    font,
                    "닫기",
                    new Color(
                        0.26f,
                        0.28f,
                        0.32f,
                        1f),
                    Color.white);

            SetAnchoredRect(
                closeButton.GetComponent<RectTransform>(),
                new Vector2(
                    0.5f,
                    0f),
                new Vector2(
                    0.5f,
                    0f),
                new Vector2(
                    170f,
                    67f),
                new Vector2(
                    190f,
                    68f));

            TextMeshProUGUI footer =
                CreateText(
                    "Footer",
                    panel.transform,
                    font,
                    "P 또는 ESC 키로 닫기",
                    18f,
                    TextAlignmentOptions.Center);

            footer.color =
                new Color(
                    0.62f,
                    0.64f,
                    0.68f,
                    1f);

            SetAnchoredRect(
                footer.rectTransform,
                new Vector2(
                    0.5f,
                    0f),
                new Vector2(
                    0.5f,
                    0f),
                new Vector2(
                    0f,
                    22f),
                new Vector2(
                    500f,
                    30f));

            sellButton.onClick.AddListener(
                new UnityAction(
                    RequestSellSelectedItem));

            closeButton.onClick.AddListener(
                new UnityAction(
                    CloseShop));

            window.SetVisualReferences(
                sellButton,
                balanceText,
                selectedItemText,
                statusText,
                inventorySlotViews);
        }

        private List<ShopInventorySlotView>
            CreateInventorySlotViews(
                Transform parent,
                TMP_FontAsset font)
        {
            List<ShopInventorySlotView> views =
                new List<ShopInventorySlotView>();

            global::Player player =
                global::Player.localPlayer;

            int slotCount =
                player != null &&
                player.itemSlots != null
                    ? player.itemSlots.Length
                    : 3;

            slotCount =
                Mathf.Max(
                    1,
                    slotCount);

            const float availableWidth = 760f;
            const float gap = 18f;

            float slotWidth =
                Mathf.Min(
                    180f,
                    (
                        availableWidth -
                        gap *
                        (slotCount - 1)
                    ) /
                    slotCount);

            float totalWidth =
                slotWidth *
                slotCount +
                gap *
                (slotCount - 1);

            float firstCenterX =
                -totalWidth *
                0.5f +
                slotWidth *
                0.5f;

            for (int i = 0;
                 i < slotCount;
                 i++)
            {
                byte capturedSlotId =
                    (byte)i;

                Image background =
                    CreateImage(
                        "InventorySlot_" +
                        (i + 1),
                        parent,
                        new Color(
                            0.16f,
                            0.17f,
                            0.20f,
                            1f));

                SetAnchoredRect(
                    background.rectTransform,
                    new Vector2(
                        0.5f,
                        0.5f),
                    new Vector2(
                        0.5f,
                        0.5f),
                    new Vector2(
                        firstCenterX +
                        i *
                        (
                            slotWidth +
                            gap
                        ),
                        105f),
                    new Vector2(
                        slotWidth,
                        175f));

                Button button =
                    background.gameObject
                        .AddComponent<Button>();

                button.targetGraphic =
                    background;

                ColorBlock colors =
                    button.colors;

                colors.normalColor =
                    Color.white;

                colors.highlightedColor =
                    new Color(
                        1.10f,
                        1.10f,
                        1.10f,
                        1f);

                colors.pressedColor =
                    new Color(
                        0.78f,
                        0.78f,
                        0.78f,
                        1f);

                button.colors =
                    colors;

                RawImage icon =
                    CreateRawImage(
                        "Icon",
                        background.transform);

                SetAnchoredRect(
                    icon.rectTransform,
                    new Vector2(
                        0.5f,
                        0.5f),
                    new Vector2(
                        0.5f,
                        0.5f),
                    new Vector2(
                        0f,
                        14f),
                    new Vector2(
                        92f,
                        92f));

                icon.raycastTarget =
                    false;

                TextMeshProUGUI slotNumber =
                    CreateText(
                        "SlotNumber",
                        background.transform,
                        font,
                        (i + 1).ToString(),
                        22f,
                        TextAlignmentOptions.TopLeft);

                SetStretchWithMargins(
                    slotNumber.rectTransform,
                    10f,
                    10f,
                    7f,
                    7f);

                TextMeshProUGUI quantity =
                    CreateText(
                        "Quantity",
                        background.transform,
                        font,
                        string.Empty,
                        25f,
                        TextAlignmentOptions.TopRight);

                quantity.fontStyle =
                    FontStyles.Bold;

                SetStretchWithMargins(
                    quantity.rectTransform,
                    10f,
                    10f,
                    7f,
                    7f);

                TextMeshProUGUI itemName =
                    CreateText(
                        "ItemName",
                        background.transform,
                        font,
                        "비어 있음",
                        18f,
                        TextAlignmentOptions.Bottom);

                SetAnchoredRect(
                    itemName.rectTransform,
                    new Vector2(
                        0.5f,
                        0f),
                    new Vector2(
                        0.5f,
                        0f),
                    new Vector2(
                        0f,
                        16f),
                    new Vector2(
                        slotWidth -
                        18f,
                        45f));

                button.onClick.AddListener(
                    new UnityAction(
                        delegate
                        {
                            SelectInventorySlot(
                                capturedSlotId);
                        }));

                views.Add(
                    new ShopInventorySlotView(
                        capturedSlotId,
                        button,
                        background,
                        icon,
                        slotNumber,
                        quantity,
                        itemName));
            }

            return views;
        }

        private static RawImage CreateRawImage(
            string objectName,
            Transform parent)
        {
            GameObject gameObject =
                new GameObject(
                    objectName,
                    typeof(RectTransform),
                    typeof(CanvasRenderer),
                    typeof(RawImage));

            gameObject.transform.SetParent(
                parent,
                false);

            RawImage image =
                gameObject.GetComponent<RawImage>();

            image.color =
                Color.white;

            return image;
        }

        private static TMP_FontAsset ResolveFontAsset()
        {
            TextMeshProUGUI[] texts =
                Resources.FindObjectsOfTypeAll<
                    TextMeshProUGUI>();

            for (int i = 0;
                 i < texts.Length;
                 i++)
            {
                TextMeshProUGUI text =
                    texts[i];

                if (text != null &&
                    text.font != null &&
                    text.gameObject.scene.IsValid())
                {
                    return text.font;
                }
            }

            return TMP_Settings.defaultFontAsset;
        }

        private void RequestSellSelectedItem()
        {
            if (sellRequestPending)
            {
                SetWindowStatus(
                    "이전 판매 요청을 처리 중입니다.");

                return;
            }

            byte slotId;
            ItemSlot slot;
            string reason;

            if (!TryGetSelectedSaleSlot(
                    out slotId,
                    out slot,
                    out reason))
            {
                SetWindowStatus(
                    reason);

                RefreshWindow();
                return;
            }

            ushort itemId =
                slot.prefab.itemID;

            string instanceGuid =
                slot.data != null
                    ? slot.data.guid.ToString()
                    : string.Empty;

            sellRequestPending =
                true;

            sellRequestStartedAt =
                Time.unscaledTime;

            SetWindowStatus(
                "판매 요청을 처리 중입니다...");

            RefreshWindow();

            object[] requestData =
            {
                (int)slotId,
                (int)itemId,
                instanceGuid
            };

            int actorNumber =
                PhotonNetwork.LocalPlayer.ActorNumber;

            if (PhotonNetwork.IsMasterClient)
            {
                ProcessSellRequestOnHost(
                    actorNumber,
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
                    SellRequestEventCode,
                    requestData,
                    options,
                    SendOptions.SendReliable);

            if (!sent)
            {
                sellRequestPending =
                    false;

                SetWindowStatus(
                    "판매 요청 전송에 실패했습니다.");

                RefreshWindow();
            }
        }

        private bool TryGetSelectedSaleSlot(
            out byte slotId,
            out ItemSlot slot,
            out string reason)
        {
            slotId = 0;
            slot = null;
            reason = string.Empty;

            global::Player player =
                global::Player.localPlayer;

            if (player == null ||
                player.itemSlots == null)
            {
                reason =
                    "플레이어 인벤토리를 찾을 수 없습니다.";

                return false;
            }

            if (selectedInventorySlotId < 0)
            {
                reason =
                    "판매할 인벤토리 슬롯을 마우스로 선택하세요.";

                return false;
            }

            if (selectedInventorySlotId >=
                player.itemSlots.Length)
            {
                reason =
                    "선택한 인벤토리 슬롯이 존재하지 않습니다.";

                selectedInventorySlotId =
                    -1;

                return false;
            }

            slotId =
                (byte)selectedInventorySlotId;

            slot =
                player.GetItemSlot(
                    slotId);

            if (slot == null ||
                slot.IsEmpty() ||
                slot.prefab == null)
            {
                reason =
                    (selectedInventorySlotId + 1) +
                    "번 슬롯이 비어 있습니다.";

                return false;
            }

            if (!Spawn.IsSaleResourceId(
                    slot.prefab.itemID))
            {
                reason =
                    (selectedInventorySlotId + 1) +
                    "번 슬롯의 아이템은 판매용 자원이 아닙니다.";

                return false;
            }

            return true;
        }

        internal void SelectInventorySlot(
            byte slotId)
        {
            global::Player player =
                global::Player.localPlayer;

            if (player == null ||
                player.itemSlots == null ||
                slotId >=
                    player.itemSlots.Length)
            {
                selectedInventorySlotId =
                    -1;

                SetWindowStatus(
                    "선택한 인벤토리 슬롯을 찾을 수 없습니다.");

                RefreshWindow();
                return;
            }

            selectedInventorySlotId =
                slotId;

            ItemSlot slot =
                player.GetItemSlot(
                    slotId);

            if (slot == null ||
                slot.IsEmpty() ||
                slot.prefab == null)
            {
                SetWindowStatus(
                    (slotId + 1) +
                    "번 슬롯은 비어 있습니다.");
            }
            else if (!Spawn.IsSaleResourceId(
                         slot.prefab.itemID))
            {
                SetWindowStatus(
                    (slotId + 1) +
                    "번 슬롯의 아이템은 판매할 수 없습니다.");
            }
            else
            {
                SetWindowStatus(
                    (slotId + 1) +
                    "번 슬롯을 판매 대상으로 선택했습니다.");
            }

            RefreshWindow();
        }

        internal int GetSelectedInventorySlotId()
        {
            return selectedInventorySlotId;
        }

        public void OnEvent(
            EventData photonEvent)
        {
            if (photonEvent == null)
            {
                return;
            }

            if (photonEvent.Code ==
                SellRequestEventCode)
            {
                if (!PhotonNetwork.IsMasterClient)
                {
                    return;
                }

                object[] requestData =
                    photonEvent.CustomData as
                    object[];

                ProcessSellRequestOnHost(
                    photonEvent.Sender,
                    requestData);

                return;
            }

            if (photonEvent.Code ==
                SellResultEventCode)
            {
                object[] resultData =
                    photonEvent.CustomData as
                    object[];

                HandleSellResultOnClient(
                    resultData);
            }
        }

        private void ProcessSellRequestOnHost(
            int actorNumber,
            object[] requestData)
        {
            if (!PhotonNetwork.IsMasterClient)
            {
                return;
            }

            if (requestData == null ||
                requestData.Length < 3)
            {
                SendSellResult(
                    actorNumber,
                    false,
                    "잘못된 판매 요청입니다.",
                    0,
                    cachedSharedMoney,
                    -1,
                    -1);

                return;
            }

            int slotIdValue;
            int expectedItemId;
            string expectedGuid;

            try
            {
                slotIdValue =
                    Convert.ToInt32(
                        requestData[0]);

                expectedItemId =
                    Convert.ToInt32(
                        requestData[1]);

                expectedGuid =
                    requestData[2] as
                    string;
            }
            catch (Exception)
            {
                SendSellResult(
                    actorNumber,
                    false,
                    "판매 요청 데이터를 해석하지 못했습니다.",
                    0,
                    cachedSharedMoney,
                    -1,
                    -1);

                return;
            }

            if (slotIdValue < 0 ||
                slotIdValue > byte.MaxValue)
            {
                SendSellResult(
                    actorNumber,
                    false,
                    "잘못된 인벤토리 슬롯입니다.",
                    0,
                    cachedSharedMoney,
                    slotIdValue,
                    expectedItemId);

                return;
            }

            global::Player player =
                PlayerHandler.GetPlayer(
                    actorNumber);

            if (player == null)
            {
                SendSellResult(
                    actorNumber,
                    false,
                    "판매 요청 플레이어를 찾을 수 없습니다.",
                    0,
                    cachedSharedMoney,
                    slotIdValue,
                    expectedItemId);

                return;
            }

            byte slotId =
                (byte)slotIdValue;

            ItemSlot slot =
                player.GetItemSlot(
                    slotId);

            if (slot == null ||
                slot.IsEmpty() ||
                slot.prefab == null)
            {
                SendSellResult(
                    actorNumber,
                    false,
                    "판매할 아이템이 슬롯에 없습니다.",
                    0,
                    cachedSharedMoney,
                    slotIdValue,
                    expectedItemId);

                return;
            }

            ushort actualItemId =
                slot.prefab.itemID;

            if (actualItemId !=
                (ushort)expectedItemId)
            {
                SendSellResult(
                    actorNumber,
                    false,
                    "선택한 아이템이 변경되었습니다.",
                    0,
                    cachedSharedMoney,
                    slotIdValue,
                    actualItemId);

                return;
            }

            if (!Spawn.IsSaleResourceId(
                    actualItemId))
            {
                SendSellResult(
                    actorNumber,
                    false,
                    "판매할 수 없는 아이템입니다.",
                    0,
                    cachedSharedMoney,
                    slotIdValue,
                    actualItemId);

                return;
            }

            string actualGuid =
                slot.data != null
                    ? slot.data.guid.ToString()
                    : string.Empty;

            if (!string.Equals(
                    actualGuid,
                    expectedGuid,
                    StringComparison.Ordinal))
            {
                SendSellResult(
                    actorNumber,
                    false,
                    "아이템 인스턴스가 변경되었습니다.",
                    0,
                    cachedSharedMoney,
                    slotIdValue,
                    actualItemId);

                return;
            }

            int salePrice =
                GetSellPrice(
                    actualItemId);

            if (salePrice <= 0)
            {
                SendSellResult(
                    actorNumber,
                    false,
                    "판매 가격이 설정되지 않은 아이템입니다.",
                    0,
                    cachedSharedMoney,
                    slotIdValue,
                    actualItemId);

                return;
            }

            string soldItemName =
                GetItemDisplayName(
                    slot.prefab);

            // Master Client가 실제 인벤토리 제거를 확정하고
            // 해당 Player의 다른 클라이언트 인벤토리를 동기화합니다.
            player.RPCRemoveItemFromSlot(
                slotId);

            int newBalance =
                Mathf.Max(
                    cachedSharedMoney,
                    ReadSharedMoneyFromRoom()) +
                salePrice;

            SetSharedMoneyOnHost(
                newBalance);

            SendSellResult(
                actorNumber,
                true,
                soldItemName +
                " 판매 완료: +" +
                salePrice,
                salePrice,
                newBalance,
                slotIdValue,
                actualItemId);

            Logger.LogInfo(
                "Host approved sale. " +
                "Actor=" +
                actorNumber +
                " | Slot=" +
                slotId +
                " | ItemID=" +
                actualItemId +
                " | Price=" +
                salePrice +
                " | SharedMoney=" +
                newBalance);
        }

        private void SendSellResult(
            int targetActorNumber,
            bool success,
            string message,
            int price,
            int balance,
            int slotId,
            int itemId)
        {
            object[] resultData =
            {
                success,
                message ?? string.Empty,
                price,
                balance,
                slotId,
                itemId
            };

            if (PhotonNetwork.LocalPlayer != null &&
                targetActorNumber ==
                PhotonNetwork.LocalPlayer.ActorNumber)
            {
                HandleSellResultOnClient(
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
                SellResultEventCode,
                resultData,
                options,
                SendOptions.SendReliable);
        }

        private void HandleSellResultOnClient(
            object[] resultData)
        {
            sellRequestPending =
                false;

            if (resultData == null ||
                resultData.Length < 6)
            {
                SetWindowStatus(
                    "판매 결과 데이터가 올바르지 않습니다.");

                RefreshWindow();
                return;
            }

            bool success;
            string message;
            int balance;
            int slotId;

            try
            {
                success =
                    Convert.ToBoolean(
                        resultData[0]);

                message =
                    resultData[1] as
                    string;

                balance =
                    Convert.ToInt32(
                        resultData[3]);

                slotId =
                    Convert.ToInt32(
                        resultData[4]);
            }
            catch (Exception)
            {
                SetWindowStatus(
                    "판매 결과를 해석하지 못했습니다.");

                RefreshWindow();
                return;
            }

            cachedSharedMoney =
                Mathf.Max(
                    0,
                    balance);

            if (success)
            {
                UnequipSoldLocalItem(
                    slotId);
            }

            SetWindowStatus(
                string.IsNullOrEmpty(
                    message)
                    ? (
                        success
                            ? "판매했습니다."
                            : "판매하지 못했습니다."
                    )
                    : message);

            RefreshWindow();
        }

        private static void UnequipSoldLocalItem(
            int soldSlotId)
        {
            Character character =
                Character.localCharacter;

            if (character == null ||
                character.refs == null ||
                character.refs.items == null ||
                character.refs.items
                    .currentSelectedSlot.IsNone)
            {
                return;
            }

            if (character.refs.items
                    .currentSelectedSlot.Value !=
                (byte)soldSlotId)
            {
                return;
            }

            ItemSlot soldSlot =
                global::Player.localPlayer != null
                    ? global::Player.localPlayer
                        .GetItemSlot(
                            (byte)soldSlotId)
                    : null;

            // 스택 판매 후 같은 슬롯에 아이템이 남아 있으면
            // 손 장착 상태를 유지합니다.
            if (soldSlot != null &&
                !soldSlot.IsEmpty())
            {
                return;
            }

            character.refs.items.EquipSlot(
                Optionable<byte>.None);
        }

        private void SetSharedMoneyOnHost(
            int money)
        {
            if (!PhotonNetwork.InRoom ||
                !PhotonNetwork.IsMasterClient ||
                PhotonNetwork.CurrentRoom == null)
            {
                return;
            }

            int safeMoney =
                Mathf.Max(
                    0,
                    money);

            ExitGames.Client.Photon.Hashtable properties =
                new ExitGames.Client.Photon.Hashtable
                {
                    {
                        SharedMoneyPropertyKey,
                        safeMoney
                    }
                };

            PhotonNetwork.CurrentRoom
                .SetCustomProperties(
                    properties);

            cachedSharedMoney =
                safeMoney;

            RefreshWindow();
        }

        private int ReadSharedMoneyFromRoom()
        {
            if (!PhotonNetwork.InRoom ||
                PhotonNetwork.CurrentRoom == null)
            {
                return cachedSharedMoney;
            }

            object value;

            if (!PhotonNetwork.CurrentRoom
                    .CustomProperties.TryGetValue(
                        SharedMoneyPropertyKey,
                        out value) ||
                value == null)
            {
                return 0;
            }

            try
            {
                return Mathf.Max(
                    0,
                    Convert.ToInt32(
                        value));
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private void RefreshSharedMoneyFromRoom()
        {
            if (!PhotonNetwork.InRoom ||
                PhotonNetwork.CurrentRoom == null)
            {
                cachedSharedMoney =
                    0;

                return;
            }

            int roomMoney =
                ReadSharedMoneyFromRoom();

            if (roomMoney ==
                cachedSharedMoney)
            {
                return;
            }

            cachedSharedMoney =
                roomMoney;

            RefreshWindow();
        }

        private static int GetSellPrice(
            ushort itemId)
        {
            switch (itemId)
            {
                // Common
                case 28:
                case 72:
                case 69:
                    return 1;

                // Normal
                case 14:
                case 13:
                case 15:
                case 99:
                    return 3;

                // Rare
                case 34:
                case 49:
                    return 7;

                // Unique
                case 51:
                    return 15;

                // Legendary
                case 112:
                    return 50;

                default:
                    return 0;
            }
        }

        private static string GetRarityName(
            ushort itemId)
        {
            switch (itemId)
            {
                case 28:
                case 72:
                case 69:
                    return "Common";

                case 14:
                case 13:
                case 15:
                case 99:
                    return "Normal";

                case 34:
                case 49:
                    return "Rare";

                case 51:
                    return "Unique";

                case 112:
                    return "Legendary";

                default:
                    return "Unknown";
            }
        }

        private static string GetItemDisplayName(
            Item item)
        {
            if (item == null)
            {
                return "<없음>";
            }

            string localizedName =
                item.GetName();

            if (!string.IsNullOrEmpty(
                    localizedName))
            {
                return localizedName;
            }

            if (item.UIData != null &&
                !string.IsNullOrEmpty(
                    item.UIData.itemName))
            {
                return item.UIData.itemName;
            }

            return item.gameObject != null
                ? item.gameObject.name
                : "<이름 없음>";
        }

        internal void RefreshWindow()
        {
            if (activeWindow == null)
            {
                return;
            }

            ValidateSelectedInventorySlot();

            activeWindow.RefreshContents();
        }

        private void ValidateSelectedInventorySlot()
        {
            if (selectedInventorySlotId < 0)
            {
                return;
            }

            global::Player player =
                global::Player.localPlayer;

            if (player == null ||
                player.itemSlots == null ||
                selectedInventorySlotId >=
                    player.itemSlots.Length)
            {
                selectedInventorySlotId =
                    -1;

                return;
            }

            ItemSlot slot =
                player.GetItemSlot(
                    (byte)selectedInventorySlotId);

            if (!sellRequestPending &&
                (
                    slot == null ||
                    slot.IsEmpty()
                ))
            {
                selectedInventorySlotId =
                    -1;
            }
        }

        private void SetWindowStatus(
            string message)
        {
            if (activeWindow == null)
            {
                return;
            }

            activeWindow.SetStatus(
                message);
        }

        internal bool IsSellRequestPending()
        {
            return sellRequestPending;
        }

        internal string BuildSelectedItemText(
            out bool canSell)
        {
            canSell = false;

            byte slotId;
            ItemSlot slot;
            string reason;

            if (!TryGetSelectedSaleSlot(
                    out slotId,
                    out slot,
                    out reason))
            {
                return reason;
            }

            ushort itemId =
                slot.prefab.itemID;

            int price =
                GetSellPrice(
                    itemId);

            int stackCount =
                InventoryStack.GetStackCount(
                    global::Player.localPlayer,
                    slotId);

            canSell =
                price > 0 &&
                !sellRequestPending;

            return
                (slotId + 1) +
                "번 슬롯 선택: " +
                GetItemDisplayName(
                    slot.prefab) +
                (
                    stackCount > 1
                        ? " x" +
                          stackCount
                        : string.Empty
                ) +
                "\n등급: " +
                GetRarityName(
                    itemId) +
                "   |   1개 판매가: " +
                price +
                "원";
        }

        public void CloseShop()
        {
            if (activeWindow == null)
            {
                return;
            }

            DestroyShopWindowObject();

            Logger.LogInfo(
                "Shop closed.");
        }

        private void DestroyShopWindowObject()
        {
            if (activeWindow == null)
            {
                return;
            }

            selectedInventorySlotId =
                -1;

            ShopMenuWindow window =
                activeWindow;

            activeWindow =
                null;

            MenuWindow.AllActiveWindows.Remove(
                window);

            if (window != null &&
                window.gameObject != null)
            {
                UnityEngine.Object.Destroy(
                    window.gameObject);
            }
        }

        private static Image CreateImage(
            string objectName,
            Transform parent,
            Color color)
        {
            GameObject gameObject =
                new GameObject(
                    objectName,
                    typeof(RectTransform),
                    typeof(CanvasRenderer),
                    typeof(Image));

            gameObject.transform.SetParent(
                parent,
                false);

            Image image =
                gameObject.GetComponent<Image>();

            image.color =
                color;

            return image;
        }

        private static TextMeshProUGUI CreateText(
            string objectName,
            Transform parent,
            TMP_FontAsset font,
            string text,
            float fontSize,
            TextAlignmentOptions alignment)
        {
            GameObject gameObject =
                new GameObject(
                    objectName,
                    typeof(RectTransform),
                    typeof(CanvasRenderer),
                    typeof(TextMeshProUGUI));

            gameObject.transform.SetParent(
                parent,
                false);

            TextMeshProUGUI label =
                gameObject.GetComponent<
                    TextMeshProUGUI>();

            label.font =
                font;

            label.text =
                text;

            label.fontSize =
                fontSize;

            label.alignment =
                alignment;

            label.color =
                Color.white;

            label.enableWordWrapping =
                true;

            label.raycastTarget =
                false;

            return label;
        }

        private static Button CreateButton(
            string objectName,
            Transform parent,
            TMP_FontAsset font,
            string labelText,
            Color backgroundColor,
            Color textColor)
        {
            Image image =
                CreateImage(
                    objectName,
                    parent,
                    backgroundColor);

            Button button =
                image.gameObject.AddComponent<
                    Button>();

            ColorBlock colors =
                button.colors;

            colors.normalColor =
                Color.white;

            colors.highlightedColor =
                new Color(
                    1.08f,
                    1.08f,
                    1.08f,
                    1f);

            colors.pressedColor =
                new Color(
                    0.78f,
                    0.78f,
                    0.78f,
                    1f);

            colors.disabledColor =
                new Color(
                    0.42f,
                    0.42f,
                    0.42f,
                    0.65f);

            button.colors =
                colors;

            TextMeshProUGUI label =
                CreateText(
                    "Label",
                    image.transform,
                    font,
                    labelText,
                    28f,
                    TextAlignmentOptions.Center);

            label.color =
                textColor;

            SetStretchWithMargins(
                label.rectTransform,
                8f,
                8f,
                4f,
                4f);

            return button;
        }

        private static void SetStretch(
            RectTransform rectTransform)
        {
            rectTransform.anchorMin =
                Vector2.zero;

            rectTransform.anchorMax =
                Vector2.one;

            rectTransform.offsetMin =
                Vector2.zero;

            rectTransform.offsetMax =
                Vector2.zero;
        }

        private static void SetStretchWithMargins(
            RectTransform rectTransform,
            float left,
            float right,
            float top,
            float bottom)
        {
            rectTransform.anchorMin =
                Vector2.zero;

            rectTransform.anchorMax =
                Vector2.one;

            rectTransform.offsetMin =
                new Vector2(
                    left,
                    bottom);

            rectTransform.offsetMax =
                new Vector2(
                    -right,
                    -top);
        }

        private static void SetCenteredRect(
            RectTransform rectTransform,
            Vector2 size,
            Vector2 anchoredPosition)
        {
            rectTransform.anchorMin =
                new Vector2(
                    0.5f,
                    0.5f);

            rectTransform.anchorMax =
                new Vector2(
                    0.5f,
                    0.5f);

            rectTransform.pivot =
                new Vector2(
                    0.5f,
                    0.5f);

            rectTransform.sizeDelta =
                size;

            rectTransform.anchoredPosition =
                anchoredPosition;
        }

        private static void SetAnchoredRect(
            RectTransform rectTransform,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 anchoredPosition,
            Vector2 size)
        {
            rectTransform.anchorMin =
                anchorMin;

            rectTransform.anchorMax =
                anchorMax;

            rectTransform.pivot =
                new Vector2(
                    0.5f,
                    0.5f);

            rectTransform.sizeDelta =
                size;

            rectTransform.anchoredPosition =
                anchoredPosition;
        }
    }

    internal sealed class ShopInventorySlotView
    {
        private static readonly Color NormalColor =
            new Color(
                0.16f,
                0.17f,
                0.20f,
                1f);

        private static readonly Color SelectedColor =
            new Color(
                0.82f,
                0.67f,
                0.31f,
                1f);

        private static readonly Color UnsellableColor =
            new Color(
                0.23f,
                0.18f,
                0.18f,
                1f);

        internal readonly byte SlotId;

        private readonly Button button;
        private readonly Image background;
        private readonly RawImage icon;
        private readonly TextMeshProUGUI slotNumber;
        private readonly TextMeshProUGUI quantity;
        private readonly TextMeshProUGUI itemName;

        internal ShopInventorySlotView(
            byte slotId,
            Button slotButton,
            Image slotBackground,
            RawImage slotIcon,
            TextMeshProUGUI slotNumberText,
            TextMeshProUGUI quantityText,
            TextMeshProUGUI itemNameText)
        {
            SlotId =
                slotId;

            button =
                slotButton;

            background =
                slotBackground;

            icon =
                slotIcon;

            slotNumber =
                slotNumberText;

            quantity =
                quantityText;

            itemName =
                itemNameText;
        }

        internal void Refresh(
            global::Player player,
            bool selected)
        {
            if (slotNumber != null)
            {
                slotNumber.text =
                    (SlotId + 1).ToString();
            }

            ItemSlot slot =
                player != null
                    ? player.GetItemSlot(
                        SlotId)
                    : null;

            bool hasItem =
                slot != null &&
                !slot.IsEmpty() &&
                slot.prefab != null;

            bool sellable =
                hasItem &&
                Spawn.IsSaleResourceId(
                    slot.prefab.itemID);

            if (background != null)
            {
                background.color =
                    selected
                        ? SelectedColor
                        : (
                            hasItem &&
                            !sellable
                                ? UnsellableColor
                                : NormalColor
                        );
            }

            if (button != null)
            {
                button.interactable =
                    player != null;
            }

            if (!hasItem)
            {
                if (icon != null)
                {
                    icon.enabled =
                        false;

                    icon.texture =
                        null;
                }

                if (quantity != null)
                {
                    quantity.text =
                        string.Empty;
                }

                if (itemName != null)
                {
                    itemName.text =
                        "비어 있음";

                    itemName.color =
                        new Color(
                            0.56f,
                            0.58f,
                            0.62f,
                            1f);
                }

                return;
            }

            if (icon != null)
            {
                icon.texture =
                    slot.prefab.UIData != null
                        ? slot.prefab.UIData.GetIcon()
                        : null;

                icon.enabled =
                    icon.texture != null;

                icon.color =
                    sellable
                        ? Color.white
                        : new Color(
                            0.55f,
                            0.55f,
                            0.55f,
                            1f);
            }

            int stackCount =
                InventoryStack.GetStackCount(
                    player,
                    SlotId);

            if (quantity != null)
            {
                quantity.text =
                    stackCount > 1
                        ? "x" +
                          stackCount
                        : string.Empty;
            }

            if (itemName != null)
            {
                string displayName =
                    slot.prefab.GetName();

                if (string.IsNullOrEmpty(
                        displayName) &&
                    slot.prefab.UIData != null)
                {
                    displayName =
                        slot.prefab.UIData.itemName;
                }

                itemName.text =
                    string.IsNullOrEmpty(
                        displayName)
                        ? slot.prefab.gameObject.name
                        : displayName;

                itemName.color =
                    sellable
                        ? Color.white
                        : new Color(
                            0.95f,
                            0.58f,
                            0.58f,
                            1f);
            }
        }
    }

    /// <summary>
    /// PEAK의 기존 MenuWindow 관리 체계에 등록되는 런타임 상점 창입니다.
    ///
    /// MenuWindow가 GUIManager.windowBlockingInput과
    /// GUIManager.windowShowingCursor를 처리하므로,
    /// 상점이 열려 있는 동안 플레이어 입력이 차단되고 마우스가 표시됩니다.
    /// </summary>
    internal sealed class ShopMenuWindow :
        MenuWindow
    {
        private Shop owner;

        private Button sellButton;
        private TextMeshProUGUI balanceText;
        private TextMeshProUGUI selectedItemText;
        private TextMeshProUGUI statusText;

        private List<ShopInventorySlotView>
            inventorySlotViews =
                new List<ShopInventorySlotView>();

        private string currentStatus =
            "인벤토리 슬롯을 클릭한 뒤 판매 버튼을 누르세요.";

        public override bool closeOnPause
        {
            get
            {
                return true;
            }
        }

        public override bool closeOnUICancel
        {
            get
            {
                return true;
            }
        }

        public override bool blocksPlayerInput
        {
            get
            {
                return true;
            }
        }

        public override bool showCursorWhileOpen
        {
            get
            {
                return true;
            }
        }

        public override Selectable objectToSelectOnOpen
        {
            get
            {
                return sellButton;
            }
        }

        internal void Initialize(
            Shop shop)
        {
            owner =
                shop;
        }

        internal void SetVisualReferences(
            Button sell,
            TextMeshProUGUI balance,
            TextMeshProUGUI selectedItem,
            TextMeshProUGUI status,
            List<ShopInventorySlotView> slotViews)
        {
            sellButton =
                sell;

            balanceText =
                balance;

            selectedItemText =
                selectedItem;

            statusText =
                status;

            inventorySlotViews =
                slotViews ??
                new List<ShopInventorySlotView>();
        }

        protected override void Update()
        {
            base.Update();

            if (!isOpen)
            {
                return;
            }

            RefreshContents();
        }

        protected override void OnOpen()
        {
            RefreshContents();
        }

        internal void RefreshContents()
        {
            if (owner == null)
            {
                return;
            }

            if (balanceText != null)
            {
                balanceText.text =
                    "공유 잔액: " +
                    Shop.SharedMoney +
                    "원";
            }

            global::Player player =
                global::Player.localPlayer;

            int selectedSlotId =
                owner.GetSelectedInventorySlotId();

            for (int i = 0;
                 i < inventorySlotViews.Count;
                 i++)
            {
                ShopInventorySlotView slotView =
                    inventorySlotViews[i];

                if (slotView == null)
                {
                    continue;
                }

                slotView.Refresh(
                    player,
                    selectedSlotId ==
                        slotView.SlotId);
            }

            bool canSell;

            string itemText =
                owner.BuildSelectedItemText(
                    out canSell);

            if (selectedItemText != null)
            {
                selectedItemText.text =
                    itemText;
            }

            if (sellButton != null)
            {
                sellButton.interactable =
                    canSell &&
                    !owner.IsSellRequestPending();
            }

            if (statusText != null)
            {
                statusText.text =
                    currentStatus;
            }
        }

        internal void SetStatus(
            string message)
        {
            currentStatus =
                string.IsNullOrEmpty(
                    message)
                    ? string.Empty
                    : message;

            RefreshContents();
        }
    }
}
