using System.Collections.Generic;
using UnityEngine;

namespace Akiya82.BlendshapeTransfer
{
    /// <summary>Closest point on a triangle and a uniform point grid for radius queries.</summary>
    public static class Geometry
    {
        /// <summary>Closest point on triangle abc to p, with its barycentric weights (Ericson, Real-Time Collision Detection, 5.1.5).</summary>
        public static Vector3 ClosestPointOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c, out Vector3 bary)
        {
            Vector3 ab = b - a, ac = c - a, ap = p - a;
            float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f) { bary = new Vector3(1f, 0f, 0f); return a; }
            var bp = p - b;
            float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3) { bary = new Vector3(0f, 1f, 0f); return b; }
            var vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f)
            {
                var v = d1 / (d1 - d3);
                bary = new Vector3(1f - v, v, 0f);
                return a + v * ab;
            }
            var cp = p - c;
            float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0f && d5 <= d6) { bary = new Vector3(0f, 0f, 1f); return c; }
            var vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f)
            {
                var w = d2 / (d2 - d6);
                bary = new Vector3(1f - w, 0f, w);
                return a + w * ac;
            }
            var va = d3 * d6 - d5 * d4;
            if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f)
            {
                var w = (d4 - d3) / (d4 - d3 + (d5 - d6));
                bary = new Vector3(0f, 1f - w, w);
                return b + w * (c - b);
            }
            var denom = 1f / (va + vb + vc);
            float vv = vb * denom, ww = vc * denom;
            bary = new Vector3(1f - vv - ww, vv, ww);
            return a + ab * vv + ac * ww;
        }

        /// <summary>Uniform grid over points for radius queries.</summary>
        public sealed class SpatialGrid
        {
            private readonly Dictionary<(int, int, int), List<int>> cells = new Dictionary<(int, int, int), List<int>>();
            private readonly IReadOnlyList<Vector3> points;
            private readonly float cell;

            public SpatialGrid(IReadOnlyList<Vector3> points, float cellSize)
            {
                this.points = points;
                cell = cellSize;
                for (var i = 0; i < points.Count; i++)
                {
                    var key = CellOf(points[i]);
                    if (!cells.TryGetValue(key, out var list)) cells[key] = list = new List<int>();
                    list.Add(i);
                }
            }

            /// <summary>Indices of the points within radius of p, written to output (cleared first).</summary>
            public void Query(Vector3 p, float radius, List<int> output)
            {
                output.Clear();
                var (x, y, z) = CellOf(p);
                var reach = Mathf.CeilToInt(radius / cell);
                var radiusSqr = radius * radius;
                var side = 2L * reach + 1;
                if (side * side * side > cells.Count)
                {
                    foreach (var list in cells.Values)
                        foreach (var i in list)
                            if ((points[i] - p).sqrMagnitude <= radiusSqr) output.Add(i);
                    return;
                }
                for (var dx = -reach; dx <= reach; dx++)
                for (var dy = -reach; dy <= reach; dy++)
                for (var dz = -reach; dz <= reach; dz++)
                {
                    if (!cells.TryGetValue((x + dx, y + dy, z + dz), out var list)) continue;
                    foreach (var i in list)
                        if ((points[i] - p).sqrMagnitude <= radiusSqr) output.Add(i);
                }
            }

            private (int, int, int) CellOf(Vector3 p) =>
                (Mathf.FloorToInt(p.x / cell), Mathf.FloorToInt(p.y / cell), Mathf.FloorToInt(p.z / cell));
        }
    }
}
