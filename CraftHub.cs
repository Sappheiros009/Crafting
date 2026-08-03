// CRAFT PEAK UNIFIED HUB
//
// CraftHub는 게임플레이 씬에서 P키로 여닫는 통합 메뉴를 생성합니다.
// Airport, Title, Pretitle 씬에서는 메뉴를 열지 않습니다.
//
// 화면에 생성되는 탭
// - 설명
// - 강화
// - 제작
// - 판매
// - 부품
//
// 상태 및 네트워크 처리
// - 공유 돈을 Photon Room CustomProperties에 저장하고 호스트가 변경합니다.
// - 판매 요청은 호스트가 슬롯, ItemID, GUID, 스택 수량과 판매가를 검증한 뒤 요청 수량을 소비합니다.
// - 제작 요청은 호스트가 공유 돈, 제작 등급, 출력 공간과 제작 요청자 본인의 재료를 다시 검증합니다.
// - 제작 탭의 제작 재료는 제작 요청자 본인의 일반 슬롯, 임시 손 슬롯과 배낭 내부 슬롯에서만 합산·소비합니다.
// - 제작식과 진행용 재료 조합은 호스트가 Room Property에 기록한 공유 시드로 결정합니다.
// - 강화 상태는 자원 등급, 적재 단계, 다음 모닥불 단계, 수집량 배율과 판매 배율을 Room Property로 동기화합니다.
// - 적재 단계는 슬롯 최대 수량을 증가시키고, 수집량은 x1~x5, 판매가는 x1·x2·x4·x8·x16으로 적용합니다.
// - ApplyUpgradeEffects는 CalculateEffectiveCampfireMaterials가 반환하는 0, 0, 0을 CampfireGate 재료 설정에 적용합니다.
// - 비행기 부품은 구간별 구매 마스크와 사용 마스크로 저장하며, 현재 모닥불 점화 성공 후 해당 구간 부품을 사용 처리합니다.
// - 정상에서 ItemID 32 조명탄 제작이 성공하면 PeakUnlocked 상태를 저장하고 완료 이벤트를 전체에 전송합니다.
//
// UI 및 갱신
// - 제작 탭은 등산, 음식, 힐, 부활, 필수 분류와 페이지당 8개 행을 표시합니다.
// - 제작 행은 RawImage 아이콘과 텍스트를 사용합니다.
// - 공유 돈, 제작식, 강화와 부품 상태는 메뉴를 열거나 관련 Room Property가 변경될 때 갱신합니다.
// - 파티 재료 합계는 1초 동안 캐시하며 공용 StringBuilder와 캐시된 TMP 폰트를 재사용합니다.
// - Developer 열거값과 돈 요청 로직은 남아 있지만 탭 버튼을 만들지 않고 Developer 탭 선택은 Description으로 전환합니다.
//
// Harmony 패치
// - Item.RequestPickup 이후 판매 자원의 수집량 배율을 보정합니다.
// - CampfireGate.ProcessIgniteRequestOnHost 전에 현재 모닥불과 진행 조건을 검증합니다.
// - Campfire.GetInteractionText에 진행 조건 안내를 추가합니다.
// - Campfire.Light_Rpc 전후로 출발 구간을 보존하고 비행기 부품 사용 상태를 갱신합니다.
//
// Shop, Store, Upgrade 클래스는 CraftHub의 판매, 제작, 강화 기능으로 연결되는 파사드입니다.

using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using ExitGames.Client.Photon;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
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
            "2.13.2";

        public const string DeveloperName =
            "Sapphire009";

        // 판매, 제작, 강화, 부품, 진행 알림, 최종 조명탄과 개발자 돈 요청에 사용하는 Photon 이벤트 코드입니다.
        private const byte SellRequestEventCode = 140;
        private const byte SellResultEventCode = 141;
        private const byte SellConsumeRequestEventCode = 153;
        private const byte SellConsumeAckEventCode = 154;

        private const byte CraftRequestEventCode = 142;
        private const byte CraftResultEventCode = 143;
        private const byte ConsumedSlotEventCode = 144;

        private const byte UpgradeRequestEventCode = 145;
        private const byte UpgradeResultEventCode = 146;

        private const byte PartPurchaseRequestEventCode = 147;
        private const byte PartPurchaseResultEventCode = 148;
        private const byte ProgressionNoticeEventCode = 149;
        private const byte FinalFlareCompletedEventCode = 150;

        private const byte DeveloperMoneyRequestEventCode = 151;
        private const byte DeveloperMoneyResultEventCode = 152;

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

        // 마스터 클라이언트는 Recipe Room Property가 없을 때 공유 시드를 생성합니다.
        // 클라이언트는 해당 시드를 받은 뒤 동일한 제작식과 진행 재료 목록을 구성합니다.
        // RunId 변경만으로 시드를 바꾸지 않으며 Room Property에 값이 있는 동안 재사용합니다.
        private const string RecipeProtocolKey =
            "CraftPeak.Recipes.Protocol";

        private const string RecipeRunIdKey =
            "CraftPeak.Recipes.RunId";

        private const string RecipeSeedKey =
            "CraftPeak.Recipes.Seed";

        private const int RecipeProtocolVersion = 1;

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

        private const string UpgradeStackKey =
            "CraftPeak.Upgrade.Stack";

        private const string UpgradeCampfireKey =
            "CraftPeak.Upgrade.Campfire";

        private const string UpgradeYieldKey =
            "CraftPeak.Upgrade.Yield";

        private const string UpgradeSellMultiplierKey =
            "CraftPeak.Upgrade.SellMultiplier";

        private const string UpgradeBaseStackKey =
            "CraftPeak.Upgrade.BaseStack";

        private const string UpgradeBaseCampfireKey =
            "CraftPeak.Upgrade.BaseCampfire";

        private const int UpgradeProtocolVersion = 1;

        private const int ResourceUpgradeMaximum = 4;
        private const int StackUpgradeMaximum = 4;

        private const int DefaultBaseStackCount =
            10;
        private const int CampfireUpgradeMaximum = 4;
        private const int YieldUpgradeMaximum = 4;
        private const int SellValueUpgradeMaximum = 4;

        private const double MinimumRequestIntervalSeconds = 0.25d;
        private const float PartyResourceCacheSeconds = 1.00f;

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
            0.90f,
            0.78f,
            0.65f
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

        // 스카우트 인형 제작식의 고정 ID 대체값과 부활 UI 분류에 사용합니다.
        private const ushort ScoutEffigyItemId = 67;

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

        // 판매 자원 11종과 횃불을 모아 둔 제작 재료 ID 목록입니다.
        private static readonly ushort[] CraftIngredientIds =
        {
            FireWoodItemId,
            StoneItemId,
            ConchItemId,
            TorchItemId,
            BinocularsItemId,
            BingBongItemId,
            BugleItemId,
            FrisbeeItemId,
            GuidebookItemId,
            ScrollItemId,
            WeirdShroomItemId,
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

        private static readonly List<PlanePartRecipe>
            PlanePartRecipes =
                new List<PlanePartRecipe>();

        private static readonly List<CampfireBuildRecipe>
            NextCampfireRecipes =
                new List<CampfireBuildRecipe>();

        private static bool progressionRecipesBuilt;

        private static string progressionRecipeRunId =
            string.Empty;

        private int sharedRecipeSeed;
        private string sharedRecipeRunId =
            string.Empty;
        private bool sharedRecipeSeedLoaded;


        private readonly List<CraftRecipe> craftRecipes =
            new List<CraftRecipe>();

        private readonly Dictionary<ushort, CraftRecipe>
            craftRecipesByOutputId =
                new Dictionary<ushort, CraftRecipe>();

        private readonly Dictionary<int, double>
            lastSellRequestAtByActor =
                new Dictionary<int, double>();

        private readonly Dictionary<int, PendingSaleTransaction>
            pendingSaleTransactions =
                new Dictionary<int, PendingSaleTransaction>();

        private readonly HashSet<string>
            reservedOrSoldItemGuids =
                new HashSet<string>(
                    StringComparer.Ordinal);

        // 동일한 가상 손 슬롯 아이템 GUID에 대해 장착 코루틴이 중복 실행되는 것을 막습니다.
        private readonly HashSet<string>
            pendingTempHandDrawGuids =
                new HashSet<string>(
                    StringComparer.Ordinal);

        private LocalPendingSale localPendingSale;
        private int nextSaleTransactionId;

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

        // 언어 선택은 각 클라이언트의 UI에만 적용되며 네트워크 상태에는 저장하지 않습니다.
        private HubLanguage currentLanguage =
            HubLanguage.English;

        private UpgradeKind selectedUpgradeKind =
            UpgradeKind.ResourceGrade;

        private int selectedCraftRecipeIndex = -1;
        private int craftPage;

        private CraftUiCategory selectedCraftUiCategory =
            CraftUiCategory.Climbing;

        private int selectedSellSlotId = -1;
        private int selectedSellQuantity = 1;
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

        private UpgradeFormulaConfig resourceUpgradeFormula;
        private UpgradeFormulaConfig stackUpgradeFormula;
        private UpgradeFormulaConfig campfireUpgradeFormula;

        private ConfigEntry<int>
            doubleYieldCostConfig;

        private UpgradeFormulaConfig
            sellValueUpgradeFormula;

        private string upgradeStatus =
            "강화 항목을 선택하세요.";

        private string craftStatus =
            "제작할 아이템을 선택하세요.";

        private string sellStatus =
            "판매할 인벤토리 슬롯을 선택하세요.";

        private string partsStatus =
            "현재 세그먼트에 필요한 비행기 부품을 구매하세요.";

        private string developerStatus =
            "버튼을 누르면 로컬에서 +100원이 누적된 뒤 묶어서 공유됩니다.";

        private int developerMoneyRequestSequence;

        private int developerMoneyPendingResponses;

        private int developerHostAuthoritativeBalance = -1;

        private const int DeveloperMoneyPerClick =
            100;

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
            Parts = 4,
            Developer = 5
        }

        internal enum HubLanguage
        {
            English = 0,
            Korean = 1,
            Chinese = 2,
            Japanese = 3,
            French = 4
        }

        private sealed class UiTranslationEntry
        {
            public readonly string Source;
            public readonly string Korean;
            public readonly string English;
            public readonly string Chinese;
            public readonly string Japanese;
            public readonly string French;

            public UiTranslationEntry(
                string source,
                string korean,
                string english,
                string chinese,
                string japanese,
                string french)
            {
                Source =
                    source ??
                    string.Empty;

                Korean =
                    korean ??
                    string.Empty;

                English =
                    english ??
                    string.Empty;

                Chinese =
                    chinese ??
                    string.Empty;

                Japanese =
                    japanese ??
                    string.Empty;

                French =
                    french ??
                    string.Empty;
            }

            public string Get(
                HubLanguage language)
            {
                switch (language)
                {
                    case HubLanguage.Korean:
                        return Korean;

                    case HubLanguage.Chinese:
                        return Chinese;

                    case HubLanguage.Japanese:
                        return Japanese;

                    case HubLanguage.French:
                        return French;

                    case HubLanguage.English:
                    default:
                        return English;
                }
            }
        }

        // UI에 출력되는 고정 문구와 상태 메시지를 현재 언어로 변환합니다.
        // 긴 문구가 먼저 처리되도록 길이 순으로 배치해 부분 문자열 충돌을 줄입니다.
        private static readonly UiTranslationEntry[]
            UiTranslationEntries =
            {
            new UiTranslationEntry(
                "모닥불 점화 조건 미충족\nP 메뉴 강화 탭의 다음 모닥불 제작에서 ",
                "모닥불 점화 조건 미충족\nP 메뉴 강화 탭의 다음 모닥불 제작에서 ",
                "Campfire requirements not met\nIn the P menu's Upgrades tab, the level ",
                "未满足篝火点燃条件\n请先在 P 菜单的升级标签中制作第",
                "焚き火の点火条件を満たしていません\nPメニューの強化タブで",
                "Conditions d’allumage du feu de camp non remplies\nDans l’onglet Améliorations du menu P, le niveau "),
            new UiTranslationEntry(
                "모닥불 점화 조건 미충족\nP 메뉴의 부품 탭에서 현재 구간 비행기 부품을 먼저 구매하세요.",
                "모닥불 점화 조건 미충족\nP 메뉴의 부품 탭에서 현재 구간 비행기 부품을 먼저 구매하세요.",
                "Campfire requirements not met\nPurchase the aircraft parts for the current segment from the Parts tab in the P menu first.",
                "未满足篝火点燃条件\n请先在 P 菜单的部件标签中购买当前区域所需的飞机部件。",
                "焚き火の点火条件を満たしていません\nPメニューの部品タブで現在の区間に必要な飛行機部品を先に購入してください。",
                "Conditions d’allumage du feu de camp non remplies\nAchetez d’abord les pièces d’avion du segment actuel dans l’onglet Pièces du menu P."),
            new UiTranslationEntry(
                "최종 조명탄 제작에는 Legendary 제작 등급이 필요합니다.",
                "최종 조명탄 제작에는 전설 제작 등급이 필요합니다.",
                "The Legendary crafting grade is required to craft the final flare.",
                "制作最终信号弹需要传说制作等级。",
                "最終フレアの作成にはレジェンダリークラフト等級が必要です。",
                "Le niveau de fabrication Légendaire est requis pour fabriquer la fusée finale."),
            new UiTranslationEntry(
                "가마 구간은 비행기 부품과 모닥불 진행 대상이 아닙니다.\n정상에 도착한 뒤 P 메뉴의 제작 탭에서 최종 조명탄을 제작하세요.",
                "가마 구간은 비행기 부품과 모닥불 진행 대상이 아닙니다.\n정상에 도착한 뒤 P 메뉴의 제작 탭에서 최종 조명탄을 제작하세요.",
                "The Kiln segment does not use aircraft parts or campfire progression.\nAfter reaching the Peak, craft the final flare from the Crafting tab in the P menu.",
                "熔炉区域不使用飞机部件或篝火推进。\n到达山顶后，在 P 菜单的制作标签中制作最终信号弹。",
                "窯区間では飛行機部品と焚き火進行を使用しません。\n山頂に到達したら、Pメニューのクラフトタブで最終フレアを作成してください。",
                "Le segment Four n’utilise ni pièces d’avion ni progression par feu de camp.\nAprès avoir atteint le Sommet, fabriquez la fusée finale dans l’onglet Fabrication du menu P."),
            new UiTranslationEntry(
                "새 등반의 비행기 부품 상태가 아직 초기화되지 않았습니다.\nP 메뉴의 부품 탭을 열어 진행 상태를 초기화하세요.",
                "새 등반의 비행기 부품 상태가 아직 초기화되지 않았습니다.\nP 메뉴의 부품 탭을 열어 진행 상태를 초기화하세요.",
                "The aircraft-part state for the new climb has not been initialized.\nOpen the Parts tab in the P menu to initialize progression.",
                "新攀登的飞机部件状态尚未初始化。\n打开 P 菜单中的部件标签以初始化进度。",
                "新しい登山の飛行機部品状態がまだ初期化されていません。\nPメニューの部品タブを開いて進行状態を初期化してください。",
                "L’état des pièces d’avion de la nouvelle ascension n’est pas initialisé.\nOuvrez l’onglet Pièces du menu P pour initialiser la progression."),
            new UiTranslationEntry(
                "\n\n가격표\nCommon 1원 · Normal 3원\nRare 7원 · Unique 15원\nLegendary 50원",
                "\n\n가격표\nCommon 1원 · Normal 3원\nRare 7원 · Unique 15원\nLegendary 50원",
                "\n\nPrice list\nCommon 1 coin · Normal 3 coins\nRare 7 coins · Unique 15 coins\nLegendary 50 coins",
                "\n\n价格表\n普通 1 金币 · 标准 3 金币\n稀有 7 金币 · 独特 15 金币\n传说 50 金币",
                "\n\n価格表\nコモン 1コイン · ノーマル 3コイン\nレア 7コイン · ユニーク 15コイン\nレジェンダリー 50コイン",
                "\n\nTarifs\nCommun 1 pièce · Normal 3 pièces\nRare 7 pièces · Unique 15 pièces\nLégendaire 50 pièces"),
            new UiTranslationEntry(
                "부품 상태 저장에 실패했습니다. 공유 돈은 환불되었지만 재료는 복구되지 않았습니다.",
                "부품 상태 저장에 실패했습니다. 공유 돈은 환불되었지만 재료는 복구되지 않았습니다.",
                "Failed to save the part state. Shared money was refunded, but materials were not restored.",
                "保存部件状态失败。共享资金已退还，但材料未恢复。",
                "部品状態の保存に失敗しました。共有資金は返金されましたが、素材は復元されませんでした。",
                "Échec de l’enregistrement de l’état de la pièce. L’argent partagé a été remboursé, mais les matériaux n’ont pas été restaurés."),
            new UiTranslationEntry(
                "구매 완료.\n인벤토리에는 들어가지 않으며 모닥불 진행 조건으로 저장됩니다.",
                "구매 완료.\n인벤토리에는 들어가지 않으며 모닥불 진행 조건으로 저장됩니다.",
                "Purchase complete.\nThe part is stored as a campfire progression requirement and does not enter the inventory.",
                "购买完成。\n部件不会进入背包，而是保存为篝火推进条件。",
                "購入完了。\nインベントリには入らず、焚き火の進行条件として保存されます。",
                "Achat terminé.\nLa pièce est enregistrée comme condition de progression du feu de camp et n’entre pas dans l’inventaire."),
            new UiTranslationEntry(
                "판매 처리 중 인벤토리가 변경되어 요청한 수량을 모두 제거하지 못했습니다.",
                "판매 처리 중 인벤토리가 변경되어 요청한 수량을 모두 제거하지 못했습니다.",
                "The inventory changed during the sale, so the full requested quantity could not be removed.",
                "出售过程中背包发生变化，无法移除全部请求数量。",
                "売却処理中にインベントリが変化し、要求数量をすべて削除できませんでした。",
                "L’inventaire a changé pendant la vente ; toute la quantité demandée n’a pas pu être retirée."),
            new UiTranslationEntry(
                "버튼을 누르면 로컬에서 +100원이 누적된 뒤 묶어서 공유됩니다.",
                "버튼을 누르면 로컬에서 +100원이 누적된 뒤 묶어서 공유됩니다.",
                "Press the button to add +100 shared money.",
                "按下按钮可增加 100 共享资金。",
                "ボタンを押すと共有資金が100増えます。",
                "Appuyez sur le bouton pour ajouter 100 à l’argent partagé."),
            new UiTranslationEntry(
                "현재 구간의 모닥불만 다음 세그먼트 진행에 사용할 수 있습니다.",
                "현재 구간의 모닥불만 다음 세그먼트 진행에 사용할 수 있습니다.",
                "Only the current segment’s campfire can advance to the next segment.",
                "只有当前区域的篝火可用于推进到下一区域。",
                "現在の区間の焚き火のみ次の区間への進行に使用できます。",
                "Seul le feu de camp du segment actuel permet de progresser vers le segment suivant."),
            new UiTranslationEntry(
                "번째 다음 모닥불 제작에는 다음 비행기 모듈 구매가 필요합니다.",
                "번째 다음 모닥불 제작에는 다음 비행기 모듈 구매가 필요합니다.",
                ": crafting the next campfire requires the following aircraft modules.",
                "：制作后续篝火需要以下飞机模块。",
                "番目の次の焚き火作成には以下の飛行機モジュールが必要です。",
                " : la fabrication du prochain feu de camp exige les modules d’avion suivants."),
            new UiTranslationEntry(
                "호스트가 이번 판 제작식과 부품 재료를 확정하는 중입니다...",
                "호스트가 이번 판 제작식과 부품 재료를 확정하는 중입니다...",
                "The host is finalizing this run’s recipes and part materials...",
                "主机正在确定本局的配方和部件材料……",
                "ホストが今回のレシピと部品素材を確定しています...",
                "L’hôte finalise les recettes et les matériaux des pièces de cette partie..."),
            new UiTranslationEntry(
                "정상에서 최종 조명탄 제작 완료. 탈출 신호를 발사했습니다.",
                "정상에서 최종 조명탄 제작 완료. 탈출 신호를 발사했습니다.",
                "Final flare crafted at the Peak. The escape signal has been launched.",
                "已在山顶制作最终信号弹，并发射逃脱信号。",
                "山頂で最終フレアを作成し、脱出信号を発射しました。",
                "Fusée finale fabriquée au Sommet. Le signal d’évacuation a été lancé."),
            new UiTranslationEntry(
                "가마 이후 정상에 도착하면 P → 제작 → 최종 조명탄 제작",
                "가마 이후 정상에 도착하면 P → 제작 → 최종 조명탄 제작",
                "After the Kiln, reach the Peak and choose P → Crafting → Final Flare",
                "熔炉之后到达山顶，选择 P → 制作 → 最终信号弹",
                "窯の後に山頂へ到達し、P → クラフト → 最終フレア",
                "Après le Four, atteignez le Sommet puis P → Fabrication → Fusée finale"),
            new UiTranslationEntry(
                "클릭할 때마다 호스트에게 +100원 요청을 즉시 보냅니다.\n",
                "클릭할 때마다 호스트에게 +100원 요청을 즉시 보냅니다.\n",
                "Each click immediately sends a +100 shared-money request to the host.\n",
                "每次点击都会立即向主机发送增加 100 共享资金的请求。\n",
                "クリックするたびにホストへ共有資金+100のリクエストを即時送信します。\n",
                "Chaque clic envoie immédiatement à l’hôte une demande de +100 d’argent partagé.\n"),
            new UiTranslationEntry(
                "재료 소비 중 인벤토리가 변경되었습니다. 다시 시도하세요.",
                "재료 소비 중 인벤토리가 변경되었습니다. 다시 시도하세요.",
                "The inventory changed while consuming materials. Try again.",
                "消耗材料时背包发生变化，请重试。",
                "素材消費中にインベントリが変化しました。もう一度お試しください。",
                "L’inventaire a changé pendant la consommation des matériaux. Réessayez."),
            new UiTranslationEntry(
                "최종 조명탄 제작과 탈출 신호 발사가 이미 완료되었습니다.",
                "최종 조명탄 제작과 탈출 신호 발사가 이미 완료되었습니다.",
                "The final flare and escape signal have already been completed.",
                "最终信号弹和逃脱信号已完成。",
                "最終フレアの作成と脱出信号の発射は完了済みです。",
                "La fusée finale et le signal d’évacuation ont déjà été réalisés."),
            new UiTranslationEntry(
                "판매 아이템은 제거됐지만 호스트 확인 전송에 실패했습니다.",
                "판매 아이템은 제거됐지만 호스트 확인 전송에 실패했습니다.",
                "The sold item was removed, but the host confirmation failed.",
                "出售物品已移除，但发送主机确认失败。",
                "売却アイテムは削除されましたが、ホスト確認の送信に失敗しました。",
                "L’objet vendu a été retiré, mais l’envoi de la confirmation à l’hôte a échoué."),
            new UiTranslationEntry(
                "현재 구간에서 필요한 비행기 부품만 구매할 수 있습니다.",
                "현재 구간에서 필요한 비행기 부품만 구매할 수 있습니다.",
                "You can only purchase the aircraft part required for the current segment.",
                "只能购买当前区域所需的飞机部件。",
                "現在の区間に必要な飛行機部品のみ購入できます。",
                "Vous ne pouvez acheter que la pièce d’avion requise pour le segment actuel."),
            new UiTranslationEntry(
                "호스트 자신이 누른 경우에도 동일한 호스트 처리 경로를\n",
                "호스트 자신이 누른 경우에도 동일한 호스트 처리 경로를\n",
                "The same host-side processing path is used even when the host clicks it,\n",
                "即使由主机点击，也会使用相同的主机处理流程，\n",
                "ホスト自身が押した場合も同じホスト処理経路を使用するため、\n",
                "Le même traitement côté hôte est utilisé même lorsque l’hôte clique,\n"),
            new UiTranslationEntry(
                "현재 구간, 제작 등급, 재료와 공유 돈을 확인하세요.",
                "현재 구간, 제작 등급, 재료와 공유 돈을 확인하세요.",
                "Check the current segment, crafting grade, materials, and shared money.",
                "请检查当前区域、制作等级、材料和共享资金。",
                "現在の区間、クラフト等級、素材、共有資金を確認してください。",
                "Vérifiez le segment actuel, le niveau de fabrication, les matériaux et l’argent partagé."),
            new UiTranslationEntry(
                "판매 전후 스택 수량이 정확히 1 감소하지 않았습니다.",
                "판매 전후 스택 수량이 정확히 1 감소하지 않았습니다.",
                "The stack count did not decrease by exactly one during the sale.",
                "出售前后堆叠数量未准确减少 1。",
                "売却前後でスタック数が正確に1減少しませんでした。",
                "La pile n’a pas diminué exactement d’une unité pendant la vente."),
            new UiTranslationEntry(
                "호스트는 각 요청을 독립적으로 순서대로 처리합니다.\n\n",
                "호스트는 각 요청을 독립적으로 순서대로 처리합니다.\n\n",
                "The host processes each request independently and in order.\n\n",
                "主机会按顺序独立处理每个请求。\n\n",
                "ホストは各リクエストを独立して順番に処理します。\n\n",
                "L’hôte traite chaque requête séparément et dans l’ordre.\n\n"),
            new UiTranslationEntry(
                "판매 슬롯 또는 아이템 GUID가 올바르지 않습니다.",
                "판매 슬롯 또는 아이템 GUID가 올바르지 않습니다.",
                "The sale slot or item GUID is invalid.",
                "出售栏位或物品 GUID 无效。",
                "売却スロットまたはアイテムGUIDが不正です。",
                "L’emplacement de vente ou le GUID de l’objet est invalide."),
            new UiTranslationEntry(
                "이미 판매 처리 중이거나 판매가 완료된 아이템입니다.",
                "이미 판매 처리 중이거나 판매가 완료된 아이템입니다.",
                "This item is already being sold or has been sold.",
                "该物品正在出售或已出售。",
                "このアイテムは売却処理中、または売却済みです。",
                "Cet objet est déjà en cours de vente ou a été vendu."),
            new UiTranslationEntry(
                "다음 모닥불 재료 소비 중 인벤토리가 변경되었습니다.",
                "다음 모닥불 재료 소비 중 인벤토리가 변경되었습니다.",
                "The inventory changed while consuming next-campfire materials.",
                "消耗后续篝火材料时背包发生变化。",
                "次の焚き火素材を消費中にインベントリが変化しました。",
                "L’inventaire a changé pendant la consommation des matériaux du prochain feu de camp."),
            new UiTranslationEntry(
                "강화 상태 저장에 실패했습니다. 비용을 환불했습니다.",
                "강화 상태 저장에 실패했습니다. 비용을 환불했습니다.",
                "Failed to save the upgrade state. The cost was refunded.",
                "保存升级状态失败，费用已退还。",
                "強化状態の保存に失敗しました。費用は返金されました。",
                "Échec de l’enregistrement de l’amélioration. Le coût a été remboursé."),
            new UiTranslationEntry(
                "최종 공유 돈은 Photon 방 전체에 동기화됩니다.",
                "최종 공유 돈은 Photon 방 전체에 동기화됩니다.",
                "The final shared-money value is synchronized across the Photon room.",
                "最终共享资金会同步到整个 Photon 房间。",
                "最終的な共有資金はPhotonルーム全体に同期されます。",
                "La valeur finale de l’argent partagé est synchronisée dans tout le salon Photon."),
            new UiTranslationEntry(
                "현재 호스트를 찾지 못해 요청을 보내지 못했습니다.",
                "현재 호스트를 찾지 못해 요청을 보내지 못했습니다.",
                "Could not find the host, so the request was not sent.",
                "找不到主机，未能发送请求。",
                "ホストが見つからず、リクエストを送信できませんでした。",
                "L’hôte est introuvable ; la requête n’a pas été envoyée."),
            new UiTranslationEntry(
                "최종 조명탄은 정상 구간에서만 제작할 수 있습니다.",
                "최종 조명탄은 정상 구간에서만 제작할 수 있습니다.",
                "The final flare can only be crafted at the Peak.",
                "最终信号弹只能在山顶制作。",
                "最終フレアは山頂区間でのみ作成できます。",
                "La fusée finale ne peut être fabriquée qu’au Sommet."),
            new UiTranslationEntry(
                "완성품을 받을 빈 슬롯이 없어 제작할 수 없습니다.",
                "완성품을 받을 빈 슬롯이 없어 제작할 수 없습니다.",
                "No empty slot is available for the crafted item.",
                "没有空栏位可接收制作物品。",
                "完成品を受け取る空きスロットがありません。",
                "Aucun emplacement libre n’est disponible pour l’objet fabriqué."),
            new UiTranslationEntry(
                "판매 아이템 제거 요청 데이터가 올바르지 않습니다.",
                "판매 아이템 제거 요청 데이터가 올바르지 않습니다.",
                "The sale-item removal request data is invalid.",
                "出售物品移除请求数据无效。",
                "売却アイテム削除リクエストデータが不正です。",
                "Les données de la demande de retrait de l’objet vendu sont invalides."),
            new UiTranslationEntry(
                "판매 확인 시간이 초과되었습니다. 다시 시도하세요.",
                "판매 확인 시간이 초과되었습니다. 다시 시도하세요.",
                "Sale confirmation timed out. Try again.",
                "出售确认超时，请重试。",
                "売却確認がタイムアウトしました。もう一度お試しください。",
                "La confirmation de vente a expiré. Réessayez."),
            new UiTranslationEntry(
                "현재 세그먼트에 필요한 비행기 부품을 구매하세요.",
                "현재 세그먼트에 필요한 비행기 부품을 구매하세요.",
                "Purchase the aircraft part required for the current segment.",
                "购买当前区域所需的飞机部件。",
                "現在の区間に必要な飛行機部品を購入してください。",
                "Achetez la pièce d’avion requise pour le segment actuel."),
            new UiTranslationEntry(
                "제작 완성품을 인벤토리에 지급하지 못했습니다.\n",
                "제작 완성품을 인벤토리에 지급하지 못했습니다.\n",
                "Could not place the crafted item in the inventory.\n",
                "无法将制作物品放入背包。\n",
                "完成品をインベントリに付与できませんでした。\n",
                "Impossible de placer l’objet fabriqué dans l’inventaire.\n"),
            new UiTranslationEntry(
                "번 슬롯의 아이템은 판매 대상 자원이 아닙니다.",
                "번 슬롯의 아이템은 판매 대상 자원이 아닙니다.",
                " slot does not contain a sellable resource.",
                " 号栏位中的物品不是可出售资源。",
                "番スロットのアイテムは売却対象資源ではありません。",
                " ne contient pas de ressource vendable."),
            new UiTranslationEntry(
                "호스트 인벤토리 처리 상태가 올바르지 않습니다.",
                "호스트 인벤토리 처리 상태가 올바르지 않습니다.",
                "The host inventory transaction state is invalid.",
                "主机背包处理状态无效。",
                "ホストのインベントリ処理状態が不正です。",
                "L’état de traitement de l’inventaire de l’hôte est invalide."),
            new UiTranslationEntry(
                "판매 요청 수량이 현재 보유 수량보다 많습니다.",
                "판매 요청 수량이 현재 보유 수량보다 많습니다.",
                "The requested sale quantity exceeds the amount owned.",
                "请求出售的数量超过当前持有量。",
                "要求された売却数量が現在の所持数を超えています。",
                "La quantité demandée dépasse la quantité possédée."),
            new UiTranslationEntry(
                "판매 승인 후 아이템 GUID가 변경되었습니다.",
                "판매 승인 후 아이템 GUID가 변경되었습니다.",
                "The item GUID changed after sale approval.",
                "出售批准后物品 GUID 发生变化。",
                "売却承認後にアイテムGUIDが変更されました。",
                "Le GUID de l’objet a changé après l’approbation de la vente."),
            new UiTranslationEntry(
                "현재 Photon 방에 입장해 있지 않습니다.",
                "현재 Photon 방에 입장해 있지 않습니다.",
                "You are not currently in a Photon room.",
                "当前未加入 Photon 房间。",
                "現在 Photon ルームに参加していません。",
                "Vous n’êtes actuellement dans aucun salon Photon."),
            new UiTranslationEntry(
                "이 구간의 비행기 부품은 이미 사용되었습니다.",
                "이 구간의 비행기 부품은 이미 사용되었습니다.",
                "The aircraft part for this segment has already been consumed.",
                "本区域的飞机部件已使用。",
                "この区間の飛行機部品はすでに使用済みです。",
                "La pièce d’avion de ce segment a déjà été utilisée."),
            new UiTranslationEntry(
                "선택한 슬롯에는 판매 가능한 자원이 없습니다.",
                "선택한 슬롯에는 판매 가능한 자원이 없습니다.",
                "The selected slot contains no sellable resource.",
                "所选栏位中没有可出售资源。",
                "選択したスロットに売却可能な資源がありません。",
                "L’emplacement sélectionné ne contient aucune ressource vendable."),
            new UiTranslationEntry(
                "판매 승인 전에 아이템 정보가 변경되었습니다.",
                "판매 승인 전에 아이템 정보가 변경되었습니다.",
                "The item data changed before sale approval.",
                "出售批准前物品信息发生变化。",
                "売却承認前にアイテム情報が変更されました。",
                "Les données de l’objet ont changé avant l’approbation de la vente."),
            new UiTranslationEntry(
                "판매 수량을 인벤토리에서 제거하지 못했습니다.",
                "판매 수량을 인벤토리에서 제거하지 못했습니다.",
                "Could not remove the sale quantity from the inventory.",
                "无法从背包中移除出售数量。",
                "売却数量をインベントリから削除できませんでした。",
                "Impossible de retirer la quantité vendue de l’inventaire."),
            new UiTranslationEntry(
                "판매 직전에 아이템 GUID가 변경되었습니다.",
                "판매 직전에 아이템 GUID가 변경되었습니다.",
                "The item GUID changed immediately before the sale.",
                "出售前物品 GUID 发生变化。",
                "売却直前にアイテムGUIDが変更されました。",
                "Le GUID de l’objet a changé juste avant la vente."),
            new UiTranslationEntry(
                "판매 승인 후 슬롯의 아이템이 변경되었습니다.",
                "판매 승인 후 슬롯의 아이템이 변경되었습니다.",
                "The slot item changed after sale approval.",
                "出售批准后栏位中的物品发生变化。",
                "売却承認後にスロットのアイテムが変更されました。",
                "L’objet de l’emplacement a changé après l’approbation de la vente."),
            new UiTranslationEntry(
                "판매 확인을 전달할 호스트를 찾지 못했습니다.",
                "판매 확인을 전달할 호스트를 찾지 못했습니다.",
                "Could not find the host to send the sale confirmation.",
                "找不到用于发送出售确认的主机。",
                "売却確認を送るホストが見つかりません。",
                "L’hôte auquel envoyer la confirmation de vente est introuvable."),
            new UiTranslationEntry(
                "공유 돈 +100 요청 전송에 실패했습니다.",
                "공유 돈 +100 요청 전송에 실패했습니다.",
                "Failed to send the +100 shared-money request.",
                "发送增加 100 共享资金的请求失败。",
                "共有資金+100のリクエスト送信に失敗しました。",
                "Échec de l’envoi de la demande de +100 d’argent partagé."),
            new UiTranslationEntry(
                "비행기 부품 구매 요청 전송에 실패했습니다.",
                "비행기 부품 구매 요청 전송에 실패했습니다.",
                "Failed to send the aircraft-part purchase request.",
                "发送飞机部件购买请求失败。",
                "飛行機部品購入リクエストの送信に失敗しました。",
                "Échec de l’envoi de la demande d’achat de pièce d’avion."),
            new UiTranslationEntry(
                "돈은 환불됐지만 재료는 복구되지 않았습니다.",
                "돈은 환불됐지만 재료는 복구되지 않았습니다.",
                "Money was refunded, but materials were not restored.",
                "资金已退还，但材料未恢复。",
                "お金は返金されましたが、素材は復元されませんでした。",
                "L’argent a été remboursé, mais les matériaux n’ont pas été restaurés."),
            new UiTranslationEntry(
                "판매 직전에 슬롯의 아이템이 변경되었습니다.",
                "판매 직전에 슬롯의 아이템이 변경되었습니다.",
                "The slot item changed immediately before the sale.",
                "出售前栏位中的物品发生变化。",
                "売却直前にスロットのアイテムが変更されました。",
                "L’objet de l’emplacement a changé juste avant la vente."),
            new UiTranslationEntry(
                "CraftHub가 아직 준비되지 않았습니다.",
                "CraftHub가 아직 준비되지 않았습니다.",
                "CraftHub is not ready yet.",
                "CraftHub 尚未准备好。",
                "CraftHubの準備がまだできていません。",
                "CraftHub n’est pas encore prêt."),
            new UiTranslationEntry(
                "직접 실행하므로 요청이 누락되지 않습니다.\n",
                "직접 실행하므로 요청이 누락되지 않습니다.\n",
                "so requests are not lost.\n",
                "因此请求不会丢失。\n",
                "リクエストが失われることはありません。\n",
                "ce qui évite toute perte de requête.\n"),
            new UiTranslationEntry(
                "호스트의 처리 결과를 확인하지 못했습니다.",
                "호스트의 처리 결과를 확인하지 못했습니다.",
                "Could not verify the host’s result.",
                "无法确认主机的处理结果。",
                "ホストの処理結果を確認できませんでした。",
                "Impossible de vérifier le résultat de l’hôte."),
            new UiTranslationEntry(
                "이미 구매했거나 사용한 비행기 부품입니다.",
                "이미 구매했거나 사용한 비행기 부품입니다.",
                "This aircraft part has already been purchased or consumed.",
                "该飞机部件已购买或使用。",
                "この飛行機部品は購入済み、または使用済みです。",
                "Cette pièce d’avion a déjà été achetée ou utilisée."),
            new UiTranslationEntry(
                "Legendary 제작 등급이 필요합니다.",
                "Legendary 제작 등급이 필요합니다.",
                "Legendary crafting grade is required.",
                "需要 Legendary 制作等级。",
                "Legendaryクラフト等級が必要です。",
                "Le niveau de fabrication Legendary est requis."),
            new UiTranslationEntry(
                "제작 성공! 추가 손 슬롯에 장착했습니다.",
                "제작 성공! 추가 손 슬롯에 장착했습니다.",
                "crafted! Equipped in the extra hand slot.",
                "制作成功！已装备到额外手持栏位。",
                "作成成功！追加の手スロットに装備しました。",
                "fabriqué ! Équipé dans l’emplacement de main supplémentaire."),
            new UiTranslationEntry(
                "개발자 요청 데이터가 올바르지 않습니다.",
                "개발자 요청 데이터가 올바르지 않습니다.",
                "The developer request data is invalid.",
                "开发者请求数据无效。",
                "開発者リクエストデータが不正です。",
                "Les données de la requête développeur sont invalides."),
            new UiTranslationEntry(
                "호스트 결과 데이터가 올바르지 않습니다.",
                "호스트 결과 데이터가 올바르지 않습니다.",
                "The host result data is invalid.",
                "主机结果数据无效。",
                "ホスト結果データが不正です。",
                "Les données du résultat de l’hôte sont invalides."),
            new UiTranslationEntry(
                "비행기 부품 구매 요청이 너무 빠릅니다.",
                "비행기 부품 구매 요청이 너무 빠릅니다.",
                "Aircraft-part purchase requests are being sent too quickly.",
                "飞机部件购买请求过于频繁。",
                "飛行機部品購入リクエストが早すぎます。",
                "Les demandes d’achat de pièce d’avion sont trop rapides."),
            new UiTranslationEntry(
                "비행기 부품 번호를 해석하지 못했습니다.",
                "비행기 부품 번호를 해석하지 못했습니다.",
                "Could not read the aircraft-part number.",
                "无法解析飞机部件编号。",
                "飛行機部品番号を解析できませんでした。",
                "Impossible d’interpréter le numéro de la pièce d’avion."),
            new UiTranslationEntry(
                "제작 아이템 번호를 해석하지 못했습니다.",
                "제작 아이템 번호를 해석하지 못했습니다.",
                "Could not read the crafted-item number.",
                "无法解析制作物品编号。",
                "クラフトアイテム番号を解析できませんでした。",
                "Impossible d’interpréter le numéro de l’objet à fabriquer."),
            new UiTranslationEntry(
                "제작 데이터베이스가 준비되지 않았습니다.",
                "제작 데이터베이스가 준비되지 않았습니다.",
                "The crafting database is not ready.",
                "制作数据库尚未准备好。",
                "クラフトデータベースの準備ができていません。",
                "La base de données de fabrication n’est pas prête."),
            new UiTranslationEntry(
                "선택한 인벤토리 슬롯을 찾지 못했습니다.",
                "선택한 인벤토리 슬롯을 찾지 못했습니다.",
                "Could not find the selected inventory slot.",
                "找不到所选背包栏位。",
                "選択したインベントリスロットが見つかりません。",
                "Emplacement d’inventaire sélectionné introuvable."),
            new UiTranslationEntry(
                "이 아이템은 판매 대상 자원이 아닙니다.",
                "이 아이템은 판매 대상 자원이 아닙니다.",
                "This item is not a sellable resource.",
                "此物品不是可出售资源。",
                "このアイテムは売却対象資源ではありません。",
                "Cet objet n’est pas une ressource vendable."),
            new UiTranslationEntry(
                "판매 요청 데이터를 해석하지 못했습니다.",
                "판매 요청 데이터를 해석하지 못했습니다.",
                "Could not read the sell request data.",
                "无法解析出售请求数据。",
                "売却リクエストデータを解析できませんでした。",
                "Impossible d’interpréter les données de la demande de vente."),
            new UiTranslationEntry(
                "판매 요청 플레이어를 찾을 수 없습니다.",
                "판매 요청 플레이어를 찾을 수 없습니다.",
                "Could not find the player who requested the sale.",
                "找不到发起出售请求的玩家。",
                "売却を要求したプレイヤーが見つかりません。",
                "Le joueur ayant demandé la vente est introuvable."),
            new UiTranslationEntry(
                "판매 가격이 설정되지 않은 아이템입니다.",
                "판매 가격이 설정되지 않은 아이템입니다.",
                "No sale price is configured for this item.",
                "此物品未设置售价。",
                "このアイテムには売却価格が設定されていません。",
                "Aucun prix de vente n’est défini pour cet objet."),
            new UiTranslationEntry(
                "보유 수량보다 많이 판매할 수 없습니다.",
                "보유 수량보다 많이 판매할 수 없습니다.",
                "You cannot sell more than the quantity owned.",
                "出售数量不能超过持有数量。",
                "所持数を超えて売却することはできません。",
                "Vous ne pouvez pas vendre plus que la quantité possédée."),
            new UiTranslationEntry(
                "판매 트랜잭션 정보가 올바르지 않습니다.",
                "판매 트랜잭션 정보가 올바르지 않습니다.",
                "The sale transaction data is invalid.",
                "出售交易信息无效。",
                "売却トランザクション情報が不正です。",
                "Les informations de la transaction de vente sont invalides."),
            new UiTranslationEntry(
                "판매자 로컬 인벤토리를 찾지 못했습니다.",
                "판매자 로컬 인벤토리를 찾지 못했습니다.",
                "Could not find the seller’s local inventory.",
                "找不到卖家的本地背包。",
                "売却者のローカルインベントリが見つかりません。",
                "Inventaire local du vendeur introuvable."),
            new UiTranslationEntry(
                "판매 결과 데이터가 올바르지 않습니다.",
                "판매 결과 데이터가 올바르지 않습니다.",
                "The sell result data is invalid.",
                "出售结果数据无效。",
                "売却結果データが不正です。",
                "Les données du résultat de vente sont invalides."),
            new UiTranslationEntry(
                "제작 결과 데이터가 올바르지 않습니다.",
                "제작 결과 데이터가 올바르지 않습니다.",
                "The craft result data is invalid.",
                "制作结果数据无效。",
                "クラフト結果データが不正です。",
                "Les données du résultat de fabrication sont invalides."),
            new UiTranslationEntry(
                "강화 결과 데이터가 올바르지 않습니다.",
                "강화 결과 데이터가 올바르지 않습니다.",
                "The upgrade result data is invalid.",
                "升级结果数据无效。",
                "強化結果データが不正です。",
                "Les données du résultat d’amélioration sont invalides."),
            new UiTranslationEntry(
                "공유 돈 또는 제작 재료가 부족합니다.",
                "공유 돈 또는 제작 재료가 부족합니다.",
                "Not enough shared money or crafting materials.",
                "共享资金或制作材料不足。",
                "共有資金またはクラフト素材が不足しています。",
                "L’argent partagé ou les matériaux de fabrication sont insuffisants."),
            new UiTranslationEntry(
                "번 슬롯을 판매 대상으로 선택했습니다.",
                "번 슬롯을 판매 대상으로 선택했습니다.",
                " slot selected for sale.",
                " 号栏位已选为出售目标。",
                "番スロットを売却対象に選択しました。",
                " sélectionné pour la vente."),
            new UiTranslationEntry(
                "강화 상태를 아직 불러오지 못했습니다.",
                "강화 상태를 아직 불러오지 못했습니다.",
                "The upgrade state has not loaded yet.",
                "升级状态尚未加载。",
                "強化状態がまだ読み込まれていません。",
                "L’état des améliorations n’est pas encore chargé."),
            new UiTranslationEntry(
                "모든 다음 모닥불을 이미 제작했습니다.",
                "모든 다음 모닥불을 이미 제작했습니다.",
                "All next campfires have already been crafted.",
                "所有后续篝火均已制作。",
                "すべての次の焚き火は作成済みです。",
                "Tous les prochains feux de camp ont déjà été fabriqués."),
            new UiTranslationEntry(
                "Purple Mushroom Berry",
                "보라색 버섯열매",
                "Purple Mushroom Berry",
                "紫色蘑菇莓",
                "紫のマッシュルームベリー",
                "Baie-champignon violette"),
            new UiTranslationEntry(
                "제작품 지급 중 오류가 발생했습니다.",
                "제작품 지급 중 오류가 발생했습니다.",
                "An error occurred while delivering the crafted item.",
                "发放制作物品时发生错误。",
                "クラフト品の付与中にエラーが発生しました。",
                "Une erreur est survenue lors de la remise de l’objet fabriqué."),
            new UiTranslationEntry(
                "잘못된 비행기 부품 구매 요청입니다.",
                "잘못된 비행기 부품 구매 요청입니다.",
                "Invalid aircraft-part purchase request.",
                "飞机部件购买请求无效。",
                "飛行機部品購入リクエストが不正です。",
                "Demande d’achat de pièce d’avion invalide."),
            new UiTranslationEntry(
                "비행기 부품 구매 결과를 받았습니다.",
                "비행기 부품 구매 결과를 받았습니다.",
                "Aircraft-part purchase result received.",
                "已收到飞机部件购买结果。",
                "飛行機部品購入結果を受信しました。",
                "Résultat de l’achat de pièce d’avion reçu."),
            new UiTranslationEntry(
                "플레이어 인벤토리를 찾지 못했습니다.",
                "플레이어 인벤토리를 찾지 못했습니다.",
                "Could not find the player inventory.",
                "找不到玩家背包。",
                "プレイヤーのインベントリが見つかりません。",
                "Inventaire du joueur introuvable."),
            new UiTranslationEntry(
                "판매할 인벤토리 슬롯을 선택하세요.",
                "판매할 인벤토리 슬롯을 선택하세요.",
                "Select an inventory slot to sell.",
                "请选择要出售的背包栏位。",
                "売却するインベントリスロットを選択してください。",
                "Sélectionnez un emplacement d’inventaire à vendre."),
            new UiTranslationEntry(
                "허용되지 않은 공유 돈 요청입니다.",
                "허용되지 않은 공유 돈 요청입니다.",
                "This shared-money request is not allowed.",
                "不允许此共享资金请求。",
                "この共有資金リクエストは許可されていません。",
                "Cette demande d’argent partagé n’est pas autorisée."),
            new UiTranslationEntry(
                "공유 돈 요청 처리에 실패했습니다.",
                "공유 돈 요청 처리에 실패했습니다.",
                "Failed to process the shared-money request.",
                "处理共享资金请求失败。",
                "共有資金リクエストの処理に失敗しました。",
                "Échec du traitement de la demande d’argent partagé."),
            new UiTranslationEntry(
                "판매 아이템을 제거하지 못했습니다.",
                "판매 아이템을 제거하지 못했습니다.",
                "Could not remove the sold item.",
                "无法移除出售物品。",
                "売却アイテムを削除できませんでした。",
                "Impossible de retirer l’objet vendu."),
            new UiTranslationEntry(
                "강화 상태를 초기화하지 못했습니다.",
                "강화 상태를 초기화하지 못했습니다.",
                "Could not initialize the upgrade state.",
                "无法初始化升级状态。",
                "強化状態を初期化できませんでした。",
                "Impossible d’initialiser l’état des améliorations."),
            new UiTranslationEntry(
                "개 판매 요청을 처리 중입니다...",
                "개 판매 요청을 처리 중입니다...",
                " units are being processed for sale...",
                "个出售请求正在处理……",
                "個の売却リクエストを処理中です...",
                " unités : demande de vente en cours..."),
            new UiTranslationEntry(
                "Blue Mushroom Berry",
                "파란색 버섯열매",
                "Blue Mushroom Berry",
                "蓝色蘑菇莓",
                "青いマッシュルームベリー",
                "Baie-champignon bleue"),
            new UiTranslationEntry(
                "판매 결과를 해석하지 못했습니다.",
                "판매 결과를 해석하지 못했습니다.",
                "Could not read the sell result.",
                "无法解析出售结果。",
                "売却結果を解析できませんでした。",
                "Impossible d’interpréter le résultat de vente."),
            new UiTranslationEntry(
                "제작 결과를 해석하지 못했습니다.",
                "제작 결과를 해석하지 못했습니다.",
                "Could not read the craft result.",
                "无法解析制作结果。",
                "クラフト結果を解析できませんでした。",
                "Impossible d’interpréter le résultat de fabrication."),
            new UiTranslationEntry(
                "구매할 비행기 부품을 선택하세요.",
                "구매할 비행기 부품을 선택하세요.",
                "Select an aircraft part to purchase.",
                "请选择要购买的飞机部件。",
                "購入する飛行機部品を選択してください。",
                "Sélectionnez une pièce d’avion à acheter."),
            new UiTranslationEntry(
                "존재하지 않는 비행기 부품입니다.",
                "존재하지 않는 비행기 부품입니다.",
                "That aircraft part does not exist.",
                "该飞机部件不存在。",
                "存在しない飛行機部品です。",
                "Cette pièce d’avion n’existe pas."),
            new UiTranslationEntry(
                "판매할 아이템이 슬롯에 없습니다.",
                "판매할 아이템이 슬롯에 없습니다.",
                "The item to sell is not in the slot.",
                "要出售的物品不在栏位中。",
                "売却するアイテムがスロットにありません。",
                "L’objet à vendre n’est pas dans l’emplacement."),
            new UiTranslationEntry(
                "판매 아이템 1개를 제거했습니다.",
                "판매 아이템 1개를 제거했습니다.",
                "Removed one sold item.",
                "已移除 1 个出售物品。",
                "売却アイテムを1個削除しました。",
                "Un objet vendu a été retiré."),
            new UiTranslationEntry(
                "강화 종류를 해석하지 못했습니다.",
                "강화 종류를 해석하지 못했습니다.",
                "Could not read the upgrade type.",
                "无法解析升级类型。",
                "強化種類を解析できませんでした。",
                "Impossible d’interpréter le type d’amélioration."),
            new UiTranslationEntry(
                "현재 판매 수익: 기본 판매가 x",
                "현재 판매 수익: 기본 판매가 x",
                "Current sale value: base price x",
                "当前出售收益：基础售价 x",
                "現在の売却利益: 基本価格 x",
                "Valeur de vente actuelle : prix de base x"),
            new UiTranslationEntry(
                "공유 돈 +100 요청 전송 완료",
                "공유 돈 +100 요청 전송 완료",
                "Shared-money +100 request sent",
                "已发送增加 100 共享资金的请求",
                "共有資金+100リクエスト送信完了",
                "Demande de +100 d’argent partagé envoyée"),
            new UiTranslationEntry(
                "개발자 치트: 공유 돈 +100원",
                "개발자 치트: 공유 돈 +100원",
                "Developer cheat: shared money +100 coins",
                "开发者作弊：共享资金 +100 金币",
                "開発者チート: 共有資金+100コイン",
                "Triche développeur : argent partagé +100 pièces"),
            new UiTranslationEntry(
                "Orange Winterberry",
                "주황 겨울열매",
                "Orange Winterberry",
                "橙色冬莓",
                "オレンジ・ウィンターベリー",
                "Baie d’hiver orange"),
            new UiTranslationEntry(
                "Red Mushroom Berry",
                "빨간색 버섯 열매",
                "Red Mushroom Berry",
                "红色蘑菇莓",
                "赤いマッシュルームベリー",
                "Baie-champignon rouge"),
            new UiTranslationEntry(
                "구매 완료 · 모닥불 점화 가능",
                "구매 완료 · 모닥불 점화 가능",
                "Purchased · Campfire can be lit",
                "已购买 · 可点燃篝火",
                "購入済み・焚き火を点火可能",
                "Acheté · Feu de camp allumable"),
            new UiTranslationEntry(
                "제작 요청 전송에 실패했습니다.",
                "제작 요청 전송에 실패했습니다.",
                "Failed to send the craft request.",
                "发送制作请求失败。",
                "クラフトリクエストの送信に失敗しました。",
                "Échec de l’envoi de la demande de fabrication."),
            new UiTranslationEntry(
                "Shift + 휠: 5개씩 조절",
                "Shift + 휠: 5개씩 조절",
                "Shift + wheel: adjust by 5",
                "Shift + 滚轮：每次调整 5",
                "Shift + ホイール: 5個ずつ調整",
                "Maj + molette : ajuster de 5"),
            new UiTranslationEntry(
                "판매 요청 전송에 실패했습니다.",
                "판매 요청 전송에 실패했습니다.",
                "Failed to send the sell request.",
                "发送出售请求失败。",
                "売却リクエストの送信に失敗しました。",
                "Échec de l’envoi de la demande de vente."),
            new UiTranslationEntry(
                "강화 요청 전송에 실패했습니다.",
                "강화 요청 전송에 실패했습니다.",
                "Failed to send the upgrade request.",
                "发送升级请求失败。",
                "強化リクエストの送信に失敗しました。",
                "Échec de l’envoi de la demande d’amélioration."),
            new UiTranslationEntry(
                "단계 모닥불을 먼저 제작하세요.",
                "단계 모닥불을 먼저 제작하세요.",
                " campfire must be crafted first.",
                "级篝火。",
                "段階の焚き火を先に作成してください。",
                " du feu de camp doit d’abord être fabriqué."),
            new UiTranslationEntry(
                "Scoutmaster Bugle",
                "스카우트지도자의 나팔",
                "Scoutmaster Bugle",
                "童军领队号角",
                "スカウトマスターのラッパ",
                "Clairon du chef scout"),
            new UiTranslationEntry(
                "열대/뿌리숲 → 메사/고산지대",
                "열대/뿌리숲 → 메사/고산지대",
                "Tropics/Roots → Mesa/Alpine",
                "热带/根系森林 → 台地/高山",
                "熱帯/根の森 → メサ/高山",
                "Tropiques/Forêt de racines → Mesa/Alpin"),
            new UiTranslationEntry(
                "선택한 슬롯이 비어 있습니다.",
                "선택한 슬롯이 비어 있습니다.",
                "The selected slot is empty.",
                "所选栏位为空。",
                "選択したスロットは空です。",
                "L’emplacement sélectionné est vide."),
            new UiTranslationEntry(
                "마우스 휠 ↑↓: 1개씩 조절",
                "마우스 휠 ↑↓: 1개씩 조절",
                "Mouse wheel ↑↓: adjust by 1",
                "鼠标滚轮 ↑↓：每次调整 1",
                "マウスホイール ↑↓: 1個ずつ調整",
                "Molette ↑↓ : ajuster de 1"),
            new UiTranslationEntry(
                "Anti-Rope Cannon",
                "반전 밧줄총",
                "Anti-Rope Cannon",
                "反向绳索炮",
                "反転ロープキャノン",
                "Canon à corde inversé"),
            new UiTranslationEntry(
                "Friendship Bugle",
                "우정 나팔",
                "Friendship Bugle",
                "友谊号角",
                "友情のラッパ",
                "Clairon de l’amitié"),
            new UiTranslationEntry(
                "Golden Bing Bong",
                "황금 빙봉",
                "Golden Bing Bong",
                "黄金冰棒",
                "ゴールデン・ビン・ボン",
                "Bing Bong doré"),
            new UiTranslationEntry(
                "Yellow Berrynana",
                "노란색 열매나나",
                "Yellow Berrynana",
                "黄色莓蕉",
                "黄色いベリーナナ",
                "Berrynana jaune"),
            new UiTranslationEntry(
                "Red Clusterberry",
                "빨간 송송열매",
                "Red Clusterberry",
                "红色簇莓",
                "赤いクラスターベリー",
                "Baie en grappe rouge"),
            new UiTranslationEntry(
                "Trumpet Mushroom",
                "나팔버섯",
                "Trumpet Mushroom",
                "喇叭蘑菇",
                "ラッパキノコ",
                "Champignon trompette"),
            new UiTranslationEntry(
                "제작할 아이템을 선택하세요.",
                "제작할 아이템을 선택하세요.",
                "Select an item to craft.",
                "请选择要制作的物品。",
                "クラフトするアイテムを選択してください。",
                "Sélectionnez un objet à fabriquer."),
            new UiTranslationEntry(
                "요청 시간이 초과되었습니다.",
                "요청 시간이 초과되었습니다.",
                "The request timed out.",
                "请求超时。",
                "リクエストがタイムアウトしました。",
                "La requête a expiré."),
            new UiTranslationEntry(
                "다른 요청을 처리 중입니다.",
                "다른 요청을 처리 중입니다.",
                "Another request is being processed.",
                "正在处理其他请求。",
                "別のリクエストを処理中です。",
                "Une autre requête est en cours."),
            new UiTranslationEntry(
                "제작 요청이 거부되었습니다.",
                "제작 요청이 거부되었습니다.",
                "The craft request was rejected.",
                "制作请求被拒绝。",
                "クラフト要求が拒否されました。",
                "La demande de fabrication a été refusée."),
            new UiTranslationEntry(
                "현재는 제작할 수 없습니다.",
                "현재는 제작할 수 없습니다.",
                "Crafting is not currently available.",
                "当前无法制作。",
                "現在はクラフトできません。",
                "La fabrication n’est pas disponible actuellement."),
            new UiTranslationEntry(
                "제작 요청이 너무 빠릅니다.",
                "제작 요청이 너무 빠릅니다.",
                "Craft requests are being sent too quickly.",
                "制作请求过于频繁。",
                "クラフトリクエストが早すぎます。",
                "Les demandes de fabrication sont trop rapides."),
            new UiTranslationEntry(
                "등록되지 않은 제작식입니다.",
                "등록되지 않은 제작식입니다.",
                "This recipe is not registered.",
                "该配方未注册。",
                "未登録のレシピです。",
                "Cette recette n’est pas enregistrée."),
            new UiTranslationEntry(
                "플레이어를 찾지 못했습니다.",
                "플레이어를 찾지 못했습니다.",
                "Could not find the player.",
                "找不到玩家。",
                "プレイヤーが見つかりません。",
                "Joueur introuvable."),
            new UiTranslationEntry(
                "판매 요청이 너무 빠릅니다.",
                "판매 요청이 너무 빠릅니다.",
                "Sell requests are being sent too quickly.",
                "出售请求过于频繁。",
                "売却リクエストが早すぎます。",
                "Les demandes de vente sont trop rapides."),
            new UiTranslationEntry(
                "현재는 강화할 수 없습니다.",
                "현재는 강화할 수 없습니다.",
                "Upgrades are not currently available.",
                "当前无法升级。",
                "現在は強化できません。",
                "Les améliorations ne sont pas disponibles actuellement."),
            new UiTranslationEntry(
                "강화 요청이 너무 빠릅니다.",
                "강화 요청이 너무 빠릅니다.",
                "Upgrade requests are being sent too quickly.",
                "升级请求过于频繁。",
                "強化リクエストが早すぎます。",
                "Les demandes d’amélioration sont trop rapides."),
            new UiTranslationEntry(
                "모든 다음 모닥불 제작 완료",
                "모든 다음 모닥불 제작 완료",
                "All next campfires completed",
                "所有后续篝火制作完成",
                "すべての次の焚き火を作成完了",
                "Tous les prochains feux de camp sont terminés"),
            new UiTranslationEntry(
                "<아이템 데이터 확인 필요>",
                "<아이템 데이터 확인 필요>",
                "<Item data required>",
                "<需要物品数据>",
                "<アイテムデータ要確認>",
                "<Données d’objet requises>"),
            new UiTranslationEntry(
                "제작 단계 | 다음 모닥불 ",
                "제작 단계 | 다음 모닥불 ",
                "crafting level | next campfire ",
                "制作等级 | 后续篝火 ",
                "クラフト段階 | 次の焚き火 ",
                "niveau de fabrication | prochain feu de camp "),
            new UiTranslationEntry(
                "Checkpoint Flag",
                "체크포인트 깃발",
                "Checkpoint Flag",
                "检查点旗帜",
                "チェックポイント旗",
                "Drapeau de point de contrôle"),
            new UiTranslationEntry(
                "Anti-Rope Spool",
                "반전 밧줄타래",
                "Anti-Rope Spool",
                "反向绳索卷",
                "反転ロープスプール",
                "Bobine de corde inversée"),
            new UiTranslationEntry(
                "Green Kingberry",
                "녹색 대왕열매",
                "Green Kingberry",
                "绿色王莓",
                "緑のキングベリー",
                "Baie royale verte"),
            new UiTranslationEntry(
                "Bundle Mushroom",
                "다발버섯",
                "Bundle Mushroom",
                "束状蘑菇",
                "束キノコ",
                "Champignon en bouquet"),
            new UiTranslationEntry(
                "Button Mushroom",
                "단추버섯",
                "Button Mushroom",
                "纽扣蘑菇",
                "ボタンキノコ",
                "Champignon bouton"),
            new UiTranslationEntry(
                "Honeycomb Honey",
                "벌집꿀",
                "Honeycomb Honey",
                "蜂巢蜜",
                "巣蜜",
                "Miel en rayon"),
            new UiTranslationEntry(
                "이전 모닥불에서 사용 완료",
                "이전 모닥불에서 사용 완료",
                "Consumed at the previous campfire",
                "已在前一个篝火使用",
                "前の焚き火で使用済み",
                "Utilisé au feu de camp précédent"),
            new UiTranslationEntry(
                "잘못된 제작 아이템입니다.",
                "잘못된 제작 아이템입니다.",
                "Invalid crafted item.",
                "制作物品无效。",
                "不正なクラフトアイテムです。",
                "Objet à fabriquer invalide."),
            new UiTranslationEntry(
                "번 슬롯은 비어 있습니다.",
                "번 슬롯은 비어 있습니다.",
                " slot is empty.",
                " 号栏位为空。",
                "番スロットは空です。",
                " est vide."),
            new UiTranslationEntry(
                "존재하지 않는 강화입니다.",
                "존재하지 않는 강화입니다.",
                "That upgrade does not exist.",
                "该升级不存在。",
                "存在しない強化です。",
                "Cette amélioration n’existe pas."),
            new UiTranslationEntry(
                "최대 단계에 도달했습니다.",
                "최대 단계에 도달했습니다.",
                "Maximum level reached.",
                "已达到最高等级。",
                "最大レベルに到達しました。",
                "Niveau maximal atteint."),
            new UiTranslationEntry(
                "Portable Stove",
                "휴대용 스토브",
                "Portable Stove",
                "便携炉",
                "携帯ストーブ",
                "Réchaud portable"),
            new UiTranslationEntry(
                "Chain Launcher",
                "사슬발사기",
                "Chain Launcher",
                "链条发射器",
                "チェーンランチャー",
                "Lance-chaîne"),
            new UiTranslationEntry(
                "Pirate Compass",
                "해적 나침반",
                "Pirate Compass",
                "海盗罗盘",
                "海賊のコンパス",
                "Boussole de pirate"),
            new UiTranslationEntry(
                "Red Crispberry",
                "빨간색 아삭 열매",
                "Red Crispberry",
                "红色脆莓",
                "赤いクリスプベリー",
                "Baie croquante rouge"),
            new UiTranslationEntry(
                "Fortified Milk",
                "강화우유",
                "Fortified Milk",
                "强化牛奶",
                "強化ミルク",
                "Lait fortifié"),
            new UiTranslationEntry(
                "Red Thornberry",
                "빨간 가시열매",
                "Red Thornberry",
                "红色刺莓",
                "赤いソーンベリー",
                "Baie épineuse rouge"),
            new UiTranslationEntry(
                "강화 항목을 선택하세요.",
                "강화 항목을 선택하세요.",
                "Select an upgrade.",
                "请选择升级项目。",
                "強化項目を選択してください。",
                "Sélectionnez une amélioration."),
            new UiTranslationEntry(
                "진행 조건을 확인하세요.",
                "진행 조건을 확인하세요.",
                "Check the progression requirements.",
                "请检查推进条件。",
                "進行条件を確認してください。",
                "Vérifiez les conditions de progression."),
            new UiTranslationEntry(
                "강화 결과를 받았습니다.",
                "강화 결과를 받았습니다.",
                "Upgrade result received.",
                "已收到升级结果。",
                "強化結果を受信しました。",
                "Résultat de l’amélioration reçu."),
            new UiTranslationEntry(
                "메사/고산지대 → 칼데라",
                "메사/고산지대 → 칼데라",
                "Mesa/Alpine → Caldera",
                "台地/高山 → 火山口",
                "メサ/高山 → カルデラ",
                "Mesa/Alpin → Caldeira"),
            new UiTranslationEntry(
                "아직 도달하지 않은 구간",
                "아직 도달하지 않은 구간",
                "Segment not reached yet",
                "尚未到达的区域",
                "未到達の区間",
                "Segment pas encore atteint"),
            new UiTranslationEntry(
                "모닥불 점화 조건 미충족",
                "모닥불 점화 조건 미충족",
                "Campfire requirements not met",
                "未满足篝火点燃条件",
                "焚き火の点火条件を満たしていません",
                "Conditions d’allumage du feu de camp non remplies"),
            new UiTranslationEntry(
                "제작 목록을 표시합니다.",
                "제작 목록을 표시합니다.",
                " crafting list displayed.",
                " 制作列表已显示。",
                " クラフト一覧を表示します。",
                " : liste de fabrication affichée."),
            new UiTranslationEntry(
                "제작 등급이 필요합니다.",
                "제작 등급이 필요합니다.",
                "crafting grade is required.",
                "需要制作等级。",
                "クラフト等級が必要です。",
                "un niveau de fabrication est requis."),
            new UiTranslationEntry(
                "잘못된 제작 요청입니다.",
                "잘못된 제작 요청입니다.",
                "Invalid craft request.",
                "制作请求无效。",
                "クラフトリクエストが不正です。",
                "Demande de fabrication invalide."),
            new UiTranslationEntry(
                "잘못된 판매 요청입니다.",
                "잘못된 판매 요청입니다.",
                "Invalid sell request.",
                "出售请求无效。",
                "売却リクエストが不正です。",
                "Demande de vente invalide."),
            new UiTranslationEntry(
                "자원 등급이 필요합니다.",
                "자원 등급이 필요합니다.",
                "resource grade is required.",
                "需要资源等级。",
                "資源等級が必要です。",
                "un niveau de ressource est requis."),
            new UiTranslationEntry(
                "잘못된 강화 요청입니다.",
                "잘못된 강화 요청입니다.",
                "Invalid upgrade request.",
                "升级请求无效。",
                "強化リクエストが不正です。",
                "Demande d’amélioration invalide."),
            new UiTranslationEntry(
                "구매를 요청했습니다...",
                "구매를 요청했습니다...",
                "purchase requested...",
                "已请求购买……",
                "の購入をリクエストしました...",
                ": achat demandé..."),
            new UiTranslationEntry(
                "제작을 요청했습니다...",
                "제작을 요청했습니다...",
                "craft requested...",
                "已请求制作……",
                "のクラフトをリクエストしました...",
                ": fabrication demandée..."),
            new UiTranslationEntry(
                "강화를 요청했습니다...",
                "강화를 요청했습니다...",
                "upgrade requested...",
                "已请求升级……",
                "の強化をリクエストしました...",
                ": amélioration demandée..."),
            new UiTranslationEntry(
                "Climbing Gear",
                "등산 장비",
                "Climbing Gear",
                "攀登装备",
                "登山装備",
                "Équipement d’escalade"),
            new UiTranslationEntry(
                "Utility Items",
                "기타 아이템",
                "Utility Items",
                "其他物品",
                "その他のアイテム",
                "Objets utilitaires"),
            new UiTranslationEntry(
                "Bounce Fungus",
                "방방 균류",
                "Bounce Fungus",
                "弹跳菌",
                "バウンドキノコ",
                "Champignon rebondissant"),
            new UiTranslationEntry(
                "Balloon Bunch",
                "풍선 다발",
                "Balloon Bunch",
                "气球束",
                "風船の束",
                "Bouquet de ballons"),
            new UiTranslationEntry(
                "Book of Bones",
                "뼈의서",
                "Book of Bones",
                "骨之书",
                "骨の書",
                "Livre des os"),
            new UiTranslationEntry(
                "Rainbow Candy",
                "무지개사탕",
                "Rainbow Candy",
                "彩虹糖",
                "レインボーキャンディ",
                "Bonbon arc-en-ciel"),
            new UiTranslationEntry(
                "Fairy Lantern",
                "요정랜턴",
                "Fairy Lantern",
                "仙女灯笼",
                "妖精のランタン",
                "Lanterne féerique"),
            new UiTranslationEntry(
                "Puff Mushroom",
                "통통버섯",
                "Puff Mushroom",
                "膨膨蘑菇",
                "パフキノコ",
                "Champignon gonflé"),
            new UiTranslationEntry(
                "Pandora’s Box",
                "판도라의 상자",
                "Pandora’s Box",
                "潘多拉魔盒",
                "パンドラの箱",
                "Boîte de Pandore"),
            new UiTranslationEntry(
                "First Aid Kit",
                "구급상자",
                "First Aid Kit",
                "急救箱",
                "救急箱",
                "Trousse de secours"),
            new UiTranslationEntry(
                "공유 돈이 부족합니다.",
                "공유 돈이 부족합니다.",
                "Not enough shared money.",
                "共享资金不足。",
                "共有資金が不足しています。",
                "L’argent partagé est insuffisant."),
            new UiTranslationEntry(
                "부활 / 스카우트 인형",
                "부활 / 스카우트 인형",
                "Revive / Scout Effigy",
                "复活 / 童军雕像",
                "蘇生 / スカウト像",
                "Résurrection / Effigie scout"),
            new UiTranslationEntry(
                "음식 / 녹색 대왕열매",
                "음식 / 녹색 대왕열매",
                "Food / Green Kingberry",
                "食物 / 绿色王莓",
                "食料 / グリーン・キングベリー",
                "Nourriture / Baie royale verte"),
            new UiTranslationEntry(
                "제작식을 선택했습니다.",
                "제작식을 선택했습니다.",
                " recipe selected.",
                " 已选择配方。",
                " レシピを選択しました。",
                " : recette sélectionnée."),
            new UiTranslationEntry(
                "이(가) 부족합니다. ",
                "이(가) 부족합니다. ",
                "is insufficient. ",
                "不足。",
                "が不足しています。",
                "est insuffisant. "),
            new UiTranslationEntry(
                "이미 최대 단계입니다.",
                "이미 최대 단계입니다.",
                "Already at maximum level.",
                "已达到最高等级。",
                "すでに最大レベルです。",
                "Le niveau maximal est déjà atteint."),
            new UiTranslationEntry(
                "완성된 다음 모닥불: ",
                "완성된 다음 모닥불: ",
                "Completed next campfires: ",
                "已完成的后续篝火：",
                "完成した次の焚き火: ",
                "Prochains feux de camp terminés : "),
            new UiTranslationEntry(
                "을(를) 선택했습니다.",
                "을(를) 선택했습니다.",
                " selected.",
                " 已选择。",
                "を選択しました。",
                " sélectionné."),
            new UiTranslationEntry(
                " 1개 판매 완료: +",
                " 1개 판매 완료: +",
                " sold 1 unit: +",
                " 出售 1 个：+",
                "を1個売却: +",
                " vendu, 1 unité : +"),
            new UiTranslationEntry(
                "개발자 테스트 치트\n\n",
                "개발자 테스트 치트\n\n",
                "Developer test cheat\n\n",
                "开发者测试作弊\n\n",
                "開発者テストチート\n\n",
                "Triche de test développeur\n\n"),
            new UiTranslationEntry(
                "Final Escape",
                "최종 탈출",
                "Final Escape",
                "最终逃脱",
                "最終脱出",
                "Évasion finale"),
            new UiTranslationEntry(
                "Weird Shroom",
                "괴상 버섯",
                "Weird Shroom",
                "怪异蘑菇",
                "奇妙なキノコ",
                "Champignon étrange"),
            new UiTranslationEntry(
                "Energy Drink",
                "에너지 드링크",
                "Energy Drink",
                "能量饮料",
                "エナジードリンク",
                "Boisson énergisante"),
            new UiTranslationEntry(
                "Shelf Fungus",
                "선반 균류",
                "Shelf Fungus",
                "层孔菌",
                "棚キノコ",
                "Champignon en console"),
            new UiTranslationEntry(
                "Cloud Fungus",
                "구름균류",
                "Cloud Fungus",
                "云菌",
                "雲キノコ",
                "Champignon nuage"),
            new UiTranslationEntry(
                "Scout Cannon",
                "스카우트 캐논",
                "Scout Cannon",
                "童军大炮",
                "スカウトキャノン",
                "Canon scout"),
            new UiTranslationEntry(
                "Cursed Skull",
                "저주받은 해골",
                "Cursed Skull",
                "诅咒头骨",
                "呪われた頭蓋骨",
                "Crâne maudit"),
            new UiTranslationEntry(
                "Coconut Half",
                "코코넛 반쪽",
                "Coconut Half",
                "半个椰子",
                "ココナッツ半分",
                "Demi-noix de coco"),
            new UiTranslationEntry(
                "Sports Drink",
                "스포츠 드링크",
                "Sports Drink",
                "运动饮料",
                "スポーツドリンク",
                "Boisson sportive"),
            new UiTranslationEntry(
                "Airline Food",
                "기내식",
                "Airline Food",
                "飞机餐",
                "機内食",
                "Repas d’avion"),
            new UiTranslationEntry(
                "Scout Cookie",
                "스카우트 과자",
                "Scout Cookie",
                "童军饼干",
                "スカウトクッキー",
                "Biscuit scout"),
            new UiTranslationEntry(
                "Scout Effigy",
                "스카우트 인형",
                "Scout Effigy",
                "童军雕像",
                "スカウト像",
                "Effigie scout"),
            new UiTranslationEntry(
                "판매하지 못했습니다.",
                "판매하지 못했습니다.",
                "Sale failed.",
                "出售失败。",
                "売却に失敗しました。",
                "Échec de la vente."),
            new UiTranslationEntry(
                "제작에 성공했습니다.",
                "제작에 성공했습니다.",
                "Crafting succeeded.",
                "制作成功。",
                "クラフトに成功しました。",
                "Fabrication réussie."),
            new UiTranslationEntry(
                "첫 번째 다음 모닥불",
                "첫 번째 다음 모닥불",
                "First next campfire",
                "第一个后续篝火",
                "1つ目の次の焚き火",
                "Premier prochain feu de camp"),
            new UiTranslationEntry(
                "두 번째 다음 모닥불",
                "두 번째 다음 모닥불",
                "Second next campfire",
                "第二个后续篝火",
                "2つ目の次の焚き火",
                "Deuxième prochain feu de camp"),
            new UiTranslationEntry(
                "세 번째 다음 모닥불",
                "세 번째 다음 모닥불",
                "Third next campfire",
                "第三个后续篝火",
                "3つ目の次の焚き火",
                "Troisième prochain feu de camp"),
            new UiTranslationEntry(
                "네 번째 다음 모닥불",
                "네 번째 다음 모닥불",
                "Fourth next campfire",
                "第四个后续篝火",
                "4つ目の次の焚き火",
                "Quatrième prochain feu de camp"),
            new UiTranslationEntry(
                "해안 → 열대/뿌리숲",
                "해안 → 열대/뿌리숲",
                "Beach → Tropics/Roots",
                "海滩 → 热带/根系森林",
                "海岸 → 熱帯/根の森",
                "Plage → Tropiques/Forêt de racines"),
            new UiTranslationEntry(
                "제작식을 선택하세요.",
                "제작식을 선택하세요.",
                "Select a recipe.",
                "请选择配方。",
                "レシピを選択してください。",
                "Sélectionnez une recette."),
            new UiTranslationEntry(
                "제작에 성공했습니다!",
                "제작에 성공했습니다!",
                "crafted successfully!",
                "制作成功！",
                "クラフトに成功しました！",
                "fabriqué avec succès !"),
            new UiTranslationEntry(
                "항목을 선택했습니다.",
                "항목을 선택했습니다.",
                " selected.",
                " 已选择。",
                "を選択しました。",
                " sélectionné."),
            new UiTranslationEntry(
                "현재 최대 적재량: ",
                "현재 최대 적재량: ",
                "Current maximum stack: ",
                "当前最大堆叠：",
                "現在の最大スタック: ",
                "Pile maximale actuelle : "),
            new UiTranslationEntry(
                "이번 판 무작위 조합",
                "이번 판 무작위 조합",
                "This run’s randomized combination",
                "本局随机组合",
                "今回のランダム組み合わせ",
                "Combinaison aléatoire de cette partie"),
            new UiTranslationEntry(
                "스카우트지도자의 나팔",
                "스카우트지도자의 나팔",
                "Scoutmaster Bugle",
                "童军领队号角",
                "スカウトマスターのラッパ",
                "Clairon du chef scout"),
            new UiTranslationEntry(
                "\n처리 대기 요청: ",
                "\n처리 대기 요청: ",
                "\nPending requests: ",
                "\n待处理请求：",
                "\n処理待ちリクエスト: ",
                "\nRequêtes en attente : "),
            new UiTranslationEntry(
                "원이 반영되었습니다.",
                "원이 반영되었습니다.",
                " coins applied.",
                " 金币已应用。",
                "コインが反映されました。",
                " pièces ajoutées."),
            new UiTranslationEntry(
                "Description",
                "설명",
                "Description",
                "说明",
                "説明",
                "Description"),
            new UiTranslationEntry(
                "Strange Gem",
                "이상한 보석",
                "Strange Gem",
                "奇异宝石",
                "不思議な宝石",
                "Gemme étrange"),
            new UiTranslationEntry(
                "Rescue Hook",
                "구조갈고리",
                "Rescue Hook",
                "救援钩",
                "レスキューフック",
                "Crochet de secours"),
            new UiTranslationEntry(
                "Rope Cannon",
                "밧줄총",
                "Rope Cannon",
                "绳索炮",
                "ロープキャノン",
                "Canon à corde"),
            new UiTranslationEntry(
                "Marshmallow",
                "마시멜로우",
                "Marshmallow",
                "棉花糖",
                "マシュマロ",
                "Guimauve"),
            new UiTranslationEntry(
                "Granola Bar",
                "그래놀라바",
                "Granola Bar",
                "燕麦棒",
                "グラノーラバー",
                "Barre de granola"),
            new UiTranslationEntry(
                "Cooked Bird",
                "요리된 새",
                "Cooked Bird",
                "烤鸟",
                "調理済みの鳥",
                "Oiseau cuit"),
            new UiTranslationEntry(
                "Sleep Berry",
                "수면 열매",
                "Sleep Berry",
                "睡眠莓",
                "睡眠ベリー",
                "Baie du sommeil"),
            new UiTranslationEntry(
                "P / ESC\n닫기",
                "P / ESC\n닫기",
                "P / ESC\nClose",
                "P / ESC\n关闭",
                "P / ESC\n閉じる",
                "P / ESC\nFermer"),
            new UiTranslationEntry(
                "필요 제작 등급: ",
                "필요 제작 등급: ",
                "Required crafting grade: ",
                "所需制作等级：",
                "必要クラフト等級: ",
                "Niveau de fabrication requis : "),
            new UiTranslationEntry(
                "음식 / 강화 우유",
                "음식 / 강화 우유",
                "Food / Fortified Milk",
                "食物 / 强化牛奶",
                "食料 / 強化ミルク",
                "Nourriture / Lait fortifié"),
            new UiTranslationEntry(
                "현재 해금 등급: ",
                "현재 해금 등급: ",
                "Current unlocked grade: ",
                "当前解锁等级：",
                "現在の解放等級: ",
                "Niveau débloqué actuel : "),
            new UiTranslationEntry(
                "맵 자원 수집량 x",
                "맵 자원 수집량 x",
                "map resource yield x",
                "地图资源采集量 x",
                "マップ資源収集量 x",
                "rendement des ressources de la carte x"),
            new UiTranslationEntry(
                "개로 설정했습니다.",
                "개로 설정했습니다.",
                " units.",
                " 个。",
                "個に設定しました。",
                " unités."),
            new UiTranslationEntry(
                "\n현재 공유 돈: ",
                "\n현재 공유 돈: ",
                "\nCurrent shared money: ",
                "\n当前共享资金：",
                "\n現在の共有資金: ",
                "\nArgent partagé actuel : "),
            new UiTranslationEntry(
                " | 비행기 부품 ",
                " | 비행기 부품 ",
                " | aircraft part ",
                " | 飞机部件 ",
                " | 飛行機部品 ",
                " | pièce d’avion "),
            new UiTranslationEntry(
                "개 판매 완료: +",
                "개 판매 완료: +",
                " units sold: +",
                " 个出售完成：+",
                "個売却完了: +",
                " unités vendues : +"),
            new UiTranslationEntry(
                "Essentials",
                "필수",
                "Essentials",
                "必需品",
                "必需品",
                "Essentiels"),
            new UiTranslationEntry(
                "Binoculars",
                "망원경",
                "Binoculars",
                "双筒望远镜",
                "双眼鏡",
                "Jumelles"),
            new UiTranslationEntry(
                "Rope Spool",
                "밧줄타래",
                "Rope Spool",
                "绳索卷",
                "ロープスプール",
                "Bobine de corde"),
            new UiTranslationEntry(
                "Magic Bean",
                "마법의 콩",
                "Magic Bean",
                "魔法豆",
                "魔法の豆",
                "Haricot magique"),
            new UiTranslationEntry(
                "공유 돈 +100",
                "공유 돈 +100",
                "Shared money +100",
                "共享资金 +100",
                "共有資金 +100",
                "Argent partagé +100"),
            new UiTranslationEntry(
                "알 수 없는 모듈",
                "알 수 없는 모듈",
                "Unknown module",
                "未知模块",
                "不明なモジュール",
                "Module inconnu"),
            new UiTranslationEntry(
                "필요 비행기 모듈",
                "필요 비행기 모듈",
                "Required aircraft modules",
                "所需飞机模块",
                "必要な飛行機モジュール",
                "Modules d’avion requis"),
            new UiTranslationEntry(
                "다음 모닥불 제작",
                "다음 모닥불 제작",
                "Next Campfire",
                "后续篝火",
                "次の焚き火",
                "Prochain feu de camp"),
            new UiTranslationEntry(
                "아이템 판매 수익",
                "아이템 판매 수익",
                "Sale Value",
                "出售收益",
                "売却利益",
                "Valeur de vente"),
            new UiTranslationEntry(
                "알 수 없는 강화",
                "알 수 없는 강화",
                "Unknown Upgrade",
                "未知升级",
                "不明な強化",
                "Amélioration inconnue"),
            new UiTranslationEntry(
                "현재 수집량: x",
                "현재 수집량: x",
                "Current gather yield: x",
                "当前采集倍率：x",
                "現在の収集量: x",
                "Rendement actuel : x"),
            new UiTranslationEntry(
                "빨간색 아삭 열매",
                "빨간색 아삭 열매",
                "Red Crispberry",
                "红色脆莓",
                "赤いクリスプベリー",
                "Baie croquante rouge"),
            new UiTranslationEntry(
                "빨간색 버섯 열매",
                "빨간색 버섯 열매",
                "Red Mushroom Berry",
                "红色蘑菇莓",
                "赤いマッシュルームベリー",
                "Baie-champignon rouge"),
            new UiTranslationEntry(
                "Developer",
                "개발자",
                "Developer",
                "开发者",
                "開発者",
                "Développeur"),
            new UiTranslationEntry(
                "Bing Bong",
                "빙봉",
                "Bing Bong",
                "冰棒",
                "ビン・ボン",
                "Bing Bong"),
            new UiTranslationEntry(
                "Guidebook",
                "가이드북",
                "Guidebook",
                "指南书",
                "ガイドブック",
                "Guide"),
            new UiTranslationEntry(
                "Aloe Vera",
                "알로에 베라",
                "Aloe Vera",
                "芦荟",
                "アロエベラ",
                "Aloe vera"),
            new UiTranslationEntry(
                "Heat Pack",
                "핫팩",
                "Heat Pack",
                "暖宝宝",
                "カイロ",
                "Chaufferette"),
            new UiTranslationEntry(
                "Sunscreen",
                "선크림",
                "Sunscreen",
                "防晒霜",
                "日焼け止め",
                "Crème solaire"),
            new UiTranslationEntry(
                "Trail Mix",
                "트레일 믹스",
                "Trail Mix",
                "什锦坚果",
                "トレイルミックス",
                "Mélange montagnard"),
            new UiTranslationEntry(
                "Legendary",
                "전설",
                "Legendary",
                "传说",
                "レジェンダリー",
                "Légendaire"),
            new UiTranslationEntry(
                "Language",
                "언어",
                "Language",
                "语言",
                "言語",
                "Langue"),
            new UiTranslationEntry(
                "연료 제어 모듈",
                "연료 제어 모듈",
                "Fuel Control Module",
                "燃料控制模块",
                "燃料制御モジュール",
                "Module de contrôle du carburant"),
            new UiTranslationEntry(
                "날개 연결 모듈",
                "날개 연결 모듈",
                "Wing Coupling Module",
                "机翼连接模块",
                "翼接続モジュール",
                "Module de raccordement des ailes"),
            new UiTranslationEntry(
                "고도 조절 모듈",
                "고도 조절 모듈",
                "Altitude Control Module",
                "高度控制模块",
                "高度調整モジュール",
                "Module de contrôle d’altitude"),
            new UiTranslationEntry(
                "내열 추진 모듈",
                "내열 추진 모듈",
                "Heat-Resistant Propulsion Module",
                "耐热推进模块",
                "耐熱推進モジュール",
                "Module de propulsion résistant à la chaleur"),
            new UiTranslationEntry(
                "칼데라 → 가마",
                "칼데라 → 가마",
                "Caldera → Kiln",
                "火山口 → 熔炉",
                "カルデラ → 窯",
                "Caldeira → Four"),
            new UiTranslationEntry(
                "이미 지난 구간",
                "이미 지난 구간",
                "Segment already passed",
                "已经通过的区域",
                "通過済みの区間",
                "Segment déjà franchi"),
            new UiTranslationEntry(
                "선택 아이템: ",
                "선택 아이템: ",
                "Selected item: ",
                "所选物品：",
                "選択アイテム: ",
                "Objet sélectionné : "),
            new UiTranslationEntry(
                "개당 판매가: ",
                "개당 판매가: ",
                "Price per unit: ",
                "单价：",
                "1個あたりの売却価格: ",
                "Prix unitaire : "),
            new UiTranslationEntry(
                "예상 판매액: ",
                "예상 판매액: ",
                "Expected proceeds: ",
                "预计收入：",
                "予想売却額: ",
                "Produit estimé : "),
            new UiTranslationEntry(
                "인벤토리 적재량",
                "인벤토리 적재량",
                "Inventory Capacity",
                "背包容量",
                "インベントリ容量",
                "Capacité d’inventaire"),
            new UiTranslationEntry(
                "기본 판매가 x",
                "기본 판매가 x",
                "base sale price x",
                "基础售价 x",
                "基本売却価格 x",
                "prix de vente de base x"),
            new UiTranslationEntry(
                "체크포인트 깃발",
                "체크포인트 깃발",
                "Checkpoint Flag",
                "检查点旗帜",
                "チェックポイント旗",
                "Drapeau de point de contrôle"),
            new UiTranslationEntry(
                "노란색 열매나나",
                "노란색 열매나나",
                "Yellow Berrynana",
                "黄色莓蕉",
                "黄色いベリーナナ",
                "Berrynana jaune"),
            new UiTranslationEntry(
                "파란색 버섯열매",
                "파란색 버섯열매",
                "Blue Mushroom Berry",
                "蓝色蘑菇莓",
                "青いマッシュルームベリー",
                "Baie-champignon bleue"),
            new UiTranslationEntry(
                "보라색 버섯열매",
                "보라색 버섯열매",
                "Purple Mushroom Berry",
                "紫色蘑菇莓",
                "紫のマッシュルームベリー",
                "Baie-champignon violette"),
            new UiTranslationEntry(
                "\n남은 수량: ",
                "\n남은 수량: ",
                "\nRemaining quantity: ",
                "\n剩余数量：",
                "\n残り数量: ",
                "\nQuantité restante : "),
            new UiTranslationEntry(
                "필요 제작 등급",
                "필요 제작 등급",
                "Required crafting grade",
                "所需制作等级",
                "必要クラフト等級",
                "Niveau de fabrication requis"),
            new UiTranslationEntry(
                "Upgrades",
                "강화",
                "Upgrades",
                "升级",
                "強化",
                "Améliorations"),
            new UiTranslationEntry(
                "Crafting",
                "제작",
                "Crafting",
                "制作",
                "クラフト",
                "Fabrication"),
            new UiTranslationEntry(
                "Climbing",
                "등산",
                "Climbing",
                "攀登",
                "登山",
                "Escalade"),
            new UiTranslationEntry(
                "Backpack",
                "배낭",
                "Backpack",
                "背包",
                "バックパック",
                "Sac à dos"),
            new UiTranslationEntry(
                "Antidote",
                "해독제",
                "Antidote",
                "解毒剂",
                "解毒剤",
                "Antidote"),
            new UiTranslationEntry(
                "Dynamite",
                "다이너마이트",
                "Dynamite",
                "炸药",
                "ダイナマイト",
                "Dynamite"),
            new UiTranslationEntry(
                "Cure-All",
                "만병통치약",
                "Cure-All",
                "万能药",
                "万能薬",
                "Remède universel"),
            new UiTranslationEntry(
                "공유 잔액: ",
                "공유 잔액: ",
                "Shared balance: ",
                "共享余额：",
                "共有残高: ",
                "Solde partagé : "),
            new UiTranslationEntry(
                "처리 중...",
                "처리 중...",
                "Processing...",
                "处理中……",
                "処理中...",
                "Traitement..."),
            new UiTranslationEntry(
                "판매했습니다.",
                "판매했습니다.",
                "Sold successfully.",
                "出售成功。",
                "売却しました。",
                "Vente réussie."),
            new UiTranslationEntry(
                "진행 조건: ",
                "진행 조건: ",
                "Progression requirements: ",
                "推进条件：",
                "進行条件: ",
                "Conditions de progression : "),
            new UiTranslationEntry(
                "판매 수량: ",
                "판매 수량: ",
                "Quantity to sell: ",
                "出售数量：",
                "売却数量: ",
                "Quantité à vendre : "),
            new UiTranslationEntry(
                "다음 효과: ",
                "다음 효과: ",
                "Next effect: ",
                "下一效果：",
                "次の効果: ",
                "Effet suivant : "),
            new UiTranslationEntry(
                "다음 제작: ",
                "다음 제작: ",
                "Next craft: ",
                "下一制作：",
                "次の作成: ",
                "Prochaine fabrication : "),
            new UiTranslationEntry(
                "필요 등급: ",
                "필요 등급: ",
                "Required grade: ",
                "所需等级：",
                "必要等級: ",
                "Niveau requis : "),
            new UiTranslationEntry(
                "<이름 없음>",
                "<이름 없음>",
                "<Unnamed>",
                "<无名称>",
                "<名前なし>",
                "<Sans nom>"),
            new UiTranslationEntry(
                "플라잉 디스크",
                "플라잉 디스크",
                "Frisbee",
                "飞盘",
                "フリスビー",
                "Frisbee"),
            new UiTranslationEntry(
                "에너지 드링크",
                "에너지 드링크",
                "Energy Drink",
                "能量饮料",
                "エナジードリンク",
                "Boisson énergisante"),
            new UiTranslationEntry(
                "휴대용 스토브",
                "휴대용 스토브",
                "Portable Stove",
                "便携炉",
                "携帯ストーブ",
                "Réchaud portable"),
            new UiTranslationEntry(
                "반전 밧줄타래",
                "반전 밧줄타래",
                "Anti-Rope Spool",
                "反向绳索卷",
                "反転ロープスプール",
                "Bobine de corde inversée"),
            new UiTranslationEntry(
                "스카우트 캐논",
                "스카우트 캐논",
                "Scout Cannon",
                "童军大炮",
                "スカウトキャノン",
                "Canon scout"),
            new UiTranslationEntry(
                "저주받은 해골",
                "저주받은 해골",
                "Cursed Skull",
                "诅咒头骨",
                "呪われた頭蓋骨",
                "Crâne maudit"),
            new UiTranslationEntry(
                "스포츠 드링크",
                "스포츠 드링크",
                "Sports Drink",
                "运动饮料",
                "スポーツドリンク",
                "Boisson sportive"),
            new UiTranslationEntry(
                "빨간 송송열매",
                "빨간 송송열매",
                "Red Clusterberry",
                "红色簇莓",
                "赤いクラスターベリー",
                "Baie en grappe rouge"),
            new UiTranslationEntry(
                "녹색 대왕열매",
                "녹색 대왕열매",
                "Green Kingberry",
                "绿色王莓",
                "緑のキングベリー",
                "Baie royale verte"),
            new UiTranslationEntry(
                "주황 겨울열매",
                "주황 겨울열매",
                "Orange Winterberry",
                "橙色冬莓",
                "オレンジ・ウィンターベリー",
                "Baie d’hiver orange"),
            new UiTranslationEntry(
                "빨간 가시열매",
                "빨간 가시열매",
                "Red Thornberry",
                "红色刺莓",
                "赤いソーンベリー",
                "Baie épineuse rouge"),
            new UiTranslationEntry(
                "스카우트 과자",
                "스카우트 과자",
                "Scout Cookie",
                "童军饼干",
                "スカウトクッキー",
                "Biscuit scout"),
            new UiTranslationEntry(
                "판도라의 상자",
                "판도라의 상자",
                "Pandora’s Box",
                "潘多拉魔盒",
                "パンドラの箱",
                "Boîte de Pandore"),
            new UiTranslationEntry(
                "스카우트 인형",
                "스카우트 인형",
                "Scout Effigy",
                "童军雕像",
                "スカウト像",
                "Effigie scout"),
            new UiTranslationEntry(
                "판매 수량을 ",
                "판매 수량을 ",
                "Sale quantity set to ",
                "出售数量设为 ",
                "売却数量を ",
                "Quantité de vente réglée sur "),
            new UiTranslationEntry(
                " · 페이지 ",
                " · 페이지 ",
                " · Page ",
                " · 第 ",
                " · ページ ",
                " · Page "),
            new UiTranslationEntry(
                "Healing",
                "회복",
                "Healing",
                "治疗",
                "回復",
                "Soins"),
            new UiTranslationEntry(
                "Frisbee",
                "플라잉디스크",
                "Frisbee",
                "飞盘",
                "フリスビー",
                "Frisbee"),
            new UiTranslationEntry(
                "Balloon",
                "풍선",
                "Balloon",
                "气球",
                "風船",
                "Ballon"),
            new UiTranslationEntry(
                "Lantern",
                "랜턴",
                "Lantern",
                "灯笼",
                "ランタン",
                "Lanterne"),
            new UiTranslationEntry(
                "Parasol",
                "파라솔",
                "Parasol",
                "阳伞",
                "パラソル",
                "Parasol"),
            new UiTranslationEntry(
                "Hot Dog",
                "핫도그",
                "Hot Dog",
                "热狗",
                "ホットドッグ",
                "Hot-dog"),
            new UiTranslationEntry(
                "Pop Pop",
                "뾱뾱이",
                "Pop Pop",
                "泡泡纸",
                "プチプチ",
                "Papier bulle"),
            new UiTranslationEntry(
                "Bandage",
                "붕대",
                "Bandage",
                "绷带",
                "包帯",
                "Bandage"),
            new UiTranslationEntry(
                "기타 아이템",
                "기타 아이템",
                "Utility Items",
                "其他物品",
                "その他のアイテム",
                "Objets utilitaires"),
            new UiTranslationEntry(
                "수집량 배율",
                "수집량 배율",
                "Gather Yield",
                "采集倍率",
                "収集倍率",
                "Rendement de collecte"),
            new UiTranslationEntry(
                "알 수 없음",
                "알 수 없음",
                "Unknown",
                "未知",
                "不明",
                "Inconnu"),
            new UiTranslationEntry(
                "플라잉디스크",
                "플라잉디스크",
                "Frisbee",
                "飞盘",
                "フリスビー",
                "Frisbee"),
            new UiTranslationEntry(
                "이상한 보석",
                "이상한 보석",
                "Strange Gem",
                "奇异宝石",
                "不思議な宝石",
                "Gemme étrange"),
            new UiTranslationEntry(
                "반전 밧줄총",
                "반전 밧줄총",
                "Anti-Rope Cannon",
                "反向绳索炮",
                "反転ロープキャノン",
                "Canon à corde inversé"),
            new UiTranslationEntry(
                "해적 나침반",
                "해적 나침반",
                "Pirate Compass",
                "海盗罗盘",
                "海賊のコンパス",
                "Boussole de pirate"),
            new UiTranslationEntry(
                "알로에 베라",
                "알로에 베라",
                "Aloe Vera",
                "芦荟",
                "アロエベラ",
                "Aloe vera"),
            new UiTranslationEntry(
                "다이너마이트",
                "다이너마이트",
                "Dynamite",
                "炸药",
                "ダイナマイト",
                "Dynamite"),
            new UiTranslationEntry(
                "코코넛 반쪽",
                "코코넛 반쪽",
                "Coconut Half",
                "半个椰子",
                "ココナッツ半分",
                "Demi-noix de coco"),
            new UiTranslationEntry(
                "트레일 믹스",
                "트레일 믹스",
                "Trail Mix",
                "什锦坚果",
                "トレイルミックス",
                "Mélange montagnard"),
            new UiTranslationEntry(
                "공유 돈 +",
                "공유 돈 +",
                "Shared money +",
                "共享资金 +",
                "共有資金 +",
                "Argent partagé +"),
            new UiTranslationEntry(
                "강화 완료\n",
                "강화 완료\n",
                "Upgrade complete\n",
                "升级完成\n",
                "強化完了\n",
                "Amélioration terminée\n"),
            new UiTranslationEntry(
                "Revive",
                "부활",
                "Revive",
                "复活",
                "蘇生",
                "Résurrection"),
            new UiTranslationEntry(
                "Scroll",
                "스크롤",
                "Scroll",
                "卷轴",
                "巻物",
                "Parchemin"),
            new UiTranslationEntry(
                "Cactus",
                "선인장",
                "Cactus",
                "仙人掌",
                "サボテン",
                "Cactus"),
            new UiTranslationEntry(
                "Unique",
                "고유",
                "Unique",
                "独特",
                "ユニーク",
                "Unique"),
            new UiTranslationEntry(
                "Normal",
                "보통",
                "Normal",
                "标准",
                "ノーマル",
                "Normal"),
            new UiTranslationEntry(
                "Common",
                "일반",
                "Common",
                "普通",
                "コモン",
                "Commun"),
            new UiTranslationEntry(
                "제작 시도",
                "제작 시도",
                "Craft",
                "制作",
                "クラフト",
                "Fabriquer"),
            new UiTranslationEntry(
                "부품 구매",
                "부품 구매",
                "Purchase part",
                "购买部件",
                "部品を購入",
                "Acheter la pièce"),
            new UiTranslationEntry(
                "최대 단계",
                "최대 단계",
                "Maximum level",
                "最高等级",
                "最大レベル",
                "Niveau maximal"),
            new UiTranslationEntry(
                "판매 불가",
                "판매 불가",
                "Cannot sell",
                "不可出售",
                "売却不可",
                "Invendable"),
            new UiTranslationEntry(
                "비어 있음",
                "비어 있음",
                "Empty",
                "空",
                "空",
                "Vide"),
            new UiTranslationEntry(
                "구매 완료",
                "구매 완료",
                "Purchased",
                "已购买",
                "購入済み",
                "Acheté"),
            new UiTranslationEntry(
                "사용 완료",
                "사용 완료",
                "Consumed",
                "已使用",
                "使用済み",
                "Utilisé"),
            new UiTranslationEntry(
                "공유 돈 ",
                "공유 돈 ",
                "Shared money ",
                "共享资金 ",
                "共有資金 ",
                "Argent partagé "),
            new UiTranslationEntry(
                "구매 가능",
                "구매 가능",
                "Available to purchase",
                "可购买",
                "購入可能",
                "Disponible à l’achat"),
            new UiTranslationEntry(
                "제작 완료",
                "제작 완료",
                "Crafted",
                "已制作",
                "作成済み",
                "Fabriqué"),
            new UiTranslationEntry(
                "등산 장비",
                "등산 장비",
                "Climbing Gear",
                "攀登装备",
                "登山装備",
                "Équipement d’escalade"),
            new UiTranslationEntry(
                "강화 우유",
                "강화 우유",
                "Fortified Milk",
                "强化牛奶",
                "強化ミルク",
                "Lait fortifié"),
            new UiTranslationEntry(
                "최종 탈출",
                "최종 탈출",
                "Final Escape",
                "最终逃脱",
                "最終脱出",
                "Évasion finale"),
            new UiTranslationEntry(
                "강화 완료",
                "강화 완료",
                "Upgrade complete",
                "升级完成",
                "強化完了",
                "Amélioration terminée"),
            new UiTranslationEntry(
                "자원 등급",
                "자원 등급",
                "Resource Grade",
                "资源等级",
                "資源等級",
                "Niveau des ressources"),
            new UiTranslationEntry(
                "등급 해금",
                "등급 해금",
                "grade unlocked",
                "等级解锁",
                "等級解放",
                "niveau débloqué"),
            new UiTranslationEntry(
                "괴상 버섯",
                "괴상 버섯",
                "Weird Shroom",
                "怪异蘑菇",
                "奇妙なキノコ",
                "Champignon étrange"),
            new UiTranslationEntry(
                "선반 균류",
                "선반 균류",
                "Shelf Fungus",
                "层孔菌",
                "棚キノコ",
                "Champignon en console"),
            new UiTranslationEntry(
                "방방 균류",
                "방방 균류",
                "Bounce Fungus",
                "弹跳菌",
                "バウンドキノコ",
                "Champignon rebondissant"),
            new UiTranslationEntry(
                "풍선 다발",
                "풍선 다발",
                "Balloon Bunch",
                "气球束",
                "風船の束",
                "Bouquet de ballons"),
            new UiTranslationEntry(
                "구조갈고리",
                "구조갈고리",
                "Rescue Hook",
                "救援钩",
                "レスキューフック",
                "Crochet de secours"),
            new UiTranslationEntry(
                "사슬발사기",
                "사슬발사기",
                "Chain Launcher",
                "链条发射器",
                "チェーンランチャー",
                "Lance-chaîne"),
            new UiTranslationEntry(
                "마법의 콩",
                "마법의 콩",
                "Magic Bean",
                "魔法豆",
                "魔法の豆",
                "Haricot magique"),
            new UiTranslationEntry(
                "우정 나팔",
                "우정 나팔",
                "Friendship Bugle",
                "友谊号角",
                "友情のラッパ",
                "Clairon de l’amitié"),
            new UiTranslationEntry(
                "무지개사탕",
                "무지개사탕",
                "Rainbow Candy",
                "彩虹糖",
                "レインボーキャンディ",
                "Bonbon arc-en-ciel"),
            new UiTranslationEntry(
                "황금 빙봉",
                "황금 빙봉",
                "Golden Bing Bong",
                "黄金冰棒",
                "ゴールデン・ビン・ボン",
                "Bing Bong doré"),
            new UiTranslationEntry(
                "마시멜로우",
                "마시멜로우",
                "Marshmallow",
                "棉花糖",
                "マシュマロ",
                "Guimauve"),
            new UiTranslationEntry(
                "그래놀라바",
                "그래놀라바",
                "Granola Bar",
                "燕麦棒",
                "グラノーラバー",
                "Barre de granola"),
            new UiTranslationEntry(
                "요리된 새",
                "요리된 새",
                "Cooked Bird",
                "烤鸟",
                "調理済みの鳥",
                "Oiseau cuit"),
            new UiTranslationEntry(
                "수면 열매",
                "수면 열매",
                "Sleep Berry",
                "睡眠莓",
                "睡眠ベリー",
                "Baie du sommeil"),
            new UiTranslationEntry(
                "만병통치약",
                "만병통치약",
                "Cure-All",
                "万能药",
                "万能薬",
                "Remède universel"),
            new UiTranslationEntry(
                "제작 · ",
                "제작 · ",
                "Crafting · ",
                "制作 · ",
                "クラフト · ",
                "Fabrication · "),
            new UiTranslationEntry(
                "Parts",
                "부품",
                "Parts",
                "部件",
                "部品",
                "Pièces"),
            new UiTranslationEntry(
                "Stick",
                "나뭇가지",
                "Stick",
                "树枝",
                "枝",
                "Branche"),
            new UiTranslationEntry(
                "Stone",
                "돌",
                "Stone",
                "石头",
                "石",
                "Pierre"),
            new UiTranslationEntry(
                "Conch",
                "소라고둥",
                "Conch",
                "海螺",
                "巻き貝",
                "Conque"),
            new UiTranslationEntry(
                "Bugle",
                "나팔",
                "Bugle",
                "号角",
                "ラッパ",
                "Clairon"),
            new UiTranslationEntry(
                "Piton",
                "피톤",
                "Piton",
                "岩钉",
                "ハーケン",
                "Piton"),
            new UiTranslationEntry(
                "Torch",
                "횃불",
                "Torch",
                "火把",
                "松明",
                "Torche"),
            new UiTranslationEntry(
                "Flare",
                "조명탄",
                "Flare",
                "信号弹",
                "フレア",
                "Fusée éclairante"),
            new UiTranslationEntry(
                "◀ 이전",
                "◀ 이전",
                "◀ Previous",
                "◀ 上一页",
                "◀ 前へ",
                "◀ Précédent"),
            new UiTranslationEntry(
                "다음 ▶",
                "다음 ▶",
                "Next ▶",
                "下一页 ▶",
                "次へ ▶",
                "Suivant ▶"),
            new UiTranslationEntry(
                "보유 중",
                "보유 중",
                "Owned",
                "已拥有",
                "所持中",
                "Possédé"),
            new UiTranslationEntry(
                "상태: ",
                "상태: ",
                "Status: ",
                "状态：",
                "状態: ",
                "État : "),
            new UiTranslationEntry(
                "등급: ",
                "등급: ",
                "Grade: ",
                "等级：",
                "等級: ",
                "Niveau : "),
            new UiTranslationEntry(
                "보유: ",
                "보유: ",
                "Owned: ",
                "持有：",
                "所持数: ",
                "Possédé : "),
            new UiTranslationEntry(
                "슬롯당 ",
                "슬롯당 ",
                "per slot ",
                "每栏位 ",
                "スロットあたり ",
                "par emplacement "),
            new UiTranslationEntry(
                "나뭇가지",
                "나뭇가지",
                "Stick",
                "树枝",
                "枝",
                "Branche"),
            new UiTranslationEntry(
                "소라고둥",
                "소라고둥",
                "Conch",
                "海螺",
                "巻き貝",
                "Conque"),
            new UiTranslationEntry(
                "소라고동",
                "소라고둥",
                "Conch",
                "海螺",
                "巻き貝",
                "Conque"),
            new UiTranslationEntry(
                "가이드북",
                "가이드북",
                "Guidebook",
                "指南书",
                "ガイドブック",
                "Guide"),
            new UiTranslationEntry(
                "구름균류",
                "구름균류",
                "Cloud Fungus",
                "云菌",
                "雲キノコ",
                "Champignon nuage"),
            new UiTranslationEntry(
                "밧줄타래",
                "밧줄타래",
                "Rope Spool",
                "绳索卷",
                "ロープスプール",
                "Bobine de corde"),
            new UiTranslationEntry(
                "뼈의 서",
                "뼈의 서",
                "Book of Bones",
                "骨之书",
                "骨の書",
                "Livre des os"),
            new UiTranslationEntry(
                "요정랜턴",
                "요정랜턴",
                "Fairy Lantern",
                "仙女灯笼",
                "妖精のランタン",
                "Lanterne féerique"),
            new UiTranslationEntry(
                "강화우유",
                "강화우유",
                "Fortified Milk",
                "强化牛奶",
                "強化ミルク",
                "Lait fortifié"),
            new UiTranslationEntry(
                "통통버섯",
                "통통버섯",
                "Puff Mushroom",
                "膨膨蘑菇",
                "パフキノコ",
                "Champignon gonflé"),
            new UiTranslationEntry(
                "나팔버섯",
                "나팔버섯",
                "Trumpet Mushroom",
                "喇叭蘑菇",
                "ラッパキノコ",
                "Champignon trompette"),
            new UiTranslationEntry(
                "다발버섯",
                "다발버섯",
                "Bundle Mushroom",
                "束状蘑菇",
                "束キノコ",
                "Champignon en bouquet"),
            new UiTranslationEntry(
                "단추버섯",
                "단추버섯",
                "Button Mushroom",
                "纽扣蘑菇",
                "ボタンキノコ",
                "Champignon bouton"),
            new UiTranslationEntry(
                "구급상자",
                "구급상자",
                "First Aid Kit",
                "急救箱",
                "救急箱",
                "Trousse de secours"),
            new UiTranslationEntry(
                "개 판매",
                "개 판매",
                " units to sell",
                "个待出售",
                "個を売却",
                " unités à vendre"),
            new UiTranslationEntry(
                "Sell",
                "판매",
                "Sell",
                "出售",
                "売却",
                "Vendre"),
            new UiTranslationEntry(
                "Food",
                "음식",
                "Food",
                "食物",
                "食料",
                "Nourriture"),
            new UiTranslationEntry(
                "Snow",
                "눈",
                "Snow",
                "雪",
                "雪",
                "Neige"),
            new UiTranslationEntry(
                "Tick",
                "진드기",
                "Tick",
                "蜱虫",
                "ダニ",
                "Tique"),
            new UiTranslationEntry(
                "Rare",
                "희귀",
                "Rare",
                "稀有",
                "レア",
                "Rare"),
            new UiTranslationEntry(
                "개발자",
                "개발자",
                "Developer",
                "开发者",
                "開発者",
                "Développeur"),
            new UiTranslationEntry(
                "가격표",
                "가격표",
                "Price list",
                "价格表",
                "価格表",
                "Tarifs"),
            new UiTranslationEntry(
                "페이지",
                "페이지",
                "Page",
                "页",
                "ページ",
                "Page"),
            new UiTranslationEntry(
                "미구매",
                "미구매",
                "Not purchased",
                "未购买",
                "未購入",
                "Non acheté"),
            new UiTranslationEntry(
                "미충족",
                "미충족",
                "Not met",
                "未满足",
                "未達成",
                "Non rempli"),
            new UiTranslationEntry(
                "미제작",
                "미제작",
                "Not crafted",
                "未制作",
                "未作成",
                "Non fabriqué"),
            new UiTranslationEntry(
                "최고급",
                "최고급",
                "Masterwork",
                "大师级",
                "最高級",
                "Chef-d’œuvre"),
            new UiTranslationEntry(
                "망원경",
                "망원경",
                "Binoculars",
                "双筒望远镜",
                "双眼鏡",
                "Jumelles"),
            new UiTranslationEntry(
                "스크롤",
                "스크롤",
                "Scroll",
                "卷轴",
                "巻物",
                "Parchemin"),
            new UiTranslationEntry(
                "밧줄총",
                "밧줄총",
                "Rope Cannon",
                "绳索炮",
                "ロープキャノン",
                "Canon à corde"),
            new UiTranslationEntry(
                "뼈의서",
                "뼈의서",
                "Book of Bones",
                "骨之书",
                "骨の書",
                "Livre des os"),
            new UiTranslationEntry(
                "해독제",
                "해독제",
                "Antidote",
                "解毒剂",
                "解毒剤",
                "Antidote"),
            new UiTranslationEntry(
                "파라솔",
                "파라솔",
                "Parasol",
                "阳伞",
                "パラソル",
                "Parasol"),
            new UiTranslationEntry(
                "선크림",
                "선크림",
                "Sunscreen",
                "防晒霜",
                "日焼け止め",
                "Crème solaire"),
            new UiTranslationEntry(
                "선인장",
                "선인장",
                "Cactus",
                "仙人掌",
                "サボテン",
                "Cactus"),
            new UiTranslationEntry(
                "조명탄",
                "조명탄",
                "Flare",
                "信号弹",
                "フレア",
                "Fusée éclairante"),
            new UiTranslationEntry(
                "진드기",
                "진드기",
                "Tick",
                "蜱虫",
                "ダニ",
                "Tique"),
            new UiTranslationEntry(
                "핫도그",
                "핫도그",
                "Hot Dog",
                "热狗",
                "ホットドッグ",
                "Hot-dog"),
            new UiTranslationEntry(
                "기내식",
                "기내식",
                "Airline Food",
                "飞机餐",
                "機内食",
                "Repas d’avion"),
            new UiTranslationEntry(
                "벌집꿀",
                "벌집꿀",
                "Honeycomb Honey",
                "蜂巢蜜",
                "巣蜜",
                "Miel en rayon"),
            new UiTranslationEntry(
                "뾱뾱이",
                "뾱뾱이",
                "Pop Pop",
                "泡泡纸",
                "プチプチ",
                "Papier bulle"),
            new UiTranslationEntry(
                "비용 ",
                "비용 ",
                "Cost ",
                "费用 ",
                "費用 ",
                "Coût "),
            new UiTranslationEntry(
                "설명",
                "설명",
                "Description",
                "说明",
                "説明",
                "Description"),
            new UiTranslationEntry(
                "강화",
                "강화",
                "Upgrades",
                "升级",
                "強化",
                "Améliorations"),
            new UiTranslationEntry(
                "제작",
                "제작",
                "Crafting",
                "制作",
                "クラフト",
                "Fabrication"),
            new UiTranslationEntry(
                "판매",
                "판매",
                "Sell",
                "出售",
                "売却",
                "Vendre"),
            new UiTranslationEntry(
                "부품",
                "부품",
                "Parts",
                "部件",
                "部品",
                "Pièces"),
            new UiTranslationEntry(
                "등산",
                "등산",
                "Climbing",
                "攀登",
                "登山",
                "Escalade"),
            new UiTranslationEntry(
                "음식",
                "음식",
                "Food",
                "食物",
                "食料",
                "Nourriture"),
            new UiTranslationEntry(
                "부활",
                "부활",
                "Revive",
                "复活",
                "蘇生",
                "Résurrection"),
            new UiTranslationEntry(
                "필수",
                "필수",
                "Essentials",
                "必需品",
                "必需品",
                "Essentiels"),
            new UiTranslationEntry(
                "닫기",
                "닫기",
                "Close",
                "关闭",
                "閉じる",
                "Fermer"),
            new UiTranslationEntry(
                "충족",
                "충족",
                "Met",
                "满足",
                "達成",
                "Rempli"),
            new UiTranslationEntry(
                "재료",
                "재료",
                "Materials",
                "材料",
                "素材",
                "Matériaux"),
            new UiTranslationEntry(
                "회복",
                "회복",
                "Healing",
                "恢复",
                "回復",
                "Soins"),
            new UiTranslationEntry(
                "기초",
                "기초",
                "Basic",
                "基础",
                "基礎",
                "Basique"),
            new UiTranslationEntry(
                "일반",
                "일반",
                "Standard",
                "普通",
                "標準",
                "Standard"),
            new UiTranslationEntry(
                "고급",
                "고급",
                "Advanced",
                "高级",
                "上級",
                "Avancé"),
            new UiTranslationEntry(
                "특수",
                "특수",
                "Special",
                "特殊",
                "特殊",
                "Spécial"),
            new UiTranslationEntry(
                "빙봉",
                "빙봉",
                "Bing Bong",
                "冰棒",
                "ビン・ボン",
                "Bing Bong"),
            new UiTranslationEntry(
                "나팔",
                "나팔",
                "Bugle",
                "号角",
                "ラッパ",
                "Clairon"),
            new UiTranslationEntry(
                "배낭",
                "배낭",
                "Backpack",
                "背包",
                "バックパック",
                "Sac à dos"),
            new UiTranslationEntry(
                "피톤",
                "피톤",
                "Piton",
                "岩钉",
                "ハーケン",
                "Piton"),
            new UiTranslationEntry(
                "풍선",
                "풍선",
                "Balloon",
                "气球",
                "風船",
                "Ballon"),
            new UiTranslationEntry(
                "핫팩",
                "핫팩",
                "Heat Pack",
                "暖宝宝",
                "カイロ",
                "Chaufferette"),
            new UiTranslationEntry(
                "횃불",
                "횃불",
                "Torch",
                "火把",
                "松明",
                "Torche"),
            new UiTranslationEntry(
                "랜턴",
                "랜턴",
                "Lantern",
                "灯笼",
                "ランタン",
                "Lanterne"),
            new UiTranslationEntry(
                "붕대",
                "붕대",
                "Bandage",
                "绷带",
                "包帯",
                "Bandage"),
            new UiTranslationEntry(
                "힐",
                "힐",
                "Healing",
                "治疗",
                "回復",
                "Soins"),
            new UiTranslationEntry(
                "돌",
                "돌",
                "Stone",
                "石头",
                "石",
                "Pierre"),
            new UiTranslationEntry(
                "눈",
                "눈",
                "Snow",
                "雪",
                "雪",
                "Neige")
            };

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

        // CraftRecipe의 원본 Category와 별개로 제작 탭 필터에 사용하는 UI 분류입니다.
        internal enum CraftUiCategory
        {
            Climbing = 0,
            Food = 1,
            Heal = 2,
            Revive = 3,
            Essential = 4
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
            public int RequiredResourceLevel;

            public int MoneyCost;

            public readonly List<IngredientCost>
                Ingredients =
                    new List<IngredientCost>();
        }

        private sealed class CampfireBuildRecipe
        {
            public int Stage;
            public string Name;
            public int RequiredResourceLevel;
            public int MoneyCost;
            public readonly List<IngredientCost> Ingredients =
                new List<IngredientCost>();

            public CampfireBuildRecipe(
                int stage,
                string name,
                int requiredResourceLevel,
                int moneyCost,
                params IngredientCost[] ingredients)
            {
                Stage = Mathf.Clamp(stage, 1, 4);
                Name = name ?? string.Empty;
                RequiredResourceLevel =
                    Mathf.Clamp(requiredResourceLevel, 0, 4);
                MoneyCost = Mathf.Max(0, moneyCost);

                if (ingredients != null)
                {
                    Ingredients.AddRange(ingredients);
                }
            }
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
                RequiredResourceLevel = Mathf.Clamp(requiredResourceLevel, 0, 4);
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
            StackCapacity = 1,
            CampfireEfficiency = 2,
            DoubleYield = 3,
            SellValue = 4
        }

        private sealed class UpgradeFormulaConfig
        {
            public ConfigEntry<int> BaseCost;
            public ConfigEntry<int> CostGrowth;
        }

        private sealed class UpgradeState
        {
            public int Protocol;
            public int Revision;
            public int OwnerActor;
            public string RunId;

            public int ResourceLevel;
            public int StackLevel;
            public int CampfireLevel;
            public int YieldMultiplier;
            public int SellMultiplier;

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

                        StackLevel =
                            StackLevel,

                        CampfireLevel =
                            CampfireLevel,

                        YieldMultiplier =
                            YieldMultiplier,

                        SellMultiplier =
                            SellMultiplier,

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

                        StackLevel =
                            0,

                        CampfireLevel =
                            0,

                        YieldMultiplier =
                            1,

                        SellMultiplier =
                            1,

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

        private sealed class PendingSaleTransaction
        {
            public int TransactionId;
            public int ActorNumber;
            public byte SlotId;
            public ushort ItemId;
            public string ItemGuid;
            public string ItemName;
            public int SalePrice;
            public double CreatedAt;
        }

        private sealed class LocalPendingSale
        {
            public int TransactionId;
            public byte SlotId;
            public ushort ItemId;
            public string ItemGuid;
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
                " loaded. Existing Shop/Store/Upgrade files are not required. Press P." +
                " CustomEventCodes=" +
                SellRequestEventCode + "-" +
                DeveloperMoneyResultEventCode +
                " (Photon-safe range)" +
                " | Room-fixed host recipe synchronization enabled");
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
            pendingSaleTransactions.Clear();
            reservedOrSoldItemGuids.Clear();
            localPendingSale = null;
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
            // 매 프레임 요청 시간 초과, 판매 수량 휠 입력, 닫힌 창 정리와 P키 토글을 처리합니다.
            UpdatePendingRequest();
            UpdateSellQuantityInput();

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

        private void UpdateSellQuantityInput()
        {
            if (activeWindow == null ||
                currentTab != HubTab.Sell ||
                pendingRequest != PendingRequest.None)
            {
                return;
            }

            Mouse mouse =
                Mouse.current;

            if (mouse == null)
            {
                return;
            }

            float scrollY =
                mouse.scroll
                    .ReadValue()
                    .y;

            if (Mathf.Abs(scrollY) <
                0.01f)
            {
                return;
            }

            int step =
                1;

            Keyboard keyboard =
                Keyboard.current;

            if (keyboard != null &&
                (
                    keyboard.leftShiftKey
                        .isPressed ||
                    keyboard.rightShiftKey
                        .isPressed
                ))
            {
                step =
                    5;
            }

            AdjustSelectedSellQuantity(
                scrollY >
                    0f
                    ? step
                    : -step);
        }

        private void AdjustSelectedSellQuantity(
            int delta)
        {
            global::Player player =
                global::Player.localPlayer;

            if (player == null ||
                player.itemSlots == null ||
                selectedSellSlotId < 0 ||
                selectedSellSlotId >=
                    player.itemSlots.Length)
            {
                selectedSellQuantity =
                    1;

                return;
            }

            ItemSlot slot =
                player.GetItemSlot(
                    (byte)selectedSellSlotId);

            if (slot == null ||
                slot.IsEmpty() ||
                slot.prefab == null ||
                !Spawn.IsSaleResourceId(
                    slot.prefab.itemID))
            {
                selectedSellQuantity =
                    1;

                return;
            }

            int count =
                Mathf.Max(
                    1,
                    InventoryStack
                        .GetStackCount(
                            player,
                            (byte)selectedSellSlotId));

            int nextQuantity =
                Mathf.Clamp(
                    selectedSellQuantity +
                    delta,
                    1,
                    count);

            if (nextQuantity ==
                selectedSellQuantity)
            {
                return;
            }

            selectedSellQuantity =
                nextQuantity;

            SetTabStatus(
                HubTab.Sell,
                "판매 수량을 " +
                selectedSellQuantity +
                "개로 설정했습니다.");
        }

        private void HandleSceneLoaded(
            Scene scene,
            LoadSceneMode mode)
        {
            CloseHub();

            pendingRequest =
                PendingRequest.None;

            pendingSaleTransactions.Clear();
            reservedOrSoldItemGuids.Clear();
            localPendingSale = null;
            nextSaleTransactionId = 0;

            developerMoneyRequestSequence =
                0;

            developerMoneyPendingResponses =
                0;

            developerHostAuthoritativeBalance =
                -1;

            currentTab =
                HubTab.Description;

            selectedUpgradeKind =
                UpgradeKind.ResourceGrade;

            selectedCraftRecipeIndex =
                -1;

            selectedSellSlotId =
                -1;

            selectedSellQuantity =
                1;

            craftPage =
                0;

            // 씬 로드 시 메뉴 선택과 상태 문자열을 초기화합니다.
            // 생성된 제작식과 진행 레시피 캐시는 Airport 분기에서만 비웁니다.

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

                ResetLocalRecipeCaches(
                    "Airport / waiting for new run");

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

            // 게임플레이 씬 진입 시 Room Property 기반 공유 제작 시드를 확보합니다.
            EnsureSharedRecipeSeed();

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

            EnsureSharedRecipeSeed();
            EnsureProgressionRecipesBuilt();
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
        /// Shop, Store, Upgrade 파사드가 CraftHub의 지정 탭을 열 때 사용하는 진입점입니다.
        /// Developer 탭 요청은 Description 탭으로 변경합니다.
        /// </summary>
        internal void OpenCompatibilityTab(
            HubTab tab)
        {
            if (tab ==
                HubTab.Developer)
            {
                tab =
                    HubTab.Description;
            }

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
            // Developer 탭 선택은 Description 탭으로 변경합니다.
            if (tab ==
                HubTab.Developer)
            {
                tab =
                    HubTab.Description;
            }

            currentTab =
                tab;

            if (tab ==
                HubTab.Craft &&
                craftRecipes.Count ==
                    0)
            {
                if (!EnsureCraftRecipesBuilt())
                {
                    SetTabStatus(
                        HubTab.Craft,
                        "호스트가 이번 판 제작식과 부품 재료를 확정하는 중입니다...");
                }

                CraftRecipe selectedRecipe =
                    SelectedCraftRecipe;

                if (selectedRecipe == null ||
                    GetCraftUiCategory(
                        selectedRecipe) !=
                    selectedCraftUiCategory)
                {
                    selectedCraftRecipeIndex =
                        GetRecipeIndexAtFilteredPosition(
                            0);
                }
            }

            if (tab ==
                HubTab.Craft)
            {
                LogScoutEffigyFinalState(
                    "Craft tab opened");
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

        internal HubLanguage CurrentLanguage
        {
            get
            {
                return currentLanguage;
            }
        }

        internal void SelectLanguage(
            HubLanguage language)
        {
            if (currentLanguage ==
                language)
            {
                return;
            }

            currentLanguage =
                language;

            RefreshWindow();
        }

        private static string GetHubTabDisplayName(
            HubTab tab)
        {
            switch (tab)
            {
                case HubTab.Upgrade:
                    return "강화";

                case HubTab.Craft:
                    return "제작";

                case HubTab.Sell:
                    return "판매";

                case HubTab.Parts:
                    return "부품";

                case HubTab.Developer:
                    return "개발자";

                case HubTab.Description:
                default:
                    return "설명";
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

                case HubTab.Developer:
                    return developerStatus;

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

                case HubTab.Developer:
                    developerStatus =
                        safe;
                    break;
            }

            RefreshWindow();
        }

        // -----------------------------------------------------------------
        // Photon 이벤트 처리와 공유 돈
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
                SellConsumeRequestEventCode)
            {
                HandleSellConsumeRequest(
                    photonEvent.CustomData as
                        object[]);

                return;
            }

            if (photonEvent.Code ==
                SellConsumeAckEventCode)
            {
                if (PhotonNetwork.IsMasterClient)
                {
                    ProcessSellConsumeAckOnHost(
                        photonEvent.Sender,
                        photonEvent.CustomData as
                            object[]);
                }

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
                DeveloperMoneyRequestEventCode)
            {
                if (PhotonNetwork.IsMasterClient)
                {
                    ProcessDeveloperMoneyRequestOnHost(
                        photonEvent.Sender,
                        photonEvent.CustomData as
                            object[]);
                }

                return;
            }

            if (photonEvent.Code ==
                DeveloperMoneyResultEventCode)
            {
                HandleDeveloperMoneyResult(
                    photonEvent.CustomData as
                        object[]);

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
                selectedSellQuantity =
                    1;
            }

            // 결과를 수신할 때는 호스트가 판매 수량 소비와 공유 돈 갱신을 이미 처리한 상태입니다.
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
                                    ? "제작품 지급 중 오류가 발생했습니다."
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

            developerHostAuthoritativeBalance =
                0;

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

        internal void RequestDeveloperMoney()
        {
            if (!PhotonNetwork.InRoom ||
                PhotonNetwork.CurrentRoom == null)
            {
                SetTabStatus(
                    HubTab.Developer,
                    "현재 Photon 방에 입장해 있지 않습니다.");

                return;
            }

            int requestId =
                ++developerMoneyRequestSequence;

            developerMoneyPendingResponses++;

            SetTabStatus(
                HubTab.Developer,
                "공유 돈 +100 요청 전송 완료" +
                (
                    developerMoneyPendingResponses > 1
                        ? "\n처리 대기 요청: " +
                          developerMoneyPendingResponses +
                          "개"
                        : string.Empty
                ));

            // 로컬 플레이어가 호스트이면 RaiseEvent를 거치지 않고 같은 호스트 처리 메서드를 호출합니다.
            if (PhotonNetwork.IsMasterClient)
            {
                ProcessDeveloperMoneyRequestOnHost(
                    PhotonNetwork.LocalPlayer != null
                        ? PhotonNetwork.LocalPlayer.ActorNumber
                        : 0,
                    new object[]
                    {
                        requestId,
                        DeveloperMoneyPerClick
                    });

                return;
            }

            Photon.Realtime.Player masterClient =
                PhotonNetwork.MasterClient;

            if (masterClient == null)
            {
                developerMoneyPendingResponses =
                    Mathf.Max(
                        0,
                        developerMoneyPendingResponses - 1);

                SetTabStatus(
                    HubTab.Developer,
                    "현재 호스트를 찾지 못해 요청을 보내지 못했습니다.");

                return;
            }

            RaiseEventOptions options =
                new RaiseEventOptions
                {
                    TargetActors =
                        new[]
                        {
                            masterClient.ActorNumber
                        }
                };

            SendOptions sendOptions =
                new SendOptions
                {
                    Reliability =
                        true
                };

            bool sent =
                PhotonNetwork.RaiseEvent(
                    DeveloperMoneyRequestEventCode,
                    new object[]
                    {
                        requestId,
                        DeveloperMoneyPerClick
                    },
                    options,
                    sendOptions);

            if (!sent)
            {
                developerMoneyPendingResponses =
                    Mathf.Max(
                        0,
                        developerMoneyPendingResponses - 1);

                SetTabStatus(
                    HubTab.Developer,
                    "공유 돈 +100 요청 전송에 실패했습니다.");
            }
        }

        private void ProcessDeveloperMoneyRequestOnHost(
            int actorNumber,
            object[] payload)
        {
            if (!PhotonNetwork.IsMasterClient ||
                PhotonNetwork.CurrentRoom == null)
            {
                return;
            }

            int requestId =
                0;

            int requestedAmount =
                DeveloperMoneyPerClick;

            try
            {
                if (payload != null &&
                    payload.Length > 0)
                {
                    requestId =
                        Convert.ToInt32(
                            payload[0]);
                }

                if (payload != null &&
                    payload.Length > 1)
                {
                    requestedAmount =
                        Convert.ToInt32(
                            payload[1]);
                }
            }
            catch (Exception)
            {
                SendDeveloperMoneyResult(
                    actorNumber,
                    false,
                    requestId,
                    ReadSharedMoney(),
                    0,
                    "개발자 요청 데이터가 올바르지 않습니다.");

                return;
            }

            // 개발자 돈 요청은 요청 데이터가 100원과 정확히 일치할 때만 처리합니다.
            if (requestedAmount !=
                DeveloperMoneyPerClick)
            {
                SendDeveloperMoneyResult(
                    actorNumber,
                    false,
                    requestId,
                    ReadSharedMoney(),
                    0,
                    "허용되지 않은 공유 돈 요청입니다.");

                return;
            }

            // 연속 요청 사이의 Room Property 반영 지연을 보완하기 위해 호스트 잔액을 별도로 누산합니다.
            int roomBalance =
                ReadSharedMoney();

            if (developerHostAuthoritativeBalance < 0)
            {
                developerHostAuthoritativeBalance =
                    roomBalance;
            }
            else if (roomBalance >
                     developerHostAuthoritativeBalance)
            {
                developerHostAuthoritativeBalance =
                    roomBalance;
            }

            developerHostAuthoritativeBalance =
                Mathf.Max(
                    0,
                    developerHostAuthoritativeBalance +
                    DeveloperMoneyPerClick);

            int updatedMoney =
                developerHostAuthoritativeBalance;

            SetSharedMoneyOnHost(
                updatedMoney);

            cachedSharedMoney =
                updatedMoney;

            SendDeveloperMoneyResult(
                actorNumber,
                true,
                requestId,
                updatedMoney,
                DeveloperMoneyPerClick,
                "개발자 치트: 공유 돈 +100원");
        }

        private static void SendDeveloperMoneyResult(
            int actorNumber,
            bool success,
            int requestId,
            int balance,
            int appliedAmount,
            string message)
        {
            if (actorNumber <= 0)
            {
                return;
            }

            RaiseEventOptions options =
                new RaiseEventOptions
                {
                    TargetActors =
                        new[]
                        {
                            actorNumber
                        }
                };

            SendOptions sendOptions =
                new SendOptions
                {
                    Reliability =
                        true
                };

            PhotonNetwork.RaiseEvent(
                DeveloperMoneyResultEventCode,
                new object[]
                {
                    success,
                    requestId,
                    balance,
                    appliedAmount,
                    message ?? string.Empty
                },
                options,
                sendOptions);
        }

        private void HandleDeveloperMoneyResult(
            object[] payload)
        {
            developerMoneyPendingResponses =
                Mathf.Max(
                    0,
                    developerMoneyPendingResponses - 1);

            if (payload == null ||
                payload.Length < 5)
            {
                SetTabStatus(
                    HubTab.Developer,
                    "호스트의 처리 결과를 확인하지 못했습니다.");

                return;
            }

            try
            {
                bool success =
                    Convert.ToBoolean(
                        payload[0]);

                int requestId =
                    Convert.ToInt32(
                        payload[1]);

                int balance =
                    Convert.ToInt32(
                        payload[2]);

                int appliedAmount =
                    Convert.ToInt32(
                        payload[3]);

                string message =
                    payload[4] as string ??
                    string.Empty;

                cachedSharedMoney =
                    Mathf.Max(
                        0,
                        balance);

                partyResourceCacheUntil =
                    0f;

                SetTabStatus(
                    HubTab.Developer,
                    (
                        string.IsNullOrEmpty(message)
                            ? (
                                success
                                    ? "공유 돈 +" +
                                      appliedAmount +
                                      "원이 반영되었습니다."
                                    : "공유 돈 요청 처리에 실패했습니다."
                              )
                            : message
                    ) +
                    "\n현재 공유 돈: " +
                    cachedSharedMoney +
                    "원" +
                    (
                        developerMoneyPendingResponses > 0
                            ? "\n처리 대기 요청: " +
                              developerMoneyPendingResponses +
                              "개"
                            : string.Empty
                    ));

                RefreshWindow();
            }
            catch (Exception)
            {
                SetTabStatus(
                    HubTab.Developer,
                    "호스트 결과 데이터가 올바르지 않습니다.");
            }
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

        private bool EnsureSharedRecipeSeed()
        {
            if (!PhotonNetwork.InRoom ||
                PhotonNetwork.CurrentRoom == null ||
                !gameplayScene)
            {
                return false;
            }

            ExitGames.Client.Photon.Hashtable properties =
                PhotonNetwork.CurrentRoom.CustomProperties;

            object protocolValue =
                null;

            object runValue =
                null;

            object seedValue =
                null;

            bool found =
                properties.TryGetValue(
                    RecipeProtocolKey,
                    out protocolValue) &&
                properties.TryGetValue(
                    RecipeRunIdKey,
                    out runValue) &&
                properties.TryGetValue(
                    RecipeSeedKey,
                    out seedValue);

            if (found)
            {
                try
                {
                    int protocol =
                        Convert.ToInt32(
                            protocolValue);

                    string roomRecipeId =
                        runValue as string ??
                        Convert.ToString(
                            runValue);

                    int roomSeed =
                        Convert.ToInt32(
                            seedValue);

                    if (protocol ==
                            RecipeProtocolVersion &&
                        !string.IsNullOrEmpty(
                            roomRecipeId))
                    {
                        ApplySharedRecipeSeed(
                            roomRecipeId,
                            roomSeed,
                            "Room properties");

                        return true;
                    }
                }
                catch (Exception exception)
                {
                    Logger.LogWarning(
                        "Shared recipe seed read failed: " +
                        exception.Message);
                }
            }

            // 일반 클라이언트는 공유 시드를 생성하지 않고 호스트가 Room Property에 기록할 때까지 기다립니다.
            if (!PhotonNetwork.IsMasterClient)
            {
                return false;
            }

            // 공유 시드 속성이 없는 Photon 방에서 호스트가 한 번 생성해 게시합니다.
            string recipeRoundId =
                Guid.NewGuid()
                    .ToString("N");

            int generatedSeed =
                CreateHostRecipeSeed(
                    recipeRoundId);

            ExitGames.Client.Photon.Hashtable newProperties =
                new ExitGames.Client.Photon.Hashtable
                {
                    {
                        RecipeProtocolKey,
                        RecipeProtocolVersion
                    },
                    {
                        RecipeRunIdKey,
                        recipeRoundId
                    },
                    {
                        RecipeSeedKey,
                        generatedSeed
                    }
                };

            if (!PhotonNetwork.CurrentRoom
                    .SetCustomProperties(
                        newProperties))
            {
                Logger.LogError(
                    "Host failed to publish shared recipe seed.");

                return false;
            }

            ApplySharedRecipeSeed(
                recipeRoundId,
                generatedSeed,
                "Host generated for room");

            Logger.LogInfo(
                "Shared recipe seed published. RecipeRoundId=" +
                recipeRoundId +
                " | Seed=" +
                generatedSeed);

            return true;
        }

        private static int CreateHostRecipeSeed(
            string runId)
        {
            unchecked
            {
                string roomName =
                    PhotonNetwork.CurrentRoom != null
                        ? PhotonNetwork.CurrentRoom.Name
                        : string.Empty;

                string source =
                    (runId ?? string.Empty) +
                    "|" +
                    roomName +
                    "|" +
                    PhotonNetwork.ServerTimestamp +
                    "|" +
                    Guid.NewGuid().ToString("N");

                int seed =
                    StableProgressionSeed(
                        source);

                return
                    seed == 0
                        ? 1
                        : seed;
            }
        }

        private void ApplySharedRecipeSeed(
            string runId,
            int seed,
            string reason)
        {
            string safeRunId =
                runId ??
                string.Empty;

            bool changed =
                !sharedRecipeSeedLoaded ||
                sharedRecipeSeed !=
                    seed ||
                !string.Equals(
                    sharedRecipeRunId,
                    safeRunId,
                    StringComparison.Ordinal);

            sharedRecipeSeed =
                seed;

            sharedRecipeRunId =
                safeRunId;

            sharedRecipeSeedLoaded =
                true;

            if (!changed)
            {
                return;
            }

            ResetGeneratedRecipeLists();

            Logger.LogInfo(
                "Shared recipe seed applied. Reason=" +
                reason +
                " | RunId=" +
                safeRunId +
                " | Seed=" +
                seed);
        }

        private void ResetLocalRecipeCaches(
            string reason)
        {
            sharedRecipeSeed =
                0;

            sharedRecipeRunId =
                string.Empty;

            sharedRecipeSeedLoaded =
                false;

            ResetGeneratedRecipeLists();

            Logger.LogDebug(
                "Local recipe caches reset. Reason=" +
                reason);
        }

        private void ResetGeneratedRecipeLists()
        {
            craftRecipes.Clear();
            craftRecipesByOutputId.Clear();

            NextCampfireRecipes.Clear();
            PlanePartRecipes.Clear();

            progressionRecipesBuilt =
                false;

            progressionRecipeRunId =
                string.Empty;

            selectedCraftRecipeIndex =
                -1;

            craftPage =
                0;
        }

        // -----------------------------------------------------------------
        // 진행 단계와 비행기 부품
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

        private bool EnsureProgressionRecipesBuilt()
        {
            if (progressionRecipesBuilt &&
                NextCampfireRecipes.Count == 4 &&
                PlanePartRecipes.Count == 4)
            {
                return true;
            }

            ItemDatabase database =
                SingletonAsset<ItemDatabase>.Instance;

            if (database == null ||
                database.itemLookup == null ||
                database.itemLookup.Count == 0)
            {
                return false;
            }

            if (!EnsureSharedRecipeSeed())
            {
                return false;
            }

            string runId =
                sharedRecipeRunId;

            if (progressionRecipesBuilt &&
                NextCampfireRecipes.Count == 4 &&
                PlanePartRecipes.Count == 4 &&
                string.Equals(
                    progressionRecipeRunId,
                    runId,
                    StringComparison.Ordinal))
            {
                return true;
            }

            List<ushort> commonSale =
                BuildResolvedPool(
                    database,
                    new[] { "FireWood", "Fire Wood", "나뭇가지" },
                    new[] { "Stone", "Rock", "돌" },
                    new[] { "Conch", "Shell", "소라고둥", "소라고동" });

            List<ushort> normalSale =
                BuildResolvedPool(
                    database,
                    new[] { "Binoculars", "망원경" },
                    new[] { "Bing Bong", "BingBong", "빙봉" },
                    new[] { "Bugle", "나팔" },
                    new[] { "Frisbee", "Flying Disc", "플라잉디스크" });

            List<ushort> rareSale =
                BuildResolvedPool(
                    database,
                    new[] { "Guidebook", "Guide Book", "가이드북" },
                    new[] { "Scroll", "스크롤" });

            List<ushort> uniqueSale =
                BuildResolvedPool(
                    database,
                    new[] { "Weird Shroom", "WeirdShroom", "괴상 버섯" });

            List<ushort> legendarySale =
                BuildResolvedPool(
                    database,
                    new[] { "Strange Gem", "StrangeGem", "이상한 보석" });

            List<ushort> commonClimbing =
                BuildResolvedPool(
                    database,
                    new[] { "Piton", "피톤" },
                    new[] { "Energy Drink", "EnergyDrink", "에너지 드링크" },
                    new[] { "Balloon", "풍선" },
                    new[] { "Portable Stove", "PortableStove", "휴대용 스토브" });

            List<ushort> normalClimbing =
                BuildResolvedPool(
                    database,
                    new[] { "Shelf Fungus", "ShelfFungus", "선반 균류" },
                    new[] { "Cloud Fungus", "CloudFungus", "구름균류" },
                    new[] { "Rope Spool", "RopeSpool", "밧줄타래" },
                    new[] { "Bounce Fungus", "BounceFungus", "방방 균류" },
                    new[] { "Checkpoint Flag", "CheckpointFlag", "체크포인트 깃발" });

            List<ushort> uniqueClimbing =
                BuildResolvedPool(
                    database,
                    new[] { "Balloon Bunch", "Bunch of Balloons", "풍선 다발" },
                    new[] { "Rescue Hook", "RescueHook", "구조갈고리" },
                    new[] { "Chain Launcher", "ChainLauncher", "사슬발사기" },
                    new[] { "Magic Bean", "MagicBean", "마법의 콩" },
                    new[] { "Rope Cannon", "RopeCannon", "밧줄총" });

            List<ushort> legendaryClimbing =
                BuildResolvedPool(
                    database,
                    new[] { "Book of Bones", "Bone Book", "뼈의서", "뼈의 서" },
                    new[] { "Anti-Rope Cannon", "Reverse Rope Cannon", "반전 밧줄총" },
                    new[] { "Anti-Rope Spool", "Reverse Rope Spool", "반전 밧줄타래" },
                    new[] { "Friendship Bugle", "Friendship Horn", "우정 나팔" });

            List<ushort> commonUtility =
                BuildResolvedPool(
                    database,
                    new[] { "Pirate Compass", "PirateCompass", "해적 나침반" },
                    new[] { "Snow", "눈" },
                    new[] { "Aloe Vera", "AloeVera", "알로에 베라" },
                    new[] { "Heat Pack", "HeatPack", "핫팩" },
                    new[] { "Torch", "횃불" });

            List<ushort> normalUtility =
                BuildResolvedPool(
                    database,
                    new[] { "Lantern", "랜턴" },
                    new[] { "Antidote", "해독제" },
                    new[] { "Rainbow Candy", "RainbowCandy", "무지개사탕" },
                    new[] { "Parasol", "파라솔" },
                    new[] { "Sunscreen", "선크림" });

            List<ushort> rareUtility =
                BuildResolvedPool(
                    database,
                    new[] { "Cactus", "선인장" },
                    new[] { "Dynamite", "다이너마이트" },
                    new[] { "Scout Cannon", "ScoutCannon", "스카우트 캐논" });

            List<ushort> uniqueUtility =
                BuildResolvedPool(
                    database,
                    new[] { "Scoutmaster Bugle", "Scoutmaster Horn", "스카우트지도자의 나팔" },
                    new[] { "Cursed Skull", "CursedSkull", "저주받은 해골" },
                    new[] { "Fairy Lantern", "FairyLantern", "요정랜턴" });

            List<ushort> legendaryUtility =
                BuildResolvedPool(
                    database,
                    new[] { "Golden Bing Bong", "GoldenBingBong", "황금 빙봉" },
                    new[] { "Flare", "조명탄" });

            List<ushort> commonFood =
                BuildResolvedPool(
                    database,
                    new[] { "Red Crispberry", "Red Crisp Berry", "빨간색 아삭 열매" },
                    new[] { "Coconut Half", "Half Coconut", "코코넛 반쪽" },
                    new[] { "Trail Mix", "TrailMix", "트레일 믹스" },
                    new[] { "Yellow Berrynana", "Yellow Banana", "노란색 열매나나" },
                    new[] { "Blue Mushroom Berry", "Blue MushroomBerry", "파란색 버섯열매" },
                    new[] { "Sports Drink", "SportsDrink", "스포츠 드링크" });

            List<ushort> normalFood =
                BuildResolvedPool(
                    database,
                    new[] { "Tick", "진드기" },
                    new[] { "Red Clusterberry", "Red Cluster Berry", "빨간 송송열매" },
                    new[] { "Green Bigberry", "Green Big Berry", "녹색 대왕열매" },
                    new[] { "Fortified Milk", "Reinforced Milk", "강화우유" },
                    new[] { "Marshmallow", "마시멜로우" },
                    new[] { "Granola Bar", "GranolaBar", "그래놀라바" },
                    new[] { "Puff Mushroom", "PuffMushroom", "통통버섯" },
                    new[] { "Trumpet Mushroom", "TrumpetMushroom", "나팔버섯" },
                    new[] { "Bundle Mushroom", "BundleMushroom", "다발버섯" },
                    new[] { "Button Mushroom", "ButtonMushroom", "단추버섯" },
                    new[] { "Orange Winterberry", "Orange Winter Berry", "주황 겨울열매" },
                    new[] { "Red Thornberry", "Red Thorn Berry", "빨간 가시열매" },
                    new[] { "Purple Mushroom Berry", "Purple MushroomBerry", "보라색 버섯열매" });

            List<ushort> rareFood =
                BuildResolvedPool(
                    database,
                    new[] { "Hot Dog", "Hotdog", "핫도그" },
                    new[] { "Cooked Bird", "CookedBird", "요리된 새" },
                    new[] { "Airline Food", "Airline Meal", "기내식" },
                    new[] { "Honeycomb Honey", "Honey", "벌집꿀" },
                    new[] { "Scout Cookie", "Scout Snack", "스카우트 과자" },
                    new[] { "Red Mushroom Berry", "Red MushroomBerry", "빨간색 버섯 열매" });

            List<ushort> uniqueFood =
                BuildResolvedPool(
                    database,
                    new[] { "Pandora's Box", "Pandora Box", "판도라의 상자" },
                    new[] { "Sleep Berry", "SleepBerry", "수면 열매" },
                    new[] { "Pop Pop", "Bubble Wrap", "뾱뾱이" });

            List<ushort> commonHeal =
                BuildResolvedPool(
                    database,
                    new[] { "Bandage", "붕대" });

            List<ushort> normalHeal =
                BuildResolvedPool(
                    database,
                    new[] { "First Aid Kit", "Medkit", "구급상자" });

            List<ushort> legendaryHeal =
                BuildResolvedPool(
                    database,
                    new[] { "Cure-All", "Panacea", "만병통치약" });

            NextCampfireRecipes.Clear();
            PlanePartRecipes.Clear();

            int seed =
                sharedRecipeSeed;

            AddRandomCampfireRecipe(
                1,
                "첫 번째 다음 모닥불",
                0,
                30,
                seed + 101,
                new PoolRequest(commonSale, 2),
                new PoolRequest(commonUtility, 1),
                new PoolRequest(commonClimbing, 1));

            AddRandomCampfireRecipe(
                2,
                "두 번째 다음 모닥불",
                1,
                110,
                seed + 202,
                new PoolRequest(normalSale, 2),
                new PoolRequest(normalUtility, 1),
                new PoolRequest(normalClimbing, 1),
                new PoolRequest(
                    MergePools(normalFood, normalHeal),
                    1));

            AddRandomCampfireRecipe(
                3,
                "세 번째 다음 모닥불",
                2,
                275,
                seed + 303,
                new PoolRequest(rareSale, 1),
                new PoolRequest(rareUtility, 1),
                new PoolRequest(normalClimbing, 2),
                new PoolRequest(rareFood, 1));

            AddRandomCampfireRecipe(
                4,
                "네 번째 다음 모닥불",
                3,
                600,
                seed + 404,
                new PoolRequest(uniqueSale, 1),
                new PoolRequest(uniqueUtility, 1),
                new PoolRequest(uniqueClimbing, 1),
                new PoolRequest(uniqueFood, 1),
                new PoolRequest(rareSale, 1));

            AddRandomPlanePartRecipe(
                0,
                "연료 제어 모듈",
                "해안 → 열대/뿌리숲",
                0,
                65,
                seed + 501,
                new PoolRequest(commonSale, 2),
                new PoolRequest(commonUtility, 1));

            AddRandomPlanePartRecipe(
                1,
                "날개 연결 모듈",
                "열대/뿌리숲 → 메사/고산지대",
                1,
                165,
                seed + 502,
                new PoolRequest(normalSale, 2),
                new PoolRequest(normalUtility, 1));

            AddRandomPlanePartRecipe(
                2,
                "고도 조절 모듈",
                "메사/고산지대 → 칼데라",
                2,
                360,
                seed + 503,
                new PoolRequest(rareSale, 1),
                new PoolRequest(rareUtility, 1),
                new PoolRequest(normalSale, 1));

            AddRandomPlanePartRecipe(
                3,
                "내열 추진 모듈",
                "칼데라 → 가마",
                4,
                800,
                seed + 504,
                new PoolRequest(uniqueSale, 1),
                new PoolRequest(legendaryUtility, 1),
                new PoolRequest(legendarySale, 1));

            progressionRecipeRunId =
                runId;

            progressionRecipesBuilt =
                NextCampfireRecipes.Count == 4 &&
                PlanePartRecipes.Count == 4;

            if (progressionRecipesBuilt)
            {
                Logger.LogInfo(
                    "Run-random progression recipes built. RunId=" +
                    runId);
            }

            return progressionRecipesBuilt;
        }

        private sealed class PoolRequest
        {
            public readonly List<ushort> Pool;
            public readonly int Count;

            public PoolRequest(
                List<ushort> pool,
                int count)
            {
                Pool = pool ??
                    new List<ushort>();

                Count = Mathf.Max(1, count);
            }
        }

        private void AddRandomCampfireRecipe(
            int stage,
            string name,
            int requiredResourceLevel,
            int moneyCost,
            int seed,
            params PoolRequest[] requests)
        {
            List<IngredientCost> ingredients =
                BuildRandomIngredients(
                    seed,
                    requests);

            NextCampfireRecipes.Add(
                new CampfireBuildRecipe(
                    stage,
                    name,
                    requiredResourceLevel,
                    moneyCost,
                    ingredients.ToArray()));
        }

        private void AddRandomPlanePartRecipe(
            int index,
            string name,
            string route,
            int requiredResourceLevel,
            int moneyCost,
            int seed,
            params PoolRequest[] requests)
        {
            List<IngredientCost> ingredients =
                BuildRandomIngredients(
                    seed,
                    requests);

            PlanePartRecipes.Add(
                new PlanePartRecipe(
                    index,
                    name,
                    route,
                    requiredResourceLevel,
                    moneyCost,
                    ingredients.ToArray()));
        }

        private static List<IngredientCost> BuildRandomIngredients(
            int seed,
            params PoolRequest[] requests)
        {
            List<IngredientCost> result =
                new List<IngredientCost>();

            HashSet<ushort> used =
                new HashSet<ushort>();

            System.Random random =
                new System.Random(seed);

            if (requests == null)
            {
                return result;
            }

            for (int requestIndex = 0;
                 requestIndex < requests.Length;
                 requestIndex++)
            {
                PoolRequest request =
                    requests[requestIndex];

                if (request == null ||
                    request.Pool == null ||
                    request.Pool.Count == 0)
                {
                    continue;
                }

                List<ushort> candidates =
                    new List<ushort>(
                        request.Pool);

                for (int count = 0;
                     count < request.Count &&
                     candidates.Count > 0;
                     count++)
                {
                    int selectedIndex =
                        random.Next(
                            candidates.Count);

                    ushort itemId =
                        candidates[selectedIndex];

                    candidates.RemoveAt(
                        selectedIndex);

                    if (itemId == 0 ||
                        !used.Add(itemId))
                    {
                        count--;
                        continue;
                    }

                    result.Add(
                        new IngredientCost(
                            itemId,
                            1));
                }
            }

            return result;
        }

        private static List<ushort> BuildResolvedPool(
            ItemDatabase database,
            params string[][] aliasGroups)
        {
            List<ushort> result =
                new List<ushort>();

            if (aliasGroups == null)
            {
                return result;
            }

            for (int i = 0;
                 i < aliasGroups.Length;
                 i++)
            {
                ushort itemId =
                    ResolveProgressionItemId(
                        database,
                        aliasGroups[i]);

                if (itemId != 0 &&
                    !result.Contains(itemId))
                {
                    result.Add(itemId);
                }
            }

            result.Sort();

            return result;
        }

        private static List<ushort> MergePools(
            params List<ushort>[] pools)
        {
            List<ushort> result =
                new List<ushort>();

            if (pools == null)
            {
                return result;
            }

            for (int i = 0;
                 i < pools.Length;
                 i++)
            {
                List<ushort> pool =
                    pools[i];

                if (pool == null)
                {
                    continue;
                }

                for (int j = 0;
                     j < pool.Count;
                     j++)
                {
                    ushort itemId =
                        pool[j];

                    if (itemId != 0 &&
                        !result.Contains(itemId))
                    {
                        result.Add(itemId);
                    }
                }
            }

            result.Sort();

            return result;
        }

        private static int StableProgressionSeed(
            string value)
        {
            unchecked
            {
                int hash =
                    17;

                string safe =
                    value ??
                    string.Empty;

                for (int i = 0;
                     i < safe.Length;
                     i++)
                {
                    hash =
                        hash *
                        31 +
                        safe[i];
                }

                return hash;
            }
        }

        private static ushort ResolveProgressionItemId(
            ItemDatabase database,
            params string[] aliases)
        {
            if (database == null ||
                database.itemLookup == null ||
                aliases == null)
            {
                return 0;
            }

            string[] normalizedAliases =
                new string[aliases.Length];

            for (int i = 0;
                 i < aliases.Length;
                 i++)
            {
                normalizedAliases[i] =
                    NormalizeProgressionName(
                        aliases[i]);
            }

            // 내부 오브젝트명 또는 표시 이름의 정규화된 완전 일치를 먼저 찾습니다.
            foreach (KeyValuePair<ushort, Item> pair
                in database.itemLookup)
            {
                Item item =
                    pair.Value;

                if (item == null)
                {
                    continue;
                }

                string objectName =
                    item.gameObject != null
                        ? item.gameObject.name
                        : string.Empty;

                string displayName =
                    GetItemDisplayName(
                        item);

                string objectKey =
                    NormalizeProgressionName(
                        objectName);

                string displayKey =
                    NormalizeProgressionName(
                        displayName);

                for (int i = 0;
                     i < normalizedAliases.Length;
                     i++)
                {
                    string aliasKey =
                        normalizedAliases[i];

                    if (string.IsNullOrEmpty(
                            aliasKey))
                    {
                        continue;
                    }

                    if (objectKey ==
                            aliasKey ||
                        displayKey ==
                            aliasKey)
                    {
                        return pair.Key;
                    }
                }
            }

            // 완전 일치 항목이 없으면 정규화된 부분 일치 항목을 찾습니다.
            foreach (KeyValuePair<ushort, Item> pair
                in database.itemLookup)
            {
                Item item =
                    pair.Value;

                if (item == null)
                {
                    continue;
                }

                string objectName =
                    item.gameObject != null
                        ? item.gameObject.name
                        : string.Empty;

                string displayName =
                    GetItemDisplayName(
                        item);

                string objectKey =
                    NormalizeProgressionName(
                        objectName);

                string displayKey =
                    NormalizeProgressionName(
                        displayName);

                for (int i = 0;
                     i < normalizedAliases.Length;
                     i++)
                {
                    string aliasKey =
                        normalizedAliases[i];

                    if (string.IsNullOrEmpty(
                            aliasKey))
                    {
                        continue;
                    }

                    if (objectKey.Contains(
                            aliasKey) ||
                        displayKey.Contains(
                            aliasKey))
                    {
                        return pair.Key;
                    }
                }
            }

            return 0;
        }

        private static string NormalizeProgressionName(
            string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            StringBuilder builder =
                new StringBuilder(
                    value.Length);

            for (int i = 0;
                 i < value.Length;
                 i++)
            {
                char character =
                    value[i];

                if (char.IsLetterOrDigit(
                        character))
                {
                    builder.Append(
                        char.ToLowerInvariant(
                            character));
                }
            }

            return builder.ToString();
        }

        private static int GetRequiredModuleMaskForCampfireStage(
            int stage)
        {
            int safeStage =
                Mathf.Clamp(
                    stage,
                    1,
                    4);

            return
                (1 <<
                    safeStage) -
                1;
        }

        private bool HasRequiredModulesForCampfireStage(
            int stage,
            out string message)
        {
            message =
                string.Empty;

            if (!partsStateLoaded ||
                partsRoomStateDirty)
            {
                LoadPartsStateOnDemand();
            }

            int requiredMask =
                GetRequiredModuleMaskForCampfireStage(
                    stage);

            int missingMask =
                requiredMask &
                ~purchasedPartsMask;

            if (missingMask ==
                0)
            {
                return true;
            }

            StringBuilder builder =
                new StringBuilder();

            builder.Append(
                stage);

            builder.Append(
                "번째 다음 모닥불 제작에는 다음 비행기 모듈 구매가 필요합니다.");

            for (int i = 0;
                 i < 4;
                 i++)
            {
                int bit =
                    1 <<
                    i;

                if ((missingMask &
                        bit) ==
                    0)
                {
                    continue;
                }

                builder.Append(
                    "\n- ");

                if (i >= 0 &&
                    i < PlanePartRecipes.Count)
                {
                    builder.Append(
                        PlanePartRecipes[i]
                            .Name);
                }
                else
                {
                    builder.Append(
                        GetPlaneModuleFallbackName(
                            i));
                }
            }

            message =
                builder.ToString();

            return false;
        }

        private static string GetPlaneModuleFallbackName(
            int index)
        {
            switch (index)
            {
                case 0:
                    return "연료 제어 모듈";

                case 1:
                    return "날개 연결 모듈";

                case 2:
                    return "고도 조절 모듈";

                case 3:
                    return "내열 추진 모듈";

                default:
                    return "알 수 없는 모듈";
            }
        }

        private string BuildCampfireModuleRequirementText(
            int stage)
        {
            if (!partsStateLoaded ||
                partsRoomStateDirty)
            {
                LoadPartsStateOnDemand();
            }

            int requiredMask =
                GetRequiredModuleMaskForCampfireStage(
                    stage);

            StringBuilder builder =
                new StringBuilder();

            builder.Append(
                "필요 비행기 모듈");

            for (int i = 0;
                 i < stage &&
                 i < 4;
                 i++)
            {
                int bit =
                    1 <<
                    i;

                bool purchased =
                    (purchasedPartsMask &
                        bit) !=
                    0;

                builder.Append(
                    "\n");

                builder.Append(
                    purchased
                        ? "<color=#79E081>"
                        : "<color=#FF8A80>");

                if (i >= 0 &&
                    i < PlanePartRecipes.Count)
                {
                    builder.Append(
                        PlanePartRecipes[i]
                            .Name);
                }
                else
                {
                    builder.Append(
                        GetPlaneModuleFallbackName(
                            i));
                }

                builder.Append(
                    purchased
                        ? " 구매 완료"
                        : " 미구매");

                builder.Append(
                    "</color>");
            }

            return
                builder.ToString();
        }

        private CampfireBuildRecipe GetNextCampfireRecipe()
        {
            EnsureProgressionRecipesBuilt();

            int stage =
                upgradeState.CampfireLevel +
                1;

            return
                stage >= 1 &&
                stage <= NextCampfireRecipes.Count
                    ? NextCampfireRecipes[
                        stage -
                        1]
                    : null;
        }

        private static CraftRecipe MakeTemporaryRecipe(
            IList<IngredientCost> ingredients)
        {
            CraftRecipe result =
                new CraftRecipe();

            for (int i = 0;
                 ingredients != null &&
                 i < ingredients.Count;
                 i++)
            {
                IngredientCost ingredient =
                    ingredients[i];

                result.Ingredients.Add(
                    new IngredientCost(
                        ingredient.ItemId,
                        ingredient.Count));
            }

            return result;
        }

        private PlanePartRecipe SelectedPartRecipe
        {
            get
            {
                EnsureProgressionRecipesBuilt();

                return
                    selectedPartIndex >=
                        0 &&
                    selectedPartIndex <
                        PlanePartRecipes.Count
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
                    PlanePartRecipes.Count)
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
            EnsureProgressionRecipesBuilt();
            if (partIndex <
                    0 ||
                partIndex >=
                    PlanePartRecipes.Count)
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
            EnsureProgressionRecipesBuilt();
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
            EnsureProgressionRecipesBuilt();
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
                    PlanePartRecipes.Count)
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

            int requiredCampfireStage =
                segment +
                1;

            int completedCampfireStage =
                Mathf.Clamp(
                    ReadRoomInt(
                        UpgradeCampfireKey,
                        CampfireUpgradeLevel),
                    0,
                    CampfireUpgradeMaximum);

            if (completedCampfireStage <
                requiredCampfireStage)
            {
                message =
                    "모닥불 점화 조건 미충족\nP 메뉴 강화 탭의 다음 모닥불 제작에서 " +
                    requiredCampfireStage +
                    "단계 모닥불을 먼저 제작하세요.";

                return false;
            }

            int requiredGrade =
                Mathf.Clamp(
                    segment,
                    0,
                    ResourceUpgradeMaximum);

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
                Mathf.Clamp(
                    segment,
                    0,
                    ResourceUpgradeMaximum);

            int currentGrade =
                Instance.upgradeState
                    .ResourceLevel;

            int bit =
                1 <<
                segment;

            int requiredModuleMask =
                GetRequiredModuleMaskForCampfireStage(
                    segment +
                    1);

            bool purchased =
                (Instance.purchasedPartsMask &
                    requiredModuleMask) ==
                requiredModuleMask;

            bool gradeReady =
                currentGrade >=
                    requiredGrade;

            bool nextCampfireReady =
                Instance.upgradeState
                    .CampfireLevel >=
                segment + 1;

            string color =
                purchased &&
                gradeReady &&
                nextCampfireReady
                    ? "#79E081"
                    : "#FF8A80";

            return
                "\n<color=" +
                color +
                ">진행 조건: " +
                GetResourceGradeName(
                    requiredGrade) +
                " 제작 단계 | 다음 모닥불 " +
                (
                    nextCampfireReady
                        ? "제작 완료"
                        : "미제작"
                ) +
                " | 비행기 부품 " +
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

            // 점화 뒤 세그먼트가 바뀔 수 있으므로 호출자가 보존한 출발 구간을 사용합니다.
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
        // 제작식 생성과 제작 탭 데이터
        // -----------------------------------------------------------------

        private bool EnsureCraftRecipesBuilt()
        {
            if (!EnsureSharedRecipeSeed())
            {
                Logger.LogWarning(
                    "CraftHub is waiting for the host shared recipe seed.");

                return false;
            }

            if (craftRecipes.Count > 0)
            {
                return true;
            }

            ItemDatabase database =
                SingletonAsset<ItemDatabase>.Instance;

            if (database == null ||
                database.itemLookup == null ||
                database.itemLookup.Count == 0)
            {
                Logger.LogWarning(
                    "CraftHub could not build recipes because ItemDatabase is not ready.");

                return false;
            }

            craftRecipes.Clear();
            craftRecipesByOutputId.Clear();

            List<ushort> commonSale =
                BuildResolvedPool(
                    database,
                    new[] { "FireWood", "Fire Wood", "나뭇가지" },
                    new[] { "Stone", "Rock", "돌" },
                    new[] { "Conch", "Shell", "소라고둥", "소라고동" });

            List<ushort> normalSale =
                BuildResolvedPool(
                    database,
                    new[] { "Binoculars", "망원경" },
                    new[] { "Bing Bong", "BingBong", "빙봉" },
                    new[] { "Bugle", "나팔" },
                    new[] { "Frisbee", "Flying Disc", "플라잉디스크" });

            List<ushort> rareSale =
                BuildResolvedPool(
                    database,
                    new[] { "Guidebook", "Guide Book", "가이드북" },
                    new[] { "Scroll", "스크롤" });

            List<ushort> uniqueSale =
                BuildResolvedPool(
                    database,
                    new[] { "Weird Shroom", "WeirdShroom", "괴상 버섯" });

            List<ushort> legendarySale =
                BuildResolvedPool(
                    database,
                    new[] { "Strange Gem", "StrangeGem", "이상한 보석" });

            List<ushort> commonOutputs =
                new List<ushort>();

            List<ushort> normalOutputs =
                new List<ushort>();

            List<ushort> rareOutputs =
                new List<ushort>();

            List<ushort> uniqueOutputs =
                new List<ushort>();

            AddNamedCraftGroup(
                database,
                "음식",
                RecipeTier.Basic,
                0,
                4,
                9,
                commonSale,
                null,
                commonOutputs,
                new[] { "Red Crispberry", "Red Crisp Berry", "빨간색 아삭 열매" },
                new[] { "Coconut Half", "Half Coconut", "코코넛 반쪽" },
                new[] { "Trail Mix", "TrailMix", "트레일 믹스" },
                new[] { "Yellow Berrynana", "Yellow Banana", "노란색 열매나나" },
                new[] { "Blue Mushroom Berry", "Blue MushroomBerry", "파란색 버섯열매" },
                new[] { "Sports Drink", "SportsDrink", "스포츠 드링크" });

            AddNamedCraftGroup(
                database,
                "등산 장비",
                RecipeTier.Basic,
                0,
                7,
                13,
                commonSale,
                commonOutputs,
                commonOutputs,
                new[] { "Backpack", "배낭" },
                new[] { "Piton", "피톤" },
                new[] { "Energy Drink", "EnergyDrink", "에너지 드링크" },
                new[] { "Balloon", "풍선" },
                new[] { "Portable Stove", "PortableStove", "휴대용 스토브" });

            AddNamedCraftGroup(
                database,
                "기타 아이템",
                RecipeTier.Basic,
                0,
                5,
                11,
                commonSale,
                commonOutputs,
                commonOutputs,
                new[] { "Pirate Compass", "PirateCompass", "해적 나침반" },
                new[] { "Snow", "눈" },
                new[] { "Aloe Vera", "AloeVera", "알로에 베라" },
                new[] { "Heat Pack", "HeatPack", "핫팩" },
                new[] { "Torch", "횃불" });

            AddNamedCraftGroup(
                database,
                "회복",
                RecipeTier.Basic,
                0,
                8,
                8,
                commonSale,
                null,
                commonOutputs,
                new[] { "Bandage", "붕대" });

            AddNamedCraftGroup(
                database,
                "음식",
                RecipeTier.Standard,
                1,
                14,
                25,
                MergePools(commonSale, normalSale),
                commonOutputs,
                normalOutputs,
                new[] { "Tick", "진드기" },
                new[] { "Red Clusterberry", "Red Cluster Berry", "빨간 송송열매" },
                new[] { "Kingberry Green", "KingberryGreen", "녹색 대왕열매" },
                new[] { "FortifiedMilk", "Fortified Milk", "강화 우유", "강화우유" },
                new[] { "Marshmallow", "마시멜로우" },
                new[] { "Granola Bar", "GranolaBar", "그래놀라바" },
                new[] { "Puff Mushroom", "PuffMushroom", "통통버섯" },
                new[] { "Trumpet Mushroom", "TrumpetMushroom", "나팔버섯" },
                new[] { "Bundle Mushroom", "BundleMushroom", "다발버섯" },
                new[] { "Button Mushroom", "ButtonMushroom", "단추버섯" },
                new[] { "Orange Winterberry", "Orange Winter Berry", "주황 겨울열매" },
                new[] { "Red Thornberry", "Red Thorn Berry", "빨간 가시열매" },
                new[] { "Purple Mushroom Berry", "Purple MushroomBerry", "보라색 버섯열매" });

            AddNamedCraftGroup(
                database,
                "등산 장비",
                RecipeTier.Standard,
                1,
                22,
                34,
                normalSale,
                commonOutputs,
                normalOutputs,
                new[] { "Shelf Fungus", "ShelfFungus", "선반 균류" },
                new[] { "Cloud Fungus", "CloudFungus", "구름균류" },
                new[] { "Rope Spool", "RopeSpool", "밧줄타래" },
                new[] { "Bounce Fungus", "BounceFungus", "방방 균류" },
                new[] { "Checkpoint Flag", "CheckpointFlag", "체크포인트 깃발" });

            AddNamedCraftGroup(
                database,
                "기타 아이템",
                RecipeTier.Standard,
                1,
                18,
                29,
                normalSale,
                commonOutputs,
                normalOutputs,
                new[] { "Lantern", "랜턴" },
                new[] { "Antidote", "해독제" },
                new[] { "Rainbow Candy", "RainbowCandy", "무지개사탕" },
                new[] { "Parasol", "파라솔" },
                new[] { "Sunscreen", "선크림" });

            AddNamedCraftGroup(
                database,
                "회복",
                RecipeTier.Standard,
                1,
                28,
                28,
                normalSale,
                commonOutputs,
                normalOutputs,
                new[] { "First Aid Kit", "First-Aid Kit", "Medkit", "구급상자" });

            AddNamedCraftGroup(
                database,
                "음식",
                RecipeTier.Advanced,
                2,
                42,
                64,
                rareSale,
                normalOutputs,
                rareOutputs,
                new[] { "Hot Dog", "Hotdog", "핫도그" },
                new[] { "Cooked Bird", "CookedBird", "요리된 새" },
                new[] { "Airline Food", "Airline Meal", "기내식" },
                new[] { "Honeycomb Honey", "Honey", "벌집꿀" },
                new[] { "Scout Cookie", "Scout Snack", "스카우트 과자" },
                new[] { "Red Mushroom Berry", "Red MushroomBerry", "빨간색 버섯 열매" });

            AddNamedCraftGroup(
                database,
                "기타 아이템",
                RecipeTier.Advanced,
                2,
                58,
                78,
                rareSale,
                normalOutputs,
                rareOutputs,
                new[] { "Cactus", "선인장" },
                new[] { "Dynamite", "다이너마이트" },
                new[] { "Scout Cannon", "ScoutCannon", "스카우트 캐논" });

            AddScoutStatueRecipe(
                database,
                rareOutputs);

            AddNamedCraftGroup(
                database,
                "음식",
                RecipeTier.Special,
                3,
                90,
                130,
                MergePools(rareSale, uniqueSale),
                rareOutputs,
                uniqueOutputs,
                new[] { "Pandora's Box", "Pandora Box", "판도라의 상자" },
                new[] { "Sleep Berry", "SleepBerry", "수면 열매" },
                new[] { "Pop Pop", "Bubble Wrap", "뾱뾱이" });

            AddNamedCraftGroup(
                database,
                "등산 장비",
                RecipeTier.Special,
                3,
                120,
                170,
                MergePools(rareSale, uniqueSale),
                normalOutputs,
                uniqueOutputs,
                new[] { "Balloon Bunch", "Bunch of Balloons", "풍선 다발" },
                new[] { "Rescue Hook", "RescueHook", "구조갈고리" },
                new[] { "Chain Launcher", "ChainLauncher", "사슬발사기" },
                new[] { "Magic Bean", "MagicBean", "마법의 콩" },
                new[] { "Rope Cannon", "RopeCannon", "밧줄총" });

            AddNamedCraftGroup(
                database,
                "기타 아이템",
                RecipeTier.Special,
                3,
                115,
                160,
                MergePools(rareSale, uniqueSale),
                rareOutputs,
                uniqueOutputs,
                new[] { "Scoutmaster Bugle", "Scoutmaster Horn", "스카우트지도자의 나팔" },
                new[] { "Cursed Skull", "CursedSkull", "저주받은 해골" },
                new[] { "Fairy Lantern", "FairyLantern", "요정랜턴" });

            AddNamedCraftGroup(
                database,
                "등산 장비",
                RecipeTier.Masterwork,
                4,
                230,
                330,
                legendarySale,
                uniqueOutputs,
                null,
                new[] { "Book of Bones", "Bone Book", "뼈의서", "뼈의 서" },
                new[] { "Anti-Rope Cannon", "Reverse Rope Cannon", "반전 밧줄총" },
                new[] { "Anti-Rope Spool", "Reverse Rope Spool", "반전 밧줄타래" },
                new[] { "Friendship Bugle", "Friendship Horn", "우정 나팔" });

            AddNamedCraftGroup(
                database,
                "기타 아이템",
                RecipeTier.Masterwork,
                4,
                240,
                340,
                legendarySale,
                uniqueOutputs,
                null,
                new[] { "Golden Bing Bong", "GoldenBingBong", "황금 빙봉" });

            AddNamedCraftGroup(
                database,
                "회복",
                RecipeTier.Masterwork,
                4,
                280,
                280,
                legendarySale,
                uniqueOutputs,
                null,
                new[] { "Cure-All", "Cure All", "Panacea", "만병통치약" });

            // ItemID 32를 최고급 최종 탈출 제작식으로 추가합니다.
            AddExplicitRecipe(
                database,
                FlareItemId,
                "최종 탈출",
                RecipeTier.Masterwork,
                4,
                500,
                new IngredientCost(StrangeGemItemId, 1),
                new IngredientCost(WeirdShroomItemId, 1),
                PickIngredient(uniqueOutputs, FlareItemId, 1),
                PickIngredient(rareOutputs, FlareItemId, 2));

            craftRecipes.Sort(
                CompareRecipes);

            Logger.LogInfo(
                "CraftHub full categorized crafting catalog built. Count=" +
                craftRecipes.Count +
                ".");

            LogVerifiedCraftItemMapping(
                ScoutEffigyItemId,
                "부활 / 스카우트 인형");

            LogVerifiedCraftItemMapping(
                56,
                "음식 / 녹색 대왕열매");

            LogVerifiedCraftItemMapping(
                152,
                "음식 / 강화 우유");

            LogScoutEffigyFinalState(
                "Craft catalog built");

            return craftRecipes.Count > 0;
        }

        private void LogVerifiedCraftItemMapping(
            ushort itemId,
            string expectedCategory)
        {
            CraftRecipe recipe;

            if (craftRecipesByOutputId.TryGetValue(
                    itemId,
                    out recipe) &&
                recipe != null)
            {
                Logger.LogInfo(
                    "Verified craft item mapping. ItemID=" +
                    itemId +
                    " | Name=" +
                    recipe.DisplayName +
                    " | ExpectedUI=" +
                    expectedCategory);

                return;
            }

            Logger.LogWarning(
                "Expected craft item was not generated. ItemID=" +
                itemId +
                " | ExpectedUI=" +
                expectedCategory);
        }

        private void AddNamedCraftGroup(
            ItemDatabase database,
            string category,
            RecipeTier tier,
            int requiredResourceLevel,
            int minimumMoney,
            int maximumMoney,
            List<ushort> salePool,
            List<ushort> previousCraftPool,
            List<ushort> outputCollector,
            params string[][] aliasGroups)
        {
            if (aliasGroups == null)
            {
                return;
            }

            for (int i = 0;
                 i < aliasGroups.Length;
                 i++)
            {
                ushort outputItemId =
                    ResolveProgressionItemId(
                        database,
                        aliasGroups[i]);

                if (outputItemId == 0 ||
                    IsSaleResourceId(outputItemId) ||
                    craftRecipesByOutputId.ContainsKey(
                        outputItemId))
                {
                    continue;
                }

                int seed =
                    GetDeterministicSeed(
                        outputItemId,
                        category);

                int moneyRange =
                    Mathf.Max(
                        0,
                        maximumMoney -
                        minimumMoney);

                int moneyCost =
                    minimumMoney +
                    (
                        moneyRange > 0
                            ? PositiveModulo(
                                seed,
                                moneyRange + 1)
                            : 0
                    );

                List<IngredientCost> ingredients =
                    new List<IngredientCost>();

                int saleCount =
                    requiredResourceLevel <= 0
                        ? 2
                        : (
                            requiredResourceLevel == 1
                                ? 2
                                : 1
                        );

                AddPickedIngredients(
                    ingredients,
                    salePool,
                    outputItemId,
                    seed,
                    saleCount);

                if (requiredResourceLevel > 0)
                {
                    AddPickedIngredients(
                        ingredients,
                        previousCraftPool,
                        outputItemId,
                        seed / 3 + 17,
                        requiredResourceLevel >= 3
                            ? 2
                            : 1);
                }

                if (requiredResourceLevel >= 2)
                {
                    AddPickedIngredients(
                        ingredients,
                        CommonIds != null
                            ? new List<ushort>(CommonIds)
                            : null,
                        outputItemId,
                        seed / 7 + 31,
                        1);
                }

                AddExplicitRecipe(
                    database,
                    outputItemId,
                    category,
                    tier,
                    requiredResourceLevel,
                    moneyCost,
                    ingredients.ToArray());

                if (outputCollector != null &&
                    !outputCollector.Contains(
                        outputItemId))
                {
                    outputCollector.Add(
                        outputItemId);
                }
            }
        }

        private void AddScoutStatueRecipe(
            ItemDatabase database,
            List<ushort> outputCollector)
        {
            ushort itemId =
                ResolveProgressionItemId(
                    database,
                    "ScoutEffigy",
                    "Scout Effigy",
                    "스카우트 인형",
                    "Scout Statue",
                    "Scout Statue Item",
                    "Scoutmaster Statue",
                    "Scout Effigy Item",
                    "Scoutmaster Effigy",
                    "Effigy",
                    "Revive Statue",
                    "Resurrection Statue",
                    "스카우트 석상",
                    "스카우트석상",
                    "스카우트 조각상",
                    "부활 석상",
                    "부활석상");

            // 이름 별칭으로 찾지 못하면 ItemDatabase의 ItemID 67 항목을 사용합니다.
            Item directPrefab;

            if (itemId == 0 &&
                database != null &&
                database.itemLookup != null &&
                database.itemLookup.TryGetValue(
                    ScoutEffigyItemId,
                    out directPrefab) &&
                directPrefab != null)
            {
                itemId =
                    ScoutEffigyItemId;

                Logger.LogInfo(
                    "[CraftCategoryDiag] ScoutEffigy resolved by fixed ItemID fallback. ItemID=" +
                    itemId +
                    " | DisplayName=" +
                    GetItemDisplayName(
                        directPrefab) +
                    " | ObjectName=" +
                    (
                        directPrefab.gameObject != null
                            ? directPrefab.gameObject.name
                            : "<null>"
                    ));
            }

            if (itemId == 0)
            {
                Logger.LogError(
                    "[CraftCategoryDiag] ScoutEffigy recipe was not created because no matching item was found.");

                return;
            }

            if (craftRecipesByOutputId.ContainsKey(
                    itemId))
            {
                Logger.LogWarning(
                    "[CraftCategoryDiag] ScoutEffigy recipe already exists before AddScoutStatueRecipe. ItemID=" +
                    itemId);

                return;
            }

            AddExplicitRecipe(
                database,
                itemId,
                "특수",
                RecipeTier.Advanced,
                2,
                100);

            CraftRecipe createdRecipe;

            if (craftRecipesByOutputId.TryGetValue(
                    itemId,
                    out createdRecipe) &&
                createdRecipe != null)
            {
                Logger.LogInfo(
                    "[CraftCategoryDiag] ScoutEffigy recipe created. ItemID=" +
                    itemId +
                    " | DisplayName=" +
                    createdRecipe.DisplayName +
                    " | SourceCategory=" +
                    createdRecipe.Category +
                    " | UiCategory=" +
                    GetCraftUiCategoryName(
                        GetCraftUiCategory(
                            createdRecipe)) +
                    " | Price=" +
                    createdRecipe.MoneyCost);
            }
            else
            {
                Logger.LogError(
                    "[CraftCategoryDiag] AddExplicitRecipe returned without registering ScoutEffigy. ItemID=" +
                    itemId);
            }

            if (outputCollector != null)
            {
                outputCollector.Add(itemId);
            }
        }

        private static void AddPickedIngredients(
            List<IngredientCost> destination,
            List<ushort> pool,
            ushort excludedItemId,
            int seed,
            int wantedCount)
        {
            if (destination == null ||
                pool == null ||
                pool.Count == 0 ||
                wantedCount <= 0)
            {
                return;
            }

            HashSet<ushort> alreadyUsed =
                new HashSet<ushort>();

            for (int i = 0;
                 i < destination.Count;
                 i++)
            {
                alreadyUsed.Add(
                    destination[i].ItemId);
            }

            for (int offset = 0;
                 offset < pool.Count &&
                 wantedCount > 0;
                 offset++)
            {
                ushort candidate =
                    pool[
                        PositiveModulo(
                            seed + offset * 13,
                            pool.Count)];

                if (candidate == 0 ||
                    candidate == excludedItemId ||
                    !alreadyUsed.Add(candidate))
                {
                    continue;
                }

                destination.Add(
                    new IngredientCost(
                        candidate,
                        1));

                wantedCount--;
            }
        }

        private static IngredientCost PickIngredient(
            List<ushort> pool,
            ushort excludedItemId,
            int seed)
        {
            if (pool == null ||
                pool.Count == 0)
            {
                return
                    new IngredientCost(
                        FireWoodItemId,
                        1);
            }

            for (int i = 0;
                 i < pool.Count;
                 i++)
            {
                ushort itemId =
                    pool[
                        PositiveModulo(
                            seed + i,
                            pool.Count)];

                if (itemId != 0 &&
                    itemId != excludedItemId)
                {
                    return
                        new IngredientCost(
                            itemId,
                            1);
                }
            }

            return
                new IngredientCost(
                    FireWoodItemId,
                    1);
        }

        private static bool IsSaleResourceId(
            ushort itemId)
        {
            switch (itemId)
            {
                case FireWoodItemId:
                case StoneItemId:
                case ConchItemId:
                case BinocularsItemId:
                case BingBongItemId:
                case BugleItemId:
                case FrisbeeItemId:
                case GuidebookItemId:
                case ScrollItemId:
                case WeirdShroomItemId:
                case StrangeGemItemId:
                    return true;

                default:
                    return false;
            }
        }

        private int GetDeterministicSeed(
            ushort itemId,
            string category)
        {
            unchecked
            {
                // 호스트 공유 시드, 카테고리 문자열과 출력 ItemID로 제작식별 파생 시드를 계산합니다.
                int hash =
                    sharedRecipeSeedLoaded
                        ? sharedRecipeSeed
                        : 17;

                string safeCategory =
                    category ??
                    string.Empty;

                for (int i = 0;
                     i < safeCategory.Length;
                     i++)
                {
                    hash =
                        hash *
                        31 +
                        safeCategory[i];
                }

                hash =
                    hash *
                    31 +
                    itemId;

                return hash;
            }
        }

        private static int PositiveModulo(
            int value,
            int modulus)
        {
            if (modulus <= 0)
            {
                return 0;
            }

            int result =
                value %
                modulus;

            return
                result < 0
                    ? result + modulus
                    : result;
        }

        private void AddExplicitRecipe(
            ItemDatabase database,
            ushort outputItemId,
            string category,
            RecipeTier tier,
            int requiredResourceLevel,
            int moneyCost,
            params IngredientCost[] ingredients)
        {
            Item prefab;

            if (database == null ||
                database.itemLookup == null ||
                !database.itemLookup.TryGetValue(outputItemId, out prefab) ||
                prefab == null ||
                prefab.gameObject == null ||
                prefab.UIData == null)
            {
                Logger.LogWarning(
                    "CraftHub skipped explicit recipe because item was not found. ItemID=" +
                    outputItemId);

                return;
            }

            CraftRecipe recipe =
                new CraftRecipe
                {
                    OutputItemId = outputItemId,
                    OutputPrefab = prefab,
                    DisplayName = GetItemDisplayName(prefab),
                    Category = category ?? string.Empty,
                    Tier = tier,
                    RequiredResourceLevel =
                        Mathf.Clamp(
                            requiredResourceLevel,
                            0,
                            ResourceUpgradeMaximum),
                    MoneyCost = Mathf.Max(0, moneyCost)
                };

            if (ingredients != null)
            {
                for (int i = 0; i < ingredients.Length; i++)
                {
                    IngredientCost ingredient = ingredients[i];

                    if (ingredient == null)
                    {
                        continue;
                    }

                    AddIngredient(
                        recipe,
                        ingredient.ItemId,
                        ingredient.Count);
                }
            }

            craftRecipes.Add(recipe);
            craftRecipesByOutputId[outputItemId] = recipe;
        }

        private static int CompareRecipes(
            CraftRecipe left,
            CraftRecipe right)
        {
            int result =
                left.RequiredResourceLevel.CompareTo(
                    right.RequiredResourceLevel);

            if (result != 0)
            {
                return result;
            }

            result =
                left.Tier.CompareTo(
                    right.Tier);

            if (result != 0)
            {
                return result;
            }

            return
                string.Compare(
                    left.DisplayName,
                    right.DisplayName,
                    StringComparison.Ordinal);
        }

        private static void AddIngredient(
            CraftRecipe recipe,
            ushort itemId,
            int count)
        {
            if (recipe == null ||
                count <= 0)
            {
                return;
            }

            for (int i = 0;
                 i < recipe.Ingredients.Count;
                 i++)
            {
                if (recipe.Ingredients[i].ItemId ==
                    itemId)
                {
                    recipe.Ingredients[i].Count +=
                        count;

                    return;
                }
            }

            recipe.Ingredients.Add(
                new IngredientCost(
                    itemId,
                    count));
        }

        private static bool IsCraftIngredientId(
            ushort itemId)
        {
            return itemId != 0;
        }

        internal int CraftPage
        {
            get
            {
                return craftPage;
            }
        }

        internal CraftUiCategory SelectedCraftUiCategory
        {
            get
            {
                return selectedCraftUiCategory;
            }
        }

        internal int CraftTotalPages
        {
            get
            {
                int filteredCount =
                    GetFilteredCraftRecipeCount();

                return
                    filteredCount == 0
                        ? 1
                        : Mathf.CeilToInt(
                            (float)filteredCount /
                            CraftRecipesPerPage);
            }
        }

        internal CraftRecipe GetCraftRecipeAtCard(
            int cardIndex)
        {
            int filteredPosition =
                craftPage *
                CraftRecipesPerPage +
                cardIndex;

            int recipeIndex =
                GetRecipeIndexAtFilteredPosition(
                    filteredPosition);

            return
                recipeIndex >= 0 &&
                recipeIndex < craftRecipes.Count
                    ? craftRecipes[recipeIndex]
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

        internal void SelectCraftUiCategory(
            CraftUiCategory category)
        {
            if (selectedCraftUiCategory ==
                category)
            {
                return;
            }

            selectedCraftUiCategory =
                category;

            craftPage =
                0;

            selectedCraftRecipeIndex =
                GetRecipeIndexAtFilteredPosition(
                    0);

            LogCraftCategoryContents(
                category,
                "Category selected");

            SetTabStatus(
                HubTab.Craft,
                GetCraftUiCategoryName(
                    category) +
                " 제작 목록을 표시합니다.");

            RefreshWindow();
        }

        internal void SelectCraftCard(
            int cardIndex)
        {
            int filteredPosition =
                craftPage *
                CraftRecipesPerPage +
                cardIndex;

            int recipeIndex =
                GetRecipeIndexAtFilteredPosition(
                    filteredPosition);

            if (recipeIndex < 0 ||
                recipeIndex >=
                    craftRecipes.Count)
            {
                return;
            }

            selectedCraftRecipeIndex =
                recipeIndex;

            SetTabStatus(
                HubTab.Craft,
                craftRecipes[recipeIndex]
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
                GetRecipeIndexAtFilteredPosition(
                    craftPage *
                    CraftRecipesPerPage);

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
                GetRecipeIndexAtFilteredPosition(
                    craftPage *
                    CraftRecipesPerPage);

            RefreshWindow();
        }

        private int GetFilteredCraftRecipeCount()
        {
            int count =
                0;

            for (int i = 0;
                 i < craftRecipes.Count;
                 i++)
            {
                if (GetCraftUiCategory(
                        craftRecipes[i]) ==
                    selectedCraftUiCategory)
                {
                    count++;
                }
            }

            return count;
        }

        private int GetRecipeIndexAtFilteredPosition(
            int filteredPosition)
        {
            if (filteredPosition < 0)
            {
                return -1;
            }

            int currentPosition =
                0;

            for (int i = 0;
                 i < craftRecipes.Count;
                 i++)
            {
                if (GetCraftUiCategory(
                        craftRecipes[i]) !=
                    selectedCraftUiCategory)
                {
                    continue;
                }

                if (currentPosition ==
                    filteredPosition)
                {
                    return i;
                }

                currentPosition++;
            }

            return -1;
        }

        private static CraftUiCategory GetCraftUiCategory(
            CraftRecipe recipe)
        {
            if (recipe == null)
            {
                return CraftUiCategory.Essential;
            }

            // ItemID 67은 표시 이름과 관계없이 부활 카테고리로 분류합니다.
            if (recipe.OutputItemId ==
                ScoutEffigyItemId)
            {
                return CraftUiCategory.Revive;
            }

            string normalizedName =
                NormalizeCraftUiName(
                    recipe.DisplayName);

            if (MatchesCraftUiName(
                    normalizedName,
                    FoodCraftUiNames))
            {
                return CraftUiCategory.Food;
            }

            if (MatchesCraftUiName(
                    normalizedName,
                    HealCraftUiNames))
            {
                return CraftUiCategory.Heal;
            }

            if (MatchesCraftUiName(
                    normalizedName,
                    ReviveCraftUiNames))
            {
                return CraftUiCategory.Revive;
            }

            if (MatchesCraftUiName(
                    normalizedName,
                    ClimbingCraftUiNames))
            {
                return CraftUiCategory.Climbing;
            }

            if (MatchesCraftUiName(
                    normalizedName,
                    EssentialCraftUiNames))
            {
                return CraftUiCategory.Essential;
            }

            // 어느 이름 목록에도 일치하지 않는 제작식은 필수 카테고리에 표시합니다.
            return CraftUiCategory.Essential;
        }

        private static readonly string[] FoodCraftUiNames =
        {
            "빨간색아삭열매",
            "빨간아삭열매",
            "코코넛반쪽",
            "트레일믹스",
            "노란색열매나나",
            "노란열매나나",
            "파란색버섯열매",
            "파란버섯열매",
            "스포츠드링크",
            "진드기",
            "빨간송송열매",
            "녹색대왕열매",
            "강화우유",
            "마시멜로우",
            "그래놀라바",
            "통통버섯",
            "나팔버섯",
            "다발버섯",
            "단추버섯",
            "주황겨울열매",
            "빨간가시열매",
            "보라색버섯열매",
            "보라버섯열매",
            "핫도그",
            "요리된새",
            "기내식",
            "벌집꿀",
            "스카우트과자",
            "빨간색버섯열매",
            "빨간버섯열매",
            "판도라의상자",
            "수면열매",
            "뾱뾱이",
            "crispberry",
            "coconuthalf",
            "trailmix",
            "berrynana",
            "sportdrink",
            "tick",
            "marshmallow",
            "granolabar",
            "hotdog",
            "cookedbird",
            "airlinemeal",
            "honeycomb",
            "scoutcookies",
            "pandorasbox",
            "sleepberry"
        };

        private static readonly string[] ClimbingCraftUiNames =
        {
            "배낭",
            "피톤",
            "에너지드링크",
            "풍선",
            "휴대용스토브",
            "선반균류",
            "구름균류",
            "밧줄타래",
            "방방균류",
            "체크포인트깃발",
            "풍선다발",
            "구조갈고리",
            "사슬발사기",
            "마법의콩",
            "밧줄총",
            "뼈의서",
            "반전밧줄총",
            "반전밧줄타래",
            "우정나팔",
            "backpack",
            "piton",
            "balloon",
            "portablestove",
            "shelffungus",
            "cloudfungus",
            "rope",
            "coil",
            "bouncyfungus",
            "checkpointflag",
            "balloonbundle",
            "rescuehook",
            "chainlauncher",
            "magicbean",
            "ropecannon",
            "ropegun",
            "bookofbones",
            "invertedrope",
            "friendshipbugle"
        };

        private static readonly string[] HealCraftUiNames =
        {
            "붕대",
            "구급상자",
            "만병통치약",
            "bandage",
            "firstaidkit",
            "medkit",
            "cureall",
            "panacea"
        };

        private static readonly string[] ReviveCraftUiNames =
        {
            "스카우트인형",
            "스카우트석상",
            "스카우트조각상",
            "부활인형",
            "부활석상",
            "생명의석상",
            "scouteffigy",
            "scoutstatue",
            "effigy",
            "revive",
            "resurrection"
        };

        private static readonly string[] EssentialCraftUiNames =
        {
            "해적나침반",
            "눈",
            "알로에베라",
            "핫팩",
            "횃불",
            "랜턴",
            "해독제",
            "무지개사탕",
            "파라솔",
            "선크림",
            "선인장",
            "다이너마이트",
            "스카우트캐논",
            "스카우트지도자의나팔",
            "저주받은해골",
            "요정랜턴",
            "황금빙봉",
            "조명탄",
            "piratecompass",
            "eye",
            "aloevera",
            "heatpack",
            "torch",
            "lantern",
            "antidote",
            "rainbowcandy",
            "parasol",
            "sunscreen",
            "cactus",
            "dynamite",
            "scoutcannon",
            "scoutmasterbugle",
            "cursedskull",
            "fairylantern",
            "goldenbingbong",
            "flare"
        };

        private static bool MatchesCraftUiName(
            string normalizedName,
            string[] candidates)
        {
            if (string.IsNullOrEmpty(
                    normalizedName) ||
                candidates == null)
            {
                return false;
            }

            for (int i = 0;
                 i < candidates.Length;
                 i++)
            {
                if (normalizedName.IndexOf(
                        candidates[i],
                        StringComparison.OrdinalIgnoreCase) >=
                    0)
                {
                    return true;
                }
            }

            return false;
        }

        private static string NormalizeCraftUiName(
            string value)
        {
            if (string.IsNullOrEmpty(
                    value))
            {
                return string.Empty;
            }

            StringBuilder builder =
                new StringBuilder(
                    value.Length);

            for (int i = 0;
                 i < value.Length;
                 i++)
            {
                char character =
                    value[i];

                if (char.IsLetterOrDigit(
                        character))
                {
                    builder.Append(
                        char.ToLowerInvariant(
                            character));
                }
            }

            return builder.ToString();
        }

        internal static string GetCraftUiCategoryName(
            CraftUiCategory category)
        {
            switch (category)
            {
                case CraftUiCategory.Climbing:
                    return "등산";

                case CraftUiCategory.Food:
                    return "음식";

                case CraftUiCategory.Heal:
                    return "힐";

                case CraftUiCategory.Revive:
                    return "부활";

                default:
                    return "필수";
            }
        }

        internal static Texture GetCraftRecipeIcon(
            CraftRecipe recipe)
        {
            if (recipe == null ||
                recipe.OutputPrefab == null ||
                recipe.OutputPrefab.UIData == null)
            {
                return null;
            }

            return
                recipe.OutputPrefab
                    .UIData
                    .GetIcon();
        }

        private void LogCraftCategoryContents(
            CraftUiCategory category,
            string reason)
        {
            if (ModLogger == null)
            {
                return;
            }

            int count =
                0;

            bool scoutFound =
                false;

            StringBuilder builder =
                new StringBuilder();

            for (int i = 0;
                 i < craftRecipes.Count;
                 i++)
            {
                CraftRecipe recipe =
                    craftRecipes[i];

                if (recipe == null ||
                    GetCraftUiCategory(
                        recipe) !=
                    category)
                {
                    continue;
                }

                count++;

                if (recipe.OutputItemId ==
                    ScoutEffigyItemId)
                {
                    scoutFound =
                        true;
                }

                if (builder.Length >
                    0)
                {
                    builder.Append(
                        ", ");
                }

                builder.Append(
                    recipe.OutputItemId);

                builder.Append(
                    ":");

                builder.Append(
                    recipe.DisplayName);
            }

            ModLogger.LogInfo(
                "[CraftCategoryDiag] Reason=" +
                reason +
                " | Category=" +
                GetCraftUiCategoryName(
                    category) +
                " | Count=" +
                count +
                " | ScoutEffigyFound=" +
                scoutFound +
                " | Items=[" +
                builder +
                "]");
        }

        private void LogScoutEffigyFinalState(
            string reason)
        {
            if (ModLogger == null)
            {
                return;
            }

            CraftRecipe recipe;

            bool exists =
                craftRecipesByOutputId.TryGetValue(
                    ScoutEffigyItemId,
                    out recipe) &&
                recipe != null;

            ModLogger.LogInfo(
                "[CraftCategoryDiag] Final scout state. Reason=" +
                reason +
                " | Exists=" +
                exists +
                " | CraftRecipeCount=" +
                craftRecipes.Count +
                (
                    exists
                        ? " | DisplayName=" +
                          recipe.DisplayName +
                          " | SourceCategory=" +
                          recipe.Category +
                          " | UiCategory=" +
                          GetCraftUiCategoryName(
                              GetCraftUiCategory(
                                  recipe)) +
                          " | Price=" +
                          recipe.MoneyCost
                        : string.Empty
                ));

            LogCraftCategoryContents(
                CraftUiCategory.Revive,
                reason);
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

            int currentResourceLevel =
                GetCurrentResourceLevel();

            if (currentResourceLevel <
                recipe.RequiredResourceLevel)
            {
                return
                    "<color=#FF8A80>" +
                    GetResourceGradeName(
                        recipe.RequiredResourceLevel) +
                    " 제작 등급이 필요합니다.</color>";
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

                int available =
                    CountLocalItemUnits(
                        cost.ItemId);

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
                    "공유 돈 또는 본인 인벤토리의 제작 재료가 부족합니다.");

                return;
            }

            global::Player player =
                global::Player.localPlayer;

            if (player == null)
            {
                SetTabStatus(
                    HubTab.Craft,
                    "플레이어 인벤토리를 찾지 못했습니다.");

                return;
            }

            // HasEmptySlot이 일반 슬롯, 기존 스택 또는 임시 손 슬롯에 지급 가능한 공간이 없다고 판단하면 요청하지 않습니다.
            if (!player.HasEmptySlot(
                    recipe.OutputItemId))
            {
                SetTabStatus(
                    HubTab.Craft,
                    "완성품을 받을 빈 슬롯이 없어 제작할 수 없습니다.");

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
        // 강화 탭 연결
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

        internal bool CanAttemptUpgrade
        {
            get
            {
                bool basicReady =
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

                if (!basicReady ||
                    selectedUpgradeKind !=
                        UpgradeKind.CampfireEfficiency)
                {
                    return basicReady;
                }

                CampfireBuildRecipe campfire =
                    GetNextCampfireRecipe();

                if (campfire == null ||
                    GetCurrentResourceLevel() <
                        campfire.RequiredResourceLevel)
                {
                    return false;
                }

                string moduleMessage;

                if (!HasRequiredModulesForCampfireStage(
                        campfire.Stage,
                        out moduleMessage))
                {
                    return false;
                }

                CraftConsumptionPlan plan;
                string missing;

                return TryBuildCraftConsumptionPlan(
                    MakeTemporaryRecipe(
                        campfire.Ingredients),
                    out plan,
                    out missing);
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

            if (selectedUpgradeKind ==
                UpgradeKind.CampfireEfficiency)
            {
                CampfireBuildRecipe campfire =
                    GetNextCampfireRecipe();

                if (campfire == null)
                {
                    SetTabStatus(
                        HubTab.Upgrade,
                        "모든 다음 모닥불을 이미 제작했습니다.");

                    return;
                }

                if (GetCurrentResourceLevel() <
                    campfire.RequiredResourceLevel)
                {
                    SetTabStatus(
                        HubTab.Upgrade,
                        GetResourceGradeName(
                            campfire.RequiredResourceLevel) +
                        " 자원 등급이 필요합니다.");

                    return;
                }

                string moduleMessage;

                if (!HasRequiredModulesForCampfireStage(
                        campfire.Stage,
                        out moduleMessage))
                {
                    SetTabStatus(
                        HubTab.Upgrade,
                        moduleMessage);

                    return;
                }

                CraftConsumptionPlan plan;
                string missing;

                if (!TryBuildCraftConsumptionPlan(
                        MakeTemporaryRecipe(
                            campfire.Ingredients),
                        out plan,
                        out missing))
                {
                    SetTabStatus(
                        HubTab.Upgrade,
                        missing);

                    return;
                }
            }

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
        // 판매 탭 연결
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

            selectedSellQuantity =
                1;

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
                    "번 슬롯의 아이템은 판매 대상 자원이 아닙니다.");
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
                selectedSellQuantity =
                    1;

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
                selectedSellQuantity =
                    1;

                return
                    "선택한 슬롯이 비어 있습니다.";
            }

            ushort itemId =
                slot.prefab.itemID;

            if (!Spawn.IsSaleResourceId(
                    itemId))
            {
                selectedSellQuantity =
                    1;

                return
                    "선택 아이템: " +
                    GetItemDisplayName(
                        slot.prefab) +
                    "\n이 아이템은 판매 대상 자원이 아닙니다.";
            }

            int unitPrice =
                Mathf.Max(
                    0,
                    GetSellPrice(
                        itemId) *
                    NormalizeSellMultiplier(
                        upgradeState
                            .SellMultiplier));

            int count =
                Mathf.Max(
                    1,
                    InventoryStack
                        .GetStackCount(
                            player,
                            (byte)selectedSellSlotId));

            selectedSellQuantity =
                Mathf.Clamp(
                    selectedSellQuantity,
                    1,
                    count);

            int totalPrice =
                unitPrice *
                selectedSellQuantity;

            canSell =
                unitPrice >
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
                "개\n판매 수량: " +
                selectedSellQuantity +
                "/" +
                count +
                "개" +
                "\n개당 판매가: " +
                unitPrice +
                "원" +
                "\n예상 판매액: " +
                totalPrice +
                "원" +
                "\n\n마우스 휠 ↑↓: 1개씩 조절" +
                "\nShift + 휠: 5개씩 조절";
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

            int stackCount =
                Mathf.Max(
                    1,
                    InventoryStack
                        .GetStackCount(
                            player,
                            slotId));

            int requestedQuantity =
                Mathf.Clamp(
                    selectedSellQuantity,
                    1,
                    stackCount);

            selectedSellQuantity =
                requestedQuantity;

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
                requestedQuantity +
                "개 판매 요청을 처리 중입니다...");

            object[] payload =
            {
                selectedSellSlotId,
                (int)itemId,
                guid,
                requestedQuantity
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
        // 파티 수량 집계와 표시용 도우미
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
                !IsCraftIngredientId(
                    slot.prefab.itemID))
            {
                return;
            }

            // 실제 ItemSlot을 전달해 일반 슬롯과 배낭 내부 슬롯의 스택 수량을 읽습니다.
            int amount =
                Mathf.Max(
                    1,
                    InventoryStack
                        .GetStackCount(
                            slot));

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
                        count +=
                            Mathf.Max(
                                1,
                                InventoryStack
                                    .GetStackCount(
                                        slot));
                    }
                }
            }

            return count;
        }

        internal bool CanClearLocalTempHandSlot
        {
            get
            {
                global::Player player =
                    global::Player.localPlayer;

                Character character =
                    Character.localCharacter;

                return
                    player != null &&
                    character != null &&
                    ReferenceEquals(
                        character.player,
                        player) &&
                    character.data.currentItem == null &&
                    player.tempFullSlot != null &&
                    !player.tempFullSlot.IsEmpty();
            }
        }

        internal void ClearLocalTempHandSlot()
        {
            global::Player player =
                global::Player.localPlayer;

            Character character =
                Character.localCharacter;

            if (player == null ||
                character == null ||
                !ReferenceEquals(
                    character.player,
                    player))
            {
                SetTabStatus(
                    currentTab,
                    "로컬 플레이어를 찾지 못했습니다.");

                return;
            }

            if (character.data.currentItem != null)
            {
                SetTabStatus(
                    currentTab,
                    "현재 손에 든 아이템을 먼저 내려놓아야 합니다.");

                return;
            }

            ItemSlot tempSlot =
                player.tempFullSlot;

            if (tempSlot == null ||
                tempSlot.IsEmpty() ||
                tempSlot.prefab == null)
            {
                SetTabStatus(
                    currentTab,
                    "가상 손 슬롯에 삭제할 아이템이 없습니다.");

                return;
            }

            string itemName =
                GetIngredientDisplayName(
                    tempSlot.prefab.itemID);

            int amount =
                Mathf.Max(
                    1,
                    InventoryStack.GetStackCount(
                        player,
                        (byte)250));

            CharacterItems items =
                character.refs != null
                    ? character.refs.items
                    : null;

            bool selectedTempSlot =
                items != null &&
                items.currentSelectedSlot.IsSome &&
                items.currentSelectedSlot.Value ==
                    (byte)250;

            player.EmptySlot(
                Optionable<byte>.Some(
                    (byte)250));

            if (selectedTempSlot)
            {
                items.EquipSlot(
                    Optionable<byte>.None);
            }

            if (player.itemsChangedAction != null)
            {
                player.itemsChangedAction(
                    player.itemSlots);
            }

            if (items != null)
            {
                items.RefreshAllCharacterCarryWeight();
            }

            partyResourceCacheUntil =
                0f;

            string message =
                "가상 손 슬롯의 " +
                itemName +
                " " +
                amount +
                "개를 삭제했습니다.";

            if (currentTab ==
                HubTab.Description)
            {
                RefreshWindow();
            }
            else
            {
                SetTabStatus(
                    currentTab,
                    message);
            }

            Logger.LogInfo(
                "Local temp-hand slot cleared. Actor=" +
                LocalActorNumber() +
                " | Item=" +
                itemName +
                " | Count=" +
                amount +
                ".");
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
                case 0:
                    return "<아이템 데이터 확인 필요>";

                case FireWoodItemId:
                    return "나뭇가지";

                case StoneItemId:
                    return "돌";

                case ConchItemId:
                    return "소라고둥";

                case TorchItemId:
                    return "횃불";

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
        // 강화 상태 저장과 효과 적용
        // -----------------------------------------------------------------

        private void BindUpgradeConfig()
        {
            resourceUpgradeFormula =
                BindFormula(
                    "02. 자원 등급 강화",
                    20,
                    40);

            stackUpgradeFormula =
                BindFormula(
                    "04. 인벤토리 적재 강화",
                    12,
                    18);

            campfireUpgradeFormula =
                BindFormula(
                    "05. 모닥불 효율 강화",
                    20,
                    50);

            doubleYieldCostConfig = Config.Bind(
                "06. 수집량 배율 강화",
                "강화 비용",
                60,
                new ConfigDescription(
                    "수집량 배율 강화의 1단계 기본 비용입니다. x3, x4, x5 단계는 각각 기본 비용의 2배, 3배, 4배입니다.",
                    new AcceptableValueRange<int>(0, 100000)));

            sellValueUpgradeFormula =
                BindFormula(
                    "07. 아이템 판매 수익 강화",
                    40,
                    60);
        }

        private UpgradeFormulaConfig BindFormula(
            string section,
            int baseCost,
            int costGrowth)
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
                        new AcceptableValueRange<int>(0, 100000)))
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

                    upgradeState.BaseStackCount =
                        DefaultBaseStackCount;

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
            fresh.BaseStackCount =
                DefaultBaseStackCount;
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
                incoming.StackLevel = Mathf.Clamp(
                    ReadInt(props, UpgradeStackKey, 0), 0, StackUpgradeMaximum);
                incoming.CampfireLevel = Mathf.Clamp(
                    ReadInt(props, UpgradeCampfireKey, 0), 0, CampfireUpgradeMaximum);
                incoming.YieldMultiplier = Mathf.Clamp(
                    ReadInt(
                        props,
                        UpgradeYieldKey,
                        1),
                    1,
                    YieldUpgradeMaximum + 1);

                incoming.SellMultiplier = NormalizeSellMultiplier(
                    ReadInt(
                        props,
                        UpgradeSellMultiplierKey,
                        1));

                incoming.BaseStackCount =
                    DefaultBaseStackCount;
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
                    { UpgradeStackKey, safe.StackLevel },
                    { UpgradeCampfireKey, safe.CampfireLevel },
                    { UpgradeYieldKey, safe.YieldMultiplier },
                    { UpgradeSellMultiplierKey, safe.SellMultiplier },
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
                " | Stack=" + safe.StackLevel +
                " | Campfire=" + safe.CampfireLevel +
                " | Yield=x" + safe.YieldMultiplier +
                " | Sell=x" + safe.SellMultiplier);

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
            safe.StackLevel = Mathf.Clamp(safe.StackLevel, 0, StackUpgradeMaximum);
            safe.CampfireLevel = Mathf.Clamp(safe.CampfireLevel, 0, CampfireUpgradeMaximum);
            safe.YieldMultiplier = Mathf.Clamp(safe.YieldMultiplier, 1, 5);
            safe.SellMultiplier = NormalizeSellMultiplier(
                safe.SellMultiplier);
            safe.BaseStackCount =
                DefaultBaseStackCount;
            safe.BaseCampfireMaterials = EnsureThree(
                safe.BaseCampfireMaterials,
                new[] { 1, 1, 1 });

            for (int i = 0; i < safe.BaseCampfireMaterials.Length; i++)
                safe.BaseCampfireMaterials[i] =
                    Mathf.Max(0, safe.BaseCampfireMaterials[i]);

            return safe;
        }

        private static int NormalizeSellMultiplier(
            int value)
        {
            if (value >= 16)
                return 16;

            if (value >= 8)
                return 8;

            if (value >= 4)
                return 4;

            if (value >= 2)
                return 2;

            return 1;
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
            ResourceYieldMultiplier = Mathf.Clamp(upgradeState.YieldMultiplier, 1, 5);

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
                " | Stack=" + CalculateEffectiveStackMaximum(upgradeState) +
                " | Yield=x" + ResourceYieldMultiplier);
        }

        private void RestoreBaseUpgradeEffects()
        {
            ResourceYieldMultiplier = 1;

            if (!upgradeStateLoaded)
                return;

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
            return new[]
            {
                0,
                0,
                0
            };
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

            if (kindValue < 0 || kindValue > (int)UpgradeKind.SellValue)
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
            int money = ReadSharedMoney();

            CraftConsumptionPlan campfirePlan = null;

            if (kind == UpgradeKind.CampfireEfficiency)
            {
                CampfireBuildRecipe campfire =
                    GetNextCampfireRecipe();

                if (campfire == null)
                {
                    SendUpgradeResult(
                        actor,
                        false,
                        "모든 다음 모닥불을 이미 제작했습니다.");

                    return;
                }

                if (GetCurrentResourceLevel() <
                    campfire.RequiredResourceLevel)
                {
                    SendUpgradeResult(
                        actor,
                        false,
                        GetResourceGradeName(
                            campfire.RequiredResourceLevel) +
                        " 자원 등급이 필요합니다.");

                    return;
                }

                string moduleMessage;

                if (!HasRequiredModulesForCampfireStage(
                        campfire.Stage,
                        out moduleMessage))
                {
                    SendUpgradeResult(
                        actor,
                        false,
                        moduleMessage);

                    return;
                }

                string missing;

                if (!TryBuildCraftConsumptionPlan(
                        MakeTemporaryRecipe(
                            campfire.Ingredients),
                        out campfirePlan,
                        out missing))
                {
                    SendUpgradeResult(
                        actor,
                        false,
                        missing);

                    return;
                }
            }

            if (money < cost)
            {
                SendUpgradeResult(actor, false, "공유 돈이 부족합니다.");
                return;
            }

            if (campfirePlan != null)
            {
                List<ConsumedSelectedSlot> consumedSlots;

                if (!TryConsumePlan(
                        campfirePlan,
                        out consumedSlots))
                {
                    SendUpgradeResult(
                        actor,
                        false,
                        "다음 모닥불 재료 소비 중 인벤토리가 변경되었습니다.");

                    return;
                }

                BroadcastConsumedSelectedSlots(
                    consumedSlots);

                partyResourceCacheUntil = 0f;
            }

            SetSharedMoneyOnHost(
                money - cost);

            UpgradeState upgraded = upgradeState.Clone();
            IncreaseUpgradeLevel(upgraded, kind);

            if (!PublishUpgradeState(upgraded, "Upgrade completed: " + kind))
            {
                SetSharedMoneyOnHost(
                    ReadSharedMoney() + cost);

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
                " 강화 완료\n" +
                GetUpgradeCurrentEffect(kind));

            if (kind ==
                UpgradeKind.CampfireEfficiency)
            {
                Logger.LogInfo(
                    "Campfire stage unlocked with cumulative modules. " +
                    "Stage=" +
                    upgraded.CampfireLevel +
                    " | RequiredMask=" +
                    GetRequiredModuleMaskForCampfireStage(
                        upgraded.CampfireLevel) +
                    " | PurchasedMask=" +
                    purchasedPartsMask);
            }
        }

        private static void IncreaseUpgradeLevel(UpgradeState value, UpgradeKind kind)
        {
            switch (kind)
            {
                case UpgradeKind.ResourceGrade:
                    value.ResourceLevel = Mathf.Min(ResourceUpgradeMaximum, value.ResourceLevel + 1);
                    break;

                case UpgradeKind.StackCapacity:
                    value.StackLevel = Mathf.Min(StackUpgradeMaximum, value.StackLevel + 1);
                    break;

                case UpgradeKind.CampfireEfficiency:
                    value.CampfireLevel = Mathf.Min(CampfireUpgradeMaximum, value.CampfireLevel + 1);
                    break;

                case UpgradeKind.DoubleYield:
                    value.YieldMultiplier =
                        Mathf.Min(
                            YieldUpgradeMaximum + 1,
                            Mathf.Max(
                                1,
                                value.YieldMultiplier) +
                            1);
                    break;

                case UpgradeKind.SellValue:
                    value.SellMultiplier =
                        Mathf.Min(
                            16,
                            Mathf.Max(
                                1,
                                value.SellMultiplier) *
                            2);
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
                case UpgradeKind.StackCapacity:
                    return upgradeState.StackLevel;
                case UpgradeKind.CampfireEfficiency:
                    return upgradeState.CampfireLevel;
                case UpgradeKind.DoubleYield:
                    return
                        Mathf.Clamp(
                            upgradeState.YieldMultiplier - 1,
                            0,
                            YieldUpgradeMaximum);

                case UpgradeKind.SellValue:
                    switch (NormalizeSellMultiplier(
                        upgradeState.SellMultiplier))
                    {
                        case 2:
                            return 1;
                        case 4:
                            return 2;
                        case 8:
                            return 3;
                        case 16:
                            return 4;
                        default:
                            return 0;
                    }

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
                case UpgradeKind.StackCapacity:
                    return StackUpgradeMaximum;
                case UpgradeKind.CampfireEfficiency:
                    return CampfireUpgradeMaximum;
                case UpgradeKind.DoubleYield:
                    return YieldUpgradeMaximum;

                case UpgradeKind.SellValue:
                    return SellValueUpgradeMaximum;

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
                case UpgradeKind.StackCapacity:
                    return stackUpgradeFormula;
                case UpgradeKind.CampfireEfficiency:
                    return campfireUpgradeFormula;

                case UpgradeKind.SellValue:
                    return sellValueUpgradeFormula;

                default:
                    return null;
            }
        }

        private int GetNextUpgradeCost(UpgradeKind kind)
        {
            int nextLevel = GetUpgradeCurrentLevel(kind) + 1;

            if (kind == UpgradeKind.CampfireEfficiency)
            {
                CampfireBuildRecipe campfire =
                    GetNextCampfireRecipe();

                return
                    campfire != null
                        ? campfire.MoneyCost
                        : 0;
            }

            if (kind == UpgradeKind.DoubleYield)
            {
                int yieldBaseCost =
                    doubleYieldCostConfig != null
                        ? Mathf.Max(
                            0,
                            doubleYieldCostConfig.Value)
                        : 60;

                return
                    yieldBaseCost *
                    Mathf.Clamp(
                        nextLevel,
                        1,
                        YieldUpgradeMaximum);
            }

            UpgradeFormulaConfig formula = GetUpgradeFormula(kind);
            int baseCost = formula != null && formula.BaseCost != null
                ? Mathf.Max(0, formula.BaseCost.Value)
                : 0;
            int growth = formula != null && formula.CostGrowth != null
                ? Mathf.Max(0, formula.CostGrowth.Value)
                : 0;

            return baseCost + growth * Mathf.Max(0, nextLevel - 1);
        }

        internal string GetUpgradeDisplayName(UpgradeKind kind)
        {
            switch (kind)
            {
                case UpgradeKind.ResourceGrade:
                    return "자원 등급";
                case UpgradeKind.StackCapacity:
                    return "인벤토리 적재량";
                case UpgradeKind.CampfireEfficiency:
                    return "다음 모닥불 제작";
                case UpgradeKind.DoubleYield:
                    return "수집량 배율";

                case UpgradeKind.SellValue:
                    return "아이템 판매 수익";

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

                case UpgradeKind.StackCapacity:
                    return "현재 최대 적재량: " + CalculateEffectiveStackMaximum(upgradeState) + "개";

                case UpgradeKind.CampfireEfficiency:
                    return "완성된 다음 모닥불: " +
                           upgradeState.CampfireLevel +
                           "/4";

                case UpgradeKind.DoubleYield:
                    return "현재 수집량: x" + upgradeState.YieldMultiplier;

                case UpgradeKind.SellValue:
                    return "현재 판매 수익: 기본 판매가 x" +
                           NormalizeSellMultiplier(
                               upgradeState.SellMultiplier);

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

                case UpgradeKind.StackCapacity:
                    return "다음 효과: 슬롯당 " +
                           CalculateEffectiveStackMaximum(preview) + "개";

                case UpgradeKind.CampfireEfficiency:
                    CampfireBuildRecipe campfire =
                        GetNextCampfireRecipe();

                    if (campfire == null)
                    {
                        return "모든 다음 모닥불 제작 완료";
                    }

                    StringBuilder builder =
                        new StringBuilder();

                    builder.Append("이번 판 무작위 조합\n");
                    builder.Append("다음 제작: ");
                    builder.Append(campfire.Name);
                    builder.Append("\n필요 등급: ");
                    builder.Append(
                        GetResourceGradeName(
                            campfire.RequiredResourceLevel));

                    for (int i = 0;
                         i < campfire.Ingredients.Count;
                         i++)
                    {
                        IngredientCost cost =
                            campfire.Ingredients[i];

                        builder.Append("\n");
                        builder.Append(
                            GetIngredientDisplayName(
                                cost.ItemId));
                        builder.Append(" x");
                        builder.Append(cost.Count);
                    }

                    builder.Append(
                        "\n\n");

                    builder.Append(
                        BuildCampfireModuleRequirementText(
                            campfire.Stage));

                    return builder.ToString();

                case UpgradeKind.DoubleYield:
                    return
                        "다음 효과: 맵 자원 수집량 x" +
                        preview.YieldMultiplier;

                case UpgradeKind.SellValue:
                    return
                        "다음 효과: 기본 판매가 x" +
                        NormalizeSellMultiplier(
                            preview.SellMultiplier);

                default:
                    return string.Empty;
            }
        }

        private static string GetResourceGradeName(
            int level)
        {
            int safeLevel =
                Mathf.Clamp(
                    level,
                    0,
                    4);

            switch (GetCurrentLanguage())
            {
                case HubLanguage.Korean:
                    switch (safeLevel)
                    {
                        case 0:
                            return "일반";

                        case 1:
                            return "보통";

                        case 2:
                            return "희귀";

                        case 3:
                            return "고유";

                        case 4:
                            return "전설";
                    }
                    break;

                case HubLanguage.Chinese:
                    switch (safeLevel)
                    {
                        case 0:
                            return "普通";

                        case 1:
                            return "标准";

                        case 2:
                            return "稀有";

                        case 3:
                            return "独特";

                        case 4:
                            return "传说";
                    }
                    break;

                case HubLanguage.Japanese:
                    switch (safeLevel)
                    {
                        case 0:
                            return "コモン";

                        case 1:
                            return "ノーマル";

                        case 2:
                            return "レア";

                        case 3:
                            return "ユニーク";

                        case 4:
                            return "レジェンダリー";
                    }
                    break;

                case HubLanguage.French:
                    switch (safeLevel)
                    {
                        case 0:
                            return "Commun";

                        case 1:
                            return "Normal";

                        case 2:
                            return "Rare";

                        case 3:
                            return "Unique";

                        case 4:
                            return "Légendaire";
                    }
                    break;

                case HubLanguage.English:
                default:
                    switch (safeLevel)
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
                    }
                    break;
            }

            return "Common";
        }



        public void OnPlayerEnteredRoom(Photon.Realtime.Player newPlayer)
        {
            // 플레이어 입장 시 강화·부품 Room Property를 즉시 읽지 않고 dirty 상태로 표시합니다.
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

            bool recipeSeedChanged =
                ContainsRecipeProperty(
                    changed);

            bool upgradeChanged =
                ContainsUpgradeProperty(
                    changed);

            bool partsChanged =
                ContainsPartsProperty(
                    changed);

            if (recipeSeedChanged)
            {
                if (EnsureSharedRecipeSeed())
                {
                    if (activeWindow != null)
                    {
                        EnsureProgressionRecipesBuilt();

                        if (currentTab ==
                            HubTab.Craft)
                        {
                            EnsureCraftRecipesBuilt();
                        }

                        RefreshWindow();
                    }
                }
            }

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

                // RunId 변경은 강화와 부품 상태를 다시 읽게 하지만 기존 Room 제작 시드는 유지합니다.
                EnsureSharedRecipeSeed();

                if (activeWindow != null)
                {
                    EnsureProgressionRecipesBuilt();
                    LoadUpgradeStateOnDemand();
                    LoadPartsStateOnDemand();

                    if (currentTab ==
                        HubTab.Craft)
                    {
                        EnsureCraftRecipesBuilt();
                    }

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

            // 새 호스트는 Room Property에 공유 제작 시드가 없을 때만 생성합니다.
            EnsureSharedRecipeSeed();

            // 메뉴가 열려 있을 때만 상태를 다시 읽고 새 호스트 소유 상태로 재게시합니다.
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

        private static bool ContainsRecipeProperty(
            ExitGames.Client.Photon.Hashtable values)
        {
            return
                values.ContainsKey(
                    RecipeProtocolKey) ||
                values.ContainsKey(
                    RecipeRunIdKey) ||
                values.ContainsKey(
                    RecipeSeedKey);
        }

        private static bool ContainsUpgradeProperty(
            ExitGames.Client.Photon.Hashtable values)
        {
            return values.ContainsKey(UpgradeProtocolKey) ||
                   values.ContainsKey(UpgradeRevisionKey) ||
                   values.ContainsKey(UpgradeOwnerKey) ||
                   values.ContainsKey(UpgradeRunIdKey) ||
                   values.ContainsKey(UpgradeResourceKey) ||
                   values.ContainsKey(UpgradeStackKey) ||
                   values.ContainsKey(UpgradeCampfireKey) ||
                   values.ContainsKey(UpgradeYieldKey) ||
                   values.ContainsKey(UpgradeSellMultiplierKey) ||
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
                !Spawn.IsSaleResourceId(
                    itemId))
            {
                return;
            }

            int safeCountBefore =
                Mathf.Max(
                    0,
                    countBefore);

            int currentTotal =
                CountPlayerResourceUnits(
                    player,
                    itemId);

            // 원본 RequestPickup 처리 후 해당 자원 수량이 증가한 경우에만 부족한 배율 수량을 추가합니다.
            if (currentTotal <=
                safeCountBefore)
            {
                return;
            }

            int expectedTotal =
                safeCountBefore +
                Mathf.Max(
                    1,
                    ResourceYieldMultiplier);

            int wanted =
                Mathf.Max(
                    0,
                    expectedTotal -
                    currentTotal);

            int granted =
                0;

            for (int i = 0;
                 i < wanted;
                 i++)
            {
                ItemSlot slot;

                if (!player.AddItem(
                        itemId,
                        null,
                        out slot))
                {
                    break;
                }

                granted++;
            }

            if (ModLogger != null)
            {
                ModLogger.LogInfo(
                    "Resource yield reconciliation. ItemID=" +
                    itemId +
                    " | Multiplier=x" +
                    ResourceYieldMultiplier +
                    " | Before=" +
                    safeCountBefore +
                    " | BeforeReconcile=" +
                    currentTotal +
                    " | Expected=" +
                    expectedTotal +
                    " | Granted=" +
                    granted +
                    "/" +
                    wanted +
                    " | Final=" +
                    CountPlayerResourceUnits(
                        player,
                        itemId));
            }
        }

        // -----------------------------------------------------------------
        // 판매 요청 검증과 수량 소비
        // -----------------------------------------------------------------

        private void ProcessSellRequestOnHost(
            int actorNumber,
            object[] requestData)
        {
            if (!PhotonNetwork.IsMasterClient)
            {
                return;
            }

            CleanupExpiredSaleTransactions();

            double now = PhotonNetwork.Time;
            double previousRequestAt;

            if (lastSellRequestAtByActor.TryGetValue(
                    actorNumber,
                    out previousRequestAt) &&
                now - previousRequestAt <
                    MinimumRequestIntervalSeconds)
            {
                SendSellResult(
                    actorNumber,
                    false,
                    "판매 요청이 너무 빠릅니다.",
                    0,
                    ReadSharedMoney(),
                    -1,
                    -1);
                return;
            }

            lastSellRequestAtByActor[actorNumber] = now;

            if (requestData == null ||
                requestData.Length < 3)
            {
                SendSellResult(
                    actorNumber,
                    false,
                    "잘못된 판매 요청입니다.",
                    0,
                    ReadSharedMoney(),
                    -1,
                    -1);
                return;
            }

            int slotIdValue;
            int expectedItemId;
            string expectedGuid;
            int requestedQuantity;

            try
            {
                slotIdValue = Convert.ToInt32(requestData[0]);
                expectedItemId = Convert.ToInt32(requestData[1]);
                expectedGuid = requestData[2] as string;
                requestedQuantity =
                    requestData.Length >
                        3
                        ? Convert.ToInt32(
                            requestData[3])
                        : 1;
            }
            catch (Exception)
            {
                SendSellResult(
                    actorNumber,
                    false,
                    "판매 요청 데이터를 해석하지 못했습니다.",
                    0,
                    ReadSharedMoney(),
                    -1,
                    -1);
                return;
            }

            if (slotIdValue < 0 ||
                slotIdValue > byte.MaxValue ||
                string.IsNullOrEmpty(expectedGuid) ||
                requestedQuantity <= 0)
            {
                SendSellResult(
                    actorNumber,
                    false,
                    "판매 슬롯 또는 아이템 GUID가 올바르지 않습니다.",
                    0,
                    ReadSharedMoney(),
                    slotIdValue,
                    expectedItemId);
                return;
            }

            if (reservedOrSoldItemGuids.Contains(expectedGuid))
            {
                SendSellResult(
                    actorNumber,
                    false,
                    "이미 판매 처리 중이거나 판매가 완료된 아이템입니다.",
                    0,
                    ReadSharedMoney(),
                    slotIdValue,
                    expectedItemId);
                return;
            }

            global::Player player =
                PlayerHandler.GetPlayer(actorNumber);

            if (player == null)
            {
                SendSellResult(
                    actorNumber,
                    false,
                    "판매 요청 플레이어를 찾을 수 없습니다.",
                    0,
                    ReadSharedMoney(),
                    slotIdValue,
                    expectedItemId);
                return;
            }

            byte slotId = (byte)slotIdValue;
            ItemSlot slot = player.GetItemSlot(slotId);

            if (slot == null ||
                slot.IsEmpty() ||
                slot.prefab == null)
            {
                SendSellResult(
                    actorNumber,
                    false,
                    "판매할 아이템이 슬롯에 없습니다.",
                    0,
                    ReadSharedMoney(),
                    slotIdValue,
                    expectedItemId);
                return;
            }

            ushort actualItemId = slot.prefab.itemID;
            string actualGuid =
                slot.data != null
                    ? slot.data.guid.ToString()
                    : string.Empty;

            if (actualItemId != (ushort)expectedItemId ||
                !Spawn.IsSaleResourceId(actualItemId) ||
                string.IsNullOrEmpty(actualGuid) ||
                !string.Equals(
                    actualGuid,
                    expectedGuid,
                    StringComparison.Ordinal))
            {
                SendSellResult(
                    actorNumber,
                    false,
                    "판매 승인 전에 아이템 정보가 변경되었습니다.",
                    0,
                    ReadSharedMoney(),
                    slotIdValue,
                    actualItemId);
                return;
            }

            int unitSalePrice =
                Mathf.Max(
                    0,
                    GetSellPrice(actualItemId) *
                    NormalizeSellMultiplier(
                        upgradeState.SellMultiplier));

            if (unitSalePrice <= 0)
            {
                SendSellResult(
                    actorNumber,
                    false,
                    "판매 가격이 설정되지 않은 아이템입니다.",
                    0,
                    ReadSharedMoney(),
                    slotIdValue,
                    actualItemId);
                return;
            }

            int availableCount =
                Mathf.Max(
                    1,
                    InventoryStack
                        .GetStackCount(
                            player,
                            slotId));

            if (requestedQuantity >
                availableCount)
            {
                SendSellResult(
                    actorNumber,
                    false,
                    "보유 수량보다 많이 판매할 수 없습니다.",
                    0,
                    ReadSharedMoney(),
                    slotIdValue,
                    actualItemId);
                return;
            }

            reservedOrSoldItemGuids.Add(
                actualGuid);

            int consumedQuantity;
            int remainingCount;
            string remainingGuid;
            string consumeFailure;

            bool consumed =
                TryConsumeSaleQuantityOnHost(
                    player,
                    slotId,
                    actualItemId,
                    actualGuid,
                    requestedQuantity,
                    out consumedQuantity,
                    out remainingCount,
                    out remainingGuid,
                    out consumeFailure);

            reservedOrSoldItemGuids.Remove(
                actualGuid);

            if (!consumed ||
                consumedQuantity <= 0)
            {
                SendSellResult(
                    actorNumber,
                    false,
                    string.IsNullOrEmpty(
                        consumeFailure)
                        ? "판매 수량을 인벤토리에서 제거하지 못했습니다."
                        : consumeFailure,
                    0,
                    ReadSharedMoney(),
                    slotIdValue,
                    actualItemId);
                return;
            }

            int totalPrice =
                unitSalePrice *
                consumedQuantity;

            int newBalance =
                Mathf.Max(
                    0,
                    ReadSharedMoney() +
                    totalPrice);

            SetSharedMoneyOnHost(
                newBalance);

            partyResourceCacheUntil =
                0f;

            SendSellResult(
                actorNumber,
                true,
                GetItemDisplayName(
                    slot.prefab) +
                " " +
                consumedQuantity +
                "개 판매 완료: +" +
                totalPrice +
                "원" +
                (
                    remainingCount >
                        0
                        ? "\n남은 수량: " +
                          remainingCount +
                          "개"
                        : string.Empty
                ),
                totalPrice,
                newBalance,
                slotIdValue,
                actualItemId);
        }

        private static bool TryConsumeSaleQuantityOnHost(
            global::Player player,
            byte slotId,
            ushort itemId,
            string expectedGuid,
            int requestedQuantity,
            out int consumedQuantity,
            out int remainingCount,
            out string remainingGuid,
            out string failureMessage)
        {
            consumedQuantity =
                0;

            remainingCount =
                0;

            remainingGuid =
                string.Empty;

            failureMessage =
                string.Empty;

            if (!PhotonNetwork.IsMasterClient ||
                InventoryStack.Instance == null ||
                player == null ||
                requestedQuantity <= 0)
            {
                failureMessage =
                    "호스트 인벤토리 처리 상태가 올바르지 않습니다.";

                return false;
            }

            ItemSlot initialSlot =
                player.GetItemSlot(
                    slotId);

            if (initialSlot == null ||
                initialSlot.IsEmpty() ||
                initialSlot.prefab == null ||
                initialSlot.prefab.itemID !=
                    itemId)
            {
                failureMessage =
                    "판매 직전에 슬롯의 아이템이 변경되었습니다.";

                return false;
            }

            string initialGuid =
                initialSlot.data != null
                    ? initialSlot.data.guid
                        .ToString()
                    : string.Empty;

            if (string.IsNullOrEmpty(
                    initialGuid) ||
                !string.Equals(
                    initialGuid,
                    expectedGuid,
                    StringComparison.Ordinal))
            {
                failureMessage =
                    "판매 직전에 아이템 GUID가 변경되었습니다.";

                return false;
            }

            int countBefore =
                Mathf.Max(
                    1,
                    InventoryStack
                        .GetStackCount(
                            player,
                            slotId));

            if (requestedQuantity >
                countBefore)
            {
                failureMessage =
                    "판매 요청 수량이 현재 보유 수량보다 많습니다.";

                return false;
            }

            bool selectedBefore =
                player.character != null &&
                player.character.refs != null &&
                player.character.refs.items != null &&
                player.character.refs.items
                    .currentSelectedSlot.IsSome &&
                player.character.refs.items
                    .currentSelectedSlot.Value ==
                    slotId;

            for (int i = 0;
                 i < requestedQuantity;
                 i++)
            {
                ItemSlot currentSlot =
                    player.GetItemSlot(
                        slotId);

                if (currentSlot == null ||
                    currentSlot.IsEmpty() ||
                    currentSlot.prefab == null ||
                    currentSlot.prefab.itemID !=
                        itemId)
                {
                    break;
                }

                string currentGuid =
                    currentSlot.data != null
                        ? currentSlot.data.guid
                            .ToString()
                        : string.Empty;

                if (!string.Equals(
                        currentGuid,
                        expectedGuid,
                        StringComparison.Ordinal))
                {
                    break;
                }

                int currentCount =
                    Mathf.Max(
                        1,
                        InventoryStack
                            .GetStackCount(
                                player,
                                slotId));

                if (currentCount >
                    1)
                {
                    if (!InventoryStack.Instance
                            .HostConsumeOneFromSlot(
                                player,
                                slotId,
                                "CraftHub.BatchSale",
                                false))
                    {
                        break;
                    }
                }
                else
                {
                    currentSlot.EmptyOut();
                }

                consumedQuantity++;
            }

            SyncPlayerInventoryFromHost(
                player);

            HashSet<global::Player> touchedPlayers =
                new HashSet<global::Player>
                {
                    player
                };

            RefreshCarryWeights(
                touchedPlayers);

            ItemSlot remainingSlot =
                player.GetItemSlot(
                    slotId);

            if (remainingSlot != null &&
                !remainingSlot.IsEmpty() &&
                remainingSlot.prefab != null &&
                remainingSlot.prefab.itemID ==
                    itemId)
            {
                remainingCount =
                    Mathf.Max(
                        1,
                        InventoryStack
                            .GetStackCount(
                                player,
                                slotId));

                remainingGuid =
                    remainingSlot.data != null
                        ? remainingSlot.data.guid
                            .ToString()
                        : string.Empty;
            }

            if (selectedBefore &&
                remainingCount <=
                    0 &&
                player.character != null &&
                player.character.photonView != null &&
                player.character.photonView.Owner !=
                    null &&
                Instance != null)
            {
                Instance.BroadcastConsumedSelectedSlots(
                    new List<ConsumedSelectedSlot>
                    {
                        new ConsumedSelectedSlot
                        {
                            ActorNumber =
                                player.character
                                    .photonView
                                    .Owner
                                    .ActorNumber,

                            SlotId =
                                slotId
                        }
                    });
            }

            if (consumedQuantity !=
                requestedQuantity)
            {
                failureMessage =
                    "판매 처리 중 인벤토리가 변경되어 요청한 수량을 모두 제거하지 못했습니다.";

                return false;
            }

            return true;
        }

        private bool SendSellConsumeRequest(
            PendingSaleTransaction transaction)
        {
            object[] payload =
            {
                transaction.TransactionId,
                (int)transaction.SlotId,
                (int)transaction.ItemId,
                transaction.ItemGuid ?? string.Empty
            };

            if (PhotonNetwork.LocalPlayer != null &&
                PhotonNetwork.LocalPlayer.ActorNumber ==
                    transaction.ActorNumber)
            {
                HandleSellConsumeRequest(payload);
                return true;
            }

            RaiseEventOptions options =
                new RaiseEventOptions
                {
                    TargetActors =
                        new[]
                        {
                            transaction.ActorNumber
                        }
                };

            return PhotonNetwork.RaiseEvent(
                SellConsumeRequestEventCode,
                payload,
                options,
                SendOptions.SendReliable);
        }

        private void HandleSellConsumeRequest(
            object[] payload)
        {
            int transactionId = 0;
            int slotIdValue;
            int itemIdValue;
            string expectedGuid;

            try
            {
                if (payload == null ||
                    payload.Length < 4)
                {
                    throw new InvalidOperationException();
                }

                transactionId = Convert.ToInt32(payload[0]);
                slotIdValue = Convert.ToInt32(payload[1]);
                itemIdValue = Convert.ToInt32(payload[2]);
                expectedGuid = payload[3] as string;
            }
            catch (Exception)
            {
                SendSellConsumeAck(
                    transactionId,
                    false,
                    "판매 아이템 제거 요청 데이터가 올바르지 않습니다.",
                    0,
                    string.Empty);
                return;
            }

            if (transactionId <= 0 ||
                slotIdValue < 0 ||
                slotIdValue > byte.MaxValue ||
                itemIdValue < 0 ||
                itemIdValue > ushort.MaxValue ||
                string.IsNullOrEmpty(expectedGuid))
            {
                SendSellConsumeAck(
                    transactionId,
                    false,
                    "판매 트랜잭션 정보가 올바르지 않습니다.",
                    0,
                    string.Empty);
                return;
            }

            localPendingSale =
                new LocalPendingSale
                {
                    TransactionId = transactionId,
                    SlotId = (byte)slotIdValue,
                    ItemId = (ushort)itemIdValue,
                    ItemGuid = expectedGuid
                };

            string failureMessage;
            int remainingCount;
            string remainingGuid;

            bool removed =
                TryRemoveSoldLocalItem(
                    localPendingSale,
                    out remainingCount,
                    out remainingGuid,
                    out failureMessage);

            SendSellConsumeAck(
                transactionId,
                removed,
                removed
                    ? "판매 아이템 1개를 제거했습니다."
                    : failureMessage,
                remainingCount,
                remainingGuid);

            if (!removed)
            {
                localPendingSale = null;
            }
        }

        private static bool TryRemoveSoldLocalItem(
            LocalPendingSale sale,
            out int remainingCount,
            out string remainingGuid,
            out string failureMessage)
        {
            remainingCount =
                0;

            remainingGuid =
                string.Empty;

            failureMessage =
                string.Empty;

            global::Player localPlayer =
                global::Player.localPlayer;

            if (sale == null ||
                localPlayer == null ||
                localPlayer.itemSlots == null ||
                sale.SlotId >= localPlayer.itemSlots.Length)
            {
                failureMessage =
                    "판매자 로컬 인벤토리를 찾지 못했습니다.";
                return false;
            }

            ItemSlot slot =
                localPlayer.GetItemSlot(sale.SlotId);

            if (slot == null ||
                slot.IsEmpty() ||
                slot.prefab == null ||
                slot.prefab.itemID != sale.ItemId)
            {
                failureMessage =
                    "판매 승인 후 슬롯의 아이템이 변경되었습니다.";
                return false;
            }

            string actualGuid =
                slot.data != null
                    ? slot.data.guid.ToString()
                    : string.Empty;

            if (!string.Equals(
                    actualGuid,
                    sale.ItemGuid,
                    StringComparison.Ordinal))
            {
                failureMessage =
                    "판매 승인 후 아이템 GUID가 변경되었습니다.";
                return false;
            }

            int countBefore =
                Mathf.Max(
                    1,
                    InventoryStack.GetStackCount(
                        localPlayer,
                        sale.SlotId));

            Character character =
                Character.localCharacter;

            if (character != null &&
                character.refs != null &&
                character.refs.items != null &&
                !character.refs.items.currentSelectedSlot.IsNone &&
                character.refs.items.currentSelectedSlot.Value ==
                    sale.SlotId)
            {
                character.refs.items.EquipSlot(
                    Optionable<byte>.None);
            }

            // 판매자 로컬 슬롯이 선택 중이면 선택을 해제한 뒤 EmptySlot으로 한 개를 제거합니다.
            localPlayer.EmptySlot(
                Optionable<byte>.Some(
                    sale.SlotId));

            ItemSlot verificationSlot =
                localPlayer.GetItemSlot(
                    sale.SlotId);

            int countAfter =
                0;

            if (verificationSlot != null &&
                !verificationSlot.IsEmpty() &&
                verificationSlot.prefab != null &&
                verificationSlot.prefab.itemID ==
                    sale.ItemId)
            {
                countAfter =
                    Mathf.Max(
                        1,
                        InventoryStack.GetStackCount(
                            localPlayer,
                            sale.SlotId));

                remainingGuid =
                    verificationSlot.data != null
                        ? verificationSlot.data.guid.ToString()
                        : string.Empty;
            }

            // EmptySlot 호출 뒤 같은 ItemID의 실제 스택 수량이 정확히 1 감소했는지 확인합니다.
            bool removed =
                countAfter ==
                    Mathf.Max(
                        0,
                        countBefore - 1);

            if (!removed)
            {
                failureMessage =
                    "판매 전후 스택 수량이 정확히 1 감소하지 않았습니다.";

                if (ModLogger != null)
                {
                    ModLogger.LogError(
                        "[SaleDiag] Client stack decrement verification failed. " +
                        "Transaction=" +
                        sale.TransactionId +
                        " | Slot=" +
                        sale.SlotId +
                        " | ItemID=" +
                        sale.ItemId +
                        " | Guid=" +
                        sale.ItemGuid +
                        " | Before=" +
                        countBefore +
                        " | After=" +
                        countAfter);
                }

                return false;
            }

            remainingCount =
                countAfter;

            if (ModLogger != null)
            {
                ModLogger.LogInfo(
                    "[SaleDiag] Client sold exactly one stack unit. " +
                    "Transaction=" +
                    sale.TransactionId +
                    " | Slot=" +
                    sale.SlotId +
                    " | ItemID=" +
                    sale.ItemId +
                    " | Guid=" +
                    sale.ItemGuid +
                    " | Before=" +
                    countBefore +
                    " | Remaining=" +
                    remainingCount +
                    " | RemainingGuid=" +
                    remainingGuid);
            }

            return true;
        }

        private void SendSellConsumeAck(
            int transactionId,
            bool removed,
            string message,
            int remainingCount,
            string remainingGuid)
        {
            object[] payload =
            {
                transactionId,
                removed,
                message ?? string.Empty,
                Mathf.Max(
                    0,
                    remainingCount),
                remainingGuid ?? string.Empty
            };

            if (PhotonNetwork.IsMasterClient)
            {
                ProcessSellConsumeAckOnHost(
                    LocalActorNumber(),
                    payload);
                return;
            }

            Photon.Realtime.Player masterClient =
                PhotonNetwork.MasterClient;

            if (masterClient == null)
            {
                pendingRequest = PendingRequest.None;
                SetTabStatus(
                    HubTab.Sell,
                    "판매 확인을 전달할 호스트를 찾지 못했습니다.");
                return;
            }

            RaiseEventOptions options =
                new RaiseEventOptions
                {
                    TargetActors =
                        new[]
                        {
                            masterClient.ActorNumber
                        }
                };

            if (!PhotonNetwork.RaiseEvent(
                    SellConsumeAckEventCode,
                    payload,
                    options,
                    SendOptions.SendReliable))
            {
                pendingRequest = PendingRequest.None;
                SetTabStatus(
                    HubTab.Sell,
                    "판매 아이템은 제거됐지만 호스트 확인 전송에 실패했습니다.");
            }
        }

        private void ProcessSellConsumeAckOnHost(
            int senderActorNumber,
            object[] payload)
        {
            if (!PhotonNetwork.IsMasterClient ||
                payload == null ||
                payload.Length < 5)
            {
                return;
            }

            int transactionId;
            bool removed;
            string message;
            int remainingCount;
            string remainingGuid;

            try
            {
                transactionId = Convert.ToInt32(payload[0]);
                removed = Convert.ToBoolean(payload[1]);
                message = payload[2] as string ?? string.Empty;
                remainingCount =
                    Mathf.Max(
                        0,
                        Convert.ToInt32(
                            payload[3]));
                remainingGuid =
                    payload[4] as string ??
                    string.Empty;
            }
            catch (Exception)
            {
                return;
            }

            PendingSaleTransaction transaction;

            if (!pendingSaleTransactions.TryGetValue(
                    transactionId,
                    out transaction) ||
                transaction == null ||
                transaction.ActorNumber != senderActorNumber)
            {
                return;
            }

            pendingSaleTransactions.Remove(transactionId);

            if (!removed)
            {
                reservedOrSoldItemGuids.Remove(
                    transaction.ItemGuid);

                SendSellResult(
                    transaction.ActorNumber,
                    false,
                    string.IsNullOrEmpty(message)
                        ? "판매 아이템을 제거하지 못했습니다."
                        : message,
                    0,
                    ReadSharedMoney(),
                    transaction.SlotId,
                    transaction.ItemId);
                return;
            }

            // 동일 GUID 스택에 수량이 남아 있으면 예약을 해제하고, 전부 소모된 GUID는 예약 집합에 남깁니다.
            if (remainingCount >
                    0 &&
                string.Equals(
                    remainingGuid,
                    transaction.ItemGuid,
                    StringComparison.Ordinal))
            {
                reservedOrSoldItemGuids.Remove(
                    transaction.ItemGuid);
            }

            int newBalance =
                Mathf.Max(
                    0,
                    ReadSharedMoney() +
                    transaction.SalePrice);

            SetSharedMoneyOnHost(newBalance);

            SendSellResult(
                transaction.ActorNumber,
                true,
                transaction.ItemName +
                " 1개 판매 완료: +" +
                transaction.SalePrice +
                "원" +
                (
                    remainingCount > 0
                        ? "\n남은 수량: " +
                          remainingCount +
                          "개"
                        : string.Empty
                ),
                transaction.SalePrice,
                newBalance,
                transaction.SlotId,
                transaction.ItemId);
        }

        private void CleanupExpiredSaleTransactions()
        {
            if (pendingSaleTransactions.Count == 0)
            {
                return;
            }

            double now = PhotonNetwork.Time;
            List<int> expired = new List<int>();

            foreach (KeyValuePair<int, PendingSaleTransaction> pair
                in pendingSaleTransactions)
            {
                if (pair.Value == null ||
                    now - pair.Value.CreatedAt >
                        RequestTimeoutSeconds)
                {
                    expired.Add(pair.Key);
                }
            }

            for (int i = 0;
                 i < expired.Count;
                 i++)
            {
                PendingSaleTransaction transaction;

                if (!pendingSaleTransactions.TryGetValue(
                        expired[i],
                        out transaction))
                {
                    continue;
                }

                pendingSaleTransactions.Remove(expired[i]);

                if (transaction != null)
                {
                    reservedOrSoldItemGuids.Remove(
                        transaction.ItemGuid);

                    SendSellResult(
                        transaction.ActorNumber,
                        false,
                        "판매 확인 시간이 초과되었습니다. 다시 시도하세요.",
                        0,
                        ReadSharedMoney(),
                        transaction.SlotId,
                        transaction.ItemId);
                }
            }
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


        private static int GetSellPrice(
            ushort itemId)
        {
            switch (itemId)
            {
                // Common 판매가
                case 28:
                case 72:
                case 69:
                    return 1;

                // Normal 판매가
                case 14:
                case 13:
                case 15:
                case 99:
                    return 3;

                // Rare 판매가
                case 34:
                case 49:
                    return 7;

                // Unique 판매가
                case 51:
                    return 15;

                // Legendary 판매가
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
        // 제작 요청 검증, 재료 소비와 완성품 지급
        // -----------------------------------------------------------------

        private static bool IsRealCraftGrantSlot(
            global::Player player,
            ItemSlot grantedSlot,
            ushort expectedItemId)
        {
            if (player == null ||
                player.itemSlots == null ||
                grantedSlot == null ||
                grantedSlot.IsEmpty() ||
                grantedSlot.prefab == null ||
                grantedSlot.prefab.itemID != expectedItemId)
            {
                return false;
            }

            // requester.tempFullSlot과 동일한 슬롯은 제작 완성품의 유효한 임시 손 지급 위치로 인정합니다.
            if (IsTemporaryCraftGrantSlot(
                    player,
                    grantedSlot))
            {
                return true;
            }

            for (int i = 0;
                 i < player.itemSlots.Length;
                 i++)
            {
                ItemSlot normalSlot =
                    player.itemSlots[i];

                if (ReferenceEquals(
                        normalSlot,
                        grantedSlot))
                {
                    return true;
                }

                if (normalSlot != null &&
                    normalSlot.itemSlotID == grantedSlot.itemSlotID &&
                    !normalSlot.IsEmpty() &&
                    normalSlot.prefab != null &&
                    normalSlot.prefab.itemID == expectedItemId)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsTemporaryCraftGrantSlot(
            global::Player player,
            ItemSlot slot)
        {
            if (player == null ||
                slot == null)
            {
                return false;
            }

            ItemSlot temp =
                player.tempFullSlot;

            return
                ReferenceEquals(
                    temp,
                    slot) ||
                (temp != null &&
                 temp.itemSlotID == slot.itemSlotID);
        }

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

            int currentResourceLevel =
                GetCurrentResourceLevel();

            if (currentResourceLevel <
                recipe.RequiredResourceLevel)
            {
                SendCraftResult(
                    actorNumber,
                    false,
                    false,
                    outputId,
                    GetResourceGradeName(
                        recipe.RequiredResourceLevel) +
                    " 제작 등급이 필요합니다.");

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

            // 호스트가 출력 공간을 다시 확인하고, 공간이 없으면 돈과 재료를 소비하기 전에 거부합니다.
            if (!requester.HasEmptySlot(
                    outputId))
            {
                SendCraftResult(
                    actorNumber,
                    false,
                    false,
                    outputId,
                    "완성품을 받을 빈 슬롯이 없어 제작할 수 없습니다.");

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

            if (!TryBuildCraftConsumptionPlan(
                    recipe,
                    out plan,
                    out missingMessage,
                    requester))
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

            ItemSlot grantedSlot;
            bool granted =
                requester.AddItem(
                    outputId,
                    null,
                    out grantedSlot);

            bool inventoryOrHandGrant =
                granted &&
                IsRealCraftGrantSlot(
                    requester,
                    grantedSlot,
                    outputId);

            bool grantedToHand =
                inventoryOrHandGrant &&
                IsTemporaryCraftGrantSlot(
                    requester,
                    grantedSlot);

            if (!inventoryOrHandGrant)
            {
                // 완성품 지급에 실패하면 공유 돈만 환불하고 월드 아이템은 생성하지 않습니다.
                SetSharedMoneyOnHost(
                    ReadSharedMoney() +
                    recipe.MoneyCost);

                SendCraftResult(
                    actorNumber,
                    true,
                    false,
                    outputId,
                    "제작 완성품을 인벤토리에 지급하지 못했습니다.\n" +
                    "돈은 환불됐지만 재료는 복구되지 않았습니다.");

                Logger.LogError(
                    "Craft output inventory delivery failed. Actor=" +
                    actorNumber +
                    " | OutputID=" +
                    outputId +
                    " | Granted=" +
                    granted +
                    " | GrantedSlot=" +
                    (
                        grantedSlot != null
                            ? grantedSlot.itemSlotID.ToString()
                            : "<null>"
                    ));

                return;
            }

            if (grantedToHand)
            {
                // 임시 손 슬롯으로 지급된 로컬 아이템은 슬롯 250을 선택해 직접 장착합니다.
                if (requester.photonView != null &&
                    requester.photonView.IsMine)
                {
                    StartCoroutine(
                        EquipCraftedTempHandLocally(
                            requester,
                            outputId));
                }
            }

            SendCraftResult(
                actorNumber,
                true,
                true,
                outputId,
                recipe.DisplayName +
                (
                    grantedToHand
                        ? " 제작 성공! 추가 손 슬롯에 장착했습니다."
                        : " 제작에 성공했습니다!"
                ));

            if (outputId ==
                FlareItemId)
            {
                MarkFinalFlareCompletedAndNotify();
            }

            Logger.LogInfo(
                "Craft succeeded. Actor=" + actorNumber +
                " | OutputID=" + outputId +
                " | Destination=" +
                (
                    grantedToHand
                        ? "SelectedHand250"
                        : "Inventory"
                ) +
                " | Slot=" +
                (grantedSlot != null ? grantedSlot.itemSlotID.ToString() : "<none>") +
                " | Money=" + recipe.MoneyCost);
        }

        private IEnumerator EquipCraftedTempHandLocally(
            global::Player requester,
            ushort expectedItemId)
        {
            yield return null;

            if (requester == null ||
                requester.tempFullSlot == null ||
                requester.tempFullSlot.IsEmpty() ||
                requester.tempFullSlot.prefab == null ||
                requester.tempFullSlot.prefab.itemID !=
                    expectedItemId ||
                requester.character == null ||
                requester.character.refs == null ||
                requester.character.refs.items == null)
            {
                Logger.LogWarning(
                    "Local crafted temp-hand equip skipped. " +
                    "ExpectedItemID=" +
                    expectedItemId);

                yield break;
            }

            string tempGuid =
                requester.tempFullSlot.data != null
                    ? requester.tempFullSlot.data.guid.ToString()
                    : string.Empty;

            if (!string.IsNullOrEmpty(
                    tempGuid) &&
                !pendingTempHandDrawGuids.Add(
                    tempGuid))
            {
                Logger.LogInfo(
                    "Duplicate crafted temp-hand draw request ignored. " +
                    "ItemID=" +
                    expectedItemId +
                    " | Guid=" +
                    tempGuid);

                yield break;
            }

            CharacterItems items =
                requester.character.refs.items;

            items.EquipSlot(
                Optionable<byte>.Some(
                    250));

            yield return
                new WaitForSecondsRealtime(
                    0.20f);

            bool selectedTemp =
                items.currentSelectedSlot.IsSome &&
                items.currentSelectedSlot.Value ==
                    250;

            Item currentItem =
                requester.character.data != null
                    ? requester.character.data.currentItem
                    : null;

            bool heldCorrectly =
                currentItem != null &&
                currentItem.itemID ==
                    expectedItemId &&
                currentItem.itemState ==
                    ItemState.Held;

            if (!selectedTemp ||
                !heldCorrectly)
            {
                Logger.LogWarning(
                    "First local crafted temp-hand equip did not complete. " +
                    "Retrying once. ExpectedItemID=" +
                    expectedItemId +
                    " | Selected250=" +
                    selectedTemp +
                    " | CurrentItem=" +
                    (
                        currentItem != null
                            ? currentItem.itemID.ToString()
                            : "<null>"
                    ));

                items.EquipSlot(
                    Optionable<byte>.Some(
                        250));

                yield return
                    new WaitForSecondsRealtime(
                        0.20f);

                selectedTemp =
                    items.currentSelectedSlot.IsSome &&
                    items.currentSelectedSlot.Value ==
                        250;

                currentItem =
                    requester.character.data != null
                        ? requester.character.data.currentItem
                        : null;

                heldCorrectly =
                    currentItem != null &&
                    currentItem.itemID ==
                        expectedItemId &&
                    currentItem.itemState ==
                        ItemState.Held;
            }

            if (selectedTemp &&
                heldCorrectly)
            {
                Logger.LogInfo(
                    "Crafted temp-hand item drawn successfully. " +
                    "ItemID=" +
                    expectedItemId +
                    " | Slot=250" +
                    " | ViewID=" +
                    (
                        currentItem.photonView != null
                            ? currentItem.photonView.ViewID.ToString()
                            : "<null>"
                    ));
            }
            else
            {
                Logger.LogError(
                    "Crafted temp-hand item draw failed. " +
                    "ItemID=" +
                    expectedItemId +
                    " | Selected250=" +
                    selectedTemp +
                    " | TempSlot=" +
                    (
                        requester.tempFullSlot != null &&
                        !requester.tempFullSlot.IsEmpty() &&
                        requester.tempFullSlot.prefab != null
                            ? requester.tempFullSlot.prefab.itemID.ToString()
                            : "<empty>"
                    ) +
                    " | CurrentItem=" +
                    (
                        currentItem != null
                            ? currentItem.itemID.ToString()
                            : "<null>"
                    ));
            }

            if (!string.IsNullOrEmpty(
                    tempGuid))
            {
                pendingTempHandDrawGuids.Remove(
                    tempGuid);
            }
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
            out string missingMessage,
            global::Player sourcePlayer = null)
        {
            plan = new CraftConsumptionPlan();
            missingMessage = string.Empty;

            List<IngredientLocation> locations =
                CollectPartyIngredientLocations(
                    sourcePlayer);

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

        private static List<IngredientLocation> CollectPartyIngredientLocations(
            global::Player sourcePlayer = null)
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

                if (sourcePlayer != null &&
                    !ReferenceEquals(
                        character.player,
                        sourcePlayer))
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
                !IsCraftIngredientId(slot.prefab.itemID))
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
        // 통합 Canvas UI 생성
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
                // HubTab.Developer는 tabNames에 포함하지 않아 탭 버튼을 생성하지 않습니다.
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
                        78f),
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
                            Image>(),
                        tabLabel));
            }


            TextMeshProUGUI languageTitle =
                CreateText(
                    "LanguageTitle",
                    sidebar.transform,
                    font,
                    "Language",
                    16f,
                    TextAlignmentOptions.Center);

            languageTitle.fontStyle =
                FontStyles.Bold;

            Anchor(
                languageTitle.rectTransform,
                new Vector2(
                    0.5f,
                    1f),
                new Vector2(
                    0.5f,
                    1f),
                new Vector2(
                    0f,
                    -552f),
                new Vector2(
                    165f,
                    24f));

            List<LiteLanguageView> languages =
                new List<LiteLanguageView>();

            HubLanguage[] languageValues =
            {
                HubLanguage.English,
                HubLanguage.Korean,
                HubLanguage.Chinese,
                HubLanguage.Japanese,
                HubLanguage.French
            };

            string[] languageNames =
            {
                "English",
                "한국어",
                "中文",
                "日本語",
                "Français"
            };

            for (int i = 0;
                 i < languageValues.Length;
                 i++)
            {
                HubLanguage capturedLanguage =
                    languageValues[i];

                TextMeshProUGUI languageLabel;

                Button languageButton =
                    CreateButton(
                        "Language_" +
                        capturedLanguage,
                        sidebar.transform,
                        font,
                        languageNames[i],
                        new Color(
                            0.18f,
                            0.19f,
                            0.22f,
                            1f),
                        Color.white,
                        out languageLabel);

                bool firstRow =
                    i <
                    3;

                float xPosition =
                    firstRow
                        ? -56f +
                          i *
                          56f
                        : -40f +
                          (
                              i -
                              3
                          ) *
                          80f;

                float yPosition =
                    firstRow
                        ? -584f
                        : -618f;

                float width =
                    firstRow
                        ? 52f
                        : 76f;

                Anchor(
                    languageButton.GetComponent<
                        RectTransform>(),
                    new Vector2(
                        0.5f,
                        1f),
                    new Vector2(
                        0.5f,
                        1f),
                    new Vector2(
                        xPosition,
                        yPosition),
                    new Vector2(
                        width,
                        28f));

                languageLabel.fontSize =
                    11.5f;

                languageLabel.textWrappingMode =
                    TextWrappingModes.NoWrap;

                languageButton.onClick.AddListener(
                    new UnityAction(
                        delegate
                        {
                            SelectLanguage(
                                capturedLanguage);
                        }));

                languages.Add(
                    new LiteLanguageView(
                        capturedLanguage,
                        languageButton,
                        languageButton.GetComponent<
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

            List<LiteCraftCategoryView>
                craftCategoryTabs =
                    new List<LiteCraftCategoryView>();

            string[] craftCategoryNames =
            {
                "등산",
                "음식",
                "힐",
                "부활",
                "필수"
            };

            for (int i = 0;
                 i < craftCategoryNames.Length;
                 i++)
            {
                CraftUiCategory category =
                    (CraftUiCategory)i;

                CraftUiCategory capturedCategory =
                    category;

                TextMeshProUGUI categoryLabel;

                Button categoryButton =
                    CreateButton(
                        "CraftCategory_" +
                        category,
                        panel.transform,
                        font,
                        craftCategoryNames[i],
                        new Color(
                            0.18f,
                            0.19f,
                            0.22f,
                            1f),
                        Color.white,
                        out categoryLabel);

                Anchor(
                    categoryButton.GetComponent<
                        RectTransform>(),
                    new Vector2(
                        0f,
                        1f),
                    new Vector2(
                        0f,
                        1f),
                    new Vector2(
                        295f +
                        i *
                        118f,
                        -105f),
                    new Vector2(
                        108f,
                        42f));

                categoryLabel.fontSize =
                    17f;

                categoryButton.onClick.AddListener(
                    new UnityAction(
                        delegate
                        {
                            SelectCraftUiCategory(
                                capturedCategory);
                        }));

                craftCategoryTabs.Add(
                    new LiteCraftCategoryView(
                        category,
                        categoryButton,
                        categoryButton.GetComponent<
                            Image>(),
                        categoryLabel));
            }

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
                        -165f -
                        i *
                        61f),
                    new Vector2(
                        590f,
                        50f));

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

                GameObject iconObject =
                    new GameObject(
                        "CraftIcon",
                        typeof(RectTransform),
                        typeof(CanvasRenderer),
                        typeof(RawImage));

                iconObject.transform.SetParent(
                    rowButton.transform,
                    false);

                RectTransform iconRect =
                    iconObject.GetComponent<
                        RectTransform>();

                Anchor(
                    iconRect,
                    new Vector2(
                        0f,
                        0.5f),
                    new Vector2(
                        0f,
                        0.5f),
                    new Vector2(
                        27f,
                        0f),
                    new Vector2(
                        40f,
                        40f));

                RawImage rowIcon =
                    iconObject.GetComponent<
                        RawImage>();

                rowIcon.raycastTarget =
                    false;

                iconObject.SetActive(
                    false);

                RectTransform labelRect =
                    rowLabel.rectTransform;

                labelRect.offsetMin =
                    new Vector2(
                        56f,
                        labelRect.offsetMin.y);

                rows.Add(
                    new LiteRowView(
                        rowButton,
                        rowButton.GetComponent<
                            Image>(),
                        rowLabel,
                        rowIcon));
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
                    "강화",
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

            TextMeshProUGUI tempHandClearLabel;

            Button tempHandClear =
                CreateButton(
                    "TempHandClear",
                    panel.transform,
                    font,
                    "장착 해제",
                    new Color(
                        0.52f,
                        0.24f,
                        0.24f,
                        1f),
                    Color.white,
                    out tempHandClearLabel);

            Anchor(
                tempHandClear.GetComponent<
                    RectTransform>(),
                new Vector2(
                    0f,
                    0f),
                new Vector2(
                    0f,
                    0f),
                new Vector2(
                    980f,
                    30f),
                new Vector2(
                    170f,
                    48f));

            tempHandClear.onClick.AddListener(
                new UnityAction(
                    ClearLocalTempHandSlot));

            window.SetReferences(
                tabs,
                craftCategoryTabs,
                languages,
                rows,
                title,
                balance,
                languageTitle,
                help,
                explanation,
                detailPanel.gameObject,
                detail,
                status,
                action,
                actionLabel,
                page,
                previous,
                previousLabel,
                next,
                nextLabel,
                tempHandClear,
                tempHandClearLabel,
                close,
                closeLabel);
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
                            (int)UpgradeKind.SellValue)
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

                case HubTab.Developer:
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

                case HubTab.Developer:
                    RequestDeveloperMoney();
                    break;
            }
        }

        private static string BuildExplanationText()
        {
            switch (GetCurrentLanguage())
            {
                case HubLanguage.Korean:
                    return
                        "Craft PEAK는 PEAK의 등산을 자원 수집과 제작 중심의 크래프팅 게임으로 바꾸는 모드입니다. " +
                        "게임 중 P키를 누르면 통합 상점이 열리며 설명, 강화, 제작, 판매, 부품 탭을 사용할 수 있습니다.\n\n" +
                        "맵에 흩어진 자원을 모아 판매하면 파티 공유 돈을 얻습니다. 공유 돈과 재료는 강화, 장비 제작, " +
                        "비행기 부품 구매에 함께 사용됩니다. 강화 탭에서 제작 등급을 Common에서 Normal, Rare, Unique, " +
                        "Legendary 순서로 올려야 다음 단계의 자원과 제작식이 열립니다. 상위 제작품은 반드시 이전 단계 제작품을 재료로 요구합니다.\n\n" +
                        "모닥불은 단순한 휴식 장소가 아니라 다음 세그먼트로 이동하기 위한 진행 트리거입니다. " +
                        "해안, 열대/뿌리숲, 메사/고산지대, 칼데라에서는 현재 구간에 맞는 제작 등급과 비행기 부품이 필요합니다. " +
                        "부품은 인벤토리에 들어가지 않고 Photon 방의 공동 진행 상태로 저장되며, 모닥불을 성공적으로 켤 때 사용 완료됩니다.\n\n" +
                        "진행 순서는 해안 → 열대/뿌리숲 → 메사/고산지대 → 칼데라 → 가마 → 정상입니다. " +
                        "가마에서 정상으로 갈 때는 모닥불과 비행기 부품을 사용하지 않습니다. 정상에 도착한 뒤 제작 탭에서 Legendary 재료가 들어가는 " +
                        "가장 비싼 최종 조명탄을 제작하면 최종 탈출 신호가 완성됩니다.\n\n" +
                        "핵심 흐름은 자원 수집 → 판매 → 강화와 제작 → 비행기 부품 구매 → 모닥불 점화 → 다음 구간 이동입니다.";

                case HubLanguage.Chinese:
                    return
                        "Craft PEAK 将 PEAK 的攀登玩法改造成以资源收集和制作为核心的生存制作模式。游戏中按 P 可打开综合商店，使用说明、升级、制作、出售和部件标签。\n\n" +
                        "收集地图中的资源并出售，可获得队伍共享资金。共享资金与材料用于升级、制作装备和购买飞机部件。必须在升级标签中按 Common、Normal、Rare、Unique、Legendary 的顺序提升制作等级，才能解锁后续资源与配方。高级物品会要求前一阶段的制作品作为材料。\n\n" +
                        "篝火不仅是休息地点，也是前往下一区域的推进触发器。在海滩、热带/根系森林、台地/高山和火山口区域，需要满足当前区域对应的制作等级并拥有指定飞机部件。部件不会进入背包，而是保存为 Photon 房间的共享进度，并在成功点燃篝火时消耗。\n\n" +
                        "推进顺序为海滩 → 热带/根系森林 → 台地/高山 → 火山口 → 熔炉 → 山顶。从熔炉前往山顶时不使用篝火或飞机部件。抵达山顶后，在制作标签中制作需要 Legendary 材料、价格最高的最终信号弹，即可完成最终逃脱信号。\n\n" +
                        "核心流程：收集资源 → 出售 → 升级与制作 → 购买飞机部件 → 点燃篝火 → 前往下一区域。";

                case HubLanguage.Japanese:
                    return
                        "Craft PEAKは、PEAKの登山を資源収集とクラフト中心のゲームへ変えるMODです。ゲーム中にPキーを押すと統合ショップが開き、説明・強化・クラフト・売却・部品タブを使用できます。\n\n" +
                        "マップ上の資源を集めて売却すると、パーティー共有資金を獲得できます。共有資金と素材は、強化、装備のクラフト、飛行機部品の購入に使用します。強化タブでクラフト等級をCommon、Normal、Rare、Unique、Legendaryの順に上げると、次の資源とレシピが解放されます。上位の完成品には前段階の完成品が素材として必要です。\n\n" +
                        "焚き火は休憩場所だけでなく、次の区間へ進むための進行トリガーです。海岸、熱帯/根の森、メサ/高山、カルデラでは、現在の区間に対応するクラフト等級と飛行機部品が必要です。部品はインベントリには入らず、Photonルームの共有進行状態として保存され、焚き火の点火成功時に使用済みになります。\n\n" +
                        "進行順は海岸 → 熱帯/根の森 → メサ/高山 → カルデラ → 窯 → 山頂です。窯から山頂へ進む際は焚き火と飛行機部品を使用しません。山頂到達後、クラフトタブでLegendary素材を使う最も高価な最終フレアを作成すると、最終脱出信号が完成します。\n\n" +
                        "基本の流れは、資源収集 → 売却 → 強化とクラフト → 飛行機部品購入 → 焚き火点火 → 次の区間へ移動、です。";

                case HubLanguage.French:
                    return
                        "Craft PEAK transforme l’ascension de PEAK en un jeu de fabrication centré sur la collecte de ressources. Pendant une partie, appuyez sur P pour ouvrir la boutique unifiée et accéder aux onglets Description, Améliorations, Fabrication, Vente et Pièces.\n\n" +
                        "Ramassez les ressources dispersées sur la carte puis vendez-les pour gagner de l’argent partagé par le groupe. Cet argent et les matériaux servent aux améliorations, à la fabrication d’équipement et à l’achat de pièces d’avion. Dans l’onglet Améliorations, augmentez le niveau de fabrication dans l’ordre Common, Normal, Rare, Unique puis Legendary afin de débloquer les ressources et recettes suivantes. Les objets avancés exigent des objets fabriqués au niveau précédent.\n\n" +
                        "Les feux de camp ne sont pas seulement des lieux de repos : ils déclenchent la progression vers le segment suivant. Sur la Plage, dans les Tropiques/Forêt de racines, sur le Mesa/Alpin et dans la Caldeira, vous devez posséder le niveau de fabrication et la pièce d’avion correspondant au segment actuel. Les pièces ne vont pas dans l’inventaire ; elles sont enregistrées dans l’état partagé du salon Photon et sont consommées lorsqu’un feu de camp est allumé avec succès.\n\n" +
                        "L’ordre de progression est Plage → Tropiques/Forêt de racines → Mesa/Alpin → Caldeira → Four → Sommet. Le passage du Four au Sommet n’utilise ni feu de camp ni pièce d’avion. Une fois au Sommet, fabriquez dans l’onglet Fabrication la fusée finale la plus coûteuse, qui exige des matériaux Legendary, pour terminer le signal d’évacuation.\n\n" +
                        "Boucle principale : collecter → vendre → améliorer et fabriquer → acheter les pièces d’avion → allumer le feu de camp → rejoindre le segment suivant.";

                case HubLanguage.English:
                default:
                    return
                        "Craft PEAK turns PEAK’s climb into a crafting game focused on gathering resources. Press P during a run to open the unified shop and use the Description, Upgrades, Crafting, Sell, and Parts tabs.\n\n" +
                        "Gather resources across the map and sell them to earn party-shared money. Shared money and materials are used for upgrades, equipment crafting, and aircraft parts. In the Upgrades tab, raise the crafting grade in order from Common to Normal, Rare, Unique, and Legendary to unlock later resources and recipes. Higher-tier items require crafted items from the previous tier as ingredients.\n\n" +
                        "Campfires are not only rest points; they trigger progression to the next segment. At the Beach, Tropics/Roots, Mesa/Alpine, and Caldera, you need the crafting grade and aircraft part assigned to the current segment. Parts do not enter the inventory. They are stored as shared Photon-room progression and are consumed when the campfire is lit successfully.\n\n" +
                        "The route is Beach → Tropics/Roots → Mesa/Alpine → Caldera → Kiln → Peak. Moving from the Kiln to the Peak does not use a campfire or aircraft part. After reaching the Peak, craft the most expensive final flare with Legendary materials in the Crafting tab to complete the escape signal.\n\n" +
                        "Core loop: gather resources → sell → upgrade and craft → purchase aircraft parts → light the campfire → move to the next segment.";
            }
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
                GetResourceGradeName(
                    recipe.RequiredResourceLevel) +
                " / " +
                recipe.Category +
                "  |  " +
                recipe.MoneyCost +
                "원";
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
                    "원");
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
                GetResourceGradeName(
                    recipe.RequiredResourceLevel));

            builder.Append(
                " / ");

            builder.Append(
                recipe.Category);

            builder.Append(
                "\n\n");

            builder.Append(
                requirements);

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

        private static HubLanguage GetCurrentLanguage()
        {
            return
                Instance != null
                    ? Instance.currentLanguage
                    : HubLanguage.English;
        }

        private static string LocalizeUiText(
            string text)
        {
            string result =
                text ??
                string.Empty;

            if (result.Length ==
                0)
            {
                return result;
            }

            HubLanguage language =
                GetCurrentLanguage();

            for (int i = 0;
                 i <
                     UiTranslationEntries.Length;
                 i++)
            {
                UiTranslationEntry entry =
                    UiTranslationEntries[i];

                if (entry == null ||
                    string.IsNullOrEmpty(
                        entry.Source) ||
                    result.IndexOf(
                        entry.Source,
                        StringComparison.Ordinal) <
                    0)
                {
                    continue;
                }

                result =
                    result.Replace(
                        entry.Source,
                        entry.Get(
                            language));
            }

            if (language ==
                HubLanguage.Korean)
            {
                return result;
            }

            return
                LocalizeNumericUnits(
                    result,
                    language);
        }

        private static string LocalizeNumericUnits(
            string text,
            HubLanguage language)
        {
            if (string.IsNullOrEmpty(
                    text))
            {
                return
                    text ??
                    string.Empty;
            }

            string result =
                Regex.Replace(
                    text,
                    @"(\d+)번 슬롯",
                    delegate (
                        Match match)
                    {
                        string number =
                            match.Groups[1]
                                .Value;

                        switch (language)
                        {
                            case HubLanguage.Chinese:
                                return
                                    "栏位 " +
                                    number;

                            case HubLanguage.Japanese:
                                return
                                    "スロット" +
                                    number;

                            case HubLanguage.French:
                                return
                                    "Emplacement " +
                                    number;

                            case HubLanguage.English:
                            default:
                                return
                                    "Slot " +
                                    number;
                        }
                    });

            result =
                Regex.Replace(
                    result,
                    @"(\d+)번째",
                    delegate (
                        Match match)
                    {
                        string number =
                            match.Groups[1]
                                .Value;

                        switch (language)
                        {
                            case HubLanguage.Chinese:
                                return
                                    "第" +
                                    number;

                            case HubLanguage.Japanese:
                                return
                                    number +
                                    "番目";

                            case HubLanguage.French:
                                return
                                    "Étape " +
                                    number;

                            case HubLanguage.English:
                            default:
                                return
                                    "Stage " +
                                    number;
                        }
                    });

            result =
                Regex.Replace(
                    result,
                    @"(\d+)단계",
                    delegate (
                        Match match)
                    {
                        string number =
                            match.Groups[1]
                                .Value;

                        switch (language)
                        {
                            case HubLanguage.Chinese:
                                return
                                    number +
                                    "级";

                            case HubLanguage.Japanese:
                                return
                                    "レベル" +
                                    number;

                            case HubLanguage.French:
                                return
                                    "Niveau " +
                                    number;

                            case HubLanguage.English:
                            default:
                                return
                                    "Level " +
                                    number;
                        }
                    });

            result =
                Regex.Replace(
                    result,
                    @"(\d+)개",
                    delegate (
                        Match match)
                    {
                        string number =
                            match.Groups[1]
                                .Value;

                        switch (language)
                        {
                            case HubLanguage.Chinese:
                                return
                                    number +
                                    "个";

                            case HubLanguage.Japanese:
                                return
                                    number +
                                    "個";

                            case HubLanguage.French:
                                return
                                    number +
                                    (
                                        number ==
                                            "1"
                                            ? " unité"
                                            : " unités"
                                    );

                            case HubLanguage.English:
                            default:
                                return
                                    number +
                                    (
                                        number ==
                                            "1"
                                            ? " unit"
                                            : " units"
                                    );
                        }
                    });

            result =
                Regex.Replace(
                    result,
                    @"(\d+)원",
                    delegate (
                        Match match)
                    {
                        string number =
                            match.Groups[1]
                                .Value;

                        switch (language)
                        {
                            case HubLanguage.Chinese:
                                return
                                    number +
                                    "金币";

                            case HubLanguage.Japanese:
                                return
                                    number +
                                    "コイン";

                            case HubLanguage.French:
                                return
                                    number +
                                    (
                                        number ==
                                            "1"
                                            ? " pièce"
                                            : " pièces"
                                    );

                            case HubLanguage.English:
                            default:
                                return
                                    number +
                                    (
                                        number ==
                                            "1"
                                            ? " coin"
                                            : " coins"
                                    );
                        }
                    });

            return result;
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
                LocalizeUiText(
                    text);

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
                LocalizeUiText(
                    text);

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
            private readonly TextMeshProUGUI label;

            public LiteTabView(
                HubTab value,
                Button tabButton,
                Image tabImage,
                TextMeshProUGUI tabLabel)
            {
                tab =
                    value;

                button =
                    tabButton;

                image =
                    tabImage;

                label =
                    tabLabel;
            }

            public void Refresh(
                HubTab selectedTab)
            {
                SetTextIfChanged(
                    label,
                    GetHubTabDisplayName(
                        tab));

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

        private sealed class LiteCraftCategoryView
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

            private readonly CraftUiCategory category;
            private readonly Button button;
            private readonly Image image;
            private readonly TextMeshProUGUI label;

            public LiteCraftCategoryView(
                CraftUiCategory value,
                Button categoryButton,
                Image categoryImage,
                TextMeshProUGUI categoryLabel)
            {
                category =
                    value;

                button =
                    categoryButton;

                image =
                    categoryImage;

                label =
                    categoryLabel;
            }

            public void Refresh(
                bool visible,
                CraftUiCategory selectedCategory)
            {
                SetTextIfChanged(
                    label,
                    GetCraftUiCategoryName(
                        category));

                SetActiveIfChanged(
                    button != null
                        ? button.gameObject
                        : null,
                    visible);

                if (!visible)
                {
                    return;
                }

                if (image != null)
                {
                    Color target =
                        category ==
                            selectedCategory
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
                    category !=
                        selectedCategory);
            }
        }

        private sealed class LiteLanguageView
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

            private readonly HubLanguage language;
            private readonly Button button;
            private readonly Image image;

            public LiteLanguageView(
                HubLanguage value,
                Button languageButton,
                Image languageImage)
            {
                language =
                    value;

                button =
                    languageButton;

                image =
                    languageImage;
            }

            public void Refresh(
                HubLanguage selectedLanguage)
            {
                if (image != null)
                {
                    Color target =
                        language ==
                            selectedLanguage
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
                    language !=
                        selectedLanguage);
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
            private readonly RawImage icon;

            public LiteRowView(
                Button rowButton,
                Image rowImage,
                TextMeshProUGUI rowLabel,
                RawImage rowIcon)
            {
                button =
                    rowButton;

                image =
                    rowImage;

                label =
                    rowLabel;

                icon =
                    rowIcon;
            }

            public void Refresh(
                bool active,
                bool selected,
                string text,
                Texture iconTexture = null,
                bool showIcon = false)
            {
                SetActiveIfChanged(
                    button.gameObject,
                    active);

                if (!active)
                {
                    if (icon != null)
                    {
                        SetActiveIfChanged(
                            icon.gameObject,
                            false);
                    }

                    return;
                }

                if (icon != null)
                {
                    bool iconVisible =
                        showIcon &&
                        iconTexture != null;

                    if (icon.texture !=
                        iconTexture)
                    {
                        icon.texture =
                            iconTexture;
                    }

                    SetActiveIfChanged(
                        icon.gameObject,
                        iconVisible);
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

            private List<LiteCraftCategoryView>
                craftCategoryTabs =
                    new List<LiteCraftCategoryView>();

            private List<LiteLanguageView> languages =
                new List<LiteLanguageView>();

            private List<LiteRowView> rows =
                new List<LiteRowView>();

            private TextMeshProUGUI title;
            private TextMeshProUGUI balance;
            private TextMeshProUGUI languageTitle;
            private TextMeshProUGUI help;
            private TextMeshProUGUI explanation;
            private GameObject detailPanel;
            private TextMeshProUGUI detail;
            private TextMeshProUGUI status;
            private TextMeshProUGUI actionLabel;
            private TextMeshProUGUI page;
            private TextMeshProUGUI previousLabel;
            private TextMeshProUGUI nextLabel;
            private TextMeshProUGUI tempHandClearLabel;
            private TextMeshProUGUI closeLabel;

            private Button action;
            private Button previous;
            private Button next;
            private Button tempHandClear;
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
                List<LiteCraftCategoryView>
                    craftCategoryViews,
                List<LiteLanguageView>
                    languageViews,
                List<LiteRowView> rowViews,
                TextMeshProUGUI titleText,
                TextMeshProUGUI balanceText,
                TextMeshProUGUI languageTitleText,
                TextMeshProUGUI helpText,
                TextMeshProUGUI explanationText,
                GameObject detailPanelObject,
                TextMeshProUGUI detailText,
                TextMeshProUGUI statusText,
                Button actionButton,
                TextMeshProUGUI actionButtonLabel,
                TextMeshProUGUI pageText,
                Button previousButton,
                TextMeshProUGUI previousButtonLabel,
                Button nextButton,
                TextMeshProUGUI nextButtonLabel,
                Button tempHandClearButton,
                TextMeshProUGUI tempHandClearButtonLabel,
                Button closeButton,
                TextMeshProUGUI closeButtonLabel)
            {
                tabs =
                    tabViews ??
                    new List<LiteTabView>();

                craftCategoryTabs =
                    craftCategoryViews ??
                    new List<
                        LiteCraftCategoryView>();

                languages =
                    languageViews ??
                    new List<
                        LiteLanguageView>();

                rows =
                    rowViews ??
                    new List<LiteRowView>();

                title =
                    titleText;

                balance =
                    balanceText;

                languageTitle =
                    languageTitleText;

                help =
                    helpText;

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

                previousLabel =
                    previousButtonLabel;

                next =
                    nextButton;

                nextLabel =
                    nextButtonLabel;

                tempHandClear =
                    tempHandClearButton;

                tempHandClearLabel =
                    tempHandClearButtonLabel;

                close =
                    closeButton;

                closeLabel =
                    closeButtonLabel;
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

                for (int i = 0;
                     i < languages.Count;
                     i++)
                {
                    languages[i].Refresh(
                        owner.currentLanguage);
                }

                SetTextIfChanged(
                    languageTitle,
                    "Language");

                SetTextIfChanged(
                    help,
                    "P / ESC\n닫기");

                SetTextIfChanged(
                    previousLabel,
                    "◀ 이전");

                SetTextIfChanged(
                    nextLabel,
                    "다음 ▶");

                SetTextIfChanged(
                    closeLabel,
                    "닫기");

                SetTextIfChanged(
                    tempHandClearLabel,
                    "장착 해제");

                SetInteractableIfChanged(
                    tempHandClear,
                    owner.CanClearLocalTempHandSlot);

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

                for (int i = 0;
                     i < craftCategoryTabs.Count;
                     i++)
                {
                    craftCategoryTabs[i].Refresh(
                        tab ==
                            HubTab.Craft,
                        owner
                            .selectedCraftUiCategory);
                }

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

                    case HubTab.Developer:
                        RefreshDeveloper();
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
                        (int)UpgradeKind.SellValue;

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
                                : "강화"
                        ));
            }

            private void RefreshCraft()
            {
                PrepareInteractiveTab();

                SetTextIfChanged(
                    title,
                    "제작 · " +
                    GetCraftUiCategoryName(
                        owner
                            .selectedCraftUiCategory));

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
                            recipe),
                        GetCraftRecipeIcon(
                            recipe),
                        true);
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
                    GetCraftUiCategoryName(
                        owner
                            .selectedCraftUiCategory) +
                    " · 페이지 " +
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
                        : owner.selectedSellQuantity +
                          "개 판매");
            }

            private void RefreshDeveloper()
            {
                PrepareInteractiveTab();

                SetTextIfChanged(
                    title,
                    "개발자");

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

                SetTextIfChanged(
                    detail,
                    "개발자 테스트 치트\n\n" +
                    "클릭할 때마다 호스트에게 +100원 요청을 즉시 보냅니다.\n" +
                    "호스트는 각 요청을 독립적으로 순서대로 처리합니다.\n\n" +
                    "호스트 자신이 누른 경우에도 동일한 호스트 처리 경로를\n" +
                    "직접 실행하므로 요청이 누락되지 않습니다.\n" +
                    "최종 공유 돈은 Photon 방 전체에 동기화됩니다.");

                SetTextIfChanged(
                    status,
                    owner.developerStatus);

                SetInteractableIfChanged(
                    action,
                    PhotonNetwork.InRoom &&
                    PhotonNetwork.CurrentRoom != null);

                SetTextIfChanged(
                    actionLabel,
                    "공유 돈 +100");
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
                        PlanePartRecipes.Count;

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
    /// Item.RequestPickup 전후의 판매 자원 수량을 비교하고 호스트에서 수집량 배율만큼 부족한 수량을 추가합니다.
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
    /// CampfireGate.ProcessIgniteRequestOnHost 실행 전에 현재 구간 모닥불 여부와 진행 조건을 호스트에서 검증합니다.
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
    /// 현재 구간의 꺼진 모닥불 상호작용 문구 뒤에 CraftHub 진행 조건을 추가합니다.
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
    /// Light_Rpc 호출 전 출발 구간을 저장하고, 성공 후 호스트가 해당 구간 비행기 부품을 사용 처리합니다.
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
    /// CraftHub의 판매 탭, 공유 돈, 슬롯 선택과 판매 대기 상태를 노출하는 파사드입니다.
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
    /// CraftHub의 제작 탭, 공유 돈과 제작 대기 상태를 노출하는 파사드입니다.
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
    /// CraftHub의 자원 등급, 적재량, 다음 모닥불과 수집량 강화 기능을 노출하는 파사드입니다.
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
            StackCapacity = 1,
            CampfireEfficiency = 2,
            DoubleYield = 3
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
