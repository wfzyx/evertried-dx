using UnityEngine;

namespace EvertriedQoL
{
    /// <summary>
    /// The game logs its turn sequence ("---Waiting for movements---", "Action finished for X", "Time tolerance
    /// for movements elapsed", ...) only to its hidden in-game debug console. This copies those messages into
    /// Player.log so a turn that never ends shows the step it stopped at.
    /// Hook: DebugConsoleUI.Log(string, MessageLogType) prologue.
    /// </summary>
    public static class DxTurnLog
    {
        public static void Forward(string message, MessageLogType type)
        {
            if (type == MessageLogType.TrackerLog)
            {
                return;
            }
            Debug.Log("[Console " + type + "] " + message);
        }
    }
}
