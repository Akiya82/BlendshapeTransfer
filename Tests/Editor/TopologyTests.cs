using NUnit.Framework;
using UnityEngine;

namespace Akiya82.BlendshapeTransfer.Tests
{
    public sealed class TopologyTests
    {
        // A quad whose second triangle uses seam duplicates of corners 0 and 2, a back-face copy of the quad,
        // and a separate triangle touching corner 2 at one point.
        private static readonly Vector3[] Positions =
        {
            new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 1, 0), new Vector3(0, 1, 0), // 0-3 front
            new Vector3(1, 1, 0), new Vector3(0, 0, 0),                                             // 4, 5 seam duplicates of 2, 0
            new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 1, 0), new Vector3(0, 1, 0), // 6-9 back copy
            new Vector3(1, 1, 0), new Vector3(2, 1, 0), new Vector3(1, 2, 0),                       // 10-12 touching triangle
        };

        private static readonly int[] Triangles =
        {
            0, 1, 2, 5, 4, 3,   // front (+z)
            6, 8, 7, 6, 9, 8,   // back (-z)
            10, 11, 12,         // separate (+z)
        };

        [Test]
        public void Weld_GroupsCoincidentVerticesAcrossComponents()
        {
            var topology = new Topology(Positions, Triangles);
            Assert.That(topology.Group[5], Is.EqualTo(topology.Group[0]));
            Assert.That(topology.Group[6], Is.EqualTo(topology.Group[0]));
            Assert.That(topology.Group[4], Is.EqualTo(topology.Group[2]));
            Assert.That(topology.Group[10], Is.EqualTo(topology.Group[2]));
            Assert.That(topology.GroupCount, Is.EqualTo(6));
        }

        [Test]
        public void Islands_JoinThroughEdgesOnly()
        {
            var topology = new Topology(Positions, Triangles);
            Assert.That(topology.Island[3], Is.EqualTo(topology.Island[0]), "the seam keeps the quad one island");
            Assert.That(topology.Island[6], Is.EqualTo(topology.Island[0]), "the back face shares the quad's edges");
            Assert.That(topology.Island[10], Is.Not.EqualTo(topology.Island[0]), "a point contact does not join");
            Assert.That(topology.IslandVertices.Length, Is.EqualTo(2));
        }

        [Test]
        public void Boundary_CountsDuplicateFacesOnce()
        {
            var topology = new Topology(Positions, Triangles);
            Assert.That(topology.IsBoundary(0, 1), Is.True, "outer edge of a double-sided quad is still its edge");
            Assert.That(topology.IsBoundary(0, 2), Is.False, "the diagonal is shared by both triangles");
        }

        [Test]
        public void Normals_FollowEachSideOfADoubleSidedSheet()
        {
            var normals = new Topology(Positions, Triangles).Normals(Positions);
            Assert.That(normals[0].z, Is.GreaterThan(0.99f));
            Assert.That(normals[5].z, Is.GreaterThan(0.99f));
            Assert.That(normals[6].z, Is.LessThan(-0.99f));
            Assert.That(normals[10].z, Is.GreaterThan(0.99f));
        }

        [Test]
        public void Normals_AreDefinedWhenAVertexFacesCancel()
        {
            var positions = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 1, 0), new Vector3(0, 1, 0) };
            var triangles = new[] { 0, 1, 2, 0, 2, 3, 0, 2, 1, 0, 3, 2 };
            var normals = new Topology(positions, triangles).Normals(positions);
            foreach (var n in normals) Assert.That(n.magnitude, Is.EqualTo(1f).Within(1e-4f));
        }

        [Test]
        public void Normals_AreUnitOnMillimetreGeometry()
        {
            var mesh = MeshFactory.Grid(MeshFactory.Range(0f, 0.002f, 5), MeshFactory.Range(0f, 0.002f, 5), 0f);
            var normals = new Topology(mesh.vertices, mesh.triangles).Normals(mesh.vertices);
            foreach (var n in normals)
            {
                Assert.That(n.magnitude, Is.EqualTo(1f).Within(1e-4f));
                Assert.That(Mathf.Abs(n.z), Is.EqualTo(1f).Within(1e-4f));
            }
        }

        [Test]
        public void Boundary_IgnoresSliverTriangles()
        {
            var positions = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 1, 0), new Vector3(1, 0, 0) };
            var topology = new Topology(positions, new[] { 0, 1, 2, 3, 1, 2 });
            Assert.That(topology.IsBoundary(1, 2), Is.True);
        }

        [Test]
        public void Islands_IgnoreCollapsedTriangles()
        {
            var positions = new[] { new Vector3(0, 0, 0), new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0) };
            var topology = new Topology(positions, new[] { 0, 1, 0, 1, 2, 3 });
            Assert.That(topology.Island[1], Is.EqualTo(topology.Island[2]));
            Assert.That(topology.IslandVertices.Length, Is.EqualTo(1));
        }

        [Test]
        public void VertexAreas_SumToTheSurface()
        {
            var areas = new Topology(Positions, Triangles).VertexAreas(Positions);
            var total = 0f;
            foreach (var a in areas) total += a;
            Assert.That(total, Is.EqualTo(2.5f).Within(1e-5f)); // quad 1 + back 1 + triangle 0.5
        }
    }
}
