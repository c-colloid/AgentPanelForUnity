using System;
using Object = UnityEngine.Object;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// The one place this package spells the UnityEngine.Object APIs whose
    /// shape changed across the supported editors (design note
    /// docs/design-notes/2026-09-11-unity-6-support.md, "Unity 6.3 / 6.4 /
    /// 6.5"). Everything else calls these two members and stays free of
    /// version conditionals:
    ///   - 2022.3 .. 6000.0: FindObjectsByType(FindObjectsSortMode) is the
    ///     current API and Object identity is GetInstanceID().
    ///   - 6000.3: EntityId and Object.GetEntityId() appear;
    ///     GetInstanceID() is still current.
    ///   - 6000.4: GetInstanceID() and FindObjectsSortMode are obsolete
    ///     (warning); the parameterless FindObjectsByType overload appears.
    ///   - 6000.5: GetInstanceID() is obsolete-as-error; the EntityId->int
    ///     cast is obsolete-as-error too.
    /// </summary>
    public static class UnityObjectCompat
    {
        /// <summary>
        /// Every loaded, active object of type <typeparamref name="T"/>, in
        /// no particular order (the callers count or scan the whole set;
        /// none relies on the old FindObjectsOfType InstanceID ordering).
        /// </summary>
        public static T[] FindAll<T>() where T : Object
        {
#if UNITY_6000_4_OR_NEWER
            return Object.FindObjectsByType<T>();
#else
            return Object.FindObjectsByType<T>(UnityEngine.FindObjectsSortMode.None);
#endif
        }
    }

    /// <summary>
    /// Version-neutral identity of a UnityEngine.Object: EntityId on Unity
    /// 6.3 and newer, the instance id before. Only equality is exposed --
    /// the underlying number is deliberately not, because EntityId stops
    /// being representable as an int from 6.5 on. <see cref="None"/> (the
    /// default value) is the identity of a true null and never equals a
    /// real object's identity (instance ids and EntityIds are never 0).
    /// </summary>
    public readonly struct UnityObjectId : IEquatable<UnityObjectId>
    {
#if UNITY_6000_3_OR_NEWER
        private readonly UnityEngine.EntityId _value;

        private UnityObjectId(UnityEngine.EntityId value)
        {
            _value = value;
        }
#else
        private readonly int _value;

        private UnityObjectId(int value)
        {
            _value = value;
        }
#endif

        /// <summary>Identity of a true null; equals no real object.</summary>
        public static UnityObjectId None
        {
            get { return default(UnityObjectId); }
        }

        /// <summary>
        /// Identity of <paramref name="obj"/>, or <see cref="None"/> for a
        /// true (managed) null. Works on a DESTROYED object too: the id is
        /// cached managed state, which is what lets "the material was
        /// swapped" be detected after the old material died with its scene.
        /// </summary>
        public static UnityObjectId Of(Object obj)
        {
            if (ReferenceEquals(obj, null))
            {
                return None;
            }
            try
            {
#if UNITY_6000_3_OR_NEWER
                return new UnityObjectId(obj.GetEntityId());
#else
                return new UnityObjectId(obj.GetInstanceID());
#endif
            }
            catch (Exception)
            {
                return None;
            }
        }

        public bool Equals(UnityObjectId other)
        {
            return _value.Equals(other._value);
        }

        public override bool Equals(object obj)
        {
            return obj is UnityObjectId other && Equals(other);
        }

        public override int GetHashCode()
        {
            return _value.GetHashCode();
        }

        public override string ToString()
        {
            return _value.ToString();
        }

        public static bool operator ==(UnityObjectId left, UnityObjectId right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(UnityObjectId left, UnityObjectId right)
        {
            return !left.Equals(right);
        }
    }
}
