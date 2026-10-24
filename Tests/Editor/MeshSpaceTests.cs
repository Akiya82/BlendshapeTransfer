using NUnit.Framework;
using UnityEngine;

namespace Akiya82.BlendshapeTransfer.Tests
{
    public sealed class MeshSpaceTests
    {
        [Test]
        public void ApplyEdit_MasksThenAddsSculpt()
        {
            var auto = new[] { Vector3.one, Vector3.one, Vector3.one };
            var edit = ScriptableObject.CreateInstance<ShapeEdit>();
            edit.mask = new[] { 0f, 0.5f, 1f };
            edit.sculpt = new[] { Vector3.zero, Vector3.zero, Vector3.up };

            var final = MeshSpace.ApplyEdit(auto, edit);

            Assert.That(final[0], Is.EqualTo(Vector3.zero));
            Assert.That(final[1], Is.EqualTo(Vector3.one * 0.5f));
            Assert.That(final[2], Is.EqualTo(Vector3.one + Vector3.up));
            Assert.That(MeshSpace.ApplyEdit(auto, null), Is.EqualTo(auto));
            Object.DestroyImmediate(edit);
        }

        [Test]
        public void NormalDeltas_AreExactlyZeroWhereNothingAroundMoves()
        {
            var mesh = MeshFactory.Grid(MeshFactory.Range(-0.1f, 0.1f, 21), MeshFactory.Range(-0.1f, 0.1f, 21), 0f);
            var positions = mesh.vertices;
            var topology = new Topology(positions, mesh.triangles);
            var bump = MeshFactory.Bump(0.01f, 0.04f);
            var delta = new Vector3[positions.Length];
            for (var v = 0; v < delta.Length; v++) delta[v] = bump(positions[v]);

            var normals = MeshSpace.NormalDeltas(topology, positions, delta);

            var checkedAny = false;
            for (var v = 0; v < positions.Length; v++)
            {
                var still = true;
                foreach (var t in topology.VertexTriangles[v])
                for (var k = 0; k < 3; k++)
                    if (delta[mesh.triangles[3 * t + k]].sqrMagnitude != 0f) still = false;
                if (!still) continue;
                checkedAny = true;
                Assert.That(normals[v], Is.EqualTo(Vector3.zero), $"vertex {v}");
            }
            Assert.That(checkedAny, Is.True);
            Assert.That(normals[220].magnitude, Is.EqualTo(0f).Within(1e-6f), "the bump's apex stays flat");
            Assert.That(normals[223].magnitude, Is.GreaterThan(0.01f), "its slope tilts");
            Object.DestroyImmediate(mesh);
        }

        [Test]
        public void SourceHash_ChangesWithVertexOrder()
        {
            var a = MeshFactory.Grid(MeshFactory.Range(0f, 1f, 3), MeshFactory.Range(0f, 1f, 3), 0f);
            var b = MeshFactory.Grid(MeshFactory.Range(0f, 1f, 3), MeshFactory.Range(0f, 1f, 3), 0f);
            Assert.That(SourceHash.Of(b), Is.EqualTo(SourceHash.Of(a)));
            var v = b.vertices;
            (v[0], v[1]) = (v[1], v[0]);
            b.vertices = v;
            Assert.That(SourceHash.Of(b), Is.Not.EqualTo(SourceHash.Of(a)));
            Object.DestroyImmediate(a);
            Object.DestroyImmediate(b);
        }
    }
}
