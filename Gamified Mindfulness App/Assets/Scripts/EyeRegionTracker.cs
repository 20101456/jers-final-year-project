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
    [Range(0.5f, 3f)] public float eyeRectWidthMultiplier = 1.55f;
    [Range(0.3f, 2f)] public float eyeRectHeightMultiplier = 0.65f;
    [Range(-0.1f, 0.1f)] public float eyeRectYOffset = 0.005f;

    [Header("Calibration")]
    [Tooltip("User should keep eyes open during this time.")]
    [Range(0.2f, 3f)] public float calibrationDuration = 1.0f;

    [Header("Template Blink Detection")]
    [Tooltip("Higher = less sensitive. If real blinks are missed, lower this slightly.")]
    [Range(0.05f, 0.6f)] public float closedDifferenceThreshold = 0.22f;

    [Tooltip("Current eye must return below this value to count as reopened.")]
    [Range(0.02f, 0.5f)] public float reopenDifferenceThreshold = 0.13f;

    [Range(1f, 40f)] public float smoothingSpeed = 18f;

    [Tooltip("A blink shorter than this is ignored.")]
    [Range(0.01f, 0.25f)] public float minBlinkClosedTime = 0.04f;

    [Tooltip("A blink longer than this is treated as holding the eyes closed.")]
    [Range(0.05f, 1f)] public float maxBlinkClosedTime = 0.45f;

    [Range(0.05f, 0.5f)] public float blinkCooldown = 0.18f;

    [Tooltip("Eyes must look open/stable before a new blink can be counted.")]
    [Range(0.02f, 0.5f)] public float minOpenStableTime = 0.12f;

    [Header("Motion Guard")]
    public bool useMotionGuard = true;

    [Range(0.05f, 0.6f)] public float rectMoveRejectFrac = 0.20f;
    [Range(0.05f, 0.6f)] public float rectSizeRejectFrac = 0.25f;
    [Range(0.01f, 0.4f)] public float motionSuppressTime = 0.16f;

    [Header("Debug")]
    public bool showDebugOverlay = true;
    public bool showEyeSamplePreview = true;
    public bool logBlinkEvents = false;
    [Range(16, 48)] public int debugFontSize = 26;

    public bool EyesClosed { get; private set; }
    public bool BlinkThisFrame { get; private set; }
    public int BlinkCount { get; private set; }
    public bool IsCalibrating { get; private set; }

    public bool LastSampleOk { get; private set; }
    public string LastSampleStatus { get; private set; } = "Waiting...";
    public string LastBlinkDecision { get; private set; } = "None";

    public bool MotionSuppressed { get; private set; }
    public bool BlinkArmed { get; private set; }
    public float OpenStableAge { get; private set; }

    public float LeftDifference01 { get; private set; }
    public float RightDifference01 { get; private set; }
    public float Difference01 { get; private set; }

    public float LeftSmoothDifference01 { get; private set; }
    public float RightSmoothDifference01 { get; private set; }
    public float SmoothDifference01 { get; private set; }

    // Compatibility values for your existing encounter/debug scripts.
    public float LeftRaw => 1f - LeftSmoothDifference01;
    public float RightRaw => 1f - RightSmoothDifference01;
    public float CombinedRaw => 1f - SmoothDifference01;

    public float LeftNorm01 => 1f - LeftSmoothDifference01;
    public float RightNorm01 => 1f - RightSmoothDifference01;
    public float Combined01 => 1f - SmoothDifference01;

    public float LeftSmooth01 => 1f - LeftSmoothDifference01;
    public float RightSmooth01 => 1f - RightSmoothDifference01;
    public float Smoothed01 => 1f - SmoothDifference01;

    public float LeftBaseline => 1f;
    public float RightBaseline => 1f;
    public float OpenBaseline => 1f;

    public float StrongerEye01 => Mathf.Max(LeftSmooth01, RightSmooth01);
    public float WeakerEye01 => Mathf.Min(LeftSmooth01, RightSmooth01);
    public float ClosureDepth01 => SmoothDifference01;
    public float LeftChange01 => LeftSmoothDifference01;
    public float RightChange01 => RightSmoothDifference01;

    public float CandidateAge { get; private set; }
    public float ClosedMinCombined01 => 1f - maxClosedDifference;
    public string BlinkPhaseName => phase.ToString();

    public bool BaselineRecenterActive => false;
    public bool TemplateReady => templateReady;
    public bool TemplateRecenterActive => false;
    public float TemplateDiff01 => SmoothDifference01;
    public float LeftTemplateDiff01 => LeftSmoothDifference01;
    public float RightTemplateDiff01 => RightSmoothDifference01;

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

    float[] leftTemplate;
    float[] rightTemplate;
    float[] leftCurrent;
    float[] rightCurrent;

    bool templateReady;
    int calibrationSampleCount;

    float nextSampleTime;
    float trackingStartTime = -1f;
    float lastGoodSampleTime = -999f;
    float lostTrackingResetDelay = 0.35f;

    float closedStartTime;
    float lastBlinkTime = -999f;
    float maxClosedDifference;

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
        InvalidateTracking(true);
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

        bool leftOk = SampleEye(src, leftRect, leftEyeRT, leftEyeTex, leftCurrent, out string leftMsg);
        bool rightOk = SampleEye(src, rightRect, rightEyeRT, rightEyeTex, rightCurrent, out string rightMsg);

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

        UpdateCalibration();

        if (!templateReady)
        {
            ResetBlinkPhaseOnly();
            LastBlinkDecision = "Building open-eye template";
            return;
        }

        UpdateDifferenceSignals();
        UpdateBlinkState();
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

    void UpdateCalibration()
    {
        float trackedTime = Time.unscaledTime - trackingStartTime;
        IsCalibrating = trackedTime < calibrationDuration;

        if (!IsCalibrating)
        {
            if (calibrationSampleCount > 0)
                templateReady = true;

            return;
        }

        AddToTemplate(leftCurrent, leftTemplate, calibrationSampleCount);
        AddToTemplate(rightCurrent, rightTemplate, calibrationSampleCount);

        calibrationSampleCount++;
        templateReady = false;
    }

    void AddToTemplate(float[] current, float[] template, int sampleIndex)
    {
        if (current == null || template == null || current.Length != template.Length)
            return;

        if (sampleIndex <= 0)
        {
            for (int i = 0; i < current.Length; i++)
                template[i] = current[i];

            return;
        }

        float t = 1f / (sampleIndex + 1f);

        for (int i = 0; i < current.Length; i++)
            template[i] = Mathf.Lerp(template[i], current[i], t);
    }

    void UpdateDifferenceSignals()
    {
        LeftDifference01 = ComputeTemplateDifference(leftCurrent, leftTemplate);
        RightDifference01 = ComputeTemplateDifference(rightCurrent, rightTemplate);
        Difference01 = 0.5f * (LeftDifference01 + RightDifference01);

        float smoothT = 1f - Mathf.Exp(-smoothingSpeed * sampleInterval);

        LeftSmoothDifference01 = Mathf.Lerp(LeftSmoothDifference01, LeftDifference01, smoothT);
        RightSmoothDifference01 = Mathf.Lerp(RightSmoothDifference01, RightDifference01, smoothT);
        SmoothDifference01 = 0.5f * (LeftSmoothDifference01 + RightSmoothDifference01);
    }

    float ComputeTemplateDifference(float[] current, float[] template)
    {
        if (current == null || template == null || current.Length != template.Length || current.Length == 0)
            return 0f;

        float sum = 0f;

        for (int i = 0; i < current.Length; i++)
            sum += Mathf.Abs(current[i] - template[i]);

        return Mathf.Clamp01(sum / current.Length);
    }

    void UpdateBlinkState()
    {
        CandidateAge = 0f;

        if (IsCalibrating || !templateReady)
        {
            ResetBlinkPhaseOnly();
            LastBlinkDecision = "Calibrating - keep eyes open";
            return;
        }

        bool leftDifferent = LeftSmoothDifference01 >= closedDifferenceThreshold;
        bool rightDifferent = RightSmoothDifference01 >= closedDifferenceThreshold;

        bool closedNow = leftDifferent && rightDifferent;

        bool leftBackOpen = LeftSmoothDifference01 <= reopenDifferenceThreshold;
        bool rightBackOpen = RightSmoothDifference01 <= reopenDifferenceThreshold;

        bool openNow = leftBackOpen && rightBackOpen;

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
                    maxClosedDifference = 0f;

                    if (inCooldown)
                    {
                        LastBlinkDecision = "Cooldown";
                        return;
                    }

                    if (!BlinkArmed)
                    {
                        LastBlinkDecision =
                            $"Waiting for open match | LDiff:{LeftSmoothDifference01:F2} RDiff:{RightSmoothDifference01:F2}";
                        return;
                    }

                    if (closedNow)
                    {
                        phase = BlinkPhase.Closed;
                        EyesClosed = true;
                        BlinkArmed = false;
                        closedStartTime = Time.unscaledTime;
                        maxClosedDifference = SmoothDifference01;

                        LastBlinkDecision =
                            $"Eyes changed/closed | LDiff:{LeftSmoothDifference01:F2} RDiff:{RightSmoothDifference01:F2}";
                        return;
                    }

                    LastBlinkDecision =
                        $"Watching | LDiff:{LeftSmoothDifference01:F2} RDiff:{RightSmoothDifference01:F2}";
                    return;
                }

            case BlinkPhase.Closed:
                {
                    EyesClosed = true;
                    CandidateAge = Time.unscaledTime - closedStartTime;
                    maxClosedDifference = Mathf.Max(maxClosedDifference, SmoothDifference01);

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
                        LastBlinkDecision = "Rejected: eyes held changed/closed too long";
                        ResetBlinkPhaseOnly();
                        return;
                    }

                    LastBlinkDecision = $"Closed holding | {CandidateAge:F2}s";
                    return;
                }
        }
    }

    void ResetBlinkPhaseOnly()
    {
        EyesClosed = false;
        phase = BlinkPhase.Idle;
        CandidateAge = 0f;
        maxClosedDifference = 0f;
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

    bool SampleEye(RenderTexture src, Rect eyeRect01TopLeft, RenderTexture dst, Texture2D dstTex, float[] outputSignature, out string message)
    {
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

        if (outputSignature == null || outputSignature.Length != sampleWidth * sampleHeight)
        {
            message = "signature buffer invalid";
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

        BuildLightingNormalisedSignature(pixels, dst.width, dst.height, outputSignature);
        message = "ok";
        return true;
    }

    void BuildLightingNormalisedSignature(Color32[] pixels, int width, int height, float[] output)
    {
        if (pixels == null || output == null || pixels.Length < width * height || output.Length < width * height)
            return;

        float sum = 0f;
        int count = width * height;

        for (int i = 0; i < count; i++)
            sum += Luma(pixels[i]);

        float mean = sum / Mathf.Max(1, count);

        float varianceSum = 0f;

        for (int i = 0; i < count; i++)
        {
            float d = Luma(pixels[i]) - mean;
            varianceSum += d * d;
        }

        float std = Mathf.Sqrt(varianceSum / Mathf.Max(1, count));
        std = Mathf.Max(std, 8f);

        for (int i = 0; i < count; i++)
        {
            float l = Luma(pixels[i]);

            // Local contrast normalisation.
            // 0.5 = local average, lower = darker than local average, higher = brighter.
            float normalised = 0.5f + ((l - mean) / (std * 4f));
            output[i] = Mathf.Clamp01(normalised);
        }
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
            leftTemplate == null ||
            rightTemplate == null ||
            leftCurrent == null ||
            rightCurrent == null ||
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

        int len = sampleWidth * sampleHeight;
        leftTemplate = new float[len];
        rightTemplate = new float[len];
        leftCurrent = new float[len];
        rightCurrent = new float[len];
    }

    void InvalidateTracking(bool clearBlinkCount)
    {
        CreateBuffers();

        if (leftTemplate != null)
            System.Array.Clear(leftTemplate, 0, leftTemplate.Length);

        if (rightTemplate != null)
            System.Array.Clear(rightTemplate, 0, rightTemplate.Length);

        if (leftCurrent != null)
            System.Array.Clear(leftCurrent, 0, leftCurrent.Length);

        if (rightCurrent != null)
            System.Array.Clear(rightCurrent, 0, rightCurrent.Length);

        templateReady = false;
        calibrationSampleCount = 0;

        LeftDifference01 = 0f;
        RightDifference01 = 0f;
        Difference01 = 0f;

        LeftSmoothDifference01 = 0f;
        RightSmoothDifference01 = 0f;
        SmoothDifference01 = 0f;

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

        phase = BlinkPhase.Idle;

        trackingStartTime = -1f;
        lastGoodSampleTime = -999f;
        lastBlinkTime = -999f;
        closedStartTime = 0f;
        maxClosedDifference = 0f;
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

        float x = 10f;
        float y = Screen.height * 0.22f;

        string text =
            $"EyeTracker | {LastSampleStatus} | Phase:{BlinkPhaseName}\n" +
            $"Diff L:{LeftSmoothDifference01:F2} R:{RightSmoothDifference01:F2} Avg:{SmoothDifference01:F2} | Closed:{EyesClosed} Armed:{BlinkArmed}\n" +
            $"BlinkCount:{BlinkCount} | MotionIgnored:{MotionSuppressed} | Calibrating:{IsCalibrating} | Template:{TemplateReady}\n" +
            $"Decision: {LastBlinkDecision}";

        GUI.Label(new Rect(x, y, Screen.width - 20, 180), text, debugTextStyle);

        if (showEyeSamplePreview)
        {
            float w = sampleWidth * 8f;
            float h = sampleHeight * 8f;
            float previewY = y + 170f;

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

        leftTemplate = null;
        rightTemplate = null;
        leftCurrent = null;
        rightCurrent = null;
    }
}