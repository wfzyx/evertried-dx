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
            if (Time.unscaledTime < next)
            {
                return;
            }
            next = Time.unscaledTime + 0.5f;
            if (!File.Exists(Flag))
            {
                return;
            }

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
                    .Append(" target=").Append(cam.targetTexture != null ? "rt" : "screen")
                    .Append(" clear=").Append(cam.clearFlags).Append(" mask=").Append(cam.cullingMask);
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
