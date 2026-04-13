using UnityEngine;

public class EyeRegionTracker : MonoBehaviour
{
    public CameraFrameCorrector cameraFeed;
    public FaceDebugDraw faceDebug;

    [Header("Sampling")]
    [Range(8, 64)] public int sampleWidth = 24;
    [Range(4, 32)] public int sampleHeight = 12;
    [Range(0.01f, 0.2f)] public float sampleInterval = 0.033f;

    [Header("Blink Heuristic")]
    [Range(0.20f, 0.95f)] public float closedThreshold01 = 0.72f;
    [Range(0.30f, 0.99f)] public float reopenThreshold01 = 0.88f;
    [Range(1f, 30f)] public float smoothingSpeed = 12f;
    [Range(0.01f, 0.25f)] public float minBlinkClosedTime = 0.03f;
    [Range(0.05f, 0.60f)] public float maxBlinkClosedTime = 0.35f;
    [Range(0.01f, 0.30f)] public float blinkCooldown = 0.12f;
    [Range(0.0f, 0.30f)] public float minClosedDepthBelowThreshold = 0.04f;

    [Header("Calibration / Baseline")]
    [Range(0.2f, 3f)] public float calibrationDuration = 1.2f;
    public bool autoTrackOpenBaseline = true;
    [Range(0.05f, 5f)] public float baselineFallSpeed = 0.75f;
    [Range(0.001f, 0.2f)] public float minBaseline = 0.01f;

    [Header("Debug")]
    public bool showDebugOverlay = true;
    public bool showEyeSamplePreview = true;
    public bool logBlinkEvents = true;
    public bool logSampleFailures = true;
    [Range(16, 48)] public int debugFontSize = 28;

    public float LeftRaw { get; private set; }
    public float RightRaw { get; private set; }
    public float CombinedRaw { get; private set; }

    public float LeftNorm01 { get; private set; }
    public float RightNorm01 { get; private set; }
    public float Combined01 { get; private set; }

    public float LeftSmooth01 { get; private set; }
    public float RightSmooth01 { get; private set; }
    public float Smoothed01 { get; private set; }

    public float LeftBaseline { get; private set; }
    public float RightBaseline { get; private set; }
    public float OpenBaseline => 0.5f * (LeftBaseline + RightBaseline);

    public bool EyesClosed { get; private set; }
    public bool BlinkThisFrame { get; private set; }
    public int BlinkCount { get; private set; }
    public bool IsCalibrating { get; private set; }

    public bool LastSampleOk { get; private set; }
    public string LastSampleStatus { get; private set; } = "Waiting...";

    RenderTexture leftEyeRT;
    RenderTexture rightEyeRT;
    Texture2D leftEyeTex;
    Texture2D rightEyeTex;

    float nextSampleTime;
    float closedStartTime;
    float lastBlinkTime = -999f;
    float closedMinCombined01 = 1f;
    float trackingStartTime = -1f;

    GUIStyle debugTextStyle;

    void Awake()
    {
        if (cameraFeed == null)
            cameraFeed = FindFirstObjectByType<CameraFrameCorrector>();

        if (faceDebug == null)
            faceDebug = FindFirstObjectByType<FaceDebugDraw>();
    }

    void Start()
    {
        CreateBuffers();
    }

    void Update()
    {
        BlinkThisFrame = false;

        if (cameraFeed == null)
        {
            InvalidateTracking(false);
            LastSampleOk = false;
            LastSampleStatus = "cameraFeed missing";
            return;
        }

        if (faceDebug == null)
        {
            InvalidateTracking(false);
            LastSampleOk = false;
            LastSampleStatus = "faceDebug missing";
            return;
        }

        if (!faceDebug.HasEyeRegions)
        {
            InvalidateTracking(false);
            LastSampleOk = false;
            LastSampleStatus = "Eye regions not ready";
            return;
        }

        var src = cameraFeed.CorrectedRT;
        if (src == null)
        {
            InvalidateTracking(false);
            LastSampleOk = false;
            LastSampleStatus = "CorrectedRT is null";
            return;
        }

        if (Time.unscaledTime < nextSampleTime)
            return;

        nextSampleTime = Time.unscaledTime + sampleInterval;

        CreateBuffers();

        bool leftOk = SampleEye(
            src,
            faceDebug.LeftEyeRect01,
            leftEyeRT,
            leftEyeTex,
            out float leftScore,
            out string leftMsg
        );

        bool rightOk = SampleEye(
            src,
            faceDebug.RightEyeRect01,
            rightEyeRT,
            rightEyeTex,
            out float rightScore,
            out string rightMsg
        );

        if (!leftOk || !rightOk)
        {
            InvalidateTracking(false);
            LastSampleOk = false;
            LastSampleStatus = $"Sample failed | L:{leftMsg} | R:{rightMsg}";

            if (logSampleFailures)
                Debug.LogWarning(LastSampleStatus);

            return;
        }

        LastSampleOk = true;
        LastSampleStatus = "Sample OK";

        if (trackingStartTime < 0f)
            trackingStartTime = Time.unscaledTime;

        LeftRaw = leftScore;
        RightRaw = rightScore;
        CombinedRaw = 0.5f * (LeftRaw + RightRaw);

        UpdateBaselinesAndSignals();
        UpdateBlinkState();
    }

    void UpdateBaselinesAndSignals()
    {
        float trackedTime = Time.unscaledTime - trackingStartTime;
        IsCalibrating = trackedTime < calibrationDuration;

        float leftCandidate = Mathf.Max(LeftRaw, minBaseline);
        float rightCandidate = Mathf.Max(RightRaw, minBaseline);

        if (LeftBaseline <= 0f) LeftBaseline = leftCandidate;
        if (RightBaseline <= 0f) RightBaseline = rightCandidate;

        if (IsCalibrating)
        {
            LeftBaseline = Mathf.Max(LeftBaseline, leftCandidate);
            RightBaseline = Mathf.Max(RightBaseline, rightCandidate);
        }
        else if (autoTrackOpenBaseline && !EyesClosed)
        {
            if (leftCandidate > LeftBaseline)
                LeftBaseline = leftCandidate;
            else
                LeftBaseline = Mathf.Lerp(
                    LeftBaseline,
                    leftCandidate,
                    1f - Mathf.Exp(-baselineFallSpeed * sampleInterval)
                );

            if (rightCandidate > RightBaseline)
                RightBaseline = rightCandidate;
            else
                RightBaseline = Mathf.Lerp(
                    RightBaseline,
                    rightCandidate,
                    1f - Mathf.Exp(-baselineFallSpeed * sampleInterval)
                );
        }

        LeftBaseline = Mathf.Max(LeftBaseline, minBaseline);
        RightBaseline = Mathf.Max(RightBaseline, minBaseline);

        LeftNorm01 = Mathf.Clamp01(LeftRaw / LeftBaseline);
        RightNorm01 = Mathf.Clamp01(RightRaw / RightBaseline);
        Combined01 = 0.5f * (LeftNorm01 + RightNorm01);

        float smoothT = 1f - Mathf.Exp(-smoothingSpeed * sampleInterval);

        LeftSmooth01 = Mathf.Lerp(LeftSmooth01, LeftNorm01, smoothT);
        RightSmooth01 = Mathf.Lerp(RightSmooth01, RightNorm01, smoothT);
        Smoothed01 = 0.5f * (LeftSmooth01 + RightSmooth01);
    }

    void UpdateBlinkState()
    {
        if (IsCalibrating)
        {
            EyesClosed = false;
            return;
        }

        float weakerEye = Mathf.Min(LeftSmooth01, RightSmooth01);

        bool closedNow =
            Smoothed01 < closedThreshold01 &&
            weakerEye < (closedThreshold01 + 0.08f);

        bool reopenedNow =
            Smoothed01 > reopenThreshold01 &&
            weakerEye > (reopenThreshold01 - 0.14f);

        if (!EyesClosed)
        {
            if (closedNow)
            {
                EyesClosed = true;
                closedStartTime = Time.unscaledTime;
                closedMinCombined01 = Smoothed01;
            }
        }
        else
        {
            if (Smoothed01 < closedMinCombined01)
                closedMinCombined01 = Smoothed01;

            if (reopenedNow)
            {
                float closedDuration = Time.unscaledTime - closedStartTime;

                bool reachedRequiredDepth =
                    closedMinCombined01 <= (closedThreshold01 - minClosedDepthBelowThreshold);

                bool validBlink =
                    reachedRequiredDepth &&
                    closedDuration >= minBlinkClosedTime &&
                    closedDuration <= maxBlinkClosedTime &&
                    Time.unscaledTime - lastBlinkTime >= blinkCooldown;

                if (validBlink)
                {
                    BlinkCount++;
                    BlinkThisFrame = true;
                    lastBlinkTime = Time.unscaledTime;

                    if (logBlinkEvents)
                    {
                        Debug.Log(
                            $"Blink detected | count={BlinkCount} | duration={closedDuration:F3}s | " +
                            $"L={LeftSmooth01:F3} | R={RightSmooth01:F3} | minCombined={closedMinCombined01:F3}"
                        );
                    }
                }

                EyesClosed = false;
                closedMinCombined01 = 1f;
            }
        }
    }

    bool SampleEye(
        RenderTexture src,
        Rect eyeRect01TopLeft,
        RenderTexture dst,
        Texture2D dstTex,
        out float score,
        out string message)
    {
        score = 0f;
        message = "Unknown";

        if (src == null)
        {
            message = "src null";
            return false;
        }

        if (dst == null)
        {
            message = "dst null";
            return false;
        }

        if (dstTex == null)
        {
            message = "dstTex null";
            return false;
        }

        Rect r = ClampRect01(eyeRect01TopLeft);

        if (r.width <= 0f || r.height <= 0f)
        {
            message = "invalid rect";
            return false;
        }

        Vector2 scale = new Vector2(r.width, r.height);
        Vector2 offset = new Vector2(r.xMin, 1f - r.yMax);

        Graphics.Blit(src, dst, scale, offset);

        RenderTexture previous = RenderTexture.active;
        RenderTexture.active = dst;

        dstTex.ReadPixels(new Rect(0, 0, dst.width, dst.height), 0, 0, false);
        dstTex.Apply(false, false);

        RenderTexture.active = previous;

        Color32[] pixels = dstTex.GetPixels32();
        if (pixels == null || pixels.Length != dst.width * dst.height)
        {
            message = "pixel read failed";
            return false;
        }

        score = ComputeEyeOpennessScore(pixels, dst.width, dst.height);
        message = $"ok ({score:F6})";
        return true;
    }

    float ComputeEyeOpennessScore(Color32[] pixels, int width, int height)
    {
        if (pixels == null || pixels.Length < width * height || width < 2 || height < 2)
            return 0f;

        int x0 = Mathf.Clamp(Mathf.RoundToInt(width * 0.15f), 0, width - 1);
        int x1 = Mathf.Clamp(Mathf.RoundToInt(width * 0.85f), x0 + 1, width);

        int y0 = Mathf.Clamp(Mathf.RoundToInt(height * 0.20f), 0, height - 2);
        int y1 = Mathf.Clamp(Mathf.RoundToInt(height * 0.80f), y0 + 1, height - 1);

        float sum = 0f;
        int count = 0;

        for (int y = y0; y < y1; y++)
        {
            int row = y * width;
            int nextRow = (y + 1) * width;

            for (int x = x0; x < x1; x++)
            {
                float a = Luma(pixels[row + x]);
                float b = Luma(pixels[nextRow + x]);

                sum += Mathf.Abs(b - a);
                count++;
            }
        }

        if (count <= 0)
            return 0f;

        return sum / (255f * count);
    }

    float Luma(Color32 c)
    {
        return 0.299f * c.r + 0.587f * c.g + 0.114f * c.b;
    }

    Rect ClampRect01(Rect r)
    {
        float xMin = Mathf.Clamp01(r.xMin);
        float yMin = Mathf.Clamp01(r.yMin);
        float xMax = Mathf.Clamp01(r.xMax);
        float yMax = Mathf.Clamp01(r.yMax);
        return Rect.MinMaxRect(xMin, yMin, xMax, yMax);
    }

    void CreateBuffers()
    {
        bool recreate =
            leftEyeRT == null ||
            rightEyeRT == null ||
            leftEyeTex == null ||
            rightEyeTex == null ||
            leftEyeRT.width != sampleWidth ||
            leftEyeRT.height != sampleHeight ||
            rightEyeRT.width != sampleWidth ||
            rightEyeRT.height != sampleHeight;

        if (!recreate)
            return;

        ReleaseBuffers();

        leftEyeRT = new RenderTexture(sampleWidth, sampleHeight, 0, RenderTextureFormat.ARGB32);
        leftEyeRT.filterMode = FilterMode.Bilinear;
        leftEyeRT.wrapMode = TextureWrapMode.Clamp;
        leftEyeRT.Create();

        rightEyeRT = new RenderTexture(sampleWidth, sampleHeight, 0, RenderTextureFormat.ARGB32);
        rightEyeRT.filterMode = FilterMode.Bilinear;
        rightEyeRT.wrapMode = TextureWrapMode.Clamp;
        rightEyeRT.Create();

        leftEyeTex = new Texture2D(sampleWidth, sampleHeight, TextureFormat.RGBA32, false, false);
        rightEyeTex = new Texture2D(sampleWidth, sampleHeight, TextureFormat.RGBA32, false, false);
    }

    void InvalidateTracking(bool clearBlinkCount)
    {
        LeftRaw = 0f;
        RightRaw = 0f;
        CombinedRaw = 0f;

        LeftNorm01 = 0f;
        RightNorm01 = 0f;
        Combined01 = 0f;

        LeftSmooth01 = 0f;
        RightSmooth01 = 0f;
        Smoothed01 = 0f;

        LeftBaseline = 0f;
        RightBaseline = 0f;

        EyesClosed = false;
        BlinkThisFrame = false;
        IsCalibrating = false;

        trackingStartTime = -1f;
        closedMinCombined01 = 1f;

        if (clearBlinkCount)
            BlinkCount = 0;
    }

    void EnsureDebugStyle()
    {
        if (debugTextStyle != null) return;

        debugTextStyle = new GUIStyle(GUI.skin.label);
        debugTextStyle.fontSize = debugFontSize;
        debugTextStyle.normal.textColor = Color.white;
    }

    void OnGUI()
    {
        if (!showDebugOverlay)
            return;

        EnsureDebugStyle();

        string text =
            $"EyeTracker | SampleOk:{LastSampleOk} | {LastSampleStatus}\n" +
            $"LRaw:{LeftRaw:F6}  RRaw:{RightRaw:F6}  Raw:{CombinedRaw:F6}\n" +
            $"LNorm:{LeftNorm01:F3}  RNorm:{RightNorm01:F3}  Norm:{Combined01:F3}\n" +
            $"LSmooth:{LeftSmooth01:F3}  RSmooth:{RightSmooth01:F3}  Smooth:{Smoothed01:F3}\n" +
            $"LBase:{LeftBaseline:F6}  RBase:{RightBaseline:F6}  Calibrating:{IsCalibrating}\n" +
            $"Closed:{EyesClosed}  BlinkThisFrame:{BlinkThisFrame}  BlinkCount:{BlinkCount}";

        GUI.Label(new Rect(10, 50, 1700, 240), text, debugTextStyle);

        if (showEyeSamplePreview)
        {
            float w = sampleWidth * 8f;
            float h = sampleHeight * 8f;

            if (leftEyeTex != null)
                GUI.DrawTexture(new Rect(10, 240, w, h), leftEyeTex, ScaleMode.StretchToFill, false);

            if (rightEyeTex != null)
                GUI.DrawTexture(new Rect(30 + w, 240, w, h), rightEyeTex, ScaleMode.StretchToFill, false);
        }
    }

    void OnDestroy()
    {
        ReleaseBuffers();
    }

    void ReleaseBuffers()
    {
        if (leftEyeRT != null)
        {
            leftEyeRT.Release();
            Destroy(leftEyeRT);
            leftEyeRT = null;
        }

        if (rightEyeRT != null)
        {
            rightEyeRT.Release();
            Destroy(rightEyeRT);
            rightEyeRT = null;
        }

        if (leftEyeTex != null)
        {
            Destroy(leftEyeTex);
            leftEyeTex = null;
        }

        if (rightEyeTex != null)
        {
            Destroy(rightEyeTex);
            rightEyeTex = null;
        }
    }
}