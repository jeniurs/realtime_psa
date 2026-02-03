using LSL;   // LSL.cs 안의 네임스페이스
using UnityEngine;

public class LSLStreamLister : MonoBehaviour
{
    void Start()
    {
        Debug.Log("[LSL] Resolving streams for 1 seconds...");

        // Python의 resolve_streams(wait_time=3.0)이랑 같은 기능
        var streams = liblsl.resolve_streams(1.0);
        Debug.Log($"[LSL] Found {streams.Length} streams");

        foreach (var info in streams)
        {
            string name = info.name();
            string type = info.type();
            string host = info.hostname();
            int ch = info.channel_count();
            double srate = info.nominal_srate();

            Debug.Log($"[LSL] name={name}, type={type}, host={host}, " +
                      $"channels={ch}, srate={srate}");
        }
    }
}
