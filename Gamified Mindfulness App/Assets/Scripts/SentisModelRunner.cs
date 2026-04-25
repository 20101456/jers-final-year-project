using System;
using UnityEngine;
using Unity.InferenceEngine;

public sealed class SentisModelRunner : IDisposable
{
    public Model RuntimeModel { get; }
    public Worker Worker { get; }

    public SentisModelRunner(ModelAsset modelAsset, BackendType backend)
    {
        RuntimeModel = ModelLoader.Load(modelAsset);
        Worker = new Worker(RuntimeModel, backend);
    }

    public void Dispose()
    {
        Worker?.Dispose();
    }
}