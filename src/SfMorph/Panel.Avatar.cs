using System.Globalization;
using UnityEngine;

namespace SfMorph
{
    // The Characters tab's avatar section (Avatar.cs): pick an avatar bundle, its size, its skin shader and brightness, hair, IK.
    internal partial class Panel
    {
        static readonly string[] AvatarScales = { "Her legs", "Her height", "Own size" };
        static readonly string[] AvatarShaders = { "Her skin shader", "HDRP Lit" };

        void AvatarSection(Character c)
        {
            GUILayout.Space(10);
            GUILayout.Label("Avatars", _bold);
            GUILayout.Label("A character's own mesh, rig and skin (tools/avatar/build.ps1) drawn instead of her and moved by her pose: her animation, " +
                            "IK and our motion still drive it. Her soft zones, dents, marks, wrinkles and muscles stay on her hidden body for now. Folder: " + Avatar.Root, _hint);
            // read once: the same controls in the Layout pass and the click pass
            var all = Avatar.Entries;
            string cur = (Avatar.Pick.Value ?? "").Trim(), status = Avatar.Status;
            string off = !c.Female || c.IsPlayer ? "only for the game's woman" : c.Look.Morph != null ? "a converted character is on: pick None above first" : null;
            int scale = Mathf.Clamp(Avatar.ScaleMode.Value, 0, 2), shader = Mathf.Clamp(Avatar.MaterialRoute.Value, 0, 1);
            float bright = Avatar.Brightness.Value, hik = Avatar.HandIk.Value, fik = Avatar.FootIk.Value;
            bool hair = Avatar.KeepHair.Value;

            const int cols = 3;
            const float card = 150f;
            string pick = null; bool pickNone = false;
            GUI.enabled = off == null;
            GUILayout.BeginHorizontal();
            if (Card(null, "None", cur.Length == 0, card)) pickNone = true;
            int i = 1;
            foreach (var e in all)
            {
                if (i % cols == 0) { GUILayout.FlexibleSpace(); GUILayout.EndHorizontal(); GUILayout.BeginHorizontal(); }
                if (Card(null, e.Name + "\n" + e.Info, cur == e.Name, card)) pick = e.Name;
                i++;
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUI.enabled = true;
            if (off != null) GUILayout.Label(off, _off);
            if (all.Count == 0) GUILayout.Label("off: no avatars yet. Build one with tools/avatar/build.ps1 -Fbx <character.fbx> and copy its folder into " + Avatar.Root + ".", _off);
            if (status.Length > 0) GUILayout.Label(status, status.StartsWith("off") ? _off : _hint);
            if (GUILayout.Button("Rescan the folder", GUILayout.Width(160))) Avatar.Rescan();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Size", GUILayout.Width(170));
            int ns = GUILayout.Toolbar(scale, AvatarScales);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Skin shader", GUILayout.Width(170));
            int nsh = GUILayout.Toolbar(shader, AvatarShaders);
            GUILayout.EndHorizontal();
            float nb = Slider("Skin brightness", bright, 0.5f, 1.5f, "0.00", 1f);
            bool nh = GUILayout.Toggle(hair, " Keep her hair (it follows her head)");
            float nhik = Slider("Hands onto hers", hik, 0f, 1f, "0.00", 0f);
            float nfik = Slider("Feet onto hers", fik, 0f, 1f, "0.00", 0f);
            GUILayout.Label("Hands / feet onto hers: 0 keeps the avatar's own arm and leg lengths (its hands can float a few cm from where hers grip); " +
                            "1 puts them exactly on hers and bends the elbows / knees to make up the difference.", _hint);

            if (pickNone && cur.Length > 0) Avatar.Pick.Value = "";
            if (pick != null && pick != cur) Avatar.Pick.Value = pick;
            if (ns != scale) Avatar.ScaleMode.Value = ns;
            if (nsh != shader) Avatar.MaterialRoute.Value = nsh;
            if (Mathf.Abs(nb - bright) > 0.0001f) Avatar.Brightness.Value = nb;
            if (nh != hair) Avatar.KeepHair.Value = nh;
            if (Mathf.Abs(nhik - hik) > 0.0001f) Avatar.HandIk.Value = nhik;
            if (Mathf.Abs(nfik - fik) > 0.0001f) Avatar.FootIk.Value = nfik;
        }
    }
}
