#pragma warning disable 0436 // MovieTexture is WebShim's stub (see UnityStubs.cs)

using UnityEngine;

namespace WebShim
{
    /// <summary>Replacement for WWW.GetMovieTexture(), which doesn't exist in the WebGL player.</summary>
    public static class MediaShim
    {
        public static MovieTexture GetMovieTexture(WWW www)
        {
            return null;
        }
    }
}
