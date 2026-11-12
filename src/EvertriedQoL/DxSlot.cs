using System.Collections;
using UnityEngine;

namespace EvertriedQoL
{
    /// <summary>
    /// The Windows build animates a freshly gained skill slot (after a boss, or a slot stash). The Linux depot has no
    /// such animation (the Windows one uses a new outline shader and materials that are not in the Linux data), so
    /// this blinks the slot's selection outline a few times instead.
    /// The patcher calls <see cref="Added"/> at the end of PlayerCombat.AddSkillSlot.
    /// </summary>
    public static class DxSlot
    {
        private const int Blinks = 4;
        private const float Interval = 0.2f;

        public static void Added(PlayerCombat combat)
        {
            try
            {
                SkillSlotsHUD hud = SkillSlotsHUD.Instance;
                if (hud == null)
                {
                    return;
                }
                SkillSlotBase slot = hud.GetSkillSlot(combat.CurrentSkills.Length - 1);
                if (slot != null && slot.gameObject.activeInHierarchy)
                {
                    slot.StartCoroutine(Blink(slot));
                }
            }
            catch (System.Exception e)
            {
                Debug.Log("[EvertriedQoL] New skill slot highlight failed: " + e.Message);
            }
        }

        private static IEnumerator Blink(SkillSlotBase slot)
        {
            yield return new WaitForSecondsRealtime(0.4f);
            for (int i = 0; i < Blinks * 2 && slot != null; i++)
            {
                slot.SetOutlineActive(i % 2 == 0);
                yield return new WaitForSecondsRealtime(Interval);
            }
            if (slot != null)
            {
                slot.SetOutlineActive(false);
            }
        }
    }
}
