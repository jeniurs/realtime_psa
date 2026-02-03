using Jint;   // Jint 네임스페이스
using System.IO;
using UnityEngine;

public class JsRunner : MonoBehaviour
{
    // JS 파일의 절대 경로 (인스펙터에서 수정 가능)
    public string jsFilePath = @"C:\Users\JM\node-dss\server_multimodal.js";

    private Engine engine;

    void Start()
    {
        // 1) JS 코드 파일 읽기
        if (!File.Exists(jsFilePath))
        {
            Debug.LogError("[JsRunner] JS file not found: " + jsFilePath);
            return;
        }

        string jsCode = File.ReadAllText(jsFilePath);
        Debug.Log("[JsRunner] Loaded JS file: " + jsFilePath);

        // 2) Jint 엔진 생성 후 코드 실행
        engine = new Engine();
        engine.Execute(jsCode);

        // 3) JS 함수 호출 예시: add(3, 5)
        var resultAdd = engine.Invoke("add", 3, 5).AsNumber();
        Debug.Log("JS add(3,5) = " + resultAdd);

        // 4) JS 함수 호출 예시: hello("JM")
        var resultHello = engine.Invoke("hello", "JM").AsString();
        Debug.Log("JS hello = " + resultHello);
    }
}
