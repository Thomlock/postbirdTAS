using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace PostbirdTAS
{
    /// <summary>
    /// Etat des entrees pour une frame donnee. Adapte les champs aux axes/boutons
    /// reellement utilises par le jeu (a verifier dans Assembly-CSharp.dll une
    /// fois decompilee, ou simplement en jouant la main et en regardant ce que
    /// InputPatches.cs enregistre).
    /// </summary>
    public struct InputFrame
    {
        public float Horizontal;   // UnityEngine.Input.GetAxis("Horizontal")
        public float Vertical;     // UnityEngine.Input.GetAxis("Vertical")
        public bool Jump;          // Input.GetButton("Jump") / espace
        public bool Interact;      // touche d'interaction (livraison, dialogue...)
        public bool Brake;         // frein du velo, si applicable

        public override string ToString() =>
            string.Join(",",
                Horizontal.ToString(CultureInfo.InvariantCulture),
                Vertical.ToString(CultureInfo.InvariantCulture),
                Jump ? "1" : "0",
                Interact ? "1" : "0",
                Brake ? "1" : "0");

        public static InputFrame Parse(string csvLine)
        {
            var parts = csvLine.Split(',');
            return new InputFrame
            {
                Horizontal = float.Parse(parts[0], CultureInfo.InvariantCulture),
                Vertical = float.Parse(parts[1], CultureInfo.InvariantCulture),
                Jump = parts[2] == "1",
                Interact = parts[3] == "1",
                Brake = parts[4] == "1",
            };
        }
    }

    /// <summary>
    /// Une "movie" TAS = une simple liste d'InputFrame, une par frame de jeu,
    /// stockee en texte pour pouvoir l'editer a la main (comme les fichiers .tas
    /// de CelesteTAS ou les .bk2 de BizHawk, en plus rudimentaire).
    /// </summary>
    public class Movie
    {
        public readonly List<InputFrame> Frames = new();

        public void Save(string path)
        {
            using var writer = new StreamWriter(path, false);
            writer.WriteLine("# PostbirdTAS movie - frame,horizontal,vertical,jump,interact,brake");
            foreach (var frame in Frames)
                writer.WriteLine(frame.ToString());
        }

        public static Movie Load(string path)
        {
            var movie = new Movie();
            foreach (var line in File.ReadAllLines(path))
            {
                if (line.Length == 0 || line.StartsWith("#")) continue;
                movie.Frames.Add(InputFrame.Parse(line));
            }
            return movie;
        }
    }
}
