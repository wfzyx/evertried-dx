using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

namespace EvertriedQoL
{
    /// <summary>
    /// Debug aid, active only while the file /tmp/evertried-dx-debug exists. Twice a second it looks for
    /// UI elements that cover most of the screen and are dark and visible, and for camera state, and logs a
    /// line to Player.log whenever that picture changes. Used to find out what the "black panel" is.
    /// </summary>
    public sealed class DxHost : MonoBehaviour
    {
        private const string Flag = "/tmp/evertried-dx-debug";
        private const string ExperimentFile = "/tmp/evertried-dx-exp";
        private string lastExperiment = "";
        private static DxHost instance;
        private float next;
        private string last = "";

        internal static void EnsureCreated()
        {
            if (instance != null)
            {
                return;
            }
            GameObject go = new GameObject("EvertriedQoL Host");
            DontDestroyOnLoad(go);
            instance = go.AddComponent<DxHost>();
        }

        private void Update()
        {
            DxSteam.Tick();
            if (Time.unscaledTime < next)
            {
                return;
            }
            next = Time.unscaledTime + 0.5f;
            if (!File.Exists(Flag))
            {
                return;
            }

            RunExperiments();

            StringBuilder report = new StringBuilder();
            report.Append("timeScale=").Append(Time.timeScale);

            Graphic[] graphics = FindObjectsOfType<Graphic>();
            for (int i = 0; i < graphics.Length; i++)
            {
                Graphic g = graphics[i];
                if (!g.isActiveAndEnabled)
                {
                    continue;
                }
                Color c = g.color;
                float alpha = c.a;
                CanvasGroup[] groups = g.GetComponentsInParent<CanvasGroup>();
                for (int k = 0; k < groups.Length; k++)
                {
                    alpha *= groups[k].alpha;
                }
                if (alpha < 0.1f || c.r + c.g + c.b > 0.9f)
                {
                    continue; // invisible or not dark
                }
                Canvas root = g.canvas != null ? g.canvas.rootCanvas : null;
                if (root == null)
                {
                    continue;
                }
                RectTransform rootRect = (RectTransform)root.transform;
                Vector3 scale = g.rectTransform.lossyScale;
                float w = g.rectTransform.rect.width * scale.x / (rootRect.rect.width * root.transform.lossyScale.x);
                float h = g.rectTransform.rect.height * scale.y / (rootRect.rect.height * root.transform.lossyScale.y);
                if (w < 0.9f || h < 0.9f)
                {
                    continue;
                }
                report.Append("\n  PANEL ").Append(Path(g.transform)).Append(" type=").Append(g.GetType().Name)
                    .Append(" color=").Append(c).Append(" effectiveAlpha=").Append(alpha.ToString("0.00"))
                    .Append(" size=").Append(w.ToString("0.00")).Append("x").Append(h.ToString("0.00"));
            }

            Camera[] cameras = FindObjectsOfType<Camera>();
            for (int i = 0; i < cameras.Length; i++)
            {
                Camera cam = cameras[i];
                report.Append("\n  CAMERA ").Append(cam.name).Append(" depth=").Append(cam.depth)
                    .Append(" target=").Append(cam.targetTexture != null ? cam.targetTexture.width + "x" + cam.targetTexture.height : "screen")
                    .Append(" clear=").Append(cam.clearFlags)
                    .Append(" mask=").Append(cam.cullingMask)
                    .Append(" size=").Append(cam.orthographicSize.ToString("0.000"));
                Component[] parts = cam.GetComponents<Component>();
                report.Append(" parts=");
                for (int k = 0; k < parts.Length; k++)
                {
                    report.Append(parts[k] == null ? "null" : parts[k].GetType().Name).Append(k + 1 < parts.Length ? "," : "");
                }
            }
            PixelArtCamera[] pixel = FindObjectsOfType<PixelArtCamera>();
            for (int i = 0; i < pixel.Length; i++)
            {
                PixelArtCamera p = pixel[i];
                report.Append("\n  PIXELCAM ").Append(Path(p.transform)).Append(" enabled=").Append(p.enabled)
                    .Append(" internal=").Append(p.internalResolution).Append(" screen=").Append(p.screenResolution)
                    .Append(" real=").Append(Screen.width).Append("x").Append(Screen.height)
                    .Append(" smooth=").Append(p.smooth).Append(" upscaleShader=").Append(p.useUpscaleShader)
                    .Append(" material=").Append(p.upscaleMaterial != null ? p.upscaleMaterial.name : "none");
            }
            BoxBlur[] blurs = FindObjectsOfType<BoxBlur>();
            for (int i = 0; i < blurs.Length; i++)
            {
                report.Append("\n  BLUR ").Append(Path(blurs[i].transform)).Append(" enabled=").Append(blurs[i].enabled);
            }

            string text = report.ToString();
            if (text != last)
            {
                last = text;
                Debug.Log("[EvertriedQoL][diag] " + text);
            }
        }

        /// <summary>
        /// Live experiments for the black-world bug, read from /tmp/evertried-dx-exp. "noblur" keeps every BoxBlur
        /// disabled; the other words run once each time the file content changes: "resetrt" (rebuild the pixel
        /// camera render texture), "camtoggle" (turn the game camera off and on), "nopixel" (stop the pixel
        /// camera and render straight to the screen), "pixelon" (undo nopixel), "smooth" / "noupscale" / "restore"
        /// (pick the pixel camera scaling path), "reresolve" (re-apply the screen size).
        /// </summary>
        private void RunExperiments()
        {
            string wanted = "";
            try
            {
                if (File.Exists(ExperimentFile))
                {
                    wanted = File.ReadAllText(ExperimentFile);
                }
            }
            catch (System.Exception)
            {
            }
            if (wanted.Contains("noblur"))
            {
                BoxBlur[] blurs = FindObjectsOfType<BoxBlur>();
                for (int i = 0; i < blurs.Length; i++)
                {
                    blurs[i].enabled = false;
                }
            }
            if (wanted == lastExperiment)
            {
                return;
            }
            lastExperiment = wanted;
            PixelArtCamera[] pixel = FindObjectsOfType<PixelArtCamera>();
            if (wanted.Contains("resetrt"))
            {
                for (int i = 0; i < pixel.Length; i++)
                {
                    pixel[i].SetupRenderTexture();
                }
            }
            if (wanted.Contains("camtoggle"))
            {
                for (int i = 0; i < pixel.Length; i++)
                {
                    if (pixel[i].mainCamera != null)
                    {
                        pixel[i].mainCamera.enabled = false;
                        pixel[i].mainCamera.enabled = true;
                    }
                }
            }
            if (wanted.Contains("nopixel"))
            {
                for (int i = 0; i < pixel.Length; i++)
                {
                    pixel[i].enabled = false;
                    if (pixel[i].mainCamera != null)
                    {
                        pixel[i].mainCamera.targetTexture = null;
                    }
                }
            }
            if (wanted.Contains("smooth") || wanted.Contains("noupscale") || wanted.Contains("restore"))
            {
                for (int i = 0; i < pixel.Length; i++)
                {
                    pixel[i].smooth = wanted.Contains("smooth");
                    pixel[i].useUpscaleShader = !wanted.Contains("noupscale");
                }
            }
            if (wanted.Contains("reresolve"))
            {
                Screen.SetResolution(Screen.width, Screen.height, true);
            }
            if (wanted.Contains("pixelon"))
            {
                for (int i = 0; i < pixel.Length; i++)
                {
                    pixel[i].enabled = true;
                }
            }
            Debug.Log("[EvertriedQoL][exp] applied '" + wanted.Trim() + "'");
        }

        private static string Path(Transform t)
        {
            StringBuilder path = new StringBuilder(t.name);
            for (Transform p = t.parent; p != null; p = p.parent)
            {
                path.Insert(0, p.name + "/");
            }
            return path.ToString();
        }
    }
}
