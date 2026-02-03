using System.Collections;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

public class Voice_Realtime : MonoBehaviour
{
    [Header("Server")]
    [Tooltip("..")]
    public string uploadUrl = "http://172.31.199.217:3000/";

    [Header("Audio Settings")]
    [Tooltip("마이크 샘플레이트(서버 요구에 맞춤). 16000 권장")]
    public int sampleRate = 16000;

    [Tooltip("한 번에 잘라 보낼 길이(초). 10초 권장")]
    public int windowSeconds = 10;

    [Tooltip("다음 블록을 몇 초마다 보낼지(스텝). windowSeconds와 같으면 겹침 0%")]
    public float hopSeconds = 10f;

    [Tooltip("마이크 내부 순환 버퍼 길이(초). windowSeconds * 2 이상 권장")]
    public int clipLenSec = 20;

    [Tooltip("기본 마이크 사용 (true면 device=null)")]
    public bool useDefaultMic = true;

    private AudioClip micClip;
    private string device = null;

#if UNITY_2020_2_OR_NEWER
    private bool IsOk(UnityWebRequest uwr) => uwr.result == UnityWebRequest.Result.Success;
#else
    private bool IsOk(UnityWebRequest uwr) => !(uwr.isHttpError || uwr.isNetworkError);
#endif

    IEnumerator Start()
    {
        // 마이크 장치 선택(필요시 직접 지정)
        if (!useDefaultMic)
        {
            var devs = Microphone.devices;
            if (devs != null && devs.Length > 0)
                device = devs[0];
        }

        // 마이크 시작(loop=true, 순환 버퍼 = clipLenSec)
        micClip = Microphone.Start(device, true, clipLenSec, sampleRate);

        // 마이크가 실제로 돌기 시작할 때까지 대기
        while (Microphone.GetPosition(device) <= 0) yield return null;

        // 첫 블록을 만들 수 있을 만큼 프리롤 대기 (최근 windowSeconds 확보)
        yield return new WaitForSeconds(windowSeconds);

        // 이후 주기적으로 블록 전송
        StartCoroutine(SendLoop());
    }

    IEnumerator SendLoop()
    {
        int totalSamples = micClip.samples;           // clipLenSec * sampleRate
        int blockSamples = windowSeconds * sampleRate;

        while (true)
        {
            // 현재 녹음 위치
            int pos = Microphone.GetPosition(device);
            // 최근 windowSeconds 시작 인덱스
            int start = pos - blockSamples;
            if (start < 0) start += totalSamples;

            // 순환 경계를 넘어가는지 체크
            var data = new float[blockSamples];

            if (start + blockSamples <= totalSamples)
            {
                // 경계 넘지 않음: 한 번에 읽기
                micClip.GetData(data, start);
            }
            else
            {
                // 경계를 넘는 경우: 두 구간으로 나눠 정확히 읽기
                int part1 = totalSamples - start;         // 끝까지 남은 길이
                int part2 = blockSamples - part1;         // 처음부터 이어서 읽을 길이

                // 뒤쪽 조각(버퍼 끝)
                float[] tail = new float[part1];
                micClip.GetData(tail, start);
                System.Array.Copy(tail, 0, data, 0, part1);

                // 앞쪽 조각(버퍼 처음부터)
                float[] head = new float[part2];
                micClip.GetData(head, 0);
                System.Array.Copy(head, 0, data, part1, part2);
            }

            // WAV 직렬화(채널은 마이크 클립 채널 값 사용)
            int channels = Mathf.Max(1, micClip.channels);
            byte[] wav = FloatToWav(data, channels, sampleRate);

            // 업로드(비동기) — 녹음은 계속
            StartCoroutine(Upload(wav));

            // 다음 블록까지 대기
            yield return new WaitForSeconds(hopSeconds);
        }
    }

    IEnumerator Upload(byte[] body)
    {
        using (var uwr = new UnityWebRequest(uploadUrl, "POST"))
        {
            uwr.uploadHandler = new UploadHandlerRaw(body);
            uwr.downloadHandler = new DownloadHandlerBuffer();
            uwr.SetRequestHeader("Content-Type", "audio/wav");
            uwr.timeout = 30;

            yield return uwr.SendWebRequest();

            if (!IsOk(uwr))
                Debug.LogError($"[AUDIO UP] Error {uwr.responseCode} {uwr.error} | Body: {uwr.downloadHandler.text}");
            else
                Debug.Log($"[AUDIO UP] OK {uwr.responseCode} ({body.Length} bytes)");
        }
    }

    // ---------- float[] -> WAV(PCM16) ----------
    static byte[] FloatToWav(float[] samples, int channels, int sampleRate)
    {
        // 다채널 지원: data는 모노로 뽑았지만 channels>1인 기기 대비하여 헤더 일치
        // (필요하면 실제 다채널 합성 로직을 추가)
        byte[] pcm = new byte[samples.Length * 2];
        int o = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            short v = (short)Mathf.Clamp(Mathf.RoundToInt(samples[i] * 32767f), short.MinValue, short.MaxValue);
            pcm[o++] = (byte)(v & 0xFF);
            pcm[o++] = (byte)((v >> 8) & 0xFF);
        }
        return WriteWav(pcm, channels, sampleRate);
    }

    static byte[] WriteWav(byte[] pcmData, int channels, int sampleRate)
    {
        using (var ms = new MemoryStream())
        using (var bw = new BinaryWriter(ms))
        {
            int byteRate = sampleRate * channels * 2; // 16-bit PCM
            int subchunk2Size = pcmData.Length;
            int chunkSize = 36 + subchunk2Size;

            // RIFF
            bw.Write(Encoding.ASCII.GetBytes("RIFF"));
            bw.Write(chunkSize);
            bw.Write(Encoding.ASCII.GetBytes("WAVE"));

            // fmt
            bw.Write(Encoding.ASCII.GetBytes("fmt "));
            bw.Write(16);                  // PCM
            bw.Write((short)1);            // AudioFormat=1(PCM)
            bw.Write((short)channels);     // NumChannels
            bw.Write(sampleRate);          // SampleRate
            bw.Write(byteRate);            // ByteRate
            bw.Write((short)(channels * 2)); // BlockAlign
            bw.Write((short)16);           // BitsPerSample

            // data
            bw.Write(Encoding.ASCII.GetBytes("data"));
            bw.Write(subchunk2Size);
            bw.Write(pcmData);

            return ms.ToArray();
        }
    }
}
