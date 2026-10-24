using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Akiya82.BlendshapeTransfer.Tests
{
    public sealed class SurfaceQueryTests
    {
        private static SurfaceQuery Triangle(bool rejectBoundary)
        {
            var positions = new[] { new Vector3(0, 0, 0), new Vector3(0.1f, 0, 0), new Vector3(0, 0.1f, 0) };
            var topology = new Topology(positions, new[] { 0, 1, 2 });
            return new SurfaceQuery(positions, topology.Normals(positions), topology, rejectBoundary);
        }

        [Test]
        public void Closest_FindsEdgeAndVertexPoints()
        {
            var query = Triangle(false);
            Assert.That(query.Closest(new Vector3(0.05f, -0.05f, 0.01f), 1f, null, out var edge), Is.True);
            Assert.That((edge.Point - new Vector3(0.05f, 0f, 0f)).magnitude, Is.LessThan(1e-6f));
            Assert.That(edge.Bary.z, Is.EqualTo(0f).Within(1e-6f));
            Assert.That(query.Closest(new Vector3(-0.1f, -0.1f, 0f), 1f, null, out var corner), Is.True);
            Assert.That(corner.Point, Is.EqualTo(Vector3.zero));
            Assert.That(corner.Corner, Is.EqualTo(0));
        }

        [Test]
        public void Closest_RejectsBoundaryPointsWhenAsked()
        {
            var query = Triangle(true);
            Assert.That(query.Closest(new Vector3(0.02f, 0.02f, 0.01f), 1f, null, out var inside), Is.True);
            Assert.That(SurfaceQuery.Signed(new Vector3(0.02f, 0.02f, 0.01f), inside), Is.EqualTo(0.01f).Within(1e-6f));
            Assert.That(query.Closest(new Vector3(0.05f, -0.05f, 0.01f), 1f, null, out _), Is.False);
        }

        [Test]
        public void Closest_SkipsRejectedTrianglesAndKeepsSearching()
        {
            // two parallel quads, near (z=0, corners 0-3) and far (z=0.05, corners 4-7)
            var positions = new[]
            {
                new Vector3(-0.1f, -0.1f, 0f), new Vector3(0.1f, -0.1f, 0f), new Vector3(0.1f, 0.1f, 0f), new Vector3(-0.1f, 0.1f, 0f),
                new Vector3(-0.1f, -0.1f, 0.05f), new Vector3(0.1f, -0.1f, 0.05f), new Vector3(0.1f, 0.1f, 0.05f), new Vector3(-0.1f, 0.1f, 0.05f),
            };
            var triangles = new[] { 0, 1, 2, 0, 2, 3, 4, 6, 5, 4, 7, 6 };
            var topology = new Topology(positions, triangles);
            var query = new SurfaceQuery(positions, topology.Normals(positions), topology, false);

            Assert.That(query.Closest(new Vector3(0f, 0f, 0.01f), 1f, (corner, p, n) => corner >= 4, out var far), Is.True);
            Assert.That(far.Point.z, Is.EqualTo(0.05f).Within(1e-6f));
            Assert.That(far.Normal.z, Is.LessThan(-0.99f), "the far quad faces -z");
        }

        [Test]
        public void Closest_SameResultForSmallAndLargeRadius()
        {
            var query = Triangle(false);
            var p = new Vector3(0.03f, 0.03f, 0.3f);
            Assert.That(query.Closest(p, 0.5f, null, out var small), Is.True);
            Assert.That(query.Closest(p, 5f, null, out var large), Is.True);
            Assert.That(large.Point, Is.EqualTo(small.Point));
            Assert.That(large.Distance, Is.EqualTo(0.3f).Within(1e-6f));
        }

        [Test]
        public void Closest_FindsAFaceWhoseCornersAreAllBeyondTheRadius()
        {
            // the nearest corner is 4.4 cm away, the face 1 cm
            Assert.That(Triangle(false).Closest(new Vector3(0.03f, 0.03f, 0.01f), 0.02f, null, out var hit), Is.True);
            Assert.That(hit.Distance, Is.EqualTo(0.01f).Within(1e-6f));
        }

        [Test]
        public void TrianglesNear_HoldsEveryTriangleWithinTheRadius_OnAnIrregularMeshWithLongEdges()
        {
            // a jittered 3 mm grid with a fan of slivers from its right column to a corner 6 cm away, so long-edge levels take part
            var random = new System.Random(7);
            float Jitter(float amount) => amount * (float)random.NextDouble();
            const int side = 15;
            var positions = new List<Vector3>();
            for (var j = 0; j < side; j++)
            for (var i = 0; i < side; i++)
                positions.Add(new Vector3(0.003f * i + Jitter(0.001f), 0.003f * j + Jitter(0.001f), Jitter(0.0005f)));
            var triangles = new List<int>();
            for (var j = 0; j < side - 1; j++)
            for (var i = 0; i < side - 1; i++)
            {
                int a = j * side + i, b = a + 1, c = a + side, d = c + 1;
                triangles.AddRange(new[] { a, b, c, b, d, c });
            }
            positions.Add(new Vector3(0.1f, 0.02f, 0.01f));
            for (var j = 0; j < side - 1; j++) triangles.AddRange(new[] { j * side + side - 1, positions.Count - 1, (j + 1) * side + side - 1 });
            var p = positions.ToArray();
            var t = triangles.ToArray();
            var count = t.Length / 3;
            var query = new SurfaceQuery(p, null, new Topology(p, t), rejectBoundary: false);
            // the slivers' centroids first: every corner of a sliver is 2 cm or more from it
            var points = Enumerable.Range(count - (side - 1), side - 1).Select(f => (p[t[3 * f]] + p[t[3 * f + 1]] + p[t[3 * f + 2]]) / 3f).ToList();
            for (var k = 0; k < 400; k++) points.Add(new Vector3(-0.005f + Jitter(0.11f), -0.005f + Jitter(0.055f), -0.004f + Jitter(0.016f)));
            var found = new List<int>();
            var near = 0;
            for (var k = 0; k < points.Count; k++)
            {
                var radius = k % 2 == 0 ? 0.003f : 0.01f;
                query.TrianglesNear(points[k], radius, found);
                Assert.That(found.Distinct().Count(), Is.EqualTo(found.Count), $"point {k}: each triangle once");
                for (var f = 0; f < count; f++)
                {
                    var distance = (Geometry.ClosestPointOnTriangle(points[k], p[t[3 * f]], p[t[3 * f + 1]], p[t[3 * f + 2]], out _) - points[k]).magnitude;
                    if (distance > radius) continue;
                    near++;
                    Assert.That(found.Contains(f), Is.True, $"point {k}: triangle {f} at {distance * 1000f:0.00} mm within {radius * 1000f:0} mm");
                }
            }
            Assert.That(near, Is.GreaterThan(500), "the points come near the mesh");
        }
    }
}
