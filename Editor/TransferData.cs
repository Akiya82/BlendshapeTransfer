using System;
using System.Collections.Generic;
using UnityEngine;

namespace Akiya82.BlendshapeTransfer
{
    /// <summary>Recipe of a generated garment mesh, stored as a sub-asset of it.</summary>
    public sealed class TransferData : ScriptableObject
    {
        public Mesh sourceMesh;
        public string sourceHash = "";
        public string rendererPath = "";
        public TransferSettings settings = new TransferSettings();
        public int layerIndex = -1;
        /// <summary>One vertex per island the owner made rigid.</summary>
        public List<int> rigidIslandVertices = new List<int>();
        public List<ShapeEntry> shapes = new List<ShapeEntry>();
        /// <summary>Body weights the source fits, recorded at the first transfer that reached each shape.</summary>
        public List<BaseWeight> baseWeights = new List<BaseWeight>();
        /// <summary>The renderer's weights of the garment's own shapes under ticked body names (overwritten or kept), recorded at the first transfer: the source fits at them.</summary>
        public List<BaseWeight> garmentWeights = new List<BaseWeight>();
        /// <summary>Local shape names whose Blendshape Sync binding this tool added.</summary>
        public List<string> addedBindings = new List<string>();
        public bool addedSyncComponent;
        /// <summary>rendererPaths of the lower garments this one is never shown with; anti-clip does not collide with them.</summary>
        public List<string> notWornWith = new List<string>();

        public ShapeEntry Entry(string name) => shapes.Find(e => e.name == name);

        public ShapeEntry GetOrAddEntry(string name)
        {
            var entry = Entry(name);
            if (entry == null) shapes.Add(entry = new ShapeEntry { name = name });
            return entry;
        }
    }

    [Serializable]
    public sealed class ShapeEntry
    {
        public string name;
        public bool enabled = true;
        public bool overwrite;
        /// <summary>Edits per frame of the body shape; a missing or null slot has no edits.</summary>
        public List<ShapeEdit> frames = new List<ShapeEdit>();

        public ShapeEdit Edit(int frame) => frame < frames.Count ? frames[frame] : null;
    }

    [Serializable]
    public struct BaseWeight
    {
        public string name;
        public float weight;
    }
}
