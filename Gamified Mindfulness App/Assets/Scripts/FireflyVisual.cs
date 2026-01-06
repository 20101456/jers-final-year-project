using UnityEngine;

public class FireflyVisual : MonoBehaviour
{
    public enum State { Shadowy, Calm }

    [Header("References")]
    public SpriteRenderer body;
    public SpriteRenderer glow;

    [Header("Tuning")]
    public float shadowFlickerSpeed = 18f;
    public float shadowFlickerAmount = 0.65f; // 0..1
    public float shadowJitterPixels = 1.5f;

    public float calmPulseSpeed = 2.0f;
    public float calmPulseAmount = 0.25f;

    State _state = State.Shadowy;
    Vector3 _baseLocalPos;

    void Awake()
    {
        _baseLocalPos = transform.localPosition;
        SetState(State.Shadowy);
    }

    public void SetState(State s)
    {
        _state = s;

        if (body != null)
            body.color = (s == State.Shadowy) ? new Color(0.1f, 0.1f, 0.12f, 1f) : new Color(1f, 0.95f, 0.75f, 1f);

        if (glow != null)
            glow.color = (s == State.Shadowy) ? new Color(0.4f, 0.4f, 0.5f, 0.25f) : new Color(1f, 0.85f, 0.45f, 0.6f);
    }

    void Update()
    {
        if (glow == null) return;

        if (_state == State.Shadowy)
        {
            // Erratic flicker
            float n = Mathf.PerlinNoise(Time.time * shadowFlickerSpeed, 0.123f);
            float a = Mathf.Lerp(0.15f, 1f, n) * shadowFlickerAmount;

            var c = glow.color;
            c.a = Mathf.Clamp01(0.15f + a);
            glow.color = c;

            // Tiny jitter
            float jx = (Mathf.PerlinNoise(Time.time * shadowFlickerSpeed, 2.2f) - 0.5f) * shadowJitterPixels;
            float jy = (Mathf.PerlinNoise(Time.time * shadowFlickerSpeed, 3.3f) - 0.5f) * shadowJitterPixels;
            transform.localPosition = _baseLocalPos + new Vector3(jx, jy, 0f);
        }
        else
        {
            // Calm candle-like rhythm
            float s = (Mathf.Sin(Time.time * calmPulseSpeed * Mathf.PI * 2f) + 1f) * 0.5f; // 0..1
            float a = 0.55f + s * calmPulseAmount;

            var c = glow.color;
            c.a = Mathf.Clamp01(a);
            glow.color = c;

            transform.localPosition = _baseLocalPos;
        }
    }
}
