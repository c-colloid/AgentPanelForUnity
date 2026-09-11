using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Colloid.AgentPanel.UI
{
    /// <summary>
    /// Textures for the transcript's image blocks and the composer strip
    /// (design note 2026-09-07 section 2.3.5): a small LRU of decoded
    /// attachment files, HideAndDontSave so a domain reload simply
    /// rebuilds it. Missing or undecodable files yield null and the caller
    /// shows a placeholder.
    /// </summary>
    public static class ImageThumbnailCache
    {
        public const int Capacity = 32;

        private static readonly Dictionary<string, Texture2D> _textures = new Dictionary<string, Texture2D>();
        private static readonly LinkedList<string> _order = new LinkedList<string>();

        public static Texture2D Get(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }
            Texture2D cached;
            if (_textures.TryGetValue(path, out cached))
            {
                if (cached != null)
                {
                    _order.Remove(path);
                    _order.AddLast(path);
                    return cached;
                }
                _textures.Remove(path);
                _order.Remove(path);
            }
            if (!File.Exists(path))
            {
                return null;
            }
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (System.Exception)
            {
                return null;
            }
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            if (!ImageConversion.LoadImage(texture, bytes, false))
            {
                Object.DestroyImmediate(texture);
                return null;
            }
            while (_order.Count >= Capacity)
            {
                string oldest = _order.First.Value;
                _order.RemoveFirst();
                Texture2D evicted;
                if (_textures.TryGetValue(oldest, out evicted))
                {
                    _textures.Remove(oldest);
                    if (evicted != null)
                    {
                        Object.DestroyImmediate(evicted);
                    }
                }
            }
            _textures[path] = texture;
            _order.AddLast(path);
            return texture;
        }

        /// <summary>Forgets one path (after its file was deleted) so the next Get re-reads.</summary>
        public static void Invalidate(string path)
        {
            Texture2D texture;
            if (!string.IsNullOrEmpty(path) && _textures.TryGetValue(path, out texture))
            {
                _textures.Remove(path);
                _order.Remove(path);
                if (texture != null)
                {
                    Object.DestroyImmediate(texture);
                }
            }
        }
    }
}
