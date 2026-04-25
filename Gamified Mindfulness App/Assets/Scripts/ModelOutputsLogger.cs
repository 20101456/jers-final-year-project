using UnityEngine;
using Unity.InferenceEngine;

public class ModelOutputsLogger : MonoBehaviour
{
    public ModelAsset modelAsset;

    void Start()
    {
        var model = ModelLoader.Load(modelAsset);
        Debug.Log($"Model has {model.outputs.Count} outputs:");
        foreach (var o in model.outputs)
            Debug.Log($"- {o.index}: {o.name}");
    }
}
