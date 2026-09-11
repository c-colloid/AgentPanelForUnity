using System;
using System.Collections.Generic;
using System.IO;

namespace Colloid.AgentPanel.Integration
{
    /// <summary>
    /// What a drop onto the panel contained, split the way the panel
    /// consumes it (design note 2026-09-07 decision I4): image files go to
    /// the composer as image attachments, everything else stays a context
    /// chip. Pure over the two DragAndDrop arrays (object references and
    /// paths) plus an asset-path lookup, so DropPayloadClassifierTests can
    /// pin every branch without a real drag.
    /// </summary>
    public sealed class DropPayload
    {
        /// <summary>Object references that are NOT image assets (GameObjects, prefabs, materials, ...).</summary>
        public readonly List<UnityEngine.Object> Objects = new List<UnityEngine.Object>();
        /// <summary>Absolute paths of image files to attach: OS drops and Texture assets alike.</summary>
        public readonly List<string> ImagePaths = new List<string>();

        public bool IsEmpty
        {
            get { return Objects.Count == 0 && ImagePaths.Count == 0; }
        }
    }

    public static class DropPayloadClassifier
    {
        /// <summary>
        /// <paramref name="objects"/> / <paramref name="paths"/> are
        /// DragAndDrop.objectReferences / DragAndDrop.paths. A Texture2D
        /// whose asset (<paramref name="assetPathOf"/>, project-relative or
        /// null) is a PNG/JPEG becomes an image path (made absolute under
        /// <paramref name="projectRoot"/>); other objects stay objects. A
        /// path with no object behind it (an OS drop) is an image when it
        /// is a PNG/JPEG; anything else is ignored. Duplicates collapse.
        /// </summary>
        public static DropPayload Classify(IList<UnityEngine.Object> objects, IList<string> paths,
            Func<UnityEngine.Object, string> assetPathOf, string projectRoot)
        {
            var payload = new DropPayload();
            var seenImages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var assetPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (objects != null)
            {
                for (int i = 0; i < objects.Count; i++)
                {
                    UnityEngine.Object obj = objects[i];
                    if (obj == null)
                    {
                        continue;
                    }
                    string assetPath = assetPathOf != null ? assetPathOf(obj) : null;
                    if (!string.IsNullOrEmpty(assetPath))
                    {
                        assetPaths.Add(assetPath);
                    }
                    if (obj is UnityEngine.Texture2D && ImageAttachmentPolicy.IsSupportedFile(assetPath))
                    {
                        string absolute = ToAbsolute(assetPath, projectRoot);
                        if (seenImages.Add(absolute))
                        {
                            payload.ImagePaths.Add(absolute);
                        }
                        continue;
                    }
                    payload.Objects.Add(obj);
                }
            }
            if (paths != null)
            {
                for (int i = 0; i < paths.Count; i++)
                {
                    string path = paths[i];
                    if (string.IsNullOrEmpty(path) || assetPaths.Contains(path))
                    {
                        // Backed by an object reference above: already handled
                        // (as an image or as a chip), never both.
                        continue;
                    }
                    if (!ImageAttachmentPolicy.IsSupportedFile(path))
                    {
                        continue;
                    }
                    string absolute = ToAbsolute(path, projectRoot);
                    if (seenImages.Add(absolute))
                    {
                        payload.ImagePaths.Add(absolute);
                    }
                }
            }
            return payload;
        }

        /// <summary>True when a drag carries anything the panel would take (objects, or image file paths).</summary>
        public static bool HasDroppable(IList<UnityEngine.Object> objects, IList<string> paths)
        {
            if (objects != null)
            {
                for (int i = 0; i < objects.Count; i++)
                {
                    if (objects[i] != null)
                    {
                        return true;
                    }
                }
            }
            if (paths != null)
            {
                for (int i = 0; i < paths.Count; i++)
                {
                    if (ImageAttachmentPolicy.IsSupportedFile(paths[i]))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>Project-relative ("Assets/x.png", "Packages/...") paths become absolute; absolute paths pass through.</summary>
        public static string ToAbsolute(string path, string projectRoot)
        {
            if (string.IsNullOrEmpty(path))
            {
                return path;
            }
            // A Windows drive path ("C:\\x") is rooted even when the editor
            // runs on a platform whose Path does not think so.
            bool rooted = Path.IsPathRooted(path)
                || (path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/'));
            if (rooted || string.IsNullOrEmpty(projectRoot))
            {
                return path.Replace('\\', '/');
            }
            return Path.Combine(projectRoot, path).Replace('\\', '/');
        }
    }
}
