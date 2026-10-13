using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace HarmonyLib
{
    /// <summary>
    /// Every [HarmonyPatch] of the baked mods is already part of the game's code (port/ModBaker), so
    /// PatchAll has nothing left to do. Patch() calls made at runtime can't be honoured on IL2CPP.
    /// </summary>
    public class Harmony
    {
        public string Id { get; private set; }

        public Harmony(string id)
        {
            Id = id;
        }

        public void PatchAll()
        {
        }

        public void PatchAll(Assembly assembly)
        {
        }

        public void PatchAll(Type type)
        {
        }

        public MethodInfo Patch(MethodBase original, HarmonyMethod prefix = null, HarmonyMethod postfix = null, HarmonyMethod transpiler = null, HarmonyMethod finalizer = null, HarmonyMethod ilmanipulator = null)
        {
            Debug.LogWarning("[Harmony] " + Id + ": runtime patch of " + (original == null ? "null" : original.DeclaringType + "." + original.Name) + " ignored (not baked into the web build)");
            return null;
        }

        public MethodInfo Patch(MethodBase original, HarmonyMethod prefix, HarmonyMethod postfix, HarmonyMethod transpiler, HarmonyMethod finalizer)
        {
            return Patch(original, prefix, postfix, transpiler, finalizer, null);
        }

        public void UnpatchSelf()
        {
        }

        public void UnpatchAll(string harmonyID = null)
        {
        }
    }

    public class HarmonyMethod
    {
        public MethodInfo method;

        public HarmonyMethod()
        {
        }

        public HarmonyMethod(MethodInfo method)
        {
            this.method = method;
        }

        public HarmonyMethod(Type methodType, string methodName, Type[] argumentTypes = null)
        {
            method = AccessTools.Method(methodType, methodName, argumentTypes);
        }
    }

    public static class AccessTools
    {
        public static readonly BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.GetField | BindingFlags.SetField | BindingFlags.GetProperty | BindingFlags.SetProperty;

        public static readonly BindingFlags allDeclared = all | BindingFlags.DeclaredOnly;

        public static MethodInfo Method(Type type, string name, Type[] parameters = null, Type[] generics = null)
        {
            if (type == null || name == null)
            {
                return null;
            }
            MethodInfo result = null;
            for (Type t = type; t != null && result == null; t = t.BaseType)
            {
                result = parameters == null ? t.GetMethod(name, allDeclared) : t.GetMethod(name, allDeclared, null, parameters, new ParameterModifier[0]);
            }
            if (result != null && generics != null)
            {
                result = result.MakeGenericMethod(generics);
            }
            return result;
        }

        public static MethodInfo DeclaredMethod(Type type, string name, Type[] parameters = null, Type[] generics = null)
        {
            if (type == null || name == null)
            {
                return null;
            }
            MethodInfo result = parameters == null ? type.GetMethod(name, allDeclared) : type.GetMethod(name, allDeclared, null, parameters, new ParameterModifier[0]);
            if (result != null && generics != null)
            {
                result = result.MakeGenericMethod(generics);
            }
            return result;
        }

        public static FieldInfo Field(Type type, string name)
        {
            for (Type t = type; t != null; t = t.BaseType)
            {
                FieldInfo f = t.GetField(name, allDeclared);
                if (f != null)
                {
                    return f;
                }
            }
            return null;
        }

        public static FieldInfo DeclaredField(Type type, string name)
        {
            return type == null ? null : type.GetField(name, allDeclared);
        }

        public static PropertyInfo Property(Type type, string name)
        {
            for (Type t = type; t != null; t = t.BaseType)
            {
                PropertyInfo p = t.GetProperty(name, allDeclared);
                if (p != null)
                {
                    return p;
                }
            }
            return null;
        }

        public static List<FieldInfo> GetDeclaredFields(Type type)
        {
            return type.GetFields(allDeclared).ToList();
        }

        public static List<MethodInfo> GetDeclaredMethods(Type type)
        {
            return type.GetMethods(allDeclared).ToList();
        }

        public static Type TypeByName(string name)
        {
            Type type = Type.GetType(name, false);
            if (type != null)
            {
                return type;
            }
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                type = assembly.GetType(name, false);
                if (type != null)
                {
                    return type;
                }
            }
            return null;
        }
    }

    public static class CollectionExtensions
    {
        public static void Do<T>(this IEnumerable<T> sequence, Action<T> action)
        {
            if (sequence == null)
            {
                return;
            }
            foreach (T item in sequence)
            {
                action(item);
            }
        }

        public static T[] AddToArray<T>(this T[] sequence, T item)
        {
            T[] result = new T[(sequence == null ? 0 : sequence.Length) + 1];
            if (sequence != null)
            {
                Array.Copy(sequence, result, sequence.Length);
            }
            result[result.Length - 1] = item;
            return result;
        }
    }
}

namespace HarmonyLib.Public.Patching
{
    /// <summary>Patched bodies built by HarmonyX call this when a [HarmonyWrapSafe] patch or finalizer throws.</summary>
    public static class HarmonyManipulator
    {
        public static void LogPatchException(object errorObject, string patch)
        {
            Debug.LogError("[Harmony] Error while running " + patch + ": " + errorObject);
        }
    }
}
