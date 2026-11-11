namespace EvertriedQoL
{
    /// <summary>
    /// The game scales its low-resolution render texture to the screen with the "Hidden/BilinearSharp" shader
    /// (PixelArtCamera.useUpscaleShader). On the native Linux build under gamescope that blit starts
    /// producing a black world after a level starts or the game is unpaused (HUD and everything else
    /// stay fine). Live experiments showed that the game's other path, an integer point-filtered upscale
    /// followed by a plain stretch, never goes black and gives crisper pixels, so it is used instead.
    /// The patcher calls <see cref="Fix"/> at the end of PixelArtCamera.Start.
    /// </summary>
    public static class DxPixel
    {
        public static void Fix(PixelArtCamera camera)
        {
            camera.useUpscaleShader = false;
        }
    }
}
