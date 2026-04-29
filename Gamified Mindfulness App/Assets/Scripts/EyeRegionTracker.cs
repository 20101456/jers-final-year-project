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
    [Tooltip("Normalised openness below this can start a blink candidate. 1 = fully open, 0 = closed.")]
    [Range(0.20f, 0.99f)] public float closedThreshold01 = 0.90f;

    [Tooltip("Normalised openness above this re-arms the detector after a blink.")]
    [Range(0.30f, 0.99f)] public float reopenThreshold01 = 0.94f;

    [Range(1f, 40f)] public float smoothingSpeed = 24f;
    [Range(0.01f, 0.25f)] public float minBlinkClosedTime = 0.025f;
    [Range(0.05f, 0.80f)] public float maxBlinkClosedTime = 0.55f;
    [Range(0.01f, 0.30f)] public float blinkCooldown = 0.12f;
    [Range(0.0f, 0.30f)] public float minClosedDepthBelowThreshold = 0.015f;

    [Header("Raw Drop Fallback - IMPORTANT")]
    [Tooltip("Keep this on. It catches blinks even if normalisation stays high.")]
    public bool useRawDropFallback = true;

    [Tooltip("Combined raw score must drop by at least this much to start a blink candidate.")]
    [Range(0.005f, 0.20f)] public float rawDropToEnter = 0.025f;

    [Tooltip("Combined raw score must drop by at least this much to count as genuinely closed.")]
    [Range(0.005f, 0.25f)] public float rawDropToClosed = 0.040f;

    [Tooltip("Combined raw / open baseline must be at or below this to count as closed.")]
    [Range(0.50f, 0.99f)] public float rawRatioClosed01 = 0.92f;

    [Tooltip("Combined raw / open baseline must recover to this to count as reopened.")]
    [Range(0.50f, 1.05f)] public float rawRatioReopen01 = 0.96f;

    [Header("Calibration / Baseline")]
    [Range(0.2f, 3f)] public float calibrationDuration = 1.0f;
    public bool autoTrackOpenBaseline = true;

    [Tooltip("How quickly the open-eye baseline can fall while the eyes are confidently open. Lower is safer.")]
    [Range(0.01f, 2f)] public float baselineFallSpeed = 0.20f;

    [Tooltip("How quickly the open-eye baseline rises when a stronger open-eye sample is seen.")]
    [Range(1f, 20f)] public float baselineRiseSpeed = 8f;

    [Range(0.001f, 0.2f)] public float minBaseline = 0.01f;

    [Header("Baseline Recenter Safety")]
    [Tooltip("Deadline-safe fallback. If the app is stuck thinking your open eyes are closed, it recenters the open baseline to the current eye score.")]
    public bool autoRecenterWhenStuck = true;

    [Tooltip("How long the tracker can stay unarmed before recentering the baseline.")]
    [Range(0.05f, 2f)] public float baselineRecenterDelay = 0.35f;

    [Tooltip("How fast the baseline recenters when stuck. Higher = fixes startup/position changes faster.")]
    [Range(0.5f, 25f)] public float baselineRecenterSpeed = 10f;

    [Tooltip("If RawRatio is below this while idle and unarmed, the baseline is probably too high.")]
    [Range(0.50f, 0.99f)] public float recenterIfRatioBelow = 0.88f;


    [Header("Template Difference Blink - PRIMARY DEADLINE DETECTOR")]
    [Tooltip("Uses the first open-eye frames as a visual template, then detects blinks as image changes from that open template. This is more reliable than raw brightness on light-coloured eyes.")]
    public bool useTemplateDifference = true;

    [Tooltip("Template difference needed to start a blink candidate. Raise if false positives happen; lower if blinks are missed.")]
    [Range(0.01f, 0.40f)] public float templateDiffToEnter = 0.060f;

    [Tooltip("Template difference needed to confirm the eyes are closed.")]
    [Range(0.01f, 0.50f)] public float templateDiffToClosed = 0.085f;

    [Tooltip("Template difference must fall back below this to count as reopened.")]
    [Range(0.005f, 0.30f)] public float templateDiffToReopen = 0.045f;

    [Tooltip("How quickly the open-eye template learns during the first second. Keep eyes open at startup.")]
    [Range(1f, 30f)] public float templateCalibrationSpeed = 12f;

    [Tooltip("How slowly the open-eye template adapts while confidently open.")]
    [Range(0.01f, 5f)] public float templateOpenTrackSpeed = 0.75f;

    [Tooltip("If the tracker is stuck unarmed, recenter the template to the current open-eye view. Keep eyes open when this appears.")]
    public bool autoRecenterTemplateWhenStuck = true;

    [Range(0.10f, 2.0f)] public float templateRecenterDelay = 0.75f;
    [Range(1f, 30f)] public float templateRecenterSpeed = 14f;

    [Header("Tracking Stability")]
    [Range(0.05f, 1.0f)] public float lostTrackingResetDelay = 0.25f;
    [Range(0.80f, 1.00f)] public float baselineTrackGate01 = 0.94f;

    [Header("Blink Event Logic")]
    [Range(0.01f, 0.30f)] public float maxCloseBuildTime = 0.20f;

    [Header("Blink Rearm")]
    [Range(0.01f, 0.30f)] public float minOpenStableTime = 0.05f;

    [Header("Motion Rejection")]
    [Range(0.05f, 0.50f)] public float rectMoveRejectFrac = 0.30f;
    [Range(0.05f, 0.50f)] public float rectSizeRejectFrac = 0.25f;
    [Range(0.01f, 0.25f)] public float motionSuppressTime = 0.05f;

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
    public bool MotionSuppressed { get; private set; }
    public float OpenStableAge { get; private set; }
    public bool BlinkArmed { get; private set; }
    public bool BaselineRecenterActive { get; private set; }
    public bool TemplateReady { get; private set; }
    public bool TemplateRecenterActive { get; private set; }
    public float TemplateDiff01 { get; private set; }
    public float LeftTemplateDiff01 { get; private set; }
    public float RightTemplateDiff01 { get; private set; }

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
    float suppressBlinkUntil = -999f;
    float openStableStartTime = -1f;
    float unarmedSince = -1f;

    float rawRatio01 = 1f;
    float rawDrop = 0f;
    bool rawCandidateNow;
    bool rawClosedNow;
    bool normCandidateNow;
    bool normClosedNow;
    bool templateCandidateNow;
    bool templateClosedNow;
    bool templateOpenNow;
    float[] leftOpenTemplate;
    float[] rightOpenTemplate;
    float templateUnarmedSince = -1f;

    Rect prevLeftEyeRect01;
    Rect prevRightEyeRect01;
    bool havePrevEyeRects;

    GUIStyle debugTextStyle;

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

        bool leftOk = SampleEye(src, faceDebug.LeftEyeRect01, leftEyeRT, leftEyeTex, out float leftScore, out Color32[] leftPixels, out string leftMsg);
        bool rightOk = SampleEye(src, faceDebug.RightEyeRect01, rightEyeRT, rightEyeTex, out float rightScore, out Color32[] rightPixels, out string rightMsg);

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

        UpdateCalibrationAndSignals();
        UpdateTemplateDifference(leftPixels, rightPixels);
        UpdateBlinkState();
        UpdateOpenBaselineAfterBlinkDecision();
        UpdateOpenTemplateAfterBlinkDecision(leftPixels, rightPixels);
    }

    void UpdateCalibrationAndSignals()
    {
        float trackedTime = Time.unscaledTime - trackingStartTime;
        IsCalibrating = trackedTime < calibrationDuration;

        float leftCandidate = Mathf.Max(LeftRaw, minBaseline);
        float rightCandidate = Mathf.Max(RightRaw, minBaseline);

        if (LeftBaseline <= 0f) LeftBaseline = leftCandidate;
        if (RightBaseline <= 0f) RightBaseline = rightCandidate;

        if (IsCalibrating)
        {
            // During calibration, assume eyes are open. Keep your eyes open for the first second.
            float t = 1f - Mathf.Exp(-5f * sampleInterval);
            LeftBaseline = Mathf.Lerp(LeftBaseline, leftCandidate, t);
            RightBaseline = Mathf.Lerp(RightBaseline, rightCandidate, t);
        }

        ComputeSignalsFromCurrentBaseline();
    }

    void ComputeSignalsFromCurrentBaseline()
    {
        LeftBaseline = Mathf.Max(LeftBaseline, minBaseline);
        RightBaseline = Mathf.Max(RightBaseline, minBaseline);

        LeftNorm01 = Mathf.Clamp01(LeftRaw / Mathf.Max(LeftBaseline, minBaseline));
        RightNorm01 = Mathf.Clamp01(RightRaw / Mathf.Max(RightBaseline, minBaseline));
        Combined01 = 0.5f * (LeftNorm01 + RightNorm01);

        LeftChange01 = Mathf.Clamp01(1f - LeftNorm01);
        RightChange01 = Mathf.Clamp01(1f - RightNorm01);

        rawRatio01 = Mathf.Clamp(CombinedRaw / Mathf.Max(OpenBaseline, minBaseline), 0f, 2f);
        rawDrop = Mathf.Max(0f, OpenBaseline - CombinedRaw);

        float smoothT = 1f - Mathf.Exp(-smoothingSpeed * sampleInterval);

        if (LeftSmooth01 <= 0f) LeftSmooth01 = LeftNorm01;
        else LeftSmooth01 = Mathf.Lerp(LeftSmooth01, LeftNorm01, smoothT);

        if (RightSmooth01 <= 0f) RightSmooth01 = RightNorm01;
        else RightSmooth01 = Mathf.Lerp(RightSmooth01, RightNorm01, smoothT);

        Smoothed01 = 0.5f * (LeftSmooth01 + RightSmooth01);

        StrongerEye01 = Mathf.Max(LeftSmooth01, RightSmooth01);
        WeakerEye01 = Mathf.Min(LeftSmooth01, RightSmooth01);
        ClosureDepth01 = 1f - Combined01;

        normCandidateNow = Combined01 <= closedThreshold01;
        normClosedNow = Combined01 <= (closedThreshold01 - minClosedDepthBelowThreshold);

        rawCandidateNow =
            useRawDropFallback &&
            rawDrop >= rawDropToEnter &&
            rawRatio01 <= Mathf.Max(rawRatioClosed01 + 0.05f, rawRatioReopen01 - 0.02f);

        rawClosedNow =
            useRawDropFallback &&
            rawDrop >= rawDropToClosed &&
            rawRatio01 <= rawRatioClosed01;
    }


    void UpdateTemplateDifference(Color32[] leftPixels, Color32[] rightPixels)
    {
        TemplateRecenterActive = false;

        int expected = sampleWidth * sampleHeight;
        if (!useTemplateDifference || leftPixels == null || rightPixels == null || leftPixels.Length != expected || rightPixels.Length != expected)
        {
            TemplateReady = false;
            TemplateDiff01 = 0f;
            templateCandidateNow = false;
            templateClosedNow = false;
            templateOpenNow = false;
            return;
        }

        EnsureTemplates(expected, leftPixels, rightPixels);

        // During calibration, learn what the user's open eyes look like.
        // Startup should be: eyes open, face steady, about one second.
        if (IsCalibrating)
        {
            float t = 1f - Mathf.Exp(-templateCalibrationSpeed * sampleInterval);
            BlendTemplate(leftOpenTemplate, leftPixels, t);
            BlendTemplate(rightOpenTemplate, rightPixels, t);
            TemplateReady = false;
        }
        else
        {
            TemplateReady = true;
        }

        RecomputeTemplateBooleans(leftPixels, rightPixels);
    }

    void UpdateOpenTemplateAfterBlinkDecision(Color32[] leftPixels, Color32[] rightPixels)
    {
        TemplateRecenterActive = false;

        if (!useTemplateDifference || !TemplateReady || IsCalibrating || leftPixels == null || rightPixels == null)
            return;

        if (leftOpenTemplate == null || rightOpenTemplate == null)
            return;

        if (blinkPhase != BlinkPhase.Idle || EyesClosed || MotionSuppressed)
        {
            templateUnarmedSince = -1f;
            return;
        }

        // Main fix:
        // If the tracker is idle and unarmed, but the template says the eyes are not open,
        // allow the open-eye template to recenter. The old code refused to do this when
        // TCand/TClosed were true, which is exactly the stuck case seen in your screenshots.
        bool stuckUnarmed =
            autoRecenterTemplateWhenStuck &&
            !BlinkArmed &&
            OpenStableAge <= 0.001f &&
            TemplateDiff01 > templateDiffToReopen;

        if (stuckUnarmed)
        {
            if (templateUnarmedSince < 0f)
                templateUnarmedSince = Time.unscaledTime;

            float stuckAge = Time.unscaledTime - templateUnarmedSince;

            if (stuckAge >= templateRecenterDelay)
            {
                TemplateRecenterActive = true;

                float recenterT = 1f - Mathf.Exp(-templateRecenterSpeed * sampleInterval);

                BlendTemplate(leftOpenTemplate, leftPixels, recenterT);
                BlendTemplate(rightOpenTemplate, rightPixels, recenterT);
                RecomputeTemplateBooleans(leftPixels, rightPixels);

                LastBlinkDecision =
                    $"Recentering eye template | templ={TemplateDiff01:F3}. Keep eyes open.";
            }

            return;
        }

        templateUnarmedSince = -1f;

        // Normal slow learning while confidently open.
        if (TemplateDiff01 <= templateDiffToReopen)
        {
            float t = 1f - Mathf.Exp(-templateOpenTrackSpeed * sampleInterval);

            BlendTemplate(leftOpenTemplate, leftPixels, t);
            BlendTemplate(rightOpenTemplate, rightPixels, t);
            RecomputeTemplateBooleans(leftPixels, rightPixels);
        }
    }

    void EnsureTemplates(int expected, Color32[] leftPixels, Color32[] rightPixels)
    {
        if (leftOpenTemplate != null && rightOpenTemplate != null && leftOpenTemplate.Length == expected && rightOpenTemplate.Length == expected)
            return;

        leftOpenTemplate = new float[expected];
        rightOpenTemplate = new float[expected];

        for (int i = 0; i < expected; i++)
        {
            leftOpenTemplate[i] = Luma(leftPixels[i]);
            rightOpenTemplate[i] = Luma(rightPixels[i]);
        }
    }

    void BlendTemplate(float[] template, Color32[] pixels, float t)
    {
        if (template == null || pixels == null)
            return;

        int count = Mathf.Min(template.Length, pixels.Length);
        for (int i = 0; i < count; i++)
            template[i] = Mathf.Lerp(template[i], Luma(pixels[i]), t);
    }

    float MeanAbsLumaDifference01(float[] template, Color32[] pixels)
    {
        if (template == null || pixels == null)
            return 0f;

        int count = Mathf.Min(template.Length, pixels.Length);
        if (count <= 0)
            return 0f;

        float sum = 0f;
        for (int i = 0; i < count; i++)
            sum += Mathf.Abs(Luma(pixels[i]) - template[i]);

        return Mathf.Clamp01((sum / count) / 255f);
    }

    void RecomputeTemplateBooleans(Color32[] leftPixels, Color32[] rightPixels)
    {
        LeftTemplateDiff01 = MeanAbsLumaDifference01(leftOpenTemplate, leftPixels);
        RightTemplateDiff01 = MeanAbsLumaDifference01(rightOpenTemplate, rightPixels);
        TemplateDiff01 = 0.5f * (LeftTemplateDiff01 + RightTemplateDiff01);

        templateCandidateNow = TemplateReady && TemplateDiff01 >= templateDiffToEnter;
        templateClosedNow = TemplateReady && TemplateDiff01 >= templateDiffToClosed;
        templateOpenNow = !TemplateReady || TemplateDiff01 <= templateDiffToReopen;
    }

    void UpdateOpenBaselineAfterBlinkDecision()
    {
        BaselineRecenterActive = false;

        if (!autoTrackOpenBaseline || IsCalibrating)
            return;

        if (blinkPhase != BlinkPhase.Idle || EyesClosed || MotionSuppressed)
        {
            unarmedSince = -1f;
            return;
        }

        bool confidentlyOpen =
            OpenStableAge >= minOpenStableTime &&
            Combined01 >= baselineTrackGate01 &&
            rawRatio01 >= rawRatioReopen01 &&
            rawDrop < rawDropToEnter;

        if (confidentlyOpen)
        {
            unarmedSince = -1f;
            TrackBaselineTowardCurrentSample(baselineRiseSpeed, baselineFallSpeed);
            ComputeSignalsFromCurrentBaseline();
            return;
        }

        // IMPORTANT:
        // Do NOT let the template detector block baseline recentering.
        // Your screenshots show the template can say TCand/TClosed while the eyes are actually open.
        bool looksStuckUnarmed =
            autoRecenterWhenStuck &&
            !BlinkArmed &&
            OpenStableAge <= 0.001f &&
            blinkPhase == BlinkPhase.Idle &&
            (rawRatio01 < recenterIfRatioBelow || Combined01 < closedThreshold01);

        if (!looksStuckUnarmed)
        {
            unarmedSince = -1f;
            return;
        }

        if (unarmedSince < 0f)
            unarmedSince = Time.unscaledTime;

        float unarmedAge = Time.unscaledTime - unarmedSince;

        if (unarmedAge < baselineRecenterDelay)
            return;

        BaselineRecenterActive = true;

        float recenterT = 1f - Mathf.Exp(-baselineRecenterSpeed * sampleInterval);

        LeftBaseline = Mathf.Lerp(LeftBaseline, Mathf.Max(LeftRaw, minBaseline), recenterT);
        RightBaseline = Mathf.Lerp(RightBaseline, Mathf.Max(RightRaw, minBaseline), recenterT);

        ComputeSignalsFromCurrentBaseline();

        LastBlinkDecision =
            $"Recentering open baseline | ratio={rawRatio01:F3} drop={rawDrop:F3}. Keep eyes open.";
    }

    void TrackBaselineTowardCurrentSample(float riseSpeed, float fallSpeed)
    {
        float riseT = 1f - Mathf.Exp(-riseSpeed * sampleInterval);
        float fallT = 1f - Mathf.Exp(-fallSpeed * sampleInterval);

        float leftTarget = Mathf.Max(LeftRaw, minBaseline);
        float rightTarget = Mathf.Max(RightRaw, minBaseline);

        LeftBaseline = Mathf.Lerp(LeftBaseline, leftTarget, LeftRaw > LeftBaseline ? riseT : fallT);
        RightBaseline = Mathf.Lerp(RightBaseline, rightTarget, RightRaw > RightBaseline ? riseT : fallT);
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
            BlinkArmed = false;
            LastBlinkDecision = "Calibrating - keep eyes open";
            return;
        }

        // Raw/baseline is now the primary system.
        bool rawOpenNow =
            rawRatio01 >= rawRatioReopen01 &&
            rawDrop < rawDropToEnter;

        bool normOpenNow =
            Combined01 >= reopenThreshold01;

        bool openStableNow = rawOpenNow || normOpenNow;

        // Reopening should be strict. Do not use templateOpenNow here.
        // Your screenshots show TOpen=True while your eyes are actually closed.
        bool reopenedNow = rawOpenNow || normOpenNow;

        if (openStableNow)
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

        bool templateStrongClosed =
            useTemplateDifference &&
            TemplateReady &&
            templateClosedNow &&
            TemplateDiff01 >= templateDiffToClosed;

        bool candidateEnterNow =
            normCandidateNow ||
            rawCandidateNow ||
            templateStrongClosed;

        bool fullyClosedNow =
            normClosedNow ||
            rawClosedNow ||
            templateStrongClosed;

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

                    if (!BlinkArmed)
                    {
                        LastBlinkDecision =
                            $"Waiting open-stable | armed={BlinkArmed} norm={Combined01:F3} rawRatio={rawRatio01:F3} rawDrop={rawDrop:F3}";
                        return;
                    }

                    if (candidateEnterNow)
                    {
                        BlinkArmed = false;
                        candidateStartTime = Time.unscaledTime;
                        closedStartTime = candidateStartTime;
                        closedMinCombined01 = Combined01;

                        if (fullyClosedNow)
                        {
                            blinkPhase = BlinkPhase.Closed;
                            EyesClosed = true;
                            LastBlinkDecision =
                                $"Closed direct | norm={Combined01:F3} rawRatio={rawRatio01:F3} rawDrop={rawDrop:F3}";
                        }
                        else
                        {
                            blinkPhase = BlinkPhase.Candidate;
                            LastBlinkDecision =
                                $"Candidate | norm={Combined01:F3} rawRatio={rawRatio01:F3} rawDrop={rawDrop:F3}";
                        }

                        return;
                    }

                    LastBlinkDecision =
                        $"Idle watching | norm={Combined01:F3} rawRatio={rawRatio01:F3} rawDrop={rawDrop:F3}";
                    return;
                }

            case BlinkPhase.Candidate:
                {
                    CandidateAge = Time.unscaledTime - candidateStartTime;
                    closedMinCombined01 = Mathf.Min(closedMinCombined01, Combined01);

                    if (fullyClosedNow)
                    {
                        blinkPhase = BlinkPhase.Closed;
                        EyesClosed = true;
                        LastBlinkDecision =
                            $"Closed | norm={Combined01:F3} rawRatio={rawRatio01:F3} rawDrop={rawDrop:F3}";
                        return;
                    }

                    if (reopenedNow)
                    {
                        LastBlinkDecision =
                            $"Rejected shallow | min={closedMinCombined01:F3} rawRatio={rawRatio01:F3}";
                        ResetBlinkPhaseOnly();
                        return;
                    }

                    if (CandidateAge > maxCloseBuildTime)
                    {
                        LastBlinkDecision =
                            $"Rejected slow close | min={closedMinCombined01:F3} rawRatio={rawRatio01:F3}";
                        ResetBlinkPhaseOnly();
                        return;
                    }

                    LastBlinkDecision =
                        $"Candidate building | age={CandidateAge:F3} rawRatio={rawRatio01:F3}";
                    return;
                }

            case BlinkPhase.Closed:
                {
                    EyesClosed = true;
                    CandidateAge = Time.unscaledTime - candidateStartTime;
                    closedMinCombined01 = Mathf.Min(closedMinCombined01, Combined01);

                    if (reopenedNow)
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

                            LastBlinkDecision = $"Blink accepted ({closedDuration:F3}s)";

                            if (logBlinkEvents)
                            {
                                Debug.Log(
                                    $"Blink detected | count={BlinkCount} | duration={closedDuration:F3}s | " +
                                    $"normMin={closedMinCombined01:F3} | rawRatio={rawRatio01:F3} | rawDrop={rawDrop:F3}"
                                );
                            }
                        }
                        else
                        {
                            if (closedDuration < minBlinkClosedTime)
                                LastBlinkDecision = $"Rejected too fast | {closedDuration:F3}s";
                            else if (closedDuration > maxBlinkClosedTime)
                                LastBlinkDecision = $"Rejected too long | {closedDuration:F3}s";
                            else
                                LastBlinkDecision = "Rejected cooldown";
                        }

                        ResetBlinkPhaseOnly();
                        return;
                    }

                    if (CandidateAge > maxBlinkClosedTime)
                    {
                        LastBlinkDecision = $"Rejected never reopened | age={CandidateAge:F3}";
                        ResetBlinkPhaseOnly();
                        return;
                    }

                    LastBlinkDecision =
                        $"Closed holding | age={CandidateAge:F3} rawRatio={rawRatio01:F3} rawDrop={rawDrop:F3}";
                    return;
                }
        }
    }

    void ResetBlinkPhaseOnly()
    {
        EyesClosed = false;
        blinkPhase = BlinkPhase.Idle;
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
        CandidateAge = 0f;

        if (blinkPhase != BlinkPhase.Idle)
            LastBlinkDecision = "Rejected: tracking lost";

        ResetBlinkPhaseOnly();

        if (lastGoodSampleTime >= 0f && Time.unscaledTime - lastGoodSampleTime > lostTrackingResetDelay)
        {
            InvalidateTracking(false);
            LastSampleStatus += " | hard reset";
        }
    }

    bool SampleEye(RenderTexture src, Rect eyeRect01TopLeft, RenderTexture dst, Texture2D dstTex, out float score, out Color32[] pixels, out string message)
    {
        pixels = null;
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

        pixels = dstTex.GetPixels32();

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

            if (blinkPhase != BlinkPhase.Idle)
                LastBlinkDecision = "Rejected: motion";

            ResetBlinkPhaseOnly();
        }

        MotionSuppressed = Time.unscaledTime < suppressBlinkUntil;
        return MotionSuppressed;
    }

    float ComputeEyeOpennessScore(Color32[] pixels, int width, int height)
    {
        if (pixels == null || pixels.Length < width * height || width < 4 || height < 4)
            return 0f;

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

        if (contrast < 6f)
            return 0f;

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

        float verticalBlob01 = averageUsefulRun / Mathf.Max(1f, roiH * 0.45f);

        float score =
            verticalBlob01 * 0.75f +
            Mathf.Clamp01(darkArea01 / 0.22f) * 0.25f;

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
        LeftChange01 = 0f;
        RightChange01 = 0f;
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
        BlinkArmed = false;
        BaselineRecenterActive = false;
        TemplateReady = false;
        TemplateRecenterActive = false;
        TemplateDiff01 = 0f;
        LeftTemplateDiff01 = 0f;
        RightTemplateDiff01 = 0f;
        templateCandidateNow = false;
        templateClosedNow = false;
        templateOpenNow = false;
        templateUnarmedSince = -1f;
        leftOpenTemplate = null;
        rightOpenTemplate = null;
        openStableStartTime = -1f;
        unarmedSince = -1f;
        suppressBlinkUntil = -999f;
        havePrevEyeRects = false;

        rawRatio01 = 1f;
        rawDrop = 0f;
        rawCandidateNow = false;
        rawClosedNow = false;
        normCandidateNow = false;
        normClosedNow = false;

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
            $"LBase:{LeftBaseline:F6}  RBase:{RightBaseline:F6}  Base:{OpenBaseline:F6}\n" +
            $"RawRatio:{rawRatio01:F3}  RawDrop:{rawDrop:F3}  RawCand:{rawCandidateNow}  RawClosed:{rawClosedNow}\n" +
            $"TemplateReady:{TemplateReady}  TDiff:{TemplateDiff01:F3}  TCand:{templateCandidateNow}  TClosed:{templateClosedNow}  TOpen:{templateOpenNow}\n" +
            $"LNorm:{LeftNorm01:F3}  RNorm:{RightNorm01:F3}  Norm:{Combined01:F3}\n" +
            $"LSmooth:{LeftSmooth01:F3}  RSmooth:{RightSmooth01:F3}  Smooth:{Smoothed01:F3}\n" +
            $"NormCand:{normCandidateNow}  NormClosed:{normClosedNow}  Calibrating:{IsCalibrating}\n" +
            $"Closed:{EyesClosed}  BlinkThisFrame:{BlinkThisFrame}  BlinkCount:{BlinkCount}\n" +
            $"MotionSupp:{MotionSuppressed}  OpenStable:{OpenStableAge:F3}  Armed:{BlinkArmed}  Recenter:{BaselineRecenterActive}  TemplateRecenter:{TemplateRecenterActive}\n" +
            $"Phase:{BlinkPhaseName}  CandAge:{CandidateAge:F3}  MinComb:{closedMinCombined01:F3}\n" +
            $"LastDecision:{LastBlinkDecision}";

        GUI.Label(new Rect(10, 50, 1800, 380), text, debugTextStyle);

        if (showEyeSamplePreview)
        {
            float w = sampleWidth * 8f;
            float h = sampleHeight * 8f;
            float previewY = 430f;

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
