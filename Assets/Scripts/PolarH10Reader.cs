using LSL;
using UnityEngine;

public class PolarH10Reader : MonoBehaviour
{
    private liblsl.StreamInlet hrInlet;
    private liblsl.StreamInlet rrInlet;

    public float heartRate;      // bpm
    public float rrInterval_ms;  // ms

    // re-resolve용 변수들
    public float resolveInterval = 1.0f;   // 몇 초마다 다시 찾을지
    public float resolveTimeout = 30.0f;  // 최대 몇 초 동안 시도할지

    private float resolveTimer = 0f;
    private float totalResolveT = 0f;
    private bool gaveUp = false;

    void Start()
    {
        Debug.Log("[PolarH10Reader] Will resolve LSL streams of type 'ExciteOMeter' until found.");
    }

    void Update()
    {
        // 아직 HR/RR 스트림을 못 잡았으면: 일정 간격으로 계속 re-resolve
        if ((hrInlet == null || rrInlet == null) && !gaveUp)
        {
            resolveTimer += Time.deltaTime;
            totalResolveT += Time.deltaTime;

            if (totalResolveT > resolveTimeout)
            {
                gaveUp = true;
                Debug.LogError("[PolarH10Reader] Gave up resolving streams after "
                               + totalResolveT + " seconds.");
                return;
            }

            // resolveInterval 마다 한 번씩만 네트워크 검색
            if (resolveTimer >= resolveInterval)
            {
                resolveTimer = 0f;
                TryResolveStreams();
            }

            // 아직 스트림 못 잡았으면 여기서 끝 (HR 읽기 안 함)
            return;
        }

        // 여기까지 왔으면 두 inlet 다 연결된 상태 → 샘플 읽기
        ReadSamples();
    }

    private void TryResolveStreams()
    {
        Debug.Log("[PolarH10Reader] Resolving streams of type 'ExciteOMeter' ...");

        // Python에서 확인한 것처럼 type=ExciteOMeter 인 것들을 다 모아온다
        var infos = liblsl.resolve_stream("type", "ExciteOMeter", 4, 1.0);

        Debug.Log($"[PolarH10Reader] Found {infos.Length} streams this round.");
        foreach (var info in infos)
        {
            Debug.Log($"[PolarH10Reader] info.name={info.name()}, type={info.type()}, src={info.source_id()}");
        }

        foreach (var info in infos)
        {
            if (hrInlet == null && info.name() == "HeartRate")
            {
                hrInlet = new liblsl.StreamInlet(info);
                Debug.Log("[PolarH10Reader] Connected HR stream.");
            }
            else if (rrInlet == null && info.name() == "RRinterval")
            {
                rrInlet = new liblsl.StreamInlet(info);
                Debug.Log("[PolarH10Reader] Connected RR stream.");
            }
        }

        if (hrInlet != null && rrInlet != null)
        {
            Debug.Log("[PolarH10Reader] All streams connected ✅");
        }
    }

    private void ReadSamples()
    {
        if (hrInlet != null)
        {
            float[] sample = new float[1];
            double ts = hrInlet.pull_sample(sample, 0.0);
            if (ts != 0.0)
                heartRate = sample[0];
        }

        if (rrInlet != null)
        {
            float[] sample = new float[1];
            double ts = rrInlet.pull_sample(sample, 0.0);
            if (ts != 0.0)
                rrInterval_ms = sample[0];
        }
    }
}
