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
    [Range(0.20f, 0.95f)] public float closedThreshold01 = 0.78f;
    [Range(0.30f, 0.99f)] public float reopenThreshold01 = 0.90f;
    [Range(1f, 30f)] public float smoothingSpeed = 16f;
    [Range(0.01f, 0.25f)] public float minBlinkClosedTime = 0.03f;
    [Range(0.05f, 0.60f)] public float maxBlinkClosedTime = 0.35f;
    [Range(0.01f, 0.30f)] public float blinkCooldown = 0.12f;
    [Range(0.0f, 0.30f)] public float minClosedDepthBelowThreshold = 0.02f;
    [Range(0.05f, 0.80f)] public float rawChangeForClosed = 0.22f;

    [Header("Calibration / Baseline")]
    [Range(0.2f, 3f)] public float calibrationDuration = 1.2f;
    public bool autoTrackOpenBaseline = true;
    [Range(0.05f, 5f)] public float baselineFallSpeed = 0.35f;
    [Range(0.001f, 0.2f)] public float minBaseline = 0.01f;

    [Header("Tracking Stability")]
    [Range(0.05f, 1.0f)] public float lostTrackingResetDelay = 0.25f;
    [Range(0.80f, 1.00f)] public float baselineTrackGate01 = 0.94f;

    [Header("Blink Event Logic")]
    [Range(0.00f, 0.25f)] public float closeSupportMargin01 = 0.10f;
    [Range(0.00f, 0.25f)] public float reopenSupportMargin01 = 0.18f;
    [Range(0.01f, 0.25f)] public float maxCloseBuildTime = 0.12f;

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

    public float StrongerEye01 { get; private set; }
    public float WeakerEye01 { get; private set; }
    public float ClosureDepth01 { get; private set; }
    public float LeftChange01 { get; private set; }
    public float RightChange01 { get; private set; }
    public float CandidateAge { get; private set; }
    public float ClosedMinCombined01 => closedMinCombined01;
    public string LastBlinkDecision { get; private set; } = "None";

    public bool EyesClosed { get; private set; }
    public bool BlinkThisFrame { get; private set; }
    public int BlinkCount { get; private set; }
    public bool IsCalibrating { get; private set; }

    public bool LastSampleOk { get; private set; }
    public string LastSampleStatus { get; private set; } = "Waiting...";

    public string BlinkPhaseName => blinkPhase.ToString();

    RenderTexture leftEyeRT;
    RenderTexture rightEyeRT;
    Texture2D leftEyeTex;
    Texture2D rightEyeTex;

    float nextSampleTime;
    float closedStartTime;
    float lastBlinkTime = -999f;
    float closedMinCombined01 = 1f;
    float trackingStartTime = -1f;
    float candidateStartTime;
    float lastGoodSampleTime = -999f;

    GUIStyle debugTextStyle;

    [Header("Motion Rejection")]
    [Range(0.05f, 0.50f)] public float rectMoveRejectFrac = 0.18f;
    [Range(0.05f, 0.50f)] public float rectSizeRejectFrac = 0.15f;
    [Range(0.01f, 0.25f)] public float motionSuppressTime = 0.08f;

    [Header("Blink Rearm")]
    [Range(0.01f, 0.25f)] public float minOpenStableTime = 0.07f;

    public bool MotionSuppressed { get; private set; }
    public float OpenStableAge { get; private set; }

    Rect prevLeftEyeRect01;
    Rect prevRightEyeRect01;
    bool havePrevEyeRects;

    float suppressBlinkUntil = -999f;
    float openStableStartTime = -1f;

    enum BlinkPhase
    {
        Idle,
        Candidate,
        Closed
    }

    BlinkPhase blinkPhase = BlinkPhase.Idle;

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

        var src = cameraFeed.CorrectedRT;
        if (src == null)
        {
            HandleTrackingLoss("CorrectedRT is null");
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
            HandleTrackingLoss($"Sample failed | L:{leftMsg} | R:{rightMsg}");

            if (logSampleFailures)
                Debug.LogWarning(LastSampleStatus);

            return;
        }

        LastSampleOk = true;
        LastSampleStatus = "Sample OK";
        lastGoodSampleTime = Time.unscaledTime;

        if (UpdateMotionSuppression())
        {
            LastSampleStatus = "Sample OK | motion-suppressed";
            return;
        }

        if (trackingStartTime < 0f)
            trackingStartTime = Time.unscaledTime;

        LeftRaw = leftScore;
        RightRaw = rightScore;
        CombinedRaw = 0.5f * (LeftRaw + RightRaw);

        UpdateBaselinesAndSignals();
        UpdateBlinkState();
    }

    void HandleTrackingLoss(string status)
    {
        LastSampleOk = false;
        LastSampleStatus = status;
        BlinkThisFrame = false;
        EyesClosed = false;
        CandidateAge = 0f;

        if (blinkPhase != BlinkPhase.Idle)
            LastBlinkDecision = "Rejected: tracking lost";

        blinkPhase = BlinkPhase.Idle;
        closedMinCombined01 = 1f;

        if (lastGoodSampleTime >= 0f &&
            Time.unscaledTime - lastGoodSampleTime > lostTrackingResetDelay)
        {
            InvalidateTracking(false);
            LastSampleStatus += " | hard reset";
        }
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
            // During calibration, assume the player is looking normally with eyes open.
            // Track the open-eye raw value directly instead of taking only the maximum.
            float calibrateT = 1f - Mathf.Exp(-5f * sampleInterval);

            LeftBaseline = Mathf.Lerp(LeftBaseline, leftCandidate, calibrateT);
            RightBaseline = Mathf.Lerp(RightBaseline, rightCandidate, calibrateT);
        }
        else if (autoTrackOpenBaseline && blinkPhase == BlinkPhase.Idle)
        {
            // Only slowly update the open baseline when the current sample is still
            // close to the current open baseline. This prevents learning a blink as "open".
            float trackT = 1f - Mathf.Exp(-baselineFallSpeed * sampleInterval);

            float leftDeltaFrac =
                Mathf.Abs(leftCandidate - LeftBaseline) / Mathf.Max(LeftBaseline, minBaseline);

            float rightDeltaFrac =
                Mathf.Abs(rightCandidate - RightBaseline) / Mathf.Max(RightBaseline, minBaseline);

            bool leftStillLooksOpen = leftDeltaFrac < rawChangeForClosed * 0.45f;
            bool rightStillLooksOpen = rightDeltaFrac < rawChangeForClosed * 0.45f;

            if (leftStillLooksOpen)
                LeftBaseline = Mathf.Lerp(LeftBaseline, leftCandidate, trackT);

            if (rightStillLooksOpen)
                RightBaseline = Mathf.Lerp(RightBaseline, rightCandidate, trackT);
        }

        LeftBaseline = Mathf.Max(LeftBaseline, minBaseline);
        RightBaseline = Mathf.Max(RightBaseline, minBaseline);

        float leftChangeFrac =
            Mathf.Abs(LeftRaw - LeftBaseline) / Mathf.Max(LeftBaseline, minBaseline);

        float rightChangeFrac =
            Mathf.Abs(RightRaw - RightBaseline) / Mathf.Max(RightBaseline, minBaseline);

        LeftChange01 = Mathf.Clamp01(leftChangeFrac / rawChangeForClosed);
        RightChange01 = Mathf.Clamp01(rightChangeFrac / rawChangeForClosed);

        LeftNorm01 = 1f - LeftChange01;
        RightNorm01 = 1f - RightChange01;
        Combined01 = 0.5f * (LeftNorm01 + RightNorm01);

        float smoothT = 1f - Mathf.Exp(-smoothingSpeed * sampleInterval);

        if (LeftSmooth01 <= 0f) LeftSmooth01 = LeftNorm01;
        else LeftSmooth01 = Mathf.Lerp(LeftSmooth01, LeftNorm01, smoothT);

        if (RightSmooth01 <= 0f) RightSmooth01 = RightNorm01;
        else RightSmooth01 = Mathf.Lerp(RightSmooth01, RightNorm01, smoothT);

        Smoothed01 = 0.5f * (LeftSmooth01 + RightSmooth01);

        StrongerEye01 = Mathf.Max(LeftSmooth01, RightSmooth01);
        WeakerEye01 = Mathf.Min(LeftSmooth01, RightSmooth01);
        ClosureDepth01 = 1f - Smoothed01;
    }

    void UpdateBlinkState()
    {
        BlinkThisFrame = false;
        CandidateAge = 0f;

        if (IsCalibrating)
        {
            EyesClosed = false;
            blinkPhase = BlinkPhase.Idle;
            closedMinCombined01 = 1f;
            openStableStartTime = -1f;
            OpenStableAge = 0f;
            LastBlinkDecision = "Calibrating";
            return;
        }

        float blinkSignal01 = Smoothed01;

        bool openEnoughNow =
            blinkSignal01 > reopenThreshold01;

        if (openEnoughNow)
        {
            if (openStableStartTime < 0f)
                openStableStartTime = Time.unscaledTime;

            OpenStableAge = Time.unscaledTime - openStableStartTime;
        }
        else
        {
            openStableStartTime = -1f;
            OpenStableAge = 0f;
        }

        bool inCooldown = Time.unscaledTime - lastBlinkTime < blinkCooldown;

        bool candidateEnterNow =
            blinkSignal01 < closedThreshold01;

        bool fullyClosedNow =
            blinkSignal01 <= (closedThreshold01 - minClosedDepthBelowThreshold);

        bool reopenedNow = openEnoughNow;

        switch (blinkPhase)
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

                    if (OpenStableAge < minOpenStableTime)
                    {
                        LastBlinkDecision = $"Waiting: open stable | signal={blinkSignal01:F3}";
                        return;
                    }

                    if (candidateEnterNow)
                    {
                        blinkPhase = BlinkPhase.Candidate;
                        candidateStartTime = Time.unscaledTime;
                        closedStartTime = candidateStartTime;
                        closedMinCombined01 = blinkSignal01;
                        LastBlinkDecision = $"Candidate | signal={blinkSignal01:F3}";
                        return;
                    }

                    LastBlinkDecision = $"Idle: watching | signal={blinkSignal01:F3}";
                    break;
                }

            case BlinkPhase.Candidate:
                {
                    CandidateAge = Time.unscaledTime - candidateStartTime;
                    closedMinCombined01 = Mathf.Min(closedMinCombined01, blinkSignal01);

                    if (reopenedNow)
                    {
                        LastBlinkDecision = $"Rejected: too shallow | min={closedMinCombined01:F3}";
                        blinkPhase = BlinkPhase.Idle;
                        closedMinCombined01 = 1f;
                        return;
                    }

                    if (fullyClosedNow)
                    {
                        blinkPhase = BlinkPhase.Closed;
                        EyesClosed = true;
                        LastBlinkDecision = $"Closed | min={closedMinCombined01:F3}";
                        return;
                    }

                    if (CandidateAge > maxCloseBuildTime)
                    {
                        LastBlinkDecision = $"Rejected: never got deep enough | min={closedMinCombined01:F3}";
                        blinkPhase = BlinkPhase.Idle;
                        closedMinCombined01 = 1f;
                        return;
                    }

                    break;
                }

            case BlinkPhase.Closed:
                {
                    EyesClosed = true;
                    CandidateAge = Time.unscaledTime - candidateStartTime;
                    closedMinCombined01 = Mathf.Min(closedMinCombined01, blinkSignal01);

                    if (reopenedNow)
                    {
                        float closedDuration = Time.unscaledTime - closedStartTime;
                        float closureDepth = 1f - closedMinCombined01;

                        bool reachedRequiredDepth =
                            closedMinCombined01 <= (closedThreshold01 - minClosedDepthBelowThreshold);

                        bool validBlink =
                            reachedRequiredDepth &&
                            closedDuration >= minBlinkClosedTime &&
                            closedDuration <= maxBlinkClosedTime &&
                            !inCooldown;

                        if (validBlink)
                        {
                            BlinkCount++;
                            BlinkThisFrame = true;
                            lastBlinkTime = Time.unscaledTime;
                            LastBlinkDecision = $"Blink accepted ({closedDuration:F3}s, depth={closureDepth:F3})";

                            if (logBlinkEvents)
                            {
                                Debug.Log(
                                    $"Blink detected | count={BlinkCount} | duration={closedDuration:F3}s | " +
                                    $"L={LeftSmooth01:F3} | R={RightSmooth01:F3} | blinkSignalMin={closedMinCombined01:F3}"
                                );
                            }
                        }
                        else
                        {
                            if (!reachedRequiredDepth)
                                LastBlinkDecision = $"Rejected: too shallow | min={closedMinCombined01:F3}";
                            else if (closedDuration < minBlinkClosedTime)
                                LastBlinkDecision = $"Rejected: too fast | {closedDuration:F3}s";
                            else if (closedDuration > maxBlinkClosedTime)
                                LastBlinkDecision = $"Rejected: too long | {closedDuration:F3}s";
                            else
                                LastBlinkDecision = "Rejected: cooldown";
                        }

                        openStableStartTime = -1f;
                        OpenStableAge = 0f;

                        EyesClosed = false;
                        blinkPhase = BlinkPhase.Idle;
                        closedMinCombined01 = 1f;
                        return;
                    }

                    if (CandidateAge > maxBlinkClosedTime)
                    {
                        LastBlinkDecision = $"Rejected: never reopened | min={closedMinCombined01:F3}";
                        EyesClosed = false;
                        blinkPhase = BlinkPhase.Idle;
                        closedMinCombined01 = 1f;
                        openStableStartTime = -1f;
                        OpenStableAge = 0f;
                        return;
                    }

                    break;
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

    bool UpdateMotionSuppression()
    {
        Rect left = faceDebug.LeftEyeRect01;
        Rect right = faceDebug.RightEyeRect01;

        if (!havePrevEyeRects)
        {
            prevLeftEyeRect01 = left;
            prevRightEyeRect01 = right;
            havePrevEyeRects = true;
            MotionSuppressed = false;
            return false;
        }

        float leftMove =
            Vector2.Distance(left.center, prevLeftEyeRect01.center) /
            Mathf.Max(left.width, 0.0001f);

        float rightMove =
            Vector2.Distance(right.center, prevRightEyeRect01.center) /
            Mathf.Max(right.width, 0.0001f);

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

            if (blinkPhase != BlinkPhase.Idle)
                LastBlinkDecision = "Rejected: motion";

            blinkPhase = BlinkPhase.Idle;
            EyesClosed = false;
            CandidateAge = 0f;
            closedMinCombined01 = 1f;
            openStableStartTime = -1f;
            OpenStableAge = 0f;
        }

        MotionSuppressed = Time.unscaledTime < suppressBlinkUntil;
        return MotionSuppressed;
    }

    float ComputeEyeOpennessScore(Color32[] pixels, int width, int height)
    {
        if (pixels == null || pixels.Length < width * height || width < 4 || height < 4)
            return 0f;

        // We are no longer measuring simple vertical edges.
        // Instead, we look for a dark vertical "blob" caused by the visible iris/pupil.
        // A closed eyelid may create a dark horizontal line, but it should not create
        // much vertical thickness.

        int x0 = Mathf.Clamp(Mathf.RoundToInt(width * 0.12f), 0, width - 1);
        int x1 = Mathf.Clamp(Mathf.RoundToInt(width * 0.88f), x0 + 1, width);

        int y0 = Mathf.Clamp(Mathf.RoundToInt(height * 0.18f), 0, height - 1);
        int y1 = Mathf.Clamp(Mathf.RoundToInt(height * 0.88f), y0 + 1, height);

        int roiW = x1 - x0;
        int roiH = y1 - y0;

        if (roiW <= 0 || roiH <= 0)
            return 0f;

        float minLuma = 255f;
        float maxLuma = 0f;
        float sumLuma = 0f;
        int count = 0;

        for (int y = y0; y < y1; y++)
        {
            int row = y * width;

            for (int x = x0; x < x1; x++)
            {
                float l = Luma(pixels[row + x]);

                minLuma = Mathf.Min(minLuma, l);
                maxLuma = Mathf.Max(maxLuma, l);
                sumLuma += l;
                count++;
            }
        }

        if (count <= 0)
            return 0f;

        float meanLuma = sumLuma / count;
        float contrast = maxLuma - minLuma;

        // If the crop has very little contrast, it is probably not useful.
        if (contrast < 6f)
            return 0f;

        // Adaptive darkness threshold.
        // This finds pixels that are meaningfully darker than the local eye region.
        float darkThreshold = Mathf.Lerp(minLuma, meanLuma, 0.45f);

        int darkPixelCount = 0;
        int bestVerticalRun = 0;
        float totalUsefulRun = 0f;
        int usefulColumns = 0;

        for (int x = x0; x < x1; x++)
        {
            int currentRun = 0;
            int bestRunThisColumn = 0;

            for (int y = y0; y < y1; y++)
            {
                float l = Luma(pixels[y * width + x]);
                bool isDark = l <= darkThreshold;

                if (isDark)
                {
                    darkPixelCount++;
                    currentRun++;
                    bestRunThisColumn = Mathf.Max(bestRunThisColumn, currentRun);
                }
                else
                {
                    currentRun = 0;
                }
            }

            // Ignore tiny 1-pixel noise lines where possible.
            if (bestRunThisColumn >= 2)
            {
                totalUsefulRun += bestRunThisColumn;
                usefulColumns++;
            }

            bestVerticalRun = Mathf.Max(bestVerticalRun, bestRunThisColumn);
        }

        float darkArea01 = darkPixelCount / Mathf.Max(1f, (float)(roiW * roiH));

        float averageUsefulRun = usefulColumns > 0
            ? totalUsefulRun / usefulColumns
            : 0f;

        // Open eye should have a thicker dark iris/pupil area.
        // Closed eye usually has a thinner dark eyelid/eyelash line.
        float verticalBlob01 = averageUsefulRun / Mathf.Max(1f, roiH * 0.45f);

        // Area helps, but vertical thickness matters more.
        float score =
            verticalBlob01 * 0.75f +
            Mathf.Clamp01(darkArea01 / 0.22f) * 0.25f;

        // Reduce confidence when contrast is weak.
        float contrastConfidence = Mathf.Clamp01(contrast / 45f);

        return Mathf.Clamp01(score * contrastConfidence);
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

        StrongerEye01 = 0f;
        WeakerEye01 = 0f;
        ClosureDepth01 = 0f;
        CandidateAge = 0f;

        EyesClosed = false;
        BlinkThisFrame = false;
        IsCalibrating = false;

        blinkPhase = BlinkPhase.Idle;
        LastBlinkDecision = "Reset";

        trackingStartTime = -1f;
        candidateStartTime = 0f;
        closedStartTime = 0f;
        closedMinCombined01 = 1f;
        lastGoodSampleTime = -999f;
        lastBlinkTime = -999f;

        MotionSuppressed = false;
        OpenStableAge = 0f;
        openStableStartTime = -1f;
        suppressBlinkUntil = -999f;
        havePrevEyeRects = false;

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
            $"LChange:{LeftChange01:F3}  RChange:{RightChange01:F3}\n" +
            $"LNorm:{LeftNorm01:F3}  RNorm:{RightNorm01:F3}  Norm:{Combined01:F3}\n" +
            $"LSmooth:{LeftSmooth01:F3}  RSmooth:{RightSmooth01:F3}  Smooth:{Smoothed01:F3}\n" +
            $"LBase:{LeftBaseline:F6}  RBase:{RightBaseline:F6}  Calibrating:{IsCalibrating}\n" +
            $"Stronger:{StrongerEye01:F3}  Weaker:{WeakerEye01:F3}  Depth:{ClosureDepth01:F3}\n" +
            $"Closed:{EyesClosed}  BlinkThisFrame:{BlinkThisFrame}  BlinkCount:{BlinkCount}\n" +
            $"MotionSupp:{MotionSuppressed}  OpenStable:{OpenStableAge:F3}\n" +
            $"Phase:{BlinkPhaseName}  CandAge:{CandidateAge:F3}  MinComb:{closedMinCombined01:F3}\n" +
            $"LastDecision:{LastBlinkDecision}";

        GUI.Label(new Rect(10, 50, 1800, 320), text, debugTextStyle);

        if (showEyeSamplePreview)
        {
            float w = sampleWidth * 8f;
            float h = sampleHeight * 8f;

            float previewY = 380f;

            if (leftEyeTex != null)
                GUI.DrawTexture(new Rect(10, previewY, w, h), leftEyeTex, ScaleMode.StretchToFill, false);

            if (rightEyeTex != null)
                GUI.DrawTexture(new Rect(30 + w, previewY, w, h), rightEyeTex, ScaleMode.StretchToFill, false);
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