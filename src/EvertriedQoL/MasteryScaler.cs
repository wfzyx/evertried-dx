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
        /// <summary>Multiplier for trees that keep the old rule (the ones whose game thresholds are small defaults).</summary>
        public const float Factor = 0.5f;

        /// <summary>Casts per mastery tier for skills with real mastery data: tiers unlock at 80, 160 and 240 casts.</summary>
        public const int CastsPerTier = 80;

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
    }
}
