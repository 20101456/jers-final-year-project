
using UnityEngine;

public class BlazeFaceSentis : MonoBehaviour
{
    public Unity.InferenceEngine.ModelAsset blazeFaceOnnx;
    public Unity.InferenceEngine.BackendType backend = Unity.InferenceEngine.BackendType.GPUCompute;

    // thresholds
    [Range(0.1f, 0.99f)] public float scoreThreshold = 0.75f;

    SentisModelRunner runner;
    Unity.InferenceEngine.Tensor<float> input;
    Unity.InferenceEngine.TextureTransform toNHWC128;

    const int InSize = 128;
    const int NumAnchors = 896;
    const int BoxStride = 16;

    // Anchor centers only (x,y) in [0..1]
    Vector2[] anchors;

    RenderTexture rt128;

    void Awake()
    {
        if (!SystemInfo.supportsComputeShaders && backend == Unity.InferenceEngine.BackendType.GPUCompute)
            backend = Unity.InferenceEngine.BackendType.CPU; // safe fallback :contentReference[oaicite:12]{index=12}

        runner = new SentisModelRunner(blazeFaceOnnx, backend);

        input = new Unity.InferenceEngine.Tensor<float>(new Unity.InferenceEngine.TensorShape(1, InSize, InSize, 3));
        toNHWC128 = new Unity.InferenceEngine.TextureTransform()
            .SetDimensions(InSize, InSize, 3)
            .SetTensorLayout(Unity.InferenceEngine.TensorLayout.NHWC);

        anchors = BuildAnchors();

        rt128 = new UnityEngine.RenderTexture(InSize, InSize, 0, RenderTextureFormat.ARGB32);
        rt128.Create();
    }

    // Call this each frame with your camera texture
    public bool TryDetect(Texture src, out FaceDet det)
    {
        det = default;
        if (src == null) return false;

        // No per-frame tensor allocations :contentReference[oaicite:13]{index=13}
        Graphics.Blit(src, rt128);
        Unity.InferenceEngine.TextureConverter.ToTensor(rt128, input, toNHWC128);

        runner.Worker.Schedule(input);

        // Peek outputs (you might need to swap indices depending on the model import)
        using var boxes = (runner.Worker.PeekOutput(0) as Unity.InferenceEngine.Tensor<float>).ReadbackAndClone();
        using var scores = (runner.Worker.PeekOutput(1) as Unity.InferenceEngine.Tensor<float>).ReadbackAndClone();

        var boxesArr = boxes.DownloadToArray();   // Sentis 2.x API :contentReference[oaicite:14]{index=14}
        var scoresArr = scores.DownloadToArray();

        float best = -1f;
        int bestIdx = -1;

        for (int i = 0; i < NumAnchors; i++)
        {
            // scores is (1,896,1)
            float logit = scoresArr[i];
            // Many BlazeFace conversions treat scores as logits; sigmoid is typical
            float p = 1f / (1f + Mathf.Exp(-logit));

            if (p > best)
            {
                best = p;
                bestIdx = i;
            }
        }

        if (bestIdx < 0 || best < scoreThreshold) return false;

        // boxes is (1,896,16)
        int baseOff = bestIdx * BoxStride;

        float x = boxesArr[baseOff + 0] + anchors[bestIdx].x * InSize;
        float y = boxesArr[baseOff + 1] + anchors[bestIdx].y * InSize;
        float w = boxesArr[baseOff + 2];
        float h = boxesArr[baseOff + 3];

        // Convert to normalized Rect in src space (because we fed a stretched 128×128 view)
        det.score = best;
        det.faceRect01 = new Rect(
            (x - 0.5f * w) / InSize,
            (y - 0.5f * h) / InSize,
            w / InSize,
            h / InSize
        );

        // 6 keypoints: (x,y)*6 starting at index 4
        det.kp01 = new Vector2[6];
        for (int k = 0; k < 6; k++)
        {
            float kx = boxesArr[baseOff + 4 + 2 * k + 0] + anchors[bestIdx].x * InSize;
            float ky = boxesArr[baseOff + 4 + 2 * k + 1] + anchors[bestIdx].y * InSize;
            det.kp01[k] = new Vector2(kx / InSize, ky / InSize);
        }

        return true;
    }

    // BlazeFace has 896 anchors: 16×16×2 + 8×8×6 = 896
    Vector2[] BuildAnchors()
    {
        var list = new System.Collections.Generic.List<Vector2>(NumAnchors);

        // grid 16x16, 2 anchors per cell
        AddGrid(list, 16, 2);
        // grid 8x8, 6 anchors per cell
        AddGrid(list, 8, 6);

        return list.ToArray();
    }

    void AddGrid(System.Collections.Generic.List<Vector2> list, int grid, int repeats)
    {
        for (int y = 0; y < grid; y++)
            for (int x = 0; x < grid; x++)
            {
                float cx = (x + 0.5f) / grid;
                float cy = (y + 0.5f) / grid;
                for (int r = 0; r < repeats; r++)
                    list.Add(new Vector2(cx, cy));
            }
    }

    void OnDestroy()
    {
        input?.Dispose();
        runner?.Dispose();
        if (rt128 != null) rt128.Release();
    }

    public struct FaceDet
    {
        public float score;
        public Rect faceRect01;      // normalized in camera texture space
        public Vector2[] kp01;       // 6 normalized keypoints
    }
}