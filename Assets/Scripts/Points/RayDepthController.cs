using System.Collections;
using UnityEngine;
using UnityEngine.XR;

namespace Points
{
	/// <summary>
	/// Controls the depth along the right-hand ray using XR InputDevices and updates ghost/readout.
	/// </summary>
	public class RayDepthController : MonoBehaviour
	{
		[SerializeField] private PointPlacementManager _manager;
		[SerializeField] private Transform _rightControllerTransform;
		[SerializeField] private Transform _leftControllerTransform;
		[SerializeField] private Transform _ghostTransform;
		[SerializeField] private LineRenderer _rayLine;
		[SerializeField] private LayerMask _raycastMask = ~0;
		[SerializeField] private float _defaultDepth = 2.0f;
		[SerializeField] private float _readoutFadeDelay = 0.5f;
		[SerializeField] private float _repeatDelay = 0.25f;
		[SerializeField] private FlightPathManager _pathManager;
		
		// Thesis Feature: Exclude UI layer from point placement raycasts
		private LayerMask _surfaceRaycastMask;
		private Canvas _wristUICanvas; // Reference to user's manual wrist menu
		
		// Birds-eye View Feature: Viewpoint-aware raycasting
		[Header("Viewpoint-Aware Raycasting")]
		[Tooltip("Detector for inside/outside corridor viewpoint. If null, assumes inside (default behavior).")]
		[SerializeField] private CorridorViewpointDetector _viewpointDetector;
		[Tooltip("Layer for corridor shell (outer walls/ceiling). Raycasts ignore this layer when outside corridor.")]
		[SerializeField] private LayerMask _corridorShellLayer = 0; // Default: no layer (disabled)

		private float _currentDepth;
		private bool _triggerPrev;
		private bool _aPrev;
		private bool _bPrev;
		private bool _leftTriggerPrev;
		private float _nextRepeatTimeA;
		private float _nextRepeatTimeB;
		private InputDevice _rightHand;
		private InputDevice _leftHand;

		private void OnEnable()
		{
			_currentDepth = Mathf.Clamp(_defaultDepth, _manager != null ? _manager.MinDepth : 0.2f, _manager != null ? _manager.MaxDepth : 10f);
			_rightHand = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
			_leftHand = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
		}

		private void Start()
		{
			if (_pathManager == null)
			{
				_pathManager = UnityEngine.Object.FindFirstObjectByType<FlightPathManager>();
			}
			
			// Thesis Feature: Find user's manual wrist menu
			GameObject wristUIObj = GameObject.Find("WristUICanvas");
			if (wristUIObj != null)
			{
				_wristUICanvas = wristUIObj.GetComponent<Canvas>();
				Debug.Log("RayDepthController: Found WristUICanvas for UI blocking");
			}
			else
			{
				Debug.LogWarning("RayDepthController: WristUICanvas not found - button clicks might place waypoints");
			}
			
			// Thesis Feature: Create raycast mask that excludes UI layer
			int uiLayer = LayerMask.NameToLayer("UI");
			if (uiLayer >= 0)
			{
				// Exclude UI layer from surface snapping
				_surfaceRaycastMask = _raycastMask & ~(1 << uiLayer);
			}
			else
			{
				_surfaceRaycastMask = _raycastMask;
			}
			
			// Birds-eye View Feature: Auto-find viewpoint detector if not assigned
			if (_viewpointDetector == null)
			{
				_viewpointDetector = UnityEngine.Object.FindFirstObjectByType<CorridorViewpointDetector>();
				if (_viewpointDetector != null)
				{
					Debug.Log("RayDepthController: Auto-found CorridorViewpointDetector");
				}
			}
			
			// Birds-eye View Feature: Try to find CorridorShell layer by name if not set
			if (_corridorShellLayer == 0)
			{
				int shellLayer = LayerMask.NameToLayer("CorridorShell");
				if (shellLayer >= 0)
				{
					_corridorShellLayer = 1 << shellLayer;
					Debug.Log($"RayDepthController: Auto-found CorridorShell layer (index {shellLayer})");
				}
			}
			
			// Ensure the ray line is visible
			if (_rayLine != null)
			{
				// Create a bright, always-visible material
				Material rayMaterial = new Material(Shader.Find("Sprites/Default"));
				rayMaterial.color = Color.cyan;
				rayMaterial.renderQueue = 3000; // Render on top
				_rayLine.material = rayMaterial;
				
				// Set a bright color so it's visible
				_rayLine.startColor = new Color(0, 1, 1, 1); // Bright cyan, fully opaque
				_rayLine.endColor = new Color(0, 1, 1, 0.5f); // Cyan, fade at end
				
				// Set visible width
				_rayLine.startWidth = 0.01f; // 1cm (thicker for better visibility)
				_rayLine.endWidth = 0.005f; // 5mm at end
				
				// Make sure it renders in world space
				_rayLine.useWorldSpace = true;
				
				// Disable shadows
				_rayLine.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
				_rayLine.receiveShadows = false;
				
				// Set sorting order to render on top
				_rayLine.sortingOrder = 100;
			}
		}

		private void Update()
		{
			if (_manager == null || _ghostTransform == null || _rightControllerTransform == null)
			{
				return;
			}

			_rightHand = EnsureDevice(_rightHand, XRNode.RightHand);
			_leftHand = EnsureDevice(_leftHand, XRNode.LeftHand);

			// Handle hover feedback for points
			HandlePointHoverFeedback();

			// DISABLED: Precision mode via grip to avoid conflicts
			// bool precision = ReadButton(_rightHand, CommonUsages.gripButton);
			// float precisionMul = precision ? _manager.PrecisionMultiplier : 1f;
			float precisionMul = 1f; // Always use normal precision for now

			// DISABLED: Thumbstick depth control to avoid conflicts with XR Rig
			// Vector2 stick;
			// if (_rightHand.TryGetFeatureValue(CommonUsages.primary2DAxis, out stick))
			// {
			// 	float delta = stick.y * (_manager.DepthSpeed * precisionMul) * Time.deltaTime;
			// 	_currentDepth = Mathf.Clamp(_currentDepth + delta, _manager.MinDepth, _manager.MaxDepth);
			// }

			bool aBtn = ReadButton(_rightHand, CommonUsages.primaryButton);
			bool bBtn = ReadButton(_rightHand, CommonUsages.secondaryButton);
			if (EdgePressed(aBtn, ref _aPrev) || (aBtn && Time.time >= _nextRepeatTimeA))
			{
				_currentDepth = Mathf.Clamp(_currentDepth + _manager.DepthStep * precisionMul, _manager.MinDepth, _manager.MaxDepth);
				_manager.TickHaptics();
				_nextRepeatTimeA = Time.time + _repeatDelay;
			}
			if (EdgePressed(bBtn, ref _bPrev) || (bBtn && Time.time >= _nextRepeatTimeB))
			{
				_currentDepth = Mathf.Clamp(_currentDepth - _manager.DepthStep * precisionMul, _manager.MinDepth, _manager.MaxDepth);
				_manager.TickHaptics();
				_nextRepeatTimeB = Time.time + _repeatDelay;
			}

	Vector3 origin = _rightControllerTransform.position;
	Vector3 dir = _rightControllerTransform.forward;

	// Calculate validity outside the conditional blocks so it's accessible everywhere
	bool valid = _currentDepth >= _manager.MinDepth && _currentDepth <= _manager.MaxDepth;

	// Record360 Feature: Update recording point if adjusting height
	if (_manager.IsAdjustingRecordingHeight)
	{
		_manager.UpdateRecordingPointFromRay(origin, dir);
	}
	else
	{
		// Normal ghost positioning
		// DISABLED: Left grip for surface snapping to avoid conflicts
		// bool leftGrip = ReadButton(_leftHand, CommonUsages.gripButton);
		// bool useSnap = _manager.SurfaceSnappingEnabled && !leftGrip;
		bool useSnap = _manager.SurfaceSnappingEnabled;
		bool snapped = false;
		Vector3 ghostPos = origin + dir * _currentDepth;
		
		// Birds-eye View Feature: When outside, ray passes through ALL colliders to reach interior space
		// Ghost uses depth-based positioning (not surface-snapped)
		// No-fly zone will still check Environment colliders at this position
		bool isOutside = _viewpointDetector != null && !_viewpointDetector.IsInsideCorridor;
		
		if (useSnap && !isOutside)
		{
			// Inside corridor: Normal surface snapping (ray stops at colliders)
			RaycastHit hit;
			if (Physics.Raycast(origin, dir, out hit, _currentDepth + 0.01f, _surfaceRaycastMask, QueryTriggerInteraction.Ignore))
			{
				ghostPos = hit.point;
				snapped = true;
			}
		}
		// When outside: Don't use surface snapping - ghost stays at depth-based position
		// This allows placing waypoints inside corridor even though colliders exist
		// No-fly zone check (in UpdateGhostVisualValidity) will still prevent placement too close to walls/ceiling

		_ghostTransform.position = ghostPos;
		// When outside, allow placement even without surface snapping (ghost at depth-based position)
		// No-fly zone check will still prevent placement too close to obstacles
		bool isValidForPlacement = valid && (isOutside || !useSnap || snapped || Mathf.Abs(_currentDepth - Mathf.Clamp(_currentDepth, _manager.MinDepth, _manager.MaxDepth)) < 0.0001f);
		_manager.UpdateGhostVisualValidity(isValidForPlacement);
	}

	_manager.UpdateReadout($"{_currentDepth:F2} m");

	if (_rayLine != null)
	{
		_rayLine.positionCount = 2;
		_rayLine.SetPosition(0, origin);
		// Make ray extend far into the distance (100m = essentially infinite for VR)
		_rayLine.SetPosition(1, origin + dir * 100f);
	}

	// Handle right trigger for point placement
	bool trigger = ReadButton(_rightHand, CommonUsages.triggerButton);
	if (EdgePressed(trigger, ref _triggerPrev))
	{
		// Record360 Feature: If adjusting recording height, confirm placement
		if (_manager.IsAdjustingRecordingHeight)
		{
			_manager.PlaceAtCurrentGhost(); // Confirms the Record360 placement
			_manager.ConfirmHaptics();
			return;
		}

		// Thesis Feature: Check if ray is hitting UI first - if so, don't place point
		if (IsRayHittingUI(origin, dir))
		{
			Debug.Log("RayDepthController: Ray hitting UI, skipping point placement");
			return; // Don't place point if we're clicking UI
		}
		
		if (valid)
		{
			// Check if we're in path mode
			if (_pathManager != null && _pathManager.PathModeEnabled)
			{
				// Path mode is handled by PathModeController - do nothing here
				// This prevents duplicate handling
			}
			else
			{
				// Normal point placement mode (includes step 1 of Record360)
				_manager.PlaceAtCurrentGhost();
				_manager.ConfirmHaptics();
				StopAllCoroutines();
				StartCoroutine(FadeReadoutRoutine());
			}
		}
	}

	// Handle left trigger for point removal
	bool leftTrigger = ReadButton(_leftHand, CommonUsages.triggerButton);
	if (EdgePressed(leftTrigger, ref _leftTriggerPrev))
	{
		HandlePointRemoval();
	}
}

		private IEnumerator FadeReadoutRoutine()
		{
			yield return new WaitForSeconds(_readoutFadeDelay);
			_manager.FadeReadout();
		}

		private static bool ReadButton(InputDevice device, InputFeatureUsage<bool> usage)
		{
			if (!device.isValid) return false;
			bool v;
			return device.TryGetFeatureValue(usage, out v) && v;
		}

		private static bool EdgePressed(bool current, ref bool prev)
		{
			bool pressed = current && !prev;
			prev = current;
			return pressed;
		}

		private static InputDevice EnsureDevice(InputDevice device, XRNode node)
		{
			if (!device.isValid)
			{
				device = InputDevices.GetDeviceAtXRNode(node);
			}
			return device;
		}


		/// <summary>
		/// Handle left trigger for point removal - hover over a point and press left trigger to remove it.
		/// Birds-eye View Feature: When outside corridor, ray passes through Environment/CorridorShell layers
		/// to reach point handles inside the structure.
		/// </summary>
		private void HandlePointRemoval()
		{
			// Thesis Feature: Use LEFT controller for left trigger deletion
			if (_leftControllerTransform == null)
			{
				Debug.LogWarning("Left controller transform not assigned - cannot delete waypoints");
				return;
			}
			
			Vector3 origin = _leftControllerTransform.position;
			Vector3 dir = _leftControllerTransform.forward;

			Debug.Log($"Left trigger pressed - raycasting from LEFT controller at {origin} in direction {dir}");

			float maxDistance = 50f;
			RaycastHit hit = new RaycastHit();
			bool foundHit = false;

			// Birds-eye View Feature: Use viewpoint-aware raycasting
			// Inside corridor: normal raycast (all layers solid)
			// Outside corridor: use RaycastAll to pass through Environment/CorridorShell layers
			if (_viewpointDetector == null || _viewpointDetector.IsInsideCorridor)
			{
				// Inside: normal raycast (all layers)
				foundHit = Physics.Raycast(origin, dir, out hit, maxDistance, ~0, QueryTriggerInteraction.Ignore);
			}
			else
			{
				// Outside: use RaycastAll to get ALL hits, then filter out pass-through layers
				// Combine Environment layer and CorridorShell layer as layers to pass through
				LayerMask environmentLayer = _manager != null ? _manager.EnvironmentLayerMask : 0;
				LayerMask layersToPassThrough = environmentLayer;
				if (_corridorShellLayer != 0)
				{
					layersToPassThrough = layersToPassThrough | _corridorShellLayer;
				}

				// Get ALL hits along the entire ray path at once
				RaycastHit[] allHits = Physics.RaycastAll(origin, dir, maxDistance, ~0, QueryTriggerInteraction.Ignore);
				
				// Sort hits by distance (closest first)
				System.Array.Sort(allHits, (a, b) => a.distance.CompareTo(b.distance));
				
				// Find the first hit that's NOT on a layer we want to pass through
				foreach (RaycastHit testHit in allHits)
				{
					int hitLayer = testHit.collider.gameObject.layer;
					bool isPassThroughLayer = (layersToPassThrough & (1 << hitLayer)) != 0;
					
					if (!isPassThroughLayer)
					{
						// This is a valid hit (not Environment or CorridorShell) - use it!
						hit = testHit;
						foundHit = true;
						break;
					}
				}
			}

			if (foundHit)
			{
				Debug.Log($"Ray hit: {hit.collider.name} at distance {hit.distance}");
				
				// Check for regular waypoint
				var pointHandle = hit.collider.GetComponent<PointHandle>();
				if (pointHandle != null)
				{
					Debug.Log($"Found point handle {pointHandle.Id}, removing it");
					// Remove the point
					bool removed = _manager.RemovePoint(pointHandle.Id);
					if (removed)
					{
						_manager.ConfirmHaptics();
						StopAllCoroutines();
						StartCoroutine(FadeReadoutRoutine());
					}
					return;
				}
				
				// Check for Start/End point
				var startEndPoint = hit.collider.GetComponent<Points.StartEndPoint>();
				if (startEndPoint != null)
				{
					Debug.Log($"Found Start/End point {startEndPoint.Type} (ID: {startEndPoint.PointId}), removing from route");
					// Remove the Start/End point from the route
					var pathManager = UnityEngine.Object.FindFirstObjectByType<Points.FlightPathManager>();
					if (pathManager != null)
					{
						pathManager.RemoveWaypointFromRoute(startEndPoint.PointId);
						_manager.ConfirmHaptics();
						StopAllCoroutines();
						StartCoroutine(FadeReadoutRoutine());
					}
					return;
				}
				
				Debug.Log($"Hit object {hit.collider.name} but no PointHandle or StartEndPoint component found");
			}
			else
			{
				Debug.Log("Left trigger raycast hit nothing");
			}

			// If no point hit, provide feedback that nothing was removed
			_manager.TickHaptics(0.1f, 0.02f);
		}

		// Simple hover tracking
		private PointHandle _currentlyHovered;

		/// <summary>
		/// Handle hover feedback when pointing at points.
		/// </summary>
		private void HandlePointHoverFeedback()
		{
			Vector3 origin = _rightControllerTransform.position;
			Vector3 dir = _rightControllerTransform.forward;

			// Clear previous hover
			if (_currentlyHovered != null)
			{
				SetPointHoverColor(_currentlyHovered, false);
				_currentlyHovered = null;
			}

			// Raycast to find hovered points with longer range for better reliability
			if (Physics.Raycast(origin, dir, out RaycastHit hit, 50f))
			{
				var pointHandle = hit.collider.GetComponent<PointHandle>();
				if (pointHandle != null)
				{
					// Set new hover
					_currentlyHovered = pointHandle;
					SetPointHoverColor(pointHandle, true);
				}
			}
		}

		/// <summary>
		/// Set hover color on a point handle.
		/// </summary>
		private void SetPointHoverColor(PointHandle pointHandle, bool isHovered)
		{
			if (pointHandle == null) return;

			var renderer = pointHandle.GetComponent<Renderer>();
			if (renderer != null)
			{
				// Thesis Feature: Always use type-specific color - brighten slightly on hover
				Color typeColor = WaypointTypeDefinition.GetTypeColor(pointHandle.WaypointType);
				Color targetColor = isHovered ? Color.Lerp(typeColor, Color.white, 0.3f) : typeColor;

				foreach (var material in renderer.materials)
				{
					if (material != null && material.HasProperty("_Color"))
					{
						material.color = targetColor;
					}
				}
			}
		}

		/// <summary>
		/// Update readout text based on current mode.
		/// </summary>
		public void UpdateModeReadout()
		{
			if (_manager == null) return;

			string readoutText = $"{_currentDepth:F2} m";
			
			if (_pathManager != null && _pathManager.PathModeEnabled)
			{
				readoutText += " [PATH MODE]";
				var activeRoute = _pathManager.ActiveRoute;
				if (activeRoute != null)
				{
					readoutText += $" - {activeRoute.RouteName} ({activeRoute.PointCount})";
				}
			}

			_manager.UpdateReadout(readoutText);
		}

		/// <summary>
		/// Thesis Feature: Check if ray is pointing toward the WristUICanvas to prevent placement.
		/// </summary>
		private bool IsRayHittingUI(Vector3 origin, Vector3 direction)
		{
			// Method 1: Check if WristUICanvas is visible and we're pointing toward it
			if (_wristUICanvas != null && _wristUICanvas.gameObject.activeSelf)
			{
				// Get canvas position
				Vector3 canvasPos = _wristUICanvas.transform.position;
				float distanceToCanvas = Vector3.Distance(origin, canvasPos);
				
				// If canvas is close (within 1m) and we're roughly pointing at it
				if (distanceToCanvas < 1f)
				{
					Vector3 toCanvas = (canvasPos - origin).normalized;
					float angle = Vector3.Angle(direction, toCanvas);
					
					// If pointing within 45 degrees of canvas
					if (angle < 45f)
					{
						Debug.Log($"RayDepthController: Pointing at WristUICanvas (angle: {angle:F1}°), blocking waypoint placement");
						return true;
					}
				}
			}
			
			// Method 2: Physics raycast check for any UI-related objects
			RaycastHit hit;
			if (Physics.Raycast(origin, direction, out hit, 2f))
			{
				// Check if we hit anything related to UI/buttons
				Transform current = hit.collider.transform;
				for (int i = 0; i < 5; i++) // Check up to 5 parents
				{
					if (current == null) break;
					
					if (current.name.Contains("WristUICanvas") || 
					    current.name.Contains("Button") ||
					    current.name == "Border")
					{
						Debug.Log($"RayDepthController: Ray hit UI element ({current.name}), blocking waypoint placement");
						return true;
					}
					
					current = current.parent;
				}
			}
			
			return false;
		}
		
		/// <summary>
		/// Birds-eye View Feature: Multi-pass raycast that passes through CorridorShell when outside,
		/// but still hits Environment layer surfaces (interior floors, walls, ceiling).
		/// </summary>
		private bool RaycastThroughShell(Vector3 origin, Vector3 direction, float maxDistance, out RaycastHit finalHit)
		{
			finalHit = new RaycastHit();
			
			// If no viewpoint detector or no corridor shell layer, use normal raycast
			if (_viewpointDetector == null || _corridorShellLayer == 0)
			{
				return Physics.Raycast(origin, direction, out finalHit, maxDistance, _surfaceRaycastMask, QueryTriggerInteraction.Ignore);
			}
			
			// If inside corridor, use normal raycast (all colliders solid)
			if (_viewpointDetector.IsInsideCorridor)
			{
				return Physics.Raycast(origin, direction, out finalHit, maxDistance, _surfaceRaycastMask, QueryTriggerInteraction.Ignore);
			}
			
			// If outside corridor: multi-pass raycast to pass through CorridorShell but hit Environment
			float remainingDistance = maxDistance;
			Vector3 currentOrigin = origin;
			int maxPasses = 10; // Safety limit
			int passCount = 0;
			
			while (remainingDistance > 0.001f && passCount < maxPasses)
			{
				passCount++;
				RaycastHit hit;
				
				// First, check if we hit CorridorShell (outer walls/ceiling)
				LayerMask shellOnlyMask = _corridorShellLayer;
				if (Physics.Raycast(currentOrigin, direction, out hit, remainingDistance, shellOnlyMask, QueryTriggerInteraction.Ignore))
				{
					// Hit CorridorShell - pass through it and continue raycast
					float distanceToShell = hit.distance;
					currentOrigin = hit.point + direction * 0.01f; // Move slightly past the shell
					remainingDistance -= (distanceToShell + 0.01f);
					
					// Continue to next pass
					continue;
				}
				
				// No CorridorShell hit - check for Environment layer (interior surfaces)
				LayerMask environmentMask = _manager != null ? _manager.EnvironmentLayerMask : _surfaceRaycastMask;
				// Exclude CorridorShell from environment check
				environmentMask = environmentMask & ~_corridorShellLayer;
				
				if (Physics.Raycast(currentOrigin, direction, out hit, remainingDistance, environmentMask, QueryTriggerInteraction.Ignore))
				{
					// Hit Environment surface (interior floor, wall, ceiling) - this is our target!
					finalHit = hit;
					return true;
				}
				
				// No hit - ray went through everything
				break;
			}
			
			// No valid surface found
			return false;
		}
	}
}
