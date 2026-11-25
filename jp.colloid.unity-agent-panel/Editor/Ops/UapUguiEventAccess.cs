using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Colloid.AgentPanel.Ops
{
    /// <summary>
    /// Reflection into uGUI's event system (UnityEngine.EventSystems in the
    /// com.unity.ugui package) for uap_game_pointer: the EventSystem, a
    /// PointerEventData, the raycast, and ExecuteEvents' typed dispatch.
    ///
    /// Reflection rather than a reference for the same reason
    /// <see cref="UapCanvasProbe"/> reads Canvas by reflection: this
    /// package must keep compiling in a project without com.unity.ugui
    /// (the CI host projects have neither it nor the Input System), and an
    /// asmdef reference to a package that may be absent is a compile break
    /// in exactly the projects that need the rest of the panel most.
    /// Every step reports which step failed rather than returning null
    /// (design note docs/design-notes/2026-09-22-game-input-tools.md
    /// section 3.3).
    ///
    /// Handler keys accepted by the dispatch methods: pointerDown,
    /// pointerUp, pointerClick, initializePotentialDrag, beginDrag, drag,
    /// endDrag, drop. Each maps onto ExecuteEvents' static functor of the
    /// same name plus "Handler" and the I...Handler interface it is generic
    /// over.
    /// </summary>
    internal static class UapUguiEventAccess
    {
        private const string EventSystemTypeName = "UnityEngine.EventSystems.EventSystem";
        private const string PointerEventDataTypeName = "UnityEngine.EventSystems.PointerEventData";
        private const string RaycastResultTypeName = "UnityEngine.EventSystems.RaycastResult";
        private const string ExecuteEventsTypeName = "UnityEngine.EventSystems.ExecuteEvents";

        /// <summary>What to tell the agent when the package is not there.</summary>
        public const string MissingPackageMessage =
            "uGUI's event system (UnityEngine.EventSystems, package com.unity.ugui) is not in this project,"
            + " so there is no EventSystem to deliver pointer events to. This tool drives uGUI (Canvas)"
            + " interfaces only.";

        private static bool _loaded;
        private static string _loadError;
        private static Type _eventSystemType;
        private static Type _pointerEventDataType;
        private static Type _raycastResultType;
        private static Type _executeEventsType;
        private static Type _inputButtonType;
        private static MethodInfo _executeMethod;
        private static MethodInfo _executeHierarchyMethod;
        private static MethodInfo _getEventHandlerMethod;
        private static MethodInfo _raycastAllMethod;

        /// <summary>Resolves every type and method once; false with the reason when any is missing.</summary>
        public static bool TryLoad(out string error)
        {
            if (_loaded)
            {
                error = _loadError;
                return _loadError == null;
            }
            _loaded = true;
            _eventSystemType = FindType(EventSystemTypeName);
            if (_eventSystemType == null)
            {
                _loadError = MissingPackageMessage;
                error = _loadError;
                return false;
            }
            _pointerEventDataType = FindType(PointerEventDataTypeName);
            _raycastResultType = FindType(RaycastResultTypeName);
            _executeEventsType = FindType(ExecuteEventsTypeName);
            if (_pointerEventDataType == null || _raycastResultType == null || _executeEventsType == null)
            {
                _loadError = "uGUI is installed but its event types could not be resolved (PointerEventData /"
                    + " RaycastResult / ExecuteEvents); this Unity version lays them out differently.";
                error = _loadError;
                return false;
            }
            _inputButtonType = _pointerEventDataType.GetNestedType("InputButton", BindingFlags.Public);
            _executeMethod = FindGenericStatic(_executeEventsType, "Execute", 3);
            _executeHierarchyMethod = FindGenericStatic(_executeEventsType, "ExecuteHierarchy", 3);
            _getEventHandlerMethod = FindGenericStatic(_executeEventsType, "GetEventHandler", 1);
            _raycastAllMethod = _eventSystemType.GetMethod("RaycastAll", BindingFlags.Public | BindingFlags.Instance);
            if (_inputButtonType == null || _executeMethod == null || _executeHierarchyMethod == null
                || _getEventHandlerMethod == null || _raycastAllMethod == null)
            {
                _loadError = "uGUI is installed but ExecuteEvents / EventSystem.RaycastAll could not be resolved;"
                    + " this Unity version lays them out differently.";
                error = _loadError;
                return false;
            }
            _loadError = null;
            error = null;
            return true;
        }

        /// <summary>EventSystem.current, or null with the reason (no EventSystem in the running scene).</summary>
        public static object CurrentEventSystem(out string error)
        {
            string loadError;
            if (!TryLoad(out loadError))
            {
                error = loadError;
                return null;
            }
            PropertyInfo current = _eventSystemType.GetProperty("current", BindingFlags.Public | BindingFlags.Static);
            object eventSystem = current != null ? current.GetValue(null, null) : null;
            if (eventSystem == null || (eventSystem is UnityEngine.Object && !((UnityEngine.Object)eventSystem)))
            {
                error = "No EventSystem is active in the running scene, so uGUI cannot receive pointer events."
                    + " Add one (GameObject > UI > Event System) -- Unity creates it with the first Canvas.";
                return null;
            }
            error = null;
            return eventSystem;
        }

        /// <summary>A PointerEventData positioned at <paramref name="screenPosition"/> (bottom-left origin) for <paramref name="button"/>.</summary>
        public static object CreatePointerData(object eventSystem, Vector2 screenPosition, UapGamePointerButton button)
        {
            ConstructorInfo ctor = _pointerEventDataType.GetConstructor(new[] { _eventSystemType });
            object data = ctor.Invoke(new[] { eventSystem });
            SetProperty(data, "position", screenPosition);
            SetProperty(data, "pressPosition", screenPosition);
            SetProperty(data, "delta", Vector2.zero);
            SetProperty(data, "button", Enum.Parse(_inputButtonType, button.ToString()));
            return data;
        }

        /// <summary>
        /// EventSystem.RaycastAll at the pointer's position: the topmost
        /// hit's GameObject (null when nothing is under the pointer), with
        /// pointerCurrentRaycast / pointerPressRaycast set to that hit so
        /// handlers reading them see what a real click would.
        /// </summary>
        public static GameObject Raycast(object eventSystem, object pointerData)
        {
            Type listType = typeof(List<>).MakeGenericType(_raycastResultType);
            IList results = (IList)Activator.CreateInstance(listType);
            _raycastAllMethod.Invoke(eventSystem, new[] { pointerData, results });
            if (results.Count == 0)
            {
                return null;
            }
            object hit = results[0];
            SetProperty(pointerData, "pointerCurrentRaycast", hit);
            SetProperty(pointerData, "pointerPressRaycast", hit);
            PropertyInfo gameObjectProperty = _raycastResultType.GetProperty("gameObject", BindingFlags.Public | BindingFlags.Instance);
            return gameObjectProperty != null ? gameObjectProperty.GetValue(hit, null) as GameObject : null;
        }

        /// <summary>Points the pointer data at <paramref name="target"/> directly, as if a raycast had hit it (the bypass path for a raycast-blocked widget).</summary>
        public static void SetDirectTarget(object pointerData, GameObject target)
        {
            object hit = Activator.CreateInstance(_raycastResultType);
            PropertyInfo gameObjectProperty = _raycastResultType.GetProperty("gameObject", BindingFlags.Public | BindingFlags.Instance);
            gameObjectProperty.SetValue(hit, target, null);
            SetProperty(pointerData, "pointerCurrentRaycast", hit);
            SetProperty(pointerData, "pointerPressRaycast", hit);
        }

        /// <summary>Moves the pointer: position and the delta since the last move.</summary>
        public static void SetPosition(object pointerData, Vector2 screenPosition, Vector2 delta)
        {
            SetProperty(pointerData, "position", screenPosition);
            SetProperty(pointerData, "delta", delta);
        }

        /// <summary>Records which object took the press (pointerPress) and which was actually hit (rawPointerPress), plus click eligibility.</summary>
        public static void SetPress(object pointerData, GameObject pressTarget, GameObject rawTarget, bool eligibleForClick)
        {
            SetProperty(pointerData, "pointerPress", pressTarget);
            SetProperty(pointerData, "rawPointerPress", rawTarget);
            SetProperty(pointerData, "eligibleForClick", eligibleForClick);
            SetProperty(pointerData, "clickCount", eligibleForClick ? 1 : 0);
            SetProperty(pointerData, "clickTime", Time.unscaledTime);
        }

        /// <summary>Marks a drag in progress (or over) on <paramref name="dragTarget"/>.</summary>
        public static void SetDragging(object pointerData, bool dragging, GameObject dragTarget)
        {
            SetProperty(pointerData, "dragging", dragging);
            SetProperty(pointerData, "pointerDrag", dragTarget);
        }

        /// <summary>ExecuteEvents.GetEventHandler&lt;T&gt;: the nearest object at or above <paramref name="from"/> that implements the handler, or null.</summary>
        public static GameObject GetHandler(string handlerKey, GameObject from)
        {
            if (from == null)
            {
                return null;
            }
            Type handlerInterface = HandlerInterface(handlerKey);
            return _getEventHandlerMethod.MakeGenericMethod(handlerInterface).Invoke(null, new object[] { from }) as GameObject;
        }

        /// <summary>ExecuteEvents.Execute&lt;T&gt; on exactly <paramref name="target"/>; false when it does not implement the handler.</summary>
        public static bool Execute(string handlerKey, GameObject target, object pointerData)
        {
            if (target == null)
            {
                return false;
            }
            Type handlerInterface = HandlerInterface(handlerKey);
            object functor = Functor(handlerKey);
            object handled = _executeMethod.MakeGenericMethod(handlerInterface)
                .Invoke(null, new[] { target, pointerData, functor });
            return handled is bool && (bool)handled;
        }

        /// <summary>ExecuteEvents.ExecuteHierarchy&lt;T&gt;: walks up from <paramref name="root"/> to the first object that handles it; returns that object or null.</summary>
        public static GameObject ExecuteHierarchy(string handlerKey, GameObject root, object pointerData)
        {
            if (root == null)
            {
                return null;
            }
            Type handlerInterface = HandlerInterface(handlerKey);
            object functor = Functor(handlerKey);
            return _executeHierarchyMethod.MakeGenericMethod(handlerInterface)
                .Invoke(null, new[] { root, pointerData, functor }) as GameObject;
        }

        private static object Functor(string handlerKey)
        {
            PropertyInfo property = _executeEventsType.GetProperty(handlerKey + "Handler", BindingFlags.Public | BindingFlags.Static);
            if (property == null)
            {
                throw new InvalidOperationException("ExecuteEvents." + handlerKey + "Handler is not available in this Unity version.");
            }
            return property.GetValue(null, null);
        }

        private static Type HandlerInterface(string handlerKey)
        {
            string name = "UnityEngine.EventSystems.I" + char.ToUpperInvariant(handlerKey[0]) + handlerKey.Substring(1) + "Handler";
            Type type = _executeEventsType.Assembly.GetType(name, false);
            if (type == null)
            {
                throw new InvalidOperationException(name + " is not available in this Unity version.");
            }
            return type;
        }

        private static void SetProperty(object target, string name, object value)
        {
            PropertyInfo property = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property == null || !property.CanWrite)
            {
                throw new InvalidOperationException("PointerEventData." + name + " is not writable in this Unity version.");
            }
            property.SetValue(target, value, null);
        }

        private static MethodInfo FindGenericStatic(Type type, string name, int parameterCount)
        {
            MethodInfo[] methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static);
            for (int i = 0; i < methods.Length; i++)
            {
                if (methods[i].Name == name && methods[i].IsGenericMethodDefinition
                    && methods[i].GetParameters().Length == parameterCount)
                {
                    return methods[i];
                }
            }
            return null;
        }

        /// <summary>Finds a type by full name across every loaded assembly, without knowing which assembly a Unity version puts it in.</summary>
        internal static Type FindType(string fullName)
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Type type;
                try
                {
                    type = assemblies[i].GetType(fullName, false);
                }
                catch (Exception)
                {
                    continue;
                }
                if (type != null)
                {
                    return type;
                }
            }
            return null;
        }
    }
}
