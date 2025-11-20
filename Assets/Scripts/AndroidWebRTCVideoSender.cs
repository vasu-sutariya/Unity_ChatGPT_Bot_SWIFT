using UnityEngine;
using System.Collections;
using System;
using Newtonsoft.Json;

#if UNITY_WEBRTC
using Unity.WebRTC;
#endif

/// <summary>
/// Captures video from Android camera and sends it via WebRTC
/// </summary>
public class AndroidWebRTCVideoSender : MonoBehaviour
{
    // Simple data classes for signaling messages
    [System.Serializable]
    private class SignalingMessage
    {
        public string type;
        public string sdp;
        public string candidate;
        public string sdpMid;
        public int sdpMLineIndex;
        public string roomId;
        public string peerId;
    }

    [System.Serializable]
    private class OfferPayload
    {
        public string type = "offer";
        public string sdp;
        public string roomId;
        public string peerId;
    }

    [System.Serializable]
    private class IceCandidatePayload
    {
        public string type = "ice-candidate";
        public string candidate;
        public string sdpMid;
        public int sdpMLineIndex;
        public string roomId;
        public string peerId;
    }

    [Header("Camera Settings")]
    [SerializeField] private int requestedWidth = 1280;
    [SerializeField] private int requestedHeight = 720;
    [SerializeField] private int requestedFPS = 30;
    [SerializeField] private int deviceIndex = 0;
    
    [Header("WebRTC Settings")]
    [SerializeField] private SignalingType signalingType = SignalingType.HTTP;
    [SerializeField] private string signalingServerUrl = "http://localhost:8080";
    [SerializeField] private string roomId = "default-room";
    [SerializeField] private string peerId = "";
    [SerializeField] private bool autoStart = false;
    
    public enum SignalingType
    {
        HTTP,      // Use HTTP POST requests (simpler, works with any server)
        WebSocket   // Use WebSocket (better for real-time, requires WebSocket server)
    }
    
    [Header("Debug")]
    [SerializeField] private bool allowEditorPreview = true;
    [SerializeField] private bool showPreview = true;
    [SerializeField] private UnityEngine.UI.RawImage previewImage;
    
    // Camera components
    private WebCamTexture webCamTexture;
    private string selectedDeviceName;
    private bool isStreaming = false;
    
    // WebRTC components
#if UNITY_WEBRTC
    private RTCPeerConnection peerConnection;
    private MediaStream videoStream;
    private VideoStreamTrack videoTrack;
    private RTCDataChannel dataChannel;
    
    // ICE servers configuration
    private RTCConfiguration rtcConfiguration = new RTCConfiguration
    {
        iceServers = new RTCIceServer[]
        {
            new RTCIceServer { urls = new string[] { "stun:stun.l.google.com:19302" } }
        }
    };
#endif

    // Events
    public event Action OnStreamStarted;
    public event Action OnStreamStopped;
    public event Action<string> OnError;

    private void Start()
    {
        EnsurePeerId();
        if (autoStart)
        {
            StartCoroutine(InitializeAndStart());
        }
    }

    private void OnDestroy()
    {
        StopStreaming();
        CleanupCamera();
    }

    private void OnApplicationPause(bool pauseStatus)
    {
        if (pauseStatus)
        {
            StopStreaming();
        }
        else if (autoStart && !isStreaming)
        {
            StartCoroutine(InitializeAndStart());
        }
    }

    /// <summary>
    /// Initialize camera and WebRTC, then start streaming
    /// </summary>
    public IEnumerator InitializeAndStart()
    {
        yield return StartCoroutine(InitializeCamera());
        yield return StartCoroutine(InitializeWebRTC());
        StartStreaming();
    }

    /// <summary>
    /// Initialize Android camera
    /// </summary>
    private IEnumerator InitializeCamera()
    {
        // Check platform environment
#if UNITY_EDITOR
        if (!allowEditorPreview)
        {
            Debug.LogWarning("AndroidWebRTCVideoSender is configured for Android only. Enable Allow Editor Preview to test in the Editor.");
            yield break;
        }
        else
        {
            Debug.Log("Running AndroidWebRTCVideoSender in Editor preview mode (using desktop camera).");
        }
#else
        if (Application.platform != RuntimePlatform.Android)
        {
            Debug.LogWarning("This script is designed for Android. Current platform: " + Application.platform);
        }
#endif

        // Request camera permission on Android
#if UNITY_ANDROID && !UNITY_EDITOR
        yield return RequestCameraPermission();

        if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Camera))
        {
            string error = "Camera permission denied. Cannot proceed.";
            Debug.LogError(error);
            OnError?.Invoke(error);
            yield break;
        }
#endif

        // Get available cameras
        if (WebCamTexture.devices.Length == 0)
        {
            string error = "No camera devices found";
            Debug.LogError(error);
            OnError?.Invoke(error);
            yield break;
        }

        Debug.Log($"Found {WebCamTexture.devices.Length} camera device(s)");

        // Select camera device
        if (deviceIndex >= 0 && deviceIndex < WebCamTexture.devices.Length)
        {
            selectedDeviceName = WebCamTexture.devices[deviceIndex].name;
        }
        else
        {
            selectedDeviceName = WebCamTexture.devices[0].name;
            deviceIndex = 0;
        }

        Debug.Log($"Selected camera: {selectedDeviceName} (index: {deviceIndex})");

        // Clean up any existing camera
        if (webCamTexture != null)
        {
            if (webCamTexture.isPlaying)
            {
                webCamTexture.Stop();
            }
            webCamTexture = null;
        }

        // Create WebCamTexture
        try
        {
            webCamTexture = new WebCamTexture(selectedDeviceName, requestedWidth, requestedHeight, requestedFPS);
            Debug.Log($"Created WebCamTexture with requested resolution: {requestedWidth}x{requestedHeight} @ {requestedFPS}fps");
        }
        catch (System.Exception e)
        {
            string error = $"Failed to create WebCamTexture: {e.Message}";
            Debug.LogError(error);
            OnError?.Invoke(error);
            yield break;
        }
        
        // Start camera
        Debug.Log("Starting camera...");
        webCamTexture.Play();
        
        // Wait for camera to start with timeout
        float timeout = 10f; // 10 seconds timeout
        float elapsed = 0f;
        
        while (!webCamTexture.isPlaying && elapsed < timeout)
        {
            elapsed += Time.deltaTime;
            yield return null;
        }
        
        if (!webCamTexture.isPlaying)
        {
            string error = $"Camera failed to start after {timeout} seconds";
            Debug.LogError(error);
            OnError?.Invoke(error);
            
            // Cleanup
            if (webCamTexture != null)
            {
                webCamTexture.Stop();
                webCamTexture = null;
            }
            yield break;
        }
        
        Debug.Log("Camera is playing, waiting for texture to be ready...");
        
        // Wait for texture dimensions to be valid
        elapsed = 0f;
        while ((webCamTexture.width <= 16 || webCamTexture.height <= 16) && elapsed < 5f)
        {
            elapsed += Time.deltaTime;
            yield return null;
        }
        
        // Wait a bit more for the texture to be fully ready
        yield return new WaitForSeconds(0.5f);

        Debug.Log($"Camera started successfully! Resolution: {webCamTexture.width}x{webCamTexture.height}, FPS: {requestedFPS}");
        Debug.Log($"Camera device: {webCamTexture.deviceName}");
        
        // Verify camera is still playing
        if (!webCamTexture.isPlaying)
        {
            string error = "Camera stopped unexpectedly after initialization";
            Debug.LogError(error);
            OnError?.Invoke(error);
            yield break;
        }
        
        // Setup preview if enabled
        if (showPreview && previewImage != null)
        {
            previewImage.texture = webCamTexture;
            previewImage.enabled = true;
            Debug.Log("Preview image set");
        }
    }

    /// <summary>
    /// Request camera permission on Android
    /// </summary>
    private IEnumerator RequestCameraPermission()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Camera))
        {
            UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.Camera);
            yield return new WaitForSeconds(1f);
            
            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Camera))
            {
                string error = "Camera permission denied";
                Debug.LogError(error);
                OnError?.Invoke(error);
                yield break;
            }
        }
#endif
        yield return null;
    }

    /// <summary>
    /// Initialize WebRTC connection
    /// </summary>
    private IEnumerator InitializeWebRTC()
    {
#if UNITY_WEBRTC
        string error = null;
        try
        {
            // Create peer connection
            peerConnection = new RTCPeerConnection(ref rtcConfiguration);
            
            // Setup event handlers
            peerConnection.OnIceCandidate = (RTCIceCandidate candidate) =>
            {
                Debug.Log($"ICE Candidate: {candidate.Candidate}");
                // Send candidate to signaling server
                SendIceCandidate(candidate);
            };
            
            peerConnection.OnIceConnectionChange = (RTCIceConnectionState state) =>
            {
                Debug.Log($"ICE Connection State: {state}");
            };
            
            peerConnection.OnConnectionStateChange = (RTCPeerConnectionState state) =>
            {
                Debug.Log($"Peer Connection State: {state}");
            };
            
            // Create video track from WebCamTexture
            if (webCamTexture != null && webCamTexture.isPlaying)
            {
                videoTrack = new VideoStreamTrack(webCamTexture);
                
                if (videoStream == null)
                {
                    videoStream = new MediaStream();
                }

                videoStream.AddTrack(videoTrack);

                peerConnection.AddTrack(videoTrack, videoStream);
                
                Debug.Log("Video track added to peer connection");
            }
        }
        catch (Exception e)
        {
            error = $"WebRTC initialization failed: {e.Message}";
        }

        if (error != null)
        {
            Debug.LogError(error);
            OnError?.Invoke(error);
            yield break;
        }

        yield return null;
#else
        Debug.LogWarning("Unity WebRTC package not installed. Please install com.unity.webrtc package.");
        yield return null;
#endif
    }

    /// <summary>
    /// Start streaming video
    /// </summary>
    public void StartStreaming()
    {
        if (isStreaming)
        {
            Debug.LogWarning("Streaming is already active");
            return;
        }

        if (webCamTexture == null)
        {
            Debug.LogError("Camera is not initialized (webCamTexture is null)");
            OnError?.Invoke("Camera is not initialized");
            return;
        }

        if (!webCamTexture.isPlaying)
        {
            Debug.LogError($"Camera is not playing. Device: {webCamTexture.deviceName}, Width: {webCamTexture.width}, Height: {webCamTexture.height}");
            OnError?.Invoke("Camera is not playing");
            return;
        }

#if UNITY_WEBRTC
        if (peerConnection == null)
        {
            Debug.LogError("WebRTC peer connection is not initialized");
            return;
        }

        StartCoroutine(CreateOffer());
        isStreaming = true;
        
        // Start polling for incoming messages (answer, ICE candidates)
        if (signalingType == SignalingType.HTTP)
        {
            StartCoroutine(PollForMessages());
        }
        
        OnStreamStarted?.Invoke();
        Debug.Log("Video streaming started");
#else
        Debug.LogWarning("Unity WebRTC package not installed. Cannot start streaming.");
#endif
    }

    /// <summary>
    /// Stop streaming video
    /// </summary>
    public void StopStreaming()
    {
        if (!isStreaming) return;

#if UNITY_WEBRTC
        if (videoTrack != null)
        {
            videoTrack.Stop();
            videoTrack.Dispose();
            videoTrack = null;
        }

        if (videoStream != null)
        {
            videoStream.Dispose();
            videoStream = null;
        }

        if (peerConnection != null)
        {
            peerConnection.Close();
            peerConnection.Dispose();
            peerConnection = null;
        }
#endif

        isStreaming = false;
        OnStreamStopped?.Invoke();
        Debug.Log("Video streaming stopped");
    }

    /// <summary>
    /// Create WebRTC offer
    /// </summary>
    private IEnumerator CreateOffer()
    {
#if UNITY_WEBRTC
        var offer = peerConnection.CreateOffer();
        yield return offer;

        if (offer.IsError)
        {
            Debug.LogError($"Error creating offer: {offer.Error.message}");
            yield break;
        }

        var offerDesc = offer.Desc;
        var op = peerConnection.SetLocalDescription(ref offerDesc);
        yield return op;

        if (op.IsError)
        {
            Debug.LogError($"Error setting local description: {op.Error.message}");
            yield break;
        }

        // Send offer to signaling server
        SendOffer(offerDesc);
#else
        yield return null;
#endif
    }

    /// <summary>
    /// Handle answer from remote peer
    /// </summary>
    public void SetRemoteDescription(string sdp, string type)
    {
#if UNITY_WEBRTC
        StartCoroutine(SetRemoteDescriptionCoroutine(sdp, type));
#endif
    }

    private IEnumerator SetRemoteDescriptionCoroutine(string sdp, string type)
    {
#if UNITY_WEBRTC
        RTCSdpType sdpType = type == "answer" ? RTCSdpType.Answer : RTCSdpType.Offer;
        RTCSessionDescription desc = new RTCSessionDescription { type = sdpType, sdp = sdp };
        
        var op = peerConnection.SetRemoteDescription(ref desc);
        yield return op;

        if (op.IsError)
        {
            Debug.LogError($"Error setting remote description: {op.Error.message}");
        }
        else
        {
            Debug.Log("Remote description set successfully");
        }
#else
        yield return null;
#endif
    }

    /// <summary>
    /// Add ICE candidate
    /// </summary>
    public void AddIceCandidate(string candidate, string sdpMid, int sdpMLineIndex)
    {
#if UNITY_WEBRTC
        var init = new RTCIceCandidateInit
        {
            candidate = candidate,
            sdpMid = sdpMid,
            sdpMLineIndex = sdpMLineIndex
        };
        RTCIceCandidate iceCandidate = new RTCIceCandidate(init);

        peerConnection.AddIceCandidate(iceCandidate);
        Debug.Log("ICE candidate added");
#endif
    }

    /// <summary>
    /// WHAT IS SIGNALING?
    /// 
    /// WebRTC needs a "signaling server" to help two devices connect to each other.
    /// Think of it like this:
    /// - Your phone (Android) wants to send video to another device (like a computer)
    /// - But they don't know each other's network addresses yet
    /// - The signaling server acts as a "middleman" that helps them exchange information
    /// 
    /// What gets sent:
    /// 1. "Offer" - Your phone says "I want to connect, here's my info"
    /// 2. "Answer" - The other device responds "OK, here's my info"
    /// 3. "ICE Candidates" - Network addresses to help them find each other
    /// 
    /// After signaling, the devices connect directly (peer-to-peer) and the server is no longer needed.
    /// </summary>
    
    /// <summary>
    /// Send offer to signaling server using HTTP POST
    /// This sends your connection offer to the server, which forwards it to the other device
    /// </summary>
#if UNITY_WEBRTC
    private void SendOffer(RTCSessionDescription offer)
    {
        Debug.Log($"Sending offer to signaling server: {signalingServerUrl}");
        
        if (signalingType == SignalingType.HTTP)
        {
            StartCoroutine(SendOfferHTTP(offer));
        }
        else if (signalingType == SignalingType.WebSocket)
        {
            SendOfferWebSocket(offer);
        }
    }

    /// <summary>
    /// Send offer using HTTP POST (like sending data to a website)
    /// </summary>
    private IEnumerator SendOfferHTTP(RTCSessionDescription offer)
    {
        if (string.IsNullOrEmpty(roomId))
        {
            OnError?.Invoke("Room ID is empty. Cannot send offer.");
            yield break;
        }

        var payload = new OfferPayload
        {
            sdp = offer.sdp,
            roomId = roomId,
            peerId = EnsurePeerId()
        };

        string jsonData = JsonUtility.ToJson(payload);
        
        using (UnityEngine.Networking.UnityWebRequest request = new UnityEngine.Networking.UnityWebRequest(signalingServerUrl + "/offer", "POST"))
        {
            byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(jsonData);
            request.uploadHandler = new UnityEngine.Networking.UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new UnityEngine.Networking.DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            
            yield return request.SendWebRequest();
            
            if (request.result != UnityEngine.Networking.UnityWebRequest.Result.Success)
            {
                Debug.LogError($"Failed to send offer: {request.error}");
                OnError?.Invoke($"Signaling error: {request.error}");
            }
            else
            {
                Debug.Log("Offer sent successfully");
                // Server should respond with the answer, which you'll handle in ReceiveAnswer()
            }
        }
    }

    /// <summary>
    /// Send offer using WebSocket (for real-time communication)
    /// Note: You'll need a WebSocket library like NativeWebSocket or similar
    /// </summary>
    private void SendOfferWebSocket(RTCSessionDescription offer)
    {
        // Example WebSocket implementation (you'll need to install a WebSocket package)
        // For now, this is a placeholder
        Debug.LogWarning("WebSocket signaling not implemented. Please use HTTP or install a WebSocket library.");
        
        // If you have a WebSocket library, it would look like:
        // string message = $"{{\"type\":\"offer\",\"sdp\":\"{offer.sdp}\",\"roomId\":\"{roomId}\"}}";
        // webSocket.SendText(message);
    }
#endif

    /// <summary>
    /// Send ICE candidate to signaling server
    /// ICE candidates are network addresses that help devices find each other
    /// </summary>
#if UNITY_WEBRTC
    private void SendIceCandidate(RTCIceCandidate candidate)
    {
        Debug.Log($"Sending ICE candidate to signaling server");
        
        if (signalingType == SignalingType.HTTP)
        {
            StartCoroutine(SendIceCandidateHTTP(candidate));
        }
        else if (signalingType == SignalingType.WebSocket)
        {
            SendIceCandidateWebSocket(candidate);
        }
    }

    /// <summary>
    /// Send ICE candidate using HTTP POST
    /// </summary>
    private IEnumerator SendIceCandidateHTTP(RTCIceCandidate candidate)
    {
        if (string.IsNullOrEmpty(roomId))
        {
            Debug.LogWarning("Room ID is empty. Skipping ICE candidate send.");
            yield break;
        }

        var payload = new IceCandidatePayload
        {
            candidate = candidate.Candidate,
            sdpMid = candidate.SdpMid,
            sdpMLineIndex = candidate.SdpMLineIndex ?? 0,
            roomId = roomId,
            peerId = EnsurePeerId()
        };

        string jsonData = JsonUtility.ToJson(payload);
        
        using (UnityEngine.Networking.UnityWebRequest request = new UnityEngine.Networking.UnityWebRequest(signalingServerUrl + "/ice-candidate", "POST"))
        {
            byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(jsonData);
            request.uploadHandler = new UnityEngine.Networking.UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new UnityEngine.Networking.DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            
            yield return request.SendWebRequest();
            
            if (request.result != UnityEngine.Networking.UnityWebRequest.Result.Success)
            {
                Debug.LogError($"Failed to send ICE candidate: {request.error}");
            }
            else
            {
                Debug.Log("ICE candidate sent successfully");
            }
        }
    }

    /// <summary>
    /// Send ICE candidate using WebSocket
    /// </summary>
    private void SendIceCandidateWebSocket(RTCIceCandidate candidate)
    {
        // Example WebSocket implementation
        Debug.LogWarning("WebSocket signaling not implemented. Please use HTTP or install a WebSocket library.");
        
        // If you have a WebSocket library, it would look like:
        // string message = $"{{\"type\":\"ice-candidate\",\"candidate\":\"{candidate.Candidate}\",\"sdpMid\":\"{candidate.SdpMid}\",\"sdpMLineIndex\":{candidate.SdpMLineIndex},\"roomId\":\"{roomId}\"}}";
        // webSocket.SendText(message);
    }
#endif

    /// <summary>
    /// Poll the server for incoming messages (answer, ICE candidates from remote peer)
    /// Call this periodically or set up a WebSocket connection for real-time updates
    /// </summary>
    public IEnumerator PollForMessages()
    {
        while (isStreaming)
        {
            yield return new WaitForSeconds(0.5f); // Poll every 0.5 seconds
            
            using (UnityEngine.Networking.UnityWebRequest request = UnityEngine.Networking.UnityWebRequest.Get(signalingServerUrl + $"/messages?roomId={roomId}&peerId={peerId}"))
            {
                yield return request.SendWebRequest();
                
                if (request.result == UnityEngine.Networking.UnityWebRequest.Result.Success)
                {
                    string response = request.downloadHandler.text;
                    if (!string.IsNullOrEmpty(response) && response != "[]")
                    {
                        ProcessSignalingMessage(response);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Process incoming signaling messages (answer or ICE candidates)
    /// </summary>
    private void ProcessSignalingMessage(string jsonMessage)
    {
        try
        {
            // Parse JSON array or single message
            SignalingMessage[] messages = null;
            
            // Try to parse as array first
            if (jsonMessage.TrimStart().StartsWith("["))
            {
                messages = JsonConvert.DeserializeObject<SignalingMessage[]>(jsonMessage);
            }
            else
            {
                // Single message
                SignalingMessage msg = JsonConvert.DeserializeObject<SignalingMessage>(jsonMessage);
                messages = new SignalingMessage[] { msg };
            }
            
            if (messages == null) return;
            
            foreach (var msg in messages)
            {
                if (msg.type == "answer")
                {
                    Debug.Log("Received answer from remote peer");
                    SetRemoteDescription(msg.sdp, "answer");
                }
                else if (msg.type == "ice-candidate")
                {
                    Debug.Log("Received ICE candidate from remote peer");
                    AddIceCandidate(msg.candidate, msg.sdpMid, msg.sdpMLineIndex);
                }
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Error parsing signaling message: {e.Message}");
        }
    }

    /// <summary>
    /// Cleanup camera resources
    /// </summary>
    private void CleanupCamera()
    {
        if (webCamTexture != null)
        {
            if (webCamTexture.isPlaying)
            {
                webCamTexture.Stop();
            }
            webCamTexture = null;
        }

        if (previewImage != null)
        {
            previewImage.texture = null;
            previewImage.enabled = false;
        }
    }

    /// <summary>
    /// Get current camera texture (for preview or other uses)
    /// </summary>
    public Texture GetCameraTexture()
    {
        return webCamTexture;
    }

    /// <summary>
    /// Check if streaming is active
    /// </summary>
    public bool IsStreaming()
    {
        return isStreaming;
    }

    /// <summary>
    /// Get available camera devices
    /// </summary>
    public static string[] GetAvailableCameras()
    {
        string[] devices = new string[WebCamTexture.devices.Length];
        for (int i = 0; i < WebCamTexture.devices.Length; i++)
        {
            devices[i] = WebCamTexture.devices[i].name;
        }
        return devices;
    }

    /// <summary>
    /// Switch to a different camera device
    /// </summary>
    public IEnumerator SwitchCamera(int newDeviceIndex)
    {
        StopStreaming();
        CleanupCamera();
        
        deviceIndex = newDeviceIndex;
        yield return StartCoroutine(InitializeCamera());
        
        if (autoStart)
        {
            yield return StartCoroutine(InitializeWebRTC());
            StartStreaming();
        }
    }

    private string EnsurePeerId()
    {
        if (string.IsNullOrEmpty(peerId))
        {
            peerId = "unity-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            Debug.Log($"Generated peerId: {peerId}");
        }
        return peerId;
    }
}

