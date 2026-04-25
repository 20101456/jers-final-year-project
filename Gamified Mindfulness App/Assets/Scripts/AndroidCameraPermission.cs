using System.Collections;
using UnityEngine;

public class AndroidCameraPermission : MonoBehaviour
{
    IEnumerator Start()
    {
        // On Android, request camera permission before starting WebCamTexture
        yield return Application.RequestUserAuthorization(UserAuthorization.WebCam);
    }
}
