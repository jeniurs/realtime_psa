using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using UnityEngine;

public class EyeAudioServer : MonoBehaviour
{
    public int port = 3000;

    private string eyeDir;
    private string audioDir;

    private HttpListener listener;
    private Thread listenerThread;
    private volatile bool isRunning = false;

    // 세션 동안 10초만 수집 / 로그 1회만 출력 제어
    private const int SessionWindowMs = 10_000; // 10 seconds
    private bool eyeLoggedThisSession = false;
    private bool audioLoggedThisSession = false;

    // 세션 관리
    private readonly object sessionLock = new object();
    private string activeSessionId = "";
    private long activeStartUtcMs = 0;
    
    // 세션 카운터 (no_1, no_2, no_3 ...)
    private static int sessionCounter = 0;

    // 4개 모달리티 모두 들어왔는지 확인 (HR, RR, Eye, Audio)
    private static EyeAudioServer instance = null;
    private readonly object modalityLock = new object();
    private bool hrReceived = false;
    private bool rrReceived = false;
    private bool eyeReceived = false;
    private bool audioReceived = false;
    private bool allModalitiesReady = false;

    /// <summary>
    /// 세션 카운터를 리셋합니다. (다음 세션부터 no_1부터 다시 시작)
    /// </summary>
    public static void ResetSessionCounter()
    {
        sessionCounter = 0;
        Debug.Log("[EyeAudioServer] Session counter reset. Next session will be 'no_1'");
    }

    void Start()
    {
        string appDir = Application.dataPath;
        eyeDir = Path.Combine(appDir, "eyedata");
        audioDir = Path.Combine(appDir, "recordings");
        Directory.CreateDirectory(eyeDir);
        Directory.CreateDirectory(audioDir);

        Debug.Log($"[EyeAudioServer] EYE:   {eyeDir}");
        Debug.Log($"[EyeAudioServer] AUDIO: {audioDir}");

        // 싱글톤 인스턴스 설정
        instance = this;

        // 서버는 Resolver에서 LSL stream 감지 시 시작됨
        // StartServer(); // 주석 처리
    }

    /// <summary>
    /// HR 모달리티가 첫 샘플을 받았음을 알림
    /// </summary>
    public static void NotifyHRReceived()
    {
        if (instance == null) return;
        lock (instance.modalityLock)
        {
            if (!instance.hrReceived)
            {
                instance.hrReceived = true;
                Debug.Log("[EyeAudioServer] ✅ HR modality received");
                instance.CheckAllModalitiesReady();
            }
        }
    }

    /// <summary>
    /// RR 모달리티가 첫 샘플을 받았음을 알림
    /// </summary>
    public static void NotifyRRReceived()
    {
        if (instance == null) return;
        lock (instance.modalityLock)
        {
            if (!instance.rrReceived)
            {
                instance.rrReceived = true;
                Debug.Log("[EyeAudioServer] ✅ RR modality received");
                instance.CheckAllModalitiesReady();
            }
        }
    }

    /// <summary>
    /// 4개 모달리티가 모두 준비되었는지 확인하고, 준비되면 세션 시작
    /// </summary>
    private void CheckAllModalitiesReady()
    {
        if (allModalitiesReady) return;

        bool allReady = hrReceived && rrReceived && eyeReceived && audioReceived;
        if (allReady)
        {
            allModalitiesReady = true;
            long markerUtcMs = TimeSync.UtcNowMs();
            
            // 세션 카운터 증가하고 no_1 시작
            sessionCounter++;
            string newSid = $"no_{sessionCounter}";
            
            SetActiveSession(newSid, markerUtcMs);
            Debug.Log($"[EyeAudioServer] 🎯 MARKER: All 4 modalities ready! Session '{newSid}' started at {markerUtcMs}");
            Debug.Log($"[EyeAudioServer] 📊 Collecting data for 10 seconds (window: {markerUtcMs} ~ {markerUtcMs + SessionWindowMs})");
        }
        else
        {
            int count = (hrReceived ? 1 : 0) + (rrReceived ? 1 : 0) + (eyeReceived ? 1 : 0) + (audioReceived ? 1 : 0);
            Debug.Log($"[EyeAudioServer] ⏳ Waiting for all modalities... ({count}/4)");
        }
    }

    public void StartServer()
    {
        Debug.Log("[EyeAudioServer] StartServer() called");
        
        if (isRunning)
        {
            Debug.Log("[EyeAudioServer] Server is already running, skipping.");
            return;
        }

        Debug.Log($"[EyeAudioServer] Creating HttpListener on port {port}...");
        listener = new HttpListener();
        listener.Prefixes.Add($"http://10.15.238.217:{port}/");

        try 
        { 
            listener.Start();
            Debug.Log("[EyeAudioServer] HttpListener started successfully.");
        }
        catch (Exception e)
        {
            Debug.LogError($"[EyeAudioServer] ❌ Failed to start HttpListener: {e.Message}");
            Debug.LogException(e);
            return;
        }

        isRunning = true;
        listenerThread = new Thread(ListenLoop) { IsBackground = true };
        listenerThread.Start();

        Debug.Log($"[EyeAudioServer] ✅ Server started successfully on http://10.15.238.217:{port}/");
    }

    void OnApplicationQuit() => StopServer();


    private void StopServer()
    {
        isRunning = false;
        try { listener?.Stop(); } catch { }
        try { listener?.Close(); } catch { }

        try
        {
            if (listenerThread != null && listenerThread.IsAlive)
                listenerThread.Join(500);
        }
        catch { }

        Debug.Log("[EyeAudioServer] Server stopped");
    }

    private void ListenLoop()
    {
        while (isRunning && listener != null && listener.IsListening)
        {
            HttpListenerContext context = null;
            try { context = listener.GetContext(); }
            catch { break; }

            if (context == null) continue;
            ThreadPool.QueueUserWorkItem(_ => HandleRequest(context));
        }
    }

    private void HandleRequest(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;

        long serverRecvUtcMs = TimeSync.UtcNowMs();

        try
        {
            res.Headers["X-Server-UtcMs"] = serverRecvUtcMs.ToString();

            string path = req.Url.AbsolutePath;

            if (req.HttpMethod == "GET" && path == "/health")
                HandleHealth(res, serverRecvUtcMs);

            else if (req.HttpMethod == "POST" && path == "/session/start")
                HandleSessionStart(req, res, serverRecvUtcMs);

            else if (req.HttpMethod == "GET" && path == "/session")
                HandleSessionGet(res, serverRecvUtcMs);

            else if (req.HttpMethod == "POST" && path == "/session/stop")
                HandleSessionStop(res, serverRecvUtcMs);

            else if (req.HttpMethod == "POST" && path == "/eye")
                HandleEye(req, res, serverRecvUtcMs);

            else if (req.HttpMethod == "POST" && (path == "/" || path == "/audio"))
                HandleAudio(req, res, serverRecvUtcMs);

            else
            {
                res.StatusCode = 404;
                byte[] msg = Encoding.UTF8.GetBytes("not found");
                res.OutputStream.Write(msg, 0, msg.Length);
            }
        }
        catch (Exception e)
        {
            Debug.LogError("[EyeAudioServer] HandleRequest error: " + e);
            try
            {
                res.StatusCode = 500;
                byte[] msg = Encoding.UTF8.GetBytes("server error");
                res.OutputStream.Write(msg, 0, msg.Length);
            }
            catch { }
        }
        finally
        {
            try { res.OutputStream.Close(); } catch { }
        }
    }

    // ---------- helpers ----------
    private static long? GetHeaderLong(HttpListenerRequest req, string name)
    {
        try
        {
            var v = req.Headers[name];
            if (string.IsNullOrWhiteSpace(v)) return null;
            if (long.TryParse(v.Trim(), out var x)) return x;
            return null;
        }
        catch { return null; }
    }

    private static string GetHeaderString(HttpListenerRequest req, string name)
    {
        try
        {
            var v = req.Headers[name];
            return string.IsNullOrWhiteSpace(v) ? "" : v.Trim();
        }
        catch { return ""; }
    }

    private static int GetQueryInt(HttpListenerRequest req, string key, int defVal)
    {
        try
        {
            var v = req.QueryString[key];
            if (string.IsNullOrWhiteSpace(v)) return defVal;
            if (int.TryParse(v.Trim(), out var x)) return x;
            return defVal;
        }
        catch { return defVal; }
    }

    private static string EscapeJson(string s)
    {
        if (s == null) return "";
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private (string sid, long startUtcMs) GetActiveSession()
    {
        lock (sessionLock) return (activeSessionId, activeStartUtcMs);
    }

    /// <summary>
    /// 주어진 기준 시각(tMs)에 맞춰 세션을 자동 생성/회전한다.
    /// - 이미 시작된 세션 기준으로 tMs가 startUtcMs + 10초를 넘으면: 기존 세션을 종료하고 no_{+1}로 회전
    /// </summary>
    private void EnsureSessionForTimestamp(long tMs)
    {
        var (sid, startMs) = GetActiveSession();

        // 세션이 아직 없으면(마커 전이면) 자동으로 만들지 않음
        if (string.IsNullOrWhiteSpace(sid) || startMs <= 0)
            return;

        // 마커 시점보다 이전 타임스탬프는 그대로 현재 세션에 포함 (startMs를 움직이지 않음)
        if (tMs <= startMs)
            return;

        // 10초 윈도우를 넘어서면 다음 세션으로 회전
        if (tMs > startMs + SessionWindowMs)
        {
            long elapsedMs = tMs - startMs;
            Debug.Log($"[EyeAudioServer] Session '{sid}' window elapsed ({elapsedMs}ms). Rotating to next session...");
            TryEndCaptureSessionSafe();

            sessionCounter++;
            string newSid = $"no_{sessionCounter}";
            long startUtcMs = tMs;
            SetActiveSession(newSid, startUtcMs);
            Debug.Log($"[EyeAudioServer] Auto session rotated: {sid} -> {newSid} (startUtcMs={startUtcMs})");
        }
    }

    private void SetActiveSession(string sid, long startUtcMs)
    {
        lock (sessionLock)
        {
            activeSessionId = sid;
            activeStartUtcMs = startUtcMs;
        }

        // 새 세션마다 Eye/Audio 로그 플래그 초기화
        eyeLoggedThisSession = false;
        audioLoggedThisSession = false;

        // ECG 쪽도 같은 세션으로 파일명 맞추기
        TryBeginCaptureSessionSafe(sid, startUtcMs);
    }

    private static void TryBeginCaptureSessionSafe(string sid, long startUtcMs)
    {
        try
        {
            CaptureSession.Begin(sid, startUtcMs);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[EyeAudioServer] CaptureSession.Begin failed: {e.Message}");
        }
    }

    private static void TryEndCaptureSessionSafe()
    {
        try
        {
            CaptureSession.End();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[EyeAudioServer] CaptureSession.End failed: {e.Message}");
        }
    }

    // ========== /health ==========
    private void HandleHealth(HttpListenerResponse res, long serverUtcMs)
    {
        res.StatusCode = 200;
        res.ContentType = "application/json; charset=utf-8";

        var (sid, startMs) = GetActiveSession();
        string json =
            "{" +
            $"\"ok\":true," +
            $"\"server_utc_ms\":{serverUtcMs}," +
            $"\"session\":\"{EscapeJson(sid)}\"," +
            $"\"session_start_utc_ms\":{(startMs > 0 ? startMs.ToString() : "null")}" +
            "}";

        byte[] bytes = Encoding.UTF8.GetBytes(json);
        res.OutputStream.Write(bytes, 0, bytes.Length);
    }

    // ========== /session/start ==========
    private void HandleSessionStart(HttpListenerRequest req, HttpListenerResponse res, long serverRecvUtcMs)
    {
        // lead_ms 만큼 미래로 잡아두면 클라(eye/voice)가 동시에 기다렸다 시작 가능
        int leadMs = Mathf.Clamp(GetQueryInt(req, "lead_ms", 1500), 0, 10000);

        var (sid0, start0) = GetActiveSession();

        // 이미 세션이 있고 아직 10초가 지나지 않았으면 그대로 반환(중복 start 방지)
        if (!string.IsNullOrWhiteSpace(sid0) && start0 > 0)
        {
            long elapsedMs = serverRecvUtcMs - start0;
            if (elapsedMs < SessionWindowMs)
            {
                WriteSessionJson(res, serverRecvUtcMs, sid0, start0);
                return;
            }
            // 10초가 지났으면 기존 세션 종료하고 새 세션 시작
            Debug.Log($"[EyeAudioServer] Previous session '{sid0}' expired ({elapsedMs}ms elapsed), starting new session...");
            TryEndCaptureSessionSafe();
        }

        // 세션 카운터 증가하고 no_X 형식으로 세션 ID 생성
        sessionCounter++;
        string newSid = $"no_{sessionCounter}";
        long startUtcMs = serverRecvUtcMs + leadMs;

        SetActiveSession(newSid, startUtcMs);
        Debug.Log($"[EyeAudioServer] Session started: {newSid} (startUtcMs={startUtcMs})");
        WriteSessionJson(res, serverRecvUtcMs, newSid, startUtcMs);
    }

    private void HandleSessionGet(HttpListenerResponse res, long serverUtcMs)
    {
        var (sid, startMs) = GetActiveSession();
        WriteSessionJson(res, serverUtcMs, sid, startMs);
    }

    private void HandleSessionStop(HttpListenerResponse res, long serverUtcMs)
    {
        lock (sessionLock)
        {
            activeSessionId = "";
            activeStartUtcMs = 0;
        }
        TryEndCaptureSessionSafe();

        res.StatusCode = 200;
        res.ContentType = "application/json; charset=utf-8";

        string json = "{" + $"\"ok\":true,\"server_utc_ms\":{serverUtcMs}" + "}";
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        res.OutputStream.Write(bytes, 0, bytes.Length);
    }

    private void WriteSessionJson(HttpListenerResponse res, long serverUtcMs, string sid, long startUtcMs)
    {
        res.StatusCode = 200;
        res.ContentType = "application/json; charset=utf-8";

        string json =
            "{" +
            $"\"ok\":true," +
            $"\"server_utc_ms\":{serverUtcMs}," +
            $"\"session\":\"{EscapeJson(sid)}\"," +
            $"\"start_utc_ms\":{(startUtcMs > 0 ? startUtcMs.ToString() : "null")}" +
            "}";

        byte[] bytes = Encoding.UTF8.GetBytes(json);
        res.OutputStream.Write(bytes, 0, bytes.Length);
    }

    // ========== /eye ==========
    private struct EyePoint
    {
        public double t;
        public double ox, oy, oz;
        public double dx, dy, dz;
    }

    private EyePoint? ParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;

        string s = line.Trim().Replace(";", ",");
        string[] parts = s.Split(',');
        if (parts.Length != 7) return null;

        double[] nums = new double[7];
        for (int i = 0; i < 7; i++)
        {
            if (!double.TryParse(parts[i].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out nums[i]))
                return null;
        }

        return new EyePoint
        {
            t = nums[0],
            ox = nums[1],
            oy = nums[2],
            oz = nums[3],
            dx = nums[4],
            dy = nums[5],
            dz = nums[6]
        };
    }

    private static long EstimateEyeUtcMs(double t, long? captureStartUtcMs, double firstT)
    {
        if (t >= 1e12) return (long)t;              // unix ms
        if (t >= 1e9) return (long)(t * 1000.0);    // unix seconds

        if (captureStartUtcMs.HasValue)
            return captureStartUtcMs.Value + (long)((t - firstT) * 1000.0);

        return -1;
    }

    private void HandleEye(HttpListenerRequest req, HttpListenerResponse res, long serverRecvUtcMs)
    {
        // 첫 Eye 요청이 들어오면 플래그만 설정하고 저장하지 않음
        lock (modalityLock)
        {
            if (!eyeReceived)
            {
                eyeReceived = true;
                Debug.Log("[EyeAudioServer] ✅ Eye modality received");
                CheckAllModalitiesReady();
            }
        }

        // 4개 모달리티가 모두 준비되지 않았으면 저장하지 않고 리턴
        if (!allModalitiesReady)
        {
            res.StatusCode = 200;
            res.OutputStream.Write(Encoding.UTF8.GetBytes("waiting"));
            return;
        }

        string body;
        using (var reader = new StreamReader(req.InputStream, req.ContentEncoding))
            body = reader.ReadToEnd();

        body = (body ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(body))
        {
            res.StatusCode = 400;
            res.OutputStream.Write(Encoding.UTF8.GetBytes("empty"));
            return;
        }

        // ✅ 세션/기준시각: “도착”이 아니라 “캡처 시작”으로 파일명 통일
        string sid = GetHeaderString(req, "X-Session-Id");
        long? captureStartUtcMs = GetHeaderLong(req, "X-Capture-Start-UtcMs");
        long baseUtcMs = captureStartUtcMs ?? serverRecvUtcMs;

        // 세션 자동 생성/회전: 첫 10초는 no_1, 다음 10초는 no_2 ...
        EnsureSessionForTimestamp(baseUtcMs);

        // 세션이 비어있으면 방금 EnsureSessionForTimestamp에서 채워졌을 것
        if (string.IsNullOrWhiteSpace(sid))
        {
            var s = GetActiveSession();
            sid = string.IsNullOrWhiteSpace(s.sid) ? "no_session" : s.sid;
        }

        string stamp = TimeSync.FileStampUtc(baseUtcMs);
        string fileCsv = Path.Combine(eyeDir, $"eye_{sid}_{stamp}_{baseUtcMs}.csv");
        string fileMeta = Path.Combine(eyeDir, $"eye_{sid}_{stamp}_{baseUtcMs}.json");

        string[] lines = body.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

        double firstT = 0;
        bool firstTSet = false;

        var parsedList = new List<EyePoint>(lines.Length);
        int ok = 0, ng = 0;

        foreach (var line in lines)
        {
            var p = ParseLine(line);
            if (p == null) { ng++; continue; }

            if (!firstTSet)
            {
                firstT = p.Value.t;
                firstTSet = true;
            }

            parsedList.Add(p.Value);
            ok++;
        }

        using (var writer = new StreamWriter(fileCsv, false, Encoding.UTF8))
        {
            writer.WriteLine("utc_ms,t,ox,oy,oz,dx,dy,dz,server_recv_utc_ms,session");
            foreach (var p in parsedList)
            {
                long utc = firstTSet ? EstimateEyeUtcMs(p.t, captureStartUtcMs, firstT) : -1;
                string utcStr = (utc >= 0) ? utc.ToString() : "";
                writer.WriteLine(
                    $"{utcStr},{p.t.ToString(CultureInfo.InvariantCulture)}," +
                    $"{p.ox.ToString(CultureInfo.InvariantCulture)},{p.oy.ToString(CultureInfo.InvariantCulture)},{p.oz.ToString(CultureInfo.InvariantCulture)}," +
                    $"{p.dx.ToString(CultureInfo.InvariantCulture)},{p.dy.ToString(CultureInfo.InvariantCulture)},{p.dz.ToString(CultureInfo.InvariantCulture)}," +
                    $"{serverRecvUtcMs},{sid}"
                );
            }
        }

        string metaJson =
            "{" +
            $"\"session\":\"{EscapeJson(sid)}\"," +
            $"\"base_utc_ms\":{baseUtcMs}," +
            $"\"server_recv_utc_ms\":{serverRecvUtcMs}," +
            $"\"capture_start_utc_ms\":{(captureStartUtcMs.HasValue ? captureStartUtcMs.Value.ToString() : "null")}," +
            $"\"ok\":{ok},\"ng\":{ng}," +
            $"\"csv\":\"{EscapeJson(Path.GetFileName(fileCsv))}\"" +
            "}";

        File.WriteAllText(fileMeta, metaJson, Encoding.UTF8);

        // 최초 1회만 저장 로그 출력
        if (!eyeLoggedThisSession)
        {
            eyeLoggedThisSession = true;
            Debug.Log($"[EyeAudioServer] EYE saved: {fileCsv} | base={baseUtcMs} recv={serverRecvUtcMs}");
        }

        res.StatusCode = 200;
        res.OutputStream.Write(Encoding.UTF8.GetBytes("ok"));
    }

    // ========== /audio ==========
    private void HandleAudio(HttpListenerRequest req, HttpListenerResponse res, long serverRecvUtcMs)
    {
        // 첫 Audio 요청이 들어오면 플래그만 설정하고 저장하지 않음
        lock (modalityLock)
        {
            if (!audioReceived)
            {
                audioReceived = true;
                Debug.Log("[EyeAudioServer] ✅ Audio modality received");
                CheckAllModalitiesReady();
            }
        }

        // 4개 모달리티가 모두 준비되지 않았으면 저장하지 않고 리턴
        if (!allModalitiesReady)
        {
            res.StatusCode = 200;
            res.OutputStream.Write(Encoding.UTF8.GetBytes("waiting"));
            return;
        }

        string sid = GetHeaderString(req, "X-Session-Id");
        if (string.IsNullOrWhiteSpace(sid))
        {
            var s = GetActiveSession();
            sid = string.IsNullOrWhiteSpace(s.sid) ? "no_session" : s.sid;
        }

        long? recStartUtcMs = GetHeaderLong(req, "X-Rec-Start-UtcMs");
        long? recEndUtcMs = GetHeaderLong(req, "X-Rec-End-UtcMs");

        // ✅ 파일명 기준을 “도착”이 아니라 “녹음 구간 시작”으로
        long baseUtcMs = recStartUtcMs ?? serverRecvUtcMs;

        // 세션 자동 생성/회전: 첫 10초는 no_1, 다음 10초는 no_2 ...
        EnsureSessionForTimestamp(baseUtcMs);

        var local = DateTimeOffset.FromUnixTimeMilliseconds(serverRecvUtcMs).ToLocalTime().ToString("HH:mm:ss.fff");
        var utc = DateTimeOffset.FromUnixTimeMilliseconds(serverRecvUtcMs).ToString("HH:mm:ss.fff'Z'");
        Debug.Log($"ARRIVAL utc={utc} local={local} ms={serverRecvUtcMs}");

        string stamp = TimeSync.FileStampUtc(baseUtcMs);
        string fileWav = Path.Combine(audioDir, $"audio_{sid}_{stamp}_{baseUtcMs}.wav");
        string fileMeta = Path.Combine(audioDir, $"audio_{sid}_{stamp}_{baseUtcMs}.json");

        long total = 0;
        long firstByteUtcMs = -1;
        long lastByteUtcMs = -1;

        using (var fs = new FileStream(fileWav, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            byte[] buffer = new byte[8192];
            int read;
            while ((read = req.InputStream.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (firstByteUtcMs < 0) firstByteUtcMs = TimeSync.UtcNowMs();
                fs.Write(buffer, 0, read);
                total += read;
                lastByteUtcMs = TimeSync.UtcNowMs();
            }
        }

        string metaJson =
            "{" +
            $"\"session\":\"{EscapeJson(sid)}\"," +
            $"\"base_utc_ms\":{baseUtcMs}," +
            $"\"server_req_arrival_utc_ms\":{serverRecvUtcMs}," +
            $"\"server_first_byte_utc_ms\":{(firstByteUtcMs >= 0 ? firstByteUtcMs.ToString() : "null")}," +
            $"\"server_last_byte_utc_ms\":{(lastByteUtcMs >= 0 ? lastByteUtcMs.ToString() : "null")}," +
            $"\"rec_start_utc_ms\":{(recStartUtcMs.HasValue ? recStartUtcMs.Value.ToString() : "null")}," +
            $"\"rec_end_utc_ms\":{(recEndUtcMs.HasValue ? recEndUtcMs.Value.ToString() : "null")}," +
            $"\"bytes\":{total}," +
            $"\"wav\":\"{EscapeJson(Path.GetFileName(fileWav))}\"" +
            "}";

        File.WriteAllText(fileMeta, metaJson, Encoding.UTF8);

        // 최초 1회만 저장 로그 출력
        if (!audioLoggedThisSession)
        {
            audioLoggedThisSession = true;
            Debug.Log($"[EyeAudioServer] AUDIO saved: {fileWav} | base={baseUtcMs} recv={serverRecvUtcMs} recStart={recStartUtcMs}");
        }

        res.StatusCode = 200;
        res.OutputStream.Write(Encoding.UTF8.GetBytes("ok"));
    }
}
