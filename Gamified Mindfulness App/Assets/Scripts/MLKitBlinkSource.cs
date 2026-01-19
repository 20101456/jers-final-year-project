using System;
using UnityEngine;

public class MLKitBlinkSource : MonoBehaviour, IBlinkSource
{
    public event Action Blinked;

    [Header("Refs")]
    public BlinkCameraFeed cameraFeed;

    [Header("Tuning")]
    public float processHz = 8f;            // how often we send frames to ML Kit
    public float closeThresh = 0.25f;
    public float openThresh = 0.60f;
    public int minIntervalMs = 250;

    AndroidJavaClass _cls;
    float _nextTime;
    Color32[] _pixels;
    int[] _argb;

    void Awake()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        _cls = new AndroidJavaClass("com.wander.blink.MLKitBlink");
        _cls.CallStatic("init");
        _cls.CallStatic("setThresholds", closeThresh, openThresh, minIntervalMs);
#endif
    }

    void OnDestroy()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        _cls?.CallStatic("release");
#endif
    }

    void Update()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (cameraFeed == null || cameraFeed.CamTex == null || !cameraFeed.CamTex.isPlaying)
            return;

        if (Time.unscaledTime < _nextTime) return;
        _nextTime = Time.unscaledTime + (1f / Mathf.Max(1f, processHz));

        var cam = cameraFeed.CamTex;
        int w = cam.width;
        int h = cam.height;
        if (w <= 16 || h <= 16) return; // camera not ready yet

        // Get pixels
        _pixels = cam.GetPixels32(_pixels);

        int n = w * h;
        if (_argb == null || _argb.Length != n)
            _argb = new int[n];

        // Convert to 0xAARRGGBB
        for (int i = 0; i < n; i++)
        {
            var c = _pixels[i];
            _argb[i] = (c.a << 24) | (c.r << 16) | (c.g << 8) | c.b;
        }

        int rotation = cam.videoRotationAngle;
        _cls.CallStatic("process", _argb, w, h, rotation);

        if (_cls.CallStatic<bool>("consumeBlink"))
            Blinked?.Invoke();
#endif
    }
}

