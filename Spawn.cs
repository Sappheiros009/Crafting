// MAP-WIDE RARITY SPAWN + MODCONFIG BUILD 1.6.0
//
// 목표
// - 맵 전체에 판매용 자원 3000개를 공간 밀도에 맞춰 골고루 분산
// - 각 위치는 고정 슬롯으로 유지
// - 해당 위치의 아이템을 주운 순간 그 슬롯만 5분 재생성 타이머 시작
// - 업그레이드 단계별 등급 비중과 아이템별 비중을 ModConfig에서 조절
// - 나뭇가지 ID 28과 돌 ID 72를 고정 슬롯에 명시적으로 포함
//
// 위치 선정 방식
// - PEAK 씬에 실제 배치된 모든 Spawner의 spawnSpots/weightedSpawnSpots를 수집
// - BerryBush/BerryVine처럼 공중 자연물 위치는 제외
// - GroundPlaceSpawner는 원본처럼 Terrain/Map 방향 Raycast로 바닥점 계산
// - Segment별 동일한 수량을 할당
// - 같은 구간 안에서는 Farthest-Point 방식으로 서로 가장 먼 좌표부터 선택
//
// 중요
// - 프로젝트에는 Spawn 클래스가 들어 있는 파일을 이 파일 하나만 남기세요.
// - 기존 Spawn.cs 및 모든 Spawn_* 이전 파일은 프로젝트에서 제거하세요.
// - Log.cs는 진단이 끝났으므로 프로젝트에서 제거하는 편이 좋습니다.

using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Photon.Pun;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
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
        "com.github.PEAKModding.PEAKLib.ModConfig",
        BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class Spawn : BaseUnityPlugin
    {
        public const string PluginGuid =
            "com.sappheiros.crafting.spawn";

        public const string PluginName =
            "Craft PEAK Map Wide Fixed Slot Spawn";

        public const string PluginVersion =
            "1.6.0";

        private const int TargetSlotCount = 3000;

        private const float RespawnDelaySeconds = 300f;
        private const float StartupDelaySeconds = 2f;
        private const float InitializationTimeoutSeconds = 45f;
        private const float SlotMonitorIntervalSeconds = 0.25f;

        private const int InitialSpawnBatchSize = 15;
        private const int RespawnBatchSize = 5;

        private const float SpawnHeightOffset = 0.25f;
        private const float CandidateDeduplicationCellSize = 1.25f;

        // 3000개를 기존 spawnSpot만으로 채우면 같은 Spawner 주변 좌표가
        // 여러 개 선택될 수 있으므로, 원본 좌표 주변의 실제 지면 후보를 추가합니다.
        //
        // 최대 배치 범위는 30m로 유지합니다.
        //
        // 1.4.3은 각 방향마다 반경 하나만 검사했기 때문에,
        // 선택된 먼 반경이 절벽/허공이면 해당 방향 후보가 통째로 사라졌습니다.
        //
        // 이번 버전은 각 방향에서 30m부터 시작해 지면을 찾을 때까지
        // 24m, 18m, 12m, 6m 순서로 후퇴합니다.
        // 따라서 넓게 퍼지는 특성은 유지하면서도 기존 6m 후보를 안전망으로 보존합니다.
        private const int ExpansionDirectionCount = 8;
        private const float ExpansionMinimumRadius = 6.0f;
        private const float ExpansionMaximumRadius = 30.0f;
        private const float ExpansionRayHeight = 35f;
        private const float ExpansionRayDistance = 120f;

        private static readonly float[] ExpansionRadiusPasses =
        {
            30.0f,
            24.0f,
            18.0f,
            12.0f,
            6.0f
        };

        // Segment마다 동일 개수를 강제하지 않고 실제로 차지하는 공간 셀 수에
        // 비례하여 슬롯을 할당합니다.
        private const float CoverageCellSize = 20f;
        private const int MinimumSlotsPerSegment = 11;

        // 후보 생성 범위는 최대 30m를 유지하지만,
        // 실제 3000개 선택은 15m부터 시작해 과도한 연산과 선택 실패를 줄입니다.
        // 공간이 부족할 때만 단계적으로 기존 최소 간격까지 완화합니다.
        private static readonly float[] SlotSpacingPasses =
        {
            15.0f,
            12.0f,
            10.0f,
            8.0f,
            6.0f,
            5.0f,
            4.0f,
            3.25f,
            2.75f,
            2.25f
        };

        private const float GroundRayStartOffset = 2f;
        private const float GroundRayDistance = 120f;
        private const float MinimumGroundNormalY = 0.30f;

        private const float InactivePickupConfirmationSeconds = 1.5f;

        public enum ResourceUpgradeGrade
        {
            Common = 0,
            Normal = 1,
            Rare = 2,
            Unique = 3,
            Legendary = 4
        }

        private enum ResourceRarity
        {
            Common = 0,
            Normal = 1,
            Rare = 2,
            Unique = 3,
            Legendary = 4
        }

        // 업그레이드 단계별 기본 등급 비중입니다.
        // ModConfig 값이 준비되지 않은 경우에도 기존 확률표를 그대로 사용합니다.
        private static readonly float[,] DefaultRarityWeights =
        {
            // Common, Normal, Rare, Unique, Legendary
            { 100f,  0f,  0f, 0f, 0f }, // Common 업그레이드
            {  80f, 20f,  0f, 0f, 0f }, // Normal 업그레이드
            {  65f, 25f, 10f, 0f, 0f }, // Rare 업그레이드
            {  55f, 25f, 15f, 5f, 0f }, // Unique 업그레이드
            {  50f, 25f, 15f, 8f, 2f }  // Legendary 업그레이드
        };

        // PEAKLib.ModConfig는 BepInEx ConfigEntry를 자동으로 표시합니다.
        // 잠기지 않은 조합만 Bind하여 다음 등급이 열리기 전에는
        // 상위 등급이 설정 화면과 실제 추첨 모두에 들어오지 않게 합니다.
        private static readonly ConfigEntry<float>[,]
            RarityWeightConfigs =
                new ConfigEntry<float>[5, 5];

        // 같은 등급 안에서 각 아이템이 선택되는 상대 비중입니다.
        // 기본값은 모두 1이므로 기존처럼 같은 등급 내부에서 동일 확률입니다.
        private static readonly Dictionary<ushort, ConfigEntry<float>>
            ItemWeightConfigs =
                new Dictionary<ushort, ConfigEntry<float>>();

        private static readonly ushort[] CommonResourceIds =
        {
            28,   // Stick / FireWood
            72,   // Stone
            69    // Conch
        };

        private static readonly ushort[] NormalResourceIds =
        {
            14,   // Binoculars
            13,   // Bing Bong
            15,   // Bugle
            99    // Frisbee
        };

        private static readonly ushort[] RareResourceIds =
        {
            34,   // Guidebook
            49    // Scroll
        };

        private static readonly ushort[] UniqueResourceIds =
        {
            51    // Weird Shroom
        };

        private static readonly ushort[] LegendaryResourceIds =
        {
            112   // Strange Gem
        };

        private static readonly ushort[] SaleResourceIds =
        {
            28,   // Common: Stick / FireWood
            72,   // Common: Stone
            69,   // Common: Conch
            14,   // Normal: Binoculars
            13,   // Normal: Bing Bong
            15,   // Normal: Bugle
            99,   // Normal: Frisbee
            34,   // Rare: Guidebook
            49,   // Rare: Scroll
            51,   // Unique: Weird Shroom
            112   // Legendary: Strange Gem
        };

        private static readonly HashSet<ushort> SaleResourceIdSet =
            new HashSet<ushort>(
                SaleResourceIds);

        public static ResourceUpgradeGrade CurrentUpgradeGrade
        {
            get;
            private set;
        } = ResourceUpgradeGrade.Common;

        private readonly List<CandidatePoint> candidates =
            new List<CandidatePoint>();

        private readonly List<SpawnSlot> slots =
            new List<SpawnSlot>();

        private readonly HashSet<CandidateKey> candidateKeys =
            new HashSet<CandidateKey>();

        private Coroutine sceneRoutine;
        private int loadedSceneHandle = -1;
        private bool initializationComplete;
        private bool expandedCandidatesAdded;

        internal static Spawn Instance
        {
            get;
            private set;
        }

        internal static ManualLogSource ModLogger
        {
            get;
            private set;
        }

        private sealed class CandidatePoint
        {
            public int SegmentIndex;
            public Vector3 Position;
            public Quaternion Rotation;
            public string SourceName;
            public string SourcePath;
        }

        private sealed class SpawnSlot
        {
            public int SlotIndex;
            public int SegmentIndex;

            public Vector3 Position;
            public Quaternion Rotation;

            public ushort LastSpawnedItemId;
            public ResourceRarity LastSpawnedRarity;

            public Item CurrentItem;
            public int CurrentViewId;

            public float RespawnAt = -1f;
            public float InactiveSince = -1f;

            public Action<ItemState> StateChangedHandler;
        }

        private struct CandidateKey :
            IEquatable<CandidateKey>
        {
            private int x;
            private int y;
            private int z;

            public static CandidateKey FromPosition(
                Vector3 position)
            {
                return new CandidateKey
                {
                    x =
                        Mathf.RoundToInt(
                            position.x /
                            CandidateDeduplicationCellSize),

                    y =
                        Mathf.RoundToInt(
                            position.y /
                            CandidateDeduplicationCellSize),

                    z =
                        Mathf.RoundToInt(
                            position.z /
                            CandidateDeduplicationCellSize)
                };
            }

            public bool Equals(
                CandidateKey other)
            {
                return x == other.x &&
                       y == other.y &&
                       z == other.z;
            }

            public override bool Equals(
                object obj)
            {
                return obj is CandidateKey &&
                       Equals(
                           (CandidateKey)obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = 17;

                    hash =
                        hash * 31 +
                        x;

                    hash =
                        hash * 31 +
                        y;

                    hash =
                        hash * 31 +
                        z;

                    return hash;
                }
            }
        }

        private void Awake()
        {
            Instance = this;
            ModLogger = Logger;

            BindSpawnProbabilityConfig();

            SceneManager.sceneLoaded += HandleSceneLoaded;

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
                " loaded. " +
                "FixedSlots=" +
                TargetSlotCount +
                " | Per-slot respawn=" +
                RespawnDelaySeconds +
                " seconds.");

            LogCurrentUpgradeProbabilities();
        }

        private void BindSpawnProbabilityConfig()
        {
            ItemWeightConfigs.Clear();

            BindUpgradeGradeWeights(
                ResourceUpgradeGrade.Common);

            BindUpgradeGradeWeights(
                ResourceUpgradeGrade.Normal);

            BindUpgradeGradeWeights(
                ResourceUpgradeGrade.Rare);

            BindUpgradeGradeWeights(
                ResourceUpgradeGrade.Unique);

            BindUpgradeGradeWeights(
                ResourceUpgradeGrade.Legendary);

            BindItemWeight(
                28,
                "Common - 나뭇가지",
                "Common 등급 안에서 나뭇가지가 선택되는 상대 비중입니다.");

            BindItemWeight(
                72,
                "Common - 돌",
                "Common 등급 안에서 돌이 선택되는 상대 비중입니다.");

            BindItemWeight(
                69,
                "Common - 소라고동",
                "Common 등급 안에서 소라고동이 선택되는 상대 비중입니다.");

            BindItemWeight(
                14,
                "Normal - 망원경",
                "Normal 등급 안에서 망원경이 선택되는 상대 비중입니다.");

            BindItemWeight(
                13,
                "Normal - 빙봉",
                "Normal 등급 안에서 빙봉이 선택되는 상대 비중입니다.");

            BindItemWeight(
                15,
                "Normal - 나팔",
                "Normal 등급 안에서 나팔이 선택되는 상대 비중입니다.");

            BindItemWeight(
                99,
                "Normal - 플라잉 디스크",
                "Normal 등급 안에서 플라잉 디스크가 선택되는 상대 비중입니다.");

            BindItemWeight(
                34,
                "Rare - 가이드북",
                "Rare 등급 안에서 가이드북이 선택되는 상대 비중입니다.");

            BindItemWeight(
                49,
                "Rare - 스크롤",
                "Rare 등급 안에서 스크롤이 선택되는 상대 비중입니다.");

            BindItemWeight(
                51,
                "Unique - 괴상 버섯",
                "Unique 등급 안에서 괴상 버섯이 선택되는 상대 비중입니다.");

            BindItemWeight(
                112,
                "Legendary - 이상한 보석",
                "Legendary 등급 안에서 이상한 보석이 선택되는 상대 비중입니다.");

            Logger.LogInfo(
                "Spawn probability ConfigEntry binding complete. " +
                "Rarity entries=" +
                CountBoundRarityEntries() +
                " | Item entries=" +
                ItemWeightConfigs.Count +
                ". PEAKLib.ModConfig can display these entries when installed.");
        }

        private void BindUpgradeGradeWeights(
            ResourceUpgradeGrade upgradeGrade)
        {
            int gradeIndex =
                (int)upgradeGrade;

            string section =
                GetUpgradeConfigSection(
                    upgradeGrade);

            // 현재 업그레이드 단계까지 열린 등급만 설정 항목으로 만듭니다.
            for (int rarityIndex = 0;
                 rarityIndex <= gradeIndex;
                 rarityIndex++)
            {
                ResourceRarity rarity =
                    (ResourceRarity)rarityIndex;

                float defaultValue =
                    DefaultRarityWeights[
                        gradeIndex,
                        rarityIndex];

                string key =
                    GetRarityDisplayName(
                        rarity) +
                    " 등급 비중";

                string description =
                    upgradeGrade +
                    " 업그레이드 단계에서 " +
                    GetRarityDisplayName(
                        rarity) +
                    " 등급이 선택되는 상대 비중입니다. " +
                    "같은 단계의 활성 비중은 자동 정규화되므로 합계를 100으로 맞출 필요가 없습니다. " +
                    "0으로 설정하면 해당 등급은 생성되지 않습니다.";

                ConfigEntry<float> entry =
                    Config.Bind(
                        section,
                        key,
                        defaultValue,
                        new ConfigDescription(
                            description,
                            new AcceptableValueRange<float>(
                                0f,
                                100f)));

                entry.SettingChanged +=
                    HandleProbabilityConfigChanged;

                RarityWeightConfigs[
                    gradeIndex,
                    rarityIndex] =
                        entry;
            }
        }

        private void BindItemWeight(
            ushort itemId,
            string key,
            string description)
        {
            ConfigEntry<float> entry =
                Config.Bind(
                    "06. 등급 내부 아이템 비중",
                    key,
                    1f,
                    new ConfigDescription(
                        description +
                        " 같은 등급 안의 값들은 상대 비중으로 계산됩니다. " +
                        "0으로 설정하면 해당 아이템은 추첨되지 않습니다.",
                        new AcceptableValueRange<float>(
                            0f,
                            100f)));

            entry.SettingChanged +=
                HandleProbabilityConfigChanged;

            ItemWeightConfigs[itemId] =
                entry;
        }

        private void HandleProbabilityConfigChanged(
            object sender,
            EventArgs eventArgs)
        {
            if (ModLogger != null)
            {
                ModLogger.LogInfo(
                    "Spawn probability config changed. " +
                    "The host applies the new values to future initial spawns " +
                    "and future slot respawns. Existing ground items are unchanged.");
            }

            LogCurrentUpgradeProbabilities();
            LogCurrentItemWeights();
        }

        private static string GetUpgradeConfigSection(
            ResourceUpgradeGrade grade)
        {
            switch (grade)
            {
                case ResourceUpgradeGrade.Common:
                    return "01. Common 업그레이드 확률";

                case ResourceUpgradeGrade.Normal:
                    return "02. Normal 업그레이드 확률";

                case ResourceUpgradeGrade.Rare:
                    return "03. Rare 업그레이드 확률";

                case ResourceUpgradeGrade.Unique:
                    return "04. Unique 업그레이드 확률";

                case ResourceUpgradeGrade.Legendary:
                    return "05. Legendary 업그레이드 확률";

                default:
                    return "Spawn 확률";
            }
        }

        private static string GetRarityDisplayName(
            ResourceRarity rarity)
        {
            switch (rarity)
            {
                case ResourceRarity.Common:
                    return "Common";

                case ResourceRarity.Normal:
                    return "Normal";

                case ResourceRarity.Rare:
                    return "Rare";

                case ResourceRarity.Unique:
                    return "Unique";

                case ResourceRarity.Legendary:
                    return "Legendary";

                default:
                    return "Unknown";
            }
        }

        private static int CountBoundRarityEntries()
        {
            int count = 0;

            for (int gradeIndex = 0;
                 gradeIndex <
                     RarityWeightConfigs.GetLength(
                         0);
                 gradeIndex++)
            {
                for (int rarityIndex = 0;
                     rarityIndex <
                         RarityWeightConfigs.GetLength(
                             1);
                     rarityIndex++)
                {
                    if (RarityWeightConfigs[
                            gradeIndex,
                            rarityIndex] !=
                        null)
                    {
                        count++;
                    }
                }
            }

            return count;
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= HandleSceneLoaded;

            StopSceneRoutine();
            ClearSlots();

            candidates.Clear();
            candidateKeys.Clear();

            for (int gradeIndex = 0;
                 gradeIndex <
                     RarityWeightConfigs.GetLength(
                         0);
                 gradeIndex++)
            {
                for (int rarityIndex = 0;
                     rarityIndex <
                         RarityWeightConfigs.GetLength(
                             1);
                     rarityIndex++)
                {
                    ConfigEntry<float> entry =
                        RarityWeightConfigs[
                            gradeIndex,
                            rarityIndex];

                    if (entry != null)
                    {
                        entry.SettingChanged -=
                            HandleProbabilityConfigChanged;
                    }

                    RarityWeightConfigs[
                        gradeIndex,
                        rarityIndex] =
                            null;
                }
            }

            foreach (
                KeyValuePair<ushort, ConfigEntry<float>> pair
                in ItemWeightConfigs)
            {
                if (pair.Value != null)
                {
                    pair.Value.SettingChanged -=
                        HandleProbabilityConfigChanged;
                }
            }

            ItemWeightConfigs.Clear();

            if (Instance == this)
            {
                Instance = null;
            }
        }

        internal static bool IsSaleResourceId(
            ushort itemId)
        {
            return SaleResourceIdSet.Contains(
                itemId);
        }

        /// <summary>
        /// 상점 또는 업그레이드 코드가 호출하는 자원 등급 변경 API입니다.
        /// 호스트만 변경할 수 있습니다. 이미 지상에 생성된 아이템은 유지되고,
        /// 이후 생성되거나 5분 뒤 재생성되는 슬롯부터 새 확률표가 적용됩니다.
        /// </summary>
        public static bool SetUpgradeGrade(
            ResourceUpgradeGrade grade)
        {
            int numericGrade =
                (int)grade;

            if (numericGrade <
                    (int)ResourceUpgradeGrade.Common ||
                numericGrade >
                    (int)ResourceUpgradeGrade.Legendary)
            {
                return false;
            }

            if (PhotonNetwork.InRoom &&
                !PhotonNetwork.IsMasterClient)
            {
                if (ModLogger != null)
                {
                    ModLogger.LogWarning(
                        "Only the master client can change resource upgrade grade.");
                }

                return false;
            }

            if (CurrentUpgradeGrade ==
                grade)
            {
                return true;
            }

            ResourceUpgradeGrade previousGrade =
                CurrentUpgradeGrade;

            CurrentUpgradeGrade =
                grade;

            if (ModLogger != null)
            {
                ModLogger.LogInfo(
                    "Resource upgrade grade changed. " +
                    "Previous=" +
                    previousGrade +
                    " | Current=" +
                    CurrentUpgradeGrade +
                    ". Existing items remain; future spawns use the new table.");
            }

            LogCurrentUpgradeProbabilities();
            return true;
        }

        public static bool SetUpgradeGrade(
            int grade)
        {
            if (grade <
                    (int)ResourceUpgradeGrade.Common ||
                grade >
                    (int)ResourceUpgradeGrade.Legendary)
            {
                return false;
            }

            return SetUpgradeGrade(
                (ResourceUpgradeGrade)grade);
        }

        public static bool AdvanceUpgradeGrade()
        {
            int currentGrade =
                (int)CurrentUpgradeGrade;

            if (currentGrade >=
                (int)ResourceUpgradeGrade.Legendary)
            {
                return false;
            }

            return SetUpgradeGrade(
                currentGrade + 1);
        }

        private void HandleSceneLoaded(
            Scene scene,
            LoadSceneMode loadSceneMode)
        {
            StopSceneRoutine();
            ClearSlots();

            candidates.Clear();
            candidateKeys.Clear();

            initializationComplete = false;
            expandedCandidatesAdded = false;
            loadedSceneHandle = scene.handle;

            CurrentUpgradeGrade =
                ResourceUpgradeGrade.Common;

            if (IsExcludedScene(
                    scene))
            {
                Logger.LogInfo(
                    "Map-wide resource slots disabled in scene: " +
                    scene.name);

                return;
            }

            // SceneManager.sceneLoaded 시점은 MapHandler.Start가 후반 Segment를
            // 비활성화하기 전이므로 GroundPlaceSpawner Raycast 성공률이 가장 높습니다.
            TryCaptureCandidates(
                scene);

            sceneRoutine =
                StartCoroutine(
                    SceneLifecycleRoutine(
                        scene.handle));
        }

        private void StopSceneRoutine()
        {
            if (sceneRoutine != null)
            {
                StopCoroutine(
                    sceneRoutine);

                sceneRoutine = null;
            }

            StopAllCoroutines();
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

        private bool IsCurrentScene(
            int sceneHandle)
        {
            Scene activeScene =
                SceneManager.GetActiveScene();

            return activeScene.IsValid() &&
                   activeScene.isLoaded &&
                   activeScene.handle == sceneHandle &&
                   loadedSceneHandle == sceneHandle &&
                   !IsExcludedScene(
                       activeScene);
        }

        private IEnumerator SceneLifecycleRoutine(
            int sceneHandle)
        {
            float timeout =
                InitializationTimeoutSeconds;

            MapHandler mapHandler = null;

            while (timeout > 0f)
            {
                if (!IsCurrentScene(
                        sceneHandle))
                {
                    yield break;
                }

                mapHandler =
                    UnityEngine.Object
                        .FindAnyObjectByType<MapHandler>();

                if (mapHandler != null)
                {
                    if (candidates.Count <
                        TargetSlotCount)
                    {
                        TryCaptureCandidates(
                            SceneManager.GetActiveScene());
                    }

                    if (candidates.Count >=
                        TargetSlotCount)
                    {
                        break;
                    }
                }

                timeout -=
                    Time.unscaledDeltaTime;

                yield return null;
            }

            if (!IsCurrentScene(
                    sceneHandle))
            {
                yield break;
            }

            if (mapHandler == null)
            {
                Logger.LogError(
                    "Map-wide slot initialization failed: " +
                    "MapHandler was not found.");

                yield break;
            }

            if (candidates.Count <
                TargetSlotCount)
            {
                Logger.LogError(
                    "Map-wide slot initialization failed: " +
                    "not enough valid Spawner candidates. " +
                    "Candidates=" +
                    candidates.Count +
                    " | Required=" +
                    TargetSlotCount);

                yield break;
            }

            BuildFixedSlots(
                mapHandler);

            if (slots.Count <
                TargetSlotCount)
            {
                Logger.LogError(
                    "Map-wide slot initialization failed: " +
                    "slot selection returned too few points. " +
                    "Slots=" +
                    slots.Count +
                    " | Required=" +
                    TargetSlotCount);

                yield break;
            }

            while (timeout > 0f)
            {
                if (!IsCurrentScene(
                        sceneHandle))
                {
                    yield break;
                }

                bool databaseReady =
                    SingletonAsset<ItemDatabase>.Instance != null &&
                    SingletonAsset<ItemDatabase>.Instance.itemLookup != null;

                if (PhotonNetwork.InRoom &&
                    databaseReady &&
                    Character.localCharacter != null)
                {
                    break;
                }

                timeout -=
                    Time.unscaledDeltaTime;

                yield return null;
            }

            if (!PhotonNetwork.InRoom)
            {
                Logger.LogError(
                    "Map-wide slot initialization failed: " +
                    "Photon room was not ready.");

                yield break;
            }

            ValidateResourcePrefabs();

            if (!PhotonNetwork.IsMasterClient)
            {
                Logger.LogInfo(
                    "This client is not the master client. " +
                    "The host owns fixed-slot spawning.");

                yield break;
            }

            yield return
                new WaitForSecondsRealtime(
                    StartupDelaySeconds);

            if (!IsCurrentScene(
                    sceneHandle))
            {
                yield break;
            }

            yield return
                StartCoroutine(
                    SpawnAllInitialSlots(
                        sceneHandle));

            initializationComplete = true;

            Logger.LogInfo(
                "Map-wide fixed slots are active. " +
                "Slots=" +
                slots.Count +
                " | RespawnDelay=" +
                RespawnDelaySeconds +
                " seconds.");

            while (IsCurrentScene(
                       sceneHandle))
            {
                if (PhotonNetwork.InRoom &&
                    PhotonNetwork.IsMasterClient)
                {
                    MonitorSlots();
                }

                yield return
                    new WaitForSecondsRealtime(
                        SlotMonitorIntervalSeconds);
            }
        }

        private void TryCaptureCandidates(
            Scene scene)
        {
            if (!scene.IsValid() ||
                !scene.isLoaded)
            {
                return;
            }

            MapHandler mapHandler =
                UnityEngine.Object
                    .FindAnyObjectByType<MapHandler>();

            if (mapHandler == null)
            {
                return;
            }

            GameObject[] roots =
                scene.GetRootGameObjects();

            int spawnerCount = 0;
            int beforeCount =
                candidates.Count;

            for (int rootIndex = 0;
                 rootIndex < roots.Length;
                 rootIndex++)
            {
                GameObject root =
                    roots[rootIndex];

                if (root == null)
                {
                    continue;
                }

                Spawner[] sceneSpawners =
                    root.GetComponentsInChildren<Spawner>(
                        true);

                for (int spawnerIndex = 0;
                     spawnerIndex < sceneSpawners.Length;
                     spawnerIndex++)
                {
                    Spawner spawner =
                        sceneSpawners[spawnerIndex];

                    if (!IsUsableSpawner(
                            spawner))
                    {
                        continue;
                    }

                    spawnerCount++;

                    int segmentIndex =
                        ResolveSegmentIndex(
                            mapHandler,
                            spawner.transform);

                    if (segmentIndex < 0)
                    {
                        continue;
                    }

                    int pointCountBefore =
                        candidates.Count;

                    AddSpawnSpotList(
                        spawner,
                        spawner.spawnSpots,
                        segmentIndex,
                        "SingleList");

                    if (spawner.weightedSpawnSpots != null)
                    {
                        for (int weightedIndex = 0;
                             weightedIndex <
                                 spawner.weightedSpawnSpots.Count;
                             weightedIndex++)
                        {
                            Spawner.WeightedSpawnPointEntry entry =
                                spawner.weightedSpawnSpots[
                                    weightedIndex];

                            if (entry == null)
                            {
                                continue;
                            }

                            AddSpawnSpotList(
                                spawner,
                                entry.spawnSpots,
                                segmentIndex,
                                "WeightedList[" +
                                weightedIndex +
                                "]");
                        }
                    }

                    bool addedConfiguredSpot =
                        candidates.Count >
                        pointCountBefore;

                    if (!addedConfiguredSpot ||
                        spawner.spawnTransformIsSpawnerTransform)
                    {
                        AddCandidateFromTransform(
                            spawner,
                            spawner.transform,
                            segmentIndex,
                            "SpawnerTransform");
                    }
                }
            }

            if (!expandedCandidatesAdded &&
                candidates.Count > 0)
            {
                AddExpandedGroundCandidates(
                    mapHandler);

                expandedCandidatesAdded = true;
            }

            int addedCount =
                candidates.Count -
                beforeCount;

            if (addedCount > 0)
            {
                Logger.LogInfo(
                    "Captured map-wide Spawner candidates. " +
                    "Scene=" +
                    scene.name +
                    " | ScannedSpawners=" +
                    spawnerCount +
                    " | Added=" +
                    addedCount +
                    " | TotalUnique=" +
                    candidates.Count);

                LogCandidateCountsBySegment(
                    mapHandler);
            }
        }

        private static bool IsUsableSpawner(
            Spawner spawner)
        {
            if (spawner == null ||
                spawner.gameObject == null)
            {
                return false;
            }

            if (spawner is BerryBush ||
                spawner is BerryVine)
            {
                return false;
            }

            if (spawner is RespawnChest)
            {
                return false;
            }

            string objectName =
                spawner.gameObject.name;

            if (!string.IsNullOrEmpty(
                    objectName) &&
                objectName.IndexOf(
                    "scout statue",
                    StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }

            return true;
        }

        private void AddSpawnSpotList(
            Spawner spawner,
            List<Transform> spawnSpots,
            int segmentIndex,
            string listName)
        {
            if (spawnSpots == null)
            {
                return;
            }

            for (int i = 0;
                 i < spawnSpots.Count;
                 i++)
            {
                Transform spawnSpot =
                    spawnSpots[i];

                if (spawnSpot == null)
                {
                    continue;
                }

                AddCandidateFromTransform(
                    spawner,
                    spawnSpot,
                    segmentIndex,
                    listName +
                    "[" +
                    i +
                    "]");
            }
        }

        private void AddCandidateFromTransform(
            Spawner spawner,
            Transform sourceTransform,
            int segmentIndex,
            string sourceLabel)
        {
            if (spawner == null ||
                sourceTransform == null)
            {
                return;
            }

            Vector3 position;
            Quaternion rotation;

            if (!TryResolveCandidatePlacement(
                    spawner,
                    sourceTransform,
                    out position,
                    out rotation))
            {
                return;
            }

            CandidateKey key =
                CandidateKey.FromPosition(
                    position);

            if (!candidateKeys.Add(
                    key))
            {
                return;
            }

            candidates.Add(
                new CandidatePoint
                {
                    SegmentIndex =
                        segmentIndex,

                    Position =
                        position,

                    Rotation =
                        rotation,

                    SourceName =
                        spawner.gameObject.name +
                        ":" +
                        sourceLabel,

                    SourcePath =
                        GetHierarchyPath(
                            sourceTransform)
                });
        }

        private static bool TryResolveCandidatePlacement(
            Spawner spawner,
            Transform sourceTransform,
            out Vector3 position,
            out Quaternion rotation)
        {
            position =
                sourceTransform.position +
                Vector3.up *
                SpawnHeightOffset;

            rotation =
                Quaternion.Euler(
                    0f,
                    sourceTransform.rotation.eulerAngles.y,
                    0f);

            // GroundPlaceSpawner는 원본 PEAK와 동일하게
            // spawnSpot에서 -spawner.up 방향으로 Terrain/Map을 찾습니다.
            if (spawner is GroundPlaceSpawner)
            {
                RaycastHit groundHit;

                bool hit =
                    Physics.Raycast(
                        sourceTransform.position,
                        -spawner.transform.up,
                        out groundHit,
                        GroundRayDistance,
                        HelperFunctions.GetMask(
                            HelperFunctions.LayerType.TerrainMap),
                        QueryTriggerInteraction.Ignore);

                if (!hit ||
                    groundHit.collider == null ||
                    groundHit.normal.y <
                        MinimumGroundNormalY)
                {
                    return false;
                }

                position =
                    groundHit.point +
                    groundHit.normal *
                    SpawnHeightOffset;

                rotation =
                    CreateGroundAlignedRotation(
                        groundHit.normal,
                        sourceTransform.rotation.eulerAngles.y);

                return true;
            }

            // 일반 Spawner의 실제 spawnSpot은 원본이 그대로 사용하는 좌표입니다.
            // 가능한 경우 짧은 바닥 Raycast로 높이만 보정합니다.
            RaycastHit shortGroundHit;

            Vector3 rayStart =
                sourceTransform.position +
                Vector3.up *
                GroundRayStartOffset;

            bool shortHit =
                Physics.Raycast(
                    rayStart,
                    Vector3.down,
                    out shortGroundHit,
                    GroundRayStartOffset +
                    8f,
                    HelperFunctions.GetMask(
                        HelperFunctions.LayerType.TerrainMap),
                    QueryTriggerInteraction.Ignore);

            if (shortHit &&
                shortGroundHit.collider != null &&
                shortGroundHit.normal.y >=
                    MinimumGroundNormalY)
            {
                position =
                    shortGroundHit.point +
                    shortGroundHit.normal *
                    SpawnHeightOffset;

                rotation =
                    CreateGroundAlignedRotation(
                        shortGroundHit.normal,
                        sourceTransform.rotation.eulerAngles.y);
            }

            return true;
        }

        private static Quaternion CreateGroundAlignedRotation(
            Vector3 groundNormal,
            float yawDegrees)
        {
            Vector3 normal =
                groundNormal.sqrMagnitude >
                    0.0001f
                    ? groundNormal.normalized
                    : Vector3.up;

            Quaternion align =
                Quaternion.FromToRotation(
                    Vector3.up,
                    normal);

            Quaternion yaw =
                Quaternion.AngleAxis(
                    yawDegrees,
                    normal);

            return yaw *
                   align;
        }

        private static int ResolveSegmentIndex(
            MapHandler mapHandler,
            Transform target)
        {
            if (mapHandler == null ||
                target == null ||
                mapHandler.segments == null)
            {
                return -1;
            }

            for (int i = 0;
                 i < mapHandler.segments.Length;
                 i++)
            {
                GameObject parent =
                    mapHandler.segments[i]
                        .segmentParent;

                if (parent != null &&
                    IsInside(
                        target,
                        parent.transform))
                {
                    return i;
                }

                GameObject campfire =
                    mapHandler.segments[i]
                        .segmentCampfire;

                if (campfire != null &&
                    IsInside(
                        target,
                        campfire.transform))
                {
                    return i;
                }
            }

            return -1;
        }

        private static bool IsInside(
            Transform target,
            Transform root)
        {
            return target != null &&
                   root != null &&
                   (
                       target == root ||
                       target.IsChildOf(
                           root)
                   );
        }

        /// <summary>
        /// 기존 Spawner 좌표를 중심으로 8방향의 실제 지면 좌표를 추가합니다.
        /// 각 방향에서 30m부터 6m까지 후퇴하며 최초의 유효 지면을 사용합니다.
        /// 기준점 자체를 복제하지 않고, TerrainMap Raycast에 성공한 좌표만 등록합니다.
        /// </summary>
        private void AddExpandedGroundCandidates(
            MapHandler mapHandler)
        {
            int originalCount =
                candidates.Count;

            int addedCount = 0;

            for (int candidateIndex = 0;
                 candidateIndex < originalCount;
                 candidateIndex++)
            {
                CandidatePoint source =
                    candidates[candidateIndex];

                float phaseDegrees =
                    (
                        candidateIndex * 47 +
                        source.SegmentIndex * 29
                    ) %
                    360;

                for (int directionIndex = 0;
                     directionIndex < ExpansionDirectionCount;
                     directionIndex++)
                {
                    float angleDegrees =
                        phaseDegrees +
                        directionIndex *
                        (
                            360f /
                            ExpansionDirectionCount
                        );

                    float angleRadians =
                        angleDegrees *
                        Mathf.Deg2Rad;

                    bool addedForDirection = false;

                    for (int radiusPassIndex = 0;
                         radiusPassIndex <
                             ExpansionRadiusPasses.Length;
                         radiusPassIndex++)
                    {
                        float expansionRadius =
                            ExpansionRadiusPasses[
                                radiusPassIndex];

                        Vector3 horizontalOffset =
                            new Vector3(
                                Mathf.Cos(
                                    angleRadians) *
                                expansionRadius,
                                0f,
                                Mathf.Sin(
                                    angleRadians) *
                                expansionRadius);

                        Vector3 rayOrigin =
                            source.Position +
                            horizontalOffset +
                            Vector3.up *
                            ExpansionRayHeight;

                        RaycastHit hit;

                        bool foundGround =
                            Physics.Raycast(
                                rayOrigin,
                                Vector3.down,
                                out hit,
                                ExpansionRayDistance,
                                HelperFunctions.GetMask(
                                    HelperFunctions.LayerType.TerrainMap),
                                QueryTriggerInteraction.Ignore);

                        if (!foundGround ||
                            hit.collider == null ||
                            hit.normal.y <
                                MinimumGroundNormalY)
                        {
                            continue;
                        }

                        int hitSegmentIndex =
                            ResolveSegmentIndex(
                                mapHandler,
                                hit.collider.transform);

                        if (hitSegmentIndex >= 0 &&
                            hitSegmentIndex !=
                                source.SegmentIndex)
                        {
                            continue;
                        }

                        Vector3 position =
                            hit.point +
                            hit.normal *
                            SpawnHeightOffset;

                        CandidateKey key =
                            CandidateKey.FromPosition(
                                position);

                        // 먼 반경 좌표가 기존 후보와 중복되면,
                        // 해당 방향 자체를 포기하지 않고 더 가까운 반경을 계속 검사합니다.
                        if (!candidateKeys.Add(
                                key))
                        {
                            continue;
                        }

                        candidates.Add(
                            new CandidatePoint
                            {
                                SegmentIndex =
                                    source.SegmentIndex,

                                Position =
                                    position,

                                Rotation =
                                    CreateGroundAlignedRotation(
                                        hit.normal,
                                        angleDegrees),

                                SourceName =
                                    source.SourceName +
                                    ":Expanded[" +
                                    directionIndex +
                                    "]@" +
                                    expansionRadius +
                                    "m",

                                SourcePath =
                                    source.SourcePath
                            });

                        addedCount++;
                        addedForDirection = true;
                        break;
                    }

                    if (!addedForDirection)
                    {
                        // 이 방향은 30m부터 6m까지 모두 유효 지면을 찾지 못한 경우입니다.
                        // 기준 후보 자체는 이미 candidates 목록에 남아 있으므로
                        // 초기 배치 전체를 실패시키지는 않습니다.
                    }
                }
            }

            Logger.LogInfo(
                "Expanded ground candidates created. " +
                "BaseCandidates=" +
                originalCount +
                " | Added=" +
                addedCount +
                " | Total=" +
                candidates.Count +
                " | RadiusRange=" +
                ExpansionMinimumRadius +
                "-" +
                ExpansionMaximumRadius +
                "m.");
        }

        private static long GetCoverageCellKey(
            Vector3 position)
        {
            int cellX =
                Mathf.FloorToInt(
                    position.x /
                    CoverageCellSize);

            int cellZ =
                Mathf.FloorToInt(
                    position.z /
                    CoverageCellSize);

            return
                (
                    (long)cellX <<
                    32
                ) ^
                (uint)cellZ;
        }

        /// <summary>
        /// 각 Segment가 실제로 차지하는 20m 공간 셀 수를 기준으로 슬롯을 배분합니다.
        /// 작은 구간에 과도한 수량이 몰리는 현상을 방지합니다.
        /// </summary>
        private static Dictionary<int, int>
            BuildSegmentQuotas(
                Dictionary<int, List<CandidatePoint>>
                    candidatesBySegment,
                List<int> segmentIndexes)
        {
            Dictionary<int, int> quotas =
                new Dictionary<int, int>();

            Dictionary<int, int> coverageWeights =
                new Dictionary<int, int>();

            int segmentCount =
                segmentIndexes.Count;

            int guaranteedPerSegment =
                Mathf.Min(
                    MinimumSlotsPerSegment,
                    TargetSlotCount /
                    Mathf.Max(
                        1,
                        segmentCount));

            int guaranteedTotal =
                guaranteedPerSegment *
                segmentCount;

            int remainingSlots =
                Mathf.Max(
                    0,
                    TargetSlotCount -
                    guaranteedTotal);

            int totalWeight = 0;

            for (int i = 0;
                 i < segmentIndexes.Count;
                 i++)
            {
                int segmentIndex =
                    segmentIndexes[i];

                HashSet<long> coverageCells =
                    new HashSet<long>();

                List<CandidatePoint> segmentCandidates =
                    candidatesBySegment[
                        segmentIndex];

                for (int candidateIndex = 0;
                     candidateIndex <
                         segmentCandidates.Count;
                     candidateIndex++)
                {
                    coverageCells.Add(
                        GetCoverageCellKey(
                            segmentCandidates[
                                candidateIndex].Position));
                }

                int weight =
                    Mathf.Max(
                        1,
                        coverageCells.Count);

                coverageWeights[
                    segmentIndex] =
                        weight;

                totalWeight +=
                    weight;

                quotas[
                    segmentIndex] =
                        guaranteedPerSegment;
            }

            List<QuotaRemainder> remainders =
                new List<QuotaRemainder>();

            int proportionallyAssigned = 0;

            for (int i = 0;
                 i < segmentIndexes.Count;
                 i++)
            {
                int segmentIndex =
                    segmentIndexes[i];

                float exactShare =
                    totalWeight > 0
                        ? (
                            remainingSlots *
                            (
                                (float)
                                coverageWeights[
                                    segmentIndex] /
                                totalWeight
                            )
                        )
                        : 0f;

                int wholeShare =
                    Mathf.FloorToInt(
                        exactShare);

                quotas[
                    segmentIndex] +=
                        wholeShare;

                proportionallyAssigned +=
                    wholeShare;

                remainders.Add(
                    new QuotaRemainder
                    {
                        SegmentIndex =
                            segmentIndex,

                        Fraction =
                            exactShare -
                            wholeShare
                    });
            }

            int leftover =
                remainingSlots -
                proportionallyAssigned;

            remainders.Sort(
                delegate (
                    QuotaRemainder left,
                    QuotaRemainder right)
                {
                    int fractionComparison =
                        right.Fraction.CompareTo(
                            left.Fraction);

                    if (fractionComparison != 0)
                    {
                        return fractionComparison;
                    }

                    return left.SegmentIndex.CompareTo(
                        right.SegmentIndex);
                });

            for (int i = 0;
                 i < leftover;
                 i++)
            {
                QuotaRemainder remainder =
                    remainders[
                        i %
                        remainders.Count];

                quotas[
                    remainder.SegmentIndex]++;
            }

            return quotas;
        }

        private struct QuotaRemainder
        {
            public int SegmentIndex;
            public float Fraction;
        }

        private void BuildFixedSlots(
            MapHandler mapHandler)
        {
            ClearSlots();

            Dictionary<int, List<CandidatePoint>>
                candidatesBySegment =
                    new Dictionary<int, List<CandidatePoint>>();

            for (int i = 0;
                 i < candidates.Count;
                 i++)
            {
                CandidatePoint candidate =
                    candidates[i];

                List<CandidatePoint> segmentCandidates;

                if (!candidatesBySegment.TryGetValue(
                        candidate.SegmentIndex,
                        out segmentCandidates))
                {
                    segmentCandidates =
                        new List<CandidatePoint>();

                    candidatesBySegment.Add(
                        candidate.SegmentIndex,
                        segmentCandidates);
                }

                segmentCandidates.Add(
                    candidate);
            }

            List<int> segmentIndexes =
                new List<int>(
                    candidatesBySegment.Keys);

            segmentIndexes.Sort();

            if (segmentIndexes.Count == 0)
            {
                return;
            }

            Dictionary<int, int> segmentQuotas =
                BuildSegmentQuotas(
                    candidatesBySegment,
                    segmentIndexes);

            List<CandidatePoint> selected =
                new List<CandidatePoint>(
                    TargetSlotCount);

            HashSet<CandidatePoint> selectedSet =
                new HashSet<CandidatePoint>();

            for (int segmentOrder = 0;
                 segmentOrder <
                     segmentIndexes.Count;
                 segmentOrder++)
            {
                int segmentIndex =
                    segmentIndexes[
                        segmentOrder];

                int quota =
                    segmentQuotas[
                        segmentIndex];

                List<CandidatePoint> segmentSelection =
                    SelectFarthestPoints(
                        candidatesBySegment[
                            segmentIndex],
                        quota,
                        null);

                for (int i = 0;
                     i <
                         segmentSelection.Count;
                     i++)
                {
                    CandidatePoint point =
                        segmentSelection[i];

                    if (selectedSet.Add(
                            point))
                    {
                        selected.Add(
                            point);
                    }
                }

                Logger.LogInfo(
                    "Balanced segment slot selection. " +
                    "Segment=" +
                    segmentIndex +
                    " | CandidateCount=" +
                    candidatesBySegment[
                        segmentIndex].Count +
                    " | SpatialQuota=" +
                    quota +
                    " | Selected=" +
                    segmentSelection.Count);
            }

            if (selected.Count <
                TargetSlotCount)
            {
                List<CandidatePoint> remaining =
                    new List<CandidatePoint>();

                for (int i = 0;
                     i < candidates.Count;
                     i++)
                {
                    if (!selectedSet.Contains(
                            candidates[i]))
                    {
                        remaining.Add(
                            candidates[i]);
                    }
                }

                int missing =
                    TargetSlotCount -
                    selected.Count;

                List<CandidatePoint> globalFill =
                    SelectFarthestPoints(
                        remaining,
                        missing,
                        selected);

                for (int i = 0;
                     i < globalFill.Count;
                     i++)
                {
                    CandidatePoint point =
                        globalFill[i];

                    if (selectedSet.Add(
                            point))
                    {
                        selected.Add(
                            point);
                    }
                }
            }

            if (selected.Count >
                TargetSlotCount)
            {
                selected.RemoveRange(
                    TargetSlotCount,
                    selected.Count -
                    TargetSlotCount);
            }

            selected.Sort(
                CompareSelectedPoints);

            for (int i = 0;
                 i < selected.Count;
                 i++)
            {
                CandidatePoint point =
                    selected[i];

                SpawnSlot slot =
                    new SpawnSlot
                    {
                        SlotIndex =
                            slots.Count,

                        SegmentIndex =
                            point.SegmentIndex,

                        Position =
                            point.Position,

                        Rotation =
                            point.Rotation
                    };

                slots.Add(
                    slot);
            }

            Logger.LogInfo(
                "Balanced fixed-slot build finished. " +
                "Requested=" +
                TargetSlotCount +
                " | Created=" +
                slots.Count +
                " | PreferredStartSpacing=" +
                SlotSpacingPasses[0] +
                "m" +
                " | MinimumFallbackSpacing=" +
                SlotSpacingPasses[
                    SlotSpacingPasses.Length - 1] +
                "m.");

            LogSlotDistribution();
        }

        private static int CompareSelectedPoints(
            CandidatePoint left,
            CandidatePoint right)
        {
            int segmentComparison =
                left.SegmentIndex.CompareTo(
                    right.SegmentIndex);

            if (segmentComparison != 0)
            {
                return segmentComparison;
            }

            int zComparison =
                left.Position.z.CompareTo(
                    right.Position.z);

            if (zComparison != 0)
            {
                return zComparison;
            }

            int yComparison =
                left.Position.y.CompareTo(
                    right.Position.y);

            if (yComparison != 0)
            {
                return yComparison;
            }

            return left.Position.x.CompareTo(
                right.Position.x);
        }

        /// <summary>
        /// 후보 전체를 거의 다 선택하지 않도록 최소 간격을 강제합니다.
        /// 15m부터 시작하고, 수량이 부족할 때만 2.25m까지 단계적으로 완화합니다.
        ///
        /// 선택 후 매번 모든 기존 선택점과 다시 비교하지 않고,
        /// 각 후보의 최근접 거리 캐시를 갱신하여 3000개에서도 처리량을 유지합니다.
        /// </summary>
        private static List<CandidatePoint>
            SelectFarthestPoints(
                List<CandidatePoint> source,
                int requestedCount,
                List<CandidatePoint> alreadySelected)
        {
            List<CandidatePoint> result =
                new List<CandidatePoint>();

            if (source == null ||
                source.Count == 0 ||
                requestedCount <= 0)
            {
                return result;
            }

            int candidateCount =
                source.Count;

            bool[] used =
                new bool[
                    candidateCount];

            float[] nearestDistanceSquared =
                new float[
                    candidateCount];

            for (int i = 0;
                 i < candidateCount;
                 i++)
            {
                nearestDistanceSquared[i] =
                    float.MaxValue;
            }

            if (alreadySelected != null &&
                alreadySelected.Count > 0)
            {
                for (int candidateIndex = 0;
                     candidateIndex < candidateCount;
                     candidateIndex++)
                {
                    CandidatePoint candidate =
                        source[
                            candidateIndex];

                    float nearest =
                        float.MaxValue;

                    for (int selectedIndex = 0;
                         selectedIndex <
                             alreadySelected.Count;
                         selectedIndex++)
                    {
                        float distance =
                            (
                                candidate.Position -
                                alreadySelected[
                                    selectedIndex].Position
                            ).sqrMagnitude;

                        if (distance <
                            nearest)
                        {
                            nearest =
                                distance;
                        }
                    }

                    nearestDistanceSquared[
                        candidateIndex] =
                            nearest;
                }
            }

            int firstIndex =
                FindFirstExtremePointIndex(
                    source);

            if (firstIndex >= 0)
            {
                AddSelectedCandidate(
                    source,
                    firstIndex,
                    used,
                    nearestDistanceSquared,
                    result);
            }

            for (int passIndex = 0;
                 passIndex <
                     SlotSpacingPasses.Length &&
                 result.Count <
                     requestedCount;
                 passIndex++)
            {
                float requiredDistance =
                    SlotSpacingPasses[
                        passIndex];

                float requiredDistanceSquared =
                    requiredDistance *
                    requiredDistance;

                while (result.Count <
                       requestedCount)
                {
                    int bestIndex = -1;
                    float bestDistance =
                        float.MinValue;

                    for (int candidateIndex = 0;
                         candidateIndex <
                             candidateCount;
                         candidateIndex++)
                    {
                        if (used[
                                candidateIndex])
                        {
                            continue;
                        }

                        float nearest =
                            nearestDistanceSquared[
                                candidateIndex];

                        if (nearest <
                            requiredDistanceSquared)
                        {
                            continue;
                        }

                        if (nearest >
                            bestDistance)
                        {
                            bestDistance =
                                nearest;

                            bestIndex =
                                candidateIndex;
                        }
                    }

                    if (bestIndex < 0)
                    {
                        break;
                    }

                    AddSelectedCandidate(
                        source,
                        bestIndex,
                        used,
                        nearestDistanceSquared,
                        result);
                }
            }

            return result;
        }

        private static int FindFirstExtremePointIndex(
            List<CandidatePoint> points)
        {
            if (points == null ||
                points.Count == 0)
            {
                return -1;
            }

            int selectedIndex = 0;

            for (int i = 1;
                 i < points.Count;
                 i++)
            {
                CandidatePoint candidate =
                    points[i];

                CandidatePoint selected =
                    points[
                        selectedIndex];

                if (candidate.Position.z <
                    selected.Position.z)
                {
                    selectedIndex = i;
                    continue;
                }

                if (Mathf.Approximately(
                        candidate.Position.z,
                        selected.Position.z) &&
                    candidate.Position.x <
                        selected.Position.x)
                {
                    selectedIndex = i;
                }
            }

            return selectedIndex;
        }

        private static void AddSelectedCandidate(
            List<CandidatePoint> source,
            int selectedIndex,
            bool[] used,
            float[] nearestDistanceSquared,
            List<CandidatePoint> result)
        {
            CandidatePoint selected =
                source[
                    selectedIndex];

            used[
                selectedIndex] = true;

            result.Add(
                selected);

            for (int candidateIndex = 0;
                 candidateIndex <
                     source.Count;
                 candidateIndex++)
            {
                if (used[
                        candidateIndex])
                {
                    continue;
                }

                float distance =
                    (
                        source[
                            candidateIndex].Position -
                        selected.Position
                    ).sqrMagnitude;

                if (distance <
                    nearestDistanceSquared[
                        candidateIndex])
                {
                    nearestDistanceSquared[
                        candidateIndex] =
                            distance;
                }
            }
        }

        private IEnumerator SpawnAllInitialSlots(
            int sceneHandle)
        {
            int successCount = 0;
            int failedCount = 0;

            for (int i = 0;
                 i < slots.Count;
                 i++)
            {
                if (!IsCurrentScene(
                        sceneHandle) ||
                    !PhotonNetwork.InRoom ||
                    !PhotonNetwork.IsMasterClient)
                {
                    yield break;
                }

                bool spawned =
                    SpawnSlotItem(
                        slots[i],
                        "Initial");

                if (spawned)
                {
                    successCount++;
                }
                else
                {
                    failedCount++;

                    slots[i].RespawnAt =
                        Time.time +
                        RespawnDelaySeconds;
                }

                if ((i + 1) %
                    InitialSpawnBatchSize == 0)
                {
                    yield return null;
                }
            }

            Logger.LogInfo(
                "Initial fixed-slot spawn complete. " +
                "Slots=" +
                slots.Count +
                " | Success=" +
                successCount +
                " | Failed=" +
                failedCount);

            LogLiveItemCounts();
        }

        private void MonitorSlots()
        {
            if (!initializationComplete)
            {
                return;
            }

            int respawnedThisTick = 0;

            for (int i = 0;
                 i < slots.Count;
                 i++)
            {
                SpawnSlot slot =
                    slots[i];

                if (slot.CurrentItem != null)
                {
                    MonitorOccupiedSlot(
                        slot);
                }

                if (slot.CurrentItem == null &&
                    slot.RespawnAt >= 0f &&
                    Time.time >=
                        slot.RespawnAt &&
                    respawnedThisTick <
                        RespawnBatchSize)
                {
                    bool spawned =
                        SpawnSlotItem(
                            slot,
                            "5MinuteRespawn");

                    if (spawned)
                    {
                        respawnedThisTick++;
                    }
                    else
                    {
                        slot.RespawnAt =
                            Time.time +
                            RespawnDelaySeconds;
                    }
                }
            }
        }

        private void MonitorOccupiedSlot(
            SpawnSlot slot)
        {
            Item item =
                slot.CurrentItem;

            if (item == null)
            {
                ScheduleSlotRespawn(
                    slot,
                    "ItemDestroyedOrMissing");

                return;
            }

            if (item.itemState !=
                ItemState.Ground)
            {
                ScheduleSlotRespawn(
                    slot,
                    "State=" +
                    item.itemState);

                return;
            }

            if (!item.gameObject.activeInHierarchy)
            {
                if (slot.InactiveSince <
                    0f)
                {
                    slot.InactiveSince =
                        Time.time;
                }

                if (Time.time -
                    slot.InactiveSince >=
                    InactivePickupConfirmationSeconds)
                {
                    ScheduleSlotRespawn(
                        slot,
                        "InactivePickupDetected");
                }

                return;
            }

            slot.InactiveSince = -1f;
        }

        private bool SpawnSlotItem(
            SpawnSlot slot,
            string reason)
        {
            if (slot == null ||
                !PhotonNetwork.InRoom ||
                !PhotonNetwork.IsMasterClient)
            {
                return false;
            }

            ResourceRarity selectedRarity;

            ushort selectedItemId =
                RollItemIdForCurrentUpgrade(
                    out selectedRarity);

            Item prefab;

            if (!ItemDatabase.TryGetItem(
                    selectedItemId,
                    out prefab) ||
                prefab == null ||
                prefab.gameObject == null)
            {
                Logger.LogError(
                    "Fixed-slot prefab lookup failed. " +
                    "Slot=" +
                    slot.SlotIndex +
                    " | ItemID=" +
                    selectedItemId +
                    " | Rarity=" +
                    selectedRarity +
                    " | UpgradeGrade=" +
                    CurrentUpgradeGrade);

                return false;
            }

            GameObject spawnedObject;

            try
            {
                spawnedObject =
                    PhotonNetwork
                        .InstantiateItemRoom(
                            prefab.gameObject.name,
                            slot.Position,
                            slot.Rotation);
            }
            catch (Exception exception)
            {
                Logger.LogError(
                    "Fixed-slot InstantiateItemRoom failed. " +
                    "Slot=" +
                    slot.SlotIndex +
                    " | ItemID=" +
                    selectedItemId +
                    " | Rarity=" +
                    selectedRarity +
                    " | Prefab=" +
                    prefab.gameObject.name +
                    " | Position=" +
                    FormatVector(
                        slot.Position) +
                    " | Error=" +
                    exception);

                return false;
            }

            if (spawnedObject == null)
            {
                Logger.LogError(
                    "Fixed-slot InstantiateItemRoom returned null. " +
                    "Slot=" +
                    slot.SlotIndex +
                    " | ItemID=" +
                    selectedItemId);

                return false;
            }

            Item spawnedItem =
                spawnedObject.GetComponent<Item>();

            PhotonView photonView =
                spawnedObject.GetComponent<PhotonView>();

            if (spawnedItem == null ||
                photonView == null)
            {
                Logger.LogError(
                    "Spawned fixed-slot object is missing Item or PhotonView. " +
                    "Slot=" +
                    slot.SlotIndex +
                    " | Object=" +
                    spawnedObject.name);

                if (PhotonNetwork.IsMasterClient)
                {
                    PhotonNetwork.Destroy(
                        spawnedObject);
                }

                return false;
            }

            DetachStateHandler(
                slot);

            slot.CurrentItem =
                spawnedItem;

            slot.CurrentViewId =
                photonView.ViewID;

            slot.LastSpawnedItemId =
                selectedItemId;

            slot.LastSpawnedRarity =
                selectedRarity;

            slot.RespawnAt = -1f;
            slot.InactiveSince = -1f;

            int capturedSlotIndex =
                slot.SlotIndex;

            Item capturedItem =
                spawnedItem;

            slot.StateChangedHandler =
                delegate (
                    ItemState state)
                {
                    HandleSpawnedItemStateChanged(
                        capturedSlotIndex,
                        capturedItem,
                        state);
                };

            spawnedItem.OnStateChange +=
                slot.StateChangedHandler;

            spawnedItem.SetKinematicNetworked(
                true,
                slot.Position,
                slot.Rotation);

            Logger.LogInfo(
                "Fixed-slot item spawned. " +
                "Reason=" +
                reason +
                " | Slot=" +
                slot.SlotIndex +
                " | Segment=" +
                slot.SegmentIndex +
                " | ItemID=" +
                selectedItemId +
                " | Rarity=" +
                selectedRarity +
                " | UpgradeGrade=" +
                CurrentUpgradeGrade +
                " | Prefab=" +
                prefab.gameObject.name +
                " | UIName=" +
                GetUiName(
                    prefab) +
                " | Position=" +
                FormatVector(
                    slot.Position) +
                " | ViewID=" +
                photonView.ViewID);

            return true;
        }

        private void HandleSpawnedItemStateChanged(
            int slotIndex,
            Item expectedItem,
            ItemState state)
        {
            if (!PhotonNetwork.IsMasterClient ||
                slotIndex < 0 ||
                slotIndex >=
                    slots.Count)
            {
                return;
            }

            if (state ==
                ItemState.Ground)
            {
                return;
            }

            SpawnSlot slot =
                slots[
                    slotIndex];

            if (slot.CurrentItem !=
                expectedItem)
            {
                return;
            }

            ScheduleSlotRespawn(
                slot,
                "OnStateChange=" +
                state);
        }

        private void ScheduleSlotRespawn(
            SpawnSlot slot,
            string reason)
        {
            if (slot == null)
            {
                return;
            }

            // 이미 같은 슬롯의 타이머가 시작된 경우 중복 예약하지 않습니다.
            if (slot.CurrentItem == null &&
                slot.RespawnAt >= 0f)
            {
                return;
            }

            int oldViewId =
                slot.CurrentViewId;

            DetachStateHandler(
                slot);

            slot.CurrentItem = null;
            slot.CurrentViewId = 0;
            slot.InactiveSince = -1f;

            slot.RespawnAt =
                Time.time +
                RespawnDelaySeconds;

            Logger.LogInfo(
                "Fixed-slot respawn scheduled. " +
                "Slot=" +
                slot.SlotIndex +
                " | Segment=" +
                slot.SegmentIndex +
                " | ItemID=" +
                slot.LastSpawnedItemId +
                " | Rarity=" +
                slot.LastSpawnedRarity +
                " | OldViewID=" +
                oldViewId +
                " | Reason=" +
                reason +
                " | RespawnIn=" +
                RespawnDelaySeconds +
                " seconds.");
        }

        private static void DetachStateHandler(
            SpawnSlot slot)
        {
            if (slot == null ||
                slot.CurrentItem == null ||
                slot.StateChangedHandler == null)
            {
                if (slot != null)
                {
                    slot.StateChangedHandler = null;
                }

                return;
            }

            slot.CurrentItem.OnStateChange -=
                slot.StateChangedHandler;

            slot.StateChangedHandler = null;
        }

        private void ClearSlots()
        {
            for (int i = 0;
                 i < slots.Count;
                 i++)
            {
                DetachStateHandler(
                    slots[i]);
            }

            slots.Clear();
        }

        private static ushort RollItemIdForCurrentUpgrade(
            out ResourceRarity selectedRarity)
        {
            float commonWeight;
            float normalWeight;
            float rareWeight;
            float uniqueWeight;
            float legendaryWeight;

            GetRarityWeights(
                CurrentUpgradeGrade,
                out commonWeight,
                out normalWeight,
                out rareWeight,
                out uniqueWeight,
                out legendaryWeight);

            // 해당 등급 안에서 모든 아이템 비중이 0이면
            // 등급 비중이 남아 있어도 실제 추첨 대상에서는 제외합니다.
            commonWeight =
                HasSelectableItem(
                    ResourceRarity.Common)
                    ? commonWeight
                    : 0f;

            normalWeight =
                HasSelectableItem(
                    ResourceRarity.Normal)
                    ? normalWeight
                    : 0f;

            rareWeight =
                HasSelectableItem(
                    ResourceRarity.Rare)
                    ? rareWeight
                    : 0f;

            uniqueWeight =
                HasSelectableItem(
                    ResourceRarity.Unique)
                    ? uniqueWeight
                    : 0f;

            legendaryWeight =
                HasSelectableItem(
                    ResourceRarity.Legendary)
                    ? legendaryWeight
                    : 0f;

            float totalWeight =
                commonWeight +
                normalWeight +
                rareWeight +
                uniqueWeight +
                legendaryWeight;

            if (totalWeight <=
                0.0001f)
            {
                selectedRarity =
                    ResourceRarity.Common;

                // 사용자가 현재 단계의 모든 등급 또는 아이템을 0으로 만든 경우
                // 게임 진행이 멈추지 않도록 Common 기본 목록에서 균등 추첨합니다.
                return GetUniformRandomItemId(
                    CommonResourceIds);
            }

            float roll =
                UnityEngine.Random.Range(
                    0f,
                    totalWeight);

            if (roll < commonWeight)
            {
                selectedRarity =
                    ResourceRarity.Common;

                return GetWeightedRandomItemId(
                    ResourceRarity.Common);
            }

            roll -= commonWeight;

            if (roll < normalWeight)
            {
                selectedRarity =
                    ResourceRarity.Normal;

                return GetWeightedRandomItemId(
                    ResourceRarity.Normal);
            }

            roll -= normalWeight;

            if (roll < rareWeight)
            {
                selectedRarity =
                    ResourceRarity.Rare;

                return GetWeightedRandomItemId(
                    ResourceRarity.Rare);
            }

            roll -= rareWeight;

            if (roll < uniqueWeight)
            {
                selectedRarity =
                    ResourceRarity.Unique;

                return GetWeightedRandomItemId(
                    ResourceRarity.Unique);
            }

            selectedRarity =
                ResourceRarity.Legendary;

            return GetWeightedRandomItemId(
                ResourceRarity.Legendary);
        }

        private static ushort GetWeightedRandomItemId(
            ResourceRarity rarity)
        {
            ushort[] itemIds =
                GetResourceIdsForRarity(
                    rarity);

            if (itemIds == null ||
                itemIds.Length == 0)
            {
                return CommonResourceIds[0];
            }

            float totalWeight = 0f;

            for (int i = 0;
                 i < itemIds.Length;
                 i++)
            {
                totalWeight +=
                    GetConfiguredItemWeight(
                        itemIds[i]);
            }

            if (totalWeight <=
                0.0001f)
            {
                return GetUniformRandomItemId(
                    itemIds);
            }

            float roll =
                UnityEngine.Random.Range(
                    0f,
                    totalWeight);

            for (int i = 0;
                 i < itemIds.Length;
                 i++)
            {
                float itemWeight =
                    GetConfiguredItemWeight(
                        itemIds[i]);

                if (itemWeight <= 0f)
                {
                    continue;
                }

                if (roll <
                    itemWeight)
                {
                    return itemIds[i];
                }

                roll -=
                    itemWeight;
            }

            // 부동소수점 경계 안전망입니다.
            for (int i = itemIds.Length - 1;
                 i >= 0;
                 i--)
            {
                if (GetConfiguredItemWeight(
                        itemIds[i]) >
                    0f)
                {
                    return itemIds[i];
                }
            }

            return GetUniformRandomItemId(
                itemIds);
        }

        private static ushort GetUniformRandomItemId(
            ushort[] itemIds)
        {
            if (itemIds == null ||
                itemIds.Length == 0)
            {
                return CommonResourceIds[0];
            }

            return itemIds[
                UnityEngine.Random.Range(
                    0,
                    itemIds.Length)];
        }

        private static ushort[] GetResourceIdsForRarity(
            ResourceRarity rarity)
        {
            switch (rarity)
            {
                case ResourceRarity.Common:
                    return CommonResourceIds;

                case ResourceRarity.Normal:
                    return NormalResourceIds;

                case ResourceRarity.Rare:
                    return RareResourceIds;

                case ResourceRarity.Unique:
                    return UniqueResourceIds;

                case ResourceRarity.Legendary:
                    return LegendaryResourceIds;

                default:
                    return CommonResourceIds;
            }
        }

        private static bool HasSelectableItem(
            ResourceRarity rarity)
        {
            ushort[] itemIds =
                GetResourceIdsForRarity(
                    rarity);

            if (itemIds == null)
            {
                return false;
            }

            for (int i = 0;
                 i < itemIds.Length;
                 i++)
            {
                if (GetConfiguredItemWeight(
                        itemIds[i]) >
                    0f)
                {
                    return true;
                }
            }

            return false;
        }

        private static float GetConfiguredItemWeight(
            ushort itemId)
        {
            ConfigEntry<float> entry;

            if (!ItemWeightConfigs.TryGetValue(
                    itemId,
                    out entry) ||
                entry == null)
            {
                return 1f;
            }

            return Mathf.Max(
                0f,
                entry.Value);
        }

        private static void GetRarityWeights(
            ResourceUpgradeGrade grade,
            out float commonWeight,
            out float normalWeight,
            out float rareWeight,
            out float uniqueWeight,
            out float legendaryWeight)
        {
            int gradeIndex =
                Mathf.Clamp(
                    (int)grade,
                    (int)ResourceUpgradeGrade.Common,
                    (int)ResourceUpgradeGrade.Legendary);

            commonWeight =
                GetConfiguredRarityWeight(
                    gradeIndex,
                    (int)ResourceRarity.Common);

            normalWeight =
                gradeIndex >=
                    (int)ResourceUpgradeGrade.Normal
                    ? GetConfiguredRarityWeight(
                        gradeIndex,
                        (int)ResourceRarity.Normal)
                    : 0f;

            rareWeight =
                gradeIndex >=
                    (int)ResourceUpgradeGrade.Rare
                    ? GetConfiguredRarityWeight(
                        gradeIndex,
                        (int)ResourceRarity.Rare)
                    : 0f;

            uniqueWeight =
                gradeIndex >=
                    (int)ResourceUpgradeGrade.Unique
                    ? GetConfiguredRarityWeight(
                        gradeIndex,
                        (int)ResourceRarity.Unique)
                    : 0f;

            legendaryWeight =
                gradeIndex >=
                    (int)ResourceUpgradeGrade.Legendary
                    ? GetConfiguredRarityWeight(
                        gradeIndex,
                        (int)ResourceRarity.Legendary)
                    : 0f;
        }

        private static float GetConfiguredRarityWeight(
            int gradeIndex,
            int rarityIndex)
        {
            ConfigEntry<float> entry =
                RarityWeightConfigs[
                    gradeIndex,
                    rarityIndex];

            if (entry == null)
            {
                return Mathf.Max(
                    0f,
                    DefaultRarityWeights[
                        gradeIndex,
                        rarityIndex]);
            }

            return Mathf.Max(
                0f,
                entry.Value);
        }

        private static void LogCurrentUpgradeProbabilities()
        {
            float commonWeight;
            float normalWeight;
            float rareWeight;
            float uniqueWeight;
            float legendaryWeight;

            GetRarityWeights(
                CurrentUpgradeGrade,
                out commonWeight,
                out normalWeight,
                out rareWeight,
                out uniqueWeight,
                out legendaryWeight);

            float totalWeight =
                commonWeight +
                normalWeight +
                rareWeight +
                uniqueWeight +
                legendaryWeight;

            if (ModLogger == null)
            {
                return;
            }

            if (totalWeight <=
                0.0001f)
            {
                ModLogger.LogWarning(
                    "Resource rarity probabilities are all 0 for " +
                    CurrentUpgradeGrade +
                    ". Common fallback spawning will be used.");

                return;
            }

            ModLogger.LogInfo(
                "Resource rarity probabilities. " +
                "UpgradeGrade=" +
                CurrentUpgradeGrade +
                " | Common=" +
                ToNormalizedPercent(
                    commonWeight,
                    totalWeight) +
                "% | Normal=" +
                ToNormalizedPercent(
                    normalWeight,
                    totalWeight) +
                "% | Rare=" +
                ToNormalizedPercent(
                    rareWeight,
                    totalWeight) +
                "% | Unique=" +
                ToNormalizedPercent(
                    uniqueWeight,
                    totalWeight) +
                "% | Legendary=" +
                ToNormalizedPercent(
                    legendaryWeight,
                    totalWeight) +
                "%. RawWeights=[" +
                commonWeight +
                ", " +
                normalWeight +
                ", " +
                rareWeight +
                ", " +
                uniqueWeight +
                ", " +
                legendaryWeight +
                "].");
        }

        private static void LogCurrentItemWeights()
        {
            if (ModLogger == null)
            {
                return;
            }

            string message =
                "Resource item weights:";

            for (int i = 0;
                 i < SaleResourceIds.Length;
                 i++)
            {
                ushort itemId =
                    SaleResourceIds[i];

                message +=
                    " [" +
                    itemId +
                    "=" +
                    GetConfiguredItemWeight(
                        itemId) +
                    "]";
            }

            ModLogger.LogInfo(
                message);
        }

        private static string ToNormalizedPercent(
            float weight,
            float totalWeight)
        {
            if (totalWeight <=
                0.0001f)
            {
                return "0.00";
            }

            return (
                weight /
                totalWeight *
                100f
            ).ToString("0.00");
        }

        private static void ValidateResourcePrefabs()
        {
            for (int i = 0;
                 i <
                     SaleResourceIds.Length;
                 i++)
            {
                ushort itemId =
                    SaleResourceIds[i];

                Item prefab;

                if (!ItemDatabase.TryGetItem(
                        itemId,
                        out prefab) ||
                    prefab == null)
                {
                    if (ModLogger != null)
                    {
                        ModLogger.LogError(
                            "Resource definition missing. " +
                            "ItemID=" +
                            itemId);
                    }

                    continue;
                }

                if (ModLogger != null)
                {
                    ModLogger.LogInfo(
                        "Resource definition OK. " +
                        "ItemID=" +
                        itemId +
                        " | Prefab=" +
                        prefab.gameObject.name +
                        " | UIName=" +
                        GetUiName(
                            prefab));
                }
            }
        }

        private void LogCandidateCountsBySegment(
            MapHandler mapHandler)
        {
            Dictionary<int, int> counts =
                new Dictionary<int, int>();

            for (int i = 0;
                 i < candidates.Count;
                 i++)
            {
                int segmentIndex =
                    candidates[i]
                        .SegmentIndex;

                if (!counts.ContainsKey(
                        segmentIndex))
                {
                    counts[
                        segmentIndex] = 0;
                }

                counts[
                    segmentIndex]++;
            }

            for (int i = 0;
                 i < mapHandler.segments.Length;
                 i++)
            {
                int count =
                    counts.ContainsKey(i)
                        ? counts[i]
                        : 0;

                string segmentName =
                    mapHandler.segments[i]
                        .segmentParent != null
                        ? mapHandler.segments[i]
                            .segmentParent.name
                        : "<NoParent>";

                Logger.LogInfo(
                    "Candidate segment coverage. " +
                    "Segment=" +
                    i +
                    " | Name=" +
                    segmentName +
                    " | Candidates=" +
                    count);
            }
        }

        private void LogSlotDistribution()
        {
            Dictionary<int, int> segmentCounts =
                new Dictionary<int, int>();

            for (int i = 0;
                 i < slots.Count;
                 i++)
            {
                SpawnSlot slot =
                    slots[i];

                if (!segmentCounts.ContainsKey(
                        slot.SegmentIndex))
                {
                    segmentCounts[
                        slot.SegmentIndex] = 0;
                }

                segmentCounts[
                    slot.SegmentIndex]++;
            }

            string segmentMessage =
                "Fixed slots by segment:";

            List<int> segmentIndexes =
                new List<int>(
                    segmentCounts.Keys);

            segmentIndexes.Sort();

            for (int i = 0;
                 i < segmentIndexes.Count;
                 i++)
            {
                int segmentIndex =
                    segmentIndexes[i];

                segmentMessage +=
                    " [" +
                    segmentIndex +
                    "=" +
                    segmentCounts[
                        segmentIndex] +
                    "]";
            }

            Logger.LogInfo(
                segmentMessage);

            Logger.LogInfo(
                "Slot item types are rolled when spawned. " +
                "Initial upgrade grade=" +
                CurrentUpgradeGrade +
                ".");

            LogCurrentUpgradeProbabilities();
        }

        private static void LogLiveItemCounts()
        {
            Dictionary<ushort, int> counts =
                new Dictionary<ushort, int>();

            for (int i = 0;
                 i <
                     SaleResourceIds.Length;
                 i++)
            {
                counts[
                    SaleResourceIds[i]] = 0;
            }

            if (Item.ALL_ITEMS != null)
            {
                List<Item> snapshot =
                    new List<Item>(
                        Item.ALL_ITEMS);

                for (int i = 0;
                     i <
                         snapshot.Count;
                     i++)
                {
                    Item item =
                        snapshot[i];

                    if (item == null ||
                        !IsSaleResourceId(
                            item.itemID))
                    {
                        continue;
                    }

                    counts[
                        item.itemID]++;
                }
            }

            string message =
                "Live sale resource instances:";

            for (int i = 0;
                 i <
                     SaleResourceIds.Length;
                 i++)
            {
                ushort itemId =
                    SaleResourceIds[i];

                message +=
                    " [" +
                    itemId +
                    "=" +
                    counts[
                        itemId] +
                    "]";
            }

            if (ModLogger != null)
            {
                ModLogger.LogInfo(
                    message);
            }
        }

        private static string GetUiName(
            Item item)
        {
            if (item == null ||
                item.UIData == null ||
                string.IsNullOrEmpty(
                    item.UIData.itemName))
            {
                return "<NoUIName>";
            }

            return item.UIData.itemName;
        }

        private static string GetHierarchyPath(
            Transform transform)
        {
            if (transform == null)
            {
                return "<null>";
            }

            string path =
                transform.name;

            Transform parent =
                transform.parent;

            while (parent != null)
            {
                path =
                    parent.name +
                    "/" +
                    path;

                parent =
                    parent.parent;
            }

            return path;
        }

        private static string FormatVector(
            Vector3 value)
        {
            return "(" +
                   value.x.ToString("0.000") +
                   ", " +
                   value.y.ToString("0.000") +
                   ", " +
                   value.z.ToString("0.000") +
                   ")";
        }
    }
}
