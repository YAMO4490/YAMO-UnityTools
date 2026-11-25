using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace YAMO.UnityTools.Editor
{
    public static class YamoUnityVersionCompat
    {
        public static ulong ObjectId(UnityEngine.Object value)
        {
            if (!value) return 0;
#if UNITY_6000_3_OR_NEWER
            return EntityId.ToULong(value.GetEntityId());
#else
            return unchecked((ulong)(long)value.GetInstanceID());
#endif
        }
        public static UnityEngine.Object ResolveObject(ulong id)
        {
#if UNITY_6000_3_OR_NEWER
            return EditorUtility.EntityIdToObject(EntityId.FromULong(id));
#else
            return EditorUtility.InstanceIDToObject(unchecked((int)id));
#endif
        }
        public static System.Collections.Generic.IEnumerable<Assembly> LoadedAssemblies()
        {
#if UNITY_7000_0_OR_NEWER
            return UnityEngine.Assemblies.CurrentAssemblies.GetLoadedAssemblies();
#else
            return AppDomain.CurrentDomain.GetAssemblies();
#endif
        }
        public static GameObject CloneInScene(GameObject original, Scene scene)
        {
#if UNITY_7000_0_OR_NEWER
            return (GameObject)UnityEngine.Object.Instantiate(original, scene);
#else
            var copy = UnityEngine.Object.Instantiate(original);
            SceneManager.MoveGameObjectToScene(copy, scene);
            return copy;
#endif
        }
    }
}
