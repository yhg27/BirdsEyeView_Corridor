using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using Points;

namespace Experiment
{
    /// <summary>
    /// Manages experiment data collection and tracking for the VR drone path study.
    /// Tracks all metrics in real-time and saves to JSON on trial submission.
    /// </summary>
    public class ExperimentDataManager : MonoBehaviour
    {
        #region Singleton
        private static ExperimentDataManager _instance;
        public static ExperimentDataManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = FindFirstObjectByType<ExperimentDataManager>();
                }
                return _instance;
            }
        }
        #endregion

        [Header("Experiment Configuration")]
        [Tooltip("Participant ID (P01-P16)")]
        public string participantID = "P01";

        [Tooltip("Task variant for this build")]
        public TaskVariant taskVariant = TaskVariant.BirdsEye_Corridor;

        [Header("References")]
        [SerializeField] private Camera _mainCamera;
        [SerializeField] private FlightPathManager _flightPathManager;
        [SerializeField] private PointPlacementManager _pointPlacementManager;
        [SerializeField] private DronePathFollower _dronePathFollower;

        [Header("Tracking Settings")]
        [SerializeField] private float _movementSampleRate = 0.1f; // 10Hz

        // Trial state
        private bool _trialActive = false;
        private float _trialStartTime;
        private float _lastMovementSampleTime;

        // Waypoint tracking
        private List<WaypointData> _waypoints = new List<WaypointData>();
        private int _totalPointsPlaced = 0;
        private int _totalPointsDeleted = 0;
        private Dictionary<PointType, int> _pointTypeCount = new Dictionary<PointType, int>();

        // Path tracking
        private List<SegmentData> _segments = new List<SegmentData>();
        private int _placementBlockedCount = 0;
        private int _segmentBlockedCount = 0;

        // Movement tracking
        private List<MovementSample> _movementSamples = new List<MovementSample>();
        private Vector3 _lastCameraPosition;
        private float _totalDistanceMoved = 0f;

        // Drone tracking
        private float _droneFlightStartTime = -1f;
        private float _droneFlightDuration = 0f;
        private bool _droneFlightSuccess = false;

        // Event log
        private List<EventLogEntry> _eventLog = new List<EventLogEntry>();

        #region Unity Lifecycle

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            _instance = this;

            // Auto-find references if not set
            if (_mainCamera == null) _mainCamera = Camera.main;
            if (_flightPathManager == null) _flightPathManager = FindFirstObjectByType<FlightPathManager>();
            if (_pointPlacementManager == null) _pointPlacementManager = FindFirstObjectByType<PointPlacementManager>();
            if (_dronePathFollower == null) _dronePathFollower = FindFirstObjectByType<DronePathFollower>();

            // Initialize point type counter
            _pointTypeCount[PointType.FlyThrough] = 0;
            _pointTypeCount[PointType.StopAndRotate] = 0;
            _pointTypeCount[PointType.Record360] = 0;
        }

        private void Start()
        {
            StartTrial();
        }

        private void Update()
        {
            if (!_trialActive) return;

            // Sample camera movement at fixed rate
            float currentTime = Time.time - _trialStartTime;
            if (currentTime - _lastMovementSampleTime >= _movementSampleRate)
            {
                SampleCameraMovement(currentTime);
                _lastMovementSampleTime = currentTime;
            }
        }

        #endregion

        #region Trial Control

        private void StartTrial()
        {
            _trialActive = true;
            _trialStartTime = Time.time;
            _lastMovementSampleTime = 0f;
            
            if (_mainCamera != null)
            {
                _lastCameraPosition = _mainCamera.transform.position;
            }

            LogEvent("TrialStarted");
            Debug.Log($"[ExperimentDataManager] Trial started: {participantID} - {taskVariant}");
        }

        /// <summary>
        /// Call this method when the user presses the Submit button.
        /// Stops tracking, calculates metrics, and saves data.
        /// </summary>
        public void SubmitTrial()
        {
            if (!_trialActive)
            {
                Debug.LogWarning("[ExperimentDataManager] Trial already submitted!");
                return;
            }

            // Create segment from last waypoint → End point BEFORE setting _trialActive to false
            // (OnSegmentCreated checks _trialActive and returns early if false)
            CreateEndSegment();
            
            _trialActive = false;
            LogEvent("TrialSubmitted");

            // Save data
            string filePath = SaveTrialData();

            Debug.Log($"[ExperimentDataManager] Trial complete! Data saved to: {filePath}");
        }

        #endregion

        #region Waypoint Tracking

        public void OnWaypointPlaced(Vector3 position, PointType type, float yaw)
        {
            if (!_trialActive) return;

            _totalPointsPlaced++;
            _pointTypeCount[type]++;

            int waypointId = _waypoints.Count + 1;
            float timestamp = Time.time - _trialStartTime;

            _waypoints.Add(new WaypointData
            {
                id = waypointId,
                position = position,
                type = type,
                yaw = yaw,
                timestamp = timestamp,
                wasDeleted = false
            });

            LogEvent("WaypointPlaced", $"ID={waypointId}, Type={type}");
        }

        public void OnWaypointDeleted(int waypointId)
        {
            if (!_trialActive) return;

            _totalPointsDeleted++;

            // Mark waypoint as deleted
            var waypoint = _waypoints.FirstOrDefault(w => w.id == waypointId);
            if (waypoint != null)
            {
                waypoint.wasDeleted = true;
            }

            LogEvent("WaypointDeleted", $"ID={waypointId}");
        }

        #endregion

        #region Path Tracking

        public void OnSegmentCreated(int fromId, int toId, Vector3 fromPos, Vector3 toPos)
        {
            if (!_trialActive) return;

            float timestamp = Time.time - _trialStartTime;
            float length3D = Vector3.Distance(fromPos, toPos);
            float lengthHorizontal = Vector3.Distance(
                new Vector3(fromPos.x, 0, fromPos.z),
                new Vector3(toPos.x, 0, toPos.z)
            );
            float verticalChange = Mathf.Abs(toPos.y - fromPos.y);

            // Calculate angular change (if not first segment)
            float angularChange = 0f;
            if (_segments.Count > 0)
            {
                var prevSegment = _segments[_segments.Count - 1];
                Vector3 prevDir = (prevSegment.toPos - prevSegment.fromPos).normalized;
                Vector3 currentDir = (toPos - fromPos).normalized;
                angularChange = Vector3.Angle(prevDir, currentDir);
            }

            _segments.Add(new SegmentData
            {
                fromId = fromId,
                toId = toId,
                fromPos = fromPos,
                toPos = toPos,
                length3D = length3D,
                lengthHorizontal = lengthHorizontal,
                verticalChange = verticalChange,
                angularChangeDeg = angularChange,
                wasBlocked = false,
                timestamp = timestamp
            });

            LogEvent("SegmentCreated", $"From={fromId} To={toId}");
        }

        public void OnPlacementBlocked(Vector3 position, string reason)
        {
            if (!_trialActive) return;

            _placementBlockedCount++;
            LogEvent("PlacementBlocked", reason);
        }

        public void OnSegmentBlocked(Vector3 fromPos, Vector3 toPos, string reason)
        {
            if (!_trialActive) return;

            _segmentBlockedCount++;
            LogEvent("SegmentBlocked", reason);
        }

        #endregion

        #region Movement Tracking

        private void SampleCameraMovement(float currentTime)
        {
            if (_mainCamera == null) return;

            Vector3 currentPos = _mainCamera.transform.position;
            
            // Calculate distance moved since last sample
            float distanceMoved = Vector3.Distance(_lastCameraPosition, currentPos);
            _totalDistanceMoved += distanceMoved;

            _movementSamples.Add(new MovementSample
            {
                time = currentTime,
                position = currentPos
            });

            _lastCameraPosition = currentPos;
        }

        #endregion

        #region Drone Tracking

        public void OnDroneFlightStarted()
        {
            if (!_trialActive) return;

            _droneFlightStartTime = Time.time - _trialStartTime;
            LogEvent("DroneFlightStarted");
        }

        public void OnDroneFlightCompleted(bool success)
        {
            if (!_trialActive) return;

            if (_droneFlightStartTime >= 0)
            {
                _droneFlightDuration = (Time.time - _trialStartTime) - _droneFlightStartTime;
            }
            _droneFlightSuccess = success;

            LogEvent("DroneFlightCompleted", $"Success={success}");
        }

        #endregion

        #region Event Logging

        private void LogEvent(string eventName, string details = "")
        {
            float timestamp = _trialActive ? (Time.time - _trialStartTime) : 0f;
            _eventLog.Add(new EventLogEntry
            {
                time = timestamp,
                eventName = eventName,
                details = details
            });
        }

        #endregion

        #region Data Calculation

        private PathMetrics CalculatePathMetrics()
        {
            var activeSegments = _segments.Where(s => !s.wasBlocked).ToList();

            // Get Start/End positions
            Vector3 startPos = Vector3.zero;
            Vector3 endPos = Vector3.zero;
            
            if (_flightPathManager != null)
            {
                var startPoint = _flightPathManager.GetStartPoint();
                var endPoint = _flightPathManager.GetEndPoint();
                
                if (startPoint != null)
                {
                    startPos = startPoint.Position;
                }
                
                if (endPoint != null)
                {
                    endPos = endPoint.Position;
                }
            }

            if (activeSegments.Count == 0)
            {
                return new PathMetrics
                {
                    startPosition = startPos,
                    endPosition = endPos
                };
            }

            float totalLength3D = activeSegments.Sum(s => s.length3D);
            float totalLengthHorizontal = activeSegments.Sum(s => s.lengthHorizontal);
            float totalVerticalChange = activeSegments.Sum(s => s.verticalChange);

            var lengths = activeSegments.Select(s => s.length3D).ToList();
            float avgSegmentLength = lengths.Average();
            float stdSegmentLength = CalculateStdDev(lengths);

            // Angular changes (skip first segment which has 0 angle)
            var angles = activeSegments.Skip(1).Select(s => s.angularChangeDeg).ToList();
            float avgAngularChange = angles.Count > 0 ? angles.Average() : 0f;
            float stdAngularChange = angles.Count > 0 ? CalculateStdDev(angles) : 0f;
            float maxAngularChange = angles.Count > 0 ? angles.Max() : 0f;

            return new PathMetrics
            {
                startPosition = startPos,
                endPosition = endPos,
                totalLength3D = totalLength3D,
                totalLengthHorizontal2D = totalLengthHorizontal,
                totalVerticalChange = totalVerticalChange,
                segmentCount = activeSegments.Count,
                avgSegmentLength = avgSegmentLength,
                stdSegmentLength = stdSegmentLength,
                avgAngularChangeDeg = avgAngularChange,
                stdAngularChangeDeg = stdAngularChange,
                maxAngularChangeDeg = maxAngularChange
            };
        }

        private float CalculateStdDev(List<float> values)
        {
            if (values.Count <= 1) return 0f;

            float avg = values.Average();
            float sumOfSquares = values.Sum(v => (v - avg) * (v - avg));
            return Mathf.Sqrt(sumOfSquares / values.Count);
        }

        #endregion

        #region Data Saving

        private string SaveTrialData()
        {
            float trialDuration = Time.time - _trialStartTime;
            var pathMetrics = CalculatePathMetrics();
            var activeWaypoints = _waypoints.Where(w => !w.wasDeleted).ToList();

            // Build trial data structure
            var trialData = new TrialData
            {
                metadata = new TrialMetadata
                {
                    participantId = participantID,
                    taskVariant = taskVariant.ToString(),
                    viewMode = GetViewMode(),
                    environment = GetEnvironment(),
                    unityVersion = Application.unityVersion,
                    timestampStart = DateTime.Now.AddSeconds(-trialDuration).ToString("o"),
                    timestampEnd = DateTime.Now.ToString("o"),
                    durationSeconds = trialDuration
                },

                waypointMetrics = new WaypointMetrics
                {
                    totalPlaced = _totalPointsPlaced,
                    totalDeleted = _totalPointsDeleted,
                    finalCount = activeWaypoints.Count,
                    byType = new WaypointTypeCounts
                    {
                        FlyThrough = _pointTypeCount[PointType.FlyThrough],
                        StopRotate = _pointTypeCount[PointType.StopAndRotate],
                        Record360 = _pointTypeCount[PointType.Record360]
                    }
                },

                pathMetrics = pathMetrics,

                errorMetrics = new ErrorMetrics
                {
                    placementBlockedCount = _placementBlockedCount,
                    segmentBlockedCount = _segmentBlockedCount,
                    submittedWithGaps = CheckForGaps(),
                    droneCollisions = 0, // TODO: Implement if needed
                    droneFlightFailed = !_droneFlightSuccess
                },

                participantMovement = new ParticipantMovement
                {
                    totalDistanceMoved = _totalDistanceMoved,
                    samples = _movementSamples
                },

                droneFlight = new DroneFlightData
                {
                    durationSeconds = _droneFlightDuration,
                    success = _droneFlightSuccess
                },

                detailedWaypoints = _waypoints,
                detailedSegments = _segments,
                eventLog = _eventLog
            };

            // Serialize to JSON
            string json = JsonUtility.ToJson(trialData, true);

            // Determine save path with timestamp to avoid overwriting
            string folderPath = GetSaveFolderPath();
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string fileName = $"{participantID}_{taskVariant}_{timestamp}.json";
            string fullPath = Path.Combine(folderPath, fileName);

            // Ensure directory exists
            Directory.CreateDirectory(folderPath);

            // Write to file
            File.WriteAllText(fullPath, json);

            Debug.Log($"[ExperimentDataManager] Saved trial data to: {fullPath}");
            return fullPath;
        }

        private string GetSaveFolderPath()
        {
            string basePath = Application.persistentDataPath;
            string experimentFolder = Path.Combine(basePath, "isometric_corridor");
            return experimentFolder;
        }

        private string GetViewMode()
        {
            return taskVariant.ToString().StartsWith("RoomView") ? "RoomView" : "BirdsEye";
        }

        private string GetEnvironment()
        {
            return taskVariant.ToString().EndsWith("Corridor") ? "Corridor" : "Staircase";
        }

        private bool CheckForGaps()
        {
            // Check if there are any disconnected segments in the final path
            if (_flightPathManager == null) return false;

            var activeRoute = _flightPathManager.ActiveRoute;
            if (activeRoute == null || activeRoute.PointCount <= 1) return false;

            // Check if waypoints are sequentially connected
            var pointIds = activeRoute.PointIds;
            for (int i = 0; i < pointIds.Count - 1; i++)
            {
                int currentId = pointIds[i];
                int nextId = pointIds[i + 1];

                // Check if there's a segment connecting them
                bool hasConnection = _segments.Any(s => 
                    !s.wasBlocked && 
                    s.fromId == currentId && 
                    s.toId == nextId
                );

                if (!hasConnection)
                {
                    return true; // Gap found
                }
            }

            return false;
        }

        private void CreateEndSegment()
        {
            if (_flightPathManager == null) return;
            
            var endPoint = _flightPathManager.GetEndPoint();
            if (endPoint == null) return;
            
            // Find the last active waypoint
            var activeWaypoints = _waypoints.Where(w => !w.wasDeleted).OrderBy(w => w.id).ToList();
            if (activeWaypoints.Count == 0) return;
            
            var lastWaypoint = activeWaypoints[activeWaypoints.Count - 1];
            
            // Create segment from last waypoint to end point
            OnSegmentCreated(
                lastWaypoint.id,
                -2, // End point ID
                lastWaypoint.position,
                endPoint.transform.position
            );
        }

        #endregion
    }

    #region Data Structures

    public enum TaskVariant
    {
        RoomView_Corridor,
        RoomView_Staircase,
        BirdsEye_Corridor,
        BirdsEye_Staircase
    }

    public enum PointType
    {
        FlyThrough,
        StopAndRotate,
        Record360
    }

    [Serializable]
    public class TrialData
    {
        public TrialMetadata metadata;
        public WaypointMetrics waypointMetrics;
        public PathMetrics pathMetrics;
        public ErrorMetrics errorMetrics;
        public ParticipantMovement participantMovement;
        public DroneFlightData droneFlight;
        public List<WaypointData> detailedWaypoints;
        public List<SegmentData> detailedSegments;
        public List<EventLogEntry> eventLog;
    }

    [Serializable]
    public class TrialMetadata
    {
        public string participantId;
        public string taskVariant;
        public string viewMode;
        public string environment;
        public string unityVersion;
        public string timestampStart;
        public string timestampEnd;
        public float durationSeconds;
    }

    [Serializable]
    public class WaypointMetrics
    {
        public int totalPlaced;
        public int totalDeleted;
        public int finalCount;
        public WaypointTypeCounts byType;
    }

    [Serializable]
    public class WaypointTypeCounts
    {
        public int FlyThrough;
        public int StopRotate;
        public int Record360;
    }

    [Serializable]
    public class PathMetrics
    {
        public Vector3 startPosition;
        public Vector3 endPosition;
        public float totalLength3D;
        public float totalLengthHorizontal2D;
        public float totalVerticalChange;
        public int segmentCount;
        public float avgSegmentLength;
        public float stdSegmentLength;
        public float avgAngularChangeDeg;
        public float stdAngularChangeDeg;
        public float maxAngularChangeDeg;
    }

    [Serializable]
    public class ErrorMetrics
    {
        public int placementBlockedCount;
        public int segmentBlockedCount;
        public bool submittedWithGaps;
        public int droneCollisions;
        public bool droneFlightFailed;
    }

    [Serializable]
    public class ParticipantMovement
    {
        public float totalDistanceMoved;
        public List<MovementSample> samples;
    }

    [Serializable]
    public class DroneFlightData
    {
        public float durationSeconds;
        public bool success;
    }

    [Serializable]
    public class WaypointData
    {
        public int id;
        public Vector3 position;
        public PointType type;
        public float yaw;
        public float timestamp;
        public bool wasDeleted;
    }

    [Serializable]
    public class SegmentData
    {
        public int fromId;
        public int toId;
        public Vector3 fromPos;
        public Vector3 toPos;
        public float length3D;
        public float lengthHorizontal;
        public float verticalChange;
        public float angularChangeDeg;
        public bool wasBlocked;
        public float timestamp;
    }

    [Serializable]
    public class MovementSample
    {
        public float time;
        public Vector3 position;
    }

    [Serializable]
    public class EventLogEntry
    {
        public float time;
        public string eventName;
        public string details;
    }

    #endregion
}

