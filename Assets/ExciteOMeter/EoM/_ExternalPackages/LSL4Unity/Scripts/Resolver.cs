using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.Linq;
using LSL;
using UnityEngine.Events;
using UnityEngine.EventSystems;

namespace Assets.LSL4Unity.Scripts
{
    /// <summary>
    /// Encapsulates the lookup logic for LSL streams with an event based approach
    /// your custom stream inlet implementations could be subscribed to the On
    /// </summary>
    public class Resolver : MonoBehaviour, IEventSystemHandler
    {
        // ✅ null 방지 (인스펙터 미설정/초기화 누락 방지)
        public List<LSLStreamInfoWrapper> knownStreams = new List<LSLStreamInfoWrapper>();

        public float forgetStreamAfter = 1.0f;

        private liblsl.ContinuousResolver resolver;
        private bool resolve = true;

        public StreamEvent onStreamFound = new StreamEvent();
        public StreamEvent onStreamLost = new StreamEvent();

        [Header("Server Integration")]
        public EyeAudioServer eyeAudioServer;
        private bool serverStartAttempted = false;

        // Use this for initialization
        void Start()
        {
            resolver = new liblsl.ContinuousResolver(forgetStreamAfter);
            
            // EyeAudioServer 할당 확인
            if (eyeAudioServer == null)
            {
                Debug.LogWarning("[Resolver] ⚠️ EyeAudioServer is not assigned! Trying to find it automatically...");
                eyeAudioServer = FindFirstObjectByType<EyeAudioServer>();
                
                if (eyeAudioServer == null)
                {
                    Debug.LogError("[Resolver] ❌ EyeAudioServer not found in scene! Please add EyeAudioServer component to a GameObject and assign it to Resolver.");
                }
                else
                {
                    Debug.Log("[Resolver] ✅ EyeAudioServer found automatically.");
                }
            }
            else
            {
                Debug.Log("[Resolver] ✅ EyeAudioServer is assigned.");
            }
            
            StartCoroutine(resolveContinuously());
        }

        public bool IsStreamAvailable(out LSLStreamInfoWrapper info, string streamName = "", string streamType = "", string hostName = "")
        {
            var result = knownStreams.Where(i =>
                (streamName == "" || i.Name.Equals(streamName)) &&
                (streamType == "" || i.Type.Equals(streamType)) &&
                // ✅ 버그 수정: hostName 비교가 i.Type.Equals(hostName)로 되어 있었음
                (hostName == "" || i.HostName.Equals(hostName))
            );

            if (result.Any())
            {
                info = result.First();
                return true;
            }
            else
            {
                info = null;
                return false;
            }
        }

        private IEnumerator resolveContinuously()
        {
            Debug.Log("[Resolver] Starting continuous stream resolution...");
            
            while (resolve)
            {
                try
                {
                    var results = resolver.results();

                    // 디버깅: 현재 발견된 stream 개수 출력
                    if (results != null && results.Length > 0)
                    {
                        Debug.Log($"[Resolver] Currently detecting {results.Length} stream(s)");
                    }

                    // lost stream 이벤트
                    foreach (var item in knownStreams)
                    {
                        if (!results.Any(r => r.name().Equals(item.Name)))
                        {
                            Debug.Log($"[Resolver] Stream lost: {item.Name} (type: {item.Type})");
                            if (onStreamLost.GetPersistentEventCount() > 0)
                                onStreamLost.Invoke(item);
                        }
                    }

                    // remove lost streams from cache
                    knownStreams.RemoveAll(s => !results.Any(r => r.name().Equals(s.Name)));

                    // add new found streams to the cache
                    foreach (var item in results)
                    {
                        if (!knownStreams.Any(s => s.Name == item.name() && s.Type == item.type()))
                        {
                            Debug.Log(string.Format("[Resolver] ✅ Found new Stream: Name='{0}', Type='{1}', Host='{2}', Channels={3}, SampleRate={4}",
                                item.name(), item.type(), item.hostname(), item.channel_count(), item.nominal_srate()));

                            var newStreamInfo = new LSLStreamInfoWrapper(item);
                            knownStreams.Add(newStreamInfo);

                            Debug.Log($"[Resolver] Total known streams: {knownStreams.Count}");
                            Debug.Log($"[Resolver] Stream found listeners: {onStreamFound.GetPersistentEventCount()}");

                            if (onStreamFound.GetPersistentEventCount() > 0)
                            {
                                Debug.Log($"[Resolver] Invoking onStreamFound event...");
                                onStreamFound.Invoke(newStreamInfo);
                            }
                            else
                            {
                                Debug.LogWarning($"[Resolver] ⚠️ No listeners registered for onStreamFound! Inlet components may not be connected.");
                            }
                        }
                    }

                    // HR과 RR stream이 모두 감지되면 서버 시작
                    CheckAndStartServer();
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"[Resolver] Error in resolveContinuously: {e.Message}");
                    Debug.LogException(e);
                }

                yield return new WaitForSecondsRealtime(0.1f);
            }

            yield return null;
        }

        private void CheckAndStartServer()
        {
            // 서버가 이미 시작되었으면 리턴
            if (serverStartAttempted)
            {
                Debug.Log("[Resolver] Server already started, skipping check.");
                return;
            }

            // EyeAudioServer가 설정되지 않았으면 경고
            if (eyeAudioServer == null)
            {
                Debug.LogWarning("[Resolver] ⚠️ EyeAudioServer is not assigned in Inspector! Please assign it in the Resolver component.");
                return;
            }

            // 정확한 이름으로 HeartRate와 RRinterval 확인
            bool foundHeartRate = false;
            bool foundRRinterval = false;
            LSLStreamInfoWrapper heartRateStream = null;
            LSLStreamInfoWrapper rrIntervalStream = null;

            Debug.Log($"[Resolver] Checking {knownStreams.Count} known streams for 'HeartRate' and 'RRinterval'...");

            foreach (var stream in knownStreams)
            {
                Debug.Log($"[Resolver] Checking stream: Name='{stream.Name}', Type='{stream.Type}'");
                
                // 정확한 이름 매칭: "HeartRate"
                if (stream.Name.Equals("HeartRate", System.StringComparison.OrdinalIgnoreCase))
                {
                    foundHeartRate = true;
                    heartRateStream = stream;
                    Debug.Log($"[Resolver] ✅ Found HeartRate stream: Name='{stream.Name}', Type='{stream.Type}'");
                }

                // 정확한 이름 매칭: "RRinterval"
                if (stream.Name.Equals("RRinterval", System.StringComparison.OrdinalIgnoreCase))
                {
                    foundRRinterval = true;
                    rrIntervalStream = stream;
                    Debug.Log($"[Resolver] ✅ Found RRinterval stream: Name='{stream.Name}', Type='{stream.Type}'");
                }
            }

            Debug.Log($"[Resolver] Stream status - HeartRate: {foundHeartRate}, RRinterval: {foundRRinterval}");

            // HeartRate와 RRinterval이 모두 감지되면 서버 시작하고 resolver 중지
            if (foundHeartRate && foundRRinterval)
            {
                Debug.Log("[Resolver] ✅ Both HeartRate and RRinterval streams detected!");
                Debug.Log("[Resolver] Stopping resolver and starting EyeAudioServer...");
                
                // Resolver 중지
                resolve = false;
                Debug.Log("[Resolver] Resolver stopped.");
                
                // 서버 시작
                serverStartAttempted = true;
                
                try
                {
                    Debug.Log("[Resolver] Calling EyeAudioServer.StartServer()...");
                    eyeAudioServer.StartServer();
                    Debug.Log("[Resolver] ✅ EyeAudioServer.StartServer() called successfully");
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"[Resolver] ❌ Error calling StartServer(): {e.Message}");
                    Debug.LogException(e);
                }
            }
            else
            {
                if (!foundHeartRate)
                    Debug.Log("[Resolver] ⏳ Waiting for HeartRate stream...");
                if (!foundRRinterval)
                    Debug.Log("[Resolver] ⏳ Waiting for RRinterval stream...");
            }
        }
    }

    [Serializable]
    public class LSLStreamInfoWrapper
    {
        public string Name;
        public string Type;

        private liblsl.StreamInfo item;
        private readonly string streamUID;

        private readonly int channelCount;
        private readonly string sessionId;
        private readonly string sourceID;
        private readonly double dataRate;
        private readonly string hostName;
        private readonly int streamVersion;

        public LSLStreamInfoWrapper(liblsl.StreamInfo item)
        {
            this.item = item;
            Name = item.name();
            Type = item.type();
            channelCount = item.channel_count();
            streamUID = item.uid();
            sessionId = item.session_id();
            sourceID = item.source_id();
            dataRate = item.nominal_srate();
            hostName = item.hostname();
            streamVersion = item.version();
        }

        public liblsl.StreamInfo Item
        {
            get { return item; }
        }

        public string StreamUID
        {
            get { return streamUID; }
        }

        public int ChannelCount
        {
            get { return channelCount; }
        }

        public string SessionId
        {
            get { return sessionId; }
        }

        public string SourceID
        {
            get { return sourceID; }
        }

        public string HostName
        {
            get { return hostName; }
        }

        public double DataRate
        {
            get { return dataRate; }
        }

        public int StreamVersion
        {
            get { return streamVersion; }
        }
    }

    [Serializable]
    public class StreamEvent : UnityEvent<LSLStreamInfoWrapper> { }
}
