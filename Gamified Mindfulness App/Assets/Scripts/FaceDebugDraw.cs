using UnityEngine;

public class FaceDebugDraw : MonoBehaviour
{
    public CameraFrameCorrector cameraFeed;
    public BlazeFaceSentis detector;
    public bool drawKeypoints = true;

    static Texture2D whiteTex;

    void OnGUI()
    {
        if (cameraFeed == null || detector == null) return;

        var tex = cameraFeed.CorrectedRT;
        if (tex == null) return;

        if (whiteTex == null)
        {
            whiteTex = new Texture2D(1, 1);
            whiteTex.SetPixel(0, 0, Color.white);
            whiteTex.Apply();
        }

        if (!detector.TryDetect(tex, out var det)) return;

        Rect viewRect = GetFittedScreenRect(tex.width, tex.height);

        float x = viewRect.x + det.faceRect01.x * viewRect.width;
        float y = viewRect.y + det.faceRect01.y * viewRect.height;
        float w = det.faceRect01.width * viewRect.width;
        float h = det.faceRect01.height * viewRect.height;

        DrawRectOutline(new Rect(x, y, w, h), 3f);

        if (drawKeypoints && det.kp01 != null)
        {
            foreach (var p in det.kp01)
            {
                float px = viewRect.x + p.x * viewRect.width;
                float py = viewRect.y + p.y * viewRect.height;
                GUI.DrawTexture(new Rect(px - 4, py - 4, 8, 8), whiteTex);
            }
        }

        GUI.Label(new Rect(10, 10, 500, 30), $"Face score: {det.score:F3}");
    }

    Rect GetFittedScreenRect(int texW, int texH)
    {
        float texAspect = (float)texW / texH;
        float screenAspect = (float)Screen.width / Screen.height;

        if (screenAspect > texAspect)
        {
            float h = Screen.height;
            float w = h * texAspect;
            float x = (Screen.width - w) * 0.5f;
            return new Rect(x, 0, w, h);
        }
        else
        {
            float w = Screen.width;
            float h = w / texAspect;
            float y = (Screen.height - h) * 0.5f;
            return new Rect(0, y, w, h);
        }
    }

    void DrawRectOutline(Rect r, float t)
    {
        GUI.DrawTexture(new Rect(r.x, r.y, r.width, t), whiteTex);
        GUI.DrawTexture(new Rect(r.x, r.yMax - t, r.width, t), whiteTex);
        GUI.DrawTexture(new Rect(r.x, r.y, t, r.height), whiteTex);
        GUI.DrawTexture(new Rect(r.xMax - t, r.y, t, r.height), whiteTex);
    }
}