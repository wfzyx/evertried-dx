using System.Collections;
using UnityEngine;

namespace EvertriedQoL
{
    /// <summary>
    /// Boot splash skip. One press (a pad face button, Start, Enter, Space, Escape or a click) jumps the splash
    /// animation to the start of the next fade-out, so each press skips exactly the part currently on screen.
    /// The animation is SplashScreen_Main (12.58 s): a Dangen sting that ends at 5.0 s, then three logos that
    /// fade out at 6.583, 9.083 and 11.583 s, then the TriggerEnd event at 12.083 s. Presses during the end
    /// animation or while the menu loads do nothing.
    /// The patcher calls <see cref="Begin"/> at the end of SplashScreen.Start.
    /// </summary>
    public static class DxSplash
    {
        // Times (seconds into the Main clip) a press can jump to: the end of the Dangen sting and the start of
        // each logo's fade-out. The fade itself then plays at normal speed.
        private static readonly float[] Targets = { 5.0f, 6.583f, 9.083f, 11.583f };

        // Presses count only while something is held on screen: the Dangen sting and each logo between its
        // fade-in and fade-out. Fades, the gaps between logos and fast-forwarding ignore input.
        private static readonly float[] WindowStart = { 0.6f, 5.583f, 8.083f, 10.583f };
        private static readonly float[] WindowEnd = { 5.0f, 6.583f, 9.083f, 11.583f };

        // The End clip (the fade that reveals the title screen) is 2.0 s long; its Dispose event fires at its start.
        private const float EndFadeSeconds = 2.25f;
        private static float unblockAt;
        private const float Margin = 0.05f;
        private const float MaxSpeed = 40f;

        public static void Begin(SplashScreen splash)
        {
            splash.StartCoroutine(Watch(splash));
        }

        // The stripped UnityEngine in the game has no Animator.Play or normalizedTime, only Animator.speed. So
        // the clip position is tracked here (the splash animator runs on unscaled time at speed 1) and a skip
        // raises the speed just enough to land on the target time.
        private static IEnumerator Watch(SplashScreen splash)
        {
            yield return null; // presses that started the game (e.g. A held on the launcher) must not count
            Animator[] found = splash.GetComponentsInChildren<Animator>(true);
            Animator animator = found.Length > 0 ? found[0] : null;
            if (animator == null)
            {
                yield break;
            }
            float clip = 0f;
            float target = -1f;
            bool wasEnabled = false;
            while (splash != null && animator != null)
            {
                if (animator.enabled && !wasEnabled)
                {
                    clip = 0f;
                    target = -1f;
                }
                if (!animator.enabled && wasEnabled)
                {
                    break; // the game restarts the animator for its End state: the Main clip is over
                }
                wasEnabled = animator.enabled;

                if (animator.enabled)
                {
                    float dt = Time.unscaledDeltaTime;
                    if (target < 0f && InWindow(clip) && SkipPressed())
                    {
                        float from = target > 0f ? target : clip;
                        for (int i = 0; i < Targets.Length; i++)
                        {
                            if (Targets[i] > from + Margin)
                            {
                                target = Targets[i];
                                Debug.Log("[EvertriedQoL] Splash skip: " + clip.ToString("0.00") + "s -> " + target.ToString("0.000") + "s");
                                break;
                            }
                        }
                    }

                    float speed = 1f;
                    if (target > 0f && dt > 0f)
                    {
                        float remaining = target - clip;
                        if (remaining <= 0f)
                        {
                            target = -1f;
                        }
                        else
                        {
                            speed = Mathf.Min(MaxSpeed, Mathf.Max(1f, remaining / dt));
                        }
                    }
                    animator.speed = speed;
                    clip += dt * speed;
                    if (clip > 12.2f)
                    {
                        break;
                    }
                }
                yield return null;
            }
            if (animator != null)
            {
                animator.speed = 1f;
            }
        }

        private static bool InWindow(float clip)
        {
            for (int i = 0; i < WindowStart.Length; i++)
            {
                if (clip >= WindowStart[i] && clip < WindowEnd[i] - Margin)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>SplashScreen.Dispose prologue: the title screen starts fading in now.</summary>
        public static void OnDispose()
        {
            unblockAt = Time.realtimeSinceStartup + EndFadeSeconds;
        }

        /// <summary>MainMenu.ActiveUpdate prologue: no menu input until the title screen has finished fading in.</summary>
        public static bool InputBlocked()
        {
            return Time.realtimeSinceStartup < unblockAt;
        }

        private static bool SkipPressed()
        {
            const ControllerInputs.ButtonState pressed = ControllerInputs.ButtonState.Pressed;
            return DxPad.Button(0, pressed) || DxPad.Button(1, pressed) || DxPad.Button(2, pressed)
                || DxPad.Button(3, pressed) || DxPad.Button(7, pressed)
                || Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)
                || Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.Escape)
                || Input.GetMouseButtonDown(0);
        }
    }
}
