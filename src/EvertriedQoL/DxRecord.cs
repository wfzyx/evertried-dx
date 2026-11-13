namespace EvertriedQoL
{
    /// <summary>
    /// The game keeps one floor record ("FloorRecord") for every mode. Hard mode gets its own, so a record set in
    /// normal mode does not hand out free floors (see <see cref="FreeFloors"/>) at the start of a hard run.
    /// The patcher replaces the key the game over panel reads and writes with <see cref="Key"/>.
    /// </summary>
    public static class DxRecord
    {
        public static string Key()
        {
            bool hard = GameData.Instance != null && GameData.Instance.CurrentGameMode == GameMode.HardMode;
            return hard ? "FloorRecordHard" : "FloorRecord";
        }
    }
}
