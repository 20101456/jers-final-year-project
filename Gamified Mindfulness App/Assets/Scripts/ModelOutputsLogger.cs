using UnityEngine;
using Unity.InferenceEngine;

/// <summary>
/// Small debug utility for inspecting the output names and indices of an ONNX model.
/// Not required during normal gameplay.
/// </summary>
public class ModelOutputsLogger : MonoBehaviour
{
    public ModelAsset modelAsset;

    void Start()
    {
        if (modelAsset == null)
        {
            Debug.LogWarning("ModelOutputsLogger: No model asset assigned.");
            return;
        }

        Model model = ModelLoader.Load(modelAsset);

        Debug.Log($"ModelOutputsLogger: Model has {model.outputs.Count} outputs:");

        foreach (Model.Output output in model.outputs)
            Debug.Log($"- {output.index}: {output.name}");
    }
}
