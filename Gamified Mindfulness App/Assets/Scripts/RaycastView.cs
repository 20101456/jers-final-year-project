using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;

public class RaycastView : MonoBehaviour
{
    [Header("References")]
    public GridMap1 map;
    public RouteWalker player;
    public RawImage output;

    [Header("Render Settings")]
    [Range(30f, 110f)] public float fovDegrees = 70f;
    public int internalWidth = 240;              // rays = this width
    public bool matchScreenAspect = true;        // auto height from aspect
    public int internalHeightOverride = 426;     // used if matchScreenAspect = false
    public float maxDistanceCells = 40f;         // in grid cells, not world units

    [Header("Colors")]
    public Color32 sky = new Color32(170, 200, 255, 255);
    public Color32 ground = new Color32(60, 50, 40, 255);
    public Color32 wall = new Color32(40, 120, 50, 255);

    [Header("Debug")]
    public bool draw = true;

    Texture2D _tex;
    Color32[] _buffer;

    [Header("Drag Look (Touch/Mouse)")]
    public bool useDragLook = true;
    public float maxDragPixels = 220f;     // drag this far = full look (-1..+1)
    public bool invertY = false;

    bool _dragging = false;
    Vector2 _dragStart;
    Vector2 _dragDelta; // pixels


    [Header("Look (Gaze)")]
    public bool useMouseAsGaze = true;
    [Range(0f, 45f)] public float maxYawDegrees = 20f;     // clamp left/right
    public float maxPitchPixels = 40f;                     // clamp up/down (screen pixels)
    [Range(0f, 0.5f)] public float deadZone = 0.12f;       // ignore tiny drift near center
    public float lookSmoothing = 10f;                      // higher = snappier

    float _yawDeg = 0f;
    float _pitchPx = 0f;

    [Header("Wall Texture")]
    public Texture2D wallTex;
    public bool useWallTexture = true;

    [Header("Fog")]
    public bool useFog = true;
    public Color32 fogColor = new Color32(40, 60, 50, 255);
    public float fogStart = 6f;   // in cells
    public float fogEnd = 22f;    // in cells

    [Header("Scale")]
    public float wallHeightScale = 1.6f;  // 1 = current, higher = taller walls

    [Header("Texture Tiling")]
    public float wallTexWorldWidth = 4f;  // how many grid cells wide before the texture repeats (bigger = more stretched)
    public float wallTexVScale = 1f;      // optional vertical tiling (1 = normal)

    static Vector2 Rotate(Vector2 v, float degrees)
{
    float r = degrees * Mathf.Deg2Rad;
    float cs = Mathf.Cos(r);
    float sn = Mathf.Sin(r);
    return new Vector2(v.x * cs - v.y * sn, v.x * sn + v.y * cs);
}

static float ApplyDeadZone(float v, float dz)
{
    float a = Mathf.Abs(v);
    if (a <= dz) return 0f;
    // remap (dz..1) -> (0..1) so it ramps nicely
    float t = (a - dz) / (1f - dz);
    return Mathf.Sign(v) * t;
}


    void Start()
    {
        if (map == null || player == null || output == null)
        {
            Debug.LogError("RaycastView: Assign map, player, and output in Inspector.");
            enabled = false;
            return;
        }

        map.Parse(); // ensure Width/Height are valid

        int h = matchScreenAspect
            ? Mathf.Max(64, Mathf.RoundToInt(internalWidth * ((float)Screen.height / Screen.width)))
            : Mathf.Max(64, internalHeightOverride);

        _tex = new Texture2D(internalWidth, h, TextureFormat.RGBA32, false);
        _tex.filterMode = FilterMode.Point;  // crisp retro look
        _tex.wrapMode = TextureWrapMode.Clamp;

        _buffer = new Color32[_tex.width * _tex.height];

        output.texture = _tex;
        output.color = Color.white; // important: don’t tint the texture
    }

    void Update()
    {
        if (!draw || _tex == null) return;

        int w = _tex.width;
        int h = _tex.height;

        // Fill sky/ground first
        int half = h / 2;
        for (int y = 0; y < h; y++)
        {
            Color32 c = (y < half) ? sky : ground;
            int row = y * w;
            for (int x = 0; x < w; x++)
                _buffer[row + x] = c;
        }

        // Player position in "cell space" (1 unit = 1 grid cell)
        Vector2 bl = map.BottomLeftWorld;
        float cell = map.cellSize;

        Vector2 posWorld = new Vector2(player.transform.position.x, player.transform.position.y);
        Vector2 pos = (posWorld - bl) / cell;

        Vector2 dir = player.Forward.normalized;
        if (dir.sqrMagnitude < 0.0001f) dir = Vector2.up;

        // --- Look input (touch drag / mouse drag) ---
        float gx = 0f, gy = 0f;

        // Prefer touch if present, otherwise mouse
        bool pressed = false;
        Vector2 pointerPos = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);

        if (Touchscreen.current != null && Touchscreen.current.primaryTouch.press.isPressed)
        {
            pressed = true;
            pointerPos = Touchscreen.current.primaryTouch.position.ReadValue();
        }
        else if (Mouse.current != null && Mouse.current.leftButton.isPressed)
        {
            pressed = true;
            pointerPos = Mouse.current.position.ReadValue();
        }

        if (useDragLook)
        {
            if (pressed)
            {
                if (!_dragging)
                {
                    _dragging = true;
                    _dragStart = pointerPos;
                    _dragDelta = Vector2.zero;
                }

                _dragDelta = pointerPos - _dragStart;

                // Convert pixels -> normalized (-1..+1)
                gx = Mathf.Clamp(_dragDelta.x / maxDragPixels, -1f, 1f);
                gy = Mathf.Clamp(_dragDelta.y / maxDragPixels, -1f, 1f);
            }
            else
            {
                _dragging = false;
                _dragDelta = Vector2.zero;
                gx = 0f;
                gy = 0f;
            }
        }
        else
        {
            // fallback: absolute pointer position on screen (-1..+1)
            Vector2 m = (Mouse.current != null) ? Mouse.current.position.ReadValue() : pointerPos;
            gx = (m.x / Screen.width) * 2f - 1f;
            gy = (m.y / Screen.height) * 2f - 1f;
        }
        if (invertY) gy = -gy;


        // dead zone so it doesn't constantly drift
        gx = ApplyDeadZone(gx, deadZone);
        gy = ApplyDeadZone(gy, deadZone);

        // target offsets
        float targetYaw = gx * maxYawDegrees;
        float targetPitch = gy * maxPitchPixels;

        // smooth
        _yawDeg = Mathf.Lerp(_yawDeg, targetYaw, 1f - Mathf.Exp(-lookSmoothing * Time.deltaTime));
        _pitchPx = Mathf.Lerp(_pitchPx, targetPitch, 1f - Mathf.Exp(-lookSmoothing * Time.deltaTime));

        // apply yaw to direction (clamped)
        Vector2 viewDir = Rotate(dir, _yawDeg).normalized;


        // Camera plane (perpendicular to direction) controls FOV
        float fovRad = fovDegrees * Mathf.Deg2Rad;
        float planeMag = Mathf.Tan(fovRad * 0.5f);
        Vector2 plane = new Vector2(-viewDir.y, viewDir.x) * planeMag;

        int verticalCenter = (h / 2) + Mathf.RoundToInt(_pitchPx);

        for (int x = 0; x < w; x++)
        {
            float cameraX = (2f * x / (w - 1)) - 1f;  // -1..+1
            Vector2 rayDir = (viewDir + plane * cameraX);

            CastAndDrawColumn(x, pos, rayDir, w, h, verticalCenter);
        }

        _tex.SetPixels32(_buffer);
        _tex.Apply(false);
    }

    void CastAndDrawColumn(int x, Vector2 pos, Vector2 rayDir, int w, int h, int verticalCenter)
    {
        // DDA setup
        int mapX = Mathf.FloorToInt(pos.x);
        int mapY = Mathf.FloorToInt(pos.y);

        float rayDirX = rayDir.x;
        float rayDirY = rayDir.y;

        float deltaDistX = (Mathf.Abs(rayDirX) < 0.00001f) ? float.PositiveInfinity : Mathf.Abs(1f / rayDirX);
        float deltaDistY = (Mathf.Abs(rayDirY) < 0.00001f) ? float.PositiveInfinity : Mathf.Abs(1f / rayDirY);

        int stepX, stepY;
        float sideDistX, sideDistY;

        if (rayDirX < 0)
        {
            stepX = -1;
            sideDistX = (pos.x - mapX) * deltaDistX;
        }
        else
        {
            stepX = 1;
            sideDistX = (mapX + 1f - pos.x) * deltaDistX;
        }

        if (rayDirY < 0)
        {
            stepY = -1;
            sideDistY = (pos.y - mapY) * deltaDistY;
        }
        else
        {
            stepY = 1;
            sideDistY = (mapY + 1f - pos.y) * deltaDistY;
        }

        bool hit = false;
        int side = 0; // 0 = hit x-side, 1 = hit y-side

        // Step through the grid
        int maxSteps = Mathf.CeilToInt(maxDistanceCells * 2f);
        for (int i = 0; i < maxSteps; i++)
        {
            if (sideDistX < sideDistY)
            {
                sideDistX += deltaDistX;
                mapX += stepX;
                side = 0;
            }
            else
            {
                sideDistY += deltaDistY;
                mapY += stepY;
                side = 1;
            }

            if (map.IsWall(mapX, mapY))
            {
                hit = true;
                break;
            }
        }

        if (!hit) return;

        // Perpendicular distance (in cell units)
        float perpDist;
        if (side == 0)
            perpDist = (mapX - pos.x + (1 - stepX) * 0.5f) / (rayDirX == 0 ? 0.00001f : rayDirX);
        else
            perpDist = (mapY - pos.y + (1 - stepY) * 0.5f) / (rayDirY == 0 ? 0.00001f : rayDirY);

        perpDist = Mathf.Abs(perpDist);
        if (perpDist < 0.0001f) perpDist = 0.0001f;

        // Convert distance to wall slice height
        int lineHeight = Mathf.Clamp((int)((h * wallHeightScale) / perpDist), 1, h * 4);

        int drawStart = -lineHeight / 2 + verticalCenter;
        int drawEnd = lineHeight / 2 + verticalCenter;


        drawStart = Mathf.Clamp(drawStart, 0, h - 1);
        drawEnd = Mathf.Clamp(drawEnd, 0, h - 1);

        // Simple shading: darker on Y-sides + farther = darker
        float shade = 1f / (1f + perpDist * 0.08f);
        if (side == 1) shade *= 0.75f;

        Color32 wc = wall;
        wc.r = (byte)Mathf.Clamp(wc.r * shade, 0, 255);
        wc.g = (byte)Mathf.Clamp(wc.g * shade, 0, 255);
        wc.b = (byte)Mathf.Clamp(wc.b * shade, 0, 255);

        // --- Texture coordinate for this wall hit (0..1 across the wall face) ---
    float hitCoord;
    if (side == 0) hitCoord = pos.y + perpDist * rayDir.y;
    else           
    hitCoord = pos.x + perpDist * rayDir.x;

//wallX -= Mathf.Floor(wallX); // keep fractional part (0..1)
    // Make the texture repeat every N cells instead of every 1 cell
float u = hitCoord / Mathf.Max(0.0001f, wallTexWorldWidth);
u -= Mathf.Floor(u); // keep fractional part (0..1)


    int texW = (wallTex != null) ? wallTex.width : 1;
    int texH = (wallTex != null) ? wallTex.height : 1;

// Pick x on texture
//int texX = Mathf.Clamp((int)(wallX * texW), 0, texW - 1);

    int texX = Mathf.Clamp((int)(u * texW), 0, texW - 1);

// Optional: flip for consistency based on ray direction (helps reduce “mirrored” feel)
if (side == 0 && rayDir.x > 0) texX = texW - 1 - texX;
if (side == 1 && rayDir.y < 0) texX = texW - 1 - texX;

// Fog factor (0 = no fog, 1 = full fog)
float fogT = 0f;
if (useFog)
    fogT = Mathf.InverseLerp(fogStart, fogEnd, perpDist);

for (int y = drawStart; y <= drawEnd; y++)
{
    Color32 col = wall;

    if (useWallTexture && wallTex != null)
    {
        // Map y along the wall slice to texY
        float v = (lineHeight <= 0) ? 0f : (float)(y - drawStart) / lineHeight;
        int texY = Mathf.Clamp((int)(v * texH), 0, texH - 1);

        col = wallTex.GetPixel(texX, texY);
    }

    // Apply existing shading
    col.r = (byte)Mathf.Clamp(col.r * shade, 0, 255);
    col.g = (byte)Mathf.Clamp(col.g * shade, 0, 255);
    col.b = (byte)Mathf.Clamp(col.b * shade, 0, 255);

    // Apply fog (blend toward fogColor)
    if (useFog)
    {
        col.r = (byte)Mathf.Lerp(col.r, fogColor.r, fogT);
        col.g = (byte)Mathf.Lerp(col.g, fogColor.g, fogT);
        col.b = (byte)Mathf.Lerp(col.b, fogColor.b, fogT);
    }

    _buffer[y * w + x] = col;
        }

    }
}
