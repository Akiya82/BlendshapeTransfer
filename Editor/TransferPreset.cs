using UnityEngine;

namespace Akiya82.BlendshapeTransfer
{
    /// <summary>A preset the owner saved. Built-in presets live in code (TransferSettings.BuiltIns).</summary>
    [CreateAssetMenu(menuName = "Blendshape Transfer/Preset")]
    public sealed class TransferPreset : ScriptableObject
    {
        public TransferSettings settings = new TransferSettings();
    }
}
