using System.Collections;
using UnityEngine;

/// <summary>
/// Requests camera permission before any WebCamTexture-based camera systems start.
/// This is required for the Android front-camera eye tracking pipeline.
/// </summary>
public class AndroidCameraPermission : MonoBehaviour
{
    public bool HasCameraPermission { get; private set; }

    IEnumerator Start()
    {
        yield return Application.RequestUserAuthorization(UserAuthorization.WebCam);

        HasCameraPermission = Application.HasUserAuthorization(UserAuthorization.WebCam);

        if (!HasCameraPermission)
        {
            Debug.LogWarning("Camera permission was not granted. Eye tracking will not be available.");
        }
    }
}
