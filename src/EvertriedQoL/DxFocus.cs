namespace EvertriedQoL
{
    /// <summary>
    /// "Disable Focus Timer", an option the Windows build has and the Linux depot lacks. When on, the
    /// Focus meter never drains (FocusMeter.TimerMultiplier becomes 0), so the combo only drops when
    /// the player is hit. The value is saved per save slot under the same key the Windows build uses
    /// (GameData.FocusTimerDisabled), so a converted Proton save keeps its setting.
    /// </summary>
    public static class DxFocus
    {
        public static bool Disabled;

        /// <summary>Stored in the slot where the Linux build kept the unused "GoldMultiplier".</summary>
        public static string Serialize()
        {
            return Disabled ? "True" : "False";
        }

        public static void Load(string value)
        {
            bool parsed;
            Disabled = value != null && bool.TryParse(value, out parsed) && parsed;
        }

        public static void Reset()
        {
            Disabled = false;
        }

        /// <summary>Wraps the result of FocusMeter.TimerMultiplier.</summary>
        public static float Multiplier(float multiplier)
        {
            return Disabled ? 0f : multiplier;
        }
    }
}
