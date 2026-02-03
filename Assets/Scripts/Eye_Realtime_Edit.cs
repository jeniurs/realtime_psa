using System.Collections;
using System.Globalization;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.XR;

public class Eye_Realtime_Edit : MonoBehaviour
{
    [Header("Server")]
    public string uploadUrl = "http://10.15.238.217:3000/eye";

    [Header("Record Settings")]
    [Tooltip("한 번에 잘라서 보낼 길이(초)")]
    public float windowSeconds = 10f;   // 예: 10초

    [Header("UI")]
    public TextMeshProUGUI eyeText;

    private StringBuilder buffer;
    private float nextCutTime;

    // UI용 마지막 값
    private Vector3 lastOrigin;
    private Vector3 lastDir;
    private float lastTime;
    private bool hasEyeData = false;

    void Start()
    {
        buffer = new StringBuilder();
        // 플레이 시작 시점 기준으로 windowSeconds 뒤에 첫 업로드
        nextCutTime = Time.time + windowSeconds;

        StartCoroutine(CaptureLoop());
    }

    void CaptureEyeFrame()
    {
        // XR이 활성화되어 있는지 확인
        if (!XRSettings.enabled)
            return;

        InputDevice centerEye = InputDevices.GetDeviceAtXRNode(XRNode.CenterEye);

        // InputDevice가 유효하지 않으면 리턴 (XR 초기화 전 또는 디바이스 없음)
        if (!centerEye.isValid)
            return;

        if (centerEye.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 origin) &&
            centerEye.TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion rot))
        {
            Vector3 dir = rot * Vector3.forward;
            float t = Time.time; // timestamp (seconds since play start)

            // CSV line: t,ox,oy,oz,dx,dy,dz
            buffer.Append(t.ToString("F3", CultureInfo.InvariantCulture)).Append(",")
                  .Append(origin.x.ToString("F4", CultureInfo.InvariantCulture)).Append(",")
                  .Append(origin.y.ToString("F4", CultureInfo.InvariantCulture)).Append(",")
                  .Append(origin.z.ToString("F4", CultureInfo.InvariantCulture)).Append(",")
                  .Append(dir.x.ToString("F4", CultureInfo.InvariantCulture)).Append(",")
                  .Append(dir.y.ToString("F4", CultureInfo.InvariantCulture)).Append(",")
                  .Append(dir.z.ToString("F4", CultureInfo.InvariantCulture)).Append("\n");

            // UI 업데이트용
            lastOrigin = origin;
            lastDir = dir;
            lastTime = t;
            hasEyeData = true;

            if (eyeText != null)
            {
                eyeText.text =
                    $"[Eye Realtime]\n" +
                    $"t = {lastTime:F3} s\n\n" +
                    $"Origin:\n" +
                    $"X = {lastOrigin.x:F4}\n" +
                    $"Y = {lastOrigin.y:F4}\n" +
                    $"Z = {lastOrigin.z:F4}\n\n" +
                    $"Direction:\n" +
                    $"X = {lastDir.x:F4}\n" +
                    $"Y = {lastDir.y:F4}\n" +
                    $"Z = {lastDir.z:F4}";
            }
        }
        else
        {
            if (eyeText != null && !hasEyeData)
            {
                eyeText.text = "Eye tracking data not available...";
            }
        }
    }


    IEnumerator CaptureLoop()
    {
        while (true)
        {
            // 매 프레임 시선 1줄씩 추가
            CaptureEyeFrame();

            // windowSeconds마다 버퍼를 잘라서 업로드
            if (Time.time >= nextCutTime)
            {
                string chunk = buffer.ToString();

                if (!string.IsNullOrEmpty(chunk))
                {
                    // 업로드는 별도 코루틴 → 녹화 계속 진행
                    StartCoroutine(UploadChunk(chunk));
                }

                buffer.Clear();
                // 다음 컷 시점 (연속적으로 0~10, 10~20, 20~30...)
                nextCutTime += windowSeconds;
            }

            yield return null;   // 다음 프레임까지 대기
        }
    }



    IEnumerator UploadChunk(string textData)
    {
        var bytes = Encoding.UTF8.GetBytes(textData);

        using (UnityWebRequest uwr = new UnityWebRequest(uploadUrl, "POST"))
        {
            uwr.uploadHandler = new UploadHandlerRaw(bytes);
            uwr.downloadHandler = new DownloadHandlerBuffer();
            uwr.SetRequestHeader("Content-Type", "text/plain");
            uwr.timeout = 20;

            yield return uwr.SendWebRequest();

            if (uwr.result != UnityWebRequest.Result.Success)
                Debug.LogError($"❌ Eye Upload Error: {uwr.error}");
            else
                Debug.Log($"✅ Eye chunk uploaded ({bytes.Length} bytes), server: {uwr.downloadHandler.text}");
        }
    }
}
