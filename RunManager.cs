// CRAFT PEAK RUN MANAGER BUILD 1.0.0
//
// 역할
// - Airport 대기 상태, 새 런 초기화, 플레이 중, 구간 이동, 종료 상태를 통합 관리합니다.
// - 런 ID, 런 순번, 현재 Segment, 시작/종료 시각, 현재 호스트를
//   Photon Room Custom Properties에 저장합니다.
// - 새 플레이어는 Photon Room Property를 통해 현재 런 상태를 자동으로 받습니다.
// - 호스트가 나가도 Room Property는 방에 남으므로 다음 호스트가 같은 런을 이어갈 수 있습니다.
// - 다음에 제작할 Connect.cs가 사용할 스냅샷 Export/Import API를 제공합니다.
// - 다음에 제작할 ResourceStreaming.cs가 사용할 런 상태/Segment 변경 이벤트를 제공합니다.
//
// 이 파일이 직접 관리하지 않는 것
// - 자원 슬롯과 재생성 타이머: ResourceStreaming.cs / Spawn.cs
// - 인벤토리 스택 세부 상태: Inventory.cs
// - 공유 돈과 업그레이드 세부 상태: Open.cs / 이후 Progression
// - 위 세부 상태들의 호스트 승계: Connect.cs
//
// 상태 흐름
//   None
//   → Lobby
//   → Initializing
//   → Playing
//   → Transitioning
//   → Playing
//   → Finished
//   → 다음 게임 맵에서 새로운 Initializing
//
// 중요
// - 리플렉션을 사용하지 않습니다.
// - Harmony 패치를 사용하지 않습니다.
// - Delete.cs와 같은 Craft PEAK.dll에 포함합니다.
// - 전역 PEAK 원본에도 RunManager 클래스가 있으므로,
//   충돌을 피하기 위해 실제 클래스명은 CraftRunManager입니다.

using BepInEx;
using BepInEx.Logging;
using ExitGames.Client.Photon;
using Photon.Pun;
using Photon.Realtime;
using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CraftPeak
{
    public enum CraftRunState
    {
        None = 0,
        Lobby = 1,
        Initializing = 2,
        Playing = 3,
        Transitioning = 4,
        Finished = 5
    }

    /// <summary>
    /// 현재 런의 공통 상태입니다.
    ///
    /// Photon이 사용자 정의 클래스를 자동 직렬화하지 않아도 사용할 수 있도록
    /// Connect.cs용 object[] 변환 메서드를 함께 제공합니다.
    /// </summary>
    public sealed class CraftRunSnapshot
    {
        public int ProtocolVersion;
        public int Revision;
        public int RunSequence;

        public string RunId;
        public CraftRunState State;

        public string SceneName;
        public int SceneBuildIndex;
        public int CurrentSegment;

        public double StartedAtNetworkTime;
        public double FinishedAtNetworkTime;
        public double UpdatedAtNetworkTime;

        public int HostActorNumber;

        public bool IsActive
        {
            get
            {
                return
                    State ==
                        CraftRunState.Initializing ||
                    State ==
                        CraftRunState.Playing ||
                    State ==
                        CraftRunState.Transitioning;
            }
        }

        public bool HasRunIdentity
        {
            get
            {
                return
                    !string.IsNullOrEmpty(
                        RunId) &&
                    RunSequence > 0;
            }
        }

        public CraftRunSnapshot Clone()
        {
            return
                new CraftRunSnapshot
                {
                    ProtocolVersion =
                        ProtocolVersion,

                    Revision =
                        Revision,

                    RunSequence =
                        RunSequence,

                    RunId =
                        RunId ?? string.Empty,

                    State =
                        State,

                    SceneName =
                        SceneName ?? string.Empty,

                    SceneBuildIndex =
                        SceneBuildIndex,

                    CurrentSegment =
                        CurrentSegment,

                    StartedAtNetworkTime =
                        StartedAtNetworkTime,

                    FinishedAtNetworkTime =
                        FinishedAtNetworkTime,

                    UpdatedAtNetworkTime =
                        UpdatedAtNetworkTime,

                    HostActorNumber =
                        HostActorNumber
                };
        }

        public object[] ToPayload()
        {
            return
                new object[]
                {
                    ProtocolVersion,
                    Revision,
                    RunSequence,
                    RunId ?? string.Empty,
                    (int)State,
                    SceneName ?? string.Empty,
                    SceneBuildIndex,
                    CurrentSegment,
                    StartedAtNetworkTime,
                    FinishedAtNetworkTime,
                    UpdatedAtNetworkTime,
                    HostActorNumber
                };
        }

        public static bool TryFromPayload(
            object[] payload,
            out CraftRunSnapshot snapshot)
        {
            snapshot =
                null;

            if (payload == null ||
                payload.Length < 12)
            {
                return false;
            }

            try
            {
                int stateValue =
                    Convert.ToInt32(
                        payload[4]);

                if (stateValue <
                        (int)CraftRunState.None ||
                    stateValue >
                        (int)CraftRunState.Finished)
                {
                    return false;
                }

                CraftRunSnapshot parsed =
                    new CraftRunSnapshot
                    {
                        ProtocolVersion =
                            Convert.ToInt32(
                                payload[0]),

                        Revision =
                            Convert.ToInt32(
                                payload[1]),

                        RunSequence =
                            Convert.ToInt32(
                                payload[2]),

                        RunId =
                            payload[3] as string ??
                            string.Empty,

                        State =
                            (CraftRunState)
                            stateValue,

                        SceneName =
                            payload[5] as string ??
                            string.Empty,

                        SceneBuildIndex =
                            Convert.ToInt32(
                                payload[6]),

                        CurrentSegment =
                            Convert.ToInt32(
                                payload[7]),

                        StartedAtNetworkTime =
                            Convert.ToDouble(
                                payload[8]),

                        FinishedAtNetworkTime =
                            Convert.ToDouble(
                                payload[9]),

                        UpdatedAtNetworkTime =
                            Convert.ToDouble(
                                payload[10]),

                        HostActorNumber =
                            Convert.ToInt32(
                                payload[11])
                    };

                snapshot =
                    parsed;

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public override string ToString()
        {
            return
                "RunId=" +
                (
                    string.IsNullOrEmpty(
                        RunId)
                        ? "<none>"
                        : RunId
                ) +
                " | Sequence=" +
                RunSequence +
                " | Revision=" +
                Revision +
                " | State=" +
                State +
                " | Scene=" +
                (
                    string.IsNullOrEmpty(
                        SceneName)
                        ? "<none>"
                        : SceneName
                ) +
                " | Segment=" +
                CurrentSegment +
                " | HostActor=" +
                HostActorNumber;
        }
    }

    [BepInPlugin(
        PluginGuid,
        PluginName,
        PluginVersion)]
    [BepInDependency(
        Delete.PluginGuid,
        BepInDependency.DependencyFlags.HardDependency)]
    public sealed class CraftRunManager :
        BaseUnityPlugin,
        IInRoomCallbacks
    {
        public const string PluginGuid =
            "com.sappheiros.crafting.runmanager";

        public const string PluginName =
            "Craft PEAK Run Manager";

        public const string PluginVersion =
            "1.0.1";

        public const int SnapshotProtocolVersion = 1;

        // Photon Room Custom Property 키입니다.
        // Connect.cs에서도 이 파일의 공개 스냅샷 API를 사용하고,
        // 키를 직접 중복 선언하지 않는 구조를 권장합니다.
        private const string PropertyProtocol =
            "CraftPeak.Run.Protocol";

        private const string PropertyRevision =
            "CraftPeak.Run.Revision";

        private const string PropertySequence =
            "CraftPeak.Run.Sequence";

        private const string PropertyRunId =
            "CraftPeak.Run.Id";

        private const string PropertyState =
            "CraftPeak.Run.State";

        private const string PropertySceneName =
            "CraftPeak.Run.SceneName";

        private const string PropertySceneBuildIndex =
            "CraftPeak.Run.SceneBuildIndex";

        private const string PropertySegment =
            "CraftPeak.Run.Segment";

        private const string PropertyStartedAt =
            "CraftPeak.Run.StartedAt";

        private const string PropertyFinishedAt =
            "CraftPeak.Run.FinishedAt";

        private const string PropertyUpdatedAt =
            "CraftPeak.Run.UpdatedAt";

        private const string PropertyHostActor =
            "CraftPeak.Run.HostActor";

        private const float StatePollIntervalSeconds =
            0.25f;

        private const float TransitionStateDurationSeconds =
            0.75f;

        private const int UnknownSegment = -1;

        private CraftRunSnapshot currentSnapshot =
            CreateEmptySnapshot();

        private Room observedRoom;

        private Scene loadedScene;

        private float nextStatePollAt;

        private float transitionCompleteAt = -1f;

        private bool sceneNeedsEvaluation;

        private bool roomSnapshotLoaded;

        private int lastObservedSegment =
            UnknownSegment;

        internal static CraftRunManager Instance
        {
            get;
            private set;
        }

        internal static ManualLogSource ModLogger
        {
            get;
            private set;
        }

        /// <summary>
        /// 모든 스냅샷 변경에서 호출됩니다.
        /// 인자로 전달되는 스냅샷은 복사본입니다.
        /// </summary>
        public static event Action<CraftRunSnapshot>
            SnapshotChanged;

        public static event Action<CraftRunSnapshot>
            RunStarted;

        public static event Action<CraftRunSnapshot>
            RunFinished;

        public static event Action<
            CraftRunState,
            CraftRunState>
            RunStateChanged;

        public static event Action<int, int>
            SegmentChanged;

        public static CraftRunState CurrentState
        {
            get
            {
                return Instance != null
                    ? Instance.currentSnapshot.State
                    : CraftRunState.None;
            }
        }

        public static string CurrentRunId
        {
            get
            {
                return Instance != null
                    ? Instance.currentSnapshot.RunId ??
                      string.Empty
                    : string.Empty;
            }
        }

        public static int CurrentRunSequence
        {
            get
            {
                return Instance != null
                    ? Instance.currentSnapshot.RunSequence
                    : 0;
            }
        }

        public static int CurrentSegment
        {
            get
            {
                return Instance != null
                    ? Instance.currentSnapshot.CurrentSegment
                    : UnknownSegment;
            }
        }

        public static int CurrentRevision
        {
            get
            {
                return Instance != null
                    ? Instance.currentSnapshot.Revision
                    : 0;
            }
        }

        public static int AuthoritativeHostActorNumber
        {
            get
            {
                return Instance != null
                    ? Instance.currentSnapshot.HostActorNumber
                    : 0;
            }
        }

        public static bool IsRunActive
        {
            get
            {
                return
                    Instance != null &&
                    Instance.currentSnapshot.IsActive;
            }
        }

        public static bool IsGameplaySceneLoaded
        {
            get
            {
                Scene scene =
                    SceneManager.GetActiveScene();

                return
                    IsGameplayScene(
                        scene);
            }
        }

        public static bool HasAuthoritativeRoomSnapshot
        {
            get
            {
                return
                    Instance != null &&
                    Instance.roomSnapshotLoaded &&
                    Instance.currentSnapshot
                        .ProtocolVersion ==
                    SnapshotProtocolVersion;
            }
        }

        private void Awake()
        {
            Instance =
                this;

            ModLogger =
                Logger;

            loadedScene =
                SceneManager.GetActiveScene();

            SceneManager.sceneLoaded +=
                HandleSceneLoaded;

            SceneManager.sceneUnloaded +=
                HandleSceneUnloaded;

            sceneNeedsEvaluation =
                true;

            Logger.LogInfo(
                PluginName +
                " " +
                PluginVersion +
                " loaded. Photon Room Properties will store the shared run state.");
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
            SceneManager.sceneLoaded -=
                HandleSceneLoaded;

            SceneManager.sceneUnloaded -=
                HandleSceneUnloaded;

            if (Instance == this)
            {
                Instance = null;
            }

            observedRoom =
                null;

            SnapshotChanged =
                null;

            RunStarted =
                null;

            RunFinished =
                null;

            RunStateChanged =
                null;

            SegmentChanged =
                null;

            ModLogger =
                null;
        }

        private void Update()
        {
            DetectRoomChange();

            if (Time.unscaledTime <
                nextStatePollAt)
            {
                return;
            }

            nextStatePollAt =
                Time.unscaledTime +
                StatePollIntervalSeconds;

            if (PhotonNetwork.InRoom &&
                !roomSnapshotLoaded)
            {
                TryApplyRoomSnapshot(
                    "Periodic room snapshot poll",
                    false);
            }

            EvaluateCurrentScene();

            if (!PhotonNetwork.InRoom ||
                !PhotonNetwork.IsMasterClient)
            {
                return;
            }

            MonitorAuthoritativeGameplayState();
        }

        private void DetectRoomChange()
        {
            Room currentRoom =
                PhotonNetwork.InRoom
                    ? PhotonNetwork.CurrentRoom
                    : null;

            if (ReferenceEquals(
                    observedRoom,
                    currentRoom))
            {
                return;
            }

            Room previousRoom =
                observedRoom;

            observedRoom =
                currentRoom;

            roomSnapshotLoaded =
                false;

            transitionCompleteAt =
                -1f;

            lastObservedSegment =
                UnknownSegment;

            if (currentRoom == null)
            {
                Logger.LogInfo(
                    "Left Photon room. Last known run snapshot is retained locally for reconnect/Connect.cs.");

                return;
            }

            Logger.LogInfo(
                "Entered Photon room. Room=" +
                currentRoom.Name +
                " | IsMaster=" +
                PhotonNetwork.IsMasterClient +
                ".");

            bool applied =
                TryApplyRoomSnapshot(
                    "Entered Photon room",
                    true);

            if (!PhotonNetwork.IsMasterClient)
            {
                return;
            }

            if (applied)
            {
                ReassertCurrentSnapshotAsHostInternal(
                    "Master entered room with an existing run snapshot");

                return;
            }

            Scene scene =
                SceneManager.GetActiveScene();

            if (IsGameplayScene(
                    scene))
            {
                BeginNewRunInternal(
                    "Master entered gameplay room without a run snapshot");

                return;
            }

            PublishLobbyInternal(
                "Master entered room without a run snapshot");
        }

        private void HandleSceneLoaded(
            Scene scene,
            LoadSceneMode loadSceneMode)
        {
            loadedScene =
                scene;

            sceneNeedsEvaluation =
                true;

            transitionCompleteAt =
                -1f;

            lastObservedSegment =
                UnknownSegment;

            Logger.LogInfo(
                "RunManager scene loaded. Scene=" +
                scene.name +
                " | BuildIndex=" +
                scene.buildIndex +
                ".");

            if (!PhotonNetwork.InRoom ||
                !PhotonNetwork.IsMasterClient)
            {
                return;
            }

            if (IsAirportScene(
                    scene))
            {
                HandleAirportEnteredByHost(
                    "Airport scene loaded");

                return;
            }

            if (IsTitleScene(
                    scene))
            {
                if (currentSnapshot.IsActive)
                {
                    FinishRunInternal(
                        "Title scene loaded");
                }

                return;
            }

            // MapHandler가 sceneLoaded 순간 아직 검색되지 않을 수 있으므로
            // 실제 게임 맵 시작은 Update의 EvaluateCurrentScene에서 확정합니다.
        }

        private void HandleSceneUnloaded(
            Scene scene)
        {
            if (scene.handle ==
                loadedScene.handle)
            {
                sceneNeedsEvaluation =
                    true;
            }
        }

        private void EvaluateCurrentScene()
        {
            Scene scene =
                SceneManager.GetActiveScene();

            if (!scene.IsValid() ||
                !scene.isLoaded)
            {
                return;
            }

            bool sceneChanged =
                !loadedScene.IsValid() ||
                loadedScene.handle !=
                    scene.handle;

            if (!sceneNeedsEvaluation &&
                !sceneChanged)
            {
                return;
            }

            loadedScene =
                scene;

            sceneNeedsEvaluation =
                false;

            if (!PhotonNetwork.InRoom ||
                !PhotonNetwork.IsMasterClient)
            {
                return;
            }

            if (IsAirportScene(
                    scene))
            {
                HandleAirportEnteredByHost(
                    "Airport scene evaluation");

                return;
            }

            if (IsTitleScene(
                    scene))
            {
                if (currentSnapshot.IsActive)
                {
                    FinishRunInternal(
                        "Title/Pretitle scene evaluation");
                }

                return;
            }

            if (!IsGameplayScene(
                    scene))
            {
                // 아직 MapHandler가 생성되기 전일 수 있으므로 다음 Poll에서 재검사합니다.
                sceneNeedsEvaluation =
                    true;

                return;
            }

            if (!currentSnapshot.IsActive)
            {
                BeginNewRunInternal(
                    "Gameplay scene became ready");

                return;
            }

            if (!string.Equals(
                    currentSnapshot.SceneName,
                    scene.name,
                    StringComparison.Ordinal) ||
                currentSnapshot.SceneBuildIndex !=
                    scene.buildIndex)
            {
                CraftRunSnapshot updated =
                    currentSnapshot.Clone();

                updated.SceneName =
                    scene.name;

                updated.SceneBuildIndex =
                    scene.buildIndex;

                updated.State =
                    CraftRunState.Initializing;

                updated.FinishedAtNetworkTime =
                    0d;

                PublishAuthoritativeSnapshot(
                    updated,
                    "Active run moved to a different gameplay scene");
            }
        }

        private void MonitorAuthoritativeGameplayState()
        {
            Scene scene =
                SceneManager.GetActiveScene();

            if (!IsGameplayScene(
                    scene))
            {
                return;
            }

            if (!currentSnapshot.IsActive)
            {
                BeginNewRunInternal(
                    "Gameplay monitor found no active run");

                return;
            }

            if (currentSnapshot.State ==
                    CraftRunState.Initializing &&
                IsGameplayReady())
            {
                CraftRunSnapshot playing =
                    currentSnapshot.Clone();

                playing.State =
                    CraftRunState.Playing;

                int detectedSegment =
                    ReadCurrentSegment();

                if (detectedSegment >= 0)
                {
                    playing.CurrentSegment =
                        detectedSegment;

                    lastObservedSegment =
                        detectedSegment;
                }

                PublishAuthoritativeSnapshot(
                    playing,
                    "Gameplay initialization completed");

                return;
            }

            if (currentSnapshot.State ==
                    CraftRunState.Transitioning &&
                transitionCompleteAt >= 0f &&
                Time.unscaledTime >=
                    transitionCompleteAt)
            {
                transitionCompleteAt =
                    -1f;

                CraftRunSnapshot playing =
                    currentSnapshot.Clone();

                playing.State =
                    CraftRunState.Playing;

                PublishAuthoritativeSnapshot(
                    playing,
                    "Automatic segment transition completed");

                return;
            }

            if (currentSnapshot.State !=
                    CraftRunState.Playing &&
                currentSnapshot.State !=
                    CraftRunState.Transitioning)
            {
                return;
            }

            int currentSegment =
                ReadCurrentSegment();

            if (currentSegment < 0)
            {
                return;
            }

            if (lastObservedSegment <
                0)
            {
                lastObservedSegment =
                    currentSnapshot.CurrentSegment >= 0
                        ? currentSnapshot.CurrentSegment
                        : currentSegment;
            }

            if (currentSegment ==
                currentSnapshot.CurrentSegment)
            {
                lastObservedSegment =
                    currentSegment;

                return;
            }

            int previousSegment =
                currentSnapshot.CurrentSegment;

            lastObservedSegment =
                currentSegment;

            CraftRunSnapshot transitioning =
                currentSnapshot.Clone();

            transitioning.CurrentSegment =
                currentSegment;

            transitioning.State =
                CraftRunState.Transitioning;

            PublishAuthoritativeSnapshot(
                transitioning,
                "MapHandler segment changed from " +
                previousSegment +
                " to " +
                currentSegment);

            transitionCompleteAt =
                Time.unscaledTime +
                TransitionStateDurationSeconds;
        }

        private void HandleAirportEnteredByHost(
            string reason)
        {
            if (!PhotonNetwork.IsMasterClient)
            {
                return;
            }

            if (currentSnapshot.IsActive)
            {
                FinishRunInternal(
                    reason);

                return;
            }

            if (currentSnapshot.State ==
                    CraftRunState.None)
            {
                PublishLobbyInternal(
                    reason);
            }

            // Finished 상태는 다음 게임 맵이 시작될 때까지 유지합니다.
            // 다음 BeginNewRun에서 RunSequence를 올리고 새 RunId를 만듭니다.
        }

        private bool BeginNewRunInternal(
            string reason)
        {
            if (!CanPublishAsHost())
            {
                return false;
            }

            Scene scene =
                SceneManager.GetActiveScene();

            if (!IsGameplayScene(
                    scene))
            {
                Logger.LogWarning(
                    "BeginNewRun rejected because the active scene is not a gameplay scene. Scene=" +
                    scene.name);

                return false;
            }

            int previousSequence =
                Mathf.Max(
                    0,
                    currentSnapshot.RunSequence);

            CraftRunSnapshot snapshot =
                new CraftRunSnapshot
                {
                    ProtocolVersion =
                        SnapshotProtocolVersion,

                    Revision =
                        currentSnapshot.Revision,

                    RunSequence =
                        previousSequence + 1,

                    RunId =
                        Guid.NewGuid()
                            .ToString("N"),

                    State =
                        CraftRunState.Initializing,

                    SceneName =
                        scene.name,

                    SceneBuildIndex =
                        scene.buildIndex,

                    CurrentSegment =
                        ReadCurrentSegment(),

                    StartedAtNetworkTime =
                        PhotonNetwork.Time,

                    FinishedAtNetworkTime =
                        0d,

                    UpdatedAtNetworkTime =
                        PhotonNetwork.Time,

                    HostActorNumber =
                        GetLocalActorNumber()
                };

            lastObservedSegment =
                snapshot.CurrentSegment;

            transitionCompleteAt =
                -1f;

            return
                PublishAuthoritativeSnapshot(
                    snapshot,
                    reason);
        }

        private bool FinishRunInternal(
            string reason)
        {
            if (!CanPublishAsHost())
            {
                return false;
            }

            if (!currentSnapshot.IsActive)
            {
                return false;
            }

            CraftRunSnapshot finished =
                currentSnapshot.Clone();

            finished.State =
                CraftRunState.Finished;

            finished.FinishedAtNetworkTime =
                PhotonNetwork.Time;

            transitionCompleteAt =
                -1f;

            return
                PublishAuthoritativeSnapshot(
                    finished,
                    reason);
        }

        private bool PublishLobbyInternal(
            string reason)
        {
            if (!CanPublishAsHost())
            {
                return false;
            }

            Scene scene =
                SceneManager.GetActiveScene();

            CraftRunSnapshot lobby =
                new CraftRunSnapshot
                {
                    ProtocolVersion =
                        SnapshotProtocolVersion,

                    Revision =
                        currentSnapshot.Revision,

                    RunSequence =
                        Mathf.Max(
                            0,
                            currentSnapshot.RunSequence),

                    RunId =
                        string.Empty,

                    State =
                        CraftRunState.Lobby,

                    SceneName =
                        scene.IsValid()
                            ? scene.name
                            : string.Empty,

                    SceneBuildIndex =
                        scene.IsValid()
                            ? scene.buildIndex
                            : -1,

                    CurrentSegment =
                        UnknownSegment,

                    StartedAtNetworkTime =
                        0d,

                    FinishedAtNetworkTime =
                        0d,

                    UpdatedAtNetworkTime =
                        PhotonNetwork.Time,

                    HostActorNumber =
                        GetLocalActorNumber()
                };

            return
                PublishAuthoritativeSnapshot(
                    lobby,
                    reason);
        }

        private bool ReassertCurrentSnapshotAsHostInternal(
            string reason)
        {
            if (!CanPublishAsHost())
            {
                return false;
            }

            CraftRunSnapshot snapshot =
                currentSnapshot.Clone();

            snapshot.ProtocolVersion =
                SnapshotProtocolVersion;

            snapshot.HostActorNumber =
                GetLocalActorNumber();

            snapshot.UpdatedAtNetworkTime =
                PhotonNetwork.Time;

            return
                PublishAuthoritativeSnapshot(
                    snapshot,
                    reason);
        }

        private bool PublishAuthoritativeSnapshot(
            CraftRunSnapshot snapshot,
            string reason)
        {
            if (!CanPublishAsHost() ||
                snapshot == null)
            {
                return false;
            }

            CraftRunSnapshot safe =
                SanitizeSnapshot(
                    snapshot);

            safe.ProtocolVersion =
                SnapshotProtocolVersion;

            safe.Revision =
                Mathf.Max(
                    currentSnapshot.Revision,
                    safe.Revision) +
                1;

            safe.HostActorNumber =
                GetLocalActorNumber();

            safe.UpdatedAtNetworkTime =
                PhotonNetwork.Time;

            ExitGames.Client.Photon.Hashtable
                properties =
                    BuildRoomProperties(
                        safe);

            bool queued =
                PhotonNetwork.CurrentRoom != null &&
                PhotonNetwork.CurrentRoom
                    .SetCustomProperties(
                        properties);

            if (!queued)
            {
                Logger.LogError(
                    "Failed to publish run snapshot to Photon Room Properties. Reason=" +
                    reason);

                return false;
            }

            roomSnapshotLoaded =
                true;

            ApplySnapshot(
                safe,
                reason,
                true);

            Logger.LogInfo(
                "Authoritative run snapshot published. Reason=" +
                reason +
                " | " +
                safe);

            return true;
        }

        private bool TryApplyRoomSnapshot(
            string reason,
            bool force)
        {
            if (!PhotonNetwork.InRoom ||
                PhotonNetwork.CurrentRoom == null)
            {
                return false;
            }

            CraftRunSnapshot snapshot;

            if (!TryReadSnapshotFromProperties(
                    PhotonNetwork.CurrentRoom
                        .CustomProperties,
                    out snapshot))
            {
                return false;
            }

            roomSnapshotLoaded =
                true;

            return
                ApplySnapshot(
                    snapshot,
                    reason,
                    force);
        }

        private bool ApplySnapshot(
            CraftRunSnapshot incoming,
            string reason,
            bool force)
        {
            if (incoming == null)
            {
                return false;
            }

            CraftRunSnapshot safe =
                SanitizeSnapshot(
                    incoming);

            if (safe.ProtocolVersion !=
                SnapshotProtocolVersion)
            {
                Logger.LogWarning(
                    "Ignored run snapshot with unsupported protocol. Incoming=" +
                    safe.ProtocolVersion +
                    " | Supported=" +
                    SnapshotProtocolVersion +
                    ".");

                return false;
            }

            if (!force &&
                safe.Revision <
                    currentSnapshot.Revision)
            {
                return false;
            }

            if (!force &&
                AreSnapshotsEquivalent(
                    currentSnapshot,
                    safe))
            {
                return false;
            }

            CraftRunSnapshot previous =
                currentSnapshot.Clone();

            currentSnapshot =
                safe.Clone();

            if (currentSnapshot.CurrentSegment >= 0)
            {
                lastObservedSegment =
                    currentSnapshot.CurrentSegment;
            }

            InvokeSnapshotEvents(
                previous,
                currentSnapshot);

            Logger.LogInfo(
                "Run snapshot applied. Reason=" +
                reason +
                " | " +
                currentSnapshot);

            return true;
        }

        private static void InvokeSnapshotEvents(
            CraftRunSnapshot previous,
            CraftRunSnapshot current)
        {
            CraftRunSnapshot currentCopy =
                current.Clone();

            if (SnapshotChanged != null)
            {
                SnapshotChanged(
                    currentCopy.Clone());
            }

            bool runIdentityChanged =
                !string.Equals(
                    previous.RunId,
                    current.RunId,
                    StringComparison.Ordinal) ||
                previous.RunSequence !=
                    current.RunSequence;

            if (runIdentityChanged &&
                current.IsActive &&
                RunStarted != null)
            {
                RunStarted(
                    currentCopy.Clone());
            }

            if (previous.State !=
                current.State)
            {
                if (RunStateChanged != null)
                {
                    RunStateChanged(
                        previous.State,
                        current.State);
                }

                if (current.State ==
                        CraftRunState.Finished &&
                    RunFinished != null)
                {
                    RunFinished(
                        currentCopy.Clone());
                }
            }

            if (previous.CurrentSegment !=
                    current.CurrentSegment &&
                SegmentChanged != null)
            {
                SegmentChanged(
                    previous.CurrentSegment,
                    current.CurrentSegment);
            }
        }

        private static CraftRunSnapshot
            SanitizeSnapshot(
                CraftRunSnapshot snapshot)
        {
            CraftRunSnapshot safe =
                snapshot.Clone();

            safe.ProtocolVersion =
                safe.ProtocolVersion <= 0
                    ? SnapshotProtocolVersion
                    : safe.ProtocolVersion;

            safe.Revision =
                Mathf.Max(
                    0,
                    safe.Revision);

            safe.RunSequence =
                Mathf.Max(
                    0,
                    safe.RunSequence);

            safe.RunId =
                safe.RunId ??
                string.Empty;

            safe.SceneName =
                safe.SceneName ??
                string.Empty;

            safe.SceneBuildIndex =
                Mathf.Max(
                    -1,
                    safe.SceneBuildIndex);

            safe.CurrentSegment =
                Mathf.Max(
                    UnknownSegment,
                    safe.CurrentSegment);

            safe.StartedAtNetworkTime =
                Math.Max(
                    0d,
                    safe.StartedAtNetworkTime);

            safe.FinishedAtNetworkTime =
                Math.Max(
                    0d,
                    safe.FinishedAtNetworkTime);

            safe.UpdatedAtNetworkTime =
                Math.Max(
                    0d,
                    safe.UpdatedAtNetworkTime);

            safe.HostActorNumber =
                Mathf.Max(
                    0,
                    safe.HostActorNumber);

            int stateValue =
                (int)safe.State;

            if (stateValue <
                    (int)CraftRunState.None ||
                stateValue >
                    (int)CraftRunState.Finished)
            {
                safe.State =
                    CraftRunState.None;
            }

            if (safe.State ==
                    CraftRunState.Lobby ||
                safe.State ==
                    CraftRunState.None)
            {
                safe.CurrentSegment =
                    UnknownSegment;
            }

            return safe;
        }

        private static bool TryReadSnapshotFromProperties(
            ExitGames.Client.Photon.Hashtable properties,
            out CraftRunSnapshot snapshot)
        {
            snapshot =
                null;

            if (properties == null)
            {
                return false;
            }

            object protocolValue;
            object revisionValue;
            object sequenceValue;
            object stateValue;

            if (!properties.TryGetValue(
                    PropertyProtocol,
                    out protocolValue) ||
                !properties.TryGetValue(
                    PropertyRevision,
                    out revisionValue) ||
                !properties.TryGetValue(
                    PropertySequence,
                    out sequenceValue) ||
                !properties.TryGetValue(
                    PropertyState,
                    out stateValue))
            {
                return false;
            }

            try
            {
                int parsedState =
                    Convert.ToInt32(
                        stateValue);

                if (parsedState <
                        (int)CraftRunState.None ||
                    parsedState >
                        (int)CraftRunState.Finished)
                {
                    return false;
                }

                CraftRunSnapshot parsed =
                    new CraftRunSnapshot
                    {
                        ProtocolVersion =
                            Convert.ToInt32(
                                protocolValue),

                        Revision =
                            Convert.ToInt32(
                                revisionValue),

                        RunSequence =
                            Convert.ToInt32(
                                sequenceValue),

                        RunId =
                            ReadStringProperty(
                                properties,
                                PropertyRunId),

                        State =
                            (CraftRunState)
                            parsedState,

                        SceneName =
                            ReadStringProperty(
                                properties,
                                PropertySceneName),

                        SceneBuildIndex =
                            ReadIntProperty(
                                properties,
                                PropertySceneBuildIndex,
                                -1),

                        CurrentSegment =
                            ReadIntProperty(
                                properties,
                                PropertySegment,
                                UnknownSegment),

                        StartedAtNetworkTime =
                            ReadDoubleProperty(
                                properties,
                                PropertyStartedAt,
                                0d),

                        FinishedAtNetworkTime =
                            ReadDoubleProperty(
                                properties,
                                PropertyFinishedAt,
                                0d),

                        UpdatedAtNetworkTime =
                            ReadDoubleProperty(
                                properties,
                                PropertyUpdatedAt,
                                0d),

                        HostActorNumber =
                            ReadIntProperty(
                                properties,
                                PropertyHostActor,
                                0)
                    };

                snapshot =
                    SanitizeSnapshot(
                        parsed);

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static ExitGames.Client.Photon.Hashtable
            BuildRoomProperties(
                CraftRunSnapshot snapshot)
        {
            return
                new ExitGames.Client.Photon.Hashtable
                {
                    {
                        PropertyProtocol,
                        snapshot.ProtocolVersion
                    },
                    {
                        PropertyRevision,
                        snapshot.Revision
                    },
                    {
                        PropertySequence,
                        snapshot.RunSequence
                    },
                    {
                        PropertyRunId,
                        snapshot.RunId ??
                        string.Empty
                    },
                    {
                        PropertyState,
                        (int)snapshot.State
                    },
                    {
                        PropertySceneName,
                        snapshot.SceneName ??
                        string.Empty
                    },
                    {
                        PropertySceneBuildIndex,
                        snapshot.SceneBuildIndex
                    },
                    {
                        PropertySegment,
                        snapshot.CurrentSegment
                    },
                    {
                        PropertyStartedAt,
                        snapshot.StartedAtNetworkTime
                    },
                    {
                        PropertyFinishedAt,
                        snapshot.FinishedAtNetworkTime
                    },
                    {
                        PropertyUpdatedAt,
                        snapshot.UpdatedAtNetworkTime
                    },
                    {
                        PropertyHostActor,
                        snapshot.HostActorNumber
                    }
                };
        }

        private static string ReadStringProperty(
            ExitGames.Client.Photon.Hashtable properties,
            string key)
        {
            object value;

            if (!properties.TryGetValue(
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

        private static int ReadIntProperty(
            ExitGames.Client.Photon.Hashtable properties,
            string key,
            int fallback)
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
                    Convert.ToInt32(
                        value);
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        private static double ReadDoubleProperty(
            ExitGames.Client.Photon.Hashtable properties,
            string key,
            double fallback)
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
                    Convert.ToDouble(
                        value);
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        private static bool AreSnapshotsEquivalent(
            CraftRunSnapshot left,
            CraftRunSnapshot right)
        {
            if (left == null ||
                right == null)
            {
                return false;
            }

            return
                left.ProtocolVersion ==
                    right.ProtocolVersion &&
                left.Revision ==
                    right.Revision &&
                left.RunSequence ==
                    right.RunSequence &&
                string.Equals(
                    left.RunId,
                    right.RunId,
                    StringComparison.Ordinal) &&
                left.State ==
                    right.State &&
                string.Equals(
                    left.SceneName,
                    right.SceneName,
                    StringComparison.Ordinal) &&
                left.SceneBuildIndex ==
                    right.SceneBuildIndex &&
                left.CurrentSegment ==
                    right.CurrentSegment &&
                Math.Abs(
                    left.StartedAtNetworkTime -
                    right.StartedAtNetworkTime) <
                    0.0001d &&
                Math.Abs(
                    left.FinishedAtNetworkTime -
                    right.FinishedAtNetworkTime) <
                    0.0001d &&
                Math.Abs(
                    left.UpdatedAtNetworkTime -
                    right.UpdatedAtNetworkTime) <
                    0.0001d &&
                left.HostActorNumber ==
                    right.HostActorNumber;
        }

        private static bool IsGameplayReady()
        {
            return
                UnityEngine.Object
                    .FindAnyObjectByType<MapHandler>() !=
                null &&
                Character.localCharacter !=
                null;
        }

        private static bool IsGameplayScene(
            Scene scene)
        {
            if (!scene.IsValid() ||
                !scene.isLoaded ||
                IsAirportScene(
                    scene) ||
                IsTitleScene(
                    scene))
            {
                return false;
            }

            return
                UnityEngine.Object
                    .FindAnyObjectByType<MapHandler>() !=
                null;
        }

        private static bool IsAirportScene(
            Scene scene)
        {
            return
                scene.IsValid() &&
                string.Equals(
                    scene.name,
                    "Airport",
                    StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsTitleScene(
            Scene scene)
        {
            if (!scene.IsValid())
            {
                return false;
            }

            return
                string.Equals(
                    scene.name,
                    "Title",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    scene.name,
                    "Pretitle",
                    StringComparison.OrdinalIgnoreCase);
        }

        private static int ReadCurrentSegment()
        {
            MapHandler mapHandler =
                UnityEngine.Object
                    .FindAnyObjectByType<MapHandler>();

            if (mapHandler == null)
            {
                return UnknownSegment;
            }

            return
                Mathf.Max(
                    0,
                    (int)MapHandler.CurrentSegmentNumber);
        }

        private static int GetLocalActorNumber()
        {
            return
                PhotonNetwork.LocalPlayer != null
                    ? PhotonNetwork.LocalPlayer.ActorNumber
                    : 0;
        }

        private static bool CanPublishAsHost()
        {
            return
                Instance != null &&
                PhotonNetwork.InRoom &&
                PhotonNetwork.IsMasterClient &&
                PhotonNetwork.CurrentRoom !=
                    null;
        }

        private static CraftRunSnapshot
            CreateEmptySnapshot()
        {
            return
                new CraftRunSnapshot
                {
                    ProtocolVersion =
                        SnapshotProtocolVersion,

                    Revision =
                        0,

                    RunSequence =
                        0,

                    RunId =
                        string.Empty,

                    State =
                        CraftRunState.None,

                    SceneName =
                        string.Empty,

                    SceneBuildIndex =
                        -1,

                    CurrentSegment =
                        UnknownSegment,

                    StartedAtNetworkTime =
                        0d,

                    FinishedAtNetworkTime =
                        0d,

                    UpdatedAtNetworkTime =
                        0d,

                    HostActorNumber =
                        0
                };
        }

        // -----------------------------------------------------------------
        // 외부 모드 파일용 공개 API
        // -----------------------------------------------------------------

        public static CraftRunSnapshot GetSnapshot()
        {
            return
                Instance != null
                    ? Instance.currentSnapshot.Clone()
                    : CreateEmptySnapshot();
        }

        public static object[] ExportSnapshotPayload()
        {
            return
                GetSnapshot()
                    .ToPayload();
        }

        /// <summary>
        /// Connect.cs가 이전 호스트 또는 로컬 보관본에서 받은 런 스냅샷을
        /// 새 호스트 권한으로 Room Property에 다시 확정할 때 사용합니다.
        /// </summary>
        public static bool TryImportSnapshotPayload(
            object[] payload,
            string reason)
        {
            CraftRunSnapshot snapshot;

            if (!CraftRunSnapshot.TryFromPayload(
                    payload,
                    out snapshot))
            {
                if (ModLogger != null)
                {
                    ModLogger.LogWarning(
                        "Run snapshot payload import failed: invalid payload.");
                }

                return false;
            }

            return
                TryImportSnapshot(
                    snapshot,
                    reason);
        }

        public static bool TryImportSnapshot(
            CraftRunSnapshot snapshot,
            string reason)
        {
            if (Instance == null ||
                snapshot == null ||
                !CanPublishAsHost())
            {
                return false;
            }

            CraftRunSnapshot imported =
                snapshot.Clone();

            imported.ProtocolVersion =
                SnapshotProtocolVersion;

            imported.Revision =
                Mathf.Max(
                    Instance.currentSnapshot.Revision,
                    imported.Revision);

            imported.HostActorNumber =
                GetLocalActorNumber();

            imported.UpdatedAtNetworkTime =
                PhotonNetwork.Time;

            return
                Instance.PublishAuthoritativeSnapshot(
                    imported,
                    string.IsNullOrEmpty(
                        reason)
                        ? "External snapshot import"
                        : reason);
        }

        public static bool TryBeginNewRun(
            string reason)
        {
            return
                Instance != null &&
                Instance.BeginNewRunInternal(
                    string.IsNullOrEmpty(
                        reason)
                        ? "External BeginNewRun"
                        : reason);
        }

        public static bool TryFinishCurrentRun(
            string reason)
        {
            return
                Instance != null &&
                Instance.FinishRunInternal(
                    string.IsNullOrEmpty(
                        reason)
                        ? "External FinishRun"
                        : reason);
        }

        public static bool TryResetToLobby(
            string reason)
        {
            return
                Instance != null &&
                Instance.PublishLobbyInternal(
                    string.IsNullOrEmpty(
                        reason)
                        ? "External lobby reset"
                        : reason);
        }

        public static bool TrySetRunState(
            CraftRunState state,
            string reason)
        {
            if (Instance == null ||
                !CanPublishAsHost())
            {
                return false;
            }

            int stateValue =
                (int)state;

            if (stateValue <
                    (int)CraftRunState.None ||
                stateValue >
                    (int)CraftRunState.Finished)
            {
                return false;
            }

            CraftRunSnapshot snapshot =
                Instance.currentSnapshot.Clone();

            snapshot.State =
                state;

            if (state ==
                CraftRunState.Finished)
            {
                snapshot.FinishedAtNetworkTime =
                    PhotonNetwork.Time;
            }
            else if (state ==
                     CraftRunState.Initializing &&
                     snapshot.StartedAtNetworkTime <=
                        0d)
            {
                snapshot.StartedAtNetworkTime =
                    PhotonNetwork.Time;
            }

            return
                Instance.PublishAuthoritativeSnapshot(
                    snapshot,
                    string.IsNullOrEmpty(
                        reason)
                        ? "External state change"
                        : reason);
        }

        public static bool TrySetCurrentSegment(
            int segment,
            bool markTransitioning,
            string reason)
        {
            if (Instance == null ||
                !CanPublishAsHost() ||
                segment < 0)
            {
                return false;
            }

            CraftRunSnapshot snapshot =
                Instance.currentSnapshot.Clone();

            snapshot.CurrentSegment =
                segment;

            if (markTransitioning)
            {
                snapshot.State =
                    CraftRunState.Transitioning;

                Instance.transitionCompleteAt =
                    Time.unscaledTime +
                    TransitionStateDurationSeconds;
            }

            Instance.lastObservedSegment =
                segment;

            return
                Instance.PublishAuthoritativeSnapshot(
                    snapshot,
                    string.IsNullOrEmpty(
                        reason)
                        ? "External segment change"
                        : reason);
        }

        /// <summary>
        /// 새 호스트가 Room Property에 남은 현재 스냅샷을
        /// 자신의 HostActorNumber로 재확정합니다.
        /// Connect.cs에서 전체 모듈 승계가 끝난 뒤 호출할 수 있습니다.
        /// </summary>
        public static bool TryReassertCurrentSnapshotAsHost(
            string reason)
        {
            return
                Instance != null &&
                Instance
                    .ReassertCurrentSnapshotAsHostInternal(
                        string.IsNullOrEmpty(
                            reason)
                            ? "External host reassertion"
                            : reason);
        }

        // -----------------------------------------------------------------
        // Photon IInRoomCallbacks
        // -----------------------------------------------------------------

        public void OnPlayerEnteredRoom(
            Photon.Realtime.Player newPlayer)
        {
            if (newPlayer == null)
            {
                return;
            }

            Logger.LogInfo(
                "Player entered room. Actor=" +
                newPlayer.ActorNumber +
                " | Name=" +
                newPlayer.NickName +
                ". Photon Room Properties will provide the run snapshot automatically.");

            // Room Custom Properties는 Photon이 자동으로 신규 참가자에게 전달합니다.
            // Connect.cs는 이 시점 이후 세부 모듈 스냅샷을 별도로 보냅니다.
        }

        public void OnPlayerLeftRoom(
            Photon.Realtime.Player otherPlayer)
        {
            if (otherPlayer == null)
            {
                return;
            }

            Logger.LogInfo(
                "Player left room. Actor=" +
                otherPlayer.ActorNumber +
                " | Name=" +
                otherPlayer.NickName +
                ".");
        }

        public void OnRoomPropertiesUpdate(
            ExitGames.Client.Photon.Hashtable
                propertiesThatChanged)
        {
            if (propertiesThatChanged == null ||
                !ContainsRunProperty(
                    propertiesThatChanged))
            {
                return;
            }

            TryApplyRoomSnapshot(
                "Photon room properties updated",
                false);
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
            if (newMasterClient == null)
            {
                return;
            }

            Logger.LogWarning(
                "Master client switched. NewHostActor=" +
                newMasterClient.ActorNumber +
                " | NewHostName=" +
                newMasterClient.NickName +
                ".");

            TryApplyRoomSnapshot(
                "Master client switched",
                true);

            if (PhotonNetwork.LocalPlayer == null ||
                newMasterClient.ActorNumber !=
                    PhotonNetwork.LocalPlayer.ActorNumber)
            {
                return;
            }

            // RunManager 자체의 핵심 상태는 Room Property에서 즉시 승계합니다.
            // Spawn/Inventory/Shop 등 세부 상태 승계는 Connect.cs가 담당합니다.
            if (roomSnapshotLoaded)
            {
                ReassertCurrentSnapshotAsHostInternal(
                    "Local player became master; preserved room run snapshot");

                return;
            }

            Scene scene =
                SceneManager.GetActiveScene();

            if (IsGameplayScene(
                    scene))
            {
                BeginNewRunInternal(
                    "Local player became master without an existing run snapshot");
            }
            else
            {
                PublishLobbyInternal(
                    "Local player became master without an existing run snapshot");
            }
        }

        private static bool ContainsRunProperty(
            ExitGames.Client.Photon.Hashtable properties)
        {
            return
                properties.ContainsKey(
                    PropertyProtocol) ||
                properties.ContainsKey(
                    PropertyRevision) ||
                properties.ContainsKey(
                    PropertySequence) ||
                properties.ContainsKey(
                    PropertyRunId) ||
                properties.ContainsKey(
                    PropertyState) ||
                properties.ContainsKey(
                    PropertySceneName) ||
                properties.ContainsKey(
                    PropertySceneBuildIndex) ||
                properties.ContainsKey(
                    PropertySegment) ||
                properties.ContainsKey(
                    PropertyStartedAt) ||
                properties.ContainsKey(
                    PropertyFinishedAt) ||
                properties.ContainsKey(
                    PropertyUpdatedAt) ||
                properties.ContainsKey(
                    PropertyHostActor);
        }
    }
}
