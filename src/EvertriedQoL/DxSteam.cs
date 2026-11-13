using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace EvertriedQoL
{
    /// <summary>
    /// Reports the game's achievements to Steam. The Linux depot ships no Steamworks, so nothing the game
    /// unlocks ever reaches the Steam account. This loads libsteam_api.so from wherever one can be found
    /// (a copy in the game's Plugins folder, or the one inside Proton or another installed game), starts the
    /// API for the Steam client that launched the game, and mirrors the game's own list of unlocked
    /// achievements (PlayerPrefs "AcheivementsUnlocked") to Steam. The game's keys are the same strings as
    /// Steam's API names. Nothing happens when the game was not started from Steam.
    /// </summary>
    internal static class DxSteam
    {
        private const string SteamLib = "libsteam_api.so";
        private const string Libc = "libc.so.6";
        private const string UnlockedKey = "AcheivementsUnlocked";

        [DllImport(Libc)]
        private static extern IntPtr dlopen(string file, int mode);

        [DllImport(SteamLib)]
        private static extern byte SteamAPI_Init();

        [DllImport(SteamLib)]
        private static extern IntPtr SteamClient();

        [DllImport(SteamLib)]
        private static extern int SteamAPI_GetHSteamUser();

        [DllImport(SteamLib)]
        private static extern int SteamAPI_GetHSteamPipe();

        [DllImport(SteamLib)]
        private static extern void SteamAPI_RunCallbacks();

        [DllImport(SteamLib)]
        private static extern IntPtr SteamAPI_ISteamClient_GetISteamUserStats(IntPtr client, int user, int pipe, string version);

        [DllImport(SteamLib)]
        private static extern byte SteamAPI_ISteamUserStats_RequestCurrentStats(IntPtr stats);

        [DllImport(SteamLib)]
        private static extern uint SteamAPI_ISteamUserStats_GetNumAchievements(IntPtr stats);

        [DllImport(SteamLib)]
        private static extern byte SteamAPI_ISteamUserStats_GetAchievement(IntPtr stats, string name, out byte achieved);

        [DllImport(SteamLib)]
        private static extern byte SteamAPI_ISteamUserStats_SetAchievement(IntPtr stats, string name);

        [DllImport(SteamLib)]
        private static extern byte SteamAPI_ISteamUserStats_StoreStats(IntPtr stats);

        private const int RtldNowGlobal = 0x2 | 0x100;
        private const float FirstTry = 3f;

        private enum State { Waiting, Connecting, Ready, Off }

        private static State state = State.Waiting;
        private static IntPtr stats = IntPtr.Zero;
        private static float next = FirstTry;
        private static float giveUpAt;
        private static string lastSeen;
        private static readonly HashSet<string> handled = new HashSet<string>();

        /// <summary>Called every frame from the host component; does its own pacing.</summary>
        internal static void Tick()
        {
            if (state == State.Off || Time.unscaledTime < next)
            {
                return;
            }
            next = Time.unscaledTime + 1f;
            try
            {
                switch (state)
                {
                    case State.Waiting:
                        Connect();
                        break;
                    case State.Connecting:
                        SteamAPI_RunCallbacks();
                        if (SteamAPI_ISteamUserStats_GetNumAchievements(stats) > 0)
                        {
                            state = State.Ready;
                            Debug.Log("[EvertriedQoL] Steam stats ready (" + SteamAPI_ISteamUserStats_GetNumAchievements(stats) + " achievements)");
                            Sync();
                        }
                        else if (Time.unscaledTime > giveUpAt)
                        {
                            Debug.Log("[EvertriedQoL] Steam never delivered the stats; achievements will not be reported");
                            Shutdown();
                        }
                        break;
                    case State.Ready:
                        SteamAPI_RunCallbacks();
                        Sync();
                        break;
                }
            }
            catch (Exception e)
            {
                Debug.Log("[EvertriedQoL] Steam reporting stopped: " + e.GetType().Name + " " + e.Message);
                state = State.Off;
            }
        }

        private static void Connect()
        {
            if (!LoadLibrary())
            {
                Debug.Log("[EvertriedQoL] No libsteam_api.so found; achievements will not be reported to Steam. Copy one into Evertried_Release_Data/Plugins to enable it.");
                state = State.Off;
                return;
            }
            if (SteamAPI_Init() == 0)
            {
                Debug.Log("[EvertriedQoL] SteamAPI_Init failed (is Steam running and was the game started from it?); achievements will not be reported");
                state = State.Off;
                return;
            }
            IntPtr client = SteamClient();
            stats = SteamAPI_ISteamClient_GetISteamUserStats(client, SteamAPI_GetHSteamUser(), SteamAPI_GetHSteamPipe(), "STEAMUSERSTATS_INTERFACE_VERSION012");
            if (client == IntPtr.Zero || stats == IntPtr.Zero)
            {
                Debug.Log("[EvertriedQoL] Steam user stats interface unavailable; achievements will not be reported");
                state = State.Off;
                return;
            }
            SteamAPI_ISteamUserStats_RequestCurrentStats(stats);
            giveUpAt = Time.unscaledTime + 30f;
            state = State.Connecting;
        }

        private static void Shutdown()
        {
            state = State.Off;
        }

        private static void Sync()
        {
            string raw = PlayerPrefs.GetString(UnlockedKey, "");
            if (raw == lastSeen)
            {
                return;
            }
            lastSeen = raw;
            string[] keys = raw.Split('|');
            bool changed = false;
            for (int i = 0; i < keys.Length; i++)
            {
                string key = keys[i];
                if (key.Length == 0 || !handled.Add(key))
                {
                    continue;
                }
                byte achieved;
                if (SteamAPI_ISteamUserStats_GetAchievement(stats, key, out achieved) == 0)
                {
                    Debug.Log("[EvertriedQoL] Steam has no achievement named '" + key + "'");
                    continue;
                }
                if (achieved != 0)
                {
                    continue;
                }
                if (SteamAPI_ISteamUserStats_SetAchievement(stats, key) != 0)
                {
                    Debug.Log("[EvertriedQoL] Steam achievement unlocked: " + key);
                    changed = true;
                }
                else
                {
                    Debug.Log("[EvertriedQoL] Steam refused achievement '" + key + "'");
                }
            }
            if (changed)
            {
                SteamAPI_ISteamUserStats_StoreStats(stats);
            }
        }

        private static bool LoadLibrary()
        {
            List<string> candidates = new List<string>();
            string data = Application.dataPath;
            candidates.Add(data + "/Plugins/" + SteamLib);
            candidates.Add(data + "/Plugins/x86_64/" + SteamLib);
            string common = Up(Up(data));
            if (common != null && Directory.Exists(common))
            {
                string[] folders = Directory.GetDirectories(common);
                for (int i = 0; i < folders.Length; i++)
                {
                    candidates.Add(folders[i] + "/files/lib64/" + SteamLib);
                }
                for (int i = 0; i < folders.Length; i++)
                {
                    candidates.Add(folders[i] + "/" + SteamLib);
                    candidates.Add(folders[i] + "/linux64/" + SteamLib);
                    candidates.Add(folders[i] + "/lib64/" + SteamLib);
                }
            }
            for (int i = 0; i < candidates.Count; i++)
            {
                if (!File.Exists(candidates[i]))
                {
                    continue;
                }
                // The bare name used by the imports then resolves to this already loaded library.
                if (dlopen(candidates[i], RtldNowGlobal) != IntPtr.Zero)
                {
                    Debug.Log("[EvertriedQoL] Steam API library: " + candidates[i]);
                    return true;
                }
            }
            return false;
        }

        private static string Up(string path)
        {
            int cut = path == null ? -1 : path.LastIndexOf('/');
            return cut > 0 ? path.Substring(0, cut) : null;
        }
    }
}
