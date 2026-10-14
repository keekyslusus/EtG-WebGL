#pragma warning disable 0436 // MovieTexture is WebShim's stub (see UnityStubs.cs)

using UnityEngine;
using UnityEngine.Video;

namespace WebShim
{
    public static class MediaShim
    {
        /// <summary>Replacement for WWW.GetMovieTexture(), which doesn't exist in the WebGL player.</summary>
        public static MovieTexture GetMovieTexture(WWW www)
        {
            return null;
        }

        /// <summary>
        /// Replacement for `videoPlayer.clip = clip` in mod code. The WebGL VideoPlayer only plays URLs (the browser's
        /// video element decodes them); a mod's clips (Expand the Gungeon's VHS and old-film overlays) are shipped as
        /// StreamingAssets/mods/video/&lt;clip name&gt;.mp4 by port/mods/mod_bundles.py.
        /// </summary>
        public static void SetVideoClip(VideoPlayer player, VideoClip clip)
        {
            if (Application.platform != RuntimePlatform.WebGLPlayer || clip == null)
            {
                player.clip = clip;
                return;
            }
            player.source = VideoSource.Url;
            player.url = Application.streamingAssetsPath + "/mods/video/" + clip.name + ".mp4";
        }

        /// <summary>
        /// Application.platform for code that only does something on Windows that works here as well (the
        /// patcher's WindowsPlatformChecks): WindowsPlayer instead of WebGLPlayer, as in the Steam game.
        /// </summary>
        public static RuntimePlatform PlatformAsWindows()
        {
            RuntimePlatform platform = Application.platform;
            return platform == RuntimePlatform.WebGLPlayer ? RuntimePlatform.WindowsPlayer : platform;
        }
    }
}
