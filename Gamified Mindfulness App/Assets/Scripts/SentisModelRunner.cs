using System;
using Unity.InferenceEngine;

/// <summary>
/// Small disposable wrapper around a Unity Inference Engine model and worker.
/// This keeps model loading and worker disposal separate from gameplay scripts.
/// </summary>
public sealed class SentisModelRunner : IDisposable
{
    public Model RuntimeModel { get; }
    public Worker Worker { get; }

    bool disposed;

    public SentisModelRunner(ModelAsset modelAsset, BackendType backend)
    {
        if (modelAsset == null)
            throw new ArgumentNullException(nameof(modelAsset));

        RuntimeModel = ModelLoader.Load(modelAsset);
        Worker = new Worker(RuntimeModel, backend);
    }

    public void Dispose()
    {
        if (disposed)
            return;

        Worker?.Dispose();
        disposed = true;
    }
}