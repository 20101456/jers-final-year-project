using UnityEngine;

public class EyeRegionTracker : MonoBehaviour
{
    public CameraFrameCorrector cameraFeed;
    public FaceDebugDraw faceDebug;

    [Header("Sampling")]
    [Range(8, 64)] public int sampleWidth = 32;
    [Range(4, 32)] public int sampleHeight = 14;
    [Range(0.01f, 0.2f)] public float sampleInterval = 0.033f;

    [Header("Eye Crop Adjustment")]
    [Tooltip("Widen the eye crop so the inner and outer corners are included.")]
    [Range(0.5f, 3f)] public float eyeRectWidthMultiplier = 1.55f;

    [Tooltip("Reduce this if the crop shows too much cheek/cheekbone.")]
    [Range(0.3f, 2f)] public float eyeRectHeightMultiplier = 0.65f;

    [Tooltip("Negative moves the crop upward. Positive moves it downward.")]
    [Range(-0.1f, 0.1f)] public float eyeRectYOffset = -0.015f;

    [Header("Blink Detection")]
    [Tooltip("Keep eyes open during this startup period so the open-eye baseline can be learned.")]
    [Range(0.2f, 3f)] public float calibrationDuration = 1.0f;

    [Tooltip("Normalised eye openness below this counts as closed.")]
    [Range(0.2f, 0.95f)] public float closedThreshold01 = 0.70f;

    [Tooltip("Normalised eye openness above this counts as open again.")]
    [Range(0.3f, 1f)] public float reopenThreshold01 = 0.86f;

    [Tooltip("How quickly the openness value smooths. Higher = faster response.")]
    [Range(1f, 40f)] public float smoothingSpeed = 18f;

    [Tooltip("A blink shorter than this is ignored.")]
    [Range(0.01f, 0.25f)] public float minBlinkClosedTime = 0.04f;

    [Tooltip("A blink longer than this is treated as a long eye closure, not a normal blink.")]
    [Range(0.05f, 1f)] public float maxBlinkClosedTime = 0.45f;

    [Tooltip("Prevents one blink being counted multiple times.")]
    [Range(0.05f, 0.5f)] public float blinkCooldown = 0.18f;

    [Tooltip("The eyes must be open/stable before another blink can be counted.")]
    [Range(0.02f, 0.5f)] public float minOpenStableTime = 0.12f;

    [Tooltip("Safer for this project. A blink should affect both eyes, while head movement often affects one crop more than the other.")]
    public bool requireBothEyesClosed = true;

    [Header("Open Baseline Tracking")]
    [Range(0.001f, 0.2f)] public float minBaseline = 0.01f;

    [Tooltip("How quickly the open-eye baseline rises when a stronger open-eye sample is seen.")]
    [Range(1f, 20f)] public float baselineRiseSpeed = 8f;

    [Tooltip("How slowly the open-eye baseline can fall while eyes are open. Lower is safer.")]
    [Range(0.01f, 2f)] public float baselineFallSpeed = 0.15f;

    [Header("Motion Guard")]
    public bool useMotionGuard = true;

    [Tooltip("If eye rectangles move too much between samples, ignore blink detection briefly.")]
    [Range(0.05f, 0.6f)] public float rectMoveRejectFrac = 0.25f;

    [Tooltip("If eye rectangles change size too much between samples, ignore blink detection briefly.")]
    [Range(0.05f, 0.6f)] public float rectSizeRejectFrac = 0.25f;

    [Range(0.01f, 0.4f)] public float motionSuppressTime = 0.12f;

    [Header("Debug")]
    public bool showDebugOverlay = true;
    public bool showEyeSamplePreview = true;
    public bool logBlinkEvents = false;
    [Range(16, 48)] public int debugFontSize = 26;

    public float LeftRaw { get; private set; }
    public float RightRaw { get; private set; }
    public float CombinedRaw { get; private set; }

    public float LeftBaseline { get; private set; }
    public float RightBaseline { get; private set; }
    public float OpenBaseline => 0.5f * (LeftBaseline + RightBaseline);

    public float LeftNorm01 { get; private set; }
    public float RightNorm01 { get; private set; }
    public float Combined01 { get; private set; }

    public float LeftSmooth01 { get; private set; }
    public float RightSmooth01 { get; private set; }
    public float Smoothed01 { get; private set; }

    public float StrongerEye01 { get; private set; }
    public float WeakerEye01 { get; private set; }
    public float ClosureDepth01 { get; private set; }
    public float LeftChange01 { get; private set; }
    public float RightChange01 { get; private set; }

    public bool EyesClosed { get; private set; }
    public bool BlinkThisFrame { get; private set; }
    public int BlinkCount { get; private set; }
    public bool IsCalibrating { get; private set; }

    public bool LastSampleOk { get; private set; }
    public string LastSampleStatus { get; private set; } = "Waiting...";
    public string LastBlinkDecision { get; private set; } = "None";

    public bool MotionSuppressed { get; private set; }
    public float OpenStableAge { get; private set; }
    public bool BlinkArmed { get; private set; }
    public float CandidateAge { get; private set; }
    public float ClosedMinCombined01 => closedMinCombined01;
    public string BlinkPhaseName => phase.ToString();

    // Kept as harmless compatibility stubs in case other scripts/debug UI referenced the old version.
    public bool BaselineRecenterActive { get; private set; }
    public bool TemplateReady { get; private set; }
    public bool TemplateRecenterActive { get; private set; }
    public float TemplateDiff01 { get; private set; }
    public float LeftTemplateDiff01 { get; private set; }
    public float RightTemplateDiff01 { get; private set; }

    enum BlinkPhase
    {
        Idle,
        Closed
    }

    BlinkPhase phase = BlinkPhase.Idle;

    RenderTexture leftEyeRT;
    RenderTexture rightEyeRT;
    Texture2D leftEyeTex;
    Texture2D rightEyeTex;

    float nextSampleTime;
    float trackingStartTime = -1f;
    float lastGoodSampleTime = -999f;
    float lostTrackingResetDelay = 0.35f;

    float closedStartTime;
    float lastBlinkTime = -999f;
    float closedMinCombined01 = 1f;

    float openStableStartTime = -1f;
    float suppressBlinkUntil = -999f;

    Rect prevLeftEyeRect01;
    Rect prevRightEyeRect01;
    bool havePrevEyeRects;

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
            HandleTrackingLoss("cameraFeed missing");
            return;
        }

        if (faceDebug == null)
        {
            HandleTrackingLoss("faceDebug missing");
            return;
        }

        if (!faceDebug.HasEyeRegions)
        {
            HandleTrackingLoss("Eye regions not ready");
            return;
        }

        RenderTexture src = cameraFeed.CorrectedRT;

        if (src == null)
        {
            HandleTrackingLoss("CorrectedRT is null");
            return;
        }

        if (Time.unscaledTime < nextSampleTime)
            return;

        nextSampleTime = Time.unscaledTime + sampleInterval;
        CreateBuffers();

        Rect leftRect = AdjustEyeRect(faceDebug.LeftEyeRect01);
        Rect rightRect = AdjustEyeRect(faceDebug.RightEyeRect01);

        if (UpdateMotionSuppression(leftRect, rightRect))
        {
            LastSampleOk = true;
            LastSampleStatus = "Sample OK | motion ignored";
            LastBlinkDecision = "Ignoring head/camera movement";
            return;
        }

        bool leftOk = SampleEye(src, leftRect, leftEyeRT, leftEyeTex, out float leftScore, out string leftMsg);
        bool rightOk = SampleEye(src, rightRect, rightEyeRT, rightEyeTex, out float rightScore, out string rightMsg);

        if (!leftOk || !rightOk)
        {
            HandleTrackingLoss($"Sample failed | L:{leftMsg} | R:{rightMsg}");
            return;
        }

        LastSampleOk = true;
        LastSampleStatus = "Sample OK";
        lastGoodSampleTime = Time.unscaledTime;

        if (trackingStartTime < 0f)
            trackingStartTime = Time.unscaledTime;

        LeftRaw = leftScore;
        RightRaw = rightScore;
        CombinedRaw = 0.5f * (LeftRaw + RightRaw);

        UpdateCalibrationAndSignals();
        UpdateBlinkState();
        UpdateOpenBaseline();
    }

    Rect AdjustEyeRect(Rect r)
    {
        r = ClampRect01(r);

        Vector2 c = r.center;
        c.y += eyeRectYOffset;

        float w = r.width * eyeRectWidthMultiplier;
        float h = r.height * eyeRectHeightMultiplier;

        Rect adjusted = new Rect(c.x - w * 0.5f, c.y - h * 0.5f, w, h);
        return ClampRect01(adjusted);
    }

    void UpdateCalibrationAndSignals()
    {
        float trackedTime = Time.unscaledTime - trackingStartTime;
        IsCalibrating = trackedTime < calibrationDuration;

        float leftCandidate = Mathf.Max(LeftRaw, minBaseline);
        float rightCandidate = Mathf.Max(RightRaw, minBaseline);

        if (LeftBaseline <= 0f)
            LeftBaseline = leftCandidate;

        if (RightBaseline <= 0f)
            RightBaseline = rightCandidate;

        if (IsCalibrating)
        {
            float t = 1f - Mathf.Exp(-8f * sampleInterval);
            LeftBaseline = Mathf.Lerp(LeftBaseline, leftCandidate, t);
            RightBaseline = Mathf.Lerp(RightBaseline, rightCandidate, t);
        }

        ComputeSignals();
    }

    void ComputeSignals()
    {
        LeftBaseline = Mathf.Max(LeftBaseline, minBaseline);
        RightBaseline = Mathf.Max(RightBaseline, minBaseline);

        LeftNorm01 = Mathf.Clamp01(LeftRaw / LeftBaseline);
        RightNorm01 = Mathf.Clamp01(RightRaw / RightBaseline);
        Combined01 = 0.5f * (LeftNorm01 + RightNorm01);

        float smoothT = 1f - Mathf.Exp(-smoothingSpeed * sampleInterval);

        if (LeftSmooth01 <= 0f)
            LeftSmooth01 = LeftNorm01;
        else
            LeftSmooth01 = Mathf.Lerp(LeftSmooth01, LeftNorm01, smoothT);

        if (RightSmooth01 <= 0f)
            RightSmooth01 = RightNorm01;
        else
            RightSmooth01 = Mathf.Lerp(RightSmooth01, RightNorm01, smoothT);

        Smoothed01 = 0.5f * (LeftSmooth01 + RightSmooth01);

        StrongerEye01 = Mathf.Max(LeftSmooth01, RightSmooth01);
        WeakerEye01 = Mathf.Min(LeftSmooth01, RightSmooth01);
        ClosureDepth01 = 1f - Smoothed01;
        LeftChange01 = 1f - LeftSmooth01;
        RightChange01 = 1f - RightSmooth01;
    }

    void UpdateBlinkState()
    {
        CandidateAge = 0f;

        if (IsCalibrating)
        {
            ResetBlinkPhaseOnly();
            LastBlinkDecision = "Calibrating - keep eyes open";
            return;
        }

        bool leftClosed = LeftSmooth01 <= closedThreshold01;
        bool rightClosed = RightSmooth01 <= closedThreshold01;

        bool closedNow = requireBothEyesClosed
            ? leftClosed && rightClosed
            : Smoothed01 <= closedThreshold01 && (leftClosed || rightClosed);

        bool leftOpen = LeftSmooth01 >= reopenThreshold01;
        bool rightOpen = RightSmooth01 >= reopenThreshold01;

        bool openNow = requireBothEyesClosed
            ? leftOpen && rightOpen
            : Smoothed01 >= reopenThreshold01;

        if (openNow && !MotionSuppressed)
        {
            if (openStableStartTime < 0f)
                openStableStartTime = Time.unscaledTime;

            OpenStableAge = Time.unscaledTime - openStableStartTime;

            if (OpenStableAge >= minOpenStableTime)
                BlinkArmed = true;
        }
        else
        {
            openStableStartTime = -1f;
            OpenStableAge = 0f;
        }

        bool inCooldown = Time.unscaledTime - lastBlinkTime < blinkCooldown;

        switch (phase)
        {
            case BlinkPhase.Idle:
                {
                    EyesClosed = false;
                    closedMinCombined01 = 1f;

                    if (inCooldown)
                    {
                        LastBlinkDecision = "Cooldown";
                        return;
                    }

                    if (!BlinkArmed)
                    {
                        LastBlinkDecision =
                            $"Waiting for open eyes | L:{LeftSmooth01:F2} R:{RightSmooth01:F2}";
                        return;
                    }

                    if (closedNow)
                    {
                        phase = BlinkPhase.Closed;
                        EyesClosed = true;
                        BlinkArmed = false;
                        closedStartTime = Time.unscaledTime;
                        closedMinCombined01 = Smoothed01;

                        LastBlinkDecision =
                            $"Eyes closed | L:{LeftSmooth01:F2} R:{RightSmooth01:F2}";
                        return;
                    }

                    LastBlinkDecision =
                        $"Watching | L:{LeftSmooth01:F2} R:{RightSmooth01:F2}";
                    return;
                }

            case BlinkPhase.Closed:
                {
                    EyesClosed = true;
                    CandidateAge = Time.unscaledTime - closedStartTime;
                    closedMinCombined01 = Mathf.Min(closedMinCombined01, Smoothed01);

                    if (openNow)
                    {
                        float closedDuration = Time.unscaledTime - closedStartTime;

                        bool validBlink =
                            closedDuration >= minBlinkClosedTime &&
                            closedDuration <= maxBlinkClosedTime &&
                            !inCooldown;

                        if (validBlink)
                        {
                            BlinkCount++;
                            BlinkThisFrame = true;
                            lastBlinkTime = Time.unscaledTime;

                            LastBlinkDecision = $"Blink accepted ({closedDuration:F2}s)";

                            if (logBlinkEvents)
                                Debug.Log($"Blink detected | count={BlinkCount} | duration={closedDuration:F3}s");
                        }
                        else if (closedDuration < minBlinkClosedTime)
                        {
                            LastBlinkDecision = $"Rejected too fast ({closedDuration:F2}s)";
                        }
                        else
                        {
                            LastBlinkDecision = $"Rejected too long ({closedDuration:F2}s)";
                        }

                        ResetBlinkPhaseOnly();
                        return;
                    }

                    if (CandidateAge > maxBlinkClosedTime)
                    {
                        LastBlinkDecision = "Rejected: eyes held closed too long";
                        ResetBlinkPhaseOnly();
                        return;
                    }

                    LastBlinkDecision =
                        $"Closed holding | {CandidateAge:F2}s";
                    return;
                }
        }
    }

    void UpdateOpenBaseline()
    {
        if (IsCalibrating || MotionSuppressed || phase != BlinkPhase.Idle || EyesClosed)
            return;

        bool confidentlyOpen =
            BlinkArmed &&
            LeftSmooth01 >= reopenThreshold01 &&
            RightSmooth01 >= reopenThreshold01;

        if (!confidentlyOpen)
            return;

        float riseT = 1f - Mathf.Exp(-baselineRiseSpeed * sampleInterval);
        float fallT = 1f - Mathf.Exp(-baselineFallSpeed * sampleInterval);

        float leftTarget = Mathf.Max(LeftRaw, minBaseline);
        float rightTarget = Mathf.Max(RightRaw, minBaseline);

        LeftBaseline = Mathf.Lerp(
            LeftBaseline,
            leftTarget,
            LeftRaw > LeftBaseline ? riseT : fallT
        );

        RightBaseline = Mathf.Lerp(
            RightBaseline,
            rightTarget,
            RightRaw > RightBaseline ? riseT : fallT
        );

        ComputeSignals();
    }

    void ResetBlinkPhaseOnly()
    {
        EyesClosed = false;
        phase = BlinkPhase.Idle;
        CandidateAge = 0f;
        closedMinCombined01 = 1f;
        openStableStartTime = -1f;
        OpenStableAge = 0f;
        BlinkArmed = false;
    }

    public void Recalibrate(bool clearBlinkCount = true)
    {
        InvalidateTracking(clearBlinkCount);
        nextSampleTime = 0f;
        LastBlinkDecision = "Recalibrating - keep eyes open";
    }

    void HandleTrackingLoss(string status)
    {
        LastSampleOk = false;
        LastSampleStatus = status;
        BlinkThisFrame = false;
        EyesClosed = false;

        if (phase != BlinkPhase.Idle)
            LastBlinkDecision = "Rejected: tracking lost";

        ResetBlinkPhaseOnly();

        if (lastGoodSampleTime >= 0f && Time.unscaledTime - lastGoodSampleTime > lostTrackingResetDelay)
        {
            InvalidateTracking(false);
            LastSampleStatus += " | hard reset";
        }
    }

    bool UpdateMotionSuppression(Rect left, Rect right)
    {
        if (!useMotionGuard)
        {
            MotionSuppressed = false;
            return false;
        }

        if (!havePrevEyeRects)
        {
            prevLeftEyeRect01 = left;
            prevRightEyeRect01 = right;
            havePrevEyeRects = true;
            MotionSuppressed = false;
            return false;
        }

        float leftMove = Vector2.Distance(left.center, prevLeftEyeRect01.center) / Mathf.Max(left.width, 0.0001f);
        float rightMove = Vector2.Distance(right.center, prevRightEyeRect01.center) / Mathf.Max(right.width, 0.0001f);

        float leftSizeChange = Mathf.Max(
            Mathf.Abs(left.width - prevLeftEyeRect01.width) / Mathf.Max(prevLeftEyeRect01.width, 0.0001f),
            Mathf.Abs(left.height - prevLeftEyeRect01.height) / Mathf.Max(prevLeftEyeRect01.height, 0.0001f)
        );

        float rightSizeChange = Mathf.Max(
            Mathf.Abs(right.width - prevRightEyeRect01.width) / Mathf.Max(prevRightEyeRect01.width, 0.0001f),
            Mathf.Abs(right.height - prevRightEyeRect01.height) / Mathf.Max(prevRightEyeRect01.height, 0.0001f)
        );

        prevLeftEyeRect01 = left;
        prevRightEyeRect01 = right;

        bool movedTooMuch =
            leftMove > rectMoveRejectFrac ||
            rightMove > rectMoveRejectFrac ||
            leftSizeChange > rectSizeRejectFrac ||
            rightSizeChange > rectSizeRejectFrac;

        if (movedTooMuch)
        {
            suppressBlinkUntil = Time.unscaledTime + motionSuppressTime;
            ResetBlinkPhaseOnly();
        }

        MotionSuppressed = Time.unscaledTime < suppressBlinkUntil;
        return MotionSuppressed;
    }

    bool SampleEye(RenderTexture src, Rect eyeRect01TopLeft, RenderTexture dst, Texture2D dstTex, out float score, out string message)
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
        message = $"ok ({score:F4})";
        return true;
    }

    float ComputeEyeOpennessScore(Color32[] pixels, int width, int height)
    {
        if (pixels == null || pixels.Length < width * height || width < 4 || height < 4)
            return 0f;

        // Ignore the very edges of the sample box.
        int x0 = Mathf.Clamp(Mathf.RoundToInt(width * 0.10f), 0, width - 1);
        int x1 = Mathf.Clamp(Mathf.RoundToInt(width * 0.90f), x0 + 1, width);

        int y0 = Mathf.Clamp(Mathf.RoundToInt(height * 0.12f), 0, height - 1);
        int y1 = Mathf.Clamp(Mathf.RoundToInt(height * 0.88f), y0 + 1, height);

        int roiW = x1 - x0;
        int roiH = y1 - y0;

        if (roiW <= 0 || roiH <= 0)
            return 0f;

        // First pass: calculate the local average brightness.
        // This makes the detector care less about the room being bright/dark.
        float sum = 0f;
        int count = 0;

        for (int y = y0; y < y1; y++)
        {
            int row = y * width;

            for (int x = x0; x < x1; x++)
            {
                sum += Luma(pixels[row + x]);
                count++;
            }
        }

        if (count <= 0)
            return 0f;

        float mean = sum / count;

        // Second pass: calculate local contrast.
        float varianceSum = 0f;

        for (int y = y0; y < y1; y++)
        {
            int row = y * width;

            for (int x = x0; x < x1; x++)
            {
                float d = Luma(pixels[row + x]) - mean;
                varianceSum += d * d;
            }
        }

        float std = Mathf.Sqrt(varianceSum / count);

        // If the image has almost no contrast, we cannot confidently read the eye.
        // Return a middle-ish value instead of pretending it is fully open or closed.
        if (std < 3f)
            return 0.5f;

        float[] rowDarkEnergy = new float[height];

        // Third pass: find pixels that are dark relative to the local eye crop.
        // This is the important change: it uses relative darkness, not absolute light level.
        for (int y = y0; y < y1; y++)
        {
            float rowEnergy = 0f;
            int row = y * width;

            for (int x = x0; x < x1; x++)
            {
                float l = Luma(pixels[row + x]);

                // Positive when this pixel is darker than the local average.
                float darkAmount = Mathf.Clamp01((mean - l) / (std * 1.25f));

                rowEnergy += darkAmount;
            }

            rowDarkEnergy[y] = rowEnergy / roiW;
        }

        // Find the strongest dark row. In an open eye this should come from iris/pupil.
        float maxRowEnergy = 0f;

        for (int y = y0; y < y1; y++)
            maxRowEnergy = Mathf.Max(maxRowEnergy, rowDarkEnergy[y]);

        if (maxRowEnergy <= 0.02f)
            return 0.5f;

        // Measure how many vertical rows contain meaningful dark eye detail.
        // Open eye: iris/pupil creates a taller vertical dark region.
        // Closed eye: eyelid/eyelash line is usually much flatter/thinner.
        float threshold = maxRowEnergy * 0.38f;

        int firstActiveRow = -1;
        int lastActiveRow = -1;

        for (int y = y0; y < y1; y++)
        {
            if (rowDarkEnergy[y] >= threshold)
            {
                if (firstActiveRow < 0)
                    firstActiveRow = y;

                lastActiveRow = y;
            }
        }

        if (firstActiveRow < 0 || lastActiveRow < 0)
            return 0.5f;

        float activeHeight = lastActiveRow - firstActiveRow + 1;

        // Convert vertical dark feature height into a 0-1 openness score.
        float openness = activeHeight / Mathf.Max(1f, roiH * 0.55f);

        // Add a small confidence factor so noisy/flat crops do not jump around too much.
        float contrastConfidence = Mathf.Clamp01(std / 18f);

        openness = Mathf.Lerp(0.5f, openness, contrastConfidence);

        return Mathf.Clamp01(openness);
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

        if (xMax < xMin)
        {
            float temp = xMin;
            xMin = xMax;
            xMax = temp;
        }

        if (yMax < yMin)
        {
            float temp = yMin;
            yMin = yMax;
            yMax = temp;
        }

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

        LeftBaseline = 0f;
        RightBaseline = 0f;

        LeftNorm01 = 0f;
        RightNorm01 = 0f;
        Combined01 = 0f;

        LeftSmooth01 = 0f;
        RightSmooth01 = 0f;
        Smoothed01 = 0f;

        StrongerEye01 = 0f;
        WeakerEye01 = 0f;
        ClosureDepth01 = 0f;
        LeftChange01 = 0f;
        RightChange01 = 0f;

        EyesClosed = false;
        BlinkThisFrame = false;
        IsCalibrating = false;
        LastSampleOk = false;
        LastSampleStatus = "Reset";
        LastBlinkDecision = "Reset";

        MotionSuppressed = false;
        OpenStableAge = 0f;
        BlinkArmed = false;
        CandidateAge = 0f;

        BaselineRecenterActive = false;
        TemplateReady = false;
        TemplateRecenterActive = false;
        TemplateDiff01 = 0f;
        LeftTemplateDiff01 = 0f;
        RightTemplateDiff01 = 0f;

        phase = BlinkPhase.Idle;

        trackingStartTime = -1f;
        lastGoodSampleTime = -999f;
        lastBlinkTime = -999f;
        closedStartTime = 0f;
        closedMinCombined01 = 1f;
        openStableStartTime = -1f;
        suppressBlinkUntil = -999f;
        havePrevEyeRects = false;

        if (clearBlinkCount)
            BlinkCount = 0;
    }

    void EnsureDebugStyle()
    {
        if (debugTextStyle != null)
            return;

        debugTextStyle = new GUIStyle(GUI.skin.label);
        debugTextStyle.fontSize = debugFontSize;
        debugTextStyle.normal.textColor = Color.white;
    }

    void OnGUI()
    {
        if (!showDebugOverlay)
            return;

        EnsureDebugStyle();

        // Positioned under the eye/blink counter area instead of at the bottom.
        // On a tall portrait phone screen this should sit around the upper-left/middle area.
        float x = 10f;
        float y = Screen.height * 0.22f;

        string text =
            $"EyeTracker | {LastSampleStatus} | Phase:{BlinkPhaseName}\n" +
            $"Open L:{LeftSmooth01:F2} R:{RightSmooth01:F2} Avg:{Smoothed01:F2} | Closed:{EyesClosed} Armed:{BlinkArmed}\n" +
            $"BlinkCount:{BlinkCount} | MotionIgnored:{MotionSuppressed} | Calibrating:{IsCalibrating}\n" +
            $"Decision: {LastBlinkDecision}";

        GUI.Label(new Rect(x, y, Screen.width - 20, 180), text, debugTextStyle);

        if (showEyeSamplePreview)
        {
            float w = sampleWidth * 8f;
            float h = sampleHeight * 8f;

            // Put the eye sample boxes directly below the debug text.
            float previewY = y + 160f;

            if (leftEyeTex != null)
                GUI.DrawTexture(new Rect(x, previewY, w, h), leftEyeTex, ScaleMode.StretchToFill, false);

            if (rightEyeTex != null)
                GUI.DrawTexture(new Rect(x + w + 20f, previewY, w, h), rightEyeTex, ScaleMode.StretchToFill, false);
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
