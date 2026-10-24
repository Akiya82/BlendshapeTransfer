using NUnit.Framework;
using UnityEngine;

namespace Akiya82.BlendshapeTransfer.Tests
{
    public sealed class ShapeMathTests
    {
        [Test]
        public void DeltaAt_FollowsUnityFrameInterpolation()
        {
            var weights = new[] { 50f, 100f };
            var frames = new[] { new[] { Vector3.one }, new[] { Vector3.one * 3f } };
            var output = new Vector3[1];

            ShapeMath.DeltaAt(weights, frames, 25f, output);
            Assert.That(output[0].x, Is.EqualTo(0.5f).Within(1e-6f), "zero to the first frame");
            ShapeMath.DeltaAt(weights, frames, 75f, output);
            Assert.That(output[0].x, Is.EqualTo(2f).Within(1e-6f), "between frames");
            ShapeMath.DeltaAt(weights, frames, 0f, output);
            Assert.That(output[0], Is.EqualTo(Vector3.zero));
            ShapeMath.DeltaAt(new[] { 100f }, new[] { new[] { Vector3.one } }, 150f, output);
            Assert.That(output[0].x, Is.EqualTo(1.5f).Within(1e-6f), "past the last frame");
        }

        [Test]
        public void PositionsWithWeights_AddsShapesByName()
        {
            var mesh = MeshFactory.Grid(MeshFactory.Range(0f, 1f, 2), MeshFactory.Range(0f, 1f, 2), 0f);
            MeshFactory.AddShape(mesh, "Up", _ => Vector3.forward);
            MeshFactory.AddShape(mesh, "Side", _ => Vector3.right);

            var positions = ShapeMath.PositionsWithWeights(mesh, name => name == "Up" ? 50f : 0f);

            Assert.That(positions[0], Is.EqualTo(new Vector3(0f, 0f, 0.5f)));
            Object.DestroyImmediate(mesh);
        }
    }
}
