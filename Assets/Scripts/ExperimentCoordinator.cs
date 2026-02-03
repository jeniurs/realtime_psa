// LSL을 쓰는 프로젝트면 아래 using이 있어도 되고 없어도 됨.
// (코드 내부는 reflection으로도 동작하게 만들어서, LSL 네임스페이스 충돌/버전차를 최대한 피함)
using LSL;
using System;
using System.Collections;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// LSL 스트림이 발견되면, Eye/Voice/HR/RRi를 동시에 시작해서 recordSeconds만 기록 후 정리.
/// - Eye/Voice는 enable 되는 순간부터 돌아가므로 coordinator가 enable 타이밍을 맞춰줌.
/// - HR/RR도 enable 시점부터 Process가 들어오며 파일 생성/append.
/// - 세션 시작(anchor)을 CaptureSession.Begin으로 먼저 공유한 뒤 enable 해야 HR/RR의 session_start_utc_ms가 채워짐.
/// </summary>
public class ExperimentCoordinator : MonoBehaviour
{
    public enum State { Idle, WaitingForLsl, Recording, Grace, Stopping, Done }

    [Header("Auto Start")]
    public bool autoStartOnPlay = true;
    public KeyCode manualStartKey = KeyCode.Space;

    [Header("Server (Optional)")]
    [Tooltip("EyeAudioServer가 돌아가는 base URL (세션 start/stop 호출용). 예: http://10.147.13.217:3000")]
    public string serverBaseUrl = "http://10.15.238.217:3000";

    [Header("Recording Window")]
    [Tooltip("실제 기록 길이(초)")]
    public float recordSeconds = 10f;

    [Tooltip("recordSeconds가 끝난 뒤, 마지막 업로드/샘플 정리를 위해 기다리는 시간(초)")]
    public float uploadGraceSeconds = 2.0f;

    [Header("LSL Wait")]
    [Tooltip("LSL 스트림을 기다릴 최대 시간(초). 0이면 무한대기")]
    public float lslWaitTimeoutSeconds = 0f;

    [Tooltip("이 키워드(이름/타입)가 하나라도 매칭되면 'LSL 준비됨'으로 판단. 비우면 '아무 스트림'이면 OK.")]
    public string[] lslMustContainKeywords = new string[] { "HR", "RR" };

    [Tooltip("LSL resolve 재시도 주기(초)")]
    public float lslPollIntervalSeconds = 0.25f;

    [Header("Recorders (drag & drop)")]
    public MonoBehaviour eye_voice_Recorder;   // Eye_Realtime + Voice_Realtime 합친 컴포넌트(또는 Eye만)
    public MonoBehaviour hrInlet;              // ExciteOMeter.LSL_Inlet_HR
    public MonoBehaviour rrInlet;              // ExciteOMeter.LSL_Inlet_RRi

    [Header("Recorder Enable Control")]
    [Tooltip("true면 Coordinator가 enabled 토글로 레코더를 시작/중지합니다. (홀로그램에서 enable 대상이 없으면 false 권장)")]
    public bool controlRecordersByEnable = false;

    [Header("Debug")]
    public bool logVerbose = true;

    public State CurrentState { get; private set; } = State.Idle;

    // 공통 세션 정보(다른 스크립트에서 참고하고 싶으면 이걸 사용)
    public static class ExperimentSession
    {
        public static string SessionId { get; internal set; }
        public static long StartUtcMs { get; internal set; }
        public static long EndUtcMs { get; internal set; }
        public static bool IsRecording { get; internal set; }

        public static string StampToSecondUtc(long utcMs)
            => DateTimeOffset.FromUnixTimeMilliseconds(utcMs).UtcDateTime.ToString("yyyy-MM-dd_HH-mm-ss");
    }

    private Coroutine _runCo;

    private void Awake()
    {
        // 시작 전에는 필요 시 레코더를 꺼둠(특히 HR/RR) — 홀로그램에서 enable 대상이 없으면 controlRecordersByEnable=false
        if (controlRecordersByEnable) DisableAllRecorders();

        // 혹시 이전 런에서 남아있을 수 있으니 세션도 초기화
        TryEndCaptureSessionSafe();
    }

    private void Start()
    {
        if (autoStartOnPlay)
            _runCo = StartCoroutine(Run());
    }

    private void Update()
    {
        if (!autoStartOnPlay && CurrentState == State.Idle && Input.GetKeyDown(manualStartKey))
            _runCo = StartCoroutine(Run());
    }

    private void OnApplicationQuit()
    {
        try
        {
            StopAllCoroutines();
            SafeStopVoiceMic();
            ForceCloseWriterIfAny(hrInlet);
            ForceCloseWriterIfAny(rrInlet);
            TryEndCaptureSessionSafe();
        }
        catch { }
    }

    public IEnumerator Run()
    {
        if (CurrentState != State.Idle && CurrentState != State.Done)
            yield break;

        CurrentState = State.WaitingForLsl;

        // 1) LSL 스트림 대기
        if (logVerbose) Debug.Log("[Coordinator] Waiting for LSL streams...");
        yield return StartCoroutine(WaitForLslStreams());

        // 2) 공통 세션 생성(이 시점이 "START flag")
        ExperimentSession.SessionId = $"{DateTimeOffset.UtcNow:MMddHHmmss}_{Guid.NewGuid():N}".Substring(0, 24);
        ExperimentSession.StartUtcMs = TimeSync.UtcNowMs();
        ExperimentSession.EndUtcMs = ExperimentSession.StartUtcMs + (long)(recordSeconds * 1000.0);
        ExperimentSession.IsRecording = true;

        // ✅ 추가: HR/RR/Eye/Voice가 같은 세션 anchor(startUtcMs)를 공유하도록 먼저 Begin
        // (EnableAllRecorders() 이전에 해야 HR/RR가 no_session으로 파일을 열지 않음)
        yield return StartCoroutine(PostSessionStartToServer(ExperimentSession.SessionId, ExperimentSession.StartUtcMs));
        TryBeginCaptureSessionSafe(ExperimentSession.SessionId, ExperimentSession.StartUtcMs);

        if (logVerbose)
        {
            Debug.Log($"[Coordinator] START session={ExperimentSession.SessionId} " +
                      $"startUtcMs={ExperimentSession.StartUtcMs}({ExperimentSession.StampToSecondUtc(ExperimentSession.StartUtcMs)}) " +
                      $"duration={recordSeconds:F3}s");
        }

        // 3) “동시에” 시작: (옵션) 같은 프레임에 Enable
        if (controlRecordersByEnable) EnableAllRecorders();
        CurrentState = State.Recording;

        // 4) 정확히 recordSeconds만큼 대기
        float t0 = Time.realtimeSinceStartup;
        while (Time.realtimeSinceStartup - t0 < recordSeconds)
            yield return null;

        // 5) 업로드/마지막 샘플 여유
        CurrentState = State.Grace;
        if (uploadGraceSeconds > 0)
        {
            if (logVerbose) Debug.Log($"[Coordinator] GRACE {uploadGraceSeconds:F2}s for last uploads/samples...");
            float g0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - g0 < uploadGraceSeconds)
                yield return null;
        }

        // 6) 정리(Stop/Disable + HR/RR writer flush)
        CurrentState = State.Stopping;

        ExperimentSession.IsRecording = false;

        // Voice는 마이크가 계속 돌 수 있으니 먼저 끊음
        SafeStopVoiceMic();

        // HR/RR는 StreamWriter flush/close (OnDestroy 기다리지 않고 즉시 파일 완결)
        ForceCloseWriterIfAny(hrInlet);
        ForceCloseWriterIfAny(rrInlet);

        // Eye/Voice는 (옵션) 다음 window로 넘어가서 추가 업로드가 생기지 않게 StopAllCoroutines + Disable
        if (controlRecordersByEnable)
        {
            StopCoroutinesIfAny(eye_voice_Recorder);
            DisableAllRecorders();
        }

        // ✅ 추가: 세션 종료(다음 런에 이전 세션이 남지 않도록)
        TryEndCaptureSessionSafe();

        if (logVerbose)
        {
            Debug.Log($"[Coordinator] STOP session={ExperimentSession.SessionId} " +
                      $"endUtcMs={TimeSync.UtcNowMs()}({ExperimentSession.StampToSecondUtc(TimeSync.UtcNowMs())})");
        }

        CurrentState = State.Done;
    }

    private IEnumerator WaitForLslStreams()
    {
        double start = Time.realtimeSinceStartupAsDouble;

        while (true)
        {
            bool ok = false;
            try
            {
                var infos = TryResolveAllStreams(0.2);

                if (infos != null && infos.Length > 0)
                {
                    if (lslMustContainKeywords == null || lslMustContainKeywords.Length == 0)
                    {
                        ok = true;
                    }
                    else
                    {
                        string blob = BuildStreamInfoBlob(infos);
                        ok = ContainsAllKeywords(blob, lslMustContainKeywords);
                        if (logVerbose && !ok)
                            Debug.Log($"[Coordinator] LSL found but not matched yet. streams={blob}");
                    }
                }
            }
            catch (Exception e)
            {
                if (logVerbose) Debug.Log($"[Coordinator] LSL resolve exception (retry): {e.Message}");
            }

            if (ok) yield break;

            if (lslWaitTimeoutSeconds > 0)
            {
                double elapsed = Time.realtimeSinceStartupAsDouble - start;
                if (elapsed >= lslWaitTimeoutSeconds)
                {
                    Debug.LogWarning("[Coordinator] LSL wait timeout. Start anyway.");
                    yield break;
                }
            }

            yield return new WaitForSeconds(lslPollIntervalSeconds);
        }
    }

    private IEnumerator PostSessionStartToServer(string sid, long startUtcMs)
    {
        string url = $"{serverBaseUrl.TrimEnd('/')}/session/start"; // EyeAudioServer와 일치
        string json = $"{{\"session\":\"{sid}\",\"start_utc_ms\":{startUtcMs}}}";

        using (UnityWebRequest uwr = new UnityWebRequest(url, "POST"))
        {
            byte[] body = Encoding.UTF8.GetBytes(json);
            uwr.uploadHandler = new UploadHandlerRaw(body);
            uwr.downloadHandler = new DownloadHandlerBuffer();
            uwr.SetRequestHeader("Content-Type", "application/json");

            yield return uwr.SendWebRequest();

            if (uwr.result == UnityWebRequest.Result.Success)
                Debug.Log($"[Coordinator] Server session started OK: {uwr.downloadHandler.text}");
            else
                Debug.LogWarning($"[Coordinator] Server session start failed: {uwr.responseCode} {uwr.error}");
        }
    }

    // ---------- CaptureSession bridge ----------
    // CaptureSession.cs(전역 클래스)가 프로젝트에 있어야 함.
    private static void TryBeginCaptureSessionSafe(string sid, long startUtcMs)
    {
        try
        {
            CaptureSession.Begin(sid, startUtcMs);
        }
        catch (Exception e)
        {
            Debug.LogWarning("[Coordinator] CaptureSession.Begin failed: " + e.Message);
        }
    }

    private static void TryEndCaptureSessionSafe()
    {
        try
        {
            CaptureSession.End();
        }
        catch { }
    }

    // ---------- Recorder enable/disable ----------
    private void EnableAllRecorders()
    {
        SetEnabled(hrInlet, true);
        SetEnabled(rrInlet, true);
        SetEnabled(eye_voice_Recorder, true);
    }

    private void DisableAllRecorders()
    {
        SetEnabled(hrInlet, false);
        SetEnabled(rrInlet, false);
        SetEnabled(eye_voice_Recorder, false);
    }

    private static void SetEnabled(MonoBehaviour mb, bool on)
    {
        if (mb == null) return;
        mb.enabled = on;

        if (on && mb.gameObject != null && !mb.gameObject.activeSelf)
            mb.gameObject.SetActive(true);
    }

    private static void StopCoroutinesIfAny(MonoBehaviour mb)
    {
        if (mb == null) return;
        try { mb.StopAllCoroutines(); } catch { }
    }

    private static void SafeStopVoiceMic()
    {
        try { Microphone.End(null); } catch { }
    }

    // ---------- HR/RR StreamWriter 강제 close (파일 완결) ----------
    private static void ForceCloseWriterIfAny(MonoBehaviour mb)
    {
        if (mb == null) return;

        try
        {
            var t = mb.GetType();
            var f = FindFieldRecursive(t, "writer");
            if (f == null) return;

            object wObj = f.GetValue(mb);
            if (wObj == null) return;

            var flush = wObj.GetType().GetMethod("Flush", BindingFlags.Instance | BindingFlags.Public);
            var close = wObj.GetType().GetMethod("Close", BindingFlags.Instance | BindingFlags.Public);

            flush?.Invoke(wObj, null);
            close?.Invoke(wObj, null);

            f.SetValue(mb, null);
        }
        catch { }
    }

    private static FieldInfo FindFieldRecursive(Type t, string name)
    {
        while (t != null)
        {
            var f = t.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (f != null) return f;
            t = t.BaseType;
        }
        return null;
    }

    // ---------- LSL resolve (버전 호환 위해 reflection로 최대한) ----------
    private static LSL.liblsl.StreamInfo[] TryResolveAllStreams(double timeoutSec)
    {
        Type libType = typeof(LSL.liblsl);

        var m1 = libType.GetMethod("resolve_streams", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(double) }, null);
        if (m1 != null)
        {
            var r = m1.Invoke(null, new object[] { timeoutSec }) as LSL.liblsl.StreamInfo[];
            return r;
        }

        var m2 = libType.GetMethod("resolve_stream", BindingFlags.Public | BindingFlags.Static, null,
            new[] { typeof(string), typeof(string), typeof(int), typeof(double) }, null);

        if (m2 != null)
        {
            string[] commonTypes = { "ECG", "HR", "RR", "RRI", "PPG", "BIO", "EEG" };
            foreach (var ct in commonTypes)
            {
                var r = m2.Invoke(null, new object[] { "type", ct, 1, timeoutSec }) as LSL.liblsl.StreamInfo[];
                if (r != null && r.Length > 0) return r;
            }
        }

        return null;
    }

    private static string BuildStreamInfoBlob(LSL.liblsl.StreamInfo[] infos)
    {
        try
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = 0; i < infos.Length; i++)
            {
                var info = infos[i];
                string name = SafeCallString(info, "name");
                string type = SafeCallString(info, "type");
                string sid = SafeCallString(info, "source_id");

                if (i > 0) sb.Append(" | ");
                sb.Append($"name={name},type={type},sid={sid}");
            }
            return sb.ToString();
        }
        catch
        {
            return $"count={infos?.Length ?? 0}";
        }
    }

    private static string SafeCallString(object obj, string method)
    {
        if (obj == null) return "";
        var m = obj.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.Public);
        if (m == null) return "";
        try
        {
            object r = m.Invoke(obj, null);
            return r?.ToString() ?? "";
        }
        catch { return ""; }
    }

    private static bool ContainsAllKeywords(string haystack, string[] keywords)
    {
        if (keywords == null || keywords.Length == 0) return true;
        if (haystack == null) haystack = "";

        for (int i = 0; i < keywords.Length; i++)
        {
            string k = (keywords[i] ?? "").Trim();
            if (k.Length == 0) continue;

            if (haystack.IndexOf(k, StringComparison.OrdinalIgnoreCase) < 0)
                return false;
        }
        return true;
    }
}
