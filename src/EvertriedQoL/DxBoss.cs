using Evertried.Bosses.SmokeBoss;
using UnityEngine;

namespace EvertriedQoL
{
    /// <summary>
    /// The "Denial" boss (Boss_Smoke, floor 10) has no body of its own: it stays at the origin while smoke cells
    /// move around and one of them is the vulnerable spot. A Mark applied to it was parented to that fixed root and
    /// stayed on the ground. The Windows build overrides ApplyMark for this boss; here the mark effect is
    /// re-parented to the current vulnerable cell and follows it, and goes back to the root when the cells go away.
    /// </summary>
    public static class DxBoss
    {
        private static Smoke_Cell vulnerable;
        private static Boss_Smoke boss;
        private static Transform mark;
        private static int childrenBefore;

        /// <summary>Boss_Smoke.ReassignVulnerableSpot epilogue.</summary>
        public static void SetVulnerable(Smoke_Cell cell)
        {
            vulnerable = cell;
            Follow();
        }

        /// <summary>Boss_Smoke.ClearCellGroups prologue: the cells are about to be pooled.</summary>
        public static void CellsClearing(Boss_Smoke owner)
        {
            vulnerable = null;
            if (mark != null && owner != null)
            {
                mark.SetParent(owner.transform, false);
                mark.localPosition = Vector3.zero;
            }
        }

        /// <summary>EnemyBase.ApplyMark prologue.</summary>
        public static void BeforeMark(EnemyBase enemy)
        {
            if (enemy is Boss_Smoke)
            {
                childrenBefore = enemy.transform.childCount;
            }
        }

        /// <summary>EnemyBase.ApplyMark epilogue.</summary>
        public static void AfterMark(EnemyBase enemy, bool value)
        {
            Boss_Smoke smoke = enemy as Boss_Smoke;
            if (smoke == null)
            {
                return;
            }
            if (!value)
            {
                mark = null; // the game plays its own fade-out and pools the object
                return;
            }
            boss = smoke;
            if (enemy.transform.childCount > childrenBefore)
            {
                mark = enemy.transform.GetChild(enemy.transform.childCount - 1);
            }
            Follow();
        }

        private static void Follow()
        {
            if (mark == null)
            {
                return;
            }
            try
            {
                if (vulnerable != null)
                {
                    mark.SetParent(vulnerable.transform, false);
                    mark.localPosition = Vector3.zero;
                    Debug.Log("[EvertriedQoL] Mark follows the vulnerable cell");
                }
                else if (boss != null)
                {
                    mark.SetParent(boss.transform, false);
                    mark.localPosition = Vector3.zero;
                }
            }
            catch (System.Exception e)
            {
                Debug.Log("[EvertriedQoL] Could not move the boss mark: " + e.Message);
            }
        }
    }
}
