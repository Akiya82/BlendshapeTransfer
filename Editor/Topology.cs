using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Akiya82.BlendshapeTransfer
{
    /// <summary>
    /// Connectivity of a triangle mesh with coincident vertices welded: vertices within WeldDistance form a group (UV and normal
    /// seams, double-sided duplicates). Groups give the adjacency for smoothing; islands join triangles only across shared edges,
    /// so parts that touch at a single point stay apart. A boundary edge belongs to one distinct face (duplicate faces count once).
    /// </summary>
    internal sealed class Topology
    {
        public const float WeldDistance = 1e-5f;
        public readonly int VertexCount;
        public readonly int[] Triangles;
        public readonly int[] Group;
        public readonly int GroupCount;
        public readonly int[][] Members;
        public readonly int[][] Neighbours;
        public readonly int[][] VertexTriangles;
        /// <summary>Island of each vertex; -1 for a vertex in no triangle.</summary>
        public readonly int[] Island;
        public readonly int[][] IslandVertices;
        private readonly HashSet<long> boundary = new HashSet<long>();

        public Topology(Vector3[] positions, int[] triangles)
        {
            VertexCount = positions.Length;
            Triangles = triangles;

            var weld = Enumerable.Range(0, VertexCount).ToArray();
            var grid = new Geometry.SpatialGrid(positions, WeldDistance * 10f);
            var near = new List<int>();
            for (var i = 0; i < VertexCount; i++)
            {
                grid.Query(positions[i], WeldDistance, near);
                foreach (var j in near)
                    if (j != i) Union(weld, i, j);
            }
            Group = new int[VertexCount];
            var ids = new Dictionary<int, int>();
            for (var i = 0; i < VertexCount; i++)
            {
                var root = Find(weld, i);
                if (!ids.TryGetValue(root, out var id)) ids[root] = id = ids.Count;
                Group[i] = id;
            }
            GroupCount = ids.Count;
            var members = Enumerable.Range(0, GroupCount).Select(_ => new List<int>()).ToArray();
            for (var i = 0; i < VertexCount; i++) members[Group[i]].Add(i);
            Members = members.Select(m => m.ToArray()).ToArray();

            var triangleCount = triangles.Length / 3;
            var vertexTriangles = Enumerable.Range(0, VertexCount).Select(_ => new List<int>()).ToArray();
            var neighbours = Enumerable.Range(0, GroupCount).Select(_ => new HashSet<int>()).ToArray();
            var edgeFaces = new Dictionary<long, HashSet<(int, int, int)>>();
            var edgeFirst = new Dictionary<long, int>();
            var faceIsland = Enumerable.Range(0, triangleCount).ToArray();
            for (var t = 0; t < triangleCount; t++)
            {
                int a = triangles[3 * t], b = triangles[3 * t + 1], c = triangles[3 * t + 2];
                vertexTriangles[a].Add(t);
                vertexTriangles[b].Add(t);
                vertexTriangles[c].Add(t);
                int ga = Group[a], gb = Group[b], gc = Group[c];
                var face = Sorted(ga, gb, gc);
                var sliver = ga == gb || gb == gc || gc == ga;
                foreach (var (x, y) in new[] { (ga, gb), (gb, gc), (gc, ga) })
                {
                    if (x == y) continue;
                    neighbours[x].Add(y);
                    neighbours[y].Add(x);
                    var key = Key(x, y);
                    if (!edgeFaces.TryGetValue(key, out var faces)) edgeFaces[key] = faces = new HashSet<(int, int, int)>();
                    if (!sliver) faces.Add(face);
                    if (edgeFirst.TryGetValue(key, out var other)) Union(faceIsland, t, other);
                    else edgeFirst[key] = t;
                }
            }
            foreach (var kv in edgeFaces)
                if (kv.Value.Count == 1) boundary.Add(kv.Key);
            Neighbours = neighbours.Select(n => n.ToArray()).ToArray();
            VertexTriangles = vertexTriangles.Select(l => l.ToArray()).ToArray();

            Island = Enumerable.Repeat(-1, VertexCount).ToArray();
            var islandIds = new Dictionary<int, int>();
            for (var t = 0; t < triangleCount; t++)
            {
                if (Collapsed(t)) continue;
                var root = Find(faceIsland, t);
                if (!islandIds.TryGetValue(root, out var id)) islandIds[root] = id = islandIds.Count;
                for (var k = 0; k < 3; k++)
                    if (Island[triangles[3 * t + k]] < 0) Island[triangles[3 * t + k]] = id;
            }
            var islands = Enumerable.Range(0, islandIds.Count).Select(_ => new List<int>()).ToArray();
            for (var v = 0; v < VertexCount; v++)
                if (Island[v] >= 0) islands[Island[v]].Add(v);
            IslandVertices = islands.Select(l => l.ToArray()).ToArray();
        }

        private bool Collapsed(int t) => Group[Triangles[3 * t]] == Group[Triangles[3 * t + 1]] && Group[Triangles[3 * t + 1]] == Group[Triangles[3 * t + 2]];

        public bool IsBoundary(int vertexA, int vertexB) => boundary.Contains(Key(Group[vertexA], Group[vertexB]));

        /// <summary>
        /// Area-weighted normals over the welded topology. A vertex sums the faces around its whole group that face its own side,
        /// so seams are smooth and the two sides of a double-sided sheet keep opposite normals.
        /// </summary>
        public Vector3[] Normals(Vector3[] positions)
        {
            var face = new Vector3[Triangles.Length / 3];
            var own = new Vector3[VertexCount];
            var ownArea = new float[VertexCount];
            for (var t = 0; t < face.Length; t++)
            {
                int a = Triangles[3 * t], b = Triangles[3 * t + 1], c = Triangles[3 * t + 2];
                face[t] = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
                own[a] += face[t];
                own[b] += face[t];
                own[c] += face[t];
                ownArea[a] += face[t].magnitude;
                ownArea[b] += face[t].magnitude;
                ownArea[c] += face[t].magnitude;
            }
            var normals = new Vector3[VertexCount];
            foreach (var group in Members)
            foreach (var v in group)
            {
                var sum = Vector3.zero;
                var largest = Vector3.zero;
                var all = Vector3.zero;
                foreach (var u in group)
                foreach (var t in VertexTriangles[u])
                {
                    if (Vector3.Dot(face[t], own[v]) > 0f) sum += face[t];
                    all += face[t];
                    if (face[t].sqrMagnitude > largest.sqrMagnitude) largest = face[t];
                }
                // Faces that cancel (a sheet sharing indices between its sides) leave no side of their own: take the strongest face
                // around the group, or the group's sum when the vertex is in no triangle.
                var cancelled = own[v].sqrMagnitude <= 1e-8f * ownArea[v] * ownArea[v];
                var normal = cancelled ? (ownArea[v] > 0f ? largest : all) : sum.sqrMagnitude > 0f ? sum : own[v];
                // Not .normalized: the sums are in m², and Unity zeroes vectors shorter than 1e-5 on fine geometry.
                var length = normal.magnitude;
                normals[v] = length > 0f ? normal / length : Vector3.up;
            }
            return normals;
        }

        /// <summary>A third of the area of each triangle around the vertex.</summary>
        public float[] VertexAreas(Vector3[] positions)
        {
            var area = new float[VertexCount];
            for (var t = 0; t < Triangles.Length / 3; t++)
            {
                int a = Triangles[3 * t], b = Triangles[3 * t + 1], c = Triangles[3 * t + 2];
                var third = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]).magnitude / 6f;
                area[a] += third;
                area[b] += third;
                area[c] += third;
            }
            return area;
        }

        private static long Key(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

        private static (int, int, int) Sorted(int a, int b, int c)
        {
            if (a > b) (a, b) = (b, a);
            if (b > c) (b, c) = (c, b);
            if (a > b) (a, b) = (b, a);
            return (a, b, c);
        }

        private static int Find(int[] parent, int i)
        {
            while (parent[i] != i) i = parent[i] = parent[parent[i]];
            return i;
        }

        private static void Union(int[] parent, int a, int b)
        {
            a = Find(parent, a);
            b = Find(parent, b);
            if (a != b) parent[a] = b;
        }
    }
}
