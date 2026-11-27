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

        /// <summary>
        /// The decoded texture for <paramref name="path"/> when it is
        /// already in the cache and the file has not changed since; null
        /// otherwise. Never reads or decodes the file, so it is safe on a
        /// path that builds many rows at once (see
        /// <see cref="DeferredThumbnails"/>).
        /// </summary>
        public static Texture2D Peek(string path)
        {
            Texture2D cached;
            if (string.IsNullOrEmpty(path) || !_textures.TryGetValue(path, out cached) || cached == null)
            {
                return null;
            }
            try
            {
                var info = new FileInfo(path);
                (System.DateTime, long) seen;
                if (!info.Exists || !_stamps.TryGetValue(path, out seen)
                    || seen != (info.LastWriteTimeUtc, info.Length))
                {
                    return null;
                }
            }
            catch (System.Exception)
            {
                return null;
            }
            _order.Remove(path);
            _order.AddLast(path);
            return cached;
        }

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

    /// <summary>
    /// Decodes transcript thumbnails a few per editor tick instead of
    /// inside the code that builds a message row (design note
    /// 2026-11-28-lazy-tool-card-details.md). Reading and decoding a
    /// full-size screenshot costs tens of milliseconds, a long turn holds
    /// dozens of them, and a row is rebuilt whenever its message changes
    /// structurally -- so decoding in the row builder froze the panel for
    /// the sum of every picture in every live row, on each reload and on
    /// each rebuild that had fallen out of the 32-entry cache.
    ///
    /// A request is served only while its element is on a panel: an
    /// element that was built and thrown away (a rebuilt row, a row the
    /// virtualized list released) costs nothing. One that is attached
    /// later asks again by itself.
    /// </summary>
    public static class DeferredThumbnails
    {
        /// <summary>Decoding stops for this tick once it has taken this long (at least one picture is always decoded).</summary>
        internal const double BudgetMillis = 8.0;

        private sealed class Pending
        {
            public UnityEngine.UIElements.Image Image;
            public string Path;
            public System.Action<Texture2D> Done;
            public bool Queued;
            public bool Served;
        }

        private static readonly Queue<Pending> _queue = new Queue<Pending>();
        private static bool _hooked;

        /// <summary>Requests waiting for a decode (test seam).</summary>
        internal static int PendingCount
        {
            get { return _queue.Count; }
        }

        /// <summary>
        /// Calls <paramref name="done"/> with the texture for
        /// <paramref name="path"/> (null when the file cannot be decoded)
        /// on a later editor tick, once <paramref name="image"/> is on a
        /// panel. The caller is expected to have tried
        /// <see cref="ImageThumbnailCache.Peek"/> first.
        /// </summary>
        public static void Request(UnityEngine.UIElements.Image image, string path,
            System.Action<Texture2D> done)
        {
            if (image == null || string.IsNullOrEmpty(path) || done == null)
            {
                return;
            }
            var request = new Pending { Image = image, Path = path, Done = done };
            image.RegisterCallback<UnityEngine.UIElements.AttachToPanelEvent>(delegate
            {
                Enqueue(request);
            });
            if (IsLive(image))
            {
                Enqueue(request);
            }
        }

        /// <summary>
        /// EditMode tests build elements without a panel; this makes them
        /// count as on screen so the serving path can be exercised.
        /// </summary>
        internal static bool TreatDetachedAsLiveForTests;

        private static bool IsLive(UnityEngine.UIElements.Image image)
        {
            return image.panel != null || TreatDetachedAsLiveForTests;
        }

        /// <summary>Drops every waiting request (test seam).</summary>
        internal static void ClearForTests()
        {
            _queue.Clear();
        }

        private static void Enqueue(Pending request)
        {
            if (request.Served || request.Queued)
            {
                return;
            }
            request.Queued = true;
            _queue.Enqueue(request);
            if (!_hooked)
            {
                _hooked = true;
                UnityEditor.EditorApplication.update += Pump;
            }
        }

        private static void Pump()
        {
            PumpOnce(BudgetMillis);
            if (_queue.Count == 0)
            {
                _hooked = false;
                UnityEditor.EditorApplication.update -= Pump;
            }
        }

        /// <summary>One tick of work (internal for the EditMode suite).</summary>
        internal static void PumpOnce(double budgetMillis)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            bool decoded = false;
            int pending = _queue.Count;
            while (pending-- > 0)
            {
                Pending request = _queue.Peek();
                if (!IsLive(request.Image))
                {
                    // Thrown away before its turn came; it asks again if
                    // it is ever attached.
                    _queue.Dequeue();
                    request.Queued = false;
                    continue;
                }
                Texture2D texture = ImageThumbnailCache.Peek(request.Path);
                if (texture == null)
                {
                    if (decoded && clock.Elapsed.TotalMilliseconds >= budgetMillis)
                    {
                        return;
                    }
                    texture = ImageThumbnailCache.Get(request.Path);
                    decoded = true;
                }
                _queue.Dequeue();
                request.Queued = false;
                request.Served = true;
                try
                {
                    request.Done(texture);
                }
                catch (System.Exception ex)
                {
                    Debug.LogException(ex);
                }
            }
        }
    }
}
