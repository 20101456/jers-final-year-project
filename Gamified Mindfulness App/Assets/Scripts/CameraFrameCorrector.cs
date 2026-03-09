using UnityEngine;
using UnityEngine.UI;

public class CameraFrameCorrector : MonoBehaviour
{
    [Header("UI Preview (optional)")]
    public RawImage preview;
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

    [Tooltip("Toggle this if the preview is still upside down even when rotation looks correct.")]
    public bool invertReportedVerticalMirror = false;

    [Header("Debug")]
    public bool logCameraMeta = true;

    public RenderTexture CorrectedRT { get; private set; }
    public WebCamTexture CamTex { get; private set; }

    Material blitMat;
    bool isFront;

    int lastLoggedRot = -999;
    bool lastLoggedVMirror = false;
    bool hasLogged = false;

    static readonly int Rot90ID = Shader.PropertyToID("_Rot90");
    static readonly int FlipXID = Shader.PropertyToID("_FlipX");
    static readonly int FlipYID = Shader.PropertyToID("_FlipY");

    void Start()
    {
        Shader s = cameraRotateFlipShader != null
            ? cameraRotateFlipShader
            : Shader.Find("Hidden/CameraRotateFlip");

        if (s == null)
        {
            Debug.LogError("Missing shader: Hidden/CameraRotateFlip");
            enabled = false;
            return;
        }

        blitMat = new Material(s);

        var devices = WebCamTexture.devices;
        if (devices == null || devices.Length == 0)
        {
            Debug.LogError("No camera devices found.");
            enabled = false;
            return;
        }

        string camName = null;

        for (int i = 0; i < devices.Length; i++)
        {
            if (devices[i].isFrontFacing)
            {
                camName = devices[i].name;
                isFront = true;
                break;
            }
        }

        if (string.IsNullOrEmpty(camName))
        {
            camName = devices[0].name;
            isFront = devices[0].isFrontFacing;
        }

        CamTex = new WebCamTexture(camName, requestWidth, requestHeight, requestFPS);
        CamTex.Play();

        if (preview != null)
        {
            preview.texture = null;
            preview.rectTransform.localEulerAngles = Vector3.zero;
            preview.rectTransform.localScale = Vector3.one;
        }
    }

    void Update()
    {
        if (CamTex == null || !CamTex.isPlaying || CamTex.width <= 16)
            return;

        if (logCameraMeta &&
            (!hasLogged ||
             lastLoggedRot != CamTex.videoRotationAngle ||
             lastLoggedVMirror != CamTex.videoVerticallyMirrored))
        {
            Debug.Log(
                $"Camera meta -> raw={CamTex.width}x{CamTex.height}, " +
                $"videoRotationAngle={CamTex.videoRotationAngle}, " +
                $"videoVerticallyMirrored={CamTex.videoVerticallyMirrored}, " +
                $"isFront={isFront}"
            );

            lastLoggedRot = CamTex.videoRotationAngle;
            lastLoggedVMirror = CamTex.videoVerticallyMirrored;
            hasLogged = true;
        }

        int rawW = CamTex.width;
        int rawH = CamTex.height;

        int rot = ((CamTex.videoRotationAngle / 90) + rotationOffset90) & 3;
        bool swapWH = (rot & 1) == 1;

        int outW = swapWH ? rawH : rawW;
        int outH = swapWH ? rawW : rawH;

        EnsureRenderTexture(outW, outH);

        int flipX = (isFront && mirrorFrontCamera) ? 1 : 0;
        int flipY = CamTex.videoVerticallyMirrored ? 1 : 0;

        if (invertReportedVerticalMirror)
            flipY ^= 1;

        blitMat.SetFloat(Rot90ID, rot);
        blitMat.SetFloat(FlipXID, flipX);
        blitMat.SetFloat(FlipYID, flipY);

        Graphics.Blit(CamTex, CorrectedRT, blitMat);

        if (preview != null && preview.texture != CorrectedRT)
            preview.texture = CorrectedRT;

        if (aspectFitter != null)
            aspectFitter.aspectRatio = (float)outW / outH;
    }

    void EnsureRenderTexture(int width, int height)
    {
        if (CorrectedRT != null && CorrectedRT.width == width && CorrectedRT.height == height)
            return;

        if (CorrectedRT != null)
        {
            CorrectedRT.Release();
            Destroy(CorrectedRT);
        }

        CorrectedRT = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32);
        CorrectedRT.filterMode = FilterMode.Bilinear;
        CorrectedRT.wrapMode = TextureWrapMode.Clamp;
        CorrectedRT.Create();
    }

    void OnDestroy()
    {
        if (CamTex != null && CamTex.isPlaying)
            CamTex.Stop();

        if (CorrectedRT != null)
        {
            CorrectedRT.Release();
            Destroy(CorrectedRT);
        }

        if (blitMat != null)
            Destroy(blitMat);
    }
}