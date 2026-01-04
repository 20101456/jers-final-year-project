using UnityEngine;
using System;

public class GridMap1 : MonoBehaviour
{
    [Header("Map")]
    [TextArea(10, 40)]
    public string mapText =
@"####################
######......########
######......########
#####......#########
####......##########
####......##########
####......##########
#####......#########
######......########
#######......#######
#######......#######
########......######
#########......#####
##########......####
##########......####
#########.......####
########.......#####
#######.......######
#######......#######
#######......#######
#######......#######
######........######
#####..........#####
####............####
###..............###
###..............###
####............####
#####..........#####
######........######
####################";


    [Header("Grid Settings")]
    public float cellSize = 1f;

    [Header("Debug")]
    public bool drawGizmos = true;
    public Color wallColor = new Color(0.15f, 0.5f, 0.15f, 0.9f);
    public Color emptyColor = new Color(0f, 0f, 0f, 0.05f);
    public bool drawEmptyCells = false;


    private string[] _rows;
    public int Width {get; private set;}
    public int Height { get; private set;}

    private void Awake() => Parse();
    private void OnValidate() => Parse();

    public bool centerOnTransform = true;

    public void Parse() {
        if (string.IsNullOrWhiteSpace(mapText))
        return;

        _rows = mapText.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Height = _rows.Length;
        Width = _rows[0].Length;

        //Basic validation - all rows same width
        for (int i=0; i<_rows.Length; i++) {
            if (_rows[i].Length != Width) {
                Debug.LogWarning($"GridMap1: Row {i} length {_rows[i].Length} != Width {Width}. Fix the mapText formatting.");
                break;
            }
        }
    }

    /// <summary>
    /// Grid coords: (0,0) is bottom-left.
    /// mapText is written top-to-bottom, so we invert Y when indexing.
    /// </summary>
    public bool IsWall(int x, int y)
    {
        if (_rows == null || _rows.Length == 0) Parse();

        if (x < 0 || y < 0 || x >= Width || y >= Height)
            return true; // treat out-of-bounds as wall

        int invertedRow = (Height - 1) - y;
        char c = _rows[invertedRow][x];
        return c == '#';
    }

    public Vector2 CellCenter(int x, int y) 
    {
    Vector2 bl = BottomLeftWorld;
    return new Vector2(bl.x + (x + 0.5f) * cellSize, bl.y + (y + 0.5f) * cellSize);
    }


    private void OnDrawGizmos()
    {
        if (!drawGizmos) return;
        if (string.IsNullOrWhiteSpace(mapText)) return;

        Parse();
        if (_rows == null) return;

        Vector2 bottomLeft = BottomLeftWorld;
        Vector3 origin = new Vector3(bottomLeft.x, bottomLeft.y, transform.position.z);

        // Draw map bounds + origin marker (debug)
        Gizmos.color = Color.white;
        Gizmos.DrawWireCube(transform.position,
        new Vector3(Width * cellSize, Height * cellSize, 0.1f));
        Gizmos.DrawLine(transform.position + Vector3.left * 20f, transform.position + Vector3.right * 20f);
        Gizmos.DrawLine(transform.position + Vector3.down * 20f, transform.position + Vector3.up * 20f);


        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                bool wall = IsWall(x, y);
                if (!wall && !drawEmptyCells) continue;
                Gizmos.color = wall ? wallColor : emptyColor;

                Vector3 worldPos = origin + new Vector3((x + 0.5f) * cellSize, (y + 0.5f) * cellSize, 0f);
                Vector3 size = new Vector3(cellSize, cellSize, 0.05f);

                Gizmos.DrawCube(worldPos, size);
            }
        }
    }

    public Vector2 BottomLeftWorld
    {
        get
        {
            Vector3 p = transform.position;
            if (!centerOnTransform) return p;

            float w = Width * cellSize;
            float h = Height * cellSize;
            return new Vector2(p.x - w * 0.5f, p.y - h * 0.5f);
        }
    }
}

