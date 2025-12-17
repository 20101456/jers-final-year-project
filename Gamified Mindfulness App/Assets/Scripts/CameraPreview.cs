using UnityEngine;
using UnityEngine.UI;
#if UNITY_ANDROID
using UnityEngine.Android;
#endif

public class SimpleCameraPreview : MonoBehaviour
{
    [Header("UI Target")]
    public RawImage preview;

    [Header("Camera Choice")]
    public bool preferFrontFacing = true;

    WebCamTexture camTex;

    void Start()
    {
        StartCoroutine(StartCamera());
    }

    System.Collections.IEnumerator StartCamera()
    {
        // Runtime permission handling
#if UNITY_ANDROID
        if (!Permission.HasUserAuthorizedPermission(Permission.Camera))
        {
            Permission.RequestUserPermission(Permission.Camera);
            // Give Android a moment to show the prompt and return
            yield return new WaitForSeconds(0.5f);
        }
#endif

        // iOS + most platforms:
        // (This is also safe to call on Android; it will just return immediately if already granted)
        yield return Application.RequestUserAuthorization(UserAuthorization.WebCam);

        if (!Application.HasUserAuthorization(UserAuthorization.WebCam))
        {
            Debug.LogError("Camera permission denied.");
            yield break;
        }

        // Pick a camera device
        var devices = WebCamTexture.devices;
        if (devices == null || devices.Length == 0)
        {
            Debug.LogError("No camera devices found.");
            yield break;
        }

        int chosen = 0;
        if (preferFrontFacing)
        {
            for (int i = 0; i < devices.Length; i++)
            {
                if (devices[i].isFrontFacing) { chosen = i; break; }
            }
        }

        // Create & start texture
        camTex = new WebCamTexture(devices[chosen].name, 1280, 720, 30);
        camTex.Play();

        // Wait until the camera starts delivering frames (width becomes valid)
        while (camTex.width <= 16)
            yield return null;

        // Display on UI
        preview.texture = camTex;

        // Fix aspect ratio (optional but recommended)
        float aspect = (float)camTex.width / camTex.height;
        var fitter = preview.GetComponent<AspectRatioFitter>();
        if (!fitter) fitter = preview.gameObject.AddComponent<AspectRatioFitter>();
        fitter.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
        fitter.aspectRatio = aspect;

        // Mirror front camera so it feels natural (optional)
        preview.rectTransform.localScale = new Vector3(
            camTex.videoVerticallyMirrored ? 1 : 1,
            camTex.videoVerticallyMirrored ? -1 : 1,
            1
        );

        // Rotate to match device orientation
        preview.rectTransform.localEulerAngles = new Vector3(0, 0, -camTex.videoRotationAngle);
    }

    void OnDisable()
    {
        if (camTex != null)
        {
            if (camTex.isPlaying) camTex.Stop();
            Destroy(camTex);
            camTex = null;
        }
    }
}

