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
            LeftBaseline = Mathf.Max(LeftBaseline, leftCandidate);
            RightBaseline = Mathf.Max(RightBaseline, rightCandidate);
        }
        else if (autoTrackOpenBaseline && blinkPhase == BlinkPhase.Idle)
        {
            float riseT = 1f - Mathf.Exp(-(baselineFallSpeed * 2f) * sampleInterval);
            float fallT = 1f - Mathf.Exp(-baselineFallSpeed * sampleInterval);

            bool leftLooksOpen = leftCandidate >= LeftBaseline * baselineTrackGate01;
            bool rightLooksOpen = rightCandidate >= RightBaseline * baselineTrackGate01;

            if (leftCandidate > LeftBaseline)
                LeftBaseline = Mathf.Lerp(LeftBaseline, leftCandidate, riseT);
            else if (leftLooksOpen)
                LeftBaseline = Mathf.Lerp(LeftBaseline, leftCandidate, fallT);

            if (rightCandidate > RightBaseline)
                RightBaseline = Mathf.Lerp(RightBaseline, rightCandidate, riseT);
            else if (rightLooksOpen)
                RightBaseline = Mathf.Lerp(RightBaseline, rightCandidate, fallT);
        }

        LeftBaseline = Mathf.Max(LeftBaseline, minBaseline);
        RightBaseline = Mathf.Max(RightBaseline, minBaseline);

        LeftNorm01 = Mathf.Clamp01(LeftRaw / LeftBaseline);
        RightNorm01 = Mathf.Clamp01(RightRaw / RightBaseline);
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
            return;
        }

        bool inCooldown = Time.unscaledTime - lastBlinkTime < blinkCooldown;

        bool candidateEnterNow =
            Smoothed01 < closedThreshold01 ||
            (WeakerEye01 < Mathf.Min(closedThreshold01 + closeSupportMargin01, reopenThreshold01 - 0.02f) &&
             Smoothed01 < reopenThreshold01 - 0.02f);

        bool fullyClosedNow =
            Smoothed01 < (closedThreshold01 - 0.02f) ||
            WeakerEye01 < closedThreshold01;

        bool reopenedNow =
            Smoothed01 > reopenThreshold01 ||
            (StrongerEye01 > (reopenThreshold01 - 0.04f) &&
             WeakerEye01 > (reopenThreshold01 - reopenSupportMargin01));

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

                    if (candidateEnterNow)
                    {
                        blinkPhase = BlinkPhase.Candidate;
                        candidateStartTime = Time.unscaledTime;
                        closedStartTime = candidateStartTime;
                        closedMinCombined01 = Smoothed01;
                        LastBlinkDecision = "Candidate";
                    }

                    break;
                }

            case BlinkPhase.Candidate:
                {
                    CandidateAge = Time.unscaledTime - candidateStartTime;
                    closedMinCombined01 = Mathf.Min(closedMinCombined01, Smoothed01);

                    if (reopenedNow)
                    {
                        LastBlinkDecision = "Rejected: too shallow";
                        blinkPhase = BlinkPhase.Idle;
                        closedMinCombined01 = 1f;
                        return;
                    }

                    if (fullyClosedNow)
                    {
                        blinkPhase = BlinkPhase.Closed;
                        EyesClosed = true;
                        LastBlinkDecision = "Closed";
                        return;
                    }

                    if (CandidateAge > maxCloseBuildTime)
                    {
                        LastBlinkDecision = "Rejected: never got deep enough";
                        blinkPhase = BlinkPhase.Idle;
                        closedMinCombined01 = 1f;
                    }

                    break;
                }

            case BlinkPhase.Closed:
                {
                    EyesClosed = true;
                    CandidateAge = Time.unscaledTime - candidateStartTime;
                    closedMinCombined01 = Mathf.Min(closedMinCombined01, Smoothed01);

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
                                    $"L={LeftSmooth01:F3} | R={RightSmooth01:F3} | minCombined={closedMinCombined01:F3}"
                                );
                            }
                        }
                        else
                        {
                            if (!reachedRequiredDepth)
                                LastBlinkDecision = "Rejected: too shallow";
                            else if (closedDuration < minBlinkClosedTime)
                                LastBlinkDecision = "Rejected: too fast";
                            else if (closedDuration > maxBlinkClosedTime)
                                LastBlinkDecision = "Rejected: too long";
                            else
                                LastBlinkDecision = "Rejected: cooldown";
                        }

                        EyesClosed = false;
                        blinkPhase = BlinkPhase.Idle;
                        closedMinCombined01 = 1f;
                        return;
                    }

                    if (CandidateAge > maxBlinkClosedTime)
                    {
                        LastBlinkDecision = "Rejected: never reopened";
                        EyesClosed = false;
                        blinkPhase = BlinkPhase.Idle;
                        closedMinCombined01 = 1f;
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
            $"Stronger:{StrongerEye01:F3}  Weaker:{WeakerEye01:F3}  Depth:{ClosureDepth01:F3}\n" +
            $"Phase:{BlinkPhaseName}  CandAge:{CandidateAge:F3}  MinComb:{closedMinCombined01:F3}\n" +
            $"Closed:{EyesClosed}  BlinkThisFrame:{BlinkThisFrame}  BlinkCount:{BlinkCount}\n" +
            $"LastDecision:{LastBlinkDecision}";

        GUI.Label(new Rect(10, 50, 1800, 320), text, debugTextStyle);

        if (showEyeSamplePreview)
        {
            float w = sampleWidth * 8f;
            float h = sampleHeight * 8f;

            if (leftEyeTex != null)
                GUI.DrawTexture(new Rect(10, 280, w, h), leftEyeTex, ScaleMode.StretchToFill, false);

            if (rightEyeTex != null)
                GUI.DrawTexture(new Rect(30 + w, 280, w, h), rightEyeTex, ScaleMode.StretchToFill, false);
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