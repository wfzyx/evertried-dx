extern alias core;

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;
using RealSprite = core::UnityEngine.Sprite;

namespace EvertriedQoL
{
    /// <summary>
    /// Ports the Halloween skin ("Jack-o'-Warrior", code TRICKORTREAT) from the Windows build, which the older
    /// Linux build lacks. Its art comes from <c>halloween.dxskin</c> next to this DLL, extracted from the user's
    /// own Windows install by tools/extract_skin.py (game art is never committed).
    ///
    /// The Windows skin's animations are frame-for-frame copies of the Killer skin's, so the ported skin plays
    /// Killer's animations (animation ID 4) and <see cref="Swap"/> replaces each Player_Killer_N sprite with
    /// Player_Halloween_N right after the animator writes it. Its scarf is the default scarf with an orange
    /// colour-over-lifetime gradient, applied by <see cref="Scarf"/>.
    /// Hooks: every PlayerSkinsCollection instance method (prologue), PlayerCharacterDemo.SetSkin (prologue),
    /// PlayerEffects.SetActiveScarf (argument rewrite), and a LateUpdate added to PlayerCharacter and
    /// PlayerCharacterDemo.
    /// </summary>
    public static class DxSkin
    {
        public const string FileName = "halloween.dxskin";
        public const string ScarfName = "Halloween_ScarfEffect_VFX";
        private const string DefaultScarf = "ScarfEffect_VFX";
        private const string KillerPrefix = "Player_Killer_";
        private const int KillerAnimationId = 4;

        private const BindingFlags Field = BindingFlags.Instance | BindingFlags.NonPublic;

        private static bool loadTried;
        private static PlayerSkinData skin;
        private static RealSprite[] sprites;
        private static Gradient scarfGradient;

        private static readonly Dictionary<int, bool> Collections = new Dictionary<int, bool>();
        private static readonly Dictionary<int, RealSprite> SwapCache = new Dictionary<int, RealSprite>();
        private static readonly Dictionary<int, bool> DemoWearing = new Dictionary<int, bool>();
        private static readonly Dictionary<int, ParticleSystem.MinMaxGradient> ScarfOriginal =
            new Dictionary<int, ParticleSystem.MinMaxGradient>();

        /// <summary>Prologue of every PlayerSkinsCollection instance method: append the skin once.</summary>
        public static void EnsureSkin(PlayerSkinsCollection collection)
        {
            if (collection == null)
            {
                return;
            }
            int id = collection.GetInstanceID();
            if (Collections.ContainsKey(id))
            {
                return;
            }
            Collections[id] = true; // set first: nothing below may call back into the collection

            try
            {
                if (!Load())
                {
                    return;
                }
                FieldInfo skinsField = typeof(PlayerSkinsCollection).GetField("skins", Field);
                PlayerSkinData[] skins = (PlayerSkinData[])skinsField.GetValue(collection);
                for (int i = 0; i < skins.Length; i++)
                {
                    if (skins[i] != null && skins[i].Key == skin.Key)
                    {
                        return; // the game has its own copy (a newer Linux build)
                    }
                }
                PlayerSkinData[] grown = new PlayerSkinData[skins.Length + 1];
                Array.Copy(skins, grown, skins.Length);
                grown[skins.Length] = skin;
                skinsField.SetValue(collection, grown);
                Debug.Log("[EvertriedQoL] Added skin " + skin.DisplayName + " at index " + skins.Length);
            }
            catch (Exception e)
            {
                Debug.LogError("[EvertriedQoL] Halloween skin not added: " + e);
            }
        }

        /// <summary>Prologue of PlayerCharacterDemo.SetSkin (skin selector, code unlock prompt).</summary>
        public static void OnDemoSkin(PlayerCharacterDemo demo, PlayerSkinData chosen)
        {
            if (demo != null)
            {
                DemoWearing[demo.GetInstanceID()] = skin != null && chosen == skin;
            }
        }

        /// <summary>LateUpdate added to PlayerCharacter and PlayerCharacterDemo, after the animator has run.</summary>
        public static void Swap(CharacterBase owner, SpriteRenderer render)
        {
            if (skin == null || render == null || !Wearing(owner))
            {
                return;
            }
            RealSprite current = render.sprite;
            if (current == null)
            {
                return;
            }
            int id = current.GetInstanceID();
            RealSprite replacement;
            if (!SwapCache.TryGetValue(id, out replacement))
            {
                replacement = null;
                string name = current.name;
                int index;
                if (name.StartsWith(KillerPrefix, StringComparison.Ordinal)
                    && int.TryParse(name.Substring(KillerPrefix.Length), out index)
                    && index >= 0 && index < sprites.Length)
                {
                    replacement = sprites[index];
                }
                SwapCache[id] = replacement;
            }
            if (replacement != null)
            {
                render.sprite = replacement;
                if (!swapLogged)
                {
                    swapLogged = true;
                    Debug.Log("[EvertriedQoL] Halloween skin: first frame swapped on " + owner.name + " (" + current.name + " -> " + replacement.name + ")");
                }
            }
        }

        private static bool swapLogged;

        /// <summary>
        /// Argument rewrite of PlayerEffects.SetActiveScarf: the Halloween scarf is the default scarf recoloured,
        /// so it activates the default one with the Halloween gradient; any other scarf gets the original back.
        /// </summary>
        public static string Scarf(PlayerEffects effects, string name)
        {
            bool halloween = name == ScarfName && scarfGradient != null;
            try
            {
                FieldInfo field = typeof(PlayerEffects).GetField("scarfEffects", Field);
                ParticleSystemController[] scarves = effects == null ? null : (ParticleSystemController[])field.GetValue(effects);
                if (scarves != null)
                {
                    for (int i = 0; i < scarves.Length; i++)
                    {
                        if (scarves[i] == null || scarves[i].name != DefaultScarf)
                        {
                            continue;
                        }
                        ParticleSystem[] systems = scarves[i].GetComponentsInChildren<ParticleSystem>(true);
                        for (int j = 0; j < systems.Length; j++)
                        {
                            int id = systems[j].GetInstanceID();
                            ParticleSystem.ColorOverLifetimeModule color = systems[j].colorOverLifetime;
                            if (!ScarfOriginal.ContainsKey(id))
                            {
                                if (!halloween)
                                {
                                    continue; // never recoloured: nothing to restore
                                }
                                ScarfOriginal[id] = color.color;
                            }
                            color.color = halloween ? new ParticleSystem.MinMaxGradient(scarfGradient) : ScarfOriginal[id];
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[EvertriedQoL] Halloween scarf: " + e);
            }
            return halloween ? DefaultScarf : name;
        }

        private static bool Wearing(CharacterBase owner)
        {
            PlayerCharacterDemo demo = owner as PlayerCharacterDemo;
            if (demo != null)
            {
                bool wearing;
                return DemoWearing.TryGetValue(demo.GetInstanceID(), out wearing) && wearing;
            }
            PlayerSkinsCollection collection = ScriptableSingleton<PlayerSkinsCollection>.Instance;
            return collection != null && collection.SelectedSkin == skin;
        }

        private static bool Load()
        {
            if (loadTried)
            {
                return skin != null;
            }
            loadTried = true;
            string path = Path.Combine(Path.Combine(Application.dataPath, "Managed"), FileName);
            if (!File.Exists(path))
            {
                Debug.Log("[EvertriedQoL] No " + path + ": Halloween skin not available");
                return false;
            }
            byte[] data = File.ReadAllBytes(path);
            int o = 0;
            if (Encoding.ASCII.GetString(data, 0, 7) != "DXSKIN1")
            {
                Debug.LogError("[EvertriedQoL] " + path + " is not a DXSKIN1 file");
                return false;
            }
            o = 8;
            string key = ReadString(data, ref o);
            string displayName = ReadString(data, ref o);
            int width = ReadInt(data, ref o);
            int height = ReadInt(data, ref o);
            int count = ReadInt(data, ref o);
            int[] index = new int[count];
            float[][] rects = new float[count][];
            int max = 0;
            for (int i = 0; i < count; i++)
            {
                index[i] = ReadInt(data, ref o);
                rects[i] = new float[7];
                for (int k = 0; k < 7; k++)
                {
                    rects[i][k] = ReadFloat(data, ref o);
                }
                max = Math.Max(max, index[i]);
            }
            scarfGradient = ReadGradient(data, ref o);
            Gradient oneWithAll = ReadGradient(data, ref o);
            int bytes = width * height * 4;
            if (data.Length - o != bytes)
            {
                Debug.LogError("[EvertriedQoL] " + path + ": texture data has the wrong size");
                return false;
            }
            byte[] pixels = new byte[bytes];
            Buffer.BlockCopy(data, o, pixels, 0, bytes);

            Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            texture.name = "Player_Halloween";
            texture.filterMode = FilterMode.Point;
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.LoadRawTextureData(pixels);
            texture.Apply(false, true);
            texture.hideFlags = HideFlags.HideAndDontSave;

            sprites = new RealSprite[max + 1];
            for (int i = 0; i < count; i++)
            {
                float[] r = rects[i];
                Rect rect = new Rect(r[0], r[1], r[2], r[3]);
                Vector2 pivot = new Vector2(r[4], r[5]);
                Vector4 border = Vector4.zero;
                RealSprite sprite = (RealSprite)UnityEngine.Sprite.CreateSprite_Injected(texture, ref rect, ref pivot, r[6], 0u, 0, ref border, false);
                sprite.name = "Player_Halloween_" + index[i];
                sprite.hideFlags = HideFlags.HideAndDontSave;
                sprites[index[i]] = sprite;
            }

            // PlayerSkinData.ScarfObjectName reads the name of a GameObject; this one only carries the name.
            GameObject scarfHolder = new GameObject(ScarfName);
            scarfHolder.SetActive(false);
            scarfHolder.hideFlags = HideFlags.HideAndDontSave;

            PlayerSkinData created = ScriptableObject.CreateInstance<PlayerSkinData>();
            created.name = "PlayerSkin_Halloween";
            created.hideFlags = HideFlags.HideAndDontSave;
            Type t = typeof(PlayerSkinData);
            t.GetField("key", Field).SetValue(created, key);
            t.GetField("displayName", Field).SetValue(created, displayName);
            t.GetField("animationID", Field).SetValue(created, KillerAnimationId);
            t.GetField("scarfObject", Field).SetValue(created, scarfHolder);
            t.GetField("oneWithAllGradient", Field).SetValue(created, oneWithAll);
            skin = created;
            Debug.Log("[EvertriedQoL] Loaded Halloween skin: " + count + " sprites, " + width + "x" + height);
            return true;
        }

        private static int ReadInt(byte[] b, ref int o)
        {
            int v = BitConverter.ToInt32(b, o);
            o += 4;
            return v;
        }

        private static readonly float[] FloatBuffer = new float[1];

        private static float ReadFloat(byte[] b, ref int o)
        {
            Buffer.BlockCopy(b, o, FloatBuffer, 0, 4); // BitConverter.ToSingle is stripped
            o += 4;
            return FloatBuffer[0];
        }

        private static string ReadString(byte[] b, ref int o)
        {
            int n = ReadInt(b, ref o);
            string s = Encoding.UTF8.GetString(b, o, n);
            o += n;
            return s;
        }

        private static Gradient ReadGradient(byte[] b, ref int o)
        {
            int mode = ReadInt(b, ref o);
            GradientColorKey[] colors = new GradientColorKey[ReadInt(b, ref o)];
            for (int i = 0; i < colors.Length; i++)
            {
                float r = ReadFloat(b, ref o);
                float g = ReadFloat(b, ref o);
                float bl = ReadFloat(b, ref o);
                colors[i].color = new Color(r, g, bl, 1f); // the stripped build has no key constructors
                colors[i].time = ReadFloat(b, ref o);
            }
            GradientAlphaKey[] alphas = new GradientAlphaKey[ReadInt(b, ref o)];
            for (int i = 0; i < alphas.Length; i++)
            {
                alphas[i].alpha = ReadFloat(b, ref o);
                alphas[i].time = ReadFloat(b, ref o);
            }
            Gradient gradient = new Gradient();
            gradient.SetKeys(colors, alphas);
            if (mode != 0)
            {
                // Gradient.mode is stripped; both Halloween gradients use the default (Blend, 0).
                Debug.LogWarning("[EvertriedQoL] gradient mode " + mode + " not supported, using Blend");
            }
            return gradient;
        }
    }
}
