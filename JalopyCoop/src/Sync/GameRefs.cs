using UnityEngine;

namespace JalopyCoop.Sync
{
    /// <summary>
    /// Atalhos para os objetos do jogo que o co-op usa. Tudo pode ser null fora da cena de jogo.
    /// Nomes vêm do código descompilado (ver NOTES.md).
    /// </summary>
    public static class GameRefs
    {
        /// <summary>Objeto raiz do Laika (tem o Rigidbody do carro).</summary>
        public static Transform Car
        {
            get { return CarControleScriptC.Global != null ? CarControleScriptC.Global.transform : null; }
        }

        public static Rigidbody CarBody
        {
            get { var c = Car; return c != null ? c.GetComponent<Rigidbody>() : null; }
        }

        /// <summary>"First Person Controller" do jogador local.</summary>
        public static GameObject Player
        {
            get { return SafeFindTag("Player"); }
        }

        public static Camera Cam
        {
            get { return Camera.main; }
        }

        public static GameObject Uncle
        {
            get { return SafeFindTag("Uncle"); }
        }

        public static CarLogicC CarLogic
        {
            get { var go = SafeFindTag("CarLogic"); return go != null ? go.GetComponent<CarLogicC>() : null; }
        }

        /// <summary>Lugar onde o jogador fica sentado no banco do motorista.</summary>
        public static Transform DriverSeatLoc
        {
            get
            {
                var cl = CarLogic;
                if (cl == null || cl.playerSeat == null) return null;
                var seat = cl.playerSeat.GetComponent<SeatLogicC>();
                return seat != null ? seat.seatLoc : null;
            }
        }

        public static bool InGame
        {
            get { return Car != null && Player != null && Cam != null; }
        }

        private static GameObject SafeFindTag(string tag)
        {
            try { return GameObject.FindWithTag(tag); }
            catch { return null; }
        }

        /// <summary>Procura um objeto da cena (mesmo inativo) pelo nome de um Animator.</summary>
        public static Animator FindSceneAnimator(string name)
        {
            foreach (var a in Resources.FindObjectsOfTypeAll<Animator>())
            {
                if (a != null && a.name == name && a.gameObject.scene.name != null) return a;
            }
            return null;
        }

        public static Transform FindDeep(Transform root, string name)
        {
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                var r = FindDeep(root.GetChild(i), name);
                if (r != null) return r;
            }
            return null;
        }
    }
}
