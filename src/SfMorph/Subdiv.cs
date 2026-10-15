using UnityEngine;

namespace SfMorph
{
    // One level of PN-triangle subdivision, in place on a readable skinned mesh (0.7.2 "Fidelity"). (Interface stub.)
    internal static class Subdiv
    {
        internal class Undo { public void Restore() { } }
        public static Undo Apply(Mesh m, SkinnedMeshRenderer r, out string note) { note = ""; return null; }
    }
}
