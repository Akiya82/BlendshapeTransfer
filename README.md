# Blendshape Transfer

A Unity editor tool for VRChat avatars that transfers the body's blendshapes onto clothing meshes. Each garment gets the
body's shapes (belly, chest, body-shape sliders), follows them through MA Blendshape Sync, and is kept clear of the skin
and of the garments under it. The window is available in English, Russian, Japanese and Simplified Chinese.

## Install

1. In the VRChat Creator Companion, open **Settings → Packages → Add Repository** and add both listings, Modular Avatar's first:
   - `https://vpm.nadena.dev/vpm.json` (Modular Avatar, a dependency)
   - `https://akiya82.github.io/vpm/index.json`
2. In **Manage Project**, add **Blendshape Transfer**. VCC installs Modular Avatar with it.

Requires Unity 2022.3, VRChat Avatars SDK 3.10 or later and Modular Avatar 1.18 or later.

## Use

1. Open **Tools → Akiya82 → Blendshape Transfer**.
2. Set the avatar's body renderer and add the garments (**+ Selected** or **+ With saved transfers**), inner layers first.
3. **Read meshes**, then check each garment's settings and its ticked shapes.
4. **Transfer** computes the garments whose inputs changed; **All again** computes every garment.
5. **Save** puts each garment's output mesh on its renderer and binds the shapes with MA Blendshape Sync.

Outputs are written to `Assets/BlendshapeTransferGenerated/<avatar>/` in your project. The original meshes are not modified,
and **Restore original mesh** puts a garment back on its source mesh.

## License

MIT, see [LICENSE](LICENSE).
