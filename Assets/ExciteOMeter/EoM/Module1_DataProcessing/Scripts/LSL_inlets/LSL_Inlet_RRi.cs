using LSL;
using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;


namespace ExciteOMeter
{
    public class LSL_Inlet_RRi : InletFloatSamples
    {
        private string folder;
        private StreamWriter writer;
        private string openedSessionId = "";

        // RR 수신 로그를 세션당 1회만 찍기 위한 플래그
        private bool hasLoggedFirstSample = false;

        private double cachedTimeCorrSec = 0.0;
        private double nextCorrUpdateLocalSec = 0.0;
        private const double CorrUpdateIntervalSec = 1.0;

        void Awake()
        {
            folder = Path.Combine(Application.dataPath, "RR_CSV");
            Directory.CreateDirectory(folder);
            writer = null;
        }

        void Start()
        {
            // Resolver에 등록하여 LSL stream 감지 시작
            registerAndLookUpStream();
            Debug.Log($"[LSL_RR] Started looking for stream: Name='{StreamName}', Type='{StreamType}'");
        }

        void OnDestroy()
        {
            try { writer?.Flush(); writer?.Close(); } catch { }
        }

        private static string StampToSecondUtc(long utcMs)
            => DateTimeOffset.FromUnixTimeMilliseconds(utcMs).UtcDateTime.ToString("yyyy-MM-dd_HH-mm-ss");

        // ✅ sampleUtcMs를 받아서 Active=false면 “첫 샘플 시각”을 파일 기준으로 사용
        private void EnsureWriter(long sampleUtcMs)
        {
            string sid = CaptureSession.Active ? CaptureSession.SessionId : "no_session";
            long baseUtcMs = CaptureSession.Active ? CaptureSession.StartUtcMs : sampleUtcMs;

            // 세션이 바뀌면 파일 회전
            if (writer != null && openedSessionId == sid) return;

            try { writer?.Flush(); writer?.Close(); } catch { }
            writer = null;

            string stamp = StampToSecondUtc(baseUtcMs);
            string fileName = $"RR_{sid}_{stamp}_{baseUtcMs}.csv";
            string path = Path.Combine(folder, fileName);

            writer = new StreamWriter(path, false, Encoding.UTF8, 64 * 1024);
            writer.WriteLine("utc_ms,utc_iso,lsl_ts,rr,session,session_start_utc_ms,session_start_utc_iso");

            openedSessionId = sid;

            Debug.Log("[RR] CSV path = " + path);
        }

        protected override void Process(float[] newSample, double timeStamp)
        {
            float rr = newSample[0];

            // 모달리티 준비 여부는 세션과 무관하게 한 번만 알림
            if (!hasLoggedFirstSample)
            {
                hasLoggedFirstSample = true;
                Debug.Log($"[LSL_RR] Received RR data: {rr} (timestamp: {timeStamp:F6})");
                // EyeAudioServer에 RR 모달리티 수신 알림
                EyeAudioServer.NotifyRRReceived();
            }

            // 이벤트 브로드캐스트는 그대로 유지
            EoM_Events.Send_OnDataReceived(VariableType, ExciteOMeterManager.GetTimestamp(), newSample[0]);

            // CaptureSession이 활성화된 이후에만 파일로 기록
            if (!CaptureSession.Active)
                return;

            // ---- LSL timestamp -> UTC(ms) ----
            double localNowSec = liblsl.local_clock();

            if (localNowSec >= nextCorrUpdateLocalSec)
            {
                cachedTimeCorrSec = inlet.time_correction();
                nextCorrUpdateLocalSec = localNowSec + CorrUpdateIntervalSec;
            }

            double correctedSec = timeStamp + cachedTimeCorrSec;

            long utcNowMs = TimeSync.UtcNowMs();
            long sampleUtcMs = utcNowMs + (long)((correctedSec - localNowSec) * 1000.0);

            EnsureWriter(sampleUtcMs);

            long sessionStart = CaptureSession.Active ? CaptureSession.StartUtcMs : 0;
            string sid = CaptureSession.Active ? CaptureSession.SessionId : "no_session";

            string utcIso = DateTimeOffset.FromUnixTimeMilliseconds(sampleUtcMs).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff");
            string ssIso = (sessionStart > 0)
                ? DateTimeOffset.FromUnixTimeMilliseconds(sessionStart).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff")
                : "";

            writer.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "{0},{1},{2:F6},{3:F6},{4},{5},{6}",
                sampleUtcMs,
                utcIso,
                timeStamp,
                rr,
                sid,
                (sessionStart > 0 ? sessionStart.ToString() : ""),
                ssIso
            ));
        }
    }
}

