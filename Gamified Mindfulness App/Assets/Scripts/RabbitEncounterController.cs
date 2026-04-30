using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

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

    [Tooltip("Number of blinks allowed before the encounter fails. More than this fails.")]
    public int allowedBlinks = 3;

    [Tooltip("How long to show the keep-eyes-open calibration message at the start.")]
    public float startupCalibrationSeconds = 1.0f;

    [Tooltip("A blink resets the 10 second focus timer.")]
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

    public int BlinksRemaining
    {
        get { return Mathf.Max(0, allowedBlinks - BlinksUsed); }
    }

    Coroutine activeRoutine;
    int lastSeenTrackerBlinkCount;

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

        bool blinkNow = HasNewTrackerBlink();

        if (blinkNow)
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
        StopAllCoroutines();

        State = EncounterState.Calibrating;
        NoBlinkTimer = 0f;
        BlinksUsed = 0;

        ResetVisualsToStart();
        TryRecalibrateEyeTracker();
        SyncBlinkCounterFromTracker();
        UpdateHUD();

        activeRoutine = StartCoroutine(StartAfterCalibrationRoutine());
    }

    IEnumerator StartAfterCalibrationRoutine()
    {
        if (calibrationPanel != null)
            calibrationPanel.SetActive(true);

        if (calibrationText != null)
            calibrationText.text = "Keep your eyes open...";

        yield return new WaitForSecondsRealtime(startupCalibrationSeconds);

        if (calibrationPanel != null)
            calibrationPanel.SetActive(false);

        SyncBlinkCounterFromTracker();
        State = EncounterState.Running;
        NoBlinkTimer = 0f;
        UpdateHUD();
    }

    void SyncBlinkCounterFromTracker()
    {
        lastSeenTrackerBlinkCount = eyeTracker != null ? eyeTracker.BlinkCount : 0;
    }

    bool HasNewTrackerBlink()
    {
        if (eyeTracker == null)
            return false;

        int currentBlinkCount = eyeTracker.BlinkCount;

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

    void RegisterBlink()
    {
        if (State != EncounterState.Running)
            return;

        BlinksUsed++;

        if (resetTimerOnBlink)
            NoBlinkTimer = 0f;

        // x3 means 3 blinks are allowed.
        // The 4th blink fails the encounter.
        if (BlinksUsed > allowedBlinks)
        {
            FailEncounter();
            return;
        }
    }

    void PassEncounter()
    {
        if (State == EncounterState.Passed)
            return;

        State = EncounterState.Passed;
        NoBlinkTimer = passSeconds;

        if (calibrationPanel != null)
            calibrationPanel.SetActive(false);

        if (failPanel != null)
            failPanel.SetActive(false);

        if (passPanel != null)
            passPanel.SetActive(true);

        if (resultText != null)
            resultText.text = "ENCOUNTER PASSED!";

        UpdateHUD();

        StopAllCoroutines();
        activeRoutine = StartCoroutine(TransformRabbitRoutine());
    }

    void FailEncounter()
    {
        if (State == EncounterState.Failed)
            return;

        State = EncounterState.Failed;

        if (calibrationPanel != null)
            calibrationPanel.SetActive(false);

        if (passPanel != null)
            passPanel.SetActive(false);

        if (failPanel != null)
            failPanel.SetActive(true);

        if (resultText != null)
            resultText.text = "ENCOUNTER FAILED";

        UpdateHUD();
    }

    IEnumerator TransformRabbitRoutine()
    {
        if (rabbitImage == null || whiteRabbitSprite == null)
            yield break;

        float halfTime = Mathf.Max(0.01f, transformFadeSeconds * 0.5f);

        yield return FadeImageAlpha(rabbitImage, 1f, 0f, halfTime);

        rabbitImage.sprite = whiteRabbitSprite;

        if (backgroundImage != null && passedBackgroundSprite != null)
            backgroundImage.sprite = passedBackgroundSprite;

        yield return FadeImageAlpha(rabbitImage, 0f, 1f, halfTime);
    }

    IEnumerator FadeImageAlpha(Image img, float from, float to, float duration)
    {
        if (img == null)
            yield break;

        float t = 0f;

        while (t < duration)
        {
            t += Time.unscaledDeltaTime;
            float a = Mathf.Lerp(from, to, t / duration);
            SetImageAlpha(img, a);
            yield return null;
        }

        SetImageAlpha(img, to);
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

        if (passPanel != null)
            passPanel.SetActive(false);

        if (failPanel != null)
            failPanel.SetActive(false);

        if (calibrationPanel != null)
            calibrationPanel.SetActive(false);

        if (resultText != null)
            resultText.text = "";
    }

    void UpdateHUD()
    {
        float progress01 = passSeconds > 0f
            ? Mathf.Clamp01(NoBlinkTimer / passSeconds)
            : 1f;

        // Countdown bar empties from full to empty.
        float countdown01 = 1f - progress01;

        if (countdownFill != null)
            countdownFill.fillAmount = countdown01;


        if (blinkCounterText != null)
            blinkCounterText.text = "x" + BlinksRemaining.ToString();
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

    void SetImageAlpha(Image img, float alpha)
    {
        if (img == null)
            return;

        Color c = img.color;
        c.a = alpha;
        img.color = c;
    }

    void TryRecalibrateEyeTracker()
    {
        if (eyeTracker == null)
            return;

        // This works once you add Recalibrate(bool) to EyeRegionTracker.
        // It also avoids compile errors if the method is not there yet.
        eyeTracker.gameObject.SendMessage(
            "Recalibrate",
            true,
            SendMessageOptions.DontRequireReceiver
        );
    }
}