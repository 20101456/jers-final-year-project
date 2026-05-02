using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Opens the device camera and writes a corrected camera image into a RenderTexture.
///
/// The corrected RenderTexture is used by both the visual camera preview and the
/// face/eye tracking pipeline, so they all work from the same rotated/mirrored frame.
/// </summary>
public class CameraFrameCorrector : MonoBehaviour
{
    [Header("UI Preview")]
    [Tooltip("Optional RawImage used to show the corrected camera feed.")]
    public RawImage preview;

    [Tooltip("Optional fitter used to keep the preview at the corrected camera aspect ratio.")]
    public AspectRatioFitter aspectFitter;

    [Header("Shader")]
    [SerializeField] Shader cameraRotateFlipShader;

    [Header("Camera Request")]
    public int requestWidth = 640;
    public int requestHeight = 480;
    public int requestFPS = 30;

    [Header("Output Settings")]
    public bool mirrorFrontCamera = true;

    [Tooltip("0 = none, 1 = +90, 2 = +180, 3 = +270")]
    [Range(0, 3)] public int rotationOffset90 = 0;

    [Header("Debug")]
    public bool logCameraMeta = false;

    public RenderTexture CorrectedRT { get; private set; }
    public WebCamTexture CamTex { get; private set; }

    static readonly int Rot90ID = Shader.PropertyToID("_Rot90");
    static readonly int FlipXID = Shader.PropertyToID("_FlipX");
    static readonly int FlipYID = Shader.PropertyToID("_FlipY");

    Material blitMaterial;
    bool isFrontCamera;

    int lastLoggedRotation = -999;
    bool lastLoggedVerticalMirror;
    bool hasLoggedMeta;

    void Start()
    {
        if (!CreateBlitMaterial())
        {
            enabled = false;
            return;
        }

        if (!StartCamera())
        {
            enabled = false;
            return;
        }

        ResetPreviewTransform();
    }

    void Update()
    {
        if (!CameraIsReady())
            return;

        LogCameraMetadataIfNeeded();

        int rawWidth = CamTex.width;
        int rawHeight = CamTex.height;

        int rotation90 = GetCorrectedRotation90();
        bool swapsWidthAndHeight = (rotation90 & 1) == 1;

        int outputWidth = swapsWidthAndHeight ? rawHeight : rawWidth;
        int outputHeight = swapsWidthAndHeight ? rawWidth : rawHeight;

        EnsureRenderTexture(outputWidth, outputHeight);
        UpdateBlitMaterial(rotation90);

        Graphics.Blit(CamTex, CorrectedRT, blitMaterial);

        UpdatePreview(outputWidth, outputHeight);
    }

    bool CreateBlitMaterial()
    {
        Shader shader = cameraRotateFlipShader != null
            ? cameraRotateFlipShader
            : Shader.Find("Hidden/CameraRotateFlip");

        if (shader == null)
        {
            Debug.LogError("CameraFrameCorrector: Missing shader 'Hidden/CameraRotateFlip'.");
            return false;
        }

        blitMaterial = new Material(shader);
        return true;
    }

    bool StartCamera()
    {
        WebCamDevice[] devices = WebCamTexture.devices;

        if (devices == null || devices.Length == 0)
        {
            Debug.LogError("CameraFrameCorrector: No camera devices found.");
            return false;
        }

        WebCamDevice selectedDevice = SelectCamera(devices);

        isFrontCamera = selectedDevice.isFrontFacing;

        CamTex = new WebCamTexture(
            selectedDevice.name,
            requestWidth,
            requestHeight,
            requestFPS
        );

        CamTex.Play();

        return true;
    }

    WebCamDevice SelectCamera(WebCamDevice[] devices)
    {
        for (int i = 0; i < devices.Length; i++)
        {
            if (devices[i].isFrontFacing)
                return devices[i];
        }

        Debug.LogWarning("CameraFrameCorrector: No front camera found. Falling back to first available camera.");
        return devices[0];
    }

    bool CameraIsReady()
    {
        return CamTex != null && CamTex.isPlaying && CamTex.width > 16;
    }

    int GetCorrectedRotation90()
    {
        return ((CamTex.videoRotationAngle / 90) + rotationOffset90) & 3;
    }

    void UpdateBlitMaterial(int rotation90)
    {
        int flipX = isFrontCamera && mirrorFrontCamera ? 1 : 0;
        int flipY = CamTex.videoVerticallyMirrored ? 1 : 0;

        blitMaterial.SetFloat(Rot90ID, rotation90);
        blitMaterial.SetFloat(FlipXID, flipX);
        blitMaterial.SetFloat(FlipYID, flipY);
    }

    void ResetPreviewTransform()
    {
        if (preview == null)
            return;

        preview.texture = null;
        preview.rectTransform.localEulerAngles = Vector3.zero;
        preview.rectTransform.localScale = Vector3.one;
    }

    void UpdatePreview(int outputWidth, int outputHeight)
    {
        if (preview != null && preview.texture != CorrectedRT)
            preview.texture = CorrectedRT;

        if (aspectFitter != null && outputHeight > 0)
            aspectFitter.aspectRatio = (float)outputWidth / outputHeight;
    }

    void LogCameraMetadataIfNeeded()
    {
        if (!logCameraMeta)
            return;

        bool metadataChanged =
            !hasLoggedMeta ||
            lastLoggedRotation != CamTex.videoRotationAngle ||
            lastLoggedVerticalMirror != CamTex.videoVerticallyMirrored;

        if (!metadataChanged)
            return;

        Debug.Log(
            $"Camera meta -> raw={CamTex.width}x{CamTex.height}, " +
            $"videoRotationAngle={CamTex.videoRotationAngle}, " +
            $"videoVerticallyMirrored={CamTex.videoVerticallyMirrored}, " +
            $"isFront={isFrontCamera}"
        );

        lastLoggedRotation = CamTex.videoRotationAngle;
        lastLoggedVerticalMirror = CamTex.videoVerticallyMirrored;
        hasLoggedMeta = true;
    }

    void EnsureRenderTexture(int width, int height)
    {
        if (CorrectedRT != null && CorrectedRT.width == width && CorrectedRT.height == height)
            return;

        ReleaseCorrectedRenderTexture();

        CorrectedRT = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };

        CorrectedRT.Create();
    }

    void ReleaseCorrectedRenderTexture()
    {
        if (CorrectedRT == null)
            return;

        CorrectedRT.Release();
        Destroy(CorrectedRT);
        CorrectedRT = null;
    }

    void OnDestroy()
    {
        if (CamTex != null)
        {
            if (CamTex.isPlaying)
                CamTex.Stop();

            CamTex = null;
        }

        ReleaseCorrectedRenderTexture();

        if (blitMaterial != null)
        {
            Destroy(blitMaterial);
            blitMaterial = null;
        }
    }
}