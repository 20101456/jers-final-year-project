using System.Collections.Generic;
using UnityEngine;
using Unity.InferenceEngine;

public class BlazeFaceIE : MonoBehaviour
{
    public ModelAsset blazeFace;
    public BackendType backend = BackendType.GPUCompute;
    [Range(0.1f, 0.99f)] public float scoreThreshold = 0.75f;

    Worker worker;
    Tensor<float> input;
    TextureTransform toNHWC128;

    const int InSize = 128;
    const int NumAnchors = 896;
    const int BoxStride = 16;

    Vector2[] anchors;

    RenderTexture rt128;

    public struct FaceDet
    {
        public float score;
        public Rect faceRect01;   // normalized 0..1 in model input space
        public Vector2[] kp01;    // 6 keypoints normalized 0..1
    }

    void Awake()
    {
        if (!SystemInfo.supportsComputeShaders && backend == BackendType.GPUCompute)
            backend = BackendType.CPU;

        var model = ModelLoader.Load(blazeFace);
        worker = new Worker(model, backend);

        input = new Tensor<float>(new TensorShape(1, InSize, InSize, 3));
        toNHWC128 = new TextureTransform()
            .SetDimensions(InSize, InSize, 3)
            .SetTensorLayout(TensorLayout.NHWC);

        anchors = BuildAnchors();

        rt128 = new RenderTexture(InSize, InSize, 0, RenderTextureFormat.ARGB32);
        rt128.Create();
    }

    public bool TryDetect(Texture src, out FaceDet det)
    {
        det = default;
        if (src == null) return false;

        Graphics.Blit(src, rt128);                 // pre-resize using a normal blit
        TextureConverter.ToTensor(rt128, input, toNHWC128);  // now sizes match (no resize path)
        worker.Schedule(input);

        // BlazeFace should have 2 outputs: boxes + scores.
        using var boxesCPU = (worker.PeekOutput(0) as Tensor<float>).ReadbackAndClone();
        using var scoresCPU = (worker.PeekOutput(1) as Tensor<float>).ReadbackAndClone();

        var boxesArr = boxesCPU.DownloadToArray();   // length = 896 * 16
        var scoresArr = scoresCPU.DownloadToArray(); // length = 896

        float best = -1f;
        int bestIdx = -1;

        for (int i = 0; i < NumAnchors; i++)
        {
            float logit = scoresArr[i];
            float p = 1f / (1f + Mathf.Exp(-logit)); // sigmoid
            if (p > best) { best = p; bestIdx = i; }
        }

        if (bestIdx < 0 || best < scoreThreshold) return false;

        int baseOff = bestIdx * BoxStride;

        float x = boxesArr[baseOff + 0] + anchors[bestIdx].x * InSize;
        float y = boxesArr[baseOff + 1] + anchors[bestIdx].y * InSize;
        float w = boxesArr[baseOff + 2];
        float h = boxesArr[baseOff + 3];

        det.score = best;
        det.faceRect01 = new Rect(
            (x - 0.5f * w) / InSize,
            (y - 0.5f * h) / InSize,
            w / InSize,
            h / InSize
        );

        det.kp01 = new Vector2[6];
        for (int k = 0; k < 6; k++)
        {
            float kx = boxesArr[baseOff + 4 + 2 * k + 0] + anchors[bestIdx].x * InSize;
            float ky = boxesArr[baseOff + 4 + 2 * k + 1] + anchors[bestIdx].y * InSize;
            det.kp01[k] = new Vector2(kx / InSize, ky / InSize);
        }

        return true;
    }

    Vector2[] BuildAnchors()
    {
        // 16×16×2 + 8×8×6 = 896 anchors
        var list = new List<Vector2>(NumAnchors);
        AddGrid(list, 16, 2);
        AddGrid(list, 8, 6);
        return list.ToArray();
    }

    void AddGrid(List<Vector2> list, int grid, int repeats)
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
        worker?.Dispose();
        if (rt128 != null) rt128.Release();
    }
}