using UnityEngine;

namespace EvertriedQoL
{
    /// <summary>
    /// Free floors, paid by distance from the player's floor record. Winning a combat floor banks credits
    /// that depend on how far the floor is below the record; each banked credit clears the next ordinary
    /// combat floor on its own: every hostile enemy is killed through the normal damage path (gold, xp and
    /// drops are awarded as usual) and the level victory is triggered before the player gets to move.
    ///
    ///   floors below the record   credits per won floor
    ///   5 or fewer                0 (and banked credits stop being spent)
    ///   6 to 15                   1
    ///   16 to 25                  2
    ///   26 to 35                  3
    ///   36 to 45                  4
    ///   more than 45              5
    ///
    /// Only floors the player wins earn credits; a cleared floor earns nothing. Stores, NPC (special) floors,
    /// bosses, the tutorial and reloaded saved floors neither earn nor spend credits: banked credits wait for
    /// the next ordinary combat floor. The patcher injects calls to the hooks below into the game's methods.
    /// </summary>
    public static class FreeFloors
    {
        /// <summary>At this many floors (or fewer) below the record the player plays every floor.</summary>
        public const int NearRecord = 5;

        private static int credits;
        private static bool currentFloorCleared;

        /// <summary>True while a floor is being cleared: an Ekin that dies then does not arm its explosion.</summary>
        public static bool SuppressExplosions;

        public static int CreditsFor(int floorsBelowRecord)
        {
            if (floorsBelowRecord <= NearRecord)
            {
                return 0;
            }
            // 6-15 = 1, 16-25 = 2, 26-35 = 3, 36-45 = 4, more than 45 = 5: one more per ten floors.
            int count = (floorsBelowRecord - NearRecord + 9) / 10;
            return count > 5 ? 5 : count;
        }

        // The record is the highest floor index reached, saved by the game-over panel as "FloorRecord".
        private static int Record
        {
            get { return SaveHandler.GetInt("FloorRecord"); }
        }

        // LevelManager.EndCurrentLevel(bool isVictory) prologue.
        public static void OnBeforeLevelEnd(LevelManager level, bool isVictory)
        {
            if (!level.SetupDone)
            {
                return; // duplicate call, the game ignores it too
            }
            if (!isVictory)
            {
                credits = 0;
                return;
            }
            if (currentFloorCleared)
            {
                return;
            }
            // Floor 1 is typed Start but is an ordinary combat floor once the tutorial is done.
            bool combat = level.LevelType == LevelType.Default
                || (level.LevelType == LevelType.Start && !(level.Tutorial != null && level.Tutorial.Active));
            if (!combat || !ModeAllowed())
            {
                return;
            }
            int earned = CreditsFor(Record - level.LevelIndex);
            if (earned > 0)
            {
                credits += earned;
                Debug.Log("[EvertriedQoL] Floor " + (level.LevelIndex + 1) + " won, record floor " + (Record + 1)
                    + ": +" + earned + " free floor(s), banked " + credits);
            }
        }

        // LevelManager.StartLevel() epilogue: the floor is set up but the player has not acted yet.
        public static void OnAfterLevelStart(LevelManager level)
        {
            currentFloorCleared = false;
            if (level.LevelIndex == 0)
            {
                credits = 0; // a new run never inherits credits from the previous one
            }
            if (credits <= 0 || !CanClear(level))
            {
                return;
            }
            if (Record - level.LevelIndex <= NearRecord)
            {
                credits = 0; // close to the record every floor is played
                return;
            }
            credits--;
            Clear(level);
        }

        private static void SetInput(bool enabled)
        {
            GameplayEvents events = ReferenceSingleton<GameplayEvents>.Instance;
            if (events != null && events.OnInputEnable != null)
            {
                events.OnInputEnable(enabled);
            }
        }

        private static bool ModeAllowed()
        {
            GameMode mode = GameData.Instance.CurrentGameMode;
            return (mode == GameMode.Default || mode == GameMode.HardMode) && !GameData.Instance.CustomFloorLoaded;
        }

        private static bool HasBoss(EnemyBase[] enemies)
        {
            for (int i = 0; i < enemies.Length; i++)
            {
                if (enemies[i] is BossBase)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool CanClear(LevelManager level)
        {
            if (!level.SetupDone || level.LevelType != LevelType.Default || !ModeAllowed() || level.LoadingFromSavedLevel)
            {
                return false;
            }
            if (level.Tutorial != null && level.Tutorial.Active)
            {
                return false;
            }
            TurnHandler turns = TurnHandler.Instance;
            PlayerCharacter player = PlayerCharacter.Instance;
            if (turns == null || player == null || player.HealthPoints <= 0)
            {
                return false;
            }
            EnemyBase[] hostiles = turns.HostileCharacters;
            return hostiles.Length > 0 && !HasBoss(hostiles);
        }

        private static void Clear(LevelManager level)
        {
            PlayerCharacter player = PlayerCharacter.Instance;
            TurnHandler turns = TurnHandler.Instance;
            EnemyBase[] enemies = turns.HostileCharacters;
            currentFloorCleared = true;
            SetInput(false); // StartLevel just enabled it; the player must not act on a floor that is already won
            Debug.Log("[EvertriedQoL] Clearing floor " + (level.LevelIndex + 1) + ": " + enemies.Length + " enemies, "
                + credits + " free floor(s) left");

            SuppressExplosions = true;
            try
            {
                // Gold enemies pay nothing when they are the last one standing, so kill them first.
                for (int pass = 0; pass < 2; pass++)
                {
                    for (int i = 0; i < enemies.Length; i++)
                    {
                        EnemyBase enemy = enemies[i];
                        bool gold = enemy is Enemy_Gold;
                        if ((pass == 0) != gold || enemy == null || !enemy.IsHostile || enemy.HealthPoints <= 0)
                        {
                            continue;
                        }
                        enemy.ApplyDamage(enemy.HealthPoints, player, Vector2Int.zero, true);
                    }
                }
            }
            finally
            {
                SuppressExplosions = false;
            }

            RemoveConstructions(turns);
            int left = turns.HostileCharacters.Length;
            if (left > 0)
            {
                Debug.Log("[EvertriedQoL] " + left + " enemies survived the clear; ending the floor anyway");
            }
            level.TriggerLevelVictory();
        }

        // Hazards, traps and the player's own constructions do not count towards ending a floor, but they stay in
        // the scene and ride along (or hang in the air) while the next floor is raised. They are destroyed through
        // their normal death path, so their destruction animation plays like the enemies'. The player is the source,
        // like for the enemies, so the kills can give Focus and shards as usual.
        private static void RemoveConstructions(TurnHandler turns)
        {
            EnemyBase[] all = Object.FindObjectsOfType<EnemyBase>();
            int destroyed = 0;
            int forced = 0;
            for (int i = 0; i < all.Length; i++)
            {
                EnemyBase construction = all[i];
                if (construction == null || construction.IsHostile || !construction.gameObject.activeInHierarchy)
                {
                    continue;
                }
                if (construction.HealthPoints > 0)
                {
                    construction.ApplyDamage(construction.HealthPoints, PlayerCharacter.Instance, Vector2Int.zero, true);
                }
                if (!construction.Active || construction.HealthPoints <= 0)
                {
                    destroyed++;
                    continue;
                }
                // Some constructions ignore damage that is not aimed: remove those without an animation.
                construction.SetActive(false);
                turns.RemoveEnemy(construction);
                ReferenceSingleton<PoolHandler>.Instance.StoreObject(construction.gameObject);
                forced++;
            }
            if (destroyed + forced > 0)
            {
                Debug.Log("[EvertriedQoL] Cleared floor constructions: " + destroyed + " destroyed, " + forced + " removed");
            }
        }

        /// <summary>Enemy_Explosive.OnDeath prologue: true = die like a normal enemy, without arming the explosion.</summary>
        public static bool SkipExplosion()
        {
            return SuppressExplosions;
        }
    }
}
