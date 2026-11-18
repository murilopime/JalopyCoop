using System;
using System.Collections.Generic;
using UnityEngine;

namespace JalopyCoop.Sync
{
    /// <summary>
    /// FindObjectsOfType varre a cena inteira e é caro; as sincronias que olham o estado várias vezes por segundo
    /// usam esta lista guardada, refeita a cada poucos segundos. Quem usa tem que pular os destruídos (== null).
    /// </summary>
    internal static class SceneCache
    {
        private const float Refresh = 2.5f;
        private static readonly Dictionary<Type, KeyValuePair<float, UnityEngine.Object[]>> cache = new Dictionary<Type, KeyValuePair<float, UnityEngine.Object[]>>();

        internal static T[] Get<T>() where T : Component
        {
            KeyValuePair<float, UnityEngine.Object[]> e;
            float now = Time.realtimeSinceStartup;
            if (cache.TryGetValue(typeof(T), out e) && now - e.Key < Refresh) return (T[])e.Value;
            var arr = UnityEngine.Object.FindObjectsOfType<T>();
            cache[typeof(T)] = new KeyValuePair<float, UnityEngine.Object[]>(now, arr);
            return arr;
        }
    }
}
