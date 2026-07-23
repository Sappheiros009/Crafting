// CRAFT PEAK UNIFIED HUB - PROGRESSION ON-DEMAND BUILD 2.2.1
//
// Developer: Sapphire009
// Project: Craft PEAK
//
// P키 하나로 통합 메뉴를 엽니다.
//
// 좌측 탭
// - 설명
// - 강화
// - 제작
// - 판매
// - 부품
//
// 이 파일 하나가 기존 Open/Shop.cs, Store.cs, Upgrade.cs의 기능을 모두 포함합니다.
// 기존 세 파일은 프로젝트에서 제거해야 합니다.
//
// 포함 기능
// - 공유 돈 초기화/동기화
// - 인벤토리 슬롯 클릭 판매와 호스트 검증
// - 전체 비자원 아이템 제작식, 파티 재료 소비, 제작 성공/실패
// - 자원 등급, 채집 속도, 적재량, 모닥불 효율, 수집량 2배 강화
// - 강화 상태 Photon Room Property 저장 및 호스트 승계
// - P키 통합 UI
// - 비행기 부품 구매와 세그먼트별 모닥불 진행 조건
// - 정상에서 조명탄 제작 시 최종 탈출 신호를 완성하는 트리거
//
// 성능 최적화
// - P 메뉴가 닫혀 있을 때는 배경 폴링으로 돈/제작식/파티 인벤토리/강화/부품을 읽지 않습니다.
// - P 입력, 탭 전환, 버튼 입력, 실제 네트워크 결과가 발생했을 때만 필요한 화면을 갱신합니다.
// - 모닥불 점화·제작·판매·강화 같은 실제 게임 요청은 호스트 검증에 필요한 상태만 즉시 읽습니다.
// - 제작 재료 합계 1초 캐시 및 StringBuilder 재사용
// - 텍스트 전용 8행 UI: 아이콘/RawImage/카드 패널을 제거했습니다.
// - PEAK GUIManager의 기존 TMP 폰트를 한 번만 찾아 캐시합니다.
// - FindAnyObjectByType / Resources.FindObjectsOfTypeAll 반복 검색을 제거했습니다.
// - UI 텍스트와 활성 상태가 실제로 바뀔 때만 Canvas를 더럽힙니다.
//
// Delete.cs가 같은 어셈블리를 PatchAll하므로 별도의 Harmony.PatchAll 호출은 없습니다.
// 리플렉션을 사용하지 않습니다.

using BepInEx;
using BepInEx.Logging;
using BepInEx.Configuration;
using ExitGames.Client.Photon;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;
using System;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Zorro.Core;
using Zorro.Core.Serizalization;

namespace CraftPeak
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency(Delete.PluginGuid, BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency(Spawn.PluginGuid, BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency(LongE.PluginGuid, BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency(CampfireGate.PluginGuid, BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency(InventoryStack.PluginGuid, BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency(
        "com.github.PEAKModding.PEAKLib.ModConfig",
        BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class CraftHub :
        BaseUnityPlugin,
        IOnEventCallback,
        IInRoomCallbacks
    {
        public const string PluginGuid =
            "com.sappheiros.crafting.shop";

        public const string PluginName =
            "Craft PEAK Unified Hub";

        public const string PluginVersion =
            "2.2.1";

        public const string DeveloperName =
            "Sapphire009";

        private const byte SellRequestEventCode = 210;
        private const byte SellResultEventCode = 211;

        private const byte CraftRequestEventCode = 222;
        private const byte CraftResultEventCode = 223;
        private const byte ConsumedSlotEventCode = 224;

        private const byte UpgradeRequestEventCode = 225;
        private const byte UpgradeResultEventCode = 226;

        private const byte PartPurchaseRequestEventCode = 227;
        private const byte PartPurchaseResultEventCode = 228;
        private const byte ProgressionNoticeEventCode = 229;
        private const byte FinalFlareCompletedEventCode = 230;

        private const string SharedMoneyKey =
            "CraftPeak.SharedMoney";

        private const string PartsProtocolKey =
            "CraftPeak.Parts.Protocol";

        private const string PartsRevisionKey =
            "CraftPeak.Parts.Revision";

        private const string PartsRunIdKey =
            "CraftPeak.Parts.RunId";

        private const string PartsPurchasedMaskKey =
            "CraftPeak.Parts.PurchasedMask";

        private const string PartsConsumedMaskKey =
            "CraftPeak.Parts.ConsumedMask";

        private const string PeakUnlockedKey =
            "CraftPeak.Parts.PeakUnlocked";

        private const int PartsProtocolVersion = 1;


        private const string RunIdKey =
            "CraftPeak.Run.Id";

        private const string UpgradeProtocolKey =
            "CraftPeak.Upgrade.Protocol";

        private const string UpgradeRevisionKey =
            "CraftPeak.Upgrade.Revision";

        private const string UpgradeOwnerKey =
            "CraftPeak.Upgrade.Owner";

        private const string UpgradeRunIdKey =
            "CraftPeak.Upgrade.RunId";

        private const string UpgradeResourceKey =
            "CraftPeak.Upgrade.Resource";

        private const string UpgradeGatherKey =
            "CraftPeak.Upgrade.Gather";

        private const string UpgradeStackKey =
            "CraftPeak.Upgrade.Stack";

        private const string UpgradeCampfireKey =
            "CraftPeak.Upgrade.Campfire";

        private const string UpgradeYieldKey =
            "CraftPeak.Upgrade.Yield";

        private const string UpgradeBaseHoldKey =
            "CraftPeak.Upgrade.BaseHold";

        private const string UpgradeBaseStackKey =
            "CraftPeak.Upgrade.BaseStack";

        private const string UpgradeBaseCampfireKey =
            "CraftPeak.Upgrade.BaseCampfire";

        private const int UpgradeProtocolVersion = 1;

        private const int ResourceUpgradeMaximum = 4;
        private const int GatherUpgradeMaximum = 4;
        private const int StackUpgradeMaximum = 4;
        private const int CampfireUpgradeMaximum = 3;
        private const int YieldUpgradeMaximum = 1;

        private const double MinimumRequestIntervalSeconds = 0.25d;
        private const float PartyResourceCacheSeconds = 1.00f;

        private static readonly float[] GatherTimeFactors =
        {
            1f,
            0.80f,
            0.65f,
            0.50f,
            0.35f
        };

        private static readonly int[] StackCapacityBonuses =
        {
            0,
            5,
            10,
            20,
            40
        };

        private static readonly float[] CampfireRequirementFactors =
        {
            1f,
            0.75f,
            0.50f,
            0.25f
        };

        private static readonly ConfigDefinition
            InventoryMaximumDefinition =
                new ConfigDefinition(
                    "01. 인벤토리 적재 설정",
                    "슬롯당 최대 적재 수량");

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

        private const float RequestTimeoutSeconds = 6f;

        private const int CraftRecipesPerPage = 8;
        private const int MaximumVisibleInventorySlots = 8;

        private const ushort FireWoodItemId = 28;
        private const ushort StoneItemId = 72;
        private const ushort ConchItemId = 69;

        private const ushort BinocularsItemId = 14;
        private const ushort BingBongItemId = 13;
        private const ushort BugleItemId = 15;
        private const ushort FrisbeeItemId = 99;

        private const ushort GuidebookItemId = 34;
        private const ushort ScrollItemId = 49;

        private const ushort WeirdShroomItemId = 51;
        private const ushort StrangeGemItemId = 112;

        private const ushort TorchItemId = 109;
        private const ushort FlareItemId = 32;

        private static readonly ushort[] SaleResourceIds =
        {
            WeirdShroomItemId,
            FireWoodItemId,
            BinocularsItemId,
            BingBongItemId,
            BugleItemId,
            ConchItemId,
            FrisbeeItemId,
            GuidebookItemId,
            ScrollItemId,
            StoneItemId,
            StrangeGemItemId
        };

        private static readonly ushort[] CommonIds =
        {
            FireWoodItemId,
            StoneItemId,
            ConchItemId
        };

        private static readonly ushort[] NormalIds =
        {
            BinocularsItemId,
            BingBongItemId,
            BugleItemId,
            FrisbeeItemId
        };

        private static readonly ushort[] RareIds =
        {
            GuidebookItemId,
            ScrollItemId
        };

        private static readonly PlanePartRecipe[] PlanePartRecipes =
        {
            new PlanePartRecipe(
                0,
                "연료 제어 모듈",
                "해안 → 열대/뿌리숲",
                1,
                30,
                new IngredientCost(FireWoodItemId, 6),
                new IngredientCost(StoneItemId, 4),
                new IngredientCost(ConchItemId, 2)),

            new PlanePartRecipe(
                1,
                "날개 연결 모듈",
                "열대/뿌리숲 → 메사/고산지대",
                2,
                70,
                new IngredientCost(BinocularsItemId, 2),
                new IngredientCost(BugleItemId, 2),
                new IngredientCost(FrisbeeItemId, 2),
                new IngredientCost(GuidebookItemId, 1)),

            new PlanePartRecipe(
                2,
                "고도 조절 모듈",
                "메사/고산지대 → 칼데라",
                3,
                140,
                new IngredientCost(GuidebookItemId, 2),
                new IngredientCost(ScrollItemId, 2),
                new IngredientCost(WeirdShroomItemId, 1),
                new IngredientCost(StoneItemId, 4)),

            new PlanePartRecipe(
                3,
                "내열 추진 모듈",
                "칼데라 → 가마",
                4,
                280,
                new IngredientCost(GuidebookItemId, 3),
                new IngredientCost(ScrollItemId, 3),
                new IngredientCost(WeirdShroomItemId, 2),
                new IngredientCost(StrangeGemItemId, 1))
        };

        private readonly List<CraftRecipe> craftRecipes =
            new List<CraftRecipe>();

        private readonly Dictionary<ushort, CraftRecipe>
            craftRecipesByOutputId =
                new Dictionary<ushort, CraftRecipe>();

        private readonly Dictionary<int, double>
            lastSellRequestAtByActor =
                new Dictionary<int, double>();

        private readonly Dictionary<int, double>
            lastCraftRequestAtByActor =
                new Dictionary<int, double>();

        private readonly Dictionary<int, double>
            lastUpgradeRequestAtByActor =
                new Dictionary<int, double>();

        private readonly Dictionary<int, double>
            lastPartRequestAtByActor =
                new Dictionary<int, double>();

        private CraftHubWindow activeWindow;

        private HubTab currentTab =
            HubTab.Description;

        private UpgradeKind selectedUpgradeKind =
            UpgradeKind.ResourceGrade;

        private int selectedCraftRecipeIndex = -1;
        private int craftPage;
        private int selectedSellSlotId = -1;
        private int selectedPartIndex;

        private PendingRequest pendingRequest =
            PendingRequest.None;

        private float requestStartedAt;

        private bool waitingForNewRun = true;
        private int cachedSharedMoney;

        private UpgradeState upgradeState =
            UpgradeState.CreateDefault();

        private bool upgradeStateLoaded;
        private bool gameplayScene;
        private bool pendingFreshUpgradeRun = true;

        private float partyResourceCacheUntil;

        private readonly Dictionary<ushort, int>
            cachedPartyResourceCounts =
                new Dictionary<ushort, int>();

        private readonly StringBuilder
            sharedTextBuilder =
                new StringBuilder(384);

        private static TMP_FontAsset cachedFontAsset;

        private int partsRevision;
        private string partsRunId = string.Empty;
        private int purchasedPartsMask;
        private int consumedPartsMask;
        private bool peakUnlocked;
        private bool partsStateLoaded;
        private bool upgradeRoomStateDirty = true;
        private bool partsRoomStateDirty = true;

        private int lastAppliedUpgradeRevision = -1;
        private string lastAppliedUpgradeRunId =
            string.Empty;

        private ConfigEntry<bool>
            failureEnabledConfig;

        private ConfigEntry<bool>
            consumeCostOnFailureConfig;

        private UpgradeFormulaConfig resourceUpgradeFormula;
        private UpgradeFormulaConfig gatherUpgradeFormula;
        private UpgradeFormulaConfig stackUpgradeFormula;
        private UpgradeFormulaConfig campfireUpgradeFormula;

        private ConfigEntry<int>
            doubleYieldCostConfig;

        private ConfigEntry<float>
            doubleYieldChanceConfig;

        private string upgradeStatus =
            "강화 항목을 선택하세요.";

        private string craftStatus =
            "제작할 아이템을 선택하세요.";

        private string sellStatus =
            "판매할 인벤토리 슬롯을 선택하세요.";

        private string partsStatus =
            "현재 세그먼트에 필요한 비행기 부품을 구매하세요.";

        internal static CraftHub Instance
        {
            get;
            private set;
        }

        internal static ManualLogSource ModLogger
        {
            get;
            private set;
        }

        internal enum HubTab
        {
            Description = 0,
            Upgrade = 1,
            Craft = 2,
            Sell = 3,
            Parts = 4
        }

        internal enum PendingRequest
        {
            None = 0,
            Sell = 1,
            Craft = 2,
            Upgrade = 3,
            Parts = 4
        }

        internal enum RecipeTier
        {
            Basic = 0,
            Standard = 1,
            Advanced = 2,
            Special = 3,
            Masterwork = 4
        }

        internal sealed class IngredientCost
        {
            public ushort ItemId;
            public int Count;

            public IngredientCost(
                ushort itemId,
                int count)
            {
                ItemId =
                    itemId;

                Count =
                    Mathf.Max(
                        1,
                        count);
            }
        }

        internal sealed class CraftRecipe
        {
            public ushort OutputItemId;
            public Item OutputPrefab;

            public string DisplayName;
            public string Category;

            public RecipeTier Tier;

            public int MoneyCost;
            public float SuccessChance;

            public readonly List<IngredientCost>
                Ingredients =
                    new List<IngredientCost>();
        }

        private sealed class PlanePartRecipe
        {
            public int Index;
            public string Name;
            public string Route;
            public int RequiredResourceLevel;
            public int MoneyCost;
            public readonly List<IngredientCost> Ingredients =
                new List<IngredientCost>();

            public PlanePartRecipe(
                int index,
                string name,
                string route,
                int requiredResourceLevel,
                int moneyCost,
                params IngredientCost[] ingredients)
            {
                Index = index;
                Name = name ?? string.Empty;
                Route = route ?? string.Empty;
                RequiredResourceLevel = Mathf.Clamp(requiredResourceLevel, 1, 4);
                MoneyCost = Mathf.Max(0, moneyCost);

                if (ingredients != null)
                {
                    Ingredients.AddRange(ingredients);
                }
            }
        }


        internal enum UpgradeKind
        {
            ResourceGrade = 0,
            GatherSpeed = 1,
            StackCapacity = 2,
            CampfireEfficiency = 3,
            DoubleYield = 4
        }

        private sealed class UpgradeFormulaConfig
        {
            public ConfigEntry<int> BaseCost;
            public ConfigEntry<int> CostGrowth;
            public ConfigEntry<float> StartChance;
            public ConfigEntry<float> ChanceLoss;
        }

        private sealed class UpgradeState
        {
            public int Protocol;
            public int Revision;
            public int OwnerActor;
            public string RunId;

            public int ResourceLevel;
            public int GatherLevel;
            public int StackLevel;
            public int CampfireLevel;
            public int YieldMultiplier;

            public float BaseHoldSeconds;
            public int BaseStackCount;
            public int[] BaseCampfireMaterials;

            public UpgradeState Clone()
            {
                return
                    new UpgradeState
                    {
                        Protocol =
                            Protocol,

                        Revision =
                            Revision,

                        OwnerActor =
                            OwnerActor,

                        RunId =
                            RunId ??
                            string.Empty,

                        ResourceLevel =
                            ResourceLevel,

                        GatherLevel =
                            GatherLevel,

                        StackLevel =
                            StackLevel,

                        CampfireLevel =
                            CampfireLevel,

                        YieldMultiplier =
                            YieldMultiplier,

                        BaseHoldSeconds =
                            BaseHoldSeconds,

                        BaseStackCount =
                            BaseStackCount,

                        BaseCampfireMaterials =
                            CloneIntArray(
                                BaseCampfireMaterials)
                    };
            }

            public static UpgradeState CreateDefault()
            {
                return
                    new UpgradeState
                    {
                        Protocol =
                            UpgradeProtocolVersion,

                        Revision =
                            0,

                        OwnerActor =
                            0,

                        RunId =
                            string.Empty,

                        ResourceLevel =
                            0,

                        GatherLevel =
                            0,

                        StackLevel =
                            0,

                        CampfireLevel =
                            0,

                        YieldMultiplier =
                            1,

                        BaseHoldSeconds =
                            10f,

                        BaseStackCount =
                            10,

                        BaseCampfireMaterials =
                            new[]
                            {
                                1,
                                1,
                                1
                            }
                    };
            }
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

        private sealed class PlannedIngredientUnit
        {
            public IngredientLocation Location;
            public ushort ItemId;
        }

        private sealed class CraftConsumptionPlan
        {
            public readonly List<PlannedIngredientUnit>
                Units =
                    new List<PlannedIngredientUnit>();
        }

        private struct ConsumedSelectedSlot
        {
            public int ActorNumber;
            public byte SlotId;
        }

        internal struct PickupBonusState
        {
            public bool Eligible;
            public global::Player Player;
            public ushort ItemId;
            public int CountBefore;
        }

        public static int ResourceYieldMultiplier
        {
            get;
            private set;
        } = 1;

        public static int ResourceUpgradeLevel
        {
            get
            {
                return
                    Instance != null
                        ? Instance.upgradeState
                            .ResourceLevel
                        : 0;
            }
        }

        public static int GatherUpgradeLevel
        {
            get
            {
                return
                    Instance != null
                        ? Instance.upgradeState
                            .GatherLevel
                        : 0;
            }
        }

        public static int StackUpgradeLevel
        {
            get
            {
                return
                    Instance != null
                        ? Instance.upgradeState
                            .StackLevel
                        : 0;
            }
        }

        public static int CampfireUpgradeLevel
        {
            get
            {
                return
                    Instance != null
                        ? Instance.upgradeState
                            .CampfireLevel
                        : 0;
            }
        }

        public static bool DoubleYieldUnlocked
        {
            get
            {
                return
                    ResourceYieldMultiplier >=
                    2;
            }
        }

        private void Awake()
        {
            Instance =
                this;

            ModLogger =
                Logger;

            BindUpgradeConfig();

            SceneManager.sceneLoaded +=
                HandleSceneLoaded;

            SceneManager.sceneUnloaded +=
                HandleSceneUnloaded;

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
                " loaded. Existing Shop/Store/Upgrade files are not required. Press P.");
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

            CloseHub();
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -=
                HandleSceneLoaded;

            SceneManager.sceneUnloaded -=
                HandleSceneUnloaded;

            CloseHub();

            lastSellRequestAtByActor.Clear();
            lastCraftRequestAtByActor.Clear();
            lastUpgradeRequestAtByActor.Clear();
            lastPartRequestAtByActor.Clear();

            if (Instance == this)
            {
                Instance =
                    null;
            }

            ModLogger =
                null;
        }

        private void Update()
        {
            // 요청 타임아웃과 P키 입력만 매 프레임 확인합니다.
            // 돈, 제작식, 파티 인벤토리, 강화, 부품 데이터는
            // P 메뉴를 여는 순간 또는 실제 네트워크 이벤트가 발생했을 때만 읽습니다.
            UpdatePendingRequest();

            if (activeWindow != null &&
                !activeWindow.isOpen)
            {
                DestroyHubObject();
            }

            Keyboard keyboard =
                Keyboard.current;

            if (keyboard == null ||
                !keyboard.pKey
                    .wasPressedThisFrame)
            {
                return;
            }

            if (activeWindow != null)
            {
                CloseHub();
                return;
            }

            if (CanOpenHub())
            {
                OpenHub();
            }
        }

        private void HandleSceneLoaded(
            Scene scene,
            LoadSceneMode mode)
        {
            CloseHub();

            pendingRequest =
                PendingRequest.None;

            currentTab =
                HubTab.Description;

            selectedUpgradeKind =
                UpgradeKind.ResourceGrade;

            selectedCraftRecipeIndex =
                -1;

            selectedSellSlotId =
                -1;

            craftPage =
                0;

            // ItemDatabase 제작식은 첫 P 입력에서 한 번만 만들고
            // 씬 이동마다 다시 생성하지 않습니다.

            upgradeStatus =
                "강화 항목을 선택하세요.";

            craftStatus =
                "제작할 아이템을 선택하세요.";

            sellStatus =
                "판매할 인벤토리 슬롯을 선택하세요.";

            partsStatus =
                "현재 세그먼트에 필요한 비행기 부품을 구매하세요.";

            selectedPartIndex =
                0;

            partsRevision =
                0;

            partsRunId =
                string.Empty;

            purchasedPartsMask =
                0;

            consumedPartsMask =
                0;

            peakUnlocked =
                false;

            bool excluded =
                IsExcludedScene(
                    scene);

            gameplayScene =
                !excluded;

            if (IsAirportScene(
                    scene))
            {
                waitingForNewRun =
                    true;

                cachedSharedMoney =
                    0;

                pendingFreshUpgradeRun =
                    true;

                partsStateLoaded =
                    false;

                upgradeRoomStateDirty =
                    true;

                partsRoomStateDirty =
                    true;

                RestoreBaseUpgradeEffects();

                Logger.LogInfo(
                    "Unified hub disabled in Airport. Next gameplay run starts with fresh money and upgrades.");

                return;
            }

            if (excluded)
            {
                RestoreBaseUpgradeEffects();
                return;
            }

            upgradeStateLoaded =
                false;

            partsStateLoaded =
                false;

            upgradeRoomStateDirty =
                true;

            partsRoomStateDirty =
                true;

            partyResourceCacheUntil =
                0f;

            Logger.LogInfo(
                "Unified hub enabled in gameplay scene: " +
                scene.name);
        }

        private void HandleSceneUnloaded(
            Scene scene)
        {
            CloseHub();
        }

        private static bool CanOpenHub()
        {
            if (!IsGameplayScene() ||
                LoadingScreenHandler.loading ||
                Character.localCharacter == null ||
                global::Player.localPlayer == null)
            {
                return false;
            }

            GUIManager gui =
                GUIManager.instance;

            return
                gui != null &&
                !GUIManager.InPauseMenu &&
                !gui.wheelActive;
        }

        private static bool IsGameplayScene()
        {
            return
                !IsExcludedScene(
                    SceneManager
                        .GetActiveScene());
        }

        private static bool IsAirportScene(
            Scene scene)
        {
            return
                scene.IsValid() &&
                scene.isLoaded &&
                string.Equals(
                    scene.name,
                    "Airport",
                    StringComparison.OrdinalIgnoreCase);
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

        private void LoadHubDataOnDemand()
        {
            RefreshSharedMoneyFromRoom();
            InitializeRunMoneyIfNeeded();

            LoadUpgradeStateOnDemand();
            LoadPartsStateOnDemand();

            int currentSegment =
                GetCurrentSegmentIndex();

            if (currentSegment >=
                    (int)Segment.Beach &&
                currentSegment <=
                    (int)Segment.Caldera)
            {
                selectedPartIndex =
                    currentSegment;
            }

            partyResourceCacheUntil =
                0f;
        }

        private void OpenHub()
        {
            if (activeWindow != null)
            {
                return;
            }

            LoadHubDataOnDemand();

            GameObject root =
                new GameObject(
                    "CraftPeak_UnifiedHub",
                    typeof(RectTransform),
                    typeof(Canvas),
                    typeof(CanvasScaler),
                    typeof(GraphicRaycaster),
                    typeof(CraftHubWindow));

            UnityEngine.Object
                .DontDestroyOnLoad(
                    root);

            Canvas canvas =
                root.GetComponent<Canvas>();

            canvas.renderMode =
                RenderMode.ScreenSpaceOverlay;

            canvas.sortingOrder =
                530;

            CanvasScaler scaler =
                root.GetComponent<CanvasScaler>();

            scaler.uiScaleMode =
                CanvasScaler
                    .ScaleMode
                    .ScaleWithScreenSize;

            scaler.referenceResolution =
                new Vector2(
                    1920f,
                    1080f);

            scaler.screenMatchMode =
                CanvasScaler
                    .ScreenMatchMode
                    .MatchWidthOrHeight;

            scaler.matchWidthOrHeight =
                0.5f;

            activeWindow =
                root.GetComponent<
                    CraftHubWindow>();

            BuildHubVisuals(
                activeWindow);

            activeWindow.Initialize(
                this);

            if (selectedCraftRecipeIndex <
                    0 &&
                craftRecipes.Count >
                    0)
            {
                selectedCraftRecipeIndex =
                    0;
            }

            RefreshWindow();

            Logger.LogInfo(
                "Unified hub opened.");
        }

        public void CloseHub()
        {
            if (activeWindow == null)
            {
                return;
            }

            DestroyHubObject();

            Logger.LogInfo(
                "Unified hub closed.");
        }

        /// <summary>
        /// 기존 Shop/Store/Upgrade 호출부가 통합 허브의 특정 탭을 열 수 있게 하는
        /// 호환 진입점입니다. 별도 플러그인이나 별도 UI는 생성하지 않습니다.
        /// </summary>
        internal void OpenCompatibilityTab(
            HubTab tab)
        {
            currentTab =
                tab;

            if (activeWindow == null &&
                CanOpenHub())
            {
                OpenHub();
            }

            SelectTab(
                tab);
        }

        internal void RefreshCompatibilityWindow()
        {
            RefreshWindow();
        }

        private void DestroyHubObject()
        {
            if (activeWindow == null)
            {
                return;
            }

            CraftHubWindow window =
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

        internal void SelectTab(
            HubTab tab)
        {
            currentTab =
                tab;

            if (tab ==
                HubTab.Craft &&
                craftRecipes.Count ==
                    0)
            {
                EnsureCraftRecipesBuilt();

                if (selectedCraftRecipeIndex <
                        0 &&
                    craftRecipes.Count >
                        0)
                {
                    selectedCraftRecipeIndex =
                        0;
                }
            }

            if (tab ==
                HubTab.Parts)
            {
                int currentSegment =
                    GetCurrentSegmentIndex();

                if (currentSegment >=
                        (int)Segment.Beach &&
                    currentSegment <=
                        (int)Segment.Caldera)
                {
                    selectedPartIndex =
                        currentSegment;
                }
            }

            partyResourceCacheUntil =
                0f;

            RefreshWindow();
        }

        internal HubTab CurrentTab
        {
            get
            {
                return currentTab;
            }
        }

        private void RefreshWindow()
        {
            if (activeWindow != null)
            {
                activeWindow.RefreshContents();
            }
        }

        internal int SharedMoney
        {
            get
            {
                return
                    cachedSharedMoney;
            }
        }

        internal bool IsPending(
            PendingRequest request)
        {
            return
                pendingRequest ==
                request;
        }

        internal string GetTabStatus(
            HubTab tab)
        {
            switch (tab)
            {
                case HubTab.Upgrade:
                    return upgradeStatus;

                case HubTab.Craft:
                    return craftStatus;

                case HubTab.Sell:
                    return sellStatus;

                case HubTab.Parts:
                    return partsStatus;

                case HubTab.Description:
                    return string.Empty;

                default:
                    return string.Empty;
            }
        }

        private void SetTabStatus(
            HubTab tab,
            string message)
        {
            string safe =
                message ??
                string.Empty;

            switch (tab)
            {
                case HubTab.Upgrade:
                    upgradeStatus =
                        safe;
                    break;

                case HubTab.Craft:
                    craftStatus =
                        safe;
                    break;

                case HubTab.Sell:
                    sellStatus =
                        safe;
                    break;

                case HubTab.Parts:
                    partsStatus =
                        safe;
                    break;
            }

            RefreshWindow();
        }

        // -----------------------------------------------------------------
        // Network requests and shared money
        // -----------------------------------------------------------------

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
                if (PhotonNetwork.IsMasterClient)
                {
                    ProcessSellRequestOnHost(
                        photonEvent.Sender,
                        photonEvent.CustomData as
                            object[]);
                }

                return;
            }

            if (photonEvent.Code ==
                SellResultEventCode)
            {
                HandleSellResult(
                    photonEvent.CustomData as
                        object[]);

                return;
            }

            if (photonEvent.Code ==
                CraftRequestEventCode)
            {
                if (PhotonNetwork.IsMasterClient)
                {
                    ProcessCraftRequestOnHost(
                        photonEvent.Sender,
                        photonEvent.CustomData as
                            object[]);
                }

                return;
            }

            if (photonEvent.Code ==
                CraftResultEventCode)
            {
                HandleCraftResult(
                    photonEvent.CustomData as
                        object[]);

                return;
            }

            if (photonEvent.Code ==
                ConsumedSlotEventCode)
            {
                HandleConsumedSelectedSlots(
                    photonEvent.CustomData as
                        object[]);

                return;
            }

            if (photonEvent.Code ==
                UpgradeRequestEventCode)
            {
                if (PhotonNetwork.IsMasterClient)
                {
                    ProcessUpgradeRequestOnHost(
                        photonEvent.Sender,
                        photonEvent.CustomData as
                            object[]);
                }

                return;
            }

            if (photonEvent.Code ==
                UpgradeResultEventCode)
            {
                HandleUpgradeResult(
                    photonEvent.CustomData as
                        object[]);

                return;
            }

            if (photonEvent.Code ==
                PartPurchaseRequestEventCode)
            {
                if (PhotonNetwork.IsMasterClient)
                {
                    ProcessPartPurchaseRequestOnHost(
                        photonEvent.Sender,
                        photonEvent.CustomData as
                            object[]);
                }

                return;
            }

            if (photonEvent.Code ==
                PartPurchaseResultEventCode)
            {
                HandlePartPurchaseResult(
                    photonEvent.CustomData as
                        object[]);

                return;
            }

            if (photonEvent.Code ==
                ProgressionNoticeEventCode)
            {
                object[] payload =
                    photonEvent.CustomData as
                        object[];

                string message =
                    payload != null &&
                    payload.Length >
                        0
                        ? payload[0] as
                            string
                        : null;

                CampfireGate.NotifyLocalPlayer(
                    string.IsNullOrEmpty(
                        message)
                        ? "진행 조건을 확인하세요."
                        : message);

                return;
            }

            if (photonEvent.Code ==
                FinalFlareCompletedEventCode)
            {
                CloseHub();

                CampfireGate.NotifyLocalPlayer(
                    "정상에서 최종 조명탄 제작 완료. 탈출 신호를 발사했습니다.");
            }
        }

        private void UpdatePendingRequest()
        {
            if (pendingRequest ==
                PendingRequest.None)
            {
                return;
            }

            if (Time.unscaledTime -
                    requestStartedAt <=
                RequestTimeoutSeconds)
            {
                return;
            }

            HubTab tab =
                RequestToTab(
                    pendingRequest);

            pendingRequest =
                PendingRequest.None;

            SetTabStatus(
                tab,
                "요청 시간이 초과되었습니다.");
        }

        private static HubTab RequestToTab(
            PendingRequest request)
        {
            switch (request)
            {
                case PendingRequest.Sell:
                    return HubTab.Sell;

                case PendingRequest.Craft:
                    return HubTab.Craft;

                case PendingRequest.Upgrade:
                    return HubTab.Upgrade;

                case PendingRequest.Parts:
                    return HubTab.Parts;

                default:
                    return HubTab.Upgrade;
            }
        }

        private void HandleSellResult(
            object[] resultData)
        {
            pendingRequest =
                PendingRequest.None;

            partyResourceCacheUntil =
                0f;

            if (resultData == null ||
                resultData.Length <
                    6)
            {
                SetTabStatus(
                    HubTab.Sell,
                    "판매 결과 데이터가 올바르지 않습니다.");

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
                SetTabStatus(
                    HubTab.Sell,
                    "판매 결과를 해석하지 못했습니다.");

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

            SetTabStatus(
                HubTab.Sell,
                string.IsNullOrEmpty(
                    message)
                    ? (
                        success
                            ? "판매했습니다."
                            : "판매하지 못했습니다."
                    )
                    : message);
        }

        private void HandleCraftResult(
            object[] resultData)
        {
            pendingRequest =
                PendingRequest.None;

            partyResourceCacheUntil =
                0f;

            if (resultData == null ||
                resultData.Length <
                    5)
            {
                SetTabStatus(
                    HubTab.Craft,
                    "제작 결과 데이터가 올바르지 않습니다.");

                return;
            }

            try
            {
                bool materialsConsumed =
                    Convert.ToBoolean(
                        resultData[0]);

                bool success =
                    Convert.ToBoolean(
                        resultData[1]);

                string message =
                    resultData[3] as
                        string ??
                    string.Empty;

                if (string.IsNullOrEmpty(
                        message))
                {
                    message =
                        success
                            ? "제작에 성공했습니다."
                            : (
                                materialsConsumed
                                    ? "제작에 실패했습니다."
                                    : "제작 요청이 거부되었습니다."
                            );
                }

                SetTabStatus(
                    HubTab.Craft,
                    message);
            }
            catch (Exception)
            {
                SetTabStatus(
                    HubTab.Craft,
                    "제작 결과를 해석하지 못했습니다.");
            }
        }

        private void HandleUpgradeResult(
            object[] resultData)
        {
            pendingRequest =
                PendingRequest.None;

            if (resultData == null ||
                resultData.Length <
                    3)
            {
                SetTabStatus(
                    HubTab.Upgrade,
                    "강화 결과 데이터가 올바르지 않습니다.");

                return;
            }

            string message =
                resultData[1] as
                    string ??
                "강화 결과를 받았습니다.";

            ReadUpgradeStateFromRoom(
                false);

            SetTabStatus(
                HubTab.Upgrade,
                message);
        }

        private void InitializeRunMoneyIfNeeded()
        {
            if (!waitingForNewRun ||
                !PhotonNetwork.InRoom ||
                PhotonNetwork.CurrentRoom ==
                    null ||
                !gameplayScene)
            {
                return;
            }

            object existingValue;

            bool propertyExists =
                PhotonNetwork.CurrentRoom
                    .CustomProperties
                    .TryGetValue(
                        SharedMoneyKey,
                        out existingValue);

            if (propertyExists)
            {
                waitingForNewRun =
                    false;

                cachedSharedMoney =
                    ReadSharedMoney();

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

        private static int ReadSharedMoney()
        {
            if (!PhotonNetwork.InRoom ||
                PhotonNetwork.CurrentRoom ==
                    null)
            {
                return
                    Instance != null
                        ? Instance.cachedSharedMoney
                        : 0;
            }

            object value;

            if (!PhotonNetwork.CurrentRoom
                    .CustomProperties
                    .TryGetValue(
                        SharedMoneyKey,
                        out value) ||
                value == null)
            {
                return 0;
            }

            try
            {
                return
                    Mathf.Max(
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
            int roomMoney =
                ReadSharedMoney();

            if (roomMoney ==
                cachedSharedMoney)
            {
                return;
            }

            cachedSharedMoney =
                roomMoney;

            RefreshWindow();
        }

        private static void SetSharedMoneyOnHost(
            int money)
        {
            if (!PhotonNetwork.InRoom ||
                !PhotonNetwork.IsMasterClient ||
                PhotonNetwork.CurrentRoom ==
                    null)
            {
                return;
            }

            int safeMoney =
                Mathf.Max(
                    0,
                    money);

            ExitGames.Client.Photon.Hashtable
                properties =
                    new ExitGames.Client.Photon.Hashtable
                    {
                        {
                            SharedMoneyKey,
                            safeMoney
                        }
                    };

            PhotonNetwork.CurrentRoom
                .SetCustomProperties(
                    properties);

            if (Instance != null)
            {
                Instance.cachedSharedMoney =
                    safeMoney;

                Instance.RefreshWindow();
            }
        }

        // -----------------------------------------------------------------
        // On-demand progression / airplane parts
        // -----------------------------------------------------------------

        private void LoadPartsStateOnDemand()
        {
            partsRoomStateDirty =
                false;

            if (!PhotonNetwork.InRoom ||
                PhotonNetwork.CurrentRoom == null)
            {
                return;
            }

            ExitGames.Client.Photon.Hashtable
                properties =
                    PhotonNetwork.CurrentRoom
                        .CustomProperties;

            string currentRunId =
                ReadRunId();

            object protocolValue = null;
            object revisionValue = null;

            bool found =
                properties.TryGetValue(
                    PartsProtocolKey,
                    out protocolValue) &&
                properties.TryGetValue(
                    PartsRevisionKey,
                    out revisionValue);

            if (!found)
            {
                if (PhotonNetwork.IsMasterClient)
                {
                    PublishPartsState(
                        0,
                        0,
                        false,
                        currentRunId,
                        "Fresh parts state");
                }
                else
                {
                    partsRevision =
                        0;

                    partsRunId =
                        currentRunId;

                    purchasedPartsMask =
                        0;

                    consumedPartsMask =
                        0;

                    peakUnlocked =
                        false;

                    partsStateLoaded =
                        true;
                }

                return;
            }

            try
            {
                int protocol =
                    Convert.ToInt32(
                        protocolValue);

                if (protocol !=
                    PartsProtocolVersion)
                {
                    Logger.LogError(
                        "Parts protocol mismatch. Room=" +
                        protocol +
                        " | Local=" +
                        PartsProtocolVersion);

                    return;
                }

                string roomRunId =
                    ReadString(
                        properties,
                        PartsRunIdKey);

                bool runChanged =
                    !string.IsNullOrEmpty(
                        currentRunId) &&
                    !string.Equals(
                        roomRunId,
                        currentRunId,
                        StringComparison.Ordinal);

                if (runChanged)
                {
                    if (PhotonNetwork.IsMasterClient)
                    {
                        PublishPartsState(
                            0,
                            0,
                            false,
                            currentRunId,
                            "New run parts reset");
                    }
                    else
                    {
                        partsRevision =
                            0;

                        partsRunId =
                            currentRunId;

                        purchasedPartsMask =
                            0;

                        consumedPartsMask =
                            0;

                        peakUnlocked =
                            false;

                        partsStateLoaded =
                            true;
                    }

                    return;
                }

                partsRevision =
                    Mathf.Max(
                        0,
                        Convert.ToInt32(
                            revisionValue));

                partsRunId =
                    string.IsNullOrEmpty(
                        currentRunId)
                        ? roomRunId
                        : currentRunId;

                purchasedPartsMask =
                    ReadInt(
                        properties,
                        PartsPurchasedMaskKey,
                        0) &
                    0x0F;

                consumedPartsMask =
                    ReadInt(
                        properties,
                        PartsConsumedMaskKey,
                        0) &
                    0x0F;

                peakUnlocked =
                    ReadBool(
                        properties,
                        PeakUnlockedKey,
                        false);

                partsStateLoaded =
                    true;
            }
            catch (Exception exception)
            {
                Logger.LogError(
                    "Parts state read failed: " +
                    exception);
            }
        }

        private bool PublishPartsState(
            int purchasedMask,
            int consumedMask,
            bool unlockedPeak,
            string runId,
            string reason)
        {
            if (!PhotonNetwork.InRoom ||
                !PhotonNetwork.IsMasterClient ||
                PhotonNetwork.CurrentRoom == null)
            {
                return false;
            }

            int nextRevision =
                Mathf.Max(
                    0,
                    partsRevision) +
                1;

            string safeRunId =
                runId ??
                string.Empty;

            ExitGames.Client.Photon.Hashtable
                properties =
                    new ExitGames.Client.Photon.Hashtable
                    {
                        {
                            PartsProtocolKey,
                            PartsProtocolVersion
                        },
                        {
                            PartsRevisionKey,
                            nextRevision
                        },
                        {
                            PartsRunIdKey,
                            safeRunId
                        },
                        {
                            PartsPurchasedMaskKey,
                            purchasedMask &
                            0x0F
                        },
                        {
                            PartsConsumedMaskKey,
                            consumedMask &
                            0x0F
                        },
                        {
                            PeakUnlockedKey,
                            unlockedPeak
                        }
                    };

            if (!PhotonNetwork.CurrentRoom
                    .SetCustomProperties(
                        properties))
            {
                Logger.LogError(
                    "Parts state publish failed. Reason=" +
                    reason);

                return false;
            }

            partsRevision =
                nextRevision;

            partsRunId =
                safeRunId;

            purchasedPartsMask =
                purchasedMask &
                0x0F;

            consumedPartsMask =
                consumedMask &
                0x0F;

            peakUnlocked =
                unlockedPeak;

            partsStateLoaded =
                true;

            partsRoomStateDirty =
                false;

            RefreshWindow();

            Logger.LogInfo(
                "Parts state published. Reason=" +
                reason +
                " | PurchasedMask=" +
                purchasedPartsMask +
                " | ConsumedMask=" +
                consumedPartsMask +
                " | PeakUnlocked=" +
                peakUnlocked +
                ".");

            return true;
        }

        private PlanePartRecipe SelectedPartRecipe
        {
            get
            {
                return
                    selectedPartIndex >=
                        0 &&
                    selectedPartIndex <
                        PlanePartRecipes.Length
                        ? PlanePartRecipes[
                            selectedPartIndex]
                        : null;
            }
        }

        internal void SelectPart(
            int partIndex)
        {
            if (partIndex <
                    0 ||
                partIndex >=
                    PlanePartRecipes.Length)
            {
                return;
            }

            selectedPartIndex =
                partIndex;

            SetTabStatus(
                HubTab.Parts,
                PlanePartRecipes[
                    partIndex]
                    .Name +
                "을(를) 선택했습니다.");
        }

        private string BuildPartRowText(
            int partIndex)
        {
            if (partIndex <
                    0 ||
                partIndex >=
                    PlanePartRecipes.Length)
            {
                return string.Empty;
            }

            PlanePartRecipe recipe =
                PlanePartRecipes[
                    partIndex];

            int bit =
                1 <<
                partIndex;

            string stateText =
                (consumedPartsMask &
                    bit) !=
                    0
                    ? "사용 완료"
                    : (
                        (purchasedPartsMask &
                            bit) !=
                            0
                            ? "보유 중"
                            : "미구매"
                    );

            return
                (partIndex + 1) +
                ". " +
                recipe.Name +
                "  |  " +
                recipe.Route +
                "  |  " +
                stateText;
        }

        private string BuildPartDetailText(
            PlanePartRecipe recipe,
            out bool ready)
        {
            ready =
                false;

            if (recipe == null)
            {
                return
                    "구매할 비행기 부품을 선택하세요.";
            }

            Dictionary<ushort, int>
                counts =
                    GetCachedPartyResourceCounts();

            int currentSegment =
                GetCurrentSegmentIndex();

            int currentGrade =
                GetCurrentResourceLevel();

            int bit =
                1 <<
                recipe.Index;

            bool purchased =
                (purchasedPartsMask &
                    bit) !=
                    0;

            bool consumed =
                (consumedPartsMask &
                    bit) !=
                    0;

            bool correctSegment =
                currentSegment ==
                    recipe.Index;

            bool gradeReady =
                currentGrade >=
                    recipe.RequiredResourceLevel;

            bool moneyReady =
                cachedSharedMoney >=
                    recipe.MoneyCost;

            bool materialsReady =
                true;

            sharedTextBuilder.Length =
                0;

            sharedTextBuilder.Append(
                recipe.Name);

            sharedTextBuilder.Append(
                "\n");

            sharedTextBuilder.Append(
                recipe.Route);

            sharedTextBuilder.Append(
                "\n\n필요 제작 등급: ");

            sharedTextBuilder.Append(
                GetResourceGradeName(
                    recipe.RequiredResourceLevel));

            sharedTextBuilder.Append(
                gradeReady
                    ? "  <color=#79E081>충족</color>"
                    : "  <color=#FF8A80>미충족</color>");

            sharedTextBuilder.Append(
                "\n\n재료");

            for (int i = 0;
                 i <
                     recipe.Ingredients.Count;
                 i++)
            {
                IngredientCost cost =
                    recipe.Ingredients[i];

                int available;

                counts.TryGetValue(
                    cost.ItemId,
                    out available);

                bool enough =
                    available >=
                    cost.Count;

                materialsReady &=
                    enough;

                sharedTextBuilder.Append(
                    "\n");

                sharedTextBuilder.Append(
                    enough
                        ? "<color=#79E081>"
                        : "<color=#FF8A80>");

                sharedTextBuilder.Append(
                    GetIngredientDisplayName(
                        cost.ItemId));

                sharedTextBuilder.Append(
                    " ");

                sharedTextBuilder.Append(
                    available);

                sharedTextBuilder.Append(
                    "/");

                sharedTextBuilder.Append(
                    cost.Count);

                sharedTextBuilder.Append(
                    "</color>");
            }

            sharedTextBuilder.Append(
                "\n");

            sharedTextBuilder.Append(
                moneyReady
                    ? "<color=#79E081>"
                    : "<color=#FF8A80>");

            sharedTextBuilder.Append(
                "공유 돈 ");

            sharedTextBuilder.Append(
                cachedSharedMoney);

            sharedTextBuilder.Append(
                "/");

            sharedTextBuilder.Append(
                recipe.MoneyCost);

            sharedTextBuilder.Append(
                "원</color>");

            sharedTextBuilder.Append(
                "\n\n상태: ");

            if (consumed)
            {
                sharedTextBuilder.Append(
                    "이전 모닥불에서 사용 완료");
            }
            else if (purchased)
            {
                sharedTextBuilder.Append(
                    "구매 완료 · 모닥불 점화 가능");
            }
            else if (!correctSegment)
            {
                sharedTextBuilder.Append(
                    currentSegment <
                        recipe.Index
                        ? "아직 도달하지 않은 구간"
                        : "이미 지난 구간");
            }
            else
            {
                sharedTextBuilder.Append(
                    "구매 가능");
            }

            ready =
                !purchased &&
                !consumed &&
                correctSegment &&
                gradeReady &&
                moneyReady &&
                materialsReady &&
                pendingRequest ==
                    PendingRequest.None;

            return
                sharedTextBuilder
                    .ToString();
        }

        private void RequestPartPurchase()
        {
            if (pendingRequest !=
                PendingRequest.None)
            {
                SetTabStatus(
                    HubTab.Parts,
                    "다른 요청을 처리 중입니다.");

                return;
            }

            PlanePartRecipe recipe =
                SelectedPartRecipe;

            bool ready;

            BuildPartDetailText(
                recipe,
                out ready);

            if (recipe == null ||
                !ready)
            {
                SetTabStatus(
                    HubTab.Parts,
                    "현재 구간, 제작 등급, 재료와 공유 돈을 확인하세요.");

                return;
            }

            pendingRequest =
                PendingRequest.Parts;

            requestStartedAt =
                Time.unscaledTime;

            SetTabStatus(
                HubTab.Parts,
                recipe.Name +
                " 구매를 요청했습니다...");

            object[] payload =
            {
                recipe.Index
            };

            RaiseEventOptions options =
                new RaiseEventOptions
                {
                    Receivers =
                        ReceiverGroup.MasterClient
                };

            if (PhotonNetwork.IsMasterClient)
            {
                ProcessPartPurchaseRequestOnHost(
                    LocalActorNumber(),
                    payload);

                return;
            }

            bool sent =
                PhotonNetwork.RaiseEvent(
                    PartPurchaseRequestEventCode,
                    payload,
                    options,
                    SendOptions.SendReliable);

            if (!sent)
            {
                pendingRequest =
                    PendingRequest.None;

                SetTabStatus(
                    HubTab.Parts,
                    "비행기 부품 구매 요청 전송에 실패했습니다.");
            }
        }

        private void ProcessPartPurchaseRequestOnHost(
            int actorNumber,
            object[] payload)
        {
            if (!PhotonNetwork.IsMasterClient)
            {
                return;
            }

            if (payload == null ||
                payload.Length <
                    1)
            {
                SendPartPurchaseResult(
                    actorNumber,
                    false,
                    "잘못된 비행기 부품 구매 요청입니다.");

                return;
            }

            double now =
                PhotonNetwork.Time;

            double previousRequestAt;

            if (lastPartRequestAtByActor
                    .TryGetValue(
                        actorNumber,
                        out previousRequestAt) &&
                now -
                    previousRequestAt <
                MinimumRequestIntervalSeconds)
            {
                SendPartPurchaseResult(
                    actorNumber,
                    false,
                    "비행기 부품 구매 요청이 너무 빠릅니다.");

                return;
            }

            lastPartRequestAtByActor[
                actorNumber] =
                    now;

            int partIndex;

            try
            {
                partIndex =
                    Convert.ToInt32(
                        payload[0]);
            }
            catch (Exception)
            {
                SendPartPurchaseResult(
                    actorNumber,
                    false,
                    "비행기 부품 번호를 해석하지 못했습니다.");

                return;
            }

            if (partIndex <
                    0 ||
                partIndex >=
                    PlanePartRecipes.Length)
            {
                SendPartPurchaseResult(
                    actorNumber,
                    false,
                    "존재하지 않는 비행기 부품입니다.");

                return;
            }

            LoadPartsStateOnDemand();

            PlanePartRecipe recipe =
                PlanePartRecipes[
                    partIndex];

            int currentSegment =
                GetCurrentSegmentIndex();

            if (currentSegment !=
                recipe.Index)
            {
                SendPartPurchaseResult(
                    actorNumber,
                    false,
                    "현재 구간에서 필요한 비행기 부품만 구매할 수 있습니다.");

                return;
            }

            int currentGrade =
                GetCurrentResourceLevel();

            if (currentGrade <
                recipe.RequiredResourceLevel)
            {
                SendPartPurchaseResult(
                    actorNumber,
                    false,
                    GetResourceGradeName(
                        recipe.RequiredResourceLevel) +
                    " 제작 등급이 필요합니다.");

                return;
            }

            int bit =
                1 <<
                recipe.Index;

            if ((purchasedPartsMask &
                    bit) !=
                    0 ||
                (consumedPartsMask &
                    bit) !=
                    0)
            {
                SendPartPurchaseResult(
                    actorNumber,
                    false,
                    "이미 구매했거나 사용한 비행기 부품입니다.");

                return;
            }

            int money =
                ReadSharedMoney();

            if (money <
                recipe.MoneyCost)
            {
                SendPartPurchaseResult(
                    actorNumber,
                    false,
                    "공유 돈이 부족합니다.");

                return;
            }

            CraftConsumptionPlan plan;
            string missingMessage;

            if (!TryBuildPartConsumptionPlan(
                    recipe,
                    out plan,
                    out missingMessage))
            {
                SendPartPurchaseResult(
                    actorNumber,
                    false,
                    missingMessage);

                return;
            }

            List<ConsumedSelectedSlot>
                consumedSlots;

            if (!TryConsumePlan(
                    plan,
                    out consumedSlots))
            {
                SendPartPurchaseResult(
                    actorNumber,
                    false,
                    "재료 소비 중 인벤토리가 변경되었습니다. 다시 시도하세요.");

                return;
            }

            SetSharedMoneyOnHost(
                money -
                recipe.MoneyCost);

            BroadcastConsumedSelectedSlots(
                consumedSlots);

            int nextPurchasedMask =
                purchasedPartsMask |
                bit;

            if (!PublishPartsState(
                    nextPurchasedMask,
                    consumedPartsMask,
                    peakUnlocked,
                    ReadRunId(),
                    "Plane part purchased: " +
                    recipe.Name))
            {
                SetSharedMoneyOnHost(
                    ReadSharedMoney() +
                    recipe.MoneyCost);

                SendPartPurchaseResult(
                    actorNumber,
                    false,
                    "부품 상태 저장에 실패했습니다. 공유 돈은 환불되었지만 재료는 복구되지 않았습니다.");

                return;
            }

            partyResourceCacheUntil =
                0f;

            SendPartPurchaseResult(
                actorNumber,
                true,
                recipe.Name +
                " 구매 완료.\n인벤토리에는 들어가지 않으며 모닥불 진행 조건으로 저장됩니다.");
        }

        private static bool TryBuildPartConsumptionPlan(
            PlanePartRecipe recipe,
            out CraftConsumptionPlan plan,
            out string missingMessage)
        {
            CraftRecipe temporaryRecipe =
                new CraftRecipe();

            for (int i = 0;
                 i <
                     recipe.Ingredients.Count;
                 i++)
            {
                IngredientCost cost =
                    recipe.Ingredients[i];

                temporaryRecipe.Ingredients.Add(
                    new IngredientCost(
                        cost.ItemId,
                        cost.Count));
            }

            return
                TryBuildCraftConsumptionPlan(
                    temporaryRecipe,
                    out plan,
                    out missingMessage);
        }

        private void SendPartPurchaseResult(
            int targetActor,
            bool success,
            string message)
        {
            object[] payload =
            {
                success,
                message ??
                string.Empty,
                ReadSharedMoney()
            };

            if (PhotonNetwork.LocalPlayer !=
                    null &&
                PhotonNetwork.LocalPlayer.ActorNumber ==
                    targetActor)
            {
                HandlePartPurchaseResult(
                    payload);

                return;
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
                PartPurchaseResultEventCode,
                payload,
                options,
                SendOptions.SendReliable);
        }

        private void HandlePartPurchaseResult(
            object[] payload)
        {
            pendingRequest =
                PendingRequest.None;

            string message =
                payload != null &&
                payload.Length >
                    1
                    ? payload[1] as
                        string
                    : null;

            if (payload != null &&
                payload.Length >
                    2)
            {
                try
                {
                    cachedSharedMoney =
                        Mathf.Max(
                            0,
                            Convert.ToInt32(
                                payload[2]));
                }
                catch (Exception)
                {
                }
            }

            partyResourceCacheUntil =
                0f;

            partsRoomStateDirty =
                true;

            LoadPartsStateOnDemand();

            SetTabStatus(
                HubTab.Parts,
                string.IsNullOrEmpty(
                    message)
                    ? "비행기 부품 구매 결과를 받았습니다."
                    : message);
        }

        internal static bool IsCurrentCampfireRequest(
            object[] requestData)
        {
            if (requestData == null ||
                requestData.Length <
                    1)
            {
                return false;
            }

            try
            {
                int viewId =
                    Convert.ToInt32(
                        requestData[0]);

                PhotonView view =
                    PhotonView.Find(
                        viewId);

                global::Campfire requested =
                    view != null
                        ? view.GetComponent<
                            global::Campfire>()
                        : null;

                return
                    requested != null &&
                    IsCurrentSegmentCampfire(
                        requested);
            }
            catch (Exception)
            {
                return false;
            }
        }

        internal static bool IsCurrentSegmentCampfire(
            global::Campfire campfire)
        {
            if (campfire == null)
            {
                return false;
            }

            try
            {
                return
                    MapHandler.CurrentCampfire ==
                    campfire;
            }
            catch (Exception)
            {
                return false;
            }
        }

        internal static bool ValidateCampfireProgression(
            out string message)
        {
            message =
                string.Empty;

            if (Instance == null ||
                !PhotonNetwork.InRoom ||
                PhotonNetwork.CurrentRoom == null)
            {
                return true;
            }

            int segment =
                GetCurrentSegmentIndex();

            if (segment ==
                (int)Segment.TheKiln)
            {
                message =
                    "가마 구간은 비행기 부품과 모닥불 진행 대상이 아닙니다.\n정상에 도착한 뒤 P 메뉴의 제작 탭에서 최종 조명탄을 제작하세요.";

                return false;
            }

            if (segment <
                    (int)Segment.Beach ||
                segment >
                    (int)Segment.Caldera)
            {
                return true;
            }

            string currentRunId =
                ReadRoomString(
                    RunIdKey);

            string partRunId =
                ReadRoomString(
                    PartsRunIdKey);

            if (!string.IsNullOrEmpty(
                    currentRunId) &&
                !string.Equals(
                    currentRunId,
                    partRunId,
                    StringComparison.Ordinal))
            {
                message =
                    "새 등반의 비행기 부품 상태가 아직 초기화되지 않았습니다.\nP 메뉴의 부품 탭을 열어 진행 상태를 초기화하세요.";

                return false;
            }

            int requiredGrade =
                segment +
                1;

            int currentGrade =
                GetCurrentResourceLevel();

            if (currentGrade <
                requiredGrade)
            {
                message =
                    "모닥불 점화 조건 미충족\n" +
                    GetResourceGradeName(
                        requiredGrade) +
                    " 제작 등급이 필요합니다.";

                return false;
            }

            int bit =
                1 <<
                segment;

            int purchasedMask =
                ReadRoomInt(
                    PartsPurchasedMaskKey,
                    0);

            int consumedMask =
                ReadRoomInt(
                    PartsConsumedMaskKey,
                    0);

            if ((consumedMask &
                    bit) !=
                    0)
            {
                message =
                    "이 구간의 비행기 부품은 이미 사용되었습니다.";

                return false;
            }

            if ((purchasedMask &
                    bit) ==
                    0)
            {
                message =
                    "모닥불 점화 조건 미충족\nP 메뉴의 부품 탭에서 현재 구간 비행기 부품을 먼저 구매하세요.";

                return false;
            }

            return true;
        }

        internal static string BuildCampfireProgressionPrompt()
        {
            if (Instance == null)
            {
                return string.Empty;
            }

            int segment =
                GetCurrentSegmentIndex();

            if (segment ==
                (int)Segment.TheKiln)
            {
                return
                    "\n<color=#FFCF66>가마 이후 정상에 도착하면 P → 제작 → 최종 조명탄 제작</color>";
            }

            if (segment <
                    (int)Segment.Beach ||
                segment >
                    (int)Segment.Caldera)
            {
                return string.Empty;
            }

            int requiredGrade =
                segment +
                1;

            int currentGrade =
                Instance.upgradeState
                    .ResourceLevel;

            int bit =
                1 <<
                segment;

            bool purchased =
                (Instance.purchasedPartsMask &
                 bit) !=
                0;

            bool gradeReady =
                currentGrade >=
                    requiredGrade;

            string color =
                purchased &&
                gradeReady
                    ? "#79E081"
                    : "#FF8A80";

            return
                "\n<color=" +
                color +
                ">진행 조건: " +
                GetResourceGradeName(
                    requiredGrade) +
                " 등급 | 비행기 부품 " +
                (
                    purchased
                        ? "구매 완료"
                        : "미구매"
                ) +
                "</color>";
        }

        internal void MarkPartConsumedAfterCampfire(
            int sourceSegment)
        {
            if (!PhotonNetwork.IsMasterClient)
            {
                return;
            }

            if (sourceSegment <
                    (int)Segment.Beach ||
                sourceSegment >
                    (int)Segment.Caldera)
            {
                return;
            }

            // Light_Rpc 원본은 다음 세그먼트 전환을 먼저 시작할 수 있으므로,
            // Postfix에서 현재 세그먼트를 다시 읽지 않고 Prefix가 보존한 출발 구간을 사용합니다.
            LoadPartsStateOnDemand();

            int bit =
                1 <<
                sourceSegment;

            if ((purchasedPartsMask &
                    bit) ==
                    0 ||
                (consumedPartsMask &
                    bit) !=
                    0)
            {
                return;
            }

            PublishPartsState(
                purchasedPartsMask,
                consumedPartsMask |
                    bit,
                peakUnlocked,
                ReadRunId(),
                "Campfire consumed plane part for segment " +
                sourceSegment);
        }

        internal void SendProgressionNotice(
            int targetActor,
            string message)
        {
            object[] payload =
            {
                message ??
                string.Empty
            };

            if (PhotonNetwork.LocalPlayer !=
                    null &&
                PhotonNetwork.LocalPlayer.ActorNumber ==
                    targetActor)
            {
                CampfireGate.NotifyLocalPlayer(
                    message);

                return;
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
                ProgressionNoticeEventCode,
                payload,
                options,
                SendOptions.SendReliable);
        }

        private void MarkFinalFlareCompletedAndNotify()
        {
            if (!PhotonNetwork.IsMasterClient)
            {
                return;
            }

            LoadPartsStateOnDemand();

            if (!peakUnlocked)
            {
                PublishPartsState(
                    purchasedPartsMask,
                    consumedPartsMask,
                    true,
                    ReadRunId(),
                    "Final flare crafted at Peak");
            }

            CloseHub();

            RaiseEventOptions completionOptions =
                new RaiseEventOptions
                {
                    Receivers =
                        ReceiverGroup.All
                };

            PhotonNetwork.RaiseEvent(
                FinalFlareCompletedEventCode,
                Array.Empty<object>(),
                completionOptions,
                SendOptions.SendReliable);
        }

        private static int GetCurrentSegmentIndex()
        {
            try
            {
                return
                    (int)MapHandler
                        .CurrentSegmentNumber;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        private static int GetCurrentResourceLevel()
        {
            if (PhotonNetwork.InRoom &&
                PhotonNetwork.CurrentRoom !=
                    null)
            {
                return
                    Mathf.Clamp(
                        ReadRoomInt(
                            UpgradeResourceKey,
                            ResourceUpgradeLevel),
                        0,
                        ResourceUpgradeMaximum);
            }

            return
                ResourceUpgradeLevel;
        }

        private static string ReadRoomString(
            string key)
        {
            if (!PhotonNetwork.InRoom ||
                PhotonNetwork.CurrentRoom ==
                    null)
            {
                return string.Empty;
            }

            object value;

            if (!PhotonNetwork.CurrentRoom
                    .CustomProperties
                    .TryGetValue(
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

        private static int ReadRoomInt(
            string key,
            int fallback)
        {
            if (!PhotonNetwork.InRoom ||
                PhotonNetwork.CurrentRoom ==
                    null)
            {
                return fallback;
            }

            object value;

            if (!PhotonNetwork.CurrentRoom
                    .CustomProperties
                    .TryGetValue(
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

        private static bool ReadBool(
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

        // -----------------------------------------------------------------
        // Craft recipes
        // -----------------------------------------------------------------

        private bool EnsureCraftRecipesBuilt()
        {
            if (craftRecipes.Count >
                0)
            {
                return true;
            }

            ItemDatabase database =
                SingletonAsset<ItemDatabase>
                    .Instance;

            if (database == null ||
                database.itemLookup == null ||
                database.itemLookup.Count ==
                    0)
            {
                Logger.LogWarning(
                    "CraftHub could not build recipes because ItemDatabase is not ready.");

                return false;
            }

            craftRecipes.Clear();
            craftRecipesByOutputId.Clear();

            foreach (
                KeyValuePair<ushort, Item>
                    pair
                in database.itemLookup)
            {
                if (!IsCraftableOutput(
                        pair.Key,
                        pair.Value))
                {
                    continue;
                }

                CraftRecipe recipe =
                    BuildRecipe(
                        pair.Key,
                        pair.Value);

                if (recipe == null)
                {
                    continue;
                }

                craftRecipes.Add(
                    recipe);

                craftRecipesByOutputId[
                    pair.Key] =
                        recipe;
            }

            craftRecipes.Sort(
                CompareRecipes);

            Logger.LogInfo(
                "CraftHub recipes built. Count=" +
                craftRecipes.Count +
                ".");

            return
                craftRecipes.Count >
                0;
        }

        private static bool IsCraftableOutput(
            ushort itemId,
            Item prefab)
        {
            if (prefab == null ||
                prefab.gameObject == null ||
                prefab.UIData == null ||
                IsSaleResourceId(
                    itemId))
            {
                return false;
            }

            string objectName =
                prefab.gameObject.name ??
                string.Empty;

            return
                objectName.IndexOf(
                    "debug",
                    StringComparison.OrdinalIgnoreCase) <
                0;
        }

        private static CraftRecipe BuildRecipe(
            ushort itemId,
            Item prefab)
        {
            string prefabName =
                prefab.gameObject.name ??
                string.Empty;

            string normalizedName =
                prefabName
                    .ToLowerInvariant();

            string displayName =
                GetItemDisplayName(
                    prefab);

            string category =
                DetermineCategory(
                    normalizedName,
                    displayName);

            RecipeTier tier =
                DetermineTier(
                    normalizedName,
                    category,
                    prefab);

            CraftRecipe recipe =
                new CraftRecipe
                {
                    OutputItemId =
                        itemId,

                    OutputPrefab =
                        prefab,

                    DisplayName =
                        displayName,

                    Category =
                        category,

                    Tier =
                        tier
                };

            if (itemId ==
                FlareItemId)
            {
                recipe.Category =
                    "최종 탈출";

                recipe.Tier =
                    RecipeTier.Masterwork;

                recipe.MoneyCost =
                    500;

                recipe.SuccessChance =
                    100f;

                AddIngredient(
                    recipe,
                    StrangeGemItemId,
                    2);

                AddIngredient(
                    recipe,
                    WeirdShroomItemId,
                    4);

                AddIngredient(
                    recipe,
                    GuidebookItemId,
                    4);

                AddIngredient(
                    recipe,
                    ScrollItemId,
                    4);

                return recipe;
            }

            if (itemId ==
                TorchItemId)
            {
                recipe.Category =
                    "진행 필수품";

                recipe.Tier =
                    RecipeTier.Standard;

                recipe.MoneyCost =
                    4;

                recipe.SuccessChance =
                    75f;

                AddIngredient(
                    recipe,
                    FireWoodItemId,
                    2);

                AddIngredient(
                    recipe,
                    StoneItemId,
                    1);

                return recipe;
            }

            ApplyGeneratedRecipe(
                recipe,
                GetDeterministicSeed(
                    itemId,
                    prefabName));

            return recipe;
        }

        private static void ApplyGeneratedRecipe(
            CraftRecipe recipe,
            int seed)
        {
            int safeSeed =
                seed ==
                    int.MinValue
                    ? int.MaxValue
                    : Mathf.Abs(
                        seed);

            switch (recipe.Tier)
            {
                case RecipeTier.Basic:
                    recipe.MoneyCost =
                        2 +
                        safeSeed %
                        4;

                    recipe.SuccessChance =
                        93f +
                        safeSeed %
                        6;

                    AddIngredient(
                        recipe,
                        PickResource(
                            CommonIds,
                            safeSeed),
                        1 +
                        safeSeed %
                        2);

                    AddIngredient(
                        recipe,
                        PickDifferentResource(
                            CommonIds,
                            safeSeed +
                            1,
                            recipe.Ingredients[0]
                                .ItemId),
                        1);

                    break;

                case RecipeTier.Standard:
                    recipe.MoneyCost =
                        6 +
                        safeSeed %
                        7;

                    recipe.SuccessChance =
                        82f +
                        safeSeed %
                        9;

                    AddIngredient(
                        recipe,
                        PickResource(
                            CommonIds,
                            safeSeed),
                        2 +
                        safeSeed %
                        2);

                    AddIngredient(
                        recipe,
                        PickResource(
                            NormalIds,
                            safeSeed /
                            3 +
                            1),
                        1);

                    if (safeSeed %
                            3 ==
                        0)
                    {
                        AddIngredient(
                            recipe,
                            PickDifferentResource(
                                CommonIds,
                                safeSeed +
                                2,
                                recipe.Ingredients[0]
                                    .ItemId),
                            1);
                    }

                    break;

                case RecipeTier.Advanced:
                    recipe.MoneyCost =
                        14 +
                        safeSeed %
                        12;

                    recipe.SuccessChance =
                        68f +
                        safeSeed %
                        13;

                    AddIngredient(
                        recipe,
                        PickResource(
                            CommonIds,
                            safeSeed),
                        3 +
                        safeSeed %
                        2);

                    AddIngredient(
                        recipe,
                        PickResource(
                            NormalIds,
                            safeSeed /
                            5 +
                            2),
                        1 +
                        safeSeed %
                        2);

                    AddIngredient(
                        recipe,
                        PickResource(
                            RareIds,
                            safeSeed /
                            7 +
                            3),
                        1);

                    break;

                case RecipeTier.Special:
                    recipe.MoneyCost =
                        28 +
                        safeSeed %
                        18;

                    recipe.SuccessChance =
                        52f +
                        safeSeed %
                        14;

                    AddIngredient(
                        recipe,
                        PickResource(
                            NormalIds,
                            safeSeed),
                        2 +
                        safeSeed %
                        2);

                    AddIngredient(
                        recipe,
                        PickResource(
                            RareIds,
                            safeSeed /
                            3 +
                            4),
                        1 +
                        safeSeed %
                        2);

                    AddIngredient(
                        recipe,
                        WeirdShroomItemId,
                        1);

                    break;

                case RecipeTier.Masterwork:
                    recipe.MoneyCost =
                        55 +
                        safeSeed %
                        36;

                    recipe.SuccessChance =
                        34f +
                        safeSeed %
                        15;

                    AddIngredient(
                        recipe,
                        PickResource(
                            RareIds,
                            safeSeed),
                        2 +
                        safeSeed %
                        2);

                    AddIngredient(
                        recipe,
                        WeirdShroomItemId,
                        1 +
                        safeSeed %
                        2);

                    AddIngredient(
                        recipe,
                        StrangeGemItemId,
                        1);

                    if (safeSeed %
                            2 ==
                        0)
                    {
                        AddIngredient(
                            recipe,
                            PickResource(
                                NormalIds,
                                safeSeed /
                                11 +
                                1),
                            2);
                    }

                    break;
            }

            if (string.Equals(
                    recipe.Category,
                    "음식",
                    StringComparison.Ordinal))
            {
                recipe.MoneyCost =
                    Mathf.Max(
                        1,
                        Mathf.RoundToInt(
                            recipe.MoneyCost *
                            0.70f));

                recipe.SuccessChance =
                    Mathf.Min(
                        100f,
                        recipe.SuccessChance +
                        4f);
            }
            else if (string.Equals(
                         recipe.Category,
                         "의료",
                         StringComparison.Ordinal))
            {
                recipe.MoneyCost =
                    Mathf.Max(
                        3,
                        Mathf.RoundToInt(
                            recipe.MoneyCost *
                            1.10f));
            }
            else if (string.Equals(
                         recipe.Category,
                         "등산 장비",
                         StringComparison.Ordinal))
            {
                recipe.MoneyCost =
                    Mathf.Max(
                        5,
                        Mathf.RoundToInt(
                            recipe.MoneyCost *
                            1.20f));
            }
            else if (string.Equals(
                         recipe.Category,
                         "마법·특수",
                         StringComparison.Ordinal))
            {
                recipe.SuccessChance =
                    Mathf.Max(
                        25f,
                        recipe.SuccessChance -
                        5f);
            }
        }

        private static string DetermineCategory(
            string normalizedName,
            string displayName)
        {
            string combined =
                (
                    normalizedName +
                    " " +
                    displayName
                ).ToLowerInvariant();

            if (ContainsAny(
                    combined,
                    "ration",
                    "meal",
                    "food",
                    "berry",
                    "banana",
                    "shroom",
                    "mushroom",
                    "coconut",
                    "honey",
                    "marshmallow",
                    "granola",
                    "hotdog",
                    "milk",
                    "candy",
                    "bean",
                    "egg",
                    "bird",
                    "drink",
                    "trailmix",
                    "trail mix"))
            {
                return "음식";
            }

            if (ContainsAny(
                    combined,
                    "bandage",
                    "medkit",
                    "medical",
                    "antidote",
                    "panacea",
                    "aloe",
                    "heal",
                    "cure",
                    "sunscreen",
                    "heatpack",
                    "hot pack"))
            {
                return "의료";
            }

            if (ContainsAny(
                    combined,
                    "rope",
                    "piton",
                    "chain",
                    "grappl",
                    "backpack",
                    "balloon",
                    "parasol",
                    "stove",
                    "hook",
                    "launcher",
                    "cannon"))
            {
                return "등산 장비";
            }

            if (ContainsAny(
                    combined,
                    "compass",
                    "map",
                    "guide",
                    "binocular",
                    "lantern",
                    "flare",
                    "flag",
                    "whistle",
                    "bugle"))
            {
                return "탐험 도구";
            }

            if (ContainsAny(
                    combined,
                    "dynamite",
                    "cursed",
                    "skull",
                    "pandora",
                    "magic",
                    "fairy",
                    "rainbow",
                    "golden",
                    "reverse",
                    "scoutmaster",
                    "friendship"))
            {
                return "마법·특수";
            }

            return "기타";
        }

        private static RecipeTier DetermineTier(
            string normalizedName,
            string category,
            Item prefab)
        {
            string combined =
                (
                    normalizedName +
                    " " +
                    category
                ).ToLowerInvariant();

            if (ContainsAny(
                    combined,
                    "golden",
                    "scoutmaster",
                    "pandora",
                    "cursed",
                    "friendship",
                    "reverse rope",
                    "reverse_rope"))
            {
                return
                    RecipeTier.Masterwork;
            }

            if (ContainsAny(
                    combined,
                    "dynamite",
                    "magic",
                    "fairy",
                    "rainbow",
                    "cannon",
                    "launcher"))
            {
                return
                    RecipeTier.Special;
            }

            if (string.Equals(
                    category,
                    "등산 장비",
                    StringComparison.Ordinal) ||
                string.Equals(
                    category,
                    "탐험 도구",
                    StringComparison.Ordinal))
            {
                return
                    RecipeTier.Advanced;
            }

            if (string.Equals(
                    category,
                    "의료",
                    StringComparison.Ordinal))
            {
                return
                    RecipeTier.Standard;
            }

            if (string.Equals(
                    category,
                    "음식",
                    StringComparison.Ordinal))
            {
                return
                    prefab != null &&
                    prefab.CarryWeight >=
                        3
                        ? RecipeTier.Standard
                        : RecipeTier.Basic;
            }

            return
                prefab != null &&
                prefab.CarryWeight >=
                    4
                    ? RecipeTier.Advanced
                    : RecipeTier.Standard;
        }

        private static int CompareRecipes(
            CraftRecipe left,
            CraftRecipe right)
        {
            int result =
                left.Tier.CompareTo(
                    right.Tier);

            if (result !=
                0)
            {
                return result;
            }

            result =
                string.Compare(
                    left.Category,
                    right.Category,
                    StringComparison.Ordinal);

            return
                result !=
                    0
                    ? result
                    : string.Compare(
                        left.DisplayName,
                        right.DisplayName,
                        StringComparison.Ordinal);
        }

        private static void AddIngredient(
            CraftRecipe recipe,
            ushort itemId,
            int count)
        {
            for (int i = 0;
                 i <
                     recipe.Ingredients.Count;
                 i++)
            {
                if (recipe.Ingredients[i]
                        .ItemId ==
                    itemId)
                {
                    recipe.Ingredients[i]
                        .Count +=
                        count;

                    return;
                }
            }

            recipe.Ingredients.Add(
                new IngredientCost(
                    itemId,
                    count));
        }

        private static ushort PickResource(
            ushort[] source,
            int seed)
        {
            return
                source[
                    PositiveModulo(
                        seed,
                        source.Length)];
        }

        private static ushort PickDifferentResource(
            ushort[] source,
            int seed,
            ushort excluded)
        {
            for (int i = 0;
                 i < source.Length;
                 i++)
            {
                ushort value =
                    source[
                        PositiveModulo(
                            seed +
                            i,
                            source.Length)];

                if (value !=
                    excluded)
                {
                    return value;
                }
            }

            return
                source[0];
        }

        private static int PositiveModulo(
            int value,
            int modulus)
        {
            int result =
                value %
                modulus;

            return
                result <
                    0
                    ? result +
                      modulus
                    : result;
        }

        private static int GetDeterministicSeed(
            ushort itemId,
            string name)
        {
            unchecked
            {
                int hash =
                    itemId *
                    397;

                if (name != null)
                {
                    for (int i = 0;
                         i < name.Length;
                         i++)
                    {
                        hash =
                            hash *
                            31 +
                            name[i];
                    }
                }

                return hash;
            }
        }

        private static bool ContainsAny(
            string value,
            params string[] terms)
        {
            for (int i = 0;
                 i < terms.Length;
                 i++)
            {
                if (value.IndexOf(
                        terms[i],
                        StringComparison.OrdinalIgnoreCase) >=
                    0)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsSaleResourceId(
            ushort itemId)
        {
            for (int i = 0;
                 i < SaleResourceIds.Length;
                 i++)
            {
                if (SaleResourceIds[i] ==
                    itemId)
                {
                    return true;
                }
            }

            return false;
        }

        internal int CraftPage
        {
            get
            {
                return craftPage;
            }
        }

        internal int CraftTotalPages
        {
            get
            {
                return
                    craftRecipes.Count ==
                        0
                        ? 1
                        : Mathf.CeilToInt(
                            (float)craftRecipes.Count /
                            CraftRecipesPerPage);
            }
        }

        internal CraftRecipe GetCraftRecipeAtCard(
            int cardIndex)
        {
            int index =
                craftPage *
                CraftRecipesPerPage +
                cardIndex;

            return
                index >=
                    0 &&
                index <
                    craftRecipes.Count
                    ? craftRecipes[index]
                    : null;
        }

        internal CraftRecipe SelectedCraftRecipe
        {
            get
            {
                return
                    selectedCraftRecipeIndex >=
                        0 &&
                    selectedCraftRecipeIndex <
                        craftRecipes.Count
                        ? craftRecipes[
                            selectedCraftRecipeIndex]
                        : null;
            }
        }

        internal void SelectCraftCard(
            int cardIndex)
        {
            int index =
                craftPage *
                CraftRecipesPerPage +
                cardIndex;

            if (index <
                    0 ||
                index >=
                    craftRecipes.Count)
            {
                return;
            }

            selectedCraftRecipeIndex =
                index;

            SetTabStatus(
                HubTab.Craft,
                craftRecipes[index]
                    .DisplayName +
                " 제작식을 선택했습니다.");
        }

        internal void PreviousCraftPage()
        {
            if (craftPage <=
                0)
            {
                return;
            }

            craftPage--;

            selectedCraftRecipeIndex =
                Mathf.Clamp(
                    craftPage *
                    CraftRecipesPerPage,
                    0,
                    craftRecipes.Count -
                    1);

            RefreshWindow();
        }

        internal void NextCraftPage()
        {
            if (craftPage >=
                CraftTotalPages -
                1)
            {
                return;
            }

            craftPage++;

            selectedCraftRecipeIndex =
                Mathf.Clamp(
                    craftPage *
                    CraftRecipesPerPage,
                    0,
                    craftRecipes.Count -
                    1);

            RefreshWindow();
        }

        internal string BuildCraftRequirementText(
            CraftRecipe recipe,
            out bool ready)
        {
            ready =
                false;

            if (recipe == null)
            {
                return
                    "제작식을 선택하세요.";
            }

            if (recipe.OutputItemId ==
                FlareItemId)
            {
                int segment =
                    GetCurrentSegmentIndex();

                int grade =
                    GetCurrentResourceLevel();

                if (segment !=
                    (int)Segment.Peak)
                {
                    return
                        "<color=#FF8A80>최종 조명탄은 정상 구간에서만 제작할 수 있습니다.</color>";
                }

                if (grade <
                    ResourceUpgradeMaximum)
                {
                    return
                        "<color=#FF8A80>Legendary 제작 등급이 필요합니다.</color>";
                }

                if (peakUnlocked)
                {
                    return
                        "<color=#79E081>최종 조명탄 제작과 탈출 신호 발사가 이미 완료되었습니다.</color>";
                }
            }

            Dictionary<ushort, int> counts =
                GetCachedPartyResourceCounts();

            StringBuilder builder =
                sharedTextBuilder;

            builder.Length =
                0;

            bool allReady =
                true;

            for (int i = 0;
                 i < recipe.Ingredients.Count;
                 i++)
            {
                IngredientCost cost =
                    recipe.Ingredients[i];

                int available;

                counts.TryGetValue(
                    cost.ItemId,
                    out available);

                bool enough =
                    available >=
                    cost.Count;

                allReady &=
                    enough;

                if (i >
                    0)
                {
                    builder.Append(
                        '\n');
                }

                builder.Append(
                    enough
                        ? "<color=#79E081>"
                        : "<color=#FF8A80>");

                builder.Append(
                    GetIngredientDisplayName(
                        cost.ItemId));

                builder.Append(
                    ' ');

                builder.Append(
                    available);

                builder.Append(
                    '/');

                builder.Append(
                    cost.Count);

                builder.Append(
                    "</color>");
            }

            int money =
                cachedSharedMoney;

            bool enoughMoney =
                money >=
                    recipe.MoneyCost;

            allReady &=
                enoughMoney;

            if (builder.Length >
                0)
            {
                builder.Append(
                    '\n');
            }

            builder.Append(
                enoughMoney
                    ? "<color=#79E081>"
                    : "<color=#FF8A80>");

            builder.Append(
                "공유 돈 ");

            builder.Append(
                money);

            builder.Append(
                '/');

            builder.Append(
                recipe.MoneyCost);

            builder.Append(
                "원</color>");

            ready =
                allReady;

            return
                builder.ToString();
        }

        private Dictionary<ushort, int>
            GetCachedPartyResourceCounts()
        {
            float now =
                Time.unscaledTime;

            if (now <
                partyResourceCacheUntil)
            {
                return
                    cachedPartyResourceCounts;
            }

            cachedPartyResourceCounts.Clear();

            FillPartyResourceCounts(
                cachedPartyResourceCounts);

            partyResourceCacheUntil =
                now +
                PartyResourceCacheSeconds;

            return
                cachedPartyResourceCounts;
        }

        internal void RequestCraft()
        {
            if (pendingRequest !=
                PendingRequest.None)
            {
                SetTabStatus(
                    HubTab.Craft,
                    "다른 요청을 처리 중입니다.");

                return;
            }

            CraftRecipe recipe =
                SelectedCraftRecipe;

            if (recipe == null)
            {
                SetTabStatus(
                    HubTab.Craft,
                    "제작할 아이템을 선택하세요.");

                return;
            }

            bool ready;

            BuildCraftRequirementText(
                recipe,
                out ready);

            if (!ready)
            {
                SetTabStatus(
                    HubTab.Craft,
                    "공유 돈 또는 제작 재료가 부족합니다.");

                return;
            }

            global::Player player =
                global::Player.localPlayer;

            if (player == null ||
                !player.HasEmptySlot(
                    recipe.OutputItemId))
            {
                SetTabStatus(
                    HubTab.Craft,
                    "완성품을 받을 인벤토리 공간이 없습니다.");

                return;
            }

            pendingRequest =
                PendingRequest.Craft;

            requestStartedAt =
                Time.unscaledTime;

            SetTabStatus(
                HubTab.Craft,
                recipe.DisplayName +
                " 제작을 요청했습니다...");

            object[] payload =
            {
                (int)recipe.OutputItemId
            };

            int actor =
                LocalActorNumber();

            if (PhotonNetwork.IsMasterClient)
            {
                ProcessCraftRequestOnHost(
                    actor,
                    payload);

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
                    CraftRequestEventCode,
                    payload,
                    options,
                    SendOptions.SendReliable);

            if (!sent)
            {
                pendingRequest =
                    PendingRequest.None;

                SetTabStatus(
                    HubTab.Craft,
                    "제작 요청 전송에 실패했습니다.");
            }
        }

        // -----------------------------------------------------------------
        // Upgrade UI bridge
        // -----------------------------------------------------------------

        internal UpgradeKind SelectedUpgradeKind
        {
            get
            {
                return
                    selectedUpgradeKind;
            }
        }

        internal void SelectUpgradeKind(
            UpgradeKind kind)
        {
            selectedUpgradeKind =
                kind;

            SetTabStatus(
                HubTab.Upgrade,
                GetUpgradeDisplayName(
                    kind) +
                " 항목을 선택했습니다.");
        }

        internal int SelectedUpgradeCurrentLevel
        {
            get
            {
                return
                    GetUpgradeCurrentLevel(
                        selectedUpgradeKind);
            }
        }

        internal int SelectedUpgradeMaximumLevel
        {
            get
            {
                return
                    GetUpgradeMaximumLevel(
                        selectedUpgradeKind);
            }
        }

        internal int SelectedUpgradeCost
        {
            get
            {
                return
                    GetNextUpgradeCost(
                        selectedUpgradeKind);
            }
        }

        internal float SelectedUpgradeChance
        {
            get
            {
                return
                    GetNextUpgradeChance(
                        selectedUpgradeKind);
            }
        }

        internal string SelectedUpgradeCurrentEffect
        {
            get
            {
                return
                    GetUpgradeCurrentEffect(
                        selectedUpgradeKind);
            }
        }

        internal string SelectedUpgradeNextEffect
        {
            get
            {
                return
                    GetUpgradeNextEffect(
                        selectedUpgradeKind);
            }
        }

        internal bool UpgradeFailureActive
        {
            get
            {
                return
                    failureEnabledConfig ==
                        null ||
                    failureEnabledConfig
                        .Value;
            }
        }

        internal bool UpgradeFailureConsumesCost
        {
            get
            {
                return
                    consumeCostOnFailureConfig ==
                        null ||
                    consumeCostOnFailureConfig
                        .Value;
            }
        }

        internal bool CanAttemptUpgrade
        {
            get
            {
                return
                    upgradeStateLoaded &&
                    pendingRequest ==
                        PendingRequest.None &&
                    GetUpgradeCurrentLevel(
                        selectedUpgradeKind) <
                        GetUpgradeMaximumLevel(
                            selectedUpgradeKind) &&
                    ReadSharedMoney() >=
                        GetNextUpgradeCost(
                            selectedUpgradeKind);
            }
        }

        internal void RequestUpgrade()
        {
            if (!upgradeStateLoaded)
            {
                SetTabStatus(
                    HubTab.Upgrade,
                    "강화 상태를 아직 불러오지 못했습니다.");

                return;
            }

            if (pendingRequest !=
                PendingRequest.None)
            {
                SetTabStatus(
                    HubTab.Upgrade,
                    "다른 요청을 처리 중입니다.");

                return;
            }

            int currentLevel =
                GetUpgradeCurrentLevel(
                    selectedUpgradeKind);

            if (currentLevel >=
                GetUpgradeMaximumLevel(
                    selectedUpgradeKind))
            {
                SetTabStatus(
                    HubTab.Upgrade,
                    "이미 최대 단계입니다.");

                return;
            }

            int cost =
                GetNextUpgradeCost(
                    selectedUpgradeKind);

            if (ReadSharedMoney() <
                cost)
            {
                SetTabStatus(
                    HubTab.Upgrade,
                    "공유 돈이 부족합니다.");

                return;
            }

            pendingRequest =
                PendingRequest.Upgrade;

            requestStartedAt =
                Time.unscaledTime;

            SetTabStatus(
                HubTab.Upgrade,
                GetUpgradeDisplayName(
                    selectedUpgradeKind) +
                " 강화를 요청했습니다...");

            object[] payload =
            {
                (int)selectedUpgradeKind
            };

            int actor =
                LocalActorNumber();

            if (PhotonNetwork.IsMasterClient)
            {
                ProcessUpgradeRequestOnHost(
                    actor,
                    payload);

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
                    UpgradeRequestEventCode,
                    payload,
                    options,
                    SendOptions.SendReliable);

            if (!sent)
            {
                pendingRequest =
                    PendingRequest.None;

                SetTabStatus(
                    HubTab.Upgrade,
                    "강화 요청 전송에 실패했습니다.");
            }
        }

        // -----------------------------------------------------------------
        // Sell UI bridge
        // -----------------------------------------------------------------

        internal int SelectedSellSlotId
        {
            get
            {
                return
                    selectedSellSlotId;
            }
        }

        internal int VisibleInventorySlotCount
        {
            get
            {
                global::Player player =
                    global::Player
                        .localPlayer;

                return
                    player != null &&
                    player.itemSlots !=
                        null
                        ? Mathf.Min(
                            MaximumVisibleInventorySlots,
                            player.itemSlots.Length)
                        : 0;
            }
        }

        internal void SelectSellSlot(
            int slotId)
        {
            global::Player player =
                global::Player
                    .localPlayer;

            if (player == null ||
                player.itemSlots == null ||
                slotId <
                    0 ||
                slotId >=
                    player.itemSlots.Length)
            {
                selectedSellSlotId =
                    -1;

                SetTabStatus(
                    HubTab.Sell,
                    "선택한 인벤토리 슬롯을 찾지 못했습니다.");

                return;
            }

            selectedSellSlotId =
                slotId;

            ItemSlot slot =
                player.GetItemSlot(
                    (byte)slotId);

            if (slot == null ||
                slot.IsEmpty() ||
                slot.prefab == null)
            {
                SetTabStatus(
                    HubTab.Sell,
                    (slotId + 1) +
                    "번 슬롯은 비어 있습니다.");
            }
            else if (!Spawn.IsSaleResourceId(
                         slot.prefab.itemID))
            {
                SetTabStatus(
                    HubTab.Sell,
                    (slotId + 1) +
                    "번 슬롯의 아이템은 판매할 수 없습니다.");
            }
            else
            {
                SetTabStatus(
                    HubTab.Sell,
                    (slotId + 1) +
                    "번 슬롯을 판매 대상으로 선택했습니다.");
            }
        }

        internal string BuildSelectedSellText(
            out bool canSell)
        {
            canSell =
                false;

            global::Player player =
                global::Player
                    .localPlayer;

            if (player == null ||
                player.itemSlots == null ||
                selectedSellSlotId <
                    0 ||
                selectedSellSlotId >=
                    player.itemSlots.Length)
            {
                return
                    "판매할 인벤토리 슬롯을 선택하세요.";
            }

            ItemSlot slot =
                player.GetItemSlot(
                    (byte)selectedSellSlotId);

            if (slot == null ||
                slot.IsEmpty() ||
                slot.prefab == null)
            {
                return
                    "선택한 슬롯이 비어 있습니다.";
            }

            ushort itemId =
                slot.prefab.itemID;

            if (!Spawn.IsSaleResourceId(
                    itemId))
            {
                return
                    "선택 아이템: " +
                    GetItemDisplayName(
                        slot.prefab) +
                    "\n이 아이템은 판매할 수 없습니다.";
            }

            int price =
                GetSellPrice(
                    itemId);

            int count =
                Mathf.Max(
                    1,
                    InventoryStack
                        .GetStackCount(
                            player,
                            (byte)selectedSellSlotId));

            canSell =
                price >
                    0 &&
                pendingRequest ==
                    PendingRequest.None;

            return
                "선택 아이템: " +
                GetItemDisplayName(
                    slot.prefab) +
                "\n등급: " +
                GetRarityName(
                    itemId) +
                "   |   보유: " +
                count +
                "개\n판매가: " +
                price +
                "원";
        }

        internal void RequestSell()
        {
            if (pendingRequest !=
                PendingRequest.None)
            {
                SetTabStatus(
                    HubTab.Sell,
                    "다른 요청을 처리 중입니다.");

                return;
            }

            global::Player player =
                global::Player
                    .localPlayer;

            if (player == null ||
                player.itemSlots == null ||
                selectedSellSlotId <
                    0 ||
                selectedSellSlotId >=
                    player.itemSlots.Length)
            {
                SetTabStatus(
                    HubTab.Sell,
                    "판매할 인벤토리 슬롯을 선택하세요.");

                return;
            }

            byte slotId =
                (byte)selectedSellSlotId;

            ItemSlot slot =
                player.GetItemSlot(
                    slotId);

            if (slot == null ||
                slot.IsEmpty() ||
                slot.prefab == null ||
                !Spawn.IsSaleResourceId(
                    slot.prefab.itemID))
            {
                SetTabStatus(
                    HubTab.Sell,
                    "선택한 슬롯에는 판매 가능한 자원이 없습니다.");

                return;
            }

            ushort itemId =
                slot.prefab.itemID;

            string guid =
                slot.data != null
                    ? slot.data.guid
                        .ToString()
                    : string.Empty;

            pendingRequest =
                PendingRequest.Sell;

            requestStartedAt =
                Time.unscaledTime;

            SetTabStatus(
                HubTab.Sell,
                "판매 요청을 처리 중입니다...");

            object[] payload =
            {
                selectedSellSlotId,
                (int)itemId,
                guid
            };

            int actor =
                LocalActorNumber();

            if (PhotonNetwork.IsMasterClient)
            {
                ProcessSellRequestOnHost(
                    actor,
                    payload);

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
                    payload,
                    options,
                    SendOptions.SendReliable);

            if (!sent)
            {
                pendingRequest =
                    PendingRequest.None;

                SetTabStatus(
                    HubTab.Sell,
                    "판매 요청 전송에 실패했습니다.");
            }
        }

        // -----------------------------------------------------------------
        // Counts and display helpers
        // -----------------------------------------------------------------

        private static void FillPartyResourceCounts(
            Dictionary<ushort, int> counts)
        {
            if (counts == null)
            {
                return;
            }

            List<Character> characters =
                PlayerHandler
                    .GetAllPlayerCharacters();

            for (int characterIndex = 0;
                 characterIndex < characters.Count;
                 characterIndex++)
            {
                Character character =
                    characters[characterIndex];

                if (character == null ||
                    character.player == null ||
                    character.photonView == null ||
                    character.photonView.Owner == null ||
                    character.photonView.Owner
                        .IsInactive)
                {
                    continue;
                }

                global::Player player =
                    character.player;

                if (player.itemSlots != null)
                {
                    for (int slotIndex = 0;
                         slotIndex < player.itemSlots.Length;
                         slotIndex++)
                    {
                        AddSlotCount(
                            counts,
                            player,
                            player.itemSlots[
                                slotIndex],
                            (byte)slotIndex,
                            true);
                    }
                }

                AddSlotCount(
                    counts,
                    player,
                    player.tempFullSlot,
                    player.tempFullSlot != null
                        ? player.tempFullSlot
                            .itemSlotID
                        : (byte)250,
                    true);

                BackpackData backpackData =
                    default(BackpackData);

                bool hasBackpack =
                    player.backpackSlot != null &&
                    !player.backpackSlot.IsEmpty() &&
                    player.backpackSlot.data != null &&
                    player.backpackSlot.data
                        .TryGetDataEntry<
                            BackpackData>(
                            DataEntryKey.BackpackData,
                            out backpackData);

                if (!hasBackpack ||
                    backpackData == null ||
                    backpackData.itemSlots == null)
                {
                    continue;
                }

                for (int i = 0;
                     i < backpackData.itemSlots.Length;
                     i++)
                {
                    AddSlotCount(
                        counts,
                        player,
                        backpackData.itemSlots[i],
                        0,
                        false);
                }
            }
        }

        private static void AddSlotCount(
            Dictionary<ushort, int> counts,
            global::Player player,
            ItemSlot slot,
            byte slotId,
            bool stackAware)
        {
            if (slot == null ||
                slot.IsEmpty() ||
                slot.prefab == null ||
                !IsSaleResourceId(
                    slot.prefab.itemID))
            {
                return;
            }

            int amount =
                stackAware
                    ? Mathf.Max(
                        1,
                        InventoryStack
                            .GetStackCount(
                                player,
                                slotId))
                    : 1;

            int current;

            counts.TryGetValue(
                slot.prefab.itemID,
                out current);

            counts[
                slot.prefab.itemID] =
                    current +
                    amount;
        }

        private static int CountLocalNormalSlotUnits(
            int slotId,
            ushort itemId)
        {
            global::Player player =
                global::Player
                    .localPlayer;

            if (player == null ||
                player.itemSlots == null ||
                slotId <
                    0 ||
                slotId >=
                    player.itemSlots.Length)
            {
                return 0;
            }

            ItemSlot slot =
                player.GetItemSlot(
                    (byte)slotId);

            if (slot == null ||
                slot.IsEmpty() ||
                slot.prefab == null ||
                slot.prefab.itemID !=
                    itemId)
            {
                return 0;
            }

            return
                Mathf.Max(
                    1,
                    InventoryStack.GetStackCount(
                        player,
                        (byte)slotId));
        }

        private static int CountLocalItemUnits(
            ushort itemId)
        {
            global::Player player =
                global::Player
                    .localPlayer;

            if (player == null)
            {
                return 0;
            }

            int count = 0;

            if (player.itemSlots !=
                null)
            {
                for (int i = 0;
                     i <
                         player.itemSlots.Length;
                     i++)
                {
                    ItemSlot slot =
                        player.itemSlots[i];

                    if (slot == null ||
                        slot.IsEmpty() ||
                        slot.prefab == null ||
                        slot.prefab.itemID !=
                            itemId)
                    {
                        continue;
                    }

                    count +=
                        Mathf.Max(
                            1,
                            InventoryStack
                                .GetStackCount(
                                    player,
                                    (byte)i));
                }
            }

            ItemSlot temp =
                player.tempFullSlot;

            if (temp != null &&
                !temp.IsEmpty() &&
                temp.prefab != null &&
                temp.prefab.itemID ==
                    itemId)
            {
                count +=
                    Mathf.Max(
                        1,
                        InventoryStack
                            .GetStackCount(
                                player,
                                temp.itemSlotID));
            }

            BackpackData backpackData =
                default(BackpackData);

            bool hasBackpack =
                player.backpackSlot != null &&
                !player.backpackSlot.IsEmpty() &&
                player.backpackSlot.data != null &&
                player.backpackSlot.data
                    .TryGetDataEntry<
                        BackpackData>(
                        DataEntryKey.BackpackData,
                        out backpackData);

            if (hasBackpack &&
                backpackData != null &&
                backpackData.itemSlots !=
                    null)
            {
                for (int i = 0;
                     i <
                         backpackData.itemSlots.Length;
                     i++)
                {
                    ItemSlot slot =
                        backpackData.itemSlots[i];

                    if (slot != null &&
                        !slot.IsEmpty() &&
                        slot.prefab != null &&
                        slot.prefab.itemID ==
                            itemId)
                    {
                        count++;
                    }
                }
            }

            return count;
        }

        private static string GetItemDisplayName(
            Item item)
        {
            if (item == null)
            {
                return
                    "<이름 없음>";
            }

            string localized =
                item.GetName();

            if (!string.IsNullOrEmpty(
                    localized))
            {
                return localized;
            }

            if (item.UIData != null &&
                !string.IsNullOrEmpty(
                    item.UIData.itemName))
            {
                return
                    item.UIData.itemName;
            }

            return
                item.gameObject != null
                    ? item.gameObject.name
                    : "<이름 없음>";
        }

        private static string GetIngredientDisplayName(
            ushort itemId)
        {
            Item item;

            if (ItemDatabase.TryGetItem(
                    itemId,
                    out item) &&
                item != null)
            {
                return
                    GetItemDisplayName(
                        item);
            }

            switch (itemId)
            {
                case FireWoodItemId:
                    return "나뭇가지";

                case StoneItemId:
                    return "돌";

                case ConchItemId:
                    return "소라고동";

                case BinocularsItemId:
                    return "망원경";

                case BingBongItemId:
                    return "빙봉";

                case BugleItemId:
                    return "나팔";

                case FrisbeeItemId:
                    return "플라잉 디스크";

                case GuidebookItemId:
                    return "가이드북";

                case ScrollItemId:
                    return "스크롤";

                case WeirdShroomItemId:
                    return "괴상 버섯";

                case StrangeGemItemId:
                    return "이상한 보석";

                default:
                    return
                        "Item " +
                        itemId;
            }
        }

        private static string GetTierName(
            RecipeTier tier)
        {
            switch (tier)
            {
                case RecipeTier.Basic:
                    return "기초";

                case RecipeTier.Standard:
                    return "일반";

                case RecipeTier.Advanced:
                    return "고급";

                case RecipeTier.Special:
                    return "특수";

                case RecipeTier.Masterwork:
                    return "최고급";

                default:
                    return "알 수 없음";
            }
        }

        // -----------------------------------------------------------------
        // Integrated upgrade backend
        // -----------------------------------------------------------------

        private void BindUpgradeConfig()
        {
            failureEnabledConfig = Config.Bind(
                "01. 강화 공통 설정",
                "강화 실패 활성화",
                true,
                "비활성화하면 모든 강화가 100% 성공합니다.");

            consumeCostOnFailureConfig = Config.Bind(
                "01. 강화 공통 설정",
                "실패 시 비용 소모",
                true,
                "활성화하면 강화 실패 시에도 공유 돈에서 비용이 차감됩니다.");

            resourceUpgradeFormula = BindFormula("02. 자원 등급 강화", 20, 35, 100f, 15f);
            gatherUpgradeFormula = BindFormula("03. 채집 속도 강화", 15, 20, 100f, 15f);
            stackUpgradeFormula = BindFormula("04. 인벤토리 적재 강화", 12, 18, 100f, 14f);
            campfireUpgradeFormula = BindFormula("05. 모닥불 효율 강화", 20, 30, 90f, 15f);

            doubleYieldCostConfig = Config.Bind(
                "06. 수집량 2배 강화",
                "강화 비용",
                60,
                new ConfigDescription(
                    "맵 자원 수집량 x2 강화 비용입니다.",
                    new AcceptableValueRange<int>(0, 100000)));

            doubleYieldChanceConfig = Config.Bind(
                "06. 수집량 2배 강화",
                "성공 확률",
                55f,
                new ConfigDescription(
                    "맵 자원 수집량 x2 강화 성공 확률입니다.",
                    new AcceptableValueRange<float>(0f, 100f)));
        }

        private UpgradeFormulaConfig BindFormula(
            string section,
            int baseCost,
            int costGrowth,
            float startChance,
            float chanceLoss)
        {
            return new UpgradeFormulaConfig
            {
                BaseCost = Config.Bind(
                    section,
                    "1단계 기본 비용",
                    baseCost,
                    new ConfigDescription(
                        "첫 단계 강화 비용입니다.",
                        new AcceptableValueRange<int>(0, 100000))),

                CostGrowth = Config.Bind(
                    section,
                    "단계별 추가 비용",
                    costGrowth,
                    new ConfigDescription(
                        "다음 단계마다 추가되는 비용입니다.",
                        new AcceptableValueRange<int>(0, 100000))),

                StartChance = Config.Bind(
                    section,
                    "1단계 성공 확률",
                    startChance,
                    new ConfigDescription(
                        "첫 단계 성공 확률입니다.",
                        new AcceptableValueRange<float>(0f, 100f))),

                ChanceLoss = Config.Bind(
                    section,
                    "단계별 성공 확률 감소",
                    chanceLoss,
                    new ConfigDescription(
                        "다음 단계마다 감소하는 성공 확률입니다.",
                        new AcceptableValueRange<float>(0f, 100f)))
            };
        }



        private void LoadUpgradeStateOnDemand()
        {
            upgradeRoomStateDirty =
                false;

            if (!PhotonNetwork.InRoom ||
                PhotonNetwork.CurrentRoom == null ||
                !gameplayScene)
            {
                return;
            }

            bool found =
                ReadUpgradeStateFromRoom(
                    false);

            string runId =
                ReadRunId();

            if (!PhotonNetwork.IsMasterClient)
            {
                if (!found)
                {
                    upgradeState =
                        UpgradeState
                            .CreateDefault();

                    upgradeState.RunId =
                        runId;

                    upgradeState.BaseHoldSeconds =
                        Mathf.Clamp(
                            LongE.PickupHoldSeconds,
                            0.1f,
                            60f);

                    upgradeState.BaseStackCount =
                        Mathf.Clamp(
                            InventoryStack.MaximumStackCount,
                            1,
                            100);

                    upgradeState.BaseCampfireMaterials =
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

                    upgradeStateLoaded =
                        true;

                    ApplyUpgradeEffects(
                        "Client default state until host initializes room");
                }

                return;
            }

            bool runChanged =
                upgradeStateLoaded &&
                !string.IsNullOrEmpty(
                    runId) &&
                !string.Equals(
                    upgradeState.RunId,
                    runId,
                    StringComparison.Ordinal);

            if (!found ||
                !upgradeStateLoaded ||
                runChanged)
            {
                InitializeFreshState(
                    string.IsNullOrEmpty(
                        runId)
                        ? Guid.NewGuid()
                            .ToString("N")
                        : runId);

                pendingFreshUpgradeRun =
                    false;

                return;
            }

            if (pendingFreshUpgradeRun &&
                !string.IsNullOrEmpty(
                    runId) &&
                !string.Equals(
                    upgradeState.RunId,
                    runId,
                    StringComparison.Ordinal))
            {
                InitializeFreshState(
                    runId);
            }

            pendingFreshUpgradeRun =
                false;
        }

        private void InitializeFreshState(string runId)
        {
            if (!PhotonNetwork.IsMasterClient)
                return;

            UpgradeState fresh = UpgradeState.CreateDefault();
            fresh.RunId = runId ?? string.Empty;
            fresh.OwnerActor = LocalActorNumber();
            fresh.BaseHoldSeconds = Mathf.Clamp(LongE.PickupHoldSeconds, 0.1f, 60f);
            fresh.BaseStackCount = Mathf.Clamp(InventoryStack.MaximumStackCount, 1, 100);
            fresh.BaseCampfireMaterials = new[]
            {
                Mathf.Max(0, CampfireGate.RequiredFireWoodCount),
                Mathf.Max(0, CampfireGate.RequiredStoneCount),
                Mathf.Max(0, CampfireGate.RequiredTorchCount)
            };

            PublishUpgradeState(fresh, "Fresh run");
        }

        private string ReadRunId()
        {
            object value;

            if (!PhotonNetwork.CurrentRoom.CustomProperties.TryGetValue(
                    RunIdKey,
                    out value) ||
                value == null)
                return string.Empty;

            return value as string ?? Convert.ToString(value);
        }

        private bool ReadUpgradeStateFromRoom(bool force)
        {
            if (!PhotonNetwork.InRoom ||
                PhotonNetwork.CurrentRoom ==
                    null)
            {
                return false;
            }

            ExitGames.Client.Photon.Hashtable props =
                PhotonNetwork.CurrentRoom.CustomProperties;

            object protocolValue = null;
            object revisionValue = null;

            if (!props.TryGetValue(UpgradeProtocolKey, out protocolValue) ||
                !props.TryGetValue(UpgradeRevisionKey, out revisionValue))
                return false;

            try
            {
                if (Convert.ToInt32(protocolValue) != UpgradeProtocolVersion)
                    return false;

                UpgradeState incoming = UpgradeState.CreateDefault();
                incoming.Protocol = UpgradeProtocolVersion;
                incoming.Revision = Convert.ToInt32(revisionValue);
                incoming.OwnerActor = ReadInt(props, UpgradeOwnerKey, 0);
                incoming.RunId = ReadString(props, UpgradeRunIdKey);
                incoming.ResourceLevel = Mathf.Clamp(
                    ReadInt(props, UpgradeResourceKey, 0), 0, ResourceUpgradeMaximum);
                incoming.GatherLevel = Mathf.Clamp(
                    ReadInt(props, UpgradeGatherKey, 0), 0, GatherUpgradeMaximum);
                incoming.StackLevel = Mathf.Clamp(
                    ReadInt(props, UpgradeStackKey, 0), 0, StackUpgradeMaximum);
                incoming.CampfireLevel = Mathf.Clamp(
                    ReadInt(props, UpgradeCampfireKey, 0), 0, CampfireUpgradeMaximum);
                incoming.YieldMultiplier = Mathf.Clamp(
                    ReadInt(props, UpgradeYieldKey, 1), 1, 2);
                incoming.BaseHoldSeconds = Mathf.Clamp(
                    ReadFloat(props, UpgradeBaseHoldKey, 10f), 0.1f, 60f);
                incoming.BaseStackCount = Mathf.Clamp(
                    ReadInt(props, UpgradeBaseStackKey, 10), 1, 100);
                incoming.BaseCampfireMaterials = ReadIntArray(
                    props,
                    UpgradeBaseCampfireKey,
                    new[] { 1, 1, 1 });

                incoming = NormalizeUpgradeState(incoming);

                if (!force &&
                    upgradeStateLoaded &&
                    incoming.Revision < upgradeState.Revision)
                    return true;

                bool changed =
                    !upgradeStateLoaded ||
                    force ||
                    incoming.Revision != upgradeState.Revision ||
                    !string.Equals(
                        incoming.RunId,
                        upgradeState.RunId,
                        StringComparison.Ordinal);

                upgradeState = incoming;
                upgradeStateLoaded = true;

                if (changed)
                {
                    ApplyUpgradeEffects("Room upgradeState");
                    RefreshWindow();
                }

                return true;
            }
            catch (Exception exception)
            {
                Logger.LogError("Upgrade upgradeState read failed: " + exception);
                return false;
            }
        }

        private bool PublishUpgradeState(UpgradeState value, string reason)
        {
            if (!PhotonNetwork.InRoom ||
                !PhotonNetwork.IsMasterClient ||
                PhotonNetwork.CurrentRoom == null ||
                value == null)
                return false;

            UpgradeState safe = NormalizeUpgradeState(value.Clone());
            safe.Protocol = UpgradeProtocolVersion;
            safe.Revision = Mathf.Max(upgradeState.Revision, safe.Revision) + 1;
            safe.OwnerActor = LocalActorNumber();

            ExitGames.Client.Photon.Hashtable props =
                new ExitGames.Client.Photon.Hashtable
                {
                    { UpgradeProtocolKey, safe.Protocol },
                    { UpgradeRevisionKey, safe.Revision },
                    { UpgradeOwnerKey, safe.OwnerActor },
                    { UpgradeRunIdKey, safe.RunId ?? string.Empty },
                    { UpgradeResourceKey, safe.ResourceLevel },
                    { UpgradeGatherKey, safe.GatherLevel },
                    { UpgradeStackKey, safe.StackLevel },
                    { UpgradeCampfireKey, safe.CampfireLevel },
                    { UpgradeYieldKey, safe.YieldMultiplier },
                    { UpgradeBaseHoldKey, safe.BaseHoldSeconds },
                    { UpgradeBaseStackKey, safe.BaseStackCount },
                    { UpgradeBaseCampfireKey, CloneIntArray(safe.BaseCampfireMaterials) }
                };

            if (!PhotonNetwork.CurrentRoom.SetCustomProperties(props))
            {
                Logger.LogError("Upgrade upgradeState publish failed: " + reason);
                return false;
            }

            upgradeState = safe;
            upgradeStateLoaded = true;
            ApplyUpgradeEffects(reason);
            RefreshWindow();

            Logger.LogInfo(
                "Upgrade upgradeState published. Reason=" + reason +
                " | Resource=" + safe.ResourceLevel +
                " | Gather=" + safe.GatherLevel +
                " | Stack=" + safe.StackLevel +
                " | Campfire=" + safe.CampfireLevel +
                " | Yield=x" + safe.YieldMultiplier);

            return true;
        }

        private static UpgradeState NormalizeUpgradeState(UpgradeState value)
        {
            UpgradeState safe = value ?? UpgradeState.CreateDefault();

            safe.Protocol = UpgradeProtocolVersion;
            safe.Revision = Mathf.Max(0, safe.Revision);
            safe.OwnerActor = Mathf.Max(0, safe.OwnerActor);
            safe.RunId = safe.RunId ?? string.Empty;
            safe.ResourceLevel = Mathf.Clamp(safe.ResourceLevel, 0, ResourceUpgradeMaximum);
            safe.GatherLevel = Mathf.Clamp(safe.GatherLevel, 0, GatherUpgradeMaximum);
            safe.StackLevel = Mathf.Clamp(safe.StackLevel, 0, StackUpgradeMaximum);
            safe.CampfireLevel = Mathf.Clamp(safe.CampfireLevel, 0, CampfireUpgradeMaximum);
            safe.YieldMultiplier = Mathf.Clamp(safe.YieldMultiplier, 1, 2);
            safe.BaseHoldSeconds = Mathf.Clamp(safe.BaseHoldSeconds, 0.1f, 60f);
            safe.BaseStackCount = Mathf.Clamp(safe.BaseStackCount, 1, 100);
            safe.BaseCampfireMaterials = EnsureThree(
                safe.BaseCampfireMaterials,
                new[] { 1, 1, 1 });

            for (int i = 0; i < safe.BaseCampfireMaterials.Length; i++)
                safe.BaseCampfireMaterials[i] =
                    Mathf.Max(0, safe.BaseCampfireMaterials[i]);

            return safe;
        }

        private void EnsureUpgradeEffectsApplied()
        {
            bool apply =
                lastAppliedUpgradeRevision != upgradeState.Revision ||
                !string.Equals(
                    lastAppliedUpgradeRunId,
                    upgradeState.RunId,
                    StringComparison.Ordinal);

            if (PhotonNetwork.IsMasterClient &&
                (int)Spawn.CurrentUpgradeGrade != upgradeState.ResourceLevel)
                apply = true;

            if (PhotonNetwork.IsMasterClient &&
                InventoryStack.MaximumStackCount != CalculateEffectiveStackMaximum(upgradeState))
                apply = true;

            if (apply)
                ApplyUpgradeEffects("Verification");
        }

        private void ApplyUpgradeEffects(string reason)
        {
            ResourceYieldMultiplier = Mathf.Clamp(upgradeState.YieldMultiplier, 1, 2);

            float holdSeconds = CalculateEffectiveHoldSeconds(upgradeState);
            if (!Mathf.Approximately(LongE.PickupHoldSeconds, holdSeconds))
                LongE.SetPickupHoldSeconds(holdSeconds);

            ApplyCampfireRequirements(CalculateEffectiveCampfireMaterials(upgradeState));

            if (PhotonNetwork.IsMasterClient)
            {
                Spawn.SetUpgradeGrade(upgradeState.ResourceLevel);

                SetConfigValue(
                    InventoryStack.Instance != null
                        ? InventoryStack.Instance.Config
                        : null,
                    InventoryMaximumDefinition,
                    CalculateEffectiveStackMaximum(upgradeState));
            }

            lastAppliedUpgradeRevision = upgradeState.Revision;
            lastAppliedUpgradeRunId = upgradeState.RunId ?? string.Empty;

            Logger.LogDebug(
                "Upgrade effects applied. Reason=" + reason +
                " | Hold=" + holdSeconds.ToString("0.00") +
                " | Stack=" + CalculateEffectiveStackMaximum(upgradeState) +
                " | Yield=x" + ResourceYieldMultiplier);
        }

        private void RestoreBaseUpgradeEffects()
        {
            ResourceYieldMultiplier = 1;

            if (!upgradeStateLoaded)
                return;

            LongE.SetPickupHoldSeconds(upgradeState.BaseHoldSeconds);
            ApplyCampfireRequirements(CloneIntArray(upgradeState.BaseCampfireMaterials));

            if (PhotonNetwork.IsMasterClient)
            {
                Spawn.SetUpgradeGrade(0);

                SetConfigValue(
                    InventoryStack.Instance != null
                        ? InventoryStack.Instance.Config
                        : null,
                    InventoryMaximumDefinition,
                    upgradeState.BaseStackCount);
            }

            lastAppliedUpgradeRevision = -1;
            lastAppliedUpgradeRunId = string.Empty;
        }

        private static float CalculateEffectiveHoldSeconds(UpgradeState value)
        {
            int level = Mathf.Clamp(value.GatherLevel, 0, GatherUpgradeMaximum);
            return Mathf.Clamp(
                value.BaseHoldSeconds * GatherTimeFactors[level],
                0.1f,
                60f);
        }

        private static int CalculateEffectiveStackMaximum(UpgradeState value)
        {
            int level = Mathf.Clamp(value.StackLevel, 0, StackUpgradeMaximum);
            return Mathf.Clamp(
                value.BaseStackCount + StackCapacityBonuses[level],
                1,
                100);
        }

        private static int[] CalculateEffectiveCampfireMaterials(UpgradeState value)
        {
            int level = Mathf.Clamp(value.CampfireLevel, 0, CampfireUpgradeMaximum);
            float factor = CampfireRequirementFactors[level];
            int[] result = new int[3];

            for (int i = 0; i < result.Length; i++)
            {
                int baseCount = value.BaseCampfireMaterials[i];

                result[i] = baseCount <= 0
                    ? 0
                    : Mathf.Max(1, Mathf.CeilToInt(baseCount * factor));
            }

            return result;
        }

        private static void ApplyCampfireRequirements(int[] values)
        {
            int[] safe = EnsureThree(values, new[] { 1, 1, 1 });

            ConfigFile config =
                CampfireGate.Instance != null
                    ? CampfireGate.Instance.Config
                    : null;

            SetConfigValue(config, CampfireWoodDefinition, safe[0]);
            SetConfigValue(config, CampfireStoneDefinition, safe[1]);
            SetConfigValue(config, CampfireTorchDefinition, safe[2]);
        }



        private void ProcessUpgradeRequestOnHost(int actor, object[] payload)
        {
            if (!PhotonNetwork.IsMasterClient)
            {
                return;
            }

            if (!upgradeStateLoaded)
            {
                LoadUpgradeStateOnDemand();
            }

            if (!upgradeStateLoaded)
            {
                SendUpgradeResult(
                    actor,
                    false,
                    "강화 상태를 초기화하지 못했습니다.");

                return;
            }

            if (!IsGameplayScene())
            {
                SendUpgradeResult(actor, false, "현재는 강화할 수 없습니다.");
                return;
            }

            if (payload == null || payload.Length < 1)
            {
                SendUpgradeResult(actor, false, "잘못된 강화 요청입니다.");
                return;
            }

            double now = PhotonNetwork.Time;
            double previous;

            if (lastUpgradeRequestAtByActor.TryGetValue(actor, out previous) &&
                now - previous < MinimumRequestIntervalSeconds)
            {
                SendUpgradeResult(actor, false, "강화 요청이 너무 빠릅니다.");
                return;
            }

            lastUpgradeRequestAtByActor[actor] = now;

            int kindValue;

            try
            {
                kindValue = Convert.ToInt32(payload[0]);
            }
            catch (Exception)
            {
                SendUpgradeResult(actor, false, "강화 종류를 해석하지 못했습니다.");
                return;
            }

            if (kindValue < 0 || kindValue > (int)UpgradeKind.DoubleYield)
            {
                SendUpgradeResult(actor, false, "존재하지 않는 강화입니다.");
                return;
            }

            UpgradeKind kind = (UpgradeKind)kindValue;
            int current = GetUpgradeCurrentLevel(kind);

            if (current >= GetUpgradeMaximumLevel(kind))
            {
                SendUpgradeResult(actor, false, "이미 최대 단계입니다.");
                return;
            }

            int cost = GetNextUpgradeCost(kind);
            float chance = GetNextUpgradeChance(kind);
            int money = ReadSharedMoney();

            if (money < cost)
            {
                SendUpgradeResult(actor, false, "공유 돈이 부족합니다.");
                return;
            }

            bool failureActive =
                failureEnabledConfig == null ||
                failureEnabledConfig.Value;

            bool success =
                !failureActive ||
                UnityEngine.Random.Range(0f, 100f) < chance;

            bool consumeMoney =
                success ||
                consumeCostOnFailureConfig == null ||
                consumeCostOnFailureConfig.Value;

            if (consumeMoney)
                SetSharedMoneyOnHost(money - cost);

            if (!success)
            {
                SendUpgradeResult(
                    actor,
                    false,
                    GetUpgradeDisplayName(kind) +
                    " 강화에 실패했습니다.\n" +
                    (consumeMoney
                        ? cost + "원이 소모되었습니다."
                        : "비용은 소모되지 않았습니다."));

                Logger.LogInfo(
                    "Upgrade failed. Actor=" + actor +
                    " | Kind=" + kind +
                    " | Chance=" + chance +
                    " | Cost=" + cost);

                return;
            }

            UpgradeState upgraded = upgradeState.Clone();
            IncreaseUpgradeLevel(upgraded, kind);

            if (!PublishUpgradeState(upgraded, "Upgrade success: " + kind))
            {
                if (consumeMoney)
                    SetSharedMoneyOnHost(ReadSharedMoney() + cost);

                SendUpgradeResult(
                    actor,
                    false,
                    "강화 상태 저장에 실패했습니다. 비용을 환불했습니다.");
                return;
            }

            SendUpgradeResult(
                actor,
                true,
                GetUpgradeDisplayName(kind) +
                " 강화 성공!\n" +
                GetUpgradeCurrentEffect(kind));
        }

        private static void IncreaseUpgradeLevel(UpgradeState value, UpgradeKind kind)
        {
            switch (kind)
            {
                case UpgradeKind.ResourceGrade:
                    value.ResourceLevel = Mathf.Min(ResourceUpgradeMaximum, value.ResourceLevel + 1);
                    break;

                case UpgradeKind.GatherSpeed:
                    value.GatherLevel = Mathf.Min(GatherUpgradeMaximum, value.GatherLevel + 1);
                    break;

                case UpgradeKind.StackCapacity:
                    value.StackLevel = Mathf.Min(StackUpgradeMaximum, value.StackLevel + 1);
                    break;

                case UpgradeKind.CampfireEfficiency:
                    value.CampfireLevel = Mathf.Min(CampfireUpgradeMaximum, value.CampfireLevel + 1);
                    break;

                case UpgradeKind.DoubleYield:
                    value.YieldMultiplier = 2;
                    break;
            }
        }

        private void SendUpgradeResult(int actor, bool success, string message)
        {
            object[] payload =
            {
                success,
                message ?? string.Empty,
                ReadSharedMoney()
            };

            if (PhotonNetwork.LocalPlayer != null &&
                PhotonNetwork.LocalPlayer.ActorNumber == actor)
            {
                HandleUpgradeResult(payload);
                return;
            }

            RaiseEventOptions options = new RaiseEventOptions
            {
                TargetActors = new[] { actor }
            };

            PhotonNetwork.RaiseEvent(
                UpgradeResultEventCode,
                payload,
                options,
                SendOptions.SendReliable);
        }



        private int GetUpgradeCurrentLevel(UpgradeKind kind)
        {
            switch (kind)
            {
                case UpgradeKind.ResourceGrade:
                    return upgradeState.ResourceLevel;
                case UpgradeKind.GatherSpeed:
                    return upgradeState.GatherLevel;
                case UpgradeKind.StackCapacity:
                    return upgradeState.StackLevel;
                case UpgradeKind.CampfireEfficiency:
                    return upgradeState.CampfireLevel;
                case UpgradeKind.DoubleYield:
                    return upgradeState.YieldMultiplier >= 2 ? 1 : 0;
                default:
                    return 0;
            }
        }

        private static int GetUpgradeMaximumLevel(UpgradeKind kind)
        {
            switch (kind)
            {
                case UpgradeKind.ResourceGrade:
                    return ResourceUpgradeMaximum;
                case UpgradeKind.GatherSpeed:
                    return GatherUpgradeMaximum;
                case UpgradeKind.StackCapacity:
                    return StackUpgradeMaximum;
                case UpgradeKind.CampfireEfficiency:
                    return CampfireUpgradeMaximum;
                case UpgradeKind.DoubleYield:
                    return YieldUpgradeMaximum;
                default:
                    return 0;
            }
        }

        private UpgradeFormulaConfig GetUpgradeFormula(UpgradeKind kind)
        {
            switch (kind)
            {
                case UpgradeKind.ResourceGrade:
                    return resourceUpgradeFormula;
                case UpgradeKind.GatherSpeed:
                    return gatherUpgradeFormula;
                case UpgradeKind.StackCapacity:
                    return stackUpgradeFormula;
                case UpgradeKind.CampfireEfficiency:
                    return campfireUpgradeFormula;
                default:
                    return null;
            }
        }

        private int GetNextUpgradeCost(UpgradeKind kind)
        {
            int nextLevel = GetUpgradeCurrentLevel(kind) + 1;

            if (kind == UpgradeKind.DoubleYield)
                return doubleYieldCostConfig != null
                    ? Mathf.Max(0, doubleYieldCostConfig.Value)
                    : 60;

            UpgradeFormulaConfig formula = GetUpgradeFormula(kind);
            int baseCost = formula != null && formula.BaseCost != null
                ? Mathf.Max(0, formula.BaseCost.Value)
                : 0;
            int growth = formula != null && formula.CostGrowth != null
                ? Mathf.Max(0, formula.CostGrowth.Value)
                : 0;

            return baseCost + growth * Mathf.Max(0, nextLevel - 1);
        }

        private float GetNextUpgradeChance(UpgradeKind kind)
        {
            if (failureEnabledConfig != null && !failureEnabledConfig.Value)
                return 100f;

            int nextLevel = GetUpgradeCurrentLevel(kind) + 1;

            if (kind == UpgradeKind.DoubleYield)
                return doubleYieldChanceConfig != null
                    ? Mathf.Clamp(doubleYieldChanceConfig.Value, 0f, 100f)
                    : 55f;

            UpgradeFormulaConfig formula = GetUpgradeFormula(kind);
            float start = formula != null && formula.StartChance != null
                ? formula.StartChance.Value
                : 100f;
            float loss = formula != null && formula.ChanceLoss != null
                ? formula.ChanceLoss.Value
                : 0f;

            return Mathf.Clamp(
                start - loss * Mathf.Max(0, nextLevel - 1),
                0f,
                100f);
        }

        internal string GetUpgradeDisplayName(UpgradeKind kind)
        {
            switch (kind)
            {
                case UpgradeKind.ResourceGrade:
                    return "자원 등급";
                case UpgradeKind.GatherSpeed:
                    return "채집 속도";
                case UpgradeKind.StackCapacity:
                    return "인벤토리 적재량";
                case UpgradeKind.CampfireEfficiency:
                    return "모닥불 제작 효율";
                case UpgradeKind.DoubleYield:
                    return "수집량 2배";
                default:
                    return "알 수 없는 강화";
            }
        }

        internal string GetUpgradeCurrentEffect(UpgradeKind kind)
        {
            switch (kind)
            {
                case UpgradeKind.ResourceGrade:
                    return "현재 해금 등급: " + GetResourceGradeName(upgradeState.ResourceLevel);

                case UpgradeKind.GatherSpeed:
                    return "현재 채집 시간: " + CalculateEffectiveHoldSeconds(upgradeState).ToString("0.00") + "초";

                case UpgradeKind.StackCapacity:
                    return "현재 최대 적재량: " + CalculateEffectiveStackMaximum(upgradeState) + "개";

                case UpgradeKind.CampfireEfficiency:
                    int[] values = CalculateEffectiveCampfireMaterials(upgradeState);
                    return "현재 요구량: 나뭇가지 " + values[0] +
                           " · 돌 " + values[1] +
                           " · 횃불 " + values[2];

                case UpgradeKind.DoubleYield:
                    return "현재 수집량: x" + upgradeState.YieldMultiplier;

                default:
                    return string.Empty;
            }
        }

        internal string GetUpgradeNextEffect(UpgradeKind kind)
        {
            if (GetUpgradeCurrentLevel(kind) >= GetUpgradeMaximumLevel(kind))
                return "최대 단계에 도달했습니다.";

            UpgradeState preview = upgradeState.Clone();
            IncreaseUpgradeLevel(preview, kind);

            switch (kind)
            {
                case UpgradeKind.ResourceGrade:
                    return "다음 효과: " + GetResourceGradeName(preview.ResourceLevel) + " 등급 해금";

                case UpgradeKind.GatherSpeed:
                    return "다음 효과: 채집 시간 " +
                           CalculateEffectiveHoldSeconds(preview).ToString("0.00") + "초";

                case UpgradeKind.StackCapacity:
                    return "다음 효과: 슬롯당 " +
                           CalculateEffectiveStackMaximum(preview) + "개";

                case UpgradeKind.CampfireEfficiency:
                    int[] values = CalculateEffectiveCampfireMaterials(preview);
                    return "다음 효과: 나뭇가지 " + values[0] +
                           " · 돌 " + values[1] +
                           " · 횃불 " + values[2];

                case UpgradeKind.DoubleYield:
                    return "다음 효과: 맵 자원 수집량 x2";

                default:
                    return string.Empty;
            }
        }

        private static string GetResourceGradeName(int level)
        {
            switch (level)
            {
                case 0:
                    return "Common";
                case 1:
                    return "Normal";
                case 2:
                    return "Rare";
                case 3:
                    return "Unique";
                case 4:
                    return "Legendary";
                default:
                    return "Common";
            }
        }



        public void OnPlayerEnteredRoom(Photon.Realtime.Player newPlayer)
        {
            // P 메뉴가 닫혀 있을 때는 강화/부품 데이터를 읽거나 다시 발행하지 않습니다.
            // 참가자 입장 사실만 dirty 상태로 남기고, 다음 P 입력에서 한 번 동기화합니다.
            upgradeRoomStateDirty =
                true;

            partsRoomStateDirty =
                true;
        }

        public void OnPlayerLeftRoom(Photon.Realtime.Player otherPlayer)
        {
        }

        public void OnRoomPropertiesUpdate(
            ExitGames.Client.Photon.Hashtable changed)
        {
            if (changed == null)
                return;

            bool upgradeChanged =
                ContainsUpgradeProperty(
                    changed);

            bool partsChanged =
                ContainsPartsProperty(
                    changed);

            if (upgradeChanged)
            {
                upgradeRoomStateDirty =
                    true;

                if (activeWindow != null)
                {
                    LoadUpgradeStateOnDemand();
                }
            }

            if (partsChanged)
            {
                partsRoomStateDirty =
                    true;

                if (activeWindow != null)
                {
                    LoadPartsStateOnDemand();
                    RefreshWindow();
                }
            }

            if (changed.ContainsKey(
                    RunIdKey))
            {
                upgradeRoomStateDirty =
                    true;

                partsRoomStateDirty =
                    true;

                if (activeWindow != null)
                {
                    LoadUpgradeStateOnDemand();
                    LoadPartsStateOnDemand();
                    RefreshWindow();
                }
            }

            if (changed.ContainsKey(
                    SharedMoneyKey) &&
                activeWindow != null)
            {
                cachedSharedMoney =
                    ReadSharedMoney();

                RefreshWindow();
            }
        }

        public void OnPlayerPropertiesUpdate(
            Photon.Realtime.Player targetPlayer,
            ExitGames.Client.Photon.Hashtable changedProps)
        {
        }

        public void OnMasterClientSwitched(
            Photon.Realtime.Player newMasterClient)
        {
            if (newMasterClient == null)
                return;

            upgradeRoomStateDirty =
                true;

            partsRoomStateDirty =
                true;

            if (PhotonNetwork.LocalPlayer == null ||
                newMasterClient.ActorNumber !=
                    PhotonNetwork.LocalPlayer.ActorNumber)
            {
                return;
            }

            // 호스트 승계 시에도 닫힌 P 메뉴의 전체 데이터를 스캔하지 않습니다.
            // 실제 상태는 Room Property에 남아 있고, P를 열 때 한 번 읽어 재확정합니다.
            if (activeWindow != null)
            {
                LoadHubDataOnDemand();

                if (upgradeStateLoaded)
                {
                    PublishUpgradeState(
                        upgradeState,
                        "Host migration");
                }

                if (partsStateLoaded)
                {
                    PublishPartsState(
                        purchasedPartsMask,
                        consumedPartsMask,
                        peakUnlocked,
                        partsRunId,
                        "Host migration");
                }
            }
        }

        private static bool ContainsUpgradeProperty(
            ExitGames.Client.Photon.Hashtable values)
        {
            return values.ContainsKey(UpgradeProtocolKey) ||
                   values.ContainsKey(UpgradeRevisionKey) ||
                   values.ContainsKey(UpgradeOwnerKey) ||
                   values.ContainsKey(UpgradeRunIdKey) ||
                   values.ContainsKey(UpgradeResourceKey) ||
                   values.ContainsKey(UpgradeGatherKey) ||
                   values.ContainsKey(UpgradeStackKey) ||
                   values.ContainsKey(UpgradeCampfireKey) ||
                   values.ContainsKey(UpgradeYieldKey) ||
                   values.ContainsKey(UpgradeBaseHoldKey) ||
                   values.ContainsKey(UpgradeBaseStackKey) ||
                   values.ContainsKey(UpgradeBaseCampfireKey);
        }

        private static bool ContainsPartsProperty(
            ExitGames.Client.Photon.Hashtable values)
        {
            return
                values.ContainsKey(
                    PartsProtocolKey) ||
                values.ContainsKey(
                    PartsRevisionKey) ||
                values.ContainsKey(
                    PartsRunIdKey) ||
                values.ContainsKey(
                    PartsPurchasedMaskKey) ||
                values.ContainsKey(
                    PartsConsumedMaskKey) ||
                values.ContainsKey(
                    PeakUnlockedKey);
        }



        private static void SetConfigValue(
            ConfigFile config,
            ConfigDefinition definition,
            object value)
        {
            if (config == null ||
                definition == null ||
                !config.ContainsKey(definition))
                return;

            ConfigEntryBase entry = config[definition];

            if (entry == null)
                return;

            try
            {
                object converted = null;

                if (entry.SettingType == typeof(int))
                    converted = Convert.ToInt32(value);
                else if (entry.SettingType == typeof(float))
                    converted = Convert.ToSingle(value);
                else if (entry.SettingType == typeof(bool))
                    converted = Convert.ToBoolean(value);

                if (converted != null && !Equals(entry.BoxedValue, converted))
                    entry.BoxedValue = converted;
            }
            catch (Exception exception)
            {
                if (ModLogger != null)
                {
                    ModLogger.LogWarning(
                        "Upgrade config effect failed. Definition=" +
                        definition + " | Error=" + exception.Message);
                }
            }
        }

        private static int ReadInt(
            ExitGames.Client.Photon.Hashtable props,
            string key,
            int fallback)
        {
            object value;

            if (!props.TryGetValue(key, out value) || value == null)
                return fallback;

            try
            {
                return Convert.ToInt32(value);
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        private static float ReadFloat(
            ExitGames.Client.Photon.Hashtable props,
            string key,
            float fallback)
        {
            object value;

            if (!props.TryGetValue(key, out value) || value == null)
                return fallback;

            try
            {
                return Convert.ToSingle(value);
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        private static string ReadString(
            ExitGames.Client.Photon.Hashtable props,
            string key)
        {
            object value;

            if (!props.TryGetValue(key, out value) || value == null)
                return string.Empty;

            return value as string ?? Convert.ToString(value);
        }

        private static int[] ReadIntArray(
            ExitGames.Client.Photon.Hashtable props,
            string key,
            int[] fallback)
        {
            object value;

            if (!props.TryGetValue(key, out value) || value == null)
                return CloneIntArray(fallback);

            int[] direct = value as int[];

            if (direct != null)
                return EnsureThree(direct, fallback);

            object[] boxed = value as object[];

            if (boxed == null)
                return CloneIntArray(fallback);

            int[] result = new int[boxed.Length];

            try
            {
                for (int i = 0; i < boxed.Length; i++)
                    result[i] = Convert.ToInt32(boxed[i]);

                return EnsureThree(result, fallback);
            }
            catch (Exception)
            {
                return CloneIntArray(fallback);
            }
        }

        private static int[] EnsureThree(int[] source, int[] fallback)
        {
            int[] result = new int[3];

            for (int i = 0; i < result.Length; i++)
            {
                result[i] =
                    source != null && i < source.Length
                        ? source[i]
                        : fallback[i];
            }

            return result;
        }

        private static int[] CloneIntArray(int[] source)
        {
            if (source == null)
                return Array.Empty<int>();

            int[] clone = new int[source.Length];
            Array.Copy(source, clone, source.Length);
            return clone;
        }

        private static int LocalActorNumber()
        {
            return PhotonNetwork.LocalPlayer != null
                ? PhotonNetwork.LocalPlayer.ActorNumber
                : 0;
        }



        internal static int CountPlayerResourceUnits(
            global::Player player,
            ushort itemId)
        {
            if (player == null)
                return 0;

            int count = 0;

            if (player.itemSlots != null)
            {
                for (int i = 0; i < player.itemSlots.Length; i++)
                {
                    ItemSlot slot = player.itemSlots[i];

                    if (slot == null ||
                        slot.IsEmpty() ||
                        slot.prefab == null ||
                        slot.prefab.itemID != itemId)
                        continue;

                    count += Mathf.Max(
                        1,
                        InventoryStack.GetStackCount(
                            player,
                            slot.itemSlotID));
                }
            }

            ItemSlot temp = player.tempFullSlot;

            if (temp != null &&
                !temp.IsEmpty() &&
                temp.prefab != null &&
                temp.prefab.itemID == itemId)
            {
                count += Mathf.Max(
                    1,
                    InventoryStack.GetStackCount(
                        player,
                        temp.itemSlotID));
            }

            return count;
        }

        internal static void GrantPickupBonus(
            global::Player player,
            ushort itemId,
            int countBefore)
        {
            if (Instance == null ||
                !PhotonNetwork.IsMasterClient ||
                ResourceYieldMultiplier <= 1 ||
                player == null ||
                !Spawn.IsSaleResourceId(itemId))
                return;

            if (CountPlayerResourceUnits(player, itemId) <= countBefore)
                return;

            int wanted = ResourceYieldMultiplier - 1;
            int granted = 0;

            for (int i = 0; i < wanted; i++)
            {
                ItemSlot slot;

                if (!player.AddItem(itemId, null, out slot))
                    break;

                granted++;
            }

            if (ModLogger != null)
            {
                ModLogger.LogInfo(
                    "Resource yield bonus. ItemID=" + itemId +
                    " | Granted=" + granted + "/" + wanted);
            }
        }

        // -----------------------------------------------------------------
        // Integrated selling backend
        // -----------------------------------------------------------------

        private void ProcessSellRequestOnHost(
            int actorNumber,
            object[] requestData)
        {
            if (!PhotonNetwork.IsMasterClient)
            {
                return;
            }

            double now =
                PhotonNetwork.Time;

            double previousRequestAt;

            if (lastSellRequestAtByActor
                    .TryGetValue(
                        actorNumber,
                        out previousRequestAt) &&
                now -
                    previousRequestAt <
                MinimumRequestIntervalSeconds)
            {
                SendSellResult(
                    actorNumber,
                    false,
                    "판매 요청이 너무 빠릅니다.",
                    0,
                    cachedSharedMoney,
                    -1,
                    -1);

                return;
            }

            lastSellRequestAtByActor[
                actorNumber] =
                    now;

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
                    ReadSharedMoney()) +
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
                HandleSellResult(
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

            character.refs.items.EquipSlot(
                Optionable<byte>.None);
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


        // -----------------------------------------------------------------
        // Integrated crafting backend
        // -----------------------------------------------------------------

        private void ProcessCraftRequestOnHost(int actorNumber, object[] payload)
        {
            if (!PhotonNetwork.IsMasterClient)
                return;

            if (!PhotonNetwork.InRoom ||
                !IsGameplayScene())
            {
                SendCraftResult(
                    actorNumber,
                    false,
                    false,
                    0,
                    "현재는 제작할 수 없습니다.");

                return;
            }

            if (payload == null || payload.Length < 1)
            {
                SendCraftResult(actorNumber, false, false, 0, "잘못된 제작 요청입니다.");
                return;
            }

            double now = PhotonNetwork.Time;
            double previousRequest;

            if (lastCraftRequestAtByActor.TryGetValue(actorNumber, out previousRequest) &&
                now - previousRequest < MinimumRequestIntervalSeconds)
            {
                SendCraftResult(actorNumber, false, false, 0, "제작 요청이 너무 빠릅니다.");
                return;
            }

            lastCraftRequestAtByActor[actorNumber] = now;

            int outputValue;
            try
            {
                outputValue = Convert.ToInt32(payload[0]);
            }
            catch (Exception)
            {
                SendCraftResult(actorNumber, false, false, 0, "제작 아이템 번호를 해석하지 못했습니다.");
                return;
            }

            if (outputValue < 0 || outputValue > ushort.MaxValue)
            {
                SendCraftResult(actorNumber, false, false, 0, "잘못된 제작 아이템입니다.");
                return;
            }

            if (!EnsureCraftRecipesBuilt())
            {
                SendCraftResult(actorNumber, false, false, 0, "제작 데이터베이스가 준비되지 않았습니다.");
                return;
            }

            ushort outputId = (ushort)outputValue;
            CraftRecipe recipe;

            if (!craftRecipesByOutputId.TryGetValue(outputId, out recipe) || recipe == null)
            {
                SendCraftResult(actorNumber, false, false, outputId, "등록되지 않은 제작식입니다.");
                return;
            }

            if (outputId ==
                FlareItemId)
            {
                LoadPartsStateOnDemand();

                if (GetCurrentSegmentIndex() !=
                    (int)Segment.Peak)
                {
                    SendCraftResult(
                        actorNumber,
                        false,
                        false,
                        outputId,
                        "최종 조명탄은 정상 구간에서만 제작할 수 있습니다.");

                    return;
                }

                if (GetCurrentResourceLevel() <
                    ResourceUpgradeMaximum)
                {
                    SendCraftResult(
                        actorNumber,
                        false,
                        false,
                        outputId,
                        "최종 조명탄 제작에는 Legendary 제작 등급이 필요합니다.");

                    return;
                }

                if (peakUnlocked)
                {
                    SendCraftResult(
                        actorNumber,
                        false,
                        false,
                        outputId,
                        "최종 조명탄 제작과 탈출 신호 발사가 이미 완료되었습니다.");

                    return;
                }
            }

            global::Player requester = PlayerHandler.GetPlayer(actorNumber);
            if (requester == null)
            {
                SendCraftResult(actorNumber, false, false, outputId, "플레이어를 찾지 못했습니다.");
                return;
            }

            if (!requester.HasEmptySlot(outputId))
            {
                SendCraftResult(actorNumber, false, false, outputId, "완성품을 받을 공간이 없습니다.");
                return;
            }

            int money = ReadSharedMoney();
            if (money < recipe.MoneyCost)
            {
                SendCraftResult(actorNumber, false, false, outputId, "공유 돈이 부족합니다.");
                return;
            }

            CraftConsumptionPlan plan;
            string missingMessage;

            if (!TryBuildCraftConsumptionPlan(recipe, out plan, out missingMessage))
            {
                SendCraftResult(actorNumber, false, false, outputId, missingMessage);
                return;
            }

            List<ConsumedSelectedSlot> consumedSlots;
            if (!TryConsumePlan(plan, out consumedSlots))
            {
                SendCraftResult(
                    actorNumber,
                    false,
                    false,
                    outputId,
                    "재료 소비 중 인벤토리가 변경되었습니다. 다시 시도하세요.");
                return;
            }

            SetSharedMoneyOnHost(money - recipe.MoneyCost);
            BroadcastConsumedSelectedSlots(consumedSlots);

            bool success = UnityEngine.Random.Range(0f, 100f) < recipe.SuccessChance;

            if (!success)
            {
                SendCraftResult(
                    actorNumber,
                    true,
                    false,
                    outputId,
                    recipe.DisplayName +
                    " 제작에 실패했습니다.\n돈과 재료를 잃었습니다.");

                Logger.LogInfo(
                    "Craft failed. Actor=" + actorNumber +
                    " | OutputID=" + outputId +
                    " | Chance=" + recipe.SuccessChance +
                    " | Money=" + recipe.MoneyCost);
                return;
            }

            ItemSlot grantedSlot;
            bool granted = requester.AddItem(outputId, null, out grantedSlot);

            if (!granted)
            {
                SetSharedMoneyOnHost(ReadSharedMoney() + recipe.MoneyCost);

                SendCraftResult(
                    actorNumber,
                    true,
                    false,
                    outputId,
                    "제작은 성공했지만 지급에 실패했습니다.\n돈은 환불되었고 재료는 복구되지 않았습니다.");

                Logger.LogError(
                    "Craft grant failed. Actor=" + actorNumber +
                    " | OutputID=" + outputId);
                return;
            }

            SendCraftResult(
                actorNumber,
                true,
                true,
                outputId,
                recipe.DisplayName + " 제작에 성공했습니다!");

            if (outputId ==
                FlareItemId)
            {
                MarkFinalFlareCompletedAndNotify();
            }

            Logger.LogInfo(
                "Craft succeeded. Actor=" + actorNumber +
                " | OutputID=" + outputId +
                " | Slot=" +
                (grantedSlot != null ? grantedSlot.itemSlotID.ToString() : "<unknown>") +
                " | Chance=" + recipe.SuccessChance +
                " | Money=" + recipe.MoneyCost);
        }

        private void SendCraftResult(
            int targetActor,
            bool materialsConsumed,
            bool success,
            ushort outputId,
            string message)
        {
            object[] payload =
            {
                materialsConsumed,
                success,
                (int)outputId,
                message ?? string.Empty,
                ReadSharedMoney()
            };

            if (PhotonNetwork.LocalPlayer != null &&
                PhotonNetwork.LocalPlayer.ActorNumber == targetActor)
            {
                HandleCraftResult(payload);
                return;
            }

            RaiseEventOptions options = new RaiseEventOptions
            {
                TargetActors = new[] { targetActor }
            };

            PhotonNetwork.RaiseEvent(
                CraftResultEventCode,
                payload,
                options,
                SendOptions.SendReliable);
        }


        private static bool TryBuildCraftConsumptionPlan(
            CraftRecipe recipe,
            out CraftConsumptionPlan plan,
            out string missingMessage)
        {
            plan = new CraftConsumptionPlan();
            missingMessage = string.Empty;

            List<IngredientLocation> locations = CollectPartyIngredientLocations();

            for (int ingredientIndex = 0;
                 ingredientIndex < recipe.Ingredients.Count;
                 ingredientIndex++)
            {
                IngredientCost cost = recipe.Ingredients[ingredientIndex];
                List<IngredientLocation> matching = new List<IngredientLocation>();
                int total = 0;

                for (int i = 0; i < locations.Count; i++)
                {
                    IngredientLocation location = locations[i];

                    if (location.ItemId != cost.ItemId ||
                        location.AvailableCount <= 0)
                        continue;

                    matching.Add(location);
                    total += location.AvailableCount;
                }

                if (total < cost.Count)
                {
                    missingMessage =
                        GetIngredientDisplayName(cost.ItemId) +
                        "이(가) 부족합니다. " + total + "/" + cost.Count;
                    return false;
                }

                matching.Sort(CompareIngredientLocations);
                int remaining = cost.Count;

                for (int i = 0; i < matching.Count && remaining > 0; i++)
                {
                    IngredientLocation location = matching[i];
                    int units = Mathf.Min(location.AvailableCount, remaining);

                    for (int unit = 0; unit < units; unit++)
                    {
                        plan.Units.Add(new PlannedIngredientUnit
                        {
                            Location = location,
                            ItemId = cost.ItemId
                        });
                    }

                    remaining -= units;
                }
            }

            return true;
        }

        private static bool TryConsumePlan(
            CraftConsumptionPlan plan,
            out List<ConsumedSelectedSlot> consumedSelectedSlots)
        {
            consumedSelectedSlots = new List<ConsumedSelectedSlot>();

            if (!ValidateCraftConsumptionPlan(plan))
                return false;

            HashSet<global::Player> touchedPlayers = new HashSet<global::Player>();
            HashSet<Character> backpackCharacters = new HashSet<Character>();
            HashSet<string> selectedKeys = new HashSet<string>();

            for (int i = 0; i < plan.Units.Count; i++)
            {
                PlannedIngredientUnit unit = plan.Units[i];
                IngredientLocation location = unit.Location;

                if (IsCurrentlySelected(location) &&
                    location.Character != null &&
                    location.Character.photonView != null &&
                    location.Character.photonView.Owner != null)
                {
                    int actor = location.Character.photonView.Owner.ActorNumber;
                    string key = actor + ":" + location.ExternalSlotId;

                    if (selectedKeys.Add(key))
                    {
                        consumedSelectedSlots.Add(new ConsumedSelectedSlot
                        {
                            ActorNumber = actor,
                            SlotId = location.ExternalSlotId
                        });
                    }
                }

                location.Slot.EmptyOut();
                touchedPlayers.Add(location.Player);

                if (location.IsBackpackInternal)
                    backpackCharacters.Add(location.Character);
            }

            foreach (global::Player player in touchedPlayers)
                SyncPlayerInventoryFromHost(player);

            foreach (Character character in backpackCharacters)
                RefreshBackpackVisuals(character);

            RefreshCarryWeights(touchedPlayers);
            return true;
        }

        private static bool ValidateCraftConsumptionPlan(CraftConsumptionPlan plan)
        {
            if (plan == null)
                return false;

            Dictionary<IngredientLocation, int> required =
                new Dictionary<IngredientLocation, int>();

            for (int i = 0; i < plan.Units.Count; i++)
            {
                PlannedIngredientUnit unit = plan.Units[i];

                if (unit == null ||
                    unit.Location == null ||
                    !IsLocationValid(unit.Location, unit.ItemId))
                    return false;

                int count;
                required.TryGetValue(unit.Location, out count);
                required[unit.Location] = count + 1;
            }

            foreach (KeyValuePair<IngredientLocation, int> pair in required)
            {
                IngredientLocation location = pair.Key;
                int available = GetLocationAvailableCount(
                    location.Player,
                    location.Slot,
                    location.IsBackpackInternal,
                    location.ExternalSlotId);

                if (available < pair.Value)
                    return false;
            }

            return true;
        }

        private static List<IngredientLocation> CollectPartyIngredientLocations()
        {
            List<IngredientLocation> result = new List<IngredientLocation>();
            List<Character> characters = PlayerHandler.GetAllPlayerCharacters();

            for (int characterIndex = 0;
                 characterIndex < characters.Count;
                 characterIndex++)
            {
                Character character = characters[characterIndex];

                if (character == null ||
                    character.player == null ||
                    character.photonView == null ||
                    character.photonView.Owner == null ||
                    character.photonView.Owner.IsInactive)
                    continue;

                global::Player player = character.player;

                if (player.itemSlots != null)
                {
                    for (int slotIndex = 0;
                         slotIndex < player.itemSlots.Length;
                         slotIndex++)
                    {
                        AddIngredientLocation(
                            result,
                            player,
                            character,
                            player.itemSlots[slotIndex],
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

                BackpackData backpackData = default(BackpackData);

                bool hasBackpackData =
                    player.backpackSlot != null &&
                    !player.backpackSlot.IsEmpty() &&
                    player.backpackSlot.data != null &&
                    player.backpackSlot.data.TryGetDataEntry<BackpackData>(
                        DataEntryKey.BackpackData,
                        out backpackData);

                if (!hasBackpackData ||
                    backpackData == null ||
                    backpackData.itemSlots == null)
                    continue;

                for (int backpackIndex = 0;
                     backpackIndex < backpackData.itemSlots.Length;
                     backpackIndex++)
                {
                    AddIngredientLocation(
                        result,
                        player,
                        character,
                        backpackData.itemSlots[backpackIndex],
                        true,
                        byte.MaxValue,
                        backpackIndex);
                }
            }

            return result;
        }

        private static void AddIngredientLocation(
            List<IngredientLocation> result,
            global::Player player,
            Character character,
            ItemSlot slot,
            bool backpackInternal,
            byte externalSlotId,
            int backpackSlotIndex)
        {
            if (slot == null ||
                slot.IsEmpty() ||
                slot.prefab == null ||
                !IsSaleResourceId(slot.prefab.itemID))
                return;

            result.Add(new IngredientLocation
            {
                Player = player,
                Character = character,
                Slot = slot,
                IsBackpackInternal = backpackInternal,
                ExternalSlotId = externalSlotId,
                BackpackSlotIndex = backpackSlotIndex,
                ItemId = slot.prefab.itemID,
                AvailableCount = GetLocationAvailableCount(
                    player,
                    slot,
                    backpackInternal,
                    externalSlotId)
            });
        }

        private static int GetLocationAvailableCount(
            global::Player player,
            ItemSlot slot,
            bool backpackInternal,
            byte externalSlotId)
        {
            if (slot == null || slot.IsEmpty())
                return 0;

            return backpackInternal
                ? 1
                : Mathf.Max(1, InventoryStack.GetStackCount(player, externalSlotId));
        }

        private static int CompareIngredientLocations(
            IngredientLocation left,
            IngredientLocation right)
        {
            int result = GetConsumptionPriority(left)
                .CompareTo(GetConsumptionPriority(right));

            if (result != 0)
                return result;

            result = GetActorNumber(left).CompareTo(GetActorNumber(right));
            if (result != 0)
                return result;

            if (left.IsBackpackInternal != right.IsBackpackInternal)
                return left.IsBackpackInternal ? -1 : 1;

            return left.IsBackpackInternal
                ? left.BackpackSlotIndex.CompareTo(right.BackpackSlotIndex)
                : left.ExternalSlotId.CompareTo(right.ExternalSlotId);
        }

        private static int GetConsumptionPriority(IngredientLocation location)
        {
            if (location == null)
                return int.MaxValue;

            if (location.IsBackpackInternal)
                return 0;

            return IsCurrentlySelected(location) ? 2 : 1;
        }

        private static bool IsCurrentlySelected(IngredientLocation location)
        {
            if (location == null ||
                location.IsBackpackInternal ||
                location.Character == null ||
                location.Character.refs == null ||
                location.Character.refs.items == null)
                return false;

            Optionable<byte> selected =
                location.Character.refs.items.currentSelectedSlot;

            return selected.IsSome && selected.Value == location.ExternalSlotId;
        }

        private static int GetActorNumber(IngredientLocation location)
        {
            if (location == null ||
                location.Character == null ||
                location.Character.photonView == null ||
                location.Character.photonView.Owner == null)
                return int.MaxValue;

            return location.Character.photonView.Owner.ActorNumber;
        }

        private static bool IsLocationValid(
            IngredientLocation location,
            ushort expectedItemId)
        {
            return location != null &&
                   location.Player != null &&
                   location.Character != null &&
                   location.Slot != null &&
                   !location.Slot.IsEmpty() &&
                   location.Slot.prefab != null &&
                   location.Slot.prefab.itemID == expectedItemId;
        }

        private static void SyncPlayerInventoryFromHost(global::Player player)
        {
            if (player == null || !PhotonNetwork.IsMasterClient)
                return;

            PhotonView view = player.GetComponent<PhotonView>();
            if (view == null)
                return;

            InventorySyncData syncData = new InventorySyncData(
                player.itemSlots,
                player.backpackSlot,
                player.tempFullSlot);

            view.RPC(
                "SyncInventoryRPC",
                RpcTarget.Others,
                new object[]
                {
                    IBinarySerializable.ToManagedArray<InventorySyncData>(syncData),
                    false
                });

            if (player.itemsChangedAction != null)
                player.itemsChangedAction(player.itemSlots);
        }

        private static void RefreshBackpackVisuals(Character character)
        {
            if (character == null)
                return;

            CharacterBackpackHandler handler =
                character.GetComponent<CharacterBackpackHandler>();

            if (handler != null && handler.backpackVisuals != null)
                handler.backpackVisuals.RefreshVisuals();
        }

        private static void RefreshCarryWeights(HashSet<global::Player> players)
        {
            foreach (global::Player player in players)
            {
                if (player == null ||
                    player.character == null ||
                    player.character.refs == null ||
                    player.character.refs.items == null)
                    continue;

                player.character.refs.items.RefreshAllCharacterCarryWeight();
            }
        }

        private void BroadcastConsumedSelectedSlots(
            List<ConsumedSelectedSlot> slots)
        {
            if (slots == null || slots.Count == 0)
                return;

            object[] payload = new object[1 + slots.Count * 2];
            payload[0] = slots.Count;

            for (int i = 0; i < slots.Count; i++)
            {
                payload[1 + i * 2] = slots[i].ActorNumber;
                payload[2 + i * 2] = (int)slots[i].SlotId;
            }

            RaiseEventOptions options = new RaiseEventOptions
            {
                Receivers = ReceiverGroup.All
            };

            PhotonNetwork.RaiseEvent(
                ConsumedSlotEventCode,
                payload,
                options,
                SendOptions.SendReliable);
        }

        private static void HandleConsumedSelectedSlots(object[] payload)
        {
            if (Instance != null)
            {
                Instance.partyResourceCacheUntil =
                    0f;
            }

            if (payload == null ||
                payload.Length < 1 ||
                PhotonNetwork.LocalPlayer == null)
                return;

            int count;
            try
            {
                count = Convert.ToInt32(payload[0]);
            }
            catch (Exception)
            {
                return;
            }

            int localActor = PhotonNetwork.LocalPlayer.ActorNumber;

            for (int i = 0; i < count; i++)
            {
                int actorIndex = 1 + i * 2;
                int slotIndex = actorIndex + 1;

                if (slotIndex >= payload.Length)
                    break;

                try
                {
                    int actor = Convert.ToInt32(payload[actorIndex]);
                    int slotId = Convert.ToInt32(payload[slotIndex]);

                    if (actor == localActor)
                        UnequipConsumedSlotIfEmpty(slotId);
                }
                catch (Exception)
                {
                }
            }
        }

        private static void UnequipConsumedSlotIfEmpty(int slotId)
        {
            Character character = Character.localCharacter;
            global::Player player = global::Player.localPlayer;

            if (character == null ||
                player == null ||
                character.refs == null ||
                character.refs.items == null ||
                slotId < 0 ||
                slotId > byte.MaxValue)
                return;

            Optionable<byte> selected = character.refs.items.currentSelectedSlot;

            if (selected.IsNone || selected.Value != (byte)slotId)
                return;

            ItemSlot slot = player.GetItemSlot((byte)slotId);

            if (slot != null && !slot.IsEmpty())
                return;

            character.refs.items.EquipSlot(Optionable<byte>.None);
        }


        // -----------------------------------------------------------------
        // Lite unified UI
        // -----------------------------------------------------------------

        private void BuildHubVisuals(
            CraftHubWindow window)
        {
            TMP_FontAsset font =
                ResolveFont();

            Image backdrop =
                CreateImage(
                    "Backdrop",
                    window.transform,
                    new Color(
                        0f,
                        0f,
                        0f,
                        0.72f));

            Stretch(
                backdrop.rectTransform);

            Image panel =
                CreateImage(
                    "Panel",
                    window.transform,
                    new Color(
                        0.085f,
                        0.095f,
                        0.115f,
                        0.99f));

            Center(
                panel.rectTransform,
                new Vector2(
                    1280f,
                    760f),
                Vector2.zero);

            Image topLine =
                CreateImage(
                    "TopLine",
                    panel.transform,
                    new Color(
                        0.82f,
                        0.65f,
                        0.26f,
                        1f));

            Anchor(
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
                    1280f,
                    10f));

            Image sidebar =
                CreateImage(
                    "Sidebar",
                    panel.transform,
                    new Color(
                        0.105f,
                        0.115f,
                        0.135f,
                        1f));

            Anchor(
                sidebar.rectTransform,
                new Vector2(
                    0f,
                    0.5f),
                new Vector2(
                    0f,
                    0.5f),
                new Vector2(
                    100f,
                    0f),
                new Vector2(
                    200f,
                    750f));

            TextMeshProUGUI logo =
                CreateText(
                    "Logo",
                    sidebar.transform,
                    font,
                    "CRAFT\nPEAK",
                    29f,
                    TextAlignmentOptions.Center);

            logo.fontStyle =
                FontStyles.Bold;

            Anchor(
                logo.rectTransform,
                new Vector2(
                    0.5f,
                    1f),
                new Vector2(
                    0.5f,
                    1f),
                new Vector2(
                    0f,
                    -72f),
                new Vector2(
                    170f,
                    92f));

            TextMeshProUGUI title =
                CreateText(
                    "Title",
                    panel.transform,
                    font,
                    "설명",
                    38f,
                    TextAlignmentOptions.Center);

            Anchor(
                title.rectTransform,
                new Vector2(
                    0f,
                    1f),
                new Vector2(
                    0f,
                    1f),
                new Vector2(
                    700f,
                    -52f),
                new Vector2(
                    720f,
                    58f));

            TextMeshProUGUI balance =
                CreateText(
                    "Balance",
                    panel.transform,
                    font,
                    string.Empty,
                    24f,
                    TextAlignmentOptions.TopRight);

            Anchor(
                balance.rectTransform,
                new Vector2(
                    1f,
                    1f),
                new Vector2(
                    1f,
                    1f),
                new Vector2(
                    -155f,
                    -52f),
                new Vector2(
                    260f,
                    42f));

            List<LiteTabView> tabs =
                new List<LiteTabView>();

            string[] tabNames =
            {
                "설명",
                "강화",
                "제작",
                "판매",
                "부품"
            };

            for (int i = 0;
                 i < tabNames.Length;
                 i++)
            {
                HubTab tab =
                    (HubTab)i;

                HubTab captured =
                    tab;

                TextMeshProUGUI tabLabel;

                Button tabButton =
                    CreateButton(
                        "Tab_" +
                        tab,
                        sidebar.transform,
                        font,
                        tabNames[i],
                        new Color(
                            0.18f,
                            0.19f,
                            0.22f,
                            1f),
                        Color.white,
                        out tabLabel);

                Anchor(
                    tabButton.GetComponent<
                        RectTransform>(),
                    new Vector2(
                        0.5f,
                        1f),
                    new Vector2(
                        0.5f,
                        1f),
                    new Vector2(
                        0f,
                        -190f -
                        i *
                        92f),
                    new Vector2(
                        168f,
                        66f));

                tabButton.onClick.AddListener(
                    new UnityAction(
                        delegate
                        {
                            SelectTab(
                                captured);
                        }));

                tabs.Add(
                    new LiteTabView(
                        tab,
                        tabButton,
                        tabButton.GetComponent<
                            Image>()));
            }

            TextMeshProUGUI help =
                CreateText(
                    "Help",
                    sidebar.transform,
                    font,
                    "P / ESC\n닫기",
                    17f,
                    TextAlignmentOptions.Center);

            help.color =
                new Color(
                    0.64f,
                    0.67f,
                    0.72f,
                    1f);

            Anchor(
                help.rectTransform,
                new Vector2(
                    0.5f,
                    0f),
                new Vector2(
                    0.5f,
                    0f),
                new Vector2(
                    0f,
                    60f),
                new Vector2(
                    165f,
                    65f));

            TextMeshProUGUI explanation =
                CreateText(
                    "Explanation",
                    panel.transform,
                    font,
                    BuildExplanationText(),
                    21f,
                    TextAlignmentOptions.TopLeft);

            Anchor(
                explanation.rectTransform,
                new Vector2(
                    0f,
                    1f),
                new Vector2(
                    0f,
                    1f),
                new Vector2(
                    730f,
                    -390f),
                new Vector2(
                    980f,
                    565f));

            List<LiteRowView> rows =
                new List<LiteRowView>();

            for (int i = 0;
                 i < MaximumVisibleInventorySlots;
                 i++)
            {
                int capturedRow =
                    i;

                TextMeshProUGUI rowLabel;

                Button rowButton =
                    CreateButton(
                        "Row_" +
                        i,
                        panel.transform,
                        font,
                        string.Empty,
                        new Color(
                            0.14f,
                            0.15f,
                            0.18f,
                            1f),
                        Color.white,
                        out rowLabel);

                Anchor(
                    rowButton.GetComponent<
                        RectTransform>(),
                    new Vector2(
                        0f,
                        1f),
                    new Vector2(
                        0f,
                        1f),
                    new Vector2(
                        515f,
                        -125f -
                        i *
                        67f),
                    new Vector2(
                        590f,
                        54f));

                rowLabel.alignment =
                    TextAlignmentOptions.Left;

                rowLabel.fontSize =
                    19f;

                rowButton.onClick.AddListener(
                    new UnityAction(
                        delegate
                        {
                            SelectVisibleRow(
                                capturedRow);
                        }));

                rows.Add(
                    new LiteRowView(
                        rowButton,
                        rowButton.GetComponent<
                            Image>(),
                        rowLabel));
            }

            Image detailPanel =
                CreateImage(
                    "DetailPanel",
                    panel.transform,
                    new Color(
                        0.125f,
                        0.135f,
                        0.16f,
                        1f));

            Anchor(
                detailPanel.rectTransform,
                new Vector2(
                    1f,
                    0.5f),
                new Vector2(
                    1f,
                    0.5f),
                new Vector2(
                    -235f,
                    -5f),
                new Vector2(
                    430f,
                    590f));

            TextMeshProUGUI detail =
                CreateText(
                    "Detail",
                    detailPanel.transform,
                    font,
                    string.Empty,
                    21f,
                    TextAlignmentOptions.TopLeft);

            Anchor(
                detail.rectTransform,
                new Vector2(
                    0.5f,
                    1f),
                new Vector2(
                    0.5f,
                    1f),
                new Vector2(
                    0f,
                    -185f),
                new Vector2(
                    380f,
                    315f));

            TextMeshProUGUI status =
                CreateText(
                    "Status",
                    detailPanel.transform,
                    font,
                    string.Empty,
                    18f,
                    TextAlignmentOptions.Center);

            status.color =
                new Color(
                    0.76f,
                    0.78f,
                    0.83f,
                    1f);

            Anchor(
                status.rectTransform,
                new Vector2(
                    0.5f,
                    0f),
                new Vector2(
                    0.5f,
                    0f),
                new Vector2(
                    0f,
                    126f),
                new Vector2(
                    380f,
                    86f));

            TextMeshProUGUI actionLabel;

            Button action =
                CreateButton(
                    "Action",
                    detailPanel.transform,
                    font,
                    "강화 시도",
                    new Color(
                        0.82f,
                        0.65f,
                        0.26f,
                        1f),
                    new Color(
                        0.07f,
                        0.07f,
                        0.08f,
                        1f),
                    out actionLabel);

            Anchor(
                action.GetComponent<
                    RectTransform>(),
                new Vector2(
                    0.5f,
                    0f),
                new Vector2(
                    0.5f,
                    0f),
                new Vector2(
                    0f,
                    48f),
                new Vector2(
                    350f,
                    66f));

            action.onClick.AddListener(
                new UnityAction(
                    ExecuteCurrentTabAction));

            TextMeshProUGUI page =
                CreateText(
                    "Page",
                    panel.transform,
                    font,
                    string.Empty,
                    18f,
                    TextAlignmentOptions.Center);

            Anchor(
                page.rectTransform,
                new Vector2(
                    0f,
                    0f),
                new Vector2(
                    0f,
                    0f),
                new Vector2(
                    515f,
                    40f),
                new Vector2(
                    250f,
                    35f));

            TextMeshProUGUI previousLabel;

            Button previous =
                CreateButton(
                    "Previous",
                    panel.transform,
                    font,
                    "◀ 이전",
                    new Color(
                        0.22f,
                        0.24f,
                        0.28f,
                        1f),
                    Color.white,
                    out previousLabel);

            Anchor(
                previous.GetComponent<
                    RectTransform>(),
                new Vector2(
                    0f,
                    0f),
                new Vector2(
                    0f,
                    0f),
                new Vector2(
                    315f,
                    40f),
                new Vector2(
                    130f,
                    50f));

            previous.onClick.AddListener(
                new UnityAction(
                    PreviousCraftPage));

            TextMeshProUGUI nextLabel;

            Button next =
                CreateButton(
                    "Next",
                    panel.transform,
                    font,
                    "다음 ▶",
                    new Color(
                        0.22f,
                        0.24f,
                        0.28f,
                        1f),
                    Color.white,
                    out nextLabel);

            Anchor(
                next.GetComponent<
                    RectTransform>(),
                new Vector2(
                    0f,
                    0f),
                new Vector2(
                    0f,
                    0f),
                new Vector2(
                    715f,
                    40f),
                new Vector2(
                    130f,
                    50f));

            next.onClick.AddListener(
                new UnityAction(
                    NextCraftPage));

            TextMeshProUGUI closeLabel;

            Button close =
                CreateButton(
                    "Close",
                    panel.transform,
                    font,
                    "닫기",
                    new Color(
                        0.26f,
                        0.28f,
                        0.32f,
                        1f),
                    Color.white,
                    out closeLabel);

            Anchor(
                close.GetComponent<
                    RectTransform>(),
                new Vector2(
                    1f,
                    0f),
                new Vector2(
                    1f,
                    0f),
                new Vector2(
                    -90f,
                    30f),
                new Vector2(
                    130f,
                    48f));

            close.onClick.AddListener(
                new UnityAction(
                    CloseHub));

            window.SetReferences(
                tabs,
                rows,
                title,
                balance,
                explanation,
                detailPanel.gameObject,
                detail,
                status,
                action,
                actionLabel,
                page,
                previous,
                next,
                close);
        }

        private void SelectVisibleRow(
            int rowIndex)
        {
            switch (currentTab)
            {
                case HubTab.Description:
                    break;

                case HubTab.Upgrade:
                    if (rowIndex >=
                            0 &&
                        rowIndex <=
                            (int)UpgradeKind.DoubleYield)
                    {
                        SelectUpgradeKind(
                            (UpgradeKind)rowIndex);
                    }
                    break;

                case HubTab.Craft:
                    SelectCraftCard(
                        rowIndex);
                    break;

                case HubTab.Sell:
                    SelectSellSlot(
                        rowIndex);
                    break;

                case HubTab.Parts:
                    SelectPart(
                        rowIndex);
                    break;
            }
        }

        private void ExecuteCurrentTabAction()
        {
            switch (currentTab)
            {
                case HubTab.Description:
                    break;

                case HubTab.Upgrade:
                    RequestUpgrade();
                    break;

                case HubTab.Craft:
                    RequestCraft();
                    break;

                case HubTab.Sell:
                    RequestSell();
                    break;

                case HubTab.Parts:
                    RequestPartPurchase();
                    break;
            }
        }

        private static string BuildExplanationText()
        {
            return
                "Craft PEAK는 PEAK의 등산을 자원 수집과 제작 중심의 크래프팅 게임으로 바꾸는 모드입니다. " +
                "게임 중 P키를 누르면 통합 상점이 열리며 설명, 강화, 제작, 판매, 부품 탭을 사용할 수 있습니다.\n\n" +
                "맵에 흩어진 자원을 모아 판매하면 파티 공유 돈을 얻습니다. 공유 돈과 재료는 강화, 장비 제작, " +
                "비행기 부품 구매에 함께 사용됩니다. 강화 탭에서 제작 등급을 Common에서 Normal, Rare, Unique, " +
                "Legendary 순서로 올려야 다음 단계의 자원과 진행 조건이 열립니다.\n\n" +
                "모닥불은 단순한 휴식 장소가 아니라 다음 세그먼트로 이동하기 위한 진행 트리거입니다. " +
                "해안, 열대/뿌리숲, 메사/고산지대, 칼데라에서는 현재 구간에 맞는 제작 등급과 비행기 부품이 필요합니다. " +
                "부품은 인벤토리에 들어가지 않고 Photon 방의 공동 진행 상태로 저장되며, 모닥불을 성공적으로 켤 때 사용 완료됩니다.\n\n" +
                "진행 순서는 해안 → 열대/뿌리숲 → 메사/고산지대 → 칼데라 → 가마 → 정상입니다. " +
                "가마에서 정상으로 갈 때는 모닥불과 비행기 부품을 사용하지 않습니다. 정상에 도착한 뒤 제작 탭에서 Legendary 재료가 들어가는 " +
                "가장 비싼 최종 조명탄을 제작하면 최종 탈출 신호가 완성됩니다.\n\n" +
                "핵심 흐름은 자원 수집 → 판매 → 강화와 제작 → 비행기 부품 구매 → 모닥불 점화 → 다음 구간 이동입니다.";
        }

        private static string BuildCraftRowText(
            CraftRecipe recipe)
        {
            if (recipe == null)
            {
                return
                    string.Empty;
            }

            return
                recipe.DisplayName +
                "  |  " +
                recipe.Category +
                " / " +
                GetTierName(
                    recipe.Tier) +
                "  |  " +
                recipe.MoneyCost +
                "원  |  " +
                recipe.SuccessChance
                    .ToString("0") +
                "%";
        }

        private string BuildUpgradeRowText(
            UpgradeKind kind)
        {
            return
                GetUpgradeDisplayName(
                    kind) +
                "  |  " +
                GetUpgradeCurrentLevel(
                    kind) +
                "/" +
                GetUpgradeMaximumLevel(
                    kind);
        }

        private static string BuildSellRowText(
            int slotId)
        {
            global::Player player =
                global::Player.localPlayer;

            if (player == null ||
                player.itemSlots == null ||
                slotId < 0 ||
                slotId >=
                    player.itemSlots.Length)
            {
                return
                    string.Empty;
            }

            ItemSlot slot =
                player.GetItemSlot(
                    (byte)slotId);

            if (slot == null ||
                slot.IsEmpty() ||
                slot.prefab == null)
            {
                return
                    (slotId + 1) +
                    "번 슬롯  |  비어 있음";
            }

            int count =
                Mathf.Max(
                    1,
                    InventoryStack.GetStackCount(
                        player,
                        (byte)slotId));

            bool saleResource =
                Spawn.IsSaleResourceId(
                    slot.prefab.itemID);

            return
                (slotId + 1) +
                "번 슬롯  |  " +
                GetItemDisplayName(
                    slot.prefab) +
                (
                    count >
                        1
                        ? " x" +
                          count
                        : string.Empty
                ) +
                (
                    saleResource
                        ? "  |  " +
                          GetSellPrice(
                              slot.prefab.itemID) +
                          "원"
                        : "  |  판매 불가"
                );
        }

        private static string BuildUpgradeDetailText(
            CraftHub owner)
        {
            UpgradeKind kind =
                owner
                    .SelectedUpgradeKind;

            int current =
                owner
                    .SelectedUpgradeCurrentLevel;

            int maximum =
                owner
                    .SelectedUpgradeMaximumLevel;

            StringBuilder builder =
                owner
                    .sharedTextBuilder;

            builder.Length =
                0;

            builder.Append(
                owner.GetUpgradeDisplayName(
                    kind));

            builder.Append(
                "\n\n");

            builder.Append(
                owner
                    .SelectedUpgradeCurrentEffect);

            builder.Append(
                "\n\n");

            builder.Append(
                owner
                    .SelectedUpgradeNextEffect);

            builder.Append(
                "\n\n");

            if (current >=
                maximum)
            {
                builder.Append(
                    "최대 단계");
            }
            else
            {
                builder.Append(
                    "비용 ");

                builder.Append(
                    owner
                        .SelectedUpgradeCost);

                builder.Append(
                    "원\n성공 확률 ");

                builder.Append(
                    owner
                        .SelectedUpgradeChance
                        .ToString("0.#"));

                builder.Append(
                    "%");
            }

            builder.Append(
                "\n\n");

            if (!owner
                    .UpgradeFailureActive)
            {
                builder.Append(
                    "강화 실패 비활성화");
            }
            else
            {
                builder.Append(
                    owner
                        .UpgradeFailureConsumesCost
                        ? "실패 시 단계 유지 · 비용 소모"
                        : "실패 시 단계 유지 · 비용 보존");
            }

            return
                builder.ToString();
        }

        private static string BuildCraftDetailText(
            CraftHub owner,
            CraftRecipe recipe,
            out bool ready)
        {
            ready =
                false;

            if (recipe == null)
            {
                return
                    "제작할 아이템을 선택하세요.";
            }

            string requirements =
                owner
                    .BuildCraftRequirementText(
                        recipe,
                        out ready);

            StringBuilder builder =
                owner
                    .sharedTextBuilder;

            builder.Length =
                0;

            builder.Append(
                recipe.DisplayName);

            builder.Append(
                "\n");

            builder.Append(
                recipe.Category);

            builder.Append(
                " / ");

            builder.Append(
                GetTierName(
                    recipe.Tier));

            builder.Append(
                "\n\n");

            builder.Append(
                requirements);

            builder.Append(
                "\n\n성공 확률 ");

            builder.Append(
                recipe.SuccessChance
                    .ToString("0"));

            builder.Append(
                "%");

            return
                builder.ToString();
        }

        private static void SetActiveIfChanged(
            GameObject gameObject,
            bool active)
        {
            if (gameObject != null &&
                gameObject.activeSelf !=
                    active)
            {
                gameObject.SetActive(
                    active);
            }
        }

        private static void SetTextIfChanged(
            TextMeshProUGUI label,
            string text)
        {
            if (label == null)
            {
                return;
            }

            string safe =
                text ??
                string.Empty;

            if (!string.Equals(
                    label.text,
                    safe,
                    StringComparison.Ordinal))
            {
                label.text =
                    safe;
            }
        }

        private static void SetInteractableIfChanged(
            Button button,
            bool interactable)
        {
            if (button != null &&
                button.interactable !=
                    interactable)
            {
                button.interactable =
                    interactable;
            }
        }

        private static TMP_FontAsset ResolveFont()
        {
            if (cachedFontAsset !=
                null)
            {
                return
                    cachedFontAsset;
            }

            GUIManager gui =
                GUIManager.instance;

            if (gui != null)
            {
                TextMeshProUGUI sample =
                    gui.GetComponentInChildren<
                        TextMeshProUGUI>(
                        true);

                if (sample != null &&
                    sample.font != null)
                {
                    cachedFontAsset =
                        sample.font;

                    return
                        cachedFontAsset;
                }
            }

            cachedFontAsset =
                TMP_Settings
                    .defaultFontAsset;

            return
                cachedFontAsset;
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

            return
                image;
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

            label.textWrappingMode =
                TextWrappingModes.Normal;

            label.raycastTarget =
                false;

            return
                label;
        }

        private static Button CreateButton(
            string objectName,
            Transform parent,
            TMP_FontAsset font,
            string labelText,
            Color backgroundColor,
            Color textColor,
            out TextMeshProUGUI label)
        {
            Image image =
                CreateImage(
                    objectName,
                    parent,
                    backgroundColor);

            Button button =
                image.gameObject
                    .AddComponent<Button>();

            button.targetGraphic =
                image;

            ColorBlock colors =
                button.colors;

            colors.normalColor =
                Color.white;

            colors.highlightedColor =
                new Color(
                    1.06f,
                    1.06f,
                    1.06f,
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

            label =
                CreateText(
                    "Label",
                    image.transform,
                    font,
                    labelText,
                    22f,
                    TextAlignmentOptions.Center);

            label.color =
                textColor;

            StretchMargin(
                label.rectTransform,
                12f,
                12f,
                5f,
                5f);

            return
                button;
        }

        private static void Stretch(
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

        private static void StretchMargin(
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

        private static void Center(
            RectTransform rectTransform,
            Vector2 size,
            Vector2 position)
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
                position;
        }

        private static void Anchor(
            RectTransform rectTransform,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 position,
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
                position;
        }

        private sealed class LiteTabView
        {
            private static readonly Color Normal =
                new Color(
                    0.18f,
                    0.19f,
                    0.22f,
                    1f);

            private static readonly Color Selected =
                new Color(
                    0.82f,
                    0.65f,
                    0.26f,
                    1f);

            private readonly HubTab tab;
            private readonly Button button;
            private readonly Image image;

            public LiteTabView(
                HubTab value,
                Button tabButton,
                Image tabImage)
            {
                tab =
                    value;

                button =
                    tabButton;

                image =
                    tabImage;
            }

            public void Refresh(
                HubTab selectedTab)
            {
                if (image != null)
                {
                    Color target =
                        tab ==
                            selectedTab
                            ? Selected
                            : Normal;

                    if (image.color !=
                        target)
                    {
                        image.color =
                            target;
                    }
                }

                SetInteractableIfChanged(
                    button,
                    tab !=
                        selectedTab);
            }
        }

        private sealed class LiteRowView
        {
            private static readonly Color Normal =
                new Color(
                    0.14f,
                    0.15f,
                    0.18f,
                    1f);

            private static readonly Color Selected =
                new Color(
                    0.82f,
                    0.65f,
                    0.26f,
                    1f);

            private readonly Button button;
            private readonly Image image;
            private readonly TextMeshProUGUI label;

            public LiteRowView(
                Button rowButton,
                Image rowImage,
                TextMeshProUGUI rowLabel)
            {
                button =
                    rowButton;

                image =
                    rowImage;

                label =
                    rowLabel;
            }

            public void Refresh(
                bool active,
                bool selected,
                string text)
            {
                SetActiveIfChanged(
                    button.gameObject,
                    active);

                if (!active)
                {
                    return;
                }

                if (image != null)
                {
                    Color target =
                        selected
                            ? Selected
                            : Normal;

                    if (image.color !=
                        target)
                    {
                        image.color =
                            target;
                    }
                }

                SetTextIfChanged(
                    label,
                    text);

                SetInteractableIfChanged(
                    button,
                    true);
            }
        }

        private sealed class CraftHubWindow :
            MenuWindow
        {
            private CraftHub owner;

            private List<LiteTabView> tabs =
                new List<LiteTabView>();

            private List<LiteRowView> rows =
                new List<LiteRowView>();

            private TextMeshProUGUI title;
            private TextMeshProUGUI balance;
            private TextMeshProUGUI explanation;
            private GameObject detailPanel;
            private TextMeshProUGUI detail;
            private TextMeshProUGUI status;
            private TextMeshProUGUI actionLabel;
            private TextMeshProUGUI page;

            private Button action;
            private Button previous;
            private Button next;
            private Button close;

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

            public override Selectable
                objectToSelectOnOpen
            {
                get
                {
                    return
                        close;
                }
            }

            public void Initialize(
                CraftHub hub)
            {
                owner =
                    hub;
            }

            public void SetReferences(
                List<LiteTabView> tabViews,
                List<LiteRowView> rowViews,
                TextMeshProUGUI titleText,
                TextMeshProUGUI balanceText,
                TextMeshProUGUI explanationText,
                GameObject detailPanelObject,
                TextMeshProUGUI detailText,
                TextMeshProUGUI statusText,
                Button actionButton,
                TextMeshProUGUI actionButtonLabel,
                TextMeshProUGUI pageText,
                Button previousButton,
                Button nextButton,
                Button closeButton)
            {
                tabs =
                    tabViews ??
                    new List<LiteTabView>();

                rows =
                    rowViews ??
                    new List<LiteRowView>();

                title =
                    titleText;

                balance =
                    balanceText;

                explanation =
                    explanationText;

                detailPanel =
                    detailPanelObject;

                detail =
                    detailText;

                status =
                    statusText;

                action =
                    actionButton;

                actionLabel =
                    actionButtonLabel;

                page =
                    pageText;

                previous =
                    previousButton;

                next =
                    nextButton;

                close =
                    closeButton;
            }

            protected override void OnOpen()
            {
                RefreshContents();
            }

            public void RefreshContents()
            {
                if (owner == null)
                {
                    return;
                }

                HubTab tab =
                    owner.currentTab;

                for (int i = 0;
                     i < tabs.Count;
                     i++)
                {
                    tabs[i].Refresh(
                        tab);
                }

                SetTextIfChanged(
                    balance,
                    "공유 잔액: " +
                    owner.cachedSharedMoney +
                    "원");

                SetActiveIfChanged(
                    explanation != null
                        ? explanation.gameObject
                        : null,
                    tab ==
                    HubTab.Description);

                SetActiveIfChanged(
                    detailPanel,
                    tab !=
                    HubTab.Description);

                switch (tab)
                {
                    case HubTab.Description:
                        RefreshDescription();
                        break;

                    case HubTab.Upgrade:
                        RefreshUpgrade();
                        break;

                    case HubTab.Craft:
                        RefreshCraft();
                        break;

                    case HubTab.Sell:
                        RefreshSell();
                        break;

                    case HubTab.Parts:
                        RefreshParts();
                        break;
                }
            }

            private void RefreshDescription()
            {
                SetTextIfChanged(
                    title,
                    "설명");

                for (int i = 0;
                     i < rows.Count;
                     i++)
                {
                    rows[i].Refresh(
                        false,
                        false,
                        string.Empty);
                }

                SetActiveIfChanged(
                    page.gameObject,
                    false);

                SetActiveIfChanged(
                    previous.gameObject,
                    false);

                SetActiveIfChanged(
                    next.gameObject,
                    false);

                SetActiveIfChanged(
                    action.gameObject,
                    false);

                SetTextIfChanged(
                    explanation,
                    BuildExplanationText());
            }

            private void PrepareInteractiveTab()
            {
                SetActiveIfChanged(
                    action.gameObject,
                    true);

                SetActiveIfChanged(
                    detailPanel,
                    true);
            }

            private void RefreshUpgrade()
            {
                PrepareInteractiveTab();
                SetTextIfChanged(
                    title,
                    "강화");

                for (int i = 0;
                     i < rows.Count;
                     i++)
                {
                    bool active =
                        i <=
                        (int)UpgradeKind.DoubleYield;

                    UpgradeKind kind =
                        active
                            ? (UpgradeKind)i
                            : UpgradeKind.ResourceGrade;

                    rows[i].Refresh(
                        active,
                        active &&
                        owner.selectedUpgradeKind ==
                            kind,
                        active
                            ? owner.BuildUpgradeRowText(
                                kind)
                            : string.Empty);
                }

                SetActiveIfChanged(
                    page.gameObject,
                    false);

                SetActiveIfChanged(
                    previous.gameObject,
                    false);

                SetActiveIfChanged(
                    next.gameObject,
                    false);

                SetTextIfChanged(
                    detail,
                    BuildUpgradeDetailText(
                        owner));

                SetTextIfChanged(
                    status,
                    owner.upgradeStatus);

                bool maximumReached =
                    owner
                        .SelectedUpgradeCurrentLevel >=
                    owner
                        .SelectedUpgradeMaximumLevel;

                SetInteractableIfChanged(
                    action,
                    owner
                        .CanAttemptUpgrade);

                SetTextIfChanged(
                    actionLabel,
                    maximumReached
                        ? "최대 단계"
                        : (
                            owner.pendingRequest ==
                                PendingRequest.Upgrade
                                ? "처리 중..."
                                : "강화 시도"
                        ));
            }

            private void RefreshCraft()
            {
                PrepareInteractiveTab();

                SetTextIfChanged(
                    title,
                    "제작");

                CraftRecipe selected =
                    owner
                        .SelectedCraftRecipe;

                for (int i = 0;
                     i < rows.Count;
                     i++)
                {
                    CraftRecipe recipe =
                        owner
                            .GetCraftRecipeAtCard(
                                i);

                    rows[i].Refresh(
                        recipe != null,
                        recipe != null &&
                        selected != null &&
                        recipe.OutputItemId ==
                            selected.OutputItemId,
                        BuildCraftRowText(
                            recipe));
                }

                SetActiveIfChanged(
                    page.gameObject,
                    true);

                SetActiveIfChanged(
                    previous.gameObject,
                    true);

                SetActiveIfChanged(
                    next.gameObject,
                    true);

                SetTextIfChanged(
                    page,
                    "페이지 " +
                    (
                        owner.craftPage +
                        1
                    ) +
                    " / " +
                    owner.CraftTotalPages);

                SetInteractableIfChanged(
                    previous,
                    owner.craftPage >
                    0);

                SetInteractableIfChanged(
                    next,
                    owner.craftPage <
                    owner.CraftTotalPages -
                    1);

                bool ready;

                SetTextIfChanged(
                    detail,
                    BuildCraftDetailText(
                        owner,
                        selected,
                        out ready));

                SetTextIfChanged(
                    status,
                    owner.craftStatus);

                SetInteractableIfChanged(
                    action,
                    selected != null &&
                    ready &&
                    owner.pendingRequest ==
                        PendingRequest.None);

                SetTextIfChanged(
                    actionLabel,
                    owner.pendingRequest ==
                        PendingRequest.Craft
                        ? "처리 중..."
                        : "제작 시도");
            }

            private void RefreshSell()
            {
                PrepareInteractiveTab();

                SetTextIfChanged(
                    title,
                    "판매");

                global::Player player =
                    global::Player.localPlayer;

                int slotCount =
                    player != null &&
                    player.itemSlots != null
                        ? Mathf.Min(
                            rows.Count,
                            player.itemSlots.Length)
                        : 0;

                for (int i = 0;
                     i < rows.Count;
                     i++)
                {
                    bool active =
                        i <
                        slotCount;

                    rows[i].Refresh(
                        active,
                        active &&
                        owner.selectedSellSlotId ==
                            i,
                        active
                            ? BuildSellRowText(
                                i)
                            : string.Empty);
                }

                SetActiveIfChanged(
                    page.gameObject,
                    false);

                SetActiveIfChanged(
                    previous.gameObject,
                    false);

                SetActiveIfChanged(
                    next.gameObject,
                    false);

                bool canSell;

                string selectedText =
                    owner
                        .BuildSelectedSellText(
                            out canSell);

                SetTextIfChanged(
                    detail,
                    selectedText +
                    "\n\n가격표\nCommon 1원 · Normal 3원\nRare 7원 · Unique 15원\nLegendary 50원");

                SetTextIfChanged(
                    status,
                    owner.sellStatus);

                SetInteractableIfChanged(
                    action,
                    canSell &&
                    owner.pendingRequest ==
                        PendingRequest.None);

                SetTextIfChanged(
                    actionLabel,
                    owner.pendingRequest ==
                        PendingRequest.Sell
                        ? "처리 중..."
                        : "1개 판매");
            }

            private void RefreshParts()
            {
                PrepareInteractiveTab();

                SetTextIfChanged(
                    title,
                    "부품");

                for (int i = 0;
                     i < rows.Count;
                     i++)
                {
                    bool active =
                        i <
                        PlanePartRecipes.Length;

                    rows[i].Refresh(
                        active,
                        active &&
                        owner.selectedPartIndex ==
                            i,
                        active
                            ? owner.BuildPartRowText(
                                i)
                            : string.Empty);
                }

                SetActiveIfChanged(
                    page.gameObject,
                    false);

                SetActiveIfChanged(
                    previous.gameObject,
                    false);

                SetActiveIfChanged(
                    next.gameObject,
                    false);

                bool ready;

                SetTextIfChanged(
                    detail,
                    owner.BuildPartDetailText(
                        owner.SelectedPartRecipe,
                        out ready));

                SetTextIfChanged(
                    status,
                    owner.partsStatus);

                SetInteractableIfChanged(
                    action,
                    ready);

                SetTextIfChanged(
                    actionLabel,
                    owner.pendingRequest ==
                        PendingRequest.Parts
                        ? "처리 중..."
                        : "부품 구매");
            }
        }
    }

    /// <summary>
    /// 맵의 판매용 자원을 정상적으로 주운 경우에만 수집량 배율을 적용합니다.
    /// 제작 결과, 상점 처리, 관리자 지급에는 적용되지 않습니다.
    /// </summary>
    [HarmonyPatch(
        typeof(Item),
        "RequestPickup")]
    internal static class
        CraftHubItemRequestPickupPatch
    {
        [HarmonyPrefix]
        private static void Prefix(
            Item __instance,
            PhotonView characterView,
            ref CraftHub.PickupBonusState
                __state)
        {
            __state =
                default(
                    CraftHub
                        .PickupBonusState);

            if (CraftHub.Instance ==
                    null ||
                !PhotonNetwork.IsMasterClient ||
                CraftHub
                    .ResourceYieldMultiplier <=
                    1 ||
                __instance == null ||
                characterView == null)
            {
                return;
            }

            ushort itemId =
                __instance
                    .isSecretlyOtherItemPrefab !=
                    null
                    ? __instance
                        .isSecretlyOtherItemPrefab
                        .itemID
                    : __instance.itemID;

            if (!Spawn.IsSaleResourceId(
                    itemId))
            {
                return;
            }

            Character character =
                characterView
                    .GetComponent<Character>();

            if (character == null ||
                character.player ==
                    null)
            {
                return;
            }

            __state.Eligible =
                true;

            __state.Player =
                character.player;

            __state.ItemId =
                itemId;

            __state.CountBefore =
                CraftHub
                    .CountPlayerResourceUnits(
                        character.player,
                        itemId);
        }

        [HarmonyPostfix]
        private static void Postfix(
            CraftHub.PickupBonusState
                __state)
        {
            if (!__state.Eligible)
            {
                return;
            }

            CraftHub.GrantPickupBonus(
                __state.Player,
                __state.ItemId,
                __state.CountBefore);
        }
    }

    /// <summary>
    /// CampfireGate가 재료를 소비하기 전에 제작 등급과 비행기 부품을
    /// 호스트 권한으로 검증합니다.
    /// </summary>
    [HarmonyPatch(
        typeof(CampfireGate),
        "ProcessIgniteRequestOnHost")]
    internal static class
        CraftHubCampfireProgressionGatePatch
    {
        [HarmonyPrefix]
        private static bool Prefix(
            int __0,
            object[] __1)
        {
            int requesterActorNumber =
                __0;

            object[] requestData =
                __1;

            if (CraftHub.Instance == null ||
                !PhotonNetwork.IsMasterClient)
            {
                return true;
            }

            if (!CraftHub.IsCurrentCampfireRequest(
                    requestData))
            {
                CraftHub.Instance
                    .SendProgressionNotice(
                        requesterActorNumber,
                        "현재 구간의 모닥불만 다음 세그먼트 진행에 사용할 수 있습니다.");

                return false;
            }

            string message;

            if (CraftHub.ValidateCampfireProgression(
                    out message))
            {
                return true;
            }

            CraftHub.Instance
                .SendProgressionNotice(
                    requesterActorNumber,
                    message);

            return false;
        }
    }

    /// <summary>
    /// 원본 모닥불 재료 안내 아래에 제작 등급과 비행기 부품 진행 조건을 표시합니다.
    /// </summary>
    [HarmonyPatch(
        typeof(global::Campfire),
        "GetInteractionText")]
    internal static class
        CraftHubCampfireProgressionPromptPatch
    {
        [HarmonyPostfix]
        private static void Postfix(
            global::Campfire __instance,
            ref string __result)
        {
            if (CraftHub.Instance == null ||
                __instance == null ||
                __instance.isPyre ||
                !CraftHub.IsCurrentSegmentCampfire(
                    __instance) ||
                __instance.Lit ||
                __instance.state !=
                    global::Campfire
                        .FireState
                        .Off)
            {
                return;
            }

            __result +=
                CraftHub
                    .BuildCampfireProgressionPrompt();
        }
    }

    /// <summary>
    /// 모닥불 점화가 실제로 성공한 뒤 현재 구간의 비행기 부품을 사용 완료로 저장합니다.
    /// </summary>
    [HarmonyPatch(
        typeof(global::Campfire),
        "Light_Rpc")]
    internal static class
        CraftHubCampfirePartConsumePatch
    {
        [HarmonyPrefix]
        private static void Prefix(
            global::Campfire __instance,
            ref int __state)
        {
            __state =
                -1;

            if (CraftHub.Instance == null ||
                __instance == null ||
                __instance.isPyre ||
                !CraftHub.IsCurrentSegmentCampfire(
                    __instance))
            {
                return;
            }

            __state =
                (int)MapHandler
                    .CurrentSegmentNumber;
        }

        [HarmonyPostfix]
        private static void Postfix(
            int __state)
        {
            if (CraftHub.Instance == null ||
                __state <
                    (int)Segment.Beach ||
                __state >
                    (int)Segment.Caldera)
            {
                return;
            }

            CraftHub.Instance
                .CloseHub();

            if (!PhotonNetwork.IsMasterClient)
            {
                return;
            }

            CraftHub.Instance
                .MarkPartConsumedAfterCampfire(
                    __state);
        }
    }

    /// <summary>
    /// 기존 Open/Shop.cs를 제거해도 Shop 참조가 남아 있는 다른 소스가
    /// 컴파일되도록 제공하는 경량 호환 파사드입니다.
    ///
    /// 실제 플러그인, 판매 상태, UI 및 네트워크 처리는 모두 CraftHub가 담당합니다.
    /// </summary>
    public sealed class Shop
    {
        public const string PluginGuid =
            CraftHub.PluginGuid;

        public const string PluginName =
            CraftHub.PluginName;

        public const string PluginVersion =
            CraftHub.PluginVersion;

        private static readonly Shop FacadeInstance =
            new Shop();

        private Shop()
        {
        }

        public static Shop Instance
        {
            get
            {
                return
                    CraftHub.Instance != null
                        ? FacadeInstance
                        : null;
            }
        }

        public static int SharedMoney
        {
            get
            {
                return
                    CraftHub.Instance != null
                        ? CraftHub.Instance
                            .SharedMoney
                        : 0;
            }
        }

        public void OpenShop()
        {
            if (CraftHub.Instance != null)
            {
                CraftHub.Instance
                    .OpenCompatibilityTab(
                        CraftHub.HubTab.Sell);
            }
        }

        public void CloseShop()
        {
            if (CraftHub.Instance != null)
            {
                CraftHub.Instance
                    .CloseHub();
            }
        }

        internal void SelectInventorySlot(
            byte slotId)
        {
            if (CraftHub.Instance != null)
            {
                CraftHub.Instance
                    .SelectSellSlot(
                        slotId);
            }
        }

        internal string BuildSelectedItemText(
            out bool canSell)
        {
            if (CraftHub.Instance == null)
            {
                canSell =
                    false;

                return
                    "CraftHub가 아직 준비되지 않았습니다.";
            }

            return
                CraftHub.Instance
                    .BuildSelectedSellText(
                        out canSell);
        }

        internal bool IsSellRequestPending()
        {
            return
                CraftHub.Instance != null &&
                CraftHub.Instance
                    .IsPending(
                        CraftHub.PendingRequest.Sell);
        }

        internal void RefreshWindow()
        {
            if (CraftHub.Instance != null)
            {
                CraftHub.Instance
                    .RefreshCompatibilityWindow();
            }
        }
    }

    /// <summary>
    /// 기존 Store.cs를 제거해도 Store 참조가 남아 있는 다른 소스가
    /// 컴파일되도록 제공하는 경량 호환 파사드입니다.
    ///
    /// 제작식, 재료 소비, 성공 판정 및 완성품 지급은 CraftHub가 직접 처리합니다.
    /// </summary>
    public sealed class Store
    {
        public const string PluginGuid =
            CraftHub.PluginGuid;

        public const string PluginName =
            CraftHub.PluginName;

        public const string PluginVersion =
            CraftHub.PluginVersion;

        private static readonly Store FacadeInstance =
            new Store();

        private Store()
        {
        }

        public static Store Instance
        {
            get
            {
                return
                    CraftHub.Instance != null
                        ? FacadeInstance
                        : null;
            }
        }

        public void OpenStore()
        {
            if (CraftHub.Instance != null)
            {
                CraftHub.Instance
                    .OpenCompatibilityTab(
                        CraftHub.HubTab.Craft);
            }
        }

        public void CloseStore()
        {
            if (CraftHub.Instance != null)
            {
                CraftHub.Instance
                    .CloseHub();
            }
        }

        internal int GetSharedMoney()
        {
            return
                CraftHub.Instance != null
                    ? CraftHub.Instance
                        .SharedMoney
                    : 0;
        }

        internal bool IsRequestPending()
        {
            return
                CraftHub.Instance != null &&
                CraftHub.Instance
                    .IsPending(
                        CraftHub.PendingRequest.Craft);
        }

        internal void RefreshWindow()
        {
            if (CraftHub.Instance != null)
            {
                CraftHub.Instance
                    .RefreshCompatibilityWindow();
            }
        }
    }

    /// <summary>
    /// 기존 Upgrade.cs를 제거해도 Upgrade 참조가 남아 있는 다른 소스가
    /// 컴파일되도록 제공하는 경량 호환 파사드입니다.
    ///
    /// 강화 단계, 확률, 효과 적용 및 Photon 상태 보존은 CraftHub가 직접 처리합니다.
    /// </summary>
    public sealed class Upgrade
    {
        public const string PluginGuid =
            CraftHub.PluginGuid;

        public const string PluginName =
            CraftHub.PluginName;

        public const string PluginVersion =
            CraftHub.PluginVersion;

        public enum UpgradeKind
        {
            ResourceGrade = 0,
            GatherSpeed = 1,
            StackCapacity = 2,
            CampfireEfficiency = 3,
            DoubleYield = 4
        }

        private static readonly Upgrade FacadeInstance =
            new Upgrade();

        private Upgrade()
        {
        }

        public static Upgrade Instance
        {
            get
            {
                return
                    CraftHub.Instance != null
                        ? FacadeInstance
                        : null;
            }
        }

        public static int ResourceYieldMultiplier
        {
            get
            {
                return
                    CraftHub
                        .ResourceYieldMultiplier;
            }
        }

        public static int ResourceUpgradeLevel
        {
            get
            {
                return
                    CraftHub
                        .ResourceUpgradeLevel;
            }
        }

        public static int GatherUpgradeLevel
        {
            get
            {
                return
                    CraftHub
                        .GatherUpgradeLevel;
            }
        }

        public static int StackUpgradeLevel
        {
            get
            {
                return
                    CraftHub
                        .StackUpgradeLevel;
            }
        }

        public static int CampfireUpgradeLevel
        {
            get
            {
                return
                    CraftHub
                        .CampfireUpgradeLevel;
            }
        }

        public static bool DoubleYieldUnlocked
        {
            get
            {
                return
                    CraftHub
                        .DoubleYieldUnlocked;
            }
        }

        internal UpgradeKind SelectedKind
        {
            get
            {
                return
                    CraftHub.Instance != null
                        ? ToCompatibilityKind(
                            CraftHub.Instance
                                .SelectedUpgradeKind)
                        : UpgradeKind.ResourceGrade;
            }
        }

        internal int SelectedCurrentLevel
        {
            get
            {
                return
                    CraftHub.Instance != null
                        ? CraftHub.Instance
                            .SelectedUpgradeCurrentLevel
                        : 0;
            }
        }

        internal int SelectedMaximumLevel
        {
            get
            {
                return
                    CraftHub.Instance != null
                        ? CraftHub.Instance
                            .SelectedUpgradeMaximumLevel
                        : 0;
            }
        }

        internal int SelectedCost
        {
            get
            {
                return
                    CraftHub.Instance != null
                        ? CraftHub.Instance
                            .SelectedUpgradeCost
                        : 0;
            }
        }

        internal float SelectedChance
        {
            get
            {
                return
                    CraftHub.Instance != null
                        ? CraftHub.Instance
                            .SelectedUpgradeChance
                        : 0f;
            }
        }

        internal bool CanAttempt
        {
            get
            {
                return
                    CraftHub.Instance != null &&
                    CraftHub.Instance
                        .CanAttemptUpgrade;
            }
        }

        internal bool RequestPending
        {
            get
            {
                return
                    CraftHub.Instance != null &&
                    CraftHub.Instance
                        .IsPending(
                            CraftHub.PendingRequest.Upgrade);
            }
        }

        internal bool FailureActive
        {
            get
            {
                return
                    CraftHub.Instance != null &&
                    CraftHub.Instance
                        .UpgradeFailureActive;
            }
        }

        internal bool FailureConsumesCost
        {
            get
            {
                return
                    CraftHub.Instance != null &&
                    CraftHub.Instance
                        .UpgradeFailureConsumesCost;
            }
        }

        internal int SharedMoney
        {
            get
            {
                return
                    CraftHub.Instance != null
                        ? CraftHub.Instance
                            .SharedMoney
                        : 0;
            }
        }

        public void OpenWindow()
        {
            if (CraftHub.Instance != null)
            {
                CraftHub.Instance
                    .OpenCompatibilityTab(
                        CraftHub.HubTab.Upgrade);
            }
        }

        public void CloseUpgradeWindow()
        {
            if (CraftHub.Instance != null)
            {
                CraftHub.Instance
                    .CloseHub();
            }
        }

        internal void SelectKind(
            UpgradeKind kind)
        {
            if (CraftHub.Instance != null)
            {
                CraftHub.Instance
                    .SelectUpgradeKind(
                        ToHubKind(
                            kind));
            }
        }

        internal string DisplayName(
            UpgradeKind kind)
        {
            return
                CraftHub.Instance != null
                    ? CraftHub.Instance
                        .GetUpgradeDisplayName(
                            ToHubKind(
                                kind))
                    : kind.ToString();
        }

        internal string CurrentEffect(
            UpgradeKind kind)
        {
            return
                CraftHub.Instance != null
                    ? CraftHub.Instance
                        .GetUpgradeCurrentEffect(
                            ToHubKind(
                                kind))
                    : string.Empty;
        }

        internal string NextEffect(
            UpgradeKind kind)
        {
            return
                CraftHub.Instance != null
                    ? CraftHub.Instance
                        .GetUpgradeNextEffect(
                            ToHubKind(
                                kind))
                    : string.Empty;
        }

        internal void RefreshWindow()
        {
            if (CraftHub.Instance != null)
            {
                CraftHub.Instance
                    .RefreshCompatibilityWindow();
            }
        }

        internal static int CountResource(
            global::Player player,
            ushort itemId)
        {
            return
                CraftHub
                    .CountPlayerResourceUnits(
                        player,
                        itemId);
        }

        internal static void GrantBonus(
            global::Player player,
            ushort itemId,
            int countBefore)
        {
            CraftHub
                .GrantPickupBonus(
                    player,
                    itemId,
                    countBefore);
        }

        private static CraftHub.UpgradeKind ToHubKind(
            UpgradeKind kind)
        {
            return
                (CraftHub.UpgradeKind)
                    (int)kind;
        }

        private static UpgradeKind ToCompatibilityKind(
            CraftHub.UpgradeKind kind)
        {
            return
                (UpgradeKind)
                    (int)kind;
        }
    }

}
