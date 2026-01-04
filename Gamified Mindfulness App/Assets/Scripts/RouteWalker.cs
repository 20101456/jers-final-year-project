using System.Collections.Generic;
using UnityEngine;

public class RouteWalker : MonoBehaviour
{
    [Header("References")]
    public GridMap1 map;

    [Header("Route")]
    public bool autoPickStartEnd = true;
    public Vector2Int startCell = new Vector2Int(8, 1);
    public Vector2Int endCell = new Vector2Int(8, 25);

    [Header("Movement")]
    public float speed = 2.0f;
    public float arriveDistance = 0.05f;
    public bool playOnStart = true;

    [Header("Debug")]
    public bool drawPathGizmos = true;

    public Vector2 Forward { get; private set; } = Vector2.up;
    public Vector2 Position2D { get; private set; }

    private readonly List<Vector2> _waypoints = new();
    private int _targetIndex = 0;
    private bool _isPlaying = false;

    private void Start()
    {
        if (map == null)
        {
            Debug.LogError("RouteWalker: No GridMap1 assigned.");
            enabled = false;
            return;
        }

        if (autoPickStartEnd)
        {
            startCell = FindEndpoint(fromBottom: true);
            endCell   = FindEndpoint(fromBottom: false);
        }

        BuildPathBFS(startCell, endCell);

        if (_waypoints.Count == 0)
        {
            Debug.LogError("RouteWalker: No path found. Check that start/end are on '.' cells and corridor is connected.");
            enabled = false;
            return;
        }

        // Place player at start
        Position2D = _waypoints[0];
        transform.position = new Vector3(Position2D.x, Position2D.y, transform.position.z);

        _targetIndex = 1;
        _isPlaying = playOnStart;
    }

    private void Update()
    {
        if (!_isPlaying) return;
        if (_targetIndex >= _waypoints.Count) return;

        Vector2 target = _waypoints[_targetIndex];
        Vector2 toTarget = target - Position2D;

        float dist = toTarget.magnitude;
        if (dist <= arriveDistance)
        {
            _targetIndex++;
            return;
        }

        Vector2 dir = toTarget / dist;

        // Update forward (used later by raycaster camera)
        if (dir.sqrMagnitude > 0.0001f)
            Forward = dir;

        Position2D += dir * speed * Time.deltaTime;
        transform.position = new Vector3(Position2D.x, Position2D.y, transform.position.z);
    }

    // -------- Pathfinding --------

    private void BuildPathBFS(Vector2Int start, Vector2Int goal)
    {
        _waypoints.Clear();

        if (map.IsWall(start.x, start.y) || map.IsWall(goal.x, goal.y))
        {
            Debug.LogError($"RouteWalker: Start or goal is inside a wall. Start={start}, Goal={goal}");
            return;
        }

        int w = map.Width;
        int h = map.Height;

        int Idx(int x, int y) => x + y * w;

        var queue = new Queue<Vector2Int>();
        var cameFrom = new Vector2Int[w * h];
        var visited = new bool[w * h];

        for (int i = 0; i < cameFrom.Length; i++)
            cameFrom[i] = new Vector2Int(-1, -1);

        queue.Enqueue(start);
        visited[Idx(start.x, start.y)] = true;

        Vector2Int[] dirs = {
            new Vector2Int(1,0),
            new Vector2Int(-1,0),
            new Vector2Int(0,1),
            new Vector2Int(0,-1)
        };

        bool found = false;

        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (cur == goal) { found = true; break; }

            foreach (var d in dirs)
            {
                var nxt = cur + d;
                if (nxt.x < 0 || nxt.y < 0 || nxt.x >= w || nxt.y >= h) continue;
                if (map.IsWall(nxt.x, nxt.y)) continue;

                int ni = Idx(nxt.x, nxt.y);
                if (visited[ni]) continue;

                visited[ni] = true;
                cameFrom[ni] = cur;
                queue.Enqueue(nxt);
            }
        }

        if (!found)
            return;

        // Reconstruct (goal -> start)
        var pathCells = new List<Vector2Int>();
        var p = goal;
        pathCells.Add(p);

        while (p != start)
        {
            p = cameFrom[Idx(p.x, p.y)];
            if (p.x == -1) break;
            pathCells.Add(p);
        }

        pathCells.Reverse();

        // Convert to world waypoints (cell centers)
        foreach (var c in pathCells)
            _waypoints.Add(map.CellCenter(c.x, c.y));
    }

    private Vector2Int FindEndpoint(bool fromBottom)
    {
        int w = map.Width;
        int h = map.Height;

        int yStart = fromBottom ? 0 : (h - 1);
        int yEnd   = fromBottom ? h : -1;
        int yStep  = fromBottom ? 1 : -1;

        for (int y = yStart; y != yEnd; y += yStep)
        {
            int minX = int.MaxValue;
            int maxX = int.MinValue;
            bool any = false;

            for (int x = 0; x < w; x++)
            {
                if (!map.IsWall(x, y))
                {
                    any = true;
                    minX = Mathf.Min(minX, x);
                    maxX = Mathf.Max(maxX, x);
                }
            }

            if (any)
            {
                int mid = (minX + maxX) / 2;
                return new Vector2Int(mid, y);
            }
        }

        // Fallback (shouldn't happen if map has any '.' at all)
        return new Vector2Int(w / 2, h / 2);
    }

    private void OnDrawGizmos()
    {
        if (!drawPathGizmos) return;
        if (_waypoints == null || _waypoints.Count < 2) return;

        Gizmos.color = Color.cyan;
        for (int i = 0; i < _waypoints.Count - 1; i++)
        {
            Vector3 a = new Vector3(_waypoints[i].x, _waypoints[i].y, 0f);
            Vector3 b = new Vector3(_waypoints[i + 1].x, _waypoints[i + 1].y, 0f);
            Gizmos.DrawLine(a, b);
        }

        // Forward indicator
        Gizmos.color = Color.yellow;
        Vector3 p = transform.position;
        Gizmos.DrawLine(p, p + new Vector3(Forward.x, Forward.y, 0f) * 1.0f);
    }
}
