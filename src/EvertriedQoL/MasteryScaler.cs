using System.Collections.Generic;
using UnityEngine;

namespace EvertriedQoL
{
    /// <summary>
    /// Mastery tiers store cumulative cast thresholds (<c>xpRequirement</c>) on SkillTreeData
    /// ScriptableObjects baked into the game assets. Each skill cast adds 1 xp, so changing the
    /// thresholds changes the casts needed. The patcher injects a call to <see cref="EnsureScaled"/>
    /// at the start of every SkillTreeData instance method, so a tree is scaled exactly once, before
    /// any of its data is read (this includes save loading, which calls SetXpAmount).
    /// </summary>
    public static class MasteryScaler
    {
        /// <summary>Multiplier for trees whose game thresholds are small defaults (10/20/30 become 3/5/8).</summary>
        public const float Factor = 0.25f;

        /// <summary>Casts per mastery tier for skills with real mastery data: tiers unlock at 40, 80 and 120 casts.</summary>
        public const int CastsPerTier = 40;

        /// <summary>The game's own tables use 120/240/720; anything at or above this is such a table.</summary>
        private const float TableThreshold = 100f;

        private static readonly Dictionary<int, bool> Scaled = new Dictionary<int, bool>();

        public static void EnsureScaled(SkillTreeData tree)
        {
            if (tree == null)
            {
                return;
            }

            int id = tree.GetInstanceID();
            if (Scaled.ContainsKey(id))
            {
                return;
            }
            Scaled[id] = true; // set first: reading MasteryTiers re-enters this hook

            SkillTreeData.MasteryTier[] source = tree.MasteryTiers;
            if (source == null || source.Length == 0)
            {
                return;
            }

            SkillTreeData.MasteryTier[] tiers = (SkillTreeData.MasteryTier[])source.Clone();
            bool table = tiers[tiers.Length - 1].xpRequirement >= TableThreshold;
            float previous = 0f;
            for (int i = 0; i < tiers.Length; i++)
            {
                float original = tiers[i].xpRequirement;
                if (original <= 0f)
                {
                    continue;
                }
                float scaled = table
                    ? (float)(CastsPerTier * (i + 1))
                    : (float)System.Math.Floor(original * Factor + 0.5f);
                if (scaled < 1f)
                {
                    scaled = 1f;
                }
                if (scaled <= previous)
                {
                    scaled = previous + 1f;
                }
                tiers[i].xpRequirement = scaled;
                previous = scaled;
            }
            tree.SetMasteryTiers(tiers);
            Debug.Log("[EvertriedQoL] Scaled mastery tiers for " + tree.name);
        }

        /// <summary>
        /// Called at the start of <c>SkillTreeData.SetXpAmount</c> (loading a save, each cast, the Alchemist):
        /// the xp a save already has is kept, but never above the top tier's threshold, so lowering the
        /// thresholds leaves fully mastered skills exactly at max instead of past it.
        /// </summary>
        public static int ClampXp(SkillTreeData tree, int value)
        {
            if (tree == null)
            {
                return value;
            }
            EnsureScaled(tree);
            SkillTreeData.MasteryTier[] tiers = tree.MasteryTiers;
            if (tiers == null || tiers.Length == 0)
            {
                return value;
            }
            int max = (int)tiers[tiers.Length - 1].xpRequirement;
            if (max > 0 && value > max)
            {
                return max;
            }
            return value;
        }
    }
}
