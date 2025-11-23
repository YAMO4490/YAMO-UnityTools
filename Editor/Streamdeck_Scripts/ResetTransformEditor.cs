// Assets/Editor/StreamDeckTools/ResetTransformEditor.cs

#if UNITY_EDITOR
#if YAMO_STREAMDECK

using UnityEngine;
using UnityEditor;
using F10.StreamDeckIntegration;
using F10.StreamDeckIntegration.Attributes;

[InitializeOnLoad]
public static class StreamDeckEditorTools
{
    // Unity가 로드될 때 자동으로 StreamDeck에 이 클래스를 등록
    static StreamDeckEditorTools()
    {
        // static 클래스 등록 (한 번 등록되면 계속 사용)
        StreamDeck.AddStatic(typeof(StreamDeckEditorTools));
    }

    // ▶ 스트림덱에서 호출할 메서드
    [StreamDeckButton("ResetTransform")]
    public static void ResetSelectedTransform()
    {
        Transform t = Selection.activeTransform;
        if (t == null)
        {
            Debug.LogWarning("[StreamDeck] 선택된 오브젝트가 없습니다.");
            return;
        }

        Undo.RecordObject(t, "Reset Transform");

        t.localPosition = Vector3.zero;
        t.localRotation = Quaternion.identity;
        // 필요하면 스케일도 초기화
        // t.localScale = Vector3.one;

        Debug.Log($"[StreamDeck] Reset Transform: {t.name}");
    }
}

#endif // YAMO_STREAMDECK
#endif // UNITY_EDITOR
