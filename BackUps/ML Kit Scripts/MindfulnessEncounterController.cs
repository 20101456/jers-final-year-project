using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class MindfulnessEncounterController : MonoBehaviour
{
    [Header("Encounter")]
    [Range(6f, 10f)] public float targetSeconds = 8f;

    [Header("References")]
    public MonoBehaviour blinkSourceBehaviour; // must implement IBlinkSource
    public FireflyVisual firefly;
    public Image progressFill;
    public TMP_Text statusText;

    [Header("Passed UI")]
    public GameObject passedPanel;
    public Button continueButton;

    float _t;
    bool _passed;
    IBlinkSource _blinkSource;

    void Awake()
    {
        _blinkSource = blinkSourceBehaviour as IBlinkSource;
        if (_blinkSource != null)
            _blinkSource.Blinked += OnBlink;
    }

    void OnDestroy()
    {
        if (_blinkSource != null)
            _blinkSource.Blinked -= OnBlink;
    }

    void Start()
    {
        ResetTimer();
        if (firefly != null) firefly.SetState(FireflyVisual.State.Shadowy);

        if (passedPanel != null) passedPanel.SetActive(false);

        if (continueButton != null)
            continueButton.onClick.AddListener(OnContinueClicked);
    }

    void Update()
    {
        if (_passed) return;

        _t += Time.deltaTime;

        float p = Mathf.Clamp01(_t / targetSeconds);
        if (progressFill != null) progressFill.fillAmount = p;

        if (statusText != null)
            statusText.text = $"Hold…\n{_t:0.0}/{targetSeconds:0.0}s";

        if (_t >= targetSeconds)
            Pass();
    }

    void OnBlink()
    {
        if (_passed) return;

        ResetTimer();
        if (progressFill != null) progressFill.fillAmount = 0f;
        if (statusText != null) statusText.text = "Blink! Restarting…";
    }

    void ResetTimer() => _t = 0f;

    void Pass()
    {
        _passed = true;

        if (firefly != null) firefly.SetState(FireflyVisual.State.Calm);
        if (progressFill != null) progressFill.fillAmount = 1f;

        if (statusText != null) statusText.text = "Passed!";
        if (passedPanel != null) passedPanel.SetActive(true);
    }

    void OnContinueClicked()
{
    // If we’re launched from the forest route, exit back to it
    if (EncounterBus.ContinuePressed != null)
    {
        EncounterBus.RaiseContinue();
        return;
    }

    // Fallback: standalone testing inside encounter scene
    _passed = false;
    ResetTimer();
    if (firefly != null) firefly.SetState(FireflyVisual.State.Shadowy);
    if (passedPanel != null) passedPanel.SetActive(false);
}

}

