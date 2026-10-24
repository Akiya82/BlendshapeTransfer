using UnityEngine;

namespace Akiya82.BlendshapeTransfer
{
    /// <summary>
    /// What a garment's output was written from and its report, a sub-asset of the output written right after the mesh. Undo never
    /// records it: the mesh write cannot be undone, so the record of what the mesh is must not be rolled back either.
    /// </summary>
    public sealed class TransferOutput : ScriptableObject
    {
        public string fingerprint = "";
        public GarmentReport report = new GarmentReport();
    }
}
