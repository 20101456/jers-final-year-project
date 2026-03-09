using UnityEngine;
using UnityEngine.Android;

public class BlinkCameraFeed : MonoBehaviour
{
    public int requestedWidth = 480;
    public int requestedHeight = 360;
    public int requestedFps = 15;

    public WebCamTexture CamTex { get; private set; }

    void Start()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!Permission.HasUserAuthorizedPermission(Permission.Camera))
            Permission.RequestUserPermission(Permission.Camera);
#endif
        StartFrontCamera();
    }

    void StartFrontCamera()
    {
        var devices = WebCamTexture.devices;
        if (devices == null || devices.Length == 0)
        {
            Debug.LogError("No camera devices found.");
            return;
        }

        string chosen = devices[0].name;
        foreach (var d in devices)
        {
            if (d.isFrontFacing) { chosen = d.name; break; }
        }

        CamTex = new WebCamTexture(chosen, requestedWidth, requestedHeight, requestedFps);
        CamTex.Play();
    }

    void OnDestroy()
    {
        if (CamTex != null && CamTex.isPlaying)
            CamTex.Stop();
    }
}
