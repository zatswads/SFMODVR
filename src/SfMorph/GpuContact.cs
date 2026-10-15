using System.Collections.Generic;

namespace SfMorph
{
    // Post-skinning contact deformer on the GPU (0.7.3): her skinned vertices are pressed out of the hands' capsules by a compute
    // shader from dist/sfmorph_shaders. (Interface stub.)
    internal class GpuContact
    {
        public string Status = "";
        public static GpuContact TryLoad(string dir) { return null; }
        public void Sync(SoftZones z) { }
        public void SetCapsules(List<SoftCapsule> caps, float limit, float margin) { }
        public void Smoke(float seconds, float metres) { }
        public void Release() { Status = ""; }
    }
}
