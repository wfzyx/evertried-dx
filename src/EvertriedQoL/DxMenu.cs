namespace EvertriedQoL
{
    /// <summary>Guards for main-menu flows that must run once.</summary>
    public static class DxMenu
    {
        private static bool sessionStarting;

        /// <summary>MainMenu.Start: a fresh menu, a fresh chance to start a run.</summary>
        public static void Reset()
        {
            sessionStarting = false;
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
