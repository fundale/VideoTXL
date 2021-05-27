﻿
using System;
using UdonSharp;
using UnityEngine;
using VRC.SDK3.Components.Video;
using VRC.SDK3.Video.Components.AVPro;
using VRC.SDK3.Video.Components.Base;
using VRC.SDKBase;
using VRC.Udon;
using VRC.Udon.Common;

namespace VideoTXL
{
    [AddComponentMenu("VideoTXL/Sync Player")]
    public class SyncPlayer : UdonSharpBehaviour
    {
        public VideoPlayerProxy dataProxy;

        [Tooltip("Optional component to control and synchronize player video screens and materials")]
        public ScreenManager screenManager;
        [Tooltip("Optional component to control and synchronize player audio sources")]
        public VolumeController audioManager;
        //[Tooltip("Optional component to start or stop player based on common trigger events")]
        //public TriggerManager triggerManager;
        [Tooltip("Optional component to control access to player controls based on player type or whitelist")]
        public AccessControl accessControl;

        [Tooltip("AVPro video player component")]
        public VRCAVProVideoPlayer avProVideo;

        [Tooltip("Optional default URL to play on world load")]
        public VRCUrl defaultUrl;

        [Tooltip("Whether player controls are locked to master and instance owner by default")]
        public bool defaultLocked = false;

        public bool retryOnError = true;

        [Tooltip("Write out video player events to VRChat log")]
        public bool debugLogging = true;

        float retryTimeout = 6;
        float syncFrequency = 5;
        float syncThreshold = 1;

        [UdonSynced]
        VRCUrl _syncUrl;

        [UdonSynced]
        int _syncVideoNumber;
        int _loadedVideoNumber;

        [UdonSynced, NonSerialized]
        public bool _syncOwnerPlaying;

        [UdonSynced]
        float _syncVideoStartNetworkTime;

        [UdonSynced]
        bool _syncLocked = true;

        [NonSerialized]
        public int localPlayerState = PLAYER_STATE_STOPPED;
        [NonSerialized]
        public VideoError localLastErrorCode;

        BaseVRCVideoPlayer _currentPlayer;

        float _lastVideoPosition = 0;
        float _videoTargetTime = 0;

        bool _waitForSync;
        float _lastSyncTime;
        float _playStartTime = 0;

        float _pendingLoadTime = 0;
        float _pendingPlayTime = 0;
        VRCUrl _pendingPlayUrl;

        // Realtime state

        [NonSerialized]
        public bool seekableSource;
        [NonSerialized]
        public float trackDuration;
        [NonSerialized]
        public float trackPosition;
        [NonSerialized]
        public bool locked;
        [NonSerialized]
        public string currentUrl;
        [NonSerialized]
        public string lastUrl;

        // Constants

        const int PLAYER_STATE_STOPPED = 0;
        const int PLAYER_STATE_LOADING = 1;
        const int PLAYER_STATE_PLAYING = 2;
        const int PLAYER_STATE_ERROR = 3;

        const int SCREEN_MODE_NORMAL = 0;
        const int SCREEN_MODE_LOGO = 1;
        const int SCREEN_MODE_LOADING = 2;
        const int SCREEN_MODE_ERROR = 3;

        const int SCREEN_SOURCE_UNITY = 0;
        const int SCREEN_SOURCE_AVPRO = 1;

        void Start()
        {
            dataProxy._Init();

            avProVideo.Loop = false;
            avProVideo.Stop();
            _currentPlayer = avProVideo;

            _UpdatePlayerState(PLAYER_STATE_STOPPED);

            if (Networking.IsOwner(gameObject))
            {
                _syncLocked = defaultLocked;
                locked = _syncLocked;
                RequestSerialization();
            }

            _StartExtra();


            if (Networking.IsOwner(gameObject))
                _PlayVideo(defaultUrl);
        }

        public void _TriggerPlay()
        {
            DebugLog("Trigger play");
            if (localPlayerState == PLAYER_STATE_PLAYING || localPlayerState == PLAYER_STATE_LOADING)
                return;

            _PlayVideo(_syncUrl);
        }

        public void _TriggerStop()
        {
            DebugLog("Trigger stop");
            if (_syncLocked && !_CanTakeControl())
                return;
            if (!Networking.IsOwner(gameObject))
                Networking.SetOwner(Networking.LocalPlayer, gameObject);

            _StopVideo();
        }

        public void _TriggerLock()
        {
            if (!_CanTakeControl())
                return;
            if (!Networking.IsOwner(gameObject))
                Networking.SetOwner(Networking.LocalPlayer, gameObject);

            _syncLocked = !_syncLocked;
            locked = _syncLocked;
            RequestSerialization();
        }

        public void _Resync()
        {
            _ForceResync();
        }

        public void _ChangeUrl(VRCUrl url)
        {
            if (_syncLocked && !_CanTakeControl())
                return;

            _PlayVideo(url);
        }

        public void _SetTargetTime(float time)
        {
            if (_syncLocked && !_CanTakeControl())
                return;
            if (!Networking.IsOwner(gameObject))
                Networking.SetOwner(Networking.LocalPlayer, gameObject);

            _syncVideoStartNetworkTime = (float)Networking.GetServerTimeInSeconds() - time;
            SyncVideo();
            RequestSerialization();
        }

        void _PlayVideo(VRCUrl url)
        {
            _pendingPlayTime = 0;
            DebugLog("Play video " + url);
            bool isOwner = Networking.IsOwner(gameObject);
            if (!isOwner && !_CanTakeControl())
                return;

            if (!Utilities.IsValid(url))
                return;

            string urlStr = url.Get();
            if (urlStr == null || urlStr == "")
                return;

            if (!isOwner)
                Networking.SetOwner(Networking.LocalPlayer, gameObject);

            _syncUrl = url;
            _syncVideoNumber += isOwner ? 1 : 2;
            _loadedVideoNumber = _syncVideoNumber;
            _syncOwnerPlaying = false;

            _syncVideoStartNetworkTime = float.MaxValue;
            RequestSerialization();

            _videoTargetTime = _ParseTimeFromUrl(urlStr);
            _UpdateLastUrl();

            _StartVideoLoad();
        }

        // Time parsing code adapted from USharpVideo project by Merlin
        float _ParseTimeFromUrl(string urlStr)
        {
            // Attempt to parse out a start time from YouTube links with t= or start=
            if (!urlStr.Contains("youtube.com/watch") && !urlStr.Contains("youtu.be/"))
                return 0;

            int tIndex = urlStr.IndexOf("?t=");
            if (tIndex == -1)
                tIndex = urlStr.IndexOf("&t=");
            if (tIndex == -1)
                tIndex = urlStr.IndexOf("?start=");
            if (tIndex == -1)
                tIndex = urlStr.IndexOf("&start=");
            if (tIndex == -1)
                return 0;

            char[] urlArr = urlStr.ToCharArray();
            int numIdx = urlStr.IndexOf('=', tIndex) + 1;

            string intStr = "";
            while (numIdx < urlArr.Length)
            {
                char currentChar = urlArr[numIdx];
                if (!char.IsNumber(currentChar))
                    break;

                intStr += currentChar;
                ++numIdx;
            }

            if (intStr.Length == 0)
                return 0;

            int secondsCount = 0;
            if (!int.TryParse(intStr, out secondsCount))
                return 0;

            return secondsCount;
        }

        void _StartVideoLoadDelay(float delay)
        {
            _pendingLoadTime = Time.time + delay;
        }

        void _StartVideoLoad()
        {
            _pendingLoadTime = 0;
            if (_syncUrl == null || _syncUrl.Get() == "")
                return;

            DebugLog("Start video load " + _syncUrl);
            _UpdatePlayerState(PLAYER_STATE_LOADING);
            //localPlayerState = PLAYER_STATE_LOADING;

            _UpdateScreenMaterial(SCREEN_MODE_LOADING);

            _currentPlayer.Stop();
#if !UNITY_EDITOR
            _currentPlayer.LoadURL(_syncUrl);
#endif
        }

        public void _StopVideo()
        {
            DebugLog("Stop video");
            if (seekableSource)
                _lastVideoPosition = _currentPlayer.GetTime();

            _currentPlayer.Stop();
            _syncVideoStartNetworkTime = 0;
            _syncOwnerPlaying = false;
            _syncUrl = VRCUrl.Empty;
            _videoTargetTime = 0;
            RequestSerialization();

            _pendingPlayTime = 0;
            _pendingLoadTime = 0;
            _playStartTime = 0;
            _UpdatePlayerState(PLAYER_STATE_STOPPED);
            //localPlayerState = PLAYER_STATE_STOPPED;

            _UpdateScreenMaterial(SCREEN_MODE_LOGO);
        }

        public override void OnVideoReady()
        {
            float duration = _currentPlayer.GetDuration();
            DebugLog("Video ready, duration: " + duration + ", position: " + _currentPlayer.GetTime());

            _AudioStart();

            // If a seekable video is loaded it should have a positive duration.  Otherwise we assume it's a non-seekable stream
            seekableSource = !float.IsInfinity(duration) && !float.IsNaN(duration) && duration > 1;

            // If player is owner: play video
            // If Player is remote:
            //   - If owner playing state is already synced, play video
            //   - Otherwise, wait until owner playing state is synced and play later in update()
            //   TODO: Streamline by always doing this in update instead?

            if (Networking.IsOwner(gameObject))
                _currentPlayer.Play();
            else
            {
                // TODO: Stream bypass owner
                if (_syncOwnerPlaying)
                    _currentPlayer.Play();
                else
                    _waitForSync = true;
            }
        }

        public override void OnVideoStart()
        {
            DebugLog("Video start");

            if (Networking.IsOwner(gameObject))
            {
                _syncVideoStartNetworkTime = (float)Networking.GetServerTimeInSeconds() - _videoTargetTime;
                _syncOwnerPlaying = true;
                RequestSerialization();

                //localPlayerState = PLAYER_STATE_PLAYING;
                _playStartTime = Time.time;
                _UpdatePlayerState(PLAYER_STATE_PLAYING);

                _currentPlayer.SetTime(_videoTargetTime);
                _UpdateScreenMaterial(SCREEN_MODE_NORMAL);
            }
            else
            {
                if (!_syncOwnerPlaying)
                {
                    // TODO: Owner bypass
                    _currentPlayer.Pause();
                    _waitForSync = true;
                }
                else
                {
                    //localPlayerState = PLAYER_STATE_PLAYING;
                    _playStartTime = Time.time;
                    _UpdatePlayerState(PLAYER_STATE_PLAYING);
                    _UpdateScreenMaterial(SCREEN_MODE_NORMAL);
                    SyncVideo();
                }
            }
        }

        public override void OnVideoEnd()
        {
            if (!seekableSource && Time.time - _playStartTime < 1)
            {
                Debug.Log("Video end encountered at start of stream, ignoring");
                return;
            }

            //localPlayerState = PLAYER_STATE_STOPPED;
            seekableSource = false;

            DebugLog("Video end");
            _lastVideoPosition = 0;

            dataProxy.seekableSource = false;
            _UpdatePlayerState(PLAYER_STATE_STOPPED);

            _UpdateScreenMaterial(SCREEN_MODE_LOGO);
            _AudioStop();

            if (Networking.IsOwner(gameObject))
            {
                _syncVideoStartNetworkTime = 0;
                _syncOwnerPlaying = false;
                RequestSerialization();
            }
        }

        public override void OnVideoError(VideoError videoError)
        {
            _currentPlayer.Stop();
            _videoTargetTime = 0;

            DebugLog("Video stream failed: " + _syncUrl);
            DebugLog("Error code: " + videoError);

            //localPlayerState = PLAYER_STATE_ERROR;
            //localLastErrorCode = videoError;
            _UpdatePlayerStateError(videoError);

            _UpdateScreenVideoError(videoError);
            _UpdateScreenMaterial(SCREEN_MODE_ERROR);
            _AudioStop();

            if (Networking.IsOwner(gameObject))
            {
                if (retryOnError)
                {
                    _currentPlayer.Stop();
                    _StartVideoLoadDelay(retryTimeout);
                }
                else
                {
                    _syncVideoStartNetworkTime = 0;
                    _syncOwnerPlaying = false;
                    RequestSerialization();
                }
            }
            else
            {
                _currentPlayer.Stop();
                _StartVideoLoadDelay(retryTimeout);
            }
        }

        public bool _CanTakeControl()
        {
            if (_hasAccessControl)
                return accessControl._LocalHasAccess();

            VRCPlayerApi player = Networking.LocalPlayer;
            return player.isMaster || player.isInstanceOwner || !_syncLocked;
        }

        public override void OnDeserialization()
        {
            if (Networking.IsOwner(gameObject))
                return;

            DebugLog($"Deserialize: video #{_syncVideoNumber}");

            locked = _syncLocked;

            if (localPlayerState == PLAYER_STATE_PLAYING && !_syncOwnerPlaying)
                SendCustomEventDelayedFrames("_StopVideo", 1);

            if (_syncVideoNumber == _loadedVideoNumber)
                return;

            // There was some code here to bypass load owner sync bla bla

            _loadedVideoNumber = _syncVideoNumber;
            _UpdateLastUrl();

            DebugLog("Starting video load from sync");

            _StartVideoLoad();
        }

        public override void OnPostSerialization(SerializationResult result)
        {
            if (!result.success)
            {
                DebugLog("Failed to sync");
                return;
            }
        }

        void Update()
        {
            bool isOwner = Networking.IsOwner(gameObject);
            float time = Time.time;

            if (_pendingPlayTime > 0 && time > _pendingPlayTime)
                _PlayVideo(_pendingPlayUrl);
            if (_pendingLoadTime > 0 && Time.time > _pendingLoadTime)
                _StartVideoLoad();

            if (seekableSource && localPlayerState == PLAYER_STATE_PLAYING)
            {
                trackDuration = _currentPlayer.GetDuration();
                trackPosition = _currentPlayer.GetTime();
            }

            // Video is playing: periodically sync with owner
            if (isOwner || !_waitForSync)
            {
                SyncVideoIfTime();
                return;
            }

            // Video is not playing, but still waiting for go-ahead from owner
            if (!_syncOwnerPlaying)
                return;

            // Got go-ahead from owner, start playing video
            _waitForSync = false;
            _currentPlayer.Play();

            _UpdatePlayerState(PLAYER_STATE_PLAYING);
            //localPlayerState = PLAYER_STATE_PLAYING;

            SyncVideo();
        }

        void SyncVideoIfTime()
        {
            if (Time.realtimeSinceStartup - _lastSyncTime > syncFrequency)
            {
                _lastSyncTime = Time.realtimeSinceStartup;
                SyncVideo();
            }
        }

        void SyncVideo()
        {
            if (seekableSource)
            {
                float offsetTime = Mathf.Clamp((float)Networking.GetServerTimeInSeconds() - _syncVideoStartNetworkTime, 0f, _currentPlayer.GetDuration());
                if (Mathf.Abs(_currentPlayer.GetTime() - offsetTime) > syncThreshold)
                    _currentPlayer.SetTime(offsetTime);
            }
        }

        public void _ForceResync()
        {
            bool isOwner = Networking.IsOwner(gameObject);
            if (isOwner)
            {
                if (seekableSource)
                {
                    float startTime = _videoTargetTime;
                    if (_currentPlayer.IsPlaying)
                        startTime = _currentPlayer.GetTime();

                    _StartVideoLoad();
                    _videoTargetTime = startTime;
                }
                return;
            }

            _currentPlayer.Stop();
            if (_syncOwnerPlaying)
                _StartVideoLoad();
        }

        void _UpdatePlayerState(int state)
        {
            localPlayerState = state;
            dataProxy.playerState = state;
            dataProxy._EmitStateUpdate();
        }

        void _UpdatePlayerStateError(VideoError error)
        {
            localPlayerState = PLAYER_STATE_ERROR;
            localLastErrorCode = error;
            dataProxy.playerState = PLAYER_STATE_ERROR;
            dataProxy.lastErrorCode = error;
            dataProxy._EmitStateUpdate();
        }

        void _UpdateLastUrl()
        {
            lastUrl = currentUrl;
            if (Utilities.IsValid(_syncUrl))
                currentUrl = _syncUrl.Get();
            else
                currentUrl = "";
        }

        // Extra

        bool _hasScreenManager = false;
        //bool _hasTriggerManager = false;
        bool _hasAudioManager = false;
        bool _hasAccessControl = false;

        void _StartExtra()
        {
            _hasScreenManager = Utilities.IsValid(screenManager);
            _hasAudioManager = Utilities.IsValid(audioManager);
            //_hasTriggerManager = Utilities.IsValid(triggerManager);
            _hasAccessControl = Utilities.IsValid(accessControl);

            _UpdateScreenSource(SCREEN_SOURCE_AVPRO);
            _UpdateScreenMaterial(SCREEN_MODE_LOGO);
        }

        void _UpdateScreenMaterial(int screenMode)
        {
            //if (_hasScreenManager)
            //    screenManager._UpdateScreenMaterial(screenMode);
        }

        void _UpdateScreenSource(int screenSource)
        {
            if (_hasScreenManager)
                screenManager._UpdateScreenSource(screenSource);
        }

        void _UpdateScreenVideoError(VideoError error)
        {
            if (_hasScreenManager)
                screenManager._UpdateVideoError(error);
        }

        void _AudioStart()
        {
            if (_hasAudioManager)
                audioManager._VideoStart();
        }

        void _AudioStop()
        {
            if (_hasAudioManager)
                audioManager._VideoStop();
        }

        // Debug

        void DebugLog(string message)
        {
            if (debugLogging)
                Debug.Log("[VideoTXL:SyncPlayer] " + message);
        }
    }
}
