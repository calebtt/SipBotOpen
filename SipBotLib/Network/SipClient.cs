using Serilog;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace SipBot;

/// <summary>
/// Enhanced SIP client with improved error handling, monitoring, and resource management.
/// </summary>
public class SipClient : IDisposable
{
    private const int DefaultRegistrationExpirySeconds = 60;
    private const int MaxReconnectionAttempts = 5;
    private const int ReconnectionDelayMs = 2000;
    private const int HealthCheckIntervalMs = 30_000;
    private const int BlindTransferTimeoutSeconds = 10;

    private readonly string _sipUsername;
    private readonly string _sipPassword;
    private readonly string _sipServer;
    private readonly string _sipFromName;
    private readonly int _registrationExpirySeconds;
    private readonly bool _enableAutoReconnection;
    private readonly bool _enableHealthMonitoring;

    private SIPTransport _sipTransport;
    private SIPUserAgent _userAgent;
    private SIPServerUserAgent? _pendingIncomingCall;
    private SIPRegistrationUserAgent _registrationAgent;
    private VoIPMediaSession? _mediaSession = null;

    // Thread safety and state management
    private readonly object _lockObject = new object();
    private volatile bool _isDisposed = false;
    private volatile bool _isRegistered = false;
    private int _reconnectionAttempts = 0;
    private Timer? _healthCheckTimer;
    private Timer? _reconnectionTimer;

    private readonly Stopwatch _callDurationTimer = new();
    private DateTime _lastRegistrationAttempt = DateTime.MinValue;
    private DateTime _lastSuccessfulRegistration = DateTime.MinValue;

    // Call-specific state (thread-safe access)
    private string? _remotePhoneNumber;
    private string? _trunkDID;

    // Events
    public event Action<SipClient>? CallAnswer;
    public event Action<SipClient>? CallEnded;
    public event Action<SipClient, string>? StatusMessage;
    public event Action<SipClient>? RemotePutOnHold;
    public event Action<SipClient>? RemoteTookOffHold;
    public event Action<SipClient, Exception>? ErrorOccurred;
    public event Action<SipClient>? RegistrationStatusChanged;
    public event Action<SipClient, TimeSpan>? CallDurationUpdated;

    // Transfer events
    public event Action<SipClient, string>? TransferInitiated;
    public event Action<SipClient>? TransferSucceeded;
    public event Action<SipClient, string>? TransferFailed;

    // Properties
    public bool IsRegistered => _isRegistered;
    public bool IsCallActive => _userAgent?.IsCallActive ?? false;
    public TimeSpan CurrentCallDuration => _callDurationTimer.IsRunning ? _callDurationTimer.Elapsed : TimeSpan.Zero;
    public DateTime LastSuccessfulRegistration => _lastSuccessfulRegistration;

    public string? RemotePhoneNumber
    {
        get
        {
            lock (_lockObject)
            {
                return _remotePhoneNumber;
            }
        }
    }

    public string? TrunkDID
    {
        get
        {
            lock (_lockObject)
            {
                return _trunkDID;
            }
        }
    }

    public SipClient(
        SIPTransport sipTransport,
        SipConfig sipSettings,
        int registrationExpirySeconds = DefaultRegistrationExpirySeconds,
        bool enableAutoReconnection = true,
        bool enableHealthMonitoring = true)
    {
        _sipTransport = sipTransport ?? throw new ArgumentNullException(nameof(sipTransport));
        _sipUsername = sipSettings?.Username ?? throw new ArgumentNullException(nameof(sipSettings));
        _sipPassword = sipSettings.Password;
        _sipServer = sipSettings.Server;
        _sipFromName = sipSettings.FromName;
        _registrationExpirySeconds = Math.Max(30, registrationExpirySeconds); // Minimum 30 seconds
        _enableAutoReconnection = enableAutoReconnection;
        _enableHealthMonitoring = enableHealthMonitoring;
        _trunkDID = sipSettings.BulkVs?.FromNumber ?? _sipUsername;

        InitializeTransport();
        InitializeRegistrationAgent();
        InitializeUserAgent();

        if (_enableHealthMonitoring)
        {
            StartHealthMonitoring();
        }

        Log.Information($"SIP Client initialized for {_sipUsername}@{_sipServer}");
    }

    private void InitializeTransport()
    {
        try
        {
            _sipTransport.SIPRequestInTraceEvent += (localSIPEndPoint, endPoint, request) =>
                Log.Debug($"SIP Request Received: {request.Method} from {endPoint}");

            _sipTransport.SIPResponseOutTraceEvent += (localSIPEndPoint, endPoint, response) =>
                Log.Debug($"SIP Response Sent: {response.Status} to {endPoint}");

            StunHelper.SetupStun();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize SIP transport");
            ErrorOccurred?.Invoke(this, ex);
            throw;
        }
    }

    private void InitializeRegistrationAgent()
    {
        try
        {
            _registrationAgent = new SIPRegistrationUserAgent(
                _sipTransport,
                _sipUsername,
                _sipPassword,
                _sipServer,
                _registrationExpirySeconds
            );

            _registrationAgent.RegistrationSuccessful += OnRegistrationSuccessful;
            _registrationAgent.RegistrationFailed += OnRegistrationFailed;
            _registrationAgent.RegistrationTemporaryFailure += OnRegistrationTemporaryFailure;
            _registrationAgent.RegistrationRemoved += OnRegistrationRemoved;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize registration agent");
            ErrorOccurred?.Invoke(this, ex);
            throw;
        }
    }

    private void InitializeUserAgent()
    {
        try
        {
            _userAgent = CreateNewUserAgent(_sipTransport);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize user agent");
            ErrorOccurred?.Invoke(this, ex);
            throw;
        }
    }

    private SIPUserAgent CreateNewUserAgent(SIPTransport sipTransport)
    {
        var userAgent = new SIPUserAgent(sipTransport, null);
        userAgent.ClientCallTrying += CallTrying;
        userAgent.ClientCallRinging += CallRinging;
        userAgent.ClientCallAnswered += CallAnswered;
        userAgent.ClientCallFailed += CallFailed;
        userAgent.OnCallHungup += CallFinished;
        userAgent.ServerCallCancelled += IncomingCallCancelled;
        userAgent.OnIncomingCall += OnIncomingCall;
        return userAgent;
    }

    public void StartRegistration()
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(SipClient));
        }

        lock (_lockObject)
        {
            try
            {
                _lastRegistrationAttempt = DateTime.UtcNow;
                _registrationAgent.Start();
                StatusMessage?.Invoke(this, $"Registration attempt for {_sipUsername}@{_sipServer} started.");
                Log.Information($"Starting SIP registration for {_sipUsername}@{_sipServer}");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to start registration");
                ErrorOccurred?.Invoke(this, ex);
                throw;
            }
        }
    }

    public void Accept(SIPRequest sipRequest)
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(SipClient));
        }

        try
        {
            _pendingIncomingCall = _userAgent.AcceptCall(sipRequest);
            Log.Information($"Accepted incoming call from {sipRequest.Header.From}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to accept incoming call");
            ErrorOccurred?.Invoke(this, ex);
            throw;
        }
    }

    public async Task<bool> Answer(IAudioSink audioSink, IAudioSource audioSource)
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(SipClient));
        }

        if (_pendingIncomingCall == null)
        {
            StatusMessage?.Invoke(this, "There was no pending call available to answer.");
            return false;
        }

        try
        {
            var sipRequest = _pendingIncomingCall.ClientTransaction.TransactionRequest;

            bool hasAudio = true;
            bool hasVideo = false;

            if (sipRequest.Body != null)
            {
                SDP offerSDP = SDP.ParseSDPDescription(sipRequest.Body);
                hasAudio = offerSDP.Media.Any(x => x.Media == SDPMediaTypesEnum.audio && x.MediaStreamStatus != MediaStreamStatusEnum.Inactive);
                hasVideo = offerSDP.Media.Any(x => x.Media == SDPMediaTypesEnum.video && x.MediaStreamStatus != MediaStreamStatusEnum.Inactive);
            }

            _mediaSession = CreateMediaSession(CreateMediaEndPoints(audioSink, audioSource));

            bool result = await _userAgent.Answer(_pendingIncomingCall, _mediaSession);
            _pendingIncomingCall = null;

            if (result)
            {
                _callDurationTimer.Restart();
                CallAnswer?.Invoke(this);
                Log.Information("Call successfully answered");
            }
            else
            {
                Log.Warning("Failed to answer call");
            }

            return result;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Exception occurred while answering call");
            ErrorOccurred?.Invoke(this, ex);
            return false;
        }
    }

    public void Hangup()
    {
        if (_isDisposed)
        {
            return;
        }

        try
        {
            if (_userAgent.IsCallActive)
            {
                _userAgent.Hangup();
                CallFinished(null);
                CallEnded?.Invoke(this);
                Log.Information("Call hung up");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Exception occurred while hanging up");
            ErrorOccurred?.Invoke(this, ex);
        }
    }

    /// <summary>
    /// Performs a blind transfer to a full SIP URI (e.g., "sip:100@pbx.example.com").
    /// The original call leg is hung up on success.
    /// </summary>
    /// <param name="sipUri">The target SIP URI.</param>
    /// <param name="timeout">Optional timeout for the transfer (default 10s).</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>True if transfer succeeded, false otherwise.</returns>
    public async Task<bool> BlindTransferAsync(
        string sipUri,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(SipClient));
        }

        if (!IsCallActive)
        {
            var msg = "Cannot transfer: No active call.";
            StatusMessage?.Invoke(this, msg);
            TransferFailed?.Invoke(this, msg);
            Log.Warning(msg);
            return false;
        }

        try
        {
            if (!SIPURI.TryParse(sipUri, out var destination))
            {
                var msg = $"Invalid SIP URI: {sipUri}";
                StatusMessage?.Invoke(this, msg);
                TransferFailed?.Invoke(this, msg);
                Log.Warning(msg);
                return false;
            }

            var transferTimeout = timeout ?? TimeSpan.FromSeconds(BlindTransferTimeoutSeconds);
            TransferInitiated?.Invoke(this, sipUri);
            StatusMessage?.Invoke(this, $"Initiating blind transfer to {sipUri}...");
            Log.Information($"Blind transfer initiated to {sipUri}");

            var result = await _userAgent.BlindTransfer(destination, transferTimeout, cancellationToken);

            if (result)
            {
                TransferSucceeded?.Invoke(this);
                StatusMessage?.Invoke(this, $"Blind transfer to {sipUri} succeeded.");
                Log.Information($"Blind transfer to {sipUri} succeeded");
            }
            else
            {
                var msg = $"Blind transfer to {sipUri} failed (timeout or rejection).";
                TransferFailed?.Invoke(this, msg);
                StatusMessage?.Invoke(this, msg);
                Log.Warning(msg);
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            var msg = "Blind transfer cancelled.";
            TransferFailed?.Invoke(this, msg);
            Log.Warning(msg);
            return false;
        }
        catch (Exception ex)
        {
            var msg = $"Exception during blind transfer to {sipUri}: {ex.Message}";
            StatusMessage?.Invoke(this, msg);
            TransferFailed?.Invoke(this, msg);
            ErrorOccurred?.Invoke(this, ex);
            Log.Error(ex, msg);
            return false;
        }
    }

    /// <summary>
    /// Performs a blind transfer to an internal extension (e.g., "100").
    /// Constructs URI as sip:{extension}@{_sipServer}.
    /// </summary>
    /// <param name="extension">The target extension.</param>
    /// <param name="timeout">Optional timeout for the transfer (default 10s).</param>
    public Task<bool> BlindTransferToExtensionAsync(string extension, TimeSpan? timeout = null) =>
        BlindTransferAsync($"sip:{extension}@{_sipServer}", timeout ?? TimeSpan.FromSeconds(BlindTransferTimeoutSeconds));

    public void Shutdown()
    {
        if (_isDisposed)
        {
            return;
        }

        try
        {
            Log.Information("Shutting down SIP client");

            Hangup();

            _mediaSession?.Close("Shutdown");
            _mediaSession?.Dispose();
            _mediaSession = null;

            _registrationAgent?.Stop();

            StopHealthMonitoring();
            StopReconnectionTimer();

            _sipTransport.Shutdown();

            Log.Information("SIP client shutdown completed");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Exception occurred during shutdown");
            ErrorOccurred?.Invoke(this, ex);
        }
    }

    private MediaEndPoints CreateMediaEndPoints(IAudioSink audioSink, IAudioSource audioSource)
    {
        var mediaEndPoints = new MediaEndPoints
        {
            AudioSink = audioSink,
            AudioSource = audioSource,
            VideoSink = null,
            VideoSource = null
        };
        return mediaEndPoints;
    }

    private VoIPMediaSession CreateMediaSession(MediaEndPoints mediaEndPoints)
    {
        var voipMediaSession = new VoIPMediaSession(mediaEndPoints);
        Log.Information($"[{GetType().Name}] Created with AudioSink={mediaEndPoints.AudioSink.GetType().Name}, AcceptRtpFromAny={voipMediaSession.AcceptRtpFromAny}");
        return voipMediaSession;
    }

    // Event handlers
    private void OnRegistrationSuccessful(SIPURI uri, SIPResponse resp)
    {
        lock (_lockObject)
        {
            _isRegistered = true;
            _reconnectionAttempts = 0;
            _lastSuccessfulRegistration = DateTime.UtcNow;
        }

        StatusMessage?.Invoke(this, $"Registration successful for {uri}. Expires: {resp.Header.Expires}");
        RegistrationStatusChanged?.Invoke(this);
        Log.Debug($"SIP registration successful for {uri}");
    }

    private void OnRegistrationFailed(SIPURI uri, SIPResponse resp, string error)
    {
        lock (_lockObject)
        {
            _isRegistered = false;
        }

        StatusMessage?.Invoke(this, $"Registration failed for {uri}: {error}");
        RegistrationStatusChanged?.Invoke(this);
        Log.Warning($"SIP registration failed for {uri}: {error}");

        if (_enableAutoReconnection)
        {
            ScheduleReconnection();
        }
    }

    private void OnRegistrationTemporaryFailure(SIPURI uri, SIPResponse resp, string error)
    {
        StatusMessage?.Invoke(this, $"Registration temporary failure for {uri}: {error}");
        Log.Warning($"SIP registration temporary failure for {uri}: {error}");

        if (_enableAutoReconnection)
        {
            ScheduleReconnection();
        }
    }

    private void OnRegistrationRemoved(SIPURI uri, SIPResponse resp)
    {
        lock (_lockObject)
        {
            _isRegistered = false;
        }

        StatusMessage?.Invoke(this, $"Registration removed for {uri}");
        RegistrationStatusChanged?.Invoke(this);
        Log.Warning($"SIP registration removed for {uri}");

        if (_enableAutoReconnection)
        {
            ScheduleReconnection();
        }
    }

    private void OnIncomingCall(SIPUserAgent userAgent, SIPRequest sipRequest)
    {
        try
        {
            // Extract remote phone number (caller's number from From header)
            var remoteUri = sipRequest.Header.From?.FromURI;
            var remotePhoneNumber = remoteUri?.User ?? "unknown";

            // Extract internal extension/DID candidate: Prioritize Uri.User (Request-URI), fallback to To.URI.User
            var requestUri = sipRequest.URI;
            var internalExt = requestUri?.User ?? sipRequest.Header.To?.ToURI?.User ?? _sipUsername;  // Fallback to registered username (extension)

            // Map to external trunk DID if found (from config.bulkVs.fromNumber)
            var trunkDid = _trunkDID;  // Already set in ctor; no need for runtime mapping since per-extension

            // Debug log (remove in prod)
            Log.Debug("Internal ext: {InternalExt}, Mapped DID: {TrunkDid}, Request-URI: {RequestUri}",
                      internalExt, trunkDid, requestUri?.ToString() ?? "null");

            lock (_lockObject)
            {
                _remotePhoneNumber = remotePhoneNumber;
            }

            // Log with extension (internal username) and trunk DID (external from config)
            Log.Information("Incoming call from {RemotePhoneNumber} on extension {Extension} with (alleged) trunk DID {TrunkDid}",
                            remotePhoneNumber, _sipUsername, trunkDid);
            StatusMessage?.Invoke(this, $"Incoming call from {remotePhoneNumber} on extension {_sipUsername} with trunk DID {trunkDid}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Exception handling incoming call");
            ErrorOccurred?.Invoke(this, ex);
        }
    }

    private void CallTrying(ISIPClientUserAgent uac, SIPResponse sipResponse)
    {
        StatusMessage?.Invoke(this, "Call trying: " + sipResponse.StatusCode + " " + sipResponse.ReasonPhrase + ".");
    }

    private void CallRinging(ISIPClientUserAgent uac, SIPResponse sipResponse)
    {
        StatusMessage?.Invoke(this, "Call ringing: " + sipResponse.StatusCode + " " + sipResponse.ReasonPhrase + ".");
    }

    private void CallFailed(ISIPClientUserAgent uac, string errorMessage, SIPResponse? failureResponse)
    {
        StatusMessage?.Invoke(this, "Call failed: " + errorMessage + ".");
        CallFinished(null);
        Log.Warning($"Call failed: {errorMessage}");
    }

    private void CallAnswered(ISIPClientUserAgent uac, SIPResponse sipResponse)
    {
        StatusMessage?.Invoke(this, "Call answered: " + sipResponse.StatusCode + " " + sipResponse.ReasonPhrase + ".");
        CallAnswer?.Invoke(this);
    }

    private void CallFinished(SIPDialogue? dialogue)
    {
        if (_callDurationTimer.IsRunning)
        {
            _callDurationTimer.Stop();
            var duration = _callDurationTimer.Elapsed;
            CallDurationUpdated?.Invoke(this, duration);
            Log.Information($"Call ended. Duration: {duration:mm\\:ss}");
        }

        _mediaSession?.Close("Call Finished");
        _mediaSession?.Dispose();
        _mediaSession = null;

        lock (_lockObject)
        {
            _remotePhoneNumber = null;
        }

        _pendingIncomingCall = null;
        CallEnded?.Invoke(this);
    }

    private void IncomingCallCancelled(ISIPServerUserAgent uas, SIPRequest cancelRequest)
    {
        CallFinished(null);
    }

    // Health monitoring and reconnection
    private void StartHealthMonitoring()
    {
        _healthCheckTimer = new Timer(PerformHealthCheck, null, HealthCheckIntervalMs, HealthCheckIntervalMs);
        Log.Debug("Health monitoring started");
    }

    private void StopHealthMonitoring()
    {
        _healthCheckTimer?.Dispose();
        _healthCheckTimer = null;
    }

    private void PerformHealthCheck(object? state)
    {
        try
        {
            var timeSinceLastRegistration = DateTime.UtcNow - _lastSuccessfulRegistration;

            // If we haven't registered successfully in the last 2 minutes, try to re-register
            if (timeSinceLastRegistration.TotalMinutes > 2 && !_isRegistered)
            {
                Log.Warning("Health check: No successful registration in 2+ minutes, attempting re-registration");
                StartRegistration();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Exception during health check");
            ErrorOccurred?.Invoke(this, ex);
        }
    }

    private void ScheduleReconnection()
    {
        lock (_lockObject)
        {
            if (_reconnectionAttempts >= MaxReconnectionAttempts)
            {
                Log.Error($"Maximum reconnection attempts ({MaxReconnectionAttempts}) reached");
                return;
            }

            _reconnectionAttempts++;
            var delay = ReconnectionDelayMs * _reconnectionAttempts; // Exponential backoff

            _reconnectionTimer?.Dispose();
            _reconnectionTimer = new Timer(AttemptReconnection, null, delay, Timeout.Infinite);

            Log.Information($"Scheduling reconnection attempt {_reconnectionAttempts} in {delay}ms");
        }
    }

    private void StopReconnectionTimer()
    {
        _reconnectionTimer?.Dispose();
        _reconnectionTimer = null;
    }

    private void AttemptReconnection(object? state)
    {
        try
        {
            Log.Information($"Attempting reconnection (attempt {_reconnectionAttempts})");
            StartRegistration();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Exception during reconnection attempt");
            ErrorOccurred?.Invoke(this, ex);
        }
    }

    // IDisposable implementation
    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        Shutdown();

        _healthCheckTimer?.Dispose();
        _reconnectionTimer?.Dispose();

        Log.Information("SIP Client disposed");
    }
}