using System.Runtime.InteropServices;

namespace PostbirdTAS
{
    /// <summary>
    /// On lit l'etat clavier directement via l'API Windows plutot que via
    /// UnityEngine.Input, car ce dernier est intercepte par nos patches Harmony
    /// pendant la lecture d'un movie (cf. InputPatches.cs). Sans ca, les touches
    /// de controle du mod seraient elles-memes "rejouees" au lieu d'etre lues en direct.
    /// </summary>
    internal static class NativeKeys
    {
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        // Codes de touches virtuelles Windows (VK_*) utilises par le mod.
        public const int VK_F1 = 0x70;
        public const int VK_F2 = 0x71;
        public const int VK_F3 = 0x72;     // deja existant mais inutilise: on l'utilise pour activer la saisie
        public const int VK_F4 = 0x73;
        public const int VK_F5 = 0x74;
        public const int VK_F7 = 0x76;
        public const int VK_F9 = 0x78;
        public const int VK_F10 = 0x79;
        public const int VK_RETURN = 0x0D;
        public const int VK_BACK = 0x08;
        public const int VK_0 = 0x30;
        public const int VK_LEFT = 0x25;
        public const int VK_UP = 0x26;
        public const int VK_RIGHT = 0x27;
        public const int VK_DOWN = 0x28;
        public const int VK_J = 0x4A;    // toggle Jump
        public const int VK_I = 0x49;    // toggle Interact
        public const int VK_B = 0x42;    // toggle Brake
        public const int VK_S = 0x53;    // sauvegarder les modifs sur disque

        private static readonly System.Collections.Generic.HashSet<int> previouslyDown = new();

        /// <summary>True uniquement sur la frame ou la touche vient d'etre enfoncee.</summary>
        public static bool WasPressedThisFrame(int vKey)
        {
            bool isDown = (GetAsyncKeyState(vKey) & 0x8000) != 0;
            bool wasDown = previouslyDown.Contains(vKey);

            if (isDown && !wasDown) previouslyDown.Add(vKey);
            else if (!isDown && wasDown) previouslyDown.Remove(vKey);

            return isDown && !wasDown;
        }

        public static bool IsDown(int vKey) => (GetAsyncKeyState(vKey) & 0x8000) != 0;
    }
}
