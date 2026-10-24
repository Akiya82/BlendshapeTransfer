using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Akiya82.BlendshapeTransfer.Tests
{
    public sealed class RigidTests
    {
        [Test]
        public void Rigid_KeepsAnIslandsShapeAndStillMovesIt()
        {
            var box = MeshFactory.Box(new Vector3(0.01f, 0f, 0.012f), 0.015f);
            var positions = box.vertices;
            var topology = new Topology(positions, box.triangles);
            var delta = new Vector3[positions.Length];
            var bump = MeshFactory.Bump(0.01f, 0.04f);
            for (var v = 0; v < delta.Length; v++) delta[v] = bump(positions[v]) + new Vector3(0.002f * positions[v].y, 0f, 0f);

            var islands = DeltaOps.RigidIslands(topology, positions, 0.03f, new int[0]);
            DeltaOps.Rigid(topology, positions, delta, islands);

            Assert.That(islands.Count, Is.EqualTo(1));
            var moved = Vector3.zero;
            for (var a = 0; a < positions.Length; a++)
            {
                moved += delta[a] / positions.Length;
                for (var b = a + 1; b < positions.Length; b++)
                    Assert.That(((positions[a] + delta[a]) - (positions[b] + delta[b])).magnitude,
                        Is.EqualTo((positions[a] - positions[b]).magnitude).Within(1e-6f), $"edge {a}-{b}");
            }
            Assert.That(moved.magnitude, Is.GreaterThan(0.001f));
        }

        [Test]
        public void Rigid_DegenerateIslandsOnlyTranslate()
        {
            // a triangle with two coincident corners (two welded groups) and a collinear triangle
            var positions = new[]
            {
                new Vector3(0f, 0f, 0f), new Vector3(0.01f, 0f, 0f), new Vector3(0.01f, 0f, 0f),
                new Vector3(0.1f, 0f, 0f), new Vector3(0.105f, 0f, 0f), new Vector3(0.11f, 0f, 0f),
            };
            var topology = new Topology(positions, new[] { 0, 1, 2, 3, 4, 5 });
            var delta = new[]
            {
                new Vector3(0f, 0.001f, 0f), new Vector3(0f, 0.003f, 0f), new Vector3(0f, 0.002f, 0f),
                new Vector3(0f, 0.001f, 0f), new Vector3(0f, 0.004f, 0f), new Vector3(0f, 0.001f, 0f),
            };

            DeltaOps.Rigid(topology, positions, delta, new HashSet<int> { topology.Island[0], topology.Island[3] });

            Assert.That(delta[1], Is.EqualTo(delta[0]));
            Assert.That(delta[2], Is.EqualTo(delta[0]));
            Assert.That(delta[4], Is.EqualTo(delta[3]));
            Assert.That(delta[5], Is.EqualTo(delta[3]));
            Assert.That(delta[3].y, Is.EqualTo(0.002f).Within(1e-7f), "the mean of the island");
        }

        private static Vector3[] RotatedDelta(Vector3[] positions, Vector3 center, Quaternion rotation, Vector3 translation)
        {
            var delta = new Vector3[positions.Length];
            for (var v = 0; v < delta.Length; v++) delta[v] = center + translation + rotation * (positions[v] - center) - positions[v];
            return delta;
        }

        [Test]
        public void Rigid_RecoversARotationOfABox()
        {
            var box = MeshFactory.Box(new Vector3(0.01f, 0.02f, 0.012f), 0.015f);
            var positions = box.vertices;
            var topology = new Topology(positions, box.triangles);
            var center = new Vector3(0.01f, 0.02f, 0.012f);
            var expected = RotatedDelta(positions, center, Quaternion.AngleAxis(20f, new Vector3(1f, 2f, -0.5f).normalized), new Vector3(0.003f, -0.001f, 0.002f));
            var delta = (Vector3[])expected.Clone();

            DeltaOps.Rigid(topology, positions, delta, new HashSet<int> { topology.Island[0] });

            for (var v = 0; v < delta.Length; v++)
                Assert.That((delta[v] - expected[v]).magnitude, Is.LessThan(1e-5f), $"vertex {v}");
        }

        [Test]
        public void Rigid_RecoversARotationOfAFlatIsland()
        {
            var quad = MeshFactory.Grid(MeshFactory.Range(0f, 0.02f, 2), MeshFactory.Range(0f, 0.01f, 2), 0f);
            var positions = quad.vertices;
            var topology = new Topology(positions, quad.triangles);
            var expected = RotatedDelta(positions, new Vector3(0.01f, 0.005f, 0f), Quaternion.AngleAxis(25f, new Vector3(0.3f, 1f, 0.7f).normalized), new Vector3(0.001f, 0.002f, -0.003f));
            var delta = (Vector3[])expected.Clone();

            DeltaOps.Rigid(topology, positions, delta, new HashSet<int> { topology.Island[0] });

            for (var v = 0; v < delta.Length; v++)
                Assert.That((delta[v] - expected[v]).magnitude, Is.LessThan(1e-5f), $"vertex {v}");
        }

        [Test]
        public void Rigid_WeldedPinchPointFollowsTheRigidIsland()
        {
            // a ring triangle (0-2) and a strap triangle (3-5) sharing one position: vertices 0 and 3
            var positions = new[]
            {
                new Vector3(0f, 0f, 0f), new Vector3(0.01f, 0f, 0f), new Vector3(0f, 0.01f, 0f),
                new Vector3(0f, 0f, 0f), new Vector3(-0.01f, 0f, 0f), new Vector3(0f, -0.01f, 0f),
            };
            var topology = new Topology(positions, new[] { 0, 1, 2, 3, 4, 5 });
            var delta = new[]
            {
                new Vector3(0.001f, 0f, 0f), new Vector3(0.002f, 0.001f, 0f), new Vector3(0f, 0.003f, 0.001f),
                new Vector3(0f, 0f, 0.002f), new Vector3(0f, 0.004f, 0f), new Vector3(0.003f, 0f, 0f),
            };

            DeltaOps.Weld(topology, delta);
            DeltaOps.Rigid(topology, positions, delta, new HashSet<int> { topology.Island[0] });

            Assert.That(topology.Island[3], Is.Not.EqualTo(topology.Island[0]));
            Assert.That(delta[3], Is.EqualTo(delta[0]));
        }

        [Test]
        public void RigidIslands_TakesSmallIslandsAndMarkedOnes()
        {
            var big = MeshFactory.Grid(MeshFactory.Range(0f, 0.1f, 3), MeshFactory.Range(0f, 0.1f, 3), 0f);
            var small = MeshFactory.Box(new Vector3(0.5f, 0f, 0f), 0.01f);
            var mesh = MeshFactory.Merge(big, small);
            var topology = new Topology(mesh.vertices, mesh.triangles);

            var bySize = DeltaOps.RigidIslands(topology, mesh.vertices, 0.03f, new int[0]);
            var marked = DeltaOps.RigidIslands(topology, mesh.vertices, 0.03f, new[] { 0 });

            Assert.That(bySize, Is.EquivalentTo(new[] { topology.Island[9] }));
            Assert.That(marked, Is.EquivalentTo(new[] { topology.Island[0], topology.Island[9] }));
        }
    }
}
