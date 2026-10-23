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
    /// shows a placeholder. An entry remembers the file's write time and
    /// length: a file rewritten in place (a re-bake overwriting its
    /// _ao.png) is decoded again instead of showing the old picture.
    /// </summary>
    public static class ImageThumbnailCache
    {
        public const int Capacity = 32;

        private static readonly Dictionary<string, Texture2D> _textures = new Dictionary<string, Texture2D>();
        private static readonly Dictionary<string, (System.DateTime written, long length)> _stamps =
            new Dictionary<string, (System.DateTime, long)>();
        private static readonly LinkedList<string> _order = new LinkedList<string>();

        public static Texture2D Get(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }
            if (!File.Exists(path))
            {
                return null;
            }
            var info = new FileInfo(path);
            var stamp = (info.LastWriteTimeUtc, info.Length);
            Texture2D cached;
            if (_textures.TryGetValue(path, out cached))
            {
                (System.DateTime, long) seen;
                if (cached != null)
                {
                    if (!_stamps.TryGetValue(path, out seen) || seen != stamp)
                    {
                        // decode into the same texture, so the blocks already showing it follow too
                        byte[] fresh;
                        try
                        {
                            fresh = File.ReadAllBytes(path);
                        }
                        catch (System.Exception)
                        {
                            fresh = null;
                        }
                        if (fresh != null && ImageConversion.LoadImage(cached, fresh, false))
                        {
                            _stamps[path] = stamp;
                        }
                    }
                    _order.Remove(path);
                    _order.AddLast(path);
                    return cached;
                }
                Invalidate(path);
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
                    _stamps.Remove(oldest);
                    if (evicted != null)
                    {
                        Object.DestroyImmediate(evicted);
                    }
                }
            }
            _textures[path] = texture;
            _stamps[path] = stamp;
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
                _stamps.Remove(path);
                _order.Remove(path);
                if (texture != null)
                {
                    Object.DestroyImmediate(texture);
                }
            }
        }
    }
}
