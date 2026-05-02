using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Controls the rabbit mindfulness encounter.
///
/// The player must keep their eyes open for a set amount of time.
/// Blinks are read from EyeRegionTracker. Each blink can reset the focus timer,
/// and using more than the allowed number of blinks fails the encounter.
/// </summary>
public class RabbitEncounterController : MonoBehaviour
{
    public enum EncounterState
    {
        NotStarted,
        Calibrating,
        Running,
        Passed,
        Failed
    }

    [Header("Eye Tracking")]
    public EyeRegionTracker eyeTracker;

    [Header("Encounter Rules")]
    [Tooltip("How long the player must avoid blinking to pass.")]
    public float passSeconds = 10f;

    [Tooltip("Number of blinks allowed. The next blink after this fails the encounter.")]
    public int allowedBlinks = 3;

    [Tooltip("How long to show the keep-eyes-open calibration message at the start.")]
    public float startupCalibrationSeconds = 1.0f;

    [Tooltip("If enabled, each blink resets the focus timer.")]
    public bool resetTimerOnBlink = true;

    public bool startAutomatically = true;

    [Header("Rabbit Visuals")]
    public Image backgroundImage;
    public Sprite darkBackgroundSprite;
    public Sprite passedBackgroundSprite;

    public Image rabbitImage;
    public Sprite blackRabbitSprite;
    public Sprite whiteRabbitSprite;

    [Range(0.05f, 3f)]
    public float transformFadeSeconds = 0.75f;

    [Header("HUD")]
    public Image countdownFill;
    public TMP_Text blinkCounterText;

    [Header("Panels")]
    public GameObject calibrationPanel;
    public TMP_Text calibrationText;

    public GameObject passPanel;
    public GameObject failPanel;
    public TMP_Text resultText;

    public EncounterState State { get; private set; } = EncounterState.NotStarted;
    public float NoBlinkTimer { get; private set; }
    public int BlinksUsed { get; private set; }

    public int BlinksRemaining => Mathf.Max(0, allowedBlinks - BlinksUsed);

    Coroutine calibrationRoutine;
    Coroutine transformRoutine;

    int lastSeenTrackerBlinkCount;

    void Awake()
    {
        if (eyeTracker == null)
            eyeTracker = FindFirstObjectByType<EyeRegionTracker>();
    }

    void Start()
    {
        PrepareCountdownImage();

        if (startAutomatically)
            BeginEncounter();
        else
            ResetVisualsToStart();
    }

    void Update()
    {
        if (State != EncounterState.Running)
            return;

        if (HasNewTrackerBlink())
        {
            RegisterBlink();
            UpdateHUD();
            return;
        }

        NoBlinkTimer += Time.unscaledDeltaTime;

        if (NoBlinkTimer >= passSeconds)
        {
            PassEncounter();
            return;
        }

        UpdateHUD();
    }

    public void BeginEncounter()
    {
        StopActiveRoutines();

        State = EncounterState.Calibrating;
        NoBlinkTimer = 0f;
        BlinksUsed = 0;

        ResetVisualsToStart();
        TryRecalibrateEyeTracker();
        SyncBlinkCounterFromTracker();
        UpdateHUD();

        calibrationRoutine = StartCoroutine(StartAfterCalibrationRoutine());
    }

    IEnumerator StartAfterCalibrationRoutine()
    {
        SetPanel(calibrationPanel, true);
        SetPanel(passPanel, false);
        SetPanel(failPanel, false);

        if (calibrationText != null)
            calibrationText.text = "Keep your eyes open...";

        yield return new WaitForSecondsRealtime(startupCalibrationSeconds);

        SetPanel(calibrationPanel, false);

        SyncBlinkCounterFromTracker();

        State = EncounterState.Running;
        NoBlinkTimer = 0f;

        UpdateHUD();

        calibrationRoutine = null;
    }

    void RegisterBlink()
    {
        if (State != EncounterState.Running)
            return;

        BlinksUsed++;

        if (resetTimerOnBlink)
            NoBlinkTimer = 0f;

        // Example: if allowedBlinks is 3, the 4th blink fails the encounter.
        if (BlinksUsed > allowedBlinks)
            FailEncounter();
    }

    void PassEncounter()
    {
        if (State == EncounterState.Passed)
            return;

        State = EncounterState.Passed;
        NoBlinkTimer = passSeconds;

        SetPanel(calibrationPanel, false);
        SetPanel(failPanel, false);
        SetPanel(passPanel, true);

        if (resultText != null)
            resultText.text = "ENCOUNTER PASSED!";

        UpdateHUD();

        StopCalibrationRoutine();

        if (transformRoutine != null)
            StopCoroutine(transformRoutine);

        transformRoutine = StartCoroutine(TransformRabbitRoutine());
    }

    void FailEncounter()
    {
        if (State == EncounterState.Failed)
            return;

        State = EncounterState.Failed;

        SetPanel(calibrationPanel, false);
        SetPanel(passPanel, false);
        SetPanel(failPanel, true);

        if (resultText != null)
            resultText.text = "ENCOUNTER FAILED";

        UpdateHUD();

        StopActiveRoutines();
    }

    IEnumerator TransformRabbitRoutine()
    {
        if (rabbitImage == null || whiteRabbitSprite == null)
        {
            transformRoutine = null;
            yield break;
        }

        float halfTime = Mathf.Max(0.01f, transformFadeSeconds * 0.5f);

        yield return FadeImageAlpha(rabbitImage, 1f, 0f, halfTime);

        rabbitImage.sprite = whiteRabbitSprite;

        if (backgroundImage != null && passedBackgroundSprite != null)
            backgroundImage.sprite = passedBackgroundSprite;

        yield return FadeImageAlpha(rabbitImage, 0f, 1f, halfTime);

        transformRoutine = null;
    }

    IEnumerator FadeImageAlpha(Image image, float from, float to, float duration)
    {
        if (image == null)
            yield break;

        float elapsed = 0f;

        while (elapsed < duration)
        {
            elapsed += Time.unscaledDeltaTime;

            float t = Mathf.Clamp01(elapsed / duration);
            float alpha = Mathf.Lerp(from, to, t);

            SetImageAlpha(image, alpha);

            yield return null;
        }

        SetImageAlpha(image, to);
    }

    bool HasNewTrackerBlink()
    {
        if (eyeTracker == null)
            return false;

        int currentBlinkCount = eyeTracker.BlinkCount;

        // The tracker may have recalibrated and reset its own counter.
        if (currentBlinkCount < lastSeenTrackerBlinkCount)
        {
            lastSeenTrackerBlinkCount = currentBlinkCount;
            return false;
        }

        if (currentBlinkCount > lastSeenTrackerBlinkCount)
        {
            lastSeenTrackerBlinkCount = currentBlinkCount;
            return true;
        }

        return false;
    }

    void SyncBlinkCounterFromTracker()
    {
        lastSeenTrackerBlinkCount = eyeTracker != null ? eyeTracker.BlinkCount : 0;
    }

    void TryRecalibrateEyeTracker()
    {
        if (eyeTracker != null)
            eyeTracker.Recalibrate(true);
    }

    void ResetVisualsToStart()
    {
        if (backgroundImage != null && darkBackgroundSprite != null)
            backgroundImage.sprite = darkBackgroundSprite;

        if (rabbitImage != null && blackRabbitSprite != null)
        {
            rabbitImage.sprite = blackRabbitSprite;
            SetImageAlpha(rabbitImage, 1f);
        }

        SetPanel(passPanel, false);
        SetPanel(failPanel, false);
        SetPanel(calibrationPanel, false);

        if (resultText != null)
            resultText.text = "";
    }

    void UpdateHUD()
    {
        float progress01 = passSeconds > 0f
            ? Mathf.Clamp01(NoBlinkTimer / passSeconds)
            : 1f;

        // Countdown bar starts full and empties as the player maintains focus.
        float countdown01 = 1f - progress01;

        if (countdownFill != null)
            countdownFill.fillAmount = countdown01;

        if (blinkCounterText != null)
            blinkCounterText.text = "x" + BlinksRemaining;
    }

    void PrepareCountdownImage()
    {
        if (countdownFill == null)
            return;

        countdownFill.type = Image.Type.Filled;
        countdownFill.fillMethod = Image.FillMethod.Horizontal;
        countdownFill.fillOrigin = (int)Image.OriginHorizontal.Left;
        countdownFill.fillAmount = 1f;
    }

    void SetImageAlpha(Image image, float alpha)
    {
        if (image == null)
            return;

        Color colour = image.color;
        colour.a = alpha;
        image.color = colour;
    }

    void SetPanel(GameObject panel, bool visible)
    {
        if (panel != null)
            panel.SetActive(visible);
    }

    void StopActiveRoutines()
    {
        StopCalibrationRoutine();

        if (transformRoutine != null)
        {
            StopCoroutine(transformRoutine);
            transformRoutine = null;
        }
    }

    void StopCalibrationRoutine()
    {
        if (calibrationRoutine != null)
        {
            StopCoroutine(calibrationRoutine);
            calibrationRoutine = null;
        }
    }
}