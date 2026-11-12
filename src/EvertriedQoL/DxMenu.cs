namespace EvertriedQoL
{
    /// <summary>Guards for main-menu flows that must run once.</summary>
    public static class DxMenu
    {
        private static bool sessionStarting;
        private static bool leaving;

        /// <summary>True from the moment a run is chosen until the menu scene is left: no menu input meanwhile.</summary>
        public static bool SessionStarting
        {
            get { return sessionStarting; }
        }

        /// <summary>True while a run is being started or left (behind a fade): no panel may read input.</summary>
        public static bool InputLocked()
        {
            return sessionStarting || leaving;
        }

        /// <summary>PausePanel.ToMainMenu / GameplayCanvas.MainMenu prologue: lock panel input until the menu scene loads.</summary>
        public static void BeginLeave()
        {
            leaving = true;
        }

        /// <summary>MainMenu.Start / LevelManager.Awake: a fresh menu, a fresh chance to start a run.</summary>
        public static void Reset()
        {
            sessionStarting = false;
            leaving = false;
        }

        /// <summary>
        /// MainMenu.StartSession prologue. True for the first call only, so pressing "Ascend" again while its
        /// animation plays cannot restart the sequence or replay its sound.
        /// </summary>
        public static bool TryBeginSession()
        {
            if (sessionStarting)
            {
                return false;
            }
            sessionStarting = true;
            return true;
        }
    }
}
