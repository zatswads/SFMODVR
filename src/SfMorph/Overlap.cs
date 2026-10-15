namespace SfMorph
{
    // Secondary animation: her limbs, head and spine follow the animation through damped springs (overlap, follow-through).
    // (Interface stub for 0.7.2.)
    internal class OverlapSettings { public float Amount, Arms = 1f, Legs = 1f, Head = 1f, Spine = 1f; public Embody Embody; }   // Embody: whose arms it drives (fade those)

    internal class Overlap
    {
        public string Status = "";
        public void Update(Character her, OverlapSettings s, float dt) { }
        public void Release() { Status = ""; }
    }
}
