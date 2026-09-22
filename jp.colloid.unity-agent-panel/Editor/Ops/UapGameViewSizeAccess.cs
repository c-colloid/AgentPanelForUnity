using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>What the Game View is currently set to.</summary>
    public struct UapGameViewSizeInfo
    {
        /// <summary>"FixedResolution" / "AspectRatio", or empty when the editor did not say.</summary>
        public string SizeType;

        /// <summary>Pixels for a fixed resolution; the aspect numbers (e.g. 16 x 9) for an aspect entry.</summary>
        public int Width;
        public int Height;

        /// <summary>The dropdown's own label, e.g. "Full HD (1920x1080)".</summary>
        public string DisplayText;

        /// <summary>The Game View WINDOW's size in points -- not the rendered resolution, but the only number that is always real.</summary>
        public int WindowWidth;
        public int WindowHeight;
    }

    /// <summary>
    /// Reads and writes the Game View's size through the editor internals
    /// that own it (design note docs/design-notes/2026-09-21-console-clear-
    /// and-game-view-size.md section 3). Unity 2022.3 exposes no public API
    /// for this: `GameViewSizes`, `GameViewSizeGroup`, `GameViewSize` and
    /// `GameView.selectedSizeIndex` are all internal, which is why every
    /// tool in this space -- ours included -- goes through reflection.
    ///
    /// The rule this file follows, which the rest of the package already
    /// applies to internal APIs (ConsoleWindowSync degrades to "unavailable"
    /// rather than throwing): NEVER fail silently. Every step that cannot be
    /// resolved returns false with a message naming the step, and
    /// <see cref="TrySetSize"/> READS THE SIZE BACK and reports a mismatch
    /// instead of claiming success -- the same discipline
    /// uap_editor_ui_set_value adopted after it was caught reporting values
    /// the control had never accepted.
    /// </summary>
    public static class UapGameViewSizeAccess
    {
        private const BindingFlags AnyInstance =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags AnyStatic =
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>
        /// Reads the current size. False (with a reason) when no Game View
        /// is open or the internals moved -- batch mode and the EditMode
        /// test runner have no Game View at all, so that is a normal answer
        /// rather than an exception.
        /// </summary>
        public static bool TryGetSize(out UapGameViewSizeInfo info, out string error)
        {
            info = new UapGameViewSizeInfo();
            EditorWindow gameView = UapScreenCapture.FindOpenGameView();
            if (gameView == null)
            {
                error = "No Game View window is open. Open Window > General > Game in the Unity Editor"
                    + " (the panel never opens editor windows on its own), then try again.";
                return false;
            }
            info.WindowWidth = Mathf.RoundToInt(gameView.position.width);
            info.WindowHeight = Mathf.RoundToInt(gameView.position.height);

            object size;
            int index;
            if (!TryReadSelectedSize(gameView, out size, out index, out error))
            {
                // The window size is still worth returning: it is what
                // uap_editor_screenshot captures at.
                return false;
            }
            ReadSizeFields(size, ref info);
            error = null;
            return true;
        }

        /// <summary>
        /// Selects a fixed resolution of <paramref name="width"/> x
        /// <paramref name="height"/>, adding it to the Game View's size list
        /// when it is not there already (exactly what choosing "+" in that
        /// dropdown does, and labelled so a person can see where it came
        /// from). False with a reason when anything did not take.
        /// </summary>
        public static bool TrySetSize(int width, int height, out UapGameViewSizeInfo info, out string error)
        {
            info = new UapGameViewSizeInfo();
            EditorWindow gameView = UapScreenCapture.FindOpenGameView();
            if (gameView == null)
            {
                error = "No Game View window is open. Open Window > General > Game in the Unity Editor"
                    + " (the panel never opens editor windows on its own), then try again.";
                return false;
            }

            object group;
            if (!TryGetCurrentGroup(out group, out error))
            {
                return false;
            }

            int index;
            if (!TryFindFixedResolution(group, width, height, out index, out error))
            {
                if (error != null)
                {
                    return false;
                }
                if (!TryAddFixedResolution(group, width, height, out index, out error))
                {
                    return false;
                }
            }

            if (!TrySetSelectedIndex(gameView, index, out error))
            {
                return false;
            }
            gameView.Repaint();

            // Read back rather than trust the write: an index the editor
            // silently rejected would otherwise be reported as a resize the
            // caller can rely on.
            UapGameViewSizeInfo applied;
            string readError;
            if (!TryGetSize(out applied, out readError))
            {
                error = "The size was set, but reading it back failed: " + readError;
                return false;
            }
            if (applied.Width != width || applied.Height != height)
            {
                error = "The Game View kept " + applied.Width + " x " + applied.Height
                    + " instead of the requested " + width + " x " + height
                    + " (its selected entry is '" + applied.DisplayText + "').";
                info = applied;
                return false;
            }
            info = applied;
            error = null;
            return true;
        }

        private static bool TryGetCurrentGroup(out object group, out string error)
        {
            group = null;
            error = null;
            Type sizesType = Type.GetType("UnityEditor.GameViewSizes,UnityEditor");
            if (sizesType == null)
            {
                error = "This Unity version does not expose UnityEditor.GameViewSizes, so the Game View"
                    + " size cannot be set from here. Set it by hand in the Game View's size dropdown.";
                return false;
            }
            try
            {
                Type singletonType = typeof(ScriptableSingleton<>).MakeGenericType(sizesType);
                PropertyInfo instanceProperty = singletonType.GetProperty("instance", AnyStatic);
                object instance = instanceProperty == null ? null : instanceProperty.GetValue(null, null);
                if (instance == null)
                {
                    error = "UnityEditor.GameViewSizes has no reachable instance on this editor.";
                    return false;
                }
                PropertyInfo currentGroup = sizesType.GetProperty("currentGroup", AnyInstance);
                group = currentGroup == null ? null : currentGroup.GetValue(instance, null);
                if (group == null)
                {
                    error = "UnityEditor.GameViewSizes exposes no 'currentGroup' on this editor.";
                    return false;
                }
                return true;
            }
            catch (Exception exception)
            {
                error = "Reading the Game View size group failed: " + exception.Message;
                return false;
            }
        }

        /// <summary>
        /// Finds an existing FixedResolution entry of this size. Returns
        /// false with error==null when there simply is none (the caller then
        /// adds one), and false with a message when the lookup itself broke.
        /// </summary>
        private static bool TryFindFixedResolution(object group, int width, int height,
            out int index, out string error)
        {
            index = -1;
            error = null;
            try
            {
                Type groupType = group.GetType();
                MethodInfo totalCount = groupType.GetMethod("GetTotalCount", AnyInstance);
                MethodInfo getSize = groupType.GetMethod("GetGameViewSize", AnyInstance);
                if (totalCount == null || getSize == null)
                {
                    error = "UnityEditor.GameViewSizeGroup does not expose GetTotalCount/GetGameViewSize"
                        + " on this editor, so an existing size cannot be reused.";
                    return false;
                }
                int count = (int)totalCount.Invoke(group, null);
                for (int i = 0; i < count; i++)
                {
                    object size = getSize.Invoke(group, new object[] { i });
                    if (size == null)
                    {
                        continue;
                    }
                    var info = new UapGameViewSizeInfo();
                    ReadSizeFields(size, ref info);
                    if (info.Width == width && info.Height == height
                        && string.Equals(info.SizeType, "FixedResolution", StringComparison.Ordinal))
                    {
                        index = i;
                        return true;
                    }
                }
                return false;
            }
            catch (Exception exception)
            {
                error = "Scanning the Game View size list failed: " + exception.Message;
                return false;
            }
        }

        private static bool TryAddFixedResolution(object group, int width, int height,
            out int index, out string error)
        {
            index = -1;
            error = null;
            try
            {
                Type sizeType = Type.GetType("UnityEditor.GameViewSize,UnityEditor");
                Type sizeTypeEnum = Type.GetType("UnityEditor.GameViewSizeType,UnityEditor");
                if (sizeType == null || sizeTypeEnum == null)
                {
                    error = "This Unity version does not expose UnityEditor.GameViewSize, so a custom"
                        + " size cannot be added. Add it by hand in the Game View's size dropdown.";
                    return false;
                }
                object fixedResolution = Enum.Parse(sizeTypeEnum, "FixedResolution");
                ConstructorInfo constructor = sizeType.GetConstructor(
                    new[] { sizeTypeEnum, typeof(int), typeof(int), typeof(string) });
                if (constructor == null)
                {
                    error = "UnityEditor.GameViewSize has no (type, width, height, label) constructor on"
                        + " this editor, so a custom size cannot be added.";
                    return false;
                }
                object size = constructor.Invoke(new[]
                {
                    fixedResolution, (object)width, height,
                    UapGameViewSizePolicy.CustomSizeLabel(width, height)
                });

                Type groupType = group.GetType();
                MethodInfo addCustomSize = groupType.GetMethod("AddCustomSize", AnyInstance);
                MethodInfo indexOf = groupType.GetMethod("IndexOf", AnyInstance);
                MethodInfo totalCount = groupType.GetMethod("GetTotalCount", AnyInstance);
                if (addCustomSize == null)
                {
                    error = "UnityEditor.GameViewSizeGroup does not expose AddCustomSize on this editor.";
                    return false;
                }
                addCustomSize.Invoke(group, new[] { size });
                if (indexOf != null)
                {
                    index = (int)indexOf.Invoke(group, new[] { size });
                }
                if (index < 0 && totalCount != null)
                {
                    // AddCustomSize appends, so the new entry is last.
                    index = (int)totalCount.Invoke(group, null) - 1;
                }
                if (index < 0)
                {
                    error = "The custom size was added but the editor did not report its index.";
                    return false;
                }
                return true;
            }
            catch (Exception exception)
            {
                error = "Adding a custom Game View size failed: " + exception.Message;
                return false;
            }
        }

        private static bool TrySetSelectedIndex(EditorWindow gameView, int index, out string error)
        {
            error = null;
            try
            {
                PropertyInfo selected = gameView.GetType().GetProperty("selectedSizeIndex", AnyInstance);
                if (selected == null || !selected.CanWrite)
                {
                    error = "UnityEditor.GameView does not expose a writable 'selectedSizeIndex' on this"
                        + " editor, so the size cannot be selected from here.";
                    return false;
                }
                selected.SetValue(gameView, index, null);
                return true;
            }
            catch (Exception exception)
            {
                error = "Selecting the Game View size failed: " + exception.Message;
                return false;
            }
        }

        private static bool TryReadSelectedSize(EditorWindow gameView, out object size, out int index,
            out string error)
        {
            size = null;
            index = -1;
            error = null;
            try
            {
                PropertyInfo selected = gameView.GetType().GetProperty("selectedSizeIndex", AnyInstance);
                if (selected == null)
                {
                    error = "UnityEditor.GameView does not expose 'selectedSizeIndex' on this editor.";
                    return false;
                }
                index = (int)selected.GetValue(gameView, null);

                object group;
                if (!TryGetCurrentGroup(out group, out error))
                {
                    return false;
                }
                MethodInfo getSize = group.GetType().GetMethod("GetGameViewSize", AnyInstance);
                if (getSize == null)
                {
                    error = "UnityEditor.GameViewSizeGroup does not expose GetGameViewSize on this editor.";
                    return false;
                }
                size = getSize.Invoke(group, new object[] { index });
                if (size == null)
                {
                    error = "The Game View's selected size index (" + index + ") resolved to nothing.";
                    return false;
                }
                return true;
            }
            catch (Exception exception)
            {
                error = "Reading the Game View's selected size failed: " + exception.Message;
                return false;
            }
        }

        /// <summary>Fills what it can from a GameViewSize instance; missing members stay at their defaults rather than throwing.</summary>
        private static void ReadSizeFields(object size, ref UapGameViewSizeInfo info)
        {
            Type type = size.GetType();
            info.Width = ReadInt(type, size, "width", info.Width);
            info.Height = ReadInt(type, size, "height", info.Height);
            info.DisplayText = ReadString(type, size, "displayText", info.DisplayText);
            object sizeType = ReadMember(type, size, "sizeType");
            if (sizeType != null)
            {
                info.SizeType = sizeType.ToString();
            }
        }

        private static int ReadInt(Type type, object target, string name, int fallback)
        {
            object value = ReadMember(type, target, name);
            return value is int ? (int)value : fallback;
        }

        private static string ReadString(Type type, object target, string name, string fallback)
        {
            object value = ReadMember(type, target, name);
            return value == null ? fallback : value.ToString();
        }

        private static object ReadMember(Type type, object target, string name)
        {
            try
            {
                PropertyInfo property = type.GetProperty(name, AnyInstance);
                if (property != null)
                {
                    return property.GetValue(target, null);
                }
                FieldInfo field = type.GetField(name, AnyInstance);
                return field == null ? null : field.GetValue(target);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
