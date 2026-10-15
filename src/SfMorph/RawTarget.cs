using UnityEngine;

namespace SfMorph
{
    // vertexBufferTarget |= Raw on a SkinnedMeshRenderer, ref-counted so GpuContact and Wrinkles don't undo each other.
    // (Interface stub.)
    internal static class RawTarget
    {
        public static void Acquire(SkinnedMeshRenderer r) { }
        public static void Release(SkinnedMeshRenderer r) { }
    }
}
