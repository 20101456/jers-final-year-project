using System.Collections.Generic;
using Unity.InferenceEngine;
using UnityEngine;

/// <summary>
/// Runs a BlazeFace ONNX model through Unity Inference Engine/Sentis
/// and returns a normalised face rectangle plus six facial keypoints.
///
/// This script is used by the front-camera eye tracking pipeline.
/// </summary>
public class BlazeFaceSentis : MonoBehaviour
{
    [Header("Model")]
    public ModelAsset blazeFaceOnnx;
    public BackendType backend = BackendType.CPU;

    [Header("Thresholds")]
    [Range(0.05f, 0.99f)] public float scoreThreshold = 0.35f;

    [Header("Debug")]
    public bool verboseLogging = false;
    [Min(1)] public int logEveryNFrames = 30;

    const int InputSize = 128;
    const int NumAnchors = 896;
    const int BoxStride = 16;

    SentisModelRunner runner;
    Tensor<float> input;
    TextureTransform toNHWC128;

    Vector2[] anchors;
    RenderTexture resizedInputRT;

    void Awake()
    {
        if (blazeFaceOnnx == null)
        {
            Debug.LogError("BlazeFaceSentis: No BlazeFace ONNX model assigned.");
            enabled = false;
            return;
        }

        if (backend == BackendType.GPUCompute && !SystemInfo.supportsComputeShaders)
        {
            Debug.LogWarning("BlazeFaceSentis: GPUCompute is not supported on this device. Falling back to CPU.");
            backend = BackendType.CPU;
        }

        runner = new SentisModelRunner(blazeFaceOnnx, backend);

        input = new Tensor<float>(
            new TensorShape(1, InputSize, InputSize, 3)
        );

        toNHWC128 = new TextureTransform()
            .SetDimensions(InputSize, InputSize, 3)
            .SetTensorLayout(TensorLayout.NHWC);

        anchors = BuildAnchors();

        resizedInputRT = CreateInputRenderTexture();

        if (ShouldLog())
            Debug.Log($"BlazeFaceSentis initialised with backend: {backend}");
    }

    public bool TryDetect(Texture sourceTexture, out FaceDet detection)
    {
        detection = default;

        if (!CanRun(sourceTexture))
            return false;

        RunModel(sourceTexture);

        Tensor<float> rawOutput0 = runner.Worker.PeekOutput(0) as Tensor<float>;
        Tensor<float> rawOutput1 = runner.Worker.PeekOutput(1) as Tensor<float>;

        if (rawOutput0 == null || rawOutput1 == null)
        {
            LogWarning("One or more model outputs were null.");
            return false;
        }

        using Tensor<float> output0 = rawOutput0.ReadbackAndClone();
        using Tensor<float> output1 = rawOutput1.ReadbackAndClone();

        GetBoxesAndScores(output0, output1, out float[] boxes, out float[] scores);

        if (boxes == null || boxes.Length == 0 || scores == null || scores.Length == 0)
            return false;

        if (ShouldLog())
        {
            Debug.Log(
                $"BlazeFaceSentis outputs -> " +
                $"out0.count={output0.count}, out1.count={output1.count}, " +
                $"boxesLen={boxes.Length}, scoresLen={scores.Length}"
            );
        }

        int bestIndex = FindBestScoreIndex(scores, out float bestScore);

        if (ShouldLog())
            Debug.Log($"BlazeFaceSentis best -> index={bestIndex}, score={bestScore:F4}, threshold={scoreThreshold:F2}");

        if (bestIndex < 0 || bestScore < scoreThreshold)
            return false;

        if (!TryDecodeDetection(boxes, bestIndex, bestScore, out detection))
            return false;

        return true;
    }

    bool CanRun(Texture sourceTexture)
    {
        return
            sourceTexture != null &&
            runner != null &&
            runner.Worker != null &&
            input != null &&
            resizedInputRT != null &&
            anchors != null &&
            anchors.Length == NumAnchors;
    }

    void RunModel(Texture sourceTexture)
    {
        Graphics.Blit(sourceTexture, resizedInputRT);
        TextureConverter.ToTensor(resizedInputRT, input, toNHWC128);
        runner.Worker.Schedule(input);
    }

    void GetBoxesAndScores(
        Tensor<float> output0,
        Tensor<float> output1,
        out float[] boxes,
        out float[] scores
    )
    {
        // The box output normally contains more values than the score output.
        bool output0IsBoxes = output0.count >= output1.count;

        boxes = output0IsBoxes
            ? output0.DownloadToArray()
            : output1.DownloadToArray();

        scores = output0IsBoxes
            ? output1.DownloadToArray()
            : output0.DownloadToArray();
    }

    int FindBestScoreIndex(float[] scores, out float bestScore)
    {
        bestScore = -1f;
        int bestIndex = -1;

        int scoreCount = Mathf.Min(NumAnchors, scores.Length);

        for (int i = 0; i < scoreCount; i++)
        {
            float probability = ToProbability(scores[i]);

            if (probability > bestScore)
            {
                bestScore = probability;
                bestIndex = i;
            }
        }

        return bestIndex;
    }

    bool TryDecodeDetection(float[] boxes, int anchorIndex, float score, out FaceDet detection)
    {
        detection = default;

        int baseOffset = anchorIndex * BoxStride;

        if (baseOffset + 15 >= boxes.Length || anchorIndex >= anchors.Length)
        {
            LogWarning("Output indexing exceeded expected BlazeFace bounds.");
            return false;
        }

        Vector2 anchor = anchors[anchorIndex];

        float centerX = boxes[baseOffset + 0] + anchor.x * InputSize;
        float centerY = boxes[baseOffset + 1] + anchor.y * InputSize;
        float width = boxes[baseOffset + 2];
        float height = boxes[baseOffset + 3];

        Rect faceRect = new Rect(
            (centerX - 0.5f * width) / InputSize,
            (centerY - 0.5f * height) / InputSize,
            width / InputSize,
            height / InputSize
        );

        detection.score = score;
        detection.faceRect01 = ClampRect01(faceRect);
        detection.kp01 = DecodeKeypoints(boxes, baseOffset, anchor);

        return true;
    }

    Vector2[] DecodeKeypoints(float[] boxes, int baseOffset, Vector2 anchor)
    {
        Vector2[] keypoints = new Vector2[6];

        for (int i = 0; i < keypoints.Length; i++)
        {
            float x = boxes[baseOffset + 4 + 2 * i + 0] + anchor.x * InputSize;
            float y = boxes[baseOffset + 4 + 2 * i + 1] + anchor.y * InputSize;

            keypoints[i] = new Vector2(x / InputSize, y / InputSize);
        }

        return keypoints;
    }

    static float ToProbability(float value)
    {
        // Some exported models return probabilities directly.
        if (value >= 0f && value <= 1f)
            return value;

        // Otherwise, treat the value as a logit.
        return 1f / (1f + Mathf.Exp(-value));
    }

    static Rect ClampRect01(Rect rect)
    {
        float xMin = Mathf.Clamp01(rect.xMin);
        float yMin = Mathf.Clamp01(rect.yMin);
        float xMax = Mathf.Clamp01(rect.xMax);
        float yMax = Mathf.Clamp01(rect.yMax);

        return Rect.MinMaxRect(xMin, yMin, xMax, yMax);
    }

    Vector2[] BuildAnchors()
    {
        List<Vector2> anchorList = new List<Vector2>(NumAnchors);

        AddAnchorGrid(anchorList, 16, 2);
        AddAnchorGrid(anchorList, 8, 6);

        if (anchorList.Count != NumAnchors)
            Debug.LogWarning($"BlazeFaceSentis: Expected {NumAnchors} anchors but built {anchorList.Count}.");

        return anchorList.ToArray();
    }

    void AddAnchorGrid(List<Vector2> anchorList, int gridSize, int repeatsPerCell)
    {
        for (int y = 0; y < gridSize; y++)
        {
            for (int x = 0; x < gridSize; x++)
            {
                float centerX = (x + 0.5f) / gridSize;
                float centerY = (y + 0.5f) / gridSize;

                for (int repeat = 0; repeat < repeatsPerCell; repeat++)
                    anchorList.Add(new Vector2(centerX, centerY));
            }
        }
    }

    RenderTexture CreateInputRenderTexture()
    {
        RenderTexture renderTexture = new RenderTexture(InputSize, InputSize, 0, RenderTextureFormat.ARGB32)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };

        renderTexture.Create();
        return renderTexture;
    }

    bool ShouldLog()
    {
        return verboseLogging && logEveryNFrames > 0 && Time.frameCount % logEveryNFrames == 0;
    }

    void LogWarning(string message)
    {
        if (ShouldLog())
            Debug.LogWarning($"BlazeFaceSentis: {message}");
    }

    void OnDestroy()
    {
        input?.Dispose();
        runner?.Dispose();

        if (resizedInputRT != null)
        {
            resizedInputRT.Release();
            Destroy(resizedInputRT);
            resizedInputRT = null;
        }
    }

    public struct FaceDet
    {
        public float score;
        public Rect faceRect01;
        public Vector2[] kp01;
    }
}