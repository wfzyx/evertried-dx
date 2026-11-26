using UnityEngine;

namespace EvertriedQoL
{
    /// <summary>
    /// The zone intro (IntroductionCutscene, the art shown when a new zone starts) begins its animation through a
    /// call delayed by 0.5 s after PlaySequence. Pressing Accept twice inside that delay skips the sequence
    /// (BiomeID 0, Active false) before the delayed call sets BiomeID; the animation then plays anyway, its end
    /// event calls EndSequence, which does nothing because the sequence is no longer active, and the zone art stays
    /// on top of the game for the rest of the session while play goes on under it. The boss cutscene does not have
    /// this problem because its interrupt stops its coroutines. Here the delayed call does nothing once the
    /// sequence has ended.
    /// </summary>
    public static class DxCutscene
    {
        /// <summary>Prologue of the delayed start (IntroductionCutscene closure &lt;PlaySequence&gt;b__0): true = skip it.</summary>
        public static bool StartCancelled(IntroductionCutscene cutscene)
        {
            if (cutscene == null || cutscene.Active)
            {
                return false;
            }
            Debug.Log("[EvertriedQoL] Zone intro was skipped before its animation started; not starting it");
            return true;
        }
    }
}
