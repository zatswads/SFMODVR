using System.Collections.Generic;
using UnityEngine;

namespace SfMorph
{
    // vertexBufferTarget |= Raw on a SkinnedMeshRenderer, ref-counted so GpuContact and Wrinkles don't undo each other. The flag is only
    // taken back off when it wasn't set before the first Acquire (the game's own setting stays).
    internal static class RawTarget
    {
        class Entry { public int Count; public bool WeSet; }
        static readonly Dictionary<SkinnedMeshRenderer, Entry> Held = new Dictionary<SkinnedMeshRenderer, Entry>();

        public static void Acquire(SkinnedMeshRenderer r)
        {
            if (r == null) return;
            Entry e;
            if (!Held.TryGetValue(r, out e))
            {
                e = new Entry();
                Held[r] = e;
                var t = r.vertexBufferTarget;
                if ((t & GraphicsBuffer.Target.Raw) == 0)
                {
                    r.vertexBufferTarget = t | GraphicsBuffer.Target.Raw;   // Unity recreates the skinned buffers once
                    e.WeSet = true;
                }
            }
            e.Count++;
        }

        public static void Release(SkinnedMeshRenderer r)
        {
            if (r == null)
            {
                // destroyed renderers: drop their entries (nothing left to restore)
                var dead = new List<SkinnedMeshRenderer>();
                foreach (var k in Held.Keys) if (k == null) dead.Add(k);
                foreach (var k in dead) Held.Remove(k);
                return;
            }
            Entry e;
            if (!Held.TryGetValue(r, out e)) return;
            if (--e.Count > 0) return;
            Held.Remove(r);
            if (e.WeSet) r.vertexBufferTarget = r.vertexBufferTarget & ~GraphicsBuffer.Target.Raw;
        }

        // True when the Raw flag on r was put there by us (and is still held).
        public static bool SetByUs(SkinnedMeshRenderer r)
        {
            Entry e;
            return r != null && Held.TryGetValue(r, out e) && e.WeSet;
        }
    }
}
