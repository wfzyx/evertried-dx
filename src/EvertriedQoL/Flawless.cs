using System.Collections;
using UnityEngine;

namespace EvertriedQoL
{
    /// <summary>
    /// Finish a combat floor without taking damage and the next combat floor clears itself:
    /// every hostile enemy is killed through the normal damage path (so gold, xp and drops are
    /// awarded as usual) and the level victory is then triggered.
    /// An auto-cleared floor never earns the bonus, so floors alternate at best.
    /// The patcher injects calls to the three hooks below into the game's methods.
    /// </summary>
    public static class Flawless
    {
        /// <summary>Seconds to wait after the floor starts before it is auto-cleared.</summary>
        public const float AutoClearDelay = 1.5f;

        /// <summary>Only earn the bonus on floors up to this number (0 = no limit).</summary>
        public const int MaxFloor = 0;

        private static bool tookDamage;
        private static bool currentFloorAutoCleared;
        private static bool pending;
        private static int epoch;

        // PlayerStatus.ApplyDamage(int amount, CharacterBase source, ...) epilogue.
        // Dodged and shielded hits return false and do not count.
        public static void OnPlayerDamage(bool applied, int amount, CharacterBase source)
        {
            if (applied && amount > 0 && !(source is PlayerCharacter))
            {
                tookDamage = true;
            }
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
                pending = false;
                return;
            }
            if (tookDamage || currentFloorAutoCleared)
            {
                return;
            }
            if (level.LevelType != LevelType.Default || !ModeAllowed())
            {
                return;
            }
            if (MaxFloor > 0 && level.LevelIndex + 1 > MaxFloor)
            {
                return;
            }
            pending = true;
            Debug.Log("[EvertriedQoL] Flawless floor " + (level.LevelIndex + 1) + ": next combat floor will auto-clear");
        }

        // LevelManager.StartLevel() epilogue.
        public static void OnAfterLevelStart(LevelManager level)
        {
            epoch++;
            tookDamage = false;
            currentFloorAutoCleared = false;
            if (!pending || !CanAutoClear(level))
            {
                return;
            }
            pending = false;
            currentFloorAutoCleared = true;
            SetInput(false); // the player must not act while the floor is being cleared
            level.StartCoroutine(AutoClear(level, level.LevelIndex, epoch));
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

        private static bool CanAutoClear(LevelManager level)
        {
            if (level.LevelType != LevelType.Default || !ModeAllowed() || level.LoadingFromSavedLevel)
            {
                return false;
            }
            if (level.Tutorial != null && level.Tutorial.Active)
            {
                return false;
            }
            TurnHandler turns = TurnHandler.Instance;
            if (turns == null)
            {
                return false;
            }
            EnemyBase[] hostiles = turns.HostileCharacters;
            return hostiles.Length > 0 && !HasBoss(hostiles);
        }

        private static IEnumerator AutoClear(LevelManager level, int floorIndex, int startEpoch)
        {
            yield return new WaitForSeconds(AutoClearDelay);

            float waited = 0f;
            while ((level.InTransition || Time.timeScale <= 0f) && waited < 10f)
            {
                waited += Time.unscaledDeltaTime;
                yield return null;
            }

            PlayerCharacter player = PlayerCharacter.Instance;
            TurnHandler turns = TurnHandler.Instance;
            if (epoch != startEpoch)
            {
                yield break; // a new level already started and owns the input state
            }
            if (!level.SetupDone || player == null || turns == null || player.HealthPoints <= 0)
            {
                SetInput(true);
                yield break;
            }

            EnemyBase[] enemies = turns.HostileCharacters;
            if (HasBoss(enemies))
            {
                SetInput(true);
                yield break;
            }

            Debug.Log("[EvertriedQoL] Auto-clearing floor " + (floorIndex + 1) + ": " + enemies.Length + " enemies");

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

            waited = 0f;
            while (turns.HostileCharacters.Length > 0 && waited < 5f)
            {
                waited += Time.unscaledDeltaTime;
                yield return null;
            }

            // Only end the level if nothing else already did (game turn check, death, ...).
            if (epoch == startEpoch && level.SetupDone && player.HealthPoints > 0)
            {
                level.TriggerLevelVictory();
            }
            else if (epoch == startEpoch && player.HealthPoints > 0)
            {
                SetInput(true);
            }
        }
    }
}
