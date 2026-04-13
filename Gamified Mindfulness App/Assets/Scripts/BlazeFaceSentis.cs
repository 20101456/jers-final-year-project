using UnityEngine;

public class BlazeFaceSentis : MonoBehaviour
{
    public Unity.InferenceEngine.ModelAsset blazeFaceOnnx;
    public Unity.InferenceEngine.BackendType backend = Unity.InferenceEngine.BackendType.CPU;

    [Header("Thresholds")]
    [Range(0.05f, 0.99f)] public float scoreThreshold = 0.35f;

    [Header("Debug")]
    public bool verboseLogging = true;
    public int logEveryNFrames = 30;

    SentisModelRunner runner;
    Unity.InferenceEngine.Tensor<float> input;
    Unity.InferenceEngine.TextureTransform toNHWC128;

    const int InSize = 128;
    const int NumAnchors = 896;
    const int BoxStride = 16;

    Vector2[] anchors;
    RenderTexture rt128;

    void Awake()
    {
        if (backend == Unity.InferenceEngine.BackendType.GPUCompute && !SystemInfo.supportsComputeShaders)
            backend = Unity.InferenceEngine.BackendType.CPU;

        runner = new SentisModelRunner(blazeFaceOnnx, backend);

        input = new Unity.InferenceEngine.Tensor<float>(
            new Unity.InferenceEngine.TensorShape(1, InSize, InSize, 3)
        );

        toNHWC128 = new Unity.InferenceEngine.TextureTransform()
            .SetDimensions(InSize, InSize, 3)
            .SetTensorLayout(Unity.InferenceEngine.TensorLayout.NHWC);

        anchors = BuildAnchors();

        rt128 = new RenderTexture(InSize, InSize, 0, RenderTextureFormat.ARGB32);
        rt128.filterMode = FilterMode.Bilinear;
        rt128.wrapMode = TextureWrapMode.Clamp;
        rt128.Create();

        Debug.Log($"BlazeFaceSentis Awake -> backend = {backend}");
    }

    public bool TryDetect(Texture src, out FaceDet det)
    {
        det = default;

        if (src == null || runner == null || input == null)
            return false;

        Graphics.Blit(src, rt128);
        Unity.InferenceEngine.TextureConverter.ToTensor(rt128, input, toNHWC128);

        runner.Worker.Schedule(input);

        var out0Raw = runner.Worker.PeekOutput(0) as Unity.InferenceEngine.Tensor<float>;
        var out1Raw = runner.Worker.PeekOutput(1) as Unity.InferenceEngine.Tensor<float>;

        if (out0Raw == null || out1Raw == null)
        {
            if (verboseLogging && Time.frameCount % logEveryNFrames == 0)
                Debug.LogWarning("BlazeFaceSentis: one or more outputs were null.");
            return false;
        }

        using var out0 = out0Raw.ReadbackAndClone();
        using var out1 = out1Raw.ReadbackAndClone();

        // Usually boxes has far more values than scores.
        bool out0IsBoxes = out0.count >= out1.count;

        var boxesArr = out0IsBoxes ? out0.DownloadToArray() : out1.DownloadToArray();
        var scoresArr = out0IsBoxes ? out1.DownloadToArray() : out0.DownloadToArray();

        if (verboseLogging && Time.frameCount % logEveryNFrames == 0)
        {
            Debug.Log(
                $"BlazeFaceSentis -> out0.count={out0.count}, out1.count={out1.count}, " +
                $"boxesLen={boxesArr.Length}, scoresLen={scoresArr.Length}"
            );
        }

        if (scoresArr == null || scoresArr.Length == 0 || boxesArr == null || boxesArr.Length == 0)
            return false;

        float best = -1f;
        int bestIdx = -1;

        int scoreCount = Mathf.Min(NumAnchors, scoresArr.Length);

        for (int i = 0; i < scoreCount; i++)
        {
            float p = ToProbability(scoresArr[i]);

            if (p > best)
            {
                best = p;
                bestIdx = i;
            }
        }

        if (verboseLogging && Time.frameCount % logEveryNFrames == 0)
            Debug.Log($"BlazeFaceSentis -> bestIdx={bestIdx}, bestScore={best:F4}, threshold={scoreThreshold:F2}");

        if (bestIdx < 0 || best < scoreThreshold)
            return false;

        int baseOff = bestIdx * BoxStride;

        if (baseOff + 15 >= boxesArr.Length || bestIdx >= anchors.Length)
        {
            if (verboseLogging && Time.frameCount % logEveryNFrames == 0)
                Debug.LogWarning("BlazeFaceSentis: output indexing exceeded expected bounds.");
            return false;
        }

        float x = boxesArr[baseOff + 0] + anchors[bestIdx].x * InSize;
        float y = boxesArr[baseOff + 1] + anchors[bestIdx].y * InSize;
        float w = boxesArr[baseOff + 2];
        float h = boxesArr[baseOff + 3];

        Rect r = new Rect(
            (x - 0.5f * w) / InSize,
            (y - 0.5f * h) / InSize,
            w / InSize,
            h / InSize
        );

        det.score = best;
        det.faceRect01 = ClampRect01(r);

        det.kp01 = new Vector2[6];
        for (int k = 0; k < 6; k++)
        {
            float kx = boxesArr[baseOff + 4 + 2 * k + 0] + anchors[bestIdx].x * InSize;
            float ky = boxesArr[baseOff + 4 + 2 * k + 1] + anchors[bestIdx].y * InSize;
            det.kp01[k] = new Vector2(kx / InSize, ky / InSize);
        }

        return true;
    }

    static float ToProbability(float v)
    {
        // If it's already a probability, keep it.
        if (v >= 0f && v <= 1f)
            return v;

        // Otherwise treat it like a logit.
        return 1f / (1f + Mathf.Exp(-v));
    }

    static Rect ClampRect01(Rect r)
    {
        float xMin = Mathf.Clamp01(r.xMin);
        float yMin = Mathf.Clamp01(r.yMin);
        float xMax = Mathf.Clamp01(r.xMax);
        float yMax = Mathf.Clamp01(r.yMax);

        return Rect.MinMaxRect(xMin, yMin, xMax, yMax);
    }

    Vector2[] BuildAnchors()
    {
        var list = new System.Collections.Generic.List<Vector2>(NumAnchors);

        AddGrid(list, 16, 2);
        AddGrid(list, 8, 6);

        return list.ToArray();
    }

    void AddGrid(System.Collections.Generic.List<Vector2> list, int grid, int repeats)
    {
        for (int y = 0; y < grid; y++)
        {
            for (int x = 0; x < grid; x++)
            {
                float cx = (x + 0.5f) / grid;
                float cy = (y + 0.5f) / grid;

                for (int r = 0; r < repeats; r++)
                    list.Add(new Vector2(cx, cy));
            }
        }
    }

    void OnDestroy()
    {
        input?.Dispose();
        runner?.Dispose();

        if (rt128 != null)
        {
            rt128.Release();
            Destroy(rt128);
        }
    }

    public struct FaceDet
    {
        public float score;
        public Rect faceRect01;
        public Vector2[] kp01;
    }
}