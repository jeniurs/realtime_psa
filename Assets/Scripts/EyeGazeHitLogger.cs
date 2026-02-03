using System.IO;
using UnityEngine;
using MixedReality.Toolkit.Input;

public class EyeGazeHitLogger : MonoBehaviour
{
    [SerializeField] private FuzzyGazeInteractor fuzzy;
    private StreamWriter writer;

    void Start()
    {
        try
        {
            string path = Path.Combine(UnityEngine.Application.persistentDataPath, "gaze_hits.csv");
            Debug.Log($"[EyeGazeHitLogger] Will write to: {path}");
            writer = new StreamWriter(path, append: false);
            writer.WriteLine("t,ox,oy,oz,dx,dy,dz,hx,hy,hz,targetName,targetPosX,targetPosY,targetPosZ");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[EyeGazeHitLogger] Failed to initialize file writer: {e.Message}");
            writer = null;
        }
    }

    void Update()
    {
        // fuzzy가 null이거나 유효하지 않으면 리턴
        if (fuzzy == null || writer == null)
            return;

        var hitResult = fuzzy.PreciseHitResult;
        var interactable = hitResult.targetInteractable;

        if (interactable == null)
            return;

        RaycastHit hit = hitResult.raycastHit;

        Vector3 origin = fuzzy.transform.position;
        Vector3 dir = fuzzy.transform.forward;
        Vector3 hitPos = hit.point;
        Vector3 targetPos = interactable.transform.position;

        float t = Time.time;

        writer.WriteLine(string.Format(
            "{0},{1},{2},{3},{4},{5},{6},{7},{8},{9},{10},{11},{12},{13}",
            t,
            origin.x, origin.y, origin.z,
            dir.x, dir.y, dir.z,
            hitPos.x, hitPos.y, hitPos.z,
            interactable.transform.name,
            targetPos.x, targetPos.y, targetPos.z
        ));
    }

    void OnDestroy()
    {
        writer?.Flush();
        writer?.Close();
    }
}
