using UnityEngine;

/// <summary>
/// Tracks eye-region appearance changes from the corrected front-camera feed
/// and uses those changes to estimate blinks.
///
/// The tracker builds an open-eye template during calibration, then compares
/// each new eye sample against that template. A blink is counted when both eyes
/// differ enough from the template and then return to the open-eye template
/// within a valid blink duration.
/// </summary>
public class EyeRegionTracker : MonoBehaviour
{
    [Header("References")]
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

    /*
     * Compatibility values.
     * These keep older encounter/debug scripts working even though the tracker
     * now uses template-difference values internally.
     */
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

    const float NoTime = -999f;
    const float LostTrackingResetDelay = 0.35f;

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
    float lastGoodSampleTime = NoTime;

    float closedStartTime;
    float lastBlinkTime = NoTime;
    float maxClosedDifference;

    float openStableStartTime = -1f;
    float suppressBlinkUntil = NoTime;

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

        if (!CanSample(out string trackingError))
        {
            HandleTrackingLoss(trackingError);
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

        RenderTexture src = cameraFeed.CorrectedRT;

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

    bool CanSample(out string error)
    {
        if (cameraFeed == null)
        {
            error = "cameraFeed missing";
            return false;
        }

        if (faceDebug == null)
        {
            error = "faceDebug missing";
            return false;
        }

        if (!faceDebug.HasEyeRegions)
        {
            error = "Eye regions not ready";
            return false;
        }

        if (cameraFeed.CorrectedRT == null)
        {
            error = "CorrectedRT is null";
            return false;
        }

        error = string.Empty;
        return true;
    }

    Rect AdjustEyeRect(Rect rect)
    {
        rect = ClampRect01(rect);

        Vector2 center = rect.center;
        center.y += eyeRectYOffset;

        float width = rect.width * eyeRectWidthMultiplier;
        float height = rect.height * eyeRectHeightMultiplier;

        Rect adjusted = new Rect(
            center.x - width * 0.5f,
            center.y - height * 0.5f,
            width,
            height
        );

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

        float blend = 1f / (sampleIndex + 1f);

        for (int i = 0; i < current.Length; i++)
            template[i] = Mathf.Lerp(template[i], current[i], blend);
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

        bool closedNow = AreBothEyesDifferentFromTemplate();
        bool openNow = AreBothEyesBackToTemplate();

        UpdateOpenStableState(openNow);

        bool inCooldown = Time.unscaledTime - lastBlinkTime < blinkCooldown;

        switch (phase)
        {
            case BlinkPhase.Idle:
                UpdateIdleBlinkState(closedNow, inCooldown);
                break;

            case BlinkPhase.Closed:
                UpdateClosedBlinkState(openNow, inCooldown);
                break;
        }
    }

    bool AreBothEyesDifferentFromTemplate()
    {
        return
            LeftSmoothDifference01 >= closedDifferenceThreshold &&
            RightSmoothDifference01 >= closedDifferenceThreshold;
    }

    bool AreBothEyesBackToTemplate()
    {
        return
            LeftSmoothDifference01 <= reopenDifferenceThreshold &&
            RightSmoothDifference01 <= reopenDifferenceThreshold;
    }

    void UpdateOpenStableState(bool openNow)
    {
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
    }

    void UpdateIdleBlinkState(bool closedNow, bool inCooldown)
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
    }

    void UpdateClosedBlinkState(bool openNow, bool inCooldown)
    {
        EyesClosed = true;
        CandidateAge = Time.unscaledTime - closedStartTime;
        maxClosedDifference = Mathf.Max(maxClosedDifference, SmoothDifference01);

        if (openNow)
        {
            TryAcceptBlink(inCooldown);
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
    }

    void TryAcceptBlink(bool inCooldown)
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

            return;
        }

        if (closedDuration < minBlinkClosedTime)
        {
            LastBlinkDecision = $"Rejected too fast ({closedDuration:F2}s)";
        }
        else
        {
            LastBlinkDecision = $"Rejected too long ({closedDuration:F2}s)";
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

    public void ToggleDebugUI()
    {
        showDebugOverlay = !showDebugOverlay;
        showEyeSamplePreview = showDebugOverlay;
    }

    public void SetDebugUI(bool visible)
    {
        showDebugOverlay = visible;
        showEyeSamplePreview = visible;
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

        bool trackingWasRecentlyValid = lastGoodSampleTime >= 0f;
        bool trackingLostTooLong = Time.unscaledTime - lastGoodSampleTime > LostTrackingResetDelay;

        if (trackingWasRecentlyValid && trackingLostTooLong)
        {
            InvalidateTracking(false);
            LastSampleStatus += " | hard reset";
        }
    }

    bool UpdateMotionSuppression(Rect leftRect, Rect rightRect)
    {
        if (!useMotionGuard)
        {
            MotionSuppressed = false;
            return false;
        }

        if (!havePrevEyeRects)
        {
            prevLeftEyeRect01 = leftRect;
            prevRightEyeRect01 = rightRect;
            havePrevEyeRects = true;
            MotionSuppressed = false;
            return false;
        }

        float leftMove = GetRectMoveAmount(leftRect, prevLeftEyeRect01);
        float rightMove = GetRectMoveAmount(rightRect, prevRightEyeRect01);

        float leftSizeChange = GetRectSizeChange(leftRect, prevLeftEyeRect01);
        float rightSizeChange = GetRectSizeChange(rightRect, prevRightEyeRect01);

        prevLeftEyeRect01 = leftRect;
        prevRightEyeRect01 = rightRect;

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

    float GetRectMoveAmount(Rect current, Rect previous)
    {
        return Vector2.Distance(current.center, previous.center) / Mathf.Max(current.width, 0.0001f);
    }

    float GetRectSizeChange(Rect current, Rect previous)
    {
        float widthChange = Mathf.Abs(current.width - previous.width) / Mathf.Max(previous.width, 0.0001f);
        float heightChange = Mathf.Abs(current.height - previous.height) / Mathf.Max(previous.height, 0.0001f);

        return Mathf.Max(widthChange, heightChange);
    }

    bool SampleEye(
        RenderTexture source,
        Rect eyeRect01TopLeft,
        RenderTexture destination,
        Texture2D destinationTexture,
        float[] outputSignature,
        out string message
    )
    {
        message = "Unknown";

        if (source == null)
        {
            message = "source null";
            return false;
        }

        if (destination == null)
        {
            message = "destination null";
            return false;
        }

        if (destinationTexture == null)
        {
            message = "destinationTexture null";
            return false;
        }

        if (outputSignature == null || outputSignature.Length != sampleWidth * sampleHeight)
        {
            message = "signature buffer invalid";
            return false;
        }

        Rect rect = ClampRect01(eyeRect01TopLeft);

        if (rect.width <= 0f || rect.height <= 0f)
        {
            message = "invalid rect";
            return false;
        }

        Vector2 scale = new Vector2(rect.width, rect.height);
        Vector2 offset = new Vector2(rect.xMin, 1f - rect.yMax);

        Graphics.Blit(source, destination, scale, offset);

        RenderTexture previousActiveRT = RenderTexture.active;
        RenderTexture.active = destination;

        destinationTexture.ReadPixels(new Rect(0, 0, destination.width, destination.height), 0, 0, false);
        destinationTexture.Apply(false, false);

        RenderTexture.active = previousActiveRT;

        Color32[] pixels = destinationTexture.GetPixels32();

        if (pixels == null || pixels.Length != destination.width * destination.height)
        {
            message = "pixel read failed";
            return false;
        }

        BuildLightingNormalisedSignature(pixels, destination.width, destination.height, outputSignature);

        message = "ok";
        return true;
    }

    void BuildLightingNormalisedSignature(Color32[] pixels, int width, int height, float[] output)
    {
        if (pixels == null || output == null || pixels.Length < width * height || output.Length < width * height)
            return;

        int count = width * height;
        float sum = 0f;

        for (int i = 0; i < count; i++)
            sum += Luma(pixels[i]);

        float mean = sum / Mathf.Max(1, count);

        float varianceSum = 0f;

        for (int i = 0; i < count; i++)
        {
            float difference = Luma(pixels[i]) - mean;
            varianceSum += difference * difference;
        }

        float standardDeviation = Mathf.Sqrt(varianceSum / Mathf.Max(1, count));
        standardDeviation = Mathf.Max(standardDeviation, 8f);

        for (int i = 0; i < count; i++)
        {
            float luma = Luma(pixels[i]);

            // Local contrast normalisation.
            // 0.5 = local average.
            // Lower values are darker than local average.
            // Higher values are brighter than local average.
            float normalised = 0.5f + ((luma - mean) / (standardDeviation * 4f));
            output[i] = Mathf.Clamp01(normalised);
        }
    }

    float Luma(Color32 colour)
    {
        return 0.299f * colour.r + 0.587f * colour.g + 0.114f * colour.b;
    }

    Rect ClampRect01(Rect rect)
    {
        float xMin = Mathf.Clamp01(rect.xMin);
        float yMin = Mathf.Clamp01(rect.yMin);
        float xMax = Mathf.Clamp01(rect.xMax);
        float yMax = Mathf.Clamp01(rect.yMax);

        if (xMax < xMin)
            (xMin, xMax) = (xMax, xMin);

        if (yMax < yMin)
            (yMin, yMax) = (yMax, yMin);

        return Rect.MinMaxRect(xMin, yMin, xMax, yMax);
    }

    void CreateBuffers()
    {
        bool shouldRecreate =
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

        if (!shouldRecreate)
            return;

        ReleaseBuffers();

        leftEyeRT = CreateEyeRenderTexture();
        rightEyeRT = CreateEyeRenderTexture();

        leftEyeTex = new Texture2D(sampleWidth, sampleHeight, TextureFormat.RGBA32, false, false);
        rightEyeTex = new Texture2D(sampleWidth, sampleHeight, TextureFormat.RGBA32, false, false);

        int signatureLength = sampleWidth * sampleHeight;

        leftTemplate = new float[signatureLength];
        rightTemplate = new float[signatureLength];
        leftCurrent = new float[signatureLength];
        rightCurrent = new float[signatureLength];
    }

    RenderTexture CreateEyeRenderTexture()
    {
        RenderTexture rt = new RenderTexture(sampleWidth, sampleHeight, 0, RenderTextureFormat.ARGB32)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };

        rt.Create();
        return rt;
    }

    void InvalidateTracking(bool clearBlinkCount)
    {
        CreateBuffers();

        ClearArray(leftTemplate);
        ClearArray(rightTemplate);
        ClearArray(leftCurrent);
        ClearArray(rightCurrent);

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
        lastGoodSampleTime = NoTime;
        lastBlinkTime = NoTime;
        closedStartTime = 0f;
        maxClosedDifference = 0f;
        openStableStartTime = -1f;
        suppressBlinkUntil = NoTime;
        havePrevEyeRects = false;

        if (clearBlinkCount)
            BlinkCount = 0;
    }

    void ClearArray(float[] array)
    {
        if (array != null)
            System.Array.Clear(array, 0, array.Length);
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

        GUI.Label(new Rect(x, y, Screen.width - 20f, 180f), text, debugTextStyle);

        if (showEyeSamplePreview)
            DrawEyeSamplePreview(x, y + 170f);
    }

    void EnsureDebugStyle()
    {
        if (debugTextStyle == null)
            debugTextStyle = new GUIStyle(GUI.skin.label);

        debugTextStyle.fontSize = debugFontSize;
        debugTextStyle.normal.textColor = Color.white;
    }

    void DrawEyeSamplePreview(float x, float y)
    {
        float width = sampleWidth * 8f;
        float height = sampleHeight * 8f;

        if (leftEyeTex != null)
            GUI.DrawTexture(new Rect(x, y, width, height), leftEyeTex, ScaleMode.StretchToFill, false);

        if (rightEyeTex != null)
            GUI.DrawTexture(new Rect(x + width + 20f, y, width, height), rightEyeTex, ScaleMode.StretchToFill, false);
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