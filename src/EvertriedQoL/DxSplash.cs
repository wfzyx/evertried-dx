using System.Collections;
using UnityEngine;

namespace EvertriedQoL
{
    /// <summary>
    /// Lets the boot splash be skipped: any face button (A/B/X/Y) or Start on a gamepad, or Enter, Space,
    /// Escape or a click/tap. The splash is an animator running on unscaled time plus a realtime wait while
    /// the main menu loads, so skipping fast-forwards the animators and drops that wait.
    /// The patcher injects <see cref="Begin"/> at the end of SplashScreen.Start and wraps the read of
    /// SplashScreen.inBetweenDuration with <see cref="InBetween"/>.
    /// </summary>
    public static class DxSplash
    {
        private const float SkipSpeed = 40f;

        private static bool skipping;

        public static void Begin(SplashScreen splash)
        {
            skipping = false;
            splash.StartCoroutine(Watch(splash));
        }

        /// <summary>Value the game waits for (realtime) between loading the menu and ending the splash.</summary>
        public static float InBetween(float seconds)
        {
            return skipping ? 0f : seconds;
        }

        private static IEnumerator Watch(SplashScreen splash)
        {
            // Skip presses that started the game (e.g. holding A on the Steam launcher) do not count.
            yield return null;
            while (splash != null)
            {
                if (!skipping && SkipPressed())
                {
                    skipping = true;
                    Transform top = splash.transform;
                    while (top.parent != null)
                    {
                        top = top.parent;
                    }
                    Animator[] animators = top.GetComponentsInChildren<Animator>(true);
                    for (int i = 0; i < animators.Length; i++)
                    {
                        animators[i].speed = SkipSpeed;
                    }
                    Debug.Log("[EvertriedQoL] Splash skipped (" + animators.Length + " animators fast-forwarded)");
                }
                yield return null;
            }
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
