using UnityEngine;

namespace Akiya82.BlendshapeTransfer
{
    /// <summary>Hand edits of one frame of one transferred shape, per vertex of the source mesh. An empty array means no edit of that kind.</summary>
    public sealed class ShapeEdit : ScriptableObject
    {
        public float[] mask = new float[0];
        public Vector3[] sculpt = new Vector3[0];

        public bool IsEmpty => mask.Length == 0 && sculpt.Length == 0;
    }
}
