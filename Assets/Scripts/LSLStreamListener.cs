using LSL;  // EoM에서 쓰는 LSL 네임스페이스
using UnityEngine;

public class LSLStreamListener : MonoBehaviour
{
    void Start()
    {
        Debug.Log("LSLStreamLister: resolving streams...");

        // 여기! LSL.LSL 이 아니라 liblsl 사용
        var infos = liblsl.resolve_streams(15.0);

        Debug.Log($"LSLStreamListener: Found {infos.Length} streams.");
        foreach (var info in infos)
        {
            Debug.Log(
                $"Stream: name={info.name()}, " +
                $"type={info.type()}, " +
                $"channels={info.channel_count()}, " +
                $"source_id={info.source_id()}, " +
                $"host={info.hostname()}"
            );
        }
    }
}
