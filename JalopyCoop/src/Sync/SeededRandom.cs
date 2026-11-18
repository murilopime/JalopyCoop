using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace JalopyCoop.Sync
{
    /// <summary>
    /// MVP 4d: mundo igual nos dois jogos sem mandar o mundo pela rede.
    ///
    /// O jogo sorteia (UnityEngine.Random) as opções de rota, os trechos de estrada, cidades, motel, loja, caixas...
    /// Como o host e o tio rodam em ritmos diferentes, o Random global nunca bate. Aqui, todo método das classes
    /// que geram o mundo e que chama Random roda com uma semente própria:
    ///     semente = hash(método, posição do objeto, quantas vezes esse método já rodou nesse objeto)
    /// e depois o Random global volta ao que era. Mesmo save + mesmas escolhas = mesmo mundo nos dois.
    /// </summary>
    internal static class SeededRandom
    {
        private const uint BaseSeed = 0x4A43_7A11; // "JalopyCoop"

        // Classes que geram o mundo (rota, cidades, lojas, motel, caixas, carros abandonados, sinais...).
        private static readonly Type[] Types =
        {
            typeof(RouteGeneratorC), typeof(SpawnContinueC), typeof(Hub_CitySpawnC), typeof(HomeLogicC),
            typeof(MotelLogicC), typeof(StoreC), typeof(ShopC), typeof(AbandonCarC), typeof(LaikaBuildingC),
            typeof(ScrapYardC), typeof(PuddleLogicC), typeof(StreetLightC), typeof(BoxContentsC), typeof(BorderLogicC),
            typeof(EquipRandomItem), typeof(CurtainsLogicC), typeof(EngineComponentC), typeof(ObjectPickupC),
            typeof(NewspaperC), typeof(EngineComponentsCataloguePageC), typeof(RandomNames), typeof(EquipItems),
            typeof(TrafficLightManagerC), typeof(TrafficLightC), typeof(PlayIdleAnimations), typeof(ExtraUpgradesC),
            typeof(UniqueGameLogic),
        };

        private static readonly HashSet<string> SkipNames = new HashSet<string> { "Update", "FixedUpdate", "LateUpdate", "OnGUI" };

        private static readonly Dictionary<long, int> counters = new Dictionary<long, int>();

        /// <summary>Semente extra do mundo (0 = a do save). Trocar = outras opções de rota (botão de teste no F4).</summary>
        internal static uint WorldSalt;

        /// <summary>Nova semente: zera as contagens para os dois jogos partirem do mesmo ponto.</summary>
        internal static void SetSalt(uint salt)
        {
            WorldSalt = salt;
            counters.Clear();
        }
        private static readonly Dictionary<MethodBase, uint> methodHash = new Dictionary<MethodBase, uint>();

        /// <summary>
        /// Zera as contagens de um objeto (ex.: o gerador de rota ao criar a rota). Antes disso cada jogo pode ter
        /// rodado funções do gerador um número diferente de vezes (olhar o mapa, fronteira...), e aí a estrada saía
        /// diferente. Devolve quantos sorteios o objeto tinha feito (para conferir nos dois logs).
        /// </summary>
        internal static int ResetFor(Component c)
        {
            if (c == null) return 0;
            long id = c.GetInstanceID();
            var keys = new List<long>();
            int total = 0;
            foreach (var kv in counters) if ((kv.Key >> 32) == id) { keys.Add(kv.Key); total += kv.Value; }
            foreach (var k in keys) counters.Remove(k);
            return total;
        }

        public static void Install(Harmony harmony)
        {
            var prefixInst = new HarmonyMethod(typeof(SeededRandom).GetMethod("PrefixInstance", BindingFlags.Static | BindingFlags.NonPublic));
            var prefixStatic = new HarmonyMethod(typeof(SeededRandom).GetMethod("PrefixStatic", BindingFlags.Static | BindingFlags.NonPublic));
            var finalizer = new HarmonyMethod(typeof(SeededRandom).GetMethod("Finalizer", BindingFlags.Static | BindingFlags.NonPublic));
            int count = 0;
            foreach (var t in Types)
            {
                var all = new List<MethodBase>();
                foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)) all.Add(m);
                // corpo das corrotinas (IEnumerator): fica no MoveNext de uma classe aninhada
                foreach (var nt in t.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public))
                {
                    var mn = nt.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (mn != null) all.Add(mn);
                }
                foreach (var m in all)
                {
                    if (m.IsAbstract || m.ContainsGenericParameters || SkipNames.Contains(m.Name)) continue;
                    if (!UsesRandom(m)) continue;
                    try
                    {
                        harmony.Patch(m, prefix: m.IsStatic ? prefixStatic : prefixInst, finalizer: finalizer);
                        methodHash[m] = Hash(m.DeclaringType.FullName + "::" + m.Name);
                        count++;
                    }
                    catch (Exception e) { Plugin.Log.LogWarning("Sorteio fixo: não deu para " + t.Name + "." + m.Name + ": " + e.Message); }
                }
            }
            Plugin.Log.LogInfo("Sorteio fixo do mundo: " + count + " métodos.");
        }

        private static bool UsesRandom(MethodBase m)
        {
            try
            {
                foreach (var ci in PatchProcessor.GetOriginalInstructions(m))
                {
                    var mi = ci.operand as MethodInfo;
                    if (mi != null && mi.DeclaringType == typeof(UnityEngine.Random)) return true;
                }
            }
            catch { }
            return false;
        }

        private static void PrefixInstance(object __instance, MethodBase __originalMethod, out UnityEngine.Random.State __state)
        {
            __state = UnityEngine.Random.state;
            Seed(__instance, __originalMethod);
        }

        private static void PrefixStatic(MethodBase __originalMethod, out UnityEngine.Random.State __state)
        {
            __state = UnityEngine.Random.state;
            Seed(null, __originalMethod);
        }

        private static Exception Finalizer(Exception __exception, UnityEngine.Random.State __state)
        {
            UnityEngine.Random.state = __state;
            return __exception;
        }

        private static void Seed(object instance, MethodBase method)
        {
            var owner = instance as Component;
            if (owner == null && instance != null)
            {
                // corrotina: o dono é o campo "<>4__this"
                var f = instance.GetType().GetField("<>4__this", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (f != null) owner = f.GetValue(instance) as Component;
            }
            uint h;
            if (!methodHash.TryGetValue(method, out h)) h = Hash(method.Name);
            int px = 0, py = 0, pz = 0, id = 0;
            if (owner != null)
            {
                var p = owner.transform.position;
                // catálogo do Laika: o objeto fica com a câmera/jogador; o lugar fixo é o ponto de entrega da loja
                var cat = owner as EngineComponentsCataloguePageC;
                if (cat != null && cat.spawnLocations != null && cat.spawnLocations.Length > 0 && cat.spawnLocations[0] != null)
                    p = cat.spawnLocations[0].position;
                px = Mathf.RoundToInt(p.x * 10f); py = Mathf.RoundToInt(p.y * 10f); pz = Mathf.RoundToInt(p.z * 10f);
                id = owner.GetInstanceID();
            }
            long key = ((long)id << 32) ^ h;
            int n;
            counters.TryGetValue(key, out n);
            counters[key] = n + 1;

            uint s = BaseSeed ^ WorldSalt;
            s = Mix(s, h); s = Mix(s, (uint)px); s = Mix(s, (uint)py); s = Mix(s, (uint)pz); s = Mix(s, (uint)n);
            UnityEngine.Random.InitState((int)s);
        }

        private static uint Mix(uint a, uint b)
        {
            unchecked
            {
                a ^= b + 0x9E3779B9u + (a << 6) + (a >> 2);
                a *= 16777619u;
                return a;
            }
        }

        internal static uint Hash(string s)
        {
            unchecked
            {
                uint h = 2166136261u;
                foreach (char c in s) { h ^= c; h *= 16777619u; }
                return h;
            }
        }
    }

    /// <summary>Ao criar a rota (SpawnRoute1..4) os dois partem da mesma contagem de sorteios do gerador.</summary>
    [HarmonyPatch]
    internal static class RouteSpawnResetPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            for (int i = 1; i <= 4; i++)
            {
                var m = AccessTools.Method(typeof(RouteGeneratorC), "SpawnRoute" + i);
                if (m != null) yield return m;
            }
        }

        [HarmonyPriority(Priority.First)]
        private static void Prefix(RouteGeneratorC __instance, MethodBase __originalMethod)
        {
            try
            {
                int before = SeededRandom.ResetFor(__instance);
                Plugin.Log.LogInfo("Rota: " + __originalMethod.Name + " — sorteios do gerador antes = " + before + " (zerados para os dois partirem iguais); "
                    + "trechos já feitos " + __instance.routeAmmo + ", carros abandonados restantes " + __instance.abandonCarSpawnAmmo
                    + ", estrada " + __instance.currentRoadCondition + ", destino " + __instance.destinationNumber + ", país " + __instance.routeLevel + ".");
            }
            catch (Exception e) { Plugin.Log.LogError("SeededRandom: " + e); }
        }
    }
}
