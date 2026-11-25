using System.Runtime.CompilerServices;

namespace UnityEngine
{
    /// <summary>
    /// The Linux build's stripped UnityEngine.CoreModule has no <c>Sprite.Create</c>, but the engine
    /// (UnityPlayer.so) still registers the internal call behind it. Mono binds internal calls by type and
    /// method name, so declaring the same name here reaches it. Signature from Unity 2019.4 (the Windows
    /// build's IL2CPP dump): meshType is the SpriteMeshType enum (0 = FullRect), also stripped, so int here.
    /// The return value is a real <c>UnityEngine.Sprite</c>; callers cast it.
    /// This type shadows UnityEngine.Sprite inside this assembly (warning CS0436); code that needs the real
    /// type uses the <c>core</c> extern alias.
    /// </summary>
    internal static class Sprite
    {
        [MethodImpl(MethodImplOptions.InternalCall)]
        internal static extern Object CreateSprite_Injected(Texture2D texture, ref Rect rect, ref Vector2 pivot,
            float pixelsPerUnit, uint extrude, int meshType, ref Vector4 border, bool generateFallbackPhysicsShape);
    }
}
