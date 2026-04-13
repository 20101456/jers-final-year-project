using UnityEngine;

public class FaceDebugDraw : MonoBehaviour
{
    public CameraFrameCorrector cameraFeed;
    public BlazeFaceSentis detector;

    [Header("Face Debug")]
    public bool drawFaceBox = true;
    public bool drawKeypoints = true;
    public bool showStatus = true;
    public bool drawCenterTestMarker = false;

    [Header("Eye Debug")]
    public bool drawEyeRegions = true;

    [Tooltip("Eye box width is based on the distance between the two eye anchors.")]
    [Range(0.4f, 1.2f)] public float eyeWidthFromEyeSpacing = 0.65f;

    [Tooltip("Eye box height is based on eye box width.")]
    [Range(0.25f, 1.0f)] public float eyeHeightFromEyeWidth = 0.55f;

    [Tooltip("Useful for nudging the boxes slightly up or down.")]
    [Range(-0.15f, 0.15f)] public float eyeVerticalOffsetFromFaceHeight = 0.0f;

    [Tooltip("Pushes the eye boxes outward from the face center based on eye spacing.")]
    [Range(-0.5f, 0.5f)] public float eyeHorizontalSpreadFromEyeSpacing = 0.18f;

    [Range(1f, 8f)] public float lineThickness = 3f;

    static Texture2D whiteTex;

    bool hasFace;
    bool hasEyeRegions;

    BlazeFaceSentis.FaceDet cachedDet;

    Rect leftEyeRect01;
    Rect rightEyeRect01;
    Vector2 leftEyeCenter01;
    Vector2 rightEyeCenter01;

    string statusText = "Waiting...";
    int lastTexW;
    int lastTexH;

    public bool HasFace => hasFace;
    public bool HasEyeRegions => hasEyeRegions;

    public Rect LeftEyeRect01 => leftEyeRect01;
    public Rect RightEyeRect01 => rightEyeRect01;

    public Vector2 LeftEyeCenter01 => leftEyeCenter01;
    public Vector2 RightEyeCenter01 => rightEyeCenter01;

    public bool labelKeypointIndices = true;

    GUIStyle debugTextStyle;
    void EnsureDebugStyle()
    {
        if (debugTextStyle != null) return;

        debugTextStyle = new GUIStyle(GUI.skin.label);
        debugTextStyle.fontSize = 28;
        debugTextStyle.normal.textColor = Color.white;
    }

    void Update()
    {
        if (cameraFeed == null)
        {
            hasFace = false;
            hasEyeRegions = false;
            statusText = "FaceDebugDraw: cameraFeed missing";
            return;
        }

        if (detector == null)
        {
            hasFace = false;
            hasEyeRegions = false;
            statusText = "FaceDebugDraw: detector missing";
            return;
        }

        var tex = cameraFeed.CorrectedRT;
        if (tex == null)
        {
            hasFace = false;
            hasEyeRegions = false;
            statusText = "FaceDebugDraw: CorrectedRT is null";
            return;
        }

        lastTexW = tex.width;
        lastTexH = tex.height;

        hasFace = detector.TryDetect(tex, out cachedDet);

        if (!hasFace)
        {
            hasEyeRegions = false;
            statusText = "No face detected";
            return;
        }

        hasEyeRegions = TryBuildEyeRects(
            cachedDet,
            out leftEyeRect01,
            out rightEyeRect01,
            out leftEyeCenter01,
            out rightEyeCenter01
        );

        statusText = hasEyeRegions
            ? $"Face detected. Score: {cachedDet.score:F3} | Eye regions ready"
            : $"Face detected. Score: {cachedDet.score:F3} | Eye regions unavailable";
    }

    void OnGUI()
    {
        EnsureWhiteTex();
        EnsureDebugStyle();

        if (showStatus)
            GUI.Label(new Rect(10, 10, 1200, 40), statusText, debugTextStyle);

        if (drawCenterTestMarker)
            DrawCenterMarker();

        if (!hasFace || lastTexW <= 0 || lastTexH <= 0)
            return;

        Rect viewRect = GetFittedScreenRect(lastTexW, lastTexH);

        if (drawFaceBox)
        {
            Rect faceScreen = Rect01ToScreen(cachedDet.faceRect01, viewRect);
            DrawRectOutline(faceScreen, lineThickness);
        }

        if (drawKeypoints && cachedDet.kp01 != null)
        {
            for (int i = 0; i < cachedDet.kp01.Length; i++)
            {
                var p = cachedDet.kp01[i];
                Vector2 sp = Point01ToScreen(p, viewRect);
                DrawPoint(sp, 8f);

                if (labelKeypointIndices)
                    GUI.Label(new Rect(sp.x + 6, sp.y - 14, 40, 20), i.ToString(), debugTextStyle);
            }
        }

        if (drawEyeRegions && hasEyeRegions)
        {
            Rect leftEyeScreen = Rect01ToScreen(leftEyeRect01, viewRect);
            Rect rightEyeScreen = Rect01ToScreen(rightEyeRect01, viewRect);

            DrawRectOutline(leftEyeScreen, lineThickness);
            DrawRectOutline(rightEyeScreen, lineThickness);

            DrawPoint(Point01ToScreen(leftEyeCenter01, viewRect), 10f);
            DrawPoint(Point01ToScreen(rightEyeCenter01, viewRect), 10f);
        }
    }

    bool TryBuildEyeRects(
        BlazeFaceSentis.FaceDet det,
        out Rect leftEye,
        out Rect rightEye,
        out Vector2 leftCenter,
        out Vector2 rightCenter)
    {
        leftEye = default;
        rightEye = default;
        leftCenter = default;
        rightCenter = default;

        if (det.kp01 == null || det.kp01.Length < 2)
            return false;

        // Common BlazeFace layout uses the first two sparse keypoints as eye anchors.
        // We sort by x so "left" and "right" mean screen-left and screen-right.
        Vector2 a = det.kp01[0];
        Vector2 b = det.kp01[1];

        if (a.x <= b.x)
        {
            leftCenter = a;
            rightCenter = b;
        }
        else
        {
            leftCenter = b;
            rightCenter = a;
        }

        float faceW = det.faceRect01.width;
        float faceH = det.faceRect01.height;
        float eyeSpacing = Mathf.Abs(rightCenter.x - leftCenter.x);


        if (faceW <= 0f || faceH <= 0f || eyeSpacing <= 0.0001f)
            return false;

        float xSpread = eyeSpacing * eyeHorizontalSpreadFromEyeSpacing;
        leftCenter.x -= xSpread;
        rightCenter.x += xSpread;

        float eyeW = Mathf.Clamp(
            eyeSpacing * eyeWidthFromEyeSpacing,
            faceW * 0.14f,
            faceW * 0.40f
        );

        float eyeH = Mathf.Clamp(
            eyeW * eyeHeightFromEyeWidth,
            faceH * 0.08f,
            faceH * 0.26f
        );

        float yOffset = faceH * eyeVerticalOffsetFromFaceHeight;
        leftCenter.y += yOffset;
        rightCenter.y += yOffset;

        leftEye = ClampRect01(new Rect(
            leftCenter.x - eyeW * 0.5f,
            leftCenter.y - eyeH * 0.5f,
            eyeW,
            eyeH
        ));

        rightEye = ClampRect01(new Rect(
            rightCenter.x - eyeW * 0.5f,
            rightCenter.y - eyeH * 0.5f,
            eyeW,
            eyeH
        ));

        return true;
    }

    Rect Rect01ToScreen(Rect r01, Rect viewRect)
    {
        return new Rect(
            viewRect.x + r01.x * viewRect.width,
            viewRect.y + r01.y * viewRect.height,
            r01.width * viewRect.width,
            r01.height * viewRect.height
        );
    }

    Vector2 Point01ToScreen(Vector2 p01, Rect viewRect)
    {
        return new Vector2(
            viewRect.x + p01.x * viewRect.width,
            viewRect.y + p01.y * viewRect.height
        );
    }

    Rect ClampRect01(Rect r)
    {
        float xMin = Mathf.Clamp01(r.xMin);
        float yMin = Mathf.Clamp01(r.yMin);
        float xMax = Mathf.Clamp01(r.xMax);
        float yMax = Mathf.Clamp01(r.yMax);
        return Rect.MinMaxRect(xMin, yMin, xMax, yMax);
    }

    void DrawCenterMarker()
    {
        float cx = Screen.width * 0.5f;
        float cy = Screen.height * 0.5f;
        GUI.DrawTexture(new Rect(cx - 2, cy - 20, 4, 40), whiteTex);
        GUI.DrawTexture(new Rect(cx - 20, cy - 2, 40, 4), whiteTex);
    }

    void DrawPoint(Vector2 p, float size)
    {
        GUI.DrawTexture(new Rect(p.x - size * 0.5f, p.y - size * 0.5f, size, size), whiteTex);
    }

    void EnsureWhiteTex()
    {
        if (whiteTex != null) return;

        whiteTex = new Texture2D(1, 1);
        whiteTex.SetPixel(0, 0, Color.white);
        whiteTex.Apply();
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