using UnityEngine;

/// <summary>
/// Runs face detection on the corrected camera feed, derives left/right eye regions,
/// and optionally draws debug overlays for the face, keypoints, and eye boxes.
///
/// Despite the name, this script is part of the tracking pipeline because
/// EyeRegionTracker reads the calculated eye rectangles from it.
/// Its function changed as it devolped.
/// </summary>
public class FaceDebugDraw : MonoBehaviour
{
    [Header("References")]
    public CameraFrameCorrector cameraFeed;
    public BlazeFaceSentis detector;

    [Header("Face Debug")]
    public bool drawFaceBox = false;
    public bool drawKeypoints = false;
    public bool labelKeypointIndices = true;
    public bool showStatus = false;
    public bool drawCenterTestMarker = false;

    [Header("Eye Debug")]
    public bool drawEyeRegions = false;

    [Tooltip("Eye box width is based on the distance between the two eye anchors.")]
    [Range(0.4f, 1.2f)] public float eyeWidthFromEyeSpacing = 0.65f;

    [Tooltip("Eye box height is based on eye box width.")]
    [Range(0.25f, 1.0f)] public float eyeHeightFromEyeWidth = 0.55f;

    [Tooltip("Useful for nudging the boxes slightly up or down.")]
    [Range(-0.15f, 0.15f)] public float eyeVerticalOffsetFromFaceHeight = 0.0f;

    [Tooltip("Pushes the eye boxes outward from the face center based on eye spacing.")]
    [Range(-0.5f, 0.5f)] public float eyeHorizontalSpreadFromEyeSpacing = 0.18f;

    [Range(1f, 8f)] public float lineThickness = 3f;

    [Header("Eye Rect Stability")]
    [Range(1f, 30f)] public float eyeRectSmoothSpeed = 14f;

    static Texture2D whiteTex;

    bool hasFace;
    bool hasEyeRegions;

    BlazeFaceSentis.FaceDet cachedDetection;

    Rect leftEyeRect01;
    Rect rightEyeRect01;

    Vector2 leftEyeCenter01;
    Vector2 rightEyeCenter01;

    string statusText = "Waiting...";

    int lastTextureWidth;
    int lastTextureHeight;

    GUIStyle debugTextStyle;

    public bool HasFace => hasFace;
    public bool HasEyeRegions => hasEyeRegions;

    public Rect LeftEyeRect01 => leftEyeRect01;
    public Rect RightEyeRect01 => rightEyeRect01;

    public Vector2 LeftEyeCenter01 => leftEyeCenter01;
    public Vector2 RightEyeCenter01 => rightEyeCenter01;

    void Awake()
    {
        if (cameraFeed == null)
            cameraFeed = FindFirstObjectByType<CameraFrameCorrector>();

        if (detector == null)
            detector = FindFirstObjectByType<BlazeFaceSentis>();
    }

    void Update()
    {
        if (!TryGetCameraTexture(out RenderTexture cameraTexture))
            return;

        lastTextureWidth = cameraTexture.width;
        lastTextureHeight = cameraTexture.height;

        hasFace = detector.TryDetect(cameraTexture, out cachedDetection);

        if (!hasFace)
        {
            hasEyeRegions = false;
            statusText = "No face detected";
            return;
        }

        hasEyeRegions = TryBuildEyeRects(
            cachedDetection,
            out Rect newLeftEyeRect01,
            out Rect newRightEyeRect01,
            out Vector2 newLeftEyeCenter01,
            out Vector2 newRightEyeCenter01
        );

        if (hasEyeRegions)
        {
            ApplyEyeRectSmoothing(newLeftEyeRect01, newRightEyeRect01);
        }

        statusText = hasEyeRegions
            ? $"Face detected. Score: {cachedDetection.score:F3} | Eye regions ready"
            : $"Face detected. Score: {cachedDetection.score:F3} | Eye regions unavailable";
    }

    bool TryGetCameraTexture(out RenderTexture cameraTexture)
    {
        cameraTexture = null;

        if (cameraFeed == null)
        {
            SetTrackingUnavailable("FaceDebugDraw: cameraFeed missing");
            return false;
        }

        if (detector == null)
        {
            SetTrackingUnavailable("FaceDebugDraw: detector missing");
            return false;
        }

        cameraTexture = cameraFeed.CorrectedRT;

        if (cameraTexture == null)
        {
            SetTrackingUnavailable("FaceDebugDraw: CorrectedRT is null");
            return false;
        }

        return true;
    }

    void SetTrackingUnavailable(string reason)
    {
        hasFace = false;
        hasEyeRegions = false;
        statusText = reason;
    }

    void ApplyEyeRectSmoothing(Rect newLeftEyeRect01, Rect newRightEyeRect01)
    {
        float smoothT = 1f - Mathf.Exp(-eyeRectSmoothSpeed * Time.unscaledDeltaTime);

        bool hasExistingRects = leftEyeRect01.width > 0f && rightEyeRect01.width > 0f;

        if (!hasExistingRects)
        {
            leftEyeRect01 = newLeftEyeRect01;
            rightEyeRect01 = newRightEyeRect01;
        }
        else
        {
            leftEyeRect01 = SmoothRect(leftEyeRect01, newLeftEyeRect01, smoothT);
            rightEyeRect01 = SmoothRect(rightEyeRect01, newRightEyeRect01, smoothT);
        }

        leftEyeCenter01 = leftEyeRect01.center;
        rightEyeCenter01 = rightEyeRect01.center;
    }

    Rect SmoothRect(Rect current, Rect target, float t)
    {
        Vector2 center = Vector2.Lerp(current.center, target.center, t);
        Vector2 size = Vector2.Lerp(current.size, target.size, t);

        return new Rect(
            center.x - size.x * 0.5f,
            center.y - size.y * 0.5f,
            size.x,
            size.y
        );
    }

    bool TryBuildEyeRects(
        BlazeFaceSentis.FaceDet detection,
        out Rect leftEye,
        out Rect rightEye,
        out Vector2 leftCenter,
        out Vector2 rightCenter
    )
    {
        leftEye = default;
        rightEye = default;
        leftCenter = default;
        rightCenter = default;

        if (detection.kp01 == null || detection.kp01.Length < 2)
            return false;

        // Expand the face rect slightly so the derived eye layout is less cramped.
        Rect face = ExpandRect01(detection.faceRect01, 1.10f, 1.12f);

        float faceWidth = face.width;
        float faceHeight = face.height;

        if (faceWidth <= 0f || faceHeight <= 0f)
            return false;

        // BlazeFace commonly uses kp[0] and kp[1] as eye anchors.
        Vector2 anchorA = detection.kp01[0];
        Vector2 anchorB = detection.kp01[1];

        Vector2 keypointLeft = anchorA.x <= anchorB.x ? anchorA : anchorB;
        Vector2 keypointRight = anchorA.x <= anchorB.x ? anchorB : anchorA;

        float eyeSpacing = Mathf.Abs(keypointRight.x - keypointLeft.x);

        if (eyeSpacing <= 0.0001f)
            return false;

        // Expected eye positions from the face rectangle.
        // These help reduce jitter if the raw keypoints move inward or collapse slightly.
        Vector2 expectedLeft = new Vector2(
            face.xMin + faceWidth * 0.32f,
            face.yMin + faceHeight * 0.40f
        );

        Vector2 expectedRight = new Vector2(
            face.xMin + faceWidth * 0.68f,
            face.yMin + faceHeight * 0.40f
        );

        const float keypointWeight = 0.70f;
        const float expectedPositionWeight = 0.30f;

        leftCenter = keypointLeft * keypointWeight + expectedLeft * expectedPositionWeight;
        rightCenter = keypointRight * keypointWeight + expectedRight * expectedPositionWeight;

        float horizontalSpread = eyeSpacing * eyeHorizontalSpreadFromEyeSpacing;

        leftCenter.x -= horizontalSpread;
        rightCenter.x += horizontalSpread;

        float verticalOffset = faceHeight * eyeVerticalOffsetFromFaceHeight;

        leftCenter.y += verticalOffset;
        rightCenter.y += verticalOffset;

        float eyeWidthFromSpacing = eyeSpacing * eyeWidthFromEyeSpacing;
        float eyeWidthFromFace = faceWidth * 0.20f;

        float eyeWidth = Mathf.Clamp(
            Mathf.Max(eyeWidthFromSpacing, eyeWidthFromFace),
            faceWidth * 0.16f,
            faceWidth * 0.42f
        );

        float eyeHeight = Mathf.Clamp(
            eyeWidth * eyeHeightFromEyeWidth,
            faceHeight * 0.08f,
            faceHeight * 0.22f
        );

        leftEye = BuildEyeRect(leftCenter, eyeWidth, eyeHeight);
        rightEye = BuildEyeRect(rightCenter, eyeWidth, eyeHeight);

        return true;
    }

    Rect BuildEyeRect(Vector2 center, float width, float height)
    {
        return ClampRect01(new Rect(
            center.x - width * 0.5f,
            center.y - height * 0.5f,
            width,
            height
        ));
    }

    void OnGUI()
    {
        EnsureDebugStyle();

        if (showStatus)
            GUI.Label(new Rect(10f, 10f, 1200f, 40f), statusText, debugTextStyle);

        if (!ShouldDrawOverlay())
            return;

        EnsureWhiteTex();

        if (drawCenterTestMarker)
            DrawCenterMarker();

        if (!hasFace || lastTextureWidth <= 0 || lastTextureHeight <= 0)
            return;

        Rect viewRect = GetFittedScreenRect(lastTextureWidth, lastTextureHeight);

        if (drawFaceBox)
        {
            Rect faceScreenRect = Rect01ToScreen(cachedDetection.faceRect01, viewRect);
            DrawRectOutline(faceScreenRect, lineThickness);
        }

        if (drawKeypoints)
            DrawKeypoints(viewRect);

        if (drawEyeRegions && hasEyeRegions)
            DrawEyeRegions(viewRect);
    }

    bool ShouldDrawOverlay()
    {
        return
            drawCenterTestMarker ||
            drawFaceBox ||
            drawKeypoints ||
            drawEyeRegions;
    }

    void DrawKeypoints(Rect viewRect)
    {
        if (cachedDetection.kp01 == null)
            return;

        for (int i = 0; i < cachedDetection.kp01.Length; i++)
        {
            Vector2 screenPoint = Point01ToScreen(cachedDetection.kp01[i], viewRect);

            DrawPoint(screenPoint, 8f);

            if (labelKeypointIndices)
            {
                GUI.Label(
                    new Rect(screenPoint.x + 6f, screenPoint.y - 14f, 40f, 20f),
                    i.ToString(),
                    debugTextStyle
                );
            }
        }
    }

    void DrawEyeRegions(Rect viewRect)
    {
        Rect leftEyeScreenRect = Rect01ToScreen(leftEyeRect01, viewRect);
        Rect rightEyeScreenRect = Rect01ToScreen(rightEyeRect01, viewRect);

        DrawRectOutline(leftEyeScreenRect, lineThickness);
        DrawRectOutline(rightEyeScreenRect, lineThickness);

        DrawPoint(Point01ToScreen(leftEyeCenter01, viewRect), 10f);
        DrawPoint(Point01ToScreen(rightEyeCenter01, viewRect), 10f);
    }

    Rect Rect01ToScreen(Rect rect01, Rect viewRect)
    {
        return new Rect(
            viewRect.x + rect01.x * viewRect.width,
            viewRect.y + rect01.y * viewRect.height,
            rect01.width * viewRect.width,
            rect01.height * viewRect.height
        );
    }

    Vector2 Point01ToScreen(Vector2 point01, Rect viewRect)
    {
        return new Vector2(
            viewRect.x + point01.x * viewRect.width,
            viewRect.y + point01.y * viewRect.height
        );
    }

    Rect GetFittedScreenRect(int textureWidth, int textureHeight)
    {
        float textureAspect = (float)textureWidth / textureHeight;
        float screenAspect = (float)Screen.width / Screen.height;

        if (screenAspect > textureAspect)
        {
            float height = Screen.height;
            float width = height * textureAspect;
            float x = (Screen.width - width) * 0.5f;

            return new Rect(x, 0f, width, height);
        }
        else
        {
            float width = Screen.width;
            float height = width / textureAspect;
            float y = (Screen.height - height) * 0.5f;

            return new Rect(0f, y, width, height);
        }
    }

    Rect ClampRect01(Rect rect)
    {
        float xMin = Mathf.Clamp01(rect.xMin);
        float yMin = Mathf.Clamp01(rect.yMin);
        float xMax = Mathf.Clamp01(rect.xMax);
        float yMax = Mathf.Clamp01(rect.yMax);

        return Rect.MinMaxRect(xMin, yMin, xMax, yMax);
    }

    Rect ExpandRect01(Rect rect, float widthScale, float heightScale)
    {
        Vector2 center = rect.center;
        float width = rect.width * widthScale;
        float height = rect.height * heightScale;

        return ClampRect01(new Rect(
            center.x - width * 0.5f,
            center.y - height * 0.5f,
            width,
            height
        ));
    }

    void DrawCenterMarker()
    {
        float centerX = Screen.width * 0.5f;
        float centerY = Screen.height * 0.5f;

        GUI.DrawTexture(new Rect(centerX - 2f, centerY - 20f, 4f, 40f), whiteTex);
        GUI.DrawTexture(new Rect(centerX - 20f, centerY - 2f, 40f, 4f), whiteTex);
    }

    void DrawPoint(Vector2 point, float size)
    {
        GUI.DrawTexture(
            new Rect(point.x - size * 0.5f, point.y - size * 0.5f, size, size),
            whiteTex
        );
    }

    void DrawRectOutline(Rect rect, float thickness)
    {
        GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, thickness), whiteTex);
        GUI.DrawTexture(new Rect(rect.x, rect.yMax - thickness, rect.width, thickness), whiteTex);
        GUI.DrawTexture(new Rect(rect.x, rect.y, thickness, rect.height), whiteTex);
        GUI.DrawTexture(new Rect(rect.xMax - thickness, rect.y, thickness, rect.height), whiteTex);
    }

    void EnsureDebugStyle()
    {
        if (debugTextStyle != null)
            return;

        debugTextStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 28
        };

        debugTextStyle.normal.textColor = Color.white;
    }

    void EnsureWhiteTex()
    {
        if (whiteTex != null)
            return;

        whiteTex = new Texture2D(1, 1)
        {
            hideFlags = HideFlags.HideAndDontSave
        };

        whiteTex.SetPixel(0, 0, Color.white);
        whiteTex.Apply();
    }
}